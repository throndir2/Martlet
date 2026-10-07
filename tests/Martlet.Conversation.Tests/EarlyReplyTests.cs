using Martlet.Audio.Tests;
using Martlet.Core.Settings;
using Martlet.Providers;

namespace Martlet.Conversation.Tests;

/// <summary>Companion › Listening › Start replies early: the conversation runtime's held turn (its text streams and its first
/// piece is made, but nothing is shown, acted or played until it is released), the gate that decides when a turn starts one, the
/// request comparison and the reply latency line.</summary>
public sealed class EarlyReplyTests
{
    [Fact]
    public async Task A_reply_started_early_makes_its_text_and_first_piece_but_plays_nothing_until_it_is_released()
    {
        await using var h = new Harness(new ControlledDevice { AutoConsume = false });
        h.Answer("First sentence. ", "Second sentence. ", "Third sentence.");
        var turn = h.Runtime.StartEarly(Harness.Request(), h.Permissions, prepareVoice: true);
        Assert.True(turn.Held);
        await Harness.Until(() => turn.Snapshot.Timings?.FirstSpeechAudioAfter is not null, h.Clock);
        // Some time passes while the user's turn is still open: nothing plays, and only the first piece is made.
        for (var i = 0; i < 40; i++)
        {
            h.Clock.Advance(TimeSpan.FromMilliseconds(10));
            await Task.Delay(1);
        }
        Assert.StartsWith("First sentence.", turn.Content.Text);
        Assert.Equal(1, h.Tts.Calls);
        Assert.Equal(0, h.Device.Opens);
        var held = turn.Snapshot;
        Assert.NotEqual(ConversationState.Playing, held.State);
        Assert.False(held.MayHavePlayed);
        Assert.True(held.Timings!.StartedEarly);
        Assert.Null(held.Timings.ReleasedAfter);

        Assert.True(turn.Release());
        Assert.False(turn.Held);
        Assert.False(turn.Release());
        // The first piece plays at once: it is already made.
        await Harness.Until(() => h.Device.Samples > 0, h.Clock);
        while (!turn.Completion.IsCompleted)
        {
            h.Device.Consume(1200);
            await Task.Delay(1);
            h.Clock.Advance(TimeSpan.FromMilliseconds(5));
        }
        var result = await turn.Completion;
        Assert.Equal(ConversationState.Completed, result.State);
        Assert.Equal("First sentence. Second sentence. Third sentence.", turn.Content.Text);
        Assert.Equal(3, h.Tts.Calls);
        Assert.Equal(result.AcceptedSamples, h.Device.Samples);
        Assert.True(result.Timings!.StartedEarly);
        Assert.NotNull(result.Timings.ReleasedAfter);
        Assert.True(result.FirstAudioAfter >= result.Timings.ReleasedAfter);
    }

    [Fact]
    public async Task A_reply_started_early_without_its_voice_makes_no_voice_until_it_is_released()
    {
        await using var h = new Harness();
        h.Answer("First sentence. ", "Second sentence.");
        var turn = h.Runtime.StartEarly(Harness.Request(), h.Permissions, prepareVoice: false);
        await Harness.Until(() => turn.Content.Text.Length > 0, h.Clock);
        for (var i = 0; i < 20; i++)
        {
            h.Clock.Advance(TimeSpan.FromMilliseconds(10));
            await Task.Delay(1);
        }
        Assert.Equal(0, h.Tts.Calls);
        Assert.Equal(0, h.Device.Opens);
        Assert.True(turn.Release());
        var result = await Harness.Finish(turn, h.Clock);
        Assert.Equal(ConversationState.Completed, result.State);
        Assert.Equal(2, h.Tts.Calls);
        Assert.True(h.Device.Samples > 0);
    }

    [Fact]
    public async Task A_reply_started_early_that_is_let_go_shows_says_and_plays_nothing()
    {
        var captions = new SpokenTextFeed();
        await using var h = new Harness(spokenText: captions);
        h.Answer("First sentence. ", "Second sentence.");
        var turn = h.Runtime.StartEarly(Harness.Request(), h.Permissions, prepareVoice: true);
        await Harness.Until(() => turn.Snapshot.Timings?.FirstSpeechAudioAfter is not null, h.Clock);
        var result = await turn.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(ConversationState.Canceled, result.State);
        Assert.False(result.MayHavePlayed);
        Assert.Equal(0, h.Device.Opens);
        Assert.False(captions.Lines.TryRead(out _));
        Assert.False(turn.Release());
        // The next reply starts as usual once the one let go has released everything.
        await Harness.Until(() => turn.OwnershipRelease.IsCompleted, h.Clock);
        h.Answer("Next.");
        var next = h.Start(Harness.Request(speech: false));
        Assert.Equal(ConversationState.Completed, (await Harness.Finish(next, h.Clock)).State);
    }

    [Fact]
    public async Task A_text_only_reply_started_early_shows_its_captions_and_character_cues_only_once_released()
    {
        var captions = new SpokenTextFeed();
        await using var h = new Harness(textOnly: true, spokenText: captions);
        h.Answer("First sentence. ", "Second sentence.");
        var turn = h.Runtime.StartEarly(Harness.Request(speech: false), h.Permissions, prepareVoice: true);
        await Harness.Until(() => turn.Snapshot.TextComplete, h.Clock);
        Assert.False(captions.Lines.TryRead(out _));
        Assert.True(turn.Release());
        await Harness.Finish(turn, h.Clock);
        Assert.True(captions.Lines.TryRead(out var first));
        Assert.Equal("First sentence.", first!.Text);
    }

    [Fact]
    public void The_gate_starts_one_reply_per_pause_lets_it_go_when_the_voice_comes_back_and_keeps_it_only_for_the_pause_that_ends_the_turn()
    {
        var gate = new EarlyReplyGate(new EarlyReplyOptions());
        // Not worth it, not silent or another pause: nothing starts.
        Assert.False(gate.TryStart(1, 1, silent: true, worth: false));
        Assert.False(gate.TryStart(1, 1, silent: false, worth: true));
        Assert.False(gate.TryStart(1, 2, silent: true, worth: true));
        Assert.True(gate.TryStart(1, 1, silent: true, worth: true));
        Assert.Equal(1, gate.Running);
        // One at a time.
        Assert.False(gate.TryStart(1, 1, silent: true, worth: true));
        // The voice comes back: it goes, and the next pause may start another.
        Assert.True(gate.VoiceResumed());
        Assert.False(gate.VoiceResumed());
        Assert.True(gate.TryStart(2, 2, silent: true, worth: true));
        // The turn ends in another pause: it goes too.
        Assert.False(gate.Ended(3));
        Assert.Equal(2, gate.Cancelled);
        Assert.True(gate.TryStart(3, 3, silent: true, worth: true));
        Assert.True(gate.Spent);
        Assert.True(gate.Ended(3));
        Assert.Equal(3, gate.Starts);
        // At most three in a turn.
        gate.Cancel();
        Assert.False(gate.TryStart(4, 4, silent: true, worth: true));
    }

    [Fact]
    public void A_start_that_could_not_happen_does_not_count()
    {
        var gate = new EarlyReplyGate(new EarlyReplyOptions());
        Assert.True(gate.TryStart(1, 1, silent: true, worth: true));
        gate.NotStarted();
        Assert.Null(gate.Running);
        Assert.Equal(0, gate.Starts);
        Assert.Equal(0, gate.Cancelled);
        Assert.False(new EarlyReplyGate(EarlyReplyOptions.Off).TryStart(1, 1, silent: true, worth: true));
    }

    [Theory]
    [InlineData("So I was thinking we could go out tonight", true)]
    [InlineData("Can you remind me?", true)]
    [InlineData("Yeah.", false)]
    [InlineData("Okay, yeah.", false)]
    [InlineData("Mm-hmm.", false)]
    [InlineData("Hmm.", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Only_real_words_start_a_reply_early(string? text, bool worth)
    {
        var context = new UtteranceContext { Voiced = TimeSpan.FromSeconds(2), Speech = TimeSpan.FromSeconds(2.5) };
        Assert.Equal(worth, EarlyReplyGate.Worth(text, context, ListeningSensitivity.Normal));
    }

    [Fact]
    public void Replies_start_early_for_own_computers_by_default_and_for_cloud_models_only_when_chosen()
    {
        var options = new EarlyReplyOptions();
        Assert.True(options.Enabled);
        Assert.False(options.Cloud);
        Assert.True(options.Voice);
        Assert.Equal(3, options.MaximumStarts);
        Assert.True(options.ForThinking(ownComputer: true));
        Assert.False(options.ForThinking(ownComputer: false));
        Assert.True(options.ForVoice(ownComputer: true, paidVoice: false));
        Assert.False(options.ForVoice(ownComputer: true, paidVoice: true));
        var cloud = options with { Cloud = true };
        Assert.True(cloud.ForThinking(ownComputer: false));
        Assert.True(cloud.ForVoice(ownComputer: false, paidVoice: true));
        Assert.False((options with { Voice = false }).ForVoice(ownComputer: true, paidVoice: false));
        Assert.False(EarlyReplyOptions.Off.ForThinking(ownComputer: true));
    }

    [Fact]
    public void Only_the_same_request_is_promoted()
    {
        var audio = BoundedWaveAudio.FromPcm(Martlet.Audio.CapturedUtterance.Format, new byte[3200]);
        var other = BoundedWaveAudio.FromPcm(Martlet.Audio.CapturedUtterance.Format, Enumerable.Repeat((byte)1, 3200).ToArray());
        var started = new EarlyAsk("Can you remind me?", true, false, audio, ChattinessChoice.Normal, false, "[Sam] ");
        Assert.Null(started.Differs(started with { Recording = BoundedWaveAudio.FromPcm(Martlet.Audio.CapturedUtterance.Format, new byte[3200]) }));
        Assert.Equal("what you said changed", started.Differs(started with { Text = "Can you remind me to call?" }));
        Assert.Equal("your recording changed", started.Differs(started with { Recording = other }));
        Assert.Equal("your recording changed", started.Differs(started with { Recording = null }));
        Assert.Equal("the voice was turned on or off", started.Differs(started with { Voice = false }));
        Assert.Equal("how chatty Martlet is changed", started.Differs(started with { Chattiness = null }));
        Assert.Equal("who spoke changed", started.Differs(started with { Voices = null }));
        Assert.Equal("a picture goes with it", started.Differs(started with { Extra = "a picture goes with it" }));
        // Straight to Thinking, the recording is what was said: its words don't decide.
        var straight = started with { Straight = true, Text = "" };
        Assert.Null(straight.Differs(straight with { Text = "Can you remind me?" }));
        Assert.Equal("how your voice goes to Thinking changed", straight.Differs(started));
    }

    [Fact]
    public void The_log_says_what_became_of_each_reply_started_early()
    {
        var at = DateTimeOffset.UnixEpoch;
        Assert.Equal("Early reply: promoted after 1340 ms; it started 262 ms into your pause (start 1), and its first piece was ready.",
            new EarlyReplyRecord(at, EarlyReplyRecord.Promoted, TimeSpan.FromMilliseconds(262), TimeSpan.FromMilliseconds(1340), 1,
                FirstPieceReady: true).Describe());
        Assert.Equal("Early reply: let go after 420 ms (you went on talking); it started 350 ms into your pause (start 2).",
            new EarlyReplyRecord(at, EarlyReplyRecord.Cancelled, TimeSpan.FromMilliseconds(350), TimeSpan.FromMilliseconds(420), 2,
                "you went on talking").Describe());
        Assert.Contains("A new reply starts now.", new EarlyReplyRecord(at, EarlyReplyRecord.Changed, TimeSpan.FromMilliseconds(300),
            TimeSpan.FromMilliseconds(1300), 1, "what you said changed").Describe());
    }

    [Fact]
    public void The_reply_latency_line_says_a_reply_started_early_and_counts_its_steps_in_the_order_they_happened()
    {
        var clock = new RuntimeClock();
        long At(int ms) => ms * TimeSpan.TicksPerMillisecond;
        // The user stopped at 0; the judge was asked at 260; the reply started early at 360; the turn ended at 1600.
        var timeline = new ReplyTimeline(clock, ReplyTimeline.YouStopped, 0);
        timeline.Mark(ReplyLatency.EndOfTurnWait, At(260));
        timeline.Mark(ReplyLatency.EndOfTurnJudge, At(290));
        timeline.Mark([("building the request", At(360))]);
        timeline.Mark(ReplyLatency.EndOfSpeech, At(1600));
        timeline.Mark("waiting to answer", At(1610));
        timeline.Early = new(Promoted: true, Starts: 2, Cancelled: 1);
        var reply = new ConversationSnapshot(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, 1, ConversationState.Completed,
            ConversationFailure.None, null, null, 40, true, 0, 1, 2, 0, 80, 48_000, null, null, null, 48_000, 48_000, 48_000, true, true,
            false, 0, null, null, false, FirstTextAfter: TimeSpan.FromMilliseconds(200), FirstAudioAfter: TimeSpan.FromMilliseconds(1260),
            Timings: new(TextRequestAfter: TimeSpan.FromMilliseconds(2), FirstSegmentAfter: TimeSpan.FromMilliseconds(210),
                SpeechRequestAfter: TimeSpan.FromMilliseconds(212), FirstSpeechAudioAfter: TimeSpan.FromMilliseconds(560),
                PlaybackStartedAfter: TimeSpan.FromMilliseconds(1252), StartedEarly: true, ReleasedAfter: TimeSpan.FromMilliseconds(1250)));
        var line = ReplyLatency.Describe(timeline, At(360), clock, reply, null)!;
        Assert.StartsWith("Reply latency: first audio 1620 ms after you stopped talking (end-of-turn wait 260, end-of-turn judge 30, " +
            "building the request 70, Thinking authorization 2, Thinking first words 198, first sentence 10, voice authorization 2, " +
            "voice synthesis 348, end of speech 680, waiting to answer 10, promoted 0, playback start 2, speakers 8).", line);
        Assert.Contains("Started early at 360 ms, promoted (started early 2 times, 1 cancelled).", line);

        // A reply that started normally after the turn's early starts were let go says how many there were.
        timeline.Early = new(Promoted: false, Starts: 1, Cancelled: 1);
        var after = ReplyLatency.Describe(timeline, At(1610), clock,
            reply with { Timings = reply.Timings! with { StartedEarly = false, ReleasedAfter = null } }, null)!;
        Assert.Contains("Started early 1 time, 1 cancelled.", after);
        Assert.DoesNotContain("promoted", after);
        // The copy a restarted reply gets keeps it.
        Assert.Equal(timeline.Early, timeline.Copy().Early);
    }
}
