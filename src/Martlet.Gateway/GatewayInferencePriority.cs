namespace Martlet.Gateway;

/// <summary>One preemption or refusal of pool work: when, which route, its graphics cards and what held them.</summary>
internal sealed record GatewayPriorityEvent(DateTimeOffset At, string RouteId, IReadOnlyList<string> Gpus, string By);

/// <summary>One route in <see cref="GatewayPrioritySnapshot"/>: its lane and graphics cards, its running requests and, for a
/// pool route, whether a request there would be turned away now (<see cref="Held"/>; null for a live route).</summary>
internal sealed record GatewayPriorityRoute(string RouteId, string Name, GatewayLane Lane, IReadOnlyList<string> Gpus, int Running,
    bool? Held);

/// <summary>One graphics card (or "cpu") in <see cref="GatewayPrioritySnapshot"/>: held while a live request runs on it or a
/// hold keeps it, with how many live and pool requests run on it and how many holds keep it.</summary>
internal sealed record GatewayPriorityGpu(string Id, bool Held, int Live, int Pool, int Holds);

/// <summary>One client's hold: who holds it, the graphics cards (empty: the whole host) and until when.</summary>
internal sealed record GatewayPriorityHold(string Holder, IReadOnlyList<string> Gpus, DateTimeOffset Until);

/// <summary>What GPU priority does on this host now (GET /martlet/v1/priority): the GPU map, each card's hold state, the holds,
/// the last preemptions and refusals with their counts since the gateway started, and placement warnings.</summary>
internal sealed record GatewayPrioritySnapshot(IReadOnlyList<GatewayPriorityRoute> Routes, IReadOnlyList<GatewayPriorityGpu> Gpus,
    bool WholeHostHeld, IReadOnlyList<GatewayPriorityHold> Holds, long Preempted, long Refused,
    IReadOnlyList<GatewayPriorityEvent> LastPreemptions, IReadOnlyList<GatewayPriorityEvent> LastRefusals,
    IReadOnlyList<string> Warnings);

/// <summary>Live turn first, then the owner first. Windows has no priority between programs on one graphics card, so the
/// gateway keeps it: a card is held while a live-lane request runs on it and while a client's hold (POST
/// /martlet/v1/priority/hold) keeps it. While a card is held, a new pool-lane request that uses it is turned away (job.busy,
/// detail "live") and a running one stops at once (job.preempted): its worker aborts the request, and Ollama stops within one
/// token or prompt batch. Holds never touch live requests. A friend's request (<see cref="GatewayAccess.Friend"/>) ranks below
/// all of the owner's work, whatever its lane: any request of the owner stops it, and it is turned away (detail "owner") while
/// the owner's work or a hold keeps one of its cards. A route whose placement is unknown uses the whole host.</summary>
public sealed partial class GatewayInferenceRouteRegistry
{
    /// <summary>The failure detail of a pool request turned away while a live turn holds its graphics card.</summary>
    internal const string PriorityDetail = "live";
    /// <summary>The failure detail of a friend's request turned away while the owner's work keeps its graphics card or worker.</summary>
    internal const string OwnerDetail = "owner";
    /// <summary>The failure detail of a hold refused because this host already keeps <see cref="MaximumHolds"/>.</summary>
    internal const string HoldsDetail = "holds";
    /// <summary>Clients holding at once; each client has one hold, which a new call renews.</summary>
    internal const int MaximumHolds = 32;
    internal const int MaximumHoldRoutes = 16;
    internal static readonly TimeSpan MaximumHoldDuration = TimeSpan.FromSeconds(15);
    private const int RecentEvents = 16;
    private readonly Dictionary<string, (string Holder, string[] Gpus, DateTimeOffset Until)> holds = new(StringComparer.Ordinal);
    private readonly Queue<GatewayPriorityEvent> preemptions = new();
    private readonly Queue<GatewayPriorityEvent> refusals = new();
    private long preemptionCount;
    private long refusalCount;

    /// <summary>Lines for this host's own log (the message and its repeat key): preemptions and refusals. Set by the gateway.</summary>
    internal Action<string, string>? Activity { get; set; }

    /// <summary>Holds the graphics cards behind <paramref name="routeIds"/> for <paramref name="principal"/>'s client until now
    /// plus <paramref name="ttl"/> (at most 15 s), replacing its earlier hold, and stops pool work running on them. Returns the
    /// cards (empty: the whole host, when a route's placement is unknown or the route isn't on this host) and the end.</summary>
    internal (IReadOnlyList<string> Gpus, DateTimeOffset Until) Hold(GatewayPrincipal principal, IReadOnlyList<string> routeIds,
        TimeSpan ttl) => principal.WithAuthority(() => HoldAuthorized(principal, routeIds, ttl));

    /// <summary>Ends <paramref name="principal"/>'s client's hold; false when it had none.</summary>
    internal bool Release(GatewayPrincipal principal) => principal.WithAuthority(() =>
    {
        lock (gate) return holds.Remove(principal.CredentialId);
    });

    private (IReadOnlyList<string> Gpus, DateTimeOffset Until) HoldAuthorized(GatewayPrincipal principal,
        IReadOnlyList<string> routeIds, TimeSpan ttl)
    {
        ArgumentNullException.ThrowIfNull(routeIds);
        GatewayRules.Require(routeIds.Count is > 0 and <= MaximumHoldRoutes && ttl > TimeSpan.Zero && ttl <= MaximumHoldDuration,
            "request.invalid");
        var devices = HoldDevices(routeIds);
        var by = $"a live turn of {principal.Caller}";
        List<GatewayInferenceJob> stopped;
        DateTimeOffset until;
        lock (gate)
        {
            GatewayRules.Require(!closed, "worker.unavailable");
            var now = clock.GetUtcNow();
            foreach (var expired in holds.Where(h => h.Value.Until <= now).Select(h => h.Key).ToArray())
                holds.Remove(expired);
            if (!holds.ContainsKey(principal.CredentialId) && holds.Count >= MaximumHolds)
                throw new GatewayProtocolException("job.busy", HoldsDetail);
            until = now + ttl;
            holds[principal.CredentialId] = (principal.Caller, devices, until);
            stopped = PreemptLocked(devices, by, now, LiveRank);
        }
        Stop(stopped, by);
        return (devices, until);
    }

    // The cards behind the routes, together; the whole host (empty) when a route's placement is unknown or it isn't on this host.
    private string[] HoldDevices(IReadOnlyList<string> routeIds)
    {
        HashSet<string> devices = new(StringComparer.Ordinal);
        foreach (var id in routeIds)
        {
            GatewayRules.Identifier(id, 96);
            if (!byId.TryGetValue(id, out var registration) || registration.Route.Gpus.Count == 0) return [];
            devices.UnionWith(registration.Route.Gpus);
        }
        return [.. devices.Order(StringComparer.Ordinal)];
    }

    private const int LiveRank = 2;

    // Who comes first on a graphics card: the owner's live requests (2), then the owner's pool work (1), then a friend's requests
    // (0), whatever their route's lane. A request stops running work of a lower rank on its cards, and is turned away while work of
    // a higher rank, or a hold, keeps them.
    private static int Rank(GatewayPrincipal principal, GatewayInferenceRoute route) =>
        principal.Access == GatewayAccess.Friend ? 0 : route.Lane == GatewayLane.Pool ? 1 : LiveRank;

    private static int Rank(GatewayInferenceJob job) => Rank(job.Principal, job.Registration.Route);

    // What keeps a card of `devices` from work of `rank` now, in words: running work of a higher rank or an unexpired hold; null
    // when nothing does.
    private string? HolderLocked(IReadOnlyList<string> devices, DateTimeOffset now, int rank = 1)
    {
        foreach (var running in activeJobs.Values)
            if (Rank(running) > rank && GatewayGpus.Overlap(running.Registration.Route.Gpus, devices))
                return $"a {GatewayGpus.RouteName(running.Registration.Route)} request";
        foreach (var hold in holds.Values)
            if (hold.Until > now && GatewayGpus.Overlap(hold.Gpus, devices))
                return $"a live turn of {hold.Holder}";
        return null;
    }

    // Records a request on `route` turned away because higher-ranked work or a hold keeps its cards, and returns the log line;
    // null when it may run (the owner's live requests always may).
    private string? RefuseLocked(GatewayInferenceRoute route, GatewayPrincipal principal, DateTimeOffset now)
    {
        var rank = Rank(principal, route);
        if (rank >= LiveRank || HolderLocked(route.Gpus, now, rank) is not { } holder) return null;
        refusalCount++;
        Remember(refusals, new(now, route.RouteId, route.Gpus.ToArray(), holder));
        return rank == 0
            ? $"Owner first: turned away a {GatewayGpus.RouteName(route)} request from {principal.Caller} on " +
                $"{GatewayGpus.Describe(route.Gpus)} while {holder} of the owner uses it."
            : $"Live turn first: turned away a {GatewayGpus.RouteName(route)} request from {principal.Caller} on " +
                $"{GatewayGpus.Describe(route.Gpus)} while {holder} holds it.";
    }

    // Marks each running job of a lower rank than `rank` that shares a card of `devices` as preempted and returns them, to stop
    // outside the lock.
    private List<GatewayInferenceJob> PreemptLocked(IReadOnlyList<string> devices, string by, DateTimeOffset now, int rank) =>
        MarkPreemptedLocked(activeJobs.Values.Where(running => Rank(running) < rank &&
            GatewayGpus.Overlap(running.Registration.Route.Gpus, devices)), by, now);

    private List<GatewayInferenceJob> MarkPreemptedLocked(IEnumerable<GatewayInferenceJob> jobs, string by, DateTimeOffset now)
    {
        List<GatewayInferenceJob> stopped = [];
        foreach (var running in jobs)
        {
            if (running.Preempted) continue;
            var route = running.Registration.Route;
            running.MarkPreempted();
            stopped.Add(running);
            preemptionCount++;
            Remember(preemptions, new(now, route.RouteId, route.Gpus.ToArray(), by));
        }
        return stopped;
    }

    // Cancels preempted jobs: each ends with job.preempted and its worker aborts the request.
    private void Stop(List<GatewayInferenceJob> stopped, string by)
    {
        if (stopped.Count == 0) return;
        foreach (var running in stopped)
            _ = running.CancelAsync().ContinueWith(static task => _ = task.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        var route = stopped[0].Registration.Route;
        var friends = stopped.All(s => s.Principal.Access == GatewayAccess.Friend);
        Report($"{(friends ? "Owner first" : "Live turn first")}: stopped {stopped.Count} {GatewayGpus.RouteName(route)} " +
            $"request{(stopped.Count == 1 ? "" : "s")}{(friends ? " of a friend" : "")} on {GatewayGpus.Describe(route.Gpus)} for {by}; " +
            $"{(friends ? "the friend's computer" : "the desktop")} runs {(stopped.Count == 1 ? "it" : "them")} elsewhere or later.",
            "priority.preempted");
    }

    private void Report(string message, string repeatKey)
    {
        try { Activity?.Invoke(message, repeatKey); }
        catch (Exception) { } // a log failure never changes admission
    }

    private static void Remember(Queue<GatewayPriorityEvent> events, GatewayPriorityEvent item)
    {
        events.Enqueue(item);
        while (events.Count > RecentEvents) events.Dequeue();
    }

    /// <summary>What GPU priority does now (see <see cref="GatewayPrioritySnapshot"/>).</summary>
    internal GatewayPrioritySnapshot Priority()
    {
        lock (gate)
        {
            var now = clock.GetUtcNow();
            var live = holds.Where(h => h.Value.Until > now).ToArray();
            // A friend's request never holds a card; it counts with the pool work any of the owner's requests stops.
            var owners = activeJobs.Values.Where(j => j.Principal.Access != GatewayAccess.Friend).Select(j => j.Registration.Route).ToArray();
            var running = activeJobs.Values.Select(j => j.Registration.Route).ToArray();
            var routes = byId.Values.Select(r => r.Route).OrderBy(r => r.RouteId, StringComparer.Ordinal).ToArray();
            var devices = routes.SelectMany(r => r.Gpus).Concat(live.SelectMany(h => h.Value.Gpus))
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            var gpus = devices.Select(device =>
            {
                string[] one = [device];
                var liveCount = owners.Count(r => r.Lane == GatewayLane.Live && GatewayGpus.Overlap(r.Gpus, one));
                var holdCount = live.Count(h => GatewayGpus.Overlap(h.Value.Gpus, one));
                var all = running.Count(r => GatewayGpus.Overlap(r.Gpus, one));
                return new GatewayPriorityGpu(device, liveCount > 0 || holdCount > 0, liveCount, all - liveCount, holdCount);
            }).ToArray();
            return new(
                routes.Select(r => new GatewayPriorityRoute(r.RouteId, GatewayGpus.RouteName(r), r.Lane, r.Gpus.ToArray(),
                    running.Count(x => ReferenceEquals(x, r)), r.Lane == GatewayLane.Pool ? HolderLocked(r.Gpus, now) is not null : null)).ToArray(),
                gpus,
                owners.Any(r => r.Lane == GatewayLane.Live && r.Gpus.Count == 0) || live.Any(h => h.Value.Gpus.Length == 0),
                live.Select(h => new GatewayPriorityHold(h.Value.Holder, h.Value.Gpus, h.Value.Until)).ToArray(),
                preemptionCount, refusalCount, preemptions.Reverse().ToArray(), refusals.Reverse().ToArray(),
                GatewayGpus.Warnings(routes));
        }
    }

    /// <summary>The registered routes, for the GPU map the gateway logs when it starts.</summary>
    internal IReadOnlyList<GatewayInferenceRoute> Routes => byId.Values.Select(r => r.Route)
        .OrderBy(r => r.RouteId, StringComparer.Ordinal).ToArray();
}
