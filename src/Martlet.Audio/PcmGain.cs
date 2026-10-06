using System.Buffers.Binary;

namespace Martlet.Audio;

/// <summary>Scales signed 16-bit little-endian PCM by a volume from 0 (silent) to 1 (unchanged), the way Martlet's voice
/// volume turns the character's speech and singing down without touching Windows' own volume.</summary>
public static class PcmGain
{
    public const double Full = 1.0;

    /// <summary>A volume clamped to 0 through 1; anything that isn't a number is full volume.</summary>
    public static double Clamp(double volume) => double.IsFinite(volume) ? Math.Clamp(volume, 0, Full) : Full;

    /// <summary>Scales the whole 16-bit samples in <paramref name="pcm"/> in place. Full volume leaves them unchanged.</summary>
    public static void Apply(Span<byte> pcm, double volume)
    {
        volume = Clamp(volume);
        if (volume >= Full) return;
        for (var i = 0; i + 1 < pcm.Length; i += 2)
        {
            var sample = BinaryPrimitives.ReadInt16LittleEndian(pcm[i..]);
            BinaryPrimitives.WriteInt16LittleEndian(pcm[i..], (short)Math.Round(sample * volume));
        }
    }

    /// <summary>Scales <paramref name="samples"/> in place. Full volume leaves them unchanged.</summary>
    public static void Apply(Span<short> samples, double volume)
    {
        volume = Clamp(volume);
        if (volume >= Full) return;
        for (var i = 0; i < samples.Length; i++) samples[i] = (short)Math.Round(samples[i] * volume);
    }
}
