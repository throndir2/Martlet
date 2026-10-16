namespace Martlet.Gateway;

/// <summary>A worker whose engine also answers the OpenAI-compatible API for the one model it runs: a host's own loopback
/// Ollama (docs/OPENAI_ENDPOINT.md). Nothing else is reachable through it, so a cloud provider or key never is.</summary>
public interface IGatewayOpenAiRelay
{
    /// <summary>The model name the endpoint lists and takes: the Ollama tag the role runs, for example <c>qwen3:8b</c>.</summary>
    string OpenAiModel { get; }

    GatewayInferenceRoute Route { get; }

    /// <summary>Runs one OpenAI-compatible request on <paramref name="path"/> (one of
    /// <see cref="Martlet.Core.Access.OpenAiEndpoint.RelayPaths"/>) whose <c>model</c> is <see cref="OpenAiModel"/>. The request
    /// is read before this returns; the answer's body streams as it is made and stops with <paramref name="cancellationToken"/>.</summary>
    Task<GatewayOpenAiAnswer> RunOpenAiAsync(string path, System.Text.Json.JsonElement request, CancellationToken cancellationToken);
}

/// <summary>An OpenAI-compatible answer: its HTTP status, media type (application/json, or text/event-stream while it
/// streams) and the body's pieces in order.</summary>
public sealed class GatewayOpenAiAnswer
{
    public required int Status { get; init; }
    public required string ContentType { get; init; }
    public required IAsyncEnumerable<ReadOnlyMemory<byte>> Body { get; init; }

    /// <summary>An answer of one JSON document.</summary>
    public static GatewayOpenAiAnswer Json(int status, byte[] body) => new()
    {
        Status = status, ContentType = "application/json", Body = One(body)
    };

    /// <summary>An OpenAI-format error: <c>{"error":{"message","type","param","code"}}</c>.</summary>
    public static GatewayOpenAiAnswer Error(int status, string message, string code, string? param = null) =>
        Json(status, ErrorBody(status, message, code, param));

    public static byte[] ErrorBody(int status, string message, string code, string? param = null) =>
        System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object?>
        {
            ["error"] = new Dictionary<string, object?>
            {
                ["message"] = message, ["type"] = ErrorType(status), ["param"] = param, ["code"] = code
            }
        });

    public static string ErrorType(int status) => status switch
    {
        401 => "authentication_error",
        403 => "permission_error",
        404 => "not_found_error",
        429 => "rate_limit_error",
        >= 500 => "server_error",
        _ => "invalid_request_error"
    };

    private static async IAsyncEnumerable<ReadOnlyMemory<byte>> One(byte[] body)
    {
        await Task.CompletedTask.ConfigureAwait(false);
        yield return body;
    }
}

/// <summary>Requests of the OpenAI-compatible endpoint on a route's worker. They share the worker's slots with the owner's
/// requests and take part in GPU priority (GatewayInferencePriority.cs) with a rank of their own: below all of the owner's work
/// (rank 0, like a friend's), or with the owner's background work (rank 1) for a key with the <c>equal-priority</c> scope. They
/// never stop other work; work of a higher rank stops them and turns them away. A request that finds no free slot may wait in
/// this host's queue (<see cref="EnqueueOutside"/>): it holds no slot while it waits, and gets one only when the owner's work
/// leaves it free. The owner's requests never look at the queue, so they never wait behind it.</summary>
public sealed partial class GatewayInferenceRouteRegistry
{
    private readonly List<GatewayOutsideJob> outsideJobs = [];
    private readonly List<GatewayOutsideWaiter> outsideWaiting = [];
    private long outsideOrder;
    /// <summary>How often a waiting request checks for a free slot when nothing tells it (a hold or the owner's work on
    /// another route of its graphics card ended).</summary>
    private static readonly TimeSpan OutsideTick = TimeSpan.FromMilliseconds(250);

    /// <summary>The routes whose worker answers the OpenAI-compatible API, with their model; pool routes first, since the owner's
    /// live turns use the live routes.</summary>
    internal IReadOnlyList<(string RouteId, IGatewayOpenAiRelay Relay)> OpenAiRelays() => byId.Values
        .Where(r => r.Worker is IGatewayOpenAiRelay)
        .OrderBy(r => r.Route.Lane == GatewayLane.Pool ? 0 : 1).ThenBy(r => r.Route.RouteId, StringComparer.Ordinal)
        .Select(r => (r.Route.RouteId, (IGatewayOpenAiRelay)r.Worker)).ToArray();

    /// <summary>How many OpenAI-compatible requests run on this host now.</summary>
    internal int OutsideRunning { get { lock (gate) return outsideJobs.Count; } }

    /// <summary>How many OpenAI-compatible requests wait for a free slot on this host now.</summary>
    internal int OutsideWaiting { get { lock (gate) return outsideWaiting.Count; } }

    /// <summary>Admits an OpenAI-compatible request of <paramref name="principal"/> on route <paramref name="routeId"/>: it takes
    /// one of the worker's slots until the returned job is disposed. Turned away (job.busy) while every slot is taken, while
    /// work of a higher rank or a live turn's hold keeps the route's graphics card, while a slot is kept for the owner's request,
    /// or while requests of the same or a higher rank wait in the queue for this route (they came first).</summary>
    internal GatewayOutsideJob BeginOutside(GatewayPrincipal principal, string routeId, bool equalPriority)
    {
        ArgumentNullException.ThrowIfNull(principal);
        principal.WithAuthority(() => true);
        if (!byId.TryGetValue(routeId, out var registration))
            throw new GatewayProtocolException("request.invalid");
        var rank = equalPriority ? 1 : 0;
        string? refused;
        GatewayOutsideJob? job = null;
        lock (gate)
        {
            GatewayRules.Require(!closed, "worker.unavailable");
            if (registration.Admission.Quarantined)
                throw new GatewayProtocolException("worker.quarantined");
            var now = clock.GetUtcNow();
            refused = RefuseLocked(registration.Route, principal, now, rank);
            if (refused is null)
            {
                // A slot the owner's request waits for is never given to a shared request, whatever its rank.
                if (registration.Admission.ReservedUntil > now)
                    throw new GatewayProtocolException("job.busy", OwnerDetail);
                if (registration.Admission.Active >= registration.Route.MaximumConcurrency ||
                    outsideWaiting.Any(w => w.Rank >= rank && !w.Away && w.Routes.Contains(registration)))
                    throw new GatewayProtocolException("job.busy");
                job = StartOutsideLocked(registration, principal, rank);
            }
        }
        if (refused is not null)
        {
            Report(refused, "priority.refused");
            throw new GatewayProtocolException("job.busy", rank == 0 ? OwnerDetail : PriorityDetail);
        }
        return job!;
    }

    private GatewayOutsideJob StartOutsideLocked(RouteRegistration registration, GatewayPrincipal principal, int rank)
    {
        registration.Admission.Active++;
        var job = new GatewayOutsideJob(this, registration, principal, rank);
        outsideJobs.Add(job);
        return job;
    }

    // Whether a request of `rank` may take a slot of `registration` now: not while work of a higher rank or a hold keeps one of
    // its graphics cards, a slot is kept for the owner's request, or every slot is taken. Records nothing.
    private bool FreeForOutsideLocked(RouteRegistration registration, int rank, DateTimeOffset now) =>
        !registration.Admission.Quarantined && registration.Admission.ReservedUntil <= now &&
        registration.Admission.Active < registration.Route.MaximumConcurrency && HolderLocked(registration.Route.Gpus, now, rank) is null;

    /// <summary>Puts an OpenAI-compatible request that found no free slot in this host's queue for the routes
    /// <paramref name="routeIds"/> (in the order it prefers them). Null when <paramref name="maximum"/> requests already wait
    /// here, or <paramref name="maximumPerKey"/> of the same key. Wait with <see cref="WaitOutsideAsync"/>; always end with
    /// <see cref="LeaveOutside"/>.</summary>
    internal GatewayOutsideWaiter? EnqueueOutside(GatewayPrincipal principal, IReadOnlyList<string> routeIds, bool equalPriority,
        int maximum, int maximumPerKey)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(routeIds);
        var routes = routeIds.Select(id => byId.TryGetValue(id, out var registration) ? registration
            : throw new GatewayProtocolException("request.invalid")).ToArray();
        GatewayRules.Require(routes.Length > 0, "request.invalid");
        lock (gate)
        {
            GatewayRules.Require(!closed, "worker.unavailable");
            if (outsideWaiting.Count >= maximum ||
                outsideWaiting.Count(w => w.Principal.CredentialId == principal.CredentialId) >= maximumPerKey)
                return null;
            var waiter = new GatewayOutsideWaiter(principal, routes, equalPriority ? 1 : 0, ++outsideOrder);
            outsideWaiting.Add(waiter);
            return waiter;
        }
    }

    /// <summary>Waits until <paramref name="waiter"/> gets a slot (its job, which now holds the slot) or for at most
    /// <paramref name="within"/> (null; it still waits in the queue). Higher ranks get a free slot first, then requests in the
    /// order they came. Throws when the key stops working, the caller hangs up or the gateway closes.</summary>
    internal async Task<GatewayOutsideJob?> WaitOutsideAsync(GatewayOutsideWaiter waiter, TimeSpan within, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(waiter);
        var started = clock.GetTimestamp();
        while (true)
        {
            List<GatewayOutsideWaiter> granted;
            GatewayOutsideJob? job = null;
            Task[] wake;
            lock (gate)
            {
                GatewayRules.Require(!closed, "worker.unavailable");
                if (waiter.Taken) return null;
                granted = PumpOutsideLocked(clock.GetUtcNow());
                if (waiter.Job is { } mine && !waiter.Taken)
                {
                    waiter.Taken = true;
                    job = mine;
                }
                wake = [.. waiter.Routes.Select(r => r.Admission.Freed.Task).Distinct(), waiter.Granted.Task];
            }
            Grant(granted);
            if (job is not null) return job;
            var remaining = within - clock.GetElapsedTime(started);
            if (remaining <= TimeSpan.Zero) return null;
            waiter.Principal.WithAuthority(() => true);
            using var tick = CancellationTokenSource.CreateLinkedTokenSource(token);
            await Task.WhenAny([.. wake, Task.Delay(remaining < OutsideTick ? remaining : OutsideTick, clock, tick.Token)]).ConfigureAwait(false);
            await tick.CancelAsync().ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
        }
    }

    /// <summary>Takes <paramref name="waiter"/> out of the queue. Returns the job it was given in the meantime and nobody took
    /// yet (dispose it, or run the request with it), else null.</summary>
    internal GatewayOutsideJob? LeaveOutside(GatewayOutsideWaiter waiter)
    {
        ArgumentNullException.ThrowIfNull(waiter);
        lock (gate)
        {
            outsideWaiting.Remove(waiter);
            if (waiter.Job is null || waiter.Taken) return null;
            waiter.Taken = true;
            return waiter.Job;
        }
    }

    /// <summary>Keeps <paramref name="waiter"/>'s place in the queue but gives it no slot (and lets later requests take free
    /// slots) while its handler asks other hosts, until <see cref="StepBack"/>. Returns the job it was given before (take it and
    /// don't step away), else null.</summary>
    internal GatewayOutsideJob? StepAway(GatewayOutsideWaiter waiter)
    {
        ArgumentNullException.ThrowIfNull(waiter);
        lock (gate)
        {
            if (waiter.Job is { } job && !waiter.Taken)
            {
                waiter.Taken = true;
                return job;
            }
            waiter.Away = true;
            return null;
        }
    }

    /// <summary>Lets <paramref name="waiter"/> get a slot again after <see cref="StepAway"/>.</summary>
    internal void StepBack(GatewayOutsideWaiter waiter)
    {
        ArgumentNullException.ThrowIfNull(waiter);
        lock (gate) waiter.Away = false;
    }

    // Gives free slots to waiting requests: a higher rank first, then in the order they came; each takes the first of its routes
    // that is free for its rank. A request whose handler asks other hosts now gets none. Returns the requests given a slot, to
    // wake outside the lock.
    private List<GatewayOutsideWaiter> PumpOutsideLocked(DateTimeOffset now)
    {
        List<GatewayOutsideWaiter> granted = [];
        if (outsideWaiting.Count == 0 || closed) return granted;
        foreach (var waiter in outsideWaiting.Where(w => !w.Away).OrderByDescending(w => w.Rank).ThenBy(w => w.Order).ToArray())
            foreach (var registration in waiter.Routes)
            {
                if (!FreeForOutsideLocked(registration, waiter.Rank, now)) continue;
                waiter.Job = StartOutsideLocked(registration, waiter.Principal, waiter.Rank);
                outsideWaiting.Remove(waiter);
                granted.Add(waiter);
                break;
            }
        return granted;
    }

    private static void Grant(List<GatewayOutsideWaiter> granted)
    {
        foreach (var waiter in granted) waiter.Granted.TrySetResult();
    }

    // Stops each OpenAI-compatible request of `jobs` for `by` (its own handler says so in the log).
    private void PreemptOutsideLocked(IEnumerable<GatewayOutsideJob> jobs, string by, DateTimeOffset now)
    {
        foreach (var outside in jobs)
        {
            if (outside.Preempted) continue;
            var route = outside.Registration.Route;
            outside.Preempt(by);
            preemptionCount++;
            Remember(preemptions, new(now, route.RouteId, route.Gpus.ToArray(), by));
        }
    }

    private void FinishOutside(GatewayOutsideJob job)
    {
        TaskCompletionSource freed;
        List<GatewayOutsideWaiter> granted;
        lock (gate)
        {
            if (!outsideJobs.Remove(job)) return;
            var admission = job.Registration.Admission;
            admission.Active--;
            freed = admission.Freed;
            admission.Freed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            // The next waiting request gets the slot now, unless it is kept for the owner's request that stopped this job.
            granted = PumpOutsideLocked(clock.GetUtcNow());
        }
        freed.TrySetResult();
        Grant(granted);
    }

    /// <summary>An OpenAI-compatible request waiting in this host's queue for a slot of one of <see cref="Routes"/>. It holds no
    /// slot until it is given one (<see cref="Job"/>, set under the registry's lock).</summary>
    internal sealed class GatewayOutsideWaiter(GatewayPrincipal principal, RouteRegistration[] routes, int rank, long order)
    {
        internal GatewayPrincipal Principal { get; } = principal;
        internal RouteRegistration[] Routes { get; } = routes;
        internal int Rank { get; } = rank;
        internal long Order { get; } = order;
        internal GatewayOutsideJob? Job { get; set; }
        internal bool Taken { get; set; }
        /// <summary>Its handler asks other hosts now: it keeps its place but gets no slot.</summary>
        internal bool Away { get; set; }
        internal TaskCompletionSource Granted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>One OpenAI-compatible request holding a slot of a route's worker. <see cref="Stopped"/> fires when work of a
    /// higher rank needs its graphics card or slot (<see cref="Preempted"/>), when the key is revoked, expires or loses the
    /// openai scope, or when the gateway closes. Dispose gives the slot back.</summary>
    internal sealed class GatewayOutsideJob : IDisposable
    {
        private readonly GatewayInferenceRouteRegistry owner;
        private readonly CancellationTokenSource stop = new();
        private readonly CancellationTokenSource monitorStop = new();
        private int disposed;

        internal GatewayOutsideJob(GatewayInferenceRouteRegistry owner, RouteRegistration registration, GatewayPrincipal principal, int rank)
        {
            this.owner = owner;
            Registration = registration;
            Principal = principal;
            Rank = rank;
            _ = MonitorAuthorityAsync();
        }

        internal RouteRegistration Registration { get; }
        internal GatewayPrincipal Principal { get; }
        internal int Rank { get; }
        internal CancellationToken Stopped => stop.Token;
        internal bool Preempted { get; private set; }
        /// <summary>What stopped it for priority, in words; null while it runs.</summary>
        internal string? PreemptedBy { get; private set; }
        /// <summary>Set when the key that started it stopped allowing it (revoked, expired or no longer openai).</summary>
        internal string? RevokedCode { get; private set; }

        internal void Preempt(string by)
        {
            Preempted = true;
            PreemptedBy = by;
            Stop();
        }

        // Cancellation callbacks run on the thread pool, never under the registry's lock.
        internal void Stop()
        {
            try
            {
                _ = stop.CancelAsync().ContinueWith(static task => _ = task.Exception, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
            catch (ObjectDisposedException) { } // it already ended
        }

        private async Task MonitorAuthorityAsync()
        {
            try
            {
                while (true)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(250), monitorStop.Token).ConfigureAwait(false);
                    Principal.WithAuthority(() => true);
                }
            }
            catch (OperationCanceledException) when (monitorStop.IsCancellationRequested) { }
            catch (ObjectDisposedException) { }
            catch (GatewayProtocolException error)
            {
                RevokedCode = error.Failure.Code;
                Stop();
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            monitorStop.Cancel();
            owner.FinishOutside(this);
            monitorStop.Dispose();
            stop.Dispose();
        }
    }
}
