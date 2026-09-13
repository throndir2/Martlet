using System.Buffers.Binary;
using Martlet.Core.Audio;
using Martlet.Core.Contracts;

namespace Martlet.Providers.Tests;

public sealed class BoundedAudioTests
{
    [Theory]
    [InlineData(16000)]
    [InlineData(24000)]
    [InlineData(44100)]
    [InlineData(48000)]
    public async Task Pcm_and_wave_owners_validate_real_samples_copy_input_and_allow_longer_than_one_frame(int rate)
    {
        byte[] expected = ProviderFixtures.Wave(rate / 5, rate);
        byte[] pcm = expected[44..];
        var audio = BoundedWaveAudio.FromPcm(ProviderFixtures.Format(rate), pcm);
        Array.Fill(pcm, (byte)0);
        Assert.Equal(rate / 5, audio.SamplesPerChannel);
        Assert.Equal(TimeSpan.FromMilliseconds(200), audio.Duration);
        Assert.Equal(expected.Length, audio.ByteLength);
        var handler = new RecordingHandler();
        using var adapter = OpenAiTranscriptionAdapter.CreateForFixture(handler, new FixtureCredentials(), new FixtureClock());
        var context = ProviderFixtures.Context();
        var limits = new TranscriptionLimits();
        await adapter.TranscribeAsync(context, "gpt-transcribe", audio, limits, ProviderFixtures.Authorize(context, limits));
        Assert.True(handler.Body.AsSpan().IndexOf(expected) >= 0);

        var owned = BoundedWaveAudio.FromWave(expected);
        Array.Fill(expected, (byte)0);
        Assert.Equal(rate / 5, owned.SamplesPerChannel);
        Assert.Equal(ProviderFixtures.Format(rate), owned.Format);
        await adapter.TranscribeAsync(context, "gpt-transcribe", owned, limits, ProviderFixtures.Authorize(context, limits));
        Assert.True(handler.Body.AsSpan().IndexOf(ProviderFixtures.Wave(rate / 5, rate)) >= 0);
    }

    [Theory]
    [InlineData("riff")]
    [InlineData("wave")]
    [InlineData("riff-size")]
    [InlineData("fmt-size")]
    [InlineData("codec")]
    [InlineData("channels")]
    [InlineData("rate")]
    [InlineData("byte-rate")]
    [InlineData("alignment")]
    [InlineData("bits")]
    [InlineData("data")]
    [InlineData("data-size")]
    [InlineData("truncated")]
    [InlineData("trailing")]
    [InlineData("empty")]
    [InlineData("unaligned")]
    public void Wave_rejects_malformed_sizes_formats_or_hidden_chunks(string fault)
    {
        byte[] wave = ProviderFixtures.Wave();
        int offset = fault switch
        {
            "riff" => 0, "wave" => 8, "riff-size" => 4, "fmt-size" => 16, "codec" => 20,
            "channels" => 22, "rate" => 24, "byte-rate" => 28, "alignment" => 32,
            "bits" => 34, "data" => 36, "data-size" => 40, _ => -1
        };
        if (offset >= 0)
            wave[offset] ^= 0xFF;
        else
        {
            wave = fault switch
            {
                "empty" => [],
                "trailing" => [.. wave, 1, 2],
                _ => wave[..^1]
            };
            if (fault == "unaligned")
            {
                BinaryPrimitives.WriteUInt32LittleEndian(wave.AsSpan(4), (uint)wave.Length - 8);
                BinaryPrimitives.WriteUInt32LittleEndian(wave.AsSpan(40), (uint)wave.Length - 44);
            }
        }
        Assert.Throws<ContractException>(() => BoundedWaveAudio.FromWave(wave));
    }

    [Fact]
    public void Entire_utterance_has_hard_byte_and_duration_limits_not_trusted_metadata()
    {
        var format = ProviderFixtures.Format();
        Assert.Throws<ContractException>(() => BoundedWaveAudio.FromPcm(format, []));
        Assert.Throws<ContractException>(() => BoundedWaveAudio.FromPcm(format, [1]));
        Assert.Throws<ContractException>(() => BoundedWaveAudio.FromPcm(format with { Channels = 2 }, [1, 2, 3, 4]));
        Assert.Throws<ContractException>(() => BoundedWaveAudio.FromPcm(format with { Encoding = (PcmEncoding)42 }, [1, 2]));
        var exactlyNinety = BoundedWaveAudio.FromWave(ProviderFixtures.Wave(16000 * 90));
        Assert.Equal(TimeSpan.FromSeconds(90), exactlyNinety.Duration);
        Assert.Throws<ContractException>(() => BoundedWaveAudio.FromWave(ProviderFixtures.Wave(16000 * 90 + 1)));
        Assert.Throws<ContractException>(() => BoundedWaveAudio.FromPcm(format, new byte[16000 * 90 * 2 + 2]));
        Assert.Throws<ContractException>(() => BoundedWaveAudio.FromWave(new byte[TranscriptionLimits.HardMaxAudioBytes + 1]));
        Assert.Throws<ContractException>(() => BoundedWaveAudio.FromPcm(format, new byte[TranscriptionLimits.HardMaxAudioBytes]));
    }

    [Theory]
    [InlineData("bytes")]
    [InlineData("duration")]
    [InlineData("fractional-duration")]
    public async Task Actual_audio_must_fit_caller_visible_authorized_limits_before_any_send(string fault)
    {
        var audio = fault == "fractional-duration" ? BoundedWaveAudio.FromPcm(ProviderFixtures.Format(44100), [1, 2]) :
            BoundedWaveAudio.FromWave(ProviderFixtures.Wave());
        var limits = new TranscriptionLimits
        {
            MaxAudioBytes = fault == "bytes" ? audio.ByteLength - 1 : TranscriptionLimits.HardMaxAudioBytes,
            MaxAudioDuration = fault switch
            {
                "duration" => TimeSpan.FromMilliseconds(199),
                "fractional-duration" => TimeSpan.FromTicks(226),
                _ => TimeSpan.FromSeconds(90)
            }
        };
        var credentials = new FixtureCredentials();
        var handler = new RecordingHandler();
        using var adapter = OpenAiTranscriptionAdapter.CreateForFixture(handler, credentials, new FixtureClock());
        var context = ProviderFixtures.Context();
        var result = await adapter.TranscribeAsync(context, "gpt-transcribe", audio, limits, ProviderFixtures.Authorize(context, limits));
        Assert.Equal(ProviderFailureCode.AudioLimit, result.Failure!.Code);
        Assert.Equal(0, handler.Calls);
        Assert.Equal(0, credentials.Calls);
    }
}
