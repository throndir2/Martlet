namespace Martlet.Audio;

public enum SpeakerVerdict { User, OtherSpeaker, TooShort }

public sealed record SpeakerCheck(SpeakerVerdict Verdict, float Similarity, float LowestPartialSimilarity,
    bool OtherVoiceDetected, TimeSpan SpeechAnalyzed);

public sealed record SpeakerEnrollment(float[] Voiceprint, float Consistency, float SuggestedThreshold, TimeSpan Speech);

/// <summary>Compares speech against an enrolled voiceprint. All audio stays in memory on this PC.</summary>
public static class SpeakerVerifier
{
    public const float DefaultThreshold = 0.75f;
    public static readonly TimeSpan MinimumSpeech = TimeSpan.FromMilliseconds(800);
    // Short 1.6 s partials are noisier than the whole utterance, so a partial must fall clearly lower.
    private const float PartialMargin = 0.12f;
    private const int SegmentPadding = 1600;

    public static SpeakerCheck Check(SpeakerEncoder encoder, IReadOnlyList<float> voiceprint, float threshold,
        ReadOnlySpan<byte> pcm16)
    {
        ArgumentNullException.ThrowIfNull(encoder);
        ArgumentNullException.ThrowIfNull(voiceprint);
        if (!float.IsFinite(threshold) || threshold is <= 0 or >= 1) throw new ArgumentOutOfRangeException(nameof(threshold));
        var speech = ExtractSpeech(pcm16);
        try
        {
            var duration = TimeSpan.FromSeconds(speech.Length / (double)SpeakerEncoder.SampleRate);
            if (duration < MinimumSpeech) return new(SpeakerVerdict.TooShort, 0, 0, false, duration);
            var embedding = encoder.Embed(speech);
            var similarity = SpeakerEncoder.Similarity(embedding.Vector, voiceprint);
            var lowest = embedding.Partials.Min(partial => SpeakerEncoder.Similarity(partial, voiceprint));
            var user = similarity >= threshold;
            var other = !user || embedding.Partials.Count > 1 && lowest < threshold - PartialMargin;
            return new(user ? SpeakerVerdict.User : SpeakerVerdict.OtherSpeaker, similarity, lowest, other, duration);
        }
        finally { Array.Clear(speech); }
    }

    /// <summary>Builds a voiceprint from several separate recordings of the same person.</summary>
    public static SpeakerEnrollment Enroll(SpeakerEncoder encoder, IReadOnlyList<byte[]> recordings)
    {
        ArgumentNullException.ThrowIfNull(encoder);
        if (recordings is not { Count: >= 2 }) throw new ArgumentException("At least two recordings are required.", nameof(recordings));
        var embeddings = new List<IReadOnlyList<float>>();
        var total = 0;
        foreach (var recording in recordings)
        {
            var speech = ExtractSpeech(recording);
            try
            {
                if (speech.Length < SpeakerEncoder.SampleRate * MinimumSpeech.TotalSeconds)
                    throw new ArgumentException("A recording contained too little speech.", nameof(recordings));
                embeddings.Add(encoder.Embed(speech).Vector);
                total += speech.Length;
            }
            finally { Array.Clear(speech); }
        }
        var voiceprint = SpeakerEncoder.Average(embeddings);
        // Leave-one-out agreement avoids each sample confirming an average it contributed to.
        var consistency = embeddings.Select((embedding, index) => SpeakerEncoder.Similarity(embedding,
            SpeakerEncoder.Average(embeddings.Where((_, other) => other != index)))).Min();
        var suggested = Math.Clamp(consistency - 0.12f, 0.6f, 0.8f);
        return new(voiceprint, consistency, suggested, TimeSpan.FromSeconds(total / (double)SpeakerEncoder.SampleRate));
    }

    /// <summary>Concatenates detected speech segments (lightly padded) as float samples; silence is dropped.</summary>
    public static float[] ExtractSpeech(ReadOnlySpan<byte> pcm16)
    {
        var total = pcm16.Length / 2;
        var segments = EnergyVoiceActivityDetector.FindSegments(pcm16);
        var merged = new List<(int Start, int End)>();
        foreach (var segment in segments)
        {
            var start = Math.Max(0, segment.StartSample - SegmentPadding);
            var end = Math.Min(total, segment.EndSampleExclusive + SegmentPadding);
            if (merged.Count > 0 && start <= merged[^1].End) merged[^1] = (merged[^1].Start, Math.Max(end, merged[^1].End));
            else merged.Add((start, end));
        }
        var samples = new float[merged.Sum(range => range.End - range.Start)];
        var offset = 0;
        foreach (var (start, end) in merged)
            for (var i = start; i < end; i++)
                samples[offset++] = System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(pcm16.Slice(i * 2, 2)) / 32768f;
        return samples;
    }
}
