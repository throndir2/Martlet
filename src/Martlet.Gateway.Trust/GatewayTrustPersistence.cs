namespace Martlet.Gateway.Trust;

internal sealed partial class GatewayTrustState
{
    private readonly IDurableTrust? persistence;

    internal GatewayTrustState(GatewayHostIdentity identity, TimeProvider clock,
        TrustCheckpoint checkpoint, IDurableTrust persistence) : this(identity, clock)
    {
        this.persistence = persistence;
        var now = Enter(CancellationToken.None);
        if (now.Utc < checkpoint.ObservedAt)
        {
            Close(GatewayTrustFailure.ClockInvalid);
            throw Failure(GatewayTrustFailure.ClockInvalid);
        }
        var offline = now.Utc - checkpoint.ObservedAt;
        try
        {
            foreach (var stored in checkpoint.Devices)
            {
                var current = Restore(stored.Current.Life, now, offline);
                if (current is null)
                    continue;
                var device = new Device(stored.Scopes, new(stored.Current.Verifier.ToArray(), current));
                devices.Add(stored.DeviceId, device);
                if (stored.Previous is { } previous && stored.Overlap is { } savedOverlap &&
                    Restore(previous.Life, now, offline) is { } previousLife &&
                    Restore(savedOverlap, now, offline) is { } overlap)
                {
                    device.Previous = new(previous.Verifier.ToArray(), previousLife);
                    device.Overlap = overlap;
                }
            }
        }
        catch
        {
            Close(GatewayTrustFailure.AuthorityClosed);
            throw;
        }
    }

    private static Lifetime? Restore(StoredLifetime stored, Moment now, TimeSpan offline)
    {
        var remaining = Math.Min(stored.RemainingTicks - offline.Ticks, (stored.ExpiresAt - now.Utc).Ticks);
        return remaining <= 0 ? null : new(now, TimeSpan.FromTicks(remaining), stored.ExpiresAt);
    }

    private StoredLifetime Save(Lifetime life, Moment now)
    {
        var elapsed = ((decimal)now.Timestamp - life.Start.Timestamp) * TimeSpan.TicksPerSecond / frequency;
        var remaining = Math.Min((decimal)(life.ExpiresAt - now.Utc).Ticks, life.Duration.Ticks - elapsed);
        return new(life.ExpiresAt, Math.Max(0, (long)decimal.Floor(remaining)));
    }

    private TrustCheckpoint Checkpoint(Moment now) => new(now.Utc,
        devices.Select(pair => new StoredDevice(pair.Key, pair.Value.Scopes,
            new(pair.Value.Current.Verifier.ToArray(), Save(pair.Value.Current.Life, now)),
            pair.Value.Previous is { } previous
                ? new(previous.Verifier.ToArray(), Save(previous.Life, now)) : null,
            pair.Value.Overlap is { } overlap ? Save(overlap, now) : null)).ToArray());

    private void Commit(Moment now)
    {
        if (persistence is null)
            return;
        using var checkpoint = Checkpoint(now);
        try { persistence.Commit(checkpoint); }
        catch
        {
            // Nothing can observe a partially committed transition outside the authority lock.
            Close(GatewayTrustFailure.AuthorityClosed);
            throw;
        }
    }

    internal void Complete(CancellationToken ct)
    {
        lock (gate)
        {
            var now = Enter(ct);
            using var checkpoint = Checkpoint(now);
            ct.ThrowIfCancellationRequested();
            try { persistence?.Complete(checkpoint); }
            finally { Close(GatewayTrustFailure.AuthorityClosed); }
        }
    }
}
