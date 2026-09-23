using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Martlet.LocalStt;

public sealed class CanonicalWaveAudio : IDisposable
{
    public const int SampleRate = 16_000;
    public const int Channels = 1;
    public const int BitsPerSample = 16;
    public const int HeaderBytes = 44;
    public const int MaximumPcmBytes = 800_000;
    public const int MaximumWaveBytes = HeaderBytes + MaximumPcmBytes;
    public static TimeSpan MaximumDuration => TimeSpan.FromSeconds(25);

    private byte[]? bytes;

    public int ByteLength => GetBytes().Length;
    public int SamplesPerChannel => (ByteLength - HeaderBytes) / 2;
    public TimeSpan Duration => TimeSpan.FromSeconds((double)SamplesPerChannel / SampleRate);
    public string Sha256 { get; }

    private CanonicalWaveAudio(byte[] bytes)
    {
        this.bytes = bytes;
        Sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    public static CanonicalWaveAudio FromWave(ReadOnlySpan<byte> wave)
    {
        if (wave.Length is < HeaderBytes + 2 or > MaximumWaveBytes)
            throw new LocalSttContractException(wave.Length > MaximumWaveBytes
                ? LocalSttFailureCode.AudioLimit
                : LocalSttFailureCode.AudioFormatUnsupported);
        if (!wave[..4].SequenceEqual("RIFF"u8) ||
            !wave.Slice(8, 8).SequenceEqual("WAVEfmt "u8) ||
            !wave.Slice(36, 4).SequenceEqual("data"u8) ||
            BinaryPrimitives.ReadUInt32LittleEndian(wave[4..]) != wave.Length - 8 ||
            BinaryPrimitives.ReadUInt32LittleEndian(wave[16..]) != 16 ||
            BinaryPrimitives.ReadUInt16LittleEndian(wave[20..]) != 1 ||
            BinaryPrimitives.ReadUInt16LittleEndian(wave[22..]) != Channels ||
            BinaryPrimitives.ReadUInt32LittleEndian(wave[24..]) != SampleRate ||
            BinaryPrimitives.ReadUInt32LittleEndian(wave[28..]) != SampleRate * 2 ||
            BinaryPrimitives.ReadUInt16LittleEndian(wave[32..]) != 2 ||
            BinaryPrimitives.ReadUInt16LittleEndian(wave[34..]) != BitsPerSample ||
            BinaryPrimitives.ReadUInt32LittleEndian(wave[40..]) != wave.Length - HeaderBytes ||
            (wave.Length - HeaderBytes) % 2 != 0)
            throw new LocalSttContractException(LocalSttFailureCode.AudioFormatUnsupported);
        return new(wave.ToArray());
    }

    internal async ValueTask WriteToAsync(Stream destination, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(destination);
        await destination.WriteAsync(GetBytes(), cancellationToken).ConfigureAwait(false);
    }

    internal bool Matches(string sha256, int byteLength) =>
        byteLength == ByteLength && string.Equals(sha256, Sha256, StringComparison.Ordinal);

    private byte[] GetBytes() =>
        bytes ?? throw new ObjectDisposedException(nameof(CanonicalWaveAudio));

    public void Dispose()
    {
        var owned = Interlocked.Exchange(ref bytes, null);
        if (owned is not null)
            CryptographicOperations.ZeroMemory(owned);
    }

    public override string ToString() => nameof(CanonicalWaveAudio);
}
