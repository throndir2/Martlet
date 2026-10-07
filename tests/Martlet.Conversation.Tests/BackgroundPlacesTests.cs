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
        Assert.Equal(6, Think.MaxActive);
        var started = Enumerable.Range(0, 2).Select(_ => jobs.Start(Think, "a task", Until(release.Task), Pool)).ToArray();
        Assert.All(started, start => Assert.True(start.Started));
        Assert.Equal(["diva", "ripley"], started.Select(start => start.Job!.Place!.Name));
        Assert.Equal(2, jobs.Places.Leases.Count);

        // Without waiting in line, a full pool is refused, naming what holds each place: the last free slot (imouto) stays free
        // for the Thinking pool's fast jobs (judges and summaries).
        var busy = jobs.Start(Think, "one more", Until(release.Task), Pool);
        Assert.Equal("busy", busy.Refusal);
        Assert.Equal("Every place it can run on is busy: think-1 on diva and think-2 on ripley. The last free slot stays free for quick jobs.", busy.Message);
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
        // Without the Thinking pool's fast-slot rule, so both places can take a think.
        var think = ThinkLonger.Kind(new ThinkLongerSettings(), 2) with { PoolKind = null };
        using (var lyrics = jobs.Places.TryAcquire(two, "song-1"))
        {
            Assert.Equal("diva", lyrics!.Place.Name);
            var first = jobs.Start(think, "a task", Until(release.Task), two);
            Assert.Equal("ripley", first.Job!.Place!.Name);
            var none = jobs.Start(think, "another", Until(release.Task), two);
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
        // A long job never takes the pool's last free slot while it has two or more: N slots run N-1 thinks at once, one slot one.
        Assert.EndsWith("One at a time; more wait in line.", ThinkLonger.Description(settings), StringComparison.Ordinal);
        Assert.EndsWith("One at a time; more wait in line.", ThinkLonger.Description(settings, 2), StringComparison.Ordinal);
        Assert.EndsWith("Up to 2 at once; more wait in line.", ThinkLonger.Description(settings, 3), StringComparison.Ordinal);
        Assert.EndsWith("Up to 8 at once; more wait in line.", ThinkLonger.Description(settings, 50), StringComparison.Ordinal);
        // Twice the slots may run or wait in line, at most 8 in all.
        Assert.Equal(ThinkLonger.MaxPlaces, ThinkLonger.Kind(settings, 50).MaxActive);
        Assert.Equal(6, ThinkLonger.Kind(settings, 3).MaxActive);
        Assert.Equal(2, ThinkLonger.Kind(settings, 0).MaxActive);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 1)]
    [InlineData(3, 2)]
    [InlineData(4, 3)]
    public void The_tool_counts_only_the_thinks_the_broker_starts_at_once(int slots, int atOnce)
    {
        using var jobs = new BackgroundJobs();
        var release = new TaskCompletionSource();
        IReadOnlyList<BackgroundPlace> places = [Diva with { Slots = slots }];
        var kind = ThinkLonger.Kind(new ThinkLongerSettings(), ThinkLonger.Slots(places));
        var started = Enumerable.Range(0, slots + 1).Count(_ => jobs.Start(kind, "a task", Until(release.Task), places).Started);
        Assert.Equal(atOnce, started);
        Assert.Equal(atOnce, ThinkLonger.AtOnce(places));
        Assert.EndsWith(atOnce == 1 ? "One at a time; more wait in line." : $"Up to {atOnce} at once; more wait in line.",
            ThinkLonger.Description(new ThinkLongerSettings(), ThinkLonger.Slots(places)), StringComparison.Ordinal);
        release.SetResult();
    }

    [Fact]
    public void With_an_empty_pool_the_conversation_models_four_slots_run_three_thinks_at_once()
    {
        var cloud = new SetupRoute
        {
            RouteType = SetupRouteType.ChatCompletions, Role = SetupRole.Llm, ProviderAlias = ChatCompletionsSetup.Alias,
            Origin = ChatCompletionsEndpointCatalog.OpenRouterBaseUrl, ModelId = "x-ai/grok-4.3", ConfigurationRevision = Guid.NewGuid(), Enabled = true
        };
        var places = ThinkLonger.Places(new ThinkingPoolSettings().Plan([cloud]));
        Assert.Equal(["thinking"], places.Select(place => place.Id));
        Assert.Equal(DeepThinkingSettings.CloudThinksAtOnce, ThinkLonger.Slots(places));
        Assert.Equal(3, ThinkLonger.AtOnce(places));
        Assert.EndsWith("Up to 3 at once; more wait in line.", ThinkLonger.Description(new ThinkLongerSettings(), ThinkLonger.Slots(places)),
            StringComparison.Ordinal);
    }
}
