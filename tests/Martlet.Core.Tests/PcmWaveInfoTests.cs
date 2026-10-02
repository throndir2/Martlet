using System.Buffers.Binary;
using Martlet.Core.Contracts;
using Martlet.Core.Voices;

namespace Martlet.Core.Tests;

public sealed class PcmWaveInfoTests
{
    private const int MaximumBytes = 4 * 1024 * 1024;

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void InvalidWaveStructureRejected(int mutation)
    {
        var bytes = Wave(1);
        switch (mutation)
        {
            case 0: bytes[0] = 0; break;
            case 1: bytes[22] = 2; break;
            case 2: bytes[34] = 32; break;
            case 3: BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(40), uint.MaxValue); break;
            case 4: bytes[4] = 0; break;
            case 5: Array.Clear(bytes, 44, bytes.Length - 44); break;
        }
        Assert.Throws<ContractException>(() => PcmWaveInfo.Inspect(bytes, MaximumBytes));
    }

    [Theory]
    [InlineData(16_000)]
    [InlineData(22_050)]
    [InlineData(24_000)]
    [InlineData(44_100)]
    [InlineData(48_000)]
    public void AllDocumentedRatesAreAccepted(int rate)
    {
        var info = PcmWaveInfo.Inspect(Wave(1, rate), MaximumBytes);
        Assert.Equal(rate, info.SampleRate);
        Assert.Equal(1000, info.DurationMilliseconds);
    }

    [Fact]
    public void OversizeRejected() =>
        Assert.Throws<ContractException>(() => PcmWaveInfo.Inspect(Wave(10), 1024));

    internal static byte[] Wave(int seconds, int rate = 16_000)
    {
        var data = new byte[44 + rate * seconds * 2];
        "RIFF"u8.CopyTo(data);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(4), data.Length - 8);
        "WAVEfmt "u8.CopyTo(data.AsSpan(8));
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(16), 16);
        BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(20), 1);
        BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(22), 1);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(24), rate);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(28), rate * 2);
        BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(32), 2);
        BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(34), 16);
        "data"u8.CopyTo(data.AsSpan(36));
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(40), data.Length - 44);
        data[44] = 1;
        return data;
    }
}