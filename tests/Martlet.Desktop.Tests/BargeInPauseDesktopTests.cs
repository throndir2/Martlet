using System.IO;
using System.Text.Json;
using Martlet.Conversation;
using Martlet.Mcp;

namespace Martlet.Desktop.Tests;

public sealed class BargeInPauseDesktopTests
{
    private static ConversationSnapshot Reply(ConversationTimings timings) => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, 1,
        ConversationState.Completed, ConversationFailure.None, null, null, 40, true, 0, 1, 2, 0, 80, 48_000, null, null, null,
        48_000, 48_000, 48_000, true, true, false, 0, null, null, false, FirstTextAfter: TimeSpan.FromMilliseconds(900),
        FirstAudioAfter: TimeSpan.FromMilliseconds(2_000), Timings: timings);

    [Fact]
    public void The_reply_latency_line_says_how_long_a_reply_paused_and_whether_it_resumed_or_stopped()
    {
        var clock = TimeProvider.System;
        var paused = new ConversationTimings(PausesForYou: 1, Resumes: 1, PausedForYou: TimeSpan.FromMilliseconds(612));
        var resumed = ReplyLatency.Describe(null, clock.GetTimestamp(), clock, Reply(paused), null)!;
        Assert.Contains(", paused 612 ms when you talked over it, then resumed.", resumed);
        var read = LatencyReport.Parse(DateTimeOffset.Now, resumed)!;
        Assert.Equal(612, read.PausedForYouMs);
        Assert.True(read.Resumed);
        Assert.False(read.Interrupted);

        var stopped = ReplyLatency.Describe(null, clock.GetTimestamp(), clock, Reply(paused with { Resumes = 0 }), null, interrupted: true)!;
        Assert.Contains(", paused 612 ms, then stopped when you talked over it.", stopped);
        var readStopped = LatencyReport.Parse(DateTimeOffset.Now, stopped)!;
        Assert.True(readStopped.Interrupted);
        Assert.False(readStopped.Resumed);
        Assert.Equal(612, readStopped.PausedForYouMs);

        var plain = ReplyLatency.Describe(null, clock.GetTimestamp(), clock, Reply(new()), null, interrupted: true)!;
        Assert.Contains(", stopped when you talked over it.", plain);
        Assert.Null(LatencyReport.Parse(DateTimeOffset.Now, plain)!.PausedForYouMs);
    }

    [Fact]
    public void The_talk_window_line_says_what_happened_and_what_decided_never_the_words()
    {
        var at = DateTimeOffset.Now;
        var resumed = LiveConversationWindow.BargeInLine(new(at, BargeInSource.Judge, BargeInVerdict.NotForMe, "agreeing or laughing along",
            "rules", TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(430), "resumed"));
        Assert.EndsWith("paused 430 ms, then resumed (not for Martlet: agreeing or laughing along; rules judge, 1 ms).", resumed);
        var cue = LiveConversationWindow.BargeInLine(new(at, BargeInSource.Cue, BargeInVerdict.Interrupt, "a stop word", "none",
            TimeSpan.Zero, null, "stopped"));
        Assert.EndsWith("stopped at once (for Martlet: a stop word; a clear cue).", cue);
        var kept = LiveConversationWindow.BargeInLine(new(at, BargeInSource.KeptTalking, BargeInVerdict.Interrupt, "you kept talking",
            "rules", TimeSpan.Zero, TimeSpan.FromMilliseconds(1600), "stopped"));
        Assert.Contains("paused 1600 ms, then stopped", kept);
        Assert.Contains("you kept talking", kept);
    }

    [Fact]
    public void Pause_and_decide_is_the_default_and_the_choice_is_saved()
    {
        Assert.Equal(BargeInBehavior.PauseAndDecide, new TalkPreferences().BargeInStyle);
        var directory = Directory.CreateTempSubdirectory("martlet-barge-in-").FullName;
        try
        {
            Assert.Equal(BargeInBehavior.PauseAndDecide, TalkPreferences.Load(directory).BargeInStyle);
            Assert.True((new TalkPreferences() with { BargeIn = true, BargeInStyle = BargeInBehavior.StopAtOnce }).Save(directory));
            Assert.Equal(BargeInBehavior.StopAtOnce, TalkPreferences.Load(directory).BargeInStyle);
            File.WriteAllText(Path.Combine(directory, "talk-preferences.json"), "{\"Version\":4,\"BargeInStyle\":7}");
            Assert.Equal(BargeInBehavior.PauseAndDecide, TalkPreferences.Load(directory).BargeInStyle);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task Mcp_barge_in_check_returns_the_verdicts_the_deadline_fallback_and_what_a_pause_does()
    {
        var directory = Directory.CreateTempSubdirectory("martlet-barge-in-mcp-").FullName;
        try
        {
            using var none = JsonDocument.Parse("{}");
            var result = JsonSerializer.SerializeToElement(await BargeInCheck.RunAsync(none.RootElement, directory, CancellationToken.None));
            Assert.True(result.GetProperty("ok").GetBoolean(), result.ToString());
            Assert.Equal("PauseAndDecide", result.GetProperty("behavior").GetString());
            Assert.Equal(4, result.GetProperty("holds").GetArrayLength());
            Assert.Equal(3, result.GetProperty("modelJudge").GetArrayLength());
            Assert.True(result.GetProperty("modelJudgeOk").GetBoolean());
            Assert.Equal("Timeout", result.GetProperty("deadlines")[0].GetProperty("source").GetString());
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task A_paused_reply_through_the_production_runtime_is_silent_keeps_its_voice_coming_and_plays_on_whole()
    {
        // Production conversation runtime and playback sink with fixture text and voice (NOT AI); nothing is played.
        var result = JsonSerializer.SerializeToElement(await SpokenReplyCheck.RunAsync("paused", null, CancellationToken.None));
        Assert.True(result.GetProperty("ok").GetBoolean(), result.ToString());
        var hold = result.GetProperty("voice").GetProperty("hold");
        Assert.Equal(hold.GetProperty("SamplesBefore").GetInt64(), hold.GetProperty("SamplesAfter").GetInt64());
        Assert.Contains("then resumed", result.GetProperty("latency").GetProperty("line").GetString());
    }

    [Theory]
    // Early in its sentence, the wink falls inside the pause whether it is timed by the audio or by a reading pace.
    [InlineData("paused", "Hey cutie (winks), I like you. Tell me all about your day.")]
    // At the end of its sentence, the wink is due well after the stop.
    [InlineData("stopped", "Hey cutie, I like how you are (winks). Tell me all about your day.")]
    public async Task A_wink_in_a_sentence_waits_out_a_pause_and_is_dropped_by_a_stop(string voiceFailure, string reply)
    {
        // Production conversation runtime with fixture text and voice (NOT AI); each cue waits as the desktop's character waits.
        var result = JsonSerializer.SerializeToElement(await SpokenReplyCheck.RunAsync(voiceFailure, null, CancellationToken.None,
            reply: reply, characterTags: ["{wink}"]));
        Assert.True(result.GetProperty("ok").GetBoolean(), result.ToString());
        var wink = Assert.Single(result.GetProperty("character").GetProperty("cues").EnumerateArray());
        Assert.Equal("{wink}", wink.GetProperty("tag").GetString());
        if (voiceFailure == "stopped")
        {
            Assert.Equal("Canceled", result.GetProperty("reply").GetProperty("state").GetString());
            Assert.True(wink.GetProperty("dropped").GetBoolean(), result.ToString());
        }
        else
            // The pause began just after the sentence started, before the wink's moment: the wink came after the reply played on.
            Assert.True(wink.GetProperty("actedMs").GetInt64() >=
                result.GetProperty("voice").GetProperty("hold").GetProperty("ResumedAtMs").GetInt64(), result.ToString());
    }
}
