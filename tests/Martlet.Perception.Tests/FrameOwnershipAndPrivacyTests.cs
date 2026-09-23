using System.Reflection;
using static Martlet.Perception.Tests.CaptureFixtures;

namespace Martlet.Perception.Tests;

public sealed class FrameOwnershipAndPrivacyTests
{
    [Fact]
    public async Task PreviewAndRemoteDisclosureRequireSeparateOneUseAuthorizations()
    {
        var factory = new ControlledNativeFactory();
        var session = new ControlledNativeSession();
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
        await operation.Completion.WaitAsync(Wait);
        await operation.OwnershipRelease.WaitAsync(Wait);
        var metadata = operation.LatestFrame!;

        var deniedPreview = new WindowPreviewAuthorization(
            metadata,
            clock.GetUtcNow().AddSeconds(1),
            timeProvider: clock);
        Assert.Equal(
            PerceptionFailureCode.AuthorizationRequired,
            Failure(() => operation.OpenPreview(deniedPreview)));
        Assert.False(deniedPreview.IsConsumed);

        var previewAuthorization = new WindowPreviewAuthorization(
            metadata,
            clock.GetUtcNow().AddSeconds(1),
            localPreviewRequested: true,
            clock);
        using (var preview = operation.OpenPreview(previewAuthorization))
        {
            var copy = new byte[metadata.ByteCount];
            preview.CopyPixelsTo(copy);
            Assert.Contains(copy, value => value != 0);
        }
        Assert.True(previewAuthorization.IsConsumed);
        Assert.Equal(
            PerceptionFailureCode.AuthorizationConsumed,
            Failure(() => operation.OpenPreview(previewAuthorization)));

        var deniedDisclosure = new WindowDisclosureAuthorization(
            metadata,
            destination,
            clock.GetUtcNow().AddSeconds(1),
            timeProvider: clock);
        Assert.Equal(
            PerceptionFailureCode.AuthorizationRequired,
            Failure(() => operation.OpenDisclosure(deniedDisclosure)));
        Assert.False(deniedDisclosure.IsConsumed);

        var disclosureAuthorization = new WindowDisclosureAuthorization(
            metadata,
            destination,
            clock.GetUtcNow().AddSeconds(1),
            remoteDisclosureRequested: true,
            clock);
        using var disclosure = operation.OpenDisclosure(
            disclosureAuthorization);
        Assert.Same(destination, disclosure.Destination);
        var disclosed = new byte[metadata.ByteCount];
        disclosure.CopyPixelsTo(disclosed);
        Assert.Contains(disclosed, value => value != 0);
        Assert.True(disclosureAuthorization.IsConsumed);
    }

    [Fact]
    public async Task LocalPreviewDestinationCannotAuthorizeRemoteDisclosure()
    {
        var factory = new ControlledNativeFactory();
        var session = new ControlledNativeSession();
        var clock = new ManualCaptureClock();
        session.Frames.Enqueue(Frame(clock));
        factory.NewSession = () => session;
        var options = Options();
        await using var capture = Owner(factory, clock, options);
        var source = Enumerate(capture, clock);
        var destination = LocalDestination();
        var operation = capture.Start(
            source,
            destination,
            Authorize(source, destination, clock, options));
        await operation.Completion.WaitAsync(Wait);
        await operation.OwnershipRelease.WaitAsync(Wait);
        var metadata = operation.LatestFrame!;
        var authorization = new WindowDisclosureAuthorization(
            metadata,
            destination,
            clock.GetUtcNow().AddSeconds(1),
            remoteDisclosureRequested: true,
            clock);

        Assert.Equal(
            PerceptionFailureCode.InvalidBinding,
            Failure(() => operation.OpenDisclosure(authorization)));
        Assert.False(authorization.IsConsumed);
    }

    [Fact]
    public async Task NewestFrameZerosAndInvalidatesOlderBorrowedFrame()
    {
        var factory = new ControlledNativeFactory();
        var session = new ControlledNativeSession();
        var clock = new ManualCaptureClock();
        session.Frames.Enqueue(Frame(clock, fill: 0x21));
        session.Frames.Enqueue(Frame(
            clock,
            fill: 0x45,
            capturedAtUtc: clock.GetUtcNow().AddSeconds(1)));
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
        await Until(() => operation.LatestFrame?.FrameSequence == 1);
        var firstMetadata = operation.LatestFrame!;
        using var preview = operation.OpenPreview(new(
            firstMetadata,
            clock.GetUtcNow().AddSeconds(1),
            localPreviewRequested: true,
            clock));
        var ownedBytes = GetOwnedPixels(preview);
        Assert.All(ownedBytes, value => Assert.Equal(0x21, value));

        clock.Advance(TimeSpan.FromSeconds(1));
        await Until(() => operation.LatestFrame?.FrameSequence == 2);

        Assert.All(ownedBytes, value => Assert.Equal(0, value));
        Assert.Equal(
            PerceptionFailureCode.InvalidState,
            Failure(() => preview.CopyPixelsTo(
                new byte[preview.ByteCount])));
        Assert.Equal(2, operation.LatestFrame!.FrameSequence);
        Assert.Equal(2, operation.Snapshot.AcceptedFrames);
        Assert.Equal(1, operation.Snapshot.RetainedFrameBytes > 0 ? 1 : 0);
        await operation.Cancel().WaitAsync(Wait);
        await operation.OwnershipRelease.WaitAsync(Wait);
    }

    [Fact]
    public async Task AuthorizationForRacedFrameCannotOpenNewLatestFrame()
    {
        var factory = new ControlledNativeFactory();
        var session = new ControlledNativeSession();
        var clock = new ManualCaptureClock();
        session.Frames.Enqueue(Frame(clock, fill: 1));
        session.Frames.Enqueue(Frame(
            clock,
            fill: 2,
            capturedAtUtc: clock.GetUtcNow().AddSeconds(1)));
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
        await Until(() => operation.LatestFrame?.FrameSequence == 1);
        var staleAuthorization = new WindowPreviewAuthorization(
            operation.LatestFrame!,
            clock.GetUtcNow().AddSeconds(2),
            localPreviewRequested: true,
            clock);

        clock.Advance(TimeSpan.FromSeconds(1));
        await Until(() => operation.LatestFrame?.FrameSequence == 2);

        Assert.Equal(
            PerceptionFailureCode.InvalidBinding,
            Failure(() => operation.OpenPreview(staleAuthorization)));
        Assert.False(staleAuthorization.IsConsumed);
        Assert.True(operation.Snapshot.HasFreshFrame);
        await operation.Cancel().WaitAsync(Wait);
        await operation.OwnershipRelease.WaitAsync(Wait);
    }

    [Fact]
    public async Task AgingLatestFrameIsZeroedAndCannotBePreviewed()
    {
        var factory = new ControlledNativeFactory();
        var session = new ControlledNativeSession();
        var clock = new ManualCaptureClock();
        session.Frames.Enqueue(Frame(clock, fill: 0x61));
        factory.NewSession = () => session;
        var options = Options() with
        {
            MaximumFrameAge = TimeSpan.FromSeconds(2)
        };
        await using var capture = Owner(factory, clock, options);
        var source = Enumerate(capture, clock);
        var destination = RemoteDestination();
        var operation = capture.Start(
            source,
            destination,
            Authorize(source, destination, clock, options));
        await operation.Completion.WaitAsync(Wait);
        await operation.OwnershipRelease.WaitAsync(Wait);
        var metadata = operation.LatestFrame!;
        using var preview = operation.OpenPreview(new(
            metadata,
            clock.GetUtcNow().AddSeconds(1),
            localPreviewRequested: true,
            clock));
        var ownedBytes = GetOwnedPixels(preview);

        clock.Advance(TimeSpan.FromSeconds(2));
        await Until(() => ownedBytes.All(value => value == 0));

        Assert.Null(operation.LatestFrame);
        Assert.All(ownedBytes, value => Assert.Equal(0, value));
        Assert.Equal(
            PerceptionFailureCode.InvalidState,
            Failure(() => preview.CopyPixelsTo(
                new byte[preview.ByteCount])));
        var freshAuthorization = new WindowPreviewAuthorization(
            metadata,
            clock.GetUtcNow().AddSeconds(1),
            localPreviewRequested: true,
            clock);
        Assert.Equal(
            PerceptionFailureCode.FrameUnavailable,
            Failure(() => operation.OpenPreview(freshAuthorization)));
        Assert.False(freshAuthorization.IsConsumed);
    }

    [Fact]
    public async Task PixelSourceAndNativeExceptionCanariesNeverEnterMetadataOrErrors()
    {
        var factory = new ControlledNativeFactory();
        factory.Sources.Clear();
        factory.Sources.Add(new(Canary, Canary));
        var session = new ControlledNativeSession
        {
            ReadFailure = new IOException(Canary)
        };
        var clock = new ManualCaptureClock();
        factory.NewSession = () => session;
        var options = Options();
        await using var capture = Owner(factory, clock, options);
        var source = Enumerate(capture, clock);
        var destination = new CaptureDestination(
            Guid.NewGuid(),
            Guid.NewGuid(),
            CaptureDestinationKind.RemoteProcessor,
            Canary);
        var operation = capture.Start(
            source,
            destination,
            Authorize(source, destination, clock, options));
        var terminal = await operation.Completion.WaitAsync(Wait);

        Assert.Equal(
            PerceptionFailureCode.NativeCaptureFailed,
            terminal.Failure!.Code);
        Assert.DoesNotContain(
            Canary,
            terminal.ToString(),
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            Canary,
            operation.Snapshot.ToString(),
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            Canary,
            source.ToString(),
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            Canary,
            destination.ToString(),
            StringComparison.Ordinal);
        Assert.Null(operation.LatestFrame);
    }

    [Theory]
    [InlineData("authorization")]
    [InlineData("caller")]
    public async Task CompletedSingleFrameStillObservesOriginalRevocation(
        string cause)
    {
        using var caller = new CancellationTokenSource();
        var factory = new ControlledNativeFactory();
        var session = new ControlledNativeSession();
        var clock = new ManualCaptureClock();
        session.Frames.Enqueue(Frame(clock, fill: 0x37));
        factory.NewSession = () => session;
        var options = Options();
        await using var capture = Owner(factory, clock, options);
        var source = Enumerate(capture, clock);
        var destination = RemoteDestination();
        var captureAuthorization = Authorize(
            source,
            destination,
            clock,
            options);
        var operation = capture.Start(
            source,
            destination,
            captureAuthorization,
            caller.Token);
        Assert.Equal(
            WindowCaptureOutcome.Captured,
            (await operation.Completion.WaitAsync(Wait)).Outcome);
        Assert.True(
            (await operation.OwnershipRelease.WaitAsync(Wait)).Released);
        using var preview = operation.OpenPreview(new(
            operation.LatestFrame!,
            clock.GetUtcNow().AddSeconds(1),
            localPreviewRequested: true,
            clock));
        var ownedBytes = GetOwnedPixels(preview);

        if (cause == "authorization")
        {
            captureAuthorization.Revoke();
            clock.Advance(TimeSpan.FromMilliseconds(20));
        }
        else
        {
            caller.Cancel();
        }

        await Until(() => ownedBytes.All(value => value == 0));
        Assert.Equal(
            PerceptionFailureCode.Canceled,
            operation.Snapshot.Failure!.Code);
        Assert.False(operation.Snapshot.HasFreshFrame);
        Assert.Equal(
            PerceptionFailureCode.InvalidState,
            Failure(() => preview.CopyPixelsTo(
                new byte[preview.ByteCount])));
        Assert.True(
            await operation.LifetimeRelease.WaitAsync(Wait));
    }

    [Theory]
    [InlineData(false, "capture-revoked")]
    [InlineData(true, "capture-revoked")]
    [InlineData(false, "lease-revoked")]
    [InlineData(true, "lease-revoked")]
    [InlineData(false, "lease-expired")]
    [InlineData(true, "lease-expired")]
    [InlineData(false, "frame-expired")]
    [InlineData(true, "frame-expired")]
    [InlineData(false, "capture-expired")]
    [InlineData(true, "capture-expired")]
    public async Task RetainedLeaseRechecksPermissionAndFreshnessWithoutTimers(
        bool disclose,
        string cause)
    {
        var clock = new ManualCaptureClock();
        var session = new ControlledNativeSession();
        session.Frames.Enqueue(Frame(clock));
        var factory = new ControlledNativeFactory { NewSession = () => session };
        var options = Options();
        await using var capture = Owner(factory, clock, options);
        var source = Enumerate(capture, clock);
        var destination = RemoteDestination();
        var permission = Authorize(source, destination, clock, options,
            permissionLifetime: TimeSpan.FromSeconds(
                cause == "capture-expired" ? 3 : 10));
        var operation = capture.Start(source, destination, permission);
        await operation.Completion.WaitAsync(Wait);
        var provenance = operation.LatestFrame!;
        var previewPermission = new WindowPreviewAuthorization(
            provenance, clock.GetUtcNow().AddSeconds(2), true, clock);
        var disclosurePermission = new WindowDisclosureAuthorization(
            provenance, destination, clock.GetUtcNow().AddSeconds(2), true, clock);
        using WindowFrameLease lease = disclose
            ? operation.OpenDisclosure(disclosurePermission)
            : operation.OpenPreview(previewPermission);
        var owned = GetOwnedPixels(lease);
        var output = new byte[lease.ByteCount];

        if (cause == "capture-revoked")
            permission.Revoke();
        else if (cause == "lease-revoked")
        {
            previewPermission.Revoke();
            disclosurePermission.Revoke();
        }
        else
            clock.Advance(TimeSpan.FromSeconds(cause switch
            {
                "lease-expired" => 2,
                "capture-expired" => 3,
                _ => 5
            }), deliver: false);

        Assert.Throws<PerceptionException>(() => lease.CopyPixelsTo(output));
        Assert.All(output, value => Assert.Equal(0, value));
        if (cause is not ("lease-revoked" or "lease-expired"))
            Assert.All(owned, value => Assert.Equal(0, value));
    }

    [Fact]
    public async Task UtcRollbackCannotExtendRemainingFreshnessOfAnOlderFrame()
    {
        var clock = new ManualCaptureClock();
        var session = new ControlledNativeSession();
        session.Frames.Enqueue(Frame(clock,
            capturedAtUtc: clock.GetUtcNow().AddSeconds(-4)));
        var factory = new ControlledNativeFactory { NewSession = () => session };
        var options = Options();
        await using var capture = Owner(factory, clock, options);
        var source = Enumerate(capture, clock);
        var destination = RemoteDestination();
        var operation = capture.Start(source, destination,
            Authorize(source, destination, clock, options));
        await operation.Completion.WaitAsync(Wait);
        using var lease = operation.OpenPreview(new(
            operation.LatestFrame!, clock.GetUtcNow().AddSeconds(3), true, clock));
        var owned = GetOwnedPixels(lease);

        clock.Advance(TimeSpan.FromSeconds(1), deliver: false);
        clock.ShiftUtc(TimeSpan.FromSeconds(-1));

        Assert.Null(operation.LatestFrame);
        Assert.All(owned, value => Assert.Equal(0, value));
    }

    [Theory]
    [InlineData(PerceptionFailureCode.AccessDenied)]
    [InlineData(PerceptionFailureCode.SourceClosed)]
    [InlineData(PerceptionFailureCode.SourceChanged)]
    public async Task NativeFailureDiscardsPixelsBeforeBlockedCleanup(
        PerceptionFailureCode failure)
    {
        using var cleanup = new ManualResetEventSlim();
        var clock = new ManualCaptureClock();
        var session = new ControlledNativeSession { DisposeBlock = cleanup };
        session.Frames.Enqueue(Frame(clock));
        var factory = new ControlledNativeFactory { NewSession = () => session };
        var options = Options(WindowCaptureMode.Continuous);
        await using var capture = Owner(factory, clock, options);
        var source = Enumerate(capture, clock);
        var destination = RemoteDestination();
        var operation = capture.Start(source, destination,
            Authorize(source, destination, clock, options, WindowCaptureMode.Continuous));
        Task? advance = null;
        try
        {
            await Until(() => operation.LatestFrame is not null);
            var metadata = operation.LatestFrame!;
            using var lease = operation.OpenPreview(new(
                metadata, clock.GetUtcNow().AddSeconds(5), true, clock));
            var owned = GetOwnedPixels(lease);
            session.ReadFailure = new NativeWindowCaptureException(failure);
            advance = Task.Run(() => clock.Advance(TimeSpan.FromSeconds(1)));
            await Until(() => session.DisposeEntered.IsSet);

            Assert.False(operation.OwnershipRelease.IsCompleted);
            Assert.Equal(failure, operation.Snapshot.Failure?.Code);
            Assert.False(operation.Snapshot.HasFreshFrame);
            Assert.All(owned, value => Assert.Equal(0, value));
            Assert.Equal(PerceptionFailureCode.InvalidState,
                Failure(() => lease.CopyPixelsTo(new byte[lease.ByteCount])));
            Assert.Equal(failure, Failure(() => operation.OpenDisclosure(new(
                metadata, destination, clock.GetUtcNow().AddSeconds(1), true, clock))));
        }
        finally
        {
            cleanup.Set();
            if (advance is not null)
                await advance.WaitAsync(Wait);
            await operation.OwnershipRelease.WaitAsync(Wait);
        }
    }

    private static byte[] GetOwnedPixels(WindowFrameLease lease)
    {
        var frame = typeof(WindowFrameLease)
            .GetField("frame", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(lease)!;
        return (byte[])frame.GetType()
            .GetField("pixels", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(frame)!;
    }
}
