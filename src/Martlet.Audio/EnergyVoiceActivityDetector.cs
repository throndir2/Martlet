using System.Buffers.Binary;

namespace Martlet.Audio;

/// <summary>Hands-free listening thresholds. Sensitivity 0 is the least and 1 the most sensitive.</summary>
public sealed record VoiceActivitySettings
{
    public double Sensitivity { get; init; } = 0.5;
    public TimeSpan EndSilence { get; init; } = TimeSpan.FromMilliseconds(800);
    public TimeSpan MinimumSpeech { get; init; } = TimeSpan.FromMilliseconds(200);
    public TimeSpan PreRoll { get; init; } = TimeSpan.FromMilliseconds(300);
    public TimeSpan Tail { get; init; } = TimeSpan.FromMilliseconds(200);

    public void Validate()
    {
        if (!double.IsFinite(Sensitivity) || Sensitivity is < 0 or > 1 ||
            EndSilence < TimeSpan.FromMilliseconds(200) || EndSilence > TimeSpan.FromSeconds(5) ||
            MinimumSpeech < TimeSpan.FromMilliseconds(60) || MinimumSpeech > TimeSpan.FromSeconds(2) ||
            PreRoll < TimeSpan.Zero || PreRoll > TimeSpan.FromSeconds(1) ||
            Tail < TimeSpan.Zero || Tail > TimeSpan.FromSeconds(1))
            throw new ArgumentOutOfRangeException(nameof(VoiceActivitySettings), "Voice activity settings are out of range.");
    }
}

public enum VoiceActivityTransition { None, SpeechStarted, SpeechEnded }

public readonly record struct SpeechRange(int StartSample, int EndSampleExclusive)
{
    public int Length => EndSampleExclusive - StartSample;
}

/// <summary>
/// Streaming energy voice activity detector for canonical 16 kHz mono PCM16 in 20 ms frames.
/// It tracks an adaptive noise floor and applies onset/hangover hysteresis. It detects sound
/// that behaves like speech energy, not words or speakers; pair it with STT and Voice ID.
/// </summary>
public sealed class EnergyVoiceActivityDetector
{
    public const int FrameSamples = 320;
    public const int FrameBytes = FrameSamples * 2;
    private const int OnsetGapFrames = 2;
    private readonly double margin, absoluteFloor;
    private readonly int minimumSpeechFrames, endSilenceFrames;
    private int candidateStart = -1, candidateFrames, candidateGap, silenceFrames;
    private bool calibrated;

    public EnergyVoiceActivityDetector(VoiceActivitySettings? settings = null)
    {
        Settings = settings ?? new();
        Settings.Validate();
        margin = 18 - 12 * Settings.Sensitivity;
        absoluteFloor = -45 - 15 * Settings.Sensitivity;
        minimumSpeechFrames = Frames(Settings.MinimumSpeech);
        endSilenceFrames = Frames(Settings.EndSilence);
    }

    public VoiceActivitySettings Settings { get; }
    public int FrameIndex { get; private set; }
    public bool Speaking { get; private set; }
    public int SpeechStartFrame { get; private set; } = -1;
    public int SpeechEndFrame { get; private set; } = -1;
    public double NoiseFloorDb { get; private set; } = -90;
    public double LastLevelDb { get; private set; } = -100;

    public VoiceActivityTransition Process(ReadOnlySpan<byte> frame)
    {
        if (frame.Length != FrameBytes) throw new ArgumentException("Supply one 20 ms PCM16 frame (640 bytes).", nameof(frame));
        double squares = 0;
        for (var i = 0; i < FrameBytes; i += 2)
        {
            var sample = BinaryPrimitives.ReadInt16LittleEndian(frame.Slice(i, 2)) / 32768.0;
            squares += sample * sample;
        }
        var level = 10 * Math.Log10(squares / FrameSamples + 1e-10);
        LastLevelDb = level;
        var index = FrameIndex++;
        if (!calibrated)
        {
            NoiseFloorDb = Math.Max(-90, level);
            calibrated = true;
        }

        var transition = VoiceActivityTransition.None;
        if (!Speaking)
        {
            if (level >= Math.Max(NoiseFloorDb + margin, absoluteFloor))
            {
                if (candidateStart < 0) candidateStart = index;
                candidateFrames++;
                candidateGap = 0;
                if (candidateFrames >= minimumSpeechFrames)
                {
                    Speaking = true;
                    SpeechStartFrame = candidateStart;
                    SpeechEndFrame = -1;
                    silenceFrames = 0;
                    ResetCandidate();
                    transition = VoiceActivityTransition.SpeechStarted;
                }
            }
            else if (candidateStart >= 0 && ++candidateGap > OnsetGapFrames) ResetCandidate();
            // Follow quieter rooms immediately and louder ones slowly; freeze during a candidate onset.
            if (candidateStart < 0)
                NoiseFloorDb = Math.Max(-90, level < NoiseFloorDb ? NoiseFloorDb + (level - NoiseFloorDb) * 0.2
                    : NoiseFloorDb + (level - NoiseFloorDb) * 0.005);
            return transition;
        }

        if (level >= Math.Max(NoiseFloorDb + margin * 0.6, absoluteFloor - 3)) silenceFrames = 0;
        else if (++silenceFrames >= endSilenceFrames)
        {
            Speaking = false;
            SpeechEndFrame = index - silenceFrames + 1;
            silenceFrames = 0;
            return VoiceActivityTransition.SpeechEnded;
        }
        return VoiceActivityTransition.None;
    }

    /// <summary>All detected speech segments in a canonical PCM16 buffer, without padding.</summary>
    public static IReadOnlyList<SpeechRange> FindSegments(ReadOnlySpan<byte> pcm, VoiceActivitySettings? settings = null)
    {
        var detector = new EnergyVoiceActivityDetector(settings);
        var segments = new List<SpeechRange>();
        var frames = pcm.Length / FrameBytes;
        for (var i = 0; i < frames; i++)
            if (detector.Process(pcm.Slice(i * FrameBytes, FrameBytes)) == VoiceActivityTransition.SpeechEnded)
                segments.Add(new(detector.SpeechStartFrame * FrameSamples, detector.SpeechEndFrame * FrameSamples));
        if (detector.Speaking) segments.Add(new(detector.SpeechStartFrame * FrameSamples, frames * FrameSamples));
        return segments;
    }

    /// <summary>First speech onset through last speech end, widened by pre-roll and tail; null without speech.</summary>
    public static SpeechRange? FindSpeech(ReadOnlySpan<byte> pcm, VoiceActivitySettings? settings = null)
    {
        settings ??= new();
        var segments = FindSegments(pcm, settings);
        if (segments.Count == 0) return null;
        var total = pcm.Length / 2;
        return new(Math.Max(0, segments[0].StartSample - Samples(settings.PreRoll)),
            Math.Min(total, segments[^1].EndSampleExclusive + Samples(settings.Tail)));
    }

    private void ResetCandidate()
    {
        candidateStart = -1;
        candidateFrames = 0;
        candidateGap = 0;
    }

    public static int Samples(TimeSpan duration) => (int)(duration.Ticks * 16_000 / TimeSpan.TicksPerSecond);
    private static int Frames(TimeSpan duration) => Math.Max(1, (int)Math.Round(duration.TotalMilliseconds / 20));
}
