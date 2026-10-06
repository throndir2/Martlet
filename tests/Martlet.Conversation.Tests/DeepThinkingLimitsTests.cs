using Martlet.Core.Settings;
using Martlet.Providers;

namespace Martlet.Conversation.Tests;

public sealed class DeepThinkingLimitsTests
{
    private static async Task<BackgroundJobOutcome> Forever(BackgroundJob job, CancellationToken token)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, token);
        return BackgroundJobOutcome.Failed("unreachable");
    }

    private static async Task Until(Func<bool> done)
    {
        var waited = System.Diagnostics.Stopwatch.StartNew();
        while (!done() && waited.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(20);
    }

    [Fact]
    public async Task Deep_thinking_has_no_hourly_limit()
    {
        var kind = ThinkLonger.Kind(new ThinkLongerSettings());
        Assert.Null(kind.TimeLimit);
        Assert.Null(kind.MaxPerHour);
        using var jobs = new BackgroundJobs();
        for (var i = 0; i < 30; i++)
        {
            var start = jobs.Start(kind, $"think {i}", Forever);
            Assert.True(start.Started, start.Refusal);
            jobs.Cancel(start.Job!.Id, BackgroundJob.CanceledByYou);
            await Until(() => start.Job.Finished);
        }
        Assert.Equal(30, jobs.StartedWithinHour(ThinkLonger.KindName));
    }

    [Fact]
    public async Task Deep_thinking_runs_until_done_or_canceled()
    {
        var clock = new RuntimeClock();
        using var jobs = new BackgroundJobs(clock);
        var start = jobs.Start(ThinkLonger.Kind(new ThinkLongerSettings()), "a long think", Forever);
        // A kind with a time limit times out on the same clock; Deep thinking's doesn't.
        var limited = jobs.Start(new BackgroundJobKind("song", 1, null, TimeSpan.FromMinutes(15)), "a song", Forever);
        await Task.Delay(300);
        clock.Advance(TimeSpan.FromHours(3));
        await Until(() => limited.Job!.Finished);
        Assert.Equal(BackgroundJobState.TimedOut, limited.Job!.State);
        Assert.False(start.Job!.Finished);
        jobs.Cancel(start.Job.Id, BackgroundJob.CanceledByYou);
        await Until(() => start.Job.Finished);
        Assert.Equal(BackgroundJobState.Canceled, start.Job.State);
    }

    [Fact]
    public void A_thinks_request_gets_the_providers_whole_ceiling()
    {
        Assert.True(ThinkLonger.RequestTime >= TimeSpan.FromHours(1));
        var limits = ThinkLonger.Limits(new TextGenerationLimits(), ThinkEffort.High, ThinkLonger.RequestTime);
        limits.Validate();
        Assert.Equal(ThinkLonger.RequestTime, limits.MaxRequestTime);
        Assert.Equal(ThinkLonger.RequestTime, ThinkLonger.TurnLimits(ThinkLonger.RequestTime).TurnTimeout);
        Assert.DoesNotContain("hour", ThinkLonger.Description(new ThinkLongerSettings()), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_kind_with_limits_still_enforces_them()
    {
        using var jobs = new BackgroundJobs();
        var kind = new BackgroundJobKind("song", 1, 1, TimeSpan.FromMinutes(15));
        var first = jobs.Start(kind, "one", Forever);
        jobs.Cancel(first.Job!.Id, BackgroundJob.CanceledByYou);
        await Until(() => first.Job.Finished);
        Assert.Equal("hourly_limit", jobs.Start(kind, "two", Forever).Refusal);
        Assert.Throws<Martlet.Core.Contracts.ContractException>(() => new BackgroundJobKind("song", 1, 0, null).Validate());
        Assert.Throws<Martlet.Core.Contracts.ContractException>(() => new BackgroundJobKind("song", 1, null, TimeSpan.FromHours(1)).Validate());
    }
}
