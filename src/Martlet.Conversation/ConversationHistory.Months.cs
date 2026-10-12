using System.Collections.Concurrent;
using System.Text;

namespace Martlet.Conversation;

/// <summary>What a month file of the record looked like when it was read: its length and last write, and how many times this
/// instance of the record (<paramref name="Epoch"/>) wrote it, so the record sync knows whether anything changed there since
/// (an append, an edit, a deletion) even when two writes fall within one tick of the file system's clock. A missing file has
/// length 0 and no write time.</summary>
public readonly record struct HistoryMonthStamp(long Length, DateTime WrittenUtc, Guid Epoch = default, int Writes = 0)
{
    /// <summary>Whether nothing changed between <paramref name="saved"/> and <paramref name="now"/>: within one instance of the
    /// record its own write count decides as well; across a restart the length and the write time.</summary>
    public static bool Unchanged(HistoryMonthStamp? saved, HistoryMonthStamp now) =>
        saved is { } before && before.Length == now.Length && before.WrittenUtc == now.WrittenUtc &&
        (before.Epoch != now.Epoch || before.Writes == now.Writes);
}

/// <summary>One exchange of a month file and its line as it is on disk (newer fields this Martlet doesn't know stay in the line).</summary>
public sealed record HistoryMonthLine(HistoryExchange Exchange, string Line)
{
    public override string ToString() => $"{nameof(HistoryMonthLine)} {Exchange.Id}";
}

/// <summary>The exchanges of one month file (each ID once, the first line for it) and the file's stamp when they were read.</summary>
public sealed record HistoryMonth(string Month, IReadOnlyList<HistoryMonthLine> Lines, HistoryMonthStamp Stamp)
{
    public override string ToString() => $"{nameof(HistoryMonth)} {Month} ({Lines.Count} exchanges)";
}

/// <summary>The record by month, for the record sync (<see cref="ConversationRecordSync"/>): which months have files, reading
/// one month's lines, and putting other computers' changes into one month. Each takes the files in turn with appends, edits and
/// deletions, so a change made here while the sync runs is never lost, and none of it is on a reply's path.</summary>
public sealed partial class ConversationHistory
{
    // Which instance of the record this is, and how many times it wrote each month file (by file name).
    private readonly Guid epoch = Guid.NewGuid();
    private readonly ConcurrentDictionary<string, int> writes = new(StringComparer.OrdinalIgnoreCase);

    private void Wrote(string path) => writes.AddOrUpdate(Path.GetFileName(path), 1, (_, count) => count + 1);

    /// <summary>The months (<c>yyyy-MM</c>, UTC) the record has a file for, oldest first.</summary>
    public IReadOnlyList<string> Months() =>
        [.. MonthFiles(Directory).Select(path => Path.GetFileName(path)["history-".Length..^".jsonl".Length])];

    /// <summary>The month file of <paramref name="month"/> (<c>yyyy-MM</c>).</summary>
    public static string MonthFileName(string month) => "history-" + month + ".jsonl";

    /// <summary>The stamp of <paramref name="month"/>'s file now (no lock: the sync compares it with the one it saved).</summary>
    public HistoryMonthStamp Stamp(string month)
    {
        var name = MonthFileName(month);
        var count = writes.GetValueOrDefault(name);
        try
        {
            var file = new FileInfo(Path.Combine(Directory, name));
            return file.Exists ? new(file.Length, file.LastWriteTimeUtc, epoch, count) : new(0, default, epoch, count);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return new(-1, default, epoch, count); }
    }

    /// <summary>The exchange a line of the record holds, or null when this Martlet can't read it.</summary>
    public static HistoryExchange? FromLine(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        return Parse(line);
    }

    /// <summary>Reads one month file: each exchange once (the first line for an ID), lines that can't be read left out, and the
    /// file's stamp as it was read.</summary>
    public async Task<HistoryMonth> ReadMonthAsync(string month, CancellationToken token = default)
    {
        var path = Path.Combine(Directory, MonthFileName(month));
        await files.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (!File.Exists(path)) return new(month, [], Stamp(month));
            if (new FileInfo(path).Length > MaximumFileBytes) throw new IOException("A month of the record is too large to share.");
            var lines = new List<HistoryMonthLine>();
            var seen = new HashSet<Guid>();
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(stream, new UTF8Encoding(false, false)))
                while (await reader.ReadLineAsync(token).ConfigureAwait(false) is { } line)
                    if (!string.IsNullOrWhiteSpace(line) && Parse(line) is { } exchange && seen.Add(exchange.Id))
                        lines.Add(new(exchange, line));
            return new(month, lines, Stamp(month));
        }
        finally
        {
            files.Release();
        }
    }

    /// <summary>Puts other computers' changes into one month file and the index: each change is an exchange's ID and its new line
    /// (null deletes it). A line replaces the exchange's line in the file, or is added when the file doesn't have it; a line this
    /// Martlet can't read is left out. Lines of other exchanges, and lines this Martlet can't read, stay as they are. The file is
    /// rewritten through a temporary file and an atomic replace (deleted when nothing is left). Returns how many lines changed.</summary>
    public async Task<int> ApplyMonthAsync(string month, IReadOnlyDictionary<Guid, string?> changes, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(changes);
        var parsed = new Dictionary<Guid, (HistoryExchange? Exchange, string? Line)>();
        foreach (var (id, line) in changes)
        {
            if (line is null) parsed[id] = (null, null);
            else if (Parse(line) is { } exchange && exchange.Id == id) parsed[id] = (exchange, line);
        }
        if (parsed.Count == 0) return 0;
        var path = Path.Combine(Directory, MonthFileName(month));
        await files.WaitAsync(token).ConfigureAwait(false);
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            var kept = new StringBuilder();
            var handled = new HashSet<Guid>();
            var changed = 0;
            if (File.Exists(path))
                foreach (var line in File.ReadLines(path, Encoding.UTF8))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    if (Parse(line) is { } exchange && parsed.TryGetValue(exchange.Id, out var next))
                    {
                        // A copied line for the same exchange goes too: the exchange is kept once.
                        if (!handled.Add(exchange.Id) || next.Line is null)
                        {
                            changed++;
                            continue;
                        }
                        if (next.Line != line) changed++;
                        kept.Append(next.Line).Append('\n');
                        continue;
                    }
                    kept.Append(line).Append('\n');
                }
            foreach (var (id, next) in parsed)
                if (next.Line is not null && handled.Add(id))
                {
                    kept.Append(next.Line).Append('\n');
                    changed++;
                }
            if (changed == 0) return 0;
            Wrote(path);
            if (kept.Length == 0) File.Delete(path);
            else
            {
                var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        stream.Write(Encoding.UTF8.GetBytes(kept.ToString()));
                        stream.Flush(flushToDisk: true);
                    }
                    File.Move(temporary, path, overwrite: true);
                }
                finally
                {
                    if (File.Exists(temporary)) File.Delete(temporary);
                }
            }
            if (loaded) UpdateIndex(parsed);
            return changed;
        }
        finally
        {
            files.Release();
        }
    }

    // While the files are held: new exchanges newer than everything indexed are added at the end (the usual case: what another
    // computer recorded since the last sync); a change that keeps what was said is made in place; anything else builds the index
    // again outside the gate and swaps it in, so recall never waits for it.
    private void UpdateIndex(IReadOnlyDictionary<Guid, (HistoryExchange? Exchange, string? Line)> parsed)
    {
        List<HistoryExchange>? rebuild = null;
        lock (gate)
        {
            var entries = index.Entries;
            var positions = new Dictionary<Guid, int>(parsed.Count);
            for (var at = entries.Count - 1; at >= 0; at--)
                if (parsed.ContainsKey(entries[at].Exchange.Id)) positions.TryAdd(entries[at].Exchange.Id, at);
            var added = parsed.Where(pair => pair.Value.Exchange is not null && !positions.ContainsKey(pair.Key))
                .Select(pair => pair.Value.Exchange!).OrderBy(exchange => exchange.At).ToList();
            var inPlace = positions.All(pair => parsed[pair.Key].Exchange is { } after &&
                after.User == entries[pair.Value].Exchange.User && after.Reply == entries[pair.Value].Exchange.Reply &&
                after.ConversationId == entries[pair.Value].Exchange.ConversationId && after.At == entries[pair.Value].Exchange.At);
            var appendable = added.Count == 0 || entries.Count == 0 || added[0].At >= entries[^1].Exchange.At;
            if (inPlace && appendable && entries.Count + added.Count <= MaximumIndexedExchanges)
            {
                foreach (var (id, at) in positions) entries[at] = entries[at] with { Exchange = parsed[id].Exchange! };
                foreach (var exchange in added) index.Add(exchange);
                return;
            }
            rebuild = [];
            for (var at = 0; at < entries.Count; at++)
            {
                var exchange = entries[at].Exchange;
                // A changed exchange is kept once, at its place, in its new form; a deleted one goes.
                if (!parsed.TryGetValue(exchange.Id, out var next)) rebuild.Add(exchange);
                else if (next.Exchange is { } after && positions[exchange.Id] == at) rebuild.Add(after);
            }
            rebuild.AddRange(added);
        }
        // Exchanges made at the same moment keep the order they were in.
        var ordered = rebuild.OrderBy(exchange => exchange.At).ToList();
        var dropped = Math.Max(0, ordered.Count - MaximumIndexedExchanges);
        var built = Index.Of(ordered.Skip(dropped));
        lock (gate)
        {
            index = built;
            notIndexed += dropped;
        }
    }
}
