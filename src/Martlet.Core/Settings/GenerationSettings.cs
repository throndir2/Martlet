using System.Text.Json.Serialization;
using Martlet.Core.Contracts;

namespace Martlet.Core.Settings;

/// <summary>Optional generation (sampling) settings for the Thinking model's replies, set on Companion > Replies and used by
/// every reply. A null value sends nothing, so the model or server keeps its own default. Each route sends only what its
/// API accepts (see <see cref="GenerationSupport"/>): OpenAI takes temperature and top P; Chat Completions servers also take
/// the frequency/presence penalties, plus top K, min P and repetition penalty where the server supports them (OpenRouter,
/// vLLM, LM Studio, llama.cpp); a paired host's Ollama takes all of them and the context size.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GenerationSettings : IContract
{
    /// <summary>A safety ceiling, not how long replies are: each reply is asked to stay short (see the conversation's reply
    /// length instruction), so this only stops a runaway answer and leaves room for a reasoning model's hidden thinking.</summary>
    public const int DefaultMaxReplyTokens = 1_024;
    public const int MinimumReplyTokens = 16;
    public const int MaximumReplyTokens = 2_048;
    /// <summary>The temperature a paired host's Ollama uses when it is left unset.</summary>
    public const double DefaultHostTemperature = 0.7;
    public const int MaximumTopK = 1_000;
    /// <summary>The context window a paired host's Ollama loads when it is left unset; larger windows cost GPU memory.</summary>
    public const int DefaultHostContextTokens = 8_192;
    public const int MinimumContextTokens = 2_048;
    public const int MaximumContextTokens = 32_768;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Temperature { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? TopP { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? TopK { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? MinP { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? RepeatPenalty { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? FrequencyPenalty { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? PresencePenalty { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MaxReplyTokens { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ContextTokens { get; init; }

    [JsonIgnore]
    public bool IsDefault => Temperature is null && TopP is null && TopK is null && MinP is null && RepeatPenalty is null &&
        FrequencyPenalty is null && PresencePenalty is null && MaxReplyTokens is null && ContextTokens is null;

    /// <summary>The reply token budget requested from the model (the default when unset).</summary>
    [JsonIgnore]
    public int ReplyTokens => MaxReplyTokens ?? DefaultMaxReplyTokens;

    /// <summary>Null when nothing is set, so untouched settings keep their original saved shape.</summary>
    public static GenerationSettings? Normalize(GenerationSettings? settings) =>
        settings is null || settings.IsDefault ? null : settings;

    public void Validate()
    {
        ContractRules.Require(Temperature is null || InRange(Temperature.Value, 0, 2),
            "Temperature must be a number from 0 through 2.");
        ContractRules.Require(TopP is null || InRange(TopP.Value, 0, 1) && TopP.Value > 0,
            "Top P must be a number greater than 0 and at most 1.");
        ContractRules.Require(TopK is null or >= 1 and <= MaximumTopK,
            $"Top K must be a whole number from 1 through {MaximumTopK}.");
        ContractRules.Require(MinP is null || InRange(MinP.Value, 0, 1),
            "Min P must be a number from 0 through 1.");
        ContractRules.Require(RepeatPenalty is null || InRange(RepeatPenalty.Value, 0, 2),
            "Repeat penalty must be a number from 0 through 2.");
        ContractRules.Require(FrequencyPenalty is null || InRange(FrequencyPenalty.Value, -2, 2),
            "Frequency penalty must be a number from -2 through 2.");
        ContractRules.Require(PresencePenalty is null || InRange(PresencePenalty.Value, -2, 2),
            "Presence penalty must be a number from -2 through 2.");
        ContractRules.Require(MaxReplyTokens is null or >= MinimumReplyTokens and <= MaximumReplyTokens,
            $"Max reply length must be a whole number of tokens from {MinimumReplyTokens} through {MaximumReplyTokens}.");
        ContractRules.Require(ContextTokens is null or >= MinimumContextTokens and <= MaximumContextTokens,
            $"Context size must be a whole number of tokens from {MinimumContextTokens} through {MaximumContextTokens}.");
        ContractRules.Require(ContextTokens is null || ContextTokens > ReplyTokens,
            "Context size must be larger than the max reply length.");
    }

    private static bool InRange(double value, double minimum, double maximum) =>
        double.IsFinite(value) && value >= minimum && value <= maximum;
}

/// <summary>Which generation settings a Thinking route sends, so the Replies page can mark the others as unused.</summary>
public enum GenerationSetting
{
    MaxReplyTokens, Temperature, TopP, TopK, MinP, RepeatPenalty, FrequencyPenalty, PresencePenalty, ContextTokens
}

public enum GenerationSettingUse
{
    /// <summary>Sent and honored by the route's API.</summary>
    Used,
    /// <summary>Sent when set; some servers behind the route ignore or reject it.</summary>
    ServerDependent,
    /// <summary>Never sent on this route.</summary>
    Unused
}

public static class GenerationSupport
{
    public const string OpenAiChatHost = "api.openai.com";

    /// <summary>The OpenAI-compatible endpoint of Ollama running on this PC; it ignores Ollama-only sampling options.</summary>
    public const string LocalOllamaChatBaseUrl = "http://127.0.0.1:11434/v1";

    /// <summary>How a route of the given type and Chat Completions base URL treats a setting.</summary>
    public static GenerationSettingUse Use(SetupRouteType? routeType, string? chatBaseUrl, GenerationSetting setting)
    {
        if (routeType == SetupRouteType.GatewayOllama) return GenerationSettingUse.Used;
        if (setting == GenerationSetting.ContextTokens) return GenerationSettingUse.Unused;
        if (setting is GenerationSetting.MaxReplyTokens or GenerationSetting.Temperature or GenerationSetting.TopP)
            return GenerationSettingUse.Used;
        if (routeType != SetupRouteType.ChatCompletions) return GenerationSettingUse.Unused;
        if (setting is GenerationSetting.FrequencyPenalty or GenerationSetting.PresencePenalty) return GenerationSettingUse.Used;
        return SendsExtendedSamplers(chatBaseUrl)
            ? string.Equals(chatBaseUrl, ChatCompletionsEndpointCatalog.OpenRouterBaseUrl, StringComparison.Ordinal)
                ? GenerationSettingUse.Used : GenerationSettingUse.ServerDependent
            : GenerationSettingUse.Unused;
    }

    /// <summary>Whether top K, min P and repetition penalty are sent to a Chat Completions server: not to OpenAI, which
    /// rejects them, nor to Ollama's OpenAI-compatible endpoint, which ignores them.</summary>
    public static bool SendsExtendedSamplers(string? chatBaseUrl) =>
        Uri.TryCreate(chatBaseUrl, UriKind.Absolute, out var uri) &&
        !string.Equals(uri.Host, OpenAiChatHost, StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(chatBaseUrl, LocalOllamaChatBaseUrl, StringComparison.Ordinal);
}
