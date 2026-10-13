using Martlet.Core.Cluster;

namespace Martlet.Core.Motion;

/// <summary>
/// Body motions on a pool of the owner's own computers that run the <c>motion</c> role (HY-Motion), instead of one computer
/// (docs/MOTION_HOST.md#the-motion-pool). The pool is the <see cref="PoolAreas.Motion"/> list (pools.json, shared): its members
/// in the owner's order, each one on or off, kept for some companion PCs or for every one. With no member on, motion is off.
/// Before the list exists, the older choice (<see cref="MotionSettings"/>, motion.json) says where: automatic (every paired
/// computer the shared plan says runs the role), one computer, or off. A motion goes through Martlet's queue for shared work
/// (<see cref="WorkQueue"/>, lane <see cref="Lane"/>, background priority): the first computer that isn't making another
/// motion takes it; one that is busy is passed over for the next, one that doesn't answer or isn't set up too, and when every
/// one is busy the motion waits for the first to finish. A motion that fails on its computer is not made again elsewhere.
/// Hosts that friends share are never in the pool: the caller gives only the owner's own computers.
/// </summary>
public static class MotionPool
{
    /// <summary>The <see cref="WorkQueue"/> lane of motions: the motion pool area's ID.</summary>
    public static string Lane => PoolAreas.Motion.Id;

    /// <summary>How long a motion waits for a busy computer at most (the motion job's own limit is five minutes).</summary>
    public static readonly TimeSpan LongestWait = TimeSpan.FromMinutes(4);

    /// <summary>The motion pool of companion PC <paramref name="device"/>, each member with the paired computer it makes motions
    /// on, in the order a motion tries them. With a pool list (<paramref name="list"/>): its members that are on, kept for this PC
    /// or every one and paired here (<paramref name="paired"/>, without hosts friends share), in the owner's order; This PC is its
    /// own host service (<paramref name="own"/>). Without a list yet: the older choice <paramref name="settings"/> (one computer,
    /// even when it isn't paired here, so the motion says why; or every computer in <paramref name="plan"/> that runs the role,
    /// this PC's own first; or none when off). No network.</summary>
    public static IReadOnlyList<(PoolMember Member, string HostId)> Members(PoolList? list, MotionSettings settings, ClusterPlan plan,
        string device, string? own, IReadOnlyList<string> paired)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(paired);
        // A motion waits for a computer (its key in the queue is the host ID), so each computer is in the order once.
        if (list is not null)
            return [.. PoolPlaces.Order(PoolAreas.Motion, list, device, m => PoolPlaces.HostOf(m, own) is { } host &&
                paired.Contains(host, StringComparer.Ordinal) ? new PoolPlace(m, host, host) : null).Select(p => (p.Member, p.HostId!))];
        return settings.Place switch
        {
            MotionPlace.Off => [],
            MotionPlace.Host => settings.HostId is { } host ? [(Member(host, own), host)] : [],
            _ => [.. Runners(plan, paired, own).Select(host => (Member(host, own), host))]
        };
    }

    /// <summary>The computers that <paramref name="plan"/> says run the motion role, among <paramref name="paired"/> (null: every
    /// one, as the first shared list takes them, so each companion PC keeps the ones it is paired with): this PC's own host
    /// service (<paramref name="own"/>) first, then by ID.</summary>
    public static IReadOnlyList<string> Runners(ClusterPlan plan, IReadOnlyCollection<string>? paired, string? own)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return [.. plan.Nodes.Where(n => !n.Removed && n.Roles.Any(r => r.Kind == MotionSettings.RoleKind) && (paired is null || paired.Contains(n.HostId)))
            .Select(n => n.HostId).Distinct(StringComparer.Ordinal)
            .OrderBy(id => id == own ? 0 : 1).ThenBy(id => id, StringComparer.Ordinal)];
    }

    /// <summary>The first motion list, made once from the older choice so it isn't lost: one computer first, then the other
    /// computers that run the role (<paramref name="runners"/>: every one in the shared plan, not only the pairings of the PC
    /// that makes the list, because the list is shared and each companion PC uses the ones it is paired with); automatic: the
    /// computers that run the role; off: the computers that run the role, each turned off (an empty list when none does). Null
    /// for automatic while no computer runs the role: no list is made, so a computer that adds the role later still turns motion
    /// on. Members are computers by ID, as the list is shared and This PC means each companion PC's own host service.</summary>
    public static PoolList? FirstList(MotionSettings settings, IReadOnlyList<string> runners)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(runners);
        var hosts = settings.Place == MotionPlace.Host && settings.HostId is { } chosen ? runners.Prepend(chosen) : runners;
        var list = new PoolList { Area = PoolAreas.Motion.Id };
        foreach (var host in hosts.Distinct(StringComparer.Ordinal))
            list = list.With(PoolMember.Computer(host) with { Off = settings.Place == MotionPlace.Off });
        return settings.Place == MotionPlace.Automatic && list.Members.Count == 0 ? null : list;
    }

    /// <summary>What a failure before a motion is made means: <see cref="WorkRefusal.Busy"/> for a computer making another motion
    /// (<c>motion.busy</c>), <see cref="WorkRefusal.Unavailable"/> for one that can't make it now (not reachable, not set up,
    /// out of graphics memory, too slow to answer), else <see cref="WorkRefusal.None"/>: a real failure, not tried again
    /// elsewhere.</summary>
    public static WorkRefusal Classify(Exception error) => error switch
    {
        MotionException { Code: MotionErrorCodes.Busy } => WorkRefusal.Busy,
        MotionException { Code: MotionErrorCodes.Unavailable or MotionErrorCodes.OutOfMemory or MotionErrorCodes.TimedOut } => WorkRefusal.Unavailable,
        MotionException => WorkRefusal.None,
        HttpRequestException or IOException or TimeoutException => WorkRefusal.Unavailable,
        _ => WorkRefusal.None
    };

    /// <summary>Makes a motion on the first of <paramref name="hosts"/> that takes it (see the class summary) through
    /// <paramref name="queue"/> (<see cref="PoolRunner{TPlace}"/>), and returns it with the computer that made it.
    /// <paramref name="make"/> makes it on one computer. Throws <see cref="MotionException"/> (<c>motion.unavailable</c>) when the
    /// list is empty, else the last refusal when no computer took it.</summary>
    public static async Task<(T Result, string HostId)> RunAsync<T>(WorkQueue queue, IReadOnlyList<string> hosts,
        Func<string, CancellationToken, Task<T>> make, TimeProvider? clock, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(hosts);
        ArgumentNullException.ThrowIfNull(make);
        if (hosts.Count == 0) throw new MotionException(MotionErrorCodes.Unavailable, "No computer in the Motion list is on.");
        var run = await new PoolRunner<string>(Lane, host => host, Classify, WorkPriority.Background, queue, clock)
            .RunAsync(hosts, (host, _, t) => make(host, t), LongestWait, token).ConfigureAwait(false);
        return (run.Result, run.Place);
    }

    private static PoolMember Member(string host, string? own) => host == own ? PoolMember.ThisPc() : PoolMember.Computer(host);
}
