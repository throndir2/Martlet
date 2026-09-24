using System.Security.Cryptography;
using System.Text;
using Martlet.HostArtifacts;
using Microsoft.Win32.SafeHandles;

namespace Martlet.Host.Setup;

internal sealed class ArtifactImagePaths
{
    internal string Parent { get; }
    internal string Staging { get; }
    internal string Destination { get; }
    internal string Quarantine { get; }
    internal string Journal { get; }
    internal string Lease { get; }

    internal ArtifactImagePaths(string parent, ArtifactAcquisitionSelection selection)
    {
        Parent = parent;
        var name = "oci-" + FingerprintBuilder.Create("oci-batch-v1",
            selection.Fingerprint, selection.ImageContentInventory.Fingerprint);
        Destination = Path.Combine(parent, name);
        Staging = Destination + ".staging";
        Quarantine = Destination + ".quarantine";
        Journal = Destination + ".acquisition.json";
        Lease = Destination + ".lease";
    }

    internal static string DirectoryPath(string root, string key) => key switch
    {
        "blobs" => Path.Combine(root, "blobs"),
        "sha256" => Path.Combine(root, "blobs", "sha256"),
        "partials" => Path.Combine(root, "partials"),
        _ => throw new ArtifactAcquisitionException(ArtifactAcquisitionFailure.DestinationInvalid)
    };

    internal static string FilePath(string root, string key)
    {
        if (key == "index") return Path.Combine(root, "index.json");
        if (key == "layout") return Path.Combine(root, "oci-layout");
        var parts = key.Split(':');
        if (parts.Length != 2)
            throw new ArtifactAcquisitionException(ArtifactAcquisitionFailure.DestinationInvalid);
        AcquisitionGuard.Fingerprint(parts[1], ArtifactAcquisitionFailure.DestinationInvalid);
        return parts[0] switch
        {
            "blob" => Path.Combine(root, "blobs", "sha256", parts[1]),
            "partial" => Path.Combine(root, "partials", parts[1] + ".partial"),
            _ => throw new ArtifactAcquisitionException(ArtifactAcquisitionFailure.DestinationInvalid)
        };
    }
}

internal sealed record ArtifactImageStorageSnapshot(
    string ParentIdentity, long AvailableBytes, string? JournalVersion, ArtifactImageJournalDocument? Journal,
    string? LeaseIdentity, string? LeaseNonce, bool LeaseBusy, bool Pending,
    ArtifactImageTree? Staging, ArtifactImageTree? Published, ArtifactImageTree? Quarantine,
    bool Corrupt)
{
    internal string Fingerprint => FingerprintBuilder.Create(
        "oci-storage-v1", ParentIdentity, JournalVersion ?? "", LeaseIdentity ?? "", LeaseNonce ?? "",
        LeaseBusy.ToString(), Pending.ToString(), TreeFingerprint(Staging),
        TreeFingerprint(Published), TreeFingerprint(Quarantine), Corrupt.ToString());

    internal static string TreeFingerprint(ArtifactImageTree? tree) => tree is null ? "" :
        FingerprintBuilder.Create([tree.RootIdentity,
            .. tree.Directories.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .SelectMany(pair => new[] { pair.Key, pair.Value }),
            .. tree.Files.OrderBy(file => file.Key, StringComparer.Ordinal).SelectMany(file =>
                new[] { file.Key, file.Identity, file.Bytes.ToString(System.Globalization.CultureInfo.InvariantCulture), file.Sha256 })]);
}

public sealed partial class LocalArtifactAcquisitionStorage
{
    internal ArtifactImagePaths GetImagePaths(ArtifactAcquisitionSelection selection) => Io(() =>
    {
        var paths = new ArtifactImagePaths(RootPath, selection);
        ValidateImageParent(paths);
        return paths;
    });

    internal ValueTask<SetupFileSnapshot?> ReadImageJournalAsync(ArtifactImagePaths paths,
        CancellationToken cancellationToken) => IoAsync(() =>
        ArtifactOwnedJournalIO.ReadAsync(paths.Journal, ArtifactImageJournalCodec.MaximumBytes,
            () => ValidateImageParent(paths), cancellationToken));

    internal ValueTask<SetupFileSnapshot> WriteImageJournalAsync(ArtifactImagePaths paths,
        ArtifactImageJournalDocument document, string? expectedVersion, ImageLease lease,
        CancellationToken cancellationToken) => IoAsync(() =>
        ArtifactOwnedJournalIO.WriteAsync(paths.Journal, ArtifactImageJournalCodec.MaximumBytes,
            expectedVersion, ArtifactImageJournalCodec.Write(document), () => lease.Validate(),
            directoryCommitter, cancellationToken));

    internal ValueTask<ArtifactImageStorageSnapshot> InspectImageAsync(ArtifactImagePaths paths,
        ArtifactAcquisitionSelection selection, Func<CancellationToken, ValueTask> checkCurrent,
        CancellationToken cancellationToken, ImageLease? heldLease = null) => IoAsync<ArtifactImageStorageSnapshot>(async () =>
    {
        ValidateImageParent(paths);
        var parentIdentity = ArtifactAcquisitionFileIdentity.DirectoryIdentity(RootPath);
        var journalBytes = await ReadImageJournalAsync(paths, cancellationToken).ConfigureAwait(false);
        var document = journalBytes is null ? null : ArtifactImageJournalCodec.Read(journalBytes.Content);
        if (document is not null)
        {
            ArtifactImageJournalCodec.ValidateBinding(document, selection);
            if (document.ParentIdentity != parentIdentity) throw Failure(ArtifactAcquisitionFailure.DestinationInvalid);
        }
        var pending = ArtifactOwnedJournalIO.InspectFile(paths.Journal + ".pending") is not null;
        var marker = ArtifactOwnedJournalIO.InspectFile(paths.Lease);
        var busy = false;
        string? nonce = null;
        if (marker is not null)
        {
            if (heldLease is not null)
            {
                heldLease.Validate();
                nonce = heldLease.Nonce;
            }
            else
            {
                using var probe = ArtifactAcquisitionFileIdentity.OpenImageLease(paths.Lease, create: false);
                busy = !ArtifactAcquisitionFileIdentity.TryLockImageLease(probe.SafeFileHandle);
                if (!busy)
                {
                    try { nonce = ReadImageLeaseMarker(probe, marker.Value.Identity); }
                    finally { ArtifactAcquisitionFileIdentity.UnlockImageLease(probe.SafeFileHandle); }
                }
            }
            if (document is null || marker.Value.Identity != document.LeaseIdentity ||
                !busy && nonce != document.LeaseNonce)
                throw Failure(ArtifactAcquisitionFailure.ImageLeaseUnavailable);
        }
        else if (document is not null) throw Failure(ArtifactAcquisitionFailure.ImageLeaseUnavailable);
        if (busy)
            return new(parentIdentity, freeSpaceProbe.GetAvailableBytes(RootPath), journalBytes?.Version,
                document, marker?.Identity, null, true, pending, null, null, null, false);
        var stageExists = ImageDirectoryExists(paths.Staging);
        var finalExists = ImageDirectoryExists(paths.Destination);
        var quarantineExists = ImageDirectoryExists(paths.Quarantine);
        if (stageExists && finalExists || document is null && (stageExists || finalExists || quarantineExists))
            throw Failure(ArtifactAcquisitionFailure.FinalConflict);
        ArtifactImageTree? stage = null, final = null, quarantine = null;
        var corrupt = false;
        if (document is not null)
        {
            if (quarantineExists)
            {
                var expectedQuarantine = document.Quarantine ??
                    (document.State == ArtifactImageJournalState.Quarantining ? document.RecoveryTree : null)
                    ?? throw Failure(ArtifactAcquisitionFailure.FinalConflict);
                quarantine = await InspectImageTreeAsync(paths, paths.Quarantine, expectedQuarantine,
                    checkCurrent, cancellationToken).ConfigureAwait(false);
                if (ArtifactImageStorageSnapshot.TreeFingerprint(quarantine) !=
                    ArtifactImageStorageSnapshot.TreeFingerprint(expectedQuarantine))
                    throw Failure(ArtifactAcquisitionFailure.FinalConflict);
            }
            else if (document.Quarantine is not null) throw Failure(ArtifactAcquisitionFailure.FinalConflict);
            var root = finalExists ? paths.Destination : paths.Staging;
            if (stageExists || finalExists)
            {
                if (document.RootIdentity is null || finalExists &&
                    document.State is not (ArtifactImageJournalState.Publishing or ArtifactImageJournalState.Published or
                        ArtifactImageJournalState.Quarantining))
                    throw Failure(ArtifactAcquisitionFailure.FinalConflict);
                var expected = BuildImageTreeEvidence(root, document);
                var actual = await InspectImageTreeAsync(paths, root, expected, checkCurrent, cancellationToken).ConfigureAwait(false);
                corrupt = ArtifactImageStorageSnapshot.TreeFingerprint(actual) != ArtifactImageStorageSnapshot.TreeFingerprint(expected);
                if (finalExists) final = actual; else stage = actual;
            }
            else if (document.RootIdentity is not null &&
                !(document.State == ArtifactImageJournalState.Quarantining && quarantine is not null))
                throw Failure(ArtifactAcquisitionFailure.FinalConflict);
        }
        ValidateImageParent(paths, parentIdentity);
        return new(parentIdentity, freeSpaceProbe.GetAvailableBytes(RootPath), journalBytes?.Version, document,
            marker?.Identity, nonce, busy, pending, stage, final, quarantine, corrupt);
    });

    private ArtifactImageTree BuildImageTreeEvidence(string root, ArtifactImageJournalDocument document)
    {
        var files = new List<ArtifactImageOwnedFile>(document.PublicationFiles);
        foreach (var content in document.Contents.Where(content => content.Identity is not null))
        {
            var blobKey = "blob:" + content.Digest[7..];
            var partialKey = "partial:" + content.Digest[7..];
            var blob = ExistingImageFile(root, blobKey);
            var partial = ExistingImageFile(root, partialKey);
            if (blob is not null && partial is not null) throw Failure(ArtifactAcquisitionFailure.FinalConflict);
            var key = content.State == ArtifactAcquisitionJournalState.Finalized ||
                content.State == ArtifactAcquisitionJournalState.Verified && blob is not null ? blobKey : partialKey;
            files.Add(new() { Key = key, Identity = content.Identity!, Bytes = content.Bytes, Sha256 = content.Sha256! });
        }
        var directories = document.Directories;
        if (directories.ContainsKey("partials") &&
            document.Contents.All(row => row.State == ArtifactAcquisitionJournalState.Finalized) &&
            !ImageDirectoryExists(ArtifactImagePaths.DirectoryPath(root, "partials")))
        {
            directories = new(directories, StringComparer.Ordinal);
            directories.Remove("partials");
        }
        return new() { RootIdentity = document.RootIdentity!, Directories = directories, Files = files.ToArray() };
    }

    internal ValueTask<ArtifactImageTree> InspectImageTreeAsync(ArtifactImagePaths paths, string root,
        ArtifactImageTree expected, Func<CancellationToken, ValueTask> checkCurrent,
        CancellationToken cancellationToken) => IoAsync(async () =>
    {
        ValidateImageRoot(paths, root, expected.RootIdentity);
        foreach (var directory in expected.Directories)
        {
            var path = ArtifactImagePaths.DirectoryPath(root, directory.Key);
            if (!ImageDirectoryExists(path) || ArtifactAcquisitionFileIdentity.DirectoryIdentity(path) != directory.Value)
                throw Failure(ArtifactAcquisitionFailure.DestinationInvalid);
        }
        var expectedPaths = expected.Directories.Keys.Select(key => ArtifactImagePaths.DirectoryPath(root, key))
            .Concat(expected.Files.Select(file => ArtifactImagePaths.FilePath(root, file.Key)))
            .ToHashSet(StringComparer.Ordinal);
        foreach (var directory in new[] { root }.Concat(expected.Directories.Keys.Select(key => ArtifactImagePaths.DirectoryPath(root, key))))
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory).Take(268))
                if (!expectedPaths.Contains(entry)) throw Failure(ArtifactAcquisitionFailure.FinalConflict);
        }
        var files = new List<ArtifactImageOwnedFile>();
        var observations = new List<(ArtifactImageOwnedFile File, ArtifactAcquisitionFileMetadata Metadata)>();
        var hashBudget = checked(expected.Files.Sum(file => file.Bytes) + ArtifactAcquisitionCoordinator.DefaultFreeSpaceReserveBytes);
        long observedBytes = 0;
        foreach (var file in expected.Files.OrderBy(file => file.Key, StringComparer.Ordinal))
        {
            var actual = ExistingImageFile(root, file.Key);
            if (actual is null) continue;
            if (actual.Value.Identity != file.Identity)
                throw Failure(ArtifactAcquisitionFailure.FinalConflict);
            if (actual.Value.Bytes > checked(file.Bytes + ArtifactAcquisitionCoordinator.DefaultFreeSpaceReserveBytes))
                throw Failure(ArtifactAcquisitionFailure.FinalConflict);
            observedBytes = checked(observedBytes + actual.Value.Bytes);
            if (observedBytes > hashBudget) throw Failure(ArtifactAcquisitionFailure.FinalConflict);
            observations.Add((file, actual.Value));
        }
        foreach (var (file, actual) in observations)
        {
            var path = ArtifactImagePaths.FilePath(root, file.Key);
            var hash = await ArtifactAcquisitionFileIdentity.HashOwnedFileAsync(path, actual, async token =>
            {
                ValidateImageRoot(paths, root, expected.RootIdentity);
                await checkCurrent(token).ConfigureAwait(false);
            }, ArtifactAcquisitionFailure.FinalConflict, cancellationToken, count =>
            {
                hashBudget -= count;
                if (hashBudget < 0) throw Failure(ArtifactAcquisitionFailure.FinalConflict);
            }).ConfigureAwait(false);
            files.Add(file with { Bytes = actual.Bytes, Sha256 = hash });
        }
        ValidateImageRoot(paths, root, expected.RootIdentity);
        foreach (var directory in expected.Directories)
            if (ArtifactAcquisitionFileIdentity.DirectoryIdentity(ArtifactImagePaths.DirectoryPath(root, directory.Key)) != directory.Value)
                throw Failure(ArtifactAcquisitionFailure.DestinationInvalid);
        return new ArtifactImageTree
        {
            RootIdentity = expected.RootIdentity,
            Directories = new(expected.Directories, StringComparer.Ordinal),
            Files = files.ToArray()
        };
    });

    internal ValueTask<ImageLease> AcquireImageLeaseAsync(ArtifactImagePaths paths,
        ArtifactImageStorageSnapshot preview, CancellationToken cancellationToken) => IoAsync(async () =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateImageParent(paths, preview.ParentIdentity);
        var parentHandle = ArtifactAcquisitionFileIdentity.OpenDirectory(RootPath);
        FileStream? stream = null;
        var locked = false;
        try
        {
            stream = ArtifactAcquisitionFileIdentity.OpenImageLease(paths.Lease, create: preview.Journal is null);
            locked = ArtifactAcquisitionFileIdentity.TryLockImageLease(stream.SafeFileHandle);
            if (!locked) throw Failure(ArtifactAcquisitionFailure.ImageLeaseUnavailable);
            var identity = ArtifactAcquisitionFileIdentity.Read(stream.SafeFileHandle).Identity;
            string nonce;
            if (preview.Journal is null)
            {
                nonce = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
                await stream.WriteAsync(Encoding.ASCII.GetBytes(nonce), cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(true);
                directoryCommitter.Commit(RootPath);
            }
            else
            {
                if (identity != preview.LeaseIdentity) throw Failure(ArtifactAcquisitionFailure.ImageLeaseUnavailable);
                nonce = ReadImageLeaseMarker(stream, identity);
                if (nonce != preview.LeaseNonce) throw Failure(ArtifactAcquisitionFailure.ImageLeaseUnavailable);
            }
            return new ImageLease(this, paths, stream, parentHandle, preview.ParentIdentity, identity, nonce);
        }
        catch
        {
            try
            {
                if (locked && stream is not null) ArtifactAcquisitionFileIdentity.UnlockImageLease(stream.SafeFileHandle);
            }
            finally { stream?.Dispose(); parentHandle.Dispose(); }
            throw;
        }
    });

    internal string CreateImageDirectory(ArtifactImagePaths paths, string path, ImageLease lease) => Io(() =>
    {
        lease.Validate();
        if (path != paths.Staging && path != ArtifactImagePaths.DirectoryPath(paths.Staging, "blobs") &&
            path != ArtifactImagePaths.DirectoryPath(paths.Staging, "sha256") &&
            path != ArtifactImagePaths.DirectoryPath(paths.Staging, "partials"))
            throw Failure(ArtifactAcquisitionFailure.DestinationInvalid);
        ValidateDirectory(Path.GetDirectoryName(path)!);
        ArtifactAcquisitionFileIdentity.CreateImageDirectoryExclusive(path);
        var identity = ArtifactAcquisitionFileIdentity.DirectoryIdentity(path);
        directoryCommitter.Commit(Path.GetDirectoryName(path)!);
        lease.Validate();
        if (ArtifactAcquisitionFileIdentity.DirectoryIdentity(path) != identity)
            throw Failure(ArtifactAcquisitionFailure.DestinationInvalid);
        return identity;
    });

    internal FileStream OpenImagePartial(ArtifactImagePaths paths, ArtifactImageJournalDocument document,
        ArtifactImageContentProgress content, ImageLease lease) => Io(() =>
    {
        ValidateImageWorkingTree(paths, document, lease);
        var path = ArtifactImagePaths.FilePath(paths.Staging, "partial:" + content.Digest[7..]);
        if (content.Identity is { } identity &&
            ExistingImageFile(paths.Staging, "partial:" + content.Digest[7..]) !=
                new ArtifactAcquisitionFileMetadata(identity, content.Bytes))
            throw Failure(ArtifactAcquisitionFailure.PartialConflict);
        var stream = new FileStream(path, new FileStreamOptions
        {
            Mode = content.Identity is null ? FileMode.CreateNew : FileMode.Open,
            Access = FileAccess.ReadWrite, Share = FileShare.Read,
            Options = FileOptions.Asynchronous | FileOptions.WriteThrough, BufferSize = 0
        });
        try
        {
            var actual = ArtifactAcquisitionFileIdentity.Read(stream.SafeFileHandle);
            if (actual.Bytes != content.Bytes || content.Identity is not null && actual.Identity != content.Identity)
                throw Failure(ArtifactAcquisitionFailure.PartialConflict);
            return stream;
        }
        catch { stream.Dispose(); throw; }
    });

    internal void FinalizeImageBlob(ArtifactImagePaths paths, ArtifactImageJournalDocument document,
        ArtifactImageContentProgress content, ImageLease lease) => Io(() =>
    {
        ValidateImageWorkingTree(paths, document, lease);
        var partial = ArtifactImagePaths.FilePath(paths.Staging, "partial:" + content.Digest[7..]);
        var destination = ArtifactImagePaths.FilePath(paths.Staging, "blob:" + content.Digest[7..]);
        if (ExistingImageFile(paths.Staging, "partial:" + content.Digest[7..]) !=
            new ArtifactAcquisitionFileMetadata(content.Identity!, content.Bytes) ||
            ExistingImageFile(paths.Staging, "blob:" + content.Digest[7..]) is not null)
            throw Failure(ArtifactAcquisitionFailure.FinalConflict);
        File.Move(partial, destination, overwrite: false);
        directoryCommitter.Commit(Path.GetDirectoryName(partial)!);
        directoryCommitter.Commit(Path.GetDirectoryName(destination)!);
        ValidateImageWorkingTree(paths, document, lease);
        if (ExistingImageFile(paths.Staging, "blob:" + content.Digest[7..]) !=
            new ArtifactAcquisitionFileMetadata(content.Identity!, content.Bytes))
            throw Failure(ArtifactAcquisitionFailure.FinalConflict);
        return true;
    });

    internal ValueTask<ArtifactImageOwnedFile> WriteImagePublicationAsync(ArtifactImagePaths paths,
        ArtifactImageJournalDocument document, string key, ReadOnlyMemory<byte> content,
        ImageLease lease, CancellationToken cancellationToken) => IoAsync(async () =>
    {
        ValidateImageWorkingTree(paths, document, lease);
        if (key is not ("index" or "layout") || content.Length > 65_536)
            throw Failure(ArtifactAcquisitionFailure.InvalidPlan);
        var path = ArtifactImagePaths.FilePath(paths.Staging, key);
        string identity;
        await using (var stream = CreateNew(path))
        {
            identity = ArtifactAcquisitionFileIdentity.Read(stream.SafeFileHandle).Identity;
            await stream.WriteAsync(content, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            stream.Flush(true);
        }
        directoryCommitter.Commit(paths.Staging);
        ValidateImageWorkingTree(paths, document, lease);
        return new ArtifactImageOwnedFile
        {
            Key = key, Identity = identity, Bytes = content.Length,
            Sha256 = Convert.ToHexStringLower(SHA256.HashData(content.Span))
        };
    });

    internal void RemoveImagePartialsDirectory(ArtifactImagePaths paths,
        ArtifactImageJournalDocument document, ImageLease lease) => Io(() =>
    {
        ValidateImageWorkingTree(paths, document, lease);
        var path = ArtifactImagePaths.DirectoryPath(paths.Staging, "partials");
        if (!document.Directories.TryGetValue("partials", out var identity) ||
            ArtifactAcquisitionFileIdentity.DirectoryIdentity(path) != identity ||
            Directory.EnumerateFileSystemEntries(path).Any())
            throw Failure(ArtifactAcquisitionFailure.PartialConflict);
        Directory.Delete(path, recursive: false);
        directoryCommitter.Commit(paths.Staging);
        return true;
    });

    internal void MoveImageTree(ArtifactImagePaths paths, string source, string destination,
        ArtifactImageTree tree, ImageLease lease) => Io(() =>
    {
        lease.Validate();
        if (!(source == paths.Staging && destination == paths.Destination ||
            (source == paths.Staging || source == paths.Destination) && destination == paths.Quarantine))
            throw Failure(ArtifactAcquisitionFailure.DestinationInvalid);
        ValidateImageRoot(paths, source, tree.RootIdentity);
        if (ImageDirectoryExists(destination)) throw Failure(ArtifactAcquisitionFailure.FinalConflict);
        directoryCommitter.Commit(source);
        Directory.Move(source, destination);
        directoryCommitter.Commit(RootPath);
        lease.Validate();
        ValidateImageRoot(paths, destination, tree.RootIdentity);
        if (ImageDirectoryExists(source)) throw Failure(ArtifactAcquisitionFailure.ImagePublicationUncertain);
        return true;
    });

    internal long ImageAvailableBytes(ArtifactImagePaths paths) => Io(() =>
    {
        ValidateImageParent(paths);
        return freeSpaceProbe.GetAvailableBytes(RootPath);
    });

    internal void ValidateImageWorkingTree(ArtifactImagePaths paths,
        ArtifactImageJournalDocument document, ImageLease lease)
    {
        lease.Validate();
        ValidateImageRoot(paths, paths.Staging, document.RootIdentity!);
        foreach (var directory in document.Directories)
            if (ArtifactAcquisitionFileIdentity.DirectoryIdentity(ArtifactImagePaths.DirectoryPath(paths.Staging, directory.Key)) != directory.Value)
                throw Failure(ArtifactAcquisitionFailure.DestinationInvalid);
    }

    internal void ValidateImageJournalLocation(ArtifactImagePaths paths, ArtifactImageJournalDocument document)
    {
        ValidateImageParent(paths, document.ParentIdentity);
        if (document.RootIdentity is null) return;
        var stage = ImageDirectoryExists(paths.Staging);
        var final = ImageDirectoryExists(paths.Destination);
        if (stage && final) throw Failure(ArtifactAcquisitionFailure.FinalConflict);
        var root = stage ? paths.Staging : final ? paths.Destination :
            document.State == ArtifactImageJournalState.Quarantining ? paths.Quarantine :
            throw Failure(ArtifactAcquisitionFailure.FinalConflict);
        ValidateImageRoot(paths, root, document.RootIdentity);
        foreach (var directory in document.Directories)
            if (ArtifactAcquisitionFileIdentity.DirectoryIdentity(ArtifactImagePaths.DirectoryPath(root, directory.Key)) != directory.Value)
                throw Failure(ArtifactAcquisitionFailure.DestinationInvalid);
    }

    private void ValidateImageParent(ArtifactImagePaths paths, string? identity = null)
    {
        ValidateDirectory(RootPath);
        if (paths.Parent != RootPath) throw Failure(ArtifactAcquisitionFailure.DestinationInvalid);
        foreach (var path in new[] { paths.Staging, paths.Destination, paths.Quarantine, paths.Journal, paths.Lease })
        {
            ValidateAbsolutePath(path);
            if (Path.GetDirectoryName(path) != RootPath || Path.GetFullPath(path) != path)
                throw Failure(ArtifactAcquisitionFailure.DestinationInvalid);
        }
        if (identity is not null && ArtifactAcquisitionFileIdentity.DirectoryIdentity(RootPath) != identity)
            throw Failure(ArtifactAcquisitionFailure.DestinationInvalid);
    }

    private void ValidateImageRoot(ArtifactImagePaths paths, string root, string identity)
    {
        ValidateImageParent(paths);
        if (root != paths.Staging && root != paths.Destination && root != paths.Quarantine)
            throw Failure(ArtifactAcquisitionFailure.DestinationInvalid);
        ValidateDirectory(root);
        if (ArtifactAcquisitionFileIdentity.DirectoryIdentity(root) != identity)
            throw Failure(ArtifactAcquisitionFailure.DestinationInvalid);
    }

    private static bool ImageDirectoryExists(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device)) != FileAttributes.Directory)
                throw Failure(ArtifactAcquisitionFailure.DestinationInvalid);
            return true;
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    internal static ArtifactAcquisitionFileMetadata? ExistingImageFile(string root, string key)
    {
        var path = ArtifactImagePaths.FilePath(root, key);
        if (!ImageDirectoryExists(Path.GetDirectoryName(path)!)) return null;
        return ArtifactOwnedJournalIO.InspectFile(path);
    }

    private static string ReadImageLeaseMarker(FileStream stream, string identity)
    {
        var metadata = ArtifactAcquisitionFileIdentity.Read(stream.SafeFileHandle);
        if (metadata.Identity != identity || metadata.Bytes != 64)
            throw Failure(ArtifactAcquisitionFailure.ImageLeaseUnavailable);
        stream.Position = 0;
        var bytes = new byte[64];
        stream.ReadExactly(bytes);
        var nonce = Encoding.ASCII.GetString(bytes);
        AcquisitionGuard.Fingerprint(nonce, ArtifactAcquisitionFailure.ImageLeaseUnavailable);
        return nonce;
    }

    internal sealed class ImageLease(LocalArtifactAcquisitionStorage owner, ArtifactImagePaths paths,
        FileStream stream, SafeFileHandle parentHandle, string parentIdentity, string identity, string nonce) : IDisposable
    {
        internal string Identity => identity;
        internal string Nonce => nonce;
        private bool disposed;
        internal void Validate()
        {
            if (disposed) throw Failure(ArtifactAcquisitionFailure.ImageLeaseUnavailable);
            owner.ValidateImageParent(paths, parentIdentity);
            if (ArtifactAcquisitionFileIdentity.ReadDirectory(parentHandle) != parentIdentity ||
                ArtifactOwnedJournalIO.InspectFile(paths.Lease) != new ArtifactAcquisitionFileMetadata(identity, 64) ||
                ReadImageLeaseMarker(stream, identity) != nonce)
                throw Failure(ArtifactAcquisitionFailure.ImageLeaseUnavailable);
        }
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            try { ArtifactAcquisitionFileIdentity.UnlockImageLease(stream.SafeFileHandle); }
            finally { stream.Dispose(); parentHandle.Dispose(); }
        }
    }
}
