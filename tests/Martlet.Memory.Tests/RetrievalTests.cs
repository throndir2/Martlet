using System.Text.Json;
using System.Text.Json.Serialization;
using Xunit.Abstractions;

namespace Martlet.Memory.Tests;

public sealed class RetrievalTests(ITestOutputHelper output)
{
    private sealed record RetrievalFixture(
        [property: JsonPropertyName("facts")] FixtureFact[] Facts,
        [property: JsonPropertyName("queries")] FixtureQuery[] Queries);
    private sealed record FixtureFact(
        [property: JsonPropertyName("key")] string Key,
        [property: JsonPropertyName("content")] string Content);
    private sealed record FixtureQuery(
        [property: JsonPropertyName("text")] string Text,
        [property: JsonPropertyName("expected_first")] string ExpectedFirst,
        [property: JsonPropertyName("kind")] string? Kind);

    private static async Task<RetrievalFixture> LoadFixtureAsync()
    {
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "retrieval-held-out.json");
        var fixture = JsonSerializer.Deserialize<RetrievalFixture>(await File.ReadAllBytesAsync(fixturePath));
        Assert.NotNull(fixture);
        return fixture!;
    }

    /// <summary>Saves the fixture's facts one minute apart (so ties never fall to random fact IDs).</summary>
    private static async Task<(Dictionary<string, Guid> Ids, Dictionary<string, MemoryProvenance> Provenance)> SaveFixtureAsync(
        MemoryStore store, ManualClock clock, RetrievalFixture fixture)
    {
        var ids = new Dictionary<string, Guid>(StringComparer.Ordinal);
        var provenance = new Dictionary<string, MemoryProvenance>(StringComparer.Ordinal);
        foreach (var item in fixture.Facts)
        {
            clock.Advance(TimeSpan.FromMinutes(1));
            var source = MemoryFixtures.Provenance(clock, MemorySourceKind.UserReviewedImport);
            var saved = await store.SaveAsync(new()
            {
                Content = item.Content,
                Provenance = source,
                Retention = MemoryRetention.UntilDeleted()
            });
            ids.Add(item.Key, saved.Fact.Id);
            provenance.Add(item.Key, source);
        }
        return (ids, provenance);
    }

    [Fact]
    public async Task HeldOutSyntheticCorpusReturnsExpectedFactWithProvenance()
    {
        var fixture = await LoadFixtureAsync();
        using var scope = new TestScope();
        var clock = new ManualClock();
        using var store = scope.Open(clock);
        var (ids, provenance) = await SaveFixtureAsync(store, clock, fixture);

        foreach (var query in fixture.Queries)
        {
            var result = await store.RetrieveAsync(new() { Text = query.Text, MaximumResults = 3 });
            var first = Assert.IsType<MemoryRetrievalHit>(result.Hits.FirstOrDefault());
            Assert.True(ids[query.ExpectedFirst] == first.Fact.Id,
                $"\"{query.Text}\" ranked \"{first.Fact.Content}\" first, not {query.ExpectedFirst}");
            Assert.Equal(provenance[query.ExpectedFirst], first.Fact.CreatedFrom);
            Assert.True(first.Score > 0);
            Assert.True(first.MatchedTerms > 0);
            Assert.InRange(result.Hits.Count, 1, 3);
        }
    }

    /// <summary>Recall@k (the expected fact is in the first k) and mean reciprocal rank over the held-out fixture, written to the
    /// test output (run with a detailed console logger to see them) for the PR's before and after numbers.</summary>
    [Fact]
    public async Task HeldOutFixtureRecallAndMeanReciprocalRank()
    {
        var fixture = await LoadFixtureAsync();
        using var scope = new TestScope();
        var clock = new ManualClock();
        using var store = scope.Open(clock);
        var (ids, _) = await SaveFixtureAsync(store, clock, fixture);

        var ranks = new List<(FixtureQuery Query, int Rank)>();
        foreach (var query in fixture.Queries)
        {
            var hits = (await store.RetrieveAsync(new() { Text = query.Text, MaximumResults = MemoryLimits.MaximumResults })).Hits;
            var index = hits.Select(hit => hit.Fact.Id).ToList().IndexOf(ids[query.ExpectedFirst]);
            ranks.Add((query, index < 0 ? 0 : index + 1));
            output.WriteLine($"rank {(index < 0 ? "-" : (index + 1).ToString())} [{query.Kind}] {query.Text}");
        }

        string Line(string name, IReadOnlyList<int> found) =>
            $"{name}: queries {found.Count}, recall@1 {Recall(found, 1):0.00}, recall@3 {Recall(found, 3):0.00}, " +
            $"recall@5 {Recall(found, 5):0.00}, MRR {found.Average(rank => rank == 0 ? 0 : 1d / rank):0.000}";
        static double Recall(IReadOnlyList<int> found, int k) => found.Count(rank => rank is > 0 && rank <= k) / (double)found.Count;
        var all = ranks.Select(r => r.Rank).ToArray();
        output.WriteLine(Line("all", all));
        foreach (var kind in ranks.GroupBy(r => r.Query.Kind ?? "-"))
            output.WriteLine(Line(kind.Key, kind.Select(r => r.Rank).ToArray()));

        Assert.Equal(1d, Recall(all, 3));
        Assert.True(Recall(all, 1) >= 0.95, Line("all", all));
    }

    [Fact]
    public async Task RetrievalIsBoundedDeterministicAndNeverReturnsUnmatchedFacts()
    {
        using var scope = new TestScope();
        var clock = new ManualClock();
        using var store = scope.Open(clock);
        var alpha = await store.SaveAsync(MemoryFixtures.Save(clock, "alpha common common"));
        clock.Advance(TimeSpan.FromMinutes(1));
        var beta = await store.SaveAsync(MemoryFixtures.Save(clock, "beta common"));

        var first = await store.RetrieveAsync(new() { Text = "alpha common", MaximumResults = 1 });
        var repeated = await store.RetrieveAsync(new() { Text = "alpha common", MaximumResults = 1 });
        Assert.Equal(alpha.Fact.Id, Assert.Single(first.Hits).Fact.Id);
        Assert.Equal(first.Hits.Select(hit => hit.Fact.Id), repeated.Hits.Select(hit => hit.Fact.Id));
        Assert.Empty((await store.RetrieveAsync(new() { Text = "unmatchedtoken" })).Hits);
        Assert.DoesNotContain(beta.Fact.Id, first.Hits.Select(hit => hit.Fact.Id));
        Assert.InRange(store.DerivedState.CachedQueries, 1, MemoryLimits.MaximumCachedQueries);
    }

    [Fact]
    public async Task TiedRankingUsesRecencyBeforeTruncationAcrossResultLimits()
    {
        using var scope = new TestScope();
        var clock = new ManualClock();
        using var store = scope.Open(clock);
        var first = await store.SaveAsync(MemoryFixtures.Save(clock, "shared rankingmarker"));
        var second = await store.SaveAsync(MemoryFixtures.Save(clock, "shared rankingmarker"));
        var laterId = new[] { first.Fact, second.Fact }.MaxBy(fact => fact.Id)!;
        clock.Advance(TimeSpan.FromMinutes(1));
        await store.EditAsync(new()
        {
            Id = laterId.Id,
            ExpectedRevision = laterId.Revision,
            Content = laterId.Content,
            Provenance = MemoryFixtures.Provenance(clock),
            Retention = MemoryRetention.UntilDeleted(),
            VoiceId = null
        });

        var single = await store.RetrieveAsync(new() { Text = "rankingmarker", MaximumResults = 1 });
        var both = await store.RetrieveAsync(new() { Text = "rankingmarker", MaximumResults = 2 });
        var cached = await store.RetrieveAsync(new() { Text = "rankingmarker", MaximumResults = 1 });

        Assert.Equal(laterId.Id, Assert.Single(single.Hits).Fact.Id);
        Assert.Equal(both.Hits.Take(1), single.Hits);
        Assert.Equal(single.Hits, cached.Hits);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExpiryDuringRetrievalInvalidatesCacheMissAndHit(bool warmCache)
    {
        using var scope = new TestScope();
        var clock = new ManualClock();
        var expireDuringQuery = false;
        var hooks = new MemoryTestHooks
        {
            Query = (point, _) =>
            {
                if (expireDuringQuery && point == MemoryQueryPoint.BeforeRevisionCheck)
                    clock.Advance(TimeSpan.FromMinutes(6));
            }
        };
        using var store = scope.Open(clock, hooks);
        await store.SaveAsync(MemoryFixtures.Save(clock, "ephemeral retrievalmarker",
            MemoryRetention.ExpiringAt(clock.Utc + TimeSpan.FromMinutes(5))));
        var query = new MemoryQuery { Text = "retrievalmarker" };
        if (warmCache)
            Assert.Single((await store.RetrieveAsync(query)).Hits);
        expireDuringQuery = true;

        await MemoryFixtures.FailureAsync(MemoryFailure.QueryInvalidated,
            async () => await store.RetrieveAsync(query));

        Assert.Equal(0, store.DerivedState.IndexedFacts);
        Assert.Equal(0, store.DerivedState.CachedQueries);
        Assert.False(store.DerivedIndexContains("retrievalmarker"));
        Assert.DoesNotContain("retrievalmarker", await File.ReadAllTextAsync(scope.StorePath),
            StringComparison.Ordinal);
        Assert.Empty((await store.InspectAsync()).Facts);
    }

    [Fact]
    public async Task SnapshotSearchesLikeTheStoreAfterItClosesAndSkipsExpiredFacts()
    {
        using var scope = new TestScope();
        var clock = new ManualClock();
        MemorySnapshot snapshot;
        IReadOnlyList<MemoryRetrievalHit> fromStore;
        Guid lasting;
        using (var store = scope.Open(clock))
        {
            lasting = (await store.SaveAsync(MemoryFixtures.Save(clock, "The snapshotmarker garden has roses."))).Fact.Id;
            await store.SaveAsync(MemoryFixtures.Save(clock, "A short snapshotmarker note.",
                MemoryRetention.ExpiringAt(clock.Utc + TimeSpan.FromMinutes(5))));
            await store.SaveAsync(MemoryFixtures.Save(clock, "Nothing to find here."));
            fromStore = (await store.RetrieveAsync(new() { Text = "snapshotmarker roses" })).Hits;
            snapshot = await store.SnapshotAsync();
            Assert.Equal((await store.InspectAsync()).StoreRevision, snapshot.StoreRevision);
        }

        var query = new MemoryQuery { Text = "snapshotmarker roses" };
        Assert.Equal(3, snapshot.Facts.Count);
        Assert.Equal(fromStore.Select(hit => (hit.Fact.Id, hit.Score, hit.MatchedTerms)),
            snapshot.Search(query, clock.Utc).Select(hit => (hit.Fact.Id, hit.Score, hit.MatchedTerms)));
        clock.Advance(TimeSpan.FromMinutes(6));
        Assert.Equal(lasting, Assert.Single(snapshot.Search(query, clock.Utc)).Fact.Id);
        Assert.Equal(2, snapshot.Live(clock.Utc).Count());
        Assert.Empty(MemorySnapshot.Empty.Search(query, clock.Utc));
    }

    [Fact]
    public async Task ConcurrentIdenticalCacheMissesShareOneSafeEntry()
    {
        using var scope = new TestScope();
        var clock = new ManualClock();
        using var entered = new CountdownEvent(2);
        using var release = new ManualResetEventSlim();
        var hooks = new MemoryTestHooks
        {
            Query = (point, _) =>
            {
                if (point != MemoryQueryPoint.BeforeRevisionCheck)
                    return;
                entered.Signal();
                Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
            }
        };
        using var store = scope.Open(clock, hooks);
        var saved = await store.SaveAsync(MemoryFixtures.Save(clock, "shared cachemarker"));

        var first = Task.Run(() => store.RetrieveAsync(new() { Text = "cachemarker" }));
        var second = Task.Run(() => store.RetrieveAsync(new() { Text = "cachemarker" }));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
        release.Set();
        var results = await Task.WhenAll(first, second);

        Assert.All(results, result => Assert.Equal(saved.Fact.Id, Assert.Single(result.Hits).Fact.Id));
        Assert.Equal(1, store.DerivedState.CachedQueries);
    }
}
