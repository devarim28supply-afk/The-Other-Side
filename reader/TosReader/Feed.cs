using System.Globalization;
using System.Text;

namespace TosReader;

// Writes the same soundfeed.txt lines as the in-game SoundSpy mod, but read from outside the game:
//   L  x y z yaw                  listener = local camera
//   S  id name x y z range src    a sound started (id = the engine's record of it)
//   M  id x y z                   it moved
//   E  id                         it stopped
//   2  name src                   a sound with no position
// src = ghost | player | own | other.
//
// Every sound the game plays (one-shots at a spot, attached sounds, components) has a record in the audio
// device's active-sound list (TArray<FActiveSound*>). That list is not reachable from a fixed address, so it
// is found once per game start from a sound component that is playing: component -> USoundBase -> records
// that point at it -> lists that hold those records -> the TArray header that holds the list.
sealed class Feed
{
    readonly Mem m;
    readonly Unreal ue;
    readonly Reflect rf;
    readonly StreamWriter w;
    readonly TextWriter log;

    readonly ulong audioCls, localPlayerCls;
    readonly int oSound, oSoundAtten, oAttenStruct, oFalloff, oExtents;
    readonly int oActive; readonly byte mActive;
    readonly int oOwner, oPC, oPawn, oCam, oCache, oPOV, oLoc, oRot;

    // FActiveSound (UE 5.4, seen in this game): USoundBase* at 0x18, owner actor's object index at 0x40,
    // FTransform at 0x230 (translation at 0x250)
    const int RecSound = 0x18, RecOwnerId = 0x40, RecPos = 0x250;

    ulong listHeader;                                        // &FAudioDevice::ActiveSounds
    readonly Dictionary<ulong, ulong> classOf = new();       // object -> class
    readonly Dictionary<ulong, int> kindOf = new();          // class -> 1 AudioComponent, 2 LocalPlayer, 0 other
    readonly Dictionary<ulong, string?> soundName = new();   // USoundBase* -> name (null = not a sound)
    readonly Dictionary<ulong, Playing> seen = new();
    double[]? lastL;
    ulong localPC;
    int scans;

    sealed record Playing(ulong Sound, double X, double Y, double Z, bool Skip);

    public Feed(Mem mem, Unreal u, Reflect r, string path, TextWriter logTo)
    {
        m = mem; ue = u; rf = r; log = logTo;
        audioCls = rf.Class("AudioComponent");
        localPlayerCls = rf.Class("LocalPlayer");
        oSound = rf.Offset("AudioComponent", "Sound");
        oSoundAtten = rf.Offset("SoundBase", "AttenuationSettings");
        oAttenStruct = rf.Offset("SoundAttenuation", "Attenuation");
        oFalloff = rf.Offset("SoundAttenuationSettings", "FalloffDistance");
        oExtents = rf.Offset("SoundAttenuationSettings", "AttenuationShapeExtents");
        (oActive, mActive) = rf.Bool("ActorComponent", "bIsActive");
        oOwner = rf.Offset("Actor", "Owner");
        oPC = rf.Offset("Player", "PlayerController");
        oPawn = rf.Offset("Controller", "Pawn");
        oCam = rf.Offset("PlayerController", "PlayerCameraManager");
        oCache = rf.Offset("PlayerCameraManager", "CameraCachePrivate");
        oPOV = rf.Offset("CameraCacheEntry", "POV");
        oLoc = rf.Offset("MinimalViewInfo", "Location");
        oRot = rf.Offset("MinimalViewInfo", "Rotation");
        var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        w = new StreamWriter(fs, new UTF8Encoding(false));
    }

    public string Offsets() =>
        $"Sound 0x{oSound:X} bIsActive 0x{oActive:X}/{mActive} Owner 0x{oOwner:X} PC 0x{oPC:X} Pawn 0x{oPawn:X} Cam 0x{oCam:X} " +
        $"Cache 0x{oCache:X} active-sound list 0x{listHeader:X}";

    static string F(double v) => v.ToString("0", CultureInfo.InvariantCulture);
    static string F1(double v) => v.ToString("0.0", CultureInfo.InvariantCulture);
    static string Clean(string s) => s.Replace('\t', ' ').Replace('\n', ' ');

    void Emit(string line) => w.WriteLine(line);
    public void Close() => w.Dispose();

    static bool Heap(ulong p) => p > 0x10000 && p < 0x7FF000000000;

    // name of a USoundBase, or null if p is not one
    string? SoundName(ulong p)
    {
        if (!Heap(p) || (p & 7) != 0) return null;
        if (soundName.TryGetValue(p, out var s)) return s;
        var cls = ue.ClassName(p);
        s = cls.StartsWith("Sound") || cls.StartsWith("MetaSound") ? Clean(ue.ObjName(p)) : null;
        soundName[p] = s;
        return s;
    }

    bool IsRecord(ulong p) => Heap(p) && (p & 0xF) == 0 && SoundName(m.Ptr(p + RecSound)) != null;

    // the list header still holds a sane list
    bool ListOk()
    {
        if (listHeader == 0) return false;
        ulong data = m.Ptr(listHeader);
        int num = m.I32(listHeader + 8), max = m.I32(listHeader + 12);
        if (num < 0 || num > max || max > 100000) return false;
        return num == 0 || (Heap(data) && IsRecord(m.Ptr(data)) && IsRecord(m.Ptr(data + (ulong)(num - 1) * 8)));
    }

    // One-time search for the active-sound list (three passes over the game's heap, ~15-20 s).
    public bool FindList()
    {
        var sounds = new HashSet<ulong>();
        foreach (var c in AudioComponents())
            if ((m.Read<byte>(c + (ulong)oActive) & mActive) != 0)
            {
                ulong s = m.Ptr(c + (ulong)oSound);
                if (SoundName(s) != null) sounds.Add(s);
            }
        if (sounds.Count == 0) { log.WriteLine("  no sound playing yet to start from"); return false; }

        var recs = new HashSet<ulong>();
        foreach (var (at, _) in m.FindAny(sounds, 20000))
            if (((at - RecSound) & 0xF) == 0) recs.Add(at - RecSound);
        var starts = new HashSet<ulong>();
        foreach (var (slot, _) in m.FindAny(recs, 20000))
        {
            ulong s = slot;
            for (int k = 0; k < 4096 && IsRecord(m.Ptr(s - 8)); k++) s -= 8;
            starts.Add(s);
        }
        int best = -1;
        ulong found = 0;
        foreach (var (hdr, _) in m.FindAny(starts, 20000))
        {
            listHeader = hdr;
            int num = m.I32(hdr + 8);
            if (!ListOk() || num == 0) continue;
            bool all = true;
            for (int i = 0; i < num && all; i++) all = IsRecord(m.Ptr(m.Ptr(hdr) + (ulong)i * 8));
            if (all && num > best) { best = num; found = hdr; }
        }
        listHeader = found;
        log.WriteLine($"  {sounds.Count} playing sounds -> {recs.Count} records -> {starts.Count} lists -> header 0x{found:X} ({best} sounds)");
        return found != 0;
    }

    double Range(ulong sound)
    {
        ulong a = m.Ptr(sound + (ulong)oSoundAtten);
        if (a == 0) return -1;
        ulong s = a + (ulong)oAttenStruct;
        return (m.F32(s + (ulong)oFalloff) + m.F64(s + (ulong)oExtents)) / 100;
    }

    // ghost / player / own / other, from the owner actor's object index, walking up through owners
    string Kind(uint ownerId, ulong pawn)
    {
        if (ownerId == 0 || ownerId >= (uint)ue.Count) return "other";
        ulong a = ue.Object((int)ownerId);
        if (a == 0 || m.U32(a + 0xC) != ownerId) return "other";
        for (int d = 0; a != 0 && d < 3; d++)
        {
            if (a == pawn && pawn != 0) return "own";
            var cls = ue.ClassName(a);
            if (cls.Contains("Ghost")) return "ghost";
            if (cls.Contains("Survivor") || cls.Contains("Character_")) return "player";
            a = m.Ptr(a + (ulong)oOwner);
        }
        return "other";
    }

    // All objects whose class is (a subclass of) AudioComponent; also picks up the local player controller.
    public List<ulong> AudioComponents()
    {
        var list = new List<ulong>();
        if (++scans % 20 == 0) classOf.Clear();                 // addresses get reused: re-read every class now and then
        foreach (var (_, o) in ue.Objects())
        {
            if (!classOf.TryGetValue(o, out var cls)) classOf[o] = cls = ue.Class(o);
            if (!kindOf.TryGetValue(cls, out var kind))
                kindOf[cls] = kind = rf.IsA(cls, audioCls) ? 1 : rf.IsA(cls, localPlayerCls) ? 2 : 0;
            if (kind == 0 || ue.ObjName(o).StartsWith("Default__")) continue;
            if (kind == 1) list.Add(o);
            else
            {
                ulong pc = m.Ptr(o + (ulong)oPC);
                if (pc != 0) localPC = pc;
            }
        }
        return list;
    }

    int bad;
    public bool Verbose = true;                              // log who owns sounds marked "other"

    // false when the list went bad for a while (then FindList again)
    public bool Tick()
    {
        ulong pawn = localPC == 0 ? 0 : m.Ptr(localPC + (ulong)oPawn);
        ulong cam = localPC == 0 ? 0 : m.Ptr(localPC + (ulong)oCam);
        if (cam != 0)
        {
            ulong pov = cam + (ulong)oCache + (ulong)oPOV;
            double x = m.F64(pov + (ulong)oLoc), y = m.F64(pov + (ulong)oLoc + 8), z = m.F64(pov + (ulong)oLoc + 16);
            double yaw = m.F64(pov + (ulong)oRot + 8);
            if (lastL == null || Math.Abs(x - lastL[0]) + Math.Abs(y - lastL[1]) + Math.Abs(z - lastL[2]) > 5 || Math.Abs(yaw - lastL[3]) > 1)
            {
                lastL = [x, y, z, yaw];
                Emit($"L\t{F(x)}\t{F(y)}\t{F(z)}\t{F1(yaw)}");
            }
        }

        if (!ListOk()) { w.Flush(); return ++bad < 50; }
        bad = 0;
        ulong data = m.Ptr(listHeader);
        int num = m.I32(listHeader + 8);
        var ptrs = num == 0 ? Array.Empty<byte>() : m.Bytes(data, num * 8);
        var now = new Dictionary<ulong, Playing>();
        for (int i = 0; i + 8 <= ptrs.Length; i += 8)
        {
            ulong r = BitConverter.ToUInt64(ptrs, i);
            var rec = m.Bytes(r, RecPos + 24);
            if (rec.Length == 0) continue;
            ulong sound = BitConverter.ToUInt64(rec, RecSound);
            string? name = SoundName(sound);
            if (name == null) continue;
            seen.TryGetValue(r, out var prev);
            if (prev != null && prev.Sound == sound && prev.Skip) { now[r] = prev; continue; }
            if (name.StartsWith("UDS_")) { now[r] = new Playing(sound, 0, 0, 0, true); continue; }   // weather beds
            double x = BitConverter.ToDouble(rec, RecPos), y = BitConverter.ToDouble(rec, RecPos + 8), z = BitConverter.ToDouble(rec, RecPos + 16);
            if (prev == null || prev.Sound != sound)
            {
                if (prev != null) Emit($"E\t{r}");
                uint ownerId = BitConverter.ToUInt32(rec, RecOwnerId);
                string src = Kind(ownerId, pawn);
                if (src == "other" && Verbose)
                {
                    ulong ow = ownerId > 0 && ownerId < (uint)ue.Count ? ue.Object((int)ownerId) : 0;
                    log.WriteLine($"{DateTime.Now:HH:mm:ss} {name}: owner #{ownerId} {ue.ObjName(ow)} ({ue.ClassName(ow)}), you = {ue.ObjName(pawn)} ({ue.ClassName(pawn)})");
                }
                if (x == 0 && y == 0 && z == 0) Emit($"2\t{name}\t{src}");
                else Emit($"S\t{r}\t{name}\t{F(x)}\t{F(y)}\t{F(z)}\t{F1(Range(sound))}\t{src}");
                prev = new Playing(sound, x, y, z, false);
            }
            else if (Math.Abs(x - prev.X) + Math.Abs(y - prev.Y) + Math.Abs(z - prev.Z) > 30)
            {
                Emit($"M\t{r}\t{F(x)}\t{F(y)}\t{F(z)}");
                prev = prev with { X = x, Y = y, Z = z };
            }
            now[r] = prev;
        }
        foreach (var (id, p) in seen)
            if (!now.ContainsKey(id) && !p.Skip) Emit($"E\t{id}");
        seen.Clear();
        foreach (var kv in now) seen[kv.Key] = kv.Value;
        w.Flush();
        return true;
    }
}
