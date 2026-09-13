using System.Text;
using System.Text.Json;
using Martlet.Core.Contracts;

namespace Martlet.Providers;

internal sealed class ResponsesTextNormalizer(TextGenerationLimits limits, string model)
{
    private string? responseId;
    private string? itemId;
    private string? partType;
    private int lastSequence = -1;
    private bool inProgress;
    private bool textDone;
    private bool partDone;
    private bool itemDone;
    private string? itemDoneStatus;
    private readonly StringBuilder text = new();
    public bool HasContentDelta { get; private set; }

    public TextStreamStep? Accept(ResponsesSseEvent value)
    {
        if (value.Data.Span.SequenceEqual("[DONE]"u8))
            throw new ResponseProtocolException(ProviderFailureCode.ResponseTruncated);
        var e = ContractJson.Read<EventEnvelope>(value.Data, limits.MaxEventBytes);
        Require(value.EventType is null || value.EventType == e.Type);
        // Responses sequence numbers are not Core sequence numbers. No reconnect/replay is attempted.
        Require(e.SequenceNumber > lastSequence);
        bool firstEvent = lastSequence == -1;
        lastSequence = e.SequenceNumber;
        if (e.Type == "error")
            return Failure(ClassifyError(e.Code));
        if (e.Type == "response.created")
        {
            Require(responseId is null && firstEvent);
            ValidateResponse(e.Response, "in_progress", initial: true);
            responseId = Id(e.Response, "id");
            return null;
        }
        Require(responseId is not null);
        switch (e.Type)
        {
            case "response.in_progress":
                Require(!inProgress && itemId is null);
                ValidateResponse(e.Response, "in_progress", initial: true);
                inProgress = true;
                return null;
            case "response.output_item.added":
                Require(itemId is null && e.OutputIndex == 0);
                RequireMessage(e.Item);
                Require(String(e.Item, "status") == "in_progress");
                Require(Array(e.Item, "content").GetArrayLength() == 0);
                itemId = Id(e.Item, "id");
                return null;
            case "response.content_part.added":
                MatchPart(e);
                Require(partType is null);
                partType = String(e.Part, "type");
                RequireSupportedPart(partType);
                Require(PartText(e.Part).Length == 0);
                return null;
            case "response.output_text.delta":
            case "response.refusal.delta":
                MatchPart(e);
                Require(partType == (e.Type == "response.refusal.delta" ? "refusal" : "output_text") && !textDone);
                Require(e.Delta is { Length: > 0 });
                if (e.Delta!.Length > limits.MaxTextCharacters - text.Length)
                    throw new ResponseProtocolException(ProviderFailureCode.ResponseTooLarge);
                ContractRules.Text(e.Delta, limits.MaxTextCharacters);
                text.Append(e.Delta);
                HasContentDelta = true;
                return partType == "output_text" ? new(Text: e.Delta) : null;
            case "response.output_text.done":
            case "response.refusal.done":
                MatchPart(e);
                Require(partType == (e.Type == "response.refusal.done" ? "refusal" : "output_text") && !textDone);
                Require((partType == "refusal" ? e.Refusal : e.Text) == text.ToString());
                textDone = true;
                return null;
            case "response.content_part.done":
                MatchPart(e);
                Require(textDone && !partDone && PartText(e.Part) == text.ToString());
                partDone = true;
                return null;
            case "response.output_item.done":
                Require(e.OutputIndex == 0 && partDone && !itemDone);
                ValidateItem(e.Item, allowIncomplete: true);
                itemDoneStatus = String(e.Item, "status");
                Require(itemDoneStatus is "completed" or "incomplete");
                itemDone = true;
                return null;
            case "response.completed":
                ValidateResponse(e.Response, "completed");
                Require(textDone && partDone && itemDone && !string.IsNullOrWhiteSpace(text.ToString()));
                return new(Outcome: partType == "refusal" ? TextGenerationOutcome.Refused : TextGenerationOutcome.Completed,
                    Usage: Usage(e.Response), Refusal: partType == "refusal" ? text.ToString() : null);
            case "response.incomplete":
                ValidateResponse(e.Response, "incomplete");
                string? reason = null;
                if (e.Response.TryGetProperty("incomplete_details", out var details) && details.ValueKind != JsonValueKind.Null)
                {
                    Require(details.ValueKind == JsonValueKind.Object);
                    if (details.TryGetProperty("reason", out var reasonValue) && reasonValue.ValueKind != JsonValueKind.Null)
                    {
                        Require(reasonValue.ValueKind == JsonValueKind.String);
                        reason = reasonValue.GetString();
                    }
                }
                return reason switch
                {
                    "max_output_tokens" => Failure(ProviderFailureCode.OutputTokenLimit, TextGenerationOutcome.OutputTokenLimit, Usage(e.Response)),
                    "content_filter" => Failure(ProviderFailureCode.ContentFiltered, TextGenerationOutcome.Incomplete, Usage(e.Response)),
                    _ => Failure(ProviderFailureCode.Incomplete, TextGenerationOutcome.Incomplete, Usage(e.Response))
                };
            case "response.failed":
                ValidateResponse(e.Response, "failed");
                var error = Object(e.Response, "error");
                return Failure(ClassifyError(String(error, "code")), usage: Usage(e.Response));
            default:
                throw new ResponseProtocolException(ProviderFailureCode.UnsupportedOutput);
        }
    }

    private void ValidateResponse(JsonElement response, string status, bool initial = false)
    {
        Require(response.ValueKind == JsonValueKind.Object);
        var id = Id(response, "id");
        Require(responseId is null || id == responseId);
        Require(String(response, "object") == "response" && String(response, "status") == status &&
            String(response, "model") == model);
        if (response.TryGetProperty("store", out var store)) Require(store.ValueKind == JsonValueKind.False);
        if (response.TryGetProperty("background", out var background)) Require(background.ValueKind == JsonValueKind.False);
        var output = Array(response, "output");
        if (initial)
        {
            Require(output.GetArrayLength() == 0);
            return;
        }
        Require(output.GetArrayLength() == (itemId is null ? 0 : 1));
        if (itemId is not null)
            ValidateItem(output[0], allowIncomplete: status != "completed");
        if (status == "completed")
        {
            Require(itemId is not null);
            Require(!response.TryGetProperty("error", out var error) || error.ValueKind == JsonValueKind.Null);
            Require(!response.TryGetProperty("incomplete_details", out var details) || details.ValueKind == JsonValueKind.Null);
        }
    }

    private void ValidateItem(JsonElement item, bool allowIncomplete)
    {
        RequireMessage(item);
        Require(Id(item, "id") == itemId);
        var status = String(item, "status");
        Require(status == "completed" || (allowIncomplete && status is "incomplete" or "in_progress"));
        Require(itemDoneStatus is null || status == itemDoneStatus);
        var content = Array(item, "content");
        Require(content.GetArrayLength() == (partType is null ? 0 : 1));
        if (partType is not null)
            Require(PartText(content[0]) == text.ToString());
    }

    private static void RequireMessage(JsonElement item)
    {
        if (String(item, "type") != "message")
            throw new ResponseProtocolException(ProviderFailureCode.UnsupportedOutput);
        Require(String(item, "role") == "assistant");
        if (item.TryGetProperty("phase", out var phase) && phase.ValueKind != JsonValueKind.Null &&
            (phase.ValueKind != JsonValueKind.String || phase.GetString() != "final_answer"))
            throw new ResponseProtocolException(ProviderFailureCode.UnsupportedOutput);
    }

    private string PartText(JsonElement part)
    {
        var type = String(part, "type");
        RequireSupportedPart(type);
        Require(type == partType);
        if (type == "output_text")
        {
            var annotations = Array(part, "annotations");
            if (annotations.GetArrayLength() != 0)
                throw new ResponseProtocolException(ProviderFailureCode.UnsupportedOutput);
        }
        var value = String(part, type == "refusal" ? "refusal" : "text");
        ContractRules.Text(value, limits.MaxTextCharacters);
        return value;
    }

    private static void RequireSupportedPart(string type)
    {
        if (type is not "output_text" and not "refusal")
            throw new ResponseProtocolException(ProviderFailureCode.UnsupportedOutput);
    }

    private void MatchPart(EventEnvelope e) =>
        Require(itemId is not null && e.ItemId == itemId && e.OutputIndex == 0 && e.ContentIndex == 0 && !itemDone);

    private TextGenerationUsage Usage(JsonElement response)
    {
        if (!response.TryGetProperty("usage", out var usage) || usage.ValueKind == JsonValueKind.Null)
            return TextGenerationUsage.Unknown;
        Require(usage.ValueKind == JsonValueKind.Object);
        long input = Count(usage, "input_tokens");
        long output = Count(usage, "output_tokens");
        long total = Count(usage, "total_tokens");
        Require(total == input + output && input <= limits.MaxInputTokens && output <= limits.MaxOutputTokens);
        if (usage.TryGetProperty("input_tokens_details", out var inputDetails))
        {
            Require(Count(inputDetails, "cached_tokens") <= input);
            if (inputDetails.TryGetProperty("cache_write_tokens", out _))
                Require(Count(inputDetails, "cache_write_tokens") <= input);
        }
        if (usage.TryGetProperty("output_tokens_details", out var outputDetails))
        {
            long reasoning = Count(outputDetails, "reasoning_tokens");
            if (reasoning != 0)
                throw new ResponseProtocolException(ProviderFailureCode.UnsupportedOutput);
        }
        return new(input, output, total);
    }

    private static long Count(JsonElement value, string name)
    {
        Require(value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var count) &&
            count.ValueKind == JsonValueKind.Number && count.TryGetInt64(out var number) && number is >= 0 and <= 1_000_000_000);
        return value.GetProperty(name).GetInt64();
    }

    private static string Id(JsonElement value, string name)
    {
        var id = String(value, name);
        Require(id.Length is > 0 and <= 128 && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-'));
        return id;
    }

    private static string String(JsonElement value, string name)
    {
        Require(value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) &&
            property.ValueKind == JsonValueKind.String);
        return value.GetProperty(name).GetString()!;
    }

    private static JsonElement Object(JsonElement value, string name)
    {
        Require(value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) &&
            property.ValueKind == JsonValueKind.Object);
        return value.GetProperty(name);
    }

    private static JsonElement Array(JsonElement value, string name)
    {
        Require(value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) &&
            property.ValueKind == JsonValueKind.Array);
        return value.GetProperty(name);
    }

    private static ProviderFailureCode ClassifyError(string? code) => code switch
    {
        "server_error" => ProviderFailureCode.Server,
        "rate_limit_exceeded" => ProviderFailureCode.RateLimited,
        "insufficient_quota" => ProviderFailureCode.QuotaExceeded,
        "model_not_found" => ProviderFailureCode.ModelNotFound,
        _ => ProviderFailureCode.RequestRejected
    };

    private static TextStreamStep Failure(ProviderFailureCode code, TextGenerationOutcome outcome = TextGenerationOutcome.Failed,
        TextGenerationUsage? usage = null) => new(Outcome: outcome, Usage: usage, Failure: new(code, stage: Stage.Generation));

    private static void Require(bool value) => ContractRules.Require(value, "Unsupported Responses event schema or order.");

    internal sealed class EventEnvelope : IContract
    {
        public required string Type { get; init; }
        public required int SequenceNumber { get; init; }
        public JsonElement Response { get; init; }
        public JsonElement Item { get; init; }
        public JsonElement Part { get; init; }
        public int? OutputIndex { get; init; }
        public int? ContentIndex { get; init; }
        public string? ItemId { get; init; }
        public string? Delta { get; init; }
        public string? Text { get; init; }
        public string? Refusal { get; init; }
        public string? Code { get; init; }
        public void Validate() => Require(Type is { Length: > 0 and <= 128 } && SequenceNumber >= 0);
    }
}
