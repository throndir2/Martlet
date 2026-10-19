using Martlet.Memory;
using Xunit;

namespace Martlet.Memory.Tests;

/// <summary>Memories for a game: each fact can say where it matters (everywhere, any game, one game), and recall leaves out the
/// facts of other activities (docs/MEMORY.md, "Memories for a game").</summary>
public sealed class MemoryActivityTests
{
    private const string Palworld = "gaming/Palworld";
    private const string Elden = "gaming/ELDEN RING";

    [Fact]
    public void RulesSayWhereAFactApplies()
    {
        Assert.True(MemoryActivity.Applies(null, null));
        Assert.True(MemoryActivity.Applies(null, Palworld));
        Assert.True(MemoryActivity.Applies(Palworld, "gaming/palworld"));
        Assert.False(MemoryActivity.Applies(Palworld, Elden));
        Assert.False(MemoryActivity.Applies(Palworld, null));
        Assert.True(MemoryActivity.Applies(MemoryActivity.Gaming, Elden));
        Assert.True(MemoryActivity.Applies(MemoryActivity.Gaming, MemoryActivity.Gaming));
        Assert.False(MemoryActivity.Applies(MemoryActivity.Gaming, null));
        Assert.False(MemoryActivity.Applies(Elden, MemoryActivity.Gaming));

        Assert.Equal(Palworld, MemoryActivity.Game("  Palworld\t"));
        Assert.Equal("gaming/Fate/Grand Order", MemoryActivity.Game("Fate/Grand  Order"));
        Assert.Equal("Fate/Grand Order", MemoryActivity.Name("gaming/Fate/Grand Order"));
        Assert.Null(MemoryActivity.Game("  ~~ "));
        Assert.Equal(MemoryActivity.MaximumCharacters, MemoryActivity.Game(new string('x', 400))!.Length);
        Assert.True(MemoryActivity.Same(Elden, "gaming/elden  ring"));
        Assert.False(MemoryActivity.Same(Elden, null));

        Assert.True(MemoryActivity.Named(Palworld, "what's my favorite PALWORLD pal?"));
        Assert.True(MemoryActivity.Named(Elden, "back to elden ring tonight"));
        Assert.True(MemoryActivity.Named(Elden, "eldenring is hard"));
        Assert.False(MemoryActivity.Named(Elden, "the ring of elden"));
        Assert.False(MemoryActivity.Named(MemoryActivity.Gaming, "gaming"));

        Assert.Equal("everywhere", MemoryActivity.Describe(null));
        Assert.Equal("while gaming", MemoryActivity.Describe(MemoryActivity.Gaming));
        Assert.Equal("while playing Palworld", MemoryActivity.Describe(Palworld));
    }

    [Fact]
    public async Task RecallLeavesOutOtherGamesFacts()
    {
        using var scope = new TestScope();
        var clock = new ManualClock();
        var store = scope.Open(clock);
        using var opened = store;
        var chillet = (await Save(store, clock, "In Palworld, the user's favorite pal is Chillet.", Palworld)).Fact;
        var katana = (await Save(store, clock, "In Elden Ring, the user's favorite weapon is the katana.", Elden)).Fact;
        var coop = (await Save(store, clock, "The user's favorite games are co-op games.", MemoryActivity.Gaming)).Fact;
        var cat = (await Save(store, clock, "The user's favorite animal is their cat Miso.", null)).Fact;

        async Task<Guid[]> Recall(string text, string? now) =>
            [.. (await store.RetrieveAsync(MemoryQuery.TryFromBoundedSource(text, 10)! with { Activity = new(now) })).Hits.Select(h => h.Fact.Id)];

        Assert.Equal(new[] { chillet.Id, coop.Id, cat.Id }.Order(), (await Recall("favorite", Palworld)).Order());
        Assert.Equal(new[] { katana.Id, coop.Id, cat.Id }.Order(), (await Recall("favorite", Elden)).Order());
        Assert.Equal([cat.Id], await Recall("favorite", null));
        // Naming the game finds its facts while another game (or none) is played.
        Assert.Contains(chillet.Id, await Recall("favorite palworld pal", Elden));
        Assert.DoesNotContain(katana.Id, await Recall("favorite palworld pal", null));
        // Without a filter every fact is found, as before.
        Assert.Equal(4, (await store.RetrieveAsync(MemoryQuery.TryFromBoundedSource("favorite", 10)!)).Hits.Count);
        // The query cache keeps each activity apart.
        Assert.DoesNotContain(chillet.Id, await Recall("favorite", Elden));
        Assert.Contains(chillet.Id, await Recall("favorite", Palworld));

        var snapshot = await store.SnapshotAsync();
        Assert.Equal(new[] { katana.Id, coop.Id, cat.Id }.Order(),
            snapshot.Search(MemoryQuery.TryFromBoundedSource("favorite", 10)! with { Activity = new(Elden) }, clock.Utc)
                .Select(h => h.Fact.Id).Order());
    }

    [Fact]
    public async Task ActivityTravelsAndEditsKeepIt()
    {
        using var scope = new TestScope();
        var clock = new ManualClock();
        var store = scope.Open(clock);
        using var opened = store;
        var game = (await Save(store, clock, "In Palworld, the user's base is by the lake.", Palworld)).Fact;
        var plain = (await Save(store, clock, "The user likes tea.", null)).Fact;
        Assert.Contains("\"activity\":\"gaming/Palworld\"", MemoryFactJson.Write(game), StringComparison.Ordinal);
        Assert.DoesNotContain("activity", MemoryFactJson.Write(plain), StringComparison.Ordinal);
        Assert.Equal(Palworld, MemoryFactJson.Read(MemoryFactJson.Write(game)).Activity);

        var corrected = (await store.EditAsync(new()
        {
            Id = game.Id, ExpectedRevision = game.Revision, Content = "In Palworld, the user's base is on the cliff.",
            Provenance = MemoryFixtures.Provenance(clock, MemorySourceKind.Conversation), Retention = game.Retention,
            VoiceId = null, KeepHistory = true
        })).Fact;
        Assert.Equal(Palworld, corrected.Activity);
        Assert.Equal(Palworld, (await store.InspectAsync()).Facts.Single(f => f.IsHistory).Activity);
        var moved = (await store.EditAsync(new()
        {
            Id = corrected.Id, ExpectedRevision = corrected.Revision, Content = corrected.Content,
            Provenance = MemoryFixtures.Provenance(clock), Retention = corrected.Retention,
            VoiceId = null, SetActivity = true, Activity = null
        })).Fact;
        Assert.Null(moved.Activity);

        // Reopened, the store reads the activity back.
        store.Dispose();
        using var again = scope.Open(clock);
        Assert.Single((await again.InspectAsync()).Facts, f => f.Activity == Palworld);
    }

    [Theory]
    [InlineData("Gaming/Palworld")]
    [InlineData("gaming/ Palworld")]
    [InlineData("gaming/")]
    [InlineData("game play")]
    [InlineData("gaming/a\nb")]
    [InlineData("")]
    public async Task AnActivityOfTheWrongFormIsRefused(string activity)
    {
        using var scope = new TestScope();
        var clock = new ManualClock();
        var store = scope.Open(clock);
        using var opened = store;
        await Assert.ThrowsAsync<MemoryException>(() => Save(store, clock, "A fact.", activity));
    }

    private static Task<MemoryMutationReceipt> Save(MemoryStore store, ManualClock clock, string content, string? activity) => store.SaveAsync(new()
    {
        Content = content,
        Provenance = MemoryFixtures.Provenance(clock, MemorySourceKind.Conversation),
        Retention = MemoryRetention.UntilDeleted(),
        Activity = activity
    });
}
