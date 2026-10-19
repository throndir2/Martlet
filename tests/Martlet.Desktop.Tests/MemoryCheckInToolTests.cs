using System.Collections.Concurrent;
using System.Text.Json;
using Martlet.Conversation;
using Martlet.Desktop;
using Martlet.Providers;

namespace Martlet.Desktop.Tests;

/// <summary>The Memory check-in tool set (<see cref="MemoryToolSet"/>) through the real controller: each tool runs its
/// manage_memories action, changes are reported like the reply's, and the first line of each answer holds no fact.</summary>
public sealed class MemoryCheckInToolTests
{
    [Fact]
    public void EachToolRunsItsActionAndTheSetTakesOverManageMemories()
    {
        Assert.Equal(MemoryTools.Name, MemoryToolSet.ReplyTool);
        Assert.Equal([MemoryTools.Name], MemoryToolSet.Set.Replaces);
        Assert.Same(MemoryToolSet.Set, CheckInToolSets.Find(MemoryToolSet.Id));
        Assert.DoesNotContain(MemoryTools.FindName, CheckInToolSets.All.SelectMany(s => s.Replaces));
        Assert.DoesNotContain(MemoryTools.FindName, CheckInToolSets.All.SelectMany(s => s.Tools).Select(t => t.Name));
        foreach (var tool in MemoryToolSet.Set.Tools) JsonDocument.Parse(tool.ParametersJson).Dispose();
        JsonDocument.Parse(MemoryTools.FindDefinition.ParametersJson).Dispose();
        Assert.Equal(["overview", "find", "history", "remember", "update", "update_many", "note", "merge", "outdated", "forget"],
            MemoryToolSet.Set.Tools.Select(t => MemoryToolSet.Action(t.Name)));
        Assert.Null(MemoryToolSet.Action(MemoryTools.Name));

        // The action is always the tool's own, whatever the model wrote; bad JSON is an empty call.
        using var set = JsonDocument.Parse(MemoryTools.WithAction("""{"action":"forget","query":"dog"}""", "find"));
        Assert.Equal("find", set.RootElement.GetProperty("action").GetString());
        Assert.Equal("dog", set.RootElement.GetProperty("query").GetString());
        Assert.Equal("""{"action":"remember"}""", MemoryTools.WithAction("not json", "remember"));
        Assert.Equal("""{"action":"find"}""", MemoryTools.WithAction(null, "find"));
    }

    [Fact]
    public async Task ACheckInRemembersFindsUpdatesAndForgetsFactsWhileMemoryIsOn()
    {
        await using var fixture = await LiveFixture.Create();
        var off = await fixture.Controller.CheckInMemoryAsync(Call(MemoryToolSet.Remember, """{"fact":"Likes jazz."}"""), "Actions", default);
        Assert.True(off.IsError);
        Assert.StartsWith("Memory: off.", off.Output);

        await fixture.EnableMemory();
        fixture.Controller.AutoCapture = false;
        var reports = new ConcurrentQueue<MemoryCaptureReport>();
        fixture.Controller.MemoryCaptured += reports.Enqueue;

        var remembered = await fixture.Controller.CheckInMemoryAsync(
            Call(MemoryToolSet.Remember, """{"fact":"The user's dog is called Biscuit.","person":"everyone"}"""), "Actions", default);
        Assert.False(remembered.IsError);
        // The first line shows on the card: the outcome only, never the fact.
        Assert.Equal("Memory: remembered.", remembered.Output.Split('\n')[0]);
        Assert.Equal(MemoryCaptureKind.Remember, Assert.Single(Assert.Single(reports).Changes!).Kind);

        var found = await fixture.Controller.CheckInMemoryAsync(Call(MemoryToolSet.Find, """{"query":"dog"}"""), "Actions", default);
        Assert.Equal("Memory: found 1.", found.Output.Split('\n')[0]);
        var id = JsonDocument.Parse(found.Output.Split('\n')[1]).RootElement.GetProperty("facts")[0].GetProperty("id").GetString();
        Assert.DoesNotContain("Biscuit", found.Output.Split('\n')[0]);

        var updated = await fixture.Controller.CheckInMemoryAsync(
            Call(MemoryToolSet.Update, $$"""{"id":"{{id}}","fact":"The user's dog is called Pepper."}"""), "Actions", default);
        Assert.Equal("Memory: updated.", updated.Output.Split('\n')[0]);
        var revision = (await fixture.Store.LoadAsync()).Settings!.Memory!.ConfigurationRevision;
        var facts = (await fixture.Memory.InspectAsync(revision)).Facts;
        Assert.Equal("The user's dog is called Pepper.", Assert.Single(facts, f => !f.IsHistory).Content);
        // A correction keeps the earlier version as history, which find never lists.
        Assert.Equal("The user's dog is called Biscuit.", Assert.Single(facts, f => f.IsHistory).Content);
        var refound = await fixture.Controller.CheckInMemoryAsync(Call(MemoryToolSet.Find, """{"query":"Biscuit"}"""), "Actions", default);
        Assert.Equal("Memory: found 0.", refound.Output.Split('\n')[0]);

        var refused = await fixture.Controller.CheckInMemoryAsync(Call(MemoryToolSet.Forget, """{"ids":["nope1234"]}"""), "Actions", default);
        Assert.True(refused.IsError);
        Assert.Equal("Memory: refused.", refused.Output.Split('\n')[0]);
        var forgot = await fixture.Controller.CheckInMemoryAsync(Call(MemoryToolSet.Forget, $$"""{"ids":["{{id}}"]}"""), "Actions", default);
        Assert.Equal("Memory: forgot 1.", forgot.Output.Split('\n')[0]);
        Assert.Empty((await fixture.Memory.InspectAsync(revision)).Facts);
        Assert.Equal(3, reports.Count);

        var unknown = await fixture.Controller.CheckInMemoryAsync(Call(MemoryTools.Name, """{"action":"find"}"""), "Actions", default);
        Assert.True(unknown.IsError);
    }

    [Fact]
    public async Task ACheckInLooksAfterMemoryInSeveralStepsWithTheWholeMemoryToolSet()
    {
        await using var fixture = await LiveFixture.Create();
        await fixture.EnableMemory();
        fixture.Controller.AutoCapture = false;
        Task<ConversationToolResult> Run(string tool, string json) =>
            fixture.Controller.CheckInMemoryAsync(Call(tool, json), "Memory care", default).AsTask();
        static string First(ConversationToolResult result) => result.Output.Split('\n')[0];
        static JsonElement Json(ConversationToolResult result) => JsonDocument.Parse(result.Output.Split('\n')[1]).RootElement;
        async Task<string[]> Ids(string query) =>
            [.. Json(await Run(MemoryToolSet.Find, $$"""{"query":"{{query}}"}""")).GetProperty("facts").EnumerateArray().Select(f => f.GetProperty("id").GetString()!)];

        string[] facts = ["The user is building a castle in survival mode.", "The user likes exploring the Nether.",
            "The user's dog is called Biscuit.", "The user has a beagle named Biscuit.", "The user lives in Seattle.", "The user moved to Portland."];
        foreach (var fact in facts)
            Assert.False((await Run(MemoryToolSet.Remember, $$"""{"fact":"{{fact}}","person":"everyone"}""")).IsError);

        // 1. Look: the overview counts without a fact's text, and find pages through all the facts.
        var overview = await Run(MemoryToolSet.Overview, "{}");
        Assert.Equal("Memory: overview of 6.", First(overview));
        Assert.Equal((6, 0), (Json(overview).GetProperty("facts").GetInt32(), Json(overview).GetProperty("history").GetInt32()));
        Assert.DoesNotContain("Biscuit", overview.Output);
        var page = Json(await Run(MemoryToolSet.Find, """{"limit":4}"""));
        Assert.Equal((6, 4, true), (page.GetProperty("count").GetInt32(), page.GetProperty("shown").GetInt32(), page.GetProperty("more").GetBoolean()));
        var last = Json(await Run(MemoryToolSet.Find, """{"limit":4,"skip":4}"""));
        Assert.Equal((2, false), (last.GetProperty("shown").GetInt32(), last.TryGetProperty("more", out _)));

        // 2. Say which game: one note on many facts at once, kept as history, never twice.
        var game = await Ids("castle Nether");
        Assert.Equal(2, game.Length);
        var noted = await Run(MemoryToolSet.Note, $$"""{"ids":["{{game[0]}}","{{game[1]}}"],"note":"(in Minecraft)"}""");
        Assert.Equal("Memory: noted 2.", First(noted));
        var again = await Run(MemoryToolSet.Note, $$"""{"ids":["{{game[0]}}"],"note":"(in Minecraft)"}""");
        Assert.Equal(("Memory: noted 0.", false), (First(again), again.IsError));
        Assert.Contains("already had the note", again.Output);
        var castle = (await Ids("castle")).Single();
        var history = await Run(MemoryToolSet.History, $$"""{"id":"{{castle}}"}""");
        Assert.Equal("The user is building a castle in survival mode (in Minecraft).", Json(history).GetProperty("fact").GetString());
        Assert.Equal("The user is building a castle in survival mode.", Json(history).GetProperty("earlier")[0].GetProperty("said").GetString());

        // 3. Correct many at once: each edit is done or refused on its own.
        var dogs = await Ids("Biscuit");
        var many = await Run(MemoryToolSet.UpdateMany,
            $$"""{"edits":[{"id":"{{dogs[0]}}","fact":"The user's dog Biscuit is a beagle."},{"id":"nope1234","fact":"Wrong."}]}""");
        Assert.Equal(("Memory: updated 1 of 2.", false), (First(many), many.IsError));
        Assert.Contains("2: refused: There is no fact with id nope1234", many.Output);

        // 4. Merge the duplicates, and keep a fact a newer one replaced as history.
        var merged = await Run(MemoryToolSet.Merge, $$"""{"ids":["{{dogs[0]}}","{{dogs[1]}}"],"fact":"The user has a beagle called Biscuit."}""");
        Assert.Equal("Memory: merged 1.", First(merged));
        var outdated = await Run(MemoryToolSet.Outdated, $$"""{"id":"{{(await Ids("Seattle")).Single()}}","by":"{{(await Ids("Portland")).Single()}}"}""");
        Assert.Equal("Memory: marked outdated.", First(outdated));

        var revision = (await fixture.Store.LoadAsync()).Settings!.Memory!.ConfigurationRevision;
        var current = (await fixture.Memory.InspectAsync(revision)).Facts.Where(f => !f.IsHistory).Select(f => f.Content).Order(StringComparer.Ordinal);
        Assert.Equal(["The user has a beagle called Biscuit.", "The user is building a castle in survival mode (in Minecraft).",
            "The user likes exploring the Nether (in Minecraft).", "The user moved to Portland."], current);
        Assert.Empty(await Ids("Seattle"));
        Assert.Equal("Likes the Nether (in Minecraft)!", MemoryTools.WithNote("Likes the Nether!", "(in Minecraft)"));
        Assert.Equal("Likes the Nether. In Minecraft.", MemoryTools.WithNote("Likes the Nether.", "In Minecraft."));
    }

    private static TextToolCall Call(string name, string json) => new("call-1", name, json);
}
