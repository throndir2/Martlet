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

    /// <summary>The computer it runs on, for the live floor (<see cref="LiveResources"/>): <see cref="LiveResources.ThisPc"/>, a
    /// computer on the home network ("lan:192.168.1.20"), or null for a cloud provider, which shares no hardware with the
    /// conversation's own.</summary>
    public string? Machine { get; init; }

    /// <summary>The graphics cards it runs on, as its computer names them; empty when not known (then the whole computer counts).</summary>
    public IReadOnlyList<string> Gpus { get; init; } = [];

    /// <summary>Whether it takes quick jobs: the judges and the screen and sound summaries (<see cref="ThinkingJobKinds.IsFast"/>).
    /// The owner's Quick jobs box on Companion › Thinking pool; on unless unticked.</summary>
    public bool QuickJobs { get; init; } = true;

    /// <summary>Whether it takes long jobs: every other kind (thinking longer, research, a song's lyrics, touch zones, remembering,
    /// naming and check-ins). The owner's Long jobs box on Companion › Thinking pool; on unless unticked.</summary>
    public bool LongJobs { get; init; } = true;

    /// <summary>Whether it takes a job of <paramref name="kind"/>: a quick kind where <see cref="QuickJobs"/>, any other where
    /// <see cref="LongJobs"/>.</summary>
    public bool Takes(ThinkingJobKind kind) => ThinkingJobKinds.IsFast(kind) ? QuickJobs : LongJobs;

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
        ContractRules.Require(Machine is null || Machine.Length is > 0 and <= 200 && !Machine.Any(char.IsControl),
            "A background place's computer is a short name.");
        ContractRules.Require(Gpus is { Count: <= 16 } && Gpus.All(g => g is { Length: > 0 and <= 128 } && !g.Any(char.IsControl)),
            "A background place has at most 16 graphics cards, each a short name.");
    }

    public override string ToString() => $"{nameof(BackgroundPlace)} {Name}";
}

/// <summary>One background job's (or one step's) hold on a <see cref="BackgroundPlace"/>, until it is disposed. A
/// <see cref="Whole"/> hold (a song on the singing computer) takes every slot of that place. A <see cref="Preemptible"/> hold is
/// stopped when the live conversation needs the place (<see cref="Stopping"/>, <see cref="IPlaceRules"/>): its holder stops its
/// work, lets the place go and goes on later; the place stays taken until then.</summary>
public sealed class BackgroundPlaceLease : IDisposable
{
    private readonly BackgroundPlaces owner;
    private readonly CancellationTokenSource? stopping;
    private int released, stopped, forPriority;

    internal BackgroundPlaceLease(BackgroundPlaces owner, BackgroundPlace place, string holder, bool whole = false, ThinkingJobKind? kind = null,
        bool preemptible = false, int? priority = null)
    {
        this.owner = owner;
        Place = place;
        Holder = holder;
        Whole = whole;
        Kind = kind;
        Priority = priority;
        if (preemptible) stopping = new();
    }

    public BackgroundPlace Place { get; }
    /// <summary>The Thinking pool job kind that holds it, when it was asked for with a <see cref="ThinkingDemand"/>.</summary>
    public ThinkingJobKind? Kind { get; }
    /// <summary>The priority of the Thinking pool demand that holds it (<see cref="ThinkingDemand.Priority"/>), else null.</summary>
    public int? Priority { get; }
    /// <summary>Who holds it: a job ID such as think-2.</summary>
    public string Holder { get; }
    /// <summary>Whether it holds the whole place (every slot), such as a song being made on that computer.</summary>
    public bool Whole { get; }
    public bool Released => Volatile.Read(ref released) != 0;
    /// <summary>Whether the live floor may stop the work that holds it.</summary>
    public bool Preemptible => stopping is not null;
    /// <summary>Canceled when the live conversation needs the place: the holder stops its work, keeps what it has, disposes the
    /// lease and goes on later. Never canceled for a hold that isn't <see cref="Preemptible"/>.</summary>
    public CancellationToken Stopping => stopping?.Token ?? CancellationToken.None;
    /// <summary>Whether the live floor asked the holder to stop.</summary>
    public bool StopRequested => Volatile.Read(ref stopped) != 0;
    /// <summary>Whether higher-priority work stopped it (<see cref="ThinkingDemand.PreemptsLower"/>), not the live floor.</summary>
    public bool StoppedForPriority => Volatile.Read(ref forPriority) != 0;

    // Marks the hold stopped (under the broker's gate); false when it can't be or already was.
    internal bool MarkStopped() => stopping is not null && !Released && Interlocked.Exchange(ref stopped, 1) == 0;

    // Marks the hold stopped for higher-priority work (under the broker's gate).
    internal bool MarkStoppedForPriority()
    {
        if (!MarkStopped()) return false;
        Volatile.Write(ref forPriority, 1);
        return true;
    }

    // Asks the holder to stop; its callbacks run off this thread, so the live turn never waits for them.
    internal void Stop() => _ = stopping!.CancelAsync();

    public void Dispose()
    {
        if (Interlocked.Exchange(ref released, 1) == 0) owner.Release(this);
    }

    public override string ToString() => $"{nameof(BackgroundPlaceLease)} {Holder} on {Place.Name}";
}

/// <summary>Rules a <see cref="BackgroundPlaces"/> follows besides its free slots: the live floor's (<see cref="LiveFloorRules"/>).
/// Called under the broker's lock, so they must be quick and must not call back into the broker.</summary>
public interface IPlaceRules
{
    /// <summary>Whether work of <paramref name="kind"/> (null: not Thinking pool work) may start on <paramref name="place"/> now.</summary>
    bool MayStart(BackgroundPlace place, ThinkingJobKind? kind);

    /// <summary>Whether work of <paramref name="kind"/> running on <paramref name="place"/> must stop now.</summary>
    bool MustStop(BackgroundPlace place, ThinkingJobKind? kind);

    /// <summary>Whether <paramref name="place"/> goes last among free places now (it shares the live conversation's hardware
    /// while the conversation isn't idle).</summary>
    bool Avoid(BackgroundPlace place);

    /// <summary>Whether <paramref name="place"/> shares hardware with the live conversation, whatever the floor's level.</summary>
    bool Shares(BackgroundPlace place);

    /// <summary>Work of <paramref name="kind"/> (by <paramref name="holder"/>) waits only because of these rules.</summary>
    void Held(string holder, ThinkingJobKind kind);

    /// <summary>These rules stopped the work that holds <paramref name="lease"/>.</summary>
    void Stopped(BackgroundPlaceLease lease);
}

/// <summary>Martlet's request broker for background work (one per <see cref="BackgroundJobs"/>, <see cref="BackgroundJobs.Places"/>):
/// which places are busy with what, and who waits for one. A kind asks for a free place from its pool (<see cref="TryAcquire"/>,
/// or <see cref="AcquireAsync"/> to wait in line) and releases it when done; other work its computer does (a song) holds the
/// place for a while (<see cref="Hold"/>) so nothing else lands there. Every choice is deterministic and instant (no model is
/// asked): a free slot on the place with the lowest <see cref="BackgroundPlace.Standing"/>, then the least busy, then the pool's
/// order. Waiters are served highest priority first (<see cref="ThinkingDemand.Priority"/>), then first come, first served, each
/// as soon as a place of its own pool frees up. A demand with <see cref="ThinkingDemand.KeepLastFree"/> never takes the last free
/// slot of its pool while that pool has two or more slots (the Thinking pool's slot for fast jobs). A place whose computer doesn't
/// answer now (<see cref="Reachable"/>) gets no new work. The <see cref="Rules"/> (the live floor's) decide besides: what may start where, which places go last, and which preemptible work stops when the live
/// conversation needs its place (<see cref="Reconsider"/>). Thread-safe.</summary>
public sealed class BackgroundPlaces
{
    private sealed record Waiter(IReadOnlyList<BackgroundPlace> Pool, string Holder, TaskCompletionSource<BackgroundPlaceLease> Done,
        ThinkingDemand? Demand, long Order, bool Preemptible, bool Front = false);

    private long order;
    // The holders whose work higher-priority work stopped: their next request waits at the front of the line for its priority.
    private readonly HashSet<string> stoppedForPriority = new(StringComparer.Ordinal);

    private readonly object gate = new();
    private readonly List<BackgroundPlaceLease> leases = [];
    private readonly List<Waiter> waiters = [];
    private IPlaceRules? rules;

    /// <summary>Raised on any thread when a place is taken or released, or the line changes.</summary>
    public event Action? Changed;

    /// <summary>Rules besides free slots (the live floor's, <see cref="LiveFloorRules"/>); null: free slots only. Setting them
    /// applies them at once (<see cref="Reconsider"/>).</summary>
    public IPlaceRules? Rules
    {
        get => Volatile.Read(ref rules);
        set
        {
            Volatile.Write(ref rules, value);
            Reconsider();
        }
    }

    private Func<BackgroundPlace, bool>? reachable;

    /// <summary>Whether a place's computer answers now (the desktop's host checks); null: every place does. Work is never placed
    /// on a place that doesn't answer, and its slots don't count as free for the last-free-slot rule; work already running there
    /// carries on until it ends by itself. Called under the broker's lock, so it must be quick and must not call back into the
    /// broker. Setting it applies it at once; call <see cref="Reconsider"/> when a computer answers again, so work waiting in line
    /// starts there.</summary>
    public Func<BackgroundPlace, bool>? Reachable
    {
        get => Volatile.Read(ref reachable);
        set
        {
            Volatile.Write(ref reachable, value);
            Reconsider();
        }
    }

    /// <summary>Whether <paramref name="place"/>'s computer answers now (<see cref="Reachable"/>).</summary>
    public bool Answers(BackgroundPlace place) => Reachable?.Invoke(place) ?? true;

    /// <summary>Every place held now, oldest first.</summary>
    public IReadOnlyList<BackgroundPlaceLease> Leases { get { lock (gate) return [.. leases]; } }

    /// <summary>Who waits for a place now, first in line first.</summary>
    public IReadOnlyList<string> Line { get { lock (gate) return [.. Ordered().Select(w => w.Holder)]; } }

    /// <summary>The Thinking pool job kinds waiting now, first in line first.</summary>
    public IReadOnlyList<ThinkingJobKind> WaitingKinds
    {
        get { lock (gate) return [.. Ordered().Where(w => w.Demand is not null).Select(w => w.Demand!.Kind)]; }
    }

    /// <summary>The Thinking pool job kinds that wait only because of the <see cref="Rules"/> (a place of their pool is free,
    /// but the live conversation needs it): waiting for the conversation.</summary>
    public IReadOnlyList<ThinkingJobKind> HeldKinds
    {
        get { lock (gate) return [.. Ordered().Where(w => w.Demand is not null && HeldByRules(w.Pool, w.Demand)).Select(w => w.Demand!.Kind)]; }
    }

    // Highest priority first, then work stopped for higher-priority work (the latest stopped first), then first come. Called
    // under the gate.
    private IEnumerable<Waiter> Ordered() => waiters.OrderByDescending(w => w.Demand?.Priority ?? 0).ThenBy(w => w.Front ? 0 : 1)
        .ThenBy(w => w.Front ? -w.Order : w.Order);

    /// <summary>Whether <paramref name="holder"/> waits in line only because of the <see cref="Rules"/>: a place of its pool is
    /// free, but the live conversation needs it (waiting for the conversation).</summary>
    public bool HeldForConversation(string holder)
    {
        lock (gate) return waiters.FirstOrDefault(w => w.Holder == holder) is { Demand: { } demand } waiter && HeldByRules(waiter.Pool, demand);
    }

    /// <summary>Whether work of <paramref name="demand"/> on <paramref name="pool"/> would wait now only because of the
    /// <see cref="Rules"/> (a place is free, but the live conversation needs it).</summary>
    public bool HeldForConversation(IReadOnlyList<BackgroundPlace> pool, ThinkingDemand? demand)
    {
        ArgumentNullException.ThrowIfNull(pool);
        if (demand is null) return false;
        lock (gate) return HeldByRules(pool, demand);
    }

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
    /// <paramref name="pool"/> is empty). <paramref name="preemptible"/>: the live floor may stop the work
    /// (<see cref="BackgroundPlaceLease.Stopping"/>).</summary>
    public BackgroundPlaceLease? TryAcquire(IReadOnlyList<BackgroundPlace> pool, string holder, bool share = false, ThinkingDemand? demand = null,
        bool preemptible = false)
    {
        var lease = Acquire(pool, holder, share, demand, preemptible);
        if (lease is not null) Changed?.Invoke();
        return lease;
    }

    /// <summary>Takes the best free place in <paramref name="pool"/> for <paramref name="holder"/>, waiting in line until one
    /// frees up (and the <see cref="Rules"/> allow it) when all are busy. Canceling <paramref name="token"/> leaves the line.
    /// <paramref name="preemptible"/>: the live floor may stop the work (<see cref="BackgroundPlaceLease.Stopping"/>).</summary>
    public Task<BackgroundPlaceLease> AcquireAsync(IReadOnlyList<BackgroundPlace> pool, string holder, CancellationToken token,
        ThinkingDemand? demand = null, bool preemptible = false)
    {
        Check(pool, holder);
        ContractRules.Require(pool.Count > 0, "A background job's pool has at least one place.");
        token.ThrowIfCancellationRequested();
        Waiter waiter;
        BackgroundPlaceLease? now = null;
        var held = false;
        List<BackgroundPlaceLease> stopping = [];
        lock (gate)
        {
            var front = stoppedForPriority.Remove(holder) || demand is { Front: true };
            // Nobody earlier in line can use a free place (they'd have taken it), so a free one here is this holder's.
            if (Choose(pool, share: false, demand) is { } free)
            {
                now = new BackgroundPlaceLease(this, free, holder, kind: demand?.Kind, preemptible: preemptible, priority: demand?.Priority);
                leases.Add(now);
            }
            waiter = new(pool, holder, new(TaskCreationOptions.RunContinuationsAsynchronously), demand, ++order, preemptible, front);
            if (now is null)
            {
                waiters.Add(waiter);
                held = demand is not null && HeldByRules(pool, demand);
                PreemptForPriority(stopping);
            }
        }
        foreach (var lease in stopping) lease.Stop();
        if (held) Rules?.Held(holder, demand!.Kind);
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

    /// <summary>Applies the <see cref="Rules"/> again (the live floor changed): work they now stop has its lease's
    /// <see cref="BackgroundPlaceLease.Stopping"/> canceled (its place stays taken until the work let it go), and work waiting in
    /// line that may start now takes a free place.</summary>
    public void Reconsider()
    {
        var current = Rules;
        List<BackgroundPlaceLease> stopping = [];
        List<(Waiter Waiter, BackgroundPlaceLease Lease)> served = [];
        List<(string Holder, ThinkingJobKind Kind)> held = [];
        lock (gate)
        {
            if (current is not null)
                foreach (var lease in leases)
                    if (lease.Preemptible && !lease.StopRequested && current.MustStop(lease.Place, lease.Kind) && lease.MarkStopped())
                        stopping.Add(lease);
            Serve(served);
            PreemptForPriority(stopping);
            if (current is not null)
                held.AddRange(waiters.Where(w => w.Demand is not null && HeldByRules(w.Pool, w.Demand)).Select(w => (w.Holder, w.Demand!.Kind)));
        }
        foreach (var lease in stopping)
        {
            lease.Stop();
            if (!lease.StoppedForPriority) current!.Stopped(lease);
        }
        foreach (var (holder, kind) in held) current!.Held(holder, kind);
        foreach (var (waiter, next) in served)
            if (!waiter.Done.TrySetResult(next)) next.Dispose();
        Changed?.Invoke();
    }

    // Without raising Changed (the job list raises its own once the job is in its list).
    internal BackgroundPlaceLease? Acquire(IReadOnlyList<BackgroundPlace> pool, string holder, bool share, ThinkingDemand? demand = null,
        bool preemptible = false)
    {
        Check(pool, holder);
        lock (gate)
        {
            var choice = Choose(pool, share, demand);
            if (choice is null) return null;
            var lease = new BackgroundPlaceLease(this, choice, holder, kind: demand?.Kind, preemptible: preemptible, priority: demand?.Priority);
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

    // The deterministic choice: a free slot the rules allow on a place that answers, on a place the rules don't avoid first, then
    // the lowest standing, then the least busy, then the pool's order; with share and none free, the least busy place by its
    // share of slots (a whole hold last). Called under the gate.
    private BackgroundPlace? Choose(IReadOnlyList<BackgroundPlace> pool, bool share, ThinkingDemand? demand = null, bool obey = true)
    {
        var whole = demand is { KeepLastFree: true } ? demand.Pool ?? pool : null;
        var current = obey ? Rules : null;
        var candidates = pool.Select((place, order) => (place, order, used: Used(place)))
            .Where(c => Answers(c.place) && (current is null || current.MayStart(c.place, demand?.Kind)) &&
                (whole is null || LeavesFastSlot(whole, c.place))).ToArray();
        var free = candidates.Where(c => c.used < c.place.Slots)
            .OrderBy(c => current?.Avoid(c.place) == true ? 1 : 0)
            .ThenBy(c => c.place.Standing).ThenBy(c => c.used).ThenBy(c => c.order).Select(c => c.place).FirstOrDefault();
        if (free is not null || !share) return free;
        return candidates.OrderBy(c => leases.Any(l => l.Whole && l.Place.Id == c.place.Id))
            .ThenBy(c => (double)c.used / c.place.Slots).ThenBy(c => c.place.Standing).ThenBy(c => c.order)
            .Select(c => c.place).FirstOrDefault();
    }

    // Whether a demand waits only because of the rules: without them it would have a free place now. Called under the gate.
    private bool HeldByRules(IReadOnlyList<BackgroundPlace> pool, ThinkingDemand demand) =>
        Rules is not null && Choose(pool, share: false, demand) is null && Choose(pool, share: false, demand, obey: false) is not null;

    // Whether a long job may take a slot on place, one of whole: always on a place that takes no quick jobs, always with one slot
    // in all, else only while two or more slots that take quick jobs are free (one stays free for fast jobs). Only places that
    // answer count: an offline place's slots are neither there nor free. Called under the gate.
    private bool LeavesFastSlot(IReadOnlyList<BackgroundPlace> whole, BackgroundPlace place)
    {
        if (!place.QuickJobs) return true;
        var distinct = whole.DistinctBy(p => p.Id).Where(Answers).ToArray();
        if (distinct.Sum(p => p.Slots) < 2) return true;
        return distinct.Where(p => p.QuickJobs).Sum(p => Math.Max(0, p.Slots - Used(p))) >= 2;
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
        List<BackgroundPlaceLease> stopping = [];
        lock (gate)
        {
            removed = leases.Remove(lease);
            if (removed)
            {
                Serve(served);
                PreemptForPriority(stopping);
            }
        }
        foreach (var next in stopping) next.Stop();
        foreach (var (waiter, next) in served)
            if (!waiter.Done.TrySetResult(next)) next.Dispose();
        if (removed) Changed?.Invoke();
    }

    // Highest priority first, then first come: each waiter in turn takes a free place of its own pool, if one is free now and the
    // rules allow it. Called under the gate.
    private void Serve(List<(Waiter Waiter, BackgroundPlaceLease Lease)> served)
    {
        foreach (var waiter in Ordered().ToArray())
        {
            if (Choose(waiter.Pool, share: false, waiter.Demand) is not { } free) continue;
            var next = new BackgroundPlaceLease(this, free, waiter.Holder, kind: waiter.Demand?.Kind, preemptible: waiter.Preemptible,
                priority: waiter.Demand?.Priority);
            leases.Add(next);
            served.Add((waiter, next));
            waiters.Remove(waiter);
        }
    }

    // Higher priority first: each waiter whose demand preempts lower work (ThinkingDemand.PreemptsLower) and that has no free
    // place stops one running preemptible Thinking pool job of lower priority on its pool, when that job's slot would let it
    // start. A hold already stopping on its pool counts as its coming slot, so one waiter never stops two jobs. The stopped
    // holders' next requests wait at the front of the line for their priority. Called under the gate; the caller stops the
    // returned leases outside it.
    private void PreemptForPriority(List<BackgroundPlaceLease> stopping)
    {
        HashSet<BackgroundPlaceLease> claimed = [];
        foreach (var waiter in Ordered().ToArray())
        {
            if (waiter.Demand is not { PreemptsLower: true } demand || Choose(waiter.Pool, share: false, demand) is not null) continue;
            bool InPool(BackgroundPlaceLease lease) => waiter.Pool.Any(place => place.Id == lease.Place.Id);
            if (leases.FirstOrDefault(lease => lease.StopRequested && !lease.Released && !claimed.Contains(lease) && InPool(lease)) is { } coming)
            {
                claimed.Add(coming);
                continue;
            }
            var victims = leases.Select((lease, index) => (lease, index))
                .Where(c => c.lease is { Preemptible: true, StopRequested: false, Released: false, Whole: false, Kind: not null, Priority: { } p } &&
                    p < demand.Priority && InPool(c.lease))
                .OrderBy(c => c.lease.Priority).ThenByDescending(c => c.index).ToArray();
            foreach (var (victim, index) in victims)
            {
                leases.RemoveAt(index);
                var opens = Choose(waiter.Pool, share: false, demand) is not null;
                leases.Insert(index, victim);
                if (!opens || !victim.MarkStoppedForPriority()) continue;
                stoppedForPriority.Add(victim.Holder);
                claimed.Add(victim);
                stopping.Add(victim);
                break;
            }
        }
    }
    public override string ToString() => nameof(BackgroundPlaces);
}
