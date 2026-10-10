using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using NAudio.Wave;

namespace PhasmoSound;

/// Keeps the last 90 s of game sound (16 kHz mono) plus a timeline of direction, loudness and whether
/// you were moving. When a hunt is heard it saves the whole hunt by itself (from 10 s before it was
/// heard until it has been quiet for HuntHoldSec), so ghost steps get recorded without a key press.
/// Ctrl+0 saves the last 60 s by hand. Files go to samples\hunts\ (not loaded as samples).
public sealed class HuntRecorder
{
    const int Rate = AudioEngine.TargetRate, Seconds = 90;
    readonly float[] ring = new float[Rate * Seconds];
    long written;                              // total samples ever written
    DateTime lastWrite = DateTime.UtcNow;
    readonly object lk = new();
    readonly List<Mark> marks = new();
    DateTime lastMark = DateTime.MinValue;
    DateTime? huntStart;
    readonly string dir;

    public sealed class Mark { public double T { get; set; } public float Dir { get; set; } public float Conf { get; set; } public float Db { get; set; } public bool Moving { get; set; } public bool Onset { get; set; } }

    public HuntRecorder(string dir) { this.dir = dir; Directory.CreateDirectory(dir); }

    public void Feed(float[] s)
    {
        lock (lk)
        {
            foreach (var v in s) ring[written++ % ring.Length] = v;
            lastWrite = DateTime.UtcNow;
        }
    }

    /// Audio thread, every frame. Keeps one mark per 50 ms (always keeps onsets).
    public void Frame(DateTime t, float dir, float conf, float db, bool moving, bool onset)
    {
        lock (lk)
        {
            if (!onset && (t - lastMark).TotalMilliseconds < 50) return;
            lastMark = t;
            marks.Add(new Mark { T = (t - DateTime.UnixEpoch).TotalSeconds, Dir = dir, Conf = conf, Db = db, Moving = moving, Onset = onset });
            double cut = (t - DateTime.UnixEpoch).TotalSeconds - Seconds;
            int drop = marks.FindIndex(m => m.T >= cut);
            if (drop > 0) marks.RemoveRange(0, drop);
        }
    }

    int sideSteps;   // off-centre footsteps heard during the current clip (counted by the app)

    /// The app heard a footstep from the side (not the centre, so not you and not the heartbeat).
    public void NoteSideStep() { if (huntStart != null) Interlocked.Increment(ref sideSteps); }

    /// Called by the classifier: hunting = heartbeat / hunt sound heard. Records 30 s from the moment
    /// it is first heard (2 s before, 28 s after), then keeps it only if footsteps were heard from the side.
    /// Returns a message when a recording was saved.
    bool manual;     // started with Ctrl+3: records until Ctrl+3 again (max 85 s), kept even without side steps

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.Synchronized)]
    public string? Update(DateTime now, bool hunting, bool manualHunt = false)
    {
        if (hunting && huntStart == null)
        {
            huntStart = now.AddSeconds(-2); sideSteps = 0; manual = manualHunt;
            Log.Write(manual ? "hunt recorder: Ctrl+3 hunt, recording until Ctrl+3" : "hunt recorder: heartbeat/hunt heard, recording 30 s");
        }
        if (huntStart != null && now >= huntStart.Value.AddSeconds(manual ? 85 : 30)) return End(now);
        return null;
    }

    /// Finish the current recording now (time ran out, or Ctrl+3 said the hunt is over).
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.Synchronized)]
    public string? End(DateTime now)
    {
        if (huntStart == null) return null;
        var from = huntStart.Value; huntStart = null;
        if (!manual && sideSteps < 3) { Log.Write($"hunt recorder: discarded, only {sideSteps} footsteps from the side"); return null; }
        return Save(from, now, $"hunt_{sideSteps}steps");
    }

    public string? SaveLast(int seconds) => Save(DateTime.UtcNow.AddSeconds(-seconds), DateTime.UtcNow, "manual");

    string? Save(DateTime from, DateTime to, string kind)
    {
        float[] wave; List<Mark> tl;
        lock (lk)
        {
            double secs = Math.Min(Seconds - 1, (lastWrite - from).TotalSeconds);
            int n = (int)Math.Min(written, (long)(secs * Rate));
            if (n < Rate) return null;
            wave = new float[n];
            long start = written - n;
            for (int i = 0; i < n; i++) wave[i] = ring[(start + i) % ring.Length];
            from = lastWrite.AddSeconds(-(double)n / Rate);
            double f0 = (from - DateTime.UnixEpoch).TotalSeconds;
            tl = marks.Where(m => m.T >= f0).Select(m => new Mark { T = Math.Round(m.T - f0, 3), Dir = m.Dir, Conf = m.Conf, Db = m.Db, Moving = m.Moving, Onset = m.Onset }).ToList();
        }
        string name = $"{DateTime.Now:yyyyMMdd_HHmmss}_{kind}";
        try
        {
            using (var w = new WaveFileWriter(Path.Combine(dir, name + ".wav"), new WaveFormat(Rate, 16, 1)))
                foreach (var v in wave) w.WriteSample(Math.Clamp(v, -1f, 1f));
            File.WriteAllText(Path.Combine(dir, name + ".json"), JsonSerializer.Serialize(tl));
            Log.Write($"hunt recorder: saved {name}.wav ({wave.Length / (double)Rate:F0} s, {tl.Count} marks)");
            return $"Saved {kind} recording ({wave.Length / Rate} s)";
        }
        catch (Exception ex) { Log.Write("hunt recorder: " + ex.Message); return null; }
    }
}
