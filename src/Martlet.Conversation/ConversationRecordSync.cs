using System.Text.Json;
using System.Text.Json.Serialization;
using Martlet.Core.Contracts;
using Martlet.Core.Sync;

namespace Martlet.Conversation;

/// <summary>What one record sync did, counts only (never what was said): the months of the record, how many exchanges are
/// shared, how many it took from other computers, deleted because another computer deleted them, recorded as changes made
/// here, and how many need a newer Martlet; the hosts asked, how many hold every month the same as this computer, and why the
/// others couldn't be used; the first problem with this computer's own files (or null).</summary>
public sealed record ConversationSyncResult(int Months, int Exchanges, int Taken, int Deleted, int Recorded, int Unreadable, int Hosts,
    int Same, IReadOnlyDictionary<string, ConversationHostFailure> Failed, string? Problem, DateTimeOffset At)
{
    public int Down => Failed.Values.Count(f => f == ConversationHostFailure.Unreachable);
    public int Old => Failed.Values.Count(f => f == ConversationHostFailure.Old);
    public int Denied => Failed.Values.Count(f => f == ConversationHostFailure.Denied);
}

/// <summary>Keeps one account's record of conversations the same on every computer where the account is signed in, through the
/// paired hosts (docs/MEMORY.md, "One record on every computer"), month by month (<see cref="SharedConversations"/>). For each
/// month that changed here (its file's stamp) or on a host (its digest), it reads the month file, records the exchanges added,
/// edited or deleted here since the last sync (an exchange gone from the file is deleted everywhere), merges in the hosts'
/// copies, puts the newest version of every exchange into the file (deleting those deleted elsewhere) and gives hosts with
/// another copy the merged one. What it saw is kept in <c>sync\&lt;yyyy-MM&gt;.json</c> beside the month files: each
/// exchange's ID, revision, digest, writer and time, and the agreed deletions (what was said stays in the month files; only an
/// exchange a newer Martlet changed, which this one can't read, is kept there as it came). It never runs on a reply's
/// path: the record's files are held only while a month is read or changed.</summary>
public sealed class ConversationRecordSync
{
    public const string StateDirectoryName = "sync";
    public const string StatusFileName = "status.json";

    private readonly ConversationHistory history;
    private readonly string device;
    // What each month's state says, kept between syncs so an unchanged month costs one stamp and one digest comparison.
    private readonly Dictionary<string, MonthSummary> summaries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> empty = new(StringComparer.Ordinal);

    private sealed record MonthSummary(HistoryMonthStamp? Stamp, string Merged, int Exchanges);

    public ConversationRecordSync(ConversationHistory history, string device)
    {
        ArgumentNullException.ThrowIfNull(history);
        ContractRules.Identifier(device);
        this.history = history;
        this.device = device;
    }

    public ConversationHistory History => history;

    /// <summary>Where the sync keeps what it saw: <c>sync</c> beside the month files.</summary>
    public string StateDirectory => Path.Combine(history.Directory, StateDirectoryName);

    /// <summary>Syncs every month of the record with <paramref name="hosts"/> (the account's copies on each paired host).</summary>
    public async Task<ConversationSyncResult> RunAsync(IReadOnlyList<IConversationRecordHost> hosts, DateTimeOffset now, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(hosts);
        var failed = new Dictionary<string, ConversationHostFailure>(StringComparer.Ordinal);
        var indexes = new Dictionary<IConversationRecordHost, IReadOnlyDictionary<string, string>>();
        foreach (var host in hosts)
        {
            try { indexes[host] = await host.MonthsAsync(token).ConfigureAwait(false); }
            catch (ConversationHostException error) { failed[host.HostId] = error.Failure; }
        }
        var months = new SortedSet<string>(StringComparer.Ordinal);
        months.UnionWith(history.Months().Where(SharedConversations.IsMonth));
        months.UnionWith(StateMonths());
        foreach (var index in indexes.Values) months.UnionWith(index.Keys.Where(SharedConversations.IsMonth));

        int taken = 0, deleted = 0, recorded = 0, unreadable = 0;
        string? problem = null;
        var behind = new HashSet<string>(StringComparer.Ordinal);
        // The newest months first: what was said lately reaches the other computers first.
        foreach (var month in months.Reverse())
        {
            token.ThrowIfCancellationRequested();
            var summary = Summary(month);
            var stamp = history.Stamp(month);
            var differ = indexes.Where(pair => !failed.ContainsKey(pair.Key.HostId) && HostDigest(pair.Value, month) != summary.Merged).ToArray();
            if (HistoryMonthStamp.Unchanged(summary.Stamp, stamp) && differ.Length == 0) continue;
            try
            {
                var outcome = await SyncMonthAsync(month, differ, indexes, failed, now, token).ConfigureAwait(false);
                taken += outcome.Taken;
                deleted += outcome.Deleted;
                recorded += outcome.Recorded;
                unreadable += outcome.Unreadable;
                foreach (var host in outcome.Behind) behind.Add(host);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ContractException or JsonException)
            {
                problem ??= error.Message;
            }
        }
        var same = indexes.Keys.Count(host => !failed.ContainsKey(host.HostId) && !behind.Contains(host.HostId));
        var result = new ConversationSyncResult(months.Count, summaries.Values.Sum(s => s.Exchanges), taken, deleted, recorded, unreadable,
            hosts.Count, same, failed, problem, now);
        SaveStatus(result);
        return result;
    }

    private sealed record MonthOutcome(int Taken, int Deleted, int Recorded, int Unreadable, IReadOnlyList<string> Behind);

    private async Task<MonthOutcome> SyncMonthAsync(string month, IReadOnlyList<KeyValuePair<IConversationRecordHost, IReadOnlyDictionary<string, string>>> differ,
        IReadOnlyDictionary<IConversationRecordHost, IReadOnlyDictionary<string, string>> reachable, Dictionary<string, ConversationHostFailure> failed,
        DateTimeOffset now, CancellationToken token)
    {
        var state = MonthState.Load(StateDirectory, month);
        var read = await history.ReadMonthAsync(month, token).ConfigureAwait(false);

        // What this computer has: its exchanges (a version it didn't have at the last sync is a change made here), the agreed
        // deletions and the exchanges deleted here since then.
        var local = new Dictionary<Guid, (string Payload, HistoryExchange Exchange)>();
        foreach (var line in read.Lines)
        {
            string payload;
            try { payload = SharedConversations.Canonical(line.Line); }
            catch (ContractException) { continue; }
            local.TryAdd(line.Exchange.Id, (payload, line.Exchange));
        }
        var entries = new Dictionary<Guid, SharedExchange>();
        void Put(SharedExchange entry)
        {
            if (!entries.TryGetValue(entry.Id, out var existing) || !SharedConversations.Newer(existing, entry)) entries[entry.Id] = entry;
        }
        // Versions a newer Martlet wrote stay as they are: this computer's older line of the same exchange never replaces them.
        var kept = state.Kept.ToDictionary(entry => entry.Id);
        foreach (var entry in kept.Values) Put(entry);
        var recorded = 0;
        foreach (var (id, (payload, exchange)) in local)
        {
            if (kept.ContainsKey(id)) continue;
            var digest = SharedConversations.Sha256(payload);
            if (state.Observed.TryGetValue(id, out var seen) && seen.Digest == digest)
                Put(new() { Id = id, Revision = seen.Revision, UpdatedAt = seen.At, UpdatedBy = seen.By, Exchange = payload });
            else
            {
                recorded++;
                Put(seen is not null
                    ? new() { Id = id, Revision = seen.Revision + 1, UpdatedAt = (exchange.Edited ?? now).ToUniversalTime(), UpdatedBy = device, Exchange = payload }
                    : new() { Id = id, Revision = 1, UpdatedAt = exchange.At.ToUniversalTime(), UpdatedBy = device, Exchange = payload });
            }
        }
        foreach (var gone in state.Deleted) Put(gone);
        foreach (var (id, seen) in state.Observed)
        {
            if (local.ContainsKey(id)) continue;
            var tombstone = new SharedExchange { Id = id, Revision = seen.Revision + 1, UpdatedAt = now.ToUniversalTime(), UpdatedBy = device };
            if (entries.TryGetValue(id, out var existing) && !SharedConversations.Newer(tombstone, existing)) continue;
            Put(tombstone);
            recorded++;
        }
        var document = SharedConversations.Of(month, entries.Values);

        // The hosts' copies that differ from the last merge, each distinct copy read once (from the first host that answers).
        foreach (var group in differ.Where(pair => pair.Value.ContainsKey(month)).GroupBy(pair => pair.Value[month], StringComparer.Ordinal))
            foreach (var (host, _) in group)
            {
                if (failed.ContainsKey(host.HostId)) continue;
                try
                {
                    document = SharedConversations.Merge(document, await host.ReadAsync(month, token).ConfigureAwait(false));
                    break;
                }
                catch (ConversationHostException error) { failed[host.HostId] = error.Failure; }
            }

        // What this computer's file must change to hold the newest version of every exchange.
        var changes = new Dictionary<Guid, string?>();
        var keptNow = new List<SharedExchange>();
        foreach (var entry in document.Exchanges)
        {
            var have = local.TryGetValue(entry.Id, out var mine);
            if (entry.Deleted)
            {
                if (have) changes[entry.Id] = null;
                continue;
            }
            if (have && mine.Payload == entry.Exchange) continue;
            if (ConversationHistory.FromLine(entry.Exchange!) is not { } exchange || exchange.Id != entry.Id)
            {
                keptNow.Add(entry);
                continue;
            }
            changes[entry.Id] = entry.Exchange;
        }
        var keptIds = keptNow.Select(entry => entry.Id).ToHashSet();
        var after = read;
        if (changes.Count > 0)
        {
            await history.ApplyMonthAsync(month, changes, token).ConfigureAwait(false);
            after = await history.ReadMonthAsync(month, token).ConfigureAwait(false);
        }

        // What this computer holds now. An exchange edited or deleted here while the sync ran keeps the version the sync knew, so
        // the next sync records the change; an exchange added meanwhile isn't known yet. Either way the stamp isn't kept, so the
        // next sync reads the month again.
        var holds = new Dictionary<Guid, string>();
        foreach (var line in after.Lines)
        {
            try { holds.TryAdd(line.Exchange.Id, SharedConversations.Canonical(line.Line)); }
            catch (ContractException) { }
        }
        var observed = new Dictionary<Guid, Seen>();
        var exact = true;
        foreach (var entry in document.Live)
        {
            if (keptIds.Contains(entry.Id)) continue;
            var held = holds.TryGetValue(entry.Id, out var payload);
            if (!held && !local.ContainsKey(entry.Id) && !changes.ContainsKey(entry.Id)) continue;
            observed[entry.Id] = new(entry.Revision, SharedConversations.Sha256(entry.Exchange!), entry.UpdatedBy, entry.UpdatedAt);
            if (!held || payload != entry.Exchange) exact = false;
        }
        if (holds.Keys.Any(id => !observed.ContainsKey(id) && !keptIds.Contains(id))) exact = false;
        var stamp = changes.Count == 0 ? read.Stamp : exact ? after.Stamp : (HistoryMonthStamp?)null;
        var merged = document.Digest();
        new MonthState
        {
            SchemaVersion = MonthState.SchemaVersion1, Observed = observed, Deleted = [.. document.Deleted], Kept = keptNow, Length = stamp?.Length,
            Written = stamp?.WrittenUtc, Epoch = stamp?.Epoch, Writes = stamp?.Writes, Merged = merged, SyncedAt = now
        }.Save(StateDirectory, month);
        summaries[month] = new(stamp, merged, observed.Count);

        // Every host whose copy differs gets the merged one; one that answers with something newer is merged on the next sync.
        var behind = new List<string>();
        foreach (var (host, index) in reachable)
        {
            if (failed.ContainsKey(host.HostId)) continue;
            if (HostDigest(index, month) == merged) continue;
            try
            {
                var copy = await host.MergeAsync(document, token).ConfigureAwait(false);
                if (copy.Digest() != merged) behind.Add(host.HostId);
            }
            catch (ConversationHostException error) { failed[host.HostId] = error.Failure; }
        }
        return new(changes.Count(c => c.Value is not null), changes.Count(c => c.Value is null), recorded, keptNow.Count, behind);
    }

    /// <summary>A host's digest of <paramref name="month"/>: the empty month's when it keeps none.</summary>
    private string HostDigest(IReadOnlyDictionary<string, string> index, string month) =>
        index.TryGetValue(month, out var digest) ? digest : EmptyDigest(month);

    private string EmptyDigest(string month) =>
        empty.TryGetValue(month, out var digest) ? digest : empty[month] = SharedConversations.Empty(month).Digest();

    private MonthSummary Summary(string month)
    {
        if (summaries.TryGetValue(month, out var known)) return known;
        var state = MonthState.Load(StateDirectory, month);
        HistoryMonthStamp? stamp = state.Length is { } length && state.Written is { } written
            ? new(length, written, state.Epoch ?? default, state.Writes ?? 0) : null;
        return summaries[month] = new(stamp, state.Merged ?? EmptyDigest(month), state.Observed.Count);
    }

    private IEnumerable<string> StateMonths()
    {
        try
        {
            if (!Directory.Exists(StateDirectory)) return [];
            return Directory.EnumerateFiles(StateDirectory, "????-??.json").Select(Path.GetFileNameWithoutExtension).OfType<string>()
                .Where(SharedConversations.IsMonth).ToArray();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return []; }
    }

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        MaxDepth = 32
    };

    /// <summary>The last sync's counts (status.json beside the month states), for MCP's conversation_history_status.</summary>
    public sealed record Status(DateTimeOffset At, int Months, int Exchanges, int Hosts, int Same, int Down, int Old, int Denied, int Taken,
        int Deleted, int Recorded, int Unreadable, string? Problem);

    private void SaveStatus(ConversationSyncResult result)
    {
        try
        {
            Directory.CreateDirectory(StateDirectory);
            var status = new Status(result.At, result.Months, result.Exchanges, result.Hosts, result.Same, result.Down, result.Old, result.Denied,
                result.Taken, result.Deleted, result.Recorded, result.Unreadable, result.Problem);
            Write(Path.Combine(StateDirectory, StatusFileName), JsonSerializer.SerializeToUtf8Bytes(status, Json));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>The last sync's counts in <paramref name="recordDirectory"/>, or null when it never synced.</summary>
    public static Status? LoadStatus(string recordDirectory)
    {
        try
        {
            var path = Path.Combine(recordDirectory, StateDirectoryName, StatusFileName);
            if (!File.Exists(path) || new FileInfo(path).Length > 16_384) return null;
            return JsonSerializer.Deserialize<Status>(File.ReadAllBytes(path), Json);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or NotSupportedException) { return null; }
    }

    private static void Write(string path, byte[] bytes)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    internal sealed record Seen(long Revision, string Digest, string By, DateTimeOffset At);

    /// <summary>What the sync saw of one month (sync\&lt;yyyy-MM&gt;.json): each exchange's revision, digest, writer and time
    /// (no content), the agreed deletions, the versions a newer Martlet wrote that this one can't read (kept as they are, so this
    /// computer never writes its older line over them), the month file's stamp after the sync (null: read it again) and the
    /// merged copy's digest. An unreadable file starts empty: nothing is deleted because of it.</summary>
    internal sealed record MonthState
    {
        public const int SchemaVersion1 = 1;
        public required int SchemaVersion { get; init; }
        public required IReadOnlyDictionary<Guid, Seen> Observed { get; init; }
        public required IReadOnlyList<SharedExchange> Deleted { get; init; }
        public IReadOnlyList<SharedExchange> Kept { get; init; } = [];
        public long? Length { get; init; }
        public DateTime? Written { get; init; }
        public Guid? Epoch { get; init; }
        public int? Writes { get; init; }
        public string? Merged { get; init; }
        public DateTimeOffset? SyncedAt { get; init; }

        public static MonthState Empty { get; } = new() { SchemaVersion = SchemaVersion1, Observed = new Dictionary<Guid, Seen>(), Deleted = [] };

        public static MonthState Load(string directory, string month)
        {
            try
            {
                var path = Path.Combine(directory, month + ".json");
                if (!File.Exists(path)) return Empty;
                var state = JsonSerializer.Deserialize<MonthState>(File.ReadAllBytes(path), Json);
                if (state is not { SchemaVersion: SchemaVersion1, Observed: not null, Deleted: not null }) return Empty;
                return state with
                {
                    Deleted = [.. state.Deleted.Where(d => d is { Deleted: true } && d.Id != Guid.Empty && d.Revision > 0 && ContractRules.IsIdentifier(d.UpdatedBy))],
                    Kept = [.. (state.Kept ?? []).Where(k => k is { Deleted: false } && k.Id != Guid.Empty && k.Revision > 0 && ContractRules.IsIdentifier(k.UpdatedBy))]
                };
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or NotSupportedException or
                InvalidOperationException or ContractException)
            {
                return Empty;
            }
        }

        public void Save(string directory, string month)
        {
            Directory.CreateDirectory(directory);
            Write(Path.Combine(directory, month + ".json"), JsonSerializer.SerializeToUtf8Bytes(this with
            {
                Observed = new SortedDictionary<Guid, Seen>(Observed.ToDictionary()),
                Deleted = [.. Deleted.OrderBy(d => d.Id)]
            }, Json));
        }
    }
}
