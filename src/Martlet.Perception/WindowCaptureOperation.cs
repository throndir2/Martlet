using System.Security.Cryptography;

namespace Martlet.Perception;

public sealed class WindowCaptureOperation
{
    private readonly object gate = new();
    private readonly WindowCaptureSource source;
    private readonly CaptureDestination destination;
    private readonly WindowCaptureAuthorization authorization;
    private readonly INativeWindowCaptureFactory factory;
    private readonly CaptureRateGate rateGate;
    private readonly PerceptionOptions options;
    private readonly TimeProvider clock;
    private readonly CancellationToken callerToken;
    private readonly long startedAt;
    private readonly CancellationTokenSource stopSource = new();
    private readonly TaskCompletionSource<WindowCaptureCompletion> completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<WindowCaptureOwnershipRelease> ownershipRelease =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> lifetimeRelease =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private CancellationTokenRegistration callerCancellation;
    private ITimer? authorizationWatcher;
    private ITimer? frameExpiryTimer;
    private INativeWindowCaptureSession? backend;
    private Task? stopCallbacks;
    private Task? nativeStop;
    private OwnedWindowFrame? latest;
    private PerceptionFailure? stopFailure;
    private PerceptionFailure? workFailure;
    private PerceptionFailure? releaseFailure;
    private WindowCaptureCompletion? terminal;
    private long acceptedFrames;
    private long droppedFrames;
    private bool retiring;
    private bool finished;
    private bool captured;
    private int lifetimeCleanupStarted;

    internal WindowCaptureOperation(
        WindowCaptureSource source,
        CaptureDestination destination,
        WindowCaptureAuthorization authorization,
        INativeWindowCaptureFactory factory,
        CaptureRateGate rateGate,
        PerceptionOptions options,
        TimeProvider clock,
        CancellationToken callerToken)
    {
        this.source = source;
        this.destination = destination;
        this.authorization = authorization;
        this.factory = factory;
        this.rateGate = rateGate;
        this.options = options;
        this.clock = clock;
        this.callerToken = callerToken;
        startedAt = clock.GetTimestamp();
    }

    public WindowCaptureBinding Binding => authorization.Binding;
    public WindowCaptureSource Source => source;
    public CaptureDestination Destination => destination;
    public Task<WindowCaptureCompletion> Completion => completion.Task;
    public Task<WindowCaptureOwnershipRelease> OwnershipRelease => ownershipRelease.Task;
    internal Task<bool> LifetimeRelease => lifetimeRelease.Task;

    public WindowFrameProvenance? LatestFrame
    {
        get
        {
            ObserveRevocation();
            lock (gate)
            {
                DiscardStaleFrame();
                return latest?.Provenance;
            }
        }
    }

    public WindowCaptureSnapshot Snapshot
    {
        get
        {
            ObserveRevocation();
            lock (gate)
            {
                DiscardStaleFrame();
                var failure = releaseFailure ?? stopFailure ?? workFailure ?? terminal?.Failure;
                var outcome = failure is null ? terminal?.Outcome : OutcomeFor(failure);
                return new(
                    terminal is not null,
                    outcome,
                    OwnershipState(),
                    failure,
                    latest is not null,
                    latest?.ByteCount ?? 0,
                    acceptedFrames,
                    droppedFrames);
            }
        }
    }

    internal void Begin()
    {
        Task worker;
        try
        {
            callerCancellation = callerToken.UnsafeRegister(
                _ => Stop(new(PerceptionFailureCode.Canceled)),
                null);
            authorizationWatcher = clock.CreateTimer(
                _ => ObserveRevocation(),
                null,
                TimeSpan.FromMilliseconds(20),
                TimeSpan.FromMilliseconds(20));
            worker = Task.Run(DriveAsync);
        }
        catch (Exception)
        {
            worker = Task.FromException(
                new PerceptionException(PerceptionFailureCode.NativeCaptureFailed));
        }

        _ = RetireAsync(worker);
    }

    public Task<WindowCaptureCompletion> Cancel() =>
        Stop(new(PerceptionFailureCode.Canceled));

    public WindowPreviewFrame OpenPreview(
        WindowPreviewAuthorization authorization)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ObserveRevocation();
        lock (gate)
        {
            ThrowIfStopped();
            DiscardStaleFrame();
            PerceptionGuard.Require(latest is not null,
                PerceptionFailureCode.FrameUnavailable);
            authorization.ValidateFor(latest!.Provenance, clock);
            var lease = latest.AcquireLease();
            try
            {
                authorization.Consume();
            }
            catch
            {
                latest.ReleaseLease(lease);
                throw;
            }
            return new(latest, lease, this, authorization.State);
        }
    }

    public WindowDisclosureFrame OpenDisclosure(
        WindowDisclosureAuthorization authorization)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ObserveRevocation();
        lock (gate)
        {
            ThrowIfStopped();
            DiscardStaleFrame();
            PerceptionGuard.Require(latest is not null,
                PerceptionFailureCode.FrameUnavailable);
            authorization.ValidateFor(latest!.Provenance, destination, clock);
            var lease = latest.AcquireLease();
            try
            {
                authorization.Consume();
            }
            catch
            {
                latest.ReleaseLease(lease);
                throw;
            }
            return new(latest, lease, destination, this, authorization.State);
        }
    }

    internal void CopyPixelsTo(
        OwnedWindowFrame frame,
        long lease,
        OneUseAuthorizationState leaseAuthorization,
        Span<byte> destination)
    {
        ObserveRevocation();
        lock (gate)
        {
            DiscardStaleFrame();
            PerceptionGuard.Require(ReferenceEquals(latest, frame),
                PerceptionFailureCode.InvalidState);
            authorization.CommitCopy(destination, pixels =>
                leaseAuthorization.CommitCopy(pixels, authorizedPixels =>
                    frame.CopyTo(lease, authorizedPixels)));
        }
    }

    internal Task<WindowCaptureCompletion> Stop(PerceptionFailure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        var releaseLifetime = false;
        lock (gate)
        {
            stopFailure ??= failure;
            DiscardLatestFrame();
            if (terminal is null)
            {
                terminal = new(OutcomeFor(stopFailure), OwnershipState(), stopFailure);
                completion.TrySetResult(terminal);
            }

            if (!retiring && !finished && stopCallbacks is null)
                stopCallbacks = stopSource.CancelAsync();
            if (!retiring && !finished && backend is not null && nativeStop is null)
            {
                var owned = backend;
                nativeStop = Task.Run(owned.RequestStop);
            }
            releaseLifetime = finished;
        }

        if (releaseLifetime)
            _ = ReleaseLifetimeResourcesAsync();
        return Completion;
    }

    private async Task DriveAsync()
    {
        try
        {
            Check();
            var opened = factory.Open(
                source.NativeSource,
                new NativeCaptureAccess(Check, options),
                stopSource.Token);
            lock (gate)
                backend = opened;
            Check();

            while (true)
            {
                var wait = rateGate.Remaining();
                if (wait > TimeSpan.Zero)
                    await Task.Delay(wait, clock, stopSource.Token)
                        .ConfigureAwait(false);
                Check();
                rateGate.AdmitRead();
                using var nativeFrame = opened.ReadFrame(stopSource.Token);
                Check();
                if (nativeFrame is null)
                    continue;
                if (!Accept(nativeFrame))
                    continue;
                if (Binding.Mode == WindowCaptureMode.SingleFrame)
                {
                    captured = true;
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (HasStopFailure())
        {
        }
        catch (NativeWindowCaptureException exception)
        {
            RecordWorkFailure(new(exception.Code));
        }
        catch (PerceptionException exception)
        {
            if (!HasStopFailure())
                RecordWorkFailure(exception.Failure);
        }
        catch (Exception)
        {
            RecordWorkFailure(new(PerceptionFailureCode.NativeCaptureFailed));
        }
        finally
        {
            Task? callbacks;
            Task? stop;
            INativeWindowCaptureSession? owned;
            lock (gate)
            {
                retiring = true;
                callbacks = stopCallbacks;
                stop = nativeStop;
                owned = backend;
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

            if (stop is not null)
            {
                try
                {
                    await stop.ConfigureAwait(false);
                }
                catch (Exception)
                {
                    releaseFailure ??= new(
                        PerceptionFailureCode.CancellationFailed);
                }
            }

            if (owned is not null)
            {
                try
                {
                    owned.Dispose();
                }
                catch (Exception)
                {
                    releaseFailure ??= new(
                        PerceptionFailureCode.CleanupFailed);
                }
            }
        }
    }

    private bool Accept(NativeWindowFrame nativeFrame)
    {
        var now = clock.GetUtcNow();
        ValidateFrame(nativeFrame, now);
        if (now < nativeFrame.CapturedAtUtc ||
            now - nativeFrame.CapturedAtUtc >= options.MaximumFrameAge)
        {
            Interlocked.Increment(ref droppedFrames);
            return false;
        }

        var pixels = new byte[nativeFrame.ByteCount];
        OwnedWindowFrame? candidate = null;
        try
        {
            nativeFrame.CopyPixelsTo(pixels);
            Check();
            var acceptedTimestamp = clock.GetTimestamp();
            var acceptedAtUtc = clock.GetUtcNow();
            if (acceptedAtUtc < nativeFrame.CapturedAtUtc ||
                acceptedAtUtc - nativeFrame.CapturedAtUtc >=
                    options.MaximumFrameAge)
            {
                Interlocked.Increment(ref droppedFrames);
                return false;
            }
            var sequence = Interlocked.Read(ref acceptedFrames) + 1;
            var provenance = new WindowFrameProvenance
            {
                SessionId = Binding.SessionId,
                CaptureId = Binding.CaptureId,
                CaptureEpoch = Binding.Epoch,
                FrameSequence = sequence,
                SourceId = Binding.SourceId,
                SourceEnumerationRevision = Binding.SourceEnumerationRevision,
                DestinationId = Binding.DestinationId,
                DestinationRevision = Binding.DestinationRevision,
                DestinationKind = destination.Kind,
                ConfigurationRevision = Binding.ConfigurationRevision,
                CapturedAtUtc = nativeFrame.CapturedAtUtc,
                FreshUntilUtc = nativeFrame.CapturedAtUtc + options.MaximumFrameAge,
                Width = nativeFrame.Width,
                Height = nativeFrame.Height,
                ByteCount = nativeFrame.ByteCount,
                Format = nativeFrame.Format
            };
            candidate = new(provenance, acceptedTimestamp,
                provenance.FreshUntilUtc - acceptedAtUtc, pixels);
            pixels = [];

            lock (gate)
            {
                ThrowIfStopped();
                DiscardLatestFrame();
                latest = candidate;
                candidate = null;
                Interlocked.Exchange(ref acceptedFrames, sequence);
                ArmFrameExpiry(latest, acceptedAtUtc);
            }

            return true;
        }
        finally
        {
            candidate?.Dispose();
            CryptographicOperations.ZeroMemory(pixels);
        }
    }

    private void ValidateFrame(
        NativeWindowFrame frame,
        DateTimeOffset now)
    {
        PerceptionGuard.Require(Enum.IsDefined(frame.Format) &&
            frame.Format == WindowFrameFormat.Bgra32Premultiplied,
            PerceptionFailureCode.MalformedFrame);
        PerceptionGuard.Require(frame.Width > 0 && frame.Height > 0,
            PerceptionFailureCode.MalformedFrame);
        PerceptionGuard.Require(frame.Width <= options.MaximumLongestEdge &&
            frame.Height <= options.MaximumLongestEdge,
            PerceptionFailureCode.PayloadTooLarge);

        int expected;
        try
        {
            expected = checked(frame.Width * frame.Height * 4);
        }
        catch (OverflowException)
        {
            throw new PerceptionException(PerceptionFailureCode.PayloadTooLarge);
        }

        PerceptionGuard.Require(frame.ByteCount <= options.MaximumFrameBytes,
            PerceptionFailureCode.PayloadTooLarge);
        PerceptionGuard.Require(frame.ByteCount == expected,
            PerceptionFailureCode.MalformedFrame);
        PerceptionGuard.Utc(frame.CapturedAtUtc);
        PerceptionGuard.Require(frame.CapturedAtUtc <= now,
            PerceptionFailureCode.MalformedFrame);
    }

    private void Check()
    {
        ObserveRevocation();
        lock (gate)
            ThrowIfStopped();
    }

    private void ObserveRevocation()
    {
        if (callerToken.IsCancellationRequested)
        {
            Stop(new(PerceptionFailureCode.Canceled));
            return;
        }

        try
        {
            authorization.Check();
        }
        catch (PerceptionException exception)
        {
            Stop(exception.Failure);
            return;
        }

        if (clock.GetElapsedTime(startedAt) >= Binding.SessionLifetime)
            Stop(new(PerceptionFailureCode.SessionTimeout));
    }

    private void ThrowIfStopped()
    {
        if ((stopFailure ?? workFailure) is { } failure)
            throw new PerceptionException(failure.Code);
    }

    private void RecordWorkFailure(PerceptionFailure failure)
    {
        lock (gate)
        {
            workFailure ??= failure;
            DiscardLatestFrame();
        }
    }

    private bool HasStopFailure()
    {
        lock (gate)
            return stopFailure is not null;
    }

    private void DiscardStaleFrame()
    {
        if (latest is null || latest.IsFresh(clock))
            return;
        DiscardLatestFrame();
        droppedFrames++;
        if (finished)
            _ = ReleaseLifetimeResourcesAsync();
    }

    private void DiscardLatestFrame()
    {
        frameExpiryTimer?.Dispose();
        frameExpiryTimer = null;
        latest?.Dispose();
        latest = null;
    }

    private void ArmFrameExpiry(
        OwnedWindowFrame frame,
        DateTimeOffset observedAtUtc)
    {
        var freshnessRemaining =
            frame.Provenance.FreshUntilUtc - observedAtUtc;
        var permissionRemaining =
            authorization.ExpiresAtUtc - observedAtUtc;
        var sessionRemaining =
            Binding.SessionLifetime - clock.GetElapsedTime(startedAt);
        var due = new[]
        {
            freshnessRemaining,
            permissionRemaining,
            sessionRemaining
        }.Min();
        PerceptionGuard.Require(due > TimeSpan.Zero,
            PerceptionFailureCode.StaleFrame);
        frameExpiryTimer = clock.CreateTimer(
            _ => ExpireRetainedFrame(frame),
            null,
            due,
            Timeout.InfiniteTimeSpan);
    }

    private void ExpireRetainedFrame(OwnedWindowFrame expected)
    {
        ObserveRevocation();
        var releaseLifetime = false;
        lock (gate)
        {
            if (ReferenceEquals(latest, expected) &&
                !latest.IsFresh(clock))
            {
                DiscardLatestFrame();
                droppedFrames++;
            }
            releaseLifetime = finished && latest is null;
        }
        if (releaseLifetime)
            _ = ReleaseLifetimeResourcesAsync();
    }

    private WindowCaptureOwnershipState OwnershipState() =>
        !finished
            ? WindowCaptureOwnershipState.Pending
            : releaseFailure is null
                ? WindowCaptureOwnershipState.Released
                : WindowCaptureOwnershipState.Quarantined;

    private static WindowCaptureOutcome OutcomeFor(PerceptionFailure failure) =>
        failure.Code is PerceptionFailureCode.Canceled or
            PerceptionFailureCode.DeadlineExceeded or
            PerceptionFailureCode.SourceSelectionChanged or
            PerceptionFailureCode.DestinationChanged or
            PerceptionFailureCode.ConfigurationChanged or
            PerceptionFailureCode.Paused or
            PerceptionFailureCode.Locked or
            PerceptionFailureCode.SessionTimeout or
            PerceptionFailureCode.Disposed
            ? WindowCaptureOutcome.Canceled
            : WindowCaptureOutcome.Failed;

    private async Task RetireAsync(Task worker)
    {
        try
        {
            await worker.ConfigureAwait(false);
        }
        catch (Exception)
        {
            RecordWorkFailure(new(PerceptionFailureCode.NativeCaptureFailed));
        }

        PerceptionFailure? failure;
        bool retainObservers;
        lock (gate)
        {
            failure = releaseFailure ?? stopFailure ?? workFailure;
            retainObservers = failure is null && captured && latest is not null;
        }

        if (!retainObservers)
            await ReleaseLifetimeResourcesAsync().ConfigureAwait(false);

        bool releaseLifetimeAfterRetire;
        lock (gate)
        {
            finished = true;
            backend = null;
            failure = releaseFailure ?? stopFailure ?? workFailure;
            if (failure is not null)
                DiscardLatestFrame();
            ownershipRelease.TrySetResult(new(
                releaseFailure is null,
                releaseFailure));
            if (terminal is null)
            {
                terminal = new(
                    failure is null && captured
                        ? WindowCaptureOutcome.Captured
                        : failure is null
                            ? WindowCaptureOutcome.Failed
                            : OutcomeFor(failure),
                    OwnershipState(),
                    failure ?? (captured
                        ? null
                        : new PerceptionFailure(
                            PerceptionFailureCode.NativeCaptureFailed)));
                completion.TrySetResult(terminal);
            }
            releaseLifetimeAfterRetire = latest is null;
        }
        if (releaseLifetimeAfterRetire)
            await ReleaseLifetimeResourcesAsync().ConfigureAwait(false);
    }

    private async Task ReleaseLifetimeResourcesAsync()
    {
        await Task.Yield();
        if (Interlocked.CompareExchange(
            ref lifetimeCleanupStarted,
            1,
            0) != 0)
        {
            await lifetimeRelease.Task.ConfigureAwait(false);
            return;
        }

        PerceptionFailure? failure = null;
        ITimer? watcher;
        lock (gate)
        {
            watcher = authorizationWatcher;
            authorizationWatcher = null;
        }

        if (watcher is not null)
        {
            try
            {
                await watcher.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                failure = new(PerceptionFailureCode.CleanupFailed);
            }
        }

        try
        {
            await callerCancellation.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            failure ??= new(PerceptionFailureCode.CleanupFailed);
        }

        try
        {
            stopSource.Dispose();
        }
        catch (Exception)
        {
            failure ??= new(PerceptionFailureCode.CleanupFailed);
        }

        lock (gate)
        {
            releaseFailure ??= failure;
            lifetimeRelease.TrySetResult(failure is null);
        }
    }
}
