using System.Collections.Concurrent;
using Martlet.Core.Contracts;

namespace Martlet.Audio.Tests;

internal sealed class CaptureClock : TimeProvider
{
    private readonly object gate = new();
    private readonly List<ClockTimer> timers = new();
    private long ticks;
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() { lock (gate) return ticks; }
    public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(GetTimestamp());
    public int TimerCount { get { lock (gate) return timers.Count; } }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        lock (gate)
        {
            var timer = new ClockTimer(this, callback, state);
            timers.Add(timer);
            timer.Change(dueTime, period);
            return timer;
        }
    }

    public void Advance(TimeSpan amount, bool deliverTimers = true)
    {
        List<ClockTimer> due;
        lock (gate)
        {
            ticks += amount.Ticks;
            due = deliverTimers ? timers.Where(t => t.Due <= ticks).ToList() : new();
            foreach (var timer in due) timer.Due = long.MaxValue;
        }
        foreach (var timer in due) timer.Callback(timer.State);
    }

    private sealed class ClockTimer(CaptureClock owner, TimerCallback callback, object? state) : ITimer
    {
        internal long Due { get; set; }
        internal TimerCallback Callback => callback;
        internal object? State => state;
        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner.gate)
            {
                Due = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : owner.ticks + dueTime.Ticks;
                return true;
            }
        }
        public void Dispose() { lock (owner.gate) owner.timers.Remove(this); }
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}

internal sealed class ControlledCapture : ICaptureDeviceFactory, ICaptureDevice
{
    internal ConcurrentQueue<byte[]> Packets { get; } = new();
    internal ConcurrentQueue<(string Operation, int Thread, ApartmentState Apartment)> Calls { get; } = new();
    internal ManualResetEventSlim OpenEntered { get; } = new();
    internal ManualResetEventSlim ReadEntered { get; } = new();
    internal ManualResetEventSlim DisposeEntered { get; } = new();
    internal ManualResetEventSlim? OpenBlock { get; set; }
    internal ManualResetEventSlim? ReadBlock { get; set; }
    internal ManualResetEventSlim? DisposeBlock { get; set; }
    internal Exception? OpenFailure { get; set; }
    internal Exception? ReadFailure { get; set; }
    internal Exception? StopFailure { get; set; }
    internal Exception? DisposeFailure { get; set; }
    internal bool Discontinuous { get; set; }
    internal bool Oversized { get; set; }
    internal int Opens, Starts, Stops, Disposals, Reads;
    internal InputSelection? Selection { get; private set; }
    internal CancellationToken NativeToken { get; private set; }
    public CaptureSourceFormat Format { get; set; } = new(16000, 1, 16, DeviceSampleEncoding.IntegerPcm);

    public ICaptureDevice Open(CaptureDeviceAccess access, CancellationToken cancellationToken)
    {
        Record("open");
        access.CheckAuthorization();
        Interlocked.Increment(ref Opens);
        Selection = access.Input;
        NativeToken = cancellationToken;
        OpenEntered.Set();
        OpenBlock?.Wait();
        if (OpenFailure is not null) throw OpenFailure;
        return this;
    }
    public void Start(CancellationToken cancellationToken)
    {
        Record("start");
        Interlocked.Increment(ref Starts);
    }
    public CapturePacket Read(Span<byte> destination, CancellationToken cancellationToken)
    {
        Record("read");
        Interlocked.Increment(ref Reads);
        ReadEntered.Set();
        ReadBlock?.Wait();
        if (ReadFailure is not null) throw ReadFailure;
        if (Oversized) return new(destination.Length + 1);
        if (!Packets.TryDequeue(out var bytes)) return new(0);
        bytes.AsSpan().CopyTo(destination);
        return new(bytes.Length, Discontinuous);
    }
    public void Stop()
    {
        Record("stop");
        Interlocked.Increment(ref Stops);
        if (StopFailure is not null) throw StopFailure;
    }
    public void Dispose()
    {
        Record("dispose");
        Interlocked.Increment(ref Disposals);
        DisposeEntered.Set();
        DisposeBlock?.Wait();
        if (DisposeFailure is not null) throw DisposeFailure;
    }
    private void Record(string operation) => Calls.Enqueue((operation, Environment.CurrentManagedThreadId, Thread.CurrentThread.GetApartmentState()));
}

internal sealed class ControlledDiscovery : IInputDeviceDiscoveryFactory, IInputDeviceDiscovery
{
    internal int Opens, Enumerations, Disposals;
    internal int PendingChanges;
    internal ManualResetEventSlim? OpenBlock { get; set; }
    internal ManualResetEventSlim? DisposeBlock { get; set; }
    internal ManualResetEventSlim OpenEntered { get; } = new();
    internal ManualResetEventSlim DisposeEntered { get; } = new();
    internal Exception? Failure { get; set; }
    internal Exception? DisposeFailure { get; set; }
    internal List<int> Threads { get; } = new();
    public IInputDeviceDiscovery Open(CancellationToken cancellationToken)
    {
        Threads.Add(Environment.CurrentManagedThreadId);
        Interlocked.Increment(ref Opens);
        OpenEntered.Set();
        OpenBlock?.Wait();
        return this;
    }
    public IReadOnlyList<InputEndpoint> Enumerate(CancellationToken cancellationToken)
    {
        Threads.Add(Environment.CurrentManagedThreadId);
        Interlocked.Increment(ref Enumerations);
        if (Failure is not null) throw Failure;
        return new[] { new InputEndpoint("private-endpoint-id", "Private headset", true) };
    }
    public InputDeviceChanges PollChanges() => (InputDeviceChanges)Interlocked.Exchange(ref PendingChanges, 0);
    public void Dispose()
    {
        Threads.Add(Environment.CurrentManagedThreadId);
        Interlocked.Increment(ref Disposals);
        DisposeEntered.Set();
        DisposeBlock?.Wait();
        if (DisposeFailure is not null) throw DisposeFailure;
    }
}
