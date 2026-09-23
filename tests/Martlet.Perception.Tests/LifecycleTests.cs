using static Martlet.Perception.Tests.CaptureFixtures;

namespace Martlet.Perception.Tests;

public sealed class LifecycleTests
{
    [Theory]
    [InlineData("pause", PerceptionFailureCode.Paused)]
    [InlineData("lock", PerceptionFailureCode.Locked)]
    [InlineData("configuration", PerceptionFailureCode.ConfigurationChanged)]
    [InlineData("destination", PerceptionFailureCode.DestinationChanged)]
    [InlineData("source", PerceptionFailureCode.SourceSelectionChanged)]
    public async Task LifecycleChangeStopsAndDiscardsLatestFrame(
        string change,
        PerceptionFailureCode expected)
    {
        var factory = new ControlledNativeFactory();
        var session = new ControlledNativeSession();
        var clock = new ManualCaptureClock();
        session.Frames.Enqueue(Frame(clock));
        factory.NewSession = () => session;
        var options = Options(WindowCaptureMode.Continuous);
        await using var capture = Owner(factory, clock, options);
        var source = Enumerate(capture, clock);
        var destination = RemoteDestination();
        var operation = capture.Start(
            source,
            destination,
            Authorize(
                source,
                destination,
                clock,
                options,
                WindowCaptureMode.Continuous));
        await Until(() => operation.LatestFrame is not null);
        var metadata = operation.LatestFrame!;
        using var preview = operation.OpenPreview(new(
            metadata,
            clock.GetUtcNow().AddSeconds(1),
            localPreviewRequested: true,
            clock));

        WindowCaptureCompletion? stopped = change switch
        {
            "pause" => await capture.SetPausedAsync(true),
            "lock" => await capture.SetSessionLockedAsync(true),
            "configuration" => await capture.NotifyConfigurationChangedAsync(
                Guid.NewGuid()),
            "destination" => await capture.NotifyDestinationChangedAsync(
                destination with
                {
                    Revision = Guid.NewGuid()
                }),
            _ => await capture.NotifySourceSelectionChangedAsync(null)
        };

        Assert.NotNull(stopped);
        Assert.Equal(expected, stopped.Failure!.Code);
        Assert.False(operation.Snapshot.HasFreshFrame);
        Assert.Equal(
            PerceptionFailureCode.InvalidState,
            Failure(() => preview.CopyPixelsTo(
                new byte[preview.ByteCount])));
        Assert.True(
            (await operation.OwnershipRelease.WaitAsync(Wait)).Released);
    }

    [Theory]
    [InlineData(
        PerceptionFailureCode.SourceClosed,
        WindowCaptureOutcome.Failed)]
    [InlineData(
        PerceptionFailureCode.SourceChanged,
        WindowCaptureOutcome.Failed)]
    [InlineData(
        PerceptionFailureCode.AccessDenied,
        WindowCaptureOutcome.Failed)]
    public async Task NativeSourceAndPrivacyFailuresNeverFallback(
        PerceptionFailureCode failure,
        WindowCaptureOutcome expected)
    {
        var factory = new ControlledNativeFactory();
        var session = new ControlledNativeSession
        {
            ReadFailure = new NativeWindowCaptureException(failure)
        };
        var clock = new ManualCaptureClock();
        factory.NewSession = () => session;
        var options = Options();
        await using var capture = Owner(factory, clock, options);
        var source = Enumerate(capture, clock);
        var destination = RemoteDestination();
        var operation = capture.Start(
            source,
            destination,
            Authorize(source, destination, clock, options));

        var terminal = await operation.Completion.WaitAsync(Wait);
        Assert.Equal(expected, terminal.Outcome);
        Assert.Equal(failure, terminal.Failure!.Code);
        Assert.Null(operation.LatestFrame);
        Assert.Equal(1, factory.Opens);
        Assert.True(
            (await operation.OwnershipRelease.WaitAsync(Wait)).Released);
    }

    [Fact]
    public async Task StopDoesNotReleaseBlockedNativeReadOrQueueReplacement()
    {
        using var read = new ManualResetEventSlim();
        var factory = new ControlledNativeFactory();
        var session = new ControlledNativeSession
        {
            ReadBlock = read
        };
        var clock = new ManualCaptureClock();
        factory.NewSession = () => session;
        var options = Options(WindowCaptureMode.Continuous);
        var capture = Owner(factory, clock, options);
        var source = Enumerate(capture, clock);
        var destination = RemoteDestination();
        var operation = capture.Start(
            source,
            destination,
            Authorize(
                source,
                destination,
                clock,
                options,
                WindowCaptureMode.Continuous));
        await Until(() => session.ReadEntered.IsSet);

        Assert.Equal(
            WindowCaptureOutcome.Canceled,
            (await capture.StopAsync())!.Outcome);
        Assert.False(operation.OwnershipRelease.IsCompleted);
        await Until(() => session.StopEntered.IsSet);
        var fresh = Authorize(
            source,
            destination,
            clock,
            options,
            WindowCaptureMode.Continuous,
            epoch: 1,
            authorizationRevision: capture.AuthorizationRevision);
        Assert.Equal(
            PerceptionFailureCode.Busy,
            Failure(() => capture.Start(
                source,
                destination,
                fresh)));
        Assert.False(fresh.IsConsumed);

        read.Set();
        Assert.True(
            (await operation.OwnershipRelease.WaitAsync(Wait)).Released);
        Assert.Equal(1, session.Stops);
        Assert.Equal(1, session.Disposals);
        await capture.DisposeAsync().AsTask().WaitAsync(Wait);
    }

    [Theory]
    [InlineData("permission", PerceptionFailureCode.DeadlineExceeded)]
    [InlineData("revoked", PerceptionFailureCode.Canceled)]
    [InlineData("caller", PerceptionFailureCode.Canceled)]
    public async Task OriginalPermissionAndCancellationRemainAuthoritative(
        string cause,
        PerceptionFailureCode expected)
    {
        using var caller = new CancellationTokenSource();
        var factory = new ControlledNativeFactory();
        var session = new ControlledNativeSession();
        var clock = new ManualCaptureClock();
        session.Frames.Enqueue(Frame(clock));
        factory.NewSession = () => session;
        var options = Options(WindowCaptureMode.Continuous);
        await using var capture = Owner(factory, clock, options);
        var source = Enumerate(capture, clock);
        var destination = RemoteDestination();
        var authorization = Authorize(
            source,
            destination,
            clock,
            options,
            WindowCaptureMode.Continuous,
            permissionLifetime: cause == "permission"
                ? TimeSpan.FromSeconds(1)
                : TimeSpan.FromSeconds(30));
        var operation = capture.Start(
            source,
            destination,
            authorization,
            caller.Token);
        await Until(() => operation.LatestFrame is not null);

        if (cause == "permission")
            clock.Advance(TimeSpan.FromSeconds(1), deliver: false);
        else if (cause == "revoked")
            authorization.Revoke();
        else
            caller.Cancel();

        Assert.Equal(expected, operation.Snapshot.Failure!.Code);
        Assert.Equal(
            WindowCaptureOutcome.Canceled,
            (await operation.Completion.WaitAsync(Wait)).Outcome);
        Assert.False(operation.Snapshot.HasFreshFrame);
        Assert.True(
            (await operation.OwnershipRelease.WaitAsync(Wait)).Released);
    }

    [Fact]
    public async Task FailedNativeCancellationQuarantinesOwner()
    {
        using var read = new ManualResetEventSlim();
        var factory = new ControlledNativeFactory();
        var session = new ControlledNativeSession
        {
            ReadBlock = read,
            StopFailure = new InvalidOperationException(Canary)
        };
        var clock = new ManualCaptureClock();
        factory.NewSession = () => session;
        var options = Options(WindowCaptureMode.Continuous);
        await using var capture = Owner(factory, clock, options);
        var source = Enumerate(capture, clock);
        var destination = RemoteDestination();
        var operation = capture.Start(
            source,
            destination,
            Authorize(
                source,
                destination,
                clock,
                options,
                WindowCaptureMode.Continuous));
        await Until(() => session.ReadEntered.IsSet);
        await operation.Cancel().WaitAsync(Wait);
        await Until(() => session.StopEntered.IsSet);
        read.Set();

        var release = await operation.OwnershipRelease.WaitAsync(Wait);
        Assert.False(release.Released);
        Assert.Equal(
            PerceptionFailureCode.CancellationFailed,
            release.Failure!.Code);
        Assert.Equal(
            WindowCaptureOwnershipState.Quarantined,
            operation.Snapshot.Ownership);
        Assert.DoesNotContain(
            Canary,
            release.ToString(),
            StringComparison.Ordinal);
        var fresh = Authorize(
            source,
            destination,
            clock,
            options,
            WindowCaptureMode.Continuous,
            epoch: 1);
        Assert.Equal(
            PerceptionFailureCode.Busy,
            Failure(() => capture.Start(
                source,
                destination,
                fresh)));
        Assert.False(fresh.IsConsumed);
    }

    [Fact]
    public async Task OwnerDisposalStopsCaptureAndZerosBorrowedFrame()
    {
        var factory = new ControlledNativeFactory();
        var session = new ControlledNativeSession();
        var clock = new ManualCaptureClock();
        session.Frames.Enqueue(Frame(clock, fill: 0x4d));
        factory.NewSession = () => session;
        var options = Options(WindowCaptureMode.Continuous);
        var capture = Owner(factory, clock, options);
        var source = Enumerate(capture, clock);
        var destination = RemoteDestination();
        var operation = capture.Start(
            source,
            destination,
            Authorize(
                source,
                destination,
                clock,
                options,
                WindowCaptureMode.Continuous));
        await Until(() => operation.LatestFrame is not null);
        using var preview = operation.OpenPreview(new(
            operation.LatestFrame!,
            clock.GetUtcNow().AddSeconds(1),
            localPreviewRequested: true,
            clock));

        await capture.DisposeAsync();

        Assert.Equal(
            PerceptionFailureCode.Disposed,
            operation.Snapshot.Failure!.Code);
        Assert.False(operation.Snapshot.HasFreshFrame);
        Assert.Equal(
            PerceptionFailureCode.InvalidState,
            Failure(() => preview.CopyPixelsTo(
                new byte[preview.ByteCount])));
        Assert.True(
            (await operation.OwnershipRelease.WaitAsync(Wait)).Released);
    }

    [Fact]
    public async Task StopDuringBlockedNativeDisposeRemainsOwned()
    {
        using var dispose = new ManualResetEventSlim();
        var factory = new ControlledNativeFactory();
        var session = new ControlledNativeSession
        {
            DisposeBlock = dispose
        };
        var clock = new ManualCaptureClock();
        session.Frames.Enqueue(Frame(clock));
        factory.NewSession = () => session;
        var options = Options();
        await using var capture = Owner(factory, clock, options);
        var source = Enumerate(capture, clock);
        var destination = RemoteDestination();
        var operation = capture.Start(
            source,
            destination,
            Authorize(source, destination, clock, options));
        try
        {
            await Until(() => session.DisposeEntered.IsSet);
            Assert.Equal(
                WindowCaptureOutcome.Canceled,
                (await operation.Cancel().WaitAsync(Wait)).Outcome);
            Assert.False(operation.OwnershipRelease.IsCompleted);
            var fresh = Authorize(
                source,
                destination,
                clock,
                options,
                epoch: 1);
            Assert.Equal(
                PerceptionFailureCode.Busy,
                Failure(() => capture.Start(
                    source,
                    destination,
                    fresh)));
            Assert.False(fresh.IsConsumed);
            dispose.Set();
            Assert.True(
                (await operation.OwnershipRelease.WaitAsync(Wait)).Released);
        }
        finally
        {
            dispose.Set();
        }
    }

    [Fact]
    public async Task CancellationCallbackMustRetireBeforeOwnershipRelease()
    {
        using var read = new ManualResetEventSlim();
        using var callback = new ManualResetEventSlim();
        var factory = new ControlledNativeFactory();
        var session = new ControlledNativeSession
        {
            ReadBlock = read,
            CancellationCallbackBlock = callback
        };
        var clock = new ManualCaptureClock();
        factory.NewSession = () => session;
        var options = Options(WindowCaptureMode.Continuous);
        await using var capture = Owner(factory, clock, options);
        var source = Enumerate(capture, clock);
        var destination = RemoteDestination();
        var operation = capture.Start(
            source,
            destination,
            Authorize(
                source,
                destination,
                clock,
                options,
                WindowCaptureMode.Continuous));
        try
        {
            await Until(() => session.ReadEntered.IsSet);
            await operation.Cancel().WaitAsync(Wait);
            await Until(() =>
                session.CancellationCallbackEntered.IsSet);
            read.Set();
            await Until(() => session.ReadReleased.IsSet);
            Assert.False(operation.OwnershipRelease.IsCompleted);
            Assert.False(session.DisposeEntered.IsSet);
            callback.Set();
            Assert.True(
                (await operation.OwnershipRelease.WaitAsync(Wait)).Released);
            Assert.True(session.DisposeEntered.IsSet);
        }
        finally
        {
            callback.Set();
            read.Set();
        }
    }
}
