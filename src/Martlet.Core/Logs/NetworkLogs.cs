using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Martlet.Core.Contracts;

namespace Martlet.Core.Logs;

/// <summary>Every other computer's log lines that this computer holds: other desktops' apps, avatar renderers and host runs,
/// and every host's gateway, as log sharing collected them. This computer's own lines stay in its local log files. Each line is
/// kept once per stream and sequence number, for <see cref="LogRetention.Window"/> (a week) within the
/// <see cref="LogRetention"/> caps. On disk (this computer's logs folder) the copy is <see cref="FileName"/>, written in full now
/// and then, and <see cref="JournalFileName"/>, where each run appends the lines it added, so a run never rewrites a week of
/// lines. Thread-safe.</summary>
public sealed class NetworkLogs
{
    public const string FileName = "network-logs.json";
    /// <summary>The lines added since <see cref="FileName"/> was last written in full, one JSON line each.</summary>
    public const string JournalFileName = "network-logs.journal";
    public const int MaximumEntries = LogRetention.MaximumEntries;
    public const long MaximumBytes = LogRetention.MaximumBytes;
    private const long MaximumFileBytes = 256L * 1024 * 1024;
    /// <summary>The journal is folded into the full file once it is larger than this and a quarter of the full file.</summary>
    private const long MinimumJournalBytes = 4L * 1024 * 1024;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        MaxDepth = 8
    };

    private readonly object gate = new();
    private readonly object saveGate = new();
    private readonly TimeProvider clock;
    // Oldest first (Oldest), and each stream's lines in sequence order, so a run reads what a host lacks without sorting a week.
    private readonly List<LogRecord> entries = [];
    private readonly Dictionary<string, StreamLines> streams = new(StringComparer.Ordinal);
    // Computer, part and level names are few, so every line shares one copy of each.
    private readonly Dictionary<string, string> names = new(StringComparer.Ordinal);
    private readonly List<LogRecord> unsaved = [];
    private long bytes;
    private DateTimeOffset trimmedThrough = DateTimeOffset.MinValue;
    private long fileBytes, journalBytes;
    private bool rewrite = true;

    private sealed class StreamLines
    {
        internal readonly List<LogRecord> Lines = [];
        internal readonly HashSet<long> Seqs = [];
    }

    public NetworkLogs(TimeProvider? clock = null) => this.clock = clock ?? TimeProvider.System;

    /// <summary>How the saved copy read: "none" (no file yet), "loaded" or "unreadable" (it starts empty and is replaced at the
    /// next save).</summary>
    public string LoadState { get; private init; } = "none";

    public int Count { get { lock (gate) return entries.Count; } }

    /// <summary>Every line held, oldest first.</summary>
    public IReadOnlyList<LogRecord> Snapshot()
    {
        lock (gate) return entries.ToArray();
    }

    /// <summary>Every stream held ("computer/part").</summary>
    public IReadOnlyList<string> StreamNames()
    {
        lock (gate) return streams.Keys.ToArray();
    }

    /// <summary>Up to <paramref name="limit"/> of <paramref name="stream"/>'s lines after sequence number
    /// <paramref name="mark"/>, in order; when <paramref name="mark"/> is 0, only lines from <paramref name="since"/> on.</summary>
    public IReadOnlyList<LogRecord> After(string stream, long mark, DateTimeOffset since, int limit = int.MaxValue)
    {
        lock (gate)
        {
            if (!streams.TryGetValue(stream, out var held)) return [];
            var result = new List<LogRecord>();
            for (var i = FirstAfter(held.Lines, mark); i < held.Lines.Count && result.Count < limit; i++)
                if (mark > 0 || held.Lines[i].At >= since) result.Add(held.Lines[i]);
            return result;
        }
    }

    /// <summary>How many of <paramref name="stream"/>'s lines <see cref="After"/> would give without a limit.</summary>
    public int CountAfter(string stream, long mark, DateTimeOffset since)
    {
        lock (gate)
        {
            if (!streams.TryGetValue(stream, out var held)) return 0;
            var count = 0;
            for (var i = FirstAfter(held.Lines, mark); i < held.Lines.Count; i++)
                if (mark > 0 || held.Lines[i].At >= since) count++;
            return count;
        }
    }

    /// <summary>Adds the valid lines not held yet from the last week (nothing older than lines already dropped to stay in
    /// bounds) and returns how many it added.</summary>
    public int Add(IEnumerable<LogRecord> lines) => Add(lines, clock.GetUtcNow());

    /// <summary><see cref="Add(IEnumerable{LogRecord})"/> as of <paramref name="now"/>.</summary>
    public int Add(IEnumerable<LogRecord> lines, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(lines);
        lock (gate) return AddLocked(lines, now, keep: true);
    }

    private int AddLocked(IEnumerable<LogRecord> lines, DateTimeOffset now, bool keep)
    {
        var cutoff = now - LogRetention.Window;
        var added = 0;
        var sorted = true;
        foreach (var line in lines)
        {
            if (line is null || line.At <= trimmedThrough || line.At < cutoff || !IsValid(line)) continue;
            var stream = line.Stream;
            if (!streams.TryGetValue(stream, out var held)) streams[stream] = held = new();
            if (!held.Seqs.Add(line.Seq)) continue;
            var record = Shared(line);
            if (held.Lines.Count == 0 || held.Lines[^1].Seq < record.Seq) held.Lines.Add(record);
            else held.Lines.Insert(FirstAfter(held.Lines, record.Seq), record);
            if (entries.Count > 0 && Oldest(entries[^1], record) > 0) sorted = false;
            entries.Add(record);
            bytes += LogRetention.Size(record);
            if (keep) unsaved.Add(record);
            added++;
        }
        if (added > 0)
        {
            if (!sorted) entries.Sort(Oldest);
            Trim(cutoff);
        }
        return added;
    }

    /// <summary>The copy saved in <paramref name="logDirectory"/>, or an empty one (an unreadable file starts empty).</summary>
    public static NetworkLogs Load(string logDirectory, TimeProvider? clock = null)
    {
        var path = Path.Combine(logDirectory, FileName);
        var journal = Path.Combine(logDirectory, JournalFileName);
        if (!File.Exists(path) && !File.Exists(journal)) return new(clock);
        try
        {
            var logs = new NetworkLogs(clock) { LoadState = "loaded" };
            var now = logs.clock.GetUtcNow();
            lock (logs.gate)
            {
                if (File.Exists(path))
                {
                    var length = new FileInfo(path).Length;
                    if (length > MaximumFileBytes) return new(clock) { LoadState = "unreadable" };
                    var document = JsonSerializer.Deserialize<Document>(File.ReadAllBytes(path), Json);
                    if (document is not { SchemaVersion: 1 }) return new(clock) { LoadState = "unreadable" };
                    logs.AddLocked(document.Entries?.OfType<LogRecord>() ?? [], now, keep: false);
                    if (document.TrimmedThrough is { } through && through > logs.trimmedThrough) logs.trimmedThrough = through;
                    logs.fileBytes = length;
                    logs.rewrite = false;
                }
                if (File.Exists(journal))
                {
                    var length = new FileInfo(journal).Length;
                    // A journal this large was never written by a run; the next save writes everything in full instead.
                    if (length <= MaximumFileBytes) logs.AddLocked(Journal(journal), now, keep: false);
                    else logs.rewrite = true;
                    // A last line cut short (power lost while appending) would join the next one: write the copy in full instead.
                    if (length > 0 && !EndsWithNewLine(journal)) logs.rewrite = true;
                    logs.journalBytes = length;
                }
            }
            return logs;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or NotSupportedException or
            InvalidOperationException)
        {
            return new(clock) { LoadState = "unreadable" };
        }
    }

    /// <summary>Saves the lines added since the last save to <paramref name="logDirectory"/>: appended to the journal, or, now
    /// and then, the whole copy written in full (replacing the saved one in one step) and the journal emptied. Throws on storage
    /// failure; then the next save writes the whole copy.</summary>
    public void Save(string logDirectory)
    {
        lock (saveGate)
        {
            LogRecord[]? all = null;
            byte[]? appended = null;
            lock (gate)
            {
                if (unsaved.Count == 0 && !rewrite) return;
                using var buffer = new MemoryStream();
                foreach (var line in unsaved)
                {
                    JsonSerializer.Serialize(buffer, line, Json);
                    buffer.WriteByte((byte)'\n');
                }
                unsaved.Clear();
                if (rewrite || journalBytes + buffer.Length > Math.Max(MinimumJournalBytes, fileBytes / 4)) all = entries.ToArray();
                else appended = buffer.ToArray();
            }
            var journal = Path.Combine(logDirectory, JournalFileName);
            try
            {
                Directory.CreateDirectory(logDirectory);
                if (appended is not null)
                {
                    using (var stream = new FileStream(journal, FileMode.Append, FileAccess.Write, FileShare.Read))
                        stream.Write(appended);
                    lock (gate) journalBytes += appended.Length;
                    return;
                }
                DateTimeOffset through;
                lock (gate) through = trimmedThrough;
                var data = JsonSerializer.SerializeToUtf8Bytes(new Document
                {
                    SchemaVersion = 1, TrimmedThrough = through == DateTimeOffset.MinValue ? null : through, Entries = all!
                }, Json);
                var path = Path.Combine(logDirectory, FileName);
                var temporary = Path.Combine(logDirectory, $"network-logs.{Guid.NewGuid():N}.tmp");
                try
                {
                    File.WriteAllBytes(temporary, data);
                    File.Move(temporary, path, overwrite: true);
                }
                finally
                {
                    if (File.Exists(temporary)) File.Delete(temporary);
                }
                File.Delete(journal);
                lock (gate)
                {
                    fileBytes = data.Length;
                    journalBytes = 0;
                    rewrite = false;
                }
            }
            catch
            {
                lock (gate) rewrite = true;
                throw;
            }
        }
    }

    internal static bool IsValid(LogRecord line)
    {
        try
        {
            line.Validate();
            return true;
        }
        catch (ContractException) { return false; }
    }

    internal static int Oldest(LogRecord left, LogRecord right)
    {
        var order = left.At.CompareTo(right.At);
        if (order == 0) order = string.CompareOrdinal(left.Source, right.Source);
        if (order == 0) order = string.CompareOrdinal(left.Component, right.Component);
        return order != 0 ? order : left.Seq.CompareTo(right.Seq);
    }

    private static bool EndsWithNewLine(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        stream.Seek(-1, SeekOrigin.End);
        return stream.ReadByte() == '\n';
    }

    private static IEnumerable<LogRecord> Journal(string path)
    {
        foreach (var text in File.ReadLines(path, Encoding.UTF8))
        {
            if (text.Length == 0) continue;
            LogRecord? line = null;
            // A line cut short (power lost while appending) is skipped; the hosts still hold it.
            try { line = JsonSerializer.Deserialize<LogRecord>(text, Json); }
            catch (JsonException) { }
            if (line is not null) yield return line;
        }
    }

    /// <summary>The position of the first line with a sequence number above <paramref name="seq"/>.</summary>
    private static int FirstAfter(List<LogRecord> lines, long seq)
    {
        int low = 0, high = lines.Count;
        while (low < high)
        {
            var middle = (low + high) >>> 1;
            if (lines[middle].Seq <= seq) low = middle + 1;
            else high = middle;
        }
        return low;
    }

    private LogRecord Shared(LogRecord line)
    {
        var source = Name(line.Source);
        var component = Name(line.Component);
        var level = Name(line.Level);
        var relayed = line.RelayedBy is null ? null : Name(line.RelayedBy);
        return ReferenceEquals(source, line.Source) && ReferenceEquals(component, line.Component) && ReferenceEquals(level, line.Level) &&
            ReferenceEquals(relayed, line.RelayedBy)
            ? line
            : line with { Source = source, Component = component, Level = level, RelayedBy = relayed };
    }

    private string Name(string text)
    {
        if (names.TryGetValue(text, out var shared)) return shared;
        if (names.Count < 4096) names[text] = text;
        return text;
    }

    private void Trim(DateTimeOffset cutoff)
    {
        var remove = 0;
        var kept = bytes;
        while (remove < entries.Count &&
            (entries[remove].At < cutoff || entries.Count - remove > MaximumEntries || kept > MaximumBytes))
        {
            kept -= LogRetention.Size(entries[remove]);
            remove++;
        }
        if (remove == 0) return;
        foreach (var group in entries.Take(remove).GroupBy(line => line.Stream, StringComparer.Ordinal))
        {
            var held = streams[group.Key];
            var gone = new HashSet<LogRecord>(group, ReferenceEqualityComparer.Instance);
            held.Lines.RemoveAll(gone.Contains);
            foreach (var line in gone) held.Seqs.Remove(line.Seq);
            if (held.Lines.Count == 0) streams.Remove(group.Key);
        }
        trimmedThrough = entries[remove - 1].At;
        entries.RemoveRange(0, remove);
        bytes = kept;
    }

    private sealed record Document
    {
        public int SchemaVersion { get; init; }
        public DateTimeOffset? TrimmedThrough { get; init; }
        public LogRecord?[]? Entries { get; init; }
    }
}
