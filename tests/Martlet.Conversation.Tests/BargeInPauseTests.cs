using Martlet.Audio.Tests;
using Martlet.Providers;

namespace Martlet.Conversation.Tests;

public sealed class BargeInPauseTests
{
    private const string Sentence = "The best part is the view from the top.";

    private static BargeInJudgeInput Input(string heard, int voicedMs = 900, string? sentence = Sentence) =>
        new(heard, sentence, null, new UtteranceContext { Voiced = TimeSpan.FromMilliseconds(voicedMs) }, ListeningSensitivity.Normal);

    [Theory]
    [InlineData("Wait, stop.", BargeInVerdict.Interrupt)]
    [InlineData("Martlet, what time is it?", BargeInVerdict.Interrupt)]
    [InlineData("What about the weather tomorrow?", BargeInVerdict.Interrupt)]
    [InlineData("Can you play some music instead?", BargeInVerdict.Interrupt)]
    [InlineData("Yeah.", BargeInVerdict.NotForMe)]
    [InlineData("Yeah that's so true.", BargeInVerdict.NotForMe)]
    [InlineData("Haha no way.", BargeInVerdict.NotForMe)]
    [InlineData("the view from the top", BargeInVerdict.NotForMe)]
    [InlineData("Mmmmmm", BargeInVerdict.NotForMe)]
    public void The_rules_judge_tells_words_for_martlet_from_words_that_are_not(string heard, BargeInVerdict expected) =>
        Assert.Equal(expected, RulesBargeInJudge.Judge(Input(heard)).Verdict);

    [Fact]
    public void Martlets_own_words_heard_back_are_not_for_it_but_the_same_words_without_its_sentence_are()
    {
        Assert.Equal("Martlet's own words heard back", RulesBargeInJudge.Judge(Input("is the view from")).Reason);
        Assert.Equal(BargeInVerdict.Interrupt, RulesBargeInJudge.Judge(Input("is the view from", sentence: "Something else.")).Verdict);
    }

    [Fact]
    public async Task A_clear_cue_is_never_judged()
    {
        var judge = new SlowJudge(TimeSpan.FromSeconds(5));
        var ruling = await BargeInJudging.RuleAsync(judge, Input("Hold on."), TimeProvider.System);
        Assert.Equal(BargeInVerdict.Interrupt, ruling.Verdict);
        Assert.Equal(BargeInSource.Cue, ruling.Source);
        Assert.Equal(0, judge.Calls);
    }

    [Fact]
    public async Task A_judge_slower_than_the_deadline_gives_way_to_the_local_rules()
    {
        var clock = new RuntimeClock();
        var judge = new SlowJudge(TimeSpan.FromSeconds(5), clock);
        var ruling = BargeInJudging.RuleAsync(judge, Input("Yeah that's so true."), clock);
        await Harness.Until(() => judge.Calls == 1);
        Assert.False(ruling.IsCompleted);
        clock.Advance(BargeInJudging.Deadline);
        var result = await ruling.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(BargeInSource.Timeout, result.Source);
        Assert.Equal(BargeInVerdict.NotForMe, result.Verdict);
        Assert.Equal("rules", result.Judge);
        Assert.Contains("too slow", result.Reason);
        Assert.Equal(BargeInJudging.Deadline, result.JudgeTime);
    }

    [Fact]
    public async Task A_judge_in_time_decides_and_a_failing_one_gives_way_to_the_rules()
    {
        // A generous deadline: this checks whose verdict is used, not how fast a loaded test host runs the judge.
        var inTime = await BargeInJudging.RuleAsync(new SlowJudge(TimeSpan.Zero), Input("Yeah that's so true."), TimeProvider.System,
            TimeSpan.FromSeconds(30));
        Assert.Equal(BargeInSource.Judge, inTime.Source);
        Assert.Equal(BargeInVerdict.Interrupt, inTime.Verdict);
        Assert.Equal("fixture", inTime.Judge);
        var failed = await BargeInJudging.RuleAsync(new SlowJudge(TimeSpan.Zero) { Fail = true }, Input("What about the weather tomorrow?"),
            TimeProvider.System, TimeSpan.FromSeconds(30));
        Assert.Equal(BargeInSource.Timeout, failed.Source);
        Assert.Equal(BargeInVerdict.Interrupt, failed.Verdict);
        Assert.Contains("failed", failed.Reason);
    }

    private sealed class SlowJudge(TimeSpan delay, TimeProvider? clock = null) : IBargeInJudge
    {
        private int calls;
        internal int Calls => Volatile.Read(ref calls);
        internal bool Fail { get; init; }
        public string Name => "fixture";
        public async Task<BargeInJudgment> JudgeAsync(BargeInJudgeInput input, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref calls);
            await Task.Delay(delay, clock ?? TimeProvider.System, cancellationToken);
            if (Fail) throw new InvalidOperationException("fixture judge failed");
            return new(BargeInVerdict.Interrupt, "fixture says interrupt");
        }
    }

    private static readonly BargeInRuling NotForMe = new(BargeInVerdict.NotForMe, "a quick backchannel", BargeInSource.Judge, "rules", TimeSpan.Zero);
    private static readonly BargeInRuling ForMe = new(BargeInVerdict.Interrupt, "3 words", BargeInSource.Judge, "rules", TimeSpan.Zero);

    private static BargeInOutcome Run(BargeInHold hold, RuntimeClock clock, int voiceFrames, int frames)
    {
        for (var i = 0; i < frames && hold.Outcome == BargeInOutcome.Pending; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(BargeInHold.FrameMilliseconds));
            hold.Frame(i < voiceFrames);
        }
        return hold.Outcome;
    }

    [Fact]
    public void Not_for_martlet_plays_on_once_the_user_is_quiet()
    {
        var clock = new RuntimeClock();
        var hold = new BargeInHold(clock);
        Assert.Equal(BargeInOutcome.Pending, hold.Rule(NotForMe));
        Assert.Equal(BargeInOutcome.Resume, Run(hold, clock, voiceFrames: 10, frames: 100));
        Assert.Equal(BargeInSource.Judge, hold.Source);
        // Ten frames of voice, then twelve of quiet (240 ms).
        Assert.Equal(TimeSpan.FromMilliseconds(440), hold.Paused);
    }

    [Fact]
    public void Talking_on_past_the_limit_stops_the_reply_whatever_the_verdict()
    {
        var clock = new RuntimeClock();
        var hold = new BargeInHold(clock);
        hold.Rule(NotForMe);
        Assert.Equal(BargeInOutcome.Stop, Run(hold, clock, voiceFrames: 1000, frames: 1000));
        Assert.Equal(BargeInSource.KeptTalking, hold.Source);
        Assert.Equal(BargeInJudging.KeepTalkingLimit, hold.Voice);
    }

    [Fact]
    public void An_interrupt_verdict_stops_at_once_and_a_finished_hold_never_changes()
    {
        var clock = new RuntimeClock();
        var hold = new BargeInHold(clock);
        Assert.Equal(BargeInOutcome.Stop, hold.Rule(ForMe));
        Assert.Equal("3 words", hold.Why);
        Assert.Equal(BargeInOutcome.Stop, hold.Rule(NotForMe));
        Assert.Equal(BargeInOutcome.Stop, hold.Ended());
    }

    [Fact]
    public void The_end_of_the_utterance_plays_on_once_a_not_for_me_verdict_arrives_and_no_verdict_plays_on_at_the_limit()
    {
        var clock = new RuntimeClock();
        var ended = new BargeInHold(clock);
        Assert.Equal(BargeInOutcome.Pending, ended.Ended());
        Assert.Equal(BargeInOutcome.Resume, ended.Rule(NotForMe));

        var silent = new BargeInHold(clock);
        clock.Advance(BargeInJudging.MaximumPause);
        Assert.Equal(BargeInOutcome.Resume, silent.Evaluate());
        Assert.Equal(BargeInSource.Limit, silent.Source);
    }

    [Fact]
    public async Task A_paused_reply_keeps_making_its_text_and_voice_and_plays_on_from_where_it_paused_without_making_it_again()
    {
        await using var h = new Harness(new ControlledDevice { AutoConsume = false });
        h.Answer("First sentence. ", "Second sentence. ", "Third sentence.");
        var turn = h.Start();
        await Harness.Until(() => h.Device.Samples > 0, h.Clock);
        Assert.True(turn.Pause());
        Assert.False(turn.Pause());
        Assert.True(turn.Paused);
        Assert.Equal("First sentence.", turn.Sentence);
        // What the device already took plays out; then nothing more while paused.
        h.Device.Consume(int.MaxValue);
        await Task.Delay(60);
        var played = h.Device.Samples;
        // The text finishes and the next sentence's voice is made meanwhile.
        await Harness.Until(() => h.Tts.Calls == 2 && turn.Snapshot.TextComplete, h.Clock);
        h.Clock.Advance(TimeSpan.FromMilliseconds(600));
        h.Device.Consume(int.MaxValue);
        await Task.Delay(60);
        Assert.Equal(played, h.Device.Samples);
        Assert.Equal(ConversationState.Playing, turn.Snapshot.State);
        Assert.True(turn.Resume());
        Assert.False(turn.Paused);
        while (!turn.Completion.IsCompleted)
        {
            h.Device.Consume(1200);
            await Task.Delay(1);
            h.Clock.Advance(TimeSpan.FromMilliseconds(5));
        }
        var result = await turn.Completion;
        Assert.Equal(ConversationState.Completed, result.State);
        Assert.False(result.SpeechFailed);
        // Every sentence was made once and every sample made reached the speakers once.
        Assert.Equal(3, h.Tts.Calls);
        Assert.Equal(result.AcceptedSamples, h.Device.Samples);
        Assert.Equal(1, result.Timings!.PausesForYou);
        Assert.Equal(1, result.Timings.Resumes);
        Assert.True(result.Timings.PausedForYou >= TimeSpan.FromMilliseconds(600));
        Assert.Null(turn.Sentence);
    }

    [Fact]
    public async Task Stopping_a_paused_reply_drops_the_rest_as_talking_over_it_always_did()
    {
        await using var h = new Harness(new ControlledDevice { AutoConsume = false });
        h.Answer("First sentence. ", "Second sentence. ", "Third sentence.");
        var turn = h.Start();
        await Harness.Until(() => h.Device.Samples > 0, h.Clock);
        Assert.True(turn.Pause());
        var result = await turn.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(ConversationState.Canceled, result.State);
        Assert.Equal(1, result.Timings!.PausesForYou);
        Assert.Equal(0, result.Timings.Resumes);
    }

    [Fact]
    public async Task A_reply_that_is_not_spoken_never_pauses()
    {
        await using var h = new Harness();
        h.Answer("Plain text.");
        var turn = h.Start(Harness.Request(speech: false));
        Assert.False(turn.Pause());
        await Harness.Finish(turn);
        Assert.False(turn.Pause());
    }
}
