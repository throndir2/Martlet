using System.Reflection;
using System.Text.RegularExpressions;
using Martlet.Conversation.Guides;
using Martlet.Core.Settings;
using Martlet.Providers;

namespace Martlet.Conversation.Tests;

/// <summary>The tools models are offered and the names the prompts tell them to call: they must match, so that a model that
/// follows its prompt never calls a tool that isn't there; and the director never loses a turn or a request.</summary>
public sealed class ToolAuditTests
{
    // Every tool a model can be offered: each check-in set's tools and each public TextToolDefinition of the conversation.
    private static HashSet<string> ToolNames()
    {
        var names = CheckInToolSets.All.SelectMany(s => s.Tools).Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var type in typeof(CheckInToolSets).Assembly.GetTypes().Where(t => !t.ContainsGenericParameters))
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Static))
            if (property.PropertyType == typeof(TextToolDefinition) && property.GetValue(null) is TextToolDefinition tool) names.Add(tool.Name);
            else if (typeof(IEnumerable<TextToolDefinition>).IsAssignableFrom(property.PropertyType) &&
                property.GetValue(null) is IEnumerable<TextToolDefinition> many) names.UnionWith(many.Select(t => t.Name));
        return names;
    }

    [Fact]
    public void EveryToolNameAPromptMentionsIsATool()
    {
        var tools = ToolNames();
        var ids = PromptCatalog.All.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
        var missing = new List<string>();
        foreach (var prompt in PromptCatalog.All)
        {
            // {placeholders} are filled in, not called.
            var text = Regex.Replace(PromptCatalog.Default(prompt.Id), @"\{[^{}]*\}", " ");
            foreach (Match word in Regex.Matches(text, @"\b[a-z]+(?:_[a-z]+)+\b"))
                if (!tools.Contains(word.Value) && !ids.Contains(word.Value)) missing.Add($"{prompt.Id}: {word.Value}");
        }
        Assert.Empty(missing);
    }

    [Fact]
    public void EveryCheckInToolHasADescriptionAndJsonSchema()
    {
        foreach (var tool in CheckInToolSets.All.SelectMany(s => s.Tools))
        {
            Assert.Matches("^[a-z]+(_[a-z]+)*$", tool.Name);
            Assert.False(string.IsNullOrWhiteSpace(tool.Description), tool.Name);
            Assert.Equal("object", System.Text.Json.Nodes.JsonNode.Parse(tool.ParametersJson)?["type"]?.GetValue<string>());
        }
    }

    [Fact]
    public async Task AnInventedToolNameGetsTheRightNameBack()
    {
        var host = new CheckInToolHost([GuideToolSet.SetId], new("director", "Director", null, DateTimeOffset.UnixEpoch),
            new Dictionary<string, CheckInToolHandler> { [GuideToolSet.SetId] = (_, _, _) => new(new ConversationToolResult("Started.")) });
        var wrong = await host.CallAsync(new("1", "update_guide", "{}"), CancellationToken.None);
        Assert.True(wrong.IsError);
        Assert.Contains("Did you mean update_guide_brief?", wrong.Output);
        Assert.Contains("Its tools are: bring_up", CheckInToolHost.Unknown("sing", ["bring_up", "remind_next_reply"]));
        Assert.Contains("no tools", CheckInToolHost.Unknown("x", []));

        // The model corrects the name: the run isn't reported as failing.
        await host.CallAsync(new("2", AppGuideTools.BriefName, "{}"), CancellationToken.None);
        Assert.Null(CheckInToolHost.FirstBroken(host.Uses));
        // Without a call that worked after it, the invented name is what broke.
        Assert.Equal("update_guide", CheckInToolHost.FirstBroken(host.Uses.Take(1).ToArray())?.Tool);
        // A real tool that failed always counts.
        Assert.Equal("research", CheckInToolHost.FirstBroken([new("background-work", "research", "busy", true), new("next-reply", "bring_up", "ok", false)])?.Tool);
    }

    [Fact]
    public void TheDirectorKeepsEveryTurnMadeWhileItWasBusy()
    {
        var waiting = new List<string>();
        Assert.Null(Director.Asked(waiting));
        Director.Wait(waiting, "  start researching Stardew Valley ");
        Director.Wait(waiting, " ");
        Assert.Equal("start researching Stardew Valley", Director.Asked(waiting));
        Director.Wait(waiting, "is it running currently?");
        var asked = Director.Asked(waiting)!;
        Assert.StartsWith("2 things, oldest first.", asked);
        Assert.True(asked.IndexOf("Stardew", StringComparison.Ordinal) < asked.IndexOf("running", StringComparison.Ordinal));
        for (var i = 0; i < 10; i++) Director.Wait(waiting, $"turn {i} " + new string('x', 2_000));
        Assert.Equal(Director.MaximumWaitingAsks, waiting.Count);
        Assert.StartsWith("turn 6", waiting[0]);
        // Clipped so the newest turn still fits in what the director reads (CheckInPrompts.Asked keeps 2,000 characters).
        Assert.True(Director.Asked(waiting)!.Length < 2_000);
        Assert.Contains("- turn 9", CheckInPrompts.Asked(new CheckInState { Now = DateTimeOffset.UnixEpoch, Asked = Director.Asked(waiting) }));
    }
}
