using Martlet.Core.Contracts;

namespace Martlet.Conversation;

/// <summary>A computer (or provider) background work can run on: its stable <paramref name="Id"/> (such as <c>host:diva</c>),
/// the <paramref name="Name"/> the talk window, <c>background-jobs.json</c> and the desktop log show (the computer's name only,
/// such as "diva" or "this PC"), and its <paramref name="Rank"/>: lower goes first (0 does none of the conversation's jobs, 1
/// shares a computer or provider with the voice or listening, 2 with Thinking, 3 shares Thinking's graphics card on this PC).
/// Places of equal rank go in the order they were given.</summary>
public sealed record BackgroundPlace(string Id, string Name, int Rank = 0)
{
    public const int MaxRank = 9;

    public void Validate()
    {
        ContractRules.Require(Id is { Length: > 0 and <= 4096 } && !Id.Any(char.IsControl), "A background place needs a short ID.");
        ContractRules.Require(Name is { Length: > 0 and <= 80 } && !Name.Any(char.IsControl), "A background place needs a short name.");
        ContractRules.Require(Rank is >= 0 and <= MaxRank, "A background place's rank is 0-9.");
    }

    public override string ToString() => $"{nameof(BackgroundPlace)} {Name}";
}

/// <summary>One background job's (or one step's) hold on a <see cref="BackgroundPlace"/>, until it is disposed.</summary>
public sealed class BackgroundPlaceLease : IDisposable
{
    private readonly BackgroundPlaces owner;
    private int released;

    internal BackgroundPlaceLease(BackgroundPlaces owner, BackgroundPlace place, string holder)
    {
        this.owner = owner;
        Place = place;
        Holder = holder;
    }

    public BackgroundPlace Place { get; }
    /// <summary>Who holds it: a job ID such as think-2.</summary>
    public string Holder { get; }
    public bool Released => Volatile.Read(ref released) != 0;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref released, 1) == 0) owner.Release(this);
    }

    public override string ToString() => $"{nameof(BackgroundPlaceLease)} {Holder} on {Place.Name}";
}

/// <summary>Which places are busy with which background work (one per <see cref="BackgroundJobs"/>,
/// <see cref="BackgroundJobs.Places"/>): a kind asks for a free place from its pool (<see cref="TryAcquire"/>) and releases it
/// when done. <see cref="BackgroundJobs.Start(BackgroundJobKind, string, Func{BackgroundJob, CancellationToken, Task{BackgroundJobOutcome}}, IReadOnlyList{BackgroundPlace})"/>
/// does both for a whole job. Thread-safe.</summary>
public sealed class BackgroundPlaces
{
    private readonly object gate = new();
    private readonly List<BackgroundPlaceLease> leases = [];

    /// <summary>Raised on any thread when a place is taken or released.</summary>
    public event Action? Changed;

    /// <summary>Every place held now, oldest first.</summary>
    public IReadOnlyList<BackgroundPlaceLease> Leases { get { lock (gate) return [.. leases]; } }

    /// <summary>How many hold place <paramref name="id"/> now.</summary>
    public int Load(string id)
    {
        lock (gate) return leases.Count(lease => lease.Place.Id == id);
    }

    /// <summary>Takes the best free place in <paramref name="pool"/> for <paramref name="holder"/>: the lowest rank, then the
    /// pool's order. With <paramref name="share"/>, the least busy place when none is free (work that may wait on a busy
    /// place). Null when none (or <paramref name="pool"/> is empty).</summary>
    public BackgroundPlaceLease? TryAcquire(IReadOnlyList<BackgroundPlace> pool, string holder, bool share = false)
    {
        var lease = Acquire(pool, holder, share);
        if (lease is not null) Changed?.Invoke();
        return lease;
    }

    // Without raising Changed (the job list raises its own once the job is in its list).
    internal BackgroundPlaceLease? Acquire(IReadOnlyList<BackgroundPlace> pool, string holder, bool share)
    {
        ArgumentNullException.ThrowIfNull(pool);
        ContractRules.Require(!string.IsNullOrWhiteSpace(holder) && holder.Length <= 64, "A background place's holder needs a short name.");
        foreach (var place in pool) place.Validate();
        lock (gate)
        {
            var choice = pool.Select((place, order) => (place, order, load: leases.Count(lease => lease.Place.Id == place.Id)))
                .Where(c => share || c.load == 0)
                .OrderBy(c => c.load).ThenBy(c => c.place.Rank).ThenBy(c => c.order)
                .Select(c => c.place).FirstOrDefault();
            if (choice is null) return null;
            var lease = new BackgroundPlaceLease(this, choice, holder);
            leases.Add(lease);
            return lease;
        }
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
        lock (gate) removed = leases.Remove(lease);
        if (removed) Changed?.Invoke();
    }

    public override string ToString() => nameof(BackgroundPlaces);
}
