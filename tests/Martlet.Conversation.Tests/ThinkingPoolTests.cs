using Martlet.Core.Settings;

namespace Martlet.Conversation.Tests;

public sealed class ThinkingPoolTests
{
    private static ThinkingJob Job(ThinkingJobKind kind, ThinkingCapability? needs = null, TimeSpan? timeout = null, bool stale = false) =>
        new() { Kind = kind, Instructions = "Answer in one word.", Text = "fixture", Needs = needs, Timeout = timeout ?? TimeSpan.FromSeconds(10), DropWhenStale = stale };

    private static async Task Until(Func<bool> done)
    {
        var waited = System.Diagnostics.Stopwatch.StartNew();
        while (!done() && waited.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(10);
    }

    [Fact]
    public async Task An_empty_pool_answers_no_member_at_once()
    {
        var board = new ThinkingJobBoard(new BackgroundPlaces(), () => [], (_, _, _) => Task.FromResult(ThinkingAnswer.Done("x")));
        Assert.False(board.CanRun(ThinkingJobKind.Digest));
        var result = await board.RunAsync(Job(ThinkingJobKind.Digest), CancellationToken.None);
        Assert.Equal(ThinkingJobOutcome.NoMember, result.Outcome);
        Assert.Equal(0, result.Attempts);
    }

    [Fact]
    public async Task A_job_goes_to_a_member_that_can_do_it()
    {
        BackgroundPlace text = new("host:a", "a"), eyes = new("host:b", "b") { Can = ThinkingCapability.Text | ThinkingCapability.Vision };
        var board = new ThinkingJobBoard(new BackgroundPlaces(), () => [text, eyes], (m, _, _) => Task.FromResult(ThinkingAnswer.Done(m.Name)));
        Assert.True(board.CanRun(ThinkingJobKind.Digest, ThinkingCapability.Vision));
        Assert.False(board.CanRun(ThinkingJobKind.Digest, ThinkingCapability.Audio));
        Assert.Equal("host:b", board.Find(ThinkingJobKind.Digest, ThinkingCapability.Text | ThinkingCapability.Vision)?.Id);
        Assert.Null(board.Find(ThinkingJobKind.Digest, ThinkingCapability.Audio));
        var seen = await board.RunAsync(Job(ThinkingJobKind.Digest, ThinkingCapability.Text | ThinkingCapability.Vision), CancellationToken.None);
        Assert.Equal(("host:b", "b"), (seen.MemberId, seen.Text));
        Assert.Equal(ThinkingJobOutcome.NoMember, (await board.RunAsync(Job(ThinkingJobKind.Digest, ThinkingCapability.Audio), CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task Long_jobs_leave_the_last_free_slot_for_fast_jobs()
    {
        var places = new BackgroundPlaces();
        BackgroundPlace member = new("host:gpu", "gpu") { Slots = 2 };
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var board = new ThinkingJobBoard(places, () => [member], async (m, job, token) =>
        {
            if (job.Kind != ThinkingJobKind.EndOfTurnJudge) await release.Task.WaitAsync(token);
            return ThinkingAnswer.Done(m.Name);
        });
        var first = board.RunAsync(Job(ThinkingJobKind.ThinkLonger), CancellationToken.None);
        await Until(() => places.Leases.Count == 1);
        var second = board.RunAsync(Job(ThinkingJobKind.Research), CancellationToken.None);
        await Until(() => places.WaitingKinds.Count == 1);
        Assert.Equal([ThinkingJobKind.Research], places.WaitingKinds);
        Assert.True((await board.RunAsync(Job(ThinkingJobKind.EndOfTurnJudge), CancellationToken.None)).Succeeded);
        Assert.False(second.IsCompleted);
        var status = board.Status();
        Assert.True(status.KeepsFastSlot);
        Assert.Equal(1, status.Running["think-longer"]);
        release.TrySetResult();
        Assert.All(await Task.WhenAll(first, second), r => Assert.True(r.Succeeded));
    }

    [Fact]
    public async Task With_one_slot_long_jobs_may_take_it_and_waiters_go_by_priority()
    {
        var places = new BackgroundPlaces();
        BackgroundPlace member = new("host:one", "one");
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        List<ThinkingJobKind> ran = [];
        var board = new ThinkingJobBoard(places, () => [member], async (m, job, token) =>
        {
            int count;
            lock (ran) { ran.Add(job.Kind); count = ran.Count; }
            if (count == 1) await release.Task.WaitAsync(token);
            return ThinkingAnswer.Done(m.Name);
        });
        var holding = board.RunAsync(Job(ThinkingJobKind.ThinkLonger), CancellationToken.None);
        await Until(() => places.Leases.Count == 1);
        var research = board.RunAsync(Job(ThinkingJobKind.Research), CancellationToken.None);
        await Until(() => places.WaitingKinds.Count == 1);
        var digest = board.RunAsync(Job(ThinkingJobKind.Digest), CancellationToken.None);
        await Until(() => places.WaitingKinds.Count == 2);
        var judge = board.RunAsync(Job(ThinkingJobKind.BargeInJudge), CancellationToken.None);
        await Until(() => places.WaitingKinds.Count == 3);
        Assert.Equal([ThinkingJobKind.BargeInJudge, ThinkingJobKind.Digest, ThinkingJobKind.Research], places.WaitingKinds);
        release.TrySetResult();
        await Task.WhenAll(holding, research, digest, judge);
        Assert.Equal([ThinkingJobKind.ThinkLonger, ThinkingJobKind.BargeInJudge, ThinkingJobKind.Digest, ThinkingJobKind.Research], ran);
    }

    // One slot ("a sequential local model"): every job but the one that runs waits; a job stops when its token is canceled.
    private static (BackgroundPlaces Places, ThinkingJobBoard Board, List<(ThinkingJobKind Kind, bool Stopped)> Ran, Func<ThinkingJobKind, TaskCompletionSource> Gate)
        Sequential(ThinkingPoolPolicy policy)
    {
        var places = new BackgroundPlaces();
        BackgroundPlace member = new("host:one", "one");
        List<(ThinkingJobKind, bool)> ran = [];
        Dictionary<ThinkingJobKind, TaskCompletionSource> gates = [];
        TaskCompletionSource Gate(ThinkingJobKind kind)
        {
            lock (gates)
            {
                if (!gates.TryGetValue(kind, out var gate)) gates[kind] = gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
                return gate;
            }
        }
        var board = new ThinkingJobBoard(places, () => [member], async (m, job, token) =>
        {
            try { await Gate(job.Kind).Task.WaitAsync(token); }
            catch (OperationCanceledException)
            {
                lock (ran) ran.Add((job.Kind, true));
                throw;
            }
            lock (ran) ran.Add((job.Kind, false));
            return ThinkingAnswer.Done(m.Name);
        }) { Policy = () => policy };
        return (places, board, ran, Gate);
    }

    [Fact]
    public async Task A_higher_priority_job_stops_a_lower_one_which_waits_at_the_front_of_its_priority()
    {
        var (places, board, ran, gate) = Sequential(new(PreemptLowerPriority: true, RaiseAfterStops: 5, Retries: 0));
        gate(ThinkingJobKind.Memory).TrySetResult();
        var memory = board.RunAsync(Job(ThinkingJobKind.Research) with { Priority = ThinkingPriority.Helper }, CancellationToken.None);
        await Until(() => places.Leases.Count == 1);
        // A second helper-priority job waits behind it, then a judge needs the only slot.
        var naming = board.RunAsync(Job(ThinkingJobKind.Naming), CancellationToken.None);
        await Until(() => places.WaitingKinds.Count == 1);
        gate(ThinkingJobKind.BargeInJudge).TrySetResult();
        var judge = await board.RunAsync(Job(ThinkingJobKind.BargeInJudge), CancellationToken.None);
        Assert.True(judge.Succeeded);
        Assert.Equal((ThinkingJobKind.Research, true), ran[0]);
        // The stopped job keeps its priority and goes back to the front: it waits ahead of naming, which came first.
        await Until(() => places.Leases.Count == 1 && places.Leases[0].Kind == ThinkingJobKind.Research);
        Assert.Equal(ThinkingJobKind.Research, places.Leases[0].Kind);
        Assert.Equal([ThinkingJobKind.Naming], places.WaitingKinds);
        gate(ThinkingJobKind.Research).TrySetResult();
        gate(ThinkingJobKind.Naming).TrySetResult();
        var stopped = await memory;
        Assert.True(stopped.Succeeded);
        Assert.Equal((1, 1, (int)ThinkingPriority.Helper), (stopped.PriorityStops, stopped.Preemptions, stopped.Priority));
        Assert.True((await naming).Succeeded);
        Assert.Equal(1, board.Status().StoppedForPriority);
    }

    [Fact]
    public async Task Without_preemption_a_higher_priority_job_waits()
    {
        var (places, board, ran, gate) = Sequential(ThinkingPoolPolicy.Off);
        var research = board.RunAsync(Job(ThinkingJobKind.Research), CancellationToken.None);
        await Until(() => places.Leases.Count == 1);
        var judge = board.RunAsync(Job(ThinkingJobKind.BargeInJudge), CancellationToken.None);
        await Until(() => places.WaitingKinds.Count == 1);
        await Task.Delay(50);
        Assert.False(judge.IsCompleted);
        Assert.Empty(ran);
        gate(ThinkingJobKind.Research).TrySetResult();
        gate(ThinkingJobKind.BargeInJudge).TrySetResult();
        Assert.Equal(0, (await research).PriorityStops);
        Assert.True((await judge).Succeeded);
    }

    [Fact]
    public async Task A_job_stopped_often_enough_rises_one_priority_each_time_until_it_completes()
    {
        var (places, board, _, gate) = Sequential(new(PreemptLowerPriority: true, RaiseAfterStops: 2, Retries: 0));
        var research = board.RunAsync(Job(ThinkingJobKind.Research), CancellationToken.None);
        for (var stop = 1; stop <= 4; stop++)
        {
            await Until(() => places.Leases.Count == 1 && places.Leases[0].Kind == ThinkingJobKind.Research);
            var judge = gate(ThinkingJobKind.BargeInJudge);
            judge.TrySetResult();
            Assert.True((await board.RunAsync(Job(ThinkingJobKind.BargeInJudge), CancellationToken.None)).Succeeded);
        }
        await Until(() => places.Leases.Count == 1 && places.Leases[0].Kind == ThinkingJobKind.Research);
        Assert.Equal((int)ThinkingPriority.Research + 2, places.Leases[0].Priority);
        gate(ThinkingJobKind.Research).TrySetResult();
        var done = await research;
        Assert.True(done.Succeeded);
        Assert.Equal((4, (int)ThinkingPriority.Research + 2), (done.PriorityStops, done.Priority));
        Assert.Equal((4, 2), (board.Status().StoppedForPriority, board.Status().Raised));
    }

    [Fact]
    public async Task A_lower_priority_job_never_stops_a_higher_one()
    {
        var (places, board, ran, gate) = Sequential(new(PreemptLowerPriority: true, RaiseAfterStops: 3, Retries: 0));
        var digest = board.RunAsync(Job(ThinkingJobKind.Digest), CancellationToken.None);
        await Until(() => places.Leases.Count == 1);
        var research = board.RunAsync(Job(ThinkingJobKind.Research), CancellationToken.None);
        await Until(() => places.WaitingKinds.Count == 1);
        await Task.Delay(50);
        Assert.Empty(ran);
        gate(ThinkingJobKind.Digest).TrySetResult();
        gate(ThinkingJobKind.Research).TrySetResult();
        Assert.Equal(0, (await digest).Preemptions);
        Assert.True((await research).Succeeded);
    }

    [Fact]
    public async Task A_failed_job_is_tried_again_up_to_the_retries_at_the_priority_it_had()
    {
        var calls = 0;
        BackgroundPlace member = new("host:one", "one");
        var board = new ThinkingJobBoard(new BackgroundPlaces(), () => [member], (m, _, _) =>
            Interlocked.Increment(ref calls) <= 2 ? throw new InvalidOperationException("boom") : Task.FromResult(ThinkingAnswer.Done(m.Name)))
        {
            Policy = () => new(true, 3, Retries: 2)
        };
        var result = await board.RunAsync(Job(ThinkingJobKind.Memory), CancellationToken.None);
        Assert.True(result.Succeeded);
        Assert.Equal((3, 2, (int)ThinkingPriority.Helper), (result.Attempts, result.Retries, result.Priority));
        Assert.Equal(2, board.Status().Retried);

        calls = -10;
        board.Policy = () => new(true, 3, Retries: 1);
        var failed = await board.RunAsync(Job(ThinkingJobKind.Memory), CancellationToken.None);
        Assert.Equal((ThinkingJobOutcome.Failed, 2, 1), (failed.Outcome, failed.Attempts, failed.Retries));
    }

    [Fact]
    public async Task A_timed_out_job_is_tried_again_and_without_retries_times_out_as_before()
    {
        var calls = 0;
        BackgroundPlace member = new("host:one", "one");
        var board = new ThinkingJobBoard(new BackgroundPlaces(), () => [member], async (m, _, token) =>
        {
            if (Interlocked.Increment(ref calls) == 1) await Task.Delay(Timeout.Infinite, token);
            return ThinkingAnswer.Done(m.Name);
        }) { Policy = () => new(false, 3, Retries: 1) };
        var retried = await board.RunAsync(Job(ThinkingJobKind.Memory, timeout: TimeSpan.FromMilliseconds(100)), CancellationToken.None);
        Assert.Equal((true, 1, 2), (retried.Succeeded, retried.Retries, retried.Attempts));

        calls = 0;
        board.Policy = () => ThinkingPoolPolicy.Off;
        var timedOut = await board.RunAsync(Job(ThinkingJobKind.Memory, timeout: TimeSpan.FromMilliseconds(100)), CancellationToken.None);
        Assert.Equal((ThinkingJobOutcome.TimedOut, 0), (timedOut.Outcome, timedOut.Retries));
    }

    [Fact]
    public void The_policy_comes_from_the_settings()
    {
        Assert.Equal(new ThinkingPoolPolicy(true, 3, 1), ThinkingPoolPolicy.From(new ThinkingPoolSettings()));
        Assert.Equal(new ThinkingPoolPolicy(false, 7, 0),
            ThinkingPoolPolicy.From(new ThinkingPoolSettings { PreemptLowerPriority = false, RaisePriorityAfterStops = 7, RetriesOnFailure = 0 }));
    }

    [Fact]
    public async Task A_failed_member_is_passed_over_for_the_next()
    {
        BackgroundPlace busy = new("host:busy", "busy"), ok = new("host:ok", "ok", Rank: 1);
        var board = new ThinkingJobBoard(new BackgroundPlaces(), () => [busy, ok], (m, _, _) =>
            m.Id == "host:busy" ? throw new InvalidOperationException("job.busy") : Task.FromResult(ThinkingAnswer.Done(m.Name)));
        var result = await board.RunAsync(Job(ThinkingJobKind.Memory), CancellationToken.None);
        Assert.Equal(("ok", 2), (result.Member, result.Attempts));
    }

    [Fact]
    public async Task A_member_that_refused_a_request_as_invalid_rests_for_such_jobs_until_its_rest_ends()
    {
        const ThinkingCapability Sees = ThinkingCapability.Text | ThinkingCapability.Vision;
        var clock = new RuntimeClock();
        BackgroundPlace old = new("host:old", "old-host") { Can = Sees };
        var refuse = true;
        List<ThinkingJobKind> asked = [];
        List<ThinkingPoolRest> rested = [];
        var board = new ThinkingJobBoard(new BackgroundPlaces(), () => [old], (_, job, _) =>
        {
            lock (asked) asked.Add(job.Kind);
            return Task.FromResult(refuse && job.Required.HasFlag(ThinkingCapability.Vision)
                ? ThinkingAnswer.Rejected("old-host refused the request as invalid (request.invalid)") : ThinkingAnswer.Done("ok"));
        }, clock);
        board.Rested += rested.Add;

        var first = await board.RunAsync(Job(ThinkingJobKind.Digest, Sees), CancellationToken.None);
        Assert.Equal((ThinkingJobOutcome.Failed, "old-host refused the request as invalid (request.invalid)"), (first.Outcome, first.Problem));
        var rest = Assert.Single(rested);
        Assert.Equal(("host:old", Sees, clock.GetUtcNow() + ThinkingJobBoard.RefusedRest), (rest.Id, rest.Needs, rest.Until));

        // No more picture jobs go there (callers take their fallback); text jobs still do.
        Assert.False(board.CanRun(ThinkingJobKind.Digest, Sees));
        Assert.False(board.MayStartNow(ThinkingJobKind.Digest, Sees));
        Assert.Null(board.Find(ThinkingJobKind.Digest, Sees));
        var again = await board.RunAsync(Job(ThinkingJobKind.Digest, Sees), CancellationToken.None);
        Assert.Equal((ThinkingJobOutcome.NoMember, 0), (again.Outcome, again.Attempts));
        Assert.Contains("old-host refused such a request as invalid", again.Problem);
        Assert.True(board.CanRun(ThinkingJobKind.Memory));
        Assert.True((await board.RunAsync(Job(ThinkingJobKind.Memory), CancellationToken.None)).Succeeded);
        Assert.Equal([ThinkingJobKind.Digest, ThinkingJobKind.Memory], asked);
        Assert.Equal(rest, Assert.Single(board.Status().Resting));

        // Once the rest ends (the computer may have been updated), picture jobs go there again.
        refuse = false;
        clock.Advance(ThinkingJobBoard.RefusedRest);
        Assert.Empty(board.Status().Resting);
        Assert.True(board.CanRun(ThinkingJobKind.Digest, Sees));
        Assert.True((await board.RunAsync(Job(ThinkingJobKind.Digest, Sees), CancellationToken.None)).Succeeded);
        Assert.Equal(3, asked.Count);
    }

    [Fact]
    public async Task A_text_refusal_rests_the_member_for_every_job_and_the_next_member_takes_them()
    {
        BackgroundPlace old = new("host:old", "old-host") { Can = ThinkingCapability.Text | ThinkingCapability.Vision },
            current = new("host:new", "new-host", Rank: 1) { Can = ThinkingCapability.Text | ThinkingCapability.Vision };
        var refused = 0;
        var board = new ThinkingJobBoard(new BackgroundPlaces(), () => [old, current], (m, _, _) =>
        {
            if (m.Id != "host:old") return Task.FromResult(ThinkingAnswer.Done(m.Name));
            Interlocked.Increment(ref refused);
            return Task.FromResult(ThinkingAnswer.Rejected("old-host refused the request as invalid (request.invalid)"));
        });

        var first = await board.RunAsync(Job(ThinkingJobKind.Memory), CancellationToken.None);
        Assert.Equal(("new-host", 2), (first.Member, first.Attempts));
        // The text job's refusal rests the member for picture jobs too: they need at least text.
        var picture = await board.RunAsync(Job(ThinkingJobKind.Digest, ThinkingCapability.Text | ThinkingCapability.Vision), CancellationToken.None);
        var text = await board.RunAsync(Job(ThinkingJobKind.Naming), CancellationToken.None);
        Assert.Equal(("new-host", 1), (picture.Member, picture.Attempts));
        Assert.Equal(("new-host", 1), (text.Member, text.Attempts));
        Assert.Equal(1, refused);
        Assert.Equal("host:new", board.Find(ThinkingJobKind.Memory)?.Id);
    }

    [Fact]
    public async Task A_stale_job_is_dropped_when_nobody_frees_up_in_time()
    {
        var places = new BackgroundPlaces();
        BackgroundPlace member = new("host:one", "one");
        using var held = places.TryAcquire([member], "other")!;
        var board = new ThinkingJobBoard(places, () => [member], (m, _, _) => Task.FromResult(ThinkingAnswer.Done(m.Name)));
        var result = await board.RunAsync(Job(ThinkingJobKind.BargeInJudge, timeout: TimeSpan.FromMilliseconds(200), stale: true), CancellationToken.None);
        Assert.Equal(ThinkingJobOutcome.Stale, result.Outcome);
    }

    [Fact]
    public async Task Think_longer_jobs_keep_the_fast_slot_free_on_the_job_list()
    {
        using var jobs = new BackgroundJobs();
        BackgroundPlace member = new("host:gpu", "gpu") { Slots = 2 };
        var kind = ThinkLonger.Kind(new ThinkLongerSettings(), 2);
        Assert.Equal(ThinkingJobKind.ThinkLonger, kind.PoolKind);
        static async Task<BackgroundJobOutcome> Forever(BackgroundJob job, CancellationToken token)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return BackgroundJobOutcome.Failed("unreachable");
        }
        var first = jobs.Start(kind, "first", Forever, [member], wait: true);
        var second = jobs.Start(kind, "second", Forever, [member], wait: true);
        Assert.NotNull(first.Job!.Place);
        Assert.Null(second.Job!.Place);
        Assert.NotNull(jobs.Places.TryAcquire([member], "judge", demand: ThinkingDemand.For(ThinkingJobKind.BargeInJudge)));
        jobs.CancelAll();
    }

    [Fact]
    public async Task An_external_member_gets_pictures_and_recordings_only_when_allowed()
    {
        const ThinkingCapability Sees = ThinkingCapability.Text | ThinkingCapability.Vision;
        const ThinkingCapability Hears = ThinkingCapability.Text | ThinkingCapability.Audio;
        BackgroundPlace cloud = new("endpoint:cloud", "cloud") { Can = Sees | Hears, Media = false };
        var asked = 0;
        var board = new ThinkingJobBoard(new BackgroundPlaces(), () => [cloud], (m, _, _) =>
        {
            Interlocked.Increment(ref asked);
            return Task.FromResult(ThinkingAnswer.Done(m.Name));
        });
        Assert.False(board.CanRun(ThinkingJobKind.CheckIn, Sees));
        Assert.False(board.CanRun(ThinkingJobKind.Digest, Hears));
        Assert.Null(board.Find(ThinkingJobKind.Digest, Sees));
        Assert.True(board.CanRun(ThinkingJobKind.CheckIn));
        var picture = await board.RunAsync(Job(ThinkingJobKind.CheckIn, Sees), CancellationToken.None);
        Assert.Equal(ThinkingJobOutcome.NoMember, picture.Outcome);
        Assert.Contains("may not receive pictures and recordings", picture.Problem);
        Assert.Equal(0, asked);
        Assert.Equal("cloud", (await board.RunAsync(Job(ThinkingJobKind.CheckIn), CancellationToken.None)).Member);
        Assert.Contains(board.Status().Guidance, g => g.Contains("No member may receive pictures or recordings") && g.Contains("cloud"));

        // A member on this PC or a paired computer takes the picture job instead; the external one still gets none.
        BackgroundPlace home = new("host:diva", "diva") { Can = Sees | Hears, Rank = 3 };
        var mixed = new ThinkingJobBoard(new BackgroundPlaces(), () => [cloud, home], (m, _, _) => Task.FromResult(ThinkingAnswer.Done(m.Name)));
        Assert.Equal("diva", (await mixed.RunAsync(Job(ThinkingJobKind.CheckIn, Sees), CancellationToken.None)).Member);
        Assert.DoesNotContain(mixed.Status().Guidance, g => g.Contains("may receive pictures", StringComparison.OrdinalIgnoreCase));

        var allowed = new ThinkingJobBoard(new BackgroundPlaces(), () => [cloud with { Media = true }], (m, _, _) => Task.FromResult(ThinkingAnswer.Done(m.Name)));
        Assert.Equal("cloud", (await allowed.RunAsync(Job(ThinkingJobKind.CheckIn, Sees), CancellationToken.None)).Member);
    }

    [Fact]
    public void Pool_places_follow_the_media_box()
    {
        var cloud = new DeepThinkingSettings { Place = DeepThinkingPlace.Endpoint, Origin = "https://api.example.com/v1", ModelId = "gpt-fixture" };
        var local = new DeepThinkingSettings { Place = DeepThinkingPlace.Endpoint, Origin = "http://127.0.0.1:11434/v1", ModelId = "gemma3" };
        var pool = new ThinkingPoolSettings().Add(cloud).Add(local);
        var plan = pool.Plan([]);
        bool Media(ThinkingPoolSettings? choices, DeepThinkingSettings member) =>
            ThinkLonger.Places(plan, choices: choices).Single(p => p.Id == member.Key).Media;
        Assert.False(Media(pool, cloud));
        Assert.False(Media(null, cloud));
        Assert.True(Media(pool, local));
        Assert.True(Media(pool.WithMedia(cloud.Key, true), cloud));
    }

    [Fact]
    public async Task Each_kind_goes_only_to_a_member_ticked_for_it()
    {
        BackgroundPlace big = new("host:big", "big") { QuickJobs = false }, fast = new("endpoint:fast", "fast") { LongJobs = false };
        var board = new ThinkingJobBoard(new BackgroundPlaces(), () => [big, fast], (m, _, _) => Task.FromResult(ThinkingAnswer.Done(m.Name)));
        Assert.Equal("endpoint:fast", board.Find(ThinkingJobKind.BargeInJudge)?.Id);
        Assert.Equal("host:big", board.Find(ThinkingJobKind.Memory)?.Id);
        Assert.Equal("fast", (await board.RunAsync(Job(ThinkingJobKind.EndOfTurnJudge), CancellationToken.None)).Member);
        Assert.Equal("fast", (await board.RunAsync(Job(ThinkingJobKind.Digest), CancellationToken.None)).Member);
        Assert.Equal("big", (await board.RunAsync(Job(ThinkingJobKind.ThinkLonger), CancellationToken.None)).Member);
        Assert.Equal("big", (await board.RunAsync(Job(ThinkingJobKind.Naming), CancellationToken.None)).Member);

        var quickOnly = new ThinkingJobBoard(new BackgroundPlaces(), () => [fast], (m, _, _) => Task.FromResult(ThinkingAnswer.Done(m.Name)));
        Assert.True(quickOnly.CanRun(ThinkingJobKind.BargeInJudge));
        Assert.False(quickOnly.CanRun(ThinkingJobKind.Research));
        Assert.False(quickOnly.MayStartNow(ThinkingJobKind.Research));
        Assert.Equal(ThinkingJobOutcome.NoMember, (await quickOnly.RunAsync(Job(ThinkingJobKind.Research), CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task A_long_job_on_a_member_without_quick_jobs_needs_no_slot_kept_free()
    {
        var places = new BackgroundPlaces();
        BackgroundPlace big = new("host:big", "big") { QuickJobs = false }, mixed = new("host:mixed", "mixed");
        // The only slot that takes quick jobs is busy: a long job may still start on the member that never takes them.
        using var judge = places.TryAcquire([mixed], "judge")!;
        var board = new ThinkingJobBoard(places, () => [big, mixed], (m, _, _) => Task.FromResult(ThinkingAnswer.Done(m.Name)));
        var think = await board.RunAsync(Job(ThinkingJobKind.ThinkLonger), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("big", think.Member);
    }

    [Fact]
    public async Task A_long_job_leaves_the_last_slot_that_takes_quick_jobs_free()
    {
        var places = new BackgroundPlaces();
        // Two slots in all, but only mixed's takes quick jobs: a long job may not take it.
        BackgroundPlace mixed = new("host:mixed", "mixed"), fast = new("endpoint:fast", "fast") { LongJobs = false };
        var board = new ThinkingJobBoard(places, () => [mixed, fast], (m, _, _) => Task.FromResult(ThinkingAnswer.Done(m.Name)));
        using var held = places.TryAcquire([fast], "judge")!;
        var think = board.RunAsync(Job(ThinkingJobKind.ThinkLonger), CancellationToken.None);
        await Until(() => places.WaitingKinds.Count == 1);
        Assert.False(think.IsCompleted);
        held.Dispose();
        Assert.Equal("mixed", (await think.WaitAsync(TimeSpan.FromSeconds(5))).Member);
    }

    [Fact]
    public void Guidance_names_one_slot_and_missing_senses()
    {
        var guidance = ThinkingJobBoard.Guidance([new("host:a", "a", 1, 0, ThinkingCapability.Text, 0)]);
        Assert.StartsWith("1 slot: long thinking can delay screen and sound summaries", guidance[0], StringComparison.Ordinal);
        Assert.Contains(guidance, g => g.StartsWith("No member sees pictures", StringComparison.Ordinal));
        Assert.Equal([ThinkingJobKind.BargeInJudge, ThinkingJobKind.EndOfTurnJudge, ThinkingJobKind.Digest],
            ThinkingJobKinds.All.Where(ThinkingJobKinds.IsFast));
        Assert.True(ThinkingJobKinds.Priority(ThinkingJobKind.TouchZones) < ThinkingJobKinds.Priority(ThinkingJobKind.ThinkLonger));
        Assert.True(ThinkingJobKinds.Priority(ThinkingJobKind.TouchZones) > ThinkingJobKinds.Priority(ThinkingJobKind.Memory));
    }

    [Fact]
    public async Task A_member_whose_computer_is_offline_gets_no_job_and_its_slots_leave_the_pool_until_it_answers()
    {
        var places = new BackgroundPlaces();
        var offline = new HashSet<string>(StringComparer.Ordinal);
        places.Reachable = place => !offline.Contains(place.Id);
        BackgroundPlace diva = new("host:diva", "diva") { Slots = 2, Can = ThinkingCapability.Text | ThinkingCapability.Vision },
            ripley = new("host:ripley", "ripley", Rank: 1);
        var board = new ThinkingJobBoard(places, () => [diva, ripley], (m, _, _) => Task.FromResult(ThinkingAnswer.Done(m.Name)));
        Assert.Equal((3, 3), (board.Status().Slots, board.Status().ConfiguredSlots));

        offline.Add(diva.Id);
        var status = board.Status();
        Assert.Equal((1, 1, 3), (status.Slots, status.Free, status.ConfiguredSlots));
        Assert.Equal([false, true], status.Members.Select(m => m.Online));
        Assert.Equal("1 slot answers now: long thinking can delay screen and sound summaries until more answer.", status.Guidance[0]);
        Assert.Contains(status.Guidance, g => g.StartsWith("diva is offline: 1 of 3 slots answer now", StringComparison.Ordinal));
        Assert.Contains(status.Guidance, g => g.StartsWith("No member that answers now sees pictures", StringComparison.Ordinal));
        // Text goes to ripley; pictures only diva sees, and diva is offline: callers use their fallback.
        Assert.Equal("host:ripley", board.Find(ThinkingJobKind.Memory)?.Id);
        Assert.Equal("ripley", (await board.RunAsync(Job(ThinkingJobKind.Memory), CancellationToken.None)).Member);
        Assert.False(board.CanRun(ThinkingJobKind.Digest, ThinkingCapability.Text | ThinkingCapability.Vision));
        Assert.False(board.MayStartNow(ThinkingJobKind.Digest, ThinkingCapability.Text | ThinkingCapability.Vision));
        var seen = await board.RunAsync(Job(ThinkingJobKind.Digest, ThinkingCapability.Text | ThinkingCapability.Vision), CancellationToken.None);
        Assert.Equal(ThinkingJobOutcome.NoMember, seen.Outcome);
        Assert.Equal("every Thinking pool member that can do text and pictures is offline", seen.Problem);

        offline.Add(ripley.Id);
        Assert.StartsWith("Every Thinking pool computer is offline (diva and ripley)", board.Status().Guidance[0], StringComparison.Ordinal);
        Assert.Equal(0, board.Status().Slots);
        Assert.False(board.CanRun(ThinkingJobKind.Memory));

        offline.Clear();
        Assert.Equal((3, 3), (board.Status().Slots, board.Status().ConfiguredSlots));
        Assert.Equal("diva", (await board.RunAsync(Job(ThinkingJobKind.Digest, ThinkingCapability.Text | ThinkingCapability.Vision), CancellationToken.None)).Member);
    }

    [Fact]
    public async Task A_job_waiting_in_line_goes_to_a_member_that_answers_again()
    {
        var places = new BackgroundPlaces();
        var offline = new HashSet<string>(StringComparer.Ordinal) { "host:diva" };
        places.Reachable = place => !offline.Contains(place.Id);
        BackgroundPlace diva = new("host:diva", "diva"), ripley = new("host:ripley", "ripley");
        var held = places.TryAcquire([ripley], "other-job")!;
        var board = new ThinkingJobBoard(places, () => [diva, ripley], (m, _, _) => Task.FromResult(ThinkingAnswer.Done(m.Name)));
        // ripley is busy and diva is offline: the summary waits in line.
        var waiting = board.RunAsync(Job(ThinkingJobKind.Digest), CancellationToken.None);
        await Until(() => places.WaitingKinds.Count == 1);
        Assert.False(waiting.IsCompleted);
        // diva answers again (HostPresence.Changed in the desktop): the broker looks again and the summary runs there at once.
        offline.Clear();
        places.Reconsider();
        var result = await waiting.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(("diva", ThinkingJobOutcome.Succeeded), (result.Member, result.Outcome));
        held.Dispose();
    }

    [Fact]
    public async Task A_member_that_stops_answering_during_a_job_passes_it_on_and_never_waits_for_offline_members()
    {
        var places = new BackgroundPlaces();
        var offline = new HashSet<string>(StringComparer.Ordinal);
        places.Reachable = place => !offline.Contains(place.Id);
        BackgroundPlace diva = new("host:diva", "diva"), ripley = new("host:ripley", "ripley", Rank: 1), imouto = new("host:imouto", "imouto", Rank: 2);
        var board = new ThinkingJobBoard(places, () => [diva, ripley, imouto], (m, _, _) =>
        {
            // diva and ripley drop off the network while they take the job (the desktop marks them offline at once).
            if (m.Id == imouto.Id) return Task.FromResult(ThinkingAnswer.Done(m.Name));
            offline.Add(m.Id);
            return Task.FromResult(ThinkingAnswer.Failed($"{m.Name} didn't answer"));
        });
        var passed = await board.RunAsync(Job(ThinkingJobKind.Memory), CancellationToken.None);
        Assert.Equal(("imouto", 3), (passed.Member, passed.Attempts));

        // Now imouto fails too, and the members not tried yet are all offline: it ends at once instead of waiting for them.
        offline.Clear();
        offline.Add(ripley.Id);
        offline.Add(imouto.Id);
        var board2 = new ThinkingJobBoard(places, () => [diva, ripley, imouto], (m, _, _) => Task.FromResult(ThinkingAnswer.Failed($"{m.Name} failed")));
        var failed = await board2.RunAsync(Job(ThinkingJobKind.Memory), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal((ThinkingJobOutcome.Failed, "diva failed", 1), (failed.Outcome, failed.Problem, failed.Attempts));
    }

    [Fact]
    public async Task A_limited_member_cools_down_and_its_job_waits_for_it_instead_of_failing()
    {
        var clock = new RuntimeClock();
        var places = new BackgroundPlaces();
        BackgroundPlace nvidia = new("endpoint:https://integrate.api.nvidia.com/v1", "NVIDIA") { Slots = 4 };
        var calls = 0;
        List<ThinkingPoolCooling> said = [];
        var board = new ThinkingJobBoard(places, () => [nvidia], (m, _, _) =>
            Task.FromResult(Interlocked.Increment(ref calls) == 1 ? ThinkingAnswer.Limited("NVIDIA is limiting requests") : ThinkingAnswer.Done(m.Name)),
            clock) { Policy = () => new ThinkingPoolPolicy(PreemptLowerPriority: false, Retries: 0) };
        board.Limited += said.Add;

        var job = board.RunAsync(Job(ThinkingJobKind.Memory), CancellationToken.None);
        await Until(() => said.Count == 1 && places.WaitingKinds.Count == 1);
        Assert.False(job.IsCompleted);
        var cooling = Assert.Single(board.Status().Cooling);
        Assert.Equal((clock.GetUtcNow() + ThinkingPoolLimits.FirstWait, 2, 4, 1), (cooling.Until, cooling.SlotsNow, cooling.Slots, cooling.Times));
        Assert.Equal("NVIDIA is limiting requests; tries again in 5 s, then runs 2 of 4 jobs at once for a while", cooling.Describe(clock.GetUtcNow()));
        Assert.Equal(0, Assert.Single(board.Status().Members).SlotsNow);
        Assert.False(board.MayStartNow(ThinkingJobKind.Memory));

        clock.Advance(ThinkingPoolLimits.FirstWait);
        var result = await job.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal((ThinkingJobOutcome.Succeeded, 2, 0), (result.Outcome, result.Attempts, result.Retries));
        var after = board.Status();
        Assert.Equal(2, Assert.Single(after.Members).SlotsNow);
        Assert.Contains("NVIDIA runs 2 of 4 jobs at once for now, because it limited requests.", after.Guidance);
    }

    [Fact]
    public void A_limited_member_backs_off_and_runs_fewer_jobs_until_successes_raise_them_again()
    {
        var clock = new RuntimeClock();
        var wakes = 0;
        using var limits = new ThinkingPoolLimits(() => Interlocked.Increment(ref wakes), clock);
        BackgroundPlace member = new("endpoint:x", "x") { Slots = 4 };
        TimeSpan Wait(TimeSpan? retryAfter)
        {
            var cooling = limits.Limited(member, retryAfter, "x is limiting requests");
            var wait = cooling.Until!.Value - clock.GetUtcNow();
            clock.Advance(wait);
            return wait;
        }
        Assert.Equal([TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(40)],
            new[] { Wait(null), Wait(null), Wait(null), Wait(TimeSpan.FromSeconds(40)) });
        Assert.Equal(4, wakes);
        Assert.Equal(1, limits.SlotsNow(member));
        // A wait never goes past MaxWait, and a Retry-After past MaxRetryAfter counts as that long.
        for (var i = 0; i < 10; i++) Wait(null);
        Assert.Equal(ThinkingPoolLimits.MaxWait, Wait(null));
        Assert.Equal(ThinkingPoolLimits.MaxRetryAfter, Wait(TimeSpan.FromDays(1)));

        // A success resets the wait; every RaiseAfter successes in a row add one job at once, up to the configured slots.
        Assert.False(limits.Succeeded(member));
        Assert.Equal(ThinkingPoolLimits.FirstWait, Wait(null));
        for (var i = 0; i < ThinkingPoolLimits.RaiseAfter * 3; i++) limits.Succeeded(member);
        Assert.Equal(4, limits.SlotsNow(member));
        Assert.Empty(limits.Now([member]));
        // The configured slots stay the owner's choice: fewer configured slots cap the live limit at once.
        limits.Limited(member with { Slots = 2 }, TimeSpan.FromSeconds(1), "x");
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(1, limits.SlotsNow(member with { Slots = 2 }));
    }

    [Fact]
    public async Task While_a_member_cools_down_its_jobs_go_to_another_member()
    {
        var clock = new RuntimeClock();
        BackgroundPlace nvidia = new("endpoint:https://integrate.api.nvidia.com/v1", "NVIDIA") { Slots = 4 }, diva = new("host:diva", "diva", Rank: 1);
        List<string> asked = [];
        var board = new ThinkingJobBoard(new BackgroundPlaces(), () => [nvidia, diva], (m, _, _) =>
        {
            lock (asked) asked.Add(m.Name);
            return Task.FromResult(m == nvidia ? ThinkingAnswer.Limited("NVIDIA is limiting requests", TimeSpan.FromSeconds(30)) : ThinkingAnswer.Done(m.Name));
        }, clock);
        Assert.Equal("NVIDIA", board.Find(ThinkingJobKind.Memory)?.Name);
        var first = await board.RunAsync(Job(ThinkingJobKind.Memory), CancellationToken.None);
        Assert.Equal((ThinkingJobOutcome.Succeeded, "diva", 2), (first.Outcome, first.Member, first.Attempts));
        Assert.Equal("diva", board.Find(ThinkingJobKind.Memory)?.Name);
        var second = await board.RunAsync(Job(ThinkingJobKind.Memory), CancellationToken.None);
        Assert.Equal(("diva", 1), (second.Member, second.Attempts));
        Assert.Equal(["NVIDIA", "diva", "diva"], asked);
    }

    [Fact]
    public async Task A_stale_job_that_only_cooling_members_could_take_ends_at_once_for_the_callers_fallback()
    {
        var clock = new RuntimeClock();
        BackgroundPlace nvidia = new("endpoint:https://integrate.api.nvidia.com/v1", "NVIDIA") { Slots = 4 };
        var asked = 0;
        var board = new ThinkingJobBoard(new BackgroundPlaces(), () => [nvidia], (_, _, _) =>
        {
            Interlocked.Increment(ref asked);
            return Task.FromResult(ThinkingAnswer.Limited("NVIDIA is limiting requests", TimeSpan.FromSeconds(40)));
        }, clock);
        var first = board.RunAsync(Job(ThinkingJobKind.EndOfTurnJudge, timeout: TimeSpan.FromSeconds(2), stale: true), CancellationToken.None);
        await Until(() => Volatile.Read(ref asked) == 1);
        clock.Advance(TimeSpan.FromSeconds(2));
        var stale = await first.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(ThinkingJobOutcome.Stale, stale.Outcome);
        Assert.Contains("NVIDIA kept limiting requests", stale.Problem);

        var next = await board.RunAsync(Job(ThinkingJobKind.EndOfTurnJudge, timeout: TimeSpan.FromSeconds(2), stale: true), CancellationToken.None);
        Assert.Equal((ThinkingJobOutcome.NoMember, 0, 1), (next.Outcome, next.Attempts, asked));
        Assert.Equal("NVIDIA is limiting requests; tries again in 38 s, then runs 2 of 4 jobs at once for a while", next.Problem);
    }

    [Fact]
    public async Task A_member_that_keeps_limiting_one_jobs_requests_counts_as_failed_for_it()
    {
        var clock = new RuntimeClock();
        var places = new BackgroundPlaces();
        BackgroundPlace nvidia = new("endpoint:https://integrate.api.nvidia.com/v1", "NVIDIA");
        var asked = 0;
        var board = new ThinkingJobBoard(places, () => [nvidia], (_, _, _) =>
        {
            Interlocked.Increment(ref asked);
            return Task.FromResult(ThinkingAnswer.Limited("NVIDIA is limiting requests"));
        }, clock);
        var job = board.RunAsync(Job(ThinkingJobKind.Research), CancellationToken.None);
        for (var i = 1; i < ThinkingJobBoard.MaxLimitedAnswers; i++)
        {
            await Until(() => Volatile.Read(ref asked) == i && places.WaitingKinds.Count == 1);
            clock.Advance(ThinkingPoolLimits.MaxWait);
        }
        var result = await job.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal((ThinkingJobOutcome.Failed, "NVIDIA kept limiting requests", ThinkingJobBoard.MaxLimitedAnswers),
            (result.Outcome, result.Problem, result.Attempts));
    }

    private static readonly BackgroundPlace Small = new("endpoint:small", "small") { Smarts = ThinkingSmarts.Fast },
        Big = new("endpoint:big", "big") { Smarts = ThinkingSmarts.Smart };

    [Fact]
    public async Task Runs_on_any_member_keeps_todays_choice_and_prefer_smart_takes_the_smartest_free_member()
    {
        var board = new ThinkingJobBoard(new BackgroundPlaces(), () => [Small, Big], (m, _, _) => Task.FromResult(ThinkingAnswer.Done(m.Name)));
        var any = await board.RunAsync(Job(ThinkingJobKind.Memory), CancellationToken.None);
        Assert.Equal("small", any.Member);
        Assert.Equal("any", any.RunsOn);
        board.RunsOn = kind => kind == ThinkingJobKind.Memory ? ThinkingRunsOn.PreferSmart : ThinkingRunsOn.Any;
        var smart = await board.RunAsync(Job(ThinkingJobKind.Memory), CancellationToken.None);
        Assert.Equal("big", smart.Member);
        Assert.Equal("prefer-smart", smart.RunsOn);
        Assert.StartsWith("Prefer smart: big (Smart)", smart.Placed);
        Assert.Equal("big", board.Find(ThinkingJobKind.Memory)?.Name);
        Assert.Equal("small", board.Find(ThinkingJobKind.Naming)?.Name);
        var status = board.Status();
        Assert.Equal("prefer-smart", status.RunsOn["memory"]);
        Assert.Equal("any", status.RunsOn["check-in"]);
        Assert.Equal(ThinkingSmarts.Smart, status.Members.Single(m => m.Name == "big").Smarts);
        Assert.Equal(["big", "small"], status.Placements.Select(p => p.Member));
    }

    [Fact]
    public async Task Prefer_smart_waits_a_short_time_for_a_busy_smart_member_then_takes_a_less_smart_one()
    {
        var places = new BackgroundPlaces();
        var board = new ThinkingJobBoard(places, () => [Small, Big], (m, _, _) => Task.FromResult(ThinkingAnswer.Done(m.Name)))
        {
            RunsOn = _ => ThinkingRunsOn.PreferSmart
        };
        Assert.Equal(TimeSpan.FromMilliseconds(100), ThinkingRunsOnRules.PreferWait(TimeSpan.FromMilliseconds(400)));
        Assert.Equal(ThinkingRunsOnRules.MaxPreferWait, ThinkingRunsOnRules.PreferWait(TimeSpan.FromMinutes(5)));

        // The smart member comes free within the wait: the job waits for it.
        var held = places.TryAcquire([Big], "busy")!;
        var waits = board.RunAsync(Job(ThinkingJobKind.Memory, timeout: TimeSpan.FromSeconds(8)), CancellationToken.None);
        await Task.Delay(50);
        Assert.False(waits.IsCompleted);
        held.Dispose();
        Assert.Equal("big", (await waits).Member);

        // It doesn't come free in time: a less smart member takes the job, and the result says why.
        held = places.TryAcquire([Big], "busy")!;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        // A quick job: a long one would leave the only free slot to quick jobs.
        var result = await board.RunAsync(Job(ThinkingJobKind.Digest, timeout: TimeSpan.FromMilliseconds(400)), CancellationToken.None);
        held.Dispose();
        Assert.Equal("small", result.Member);
        Assert.True(watch.ElapsedMilliseconds >= 90, $"{watch.ElapsedMilliseconds} ms");
        Assert.Equal("Prefer smart: no Smart member came free within 100 ms, so a Fast one", result.Placed);
    }

    [Fact]
    public async Task Smart_only_and_these_members_keep_the_other_members_out()
    {
        var board = new ThinkingJobBoard(new BackgroundPlaces(), () => [Small, Big], (m, _, _) => Task.FromResult(ThinkingAnswer.Done(m.Name)))
        {
            RunsOn = kind => kind switch
            {
                ThinkingJobKind.CheckIn => ThinkingRunsOn.SmartOnly,
                ThinkingJobKind.Naming => ThinkingRunsOn.Only([Small.Id]),
                ThinkingJobKind.Memory => ThinkingRunsOn.Only([]),
                _ => ThinkingRunsOn.Any
            }
        };
        Assert.Equal("big", (await board.RunAsync(Job(ThinkingJobKind.CheckIn), CancellationToken.None)).Member);
        Assert.Equal("small", (await board.RunAsync(Job(ThinkingJobKind.Naming), CancellationToken.None)).Member);
        var nobody = await board.RunAsync(Job(ThinkingJobKind.Memory), CancellationToken.None);
        Assert.Equal(ThinkingJobOutcome.NoMember, nobody.Outcome);
        Assert.Equal("Runs on is These members, and no member is chosen", nobody.Problem);
        Assert.False(board.CanRun(ThinkingJobKind.Memory));
        // A job's own Runs on (a check-in's card) comes before its kind's.
        Assert.Equal("small", (await board.RunAsync(Job(ThinkingJobKind.CheckIn) with { RunsOn = ThinkingRunsOn.Only([Small.Id]) }, CancellationToken.None)).Member);
        Assert.True(board.CanRun(ThinkingJobKind.CheckIn, where: ThinkingRunsOn.Only([Small.Id])));

        var smallOnly = new ThinkingJobBoard(new BackgroundPlaces(), () => [Small], (m, _, _) => Task.FromResult(ThinkingAnswer.Done(m.Name)))
        {
            RunsOn = _ => ThinkingRunsOn.SmartOnly
        };
        Assert.False(smallOnly.CanRun(ThinkingJobKind.Digest));
        Assert.False(smallOnly.MayStartNow(ThinkingJobKind.Digest));
        Assert.Null(smallOnly.Find(ThinkingJobKind.Digest));
        var none = await smallOnly.RunAsync(Job(ThinkingJobKind.Digest), CancellationToken.None);
        Assert.Equal(ThinkingJobOutcome.NoMember, none.Outcome);
        Assert.Equal("Runs on is Smart only, and no Smart member can do text", none.Problem);
    }

    [Fact]
    public void Long_jobs_runs_on_filters_members_and_keeps_the_conversation_model()
    {
        BackgroundPlace conversation = new("thinking", "this PC");
        Assert.Equal([Big], ThinkLonger.RunsOn([Small, Big], ThinkingRunsOn.SmartOnly));
        Assert.Empty(ThinkLonger.RunsOn([Small], ThinkingRunsOn.SmartOnly));
        Assert.Equal([Small, Big], ThinkLonger.RunsOn([Small, Big], ThinkingRunsOn.PreferSmart));
        Assert.Equal([conversation], ThinkLonger.RunsOn([conversation], ThinkingRunsOn.SmartOnly));

        // Prefer smart: the broker takes the smartest free place first.
        var places = new BackgroundPlaces();
        var demand = ThinkingDemand.For(ThinkingJobKind.ThinkLonger, [Small, Big]) with { SmartFirst = true, KeepLastFree = false };
        using var lease = places.TryAcquire([Small, Big], "think", demand: demand);
        Assert.Equal("big", lease?.Place.Name);
    }

    [Fact]
    public void Members_get_smarts_from_the_owner_or_their_model_name()
    {
        var gemma = new DeepThinkingSettings { Place = DeepThinkingPlace.Endpoint, Origin = GenerationSupport.LocalOllamaChatBaseUrl, ModelId = "gemma4:e2b" };
        var big = new DeepThinkingSettings { Place = DeepThinkingPlace.Endpoint, Origin = GenerationSupport.LocalOllamaChatBaseUrl, ModelId = "gemma4:27b" };
        var pool = new ThinkingPoolSettings().Add(gemma).Add(big).WithSmarts(big.Key, ThinkingSmarts.Standard);
        var places = ThinkLonger.Places(pool.Plan([]), choices: pool);
        Assert.Equal(ThinkingSmarts.Fast, places.Single(p => p.Id == gemma.Key).Smarts);
        Assert.Equal(ThinkingSmarts.Standard, places.Single(p => p.Id == big.Key).Smarts);
    }

    [Fact]
    public void The_core_kind_names_match_the_job_kinds() =>
        Assert.Equal(ThinkingJobKinds.All.Select(ThinkingJobKinds.Name).Order(), ThinkingPoolSettings.JobKinds.Order());

    [Fact]
    public async Task Prefer_smart_passes_over_a_smart_member_whose_provider_asks_martlet_to_wait()
    {
        var board = new ThinkingJobBoard(new BackgroundPlaces(), () => [Small, Big], (m, _, _) => Task.FromResult(m.Id == Big.Id
            ? ThinkingAnswer.Limited("big is limiting requests", TimeSpan.FromMinutes(1)) : ThinkingAnswer.Done(m.Name)))
        {
            RunsOn = _ => ThinkingRunsOn.PreferSmart
        };
        var first = await board.RunAsync(Job(ThinkingJobKind.Digest), CancellationToken.None);
        Assert.Equal("small", first.Member);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var next = await board.RunAsync(Job(ThinkingJobKind.Digest), CancellationToken.None);
        Assert.Equal("small", next.Member);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1), $"{watch.ElapsedMilliseconds} ms");
    }
}