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
}
