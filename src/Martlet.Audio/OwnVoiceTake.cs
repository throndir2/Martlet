using System.Buffers.Binary;
using Martlet.Core.Voices;

namespace Martlet.Audio;

/// <summary>One line the owner read out loud for Record your own voice, as the voice keeps it: the speech in what the
/// microphone recorded (16 kHz mono PCM16), without the silence before and after it, as a mono 16-bit WAV.
/// <see cref="SpeechSeconds"/> is how much of it is voice.</summary>
public sealed record OwnVoiceTake(byte[] Wave, int DurationMilliseconds, double SpeechSeconds)
{
    public const int SampleRate = 16_000;

    /// <summary>A line with less voice than this was not read: the microphone heard a word or a noise.</summary>
    public const double MinimumSpeechSeconds = 1.5;

    /// <summary>How the speech is found: a little of the room before it and after it is kept, so no first or last sound
    /// is cut.</summary>
    public static VoiceActivitySettings Trim { get; } = new()
    {
        PreRoll = TimeSpan.FromMilliseconds(200),
        Tail = TimeSpan.FromMilliseconds(300)
    };

    /// <summary>The speech in <paramref name="pcm"/> (canonical 16 kHz mono PCM16), or null when it holds none.</summary>
    public static OwnVoiceTake? From(ReadOnlySpan<byte> pcm)
    {
        var segments = EnergyVoiceActivityDetector.FindSegments(pcm, Trim);
        if (segments.Count == 0) return null;
        var total = pcm.Length / 2;
        var start = Math.Max(0, segments[0].StartSample - EnergyVoiceActivityDetector.Samples(Trim.PreRoll));
        var end = Math.Min(total, segments[^1].EndSampleExclusive + EnergyVoiceActivityDetector.Samples(Trim.Tail));
        var samples = new short[end - start];
        for (var i = 0; i < samples.Length; i++)
            samples[i] = BinaryPrimitives.ReadInt16LittleEndian(pcm.Slice((start + i) * 2, 2));
        return new(PcmWaveInfo.Write(SampleRate, samples), (int)Math.Ceiling(samples.Length * 1000d / SampleRate),
            segments.Sum(s => s.Length) / (double)SampleRate);
    }

    /// <summary>How long <paramref name="takes"/> last joined into one voice, with the pause Martlet puts between them.</summary>
    public static int JoinedMilliseconds(IReadOnlyCollection<OwnVoiceTake> takes) =>
        takes.Sum(t => t.DurationMilliseconds) + Math.Max(0, takes.Count - 1) * SpeakingVoiceLibrary.ClipPauseMilliseconds;

    public override string ToString() =>
        $"{nameof(OwnVoiceTake)} {{ DurationMilliseconds = {DurationMilliseconds}, SpeechSeconds = {SpeechSeconds:0.##}, audio = omitted }}";
}
