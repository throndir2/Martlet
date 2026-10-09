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

    // The talk window's note for a reply to touches alone names the character as the persona it is ("Martlet" without one).
    [Fact]
    public void TheTalkWindowsNoteNamesThePersonaThatWasTouched()
    {
        var ledger = new TouchLedger();
        ledger.Record(Pat(1));
        ledger.Record(Poke(2));
        var burst = ledger.Peek(S(2))!;
        Assert.Equal("You touched Ivy (touch: top of head pat, left cheek poke)", burst.Note("Ivy"));
        Assert.Equal("You touched Jane Doe (sarcastic) (touch: top of head pat, left cheek poke)", burst.Note(" Jane Doe (sarcastic) "));
        Assert.Equal("You touched Martlet (touch: top of head pat, left cheek poke)", burst.Note(null));
        Assert.Equal("You touched Martlet (touch: top of head pat, left cheek poke)", TouchWording.Note("  ", burst.Entries));
    }

    [Fact]
    public void StrokesAndMovesReadPlainly()
    {
        var ledger = new TouchLedger();
        for (var pass = 0; pass < 4; pass++) ledger.Record(new(PhysicalKind.Stroke, S(1), "your hair", "hair", "slowly"));
        ledger.Record(new(PhysicalKind.Moved, S(2), Detail: "to their other monitor"));
        ledger.Record(new(PhysicalKind.Zoomed, S(3), Detail: "in on your face"));
        var burst = ledger.Drain(S(3))!;
        Assert.Equal("They slowly stroked your hair 4 times, then moved you to their other monitor, then zoomed in on your face.", burst.Line);
        Assert.Equal("(touch: hair stroke x4, moved, zoomed)", burst.HistoryLine);
        Assert.True(burst.StartsTurn);
        Assert.Equal("stroked your hair back and forth", PhysicalKinds.Phrase(PhysicalKind.Stroke, "your hair", "back and forth"));
    }

    [Fact]
    public void EveryKindHasWordsAndOnlyTouchesAndMovesStartAReply()
    {
        foreach (var kind in Enum.GetValues<PhysicalKind>())
        {
            Assert.False(string.IsNullOrWhiteSpace(PhysicalKinds.Phrase(kind, "your hair", null)));
            Assert.False(string.IsNullOrWhiteSpace(PhysicalKinds.Short(kind)));
        }
        Assert.True(PhysicalKinds.StartsTurn(PhysicalKind.Stroke));
        Assert.True(PhysicalKinds.StartsTurn(PhysicalKind.Hold));
        // Being moved around is handled like a touch, though it isn't one; the rest of what the user does to the window only
        // goes with the next reply.
        Assert.True(PhysicalKinds.StartsTurn(PhysicalKind.Moved));
        Assert.False(PhysicalKinds.IsTouch(PhysicalKind.Moved));
        Assert.False(PhysicalKinds.StartsTurn(PhysicalKind.Zoomed));
        Assert.False(PhysicalKinds.StartsTurn(PhysicalKind.Hidden));

        var ledger = new TouchLedger();
        ledger.Record(new(PhysicalKind.Zoomed, S(2)));
        ledger.Record(new(PhysicalKind.Zoomed, S(3)));
        Assert.False(ledger.Peek(S(3))!.StartsTurn);
        ledger.Record(new(PhysicalKind.Moved, S(3), Detail: "to another monitor"));
        var burst = ledger.Peek(S(3))!;
        Assert.True(burst.StartsTurn);
        Assert.Equal(1, burst.Touches);
        Assert.Equal("They zoomed in on you twice over 1 second, then moved you to another monitor.", burst.Line);
        Assert.Equal("(touch: zoomed x2, moved)", burst.HistoryLine);
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
    public void ATouchThatStoppedMartletIsAnsweredSoonerAndWithoutTheCooldown()
    {
        var debounce = new TouchDebounce();
        debounce.Touched(S(10));
        debounce.Started(S(10));
        // Without it, the next reply would wait for the cooldown (14 s).
        debounce.Touched(S(11));
        Assert.Equal(S(14), debounce.DueAt);
        debounce.CutIn();
        Assert.True(debounce.CutInWaiting);
        Assert.Equal(S(11.5), debounce.DueAt);
        debounce.Touched(S(11.3));
        Assert.Equal(S(11.8), debounce.DueAt);
        debounce.Started(S(12));
        Assert.False(debounce.CutInWaiting);
        // The next touches wait as usual again.
        debounce.Touched(S(13));
        Assert.Equal(S(16), debounce.DueAt);
        // With nothing waiting there is nothing to hurry.
        new TouchDebounce().CutIn();
    }

    [Fact]
    public void OnlyTouchesStopMartletAndOnlyTheChosenOnes()
    {
        var poke = new PhysicalEvent(PhysicalKind.Tap, S(1), "your nose", "nose");
        var groin = new PhysicalEvent(PhysicalKind.Stroke, S(1), "your groin", "groin", Intimate: true);
        var moved = new PhysicalEvent(PhysicalKind.Moved, S(1), Detail: "to the left");
        Assert.True(PhysicalKinds.Interrupts(TouchInterrupts.Any, poke));
        Assert.True(PhysicalKinds.Interrupts(TouchInterrupts.Any, groin));
        Assert.False(PhysicalKinds.Interrupts(TouchInterrupts.Any, moved));
        Assert.False(PhysicalKinds.Interrupts(TouchInterrupts.Intimate, poke));
        Assert.True(PhysicalKinds.Interrupts(TouchInterrupts.Intimate, groin));
        Assert.False(PhysicalKinds.Interrupts(TouchInterrupts.Never, groin));
    }

    [Fact]
    public void HowThePersonaFeelsGoesWithTheTouchAndTheOwnersWords()
    {
        var ledger = new TouchLedger();
        ledger.Record(new(PhysicalKind.Pat, S(1), "the top of your head", "top of head", Feeling: "you love being touched there"));
        Assert.Equal("They patted the top of your head once (you love being touched there).", ledger.Peek(S(1))!.Line);
        ledger.Record(new(PhysicalKind.Pat, S(2), "the top of your head", "top of head", Hint: "*ruffles your hair*"));
        Assert.Equal("They patted the top of your head twice over 1 second (\"*ruffles your hair*\"; you love being touched there).",
            ledger.Peek(S(2))!.Line);
    }

    [Fact]
    public void PlacesTheUserKeepsComingBackToAreNamedAcrossReplies()
    {
        var ledger = new TouchLedger();
        PhysicalEvent Groin(double at) => new(PhysicalKind.Tap, S(at), "your groin", "groin", Intimate: true);
        for (var i = 0; i < 3; i++) ledger.Record(Groin(i));
        // A burst on its own already says how many times.
        Assert.Null(ledger.Drain(S(3))!.Often);
        for (var i = 0; i < 3; i++) ledger.Record(Groin(30 + i));
        var burst = ledger.Peek(S(33))!;
        Assert.Equal([("your groin", 6)], burst.Often!.Select(h => (h.Place, h.Count)));
        Assert.Equal("They poked your groin 3 times over 2 seconds. They keep coming back to your groin: 6 times in the last minute.", burst.Line);
        Assert.Equal("(touch: groin poke x3)", burst.HistoryLine);
        ledger.Drain(S(33));

        // A stroke counts for each zone it crossed; moving the character counts for none.
        ledger.Record(new(PhysicalKind.Stroke, S(200), "down from your stomach to your groin", "stomach → groin", Zones: ["your stomach", "your groin"]));
        ledger.Record(new(PhysicalKind.Moved, S(201), Detail: "to the left"));
        Assert.EndsWith("They keep coming back to your groin: 7 times in the last 4 minutes.", ledger.Peek(S(201))!.Line);

        // What is older than ten minutes no longer counts.
        ledger.Drain(S(201));
        ledger.Record(Groin(900));
        Assert.Null(ledger.Peek(S(900))!.Often);
        ledger.Clear();
        ledger.Record(Groin(901));
        Assert.Null(ledger.Peek(S(901))!.Often);
    }

    [Fact]
    public void ATouchThatStoppedMartletGoesWithTheNextBurstAndTellsWhatItWasSaying()
    {
        var ledger = new TouchLedger();
        ledger.Record(Poke(10));
        ledger.CutIn(new("Once upon a time there was a fox.", "Tell me a story.", S(10)));
        var burst = ledger.Drain(S(11))!;
        Assert.NotNull(burst.Cut);
        Assert.Equal("They poked your left cheek once. They did it while you were talking, so you stopped mid-sentence. You had said, " +
            "out loud: \"Once upon a time there was a fox.\" You were answering their message: \"Tell me a story.\" Decide for yourself " +
            "how to go on: react to it first, then pick up where you left off, change course, or leave the rest unsaid, as you would.",
            TouchWording.Told(null, burst));
        // The next touches don't carry it; a reply that never answered puts it back.
        ledger.Record(Poke(12));
        Assert.Null(ledger.Peek(S(12))!.Cut);
        ledger.Restore(burst);
        Assert.Equal(burst.Cut, ledger.Peek(S(12))!.Cut);
        // A remark it was making has no message it answered; what it said goes alone, and an emptied prompt leaves the line.
        var remark = new TouchBurst([new(PhysicalKind.Tap, "your nose", "nose", null, null, 1, S(1), S(1))], Cut: new("Nice shot!", null, S(1)));
        Assert.EndsWith("You had said, out loud: \"Nice shot!\" Decide for yourself how to go on: react to it first, then pick up where you " +
            "left off, change course, or leave the rest unsaid, as you would.", TouchWording.Told(null, remark));
        var emptied = Martlet.Core.Settings.PromptSettings.Normalize(new Dictionary<string, string> { [Martlet.Core.Settings.PromptCatalog.TouchedCutIn] = "" });
        Assert.Equal(remark.Line, TouchWording.Told(emptied, remark));
        // Too old, it is let go with the touches.
        ledger.Clear();
        ledger.Record(Poke(0));
        ledger.CutIn(new("Hello.", null, S(0)));
        Assert.Null(ledger.Peek(S(0) + TouchLedger.MaximumAge + S(1)));
    }

    [Fact]
    public void OnlyTheEndOfALongReplyIsKept()
    {
        var said = string.Join(" ", Enumerable.Repeat("word", 200)) + " and the end.";
        var tail = TouchCut.Tail(said)!;
        Assert.StartsWith("…", tail);
        Assert.EndsWith("and the end.", tail);
        Assert.True(tail.Length <= TouchCut.MaximumCharacters + 1);
        Assert.Null(TouchCut.Tail("  "));
        Assert.Equal("Short.", TouchCut.Tail(" Short. "));
    }

    [Fact]
    public void ATouchReplyTakesNothingElse()
    {
        var plan = MomentTurn.Plan(MomentTrigger.Touch, pcWaiting: true, jobsWaiting: true, lookDue: true);
        Assert.Equal(MomentRoute.Touch, plan.Route);
        Assert.False(plan.Combined);
        Assert.Equal("your words and 2 touches", MomentTurn.Describe(true, 0, false, null, 0, touches: 2));
    }

    // A check-in's Touches fact reads the recent history without taking it: replies still drain what waits, exactly as before,
    // and the history keeps what replies took, for OftenWindow and at most MaximumHistory runs.
    [Fact]
    public void TheHistoryIsReadWithoutTakingAndOutlivesReplies()
    {
        PhysicalEvent Stroke(double at) => new(PhysicalKind.Stroke, S(at), "down from your tail to your groin", "tail → groin", "slowly",
            Zones: ["your tail", "your groin"], Intimate: true, Feeling: "you love being touched there");
        var ledger = new TouchLedger();
        Assert.Null(ledger.History(S(0)));
        ledger.Record(Pat(10));
        ledger.Record(Pat(11));
        Assert.Equal("They patted the top of your head twice over 1 second.", ledger.Drain(S(12))!.Line);
        ledger.Record(Stroke(100));
        ledger.Record(Stroke(101));
        ledger.Record(Stroke(102));
        var waiting = ledger.Peek(S(103))!.Line;
        var history = ledger.History(S(103))!;
        Assert.Equal(waiting, ledger.Peek(S(103))!.Line);
        Assert.Equal(S(103), history.Now);
        Assert.Equal(5, history.Count);
        Assert.Equal(3, history.Intimate);
        Assert.Equal(["They patted the top of your head twice over 1 second.",
            "They slowly stroked down from your tail to your groin 3 times over 2 seconds (you love being touched there)."],
            history.Entries.Select(e => TouchWording.Line([e])));
        Assert.Null(history.Often);
        var taken = ledger.Drain(S(103))!;
        Assert.Equal(waiting, taken.Line);
        Assert.Equal(2, ledger.History(S(103))!.Entries.Count);

        // The same thing, in the same place, adds to its run only within MaximumAge; places touched often are named.
        ledger.Record(Stroke(102 + TouchLedger.MaximumAge.TotalSeconds + 1));
        ledger.Record(Stroke(102 + TouchLedger.MaximumAge.TotalSeconds + 2));
        var later = ledger.History(S(300))!;
        Assert.Equal([2, 3, 2], later.Entries.Select(e => e.Count));
        Assert.Equal([("your tail", 5), ("your groin", 5)], later.Often!.Select(h => (h.Place, h.Count)));
        Assert.Equal(" They keep coming back to your tail (5 times) and your groin (5 times) in the last 4 minutes.", TouchWording.Often(later.Often));

        // Older than OftenWindow, it is let go; at most MaximumHistory runs are kept; Clear forgets it.
        Assert.Null(ledger.History(S(300) + TouchLedger.OftenWindow));
        for (var i = 0; i < TouchLedger.MaximumHistory + 5; i++) ledger.Record(i % 2 == 0 ? Pat(400 + i) : Poke(400 + i));
        Assert.Equal(TouchLedger.MaximumHistory, ledger.History(S(500))!.Entries.Count);
        ledger.Clear();
        Assert.Null(ledger.History(S(500)));
    }
}
