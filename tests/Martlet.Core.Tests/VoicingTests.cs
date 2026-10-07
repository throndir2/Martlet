using Martlet.Core.Audio;

namespace Martlet.Core.Tests;

public sealed class VoicingTests
{
    private const int Rate = 24_000;

    // FIXTURE - NOT a voice: a buzz at pitch Hz with falling harmonics, its loudness rising and falling like syllables.
    private static short[] Buzz(double pitch, double seconds = 1)
    {
        var samples = new short[(int)(Rate * seconds)];
        for (var i = 0; i < samples.Length; i++)
        {
            var t = (double)i / Rate;
            var value = 0.0;
            for (var k = 1; k < 20; k++) value += Math.Sin(2 * Math.PI * pitch * k * t) / k;
            samples[i] = (short)(3_000 * value * (0.6 + 0.4 * Math.Sin(2 * Math.PI * 4 * t)));
        }
        return samples;
    }

    // FIXTURE - NOT a whisper: seeded white noise smoothed by a two-pole resonance near 1.5 kHz, as a whisper's breath is
    // shaped by the mouth.
    private static short[] Breath(double seconds = 1)
    {
        var random = new Random(7);
        var samples = new short[(int)(Rate * seconds)];
        double y1 = 0, y2 = 0, radius = 0.97, angle = 2 * Math.PI * 1_500 / Rate;
        for (var i = 0; i < samples.Length; i++)
        {
            var y = random.NextDouble() - 0.5 + 2 * radius * Math.Cos(angle) * y1 - radius * radius * y2;
            y2 = y1;
            y1 = y;
            samples[i] = (short)Math.Clamp(1_000 * y, short.MinValue, short.MaxValue);
        }
        return samples;
    }

    [Theory]
    [InlineData(110)]
    [InlineData(220)]
    [InlineData(330)]
    public void ABuzzAtAVoicesPitchIsVoiced(double pitch) =>
        Assert.True(Voicing.VoicedShare(Buzz(pitch), Rate) >= 0.95);

    [Fact]
    public void BreathIsNotVoiced() => Assert.True(Voicing.VoicedShare(Breath(), Rate) <= 0.05);

    [Fact]
    public void OnlyTheVoicedPartOfSpeechCounts()
    {
        var mixed = Buzz(180).Concat(Breath()).ToArray();
        Assert.InRange(Voicing.VoicedShare(mixed, Rate)!.Value, 0.4, 0.6);
    }

    [Fact]
    public void SilenceAndTooLittleAudioHaveNoShare()
    {
        Assert.Null(Voicing.VoicedShare(new short[Rate], Rate));
        Assert.Null(Voicing.VoicedShare(Buzz(200, 0.02), Rate));
    }
}
