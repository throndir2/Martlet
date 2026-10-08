using System.Text.Json;
using System.Text.Json.Serialization;
using Martlet.Core.Contracts;

namespace Martlet.Core.Settings;

/// <summary>Optional generation (sampling) settings for the Thinking model's replies, set on Companion > Replies and used by
/// every reply, including whether a reasoning model thinks before it answers (<see cref="Reasoning"/>). A null value sends
/// nothing, so the model or server keeps its own default. Each route sends only what its
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
    /// <summary>Companion › Replies › Thinking steps: whether a reasoning model thinks step by step before it answers. In saved
    /// settings, null (nothing chosen) and false are Off, the default (<see cref="DefaultReasoning"/>): replies start sooner and
    /// spend no tokens on hidden thinking; true asks for it. The desktop resolves it before every request
    /// (<see cref="WithReasoning"/>), so a request always says Off or On; a request without it (after a model refused the
    /// choice, <see cref="WithoutReasoning"/>) leaves the model's own default. Each route sends it the way its API takes it
    /// (<see cref="GenerationSupport.Reasoning"/>).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Reasoning { get; init; }

    /// <summary>Companion › Replies › Thinking longer (<see cref="ThinkLongerSettings"/>); null keeps every default (on).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ThinkLongerSettings? ThinkLonger { get; init; }

    /// <summary>Companion › Replies › Short first sentence: whether every spoken reply is asked to begin with a short first
    /// sentence (Companion › Prompts › Short first sentence), so the voice can start sooner. Null (nothing chosen) is On, the
    /// default (<see cref="DefaultShortFirstSentence"/>); false turns it off. Never sent to a provider: it only adds the prompt
    /// to the instructions (<see cref="PromptSettings.ReplyClosing"/>).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? ShortFirstSentence { get; init; }

    /// <summary>Companion › Replies › Adult content (18+): whether replies and screen remarks may be sexual and explicit with an
    /// adult character, and take touches on intimate parts that way (Companion › Prompts › Adult content). Null (nothing chosen)
    /// is Off, the default; true turns it on. Never sent to a provider: it only adds the prompt to the instructions, and never
    /// in a Discord call.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? AdultContent { get; init; }

    /// <summary>How hard a request with <see cref="Reasoning"/> On thinks, for routes that take an effort (OpenAI's
    /// <c>reasoning_effort</c>, OpenRouter's <c>reasoning.effort</c>): "medium" or "high". Only a background think sets it
    /// (<see cref="ThinkLongerSettings.Effort"/>); never saved, and null sends the route's usual On.</summary>
    [JsonIgnore]
    public string? ReasoningEffort { get; init; }

    /// <summary>Thinking steps when none is chosen: Off, so a reasoning model answers straight away.</summary>
    public const bool DefaultReasoning = false;

    /// <summary>Short first sentence when none is chosen: On.</summary>
    public const bool DefaultShortFirstSentence = true;

    [JsonIgnore]
    public bool IsDefault => Temperature is null && TopP is null && TopK is null && MinP is null && RepeatPenalty is null &&
        FrequencyPenalty is null && PresencePenalty is null && MaxReplyTokens is null && ContextTokens is null && Reasoning is null &&
        ThinkLonger is null && ShortFirstSentence is null && AdultContent is null;

    /// <summary>The reply token budget requested from the model (the default when unset).</summary>
    [JsonIgnore]
    public int ReplyTokens => MaxReplyTokens ?? DefaultMaxReplyTokens;

    /// <summary>Whether Thinking steps is on for <paramref name="settings"/> (unset is Off).</summary>
    public static bool ThinkingSteps(GenerationSettings? settings) => settings?.Reasoning ?? DefaultReasoning;

    /// <summary>Whether spoken replies are asked for a short first sentence with <paramref name="settings"/> (unset is On).</summary>
    public static bool StartsShort(GenerationSettings? settings) => settings?.ShortFirstSentence ?? DefaultShortFirstSentence;

    /// <summary>Whether adult content is on with <paramref name="settings"/> (unset is Off).</summary>
    public static bool Adult(GenerationSettings? settings) => settings?.AdultContent ?? false;

    /// <summary>The settings a request sends: <paramref name="settings"/> with Thinking steps resolved (unset is Off).</summary>
    public static GenerationSettings WithReasoning(GenerationSettings? settings) =>
        (settings ?? new()) with { Reasoning = ThinkingSteps(settings) };

    /// <summary>Null when nothing is set, so untouched settings keep their original saved shape.</summary>
    public static GenerationSettings? Normalize(GenerationSettings? settings) =>
        settings is null || settings.IsDefault ? null : settings;

    /// <summary>The same settings without a Thinking steps choice, so the model or server keeps its own default (null when
    /// nothing else is set): what a reply is asked again with when the choice was refused.</summary>
    public static GenerationSettings? WithoutReasoning(GenerationSettings? settings) =>
        settings is { Reasoning: not null } ? Normalize(settings with { Reasoning = null }) : settings;

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
        ContractRules.Require(ReasoningEffort is null or GenerationSupport.ReasoningEffortOn or GenerationSupport.ReasoningEffortHigh,
            "The reasoning effort must be medium or high.");
        ContractRules.Require(ThinkLonger is null || !ThinkLonger.IsDefault,
            "Thinking longer is saved as absent while every value is the default.");
        ThinkLonger?.Validate();
    }

    private static bool InRange(double value, double minimum, double maximum) =>
        double.IsFinite(value) && value >= minimum && value <= maximum;
}

/// <summary>Which generation settings a Thinking route sends, so the Replies page can mark the others as unused.</summary>
public enum GenerationSetting
{
    MaxReplyTokens, Temperature, TopP, TopK, MinP, RepeatPenalty, FrequencyPenalty, PresencePenalty, ContextTokens, Reasoning
}

/// <summary>How a route is told whether to think before answering (<see cref="GenerationSettings.Reasoning"/>).</summary>
public enum ReasoningControl
{
    /// <summary>Never sent: the OpenAI route only offers models that don't reason.</summary>
    None,
    /// <summary>OpenAI's <c>reasoning_effort</c>: <c>"none"</c> or <c>"medium"</c>. Ollama's OpenAI-compatible endpoint turns
    /// <c>"none"</c> into <c>think: false</c>; OpenAI and Gemini take it on models that reason.</summary>
    ReasoningEffort,
    /// <summary>OpenRouter's <c>reasoning</c> object: <c>{"effort":"none"}</c> or <c>{"enabled":true}</c>.</summary>
    OpenRouter,
    /// <summary><c>chat_template_kwargs</c> <c>enable_thinking</c> and <c>thinking</c> (Qwen 3, Gemma 4, DeepSeek and others
    /// on vLLM, SGLang, llama.cpp and NVIDIA Build); a template without either ignores them.</summary>
    ChatTemplate,
    /// <summary>A paired host's Ollama: its native <c>think</c>, carried by the gateway.</summary>
    OllamaThink
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
        if (setting == GenerationSetting.Reasoning)
            return Reasoning(routeType, chatBaseUrl) switch
            {
                ReasoningControl.None => GenerationSettingUse.Unused,
                ReasoningControl.OllamaThink or ReasoningControl.OpenRouter => GenerationSettingUse.Used,
                _ when string.Equals(chatBaseUrl, LocalOllamaChatBaseUrl, StringComparison.Ordinal) => GenerationSettingUse.Used,
                // Whether the model reasons and which control its server reads depend on the model and server.
                _ => GenerationSettingUse.ServerDependent
            };
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

    public const string GeminiChatHost = "generativelanguage.googleapis.com";
    /// <summary>Ollama's own port: its OpenAI-compatible endpoint takes <c>reasoning_effort</c>, not chat template arguments.</summary>
    public const int OllamaPort = 11434;
    /// <summary>The <c>reasoning_effort</c> that turns thinking off, the one that asks for it, and the one a background think
    /// asks for when it should think hard (Companion › Replies › Thinking longer).</summary>
    public const string ReasoningEffortOff = "none", ReasoningEffortOn = "medium", ReasoningEffortHigh = "high";

    /// <summary>How a route says whether to think first (Companion › Replies › Thinking steps).</summary>
    public static ReasoningControl Reasoning(SetupRouteType? routeType, string? chatBaseUrl) => routeType switch
    {
        SetupRouteType.GatewayOllama => ReasoningControl.OllamaThink,
        SetupRouteType.ChatCompletions => ChatReasoning(chatBaseUrl),
        _ => ReasoningControl.None
    };

    /// <summary>How a Chat Completions server is told whether to think first: OpenRouter's reasoning object; OpenAI's
    /// reasoning_effort for OpenAI, Gemini and Ollama (which read it, and may reject unknown arguments); otherwise the chat
    /// template arguments that vLLM, SGLang, llama.cpp and NVIDIA Build pass to the model's template.</summary>
    public static ReasoningControl ChatReasoning(string? chatBaseUrl)
    {
        if (string.Equals(chatBaseUrl, ChatCompletionsEndpointCatalog.OpenRouterBaseUrl, StringComparison.Ordinal))
            return ReasoningControl.OpenRouter;
        return Uri.TryCreate(chatBaseUrl, UriKind.Absolute, out var uri) &&
            (string.Equals(uri.Host, OpenAiChatHost, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(uri.Host, GeminiChatHost, StringComparison.OrdinalIgnoreCase) || uri.Port == OllamaPort)
            ? ReasoningControl.ReasoningEffort : ReasoningControl.ChatTemplate;
    }

    /// <summary>Writes the request properties that carry <paramref name="reasoning"/> for <paramref name="control"/> into the
    /// open request object; <see cref="ReasoningControl.None"/> writes nothing. <paramref name="effort"/> ("medium" or "high",
    /// a background think's) goes where the route takes an effort; the others only turn thinking on.</summary>
    public static void WriteReasoning(Utf8JsonWriter writer, ReasoningControl control, bool reasoning, string? effort = null)
    {
        ArgumentNullException.ThrowIfNull(writer);
        switch (control)
        {
            case ReasoningControl.ReasoningEffort:
                writer.WriteString("reasoning_effort", reasoning ? effort ?? ReasoningEffortOn : ReasoningEffortOff);
                break;
            case ReasoningControl.OpenRouter:
                writer.WriteStartObject("reasoning");
                if (reasoning && effort is not null) writer.WriteString("effort", effort);
                else if (reasoning) writer.WriteBoolean("enabled", true);
                else writer.WriteString("effort", ReasoningEffortOff);
                writer.WriteEndObject();
                break;
            case ReasoningControl.ChatTemplate:
                writer.WriteStartObject("chat_template_kwargs");
                writer.WriteBoolean("enable_thinking", reasoning);
                writer.WriteBoolean("thinking", reasoning);
                writer.WriteEndObject();
                break;
            case ReasoningControl.OllamaThink:
                writer.WriteBoolean("think", reasoning);
                break;
        }
    }

    /// <summary>What a route's requests carry for a Thinking steps choice (and a background think's effort), as JSON (<c>{}</c>
    /// for none), for MCP.</summary>
    public static string ReasoningJson(SetupRouteType? routeType, string? chatBaseUrl, bool? reasoning, string? effort = null)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            if (reasoning is { } value) WriteReasoning(writer, Reasoning(routeType, chatBaseUrl), value, effort);
            writer.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>Whether a streamed Chat Completions request asks for the closing usage chunk (stream_options.include_usage),
    /// which says how much of the input came from the prompt cache: only Ollama on this PC, which reports usage only when
    /// asked. OpenRouter reports it unasked; other servers aren't sent an option they might reject.</summary>
    public static bool AsksStreamUsage(string? chatBaseUrl) =>
        string.Equals(chatBaseUrl?.TrimEnd('/'), LocalOllamaChatBaseUrl, StringComparison.Ordinal);
}
