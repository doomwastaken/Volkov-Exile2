using System;
using System.Diagnostics;
using System.Drawing;
using System.Media;
using System.Threading;
using System.Windows.Forms;

// F9 anti-landmine for merchant purchases (Ctrl+click). While Ctrl is held, the price in the hovered item's
// tooltip is read once the cursor stops. The first price read becomes the reference; its first Ctrl+click is
// blocked so the reference is seen before anything is bought. After that a Ctrl+click passes only when the
// hovered item's price is known and costs at most MaxRatio times the reference (currencies converted by the
// rates in VolkovExile.ini). Clicks before the price is read are dropped.
sealed class AntiLandmine : IDisposable
{
    const int WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202;
    const int SettleMs = 50;          // cursor still this long before reading: the tooltip is drawn
    const int AfterClickMs = 150;     // after a purchase the item under the cursor changes; read again after this
    const int MaxSavedMisses = 30;

    enum Verdict { None, Reference, Ok, Landmine, Unreadable }

    readonly PriceReader reader = new PriceReader();
    readonly OverlayForm form = new OverlayForm();
    readonly Label label;
    readonly Native.LowLevelMouseProc hookProc;
    readonly object gate = new object();

    Config config;
    IntPtr hook;
    Thread worker;
    volatile bool running;

    // Shared between the worker and the hook (UI thread); guarded by gate.
    Verdict verdict;
    Point verdictAt;          // cursor position the verdict belongs to
    long readAfter;           // timestamp before which the worker must not read (cursor settling, after a click)
    Price reference;
    bool swallowUp;
    int savedMisses;

    public bool Enabled { get; private set; }

    public AntiLandmine()
    {
        hookProc = OnMouse;
        label = new Label { AutoSize = true, Font = new Font("Segoe UI", 16f, FontStyle.Bold, GraphicsUnit.Pixel), BackColor = Color.Black, Padding = new Padding(4, 2, 4, 2) };
        form.Controls.Add(label);
        GC.KeepAlive(form.Handle);
    }

    public void Toggle()
    {
        Enabled = !Enabled;
        if (Enabled)
        {
            config = Config.Load();
            lock (gate) { verdict = Verdict.None; reference = null; swallowUp = false; }
            savedMisses = 0;
            hook = Native.SetWindowsHookEx(14 /* WH_MOUSE_LL */, hookProc, Native.GetModuleHandle(null), 0);
            running = true;
            worker = new Thread(Run) { IsBackground = true, Name = "anti-landmine" };
            worker.Start();
            Log.Write("anti-landmine ON");
        }
        else
        {
            running = false;
            if (hook != IntPtr.Zero) Native.UnhookWindowsHookEx(hook);
            hook = IntPtr.Zero;
            worker.Join(1000);
            form.Hide();
            Log.Write("anti-landmine OFF");
        }
    }

    static bool CtrlDown() { return (Native.GetAsyncKeyState(0x11 /* VK_CONTROL */) & 0x8000) != 0; }
    static long Now() { return Stopwatch.GetTimestamp(); }
    static long Ms(int ms) { return ms * Stopwatch.Frequency / 1000; }

    void Run()
    {
        using (var grab = new ScreenGrab())
        {
            Point last = Point.Empty;
            bool shown = false;
            while (running)
            {
                Rectangle game;
                if (!Native.TryGetForegroundGame(out game) || !CtrlDown())
                {
                    lock (gate) verdict = Verdict.None;
                    if (shown) { UiHide(); shown = false; }
                    Thread.Sleep(30);
                    continue;
                }

                var pos = Cursor.Position;
                bool due;
                lock (gate)
                {
                    if (pos != last) { verdict = Verdict.None; readAfter = Math.Max(readAfter, Now() + Ms(SettleMs)); }
                    due = verdict == Verdict.None && Now() >= readAfter;
                }
                last = pos;
                if (!due) { Thread.Sleep(5); continue; }

                long t0 = Now();
                grab.Capture(game.X, game.Y, game.Width, game.Height);
                var price = reader.Read(grab.Pixels, grab.Width, 0, 0, grab.Width, grab.Height, game.Height < 1440);
                double ms = (Now() - t0) * 1000.0 / Stopwatch.Frequency;
                if (Cursor.Position != pos) continue; // moved while reading: the result may belong to another item

                string text; Color color;
                lock (gate)
                {
                    verdict = Judge(price);
                    verdictAt = pos;
                    if (verdict == Verdict.Reference) reference = price;
                    text = Describe(price);
                    color = verdict == Verdict.Ok ? Color.LimeGreen : verdict == Verdict.Reference ? Color.Gold : verdict == Verdict.None ? Color.Gray : Color.Red;
                }
                Log.Write("price {0} -> {1} ({2:F0} ms)", price == null ? "none" : price.ToString(), verdict, ms);
                if (price == null && config.SaveMisses && savedMisses < MaxSavedMisses) SaveMiss(grab);
                Show(pos, text, color);
                shown = true;
            }
        }
    }

    // Called under gate.
    Verdict Judge(Price price)
    {
        if (price == null) return Verdict.Ok; // not a merchant item (e.g. Ctrl+click in the stash)
        double rate = config.RateOf(price.Currency);
        if (price.Amount <= 0 || rate <= 0) return Verdict.Unreadable;
        if (reference == null) return Verdict.Reference;
        double refValue = reference.Amount * config.RateOf(reference.Currency);
        return price.Amount * rate > refValue * config.MaxRatio ? Verdict.Landmine : Verdict.Ok;
    }

    // Called under gate. The second line always shows the reference so a wrong one is easy to spot.
    string Describe(Price price)
    {
        string p = price == null ? "no price" : price.ToString();
        string limit = reference == null ? "" : "\nreference " + reference + ", up to " + config.MaxRatio + "x";
        switch (verdict)
        {
            case Verdict.Reference: return "reference: " + p + "\nclick again to buy";
            case Verdict.Landmine: return "LANDMINE " + p + limit;
            case Verdict.Unreadable: return "BLOCKED " + p + (price.Amount <= 0 ? " (amount unreadable)" : " (currency not in ini)") + limit;
            default: return p + limit;
        }
    }

    void SaveMiss(ScreenGrab grab)
    {
        try
        {
            var dir = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "debug");
            System.IO.Directory.CreateDirectory(dir);
            using (var bmp = new Bitmap(grab.Width, grab.Height, System.Drawing.Imaging.PixelFormat.Format32bppRgb))
            {
                var d = bmp.LockBits(new Rectangle(0, 0, grab.Width, grab.Height), System.Drawing.Imaging.ImageLockMode.WriteOnly, bmp.PixelFormat);
                System.Runtime.InteropServices.Marshal.Copy(grab.Pixels, 0, d.Scan0, grab.Pixels.Length);
                bmp.UnlockBits(d);
                var file = System.IO.Path.Combine(dir, "miss_" + DateTime.Now.ToString("HHmmss_fff") + ".png");
                bmp.Save(file, System.Drawing.Imaging.ImageFormat.Png);
                Log.Write("saved {0}", file);
            }
            savedMisses++;
        }
        catch (Exception e) { Log.Write("saving miss failed: {0}", e.Message); }
    }

    void Show(Point cursor, string text, Color color)
    {
        Ui(() =>
        {
            label.Text = text;
            label.ForeColor = color;
            form.ClientSize = label.PreferredSize;
            form.Location = new Point(cursor.X + 24, cursor.Y + 24);
            if (!form.Visible) Native.ShowNoActivate(form);
        });
    }

    void UiHide() { Ui(() => { if (form.Visible) form.Hide(); }); }

    void Ui(Action a)
    {
        try { form.BeginInvoke(a); }
        catch (InvalidOperationException) { } // form already disposed on exit
    }

    // Runs on the UI thread for every mouse event system-wide: decides from the worker's state, never captures.
    IntPtr OnMouse(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            int msg = wParam.ToInt32();
            if (msg == WM_LBUTTONUP && swallowUp) { swallowUp = false; return new IntPtr(1); }
            if (msg == WM_LBUTTONDOWN && CtrlDown() && Block()) { swallowUp = true; return new IntPtr(1); }
        }
        return Native.CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    bool Block()
    {
        Rectangle game;
        if (!Native.TryGetForegroundGame(out game)) return false;
        lock (gate)
        {
            if (verdict == Verdict.None || verdictAt != Cursor.Position) return true; // price not read yet
            switch (verdict)
            {
                case Verdict.Reference:
                    verdict = Verdict.Ok;
                    Log.Write("click blocked: reference set to {0}", reference);
                    return true;
                case Verdict.Landmine:
                case Verdict.Unreadable:
                    Log.Write("click blocked: {0}", verdict);
                    SystemSounds.Exclamation.Play();
                    return true;
            }
            // Bought: the item under the cursor changes, read it again.
            verdict = Verdict.None;
            readAfter = Now() + Ms(AfterClickMs);
            return false;
        }
    }

    public void Dispose()
    {
        if (Enabled) Toggle();
        form.Dispose();
    }
}
