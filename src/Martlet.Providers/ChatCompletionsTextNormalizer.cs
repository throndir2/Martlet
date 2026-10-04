using System.Text;
using System.Text.Json;
using Martlet.Core.Contracts;

namespace Martlet.Providers;

// Tool calls are accepted only when the request offered tools. Reported output tokens are held to the reply budget only when
// the request sent one (max_tokens). After tool rounds (emptyAllowed) the model may finish with nothing more to say: it said
// what it had to before its calls (such as "let me think about that" before think_longer).
internal sealed class ChatCompletionsTextNormalizer(TextGenerationLimits limits, bool toolsOffered = false, bool budgetSent = true,
    bool emptyAllowed = false)
{
    private sealed class CallBuilder
    {
        public string? Id, Name;
        public readonly StringBuilder Arguments = new();
    }

    private string? finish;
    private int textCharacters;
    private bool hasText;
    private readonly StringBuilder refusal = new();
    private readonly List<CallBuilder> calls = [];
    private TextGenerationUsage usage = TextGenerationUsage.Unknown;
    private static readonly JsonElement EmptyObject = JsonDocument.Parse("{}").RootElement.Clone();
    public bool HasContentDelta { get; private set; }
    public bool HasReasoningDelta { get; private set; }
    public bool MadeProgress { get; private set; }

    /// <summary>The provider's own explanation from an in-stream error, for local diagnostics only.</summary>
    public string? ProviderDetail { get; private set; }

    public TextStreamStep? Accept(ResponsesSseEvent value)
    {
        MadeProgress = false;
        Require(value.EventType is null or "message");
        if (value.Data.Span.SequenceEqual("[DONE]"u8))
        {
            if (finish is null) throw new ResponseProtocolException(ProviderFailureCode.ResponseTruncated);
            // Some servers (older Ollama, some proxies) finish tool calls with "stop".
            if (calls.Count > 0 && finish is "tool_calls" or "function_call" or "stop" && refusal.Length == 0)
                return new(Outcome: TextGenerationOutcome.Completed, Usage: usage, ToolCalls: BuildCalls());
            return finish switch
            {
                "stop" when refusal.Length > 0 => new(Outcome: TextGenerationOutcome.Refused, Usage: usage, Refusal: refusal.ToString()),
                "stop" when hasText || emptyAllowed && refusal.Length == 0 => new(Outcome: TextGenerationOutcome.Completed, Usage: usage),
                "length" => Fail(ProviderFailureCode.OutputTokenLimit, TextGenerationOutcome.OutputTokenLimit),
                "content_filter" => Fail(ProviderFailureCode.ContentFiltered, TextGenerationOutcome.Incomplete),
                _ => throw new ResponseProtocolException(ProviderFailureCode.ResponseSchema)
            };
        }
        var root = ContractJson.Read<ChatChunk>(value.Data, limits.MaxEventBytes);
        if (root.Error.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null))
        {
            ProviderDetail = ProviderDiagnostics.Describe(value.Data);
            throw new ResponseProtocolException(ProviderFailureCode.RequestRejected);
        }
        // Routers and proxies (OpenRouter and the providers behind it) vary ids, model names and object labels between chunks,
        // and some send usage more than once; none of that changes the answer, so the latest usage is kept.
        Require(root.Object is null or "chat.completion.chunk" or "chat.completion");
        var counts = root.Usage;
        bool accountingFrame = counts.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null);
        if (accountingFrame)
        {
            Require(counts.ValueKind == JsonValueKind.Object);
            MadeProgress = true;
            usage = new(Count(counts, "prompt_tokens"), Count(counts, "completion_tokens"), Count(counts, "total_tokens"),
                CachedPromptTokens(counts));
            if (budgetSent && usage.OutputTokens > limits.MaxOutputTokens)
                throw new ResponseProtocolException(ProviderFailureCode.OutputTokenLimit);
        }
        var choices = root.Choices;
        if (choices.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return null;
        Require(choices.ValueKind == JsonValueKind.Array);
        if (choices.GetArrayLength() == 0) return null;
        Require(choices.GetArrayLength() == 1);
        var choice = choices[0];
        Require(choice.ValueKind == JsonValueKind.Object && (!choice.TryGetProperty("index", out var index) ||
            index.ValueKind == JsonValueKind.Number && index.TryGetInt32(out var number) && number == 0));
        if (!choice.TryGetProperty("delta", out var delta) || delta.ValueKind == JsonValueKind.Null) delta = EmptyObject;
        Require(delta.ValueKind == JsonValueKind.Object);
        if (finish is not null)
        {
            // After the finish reason only accounting may follow (OpenRouter repeats the finish reason on its usage chunk).
            Require(string.IsNullOrEmpty(OptionalString(delta, "content")) &&
                string.IsNullOrEmpty(OptionalString(delta, "refusal")) &&
                (!delta.TryGetProperty("tool_calls", out var late) || IsEmpty(late)));
            return null;
        }
        foreach (var property in delta.EnumerateObject())
        {
            if (property.Name is "content" or "refusal" or "role") continue;
            if (property.Name == "tool_calls")
            {
                if (!IsEmpty(property.Value))
                {
                    if (!toolsOffered) throw new ResponseProtocolException(ProviderFailureCode.UnsupportedOutput);
                    AcceptToolCalls(property.Value);
                }
                continue;
            }
            // Reasoning/thinking traces (NVIDIA NIM reasoning_content, OpenRouter reasoning/reasoning_details)
            // are never spoken or shown; they only prove the model is still working.
            if (property.Name is "reasoning" or "reasoning_content" or "reasoning_details")
            {
                if (!IsEmpty(property.Value))
                {
                    HasReasoningDelta = true;
                    MadeProgress = true;
                }
            }
            // Anything else a server adds (annotations, token ids, provider metadata) is never spoken and is ignored.
        }
        var text = OptionalString(delta, "content");
        var denied = OptionalString(delta, "refusal");
        if (!string.IsNullOrEmpty(text))
        {
            Require(refusal.Length == 0 && string.IsNullOrEmpty(denied));
            AddText(text);
            hasText |= !string.IsNullOrWhiteSpace(text);
        }
        if (!string.IsNullOrEmpty(denied))
        {
            Require(textCharacters == refusal.Length);
            AddText(denied);
            refusal.Append(denied);
        }
        finish = OptionalString(choice, "finish_reason");
        MadeProgress |= finish is not null;
        if (finish is not null && finish is not ("stop" or "length" or "content_filter") &&
            !(toolsOffered && finish is "tool_calls" or "function_call"))
            throw new ResponseProtocolException(ProviderFailureCode.UnsupportedOutput);
        return string.IsNullOrEmpty(text) ? null : new(Text: text);
    }

    // Streamed calls arrive in pieces keyed by index (OpenAI, vLLM, LM Studio) or whole per chunk without an index (some
    // local servers); ids and names come once, arguments accumulate.
    private void AcceptToolCalls(JsonElement value)
    {
        Require(value.ValueKind == JsonValueKind.Array);
        foreach (var item in value.EnumerateArray())
        {
            Require(item.ValueKind == JsonValueKind.Object);
            var callId = OptionalString(item, "id");
            if (item.TryGetProperty("type", out var type) && type.ValueKind != JsonValueKind.Null)
                Require(type.ValueKind == JsonValueKind.String && type.GetString() is "function" or "");
            CallBuilder call;
            if (item.TryGetProperty("index", out var index) && index.ValueKind != JsonValueKind.Null)
            {
                Require(index.ValueKind == JsonValueKind.Number && index.TryGetInt32(out _));
                var position = index.GetInt32();
                Require(position >= 0 && position <= calls.Count);
                if (position == calls.Count) calls.Add(new());
                call = calls[position];
            }
            else if (calls.Count == 0 || callId is not null && calls[^1].Id is not null && calls[^1].Id != callId)
            {
                calls.Add(new());
                call = calls[^1];
            }
            else call = calls[^1];
            if (calls.Count > TextToolRound.MaxCalls) throw new ResponseProtocolException(ProviderFailureCode.ResponseTooLarge);
            if (!string.IsNullOrEmpty(callId))
            {
                Require(call.Id is null || call.Id == callId);
                call.Id = callId;
            }
            if (item.TryGetProperty("function", out var function) && function.ValueKind != JsonValueKind.Null)
            {
                Require(function.ValueKind == JsonValueKind.Object);
                if (OptionalString(function, "name") is { Length: > 0 } name && call.Name != name)
                    call.Name = call.Name is null ? name : call.Name + name;
                if (function.TryGetProperty("arguments", out var arguments) && arguments.ValueKind != JsonValueKind.Null)
                {
                    var piece = arguments.ValueKind == JsonValueKind.String ? arguments.GetString()! :
                        arguments.ValueKind == JsonValueKind.Object ? arguments.GetRawText() :
                        throw new ResponseProtocolException(ProviderFailureCode.ResponseSchema);
                    if (piece.Length > ContractRules.MaxTextCharacters - call.Arguments.Length)
                        throw new ResponseProtocolException(ProviderFailureCode.ResponseTooLarge);
                    call.Arguments.Append(piece);
                }
            }
            HasContentDelta = true;
            MadeProgress = true;
        }
    }

    private IReadOnlyList<TextToolCall> BuildCalls()
    {
        var result = new List<TextToolCall>();
        for (var i = 0; i < calls.Count; i++)
        {
            var call = calls[i];
            Require(!string.IsNullOrWhiteSpace(call.Name));
            var callId = string.IsNullOrEmpty(call.Id) ? $"call_{i + 1}" : call.Id;
            Require(result.All(c => c.CallId != callId));
            try { result.Add(new(callId, call.Name!, call.Arguments.ToString())); }
            catch (ContractException) { throw new ResponseProtocolException(ProviderFailureCode.ResponseSchema); }
        }
        return result;
    }

    private void AddText(string text)
    {
        if (text.Length > limits.MaxTextCharacters - textCharacters)
            throw new ResponseProtocolException(ProviderFailureCode.ResponseTooLarge);
        ContractRules.Text(text, limits.MaxTextCharacters);
        textCharacters += text.Length;
        HasContentDelta = true;
        MadeProgress = true;
    }

    private TextStreamStep Fail(ProviderFailureCode code, TextGenerationOutcome outcome) =>
        new(Outcome: outcome, Usage: usage, Failure: new(code, stage: Stage.Generation));

    private static bool IsEmpty(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null => true,
        JsonValueKind.String => value.GetString()!.Length == 0,
        JsonValueKind.Array => value.GetArrayLength() == 0,
        _ => false
    };

    private static string? OptionalString(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        Require(value.ValueKind == JsonValueKind.String);
        return value.GetString();
    }

    private static long? Count(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        Require(value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var count) && count >= 0);
        return value.GetInt64();
    }

    // Prompt-cache hits: prompt_tokens_details.cached_tokens (OpenAI, OpenRouter, Ollama, vLLM...) or DeepSeek's
    // prompt_cache_hit_tokens. Accounting only, so an odd shape is ignored rather than failing the reply.
    private static long? CachedPromptTokens(JsonElement counts)
    {
        long? cached = null;
        if (counts.TryGetProperty("prompt_tokens_details", out var details) && details.ValueKind == JsonValueKind.Object &&
            details.TryGetProperty("cached_tokens", out var hits) && hits.ValueKind == JsonValueKind.Number && hits.TryGetInt64(out var n) && n >= 0)
            cached = n;
        else if (counts.TryGetProperty("prompt_cache_hit_tokens", out var deepSeek) && deepSeek.ValueKind == JsonValueKind.Number &&
            deepSeek.TryGetInt64(out var m) && m >= 0)
            cached = m;
        return cached;
    }

    private static void Require(bool condition)
    {
        if (!condition) throw new ResponseProtocolException(ProviderFailureCode.ResponseSchema);
    }

    private sealed record ChatChunk : IContract
    {
        public string? Id { get; init; }
        public string? Object { get; init; }
        public string? Model { get; init; }
        public JsonElement Choices { get; init; }
        public JsonElement Usage { get; init; }
        public JsonElement Error { get; init; }
        public void Validate() { }
    }
}
