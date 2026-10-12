using Martlet.Core.Search;

namespace Martlet.Memory.Tests;

/// <summary>Finding by meaning in the memory store (docs/SEARCH.md, "Finding by meaning"): facts an embedding search found are fused
/// with the lexical order through the search engine. The embeddings are a fixture (NOT AI).</summary>
public sealed class MeaningFusionTests
{
    private static async Task<(MemoryStore Store, List<MemoryFact> Facts)> SaveAsync(TestScope scope, ManualClock clock, params string[] contents)
    {
        var store = scope.Open(clock);
        var facts = new List<MemoryFact>();
        foreach (var content in contents)
        {
            clock.Advance(TimeSpan.FromMinutes(1));
            facts.Add((await store.SaveAsync(new()
            {
                Content = content,
                Provenance = MemoryFixtures.Provenance(clock, MemorySourceKind.UserReviewedImport),
                Retention = MemoryRetention.UntilDeleted()
            })).Fact);
        }
        return (store, facts);
    }

    private static IReadOnlyList<Guid> Near(IReadOnlyList<MemoryFact> facts, string question)
    {
        var set = new VectorSet("fixture:concepts");
        foreach (var fact in facts) set.Put(fact.Id.ToString("N"), fact.Content, FixtureEmbedder.Embed(fact.Content));
        return [.. set.Nearest(FixtureEmbedder.Embed(question), 8).Where(n => n.Meaning >= 0.15).Select(n => Guid.ParseExact(n.Id, "N"))];
    }

    [Fact]
    public async Task AFactInOtherWordsIsFoundOnlyWithMeaningAndNamesStayFirst()
    {
        using var scope = new TestScope();
        var (store, facts) = await SaveAsync(scope, new ManualClock(), "The user is vegetarian.", "The user's dog is called Rex.",
            "The user lives in Seattle.", "The user likes the color green.", "The user was born in March.", "The user collects old maps.",
            "The user has a red bicycle.", "The user speaks some French.", "The user wears glasses.");
        using (store)
        {
            const string cook = "What should I cook tonight?";
            var words = MemoryQuery.TryFromBoundedSource(cook, 3)!;
            Assert.DoesNotContain((await store.RetrieveAsync(words)).Hits, h => h.Fact.Id == facts[0].Id);
            var fused = await store.RetrieveAsync(words with { Similar = Near(facts, cook) });
            Assert.Equal(facts[0].Id, fused.Hits[0].Fact.Id);
            Assert.Equal(0, fused.Hits[0].MatchedTerms);
            // The same words without meaning aren't served the fused answer from the cache.
            Assert.DoesNotContain((await store.RetrieveAsync(words)).Hits, h => h.Fact.Id == facts[0].Id);

            const string rex = "Is Rex hungry?";
            var named = await store.RetrieveAsync(MemoryQuery.TryFromBoundedSource(rex, 3)! with { Similar = Near(facts, rex) });
            Assert.Equal(facts[1].Id, named.Hits[0].Fact.Id);

            // IDs the store doesn't have are left out; the snapshot fuses the same way.
            var snapshot = await store.SnapshotAsync();
            var hits = snapshot.Search(words with { Similar = [Guid.NewGuid(), facts[0].Id] }, DateTimeOffset.UtcNow);
            Assert.Equal(facts[0].Id, hits[0].Fact.Id);
        }
    }

    [Fact]
    public async Task HistoryIsNeverFoundByMeaning()
    {
        using var scope = new TestScope();
        var clock = new ManualClock();
        var (store, facts) = await SaveAsync(scope, clock, "The user lives in Seattle.", "The user collects old maps.");
        using (store)
        {
            var old = facts[0];
            await store.EditAsync(new()
            {
                Id = old.Id, ExpectedRevision = old.Revision, Content = "The user lives in Portland.",
                Provenance = MemoryFixtures.Provenance(clock), Retention = old.Retention, VoiceId = old.VoiceId, KeepHistory = true
            });
            var history = (await store.InspectAsync()).Facts.Single(f => f.IsHistory);
            var found = await store.RetrieveAsync(MemoryQuery.TryFromBoundedSource("apartment neighborhood", 5)! with { Similar = [history.Id] });
            Assert.DoesNotContain(found.Hits, h => h.Fact.IsHistory);
        }
    }
}
