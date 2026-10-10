using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace PhasmoSound;

public sealed class Config
{
    // "auto" = use "CABLE Input" (7.1 virtual cable) if present, else the default output device.
    public string Device { get; set; } = "FireTV";   // the device the GAME plays on (what the overlay listens to). "auto" = default output.
    // Follow the Phasmophobia window (overlay covers whichever monitor the game is on).
    public bool FollowGame { get; set; } = true;
    public string GameProcess { get; set; } = "Phasmophobia";
    // Make the game window borderless and stretch it over its monitor (fullscreen look, overlay still works). F9 re-applies.
    public bool BorderlessGame { get; set; } = true;
    // Whisper captions: the overlay streams the game audio to a local faster-whisper process and draws the captions itself.
    public bool WhisperCaptions { get; set; } = true;
    public string WhisperScript { get; set; } = @"D:\Tools\kokoro-tts\captions_server.py";
    public int WhisperPort { get; set; } = 8766;
    public string WhisperModel { get; set; } = "small.en";   // tiny.en / base.en / small.en / medium.en / large-v3
    public string WhisperDevice { get; set; } = "auto";      // auto = GPU if it works, else CPU
    public int CaptionLines { get; set; } = 4;
    public double CaptionFontSize { get; set; } = 17;
    public int CaptionLifetimeSec { get; set; } = 12;
    // Windows Live Captions (weak on game audio; off now that Whisper captions exist).
    public bool LiveCaptions { get; set; } = false;
    // Where to park the Live Captions bar: "bottom", "top", or "leave" (do not move it).
    public string LiveCaptionsPosition { get; set; } = "bottom";
    public int LiveCaptionsBottomMargin { get; set; } = 40;
    // "phasmophobia" = only label sounds that exist in Phasmophobia. "all" = every sound class.
    public string Profile { get; set; } = "phasmophobia";
    // "edges" = glow on the screen edges (top = front, bottom = back, sides = left/right). "ring" = circle in the middle.
    public string Style { get; set; } = "edges";
    public double EdgeThickness { get; set; } = 70;
    // Ring style only: radius as a fraction of the shorter screen edge.
    public double RingRadius { get; set; } = 0.17;
    public double RingThickness { get; set; } = 16;
    // Loudness range mapped to ring brightness (dBFS).
    public float MinDb { get; set; } = -42f;
    public float MaxDb { get; set; } = -10f;
    // Steady background (hum, buzz, outside drone) is learned and subtracted: the edges and the
    // classifier only react to sound that rises AmbientGateDb above the slowly-tracked floor.
    public bool AdaptiveFloor { get; set; } = true;
    public float AmbientRiseDbPerSec { get; set; } = 2f;   // how fast a constant sound gets absorbed into the floor
    public float AmbientGateDb { get; set; } = 6f;         // must be this far above the floor to count
    // A sudden jump of this many dB within ~20 ms counts as a "sudden sound".
    public float OnsetDb { get; set; } = 10f;
    public float LoudOnsetDb { get; set; } = 14f;
    // Sound classifier
    public float ClassifyThreshold { get; set; } = 0.30f;
    public int ClassifyIntervalMs { get; set; } = 250;  // background schedule
    public int FastClassifyDelayMs { get; set; } = 20;  // after a sudden sound: wait this long, then classify at once
    public int CaptureBufferMs { get; set; } = 10;      // audio capture buffer; smaller = less delay
    // Categories never shown (and, for custom/game, never used for matching):
    // move, door, object, ghost, voice, alert, env (teal), music, other,
    // custom (your own Ctrl+1..9 recordings), game (clips from the game's own files, gold)
    public string[] HideCategories { get; set; } = { "env", "custom" };
    public bool ShowVoice { get; set; } = true;
    public bool ShowMusic { get; set; } = false;
    // Hide your own sounds: centered footsteps while a movement key is held, and centered item/door
    // sounds right after you press an action key or mouse button.
    public bool HideOwnSounds { get; set; } = true;
    public int OwnMoveTailMs { get; set; } = 500;     // keep hiding footsteps this long after you stop
    public int OwnActionWindowMs { get; set; } = 700; // hide item/door sounds this long after a key press
    public float CenteredConf { get; set; } = 0.34f;  // below this left/right difference (about 30 degrees) a sound counts as "centered"
    // Footsteps heard further off-center than this (degrees) cannot be yours: a footprint icon is shown on that side.
    public bool StepIcon { get; set; } = true;
    public float OwnStepMaxDeg { get; set; } = 30f;    // while you are moving: steps inside this are yours
    public float StillStepMinDeg { get; set; } = 15f;  // while you stand still: steps beyond this get the icon
    public double StepIconSize { get; set; } = 64;
    // Step meter: times footsteps that are not yours and shows steps per second + SLOW / NORMAL / FAST at the top.
    // Every ghost uses the same footstep sounds, so the type shows in the SPEED, not the sound.
    public bool StepMeter { get; set; } = true;
    public float StepOnsetDb { get; set; } = 6f;               // a step = a jump of this many dB (lower than OnsetDb so quick steps all count)
    public float GhostNormalStepsPerSec { get; set; } = 2.3f;  // a normal-speed ghost (1.7 m/s); calibrate from the log ("STEPS" lines)
    public int HuntHoldSec { get; set; } = 25;                 // after "GHOST HUNTING" is heard, steps count as the ghost's for this long
    // Save every hunt by itself to samples\hunts\ (sound + direction/moving timeline) for training ghost steps. Ctrl+0 = save the last 60 s.
    public bool RecordHunts { get; set; } = true;
    // Ghost watch: during a hunt, watch the game picture for the ghost's blinking and time it (visible / hidden).
    // While it watches, the overlay is hidden from screen capture (OBS / Discord) so it does not see its own drawings.
    public bool GhostWatch { get; set; } = true;
    public bool SaveHuntFrames { get; set; } = true;        // samples\hunts\*_frames.gray320x180 (45 s max, ~2.6 MB/s) to check blinks by eye
    public float BlinkMinChange { get; set; } = 12f;        // brightness change (0-255) of a 10x10 patch that counts as a pop
    public float BlinkMaxBackground { get; set; } = 3f;     // ignore frames where the whole picture moves more than this (you turning)
    public float BlinkPhantomHiddenSec { get; set; } = 1.0f;
    public float BlinkOniHiddenSec { get; set; } = 0.3f;
    // Stopwatch: Ctrl+1 start, Ctrl+2 reset (smudge / hunt cooldown timing). Shown top-left. Takes over the
    // Ctrl+1 / Ctrl+2 training keys (Door open / Door close). Ctrl+3 = hunt over / hunt mode on (was Door slam); Ctrl+5 is off (hunts record themselves); Ctrl+4, Ctrl+6..9 still train.
    public bool Stopwatch { get; set; } = true;
    public string MoveKeys { get; set; } = "W A S D Up Down Left Right Shift";
    public string ActionKeys { get; set; } = "E F G Q R T J C LButton RButton MButton";
    // Training: Ctrl+1 .. Ctrl+9 save the last 2.5 s of audio as an example of TrainLabels[n-1].
    // F7 saves an unlabeled clip. Saved clips live in app\samples\ and are matched at runtime.
    public string[] TrainLabels { get; set; } =
    {
        "Door open", "Door close", "Door slam", "Knock", "Ghost footsteps", "Item drop", "Light switch", "Breathing", "Whisper",
    };
    public float CustomMatch { get; set; } = 0.82f;  // cosine similarity needed to accept a trained label (0-1)
    public float CustomMargin { get; set; } = 0.04f; // and it must beat the similarity to the room's background by this much
    public float GameMatch { get; set; } = 0.88f;    // stricter bar for the game's own (dry, studio) sound files in samples\game
    public bool UseTrainedHead { get; set; } = true;  // model\head.onnx, trained by PhasmoTrain on the game's sounds
    public float HeadMinProb { get; set; } = 0.6f;    // show its name only when it is at least this sure (0-1)
    // Quiet room noise got called "CRUCIFIX BURNED" / "GHOST ATTACK" at 0.6-0.8 (2026-10-08): these names, and any
    // sound quieter than HeadQuietDb, need HeadStrictProb instead.
    public string[] HeadStrictLabels { get; set; } = { "CRUCIFIX BURNED", "GHOST ATTACK", "GHOST HUNTING", "SCREAM", "BANSHEE SCREAM", "HEARTBEAT" };
    public float HeadStrictProb { get; set; } = 0.85f;
    public float HeadQuietDb { get; set; } = -32f;
    public float OwnSampleBonus { get; set; } = 0.05f; // clips you recorded in-game outrank game files by this much
    public int TrainClipMs { get; set; } = 2500;
    // Speak: F6 opens a text box; Enter speaks the line into the virtual mic the game uses as your microphone.
    public bool Speak { get; set; } = true;
    public string SpeakDevice { get; set; } = "CABLE Input";     // output device that feeds the virtual mic (VB-CABLE)
    public string MicDevice { get; set; } = "CABLE Output";      // the virtual mic the game should use (made Windows default at startup)
    public int MicFormatRate { get; set; } = 0;                  // 0 = leave the mic format alone (VB-CABLE is already 48 kHz); e.g. 48000 to force it
    // Voice engine order: Kokoro (local, natural) -> edge-tts (Microsoft online) -> Windows voice.
    public bool UseKokoro { get; set; } = true;
    public string KokoroPython { get; set; } = @"D:\Tools\kokoro-tts\.venv\Scripts\python.exe";
    public string KokoroScript { get; set; } = @"D:\Tools\kokoro-tts\kokoro_server.py";
    public int KokoroPort { get; set; } = 8765;
    public string KokoroVoice { get; set; } = "am_adam";   // am_adam, am_michael, am_eric, am_liam, am_onyx, bm_george, af_heart ...
    public float KokoroSpeed { get; set; } = 1.0f;
    public string SpeakVoice { get; set; } = "en-US-BrianMultilingualNeural";
    public string SpeakRate { get; set; } = "-8%";
    public float SpeakVolume { get; set; } = 0.4f;       // 0..1, how loud the voice goes into the mic (1.0 was too loud for others)
    public int SpeakLeadInMs { get; set; } = 350;         // noise-only lead-in so the game's voice gate opens before the first word
    public int SpeakTailMs { get; set; } = 400;
    public float SpeakNoiseFloor { get; set; } = 0.004f;  // faint room-noise bed under the voice (keeps the gate open between words)
    // F5 speaks this line (say it when you join a lobby). Ctrl+F5 speaks IntroLine2.
    public string IntroLine { get; set; } = "Hi everyone. I am deaf, so I talk through a computer voice and I read you with captions. Please just talk normally, I can follow you.";
    public string IntroLine2 { get; set; } = "Sorry, I did not catch that. Could you say it again a bit slower?";
    // Walkie (V) reaches the whole map with a radio filter; local voice reaches players near you and sounds natural.
    public bool SpeakOverWalkie { get; set; } = false;
    public string EdgeTtsPython { get; set; } = @"D:\Tools\type-to-speak\venv\Scripts\python.exe";
    public string PushToTalkKey { get; set; } = "";       // e.g. "V" to hold the game's radio key while speaking; empty = none
    // The Other Side: V = Proximity Voice (players near you), B = Radio Voice (whole team). Shift+Enter in the say box uses this key.
    public string RadioKey { get; set; } = "B";
    // Make the virtual mic the Windows default recording device at startup (the game's voice engine follows that, not its own dropdown).
    public bool SetDefaultMic { get; set; } = true;
    public int DefaultMicVolume { get; set; } = 70;
    public bool SayCloseOnFocusLoss { get; set; } = true;
    // Middle mouse click opens the phrase wheel; scroll wheel rotates the category; 1..9 says a line; middle click / Esc closes.
    public bool MouseWheelMenu { get; set; } = true;
    public int WheelHoldMs { get; set; } = 0;
    public bool WheelNeedsCtrl { get; set; } = false;
    // Kaalob 2026-10-10: the wheel opens with Alt + Down arrow instead of (Ctrl +) middle click.
    public bool WheelAltDown { get; set; } = true;
    public bool WheelMiddleClick { get; set; } = false;
    // Caption your own mic too (Kaalob 2026-10-10: "to know if the text to speech is clear enough").
    // OwnVoiceDevice empty = MicDevice. With Voicemeeter Banana it becomes "Voicemeeter Out B2".
    public bool OwnVoiceCaptions { get; set; } = false;
    public string OwnVoiceDevice { get; set; } = "";
    public string OwnVoiceLabel { get; set; } = "YOU";   // true: Ctrl + middle click opens the wheel, a plain middle click goes to the game
    // Ctrl+3 hunt over / hunt on, and the red "HUNT · Ctrl+3 = over" box. Off for The Other Side (Kaalob 2026-10-10);
    // hunts are still noticed and recorded automatically.
    public bool HuntKey { get; set; } = true;   // > 0: the middle button must be HELD this long; a quick click goes to the game
    public string[] PageNames { get; set; } = { "Quick", "Evidence", "Ghost did", "Where", "Doing", "Status", "Ghost type" };
    public int EventLifetimeSec { get; set; } = 10;
    public int MaxEvents { get; set; } = 8;
    // Sound groups (The Other Side, asked 2026-10-09): every shown sound takes its group's color and a color key sits under
    // the event list. LabelGroups (sound name -> group) wins over CategoryGroups (detector category -> group).
    // GroupColors is group -> "#RRGGBB", in legend order. All empty = the old per-category colors.
    public Dictionary<string, string> LabelGroups { get; set; } = new();
    public Dictionary<string, string> CategoryGroups { get; set; } = new();
    public Dictionary<string, string> GroupColors { get; set; } = new();
    public string[] GroupOutline { get; set; } = Array.Empty<string>();   // groups whose names get a black outline
    // See-through-ness (0-1): sound names (chips, event list, color key), per-group overrides, live captions.
    public double SoundOpacity { get; set; } = 1.0;
    public Dictionary<string, double> GroupOpacity { get; set; } = new();
    public double CaptionOpacity { get; set; } = 1.0;
    public string[] HideGroups { get; set; } = Array.Empty<string>();
    // Python that runs WhisperScript; empty = KokoroPython. The Other Side uses WhisperLive's own venv (D:\Tools\whisperlive).
    public string WhisperPython { get; set; } = "";   // groups never shown as sound rows (their color can still be used)
    // 0 = automatic (scales with screen height so a 4K TV and a 1080p monitor look the same). Or a fixed multiplier.
    public double UiScale { get; set; } = 0;

    public static Config Load(string path)
    {
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<Config>(File.ReadAllText(path), Json) ?? new Config();
        }
        catch (Exception ex) { Log.Write("config load failed: " + ex.Message); }
        var c = new Config();
        try { File.WriteAllText(path, JsonSerializer.Serialize(c, Json)); } catch { }
        return c;
    }

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
}

public static class Log
{
    static readonly object L = new();
    public static string Path = System.IO.Path.Combine(AppContext.BaseDirectory, "log.txt");
    public static void Write(string msg)
    {
        lock (L)
        {
            try { File.AppendAllText(Path, $"{DateTime.Now:HH:mm:ss.fff} {msg}{Environment.NewLine}"); } catch { }
        }
    }
}
