using System.Runtime.InteropServices;
using System.Text.Json;

namespace Martlet.Sherpa;

/// <summary>What Parakeet heard: the text (empty when nothing was said), the mean and lowest token probability (the model's own
/// estimate, not calibrated accuracy), the language it detected, when it says, and when the first and last tokens were heard
/// (seconds from the start of the audio).</summary>
public sealed record ParakeetTranscript(string Text, double? Confidence, string? Language, double? Minimum = null,
    double? FirstToken = null, double? LastToken = null);

/// <summary>NVIDIA Parakeet TDT 0.6B v3 speech-to-text on this PC's processor through sherpa-onnx, as in AudioTranscriber:
/// 25 European languages detected automatically, punctuated and cased. Audio stays in memory and nothing is sent anywhere.
/// The model (about 1 GB in memory) loads on first use; calls are serialized.</summary>
public sealed class ParakeetEngine : IDisposable
{
    public const int SampleRate = 16_000;
    public const int MaximumSeconds = 60;
    private readonly string root;
    private readonly string? runtime;
    private readonly int threads;
    private readonly object gate = new();
    private IntPtr recognizer;
    private bool disposed;

    /// <summary>Parakeet in <paramref name="root"/> (the data folder's speech directory), run with the sherpa-onnx runtime in
    /// <paramref name="runtimeDirectory"/> (default: Martlet's own folder).</summary>
    public ParakeetEngine(string root, int? threads = null, string? runtimeDirectory = null)
    {
        this.root = Path.GetFullPath(root);
        runtime = runtimeDirectory;
        // ONNX Runtime's worker threads spin: 4 threads is ~1.4x faster than 2 but uses ~2x the CPU (AudioTranscriber's measurement).
        this.threads = threads ?? Math.Clamp(Environment.ProcessorCount / 4, 2, 4);
    }

    /// <summary>Parakeet is downloaded to <paramref name="root"/> and Martlet's folder has the sherpa-onnx runtime.</summary>
    public static bool Installed(string root) =>
        SherpaComponents.RuntimeDirectory() is not null && SherpaComponents.IsParakeetInstalled(root);

    /// <summary>Transcribes 16 kHz mono <paramref name="samples"/> (at most a minute).</summary>
    public ParakeetTranscript Transcribe(float[] samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (samples.Length > MaximumSeconds * SampleRate) throw new ArgumentException("At most a minute of audio can be transcribed at once.", nameof(samples));
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            Open();
            if (samples.Length == 0) return new("", null, null);
            var stream = SherpaNative.SherpaOnnxCreateOfflineStream(recognizer);
            if (stream == IntPtr.Zero) throw new SherpaException("Parakeet could not start transcribing.");
            try
            {
                SherpaNative.SherpaOnnxAcceptWaveformOffline(stream, SampleRate, samples, samples.Length);
                SherpaNative.SherpaOnnxDecodeOfflineStream(recognizer, stream);
                var json = SherpaNative.SherpaOnnxGetOfflineStreamResultAsJson(stream);
                if (json == IntPtr.Zero) throw new SherpaException("Parakeet returned no result.");
                try { return Parse(Marshal.PtrToStringUTF8(json) ?? "{}"); }
                finally { SherpaNative.SherpaOnnxDestroyOfflineStreamResultJson(json); }
            }
            finally { SherpaNative.SherpaOnnxDestroyOfflineStream(stream); }
        }
    }

    /// <summary>Loads the model ahead of the first utterance, so the first reply is not slower.</summary>
    public void Warm()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            Open();
        }
    }

    private void Open()
    {
        if (recognizer != IntPtr.Zero) return;
        if (!SherpaComponents.IsParakeetInstalled(root)) throw new SherpaException("The Parakeet model isn't downloaded yet.");
        SherpaNative.Load(runtime ?? SherpaComponents.RuntimeDirectory() ??
            throw new SherpaException("The speech runtime is missing from Martlet's folder. Reinstall Martlet."));
        var model = SherpaComponents.ParakeetDirectory(root);
        // Offsets of SherpaOnnxOfflineRecognizerConfig (608 bytes): feat{sample_rate@0, feature_dim@4},
        // model{transducer{encoder@8, decoder@16, joiner@24}, tokens@104, num_threads@112, debug@116, provider@120,
        // model_type@128}, decoding_method@528.
        using var config = new NativeConfig(608)
            .Int(0, SampleRate).Int(4, 80)
            .Text(8, Path.Combine(model, "encoder.int8.onnx")).Text(16, Path.Combine(model, "decoder.int8.onnx"))
            .Text(24, Path.Combine(model, "joiner.int8.onnx")).Text(104, Path.Combine(model, "tokens.txt"))
            .Int(112, threads).Text(120, "cpu").Text(128, "nemo_transducer").Text(528, "greedy_search");
        recognizer = SherpaNative.SherpaOnnxCreateOfflineRecognizer(config.Pointer);
        if (recognizer == IntPtr.Zero) throw new SherpaException("The Parakeet model could not be loaded.");
    }

    internal static ParakeetTranscript Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var text = root.TryGetProperty("text", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()?.Trim() ?? "" : "";
        double? confidence = null, minimum = null, first = null, last = null;
        if (root.TryGetProperty("ys_log_probs", out var probabilities) && probabilities.ValueKind == JsonValueKind.Array)
        {
            var values = probabilities.EnumerateArray().Where(p => p.ValueKind == JsonValueKind.Number).Select(p => p.GetDouble())
                .Where(double.IsFinite).Select(Math.Exp).ToArray();
            if (values.Length > 0)
            {
                confidence = Math.Round(values.Average(), 4);
                minimum = Math.Round(values.Min(), 4);
            }
        }
        if (root.TryGetProperty("timestamps", out var times) && times.ValueKind == JsonValueKind.Array)
        {
            var values = times.EnumerateArray().Where(t => t.ValueKind == JsonValueKind.Number).Select(t => t.GetDouble())
                .Where(double.IsFinite).ToArray();
            if (values.Length > 0)
            {
                first = Math.Round(values.Min(), 3);
                last = Math.Round(values.Max(), 3);
            }
        }
        var language = root.TryGetProperty("lang", out var lang) && lang.ValueKind == JsonValueKind.String && lang.GetString() is { Length: > 0 } l ? l : null;
        return new(text, confidence, language, minimum, first, last);
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            if (recognizer != IntPtr.Zero) SherpaNative.SherpaOnnxDestroyOfflineRecognizer(recognizer);
            recognizer = IntPtr.Zero;
        }
    }
}
