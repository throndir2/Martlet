using System.Diagnostics;

namespace Martlet.Sherpa;

/// <summary>Pipecat's Smart Turn v3.2 (BSD 2-Clause, Daily), the end-of-turn judge: from the last 8 s of what someone said (16 kHz),
/// how likely it is that they finished their turn, from how it sounds (intonation, pace, trailing words), not from the words.
/// The int8 CPU model ships in Martlet's <c>turn-detection</c> folder (the build downloads it at a pinned SHA-256) and runs through
/// the ONNX Runtime that ships with sherpa-onnx, in about 20-40 ms on two processor threads.</summary>
public sealed class SmartTurnEngine : IDisposable
{
    public const string Folder = "turn-detection";
    public const string ModelFile = "smart-turn-v3.2-cpu.onnx";
    public const string Name = "Smart Turn v3.2";
    public const int SampleRate = WhisperFeatures.SampleRate;
    private readonly string modelPath, runtimeDirectory;
    private readonly int threads;
    private readonly object gate = new();
    private OrtNative.Session? session;
    private bool disposed;

    public SmartTurnEngine(string modelPath, string runtimeDirectory, int threads = 2)
    {
        this.modelPath = modelPath;
        this.runtimeDirectory = runtimeDirectory;
        this.threads = Math.Clamp(threads, 1, Math.Max(1, Environment.ProcessorCount));
    }

    /// <summary>The bundled model in Martlet's folder (or <paramref name="appDirectory"/>), or null when it isn't there.</summary>
    public static string? ModelPath(string? appDirectory = null)
    {
        var path = Path.Combine(Path.GetFullPath(appDirectory ?? SherpaComponents.AppDirectory), Folder, ModelFile);
        return File.Exists(path) ? path : null;
    }

    /// <summary>The engine for Martlet's own folder, or null when the model or the runtime is missing.</summary>
    public static SmartTurnEngine? Bundled(string? appDirectory = null) =>
        ModelPath(appDirectory) is { } model && SherpaComponents.RuntimeDirectory(appDirectory) is { } runtime ? new(model, runtime) : null;

    /// <summary>How long loading took the first time (zero after).</summary>
    public TimeSpan Warm()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (session is not null) return TimeSpan.Zero;
            var watch = Stopwatch.StartNew();
            OrtNative.Load(runtimeDirectory);
            session = new OrtNative.Session(File.ReadAllBytes(modelPath), threads, "input_features", "logits");
            return watch.Elapsed;
        }
    }

    public bool Loaded { get { lock (gate) return session is not null; } }

    /// <summary>The probability (0-1) that the speaker finished, from 16 kHz mono samples (the end of what they said, with the
    /// short silence after it). Above 0.5 means finished.</summary>
    public double Probability(ReadOnlySpan<float> samples)
    {
        Warm();
        var features = WhisperFeatures.Compute(samples);
        OrtNative.Session current;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            current = session!;
        }
        return current.RunFirst(features, [1, WhisperFeatures.Bands, WhisperFeatures.Frames]);
    }

    public void Dispose()
    {
        lock (gate)
        {
            disposed = true;
            session?.Dispose();
            session = null;
        }
    }
}
