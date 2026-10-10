using System.Text.Json;
using Martlet.Conversation;
using Martlet.Mcp;

namespace Martlet.Desktop.Tests;

/// <summary>The Thinking trace through the real controller: a reply is traced from what came before its request to its end, and
/// MCP's thinking_trace puts the lines back together.</summary>
public sealed class ThinkingTraceDesktopTests
{
    [Fact]
    public async Task A_reply_is_traced_from_its_preparation_to_its_end_and_thinking_trace_reads_it_back()
    {
        var heard = new List<(DateTimeOffset At, string Line)>();
        using var listening = ThinkingTrace.Listen(line =>
        {
            lock (heard) heard.Add((DateTimeOffset.Now, line));
        });
        await using var fixture = await LiveFixture.Create();
        fixture.Answer("Sure.");
        var typed = fixture.Start("What do you think?");
        await fixture.Finish(typed);
        var name = typed.Turn!.TraceName!;
        Assert.Matches(@"^Thinking turn \d+ \(reply\)$", name);
        (DateTimeOffset At, string Line)[] Mine()
        {
            lock (heard) return [.. heard.Where(line => line.Line.StartsWith(name + " ", StringComparison.Ordinal) || line.Line.StartsWith(name + ":", StringComparison.Ordinal))];
        }
        await LiveConversationTests.Until(() => Mine().Any(line => line.Line.Contains(" started: ", StringComparison.Ordinal)));

        var lines = Mine().Select(line => line.Line).ToArray();
        Assert.Contains(lines, line => line.StartsWith(name + " started: ", StringComparison.Ordinal));
        var prepared = Assert.Single(lines, line => line.StartsWith(name + ": prepared ", StringComparison.Ordinal));
        Assert.Matches(@": prepared \d+ ms after you sent your message \(.*building the request \d+\)\.$", prepared);
        Assert.Contains(lines, line => line.StartsWith(name + ": request 1 (first) answered at ", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.StartsWith(name + " ended at ", StringComparison.Ordinal) && line.Contains(" ms: Completed; 1 request, "));
        Assert.DoesNotContain(lines, line => line.Contains("What do you think", StringComparison.Ordinal) || line.Contains("Sure.", StringComparison.Ordinal));

        using var report = JsonDocument.Parse(JsonSerializer.Serialize(ThinkingTraceReport.Summarize(Mine().Select(line => (line.At, line.Line)), 5)));
        var root = report.RootElement;
        Assert.Equal(1, root.GetProperty("turns").GetInt32());
        Assert.Equal(0, root.GetProperty("unfinishedTurns").GetInt32());
        var turn = Assert.Single(root.GetProperty("newest").EnumerateArray());
        Assert.Equal(name, turn.GetProperty("name").GetString());
        Assert.Equal("reply", turn.GetProperty("purpose").GetString());
        Assert.Equal("Completed", turn.GetProperty("state").GetString());
        Assert.Equal(1, turn.GetProperty("requests").GetInt32());
        Assert.True(turn.GetProperty("firstWordsMs").GetDouble() >= 0);
        Assert.Equal(lines.Length, turn.GetProperty("lines").GetArrayLength());
    }

    [Fact]
    public void Thinking_trace_reads_waits_and_pool_requests_and_starts_again_with_each_run()
    {
        var at = new DateTimeOffset(2026, 10, 9, 20, 0, 0, TimeSpan.Zero);
        (DateTimeOffset, string)[] lines =
        [
            (at, "Thinking turn 1 (image sense job on diva (llava)): prepared 4 ms after you sent your message (building the request 4)."),
            (at.AddMilliseconds(1), "Thinking turn 1 (image sense job on diva (llava)) started: Martlet host diva, model llava; input of about 9 tokens."),
            (at.AddSeconds(2), "Thinking turn 1 (image sense job on diva (llava)): still waiting at 2004 ms for request 1's answer (2001 ms so far; no response yet)."),
            (at.AddSeconds(3), "Thinking turn 1 (image sense job on diva (llava)): still waiting at 3100 ms: no new words from request 1 for 2000 ms (40 characters so far)."),
            (at.AddSeconds(4), "Thinking turn 1 (image sense job on diva (llava)): request 1 (first) answered at 4000 ms: authorized at 1 ms, first words at 1100 ms."),
            (at.AddSeconds(4), "Thinking turn 1 (image sense job on diva (llava)) ended at 4002 ms: Completed; 1 request, first words at 1100 ms, 80 characters."),
            (at.AddSeconds(5), "Thinking request tr-1 (memory, memory-1): waits in line: a pool job, priority 2, needs Text."),
            (at.AddSeconds(6), "Thinking request tr-1 (memory, memory-1): Succeeded after 1000 ms: waited 400 ms, ran 600 ms, 1 try (last on diva)."),
            // Martlet started again: the numbers start again too.
            (at.AddMinutes(1), "Thinking turn 1 (reply) started: OpenAI Responses, model gpt-5-mini; input of about 9 tokens."),
            (at.AddMinutes(1).AddSeconds(1), "Thinking request tr-1 (digest, digest-1): waits in line: a pool job, priority 1, needs Text.")
        ];
        using var report = JsonDocument.Parse(JsonSerializer.Serialize(ThinkingTraceReport.Summarize(lines, 10)));
        var root = report.RootElement;
        Assert.Equal((2, 1, 2, 2), (root.GetProperty("turns").GetInt32(), root.GetProperty("unfinishedTurns").GetInt32(),
            root.GetProperty("stillWaitingLines").GetInt32(), root.GetProperty("poolRequests").GetInt32()));
        var newest = root.GetProperty("newest").EnumerateArray().ToArray();
        Assert.Equal(("reply", JsonValueKind.Null), (newest[0].GetProperty("purpose").GetString(), newest[0].GetProperty("state").ValueKind));
        var sense = newest[1];
        Assert.Equal(("image sense job on diva (llava)", "Martlet host diva", "llava", "Completed"), (sense.GetProperty("purpose").GetString(),
            sense.GetProperty("route").GetString(), sense.GetProperty("model").GetString(), sense.GetProperty("state").GetString()));
        Assert.Equal((4002, 1100, 1, 6), (sense.GetProperty("totalMs").GetDouble(), sense.GetProperty("firstWordsMs").GetDouble(),
            sense.GetProperty("requests").GetInt32(), sense.GetProperty("lines").GetArrayLength()));
        var waits = sense.GetProperty("waits").EnumerateArray().ToArray();
        Assert.Equal(("request 1's answer", 2001), (waits[0].GetProperty("what").GetString(), waits[0].GetProperty("waitedMs").GetDouble()));
        Assert.Equal(("the next words", 2000), (waits[1].GetProperty("what").GetString(), waits[1].GetProperty("waitedMs").GetDouble()));
        var pool = root.GetProperty("newestPoolRequests").EnumerateArray().ToArray();
        Assert.Equal(("digest", JsonValueKind.Null), (pool[0].GetProperty("kind").GetString(), pool[0].GetProperty("state").ValueKind));
        Assert.Equal(("memory", "Succeeded", 400, 600), (pool[1].GetProperty("kind").GetString(), pool[1].GetProperty("state").GetString(),
            pool[1].GetProperty("waitedMs").GetDouble(), pool[1].GetProperty("ranMs").GetDouble()));

        using var filtered = JsonDocument.Parse(JsonSerializer.Serialize(ThinkingTraceReport.Summarize(lines, 10, contains: "still waiting")));
        Assert.Single(filtered.RootElement.GetProperty("newest").EnumerateArray());
        Assert.Empty(filtered.RootElement.GetProperty("newestPoolRequests").EnumerateArray());
    }

    [Fact]
    public void What_came_before_a_request_is_each_wait_from_the_moment_that_counts()
    {
        var clock = TimeProvider.System;
        var start = clock.GetTimestamp();
        var timeline = new ReplyTimeline(clock, ReplyTimeline.YouStopped, start);
        var second = clock.TimestampFrequency;
        timeline.Mark(ReplyLatency.EndOfSpeech, start + second * 8 / 10);
        timeline.Mark("speech-to-text", start + second);
        timeline.Mark("building the request", start + second * 11 / 10);
        // A step after the request started belongs to the reply, not to what came before it.
        timeline.Mark("later", start + second * 2);
        Assert.Equal("1100 ms after you stopped talking (end of speech 800, speech-to-text 200, building the request 100)",
            ReplyLatency.Before(timeline, start + second * 11 / 10));
        Assert.Equal("0 ms after you stopped talking", ReplyLatency.Before(new ReplyTimeline(clock, ReplyTimeline.YouStopped, start), start));
    }
}
