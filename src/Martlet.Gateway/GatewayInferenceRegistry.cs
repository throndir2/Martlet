namespace Martlet.Gateway;

public sealed class GatewayInferenceRouteRegistry
{
    private readonly object gate = new();
    private readonly Dictionary<string, RouteRegistration> byPath =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, RouteRegistration> byId =
        new(StringComparer.Ordinal);
    private readonly Dictionary<WorkerKey, WorkerAdmission> admissions = [];
    private readonly Dictionary<Guid, GatewayInferenceJob> activeJobs = [];
    private readonly Dictionary<Guid, DateTimeOffset> admittedRequests = [];
    private readonly TimeProvider clock;
    private bool closed;
    private TaskCompletionSource idle = CompletedIdle();

    private static TaskCompletionSource CompletedIdle()
    {
        var value = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        value.SetResult();
        return value;
    }

    internal async ValueTask CloseAsync()
    {
        GatewayInferenceJob[] jobs;
        Task retirement;
        lock (gate)
        {
            closed = true;
            jobs = activeJobs.Values.ToArray();
            retirement = idle.Task;
        }
        foreach (var job in jobs)
        {
            try { _ = await job.CancelAsync().ConfigureAwait(false); }
            catch (GatewayProtocolException error) when (error.Failure.Code == "job.not_found")
            {
                // Completion owns retirement; the idle task below still joins it.
            }
        }
        await retirement.ConfigureAwait(false);
    }

    public GatewayInferenceRouteRegistry(
        IEnumerable<IGatewayInferenceWorker> workers,
        TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(workers);
        this.clock = clock ?? TimeProvider.System;
        var supplied = workers.Take(GatewayInferenceProtocol.MaximumRoutes + 1).ToArray();
        GatewayRules.Require(supplied.Length <= GatewayInferenceProtocol.MaximumRoutes,
            "worker.invalid");
        foreach (var worker in supplied)
        {
            ArgumentNullException.ThrowIfNull(worker);
            var route = worker.Route;
            ArgumentNullException.ThrowIfNull(route);
            GatewayRules.Require(ReferenceEquals(route, worker.Route), "worker.invalid");
            ValidateWorkerType(worker, route.Kind);
            GatewayRules.Require(byPath.TryAdd(
                    route.Path,
                    new(worker, route, Admission(route))) &&
                byId.TryAdd(route.RouteId, byPath[route.Path]),
                "worker.invalid");
        }
    }

    internal GatewayInferenceRouteCapability[] CapabilitiesFor(
        GatewayRole role) =>
        byId.Values
            .Where(registration => registration.Route.RequiredRole == role)
            .OrderBy(registration => registration.Route.RouteId, StringComparer.Ordinal)
            .Select(registration => GatewayInferenceRouteCapability.From(
                registration.Route))
            .ToArray();

    internal bool TryGetByPath(
        string path,
        out GatewayInferenceRoute route)
    {
        if (byPath.TryGetValue(path, out var registration))
        {
            route = registration.Route;
            return true;
        }
        route = null!;
        return false;
    }

    internal void ValidateRouteRole(string routeId, GatewayRole role)
    {
        if (!byId.TryGetValue(routeId, out var registration))
            throw new GatewayProtocolException("request.invalid");
        GatewayRules.Require(registration.Route.RequiredRole == role, "auth.role");
    }

    internal GatewayInferenceJob Begin(
        GatewayPrincipal principal,
        GatewayInferenceRequest request)
        => principal.WithAuthority(() => BeginAuthorized(principal, request));

    private GatewayInferenceJob BeginAuthorized(
        GatewayPrincipal principal, GatewayInferenceRequest request)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(request);
        if (!byId.TryGetValue(request.Route.RouteId, out var registration) ||
            !ReferenceEquals(registration.Route, request.Route))
            throw new GatewayProtocolException("request.invalid");
        GatewayRules.Require(principal.Role == request.Route.RequiredRole,
            "auth.role");

        lock (gate)
        {
            GatewayRules.Require(!closed, "worker.unavailable");
            var now = clock.GetUtcNow();
            foreach (var expired in admittedRequests.Where(item => item.Value <= now)
                .Select(item => item.Key).ToArray())
                admittedRequests.Remove(expired);
            if (registration.Admission.Quarantined)
                throw new GatewayProtocolException("worker.quarantined");
            if (registration.Admission.Active ||
                activeJobs.ContainsKey(request.RequestId))
                throw new GatewayProtocolException("job.busy");
            GatewayRules.Require(!admittedRequests.ContainsKey(request.RequestId), "job.replay");
            GatewayRules.Require(admittedRequests.Count < 1024, "job.busy");
            GatewayRules.Require(ReferenceEquals(registration.Worker.Route, registration.Route),
                "worker.identity");
            admittedRequests.Add(request.RequestId, request.DeadlineUtc);
            registration.Admission.Active = true;
            if (activeJobs.Count == 0)
                idle = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var job = new GatewayInferenceJob(
                this,
                registration,
                principal,
                request,
                clock);
            activeJobs.Add(request.RequestId, job);
            return job;
        }
    }

    internal async ValueTask<GatewayInferenceCancellationReceipt> CancelAsync(
        GatewayPrincipal principal,
        GatewayInferenceCancellationRequest request)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(request);
        var cancellation = principal.WithAuthority(() =>
        {
            lock (gate)
            {
                if (!activeJobs.TryGetValue(request.RequestId, out var job) ||
                    job.Request.Route.RouteId != request.RouteId ||
                    job.Principal.HostId != principal.HostId ||
                    job.Principal.DeviceId != principal.DeviceId ||
                    job.Principal.CredentialId != principal.CredentialId ||
                    job.Principal.Role != principal.Role)
                    throw new GatewayProtocolException("job.not_found");
                return job.CancelAsync();
            }
        });
        return await cancellation.ConfigureAwait(false);
    }

    private WorkerAdmission Admission(GatewayInferenceRoute route)
    {
        var key = new WorkerKey(route.RequiredRole, route.WorkerId);
        if (!admissions.TryGetValue(key, out var admission))
        {
            admission = new();
            admissions.Add(key, admission);
        }
        return admission;
    }

    private void Finish(GatewayInferenceJob job, bool healthy)
    {
        lock (gate)
        {
            if (!activeJobs.Remove(job.Request.RequestId))
                return;
            job.Registration.Admission.Active = false;
            if (!healthy)
                job.Registration.Admission.Quarantined = true;
            if (activeJobs.Count == 0)
                idle.TrySetResult();
        }
    }

    private void Quarantine(GatewayInferenceJob job)
    {
        lock (gate)
            job.Registration.Admission.Quarantined = true;
    }

    private static void ValidateWorkerType(
        IGatewayInferenceWorker worker,
        GatewayInferenceKind kind) =>
        GatewayRules.Require(kind switch
        {
            GatewayInferenceKind.OllamaChat =>
                worker is IOllamaGatewayInferenceWorker,
            GatewayInferenceKind.F5Synthesis =>
                worker is IF5GatewayInferenceWorker,
            GatewayInferenceKind.PerceptionOcr or
                GatewayInferenceKind.PerceptionVlm =>
                worker is IPerceptionGatewayInferenceWorker,
            GatewayInferenceKind.Audio2Face =>
                worker is IAudio2FaceGatewayInferenceWorker,
            _ => false
        }, "worker.invalid");

    private readonly record struct WorkerKey(
        GatewayRole Role,
        string WorkerId);

    internal sealed class WorkerAdmission
    {
        internal bool Active;
        internal bool Quarantined;
    }

    internal sealed record RouteRegistration(
        IGatewayInferenceWorker Worker,
        GatewayInferenceRoute Route,
        WorkerAdmission Admission);

    internal sealed class GatewayInferenceJob
    {
        private readonly object cancellationGate = new();
        private readonly GatewayInferenceRouteRegistry owner;
        private readonly TimeProvider clock;
        private readonly CancellationTokenSource cancellation = new();
        private Task<GatewayInferenceCancellationReceipt>? cancellationTask;
        private int completed;
        private bool cancellationInvalid;
        private GatewayInferencePermissionLease? permission;
        private Task? preparation;
        private Task? workerCancellation;
        private Task? cancellationCallbacks;
        private Task<bool>? cleanup;
        private int released;
        private readonly CancellationTokenSource monitorStop = new();
        private readonly Task authorityMonitor;
        internal Task? PendingOperation { get; set; }

        internal GatewayInferenceJob(
            GatewayInferenceRouteRegistry owner,
            RouteRegistration registration,
            GatewayPrincipal principal,
            GatewayInferenceRequest request,
            TimeProvider clock)
        {
            this.owner = owner;
            Registration = registration;
            Principal = principal;
            Request = request;
            this.clock = clock;
            authorityMonitor = MonitorAuthorityAsync();
        }

        internal RouteRegistration Registration { get; }
        internal GatewayPrincipal Principal { get; }
        internal GatewayInferenceRequest Request { get; }
        internal CancellationToken CancellationToken => cancellation.Token;
        internal bool IsCancellationRequested => cancellation.IsCancellationRequested;
        internal CancellationToken PermissionRevoked => permission?.Revoked ?? CancellationToken.None;

        private async Task MonitorAuthorityAsync()
        {
            try
            {
                while (true)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(100), monitorStop.Token).ConfigureAwait(false);
                    Principal.WithAuthority(() => true);
                }
            }
            catch (OperationCanceledException) when (monitorStop.IsCancellationRequested) { }
            catch (GatewayProtocolException)
            {
                try { await CancelAsync().ConfigureAwait(false); }
                catch (GatewayProtocolException error) when (error.Failure.Code == "job.not_found")
                {
                    // Normal completion has already taken ownership of retirement.
                }
            }
        }

        internal Task PrepareAsync(CancellationToken token) =>
            preparation = PrepareCoreAsync(token);

        private async Task PrepareCoreAsync(CancellationToken token)
        {
            permission = await Principal.WithAuthority(() =>
                Registration.Worker.AcquirePermissionAsync(Request, Principal, token)).ConfigureAwait(false);
            WithPermission(() => true);
        }

        internal T WithPermission<T>(Func<T> operation, CancellationToken token = default) => Principal.WithAuthority(() =>
        {
            lock (cancellationGate)
            {
                token.ThrowIfCancellationRequested();
                cancellation.Token.ThrowIfCancellationRequested();
                GatewayRules.Require(ReferenceEquals(Registration.Worker.Route, Request.Route),
                    "worker.identity");
                GatewayRules.Require(permission is not null &&
                    ReferenceEquals(permission.Request, Request) &&
                    permission.HostId == Principal.HostId &&
                    permission.DeviceId == Principal.DeviceId &&
                    permission.CredentialId == Principal.CredentialId &&
                    permission.Role == Principal.Role && !permission.Revoked.IsCancellationRequested,
                    "action.denied");
                try { permission!.Validate(); }
                catch (GatewayInferenceWorkerException error) { throw new GatewayProtocolException(error.Code); }
                GatewayRules.Require(clock.GetUtcNow() < Request.DeadlineUtc, "job.deadline");
                if (Request.Payload is GatewayPerceptionPayload perception)
                    GatewayRules.Require(clock.GetUtcNow() - perception.Frame.CapturedAtUtc <
                        perception.MaximumFrameAge &&
                        (perception.Frame.Content.ReferenceExpiresAtUtc is null ||
                            perception.Frame.Content.ReferenceExpiresAtUtc > clock.GetUtcNow()), "request.invalid");
                token.ThrowIfCancellationRequested();
                cancellation.Token.ThrowIfCancellationRequested();
                return operation();
            }
        });

        internal Task<GatewayInferenceCancellationReceipt> CancelAsync()
        {
            lock (cancellationGate)
            {
                if (Volatile.Read(ref completed) != 0 &&
                    cancellationTask is null)
                    return Task.FromException<GatewayInferenceCancellationReceipt>(
                        new GatewayProtocolException("job.not_found"));
                cancellationTask ??= CancelCoreAsync();
                return cancellationTask;
            }
        }

        internal async ValueTask<bool> CompleteAsync(
            IAsyncEnumerator<GatewayInferenceEvent>? enumerator,
            bool cancelWorker,
            bool cleanupHealthy = true,
            bool quarantineWorker = false)
        {
            if (Interlocked.Exchange(ref completed, 1) != 0)
                return false;
            cleanup = CleanupAsync(enumerator, cancelWorker, cleanupHealthy, quarantineWorker);
            try
            {
                return await cleanup.WaitAsync(GatewayInferenceProtocol.MaximumRetirementDuration)
                    .ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                owner.Quarantine(this);
                return false;
            }
        }

        internal async ValueTask<bool> ReleaseAsync()
        {
            if (Interlocked.Exchange(ref released, 1) != 0)
                return false;
            var retirement = ReleaseCoreAsync();
            try { return await retirement.WaitAsync(GatewayInferenceProtocol.MaximumRetirementDuration).ConfigureAwait(false); }
            catch (TimeoutException)
            {
                owner.Quarantine(this);
                return false;
            }
        }

        private async Task<bool> ReleaseCoreAsync()
        {
            var healthy = false;
            try
            {
                try { healthy = await (cleanup ?? Task.FromResult(false)).ConfigureAwait(false); }
                catch (Exception) { healthy = false; }
                try
                {
                    await monitorStop.CancelAsync().ConfigureAwait(false);
                    await authorityMonitor.ConfigureAwait(false);
                }
                catch (Exception) { healthy = false; }
                if (permission is not null)
                {
                    var disposal = permission.DisposeAsync().AsTask();
                    try { await disposal.WaitAsync(GatewayInferenceProtocol.MaximumRetirementDuration).ConfigureAwait(false); }
                    catch (TimeoutException)
                    {
                        owner.Quarantine(this);
                        await disposal.ConfigureAwait(false);
                    }
                }
            }
            catch (Exception)
            {
                healthy = false;
            }
            finally
            {
                Request.Payload.Clear();
                cancellation.Dispose();
                monitorStop.Dispose();
                owner.Finish(this, healthy);
            }
            return healthy;
        }

        private async Task<bool> CleanupAsync(
            IAsyncEnumerator<GatewayInferenceEvent>? enumerator,
            bool cancelWorker, bool cleanupHealthy, bool quarantineWorker)
        {
            var healthy = cleanupHealthy;
            Task<GatewayInferenceCancellationReceipt>? cancellationToJoin;
            lock (cancellationGate)
            {
                if (cancelWorker)
                    cancellationTask ??= CancelCoreAsync();
                cancellationToJoin = cancellationTask;
            }
            if (cancellationToJoin is not null)
                _ = await cancellationToJoin.ConfigureAwait(false);
            foreach (var pending in new[] { preparation, PendingOperation, workerCancellation, cancellationCallbacks })
            {
                if (pending is null)
                    continue;
                try { await pending.ConfigureAwait(false); }
                catch (OperationCanceledException) { }
                // Execution/preparation failures are mapped by the HTTP owner; joining is not a cleanup failure.
                catch (Exception) when (ReferenceEquals(pending, preparation) ||
                    ReferenceEquals(pending, PendingOperation)) { }
                catch (Exception) { healthy = false; }
            }
            if (enumerator is not null)
            {
                Task disposal;
                try
                {
                    disposal = enumerator.DisposeAsync().AsTask();
                }
                catch (Exception)
                {
                    healthy = false;
                    disposal = Task.CompletedTask;
                }
                try
                {
                    await disposal.ConfigureAwait(false);
                }
                catch (Exception)
                {
                    healthy = false;
                    _ = disposal.ContinueWith(
                        static task => _ = task.Exception,
                        CancellationToken.None,
                        TaskContinuationOptions.OnlyOnFaulted |
                            TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default);
                }
            }
            healthy &= !cancellationInvalid;
            if (!healthy || quarantineWorker)
                owner.Quarantine(this);
            return healthy;
        }

        private async Task<GatewayInferenceCancellationReceipt> CancelCoreAsync()
        {
            try
            {
                cancellationCallbacks = cancellation.CancelAsync();
                await cancellationCallbacks.WaitAsync(GatewayInferenceProtocol.MaximumCancellationDuration)
                    .ConfigureAwait(false);
            }
            catch (Exception)
            {
                cancellationInvalid = true;
            }

            using var timeout = new CancellationTokenSource(
                GatewayInferenceProtocol.MaximumCancellationDuration,
                clock);
            try
            {
                var cancellationOperation = Registration.Worker.CancelAsync(
                    new()
                    {
                        RouteId = Request.Route.RouteId,
                        RequestId = Request.RequestId
                    },
                    timeout.Token).AsTask();
                workerCancellation = cancellationOperation;
                GatewayInferenceCancellationReceipt receipt;
                try
                {
                    receipt = await cancellationOperation
                        .WaitAsync(timeout.Token).ConfigureAwait(false);
                }
                catch
                {
                    if (!cancellationOperation.IsCompleted)
                    {
                        _ = cancellationOperation.ContinueWith(
                            static task => _ = task.Exception,
                            CancellationToken.None,
                            TaskContinuationOptions.OnlyOnFaulted |
                                TaskContinuationOptions.ExecuteSynchronously,
                            TaskScheduler.Default);
                    }
                    throw;
                }
                ValidateCancellation(receipt);
                return receipt;
            }
            catch (Exception)
            {
                cancellationInvalid = true;
                return FallbackCancellation();
            }
        }

        private void ValidateCancellation(
            GatewayInferenceCancellationReceipt? receipt)
        {
            GatewayRules.Require(receipt is not null &&
                receipt.RouteId == Request.Route.RouteId &&
                receipt.RequestId == Request.RequestId &&
                receipt.LocalDiscardAcknowledged &&
                Enum.IsDefined(receipt.ComputeCancellation) &&
                CancellationRank(receipt.ComputeCancellation) <=
                    CancellationRank(Request.Route.Cancellation) &&
                (receipt.ComputeCancellation ==
                    GatewayCancellationCapability.CooperativeComputeCancel ||
                    receipt.WorkerMayContinue), "stream.invalid");
        }

        private GatewayInferenceCancellationReceipt FallbackCancellation() =>
            new()
            {
                RouteId = Request.Route.RouteId,
                RequestId = Request.RequestId,
                LocalDiscardAcknowledged = true,
                ComputeCancellation = GatewayCancellationCapability.DiscardOnly,
                WorkerMayContinue = true
            };

        private static int CancellationRank(
            GatewayCancellationCapability capability) => capability switch
            {
                GatewayCancellationCapability.DiscardOnly => 0,
                GatewayCancellationCapability.RequestAbort => 1,
                GatewayCancellationCapability.CooperativeComputeCancel => 2,
                _ => int.MaxValue
            };
    }
}
