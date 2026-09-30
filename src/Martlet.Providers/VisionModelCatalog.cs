using System.Text.RegularExpressions;

namespace Martlet.Providers;

/// <summary>Whether a text model can also take an image in the same request. Unknown means Martlet has no record
/// either way (an arbitrary Chat Completions model ID); the provider's own answer is the only proof.</summary>
public enum VisionSupport { Supported, Unsupported, Unknown }

/// <summary>A locally hostable model (an Ollama tag) that both talks and sees, for the host's Thinking role or a
/// same-PC OpenAI-compatible server (Ollama, LM Studio, llama.cpp).</summary>
public sealed record LocalVisionModel(string Tag, string Memory, string Why);

/// <summary>Name-based vision capability of the conversation model. There is no provider capability discovery API that
/// all routes share, so this is a curated, conservative list: known multimodal families are Supported, known text-only
/// families are Unsupported and everything else is Unknown.</summary>
public static partial class VisionModelCatalog
{
    public static IReadOnlyList<LocalVisionModel> LocalRecommendations { get; } = Array.AsReadOnly(new[]
    {
        new LocalVisionModel("gemma3:4b", "about 4 GB of GPU memory (also runs on the CPU)", "small, talks and sees; the easy default"),
        new LocalVisionModel("qwen2.5vl:7b", "about 6-8 GB", "best at reading on-screen text and game HUDs at this size"),
        new LocalVisionModel("gemma3:12b", "about 9-11 GB", "a smarter talker that also sees"),
        new LocalVisionModel("gemma3:27b", "about 18-20 GB", "the strongest single-GPU option")
    });

    private static readonly string[] SupportedMarkers =
    [
        "vision", "qwen25vl", "qwen2vl", "qwen3vl", "qwenvl", "qwen25omni", "qwen3omni", "internvl", "kimivl", "nanovl",
        "llava", "minicpmv", "moondream", "pixtral", "multimodal", "llama4", "mistralsmall31", "mistralsmall32",
        "mistralmedium3", "gpt4o", "gpt41", "gpt5", "gpt4turbo", "gemini", "claude", "grok4", "glm4v", "glm45v",
        "smolvlm", "paligemma", "idefics"
    ];

    private static readonly string[] TextOnlyMarkers =
    [
        "llama", "qwen", "qwq", "mistral", "mixtral", "ministral", "codestral", "devstral", "magistral", "phi",
        "deepseek", "gemma", "gptoss", "gpt35", "smollm", "granite", "falcon", "olmo", "starcoder", "nemotron",
        "commandr", "hermes", "dolphin", "openchat", "zephyr", "solar", "wizardlm", "vicuna", "orca", "stablelm", "yi"
    ];

    /// <summary>Classifies an upstream model ID or host model alias, for example <c>gpt-4.1-mini-2025-04-14</c>,
    /// <c>gemma3-4b</c> (the host alias of <c>gemma3:4b</c>) or <c>meta/llama-3.2-90b-vision-instruct</c>.</summary>
    public static VisionSupport Classify(string? modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId)) return VisionSupport.Unknown;
        if (OpenAiTextGenerationCatalog.SupportsModel(modelId)) return VisionSupport.Supported;
        var name = modelId.Trim().ToLowerInvariant();
        name = name[(name.LastIndexOf('/') + 1)..];
        var compact = Compact().Replace(name, "");
        // Gemma 3 sees from 4B up; the 1B and 270M sizes and the gemma3n family are text-only in Ollama.
        if (Gemma3().Match(name) is { Success: true } gemma)
            return gemma.Groups["n"].Success || gemma.Groups["size"].Value is "1b" or "270m"
                ? VisionSupport.Unsupported : VisionSupport.Supported;
        // OpenAI reasoning models: o1/o3/o4 see, except the o1-mini and o3-mini text models.
        if (Reasoning().Match(name) is { Success: true } reasoning)
            return reasoning.Groups["mini"].Success && reasoning.Groups["n"].Value is "1" or "3"
                ? VisionSupport.Unsupported : VisionSupport.Supported;
        if (SupportedMarkers.Any(compact.Contains) || VlToken().IsMatch(name)) return VisionSupport.Supported;
        if (TextOnlyMarkers.Any(compact.StartsWith)) return VisionSupport.Unsupported;
        return VisionSupport.Unknown;
    }

    public static string DescribeLocalOptions() =>
        string.Join("; ", LocalRecommendations.Select(m => $"{m.Tag} ({m.Memory}: {m.Why})"));

    [GeneratedRegex("[^a-z0-9]")]
    private static partial Regex Compact();

    [GeneratedRegex(@"^gemma-?3(?<n>n)?(?:[-:._]?(?<size>\d+(?:\.\d+)?[bm]))?")]
    private static partial Regex Gemma3();

    [GeneratedRegex(@"^o(?<n>\d)(?:-(?<mini>mini))?(?:$|[-:._])")]
    private static partial Regex Reasoning();

    // A standalone "vl" token or suffix, for example qwen2.5-vl, qwen2.5vl or deepseek-vl2.
    [GeneratedRegex(@"(?:^|[-:._])[a-z0-9.]*?vl\d*(?:$|[-:._])")]
    private static partial Regex VlToken();
}
