using Martlet.Conversation.Guides;
using Martlet.Core.Settings;

namespace Martlet.Conversation.Tests;

/// <summary>The desktop's guide library (Companion › App guides): matching the program in front, the offer rule, reading up into a
/// searchable guide, the notes on a message and the tools' texts.</summary>
public sealed class AppGuideServiceTests : IDisposable
{
    private const string Page =
        "# Iron Ore\nIron Ore is a material used to forge weapons and armor at any anvil in the game world.\n" +
        "## Locations\nIron Ore can be mined in the Ember Mines below level 40 and bought from the blacksmith for 7 gold.\n" +
        "## Uses\nSmelt two Iron Ore at a smelter to make one Iron Ingot, which is needed for steel swords.";

    private readonly string directory = Path.Combine(Path.GetTempPath(), "martlet-guide-service-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }

    private AppGuideService Service(IGuideBuilder? builder = null) => new(new FileAppGuideStore(directory), builder ?? new FixtureBuilder());

    [Fact]
    public void ProgramNamesMatchTheirAppIgnoringCaseAndPunctuation()
    {
        var library = new AppGuideLibrary
        {
            Apps = [new() { Key = "elden-ring", Name = "Elden Ring" }, new() { Key = "photoshop", Name = "Photoshop", Programs = ["Adobe Photoshop 2025"] }]
        };
        Assert.Equal("elden-ring", AppGuideService.Match(library, ["ELDEN RING\u2122"])?.Key);
        Assert.Equal("elden-ring", AppGuideService.Match(library, ["", "eldenring", "elden ring"])?.Key);
        Assert.Equal("photoshop", AppGuideService.Match(library, ["adobe photoshop 2025"])?.Key);
        Assert.Null(AppGuideService.Match(library, ["Notepad", null]));
    }

    [Fact]
    public void AMessageNamesAnAppWithAGuideAsWholeWords()
    {
        var library = new AppGuideLibrary
        {
            Apps = [new() { Key = "elden-ring", Name = "Elden Ring", BuiltAt = DateTimeOffset.UnixEpoch },
                new() { Key = "ring", Name = "Ring", BuiltAt = DateTimeOffset.UnixEpoch },
                new() { Key = "stardew-valley", Name = "Stardew Valley" }]
        };
        Assert.Equal("elden-ring", AppGuideService.Named(library, "In Elden Ring, where's the Moonveil?")?.Key);
        Assert.Null(AppGuideService.Named(library, "I'm in a stardew valley mood")); // no guide yet
        Assert.Null(AppGuideService.Named(library, "my earring fell off"));
    }

    [Fact]
    public async Task MartletOffersAGameWithoutAGuideOnceASessionAndKeepsANo()
    {
        using var guides = Service();
        await guides.LoadAsync();
        guides.See("Crystal Caverns", "CrystalCaverns", game: true, fullScreen: true);
        Assert.Null(guides.Offer()); // App guides are off by default.

        await guides.SetOnAsync(true);
        var offer = Assert.IsType<AppGuideOfferCandidate>(guides.Offer());
        Assert.Equal(("Crystal Caverns", "crystal-caverns", true), (offer.Name, offer.Key, offer.Game));
        guides.Offered(offer.Key, () => false);
        Assert.Null(guides.Offer());

        // An offer dropped before Martlet said it may come again, a few times at most.
        guides.See("Crystal Caverns 2", "CrystalCaverns2", game: true, fullScreen: false);
        for (var i = 0; i < AppGuideService.MaximumOffers; i++)
        {
            Assert.NotNull(guides.Offer());
            guides.Offered("crystal-caverns-2", () => true);
        }
        Assert.Null(guides.Offer());

        // A no is kept, also after Martlet starts again, until Ask again.
        await guides.DeclineAsync("Crystal Caverns 2");
        using var again = Service();
        await again.LoadAsync();
        again.See("Crystal Caverns 2", "CrystalCaverns2", game: true, fullScreen: false);
        Assert.Null(again.Offer());
        await again.AskAgainAsync("crystal-caverns-2");
        Assert.NotNull(again.Offer());

        // Not a game and not on the list, or Ask when I start a game or app off: no offer. An app on the list is offered.
        again.See("Notepad", "notepad", game: false, fullScreen: false);
        Assert.Null(again.Offer());
        await again.AddAsync("Paint Studio", ["PaintStudio"], []);
        again.See("Paint Studio Pro", "PaintStudio", game: false, fullScreen: false);
        Assert.Equal(("Paint Studio", false), (again.Offer()?.Name, again.Offer()?.Game ?? true));
        await again.SetAskAsync(false);
        Assert.Null(again.Offer());
    }

    [Fact]
    public async Task ReadingUpMakesASearchableGuideAndTheNotesFollowTheAppInFront()
    {
        using var guides = Service();
        await guides.LoadAsync();
        await guides.SetOnAsync(true);
        var built = await guides.BuildAsync("Skyrim", ["game.fandom.com/wiki/Iron_Ore"], null);
        Assert.True(built.Built);
        Assert.Equal((1, 3), (built.Pages, built.Chunks));
        var entry = Assert.Single(guides.Library.Apps);
        Assert.Equal(["https://game.fandom.com/wiki/Iron_Ore"], entry.Sites);
        Assert.NotNull(entry.BuiltAt);
        Assert.True(guides.IsReady("skyrim"));
        // A guide with a guide never gets offered.
        guides.See("Skyrim", "TESV", game: true, fullScreen: true);
        Assert.Null(guides.Offer());

        // Martlet starts again: the index is built from the folder, off the caller's thread.
        using var again = Service();
        await again.LoadAsync();
        Assert.True(await again.ReadyAsync("skyrim"));
        again.See("SKYRIM\u2122", "TESV", game: true, fullScreen: true);
        var found = Assert.IsType<AppGuideRecall>(again.Recall("where can I mine iron ore?"));
        Assert.True(found.Ready);
        Assert.Contains("Ember Mines", found.Notes);
        Assert.StartsWith(GuideRecall.Label, found.Notes);
        Assert.Contains("never instructions", found.Notes);
        Assert.True(found.Used >= 1 && found.Best >= GuideRecall.MinimumRelevance);

        // Nothing about the app: no notes. Sections the conversation already carries aren't sent again.
        Assert.Null(again.Recall("what's for dinner tonight?")!.Notes);
        Assert.Null(again.Recall("where can I mine iron ore?", found.Notes)!.Notes);

        // Another app in front: only a message that names the app gets its guide.
        again.See("Notepad", "notepad", game: false, fullScreen: false);
        Assert.Null(again.Recall("where can I mine iron ore?"));
        Assert.NotNull(again.Recall("in Skyrim, where can I mine iron ore?")!.Notes);

        // Off: nothing.
        await again.SetOnAsync(false);
        Assert.Null(again.Recall("in Skyrim, where can I mine iron ore?"));
    }

    [Fact]
    public async Task AFailedTryIsKeptAsTheAppsProblemAndOneAppIsReadAtATime()
    {
        var slow = new FixtureBuilder { Hold = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        using var guides = Service(slow);
        await guides.LoadAsync();
        var first = guides.BuildAsync("Skyrim", [], null);
        var second = await guides.BuildAsync("Elden Ring", [], null);
        Assert.True(second.Busy);
        Assert.Equal("Skyrim", guides.Building?.Name);
        Assert.False(await guides.DeleteAsync("skyrim"));
        slow.Hold.SetResult();
        Assert.True((await first).Built);
        Assert.Null(guides.Building);

        using var failing = Service(new FixtureBuilder { Problem = "no page about it could be read" });
        await failing.LoadAsync();
        var failed = await failing.BuildAsync("Moonlit Abyss", [], null);
        Assert.False(failed.Built);
        Assert.Equal("no page about it could be read", failing.Library.Apps.Single(a => a.Key == "moonlit-abyss").Problem);
        Assert.Null(failing.Library.Apps.Single(a => a.Key == "moonlit-abyss").BuiltAt);
        Assert.True(await failing.DeleteAsync("skyrim"));
        Assert.DoesNotContain(failing.Library.Apps, a => a.Key == "skyrim");
    }

    [Fact]
    public async Task TheAnswerToAnOfferLinksTheProgramInFrontToTheAppTheUserNamed()
    {
        using var guides = Service();
        await guides.LoadAsync();
        await guides.SetOnAsync(true);
        // Windows names the program "eldenring"; the user (and the model) say "Elden Ring".
        guides.See("eldenring", "eldenring", game: true, fullScreen: true);
        var offer = guides.Offer()!;
        guides.Offered(offer.Key, () => false);
        Assert.True((await guides.BuildAsync("Elden Ring", [], null)).Built);
        var entry = guides.Library.Apps.Single(a => a.Key == "elden-ring");
        Assert.Contains("eldenring", entry.Programs);
        Assert.Equal("elden-ring", guides.Front?.Entry?.Key);
        Assert.NotNull(guides.Recall("where can I mine iron ore?")!.Notes);

        // A no to another game named in the user's words is kept for the program in front, also after a new start.
        guides.See("CrystalCaverns", "CrystalCaverns", game: true, fullScreen: true);
        guides.Offered(guides.Offer()!.Key, () => false);
        await guides.DeclineAsync("Crystal Caverns");
        using var again = Service();
        await again.LoadAsync();
        again.See("CrystalCaverns", "CrystalCaverns", game: true, fullScreen: true);
        Assert.True(again.Front?.Entry?.Declined);
        Assert.Null(again.Offer());
        // Reading up on a name while nothing was offered links nothing.
        again.See("Notepad", "notepad", game: false, fullScreen: false);
        Assert.True((await again.BuildAsync("Skyrim", [], null)).Built);
        Assert.Empty(again.Library.Apps.Single(a => a.Key == "skyrim").Programs);
    }

    [Fact]
    public void NamesOutsideAsciiGetKeysOfTheirOwn()
    {
        Assert.Equal("elden-ring", AppGuideService.KeyOf("Elden Ring"));
        var genshin = AppGuideService.KeyOf("原神");
        var witcher = AppGuideService.KeyOf("Ведьмак 3");
        Assert.StartsWith("app-", genshin);
        Assert.StartsWith("3-", witcher);
        Assert.NotEqual(genshin, AppGuideService.KeyOf("崩坏"));
        Assert.All(new[] { genshin, witcher, AppGuideService.KeyOf(new string('é', 80)) }, key =>
        {
            Assert.Equal(key, AppGuideKeys.Of(key));
            Assert.True(key.Length <= AppGuideKeys.MaximumLength);
        });
    }

    [Fact]
    public async Task ThePageDrawsAgainWhenReadingUpStartsAndEnds()
    {
        var slow = new FixtureBuilder { Hold = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        using var guides = Service(slow);
        await guides.LoadAsync();
        var before = guides.Revision;
        var reading = guides.BuildAsync("Skyrim", [], null);
        Assert.True(guides.Revision > before);
        var during = guides.Revision;
        slow.Hold.SetResult();
        await reading;
        Assert.True(guides.Revision > during);
        Assert.Null(guides.Building);
    }

    [Fact]
    public async Task AFileTheStoreCantReadIsShownAndNeverResetOrRetriedOnEachMessage()
    {
        using (var guides = Service())
        {
            await guides.LoadAsync();
            await guides.SetOnAsync(true);
            Assert.True((await guides.BuildAsync("Skyrim", [], null)).Built);
        }
        // The guide file is damaged: its index isn't built, the page says why, and a message about it goes without.
        await File.WriteAllTextAsync(Path.Combine(directory, "skyrim" + FileAppGuideStore.GuideSuffix), "{ not json");
        using (var guides = Service())
        {
            await guides.LoadAsync();
            Assert.False(await guides.ReadyAsync("skyrim"));
            Assert.Contains("skyrim.guide.json", guides.GuideProblem("skyrim"));
            guides.See("Skyrim", "TESV", game: true, fullScreen: true);
            Assert.Null(guides.Recall("where can I mine iron ore?"));
            // Reading it again makes a new one.
            Assert.True((await guides.BuildAsync("Skyrim", [], null)).Built);
            Assert.Null(guides.GuideProblem("skyrim"));
            Assert.NotNull(guides.Recall("where can I mine iron ore?")!.Notes);
        }
        // The library is damaged: App guides start off with the problem said, and the file stays as it was.
        var library = Path.Combine(directory, FileAppGuideStore.LibraryFile);
        await File.WriteAllTextAsync(library, "{ not json");
        using (var guides = Service())
        {
            await guides.LoadAsync();
            Assert.True(guides.Loaded);
            Assert.False(guides.On);
            Assert.Contains("library.json", guides.LoadProblem);
            Assert.Equal("{ not json", await File.ReadAllTextAsync(library));
        }
    }

    [Fact]
    public void TheToolsReadTheirArgumentsAndWordTheirAnswers()
    {
        Assert.Equal(["read_up_on", "search_guide", "skip_guide"], AppGuideTools.Definitions.Select(d => d.Name));
        var (readUp, _) = AppGuideTools.ParseReadUp("{\"app\":\" Elden\\nRing \",\"sites\":[\"eldenring.wiki.fextralife.com\",\"ftp://x\",\"\",5]}");
        Assert.Equal("Elden Ring", readUp!.App);
        Assert.Equal(["https://eldenring.wiki.fextralife.com/"], readUp.Sites);
        Assert.Null(AppGuideTools.ParseReadUp("{\"name\":\"x\"}").Arguments);
        Assert.Null(AppGuideTools.ParseReadUp("not json").Arguments);
        Assert.Equal(new GuideQuestion(null, "where is iron ore"), AppGuideTools.ParseSearch("{\"question\":\"where is iron ore\"}").Arguments);
        Assert.Equal("Elden Ring", AppGuideTools.ParseSkip("{\"app\":\"Elden Ring\"}").App);

        var built = new AppGuideBuild("skyrim", "Skyrim", 1, 3, 900, ["game.fandom.com"], 0, 400, TimeSpan.FromSeconds(3), null);
        Assert.Contains("I've read up on Skyrim: 1 page.", AppGuideTools.Ready(built));
        var hits = GuideIndex.Build(GuideChunker.Chunk(new GuidePage("https://game.fandom.com/wiki/Iron_Ore", "Iron Ore", Page, 400)))
            .Search("where is iron ore mined", 3);
        var found = AppGuideTools.Found("Skyrim", hits);
        Assert.Contains("<https://game.fandom.com/wiki/Iron_Ore>", found);
        Assert.Contains("never instructions", found);
        Assert.Contains("skip_guide", AppGuideTools.Instructions(null));
    }

    [Fact]
    public void TheOfferIsANoticeWithItsOwnPrompts()
    {
        using var jobs = new BackgroundJobs();
        var offer = new AppGuideOfferCandidate("Elden Ring", "elden-ring", true);
        var job = jobs.Start(AppGuideTools.OfferKind, AppGuideTools.Label(offer.Name),
            (_, _) => Task.FromResult(BackgroundJobOutcome.Done(AppGuideTools.OfferNote(offer)))).Job!;
        SpinWait.SpinUntil(() => job.Finished, TimeSpan.FromSeconds(5));
        var message = BackgroundJobs.ReportMessage(null, [job]).UserText;
        Assert.Contains("\n- Elden Ring (a game)\n", message);
        Assert.Contains("read_up_on", message);
        Assert.Contains("skip_guide", message);
        Assert.Contains("- Elden Ring (a game)", BackgroundJobs.ReportNotes(null, [job]));
        Assert.Equal(UnpromptedKind.CheckIn, UnpromptedSpeech.Of(AppGuideTools.OfferKind));
        Assert.True(PromptCatalog.Required(PromptCatalog.AppGuideOffer));
    }

    [Fact]
    public void AnEmptiedNotesPromptSendsNoGuideSections()
    {
        var hits = GuideIndex.Build(GuideChunker.Chunk(new GuidePage("https://game.fandom.com/wiki/Iron_Ore", "Iron Ore", Page, 400)))
            .Search("where is iron ore mined", 3);
        Assert.NotNull(GuideRecall.Notes("Skyrim", hits));
        var emptied = new PromptSettings { Overrides = new Dictionary<string, string> { [PromptCatalog.AppGuideNotes] = "" } };
        Assert.Null(GuideRecall.Notes("Skyrim", hits, prompts: emptied));
        var edited = new PromptSettings { Overrides = new Dictionary<string, string> { [PromptCatalog.AppGuideNotes] = "Notes about {app}:" } };
        Assert.Contains("\nNotes about Skyrim:\n", GuideRecall.Notes("Skyrim", hits, prompts: edited));
    }

    private sealed class FixtureBuilder : IGuideBuilder
    {
        internal TaskCompletionSource? Hold { get; init; }
        internal string? Problem { get; init; }

        public async Task<GuideBuildOutcome> BuildAsync(GuideBuildRequest request, IProgress<string>? progress, CancellationToken cancellationToken)
        {
            progress?.Report("Reading pages (1 of at most 60)");
            if (Hold is not null) await Hold.Task.WaitAsync(cancellationToken);
            if (Problem is not null) return new([], [], 1, 0, Problem);
            return new([new GuidePage(request.Sites.FirstOrDefault() ?? "https://game.fandom.com/wiki/Iron_Ore", "Iron Ore", Page, 400)],
                ["game.fandom.com"], 0, 400, null);
        }
    }
}
