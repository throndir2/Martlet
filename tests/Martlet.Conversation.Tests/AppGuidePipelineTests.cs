using Martlet.Conversation.Guides;

namespace Martlet.Conversation.Tests;

public sealed class AppGuidePipelineTests
{
    private const string Page =
        "# Iron Ore\nIron Ore is a material used to forge weapons and armor at any anvil in the game world.\n" +
        "## Locations\nIron Ore can be mined in the Northern Caves and bought from the blacksmith in Riverwood for 7 gold.\n" +
        "## Uses\nSmelt two Iron Ore at a smelter to make one Iron Ingot, which is needed for steel swords.";

    [Fact]
    public void ChunksFollowHeadingsAndSearchFindsTheRightSection()
    {
        var chunks = GuideChunker.Chunk(new GuidePage("https://game.fandom.com/wiki/Iron_Ore", "Iron Ore", Page, 400));
        Assert.Equal(3, chunks.Count);
        Assert.Equal("Iron Ore › Locations", chunks[1].Section);

        var hits = GuideIndex.Build(chunks).Search("hey where can I mine iron ore?", 3);
        Assert.NotEmpty(hits);
        Assert.Contains("Northern Caves", hits[0].Chunk.Text);
        Assert.True(hits[0].Relevance >= GuideRecall.MinimumRelevance);
        var notes = GuideRecall.Notes("Skyrim", hits);
        Assert.StartsWith(GuideRecall.Label, notes);
        Assert.EndsWith(GuideRecall.EndLabel, notes);
        Assert.Empty(GuideIndex.Build(chunks).Search("what's for dinner tonight", 3));
    }

    [Fact]
    public async Task TheStoreKeepsGuidesAndTheirEntries()
    {
        var directory = Path.Combine(Path.GetTempPath(), "martlet-guides-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new FileAppGuideStore(directory);
            Assert.False((await store.LoadLibraryAsync(default)).On);
            var outcome = new GuideBuildOutcome([new GuidePage("https://game.fandom.com/wiki/Iron_Ore", "Iron Ore", Page, 400)],
                ["game.fandom.com"], 0, 400, null);
            var guide = GuideChunker.Guide(AppGuideKeys.Of("The Elder Scrolls V: Skyrim"), "The Elder Scrolls V: Skyrim", outcome,
                DateTimeOffset.UnixEpoch);
            Assert.Equal("the-elder-scrolls-v-skyrim", guide.Key);
            await store.SaveGuideAsync(guide, default);

            var entry = Assert.Single((await store.LoadLibraryAsync(default)).Apps);
            Assert.Equal((1, 3), (entry.Pages, entry.Chunks));
            Assert.Equal(3, (await store.LoadGuideAsync(guide.Key, default))!.Chunks.Count);

            await store.DeleteGuideAsync(guide.Key, default);
            Assert.Empty((await store.LoadLibraryAsync(default)).Apps);
            Assert.Null(await store.LoadGuideAsync(guide.Key, default));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task TheBuilderReadsTheWikiASearchFinds()
    {
        var web = new FixtureWeb();
        var outcome = await new WebGuideBuilder(web, web).BuildAsync(new("Skyrim", []), null, default);
        Assert.Null(outcome.Problem);
        Assert.Equal("Iron Ore", Assert.Single(outcome.Pages).Title);
        Assert.Equal(["game.fandom.com"], outcome.Sites);
    }

    private sealed class FixtureWeb : IWebSearch, IWebFetch
    {
        public Task<IReadOnlyList<WebSearchResult>> SearchAsync(string query, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<WebSearchResult>>([new("Skyrim Wiki", "https://game.fandom.com/wiki/Iron_Ore", "")]);

        public Task<WebPage> FetchAsync(Uri url, CancellationToken cancellationToken) =>
            Task.FromResult(new WebPage(url.AbsoluteUri, "Iron Ore", Page, 400));
    }
}
