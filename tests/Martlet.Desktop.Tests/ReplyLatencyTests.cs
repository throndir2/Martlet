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
    public void A_reply_answered_from_the_recording_says_so_and_latency_report_counts_it_apart()
    {
        var clock = TimeProvider.System;
        var timeline = new ReplyTimeline(clock, ReplyTimeline.YouStopped, clock.GetTimestamp() - clock.TimestampFrequency);
        var start = clock.GetTimestamp();
        var transcribed = timeline.OriginAt + clock.TimestampFrequency / 5;
        var line = ReplyLatency.Describe(timeline, start, clock, Reply(new ConversationTimings()), "Thinking gemma4:e2b",
            fromRecording: true, transcriptAt: transcribed)!;
        Assert.Contains("Answered from your recording; speech-to-text finished beside it 200 ms after you stopped talking. " +
            "Models: Thinking gemma4:e2b.", line);
        var parsed = LatencyReport.Parse(DateTimeOffset.Now, line)!;
        Assert.True(parsed.FromRecording);
        Assert.Equal(200, parsed.TranscriptMs);
        Assert.Equal("Thinking gemma4:e2b", parsed.Models);
        var words = LatencyReport.Parse(DateTimeOffset.Now, ReplyLatency.Describe(timeline, start, clock, Reply(new ConversationTimings()), null)!)!;
        Assert.False(words.FromRecording);
        var summary = System.Text.Json.JsonSerializer.SerializeToElement(LatencyReport.Summarize([parsed, words]));
        Assert.Equal(1, summary.GetProperty("firstAudioFromRecording").GetProperty("Count").GetInt32());
        Assert.Equal(1, summary.GetProperty("firstAudioFromWords").GetProperty("Count").GetInt32());
        Assert.Equal(200, summary.GetProperty("transcriptBeside").GetProperty("Median").GetDouble());
    }

    [Fact]
    public async Task Hearing_check_rehearses_a_reply_from_the_recording_alone()
    {
        // The production Chat Completions adapter against a fixture endpoint on 127.0.0.1 (NOT AI), on an empty data directory.
        var directory = Path.Combine(Path.GetTempPath(), "Martlet.HearingCheck." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var result = System.Text.Json.JsonSerializer.SerializeToElement(await HearingCheck.RunAsync("gemma4:e2b", directory, CancellationToken.None));
            var fixture = result.GetProperty("fixture");
            Assert.True(fixture.GetProperty("ok").GetBoolean(), result.ToString());
            var alone = fixture.GetProperty("recordingOnly");
            Assert.True(alone.GetProperty("messageIsHeardOnlyPrompt").GetBoolean());
            Assert.Equal(["text", "input_audio"], alone.GetProperty("contentParts").EnumerateArray().Select(p => p.GetString()));
            Assert.True(result.GetProperty("answerDecisions").GetProperty("ok").GetBoolean(), result.ToString());
            Assert.True(result.GetProperty("answerFromVoice").GetBoolean());
        }
        finally { Directory.Delete(directory, recursive: true); }
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
