using System.Text.Json.Serialization;
using Martlet.Core.Contracts;

namespace Martlet.Core.Settings;

/// <summary>Optional generation (sampling) settings for the Thinking model's replies, set on Companion > Replies and used by
/// every reply. A null value sends nothing, so the model or server keeps its own default. Each route sends only what its
/// API accepts (see <see cref="GenerationSupport"/>): OpenAI takes temperature and top P; Chat Completions servers also take
/// the frequency/presence penalties, plus top K, min P and repetition penalty where the server supports them (OpenRouter,
/// vLLM, LM Studio, llama.cpp); a paired host's Ollama takes all of them and the context size. The context size bounds every
/// route: Martlet keeps each request (persona, lore, memory, the conversation so far and the reply) within it and the
/// model's own limit (see <see cref="ContextBudget"/>).</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GenerationSettings : IContract
{
    /// <summary>A safety ceiling, not how long replies are: each reply is asked to stay short (see the conversation's reply
    /// length instruction), so this only stops a runaway answer and leaves room for a reasoning model's hidden thinking.</summary>
    public const int DefaultMaxReplyTokens = 1_024;
    /// <summary>The reply token budget on a cloud Chat Completions route (OpenRouter, NVIDIA Build, other servers) or a paired
    /// host's Ollama when no max reply length is set. Their max_tokens (Ollama's num_predict) covers a reasoning model's hidden
    /// thinking as well as the answer, so 1,024 tokens of thinking left a few words of reply; the brevity instruction keeps the
    /// answer itself short. It is also the host gateway's largest reply budget.</summary>
    public const int ChatReplyTokens = 4_096;
    public const int MinimumReplyTokens = 16;
    public const int MaximumReplyTokens = 2_048;
    /// <summary>The temperature a paired host's Ollama uses when it is left unset.</summary>
    public const double DefaultHostTemperature = 0.7;
    public const int MaximumTopK = 1_000;
    /// <summary>The context size of a cloud route (OpenAI, OpenRouter, other Chat Completions servers) when it is left unset,
    /// or the model's own limit when that is smaller. Most current cloud models hold far more; a larger size keeps more of a
    /// long conversation in mind but sends more tokens with every reply.</summary>
    public const int DefaultContextTokens = 100_000;
    /// <summary>The context window a paired host's Ollama loads when it is left unset; larger windows cost GPU memory.</summary>
    public const int DefaultHostContextTokens = 8_192;
    public const int MinimumContextTokens = 2_048;
    public const int MaximumContextTokens = 2_000_000;
    /// <summary>The largest context window a paired host's Ollama loads (its gateway's bound); a larger saved size is capped.</summary>
    public const int MaximumHostContextTokens = 32_768;

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

    /// <summary>How a route of the given type and Chat Completions base URL treats a setting. The context size is used by every
    /// route: Martlet keeps each request within it (see <see cref="ContextBudget"/>), and a paired host's Ollama also loads it.</summary>
    public static GenerationSettingUse Use(SetupRouteType? routeType, string? chatBaseUrl, GenerationSetting setting)
    {
        if (routeType == SetupRouteType.GatewayOllama) return GenerationSettingUse.Used;
        if (setting is GenerationSetting.ContextTokens or GenerationSetting.MaxReplyTokens or GenerationSetting.Temperature or
            GenerationSetting.TopP)
            return GenerationSettingUse.Used;
        if (routeType != SetupRouteType.ChatCompletions) return GenerationSettingUse.Unused;
        if (setting is GenerationSetting.FrequencyPenalty or GenerationSetting.PresencePenalty) return GenerationSettingUse.Used;
        return SendsExtendedSamplers(chatBaseUrl)
            ? string.Equals(chatBaseUrl, ChatCompletionsEndpointCatalog.OpenRouterBaseUrl, StringComparison.Ordinal)
                ? GenerationSettingUse.Used : GenerationSettingUse.ServerDependent
            : GenerationSettingUse.Unused;
    }

    /// <summary>Whether a Chat Completions request carries a reply token budget (max_tokens). Ollama on this PC gets none
    /// unless a max reply length is set on Companion › Replies: it costs nothing per token, and thinking models spend the
    /// budget on hidden reasoning before they say anything.</summary>
    public static bool SendsReplyBudget(string? chatBaseUrl, GenerationSettings? settings) =>
        settings?.MaxReplyTokens is not null || !string.Equals(chatBaseUrl, LocalOllamaChatBaseUrl, StringComparison.Ordinal);

    /// <summary>Whether a route's reply token budget also pays for a thinking model's hidden reasoning: Chat Completions
    /// max_tokens and a paired host's Ollama num_predict do; the OpenAI route only offers models that don't reason.</summary>
    public static bool BudgetIncludesThinking(SetupRouteType? routeType) =>
        routeType is SetupRouteType.ChatCompletions or SetupRouteType.GatewayOllama;

    /// <summary>The reply token budget used when no max reply length is set: <see cref="GenerationSettings.ChatReplyTokens"/>
    /// where the budget includes hidden reasoning (<see cref="BudgetIncludesThinking"/>), otherwise
    /// <see cref="GenerationSettings.DefaultMaxReplyTokens"/>.</summary>
    public static int DefaultReplyTokens(SetupRouteType? routeType) =>
        BudgetIncludesThinking(routeType) ? GenerationSettings.ChatReplyTokens : GenerationSettings.DefaultMaxReplyTokens;

    /// <summary>The reply token budget in effect: the saved max reply length, otherwise <see cref="DefaultReplyTokens"/>. A
    /// paired host's gateway needs it below the saved context size, so a small context keeps half of it for the prompt.</summary>
    public static int ReplyTokens(SetupRouteType? routeType, GenerationSettings? settings) =>
        settings?.MaxReplyTokens ?? (routeType == SetupRouteType.GatewayOllama && settings?.ContextTokens is { } context
            ? Math.Min(DefaultReplyTokens(routeType), context / 2) : DefaultReplyTokens(routeType));

    /// <summary>Whether top K, min P and repetition penalty are sent to a Chat Completions server: not to OpenAI, which
    /// rejects them, nor to Ollama's OpenAI-compatible endpoint, which ignores them.</summary>
    public static bool SendsExtendedSamplers(string? chatBaseUrl) =>
        Uri.TryCreate(chatBaseUrl, UriKind.Absolute, out var uri) &&
        !string.Equals(uri.Host, OpenAiChatHost, StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(chatBaseUrl, LocalOllamaChatBaseUrl, StringComparison.Ordinal);

    /// <summary>Whether a streamed Chat Completions request asks for the closing usage chunk (stream_options.include_usage),
    /// which says how much of the input came from the prompt cache: only Ollama on this PC, which reports usage only when
    /// asked. OpenRouter reports it unasked; other servers aren't sent an option they might reject.</summary>
    public static bool AsksStreamUsage(string? chatBaseUrl) =>
        string.Equals(chatBaseUrl?.TrimEnd('/'), LocalOllamaChatBaseUrl, StringComparison.Ordinal);
}
