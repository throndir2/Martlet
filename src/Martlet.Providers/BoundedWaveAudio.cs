using System.Buffers.Binary;
using Martlet.Core.Audio;
using Martlet.Core.Contracts;

namespace Martlet.Providers;

public sealed class BoundedWaveAudio
{
    private readonly byte[] wave;
    public PcmFormat Format { get; }
    public int ByteLength => wave.Length;
    public int SamplesPerChannel => (wave.Length - 44) / Format.BlockAlignment;
    public TimeSpan Duration => TimeSpan.FromSeconds((double)SamplesPerChannel / Format.SampleRate);

    private BoundedWaveAudio(PcmFormat format, byte[] wave)
    {
        Format = format;
        this.wave = wave;
    }

    public static BoundedWaveAudio FromPcm(PcmFormat format, ReadOnlySpan<byte> pcm)
    {
        ArgumentNullException.ThrowIfNull(format);
        ValidatePcm(format, pcm.Length);
        byte[] wave = new byte[44 + pcm.Length];
        "RIFF"u8.CopyTo(wave);
        BinaryPrimitives.WriteUInt32LittleEndian(wave.AsSpan(4), (uint)(wave.Length - 8));
        "WAVEfmt "u8.CopyTo(wave.AsSpan(8));
        BinaryPrimitives.WriteUInt32LittleEndian(wave.AsSpan(16), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(wave.AsSpan(20), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(wave.AsSpan(22), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(wave.AsSpan(24), (uint)format.SampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(wave.AsSpan(28), (uint)(format.SampleRate * format.BlockAlignment));
        BinaryPrimitives.WriteUInt16LittleEndian(wave.AsSpan(32), (ushort)format.BlockAlignment);
        BinaryPrimitives.WriteUInt16LittleEndian(wave.AsSpan(34), 16);
        "data"u8.CopyTo(wave.AsSpan(36));
        BinaryPrimitives.WriteUInt32LittleEndian(wave.AsSpan(40), (uint)pcm.Length);
        pcm.CopyTo(wave.AsSpan(44));
        return new(format, wave);
    }

    public static BoundedWaveAudio FromWave(ReadOnlySpan<byte> wave)
    {
        ContractRules.Require(wave.Length <= TranscriptionLimits.HardMaxAudioBytes,
            "The audio file exceeds the upload limit.", ErrorCode.PayloadTooLarge);
        ContractRules.Require(wave.Length >= 46 && wave[..4].SequenceEqual("RIFF"u8) &&
            wave.Slice(8, 8).SequenceEqual("WAVEfmt "u8) &&
            BinaryPrimitives.ReadUInt32LittleEndian(wave[4..]) == wave.Length - 8 &&
            BinaryPrimitives.ReadUInt32LittleEndian(wave[16..]) == 16 &&
            BinaryPrimitives.ReadUInt16LittleEndian(wave[20..]) == 1 &&
            BinaryPrimitives.ReadUInt16LittleEndian(wave[22..]) == 1 &&
            BinaryPrimitives.ReadUInt16LittleEndian(wave[34..]) == 16 &&
            wave.Slice(36, 4).SequenceEqual("data"u8) &&
            BinaryPrimitives.ReadUInt32LittleEndian(wave[40..]) == wave.Length - 44,
            "Use a canonical mono PCM16 WAV with exactly one fmt and data chunk.", ErrorCode.AudioFormatUnsupported);
        var format = new PcmFormat
        {
            SampleRate = (int)BinaryPrimitives.ReadUInt32LittleEndian(wave[24..]),
            Channels = 1, Encoding = PcmEncoding.Signed16LittleEndian
        };
        ValidatePcm(format, wave.Length - 44);
        ContractRules.Require(BinaryPrimitives.ReadUInt16LittleEndian(wave[32..]) == format.BlockAlignment &&
            BinaryPrimitives.ReadUInt32LittleEndian(wave[28..]) == format.SampleRate * format.BlockAlignment,
            "The WAV sample alignment or byte rate is inconsistent.", ErrorCode.AudioFormatUnsupported);
        return new(format, wave.ToArray());
    }

    private static void ValidatePcm(PcmFormat format, int byteLength)
    {
        format.Validate();
        ContractRules.Require(format.Channels == 1 && byteLength > 0 && byteLength % format.BlockAlignment == 0,
            "Transcription requires nonempty, sample-aligned mono PCM16.", ErrorCode.AudioFormatUnsupported);
        ContractRules.Require(byteLength <= TranscriptionLimits.HardMaxAudioBytes - 44 &&
            byteLength / format.BlockAlignment <= format.SampleRate * 90,
            "The utterance exceeds the byte or duration limit.", ErrorCode.PayloadTooLarge);
    }

    /// <summary>Several utterances of the same format as one recording, with <paramref name="gap"/> of silence between them, or
    /// null when there are none, the formats differ or together they are longer than <paramref name="maximum"/>.</summary>
    public static BoundedWaveAudio? Join(IReadOnlyList<BoundedWaveAudio> parts, TimeSpan gap, TimeSpan maximum)
    {
        ArgumentNullException.ThrowIfNull(parts);
        if (parts.Count == 0 || parts.Any(p => p.Format != parts[0].Format)) return null;
        if (parts.Sum(p => p.Duration.TotalSeconds) + gap.TotalSeconds * (parts.Count - 1) > maximum.TotalSeconds) return null;
        if (parts.Count == 1) return parts[0];
        var format = parts[0].Format;
        var silence = (int)(format.SampleRate * gap.TotalSeconds) * format.BlockAlignment;
        var pcm = new byte[parts.Sum(p => p.wave.Length - 44) + silence * (parts.Count - 1)];
        var at = 0;
        foreach (var part in parts)
        {
            if (at > 0) at += silence;
            part.wave.AsSpan(44).CopyTo(pcm.AsSpan(at));
            at += part.wave.Length - 44;
        }
        return FromPcm(format, pcm);
    }

    /// <summary>The whole WAV file, for a model that hears (Chat Completions <c>input_audio</c>).</summary>
    public string ToBase64() => Convert.ToBase64String(wave);

    /// <summary>Whether <paramref name="other"/> holds exactly the same recording (format and samples).</summary>
    public bool SameAudio(BoundedWaveAudio? other) =>
        other is not null && (ReferenceEquals(this, other) || Format == other.Format && wave.AsSpan().SequenceEqual(other.wave));

    internal HttpContent CreateContent() => new ByteArrayContent(wave);
    internal ReadOnlyMemory<byte> Pcm => wave.AsMemory(44);
    public override string ToString() => nameof(BoundedWaveAudio);
}
