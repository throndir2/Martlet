namespace Martlet.Conversation.Tests;

/// <summary>The Thinking requests journal (<see cref="ThinkingRequests"/>): what the job board and background jobs record, with
/// their timings, line places and totals.</summary>
public sealed class ThinkingRequestsTests
{
    private static readonly BackgroundPlace Shared = new("endpoint:local", "this PC") { Machine = LiveResources.ThisPc, Slots = 6 };
    private static readonly LiveResources ThinkingHere = new([new("thinking", LiveResources.ThisPc, [])]);

    private static ThinkingJob Job(ThinkingJobKind kind, TimeSpan? timeout = null, bool stale = false) =>
        new() { Kind = kind, Instructions = "Answer in one word.", Text = "fixture", Timeout = timeout ?? TimeSpan.FromSeconds(10), DropWhenStale = stale };

    private static async Task Until(Func<bool> done)
    {
        var waited = System.Diagnostics.Stopwatch.StartNew();
        while (!done() && waited.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(10);
        Assert.True(done(), "timed out");
    }

    [Fact]
    public async Task ThinkingRequests_a_board_job_that_succeeds_records_its_try_and_timings()
    {
        var clock = new RuntimeClock();
        var places = new BackgroundPlaces(clock);
        places.Requests.Origin = () => "Diva";
        BackgroundPlace member = new("host:a", "a") { Model = "qwen3:8b" };
        var board = new ThinkingJobBoard(places, () => [member], (m, _, _) =>
        {
            clock.Advance(TimeSpan.FromSeconds(3));
            return Task.FromResult(ThinkingAnswer.Done("abc"));
        });
        var posted = clock.GetUtcNow();
        var held = places.TryAcquire([member], "other")!;
        var running = board.RunAsync(Job(ThinkingJobKind.Digest) with { Label = "a summary of the screen" }, CancellationToken.None);
        await Until(() => places.Requests.List(places) is [{ Position: 1 }]);
        var waiting = Assert.Single(places.Requests.List(places));
        Assert.Equal((ThinkingRequestState.Waiting, 1, false), (waiting.State, waiting.Position, waiting.HeldForConversation));
        Assert.Equal(1, places.Requests.ActiveCount);
        clock.Advance(TimeSpan.FromSeconds(2));
        held.Dispose();
        Assert.True((await running).Succeeded);

        var request = Assert.Single(places.Requests.List(places));
        Assert.Equal((ThinkingRequestState.Succeeded, ThinkingRequestSource.Pool, ThinkingJobKind.Digest), (request.State, request.Start.Source, request.Kind));
        Assert.Equal(("a summary of the screen", "Diva", "digest-1"), (request.Start.Task, request.Origin, request.Start.Holder));
        Assert.Equal(posted, request.Posted);
        Assert.Equal(posted.AddSeconds(5), request.Finished);
        var attempt = Assert.Single(request.Attempts);
        Assert.Equal(("host:a", "a", "qwen3:8b", "answered"), (attempt.MemberId, attempt.Member, attempt.Model, attempt.Ending));
        Assert.Equal(0, request.Retries);
        Assert.Equal(3, request.AnswerLength);
        Assert.Equal(0, request.Position);
        Assert.Equal(TimeSpan.FromSeconds(2), request.FirstWait);
        Assert.Equal(TimeSpan.FromSeconds(2), request.Waited);
        Assert.Equal(TimeSpan.FromSeconds(3), request.Ran);
        Assert.Equal(TimeSpan.FromSeconds(5), request.Total);
        Assert.Equal(0, places.Requests.ActiveCount);
    }

    [Fact]
    public async Task ThinkingRequests_a_failed_member_then_another_records_two_tries_and_one_retry()
    {
        var clock = new RuntimeClock();
        var places = new BackgroundPlaces(clock);
        BackgroundPlace busy = new("host:busy", "busy"), ok = new("host:ok", "ok", Rank: 1);
        var board = new ThinkingJobBoard(places, () => [busy, ok], (m, _, _) =>
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            return Task.FromResult(m.Id == "host:busy" ? ThinkingAnswer.Failed("busy came back empty") : ThinkingAnswer.Done("x"));
        });
        var result = await board.RunAsync(Job(ThinkingJobKind.Memory), CancellationToken.None);
        Assert.Equal(("ok", 2), (result.Member, result.Attempts));

        var request = Assert.Single(places.Requests.List());
        Assert.Equal(ThinkingRequestState.Succeeded, request.State);
        Assert.Equal(2, request.Attempts.Count);
        Assert.Equal(1, request.Retries);
        Assert.Equal(("host:busy", "busy came back empty"), (request.Attempts[0].MemberId, request.Attempts[0].Ending));
        Assert.Equal(("host:ok", "answered"), (request.Attempts[1].MemberId, request.Attempts[1].Ending));
        Assert.Equal(TimeSpan.FromSeconds(2), request.Ran);
        Assert.Equal(request.Attempts[1].Started, request.Attempts[0].Ended);
        var totals = places.Requests.Totals[ThinkingJobKind.Memory];
        Assert.Equal((1, 2, 1), (totals.Count, totals.Attempts, totals.Retries));
    }

    [Fact]
    public async Task ThinkingRequests_no_member_and_stale_jobs_end_without_a_try()
    {
        var places = new BackgroundPlaces();
        var empty = new ThinkingJobBoard(places, () => [], (_, _, _) => Task.FromResult(ThinkingAnswer.Done("x")));
        Assert.Equal(ThinkingJobOutcome.NoMember, (await empty.RunAsync(Job(ThinkingJobKind.Naming), CancellationToken.None)).Outcome);
        var none = Assert.Single(places.Requests.List());
        Assert.Equal(ThinkingRequestState.NoMember, none.State);
        Assert.Empty(none.Attempts);
        Assert.NotNull(none.Note);
        Assert.Equal(none.Total, none.FirstWait);

        BackgroundPlace member = new("host:one", "one");
        using var held = places.TryAcquire([member], "other")!;
        var board = new ThinkingJobBoard(places, () => [member], (m, _, _) => Task.FromResult(ThinkingAnswer.Done(m.Name)));
        var stale = await board.RunAsync(Job(ThinkingJobKind.BargeInJudge, TimeSpan.FromMilliseconds(200), stale: true), CancellationToken.None);
        Assert.Equal(ThinkingJobOutcome.Stale, stale.Outcome);
        var dropped = places.Requests.List()[0];
        Assert.Equal((ThinkingJobKind.BargeInJudge, ThinkingRequestState.Stale), (dropped.Kind, dropped.State));
        Assert.Empty(dropped.Attempts);
        Assert.True(dropped.Start.DropWhenStale);
        Assert.Equal(1, places.Requests.Totals[ThinkingJobKind.BargeInJudge].Problems);
        Assert.Equal(1, places.Requests.Totals[ThinkingJobKind.Naming].Problems);
    }

    [Fact]
    public async Task ThinkingRequests_a_caller_that_stops_waiting_cancels_the_request()
    {
        var places = new BackgroundPlaces();
        BackgroundPlace member = new("host:one", "one");
        using var held = places.TryAcquire([member], "other")!;
        var board = new ThinkingJobBoard(places, () => [member], (m, _, _) => Task.FromResult(ThinkingAnswer.Done(m.Name)));
        using var stop = new CancellationTokenSource();
        var running = board.RunAsync(Job(ThinkingJobKind.Memory), stop.Token);
        var second = board.RunAsync(Job(ThinkingJobKind.Naming), CancellationToken.None);
        await Until(() => places.Requests.List(places) is [{ Position: > 0 }, { Position: > 0 }]);
        Assert.Equal([1, 2], places.Requests.List(places).Select(r => r.Position).Order());
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        var canceled = places.Requests.List().Single(r => r.Kind == ThinkingJobKind.Memory);
        Assert.Equal((ThinkingRequestState.Canceled, "its caller stopped waiting"), (canceled.State, canceled.Note));
        Assert.Equal(0, places.Requests.Totals[ThinkingJobKind.Memory].Problems);
        Assert.Equal(1, Assert.Single(places.Requests.List(places), r => r.Active).Position);
        held.Dispose();
        Assert.True((await second).Succeeded);
    }

    [Fact]
    public async Task ThinkingRequests_a_job_held_for_the_conversation_says_so()
    {
        var clock = new RuntimeClock();
        using var floor = new LiveFloor(clock);
        var places = new BackgroundPlaces();
        new LiveFloorRules(floor, ThinkingHere).Attach(places);
        var board = new ThinkingJobBoard(places, () => [Shared], (m, _, _) => Task.FromResult(ThinkingAnswer.Done(m.Name)));
        floor.Heard();
        var memory = board.RunAsync(Job(ThinkingJobKind.Memory), CancellationToken.None);
        await Until(() => places.Requests.List(places) is [{ HeldForConversation: true }]);
        Assert.Equal(ThinkingRequestState.Waiting, Assert.Single(places.Requests.List(places)).State);
        Assert.False(Assert.Single(places.Requests.List()).HeldForConversation);
        floor.NotWords();
        Assert.True((await memory).Succeeded);
        Assert.False(Assert.Single(places.Requests.List(places)).HeldForConversation);
    }

    [Fact]
    public void ThinkingRequests_keeps_the_last_ended_and_clearing_keeps_the_totals()
    {
        var requests = new ThinkingRequests(new RuntimeClock());
        BackgroundPlace member = new("host:a", "a");
        for (var i = 0; i < ThinkingRequests.Kept + 5; i++)
        {
            var request = requests.Post(new(ThinkingJobKind.Digest, ThinkingRequestSource.Pool, $"digest-{i + 1}"));
            request.Begin(member);
            request.Finish(ThinkingRequestState.Succeeded);
        }
        var active = requests.Post(new(ThinkingJobKind.Memory, ThinkingRequestSource.Pool, "memory-1"));
        var list = requests.List();
        Assert.Equal(ThinkingRequests.Kept + 1, list.Count);
        Assert.Equal(active.Id, list[0].Id);
        Assert.Equal($"tr-{ThinkingRequests.Kept + 5}", list[1].Id);
        Assert.Equal("tr-6", list[^1].Id);
        Assert.Equal(ThinkingRequests.Kept + 5, requests.Totals[ThinkingJobKind.Digest].Count);

        var changed = 0;
        requests.Changed += () => changed++;
        requests.ClearFinished();
        Assert.Equal(1, changed);
        Assert.Equal(active.Id, Assert.Single(requests.List()).Id);
        Assert.Equal(ThinkingRequests.Kept + 5, requests.Totals[ThinkingJobKind.Digest].Count);
        Assert.Equal(1, requests.ActiveCount);
    }

    [Fact]
    public void ThinkingRequests_totals_count_average_and_max_by_kind()
    {
        var clock = new RuntimeClock();
        var requests = new ThinkingRequests(clock);
        BackgroundPlace member = new("host:a", "a");
        void Run(ThinkingJobKind kind, int wait, int run, ThinkingRequestState end)
        {
            var request = requests.Post(new(kind, ThinkingRequestSource.Pool, "holder"));
            clock.Advance(TimeSpan.FromSeconds(wait));
            request.Begin(member);
            clock.Advance(TimeSpan.FromSeconds(run));
            request.Finish(end, end == ThinkingRequestState.Failed ? "it broke" : null);
        }
        Run(ThinkingJobKind.Digest, 1, 2, ThinkingRequestState.Succeeded);
        Run(ThinkingJobKind.Digest, 3, 4, ThinkingRequestState.Failed);
        Run(ThinkingJobKind.Memory, 5, 1, ThinkingRequestState.Canceled);

        var digest = requests.Totals[ThinkingJobKind.Digest];
        Assert.Equal((2, 1, 1, 2, 0, 0), (digest.Count, digest.Succeeded, digest.Problems, digest.Attempts, digest.Retries, digest.Preemptions));
        Assert.Equal(TimeSpan.FromSeconds(2), digest.AverageWait);
        Assert.Equal(TimeSpan.FromSeconds(3), digest.MaxWaited);
        Assert.Equal(TimeSpan.FromSeconds(3), digest.AverageRun);
        Assert.Equal(TimeSpan.FromSeconds(4), digest.MaxRan);
        var memory = requests.Totals[ThinkingJobKind.Memory];
        Assert.Equal((1, 0, 0), (memory.Count, memory.Succeeded, memory.Problems));
        Assert.Equal(TimeSpan.FromSeconds(5), memory.MaxWaited);
        Assert.False(requests.Totals.ContainsKey(ThinkingJobKind.Naming));
        Assert.Equal(TimeSpan.Zero, ThinkingRequestTotals.Empty.AverageWait);
        Assert.Equal("it broke", requests.List().Single(r => r.State == ThinkingRequestState.Failed).Note);
    }

    private static readonly BackgroundJobKind Think = new("think", 2, null, null, Doing: "Thinking about")
    {
        PoolKind = ThinkingJobKind.ThinkLonger, Yields = true
    };

    [Fact]
    public async Task ThinkingRequests_background_work_on_a_pool_records_a_conversation_request_and_its_pause()
    {
        var clock = new RuntimeClock();
        using var jobs = new BackgroundJobs(clock);
        var requests = jobs.Places.Requests;
        List<ThinkingRequestState> seen = [];
        requests.Changed += () =>
        {
            var states = requests.List().Select(r => r.State).ToArray();
            lock (seen) seen.AddRange(states);
        };
        BackgroundPlace member = new("host:gpu", "gpu") { Model = "qwen3:32b" };
        var reseated = false;
        var start = jobs.Start(Think, "a private topic", async (job, token) =>
        {
            if (!reseated)
            {
                reseated = true;
                await jobs.ReseatAsync(job, token);
            }
            return BackgroundJobOutcome.Done("thought");
        }, [member], wait: true);
        var job = start.Job!;
        await Until(() => job.Finished);
        Assert.Equal(BackgroundJobState.Succeeded, job.State);

        var request = Assert.Single(requests.List(jobs.Places));
        Assert.Equal((ThinkingRequestSource.Conversation, ThinkingJobKind.ThinkLonger), (request.Start.Source, request.Kind));
        Assert.Equal(("a private topic", job.Id, job.Id), (request.Start.Topic, request.Start.JobId, request.Start.Holder));
        Assert.Null(request.Start.Task);
        Assert.Equal(ThinkingRequestState.Succeeded, request.State);
        Assert.Equal(2, request.Attempts.Count);
        Assert.Equal(1, request.Retries);
        Assert.Equal(1, request.Preemptions);
        Assert.Equal(("host:gpu", "qwen3:32b", "stopped for the conversation"),
            (request.Attempts[0].MemberId, request.Attempts[0].Model, request.Attempts[0].Ending));
        Assert.Equal("answered", request.Attempts[1].Ending);
        Assert.Equal("thought".Length, request.AnswerLength);
        lock (seen) Assert.Contains(ThinkingRequestState.Paused, seen);
        Assert.Equal(1, requests.Totals[ThinkingJobKind.ThinkLonger].Preemptions);
    }

    [Fact]
    public void ThinkingRequests_background_work_without_a_pool_kind_records_nothing()
    {
        using var jobs = new BackgroundJobs();
        var song = new BackgroundJobKind("song", 1, 4, TimeSpan.FromMinutes(1));
        var start = jobs.Start(song, "a song", (_, _) => Task.FromResult(BackgroundJobOutcome.Done("sung")), [new BackgroundPlace("host:a", "a")]);
        Assert.True(start.Started);
        Assert.Empty(jobs.Places.Requests.List());
    }
}
