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
/// never stop other work; work of a higher rank stops them and turns them away.</summary>
public sealed partial class GatewayInferenceRouteRegistry
{
    private readonly List<GatewayOutsideJob> outsideJobs = [];

    /// <summary>The routes whose worker answers the OpenAI-compatible API, with their model; pool routes first, since the owner's
    /// live turns use the live routes.</summary>
    internal IReadOnlyList<(string RouteId, IGatewayOpenAiRelay Relay)> OpenAiRelays() => byId.Values
        .Where(r => r.Worker is IGatewayOpenAiRelay)
        .OrderBy(r => r.Route.Lane == GatewayLane.Pool ? 0 : 1).ThenBy(r => r.Route.RouteId, StringComparer.Ordinal)
        .Select(r => (r.Route.RouteId, (IGatewayOpenAiRelay)r.Worker)).ToArray();

    /// <summary>How many OpenAI-compatible requests run on this host now.</summary>
    internal int OutsideRunning { get { lock (gate) return outsideJobs.Count; } }

    /// <summary>Admits an OpenAI-compatible request of <paramref name="principal"/> on route <paramref name="routeId"/>: it takes
    /// one of the worker's slots until the returned job is disposed. Turned away (job.busy) while every slot is taken, while
    /// work of a higher rank or a live turn's hold keeps the route's graphics card, or while a slot is kept for the owner.</summary>
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
                if (rank == 0 && registration.Admission.ReservedUntil > now)
                    throw new GatewayProtocolException("job.busy", OwnerDetail);
                if (registration.Admission.Active >= registration.Route.MaximumConcurrency)
                    throw new GatewayProtocolException("job.busy");
                registration.Admission.Active++;
                job = new(this, registration, principal, rank);
                outsideJobs.Add(job);
            }
        }
        if (refused is not null)
        {
            Report(refused, "priority.refused");
            throw new GatewayProtocolException("job.busy", rank == 0 ? OwnerDetail : PriorityDetail);
        }
        return job!;
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
        lock (gate)
        {
            if (!outsideJobs.Remove(job)) return;
            var admission = job.Registration.Admission;
            admission.Active--;
            freed = admission.Freed;
            admission.Freed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        freed.TrySetResult();
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
