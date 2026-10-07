using System.Runtime.InteropServices;

namespace Martlet.Sherpa;

/// <summary>One sound the tagger heard: its AudioSet display name ("Music", "Laughter"), the label's index and its score (0 to 1).</summary>
public sealed record SoundEvent(string Name, int Index, float Probability);

/// <summary>The small sound tagger on this PC's processor, the sound digest's judge when the Thinking pool has no model that
/// hears: the sherpa-onnx audio tagging API with the Zipformer small AudioSet model (k2-fsa icefall, Apache-2.0; 527 AudioSet
/// labels) that ships in Martlet's folder (<see cref="Folder"/>). Audio stays in memory. One thread; calls are serialized; the
/// model loads on first use.</summary>
public sealed class SoundTagger : IDisposable
{
    public const int SampleRate = 16_000;
    /// <summary>The longest clip tagged at once.</summary>
    public const int MaximumSeconds = 30;
    /// <summary>The folder in Martlet's own folder with the bundled sound tagging model and its labels.</summary>
    public const string Folder = "sound-tagging";
    internal const string ModelFile = "model.int8.onnx";
    internal const string LabelsFile = "class_labels_indices.csv";
    private readonly string? appDirectory;
    private readonly object gate = new();
    private IntPtr tagger;
    private bool disposed;

    /// <summary>Uses the runtime and the sound tagging model in Martlet's folder (<paramref name="appDirectory"/>, by default the
    /// running application's).</summary>
    public SoundTagger(string? appDirectory = null) => this.appDirectory = appDirectory is null ? null : Path.GetFullPath(appDirectory);

    /// <summary>The bundled model's folder.</summary>
    public static string ModelDirectory(string? appDirectory = null) =>
        Path.Combine(Path.GetFullPath(appDirectory ?? SherpaComponents.AppDirectory), Folder);

    /// <summary>The runtime, the model and its labels are part of this Martlet.</summary>
    public static bool Included(string? appDirectory = null)
    {
        var models = ModelDirectory(appDirectory);
        return SherpaComponents.RuntimeDirectory(appDirectory) is not null && File.Exists(Path.Combine(models, ModelFile)) &&
            File.Exists(Path.Combine(models, LabelsFile));
    }

    /// <summary>The <paramref name="top"/> sounds heard in <paramref name="samples"/> (16 kHz mono, -1 to 1), highest score first.</summary>
    public IReadOnlyList<SoundEvent> Tag(float[] samples, int top = 12)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (samples.Length > MaximumSeconds * SampleRate) throw new ArgumentException("At most 30 seconds of audio can be tagged at once.", nameof(samples));
        ArgumentOutOfRangeException.ThrowIfLessThan(top, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(top, 50);
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            Open();
            if (samples.Length == 0) return [];
            var stream = SherpaNative.SherpaOnnxAudioTaggingCreateOfflineStream(tagger);
            if (stream == IntPtr.Zero) throw new SherpaException("The sound tagger could not start.");
            try
            {
                SherpaNative.SherpaOnnxAcceptWaveformOffline(stream, SampleRate, samples, samples.Length);
                var results = SherpaNative.SherpaOnnxAudioTaggingCompute(tagger, stream, top);
                if (results == IntPtr.Zero) return [];
                try
                {
                    // A null-terminated array of SherpaOnnxAudioEvent pointers: name@0, index@8, prob@12.
                    var events = new List<SoundEvent>();
                    for (var i = 0; i <= top; i++)
                    {
                        var item = Marshal.ReadIntPtr(results, i * IntPtr.Size);
                        if (item == IntPtr.Zero) break;
                        var name = Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(item, 0)) ?? "";
                        var index = Marshal.ReadInt32(item, 8);
                        var probability = BitConverter.Int32BitsToSingle(Marshal.ReadInt32(item, 12));
                        if (name.Length > 0 && float.IsFinite(probability)) events.Add(new(name, index, probability));
                    }
                    return events.OrderByDescending(e => e.Probability).ToArray();
                }
                finally { SherpaNative.SherpaOnnxAudioTaggingFreeResults(results); }
            }
            finally { SherpaNative.SherpaOnnxDestroyOfflineStream(stream); }
        }
    }

    private void Open()
    {
        if (tagger != IntPtr.Zero) return;
        if (!Included(appDirectory) || SherpaComponents.RuntimeDirectory(appDirectory) is not { } runtime)
            throw new SherpaException("The sound tagger's files are missing from Martlet's folder. Reinstall Martlet.");
        SherpaNative.Load(runtime);
        var models = ModelDirectory(appDirectory);
        // SherpaOnnxAudioTaggingConfig (48 bytes): model{zipformer{model@0}, ced@8, num_threads@16, debug@20, provider@24},
        // labels@32, top_k@40.
        using var config = new NativeConfig(48).Text(0, Path.Combine(models, ModelFile)).Int(16, 1).Text(24, "cpu")
            .Text(32, Path.Combine(models, LabelsFile)).Int(40, 12);
        tagger = SherpaNative.SherpaOnnxCreateAudioTagging(config.Pointer);
        if (tagger == IntPtr.Zero) throw new SherpaException("The sound tagging model could not be loaded.");
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            if (tagger != IntPtr.Zero) SherpaNative.SherpaOnnxDestroyAudioTagging(tagger);
            tagger = IntPtr.Zero;
        }
    }
}
