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

/// <summary>
/// Shares the logs of every computer in the owner's Martlet network with all of them (docs/DIAGNOSTICS.md). Hosts never talk to
/// each other, so each desktop carries the lines: every run it reads each paired host's new lines (every computer's lines that
/// host holds, its own gateway's included) into <see cref="Logs"/>, then gives every host the lines it lacks, from this
/// computer's own logs and from every other computer's lines it holds. Each host keeps only lines newer than its mark per
/// stream (computer and part), so a line delivered by several desktops, twice or after a restart is kept once, a host that
/// was away catches up from whichever desktop reaches it, and a host that came back without its newest lines (a power cut
/// before it saved) is read again from the start and given back what it lost. One run at a time.
/// </summary>
public sealed class LogShare
{
    public const int PageSize = 1_000;
    public const int MaximumPagesPerRun = 8;
    public const int MaximumBatchesPerRun = 4;
    public const int BatchBudget = 360 * 1024;

    /// <summary>Lines older than this are not sent to a host that has nothing yet from their stream.</summary>
    public static readonly TimeSpan FirstSendWindow = LogRetention.Window;

    /// <summary>How far this computer read each paired host, kept beside <see cref="NetworkLogs.FileName"/> so a restart goes on
    /// from there instead of reading a week of lines from every host again.</summary>
    public const string PositionsFileName = "network-logs-positions.json";

    private static readonly JsonSerializerOptions PositionsJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        MaxDepth = 4
    };

    private readonly string? logDirectory;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<string, Peer> peers = new(StringComparer.Ordinal);
    private string savedPositions = "";

    /// <param name="logDirectory">This computer's logs folder, where <see cref="NetworkLogs.FileName"/> is kept (null keeps
    /// the other computers' lines in memory only).</param>
    /// <param name="device">This computer's device ID: the source of its own lines.</param>
    /// <param name="clock">The time the saved copy is read at (lines older than a week are left out).</param>
    public LogShare(string? logDirectory, string device, TimeProvider? clock = null)
    {
        ContractRules.Identifier(device);
        this.logDirectory = logDirectory;
        Device = device;
        Logs = logDirectory is null ? new NetworkLogs(clock) : NetworkLogs.Load(logDirectory, clock);
        // Positions only mean something with the copy they were read into.
        if (logDirectory is not null && Logs.LoadState == "loaded") LoadPositions(logDirectory);
        savedPositions = Positions();
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

            var received = Logs.Add(reads.SelectMany(r => r.Lines).Where(r => r.Source != Device), now);
            string? problem = null;
            if (received > 0 && logDirectory is not null)
                try { Logs.Save(logDirectory); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { problem = error.Message; }
            if (problem is null && logDirectory is not null) SavePositions(logDirectory);

            var shares = reads.Select(r => new LogHostShare(r.Host.HostId, r.State, r.Lines.Count, 0, 0)).ToArray();
            if (send)
            {
                var streams = own.GroupBy(r => r.Stream, StringComparer.Ordinal)
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

    /// <summary>The read positions as saved, for noticing a change.</summary>
    private string Positions() => string.Join("|", peers.OrderBy(p => p.Key, StringComparer.Ordinal)
        .Select(p => $"{p.Key}:{p.Value.After}:{p.Value.LastRead?.Stream}:{p.Value.LastRead?.Seq}"));

    private void LoadPositions(string directory)
    {
        try
        {
            var path = Path.Combine(directory, PositionsFileName);
            if (!File.Exists(path) || new FileInfo(path).Length > 1024 * 1024) return;
            var saved = JsonSerializer.Deserialize<PositionsDocument>(File.ReadAllBytes(path), PositionsJson);
            if (saved is not { SchemaVersion: 1 }) return;
            foreach (var host in saved.Hosts ?? [])
                if (host is { HostId: { Length: > 0 } id, After: > 0, LastStream: { Length: > 0 } stream, LastSeq: > 0 } && stream.Contains('/'))
                    peers[id] = new() { After = host.After, LastRead = (stream, host.LastSeq) };
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or NotSupportedException) { }
    }

    /// <summary>Saves the read positions when they changed. A failure only means the next start reads the hosts again.</summary>
    private void SavePositions(string directory)
    {
        var now = Positions();
        if (now == savedPositions) return;
        try
        {
            Directory.CreateDirectory(directory);
            var data = JsonSerializer.SerializeToUtf8Bytes(new PositionsDocument
            {
                SchemaVersion = 1,
                Hosts = peers.Where(p => p.Value.After > 0 && p.Value.LastRead is not null).OrderBy(p => p.Key, StringComparer.Ordinal)
                    .Select(p => new PositionDocument { HostId = p.Key, After = p.Value.After, LastStream = p.Value.LastRead!.Value.Stream, LastSeq = p.Value.LastRead.Value.Seq })
                    .ToArray()
            }, PositionsJson);
            var path = Path.Combine(directory, PositionsFileName);
            var temporary = Path.Combine(directory, $"network-logs-positions.{Guid.NewGuid():N}.tmp");
            try
            {
                File.WriteAllBytes(temporary, data);
                File.Move(temporary, path, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
            savedPositions = now;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    private sealed record PositionsDocument
    {
        public int SchemaVersion { get; init; }
        public PositionDocument?[]? Hosts { get; init; }
    }

    private sealed record PositionDocument
    {
        public string? HostId { get; init; }
        public long After { get; init; }
        public string? LastStream { get; init; }
        public long LastSeq { get; init; }
    }

    private async Task<(ILogHost Host, LogShareState State, IReadOnlyList<LogRecord> Lines)> ReadAsync(ILogHost host, Peer peer, bool send,
        IReadOnlyList<LogRecord> own, CancellationToken token)
    {
        try
        {
            var lines = new List<LogRecord>();
            var checkedPosition = false;
            for (var page = 0; page < MaximumPagesPerRun; page++)
            {
                // The first page starts one line early, at the last line read: a host that restarted without saving its last
                // lines (a power cut, a kill) numbers new lines from an older position and holds older marks. Then this PC reads
                // that host again from the start and asks for its marks again, so nothing it lost or wrote since is missed.
                var check = !checkedPosition && peer.LastRead is not null && peer.After > 0;
                var (entries, next, more) = await host.ReadAsync(check ? peer.After - 1 : peer.After, PageSize, token).ConfigureAwait(false);
                if (check)
                {
                    checkedPosition = true;
                    if (entries.Count == 0 || (entries[0].Stream, entries[0].Seq) != peer.LastRead)
                    {
                        peer.After = 0;
                        peer.LastRead = null;
                        peer.MarksKnown = false;
                        continue;
                    }
                    entries = entries.Skip(1).ToArray();
                }
                lines.AddRange(entries);
                if (entries.Count > 0)
                {
                    peer.After = next;
                    peer.LastRead = (entries[^1].Stream, entries[^1].Seq);
                }
                if (!more) break;
            }
            // The first contact (and the first after a failure or a restart) asks for the host's marks, so the first batch
            // carries only what it lacks.
            if (send && !peer.MarksKnown)
            {
                var named = own.Select(r => r.Stream).Concat(Logs.StreamNames()).Concat(lines.Select(r => r.Stream))
                    .Where(s => s != OwnStream(host.HostId)).Distinct(StringComparer.Ordinal).Take(LogBatch.MaximumStreams).ToArray();
                var (_, marks) = await host.PushAsync(new LogBatch { SchemaVersion = 1, Streams = named.Select(StreamOf).ToArray() }, token)
                    .ConfigureAwait(false);
                peer.Replace(marks);
                peer.MarksKnown = true;
            }
            foreach (var line in lines) peer.Raise(line.Stream, line.Seq);
            return (host, LogShareState.Shared, lines);
        }
        catch (LogHostException error)
        {
            peer.MarksKnown = false;
            return (host, error.Old ? LogShareState.Old : LogShareState.Unreachable, []);
        }
    }

    private async Task<(LogShareState State, int Sent, int Waiting)> PushAsync(ILogHost host, Peer peer,
        IReadOnlyDictionary<string, LogRecord[]> own, DateTimeOffset now, CancellationToken token)
    {
        var sent = 0;
        try
        {
            for (var batch = 0; batch < MaximumBatchesPerRun; batch++)
            {
                var pending = Pending(host.HostId, peer, own, now);
                var entries = Fit(pending);
                if (entries.Count == 0) return (LogShareState.Shared, sent, 0);
                var named = pending.Where(p => p.Count > 0).Select(p => p[0].Stream)
                    .Concat(own.Keys.Concat(Logs.StreamNames()).Where(s => s != OwnStream(host.HostId)).Order(StringComparer.Ordinal))
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
            return (LogShareState.Shared, sent, Waiting(host.HostId, peer, own, now));
        }
        catch (LogHostException error)
        {
            peer.MarksKnown = false;
            return (error.Old ? LogShareState.Old : LogShareState.Unreachable, sent, 0);
        }
    }

    /// <summary>Every stream this computer can give a host: its own (<paramref name="own"/>) and the other computers' it holds.
    /// A host's own gateway lines are never sent back to it.</summary>
    private IEnumerable<string> Streams(string hostId, IReadOnlyDictionary<string, LogRecord[]> own) =>
        own.Keys.Concat(Logs.StreamNames().Where(s => !own.ContainsKey(s))).Where(s => s != OwnStream(hostId));

    /// <summary>Per stream, in order, the lines newer than the host's mark (a host that has nothing from a stream gets at most
    /// <see cref="FirstSendWindow"/> of it), at most one batch of each.</summary>
    private List<List<LogRecord>> Pending(string hostId, Peer peer, IReadOnlyDictionary<string, LogRecord[]> own, DateTimeOffset now)
    {
        var since = now - FirstSendWindow;
        var pending = new List<List<LogRecord>>();
        foreach (var stream in Streams(hostId, own))
        {
            var mark = peer.Mark(stream);
            var waiting = own.TryGetValue(stream, out var lines)
                ? lines.Where(r => r.Seq > mark && (mark > 0 || r.At >= since)).Take(LogBatch.MaximumEntries).ToList()
                : [.. Logs.After(stream, mark, since, LogBatch.MaximumEntries)];
            if (waiting.Count > 0) pending.Add(waiting);
        }
        return pending;
    }

    /// <summary>How many lines a host still lacks after this run.</summary>
    private int Waiting(string hostId, Peer peer, IReadOnlyDictionary<string, LogRecord[]> own, DateTimeOffset now)
    {
        var since = now - FirstSendWindow;
        var waiting = 0;
        foreach (var stream in Streams(hostId, own))
        {
            var mark = peer.Mark(stream);
            waiting += own.TryGetValue(stream, out var lines)
                ? lines.Count(r => r.Seq > mark && (mark > 0 || r.At >= since))
                : Logs.CountAfter(stream, mark, since);
        }
        return waiting;
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

    /// <summary>What this computer knows of one host: the store position read up to and the line read there, and, per stream,
    /// the newest line the host holds at least (<see cref="MarksKnown"/> once the host itself said so since the last failure
    /// or restart).</summary>
    private sealed class Peer
    {
        private readonly Dictionary<string, long> marks = new(StringComparer.Ordinal);
        internal long After;
        internal (string Stream, long Seq)? LastRead;
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

        /// <summary>The host's own marks, which can be older than the ones known here after it restarted without saving.</summary>
        internal void Replace(IEnumerable<LogMark> values)
        {
            marks.Clear();
            Raise(values);
        }
    }
}
