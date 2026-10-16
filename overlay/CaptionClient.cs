using System;
using System.Linq;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace PhasmoSound;

/// Partial = still being spoken (WhisperLive sends it word by word); it is replaced until the final version arrives.
public sealed class CaptionLine { public string Text = ""; public DateTime Time; public bool Partial; public bool FromGame; }

/// Streams the overlay's 16 kHz audio to the local Whisper captioner and collects the captions it returns.
public sealed class CaptionClient : IDisposable
{
    readonly Config cfg;
    readonly OverlayState state;
    Process? server;
    TcpClient? tcp;
    NetworkStream? stream;
    readonly object sendLock = new();
    readonly CancellationTokenSource cts = new();
    public bool Connected => stream != null;

    readonly bool launchServer;
    readonly string prefix;

    /// launchServer false = a second stream to the same captioner (e.g. your own mic, prefix "YOU: ").
    public CaptionClient(Config cfg, OverlayState state, bool launchServer = true, string prefix = "")
    {
        this.cfg = cfg; this.state = state; this.launchServer = launchServer; this.prefix = prefix;
    }

    public void Start()
    {
        if (!cfg.WhisperCaptions) return;
        if (!launchServer) { new Thread(ConnectLoop) { IsBackground = true, Name = "captions " + prefix }.Start(); return; }
        try
        {
            string py = string.IsNullOrEmpty(cfg.WhisperPython) ? cfg.KokoroPython : cfg.WhisperPython;
            if (File.Exists(py) && File.Exists(cfg.WhisperScript))
            {
                var psi = new ProcessStartInfo(py)
                {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                    WorkingDirectory = Path.GetDirectoryName(cfg.WhisperScript)!,
                };
                psi.ArgumentList.Add(cfg.WhisperScript); psi.ArgumentList.Add(cfg.WhisperPort.ToString());
                psi.ArgumentList.Add(cfg.WhisperModel); psi.ArgumentList.Add(cfg.WhisperDevice);
                server = Process.Start(psi);
                if (server != null)
                {
                    ChildJob.Add(server);
                    server.OutputDataReceived += (_, e) => { if (e.Data != null) Log.Write("whisper: " + e.Data); };
                    server.ErrorDataReceived += (_, e) => { if (e.Data != null && e.Data.Contains("rror")) Log.Write("whisper! " + e.Data); };
                    server.BeginOutputReadLine(); server.BeginErrorReadLine();
                    Log.Write("whisper captioner starting (pid " + server.Id + ")");
                }
            }
            else Log.Write("whisper: script or python missing, expecting an already-running captioner");
        }
        catch (Exception ex) { Log.Write("whisper start failed: " + ex.Message); }
        new Thread(ConnectLoop) { IsBackground = true, Name = "captions" }.Start();
    }

    void ConnectLoop()
    {
        while (!cts.IsCancellationRequested)
        {
            try
            {
                var c = new TcpClient();
                c.Connect("127.0.0.1", cfg.WhisperPort);
                c.NoDelay = true;
                var s = c.GetStream();
                lock (sendLock) { tcp = c; stream = s; }
                Log.Write("captions: connected");
                lock (state.Lock) state.Status = "";
                var reader = new StreamReader(s, Encoding.UTF8);
                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(line);
                        string text = doc.RootElement.GetProperty("text").GetString() ?? "";
                        bool partial = doc.RootElement.TryGetProperty("partial", out var pp) && pp.GetBoolean();
                        if (!partial) Log.Write("caption rx: " + (line.Length > 70 ? line.Substring(0, 70) : line));
                        lock (state.Lock)
                        {
                            // the line being spoken is replaced by each newer version of itself, then by the final text
                            int last = state.Captions.Count - 1;
                            if (last >= 0 && state.Captions[last].Partial) state.Captions.RemoveAt(last);
                            if (text.Length == 0) continue;
                            // the game feed already showed this ghost line word for word: drop Whisper's copy
                            if (text.StartsWith("GHOST") && state.Captions.Any(c => c.FromGame && (DateTime.UtcNow - c.Time).TotalSeconds < 8)) continue;
                            state.Captions.Add(new CaptionLine { Text = prefix + text, Time = DateTime.UtcNow, Partial = partial });
                            if (state.Captions.Count > 60) state.Captions.RemoveAt(0);   // the helper summary reads back up to ~2 min
                        }
                    }
                    catch (Exception ex) { Log.Write("caption parse: " + ex.Message); }
                }
            }
            catch (Exception ex)
            {
                if (!cts.IsCancellationRequested && stream != null) Log.Write("captions: " + ex.Message.Split('\n')[0]);
            }
            lock (sendLock) { stream = null; tcp?.Dispose(); tcp = null; }
            Thread.Sleep(2000);
        }
    }

    /// Called from the audio thread with 16 kHz mono samples.
    public void Feed(ReadOnlySpan<float> samples)
    {
        NetworkStream? s;
        lock (sendLock) s = stream;
        if (s == null) return;
        var bytes = new byte[samples.Length * 2];
        for (int i = 0; i < samples.Length; i++)
        {
            short v = (short)Math.Clamp(samples[i] * 32767f, -32768f, 32767f);
            bytes[i * 2] = (byte)v; bytes[i * 2 + 1] = (byte)(v >> 8);
        }
        try { lock (sendLock) { s.Write(bytes, 0, bytes.Length); } }
        catch { lock (sendLock) { stream = null; } }
    }

    public void Dispose()
    {
        cts.Cancel();
        try { tcp?.Dispose(); } catch { }
        try { if (server != null && !server.HasExited) server.Kill(true); } catch { }
    }
}
