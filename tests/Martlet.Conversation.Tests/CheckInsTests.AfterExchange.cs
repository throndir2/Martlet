using Martlet.Core.Settings;

namespace Martlet.Conversation.Tests;

public sealed partial class CheckInsTests
{
    [Fact]
    public void ActOnWhatWasSaidRunsAfterEachExchangeWithTheSetsThatReplaceReplyTools()
    {
        var actions = Built(CheckIns.Actions);
        Assert.Equal((true, 1, CheckInOutcome.Tools, PromptCatalog.CheckInActions, CheckInTriggers.ExchangeEnded),
            (actions.On, actions.EveryMinutes, actions.Outcome, actions.PromptId, actions.Triggers));
        Assert.Equal(CheckInFacts.Latest | CheckInFacts.Work, actions.Facts);
        Assert.Equal(CheckInConditions.SomethingNew, actions.Conditions);
        // Its sets are computed: every set that takes over reply tools.
        Assert.Equal(CheckInToolSets.All.Where(s => s.Replaces.Count > 0).Select(s => s.Id), actions.ToolSets);
        Assert.Equal(CheckInFacts.Latest | CheckInFacts.Work, CheckIns.Placed(PromptSettings.Text(null, PromptCatalog.CheckInActions)));
        Assert.Equal("a reply to end", CheckIns.TriggerWords(CheckInTriggers.ExchangeEnded));
        Assert.Equal(CheckIns.ByTouches | CheckInTriggers.ExchangeEnded, CheckIns.AllTriggers);
    }

    [Fact]
    public void AnExchangeTriggerCountsTheExchangesItKeepsAndWaitsLongerThanTouches()
    {
        var first = CheckIns.ExchangeTrigger(Now.AddSeconds(-20));
        var second = CheckIns.ExchangeTrigger(Now, first);
        Assert.Equal((CheckInTriggers.ExchangeEnded, "1 exchange"), (first.Fired, first.What));
        Assert.Equal((Now, "2 exchanges"), (second.At, second.What));
        Assert.Equal("3 exchanges", CheckIns.ExchangeTrigger(Now, second).What);
        Assert.Equal("1 exchange", CheckIns.ExchangeTrigger(Now, new(CheckInTriggers.TouchesEnded, Now, "4 touches")).What);
        Assert.Equal(CheckIns.ExchangeAge, CheckIns.Age(first));
        Assert.Equal(CheckIns.TriggerAge, CheckIns.Age(new(CheckInTriggers.TouchesEnded, Now, "1 touch")));

        var actions = Built(CheckIns.Actions);
        Assert.True(CheckIns.Fires(actions, first with { At = Now.AddMinutes(-5) }, Now));
        Assert.False(CheckIns.Fires(actions, first with { At = Now - CheckIns.ExchangeAge - TimeSpan.FromSeconds(1) }, Now));
        Assert.False(CheckIns.Fires(actions, new(CheckInTriggers.TouchesEnded, Now, "1 touch"), Now));
        Assert.False(CheckIns.Fires(Built(CheckIns.Reactions), first, Now));
    }

    [Fact]
    public void TheLatestFactReadsEveryExchangeSinceTheCheckInLastRan()
    {
        Assert.Equal("The latest exchange:\nUser: This bug is driving me crazy.\nMira: Ooh, want to talk it through?", CheckIns.Latest(State()));
        var since = CheckIns.Latest(State() with { Since = 2 });
        Assert.StartsWith("The latest 2 exchanges, oldest first:\nUser: Back to work.", since);
        Assert.DoesNotContain("oven", since);
        Assert.Contains("check the oven", CheckIns.Latest(State() with { Since = 0 }));
        Assert.Equal("Nothing has been said in this conversation yet.", CheckIns.Latest(State() with { Exchanges = [], Exchanged = 0 }));
        var message = CheckIns.Message(Built(CheckIns.Actions), State() with { Since = 3 }, null)!;
        Assert.Contains("User: This bug is driving me crazy.", message);
        Assert.Contains("a trip plan", message);
        Assert.Contains("Use your tools for what needs doing", message);
        Assert.DoesNotContain("{exchange}", message);
    }

    [Fact]
    public void TheReplyHandsToolsOffOnlyWhileTheCheckInIsOnAndThePoolCallsTools()
    {
        var replaced = CheckInToolSets.All.Where(s => s.Replaces.Count > 0).SelectMany(s => s.Replaces).Distinct(StringComparer.Ordinal);
        Assert.Equal(replaced, CheckIns.HandOffs(null, null, poolCallsTools: true).Select(h => h.Tool));
        Assert.Empty(CheckIns.HandOffs(null, null, poolCallsTools: false));
        var off = new CheckInSettings().With(CheckIns.Actions, false, 1);
        Assert.Empty(CheckIns.HandOffs(off, null, poolCallsTools: true));
        // The pool is asked only when a check-in would take a tool over.
        var asked = false;
        Assert.Empty(CheckIns.HandOffs(off, null, () => asked = true));
        Assert.False(asked);
        // An emptied prompt never runs, so it takes nothing over.
        var empty = new PromptSettings { Overrides = new Dictionary<string, string> { [PromptCatalog.CheckInActions] = "" } };
        Assert.Empty(CheckIns.HandOffs(null, empty, poolCallsTools: true));
    }

    [Fact]
    public void TheReplyIsToldBrieflyThatHandedOffThingsHappenAfterIt()
    {
        CheckInHandOff Hand(string tool, string set) => new(tool, CheckIns.Actions, "Act on what was said", set.ToLowerInvariant(), set);
        Assert.Null(CheckIns.HandOffGuidance([], null));
        var one = CheckIns.HandOffGuidance([Hand("reminders", "Reminders")], null)!;
        Assert.StartsWith("Some things you do happen on their own right after your reply: Reminders.", one);
        Assert.Contains("say briefly", one);
        Assert.Contains(": Reminders; Memory; Songs, pictures and creations.",
            CheckIns.HandOffGuidance([Hand("reminders", "Reminders"), Hand("manage_memories", "Memory"), Hand("x", "Memory"), Hand("sing_song", "Songs, pictures and creations")], null));
        Assert.Null(CheckIns.HandOffGuidance([Hand("reminders", "Reminders")], new PromptSettings { Overrides = new Dictionary<string, string> { [PromptCatalog.HandedOffTools] = "" } }));
    }
}
