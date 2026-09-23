using System.Security.Cryptography;
using Martlet.Core.Contracts;
using Martlet.Core.Voices;

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
        PcmWaveInfo wave;
        try { wave = PcmWaveInfo.Inspect(bytes, F5ReferenceLimits.MaximumAudioFileBytes); }
        catch (ContractException)
        {
            throw new F5Exception(F5Failure.InvalidAudio);
        }
        var result = new F5ReferenceAudioFormat
        {
            SampleRate = wave.SampleRate,
            Channels = 1,
            BitsPerSample = 16,
            SampleCount = wave.SampleCount,
            DataBytes = checked((int)wave.SampleCount * 2),
            DurationMilliseconds = wave.DurationMilliseconds
        };
        result.Validate();
        return result;
    }
}
