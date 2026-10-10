namespace Martlet.Memory;

/// <summary>A read-only copy of a memory store's facts at one revision, with the store's own lexical index, so the facts can be
/// searched without opening the store again. It holds no lock and never changes: take a new one (<see cref="MemoryStore.SnapshotAsync"/>)
/// after the store changed. Desktop keeps one for each memory space it reads but doesn't write in a turn, so recall searches
/// several spaces as fast as it searches one.</summary>
public sealed class MemorySnapshot
{
    private readonly IReadOnlyDictionary<Guid, MemoryFact> byId;
    private readonly LexicalIndex index;

    internal MemorySnapshot(Guid storeId, long storeRevision, IReadOnlyDictionary<Guid, MemoryFact> facts, LexicalIndex index)
    {
        StoreId = storeId;
        StoreRevision = storeRevision;
        byId = facts;
        this.index = index;
        Facts = Array.AsReadOnly(facts.Values.OrderBy(fact => fact.CreatedAtUtc).ThenBy(fact => fact.Id).ToArray());
    }

    /// <summary>An empty copy (a space with no store yet).</summary>
    public static MemorySnapshot Empty { get; } =
        new(Guid.Empty, 0, new Dictionary<Guid, MemoryFact>(), LexicalIndex.Build([]));

    public Guid StoreId { get; }
    public long StoreRevision { get; }

    /// <summary>Every fact of the copy, oldest first, as <see cref="MemoryStore.InspectAsync"/> lists them. A fact may have
    /// expired since the copy was taken; <see cref="Live"/> leaves those out.</summary>
    public IReadOnlyList<MemoryFact> Facts { get; }

    /// <summary>The facts that have not expired at <paramref name="now"/>.</summary>
    public IEnumerable<MemoryFact> Live(DateTimeOffset now) => Facts.Where(fact => !fact.Retention.IsExpired(now));

    /// <summary>The best lexical matches for <paramref name="query"/>, ranked as <see cref="MemoryStore.RetrieveAsync"/> ranks
    /// them; facts that expired at <paramref name="now"/> are left out.</summary>
    public IReadOnlyList<MemoryRetrievalHit> Search(MemoryQuery query, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        MemoryGuard.Require(query is not null);
        var terms = LexicalIndex.QueryTerms(query!.Text);
        MemoryGuard.Require(query.MaximumResults is >= 1 and <= MemoryLimits.MaximumResults);
        if (byId.Count == 0)
            return [];
        // Ask for every match: some may have expired, and the copy is small (a store holds at most 512 facts).
        return index.Search(terms, MemoryLimits.MaximumResults, cancellationToken)
            .Select(match => byId[match.FactId] is var fact && !fact.Retention.IsExpired(now)
                ? new MemoryRetrievalHit(fact, match.Score, match.MatchedTerms) : null)
            .OfType<MemoryRetrievalHit>()
            .Take(query.MaximumResults)
            .ToArray();
    }

    public override string ToString() => $"Memory snapshot r{StoreRevision} ({Facts.Count} facts)";
}
