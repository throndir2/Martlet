using System.Globalization;
using System.Text.Json.Serialization;

namespace Martlet.Core.Planning;

/// <summary>The facts the model catalog keeps (docs/MODEL_CATALOG.md). Each is a key in <see cref="CatalogModel.Facts"/> and
/// <see cref="CatalogRoute.Facts"/>. Yes/no facts hold <see cref="CatalogValues.Yes"/> or <see cref="CatalogValues.No"/>;
/// parameters are billions; context is tokens; dates are yyyy-MM-dd or yyyy-MM.</summary>
public static class CatalogFacts
{
    public const string InputText = "input.text";
    public const string InputImage = "input.image";
    public const string InputAudio = "input.audio";
    /// <summary>The model or route takes a video (the server or the model may turn it into frames itself).</summary>
    public const string InputVideo = "input.video";
    /// <summary>Martlet can send video as frames, because the model sees pictures (worked out from <see cref="InputImage"/>).</summary>
    public const string InputVideoFrames = "input.video_frames";
    public const string OutputText = "output.text";
    public const string OutputImage = "output.image";
    public const string OutputAudio = "output.audio";
    public const string OpenWeights = "open_weights";
    public const string License = "license";
    public const string ReleaseDate = "release_date";
    public const string KnowledgeCutoff = "knowledge_cutoff";
    public const string ParametersTotal = "parameters.total";
    public const string ParametersActive = "parameters.active";
    public const string Context = "context";
    public const string Tools = "tools";
    public const string Reasoning = "reasoning";

    public static IReadOnlyList<string> ModelFacts { get; } = Array.AsReadOnly(
    [
        InputText, InputImage, InputAudio, InputVideo, InputVideoFrames, OutputText, OutputImage, OutputAudio, OpenWeights, License,
        ReleaseDate, KnowledgeCutoff, ParametersTotal, ParametersActive, Context, Tools, Reasoning
    ]);

    public static IReadOnlyList<string> RouteFacts { get; } = Array.AsReadOnly(
    [
        InputText, InputImage, InputAudio, InputVideo, InputVideoFrames, OutputText, OutputImage, OutputAudio, Context, Tools, Reasoning
    ]);

    internal enum Kind { YesNo, Number, Date, Text }

    internal static Kind KindOf(string key) => key switch
    {
        License => Kind.Text,
        ReleaseDate or KnowledgeCutoff => Kind.Date,
        ParametersTotal or ParametersActive or Context => Kind.Number,
        _ => Kind.YesNo
    };
}

public static class CatalogValues
{
    public const string Yes = "yes";
    public const string No = "no";
    /// <summary>The source says only some variants or sizes take it (vLLM's <c>*</c> footnote), so it is no answer.</summary>
    public const string Some = "some";

    public static string Of(bool value) => value ? Yes : No;

    public static string Of(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}

/// <summary>The sources the catalog reads, and how far each is trusted (docs/MODEL_CATALOG.md, What to do about conflicts).</summary>
public static class CatalogSources
{
    /// <summary>A Martlet test on the route (Test hearing, a test request, a refused input): model-abilities.json.</summary>
    public const string MartletTest = "martlet-test";
    /// <summary>The model's own config.json on Hugging Face (the local model facts).</summary>
    public const string ConfigJson = "config.json";
    /// <summary>The model's Hugging Face repository (license, parameter count).</summary>
    public const string HuggingFace = "huggingface";
    public const string Vllm = "vllm";
    public const string OpenRouter = "openrouter";
    /// <summary>models.dev's own model records (models.json).</summary>
    public const string ModelsDev = "models.dev";
    /// <summary>models.dev's rows for each provider (api.json); a route counts only its own provider's row.</summary>
    public const string ModelsDevRows = "models.dev-rows";
    public const string NvidiaBuild = "nvidia-build";
    public const string LmArenaText = "lmarena-text";
    public const string LmArenaVision = "lmarena-vision";
    /// <summary>Parsed from the model's name ("26B-A4B").</summary>
    public const string Name = "name";
    /// <summary>Martlet's own list of the local models it installs (their Ollama tags and Hugging Face repositories).</summary>
    public const string Martlet = "martlet";

    /// <summary>The credit LMArena's CC-BY-4.0 license asks for wherever a rating is shown.</summary>
    public const string LmArenaCredit = "LMArena (CC-BY-4.0)";

    /// <summary>The sources the daily refresh reads, in the order it reads them.</summary>
    public static IReadOnlyList<string> Fetched { get; } =
        Array.AsReadOnly([OpenRouter, ModelsDev, ModelsDevRows, NvidiaBuild, Vllm, LmArenaText, LmArenaVision]);

    public static string Describe(string source) => source switch
    {
        MartletTest => "a Martlet test on this route",
        ConfigJson => "the model's config.json",
        HuggingFace => "the model's Hugging Face page",
        Vllm => "vLLM's supported models",
        OpenRouter => "OpenRouter's model list",
        ModelsDev => "models.dev's model record",
        ModelsDevRows => "models.dev's provider list",
        NvidiaBuild => "NVIDIA Build's model page",
        LmArenaText or LmArenaVision => LmArenaCredit,
        Name => "the model's name",
        Martlet => "Martlet's own list",
        _ => source
    };

    /// <summary>How far a source is trusted for a model fact (what the weights take), 1 first: the maker (config.json, the
    /// Hugging Face repository, vLLM by architecture), then catalogs and servers that describe the model, then the name.</summary>
    public static int ModelLevel(string source) => source switch
    {
        ConfigJson or HuggingFace or Vllm => 1,
        OpenRouter or ModelsDev or NvidiaBuild => 2,
        Name => 3,
        _ => 4
    };
}

/// <summary>One source's answer for one fact: what it said (<see cref="Value"/>; <see cref="CatalogValues.Some"/> is no
/// answer), a note in words ("Gemma4ForConditionalGeneration, by family name"), and when it was read. A null
/// <see cref="Checked"/> means the date the catalog read that source (<see cref="ModelCatalog.SourceDates"/>).</summary>
public sealed record CatalogAnswer
{
    public required string Source { get; init; }
    public required string Value { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Note { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? Checked { get; init; }
}

/// <summary>One fact with every source's answer and the answer worked out from them (<see cref="Value"/>, null when no source
/// said or sources at the same level disagree: <see cref="Disagree"/>). <see cref="From"/> is the source that decided.</summary>
public sealed record CatalogFact
{
    public static CatalogFact Unknown { get; } = new();

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Value { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? From { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Disagree { get; init; }
    public IReadOnlyList<CatalogAnswer> Answers { get; init; } = [];

    /// <summary>True for yes, false for no, null when unknown.</summary>
    [JsonIgnore]
    public bool? Yes => Value switch { CatalogValues.Yes => true, CatalogValues.No => false, _ => null };

    [JsonIgnore]
    public double? Number => Value is { } value && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : null;
}
