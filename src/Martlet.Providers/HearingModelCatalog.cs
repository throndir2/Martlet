using System.Text.RegularExpressions;
using Martlet.Core.Settings;

namespace Martlet.Providers;

/// <summary>Whether a text model can also take recorded audio in the same request. Unknown means Martlet has no record either
/// way, so only the transcript is sent.</summary>
public enum HearingSupport { Supported, Unsupported, Unknown }

/// <summary>Name-based audio-input capability of the conversation model, like <see cref="VisionModelCatalog"/>: a curated,
/// conservative list of model families that take audio through the OpenAI-compatible Chat Completions <c>input_audio</c>
/// content part (OpenAI's audio models, Gemini, Qwen Omni and Audio, Gemma 3n and the small Gemma 4 models, Phi-4 multimodal,
/// Voxtral, Ultravox...). Other known families are Unsupported; everything else is Unknown.</summary>
public static partial class HearingModelCatalog
{
    private static readonly string[] SupportedMarkers =
    [
        "gpt4oaudio", "gpt4ominiaudio", "gptaudio", "qwen2audio", "qwenaudio", "qwen25omni", "qwen3omni", "qwenomni",
        "phi4multimodal", "voxtral", "ultravox", "minicpmo", "gemma3n", "granitespeech", "kimiaudio", "stepaudio", "audioflamingo"
    ];

    /// <summary>Classifies an upstream model ID, for example <c>gpt-4o-audio-preview</c>, <c>gemini-2.5-flash</c> or
    /// <c>google/gemma-4-e4b-it</c>.</summary>
    public static HearingSupport Classify(string? modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId)) return HearingSupport.Unknown;
        var name = modelId.Trim().ToLowerInvariant();
        name = name[(name.LastIndexOf('/') + 1)..];
        var compact = Compact().Replace(name, "");
        if (SupportedMarkers.Any(compact.Contains)) return HearingSupport.Supported;
        // Every Gemini from 1.5 hears; Gemini 1.0 and the embedding models don't.
        if (compact.Contains("gemini"))
            return compact.Contains("embedding") || compact.StartsWith("gemini10") || compact is "geminipro" or "geminiprovision"
                ? HearingSupport.Unsupported : HearingSupport.Supported;
        // Gemma 4's small edge models (E2B, E4B) hear; the larger ones only see.
        if (Gemma4().Match(name) is { Success: true } gemma)
            return gemma.Groups["edge"].Success ? HearingSupport.Supported : HearingSupport.Unsupported;
        // Any other family Martlet knows (text-only or vision) takes no audio.
        return VisionModelCatalog.Classify(modelId) == VisionSupport.Unknown ? HearingSupport.Unknown : HearingSupport.Unsupported;
    }

    /// <summary>Whether the Thinking route's model hears: only a Chat Completions route takes the <c>input_audio</c> part (OpenAI's
    /// Responses route and a paired host's Ollama take none), a retired model none; then what Martlet found out about the model
    /// on that server (<paramref name="abilities"/>: its model metadata, a test request or a refused recording), and only
    /// without that the model's name (<see cref="Classify"/>). Ollama on this PC takes audio for models it says hear
    /// (Ollama 0.35 and later, for example Gemma 4).</summary>
    public static HearingSupport ForRoute(SetupRouteType? routeType, string? origin, string? modelId, ModelAbilities? abilities,
        bool retired = false)
    {
        if (routeType != SetupRouteType.ChatCompletions || retired) return HearingSupport.Unsupported;
        if (abilities?.Find(origin, modelId)?.Hears is { } hears) return hears ? HearingSupport.Supported : HearingSupport.Unsupported;
        return Classify(modelId);
    }

    /// <summary>Whether a recording sent to this Thinking route stays on this PC: Ollama on this PC (the Chat Completions route at
    /// <see cref="GenerationSupport.LocalOllamaChatBaseUrl"/>) with a model that isn't one Ollama forwards to its cloud (a
    /// <c>:cloud</c> or <c>-cloud</c> tag, such as <c>gpt-oss:120b-cloud</c>). Another server on this PC's loopback isn't
    /// counted: it may be a proxy (LiteLLM and the like) that sends the audio on. Only then does Thinking hear the user's voice
    /// without them ticking Companion › Listening › Let Thinking hear my voice.</summary>
    public static bool StaysOnThisPc(SetupRouteType? routeType, string? origin, string? modelId)
    {
        if (routeType != SetupRouteType.ChatCompletions || string.IsNullOrWhiteSpace(modelId) ||
            !string.Equals(origin?.TrimEnd('/'), GenerationSupport.LocalOllamaChatBaseUrl, StringComparison.Ordinal))
            return false;
        var model = modelId.Trim();
        return !model.EndsWith(":cloud", StringComparison.OrdinalIgnoreCase) && !model.EndsWith("-cloud", StringComparison.OrdinalIgnoreCase);
    }

    [GeneratedRegex("[^a-z0-9]")]
    private static partial Regex Compact();

    [GeneratedRegex(@"^gemma-?4(?:[-:._]?(?<edge>e[24]b))?")]
    private static partial Regex Gemma4();
}
