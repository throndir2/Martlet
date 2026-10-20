using Martlet.Core.Settings;

namespace Martlet.Conversation.Tests;

public sealed partial class CheckInsTests
{
    [Fact]
    public void ThinkAheadRunsAfterEachReplyAndPlansForTheNextOne()
    {
        var ahead = Built(CheckIns.Ahead);
        Assert.Equal((true, 1, CheckInOutcome.Plan, PromptCatalog.CheckInAhead, CheckInTriggers.ExchangeEnded),
            (ahead.On, ahead.EveryMinutes, ahead.Outcome, ahead.PromptId, ahead.Triggers));
        Assert.Equal(CheckInFacts.Persona | CheckInFacts.Conversation | CheckInFacts.Callbacks, ahead.Facts);
        Assert.Equal(CheckInFacts.Persona | CheckInFacts.Conversation | CheckInFacts.Callbacks,
            CheckInPrompts.Placed(PromptSettings.Text(null, PromptCatalog.CheckInAhead)));
        Assert.Equal(CheckInConditions.Talked | CheckInConditions.SomethingNew, ahead.Conditions);

        Assert.Equal("it waits for a reply to end", CheckInSchedule.Wait(ahead, State(), null));
        var fired = CheckIns.ExchangeTrigger(Now);
        // The user may already talk again: no reply waits for it, and it never waits for the conversation to settle.
        Assert.Null(CheckInSchedule.Wait(ahead, State() with { Quiet = TimeSpan.Zero }, null, fired: fired));
        Assert.Equal("next in 50 s", CheckInSchedule.Wait(ahead, State(), new(Now.AddSeconds(-10), 3, "x", false), fired: fired));

        var message = CheckInPrompts.Message(ahead, State(), null)!;
        Assert.Contains("Think ahead as Mira", message);
        Assert.Contains("Mira is playful and teasing.", message);
        Assert.Contains("User: This bug is driving me crazy.", message);
        Assert.Contains("starts with PLAN:", message);
        Assert.Contains("write only: OK", message);
        Assert.DoesNotContain("{persona}", message);
        Assert.DoesNotContain("KNOW:", message);
    }

    [Fact]
    public void ThoughtsAheadAreReadAndGoWithOneRequestUntilAnotherExchangeEnds()
    {
        var ahead = Built(CheckIns.Ahead);
        var read = CheckInAnswers.Read(ahead, "<think>They'll vent.</think>\n- **PLAN:** They'll say it's fixed; I'll cheer and tease them.", State());
        Assert.Equal((true, true, "They'll say it's fixed; I'll cheer and tease them."), (read.Act, read.Readable, read.Text));
        Assert.Equal((false, true), (CheckInAnswers.Read(ahead, "OK", State()).Act, CheckInAnswers.Read(ahead, "OK", State()).Readable));
        Assert.False(CheckInAnswers.Read(ahead, "PLAN: nothing to plan.", State()).Act);
        Assert.False(CheckInAnswers.Read(ahead, "KNOW: They code.", State()).Readable);

        var note = CheckIns.Plan(null, read.Text!)!;
        Assert.StartsWith("Your own thoughts from just before this message (private; the user may have said something else): They'll say it's fixed;", note);
        Assert.Contains("make that move", note);
        Assert.Contains("Never mention them", note);
        Assert.Null(CheckIns.Plan(new PromptSettings { Overrides = new Dictionary<string, string> { [PromptCatalog.CheckInPlan] = "" } }, "x"));

        var board = new ContextBoard();
        var source = CheckIns.Source(CheckIns.Ahead);
        board.Post(source, note, Now, CheckIns.PlanAge, consume: true);
        board.Post(CheckIns.Source(CheckIns.Promises), "A reminder.", Now, CheckIns.NoteAge, consume: true);
        var first = board.Snapshot(Now.AddSeconds(20));
        Assert.Contains(source, first.Sources);
        board.MarkSent(first);
        Assert.DoesNotContain(source, board.Snapshot(Now.AddSeconds(30)).Sources);

        // Thoughts that wait when another exchange ends were about the message before it: they go, other notes stay.
        board.Post(source, note, Now, CheckIns.PlanAge, consume: true);
        board.Post(CheckIns.Source(CheckIns.Promises), "A reminder.", Now, CheckIns.NoteAge, consume: true);
        Assert.Equal(1, CheckIns.DropPlans(board, CheckIns.All(null)));
        Assert.Equal([CheckIns.Source(CheckIns.Promises)], board.Snapshot(Now.AddSeconds(40)).Sources);
        Assert.Equal(0, CheckIns.DropPlans(board, CheckIns.All(null)));
        board.Post(source, note, Now, CheckIns.PlanAge, consume: true);
        Assert.DoesNotContain(source, board.Snapshot(Now + CheckIns.PlanAge + TimeSpan.FromSeconds(1)).Sources);
    }

    [Fact]
    public void AnOwnCheckInCanThinkAhead()
    {
        var own = new CustomCheckIn
        {
            Id = "c1", Name = "Plans", On = true, EveryMinutes = 5, Task = "Plan the next reply as {name}.", Outcome = CheckInOutcome.Plan,
            Triggers = CheckInTriggers.ExchangeEnded
        };
        new CheckInSettings().With(own).Validate();
        var checkIn = CheckIns.Of(own);
        Assert.Equal("Your own check-in: what it thinks ahead goes with Martlet's next reply.", checkIn.Does);
        Assert.Contains("starts with PLAN:", CheckInPrompts.Message(checkIn, State(), null));
        var board = new ContextBoard();
        board.Post(CheckIns.Source("c1"), "x", Now, CheckIns.PlanAge, consume: true);
        Assert.Equal(1, CheckIns.DropPlans(board, [checkIn, Built(CheckIns.Ahead)]));
    }
}
