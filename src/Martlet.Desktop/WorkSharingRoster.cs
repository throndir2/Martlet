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
internal static class WorkSharingRoster
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
    /// that does it there; <paramref name="model"/>, when given, the model it must run.</summary>
    internal static IReadOnlyList<(PairedHost? Host, string? Model)> Order(string? directory, string job, string roleKind, string? model,
        string planned)
    {
        if (directory is null) return [(null, null)];
        var hosts = Hosts(directory);
        var plan = Cached(Path.Combine(directory, ClusterSync.PlanFile), () => ClusterSync.LoadPlan(directory));
        var own = OwnHostId ?? hosts.FirstOrDefault(h => h.Method == HostSetupMethod.ThisPcDocker)?.HostId;
        var runs = plan.Nodes.Where(n => !n.Removed && n.HostId != planned && hosts.Any(h => h.HostId == n.HostId))
            .Select(n => (Node: n, Role: n.Roles.FirstOrDefault(r => r.Kind == roleKind && (model is null || r.Model == model))))
            .Where(p => p.Role is not null)
            .ToDictionary(p => p.Node.HostId, p => p.Role!.Model, StringComparer.Ordinal);
        var places = runs.Keys.Select(id => new WorkPlace(id, id == own,
            plan.Assignments.Count(a => ClusterJobs.All.Contains(a.Job) && a.HostId == id))).ToArray();
        var order = WorkSharing.Order(Settings(directory), job, Device, planned, places);
        return [.. order.Select(id => id == planned ? (hosts.FirstOrDefault(h => h.HostId == id), (string?)null)
            : (hosts.First(h => h.HostId == id), runs[id]))];
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
    /// or preempted (the host keeps its graphics card for a live turn: pool work waits or goes elsewhere, never a failure).</summary>
    internal static WorkRefusal Classify(Exception error) => error switch
    {
        Audio2FaceHostException { HeldForLive: true } => WorkRefusal.Preempted,
        Audio2FaceHostException { Code: "job.busy" or "worker.busy" } => WorkRefusal.Busy,
        Audio2FaceHostException { Code: "host.unreachable" or "host.redirect" or "worker.unavailable" or "worker.quarantined" } =>
            WorkRefusal.Unavailable,
        HostTextException { Code: ProviderFailureCode.ModelNotFound or ProviderFailureCode.CredentialUnavailable } => WorkRefusal.Unavailable,
        HttpRequestException or IOException => WorkRefusal.Unavailable,
        _ => WorkRefusal.None
    };

    /// <summary>A paired computer as a host target.</summary>
    internal static HostTextTarget TextTarget(PairedHost host, string routeId) => new(host.Pairing.Origin, host.HostId,
        host.Pairing.SpkiFingerprint, host.Pairing.DeviceId, HostPairingCredential.ToGuid(host.Pairing.CredentialId), routeId);
}
