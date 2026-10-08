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
}
