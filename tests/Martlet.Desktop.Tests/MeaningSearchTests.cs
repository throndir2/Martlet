using System.Text.Json;
using Martlet.Conversation.Guides;
using Martlet.Core.Search;
using Martlet.Core.Settings;
using Martlet.Desktop;
using Martlet.Memory;
using Xunit;

namespace Martlet.Desktop.Tests;

/// <summary>Finding by meaning on the desktop (docs/SEARCH.md, "Finding by meaning"): the memory find tools and tidying memory fuse
/// facts close in meaning, and the service keeps its vectors in step, says why it can't be used and drops its vectors when it is
/// turned off. The embeddings are a fixture (NOT AI); the model's place is a fake.</summary>
public sealed class MeaningSearchTests : IDisposable
{
    private readonly string root = Path.Combine(AppContext.BaseDirectory, "meaning-search", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    private async Task<(MemoryStore Store, List<MemoryFact> Facts)> FactsAsync(params string[] contents)
    {
        var preview = MemoryStoreActivationPreview.Create(Path.Combine(root, "store"));
        var store = MemoryStore.Open(preview, preview.Authorize(MemoryConsentDecision.Allow));
        var facts = new List<MemoryFact>();
        foreach (var content in contents)
        {
            await Task.Delay(5);
            facts.Add((await store.SaveAsync(new()
            {
                Content = content, Provenance = MemoryProvenance.Conversation(Guid.NewGuid(), DateTimeOffset.UtcNow),
                Retention = MemoryRetention.UntilDeleted()
            })).Fact);
        }
        return (store, facts);
    }

    private static readonly string[] Everyday =
    [
        "The user is vegetarian.", "The user's dog is called Rex.", "The user lives in Seattle.", "The user likes the color green.",
        "The user was born in March.", "The user collects old maps.", "The user has a red bicycle.", "The user wears glasses.",
        "The user reads mystery novels."
    ];

    // A fake place: the fixture embedder, ready unless told why not.
    private sealed class Place
    {
        internal FixtureEmbedder Embedder { get; } = new();
        internal MeaningReadiness Readiness { get; set; } = new(null);
        internal int Checks { get; private set; }

        internal MeaningModel? Make(MeaningSearchFile choice) => choice.On
            ? new(Embedder, _ =>
            {
                Checks++;
                return Task.FromResult(Readiness);
            }, "in the test")
            : null;
    }

    [Fact]
    public async Task FindFusesFactsCloseInMeaningAndKeepsNamesFirst()
    {
        var (store, facts) = await FactsAsync(Everyday);
        using (store)
        {
            var words = JsonDocument.Parse(MemoryTools.Find(facts, "cook tonight", false, null, null).Result.Output.Split('\n')[0]);
            Assert.Equal(0, words.RootElement.GetProperty("count").GetInt32());
            var fused = JsonDocument.Parse(MemoryTools.Find(facts, "cook tonight", false, null, null, similar: [facts[0].Id]).Result.Output);
            Assert.Equal("The user is vegetarian.", fused.RootElement.GetProperty("facts")[0].GetProperty("fact").GetString());
            // A fact that names the word comes before one only close in meaning.
            var named = JsonDocument.Parse(MemoryTools.Find(facts, "Rex", false, null, null, similar: [facts[0].Id, facts[1].Id]).Result.Output);
            Assert.Equal("The user's dog is called Rex.", named.RootElement.GetProperty("facts")[0].GetProperty("fact").GetString());
            // An empty query still lists the newest; meaning only joins a search by words.
            var newest = JsonDocument.Parse(MemoryTools.Find(facts, null, false, null, null, similar: [facts[0].Id]).Result.Output);
            Assert.Equal(Everyday[^1], newest.RootElement.GetProperty("facts")[0].GetProperty("fact").GetString());
        }
    }

    [Fact]
    public async Task TidyingGroupsAFactWithOneCloseInMeaning()
    {
        var (store, facts) = await FactsAsync([.. Everyday, "The user moved to Portland last spring."]);
        using (store)
        {
            var snapshot = await store.SnapshotAsync();
            var seattle = facts[2];
            var portland = facts[^1];
            bool Grouped(MemoryUpkeepPlan plan) => plan.Groups.Any(g => g.Select(n => plan[n].Id).ToHashSet().SetEquals([seattle.Id, portland.Id]));
            Assert.False(Grouped(MemoryUpkeep.Plan(snapshot, DateTimeOffset.UtcNow, null)));
            var plan = MemoryUpkeep.Plan(snapshot, DateTimeOffset.UtcNow, null,
                similar: fact => fact.Id == seattle.Id ? [portland.Id] : fact.Id == portland.Id ? [seattle.Id] : []);
            Assert.True(Grouped(plan));
        }
    }

    [Fact]
    public async Task TheServiceKeepsVectorsInStepAndSaysWhyItCannotBeUsed()
    {
        var (store, facts) = await FactsAsync(Everyday);
        using (store)
        {
            var place = new Place();
            using var meaning = new MeaningSearch(Path.Combine(root, "data"), () => false, place.Make);
            Assert.StartsWith("Checking nomic-embed-text", meaning.Status, StringComparison.Ordinal);
            IReadOnlyList<MemoryFact> current = facts;
            meaning.SyncMemory(_ => Task.FromResult<IReadOnlyList<MemoryFact>?>(current));
            await meaning.IdleAsync();
            Assert.True(meaning.Ready);
            Assert.Equal(Everyday.Length, meaning.MemoryVectors);
            Assert.Contains($"{Everyday.Length} memories", meaning.Status, StringComparison.Ordinal);
            var question = await meaning.QueryAsync("What should I cook tonight?", CancellationToken.None);
            Assert.Equal(facts[0].Id, meaning.Facts(question!)[0]);

            // A forgotten fact's vector is dropped at the next refresh.
            current = facts.Skip(1).ToArray();
            meaning.SyncMemory(_ => Task.FromResult<IReadOnlyList<MemoryFact>?>(current));
            await meaning.IdleAsync();
            Assert.Equal(Everyday.Length - 1, meaning.MemoryVectors);
            Assert.DoesNotContain(facts[0].Id, meaning.Facts(question!));

            // A guide's sections are embedded in the background and kept beside the guides.
            GuideChunk[] chunks =
            [
                new("u", "Blacksmith", "Blacksmith › Upgrades", "Raise a sword's damage at the forge."),
                new("u", "Fishing", "Fishing", "Cast your line at dawn near the pier.")
            ];
            var weapons = (await meaning.QueryAsync("make my weapons stronger", CancellationToken.None))!;
            Assert.Empty(meaning.GuideSections("skyrim", chunks, weapons));
            await meaning.IdleAsync();
            Assert.Equal(0, meaning.GuideSections("skyrim", chunks, weapons)[0].Chunk);
            var files = Path.Combine(root, "data", "meaning", "guides");
            Assert.Single(Directory.GetFiles(files, "*.vectors"));
            meaning.KeepGuides(new HashSet<string>());
            Assert.Empty(Directory.GetFiles(files, "*.vectors"));

            // Not downloaded: the page offers Download and the search uses words only.
            place.Readiness = new("nomic-embed-text isn't downloaded", Missing: true);
            await meaning.CheckAsync(CancellationToken.None, force: true);
            Assert.True(meaning.NeedsDownload);
            Assert.Contains("isn't downloaded", meaning.Status, StringComparison.Ordinal);
            Assert.Null(await meaning.QueryAsync("cook", CancellationToken.None));
            place.Readiness = new("Ollama has 3 models loaded, and loading nomic-embed-text could make it unload one of them", Hold: true);
            await meaning.CheckAsync(CancellationToken.None, force: true);
            Assert.StartsWith("Waiting: Ollama has 3 models loaded", meaning.Status, StringComparison.Ordinal);

            // Off drops every vector and the choice is saved for the other computers.
            Assert.True(meaning.Choose(meaning.Choice with { Place = MeaningSearchFile.Off }));
            Assert.Equal(MeaningSearchFile.Off, MeaningSearchFile.Load(Path.Combine(root, "data")).Place);
            Assert.Equal(0, meaning.MemoryVectors);
            Assert.StartsWith("Off:", meaning.Status, StringComparison.Ordinal);
            Assert.Null(await meaning.QueryAsync("cook", CancellationToken.None));
        }
    }

    [Fact]
    public async Task BackgroundEmbeddingWaitsWhileARepliesRuns()
    {
        var (store, facts) = await FactsAsync(Everyday);
        using (store)
        {
            var place = new Place();
            var busy = true;
            using var meaning = new MeaningSearch(Path.Combine(root, "busy"), () => Volatile.Read(ref busy), place.Make);
            meaning.SyncMemory(_ => Task.FromResult<IReadOnlyList<MemoryFact>?>(facts));
            await Task.Delay(300);
            Assert.Equal(0, place.Embedder.Embedded);
            Volatile.Write(ref busy, false);
            await meaning.IdleAsync();
            Assert.Equal(Everyday.Length, meaning.MemoryVectors);
        }
    }
}
