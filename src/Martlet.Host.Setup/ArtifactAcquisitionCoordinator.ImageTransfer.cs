using System.Security.Cryptography;
using System.Text.Json;
using Martlet.HostArtifacts;

namespace Martlet.Host.Setup;

public sealed partial class ArtifactAcquisitionCoordinator
{
    private async ValueTask PrepareImageTreeAsync(ArtifactImageAcquisitionPlan plan, ImageRunCursor cursor,
        LocalArtifactAcquisitionStorage.ImageLease lease, CancellationToken cancellationToken)
    {
        var local = ImageStorage();
        if (cursor.Document.RootIdentity is null)
        {
            await RequireImageCurrentAsync(plan, cursor, lease, cancellationToken).ConfigureAwait(false);
            cursor.Document = cursor.Document with { RootIdentity = local.CreateImageDirectory(plan.Paths, plan.Paths.Staging, lease) };
            await PersistImageAsync(plan, cursor, lease, cancellationToken).ConfigureAwait(false);
        }
        foreach (var key in new[] { "blobs", "sha256", "partials" })
        {
            if (cursor.Document.Directories.ContainsKey(key)) continue;
            if (key == "partials" && cursor.Document.Contents.All(row => row.State == ArtifactAcquisitionJournalState.Finalized))
                continue;
            await RequireImageCurrentAsync(plan, cursor, lease, cancellationToken).ConfigureAwait(false);
            var identity = local.CreateImageDirectory(plan.Paths, ArtifactImagePaths.DirectoryPath(plan.Paths.Staging, key), lease);
            var directories = new Dictionary<string, string>(cursor.Document.Directories, StringComparer.Ordinal) { [key] = identity };
            cursor.Document = cursor.Document with { Directories = directories };
            await PersistImageAsync(plan, cursor, lease, cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask AcquireImageContentAsync(ArtifactImageAcquisitionPlan plan, ImageRunCursor cursor,
        LocalArtifactAcquisitionStorage.ImageLease lease, ArtifactImageContent content,
        HttpsArtifactImageTransport transport, ArtifactImageRequestBudget budget, CancellationToken cancellationToken)
    {
        var local = ImageStorage();
        var progress = cursor.Document.Contents.Single(row => row.Digest == content.Digest);
        await RequireImageCurrentAsync(plan, cursor, lease, cancellationToken).ConfigureAwait(false);
        local.ValidateImageWorkingTree(plan.Paths, cursor.Document, lease);
        if (progress.State == ArtifactAcquisitionJournalState.Finalized) return;
        if (progress.State == ArtifactAcquisitionJournalState.Verified &&
            LocalArtifactAcquisitionStorage.ExistingImageFile(plan.Paths.Staging, "blob:" + content.Digest[7..]) is not null)
        {
            await VerifyOwnedImageContentAsync(plan, cursor, lease, plan.Paths.Staging, content, cancellationToken).ConfigureAwait(false);
            ReplaceImageProgress(cursor, progress with { State = ArtifactAcquisitionJournalState.Finalized });
            await PersistImageAsync(plan, cursor, lease, cancellationToken).ConfigureAwait(false);
            return;
        }
        var remaining = plan.KnownContentBytes - cursor.Document.Contents.Sum(row => row.Bytes);
        if (local.ImageAvailableBytes(plan.Paths) < checked(remaining + plan.ReserveBytes + ArtifactImageAcquisitionPlan.MetadataAllowanceBytes))
            throw new ArtifactAcquisitionException(ArtifactAcquisitionFailure.FreeSpaceInsufficient);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        FileStream? writer = null;
        ArtifactImageDownloadResponse? download = null;
        var canCheckpoint = false;
        try
        {
            if (progress.Identity is not null)
            {
                writer = local.OpenImagePartial(plan.Paths, cursor.Document, progress, lease);
                var buffer = new byte[65_536];
                long scanned = 0;
                while (scanned < progress.Bytes)
                {
                    await RequireImageCurrentAsync(plan, cursor, lease, cancellationToken).ConfigureAwait(false);
                    var read = await writer.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, progress.Bytes - scanned)),
                        cancellationToken).ConfigureAwait(false);
                    if (read == 0) throw new ArtifactAcquisitionException(ArtifactAcquisitionFailure.ResumeStateMismatch);
                    hash.AppendData(buffer, 0, read);
                    scanned += read;
                }
                if (Convert.ToHexStringLower(hash.GetCurrentHash()) != progress.Sha256 ||
                    ArtifactAcquisitionFileIdentity.Read(writer.SafeFileHandle) != new ArtifactAcquisitionFileMetadata(progress.Identity, progress.Bytes))
                    throw new ArtifactAcquisitionException(ArtifactAcquisitionFailure.ResumeStateMismatch);
                canCheckpoint = true;
            }
            if (progress.Bytes < content.ExpectedBytes)
            {
                await ReportImageAsync(plan, content.ImageIds[0], ArtifactAcquisitionProgressPhase.Connecting,
                    cursor, cancellationToken).ConfigureAwait(false);
                download = await transport.SendAsync(content, progress.Bytes, budget,
                    token => RequireImageCurrentAsync(plan, cursor, lease, token), cancellationToken).ConfigureAwait(false);
                if (writer is null)
                {
                    writer = local.OpenImagePartial(plan.Paths, cursor.Document, progress, lease);
                    progress = progress with
                    {
                        State = ArtifactAcquisitionJournalState.Downloading,
                        Identity = ArtifactAcquisitionFileIdentity.Read(writer.SafeFileHandle).Identity,
                        Sha256 = Convert.ToHexStringLower(hash.GetCurrentHash())
                    };
                    canCheckpoint = true;
                }
                progress = progress with
                {
                    State = ArtifactAcquisitionJournalState.Downloading,
                    MediaType = content.Kind is ArtifactImageContentKind.Index or ArtifactImageContentKind.Manifest ? download.MediaType : null
                };
                ReplaceImageProgress(cursor, progress);
                await PersistImageAsync(plan, cursor, lease, cancellationToken).ConfigureAwait(false);
                writer.Position = progress.Bytes;
                var checkpoint = writer.Position;
                await using var body = await download.Response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                await ArtifactContentTransfer.CopyAsync(body, content.ExpectedBytes!.Value, progress.Bytes,
                    token => RequireImageCurrentAsync(plan, cursor, lease, token), async (bytes, token) =>
                    {
                        local.ValidateImageWorkingTree(plan.Paths, cursor.Document, lease);
                        if (ArtifactAcquisitionFileIdentity.Read(writer.SafeFileHandle) !=
                            new ArtifactAcquisitionFileMetadata(progress.Identity!, writer.Position))
                            throw new ArtifactAcquisitionException(ArtifactAcquisitionFailure.PartialConflict);
                        if (local.ImageAvailableBytes(plan.Paths) < checked(plan.ReserveBytes + bytes.Length))
                            throw new ArtifactAcquisitionException(ArtifactAcquisitionFailure.FreeSpaceInsufficient);
                        await writer.WriteAsync(bytes, token).ConfigureAwait(false);
                        hash.AppendData(bytes.Span);
                        if (writer.Position - checkpoint >= JournalCheckpointBytes)
                        {
                            await writer.FlushAsync(token).ConfigureAwait(false);
                            writer.Flush(true);
                            progress = progress with { Bytes = writer.Position, Sha256 = Convert.ToHexStringLower(hash.GetCurrentHash()) };
                            ReplaceImageProgress(cursor, progress);
                            await PersistImageAsync(plan, cursor, lease, token).ConfigureAwait(false);
                            checkpoint = writer.Position;
                        }
                        await ReportImageAsync(plan, content.ImageIds[0], ArtifactAcquisitionProgressPhase.Downloading,
                            cursor, token, writer.Position - progress.Bytes).ConfigureAwait(false);
                    }, cancellationToken, budget.ContentWindow, budget.ReadContent).ConfigureAwait(false);
                if (download.Response.TrailingHeaders.Any())
                    throw new ArtifactAcquisitionException(ArtifactAcquisitionFailure.ResponseInvalid);
                await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                writer.Flush(true);
                progress = progress with { Bytes = writer.Position, Sha256 = Convert.ToHexStringLower(hash.GetCurrentHash()) };
                ReplaceImageProgress(cursor, progress);
                await PersistImageAsync(plan, cursor, lease, cancellationToken).ConfigureAwait(false);
            }
            if (progress.Bytes != content.ExpectedBytes)
                throw new ArtifactAcquisitionException(ArtifactAcquisitionFailure.ContentLengthMismatch);
            writer?.Dispose();
            writer = null;
            await ReportImageAsync(plan, content.ImageIds[0], ArtifactAcquisitionProgressPhase.Verifying,
                cursor, cancellationToken).ConfigureAwait(false);
            var path = ArtifactImagePaths.FilePath(plan.Paths.Staging, "partial:" + content.Digest[7..]);
            var actual = await ArtifactAcquisitionFileIdentity.HashOwnedFileAsync(path,
                new(progress.Identity!, progress.Bytes), token => RequireImageCurrentAsync(plan, cursor, lease, token),
                ArtifactAcquisitionFailure.PartialConflict, cancellationToken).ConfigureAwait(false);
            if (actual != content.Digest[7..])
                throw new ArtifactAcquisitionException(ArtifactAcquisitionFailure.IntegrityMismatch);
            progress = progress with { State = ArtifactAcquisitionJournalState.Verified, Sha256 = actual };
            ReplaceImageProgress(cursor, progress);
            await PersistImageAsync(plan, cursor, lease, cancellationToken).ConfigureAwait(false);
            await RequireImageCurrentAsync(plan, cursor, lease, cancellationToken).ConfigureAwait(false);
            local.FinalizeImageBlob(plan.Paths, cursor.Document, progress, lease);
            ReplaceImageProgress(cursor, progress with { State = ArtifactAcquisitionJournalState.Finalized });
            await PersistImageAsync(plan, cursor, lease, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is OperationCanceledException or ArtifactAcquisitionException or IOException)
        {
            if (canCheckpoint && writer is not null && progress.Identity is not null &&
                ArtifactAcquisitionFileIdentity.Read(writer.SafeFileHandle).Identity == progress.Identity)
            {
                await writer.FlushAsync(CancellationToken.None).ConfigureAwait(false);
                writer.Flush(true);
                progress = progress with { Bytes = writer.Position, Sha256 = Convert.ToHexStringLower(hash.GetCurrentHash()) };
                ReplaceImageProgress(cursor, progress);
            }
            throw;
        }
        finally { writer?.Dispose(); download?.Dispose(); }
    }

    private async ValueTask<Dictionary<string, VerifiedImageManifest>> VerifyImageGraphAsync(
        ArtifactImageAcquisitionPlan plan, ImageRunCursor cursor, LocalArtifactAcquisitionStorage.ImageLease lease,
        string root, CancellationToken cancellationToken)
    {
        var graph = new Dictionary<string, VerifiedImageManifest>(StringComparer.Ordinal);
        foreach (var image in plan.Selection.ImageCandidates)
        {
            var manifestContent = plan.Selection.ImageContentInventory.Contents.Single(content => content.Digest == image.Digest);
            var bytes = await ReadOwnedImageMetadataAsync(plan, cursor, lease, root, manifestContent, cancellationToken).ConfigureAwait(false);
            var manifest = ArtifactImageMetadata.VerifyManifest(image, bytes,
                cursor.Document.Contents.Single(content => content.Digest == image.Digest).MediaType);
            if (image.IndexDigest is { } indexDigest)
            {
                var index = plan.Selection.ImageContentInventory.Contents.Single(content => content.Digest == indexDigest);
                var indexBytes = await ReadOwnedImageMetadataAsync(plan, cursor, lease, root, index, cancellationToken).ConfigureAwait(false);
                var selectedMediaType = ArtifactImageMetadata.VerifyIndex(image, indexBytes,
                    cursor.Document.Contents.Single(content => content.Digest == indexDigest).MediaType);
                if (selectedMediaType != manifest.MediaType)
                    throw new ArtifactAcquisitionException(ArtifactAcquisitionFailure.ImageInventoryMismatch);
            }
            var configuration = plan.Selection.ImageContentInventory.Contents.Single(content => content.Digest == manifest.ConfigurationDigest);
            var configurationBytes = await ReadOwnedImageMetadataAsync(plan, cursor, lease, root, configuration, cancellationToken).ConfigureAwait(false);
            ArtifactImageMetadata.VerifyConfiguration(image, configurationBytes);
            graph.Add(image.ArtifactId, manifest);
        }
        return graph;
    }

    private async ValueTask VerifyAcquiredImageMetadataAsync(ArtifactImageAcquisitionPlan plan, ImageRunCursor cursor,
        LocalArtifactAcquisitionStorage.ImageLease lease, ArtifactImageContent content, CancellationToken cancellationToken)
    {
        var bytes = await ReadOwnedImageMetadataAsync(plan, cursor, lease, plan.Paths.Staging, content, cancellationToken).ConfigureAwait(false);
        var media = cursor.Document.Contents.Single(row => row.Digest == content.Digest).MediaType;
        foreach (var image in plan.Selection.ImageCandidates.Where(image => content.ImageIds.Contains(image.ArtifactId)))
        {
            if (content.Kind == ArtifactImageContentKind.Index)
                _ = ArtifactImageMetadata.VerifyIndex(image, bytes, media);
            else if (content.Kind == ArtifactImageContentKind.Manifest)
            {
                var manifest = ArtifactImageMetadata.VerifyManifest(image, bytes, media);
                if (image.IndexDigest is { } indexDigest)
                {
                    var index = plan.Selection.ImageContentInventory.Contents.Single(item => item.Digest == indexDigest);
                    var indexBytes = await ReadOwnedImageMetadataAsync(plan, cursor, lease, plan.Paths.Staging, index, cancellationToken).ConfigureAwait(false);
                    if (ArtifactImageMetadata.VerifyIndex(image, indexBytes,
                        cursor.Document.Contents.Single(row => row.Digest == indexDigest).MediaType) != manifest.MediaType)
                        throw new ArtifactAcquisitionException(ArtifactAcquisitionFailure.ImageInventoryMismatch);
                }
            }
            else ArtifactImageMetadata.VerifyConfiguration(image, bytes);
        }
    }

    private async ValueTask<byte[]> ReadOwnedImageMetadataAsync(ArtifactImageAcquisitionPlan plan, ImageRunCursor cursor,
        LocalArtifactAcquisitionStorage.ImageLease lease, string root, ArtifactImageContent content,
        CancellationToken cancellationToken)
    {
        if (content.ExpectedBytes is not { } expected || expected > ArtifactImageMetadata.MaximumBytes)
            throw new ArtifactAcquisitionException(ArtifactAcquisitionFailure.ImageMetadataInvalid);
        await VerifyOwnedImageContentAsync(plan, cursor, lease, root, content, cancellationToken).ConfigureAwait(false);
        var row = cursor.Document.Contents.Single(row => row.Digest == content.Digest);
        var path = ArtifactImagePaths.FilePath(root, "blob:" + content.Digest[7..]);
        await using var stream = ArtifactAcquisitionFileIdentity.OpenRead(path);
        if (ArtifactAcquisitionFileIdentity.Read(stream.SafeFileHandle) != new ArtifactAcquisitionFileMetadata(row.Identity!, expected))
            throw new ArtifactAcquisitionException(ArtifactAcquisitionFailure.FinalConflict);
        var bytes = new byte[checked((int)expected)];
        var offset = 0;
        while (offset < bytes.Length)
        {
            await RequireImageCurrentAsync(plan, cursor, lease, cancellationToken).ConfigureAwait(false);
            var count = await stream.ReadAsync(bytes.AsMemory(offset, Math.Min(65_536, bytes.Length - offset)), cancellationToken).ConfigureAwait(false);
            if (count == 0) throw new ArtifactAcquisitionException(ArtifactAcquisitionFailure.ContentLengthMismatch);
            offset += count;
        }
        if (stream.ReadByte() != -1 || Convert.ToHexStringLower(SHA256.HashData(bytes)) != content.Digest[7..] ||
            LocalArtifactAcquisitionStorage.ExistingImageFile(root, "blob:" + content.Digest[7..]) !=
                new ArtifactAcquisitionFileMetadata(row.Identity!, expected))
            throw new ArtifactAcquisitionException(ArtifactAcquisitionFailure.IntegrityMismatch);
        return bytes;
    }

    private async ValueTask VerifyOwnedImageContentAsync(ArtifactImageAcquisitionPlan plan, ImageRunCursor cursor,
        LocalArtifactAcquisitionStorage.ImageLease lease, string root, ArtifactImageContent content, CancellationToken cancellationToken)
    {
        var row = cursor.Document.Contents.Single(row => row.Digest == content.Digest);
        if (row.Identity is null || row.Bytes != content.ExpectedBytes ||
            row.State is not (ArtifactAcquisitionJournalState.Verified or ArtifactAcquisitionJournalState.Finalized))
            throw new ArtifactAcquisitionException(ArtifactAcquisitionFailure.ImageInventoryMismatch);
        var actual = await ArtifactAcquisitionFileIdentity.HashOwnedFileAsync(
            ArtifactImagePaths.FilePath(root, "blob:" + content.Digest[7..]), new(row.Identity, row.Bytes),
            token => RequireImageCurrentAsync(plan, cursor, lease, token),
            ArtifactAcquisitionFailure.FinalConflict, cancellationToken).ConfigureAwait(false);
        if (actual != content.Digest[7..]) throw new ArtifactAcquisitionException(ArtifactAcquisitionFailure.IntegrityMismatch);
    }

    private async ValueTask RecoverImageTreeAsync(ArtifactImageAcquisitionPlan plan, ImageRunCursor cursor,
        LocalArtifactAcquisitionStorage.ImageLease lease, CancellationToken cancellationToken)
    {
        var local = ImageStorage();
        if (plan.RecoveryTree is { } recovery && cursor.Document.State != ArtifactImageJournalState.Quarantining)
        {
            if (cursor.Document.Quarantine is not null)
                throw new ArtifactAcquisitionException(ArtifactAcquisitionFailure.FinalConflict);
            cursor.Document = cursor.Document with
            {
                State = ArtifactImageJournalState.Quarantining, RecoveryTree = recovery,
                RecoveryWasPublished = plan.Storage.Published is not null
            };
            await PersistImageAsync(plan, cursor, lease, cancellationToken).ConfigureAwait(false);
        }
        if (cursor.Document.State != ArtifactImageJournalState.Quarantining) return;
        var tree = cursor.Document.RecoveryTree ?? throw new ArtifactAcquisitionException(ArtifactAcquisitionFailure.JournalCorrupt);
        var source = cursor.Document.RecoveryWasPublished ? plan.Paths.Destination : plan.Paths.Staging;
        if (plan.Storage.Quarantine is null)
        {
            var current = await local.InspectImageTreeAsync(plan.Paths, source, tree,
                token => RequireImageCurrentAsync(plan, cursor, lease, token), cancellationToken).ConfigureAwait(false);
            if (ArtifactImageStorageSnapshot.TreeFingerprint(current) != ArtifactImageStorageSnapshot.TreeFingerprint(tree))
                throw new ArtifactAcquisitionException(ArtifactAcquisitionFailure.PlanChanged);
            await RequireImageCurrentAsync(plan, cursor, lease, cancellationToken).ConfigureAwait(false);
            local.MoveImageTree(plan.Paths, source, plan.Paths.Quarantine, tree, lease);
        }
        var retained = await local.InspectImageTreeAsync(plan.Paths, plan.Paths.Quarantine, tree,
            token => RequireImageCurrentAsync(plan, cursor, lease, token), cancellationToken).ConfigureAwait(false);
        if (ArtifactImageStorageSnapshot.TreeFingerprint(retained) != ArtifactImageStorageSnapshot.TreeFingerprint(tree))
            throw new ArtifactAcquisitionException(ArtifactAcquisitionFailure.FinalConflict);
        cursor.Document = cursor.Document with
        {
            State = ArtifactImageJournalState.Quarantined, Quarantine = tree, RecoveryTree = null,
            RootIdentity = null, Directories = [], Contents = EmptyImageProgress(plan.Selection),
            PublicationFiles = [], LastFailure = null
        };
        await PersistImageAsync(plan, cursor, lease, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask PublishImageTreeAsync(ArtifactImageAcquisitionPlan plan, ImageRunCursor cursor,
        LocalArtifactAcquisitionStorage.ImageLease lease, Dictionary<string, VerifiedImageManifest> graph,
        CancellationToken cancellationToken)
    {
        var local = ImageStorage();
        foreach (var content in plan.Selection.ImageContentInventory.Contents)
            await VerifyOwnedImageContentAsync(plan, cursor, lease, plan.Paths.Staging, content, cancellationToken).ConfigureAwait(false);
        foreach (var item in new[] { (Key: "layout", Bytes: "{\"imageLayoutVersion\":\"1.0.0\"}"u8.ToArray()),
            (Key: "index", Bytes: BuildImageIndex(plan, graph)) })
        {
            var existing = cursor.Document.PublicationFiles.SingleOrDefault(file => file.Key == item.Key);
            if (existing is not null)
            {
                if (existing.Bytes != item.Bytes.Length || existing.Sha256 != Convert.ToHexStringLower(SHA256.HashData(item.Bytes)))
                    throw new ArtifactAcquisitionException(ArtifactAcquisitionFailure.FinalConflict);
                continue;
            }
            await RequireImageCurrentAsync(plan, cursor, lease, cancellationToken).ConfigureAwait(false);
            var written = await local.WriteImagePublicationAsync(plan.Paths, cursor.Document, item.Key, item.Bytes, lease, cancellationToken).ConfigureAwait(false);
            cursor.Document = cursor.Document with { PublicationFiles = [.. cursor.Document.PublicationFiles, written] };
            await PersistImageAsync(plan, cursor, lease, cancellationToken).ConfigureAwait(false);
        }
        if (cursor.Document.Directories.ContainsKey("partials"))
        {
            await RequireImageCurrentAsync(plan, cursor, lease, cancellationToken).ConfigureAwait(false);
            local.RemoveImagePartialsDirectory(plan.Paths, cursor.Document, lease);
            var directories = new Dictionary<string, string>(cursor.Document.Directories, StringComparer.Ordinal);
            directories.Remove("partials");
            cursor.Document = cursor.Document with { Directories = directories };
            await PersistImageAsync(plan, cursor, lease, cancellationToken).ConfigureAwait(false);
        }
        var tree = await VerifyImageTreeForPublicationAsync(plan, cursor, lease, plan.Paths.Staging, graph, cancellationToken).ConfigureAwait(false);
        cursor.Document = cursor.Document with { State = ArtifactImageJournalState.Publishing };
        await PersistImageAsync(plan, cursor, lease, cancellationToken).ConfigureAwait(false);
        await ReportImageAsync(plan, plan.Selection.ImageCandidates[0].ArtifactId, ArtifactAcquisitionProgressPhase.Finalizing,
            cursor, cancellationToken).ConfigureAwait(false);
        await RequireImageCurrentAsync(plan, cursor, lease, cancellationToken).ConfigureAwait(false);
        local.MoveImageTree(plan.Paths, plan.Paths.Staging, plan.Paths.Destination, tree, lease);
        await VerifyImageTreeForPublicationAsync(plan, cursor, lease, plan.Paths.Destination, graph, cancellationToken).ConfigureAwait(false);
        cursor.Document = cursor.Document with { State = ArtifactImageJournalState.Published, LastFailure = null };
        await PersistImageAsync(plan, cursor, lease, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<ArtifactImageTree> VerifyImageTreeForPublicationAsync(ArtifactImageAcquisitionPlan plan,
        ImageRunCursor cursor, LocalArtifactAcquisitionStorage.ImageLease lease, string root,
        Dictionary<string, VerifiedImageManifest> graph, CancellationToken cancellationToken)
    {
        var files = cursor.Document.Contents.Select(row => new ArtifactImageOwnedFile
        {
            Key = "blob:" + row.Digest[7..], Identity = row.Identity!, Bytes = row.Bytes, Sha256 = row.Digest[7..]
        }).Concat(cursor.Document.PublicationFiles).ToArray();
        if (cursor.Document.PublicationFiles.Length != 2 ||
            cursor.Document.Contents.Any(row => row.State != ArtifactAcquisitionJournalState.Finalized))
            throw new ArtifactAcquisitionException(ArtifactAcquisitionFailure.ImageInventoryMismatch);
        var index = cursor.Document.PublicationFiles.Single(file => file.Key == "index");
        var layout = cursor.Document.PublicationFiles.Single(file => file.Key == "layout");
        var indexBytes = BuildImageIndex(plan, graph);
        if (index.Bytes != indexBytes.Length || index.Sha256 != Convert.ToHexStringLower(SHA256.HashData(indexBytes)) ||
            layout.Sha256 != Convert.ToHexStringLower(SHA256.HashData("{\"imageLayoutVersion\":\"1.0.0\"}"u8)))
            throw new ArtifactAcquisitionException(ArtifactAcquisitionFailure.ImageInventoryMismatch);
        var expected = new ArtifactImageTree
        {
            RootIdentity = cursor.Document.RootIdentity!, Directories = cursor.Document.Directories, Files = files
        };
        var actual = await ImageStorage().InspectImageTreeAsync(plan.Paths, root, expected,
            token => RequireImageCurrentAsync(plan, cursor, lease, token), cancellationToken).ConfigureAwait(false);
        if (ArtifactImageStorageSnapshot.TreeFingerprint(actual) != ArtifactImageStorageSnapshot.TreeFingerprint(expected))
            throw new ArtifactAcquisitionException(ArtifactAcquisitionFailure.IntegrityMismatch);
        return actual;
    }

    private static byte[] BuildImageIndex(ArtifactImageAcquisitionPlan plan, Dictionary<string, VerifiedImageManifest> graph)
        => BuildImageIndex(plan.Selection, graph);

    private static byte[] BuildImageIndex(ArtifactAcquisitionSelection selection, Dictionary<string, VerifiedImageManifest> graph)
    {
        using var bytes = new MemoryStream();
        using (var writer = new Utf8JsonWriter(bytes))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", 2);
            writer.WriteString("mediaType", HttpsArtifactImageTransport.OciIndex);
            writer.WriteStartArray("manifests");
            foreach (var image in selection.ImageCandidates)
            {
                writer.WriteStartObject();
                writer.WriteString("mediaType", graph[image.ArtifactId].MediaType);
                writer.WriteString("digest", image.Digest);
                writer.WriteNumber("size", image.ManifestBytes!.Value);
                writer.WriteStartObject("platform");
                writer.WriteString("os", "linux");
                writer.WriteString("architecture", "amd64");
                writer.WriteEndObject();
                writer.WriteStartObject("annotations");
                writer.WriteString("org.opencontainers.image.ref.name", image.ArtifactId);
                writer.WriteEndObject();
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return bytes.ToArray();
    }

    private static void ReplaceImageProgress(ImageRunCursor cursor, ArtifactImageContentProgress content) =>
        cursor.Document = cursor.Document with
        {
            Contents = cursor.Document.Contents.Select(row => row.Digest == content.Digest ? content : row).ToArray()
        };

    private async ValueTask ReportImageAsync(ArtifactImageAcquisitionPlan plan, string imageId,
        ArtifactAcquisitionProgressPhase phase, ImageRunCursor cursor, CancellationToken cancellationToken, long uncheckpointed = 0)
    {
        try
        {
            await progress.ReportAsync(new(plan.Fingerprint, imageId, phase,
                checked(cursor.Document.Contents.Sum(row => row.Bytes) + uncheckpointed), plan.KnownContentBytes,
                plan.Storage.Journal?.Contents.Any(row => row.Bytes > 0) == true, clock.GetUtcNow()), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { throw new ArtifactAcquisitionException(ArtifactAcquisitionFailure.ProgressFailed); }
    }
}
