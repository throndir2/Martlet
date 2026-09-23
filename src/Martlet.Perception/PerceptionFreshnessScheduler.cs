namespace Martlet.Perception;

public sealed record PerceptionResourceBudget
{
    public required int MaximumCpuUnits { get; init; }
    public required int MaximumGpuMemoryMiB { get; init; }
    public required int MaximumConcurrency { get; init; }

    internal void Validate()
    {
        PerceptionWorkerGuard.Require(MaximumCpuUnits is >= 1 and <= 64,
            PerceptionWorkerFailure.ResourceBudgetExceeded);
        PerceptionWorkerGuard.Require(MaximumGpuMemoryMiB is >= 0 and <= 1_048_576,
            PerceptionWorkerFailure.ResourceBudgetExceeded);
        PerceptionWorkerGuard.Require(MaximumConcurrency is >= 1 and <= 2,
            PerceptionWorkerFailure.ResourceBudgetExceeded);
    }
}

public sealed record PerceptionSchedulerOptions
{
    public required PerceptionResourceBudget Budget { get; init; }
    public PerceptionRole? PreferredRole { get; init; }

    internal void Validate()
    {
        ArgumentNullException.ThrowIfNull(Budget);
        Budget.Validate();
        if (PreferredRole is { } preferred)
            PerceptionWorkerGuard.Defined(preferred);
    }
}

public sealed class PerceptionFreshnessScheduler : IAsyncDisposable
{
    private readonly object gate = new();
    private readonly IReadOnlyDictionary<PerceptionRole, IPerceptionJobExecutor> executors;
    private readonly PerceptionSchedulerOptions options;
    private readonly TimeProvider clock;
    private readonly Dictionary<PerceptionRole, JobState> slots = [];
    private readonly HashSet<JobState> running = [];
    private readonly Dictionary<PerceptionRole, long> latestEpochs = [];
    private readonly Dictionary<PerceptionWorkerIdentity, int> activeWorkerConcurrency =
        new(ReferenceEqualityComparer.Instance);
    private int activeCpuUnits;
    private int activeGpuMemoryMiB;
    private int activeConcurrency;
    private bool closed;

    public PerceptionFreshnessScheduler(
        IEnumerable<IPerceptionJobExecutor> executors,
        PerceptionSchedulerOptions options,
        TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(executors);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        var mapped = new Dictionary<PerceptionRole, IPerceptionJobExecutor>();
        foreach (var executor in executors)
        {
            ArgumentNullException.ThrowIfNull(executor);
            PerceptionWorkerGuard.Defined(executor.Role);
            ArgumentNullException.ThrowIfNull(executor.Worker);
            PerceptionWorkerGuard.Require(executor.Worker.Role == executor.Role,
                PerceptionWorkerFailure.RoleMismatch);
            PerceptionWorkerGuard.Require(mapped.TryAdd(executor.Role, executor),
                PerceptionWorkerFailure.InvalidData);
        }
        PerceptionWorkerGuard.Require(mapped.Count is >= 1 and <= 2);
        this.executors = mapped;
        this.options = options;
        this.clock = clock ?? TimeProvider.System;
    }

    public Task<PerceptionJobResult> ScheduleAsync(
        PerceptionJobIntent request,
        PerceptionVisionAuthorization authorization,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(authorization);
        if (cancellationToken.IsCancellationRequested)
            return Task.FromResult(Result(request, PerceptionJobOutcome.Canceled,
                PerceptionWorkerFailure.Canceled, outputDiscarded: true));

        List<Task<PerceptionJobResult>> predecessors;
        JobState state;
        lock (gate)
        {
            if (closed)
                return Task.FromResult(Result(request, PerceptionJobOutcome.NotScheduled,
                    PerceptionWorkerFailure.Closed));
            if (!executors.TryGetValue(request.Task.Role, out var executor))
                return Task.FromResult(Result(request, PerceptionJobOutcome.NotScheduled,
                    PerceptionWorkerFailure.RoleMismatch));
            if (!request.ExpectedWorker.Matches(executor.Worker))
                return Task.FromResult(Result(request, PerceptionJobOutcome.NotScheduled,
                    PerceptionWorkerFailure.IdentityMismatch));
            var admission = ValidateAdmission(request, authorization, CurrentUtc());
            if (admission is not null)
                return Task.FromResult(admission);
            if (latestEpochs.TryGetValue(request.Task.Role, out var latestEpoch) &&
                request.Epoch <= latestEpoch)
                return Task.FromResult(Result(request, PerceptionJobOutcome.NotScheduled,
                    PerceptionWorkerFailure.LateEpoch, outputDiscarded: true));
            if (!FitsTotalBudget(executor.Worker.Resources))
            {
                return Task.FromResult(Result(request, PerceptionJobOutcome.NotScheduled,
                    PerceptionWorkerFailure.ResourceBudgetExceeded));
            }

            predecessors = [];
            var toCancel = new List<JobState>();
            var projectedCpu = activeCpuUnits;
            var projectedGpu = activeGpuMemoryMiB;
            var projectedConcurrency = activeConcurrency;
            var projectedWorkerConcurrency =
                ActiveWorkerConcurrency(executor.Worker);

            var replacingSameRole = slots.TryGetValue(
                request.Task.Role, out var previous);
            if (replacingSameRole)
            {
                var previousState = previous ??
                    throw new PerceptionWorkerException(PerceptionWorkerFailure.ProtocolViolation);
                predecessors.Add(previousState.Completion.Task);
                toCancel.Add(previousState);
                SubtractIfHeld(
                    previousState,
                    executor.Worker,
                    ref projectedCpu,
                    ref projectedGpu,
                    ref projectedConcurrency,
                    ref projectedWorkerConcurrency);
                // A canceled pending replacement must not sever the original retirement barrier.
                foreach (var retiring in running.Where(candidate =>
                    candidate.Request.Task.Role == request.Task.Role &&
                    !ReferenceEquals(candidate, previousState)))
                    predecessors.Add(retiring.Completion.Task);
            }

            var requirements = executor.Worker.Resources;
            if (!CanFit(
                    projectedCpu,
                    projectedGpu,
                    projectedConcurrency,
                    projectedWorkerConcurrency,
                    executor.Worker.Limits.MaximumConcurrency,
                    requirements) &&
                options.PreferredRole == request.Task.Role)
            {
                foreach (var candidate in slots.Values
                    .Where(candidate => candidate.Request.Task.Role != request.Task.Role)
                    .Where(candidate => options.PreferredRole !=
                        candidate.Request.Task.Role)
                    .ToArray())
                {
                    predecessors.Add(candidate.Completion.Task);
                    toCancel.Add(candidate);
                    SubtractIfHeld(
                        candidate,
                        executor.Worker,
                        ref projectedCpu,
                        ref projectedGpu,
                        ref projectedConcurrency,
                        ref projectedWorkerConcurrency);
                    if (CanFit(
                        projectedCpu,
                        projectedGpu,
                        projectedConcurrency,
                        projectedWorkerConcurrency,
                        executor.Worker.Limits.MaximumConcurrency,
                        requirements))
                        break;
                }
            }

            if (!CanFit(
                projectedCpu,
                projectedGpu,
                projectedConcurrency,
                projectedWorkerConcurrency,
                executor.Worker.Limits.MaximumConcurrency,
                requirements) &&
                !replacingSameRole)
            {
                return Task.FromResult(Result(request, PerceptionJobOutcome.NotScheduled,
                    PerceptionWorkerFailure.ResourceUnavailable));
            }
            if (!authorization.TryReserve())
            {
                return Task.FromResult(Result(request, PerceptionJobOutcome.NotScheduled,
                    PerceptionWorkerFailure.PermissionConsumed));
            }

            latestEpochs[request.Task.Role] = request.Epoch;
            foreach (var candidate in toCancel)
            {
                candidate.Replaced = true;
                candidate.RequestCancellation();
            }
            state = new(
                request,
                authorization,
                executor,
                cancellationToken,
                new PerceptionJobClock(clock));
            slots[request.Task.Role] = state;
            _ = Task.Run(() => RunAsync(state, predecessors));
        }

        return state.Completion.Task;
    }

    public async ValueTask DisposeAsync()
    {
        Task<PerceptionJobResult>[] tasks;
        lock (gate)
        {
            if (closed)
                return;
            closed = true;
            var active = slots.Values.ToArray();
            foreach (var state in active)
                state.RequestCancellation();
            tasks = active.Concat(running).Distinct()
                .Select(state => state.Completion.Task).ToArray();
        }

        if (tasks.Length > 0)
            await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private async Task RunAsync(
        JobState state,
        IReadOnlyCollection<Task<PerceptionJobResult>> predecessors)
    {
        PerceptionJobResult result;
        try
        {
            if (predecessors.Count > 0)
            {
                await Task.WhenAll(predecessors)
                    .WaitAsync(state.StopWaiting.Token)
                    .ConfigureAwait(false);
            }

            result = ValidateBeforeRun(state);
            if (result.Failure is not null)
            {
                Complete(state, result);
                return;
            }

            lock (gate)
            {
                if (!IsCurrent(state))
                {
                    CompleteUnderLock(state, Result(
                        state.Request,
                        PerceptionJobOutcome.NotScheduled,
                        PerceptionWorkerFailure.Superseded,
                        outputDiscarded: true));
                    return;
                }
                if (!CanFit(
                    activeCpuUnits,
                    activeGpuMemoryMiB,
                    activeConcurrency,
                    ActiveWorkerConcurrency(state.Executor.Worker),
                    state.Executor.Worker.Limits.MaximumConcurrency,
                    state.Executor.Worker.Resources))
                {
                    CompleteUnderLock(state, Result(
                        state.Request,
                        PerceptionJobOutcome.NotScheduled,
                        PerceptionWorkerFailure.ResourceUnavailable));
                    return;
                }
                if (!state.Authorization.TryOpenReservation())
                {
                    CompleteUnderLock(state, Result(
                        state.Request,
                        PerceptionJobOutcome.NotScheduled,
                        PerceptionWorkerFailure.PermissionConsumed));
                    return;
                }

                HoldResources(state);
            }

            var executionStarted = false;
            try
            {
                var remaining = state.Request.DeadlineUtc - state.Clock.GetUtcNow();
                if (remaining <= TimeSpan.Zero)
                {
                    lock (gate)
                    {
                        ReleaseResources(state);
                        CompleteUnderLock(state, Result(state.Request,
                            PerceptionJobOutcome.DeadlineExceeded,
                            PerceptionWorkerFailure.DeadlineExceeded, outputDiscarded: true));
                    }
                    return;
                }
                using var deadline = new CancellationTokenSource(remaining, clock);
                using var registration = deadline.Token.Register(state.RequestCancellation);
                var execution = Task.Run(async () =>
                {
                    var preflight = ValidateBeforeRun(state);
                    if (preflight.Failure is not null)
                        return preflight;
                    return await state.Executor.ExecuteAsync(
                        state.Request,
                        state.Authorization,
                        state.Cancellation.Token).ConfigureAwait(false);
                });
                executionStarted = true;
                PerceptionGatewayClientAdapter.ObserveFault(execution);
                try
                {
                    result = await execution.WaitAsync(state.StopWaiting.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Give an executor bounded time to report actual compute retirement.
                    result = await execution.WaitAsync(
                        PerceptionProtocol.MaximumCancelDuration, clock)
                        .ConfigureAwait(false);
                }
                await state.RetireCancellation().WaitAsync(
                    PerceptionProtocol.MaximumCancelDuration, clock).ConfigureAwait(false);
                ArgumentNullException.ThrowIfNull(result);
                PerceptionWorkerGuard.Require(result.Ids == state.Request.Ids &&
                    result.ActionId == state.Request.ActionId &&
                    result.Epoch == state.Request.Epoch &&
                    result.Role == state.Request.Task.Role &&
                    Enum.IsDefined(result.Outcome),
                    PerceptionWorkerFailure.ProtocolViolation);
                lock (gate)
                {
                    if (result.WorkerMayContinue)
                        state.ResourcesQuarantined = true;
                    else
                        ReleaseResources(state);
                }
            }
            catch
            {
                lock (gate)
                {
                    if (executionStarted)
                        state.ResourcesQuarantined = state.ResourcesHeld;
                    else
                        ReleaseResources(state);
                }
                throw;
            }

        }
        catch (OperationCanceledException)
        {
            result = Result(
                state.Request,
                state.ExternalCancellation.IsCancellationRequested
                    ? PerceptionJobOutcome.Canceled
                    : PerceptionJobOutcome.NotScheduled,
                state.ExternalCancellation.IsCancellationRequested
                    ? PerceptionWorkerFailure.Canceled
                    : PerceptionWorkerFailure.Superseded,
                outputDiscarded: true,
                workerMayContinue: state.ResourcesQuarantined);
        }
        catch (TimeoutException)
        {
            result = Result(state.Request,
                state.Clock.GetUtcNow() >= state.Request.DeadlineUtc
                    ? PerceptionJobOutcome.DeadlineExceeded
                    : PerceptionJobOutcome.Canceled,
                state.Clock.GetUtcNow() >= state.Request.DeadlineUtc
                    ? PerceptionWorkerFailure.DeadlineExceeded
                    : PerceptionWorkerFailure.Canceled,
                outputDiscarded: true, workerMayContinue: state.ResourcesQuarantined);
        }
        catch (PerceptionWorkerException error)
        {
            result = Result(
                state.Request,
                PerceptionJobOutcome.Failed,
                error.Failure,
                outputDiscarded: true,
                workerMayContinue: state.ResourcesQuarantined);
        }
        catch (Exception)
        {
            result = Result(
                state.Request,
                PerceptionJobOutcome.Failed,
                PerceptionWorkerFailure.ProtocolViolation,
                outputDiscarded: true,
                workerMayContinue: state.ResourcesQuarantined);
        }

        Complete(state, result);
    }

    private static PerceptionJobResult? ValidateAdmission(
        PerceptionJobIntent request,
        PerceptionVisionAuthorization authorization,
        DateTimeOffset now)
    {
        if (!ReferenceEquals(authorization.Request, request))
            return Result(request, PerceptionJobOutcome.NotScheduled,
                PerceptionWorkerFailure.PermissionMismatch);
        if (!authorization.IsAvailable)
            return Result(request, PerceptionJobOutcome.NotScheduled,
                PerceptionWorkerFailure.PermissionConsumed);
        if (authorization.ExpiresAtUtc <= now ||
            authorization.ExpiresAtUtc > request.DeadlineUtc)
            return Result(request, PerceptionJobOutcome.NotScheduled,
                PerceptionWorkerFailure.PermissionExpired);
        if (request.CreatedAtUtc > now + PerceptionProtocol.MaximumClockSkew ||
            request.Frame.CapturedAtUtc > now + PerceptionProtocol.MaximumClockSkew)
            return Result(request, PerceptionJobOutcome.NotScheduled,
                PerceptionWorkerFailure.ClockSkew, outputDiscarded: true);
        if (request.DeadlineUtc <= now)
            return Result(request, PerceptionJobOutcome.DeadlineExceeded,
                PerceptionWorkerFailure.DeadlineExceeded, outputDiscarded: true);
        if (request.DeadlineUtc - now > PerceptionProtocol.MaximumJobDuration)
            return Result(request, PerceptionJobOutcome.DeadlineExceeded,
                PerceptionWorkerFailure.DeadlineExceeded, outputDiscarded: true);
        if (now - request.Frame.CapturedAtUtc >= request.MaximumFrameAge)
            return Result(request, PerceptionJobOutcome.NotScheduled,
                PerceptionWorkerFailure.StaleFrame, outputDiscarded: true);
        if (request.Frame.Content.ReferenceExpiresAtUtc is { } expiry &&
            expiry <= now)
            return Result(request, PerceptionJobOutcome.NotScheduled,
                PerceptionWorkerFailure.InvalidFrameReference, outputDiscarded: true);
        return null;
    }

    private PerceptionJobResult ValidateBeforeRun(JobState state)
    {
        if (state.Cancellation.IsCancellationRequested)
            return Result(
                state.Request,
                state.ExternalCancellation.IsCancellationRequested
                    ? PerceptionJobOutcome.Canceled
                    : PerceptionJobOutcome.NotScheduled,
                state.ExternalCancellation.IsCancellationRequested
                    ? PerceptionWorkerFailure.Canceled
                    : PerceptionWorkerFailure.Superseded,
                outputDiscarded: true);

        var now = state.Clock.GetUtcNow();
        if (state.Request.CreatedAtUtc >
                now + PerceptionProtocol.MaximumClockSkew ||
            state.Request.Frame.CapturedAtUtc >
                now + PerceptionProtocol.MaximumClockSkew)
            return Result(state.Request, PerceptionJobOutcome.NotScheduled,
                PerceptionWorkerFailure.ClockSkew, outputDiscarded: true);
        if (state.Request.DeadlineUtc <= now)
            return Result(state.Request, PerceptionJobOutcome.DeadlineExceeded,
                PerceptionWorkerFailure.DeadlineExceeded, outputDiscarded: true);
        if (state.Request.DeadlineUtc - now >
            PerceptionProtocol.MaximumJobDuration)
            return Result(state.Request, PerceptionJobOutcome.DeadlineExceeded,
                PerceptionWorkerFailure.DeadlineExceeded, outputDiscarded: true);
        if (now - state.Request.Frame.CapturedAtUtc >=
            state.Request.MaximumFrameAge)
            return Result(state.Request, PerceptionJobOutcome.NotScheduled,
                PerceptionWorkerFailure.StaleFrame, outputDiscarded: true);
        if (state.Authorization.ExpiresAtUtc <= now)
            return Result(state.Request, PerceptionJobOutcome.NotScheduled,
                PerceptionWorkerFailure.PermissionExpired);
        if (state.Request.Frame.Content.ReferenceExpiresAtUtc is { } expiry &&
            expiry <= now)
            return Result(state.Request, PerceptionJobOutcome.NotScheduled,
                PerceptionWorkerFailure.InvalidFrameReference, outputDiscarded: true);
        lock (gate)
        {
            if (!IsCurrent(state))
                return Result(state.Request, PerceptionJobOutcome.NotScheduled,
                    PerceptionWorkerFailure.Superseded, outputDiscarded: true);
        }
        return Result(state.Request, PerceptionJobOutcome.Completed, null);
    }

    private PerceptionJobResult ValidateAfterRun(
        JobState state,
        PerceptionJobResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        lock (gate)
        {
            if (!IsCurrent(state) || state.Replaced)
                return Result(state.Request, PerceptionJobOutcome.NotScheduled,
                    PerceptionWorkerFailure.Superseded, outputDiscarded: true,
                    computeCancellation: result.ComputeCancellation,
                    workerMayContinue: result.WorkerMayContinue);
        }

        if (state.ExternalCancellation.IsCancellationRequested ||
            closed && state.Cancellation.IsCancellationRequested)
            return Result(state.Request, PerceptionJobOutcome.Canceled,
                PerceptionWorkerFailure.Canceled, outputDiscarded: true,
                computeCancellation: result.ComputeCancellation,
                workerMayContinue: result.WorkerMayContinue);
        if (result.Outcome != PerceptionJobOutcome.Completed)
            return result;
        if (result.Observation is null)
            return Result(state.Request, PerceptionJobOutcome.Failed,
                PerceptionWorkerFailure.ProtocolViolation, outputDiscarded: true);

        var now = state.Clock.GetUtcNow();
        if (now >= state.Request.DeadlineUtc)
            return Result(state.Request, PerceptionJobOutcome.DeadlineExceeded,
                PerceptionWorkerFailure.DeadlineExceeded, outputDiscarded: true);
        if (now >= result.Observation.ExpiresAtUtc ||
            now - state.Request.Frame.CapturedAtUtc >=
                state.Request.MaximumFrameAge)
            return Result(state.Request, PerceptionJobOutcome.NotScheduled,
                PerceptionWorkerFailure.StaleResult, outputDiscarded: true);
        return result;
    }

    private void Complete(JobState state, PerceptionJobResult result)
    {
        lock (gate)
            CompleteUnderLock(state, result);
    }

    private void CompleteUnderLock(JobState state, PerceptionJobResult result)
    {
        result = ValidateAfterRun(state, result);
        running.Remove(state);
        if (slots.TryGetValue(state.Request.Task.Role, out var current) &&
            ReferenceEquals(current, state))
            slots.Remove(state.Request.Task.Role);
        state.Authorization.FinalizeReservation();
        state.Completion.TrySetResult(result);
        state.DisposeCancellation();
    }

    private bool IsCurrent(JobState state) =>
        slots.TryGetValue(state.Request.Task.Role, out var current) &&
        ReferenceEquals(current, state) &&
        latestEpochs.TryGetValue(state.Request.Task.Role, out var epoch) &&
        epoch == state.Request.Epoch;

    private bool FitsTotalBudget(PerceptionResourceRequirements requirements) =>
        requirements.CpuUnits <= options.Budget.MaximumCpuUnits &&
        requirements.GpuMemoryMiB <= options.Budget.MaximumGpuMemoryMiB &&
        options.Budget.MaximumConcurrency >= 1;

    private bool CanFit(
        int cpu,
        int gpu,
        int concurrency,
        int workerConcurrency,
        int workerMaximumConcurrency,
        PerceptionResourceRequirements requirements) =>
        cpu + requirements.CpuUnits <= options.Budget.MaximumCpuUnits &&
        gpu + requirements.GpuMemoryMiB <= options.Budget.MaximumGpuMemoryMiB &&
        concurrency + 1 <= options.Budget.MaximumConcurrency &&
        workerConcurrency + 1 <= workerMaximumConcurrency;

    private void HoldResources(JobState state)
    {
        PerceptionWorkerGuard.Require(!state.ResourcesHeld);
        state.ResourcesHeld = true;
        running.Add(state);
        activeCpuUnits = checked(activeCpuUnits +
            state.Executor.Worker.Resources.CpuUnits);
        activeGpuMemoryMiB = checked(activeGpuMemoryMiB +
            state.Executor.Worker.Resources.GpuMemoryMiB);
        activeConcurrency = checked(activeConcurrency + 1);
        activeWorkerConcurrency[state.Executor.Worker] = checked(
            ActiveWorkerConcurrency(state.Executor.Worker) + 1);
    }

    private void ReleaseResources(JobState state)
    {
        if (!state.ResourcesHeld)
            return;
        state.ResourcesHeld = false;
        activeCpuUnits -= state.Executor.Worker.Resources.CpuUnits;
        activeGpuMemoryMiB -= state.Executor.Worker.Resources.GpuMemoryMiB;
        activeConcurrency--;
        var workerConcurrency =
            ActiveWorkerConcurrency(state.Executor.Worker) - 1;
        if (workerConcurrency == 0)
            activeWorkerConcurrency.Remove(state.Executor.Worker);
        else
            activeWorkerConcurrency[state.Executor.Worker] = workerConcurrency;
        PerceptionWorkerGuard.Require(activeCpuUnits >= 0 &&
            activeGpuMemoryMiB >= 0 &&
            activeConcurrency >= 0 &&
            workerConcurrency >= 0);
    }

    private static void SubtractIfHeld(
        JobState state,
        PerceptionWorkerIdentity requestedWorker,
        ref int cpu,
        ref int gpu,
        ref int concurrency,
        ref int workerConcurrency)
    {
        if (!state.ResourcesHeld)
            return;
        if (state.ResourcesQuarantined)
            return;
        cpu -= state.Executor.Worker.Resources.CpuUnits;
        gpu -= state.Executor.Worker.Resources.GpuMemoryMiB;
        concurrency--;
        if (ReferenceEquals(state.Executor.Worker, requestedWorker))
            workerConcurrency--;
    }

    private int ActiveWorkerConcurrency(PerceptionWorkerIdentity worker) =>
        activeWorkerConcurrency.GetValueOrDefault(worker);

    private DateTimeOffset CurrentUtc()
    {
        var now = clock.GetUtcNow();
        PerceptionWorkerGuard.Utc(now);
        return now;
    }

    private static PerceptionJobResult Result(
        PerceptionJobIntent request,
        PerceptionJobOutcome outcome,
        PerceptionWorkerFailure? failure,
        bool outputDiscarded = false,
        PerceptionCancellationCapability? computeCancellation = null,
        bool workerMayContinue = false) => new()
        {
            Ids = request.Ids,
            ActionId = request.ActionId,
            Epoch = request.Epoch,
            Role = request.Task.Role,
            Outcome = outcome,
            Failure = failure,
            OutputDiscarded = outputDiscarded,
            ComputeCancellation = computeCancellation,
            WorkerMayContinue = workerMayContinue
        };

    private sealed class JobState
    {
        internal JobState(
            PerceptionJobIntent request,
            PerceptionVisionAuthorization authorization,
            IPerceptionJobExecutor executor,
            CancellationToken externalCancellation,
            PerceptionJobClock clock)
        {
            Request = request;
            Authorization = authorization;
            Executor = executor;
            ExternalCancellation = externalCancellation;
            Clock = clock;
            externalRegistration = externalCancellation.Register(RequestCancellation);
        }

        internal PerceptionJobIntent Request { get; }
        internal PerceptionVisionAuthorization Authorization { get; }
        internal IPerceptionJobExecutor Executor { get; }
        private readonly object cancellationGate = new();
        private readonly CancellationTokenRegistration externalRegistration;
        private Task cancellationCallbacks = Task.CompletedTask;
        private bool cancellationRetired;
        internal CancellationTokenSource Cancellation { get; } = new();
        internal CancellationTokenSource StopWaiting { get; } = new();
        internal CancellationToken ExternalCancellation { get; }
        internal PerceptionJobClock Clock { get; }
        internal Task RetireCancellation()
        {
            lock (cancellationGate)
            {
                cancellationRetired = true;
                return cancellationCallbacks;
            }
        }
        internal void RequestCancellation()
        {
            lock (cancellationGate)
            {
                if (!cancellationRetired && !Cancellation.IsCancellationRequested)
                {
                    cancellationCallbacks = Cancellation.CancelAsync();
                    PerceptionGatewayClientAdapter.ObserveFault(cancellationCallbacks);
                    StopWaiting.Cancel();
                }
            }
        }
        internal void DisposeCancellation()
        {
            // Never wait for foreign cancellation callbacks under the scheduler lock.
            _ = RetireCancellation().ContinueWith(_ =>
            {
                externalRegistration.Dispose();
                Cancellation.Dispose();
                StopWaiting.Dispose();
            }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        }
        internal TaskCompletionSource<PerceptionJobResult> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool ResourcesHeld { get; set; }
        internal bool ResourcesQuarantined { get; set; }
        internal bool Replaced { get; set; }
    }
}
