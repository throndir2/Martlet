using System.Text.Json;
using Martlet.Conversation;
using Martlet.Desktop;

namespace Martlet.Desktop.Tests;

public sealed class ThinkingRequestsDesktopTests
{
    private const string Topic = "the user's secret plans for Friday";

    private static ThinkingRequests Requests()
    {
        var requests = new ThinkingRequests();
        BackgroundPlace busy = new("host:busy", "busy") { Model = "qwen3:8b" }, ok = new("host:ok", "ok") { Model = "gemma4:e4b" };
        var request = requests.Post(new(ThinkingJobKind.ThinkLonger, ThinkingRequestSource.Conversation, "think-1")
        {
            Topic = Topic, JobId = "think-1", Origin = "Diva", Timeout = TimeSpan.FromMinutes(5)
        });
        request.Begin(busy);
        request.End("stopped for the conversation", paused: true, preempted: true);
        request.Begin(ok);
        request.Finish(ThinkingRequestState.Succeeded, answer: 42);
        requests.Post(new(ThinkingJobKind.Digest, ThinkingRequestSource.Pool, "digest-1") { Task = "a summary of the screen" });
        return requests;
    }

    [Fact]
    public void ThinkingRequests_status_json_has_totals_and_tries_but_never_the_topic()
    {
        var requests = Requests();
        var json = LiveConversationController.RequestsJson(requests, new BackgroundPlaces(), DateTimeOffset.UtcNow);
        Assert.DoesNotContain(Topic, json, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", json, StringComparison.Ordinal);
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
        Assert.Equal((1, 1, 42), (done.GetProperty("retries").GetInt32(), done.GetProperty("preemptions").GetInt32(),
            done.GetProperty("answerCharacters").GetInt32()));
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
        Assert.StartsWith($"{done.Id}  Think longer", text, StringComparison.Ordinal);
        Assert.Contains("State:       Done", text, StringComparison.Ordinal);
        Assert.Contains("For:         Diva (Conversation's background work think-1)", text, StringComparison.Ordinal);
        Assert.Contains("Tries:       2 (1 retry), stopped for the conversation 1 time", text, StringComparison.Ordinal);
        Assert.Contains("Answer:      42 characters", text, StringComparison.Ordinal);
        Assert.Contains("1. busy (qwen3:8b) at ", text, StringComparison.Ordinal);
        Assert.Contains(": stopped for the conversation", text, StringComparison.Ordinal);
        Assert.Contains("2. ok (gemma4:e4b) at ", text, StringComparison.Ordinal);
        Assert.Contains("Limits:      time limit ", text, StringComparison.Ordinal);
    }
}
