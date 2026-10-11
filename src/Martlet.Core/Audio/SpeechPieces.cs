using System.Buffers.Binary;

namespace Martlet.Core.Audio;

/// <summary>Cuts a long recording into pieces that one speech-to-text request takes, each cut at the quietest moment near its
/// end (a pause between words where there is one), so talking is never limited by how much one request hears.</summary>
public static class SpeechPieces
{
    /// <summary>How far back from a piece's longest end the quietest moment is looked for.</summary>
    public static readonly TimeSpan SearchBack = TimeSpan.FromSeconds(5);

    /// <summary>The pieces of <paramref name="pcm16"/> (mono 16-bit little-endian PCM at <paramref name="sampleRate"/>) as
    /// (start, length) in bytes, in order, together covering every byte, none longer than <paramref name="longest"/>. A
    /// recording that fits is one piece.</summary>
    public static IReadOnlyList<(int Start, int Length)> Split(ReadOnlySpan<byte> pcm16, int sampleRate, TimeSpan longest)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 8_000);
        if (longest < TimeSpan.FromSeconds(1)) throw new ArgumentOutOfRangeException(nameof(longest));
        var total = pcm16.Length / 2;
        var most = (int)Math.Min(int.MaxValue, (long)(longest.TotalSeconds * sampleRate));
        if (total <= most) return [(0, total * 2)];
        var frame = sampleRate / 50;
        var back = (int)Math.Min(most / 2, (long)(SearchBack.TotalSeconds * sampleRate));
        var pieces = new List<(int, int)>();
        var start = 0;
        while (total - start > most)
        {
            var end = start + most;
            var cut = end;
            var quietest = double.MaxValue;
            for (var at = end - frame; at >= end - back; at -= frame / 2)
            {
                var energy = Energy(pcm16, at, frame);
                if (energy < quietest)
                {
                    quietest = energy;
                    cut = at + frame / 2;
                }
            }
            pieces.Add((start * 2, (cut - start) * 2));
            start = cut;
        }
        pieces.Add((start * 2, (total - start) * 2));
        return pieces;
    }

    private static double Energy(ReadOnlySpan<byte> pcm16, int first, int samples)
    {
        double sum = 0;
        for (var i = first; i < first + samples; i++)
        {
            double sample = BinaryPrimitives.ReadInt16LittleEndian(pcm16.Slice(i * 2, 2));
            sum += sample * sample;
        }
        return sum;
    }
}
