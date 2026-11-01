using System.IO;
using Martlet.Conversation.Guides;
using Martlet.Desktop;

namespace Martlet.Desktop.Tests;

/// <summary>Companion › App guides' decisions without WPF (<see cref="AppGuidesPageModel"/>): the Now line, the In front line, an
/// app's status line, sizes and what the Add an app boxes hold.</summary>
public sealed class AppGuidesPageModelTests
{
    [Fact]
    public async Task TheLinesSayOffOnAndWhatAnAppHas()
    {
        var root = Path.Combine(Path.GetTempPath(), "martlet-app-guides-model-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var guides = new AppGuideService(new FileAppGuideStore(root), new NoBuilder());
            await guides.LoadAsync();
            await guides.SetOnAsync(false);
            var off = new AppGuidesPageModel(guides);
            Assert.Equal("Off: Martlet doesn't read up on games and apps, or notice which one you play.", off.NowText);
            Assert.Equal("Martlet doesn't look at which app is in front while App guides are off.", off.FrontText);
            Assert.Null(off.LoadProblemText);

            await guides.SetOnAsync(true);
            var entry = await guides.AddAsync("Stardew Valley", ["StardewValley"], ["https://stardewvalleywiki.com/"]);
            var on = new AppGuidesPageModel(guides);
            Assert.Equal("On: no guides yet.", on.NowText);
            Assert.Equal("In front: nothing Martlet has noticed yet.", on.FrontText);
            Assert.Equal("No guide yet; reads from stardewvalleywiki.com; programs: StardewValley.", on.Status(entry));
            Assert.Equal("Hints and secrets: not running on this PC.", AppGuidesPageModel.HintsText(guides, null));
            await guides.SetOnAsync(false);
            Assert.Equal("Hints and secrets: off while App guides are off.", AppGuidesPageModel.HintsText(guides, null));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }

    [Fact]
    public void SizesAndTheAddBoxesReadAsBefore()
    {
        Assert.Equal(("1 KB", "42 KB", "2.5 MB"), (AppGuidesPageModel.Size(10), AppGuidesPageModel.Size(42_000), AppGuidesPageModel.Size(2_500_000)));

        var (name, programs, given, sites) = AppGuidesPageModel.AddInput(" Stardew Valley ", "StardewValley; SV", "stardewvalleywiki.com ftp://files.example");
        Assert.Equal("Stardew Valley", name);
        Assert.Equal(["StardewValley", "SV"], programs);
        Assert.Equal(2, given.Length);
        Assert.Equal(["https://stardewvalleywiki.com/"], sites);
        Assert.Equal("", AppGuidesPageModel.AddInput("  ", "", "").Name);
    }

    [Fact]
    public async Task TheHintsLineSaysWhatTheHintsDid()
    {
        var root = Path.Combine(Path.GetTempPath(), "martlet-app-guides-hints-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var guides = new AppGuideService(new FileAppGuideStore(root), new NoBuilder());
            await guides.LoadAsync();
            await guides.SetOnAsync(true);
            var place = new GamePlace("Elden Ring", "Liurnia", null);
            var places = new Places { Current = place };
            var thinker = new Thinker("Topic: Secret room\nHint: A secret room is behind the waterfall here.");
            using var hints = new GameHints(places, new NoPreferences(), new OneSection(), thinker, new Speaker());
            Assert.Equal("Hints and secrets: on; tell Martlet if you'd rather not get them.", AppGuidesPageModel.HintsText(guides, hints));
            await hints.HintAsync(place);
            thinker.Answer = GameHints.Pass;
            await hints.HintAsync(place with { Place = "Caelid" });
            Assert.Equal("Hints and secrets: on; tell Martlet if you'd rather not get them (1 said, 1 held back by your preferences).",
                AppGuidesPageModel.HintsText(guides, hints));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }

    private sealed class Places : IGamePlaceSource
    {
        public GamePlace? Current { get; set; }
        public event Action<GamePlace>? Changed { add { } remove { } }
    }

    private sealed class NoPreferences : IHintPreferences
    {
        public string? For(string game) => null;
    }

    private sealed class OneSection : IHintGuide
    {
        public Task<IReadOnlyList<GuideChunk>> FindAsync(GamePlace place, IReadOnlyList<string> questions, int max, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<GuideChunk>>([new("https://wiki/a", "A", "B", "A secret room is behind the waterfall."),
                new("https://wiki/a", "A", "C", "The boss is weak to fire.")]);
        public Task<bool> ResearchAsync(GamePlace place, string area, CancellationToken cancellationToken) => Task.FromResult(false);
    }

    private sealed class Thinker(string answer) : IGuideThinker
    {
        public string Answer { get; set; } = answer;
        public bool CanThink => true;
        public Task<(string? Text, string? Problem)> ThinkAsync(GuideThinkingAsk ask, CancellationToken cancellationToken) =>
            Task.FromResult<(string?, string?)>((Answer, null));
    }

    private sealed class Speaker : IHintSpeaker
    {
        public bool Quiet => true;
        public Task<bool> SayAsync(GameHint hint, CancellationToken cancellationToken) => Task.FromResult(true);
    }

    private sealed class NoBuilder : IGuideBuilder
    {
        public Task<GuideBuildOutcome> BuildAsync(GuideBuildRequest request, IProgress<string>? progress, CancellationToken cancellationToken) =>
            Task.FromResult(new GuideBuildOutcome([], [], 0, 0, "not in this test"));
    }
}
