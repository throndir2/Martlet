using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Martlet.Core.Planning;

namespace Martlet.Providers.LocalModels;

/// <summary>One file in a Hugging Face repository and its size in bytes.</summary>
public sealed record HuggingFaceFile(string Path, long Bytes);

/// <summary>What Hugging Face's model API says about one repository: its task (<c>pipeline_tag</c>), license, the model it was
/// made from (<c>base_model</c>), the maker's parameter count (<c>safetensors.total</c>), a GGUF repository's own parameter count
/// and context (<c>gguf</c>), whether it is gated, and every file with its size.</summary>
public sealed record HuggingFaceModel(string Id, string? PipelineTag, string? License, IReadOnlyList<string> BaseModels, long? Parameters,
    long? GgufParameters, int? GgufContext, bool Gated, IReadOnlyList<HuggingFaceFile> Files)
{
    /// <summary>The repository holds GGUF weights (not only an encoder or a draft).</summary>
    public bool HasGgufWeights => Files.Any(f => HuggingFaceReader.Kind(f.Path) == LocalModelFileKind.Weights);
}

/// <summary>Reads Hugging Face without a key: a repository's model API (one request gives the license, task, parameters, GGUF
/// context and every file's size), its <c>config.json</c>, and the GGUF repositories made from a model
/// (<c>filter=base_model:quantized:{repo}</c>). Answers are cached and requests budgeted (<see cref="PublicMetadataSource"/>).</summary>
public sealed partial class HuggingFaceReader
{
    public static Uri DefaultOrigin { get; } = new("https://huggingface.co/");
    private readonly PublicMetadataSource source;
    private readonly Uri origin;

    internal HuggingFaceReader(PublicMetadataSource source, Uri origin)
    {
        this.source = source;
        this.origin = origin;
    }

    public int Sent => source.Sent;

    /// <summary>Whether <paramref name="text"/> is a repository name such as google/gemma-4-E2B-it.</summary>
    public static bool IsRepo(string? text) => text is not null && RepoName().IsMatch(text);

    public async Task<(HuggingFaceModel? Model, string? Problem)> ModelAsync(string repo, CancellationToken token)
    {
        if (!IsRepo(repo)) return (null, $"{repo} isn't a Hugging Face repository name");
        var answer = await source.GetAsync(new Uri(origin, $"api/models/{repo}?expand%5B%5D=safetensors&expand%5B%5D=cardData" +
            "&expand%5B%5D=gguf&expand%5B%5D=pipeline_tag&expand%5B%5D=siblings&expand%5B%5D=gated&blobs=true"), "application/json", token)
            .ConfigureAwait(false);
        if (!answer.Ok) return (null, answer.Problem);
        try
        {
            using var document = JsonDocument.Parse(answer.Body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return (null, $"Hugging Face's answer for {repo} wasn't a model");
            var card = root.TryGetProperty("cardData", out var c) && c.ValueKind == JsonValueKind.Object ? c : default;
            var gguf = root.TryGetProperty("gguf", out var g) && g.ValueKind == JsonValueKind.Object ? g : default;
            var files = new List<HuggingFaceFile>();
            if (root.TryGetProperty("siblings", out var siblings) && siblings.ValueKind == JsonValueKind.Array)
                foreach (var sibling in siblings.EnumerateArray().Take(2000))
                    if (Text(sibling, "rfilename") is { Length: > 0 and <= 512 } path)
                        files.Add(new(path, Long(sibling, "size") ?? (sibling.TryGetProperty("lfs", out var lfs) ? Long(lfs, "size") : null) ?? 0));
            var license = Text(card, "license") ?? Tags(root).FirstOrDefault(t => t.StartsWith("license:", StringComparison.Ordinal))?[8..];
            var gated = root.TryGetProperty("gated", out var gate) &&
                (gate.ValueKind == JsonValueKind.True || gate.ValueKind == JsonValueKind.String && gate.GetString() is { Length: > 0 } and not "false");
            return (new(Text(root, "id") ?? repo, Text(root, "pipeline_tag") ?? Text(card, "pipeline_tag"), license, BaseModels(card, repo),
                root.TryGetProperty("safetensors", out var tensors) ? Long(tensors, "total") : null, Long(gguf, "total"),
                (int?)Long(gguf, "context_length"), gated, files), null);
        }
        catch (JsonException) { return (null, $"Hugging Face's answer for {repo} wasn't JSON"); }
    }

    /// <summary>The model's shape and inputs from <c>{repo}/raw/main/config.json</c>.</summary>
    public async Task<(LocalModelArchitecture? Architecture, string? Problem)> ConfigAsync(string repo, CancellationToken token)
    {
        if (!IsRepo(repo)) return (null, $"{repo} isn't a Hugging Face repository name");
        var answer = await source.GetAsync(new Uri(origin, $"{repo}/raw/main/config.json"), null, token).ConfigureAwait(false);
        if (!answer.Ok) return (null, answer.Problem);
        return LocalModelArchitecture.FromConfig(Encoding.UTF8.GetString(answer.Body!)) is { } architecture
            ? (architecture, null)
            : (null, $"{repo}'s config.json has no layers or heads Martlet can read");
    }

    /// <summary>GGUF repositories made from <paramref name="baseRepo"/>, most downloaded first (at most 20).</summary>
    public async Task<(IReadOnlyList<string> Repos, string? Problem)> GgufReposAsync(string baseRepo, CancellationToken token)
    {
        if (!IsRepo(baseRepo)) return ([], $"{baseRepo} isn't a Hugging Face repository name");
        var answer = await source.GetAsync(new Uri(origin, $"api/models?filter=base_model:quantized:{baseRepo}&filter=gguf" +
            "&sort=downloads&direction=-1&limit=20"), "application/json", token).ConfigureAwait(false);
        if (!answer.Ok) return ([], answer.Problem);
        try
        {
            using var document = JsonDocument.Parse(answer.Body);
            return document.RootElement.ValueKind == JsonValueKind.Array
                ? ([.. document.RootElement.EnumerateArray().Select(m => Text(m, "id") ?? Text(m, "modelId")).OfType<string>().Where(IsRepo).Take(20)], null)
                : ([], "Hugging Face's search answer wasn't a list");
        }
        catch (JsonException) { return ([], "Hugging Face's search answer wasn't JSON"); }
    }

    /// <summary>What a GGUF file is: an encoder (<c>mmproj</c>), a speculative-decoding draft (<c>mtp</c>, <c>draft</c>), weights,
    /// or null for anything else (imatrix data, README).</summary>
    public static LocalModelFileKind? Kind(string path)
    {
        if (!path.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase)) return null;
        var name = path.Replace('\\', '/').ToLowerInvariant();
        var file = name.Split('/')[^1];
        if (file.Contains("imatrix", StringComparison.Ordinal)) return null;
        if (file.StartsWith("mmproj", StringComparison.Ordinal) || file.Contains("-mmproj", StringComparison.Ordinal)) return LocalModelFileKind.Encoder;
        if (file.StartsWith("mtp", StringComparison.Ordinal) || name.Contains("/mtp/", StringComparison.Ordinal) ||
            name.StartsWith("mtp/", StringComparison.Ordinal) || file.Contains("draft", StringComparison.Ordinal)) return LocalModelFileKind.Draft;
        return LocalModelFileKind.Weights;
    }

    /// <summary>The quantizations in a GGUF repository's files, split parts added together, each with the encoder Ollama would
    /// use beside it (16-bit when there is one); plus the encoder and draft files.</summary>
    public static (IReadOnlyList<LocalModelQuantization> Quantizations, IReadOnlyList<LocalModelFile> Encoders, IReadOnlyList<LocalModelFile> Drafts)
        Quantizations(HuggingFaceModel repo)
    {
        ArgumentNullException.ThrowIfNull(repo);
        LocalModelFile File(HuggingFaceFile f, LocalModelFileKind kind) => new(f.Path, kind, LocalModelQuantizations.FromFileName(f.Path), f.Bytes);
        var encoders = repo.Files.Where(f => Kind(f.Path) == LocalModelFileKind.Encoder).Select(f => File(f, LocalModelFileKind.Encoder)).ToArray();
        var drafts = repo.Files.Where(f => Kind(f.Path) == LocalModelFileKind.Draft).Select(f => File(f, LocalModelFileKind.Draft)).ToArray();
        var encoder = encoders.FirstOrDefault(e => e.Quantization is "BF16" or "F16") ?? encoders.FirstOrDefault();
        var quantizations = repo.Files.Where(f => Kind(f.Path) == LocalModelFileKind.Weights)
            .Select(f => (File: f, Quantization: LocalModelQuantizations.FromFileName(f.Path)))
            .Where(f => f.Quantization is not null)
            .GroupBy(f => f.Quantization!, StringComparer.OrdinalIgnoreCase)
            .Select(group => new LocalModelQuantization(group.Key, group.Sum(f => f.File.Bytes),
                LocalModelQuantizations.HuggingFaceInstallName(repo.Id, group.Key), $"Hugging Face {repo.Id}")
            {
                EncoderBytes = encoder?.Bytes ?? 0
            })
            .OrderBy(q => q.WeightsBytes)
            .ToArray();
        return (quantizations, encoders, drafts);
    }

    private static IReadOnlyList<string> BaseModels(JsonElement card, string repo)
    {
        if (card.ValueKind != JsonValueKind.Object || !card.TryGetProperty("base_model", out var value)) return [];
        IEnumerable<string?> names = value.ValueKind switch
        {
            JsonValueKind.String => [value.GetString()],
            JsonValueKind.Array => value.EnumerateArray().Select(v => v.ValueKind == JsonValueKind.String ? v.GetString() : null),
            _ => []
        };
        return [.. names.OfType<string>().Where(IsRepo).Where(n => !string.Equals(n, repo, StringComparison.OrdinalIgnoreCase)).Take(8)];
    }

    private static IEnumerable<string> Tags(JsonElement root) =>
        root.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Array
            ? tags.EnumerateArray().Where(t => t.ValueKind == JsonValueKind.String).Select(t => t.GetString()!)
            : [];

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    private static long? Long(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt64(out var number) && number >= 0 ? number : null;

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,95}/[A-Za-z0-9][A-Za-z0-9._-]{0,95}$")]
    private static partial Regex RepoName();
}
