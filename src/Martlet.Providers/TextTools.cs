using System.Text;
using System.Text.Json;
using Martlet.Core.Contracts;

namespace Martlet.Providers;

/// <summary>A function the model may call (for example an MCP tool on this PC). <see cref="ParametersJson"/> is a JSON
/// Schema object; it is sent as is, never strict-mode rewritten.</summary>
public sealed class TextToolDefinition
{
    public const int MaxNameLength = 64;
    public const int MaxDescriptionCharacters = 1024;
    public const int MaxSchemaDepth = 24;
    public const int MaxSchemaBytes = 32_768;

    public TextToolDefinition(string name, string description, string parametersJson)
    {
        ContractRules.Require(IsValidName(name), "A tool name must be 1-64 ASCII letters, digits, underscores or hyphens.");
        ContractRules.Text(description, MaxDescriptionCharacters);
        ContractRules.Require(parametersJson is not null && Encoding.UTF8.GetByteCount(parametersJson) <= MaxSchemaBytes,
            "A tool's parameter schema is too large.");
        try
        {
            using var schema = JsonDocument.Parse(parametersJson!, new JsonDocumentOptions { MaxDepth = MaxSchemaDepth });
            ContractRules.Require(schema.RootElement.ValueKind == JsonValueKind.Object, "A tool's parameters must be a JSON Schema object.");
            ParametersJson = schema.RootElement.GetRawText();
        }
        catch (JsonException)
        {
            throw new ContractException(ErrorCode.InvalidContract, "A tool's parameter schema is not valid JSON or is nested too deeply.");
        }
        Name = name;
        Description = description;
        Utf8Bytes = Encoding.UTF8.GetByteCount(name) + Encoding.UTF8.GetByteCount(description) + Encoding.UTF8.GetByteCount(ParametersJson);
    }

    public string Name { get; }
    public string Description { get; }
    public string ParametersJson { get; }
    public int Utf8Bytes { get; }

    public static bool IsValidName(string? name) =>
        name is { Length: > 0 and <= MaxNameLength } && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');

    public override string ToString() => $"{nameof(TextToolDefinition)} {Name}";
}

/// <summary>One function call the model asked for. Arguments are the model's raw JSON text (possibly invalid JSON).</summary>
public sealed class TextToolCall
{
    public const int MaxCallIdLength = 256;
    public const int MaxNameLength = 128;

    public TextToolCall(string callId, string name, string argumentsJson)
    {
        ContractRules.Require(callId is { Length: > 0 and <= MaxCallIdLength } && !callId.Any(char.IsControl),
            "A tool call needs a short identifier.");
        ContractRules.Require(name is { Length: > 0 and <= MaxNameLength } && !name.Any(char.IsControl), "A tool call needs a tool name.");
        ContractRules.Text(argumentsJson, ContractRules.MaxTextCharacters);
        CallId = callId;
        Name = name;
        ArgumentsJson = argumentsJson.Trim().Length == 0 ? "{}" : argumentsJson;
    }

    public string CallId { get; }
    public string Name { get; }
    [System.Text.Json.Serialization.JsonIgnore]
    public string ArgumentsJson { get; }
    public int Utf8Bytes => Encoding.UTF8.GetByteCount(CallId) + Encoding.UTF8.GetByteCount(Name) + Encoding.UTF8.GetByteCount(ArgumentsJson);
    public override string ToString() => $"{nameof(TextToolCall)} {Name}";
}

/// <summary>What one tool call returned, as text for the model.</summary>
public sealed class TextToolResult
{
    public const int MaxOutputCharacters = 12_000;

    public TextToolResult(string callId, string output)
    {
        ContractRules.Require(callId is { Length: > 0 and <= TextToolCall.MaxCallIdLength }, "A tool result needs its call's identifier.");
        ContractRules.Require(output is not null && output.Length <= MaxOutputCharacters, "A tool result is too long.");
        CallId = callId;
        Output = output!.Length == 0 ? "(no output)" : output;
    }

    public string CallId { get; }
    [System.Text.Json.Serialization.JsonIgnore]
    public string Output { get; }
    public int Utf8Bytes => Encoding.UTF8.GetByteCount(CallId) + Encoding.UTF8.GetByteCount(Output);

    /// <summary>Shortens <paramref name="output"/> to at most <paramref name="maximumUtf8Bytes"/> UTF-8 bytes (and the
    /// character cap), saying that it was cut, and replaces control characters a provider would reject.</summary>
    public static string Bound(string output, int maximumUtf8Bytes)
    {
        var clean = new string(output.Select(c => char.IsControl(c) && c is not '\n' and not '\r' and not '\t' ? ' ' : c).ToArray());
        const string cut = "\n[output cut short]";
        var limit = Math.Max(64, maximumUtf8Bytes);
        if (clean.Length <= MaxOutputCharacters && Encoding.UTF8.GetByteCount(clean) <= limit) return clean;
        var budget = Math.Min(MaxOutputCharacters - cut.Length, limit - cut.Length);
        var builder = new StringBuilder();
        var bytes = 0;
        foreach (var rune in clean.EnumerateRunes())
        {
            if (builder.Length + rune.Utf16SequenceLength > budget || bytes + rune.Utf8SequenceLength > budget) break;
            builder.Append(rune.ToString());
            bytes += rune.Utf8SequenceLength;
        }
        return builder.Append(cut).ToString();
    }

    public override string ToString() => nameof(TextToolResult);
}

/// <summary>One finished tool round: what the model said before calling, its calls and each call's result.</summary>
public sealed class TextToolRound
{
    public const int MaxCalls = 16;

    public TextToolRound(string text, IReadOnlyList<TextToolCall> calls, IReadOnlyList<TextToolResult> results)
    {
        ArgumentNullException.ThrowIfNull(calls);
        ArgumentNullException.ThrowIfNull(results);
        ContractRules.Text(text, ContractRules.MaxTextCharacters);
        ContractRules.Require(calls.Count is > 0 and <= MaxCalls && results.Count == calls.Count &&
            calls.Zip(results).All(pair => pair.First is not null && pair.Second is not null && pair.First.CallId == pair.Second.CallId),
            "Every tool call in a round needs exactly one result.");
        ContractRules.Require(calls.Select(c => c.CallId).Distinct(StringComparer.Ordinal).Count() == calls.Count,
            "Tool call identifiers must be unique in a round.");
        Text = text;
        Calls = calls.ToArray();
        Results = results.ToArray();
        Utf8Bytes = Encoding.UTF8.GetByteCount(text) + Calls.Sum(c => c.Utf8Bytes) + Results.Sum(r => r.Utf8Bytes);
    }

    [System.Text.Json.Serialization.JsonIgnore]
    public string Text { get; }
    public IReadOnlyList<TextToolCall> Calls { get; }
    public IReadOnlyList<TextToolResult> Results { get; }
    public int Utf8Bytes { get; }
    public override string ToString() => nameof(TextToolRound);
}
