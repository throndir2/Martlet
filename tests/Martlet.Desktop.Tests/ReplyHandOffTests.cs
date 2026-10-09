using Martlet.Conversation;
using Martlet.Desktop;
using Martlet.Providers;

namespace Martlet.Desktop.Tests;

/// <summary>The reply tools a check-in that runs after each exchange takes over (LiveConversationController.WithoutHandedOff):
/// they leave the reply's own tools, and what the reply is told instead still reaches it when no tool is left.</summary>
public sealed class ReplyHandOffTests
{
    private static (TextToolDefinition, Func<TextToolCall, CancellationToken, ValueTask<ConversationToolResult>>) Tool(string name) =>
        (new(name, "FIXTURE tool.", """{"type":"object","properties":{},"additionalProperties":false}"""),
            (_, _) => ValueTask.FromResult(new ConversationToolResult("done")));

    private static CheckInHandOff Hand(string tool) => new(tool, CheckIns.Actions, "Act on what was said", "fixture", "Fixture");

    [Fact]
    public void HandedOffToolsLeaveTheReplyAndTheGuidanceIsAddedAfterTheSetsOwn()
    {
        var own = new BuiltInTools([Tool("search_conversations"), Tool("manage_memories"), Tool("reminders")], "Own guidance.");
        var kept = LiveConversationController.WithoutHandedOff(own, [Hand("manage_memories"), Hand("reminders")], "After the reply.")!;
        Assert.Equal(["search_conversations"], kept.Tools.Select(t => t.Definition.Name));
        Assert.Equal("Own guidance.\n\nAfter the reply.", kept.Guidance);
    }

    [Fact]
    public void WithEveryToolHandedOffTheGuidanceStillReachesTheReply()
    {
        var own = new BuiltInTools([Tool("call_on_discord")], null);
        var left = LiveConversationController.WithoutHandedOff(own, [Hand("call_on_discord")], "After the reply.")!;
        Assert.Empty(left.Tools);
        Assert.Equal("After the reply.", left.Guidance);
        // A set's own line with no tool left (it said the reply will call) stays too.
        Assert.Equal("Set line.\n\nAfter the reply.",
            LiveConversationController.WithoutHandedOff(new([], "Set line."), [Hand("call_on_discord")], "After the reply.")!.Guidance);
        Assert.Null(LiveConversationController.WithoutHandedOff(new([], null), [Hand("call_on_discord")], null));
    }

    [Fact]
    public void WithoutHandOffsTheReplyKeepsEveryToolAsBefore()
    {
        var own = new BuiltInTools([Tool("reminders")], "Own guidance.");
        Assert.Same(own, LiveConversationController.WithoutHandedOff(own, [], "After the reply."));
        Assert.Null(LiveConversationController.WithoutHandedOff(new([], null), [], null));
    }
}
