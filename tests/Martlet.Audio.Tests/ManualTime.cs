namespace Martlet.Audio.Tests;

internal sealed class ManualTime : TimeProvider
{
    private readonly object gate = new();
    private readonly List<Timer> timers = new();
    private long ticks;
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() { lock (gate) return ticks; }
    public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(GetTimestamp());
    public int TimerCount { get { lock (gate) return timers.Count; } }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        lock (gate)
        {
            var timer = new Timer(this, callback, state);
            timers.Add(timer);
            timer.Change(dueTime, period);
            return timer;
        }
    }

    public void Advance(TimeSpan amount)
    {
        List<Timer> due;
        lock (gate)
        {
            ticks += amount.Ticks;
            due = timers.Where(t => t.Due <= ticks).ToList();
            foreach (var timer in due)
                timer.Due = timer.Period > 0 ? ticks + timer.Period : long.MaxValue;
        }
        foreach (var timer in due) timer.Callback(timer.State);
    }

    private sealed class Timer(ManualTime owner, TimerCallback callback, object? state) : ITimer
    {
        internal TimerCallback Callback => callback;
        internal object? State => state;
        internal long Due { get; set; }
        internal long Period { get; private set; }
        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner.gate)
            {
                Due = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : owner.ticks + dueTime.Ticks;
                Period = period == Timeout.InfiniteTimeSpan ? 0 : period.Ticks;
                return true;
            }
        }
        public void Dispose() { lock (owner.gate) owner.timers.Remove(this); }
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
