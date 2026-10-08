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
    public async Task A_turn_parakeet_heard_because_listening_failed_names_it_in_the_line()
    {
        await using var fixture = await LiveFixture.Create();
        var configured = fixture.Controller.Configuration!;
        Assert.EndsWith(", voice gpt-4o-mini-tts-2025-12-15, speech-to-text gpt-transcribe", configured.LatencyModels(spokenInput: true));
        var models = configured.LatencyModels(spokenInput: true, standIn: "parakeet-tdt-110m-en");
        Assert.EndsWith(", speech-to-text parakeet-tdt-110m-en on this PC, standing in for gpt-transcribe", models);
        // The timeline carries it from the utterance to the reply that answers it, and latency_report reads the line back.
        var clock = TimeProvider.System;
        var timeline = new ReplyTimeline(clock, ReplyTimeline.YouStopped) { StandIn = "parakeet-tdt-110m-en" };
        Assert.Equal("parakeet-tdt-110m-en", timeline.Copy().StandIn);
        var line = ReplyLatency.Describe(null, clock.GetTimestamp(), clock, Reply(new()), models)!;
        Assert.Equal(models, LatencyReport.Parse(DateTimeOffset.Now, line)!.Models);
    }

    [Fact]
    public void A_reply_started_early_is_said_in_the_line_and_read_back_by_latency_report()
    {
        var clock = TimeProvider.System;
        var stopped = clock.GetTimestamp() - 2 * clock.TimestampFrequency;
        var started = stopped + (long)(0.262 * clock.TimestampFrequency);
        var timeline = new ReplyTimeline(clock, ReplyTimeline.YouStopped, stopped) { Early = new(true, 2, 1) };
        timeline.Mark(ReplyLatency.EndOfSpeech, stopped + (long)(1.6 * clock.TimestampFrequency));
        var promoted = ReplyLatency.Describe(timeline, started, clock, Reply(new ConversationTimings(StartedEarly: true)), null,
            floor: "held 1 pool job")!;
        // Next to what the live floor did, which the reply held from its start.
        Assert.Contains("Started early at 262 ms, promoted (started early 2 times, 1 cancelled). Live floor: held 1 pool job.", promoted);
        var parsed = LatencyReport.Parse(DateTimeOffset.Now, promoted)!;
        Assert.Equal("held 1 pool job", parsed.Floor);
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
        // The reply started early held the live floor when the turn ended, and no reply held it once the reply was done.
        var floor = early.GetProperty("liveFloor");
        Assert.Equal("Live", floor.GetProperty("atTurnEnd").GetString());
        Assert.Equal(1, floor.GetProperty("repliesAtTurnEnd").GetInt32());
        Assert.Equal(0, floor.GetProperty("repliesWhenDone").GetInt32());
    }

    [Fact]
    public void Backup_thinking_is_said_in_the_line_and_read_back_by_latency_report()
    {
        var clock = TimeProvider.System;
        var stopped = clock.GetTimestamp() - 3 * clock.TimestampFrequency;
        var started = stopped + (long)(0.3 * clock.TimestampFrequency);
        var timeline = new ReplyTimeline(clock, ReplyTimeline.YouStopped, stopped);
        ConversationSnapshot With(ThinkingBackupResult backup) => Reply(new ConversationTimings()) with { Backup = backup };
        // Counted from the same moment as the line's total: 300 ms to the reply's start, then the member was asked at 912 ms.
        var won = ReplyLatency.Describe(timeline, started, clock, With(new(ThinkingBackupOutcome.Won, TimeSpan.FromMilliseconds(900),
            "diva (qwen3-8b)", TimeSpan.FromMilliseconds(912), TimeSpan.FromMilliseconds(1_104))), "Thinking qwen3:8b")!;
        Assert.Contains(" Backup Thinking won at 1404 ms (diva (qwen3-8b), asked at 1212 ms). Models: Thinking qwen3:8b.", won);
        var lost = ReplyLatency.Describe(timeline, started, clock, With(new(ThinkingBackupOutcome.Lost, TimeSpan.FromMilliseconds(900),
            "openrouter.ai (x-ai/grok-4.3)", TimeSpan.FromMilliseconds(900), TimeSpan.FromMilliseconds(1_300))), null)!;
        Assert.Contains(" Backup Thinking asked at 1200 ms (openrouter.ai (x-ai/grok-4.3)); the conversation's model won.", lost);
        var none = ReplyLatency.Describe(timeline, started, clock, With(new(ThinkingBackupOutcome.NoMember, TimeSpan.FromMilliseconds(900))), null)!;
        Assert.Contains(" Backup Thinking: no member could take it.", none);
        // A fast reply asks no member and says nothing about it.
        var fast = ReplyLatency.Describe(timeline, started, clock, With(new(ThinkingBackupOutcome.NotNeeded, TimeSpan.FromMilliseconds(900))), null)!;
        Assert.DoesNotContain("Backup Thinking", fast);

        var parsed = LatencyReport.Parse(DateTimeOffset.Now, won)!;
        Assert.Equal(("won", 1212d, 1404d, "diva (qwen3-8b)"), (parsed.Backup, parsed.BackupAskedMs!.Value, parsed.BackupWonMs!.Value, parsed.BackupMember));
        Assert.Equal("Thinking qwen3:8b", parsed.Models);
        var other = LatencyReport.Parse(DateTimeOffset.Now, lost)!;
        Assert.Equal(("lost", 1200d, "openrouter.ai (x-ai/grok-4.3)"), (other.Backup, other.BackupAskedMs!.Value, other.BackupMember));
        Assert.Equal("no member", LatencyReport.Parse(DateTimeOffset.Now, none)!.Backup);
        Assert.Null(LatencyReport.Parse(DateTimeOffset.Now, fast)!.Backup);
        var summary = System.Text.Json.JsonSerializer.SerializeToElement(LatencyReport.Summarize([parsed, other])).GetProperty("backupThinking");
        Assert.Equal(2, summary.GetProperty("replies").GetInt32());
        Assert.Equal(1, summary.GetProperty("won").GetInt32());
        Assert.Equal(1, summary.GetProperty("lost").GetInt32());
        Assert.Equal(1404, summary.GetProperty("wonAtMs").GetProperty("Median").GetDouble());
    }

    [Fact]
    public async Task The_backup_thinking_check_races_two_fixture_endpoints_through_the_production_runtime()
    {
        // Production race, runtime, Chat Completions adapter and member choice with two fixture endpoints (NOT AI).
        var result = System.Text.Json.JsonSerializer.SerializeToElement(await BackupThinkingCheck.RunAsync("backup-wins", 900, CancellationToken.None));
        Assert.True(result.GetProperty("ok").GetBoolean(), result.ToString());
        var turn = result.GetProperty("scenarios")[0].GetProperty("turn");
        Assert.Equal("Won", turn.GetProperty("Outcome").GetString());
        Assert.NotEqual(System.Text.Json.JsonValueKind.Null, turn.GetProperty("Conversation").GetProperty("StoppedMs").ValueKind);
        Assert.Contains("Backup Thinking won at", turn.GetProperty("Line").GetString());
    }

    [Fact]
    public void A_quick_sound_is_said_in_the_line_and_read_back_by_latency_report()
    {
        var clock = TimeProvider.System;
        var stopped = clock.GetTimestamp() - 2 * clock.TimestampFrequency;
        var started = stopped + (long)(0.3 * clock.TimestampFrequency);
        var timeline = new ReplyTimeline(clock, ReplyTimeline.YouStopped, stopped);
        // Counted from the same moment as the line's total: 300 ms to the reply's start, then 712 ms.
        var line = ReplyLatency.Describe(timeline, started, clock,
            Reply(new ConversationTimings(QuickSoundAfter: TimeSpan.FromMilliseconds(712))), "Thinking qwen3:8b")!;
        Assert.Contains(" Quick sound at 1012 ms. Models: Thinking qwen3:8b.", line);
        var parsed = LatencyReport.Parse(DateTimeOffset.Now, line)!;
        Assert.Equal(1012, parsed.QuickSoundMs);
        var plain = LatencyReport.Parse(DateTimeOffset.Now, ReplyLatency.Describe(null, clock.GetTimestamp(), clock, Reply(new()), null)!)!;
        Assert.Null(plain.QuickSoundMs);
        var summary = System.Text.Json.JsonSerializer.SerializeToElement(LatencyReport.Summarize([parsed, plain])).GetProperty("quickSounds");
        Assert.Equal(1, summary.GetProperty("replies").GetInt32());
        Assert.Equal(1012, summary.GetProperty("atMs").GetProperty("Median").GetDouble());
    }

    [Fact]
    public async Task The_quick_sound_check_plays_one_in_front_of_a_slow_reply_through_the_production_runtime()
    {
        // Production rules, conversation runtime, Chat Completions adapter, host voice stream and playback sink with fixtures (NOT AI).
        var result = System.Text.Json.JsonSerializer.SerializeToElement(await QuickSoundCheck.RunAsync("slow", 700, CancellationToken.None));
        Assert.True(result.GetProperty("ok").GetBoolean(), result.ToString());
        var turn = result.GetProperty("scenarios")[0].GetProperty("turns")[0];
        Assert.True(turn.GetProperty("played").GetBoolean());
        Assert.True(turn.GetProperty("replyFollowedUncut").GetBoolean());
        Assert.Contains("Quick sound at", turn.GetProperty("latencyLine").GetString());
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
