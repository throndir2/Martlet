using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Martlet.Core.Planning;

/// <summary>What one source said about one model or route, as read (docs/MODEL_CATALOG.md). <see cref="Id"/> is the model's ID
/// on that source (an OpenRouter ID, an NVIDIA Build ID, a vLLM architecture, an LMArena name). <see cref="Facts"/> holds the
/// <see cref="CatalogFacts"/> keys the source gives, and <see cref="Notes"/> a note for some of them.</summary>
public sealed record CatalogObservation
{
    public required string Id { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; init; }
    /// <summary>The model's Hugging Face repository, when the source links one ("google/gemma-4-26B-A4B-it").</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? HuggingFace { get; init; }
    /// <summary>models.dev's model ID for a provider row (<c>canonical_model_id</c>).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Canonical { get; init; }
    /// <summary>The provider of a models.dev row (Martlet's ID when Martlet names it: "nvidia-build", "google-gemini").</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Provider { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? BaseUrl { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, string>? Facts { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, string>? Notes { get; init; }
    /// <summary>vLLM: the example Hugging Face repositories of the architecture.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Examples { get; init; }
    /// <summary>vLLM: the model names the architecture covers ("Gemma 4").</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Covers { get; init; }
    /// <summary>vLLM: the architecture is in a text-only table (used only when config.json names it).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool TextOnly { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Ollama { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Free { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Expires { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Deprecated { get; init; }
    /// <summary>OpenRouter: where the model's Artificial Analysis intelligence index sits among all scored models, 0 to 100.
    /// Only for ranking inside Martlet; never shown (Artificial Analysis's terms). The index itself is not kept.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Rank { get; init; }
    /// <summary>LMArena: the rating (shown with <see cref="CatalogSources.LmArenaCredit"/>).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Rating { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Votes { get; init; }
    /// <summary>NVIDIA Build: the page it came from ("/qc69jvmznzxy/gemma-4-31b-it.md").</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Page { get; init; }

    public string? Fact(string key) => Facts is not null && Facts.TryGetValue(key, out var value) ? value : null;
}

/// <summary>Everything read from one source at <see cref="Read"/>.</summary>
public sealed record CatalogSourceBlock
{
    public required DateTimeOffset Read { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Url { get; init; }
    public IReadOnlyList<CatalogObservation> Items { get; init; } = [];
}

/// <summary>The catalog file (the snapshot shipped with Martlet and the daily copy): one block per source, as read. The
/// catalog itself (<see cref="ModelCatalog"/>) is worked out from it, so a source that fails keeps its last good block.</summary>
public sealed record ModelCatalogData
{
    public const int CurrentSchema = 1;
    public const long MaximumBytes = 64L * 1024 * 1024;

    public int SchemaVersion { get; init; } = CurrentSchema;
    public DateTimeOffset Built { get; init; }
    public Dictionary<string, CatalogSourceBlock> Sources { get; init; } = new(StringComparer.Ordinal);

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        MaxDepth = 16
    };

    public CatalogSourceBlock? Block(string source) => Sources.TryGetValue(source, out var block) ? block : null;

    /// <summary>This data with <paramref name="block"/> for <paramref name="source"/>.</summary>
    public ModelCatalogData With(string source, CatalogSourceBlock block) =>
        this with { Sources = new(Sources, StringComparer.Ordinal) { [source] = block } };

    /// <summary>The data a file holds; null when it isn't a catalog this Martlet reads.</summary>
    public static ModelCatalogData? Parse(ReadOnlySpan<byte> json)
    {
        try
        {
            var data = JsonSerializer.Deserialize<ModelCatalogData>(json, Json);
            return data is { SchemaVersion: CurrentSchema, Sources: not null } &&
                data.Sources.Values.All(b => b is { Items: not null } && b.Items.All(i => i is { Id.Length: > 0 }))
                ? data with { Sources = new(data.Sources, StringComparer.Ordinal) } : null;
        }
        catch (Exception error) when (error is JsonException or NotSupportedException or ArgumentException) { return null; }
    }

    /// <summary>The file's text: one observation on each line, so a refresh changes only the lines of models that changed.</summary>
    public string Write()
    {
        var text = new StringBuilder();
        text.Append("{\"schemaVersion\":").Append(SchemaVersion).Append(",\"built\":").Append(JsonSerializer.Serialize(Built, Json))
            .Append(",\"sources\":{");
        var first = true;
        foreach (var (name, block) in Sources.OrderBy(s => s.Key, StringComparer.Ordinal))
        {
            text.Append(first ? "\n" : ",\n").Append(JsonSerializer.Serialize(name, Json)).Append(":{\"read\":")
                .Append(JsonSerializer.Serialize(block.Read, Json));
            if (block.Url is { } url) text.Append(",\"url\":").Append(JsonSerializer.Serialize(url, Json));
            text.Append(",\"items\":[");
            for (var index = 0; index < block.Items.Count; index++)
                text.Append(index == 0 ? "\n" : ",\n").Append(JsonSerializer.Serialize(block.Items[index], Json));
            text.Append("\n]}");
            first = false;
        }
        return text.Append("\n}}\n").ToString();
    }
}
