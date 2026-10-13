using Martlet.Core.Cluster;
using Martlet.Core.Speakers;

namespace Martlet.Core.Tests;

/// <summary>Companion › People's Voice recognition list (docs/VOICES.md#where-voice-recognition-runs): the order a companion PC
/// tries, this PC's processor as the fallback that keeps recognition working, the router's head start and rests, and the keeper
/// that loads this PC's engine again after failures.</summary>
public sealed class VoiceRecognitionTests
{
    private static PoolList List(params PoolMember[] members) => new() { Area = PoolAreas.VoiceRecognition.Id, Members = members };
    private static bool Paired(string id) => id is "gpu-pc" or "m3-host" or "kept-pc";
    private static string[] Keys(IReadOnlyList<VoiceRecognitionTarget> targets) =>
        [.. targets.Select(t => t.Key + (t.Fallback ? " (fallback)" : ""))];

    [Fact]
    public void The_list_is_tried_in_order_with_this_pc_always_last_as_the_fallback()
    {
        Assert.Equal(["host:gpu-pc", "this-pc", "host:m3-host"], Keys(VoiceRecognitionPool.Targets(List(PoolMember.Computer("gpu-pc"),
            PoolMember.Gpu("gpu-pc", 2), PoolMember.ThisPc(), PoolMember.Computer("nas-host"),
            PoolMember.Computer("kept-pc") with { OnlyFor = ["desk-2"] }, PoolMember.Computer("m3-host")), "desk-1", Paired)));
        Assert.Equal(["host:gpu-pc", "this-pc (fallback)"],
            Keys(VoiceRecognitionPool.Targets(List(PoolMember.Computer("gpu-pc"), PoolMember.ThisPc() with { Off = true }), "desk-1", Paired)));
        Assert.Equal(["this-pc"], Keys(VoiceRecognitionPool.Targets(null, "desk-1", Paired)));
        Assert.Equal(["this-pc (fallback)"], Keys(VoiceRecognitionPool.Targets(List(), "desk-1", Paired)));
        Assert.Equal(["this-pc (fallback)"], Keys(VoiceRecognitionPool.Targets(List(PoolMember.Computer("gpu-pc") with { Off = true }), "desk-1", Paired)));
    }

    [Fact]
    public void The_area_is_shared_and_required_and_a_second_computer_reads_the_same_list()
    {
        var area = PoolAreas.VoiceRecognition;
        Assert.Contains(area, PoolAreas.All);
        Assert.True(area.Shared);
        Assert.True(area.Required);
        Assert.Equal("This PC's processor", area.WhenEmpty);
        Assert.Equal("voice-recognition", area.HostRole);
        Assert.Equal(PoolSettings.FileName, PoolSettings.File(area.Shared));
        Assert.True(PoolRouting.Order(area, List(), "desk-1").Fallback);

        // Two simulated computers: the list one saves is the list the other reads.
        var desk = new PoolSettings().With(List(PoolMember.Computer("gpu-pc"), PoolMember.ThisPc()));
        var other = PoolSettings.Parse(desk.Share());
        Assert.NotNull(other);
        Assert.Equal(["host:gpu-pc", "this-pc"], other.Pool(area.Id).Members.Select(m => m.Key));
        Assert.Equal("Voice recognition", WorkSharingJobs.Title(VoiceRecognitionRouter<int>.Lane));
    }

    // ---------- the keeper ----------

    private sealed class Engine(Func<bool> fails, List<string> log) : IVoiceEngine<int>
    {
        public bool Disposed { get; private set; }
        public int Analyze(float[] samples)
        {
            ObjectDisposedException.ThrowIf(Disposed, this);
            lock (log) log.Add("analyze");
            return fails() ? throw new InvalidOperationException("native failure") : samples.Length;
        }
        public void Load()
        {
            lock (log) log.Add("load");
            if (fails()) throw new InvalidOperationException("native failure");
        }
        public void Dispose() => Disposed = true;
    }

    [Fact]
    public void One_failure_loads_the_engine_again_at_once_and_it_keeps_working()
    {
        var log = new List<string>();
        var made = 0;
        var failures = 1;
        using var keeper = new VoiceEngineKeeper<int>(() =>
        {
            made++;
            return new Engine(() => failures-- > 0, log);
        }, new ManualClock());

        Assert.Equal(16_000, keeper.Analyze(new float[16_000]));
        Assert.Equal(2, made);
        Assert.Equal(VoiceEngineState.Ready, keeper.Status.State);
        Assert.Equal(1, keeper.Status.Reloads);
        Assert.Equal(0, keeper.Status.Failures);
    }

    [Fact]
    public void Repeated_failures_rest_the_engine_and_a_later_check_brings_it_back()
    {
        var clock = new ManualClock();
        var log = new List<string>();
        var broken = true;
        var lines = new List<string>();
        using var keeper = new VoiceEngineKeeper<int>(() => new Engine(() => broken, log), clock);
        keeper.Log += (line, _) => { lock (lines) lines.Add(line); };
        var changes = 0;
        keeper.Changed += () => Interlocked.Increment(ref changes);

        var failed = Assert.Throws<VoiceEngineException>(() => keeper.Analyze(new float[16_000]));
        Assert.Contains("native failure", failed.Message, StringComparison.Ordinal);
        var status = keeper.Status;
        Assert.Equal(VoiceEngineState.Failed, status.State);
        Assert.Equal(1, status.Failures);
        Assert.Equal(clock.GetUtcNow() + TimeSpan.FromSeconds(5), status.RetryAt);
        Assert.True(changes > 0);
        Assert.Contains(lines, l => l.Contains("tries it again in 5 seconds", StringComparison.Ordinal));

        // While it rests, an utterance doesn't call the engine.
        lock (log) log.Clear();
        Assert.Throws<VoiceEngineException>(() => keeper.Analyze(new float[16_000]));
        Assert.Empty(log);

        // The background check after 5 seconds still fails: the next one is 30 seconds later.
        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.True(SpinWait.SpinUntil(() => keeper.Status.Failures == 2, TimeSpan.FromSeconds(5)));
        Assert.Equal(clock.GetUtcNow() + TimeSpan.FromSeconds(30), keeper.Status.RetryAt);

        // It works again: the next check finds it and recognition is back.
        broken = false;
        clock.Advance(TimeSpan.FromSeconds(30));
        Assert.True(SpinWait.SpinUntil(() => keeper.Status.State == VoiceEngineState.Ready, TimeSpan.FromSeconds(5)));
        Assert.Equal(16_000, keeper.Analyze(new float[16_000]));
        Assert.Contains(lines, l => l == "Voice recognition on this PC works again.");
    }

    [Fact]
    public void Too_much_audio_is_not_an_engine_failure_and_free_up_unloads_it()
    {
        using var keeper = new VoiceEngineKeeper<int>(() => new TooLong(), new ManualClock());
        Assert.Throws<ArgumentException>(() => keeper.Analyze(new float[10]));
        Assert.Equal(0, keeper.Status.Failures);
        Assert.True(keeper.Loaded);
        Assert.True(keeper.Unload());
        Assert.False(keeper.Loaded);
        Assert.Equal(VoiceEngineState.NotLoaded, keeper.Status.State);
    }

    private sealed class TooLong : IVoiceEngine<int>
    {
        public int Analyze(float[] samples) => throw new ArgumentException("At most a minute of audio can be analyzed at once.");
        public void Load() { }
        public void Dispose() { }
    }

    [Fact]
    public void Paused_while_recognition_is_off_and_resumed_at_once_when_it_is_on_again()
    {
        var clock = new ManualClock();
        var broken = true;
        using var keeper = new VoiceEngineKeeper<int>(() => new Engine(() => broken, []), clock);
        Assert.Throws<VoiceEngineException>(() => keeper.Analyze(new float[16_000]));
        keeper.Pause();
        broken = false;
        clock.Advance(TimeSpan.FromMinutes(10));
        Thread.Sleep(50);
        Assert.Equal(VoiceEngineState.Failed, keeper.Status.State);
        keeper.Resume();
        Assert.True(SpinWait.SpinUntil(() => keeper.Status.State == VoiceEngineState.Ready, TimeSpan.FromSeconds(5)));
    }

    // ---------- the router ----------

    private sealed class Busy : Exception;

    private static VoiceRecognitionTarget Host(string id) => new("host:" + id, id, PoolMember.Computer(id));
    private static readonly VoiceRecognitionTarget Self = new(PoolMember.ThisPcKey, null, PoolMember.ThisPc());

    private static (VoiceRecognitionRouter<int> Router, VoiceEngineKeeper<int> Keeper, List<string> Calls) Router(
        Func<VoiceRecognitionTarget, CancellationToken, Task<int>> remote, Func<bool>? localFails = null)
    {
        var calls = new List<string>();
        var keeper = new VoiceEngineKeeper<int>(() => new Engine(localFails ?? (() => false), calls));
        var router = new VoiceRecognitionRouter<int>(keeper, (target, _, token) =>
        {
            lock (calls) calls.Add(target.Key);
            return remote(target, token);
        }, new WorkQueue(), error => error is Busy ? WorkRefusal.Busy : WorkRefusal.Unavailable)
        {
            DefaultHeadStart = TimeSpan.FromMilliseconds(250), Longest = TimeSpan.FromSeconds(2)
        };
        return (router, keeper, calls);
    }

    [Fact]
    public async Task A_computer_that_answers_in_time_recognizes_and_this_pc_does_nothing()
    {
        var (router, keeper, calls) = Router((_, _) => Task.FromResult(7));
        using var owned = keeper;
        var run = await router.RunAsync([Host("gpu-pc"), Self], new float[48_000], CancellationToken.None);
        Assert.Equal(7, run.Result);
        Assert.Equal("host:gpu-pc", run.By.Key);
        Assert.False(run.Raced);
        Assert.Equal(["host:gpu-pc"], calls);
        Assert.Equal(VoiceEngineState.NotLoaded, keeper.Status.State);
        Assert.NotNull(Assert.Single(router.Hosts).Milliseconds);
    }

    [Fact]
    public async Task A_busy_computer_passes_it_to_this_pc_at_once_and_does_not_rest()
    {
        var (router, keeper, calls) = Router((_, _) => Task.FromException<int>(new Busy()));
        using var owned = keeper;
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var run = await router.RunAsync([Host("gpu-pc"), Self], new float[48_000], CancellationToken.None);
        Assert.Equal(48_000, run.Result);
        Assert.True(run.By.Here);
        Assert.True(timer.ElapsedMilliseconds < 200, $"took {timer.ElapsedMilliseconds} ms");
        Assert.Equal(["host:gpu-pc", "analyze"], calls);
        Assert.Contains("gpu-pc: busy", run.Passed);
        Assert.False(router.Resting("gpu-pc"));
    }

    [Fact]
    public async Task A_slow_computer_loses_to_this_pc_after_its_head_start_and_rests()
    {
        var (router, keeper, _) = Router(async (_, token) =>
        {
            await Task.Delay(1_500, token);
            return 7;
        });
        using var owned = keeper;
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var run = await router.RunAsync([Host("gpu-pc"), Self], new float[48_000], CancellationToken.None);
        Assert.True(run.By.Here);
        Assert.True(run.Raced);
        Assert.True(timer.ElapsedMilliseconds < 1_000, $"took {timer.ElapsedMilliseconds} ms");
        Assert.True(router.Resting("gpu-pc"));
        Assert.Contains(run.Passed, p => p.StartsWith("gpu-pc didn't answer within", StringComparison.Ordinal));

        // Resting: left out, so the next utterance doesn't wait for it.
        var next = await router.RunAsync([Host("gpu-pc"), Self], new float[48_000], CancellationToken.None);
        Assert.True(next.By.Here);
        Assert.False(next.Raced);
        Assert.Contains(next.Passed, p => p.Contains("rests", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_computer_that_answers_after_the_head_start_but_first_still_wins()
    {
        var gate = new TaskCompletionSource();
        var (router, keeper, _) = Router(async (_, token) =>
        {
            await Task.Delay(400, token);
            return 7;
        }, () =>
        {
            gate.Task.Wait(TimeSpan.FromSeconds(3));
            return false;
        });
        using var owned = keeper;
        var run = await router.RunAsync([Host("gpu-pc"), Self], new float[48_000], CancellationToken.None);
        gate.SetResult();
        Assert.Equal("host:gpu-pc", run.By.Key);
        Assert.True(run.Raced);
        Assert.False(router.Resting("gpu-pc"));
    }

    [Fact]
    public async Task A_failed_computer_rests_and_this_pc_recognizes()
    {
        var (router, keeper, _) = Router((_, _) => Task.FromException<int>(new InvalidDataException("bad voiceprint")));
        using var owned = keeper;
        var run = await router.RunAsync([Host("gpu-pc"), Self], new float[48_000], CancellationToken.None);
        Assert.True(run.By.Here);
        Assert.False(run.Raced);
        Assert.True(router.Resting("gpu-pc"));
        Assert.Equal("bad voiceprint", Assert.Single(router.Hosts).Problem);
    }

    [Fact]
    public async Task When_this_pc_fails_the_computers_after_it_recognize_and_with_none_the_failure_is_reported()
    {
        var (router, keeper, calls) = Router((target, _) => Task.FromResult(target.HostId == "m3-host" ? 9 : throw new InvalidOperationException()),
            () => true);
        using var owned = keeper;
        var run = await router.RunAsync([Self, Host("m3-host")], new float[48_000], CancellationToken.None);
        Assert.Equal(9, run.Result);
        Assert.Equal("host:m3-host", run.By.Key);
        Assert.Contains(run.Passed, p => p.StartsWith("this PC: ", StringComparison.Ordinal));
        Assert.Equal(VoiceEngineState.Failed, keeper.Status.State);

        var alone = await Assert.ThrowsAsync<VoiceEngineException>(() => router.RunAsync([Self], new float[48_000], CancellationToken.None));
        Assert.Contains("native failure", alone.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_head_start_follows_this_pcs_measured_speed()
    {
        var (router, keeper, _) = Router((_, _) => Task.FromResult(1));
        using var owned = keeper;
        Assert.Equal(TimeSpan.FromMilliseconds(250), router.HeadStart(48_000));
        keeper.Analyze(new float[16_000]);
        keeper.Analyze(new float[16_000]);
        Assert.NotNull(keeper.Status.MillisecondsPerSecond);
        Assert.Equal(VoiceRecognitionRouter<int>.MinimumHeadStart, router.HeadStart(48_000));
    }
}
