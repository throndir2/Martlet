using System.Text.Json;
using System.Text.Json.Serialization;
using Martlet.Core.Contracts;
using Martlet.Core.Logs;
using Microsoft.AspNetCore.Http;

namespace Martlet.Gateway;

/// <summary>Where a gateway keeps its log between restarts (logs.json beside host.json on Linux hosts). <see cref="Load"/>
/// returns null when there is none; either call may throw on storage failure.</summary>
public interface IGatewayLogStorage
{
    byte[]? Load();
    void Save(byte[] bytes);
}

/// <summary>This host's log: its own activity (requests it served or refused, pairings, starts and stops) and, when the
/// owner chose it as the log host, the lines paired desktops send from their own logs and relay from other hosts. Each
/// stream (computer and part) keeps its newest sequence number, so a line delivered twice is kept once. Bounded to the
/// newest <see cref="MaximumEntries"/> lines and about <see cref="MaximumStoredBytes"/>; saved at most every
/// <see cref="SaveDelay"/> and when the listener stops. Holds no secrets or conversation content.</summary>
internal sealed class GatewayLogStore
{
    internal const int MaximumEntries = 6_000;
    internal const int MaximumStoredBytes = 1_800_000;
    internal const int MaximumMarks = 512;
    internal static readonly TimeSpan SaveDelay = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan RepeatWindow = TimeSpan.FromMinutes(1);

    private static readonly JsonSerializerOptions StorageJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        MaxDepth = 8
    };

    private readonly object gate = new();
    private readonly object saveGate = new();
    private readonly string hostId;
    private readonly TimeProvider clock;
    private readonly List<Stored> entries = [];
    private readonly Dictionary<string, long> marks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (DateTimeOffset At, int Suppressed)> repeats = new(StringComparer.Ordinal);
    private long nextIndex = 1;
    private long ownSeq;
    private long storedBytes;
    private IGatewayLogStorage? storage;
    private ITimer? saveTimer;
    private bool dirty;

    internal sealed record Stored(long Index, LogRecord Record)
    {
        internal long Size { get; } = Record.Message.Length * 2L + 240;
    }

    internal GatewayLogStore(string hostId, TimeProvider clock)
    {
        this.hostId = hostId;
        this.clock = clock;
    }

    internal int Count { get { lock (gate) return entries.Count; } }

    internal void Attach(IGatewayLogStorage value)
    {
        ArgumentNullException.ThrowIfNull(value);
        Document? saved = null;
        try { if (value.Load() is { } bytes) saved = JsonSerializer.Deserialize<Document>(bytes, StorageJson); }
        // An unreadable or malformed log starts empty; it is replaced at the next save.
        catch (Exception) { }
        lock (gate)
        {
            storage = value;
            if (saved is null) return;
            // Lines recorded before the saved log was attached follow it.
            var pending = entries.ToArray();
            entries.Clear();
            storedBytes = 0;
            foreach (var item in (saved.Entries ?? []).OrderBy(e => e?.Index ?? 0))
            {
                if (item?.Record is not { } record || item.Index <= 0 || entries.Count > 0 && item.Index <= entries[^1].Index) continue;
                try { record.Validate(); }
                catch (ContractException) { continue; }
                Add(new(item.Index, record));
                Mark(record.Stream, record.Seq);
            }
            foreach (var mark in saved.Marks ?? [])
                if (mark is { Seq: > 0 and <= LogRules.MaximumSeq } && LogRules.IsComponent(mark.Component) && !string.IsNullOrEmpty(mark.Source))
                    Mark(mark.Source + "/" + mark.Component, mark.Seq);
            nextIndex = Math.Max(saved.NextIndex, entries.Count == 0 ? 1 : entries[^1].Index + 1);
            ownSeq = Math.Max(ownSeq, Math.Clamp(saved.OwnSeq, 0, LogRules.MaximumSeq));
            foreach (var item in pending) Add(new(nextIndex++, item.Record));
            Trim();
        }
    }

    /// <summary>Records this gateway's own activity. The same <paramref name="repeatKey"/> within a minute is counted, not
    /// repeated, so a device polling with a revoked credential cannot flood the log.</summary>
    internal void Own(string level, string message, string? repeatKey = null)
    {
        if (!LogLevels.All.Contains(level)) level = LogLevels.Info;
        var now = clock.GetUtcNow();
        lock (gate)
        {
            if (repeatKey is not null)
            {
                if (repeats.TryGetValue(repeatKey, out var seen) && now - seen.At < RepeatWindow)
                {
                    repeats[repeatKey] = (seen.At, seen.Suppressed + 1);
                    return;
                }
                if (seen.Suppressed > 0) message += $" (It also happened {seen.Suppressed} more time{(seen.Suppressed == 1 ? "" : "s")} in the minute before.)";
                repeats[repeatKey] = (now, 0);
                if (repeats.Count > 256)
                    foreach (var stale in repeats.Where(r => now - r.Value.At >= RepeatWindow).Select(r => r.Key).ToArray())
                        repeats.Remove(stale);
            }
            ownSeq = LogRules.Next(Math.Max(ownSeq, marks.GetValueOrDefault(hostId + "/" + LogComponents.Gateway)), now);
            var record = new LogRecord
            {
                Source = hostId, Component = LogComponents.Gateway, Seq = ownSeq, At = now, Level = level,
                Message = LogRules.Clean(message) is { Length: > 0 } clean ? clean : "(empty)"
            };
            Add(new(nextIndex++, record));
            Mark(record.Stream, record.Seq);
            Trim();
        }
        ScheduleSave();
    }

    /// <summary>Keeps each line of <paramref name="batch"/> newer than its stream's mark, noting who relayed lines from
    /// other computers, and returns how many were kept and the marks of every stream the batch names.</summary>
    internal (int Accepted, IReadOnlyList<LogMark> Marks) Accept(LogBatch batch, string deviceId)
    {
        var accepted = 0;
        LogMark[] result;
        lock (gate)
        {
            foreach (var entry in batch.Entries.OrderBy(e => e.Stream, StringComparer.Ordinal).ThenBy(e => e.Seq))
            {
                if (entry.Seq <= marks.GetValueOrDefault(entry.Stream)) continue;
                var record = entry with { RelayedBy = entry.Source == deviceId ? null : deviceId };
                Add(new(nextIndex++, record));
                Mark(entry.Stream, entry.Seq);
                accepted++;
            }
            if (accepted > 0) Trim();
            result = batch.Streams.Select(s => (s.Source, s.Component))
                .Concat(batch.Entries.Select(e => (e.Source, e.Component)))
                .Distinct()
                .Select(s => new LogMark { Source = s.Source, Component = s.Component, Seq = marks.GetValueOrDefault(s.Source + "/" + s.Component) })
                .ToArray();
        }
        if (accepted > 0) ScheduleSave();
        return (accepted, result);
    }

    /// <summary>Lines kept after store position <paramref name="after"/>, oldest first, up to <paramref name="limit"/> lines
    /// and roughly <paramref name="budget"/> bytes of JSON. <c>Next</c> is the position to continue from.</summary>
    internal (IReadOnlyList<LogRecord> Entries, long Next, bool More) Read(long after, int limit, int budget) =>
        Page(after, limit, budget, own: false);

    /// <summary>This gateway's own lines with a sequence number after <paramref name="afterSeq"/>, oldest first.</summary>
    internal (IReadOnlyList<LogRecord> Entries, long Next, bool More) ReadOwn(long afterSeq, int limit, int budget) =>
        Page(afterSeq, limit, budget, own: true);

    private (IReadOnlyList<LogRecord> Entries, long Next, bool More) Page(long after, int limit, int budget, bool own)
    {
        lock (gate)
        {
            var result = new List<LogRecord>();
            var used = 0L;
            var next = after;
            var more = false;
            foreach (var item in entries)
            {
                var record = item.Record;
                if (own ? record.Source != hostId || record.Component != LogComponents.Gateway || record.Seq <= after : item.Index <= after)
                    continue;
                // JSON escapes non-ASCII text as \uXXXX, so a character can take six bytes.
                var size = record.Message.Length * 6L + 320;
                if (result.Count >= limit || result.Count > 0 && used + size > budget)
                {
                    more = true;
                    break;
                }
                result.Add(record);
                used += size;
                next = own ? record.Seq : item.Index;
            }
            return (result, next, more);
        }
    }

    /// <summary>Writes the log now if it changed since the last save. Never throws.</summary>
    internal void Flush()
    {
        lock (saveGate)
        {
            byte[] bytes;
            IGatewayLogStorage target;
            lock (gate)
            {
                saveTimer?.Dispose();
                saveTimer = null;
                if (!dirty || storage is null) return;
                dirty = false;
                target = storage;
                while (true)
                {
                    bytes = JsonSerializer.SerializeToUtf8Bytes(new Document
                    {
                        SchemaVersion = 1, NextIndex = nextIndex, OwnSeq = ownSeq,
                        Marks = marks.Select(m =>
                        {
                            var split = m.Key.LastIndexOf('/');
                            return new LogMark { Source = m.Key[..split], Component = m.Key[(split + 1)..], Seq = m.Value };
                        }).ToArray(),
                        Entries = entries.Select(e => new StoredDocument { Index = e.Index, Record = e.Record }).ToArray()
                    }, StorageJson);
                    if (bytes.Length <= MaximumStoredBytes + 200_000 || entries.Count == 0) break;
                    RemoveOldest(Math.Max(1, entries.Count / 10));
                }
            }
            try { target.Save(bytes); }
            catch (Exception) { lock (gate) dirty = true; }
        }
    }

    private void ScheduleSave()
    {
        lock (gate)
        {
            if (storage is null) return;
            dirty = true;
            saveTimer ??= clock.CreateTimer(_ => Flush(), null, SaveDelay, Timeout.InfiniteTimeSpan);
        }
    }

    private void Add(Stored item)
    {
        entries.Add(item);
        storedBytes += item.Size;
    }

    private void Mark(string stream, long seq)
    {
        if (seq <= marks.GetValueOrDefault(stream)) return;
        if (!marks.ContainsKey(stream) && marks.Count >= MaximumMarks)
            marks.Remove(marks.MinBy(m => m.Value).Key);
        marks[stream] = seq;
    }

    private void Trim()
    {
        var remove = Math.Max(entries.Count - MaximumEntries, 0);
        var bytes = storedBytes - entries.Take(remove).Sum(e => e.Size);
        while (remove < entries.Count && bytes > MaximumStoredBytes)
        {
            bytes -= entries[remove].Size;
            remove++;
        }
        if (remove > 0) RemoveOldest(remove);
    }

    private void RemoveOldest(int count)
    {
        storedBytes -= entries.Take(count).Sum(e => e.Size);
        entries.RemoveRange(0, count);
    }

    private sealed record Document
    {
        public int SchemaVersion { get; init; }
        public long NextIndex { get; init; }
        public long OwnSeq { get; init; }
        public LogMark[]? Marks { get; init; }
        public StoredDocument[]? Entries { get; init; }
    }

    private sealed record StoredDocument
    {
        public long Index { get; init; }
        public LogRecord? Record { get; init; }
    }
}

internal sealed partial class GatewayHttpApplication
{
    internal const string LogsPath = "/martlet/v1/logs";
    internal const int DefaultLogPage = 500;
    internal const int MaximumLogPage = 1_000;
    private const int MaximumLogsResponseBytes = 524_288;
    private const int LogsResponseBudget = 458_752;
    private const string RouteItem = "martlet.route", DeviceItem = "martlet.device";

    internal GatewayLogStore Logs { get; }

    internal static bool IsLogsTarget(string rawTarget) =>
        rawTarget == LogsPath || rawTarget.StartsWith(LogsPath + "?", StringComparison.Ordinal);

    /// <summary>GET returns stored lines after <c>after</c> (a store position; every computer's lines) or this host's own
    /// lines after <c>own_after</c> (a sequence number), with an optional <c>limit</c>; POST stores a desktop's batch. Any
    /// paired device may do either over its signed, pinned connection.</summary>
    private async ValueTask InvokeLogsAsync(HttpContext context, string rawTarget)
    {
        if (context.Request.Method == HttpMethods.Get)
        {
            EnsureEmptyRequest(context.Request);
            _ = authenticator.Authenticate(context.Request);
            var query = LogsQuery(rawTarget);
            var limit = (int)Math.Min(query.GetValueOrDefault("limit", DefaultLogPage), MaximumLogPage);
            GatewayRules.Require(limit >= 1 && !(query.ContainsKey("after") && query.ContainsKey("own_after")), "request.invalid");
            var own = query.TryGetValue("own_after", out var ownAfter);
            var (entries, next, more) = own ? Logs.ReadOwn(ownAfter, limit, LogsResponseBudget)
                : Logs.Read(query.GetValueOrDefault("after"), limit, LogsResponseBudget);
            await WriteJsonAsync(context, StatusCodes.Status200OK, new LogsDocument
            {
                ProtocolVersion = GatewayProtocolVersion.Current, HostId = identity.HostId, Next = next, More = more,
                Entries = entries
            }, MaximumLogsResponseBytes).ConfigureAwait(false);
            return;
        }
        GatewayRules.Require(context.Request.Method == HttpMethods.Post && rawTarget == LogsPath, "request.invalid");
        var bytes = await ReadInferenceBodyAsync(context.Request, LogBatch.MaximumBytes, context.RequestAborted).ConfigureAwait(false);
        var principal = authenticator.Authenticate(context.Request, crypto.Sha256(bytes));
        LogBatch batch;
        try { batch = LogBatch.Parse(bytes); }
        catch (ContractException) { throw new GatewayProtocolException("request.invalid"); }
        var (accepted, marks) = Logs.Accept(batch, principal.DeviceId);
        await WriteJsonAsync(context, StatusCodes.Status200OK, new LogsAcceptedDocument
        {
            ProtocolVersion = GatewayProtocolVersion.Current, HostId = identity.HostId, Accepted = accepted, Marks = marks
        }).ConfigureAwait(false);
    }

    private static Dictionary<string, long> LogsQuery(string rawTarget)
    {
        var values = new Dictionary<string, long>(StringComparer.Ordinal);
        var mark = rawTarget.IndexOf('?');
        if (mark < 0) return values;
        foreach (var pair in rawTarget[(mark + 1)..].Split('&'))
        {
            var equals = pair.IndexOf('=');
            GatewayRules.Require(equals > 0, "request.invalid");
            var name = pair[..equals];
            var text = pair[(equals + 1)..];
            GatewayRules.Require(name is "after" or "own_after" or "limit" && text.Length is > 0 and <= 18 &&
                text.All(char.IsAsciiDigit) && !values.ContainsKey(name), "request.invalid");
            values[name] = long.Parse(text, System.Globalization.CultureInfo.InvariantCulture);
        }
        return values;
    }

    /// <summary>Records a refused or failed request in this host's log: what was asked (the route and device for model
    /// requests), the stable code and HTTP status, and the trace ID the desktop also shows. Never the request body.</summary>
    private void LogFailure(HttpContext? context, Guid traceId, string code, int status)
    {
        var failure = GatewayFailures.Get(code);
        var what = context?.Items[RouteItem] is string route
            ? $"{route} request from {context.Items[DeviceItem] as string ?? "a paired device"}"
            : context is null ? "A request" : $"{context.Request.Method} {context.Request.Path}";
        var level = code == "job.canceled" ? LogLevels.Info : status >= 500 ? LogLevels.Error : LogLevels.Warn;
        Logs.Own(level, $"{what} failed: {code} (HTTP {status}). {failure.Summary} Trace {traceId}.",
            repeatKey: $"{code}|{(context?.Items[RouteItem] as string) ?? context?.Request.Method}");
    }

    private sealed record LogsDocument
    {
        public required GatewayProtocolVersion ProtocolVersion { get; init; }
        public required string HostId { get; init; }
        public required long Next { get; init; }
        public required bool More { get; init; }
        public required IReadOnlyList<LogRecord> Entries { get; init; }
    }

    private sealed record LogsAcceptedDocument
    {
        public required GatewayProtocolVersion ProtocolVersion { get; init; }
        public required string HostId { get; init; }
        public required int Accepted { get; init; }
        public required IReadOnlyList<LogMark> Marks { get; init; }
    }
}
