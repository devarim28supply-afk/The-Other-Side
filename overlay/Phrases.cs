using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PhasmoSound;

/// The phrase board (F5 then 1..9) and the audio cache. Pre-made audio for a phrase (e.g. from
/// ElevenLabs) lives in app\phrases\<file>; anything else synthesized is cached in app\phrases\cache.
public sealed class Phrases
{
    public sealed class Phrase { public string Text { get; set; } = ""; public string? File { get; set; } }

    public readonly string Dir;
    public List<Phrase> Items { get; private set; } = new();

    public Phrases(string dir)
    {
        Dir = dir;
        Directory.CreateDirectory(dir);
        Directory.CreateDirectory(Path.Combine(dir, "cache"));
        Reload(first: true);
    }

    static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    /// Re-read phrases.json (the editor page saves it while the overlay runs).
    public void Reload(bool first = false)
    {
        string path = Path.Combine(Dir, "phrases.json");
        try
        {
            if (File.Exists(path)) Items = JsonSerializer.Deserialize<List<Phrase>>(File.ReadAllText(path), JsonOpts)?.Where(p => !string.IsNullOrWhiteSpace(p.Text)).ToList() ?? new();
        }
        catch (Exception ex) { Log.Write("phrases.json: " + ex.Message); }
        if (!first) return;
        if (Items.Count == 0)
        {
            Items = new()
            {
                new() { Text = "Hi everyone. I am deaf, so I talk through a computer voice and I read you with captions. Please just talk normally." },
                new() { Text = "Sorry, can you say that again, a bit slower?" },
                new() { Text = "Yes." },
                new() { Text = "No." },
                new() { Text = "I am coming." },
                new() { Text = "It is hunting. Hide!" },
                new() { Text = "The ghost is in this room." },
                new() { Text = "I found the ghost room. Come here." },
                new() { Text = "I am going back to the truck." },
            };
            try { File.WriteAllText(path, JsonSerializer.Serialize(Items, new JsonSerializerOptions { WriteIndented = true })); } catch { }
        }
        Log.Write($"phrases: {Items.Count} loaded, {Items.Count(p => PremadeFile(p) != null)} with pre-made audio");
    }

    /// Pre-made audio for this phrase, if a file exists (phrases.json "File", or <n>.wav/.mp3, or the text's hash).
    public string? PremadeFile(Phrase p)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrEmpty(p.File)) candidates.Add(Path.Combine(Dir, p.File));
        candidates.Add(Path.Combine(Dir, Key(p.Text) + ".mp3")); candidates.Add(Path.Combine(Dir, Key(p.Text) + ".wav"));
        return candidates.FirstOrDefault(File.Exists);
    }

    /// Any audio we already have for this exact text: a pre-made phrase file, or a cached synthesis.
    public string? Lookup(string text, string engine, string voice)
    {
        var p = Items.FirstOrDefault(i => string.Equals(Norm(i.Text), Norm(text), StringComparison.Ordinal));
        if (p != null) { var f = PremadeFile(p); if (f != null) return f; }
        string c = CachePath(text, engine, voice);
        return File.Exists(c) ? c : null;
    }

    public string CachePath(string text, string engine, string voice) =>
        Path.Combine(Dir, "cache", Key(text + "|" + engine + "|" + voice) + ".wav");

    static string Norm(string s) => string.Join(' ', s.ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).TrimEnd('.', '!', '?');
    public static string Key(string s) => Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(Norm(s))))[..16].ToLowerInvariant();
}
