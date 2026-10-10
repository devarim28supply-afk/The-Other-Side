using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PhasmoSound;

/// Global low-level mouse hook: middle click and wheel for the phrase wheel. Events we handle are swallowed
/// so the game does not see them. Must be created on a thread with a message loop (the WPF UI thread).
public sealed class MouseHook : IDisposable
{
    public event Action? MiddleClick;
    /// +1 = wheel up, -1 = wheel down. Return true from WheelActive to swallow wheel events.
    public event Action<int>? Wheel;
    public Func<bool>? WheelActive;

    readonly LowLevelMouseProc proc;
    IntPtr hook;

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

    public void Dispose()
    {
        if (hook != IntPtr.Zero) { UnhookWindowsHookEx(hook); hook = IntPtr.Zero; }
    }

    delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);
    const int WH_MOUSE_LL = 14, WM_MBUTTONDOWN = 0x0207, WM_MBUTTONUP = 0x0208, WM_MOUSEWHEEL = 0x020A;
    [DllImport("user32.dll")] static extern IntPtr SetWindowsHookEx(int id, LowLevelMouseProc proc, IntPtr mod, uint thread);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string? name);
}
