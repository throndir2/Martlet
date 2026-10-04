using System.Text.Json;
using System.Text.Json.Serialization;
using Martlet.Core.Contracts;

namespace Martlet.Core.Logs;

/// <summary>One paired host as log sharing sees it (the desktop's paired, signed, pinned connection; docs/DIAGNOSTICS.md).
/// Every call throws <see cref="LogHostException"/> when the host can't be used.</summary>
public interface ILogHost
{
    string HostId { get; }

    /// <summary>The lines the host keeps after store position <paramref name="after"/> (0 for the oldest), oldest first, with
    /// the position to continue from and whether more are waiting.</summary>
    Task<(IReadOnlyList<LogRecord> Entries, long Next, bool More)> ReadAsync(long after, int limit, CancellationToken token);

    /// <summary>Gives the host a batch of lines; returns how many it kept and its marks for every stream the batch names.</summary>
    Task<(int Accepted, IReadOnlyList<LogMark> Marks)> PushAsync(LogBatch batch, CancellationToken token);
}

/// <summary>A host that can't share logs now: unreachable, refusing, or (<see cref="Old"/>) a Martlet older than shared logs.</summary>
public sealed class LogHostException(string message, bool old = false, Exception? inner = null) : Exception(message, inner)
{
    public bool Old { get; } = old;
}

public enum LogShareState
{
    Shared,
    Unreachable,
    Old
}

/// <summary>How one host took part in a <see cref="LogShare.RunAsync"/>: lines read from it, lines it kept from this computer
/// and lines that wait for the next run.</summary>
public sealed record LogHostShare(string HostId, LogShareState State, int Read, int Sent, int Waiting);

/// <summary>What one <see cref="LogShare.RunAsync"/> did: each host's part, the lines new to this computer, the lines the hosts
/// kept and why the copy of the other computers' lines couldn't be saved (or null).</summary>
public sealed record LogShareResult(IReadOnlyList<LogHostShare> Hosts, int Received, int Sent, string? Problem = null)
{
    public int Shared => Hosts.Count(h => h.State == LogShareState.Shared);

    public int Waiting => Hosts.Sum(h => h.Waiting);

    /// <summary>The run in a sentence or two, such as "Sharing logs with 2 of 2 hosts. Waiting for gpu-box."</summary>
    public string Describe()
    {
        if (Hosts.Count == 0) return "No Martlet hosts are paired with this PC yet, so it shows only its own logs.";
        var unreachable = Hosts.Where(h => h.State == LogShareState.Unreachable).Select(h => h.HostId).ToArray();
        var old = Hosts.Where(h => h.State == LogShareState.Old).Select(h => h.HostId).ToArray();
        return $"Sharing logs with {Shared} of {Hosts.Count} host{(Hosts.Count == 1 ? "" : "s")}." +
            (Waiting > 0 ? $" {Waiting:N0} more line{(Waiting == 1 ? "" : "s")} will follow." : "") +
            (unreachable.Length > 0 ? $" Waiting for {string.Join(", ", unreachable)}." : "") +
            (old.Length > 0 ? $" Update {string.Join(", ", old)} to share {(old.Length == 1 ? "its" : "their")} logs." : "") +
            (Problem is { } problem ? $" Couldn't keep a copy here: {problem}" : "");
    }
}

/// <summary>Every other computer's log lines that this computer holds: other desktops' apps, avatar renderers and host runs,
/// and every host's gateway, as log sharing collected them (<see cref="FileName"/> in this computer's logs folder). This
/// computer's own lines stay in its local log files. Each line is kept once per stream and sequence number; bounded to the
/// newest <see cref="MaximumEntries"/> lines and about <see cref="MaximumBytes"/>. Thread-safe.</summary>
public sealed class NetworkLogs
{
    public const string FileName = "network-logs.json";
    public const int MaximumEntries = 10_000;
    public const long MaximumBytes = 4_000_000;
    private const long MaximumFileBytes = 64L * 1024 * 1024;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        MaxDepth = 8
    };

    private readonly object gate = new();
    private readonly List<LogRecord> entries = [];
    private readonly HashSet<(string Stream, long Seq)> keys = [];
    private long bytes;
    private DateTimeOffset trimmedThrough = DateTimeOffset.MinValue;

    /// <summary>How the saved copy read: "none" (no file yet), "loaded" or "unreadable" (it starts empty and is replaced at the
    /// next save).</summary>
    public string LoadState { get; private init; } = "none";

    public int Count { get { lock (gate) return entries.Count; } }

    /// <summary>Every line held, oldest first.</summary>
    public IReadOnlyList<LogRecord> Snapshot()
    {
        lock (gate) return entries.ToArray();
    }

    /// <summary>Adds the valid lines not held yet (nothing older than lines already dropped to stay in bounds) and returns how
    /// many it added.</summary>
    public int Add(IEnumerable<LogRecord> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var added = 0;
        lock (gate)
        {
            foreach (var line in lines)
            {
                if (line is null || line.At <= trimmedThrough || keys.Contains((line.Stream, line.Seq)) || !IsValid(line)) continue;
                entries.Add(line);
                keys.Add((line.Stream, line.Seq));
                bytes += Size(line);
                added++;
            }
            if (added > 0)
            {
                entries.Sort(Oldest);
                Trim();
            }
        }
        return added;
    }

    /// <summary>The copy saved in <paramref name="logDirectory"/>, or an empty one (an unreadable file starts empty).</summary>
    public static NetworkLogs Load(string logDirectory)
    {
        var path = Path.Combine(logDirectory, FileName);
        if (!File.Exists(path)) return new();
        try
        {
            if (new FileInfo(path).Length > MaximumFileBytes) return new() { LoadState = "unreadable" };
            var document = JsonSerializer.Deserialize<Document>(File.ReadAllBytes(path), Json);
            if (document is not { SchemaVersion: 1 }) return new() { LoadState = "unreadable" };
            var logs = new NetworkLogs { LoadState = "loaded" };
            logs.Add(document.Entries?.OfType<LogRecord>() ?? []);
            lock (logs.gate)
                if (document.TrimmedThrough is { } through && through > logs.trimmedThrough) logs.trimmedThrough = through;
            return logs;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or NotSupportedException or
            InvalidOperationException)
        {
            return new() { LoadState = "unreadable" };
        }
    }

    /// <summary>Writes the copy to <paramref name="logDirectory"/> (replacing the saved one in one step). Throws on storage
    /// failure.</summary>
    public void Save(string logDirectory)
    {
        byte[] data;
        lock (gate)
            data = JsonSerializer.SerializeToUtf8Bytes(new Document
            {
                SchemaVersion = 1, TrimmedThrough = trimmedThrough == DateTimeOffset.MinValue ? null : trimmedThrough, Entries = entries.ToArray()
            }, Json);
        Directory.CreateDirectory(logDirectory);
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
        if (order == 0) order = string.CompareOrdinal(left.Stream, right.Stream);
        return order != 0 ? order : left.Seq.CompareTo(right.Seq);
    }

    private static long Size(LogRecord line) => line.Message.Length * 2L + 240;

    private void Trim()
    {
        var remove = Math.Max(entries.Count - MaximumEntries, 0);
        var kept = bytes - entries.Take(remove).Sum(Size);
        while (remove < entries.Count && kept > MaximumBytes)
        {
            kept -= Size(entries[remove]);
            remove++;
        }
        if (remove == 0) return;
        foreach (var line in entries.Take(remove)) keys.Remove((line.Stream, line.Seq));
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

/// <summary>
/// Shares the logs of every computer in the owner's Martlet network with all of them (docs/DIAGNOSTICS.md). Hosts never talk to
/// each other, so each desktop carries the lines: every run it reads each paired host's new lines (every computer's lines that
/// host holds, its own gateway's included) into <see cref="Logs"/>, then gives every host the lines it lacks, from this
/// computer's own logs and from every other computer's lines it holds. Each host keeps only lines newer than its mark per
/// stream (computer and part), so a line delivered by several desktops, twice or after a restart is kept once, and a host that
/// was away catches up from whichever desktop reaches it. One run at a time.
/// </summary>
public sealed class LogShare
{
    public const int PageSize = 1_000;
    public const int MaximumPagesPerRun = 8;
    public const int MaximumBatchesPerRun = 4;
    public const int BatchBudget = 360 * 1024;

    /// <summary>Lines older than this are not sent to a host that has nothing yet from their stream.</summary>
    public static readonly TimeSpan FirstSendWindow = TimeSpan.FromDays(7);

    private readonly string? logDirectory;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<string, Peer> peers = new(StringComparer.Ordinal);

    /// <param name="logDirectory">This computer's logs folder, where <see cref="NetworkLogs.FileName"/> is kept (null keeps
    /// the other computers' lines in memory only).</param>
    /// <param name="device">This computer's device ID: the source of its own lines.</param>
    public LogShare(string? logDirectory, string device)
    {
        ContractRules.Identifier(device);
        this.logDirectory = logDirectory;
        Device = device;
        Logs = logDirectory is null ? new NetworkLogs() : NetworkLogs.Load(logDirectory);
    }

    public string Device { get; }

    /// <summary>Every other computer's lines this computer holds.</summary>
    public NetworkLogs Logs { get; }

    /// <summary>The last run's result, or null before the first.</summary>
    public LogShareResult? Last { get; private set; }

    /// <summary>One run: reads every host's new lines and, when <paramref name="send"/>, gives each host what it lacks.</summary>
    /// <param name="local">This computer's own lines (its local logs, any order); lines from other sources are ignored.</param>
    public async Task<LogShareResult> RunAsync(IReadOnlyList<ILogHost> hosts, IReadOnlyList<LogRecord> local, bool send, DateTimeOffset now,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(hosts);
        ArgumentNullException.ThrowIfNull(local);
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            foreach (var gone in peers.Keys.Where(id => hosts.All(h => h.HostId != id)).ToArray()) peers.Remove(gone);
            var own = local.Where(r => r is not null && r.Source == Device && NetworkLogs.IsValid(r)).ToArray();
            var reads = await Task.WhenAll(hosts.Select(host => ReadAsync(host, PeerFor(host.HostId), send, own, token))).ConfigureAwait(false);

            var received = Logs.Add(reads.SelectMany(r => r.Lines).Where(r => r.Source != Device));
            string? problem = null;
            if (received > 0 && logDirectory is not null)
                try { Logs.Save(logDirectory); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { problem = error.Message; }

            var shares = reads.Select(r => new LogHostShare(r.Host.HostId, r.State, r.Lines.Count, 0, 0)).ToArray();
            if (send)
            {
                var streams = own.Concat(Logs.Snapshot()).GroupBy(r => r.Stream, StringComparer.Ordinal)
                    .ToDictionary(g => g.Key, g => g.OrderBy(r => r.Seq).ToArray(), StringComparer.Ordinal);
                var pushes = await Task.WhenAll(reads.Select(r => r.State == LogShareState.Shared
                    ? PushAsync(r.Host, PeerFor(r.Host.HostId), streams, now, token)
                    : Task.FromResult((State: r.State, Sent: 0, Waiting: 0)))).ConfigureAwait(false);
                shares = shares.Select((share, index) => share with
                {
                    State = pushes[index].State, Sent = pushes[index].Sent, Waiting = pushes[index].Waiting
                }).ToArray();
            }
            var result = new LogShareResult(shares, received, shares.Sum(s => s.Sent), problem);
            Last = result;
            return result;
        }
        finally
        {
            gate.Release();
        }
    }

    private Peer PeerFor(string hostId) => peers.TryGetValue(hostId, out var peer) ? peer : peers[hostId] = new();

    private async Task<(ILogHost Host, LogShareState State, IReadOnlyList<LogRecord> Lines)> ReadAsync(ILogHost host, Peer peer, bool send,
        IReadOnlyList<LogRecord> own, CancellationToken token)
    {
        try
        {
            // The first contact asks for the host's marks, so the first batch carries only what it lacks.
            if (send && !peer.MarksKnown)
            {
                var named = own.Select(r => r.Stream).Concat(Logs.Snapshot().Select(r => r.Stream))
                    .Where(s => s != OwnStream(host.HostId)).Distinct(StringComparer.Ordinal).Take(LogBatch.MaximumStreams).ToArray();
                var (_, marks) = await host.PushAsync(new LogBatch { SchemaVersion = 1, Streams = named.Select(StreamOf).ToArray() }, token)
                    .ConfigureAwait(false);
                peer.Raise(marks);
                peer.MarksKnown = true;
            }
            var lines = new List<LogRecord>();
            for (var page = 0; page < MaximumPagesPerRun; page++)
            {
                var (entries, next, more) = await host.ReadAsync(peer.After, PageSize, token).ConfigureAwait(false);
                lines.AddRange(entries);
                foreach (var entry in entries) peer.Raise(entry.Stream, entry.Seq);
                peer.After = Math.Max(peer.After, next);
                if (!more || entries.Count == 0) break;
            }
            return (host, LogShareState.Shared, lines);
        }
        catch (LogHostException error)
        {
            peer.MarksKnown = false;
            return (host, error.Old ? LogShareState.Old : LogShareState.Unreachable, []);
        }
    }

    private async Task<(LogShareState State, int Sent, int Waiting)> PushAsync(ILogHost host, Peer peer,
        IReadOnlyDictionary<string, LogRecord[]> streams, DateTimeOffset now, CancellationToken token)
    {
        var sent = 0;
        try
        {
            for (var batch = 0; batch < MaximumBatchesPerRun; batch++)
            {
                var pending = Pending(host.HostId, peer, streams, now);
                var entries = Fit(pending);
                if (entries.Count == 0) return (LogShareState.Shared, sent, 0);
                var named = pending.Where(p => p.Count > 0).Select(p => p[0].Stream)
                    .Concat(streams.Keys.Where(s => s != OwnStream(host.HostId)).Order(StringComparer.Ordinal))
                    .Distinct(StringComparer.Ordinal).Take(LogBatch.MaximumStreams).ToArray();
                var (accepted, marks) = await host.PushAsync(new LogBatch
                {
                    SchemaVersion = 1, Streams = named.Select(StreamOf).ToArray(), Entries = entries
                }, token).ConfigureAwait(false);
                peer.Raise(marks);
                // The host has seen every line it was given: it kept it or already held a newer one.
                foreach (var entry in entries) peer.Raise(entry.Stream, entry.Seq);
                sent += accepted;
            }
            return (LogShareState.Shared, sent, Pending(host.HostId, peer, streams, now).Sum(p => p.Count));
        }
        catch (LogHostException error)
        {
            peer.MarksKnown = false;
            return (error.Old ? LogShareState.Old : LogShareState.Unreachable, sent, 0);
        }
    }

    /// <summary>Per stream, in order, the lines newer than the host's mark (a host that has nothing from a stream gets at most
    /// <see cref="FirstSendWindow"/> of it). A host's own gateway lines are never sent back to it.</summary>
    private static List<List<LogRecord>> Pending(string hostId, Peer peer, IReadOnlyDictionary<string, LogRecord[]> streams, DateTimeOffset now)
    {
        var since = now - FirstSendWindow;
        var pending = new List<List<LogRecord>>();
        foreach (var (stream, lines) in streams)
        {
            if (stream == OwnStream(hostId)) continue;
            var mark = peer.Mark(stream);
            var waiting = lines.Where(r => r.Seq > mark && (mark > 0 || r.At >= since)).ToList();
            if (waiting.Count > 0) pending.Add(waiting);
        }
        return pending;
    }

    /// <summary>The oldest pending lines across streams that fit one batch. Each stream gives a prefix of its lines, so a host's
    /// mark never passes a line it wasn't given.</summary>
    private static List<LogRecord> Fit(List<List<LogRecord>> pending)
    {
        var result = new List<LogRecord>();
        var next = new int[pending.Count];
        var used = 0L;
        while (result.Count < LogBatch.MaximumEntries)
        {
            var pick = -1;
            for (var i = 0; i < pending.Count; i++)
                if (next[i] < pending[i].Count && (pick < 0 || pending[i][next[i]].At < pending[pick][next[pick]].At)) pick = i;
            if (pick < 0) break;
            var record = pending[pick][next[pick]];
            // JSON escapes non-ASCII text as \uXXXX, so a character can take six bytes.
            var size = record.Message.Length * 6L + 320;
            if (used + size > BatchBudget) break;
            used += size;
            result.Add(record.RelayedBy is null ? record : record with { RelayedBy = null });
            next[pick]++;
        }
        return result;
    }

    private static string OwnStream(string hostId) => hostId + "/" + LogComponents.Gateway;

    private static LogStream StreamOf(string stream)
    {
        var split = stream.LastIndexOf('/');
        return new() { Source = stream[..split], Component = stream[(split + 1)..] };
    }

    /// <summary>What this computer knows of one host: the store position read up to and, per stream, the newest line the host
    /// holds at least (<see cref="MarksKnown"/> once the host itself said so this session).</summary>
    private sealed class Peer
    {
        private readonly Dictionary<string, long> marks = new(StringComparer.Ordinal);
        internal long After;
        internal bool MarksKnown;

        internal long Mark(string stream) => marks.GetValueOrDefault(stream);

        internal void Raise(string stream, long seq)
        {
            if (seq > marks.GetValueOrDefault(stream)) marks[stream] = seq;
        }

        internal void Raise(IEnumerable<LogMark> values)
        {
            foreach (var mark in values) Raise(mark.Source + "/" + mark.Component, mark.Seq);
        }
    }
}
