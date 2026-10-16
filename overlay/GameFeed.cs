using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace PhasmoSound;

/// Reads the SoundSpy feed (UE4SS mod inside the game) and turns it into sound rows with exact positions.
/// The feed sends the KIND of source only (ghost / player / own / other), never ghost model or type names.
public sealed class GameFeed : IDisposable
{
    readonly Config cfg;
    readonly OverlayState state;
    readonly Action redraw;
    readonly Thread thread;
    volatile bool stop;
    readonly List<(Regex re, string label, string group)> rules = new();
    DateTime huntUntil = DateTime.MinValue;
    readonly Dictionary<string, (string label, string group)> nameCache = new();
    // ghost voice lines: sound file name -> who + exact sentence (captions/ghost_lines.json), and the spirit box
    // answer clips (captions/evidence_clips.txt). Kaalob 2026-10-16: show them as subtitles, not as sound rows.
    readonly Dictionary<string, (string speaker, string text)> ghostLines = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> evidenceClips = new(StringComparer.OrdinalIgnoreCase);
    string lastGhostLine = ""; DateTime lastGhostLineAt = DateTime.MinValue;

    // [regex on the game's sound name, label, group]; label "" = never shown (menu clicks, music, weather beds)
    static readonly string[][] Builtin =
    {
        new[] { @"^(SW_SubtleClick|SFX_ItemClick|SFX_itemClick|Lyra_UI|Mouse(Pressed|Released)|SFX_UI_|SFX_BassyButton|UI_Sound|UDS_|SFX_BackgroundNoise|SFX_Intro_Music|Intro_|Karl_Casey|VehicleDoor|Coins)", "", "" },
        new[] { @"Footstep_Ghost|Ghost_Footstep|GhostFootstep", "GHOST STEPS", "Ghost" },
        new[] { @"Heartbeat", "HEARTBEAT", "Ghost" },
        new[] { @"GhostWhisper|Whisper", "GHOST WHISPER", "Ghost" },
        new[] { @"BehindYou", "GHOST BEHIND YOU", "Ghost" },
        new[] { @"Laugh", "GHOST LAUGH", "Ghost" },
        new[] { @"Scream", "SCREAM", "Ghost" },
        new[] { @"Growl", "GROWL", "Ghost" },
        new[] { @"Breath", "BREATHING", "Ghost" },
        new[] { @"Crying|Cry_", "CRYING", "Ghost" },
        new[] { @"^Footsteps?_", "Footsteps", "Physical" },
        new[] { @"FloorCreak", "Floor creak", "Physical" },
        new[] { @"Door.*Slam|Slam", "DOOR SLAM", "Physical" },
        new[] { @"Door.*Creak", "Door creak", "Physical" },
        new[] { @"Door.*Open", "Door open", "Physical" },
        new[] { @"Door.*Close", "Door close", "Physical" },
        new[] { @"Door|Knock", "Door", "Physical" },
        new[] { @"Chair_Slide", "Chair moved", "Physical" },
        new[] { @"Glass", "Glass", "Physical" },
        new[] { @"Impact|Drop|Thud|Bang", "Bang / thrown", "Physical" },
        new[] { @"LightFlicker|Flicker", "Lights flicker", "Environment" },
        new[] { @"lightswitch|PowerSwitch|Breaker", "Light switch", "Device" },
        new[] { @"Television|TVStatic|AMRadio|Radio", "TV / radio", "Device" },
        new[] { @"SpiritBox", "Spirit box", "Device" },
        new[] { @"Geiger", "Geiger counter", "Device" },
        new[] { @"Sensor", "Sensor", "Device" },
        new[] { @"Flashlight", "Flashlight", "Device" },
        new[] { @"CameraShutter|Camera", "Camera", "Device" },
        new[] { @"phone", "Phone", "Device" },
        new[] { @"Stove|Microwave|Toilet|Shower|Fan", "Appliance", "Device" },
        new[] { @"Alarm", "ALARM", "Device" },
        new[] { @"RockingChair", "Rocking chair", "Object" },
        new[] { @"Piano", "PIANO", "Object" },
        new[] { @"Guitar", "GUITAR", "Object" },
        new[] { @"Clock", "Clock", "Object" },
        new[] { @"Lighter|Candle", "Candle / lighter", "Object" },
        new[] { @"Crow", "Crows", "Environment" },
    };

    public GameFeed(Config cfg, OverlayState state, Action redraw)
    {
        this.cfg = cfg; this.state = state; this.redraw = redraw;
        foreach (var r in (cfg.SoundNames ?? new()).Concat(Builtin))
            if (r.Length >= 3)
                try { rules.Add((new Regex(r[0], RegexOptions.IgnoreCase | RegexOptions.Compiled), r[1], r[2])); }
                catch (Exception ex) { Log.Write($"SoundNames rule \"{r[0]}\" bad: {ex.Message}"); }
        LoadGhostLines();
        thread = new Thread(Run) { IsBackground = true, Name = "gamefeed" };
        thread.Start();
    }

    void LoadGhostLines()
    {
        string dir = Path.GetDirectoryName(cfg.WhisperScript) ?? "";
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "ghost_lines.json")));
            foreach (var l in doc.RootElement.EnumerateArray())
            {
                string file = l.GetProperty("file").GetString() ?? "";
                ghostLines[Path.GetFileNameWithoutExtension(file)] = (l.GetProperty("speaker").GetString() ?? "Ghost", l.GetProperty("text").GetString() ?? "");
            }
        }
        catch (Exception ex) { Log.Write("ghost lines: " + ex.Message); }
        try
        {
            foreach (var p in File.ReadAllText(Path.Combine(dir, "evidence_clips.txt")).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                evidenceClips.Add(p[(p.LastIndexOf('/') + 1)..]);
        }
        catch (Exception ex) { Log.Write("evidence clips: " + ex.Message); }
        Log.Write($"game feed: {ghostLines.Count} ghost lines, {evidenceClips.Count} spirit box answer clips");
    }

    /// A ghost voice line or spirit box answer: shown as a pink subtitle with the exact sentence. Caller holds state.Lock.
    bool GhostLine(string raw)
    {
        string stem = Regex.Replace(raw, "_Cue$", "", RegexOptions.IgnoreCase);
        bool known = ghostLines.TryGetValue(stem, out var line);
        bool evidence = evidenceClips.Contains(stem);
        if (!known && !evidence) return false;
        var now = DateTime.UtcNow;
        if (stem == lastGhostLine && (now - lastGhostLineAt).TotalSeconds < 3) return true;   // same clip seen twice (hook + poll)
        lastGhostLine = stem; lastGhostLineAt = now;
        string who = known && !line.speaker.Equals("Ghost", StringComparison.OrdinalIgnoreCase) ? $"GHOST ({line.speaker})" : "GHOST";
        string text = known && line.text.Length > 0 ? line.text : "[" + Regex.Replace(stem, @"^(SFX_SpiritBox_|SFX_)|_\d+$", "").Replace('_', ' ') + "]";
        string cap = $"{who}: {text}" + (evidence ? "  = AUDIO EVIDENCE" : "");
        state.Captions.Add(new CaptionLine { Text = cap, Time = now, FromGame = true });
        if (state.Captions.Count > 60) state.Captions.RemoveAt(0);
        Log.Write("GHOST LINE " + cap + " raw=" + raw);
        return true;
    }

    /// Friendly label + group for a game sound name; label "" = hidden.
    (string label, string group) Name(string raw, string src)
    {
        string key = raw + "|" + src;
        if (nameCache.TryGetValue(key, out var hit)) return hit;
        (string, string) res = ("", "");
        bool matched = false;
        foreach (var (re, label, group) in rules)
            if (re.IsMatch(raw)) { res = (label, group); matched = true; break; }
        if (!matched)
        {
            // unknown: tidy the asset name ("SFX_Stove_Knob" -> "Stove knob")
            string s = Regex.Replace(raw, @"^(SFX_|SW_|MS_|SC_|SCue_|S_)|(_Cue|_SFX|_SC)$|_\d+$", "", RegexOptions.IgnoreCase).Replace('_', ' ').Trim();
            s = s.Length > 0 ? char.ToUpper(s[0]) + s[1..].ToLowerInvariant() : raw;
            res = (s, "Environment");
        }
        // anything the ghost itself makes is drawn in the ghost color
        if (src == "ghost" && res.Item1 != "") res = (res.Item1, "Ghost");
        // players' footsteps: say so, never ghost-colored
        if (src == "player" && res.Item1 == "Footsteps") res = ("Player steps", "Physical");
        return nameCache[key] = res;
    }

    void Run()
    {
        long pos = 0;
        var pending = new StringBuilder();
        var buf = new byte[65536];
        string path = cfg.GameFeedPath;
        Log.Write("game feed: " + path);
        bool warned = false, catchUp = true;
        while (!stop)
        {
            try
            {
                if (!File.Exists(path))
                {
                    if (!warned) { Log.Write("game feed: file not there yet (start the game with SoundSpy installed)"); warned = true; }
                    Thread.Sleep(1000);
                    continue;
                }
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (fs.Length < pos)
                {
                    // the game restarted and emptied the feed
                    pos = 0; pending.Clear(); catchUp = false;
                    lock (state.Lock) { state.Events.Clear(); state.HasListener = false; }
                    Log.Write("game feed: new game session");
                }
                if (fs.Length == pos) { fs.Dispose(); Tick(); Thread.Sleep(30); continue; }
                fs.Seek(pos, SeekOrigin.Begin);
                int n;
                while ((n = fs.Read(buf, 0, buf.Length)) > 0) { pending.Append(Encoding.UTF8.GetString(buf, 0, n)); pos += n; }
                string text = pending.ToString();
                int last = text.LastIndexOf('\n');
                if (last < 0) continue;
                pending.Clear().Append(text[(last + 1)..]);
                bool changed = false;
                lock (state.Lock)
                {
                    foreach (var line in text[..last].Split('\n'))
                    {
                        var f = line.TrimEnd('\r').Split('\t');
                        // overlay started mid-game: the old part of the feed only tells where you stand, its sounds are long over
                        if (catchUp && f[0] != "L") continue;
                        changed |= Handle(f);
                    }
                    state.FeedSeen = DateTime.UtcNow;
                }
                if (catchUp) Log.Write($"game feed: skipped {pos / 1024} KB of earlier sounds (overlay started mid-game)");
                catchUp = false;
                Tick();
                if (changed) redraw();
            }
            catch (Exception ex) { Log.Write("game feed: " + ex.Message); Thread.Sleep(500); }
        }
    }

    void Tick()
    {
        lock (state.Lock) state.HuntActive = DateTime.UtcNow < huntUntil;
    }

    static double D(string s) => double.Parse(s, CultureInfo.InvariantCulture);

    // caller holds state.Lock; true when a row appeared or changed
    bool Handle(string[] f)
    {
        if (f.Length == 0 || f[0].Length == 0) return false;
        var now = DateTime.UtcNow;
        switch (f[0])
        {
            case "L" when f.Length >= 5:
                state.LX = D(f[1]); state.LY = D(f[2]); state.LZ = D(f[3]); state.LYaw = D(f[4]); state.HasListener = true;
                return false;
            case "S" when f.Length >= 8:
            {
                string id = f[1], raw = f[2], src = f[7];
                double x = D(f[3]), y = D(f[4]), z = D(f[5]), range = D(f[6]);
                var (label, group) = Name(raw, src);
                if (label == "") return false;
                if (state.HasListener)
                {
                    double dx = x - state.LX, dy = y - state.LY, dz = z - state.LZ;
                    double dist = Math.Sqrt(dx * dx + dy * dy + dz * dz) / 100;
                    // fair play: only what a hearing player could hear from here
                    double hear = range > 0 ? range : cfg.DefaultHearM;
                    if (dist > hear) return false;
                }
                // ghost speech (also from the spirit box in your own hand) = subtitle, not a sound row
                if (GhostLine(raw)) return true;
                // ghost sounds are never "yours" even when the game plays them on you (heartbeat, "behind you")
                if (src == "own" && group != "Ghost" && !cfg.ShowOwnSounds) return false;
                if (state.HasListener)
                {
                    double dx = x - state.LX, dy = y - state.LY, dz = z - state.LZ;
                    // own sounds the feed could not tie to you (no owner): right on top of you and not the ghost's
                    if (!cfg.ShowOwnSounds && src != "ghost" && group != "Ghost" && Math.Sqrt(dx * dx + dy * dy) < 80 && Math.Abs(dz + cfg.EyeHeightM * 100) < 120) return false;
                }
                if (label is "GHOST STEPS" or "HEARTBEAT") huntUntil = now.AddSeconds(8);
                // repeats of the same sound close together (footsteps every 0.6 s) move one row instead of adding rows
                var same = state.Events.FirstOrDefault(e => e.Label == label && e.HasPos && (now - e.Time).TotalSeconds < 2.5
                                                         && Math.Abs(e.X - x) + Math.Abs(e.Y - y) < 600);
                if (same != null)
                {
                    same.MoveTo(x, y, z, now); same.Time = now; same.Count++;
                    if (id != "0") { same.Id = id; same.Playing = true; }
                    return true;
                }
                AddEvent(new EventItem { Time = now, Label = label, Category = group, HasPos = true, X = x, Y = y, Z = z, Id = id, Playing = id != "0" });
                Log.Write($"SOUND {label} [{group}] raw={raw} src={src} range={range:F0}m");
                return true;
            }
            case "M" when f.Length >= 5:
            {
                var e = state.Events.FirstOrDefault(e => e.Id == f[1]);
                if (e == null) return false;
                e.MoveTo(D(f[2]), D(f[3]), D(f[4]), now);
                if (e.Category == "Ghost") e.Time = now;   // a moving ghost sound stays on screen while it moves
                return true;
            }
            case "E" when f.Length >= 2:
                foreach (var e in state.Events.Where(e => e.Id == f[1])) e.Playing = false;
                return false;
            case "2" when f.Length >= 3:
            {
                var (label, group) = Name(f[1], f[2]);
                if (label == "") return false;
                if (GhostLine(f[1])) return true;
                if (f[2] == "own" && group != "Ghost" && !cfg.ShowOwnSounds) return false;
                var same = state.Events.FirstOrDefault(e => e.Label == label && !e.HasPos && (now - e.Time).TotalSeconds < 2.5);
                if (same != null) { same.Time = now; same.Count++; return true; }
                AddEvent(new EventItem { Time = now, Label = label, Category = group });
                Log.Write($"SOUND {label} [{group}] raw={f[1]} 2D");
                return true;
            }
        }
        return false;
    }

    // caller holds state.Lock
    void AddEvent(EventItem ev)
    {
        state.Events.Insert(0, ev);
        if (state.Events.Count > 40) state.Events.RemoveRange(40, state.Events.Count - 40);
    }

    public void Dispose() { stop = true; }
}
