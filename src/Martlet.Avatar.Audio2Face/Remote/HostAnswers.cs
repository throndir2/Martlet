namespace Martlet.Avatar.Audio2Face.Remote;

/// <summary>A change in whether a paired host answers, as the desktop log says it.</summary>
/// <param name="Answering">True when the host answers again; false when it stopped answering (or never answered).</param>
/// <param name="Text">The log line ("Host gpu-box stopped answering: ...", "Host gpu-box answers again.").</param>
public sealed record HostAnswerChange(string HostId, bool Answering, string Text);

/// <summary>
/// Whether each paired host answers this PC's regular checks, for the desktop's status and log. A host counts as not
/// answering only after it missed <see cref="MissesBeforeDown"/> checks in a row, so one slow answer from a busy host is not
/// a change. Each change is reported once: "stopped answering" (or "didn't answer" when it never answered since Martlet
/// started) when it goes, "answers again" when it is back. Not thread-safe; call it from one thread.
/// </summary>
public sealed class HostAnswers
{
    private readonly Dictionary<string, State> hosts = new(StringComparer.Ordinal);

    /// <param name="missesBeforeDown">Checks in a row a host must miss before it counts as not answering (at least 1).</param>
    public HostAnswers(int missesBeforeDown = 2)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(missesBeforeDown, 1);
        MissesBeforeDown = missesBeforeDown;
    }

    public int MissesBeforeDown { get; }

    /// <summary>Records one check of <paramref name="hostId"/>: whether it answered and, when not, why. Returns the change to
    /// log when this check changed whether the host counts as answering, else null.</summary>
    public HostAnswerChange? Record(string hostId, bool answered, string? problem)
    {
        ArgumentException.ThrowIfNullOrEmpty(hostId);
        var state = hosts.TryGetValue(hostId, out var known) ? known : new State(0, false, false);
        if (answered)
        {
            hosts[hostId] = new State(0, false, true);
            return state.Down ? new(hostId, true, $"Host {hostId} answers again.") : null;
        }
        var misses = Math.Min(state.Misses + 1, 1_000);
        var down = state.Down || misses >= MissesBeforeDown;
        hosts[hostId] = new State(misses, down, state.Answered);
        if (state.Down || !down) return null;
        var why = string.IsNullOrWhiteSpace(problem) ? "" : ": " + problem.Trim();
        return new(hostId, false, $"Host {hostId} {(state.Answered ? "stopped answering" : "didn't answer")}{why}");
    }

    /// <summary>False only while <paramref name="hostId"/> counts as not answering (it missed enough checks in a row).</summary>
    public bool Answering(string hostId) => !hosts.TryGetValue(hostId, out var state) || !state.Down;

    /// <summary>The checks in a row <paramref name="hostId"/> missed (0 when its last check answered or it was never checked).</summary>
    public int Misses(string hostId) => hosts.TryGetValue(hostId, out var state) ? state.Misses : 0;

    /// <summary>Forgets a host (unpaired, or checks turned off): its next check starts over.</summary>
    public void Forget(string hostId) => hosts.Remove(hostId);

    /// <summary>Forgets every host.</summary>
    public void Clear() => hosts.Clear();

    private readonly record struct State(int Misses, bool Down, bool Answered);
}
