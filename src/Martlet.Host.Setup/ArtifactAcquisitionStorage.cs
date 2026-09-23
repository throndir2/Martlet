using System.Collections.Concurrent;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using Martlet.HostArtifacts;

namespace Martlet.Host.Setup;

public sealed class ArtifactAcquisitionPaths
{
    public string RootPath { get; }
    public string DestinationPath { get; }
    public string PartialPath { get; }
    public string JournalPath { get; }
    public string LeasePath => DestinationPath + ".lease";
    public string QuarantinePath => DestinationPath + ".quarantine";

    internal ArtifactAcquisitionPaths(string rootPath, string destinationPath,
        string partialPath, string journalPath)
    {
        RootPath = rootPath;
        DestinationPath = destinationPath;
        PartialPath = partialPath;
        JournalPath = journalPath;
    }
}

public sealed class ArtifactAcquisitionStorageSnapshot
{
    private readonly byte[]? journalContent;
    public long AvailableBytes { get; }
    public bool PartialExists { get; }
    public long PartialBytes { get; }
    public string? PartialIdentity { get; }
    public bool FinalExists { get; }
    public long FinalBytes { get; }
    public string? FinalIdentity { get; }
    public bool QuarantineExists { get; }
    public long QuarantineBytes { get; }
    public string? QuarantineIdentity { get; }
    public string? JournalVersion { get; }
    public bool JournalPendingExists { get; }
    public bool LeaseExists { get; }
    public ReadOnlyMemory<byte>? JournalContent =>
        journalContent is null
            ? (ReadOnlyMemory<byte>?)null
            : new ReadOnlyMemory<byte>(journalContent.ToArray());
    public string Fingerprint { get; }

    public ArtifactAcquisitionStorageSnapshot(long availableBytes, bool partialExists,
        long partialBytes, bool finalExists, long finalBytes, string? journalVersion,
        ReadOnlyMemory<byte>? journalContent, bool journalPendingExists,
        string? partialIdentity = null, string? finalIdentity = null,
        bool quarantineExists = false, long quarantineBytes = 0,
        string? quarantineIdentity = null, bool leaseExists = false)
    {
        AcquisitionGuard.Require(availableBytes >= 0 && partialBytes >= 0 &&
            finalBytes >= 0 && quarantineBytes >= 0 &&
            (partialExists || partialBytes == 0 && partialIdentity is null) &&
            (finalExists || finalBytes == 0 && finalIdentity is null) &&
            (quarantineExists || quarantineBytes == 0 && quarantineIdentity is null),
            ArtifactAcquisitionFailure.StorageFailed);
        if (journalVersion is not null)
            AcquisitionGuard.Fingerprint(journalVersion, ArtifactAcquisitionFailure.JournalCorrupt);
        AcquisitionGuard.Require((journalVersion is null) == (journalContent is null),
            ArtifactAcquisitionFailure.JournalCorrupt);
        AvailableBytes = availableBytes;
        PartialExists = partialExists;
        PartialBytes = partialBytes;
        PartialIdentity = partialIdentity;
        FinalExists = finalExists;
        FinalBytes = finalBytes;
        FinalIdentity = finalIdentity;
        QuarantineExists = quarantineExists;
        QuarantineBytes = quarantineBytes;
        QuarantineIdentity = quarantineIdentity;
        JournalVersion = journalVersion;
        this.journalContent = journalContent?.ToArray();
        JournalPendingExists = journalPendingExists;
        LeaseExists = leaseExists;
        Fingerprint = FingerprintBuilder.Create("artifact-storage-facts-v2",
            partialExists.ToString(), partialBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
            partialIdentity ?? "null", finalExists.ToString(),
            finalBytes.ToString(System.Globalization.CultureInfo.InvariantCulture), finalIdentity ?? "null",
            quarantineExists.ToString(), quarantineBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
            quarantineIdentity ?? "null", journalVersion ?? "null", journalPendingExists.ToString());
    }
}

internal sealed record ArtifactAcquisitionFileSnapshot(string Identity, long Bytes);

internal interface IArtifactPartialWriter : IAsyncDisposable
{
    string Identity { get; }
    long Position { get; }
    ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken);
    ValueTask FlushToDiskAsync(CancellationToken cancellationToken);
}

public interface IArtifactAcquisitionStorage
{
    string RootPath { get; }
    ArtifactAcquisitionPaths GetPaths(ArtifactAcquisitionCandidate candidate);
    ValueTask<ArtifactAcquisitionStorageSnapshot> InspectAsync(
        ArtifactAcquisitionPaths paths, CancellationToken cancellationToken);
    ValueTask<string> ComputePartialSha256Async(
        ArtifactAcquisitionPaths paths, long expectedBytes, CancellationToken cancellationToken);
    ValueTask<string> ComputeFinalSha256Async(
        ArtifactAcquisitionPaths paths, long expectedBytes, CancellationToken cancellationToken);
}

internal interface IArtifactAcquisitionMutationStorage : IArtifactAcquisitionStorage
{
    ValueTask<IAsyncDisposable> AcquireLeaseAsync(
        ArtifactAcquisitionPaths paths, Guid ownerId, CancellationToken cancellationToken);
    ValueTask ValidateLeaseAsync(
        ArtifactAcquisitionPaths paths, CancellationToken cancellationToken);
    ValueTask<SetupFileSnapshot> WriteJournalAsync(ArtifactAcquisitionPaths paths,
        string? expectedVersion, ReadOnlyMemory<byte> content, CancellationToken cancellationToken);
    ValueTask<IArtifactPartialWriter> OpenPartialAsync(ArtifactAcquisitionPaths paths,
        long offset, string? expectedIdentity, CancellationToken cancellationToken);
    ValueTask FinalizeAsync(ArtifactAcquisitionPaths paths, string expectedIdentity,
        long expectedBytes, CancellationToken cancellationToken);
    ValueTask DeleteOwnedPartialAsync(ArtifactAcquisitionPaths paths, string expectedIdentity,
        long expectedBytes, CancellationToken cancellationToken);
    ValueTask<ArtifactAcquisitionFileSnapshot> QuarantineOwnedFinalAsync(
        ArtifactAcquisitionPaths paths, string expectedIdentity, long expectedBytes,
        string expectedHash, CancellationToken cancellationToken);
}

/// <summary>
/// Bounded files in an existing private local directory. The caller must keep
/// the root and its ancestors non-hostile and non-replaceable. Revalidation and
/// cooperative leases do not provide arbitrary hostile namespace race defense.
/// Construction and inspection never create directories or payload files.
/// </summary>
public sealed partial class LocalArtifactAcquisitionStorage : IArtifactAcquisitionMutationStorage
{
    private readonly ISetupDirectoryCommitter directoryCommitter;
    private readonly IArtifactFreeSpaceProbe freeSpaceProbe;
    private readonly ConcurrentDictionary<string, Lease> leases = new(StringComparer.Ordinal);
    public string RootPath { get; }

    public LocalArtifactAcquisitionStorage(string absoluteExistingArtifactDirectory,
        ISetupDirectoryCommitter? directoryCommitter = null,
        IArtifactFreeSpaceProbe? freeSpaceProbe = null)
    {
        RootPath = Io(() =>
        {
            ValidateAbsolutePath(absoluteExistingArtifactDirectory);
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(absoluteExistingArtifactDirectory));
            ValidateDirectory(root);
            if (directoryCommitter is null && !OperatingSystem.IsLinux())
                throw Failure(ArtifactAcquisitionFailure.DestinationInvalid);
            return root;
        });
        this.directoryCommitter = directoryCommitter ?? new PlatformSetupDirectoryCommitter();
        this.freeSpaceProbe = freeSpaceProbe ?? new PlatformArtifactFreeSpaceProbe();
    }

    public ArtifactAcquisitionPaths GetPaths(ArtifactAcquisitionCandidate candidate) => Io(() =>
    {
        ArgumentNullException.ThrowIfNull(candidate);
        AcquisitionGuard.Require(candidate.ArtifactId.Length is > 0 and <= 128 &&
            candidate.ArtifactId.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.'),
            ArtifactAcquisitionFailure.DestinationInvalid);
        var prefix = candidate.ExpectedSha256 is { Length: 64 } hash ? hash[..16] : "unpinned";
        var destination = Path.Combine(RootPath, $"artifact-{candidate.ArtifactId}-{prefix}.bin");
        var paths = new ArtifactAcquisitionPaths(RootPath, destination,
            destination + ".partial", destination + ".acquisition.json");
        ValidatePaths(paths);
        return paths;
    });

    public ValueTask<ArtifactAcquisitionStorageSnapshot> InspectAsync(
        ArtifactAcquisitionPaths paths, CancellationToken cancellationToken) => IoAsync(async () =>
    {
        ValidatePaths(paths);
        cancellationToken.ThrowIfCancellationRequested();
        var partial = FileState(paths, paths.PartialPath);
        var final = FileState(paths, paths.DestinationPath);
        var quarantine = FileState(paths, paths.QuarantinePath);
        var pending = FileState(paths, paths.JournalPath + ".pending");
        var lease = FileState(paths, paths.LeasePath);
        var journal = await ReadJournalAsync(paths, cancellationToken).ConfigureAwait(false);
        ValidatePaths(paths);
        var available = freeSpaceProbe.GetAvailableBytes(RootPath);
        return new ArtifactAcquisitionStorageSnapshot(available, partial is not null,
            partial?.Bytes ?? 0, final is not null, final?.Bytes ?? 0,
            journal?.Version, journal?.Content, pending is not null,
            partial?.Identity, final?.Identity, quarantine is not null,
            quarantine?.Bytes ?? 0, quarantine?.Identity, lease is not null);
    });

    internal ValueTask<IAsyncDisposable> AcquireLeaseAsync(ArtifactAcquisitionPaths paths,
        Guid ownerId, CancellationToken cancellationToken) => IoAsync<IAsyncDisposable>(async () =>
    {
        ValidatePaths(paths);
        cancellationToken.ThrowIfCancellationRequested();
        AcquisitionGuard.Require(ownerId != Guid.Empty, ArtifactAcquisitionFailure.InvalidPlan);
        if (FileState(paths, paths.LeasePath) is not null)
            throw Failure(ArtifactAcquisitionFailure.JournalChanged);
        FileStream stream;
        try { stream = CreateNew(paths.LeasePath); }
        catch (IOException error) { throw new ArtifactAcquisitionException(ArtifactAcquisitionFailure.JournalChanged, error); }
        await using (stream.ConfigureAwait(false))
        {
            var metadata = ArtifactAcquisitionFileIdentity.Read(stream.SafeFileHandle);
            var content = Encoding.ASCII.GetBytes(ownerId.ToString("N"));
            await stream.WriteAsync(content, cancellationToken).ConfigureAwait(false);
            ValidatePaths(paths);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            stream.Flush(true);
            ValidatePaths(paths);
            directoryCommitter.Commit(RootPath);
            var lease = new Lease(this, paths, metadata.Identity, content);
            // A failed acquisition deliberately leaves its marker. There is no
            // stale-marker theft or guessed ownership after uncertain IO.
            if (!leases.TryAdd(paths.LeasePath, lease))
                throw Failure(ArtifactAcquisitionFailure.JournalChanged);
            return lease;
        }
    });

    internal ValueTask ValidateLeaseAsync(ArtifactAcquisitionPaths paths,
        CancellationToken cancellationToken) => IoAction(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        RequireLease(paths);
    });

    internal ValueTask<SetupFileSnapshot> WriteJournalAsync(ArtifactAcquisitionPaths paths,
        string? expectedVersion, ReadOnlyMemory<byte> content, CancellationToken cancellationToken) =>
        IoAsync(() => ArtifactOwnedJournalIO.WriteAsync(paths.JournalPath,
            ArtifactAcquisitionJournalCodec.MaximumBytes, expectedVersion, content,
            () => RequireLease(paths), directoryCommitter, cancellationToken));

    internal ValueTask<IArtifactPartialWriter> OpenPartialAsync(ArtifactAcquisitionPaths paths,
        long offset, string? expectedIdentity, CancellationToken cancellationToken) =>
        new(Io<IArtifactPartialWriter>(() =>
        {
            RequireLease(paths);
            cancellationToken.ThrowIfCancellationRequested();
            AcquisitionGuard.Require(offset >= 0 && (expectedIdentity is not null || offset == 0),
                ArtifactAcquisitionFailure.ResumeStateMismatch);
            if (expectedIdentity is not null)
                RequireFile(paths, paths.PartialPath, expectedIdentity, offset,
                    ArtifactAcquisitionFailure.ResumeStateMismatch);
            else if (FileState(paths, paths.PartialPath) is not null)
                throw Failure(ArtifactAcquisitionFailure.PartialConflict);
            var stream = new FileStream(paths.PartialPath, new FileStreamOptions
            {
                Mode = expectedIdentity is null ? FileMode.CreateNew : FileMode.Open,
                Access = FileAccess.Write,
                Share = FileShare.Read,
                Options = FileOptions.Asynchronous | FileOptions.WriteThrough,
                BufferSize = 0
            });
            try
            {
                var metadata = ArtifactAcquisitionFileIdentity.Read(stream.SafeFileHandle);
                if (metadata.Bytes != offset ||
                    expectedIdentity is not null && metadata.Identity != expectedIdentity)
                    throw Failure(ArtifactAcquisitionFailure.ResumeStateMismatch);
                stream.Position = offset;
                return new PartialWriter(this, paths, stream, metadata.Identity);
            }
            catch { stream.Dispose(); throw; }
        }));

    public ValueTask<string> ComputePartialSha256Async(ArtifactAcquisitionPaths paths,
        long expectedBytes, CancellationToken cancellationToken) =>
        ComputeSha256Async(paths, paths.PartialPath, expectedBytes, null,
            ArtifactAcquisitionFailure.ResumeStateMismatch, cancellationToken);

    public ValueTask<string> ComputeFinalSha256Async(ArtifactAcquisitionPaths paths,
        long expectedBytes, CancellationToken cancellationToken) =>
        ComputeSha256Async(paths, paths.DestinationPath, expectedBytes, null,
            ArtifactAcquisitionFailure.FinalConflict, cancellationToken);

    internal ValueTask FinalizeAsync(ArtifactAcquisitionPaths paths, string expectedIdentity,
        long expectedBytes, CancellationToken cancellationToken) => IoAction(() =>
    {
        RequireLease(paths);
        cancellationToken.ThrowIfCancellationRequested();
        RequireFile(paths, paths.PartialPath, expectedIdentity, expectedBytes,
            ArtifactAcquisitionFailure.ResumeStateMismatch);
        if (FileState(paths, paths.DestinationPath) is not null)
            throw Failure(ArtifactAcquisitionFailure.FinalConflict);
        RequireLease(paths);
        File.Move(paths.PartialPath, paths.DestinationPath, overwrite: false);
        Commit(paths);
        RequireFile(paths, paths.DestinationPath, expectedIdentity, expectedBytes,
            ArtifactAcquisitionFailure.FinalConflict);
    });

    internal ValueTask DeleteOwnedPartialAsync(ArtifactAcquisitionPaths paths, string expectedIdentity,
        long expectedBytes, CancellationToken cancellationToken) => IoAction(() =>
    {
        RequireLease(paths);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateIdentity(expectedIdentity);
        if (FileState(paths, paths.PartialPath) is null)
            return;
        RequireFile(paths, paths.PartialPath, expectedIdentity, expectedBytes,
            ArtifactAcquisitionFailure.PartialConflict);
        RequireLease(paths);
        File.Delete(paths.PartialPath);
        Commit(paths);
    });

    internal ValueTask<ArtifactAcquisitionFileSnapshot> QuarantineOwnedFinalAsync(
        ArtifactAcquisitionPaths paths, string expectedIdentity, long expectedBytes,
        string expectedHash, CancellationToken cancellationToken) => IoAsync(async () =>
    {
        RequireLease(paths);
        AcquisitionGuard.Fingerprint(expectedHash, ArtifactAcquisitionFailure.FinalConflict);
        if (FileState(paths, paths.DestinationPath) is null)
        {
            // A durable move may precede the journal update. Recover only the
            // exact retained inode and corrupt bytes from that approved action.
            var retained = RequireFile(paths, paths.QuarantinePath, expectedIdentity, expectedBytes,
                ArtifactAcquisitionFailure.FinalConflict);
            var retainedHash = await ComputeSha256Async(paths, paths.QuarantinePath, expectedBytes,
                expectedIdentity, ArtifactAcquisitionFailure.FinalConflict, cancellationToken).ConfigureAwait(false);
            if (retainedHash != expectedHash)
                throw Failure(ArtifactAcquisitionFailure.FinalConflict);
            RequireLease(paths);
            cancellationToken.ThrowIfCancellationRequested();
            if (FileState(paths, paths.DestinationPath) is not null)
                throw Failure(ArtifactAcquisitionFailure.FinalConflict);
            Commit(paths);
            return new ArtifactAcquisitionFileSnapshot(retained.Identity, retained.Bytes);
        }
        RequireFile(paths, paths.DestinationPath, expectedIdentity, expectedBytes,
            ArtifactAcquisitionFailure.FinalConflict);
        if (FileState(paths, paths.QuarantinePath) is not null)
            throw Failure(ArtifactAcquisitionFailure.FinalConflict);
        var actualHash = await ComputeSha256Async(paths, paths.DestinationPath, expectedBytes,
            expectedIdentity, ArtifactAcquisitionFailure.FinalConflict, cancellationToken).ConfigureAwait(false);
        if (actualHash != expectedHash)
            throw Failure(ArtifactAcquisitionFailure.FinalConflict);
        RequireLease(paths);
        cancellationToken.ThrowIfCancellationRequested();
        RequireFile(paths, paths.DestinationPath, expectedIdentity, expectedBytes,
            ArtifactAcquisitionFailure.FinalConflict);
        File.Move(paths.DestinationPath, paths.QuarantinePath, overwrite: false);
        Commit(paths);
        var result = RequireFile(paths, paths.QuarantinePath, expectedIdentity, expectedBytes,
            ArtifactAcquisitionFailure.FinalConflict);
        return new ArtifactAcquisitionFileSnapshot(result.Identity, result.Bytes);
    });

    private ValueTask<string> ComputeSha256Async(ArtifactAcquisitionPaths paths, string path,
        long expectedBytes, string? expectedIdentity, ArtifactAcquisitionFailure mismatch,
        CancellationToken cancellationToken) => IoAsync(async () =>
    {
        AcquisitionGuard.Require(expectedBytes >= 0, ArtifactAcquisitionFailure.InvalidPlan);
        cancellationToken.ThrowIfCancellationRequested();
        var before = FileState(paths, path) ?? throw Failure(mismatch);
        if (before.Bytes != expectedBytes ||
            expectedIdentity is not null && before.Identity != expectedIdentity)
            throw Failure(mismatch);
        return await ArtifactAcquisitionFileIdentity.HashOwnedFileAsync(path, before, token =>
        {
            token.ThrowIfCancellationRequested();
            ValidatePaths(paths);
            return ValueTask.CompletedTask;
        }, mismatch, cancellationToken).ConfigureAwait(false);
    });

    private ValueTask<SetupFileSnapshot?> ReadJournalAsync(
        ArtifactAcquisitionPaths paths, CancellationToken cancellationToken) =>
        ArtifactOwnedJournalIO.ReadAsync(paths.JournalPath, ArtifactAcquisitionJournalCodec.MaximumBytes,
            () => ValidatePaths(paths), cancellationToken);

    private ArtifactAcquisitionFileMetadata? FileState(ArtifactAcquisitionPaths paths, string path)
    {
        ValidatePaths(paths);
        if (!HasRegularAttributes(path)) return null;
        using var stream = ArtifactAcquisitionFileIdentity.OpenRead(path);
        return ArtifactAcquisitionFileIdentity.Read(stream.SafeFileHandle);
    }

    private ArtifactAcquisitionFileMetadata RequireFile(ArtifactAcquisitionPaths paths,
        string path, string identity, long bytes, ArtifactAcquisitionFailure failure)
    {
        ValidateIdentity(identity);
        var state = FileState(paths, path);
        if (state is null || state.Value.Identity != identity || state.Value.Bytes != bytes)
            throw Failure(failure);
        return state.Value;
    }

    private static void ValidateIdentity(string identity) =>
        AcquisitionGuard.Text(identity, 256, ArtifactAcquisitionFailure.InvalidPlan);

    private void RequireLease(ArtifactAcquisitionPaths paths)
    {
        ValidatePaths(paths);
        if (!leases.TryGetValue(paths.LeasePath, out var lease))
            throw Failure(ArtifactAcquisitionFailure.JournalChanged);
        lease.Validate();
    }

    private void Commit(ArtifactAcquisitionPaths paths)
    {
        RequireLease(paths);
        directoryCommitter.Commit(RootPath);
    }

    private void ValidatePaths(ArtifactAcquisitionPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ValidateDirectory(RootPath);
        if (paths.RootPath != RootPath ||
            paths.PartialPath != paths.DestinationPath + ".partial" ||
            paths.JournalPath != paths.DestinationPath + ".acquisition.json")
            throw Failure(ArtifactAcquisitionFailure.DestinationInvalid);
        foreach (var path in new[] { paths.DestinationPath, paths.PartialPath, paths.JournalPath,
            paths.JournalPath + ".pending", paths.LeasePath, paths.QuarantinePath })
        {
            ValidateAbsolutePath(path);
            if (!string.Equals(Path.GetFullPath(path), path, StringComparison.Ordinal) ||
                !string.Equals(Path.GetDirectoryName(path), RootPath, StringComparison.Ordinal))
                throw Failure(ArtifactAcquisitionFailure.DestinationInvalid);
            _ = HasRegularAttributes(path);
        }
    }

    internal static void ValidateDirectory(string root)
    {
        ValidateAbsolutePath(root);
        for (var directory = new DirectoryInfo(root); directory is not null; directory = directory.Parent)
        {
            var attributes = File.GetAttributes(directory.FullName);
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint |
                FileAttributes.Device)) != FileAttributes.Directory)
                throw Failure(ArtifactAcquisitionFailure.DestinationInvalid);
        }
        if (OperatingSystem.IsWindows() &&
            new DriveInfo(Path.GetPathRoot(root)!).DriveType == DriveType.Network)
            throw Failure(ArtifactAcquisitionFailure.DestinationInvalid);
        ArtifactAcquisitionFileIdentity.RequireLocalLinuxDirectory(root);
    }

    private static void ValidateAbsolutePath(string path)
    {
        AcquisitionGuard.Text(path, 1024, ArtifactAcquisitionFailure.DestinationInvalid);
        if (!Path.IsPathFullyQualified(path) || !OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
            throw Failure(ArtifactAcquisitionFailure.DestinationInvalid);
        if (OperatingSystem.IsWindows())
        {
            try { LocalSetupFileSystem.ValidateWindowsPath(path); }
            catch (SetupException error)
            { throw new ArtifactAcquisitionException(ArtifactAcquisitionFailure.DestinationInvalid, error); }
        }
    }

    private static bool HasRegularAttributes(string path)
    {
        FileAttributes attributes;
        try { attributes = File.GetAttributes(path); }
        catch (FileNotFoundException) { return false; }
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint |
            FileAttributes.Device)) != 0)
            throw Failure(ArtifactAcquisitionFailure.DestinationInvalid);
        return true;
    }

    private static FileStream CreateNew(string path) => new(path, new FileStreamOptions
    {
        Mode = FileMode.CreateNew,
        Access = FileAccess.Write,
        Share = FileShare.Read,
        BufferSize = 0,
        Options = FileOptions.Asynchronous | FileOptions.WriteThrough
    });

    private static ArtifactAcquisitionException Failure(ArtifactAcquisitionFailure failure) => new(failure);

    private static ArtifactAcquisitionException Map(Exception error) => new(error switch
    {
        UnauthorizedAccessException or SecurityException => ArtifactAcquisitionFailure.AccessDenied,
        ArgumentException or NotSupportedException => ArtifactAcquisitionFailure.DestinationInvalid,
        IOException io when (io.HResult & 0xffff) is 0x27 or 0x70 ||
            OperatingSystem.IsLinux() && (io.HResult & 0xffff) == 28 =>
            ArtifactAcquisitionFailure.DiskFull,
        _ => ArtifactAcquisitionFailure.StorageFailed
    }, error);

    private static bool Mappable(Exception error) => error is IOException or UnauthorizedAccessException or
        SecurityException or ArgumentException or NotSupportedException or SetupException or
        OverflowException or EntryPointNotFoundException or DllNotFoundException;

    private static T Io<T>(Func<T> action)
    {
        try { return action(); }
        catch (Exception error) when (Mappable(error)) { throw Map(error); }
    }

    private static async ValueTask<T> IoAsync<T>(Func<ValueTask<T>> action)
    {
        try { return await action().ConfigureAwait(false); }
        catch (Exception error) when (Mappable(error)) { throw Map(error); }
    }

    private static ValueTask IoAction(Action action)
    {
        Io(() => { action(); return true; });
        return ValueTask.CompletedTask;
    }

    private sealed class Lease(LocalArtifactAcquisitionStorage storage, ArtifactAcquisitionPaths paths,
        string identity, byte[] content) : IAsyncDisposable
    {
        private int disposed;

        internal void Validate()
        {
            if (Volatile.Read(ref disposed) != 0)
                throw Failure(ArtifactAcquisitionFailure.JournalChanged);
            storage.RequireFile(paths, paths.LeasePath, identity, content.Length,
                ArtifactAcquisitionFailure.JournalChanged);
            storage.ValidatePaths(paths);
            using var stream = ArtifactAcquisitionFileIdentity.OpenRead(paths.LeasePath);
            if (ArtifactAcquisitionFileIdentity.Read(stream.SafeFileHandle).Identity != identity)
                throw Failure(ArtifactAcquisitionFailure.JournalChanged);
            var current = new byte[content.Length];
            stream.ReadExactly(current);
            if (!current.AsSpan().SequenceEqual(content) || stream.ReadByte() != -1 ||
                ArtifactAcquisitionFileIdentity.Read(stream.SafeFileHandle).Identity != identity)
                throw Failure(ArtifactAcquisitionFailure.JournalChanged);
        }

        public ValueTask DisposeAsync() => IoAction(() =>
        {
            if (Volatile.Read(ref disposed) != 0) return;
            Validate();
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            // The exact physical marker and owner must still match. Foreign
            // replacements and abandoned markers are never removed or adopted.
            storage.ValidatePaths(paths);
            storage.RequireFile(paths, paths.LeasePath, identity, content.Length,
                ArtifactAcquisitionFailure.JournalChanged);
            File.Delete(paths.LeasePath);
            storage.leases.TryRemove(paths.LeasePath, out _);
            storage.ValidatePaths(paths);
            storage.directoryCommitter.Commit(storage.RootPath);
        });
    }

    private sealed class PartialWriter(LocalArtifactAcquisitionStorage storage,
        ArtifactAcquisitionPaths paths, FileStream stream, string identity) : IArtifactPartialWriter
    {
        public string Identity => identity;
        public long Position => stream.Position;

        private void Validate()
        {
            storage.RequireLease(paths);
            var state = ArtifactAcquisitionFileIdentity.Read(stream.SafeFileHandle);
            if (state.Identity != identity || state.Bytes != stream.Position)
                throw Failure(ArtifactAcquisitionFailure.PartialConflict);
            storage.RequireFile(paths, paths.PartialPath, identity, state.Bytes,
                ArtifactAcquisitionFailure.PartialConflict);
        }

        public async ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
        {
            await IoAsync(async () =>
            {
                Validate();
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                Validate();
                return true;
            }).ConfigureAwait(false);
        }

        public async ValueTask FlushToDiskAsync(CancellationToken cancellationToken)
        {
            await IoAsync(async () =>
            {
                Validate();
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                Validate();
                stream.Flush(true);
                cancellationToken.ThrowIfCancellationRequested();
                Validate();
                return true;
            }).ConfigureAwait(false);
        }

        public ValueTask DisposeAsync() => stream.DisposeAsync();
    }

    ValueTask<IAsyncDisposable> IArtifactAcquisitionMutationStorage.AcquireLeaseAsync(
        ArtifactAcquisitionPaths paths, Guid ownerId, CancellationToken ct) => AcquireLeaseAsync(paths, ownerId, ct);
    ValueTask IArtifactAcquisitionMutationStorage.ValidateLeaseAsync(
        ArtifactAcquisitionPaths paths, CancellationToken ct) => ValidateLeaseAsync(paths, ct);
    ValueTask<SetupFileSnapshot> IArtifactAcquisitionMutationStorage.WriteJournalAsync(
        ArtifactAcquisitionPaths paths, string? version, ReadOnlyMemory<byte> content, CancellationToken ct) =>
        WriteJournalAsync(paths, version, content, ct);
    ValueTask<IArtifactPartialWriter> IArtifactAcquisitionMutationStorage.OpenPartialAsync(
        ArtifactAcquisitionPaths paths, long offset, string? identity, CancellationToken ct) =>
        OpenPartialAsync(paths, offset, identity, ct);
    ValueTask IArtifactAcquisitionMutationStorage.FinalizeAsync(
        ArtifactAcquisitionPaths paths, string identity, long bytes, CancellationToken ct) =>
        FinalizeAsync(paths, identity, bytes, ct);
    ValueTask IArtifactAcquisitionMutationStorage.DeleteOwnedPartialAsync(
        ArtifactAcquisitionPaths paths, string identity, long bytes, CancellationToken ct) =>
        DeleteOwnedPartialAsync(paths, identity, bytes, ct);
    ValueTask<ArtifactAcquisitionFileSnapshot> IArtifactAcquisitionMutationStorage.QuarantineOwnedFinalAsync(
        ArtifactAcquisitionPaths paths, string identity, long bytes, string hash, CancellationToken ct) =>
        QuarantineOwnedFinalAsync(paths, identity, bytes, hash, ct);
}
