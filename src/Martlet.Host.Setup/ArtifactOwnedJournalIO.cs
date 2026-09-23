using System.Security.Cryptography;

namespace Martlet.Host.Setup;

internal static class ArtifactOwnedJournalIO
{
    internal static ArtifactAcquisitionFileMetadata? InspectFile(string path)
    {
        FileAttributes attributes;
        try { attributes = File.GetAttributes(path); }
        catch (FileNotFoundException) { return null; }
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
            throw new ArtifactAcquisitionException(ArtifactAcquisitionFailure.DestinationInvalid);
        using var stream = ArtifactAcquisitionFileIdentity.OpenRead(path);
        return ArtifactAcquisitionFileIdentity.Read(stream.SafeFileHandle);
    }

    internal static async ValueTask<SetupFileSnapshot?> ReadAsync(string path, int maximumBytes,
        Action validate, CancellationToken cancellationToken)
    {
        validate();
        cancellationToken.ThrowIfCancellationRequested();
        var before = InspectFile(path);
        if (before is null) return null;
        if (before.Value.Bytes > maximumBytes)
            throw new ArtifactAcquisitionException(ArtifactAcquisitionFailure.JournalTooLarge);
        await using var stream = ArtifactAcquisitionFileIdentity.OpenRead(path);
        if (ArtifactAcquisitionFileIdentity.Read(stream.SafeFileHandle) != before)
            throw new ArtifactAcquisitionException(ArtifactAcquisitionFailure.JournalChanged);
        var bytes = new byte[checked((int)before.Value.Bytes)];
        await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        validate();
        if (stream.ReadByte() != -1 || InspectFile(path) != before ||
            ArtifactAcquisitionFileIdentity.Read(stream.SafeFileHandle) != before)
            throw new ArtifactAcquisitionException(ArtifactAcquisitionFailure.JournalChanged);
        return new(bytes, Convert.ToHexStringLower(SHA256.HashData(bytes)));
    }

    internal static async ValueTask<SetupFileSnapshot> WriteAsync(string path, int maximumBytes,
        string? expectedVersion, ReadOnlyMemory<byte> content, Action validate,
        ISetupDirectoryCommitter committer, CancellationToken cancellationToken)
    {
        validate();
        cancellationToken.ThrowIfCancellationRequested();
        if (content.Length > maximumBytes)
            throw new ArtifactAcquisitionException(ArtifactAcquisitionFailure.JournalTooLarge);
        if (expectedVersion is not null)
            AcquisitionGuard.Fingerprint(expectedVersion, ArtifactAcquisitionFailure.JournalChanged);
        var pending = path + ".pending";
        if (InspectFile(pending) is not null)
            throw new ArtifactAcquisitionException(ArtifactAcquisitionFailure.JournalChanged);
        var before = InspectFile(path);
        var current = await ReadAsync(path, maximumBytes, validate, cancellationToken).ConfigureAwait(false);
        if (current?.Version != expectedVersion)
            throw new ArtifactAcquisitionException(ArtifactAcquisitionFailure.JournalChanged);
        var ownedBytes = content.ToArray();
        string identity;
        validate();
        await using (var stream = new FileStream(pending, new FileStreamOptions
        {
            Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.Read,
            Options = FileOptions.Asynchronous | FileOptions.WriteThrough, BufferSize = 0
        }))
        {
            identity = ArtifactAcquisitionFileIdentity.Read(stream.SafeFileHandle).Identity;
            await stream.WriteAsync(ownedBytes, cancellationToken).ConfigureAwait(false);
            validate();
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            stream.Flush(true);
        }
        validate();
        cancellationToken.ThrowIfCancellationRequested();
        var latest = await ReadAsync(path, maximumBytes, validate, cancellationToken).ConfigureAwait(false);
        if (latest?.Version != expectedVersion || InspectFile(path) != before ||
            InspectFile(pending) != new ArtifactAcquisitionFileMetadata(identity, ownedBytes.Length))
            throw new ArtifactAcquisitionException(ArtifactAcquisitionFailure.JournalChanged);
        validate();
        File.Move(pending, path, overwrite: before is not null);
        committer.Commit(Path.GetDirectoryName(path)!);
        validate();
        if (InspectFile(path) != new ArtifactAcquisitionFileMetadata(identity, ownedBytes.Length))
            throw new ArtifactAcquisitionException(ArtifactAcquisitionFailure.JournalChanged);
        var result = await ReadAsync(path, maximumBytes, validate, CancellationToken.None).ConfigureAwait(false);
        if (result?.Version != Convert.ToHexStringLower(SHA256.HashData(ownedBytes)))
            throw new ArtifactAcquisitionException(ArtifactAcquisitionFailure.JournalChanged);
        return result;
    }
}
