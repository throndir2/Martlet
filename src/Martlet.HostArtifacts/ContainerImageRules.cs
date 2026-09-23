using System.Text.RegularExpressions;
using static Martlet.HostArtifacts.ArtifactSourceRules;

namespace Martlet.HostArtifacts;

internal static partial class ContainerImageRules
{
    internal static void Validate(ManifestDocument doc, IEnumerable<ContainerImageDocument> images,
        IEnumerable<ArtifactDocument> artifacts)
    {
        var manifests = new HashSet<string>(StringComparer.Ordinal);
        var facts = new Dictionary<string, ContentFact>(StringComparer.Ordinal);
        var blobCount = 0;
        foreach (var image in images)
        {
            Require(image.Registry.Length is > 0 and <= 253 && RegistryPattern().IsMatch(image.Registry),
                "image.reference_invalid");
            Require(image.Repository.Length is > 0 and <= 255 && RepositoryPattern().IsMatch(image.Repository),
                "image.reference_invalid");
            Digest(image.Digest);
            Require(manifests.Add(image.Digest), "manifest.alias_invalid");
            if (image.Platform is { } platform) ValidatePlatform(platform);
            Require(image.Evidence != ImageMetadataEvidence.SyntheticFixture ||
                doc.Provenance == MetadataProvenance.SyntheticFixture, "image.evidence_invalid");
            ExactUrl(image.EvidenceUrl, $"https://{image.Registry}/v2/{image.Repository}/manifests/{image.Digest}");
            Require(image.Blobs.Length <= 128 && image.Blobs.All(b => b is not null), "manifest.bounds_invalid");
            blobCount += image.Blobs.Length;
            Require(blobCount <= 256, "manifest.bounds_invalid");
            Require(image.Blobs.Select(b => b.Digest).Distinct(StringComparer.Ordinal).Count() == image.Blobs.Length,
                "manifest.alias_invalid");
            var configurations = image.Blobs.Count(b => b.Kind == ImageBlobKind.Configuration);
            Require(configurations <= 1 && (!image.BlobInventoryComplete || configurations == 1),
                "image.inventory_invalid");
            foreach (var fact in Facts(image))
            {
                Digest(fact.Digest);
                Size(fact.CompressedBytes, positive: true);
                Size(fact.ExpandedBytes, positive: false);
                Size(fact.StagingBytes, positive: false);
                Require(!facts.TryGetValue(fact.Digest, out var previous) || previous == fact, "image.facts_conflict");
                facts[fact.Digest] = fact;
            }
        }
        // The v1 file model has different size/evidence semantics. Do not silently count an alias twice.
        Require(!artifacts.Any(a => a.Sha256 is { } hash && facts.ContainsKey("sha256:" + hash)),
            "manifest.alias_invalid");
        Require(facts.Values.Sum(f => f.CompressedBytes ?? 0) + artifacts.Sum(a => a.Bytes) <= 70_368_744_177_664L &&
            facts.Values.Sum(f => f.ExpandedBytes ?? 0) <= 70_368_744_177_664L &&
            facts.Values.Sum(f => f.StagingBytes ?? 0) <= 70_368_744_177_664L, "manifest.bounds_invalid");
    }

    internal static ImagePlatform ParsePlatform(string value)
    {
        var parts = value.Split('/');
        Require(parts.Length is 2 or 3, "image.platform_invalid");
        var result = new ImagePlatform { Os = parts[0], Architecture = parts[1], Variant = parts.Length == 3 ? parts[2] : null };
        ValidatePlatform(result);
        return result;
    }

    internal static void ValidatePlatform(ImagePlatform platform)
    {
        Token(platform.Os);
        Token(platform.Architecture);
        if (platform.Variant is { } variant) Token(variant);
    }

    internal static string Compare(ImagePlatform? declared, ImagePlatform? requested) =>
        declared is null || requested is null ? "unknown" :
        declared == requested ? "declared_match_not_qualified" : "declared_mismatch";

    internal static ImageByteInventory Inventory(IEnumerable<ContainerImageDocument> images)
    {
        var selected = images.ToArray();
        var facts = selected.SelectMany(Facts).DistinctBy(f => f.Digest).ToArray();
        var blobs = facts.Where(f => f.Kind is "configuration" or "layer").ToArray();
        return new(facts.Sum(f => f.CompressedBytes ?? 0), facts.Count(f => f.CompressedBytes is null),
            blobs.Sum(f => f.ExpandedBytes ?? 0), blobs.Count(f => f.ExpandedBytes is null),
            blobs.Sum(f => f.StagingBytes ?? 0), blobs.Count(f => f.StagingBytes is null),
            facts.Length, selected.Count(i => !i.BlobInventoryComplete));
    }

    private static IEnumerable<ContentFact> Facts(ContainerImageDocument image)
    {
        yield return new(image.Digest, "manifest", image.ManifestBytes, null, null);
        if (image.Index is { } index)
            yield return new(index.Digest, "index", index.Bytes, null, null);
        foreach (var blob in image.Blobs)
            yield return new(blob.Digest, blob.Kind == ImageBlobKind.Layer ? "layer" : "configuration",
                blob.CompressedBytes, blob.ExpandedBytes, blob.StagingBytes);
    }

    private static void Digest(string value) =>
        Require(value.StartsWith("sha256:", StringComparison.Ordinal) && Hex(value[7..], 64), "image.reference_invalid");

    private static void Size(long? value, bool positive) =>
        Require(value is null || value >= (positive ? 1 : 0) && value <= 17_592_186_044_416L, "artifact.pin_invalid");

    private static void Token(string value) =>
        Require(value.Length is > 0 and <= 32 && PlatformPattern().IsMatch(value), "image.platform_invalid");

    private sealed record ContentFact(string Digest, string Kind, long? CompressedBytes, long? ExpandedBytes, long? StagingBytes);

    [GeneratedRegex(@"\A(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z]{2,63}\z", RegexOptions.CultureInvariant)]
    private static partial Regex RegistryPattern();
    [GeneratedRegex(@"\A[a-z0-9]+(?:(?:[._]|__|-+)[a-z0-9]+)*(?:/[a-z0-9]+(?:(?:[._]|__|-+)[a-z0-9]+)*)*\z", RegexOptions.CultureInvariant)]
    private static partial Regex RepositoryPattern();
    [GeneratedRegex(@"\A[a-z0-9]+(?:[._-][a-z0-9]+)*\z", RegexOptions.CultureInvariant)]
    private static partial Regex PlatformPattern();
}
