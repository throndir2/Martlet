namespace Martlet.Memory.Tests;

/// <summary>Hidden search words, replaced facts kept as history and tidying (docs/MEMORY.md, "Search words, history and tidying").</summary>
public sealed class SearchWordsAndHistoryTests
{
    [Fact]
    public async Task SearchWordsFindAFactByWordsItDoesNotHoldAndNeverChangeItsTime()
    {
        using var scope = new TestScope();
        var clock = new ManualClock();
        using var store = scope.Open(clock);
        var fact = (await store.SaveAsync(MemoryFixtures.Save(clock, "The user is vegetarian."))).Fact;
        Assert.Empty((await store.RetrieveAsync(new() { Text = "cooking" })).Hits);

        clock.Advance(TimeSpan.FromMinutes(5));
        var receipt = await store.TidyAsync(new() { Words = [new(fact.Id, fact.Revision, "diet, food, meat, cooking")] });
        Assert.Equal(1, receipt.Worded);
        var worded = Assert.Single((await store.InspectAsync()).Facts);
        Assert.Equal("diet, food, meat, cooking", worded.SearchWords);
        Assert.Equal(fact.UpdatedAtUtc, worded.UpdatedAtUtc);
        Assert.Equal(fact.LastModifiedBy, worded.LastModifiedBy);
        Assert.Equal(2, worded.Revision);
        Assert.Equal(fact.Id, Assert.Single((await store.RetrieveAsync(new() { Text = "cooking" })).Hits).Fact.Id);

        // A stale revision is skipped, not failed.
        Assert.Equal(0, (await store.TidyAsync(new() { Words = [new(fact.Id, fact.Revision, "other, words")] })).Worded);
    }

    [Fact]
    public async Task SearchWordsDoNotMakeTwoDifferentFactsNearDuplicates()
    {
        using var scope = new TestScope();
        var clock = new ManualClock();
        using var store = scope.Open(clock);
        // Twenty shared search words would make the two facts look alike (Jaccard above 0.8) if near-duplicates compared them.
        var words = string.Join(", ", Enumerable.Range(0, 20).Select(i => "sharedword" + (char)('a' + i)));
        var cat = (await store.SaveAsync(MemoryFixtures.Save(clock, "Cat naps.") with { SearchWords = words })).Fact;
        clock.Advance(TimeSpan.FromMinutes(1));
        var dog = (await store.SaveAsync(MemoryFixtures.Save(clock, "Dog barks.") with { SearchWords = words })).Fact;
        var hits = (await store.RetrieveAsync(new() { Text = "sharedworda sharedwordb sharedwordc", MaximumResults = 5 })).Hits;
        Assert.Equal(2, hits.Count);
        // Equal matches stay equal apart from recency: neither counts as a near-duplicate of the other.
        Assert.True(hits[1].Score > hits[0].Score * 0.8, $"{hits[0].Score} {hits[1].Score}");
        Assert.Contains(hits, hit => hit.Fact.Id == cat.Id);
        Assert.Contains(hits, hit => hit.Fact.Id == dog.Id);
    }

    [Fact]
    public async Task ACorrectionKeepsTheEarlierVersionAsHistoryThatIsNeverRecalled()
    {
        using var scope = new TestScope();
        var clock = new ManualClock();
        using var store = scope.Open(clock);
        var fact = (await store.SaveAsync(MemoryFixtures.Save(clock, "The user lives in Portland.") with { SearchWords = "home, city" })).Fact;
        clock.Advance(TimeSpan.FromDays(3));
        var edited = await store.EditAsync(new()
        {
            Id = fact.Id, ExpectedRevision = fact.Revision, Content = "The user lives in Seattle.",
            Provenance = MemoryFixtures.Provenance(clock, MemorySourceKind.Conversation), Retention = fact.Retention,
            VoiceId = null, KeepHistory = true
        });
        Assert.Equal(fact.Id, edited.Fact.Id);
        Assert.Null(edited.Fact.SearchWords);
        var facts = (await store.InspectAsync()).Facts;
        Assert.Equal(2, facts.Count);
        var earlier = Assert.Single(facts, f => f.IsHistory);
        Assert.Equal("The user lives in Portland.", earlier.Content);
        Assert.Equal(fact.Id, earlier.ReplacedBy);
        Assert.Equal(clock.Utc, earlier.ReplacedAtUtc);
        Assert.Equal(fact.CreatedAtUtc, earlier.CreatedAtUtc);
        Assert.Equal("home, city", earlier.SearchWords);
        Assert.Equal(1, store.DerivedState.IndexedFacts);
        Assert.Empty((await store.RetrieveAsync(new() { Text = "Portland" })).Hits);
        Assert.Empty((await store.RetrieveAsync(new() { Text = "city" })).Hits);
        var snapshot = await store.SnapshotAsync();
        Assert.Equal(fact.Id, Assert.Single(snapshot.Live(clock.Utc)).Id);
        Assert.Empty(snapshot.Search(new() { Text = "Portland" }, clock.Utc));

        // History is kept as it was.
        await MemoryFixtures.FailureAsync(MemoryFailure.InvalidData, () => store.EditAsync(new()
        {
            Id = earlier.Id, ExpectedRevision = earlier.Revision, Content = "Changed.", Provenance = MemoryFixtures.Provenance(clock),
            Retention = earlier.Retention, VoiceId = null
        }));

        // An edit without KeepHistory, or one that keeps the words, adds no history.
        var reassigned = await store.EditAsync(new()
        {
            Id = fact.Id, ExpectedRevision = edited.Fact.Revision, Content = edited.Fact.Content,
            Provenance = MemoryFixtures.Provenance(clock), Retention = fact.Retention, VoiceId = "voice-1", KeepHistory = true
        });
        Assert.Equal(2, (await store.InspectAsync()).Facts.Count);
        Assert.Equal("voice-1", reassigned.Fact.VoiceId);
    }

    [Fact]
    public async Task ForgettingAFactForgetsItsEarlierVersionsUnlessAskedToKeepThem()
    {
        using var scope = new TestScope();
        var clock = new ManualClock();
        using var store = scope.Open(clock);
        async Task<MemoryFact> Corrected(string first, string then)
        {
            var fact = (await store.SaveAsync(MemoryFixtures.Save(clock, first))).Fact;
            clock.Advance(TimeSpan.FromMinutes(1));
            return (await store.EditAsync(new()
            {
                Id = fact.Id, ExpectedRevision = fact.Revision, Content = then, Provenance = MemoryFixtures.Provenance(clock),
                Retention = fact.Retention, VoiceId = null, KeepHistory = true
            })).Fact;
        }
        var one = await Corrected("The bike is red.", "The bike is blue.");
        var two = await Corrected("The car is old.", "The car is new.");
        var three = await Corrected("The boat is small.", "The boat is big.");
        Assert.Equal(6, (await store.InspectAsync()).Facts.Count);

        await store.DeleteAsync(new() { Id = one.Id, ExpectedRevision = one.Revision, ConsentId = Guid.NewGuid() });
        Assert.DoesNotContain((await store.InspectAsync()).Facts, f => f.Content.Contains("bike", StringComparison.Ordinal));

        await store.DeleteManyAsync([new() { Id = two.Id, ExpectedRevision = two.Revision, ConsentId = Guid.NewGuid() }]);
        Assert.DoesNotContain((await store.InspectAsync()).Facts, f => f.Content.Contains("car", StringComparison.Ordinal));

        await store.DeleteAsync(new() { Id = three.Id, ExpectedRevision = three.Revision, ConsentId = Guid.NewGuid(), KeepEarlierVersions = true });
        var left = Assert.Single((await store.InspectAsync()).Facts);
        Assert.Equal("The boat is small.", left.Content);
        Assert.True(left.IsHistory);
    }

    [Fact]
    public async Task TidyRetiresMergesAndMovesHistoryInOneCommit()
    {
        using var scope = new TestScope();
        var clock = new ManualClock();
        using var store = scope.Open(clock);
        var portland = (await store.SaveAsync(MemoryFixtures.Save(clock, "The user lives in Portland."))).Fact;
        clock.Advance(TimeSpan.FromDays(10));
        var seattle = (await store.SaveAsync(MemoryFixtures.Save(clock, "The user moved to Seattle.") with { SearchWords = "home, city" })).Fact;
        var veg = (await store.SaveAsync(MemoryFixtures.Save(clock, "The user is vegetarian.") with { SearchWords = "diet, food" })).Fact;
        var meat = (await store.SaveAsync(MemoryFixtures.Save(clock, "The user doesn't eat meat.") with { SearchWords = "diet, vegetarian" })).Fact;
        // The duplicate has history of its own, which moves to the fact it is merged into.
        clock.Advance(TimeSpan.FromMinutes(1));
        meat = (await store.EditAsync(new()
        {
            Id = meat.Id, ExpectedRevision = meat.Revision, Content = "The user never eats meat.", Provenance = MemoryFixtures.Provenance(clock),
            Retention = meat.Retention, VoiceId = null, SearchWords = "diet, vegetarian", KeepHistory = true
        })).Fact;
        var revision = (await store.InspectAsync()).StoreRevision;
        clock.Advance(TimeSpan.FromHours(1));

        var receipt = await store.TidyAsync(new()
        {
            Retirements = [new(portland.Id, portland.Revision, seattle.Id)],
            Merges = [new(veg.Id, veg.Revision, [(meat.Id, meat.Revision)], "The user is vegetarian and never eats meat.", "diet, food, vegetarian")]
        });
        Assert.Equal((1, 1, 0, revision + 1), (receipt.Retired, receipt.Merged, receipt.Worded, receipt.StoreRevision));
        var facts = (await store.InspectAsync()).Facts.ToDictionary(f => f.Id);
        Assert.True(facts[portland.Id].IsHistory);
        Assert.Equal(seattle.Id, facts[portland.Id].ReplacedBy);
        Assert.Equal(seattle.UpdatedAtUtc, facts[portland.Id].ReplacedAtUtc);
        Assert.Equal(clock.Utc, facts[portland.Id].UpdatedAtUtc);
        Assert.False(facts.ContainsKey(meat.Id));
        Assert.Equal("The user is vegetarian and never eats meat.", facts[veg.Id].Content);
        Assert.Equal("diet, food, vegetarian", facts[veg.Id].SearchWords);
        Assert.Equal(veg.LastModifiedBy, facts[veg.Id].LastModifiedBy);
        Assert.Equal(veg.Id, Assert.Single(facts.Values, f => f.Content == "The user doesn't eat meat.").ReplacedBy);
        Assert.Empty((await store.RetrieveAsync(new() { Text = "Portland" })).Hits);

        // Items about facts gone, history or changed since are skipped.
        var none = await store.TidyAsync(new()
        {
            Retirements = [new(portland.Id, facts[portland.Id].Revision, seattle.Id), new(veg.Id, veg.Revision, seattle.Id)],
            Merges = [new(seattle.Id, seattle.Revision, [(meat.Id, meat.Revision)])],
            Words = [new(portland.Id, facts[portland.Id].Revision, "old, words")]
        });
        Assert.Equal((0, 0, 0, receipt.StoreRevision), (none.Retired, none.Merged, none.Worded, none.StoreRevision));
    }

    [Fact]
    public async Task NewFieldsSurviveTheFileAndTravelAsJsonWhileAPlainFactIsWrittenAsBefore()
    {
        using var scope = new TestScope();
        var clock = new ManualClock();
        Guid id;
        using (var store = scope.Open(clock))
        {
            var fact = (await store.SaveAsync(MemoryFixtures.Save(clock, "The plain fact."))).Fact;
            id = fact.Id;
            var json = MemoryFactJson.Write(fact);
            Assert.DoesNotContain("search_words", json, StringComparison.Ordinal);
            Assert.DoesNotContain("replaced", json, StringComparison.Ordinal);
            clock.Advance(TimeSpan.FromMinutes(1));
            await store.EditAsync(new()
            {
                Id = fact.Id, ExpectedRevision = fact.Revision, Content = "The changed fact.", Provenance = MemoryFixtures.Provenance(clock),
                Retention = fact.Retention, VoiceId = null, SearchWords = "changed, plain", KeepHistory = true
            });
        }
        using (var store = scope.Open(clock))
        {
            var facts = (await store.InspectAsync()).Facts;
            var current = Assert.Single(facts, f => !f.IsHistory);
            var earlier = Assert.Single(facts, f => f.IsHistory);
            Assert.Equal("changed, plain", current.SearchWords);
            Assert.Equal(id, earlier.ReplacedBy);
            var json = MemoryFactJson.Write(earlier);
            Assert.Contains("\"replaced_at_utc\"", json, StringComparison.Ordinal);
            Assert.Contains("\"replaced_by\"", json, StringComparison.Ordinal);
            Assert.DoesNotContain("is_history", json, StringComparison.Ordinal);
            Assert.Equal(earlier, MemoryFactJson.Read(json));
            Assert.Equal(current, MemoryFactJson.Read(MemoryFactJson.Write(current)));
        }
    }

    [Theory]
    [InlineData("{\"search_words\":\"\"}")]
    [InlineData("{\"search_words\":\"a\\nb\"}")]
    [InlineData("{\"search_words\":\" spaced\"}")]
    [InlineData("{\"replaced_by\":\"6f1d2c1e-8d1f-4a8e-9c84-2a4dd2c9b7a1\"}")]
    [InlineData("{\"replaced_at_utc\":\"2026-01-01T00:00:00+00:00\"}")]
    public async Task InvalidNewFieldsAreRefused(string fields)
    {
        using var scope = new TestScope();
        var clock = new ManualClock();
        using var store = scope.Open(clock);
        var json = MemoryFactJson.Write((await store.SaveAsync(MemoryFixtures.Save(clock, "A fact."))).Fact);
        var extra = fields.Trim('{', '}');
        var broken = json[..^1] + "," + extra + "}";
        MemoryFixtures.Failure(MemoryFailure.InvalidData, () => MemoryFactJson.Read(broken));
    }
}
