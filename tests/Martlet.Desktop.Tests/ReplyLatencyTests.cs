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
    public void What_the_live_floor_held_and_stopped_is_said_in_the_line_and_read_back_by_latency_report()
    {
        var clock = TimeProvider.System;
        var line = ReplyLatency.Describe(null, clock.GetTimestamp(), clock, Reply(new ConversationTimings()), "Thinking qwen3:8b",
            floor: "held 2 pool jobs, stopped 1 (think longer)")!;
        Assert.Contains(" Live floor: held 2 pool jobs, stopped 1 (think longer). Models: Thinking qwen3:8b.", line);
        var parsed = LatencyReport.Parse(DateTimeOffset.Now, line)!;
        Assert.Equal("held 2 pool jobs, stopped 1 (think longer)", parsed.Floor);
        Assert.Equal("Thinking qwen3:8b", parsed.Models);
        // Without models, and without a live floor part.
        Assert.Equal("stopped 1 (digest)", LatencyReport.Parse(DateTimeOffset.Now, ReplyLatency.Describe(null, clock.GetTimestamp(), clock,
            Reply(new ConversationTimings()), null, floor: "stopped 1 (digest)")!)!.Floor);
        var plain = ReplyLatency.Describe(null, clock.GetTimestamp(), clock, Reply(new ConversationTimings()), "Thinking qwen3:8b")!;
        Assert.DoesNotContain("Live floor", plain);
        Assert.Null(LatencyReport.Parse(DateTimeOffset.Now, plain)!.Floor);
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
