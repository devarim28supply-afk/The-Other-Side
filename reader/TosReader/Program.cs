using System.Diagnostics;
using TosReader;

// Outside reader for The Other Side: looks at the running game's memory, read-only.
//   TosReader scan [words]     find the tables, list classes + objects named like words
//   TosReader props <Class>    list a class's properties and offsets (from the game's reflection)
//   TosReader audio            list sound components that are playing right now
//   TosReader box [sec]        watch spirit boxes: where an answer clip shows up (ask the box questions meanwhile)
//   TosReader [feed] [file]    (default) write the live sound feed for the overlay, waits for the game

const string Game = "TheOtherSide-Win64-Shipping";

string mode = args.Length > 0 ? args[0] : "feed";
string[] rest = args.Skip(1).ToArray();

if (mode == "feed")
{
    string path = rest.Length > 0 ? rest[0] : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "soundfeed.txt"));
    // one reader per feed file: a second start (the .cmd clicked twice) just leaves
    using var one = new Mutex(true, "TosReader-" + path.ToLowerInvariant().Replace('\\', '/'), out bool first);
    if (!first) { Console.WriteLine("The reader is already running."); return 0; }
    // everything printed also goes to reader.log next to the feed
    var logStream = new FileStream(Path.Combine(Path.GetDirectoryName(path)!, "reader.log"), FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
    var logFile = new StreamWriter(logStream) { AutoFlush = true };
    Console.SetOut(new Tee(Console.Out, logFile));
    while (true)
    {
        Mem? g;
        while ((g = Mem.Open(Game)) == null) Thread.Sleep(2000);
        using (g)
        {
            Console.WriteLine($"{DateTime.Now:HH:mm:ss} game process {g.Pid} opened READ-ONLY");
            Thread.Sleep(3000);                                  // let the game finish starting
            try { RunFeed(g, path); }
            catch (Exception e) { Console.WriteLine("reader stopped: " + e.Message); }
        }
        Console.WriteLine($"{DateTime.Now:HH:mm:ss} game closed, waiting for it to start again");
        while (Process.GetProcessesByName(Game).Length > 0) Thread.Sleep(2000);
    }
}

using var mem = Mem.Open(Game);
if (mem == null) { Console.WriteLine("The Other Side is not running."); return 1; }
Console.WriteLine($"game process {mem.Pid} opened READ-ONLY");

var ue = new Unreal(mem);
var sw = Stopwatch.StartNew();
if (!ue.Find(Console.Out)) return 2;
Console.WriteLine($"tables found in {sw.ElapsedMilliseconds} ms, {ue.Count} objects");

if (mode == "scan")
{
    sw.Restart();
    var byClass = new Dictionary<string, int>();
    var hits = new List<string>();
    string[] look = rest.Length > 0 ? rest : ["Ghost", "Hunt", "Player"];
    foreach (var (i, o) in ue.Objects())
    {
        var cls = ue.ClassName(o);
        byClass[cls] = byClass.GetValueOrDefault(cls) + 1;
        var name = ue.ObjName(o);
        if (name.StartsWith("Default__")) continue;
        if (look.Any(w => name.Contains(w, StringComparison.OrdinalIgnoreCase)) && cls != "Function" && !cls.EndsWith("Property"))
            hits.Add($"  [{i}] {cls}  {ue.Path(o)}");
    }
    Console.WriteLine($"walked all objects in {sw.ElapsedMilliseconds} ms, {byClass.Count} classes");
    Console.WriteLine("\nmost common classes:");
    foreach (var (c, n) in byClass.OrderByDescending(kv => kv.Value).Take(15))
        Console.WriteLine($"  {n,7}  {c}");
    Console.WriteLine($"\nobjects named like {string.Join("/", look)} ({hits.Count}):");
    foreach (var h in hits.Take(80)) Console.WriteLine(h);
    return 0;
}

var rf = new Reflect(mem, ue);
rf.Load();

if (mode == "props")
{
    for (ulong st = rf.Class(rest[0]); st != 0; st = rf.Super(st))
    {
        Console.WriteLine($"-- {ue.ObjName(st)}");
        foreach (var (n, off, t) in rf.Props(st)) Console.WriteLine($"  0x{off:X4}  {t,-20} {n}");
    }
    return 0;
}

if (mode == "audio")
{
    ulong audioCls = rf.Class("AudioComponent");
    int oSound = rf.Offset("AudioComponent", "Sound");
    var (oActive, mActive) = rf.Bool("ActorComponent", "bIsActive");
    Console.WriteLine($"AudioComponent.Sound at 0x{oSound:X}, bIsActive at 0x{oActive:X} mask {mActive}");
    int total = 0, active = 0;
    foreach (var (i, o) in ue.Objects())
    {
        if (!rf.IsA(ue.Class(o), audioCls) || ue.ObjName(o).StartsWith("Default__")) continue;
        total++;
        bool on = (mem.Read<byte>(o + (ulong)oActive) & mActive) != 0;
        if (!on) continue;
        active++;
        ulong snd = mem.Ptr(o + (ulong)oSound);
        Console.WriteLine($"  {ue.ObjName(snd),-40} on {ue.Path(ue.Outer(o))}");
    }
    Console.WriteLine($"{active} active of {total} sound components");
    return 0;
}

if (mode == "hunt")
{
    // where in the heap is a playing sound referenced? (to find the engine's active-sound list)
    var feed = new Feed(mem, ue, rf, Path.Combine(Path.GetTempPath(), "tos-reader-probe.txt"), Console.Out);
    int oSound = rf.Offset("AudioComponent", "Sound");
    var (oA, mA) = rf.Bool("ActorComponent", "bIsActive");
    var playing = feed.AudioComponents().Where(c => (mem.Read<byte>(c + (ulong)oA) & mA) != 0).ToList();
    feed.Close();
    foreach (var c in playing)
    {
        ulong s = mem.Ptr(c + (ulong)oSound);
        if (!ue.ObjName(s).Contains(rest.Length > 0 ? rest[0] : "Clock")) continue;
        sw.Restart();
        var hits = mem.FindQword(s);
        Console.WriteLine($"comp 0x{c:X} sound {ue.ObjName(s)} 0x{s:X}: {hits.Count} refs in {sw.ElapsedMilliseconds} ms");
        foreach (var h in hits)
        {
            // the clock's world X, as a double, near the reference = a record of this sound with its position
            double cx = mem.F64(c + 0x1F0);
            var b = mem.Bytes(h - 0x100, 0x600);
            var near = new List<string>();
            for (int q = 0; q + 8 <= b.Length; q += 8)
            {
                ulong v = BitConverter.ToUInt64(b, q);
                if (v == c) near.Add($"comp@{q - 0x100:X}");
                if (Math.Abs(BitConverter.ToDouble(b, q) - cx) < 0.5) near.Add($"X@{q - 0x100:X}");
                if (q + 4 <= b.Length && Math.Abs(BitConverter.ToSingle(b, q) - cx) < 0.5) near.Add($"fX@{q - 0x100:X}");
            }
            Console.WriteLine($"  0x{h:X}  {(h >= c && h < c + 0x1000 ? "inside comp" : "")} {string.Join(" ", near)}");
        }
        // who points at the start of each reference's record (sound ref minus 0..0x40)?
        var starts = new HashSet<ulong>();
        foreach (var h in hits) for (ulong k = 0; k <= 0x40; k += 8) starts.Add(h - k);
        sw.Restart();
        foreach (var (at, v) in mem.FindAny(starts))
        {
            ulong h = hits.First(x => x - v <= 0x40);
            Console.WriteLine($"  ptr at 0x{at:X} -> 0x{v:X} (= ref 0x{h:X} - 0x{h - v:X})   next qwords {mem.Ptr(at + 8):X} {mem.Ptr(at + 16):X}");
        }
        Console.WriteLine($"  back-pointer scan {sw.ElapsedMilliseconds} ms");
        break;
    }
    return 0;
}

if (mode == "lists")
{
    // rest = addresses of pointer slots seen in "hunt"; print each pointer list around them
    var heads = new HashSet<ulong>();
    foreach (var a in rest)
    {
        ulong at = Convert.ToUInt64(a, 16);
        ulong s = at;
        while (SoundRec(mem.Ptr(s - 8)) != null) s -= 8;
        var items = new List<string>();
        ulong e = s;
        for (; items.Count < 200; e += 8)
        {
            var n = SoundRec(mem.Ptr(e));
            if (n == null) break;
            items.Add(n);
        }
        Console.WriteLine($"list at 0x{s:X}: {items.Count} sounds: {string.Join(", ", items.Take(12))}");
        heads.Add(s);
    }
    sw.Restart();
    foreach (var (at, v) in mem.FindAny(heads))
        Console.WriteLine($"  TArray? at 0x{at:X} -> 0x{v:X}  num {mem.I32(at + 8)} max {mem.I32(at + 12)}");
    Console.WriteLine($"  scan {sw.ElapsedMilliseconds} ms");
    return 0;

    // name of the sound if p looks like an active-sound record (USoundBase* at +0x18)
    string? SoundRec(ulong p)
    {
        if (p < 0x10000 || p > 0x7FF000000000 || (p & 0xF) != 0) return null;
        ulong snd = mem.Ptr(p + 0x18);
        if (snd < 0x10000 || snd > 0x7FF000000000) return null;
        var cls = ue.ClassName(snd);
        return cls.StartsWith("Sound") || cls.StartsWith("MetaSound") ? ue.ObjName(snd) : null;
    }
}

if (mode == "owner")
{
    // rest[0] = address of a field (the active-sound TArray). Who points near below it = its owning struct.
    ulong field = Convert.ToUInt64(rest[0], 16);
    var bases = new HashSet<ulong>();
    for (ulong b = field - 0x20000; b <= field; b += 0x10) bases.Add(b);
    var counts = new Dictionary<ulong, int>();
    var hits = mem.FindAny(bases, 200000);
    foreach (var (_, v) in hits) counts[v] = counts.GetValueOrDefault(v) + 1;
    foreach (var (v, n) in counts.OrderByDescending(kv => kv.Value).Take(8))
        Console.WriteLine($"  0x{v:X} (field - 0x{field - v:X}) pointed to {n} times, vtable 0x{mem.Ptr(v):X} {(mem.Ptr(v) - mem.Base < (ulong)mem.Size ? "in exe" : "")}");
    // engine objects that hold one of the top candidates
    ulong engine = ue.Objects().Select(x => x.obj).FirstOrDefault(o => ue.ClassName(o) == "GameEngine" && !ue.ObjName(o).StartsWith("Default__"));
    Console.WriteLine($"GameEngine 0x{engine:X}");
    foreach (var (at, v) in hits.Where(h => h.at >= engine && h.at < engine + 0x2000))
        Console.WriteLine($"  GameEngine+0x{at - engine:X} -> 0x{v:X} (field - 0x{field - v:X})");
    return 0;
}

if (mode == "recs")
{
    // rest[0] = TArray header of the active-sound list: dump each record's names + doubles
    ulong hdr = Convert.ToUInt64(rest[0], 16);
    ulong data = mem.Ptr(hdr);
    int num = mem.I32(hdr + 8);
    Console.WriteLine($"{num} active sounds");
    for (int i = 0; i < num; i++)
    {
        ulong r = mem.Ptr(data + (ulong)i * 8);
        Console.WriteLine($"[{i}] 0x{r:X} {ue.ObjName(mem.Ptr(r + 0x18))}");
        var b = mem.Bytes(r, 0x400);
        for (int q = 0; q + 8 <= b.Length; q += 8)
        {
            uint idx = BitConverter.ToUInt32(b, q), n2 = BitConverter.ToUInt32(b, q + 4);
            if (idx > 0x100 && idx < 0x2000000 && n2 < 64)
            {
                var s = ue.Name(idx);
                if (s.Length > 3 && s.All(ch => ch >= 32 && ch < 127) && (s.Contains('_') || s.Contains("Component")))
                    Console.WriteLine($"    +0x{q:X3} name {s}{(n2 > 0 ? "_" + (n2 - 1) : "")}");
            }
            for (int h = 0; h < 8; h += 4)
            {
                uint id = BitConverter.ToUInt32(b, q + h);
                if (id < 1000 || id >= ue.Count) continue;
                ulong o = ue.Object((int)id);
                if (o != 0 && mem.I32(o + 0xC) == id && ue.ObjName(o).Contains("_C_"))
                    Console.WriteLine($"    +0x{q + h:X3} object #{id} {ue.ObjName(o)} ({ue.ClassName(o)})");
            }
            double d = BitConverter.ToDouble(b, q);
            if (Math.Abs(d) > 50 && Math.Abs(d) < 200000 && Math.Abs(d - Math.Round(d, 3)) < 1e-9 | Math.Abs(d) > 50 && Math.Abs(d) < 200000 && d % 1 != 0)
                Console.WriteLine($"    +0x{q:X3} double {d:0.0}");
        }
    }
    return 0;
}

if (mode == "raw")
{
    // first properties of a class, as raw qwords (to check the FField layout)
    ulong f = rest.Length > 1 ? rf.Prop(rf.Class(rest[0]), rest[1]) : mem.Ptr(rf.Class(rest[0]) + 0x50);
    for (int k = 0; k < (rest.Length > 1 ? 1 : 3) && f != 0; k++)
    {
        var b = mem.Bytes(f, 0x90);
        for (int q = 0; q < 0x90; q += 8)
            Console.WriteLine($"  +0x{q:X2}  {BitConverter.ToUInt64(b, q):X16}  {ue.FName(f + (ulong)q)}");
        Console.WriteLine();
        f = mem.Ptr(f + 0x18);
    }
    return 0;
}

if (mode == "box")
{
    // where does a spirit box answer go? Watch for [sec] (default 600):
    //  - SFX_SpiritBox_* sound objects (which are loaded, and any that appear later = picked on demand)
    //  - every sound component inside a spirit box actor: its Sound and on/off, when they change
    //  - pointers to any SFX_SpiritBox_* object inside the box components and the box actor, 1 and 2 levels deep
    int secs = rest.Length > 0 ? int.Parse(rest[0]) : 600;
    ulong audioCls = rf.Class("AudioComponent");
    int oSound = rf.Offset("AudioComponent", "Sound");
    var (oActive, mActive) = rf.Bool("ActorComponent", "bIsActive");
    static bool H(ulong p) => p > 0x10000 && p < 0x7FF000000000 && (p & 7) == 0;
    var answers = new Dictionary<ulong, string>();
    var comps = new Dictionary<ulong, string>();               // box component -> last "sound on/off"
    var boxActors = new HashSet<ulong>();
    var refsSeen = new Dictionary<string, string>();          // slot -> clip it pointed at last
    var end = DateTime.Now.AddSeconds(secs);
    var walk = Stopwatch.StartNew();
    bool firstWalk = true;
    string T() => DateTime.Now.ToString("HH:mm:ss.f");
    void Scan(ulong at, string where, int size)
    {
        var b = mem.Bytes(at, size);
        for (int q = 0; q + 8 <= b.Length; q += 8)
        {
            ulong p = BitConverter.ToUInt64(b, q);
            if (!H(p)) continue;
            if (answers.TryGetValue(p, out var n1)) { Report($"{where}+0x{q:X}", n1); continue; }
            var b2 = mem.Bytes(p, 0x100);
            for (int r = 0; r + 8 <= b2.Length; r += 8)
                if (answers.TryGetValue(BitConverter.ToUInt64(b2, r), out var n2)) Report($"{where}+0x{q:X} -> +0x{r:X}", n2);
        }
    }
    // a slot is printed when it first points at a clip and whenever it points at another one
    void Report(string slot, string clip)
    {
        if (refsSeen.GetValueOrDefault(slot) == clip) return;
        refsSeen[slot] = clip;
        Console.WriteLine($"{T()} REF {slot} -> {clip}");
    }
    Console.WriteLine($"watching spirit boxes for {secs} s - ask the box questions now");
    while (DateTime.Now < end)
    {
        if (firstWalk || walk.ElapsedMilliseconds > 1000)
        {
            walk.Restart();
            foreach (var (_, o) in ue.Objects())
            {
                string name = ue.ObjName(o);
                if (name.StartsWith("Default__")) continue;
                if (name.StartsWith("SFX_SpiritBox_", StringComparison.OrdinalIgnoreCase) && ue.ClassName(o).StartsWith("Sound"))
                {
                    if (answers.TryAdd(o, name) && !firstWalk) Console.WriteLine($"{T()} NEW clip object loaded: {name}");
                }
                else if (rf.IsA(ue.Class(o), audioCls))
                {
                    ulong actor = ue.Outer(o);
                    if (ue.ClassName(actor).Contains("SpiritBox", StringComparison.OrdinalIgnoreCase) && comps.TryAdd(o, ""))
                    {
                        boxActors.Add(actor);
                        Console.WriteLine($"{T()} box component {name} in {ue.ObjName(actor)} ({ue.ClassName(actor)})");
                    }
                }
            }
            if (firstWalk) Console.WriteLine($"{T()} {answers.Count} SFX_SpiritBox_ clip objects loaded, {comps.Count} box components");
            firstWalk = false;
        }
        foreach (var c in comps.Keys.ToList())
        {
            ulong s = mem.Ptr(c + (ulong)oSound);
            bool on = (mem.Read<byte>(c + (ulong)oActive) & mActive) != 0;
            string now = $"{ue.ObjName(s)} ({ue.ClassName(s)}) {(on ? "ON" : "off")}";
            if (now != comps[c]) { Console.WriteLine($"{T()} {ue.ObjName(c)}: {now}"); comps[c] = now; }
            if (answers.Count > 0) Scan(c, ue.ObjName(c), 0x1000);
        }
        if (answers.Count > 0)
            foreach (var a in boxActors) Scan(a, ue.ObjName(a), 0x1000);
        Thread.Sleep(100);
    }
    return 0;
}

if (mode == "params")
{
    // spirit box sound parameters (MetaSound inputs set by the game), printed when they change, for [sec] (default 600)
    int secs = rest.Length > 0 ? int.Parse(rest[0]) : 600;
    ulong audioCls = rf.Class("AudioComponent");
    int oDef = rf.Offset("AudioComponent", "DefaultParameters"), oInst = rf.Offset("AudioComponent", "InstanceParameters");
    const int Stride = 0xA0;                                  // FAudioParameter (UE 5.4)
    var last = new Dictionary<string, string>();
    var comps = new List<ulong>();
    var end = DateTime.Now.AddSeconds(secs);
    var walk = Stopwatch.StartNew();
    bool first = true;
    string P(ulong arr)
    {
        ulong data = mem.Ptr(arr); int n = mem.I32(arr + 8);
        if (n <= 0 || n > 64 || data == 0) return $"({n})";
        var sb = new System.Text.StringBuilder($"({n})");
        for (int i = 0; i < n; i++)
        {
            ulong e = data + (ulong)(i * Stride);
            ulong obj = mem.Ptr(e + 0x18);
            int objs = mem.I32(e + 0x68);
            sb.Append($" [{ue.FName(e)} f={mem.F32(e + 8):0.##} b={mem.Read<byte>(e + 0xC)} i={mem.I32(e + 0x10)} type={mem.Read<byte>(e + 0x80)}");
            if (obj != 0) sb.Append($" obj={ue.ObjName(obj)}");
            if (objs > 0 && objs < 50)
                for (int k = 0; k < objs; k++) sb.Append($" obj[{k}]={ue.ObjName(mem.Ptr(mem.Ptr(e + 0x60) + (ulong)k * 8))}");
            sb.Append(']');
        }
        return sb.ToString();
    }
    Console.WriteLine($"watching spirit box parameters for {secs} s");
    while (DateTime.Now < end)
    {
        if (first || walk.ElapsedMilliseconds > 3000)
        {
            walk.Restart(); first = false;
            comps.Clear();
            foreach (var (_, o) in ue.Objects())
                if (rf.IsA(ue.Class(o), audioCls) && ue.ClassName(ue.Outer(o)).Contains("SpiritBox", StringComparison.OrdinalIgnoreCase)) comps.Add(o);
        }
        foreach (var c in comps)
        {
            string key = $"{ue.ObjName(ue.Outer(c))}.{ue.ObjName(c)}";
            string now = $"default {P(c + (ulong)oDef)}  instance {P(c + (ulong)oInst)}";
            if (last.GetValueOrDefault(key) != now) { last[key] = now; Console.WriteLine($"{DateTime.Now:HH:mm:ss.f} {key}: {now}"); }
        }
        Thread.Sleep(100);
    }
    return 0;
}

if (mode == "diff")
{
    // which values inside a spirit box change, and when (to find the "ghost answered" moment), for [sec] (default 600):
    // the box actor, its sound component and its EvidenceActor, first 0x420 bytes each (the box actor ends at 0x3C0); a slot that keeps changing
    // (timers, positions) is printed 20 times and then muted
    int secs = rest.Length > 0 ? int.Parse(rest[0]) : 600;
    const int Size = 0x420;
    ulong boxCls = rf.Class("BP_Holdable_SpiritBox_C");
    int oMain = rf.Offset("BP_Holdable_SpiritBox_C", "MainAudio"), oEv = rf.Offset("BP_Holdable_SpiritBox_C", "EvidenceActor");
    var prev = new Dictionary<ulong, byte[]>();
    var count = new Dictionary<(ulong, int), int>();
    var targets = new List<(ulong at, string name)>();
    var end = DateTime.Now.AddSeconds(secs);
    var walk = Stopwatch.StartNew();
    bool first = true;
    static bool H(ulong p) => p > 0x10000 && p < 0x7FF000000000 && (p & 7) == 0;
    Console.WriteLine($"watching spirit box values for {secs} s");
    while (DateTime.Now < end)
    {
        if (first || walk.ElapsedMilliseconds > 3000)
        {
            walk.Restart(); first = false;
            var t = new List<(ulong, string)>();
            foreach (var (_, o) in ue.Objects())
                if (ue.Class(o) == boxCls && !ue.ObjName(o).StartsWith("Default__"))
                {
                    string n = ue.ObjName(o)[^4..];
                    t.Add((o, $"box{n}"));
                    if (mem.Ptr(o + (ulong)oMain) is ulong a && a != 0) t.Add((a, $"box{n}.MainAudio"));
                    if (mem.Ptr(o + (ulong)oEv) is ulong e && e != 0) t.Add((e, $"box{n}.Evidence({ue.ClassName(e)})"));
                }
            foreach (var x in t) if (!targets.Contains(x)) Console.WriteLine($"{DateTime.Now:HH:mm:ss.f} watching {x.Item2}");
            targets = t;
        }
        foreach (var (at, name) in targets)
        {
            var b = mem.Bytes(at, Size);
            if (b.Length < Size) continue;
            if (prev.TryGetValue(at, out var old))
                for (int q = 0; q < Size; q += 8)
                {
                    ulong ov = BitConverter.ToUInt64(old, q), nv = BitConverter.ToUInt64(b, q);
                    if (ov == nv) continue;
                    int c = count[(at, q)] = count.GetValueOrDefault((at, q)) + 1;
                    if (c > 20) continue;
                    string what = H(nv) && ue.ObjName(nv) is string on && on.Length > 0 ? $" ({on})" : "";
                    Console.WriteLine($"{DateTime.Now:HH:mm:ss.f} {name}+0x{q:X3}: {ov:X} -> {nv:X}{what}" +
                        $"  f={BitConverter.ToSingle(b, q):0.###}/{BitConverter.ToSingle(b, q + 4):0.###}{(c == 20 ? "  (now muted)" : "")}");
                }
            prev[at] = b;
        }
        Thread.Sleep(100);
    }
    return 0;
}

Console.WriteLine("unknown mode " + mode);
return 3;

static void RunFeed(Mem g, string path)
{
    var ue = new Unreal(g);
    // right after the game starts the tables may not be filled in yet: keep trying for a minute
    for (int t = 0; !ue.Find(Console.Out); t++)
    {
        if (t >= 12 || !g.Alive) throw new Exception("game tables not found");
        Thread.Sleep(5000);
    }
    var rf = new Reflect(g, ue);
    rf.Load();
    var feed = new Feed(g, ue, rf, path, Console.Out);
    try
    {
        Console.WriteLine("writing " + path);
        int tries = 0;
        while (g.Alive)
        {
            Console.WriteLine($"{DateTime.Now:HH:mm:ss} looking for the game's list of playing sounds... (try {++tries})");
            var sw = Stopwatch.StartNew();
            if (!feed.FindList())
            {
                Console.WriteLine($"  not found ({sw.ElapsedMilliseconds / 1000.0:0.0} s), trying again - be in a round and walk around");
                Thread.Sleep(3000);
                continue;
            }
            Console.WriteLine($"{DateTime.Now:HH:mm:ss} found in {sw.ElapsedMilliseconds / 1000.0:0.0} s: {feed.Offsets()}");
            var you = Stopwatch.StartNew();
            while (g.Alive && feed.Tick())
            {
                if (you.ElapsedMilliseconds >= 5000)                 // you (local player) after map changes
                {
                    if (feed.Stale(feed.AudioComponents())) break;  // new round: the list moved, find it again
                    you.Restart();
                }
                Thread.Sleep(50);
            }
        }
    }
    finally { feed.Close(); }
}

sealed class Tee(TextWriter a, TextWriter b) : TextWriter
{
    public override System.Text.Encoding Encoding => a.Encoding;
    public override void Write(char c) { a.Write(c); b.Write(c); }
    public override void Write(string? s) { a.Write(s); b.Write(s); }
    public override void WriteLine(string? s) { a.WriteLine(s); b.WriteLine(s); }
    public override void Flush() { a.Flush(); b.Flush(); }
}
