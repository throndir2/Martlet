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

    private sealed class NoBuilder : IGuideBuilder
    {
        public Task<GuideBuildOutcome> BuildAsync(GuideBuildRequest request, IProgress<string>? progress, CancellationToken cancellationToken) =>
            Task.FromResult(new GuideBuildOutcome([], [], 0, 0, "not in this test"));
    }
}
