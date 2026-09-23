namespace Martlet.Perception;

internal sealed class CaptureRateGate(
    TimeProvider clock,
    TimeSpan minimumInterval)
{
    private readonly object gate = new();
    private long? lastReadTimestamp;

    internal void RequireStartAllowed()
    {
        lock (gate)
        {
            PerceptionGuard.Require(RemainingUnderLock() == TimeSpan.Zero,
                PerceptionFailureCode.RateLimited);
        }
    }

    internal TimeSpan Remaining()
    {
        lock (gate)
            return RemainingUnderLock();
    }

    internal void AdmitRead()
    {
        lock (gate)
        {
            PerceptionGuard.Require(RemainingUnderLock() == TimeSpan.Zero,
                PerceptionFailureCode.RateLimited);
            lastReadTimestamp = clock.GetTimestamp();
        }
    }

    private TimeSpan RemainingUnderLock()
    {
        if (lastReadTimestamp is not { } previous)
            return TimeSpan.Zero;
        var remaining = minimumInterval - clock.GetElapsedTime(previous);
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }
}
