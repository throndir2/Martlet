using System.Text.Json;
using System.Text.Json.Serialization;

namespace Martlet.Core.Settings;

/// <summary>What a Thinking model takes besides text, as Martlet found out for one server (<see cref="Origin"/>, its base URL)
/// and model: <see cref="Hears"/> recorded audio (the Chat Completions <c>input_audio</c> part) and <see cref="Sees"/>
/// pictures; null when nothing said either way. <see cref="Source"/> says where it came from in words ("OpenRouter's model
/// list", "Ollama on this PC", "a test request", "a refused recording"). Never a key or anything said.</summary>
public sealed record ModelAbility
{
    public required string Origin { get; init; }
    public required string ModelId { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Hears { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Sees { get; init; }
    public required string Source { get; init; }
    public required DateTimeOffset CheckedAt { get; init; }
}

/// <summary>What Thinking models can hear and see, as Martlet found out (model-abilities.json in the data directory): from the
/// provider's own model metadata when a model is chosen, tested or checked (OpenRouter's input modalities, Ollama's
/// capabilities, llama.cpp's modalities), from Companion › Listening › Test hearing, and from a model refusing a recording.
/// It overrides Martlet's name-based guesses (<c>HearingModelCatalog</c>, <c>VisionModelCatalog</c>) and travels to the owner's
/// other computers as the <c>model-abilities</c> shared setting, so a model is found out once for all of them.</summary>
public sealed record ModelAbilities
{
    public const string FileName = "model-abilities.json";
    public const int MaximumModels = 64;
    private static readonly JsonSerializerOptions Canonical = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        RespectNullableAnnotations = true,
        MaxDepth = 8
    };
    private static readonly JsonSerializerOptions Indented = new(Canonical) { WriteIndented = true };

    public int SchemaVersion { get; init; } = 1;
    public IReadOnlyList<ModelAbility> Models { get; init; } = [];

    public ModelAbility? Find(string? origin, string? modelId) =>
        Models.FirstOrDefault(m => string.Equals(m.Origin, origin, StringComparison.Ordinal) &&
            string.Equals(m.ModelId, modelId, StringComparison.Ordinal));

    /// <summary>This list with <paramref name="ability"/> first. A field it leaves unknown keeps what was known before (a model
    /// list that says nothing about audio doesn't erase a test's answer).</summary>
    public ModelAbilities With(ModelAbility ability)
    {
        ArgumentNullException.ThrowIfNull(ability);
        var before = Find(ability.Origin, ability.ModelId);
        var merged = ability with
        {
            Hears = ability.Hears ?? before?.Hears,
            Sees = ability.Sees ?? before?.Sees,
            CheckedAt = ability.CheckedAt.ToUniversalTime()
        };
        return this with
        {
            Models = [merged, .. Models.Where(m => m.Origin != ability.Origin || m.ModelId != ability.ModelId).Take(MaximumModels - 1)]
        };
    }

    private static bool Valid(ModelAbility? m) =>
        m is { Origin.Length: > 0 and <= 2048, ModelId.Length: > 0 and <= 256, Source.Length: > 0 and <= 200 } && (m.Hears is not null || m.Sees is not null);

    /// <summary>The canonical JSON every computer writes for the same list (sorted by server and model), for sharing.</summary>
    public string Share() => JsonSerializer.Serialize(new ModelAbilities
    {
        Models = [.. Models.Where(Valid).Take(MaximumModels).Select(m => m with { CheckedAt = m.CheckedAt.ToUniversalTime() })
            .OrderBy(m => m.Origin, StringComparer.Ordinal).ThenBy(m => m.ModelId, StringComparer.Ordinal)]
    }, Canonical);

    /// <summary>A list another computer shared (<see cref="Share"/>); null when it isn't one this Martlet reads.</summary>
    public static ModelAbilities? Parse(string json)
    {
        try
        {
            var parsed = JsonSerializer.Deserialize<ModelAbilities>(json, Canonical);
            return parsed is { SchemaVersion: 1, Models: not null } && parsed.Models.All(Valid) && parsed.Models.Count <= MaximumModels
                ? parsed : null;
        }
        catch (Exception error) when (error is JsonException or NotSupportedException) { return null; }
    }

    public static ModelAbilities Load(string? directory)
    {
        if (directory is null) return new();
        try
        {
            var path = Path.Combine(directory, FileName);
            if (!File.Exists(path)) return new();
            var loaded = JsonSerializer.Deserialize<ModelAbilities>(File.ReadAllText(path), Canonical);
            return loaded is { SchemaVersion: 1 } ? new() { Models = [.. (loaded.Models ?? []).Where(Valid).Take(MaximumModels)] } : new();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or NotSupportedException) { return new(); }
    }

    public bool Save(string? directory)
    {
        if (directory is null) return false;
        try
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, FileName);
            var temporary = Path.Combine(directory, $"model-abilities.{Guid.NewGuid():N}.tmp");
            try
            {
                File.WriteAllText(temporary, JsonSerializer.Serialize(this with { Models = [.. Models.Where(Valid).Take(MaximumModels)] }, Indented));
                File.Move(temporary, path, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }
}
