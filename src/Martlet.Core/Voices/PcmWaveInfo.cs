using System.Buffers.Binary;
using Martlet.Core.Contracts;

namespace Martlet.Core.Voices;

public sealed record PcmWaveInfo(int SampleRate, long SampleCount)
{
    public int DurationMilliseconds => checked((int)Math.Ceiling(SampleCount * 1000d / SampleRate));

    // A narrow import format, not a decoder. Do not execute codecs on arbitrary uploads.
    public static PcmWaveInfo Inspect(ReadOnlySpan<byte> bytes, int maximumBytes)
    {
        void Require(bool condition) => ContractRules.Require(condition,
            "Use a non-silent mono PCM16 WAV at 16, 22.05, 24, 44.1 or 48 kHz within the displayed duration and size limits.",
            ErrorCode.AudioFormatUnsupported);
        Require(bytes.Length >= 44 && bytes.Length <= maximumBytes);
        Require(bytes[..4].SequenceEqual("RIFF"u8) && bytes.Slice(8, 4).SequenceEqual("WAVE"u8) &&
            BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(4, 4)) + 8UL == (ulong)bytes.Length);
        int? rate = null;
        int? samples = null;
        var audibleSample = false;
        var offset = 12;
        while (offset < bytes.Length)
        {
            Require(bytes.Length - offset >= 8);
            var id = bytes.Slice(offset, 4);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(offset + 4, 4));
            offset += 8;
            Require(size <= bytes.Length - offset);
            var chunk = bytes.Slice(offset, (int)size);
            if (id.SequenceEqual("fmt "u8))
            {
                Require(rate is null && size == 16);
                var value = BinaryPrimitives.ReadInt32LittleEndian(chunk.Slice(4, 4));
                Require(BinaryPrimitives.ReadUInt16LittleEndian(chunk) == 1 &&
                    BinaryPrimitives.ReadUInt16LittleEndian(chunk.Slice(2, 2)) == 1 &&
                    value is 16_000 or 22_050 or 24_000 or 44_100 or 48_000 &&
                    BinaryPrimitives.ReadInt32LittleEndian(chunk.Slice(8, 4)) == value * 2 &&
                    BinaryPrimitives.ReadUInt16LittleEndian(chunk.Slice(12, 2)) == 2 &&
                    BinaryPrimitives.ReadUInt16LittleEndian(chunk.Slice(14, 2)) == 16);
                rate = value;
            }
            else if (id.SequenceEqual("data"u8))
            {
                Require(samples is null && size > 0 && size % 2 == 0);
                samples = (int)size / 2;
                foreach (var value in chunk) audibleSample |= value != 0;
            }
            offset = checked(offset + (int)size + (int)(size & 1));
            Require(offset <= bytes.Length);
        }
        Require(rate.HasValue && samples.HasValue && audibleSample);
        return new PcmWaveInfo(rate!.Value, samples!.Value);
    }
}
