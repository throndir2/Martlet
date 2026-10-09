using System.Net;
using System.Text.Json.Nodes;
using Martlet.Conversation;
using Martlet.Core.Settings;
using Martlet.Providers;
using Martlet.Providers.Tests;

namespace Martlet.Desktop.Tests;

// NOT AI: the fixture's Chat Completions handler stands in for NVIDIA Build (https://integrate.api.nvidia.com/v1) as a Thinking
// pool member. No request leaves this PC and no real key is used.
public sealed class ThinkingPoolRateLimitTests
{
    private static readonly DeepThinkingSettings Nvidia = new()
    {
        Place = DeepThinkingPlace.Endpoint, Origin = ChatCompletionsEndpointCatalog.NvidiaBuildBaseUrl,
        ModelId = "nvidia/llama-3.3-nemotron-super-49b-v1", Slots = 4
    };

    private static HttpResponseMessage ChatReply(string words) => TextRecordingHandler.Sse(
        "data: {\"id\":\"chat-fixture\",\"object\":\"chat.completion.chunk\",\"model\":\"nemotron\",\"choices\":[{\"index\":0,\"delta\":" +
        "{\"role\":\"assistant\",\"content\":\"" + words + "\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n");

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, "is limiting requests")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "is busy (server error)")]
    public async Task A_busy_NVIDIA_Build_member_cools_down_and_its_job_runs_there_later_instead_of_failing(HttpStatusCode status, string said)
    {
        await using var fixture = await LiveFixture.Create();
        var calls = 0;
        fixture.Chat.Respond = (_, _) =>
        {
            if (Interlocked.Increment(ref calls) > 1) return Task.FromResult(ChatReply("Nemotron's answer."));
            var busy = new HttpResponseMessage(status) { Content = new StringContent("{}") };
            busy.Headers.RetryAfter = new(TimeSpan.FromSeconds(30));
            return Task.FromResult(busy);
        };
        fixture.Controller.PoolSettings = new ThinkingPoolSettings { Members = [Nvidia] };
        var pool = fixture.Controller.ThinkingPool;
        var job = pool.RunAsync(new ThinkingJob { Kind = ThinkingJobKind.Memory, Instructions = "Answer.", Text = "fixture", Timeout = TimeSpan.FromMinutes(2) },
            CancellationToken.None);

        await fixture.Advance(() => pool.Status().Cooling.Count == 1);
        var cooling = pool.Status().Cooling.Single();
        Assert.Equal((Nvidia.Key, 2, 4), (cooling.Id, cooling.SlotsNow, cooling.Slots));
        Assert.EndsWith(said, cooling.Problem, StringComparison.Ordinal);
        // fixture.Advance moves the clock on in small steps while it waits.
        Assert.InRange(cooling.Until!.Value - fixture.Clock.GetUtcNow(), TimeSpan.FromSeconds(29), TimeSpan.FromSeconds(30));
        var file = JsonNode.Parse(fixture.Controller.PoolStatusJson())!;
        var entry = file["cooling"]!.AsArray().Single()!;
        Assert.Equal((2, 4), (entry["slotsNow"]!.GetValue<int>(), entry["slots"]!.GetValue<int>()));
        Assert.Contains("tries again in 30 s", entry["says"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal(0, file["members"]!.AsArray().Single()!["slotsNow"]!.GetValue<int>());
        Assert.False(job.IsCompleted);

        fixture.Clock.Advance(TimeSpan.FromSeconds(30));
        var result = await job.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal((ThinkingJobOutcome.Succeeded, "Nemotron's answer.", 2), (result.Outcome, result.Text, result.Attempts));
        Assert.Equal(2, fixture.Chat.Calls);
    }
}
