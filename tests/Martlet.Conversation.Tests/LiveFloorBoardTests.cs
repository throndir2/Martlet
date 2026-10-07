namespace Martlet.Conversation.Tests;

/// <summary>The live floor's rules on the Thinking pool's job board and on background jobs (docs/CONVERSATION.md, Live floor).</summary>
public sealed class LiveFloorBoardTests
{
    // A member on this PC beside the conversation's Thinking model, and one on another computer that shares nothing.
    private static readonly BackgroundPlace Shared = new("endpoint:local", "this PC") { Machine = LiveResources.ThisPc, Slots = 6 };
    private static readonly BackgroundPlace Beside = new("host:other", "other") { Machine = "lan:192.168.1.50", Slots = 2 };
    private static readonly LiveResources ThinkingHere = new([new("thinking", LiveResources.ThisPc, [])]);

    private static ThinkingJob Job(ThinkingJobKind kind, TimeSpan? timeout = null, bool stale = false) =>
        new() { Kind = kind, Instructions = "Answer in one word.", Text = "fixture", Timeout = timeout ?? TimeSpan.FromSeconds(30), DropWhenStale = stale };

    private static async Task Until(Func<bool> done)
    {
        var waited = System.Diagnostics.Stopwatch.StartNew();
        while (!done() && waited.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(10);
        Assert.True(done(), "timed out");
    }

    /// <summary>A member that answers at once; the first job of a kind in <see cref="Hold"/> works until the floor stops it, and
    /// the first of a kind in <see cref="Gated"/> until <see cref="Gate"/> opens.</summary>
    private sealed class Members
    {
        private readonly object gate = new();
        public HashSet<ThinkingJobKind> Hold { get; } = [];
        public HashSet<ThinkingJobKind> Gated { get; } = [];
        public TaskCompletionSource Gate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Running(ThinkingJobKind kind) { lock (gate) return running.GetValueOrDefault(kind); }
        private readonly Dictionary<ThinkingJobKind, int> running = [], calls = [];

        public async Task<ThinkingAnswer> Run(BackgroundPlace member, ThinkingJob job, CancellationToken token)
        {
            bool first;
            lock (gate)
            {
                first = (calls[job.Kind] = calls.GetValueOrDefault(job.Kind) + 1) == 1;
                running[job.Kind] = running.GetValueOrDefault(job.Kind) + 1;
            }
            try
            {
                if (first && Hold.Contains(job.Kind)) await Task.Delay(Timeout.Infinite, token);
                if (first && Gated.Contains(job.Kind)) await Gate.Task.WaitAsync(token);
                return ThinkingAnswer.Done($"{ThinkingJobKinds.Name(job.Kind)} on {member.Name}");
            }
            finally { lock (gate) running[job.Kind]--; }
        }
    }

    private static (ThinkingJobBoard Board, LiveFloor Floor, LiveFloorRules Rules, RuntimeClock Clock) Board(Members members,
        params BackgroundPlace[] pool)
    {
        var clock = new RuntimeClock();
        var floor = new LiveFloor(clock);
        var places = new BackgroundPlaces();
        var rules = new LiveFloorRules(floor, ThinkingHere).Attach(places);
        return (new ThinkingJobBoard(places, () => pool, members.Run), floor, rules, clock);
    }

    [Fact]
    public async Task Listening_holds_new_work_on_a_member_that_shares_the_conversation_and_stops_nothing()
    {
        var members = new Members();
        members.Hold.Add(ThinkingJobKind.Research);
        var (board, floor, rules, _) = Board(members, Shared);
        using var end = new CancellationTokenSource();
        var research = board.RunAsync(Job(ThinkingJobKind.Research), end.Token);
        await Until(() => members.Running(ThinkingJobKind.Research) == 1);
        floor.Heard();
        Assert.Equal(LiveFloorLevel.Listening, floor.Level);
        var memory = board.RunAsync(Job(ThinkingJobKind.Memory), CancellationToken.None);
        await Until(() => board.Places.HeldKinds.Count == 1);
        Assert.Equal([ThinkingJobKind.Memory], board.Places.HeldKinds);
        Assert.False(memory.IsCompleted);
        // Running work is never stopped for Listening.
        Assert.False(research.IsCompleted);
        Assert.All(board.Places.Leases, lease => Assert.False(lease.StopRequested));
        var status = board.Status();
        Assert.Equal("Listening", status.Floor);
        Assert.Equal(1, status.Held["memory"]);
        Assert.Equal(["endpoint:local"], status.SharesLive);
        // The judges still start: the live turn needs them.
        Assert.True((await board.RunAsync(Job(ThinkingJobKind.EndOfTurnJudge), CancellationToken.None)).Succeeded);
        floor.NotWords();
        var remembered = await memory;
        Assert.True(remembered.Succeeded);
        Assert.Equal(1, rules.Total.Held["memory"]);
        Assert.Equal(0, rules.Total.StoppedTotal);
        end.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => research);
    }

    [Fact]
    public async Task Live_drops_a_summary_requeues_remembering_and_lets_touch_zones_and_judges_be()
    {
        var members = new Members();
        foreach (var kind in new[] { ThinkingJobKind.Digest, ThinkingJobKind.Memory, ThinkingJobKind.Naming }) members.Hold.Add(kind);
        members.Gated.Add(ThinkingJobKind.TouchZones);
        var (board, floor, rules, _) = Board(members, Shared);
        var digest = board.RunAsync(Job(ThinkingJobKind.Digest, stale: true), CancellationToken.None);
        var memory = board.RunAsync(Job(ThinkingJobKind.Memory), CancellationToken.None);
        var naming = board.RunAsync(Job(ThinkingJobKind.Naming), CancellationToken.None);
        var zones = board.RunAsync(Job(ThinkingJobKind.TouchZones), CancellationToken.None);
        await Until(() => board.Places.Leases.Count == 4);
        floor.Words();
        Assert.Equal(LiveFloorLevel.Live, floor.Level);
        Assert.Equal(3, rules.Period.StoppedTotal);
        Assert.Equal(["digest", "memory", "naming"], rules.Period.Stopped.Keys.Order());
        // The summary is dropped: it is only worth its moment.
        var dropped = await digest;
        Assert.Equal(ThinkingJobOutcome.Preempted, dropped.Outcome);
        Assert.Equal(1, dropped.Preemptions);
        // Remembering and naming stop and wait in line again; finding touch zones goes on.
        await Until(() => board.Places.HeldKinds.Count == 2);
        Assert.Equal(1, members.Running(ThinkingJobKind.TouchZones));
        Assert.False(zones.IsCompleted);
        // New touch zones work doesn't start on the shared member while Live; the judges do.
        var later = board.RunAsync(Job(ThinkingJobKind.TouchZones), CancellationToken.None);
        Assert.True((await board.RunAsync(Job(ThinkingJobKind.BargeInJudge), CancellationToken.None)).Succeeded);
        await Until(() => board.Places.HeldKinds.Contains(ThinkingJobKind.TouchZones));
        Assert.Equal("Live", board.Status().Floor);
        floor.Clear();
        var again = await memory;
        Assert.True(again.Succeeded);
        Assert.Equal(1, again.Preemptions);
        Assert.True((await naming).Succeeded);
        Assert.True((await later).Succeeded);
        members.Gate.TrySetResult();
        Assert.True((await zones).Succeeded);
        Assert.Equal(1, rules.Total.Stopped["memory"]);
        Assert.Equal(1, rules.Total.Stopped["digest"]);
        Assert.False(rules.Total.Stopped.ContainsKey("touch-zones"));
    }

    [Fact]
    public async Task A_member_that_shares_nothing_is_never_held_and_goes_first_while_you_talk()
    {
        var members = new Members();
        var (board, floor, _, _) = Board(members, Shared, Beside);
        floor.Words();
        // Shared has the lower standing, but while you talk Beside goes first and Shared takes nothing new.
        Assert.Equal("host:other", board.Find(ThinkingJobKind.Digest)?.Id);
        Assert.True(board.MayStartNow(ThinkingJobKind.Digest));
        Assert.True(board.CanRunBeside(ThinkingJobKind.Digest));
        var result = await board.RunAsync(Job(ThinkingJobKind.Memory), CancellationToken.None);
        Assert.Equal("other", result.Member);
        var judge = await board.RunAsync(Job(ThinkingJobKind.BargeInJudge), CancellationToken.None);
        Assert.Equal("other", judge.Member);
        floor.Clear();
        Assert.Equal("endpoint:local", board.Find(ThinkingJobKind.Digest)?.Id);
    }

    [Fact]
    public void A_pool_of_shared_members_may_not_start_a_summary_while_you_talk()
    {
        var members = new Members();
        var (board, floor, _, _) = Board(members, Shared);
        Assert.True(board.MayStartNow(ThinkingJobKind.Digest));
        Assert.False(board.CanRunBeside(ThinkingJobKind.Digest));
        floor.Heard();
        Assert.False(board.MayStartNow(ThinkingJobKind.Digest));
        Assert.True(board.MayStartNow(ThinkingJobKind.EndOfTurnJudge));
    }

    [Fact]
    public async Task A_member_whose_computer_holds_its_graphics_card_for_a_live_turn_is_asked_again_not_failed()
    {
        var calls = 0;
        var places = new BackgroundPlaces();
        var board = new ThinkingJobBoard(places, () => [Beside], (member, _, _) =>
            Task.FromResult(Interlocked.Increment(ref calls) == 1 ? ThinkingAnswer.Held("other keeps its graphics card for a live conversation")
                : ThinkingAnswer.Done("answer")));
        var result = await board.RunAsync(Job(ThinkingJobKind.Memory), CancellationToken.None);
        Assert.True(result.Succeeded);
        Assert.Equal((2, 1), (result.Attempts, result.Preemptions));
    }

    [Fact]
    public async Task A_summary_held_past_its_time_says_the_conversation_needed_its_members()
    {
        var members = new Members();
        var (board, floor, _, _) = Board(members, Shared);
        floor.Words();
        var result = await board.RunAsync(Job(ThinkingJobKind.Digest, TimeSpan.FromMilliseconds(200), stale: true), CancellationToken.None);
        Assert.Equal(ThinkingJobOutcome.Stale, result.Outcome);
        Assert.StartsWith("the conversation needed its members", result.Problem);
    }

    // ---------- background jobs (think_longer, research) ----------

    private static readonly BackgroundJobKind Think = new("think", 2, null, null, Doing: "Thinking about")
    {
        PoolKind = ThinkingJobKind.ThinkLonger, Yields = true
    };

    [Fact]
    public async Task A_think_stopped_for_the_conversation_waits_for_it_and_goes_on_from_what_it_wrote()
    {
        var clock = new RuntimeClock();
        using var floor = new LiveFloor(clock);
        using var jobs = new BackgroundJobs(clock);
        var rules = new LiveFloorRules(floor, ThinkingHere).Attach(jobs.Places);
        var wrote = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        List<string> placesUsed = [];
        var start = jobs.Start(Think, "a long task", async (job, token) =>
        {
            var partial = "";
            while (true)
            {
                lock (placesUsed) placesUsed.Add(job.Place!.Name);
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(token, job.Stopping);
                try
                {
                    if (partial.Length == 0)
                    {
                        partial = "First half. ";
                        wrote.TrySetResult();
                        await Task.Delay(Timeout.Infinite, attempt.Token);
                    }
                    return BackgroundJobOutcome.Done(partial + "Second half.");
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested && job.Stopping.IsCancellationRequested)
                {
                    await jobs.ReseatAsync(job, token);
                }
            }
        }, [Shared], wait: true);
        var job = start.Job!;
        await wrote.Task;
        floor.Words();
        await Until(() => job.State == BackgroundJobState.Paused);
        Assert.Equal(BackgroundJob.WaitingForConversation, job.Progress);
        Assert.Null(job.Place);
        Assert.Equal(1, job.Preemptions);
        Assert.Equal(1, rules.Period.Stopped["think-longer"]);
        floor.Clear();
        await Until(() => job.Finished);
        Assert.Equal(BackgroundJobState.Succeeded, job.State);
        Assert.Equal("First half. Second half.", job.Result);
        Assert.Equal(["this PC", "this PC"], placesUsed);
    }

    [Fact]
    public async Task Work_started_while_its_only_place_is_kept_for_the_conversation_waits_instead_of_being_refused()
    {
        var clock = new RuntimeClock();
        using var floor = new LiveFloor(clock);
        using var jobs = new BackgroundJobs(clock);
        new LiveFloorRules(floor, ThinkingHere).Attach(jobs.Places);
        floor.BeginReply();
        var research = new BackgroundJobKind("research", 1, 4, TimeSpan.FromMinutes(12)) { PoolKind = ThinkingJobKind.Research, Yields = true };
        var start = jobs.Start(research, "a topic", (_, _) => Task.FromResult(BackgroundJobOutcome.Done("report")), [Shared]);
        Assert.True(start.Started);
        Assert.True(start.ForConversation);
        Assert.Null(start.Job!.Place);
        await Until(() => start.Job.Progress == BackgroundJob.WaitingForConversation);
        Assert.Equal(BackgroundJobState.Waiting, start.Job.State);
        // A kind that doesn't think (a song, a picture) never waits for the conversation.
        var song = jobs.Start(new BackgroundJobKind("song", 1, 4, TimeSpan.FromMinutes(1)), "a song",
            (_, _) => Task.FromResult(BackgroundJobOutcome.Done("sung")), [Shared]);
        Assert.NotNull(song.Job?.Place);
        floor.Clear();
        await Until(() => start.Job.Finished);
        Assert.Equal("report", start.Job.Result);
    }

    [Fact]
    public async Task The_conversation_models_own_place_is_always_held_while_you_talk()
    {
        var clock = new RuntimeClock();
        using var floor = new LiveFloor(clock);
        using var jobs = new BackgroundJobs(clock);
        new LiveFloorRules(floor, LiveResources.None).Attach(jobs.Places);
        var model = new BackgroundPlace(LiveResources.ConversationModel, "openrouter.ai") { Slots = 4 };
        floor.Heard();
        var start = jobs.Start(Think, "think on the conversation model", (_, _) => Task.FromResult(BackgroundJobOutcome.Done("done")), [model], wait: true);
        Assert.True(start.ForConversation);
        await Task.Delay(100);
        Assert.False(start.Job!.Finished);
        floor.NotWords();
        await Until(() => start.Job.Finished);
        Assert.Equal("done", start.Job.Result);
    }
}
