using Martlet.Core.Contracts;
using Martlet.Core.Settings;
using Martlet.Providers;

namespace Martlet.Conversation;

/// <summary>What a Thinking pool job is for. The kind sets its default <see cref="ThinkingPriority"/> and whether it is a fast
/// kind (<see cref="ThinkingJobKinds.IsFast"/>) that may take the slot the pool keeps free. <see cref="CheckIn"/> is one of
/// Martlet's check-ins (<see cref="CheckIns"/>): a short question about what lingers, at the helpers' priority.</summary>
public enum ThinkingJobKind { BargeInJudge, EndOfTurnJudge, Digest, ThinkLonger, TouchZones, Memory, Naming, Research, CheckIn }

/// <summary>Which job a free slot takes first: the highest value first, then the oldest.</summary>
public enum ThinkingPriority
{
    Research = 10,
    Helper = 20,
    TouchZones = 35,
    ThinkLonger = 40,
    Digest = 50,
    EndOfTurnJudge = 60,
    BargeInJudge = 70
}

/// <summary>What a job needs from a member, and what a member can do: text, pictures (vision) and recordings (audio).</summary>
[Flags]
public enum ThinkingCapability { None = 0, Text = 1, Vision = 2, Audio = 4 }

/// <summary>The rules of each <see cref="ThinkingJobKind"/>.</summary>
public static class ThinkingJobKinds
{
    public static IReadOnlyList<ThinkingJobKind> All { get; } = Enum.GetValues<ThinkingJobKind>();

    public static ThinkingPriority Priority(ThinkingJobKind kind) => kind switch
    {
        ThinkingJobKind.BargeInJudge => ThinkingPriority.BargeInJudge,
        ThinkingJobKind.EndOfTurnJudge => ThinkingPriority.EndOfTurnJudge,
        ThinkingJobKind.Digest => ThinkingPriority.Digest,
        ThinkingJobKind.ThinkLonger => ThinkingPriority.ThinkLonger,
        ThinkingJobKind.TouchZones => ThinkingPriority.TouchZones,
        ThinkingJobKind.Memory or ThinkingJobKind.Naming or ThinkingJobKind.CheckIn => ThinkingPriority.Helper,
        _ => ThinkingPriority.Research
    };

    /// <summary>Fast kinds (the judges and the digests) may take the last free slot of the pool. Every other kind leaves it
    /// free when the pool has two or more slots, so a fast job never waits behind long work.</summary>
    public static bool IsFast(ThinkingJobKind kind) => kind is ThinkingJobKind.BargeInJudge or ThinkingJobKind.EndOfTurnJudge or ThinkingJobKind.Digest;

    /// <summary>The kind's name in status files and logs: barge-in-judge, think-longer...</summary>
    public static string Name(ThinkingJobKind kind) => kind switch
    {
        ThinkingJobKind.BargeInJudge => "barge-in-judge",
        ThinkingJobKind.EndOfTurnJudge => "end-of-turn-judge",
        ThinkingJobKind.Digest => "digest",
        ThinkingJobKind.ThinkLonger => "think-longer",
        ThinkingJobKind.TouchZones => "touch-zones",
        ThinkingJobKind.Memory => "memory",
        ThinkingJobKind.Naming => "naming",
        ThinkingJobKind.CheckIn => "check-in",
        _ => "research"
    };
}

/// <summary>How a job asks the board for a slot: its kind, its priority (highest first) and whether it must leave the pool's
/// last free slot for fast kinds (<see cref="KeepLastFree"/>). <see cref="Pool"/> is the whole pool the last-slot rule counts
/// (null: the places it may run on).</summary>
public sealed record ThinkingDemand(ThinkingJobKind Kind, int Priority, bool KeepLastFree, IReadOnlyList<BackgroundPlace>? Pool = null)
{
    public static ThinkingDemand For(ThinkingJobKind kind, IReadOnlyList<BackgroundPlace>? pool = null, ThinkingPriority? priority = null) =>
        new(kind, (int)(priority ?? ThinkingJobKinds.Priority(kind)), !ThinkingJobKinds.IsFast(kind), pool);
}

/// <summary>One job for the Thinking pool: instructions (the system text), the text to work on and optionally one picture and
/// one recording. <see cref="Timeout"/> bounds the run once a member took it; with <see cref="DropWhenStale"/> it bounds the
/// wait as well, and a job that no member took in time is dropped (<see cref="ThinkingJobOutcome.Stale"/>). The text is
/// private: it never goes to logs or status files.</summary>
public sealed record ThinkingJob
{
    public required ThinkingJobKind Kind { get; init; }
    public required string Instructions { get; init; }
    public required string Text { get; init; }
    public BoundedImage? Image { get; init; }
    public BoundedWaveAudio? Audio { get; init; }
    /// <summary>Null: the kind's priority.</summary>
    public ThinkingPriority? Priority { get; init; }
    /// <summary>Null: text, plus vision with an image and audio with a recording.</summary>
    public ThinkingCapability? Needs { get; init; }
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(60);
    public bool DropWhenStale { get; init; }
    public int MaxOutputTokens { get; init; } = 1_024;
    /// <summary>Whether the member thinks step by step first: true on (slower), false off (judges), null the model's default.</summary>
    public bool? Reasoning { get; init; }

    public ThinkingCapability Required => Needs ?? ThinkingCapability.Text |
        (Image is null ? ThinkingCapability.None : ThinkingCapability.Vision) |
        (Audio is null ? ThinkingCapability.None : ThinkingCapability.Audio);

    public void Validate()
    {
        ContractRules.Defined(Kind);
        ContractRules.Require(!string.IsNullOrWhiteSpace(Text) && Instructions is not null, "A Thinking pool job needs text.");
        ContractRules.Require(Timeout > TimeSpan.Zero && Timeout <= TimeSpan.FromHours(1), "A Thinking pool job's timeout is up to an hour.");
        ContractRules.Require(MaxOutputTokens is >= 1 and <= 65_536, "A Thinking pool job writes 1-65536 tokens.");
    }

    public override string ToString() => $"{nameof(ThinkingJob)} {ThinkingJobKinds.Name(Kind)} (content omitted)";
}

/// <summary>How a pool job ended. <see cref="Preempted"/>: the live conversation needed its member, and the job's result was
/// dropped (a summary is only worth its moment); other kinds wait in line again instead.</summary>
public enum ThinkingJobOutcome { Succeeded, NoMember, Stale, Failed, TimedOut, Preempted }

/// <summary>A member's answer to one attempt: its text, or that it was busy, unavailable or failed (try the next member).
/// <see cref="HeldForLive"/>: the member's computer keeps its graphics card for a live conversation turn (its own or another
/// companion PC's), so the job waits and tries again; it is not a failure of the member. <see cref="Refused"/>: the member's
/// computer refused the request itself as invalid (a paired computer's gateway answered request.invalid), so it would refuse the
/// same kind of job again: the board passes it over for such jobs for a while (<see cref="ThinkingJobBoard.RefusedRest"/>).</summary>
public sealed record ThinkingAnswer(string? Text, string? Problem = null, bool Cut = false)
{
    public bool HeldForLive { get; init; }
    public bool Refused { get; init; }
    public static ThinkingAnswer Done(string text, bool cut = false) => new(text, null, cut);
    public static ThinkingAnswer Failed(string problem) => new(null, problem);
    public static ThinkingAnswer Held(string problem) => new(null, problem) { HeldForLive = true };
    public static ThinkingAnswer Rejected(string problem) => new(null, problem) { Refused = true };
    public override string ToString() => $"{nameof(ThinkingAnswer)} (problem: {Problem is not null})";
}

/// <summary>What a pool job produced: the answer <see cref="Text"/> on success, else the <see cref="Problem"/> in a few plain
/// words, plus the member that ran it (its place ID and computer name) and how many members it tried.</summary>
public sealed record ThinkingJobResult(ThinkingJobOutcome Outcome, string? Text, string? MemberId, string? Member, string? Problem,
    int Attempts, bool Cut = false)
{
    public bool Succeeded => Outcome == ThinkingJobOutcome.Succeeded;
    /// <summary>The model the member ran it with, when known.</summary>
    public string? Model { get; init; }
    /// <summary>How many times the live conversation stopped it (or a member held its graphics card for one) before it ended.</summary>
    public int Preemptions { get; init; }
    public static ThinkingJobResult NoMember(ThinkingCapability needs) =>
        new(ThinkingJobOutcome.NoMember, null, null, null, $"the Thinking pool has no member that can do {Describe(needs)}", 0);

    /// <summary>Every member that could do the job is on a computer that doesn't answer now: callers use their fallback, as for
    /// <see cref="NoMember"/>.</summary>
    public static ThinkingJobResult Offline(ThinkingCapability needs) =>
        new(ThinkingJobOutcome.NoMember, null, null, null, $"every Thinking pool member that can do {Describe(needs)} is offline", 0);

    public static string Describe(ThinkingCapability needs) => string.Join(" and ", new[]
    {
        needs.HasFlag(ThinkingCapability.Text) ? "text" : null,
        needs.HasFlag(ThinkingCapability.Vision) ? "pictures" : null,
        needs.HasFlag(ThinkingCapability.Audio) ? "recordings" : null
    }.Where(word => word is not null));

    public override string ToString() => $"{nameof(ThinkingJobResult)} {Outcome} on {Member ?? "none"}";
}

/// <summary>One member in <see cref="ThinkingPoolStatus"/>: its place, slots in use and what it can do. <see cref="Online"/>:
/// whether its computer answers now (<see cref="BackgroundPlaces.Reachable"/>); an offline member's slots aren't in the pool's.</summary>
public sealed record ThinkingPoolMemberStatus(string Id, string Name, int Slots, int Used, ThinkingCapability Can, int Rank)
{
    public bool Online { get; init; } = true;
}

/// <summary>A member the board passes over until <paramref name="Until"/> for jobs that need at least <paramref name="Needs"/>,
/// because its computer refused such a request as invalid (<see cref="ThinkingAnswer.Refused"/>).</summary>
public sealed record ThinkingPoolRest(string Id, string Name, ThinkingCapability Needs, DateTimeOffset Until);

/// <summary>What the Thinking pool does now (no job text): its members, slots, the slot kept free for fast kinds, running and
/// waiting jobs by kind, and guidance. <see cref="Slots"/> and <see cref="Free"/> count the members whose computers answer now;
/// <see cref="ConfiguredSlots"/> counts every member. With the live floor: its level (<see cref="Floor"/>), the waiting jobs
/// held only because the conversation needs their members (<see cref="Held"/>: waiting for the conversation) and the jobs it
/// stopped since the floor last left Idle and in all (<see cref="StoppedNow"/>, <see cref="Stopped"/>).</summary>
public sealed record ThinkingPoolStatus(IReadOnlyList<ThinkingPoolMemberStatus> Members, int Slots, int Free, bool KeepsFastSlot,
    IReadOnlyDictionary<string, int> Running, IReadOnlyDictionary<string, int> Waiting, IReadOnlyList<string> Guidance)
{
    /// <summary>The slots of every member, online or not: the pool's slots when all its computers answer.</summary>
    public int ConfiguredSlots { get; init; }
    public string? Floor { get; init; }
    public IReadOnlyDictionary<string, int> Held { get; init; } = new Dictionary<string, int>();
    public IReadOnlyDictionary<string, int> StoppedNow { get; init; } = new Dictionary<string, int>();
    public IReadOnlyDictionary<string, int> Stopped { get; init; } = new Dictionary<string, int>();
    /// <summary>The members that share the live conversation's hardware (their place IDs).</summary>
    public IReadOnlyList<string> SharesLive { get; init; } = [];
    /// <summary>The members the board passes over for a while because their computer refused a request as invalid.</summary>
    public IReadOnlyList<ThinkingPoolRest> Resting { get; init; } = [];
}

/// <summary>The Thinking pool's job board: one in-process board for every Thinking pool job. Members are places
/// (<see cref="BackgroundPlace"/>, with their slots and <see cref="BackgroundPlace.Can"/>); the board shares its
/// <see cref="BackgroundPlaces"/> with the conversation's background jobs (think_longer, research), so every pool job counts
/// against the same slots. A job goes to a free slot of a capable member (the one sharing least with the conversation first);
/// a busy, unavailable or failed member is passed over for the next; when all are busy it waits in line, highest priority first.
/// A member whose computer doesn't answer now (<see cref="BackgroundPlaces.Reachable"/>) gets no job and its slots leave the
/// pool's; a job waiting in line goes to it as soon as it answers again.
/// Long kinds never take the pool's last free slot while the pool has two or more slots. The live floor's rules
/// (<see cref="LiveFloorRules"/>, the broker's <see cref="BackgroundPlaces.Rules"/>) decide besides: a job they stop (or a member
/// whose computer holds its graphics card for a live turn, <see cref="ThinkingAnswer.HeldForLive"/>) waits in line again and runs
/// later, except a summary (<see cref="ThinkingJobKind.Digest"/>), which is dropped (<see cref="ThinkingJobOutcome.Preempted"/>).
/// A member whose computer refused a request as invalid (<see cref="ThinkingAnswer.Refused"/>) rests for
/// <see cref="RefusedRest"/>: the board gives it no job that needs at least what the refused job needed, so the pool never sends
/// that computer request after request it refuses.
/// Thread-safe.</summary>
public sealed class ThinkingJobBoard
{
    /// <summary>How long a job waits before it asks a member again whose computer held its graphics card for a live turn.</summary>
    public static TimeSpan HeldRetry { get; } = TimeSpan.FromSeconds(1);
    /// <summary>How long a member whose computer refused a request as invalid gets no job that needs at least what that job
    /// needed: the refusal comes again for each such job until that computer and this PC run versions that agree.</summary>
    public static TimeSpan RefusedRest { get; } = TimeSpan.FromMinutes(10);
    private readonly Func<IReadOnlyList<BackgroundPlace>> members;
    private readonly Func<BackgroundPlace, ThinkingJob, CancellationToken, Task<ThinkingAnswer>> run;
    private readonly TimeProvider clock;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, ThinkingPoolRest> rests = new(StringComparer.Ordinal);
    private long number;

    /// <param name="members">The pool's members now (read on each call, so a settings change takes effect at once), whether their
    /// computers answer or not: the board passes over a member that doesn't answer (<see cref="BackgroundPlaces.Answers"/>) when
    /// it places a job, and a job waiting in line goes to it once it answers again.</param>
    /// <param name="run">Runs one attempt of a job on a member. It throws <see cref="OperationCanceledException"/> once its token
    /// is canceled; any other exception counts as a failed attempt.</param>
    public ThinkingJobBoard(BackgroundPlaces places, Func<IReadOnlyList<BackgroundPlace>> members,
        Func<BackgroundPlace, ThinkingJob, CancellationToken, Task<ThinkingAnswer>> run, TimeProvider? clock = null)
    {
        Places = places ?? throw new ArgumentNullException(nameof(places));
        this.members = members ?? throw new ArgumentNullException(nameof(members));
        this.run = run ?? throw new ArgumentNullException(nameof(run));
        this.clock = clock ?? TimeProvider.System;
    }

    public BackgroundPlaces Places { get; }

    /// <summary>Every member, whether its computer answers now or not.</summary>
    public IReadOnlyList<BackgroundPlace> Members => members();

    /// <summary>The members whose computers answer now.</summary>
    public IReadOnlyList<BackgroundPlace> Online => [.. Members.Where(Places.Answers)];

    /// <summary>Raised on the job's thread when a member starts to rest (<see cref="RefusedRest"/>), after the board recorded it.</summary>
    public event Action<ThinkingPoolRest>? Rested;

    /// <summary>Whether a member that answers now can take a job of <paramref name="kind"/> that needs <paramref name="needs"/>,
    /// without posting it (no waiting and no slot taken): callers choose their fallback when it is false.</summary>
    public bool CanRun(ThinkingJobKind kind, ThinkingCapability needs = ThinkingCapability.Text) =>
        Online.Any(member => Takes(member, needs));

    /// <summary>Whether a member that can take such a job may start it now (it answers, and the live floor's rules allow it; no
    /// slot taken), so a caller doesn't prepare work no member would take before it is stale.</summary>
    public bool MayStartNow(ThinkingJobKind kind, ThinkingCapability needs = ThinkingCapability.Text)
    {
        var rules = Places.Rules;
        return Online.Any(member => Takes(member, needs) && (rules?.MayStart(member, kind) ?? true));
    }

    /// <summary>Whether a member that answers and can take such a job shares no hardware with the live conversation (no slot taken).</summary>
    public bool CanRunBeside(ThinkingJobKind kind, ThinkingCapability needs = ThinkingCapability.Text)
    {
        var rules = Places.Rules;
        return Online.Any(member => Takes(member, needs) && rules?.Shares(member) != true);
    }

    /// <summary>The member a job of <paramref name="kind"/> needing <paramref name="needs"/> would go to first when every slot is
    /// free (the one sharing least with the conversation, one the live conversation doesn't use first while it isn't idle), with
    /// its name and model; null when none that answers now can (no slot taken).</summary>
    public BackgroundPlace? Find(ThinkingJobKind kind, ThinkingCapability needs = ThinkingCapability.Text)
    {
        var rules = Places.Rules;
        return Online.Where(member => Takes(member, needs))
            .OrderBy(member => rules?.Avoid(member) == true ? 1 : 0).ThenBy(member => member.Standing).FirstOrDefault();
    }

    // A member takes a job when it can do what the job needs and doesn't rest for such jobs.
    private bool Takes(BackgroundPlace member, ThinkingCapability needs) => (member.Can & needs) == needs && !Rests(member, needs);

    // A member rests for the jobs that need at least what a job it refused needed, until its rest ends.
    private bool Rests(BackgroundPlace member, ThinkingCapability needs) =>
        rests.TryGetValue(member.Id, out var rest) && (needs & rest.Needs) == rest.Needs && rest.Until > clock.GetUtcNow();

    private void Rest(BackgroundPlace member, ThinkingCapability needs)
    {
        var now = clock.GetUtcNow();
        // A member that already rests for other jobs rests for what both refused jobs needed (the wider set of jobs).
        var rest = rests.AddOrUpdate(member.Id, _ => new(member.Id, member.Name, needs, now + RefusedRest),
            (_, old) => new(member.Id, member.Name, old.Until > now ? old.Needs & needs : needs, now + RefusedRest));
        Rested?.Invoke(rest);
    }

    /// <summary>Runs <paramref name="job"/> on the pool (see the class summary) and returns its result. A pool without a capable
    /// member that answers now returns <see cref="ThinkingJobOutcome.NoMember"/> at once. Canceling <paramref name="token"/>
    /// throws <see cref="OperationCanceledException"/>.</summary>
    public async Task<ThinkingJobResult> RunAsync(ThinkingJob job, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(job);
        job.Validate();
        token.ThrowIfCancellationRequested();
        var needs = job.Required;
        var pool = Members;
        var capable = pool.Where(member => (member.Can & needs) == needs).ToArray();
        if (capable.Length == 0) return ThinkingJobResult.NoMember(needs);
        // A computer that refused such a request as invalid gets no more of them until its rest ends; the caller's fallback runs.
        if (capable.All(member => Rests(member, needs)))
            return new(ThinkingJobOutcome.NoMember, null, null, null,
                $"{string.Join(", ", capable.Select(member => member.Name))} refused such a request as invalid a short time ago", 0);
        capable = [.. capable.Where(member => !Rests(member, needs))];
        if (!capable.Any(Places.Answers)) return ThinkingJobResult.Offline(needs);
        var holder = $"{ThinkingJobKinds.Name(job.Kind)}-{Interlocked.Increment(ref number)}";
        if (holder.Length > 64) holder = holder[..64];
        var demand = ThinkingDemand.For(job.Kind, pool, job.Priority);
        using var stale = job.DropWhenStale ? new CancellationTokenSource(job.Timeout, clock) : new CancellationTokenSource();
        using var waiting = CancellationTokenSource.CreateLinkedTokenSource(token, stale.Token);
        HashSet<string> tried = new(StringComparer.Ordinal);
        string? problem = null;
        int attempts = 0, preemptions = 0;
        ThinkingJobResult Stale()
        {
            // Every capable member was held for the live conversation (or it was stopped for it): say so.
            var rules = Places.Rules;
            var conversation = preemptions > 0 || rules is not null && capable.All(member => !rules.MayStart(member, job.Kind));
            return new(ThinkingJobOutcome.Stale, null, null, null,
                conversation ? $"the conversation needed its members for more than {Wait(job.Timeout)}" : $"no member came free within {Wait(job.Timeout)}",
                attempts) { Preemptions = preemptions };
        }
        while (true)
        {
            var left = capable.Where(member => !tried.Contains(member.Id) && !Rests(member, needs)).ToArray();
            if (left.Length == 0)
                return new(ThinkingJobOutcome.Failed, null, null, null, problem ?? "no member could do it", attempts) { Preemptions = preemptions };
            // The members left are all offline (one may have stopped answering during the last attempt): don't wait for them.
            if (!left.Any(Places.Answers))
                return new(ThinkingJobOutcome.Failed, null, null, null, problem ?? "every member that could do it is offline", attempts)
                {
                    Preemptions = preemptions
                };
            BackgroundPlaceLease lease;
            try { lease = await Places.AcquireAsync(left, holder, waiting.Token, demand, preemptible: true).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { return Stale(); }
            var pause = false;
            using (lease)
            {
                var member = lease.Place;
                attempts++;
                using var limit = job.DropWhenStale ? CancellationTokenSource.CreateLinkedTokenSource(token, stale.Token)
                    : CancellationTokenSource.CreateLinkedTokenSource(token);
                if (!job.DropWhenStale) limit.CancelAfter(job.Timeout);
                // The live floor stops the attempt when the conversation needs the member (Stopping).
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(limit.Token, lease.Stopping);
                ThinkingAnswer answer;
                try { answer = await run(member, job, attempt.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (OperationCanceledException) when (lease.StopRequested && !limit.IsCancellationRequested)
                {
                    answer = ThinkingAnswer.Failed($"the conversation needed {member.Name}");
                }
                catch (OperationCanceledException)
                {
                    return new(ThinkingJobOutcome.TimedOut, null, member.Id, member.Name,
                        $"it didn't finish within {Wait(job.Timeout)}", attempts) { Preemptions = preemptions };
                }
                catch (Exception error) when (error is not OutOfMemoryException)
                {
                    answer = ThinkingAnswer.Failed($"{member.Name} failed ({error.GetType().Name})");
                }
                if (answer.Text is { Length: > 0 } text)
                    return new(ThinkingJobOutcome.Succeeded, text, member.Id, member.Name, null, attempts, answer.Cut)
                    {
                        Model = member.Model, Preemptions = preemptions
                    };
                if (lease.StopRequested || answer.HeldForLive)
                {
                    preemptions++;
                    // A summary is only worth its moment; every other kind waits in line again, where the rules allow.
                    if (job.Kind == ThinkingJobKind.Digest)
                        return new(ThinkingJobOutcome.Preempted, null, member.Id, member.Name,
                            answer.Problem ?? $"the conversation needed {member.Name}", attempts) { Preemptions = preemptions };
                    // A member whose computer refused because a live turn holds its graphics card is asked again a moment later.
                    pause = !lease.StopRequested;
                }
                else
                {
                    problem = answer.Problem ?? $"{member.Name} came back empty";
                    tried.Add(member.Id);
                    if (answer.Refused) Rest(member, needs);
                }
            }
            if (!pause) continue;
            try { await Task.Delay(HeldRetry, clock, waiting.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { return Stale(); }
        }
    }

    private static string Wait(TimeSpan time) => time < TimeSpan.FromSeconds(1) ? $"{time.TotalMilliseconds:0} ms" : BackgroundJobs.Duration(time);

    /// <summary>What the pool does now, with guidance in plain words.</summary>
    public ThinkingPoolStatus Status()
    {
        var pool = Members;
        var now = clock.GetUtcNow();
        return Describe(pool, Places) with
        {
            Resting = [.. rests.Values.Where(rest => rest.Until > now && pool.Any(member => member.Id == rest.Id)).OrderBy(rest => rest.Id, StringComparer.Ordinal)]
        };
    }

    /// <summary>The status of <paramref name="pool"/> on <paramref name="places"/>.</summary>
    public static ThinkingPoolStatus Describe(IReadOnlyList<BackgroundPlace> pool, BackgroundPlaces places)
    {
        ArgumentNullException.ThrowIfNull(pool);
        ArgumentNullException.ThrowIfNull(places);
        var leases = places.Leases;
        var members = pool.Select(member => new ThinkingPoolMemberStatus(member.Id, member.Name, member.Slots,
            Math.Min(member.Slots, leases.Where(l => l.Place.Id == member.Id).Sum(l => l.Whole ? member.Slots : 1)), member.Can, member.Rank)
        {
            Online = places.Answers(member)
        }).ToArray();
        var online = members.Where(m => m.Online).ToArray();
        var slots = online.Sum(m => m.Slots);
        var free = online.Sum(m => m.Slots - m.Used);
        var ids = pool.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
        Dictionary<string, int> running = new(StringComparer.Ordinal), waiting = new(StringComparer.Ordinal), held = new(StringComparer.Ordinal);
        foreach (var lease in leases.Where(l => ids.Contains(l.Place.Id) && l.Kind is not null))
            running[ThinkingJobKinds.Name(lease.Kind!.Value)] = running.GetValueOrDefault(ThinkingJobKinds.Name(lease.Kind!.Value)) + 1;
        foreach (var kind in places.WaitingKinds)
            waiting[ThinkingJobKinds.Name(kind)] = waiting.GetValueOrDefault(ThinkingJobKinds.Name(kind)) + 1;
        foreach (var kind in places.HeldKinds)
            held[ThinkingJobKinds.Name(kind)] = held.GetValueOrDefault(ThinkingJobKinds.Name(kind)) + 1;
        var rules = places.Rules;
        var floor = rules as LiveFloorRules;
        return new(members, slots, free, slots >= 2, running, waiting, Guidance(members))
        {
            ConfiguredSlots = members.Sum(m => m.Slots),
            Floor = floor?.Floor.Level.ToString(), Held = held,
            StoppedNow = floor?.Period.Stopped ?? LiveFloorCounts.Empty.Stopped, Stopped = floor?.Total.Stopped ?? LiveFloorCounts.Empty.Stopped,
            SharesLive = rules is null ? [] : [.. pool.Where(rules.Shares).Select(p => p.Id)]
        };
    }
    /// <summary>Plain-words guidance for a pool of <paramref name="members"/>: the slots of the members that answer now, and which
    /// members are offline.</summary>
    public static IReadOnlyList<string> Guidance(IReadOnlyList<ThinkingPoolMemberStatus> members)
    {
        var online = members.Where(m => m.Online).ToArray();
        var away = members.Where(m => !m.Online).ToArray();
        var slots = online.Sum(m => m.Slots);
        List<string> notes = [];
        if (members.Count == 0)
            notes.Add("The Thinking pool has no member: thinking longer and research use the conversation model when that is allowed, " +
                "and screen and sound summaries and the judges use their own simple rules.");
        else if (online.Length == 0)
            notes.Add($"Every Thinking pool computer is offline ({Names(away)}): thinking longer and research use the conversation model " +
                "when that is allowed, and screen and sound summaries and the judges use their own simple rules until one answers again.");
        else if (slots == 1)
            notes.Add(away.Length > 0 ? "1 slot answers now: long thinking can delay screen and sound summaries until more answer."
                : "1 slot: long thinking can delay screen and sound summaries; add a second slot for the full experience.");
        else
            notes.Add($"{slots} slots: one stays free for quick jobs (judges and summaries) while long thinking runs.");
        if (online.Length > 0 && away.Length > 0)
            notes.Add($"{Names(away)} {(away.Length == 1 ? "is" : "are")} offline: {slots} of {members.Sum(m => m.Slots)} slots answer now, " +
                "and the others come back when their computers answer again.");
        if (online.Length > 0 && !online.Any(m => m.Can.HasFlag(ThinkingCapability.Vision)))
            notes.Add(members.Any(m => m.Can.HasFlag(ThinkingCapability.Vision))
                ? "No member that answers now sees pictures: screen summaries use their simple rules until one does."
                : "No member sees pictures: screen summaries use their simple rules. Add a member with a vision model.");
        if (online.Length > 0 && !online.Any(m => m.Can.HasFlag(ThinkingCapability.Audio)))
            notes.Add(members.Any(m => m.Can.HasFlag(ThinkingCapability.Audio))
                ? "No member that answers now hears recordings: sound summaries use the transcript only until one does."
                : "No member hears recordings: sound summaries use the transcript only.");
        return notes;
    }

    private static string Names(IReadOnlyList<ThinkingPoolMemberStatus> members)
    {
        var names = members.Select(m => m.Name).Distinct(StringComparer.Ordinal).ToArray();
        return names.Length == 1 ? names[0] : $"{string.Join(", ", names[..^1])} and {names[^1]}";
    }

    public override string ToString() => nameof(ThinkingJobBoard);
}

/// <summary>What a Thinking pool member can do, from what Martlet knows of its model: pictures as Martlet's vision catalog and
/// model abilities say, recordings only on an OpenAI-compatible endpoint whose model hears (a paired computer's Ollama takes no
/// recordings).</summary>
public static class ThinkingPoolCapabilities
{
    public static ThinkingCapability For(DeepThinkingSettings member, ModelAbilities? abilities = null)
    {
        ArgumentNullException.ThrowIfNull(member);
        var can = ThinkingCapability.Text;
        switch (member.Place)
        {
            case DeepThinkingPlace.Endpoint:
                if (VisionModelCatalog.ForRoute(member.Origin, member.ModelId, abilities) == VisionSupport.Supported) can |= ThinkingCapability.Vision;
                if (HearingModelCatalog.ForRoute(SetupRouteType.ChatCompletions, member.Origin, member.ModelId, abilities) == HearingSupport.Supported)
                    can |= ThinkingCapability.Audio;
                break;
            case DeepThinkingPlace.Host:
                if (VisionModelCatalog.Classify(member.ModelId) == VisionSupport.Supported) can |= ThinkingCapability.Vision;
                break;
        }
        return can;
    }
}
