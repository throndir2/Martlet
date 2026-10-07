using Martlet.Conversation;

namespace Martlet.Conversation.Tests;

public sealed class TouchLedgerTests
{
    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    private static PhysicalEvent Pat(double at) => new(PhysicalKind.Pat, S(at), "the top of your head", "top of head");
    private static PhysicalEvent Poke(double at) => new(PhysicalKind.Tap, S(at), "your left cheek", "left cheek");

    [Fact]
    public void BurstsAddUpIntoOnePlainLineAndAShortHistoryLine()
    {
        var ledger = new TouchLedger();
        ledger.Record(Pat(10));
        ledger.Record(Pat(11));
        ledger.Record(Pat(12));
        ledger.Record(Poke(12.5));
        var burst = ledger.Drain(S(13))!;
        Assert.Equal("They patted the top of your head 3 times over 2 seconds, then poked your left cheek once.", burst.Line);
        Assert.Equal("(touch: top of head pat x3, left cheek poke)", burst.HistoryLine);
        Assert.Equal(4, burst.Count);
        Assert.True(burst.StartsTurn);
        Assert.Null(ledger.Drain(S(13)));
    }

    [Fact]
    public void EveryKindHasWordsAndOnlyTouchesStartAReply()
    {
        foreach (var kind in Enum.GetValues<PhysicalKind>())
        {
            Assert.False(string.IsNullOrWhiteSpace(PhysicalKinds.Phrase(kind, "your hair", null)));
            Assert.False(string.IsNullOrWhiteSpace(PhysicalKinds.Short(kind)));
        }
        Assert.True(PhysicalKinds.StartsTurn(PhysicalKind.Stroke));
        Assert.True(PhysicalKinds.StartsTurn(PhysicalKind.Hold));
        Assert.False(PhysicalKinds.StartsTurn(PhysicalKind.Moved));
        Assert.False(PhysicalKinds.StartsTurn(PhysicalKind.Zoomed));

        var ledger = new TouchLedger();
        ledger.Record(new(PhysicalKind.Moved, S(1), Detail: "to another monitor"));
        ledger.Record(new(PhysicalKind.Zoomed, S(2)));
        ledger.Record(new(PhysicalKind.Zoomed, S(3)));
        var burst = ledger.Peek(S(3))!;
        Assert.False(burst.StartsTurn);
        Assert.Equal("They moved you to another monitor, then zoomed in on you twice over 1 second.", burst.Line);
        Assert.Equal("(touch: moved, zoomed x2)", burst.HistoryLine);
        ledger.Record(new(PhysicalKind.Stroke, S(4), Zones: ["your hair", "the top of your head"], Label: "hair", Hint: "*strokes*"));
        Assert.EndsWith("then stroked your hair and the top of your head once (\"*strokes*\").", ledger.Peek(S(4))!.Line);
        ledger.Record(new(PhysicalKind.Hold, S(5), "your left hand", "left hand"));
        Assert.EndsWith("then pressed and held your left hand once.", ledger.Peek(S(5))!.Line);
    }

    [Fact]
    public void ItStaysSmallLetsOldTouchesGoAndTakesBackWhatWasNeverAnswered()
    {
        var ledger = new TouchLedger();
        for (var i = 0; i < TouchLedger.MaximumEntries + 3; i++)
            ledger.Record(i % 2 == 0 ? Pat(i) : Poke(i));
        Assert.Equal(TouchLedger.MaximumEntries, ledger.Peek(S(20))!.Entries.Count);
        Assert.Equal(999, Enumerable.Range(0, 1200).Aggregate(new TouchLedger(), (l, i) => { l.Record(Pat(0)); return l; }).Peek(S(0))!.Count);

        var old = new TouchLedger();
        old.Record(Pat(0));
        Assert.Null(old.Peek(S(0) + TouchLedger.MaximumAge + S(1)));

        var taken = new TouchLedger();
        taken.Record(Pat(0));
        var burst = taken.Drain(S(1))!;
        taken.Record(Poke(2));
        taken.Restore(burst);
        Assert.Equal("They patted the top of your head once, then poked your left cheek once.", taken.Peek(S(3))!.Line);
    }

    [Fact]
    public void TouchesStartAReplyAfterAQuietMomentAtMostThreeSecondsAfterTheFirst()
    {
        var debounce = new TouchDebounce();
        Assert.False(debounce.Waiting);
        debounce.Touched(S(10));
        Assert.Equal(S(11.2), debounce.DueAt);
        debounce.Touched(S(11));
        Assert.False(debounce.Due(S(11.5)));
        Assert.Equal(S(12.2), debounce.DueAt);
        debounce.Touched(S(12));
        debounce.Touched(S(12.9));
        // Each touch moves it on, but never past three seconds after the first.
        Assert.Equal(S(13), debounce.DueAt);
        Assert.True(debounce.Due(S(13)));
        debounce.Started(S(13));
        Assert.False(debounce.Waiting);

        // At most one touch reply every four seconds: later touches wait for it.
        debounce.Touched(S(14));
        Assert.Equal(S(17), debounce.DueAt);
        Assert.False(debounce.Due(S(16)));
        Assert.True(debounce.Due(S(17)));
    }

    [Fact]
    public void TalkingOrTypingCancelsTheTouchReply()
    {
        var debounce = new TouchDebounce();
        debounce.Touched(S(1));
        debounce.Cancel();
        Assert.False(debounce.Waiting);
        Assert.False(debounce.Due(S(10)));
        debounce.Touched(S(20));
        Assert.True(debounce.Due(S(21.2)));
    }

    [Fact]
    public void ATouchReplyTakesNothingElse()
    {
        var plan = MomentTurn.Plan(MomentTrigger.Touch, pcWaiting: true, jobsWaiting: true, lookDue: true);
        Assert.Equal(MomentRoute.Touch, plan.Route);
        Assert.False(plan.Combined);
        Assert.Equal("your words and 2 touches", MomentTurn.Describe(true, 0, false, null, 0, touches: 2));
    }
}
