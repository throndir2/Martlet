using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Martlet.Core.Contracts;

namespace Martlet.Providers.Ollama;

internal sealed partial class OllamaChatResponseParser(OllamaChatAction action)
{
    private int characters;
    private bool nonWhitespace;
    private static readonly HashSet<string> NormalFields = new(StringComparer.Ordinal)
    {
        "model", "created_at", "message", "done", "done_reason",
        "prompt_eval_count", "prompt_eval_cached_count", "eval_count",
        "total_duration", "load_duration", "prompt_eval_duration", "eval_duration"
    };

    internal OllamaChatStep Accept(ReadOnlyMemory<byte> bytes)
    {
        var record = ContractJson.Read<Record>(bytes, action.Limits.MaxEventBytes);
        var fields = record.Fields;
        if (fields.TryGetValue("error", out var error))
        {
            Require(fields.Count == 1);
            _ = Text(error, 16_384);
            return OllamaChatFailures.Fail(ProviderFailureCode.Server);
        }
        if (fields.Keys.Any(key => !NormalFields.Contains(key)))
            throw new OllamaChatProtocolException(ProviderFailureCode.UnsupportedOutput);
        Require(Text(Get(fields, "model"), 256) == action.Selection.RequestModel);
        var timestamp = Text(Get(fields, "created_at"), 64);
        Require(TimestampPattern().IsMatch(timestamp) &&
            DateTimeOffset.TryParse(timestamp, CultureInfo.InvariantCulture, DateTimeStyles.None, out _));
        var message = Get(fields, "message");
        Require(message.ValueKind == JsonValueKind.Object);
        foreach (var property in message.EnumerateObject())
            if (property.Name is not "role" and not "content")
                throw new OllamaChatProtocolException(ProviderFailureCode.UnsupportedOutput);
        Require(message.TryGetProperty("role", out var role) && Text(role, 16) == "assistant");
        Require(message.TryGetProperty("content", out var content));
        Require(content.ValueKind == JsonValueKind.String);
        var text = content.GetString()!;
        if (text.Length > action.Limits.MaxTextCharacters)
            throw new OllamaChatProtocolException(ProviderFailureCode.ResponseTooLarge);
        ContractRules.Text(text, action.Limits.MaxTextCharacters);
        var done = Get(fields, "done");
        Require(done.ValueKind is JsonValueKind.True or JsonValueKind.False);
        var reason = fields.TryGetValue("done_reason", out var reasonValue) ? Text(reasonValue, 64) : "";
        Require(done.GetBoolean() || reason.Length == 0);

        var usage = new OllamaChatUsage(Count(fields, "prompt_eval_count"), Count(fields, "prompt_eval_cached_count"),
            Count(fields, "eval_count"), Duration(fields, "total_duration"), Duration(fields, "load_duration"),
            Duration(fields, "prompt_eval_duration"), Duration(fields, "eval_duration"));
        Require(usage.PromptEvalCachedCount is null || usage.PromptEvalCount is null ||
            usage.PromptEvalCachedCount <= usage.PromptEvalCount);
        if (text.Length > action.Limits.MaxTextCharacters - characters)
            throw new OllamaChatProtocolException(ProviderFailureCode.ResponseTooLarge);
        characters += text.Length;
        nonWhitespace |= !string.IsNullOrWhiteSpace(text);
        if (!done.GetBoolean()) return new(Text: text.Length == 0 ? null : text);
        if (reason is "load" or "unload")
            throw new OllamaChatProtocolException(ProviderFailureCode.UnsupportedOutput);
        if (reason is not "stop" and not "length")
            return OllamaChatFailures.Fail(ProviderFailureCode.Incomplete);
        if (usage.PromptEvalCount > action.Limits.MaxInputTokens)
            return OllamaChatFailures.Fail(ProviderFailureCode.InputLimit);
        if (usage.EvalCount > action.Limits.MaxOutputTokens)
            return OllamaChatFailures.Fail(ProviderFailureCode.OutputTokenLimit);
        if (reason == "stop") Require(nonWhitespace);
        return new(Text: text.Length == 0 ? null : text,
            Outcome: reason == "stop" ? TextGenerationOutcome.Completed : TextGenerationOutcome.OutputTokenLimit,
            Failure: reason == "length" ? ProviderFailureCode.OutputTokenLimit : null, Usage: usage);
    }

    private static JsonElement Get(Dictionary<string, JsonElement> fields, string name)
    {
        Require(fields.TryGetValue(name, out var value));
        return value;
    }

    private static string Text(JsonElement value, int maximum)
    {
        Require(value.ValueKind == JsonValueKind.String);
        var text = value.GetString()!;
        ContractRules.Text(text, maximum);
        return text;
    }

    private static long? Count(Dictionary<string, JsonElement> fields, string name) => Number(fields, name, 1_000_000_000);
    private static long? Duration(Dictionary<string, JsonElement> fields, string name) => Number(fields, name, 3_600_000_000_000);

    private static long? Number(Dictionary<string, JsonElement> fields, string name, long maximum)
    {
        if (!fields.TryGetValue(name, out var value)) return null;
        Require(value.ValueKind == JsonValueKind.Number);
        var raw = value.GetRawText();
        Require(raw.Length > 0 && raw.All(char.IsAsciiDigit));
        Require(value.TryGetInt64(out var number) && number >= 0 && number <= maximum);
        return number;
    }

    private static void Require(bool condition)
    {
        if (!condition) throw new OllamaChatProtocolException(ProviderFailureCode.ResponseSchema);
    }

    [GeneratedRegex(@"\A[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(?:\.[0-9]{1,9})?(?:Z|[+-][0-9]{2}:[0-9]{2})\z", RegexOptions.CultureInvariant)]
    private static partial Regex TimestampPattern();

    internal sealed class Record : IContract
    {
        [JsonExtensionData] public Dictionary<string, JsonElement> Fields { get; init; } = new(StringComparer.Ordinal);
        public void Validate() => Require(Fields.Count is > 0 and <= 20);
    }
}
