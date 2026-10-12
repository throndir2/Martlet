using Martlet.Conversation.Guides;

namespace Martlet.Conversation.Tests;

/// <summary>Deeper research for App guides (docs/APP_GUIDES.md, "The brief" and "Researching where you are"): the brief a Thinking
/// pool member writes after a read, the brief with a message once, research on the area the screen shows, and research kept when
/// the wiki is read again. The pool member and the web are stand-ins.</summary>
public sealed class GuideResearchTests : IDisposable
{
    private const string Brief = "What it is: Skyrim is an open-world fantasy game.\nCore loop: Explore, fight and craft.\n" +
        "Early tips: Mine iron ore in the Ember Mines.";

    private const string CryptPage =
        "<html><head><title>Sunken Crypt | Skyrim Wiki</title></head><body><main><article><h1>Sunken Crypt</h1>" +
        "<p>The Sunken Crypt is a flooded dungeon under the old chapel in Skyrim. Many players find it the hardest early dungeon.</p>" +
        "<h2>Getting in</h2><p>Drain the Sunken Crypt by turning the three bronze valves in the chapel cellar; the water drops one " +
        "floor with each valve, and the last valve opens the gate to the lowest floor.</p><h2>Dangers</h2><p>Drowned knights guard " +
        "the lowest floor of the Sunken Crypt. They are weak to fire, so bring torches, and leave before midnight, when the water " +
        "rises again and the doors close behind you.</p></article></main></body></html>";

    private readonly string directory = Path.Combine(Path.GetTempPath(), "martlet-guide-research-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }

    [Fact]
    public void TheBriefIsPlainBoundedTextWithoutLabels()
    {
        Assert.Null(GuideBrief.Clean(null));
        Assert.Null(GuideBrief.Clean("NONE"));
        Assert.Null(GuideBrief.Clean("Too short."));
        var cleaned = GuideBrief.Clean("```\n## **What it is:** a game " + GuideBrief.Label + "\n\n" + new string('x', 80) + "\n```");
        Assert.NotNull(cleaned);
        Assert.StartsWith("What it is: a game", cleaned);
        Assert.DoesNotContain(GuideBrief.Label, cleaned);
        Assert.DoesNotContain("```", cleaned);
        var longer = GuideBrief.Clean(string.Join('\n', Enumerable.Repeat(new string('y', 99), 40)))!;
        Assert.True(longer.Length <= GuideBrief.MaximumCharacters);
        Assert.Equal("Stormveil Castle", GuideBrief.CleanFocus("  \"Stormveil\n Castle.\" "));
        Assert.Null(GuideBrief.CleanFocus(" ... "));
    }

    [Theory]
    [InlineData("AREA: **Frost Caves**", "Frost Caves")]
    [InlineData("Sure!\nAREA: Sunken Crypt\n", "Sunken Crypt")]
    [InlineData("AREA: NONE", null)]
    [InlineData("AREA: Skyrim", null)]
    [InlineData("The Frost Caves", null)]
    [InlineData(null, null)]
    public void TheAreaComesOnlyFromAnAreaLine(string? answer, string? area) =>
        Assert.Equal(area, GuideAreaResearch.ParseArea(answer, "Skyrim"));

    [Fact]
    public async Task AReadWritesTheBriefWhichGoesWithAMessageOnce()
    {
        var thinker = new Thinker();
        using var guides = await GuidesAsync();
        using var research = new AppGuideResearcher(guides, thinker, new Search(), new Documents(), wait: NoWait);
        Assert.True((await guides.BuildAsync("Skyrim", [], null)).Built);
        var entry = await WaitAsync(guides, research, e => e.Brief is not null);
        Assert.Equal(Brief, entry.Brief);
        Assert.NotNull(entry.BriefAt);
        var ask = Assert.Single(thinker.Asks);
        Assert.Equal(GuideBrief.Instructions, ask.Instructions);
        Assert.Contains("Ember Mines", ask.Text);

        guides.See("SKYRIM\u2122", "TESV", game: true, fullScreen: true);
        var first = guides.Recall("what should I do first?");
        Assert.Equal(Brief, first?.Brief);
        var notes = GuideRecall.BriefNotes("Skyrim", first!.Brief)!;
        Assert.StartsWith(GuideBrief.Label, notes);
        Assert.EndsWith(GuideBrief.EndLabel, notes);
        Assert.Null(guides.Recall("what should I do first?", notes)?.Brief);

        // Without a pool member that can think, nothing starts, and the old brief stays.
        thinker.CanThink = false;
        Assert.NotNull(research.Start(entry.Key, null));
        Assert.Equal(Brief, guides.Library.Apps.Single().Brief);
    }

    [Fact]
    public async Task AnAreaOnTheScreenIsResearchedOnTheWebAndKeptWhenTheWikiIsReadAgain()
    {
        var thinker = new Thinker();
        var documents = new Documents();
        using var guides = await GuidesAsync();
        using var research = new AppGuideResearcher(guides, thinker, new Search(), documents, wait: NoWait);
        Assert.True((await guides.BuildAsync("Skyrim", [], null)).Built);
        var key = (await WaitAsync(guides, research, e => e.Brief is not null)).Key;

        // The guide covers the Ember Mines: nothing is read on the web.
        var covered = await research.ResearchScreenAsync(key, "The user mines iron ore in the Ember Mines.");
        Assert.Equal(("Ember Mines", false, 0, (string?)null), (covered.Area, covered.Web, covered.Sections, covered.Problem));

        var crypt = await research.ResearchScreenAsync(key, "A dark flooded crypt; a drowned knight blocks the stairs.");
        Assert.Equal("Sunken Crypt", crypt.Area);
        Assert.True(crypt.Web);
        Assert.Null(crypt.Problem);
        Assert.True(crypt.Sections >= 2);
        Assert.DoesNotContain(documents.Read, url => url.Contains("reddit", StringComparison.Ordinal));
        var hits = guides.Search(key, "how do I drain the Sunken Crypt", 5)!;
        Assert.Contains(hits, h => GuideAreaResearch.IsResearched(h.Chunk) && h.Chunk.Section == "Summary");
        Assert.Contains(hits, h => h.Chunk.Section == "What people say on the web" && h.Chunk.Text.Contains("Reddit", StringComparison.Ordinal));

        // The same area again soon isn't researched again.
        var again = await research.ResearchScreenAsync(key, "Still in the flooded crypt.");
        Assert.False(again.Web);
        Assert.NotNull(again.Problem);

        // Read again: the research sections stay, the areas are remembered, and a new brief is written.
        var briefs = research.Briefs;
        Assert.True((await guides.BuildAsync("Skyrim", [], null)).Built);
        var entry = await WaitAsync(guides, research, _ => research.Briefs > briefs);
        Assert.Contains(guides.Search(key, "how do I drain the Sunken Crypt", 5)!, h => GuideAreaResearch.IsResearched(h.Chunk));
        Assert.Equal(["Ember Mines", "Sunken Crypt"], entry.Areas.Select(a => a.Name));
        Assert.True(entry.Areas[1].Sections >= 2);
    }

    private static Task NoWait(TimeSpan time, CancellationToken token) => Task.CompletedTask;

    private async Task<AppGuideService> GuidesAsync()
    {
        var guides = new AppGuideService(new FileAppGuideStore(directory), new Builder());
        await guides.LoadAsync();
        await guides.SetOnAsync(true);
        return guides;
    }

    private static async Task<AppGuideEntry> WaitAsync(AppGuideService guides, AppGuideResearcher research, Func<AppGuideEntry, bool> done)
    {
        for (var i = 0; i < 500; i++)
        {
            if (guides.Library.Apps.FirstOrDefault() is { } entry && done(entry) && research.Doing is null) return entry;
            await Task.Delay(20);
        }
        throw new TimeoutException("The research didn't finish.");
    }

    private sealed class Builder : IGuideBuilder
    {
        public Task<GuideBuildOutcome> BuildAsync(GuideBuildRequest request, IProgress<string>? progress, CancellationToken cancellationToken) =>
            Task.FromResult(new GuideBuildOutcome([new GuidePage("https://skyrim.fandom.com/wiki/Iron_Ore", "Iron Ore",
                "# Iron Ore\nIron Ore is a material used to forge weapons and armor at any anvil in Skyrim.\n## Locations\nIron Ore " +
                "can be mined in the Ember Mines below level 40 and bought from the blacksmith for 7 gold.", 400)],
                ["skyrim.fandom.com"], 0, 400, null));
    }

    private sealed class Thinker : IGuideThinker
    {
        private readonly List<GuideThinkingAsk> asks = [];
        public bool CanThink { get; set; } = true;
        internal IReadOnlyList<GuideThinkingAsk> Asks { get { lock (asks) return [.. asks]; } }

        public Task<(string? Text, string? Problem)> ThinkAsync(GuideThinkingAsk ask, CancellationToken cancellationToken)
        {
            lock (asks) asks.Add(ask);
            string answer = ask.Instructions switch
            {
                GuideBrief.Instructions => Brief,
                GuideAreaResearch.ClueInstructions when ask.Text.Contains("crypt", StringComparison.OrdinalIgnoreCase) => "AREA: Sunken Crypt",
                GuideAreaResearch.ClueInstructions when ask.Text.Contains("Ember", StringComparison.Ordinal) => "AREA: Ember Mines",
                GuideAreaResearch.ClueInstructions => "AREA: NONE",
                _ => "The Sunken Crypt is a flooded dungeon.\nTurn the three valves to drain it.\nBring torches for the drowned knights."
            };
            return Task.FromResult<(string?, string?)>((answer, null));
        }
    }

    private sealed class Search : IWebSearch
    {
        public Task<IReadOnlyList<WebSearchResult>> SearchAsync(string query, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<WebSearchResult>>(!query.Contains("Sunken Crypt", StringComparison.Ordinal) ? [] :
            [
                new("Sunken Crypt | Skyrim Wiki", "https://skyrim.fandom.com/wiki/Sunken_Crypt", "The Sunken Crypt is a flooded dungeon in Skyrim."),
                new("Sunken Crypt is impossible : r/skyrim", "https://www.reddit.com/r/skyrim/comments/abc/sunken_crypt/",
                    "Skyrim tip: in the Sunken Crypt, turn all three valves first and bring torches for the knights.")
            ]);
    }

    private sealed class Documents : IWebDocuments
    {
        private readonly List<string> read = [];
        internal IReadOnlyList<string> Read { get { lock (read) return [.. read]; } }

        public Task<WebDocument> ReadAsync(Uri url, int maxBytes, CancellationToken cancellationToken)
        {
            lock (read) read.Add(url.AbsoluteUri);
            return url.AbsolutePath == "/wiki/Sunken_Crypt"
                ? Task.FromResult(new WebDocument(url.AbsoluteUri, "text/html", CryptPage, CryptPage.Length, false))
                : throw new WebResearchException("not found");
        }
    }
}
