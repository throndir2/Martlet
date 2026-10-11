using System.Text.Json;
using System.Text.Json.Serialization;
using Martlet.Core.Contracts;

namespace Martlet.Core.Sync;

/// <summary>One fact in this computer's memory store: its ID, revision, last change and its JSON (Martlet.Memory's fact).</summary>
public sealed record LocalMemory(Guid Id, long Revision, DateTimeOffset UpdatedAt, string Fact);

/// <summary>This computer's memory store as the sync sees it: which store (a new memory folder is a new store) and its facts.</summary>
public sealed record LocalMemories(Guid StoreId, IReadOnlyList<LocalMemory> Facts);

/// <summary>What the sync needs to know about a fact: whether it has expired and whether the owner typed it (a full store makes
/// room by forgetting the oldest fact picked out of a conversation, never one the owner typed).</summary>
public sealed record MemoryFactInfo(bool Expired, bool Typed);

/// <summary>The facts to put into this computer's store (each the newest version every computer agrees on) and to forget.</summary>
public sealed record MemoryChanges(IReadOnlyList<string> Facts, IReadOnlyCollection<Guid> Forget);

/// <summary>What one memory sync did: the merged copy to give the hosts, how many facts it took from other computers and forgot
/// because another computer forgot them, how many changes made here it recorded, how many facts need a newer Martlet, why
/// it couldn't change this computer's store (or null), and how many facts didn't fit one store (<see cref="MemorySyncNode.StoreCapacity"/>)
/// when every computer's facts were put together.</summary>
public sealed record MemorySyncResult(SharedMemories Document, int Taken, int Forgot, int Recorded, int Unreadable, string? Problem, int Full = 0);

/// <summary>What this computer remembers about its memory store between syncs (memory-sync.json in Martlet's data folder, no
/// fact content): which store it is, the revision and digest of each fact it had at the last sync and which computer wrote
/// that version, and the forgotten facts every computer agreed on, so a fact deleted here (even while sync was off) is
/// forgotten everywhere, and one forgotten elsewhere never comes back from here.</summary>
public sealed record MemorySyncState
{
    public const string FileName = "memory-sync.json";
    public const int SchemaVersion1 = 1;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        RespectNullableAnnotations = true,
        MaxDepth = 16
    };

    public sealed record Seen(long Revision, string Digest, string By);

    public required int SchemaVersion { get; init; }
    public Guid StoreId { get; init; }
    public DateTimeOffset? SyncedAt { get; init; }
    public required IReadOnlyDictionary<Guid, Seen> Observed { get; init; }
    public required IReadOnlyList<SharedMemory> Forgotten { get; init; }

    public static MemorySyncState Empty { get; } = new() { SchemaVersion = SchemaVersion1, Observed = new Dictionary<Guid, Seen>(), Forgotten = [] };

    /// <summary>This computer's state; an unreadable file starts empty (nothing is forgotten because of it).</summary>
    public static MemorySyncState Load(string directory)
    {
        try
        {
            var bytes = File.ReadAllBytes(Path.Combine(directory, FileName));
            var state = JsonSerializer.Deserialize<MemorySyncState>(bytes, Json);
            if (state is not { SchemaVersion: SchemaVersion1, Observed: not null, Forgotten: not null }) return Empty;
            var forgotten = state.Forgotten.Where(f => f is { Forgotten: true } && f.Id != Guid.Empty && f.Revision > 0 &&
                ContractRules.IsIdentifier(f.UpdatedBy)).ToArray();
            return state with { Forgotten = forgotten };
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or NotSupportedException or
            InvalidOperationException or ContractException)
        {
            return Empty;
        }
    }

    public void Save(string directory)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, FileName);
        var temporary = Path.Combine(directory, $"memory-sync.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(this with
            {
                Observed = new SortedDictionary<Guid, Seen>(Observed.ToDictionary()),
                Forgotten = Forgotten.OrderBy(f => f.Id).ToArray()
            }, Json));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}

/// <summary>Keeps this computer's memory store the same as every other computer's. Each sync it reads the store, records the
/// facts saved, edited or forgotten here since the last sync (a fact gone from the store is forgotten everywhere), merges in the
/// hosts' copies, forgets facts that expired or don't fit (the oldest facts picked out of conversations go first), and makes
/// the store hold exactly the newest version of every fact. The store is read and changed only through the two delegates,
/// which the caller runs while it owns the store.</summary>
public sealed class MemorySyncNode
{
    /// <summary>The most facts one memory store holds (Martlet.Memory's MemoryLimits.MaximumFacts).</summary>
    public const int StoreCapacity = 100_000;

    private readonly string directory;
    private readonly string device;
    private readonly int capacity;

    /// <param name="capacity">The most facts a store holds: <see cref="StoreCapacity"/>, or less to rehearse a full store.</param>
    public MemorySyncNode(string directory, string device, int capacity = StoreCapacity)
    {
        ContractRules.Identifier(device);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(capacity, StoreCapacity);
        this.directory = directory;
        this.device = device;
        this.capacity = capacity;
    }

    /// <summary>The merged copy from the last sync (with every fact), ready to give the hosts.</summary>
    public SharedMemories Document { get; private set; } = SharedMemories.Empty;

    /// <param name="describe">What a fact's JSON says (expired, typed by the owner), or null when this Martlet can't read it.</param>
    public async Task<MemorySyncResult> SyncAsync(IEnumerable<SharedMemories> copies, Func<CancellationToken, Task<LocalMemories>> read,
        Func<MemoryChanges, CancellationToken, Task> apply, Func<string, MemoryFactInfo?> describe, DateTimeOffset now, CancellationToken token)
    {
        var state = MemorySyncState.Load(directory);
        var local = await read(token).ConfigureAwait(false);
        var observed = state.StoreId == local.StoreId ? state.Observed : new Dictionary<Guid, MemorySyncState.Seen>();
        var info = new Dictionary<string, MemoryFactInfo?>(StringComparer.Ordinal);
        MemoryFactInfo? Describe(string fact) => info.TryGetValue(fact, out var known) ? known : info[fact] = describe(fact);

        // What this computer has: its facts (a version it didn't have at the last sync is a change made here) and the deletions
        // made here since then. Gathered per fact ID, then made one document, so a large store costs one pass.
        var entries = new Dictionary<Guid, SharedMemory>();
        void Put(SharedMemory entry)
        {
            if (!entries.TryGetValue(entry.Id, out var existing) || !SharedMemories.Newer(existing, entry)) entries[entry.Id] = entry;
        }
        var recorded = 0;
        var mine = new Dictionary<Guid, (long Revision, string Fact)>();
        foreach (var fact in local.Facts)
        {
            var canonical = SharedMemories.Canonical(fact.Fact);
            if (Describe(canonical) is { Expired: true }) continue;
            mine[fact.Id] = (fact.Revision, canonical);
            var digest = SharedMemories.Sha256(canonical);
            var known = observed.TryGetValue(fact.Id, out var seen) && seen.Revision == fact.Revision && seen.Digest == digest;
            if (!known) recorded++;
            Put(new()
            {
                Id = fact.Id, Revision = fact.Revision, UpdatedAt = fact.UpdatedAt.ToUniversalTime(), UpdatedBy = known ? seen!.By : device,
                Fact = canonical
            });
        }
        foreach (var gone in state.Forgotten) Put(gone);
        foreach (var (id, seen) in observed)
        {
            if (mine.ContainsKey(id)) continue;
            var forget = Tombstone(id, seen.Revision, now);
            if (entries.TryGetValue(id, out var existing) && !SharedMemories.Newer(forget, existing)) continue;
            Put(forget);
            recorded++;
        }
        var document = SharedMemories.Of(entries.Values);

        foreach (var copy in copies) document = SharedMemories.Merge(document, copy);

        // Facts that expired, then (when every computer's facts together don't fit one store) the oldest facts picked out of
        // conversations, are forgotten everywhere. Those that didn't fit are counted, so the owner is told.
        var merged = document.Facts.ToDictionary(f => f.Id);
        foreach (var fact in document.Live)
            if (Describe(fact.Fact!) is { Expired: true })
                merged[fact.Id] = Tombstone(fact.Id, fact.Revision, now);
        var live = merged.Values.Where(f => !f.Forgotten).ToArray();
        var full = 0;
        if (live.Length > capacity)
            foreach (var fact in live.Where(f => Describe(f.Fact!) is { Typed: false }).OrderBy(f => f.UpdatedAt).ThenBy(f => f.Id)
                .Take(live.Length - capacity).ToArray())
            {
                merged[fact.Id] = Tombstone(fact.Id, fact.Revision, now);
                full++;
            }
        document = SharedMemories.Of(merged.Values);

        // What this computer's store must change to hold the newest version of every fact.
        var upserts = new List<SharedMemory>();
        var forgetHere = new List<Guid>();
        var unreadable = 0;
        foreach (var entry in document.Facts)
        {
            if (entry.Forgotten)
            {
                if (mine.ContainsKey(entry.Id)) forgetHere.Add(entry.Id);
                continue;
            }
            if (mine.TryGetValue(entry.Id, out var have) && have.Revision == entry.Revision && have.Fact == entry.Fact) continue;
            if (Describe(entry.Fact!) is null)
            {
                unreadable++;
                continue;
            }
            upserts.Add(entry);
        }
        // A store holds capacity facts: when the owner typed more than that across computers, the newest fit.
        var room = capacity - (mine.Count - forgetHere.Count) - upserts.Count(u => !mine.ContainsKey(u.Id));
        if (room < 0)
        {
            var dropped = upserts.Where(u => !mine.ContainsKey(u.Id)).OrderBy(u => u.UpdatedAt).ThenBy(u => u.Id).Take(-room).ToHashSet();
            upserts.RemoveAll(dropped.Contains);
            full += dropped.Count;
        }

        string? problem = null;
        var after = local;
        if (upserts.Count > 0 || forgetHere.Count > 0)
        {
            try
            {
                await apply(new([.. upserts.Select(u => u.Fact!)], forgetHere), token).ConfigureAwait(false);
                after = await read(token).ConfigureAwait(false);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                problem = error.Message;
            }
        }

        var seenNow = new Dictionary<Guid, MemorySyncState.Seen>();
        var final = document.Facts.ToDictionary(f => f.Id);
        foreach (var fact in after.Facts)
        {
            var canonical = SharedMemories.Canonical(fact.Fact);
            if (Describe(canonical) is { Expired: true }) continue;
            var entry = final.GetValueOrDefault(fact.Id);
            var by = entry is { Forgotten: false } && entry.Revision == fact.Revision && entry.Fact == canonical ? entry.UpdatedBy : device;
            seenNow[fact.Id] = new(fact.Revision, SharedMemories.Sha256(canonical), by);
        }
        new MemorySyncState
        {
            SchemaVersion = MemorySyncState.SchemaVersion1, StoreId = after.StoreId, SyncedAt = now, Observed = seenNow,
            Forgotten = [.. document.Forgotten]
        }.Save(directory);
        Document = document;
        return new(document, problem is null ? upserts.Count : 0, problem is null ? forgetHere.Count : 0, recorded, unreadable, problem, full);
    }

    private SharedMemory Tombstone(Guid id, long revision, DateTimeOffset now) => new()
    {
        Id = id, Revision = revision + 1, UpdatedAt = now.ToUniversalTime(), UpdatedBy = device, Fact = null
    };
}
