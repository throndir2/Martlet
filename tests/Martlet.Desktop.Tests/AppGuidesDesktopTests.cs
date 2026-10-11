using System.Text.Json;
using Martlet.Conversation.Guides;
using Martlet.Desktop;

namespace Martlet.Desktop.Tests;

/// <summary>App guides through the real controller (Companion › App guides): the guide of the app in front goes in the notes of
/// a message about it, never in the instructions; the guide tools come last and stay the same; the offer reaches the conversation.</summary>
public sealed class AppGuidesDesktopTests
{
    private const string Page =
        "# Iron Ore\nIron Ore is a material used to forge weapons and armor at any anvil in the game world.\n" +
        "## Locations\nIron Ore can be mined in the Ember Mines below level 40 and bought from the blacksmith for 7 gold.";

    [Fact]
    public async Task TheGuideOfTheAppInFrontGoesWithAMessageAboutItAndTheToolsComeLast()
    {
        await using var fixture = await LiveFixture.Create(tools: true);
        using var guides = new AppGuideService(new FileAppGuideStore(Path.Combine(fixture.DirectoryPath, "guides")), new FixtureBuilder());
        await guides.LoadAsync();
        fixture.Controller.Guides = guides;
        fixture.Answer("Hello!");
        var off = fixture.Start("Hi there.");
        await fixture.Finish(off);
        var before = ToolNames(fixture.Llm.Body);
        Assert.DoesNotContain(AppGuideTools.ReadUpName, before);
        Assert.Equal(0, off.GuideSections);

        await guides.SetOnAsync(true);
        Assert.True((await guides.BuildAsync("Skyrim", [], null)).Built);
        guides.See("SKYRIM\u2122", "TESV", game: true, fullScreen: true);
        fixture.Answer("In the Ember Mines.");
        var asked = fixture.Start("Where can I mine iron ore?");
        await fixture.Finish(asked);
        var tools = ToolNames(fixture.Llm.Body);
        Assert.Equal([.. before, AppGuideTools.ReadUpName, AppGuideTools.SearchName, AppGuideTools.SkipName], tools);
        Assert.True(asked.GuideSections >= 1);
        var notes = Notes(fixture.Llm.Body)!;
        Assert.Contains(GuideRecall.Label, notes);
        Assert.Contains("Ember Mines", notes);
        var instructions = Instructions(fixture.Llm.Body);
        Assert.Contains("read_up_on reads up on a game or app", instructions);
        Assert.DoesNotContain("Ember Mines", instructions);

        // A message that isn't about the app gets no guide, and its request starts exactly like the one before.
        fixture.Answer("Pasta!");
        var ordinary = fixture.Start("What should we cook for dinner?");
        await fixture.Finish(ordinary);
        Assert.Equal(0, ordinary.GuideSections);
        Assert.DoesNotContain(GuideRecall.Label, Notes(fixture.Llm.Body) ?? "");
        Assert.Equal(instructions, Instructions(fixture.Llm.Body));
        Assert.Equal(tools, ToolNames(fixture.Llm.Body));
    }

    [Fact]
    public async Task TheOfferGoesWithWhatTheUserSaysNext()
    {
        await using var fixture = await LiveFixture.Create(tools: true);
        using var guides = new AppGuideService(new FileAppGuideStore(Path.Combine(fixture.DirectoryPath, "guides")), new FixtureBuilder());
        await guides.LoadAsync();
        await guides.SetOnAsync(true);
        fixture.Controller.Guides = guides;
        guides.See("Crystal Caverns", "CrystalCaverns", game: true, fullScreen: true);
        var offer = Assert.IsType<AppGuideOfferCandidate>(guides.Offer());
        var job = fixture.Controller.OfferGuide(offer);
        Assert.NotNull(job);
        await fixture.Advance(() => job!.Finished);

        fixture.Answer("Sure thing!");
        var next = fixture.Start("Hey, I'm back.");
        await fixture.Finish(next);
        var notes = Notes(fixture.Llm.Body)!;
        Assert.Contains("you have no guide for it yet", notes);
        Assert.Contains("- Crystal Caverns (a game)", notes);
        Assert.Contains(AppGuideTools.SkipName, ToolNames(fixture.Llm.Body));
    }

    [Theory]
    [InlineData(@"D:\SteamLibrary\steamapps\common\Stardew Valley\Stardew Valley.exe", "Stardew Valley", false, false, true)]
    [InlineData(@"C:\Games\Starfall\Starfall.exe", "Starfall", true, true, true)]
    [InlineData(@"C:\Games\Starfall\Starfall.exe", "Starfall", true, false, false)]
    [InlineData(@"C:\Windows\System32\notepad.exe", "Notepad", false, false, false)]
    [InlineData(@"C:\Program Files (x86)\Steam\steam.exe", "Steam", false, false, false)]
    public void TheWatchTellsAGameFromItsProgramAlone(string path, string name, bool fullScreen, bool exclusive, bool game)
    {
        var seen = AppGuideWatch.Describe(path, name, fullScreen, exclusive);
        Assert.Equal((name, Path.GetFileNameWithoutExtension(path), game), seen);
        Assert.Equal(Path.GetFileNameWithoutExtension(path), AppGuideWatch.Describe(path, "", fullScreen, exclusive).Name);
    }

    private static string Instructions(byte[] body)
    {
        using var json = JsonDocument.Parse(body);
        return json.RootElement.TryGetProperty("instructions", out var instructions) ? instructions.GetString() ?? "" : "";
    }

    private static string[] ToolNames(byte[] body)
    {
        using var json = JsonDocument.Parse(body);
        return json.RootElement.TryGetProperty("tools", out var tools)
            ? tools.EnumerateArray().Select(tool => tool.GetProperty("name").GetString()!).ToArray() : [];
    }

    // The notes on the latest user message, or null.
    private static string? Notes(byte[] body)
    {
        using var json = JsonDocument.Parse(body);
        var last = json.RootElement.GetProperty("input").EnumerateArray()
            .Where(item => item.GetProperty("role").GetString() == "user")
            .Select(item => item.GetProperty("content") is { ValueKind: JsonValueKind.String } text ? text.GetString()!
                : item.GetProperty("content").EnumerateArray().Single(part => part.GetProperty("type").GetString() == "input_text")
                    .GetProperty("text").GetString()!)
            .Last();
        var at = last.LastIndexOf("[" + LiveConversationConfiguration.NotesLabel + "]", StringComparison.Ordinal);
        return at < 0 ? null : last[at..];
    }

    private sealed class FixtureBuilder : IGuideBuilder
    {
        public Task<GuideBuildOutcome> BuildAsync(GuideBuildRequest request, IProgress<string>? progress, CancellationToken cancellationToken) =>
            Task.FromResult(new GuideBuildOutcome([new GuidePage("https://game.fandom.com/wiki/Iron_Ore", "Iron Ore", Page, 400)],
                ["game.fandom.com"], 0, 400, null));
    }
}
