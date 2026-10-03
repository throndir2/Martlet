namespace Martlet.Core.Nodes;

/// <summary>How Martlet on one computer keeps its own host service updates from colliding, and what each host's Devices card
/// says about its latest update. Every route that updates a host from this Martlet (a run window, a command from another
/// computer, keeping this PC's own host service current, the automatic pass) runs inside <see cref="Begin"/> for that host's
/// update key (<see cref="ThisPc"/> for this PC's own host service, whatever its pairing is called). The automatic pass skips
/// a host Martlet is already updating instead of starting a second run that would find the host locked by Martlet's own
/// update and report it busy. When a host is updated by any route, or a check finds it current, its retry and the note about
/// an earlier try go away.</summary>
public sealed class HostUpdateTracker
{
    /// <summary>The update key (and note ID) of this PC's own host service.</summary>
    public const string ThisPc = "this-pc";

    /// <summary>How long an update you asked for (Update hosts now) waits for another change running on that host before it
    /// gives up and Martlet tries again later, in seconds. Automatic updates don't wait: they stop at once and try again a few
    /// minutes later.</summary>
    public const int AskedLockWaitSeconds = 1800;

    private readonly Dictionary<string, int> running = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> notes = new(StringComparer.Ordinal);
    private readonly HashSet<string> waiting = new(StringComparer.Ordinal);

    /// <summary>What happened to each host's latest update, by host ID, for its Devices card.</summary>
    public IReadOnlyDictionary<string, string> Notes => notes;

    /// <summary>Hosts whose update found them busy, by host ID (<see cref="ThisPc"/> for this PC's own host service when this PC
    /// has no pairing with it); Martlet tries them again a few minutes later.</summary>
    public IReadOnlyCollection<string> Waiting => waiting;

    /// <summary>Whether any route is updating a host from this Martlet right now.</summary>
    public bool Running => running.Count > 0;

    /// <summary>The update key of a paired host: <see cref="ThisPc"/> when Martlet runs it on this PC's Docker Desktop.</summary>
    public static string Key(string hostId, bool onThisPc) => onThisPc ? ThisPc : hostId;

    /// <summary>Whether a route of this Martlet is updating <paramref name="key"/> right now.</summary>
    public bool IsUpdating(string key) => running.ContainsKey(key);

    /// <summary>Marks <paramref name="key"/> as being updated until the returned scope is disposed. Routes may overlap (an update
    /// you start while the automatic one runs waits its turn on the host), so each ends on its own.</summary>
    public IDisposable Begin(string key)
    {
        running[key] = running.GetValueOrDefault(key) + 1;
        return new Scope(this, key);
    }

    public void Note(string hostId, string text) => notes[hostId] = text;

    public void ClearNote(string hostId) => notes.Remove(hostId);

    /// <summary>Keeps <paramref name="hostId"/> for the next retry.</summary>
    public void Wait(string hostId) => waiting.Add(hostId);

    /// <summary>The hosts waiting for a retry; none wait afterwards (a retry that finds one busy again keeps it).</summary>
    public IReadOnlyList<string> TakeWaiting()
    {
        var taken = waiting.ToList();
        waiting.Clear();
        return taken;
    }

    /// <summary>A host is current: updated by any route, or found current by a check. Its retry and the note about an earlier
    /// try go; true when there was one. For <see cref="ThisPc"/> that includes this PC's own pairings with it
    /// (<paramref name="thisPcHostIds"/>).</summary>
    public bool Settled(string key, IEnumerable<string>? thisPcHostIds = null)
    {
        IEnumerable<string> ids = key == ThisPc ? [ThisPc, .. thisPcHostIds ?? []] : [key];
        var changed = false;
        foreach (var id in ids)
        {
            changed |= notes.Remove(id);
            changed |= waiting.Remove(id);
        }
        return changed;
    }

    /// <summary>The Devices note for an update that found the host busy; <paramref name="what"/> is the engine's MARTLET-BUSY
    /// description. Another update of the same host (from another computer or a console there) is said as such.</summary>
    public static string BusyNote(string version, string what, DateTime at) =>
        what.StartsWith("updating this host", StringComparison.Ordinal)
            ? $"Another update of that host was already running ({what}), so this one changed nothing. Martlet checks again in a few " +
              $"minutes and clears this once it runs Martlet {version} (last try {at:t})."
            : $"Waiting to update to Martlet {version}: that host is busy ({what}). Nothing was changed; Martlet tries again every few " +
              $"minutes until it is free (last try {at:t}).";

    private sealed class Scope(HostUpdateTracker tracker, string key) : IDisposable
    {
        private bool ended;

        public void Dispose()
        {
            if (ended) return;
            ended = true;
            if (tracker.running.GetValueOrDefault(key) <= 1) tracker.running.Remove(key);
            else tracker.running[key]--;
        }
    }
}
