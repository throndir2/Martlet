using System.Runtime.InteropServices;

namespace Martlet.Sherpa;

/// <summary>One person heard in an utterance: a normalized 256-number voiceprint from their clean speech (no one else
/// talking over them), how much clean speech it came from and where it was (seconds from the start).</summary>
public sealed record HeardSpeaker(int Local, float[] Voiceprint, double CleanSeconds, double Start, double End);

/// <summary>Who was heard in one utterance. <see cref="Speakers"/> are the people with enough clean speech to recognize,
/// most speech first; <see cref="Whole"/> is a voiceprint of all the speech, for an utterance too short or mixed for that.
/// <see cref="Overlap"/> means people talked over each other.</summary>
public sealed record SpeakerAnalysis(IReadOnlyList<HeardSpeaker> Speakers, int Voices, bool Overlap, float[]? Whole, double SpeechSeconds);

/// <summary>Speaker recognition with sherpa-onnx on this PC, the same pipeline as AudioTranscriber: pyannote segmentation finds
/// who spoke when (up to three people per ten seconds, clustering across longer utterances), then WeSpeaker voiceprints are
/// taken only from each person's clean stretches, so overlapping speech never blends two people into one voiceprint.
/// Audio stays in memory. Calls are serialized; the native models load on first use.</summary>
public sealed class SpeakerEngine : IDisposable
{
    public const int SampleRate = 16_000;
    public const double MinimumCleanSeconds = 2.0;
    public const double MinimumWholeSeconds = 1.0;
    public const int MaximumSeconds = 60;
    private const double EdgeTrim = 0.12, MinimumFragment = 1.5, MaximumFragment = 8.0, Consistency = 0.45;
    private readonly string? appDirectory;
    private readonly object gate = new();
    private IntPtr extractor, diarizer;
    private bool disposed;

    /// <summary>Uses the runtime and voice models in Martlet's folder (<paramref name="appDirectory"/>, by default the running
    /// application's).</summary>
    public SpeakerEngine(string? appDirectory = null) => this.appDirectory = appDirectory is null ? null : Path.GetFullPath(appDirectory);

    /// <summary>The runtime and voice models are part of this Martlet.</summary>
    public static bool Included(string? appDirectory = null) => SherpaComponents.VoiceRecognitionIncluded(appDirectory);

    /// <summary>Who spoke in <paramref name="samples"/> (16 kHz mono, at most a minute).</summary>
    public SpeakerAnalysis Analyze(float[] samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (samples.Length > MaximumSeconds * SampleRate) throw new ArgumentException("At most a minute of audio can be analyzed at once.", nameof(samples));
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            Open();
            var duration = samples.Length / (double)SampleRate;
            var turns = Segment(samples, duration);
            var speakers = new List<HeardSpeaker>();
            foreach (var local in turns.Select(t => t.Speaker).Distinct().Order())
            {
                var vectors = new List<float[]>();
                double seconds = 0, from = double.MaxValue, to = 0;
                foreach (var (start, end) in Clean(turns, local).Take(3))
                {
                    var first = (int)Math.Ceiling(start * SampleRate);
                    var last = Math.Min(samples.Length, Math.Min((int)Math.Floor(end * SampleRate), first + (int)(MaximumFragment * SampleRate)));
                    if (last - first < MinimumFragment * SampleRate) continue;
                    if (Embed(samples.AsSpan(first, last - first).ToArray()) is not { } vector) continue;
                    vectors.Add(vector);
                    seconds += (last - first) / (double)SampleRate;
                    from = Math.Min(from, first / (double)SampleRate);
                    to = Math.Max(to, last / (double)SampleRate);
                }
                // Too little to trust, or fragments that disagree (a mixed cluster) never reach the voice list.
                if (seconds < MinimumCleanSeconds || vectors.Count == 0 ||
                    vectors.Any(a => vectors.Any(b => Cosine(a, b) < Consistency))) continue;
                var average = new float[vectors[0].Length];
                foreach (var vector in vectors)
                    for (var i = 0; i < average.Length; i++) average[i] += vector[i];
                speakers.Add(new(local, Normalize(average), Math.Round(seconds, 2), Math.Round(from, 2), Math.Round(to, 2)));
            }
            var speech = turns.Select(t => (t.Start, t.End)).Distinct().Sum(t => t.End - t.Start);
            var overlap = turns.Any(a => turns.Any(b => a.Speaker != b.Speaker && Math.Min(a.End, b.End) - Math.Max(a.Start, b.Start) > 0.2));
            float[]? whole = null;
            if (speakers.Count == 0 && duration >= MinimumWholeSeconds) whole = Embed(samples);
            return new(speakers.OrderByDescending(s => s.CleanSeconds).ToArray(), turns.Select(t => t.Speaker).Distinct().Count(),
                overlap, whole, Math.Round(Math.Min(speech, duration), 2));
        }
    }

    /// <summary>One voiceprint of all of <paramref name="samples"/> (no segmentation), or null when it is too short.</summary>
    public float[]? Voiceprint(float[] samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (samples.Length > MaximumSeconds * SampleRate) throw new ArgumentException("At most a minute of audio can be analyzed at once.", nameof(samples));
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            Open();
            return samples.Length >= MinimumWholeSeconds * SampleRate ? Embed(samples) : null;
        }
    }

    private void Open()
    {
        if (extractor != IntPtr.Zero && diarizer != IntPtr.Zero) return;
        if (!Included(appDirectory) || SherpaComponents.RuntimeDirectory(appDirectory) is not { } runtime)
            throw new SherpaException("Voice recognition files are missing from Martlet's folder. Reinstall Martlet.");
        SherpaNative.Load(runtime);
        var models = SherpaComponents.VoiceModelsDirectory(appDirectory);
        var threads = Math.Clamp(Environment.ProcessorCount / 4, 1, 4);
        if (extractor == IntPtr.Zero)
        {
            using var config = new NativeConfig(24).Text(0, SherpaComponents.SpeakerModelPath(models)).Int(8, threads).Text(16, "cpu");
            extractor = SherpaNative.SherpaOnnxCreateSpeakerEmbeddingExtractor(config.Pointer);
            if (extractor == IntPtr.Zero) throw new SherpaException("The voice model could not be loaded.");
            if (SherpaNative.SherpaOnnxSpeakerEmbeddingExtractorDim(extractor) != 256)
                throw new SherpaException("The voice model has an unexpected format.");
        }
        if (diarizer == IntPtr.Zero)
        {
            // Offsets of SherpaOnnxOfflineSpeakerDiarizationConfig (80 bytes): segmentation{pyannote{model@0, shift@8},
            // threads@16, debug@20, provider@24}, embedding{model@32, threads@40, debug@44, provider@48},
            // clustering{num_clusters@56, threshold@60, confidence@64}, min_duration_on@68, min_duration_off@72.
            using var config = new NativeConfig(80)
                .Text(0, SherpaComponents.SegmentationModelPath(models)).Int(16, threads).Text(24, "cpu")
                .Text(32, SherpaComponents.SpeakerModelPath(models)).Int(40, threads).Text(48, "cpu")
                .Int(56, -1).Float(60, 0.5f);
            diarizer = SherpaNative.SherpaOnnxCreateOfflineSpeakerDiarization(config.Pointer);
            if (diarizer == IntPtr.Zero) throw new SherpaException("The speaker segmentation model could not be loaded.");
            if (SherpaNative.SherpaOnnxOfflineSpeakerDiarizationGetSampleRate(diarizer) != SampleRate)
                throw new SherpaException("The speaker segmentation model has an unexpected format.");
        }
    }

    private (double Start, double End, int Speaker)[] Segment(float[] samples, double duration)
    {
        if (samples.Length == 0) return [];
        var result = SherpaNative.SherpaOnnxOfflineSpeakerDiarizationProcess(diarizer, samples, samples.Length);
        if (result == IntPtr.Zero) return [];
        try
        {
            var count = SherpaNative.SherpaOnnxOfflineSpeakerDiarizationResultGetNumSegments(result);
            if (count <= 0) return [];
            if (count > 4096) throw new SherpaException("Speaker segmentation returned too many turns.");
            var segments = SherpaNative.SherpaOnnxOfflineSpeakerDiarizationResultSortByStartTime(result);
            if (segments == IntPtr.Zero) return [];
            try
            {
                var turns = new List<(double, double, int)>(count);
                for (var i = 0; i < count; i++)
                {
                    // SherpaOnnxOfflineSpeakerDiarizationSegment: start@0, end@4, speaker@8, confidence@12 (16 bytes).
                    var start = BitConverter.Int32BitsToSingle(Marshal.ReadInt32(segments, i * 16));
                    var end = BitConverter.Int32BitsToSingle(Marshal.ReadInt32(segments, i * 16 + 4));
                    var speaker = Marshal.ReadInt32(segments, i * 16 + 8);
                    if (!float.IsFinite(start) || !float.IsFinite(end) || speaker < 0) continue;
                    var from = Math.Max(0, start);
                    var to = Math.Min(duration, end);
                    if (to > from) turns.Add((from, to, speaker));
                }
                return turns.Distinct().ToArray();
            }
            finally { SherpaNative.SherpaOnnxOfflineSpeakerDiarizationDestroySegment(segments); }
        }
        finally { SherpaNative.SherpaOnnxOfflineSpeakerDiarizationDestroyResult(result); }
    }

    /// <summary>A person's stretches with nobody else talking, trimmed at the edges, longest first.</summary>
    internal static IReadOnlyList<(double Start, double End)> Clean(IReadOnlyList<(double Start, double End, int Speaker)> turns, int speaker)
    {
        var merged = new List<(double Start, double End)>();
        foreach (var turn in turns.Where(t => t.Speaker == speaker).OrderBy(t => t.Start))
        {
            if (merged.Count > 0 && merged[^1].End >= turn.Start) merged[^1] = (merged[^1].Start, Math.Max(merged[^1].End, turn.End));
            else merged.Add((turn.Start, turn.End));
        }
        var result = new List<(double Start, double End)>();
        foreach (var span in merged)
        {
            var spans = new List<(double Start, double End)> { span };
            foreach (var other in turns.Where(t => t.Speaker != speaker))
            {
                var next = new List<(double, double)>();
                foreach (var (start, end) in spans)
                {
                    if (other.End <= start || other.Start >= end) next.Add((start, end));
                    else
                    {
                        if (other.Start > start) next.Add((start, Math.Min(end, other.Start)));
                        if (other.End < end) next.Add((Math.Max(start, other.End), end));
                    }
                }
                spans = next;
            }
            result.AddRange(spans.Where(s => s.End - s.Start - 2 * EdgeTrim >= MinimumFragment)
                .Select(s => (s.Start + EdgeTrim, s.End - EdgeTrim)));
        }
        return result.OrderByDescending(s => s.End - s.Start).ThenBy(s => s.Start).ToArray();
    }

    private float[]? Embed(float[] samples)
    {
        var stream = SherpaNative.SherpaOnnxSpeakerEmbeddingExtractorCreateStream(extractor);
        if (stream == IntPtr.Zero) return null;
        try
        {
            SherpaNative.SherpaOnnxOnlineStreamAcceptWaveform(stream, SampleRate, samples, samples.Length);
            SherpaNative.SherpaOnnxOnlineStreamInputFinished(stream);
            if (SherpaNative.SherpaOnnxSpeakerEmbeddingExtractorIsReady(extractor, stream) == 0) return null;
            var pointer = SherpaNative.SherpaOnnxSpeakerEmbeddingExtractorComputeEmbedding(extractor, stream);
            if (pointer == IntPtr.Zero) return null;
            try
            {
                var vector = new float[256];
                Marshal.Copy(pointer, vector, 0, vector.Length);
                return vector.All(float.IsFinite) && vector.Any(v => v != 0) ? Normalize(vector) : null;
            }
            finally { SherpaNative.SherpaOnnxSpeakerEmbeddingExtractorDestroyEmbedding(pointer); }
        }
        finally { SherpaNative.SherpaOnnxDestroyOnlineStream(stream); }
    }

    private static float[] Normalize(float[] vector)
    {
        var norm = Math.Sqrt(vector.Sum(x => (double)x * x));
        return norm < 1e-8 ? vector : vector.Select(x => (float)(x / norm)).ToArray();
    }

    private static double Cosine(float[] a, float[] b)
    {
        double sum = 0;
        for (var i = 0; i < a.Length; i++) sum += (double)a[i] * b[i];
        return sum;
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            if (extractor != IntPtr.Zero) SherpaNative.SherpaOnnxDestroySpeakerEmbeddingExtractor(extractor);
            if (diarizer != IntPtr.Zero) SherpaNative.SherpaOnnxDestroyOfflineSpeakerDiarization(diarizer);
            extractor = diarizer = IntPtr.Zero;
        }
    }
}

public sealed class SherpaException(string message, Exception? inner = null) : Exception(message, inner);
