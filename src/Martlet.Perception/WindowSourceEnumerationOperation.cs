namespace Martlet.Perception;

internal sealed class WindowSourceEnumerationOperation
{
    private readonly object gate = new();
    private readonly INativeWindowCaptureFactory factory;
    private readonly SourceEnumerationAuthorization authorization;
    private readonly TimeProvider clock;
    private readonly CancellationToken callerToken;
    private readonly int maximumSources;
    private readonly CancellationTokenSource stopSource = new();
    private readonly TaskCompletionSource<IReadOnlyList<NativeWindowSource>> completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<WindowCaptureOwnershipRelease> ownershipRelease =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private CancellationTokenRegistration callerCancellation;
    private ITimer? authorizationWatcher;
    private Task? stopCallbacks;
    private PerceptionFailure? stopFailure;
    private PerceptionFailure? workFailure;
    private PerceptionFailure? releaseFailure;
    private IReadOnlyList<NativeWindowSource>? result;
    private bool retiring;
    private bool finished;

    internal WindowSourceEnumerationOperation(
        INativeWindowCaptureFactory factory,
        SourceEnumerationAuthorization authorization,
        int maximumSources,
        TimeProvider clock,
        CancellationToken callerToken)
    {
        this.factory = factory;
        this.authorization = authorization;
        this.maximumSources = maximumSources;
        this.clock = clock;
        this.callerToken = callerToken;
    }

    internal Task<IReadOnlyList<NativeWindowSource>> Completion =>
        completion.Task;

    internal Task<WindowCaptureOwnershipRelease> OwnershipRelease =>
        ownershipRelease.Task;

    internal void Begin()
    {
        Task worker;
        try
        {
            callerCancellation = callerToken.UnsafeRegister(
                _ => Cancel(new(PerceptionFailureCode.Canceled)),
                null);
            authorizationWatcher = clock.CreateTimer(
                _ => ObserveRevocation(),
                null,
                TimeSpan.FromMilliseconds(20),
                TimeSpan.FromMilliseconds(20));
            worker = Task.Factory.StartNew(
                Drive,
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
        }
        catch (Exception)
        {
            worker = Task.FromException(
                new PerceptionException(
                    PerceptionFailureCode.EnumerationFailed));
        }

        _ = RetireAsync(worker);
    }

    internal void Cancel(PerceptionFailure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        lock (gate)
        {
            stopFailure ??= failure;
            result = null;
            completion.TrySetException(
                new PerceptionException(stopFailure.Code));
            if (!retiring && !finished && stopCallbacks is null)
                stopCallbacks = stopSource.CancelAsync();
        }
    }

    private void Drive()
    {
        try
        {
            Check();
            var candidate = factory.Enumerate(
                new NativeEnumerationAccess(Check),
                stopSource.Token);
            Check();
            PerceptionGuard.Require(candidate is not null,
                PerceptionFailureCode.EnumerationFailed);
            if (candidate is null)
                throw new PerceptionException(
                    PerceptionFailureCode.EnumerationFailed);
            PerceptionGuard.Require(
                candidate.Count <= maximumSources,
                PerceptionFailureCode.EnumerationFailed);
            var snapshot = new NativeWindowSource[candidate.Count];
            for (var index = 0; index < snapshot.Length; index++)
            {
                Check();
                snapshot[index] = candidate[index];
            }
            result = Array.AsReadOnly(snapshot);
        }
        catch (OperationCanceledException) when (HasStopFailure())
        {
        }
        catch (NativeWindowCaptureException exception)
        {
            workFailure = new(exception.Code);
        }
        catch (PerceptionException exception)
        {
            if (!HasStopFailure())
                workFailure = exception.Failure;
        }
        catch (Exception)
        {
            workFailure = new(
                PerceptionFailureCode.EnumerationFailed);
        }
    }

    private void Check()
    {
        ObserveRevocation();
        lock (gate)
        {
            if (stopFailure is not null)
                throw new PerceptionException(stopFailure.Code);
        }
    }

    private void ObserveRevocation()
    {
        if (callerToken.IsCancellationRequested)
        {
            Cancel(new(PerceptionFailureCode.Canceled));
            return;
        }

        try
        {
            authorization.Check();
        }
        catch (PerceptionException exception)
        {
            Cancel(exception.Failure);
        }
    }

    private bool HasStopFailure()
    {
        lock (gate)
            return stopFailure is not null;
    }

    private async Task RetireAsync(Task worker)
    {
        try
        {
            await worker.ConfigureAwait(false);
        }
        catch (Exception)
        {
            workFailure ??= new(
                PerceptionFailureCode.EnumerationFailed);
        }

        Task? callbacks;
        lock (gate)
        {
            retiring = true;
            callbacks = stopCallbacks;
        }

        if (callbacks is not null)
        {
            try
            {
                await callbacks.ConfigureAwait(false);
            }
            catch (Exception)
            {
                releaseFailure ??= new(
                    PerceptionFailureCode.CancellationFailed);
            }
        }

        if (authorizationWatcher is not null)
        {
            try
            {
                await authorizationWatcher.DisposeAsync()
                    .ConfigureAwait(false);
            }
            catch (Exception)
            {
                releaseFailure ??= new(
                    PerceptionFailureCode.CleanupFailed);
            }
        }

        try
        {
            await callerCancellation.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            releaseFailure ??= new(
                PerceptionFailureCode.CleanupFailed);
        }
        try
        {
            stopSource.Dispose();
        }
        catch (Exception)
        {
            releaseFailure ??= new(
                PerceptionFailureCode.CleanupFailed);
        }

        lock (gate)
        {
            finished = true;
            var failure = releaseFailure ?? stopFailure ?? workFailure;
            ownershipRelease.TrySetResult(new(
                releaseFailure is null,
                releaseFailure));
            if (failure is not null)
            {
                result = null;
                completion.TrySetException(
                    new PerceptionException(failure.Code));
            }
            else
            {
                completion.TrySetResult(result!);
            }
        }
    }
}
