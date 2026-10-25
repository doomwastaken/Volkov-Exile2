using System;
using System.Diagnostics;
using System.Drawing;
using System.Media;
using System.Threading;
using System.Windows.Forms;

// F7 craft lock. Each Shift+click on the craft slot that reaches the game starts a reroll; further clicks are
// swallowed until the new roll is on screen (the tooltip's mod text changed) and has been checked. A roll
// that matches the search highlight latches: clicks stay blocked until Shift is released. Releasing Shift
// over a match accepts it as unwanted, and the next click rerolls it.
// A frame around the slot shows the state: blue armed, red match, gold accepted match.
sealed class CraftLock : IDisposable
{
    const int WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202;
    const int IdleSleepMs = 100, ShiftSleepMs = 10, PendingSleepMs = 1;
    const int RollTimeoutMs = 1500;   // a click that changes nothing (e.g. no currency on cursor) stops waiting
    const int AutoOffMs = 5000;       // craft slot gone from screen this long: the lock turns itself off

    static readonly Color ArmedColor = Color.DeepSkyBlue;
    static readonly Color MatchColor = Color.Red;
    static readonly Color AcceptedColor = Color.Gold;

    readonly FrameForm frameForm = new FrameForm();
    readonly SoundPlayer matchSound;
    readonly Native.LowLevelMouseProc hookProc; // kept alive: the hook holds only a native pointer to it
    readonly object gate = new object();

    IntPtr hook;
    Thread worker;
    volatile bool running;

    // Shared between the worker and the hook (UI thread); guarded by gate.
    Rectangle slotOnScreen;
    bool gameActive;
    bool lit;              // latest frame shows the match highlight
    long fingerprint;      // latest frame's roll fingerprint
    bool latched;          // a match was seen; clicks blocked until Shift is released
    bool accepted;         // Shift was released over the current match; the next click may reroll it
    bool pending;          // a click went through; waiting for the new roll to appear
    long pendingFrom;      // fingerprint when that click went through
    long candidate;        // changed fingerprint seen once; confirmed when the next frame agrees
    long clickTicks;
    bool swallowUp;

    // Stats for the log.
    int blockedWhilePending, frames;
    double captureMsSum, captureMsMax;

    public bool Enabled { get; private set; }

    // Raised on the UI thread after the lock turned itself off.
    public event Action AutoDisabled;

    public CraftLock()
    {
        hookProc = OnMouse;
        // The divine orb drop sound, embedded (src/divine.wav, made from samples/divine_drop.mp3 at 1/3 volume).
        matchSound = new SoundPlayer(typeof(CraftLock).Assembly.GetManifestResourceStream("divine.wav"));
        matchSound.Load();
        GC.KeepAlive(frameForm.Handle); // created now, on the UI thread: the worker marshals frame updates through it
    }

    public void Toggle()
    {
        Enabled = !Enabled;
        if (Enabled)
        {
            lock (gate) { latched = accepted = pending = swallowUp = false; candidate = 0; }
            hook = Native.SetWindowsHookEx(14 /* WH_MOUSE_LL */, hookProc, Native.GetModuleHandle(null), 0);
            running = true;
            worker = new Thread(Run) { IsBackground = true, Name = "craft-lock", Priority = ThreadPriority.AboveNormal };
            worker.Start();
            Log.Write("craft lock ON");
        }
        else
        {
            running = false;
            if (hook != IntPtr.Zero) Native.UnhookWindowsHookEx(hook);
            hook = IntPtr.Zero;
            worker.Join(500);
            frameForm.Hide();
            Log.Write("craft lock OFF");
        }
    }

    static bool ShiftDown() { return (Native.GetAsyncKeyState(0x10 /* VK_SHIFT */) & 0x8000) != 0; }

    void Run()
    {
        using (var grab = new ScreenGrab())
        {
            Color shown = Color.Empty;
            var slotSeen = Stopwatch.StartNew();
            var statsTimer = Stopwatch.StartNew();
            var cpuStart = Process.GetCurrentProcess().TotalProcessorTime;
            while (running)
            {
                Rectangle game;
                if (!Native.TryGetForegroundGame(out game))
                {
                    lock (gate) { gameActive = false; pending = false; }
                    if (shown != Color.Empty) { UpdateFrame(Rectangle.Empty, Color.Empty); shown = Color.Empty; }
                    Thread.Sleep(200);
                    continue;
                }

                var watch = SlotDetector.WatchBounds(game.Height);
                var slot = SlotDetector.SlotBounds(game.Height);
                long t0 = Stopwatch.GetTimestamp();
                grab.Capture(game.X + watch.X, game.Y + watch.Y, watch.Width, watch.Height);
                double captureMs = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
                var slotInGrab = slot; slotInGrab.Offset(-watch.X, -watch.Y);
                bool nowLit = SlotDetector.Highlighted(grab.Pixels, grab.Width, grab.Height, slotInGrab, game.Height);
                bool present = nowLit || SlotDetector.Present(grab.Pixels, grab.Width, grab.Height, slotInGrab);
                long fp = SlotDetector.RollFingerprint(grab.Pixels, grab.Width, grab.Height);
                double frameMs = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
                bool shift = ShiftDown();

                // The currency tab is closed: no reason to keep watching (and hooking the mouse) until F7 again.
                if (present) slotSeen.Restart();
                else if (slotSeen.ElapsedMilliseconds >= AutoOffMs && !shift)
                {
                    Log.Write("craft slot not on screen for {0} s, turning craft lock off", AutoOffMs / 1000);
                    AutoOff();
                    break;
                }
                if (!present)
                {
                    lock (gate) { gameActive = false; }
                    if (shown != Color.Empty) { UpdateFrame(Rectangle.Empty, Color.Empty); shown = Color.Empty; }
                    Thread.Sleep(IdleSleepMs);
                    continue;
                }

                Color color;
                bool signal = false, waiting;
                lock (gate)
                {
                    gameActive = true;
                    slotOnScreen = slot; slotOnScreen.Offset(game.Location);
                    lit = nowLit; fingerprint = fp;
                    frames++; captureMsSum += frameMs; captureMsMax = Math.Max(captureMsMax, frameMs);

                    if (pending)
                    {
                        double waited = Elapsed(clickTicks);
                        if (fp != 0 && fp != pendingFrom)
                        {
                            // The tooltip may be mid-redraw; take the roll once two frames agree.
                            if (fp == candidate)
                            {
                                pending = false;
                                if (nowLit) { latched = true; signal = true; }
                                Log.Write("roll seen after {0:F0} ms, match={1}, blocked meanwhile={2}, frame {3:F1} ms",
                                    waited, nowLit, blockedWhilePending, frameMs);
                            }
                            else candidate = fp;
                        }
                        else if (waited > RollTimeoutMs)
                        {
                            pending = false;
                            Log.Write("no new roll within {0} ms, unblocking (blocked meanwhile={1})", RollTimeoutMs, blockedWhilePending);
                        }
                    }
                    else if (nowLit && !accepted && !latched && shift)
                    {
                        latched = true; signal = true;
                        Log.Write("match on screen, latched");
                    }

                    if (!shift)
                    {
                        if (latched || (nowLit && !accepted)) { accepted = nowLit; if (accepted) Log.Write("match accepted (Shift released)"); }
                        latched = false;
                    }
                    if (!nowLit && !pending) accepted = false;

                    color = accepted ? AcceptedColor : latched || nowLit ? MatchColor : ArmedColor;
                    waiting = pending;
                }

                if (signal) matchSound.Play();
                if (color != shown) { UpdateFrame(slotOnScreen, color); shown = color; }

                if (statsTimer.ElapsedMilliseconds >= 10000)
                {
                    var cpu = (Process.GetCurrentProcess().TotalProcessorTime - cpuStart).TotalMilliseconds;
                    lock (gate)
                    {
                        Log.Write("stats: {0} frames, avg {1:F2} ms, max {2:F2} ms, capture area {3}x{4}, CPU {5:F1}% of one core",
                            frames, frames == 0 ? 0 : captureMsSum / frames, captureMsMax, watch.Width, watch.Height,
                            cpu / statsTimer.Elapsed.TotalMilliseconds * 100);
                        frames = 0; captureMsSum = captureMsMax = 0;
                    }
                    statsTimer.Restart();
                    cpuStart = Process.GetCurrentProcess().TotalProcessorTime;
                }

                Thread.Sleep(waiting ? PendingSleepMs : shift ? ShiftSleepMs : IdleSleepMs);
            }
        }
    }

    // Toggle unhooks the mouse and joins this thread, so it has to run on the UI thread, after this loop exits.
    void AutoOff()
    {
        running = false;
        try
        {
            frameForm.BeginInvoke((Action)(() =>
            {
                if (!Enabled) return;
                Toggle();
                if (AutoDisabled != null) AutoDisabled();
            }));
        }
        catch (InvalidOperationException) { } // form already disposed on exit
    }

    static double Elapsed(long ticks) { return (Stopwatch.GetTimestamp() - ticks) * 1000.0 / Stopwatch.Frequency; }

    void UpdateFrame(Rectangle slot, Color color)
    {
        try
        {
            frameForm.BeginInvoke((Action)(() =>
            {
                if (color == Color.Empty || !Enabled) frameForm.Hide();
                else frameForm.ShowAround(slot, Math.Max(2, slot.Height / 70), color);
            }));
        }
        catch (InvalidOperationException) { } // form already disposed on exit
    }

    // Runs on the UI thread for every mouse event system-wide: decides from the worker's state, never captures.
    IntPtr OnMouse(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            int msg = wParam.ToInt32();
            if (msg == WM_LBUTTONUP && swallowUp) { swallowUp = false; return new IntPtr(1); }
            if (msg == WM_LBUTTONDOWN && ShiftDown() && Block()) { swallowUp = true; return new IntPtr(1); }
        }
        return Native.CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    bool Block()
    {
        lock (gate)
        {
            if (!gameActive || !slotOnScreen.Contains(Cursor.Position)) return false;
            if (latched) { Log.Write("click blocked: match"); return true; }
            if (pending) { blockedWhilePending++; return true; }
            if (lit && !accepted)
            {
                latched = true;
                Log.Write("click blocked: match");
                return true;
            }
            // This click rerolls; hold the next ones until its result is on screen.
            if (accepted) { accepted = false; Log.Write("rerolling accepted match"); }
            pending = true;
            pendingFrom = fingerprint;
            candidate = 0;
            clickTicks = Stopwatch.GetTimestamp();
            blockedWhilePending = 0;
            return false;
        }
    }

    public void Dispose()
    {
        if (Enabled) Toggle();
        frameForm.Dispose();
        matchSound.Dispose();
    }
}

// A click-through window shaped as a hollow rectangle around the slot.
sealed class FrameForm : OverlayForm
{
    Rectangle shownBounds;

    public void ShowAround(Rectangle slot, int thickness, Color color)
    {
        var outer = Rectangle.Inflate(slot, thickness + 1, thickness + 1);
        if (BackColor != color) BackColor = color;
        if (outer != shownBounds)
        {
            shownBounds = outer;
            Bounds = outer;
            var region = new Region(new Rectangle(Point.Empty, outer.Size));
            region.Exclude(new Rectangle(thickness, thickness, outer.Width - 2 * thickness, outer.Height - 2 * thickness));
            var old = Region;
            Region = region;
            if (old != null) old.Dispose();
        }
        if (!Visible) Native.ShowNoActivate(this);
    }
}
