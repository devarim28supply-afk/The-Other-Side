using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace PhasmoSound;

/// Labeled example clips recorded from the game, with their YAMNet embeddings.
/// New sounds are matched against them by cosine similarity (nearest neighbour).
public sealed class SampleStore
{
    public sealed class Sample
    {
        public string File { get; set; } = "";
        public string Label { get; set; } = "";
        public DateTime Time { get; set; }
        public float[] Embedding { get; set; } = Array.Empty<float>();
    }

    readonly string dir, indexPath;
    readonly List<Sample> samples = new();
    readonly object lk = new();

    public SampleStore(string dir)
    {
        this.dir = dir;
        Directory.CreateDirectory(dir);
        indexPath = Path.Combine(dir, "index.json");
        try
        {
            if (File.Exists(indexPath))
                samples = JsonSerializer.Deserialize<List<Sample>>(File.ReadAllText(indexPath)) ?? new();
        }
        catch (Exception ex) { Log.Write("samples index load failed: " + ex.Message); }
        Log.Write($"samples: {samples.Count} loaded ({string.Join(", ", samples.GroupBy(s => s.Label).Select(g => $"{g.Key}={g.Count()}"))})");
    }

    // Running reference of what "nothing happening" sounds like right now, learned from quiet windows.
    float[]? background;
    public void LearnBackground(float[] embedding)
    {
        var e = Normalize(embedding);
        lock (lk)
        {
            if (background == null || background.Length != e.Length) { background = e; return; }
            for (int i = 0; i < e.Length; i++) background[i] += 0.1f * (e[i] - background[i]);
            background = Normalize(background);
        }
    }
    public float BackgroundSimilarity(float[] embedding)
    {
        var e = Normalize(embedding);
        lock (lk)
        {
            if (background == null || background.Length != e.Length) return -1f;
            float dot = 0; for (int i = 0; i < e.Length; i++) dot += e[i] * background[i];
            return dot;
        }
    }

    public int Count { get { lock (lk) return samples.Count; } }
    public int CountOf(string label) { lock (lk) return samples.Count(s => s.Label == label); }

    public Sample Add(string label, float[] wave16k, float[] embedding)
    {
        var s = new Sample
        {
            File = $"{DateTime.Now:yyyyMMdd_HHmmss}_{Sanitize(label)}.wav",
            Label = label, Time = DateTime.Now, Embedding = Normalize(embedding),
        };
        WriteWav(Path.Combine(dir, s.File), wave16k);
        lock (lk)
        {
            samples.Add(s);
            File.WriteAllText(indexPath, JsonSerializer.Serialize(samples));
        }
        return s;
    }

    /// Best matching labeled sample. Unlabeled clips are ignored. Clips you recorded in the game
    /// ("own") get a ranking bonus over the game's dry studio files ("game/..."), because they
    /// sound like what the speakers actually play. Returns the raw similarity and whether the winner is a game file.
    public (string label, float sim, string file, bool isGame) Nearest(float[] embedding, float ownBonus, bool includeOwn = true, bool includeGame = true)
    {
        var e = Normalize(embedding);
        string best = ""; float bestSim = -1, bestScore = -10; string bestFile = ""; bool bestGame = false;
        lock (lk)
        {
            foreach (var s in samples)
            {
                if (string.IsNullOrEmpty(s.Label) || s.Label == "unlabeled" || s.Embedding.Length != e.Length) continue;
                bool game = s.File.StartsWith("game/", StringComparison.OrdinalIgnoreCase);
                if (game ? !includeGame : !includeOwn) continue;
                float dot = 0;
                for (int i = 0; i < e.Length; i++) dot += e[i] * s.Embedding[i];
                float score = dot + (game ? 0f : ownBonus);
                if (score > bestScore) { bestScore = score; bestSim = dot; best = s.Label; bestFile = s.File; bestGame = game; }
            }
        }
        return (best, bestSim, bestFile, bestGame);
    }

    static float[] Normalize(float[] v)
    {
        double n = 0; foreach (var x in v) n += x * x;
        float k = n > 0 ? (float)(1 / Math.Sqrt(n)) : 0;
        var r = new float[v.Length];
        for (int i = 0; i < v.Length; i++) r[i] = v[i] * k;
        return r;
    }

    static string Sanitize(string s) => new string(s.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());

    static void WriteWav(string path, float[] wave)
    {
        using var fs = File.Create(path);
        using var w = new BinaryWriter(fs);
        int n = wave.Length, rate = AudioEngine.TargetRate;
        w.Write("RIFF"u8); w.Write(36 + n * 2); w.Write("WAVE"u8); w.Write("fmt "u8);
        w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(n * 2);
        foreach (var v in wave) w.Write((short)Math.Clamp(v * 32767f, -32768f, 32767f));
    }
}
