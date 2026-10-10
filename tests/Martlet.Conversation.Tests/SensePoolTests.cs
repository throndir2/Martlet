using Martlet.Core.Cluster;
using Martlet.Core.Settings;
using Martlet.Providers;

namespace Martlet.Conversation.Tests;

// The image and audio models' pools (docs/SENSE_MODELS.md) with simulated members, NOT models.
public sealed class SensePoolTests
{
    private const string Ollama = GenerationSupport.LocalOllamaChatBaseUrl, Cloud = "https://openrouter.ai/api/v1";

    private static readonly SetupRoute Thinking = new()
    {
        RouteType = SetupRouteType.ChatCompletions, Role = SetupRole.Llm, ProviderAlias = ChatCompletionsSetup.Alias,
        Origin = Ollama, ModelId = "gemma4:e2b", ConfigurationRevision = Guid.NewGuid(), Enabled = true
    };

    private static DeepThinkingSettings Endpoint(string model, string origin = Ollama) =>
        new() { Place = DeepThinkingPlace.Endpoint, Origin = origin, ModelId = model };

    private static DeepThinkingSettings Host(string host, string model) => new()
    {
        Place = DeepThinkingPlace.Host, ModelId = model, HostId = host, HostOrigin = $"https://{host}.local:9443",
        HostSpkiFingerprint = "sha256:" + new string('0', 64), HostDeviceId = "device", HostCredentialId = Guid.NewGuid()
    };

    private static readonly DeepThinkingSettings Chosen = Host("diva", "qwen2.5vl:7b");

    // A cloud model Martlet found sees (a test request), outside this PC and your computers.
    private static readonly DeepThinkingSettings CloudEyes = Endpoint("house-model-1", Cloud);

    private static readonly ModelAbilities Found = new ModelAbilities().With(new()
    {
        Origin = Cloud, ModelId = "house-model-1", Sees = true, Source = "a test request", CheckedAt = DateTimeOffset.UtcNow
    });

    [Fact]
    public void The_chosen_model_comes_first_then_the_lists_other_models_that_may_take_the_kind()
    {
        var ripley = Host("ripley", "qwen2.5vl:32b");
        IReadOnlyList<DeepThinkingSettings> list = [Host("text-only", "qwen3:8b"), CloudEyes, Endpoint("gemma4:e2b"), Chosen, ripley,
            Host("friend", "qwen2.5vl:7b"), Endpoint("qwen2.5vl:7b")];

        // The first model that isn't known not to see is chosen: the text-only model is passed over.
        Assert.Same(CloudEyes, SensePool.Chosen(SenseKind.Image, list, null));
        Assert.Equal(list[0], SensePool.Chosen(SenseKind.Image, [list[0]], null));
        Assert.Null(SensePool.Chosen(SenseKind.Image, [], null));

        var members = SensePool.Members(SenseKind.Image, Chosen, list, Thinking, Found, m => m.HostId == "friend");

        // Left out: the text-only model, the conversation's own Thinking model, diva again, and the computer leaveOut names. The
        // owner put the cloud model in the list, so a model Martlet can't tell about is tried too.
        Assert.Equal([Chosen.Key, CloudEyes.Key, ripley.Key, "endpoint:" + Ollama + "|qwen2.5vl:7b"], members.Select(m => m.Key));
        Assert.Same(Chosen, members[0]);
        Assert.Contains(CloudEyes.Key, SensePool.Members(SenseKind.Image, Chosen, list, Thinking, null).Select(m => m.Key));
        // A model found not to see is left out.
        var refused = new ModelAbilities().With(new() { Origin = Cloud, ModelId = "house-model-1", Sees = false, Source = "a refused picture", CheckedAt = DateTimeOffset.UtcNow });
        Assert.DoesNotContain(CloudEyes.Key, SensePool.Members(SenseKind.Image, Chosen, list, Thinking, refused).Select(m => m.Key));
    }

    [Fact]
    public void The_audio_pool_leaves_out_models_that_dont_hear_and_a_paired_computer_hears_through_its_gateway()
    {
        var ears = Endpoint("gemma3n:e4b");
        var chosen = Endpoint("gemma3n:e2b", "https://10.77.0.20:8443/v1");
        var hostEars = Host("ripley", "gemma4:e4b");

        Assert.Equal([chosen.Key, hostEars.Key, ears.Key],
            SensePool.Members(SenseKind.Audio, chosen, [Host("diva", "qwen2.5vl:7b"), hostEars, Endpoint("qwen2.5vl:7b"), ears], Thinking, null).Select(m => m.Key));
        Assert.Equal([chosen.Key], SensePool.Members(SenseKind.Audio, chosen, null, Thinking, null).Select(m => m.Key));
        Assert.Equal(HearingSupport.Supported, SenseRouting.Hears(hostEars, null));
    }

    [Fact]
    public void A_pool_never_has_more_than_its_maximum_members()
    {
        var list = Enumerable.Range(1, 12).Select(i => Host($"pc{i}", "qwen2.5vl:7b")).ToArray();
        Assert.Equal(SensePool.MaximumMembers, SensePool.Members(SenseKind.Image, Chosen, list, Thinking, null).Count);
    }

    private static readonly DeepThinkingSettings Ripley = Host("ripley", "qwen2.5vl:7b"), Kirk = Host("kirk", "qwen2.5vl:7b");

    private static Task<(SenseAnswer Answer, SensePoolRoute Route)> Run(Func<DeepThinkingSettings, CancellationToken, Task<SenseAttempt>> attempt,
        TimeSpan? until = null, params DeepThinkingSettings[] members) =>
        SensePool.RunAsync(SenseKind.Image, members.Length == 0 ? [Chosen, Ripley, Kirk] : members, attempt,
            DateTimeOffset.UtcNow + (until ?? TimeSpan.FromSeconds(5)), null, CancellationToken.None,
            new WorkQueue { Retry = TimeSpan.FromMilliseconds(10) });

    [Fact]
    public async Task A_free_chosen_model_takes_the_job_with_one_request_and_no_wait()
    {
        var asked = new List<string>();
        var (answer, route) = await Run((member, _) =>
        {
            asked.Add(member.HostId!);
            return Task.FromResult(SenseAttempt.Done(SenseAnswer.Done("A boss fight.")));
        });

        Assert.Equal("A boss fight.", answer.Text);
        Assert.Equal(["diva"], asked);
        Assert.Equal((Chosen.Key, 0, 0, 0, false), (route.Member, route.Position, route.Busy, route.Unavailable, route.Elsewhere));
    }

    [Fact]
    public async Task A_busy_or_unreachable_member_passes_the_job_to_the_next_at_once()
    {
        var asked = new List<string>();
        var (answer, route) = await Run((member, _) =>
        {
            asked.Add(member.HostId!);
            return Task.FromResult(member.HostId switch
            {
                "diva" => SenseAttempt.Busy("diva is busy with another job"),
                "ripley" => SenseAttempt.Unavailable("ripley is offline"),
                _ => SenseAttempt.Done(SenseAnswer.Done("Words from kirk."))
            });
        });

        Assert.Equal("Words from kirk.", answer.Text);
        Assert.Equal(["diva", "ripley", "kirk"], asked);
        Assert.Equal((Kirk.Key, 2, 1, 1, true), (route.Member, route.Position, route.Busy, route.Unavailable, route.Elsewhere));
        Assert.Equal("kirk (qwen2.5vl:7b)", route.Name);
    }

    [Fact]
    public async Task When_every_member_is_busy_the_job_waits_for_whichever_frees_first()
    {
        var freed = DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(150);
        var (answer, route) = await Run((member, _) => Task.FromResult(member.HostId == "ripley" && DateTimeOffset.UtcNow >= freed
            ? SenseAttempt.Done(SenseAnswer.Done("Words from ripley."))
            : SenseAttempt.Busy($"{member.HostId} is busy")));

        Assert.Equal("Words from ripley.", answer.Text);
        Assert.Equal(1, route.Position);
        Assert.True(route.Busy >= 3);
        Assert.True(route.Waited >= TimeSpan.FromMilliseconds(100), $"waited {route.Waited.TotalMilliseconds} ms");
    }

    [Fact]
    public async Task A_busy_pool_gives_up_at_the_deadline_with_the_last_reason()
    {
        var (answer, route) = await Run((member, _) => Task.FromResult(SenseAttempt.Busy($"{member.HostId} is busy")),
            TimeSpan.FromMilliseconds(80));

        Assert.Null(answer.Text);
        Assert.EndsWith("is busy", answer.Problem);
        Assert.Null(route.Member);
        Assert.Equal(-1, route.Position);
    }

    [Fact]
    public async Task A_real_failure_ends_the_job_without_asking_another_member()
    {
        var asked = 0;
        var (answer, route) = await Run((_, _) =>
        {
            asked++;
            return Task.FromResult(SenseAttempt.Done(SenseAnswer.Failed("the job is too large")));
        });

        Assert.Equal(1, asked);
        Assert.Equal("the job is too large", answer.Problem);
        Assert.Equal(0, route.Position);
    }

    [Fact]
    public async Task A_member_held_for_a_live_turn_is_passed_over_and_a_pool_held_everywhere_ends_without_waiting()
    {
        var (answer, route) = await Run((member, _) => Task.FromResult(member.HostId == "diva"
            ? SenseAttempt.Held("diva keeps its graphics card for a live conversation")
            : SenseAttempt.Done(SenseAnswer.Done("Words from ripley."))));
        Assert.Equal(("Words from ripley.", 1, 1), (answer.Text, route.Position, route.Unavailable));

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var (held, _) = await Run((member, _) => Task.FromResult(SenseAttempt.Held($"{member.HostId} keeps its card")));
        Assert.Contains("keeps its graphics card for a live conversation", held.Problem);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task A_refused_picture_from_the_chosen_model_ends_the_job_as_a_refusal()
    {
        var (answer, route) = await Run((_, _) => Task.FromResult(SenseAttempt.Done(SenseAnswer.Rejected("diva refused the picture"))));
        Assert.True(answer.Refused);
        Assert.Equal(0, route.Position);
    }
}
