using System.IO;
using System.Net.Http;
using Martlet.Avatar.Audio2Face.Remote;
using Martlet.Core.Cluster;
using Martlet.Providers;

namespace Martlet.Desktop;

/// <summary>Devices › Sharing work on this PC: the shared choices (work-sharing.json), which paired computers run each job's
/// engine (the shared plan's record of their roles, cluster.json, and this PC's pairings, hosts.json) and the order a request
/// tries them in (<see cref="WorkSharing.Order"/>). Every file is read again only when it changed, so asking costs a few file
/// times per request and never the network. Requests then go through <see cref="WorkQueue.Shared"/>: a busy computer is passed
/// over for the next, and when every one is busy the request waits for whichever frees first.</summary>
internal static partial class WorkSharingRoster
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, (DateTime Written, long Checked, object Value)> Files = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, WorkRoute> LastRoutes = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, int> Reroutes = new(StringComparer.Ordinal);
    private static string? ownHost;

    static WorkSharingRoster()
    {
        // A live request stopped this PC's own background request on a computer (the live turn comes first).
        WorkQueue.Shared.Preempted += (lane, host) =>
            ErrorLog.Info($"Live floor: a live {WorkSharingJobs.Title(lane)} request stopped this PC's own background request on {host}; it runs again later.");
        WorkQueue.Shared.Rerouted += (lane, route) =>
        {
            lock (Gate)
            {
                LastRoutes[lane] = route;
                Reroutes[lane] = Reroutes.GetValueOrDefault(lane) + 1;
            }
            ErrorLog.Info($"Sharing work: {WorkSharingJobs.Title(lane)} went to {route.HostId}" +
                (route.Busy > 0 ? $" ({route.Busy} busy" + (route.Unavailable > 0 ? $", {route.Unavailable} not answering" : "") + ")"
                    : route.Unavailable > 0 ? $" ({route.Unavailable} not answering)" : "") +
                (route.Waited >= TimeSpan.FromMilliseconds(50) ? $" after {route.Waited.TotalMilliseconds:0} ms." : "."));
        };
    }

    /// <summary>This PC's device ID, as the companion PCs a computer is kept for name it.</summary>
    internal static string Device { get; } = HostSetupCommands.SuggestedDeviceId();

    /// <summary>The host service Martlet runs on this PC (set by the main window as it learns it); null falls back to a pairing
    /// saved as this PC's own.</summary>
    internal static string? OwnHostId
    {
        get => Volatile.Read(ref ownHost);
        set => Volatile.Write(ref ownHost, value);
    }

    /// <summary>The data directory requests without one of their own (Thinking, Listening) read the choices from; set at start.</summary>
    internal static string? DataDirectory { get; set; }

    /// <summary>The choices, read again only when work-sharing.json changed.</summary>
    internal static WorkSharingSettings Settings(string? directory) =>
        directory is null ? new() : Cached(Path.Combine(directory, WorkSharingSettings.FileName), () => WorkSharingSettings.Load(directory));

    /// <summary>How many requests of each job another computer took since Martlet started, and the last one's way.</summary>
    internal static IReadOnlyDictionary<string, (int Count, WorkRoute Last)> Recent()
    {
        lock (Gate) return LastRoutes.ToDictionary(p => p.Key, p => (Reroutes.GetValueOrDefault(p.Key), p.Value), StringComparer.Ordinal);
    }

    /// <summary>The paired computers to try for <paramref name="job"/>, first to last, each with the model its role runs (from the
    /// shared plan; null for <paramref name="planned"/>, which keeps the route's own). <paramref name="roleKind"/> is the host role
    /// that does it there; <paramref name="model"/>, when given, the model it must run. Without <paramref name="planned"/>, only
    /// the computers the plan says run it.</summary>
    internal static IReadOnlyList<(PairedHost? Host, string? Model)> Order(string? directory, string job, string roleKind, string? model,
        string? planned)
    {
        if (directory is null) return [(null, null)];
        var hosts = Hosts(directory);
        var plan = Cached(Path.Combine(directory, ClusterSync.PlanFile), () => ClusterSync.LoadPlan(directory));
        var own = OwnHostId ?? hosts.FirstOrDefault(h => h.Method == HostSetupMethod.ThisPcDocker)?.HostId;
        var runs = plan.Nodes.Where(n => !n.Removed && n.HostId != planned && hosts.Any(h => h.HostId == n.HostId))
            .Select(n => (Node: n, Role: n.Roles.FirstOrDefault(r => r.Kind == roleKind && (model is null || r.Model == model))))
            .Where(p => p.Role is not null)
            .ToDictionary(p => p.Node.HostId, p => p.Role!.Model, StringComparer.Ordinal);
        (PairedHost? Host, string? Model) Place(string id) => id == planned ? (hosts.FirstOrDefault(h => h.HostId == id), (string?)null)
            : (hosts.First(h => h.HostId == id), runs[id]);
        // The area's pool list (Companion › Voice, Listening, Thinking), once it has one: its members in the owner's order.
        if (PoolAreas.Find(job) is { } area && Pool(directory, area) is { } list)
            return [.. PoolHosts(area, list, planned, own, runs.Keys.ToHashSet(StringComparer.Ordinal)).Select(Place)];
        var places = runs.Keys.Select(id => new WorkPlace(id, id == own,
            plan.Assignments.Count(a => ClusterJobs.All.Contains(a.Job) && a.HostId == id))).ToArray();
        var order = WorkSharing.Order(Settings(directory), job, Device, planned, places);
        return [.. order.Select(Place)];
    }

    private static IReadOnlyList<PairedHost> Hosts(string directory) => Cached(Path.Combine(directory, HostRegistry.FileName), () =>
    {
        try { return HostRegistry.Load(directory); }
        catch (Exception error) when (error is InvalidDataException or IOException or UnauthorizedAccessException) { return (IReadOnlyList<PairedHost>)[]; }
    });

    /// <summary>Forgets what was read, so the next request reads the files again (after this PC changed one).</summary>
    internal static void Forget()
    {
        lock (Gate) Files.Clear();
    }

    private static T Cached<T>(string path, Func<T> read) where T : class
    {
        // A file is looked at again at most once a second, so a request pays no file system call on the way to its computer.
        var now = Environment.TickCount64;
        lock (Gate)
            if (Files.TryGetValue(path, out var seen) && now - seen.Checked < 1_000 && seen.Value is T recent) return recent;
        DateTime written;
        try { written = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { written = DateTime.MinValue; }
        lock (Gate)
            if (Files.TryGetValue(path, out var known) && known.Written == written && known.Value is T value)
            {
                Files[path] = (written, now, value);
                return value;
            }
        var fresh = read();
        lock (Gate) Files[path] = (written, now, fresh);
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
        _ => WorkRefusal.None
    };

    private static readonly Dictionary<string, long> OwnerNoticed = new(StringComparer.Ordinal);

    /// <summary>Raised (host, job) the first time since Martlet started that a host a friend shares with this PC turned this PC
    /// away for its owner's own work, so the owner of this PC hears it once, in plain words.</summary>
    internal static event Action<string, string>? OwnerFirst;

    /// <summary>A plain sentence for <see cref="OwnerFirst"/>.</summary>
    internal static string OwnerFirstText(string hostId, string job) =>
        $"{hostId} is busy with its owner's own work right now. A friend shares it with this PC, and their own work always comes first, " +
        $"so Martlet used another of your computers for {job} if one runs it, or tries {hostId} again on the next request.";

    /// <summary><paramref name="source"/>, a request to <paramref name="hostId"/>: when that is a host a friend shares and it turns
    /// the request away for its owner's work (<c>job.busy</c> with detail owner, or <c>job.preempted</c>), the desktop log says so
    /// (once a minute at most) and <see cref="OwnerFirst"/> is raised the first time. Before the first answer a stop there is the
    /// owner's work too, so it reaches the queue as that host's owner refusal: the request moves on rather than waiting for it.</summary>
    internal static async IAsyncEnumerable<T> Watched<T>(string hostId, string job, IAsyncEnumerable<T> source,
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
    internal static bool IsShared(string hostId) =>
        DataDirectory is { } directory && Hosts(directory).Any(h => h.HostId == hostId && h.Shared);

    /// <summary>The one-answer form of <see cref="Watched{T}"/> (a transcription).</summary>
    internal static async Task<T> WatchedOnce<T>(string hostId, string job, Task<T> answer)
    {
        try { return await answer.ConfigureAwait(false); }
        catch (Audio2FaceHostException error) when ((error.OwnerFirst || error.Code == "job.preempted") && IsShared(hostId))
        {
            NoteOwnerFirst(hostId, job);
            if (error.OwnerFirst) throw;
            throw new Audio2FaceHostException("job.busy", error.Message) { Detail = Audio2FaceHostException.OwnerDetail };
        }
    }

    private static void NoteOwnerFirst(string hostId, string job)
    {
        bool first;
        lock (Gate)
        {
            var now = Environment.TickCount64;
            first = !OwnerNoticed.TryGetValue(hostId, out var last);
            if (!first && now - last < 60_000) return;
            OwnerNoticed[hostId] = now;
        }
        ErrorLog.Info($"Shared hosts: {hostId} turned this PC's {job} request away for its owner's own work (a friend shares it; their work comes first).");
        if (first) OwnerFirst?.Invoke(hostId, job);
    }

    /// <summary>A paired computer as a host target.</summary>
    internal static HostTextTarget TextTarget(PairedHost host, string routeId) => new(host.Pairing.Origin, host.HostId,
        host.Pairing.SpkiFingerprint, host.Pairing.DeviceId, HostPairingCredential.ToGuid(host.Pairing.CredentialId), routeId);
}
