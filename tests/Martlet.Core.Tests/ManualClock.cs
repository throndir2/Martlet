namespace Martlet.Core.Tests;

internal sealed class ManualClock : TimeProvider
{
    private readonly object gate = new();
    private readonly List<ManualTimer> timers = [];
    private long ticks;
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override DateTimeOffset GetUtcNow() => new DateTimeOffset(2026, 9, 13, 0, 0, 0, TimeSpan.Zero).AddTicks(GetTimestamp());
    public override long GetTimestamp() { lock (gate) return ticks; }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        lock (gate)
        {
            timers.Add(timer);
            timer.Change(dueTime, period);
        }
        return timer;
    }

    public void Advance(TimeSpan duration, bool fireTimers = true)
    {
        List<ManualTimer> due;
        lock (gate)
        {
            ticks += duration.Ticks;
            due = fireTimers ? timers.Where(timer => timer.Due <= ticks).ToList() : [];
            foreach (var timer in due)
                timer.Due = timer.Period == Timeout.InfiniteTimeSpan ? long.MaxValue : ticks + timer.Period.Ticks;
        }
        foreach (var timer in due)
            timer.Fire();
    }

    private sealed class ManualTimer(ManualClock clock, TimerCallback callback, object? state) : ITimer
    {
        public long Due { get; set; } = long.MaxValue;
        public TimeSpan Period { get; private set; }
        private bool disposed;
        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (clock.gate)
            {
                if (disposed)
                    return false;
                Due = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : clock.ticks + dueTime.Ticks;
                Period = period;
                return true;
            }
        }
        public void Fire() { if (!disposed) callback(state); }
        public void Dispose() { lock (clock.gate) { disposed = true; clock.timers.Remove(this); } }
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
