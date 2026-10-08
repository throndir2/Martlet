using Martlet.Core.Contracts;
using Martlet.Core.Settings;
using Martlet.Providers;

namespace Martlet.Conversation.Tests;

// The image and audio models' lanes (docs/SENSE_MODELS.md) with a simulated runner, NOT a model.
public sealed class SenseLanesTests
{
    private static readonly SetupRoute TextOnly = new()
    {
        RouteType = SetupRouteType.ChatCompletions, Role = SetupRole.Llm, ProviderAlias = ChatCompletionsSetup.Alias,
        Origin = GenerationSupport.LocalOllamaChatBaseUrl, ModelId = "qwen3:8b", ConfigurationRevision = Guid.NewGuid(), Enabled = true
    };

    private static readonly DeepThinkingSettings Eyes = new()
    {
        Place = DeepThinkingPlace.Endpoint, Origin = "https://10.77.0.20:8443/v1", ModelId = "qwen2.5vl:7b"
    };

    private static readonly DeepThinkingSettings Omni = new()
    {
        Place = DeepThinkingPlace.Endpoint, Origin = "https://integrate.api.nvidia.com/v1", ModelId = "nvidia/nemotron-3-nano-omni-30b-a3b-reasoning"
    };

    private static SenseModel Own(DeepThinkingSettings model) => new() { Source = SenseSource.Own, Own = model };

    private static Func<SenseKind, SenseRoute> Routes(SenseModels senses) => kind => SenseRouting.For(kind, senses, TextOnly, null);

    private static readonly Func<SenseKind, SenseRoute> Described = Routes(new() { Image = Own(Eyes) });

    private static BoundedImage Picture => new([0xFF, 0xD8, 0xFF, .. new byte[32]], ImageMediaType.Jpeg, 4, 4);

    private static BoundedWaveAudio Recording => BoundedWaveAudio.FromPcm(
        new() { SampleRate = 16_000, Channels = 1, Encoding = Martlet.Core.Audio.PcmEncoding.Signed16LittleEndian }, new byte[16_000]);

    private static SenseJob Look(string purpose, string? key = null, int priority = 0, TimeSpan? timeout = null) => new()
    {
        Purpose = purpose, Key = key, Priority = priority, Instructions = "Describe the picture.", Text = "fixture", Image = Picture,
        Timeout = timeout ?? TimeSpan.FromSeconds(10)
    };

    private static SenseJob Listen(string purpose) => new()
    {
        Purpose = purpose, Instructions = "Describe the recording.", Text = "fixture", Audio = Recording
    };

    // Moves the clock on in small steps until the condition holds, letting the work it wakes run in between.
    private static async Task Until(RuntimeClock clock, Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (!condition())
        {
            clock.Advance(TimeSpan.FromMilliseconds(5));
            await Task.Delay(1, timeout.Token);
        }
    }

    private static async Task<SenseJobResult> Done(RuntimeClock clock, Task<SenseJobResult> job)
    {
        await Until(clock, () => job.IsCompleted);
        return await job;
    }

    [Fact]
    public async Task A_kind_without_a_model_of_its_own_answers_at_once_without_a_request()
    {
        var asked = 0;
        var lanes = new SenseLanes(Routes(new()), (_, _, _, _) =>
        {
            asked++;
            return Task.FromResult(SenseAnswer.Done("x"));
        }, new RuntimeClock());
        var result = await lanes.RunAsync(SenseKind.Image, Look("reply picture"), CancellationToken.None);
        Assert.Equal(SenseJobOutcome.NoModel, result.Outcome);
        Assert.Contains("text-only", result.Problem);
        Assert.Equal(0, asked);
    }

    [Fact]
    public async Task One_job_runs_at_a_time_a_newer_picture_replaces_the_waiting_one_and_priority_goes_first()
    {
        var clock = new RuntimeClock();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ran = new List<string>();
        var lanes = new SenseLanes(Described, async (_, model, job, token) =>
        {
            lock (ran) ran.Add(job.Purpose);
            Assert.Equal("qwen2.5vl:7b", model.ModelId);
            if (job.Purpose == "first") await gate.Task.WaitAsync(token);
            return SenseAnswer.Done(job.Purpose + " described");
        }, clock);
        var first = lanes.RunAsync(SenseKind.Image, Look("first"), CancellationToken.None);
        await Until(clock, () => lanes.Status()[0].Busy);
        var summary = lanes.RunAsync(SenseKind.Image, Look("summary", priority: 1), CancellationToken.None);
        var older = lanes.RunAsync(SenseKind.Image, Look("older picture", key: "picture", priority: 5), CancellationToken.None);
        await Until(clock, () => lanes.Status()[0].Waiting == 2);
        var newer = lanes.RunAsync(SenseKind.Image, Look("newer picture", key: "picture", priority: 5), CancellationToken.None);

        var replaced = await older;
        Assert.Equal(SenseJobOutcome.Stale, replaced.Outcome);
        Assert.Equal("a newer job took its place", replaced.Problem);
        gate.SetResult();
        var results = await Task.WhenAll(first, summary, newer);
        Assert.All(results, r => Assert.True(r.Succeeded));
        Assert.Equal(["first", "newer picture", "summary"], ran);
        Assert.Equal("newer picture described", results[2].Text);
        var status = lanes.Status()[0];
        Assert.Equal((false, 0, 3), (status.Busy, status.Waiting, status.Runs));
        Assert.Equal(Eyes.Describe(), status.LastModel);
    }

    [Fact]
    public async Task A_job_its_lane_does_not_free_in_time_is_dropped_and_refusals_failures_and_silence_end_as_such()
    {
        var clock = new RuntimeClock();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<SenseAnswer> Run(SenseKind kind, DeepThinkingSettings model, SenseJob job, CancellationToken token)
        {
            switch (job.Purpose)
            {
                case "busy":
                    await gate.Task.WaitAsync(token);
                    return SenseAnswer.Done("done");
                case "refused": return SenseAnswer.Rejected("it refused the picture");
                case "failed": return SenseAnswer.Failed("it failed");
                case "empty": return SenseAnswer.Done("  ");
                default:
                    await Task.Delay(Timeout.Infinite, token);
                    return SenseAnswer.Done("late");
            }
        }
        var lanes = new SenseLanes(Described, Run, clock);
        var busy = lanes.RunAsync(SenseKind.Image, Look("busy"), CancellationToken.None);
        await Until(clock, () => lanes.Status()[0].Busy);
        var stale = await Done(clock, lanes.RunAsync(SenseKind.Image, Look("waits", timeout: TimeSpan.FromMilliseconds(200)), CancellationToken.None));
        Assert.Equal(SenseJobOutcome.Stale, stale.Outcome);
        gate.SetResult();
        Assert.True((await busy).Succeeded);

        Assert.Equal(SenseJobOutcome.Refused, (await lanes.RunAsync(SenseKind.Image, Look("refused"), CancellationToken.None)).Outcome);
        Assert.Equal(SenseJobOutcome.Failed, (await lanes.RunAsync(SenseKind.Image, Look("failed"), CancellationToken.None)).Outcome);
        var empty = await lanes.RunAsync(SenseKind.Image, Look("empty"), CancellationToken.None);
        Assert.Equal((SenseJobOutcome.Failed, "it came back empty"), (empty.Outcome, empty.Problem));
        var silent = await Done(clock, lanes.RunAsync(SenseKind.Image, Look("silent", timeout: TimeSpan.FromMilliseconds(200)), CancellationToken.None));
        Assert.Equal(SenseJobOutcome.TimedOut, silent.Outcome);
        Assert.True(silent.Took >= TimeSpan.FromMilliseconds(200));
        var status = lanes.Status()[0];
        Assert.Equal((false, 0, 5, SenseJobOutcome.TimedOut, "silent"), (status.Busy, status.Waiting, status.Runs, status.LastOutcome, status.LastPurpose));
    }

    [Fact]
    public async Task Canceling_the_caller_throws_and_frees_the_lane()
    {
        var clock = new RuntimeClock();
        var lanes = new SenseLanes(Described, async (_, _, _, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return SenseAnswer.Done("late");
        }, clock);
        using var stop = new CancellationTokenSource();
        var running = lanes.RunAsync(SenseKind.Image, Look("running"), stop.Token);
        await Until(clock, () => lanes.Status()[0].Busy);
        using var stopWaiting = new CancellationTokenSource();
        var waiting = lanes.RunAsync(SenseKind.Image, Look("waiting"), stopWaiting.Token);
        await Until(clock, () => lanes.Status()[0].Waiting == 1);
        stopWaiting.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        Assert.Equal((false, 0), (lanes.Status()[0].Busy, lanes.Status()[0].Waiting));
    }

    [Fact]
    public async Task One_model_for_both_kinds_runs_one_job_at_a_time()
    {
        var clock = new RuntimeClock();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ran = new List<SenseKind>();
        var lanes = new SenseLanes(Routes(new() { Image = Own(Omni), Audio = new() { Source = SenseSource.OtherSense } }), async (kind, _, _, token) =>
        {
            lock (ran) ran.Add(kind);
            if (kind == SenseKind.Image) await gate.Task.WaitAsync(token);
            return SenseAnswer.Done("described");
        }, clock);
        var image = lanes.RunAsync(SenseKind.Image, Look("glance"), CancellationToken.None);
        await Until(clock, () => lanes.Status()[0].Busy);
        var audio = lanes.RunAsync(SenseKind.Audio, Listen("your voice"), CancellationToken.None);
        await Until(clock, () => lanes.Status()[1].Waiting == 1);
        Assert.Equal([SenseKind.Image], ran);
        gate.SetResult();
        Assert.All(await Task.WhenAll(image, audio), r => Assert.True(r.Succeeded));
        Assert.Equal([SenseKind.Image, SenseKind.Audio], ran);
    }

    [Fact]
    public async Task A_job_must_carry_what_its_kind_takes()
    {
        var lanes = new SenseLanes(Described, (_, _, _, _) => Task.FromResult(SenseAnswer.Done("x")), new RuntimeClock());
        await Assert.ThrowsAsync<ContractException>(() => lanes.RunAsync(SenseKind.Audio, Look("a picture"), CancellationToken.None));
        await Assert.ThrowsAsync<ContractException>(() => lanes.RunAsync(SenseKind.Image, Listen("a recording"), CancellationToken.None));
        await Assert.ThrowsAsync<ContractException>(() =>
            lanes.RunAsync(SenseKind.Image, Look("too long") with { Timeout = TimeSpan.FromMinutes(6) }, CancellationToken.None));
        await Assert.ThrowsAsync<ContractException>(() =>
            lanes.RunAsync(SenseKind.Image, Look("too much") with { MaxOutputTokens = SenseJob.MaximumOutputTokens + 1 }, CancellationToken.None));
        // A helper job (touch zones, the eyes) may write as much as on a Thinking pool member, for as long.
        Assert.True((await lanes.RunAsync(SenseKind.Image, Look("touch zones") with { MaxOutputTokens = 4096, Timeout = TimeSpan.FromMinutes(3) },
            CancellationToken.None)).Succeeded);
    }

    [Fact]
    public async Task The_conversation_comes_first_a_job_waits_while_a_reply_holds_the_hardware_and_a_running_one_is_stopped()
    {
        var clock = new RuntimeClock();
        var holding = 1;
        var started = new List<string>();
        var lanes = new SenseLanes(Described, async (_, _, job, token) =>
        {
            lock (started) started.Add(job.Purpose);
            if (job.Purpose == "long") await Task.Delay(Timeout.Infinite, token);
            return SenseAnswer.Done("described");
        }, clock, held: _ => Volatile.Read(ref holding) == 1);

        // A reply's Thinking request runs before its first audio: the job waits, and a newer picture takes its place.
        var older = lanes.RunAsync(SenseKind.Image, Look("older picture", key: "picture"), CancellationToken.None);
        await Until(clock, () => lanes.Status()[0].Held == 1);
        var newer = lanes.RunAsync(SenseKind.Image, Look("newer picture", key: "picture"), CancellationToken.None);
        Assert.Equal(SenseJobOutcome.Stale, (await Done(clock, older)).Outcome);
        await Until(clock, () => lanes.Status()[0].Held == 1);
        Assert.Empty(started);
        // The reply played its first audio: the newest picture is described.
        Volatile.Write(ref holding, 0);
        Assert.Equal("described", (await Done(clock, newer)).Text);
        Assert.Equal(["newer picture"], started);

        // A reply starts while a job runs: the job is stopped at once.
        var running = lanes.RunAsync(SenseKind.Image, Look("long"), CancellationToken.None);
        await Until(clock, () => started.Count == 2);
        Volatile.Write(ref holding, 1);
        var stopped = await Done(clock, running);
        Assert.Equal(SenseJobOutcome.Preempted, stopped.Outcome);
        Assert.Contains("a reply started", stopped.Problem);

        // A job the hold outlasts is dropped.
        var outlasted = await Done(clock, lanes.RunAsync(SenseKind.Image, Look("summary", timeout: TimeSpan.FromMilliseconds(300)), CancellationToken.None));
        Assert.Equal(SenseJobOutcome.Stale, outlasted.Outcome);
        Assert.Equal("the conversation's reply kept its computer busy", outlasted.Problem);
        Assert.Equal((false, 0, 0), (lanes.Status()[0].Busy, lanes.Status()[0].Waiting, lanes.Status()[0].Held));
    }
}
