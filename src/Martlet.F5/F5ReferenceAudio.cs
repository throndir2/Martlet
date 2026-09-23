using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Martlet.F5;

internal sealed record ValidatedReferenceAudio(
    string SourcePath,
    byte[] Bytes,
    string Sha256,
    F5ReferenceAudioFormat Format);

internal static class F5ReferenceAudio
{
    internal static async Task<ValidatedReferenceAudio> ReadAsync(
        string absolutePath,
        CancellationToken cancellationToken)
    {
        var path = F5ReferencePaths.NormalizeSource(absolutePath, requireExisting: true);
        try
        {
            var before = new FileInfo(path);
            before.Refresh();
            F5Guard.Require(before.Exists, F5Failure.SourceMissing);
            F5Guard.Require(before.Length is >= 44 and <= F5ReferenceLimits.MaximumAudioFileBytes,
                before.Length > F5ReferenceLimits.MaximumAudioFileBytes
                    ? F5Failure.LimitExceeded
                    : F5Failure.InvalidAudio);
            var originalLength = before.Length;
            var originalWrite = before.LastWriteTimeUtc;
            var bytes = new byte[checked((int)originalLength)];
            var succeeded = false;
            try
            {
                await using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                    16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    F5Guard.Require(input.Length == originalLength, F5Failure.SourceChanged);
                    await input.ReadExactlyAsync(bytes, cancellationToken);
                    F5Guard.Require(input.ReadByte() == -1, F5Failure.SourceChanged);
                }

                var after = new FileInfo(path);
                after.Refresh();
                F5Guard.Require(after.Exists && after.Length == originalLength &&
                    after.LastWriteTimeUtc == originalWrite, F5Failure.SourceChanged);
                var format = Parse(bytes);
                succeeded = true;
                return new(path, bytes, Convert.ToHexStringLower(SHA256.HashData(bytes)), format);
            }
            finally
            {
                if (!succeeded)
                    CryptographicOperations.ZeroMemory(bytes);
            }
        }
        catch (F5Exception)
        {
            throw;
        }
        catch (FileNotFoundException)
        {
            throw new F5Exception(F5Failure.SourceMissing);
        }
        catch (DirectoryNotFoundException)
        {
            throw new F5Exception(F5Failure.SourceMissing);
        }
        catch (UnauthorizedAccessException)
        {
            throw new F5Exception(F5Failure.AccessDenied);
        }
        catch (IOException)
        {
            throw new F5Exception(F5Failure.IoFailure);
        }
    }

    internal static F5ReferenceAudioFormat Parse(ReadOnlySpan<byte> bytes)
    {
        F5Guard.Require(bytes.Length is >= 44 and <= F5ReferenceLimits.MaximumAudioFileBytes,
            F5Failure.InvalidAudio);
        F5Guard.Require(bytes[..4].SequenceEqual("RIFF"u8) &&
            bytes.Slice(8, 4).SequenceEqual("WAVE"u8), F5Failure.InvalidAudio);
        var riffBytes = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(4, 4));
        F5Guard.Require(riffBytes + 8UL == (ulong)bytes.Length, F5Failure.InvalidAudio);

        int? sampleRate = null;
        int? dataBytes = null;
        var usableSample = false;
        var offset = 12;
        while (offset < bytes.Length)
        {
            F5Guard.Require(bytes.Length - offset >= 8, F5Failure.InvalidAudio);
            var id = bytes.Slice(offset, 4);
            var declaredChunkBytes = BinaryPrimitives.ReadUInt32LittleEndian(
                bytes.Slice(offset + 4, 4));
            var payload = offset + 8;
            F5Guard.Require(declaredChunkBytes <= int.MaxValue &&
                declaredChunkBytes <= bytes.Length - payload, F5Failure.InvalidAudio);
            var chunkBytes = (int)declaredChunkBytes;
            var end = payload + chunkBytes;
            if (id.SequenceEqual("fmt "u8))
            {
                F5Guard.Require(sampleRate is null && chunkBytes == 16, F5Failure.InvalidAudio);
                var formatTag = BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(payload, 2));
                var channels = BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(payload + 2, 2));
                var rate = BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(payload + 4, 4));
                var byteRate = BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(payload + 8, 4));
                var blockAlign = BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(payload + 12, 2));
                var bits = BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(payload + 14, 2));
                F5Guard.Require(formatTag == 1 && channels == 1 && bits == 16 &&
                    rate is 16_000 or 22_050 or 24_000 or 44_100 or 48_000 &&
                    blockAlign == 2 && byteRate == checked(rate * 2), F5Failure.InvalidAudio);
                sampleRate = rate;
            }
            else if (id.SequenceEqual("data"u8))
            {
                F5Guard.Require(dataBytes is null && chunkBytes > 0 && chunkBytes % 2 == 0,
                    F5Failure.InvalidAudio);
                dataBytes = checked((int)chunkBytes);
                for (var sample = payload; sample < end; sample += 2)
                    usableSample |= BinaryPrimitives.ReadInt16LittleEndian(bytes.Slice(sample, 2)) != 0;
            }

            offset = end + ((chunkBytes & 1) == 0 ? 0 : 1);
            F5Guard.Require(offset <= bytes.Length, F5Failure.InvalidAudio);
        }

        F5Guard.Require(offset == bytes.Length && sampleRate is not null && dataBytes is not null &&
            usableSample, F5Failure.InvalidAudio);
        var validatedRate = sampleRate ?? throw new F5Exception(F5Failure.InvalidAudio);
        var validatedDataBytes = dataBytes ?? throw new F5Exception(F5Failure.InvalidAudio);
        var samples = validatedDataBytes / 2L;
        var duration = checked((int)Math.Ceiling(samples * 1000d / validatedRate));
        var result = new F5ReferenceAudioFormat
        {
            SampleRate = validatedRate,
            Channels = 1,
            BitsPerSample = 16,
            SampleCount = samples,
            DataBytes = validatedDataBytes,
            DurationMilliseconds = duration
        };
        result.Validate();
        return result;
    }
}
