using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace PhasmoSound;

public partial class App : System.Windows.Application
{
    Config cfg = new();
    readonly OverlayState state = new();
    AudioEngine? engine;            // game audio, only for the captions now
    GameFeed? feed;                 // sounds read from the game itself (SoundSpy mod)
    OverlayWindow? win;
    System.Windows.Forms.NotifyIcon? tray;
    readonly CancellationTokenSource cts = new();
    Speaker? speaker;
    GameHelper? helper;
    SayWindow? say;
    CaptionClient? captions;
    CaptionClient? ownCaptions;      // second stream: your own mic ("YOU: ...")
    MicListener? ownMic;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Log.Write("---- Other Side Sound starting ----");
        cfg = Config.Load(Path.Combine(AppContext.BaseDirectory, "config.json"));
        // The game keeps the GPU ~99% busy, and WPF's GPU drawing of this transparent full-screen window queued behind it (~3 s late).
        if (cfg.SoftwareRender) System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        Log.Write("render mode: " + (cfg.SoftwareRender ? "software (CPU)" : "GPU"));
        // Sounds come from the game itself (SoundSpy feed). The listening sound detector was removed 2026-10-16
        // (Kaalob: "remove the sound detector entirely"); game audio is still captured, only for the captions.
        feed = new GameFeed(cfg, state, () => win?.RedrawNow());
        if (cfg.WhisperCaptions)
        {
            try
            {
                var dev = AudioEngine.PickDevice(cfg.Device);
                engine = new AudioEngine(cfg, dev);
                engine.Start();
                Log.Write("caption audio: " + dev.FriendlyName);
            }
            catch (Exception ex) { Log.Write("caption audio failed: " + ex); }
        }
        if (cfg.WhisperCaptions && engine != null)
        {
            captions = new CaptionClient(cfg, state);
            captions.Start();
            engine.Samples16k += s => captions.Feed(s);
            if (cfg.OwnVoiceCaptions)
            {
                // your text-to-speech as it really arrives in the game mic, captioned as "YOU: ..."
                ownCaptions = new CaptionClient(cfg, state, launchServer: false, prefix: cfg.OwnVoiceLabel + ": ");
                ownCaptions.Start();
                ownMic = new MicListener(string.IsNullOrEmpty(cfg.OwnVoiceDevice) ? cfg.MicDevice : cfg.OwnVoiceDevice);
                ownMic.Samples16k += s => ownCaptions.Feed(s);
            }
        }
        Log.Write("creating window");
        win = new OverlayWindow(cfg, state);
        if (cfg.Speak)
        {
            if (cfg.SetDefaultMic) DefaultMic.Ensure(cfg.MicDevice, cfg.DefaultMicVolume, cfg.MicFormatRate, cfg.SpeakDevice, cfg.Device);
            if (cfg.HelperEnabled) helper = new GameHelper(cfg, state);
            speaker = new Speaker(cfg);
            speaker.StartKokoro();
            var phrases = new Phrases(Path.Combine(AppContext.BaseDirectory, "phrases"));
            speaker.Phrases = phrases;
            say = new SayWindow(cfg, speaker);
            speaker.Playing += on => { lock (state.Lock) state.SpeakPhase = on ? 2 : 0; win?.RedrawNow(); };
            say.IsVisibleChanged += (_, _) => win?.SetQuiet(say.IsVisible);
            win.BoardPage += page =>
            {
                if (page == 0) phrases.Reload();
                var lines = phrases.Items.Skip(page * 9).Take(9).Select(p => p.Text).ToList();
                int pages = Math.Max(1, (phrases.Items.Count + 8) / 9);
                lock (state.Lock)
                {
                    state.Board = lines; state.BoardPageNo = page; state.BoardPages = pages;
                    state.PageNames = Enumerable.Range(0, pages).Select(i => i < (cfg.PageNames?.Length ?? 0) ? cfg.PageNames![i] : "Page " + (i + 1)).ToList();
                }
            };
            win.PhraseChosen += (i, game) =>
            {
                if (i < 0 || i >= phrases.Items.Count) return;
                say.SetGame(game);
                _ = say.SpeakLine(phrases.Items[i].Text);
            };
            var wheel = new WheelWindow(cfg, phrases);
            IntPtr wheelGame = IntPtr.Zero;
            wheel.PhraseChosen += i =>
            {
                if (i < 0 || i >= phrases.Items.Count) return;
                say.SetGame(wheelGame);
                _ = say.SpeakLine(phrases.Items[i].Text);
            };
            win.WheelToggle += (l, t, r, b, game) => { wheelGame = game; wheel.Toggle(l, t, r, b, game); };
            say.Spoken += text =>
            {
                lock (state.Lock) { state.SpeakText = text; state.SpeakPhase = 1; }
            };
            win.SayRequested += (l, t, r, b, game) => say.ShowOver(l, t, r, b, game);
            win.TeachRequested += (l, t, r, b, game) => { if (helper != null) say.ShowOver(l, t, r, b, game, teachMode: true); };
            say.Taught += text => helper?.Learn(text);
            win.PresetRequested += (n, game) =>
            {
                say.SetGame(game);
                string line = n == 2 ? cfg.IntroLine2 : cfg.IntroLine;
                if (!string.IsNullOrWhiteSpace(line)) _ = say.SpeakLine(line);
            };
        }
        win.PrePosition();
        Log.Write("showing window");
        win.Show();
        Log.Write("window shown");
        SetupTray();
        Log.Write("tray ready");
        DispatcherUnhandledException += (_, a) => { Log.Write("UI exception: " + a.Exception); a.Handled = true; };
    }

    void SetupTray()
    {
        tray = new System.Windows.Forms.NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Information,
            Text = "Other Side Sound overlay  (F8 hide, Ctrl+F8 quit)",
            Visible = true,
        };
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Show / hide overlay (F8)", null, (_, _) => win?.ToggleOverlay());
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("Quit Other Side Sound (Ctrl+F8)", null, (_, _) => Shutdown());
        tray.ContextMenuStrip = menu;
        tray.DoubleClick += (_, _) => win?.ToggleOverlay();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        cts.Cancel();
        captions?.Dispose();
        ownMic?.Dispose();
        ownCaptions?.Dispose();
        helper?.Dispose();
        speaker?.StopKokoro();
        if (tray != null) { tray.Visible = false; tray.Dispose(); }
        feed?.Dispose();
        engine?.Dispose();
        Log.Write("---- Other Side Sound exit ----");
        base.OnExit(e);
    }
}
