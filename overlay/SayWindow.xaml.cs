using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace PhasmoSound;

/// Small text box over the game. Enter speaks the line into the virtual mic; Esc closes.
public partial class SayWindow : Window
{
    readonly Speaker speaker;
    readonly Config cfg;
    public event Action<string>? Spoken;
    IntPtr gameHwnd;

    public SayWindow(Config cfg, Speaker speaker)
    {
        this.cfg = cfg; this.speaker = speaker;
        InitializeComponent();
        speaker.Status += s => Dispatcher.BeginInvoke(new Action(() => Status.Text = s));
        Input.PreviewKeyDown += OnKey;
        Deactivated += (_, _) => { if (cfg.SayCloseOnFocusLoss) Hide(); };
    }

    /// Show centred near the bottom of the given (physical pixel) game rectangle and focus the box.
    public void ShowOver(int left, int top, int right, int bottom, IntPtr game)
    {
        gameHwnd = game;
        Status.Text = "";
        ClipCursor(IntPtr.Zero);   // the game confines the mouse to its window; release it while the box is open
        Show();
        var src = PresentationSource.FromVisual(this);
        double sx = src?.CompositionTarget?.TransformFromDevice.M11 ?? 1, sy = src?.CompositionTarget?.TransformFromDevice.M22 ?? 1;
        double w = Width;
        Left = (left + (right - left) / 2.0) * sx - w / 2;
        Top = (bottom - 260) * sy;
        Activate();
        Input.Focus();
        Input.SelectAll();
    }

    async void OnKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { e.Handled = true; Hide(); BackToGame(); return; }
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        string text = Input.Text.Trim();
        if (text.Length == 0) { Hide(); BackToGame(); return; }
        bool radio = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        Input.Clear();
        Hide();
        BackToGame();
        await SpeakLine(text, radio);
    }

    /// Speak a line into the game mic (used by Enter in the box, the wheel and the F5 preset lines).
    /// The game only sends the mic while its talk key is held, so the key is held for the whole line.
    public async Task SpeakLine(string text, bool radio = false)
    {
        Spoken?.Invoke(text);
        string key = radio ? cfg.RadioKey : (cfg.SpeakOverWalkie ? cfg.PushToTalkKey : "");
        bool hold = !string.IsNullOrEmpty(key);
        if (hold)
        {
            await Task.Delay(120);   // let the game take focus back first, or the key goes to the closed box
            HoldKey(key, true);
            Log.Write($"talk key {key} down");
        }
        try { await speaker.SayAsync(text); }
        finally { if (hold) { HoldKey(key, false); Log.Write($"talk key {key} up"); } }
    }

    public void SetGame(IntPtr game) => gameHwnd = game;

    void BackToGame()
    {
        if (gameHwnd != IntPtr.Zero) SetForegroundWindow(gameHwnd);
    }

    static void HoldKey(string key, bool down)
    {
        byte vk = KeyCode(key);
        if (vk == 0) return;
        byte sc = (byte)MapVirtualKey(vk, 0);   // games read the scan code too
        keybd_event(vk, sc, down ? 0u : 2u, UIntPtr.Zero);
    }

    [DllImport("user32.dll")] static extern uint MapVirtualKey(uint code, uint mapType);

    static byte KeyCode(string k)
    {
        k = k.Trim();
        if (k.Length == 1) return (byte)char.ToUpperInvariant(k[0]);
        return k.ToUpperInvariant() switch { "SPACE" => 0x20, "TAB" => 0x09, "SHIFT" => 0x10, "CTRL" => 0x11, "ALT" => 0x12, _ => 0 };
    }

    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool ClipCursor(IntPtr rect);
    [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte sc, uint flags, UIntPtr extra);
}
