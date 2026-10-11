using System.Text.Json;
using System.Text.Json.Serialization;

namespace Martlet.Core.Settings;

/// <summary>What a model takes on one route (a route is one server, <see cref="Origin"/> its base URL, and one model ID), as
/// Martlet found out: <see cref="Hears"/> recorded audio (the Chat Completions <c>input_audio</c> part), <see cref="Sees"/>
/// pictures, <see cref="Video"/> video itself (apart from seeing frames as pictures), <see cref="Tools"/> tool calls, each
/// null when nothing said either way, and <see cref="Retired"/> the date the route answered that the model is gone (HTTP 410).
/// <see cref="Source"/> and <see cref="CheckedAt"/> are the latest finding in words ("OpenRouter's model list", "Ollama on this
/// PC", "a test request", "a refused recording"); <see cref="Sources"/> keeps each fact's own source and date. Never a key or
/// anything said.</summary>
public sealed record ModelAbility
{
    /// <summary>Sources that are a Martlet test on the route itself: they win over the server's metadata (docs/MODEL_CATALOG.md).</summary>
    public const string TestRequest = "a test request", RefusedRecording = "a refused recording", RefusedPicture = "a refused picture",
        GoneAnswer = "an HTTP 410 Gone answer";

    public required string Origin { get; init; }
    public required string ModelId { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Hears { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Sees { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Video { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Tools { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? Retired { get; init; }
    public required string Source { get; init; }
    public required DateTimeOffset CheckedAt { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ModelAbilitySources? Sources { get; init; }

    /// <summary>Whether <paramref name="source"/> is a Martlet test on the route (a test request, a refused input, an HTTP 410).</summary>
    public static bool IsTest(string? source) => source is TestRequest or RefusedRecording or RefusedPicture or GoneAnswer;

    /// <summary>Whether this says anything: a sense, video, tools or that the model is retired.</summary>
    [JsonIgnore]
    public bool Known => Hears is not null || Sees is not null || Video is not null || Tools is not null || Retired is not null;

    /// <summary>Where <paramref name="fact"/> came from and when: its own source, else (a record from an older Martlet) the latest.</summary>
    public ModelAbilitySource? SourceOf(ModelFact fact) =>
        Sources?.Of(fact) ?? (ValueOf(fact) is null ? null : new ModelAbilitySource(Source, CheckedAt));

    internal object? ValueOf(ModelFact fact) => fact switch
    {
        ModelFact.Hears => Hears, ModelFact.Sees => Sees, ModelFact.Video => Video, ModelFact.Tools => Tools, _ => Retired
    };
}

/// <summary>One fact a <see cref="ModelAbility"/> records.</summary>
public enum ModelFact { Hears, Sees, Video, Tools, Retired }

/// <summary>Where one fact came from (<see cref="ModelAbility.Source"/>'s words) and when.</summary>
public sealed record ModelAbilitySource(string Source, DateTimeOffset At);

/// <summary>Each fact's own source and date, so a test's answer isn't overwritten by metadata found later.</summary>
public sealed record ModelAbilitySources
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ModelAbilitySource? Hears { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ModelAbilitySource? Sees { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ModelAbilitySource? Video { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ModelAbilitySource? Tools { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ModelAbilitySource? Retired { get; init; }

    public ModelAbilitySource? Of(ModelFact fact) => fact switch
    {
        ModelFact.Hears => Hears, ModelFact.Sees => Sees, ModelFact.Video => Video, ModelFact.Tools => Tools, _ => Retired
    };
}

/// <summary>What models take on each route, as Martlet found out (model-abilities.json in the data directory): from the
/// provider's own model metadata when a model is chosen, tested or checked (OpenRouter's input modalities and supported
/// parameters, Ollama's capabilities, llama.cpp's modalities, LM Studio's type, NVIDIA Build's model page), from Test hearing,
/// Test vision and Test tools, from a model refusing a recording or picture, and from a route answering HTTP 410 Gone (retired).
/// A test outranks metadata, and both outrank Martlet's name-based guesses (<c>HearingModelCatalog</c>, <c>VisionModelCatalog</c>);
/// docs/MODEL_CATALOG.md. It travels to the owner's other computers as the <c>model-abilities</c> shared setting, so a model is
/// found out once for all of them.</summary>
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
    /// <summary>In a shared value only: the routes that say neither hearing nor seeing (only video, tools or retired). They are
    /// kept apart because an older Martlet reads <see cref="Models"/> only when each one says hearing or seeing; it skips this
    /// list. <see cref="Parse"/> puts them back in <see cref="Models"/>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<ModelAbility>? MoreModels { get; init; }

    public ModelAbility? Find(string? origin, string? modelId) =>
        Models.FirstOrDefault(m => string.Equals(m.Origin, origin, StringComparison.Ordinal) &&
            string.Equals(m.ModelId, modelId, StringComparison.Ordinal));

    /// <summary>This list with <paramref name="ability"/> first. A fact it leaves unknown keeps what was known before (a model
    /// list that says nothing about audio doesn't erase a test's answer), and a Martlet test's answer
    /// (<see cref="ModelAbility.IsTest"/>) isn't replaced by the server's metadata, only by another test. A model found retired
    /// keeps the date it was first found. Each fact keeps its own source and date (<see cref="ModelAbility.Sources"/>).</summary>
    public ModelAbilities With(ModelAbility ability)
    {
        ArgumentNullException.ThrowIfNull(ability);
        var before = Find(ability.Origin, ability.ModelId);
        var said = new ModelAbilitySource(ability.Source, ability.CheckedAt.ToUniversalTime());
        var test = ModelAbility.IsTest(ability.Source);
        var changed = false;
        (T? Value, ModelAbilitySource? From) Pick<T>(ModelFact fact, T? now, T? was, bool keepEarlier = false) where T : struct
        {
            var earlier = before?.SourceOf(fact) is { } first ? first with { At = first.At.ToUniversalTime() } : null;
            if (now is null || was is not null && (keepEarlier || !test && ModelAbility.IsTest(earlier?.Source))) return (was, was is null ? null : earlier);
            changed = true;
            return (now, said);
        }
        var hears = Pick(ModelFact.Hears, ability.Hears, before?.Hears);
        var sees = Pick(ModelFact.Sees, ability.Sees, before?.Sees);
        var video = Pick(ModelFact.Video, ability.Video, before?.Video);
        var tools = Pick(ModelFact.Tools, ability.Tools, before?.Tools);
        var retired = Pick(ModelFact.Retired, ability.Retired?.ToUniversalTime(), before?.Retired?.ToUniversalTime(), keepEarlier: true);
        if (!changed) return this;
        var merged = new ModelAbility
        {
            Origin = ability.Origin, ModelId = ability.ModelId, Hears = hears.Value, Sees = sees.Value, Video = video.Value,
            Tools = tools.Value, Retired = retired.Value, Source = said.Source, CheckedAt = said.At,
            Sources = new() { Hears = hears.From, Sees = sees.From, Video = video.From, Tools = tools.From, Retired = retired.From }
        };
        return this with
        {
            Models = [merged, .. Models.Where(m => m.Origin != ability.Origin || m.ModelId != ability.ModelId).Take(MaximumModels - 1)]
        };
    }

    /// <summary>This list without the retired mark on the route: the model answered again (a reply or a test). A route that then
    /// says nothing else is dropped. The same list when it wasn't marked retired.</summary>
    public ModelAbilities Answered(string? origin, string? modelId)
    {
        if (Find(origin, modelId) is not { Retired: not null } found) return this;
        var cleared = found with { Retired = null, Sources = found.Sources is { } sources ? sources with { Retired = null } : null };
        return this with
        {
            Models = [.. Models.Select(m => ReferenceEquals(m, found) ? cleared : m).Where(m => m.Known)]
        };
    }

    private static bool Valid(ModelAbility? m) =>
        m is { Origin.Length: > 0 and <= 2048, ModelId.Length: > 0 and <= 256, Source.Length: > 0 and <= 200 } && m.Known &&
        (m.Sources is not { } sources || Enum.GetValues<ModelFact>().All(f => sources.Of(f) is not { } said || said.Source.Length is > 0 and <= 200));

    // What an older Martlet reads in Models: a route that says hearing or seeing.
    private static bool OlderReads(ModelAbility m) => m.Hears is not null || m.Sees is not null;

    private static ModelAbility Universal(ModelAbility m) => m with
    {
        CheckedAt = m.CheckedAt.ToUniversalTime(), Retired = m.Retired?.ToUniversalTime(),
        Sources = m.Sources is not { } sources ? null : new()
        {
            Hears = Utc(sources.Hears), Sees = Utc(sources.Sees), Video = Utc(sources.Video), Tools = Utc(sources.Tools),
            Retired = Utc(sources.Retired)
        }
    };

    private static ModelAbilitySource? Utc(ModelAbilitySource? source) => source is null ? null : source with { At = source.At.ToUniversalTime() };

    /// <summary>The canonical JSON every computer writes for the same list (sorted by server and model), for sharing. Routes
    /// that say neither hearing nor seeing go in <see cref="MoreModels"/>, so an older Martlet still reads the rest.</summary>
    public string Share()
    {
        var all = Models.Where(Valid).Take(MaximumModels).Select(Universal)
            .OrderBy(m => m.Origin, StringComparer.Ordinal).ThenBy(m => m.ModelId, StringComparer.Ordinal).ToArray();
        var more = all.Where(m => !OlderReads(m)).ToArray();
        return JsonSerializer.Serialize(new ModelAbilities { Models = [.. all.Where(OlderReads)], MoreModels = more.Length == 0 ? null : more },
            Canonical);
    }

    /// <summary>A list another computer shared (<see cref="Share"/>); null when it isn't one this Martlet reads.</summary>
    public static ModelAbilities? Parse(string json)
    {
        try
        {
            var parsed = JsonSerializer.Deserialize<ModelAbilities>(json, Canonical);
            if (parsed is not { SchemaVersion: 1, Models: not null }) return null;
            IReadOnlyList<ModelAbility> all = [.. parsed.Models, .. parsed.MoreModels ?? []];
            return all.All(Valid) && all.Count <= MaximumModels ? new() { Models = all } : null;
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
            return loaded is { SchemaVersion: 1 }
                ? new() { Models = [.. (loaded.Models ?? []).Concat(loaded.MoreModels ?? []).Where(Valid).Take(MaximumModels)] } : new();
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
                File.WriteAllText(temporary, JsonSerializer.Serialize(this with { Models = [.. Models.Where(Valid).Take(MaximumModels)], MoreModels = null }, Indented));
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
