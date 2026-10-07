using Martlet.Conversation;
using Martlet.Mcp;

namespace Martlet.Desktop.Tests;

public sealed class ReplyLatencyTests
{
    private static ConversationSnapshot Reply(ConversationTimings timings) => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, 1,
        ConversationState.Completed, ConversationFailure.None, null, null, 40, true, 0, 1, 2, 0, 80, 48_000, null, null, null,
        48_000, 48_000, 48_000, true, true, false, 0, null, null, false, FirstTextAfter: TimeSpan.FromMilliseconds(900),
        FirstAudioAfter: TimeSpan.FromMilliseconds(2_000), Timings: timings);

    [Fact]
    public void A_voice_slower_than_real_time_is_said_in_the_line_and_read_back_by_latency_report()
    {
        var clock = TimeProvider.System;
        var start = clock.GetTimestamp();
        var timings = new ConversationTimings(SpeechRequestAfter: TimeSpan.FromMilliseconds(1_000),
            FirstSpeechAudioAfter: TimeSpan.FromMilliseconds(1_900), FirstPieceSynthesizedAfter: TimeSpan.FromMilliseconds(5_964),
            FirstPieceSpeech: TimeSpan.FromSeconds(2.54), VoiceWaits: 2, VoiceWaited: TimeSpan.FromMilliseconds(3_120));
        var line = ReplyLatency.Describe(null, start, clock, Reply(timings), "Thinking gemma4:12b, voice chatterbox-turbo");

        Assert.NotNull(line);
        Assert.Contains("First piece: 2.54 s of speech made in 4964 ms. The voice paused 2 times for 3120 ms in all, " +
            "waiting for its next audio. Models: Thinking gemma4:12b, voice chatterbox-turbo.", line);
        var parsed = LatencyReport.Parse(DateTimeOffset.Now, line!)!;
        Assert.Equal(2, parsed.VoicePauses);
        Assert.Equal(3_120, parsed.VoicePausedMs);
        Assert.Equal("Thinking gemma4:12b, voice chatterbox-turbo", parsed.Models);
        Assert.Equal(2.54, parsed.FirstPieceSpeechSeconds);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(3, 60)]
    public void Waits_too_short_to_hear_are_left_out(int waits, int milliseconds)
    {
        var clock = TimeProvider.System;
        var line = ReplyLatency.Describe(null, clock.GetTimestamp(), clock,
            Reply(new ConversationTimings(VoiceWaits: waits, VoiceWaited: TimeSpan.FromMilliseconds(milliseconds))), null)!;
        Assert.DoesNotContain("The voice paused", line);
        Assert.Equal(0, LatencyReport.Parse(DateTimeOffset.Now, line)!.VoicePauses);
    }

    [Fact]
    public void A_reply_started_early_is_said_in_the_line_and_read_back_by_latency_report()
    {
        var clock = TimeProvider.System;
        var stopped = clock.GetTimestamp() - 2 * clock.TimestampFrequency;
        var started = stopped + (long)(0.262 * clock.TimestampFrequency);
        var timeline = new ReplyTimeline(clock, ReplyTimeline.YouStopped, stopped) { Early = new(true, 2, 1) };
        timeline.Mark(ReplyLatency.EndOfSpeech, stopped + (long)(1.6 * clock.TimestampFrequency));
        var promoted = ReplyLatency.Describe(timeline, started, clock, Reply(new ConversationTimings(StartedEarly: true)), null)!;
        Assert.Contains("Started early at 262 ms, promoted (started early 2 times, 1 cancelled).", promoted);
        var parsed = LatencyReport.Parse(DateTimeOffset.Now, promoted)!;
        Assert.Equal(262, parsed.StartedEarlyMs);
        Assert.Equal(2, parsed.EarlyStarts);
        Assert.Equal(1, parsed.EarlyCancelled);
        // The end of the turn counts as one of the steps, in the order things happened.
        Assert.Contains(ReplyLatency.EndOfSpeech, parsed.Steps.Keys);

        timeline.Early = new(false, 1, 1);
        var restarted = LatencyReport.Parse(DateTimeOffset.Now,
            ReplyLatency.Describe(timeline, stopped + 2 * clock.TimestampFrequency, clock, Reply(new()), null)!)!;
        Assert.Null(restarted.StartedEarlyMs);
        Assert.Equal(1, restarted.EarlyStarts);
        Assert.Equal(1, restarted.EarlyCancelled);

        var summary = System.Text.Json.JsonSerializer.SerializeToElement(LatencyReport.Summarize([parsed, restarted])).GetProperty("early");
        Assert.Equal(2, summary.GetProperty("replies").GetInt32());
        Assert.Equal(1, summary.GetProperty("promoted").GetInt32());
        Assert.Equal(3, summary.GetProperty("starts").GetInt32());
        Assert.Equal(2, summary.GetProperty("cancelled").GetInt32());
    }

    [Fact]
    public async Task The_early_reply_check_promotes_the_reply_started_early_and_its_first_audio_comes_much_sooner()
    {
        // Production gates, held turn, Chat Completions adapter, host voice stream and playback sink with fixtures (NOT AI).
        var result = System.Text.Json.JsonSerializer.SerializeToElement(
            await EarlyReplyCheck.RunAsync("incomplete", thinkingMs: 150, voiceMs: 200, sttMs: 60, judgeMs: 20, CancellationToken.None));
        Assert.True(result.GetProperty("ok").GetBoolean(), result.ToString());
        var scenario = result.GetProperty("scenarios")[0];
        Assert.True(scenario.GetProperty("savedMs").GetDouble() >= 200, result.ToString());
        var early = scenario.GetProperty("withEarlyReplies");
        Assert.Equal("promoted", early.GetProperty("outcome").GetString());
        Assert.Equal(0, early.GetProperty("playedBeforeTurnEnded").GetInt32());
        Assert.Equal(1, early.GetProperty("thinkingRequests").GetInt32());
        Assert.Contains("ms, promoted.", early.GetProperty("latencyLine").GetString());
    }

    [Fact]
    public async Task A_reply_with_a_voice_slower_than_real_time_is_spoken_whole()
    {
        // Production conversation runtime and playback sink with a fixture voice (NOT AI) that pauses 1.5 s mid-piece.
        var result = System.Text.Json.JsonSerializer.SerializeToElement(
            await SpokenReplyCheck.RunAsync("slow", null, CancellationToken.None));
        Assert.True(result.GetProperty("ok").GetBoolean(), result.ToString());
        var voice = result.GetProperty("voice");
        Assert.False(voice.GetProperty("stopped").GetBoolean());
        Assert.Equal(voice.GetProperty("piecesAsked").GetInt32(), voice.GetProperty("piecesSpoken").GetInt32());
        Assert.True(voice.GetProperty("pauses").GetInt32() >= voice.GetProperty("piecesAsked").GetInt32());
        Assert.Contains("The voice paused", result.GetProperty("latency").GetProperty("line").GetString());
    }
}
