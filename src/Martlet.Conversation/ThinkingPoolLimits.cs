namespace Martlet.Conversation;

/// <summary>A Thinking pool member whose provider limited requests (<see cref="ThinkingAnswer.RateLimited"/>): it gets no new job
/// until <see cref="Until"/> (null: it may take jobs again), and it runs <see cref="SlotsNow"/> of its <see cref="Slots"/> jobs
/// at once until a run of successes raises them again. <see cref="Times"/>: how many times it limited requests since it last ran
/// at all its slots. <see cref="Problem"/>: what it said last, in a few plain words (never a job's text).</summary>
public sealed record ThinkingPoolCooling(string Id, string Name, DateTimeOffset? Until, int SlotsNow, int Slots, int Times, string Problem)
{
    /// <summary>The cooling member in plain words: "NVIDIA is limiting requests; tries again in 40 s", or "NVIDIA runs 2 of 4 jobs
    /// at once for now, because it limited requests".</summary>
    public string Describe(DateTimeOffset now) => Until is { } until && until > now
        ? $"{Name} is limiting requests; tries again in {In(until - now)}" +
            (SlotsNow < Slots ? $", then runs {SlotsNow} of {Slots} jobs at once for a while" : "")
        : $"{Name} runs {SlotsNow} of {Slots} job{(Slots == 1 ? "" : "s")} at once for now, because it limited requests";

    private static string In(TimeSpan wait) => wait < TimeSpan.FromMinutes(1) ? $"{Math.Max(1, Math.Ceiling(wait.TotalSeconds)):0} s"
        : wait < TimeSpan.FromHours(1) ? $"{Math.Ceiling(wait.TotalMinutes):0} min" : $"{wait.TotalHours:0.#} h";
}

/// <summary>How the Thinking pool adjusts to members whose providers limit requests (a free cloud endpoint such as NVIDIA Build
/// answers 429 or 503 when it is busy). Only the member that limited requests is affected:
/// <list type="number">
/// <item>It cools down: no new job goes to it until the provider's Retry-After passes, or, without one, for a wait that starts at
/// <see cref="FirstWait"/> and doubles on each further limit, up to <see cref="MaxWait"/>. A success resets the wait.</item>
/// <item>It runs fewer jobs at once: each limit halves its live slot limit (at least 1), and every
/// <see cref="RaiseAfter"/> successes in a row raise it by one, back up to its configured slots (the owner's choice).</item>
/// </list>
/// The broker reads the limit (<see cref="BackgroundPlaces.SlotLimit"/>), and a member that cools down gets its jobs again as
/// soon as its wait ends (<see cref="BackgroundPlaces.Reconsider"/>). Thread-safe.</summary>
public sealed class ThinkingPoolLimits : IDisposable
{
    public static TimeSpan FirstWait { get; } = TimeSpan.FromSeconds(5);
    public static TimeSpan MaxWait { get; } = TimeSpan.FromMinutes(5);
    /// <summary>The longest Retry-After Martlet honors; a longer one counts as this long.</summary>
    public static TimeSpan MaxRetryAfter { get; } = TimeSpan.FromHours(1);
    public const int RaiseAfter = 5;

    private sealed class State(string name, int slots)
    {
        internal string Name = name;
        internal int Slots = slots, SlotsNow = slots, Successes, Times;
        internal DateTimeOffset? Until;
        internal TimeSpan NextWait = FirstWait;
        internal string Problem = "";
        internal ITimer? Wake;
    }

    private readonly object gate = new();
    private readonly Dictionary<string, State> states = new(StringComparer.Ordinal);
    private readonly TimeProvider clock;
    private readonly Action wake;
    private bool disposed;

    /// <param name="wake">Called (off the caller's thread, without the lock) when a member's wait ends, so work waiting in line
    /// takes it again: the board passes <see cref="BackgroundPlaces.Reconsider"/>.</param>
    public ThinkingPoolLimits(Action wake, TimeProvider? clock = null)
    {
        this.wake = wake ?? throw new ArgumentNullException(nameof(wake));
        this.clock = clock ?? TimeProvider.System;
    }

    /// <summary>Notes that <paramref name="member"/>'s provider limited a request and returns the member's state now. It waits for
    /// <paramref name="retryAfter"/> when the provider gave one (0 or less: the backoff), else for the backoff; a wait that already
    /// ends later stays.</summary>
    public ThinkingPoolCooling Limited(BackgroundPlace member, TimeSpan? retryAfter, string problem)
    {
        ArgumentNullException.ThrowIfNull(member);
        lock (gate)
        {
            var state = StateOf(member);
            var wait = retryAfter is { } asked && asked > TimeSpan.Zero
                ? asked > MaxRetryAfter ? MaxRetryAfter : asked
                : state.NextWait;
            state.NextWait = state.NextWait * 2 > MaxWait ? MaxWait : state.NextWait * 2;
            var until = clock.GetUtcNow() + wait;
            if (state.Until is not { } old || old < until) state.Until = until;
            state.SlotsNow = Math.Max(1, state.SlotsNow / 2);
            state.Successes = 0;
            state.Times++;
            state.Problem = problem;
            state.Wake?.Dispose();
            state.Wake = disposed ? null : clock.CreateTimer(_ => Ended(member.Id), null, state.Until.Value - clock.GetUtcNow(), Timeout.InfiniteTimeSpan);
            return Snapshot(member.Id, state)!;
        }
    }

    /// <summary>Notes a success on <paramref name="member"/>: its wait starts again from <see cref="FirstWait"/>, and after
    /// <see cref="RaiseAfter"/> successes in a row it runs one more job at once, up to its configured slots. True when its slot
    /// limit went up (the caller lets the broker know once the job's slot is free).</summary>
    public bool Succeeded(BackgroundPlace member)
    {
        ArgumentNullException.ThrowIfNull(member);
        lock (gate)
        {
            if (!states.TryGetValue(member.Id, out var state)) return false;
            state.NextWait = FirstWait;
            state.Slots = member.Slots;
            if (++state.Successes < RaiseAfter && state.SlotsNow < state.Slots) return false;
            state.Successes = 0;
            var raised = state.SlotsNow < state.Slots;
            if (raised) state.SlotsNow++;
            // Back at every slot and not waiting: forgotten until the provider limits requests again.
            if (state.SlotsNow >= state.Slots && !Cools(state))
            {
                state.Wake?.Dispose();
                states.Remove(member.Id);
            }
            return raised;
        }
    }

    /// <summary>Whether <paramref name="member"/> waits now because its provider limited requests.</summary>
    public bool Cooling(BackgroundPlace member)
    {
        lock (gate) return states.TryGetValue(member.Id, out var state) && Cools(state);
    }

    /// <summary>How many jobs <paramref name="member"/> may run at once now: 0 while it waits, else its live slot limit (never more
    /// than its configured <see cref="BackgroundPlace.Slots"/>). For <see cref="BackgroundPlaces.SlotLimit"/>.</summary>
    public int SlotsNow(BackgroundPlace member)
    {
        lock (gate)
        {
            if (!states.TryGetValue(member.Id, out var state)) return member.Slots;
            return Cools(state) ? 0 : Math.Min(member.Slots, state.SlotsNow);
        }
    }

    /// <summary>The members of <paramref name="pool"/> that wait or run fewer jobs at once now, by ID.</summary>
    public IReadOnlyList<ThinkingPoolCooling> Now(IReadOnlyList<BackgroundPlace> pool)
    {
        ArgumentNullException.ThrowIfNull(pool);
        lock (gate)
            return [.. pool.DistinctBy(p => p.Id).Where(p => states.ContainsKey(p.Id))
                .Select(p => Snapshot(p.Id, states[p.Id], p) is { } cooling && (cooling.Until is not null || cooling.SlotsNow < cooling.Slots) ? cooling : null)
                .OfType<ThinkingPoolCooling>().OrderBy(c => c.Id, StringComparer.Ordinal)];
    }

    // Called under the gate.
    private State StateOf(BackgroundPlace member)
    {
        if (!states.TryGetValue(member.Id, out var state)) states[member.Id] = state = new(member.Name, member.Slots);
        state.Name = member.Name;
        if (state.Slots != member.Slots)
        {
            state.Slots = member.Slots;
            state.SlotsNow = Math.Min(state.SlotsNow, member.Slots);
        }
        return state;
    }

    private bool Cools(State state) => state.Until is { } until && until > clock.GetUtcNow();

    private ThinkingPoolCooling? Snapshot(string id, State state, BackgroundPlace? member = null)
    {
        var slots = member?.Slots ?? state.Slots;
        return new(id, member?.Name ?? state.Name, Cools(state) ? state.Until : null, Math.Min(slots, state.SlotsNow), slots, state.Times, state.Problem);
    }

    private void Ended(string id)
    {
        lock (gate)
        {
            if (disposed) return;
            if (states.TryGetValue(id, out var state) && Cools(state)) return;
        }
        wake();
    }

    public void Dispose()
    {
        lock (gate)
        {
            disposed = true;
            foreach (var state in states.Values) state.Wake?.Dispose();
        }
    }

    public override string ToString() => nameof(ThinkingPoolLimits);
}
