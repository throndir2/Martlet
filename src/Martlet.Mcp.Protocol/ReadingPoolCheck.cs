using System.IO;
using System.Text.Json.Nodes;
using Martlet.Core.Cluster;
using Martlet.Core.Reading;

namespace Martlet.Mcp;

/// <summary>reading_check's pool (docs/READING.md#the-reading-pool): with the Reading role, each read goes through the queue
/// (<see cref="WorkQueue"/>) at background priority to the computer named in Companion › Reading first, then the owner's other
/// computers that the shared plan says run the Reading role (<see cref="WorkSharing.Order"/> for
/// <see cref="WorkSharingJobs.Reading"/>), as the desktop's HostScreenTextReader does. <see cref="Status"/> gives that order for
/// a data directory from its files; <see cref="RunAsync"/> rehearses the production planner and queue with simulated computers
/// that read one screenshot at a time and turn another away at once (job.busy), NOT real hosts. Nothing leaves the process.</summary>
internal static class ReadingPoolCheck
{
    internal const string Role = "ocr";

    /// <summary>The order this PC's reads try the computers in, from reading.json, cluster.json, hosts.json and work-sharing.json.</summary>
    internal static object Status(string dataDirectory, ReadingSettings reading, string? device = null)
    {
        device ??= Martlet.Diagnostics.LocalLogs.ThisDeviceId();
        var sharing = WorkSharingSettings.Load(dataDirectory);
        var plan = Plan(dataDirectory);
        var (own, friends, ownHost) = Paired(dataDirectory);
        var chosen = reading.Place == ReadingPlace.Host ? reading.HostId : null;
        var places = plan.Nodes.Where(n => !n.Removed && n.HostId != chosen && n.Roles.Any(r => r.Kind == Role) &&
                (own.Contains(n.HostId) || friends.Contains(n.HostId)))
            .Select(n => new WorkPlace(n.HostId, n.HostId == ownHost, plan.Assignments.Count(a => ClusterJobs.All.Contains(a.Job) && a.HostId == n.HostId)))
            .ToArray();
        IReadOnlyList<string> tries = reading.Place != ReadingPlace.Host ? []
            : [.. WorkSharing.Order(sharing, WorkSharingJobs.Reading, device, chosen, places).Where(id => !friends.Contains(id) || id == chosen)];
        // With no computer named and none in the plan, the desktop tries every computer of the owner's own.
        var everyOwn = reading.Place == ReadingPlace.Host && chosen is null && tries.Count == 0;
        if (everyOwn) tries = [.. own.Where(id => sharing.Allows(id, device))];
        return new
        {
            pooled = reading.Place == ReadingPlace.Host, job = WorkSharingJobs.Reading, role = Role, priority = nameof(WorkPriority.Background),
            waitSeconds = 3, device, chosen, chosenIsFriends = chosen is not null && friends.Contains(chosen), ownHost,
            runs = places.Select(p => p.HostId), tries, everyOwn,
            kept = sharing.Hosts.Select(h => new { hostId = h.HostId, onlyFor = h.OnlyFor, usableHere = sharing.Allows(h.HostId, device) })
        };
    }

    private static ClusterPlan Plan(string dataDirectory)
    {
        try { return ClusterPlan.Parse(File.ReadAllBytes(Path.Combine(dataDirectory, "cluster.json"))); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Martlet.Core.Contracts.ContractException) { return ClusterPlan.Empty; }
    }

    // The owner's own paired hosts, the hosts friends share with this PC (access "friend") and the one saved as this PC's own.
    private static (string[] Own, string[] Friends, string? OwnHost) Paired(string dataDirectory)
    {
        try
        {
            var path = Path.Combine(dataDirectory, "hosts.json");
            if (!File.Exists(path) || new FileInfo(path).Length > 65_536) return ([], [], null);
            var hosts = (JsonNode.Parse(File.ReadAllText(path))?["hosts"] as JsonArray ?? []).OfType<JsonObject>()
                .Select(h => (Id: h["pairing"]?["hostId"]?.GetValue<string>(), Friend: h["access"]?.GetValue<string>() == "friend",
                    Method: h["method"]?.GetValue<string>()))
                .Where(h => h.Id is not null).ToArray();
            return ([.. hosts.Where(h => !h.Friend).Select(h => h.Id!)], [.. hosts.Where(h => h.Friend).Select(h => h.Id!)],
                hosts.FirstOrDefault(h => h.Method == "ThisPcDocker").Id);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException)
        {
            return ([], [], null);
        }
    }

    // ---------- the rehearsal ----------

    private sealed class Busy : Exception;
    private sealed class Gone : Exception;
    private sealed class OwnerBusy : Exception;

    private static WorkRefusal Classify(Exception error) => error switch
    {
        Busy => WorkRefusal.Busy,
        Gone => WorkRefusal.Unavailable,
        OwnerBusy => WorkRefusal.Owner,
        _ => WorkRefusal.None
    };

    /// <summary>A simulated computer's Reading role: one screenshot at a time (its gateway turns another away at once).</summary>
    private sealed class Reader(string id, TimeSpan work)
    {
        private int running;
        public string Id { get; } = id;
        public bool Down { get; init; }
        public bool OwnersWork { get; init; }

        public async Task<string> ReadAsync(CancellationToken token)
        {
            if (Down) throw new Gone();
            if (OwnersWork) throw new OwnerBusy();
            if (Interlocked.CompareExchange(ref running, 1, 0) != 0) throw new Busy();
            try
            {
                await Task.Delay(work, token);
                return Id;
            }
            finally { Volatile.Write(ref running, 0); }
        }
    }

    private sealed record Read(string? By, double WaitedMs, string? Refusal);

    private static async Task<Read> ReadAsync(WorkQueue queue, IReadOnlyList<Reader> order, TimeSpan wait, TimeSpan work)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var by = await queue.RunAsync(WorkSharingJobs.Reading, order, r => r.Id, (r, t) => r.ReadAsync(t), Classify,
                DateTimeOffset.UtcNow + wait, null, CancellationToken.None, WorkPriority.Background);
            return new(by, Math.Round(started.Elapsed.TotalMilliseconds - work.TotalMilliseconds), null);
        }
        catch (WorkPreemptedException) { return new(null, Math.Round(started.Elapsed.TotalMilliseconds), "later"); }
        catch (Busy) { return new(null, Math.Round(started.Elapsed.TotalMilliseconds), "busy"); }
    }

    internal static async Task<object> RunAsync(CancellationToken cancellation)
    {
        List<object> steps = [];
        var ok = true;
        void Step(string name, bool passed, object detail)
        {
            ok &= passed;
            steps.Add(new { name, passed, detail });
        }

        // The owner names gpu-pc in Companion › Reading; desk-host (this PC's own host service) and nas-host also run the role,
        // nas-host with more plan jobs. A friend's host is never in the owner's shared plan.
        WorkPlace[] places = [new("desk-host", Own: true, Jobs: 2), new("nas-host", Jobs: 3), new("spare-host", Jobs: 0)];
        var none = new WorkSharingSettings();
        var order = WorkSharing.Order(none, WorkSharingJobs.Reading, "desk-1", "gpu-pc", places);
        Step("The named computer first, then this PC's own host service, then the rest fewest jobs first",
            order.SequenceEqual(["gpu-pc", "desk-host", "spare-host", "nas-host"]), new { order });
        var kept = WorkSharing.Order(none.With(new WorkSharingHost { HostId = "desk-host", OnlyFor = ["desk-2"] }), WorkSharingJobs.Reading,
            "desk-1", "gpu-pc", places);
        Step("A computer kept for another companion PC is left out", kept.SequenceEqual(["gpu-pc", "spare-host", "nas-host"]), new { kept });
        Step("Reading is shared by default but has no Devices › Sharing work card",
            WorkSharingJobs.SharedByDefault(WorkSharingJobs.Reading) && !WorkSharingJobs.All.Contains(WorkSharingJobs.Reading),
            new { sharedByDefault = WorkSharingJobs.SharedByDefault(WorkSharingJobs.Reading), all = WorkSharingJobs.All });

        var work = TimeSpan.FromMilliseconds(150);
        var wait = TimeSpan.FromSeconds(3);
        var gpu = new Reader("gpu-pc", work);
        var desk = new Reader("desk-host", work);
        var free = await ReadAsync(new WorkQueue(), [gpu, desk], wait, work);
        Step("The named computer is free: it reads, with no wait", free.By == "gpu-pc" && free.WaitedMs < 100, free);

        var holding = ReadAsync(new WorkQueue(), [gpu], wait, work);
        await Task.Delay(20, cancellation);
        var passed = await ReadAsync(new WorkQueue(), [gpu, desk], wait, work);
        await holding;
        Step("The named computer is busy: the next computer reads at once", passed.By == "desk-host" && passed.WaitedMs < 100, passed);

        var slow = new Reader("gpu-pc", TimeSpan.FromMilliseconds(400));
        var quick = new Reader("desk-host", TimeSpan.FromMilliseconds(200));
        var one = ReadAsync(new WorkQueue(), [slow], wait, TimeSpan.FromMilliseconds(400));
        var two = ReadAsync(new WorkQueue(), [quick], wait, TimeSpan.FromMilliseconds(200));
        await Task.Delay(20, cancellation);
        var waited = await ReadAsync(new WorkQueue { Retry = TimeSpan.FromMilliseconds(20) }, [slow, quick], wait, TimeSpan.FromMilliseconds(200));
        await Task.WhenAll(one, two);
        Step("Every computer is busy: the read waits and the first to free reads it",
            waited.By == "desk-host" && waited.WaitedMs is > 100 and < 400, waited);

        var stuck = new Reader("gpu-pc", TimeSpan.FromMilliseconds(500));
        var busyHold = ReadAsync(new WorkQueue(), [stuck], wait, TimeSpan.FromMilliseconds(500));
        await Task.Delay(20, cancellation);
        var gaveUp = await ReadAsync(new WorkQueue { Retry = TimeSpan.FromMilliseconds(20) }, [stuck], TimeSpan.FromMilliseconds(100), work);
        await busyHold;
        Step("Still busy after the wait: the read gives up as busy, and the next screenshot tries again", gaveUp.Refusal == "busy", gaveUp);

        var down = await ReadAsync(new WorkQueue(), [new Reader("gpu-pc", work) { Down = true }, new Reader("desk-host", work)], wait, work);
        Step("A computer that doesn't answer is passed over at once", down.By == "desk-host" && down.WaitedMs < 100, down);

        var friend = await ReadAsync(new WorkQueue(), [new Reader("friend-pc", work) { OwnersWork = true }, new Reader("desk-host", work)], wait, work);
        Step("A friend's host named in Companion › Reading is busy with its owner's work: your own computer reads",
            friend.By == "desk-host", friend);
        var friendOnly = await ReadAsync(new WorkQueue(), [new Reader("friend-pc", work) { OwnersWork = true }], wait, work);
        Step("Only that friend's host: the read waits for later, never a failure", friendOnly.Refusal == "later", friendOnly);

        return new { ok, fixture = "simulated computers (NOT real hosts or models)", steps };
    }
}
