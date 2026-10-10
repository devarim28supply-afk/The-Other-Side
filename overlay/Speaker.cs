using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace PhasmoSound;

/// Text to speech into the virtual microphone the game listens to.
/// Engine: Microsoft neural voices via edge-tts (the Type to Speak venv). Fallback: Windows SAPI voice.
public sealed class Speaker
{
    readonly Config cfg;
    public event Action<string>? Status;
    public Speaker(Config cfg) { this.cfg = cfg; }

    public bool IsSpeaking { get; private set; }
    /// true when the voice starts playing into the mic, false when the line is over (or failed).
    public event Action<bool>? Playing;

    // ---- Kokoro: local server started with the overlay
    Process? kokoro;
    static readonly System.Net.Http.HttpClient http = new() { Timeout = TimeSpan.FromSeconds(20) };

    public void StartKokoro()
    {
        if (!cfg.UseKokoro) return;
        try
        {
            if (!File.Exists(cfg.KokoroPython) || !File.Exists(cfg.KokoroScript)) { Log.Write("kokoro: python or script missing, skipping"); return; }
            var psi = new ProcessStartInfo(cfg.KokoroPython)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(cfg.KokoroScript)!,
            };
            psi.ArgumentList.Add(cfg.KokoroScript); psi.ArgumentList.Add(cfg.KokoroPort.ToString());
            kokoro = Process.Start(psi);
            if (kokoro == null) return;
            ChildJob.Add(kokoro);
            kokoro.OutputDataReceived += (_, e) => { if (e.Data != null) Log.Write("kokoro: " + e.Data); };
            kokoro.ErrorDataReceived += (_, e) => { if (e.Data != null && !e.Data.Contains("Warning") && !e.Data.Contains("warn")) Log.Write("kokoro! " + e.Data); };
            kokoro.BeginOutputReadLine(); kokoro.BeginErrorReadLine();
            Log.Write("kokoro server starting (pid " + kokoro.Id + ")");
        }
        catch (Exception ex) { Log.Write("kokoro start failed: " + ex.Message); }
    }

    public void StopKokoro()
    {
        try { if (kokoro != null && !kokoro.HasExited) kokoro.Kill(true); } catch { }
    }

    async Task<WaveStream?> SynthesizeKokoro(string text)
    {
        if (!cfg.UseKokoro) return null;
        try
        {
            string url = $"http://127.0.0.1:{cfg.KokoroPort}/say?voice={Uri.EscapeDataString(cfg.KokoroVoice)}&speed={cfg.KokoroSpeed:F2}&text={Uri.EscapeDataString(text)}";
            var sw = Stopwatch.StartNew();
            var bytes = await http.GetByteArrayAsync(url);
            Log.Write($"kokoro synthesized in {sw.ElapsedMilliseconds} ms");
            return new WaveFileReader(new MemoryStream(bytes));
        }
        catch (Exception ex) { Log.Write("kokoro unavailable (" + ex.Message.Split('\n')[0] + "), falling back"); return null; }
    }

    public Phrases? Phrases { get; set; }

    public async Task SayAsync(string text)
    {
        text = text.Trim();
        if (text.Length == 0) return;
        string mp3 = Path.Combine(Path.GetTempPath(), $"phasmosound_say_{Environment.TickCount64}.mp3");
        WaveStream? reader = null;
        // 1. pre-made or cached audio for this exact line (instant)
        string? ready = Phrases?.Lookup(text, cfg.UseKokoro ? "kokoro" : "edge", cfg.UseKokoro ? cfg.KokoroVoice : cfg.SpeakVoice);
        if (ready != null)
        {
            try { reader = ready.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) ? new WaveFileReader(ready) : new MediaFoundationReader(ready); Log.Write("speak: using " + Path.GetFileName(ready)); }
            catch (Exception ex) { Log.Write("cached audio unreadable: " + ex.Message); reader = null; }
        }
        // 2. Kokoro (local), cached for next time
        if (reader == null)
        {
            reader = await SynthesizeKokoro(text);
            if (reader != null && Phrases != null)
            {
                try
                {
                    string cp = Phrases.CachePath(text, "kokoro", cfg.KokoroVoice);
                    using (var w = new WaveFileWriter(cp, reader.WaveFormat)) { reader.CopyTo(w); }
                    reader.Position = 0;
                }
                catch (Exception ex) { Log.Write("cache write: " + ex.Message); }
            }
        }
        // 3. edge-tts (online)
        try
        {
            if (reader == null && await SynthesizeEdge(text, mp3)) reader = new MediaFoundationReader(mp3);
        }
        catch (Exception ex) { Log.Write("edge-tts failed: " + ex.Message); }

        try
        {
            if (reader == null)
            {
                Status?.Invoke("online voice unavailable, using Windows voice");
                reader = SynthesizeSapi(text);
            }
            var dev = PickDevice();
            if (dev == null) { Status?.Invoke("mic device not found: " + cfg.SpeakDevice); Log.Write("speak: device not found " + cfg.SpeakDevice); return; }
            IsSpeaking = true;
            using (reader)
            using (var outp = new WasapiOut(dev, AudioClientShareMode.Shared, true, 60))
            {
                // Make it look like a real microphone to the game's voice gate: a short lead-in and tail,
                // and a faint noise floor under the voice so the gate stays open between words.
                var voice = new NAudio.Wave.SampleProviders.VolumeSampleProvider(reader.ToSampleProvider()) { Volume = Math.Clamp(cfg.SpeakVolume, 0f, 1f) };
                var shaped = new MicLikeProvider(voice, cfg.SpeakLeadInMs, cfg.SpeakTailMs, cfg.SpeakNoiseFloor);
                outp.Init(shaped);
                outp.Play();
                Playing?.Invoke(true);
                var tcs = new TaskCompletionSource();
                outp.PlaybackStopped += (_, _) => tcs.TrySetResult();
                await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(60)));
            }
            Log.Write($"spoke \"{text}\" via {dev.FriendlyName}");
        }
        catch (Exception ex) { Log.Write("speak: " + ex.Message); Status?.Invoke("speak failed: " + ex.Message); }
        finally
        {
            IsSpeaking = false;
            Playing?.Invoke(false);
            try { File.Delete(mp3); } catch { }
        }
    }

    MMDevice? PickDevice()
    {
        using var e = new MMDeviceEnumerator();
        var all = e.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).ToList();
        return all.FirstOrDefault(d => d.FriendlyName.Contains(cfg.SpeakDevice, StringComparison.OrdinalIgnoreCase));
    }

    async Task<bool> SynthesizeEdge(string text, string mp3)
    {
        string py = cfg.EdgeTtsPython;
        if (!File.Exists(py)) { Log.Write("edge-tts python not found: " + py); return false; }
        var psi = new ProcessStartInfo(py)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true,
        };
        psi.ArgumentList.Add("-m"); psi.ArgumentList.Add("edge_tts");
        psi.ArgumentList.Add("--voice"); psi.ArgumentList.Add(cfg.SpeakVoice);
        psi.ArgumentList.Add("--rate"); psi.ArgumentList.Add(cfg.SpeakRate);
        psi.ArgumentList.Add("--text"); psi.ArgumentList.Add(text);
        psi.ArgumentList.Add("--write-media"); psi.ArgumentList.Add(mp3);
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        try { await p.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException) { try { p.Kill(); } catch { } Log.Write("edge-tts timed out"); return false; }
        if (p.ExitCode != 0 || !File.Exists(mp3) || new FileInfo(mp3).Length < 100)
        {
            Log.Write("edge-tts error: " + (await err).Trim().Split('\n').LastOrDefault());
            return false;
        }
        return true;
    }

    /// Wraps a voice stream with lead-in/tail and a faint noise floor (what a real mic sends).
    sealed class MicLikeProvider : ISampleProvider
    {
        readonly ISampleProvider src; readonly int leadSamples; readonly int tailSamples; readonly float noise;
        int leadLeft, tailLeft; bool srcDone; readonly Random rng = new();
        public WaveFormat WaveFormat => src.WaveFormat;
        public MicLikeProvider(ISampleProvider src, int leadMs, int tailMs, float noise)
        {
            this.src = src; this.noise = noise;
            leadSamples = leadLeft = src.WaveFormat.SampleRate * src.WaveFormat.Channels * leadMs / 1000;
            tailSamples = tailLeft = src.WaveFormat.SampleRate * src.WaveFormat.Channels * tailMs / 1000;
        }
        public int Read(float[] buffer, int offset, int count)
        {
            int n = 0;
            while (n < count)
            {
                if (leadLeft > 0) { int k = Math.Min(count - n, leadLeft); Fill(buffer, offset + n, k); leadLeft -= k; n += k; continue; }
                if (!srcDone)
                {
                    int got = src.Read(buffer, offset + n, count - n);
                    if (got == 0) { srcDone = true; continue; }
                    if (noise > 0) for (int i = 0; i < got; i++) buffer[offset + n + i] += (float)(rng.NextDouble() * 2 - 1) * noise;
                    n += got; continue;
                }
                if (tailLeft > 0) { int k = Math.Min(count - n, tailLeft); Fill(buffer, offset + n, k); tailLeft -= k; n += k; continue; }
                break;
            }
            return n;
        }
        void Fill(float[] b, int o, int k) { for (int i = 0; i < k; i++) b[o + i] = noise > 0 ? (float)(rng.NextDouble() * 2 - 1) * noise : 0f; }
    }

    /// Offline fallback: Windows built-in voice rendered to a WAV stream.
    static WaveStream SynthesizeSapi(string text)
    {
        var ms = new MemoryStream();
        using (var synth = new System.Speech.Synthesis.SpeechSynthesizer())
        {
            var male = synth.GetInstalledVoices().Select(v => v.VoiceInfo).FirstOrDefault(v => v.Gender == System.Speech.Synthesis.VoiceGender.Male);
            if (male != null) synth.SelectVoice(male.Name);
            synth.SetOutputToWaveStream(ms);
            synth.Speak(text);
        }
        ms.Position = 0;
        return new WaveFileReader(ms);
    }
}
