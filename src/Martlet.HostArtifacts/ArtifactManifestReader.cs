using System.Security.Cryptography;
using Martlet.Core.Contracts;

namespace Martlet.HostArtifacts;

public sealed class ArtifactManifest
{
    internal ManifestDocument Document { get; }
    public string DocumentSha256 { get; }
    public int FormatVersion => Document.FormatVersion;

    internal ArtifactManifest(ManifestDocument document, string sha256)
    {
        Document = document;
        DocumentSha256 = sha256;
    }

    public ArtifactAcquisitionSelection DescribeAcquisition(IEnumerable<string> roleIds,
        string? target = null, string? platform = null) =>
        new(this, ArtifactInspector.InspectRoles(this, roleIds, target, platform));

    public ArtifactAcquisitionCandidate DescribeArtifact(string artifactId)
    {
        try { ArtifactSourceRules.Id(artifactId); }
        catch (Exception error) when (error is ContractException or ArtifactManifestException)
        { throw new ArtifactManifestException("inspection.invalid_invocation"); }
        return DescribeAcquisition(Document.Roles.Select(role => role.Id)).Artifacts
            .SingleOrDefault(artifact => artifact.ArtifactId == artifactId)
            ?? throw new ArtifactManifestException("inspection.invalid_invocation");
    }
}

public static class ArtifactManifestReader
{
    public const int MaximumBytes = 262_144;

    public static ArtifactManifest Read(ReadOnlyMemory<byte> bytes)
    {
        if (bytes.Length > MaximumBytes)
            throw new ArtifactManifestException("manifest.too_large");
        var owned = bytes.ToArray();
        try
        {
            var header = ContractJson.Read<VersionHeader>(owned);
            // Keep v1's exact field vocabulary, including rejecting an explicit null v2 collection.
            using var json = System.Text.Json.JsonDocument.Parse(owned);
            ArtifactSourceRules.Require((header.FormatVersion == 2) ==
                json.RootElement.TryGetProperty("container_images", out _), "manifest.invalid_json");
            var document = ContractJson.Read<ManifestDocument>(owned);
            return new(document, Convert.ToHexStringLower(SHA256.HashData(owned)));
        }
        catch (ContractException error)
        {
            throw new ArtifactManifestException(error.Code switch
            {
                ErrorCode.UnsupportedVersion => "manifest.unsupported_version",
                ErrorCode.PayloadTooLarge => "manifest.too_large",
                _ => "manifest.invalid_json"
            });
        }
    }

    private sealed record VersionHeader : IContract
    {
        public required int FormatVersion { get; init; }
        public void Validate() => ContractRules.Require(FormatVersion is 1 or 2,
            "Unsupported artifact manifest version.", ErrorCode.UnsupportedVersion);
    }
}

public sealed class ArtifactManifestException : Exception
{
    public string DiagnosticCode { get; }
    public string Remedy { get; }

    internal ArtifactManifestException(string code) : base(InspectionFindings.Get(code).Summary)
    {
        DiagnosticCode = code;
        Remedy = InspectionFindings.Get(code).Remedy;
    }
}
