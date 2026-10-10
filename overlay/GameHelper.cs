using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace PhasmoSound;

/// Game helper (Kaalob 2026-10-10): every few seconds, if something new was heard, Claude reads the overlay's
/// recent sound rows and captions and writes ONE short note ("Footsteps getting closer on your left").
/// Text only goes out (sound names, captions), never audio. Runs on Kaalob's Claude subscription through a kept-open
/// headless Claude Code session with NO tools (it can only answer), so nothing is bought and it cannot act on the PC.
public sealed class GameHelper : IDisposable
{
    readonly Config cfg;
    readonly OverlayState state;
    Process? proc;
    TaskCompletionSource<string>? pending;
    int callsThisProc;
    readonly CancellationTokenSource cts = new();
    readonly List<string> lastNotes = new();
    DateTime lastSeenNewest = DateTime.MinValue;
    int calls;

    const string SystemPrompt =
        "You help a deaf player in the horror game The Other Side (a ghost-hunting co-op game like Phasmophobia). " +
        "An overlay listens to the game and writes what it hears: sound rows (a sound name, where it came from, how many seconds ago) " +
        "and captions (what players and ghosts said; \"YOU:\" is the player's own computer voice, \"GHOST (...)\" is a ghost line). " +
        "Sound names can be wrong, and \"(far)\" means quiet or far away. Your job is to say what the sounds MEAN for the player, the way a hearing friend would: " +
        "is something coming closer, is the ghost active, is a hunt starting, did a teammate ask for something. " +
        "Reply with ONE note of at most 12 simple words, no emoji, no quotes. Only say what the evidence supports, and say \"maybe\" when unsure. " +
        "Do not just repeat a single sound name; the player already sees those. Do not repeat your previous note unless something changed. " +
        "If there is nothing useful to add, reply with exactly: -";

    // helper\knowledge.md = game cheat sheet (shipped); helper\learned.txt = the player's corrections (Page Down), one per line
    static string HelperDir => Path.Combine(AppContext.BaseDirectory, "helper");
    static string LearnedPath => Path.Combine(HelperDir, "learned.txt");

    /// Base rules + game knowledge + everything the player taught. Rebuilt whenever a session starts.
    static string FullPrompt()
    {
        var sb = new StringBuilder(SystemPrompt);
        string know = Path.Combine(HelperDir, "knowledge.md");
        if (File.Exists(know)) sb.Append("\n\nGAME KNOWLEDGE:\n").Append(File.ReadAllText(know));
        if (File.Exists(LearnedPath))
        {
            var rules = File.ReadAllLines(LearnedPath).Where(l => l.Trim().Length > 0).ToList();
            if (rules.Count > 0)
                sb.Append("\n\nTHE PLAYER TAUGHT YOU THESE (they know the game and their own setup; these override everything above):\n- ")
                  .Append(string.Join("\n- ", rules));
        }
        return sb.ToString();
    }

    /// Page Down: the player corrects or teaches the helper. Saved for good, and the session restarts so it applies now.
    public void Learn(string text)
    {
        text = text.Trim();
        if (text.Length == 0) return;
        string last = lastNotes.Count > 0 ? lastNotes[^1] : "";
        string line = last.Length > 0 ? $"{text} (said after your note \"{last}\")" : text;
        Directory.CreateDirectory(HelperDir);
        File.AppendAllText(LearnedPath, line + Environment.NewLine);
        Log.Write("helper learned: " + line);
        callsThisProc = int.MaxValue;   // next question starts a fresh session with the new rule
        lock (state.Lock) { state.HelperNote = "Got it, I will remember that."; state.HelperUntil = DateTime.UtcNow.AddSeconds(4); }
    }

    public GameHelper(Config cfg, OverlayState state)
    {
        this.cfg = cfg; this.state = state;
        if (!File.Exists(cfg.HelperClaudePath))
        {
            Log.Write("helper: claude.exe not found at " + cfg.HelperClaudePath + ", game helper off");
            return;
        }
        Log.Write($"helper: on, model {cfg.HelperModel}, every {cfg.HelperIntervalSec}s when something new is heard");
        _ = Task.Run(Loop);
    }

    async Task Loop()
    {
        while (!cts.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(2, cfg.HelperIntervalSec)), cts.Token);
                string? snapshot = Snapshot();
                if (snapshot == null) continue;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                string note = await Ask(snapshot);
                if (note.Length == 0 || note == "-") { Log.Write($"helper: nothing to add ({sw.ElapsedMilliseconds} ms)"); continue; }
                Log.Write($"helper: \"{note}\" ({sw.ElapsedMilliseconds} ms, call {calls})");
                lastNotes.Add(note);
                if (lastNotes.Count > 4) lastNotes.RemoveAt(0);
                lock (state.Lock) { state.HelperNote = note; state.HelperUntil = DateTime.UtcNow.AddSeconds(cfg.HelperShowSec); }
            }
            catch (TaskCanceledException) { return; }
            catch (Exception ex) { Log.Write("helper: " + ex.GetType().Name + ": " + ex.Message.Split('\n')[0]); await Task.Delay(5000); }
        }
    }

    /// What the overlay heard in the last HelperWindowSec seconds, as plain text; null when nothing new since the last look.
    string? Snapshot()
    {
        var now = DateTime.UtcNow;
        double win = cfg.HelperWindowSec;
        List<EventItem> evs; List<CaptionLine> caps; bool hunt, surround;
        lock (state.Lock)
        {
            evs = state.Events.Where(e => !e.Pending && (now - e.Time).TotalSeconds <= win && !e.Label.StartsWith("You: ")).ToList();
            caps = state.Captions.Where(c => !c.Partial && (now - c.Time).TotalSeconds <= win).ToList();
            hunt = state.HuntActive; surround = state.Surround;
        }
        var newest = evs.Select(e => e.Time).Concat(caps.Select(c => c.Time)).DefaultIfEmpty(DateTime.MinValue).Max();
        if (newest <= lastSeenNewest) return null;
        lastSeenNewest = newest;

        var sb = new StringBuilder();
        sb.AppendLine($"Last {win:F0} seconds. Audio is {(surround ? "surround" : "stereo, so only left / right / ahead")}.{(hunt ? " A HUNT may be on." : "")}");
        sb.AppendLine("Sounds (newest first):");
        if (evs.Count == 0) sb.AppendLine("  none");
        foreach (var e in evs.Take(20)) sb.AppendLine($"  {(now - e.Time).TotalSeconds:F0}s ago: {e.Label} [{e.Category}] from {Where(e.Angle, surround)}, loud {e.Score:F2}");
        sb.AppendLine("Captions (oldest first):");
        if (caps.Count == 0) sb.AppendLine("  none");
        foreach (var c in caps.TakeLast(8)) sb.AppendLine($"  {(now - c.Time).TotalSeconds:F0}s ago: {c.Text}");
        if (lastNotes.Count > 0) sb.AppendLine("Your previous notes: " + string.Join(" | ", lastNotes));
        return sb.ToString();
    }

    static string Where(float a, bool surround)
    {
        if (!surround) return a < -20 ? "left" : a > 20 ? "right" : "ahead or both sides";
        int i = ((int)Math.Round(a / 45.0) % 8 + 8) % 8;
        return new[] { "front", "front-right", "right", "back-right", "behind", "back-left", "left", "front-left" }[i];
    }

    /// Start (or restart) the kept-open session. Restarted every HelperRestartCalls answers so its history stays small.
    void StartProc()
    {
        try { if (proc != null && !proc.HasExited) proc.Kill(true); } catch { }
        string dir = Path.Combine(AppContext.BaseDirectory, "helper");
        Directory.CreateDirectory(dir);   // empty folder: no CLAUDE.md or project files for the session to pick up
        var psi = new ProcessStartInfo(cfg.HelperClaudePath)
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = dir,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var a in new[] { "-p", "--input-format", "stream-json", "--output-format", "stream-json", "--verbose",
                                  "--model", cfg.HelperModel, "--effort", cfg.HelperEffort, "--tools", "", "--strict-mcp-config",
                                  "--disable-slash-commands", "--no-session-persistence", "--system-prompt", FullPrompt() })
            psi.ArgumentList.Add(a);
        proc = Process.Start(psi)!;
        ChildJob.Add(proc);
        callsThisProc = 0;
        proc.OutputDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            try
            {
                using var doc = JsonDocument.Parse(e.Data);
                var root = doc.RootElement;
                if (root.TryGetProperty("type", out var t) && t.GetString() == "result")
                    pending?.TrySetResult(root.TryGetProperty("result", out var r) ? r.GetString() ?? "" : "");
            }
            catch { }
        };
        proc.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) Log.Write("helper! " + e.Data); };
        proc.BeginOutputReadLine(); proc.BeginErrorReadLine();
        Log.Write("helper: session started (pid " + proc.Id + ")");
    }

    async Task<string> Ask(string snapshot)
    {
        if (proc == null || proc.HasExited || callsThisProc >= cfg.HelperRestartCalls) StartProc();
        pending = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        string line = JsonSerializer.Serialize(new { type = "user", message = new { role = "user", content = snapshot } });
        await proc!.StandardInput.WriteLineAsync(line);
        await proc.StandardInput.FlushAsync();
        var done = await Task.WhenAny(pending.Task, Task.Delay(TimeSpan.FromSeconds(25), cts.Token));
        if (done != pending.Task) { Log.Write("helper: no answer in 25 s, restarting session"); proc = null; return ""; }
        calls++; callsThisProc++;
        // notes are drawn as plain text: drop emoji and other symbols the overlay font cannot show
        string text = new string(pending.Task.Result.Where(ch => ch < 0x2000).ToArray()).Trim();
        text = string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return text.Length > 120 ? text[..120] : text;
    }

    public void Dispose()
    {
        cts.Cancel();
        try { if (proc != null && !proc.HasExited) proc.Kill(true); } catch { }
        if (calls > 0) Log.Write($"helper: {calls} notes asked this run");
    }
}
