using System;
using System.Collections.Generic;
using System.Linq;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace PhasmoSound;

/// Listens to a recording device (your game mic, e.g. "CABLE Output") and hands out 16 kHz mono samples,
/// so the captioner can show what your text-to-speech voice really sounded like in the mic ("YOU: ...").
public sealed class MicListener : IDisposable
{
    public event Action<float[]>? Samples16k;
    readonly WasapiCapture? cap;
    readonly int rate, channels, bps;
    readonly bool isFloat;
    double srcIndex, outPos;
    float prev;

    public MicListener(string deviceName)
    {
        var dev = new MMDeviceEnumerator().EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
            .FirstOrDefault(d => d.FriendlyName.Contains(deviceName, StringComparison.OrdinalIgnoreCase));
        if (dev == null) { Log.Write("own-voice captions: mic not found: " + deviceName); return; }
        cap = new WasapiCapture(dev, true, 20);
        var f = cap.WaveFormat;
        rate = f.SampleRate; channels = f.Channels; bps = f.BitsPerSample / 8;
        isFloat = f.Encoding == WaveFormatEncoding.IeeeFloat
               || (f is WaveFormatExtensible x && x.SubFormat == NAudio.Dmo.AudioMediaSubtypes.MEDIASUBTYPE_IEEE_FLOAT);
        cap.DataAvailable += OnData;
        cap.StartRecording();
        Log.Write($"own-voice captions: listening to {dev.FriendlyName} ({rate} Hz, {channels} ch)");
    }

    void OnData(object? s, WaveInEventArgs e)
    {
        int frameBytes = channels * bps, frames = e.BytesRecorded / frameBytes;
        double step = rate / (double)AudioEngine.TargetRate;
        var outp = new List<float>(frames / 2 + 8);
        for (int i = 0; i < frames; i++)
        {
            float mono = 0;
            for (int c = 0; c < channels; c++)
            {
                int off = i * frameBytes + c * bps;
                mono += isFloat ? BitConverter.ToSingle(e.Buffer, off)
                      : bps == 2 ? BitConverter.ToInt16(e.Buffer, off) / 32768f
                      : bps == 4 ? BitConverter.ToInt32(e.Buffer, off) / 2147483648f : 0f;
            }
            mono /= channels;
            // linear-interpolating resampler to 16 kHz (speech only, no anti-alias filter needed for captions)
            while (outPos <= srcIndex)
            {
                float t = (float)(outPos - (srcIndex - 1));
                outp.Add(prev + (mono - prev) * t);
                outPos += step;
            }
            prev = mono;
            srcIndex++;
        }
        if (outp.Count > 0) Samples16k?.Invoke(outp.ToArray());
    }

    public void Dispose()
    {
        try { cap?.StopRecording(); cap?.Dispose(); } catch { }
    }
}
