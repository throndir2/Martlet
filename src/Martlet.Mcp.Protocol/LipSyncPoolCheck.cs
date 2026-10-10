using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Martlet.Core.Cluster;

namespace Martlet.Mcp;

/// <summary>lip_sync_pool_status and lip_sync_pool_check: lip-sync's pool of computers that run Audio2Face. The status reads a
/// data directory: who does lip-sync (avatar.json), the paired computers the shared plan says run the Audio2Face role
/// (cluster.json, hosts.json), the lip-sync choices (work-sharing.json) and the order this PC tries them in with the production
/// planner (<see cref="WorkSharing.Order"/>). The check rehearses the production pool (<see cref="LipSyncSharing"/> on
/// <see cref="WorkQueue"/>) with simulated Audio2Face computers that animate one chunk at a time and turn another away at once,
/// as a host's gateway does (job.busy). Nothing leaves the process.</summary>
internal static class LipSyncPoolCheck
{
    private const string Role = "audio2face";

    // ---------- lip_sync_pool_status ----------

    internal static object Status(string dataDirectory, string? device)
    {
        device ??= Martlet.Diagnostics.LocalLogs.ThisDeviceId();
        var (avatar, mode, assigned) = Avatar(dataDirectory);
        var plan = Plan(dataDirectory);
        var (paired, own) = Paired(dataDirectory);
        var settings = WorkSharingSettings.Load(dataDirectory);
        var rules = settings.Job(WorkSharingJobs.LipSync);
        var places = plan.Nodes.Where(n => !n.Removed && n.HostId != assigned && n.Roles.Any(r => r.Kind == Role) && paired.Contains(n.HostId))
            .Select(n => new WorkPlace(n.HostId, n.HostId == own, plan.Assignments.Count(a => ClusterJobs.All.Contains(a.Job) && a.HostId == n.HostId)))
            .ToArray();
        var pooled = assigned is not null && mode == "auto";
        return new
        {
            avatar, lipSync = mode, assigned, device, ownHost = own, paired,
            shares = rules.Shares, sharedByDefault = WorkSharingJobs.SharedByDefault(WorkSharingJobs.LipSync),
            order = rules.Order, never = rules.Never,
            runs = places.Select(p => p.HostId),
            tries = pooled ? WorkSharing.Order(settings, WorkSharingJobs.LipSync, device, assigned, places) : [],
            planHost = plan.For(ClusterJobs.LipSync)?.HostId,
            empty = mode == "loudness" ? "Lip-sync is off: the voice's loudness moves the mouth."
                : pooled ? null
                : "No computer does lip-sync: this PC's own Audio2Face service when it answers, otherwise the voice's loudness."
        };
    }

    // avatar.json: whether it exists, its lip-sync mode (auto, loudness or audio2face) and the host it names.
    private static (string State, string Mode, string? Host) Avatar(string dataDirectory)
    {
        var path = Path.Combine(dataDirectory, "avatar.json");
        if (!File.Exists(path)) return ("none", "auto", null);
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            var root = document.RootElement;
            var mode = root.TryGetProperty("lip_sync", out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()!.ToLowerInvariant() : "auto";
            var host = root.TryGetProperty("remote_host", out var remote) && remote.ValueKind == JsonValueKind.Object &&
                remote.TryGetProperty("host_id", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() : null;
            return ("loaded", mode, host);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return ("unreadable", "auto", null);
        }
    }

    private static ClusterPlan Plan(string dataDirectory)
    {
        try { return ClusterPlan.Parse(File.ReadAllBytes(Path.Combine(dataDirectory, "cluster.json"))); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Martlet.Core.Contracts.ContractException) { return ClusterPlan.Empty; }
    }

    // The paired host IDs in hosts.json and the one saved as this PC's own (Docker Desktop here).
    private static (string[] Hosts, string? Own) Paired(string dataDirectory)
    {
        try
        {
            var path = Path.Combine(dataDirectory, "hosts.json");
            if (!File.Exists(path) || new FileInfo(path).Length > 65_536) return ([], null);
            var hosts = (JsonNode.Parse(File.ReadAllText(path))?["hosts"] as JsonArray ?? []).OfType<JsonObject>().ToArray();
            return ([.. hosts.Select(h => h["pairing"]?["hostId"]?.GetValue<string>()).OfType<string>()],
                hosts.FirstOrDefault(h => h["method"]?.GetValue<string>() == "ThisPcDocker")?["pairing"]?["hostId"]?.GetValue<string>());
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            return ([], null);
        }
    }

    // ---------- lip_sync_pool_check ----------

    private sealed class Busy : Exception;
    private sealed class Gone : Exception;

    private static WorkRefusal Classify(Exception error) => error switch
    {
        Busy => WorkRefusal.Busy,
        Gone => WorkRefusal.Unavailable,
        _ => WorkRefusal.None
    };

    /// <summary>A simulated computer's Audio2Face relay: one chunk at a time (its gateway turns another away at once), each taking
    /// <see cref="Work"/>.</summary>
    private sealed class Face(string id, TimeSpan work)
    {
        private int running;
        public string Id { get; } = id;
        public TimeSpan Work { get; set; } = work;
        public bool Down { get; init; }

        public async IAsyncEnumerable<string> Animate([EnumeratorCancellation] CancellationToken token)
        {
            if (Down) throw new Gone();
            if (Interlocked.CompareExchange(ref running, 1, 0) != 0) throw new Busy();
            try
            {
                await Task.Delay(Work, token);
                yield return Id;
            }
            finally { Volatile.Write(ref running, 0); }
        }
    }

    // A companion PC (its own queue and pool) sending one chunk of reply turn; returns who animated it ("loudness" when skipped).
    private sealed record Chunk(string By, double WaitedMs);

    private static async Task<Chunk> ChunkAsync(LipSyncSharing pool, WorkQueue queue, Guid turn, IReadOnlyList<Face> faces,
        long samples = 24_000, int rate = 24_000)
    {
        var watch = Stopwatch.StartNew();
        await foreach (var by in pool.ChunkAsync(queue, turn, faces, f => f.Id, (f, t) => f.Animate(t), Classify,
            LipSyncSharing.Wait(samples, rate), null, CancellationToken.None))
            return new(by, Math.Round(watch.Elapsed.TotalMilliseconds - faces.First(f => f.Id == by).Work.TotalMilliseconds));
        return new("loudness", Math.Round(watch.Elapsed.TotalMilliseconds));
    }

    private static async Task<Chunk[]> ReplyAsync(LipSyncSharing pool, WorkQueue queue, Guid turn, IReadOnlyList<Face> faces, int chunks)
    {
        List<Chunk> list = [];
        for (var i = 0; i < chunks; i++) list.Add(await ChunkAsync(pool, queue, turn, faces));
        return [.. list];
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

        // The owner's network: companion PCs desk-1, desk-2 and desk-3; m4-host is assigned to lip-sync, m5-host also runs it.
        var none = new WorkSharingSettings();
        var order = WorkSharing.Order(none, WorkSharingJobs.LipSync, "desk-2", "m4-host", [new("m5-host")]);
        Step("Lip-sync is shared by default: the assigned computer first, then the others that run Audio2Face",
            order.SequenceEqual(["m4-host", "m5-host"]) && WorkSharingJobs.SharedByDefault(WorkSharingJobs.LipSync) &&
            WorkSharingJobs.Title(WorkSharingJobs.LipSync) == "Lip-sync" && !WorkSharingJobs.All.Contains(WorkSharingJobs.LipSync),
            new { order, title = WorkSharingJobs.Title(WorkSharingJobs.LipSync), devicesCard = WorkSharingJobs.All.Contains(WorkSharingJobs.LipSync) });

        var free = new LipSyncSharing();
        var first = await ChunkAsync(free, new WorkQueue(), Guid.NewGuid(), [new("m4-host", TimeSpan.FromMilliseconds(40)), new("m5-host", TimeSpan.FromMilliseconds(40))]);
        Step("A free first computer takes the chunk at once (no extra request, no added wait)",
            first.By == "m4-host" && first.WaitedMs < 50 && free.Moved == 0, new { first, free.Moved });

        // Two companion PCs speak at once: desk-2's first chunk finds m4-host busy with desk-1's and goes to m5-host, then each
        // reply stays on its computer. Each companion PC is its own process, so each has its own queue and pool.
        var m4 = new Face("m4-host", TimeSpan.FromMilliseconds(60));
        var m5 = new Face("m5-host", TimeSpan.FromMilliseconds(60));
        var (one, two) = (new LipSyncSharing(), new LipSyncSharing());
        var replyOne = ReplyAsync(one, new WorkQueue(), Guid.NewGuid(), [m4, m5], 4);
        await Task.Delay(15, cancellation);
        var turnTwo = Guid.NewGuid();
        var replyTwo = await ReplyAsync(two, new WorkQueue(), turnTwo, [m4, m5], 4);
        await replyOne;
        Step("Two companion PCs at once: the second's chunk goes to the next computer instead of the voice's loudness, and each reply stays on its computer",
            replyOne.Result.All(c => c.By == "m4-host") && replyTwo.All(c => c.By == "m5-host") && one.Skipped + two.Skipped == 0 && two.Moved == 1,
            new { desk1 = replyOne.Result.Select(c => c.By), desk2 = replyTwo.Select(c => c.By), desk2Moved = two.Moved });

        var again = await ChunkAsync(two, new WorkQueue(), Guid.NewGuid(), [m4, m5]);
        Step("The next reply chooses again from the order", again.By == "m4-host", new { again, last = two.LastHost });

        var slow4 = new Face("m4-host", TimeSpan.FromMilliseconds(700));
        var slow5 = new Face("m5-host", TimeSpan.FromMilliseconds(700));
        var hold4 = ChunkAsync(new LipSyncSharing(), new WorkQueue(), Guid.NewGuid(), [slow4]);
        var hold5 = ChunkAsync(new LipSyncSharing(), new WorkQueue(), Guid.NewGuid(), [slow5]);
        await Task.Delay(20, cancellation);
        var three = new LipSyncSharing();
        var skipped = await ChunkAsync(three, new WorkQueue { Retry = TimeSpan.FromMilliseconds(25) }, Guid.NewGuid(), [slow4, slow5]);
        await Task.WhenAll(hold4, hold5);
        Step("Every computer busy: a one-second chunk waits at most half a second, then the voice's loudness moves the mouth for it",
            skipped.By == "loudness" && three.Skipped == 1 && skipped.WaitedMs is >= 450 and < 680,
            new { skipped, wait = LipSyncSharing.Wait(24_000, 24_000).TotalMilliseconds });

        var busy4 = new Face("m4-host", TimeSpan.FromMilliseconds(150));
        var holding = ChunkAsync(new LipSyncSharing(), new WorkQueue(), Guid.NewGuid(), [busy4]);
        await Task.Delay(20, cancellation);
        var waiter = new LipSyncSharing();
        busy4.Work = TimeSpan.FromMilliseconds(20);
        var waited = await ChunkAsync(waiter, new WorkQueue { Retry = TimeSpan.FromMilliseconds(10) }, Guid.NewGuid(),
            [busy4, new Face("m5-host", TimeSpan.FromMilliseconds(20)) { Down = true }]);
        await holding;
        Step("A computer that frees within the wait takes the chunk", waited.By == "m4-host" && waiter.Skipped == 0 && waited.WaitedMs is > 80 and < 400,
            waited);

        var down = new Face("m4-host", TimeSpan.FromMilliseconds(20)) { Down = true };
        var passed = await ChunkAsync(new LipSyncSharing(), new WorkQueue(), Guid.NewGuid(), [down, new Face("m5-host", TimeSpan.FromMilliseconds(20))]);
        Step("A computer that doesn't answer is passed over at once", passed.By == "m5-host" && passed.WaitedMs < 50, passed);

        var kept = none.With(new WorkSharingHost { HostId = "m5-host", OnlyFor = ["desk-3"] });
        var keptOrder = WorkSharing.Order(kept, WorkSharingJobs.LipSync, "desk-2", "m4-host", [new("m5-host")]);
        var never = none.With(new WorkSharingJob { Job = WorkSharingJobs.LipSync, Never = ["m4-host"] });
        var neverOrder = WorkSharing.Order(never, WorkSharingJobs.LipSync, "desk-2", "m4-host", [new("m5-host")]);
        Step("A computer kept for another companion PC, or never used for lip-sync, is left out",
            keptOrder.SequenceEqual(["m4-host"]) && neverOrder.SequenceEqual(["m5-host"]), new { keptOrder, neverOrder });

        return new { ok, fixture = "simulated Audio2Face computers (NOT real hosts or models)", steps };
    }
}
