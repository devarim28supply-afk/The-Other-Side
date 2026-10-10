using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace PhasmoSound;

public partial class OverlayWindow : Window
{
    readonly Config cfg;
    readonly OverlayState state;
    readonly RadarElement radar;
    IntPtr hwnd;
    public IntPtr Handle => hwnd;
    RECT lastRect;
    bool forceBorderless;
    bool borderlessApplied;   // the game is made borderless once per run; after that we never fight it
    public bool OverlayVisible { get; private set; } = true;
    /// Fired for training hotkeys: label index 0..8 for Ctrl+1..9, -1 for F7 (unlabeled).
    public event Action<int>? TrainRequested;
    /// Fired for Ctrl+0: save the last 60 s of game sound (after a hunt you had to run from).
    public event Action? SaveLastRequested;
    /// Ctrl+1 (false) = stopwatch start, Ctrl+2 (true) = reset. These replace the Door open / Door close training keys.
    public event Action<bool>? StopwatchKey;
    /// Ctrl+3: hunt over (stop recording / blink watch), or start hunt mode by hand when none is running.
    public event Action? HuntKey;
    /// Fired for F6: open the "say" box. Arguments: game rect (physical px) and the game window handle.
    public event Action<int, int, int, int, IntPtr>? SayRequested;
    /// Fired for Ctrl+F5: speak preset line 2. F5 toggles the phrase board; a digit fires PhraseChosen (1..9).
    public event Action<int, IntPtr>? PresetRequested;
    public event Action<int, IntPtr>? PhraseChosen;
    /// Raised when the board opens or flips to the next page; the handler fills state.Board for that page.
    public event Action<int>? BoardPage;
    bool boardOpen; int boardPage;
    MouseHook? mouse;

    /// Rotate the wheel to another category (wraps around).
    void StepPage(int delta)
    {
        int pages; lock (state.Lock) pages = Math.Max(1, state.BoardPages);
        boardPage = ((boardPage + delta) % pages + pages) % pages;
        BoardPage?.Invoke(boardPage);
    }

    /// Raised for middle click / F5: the app opens or closes the wheel window over the game.
    public event Action<int, int, int, int, IntPtr>? WheelToggle;

    void ToggleBoard(bool? open = null)
    {
        // the mouse-driven wheel window replaces the in-overlay board
        WheelToggle?.Invoke(lastRect.Left, lastRect.Top, lastRect.Right, lastRect.Bottom, lastGameHwnd);
        return;
#pragma warning disable CS0162
        if (open == null && boardOpen)
        {
            StepPage(1);
            return;
        }
        bool want = open ?? !boardOpen;
        if (want == boardOpen) return;
        boardOpen = want;
        if (boardOpen)
        {
            boardPage = 0;
            BoardPage?.Invoke(0);
            for (int i = 0; i < 9; i++) if (!RegisterHotKey(hwnd, 20 + i, 0, (uint)(0x31 + i))) Log.Write($"hotkey {i + 1} busy");
            RegisterHotKey(hwnd, 19, 0, VK_ESCAPE);
        }
        else
        {
            for (int i = 0; i < 9; i++) UnregisterHotKey(hwnd, 20 + i);
            UnregisterHotKey(hwnd, 19);
        }
        lock (state.Lock) state.BoardOpen = boardOpen;
    }
    IntPtr lastGameHwnd;

    public OverlayWindow(Config cfg, OverlayState state)
    {
        this.cfg = cfg;
        this.state = state;
        InitializeComponent();
        radar = new RadarElement(cfg, state);
        Root.Children.Add(radar);

        // redraw at ~60 Hz (a CompositionTarget.Rendering hook stalled window creation for 20+ s here)
        var draw = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
        draw.Tick += (_, _) => radar.InvalidateVisual();
        draw.Start();

        var place = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        place.Tick += (_, _) => PlaceOverGame();
        place.Start();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        hwnd = new WindowInteropHelper(this).Handle;
        long ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        ex |= WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(ex));
        // Never call SetLayeredWindowAttributes on a per-pixel-alpha (AllowsTransparency) window:
        // it switches the window to constant-alpha mode and WPF's UpdateLayeredWindow stops showing anything.
        if (!AllowsTransparency) SetLayeredWindowAttributes(hwnd, 0, 255, LWA_ALPHA);
        var src = HwndSource.FromHwnd(hwnd);
        if (src != null)
        {
            src.AddHook(WndProc);
            if (!AllowsTransparency)
            {
                // GPU-composited transparency via DWM. Not used: WPF strips WS_EX_LAYERED from such a
                // window, and without it WS_EX_TRANSPARENT does not pass mouse clicks to the game.
                src.CompositionTarget.BackgroundColor = System.Windows.Media.Colors.Transparent;
                var m = new MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
                int hr = DwmExtendFrameIntoClientArea(hwnd, ref m);
                Log.Write($"source init: dwm extend hr=0x{hr:X8}");
            }
        }
        Log.Write($"source init: styles {sw.ElapsedMilliseconds} ms");
        if (!RegisterHotKey(hwnd, 1, 0, VK_F8)) Log.Write("hotkey F8 not registered");
        if (!RegisterHotKey(hwnd, 2, MOD_CONTROL, VK_F8)) Log.Write("hotkey Ctrl+F8 not registered");
        if (!RegisterHotKey(hwnd, 9, MOD_CONTROL, 0x30)) Log.Write("hotkey Ctrl+0 not registered");
        if (!RegisterHotKey(hwnd, 3, 0, VK_F7)) Log.Write("hotkey F7 not registered");
        if (!RegisterHotKey(hwnd, 4, 0, VK_F9)) Log.Write("hotkey F9 not registered");
        if (!RegisterHotKey(hwnd, 5, 0, VK_F6)) Log.Write("hotkey F6 not registered");
        if (!RegisterHotKey(hwnd, 6, 0, VK_F10)) Log.Write("hotkey F10 not registered");
        if (!RegisterHotKey(hwnd, 7, 0, VK_F5)) Log.Write("hotkey F5 not registered");
        if (!RegisterHotKey(hwnd, 8, MOD_CONTROL, VK_F5)) Log.Write("hotkey Ctrl+F5 not registered");
        for (int i = 0; i < 9; i++)
            if (i != 4 && !RegisterHotKey(hwnd, 10 + i, MOD_CONTROL, (uint)(0x31 + i)))   // Ctrl+5 (Ghost footsteps) removed, hunts record themselves Log.Write($"hotkey Ctrl+{i + 1} not registered");
        Log.Write($"source init: hotkeys {sw.ElapsedMilliseconds} ms");
        PlaceOverGame();
        Log.Write($"source init: placed {sw.ElapsedMilliseconds} ms");
        if (cfg.MouseWheelMenu)
        {
            mouse = new MouseHook();
            mouse.MiddleClick += () => Dispatcher.BeginInvoke(new Action(() => ToggleBoard()));
            mouse.WheelActive = () => false;   // the wheel window handles scrolling itself while it has focus
        }
    }

    /// Before the window exists: put its (tiny) starting position on the game's monitor, so the window
    /// is born at that monitor's DPI and never has to be rescaled from the primary monitor's DPI.
    public void PrePosition()
    {
        try
        {
            foreach (var p in Process.GetProcessesByName(cfg.GameProcess))
            {
                var wh = p.MainWindowHandle;
                if (wh == IntPtr.Zero) continue;
                var mon = MonitorFromWindow(wh, MONITOR_DEFAULTTONEAREST);
                var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
                if (mon == IntPtr.Zero || !GetMonitorInfo(mon, ref mi)) continue;
                double scale = GetDpiForSystem() / 96.0;   // WPF converts Left/Top with the system DPI before the HWND exists
                Left = (mi.rcMonitor.Left + 8) / scale;
                Top = (mi.rcMonitor.Top + 8) / scale;
                Log.Write($"pre-positioned at {Left},{Top} DIPs (monitor {mi.rcMonitor.Left},{mi.rcMonitor.Top})");
                return;
            }
        }
        catch (Exception ex) { Log.Write("pre-position: " + ex.Message); }
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        EnsureClickThrough();
    }

    /// WPF strips WS_EX_LAYERED from a non-AllowsTransparency window when it shows it; without it,
    /// WS_EX_TRANSPARENT does not pass mouse input through. Re-apply whenever it is missing.
    void EnsureClickThrough()
    {
        if (hwnd == IntPtr.Zero) return;
        long ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        long want = ex | WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
        if (want != ex)
        {
            SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(want));
            if (!AllowsTransparency) SetLayeredWindowAttributes(hwnd, 0, 255, LWA_ALPHA);
            Log.Write($"click-through re-applied (exstyle was 0x{ex:X})");
        }
    }

    /// Keep Windows Live Captions running and parked over the game (bottom centre by default).
    DateTime lastCaptionsLaunch = DateTime.MinValue;
    RECT lastCaptionsTarget;
    void PlaceLiveCaptions(RECT game)
    {
        try
        {
            var procs = Process.GetProcessesByName("LiveCaptions");
            if (procs.Length == 0)
            {
                if ((DateTime.UtcNow - lastCaptionsLaunch).TotalSeconds < 15) return;
                lastCaptionsLaunch = DateTime.UtcNow;
                var exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "LiveCaptions.exe");
                if (File.Exists(exe)) { Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true }); Log.Write("live captions launched"); }
                return;
            }
            var ch = procs[0].MainWindowHandle;
            if (ch == IntPtr.Zero || !GetWindowRect(ch, out var cr)) return;
            string pos = (cfg.LiveCaptionsPosition ?? "bottom").ToLowerInvariant();
            if (pos == "leave") return;
            int w = cr.Right - cr.Left, h = cr.Bottom - cr.Top;
            int x = game.Left + (game.Right - game.Left - w) / 2;
            int y = pos == "top" ? game.Top + 60 : game.Bottom - h - cfg.LiveCaptionsBottomMargin;
            var target = new RECT { Left = x, Top = y, Right = x + w, Bottom = y + h };
            bool same = target.Left == lastCaptionsTarget.Left && target.Top == lastCaptionsTarget.Top && target.Right == lastCaptionsTarget.Right && target.Bottom == lastCaptionsTarget.Bottom;
            bool already = cr.Left == x && cr.Top == y;
            if (same && already) return;
            // only move it when it is not where we put it (the user may drag it elsewhere; we re-park once per change)
            if (!same || !already)
            {
                SetWindowPos(ch, HWND_TOPMOST, x, y, w, h, SWP_NOACTIVATE | SWP_NOSIZE);
                if (!same) Log.Write($"live captions parked at {x},{y}");
                lastCaptionsTarget = target;
            }
        }
        catch (Exception ex) { Log.Write("live captions: " + ex.Message); }
    }

    /// F10: release the game's cursor clip and put the mouse on another monitor (or the screen centre).
    public void FreeMouse()
    {
        ClipCursor(IntPtr.Zero);
        var g = lastRect;
        int cx = (g.Left + g.Right) / 2, cy = (g.Top + g.Bottom) / 2;
        // prefer a monitor that is not the game's
        int tx = cx, ty = cy;
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr mon, IntPtr hdc, ref RECT r, IntPtr data) =>
        {
            int mx = (r.Left + r.Right) / 2, my = (r.Top + r.Bottom) / 2;
            bool isGame = mx >= g.Left && mx < g.Right && my >= g.Top && my < g.Bottom;
            if (!isGame) { tx = mx; ty = my; return false; }
            return true;
        }, IntPtr.Zero);
        SetCursorPos(tx, ty);
        Log.Write($"mouse freed -> {tx},{ty}");
    }

    /// F9: switch the game between borderless full screen and a normal window (so the mouse can leave it).
    bool userWantsWindowed;
    void ToggleBorderless()
    {
        if (lastGameHwnd == IntPtr.Zero) return;
        if (!userWantsWindowed && borderlessDone == lastGameHwnd)
        {
            // back to a normal window at 75% of the monitor, centred
            var mon = MonitorFromWindow(lastGameHwnd, MONITOR_DEFAULTTONEAREST);
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            GetMonitorInfo(mon, ref mi);
            long st = GetWindowLongPtr(lastGameHwnd, GWL_STYLE).ToInt64() | WS_OVERLAPPEDWINDOW;
            SetWindowLongPtr(lastGameHwnd, GWL_STYLE, new IntPtr(st));
            int mw = mi.rcMonitor.Right - mi.rcMonitor.Left, mh = mi.rcMonitor.Bottom - mi.rcMonitor.Top;
            int w = mw * 3 / 4, h = mh * 3 / 4;
            SetWindowPos(lastGameHwnd, IntPtr.Zero, mi.rcMonitor.Left + (mw - w) / 2, mi.rcMonitor.Top + (mh - h) / 2, w, h, SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
            userWantsWindowed = true; borderlessDone = IntPtr.Zero;
            ClipCursor(IntPtr.Zero);
            Log.Write("game window restored to windowed (F9)");
        }
        else
        {
            userWantsWindowed = false; forceBorderless = true; borderlessApplied = false;
        }
        PlaceOverGame();
    }

    /// Turn the game's window into a borderless window covering its whole monitor (looks like
    /// fullscreen, but overlays still work; exclusive fullscreen would hide them).
    IntPtr borderlessDone = IntPtr.Zero;
    void MakeGameBorderless(IntPtr wh, RECT mon, bool force)
    {
        if (!force && wh == borderlessDone) return;
        long st = GetWindowLongPtr(wh, GWL_STYLE).ToInt64();
        bool hasFrame = (st & (WS_CAPTION | WS_THICKFRAME)) != 0;
        GetWindowRect(wh, out var cur);
        bool covers = cur.Left == mon.Left && cur.Top == mon.Top && cur.Right == mon.Right && cur.Bottom == mon.Bottom;
        if (!force && !hasFrame && covers) { borderlessDone = wh; return; }
        if (IsZoomed(wh)) ShowWindow(wh, SW_RESTORE);
        st &= ~(WS_CAPTION | WS_THICKFRAME | WS_MINIMIZEBOX | WS_MAXIMIZEBOX | WS_SYSMENU);
        SetWindowLongPtr(wh, GWL_STYLE, new IntPtr(st));
        long ex = GetWindowLongPtr(wh, GWL_EXSTYLE).ToInt64();
        ex &= ~(WS_EX_DLGMODALFRAME | WS_EX_CLIENTEDGE | WS_EX_STATICEDGE);
        SetWindowLongPtr(wh, GWL_EXSTYLE, new IntPtr(ex));
        SetWindowPos(wh, IntPtr.Zero, mon.Left, mon.Top, mon.Right - mon.Left, mon.Bottom - mon.Top, SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
        borderlessDone = wh;
        Log.Write($"game window made borderless at {mon.Left},{mon.Top} {mon.Right - mon.Left}x{mon.Bottom - mon.Top}");
    }

    IntPtr WndProc(IntPtr h, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY)
        {
            int id = wParam.ToInt32();
            if (id == 1) ToggleOverlay();
            else if (id == 2) System.Windows.Application.Current.Shutdown();
            else if (id == 3) TrainRequested?.Invoke(-1);
            else if (id == 9) SaveLastRequested?.Invoke();
            else if (id == 4) ToggleBorderless();
            else if (id == 6) FreeMouse();
            else if (id == 7) ToggleBoard();
            else if (id == 8) PresetRequested?.Invoke(2, lastGameHwnd);
            else if (id == 19) ToggleBoard(false);
            else if (id >= 20 && id < 29) { int idx = boardPage * 9 + (id - 20); ToggleBoard(false); PhraseChosen?.Invoke(idx, lastGameHwnd); }
            else if (id == 5) SayRequested?.Invoke(lastRect.Left, lastRect.Top, lastRect.Right, lastRect.Bottom, lastGameHwnd);
            else if (id == 10 && cfg.Stopwatch) StopwatchKey?.Invoke(false);
            else if (id == 11 && cfg.Stopwatch) StopwatchKey?.Invoke(true);
            else if (id == 12) HuntKey?.Invoke();
            else if (id >= 10 && id < 19) TrainRequested?.Invoke(id - 10);
            handled = true;
        }
        return IntPtr.Zero;
    }

    /// Redraw right now (called from the audio and classifier threads) instead of waiting for the timer.
    public void RedrawNow() => Dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(() => radar.InvalidateVisual()));

    public void ToggleOverlay()
    {
        OverlayVisible = !OverlayVisible;
        Root.Visibility = OverlayVisible ? Visibility.Visible : Visibility.Hidden;
    }

    /// Cover the game window's monitor area (borderless-window mode), else the primary screen.
    void PlaceOverGame()
    {
        if (hwnd == IntPtr.Zero) return;
        RECT r = default;
        bool got = false;
        var swp = System.Diagnostics.Stopwatch.StartNew();
        if (cfg.FollowGame)
        {
            try
            {
                var procs = Process.GetProcessesByName(cfg.GameProcess);
                long tFind = swp.ElapsedMilliseconds;
                foreach (var p in procs)
                {
                    var wh = p.MainWindowHandle;
                    if (wh != IntPtr.Zero && !IsIconic(wh) && GetWindowRect(wh, out r) && r.Right - r.Left > 300 && r.Bottom - r.Top > 200)
                    {
                        // clip to the monitor the game is on (a window can be bigger than its screen)
                        var mon = MonitorFromWindow(wh, MONITOR_DEFAULTTONEAREST);
                        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
                        if (mon != IntPtr.Zero && GetMonitorInfo(mon, ref mi))
                        {
                            if ((cfg.BorderlessGame && !userWantsWindowed && !borderlessApplied) || forceBorderless)
                            {
                                try { MakeGameBorderless(wh, mi.rcMonitor, forceBorderless); borderlessApplied = true; } catch (Exception ex) { Log.Write("borderless: " + ex.Message); }
                                forceBorderless = false;
                                GetWindowRect(wh, out r);
                            }
                            r.Left = Math.Max(r.Left, mi.rcMonitor.Left); r.Top = Math.Max(r.Top, mi.rcMonitor.Top);
                            r.Right = Math.Min(r.Right, mi.rcMonitor.Right); r.Bottom = Math.Min(r.Bottom, mi.rcMonitor.Bottom);
                        }
                        got = r.Right - r.Left > 300 && r.Bottom - r.Top > 200;
                        if (got) { lastGameHwnd = wh; break; }
                    }
                }
            }
            catch (Exception ex) { Log.Write("find game window: " + ex.Message); }
        }
        if (!got)
        {
            r = new RECT { Left = 0, Top = 0, Right = GetSystemMetrics(SM_CXSCREEN), Bottom = GetSystemMetrics(SM_CYSCREEN) };
        }
        bool changed = r.Left != lastRect.Left || r.Top != lastRect.Top || r.Right != lastRect.Right || r.Bottom != lastRect.Bottom;
        lastRect = r;
        // Always re-apply the exact rectangle: moving between monitors with different DPI makes
        // WPF rescale the window (WM_DPICHANGED), so one SetWindowPos is not always enough.
        for (int attempt = 0; attempt < 3; attempt++)
        {
            SetWindowPos(hwnd, HWND_TOPMOST, r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top, SWP_NOACTIVATE | SWP_SHOWWINDOW);
            if (GetWindowRect(hwnd, out var cur) && cur.Left == r.Left && cur.Top == r.Top && cur.Right == r.Right && cur.Bottom == r.Bottom) break;
        }
        EnsureClickThrough();
        if (cfg.LiveCaptions) PlaceLiveCaptions(r);
        if (changed)
        {
            GetWindowRect(hwnd, out var fin);
            Log.Write($"overlay placed at {r.Left},{r.Top} {r.Right - r.Left}x{r.Bottom - r.Top} (game window: {got}); actual {fin.Left},{fin.Top} {fin.Right - fin.Left}x{fin.Bottom - fin.Top}; took {swp.ElapsedMilliseconds} ms");
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        mouse?.Dispose();
        UnregisterHotKey(hwnd, 1);
        UnregisterHotKey(hwnd, 2);
        UnregisterHotKey(hwnd, 3);
        UnregisterHotKey(hwnd, 9);
        UnregisterHotKey(hwnd, 4);
        UnregisterHotKey(hwnd, 5);
        UnregisterHotKey(hwnd, 6);
        UnregisterHotKey(hwnd, 7);
        UnregisterHotKey(hwnd, 8);
        for (int i = 0; i < 9; i++) UnregisterHotKey(hwnd, 10 + i);
        base.OnClosed(e);
    }

    // ---- Win32 ----
    const int GWL_EXSTYLE = -20;
    const long WS_EX_TRANSPARENT = 0x20, WS_EX_LAYERED = 0x80000, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
    const int WM_HOTKEY = 0x0312;
    const uint MOD_CONTROL = 0x2, VK_F8 = 0x77, VK_F7 = 0x76, VK_F9 = 0x78, VK_F6 = 0x75, VK_F10 = 0x79, VK_F5 = 0x74, VK_ESCAPE = 0x1B;
    const long WS_OVERLAPPEDWINDOW = 0x00CF0000;
    delegate bool MonitorEnumProc(IntPtr mon, IntPtr hdc, ref RECT r, IntPtr data);
    [DllImport("user32.dll")] static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc cb, IntPtr data);
    [DllImport("user32.dll")] static extern bool ClipCursor(IntPtr rect);
    [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
    const int GWL_STYLE = -16, SW_RESTORE = 9;
    const long WS_CAPTION = 0x00C00000, WS_THICKFRAME = 0x00040000, WS_MINIMIZEBOX = 0x00020000, WS_MAXIMIZEBOX = 0x00010000, WS_SYSMENU = 0x00080000;
    const long WS_EX_DLGMODALFRAME = 0x1, WS_EX_CLIENTEDGE = 0x200, WS_EX_STATICEDGE = 0x20000;
    const uint SWP_NOZORDER = 0x4, SWP_FRAMECHANGED = 0x20;
    [DllImport("user32.dll")] static extern uint GetDpiForSystem();
    [DllImport("user32.dll")] static extern bool IsZoomed(IntPtr h);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
    const int SM_CXSCREEN = 0, SM_CYSCREEN = 1;
    const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10, SWP_SHOWWINDOW = 0x40;
    static readonly IntPtr HWND_TOPMOST = new(-1);

    const uint MONITOR_DEFAULTTONEAREST = 2;

    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }

    const uint LWA_ALPHA = 0x2;
    [StructLayout(LayoutKind.Sequential)]
    struct MARGINS { public int Left, Right, Top, Bottom; }
    [DllImport("dwmapi.dll")] static extern int DwmExtendFrameIntoClientArea(IntPtr h, ref MARGINS m);
    [DllImport("user32.dll")] static extern bool SetLayeredWindowAttributes(IntPtr h, uint key, byte alpha, uint flags);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")] static extern bool GetMonitorInfo(IntPtr mon, ref MONITORINFO mi);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern IntPtr GetWindowLongPtr(IntPtr h, int i);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] static extern IntPtr SetWindowLongPtr(IntPtr h, int i, IntPtr v);
    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr h, int id, uint mod, uint vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr h, int id);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int i);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
}
