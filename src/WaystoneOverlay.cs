using System;
using System.Diagnostics;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

// F8 waystone overlay. A worker thread watches the screen and shows the mod count after "(Tier X)".
// The label colour is the status:
//   yellow "... mods" - waystone recognized, counting (two-line mods are still being read)
//   green  "6 mods"   - counted, and a second frame confirmed the same tooltip
//   red    "? mods"   - waystone header found, but its mod lines are not readable (covered or cut off)
sealed class WaystoneOverlay : IDisposable
{
    const int PollMs = 15;          // cursor check; costs nothing
    const int RecheckMs = 250;      // catches tooltip changes without cursor movement (e.g. after a currency click)
    const int MovingCheckMs = 100;  // while moving: is the shown tooltip still there?

    static readonly Color Counting = Color.Gold;
    static readonly Color Done = Color.Lime;
    static readonly Color Unreadable = Color.Red;

    readonly Bitmap template;
    readonly OverlayForm form = new OverlayForm();
    readonly Label label;
    Thread worker;
    volatile bool running;

    public bool Enabled { get; private set; }

    public WaystoneOverlay(Bitmap template)
    {
        this.template = template;
        label = new Label { AutoSize = true, Font = new Font("Segoe UI", 14f, FontStyle.Bold), ForeColor = Done, BackColor = Color.Black, Padding = new Padding(4, 0, 4, 0) };
        form.Controls.Add(label);
        GC.KeepAlive(form.Handle); // created now, on the UI thread: the worker marshals updates through it
    }

    public void Toggle()
    {
        Enabled = !Enabled;
        if (Enabled)
        {
            running = true;
            worker = new Thread(Run) { IsBackground = true, Name = "waystone" };
            worker.Start();
            Log.Write("waystone overlay ON");
        }
        else
        {
            running = false;
            worker.Join(1000);
            UiHide();
            Log.Write("waystone overlay OFF");
        }
    }

    // Per-thread state of the worker.
    Analyzer analyzer;
    ScreenGrab fullGrab, trackGrab;
    Rectangle tracked;          // client-relative area around the shown tooltip; empty when none
    string shownSignature;      // tooltip whose count is confirmed on screen
    bool shown;

    void Run()
    {
        // Created on this thread so OCR calls stay on it instead of marshalling to the UI thread.
        analyzer = new Analyzer(template);
        fullGrab = new ScreenGrab();
        trackGrab = new ScreenGrab();
        try
        {
            Point lastCursor = Cursor.Position;
            bool dirty = true;
            long lastScan = 0;
            while (running)
            {
                Thread.Sleep(PollMs);
                Rectangle game;
                if (!Native.TryGetForegroundGame(out game)) { Forget(); Thread.Sleep(100); continue; }
                // Alt shows the detailed tooltip (mod tiers and ranges), whose layout can't be counted: pause.
                if ((Native.GetAsyncKeyState(0x12 /* VK_MENU */) & 0x8000) != 0) { Forget(); dirty = true; continue; }

                var cursor = Cursor.Position;
                bool moving = cursor != lastCursor;
                lastCursor = cursor;
                long now = Stopwatch.GetTimestamp();
                if (moving)
                {
                    dirty = true;
                    if (shown && Ms(lastScan) >= MovingCheckMs)
                    {
                        lastScan = now;
                        if (ScanTracked(game, false) == null) Forget();
                    }
                    continue;
                }
                if (!dirty && Ms(lastScan) < RecheckMs) continue;
                dirty = false;
                lastScan = now;
                Detect(game);
            }
        }
        finally
        {
            fullGrab.Dispose(); trackGrab.Dispose();
            fullGrab = trackGrab = null;
        }
    }

    // One detection round: find the tooltip, show it as counting, count, confirm on a fresh frame.
    void Detect(Rectangle game)
    {
        long t0 = Stopwatch.GetTimestamp();
        var r = tracked.IsEmpty ? null : ScanTracked(game, false);
        if (r == null) r = ScanFull(game);
        double findMs = Ms(t0);
        if (r == null) { Forget(); return; }
        Track(game, r);

        if (r.Lines == 0)
        {
            Show(game, r, "? mods", Unreadable);
            shownSignature = null;
            return;
        }
        if (r.Signature == shownSignature) return; // nothing changed

        if (!r.Counted) Show(game, r, "... mods", Counting);
        // A fresh frame counts the two-line mods and confirms the tooltip was stable between the two frames.
        long t1 = Stopwatch.GetTimestamp();
        var c = ScanTracked(game, true);
        double countMs = Ms(t1);
        if (c == null) { Forget(); return; }
        if (c.Signature != r.Signature || !c.Counted)
        {
            Log.Write("waystone: tooltip changed while counting, retrying");
            return; // the next round runs right away: the tooltip changed, and it doesn't wait for RecheckMs
        }
        Show(game, c, c.Mods + " mods", Done);
        shownSignature = c.Signature;
        Log.Write("waystone: {0} mods (lines {1}, pairs {2}); found in {3:F0} ms, counted+confirmed in {4:F0} ms",
            c.Mods, c.Lines, c.Pairs, findMs, countMs);
    }

    Analyzer.Result ScanFull(Rectangle game)
    {
        fullGrab.Capture(game.X, game.Y, game.Width, game.Height);
        return analyzer.Analyze(fullGrab.Pixels, fullGrab.Width, fullGrab.Height, game.Height, false);
    }

    Analyzer.Result ScanTracked(Rectangle game, bool countPairs)
    {
        if (tracked.IsEmpty) return null;
        trackGrab.Capture(game.X + tracked.X, game.Y + tracked.Y, tracked.Width, tracked.Height);
        var r = analyzer.Analyze(trackGrab.Pixels, trackGrab.Width, trackGrab.Height, game.Height, countPairs);
        if (r == null) return null;
        r.HeaderRight += tracked.X; r.HeaderY += tracked.Y;
        r.Area.Offset(tracked.Location);
        return r;
    }

    // A fixed-size box around the tooltip, so the tracked capture buffer is reused across tooltips.
    void Track(Rectangle game, Analyzer.Result r)
    {
        double s = game.Height / 1440.0;
        int w = Math.Min(game.Width, (int)(1500 * s)), h = Math.Min(game.Height, (int)(1150 * s));
        int cx = r.Area.X + r.Area.Width / 2;
        int x = Math.Max(0, Math.Min(game.Width - w, cx - w / 2));
        int y = Math.Max(0, Math.Min(game.Height - h, r.HeaderY - r.HeaderH * 2));
        tracked = new Rectangle(x, y, w, h);
    }

    void Forget()
    {
        tracked = Rectangle.Empty;
        shownSignature = null;
        if (shown) { shown = false; UiHide(); }
    }

    void Show(Rectangle game, Analyzer.Result r, string text, Color color)
    {
        shown = true;
        var at = new Point(game.X + r.HeaderRight + r.HeaderH / 2, game.Y + r.HeaderY);
        int headerH = r.HeaderH;
        Ui(() =>
        {
            label.Text = text;
            label.ForeColor = color;
            SetFontFor(headerH);
            form.ClientSize = label.PreferredSize;
            form.Location = new Point(at.X, at.Y + (headerH - form.Height) / 2);
            if (!form.Visible) Native.ShowNoActivate(form);
        });
    }

    void UiHide() { Ui(() => { if (form.Visible) form.Hide(); }); }

    void Ui(Action a)
    {
        try { form.BeginInvoke(a); }
        catch (InvalidOperationException) { } // form already disposed on exit
    }

    void SetFontFor(int headerHeight)
    {
        float size = Math.Max(10f, headerHeight * 0.7f);
        var old = label.Font;
        if (old.Unit == GraphicsUnit.Pixel && Math.Abs(old.Size - size) < 0.5f) return;
        label.Font = new Font(old.FontFamily, size, FontStyle.Bold, GraphicsUnit.Pixel);
        old.Dispose();
    }

    static double Ms(long ticks) { return (Stopwatch.GetTimestamp() - ticks) * 1000.0 / Stopwatch.Frequency; }

    public void Dispose()
    {
        if (Enabled) Toggle();
        form.Dispose();
    }
}
