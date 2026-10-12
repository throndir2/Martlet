using System.Text.Json;
using Martlet.Conversation;
using Martlet.Conversation.Guides;
using Martlet.Conversation.Tests;
using Martlet.Core.Creations;
using Martlet.Desktop;
using Martlet.Providers.Tests;

namespace Martlet.Desktop.Tests;

/// <summary>Research reports found again through the real controller (docs/CONVERSATION.md, "Finding a report again"): search_guide
/// finds a report's sections with the sources they cite, with App guides on or, while they are off, offered alone with Web
/// research.</summary>
public sealed class ResearchReportSearchDesktopTests
{
    [Fact]
    public async Task SearchGuideFindsWhatAnEarlierReportSaid()
    {
        await using var fixture = await LiveFixture.Create(tools: true, data: true);
        var report = await AddReportAsync(fixture.DirectoryPath);
        using var guides = new AppGuideService(new FileAppGuideStore(Path.Combine(fixture.DirectoryPath, "guides")), new NoBuilder());
        await guides.LoadAsync();
        await guides.SetOnAsync(true);
        fixture.Controller.Guides = guides;

        var calls = 0;
        fixture.Llm.Respond = (_, _) => Task.FromResult(TextRecordingHandler.Sse(Interlocked.Increment(ref calls) == 1
            ? CallTrace(AppGuideTools.SearchName, """{"question":"are laser pointers safe for my cat"}""")
            : Harness.Trace("Yes, if the game ends on a toy she can catch.")));
        var asked = fixture.Start("What did you find out about laser pointers for cats?");
        await fixture.Finish(asked);

        var output = ToolOutput(fixture.Llm.Body);
        Assert.StartsWith("From research reports you wrote earlier", output, StringComparison.Ordinal);
        Assert.Contains($"(report id {report.Key}, relevance ", output, StringComparison.Ordinal);
        Assert.Contains("Laser pointers are fine", output, StringComparison.Ordinal);
        Assert.Contains("[2] Laser play <https://cats.example/laser>", output, StringComparison.Ordinal);
        Assert.Equal(1, fixture.Controller.Reports.Count);
    }

    [Fact]
    public async Task WithAppGuidesOffSearchGuideComesAloneWithWebResearch()
    {
        await using var fixture = await LiveFixture.Create(tools: true, data: true);
        await AddReportAsync(fixture.DirectoryPath);
        var controller = fixture.Controller;
        var configured = controller.Configuration!;
        Assert.True(controller.OffersResearch(configured));
        Assert.True(controller.OffersReportSearch(configured));

        var calls = 0;
        fixture.Llm.Respond = (_, _) => Task.FromResult(TextRecordingHandler.Sse(Interlocked.Increment(ref calls) == 1
            ? CallTrace(AppGuideTools.SearchName, """{"app":"cat toys","question":"wand toys"}""")
            : Harness.Trace("Wand toys, mostly.")));
        var asked = fixture.Start("Which toys did your research say cats like?");
        await fixture.Finish(asked);
        var tools = ToolNames(fixture.Llm.Body);
        Assert.Equal(AppGuideTools.SearchName, tools[^1]);
        Assert.DoesNotContain(AppGuideTools.ReadUpName, tools);
        Assert.Contains("Most cats prefer wand toys", ToolOutput(fixture.Llm.Body), StringComparison.Ordinal);

        // Turning Web research off takes it away again.
        var loaded = await fixture.Store.LoadAsync();
        await fixture.Save(loaded.Settings! with { Generation = new() { ThinkLonger = new() { WebResearch = false } } });
        Assert.False(controller.OffersReportSearch(controller.Configuration!));
    }

    private static async Task<Creation> AddReportAsync(string directory)
    {
        var registry = new CreationRegistry();
        registry.Register(ResearchReports.Kind);
        var pages = new List<WebPage>
        {
            new("https://cats.example/wand", "Wand toys - Cat Care", "text", 4), new("https://cats.example/laser", "Laser play", "text", 4),
            new("https://cats.example/crinkle", "Crinkle balls", "text", 4)
        };
        var report = WebResearch.Report(WebResearch.ParseStep("TITLE: Toys cats like best\nSUMMARY: Wand toys and crinkle balls.\nREPORT:\n" +
            "## Wand toys\nMost cats prefer wand toys that move like prey and let them stalk and pounce [1].\n" +
            "## Laser pointers\nLaser pointers are fine if the game ends on a toy the cat can catch, so it isn't frustrated [2, 3]."),
            new("cat toys", "which toys cats like"), pages);
        return await CreationStore.AddAsync(directory, ResearchReports.Draft(report, new CreationAuthor { Device = "test" }), registry,
            DateTimeOffset.UtcNow, CancellationToken.None);
    }

    private static string[] ToolNames(byte[] body)
    {
        using var json = JsonDocument.Parse(body);
        return json.RootElement.TryGetProperty("tools", out var tools)
            ? tools.EnumerateArray().Select(tool => tool.GetProperty("name").GetString()!).ToArray() : [];
    }

    // What the tool returned, as the model's next request carries it.
    private static string ToolOutput(byte[] body)
    {
        using var json = JsonDocument.Parse(body);
        return json.RootElement.GetProperty("input").EnumerateArray()
            .Single(item => item.TryGetProperty("type", out var type) && type.GetString() == "function_call_output")
            .GetProperty("output").GetString()!;
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

    private sealed class NoBuilder : IGuideBuilder
    {
        public Task<GuideBuildOutcome> BuildAsync(GuideBuildRequest request, IProgress<string>? progress, CancellationToken cancellationToken) =>
            Task.FromResult(new GuideBuildOutcome([], [], 0, 0, "not in this test"));
    }
}
