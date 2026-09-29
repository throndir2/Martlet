using System.Text;
using System.Text.Json;
using Martlet.Core.Contracts;

namespace Martlet.Providers;

internal sealed class ChatCompletionsTextNormalizer(TextGenerationLimits limits)
{
    private string? id;
    private string? model;
    private string? finish;
    private int textCharacters;
    private bool hasText;
    private readonly StringBuilder refusal = new();
    private TextGenerationUsage usage = TextGenerationUsage.Unknown;
    private bool usageSeen;
    public bool HasContentDelta { get; private set; }
    public bool HasReasoningDelta { get; private set; }
    public bool MadeProgress { get; private set; }

    public TextStreamStep? Accept(ResponsesSseEvent value)
    {
        MadeProgress = false;
        Require(value.EventType is null or "message");
        if (value.Data.Span.SequenceEqual("[DONE]"u8))
        {
            if (finish is null) throw new ResponseProtocolException(ProviderFailureCode.ResponseTruncated);
            return finish switch
            {
                "stop" when refusal.Length > 0 => new(Outcome: TextGenerationOutcome.Refused, Usage: usage, Refusal: refusal.ToString()),
                "stop" when hasText => new(Outcome: TextGenerationOutcome.Completed, Usage: usage),
                "length" => Fail(ProviderFailureCode.OutputTokenLimit, TextGenerationOutcome.OutputTokenLimit),
                "content_filter" => Fail(ProviderFailureCode.ContentFiltered, TextGenerationOutcome.Incomplete),
                _ => throw new ResponseProtocolException(ProviderFailureCode.ResponseSchema)
            };
        }
        var root = ContractJson.Read<ChatChunk>(value.Data, limits.MaxEventBytes);
        if (root.Error.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null))
            throw new ResponseProtocolException(ProviderFailureCode.RequestRejected);
        Require(root.Object == "chat.completion.chunk");
        var nextId = root.Id;
        var nextModel = root.Model;
        Require(nextId is { Length: > 0 and <= 256 } && nextModel is { Length: > 0 and <= 256 } &&
            (id is null || id == nextId) && (model is null || model == nextModel));
        id = nextId;
        model = nextModel;
        var counts = root.Usage;
        bool accountingFrame = counts.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null);
        if (accountingFrame)
        {
            Require(!usageSeen && counts.ValueKind == JsonValueKind.Object);
            usageSeen = true;
            MadeProgress = true;
            usage = new(Count(counts, "prompt_tokens"), Count(counts, "completion_tokens"), Count(counts, "total_tokens"));
            if (usage.InputTokens is { } input && usage.OutputTokens is { } output && usage.TotalTokens is { } total)
                Require(input <= long.MaxValue - output && input + output == total);
            if (usage.OutputTokens > limits.MaxOutputTokens)
                throw new ResponseProtocolException(ProviderFailureCode.OutputTokenLimit);
        }
        var choices = root.Choices;
        Require(choices.ValueKind == JsonValueKind.Array);
        if (choices.GetArrayLength() == 0)
        {
            Require(finish is not null && usageSeen);
            return null;
        }
        Require(choices.GetArrayLength() == 1);
        var choice = choices[0];
        Require(choice.ValueKind == JsonValueKind.Object && choice.TryGetProperty("index", out var index) &&
            index.ValueKind == JsonValueKind.Number && index.TryGetInt32(out var number) && number == 0);
        Require(choice.TryGetProperty("delta", out var delta) && delta.ValueKind == JsonValueKind.Object);
        if (finish is not null)
        {
            Require(accountingFrame && delta.EnumerateObject().All(property =>
                property.Name is "role" or "content" or "refusal" || IsEmpty(property.Value)) &&
                AssistantRole(delta) &&
                string.IsNullOrEmpty(OptionalString(delta, "content")) &&
                string.IsNullOrEmpty(OptionalString(delta, "refusal")) &&
                OptionalString(choice, "finish_reason") == finish);
            return null;
        }
        foreach (var property in delta.EnumerateObject())
        {
            if (property.Name is "content" or "refusal" or "role") continue;
            // Reasoning/thinking traces (NVIDIA NIM reasoning_content, OpenRouter reasoning/reasoning_details)
            // are never spoken or shown; they only prove the model is still working.
            if (property.Name is "reasoning" or "reasoning_content" or "reasoning_details")
            {
                if (!IsEmpty(property.Value))
                {
                    HasReasoningDelta = true;
                    MadeProgress = true;
                }
                continue;
            }
            if (!IsEmpty(property.Value))
                throw new ResponseProtocolException(ProviderFailureCode.UnsupportedOutput);
        }
        Require(AssistantRole(delta));
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
        if (finish is not null && finish is not ("stop" or "length" or "content_filter"))
            throw new ResponseProtocolException(ProviderFailureCode.UnsupportedOutput);
        return string.IsNullOrEmpty(text) ? null : new(Text: text);
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

    // Some servers (for example NVIDIA NIM backends) send an explicit null role on continuation chunks.
    private static bool AssistantRole(JsonElement delta) =>
        !delta.TryGetProperty("role", out var role) || role.ValueKind == JsonValueKind.Null ||
        role.ValueKind == JsonValueKind.String && role.GetString() == "assistant";

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
