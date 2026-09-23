using System.Text.Json.Serialization;
using Martlet.Core.Contracts;

namespace Martlet.HostArtifacts;

internal enum MetadataProvenance { UpstreamMetadata, SyntheticFixture }
internal enum SourceKind { Github, HuggingFace }
internal enum RuntimeFamily { Ollama, F5Tts }
internal enum ArtifactKind { RuntimeArchive, LlmWeights, TtsWeights, Vocabulary, VocoderWeights, VocoderConfiguration, ContainerImage }
internal enum HashEvidence { GithubReleaseMetadata, HuggingFaceLfsMetadata, Unavailable }
internal enum LicenseScope { SourceCode, ModelRepository, RuntimeArchiveComponents, ContainerImageComponents }
internal enum LicenseDisposition { Unreviewed }
internal enum DependencyEcosystem { Python, PythonBuild, Native }
internal enum DependencyCondition { Always, PythonAtMost310, NotDarwinAndNotArm64 }
internal enum VersionComparison { AtLeast, GreaterThan, AtMost, Exact }
internal enum UnresolvedFact { RuntimeImage, RuntimeDependencies, ArchiveComponents, ExpandedBytes }

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ManifestDocument : IContract
{
    public required int FormatVersion { get; init; }
    public required string Kind { get; init; }
    public required string Id { get; init; }
    public required MetadataProvenance Provenance { get; init; }
    public required string Scope { get; init; }
    public required SourceDocument[] Sources { get; init; }
    public required RuntimeDocument[] Runtimes { get; init; }
    public required ArtifactDocument[] Artifacts { get; init; }
    public required LicenseDocument[] Licenses { get; init; }
    public required RoleDocument[] Roles { get; init; }
    public ContainerImageDocument[]? ContainerImages { get; init; }
    public void Validate() => ArtifactManifestValidator.Validate(this);
}

internal enum ImageMetadataEvidence { RegistryMetadata, SyntheticFixture }
internal enum ImageBlobKind { Configuration, Layer }

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ContainerImageDocument
{
    public required string Id { get; init; }
    public required string SourceId { get; init; }
    public required string Registry { get; init; }
    public required string Repository { get; init; }
    public required string Digest { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public required long? ManifestBytes { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public required ImageIndexDocument? Index { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public required ImagePlatform? Platform { get; init; }
    public required ImageMetadataEvidence Evidence { get; init; }
    public required string EvidenceUrl { get; init; }
    public required ImageBlobDocument[] Blobs { get; init; }
    public required bool BlobInventoryComplete { get; init; }
    public required string[] LicenseIds { get; init; }
    public required string[] DependsOn { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ImageIndexDocument
{
    public required string Digest { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public required long? Bytes { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ImagePlatform
{
    public required string Os { get; init; }
    public required string Architecture { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public required string? Variant { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ImageBlobDocument
{
    public required string Digest { get; init; }
    public required ImageBlobKind Kind { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public required long? CompressedBytes { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public required long? ExpandedBytes { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public required long? StagingBytes { get; init; }
}

internal sealed record ArtifactNode(string Id, ArtifactKind Kind, string SourceId, string[] LicenseIds, string[] DependsOn);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record SourceDocument
{
    public required string Id { get; init; }
    public required SourceKind Kind { get; init; }
    public required string Repository { get; init; }
    public required string Revision { get; init; }
    public required string CommitUrl { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record RuntimeDocument
{
    public required string Id { get; init; }
    public required RuntimeFamily Family { get; init; }
    public required ProviderRole Role { get; init; }
    public required string SourceId { get; init; }
    public required string UpstreamVersion { get; init; }
    public required string Target { get; init; }
    public required string LicenseId { get; init; }
    public required string DependencyEvidencePath { get; init; }
    public required string DependencyEvidenceUrl { get; init; }
    public required DependencyDocument[] Dependencies { get; init; }
    public required UnresolvedFact[] Unresolved { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record DependencyDocument
{
    public required string Name { get; init; }
    public required DependencyEcosystem Ecosystem { get; init; }
    public required DependencyCondition Condition { get; init; }
    public required VersionConstraint[] Constraints { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record VersionConstraint
{
    public required VersionComparison Comparison { get; init; }
    public required string Version { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ArtifactDocument
{
    public required string Id { get; init; }
    public required ArtifactKind Kind { get; init; }
    public required string SourceId { get; init; }
    public required string Path { get; init; }
    public required long Bytes { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public required string? Sha256 { get; init; }
    public required HashEvidence Sha256Evidence { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public required string? GitBlobSha1 { get; init; }
    public required string SourceUrl { get; init; }
    public required string EvidenceUrl { get; init; }
    public required ReleaseDocument? Release { get; init; }
    public required string[] LicenseIds { get; init; }
    public required string[] DependsOn { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ReleaseDocument
{
    public required long ReleaseId { get; init; }
    public required long AssetId { get; init; }
    public required string Tag { get; init; }
    public required string MetadataUrl { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record LicenseDocument
{
    public required string Id { get; init; }
    public required string SourceId { get; init; }
    public required LicenseScope Scope { get; init; }
    public required string Spdx { get; init; }
    public required LicenseDisposition Disposition { get; init; }
    public required string? EvidencePath { get; init; }
    public required string? EvidenceUrl { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record RoleDocument
{
    public required string Id { get; init; }
    public required ProviderRole Role { get; init; }
    public required string RuntimeId { get; init; }
    public required string Target { get; init; }
    public required string[] RootArtifactIds { get; init; }
    public required ComponentDocument[] Components { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ComponentDocument
{
    public required ArtifactKind Kind { get; init; }
    public required string? ArtifactId { get; init; }
}
