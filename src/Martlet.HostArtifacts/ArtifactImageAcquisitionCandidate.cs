using System.Collections.Immutable;
using System.Globalization;
using static Martlet.HostArtifacts.ArtifactAcquisitionCandidate;

namespace Martlet.HostArtifacts;

public enum ArtifactImageAcquisitionProvider { Unsupported, DockerHubPublic, GithubContainerRegistryPublic }
public enum ArtifactImageContentKind { Index, Manifest, Configuration, Layer }

public sealed class ArtifactImageAcquisitionSource
{
    public ArtifactImageAcquisitionProvider Provider { get; }
    public string Policy { get; }
    public string Registry { get; }
    public string Repository { get; }
    public string RegistryOrigin => "https://" + Registry;
    public string? TokenRealm { get; }
    public string? TokenService { get; }
    public string? CdnOrigin { get; }
    public string PullScope => "repository:" + Repository + ":pull";
    public string IdentityFingerprint { get; }

    internal ArtifactImageAcquisitionSource(ContainerImageDocument image, SourceDocument source)
    {
        Registry = image.Registry;
        Repository = image.Repository;
        Provider = (Registry, Repository, source.Kind, source.Repository) switch
        {
            ("registry-1.docker.io", "ollama/ollama", SourceKind.Github, "ollama/ollama") =>
                ArtifactImageAcquisitionProvider.DockerHubPublic,
            ("ghcr.io", "swivid/f5-tts", SourceKind.Github, "SWivid/F5-TTS") =>
                ArtifactImageAcquisitionProvider.GithubContainerRegistryPublic,
            _ => ArtifactImageAcquisitionProvider.Unsupported
        };
        (Policy, TokenRealm, TokenService, CdnOrigin) = Provider switch
        {
            ArtifactImageAcquisitionProvider.DockerHubPublic =>
                ("docker-hub-public-ollama-v1", "https://auth.docker.io/token",
                    "registry.docker.io", "https://production.cloudfront.docker.com"),
            ArtifactImageAcquisitionProvider.GithubContainerRegistryPublic =>
                ("ghcr-public-f5-v1", "https://ghcr.io/token",
                    "ghcr.io", "https://pkg-containers.githubusercontent.com"),
            _ => ("oci-provider-unsupported-v1", (string?)null, null, null)
        };
        IdentityFingerprint = Fingerprint(
            [Policy, Registry, Repository, source.Id, source.Repository, source.Revision]);
    }
}

public sealed class ArtifactImageAcquisitionCandidate
{
    public string ArtifactId { get; }
    public string ManifestSha256 { get; }
    public string InventoryProvenance { get; }
    public string SourceId { get; }
    public string SourceRepository { get; }
    public string SourceRevision { get; }
    public string MetadataEvidence { get; }
    public string EvidenceUrl { get; }
    public string Digest { get; }
    public long? ManifestBytes { get; }
    public string? IndexDigest { get; }
    public long? IndexBytes { get; }
    public string? Platform { get; }
    public bool BlobInventoryComplete { get; }
    public ImmutableArray<AcquisitionImageBlob> Blobs { get; }
    public ImmutableArray<ArtifactLicenseClaim> Licenses { get; }
    public ImmutableArray<string> RoleIds { get; }
    public ArtifactImageAcquisitionSource Source { get; }
    public string IdentityFingerprint { get; }

    internal ArtifactImageAcquisitionCandidate(ArtifactManifest manifest, ContainerImageDocument image,
        SourceDocument source, IEnumerable<LicenseDocument> licenses, IEnumerable<string> roles)
    {
        ArtifactId = image.Id;
        ManifestSha256 = manifest.DocumentSha256;
        InventoryProvenance = Token(manifest.Document.Provenance);
        SourceId = source.Id;
        SourceRepository = source.Repository;
        SourceRevision = source.Revision;
        MetadataEvidence = Token(image.Evidence);
        EvidenceUrl = image.EvidenceUrl;
        Digest = image.Digest;
        ManifestBytes = image.ManifestBytes;
        IndexDigest = image.Index?.Digest;
        IndexBytes = image.Index?.Bytes;
        Platform = image.Platform is { } platform
            ? $"{platform.Os}/{platform.Architecture}" + (platform.Variant is null ? "" : "/" + platform.Variant)
            : null;
        BlobInventoryComplete = image.BlobInventoryComplete;
        Blobs = image.Blobs.Select(blob => new AcquisitionImageBlob(blob.Digest, Token(blob.Kind),
            blob.CompressedBytes, blob.ExpandedBytes, blob.StagingBytes)).ToImmutableArray();
        Licenses = licenses.OrderBy(license => license.Id, StringComparer.Ordinal).Select(license =>
            new ArtifactLicenseClaim(license.Id, license.SourceId, Token(license.Scope), license.Spdx,
                Token(license.Disposition), license.EvidencePath, license.EvidenceUrl)).ToImmutableArray();
        RoleIds = roles.Order(StringComparer.Ordinal).ToImmutableArray();
        Source = new ArtifactImageAcquisitionSource(image, source);
        IdentityFingerprint = Fingerprint(
        [
            "artifact-image-acquisition-candidate-v1", ArtifactId, ManifestSha256, InventoryProvenance,
            SourceId, SourceRepository, SourceRevision, MetadataEvidence, EvidenceUrl,
            Source.IdentityFingerprint, Digest, Number(ManifestBytes), IndexDigest ?? "", Number(IndexBytes),
            Platform ?? "", BlobInventoryComplete.ToString(CultureInfo.InvariantCulture),
            .. Blobs.SelectMany(blob => new[] { blob.Digest, blob.Kind, Number(blob.CompressedBytes),
                Number(blob.ExpandedBytes), Number(blob.StagingBytes) }),
            .. Licenses.SelectMany(license => new[] { license.Id, license.SourceId, license.Scope,
                license.Spdx, license.Disposition, license.EvidencePath ?? "", license.EvidenceUrl ?? "" }),
            .. RoleIds
        ]);
    }

    internal static string Number(long? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "unknown";
}

public sealed class ArtifactImageContent
{
    public string Digest { get; }
    public ArtifactImageContentKind Kind { get; }
    public long? ExpectedBytes { get; }
    public ImmutableArray<string> ImageIds { get; }
    public ArtifactImageAcquisitionSource Source { get; }
    public string IdentityFingerprint { get; }

    internal ArtifactImageContent(ContainerImageRules.ContentFact fact,
        IEnumerable<ArtifactImageAcquisitionCandidate> images)
    {
        Digest = fact.Digest;
        Kind = fact.Kind switch
        {
            "index" => ArtifactImageContentKind.Index,
            "manifest" => ArtifactImageContentKind.Manifest,
            "configuration" => ArtifactImageContentKind.Configuration,
            "layer" => ArtifactImageContentKind.Layer,
            _ => throw new InvalidOperationException("Invalid image content kind.")
        };
        ExpectedBytes = fact.CompressedBytes;
        var ordered = images.OrderBy(image => image.ArtifactId, StringComparer.Ordinal).ToArray();
        ImageIds = ordered.Select(image => image.ArtifactId).ToImmutableArray();
        Source = ordered[0].Source;
        IdentityFingerprint = Fingerprint(["artifact-image-content-v1", Digest, Kind.ToString(),
            ArtifactImageAcquisitionCandidate.Number(ExpectedBytes), Source.IdentityFingerprint, .. ImageIds]);
    }
}

public sealed class ArtifactImageContentInventory
{
    public ImmutableArray<ArtifactImageContent> Contents { get; }
    public long KnownBytes { get; }
    public int UnknownBytesCount { get; }
    public string Fingerprint { get; }

    internal ArtifactImageContentInventory(IEnumerable<ContainerImageDocument> images,
        ImmutableArray<ArtifactImageAcquisitionCandidate> candidates)
    {
        var documents = images.ToArray();
        var facts = documents.SelectMany(image => ContainerImageRules.Facts(image)
            .Select(fact => (image.Id, Fact: fact)));
        Contents = facts.GroupBy(item => item.Fact.Digest, StringComparer.Ordinal).Select(group =>
            new ArtifactImageContent(group.First().Fact,
                candidates.Where(candidate => group.Any(item => item.Id == candidate.ArtifactId))))
            .OrderBy(content => content.Kind).ThenBy(content => content.Digest, StringComparer.Ordinal)
            .ToImmutableArray();
        var inventory = ContainerImageRules.Inventory(documents);
        KnownBytes = inventory.KnownCompressedBytes;
        UnknownBytesCount = inventory.UnknownCompressedCount;
        Fingerprint = ArtifactAcquisitionCandidate.Fingerprint(
            ["artifact-image-content-inventory-v1", .. Contents.Select(content => content.IdentityFingerprint)]);
    }
}
