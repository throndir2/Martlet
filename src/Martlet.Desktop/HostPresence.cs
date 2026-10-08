namespace Martlet.Desktop;

/// <summary>Which paired computers answer now, for everything in this PC that places work on them (the Thinking pool first).
/// Device sync's check of every paired host (<see cref="ClusterSync.Interval"/>, while Keep in sync is on) feeds it, and so
/// does a Thinking pool job that could not reach its computer. A host that was never checked counts as online; one failed
/// check (or one unreachable pool job) marks it offline at once, and the next check that reaches it marks it online again.
/// It keeps no hysteresis of its own: a reader that wants patience reads <see cref="OfflineSince"/>. Thread-safe;
/// <see cref="Changed"/> runs on the thread that noted the transition, after the state changed.</summary>
internal static class HostPresence
{
    private sealed record State(bool Reachable, DateTimeOffset Since, TimeSpan? LastAbsence);

    private static readonly object Gate = new();
    private static readonly Dictionary<string, State> Hosts = new(StringComparer.Ordinal);

    /// <summary>The clock the transition times come from; tests set their own.</summary>
    internal static TimeProvider Clock { get; set; } = TimeProvider.System;

    /// <summary>Raised once for each transition: (host ID, true) when an offline host answers again, (host ID, false) when a host
    /// that answered (or was never checked) stops answering. Never raised when a check only repeats what was known.</summary>
    internal static event Action<string, bool>? Changed;

    /// <summary>Records what a check of <paramref name="hostId"/> found, and raises <see cref="Changed"/> on a transition.</summary>
    internal static void Note(string hostId, bool reachable)
    {
        if (string.IsNullOrWhiteSpace(hostId)) return;
        var now = Clock.GetUtcNow();
        lock (Gate)
        {
            var known = Hosts.GetValueOrDefault(hostId);
            // Never checked counts as online: a first check that reaches it is no transition.
            var was = known?.Reachable ?? true;
            if (known is not null && was == reachable) return;
            Hosts[hostId] = new(reachable, now, reachable && known is { Reachable: false } away ? now - away.Since : known?.LastAbsence);
            if (was == reachable) return;
        }
        Raise(hostId, reachable);
    }

    /// <summary>Whether <paramref name="hostId"/> is offline: true only after a check said it doesn't answer.</summary>
    internal static bool IsOffline(string hostId)
    {
        lock (Gate) return Hosts.TryGetValue(hostId, out var state) && !state.Reachable;
    }

    /// <summary>The hosts offline now.</summary>
    internal static IReadOnlyCollection<string> Offline
    {
        get { lock (Gate) return [.. Hosts.Where(pair => !pair.Value.Reachable).Select(pair => pair.Key)]; }
    }

    /// <summary>Since when <paramref name="hostId"/> is offline; null while it answers (or was never checked).</summary>
    internal static DateTimeOffset? OfflineSince(string hostId)
    {
        lock (Gate) return Hosts.TryGetValue(hostId, out var state) && !state.Reachable ? state.Since : null;
    }

    /// <summary>How long <paramref name="hostId"/> stayed away the last time it came back; null when it never did.</summary>
    internal static TimeSpan? LastAbsence(string hostId)
    {
        lock (Gate) return Hosts.TryGetValue(hostId, out var state) ? state.LastAbsence : null;
    }

    /// <summary>Forgets every host (tests).</summary>
    internal static void Reset()
    {
        lock (Gate) Hosts.Clear();
    }

    // Each listener in turn, so one that fails doesn't keep the others (or the check that noted it) from going on.
    private static void Raise(string hostId, bool reachable)
    {
        if (Changed is not { } changed) return;
        foreach (var listener in changed.GetInvocationList().Cast<Action<string, bool>>())
        {
            try { listener(hostId, reachable); }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                ErrorLog.Warn($"Martlet couldn't act on {hostId} {(reachable ? "answering again" : "going offline")} ({error.GetType().Name}).");
            }
        }
    }
}
