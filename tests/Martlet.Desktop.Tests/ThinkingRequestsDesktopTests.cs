using System.Text.Json;
using Martlet.Conversation;
using Martlet.Desktop;

namespace Martlet.Desktop.Tests;

public sealed class ThinkingRequestsDesktopTests
{
    private const string Topic = "the user's secret plans for Friday";
    private const string Output = "The user plans a picnic on Friday at noon.";
    private const string Sent = "Think about the user's lunch at the lakeside.";

    private static ThinkingRequests Requests()
    {
        var requests = new ThinkingRequests();
        BackgroundPlace busy = new("host:busy", "busy") { Model = "qwen3:8b" }, ok = new("host:ok", "ok") { Model = "gemma4:e4b" };
        var request = requests.Post(new(ThinkingJobKind.ThinkLonger, ThinkingRequestSource.Conversation, "think-1")
        {
            Topic = Topic, JobId = "think-1", Origin = "Diva", Timeout = TimeSpan.FromMinutes(5)
        });
        request.Asked(ThinkingRequestInput.Of("You are Diva.", Sent));
        request.Begin(busy);
        request.End("stopped for the conversation", paused: true, preempted: true);
        request.Asked(ThinkingRequestInput.Of("You are Diva.", Sent));
        request.Begin(ok);
        request.Finish(ThinkingRequestState.Succeeded, text: Output);
        requests.Post(new(ThinkingJobKind.Digest, ThinkingRequestSource.Pool, "digest-1")
        {
            Task = "a summary of the screen", Input = ThinkingRequestInput.Of("Summarize.", "lakeside")
        });
        return requests;
    }

    [Fact]
    public void ThinkingRequests_status_json_has_totals_and_tries_but_never_the_topic()
    {
        var requests = Requests();
        var json = LiveConversationController.RequestsJson(requests, new BackgroundPlaces(), DateTimeOffset.UtcNow);
        Assert.DoesNotContain(Topic, json, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", json, StringComparison.Ordinal);
        Assert.DoesNotContain("picnic", json, StringComparison.Ordinal);
        Assert.DoesNotContain("lakeside", json, StringComparison.Ordinal);
        Assert.DoesNotContain("Diva.", json, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal((1, 2), (root.GetProperty("active").GetInt32(), root.GetProperty("kept").GetInt32()));
        var think = root.GetProperty("totals").GetProperty("think-longer");
        Assert.Equal((1, 1, 1, 1), (think.GetProperty("count").GetInt32(), think.GetProperty("succeeded").GetInt32(),
            think.GetProperty("retries").GetInt32(), think.GetProperty("preemptions").GetInt32()));
        var list = root.GetProperty("requests").EnumerateArray().ToArray();
        Assert.Equal(("digest", "Waiting", "a summary of the screen"),
            (list[0].GetProperty("kind").GetString(), list[0].GetProperty("state").GetString(), list[0].GetProperty("task").GetString()));
        var done = list[1];
        Assert.Equal(("Conversation", "think-1", "Diva", "Succeeded"), (done.GetProperty("source").GetString(), done.GetProperty("jobId").GetString(),
            done.GetProperty("origin").GetString(), done.GetProperty("state").GetString()));
        Assert.Equal("Think longer", done.GetProperty("task").GetString());
        var attempts = done.GetProperty("attempts").EnumerateArray().ToArray();
        Assert.Equal(2, attempts.Length);
        Assert.Equal(("host:busy", "qwen3:8b", "stopped for the conversation"), (attempts[0].GetProperty("memberId").GetString(),
            attempts[0].GetProperty("model").GetString(), attempts[0].GetProperty("ending").GetString()));
        Assert.Equal(("ok", "answered"), (attempts[1].GetProperty("member").GetString(), attempts[1].GetProperty("ending").GetString()));
        Assert.Equal((1, 1, Output.Length), (done.GetProperty("retries").GetInt32(), done.GetProperty("preemptions").GetInt32(),
            done.GetProperty("answerCharacters").GetInt32()));
        var sentText = $"[Instructions]\nYou are Diva.\n\n[Message]\n{Sent}";
        Assert.Equal((sentText.Length, 2), (done.GetProperty("inputCharacters").GetInt32(), done.GetProperty("inputs").GetInt32()));
        Assert.Equal((0, "[Instructions]\nSummarize.\n\n[Message]\nlakeside".Length),
            (list[0].GetProperty("inputs").GetInt32(), list[0].GetProperty("inputCharacters").GetInt32()));
        Assert.Equal(300_000, done.GetProperty("timeoutMs").GetDouble());
        foreach (var name in new[] { "firstWaitMs", "waitedMs", "ranMs", "totalMs" })
            Assert.True(done.GetProperty(name).GetDouble() >= 0, name);
    }

    [Fact]
    public void ThinkingRequests_detail_text_says_everything_but_the_topic()
    {
        var done = Requests().List().Single(r => r.Done);
        var text = MainWindow.Describe(done);
        Assert.DoesNotContain(Topic, text, StringComparison.Ordinal);
        Assert.DoesNotContain("picnic", text, StringComparison.Ordinal);
        Assert.StartsWith($"{done.Id}  Think longer", text, StringComparison.Ordinal);
        Assert.Contains("State:       Done", text, StringComparison.Ordinal);
        Assert.Contains("For:         Diva (Conversation's background work think-1)", text, StringComparison.Ordinal);
        Assert.Contains("Tries:       2 (1 retry), stopped for the conversation 1 time", text, StringComparison.Ordinal);
        Assert.Contains($"Answer:      {Output.Length} characters", text, StringComparison.Ordinal);
        Assert.Contains("1. busy (qwen3:8b) at ", text, StringComparison.Ordinal);
        Assert.Contains(": stopped for the conversation", text, StringComparison.Ordinal);
        Assert.Contains("2. ok (gemma4:e4b) at ", text, StringComparison.Ordinal);
        Assert.Contains("Limits:      time limit ", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ThinkingRequests_output_line_gives_the_length_or_why_there_is_none_but_never_the_output()
    {
        var requests = Requests();
        var done = requests.List().Single(r => r.Done);
        Assert.Equal(Output, done.Output);
        Assert.Equal($"Output: {Output.Length} characters.", MainWindow.OutputLine(done));
        Assert.Equal("Output: none yet. It shows here when the request ends.", MainWindow.OutputLine(requests.List().Single(r => r.Active)));

        var cut = requests.Post(new(ThinkingJobKind.Research, ThinkingRequestSource.Conversation, "research-1"));
        cut.Finish(ThinkingRequestState.Succeeded, wasCut: true, text: new string('y', ThinkingRequests.OutputKept + 1));
        var failed = requests.Post(new(ThinkingJobKind.Memory, ThinkingRequestSource.Pool, "memory-1"));
        failed.Finish(ThinkingRequestState.TimedOut);
        var empty = requests.Post(new(ThinkingJobKind.Naming, ThinkingRequestSource.Pool, "naming-1"));
        empty.Finish(ThinkingRequestState.Succeeded, text: "");
        var list = requests.List();
        Assert.Equal($"Output: the first {ThinkingRequests.OutputKept:N0} of {ThinkingRequests.OutputKept + 1:N0} characters, cut at the token limit.",
            MainWindow.OutputLine(list.Single(r => r.Kind == ThinkingJobKind.Research)));
        Assert.Equal("Output: none. It ended: timed out.", MainWindow.OutputLine(list.Single(r => r.Kind == ThinkingJobKind.Memory)));
        Assert.Equal("Output: none. The answer was empty.", MainWindow.OutputLine(list.Single(r => r.Kind == ThinkingJobKind.Naming)));
        Assert.All(list, r => Assert.DoesNotContain("picnic", MainWindow.OutputLine(r), StringComparison.Ordinal));
    }

    [Fact]
    public void ThinkingRequests_input_line_gives_the_length_and_whether_it_was_sent_but_never_the_input()
    {
        var requests = Requests();
        var list = requests.List();
        var done = list.Single(r => r.Done);
        Assert.Equal($"[Instructions]\nYou are Diva.\n\n[Message]\n{Sent}", done.Input?.Text);
        var length = done.Input!.Length;
        Assert.Equal($"Input: {length} characters, the newest of 2 requests it sent.", MainWindow.InputLine(done));
        var waiting = list.Single(r => r.Active);
        Assert.Equal($"Input: {waiting.Input!.Length} characters, not sent yet.", MainWindow.InputLine(waiting));

        var later = requests.Post(new(ThinkingJobKind.Research, ThinkingRequestSource.Conversation, "research-1"));
        Assert.Equal("Input: none yet. It shows here when the request is sent.", MainWindow.InputLine(requests.List().Single(r => r.Id == later.Id)));
        later.Finish(ThinkingRequestState.Canceled);
        Assert.Equal("Input: none. It wasn't sent.", MainWindow.InputLine(requests.List().Single(r => r.Id == later.Id)));
        var never = requests.Post(new(ThinkingJobKind.Naming, ThinkingRequestSource.Pool, "naming-1") { Input = ThinkingRequestInput.Of(null, "x") });
        never.Finish(ThinkingRequestState.NoMember);
        Assert.Equal("Input: 11 characters, never sent.", MainWindow.InputLine(requests.List().Single(r => r.Id == never.Id)));
        var huge = requests.Post(new(ThinkingJobKind.Memory, ThinkingRequestSource.Pool, "memory-1")
        {
            Input = ThinkingRequestInput.Of(null, new string('z', 40_000))
        });
        huge.Begin(new BackgroundPlace("host:a", "a"));
        var cut = requests.List().Single(r => r.Id == huge.Id);
        Assert.Equal($"Input: {40_010:N0} characters, {40_010 - ThinkingRequestInput.Kept:N0} in the middle left out here.", MainWindow.InputLine(cut));
        Assert.All(requests.List(), r => Assert.DoesNotContain("lakeside", MainWindow.InputLine(r), StringComparison.Ordinal));
    }
}
