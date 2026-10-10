using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PhasmoSound;

/// Global low-level mouse hook: middle click and wheel for the phrase wheel. Events we handle are swallowed
/// so the game does not see them. Must be created on a thread with a message loop (the WPF UI thread).
public sealed class MouseHook : IDisposable
{
    public event Action? MiddleClick;
    /// HoldMs > 0: only HOLDING the middle button this long fires MiddleHold; a quick click is passed on to the
    /// game (replayed, since the press had to be held back while we waited). Kaalob 2026-10-10: "hold wheel to open".
    public event Action? MiddleHold;
    public int HoldMs;
    public bool NeedCtrl;
    /// false = middle clicks always go to the game (wheel opens from a key instead)
    public bool HandleMiddle = true;
    /// Mouse thumb button that opens the say box: 1 = back (XButton1), 2 = forward (XButton2), 0 = none. Swallowed.
    public int ThumbButton;
    public event Action? ThumbClick;
    bool ctrlDown;
    /// +1 = wheel up, -1 = wheel down. Return true from WheelActive to swallow wheel events.
    public event Action<int>? Wheel;
    public Func<bool>? WheelActive;

    readonly LowLevelMouseProc proc;
    IntPtr hook;
    readonly object holdLock = new();
    bool downPending, holdFired;
    System.Threading.Timer? holdTimer;

    public MouseHook()
    {
        proc = Callback;
        using var mod = Process.GetCurrentProcess().MainModule!;
        hook = SetWindowsHookEx(WH_MOUSE_LL, proc, GetModuleHandle(mod.ModuleName), 0);
        Log.Write(hook == IntPtr.Zero ? "mouse hook failed" : "mouse hook installed");
    }

    IntPtr Callback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            int msg = wParam.ToInt32();
            if (msg == WM_XBUTTONDOWN || msg == WM_XBUTTONUP)
            {
                int which = (Marshal.ReadInt32(lParam, 8) >> 16) & 0xFFFF;   // mouseData high word: 1 or 2
                if (msg == WM_XBUTTONDOWN) Log.Write($"thumb button {which} pressed");
                if (which == ThumbButton)
                {
                    if (msg == WM_XBUTTONDOWN) ThumbClick?.Invoke();
                    return new IntPtr(1);
                }
                return CallNextHookEx(hook, code, wParam, lParam);
            }
            if (!HandleMiddle && (msg == WM_MBUTTONDOWN || msg == WM_MBUTTONUP)) return CallNextHookEx(hook, code, wParam, lParam);
            // our own replayed click: let it through to the game
            if ((msg == WM_MBUTTONDOWN || msg == WM_MBUTTONUP) && (Marshal.ReadInt32(lParam, 12) & LLMHF_INJECTED) != 0)
                return CallNextHookEx(hook, code, wParam, lParam);
            // NeedCtrl (Kaalob 2026-10-10): only Ctrl + middle click opens the wheel; a plain middle click is the game's
            if (NeedCtrl && (msg == WM_MBUTTONDOWN || msg == WM_MBUTTONUP))
            {
                bool ctrl = (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0;
                if (msg == WM_MBUTTONDOWN && ctrl) { ctrlDown = true; MiddleClick?.Invoke(); return new IntPtr(1); }
                if (msg == WM_MBUTTONUP && ctrlDown) { ctrlDown = false; return new IntPtr(1); }
                return CallNextHookEx(hook, code, wParam, lParam);
            }
            if (msg == WM_MBUTTONDOWN && HoldMs > 0)
            {
                lock (holdLock)
                {
                    downPending = true; holdFired = false;
                    holdTimer?.Dispose();
                    holdTimer = new System.Threading.Timer(_ =>
                    {
                        bool fire;
                        lock (holdLock) { fire = downPending && !holdFired; if (fire) holdFired = true; }
                        if (fire) MiddleHold?.Invoke();
                    }, null, HoldMs, System.Threading.Timeout.Infinite);
                }
                return new IntPtr(1);
            }
            if (msg == WM_MBUTTONUP && HoldMs > 0)
            {
                bool replay, ours;
                lock (holdLock) { ours = downPending; replay = downPending && !holdFired; downPending = false; holdTimer?.Dispose(); holdTimer = null; }
                if (replay) System.Threading.ThreadPool.QueueUserWorkItem(_ => ReplayMiddleClick());
                if (ours) return new IntPtr(1);
            }
            if (msg == WM_MBUTTONDOWN) { MiddleClick?.Invoke(); return new IntPtr(1); }
            if (msg == WM_MBUTTONUP && (WheelActive?.Invoke() ?? false)) return new IntPtr(1);
            if (msg == WM_MOUSEWHEEL && (WheelActive?.Invoke() ?? false))
            {
                var data = Marshal.ReadInt32(lParam, 8);          // mouseData: high word = wheel delta
                int delta = (short)((data >> 16) & 0xFFFF);
                Wheel?.Invoke(delta > 0 ? 1 : -1);
                return new IntPtr(1);
            }
        }
        return CallNextHookEx(hook, code, wParam, lParam);
    }

    /// A quick middle click that was held back: give it to the game after all.
    static void ReplayMiddleClick()
    {
        var inputs = new[]
        {
            new INPUT { type = INPUT_MOUSE, mi = new MOUSEINPUT { dwFlags = MOUSEEVENTF_MIDDLEDOWN } },
            new INPUT { type = INPUT_MOUSE, mi = new MOUSEINPUT { dwFlags = MOUSEEVENTF_MIDDLEUP } },
        };
        if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>()) != inputs.Length) Log.Write("middle click replay failed");
    }

    public void Dispose()
    {
        lock (holdLock) { holdTimer?.Dispose(); holdTimer = null; }
        if (hook != IntPtr.Zero) { UnhookWindowsHookEx(hook); hook = IntPtr.Zero; }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)]
    struct INPUT { public int type; public MOUSEINPUT mi; }
    const int INPUT_MOUSE = 0, LLMHF_INJECTED = 0x1;
    const uint MOUSEEVENTF_MIDDLEDOWN = 0x20, MOUSEEVENTF_MIDDLEUP = 0x40;
    [DllImport("user32.dll", SetLastError = true)] static extern uint SendInput(uint count, INPUT[] inputs, int size);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vKey);
    const int VK_CONTROL = 0x11;

    delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);
    const int WH_MOUSE_LL = 14, WM_MBUTTONDOWN = 0x0207, WM_MBUTTONUP = 0x0208, WM_MOUSEWHEEL = 0x020A, WM_XBUTTONDOWN = 0x020B, WM_XBUTTONUP = 0x020C;
    [DllImport("user32.dll")] static extern IntPtr SetWindowsHookEx(int id, LowLevelMouseProc proc, IntPtr mod, uint thread);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string? name);
}
