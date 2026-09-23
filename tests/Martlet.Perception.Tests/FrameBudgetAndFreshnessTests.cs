using static Martlet.Perception.Tests.CaptureFixtures;

namespace Martlet.Perception.Tests;

public sealed class FrameBudgetAndFreshnessTests
{
    [Fact]
    public async Task ValidSingleFrameHasBoundedFreshProvenance()
    {
        var factory = new ControlledNativeFactory();
        var session = new ControlledNativeSession();
        var clock = new ManualCaptureClock();
        var pixels = Enumerable.Repeat((byte)0x44, 16 * 9 * 4).ToArray();
        session.Frames.Enqueue(new(16, 9, pixels, clock.GetUtcNow()));
        factory.NewSession = () => session;
        var options = Options();
        await using var capture = Owner(factory, clock, options);
        var source = Enumerate(capture, clock);
        var destination = RemoteDestination();

        var operation = capture.Start(
            source,
            destination,
            Authorize(source, destination, clock, options));
        var completed = await operation.Completion.WaitAsync(Wait);
        var released = await operation.OwnershipRelease.WaitAsync(Wait);
        var frame = Assert.IsType<WindowFrameProvenance>(
            operation.LatestFrame);

        Assert.Equal(WindowCaptureOutcome.Captured, completed.Outcome);
        Assert.True(released.Released);
        Assert.Equal(source.Id, frame.SourceId);
        Assert.Equal(source.EnumerationRevision, frame.SourceEnumerationRevision);
        Assert.Equal(destination.Id, frame.DestinationId);
        Assert.Equal(destination.Revision, frame.DestinationRevision);
        Assert.Equal(ConfigurationRevision, frame.ConfigurationRevision);
        Assert.Equal(16, frame.Width);
        Assert.Equal(9, frame.Height);
        Assert.Equal(16 * 9 * 4, frame.ByteCount);
        Assert.Equal(clock.GetUtcNow(), frame.CapturedAtUtc);
        Assert.Equal(
            frame.CapturedAtUtc + options.MaximumFrameAge,
            frame.FreshUntilUtc);
        Assert.All(pixels, value => Assert.Equal(0, value));
        Assert.Equal(1, operation.Snapshot.AcceptedFrames);
        Assert.Equal(0, operation.Snapshot.DroppedFrames);
    }

    [Theory]
    [InlineData("zero-width", PerceptionFailureCode.MalformedFrame)]
    [InlineData("wrong-length", PerceptionFailureCode.MalformedFrame)]
    [InlineData("future", PerceptionFailureCode.MalformedFrame)]
    [InlineData("undefined-format", PerceptionFailureCode.MalformedFrame)]
    [InlineData("edge", PerceptionFailureCode.PayloadTooLarge)]
    [InlineData("bytes", PerceptionFailureCode.PayloadTooLarge)]
    public async Task MalformedOrOversizedFrameFailsAndZerosNativeBytes(
        string fault,
        PerceptionFailureCode expected)
    {
        var factory = new ControlledNativeFactory();
        var session = new ControlledNativeSession();
        var clock = new ManualCaptureClock();
        var width = fault switch
        {
            "zero-width" => 0,
            "edge" => 1281,
            "bytes" => 600,
            _ => 16
        };
        var height = fault == "bytes" ? 600 : 9;
        var required = Math.Max(4, checked(Math.Max(1, width) * height * 4));
        var pixels = new byte[fault == "wrong-length" ? required - 1 : required];
        Array.Fill(pixels, (byte)0x7c);
        var capturedAt = fault == "future"
            ? clock.GetUtcNow().AddMilliseconds(1)
            : clock.GetUtcNow();
        var format = fault == "undefined-format"
            ? (WindowFrameFormat)42
            : WindowFrameFormat.Bgra32Premultiplied;
        session.Frames.Enqueue(new(
            width,
            height,
            pixels,
            capturedAt,
            format));
        factory.NewSession = () => session;
        var options = Options();
        await using var capture = Owner(factory, clock, options);
        var source = Enumerate(capture, clock);
        var destination = RemoteDestination();

        var operation = capture.Start(
            source,
            destination,
            Authorize(source, destination, clock, options));
        var completed = await operation.Completion.WaitAsync(Wait);

        Assert.Equal(WindowCaptureOutcome.Failed, completed.Outcome);
        Assert.Equal(expected, completed.Failure!.Code);
        Assert.True(
            (await operation.OwnershipRelease.WaitAsync(Wait)).Released);
        Assert.Null(operation.LatestFrame);
        Assert.All(pixels, value => Assert.Equal(0, value));
    }

    [Fact]
    public async Task StaleNativeFrameIsDroppedInsteadOfBecomingContext()
    {
        var factory = new ControlledNativeFactory();
        var session = new ControlledNativeSession();
        var clock = new ManualCaptureClock();
        var pixels = Enumerable.Repeat((byte)0x31, 16 * 9 * 4).ToArray();
        session.Frames.Enqueue(new(
            16,
            9,
            pixels,
            clock.GetUtcNow().AddSeconds(-6)));
        factory.NewSession = () => session;
        var options = Options();
        await using var capture = Owner(factory, clock, options);
        var source = Enumerate(capture, clock);
        var destination = RemoteDestination();
        var operation = capture.Start(
            source,
            destination,
            Authorize(source, destination, clock, options));

        await Until(() => operation.Snapshot.DroppedFrames == 1);
        Assert.Equal(1, session.Reads);
        Assert.Null(operation.LatestFrame);
        Assert.Equal(0, operation.Snapshot.AcceptedFrames);
        Assert.Equal(1, operation.Snapshot.DroppedFrames);
        Assert.All(pixels, value => Assert.Equal(0, value));
        Assert.Equal(
            WindowCaptureOutcome.Canceled,
            (await operation.Cancel().WaitAsync(Wait)).Outcome);
        Assert.True(
            (await operation.OwnershipRelease.WaitAsync(Wait)).Released);
    }

    [Fact]
    public async Task ContinuousCaptureDoesNotReadAboveOneFramePerSecond()
    {
        var factory = new ControlledNativeFactory();
        var session = new ControlledNativeSession();
        var clock = new ManualCaptureClock();
        session.Frames.Enqueue(Frame(clock, fill: 1));
        session.Frames.Enqueue(Frame(clock, fill: 2));
        session.Frames.Enqueue(Frame(clock, fill: 3));
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

        await Until(() => operation.Snapshot.AcceptedFrames == 1);
        Assert.Equal(1, session.Reads);
        clock.Advance(TimeSpan.FromMilliseconds(999));
        await Task.Delay(10);
        Assert.Equal(1, session.Reads);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        await Until(() => operation.Snapshot.AcceptedFrames == 2);
        Assert.Equal(2, session.Reads);
        clock.Advance(TimeSpan.FromSeconds(1));
        await Until(() => operation.Snapshot.AcceptedFrames == 3);
        Assert.Equal(3, session.Reads);

        await operation.Cancel().WaitAsync(Wait);
        Assert.True(
            (await operation.OwnershipRelease.WaitAsync(Wait)).Released);
    }

    [Fact]
    public async Task SessionAndOriginalPermissionClocksStopWithoutTimerReliance()
    {
        var factory = new ControlledNativeFactory();
        var session = new ControlledNativeSession();
        var clock = new ManualCaptureClock();
        session.Frames.Enqueue(Frame(clock));
        factory.NewSession = () => session;
        var options = Options(WindowCaptureMode.Continuous) with
        {
            SessionLifetime = TimeSpan.FromSeconds(3),
            MaximumFrameAge = TimeSpan.FromSeconds(2)
        };
        await using var capture = Owner(factory, clock, options);
        var source = Enumerate(capture, clock);
        var destination = RemoteDestination();
        var binding = Binding(
            source,
            destination,
            WindowCaptureMode.Continuous,
            lifetime: TimeSpan.FromSeconds(3));
        var authorization = Authorize(
            source,
            destination,
            clock,
            options,
            WindowCaptureMode.Continuous,
            permissionLifetime: TimeSpan.FromSeconds(5),
            binding: binding);
        var operation = capture.Start(
            source,
            destination,
            authorization);
        await Until(() => operation.Snapshot.AcceptedFrames == 1);

        clock.Advance(TimeSpan.FromSeconds(3), deliver: false);
        Assert.Equal(
            PerceptionFailureCode.SessionTimeout,
            operation.Snapshot.Failure!.Code);
        Assert.Equal(
            WindowCaptureOutcome.Canceled,
            (await operation.Completion.WaitAsync(Wait)).Outcome);
        Assert.True(
            (await operation.OwnershipRelease.WaitAsync(Wait)).Released);
        Assert.False(operation.Snapshot.HasFreshFrame);
    }

    [Theory]
    [InlineData("edge")]
    [InlineData("bytes")]
    [InlineData("rate")]
    [InlineData("lifetime")]
    [InlineData("freshness")]
    public void OptionsCannotExceedHardBudgets(string changed)
    {
        var options = Options() with
        {
            MaximumLongestEdge = changed == "edge" ? 1281 : 1280,
            MaximumFrameBytes = changed == "bytes"
                ? 1024 * 1024 + 1
                : 1024 * 1024,
            FrameInterval = changed == "rate"
                ? TimeSpan.FromMilliseconds(999)
                : TimeSpan.FromSeconds(1),
            SessionLifetime = changed == "lifetime"
                ? TimeSpan.FromMinutes(5).Add(TimeSpan.FromTicks(1))
                : TimeSpan.FromSeconds(10),
            MaximumFrameAge = changed == "freshness"
                ? TimeSpan.FromSeconds(5).Add(TimeSpan.FromTicks(1))
                : TimeSpan.FromSeconds(5)
        };

        Assert.Equal(
            PerceptionFailureCode.InvalidInput,
            Failure(options.Validate));
    }

    [Fact]
    public async Task OperationRestartCannotBypassHardFrameRate()
    {
        var firstSession = new ControlledNativeSession();
        var secondSession = new ControlledNativeSession();
        var factory = new ControlledNativeFactory();
        var clock = new ManualCaptureClock();
        firstSession.Frames.Enqueue(Frame(clock, fill: 1));
        secondSession.Frames.Enqueue(Frame(clock, fill: 2));
        factory.NewSession = () => firstSession;
        var options = Options();
        await using var capture = Owner(factory, clock, options);
        var source = Enumerate(capture, clock);
        var destination = RemoteDestination();
        var first = capture.Start(
            source,
            destination,
            Authorize(source, destination, clock, options));
        await first.Completion.WaitAsync(Wait);
        await first.OwnershipRelease.WaitAsync(Wait);
        factory.NewSession = () => secondSession;
        var next = Authorize(
            source,
            destination,
            clock,
            options,
            epoch: 1);

        Assert.Equal(
            PerceptionFailureCode.RateLimited,
            Failure(() => capture.Start(
                source,
                destination,
                next)));
        Assert.False(next.IsConsumed);
        Assert.Equal(1, factory.Opens);

        clock.Advance(TimeSpan.FromSeconds(1));
        var second = capture.Start(source, destination, next);
        Assert.Equal(
            WindowCaptureOutcome.Captured,
            (await second.Completion.WaitAsync(Wait)).Outcome);
        Assert.True(
            (await second.OwnershipRelease.WaitAsync(Wait)).Released);
        Assert.Equal(2, factory.Opens);
    }
}
