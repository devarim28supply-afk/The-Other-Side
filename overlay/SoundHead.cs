using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace PhasmoSound;

/// Sound recognizer trained on the game's own sound files (PhasmoTrain\train_cnn.py): a small log-mel
/// CNN that hears the same 16 kHz window YAMNet gets and gives one probability per game sound.
/// Labels starting with "_" (background, your equipment) let it say "nothing worth showing"; never displayed.
public sealed class SoundHead : IDisposable
{
    readonly InferenceSession session;
    readonly string[] labels;
    public int Count => labels.Length;

    public SoundHead(string onnxPath, string labelsPath)
    {
        labels = JsonSerializer.Deserialize<string[]>(File.ReadAllText(labelsPath))!;
        session = new InferenceSession(onnxPath, new SessionOptions { IntraOpNumThreads = 2, GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL });
        var sw = System.Diagnostics.Stopwatch.StartNew();
        foreach (var n in new[] { 15600, 24000 }) Predict(new float[n]);
        Log.Write($"trained sound head: {labels.Length} sounds, warm-up {sw.ElapsedMilliseconds} ms ({string.Join(", ", labels.Where(l => !l.StartsWith('_')))})");
    }

    /// Best label and its probability for a mono 16 kHz window.
    public (string label, float prob) Predict(float[] wave)
    {
        var t = new DenseTensor<float>(wave, new[] { 1, wave.Length });
        using var res = session.Run(new[] { NamedOnnxValue.CreateFromTensor("waveform", t) });
        var p = res.First().AsTensor<float>();
        int best = 0;
        for (int c = 1; c < labels.Length; c++) if (p[0, c] > p[0, best]) best = c;
        return (labels[best], p[0, best]);
    }

    public void Dispose() => session.Dispose();
}
