using Martlet.HostArtifacts;

namespace Martlet.Host.Setup;

public sealed class ArtifactPublishedImageObservation
{
    public string SelectionFingerprint { get; }
    public string Fingerprint { get; }
    public bool ContentVerified { get; }
    public string? JournalVersion { get; }
    public string? RecordedRightsFingerprint { get; }
    public bool PublisherAuthenticated => false;
    public bool ExecutionAuthorized => false;
    public bool EngineImageAvailable => false;

    internal ArtifactPublishedImageObservation(ArtifactAcquisitionSelection selection,
        ArtifactImageStorageSnapshot snapshot)
    {
        SelectionFingerprint = selection.Fingerprint;
        ContentVerified = snapshot.Journal?.State == ArtifactImageJournalState.Published &&
            snapshot.Published is not null && !snapshot.Corrupt && !snapshot.Pending && !snapshot.LeaseBusy;
        JournalVersion = snapshot.JournalVersion;
        RecordedRightsFingerprint = snapshot.Journal?.RightsFingerprint;
        Fingerprint = FingerprintBuilder.Create("published-image-observation-v1", selection.Fingerprint,
            snapshot.ParentIdentity, snapshot.JournalVersion ?? "", snapshot.LeaseIdentity ?? "",
            ArtifactImageStorageSnapshot.TreeFingerprint(snapshot.Published), ContentVerified.ToString());
    }
}

public sealed partial class ArtifactAcquisitionCoordinator
{
    public async ValueTask<ArtifactPublishedImageObservation> ObservePublishedImagesAsync(
        ArtifactAcquisitionSelection selection, CancellationToken cancellationToken = default)
    {
        using var observation = await OpenPublishedImagesAsync(ImageStorage(), selection, cancellationToken)
            .ConfigureAwait(false);
        return observation.Observation;
    }

    internal static async ValueTask<PublishedImageObservationScope> OpenPublishedImagesAsync(
        LocalArtifactAcquisitionStorage local, ArtifactAcquisitionSelection selection,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(selection);
        var paths = local.GetImagePaths(selection);
        var snapshot = await local.InspectImageAsync(paths, selection,
            token => { token.ThrowIfCancellationRequested(); return ValueTask.CompletedTask; },
            cancellationToken).ConfigureAwait(false);
        if (snapshot.Pending || snapshot.LeaseBusy || snapshot.Corrupt)
            throw new ArtifactAcquisitionException(ArtifactAcquisitionFailure.FinalConflict);
        LocalArtifactAcquisitionStorage.ImageLease? lease = null;
        try
        {
            // A passive observation never creates a marker or repairs acquisition state.
            if (snapshot.Journal is not null)
                lease = await local.AcquireImageLeaseAsync(paths, snapshot, cancellationToken).ConfigureAwait(false);
            var scope = new PublishedImageObservationScope(local, selection, paths, snapshot, lease);
            await scope.RevalidateAsync(cancellationToken).ConfigureAwait(false);
            return scope;
        }
        catch { lease?.Dispose(); throw; }
    }

    internal sealed class PublishedImageObservationScope(
        LocalArtifactAcquisitionStorage local, ArtifactAcquisitionSelection selection,
        ArtifactImagePaths paths, ArtifactImageStorageSnapshot original,
        LocalArtifactAcquisitionStorage.ImageLease? lease) : IDisposable
    {
        internal ArtifactPublishedImageObservation Observation { get; } = new(selection, original);

        internal async ValueTask RevalidateAsync(CancellationToken cancellationToken)
        {
            async ValueTask Check(CancellationToken token)
            {
                token.ThrowIfCancellationRequested();
                lease?.Validate();
                var journal = await local.ReadImageJournalAsync(paths, token).ConfigureAwait(false);
                if (journal?.Version != original.JournalVersion)
                    throw new ArtifactAcquisitionException(ArtifactAcquisitionFailure.JournalChanged);
            }
            var current = await local.InspectImageAsync(paths, selection, Check, cancellationToken, lease)
                .ConfigureAwait(false);
            if (current.Pending || current.Corrupt || current.LeaseBusy ||
                new ArtifactPublishedImageObservation(selection, current).Fingerprint != Observation.Fingerprint)
                throw new ArtifactAcquisitionException(ArtifactAcquisitionFailure.FinalConflict);
            if (!Observation.ContentVerified) return;
            async ValueTask<ReadOnlyMemory<byte>> Read(string digest)
            {
                var file = current.Published!.Files.Single(row => row.Key == "blob:" + digest[7..]);
                var path = ArtifactImagePaths.FilePath(paths.Destination, file.Key);
                var bytes = await ArtifactOwnedJournalIO.ReadAsync(path, ArtifactImageMetadata.MaximumBytes,
                    () => lease!.Validate(), cancellationToken).ConfigureAwait(false);
                if (bytes is null || bytes.Version != file.Sha256 ||
                    ArtifactOwnedJournalIO.InspectFile(path) != new ArtifactAcquisitionFileMetadata(file.Identity, file.Bytes))
                    throw new ArtifactAcquisitionException(ArtifactAcquisitionFailure.IntegrityMismatch);
                return bytes.Content;
            }
            var graph = new Dictionary<string, VerifiedImageManifest>(StringComparer.Ordinal);
            foreach (var image in selection.ImageCandidates)
            {
                var manifest = ArtifactImageMetadata.VerifyManifest(image, await Read(image.Digest).ConfigureAwait(false), null);
                if (image.IndexDigest is { } index &&
                    ArtifactImageMetadata.VerifyIndex(image, await Read(index).ConfigureAwait(false), null) != manifest.MediaType)
                    throw new ArtifactAcquisitionException(ArtifactAcquisitionFailure.ImageInventoryMismatch);
                ArtifactImageMetadata.VerifyConfiguration(image, await Read(manifest.ConfigurationDigest).ConfigureAwait(false));
                graph.Add(image.ArtifactId, manifest);
            }
            var publication = current.Journal!.PublicationFiles;
            var indexBytes = BuildImageIndex(selection, graph);
            if (publication.Length != 2 ||
                !publication.Any(file => file.Key == "index" && file.Bytes == indexBytes.Length &&
                    file.Sha256 == FingerprintBuilder.Bytes(indexBytes)) ||
                !publication.Any(file => file.Key == "layout" &&
                    file.Sha256 == FingerprintBuilder.Bytes("{\"imageLayoutVersion\":\"1.0.0\"}"u8)))
                throw new ArtifactAcquisitionException(ArtifactAcquisitionFailure.ImageInventoryMismatch);
            await Check(cancellationToken).ConfigureAwait(false);
        }

        public void Dispose() => lease?.Dispose();
    }
}
