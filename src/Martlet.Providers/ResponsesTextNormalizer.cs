using System.Text;
using System.Text.Json;
using Martlet.Core.Contracts;

namespace Martlet.Providers;

// Output items arrive in index order: assistant messages (text or refusal) and, when tools were offered, function calls.
// Function calls are accepted only when the request offered tools; events that echo the request's tool schemas can nest
// deeply, so tool requests read events with a deeper JSON limit.
internal sealed class ResponsesTextNormalizer(TextGenerationLimits limits, string model, bool toolsOffered = false)
{
    private const int MaxItems = TextToolRound.MaxCalls + 2;

    private sealed class OutputItem(string id, bool message)
    {
        public string Id { get; } = id;
        public bool IsMessage { get; } = message;
        public string? CallId, Name, PartType, DoneStatus;
        public readonly StringBuilder Arguments = new();
        public readonly StringBuilder Text = new();
        public bool TextDone, PartDone, Done;
    }

    private readonly List<OutputItem> items = [];
    private string? responseId;
    private int lastSequence = -1;
    private int characters;
    private bool inProgress;
    public bool HasContentDelta { get; private set; }
    /// <summary>The provider's own explanation from an error event, for local diagnostics only.</summary>
    public string? ProviderDetail { get; private set; }

    public TextStreamStep? Accept(ResponsesSseEvent value)
    {
        if (value.Data.Span.SequenceEqual("[DONE]"u8))
            throw new ResponseProtocolException(ProviderFailureCode.ResponseTruncated);
        var e = ContractJson.Read<EventEnvelope>(value.Data, limits.MaxEventBytes,
            toolsOffered ? ContractJson.DeepMaxDepth : ContractJson.DefaultMaxDepth);
        Require(value.EventType is null || value.EventType == e.Type);
        // Responses sequence numbers are not Core sequence numbers. No reconnect/replay is attempted.
        Require(e.SequenceNumber > lastSequence);
        bool firstEvent = lastSequence == -1;
        lastSequence = e.SequenceNumber;
        if (e.Type == "error")
        {
            ProviderDetail = ProviderDiagnostics.Describe(value.Data);
            return Failure(ClassifyError(e.Code));
        }
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
                Require(!inProgress && items.Count == 0);
                ValidateResponse(e.Response, "in_progress", initial: true);
                inProgress = true;
                return null;
            case "response.output_item.added":
                Require(e.OutputIndex == items.Count && items.Count < MaxItems);
                if (String(e.Item, "type") == "function_call")
                {
                    if (!toolsOffered) throw new ResponseProtocolException(ProviderFailureCode.UnsupportedOutput);
                    Require(OptionalString(e.Item, "status") is null or "in_progress");
                    var call = new OutputItem(Id(e.Item, "id"), message: false) { CallId = CallId(e.Item), Name = ToolName(e.Item) };
                    AppendArguments(call, OptionalString(e.Item, "arguments") ?? "");
                    items.Add(call);
                }
                else
                {
                    RequireMessage(e.Item);
                    Require(String(e.Item, "status") == "in_progress");
                    Require(Array(e.Item, "content").GetArrayLength() == 0);
                    items.Add(new(Id(e.Item, "id"), message: true));
                }
                Require(items.Select(i => i.Id).Distinct(StringComparer.Ordinal).Count() == items.Count);
                return null;
            case "response.content_part.added":
            {
                var item = Message(e);
                Require(item.PartType is null);
                item.PartType = String(e.Part, "type");
                RequireSupportedPart(item.PartType);
                Require(PartText(e.Part, item).Length == 0);
                return null;
            }
            case "response.output_text.delta":
            case "response.refusal.delta":
            {
                var item = Message(e);
                Require(item.PartType == (e.Type == "response.refusal.delta" ? "refusal" : "output_text") && !item.TextDone);
                Require(e.Delta is { Length: > 0 });
                if (e.Delta!.Length > limits.MaxTextCharacters - characters)
                    throw new ResponseProtocolException(ProviderFailureCode.ResponseTooLarge);
                ContractRules.Text(e.Delta, limits.MaxTextCharacters);
                item.Text.Append(e.Delta);
                characters += e.Delta.Length;
                HasContentDelta = true;
                return item.PartType == "output_text" ? new(Text: e.Delta) : null;
            }
            case "response.output_text.done":
            case "response.refusal.done":
            {
                var item = Message(e);
                Require(item.PartType == (e.Type == "response.refusal.done" ? "refusal" : "output_text") && !item.TextDone);
                Require((item.PartType == "refusal" ? e.Refusal : e.Text) == item.Text.ToString());
                item.TextDone = true;
                return null;
            }
            case "response.content_part.done":
            {
                var item = Message(e);
                Require(item.TextDone && !item.PartDone && PartText(e.Part, item) == item.Text.ToString());
                item.PartDone = true;
                return null;
            }
            case "response.function_call_arguments.delta":
            {
                var call = Call(e);
                Require(e.Delta is not null);
                AppendArguments(call, e.Delta!);
                HasContentDelta = true;
                return null;
            }
            case "response.function_call_arguments.done":
            {
                var call = Call(e);
                Require(e.Arguments == call.Arguments.ToString());
                return null;
            }
            case "response.output_item.done":
            {
                Require(e.OutputIndex is { } index && index >= 0 && index < items.Count);
                var item = items[e.OutputIndex!.Value];
                Require(!item.Done && (!item.IsMessage || item.PartDone));
                ValidateItem(e.Item, item, allowIncomplete: true);
                item.DoneStatus = OptionalString(e.Item, "status") ?? "completed";
                Require(item.DoneStatus is "completed" or "incomplete");
                item.Done = true;
                return null;
            }
            case "response.completed":
            {
                ValidateResponse(e.Response, "completed");
                Require(items.Count > 0 && items.All(i => i.Done));
                var usage = Usage(e.Response);
                var refusals = items.Where(i => i.PartType == "refusal").Select(i => i.Text.ToString()).ToArray();
                if (refusals.Length > 0)
                    return new(Outcome: TextGenerationOutcome.Refused, Usage: usage, Refusal: string.Join("\n", refusals));
                var calls = items.Where(i => !i.IsMessage)
                    .Select(i => new TextToolCall(i.CallId!, i.Name!, i.Arguments.ToString())).ToArray();
                Require(calls.Length > 0 || items.Any(i => i.IsMessage && !string.IsNullOrWhiteSpace(i.Text.ToString())));
                Require(calls.Length <= TextToolRound.MaxCalls &&
                    calls.Select(c => c.CallId).Distinct(StringComparer.Ordinal).Count() == calls.Length);
                return new(Outcome: TextGenerationOutcome.Completed, Usage: usage, ToolCalls: calls.Length == 0 ? null : calls);
            }
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
                ProviderDetail = ProviderDiagnostics.Describe(e.Response);
                return Failure(ClassifyError(String(error, "code")), usage: Usage(e.Response));
            default:
                throw new ResponseProtocolException(ProviderFailureCode.UnsupportedOutput);
        }
    }

    private OutputItem Message(EventEnvelope e)
    {
        Require(e.OutputIndex is { } index && index >= 0 && index < items.Count);
        var item = items[e.OutputIndex!.Value];
        Require(item.IsMessage && e.ItemId == item.Id && e.ContentIndex == 0 && !item.Done);
        return item;
    }

    private OutputItem Call(EventEnvelope e)
    {
        Require(e.OutputIndex is { } index && index >= 0 && index < items.Count);
        var item = items[e.OutputIndex!.Value];
        Require(!item.IsMessage && e.ItemId == item.Id && !item.Done);
        return item;
    }

    private static void AppendArguments(OutputItem call, string delta)
    {
        if (delta.Length > ContractRules.MaxTextCharacters - call.Arguments.Length)
            throw new ResponseProtocolException(ProviderFailureCode.ResponseTooLarge);
        call.Arguments.Append(delta);
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
        Require(output.GetArrayLength() == items.Count);
        for (var i = 0; i < items.Count; i++)
            ValidateItem(output[i], items[i], allowIncomplete: status != "completed");
        if (status == "completed")
        {
            Require(items.Count > 0);
            Require(!response.TryGetProperty("error", out var error) || error.ValueKind == JsonValueKind.Null);
            Require(!response.TryGetProperty("incomplete_details", out var details) || details.ValueKind == JsonValueKind.Null);
        }
    }

    private void ValidateItem(JsonElement element, OutputItem item, bool allowIncomplete)
    {
        if (item.IsMessage)
        {
            RequireMessage(element);
            Require(Id(element, "id") == item.Id);
        }
        else
        {
            Require(String(element, "type") == "function_call" && Id(element, "id") == item.Id &&
                CallId(element) == item.CallId && ToolName(element) == item.Name);
        }
        var status = item.IsMessage ? String(element, "status") : OptionalString(element, "status") ?? "completed";
        Require(status == "completed" || (allowIncomplete && status is "incomplete" or "in_progress"));
        Require(item.DoneStatus is null || status == item.DoneStatus);
        if (!item.IsMessage)
        {
            if (status == "completed") Require(String(element, "arguments") == item.Arguments.ToString());
            return;
        }
        var content = Array(element, "content");
        Require(content.GetArrayLength() == (item.PartType is null ? 0 : 1));
        if (item.PartType is not null)
            Require(PartText(content[0], item) == item.Text.ToString());
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

    private string PartText(JsonElement part, OutputItem item)
    {
        var type = String(part, "type");
        RequireSupportedPart(type);
        Require(type == item.PartType);
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

    private static string CallId(JsonElement value)
    {
        var id = String(value, "call_id");
        Require(id.Length is > 0 and <= TextToolCall.MaxCallIdLength && !id.Any(char.IsControl));
        return id;
    }

    private static string ToolName(JsonElement value)
    {
        var name = String(value, "name");
        Require(name.Length is > 0 and <= TextToolCall.MaxNameLength && !name.Any(char.IsControl));
        return name;
    }

    private static string String(JsonElement value, string name)
    {
        Require(value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) &&
            property.ValueKind == JsonValueKind.String);
        return value.GetProperty(name).GetString()!;
    }

    private static string? OptionalString(JsonElement value, string name)
    {
        Require(value.ValueKind == JsonValueKind.Object);
        if (!value.TryGetProperty(name, out var property) || property.ValueKind == JsonValueKind.Null) return null;
        Require(property.ValueKind == JsonValueKind.String);
        return property.GetString();
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
        public string? Arguments { get; init; }
        public string? Code { get; init; }
        public void Validate() => Require(Type is { Length: > 0 and <= 128 } && SequenceNumber >= 0);
    }
}
