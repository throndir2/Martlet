using System.Text.Json;
using Martlet.Conversation;
using Martlet.Conversation.Guides;
using Martlet.Conversation.Tests;
using Martlet.Desktop;
using Martlet.Providers.Tests;

namespace Martlet.Desktop.Tests;

/// <summary>App guides through the real controller (Companion › App guides): the guide of the app in front goes in the notes of
/// a message about it, never in the instructions; the guide tools come last and stay the same; a game in front is read up on by
/// Martlet itself, whatever the model says, and a no stops it.</summary>
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
        await guides.SetOnAsync(false);
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
        Assert.Equal([.. before, AppGuideTools.ReadUpName, AppGuideTools.SearchName, AppGuideTools.SkipName, AppGuideTools.BriefName], tools);
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
    public async Task TheAppsBriefGoesWithTheFirstMessageAboutItOnly()
    {
        await using var fixture = await LiveFixture.Create(tools: true);
        using var guides = new AppGuideService(new FileAppGuideStore(Path.Combine(fixture.DirectoryPath, "guides")), new FixtureBuilder());
        await guides.LoadAsync();
        await guides.SetOnAsync(true);
        fixture.Controller.Guides = guides;
        Assert.True((await guides.BuildAsync("Skyrim", [], null)).Built);
        var key = guides.Library.Apps.Single().Key;
        Assert.True(await guides.KeepBriefAsync(key, "What it is: an open-world fantasy game.\nEarly tips: Mine iron ore in the Ember Mines."));
        guides.See("SKYRIM\u2122", "TESV", game: true, fullScreen: true);

        fixture.Answer("In the Ember Mines.");
        var first = fixture.Start("Where can I mine iron ore?");
        await fixture.Finish(first);
        var notes = Notes(fixture.Llm.Body)!;
        Assert.Contains(GuideBrief.Label, notes);
        Assert.Contains("open-world fantasy game", notes);
        var instructions = Instructions(fixture.Llm.Body);
        Assert.DoesNotContain("open-world fantasy game", instructions);

        fixture.Answer("At a smelter.");
        var second = fixture.Start("And where do I smelt the iron ore?");
        await fixture.Finish(second);
        Assert.DoesNotContain(GuideBrief.Label, Notes(fixture.Llm.Body) ?? "");
        Assert.Equal(instructions, Instructions(fixture.Llm.Body));
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
        Assert.Contains("you've started reading up on it", notes);
        Assert.Contains("- Crystal Caverns (a game)", notes);
        Assert.Contains(AppGuideTools.SkipName, ToolNames(fixture.Llm.Body));
    }

    // The tester's report: Abiotic Factor came to the front, Martlet said it read up on it, and it wasn't on the list. Reading up
    // depended on the model calling read_up_on, which a Thinking route without function calling can't and a model in character
    // often doesn't. Now Martlet reads up on its own, so the game is on the list with its guide whatever the model says.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AGameInFrontIsReadUpOnByItselfWhetherOrNotTheModelCallsATool(bool tools)
    {
        await using var fixture = await LiveFixture.Create(tools: tools);
        using var guides = new AppGuideService(new FileAppGuideStore(Path.Combine(fixture.DirectoryPath, "guides")), new FixtureBuilder());
        await guides.LoadAsync();
        await guides.SetOnAsync(true);
        fixture.Controller.Guides = guides;
        // Windows names an Unreal game's program like this.
        guides.See("AbioticFactor", "AbioticFactor-Win64-Shipping", game: true, fullScreen: true);
        var due = Assert.IsType<AppGuideOfferCandidate>(guides.Offer());
        guides.Offered(due.Key);
        var job = Assert.IsType<BackgroundJob>(fixture.Controller.ReadUpOnItsOwn(guides, due).Job);
        await fixture.Advance(() => job.Finished);
        Assert.Equal(BackgroundJobState.Succeeded, job.State);

        // On the list with its guide, under the game's name, and known by its program from now on.
        var entry = Assert.Single(guides.Library.Apps);
        Assert.Equal("Abiotic Factor", entry.Name);
        Assert.NotNull(entry.BuiltAt);
        Assert.Contains("AbioticFactor-Win64-Shipping", entry.Programs);
        Assert.Equal(entry.Key, guides.Front?.Entry?.Key);
        Assert.Null(guides.Offer());
        Assert.True(await guides.ReadyAsync(entry.Key));

        // The model only talks: what Martlet did goes with the next message (that it reads up, and that it's done), and so do
        // the guide's sections while the game is in front.
        fixture.Answer("In the Ember Mines.");
        var asked = fixture.Start("Where can I mine iron ore?");
        await fixture.Finish(asked);
        Assert.True(asked.GuideSections >= 1);
        var notes = Notes(fixture.Llm.Body)!;
        Assert.Contains("Ember Mines", notes);
        Assert.Contains("you've started reading up on it", notes);
        Assert.Contains("read up on Abiotic Factor", notes);
    }

    [Fact]
    public async Task SayingNoStopsReadingUpOnTheGameInFrontAndKeepsTheNo()
    {
        await using var fixture = await LiveFixture.Create(tools: true);
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var guides = new AppGuideService(new FileAppGuideStore(Path.Combine(fixture.DirectoryPath, "guides")),
            new FixtureBuilder { Hold = hold });
        await guides.LoadAsync();
        await guides.SetOnAsync(true);
        fixture.Controller.Guides = guides;
        guides.See("AbioticFactor", "AbioticFactor-Win64-Shipping", game: true, fullScreen: true);
        var due = guides.Offer()!;
        guides.Offered(due.Key);
        var job = fixture.Controller.ReadUpOnItsOwn(guides, due).Job!;
        await fixture.Advance(() => guides.Building is not null);

        var calls = 0;
        fixture.Llm.Respond = (_, _) => Task.FromResult(TextRecordingHandler.Sse(Interlocked.Increment(ref calls) == 1
            ? CallTrace(AppGuideTools.SkipName, """{"app":"Abiotic Factor"}""") : Harness.Trace("Okay, I'll leave it.")));
        var no = fixture.Start("No thanks, don't read up on it.");
        await fixture.Finish(no);
        await fixture.Advance(() => job.Finished && guides.Building is null);

        Assert.Equal(BackgroundJobState.Canceled, job.State);
        var entry = Assert.Single(guides.Library.Apps);
        Assert.True(entry.Declined);
        Assert.Null(entry.BuiltAt);
        Assert.Null(guides.Offer());
    }

    [Fact]
    public async Task ReadUpOnAnAppStartsReadingWhenTheReplyCantCallTools()
    {
        await using var fixture = await LiveFixture.Create();
        using var guides = new AppGuideService(new FileAppGuideStore(Path.Combine(fixture.DirectoryPath, "guides")), new FixtureBuilder());
        await guides.LoadAsync();
        await guides.SetOnAsync(true);
        fixture.Controller.Guides = guides;
        fixture.Answer("Sure, I'm on it!");
        var asked = fixture.Start("Can you read up on Stardew Valley please?");
        await fixture.Finish(asked);
        Assert.Empty(ToolNames(fixture.Llm.Body));
        Assert.Contains("you're reading up on it in the background now", Notes(fixture.Llm.Body));
        await fixture.Advance(() => guides.Building is null && guides.Library.Apps.Any(a => a.BuiltAt is not null));
        Assert.Equal("Stardew Valley", Assert.Single(guides.Library.Apps).Name);

        // "Don't read up on it" never starts anything.
        fixture.Answer("Okay.");
        var not = fixture.Start("Don't read up on Elden Ring.");
        await fixture.Finish(not);
        Assert.Single(guides.Library.Apps);
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

    [Fact]
    public void TheWatchTellsAWindowedUnityGameOutsideAnyLibrary()
    {
        var folder = Directory.CreateTempSubdirectory("martlet-guide-engine-");
        try
        {
            File.WriteAllBytes(Path.Combine(folder.FullName, "UnityPlayer.dll"), []);
            var path = Path.Combine(folder.FullName, "StarRail.exe");
            Assert.Equal(("Star Rail", "StarRail", true), AppGuideWatch.Describe(path, "Star Rail", fullScreen: false, exclusive: false));
        }
        finally { folder.Delete(recursive: true); }
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

    // The model answers with one function call and no text.
    private static string CallTrace(string name, string arguments)
    {
        var done = new { id = "fc_1", type = "function_call", call_id = "call_a", name, arguments, status = "completed" };
        return string.Concat(
            TextFixtures.Event("response.created", 0, new { response = Response("in_progress", []) }),
            TextFixtures.Event("response.in_progress", 1, new { response = Response("in_progress", []) }),
            TextFixtures.Event("response.output_item.added", 2, new { output_index = 0,
                item = new { id = "fc_1", type = "function_call", call_id = "call_a", name, arguments = "", status = "in_progress" } }),
            TextFixtures.Event("response.function_call_arguments.delta", 3, new { output_index = 0, item_id = "fc_1", delta = arguments }),
            TextFixtures.Event("response.function_call_arguments.done", 4, new { output_index = 0, item_id = "fc_1", arguments }),
            TextFixtures.Event("response.output_item.done", 5, new { output_index = 0, item = done }),
            TextFixtures.Event("response.completed", 6, new { response = Response("completed", [done]) }));
    }

    private static object Response(string status, object[] output) => new
    {
        id = "resp_call", @object = "response", model = TextFixtures.Model, status, store = false, background = false,
        output, error = (object?)null, incomplete_details = (object?)null
    };

    private sealed class FixtureBuilder : IGuideBuilder
    {
        internal TaskCompletionSource? Hold { get; init; }

        public async Task<GuideBuildOutcome> BuildAsync(GuideBuildRequest request, IProgress<string>? progress, CancellationToken cancellationToken)
        {
            if (Hold is not null) await Hold.Task.WaitAsync(cancellationToken);
            return new GuideBuildOutcome([new GuidePage("https://game.fandom.com/wiki/Iron_Ore", "Iron Ore", Page, 400)],
                ["game.fandom.com"], 0, 400, null);
        }
    }
}
