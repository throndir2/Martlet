using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Martlet.Core.Planning;

namespace Martlet.Providers.LocalModels;

/// <summary>One Ollama tag as the registry gives it: the exact size of its weights, picture and sound encoder (an
/// <c>image.projector</c> layer, which means the model sees) and speculative-decoding draft (<c>image.draft</c>), and from its config
/// blob the quantization (<c>file_type</c>), parameter count (<c>model_type</c>) and family. <see cref="Digest"/> is the
/// manifest's SHA-256, the digest Ollama shows for the model.</summary>
public sealed record OllamaRegistryTag(string Name, long WeightsBytes, long EncoderBytes, long DraftBytes, string? Quantization,
    long? Parameters, string? Family, string? Digest)
{
    public bool HasProjector => EncoderBytes > 0;
}

/// <summary>Reads one Ollama tag from <c>registry.ollama.ai</c> (the requests <c>ollama pull</c> makes), never ollama.com: its terms
/// forbid automated access and its robots.txt disallows <c>/api/</c>. Two requests a tag (the manifest and its small config blob),
/// cached and budgeted (<see cref="PublicMetadataSource"/>).</summary>
public sealed partial class OllamaRegistryReader
{
    public static Uri DefaultOrigin { get; } = new("https://registry.ollama.ai/");
    private const string Manifest = "application/vnd.docker.distribution.manifest.v2+json";
    private readonly PublicMetadataSource source;
    private readonly Uri origin;

    internal OllamaRegistryReader(PublicMetadataSource source, Uri origin)
    {
        this.source = source;
        this.origin = origin;
    }

    public int Sent => source.Sent;

    /// <summary>A registry tag's parts ("gemma4:e2b" is library, gemma4, e2b; "user/model" is user, model, latest); null for a name
    /// that isn't one (a Hugging Face install name or one with characters Ollama doesn't use).</summary>
    public static (string Namespace, string Model, string Tag)? Parse(string? name)
    {
        if (name is null || name.StartsWith("hf.co/", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("huggingface.co/", StringComparison.OrdinalIgnoreCase)) return null;
        var match = TagName().Match(name.Trim().ToLowerInvariant());
        if (!match.Success) return null;
        return (match.Groups["ns"].Success ? match.Groups["ns"].Value : "library", match.Groups["model"].Value,
            match.Groups["tag"].Success ? match.Groups["tag"].Value : "latest");
    }

    public async Task<(OllamaRegistryTag? Tag, string? Problem)> TagAsync(string name, CancellationToken token)
    {
        if (Parse(name) is not { } parts) return (null, $"{name} isn't an Ollama registry tag");
        var (space, model, tag) = parts;
        var manifest = await source.GetAsync(new Uri(origin, $"v2/{space}/{model}/manifests/{tag}"), Manifest, token).ConfigureAwait(false);
        if (!manifest.Ok) return (null, manifest.Status == 404 ? $"The Ollama registry has no {space}/{model}:{tag}" : manifest.Problem);
        long weights = 0, encoder = 0, draft = 0;
        string? config = null;
        try
        {
            using var document = JsonDocument.Parse(manifest.Body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("layers", out var layers) || layers.ValueKind != JsonValueKind.Array)
                return (null, $"The Ollama registry's manifest for {model}:{tag} has no layers");
            foreach (var layer in layers.EnumerateArray().Take(64))
            {
                var size = layer.TryGetProperty("size", out var s) && s.TryGetInt64(out var bytes) && bytes > 0 ? bytes : 0;
                switch (layer.TryGetProperty("mediaType", out var type) ? type.GetString() : null)
                {
                    case "application/vnd.ollama.image.model": weights += size; break;
                    case "application/vnd.ollama.image.projector": encoder += size; break;
                    case "application/vnd.ollama.image.draft": draft += size; break;
                }
            }
            if (root.TryGetProperty("config", out var c) && c.TryGetProperty("digest", out var d) && d.GetString() is { } digest &&
                BlobDigest().IsMatch(digest))
                config = digest;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException)
        {
            return (null, $"The Ollama registry's manifest for {model}:{tag} wasn't JSON Martlet can read");
        }
        if (weights <= 0) return (null, $"The Ollama registry's {model}:{tag} has no model weights");
        string? quantization = null, family = null;
        long? parameters = null;
        if (config is not null)
        {
            var blob = await source.GetAsync(new Uri(origin, $"v2/{space}/{model}/blobs/{config}"), null, token).ConfigureAwait(false);
            if (blob.Ok)
                try
                {
                    using var document = JsonDocument.Parse(blob.Body);
                    var root = document.RootElement;
                    quantization = Text(root, "file_type");
                    family = Text(root, "model_family");
                    parameters = LocalModelQuantizations.ParseParameters(Text(root, "model_type"));
                }
                catch (JsonException) { }
        }
        var full = space == "library" ? $"{model}:{tag}" : $"{space}/{model}:{tag}";
        return (new(full, weights, encoder, draft, quantization, parameters, family,
            Convert.ToHexStringLower(SHA256.HashData(manifest.Body!))), null);
    }

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    [GeneratedRegex(@"^(?:(?<ns>[a-z0-9][a-z0-9._-]{0,63})/)?(?<model>[a-z0-9][a-z0-9._-]{0,127})(?::(?<tag>[a-z0-9][a-z0-9._-]{0,127}))?$")]
    private static partial Regex TagName();

    [GeneratedRegex(@"^sha256:[0-9a-f]{64}$")]
    private static partial Regex BlobDigest();
}
