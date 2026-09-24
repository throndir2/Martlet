using System.Security.Cryptography;
using System.Text.Json;
using Martlet.HostArtifacts;

namespace Martlet.Host.Setup;

internal sealed record VerifiedImageManifest(string MediaType, string ConfigurationDigest, string[] LayerDigests);

internal static class ArtifactImageMetadata
{
    internal const int MaximumBytes = 1_048_576;
    private const string OciConfig = "application/vnd.oci.image.config.v1+json";
    private const string DockerConfig = "application/vnd.docker.container.image.v1+json";
    private const string OciLayer = "application/vnd.oci.image.layer.v1.tar+gzip";
    private const string DockerLayer = "application/vnd.docker.image.rootfs.diff.tar.gzip";

    internal static string VerifyIndex(ArtifactImageAcquisitionCandidate image, ReadOnlyMemory<byte> bytes,
        string? responseMediaType)
    {
        return Guarded(() =>
        {
            VerifyBytes(bytes, image.IndexBytes, image.IndexDigest);
            using var document = ArtifactImageJson.Parse(bytes);
            var root = document.RootElement;
            ArtifactImageJson.Properties(root, ["schemaVersion", "mediaType", "manifests", "annotations"],
                ["schemaVersion", "mediaType", "manifests"]);
            var media = Schema(root, responseMediaType);
            Require(media is HttpsArtifactImageTransport.OciIndex or HttpsArtifactImageTransport.DockerIndex);
            Annotations(root);
            var entries = Array(root.GetProperty("manifests"), 128);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var matches = 0;
            string? selectedMediaType = null;
            foreach (var entry in entries)
            {
                var descriptor = Descriptor(entry, withPlatform: true);
                Require(descriptor.MediaType is HttpsArtifactImageTransport.OciManifest or HttpsArtifactImageTransport.DockerManifest);
                Require(seen.Add(descriptor.Digest));
                if (!entry.TryGetProperty("platform", out var platform))
                    throw Failure(ArtifactAcquisitionFailure.ImagePlatformMismatch);
                if (Platform(platform, strict: true) != image.Platform) continue;
                matches++;
                selectedMediaType = descriptor.MediaType;
                if (descriptor.Digest != image.Digest || descriptor.Bytes != image.ManifestBytes)
                    throw Failure(ArtifactAcquisitionFailure.ImageInventoryMismatch);
            }
            if (matches != 1) throw Failure(ArtifactAcquisitionFailure.ImagePlatformMismatch);
            return selectedMediaType!;
        });
    }

    internal static VerifiedImageManifest VerifyManifest(ArtifactImageAcquisitionCandidate image,
        ReadOnlyMemory<byte> bytes, string? responseMediaType) => Guarded(() =>
    {
        VerifyBytes(bytes, image.ManifestBytes, image.Digest);
        using var document = ArtifactImageJson.Parse(bytes);
        var root = document.RootElement;
        ArtifactImageJson.Properties(root, ["schemaVersion", "mediaType", "config", "layers", "annotations"],
            ["schemaVersion", "mediaType", "config", "layers"]);
        var media = Schema(root, responseMediaType);
        Require(media is HttpsArtifactImageTransport.OciManifest or HttpsArtifactImageTransport.DockerManifest);
        Annotations(root);
        var configuration = Descriptor(root.GetProperty("config"), withPlatform: false);
        var oci = media == HttpsArtifactImageTransport.OciManifest;
        Require(configuration.MediaType == (oci ? OciConfig : DockerConfig));
        var expectedConfig = image.Blobs.SingleOrDefault(blob => blob.Kind == "configuration")
            ?? throw Failure(ArtifactAcquisitionFailure.ImageInventoryMismatch);
        Match(configuration, expectedConfig);
        var layers = Array(root.GetProperty("layers"), 127).Select(layer =>
        {
            var descriptor = Descriptor(layer, withPlatform: false);
            Require(descriptor.MediaType == (oci ? OciLayer : DockerLayer));
            return descriptor;
        }).ToArray();
        var expectedLayers = image.Blobs.Where(blob => blob.Kind == "layer").ToArray();
        if (expectedLayers.Length != layers.Length)
            throw Failure(ArtifactAcquisitionFailure.ImageInventoryMismatch);
        for (var index = 0; index < layers.Length; index++)
            Match(layers[index], expectedLayers[index]);
        return new VerifiedImageManifest(media, configuration.Digest, layers.Select(layer => layer.Digest).ToArray());
    });

    internal static void VerifyConfiguration(ArtifactImageAcquisitionCandidate image,
        ReadOnlyMemory<byte> bytes)
    {
        Guarded(() =>
        {
            var configuration = image.Blobs.Single(blob => blob.Kind == "configuration");
            VerifyBytes(bytes, configuration.CompressedBytes, configuration.Digest);
            using var document = ArtifactImageJson.Parse(bytes);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || Platform(root, strict: false) != image.Platform)
                throw Failure(ArtifactAcquisitionFailure.ImagePlatformMismatch);
            if (root.TryGetProperty("os.version", out var version) && version.ValueKind != JsonValueKind.Null ||
                root.TryGetProperty("os.features", out var features) && features.ValueKind != JsonValueKind.Null)
                throw Failure(ArtifactAcquisitionFailure.ImagePlatformMismatch);
            var filesystem = root.GetProperty("rootfs");
            ArtifactImageJson.Properties(filesystem, ["type", "diff_ids"], ["type", "diff_ids"]);
            Require(filesystem.GetProperty("type").GetString() == "layers");
            var diffIds = Array(filesystem.GetProperty("diff_ids"), 127);
            Require(diffIds.Length == image.Blobs.Count(blob => blob.Kind == "layer"));
            foreach (var digest in diffIds) Digest(digest.GetString());
            // Execution parameters and history remain opaque configuration data, not instructions.
            return true;
        });
    }

    private static void VerifyBytes(ReadOnlyMemory<byte> bytes, long? size, string? digest)
    {
        if (size is null || size > MaximumBytes || bytes.Length != size)
            throw Failure(ArtifactAcquisitionFailure.ContentLengthMismatch);
        if ("sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes.Span)) != digest)
            throw Failure(ArtifactAcquisitionFailure.IntegrityMismatch);
    }

    private static string Schema(JsonElement root, string? responseMediaType)
    {
        Require(root.GetProperty("schemaVersion").TryGetInt32(out var schema) && schema == 2);
        var media = root.GetProperty("mediaType").GetString();
        Require(media is not null && (responseMediaType is null || responseMediaType == media));
        return media!;
    }

    private static ImageDescriptor Descriptor(JsonElement element, bool withPlatform)
    {
        ArtifactImageJson.Properties(element,
            withPlatform ? ["mediaType", "digest", "size", "platform", "annotations"] : ["mediaType", "digest", "size", "annotations"],
            ["mediaType", "digest", "size"]);
        var media = element.GetProperty("mediaType").GetString();
        Require(media is { Length: > 0 and <= 128 });
        var digest = element.GetProperty("digest").GetString();
        Digest(digest);
        Require(element.GetProperty("size").TryGetInt64(out var size) && size is > 0 and <= 17_592_186_044_416L);
        Annotations(element);
        return new(media!, digest!, size);
    }

    private static string Platform(JsonElement element, bool strict)
    {
        if (strict)
            ArtifactImageJson.Properties(element, ["os", "architecture", "variant"], ["os", "architecture"]);
        var os = element.GetProperty("os").GetString();
        var architecture = element.GetProperty("architecture").GetString();
        var variant = element.TryGetProperty("variant", out var value) && value.ValueKind != JsonValueKind.Null
            ? value.GetString() : null;
        foreach (var token in variant is null ? new[] { os, architecture } : [os, architecture, variant])
            Require(token is { Length: > 0 and <= 32 } &&
                token.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '_' or '-'));
        return $"{os}/{architecture}" + (variant is null ? "" : "/" + variant);
    }

    private static void Annotations(JsonElement element)
    {
        if (!element.TryGetProperty("annotations", out var annotations)) return;
        Require(annotations.ValueKind == JsonValueKind.Object);
        var values = annotations.EnumerateObject().ToArray();
        Require(values.Length <= 64);
        foreach (var value in values)
            Require(value.Name.Length <= 256 && value.Value.ValueKind == JsonValueKind.String &&
                value.Value.GetString()!.Length <= 4096);
    }

    private static JsonElement[] Array(JsonElement element, int maximum)
    {
        Require(element.ValueKind == JsonValueKind.Array && element.GetArrayLength() <= maximum);
        return element.EnumerateArray().ToArray();
    }

    private static void Match(ImageDescriptor descriptor, AcquisitionImageBlob expected)
    {
        if (descriptor.Digest != expected.Digest || descriptor.Bytes != expected.CompressedBytes)
            throw Failure(ArtifactAcquisitionFailure.ImageInventoryMismatch);
    }

    internal static void Digest(string? value) =>
        Require(value is { Length: 71 } && value.StartsWith("sha256:", StringComparison.Ordinal) &&
            value.AsSpan(7).IndexOfAnyExcept("0123456789abcdef") < 0);

    private static T Guarded<T>(Func<T> action)
    {
        try { return action(); }
        catch (Exception error) when (error is JsonException or InvalidOperationException or
            KeyNotFoundException or FormatException or ArgumentException or OverflowException)
        {
            throw Failure(ArtifactAcquisitionFailure.ImageMetadataInvalid);
        }
    }

    private static void Require(bool condition)
    {
        if (!condition) throw Failure(ArtifactAcquisitionFailure.ImageMetadataInvalid);
    }
    private static ArtifactAcquisitionException Failure(ArtifactAcquisitionFailure failure) => new(failure);
    private sealed record ImageDescriptor(string MediaType, string Digest, long Bytes);
}
