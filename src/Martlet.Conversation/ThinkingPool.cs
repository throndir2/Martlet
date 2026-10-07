using Martlet.Core.Contracts;
using Martlet.Core.Settings;
using Martlet.Providers;

namespace Martlet.Conversation;

/// <summary>What a Thinking pool job is for. The kind sets its default <see cref="ThinkingPriority"/> and whether it is a fast
/// kind (<see cref="ThinkingJobKinds.IsFast"/>) that may take the slot the pool keeps free.</summary>
public enum ThinkingJobKind { BargeInJudge, EndOfTurnJudge, Digest, ThinkLonger, TouchZones, Memory, Naming, Research }

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
        ThinkingJobKind.Memory or ThinkingJobKind.Naming => ThinkingPriority.Helper,
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

public enum ThinkingJobOutcome { Succeeded, NoMember, Stale, Failed, TimedOut }

/// <summary>A member's answer to one attempt: its text, or that it was busy, unavailable or failed (try the next member).</summary>
public sealed record ThinkingAnswer(string? Text, string? Problem = null, bool Cut = false)
{
    public static ThinkingAnswer Done(string text, bool cut = false) => new(text, null, cut);
    public static ThinkingAnswer Failed(string problem) => new(null, problem);
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
    public static ThinkingJobResult NoMember(ThinkingCapability needs) =>
        new(ThinkingJobOutcome.NoMember, null, null, null, $"the Thinking pool has no member that can do {Describe(needs)}", 0);

    public static string Describe(ThinkingCapability needs) => string.Join(" and ", new[]
    {
        needs.HasFlag(ThinkingCapability.Text) ? "text" : null,
        needs.HasFlag(ThinkingCapability.Vision) ? "pictures" : null,
        needs.HasFlag(ThinkingCapability.Audio) ? "recordings" : null
    }.Where(word => word is not null));

    public override string ToString() => $"{nameof(ThinkingJobResult)} {Outcome} on {Member ?? "none"}";
}

/// <summary>One member in <see cref="ThinkingPoolStatus"/>: its place, slots in use and what it can do.</summary>
public sealed record ThinkingPoolMemberStatus(string Id, string Name, int Slots, int Used, ThinkingCapability Can, int Rank);

/// <summary>What the Thinking pool does now (no job text): its members, slots, the slot kept free for fast kinds, running and
/// waiting jobs by kind, and guidance.</summary>
public sealed record ThinkingPoolStatus(IReadOnlyList<ThinkingPoolMemberStatus> Members, int Slots, int Free, bool KeepsFastSlot,
    IReadOnlyDictionary<string, int> Running, IReadOnlyDictionary<string, int> Waiting, IReadOnlyList<string> Guidance);

/// <summary>The Thinking pool's job board: one in-process board for every Thinking pool job. Members are places
/// (<see cref="BackgroundPlace"/>, with their slots and <see cref="BackgroundPlace.Can"/>); the board shares its
/// <see cref="BackgroundPlaces"/> with the conversation's background jobs (think_longer, research), so every pool job counts
/// against the same slots. A job goes to a free slot of a capable member (the one sharing least with the conversation first);
/// a busy, unavailable or failed member is passed over for the next; when all are busy it waits in line, highest priority first.
/// Long kinds never take the pool's last free slot while the pool has two or more slots. Thread-safe.</summary>
public sealed class ThinkingJobBoard
{
    private readonly Func<IReadOnlyList<BackgroundPlace>> members;
    private readonly Func<BackgroundPlace, ThinkingJob, CancellationToken, Task<ThinkingAnswer>> run;
    private readonly TimeProvider clock;
    private long number;

    /// <param name="members">The pool's members now (read on each call, so a settings change takes effect at once).</param>
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

    public IReadOnlyList<BackgroundPlace> Members => members();

    /// <summary>Whether a member can take a job of <paramref name="kind"/> that needs <paramref name="needs"/>, without posting
    /// it (no waiting and no slot taken): callers choose their fallback when it is false.</summary>
    public bool CanRun(ThinkingJobKind kind, ThinkingCapability needs = ThinkingCapability.Text) =>
        Members.Any(member => (member.Can & needs) == needs);

    /// <summary>The member a job of <paramref name="kind"/> needing <paramref name="needs"/> would go to first when every slot is
    /// free (the one sharing least with the conversation), with its name and model; null when none can (no slot taken).</summary>
    public BackgroundPlace? Find(ThinkingJobKind kind, ThinkingCapability needs = ThinkingCapability.Text) =>
        Members.Where(member => (member.Can & needs) == needs).OrderBy(member => member.Standing).FirstOrDefault();

    /// <summary>Runs <paramref name="job"/> on the pool (see the class summary) and returns its result. A pool without a capable
    /// member returns <see cref="ThinkingJobOutcome.NoMember"/> at once. Canceling <paramref name="token"/> throws
    /// <see cref="OperationCanceledException"/>.</summary>
    public async Task<ThinkingJobResult> RunAsync(ThinkingJob job, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(job);
        job.Validate();
        token.ThrowIfCancellationRequested();
        var needs = job.Required;
        var pool = Members;
        var capable = pool.Where(member => (member.Can & needs) == needs).ToArray();
        if (capable.Length == 0) return ThinkingJobResult.NoMember(needs);
        var holder = $"{ThinkingJobKinds.Name(job.Kind)}-{Interlocked.Increment(ref number)}";
        if (holder.Length > 64) holder = holder[..64];
        var demand = ThinkingDemand.For(job.Kind, pool, job.Priority);
        using var stale = job.DropWhenStale ? new CancellationTokenSource(job.Timeout, clock) : new CancellationTokenSource();
        using var waiting = CancellationTokenSource.CreateLinkedTokenSource(token, stale.Token);
        HashSet<string> tried = new(StringComparer.Ordinal);
        string? problem = null;
        var attempts = 0;
        while (true)
        {
            var left = capable.Where(member => !tried.Contains(member.Id)).ToArray();
            if (left.Length == 0)
                return new(ThinkingJobOutcome.Failed, null, null, null, problem ?? "no member could do it", attempts);
            BackgroundPlaceLease lease;
            try { lease = await Places.AcquireAsync(left, holder, waiting.Token, demand).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                return new(ThinkingJobOutcome.Stale, null, null, null,
                    $"no member came free within {Wait(job.Timeout)}", attempts);
            }
            using (lease)
            {
                var member = lease.Place;
                attempts++;
                using var limit = job.DropWhenStale ? CancellationTokenSource.CreateLinkedTokenSource(token, stale.Token)
                    : CancellationTokenSource.CreateLinkedTokenSource(token);
                if (!job.DropWhenStale) limit.CancelAfter(job.Timeout);
                ThinkingAnswer answer;
                try { answer = await run(member, job, limit.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (OperationCanceledException)
                {
                    return new(ThinkingJobOutcome.TimedOut, null, member.Id, member.Name,
                        $"it didn't finish within {Wait(job.Timeout)}", attempts);
                }
                catch (Exception error) when (error is not OutOfMemoryException)
                {
                    answer = ThinkingAnswer.Failed($"{member.Name} failed ({error.GetType().Name})");
                }
                if (answer.Text is { Length: > 0 } text)
                    return new(ThinkingJobOutcome.Succeeded, text, member.Id, member.Name, null, attempts, answer.Cut) { Model = member.Model };
                problem = answer.Problem ?? $"{member.Name} came back empty";
                tried.Add(member.Id);
            }
        }
    }

    private static string Wait(TimeSpan time) => time < TimeSpan.FromSeconds(1) ? $"{time.TotalMilliseconds:0} ms" : BackgroundJobs.Duration(time);

    /// <summary>What the pool does now, with guidance in plain words.</summary>
    public ThinkingPoolStatus Status() => Describe(Members, Places);

    /// <summary>The status of <paramref name="pool"/> on <paramref name="places"/>.</summary>
    public static ThinkingPoolStatus Describe(IReadOnlyList<BackgroundPlace> pool, BackgroundPlaces places)
    {
        ArgumentNullException.ThrowIfNull(pool);
        ArgumentNullException.ThrowIfNull(places);
        var leases = places.Leases;
        var members = pool.Select(member => new ThinkingPoolMemberStatus(member.Id, member.Name, member.Slots,
            Math.Min(member.Slots, leases.Where(l => l.Place.Id == member.Id).Sum(l => l.Whole ? member.Slots : 1)), member.Can, member.Rank)).ToArray();
        var slots = members.Sum(m => m.Slots);
        var free = members.Sum(m => m.Slots - m.Used);
        var ids = pool.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
        Dictionary<string, int> running = new(StringComparer.Ordinal), waiting = new(StringComparer.Ordinal);
        foreach (var lease in leases.Where(l => ids.Contains(l.Place.Id) && l.Kind is not null))
            running[ThinkingJobKinds.Name(lease.Kind!.Value)] = running.GetValueOrDefault(ThinkingJobKinds.Name(lease.Kind!.Value)) + 1;
        foreach (var kind in places.WaitingKinds)
            waiting[ThinkingJobKinds.Name(kind)] = waiting.GetValueOrDefault(ThinkingJobKinds.Name(kind)) + 1;
        return new(members, slots, free, slots >= 2, running, waiting, Guidance(members));
    }

    /// <summary>Plain-words guidance for a pool of <paramref name="members"/>.</summary>
    public static IReadOnlyList<string> Guidance(IReadOnlyList<ThinkingPoolMemberStatus> members)
    {
        var slots = members.Sum(m => m.Slots);
        List<string> notes = [];
        if (members.Count == 0)
            notes.Add("The Thinking pool has no member: thinking longer and research use the conversation model when that is allowed, " +
                "and screen and sound summaries and the judges use their own simple rules.");
        else if (slots == 1)
            notes.Add("1 slot: long thinking can delay screen and sound summaries; add a second slot for the full experience.");
        else
            notes.Add($"{slots} slots: one stays free for quick jobs (judges and summaries) while long thinking runs.");
        if (members.Count > 0 && !members.Any(m => m.Can.HasFlag(ThinkingCapability.Vision)))
            notes.Add("No member sees pictures: screen summaries use their simple rules. Add a member with a vision model.");
        if (members.Count > 0 && !members.Any(m => m.Can.HasFlag(ThinkingCapability.Audio)))
            notes.Add("No member hears recordings: sound summaries use the transcript only.");
        return notes;
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
