using Martlet.Conversation;

namespace Martlet.Conversation.Tests;

public sealed class BackgroundJobsFaultTests
{
    private static readonly BackgroundPlace Diva = new("host:diva", "diva");
    private static readonly BackgroundJobKind Kind = new("test", 2, null, TimeSpan.FromMinutes(1));

    // A clock whose timers can break (as a disposed clock would at shutdown).
    private sealed class BrokenTimers : TimeProvider
    {
        internal volatile bool Broken;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            Broken ? throw new ObjectDisposedException("clock") : System.CreateTimer(callback, state, dueTime, period);
    }

    private static async Task WaitAsync(Func<bool> done)
    {
        for (var i = 0; i < 500 && !done(); i++) await Task.Delay(10);
        Assert.True(done());
    }

    [Fact]
    public async Task A_job_whose_seating_throws_ends_failed_is_reported_and_does_not_block_its_kind()
    {
        var clock = new BrokenTimers();
        using var jobs = new BackgroundJobs(clock);
        var faults = new List<(string Id, Exception Error)>();
        jobs.Faulted += (id, error) => { lock (faults) faults.Add((id, error)); };
        var running = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = jobs.Start(Kind, "first", async (_, token) =>
        {
            running.SetResult();
            await release.Task.WaitAsync(token);
            return BackgroundJobOutcome.Done("done");
        }, [Diva]);
        await running.Task;

        // The only place is taken: the next waits in line, and making its wait fails.
        clock.Broken = true;
        var second = jobs.Start(Kind, "second", (_, _) => Task.FromResult(BackgroundJobOutcome.Done("never")), [Diva], wait: true);
        Assert.True(second.Started);
        await WaitAsync(() => second.Job!.Finished);
        clock.Broken = false;

        Assert.Equal(BackgroundJobState.Failed, second.Job!.State);
        Assert.Equal("it couldn't start on this PC", second.Job.Problem);
        var fault = Assert.Single(faults);
        Assert.Equal(second.Job.Id, fault.Id);
        Assert.IsType<ObjectDisposedException>(fault.Error);
        Assert.Equal(first.Job, Assert.Single(jobs.Active));

        release.SetResult();
        await WaitAsync(() => first.Job!.Finished);
        // Nothing is stuck: both jobs of the kind may start again.
        var third = jobs.Start(Kind, "third", (_, _) => Task.FromResult(BackgroundJobOutcome.Done("ok")), [Diva]);
        Assert.True(third.Started);
        await WaitAsync(() => third.Job!.Finished);
        Assert.Equal(BackgroundJobState.Succeeded, third.Job!.State);
    }

    [Fact]
    public async Task A_runner_that_throws_is_reported_with_its_job_id()
    {
        using var jobs = new BackgroundJobs();
        var faults = new List<string>();
        jobs.Faulted += (id, _) => { lock (faults) faults.Add(id); };
        var start = jobs.Start(Kind, "broken", (_, _) => throw new InvalidOperationException("boom"));
        await WaitAsync(() => start.Job!.Finished);
        Assert.Equal(BackgroundJobState.Failed, start.Job!.State);
        Assert.Equal("it failed on this PC", start.Job.Problem);
        lock (faults) Assert.Equal(start.Job.Id, Assert.Single(faults));
    }

    [Fact]
    public async Task Finished_jobs_waiting_to_be_brought_up_are_trimmed_past_the_limit_oldest_first()
    {
        using var jobs = new BackgroundJobs();
        var kind = new BackgroundJobKind("quick", 8, null, null);
        var started = new List<BackgroundJob>();
        for (var i = 0; i < BackgroundJobs.KeptWaiting + 8; i++)
        {
            var start = jobs.Start(kind, $"job {i}", (_, _) => Task.FromResult(BackgroundJobOutcome.Done("ok")));
            Assert.True(start.Started, start.Message);
            started.Add(start.Job!);
            await WaitAsync(() => start.Job!.Finished);
        }

        // None was brought up, so all wait; the list keeps at most KeptWaiting (plus the one finished since the last start).
        Assert.InRange(jobs.Recent.Count, BackgroundJobs.KeptWaiting, BackgroundJobs.KeptWaiting + 1);
        Assert.Equal(BackgroundDeliveryState.Dropped, started[0].Delivery);
        Assert.DoesNotContain(started[0], jobs.Recent);
        Assert.Equal(BackgroundDeliveryState.Pending, started[^1].Delivery);
        Assert.Contains(started[^1], jobs.Undelivered);
    }
}
