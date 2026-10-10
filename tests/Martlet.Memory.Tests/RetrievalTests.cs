using System.Text.Json;
using System.Text.Json.Serialization;

namespace Martlet.Memory.Tests;

public sealed class RetrievalTests
{
    private sealed record RetrievalFixture(
        [property: JsonPropertyName("facts")] FixtureFact[] Facts,
        [property: JsonPropertyName("queries")] FixtureQuery[] Queries);
    private sealed record FixtureFact(
        [property: JsonPropertyName("key")] string Key,
        [property: JsonPropertyName("content")] string Content);
    private sealed record FixtureQuery(
        [property: JsonPropertyName("text")] string Text,
        [property: JsonPropertyName("expected_first")] string ExpectedFirst);

    [Fact]
    public async Task HeldOutSyntheticCorpusReturnsExpectedFactWithProvenance()
    {
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "retrieval-held-out.json");
        var fixture = JsonSerializer.Deserialize<RetrievalFixture>(await File.ReadAllBytesAsync(fixturePath));
        Assert.NotNull(fixture);
        using var scope = new TestScope();
        var clock = new ManualClock();
        using var store = scope.Open(clock);
        var ids = new Dictionary<string, Guid>(StringComparer.Ordinal);
        var provenance = new Dictionary<string, MemoryProvenance>(StringComparer.Ordinal);

        foreach (var item in fixture!.Facts)
        {
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

        foreach (var query in fixture.Queries)
        {
            var result = await store.RetrieveAsync(new() { Text = query.Text, MaximumResults = 3 });
            var first = Assert.IsType<MemoryRetrievalHit>(result.Hits.FirstOrDefault());
            Assert.Equal(ids[query.ExpectedFirst], first.Fact.Id);
            Assert.Equal(provenance[query.ExpectedFirst], first.Fact.CreatedFrom);
            Assert.True(first.Score > 0);
            Assert.True(first.MatchedTerms > 0);
            Assert.InRange(result.Hits.Count, 1, 3);
        }
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
