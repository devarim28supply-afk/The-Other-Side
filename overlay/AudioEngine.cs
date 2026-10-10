using System;
using System.Collections.Generic;
using System.Linq;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace PhasmoSound;

public sealed class LevelFrame
{
    public float[] ChannelRms = Array.Empty<float>();
    public float LoudDb;
    public bool Onset;
    public float OnsetDb;
    public DateTime Time;
}

/// Captures what the chosen output device is playing (WASAPI loopback), reports
/// per-channel levels every ~20 ms, detects sudden sounds, and keeps a 16 kHz mono
/// ring buffer for the sound classifier.
/// WASAPI loopback with a small buffer (NAudio's default is 100 ms, which alone adds up to 100 ms of delay).
sealed class LowLatencyLoopback : WasapiCapture
{
    public LowLatencyLoopback(MMDevice device, int bufferMs) : base(device, false, bufferMs) { }
    protected override AudioClientStreamFlags GetAudioClientStreamFlags() => AudioClientStreamFlags.Loopback;
}

public sealed class AudioEngine : IDisposable
{
    public const int TargetRate = 16000;
    public MMDevice Device { get; }
    public string DeviceName => Device.FriendlyName;
    public int Channels { get; private set; }
    public int SampleRate { get; private set; }
    public event Action<LevelFrame>? Frame;
    /// 16 kHz mono samples as they are produced (audio thread) - used to feed the captioner.
    public event Action<float[]>? Samples16k;

    readonly Config cfg;
    WasapiCapture? cap;
    WasapiOut? keepAlive;
    bool isFloat; int bytesPerSample;

    // block accumulation
    float[] sumSq = Array.Empty<float>();
    int blockFrames, blockLen;
    float smoothDb = -100f;
    int blocksSeen;
    DateTime lastOnset = DateTime.MinValue;

    // resampler -> 16k mono ring
    double ratio, outPos; long srcIndex; float prevMono, lp;
    readonly float[] ring = new float[TargetRate * 8];
    int ringWrite; long ringCount;
    readonly object ringLock = new();
    readonly List<float> pending = new(4096);

    public AudioEngine(Config cfg, MMDevice device)
    {
        this.cfg = cfg;
        Device = device;
    }

    public static MMDevice PickDevice(string wanted)
    {
        using var e = new MMDeviceEnumerator();
        var all = e.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).ToList();
        foreach (var d in all)
            Log.Write($"output device: {d.FriendlyName} ch={d.AudioClient.MixFormat.Channels} rate={d.AudioClient.MixFormat.SampleRate}");
        if (!string.Equals(wanted, "auto", StringComparison.OrdinalIgnoreCase))
        {
            var m = all.FirstOrDefault(d => d.FriendlyName.Contains(wanted, StringComparison.OrdinalIgnoreCase));
            if (m != null) return m;
            Log.Write($"device \"{wanted}\" not found, falling back to auto");
        }
        return e.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console);
    }

    public void Start()
    {
        cap = new LowLatencyLoopback(Device, cfg.CaptureBufferMs);
        var fmt = cap.WaveFormat;
        Channels = fmt.Channels;
        SampleRate = fmt.SampleRate;
        bytesPerSample = fmt.BitsPerSample / 8;
        isFloat = fmt.Encoding == WaveFormatEncoding.IeeeFloat
               || (fmt is WaveFormatExtensible ext && ext.SubFormat == new Guid("00000003-0000-0010-8000-00aa00389b71"));
        Log.Write($"capture: {DeviceName} ch={Channels} rate={SampleRate} bits={fmt.BitsPerSample} float={isFloat}");

        sumSq = new float[Channels];
        blockLen = SampleRate / 200; // 5 ms
        ratio = (double)SampleRate / TargetRate;
        outPos = 0; srcIndex = 0;

        // Keep the loopback stream alive by playing silence on the device.
        try
        {
            keepAlive = new WasapiOut(Device, AudioClientShareMode.Shared, true, 200);
            keepAlive.Init(new SilenceProvider(WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, Channels)));
            keepAlive.Play();
        }
        catch (Exception ex) { Log.Write("keep-alive failed (ok): " + ex.Message); }

        cap.DataAvailable += OnData;
        cap.RecordingStopped += (_, e) => Log.Write("recording stopped " + e.Exception?.Message);
        cap.StartRecording();
    }

    void OnData(object? s, WaveInEventArgs e)
    {
        int ch = Channels, bps = bytesPerSample, frameBytes = ch * bps;
        int frames = e.BytesRecorded / frameBytes;
        var buf = e.Buffer;
        // single-pole low-pass before decimation (~7 kHz at 48k)
        float a = 1f - MathF.Exp(-2f * MathF.PI * 7000f / SampleRate);
        pending.Clear();
        for (int f = 0; f < frames; f++)
        {
            float mono = 0f;
            int baseOff = f * frameBytes;
            for (int c = 0; c < ch; c++)
            {
                int off = baseOff + c * bps;
                float v;
                if (isFloat) v = BitConverter.ToSingle(buf, off);
                else if (bps == 2) v = BitConverter.ToInt16(buf, off) / 32768f;
                else if (bps == 4) v = BitConverter.ToInt32(buf, off) / 2147483648f;
                else if (bps == 3) v = ((buf[off] << 8) | (buf[off + 1] << 16) | (buf[off + 2] << 24)) / 2147483648f;
                else v = 0f;
                sumSq[c] += v * v;
                mono += v;
            }
            mono /= ch;
            lp += a * (mono - lp);
            // linear-interpolating resampler to 16 kHz
            while (outPos <= srcIndex)
            {
                float t = (float)(outPos - (srcIndex - 1));
                pending.Add(prevMono + (lp - prevMono) * t);
                outPos += ratio;
            }
            prevMono = lp;
            srcIndex++;
            if (++blockFrames >= blockLen) EmitBlock();
        }
        if (pending.Count > 0)
        {
            lock (ringLock)
            {
                foreach (var v in pending)
                {
                    ring[ringWrite] = v;
                    ringWrite = (ringWrite + 1) % ring.Length;
                }
                ringCount += pending.Count;
            }
            if (Samples16k != null) Samples16k(pending.ToArray());
        }
    }

    void EmitBlock()
    {
        var rms = new float[Channels];
        double total = 0;
        for (int c = 0; c < Channels; c++)
        {
            rms[c] = MathF.Sqrt(sumSq[c] / blockFrames);
            total += sumSq[c];
            sumSq[c] = 0f;
        }
        float loud = 20f * MathF.Log10(MathF.Sqrt((float)(total / (blockFrames * Channels))) + 1e-7f);
        blockFrames = 0;

        if (blocksSeen < 200) { smoothDb = blocksSeen == 0 ? loud : smoothDb + 0.5f * (loud - smoothDb); blocksSeen++; }
        float prevSmooth = smoothDb;
        smoothDb += (loud > smoothDb ? 0.2f : 0.035f) * (loud - smoothDb);
        float jump = loud - prevSmooth;
        var now = DateTime.UtcNow;
        // no onsets during the first second: the smoother is still settling
        bool onset = blocksSeen >= 200 && jump >= cfg.OnsetDb && loud > cfg.MinDb && (now - lastOnset).TotalMilliseconds > 120;
        if (onset) lastOnset = now;

        Frame?.Invoke(new LevelFrame { ChannelRms = rms, LoudDb = loud, Onset = onset, OnsetDb = jump, Time = now });
    }

    /// Copies the most recent n samples (16 kHz mono) into dst. Returns false if not enough audio yet.
    public bool GetLatest(int n, float[] dst)
    {
        lock (ringLock)
        {
            if (ringCount < n) return false;
            int start = (ringWrite - n + ring.Length * 2) % ring.Length;
            for (int i = 0; i < n; i++) dst[i] = ring[(start + i) % ring.Length];
            return true;
        }
    }

    public void Dispose()
    {
        try { cap?.StopRecording(); } catch { }
        cap?.Dispose();
        try { keepAlive?.Stop(); } catch { }
        keepAlive?.Dispose();
    }
}

public static class Direction
{
    // Standard WAVE channel order. NaN = LFE (no direction). 0 = front, clockwise, degrees.
    public static float[]? Angles(int ch) => ch switch
    {
        1 => new[] { 0f },
        2 => new[] { -30f, 30f },
        4 => new[] { -45f, 45f, -135f, 135f },
        6 => new[] { -30f, 30f, 0f, float.NaN, -110f, 110f },
        8 => new[] { -30f, 30f, 0f, float.NaN, -150f, 150f, -90f, 90f },
        _ => null
    };

    public static bool IsSurround(int ch) => ch >= 4;

    /// Returns (angle degrees, confidence 0..1). Stereo uses left/right panning (front half only).
    public static (float angle, float conf) Estimate(float[] rms)
    {
        int ch = rms.Length;
        if (ch == 2)
        {
            float l = rms[0], r = rms[1], s = l + r + 1e-9f;
            float pan = (r - l) / s;
            return (Math.Clamp(pan * 90f, -90f, 90f), MathF.Abs(pan));
        }
        var ang = Angles(ch);
        if (ang == null) return (0f, 0f);
        double x = 0, y = 0, sum = 0;
        for (int c = 0; c < ch; c++)
        {
            if (float.IsNaN(ang[c])) continue;
            double a = ang[c] * Math.PI / 180.0;
            x += rms[c] * Math.Sin(a); y += rms[c] * Math.Cos(a); sum += rms[c];
        }
        if (sum <= 1e-9) return (0f, 0f);
        float angle = (float)(Math.Atan2(x, y) * 180.0 / Math.PI);
        float conf = (float)(Math.Sqrt(x * x + y * y) / sum);
        return (angle, conf);
    }
}
