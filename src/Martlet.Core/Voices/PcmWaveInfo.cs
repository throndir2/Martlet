using System.Buffers.Binary;
using Martlet.Core.Contracts;

namespace Martlet.Core.Voices;

public sealed record PcmWaveInfo(int SampleRate, long SampleCount)
{
    public int DurationMilliseconds => checked((int)Math.Ceiling(SampleCount * 1000d / SampleRate));

    // A narrow import format, not a decoder. Do not execute codecs on arbitrary uploads: Desktop's Add a voice converts the
    // owner's own files on their PC (Martlet.Audio's VoiceRecordingImport) before anything reaches this check.
    public static PcmWaveInfo Inspect(ReadOnlySpan<byte> bytes, int maximumBytes) => Parse(bytes, maximumBytes).Info;

    /// <summary>The recording's mono PCM16 samples, after the same checks as <see cref="Inspect"/>.</summary>
    public static short[] Samples(ReadOnlySpan<byte> bytes, int maximumBytes, out PcmWaveInfo info)
    {
        (info, var offset) = Parse(bytes, maximumBytes);
        var samples = new short[info.SampleCount];
        for (var i = 0; i < samples.Length; i++)
            samples[i] = BinaryPrimitives.ReadInt16LittleEndian(bytes.Slice(offset + i * 2, 2));
        return samples;
    }

    /// <summary>A mono PCM16 WAV of <paramref name="samples"/> at <paramref name="sampleRate"/> with a plain 44-byte header.</summary>
    public static byte[] Write(int sampleRate, ReadOnlySpan<short> samples)
    {
        var wave = new byte[checked(44 + samples.Length * 2)];
        "RIFF"u8.CopyTo(wave);
        BinaryPrimitives.WriteUInt32LittleEndian(wave.AsSpan(4), (uint)(wave.Length - 8));
        "WAVEfmt "u8.CopyTo(wave.AsSpan(8));
        BinaryPrimitives.WriteUInt32LittleEndian(wave.AsSpan(16), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(wave.AsSpan(20), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(wave.AsSpan(22), 1);
        BinaryPrimitives.WriteInt32LittleEndian(wave.AsSpan(24), sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(wave.AsSpan(28), sampleRate * 2);
        BinaryPrimitives.WriteUInt16LittleEndian(wave.AsSpan(32), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(wave.AsSpan(34), 16);
        "data"u8.CopyTo(wave.AsSpan(36));
        BinaryPrimitives.WriteUInt32LittleEndian(wave.AsSpan(40), (uint)(samples.Length * 2));
        for (var i = 0; i < samples.Length; i++)
            BinaryPrimitives.WriteInt16LittleEndian(wave.AsSpan(44 + i * 2), samples[i]);
        return wave;
    }

    private static (PcmWaveInfo Info, int DataOffset) Parse(ReadOnlySpan<byte> bytes, int maximumBytes)
    {
        void Require(bool condition) => ContractRules.Require(condition,
            "Use a non-silent mono PCM16 WAV at 16, 22.05, 24, 44.1 or 48 kHz within the displayed duration and size limits.",
            ErrorCode.AudioFormatUnsupported);
        Require(bytes.Length >= 44 && bytes.Length <= maximumBytes);
        Require(bytes[..4].SequenceEqual("RIFF"u8) && bytes.Slice(8, 4).SequenceEqual("WAVE"u8) &&
            BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(4, 4)) + 8UL == (ulong)bytes.Length);
        int? rate = null;
        int? samples = null;
        var dataOffset = 0;
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
                dataOffset = offset;
                foreach (var value in chunk) audibleSample |= value != 0;
            }
            offset = checked(offset + (int)size + (int)(size & 1));
            Require(offset <= bytes.Length);
        }
        Require(rate.HasValue && samples.HasValue && audibleSample);
        return (new PcmWaveInfo(rate!.Value, samples!.Value), dataOffset);
    }
}
