using Martlet.Core.Contracts;

namespace Martlet.Conversation;

/// <summary>A computer (or provider) background work can run on: its stable <paramref name="Id"/> (such as <c>host:diva</c>),
/// the <paramref name="Name"/> the talk window, <c>background-jobs.json</c> and the desktop log show (the computer's name only,
/// such as "diva" or "this PC"), and its <paramref name="Rank"/>: lower goes first (0 does none of the conversation's jobs, 1
/// shares a computer or provider with the voice or listening, 2 with Thinking, 3 shares Thinking's graphics card on this PC).
/// <see cref="Slots"/> is how many jobs it runs at once (a computer with several Deep thinking models or parallel slots on its
/// graphics card, or a cloud provider, runs several); <see cref="Duties"/> names other work its computer is kept free for
/// (such as "singing" or "image generation"), so it is used only when places of the same rank without such duties are busy.
/// Places of equal standing go in the order they were given.</summary>
public sealed record BackgroundPlace(string Id, string Name, int Rank = 0)
{
    public const int MaxRank = 9;
    public const int MaxSlots = 8;

    /// <summary>How many jobs run on it at once (1-8).</summary>
    public int Slots { get; init; } = 1;

    /// <summary>Other work its computer is kept free for, in a word or two each ("singing", "image generation"); empty when none.</summary>
    public IReadOnlyList<string> Duties { get; init; } = [];

    /// <summary>What a Thinking pool member can do (text, pictures, recordings); text only unless its model is known to do more.</summary>
    public ThinkingCapability Can { get; init; } = ThinkingCapability.Text;

    /// <summary>The model a Thinking pool member runs (its model ID), when known.</summary>
    public string? Model { get; init; }

    /// <summary>Where it stands in line for new work, lowest first: its rank, and a place kept free for other duties just after the
    /// places of the same rank without any. Deterministic: the same places and the same load always pick the same place.</summary>
    public int Standing => Rank * 2 + (Duties.Count > 0 ? 1 : 0);

    public void Validate()
    {
        ContractRules.Require(Id is { Length: > 0 and <= 4096 } && !Id.Any(char.IsControl), "A background place needs a short ID.");
        ContractRules.Require(Name is { Length: > 0 and <= 80 } && !Name.Any(char.IsControl), "A background place needs a short name.");
        ContractRules.Require(Rank is >= 0 and <= MaxRank, "A background place's rank is 0-9.");
        ContractRules.Require(Slots is >= 1 and <= MaxSlots, "A background place runs 1-8 jobs at once.");
        ContractRules.Require(Duties is { Count: <= 8 } && Duties.All(d => d is { Length: > 0 and <= 40 } && !d.Any(char.IsControl)),
            "A background place's other duties are a few short words each.");
    }

    public override string ToString() => $"{nameof(BackgroundPlace)} {Name}";
}

/// <summary>One background job's (or one step's) hold on a <see cref="BackgroundPlace"/>, until it is disposed. A
/// <see cref="Whole"/> hold (a song on the singing computer) takes every slot of that place.</summary>
public sealed class BackgroundPlaceLease : IDisposable
{
    private readonly BackgroundPlaces owner;
    private int released;

    internal BackgroundPlaceLease(BackgroundPlaces owner, BackgroundPlace place, string holder, bool whole = false, ThinkingJobKind? kind = null)
    {
        this.owner = owner;
        Place = place;
        Holder = holder;
        Whole = whole;
        Kind = kind;
    }

    public BackgroundPlace Place { get; }
    /// <summary>The Thinking pool job kind that holds it, when it was asked for with a <see cref="ThinkingDemand"/>.</summary>
    public ThinkingJobKind? Kind { get; }
    /// <summary>Who holds it: a job ID such as think-2.</summary>
    public string Holder { get; }
    /// <summary>Whether it holds the whole place (every slot), such as a song being made on that computer.</summary>
    public bool Whole { get; }
    public bool Released => Volatile.Read(ref released) != 0;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref released, 1) == 0) owner.Release(this);
    }

    public override string ToString() => $"{nameof(BackgroundPlaceLease)} {Holder} on {Place.Name}";
}

/// <summary>Martlet's request broker for background work (one per <see cref="BackgroundJobs"/>, <see cref="BackgroundJobs.Places"/>):
/// which places are busy with what, and who waits for one. A kind asks for a free place from its pool (<see cref="TryAcquire"/>,
/// or <see cref="AcquireAsync"/> to wait in line) and releases it when done; other work its computer does (a song) holds the
/// place for a while (<see cref="Hold"/>) so nothing else lands there. Every choice is deterministic and instant (no model is
/// asked): a free slot on the place with the lowest <see cref="BackgroundPlace.Standing"/>, then the least busy, then the pool's
/// order. Waiters are served highest priority first (<see cref="ThinkingDemand.Priority"/>), then first come, first served, each
/// as soon as a place of its own pool frees up. A demand with <see cref="ThinkingDemand.KeepLastFree"/> never takes the last free
/// slot of its pool while that pool has two or more slots (the Thinking pool's slot for fast jobs). Thread-safe.</summary>
public sealed class BackgroundPlaces
{
    private sealed record Waiter(IReadOnlyList<BackgroundPlace> Pool, string Holder, TaskCompletionSource<BackgroundPlaceLease> Done,
        ThinkingDemand? Demand, long Order);

    private long order;

    private readonly object gate = new();
    private readonly List<BackgroundPlaceLease> leases = [];
    private readonly List<Waiter> waiters = [];

    /// <summary>Raised on any thread when a place is taken or released, or the line changes.</summary>
    public event Action? Changed;

    /// <summary>Every place held now, oldest first.</summary>
    public IReadOnlyList<BackgroundPlaceLease> Leases { get { lock (gate) return [.. leases]; } }

    /// <summary>Who waits for a place now, first in line first.</summary>
    public IReadOnlyList<string> Line { get { lock (gate) return [.. Ordered().Select(w => w.Holder)]; } }

    /// <summary>The Thinking pool job kinds waiting now, first in line first.</summary>
    public IReadOnlyList<ThinkingJobKind> WaitingKinds
    {
        get { lock (gate) return [.. Ordered().Where(w => w.Demand is not null).Select(w => w.Demand!.Kind)]; }
    }

    // Highest priority first, then first come. Called under the gate.
    private IEnumerable<Waiter> Ordered() => waiters.OrderByDescending(w => w.Demand?.Priority ?? 0).ThenBy(w => w.Order);

    /// <summary>How many hold place <paramref name="id"/> now.</summary>
    public int Load(string id)
    {
        lock (gate) return leases.Count(lease => lease.Place.Id == id);
    }

    /// <summary>Where <paramref name="holder"/> is in line (1 is next), or 0 when it isn't waiting.</summary>
    public int Position(string holder)
    {
        lock (gate) return Ordered().ToList().FindIndex(w => w.Holder == holder) + 1;
    }

    /// <summary>Takes the best free place in <paramref name="pool"/> for <paramref name="holder"/> (see the class summary). With
    /// <paramref name="share"/>, the least busy place when none is free (work that may wait on a busy place). Null when none (or
    /// <paramref name="pool"/> is empty).</summary>
    public BackgroundPlaceLease? TryAcquire(IReadOnlyList<BackgroundPlace> pool, string holder, bool share = false, ThinkingDemand? demand = null)
    {
        var lease = Acquire(pool, holder, share, demand);
        if (lease is not null) Changed?.Invoke();
        return lease;
    }

    /// <summary>Takes the best free place in <paramref name="pool"/> for <paramref name="holder"/>, waiting in line until one
    /// frees up when all are busy. Canceling <paramref name="token"/> leaves the line.</summary>
    public Task<BackgroundPlaceLease> AcquireAsync(IReadOnlyList<BackgroundPlace> pool, string holder, CancellationToken token,
        ThinkingDemand? demand = null)
    {
        Check(pool, holder);
        ContractRules.Require(pool.Count > 0, "A background job's pool has at least one place.");
        token.ThrowIfCancellationRequested();
        Waiter waiter;
        BackgroundPlaceLease? now = null;
        lock (gate)
        {
            // Nobody earlier in line can use a free place (they'd have taken it), so a free one here is this holder's.
            if (Choose(pool, share: false, demand) is { } free)
            {
                now = new BackgroundPlaceLease(this, free, holder, kind: demand?.Kind);
                leases.Add(now);
            }
            waiter = new(pool, holder, new(TaskCreationOptions.RunContinuationsAsynchronously), demand, ++order);
            if (now is null) waiters.Add(waiter);
        }
        Changed?.Invoke();
        if (now is not null) return Task.FromResult(now);
        if (token.CanBeCanceled)
        {
            var registration = token.Register(() =>
            {
                bool left;
                lock (gate) left = waiters.Remove(waiter);
                if (!left) return;
                waiter.Done.TrySetCanceled(token);
                Changed?.Invoke();
            });
            _ = waiter.Done.Task.ContinueWith(_ => registration.Dispose(), CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        return waiter.Done.Task;
    }

    /// <summary>Holds the whole of <paramref name="place"/> for <paramref name="holder"/> (other work its computer does, such as
    /// making a song): no background work is placed there until it is disposed. Work already running there carries on.</summary>
    public BackgroundPlaceLease Hold(BackgroundPlace place, string holder)
    {
        Check([place], holder);
        BackgroundPlaceLease lease;
        lock (gate)
        {
            lease = new(this, place, holder, whole: true);
            leases.Add(lease);
        }
        Changed?.Invoke();
        return lease;
    }

    // Without raising Changed (the job list raises its own once the job is in its list).
    internal BackgroundPlaceLease? Acquire(IReadOnlyList<BackgroundPlace> pool, string holder, bool share, ThinkingDemand? demand = null)
    {
        Check(pool, holder);
        lock (gate)
        {
            var choice = Choose(pool, share, demand);
            if (choice is null) return null;
            var lease = new BackgroundPlaceLease(this, choice, holder, kind: demand?.Kind);
            leases.Add(lease);
            return lease;
        }
    }

    private static void Check(IReadOnlyList<BackgroundPlace> pool, string holder)
    {
        ArgumentNullException.ThrowIfNull(pool);
        ContractRules.Require(!string.IsNullOrWhiteSpace(holder) && holder.Length <= 64, "A background place's holder needs a short name.");
        foreach (var place in pool) place.Validate();
    }

    // How many of a place's slots are taken: one per lease, all of them while it is held whole. Called under the gate.
    private int Used(BackgroundPlace place)
    {
        var used = 0;
        foreach (var lease in leases)
            if (lease.Place.Id == place.Id)
                used += lease.Whole ? place.Slots : 1;
        return used;
    }

    // The deterministic choice: a free slot on the place with the lowest standing, then the least busy, then the pool's order;
    // with share and none free, the least busy place by its share of slots (a whole hold last). Called under the gate.
    private BackgroundPlace? Choose(IReadOnlyList<BackgroundPlace> pool, bool share, ThinkingDemand? demand = null)
    {
        if (demand is { KeepLastFree: true } && !LeavesFastSlot(demand.Pool ?? pool)) return null;
        var candidates = pool.Select((place, order) => (place, order, used: Used(place))).ToArray();
        var free = candidates.Where(c => c.used < c.place.Slots)
            .OrderBy(c => c.place.Standing).ThenBy(c => c.used).ThenBy(c => c.order).Select(c => c.place).FirstOrDefault();
        if (free is not null || !share) return free;
        return candidates.OrderBy(c => leases.Any(l => l.Whole && l.Place.Id == c.place.Id))
            .ThenBy(c => (double)c.used / c.place.Slots).ThenBy(c => c.place.Standing).ThenBy(c => c.order)
            .Select(c => c.place).FirstOrDefault();
    }

    // Whether a long job may take a slot of whole: always with one slot in all, else only while two or more are free (one stays
    // free for fast jobs). Called under the gate.
    private bool LeavesFastSlot(IReadOnlyList<BackgroundPlace> whole)
    {
        var distinct = whole.DistinctBy(place => place.Id).ToArray();
        if (distinct.Sum(place => place.Slots) < 2) return true;
        return distinct.Sum(place => Math.Max(0, place.Slots - Used(place))) >= 2;
    }

    /// <summary>What holds the places of <paramref name="pool"/> now, in words: "think-1 on diva and think-2 on ripley".</summary>
    public string Busy(IReadOnlyList<BackgroundPlace> pool)
    {
        ArgumentNullException.ThrowIfNull(pool);
        List<string> held;
        lock (gate) held = [.. leases.Where(lease => pool.Any(place => place.Id == lease.Place.Id)).Select(lease => $"{lease.Holder} on {lease.Place.Name}")];
        return held.Count switch
        {
            0 => "nothing",
            1 => held[0],
            _ => string.Join(", ", held.Take(held.Count - 1)) + " and " + held[^1]
        };
    }

    internal void Release(BackgroundPlaceLease lease)
    {
        bool removed;
        List<(Waiter Waiter, BackgroundPlaceLease Lease)> served = [];
        lock (gate)
        {
            removed = leases.Remove(lease);
            if (removed)
                // First come, first served: each waiter in turn takes a free place of its own pool, if one is free now.
                foreach (var waiter in Ordered().ToArray())
                {
                    if (Choose(waiter.Pool, share: false, waiter.Demand) is not { } free) continue;
                    var next = new BackgroundPlaceLease(this, free, waiter.Holder, kind: waiter.Demand?.Kind);
                    leases.Add(next);
                    served.Add((waiter, next));
                    waiters.Remove(waiter);
                }
        }
        foreach (var (waiter, next) in served)
            if (!waiter.Done.TrySetResult(next)) next.Dispose();
        if (removed) Changed?.Invoke();
    }

    public override string ToString() => nameof(BackgroundPlaces);
}
