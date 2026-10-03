using System.Text.RegularExpressions;

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

    [GeneratedRegex("[^a-z0-9]")]
    private static partial Regex Compact();

    [GeneratedRegex(@"^gemma-?4(?:[-:._]?(?<edge>e[24]b))?")]
    private static partial Regex Gemma4();
}
