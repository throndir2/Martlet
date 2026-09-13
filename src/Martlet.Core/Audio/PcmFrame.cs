using Martlet.Core.Contracts;
using System.Text.Json.Serialization;

namespace Martlet.Core.Audio;

public enum PcmEncoding { Signed16LittleEndian }

public sealed record PcmFormat : IContract
{
    public required int SampleRate { get; init; }
    public required int Channels { get; init; }
    public required PcmEncoding Encoding { get; init; }
    [JsonIgnore]
    public int BlockAlignment => Channels * 2;

    public void Validate()
    {
        ContractRules.Require(SampleRate is 16000 or 24000 or 44100 or 48000, "Unsupported PCM sample rate.");
        ContractRules.Require(Channels is 1 or 2, "PCM must be mono or stereo.");
        ContractRules.Require(Encoding == PcmEncoding.Signed16LittleEndian, "PCM must be signed 16-bit little-endian.");
    }
}

// In-process PCM only. The remote binary header/transport belongs to a later D02 slice.
public sealed class PcmFrame
{
    public const int MaxDataBytes = 19_200;
    private readonly byte[] data;
    public CorrelationIds Ids { get; }
    public long Epoch { get; }
    public long Sequence { get; }
    public long SampleOffset { get; }
    public PcmFormat Format { get; }
    public ReadOnlyMemory<byte> Data => data;
    public int SamplesPerChannel => data.Length / Format.BlockAlignment;

    public PcmFrame(CorrelationIds ids, long epoch, long sequence, long sampleOffset, PcmFormat format, ReadOnlySpan<byte> data)
    {
        ArgumentNullException.ThrowIfNull(ids);
        ArgumentNullException.ThrowIfNull(format);
        ids.Validate();
        format.Validate();
        ContractRules.Require(epoch is >= 0 and <= int.MaxValue && sequence is >= 0 and <= int.MaxValue, "PCM epoch or sequence is out of range.");
        ContractRules.Require(data.Length is > 0 and <= MaxDataBytes && data.Length % format.BlockAlignment == 0, "PCM data must be bounded and sample-aligned.");
        var samples = data.Length / format.BlockAlignment;
        ContractRules.Require(samples <= format.SampleRate / 10, "A PCM frame cannot exceed 100 ms.");
        ContractRules.Require(sampleOffset >= 0 && sampleOffset <= long.MaxValue - samples, "PCM sample offset is out of range.");
        Ids = ids;
        Epoch = epoch;
        Sequence = sequence;
        SampleOffset = sampleOffset;
        Format = format;
        this.data = data.ToArray();
    }
}
