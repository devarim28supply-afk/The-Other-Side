using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace PhasmoSound;

public sealed record Detection(string Label, string Category, float Score, string RawName);

/// YAMNet (AudioSet, 521 classes) via ONNX Runtime. Input: mono 16 kHz waveform.
public sealed class Classifier : IDisposable
{
    readonly InferenceSession session;
    readonly string[] names;

    public Classifier(string modelPath, string csvPath)
    {
        var so = new SessionOptions { IntraOpNumThreads = 2, GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
        session = new InferenceSession(modelPath, so);
        names = LoadCsv(csvPath);
        Log.Write($"classifier loaded: {names.Length} classes; inputs={string.Join(",", session.InputMetadata.Keys)} outputs={string.Join(",", session.OutputMetadata.Keys)}");
        // warm up: the first inference for each input length is slow (allocations, kernel setup)
        var sw = System.Diagnostics.Stopwatch.StartNew();
        foreach (var n in new[] { 15600, 24000, 40000 }) Run(new float[n]);
        foreach (var n in new[] { 15600, 24000 }) Run(new float[n]);
        Log.Write($"classifier warm-up done in {sw.ElapsedMilliseconds} ms");
    }

    static string[] LoadCsv(string path)
    {
        var list = new List<string>();
        foreach (var line in File.ReadLines(path).Skip(1))
        {
            // index,mid,display_name  (display_name may be quoted and contain commas)
            int c1 = line.IndexOf(','); int c2 = line.IndexOf(',', c1 + 1);
            string name = line[(c2 + 1)..].Trim();
            if (name.StartsWith('"') && name.EndsWith('"')) name = name[1..^1].Replace("\"\"", "\"");
            list.Add(name);
        }
        return list.ToArray();
    }

    /// Runs the model. Returns per-class max score over all frames, and the mean 1024-d embedding.
    public (float[] scores, float[] embedding) Run(float[] wave)
    {
        var t = new DenseTensor<float>(wave, new[] { wave.Length });
        string inName = session.InputMetadata.Keys.First();
        var outs = session.OutputMetadata.Keys.ToList();
        using var res = session.Run(new[] { NamedOnnxValue.CreateFromTensor(inName, t) }, outs.Take(2).ToList());
        var scores = res.First().AsTensor<float>();
        int frames = scores.Dimensions[0], classes = scores.Dimensions[1];
        var max = new float[classes];
        for (int f = 0; f < frames; f++)
            for (int c = 0; c < classes; c++)
                if (scores[f, c] > max[c]) max[c] = scores[f, c];
        // Embedding of the loudest frame (the event itself), not the average of the whole clip,
        // so that trained examples describe the sound and not the room's background.
        var emb = res.Skip(1).First().AsTensor<float>();
        int ef = emb.Dimensions[0], dim = emb.Dimensions[1];
        const int win = 15600, hop = 7800;
        int bestF = 0; double bestRms = -1;
        for (int f = 0; f < ef; f++)
        {
            int s0 = Math.Min(f * hop, Math.Max(0, wave.Length - 1)), s1 = Math.Min(wave.Length, s0 + win);
            double acc = 0; for (int i = s0; i < s1; i++) acc += wave[i] * wave[i];
            double rms = s1 > s0 ? acc / (s1 - s0) : 0;
            if (rms > bestRms) { bestRms = rms; bestF = f; }
        }
        var e = new float[dim];
        for (int i = 0; i < dim; i++) e[i] = emb[bestF, i];
        return (max, e);
    }

    /// Hard knocks on wood all sound alike to the model; below this score they share one honest label.
    public const float SpecificScore = 0.5f;
    public static readonly HashSet<string> WoodFamily = new()
    {
        "Door", "Sliding door", "Wood", "Knock", "Tap", "Thud", "Bounce", "Crack", "SLAM", "Clatter", "Cupboard", "Drawer",
    };
    public const string WoodLabel = "Knock / thump";
    /// Sharp metallic or glassy clicks likewise: equipment clicks, keys, thrown kitchen items.
    public static readonly HashSet<string> ClinkFamily = new()
    {
        "Glass", "Clink", "Dishes", "Cutlery", "Keys", "Clock", "Tick", "Ping", "Click", "Jingle", "Rattle",
    };
    public const string ClinkLabel = "Clink / click";
    /// Rare but important: shown only when the model is at least this sure; below that they count as a plain sharp click.
    public static readonly Dictionary<string, float> StrictMin = new()
    {
        ["SHATTER"] = 0.60f, ["Breaking"] = 0.60f, ["Crack"] = 0.60f,
    };

    /// Maps raw class scores to Phasmophobia-friendly labels, merged and sorted by score.
    public List<Detection> Detect(float[] scores, float minScore, bool showVoice, bool showMusic, HashSet<string>? allow = null)
    {
        var best = new Dictionary<string, Detection>();
        for (int i = 0; i < scores.Length && i < names.Length; i++)
        {
            if (scores[i] < minScore) continue;
            string raw = names[i];
            if (Labels.Ignore.Contains(raw)) continue;
            string label, cat;
            if (Labels.Map.TryGetValue(raw, out var m)) { label = m.Label; cat = m.Category; }
            else { label = raw; cat = "other"; }
            if (cat == "voice" && !showVoice) continue;
            if (cat == "music" && !showMusic) continue;
            if (allow != null && !allow.Contains(label)) continue;
            if (StrictMin.TryGetValue(label, out var strict) && scores[i] < strict) { label = ClinkLabel; cat = "object"; }
            else if (WoodFamily.Contains(label) && scores[i] < SpecificScore) { label = WoodLabel; cat = "object"; }
            else if (ClinkFamily.Contains(label) && scores[i] < SpecificScore) { label = ClinkLabel; cat = "object"; }
            if (!best.TryGetValue(label, out var d) || d.Score < scores[i])
                best[label] = new Detection(label, cat, scores[i], raw);
        }
        return best.Values.OrderByDescending(d => d.Score).ToList();
    }

    public void Dispose() => session.Dispose();
}

public static class Labels
{
    public record Entry(string Label, string Category);

    static Entry E(string l, string c) => new(l, c);

    // Categories: move, door, object, ghost, voice, alert, env, music, other
    public static readonly Dictionary<string, Entry> Map = new()
    {
        // movement
        ["Footsteps"] = E("Footsteps", "move"),
        ["Walk, footsteps"] = E("Footsteps", "move"),
        ["Run"] = E("Running", "move"),
        ["Shuffle"] = E("Shuffling", "move"),
        ["Stomp, stamp"] = E("Stomping", "move"),
        ["Creak"] = E("Creak", "door"),
        ["Squeak"] = E("Squeak", "door"),
        // doors / furniture
        ["Door"] = E("Door", "door"),
        ["Doorbell"] = E("Doorbell", "alert"),
        ["Ding-dong"] = E("Doorbell", "alert"),
        ["Sliding door"] = E("Sliding door", "door"),
        ["Slam"] = E("SLAM", "door"),
        ["Knock"] = E("Knock", "door"),
        ["Tap"] = E("Tap", "door"),
        ["Cupboard open or close"] = E("Cupboard", "door"),
        ["Drawer open or close"] = E("Drawer", "door"),
        ["Wood"] = E("Wood", "object"),
        ["Crack"] = E("Crack", "object"),
        ["Crackle"] = E("Crackle", "object"),
        // objects / items
        ["Thump, thud"] = E("Thud", "object"),
        ["Bouncing"] = E("Bounce", "object"),
        ["Glass"] = E("Glass", "object"),
        ["Chink, clink"] = E("Clink", "object"),
        ["Shatter"] = E("SHATTER", "object"),
        ["Breaking"] = E("Breaking", "object"),
        ["Dishes, pots, and pans"] = E("Dishes", "object"),
        ["Cutlery, silverware"] = E("Cutlery", "object"),
        ["Coin (dropping)"] = E("Coin drop", "object"),
        ["Keys jangling"] = E("Keys", "object"),
        ["Scissors"] = E("Scissors", "object"),
        ["Zipper (clothing)"] = E("Zipper", "object"),
        ["Whoosh, swoosh, swish"] = E("Whoosh", "object"),
        ["Clicking"] = E("Click", "object"),
        ["Click"] = E("Click", "object"),
        ["Tick"] = E("Tick", "object"),
        ["Tick-tock"] = E("Clock", "object"),
        ["Clock"] = E("Clock", "object"),
        ["Rattle"] = E("Rattle", "object"),
        ["Rattle (instrument)"] = E("Rattle", "object"),
        ["Scrape"] = E("Scrape", "object"),
        ["Rub"] = E("Rub", "object"),
        ["Roll"] = E("Rolling", "object"),
        ["Jingle, tinkle"] = E("Jingle", "object"),
        ["Camera"] = E("Camera", "object"),
        ["Single-lens reflex camera"] = E("Camera", "object"),
        ["Writing"] = E("Writing", "object"),
        ["Typing"] = E("Typing", "object"),
        ["Paper"]= E("Paper", "object"),
        ["Crumpling, crinkling"] = E("Crinkle", "object"),
        ["Tearing"] = E("Tearing", "object"),
        ["Mechanisms"] = E("Mechanism", "object"),
        ["Ratchet, pawl"] = E("Ratchet", "object"),
        ["Gears"] = E("Gears", "object"),
        ["Pulleys"] = E("Pulley", "object"),
        ["Hammer"] = E("Hammer", "object"),
        ["Sawing"] = E("Sawing", "object"),
        ["Toilet flush"] = E("Toilet flush", "object"),
        ["Water tap, faucet"] = E("Faucet", "object"),
        ["Sink (filling or washing)"] = E("Sink", "object"),
        ["Bathtub (filling or washing)"] = E("Bathtub", "object"),
        ["Drip"] = E("Drip", "object"),
        ["Pour"] = E("Pouring", "object"),
        ["Water"] = E("Water", "env"),
        ["Splash, splatter"] = E("Splash", "object"),
        ["Gush"] = E("Gush", "object"),
        ["Fill (with liquid)"] = E("Filling", "object"),
        ["Boiling"] = E("Boiling", "object"),
        ["Frying (food)"] = E("Frying", "object"),
        ["Microwave oven"] = E("Microwave", "object"),
        ["Blender"] = E("Blender", "object"),
        ["Television"] = E("TV", "alert"),
        ["Radio"] = E("Radio", "alert"),
        ["Static"] = E("Static", "alert"),
        ["White noise"] = E("Static", "alert"),
        ["Pink noise"] = E("Static", "alert"),
        ["Hum"] = E("Hum", "env"),
        ["Mains hum"] = E("Electric hum", "env"),
        ["Buzz"] = E("Buzz", "env"),
        ["Electric shaver, electric razor"] = E("Buzzing", "env"),
        ["Telephone"] = E("PHONE", "alert"),
        ["Telephone bell ringing"] = E("PHONE RINGING", "alert"),
        ["Ringtone"] = E("Ringtone", "alert"),
        ["Telephone dialing, DTMF"] = E("Phone dialing", "alert"),
        ["Dial tone"] = E("Dial tone", "alert"),
        ["Busy signal"] = E("Busy signal", "alert"),
        ["Alarm"] = E("ALARM", "alert"),
        ["Alarm clock"] = E("Alarm clock", "alert"),
        ["Smoke detector, smoke alarm"] = E("SMOKE ALARM", "alert"),
        ["Fire alarm"] = E("FIRE ALARM", "alert"),
        ["Siren"] = E("Siren", "alert"),
        ["Civil defense siren"] = E("Siren", "alert"),
        ["Buzzer"] = E("Buzzer", "alert"),
        ["Beep, bleep"] = E("Beep", "alert"),
        ["Ping"] = E("Ping", "alert"),
        ["Bell"] = E("Bell", "alert"),
        ["Church bell"] = E("Church bell", "alert"),
        ["Chime"] = E("Chime", "alert"),
        ["Wind chime"] = E("Wind chime", "alert"),
        ["Music box"] = E("MUSIC BOX", "alert"),
        ["Heart sounds, heartbeat"] = E("HEARTBEAT", "alert"),
        ["Heart murmur"] = E("HEARTBEAT", "alert"),
        ["Explosion"] = E("Explosion", "alert"),
        ["Gunshot, gunfire"] = E("Bang", "alert"),
        ["Fireworks"] = E("Bang", "alert"),
        ["Burst, pop"] = E("Pop", "object"),
        ["Eruption"] = E("Boom", "alert"),
        ["Boom"] = E("BOOM", "alert"),
        ["Electricity"] = E("Electricity", "alert"),
        ["Electronic music"] = E("Music", "music"),
        // ghost / voices
        ["Breathing"] = E("Breathing", "ghost"),
        ["Wheeze"] = E("Wheeze", "ghost"),
        ["Snoring"] = E("Snoring", "ghost"),
        ["Gasp"] = E("Gasp", "ghost"),
        ["Pant"] = E("Panting", "ghost"),
        ["Snort"] = E("Snort", "ghost"),
        ["Cough"] = E("Cough", "ghost"),
        ["Throat clearing"] = E("Throat clearing", "ghost"),
        ["Sneeze"] = E("Sneeze", "ghost"),
        ["Sniff"] = E("Sniff", "ghost"),
        ["Whispering"] = E("WHISPER", "ghost"),
        ["Whimper"] = E("Whimper", "ghost"),
        ["Groan"] = E("Groan", "ghost"),
        ["Grunt"] = E("Grunt", "ghost"),
        ["Growling"] = E("GROWL", "ghost"),
        ["Roar"] = E("ROAR", "ghost"),
        ["Screaming"] = E("SCREAM", "ghost"),
        ["Shout"] = E("Shout", "ghost"),
        ["Yell"] = E("Yell", "ghost"),
        ["Bellow"] = E("Bellow", "ghost"),
        ["Whoop"] = E("Whoop", "ghost"),
        ["Crying, sobbing"] = E("Crying", "ghost"),
        ["Wail, moan"] = E("MOAN", "ghost"),
        ["Sigh"] = E("Sigh", "ghost"),
        ["Laughter"] = E("Laugh", "ghost"),
        ["Giggle"] = E("Giggle", "ghost"),
        ["Chuckle, chortle"] = E("Chuckle", "ghost"),
        ["Snicker"] = E("Snicker", "ghost"),
        ["Belly laugh"] = E("Laugh", "ghost"),
        ["Humming"] = E("Humming", "ghost"),
        ["Singing"] = E("Singing", "ghost"),
        ["Child singing"] = E("Child singing", "ghost"),
        ["Baby cry, infant cry"] = E("Baby crying", "ghost"),
        ["Baby laughter"] = E("Baby laugh", "ghost"),
        ["Child speech, kid speaking"] = E("Child voice", "ghost"),
        ["Children shouting"] = E("Child voice", "ghost"),
        ["Children playing"] = E("Child voice", "ghost"),
        ["Hiss"] = E("Hiss", "ghost"),
        ["Speech"] = E("Voice", "voice"),
        ["Male speech, man speaking"] = E("Voice", "voice"),
        ["Female speech, woman speaking"] = E("Voice", "voice"),
        ["Conversation"] = E("Voice", "voice"),
        ["Narration, monologue"] = E("Voice", "voice"),
        ["Speech synthesizer"] = E("Voice", "voice"),
        ["Chatter"] = E("Voice", "voice"),
        ["Babbling"] = E("Babbling", "ghost"),
        ["Chewing, mastication"] = E("Chewing", "ghost"),
        ["Burping, eructation"] = E("Burp", "ghost"),
        ["Hiccup"] = E("Hiccup", "ghost"),
        ["Fart"] = E("Fart", "ghost"),
        ["Finger snapping"] = E("Finger snap", "object"),
        ["Clapping"] = E("Clap", "object"),
        ["Hands"] = E("Hands", "object"),
        ["Heartbeat"] = E("HEARTBEAT", "alert"),
        // environment
        ["Wind"] = E("Wind", "env"),
        ["Wind noise (microphone)"] = E("Wind", "env"),
        ["Rustling leaves"] = E("Rustling", "env"),
        ["Howl (wind)"] = E("Howling wind", "env"),
        ["Rain"] = E("Rain", "env"),
        ["Raindrop"] = E("Rain", "env"),
        ["Rain on surface"] = E("Rain", "env"),
        ["Thunderstorm"] = E("THUNDER", "env"),
        ["Thunder"] = E("THUNDER", "env"),
        ["Fire"] = E("Fire", "env"),
        ["Vehicle"] = E("Vehicle", "env"),
        ["Car"] = E("Car", "env"),
        ["Truck"] = E("Truck", "env"),
        ["Engine"] = E("Engine", "env"),
        ["Idling"] = E("Engine idling", "env"),
        ["Bird"] = E("Bird", "env"),
        ["Bird vocalization, bird call, bird song"] = E("Bird", "env"),
        ["Crow"] = E("Crow", "env"),
        ["Owl"] = E("Owl", "env"),
        ["Dog"] = E("Dog", "env"),
        ["Bark"] = E("Dog bark", "env"),
        ["Growling"] = E("GROWL", "ghost"),
        ["Cat"] = E("Cat", "env"),
        ["Insect"] = E("Crickets (outside)", "env"),
        ["Cricket"] = E("Crickets (outside)", "env"),
        ["Fly, housefly"] = E("Fly", "env"),
        ["Bee, wasp, etc."] = E("Buzzing", "env"),
        ["Frog"] = E("Frog", "env"),
        ["Whistling"] = E("Whistling", "ghost"),
        ["Whistle"] = E("Whistle", "alert"),
        ["Steam"] = E("Steam", "env"),
        ["Air conditioning"] = E("Fan/AC", "env"),
        ["Mechanical fan"] = E("Fan", "env"),
        ["Light engine (high frequency)"] = E("Engine", "env"),
        ["Squeal"] = E("Squeal", "ghost"),
        ["Screech"] = E("Screech", "ghost"),
        ["Whir"] = E("Whir", "env"),
        ["Vibration"] = E("Vibration", "env"),
        ["Rumble"] = E("Rumble", "env"),
        ["Throbbing"] = E("Throbbing", "env"),
        ["Music"] = E("Music", "music"),
        ["Piano"] = E("Piano", "music"),
        ["Organ"] = E("Organ", "music"),
        ["Electric piano"] = E("Piano", "music"),
        ["Keyboard (musical)"] = E("Piano", "music"),
        ["Bell"] = E("Bell", "alert"),
        ["Scary music"] = E("Scary music", "music"),
        ["Tender music"] = E("Music", "music"),
        ["Sad music"] = E("Music", "music"),
        ["Ambient music"] = E("Music", "music"),
        ["New-age music"] = E("Music", "music"),
        ["Background music"] = E("Music", "music"),
        ["Theme music"] = E("Music", "music"),
        ["Soundtrack music"] = E("Music", "music"),
        ["Drone"] = E("Drone", "music"),
        ["Musical instrument"] = E("Music", "music"),
        ["Synthesizer"] = E("Music", "music"),
        ["Plucked string instrument"] = E("Music", "music"),
        ["Guitar"] = E("Music", "music"),
        ["Violin, fiddle"] = E("Music", "music"),
        ["Bowed string instrument"] = E("Music", "music"),
        ["Orchestra"] = E("Music", "music"),
        ["Choir"] = E("Choir", "music"),
        ["Wind instrument, woodwind instrument"] = E("Music", "music"),
        ["Brass instrument"] = E("Music", "music"),
        ["Drum"] = E("Drum", "music"),
        ["Percussion"] = E("Percussion", "music"),
        ["Clatter"] = E("Clatter", "object"),
        ["Car alarm"] = E("CAR ALARM", "alert"),
        ["Vehicle horn, car horn, honking"] = E("Car horn", "alert"),
        ["Car passing by"] = E("Car", "env"),
        ["Sound effect"] = E("Sound effect", "other"),
        ["Effects unit"] = E("Sound effect", "other"),
        ["Chorus effect"] = E("Sound effect", "other"),
        ["Echo"] = E("Echo", "other"),
        ["Reverberation"] = E("Reverb", "other"),
    };

    /// Sounds you make when you walk (hidden while a movement key is held and the sound is centered).
    public static readonly HashSet<string> OwnMoveLabels = new() { "Footsteps", "Running", "Shuffling", "Stomping", "Floor creak" };

    /// Sounds you make when you press an action key: doors you open, items you pick up, drop, or use.
    public static readonly HashSet<string> OwnActionLabels = new()
    {
        "Creak", "Squeak", "Door", "Cupboard", "Drawer", "Sliding door", "Wood", "Knock / thump", "Clink / click",
        "Thud", "Bounce", "Clatter", "Rattle", "Keys", "Clink", "Click", "Camera", "Paper", "Writing", "Whoosh", "Scrape",
        // labels learned from the game's own sound files (things you can also cause yourself)
        "Door open", "Door close", "Door creak", "Door moving", "Door lock", "Cabinet / drawer", "Gate", "Light switch",
        "Item thrown / dropped", "Object interaction", "Door open / creak", "Drawer / cabinet", "Lock / key", "Water / tap", "Sink / tap", "Toilet flush", "Squeaky toy", "Tarot card", "Spirit box",
    };

    /// Sounds that actually occur in Phasmophobia (labels after mapping). Everything else is dropped in this profile.
    public static readonly Dictionary<string, HashSet<string>> Profiles = new()
    {
        ["phasmophobia"] = new HashSet<string>
        {
            // movement (ghost and players)
            "Footsteps", "Running", "Shuffling", "Stomping",
            // doors, cupboards, drawers, windows
            "Creak", "Squeak", "Door", "SLAM", "Knock", "Tap", "Cupboard", "Drawer", "Sliding door", "Wood", "Crack",
            // thrown / dropped items, kitchen, furniture
            // SHATTER, Breaking, Crack need 60% confidence (StrictMin); below that they show as "Clink / click"
            "Thud", "Bounce", "Clatter", "Rattle", "Scrape", "Glass", "Clink", "Dishes", "Cutlery", "Keys", "Knock / thump", "Clink / click",
            "SHATTER", "Breaking", "Crack",
            "Paper", "Writing", "Camera", "Click", "Clock", "Whoosh",
            // water, electrics, phones, radios
            "Faucet", "Sink", "Drip", "Water", "Pouring", "Splash",
            "PHONE", "PHONE RINGING", "Ringtone", "Radio", "Static", "TV", "Beep", "Ping", "Electric hum", "Hum", "Buzz", "Buzzing", "Electricity",
            "Piano", "MUSIC BOX", "CAR ALARM", "Car horn",
            // hunts, ghost events, spirit box, ouija
            "HEARTBEAT", "Breathing", "Wheeze", "Gasp", "Panting", "WHISPER", "Whimper", "Groan", "Grunt", "GROWL", "ROAR",
            "SCREAM", "Shout", "Yell", "MOAN", "Crying", "Sigh", "Laugh", "Giggle", "Chuckle", "Humming", "Singing", "Child singing",
            "Child voice", "Hiss", "Voice", "Babbling", "Screech", "Squeal", "Whistling",
            // weather, animals, traffic, and fire are atmosphere only: the ghost never makes them, so they are left out
        },
    };

    public static readonly HashSet<string> Ignore = new()
    {
        "Silence", "Inside, small room", "Inside, large room or hall", "Inside, public space",
        "Outside, urban or manmade", "Outside, rural or natural", "Noise", "Environmental noise",
        "Sound reproduction", "Male singing", "Female singing", "Field recording", "Audio logo",
        "Jingle (music)", "Loop", "Sampler", "Distortion", "Sidetone", "Cacophony",
        // too generic to be useful (the specific classes like Cricket, Dog, Owl still show)
        "Animal", "Wild animals", "Domestic animals, pets", "Livestock, farm animals, working animals",
        "Snake", "Rodents, mice", "Mouse", "Patter",
    };
}
