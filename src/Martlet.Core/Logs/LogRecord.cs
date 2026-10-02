using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Martlet.Core.Contracts;

namespace Martlet.Core.Logs;

/// <summary>Severity of a log line, as Martlet's local logs write it.</summary>
public static class LogLevels
{
    public const string Info = "INFO";
    public const string Warn = "WARN";
    public const string Error = "ERROR";
    public const string Fatal = "FATAL";
    public static readonly IReadOnlyList<string> All = [Info, Warn, Error, Fatal];

    /// <summary>0 for INFO up to 3 for FATAL; unknown levels count as INFO.</summary>
    public static int Rank(string? level) => level switch { Warn => 1, Error => 2, Fatal => 3, _ => 0 };
}

/// <summary>The parts of Martlet that write logs: the desktop app, its avatar renderer, host runs (setup, pairing and role
/// changes shown in a run window) and a host's gateway.</summary>
public static class LogComponents
{
    public const string Desktop = "desktop";
    public const string AvatarRenderer = "avatar-renderer";
    public const string HostRuns = "host-runs";
    public const string Gateway = "gateway";
    public static readonly IReadOnlyList<string> Local = [Desktop, AvatarRenderer, HostRuns];
}

/// <summary>One line of activity from any of the owner's computers. <see cref="Source"/> is the computer (a desktop's
/// device ID or a host ID) and <see cref="Component"/> the part of Martlet that wrote it; together they form a stream in
/// which <see cref="Seq"/> only grows, so a log host keeps each line once however many times it is delivered.
/// <see cref="RelayedBy"/> is set by the log host when another device delivered the line. Never holds keys or
/// conversation content: the writers log metadata, errors and status only.</summary>
public sealed record LogRecord
{
    public const int MaximumMessageCharacters = 8_000;

    public required string Source { get; init; }
    public required string Component { get; init; }
    public required long Seq { get; init; }
    public required DateTimeOffset At { get; init; }
    public required string Level { get; init; }
    public required string Message { get; init; }
    public string? RelayedBy { get; init; }

    [JsonIgnore] public string Stream => Source + "/" + Component;

    /// <summary>The message's first line (the rest is usually a stack trace or command output).</summary>
    [JsonIgnore] public string Headline
    {
        get
        {
            var end = Message.IndexOf('\n');
            return end >= 0 ? Message[..end].TrimEnd('\r') : Message;
        }
    }

    public void Validate()
    {
        ContractRules.Identifier(Source);
        ContractRules.Require(LogRules.IsComponent(Component), "A log component name is invalid.");
        ContractRules.Require(Seq is > 0 and <= LogRules.MaximumSeq, "A log sequence number is out of range.");
        ContractRules.Require(At > DateTimeOffset.UnixEpoch && At < new DateTimeOffset(2200, 1, 1, 0, 0, 0, TimeSpan.Zero),
            "A log time is out of range.");
        ContractRules.Require(LogLevels.All.Contains(Level), "A log level is invalid.");
        ContractRules.Text(Message, MaximumMessageCharacters);
        if (RelayedBy is not null) ContractRules.Identifier(RelayedBy);
    }
}

public sealed record LogStream
{
    public required string Source { get; init; }
    public required string Component { get; init; }
}

/// <summary>The newest <see cref="Seq"/> a log host holds for one stream (0 when it holds none).</summary>
public sealed record LogMark
{
    public required string Source { get; init; }
    public required string Component { get; init; }
    public required long Seq { get; init; }
}

/// <summary>What a desktop sends its log host: lines from its own logs and lines it relays from other hosts, plus the
/// streams whose marks it wants back so the next batch starts where the log host left off.</summary>
public sealed record LogBatch
{
    public const int MaximumBytes = 393_216;
    public const int MaximumEntries = 1_000;
    public const int MaximumStreams = 64;

    public required int SchemaVersion { get; init; }
    public IReadOnlyList<LogStream> Streams { get; init; } = [];
    public IReadOnlyList<LogRecord> Entries { get; init; } = [];

    public void Validate()
    {
        ContractRules.Require(SchemaVersion == 1, "This log batch was written by a newer Martlet.", ErrorCode.UnsupportedVersion);
        ContractRules.Require(Streams is { Count: <= MaximumStreams } && Entries is { Count: <= MaximumEntries },
            "The log batch has too many streams or lines.");
        foreach (var stream in Streams)
        {
            ContractRules.Require(stream is not null, "A log stream is missing.");
            ContractRules.Identifier(stream!.Source);
            ContractRules.Require(LogRules.IsComponent(stream.Component), "A log component name is invalid.");
        }
        foreach (var entry in Entries)
        {
            ContractRules.Require(entry is not null, "A log line is missing.");
            entry!.Validate();
            ContractRules.Require(entry.RelayedBy is null, "Only the log host records who relayed a line.");
        }
    }

    public byte[] Write()
    {
        Validate();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(this, LogRules.Json);
        ContractRules.Require(bytes.Length <= MaximumBytes, "The log batch is too large.", ErrorCode.PayloadTooLarge);
        return bytes;
    }

    public static LogBatch Parse(ReadOnlySpan<byte> bytes)
    {
        ContractRules.Require(bytes.Length is > 0 and <= MaximumBytes, "The log batch is empty or too large.", ErrorCode.PayloadTooLarge);
        LogBatch? batch;
        try { batch = JsonSerializer.Deserialize<LogBatch>(bytes, LogRules.Json); }
        catch (Exception error) when (error is JsonException or NotSupportedException or InvalidOperationException)
        {
            throw new ContractException(ErrorCode.InvalidContract, "The log batch is malformed.");
        }
        ContractRules.Require(batch is not null, "The log batch is empty.");
        batch!.Validate();
        return batch;
    }
}

public static class LogRules
{
    /// <summary>Sequence numbers are Unix milliseconds times 1000 plus an ordinal, so they stay below this.</summary>
    public const long MaximumSeq = long.MaxValue / 4;

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true,
        MaxDepth = 8
    };

    public static bool IsComponent(string? text) => text is { Length: > 0 and <= 32 } && char.IsAsciiLetterLower(text[0]) &&
        text.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-');

    /// <summary>A sequence number for a line written at <paramref name="at"/>: Unix milliseconds times 1000 plus its
    /// <paramref name="ordinal"/> among lines in the same millisecond (at most 999).</summary>
    public static long Seq(DateTimeOffset at, int ordinal) =>
        Math.Max(1, at.ToUnixTimeMilliseconds()) * 1000 + Math.Clamp(ordinal, 0, 999);

    /// <summary>A sequence number later than <paramref name="last"/> and, normally, than anything written before now.</summary>
    public static long Next(long last, DateTimeOffset now) => Math.Max(last + 1, Seq(now, 0));

    /// <summary>Makes text safe to keep in a log line: control characters other than tab and newline become spaces,
    /// carriage returns are dropped and the result is cut to <see cref="LogRecord.MaximumMessageCharacters"/>.</summary>
    public static string Clean(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var builder = new StringBuilder(Math.Min(text.Length, LogRecord.MaximumMessageCharacters));
        foreach (var c in text)
        {
            if (builder.Length >= LogRecord.MaximumMessageCharacters) break;
            if (c == '\r') continue;
            builder.Append(char.IsControl(c) && c is not '\n' and not '\t' ? ' ' : c);
        }
        if (builder.Length > 0 && char.IsHighSurrogate(builder[^1])) builder.Length--;
        return builder.ToString().TrimEnd();
    }
}
