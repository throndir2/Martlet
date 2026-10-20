using System.IO;
using System.Net.Http;
using Martlet.Avatar.Audio2Face.Remote;
using Martlet.Core.Cluster;
using Martlet.Core.Settings;
using Martlet.Providers;

namespace Martlet.Desktop;

/// <summary>Devices › Sharing work on this PC: the shared choices (work-sharing.json), which paired computers run each job's
/// engine (the shared plan's record of their roles, cluster.json, and this PC's pairings, hosts.json) and the order a request
/// tries them in (<see cref="WorkSharing.Order"/>). Every file is read again only when it changed, so asking costs a few file
/// times per request and never the network. Requests then go through <see cref="WorkQueue.Shared"/>: a busy computer is passed
/// over for the next, and when every one is busy the request waits for whichever frees first.
/// <para>The desktop uses one roster, <see cref="Shared"/>. A store that saves a file this roster read tells it
/// (<see cref="SettingsFiles.Saved"/>), so the next request reads the new file and <see cref="Changed"/> is raised; a page that
/// saves a list doesn't tell the roster itself. A file another program changed is seen at the next look (at most once a
/// second). Tests make their own roster with their own clock.</para></summary>
internal sealed partial class WorkSharingRoster : IDisposable
{
    /// <summary>The desktop's roster, for every request and page.</summary>
    internal static WorkSharingRoster Shared { get; } = new(WorkQueue.Shared);

    private readonly object gate = new();
    private readonly Dictionary<string, (DateTime Written, long Checked, object Value)> files = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, WorkRoute> lastRoutes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> reroutes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> ownerNoticed = new(StringComparer.Ordinal);
    private readonly WorkQueue queue;
    private readonly TimeProvider clock;
    private string? ownHost;

    /// <param name="queue">The queue whose stops and moves this roster logs and counts (<see cref="Recent"/>).</param>
    /// <param name="clock">The clock for how often a file is looked at again; the system clock by default.</param>
    internal WorkSharingRoster(WorkQueue queue, TimeProvider? clock = null)
    {
        this.queue = queue;
        this.clock = clock ?? TimeProvider.System;
        queue.Preempted += Preempted;
        queue.Rerouted += Rerouted;
        SettingsFiles.Saved += Saved;
    }

    public void Dispose()
    {
        queue.Preempted -= Preempted;
        queue.Rerouted -= Rerouted;
        SettingsFiles.Saved -= Saved;
    }

    /// <summary>Raised with a file's full path when a store saved a file this roster had read, after it forgot its copy: what
    /// the roster gives for that file changed. Runs on the thread that saved.</summary>
    internal event Action<string>? Changed;

    // A live request stopped this PC's own background request on a computer (the live turn comes first).
    private static void Preempted(string lane, string host) =>
        ErrorLog.Info($"Live floor: a live {WorkSharingJobs.Title(lane)} request stopped this PC's own background request on {host}; it runs again later.");

    private void Rerouted(string lane, WorkRoute route)
    {
        lock (gate)
        {
            lastRoutes[lane] = route;
            reroutes[lane] = reroutes.GetValueOrDefault(lane) + 1;
        }
        ErrorLog.Info($"Sharing work: {WorkSharingJobs.Title(lane)} went to {route.HostId}" +
            (route.Busy > 0 ? $" ({route.Busy} busy" + (route.Unavailable > 0 ? $", {route.Unavailable} not answering" : "") + ")"
                : route.Unavailable > 0 ? $" ({route.Unavailable} not answering)" : "") +
            (route.Waited >= TimeSpan.FromMilliseconds(50) ? $" after {route.Waited.TotalMilliseconds:0} ms." : "."));
    }

    private void Saved(string path)
    {
        string? known;
        lock (gate)
        {
            var full = Full(path);
            known = files.Keys.FirstOrDefault(key => string.Equals(Full(key), full, StringComparison.OrdinalIgnoreCase));
            if (known is not null) files.Remove(known);
        }
        if (known is not null) Changed?.Invoke(path);
    }

    private static string Full(string path)
    {
        try { return Path.GetFullPath(path); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { return path; }
    }

    /// <summary>This PC's device ID, as the companion PCs a computer is kept for name it.</summary>
    internal static string Device => HostSetupCommands.SuggestedDeviceId();

    /// <summary>The host service Martlet runs on this PC (set by the main window as it learns it); null falls back to a pairing
    /// saved as this PC's own.</summary>
    internal string? OwnHostId
    {
        get => Volatile.Read(ref ownHost);
        set => Volatile.Write(ref ownHost, value);
    }

    /// <summary>The data directory requests without one of their own (Thinking, Listening) read the choices from; set at start.</summary>
    internal string? DataDirectory { get; set; }

    /// <summary>The choices, read again only when work-sharing.json changed.</summary>
    internal WorkSharingSettings Settings(string? directory) =>
        directory is null ? new() : Cached(Path.Combine(directory, WorkSharingSettings.FileName), () => WorkSharingSettings.Load(directory));

    /// <summary>How many requests of each job another computer took since Martlet started, and the last one's way.</summary>
    internal IReadOnlyDictionary<string, (int Count, WorkRoute Last)> Recent()
    {
        lock (gate) return lastRoutes.ToDictionary(p => p.Key, p => (reroutes.GetValueOrDefault(p.Key), p.Value), StringComparer.Ordinal);
    }

    /// <summary>The paired computers to try for <paramref name="job"/>, first to last, each with the model its role runs (from the
    /// shared plan; null for <paramref name="planned"/>, which keeps the route's own). <paramref name="roleKind"/> is the host role
    /// that does it there; <paramref name="model"/>, when given, the model it must run. Without <paramref name="planned"/>, only
    /// the computers the plan says run it.</summary>
    internal IReadOnlyList<(PairedHost? Host, string? Model)> Order(string? directory, string job, string roleKind, string? model,
        string? planned) =>
        [.. Places(directory, job, roleKind, model, planned).Where(p => p.Cloud is null).Select(p => (p.Host, p.Model))];

    /// <summary><see cref="Order"/> with the area's other members in their turn: each stop is a paired computer (with its model)
    /// or, for Speaking and Listening, a cloud member <paramref name="cloud"/> says can take it on this PC (it has a key here).
    /// Without <paramref name="cloud"/>, only computers.</summary>
    internal IReadOnlyList<(PairedHost? Host, string? Model, PoolMember? Cloud)> Places(string? directory, string job, string roleKind,
        string? model, string? planned, Func<PoolMember, bool>? cloud = null)
    {
        if (directory is null) return [(null, null, null)];
        var hosts = Hosts(directory);
        var plan = Cached(Path.Combine(directory, ClusterSync.PlanFile), () => ClusterSync.LoadPlan(directory));
        var own = OwnHostId ?? hosts.FirstOrDefault(h => h.Method == HostSetupMethod.ThisPcDocker)?.HostId;
        var runs = plan.Nodes.Where(n => !n.Removed && n.HostId != planned && hosts.Any(h => h.HostId == n.HostId))
            .Select(n => (Node: n, Role: n.Roles.FirstOrDefault(r => r.Kind == roleKind && (model is null || r.Model == model))))
            .Where(p => p.Role is not null)
            .ToDictionary(p => p.Node.HostId, p => p.Role!.Model, StringComparer.Ordinal);
        (PairedHost? Host, string? Model, PoolMember? Cloud) Place(string id) => id == planned
            ? (hosts.FirstOrDefault(h => h.HostId == id), (string?)null, null)
            : (hosts.First(h => h.HostId == id), runs[id], null);
        // Speaking's, Listening's and Thinking's pool list (Companion › Voice, Listening, Thinking), once it has one: its members
        // in the owner's order. The other pooled areas route their own lists.
        if (PoolAreas.Find(job) is { } area && (area == PoolAreas.Speaking || area == PoolAreas.Listening || area == PoolAreas.Thinking) &&
            Pool(directory, area) is { } list)
            return LiveSituation.Order([.. PoolRouting.Stops(area, list, Device, planned, own, runs.Keys.ToHashSet(StringComparer.Ordinal), cloud)
                .Select(stop => stop.HostId is { } id ? Place(id) : (null, null, stop.Member))], p => p.Host?.HostId, own);
        var places = runs.Keys.Select(id => new WorkPlace(id, id == own,
            plan.Assignments.Count(a => ClusterJobs.All.Contains(a.Job) && a.HostId == id))).ToArray();
        var order = WorkSharing.Order(Settings(directory), job, Device, planned, places);
        // A computer that doesn't answer now goes last, and so does this PC's own host service while a game runs here (Situations).
        return LiveSituation.Order([.. order.Select(Place)], p => p.Host?.HostId, own);
    }

    internal IReadOnlyList<PairedHost> Hosts(string directory) => Cached(Path.Combine(directory, HostRegistry.FileName), () =>
    {
        try { return HostRegistry.Load(directory); }
        catch (Exception error) when (error is InvalidDataException or IOException or UnauthorizedAccessException) { return (IReadOnlyList<PairedHost>)[]; }
    });

    /// <summary>Forgets every file read, so the next request reads them again. Stores tell the roster about their own saves; this
    /// is for a file written without its store (a test).</summary>
    internal void Forget()
    {
        lock (gate) files.Clear();
    }

    internal T Cached<T>(string path, Func<T> read) where T : class
    {
        // A file is looked at again at most once a second, so a request pays no file system call on the way to its computer.
        var now = clock.GetTimestamp();
        lock (gate)
            if (files.TryGetValue(path, out var seen) && clock.GetElapsedTime(seen.Checked, now) < TimeSpan.FromSeconds(1) &&
                seen.Value is T recent) return recent;
        DateTime written;
        try { written = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { written = DateTime.MinValue; }
        lock (gate)
            if (files.TryGetValue(path, out var known) && known.Written == written && known.Value is T value)
            {
                files[path] = (written, now, value);
                return value;
            }
        var fresh = read();
        lock (gate) files[path] = (written, now, fresh);
        return fresh;
    }

    /// <summary>What a refusal before a request's first answer means: busy (try the next, then wait), unavailable (try the next),
    /// preempted (the host keeps its graphics card for a live turn: pool work waits or goes elsewhere, never a failure) or owner
    /// (a host a friend shares keeps its card for its owner's own work: try the next at once; background work goes on later).</summary>
    internal static WorkRefusal Classify(Exception error) => error switch
    {
        Audio2FaceHostException { OwnerFirst: true } => WorkRefusal.Owner,
        Audio2FaceHostException { HeldForLive: true } => WorkRefusal.Preempted,
        Audio2FaceHostException { Code: "job.busy" or "worker.busy" } => WorkRefusal.Busy,
        Audio2FaceHostException { Code: "host.unreachable" or "host.redirect" or "worker.unavailable" or "worker.quarantined" } =>
            WorkRefusal.Unavailable,
        HostTextException { Code: ProviderFailureCode.ModelNotFound or ProviderFailureCode.CredentialUnavailable } => WorkRefusal.Unavailable,
        HttpRequestException or IOException => WorkRefusal.Unavailable,
        // A cloud member of the list: a rate limit is busy, a missing key or an outage is unavailable (PoolCloud).
        _ => PoolCloud.Refusal(error)
    };

    /// <summary>Raised (host, job) the first time since Martlet started that a host a friend shares with this PC turned this PC
    /// away for its owner's own work, so the owner of this PC hears it once, in plain words.</summary>
    internal event Action<string, string>? OwnerFirst;

    /// <summary>A plain sentence for <see cref="OwnerFirst"/>.</summary>
    internal static string OwnerFirstText(string hostId, string job) =>
        $"{hostId} is busy with its owner's own work right now, so Martlet used another of your computers for {job} if one runs it, " +
        $"or tries {hostId} again on the next request.";

    /// <summary><paramref name="source"/>, a request to <paramref name="hostId"/>: when that is a host a friend shares and it turns
    /// the request away for its owner's work (<c>job.busy</c> with detail owner, or <c>job.preempted</c>), the desktop log says so
    /// (once a minute at most) and <see cref="OwnerFirst"/> is raised the first time. Before the first answer a stop there is the
    /// owner's work too, so it reaches the queue as that host's owner refusal: the request moves on rather than waiting for it.</summary>
    internal async IAsyncEnumerable<T> Watched<T>(string hostId, string job, IAsyncEnumerable<T> source,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token = default)
    {
        await using var items = source.GetAsyncEnumerator(token);
        var started = false;
        while (true)
        {
            bool moved;
            try { moved = await items.MoveNextAsync().ConfigureAwait(false); }
            catch (Audio2FaceHostException error) when ((error.OwnerFirst || error.Code == "job.preempted") && IsShared(hostId))
            {
                NoteOwnerFirst(hostId, job);
                if (started || error.OwnerFirst) throw;
                throw new Audio2FaceHostException("job.busy", error.Message) { Detail = Audio2FaceHostException.OwnerDetail };
            }
            if (!moved) yield break;
            started = true;
            yield return items.Current;
        }
    }

    /// <summary>Whether <paramref name="hostId"/> is a host a friend shares with this PC (hosts.json, read again only when it changed).</summary>
    internal bool IsShared(string hostId) =>
        DataDirectory is { } directory && Hosts(directory).Any(h => h.HostId == hostId && h.Shared);

    /// <summary>The one-answer form of <see cref="Watched{T}"/> (a transcription).</summary>
    internal async Task<T> WatchedOnce<T>(string hostId, string job, Task<T> answer)
    {
        try { return await answer.ConfigureAwait(false); }
        catch (Audio2FaceHostException error) when ((error.OwnerFirst || error.Code == "job.preempted") && IsShared(hostId))
        {
            NoteOwnerFirst(hostId, job);
            if (error.OwnerFirst) throw;
            throw new Audio2FaceHostException("job.busy", error.Message) { Detail = Audio2FaceHostException.OwnerDetail };
        }
    }

    private void NoteOwnerFirst(string hostId, string job)
    {
        bool first;
        lock (gate)
        {
            var now = clock.GetTimestamp();
            first = !ownerNoticed.TryGetValue(hostId, out var last);
            if (!first && clock.GetElapsedTime(last, now) < TimeSpan.FromMinutes(1)) return;
            ownerNoticed[hostId] = now;
        }
        ErrorLog.Info($"Shared hosts: {hostId} turned this PC's {job} request away for its owner's own work (a friend shares it; their work comes first).");
        if (first) OwnerFirst?.Invoke(hostId, job);
    }

    /// <summary>A paired computer as a host target.</summary>
    internal static HostTextTarget TextTarget(PairedHost host, string routeId) => new(host.Pairing.Origin, host.HostId,
        host.Pairing.SpkiFingerprint, host.Pairing.DeviceId, HostPairingCredential.ToGuid(host.Pairing.CredentialId), routeId);
}
