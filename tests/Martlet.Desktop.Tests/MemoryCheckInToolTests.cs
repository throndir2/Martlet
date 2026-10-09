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
        Assert.Equal(["find", "remember", "update", "forget"], MemoryToolSet.Set.Tools.Select(t => MemoryToolSet.Action(t.Name)));
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
        Assert.Equal("The user's dog is called Pepper.", Assert.Single((await fixture.Memory.InspectAsync(revision)).Facts).Content);

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

    private static TextToolCall Call(string name, string json) => new("call-1", name, json);
}
