using Martlet.Core.Settings;

namespace Martlet.Conversation.Tests;

public sealed partial class CheckInsTests
{
    [Fact]
    public void TheCallbackListKeepsWhenEachThingCameUpAndForgetsWithTheConversation()
    {
        var list = new CallbackList();
        Assert.Empty(list.Recent(Now));
        list.Keep(["open - stuck on the final boss", "joke - Mira secretly owns the fridge"], Now.AddMinutes(-40));
        // The same thing again (any case, spaces or a closing period) keeps when it came up; a new one came up now.
        var kept = list.Keep(["Joke -  Mira secretly owns the fridge.", "quote - they said \"one more try\"", "quote - they said \"one more try\""], Now)!;
        Assert.Equal(["Joke - Mira secretly owns the fridge.", "quote - they said \"one more try\""], kept.Select(c => c.Text));
        Assert.Equal([Now.AddMinutes(-40), Now], kept.Select(c => c.Since));
        Assert.Equal("- 9:37 PM (40 min ago): Joke - Mira secretly owns the fridge.\n- 10:17 PM (0 s ago): quote - they said \"one more try\"",
            CallbackList.Lines(kept, Now));

        // At most 8 things, each cut to 160 characters.
        var many = CallbackList.Merge([], [.. Enumerable.Range(1, 12).Select(i => $"fact - thing {i}"), new string('x', 400)], Now);
        Assert.Equal(CallbackList.MaximumItems, many.Count);
        Assert.True(CallbackList.Merge([], [new string('x', 400)], Now).Single().Text.Length <= CallbackList.MaximumCharacters + 1);

        // A list nobody wrote for two hours is stale; a forgotten conversation's answer writes nothing.
        Assert.Empty(list.Recent(Now + CallbackList.Window + TimeSpan.FromMinutes(1)));
        var version = list.Version;
        list.Clear();
        Assert.Empty(list.Recent(Now));
        Assert.Null(list.Keep(["joke - late"], Now, version));
        Assert.Equal(0, list.Count);
        Assert.NotNull(list.Keep(["joke - fresh"], Now, list.Version));
        Assert.Empty(list.Keep([], Now)!);
    }

    [Fact]
    public void CallbackListRunsAfterEachReplyAndItsAnswerIsTheWholeNewList()
    {
        var keeper = Built(CheckIns.Callbacks);
        Assert.Equal((true, 1, CheckInOutcome.Callbacks, PromptCatalog.CheckInCallbacks, CheckInTriggers.ExchangeEnded),
            (keeper.On, keeper.EveryMinutes, keeper.Outcome, keeper.PromptId, keeper.Triggers));
        Assert.Equal(CheckInFacts.Latest | CheckInFacts.Callbacks, keeper.Facts);
        Assert.Equal(CheckInFacts.Latest | CheckInFacts.Callbacks, CheckIns.Placed(PromptSettings.Text(null, PromptCatalog.CheckInCallbacks)));
        Assert.Equal("it waits for a reply to end", CheckIns.Wait(keeper, State(), null));
        Assert.Null(CheckIns.Wait(keeper, State(), null, fired: CheckIns.ExchangeTrigger(Now)));

        var state = State() with { Callbacks = [new(Now.AddMinutes(-40), "open - stuck on the final boss")] };
        var message = CheckIns.Message(keeper, state, null)!;
        Assert.Contains("You keep Mira's callback list", message);
        Assert.Contains("- 9:37 PM (40 min ago): open - stuck on the final boss", message);
        Assert.Contains("User: This bug is driving me crazy.", message);
        Assert.Contains("each line starting with CALLBACK:", message);
        Assert.Contains("write only: KEEP", message);
        Assert.DoesNotContain("{callbacks}", message);
        Assert.Contains("Mira's callback list is empty.", CheckIns.Facts(CheckInFacts.Callbacks, State()));
        Assert.Contains("(40 min ago): open - stuck", CheckIns.Facts(CheckInFacts.Callbacks, state));

        var read = CheckIns.Read(keeper, "<think>Keep the boss.</think>\nCALLBACKS:\n- **CALLBACK:** open - stuck on the final boss\n" +
            "- CALLBACK: quote - they said \"one more try\"\nCALLBACK: quote - they said \"one more try\"\nCALLBACK: nothing", state);
        Assert.Equal((true, true), (read.Act, read.Readable));
        Assert.Equal(["open - stuck on the final boss", "quote - they said \"one more try\""], read.Tags);
        Assert.Equal((false, true), CheckIns.Read(keeper, "KEEP", state) is var keep ? (keep.Act, keep.Readable) : default);
        Assert.Equal((true, 0), CheckIns.Read(keeper, "NONE", state) is var none ? (none.Act, none.Tags.Count) : default);
        Assert.False(CheckIns.Read(keeper, "Here is the list.", state).Readable);

        // Your own check-in can keep the list too.
        var own = new CustomCheckIn { Id = "c1", Name = "Bits", On = true, Task = "List the bits.", Outcome = CheckInOutcome.Callbacks };
        new CheckInSettings().With(own).Validate();
        Assert.Equal("Your own check-in: what it lists becomes Martlet's callback list.", CheckIns.Of(own).Does);
    }

    [Fact]
    public void QuietMomentsPicksAQuietSpellBackUpOnceWithTheUserAtThePc()
    {
        var lull = Built(CheckIns.Lull);
        Assert.Equal((true, 10, CheckInOutcome.Say, PromptCatalog.CheckInLull, 2), (lull.On, lull.EveryMinutes, lull.Outcome, lull.PromptId, lull.MostPerHour));
        Assert.Equal(CheckInConditions.Lull | CheckInConditions.NotOnCall | CheckInConditions.Between, lull.Conditions);
        Assert.True(Built(CheckIns.Unanswered).On);

        // 8:17 PM, quiet for 5 minutes, the user at the PC, something on the list, Martlet's last remark asked nothing.
        var evening = State() with
        {
            Now = Now.AddHours(-2), Quiet = TimeSpan.FromMinutes(5), Callbacks = [new(Now.AddHours(-3), "quote - they said \"one more try\"")]
        };
        Assert.Null(CheckIns.Wait(lull, evening, null));
        Assert.Equal("it waits for 3 min of quiet", CheckIns.Wait(lull, evening with { Quiet = TimeSpan.FromMinutes(1) }, null));
        Assert.Equal("the conversation has been quiet for more than 30 min", CheckIns.Wait(lull, evening with { Quiet = TimeSpan.FromMinutes(45) }, null));
        Assert.Equal("the callback list is empty", CheckIns.Wait(lull, evening with { Callbacks = [] }, null));
        Assert.Equal("the callback list is empty", CheckIns.Wait(lull, evening with { Callbacks = [] }, null, now: true));
        Assert.Equal("you aren't at the PC", CheckIns.Wait(lull, evening with { Away = TimeSpan.FromMinutes(5) }, null));
        Assert.Equal("you're on a call", CheckIns.Wait(lull, evening with { OnCall = true }, null));
        Assert.Equal("it runs only between 8 AM and 10 PM", CheckIns.Wait(lull, evening with { Now = Now }, null));
        Assert.Equal("it already picked this quiet spell up",
            CheckIns.Wait(lull, evening with { Quiet = TimeSpan.FromMinutes(15) }, new(evening.Now.AddMinutes(-12), 4, "x", false)));
        Assert.Null(CheckIns.Wait(lull, evening, new(evening.Now.AddMinutes(-20), 4, "x", false)));
        var asked = evening with { Said = [.. evening.Said, new(evening.Now.AddMinutes(-5), "So, which boss is next?")] };
        Assert.StartsWith("Martlet's last remark asked something", CheckIns.Wait(lull, asked, null));

        var message = CheckIns.Message(lull, evening, null)!;
        Assert.Contains("pick the conversation back up", message);
        Assert.Contains("quote - they said \"one more try\"", message);
        Assert.Contains("starts with SAY:", message);
        Assert.Equal("So did that \"one more try\" ever end?", CheckIns.Read(lull, "SAY: So did that \"one more try\" ever end?", evening).Text);
    }

    [Fact]
    public void TheRepeatCheckLetsCallbacksThroughAndRunningJokesAreRemembered()
    {
        Assert.Contains("A callback is not a repeat", CheckIns.Message(Built(CheckIns.Repeats), State(), null));
        Assert.Contains("running joke", PromptCatalog.DefaultMemoryCaptureInstructions);
        Assert.Contains("running jokes", PromptCatalog.DefaultMemoryBriefWritingInstructions);
        Assert.Contains("example dialogue", PromptCatalog.DefaultCharacterWritingInstructions);
    }
}
