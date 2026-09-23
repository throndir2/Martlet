using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;

namespace Martlet.HostArtifacts;

public sealed record ArtifactLicenseClaim(
    string Id,
    string SourceId,
    string Scope,
    string Spdx,
    string Disposition,
    string? EvidencePath,
    string? EvidenceUrl);

public sealed class ArtifactAcquisitionCandidate
{
    public ArtifactAcquisitionSource Source { get; }
    public string ManifestSha256 { get; }
    public string InventoryProvenance { get; }
    public bool DirectTransportEligible { get; }
    public string ArtifactId { get; }
    public string Kind { get; }
    public string SourceId { get; }
    public string SourceKind { get; }
    public string SourceRepository { get; }
    public string SourceRevision { get; }
    public string SourceUrl { get; }
    public string SourceOrigin { get; }
    public long ExpectedBytes { get; }
    public string? ExpectedSha256 { get; }
    public string HashEvidence { get; }
    public ImmutableArray<ArtifactLicenseClaim> Licenses { get; }
    public ImmutableArray<string> RoleIds { get; }
    public string IdentityFingerprint { get; }

    internal ArtifactAcquisitionCandidate(
        string manifestSha256,
        MetadataProvenance provenance,
        ArtifactDocument artifact,
        SourceDocument source,
        IEnumerable<LicenseDocument> licenses,
        IEnumerable<string> roleIds)
    {
        ManifestSha256 = manifestSha256;
        InventoryProvenance = Token(provenance);
        DirectTransportEligible = false;
        ArtifactId = artifact.Id;
        Kind = Token(artifact.Kind);
        SourceId = source.Id;
        SourceKind = Token(source.Kind);
        SourceRepository = source.Repository;
        SourceRevision = source.Revision;
        SourceUrl = artifact.SourceUrl;
        SourceOrigin = new Uri(artifact.SourceUrl).GetLeftPart(UriPartial.Authority);
        ExpectedBytes = artifact.Bytes;
        ExpectedSha256 = artifact.Sha256;
        HashEvidence = Token(artifact.Sha256Evidence);
        Licenses = licenses
            .OrderBy(value => value.Id, StringComparer.Ordinal)
            .Select(value => new ArtifactLicenseClaim(
                value.Id,
                value.SourceId,
                Token(value.Scope),
                value.Spdx,
                Token(value.Disposition),
                value.EvidencePath,
                value.EvidenceUrl))
            .ToImmutableArray();
        RoleIds = roleIds.Order(StringComparer.Ordinal).ToImmutableArray();
        Source = new ArtifactAcquisitionSource(source, artifact);
        IdentityFingerprint = Fingerprint(
        [
            "artifact-acquisition-candidate-v1",
            ManifestSha256,
            InventoryProvenance,
            DirectTransportEligible.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ArtifactId,
            Kind,
            SourceId,
            SourceKind,
            SourceRepository,
            SourceRevision,
            SourceUrl,
            SourceOrigin,
            Source.IdentityFingerprint,
            ExpectedBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ExpectedSha256 ?? "null",
            HashEvidence,
            .. Licenses.SelectMany(value => new[]
            {
                value.Id,
                value.SourceId,
                value.Scope,
                value.Spdx,
                value.Disposition,
                value.EvidencePath ?? "null",
                value.EvidenceUrl ?? "null"
            }),
            .. RoleIds
        ]);
    }

    internal static string Token<T>(T value) where T : struct, Enum
    {
        var name = value.ToString();
        var builder = new StringBuilder(name.Length + 4);
        for (var index = 0; index < name.Length; index++)
        {
            var character = name[index];
            if (index > 0 && char.IsUpper(character))
                builder.Append('_');
            builder.Append(char.ToLowerInvariant(character));
        }
        return builder.ToString();
    }

    internal static string Fingerprint(IEnumerable<string> values)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> length = stackalloc byte[4];
        foreach (var value in values)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
            hash.AppendData(length);
            hash.AppendData(bytes);
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
}

public enum ArtifactAcquisitionProvider { Unsupported, GithubReleaseAsset }

public sealed class ArtifactAcquisitionSource
{
    public ArtifactAcquisitionProvider Provider { get; }
    public string LogicalSourceUrl { get; }
    public Uri RequestUri { get; }
    public string IdentityFingerprint { get; }
    public string Policy => "github-release-asset-v1";

    internal ArtifactAcquisitionSource(SourceDocument source, ArtifactDocument artifact)
    {
        LogicalSourceUrl = artifact.SourceUrl;
        Provider = source.Kind == SourceKind.Github && artifact.Release is not null &&
            artifact.Sha256 is not null && artifact.Sha256Evidence == HashEvidence.GithubReleaseMetadata
            ? ArtifactAcquisitionProvider.GithubReleaseAsset : ArtifactAcquisitionProvider.Unsupported;
        RequestUri = new Uri(Provider == ArtifactAcquisitionProvider.GithubReleaseAsset
            ? $"https://api.github.com/repos/{source.Repository}/releases/assets/{artifact.Release!.AssetId}"
            : artifact.SourceUrl);
        IdentityFingerprint = ArtifactAcquisitionCandidate.Fingerprint(
            [Policy, Provider.ToString(), source.Repository, source.Revision,
                        LogicalSourceUrl, RequestUri.AbsoluteUri, artifact.Sha256 ?? "unknown",
                        artifact.Bytes.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
    }
}

public sealed record AcquisitionImageBlob(string Digest, string Kind,
    long? CompressedBytes, long? ExpandedBytes, long? StagingBytes);
public sealed record AcquisitionImage(string Id, string Registry, string Repository,
    string Digest, string? IndexDigest, string? Platform, bool BlobInventoryComplete,
    ImmutableArray<AcquisitionImageBlob> Blobs);

public sealed class ArtifactAcquisitionSelection
{
    public string ManifestSha256 { get; }
    public int FormatVersion { get; }
    public ImmutableArray<string> RoleIds { get; }
    public string? Target { get; }
    public string? Platform { get; }
    public bool DeclaredMismatch { get; }
    public ImmutableArray<ArtifactAcquisitionCandidate> Artifacts { get; }
    public ImmutableArray<AcquisitionImage> Images { get; }
    public ImmutableArray<ArtifactImageAcquisitionCandidate> ImageCandidates { get; }
    public ArtifactImageContentInventory ImageContentInventory { get; }
    public long KnownListedBytes { get; }
    public long KnownImageCompressedBytes { get; }
    public long KnownImageExpandedBytes { get; }
    public long KnownImageStagingBytes { get; }
    public int UnknownImageCompressedCount { get; }
    public int UnknownImageExpandedCount { get; }
    public int UnknownImageStagingCount { get; }
    public int IncompleteImageCount { get; }
    public string Fingerprint { get; }

    internal ArtifactAcquisitionSelection(ArtifactManifest manifest, InspectionReport inspection)
    {
        var report = inspection.Document;
        ManifestSha256 = manifest.DocumentSha256;
        FormatVersion = report.FormatVersion;
        RoleIds = report.Roles.Select(role => role.Id).ToImmutableArray();
        Target = report.RequestedTarget;
        Platform = report.RequestedPlatform;
        DeclaredMismatch = report.ExitCode == 1;
        Artifacts = report.Artifacts.Select(artifact => new ArtifactAcquisitionCandidate(
            manifest.DocumentSha256, manifest.Document.Provenance, artifact,
            report.Sources.Single(source => source.Id == artifact.SourceId),
            report.Licenses.Where(license => artifact.LicenseIds.Contains(license.Id, StringComparer.Ordinal)),
            report.Roles.Where(role => role.ArtifactIds.Contains(artifact.Id, StringComparer.Ordinal))
                .Select(role => role.Id))).ToImmutableArray();
        Images = (report.ContainerImages ?? []).Select(image => new AcquisitionImage(
            image.Metadata.Id, image.Metadata.Registry, image.Metadata.Repository,
            image.Metadata.Digest, image.Metadata.Index?.Digest,
            image.Metadata.Platform is { } p ? $"{p.Os}/{p.Architecture}" +
                (p.Variant is null ? "" : "/" + p.Variant) : null,
            image.Metadata.BlobInventoryComplete,
            image.Metadata.Blobs.Select(blob => new AcquisitionImageBlob(blob.Digest,
                blob.Kind.ToString(), blob.CompressedBytes, blob.ExpandedBytes, blob.StagingBytes))
                .ToImmutableArray())).ToImmutableArray();
        ImageCandidates = (report.ContainerImages ?? []).Select(image =>
            new ArtifactImageAcquisitionCandidate(manifest, image.Metadata,
                report.Sources.Single(source => source.Id == image.Metadata.SourceId),
                report.Licenses.Where(license => image.Metadata.LicenseIds.Contains(license.Id, StringComparer.Ordinal)),
                report.Roles.Where(role => role.ArtifactIds.Contains(image.Metadata.Id, StringComparer.Ordinal))
                    .Select(role => role.Id))).ToImmutableArray();
        ImageContentInventory = new ArtifactImageContentInventory(
            (report.ContainerImages ?? []).Select(image => image.Metadata), ImageCandidates);
        KnownListedBytes = report.Disk?.KnownListedPayloadBytes ?? 0;
        KnownImageCompressedBytes = report.Disk?.ContainerContent?.KnownCompressedBytes ?? 0;
        KnownImageExpandedBytes = report.Disk?.ContainerContent?.KnownExpandedBlobBytes ?? 0;
        KnownImageStagingBytes = report.Disk?.ContainerContent?.KnownStagingBlobBytes ?? 0;
        UnknownImageCompressedCount = report.Disk?.ContainerContent?.UnknownCompressedCount ?? 0;
        UnknownImageExpandedCount = report.Disk?.ContainerContent?.UnknownExpandedBlobCount ?? 0;
        UnknownImageStagingCount = report.Disk?.ContainerContent?.UnknownStagingBlobCount ?? 0;
        IncompleteImageCount = report.Disk?.ContainerContent?.IncompleteImageCount ?? 0;
        Fingerprint = ArtifactAcquisitionCandidate.Fingerprint(
            [ManifestSha256, string.Join(",", RoleIds), Target ?? "", Platform ?? ""]);
    }
}
