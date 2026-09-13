using System.Buffers.Binary;
using Martlet.Core.Audio;
using Martlet.Core.Contracts;

namespace Martlet.Audio;

// Locally authored 200 ms faded tone, not speech, a recording, or audibility evidence.
public static class SyntheticTone
{
    public const int SampleCount = 4800;
    public static PcmFormat Format { get; } = new()
    {
        SampleRate = 24000, Channels = 1, Encoding = PcmEncoding.Signed16LittleEndian
    };

    public static IEnumerable<PcmFrame> Frames(CorrelationIds ids, long epoch)
    {
        for (var frame = 0; frame < 2; frame++)
        {
            var bytes = new byte[4800];
            for (var sample = 0; sample < 2400; sample++)
            {
                var position = frame * 2400 + sample;
                var fade = Math.Min(1.0, Math.Min(position, 4799 - position) / 240.0);
                var value = (short)(655 * fade * Math.Sin(2 * Math.PI * 440 * position / 24000));
                BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(sample * 2), value);
            }
            yield return new(ids, epoch, frame, frame * 2400, Format, bytes);
        }
    }
}
