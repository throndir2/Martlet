using Martlet.Conversation;

namespace Martlet.Conversation.Tests;

public sealed class EndOfTurnGateTests
{
    private static readonly TimeSpan Plain = TimeSpan.FromMilliseconds(800);

    // Steps one pause frame by frame; answer(askedAtFrame, frame) may judge it. Returns the step that ended it and its frame.
    private static (EndOfTurnStep Step, int Frame, EndOfTurnGate Gate) Pause(Func<EndOfTurnGate, int, int, bool>? answer = null,
        bool judgeable = true, TimeSpan? endSilence = null)
    {
        var gate = new EndOfTurnGate(endSilence ?? Plain);
        Assert.Equal(EndOfTurnStep.Wait, gate.Step(0, judgeable));
        var asked = -1;
        var cap = (int)(gate.DetectorEndSilence.TotalMilliseconds / 20);
        for (var frame = 1; frame <= cap; frame++)
        {
            if (asked >= 0) answer?.Invoke(gate, asked, frame);
            var step = gate.Step(frame, judgeable);
            if (step == EndOfTurnStep.Judge) asked = frame;
            else if (step != EndOfTurnStep.Wait) return (step, frame, gate);
        }
        return (EndOfTurnStep.Wait, cap, gate);
    }

    [Fact]
    public void The_judge_is_asked_once_after_the_short_pause()
    {
        var asks = 0;
        var gate = new EndOfTurnGate(Plain);
        for (var frame = 1; frame <= 30; frame++)
            if (gate.Step(frame, judgeable: true) == EndOfTurnStep.Judge)
            {
                asks++;
                Assert.Equal(13, frame);
            }
        Assert.Equal(1, asks);
    }

    [Fact]
    public void Complete_ends_the_turn_as_soon_as_the_answer_is_in()
    {
        var (step, frame, _) = Pause((gate, asked, now) =>
        {
            if (now == asked + 2) gate.Judged(gate.Pause, new(TurnVerdict.Complete, 0.9));
            return true;
        });
        Assert.Equal(EndOfTurnStep.Complete, step);
        Assert.Equal(15, frame);
    }

    [Fact]
    public void Incomplete_waits_through_the_plain_pause_for_the_longer_one()
    {
        var (step, frame, gate) = Pause((gate, asked, now) =>
        {
            if (now == asked + 2) gate.Judged(gate.Pause, new(TurnVerdict.Incomplete, 0.1));
            return true;
        });
        // The detector itself (built with DetectorEndSilence) ends the speech at the longer pause.
        Assert.Equal(EndOfTurnStep.Wait, step);
        Assert.Equal(80, frame);
        Assert.Equal(TimeSpan.FromMilliseconds(1600), gate.DetectorEndSilence);
    }

    [Fact]
    public void A_slow_judge_leaves_the_plain_pause_to_end_the_turn()
    {
        var (step, frame, gate) = Pause();
        Assert.Equal(EndOfTurnStep.Fallback, step);
        Assert.Equal(40, frame);
        Assert.Equal(EndOfTurnFallback.Slow, gate.Fallback);
    }

    [Fact]
    public void A_failed_judge_leaves_the_plain_pause_to_end_the_turn()
    {
        var (step, frame, gate) = Pause((gate, asked, now) =>
        {
            if (now == asked + 1) gate.Judged(gate.Pause, null);
            return true;
        });
        Assert.Equal(EndOfTurnStep.Fallback, step);
        Assert.Equal(40, frame);
        Assert.Equal(EndOfTurnFallback.Failed, gate.Fallback);
    }

    [Fact]
    public void Speech_that_was_not_accepted_yet_is_never_judged_and_ends_at_the_plain_pause()
    {
        var (step, frame, gate) = Pause(judgeable: false);
        Assert.Equal(EndOfTurnStep.Fallback, step);
        Assert.Equal(40, frame);
        Assert.False(gate.Asked);
        Assert.Equal(EndOfTurnFallback.NotJudged, gate.Fallback);
    }

    [Fact]
    public void Talking_again_ignores_the_old_answer_and_asks_again_in_the_next_pause()
    {
        var gate = new EndOfTurnGate(Plain);
        for (var frame = 1; frame <= 13; frame++) gate.Step(frame, true);
        var first = gate.Pause;
        Assert.True(gate.Judged(first, new(TurnVerdict.Incomplete, 0.2)));
        Assert.Equal(EndOfTurnStep.Wait, gate.Step(14, true));
        // The user goes on talking: the unfinished answer is reported once.
        Assert.Equal(EndOfTurnStep.Wait, gate.Step(0, true));
        Assert.Equal(0.2, gate.WentOn!.Probability);
        Assert.Equal(EndOfTurnStep.Wait, gate.Step(0, true));
        Assert.Null(gate.WentOn);
        // A late answer for the old pause no longer counts.
        Assert.False(gate.Judged(first, new(TurnVerdict.Complete, 0.9)));
        EndOfTurnStep step = EndOfTurnStep.Wait;
        for (var frame = 1; frame <= 13; frame++) step = gate.Step(frame, true);
        Assert.Equal(EndOfTurnStep.Judge, step);
        Assert.Equal(first + 1, gate.Pause);
    }

    [Fact]
    public void The_longer_pause_is_at_least_twice_the_plain_one_and_at_most_five_seconds()
    {
        var options = new EndOfTurnOptions();
        Assert.Equal(TimeSpan.FromMilliseconds(1600), options.Cap(TimeSpan.FromMilliseconds(500)));
        Assert.Equal(TimeSpan.FromMilliseconds(2400), options.Cap(TimeSpan.FromMilliseconds(1200)));
        Assert.Equal(TimeSpan.FromSeconds(5), options.Cap(TimeSpan.FromSeconds(4)));
        // A short plain pause is still judged first.
        var (step, frame, _) = Pause(endSilence: TimeSpan.FromMilliseconds(500));
        Assert.Equal(EndOfTurnStep.Fallback, step);
        Assert.Equal(25, frame);
    }

    [Fact]
    public async Task A_fallback_judge_answers_when_the_primary_one_cannot()
    {
        var primary = new FixedJudge("primary", available: true, fails: true);
        var fallback = new FixedJudge("pool", available: true, verdict: TurnVerdict.Incomplete);
        var judge = EndOfTurnJudges.WithFallback(primary, fallback);
        var answer = await judge.JudgeAsync(new(new byte[640], TimeSpan.FromMilliseconds(260)), CancellationToken.None);
        Assert.Equal(TurnVerdict.Incomplete, answer.Verdict);
        Assert.Equal("primary", judge.Name);
        var missing = EndOfTurnJudges.WithFallback(new FixedJudge("primary", available: false), fallback);
        Assert.Equal("pool", missing.Name);
        Assert.Equal(TurnVerdict.Incomplete, (await missing.JudgeAsync(new(new byte[640], TimeSpan.Zero), CancellationToken.None)).Verdict);
    }

    [Fact]
    public void Each_decision_reads_as_one_log_line_without_words()
    {
        var at = DateTimeOffset.UnixEpoch;
        Assert.Equal("End of turn: complete (Smart Turn v3.2, 0.93) after 280 ms of silence; judge 31 ms.",
            new EndOfTurnDecision(at, EndOfTurnDecision.Complete, TimeSpan.FromMilliseconds(280), Plain, TimeSpan.FromMilliseconds(31), 0.93,
                "Smart Turn v3.2").Describe());
        Assert.Equal("End of turn: the judge (Smart Turn v3.2) had no answer within 800 ms of silence; the plain pause ended the turn.",
            new EndOfTurnDecision(at, EndOfTurnDecision.Slow, Plain, Plain, Judge: "Smart Turn v3.2").Describe());
        Assert.Contains("the longer pause for unfinished speech ended it after 1600 ms",
            new EndOfTurnDecision(at, EndOfTurnDecision.Incomplete, TimeSpan.FromMilliseconds(1600), Plain, TimeSpan.FromMilliseconds(30), 0.2,
                "Smart Turn v3.2").Describe());
    }

    private sealed class FixedJudge(string name, bool available, bool fails = false, TurnVerdict verdict = TurnVerdict.Complete) : IEndOfTurnJudge
    {
        public string Name => name;
        public bool Available => available;

        public Task<EndOfTurnJudgement> JudgeAsync(EndOfTurnRequest request, CancellationToken cancellationToken) =>
            fails ? Task.FromException<EndOfTurnJudgement>(new InvalidOperationException("no")) : Task.FromResult(new EndOfTurnJudgement(verdict));
    }
}
