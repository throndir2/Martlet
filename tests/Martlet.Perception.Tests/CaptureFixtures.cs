using System.Collections.Concurrent;

namespace Martlet.Perception.Tests;

internal sealed class ManualCaptureClock : TimeProvider
{
    private readonly object gate = new();
    private readonly List<ClockTimer> timers = [];
    private long ticks;
    private long utcShift;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp()
    {
        lock (gate)
            return ticks;
    }

    public override DateTimeOffset GetUtcNow()
    {
        lock (gate)
            return DateTimeOffset.UnixEpoch.AddYears(31).AddTicks(ticks + utcShift);
    }

    internal void ShiftUtc(TimeSpan amount)
    {
        lock (gate)
            utcShift += amount.Ticks;
    }

    internal void Advance(TimeSpan amount, bool deliver = true)
    {
        List<ClockTimer> due;
        lock (gate)
        {
            ticks += amount.Ticks;
            due = deliver
                ? timers.Where(timer => timer.Due <= ticks).ToList()
                : [];
            foreach (var timer in due)
            {
                timer.Due = timer.Period > 0
                    ? ticks + timer.Period
                    : long.MaxValue;
            }
        }

        foreach (var timer in due)
            timer.Callback(timer.State);
    }

    public override ITimer CreateTimer(
        TimerCallback callback,
        object? state,
        TimeSpan dueTime,
        TimeSpan period)
    {
        lock (gate)
        {
            var timer = new ClockTimer(this, callback, state);
            timers.Add(timer);
            timer.Change(dueTime, period);
            return timer;
        }
    }

    private sealed class ClockTimer(
        ManualCaptureClock owner,
        TimerCallback callback,
        object? state) : ITimer
    {
        internal TimerCallback Callback => callback;
        internal object? State => state;
        internal long Due { get; set; }
        internal long Period { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner.gate)
            {
                Due = dueTime == Timeout.InfiniteTimeSpan
                    ? long.MaxValue
                    : owner.ticks + dueTime.Ticks;
                Period = period == Timeout.InfiniteTimeSpan
                    ? 0
                    : period.Ticks;
                return true;
            }
        }

        public void Dispose()
        {
            lock (owner.gate)
                owner.timers.Remove(this);
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

internal sealed class ControlledNativeFactory : INativeWindowCaptureFactory
{
    internal List<NativeWindowSource> Sources { get; } =
        [new("Fixture application", "Fixture selected window")];
    internal Func<ControlledNativeSession> NewSession { get; set; } =
        () => new();
    internal Action? OnEnumerate { get; set; }
    internal Action? OnOpen { get; set; }
    internal int Enumerations;
    internal int Opens;

    public IReadOnlyList<NativeWindowSource> Enumerate(
        NativeEnumerationAccess access,
        CancellationToken cancellationToken)
    {
        access.Check();
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref Enumerations);
        OnEnumerate?.Invoke();
        access.Check();
        return Sources;
    }

    public INativeWindowCaptureSession Open(
        NativeWindowSource source,
        NativeCaptureAccess access,
        CancellationToken cancellationToken)
    {
        access.Check();
        cancellationToken.ThrowIfCancellationRequested();
        Assert.Contains(source, Sources);
        Interlocked.Increment(ref Opens);
        OnOpen?.Invoke();
        access.Check();
        return NewSession();
    }
}

internal sealed class ControlledNativeSession : INativeWindowCaptureSession
{
    internal ConcurrentQueue<NativeWindowFrame> Frames { get; } = new();
    internal ManualResetEventSlim ReadEntered { get; } = new();
    internal ManualResetEventSlim ReadReleased { get; } = new();
    internal ManualResetEventSlim StopEntered { get; } = new();
    internal ManualResetEventSlim DisposeEntered { get; } = new();
    internal ManualResetEventSlim CancellationCallbackEntered { get; } =
        new();
    internal ManualResetEventSlim? ReadBlock { get; set; }
    internal ManualResetEventSlim? StopBlock { get; set; }
    internal ManualResetEventSlim? DisposeBlock { get; set; }
    internal ManualResetEventSlim? CancellationCallbackBlock { get; set; }
    internal Exception? ReadFailure { get; set; }
    internal Exception? StopFailure { get; set; }
    internal Exception? DisposeFailure { get; set; }
    internal int Reads;
    internal int Stops;
    internal int Disposals;

    public NativeWindowFrame? ReadFrame(CancellationToken cancellationToken)
    {
        using var registration = CancellationCallbackBlock is null
            ? default
            : cancellationToken.Register(() =>
            {
                CancellationCallbackEntered.Set();
                CancellationCallbackBlock.Wait();
            });
        Interlocked.Increment(ref Reads);
        ReadEntered.Set();
        ReadBlock?.Wait();
        ReadReleased.Set();
        if (ReadFailure is not null)
            throw ReadFailure;
        cancellationToken.ThrowIfCancellationRequested();
        return Frames.TryDequeue(out var frame) ? frame : null;
    }

    public void RequestStop()
    {
        Interlocked.Increment(ref Stops);
        StopEntered.Set();
        StopBlock?.Wait();
        if (StopFailure is not null)
            throw StopFailure;
    }

    public void Dispose()
    {
        Interlocked.Increment(ref Disposals);
        DisposeEntered.Set();
        while (Frames.TryDequeue(out var frame))
            frame.Dispose();
        DisposeBlock?.Wait();
        if (DisposeFailure is not null)
            throw DisposeFailure;
    }
}

internal static class CaptureFixtures
{
    internal static readonly Guid SessionId =
        Guid.Parse("ce7dcbcf-bdd9-4742-8d98-f8efcf605737");
    internal static readonly Guid ConfigurationRevision =
        Guid.Parse("94b43f3e-9217-45e3-9491-40dd2bba7cae");
    internal static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    internal const string Canary = "PRIVATE-WINDOW-CONTENT-CANARY";

    internal static PerceptionOptions Options(
        WindowCaptureMode mode = WindowCaptureMode.SingleFrame) =>
        new()
        {
            MaximumLongestEdge = 1280,
            MaximumFrameBytes = 1024 * 1024,
            MaximumSources = 8,
            FrameInterval = TimeSpan.FromSeconds(1),
            SessionLifetime = mode == WindowCaptureMode.SingleFrame
                ? TimeSpan.FromSeconds(10)
                : TimeSpan.FromSeconds(30),
            MaximumFrameAge = TimeSpan.FromSeconds(5),
            ShutdownObservationWait = TimeSpan.FromSeconds(2)
        };

    internal static SelectedWindowCapture Owner(
        ControlledNativeFactory factory,
        ManualCaptureClock clock,
        PerceptionOptions options) =>
        new(
            SessionId,
            ConfigurationRevision,
            factory,
            options,
            clock);

    internal static WindowCaptureSource Enumerate(
        SelectedWindowCapture owner,
        ManualCaptureClock clock)
    {
        var binding = new SourceEnumerationBinding(
            SessionId,
            Guid.NewGuid(),
            ConfigurationRevision,
            owner.AuthorizationRevision);
        var authorization = new SourceEnumerationAuthorization(
            binding,
            clock.GetUtcNow().AddSeconds(10),
            selectedWindowEnumerationRequested: true,
            clock);
        return Assert.Single(
            owner.EnumerateSourcesAsync(authorization)
                .GetAwaiter()
                .GetResult());
    }

    internal static CaptureDestination RemoteDestination() =>
        new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            CaptureDestinationKind.RemoteProcessor,
            "Fixture host-2 perception route");

    internal static CaptureDestination LocalDestination() =>
        new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            CaptureDestinationKind.LocalOnly,
            "Local preview only");

    internal static WindowCaptureBinding Binding(
        WindowCaptureSource source,
        CaptureDestination destination,
        WindowCaptureMode mode = WindowCaptureMode.SingleFrame,
        long epoch = 0,
        TimeSpan? lifetime = null,
        Guid? configurationRevision = null,
        Guid? authorizationRevision = null) =>
        new(
            SessionId,
            Guid.NewGuid(),
            epoch,
            source.Id,
            source.EnumerationRevision,
            destination.Id,
            destination.Revision,
            configurationRevision ?? ConfigurationRevision,
            authorizationRevision ?? source.AuthorizationRevision,
            mode,
            lifetime ?? TimeSpan.FromSeconds(10));

    internal static WindowCaptureAuthorization Authorize(
        WindowCaptureSource source,
        CaptureDestination destination,
        ManualCaptureClock clock,
        PerceptionOptions options,
        WindowCaptureMode mode = WindowCaptureMode.SingleFrame,
        long epoch = 0,
        bool capture = true,
        bool continuous = true,
        TimeSpan? permissionLifetime = null,
        WindowCaptureBinding? binding = null,
        TimeProvider? authorizationClock = null,
        Guid? authorizationRevision = null) =>
        new(
            source,
            destination,
            binding ?? Binding(
                source,
                destination,
                mode,
                epoch,
                mode == WindowCaptureMode.SingleFrame
                    ? TimeSpan.FromSeconds(10)
                    : options.SessionLifetime,
                authorizationRevision: authorizationRevision),
            options,
            clock.GetUtcNow() + (permissionLifetime ?? options.SessionLifetime),
            capture,
            continuous,
            authorizationClock ?? clock);

    internal static NativeWindowFrame Frame(
        ManualCaptureClock clock,
        int width = 16,
        int height = 9,
        byte fill = 0x5a,
        DateTimeOffset? capturedAtUtc = null)
    {
        var bytes = Enumerable.Repeat(fill, checked(width * height * 4)).ToArray();
        return new(
            width,
            height,
            bytes,
            capturedAtUtc ?? clock.GetUtcNow());
    }

    internal static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(Wait);
        while (!condition())
            await Task.Delay(1, timeout.Token);
    }

    internal static PerceptionFailureCode Failure(
        Action action)
    {
        var exception = Assert.Throws<PerceptionException>(action);
        return exception.Failure.Code;
    }

    internal static async Task<PerceptionFailureCode> FailureAsync(
        Func<Task> action)
    {
        var exception = await Assert.ThrowsAsync<PerceptionException>(action);
        return exception.Failure.Code;
    }
}
