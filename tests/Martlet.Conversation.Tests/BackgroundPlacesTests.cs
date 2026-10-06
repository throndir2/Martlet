using Martlet.Conversation;
using Martlet.Core.Settings;

namespace Martlet.Conversation.Tests;

public sealed class BackgroundPlacesTests
{
    private static readonly BackgroundPlace Diva = new("host:diva", "diva"), Imouto = new("host:imouto", "imouto", Rank: 1),
        Ripley = new("host:ripley", "ripley");
    private static readonly IReadOnlyList<BackgroundPlace> Pool = [Diva, Imouto, Ripley];
    private static readonly BackgroundJobKind Think = ThinkLonger.Kind(new ThinkLongerSettings(), Pool.Count);

    // A job that runs until it is canceled or released.
    private static Func<BackgroundJob, CancellationToken, Task<BackgroundJobOutcome>> Until(Task release) => async (job, token) =>
    {
        await release.WaitAsync(token);
        return BackgroundJobOutcome.Done("done on " + job.Place?.Name);
    };

    private static async Task WaitAsync(Func<bool> done)
    {
        for (var i = 0; i < 500 && !done(); i++) await Task.Delay(10);
        Assert.True(done());
    }

    [Fact]
    public async Task Each_job_goes_to_a_free_place_least_shared_first_and_a_busy_refusal_names_each_place()
    {
        using var jobs = new BackgroundJobs();
        var release = new TaskCompletionSource();
        Assert.Equal(3, Think.MaxActive);
        var started = Enumerable.Range(0, 3).Select(_ => jobs.Start(Think, "a task", Until(release.Task), Pool)).ToArray();
        Assert.All(started, start => Assert.True(start.Started));
        Assert.Equal(["diva", "ripley", "imouto"], started.Select(start => start.Job!.Place!.Name));
        Assert.Equal(3, jobs.Places.Leases.Count);

        var busy = jobs.Start(Think, "one more", Until(release.Task), Pool);
        Assert.Equal("busy", busy.Refusal);
        Assert.Equal("think-1 on diva, think-2 on ripley and think-3 on imouto are still running, and only 3 thinks run at once (one on each place).",
            busy.Message);
        Assert.Contains("still thinking about something else", ThinkLonger.Refused(busy), StringComparison.Ordinal);

        // Finishing frees its place for the next job.
        jobs.Cancel("think-2", BackgroundJob.CanceledByMartlet);
        await WaitAsync(() => jobs.Places.Load(Ripley.Id) == 0);
        var next = jobs.Start(Think, "next", Until(release.Task), Pool);
        Assert.Equal("ripley", next.Job!.Place!.Name);
        release.SetResult();
        await WaitAsync(() => jobs.Active.Count == 0);
        Assert.Empty(jobs.Places.Leases);
        Assert.Equal("done on ripley", next.Job.Result);
    }

    [Fact]
    public async Task Places_are_shared_across_kinds_and_a_step_may_wait_on_the_least_busy_place()
    {
        using var jobs = new BackgroundJobs();
        var release = new TaskCompletionSource();
        IReadOnlyList<BackgroundPlace> two = [Diva, Ripley];
        // Another kind's step (a song's lyrics) holds diva for a while.
        using (var lyrics = jobs.Places.TryAcquire(two, "song-1"))
        {
            Assert.Equal("diva", lyrics!.Place.Name);
            var first = jobs.Start(ThinkLonger.Kind(new ThinkLongerSettings(), 2), "a task", Until(release.Task), two);
            Assert.Equal("ripley", first.Job!.Place!.Name);
            var none = jobs.Start(ThinkLonger.Kind(new ThinkLongerSettings(), 2), "another", Until(release.Task), two);
            Assert.Equal("busy", none.Refusal);
            Assert.Equal("Every place it can run on is busy: song-1 on diva and think-1 on ripley.", none.Message);
            Assert.Null(jobs.Places.TryAcquire(two, "song-2"));
            // Sharing takes the least busy place instead.
            using var shared = jobs.Places.TryAcquire(two, "song-2", share: true);
            Assert.Equal(2, jobs.Places.Load(shared!.Place.Id));
        }
        Assert.Single(jobs.Places.Leases);
        release.SetResult();
        await WaitAsync(() => jobs.Places.Leases.Count == 0);
        // A job started without a pool runs anywhere and holds no place.
        var anywhere = jobs.Start(ThinkLonger.Kind(new ThinkLongerSettings()), "a task", Until(Task.CompletedTask));
        Assert.Null(anywhere.Job!.Place);
        Assert.Equal("think-2", anywhere.Job.Id);
    }

    [Fact]
    public void The_tool_says_how_many_think_at_once_from_the_settings()
    {
        var settings = new ThinkLongerSettings();
        Assert.Contains("One at a time, 6 an hour.", ThinkLonger.Description(settings), StringComparison.Ordinal);
        Assert.Contains("Up to 3 at once, 6 an hour.", ThinkLonger.Description(settings, 3), StringComparison.Ordinal);
        Assert.Equal(ThinkLonger.MaxPlaces, ThinkLonger.Kind(settings, 50).MaxActive);
        Assert.Equal(1, ThinkLonger.Kind(settings, 0).MaxActive);
    }
}
