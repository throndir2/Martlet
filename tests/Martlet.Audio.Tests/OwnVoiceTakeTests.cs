using System.Buffers.Binary;
using Martlet.Audio;
using Martlet.Core.Voices;

namespace Martlet.Audio.Tests;

public sealed class OwnVoiceTakeTests
{
    private const int Rate = OwnVoiceTake.SampleRate;

    /// <summary>16 kHz mono PCM16: each part is (seconds, amplitude) of a 220 Hz tone, or quiet for amplitude 0.</summary>
    private static byte[] Pcm(params (double Seconds, double Amplitude)[] parts)
    {
        var samples = parts.Sum(p => (int)(p.Seconds * Rate));
        var pcm = new byte[samples * 2];
        var at = 0;
        foreach (var (seconds, amplitude) in parts)
            for (var i = 0; i < (int)(seconds * Rate); i++, at++)
                BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(at * 2),
                    (short)(Math.Max(amplitude, 0.0005) * 32767 * Math.Sin(2 * Math.PI * 220 * at / Rate)));
        return pcm;
    }

    [Fact]
    public void A_line_keeps_its_speech_without_the_silence_around_it()
    {
        var pcm = Pcm((1.0, 0), (3.0, 0.3), (0.4, 0), (1.0, 0.3), (1.5, 0));

        var take = OwnVoiceTake.From(pcm);

        Assert.NotNull(take);
        // 4.4 seconds from the first word to the last, with 0.2 s kept before and 0.3 s after.
        Assert.InRange(take.DurationMilliseconds, 4_800, 5_000);
        Assert.InRange(take.SpeechSeconds, 3.9, 4.4);
        var wave = PcmWaveInfo.Inspect(take.Wave, SpeakingVoiceLibrary.MaximumAudioBytes);
        Assert.Equal(Rate, wave.SampleRate);
        Assert.Equal(take.DurationMilliseconds, wave.DurationMilliseconds);
        Assert.Contains("audio = omitted", take.ToString());
    }

    [Fact]
    public void A_line_without_a_voice_has_no_take() =>
        Assert.Null(OwnVoiceTake.From(Pcm((3.0, 0))));

    [Fact]
    public void Lines_joined_last_as_long_as_the_voice_keeps_them()
    {
        var take = OwnVoiceTake.From(Pcm((0.5, 0), (2.0, 0.3), (1.0, 0)))!;
        var takes = new[] { take, take, take };

        var joined = SpeakingVoiceRecordings.Join(takes.Select((t, i) => ((ReadOnlyMemory<byte>)t.Wave, $"Line {i + 1}.")).ToArray());

        Assert.InRange(joined.DurationMilliseconds - OwnVoiceTake.JoinedMilliseconds(takes), -2, 2);
        Assert.Equal(3, joined.Clips.Count);
        Assert.Equal("Line 1. Line 2. Line 3.", joined.Transcript);
    }
}
