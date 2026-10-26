using System;
using System.Linq;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

// VolkovExile: PoE2 helper overlays. F8 shows the mod count of a hovered waystone; F7 is the craft lock.
// The waystone overlay only reads screen pixels and never sends input to the game.
static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        Native.SetProcessDPIAware();
        Bitmap template;
        using (var s = typeof(Program).Assembly.GetManifestResourceStream("ws_tpl.png")) template = new Bitmap(s);

        if (args.Length == 2 && args[0] == "--test") { Test(new Analyzer(template), args[1]); return; }
        if (args.Length == 2 && args[0] == "--slot") { TestSlot(args[1]); return; }
        if (args.Length >= 2 && args[0] == "--price") { TestPrice(args[1], args.Skip(2).Select(int.Parse).ToArray()); return; }

        bool created;
        using (new System.Threading.Mutex(true, "VolkovExile.SingleInstance", out created))
        {
            if (!created) return;
            Application.EnableVisualStyles();
            Application.Run(new OverlayApp(template));
        }
    }

    static int[] LoadPixels(Bitmap b)
    {
        var p = new int[b.Width * b.Height];
        var d = b.LockBits(new Rectangle(0, 0, b.Width, b.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        Marshal.Copy(d.Scan0, p, 0, p.Length);
        b.UnlockBits(d);
        return p;
    }

    static void TestSlot(string path)
    {
        using (var b = new Bitmap(path))
        {
            var p = LoadPixels(b);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            bool lit = SlotDetector.Highlighted(p, b.Width, b.Height, SlotDetector.SlotBounds(b.Height), b.Height);
            bool present = SlotDetector.Present(p, b.Width, b.Height, SlotDetector.SlotBounds(b.Height));
            var a = SlotDetector.WatchBounds(b.Height);
            var crop = new int[a.Width * a.Height];
            for (int y = 0; y < a.Height; y++) Array.Copy(p, (a.Y + y) * b.Width + a.X, crop, y * a.Width, a.Width);
            long fp = SlotDetector.RollFingerprint(crop, a.Width, a.Height);
            Console.WriteLine("{0}: present={1} highlighted={2} roll={3:X16} ({4:F3} ms)", path, present, lit, fp, sw.Elapsed.TotalMilliseconds);
        }
    }

    // --price file [x y w h]: OCR the whole image or the given area.
    static void TestPrice(string path, int[] area)
    {
        using (var b = new Bitmap(path))
        {
            var p = LoadPixels(b);
            var reader = new PriceReader();
            var r = area.Length == 4 ? new Rectangle(area[0], area[1], area[2], area[3]) : new Rectangle(0, 0, b.Width, b.Height);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var price = reader.Read(p, b.Width, r.X, r.Y, r.Width, r.Height, false);
            Console.WriteLine("{0}: {1} ({2} ms)", path, price == null ? "no price" : price.ToString(), sw.ElapsedMilliseconds);
            if (Environment.GetEnvironmentVariable("WM_DEBUG") != null) Console.WriteLine(reader.LastText);
        }
    }

    static void Test(Analyzer analyzer, string path)
    {
        using (var b = new Bitmap(path))
        {
            var p = LoadPixels(b);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            analyzer.Analyze(new int[p.Length], b.Width, b.Height, b.Height);
            long empty = sw.ElapsedMilliseconds; sw.Restart();
            var quick = analyzer.Analyze(p, b.Width, b.Height, b.Height, false);
            long find = sw.ElapsedMilliseconds; sw.Restart();
            var r = analyzer.Analyze(p, b.Width, b.Height, b.Height);
            long count = sw.ElapsedMilliseconds;
            if (r == null) { Console.WriteLine("{0}: no waystone ({1} ms)", path, find); return; }
            Console.WriteLine("{0}: {1} mods (lines {2}, pairs {3}) label at x={4} y={5}; empty {6} ms, find {7} ms (counted={8}), count {9} ms",
                path, r.Mods, r.Lines, r.Pairs, r.HeaderRight, r.HeaderY, empty, find, quick.Counted, count);
            if (Environment.GetEnvironmentVariable("WM_DEBUG") != null) Console.WriteLine(analyzer.LastOcrText);
        }
    }
}

sealed class OverlayApp : ApplicationContext
{
    const string AppName = "VolkovExile";
    const int WaystoneHotkeyId = 1, CraftHotkeyId = 2, LandmineHotkeyId = 4; // 3 is Shift+F7

    readonly HotkeyWindow hotkeys;
    readonly NotifyIcon tray;
    readonly WaystoneOverlay waystones;
    readonly CraftLock craftLock = new CraftLock();
    readonly AntiLandmine landmine = new AntiLandmine();

    public OverlayApp(Bitmap template)
    {
        waystones = new WaystoneOverlay(template);
        craftLock.AutoDisabled += UpdateTray;

        hotkeys = new HotkeyWindow(id =>
        {
            if (id == WaystoneHotkeyId) waystones.Toggle();
            else if (id == LandmineHotkeyId) landmine.Toggle();
            else craftLock.Toggle();
            UpdateTray();
        });
        if (!Native.RegisterHotKey(hotkeys.Handle, WaystoneHotkeyId, 0x4000 /* MOD_NOREPEAT */, (uint)Keys.F8))
            MessageBox.Show("F8 is already taken by another program.", AppName);
        // Shift is allowed so F7 still works while Shift is held for crafting.
        if (!Native.RegisterHotKey(hotkeys.Handle, CraftHotkeyId, 0x4000, (uint)Keys.F7)
            | !Native.RegisterHotKey(hotkeys.Handle, CraftHotkeyId + 1, 0x4000 | 0x0004 /* MOD_SHIFT */, (uint)Keys.F7))
            MessageBox.Show("F7 is already taken by another program.", AppName);
        // Ctrl is allowed so F9 still works while Ctrl is held for buying.
        if (!Native.RegisterHotKey(hotkeys.Handle, LandmineHotkeyId, 0x4000, (uint)Keys.F9)
            | !Native.RegisterHotKey(hotkeys.Handle, LandmineHotkeyId + 1, 0x4000 | 0x0002 /* MOD_CONTROL */, (uint)Keys.F9))
            MessageBox.Show("F9 is already taken by another program.", AppName);

        var menu = new ContextMenuStrip();
        menu.Items.Add("Exit", null, (s, e) => ExitThread());
        Icon icon;
        using (var s = typeof(Program).Assembly.GetManifestResourceStream("volkov.ico")) icon = new Icon(s, SystemInformation.SmallIconSize);
        tray = new NotifyIcon { Icon = icon, ContextMenuStrip = menu, Visible = true };
        UpdateTray();
    }

    void UpdateTray()
    {
        tray.Text = AppName + "\nMods (F8): " + (waystones.Enabled ? "ON" : "OFF") + "\nCraft lock (F7): " + (craftLock.Enabled ? "ON" : "OFF")
            + "\nAnti-landmine (F9): " + (landmine.Enabled ? "ON" : "OFF");
    }

    protected override void ExitThreadCore()
    {
        Native.UnregisterHotKey(hotkeys.Handle, WaystoneHotkeyId);
        Native.UnregisterHotKey(hotkeys.Handle, CraftHotkeyId);
        Native.UnregisterHotKey(hotkeys.Handle, CraftHotkeyId + 1);
        Native.UnregisterHotKey(hotkeys.Handle, LandmineHotkeyId);
        Native.UnregisterHotKey(hotkeys.Handle, LandmineHotkeyId + 1);
        waystones.Dispose();
        craftLock.Dispose();
        landmine.Dispose();
        tray.Visible = false; tray.Dispose();
        hotkeys.DestroyHandle();
        base.ExitThreadCore();
    }
}

class OverlayForm : Form
{
    public OverlayForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.Black;
    }

    protected override bool ShowWithoutActivation { get { return true; } }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            // Click-through, never focused, not in alt-tab.
            cp.ExStyle |= 0x80000 /* LAYERED */ | 0x20 /* TRANSPARENT */ | 0x80 /* TOOLWINDOW */ | 0x08000000 /* NOACTIVATE */ | 0x8 /* TOPMOST */;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Native.SetLayeredWindowAttributes(Handle, 0, 220, 2 /* LWA_ALPHA */);
        // Keep our own label out of the screenshots we analyze.
        Native.SetWindowDisplayAffinity(Handle, 0x11 /* WDA_EXCLUDEFROMCAPTURE */);
    }
}

sealed class HotkeyWindow : NativeWindow
{
    readonly Action<int> onHotkey;
    public HotkeyWindow(Action<int> onHotkey) { this.onHotkey = onHotkey; CreateHandle(new CreateParams()); }
    protected override void WndProc(ref Message m)
    {
        // Hotkeys registered twice (plain and with a modifier) share the action of the first id.
        if (m.Msg == 0x0312 /* WM_HOTKEY */) { int id = m.WParam.ToInt32(); onHotkey(id == 3 ? 2 : id == 5 ? 4 : id); }
        base.WndProc(ref m);
    }
}

static class Native
{
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);
    [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("user32.dll")] public static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint key, byte alpha, uint flags);
    [DllImport("user32.dll")] public static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, System.Text.StringBuilder s, int n);
    [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr h, ref Point p);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);

    public delegate IntPtr LowLevelMouseProc(int code, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] public static extern IntPtr SetWindowsHookEx(int id, LowLevelMouseProc proc, IntPtr module, uint thread);
    [DllImport("user32.dll")] public static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] public static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vk);
    [DllImport("kernel32.dll")] public static extern IntPtr GetModuleHandle(string name);

    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }

    public static bool TryGetForegroundGame(out Rectangle client)
    {
        client = Rectangle.Empty;
        var h = GetForegroundWindow();
        var sb = new System.Text.StringBuilder(64);
        GetWindowText(h, sb, sb.Capacity);
        if (sb.ToString() != "Path of Exile 2") return false;
        RECT r; if (!GetClientRect(h, out r) || r.Right < 100 || r.Bottom < 100) return false;
        var pt = new Point(0, 0); ClientToScreen(h, ref pt);
        client = new Rectangle(pt.X, pt.Y, r.Right, r.Bottom);
        return true;
    }

    public static void ShowNoActivate(Form f)
    {
        ShowWindow(f.Handle, 4 /* SW_SHOWNOACTIVATE */);
        SetWindowPos(f.Handle, new IntPtr(-1) /* HWND_TOPMOST */, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010 /* NOSIZE|NOMOVE|NOACTIVATE */);
    }
}
