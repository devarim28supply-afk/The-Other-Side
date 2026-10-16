// Extracts every sound from The Other Side (UE 5.4, one Oodle pak) to gameclips\.
// The game ships unversioned properties (no .usmap), so SoundWaves are not parsed as objects:
// the Bink Audio ("ABEU") first chunk is cut out of the .uexp and joined with the .ubulk
// stream chunks, then vgmstream-cli decodes the .binka to .wav.
// Usage: TosAudioExtract list <out.txt>
//        TosAudioExtract extract <outDir> [pathFilter]
using System.Diagnostics;
using System.Text;
using CUE4Parse.Compression;
using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Versions;

const string GameDir = @"D:\Steam\steamapps\common\The Other Side\TheOtherSide\Content\Paks";
const string Vgmstream = @"D:\Tools\vgmstream\vgmstream-cli.exe";
var toolDir = AppContext.BaseDirectory;

var oodle = Path.Combine(toolDir, OodleHelper.OodleFileName);
if (!File.Exists(oodle)) OodleHelper.DownloadOodleDll(ref oodle);
OodleHelper.Initialize(oodle);
var zlib = Path.Combine(toolDir, ZlibHelper.DllName);
if (!File.Exists(zlib)) ZlibHelper.DownloadDll(zlib, null!);
ZlibHelper.Initialize(zlib);

var provider = new DefaultFileProvider(GameDir, SearchOption.TopDirectoryOnly, new VersionContainer(EGame.GAME_UE5_4), StringComparer.OrdinalIgnoreCase);
provider.Initialize();
provider.SubmitKey(new CUE4Parse.UE4.Objects.Core.Misc.FGuid(), new CUE4Parse.Encryption.Aes.FAesKey(new byte[32]));
Console.WriteLine($"files: {provider.Files.Count}");

var mode = args.Length > 0 ? args[0] : "list";
if (mode == "list")
{
    File.WriteAllLines(args[1], provider.Files.Keys.OrderBy(k => k));
    return;
}
if (mode == "raw")
{
    // raw <pak path without extension> <out dir>: saves .uasset/.uexp/.ubulk as stored (e.g. to read key mappings)
    foreach (var ext in new[] { ".uasset", ".uexp", ".ubulk" })
        if (provider.TrySaveAsset(args[1] + ext, out var bytes))
            File.WriteAllBytes(Path.Combine(args[2], Path.GetFileName(args[1]) + ext), bytes);
    return;
}

if (mode == "scan")
{
    // scan <out.tsv> <name1,name2,...>: which assets (Blueprints etc.) reference these names, e.g. sound functions
    var names = args[2].Split(',');
    var pats = names.Select(s => Encoding.ASCII.GetBytes(s)).ToArray();
    var rows = new List<string> { "asset\t" + string.Join("\t", names) };
    foreach (var path in provider.Files.Keys.Where(k => k.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase)).OrderBy(k => k))
    {
        if (!provider.TrySaveAsset(path, out var head)) continue;
        var hits = pats.Select(pt => head.AsSpan().IndexOf(pt) >= 0 ? "1" : "").ToArray();
        if (hits.Any(h => h != "")) rows.Add(path + "\t" + string.Join("\t", hits));
    }
    File.WriteAllLines(args[1], rows);
    Console.WriteLine($"{rows.Count - 1} assets");
    return;
}

var outDir = args[1];
var filter = args.Length > 2 ? args[2] : null;
Directory.CreateDirectory(outDir);
var tmp = Path.Combine(outDir, "_tmp.binka");
var index = new List<string> { "path\tformat\tseconds" };
var soundWave = Encoding.ASCII.GetBytes("SoundWave");
var abeu = Encoding.ASCII.GetBytes("ABEU");
int n = 0, fail = 0, other = 0;

foreach (var path in provider.Files.Keys.Where(k => k.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase)).OrderBy(k => k))
{
    if (filter != null && !path.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
    var stem = path[..^".uasset".Length];
    if (!provider.TrySaveAsset(path, out var head) || head.AsSpan().IndexOf(soundWave) < 0) continue;
    if (!provider.TrySaveAsset(stem + ".uexp", out var uexp)) continue;

    var p = uexp.AsSpan().IndexOf(abeu);
    if (p < 0)
    {
        // Not Bink Audio (or a SoundCue/other asset that only references SoundWave)
        var kind = uexp.AsSpan().IndexOf("OggS"u8) >= 0 ? "ogg" : uexp.AsSpan().IndexOf("RIFF"u8) >= 0 ? "riff" : null;
        if (kind != null) { other++; Console.WriteLine($"OTHER {kind} {stem}"); }
        continue;
    }
    // First chunk length: the serialized chunk table right after it repeats the size twice (u32 size, u32 size).
    int first = -1;
    for (int k = 24; p + k + 8 <= uexp.Length && k < 1 << 20; k++)
        if (BitConverter.ToInt32(uexp, p + k) == k && BitConverter.ToInt32(uexp, p + k + 4) == k) { first = k; break; }
    if (first < 0) { fail++; Console.WriteLine($"NOCHUNK {stem}"); continue; }

    using (var fs = File.Create(tmp))
    {
        fs.Write(uexp, p, first);
        if (provider.TrySaveAsset(stem + ".ubulk", out var bulk)) fs.Write(bulk);
    }
    var rel = stem.Replace('/', Path.DirectorySeparatorChar);
    if (rel.StartsWith(@"TheOtherSide\Content\")) rel = rel[@"TheOtherSide\Content\".Length..];
    var wav = Path.Combine(outDir, rel + ".wav");
    Directory.CreateDirectory(Path.GetDirectoryName(wav)!);
    var psi = new ProcessStartInfo(Vgmstream, $"-o \"{wav}\" \"{tmp}\"") { RedirectStandardOutput = true, RedirectStandardError = true };
    using var proc = Process.Start(psi)!;
    var outText = proc.StandardOutput.ReadToEnd(); proc.StandardError.ReadToEnd(); proc.WaitForExit();
    if (proc.ExitCode != 0 || !File.Exists(wav)) { fail++; Console.WriteLine($"DECODE {stem}"); continue; }
    var m = System.Text.RegularExpressions.Regex.Match(outText, @"play duration: \d+ samples \((\d+):([\d.]+) seconds\)");
    var secs = m.Success ? int.Parse(m.Groups[1].Value) * 60 + double.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture) : 0;
    index.Add($"{rel}\tbinka\t{secs:0.###}");
    if (++n % 200 == 0) Console.WriteLine($"{n} ...");
}
File.Delete(tmp);
File.WriteAllLines(Path.Combine(outDir, "index.tsv"), index);
Console.WriteLine($"done: {n} clips, {fail} failed, {other} other-format");
