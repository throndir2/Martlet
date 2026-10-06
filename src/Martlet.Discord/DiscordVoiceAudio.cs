using Concentus.Enums;
using Concentus.Structs;

namespace Martlet.Discord;

/// <summary>Discord voice audio formats. Discord sends and takes Opus at 48 kHz stereo in 20 ms frames; Martlet's speech-to-text
/// takes 16 kHz mono PCM16 and its voices speak 16 or 24 kHz mono PCM16.</summary>
public static class DiscordVoiceAudio
{
    public const int DiscordRate = 48_000;
    public const int DiscordChannels = 2;
    public const int FrameMilliseconds = 20;
    /// <summary>Samples per channel in one 20 ms Discord frame.</summary>
    public const int FrameSamples = DiscordRate / 1000 * FrameMilliseconds;
    public const int SpeechRate = 16_000;
    /// <summary>Samples in one 20 ms frame of speech-to-text audio.</summary>
    public const int SpeechFrameSamples = SpeechRate / 1000 * FrameMilliseconds;
    /// <summary>Discord's Opus silence frame, sent after speaking so receivers don't interpolate.</summary>
    public static ReadOnlySpan<byte> SilenceFrame => [0xF8, 0xFF, 0xFE];

    public static byte[] ToBytes(ReadOnlySpan<short> samples)
    {
        var bytes = new byte[samples.Length * 2];
        System.Runtime.InteropServices.MemoryMarshal.AsBytes(samples).CopyTo(bytes);
        return bytes;
    }

    public static short[] ToSamples(ReadOnlySpan<byte> pcm) =>
        System.Runtime.InteropServices.MemoryMarshal.Cast<byte, short>(pcm[..(pcm.Length & ~1)]).ToArray();
}

/// <summary>48 kHz stereo (interleaved) to 16 kHz mono, streaming: mixes the channels, low-passes below 8 kHz with a windowed-sinc
/// filter and keeps every third sample. One per speaker, so the filter's history stays with that speaker's audio.</summary>
public sealed class DiscordDownsampler
{
    private const int Factor = DiscordVoiceAudio.DiscordRate / DiscordVoiceAudio.SpeechRate;
    private const int Taps = 48;
    private static readonly float[] Filter = MakeFilter();
    private readonly float[] history = new float[Taps];
    private int phase;

    /// <summary>Converts <paramref name="stereo"/> (interleaved 48 kHz) and returns the 16 kHz mono samples it produced.</summary>
    public short[] Convert(ReadOnlySpan<short> stereo)
    {
        var frames = stereo.Length / 2;
        var output = new List<short>(frames / Factor + 1);
        for (var i = 0; i < frames; i++)
        {
            Array.Copy(history, 1, history, 0, Taps - 1);
            history[Taps - 1] = (stereo[2 * i] + stereo[2 * i + 1]) * 0.5f;
            if (++phase < Factor) continue;
            phase = 0;
            var sum = 0f;
            for (var t = 0; t < Taps; t++) sum += history[t] * Filter[t];
            output.Add((short)Math.Clamp(MathF.Round(sum), short.MinValue, short.MaxValue));
        }
        return [.. output];
    }

    public void Reset()
    {
        Array.Clear(history);
        phase = 0;
    }

    // Cutoff 7 kHz at 48 kHz (just under 16 kHz's Nyquist), Blackman window, unity gain.
    private static float[] MakeFilter()
    {
        var filter = new float[Taps];
        var cutoff = 7_000.0 / DiscordVoiceAudio.DiscordRate;
        var sum = 0.0;
        for (var i = 0; i < Taps; i++)
        {
            var n = i - (Taps - 1) / 2.0;
            var sinc = n == 0 ? 2 * cutoff : Math.Sin(2 * Math.PI * cutoff * n) / (Math.PI * n);
            var window = 0.42 - 0.5 * Math.Cos(2 * Math.PI * i / (Taps - 1)) + 0.08 * Math.Cos(4 * Math.PI * i / (Taps - 1));
            filter[i] = (float)(sinc * window);
            sum += filter[i];
        }
        for (var i = 0; i < Taps; i++) filter[i] = (float)(filter[i] / sum);
        return filter;
    }
}

/// <summary>Mono PCM16 at any rate (Martlet's voices speak 16 or 24 kHz) to 48 kHz stereo (interleaved), streaming, by linear
/// interpolation. Opus then band-limits it.</summary>
public sealed class DiscordUpsampler(int sourceRate)
{
    private const long Out = DiscordVoiceAudio.DiscordRate;
    // Position in source samples times 48000 (exact): 0 is the last sample of the previous chunk, k * 48000 is mono[k - 1].
    private long position;
    private short previous;
    private bool started;

    public int SourceRate { get; } = sourceRate > 0 ? sourceRate : throw new ArgumentOutOfRangeException(nameof(sourceRate));

    public short[] Convert(ReadOnlySpan<short> mono)
    {
        if (mono.Length == 0) return [];
        var output = new List<short>((int)(mono.Length * Out / SourceRate * 2) + 4);
        if (!started) { previous = mono[0]; started = true; }
        var end = mono.Length * Out;
        while (position <= end)
        {
            var index = (int)(position / Out);
            var fraction = position % Out / (double)Out;
            var a = index == 0 ? previous : mono[index - 1];
            var b = index >= mono.Length ? a : mono[index];
            var value = (short)Math.Round(a + (b - a) * fraction);
            output.Add(value);
            output.Add(value);
            position += SourceRate;
        }
        position -= end;
        previous = mono[^1];
        return [.. output];
    }
}

/// <summary>Decodes one speaker's Opus frames to 48 kHz stereo PCM16 with Concentus' managed decoder (never a native opus).</summary>
public sealed class DiscordOpusDecoder
{
    // Concentus' managed decoder directly: its factory may load a native opus.dll found on this PC instead.
#pragma warning disable CS0618
    private readonly OpusDecoder decoder = new(DiscordVoiceAudio.DiscordRate, DiscordVoiceAudio.DiscordChannels);
#pragma warning restore CS0618
    private readonly short[] buffer = new short[DiscordVoiceAudio.FrameSamples * 6 * DiscordVoiceAudio.DiscordChannels];

    /// <summary>The decoded interleaved samples of <paramref name="frame"/>; an empty frame conceals one lost 20 ms frame.</summary>
    public ReadOnlySpan<short> Decode(ReadOnlySpan<byte> frame)
    {
        var samples = frame.IsEmpty
            ? decoder.Decode([], buffer, DiscordVoiceAudio.FrameSamples, false)
            : decoder.Decode(frame, buffer, buffer.Length / DiscordVoiceAudio.DiscordChannels, false);
        return buffer.AsSpan(0, samples * DiscordVoiceAudio.DiscordChannels);
    }
}

/// <summary>Encodes 48 kHz stereo PCM16 into 20 ms Opus frames for Discord with Concentus' managed encoder.</summary>
public sealed class DiscordOpusEncoder
{
#pragma warning disable CS0618
    private readonly OpusEncoder encoder = new(DiscordVoiceAudio.DiscordRate, DiscordVoiceAudio.DiscordChannels,
        OpusApplication.OPUS_APPLICATION_VOIP) { Bitrate = 64_000 };
#pragma warning restore CS0618
    private readonly byte[] buffer = new byte[4000];

    /// <summary>Encodes exactly one 20 ms frame (<see cref="DiscordVoiceAudio.FrameSamples"/> per channel, interleaved).</summary>
    public byte[] Encode(ReadOnlySpan<short> frame)
    {
        if (frame.Length != DiscordVoiceAudio.FrameSamples * DiscordVoiceAudio.DiscordChannels)
            throw new ArgumentException("Encode one 20 ms stereo frame at a time.", nameof(frame));
        var length = encoder.Encode(frame, DiscordVoiceAudio.FrameSamples, buffer, buffer.Length);
        return buffer.AsSpan(0, length).ToArray();
    }
}
