using Martlet.Desktop;

namespace Martlet.Desktop.Tests;

/// <summary>Setup runs that need the same step (installing or starting Docker Desktop, the host image, the firewall) do it
/// once: the others wait for it instead of repeating it or being refused, and different steps run side by side.</summary>
public sealed class SharedStepsTests
{
    private static string Key() => "test-" + Guid.NewGuid().ToString("N");

    private sealed class Lines : IProgress<string>
    {
        private readonly List<string> lines = [];
        public void Report(string value) { lock (lines) lines.Add(value); }
        public string[] All() { lock (lines) return [.. lines]; }
    }

    [Fact]
    public async Task A_second_run_waits_for_the_step_the_first_one_does_and_gets_its_result()
    {
        var key = Key();
        var release = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = 0;
        var leader = SharedSteps.RunAsync(key, "Start Docker Desktop", "starting Docker Desktop", async () =>
        {
            Interlocked.Increment(ref started);
            return await release.Task;
        }, _ => { }, null, CancellationToken.None);
        Assert.True(SharedSteps.IsRunning(key));
        Assert.Contains(SharedSteps.Now(), step => step.Key == key && step.By == "Start Docker Desktop" && step.Doing == "starting Docker Desktop");

        string? waiting = null;
        var output = new Lines();
        var follower = SharedSteps.RunAsync(key, "Set up this PC as a host", "starting Docker Desktop", () =>
        {
            Interlocked.Increment(ref started);
            return Task.FromResult(-1);
        }, status => waiting = status, output, CancellationToken.None);

        Assert.False(follower.IsCompleted);
        Assert.Equal("Waiting: \"Start Docker Desktop\" is starting Docker Desktop. This continues once that's done...", waiting);
        Assert.Contains(output.All(), line => line.Contains("\"Start Docker Desktop\" is starting Docker Desktop already", StringComparison.Ordinal));

        release.SetResult(42);
        Assert.Equal(42, await leader);
        Assert.Equal(42, await follower);
        Assert.Equal(1, started);
        Assert.False(SharedSteps.IsRunning(key));
    }

    [Fact]
    public async Task A_waiting_run_stops_with_the_same_reason_when_the_step_fails()
    {
        var key = Key();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var leader = SharedSteps.RunAsync<bool>(key, "Install Docker Desktop", "installing Docker Desktop", async () =>
        {
            await release.Task;
            throw new InvalidOperationException("Docker Desktop wasn't installed. The output shows why.");
        }, _ => { }, null, CancellationToken.None);
        var follower = SharedSteps.RunAsync(key, "Set up this PC as a host", "installing Docker Desktop", () => Task.FromResult(true),
            _ => { }, null, CancellationToken.None);

        release.SetResult();
        var first = await Assert.ThrowsAsync<InvalidOperationException>(() => leader);
        var second = await Assert.ThrowsAsync<InvalidOperationException>(() => follower);
        Assert.Equal(first.Message, second.Message);
        Assert.False(SharedSteps.IsRunning(key));
    }

    [Fact]
    public async Task A_waiting_run_does_the_step_itself_when_the_run_doing_it_is_canceled()
    {
        var key = Key();
        using var cancelLeader = new CancellationTokenSource();
        var leaderStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var leader = SharedSteps.RunAsync(key, "Start Docker Desktop", "starting Docker Desktop", async () =>
        {
            leaderStarted.SetResult();
            await Task.Delay(Timeout.Infinite, cancelLeader.Token);
            return "leader";
        }, _ => { }, null, cancelLeader.Token);
        await leaderStarted.Task;
        var output = new Lines();
        var follower = SharedSteps.RunAsync(key, "Add Whisper to this PC", "starting Docker Desktop", () => Task.FromResult("follower"),
            _ => { }, output, CancellationToken.None);

        cancelLeader.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => leader);
        Assert.Equal("follower", await follower);
        Assert.Contains(output.All(), line => line.Contains("was canceled before it finished", StringComparison.Ordinal));
        Assert.False(SharedSteps.IsRunning(key));
    }

    [Fact]
    public async Task A_waiting_run_that_is_canceled_stops_without_disturbing_the_step()
    {
        var key = Key();
        var release = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var leader = SharedSteps.RunAsync(key, "Start Docker Desktop", "starting Docker Desktop", () => release.Task, _ => { }, null,
            CancellationToken.None);
        using var cancelFollower = new CancellationTokenSource();
        var follower = SharedSteps.RunAsync(key, "Pair your main PC", "starting Docker Desktop", () => Task.FromResult("follower"),
            _ => { }, null, cancelFollower.Token);

        cancelFollower.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => follower);
        Assert.True(SharedSteps.IsRunning(key));
        release.SetResult("leader");
        Assert.Equal("leader", await leader);
    }

    [Fact]
    public async Task Different_steps_run_side_by_side()
    {
        var first = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = 0;
        var a = SharedSteps.RunAsync(Key(), "Install Docker Desktop", "installing Docker Desktop", () =>
        {
            Interlocked.Increment(ref started);
            return first.Task;
        }, _ => { }, null, CancellationToken.None);
        var b = SharedSteps.RunAsync(Key(), "Set up host service", "checking Windows Firewall", () =>
        {
            Interlocked.Increment(ref started);
            return second.Task;
        }, _ => { }, null, CancellationToken.None);

        Assert.Equal(2, started);
        second.SetResult(2);
        Assert.Equal(2, await b);
        Assert.False(a.IsCompleted);
        first.SetResult(1);
        Assert.Equal(1, await a);
    }

    [Fact]
    public async Task Waiting_for_a_step_returns_at_once_when_none_runs_and_after_it_when_one_does()
    {
        var key = Key();
        Assert.False(await SharedSteps.WaitAsync(key, _ => { }, null, CancellationToken.None));

        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var install = SharedSteps.RunAsync(key, "Install Docker Desktop", "installing Docker Desktop", () => release.Task, _ => { }, null,
            CancellationToken.None);
        string? status = null;
        var waiting = SharedSteps.WaitAsync(key, text => status = text, null, CancellationToken.None);
        Assert.False(waiting.IsCompleted);
        Assert.Contains("\"Install Docker Desktop\" is installing Docker Desktop", status, StringComparison.Ordinal);
        release.SetResult(true);
        Assert.True(await waiting);
        Assert.True(await install);
    }
}

/// <summary>Settings changes from the main window take turns: one that finds another saving waits for it instead of being
/// refused, and a turn is released exactly once.</summary>
public sealed class ChangeTurnsTests
{
    [Fact]
    public async Task A_change_waits_for_the_one_saving_and_then_goes()
    {
        var changes = new ChangeTurns();
        var first = changes.TryTake();
        Assert.NotNull(first);
        Assert.True(changes.Busy);
        Assert.Null(changes.TryTake());

        var second = changes.TakeAsync(CancellationToken.None);
        Assert.False(second.IsCompleted);
        first!.Dispose();
        using (await second.WaitAsync(TimeSpan.FromSeconds(5)))
            Assert.True(changes.Busy);
        Assert.False(changes.Busy);
    }

    [Fact]
    public async Task Changes_that_wait_go_one_after_another()
    {
        var changes = new ChangeTurns();
        var order = new List<int>();
        var holding = changes.TryTake()!;
        async Task Change(int number)
        {
            using var turn = await changes.TakeAsync(CancellationToken.None);
            order.Add(number);
            await Task.Yield();
            Assert.True(changes.Busy);
        }
        var one = Change(1);
        var two = Change(2);
        holding.Dispose();
        await Task.WhenAll(one, two).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new[] { 1, 2 }, order);
        Assert.False(changes.Busy);
    }

    [Fact]
    public void A_turn_disposed_twice_lets_only_one_more_change_go()
    {
        var changes = new ChangeTurns();
        var turn = changes.TryTake()!;
        turn.Dispose();
        turn.Dispose();
        var next = changes.TryTake();
        Assert.NotNull(next);
        Assert.Null(changes.TryTake());
        next!.Dispose();
    }

    [Fact]
    public async Task A_canceled_wait_takes_no_turn()
    {
        var changes = new ChangeTurns();
        var holding = changes.TryTake()!;
        using var cancel = new CancellationTokenSource();
        var waiting = changes.TakeAsync(cancel.Token);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        holding.Dispose();
        Assert.False(changes.Busy);
    }
}
