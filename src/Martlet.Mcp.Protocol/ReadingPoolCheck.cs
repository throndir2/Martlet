using System.IO;
using System.Text.Json.Nodes;
using Martlet.Core.Cluster;
using Martlet.Core.Reading;

namespace Martlet.Mcp;

/// <summary>reading_check's pool (docs/READING.md#the-reading-pool): Companion › Reading's list on this PC (pools-local.json,
/// <see cref="PoolAreas.Reading"/>; until the page saves one, the list made from reading.json). Each read goes through the queue
/// (<see cref="WorkQueue"/>, lane reading) at background priority to the list's places in order (<see cref="ReadingPool.Targets"/>),
/// as the desktop's PoolScreenTextReader does. <see cref="Status"/> gives the list and those places for a data directory from its
/// files; <see cref="RunAsync"/> rehearses the production list code, planner and queue with simulated computers that read one
/// screenshot at a time and turn another away at once (job.busy), NOT real hosts. Nothing leaves the process.</summary>
internal static class ReadingPoolCheck
{
    internal const string Role = ReadingPool.Role;

    /// <summary>This PC's Reading list and the places a read tries, from pools-local.json (else reading.json, cluster.json and
    /// work-sharing.json, as the desktop makes the list once) and hosts.json.</summary>
    internal static object Status(string dataDirectory, string? device = null)
    {
        device ??= Martlet.Diagnostics.LocalLogs.ThisDeviceId(dataDirectory);
        var (own, friends, ownHost) = Paired(dataDirectory);
        var saved = PoolSettings.LoadFor(dataDirectory, PoolAreas.Reading);
        var list = saved ?? ReadingPool.FromChoice(ReadingSettings.Load(dataDirectory), ownHost, Others(dataDirectory, device, own, friends, ownHost));
        var paired = own.Concat(friends).ToHashSet(StringComparer.Ordinal);
        return new
        {
            area = PoolAreas.Reading.Id, file = PoolSettings.File(PoolAreas.Reading.Shared),
            list = saved is null ? "made from reading.json (not saved yet)" : "saved", off = list.NoneOn,
            lane = PoolAreas.Reading.Id, priority = nameof(WorkPriority.Background), waitSeconds = 3, device, ownHost,
            members = list.Members.Select((m, i) => new
            {
                index = i, key = m.Key, name = m.Name, kind = m.Kind.ToString(), on = !m.Off, reads = ReadingPool.Describe(m),
                engine = m.Setting(PoolSettingKeys.Engine), model = m.Setting(PoolSettingKeys.Model), onlyFor = m.OnlyFor,
                paired = m.HostId is null || paired.Contains(m.HostId)
            }),
            tries = ReadingPool.Targets(list, device, ownHost, paired.Contains).Select(t => new { key = t.Key, hostId = t.HostId, member = t.Member.Key })
        };
    }

    // The other computers reading.json's Reading role tried after the one chosen, as the desktop makes the list: the shared
    // plan's computers that run the role (Devices › Sharing work's order); with none chosen and none there, every own computer.
    private static IReadOnlyList<string> Others(string dataDirectory, string device, string[] own, string[] friends, string? ownHost)
    {
        var reading = ReadingSettings.Load(dataDirectory);
        if (reading.Place != ReadingPlace.Host) return [];
        var sharing = WorkSharingSettings.Load(dataDirectory);
        var plan = Plan(dataDirectory);
        var places = plan.Nodes.Where(n => !n.Removed && n.HostId != reading.HostId && n.Roles.Any(r => r.Kind == Role) &&
                (own.Contains(n.HostId) || friends.Contains(n.HostId)))
            .Select(n => new WorkPlace(n.HostId, n.HostId == ownHost, plan.Assignments.Count(a => ClusterJobs.All.Contains(a.Job) && a.HostId == n.HostId)))
            .ToArray();
        var others = WorkSharing.Order(sharing, WorkSharingJobs.Reading, device, reading.HostId, places).Where(id => id != reading.HostId).ToList();
        return reading.HostId is null && others.Count == 0 ? own : others;
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

        // This PC (desk-1, its own host service desk-host) lists Windows OCR, gpu-pc and more; nas-host isn't paired here.
        bool Paired(string id) => id is "gpu-pc" or "desk-host" or "kept-pc" or "off-pc";
        var list = new PoolList
        {
            Area = PoolAreas.Reading.Id,
            Members =
            [
                ReadingPool.Windows(), PoolMember.Computer("gpu-pc"), PoolMember.Gpu("gpu-pc", 2), PoolMember.Computer("off-pc") with { Off = true },
                PoolMember.Computer("kept-pc") with { OnlyFor = ["desk-2"] }, PoolMember.Computer("nas-host"), PoolMember.Computer("desk-host")
            ]
        };
        var tries = ReadingPool.Targets(list, "desk-1", "desk-host", Paired).Select(t => t.Key).ToArray();
        Step("The list's order: Windows OCR on this PC, then gpu-pc once (a card is its computer), then desk-host; a member that is " +
            "off, kept for another companion PC or not paired here is left out",
            tries.SequenceEqual(["this-pc", "host:gpu-pc", "host:desk-host"]), new { tries });
        var role = ReadingPool.Targets(new PoolList { Area = PoolAreas.Reading.Id, Members = [ReadingPool.ThisPcRole(), PoolMember.Computer("desk-host")] },
            "desk-1", "desk-host", Paired).Select(t => t.Key).ToArray();
        Step("This PC with the Reading role is its own host service, counted once", role.SequenceEqual(["host:desk-host"]), new { role });
        var allOff = new PoolList { Area = PoolAreas.Reading.Id, Members = [ReadingPool.Windows() with { Off = true }] };
        Step("Nothing on, or an empty list: reading is off",
            allOff.NoneOn && new PoolList { Area = PoolAreas.Reading.Id }.NoneOn && ReadingPool.Targets(allOff, "desk-1", null, Paired).Count == 0,
            new { allOff = allOff.NoneOn });
        string[] Keys(PoolList made) => [.. made.Members.Select(m => m.Key + (m.Setting(PoolSettingKeys.Engine) is { } e ? "=" + e : ""))];
        var windows = Keys(ReadingPool.FromChoice(new ReadingSettings(), "desk-host", []));
        var host = Keys(ReadingPool.FromChoice(new ReadingSettings { Place = ReadingPlace.Host, HostId = "gpu-pc" }, "desk-host", ["desk-host", "nas-host"]));
        var off = Keys(ReadingPool.FromChoice(new ReadingSettings { Place = ReadingPlace.Off }, "desk-host", []));
        Step("The older choice (reading.json) becomes the list once: Windows OCR as This PC; the Reading role's computer first, " +
            "this PC's own host service as This PC with the role; off as an empty list",
            windows.SequenceEqual(["this-pc=windows-ocr"]) && host.SequenceEqual(["host:gpu-pc", "this-pc=ocr", "host:nas-host"]) && off.Length == 0,
            new { windows, host, off });
        Step("Each PC keeps its own Reading list (pools-local.json), as reading.json was never shared",
            !PoolAreas.Reading.Shared && !PoolAreas.Reading.Required && PoolSettings.File(PoolAreas.Reading.Shared) == PoolSettings.LocalFileName,
            new { shared = PoolAreas.Reading.Shared, file = PoolSettings.File(PoolAreas.Reading.Shared) });

        var work = TimeSpan.FromMilliseconds(150);
        var wait = TimeSpan.FromSeconds(3);
        var gpu = new Reader("gpu-pc", work);
        var desk = new Reader("desk-host", work);
        var free = await ReadAsync(new WorkQueue(), [gpu, desk], wait, work);
        Step("The first in the list is free: it reads, with no wait", free.By == "gpu-pc" && free.WaitedMs < 100, free);

        var holding = ReadAsync(new WorkQueue(), [gpu], wait, work);
        await Task.Delay(20, cancellation);
        var passed = await ReadAsync(new WorkQueue(), [gpu, desk], wait, work);
        await holding;
        Step("The first in the list is busy: the next one reads at once", passed.By == "desk-host" && passed.WaitedMs < 100, passed);

        var slow = new Reader("gpu-pc", TimeSpan.FromMilliseconds(400));
        var quick = new Reader("desk-host", TimeSpan.FromMilliseconds(200));
        var one = ReadAsync(new WorkQueue(), [slow], wait, TimeSpan.FromMilliseconds(400));
        var two = ReadAsync(new WorkQueue(), [quick], wait, TimeSpan.FromMilliseconds(200));
        await Task.Delay(20, cancellation);
        var waited = await ReadAsync(new WorkQueue { Retry = TimeSpan.FromMilliseconds(20) }, [slow, quick], wait, TimeSpan.FromMilliseconds(200));
        await Task.WhenAll(one, two);
        Step("Every one is busy: the read waits and the first to free reads it",
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
        Step("A friend's host first in the list is busy with its owner's work: your own computer reads",
            friend.By == "desk-host", friend);
        var friendOnly = await ReadAsync(new WorkQueue(), [new Reader("friend-pc", work) { OwnersWork = true }], wait, work);
        Step("Only that friend's host: the read waits for later, never a failure", friendOnly.Refusal == "later", friendOnly);

        return new { ok, fixture = "simulated computers (NOT real hosts or models)", steps };
    }
}
