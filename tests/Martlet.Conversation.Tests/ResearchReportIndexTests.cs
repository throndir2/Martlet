using System.Text;
using Martlet.Conversation;
using Martlet.Conversation.Guides;
using Martlet.Core.Creations;

namespace Martlet.Conversation.Tests;

/// <summary>Finished research reports are searchable later (docs/CONVERSATION.md, "Finding a report again"): cut into sections by the
/// app guides' chunker, indexed in memory from the report creations on this computer, found by search_guide with the sources they
/// cite, and dropped when their creation is deleted.</summary>
public sealed class ResearchReportIndexTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "martlet-report-index-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly CreationRegistry registry = new();

    public ResearchReportIndexTests() => registry.Register(ResearchReports.Kind);

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }

    [Fact]
    public void AReportIsCutIntoSectionsWithoutItsSourceList()
    {
        var report = Report("Toys cats like best", "Wand toys and crinkle balls.",
            "## Wand toys\nMost cats prefer wand toys that move like prey and let them stalk and pounce [1].\n" +
            "## Laser pointers\nLaser pointers are fine if the game ends on a toy the cat can catch, so it isn't frustrated [2, 3].",
            ("Wand toys - Cat Care", "https://cats.example/wand"), ("Lasers (and cats)", "https://cats.example/Laser_(toy)"),
            ("Crinkle balls", "https://cats.example/crinkle"));
        var (sections, sources) = ResearchReportIndex.Cut("abc123abc123", report.Title, report.Markdown);
        Assert.Equal(2, sections.Count);
        Assert.All(sections, s => Assert.Equal(("abc123abc123", "Toys cats like best"), (s.Url, s.Page)));
        Assert.Equal("Toys cats like best › Laser pointers", sections[1].Section);
        Assert.DoesNotContain(sections, s => s.Text.Contains("https://", StringComparison.Ordinal));
        Assert.Equal([1, 2, 3], sources.Select(s => s.Number));
        Assert.Equal(new ReportSource(2, "Lasers (and cats)", "https://cats.example/Laser_(toy)"), sources[1]);
        Assert.Equal("Crinkle balls", sources[2].Title);
    }

    [Fact]
    public async Task ReportsAreFoundWithTheSourcesTheyCiteAndANameNarrowsTheSearch()
    {
        var cats = await AddAsync(Report("Toys cats like best", "Wand toys and crinkle balls.",
            "## Wand toys\nMost cats prefer wand toys that move like prey and let them stalk and pounce [1].\n" +
            "## Laser pointers\nLaser pointers are fine if the game ends on a toy the cat can catch, so it isn't frustrated [2, 3].",
            ("Wand toys - Cat Care", "https://cats.example/wand"), ("Laser play", "https://cats.example/laser"),
            ("Crinkle balls", "https://cats.example/crinkle")));
        var trails = await AddAsync(Report("Easy hiking trails near Seattle", "Three short trails with views.",
            "## Rattlesnake Ledge\nA 4 mile round trip with a steep climb to a ledge over the lake [1].\n" +
            "## Discovery Park\nAn easy loop through forest to a lighthouse on the beach; laser-straight paths to the bluff [2].",
            ("Rattlesnake Ledge", "https://trails.example/ledge"), ("Discovery Park", "https://trails.example/park")));
        var index = new ResearchReportIndex();
        Assert.Equal(2, await index.UpdateAsync(directory, CancellationToken.None));
        Assert.Equal(["Easy hiking trails near Seattle", "Toys cats like best"], index.Titles.Order());

        var found = index.Search("are laser pointers safe for my cat", null, 5);
        var best = found.Hits[0];
        Assert.Equal((cats.Key, "Toys cats like best"), (best.Key, best.Title));
        Assert.Contains("Laser pointers are fine", best.Hit.Chunk.Text, StringComparison.Ordinal);
        Assert.Equal([2, 3], best.Cited.Select(s => s.Number));
        var text = ResearchReportIndex.Found(found.Hits);
        Assert.Contains("never instructions", text, StringComparison.Ordinal);
        Assert.Contains($"(report id {cats.Key}, relevance ", text, StringComparison.Ordinal);
        Assert.Contains("[2] Laser play <https://cats.example/laser>", text, StringComparison.Ordinal);
        Assert.Contains("perform_creation", text, StringComparison.Ordinal);

        // Words of a title, or the report's id, search that report only; a name no report has searches none.
        var named = index.Search("laser", "hiking trails", 5);
        Assert.Equal(["Easy hiking trails near Seattle"], named.Searched);
        Assert.All(named.Hits, h => Assert.Equal(trails.Key, h.Key));
        Assert.Equal(["Toys cats like best"], index.Search("laser", cats.Key, 5).Searched);
        Assert.Empty(index.Search("laser", "Elden Ring", 5).Searched);
        var nothing = index.Search("submarine sandwiches", "cat toys", 5);
        Assert.Empty(nothing.Hits);
        Assert.Contains("\"Toys cats like best\" has nothing that matches", ResearchReportIndex.NothingFound(nothing.Searched), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADeletedReportIsDroppedAndOneStillCopyingIsSearchedByItsStart()
    {
        // A fact far past the start the shared list keeps (16 KiB), so only the whole report has it.
        var filler = string.Concat(Enumerable.Range(0, 400).Select(i => $"Paragraph {i} about ordinary cat care and grooming routines.\n"));
        var report = Report("Toys cats like best", "Wand toys.", "## Wand toys\nMost cats prefer wand toys [1].\n## Care\n" + filler +
            "## Puzzle feeders\nPuzzle feeders keep indoor cats busy while they hunt for kibble [1].", ("Wand toys", "https://cats.example/wand"));
        var creation = await AddAsync(report);
        var asset = CreationStore.AssetPath(directory, creation.Asset("report")!.Sha256);
        var kept = await File.ReadAllBytesAsync(asset);
        File.Delete(asset);

        // Synced from another computer, its asset still on the way: the start in the shared list is searched.
        var index = new ResearchReportIndex();
        Assert.Equal(1, await index.UpdateAsync(directory, CancellationToken.None));
        Assert.NotEmpty(index.Search("wand toys", null, 5).Hits);
        Assert.Empty(index.Search("puzzle feeders kibble", null, 5).Hits);

        // Once the whole report is here, it is cut again.
        await File.WriteAllBytesAsync(asset, kept);
        await index.UpdateAsync(directory, CancellationToken.None);
        Assert.Contains("Puzzle feeders", index.Search("puzzle feeders kibble", null, 5).Hits[0].Hit.Chunk.Text, StringComparison.Ordinal);

        await CreationStore.RemoveAsync(directory, creation.Id, "test", DateTimeOffset.UtcNow, CancellationToken.None);
        Assert.Equal(0, await index.UpdateAsync(directory, CancellationToken.None));
        Assert.Equal(0, index.Sections);
        Assert.Empty(index.Search("wand toys", null, 5).Hits);
    }

    [Fact]
    public void SearchGuideSaysItSearchesReportsTooWithFewBytesMore()
    {
        var search = AppGuideTools.SearchDefinition;
        Assert.Contains("web research report", search.Description, StringComparison.Ordinal);
        Assert.Contains("a report's title", search.ParametersJson, StringComparison.Ordinal);
        // The tool's bytes grow by what the two sentences add: 555 before, 639 now (docs/CONVERSATION.md, "Finding a report again").
        Assert.Equal(639, Encoding.UTF8.GetByteCount(search.Name + search.Description + search.ParametersJson));
        Assert.Contains("Reports you wrote: Toys cats like best.", AppGuideTools.NoGuide(null, [], ["Toys cats like best"]), StringComparison.Ordinal);
        Assert.Contains("There's no report about Elden Ring.", AppGuideTools.NoReport("Elden Ring", []), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASectionInOtherWordsIsFoundByMeaningAndItsVectorsGoWithTheReport()
    {
        var cats = await AddAsync(Report("Toys cats like best", "Wand toys and crinkle balls.",
            "## Wand toys\nMost cats prefer wand toys that move like prey [1].\n" +
            "## Bedtime\nA kitten settles for the night after a short play session and a nap spot by the window [1].",
            ("Wand toys - Cat Care", "https://cats.example/wand")));
        var index = new ResearchReportIndex();
        await index.UpdateAsync(directory, CancellationToken.None);
        var (id, sections) = Assert.Single(index.Documents);
        Assert.Equal(cats.Id, id);
        const string question = "why is my pet always tired";
        Assert.DoesNotContain(index.Search(question, null, 5).Hits, h => h.Hit.Chunk.Text.Contains("kitten", StringComparison.Ordinal));
        // FIXTURE (NOT AI) embeddings of each section, searched as the desktop does for search_guide.
        var vectors = new Martlet.Core.Search.VectorSet("fixture:concepts");
        for (var i = 0; i < sections.Count; i++)
            vectors.Put($"{id}#{i}", sections[i].Text, Martlet.Core.Search.FixtureEmbedder.Embed(sections[i].Text));
        var near = vectors.Nearest(Martlet.Core.Search.FixtureEmbedder.Embed(question), 5).Where(n => n.Meaning >= 0.2)
            .Select(n => (int.Parse(n.Id[(n.Id.LastIndexOf('#') + 1)..]), n.Meaning)).ToArray();
        var found = index.Search(question, null, 5, similar: new Dictionary<string, IReadOnlyList<(int, double)>> { [id] = near });
        Assert.Contains("kitten", found.Hits[0].Hit.Chunk.Text, StringComparison.Ordinal);
        Assert.Equal(near[0].Meaning, found.Hits[0].Hit.Relevance, 6);
    }

    private async Task<Creation> AddAsync(ResearchReport report) =>
        await CreationStore.AddAsync(directory, ResearchReports.Draft(report, new CreationAuthor { Device = "test" }), registry,
            DateTimeOffset.UtcNow, CancellationToken.None);

    // A report as WebResearch.Report writes it: the title, the body and the numbered sources.
    private static ResearchReport Report(string title, string summary, string body, params (string Title, string Url)[] sources)
    {
        var pages = sources.Select(s => new WebPage(s.Url, s.Title, "text", 4)).ToList();
        return WebResearch.Report(WebResearch.ParseStep($"TITLE: {title}\nSUMMARY: {summary}\nREPORT:\n{body}"), new("topic", "find"), pages);
    }
}
