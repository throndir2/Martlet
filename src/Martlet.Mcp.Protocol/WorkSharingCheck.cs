using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Martlet.Core.Cluster;
using Martlet.Core.Settings;

namespace Martlet.Mcp;

/// <summary>work_sharing_status and work_sharing_check: Devices › Sharing work. The status reads a data directory's choices
/// (work-sharing.json), the shared plan's record of which paired computers run each job's engine (cluster.json), this PC's
/// pairings (hosts.json) and the computer each job uses now (settings.json), and gives the order this PC tries them in with the
/// production planner (<see cref="WorkSharing.Order"/>). The check rehearses the production planner and queue
/// (<see cref="WorkQueue"/>) on the four-computer network from the owner's example with simulated computers that run one
/// request at a time and turn another away at once, as a host's gateway does (job.busy). Nothing leaves the process.</summary>
internal static class WorkSharingCheck
{
    // ---------- work_sharing_status ----------

    internal static async Task<object> StatusAsync(string dataDirectory, string? device, CancellationToken cancellation)
    {
        var path = Path.Combine(dataDirectory, WorkSharingSettings.FileName);
        var settings = WorkSharingSettings.Load(dataDirectory);
        device ??= Martlet.Diagnostics.LocalLogs.ThisDeviceId();
        var plan = Plan(dataDirectory);
        var (paired, own) = Paired(dataDirectory);
        var loaded = await new SettingsStore(dataDirectory).LoadAsync(cancellation);
        var routes = loaded.Settings?.Setup?.Routes ?? [];
        string? Planned(SetupRole role) => routes.FirstOrDefault(r => r.Role == role) is { Gateway: { } gateway } route &&
            SelfHostSetup.IsGateway(route.RouteType) ? gateway.HostId : null;
        var speaking = routes.FirstOrDefault(r => r.Role == SetupRole.Tts);
        var voiceRole = SpeechEngines.ForRoute(speaking?.GatewaySnapshot?.RouteId)?.HostRoleKind ?? SpeechEngines.Chatterbox.HostRoleKind;
        var jobs = WorkSharingJobs.All.Select(job =>
        {
            var (kind, planned) = job switch
            {
                WorkSharingJobs.Thinking => ("ollama", Planned(SetupRole.Llm)),
                WorkSharingJobs.Listening => ("stt", Planned(SetupRole.Stt)),
                WorkSharingJobs.Speaking => (voiceRole, Planned(SetupRole.Tts)),
                _ => ("deep-thinking", (string?)null)
            };
            var places = plan.Nodes.Where(n => !n.Removed && n.Roles.Any(r => r.Kind == kind) && paired.Contains(n.HostId))
                .Select(n => new WorkPlace(n.HostId, n.HostId == own, plan.Assignments.Count(a => ClusterJobs.All.Contains(a.Job) && a.HostId == n.HostId)))
                .ToArray();
            var rules = settings.Job(job);
            return new
            {
                job, title = WorkSharingJobs.Title(job), role = kind, shares = rules.Shares, sharedByDefault = WorkSharingJobs.SharedByDefault(job),
                order = rules.Order, never = rules.Never, planned, runs = places.Select(p => p.HostId),
                tries = job == WorkSharingJobs.DeepThinking ? null : WorkSharing.Order(settings, job, device, planned, places)
            };
        }).ToArray();
        // The pool lists (pools.json and pools-local.json): each area's members in order, and for Speaking, Listening and Thinking
        // the computers this PC (or deviceId) tries with the production planner (PoolRouting.Hosts).
        var shared = PoolSettings.Load(dataDirectory);
        var local = PoolSettings.Load(dataDirectory, shared: false);
        var pools = PoolAreas.All.Select(area =>
        {
            var file = area.Shared ? shared : local;
            if (!file.Has(area.Id)) return null;
            var list = file.Pool(area.Id);
            var order = PoolRouting.Order(area, list, device);
            var job = jobs.FirstOrDefault(j => j.job == area.Id);
            return (object)new
            {
                area = area.Id, title = area.Title, page = area.Page, shared = area.Shared, required = area.Required, whenEmpty = area.WhenEmpty,
                off = order.Off, fallback = order.Fallback,
                members = list.Members.Select(m => new
                {
                    key = m.Key, kind = m.Kind.ToString(), name = m.Name, on = !m.Off, onlyFor = m.OnlyFor, settings = m.Settings,
                    usableHere = m.Allows(device), agreed = m.Consented(area.Id)
                }),
                tries = job?.planned is { } planned
                    ? PoolRouting.Hosts(area, list, device, planned, own, job.runs.ToHashSet(StringComparer.Ordinal)) : null
            };
        }).OfType<object>().ToArray();
        return new
        {
            file = File.Exists(path) ? "loaded" : "none", shared = settings.Share(), isDefault = settings.IsDefault, device, ownHost = own,
            paired, planHosts = plan.Nodes.Count(n => !n.Removed), jobs,
            kept = settings.Hosts.Select(h => new { hostId = h.HostId, onlyFor = h.OnlyFor, usableHere = settings.Allows(h.HostId, device) }),
            poolsFile = File.Exists(Path.Combine(dataDirectory, PoolSettings.FileName)) ? "loaded" : "none",
            poolsLocalFile = File.Exists(Path.Combine(dataDirectory, PoolSettings.LocalFileName)) ? "loaded" : "none",
            pools
        };
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
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException)
        {
            return ([], null);
        }
    }

    // ---------- work_sharing_check ----------

    private sealed class Busy : Exception;
    private sealed class Gone : Exception;
    /// <summary>A cloud provider's rate limit (HTTP 429).</summary>
    private sealed class Limited : Exception;

    private static WorkRefusal Classify(Exception error) => error switch
    {
        Busy => WorkRefusal.Busy,
        Gone => WorkRefusal.Unavailable,
        Limited => PoolRefusals.Http(429),
        _ => WorkRefusal.None
    };

    /// <summary>A simulated computer's voice: one segment at a time (its gateway turns another away at once), each taking
    /// <see cref="Work"/>.</summary>
    private sealed class Voice(string id, TimeSpan work)
    {
        private int running;
        public string Id { get; } = id;
        public TimeSpan Work { get; } = work;
        public bool Down { get; set; }
        public bool RateLimited { get; set; }
        public List<string> Served { get; } = [];

        public async IAsyncEnumerable<string> Speak(string who, [EnumeratorCancellation] CancellationToken token)
        {
            if (Down) throw new Gone();
            if (RateLimited) throw new Limited();
            if (Interlocked.CompareExchange(ref running, 1, 0) != 0) throw new Busy();
            try
            {
                lock (Served) Served.Add(who);
                await Task.Delay(Work, token);
                yield return $"{who} spoken by {Id}";
            }
            finally { Volatile.Write(ref running, 0); }
        }
    }

    private sealed record Spoken(string By, double WaitedMs);

    private static async Task<Spoken> SpeakAsync(WorkQueue queue, string who, IReadOnlyList<Voice> order)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        await foreach (var answer in queue.StreamAsync(WorkSharingJobs.Speaking, order, v => v.Id, (v, t) => v.Speak(who, t), Classify,
            DateTimeOffset.UtcNow.AddSeconds(10), null, CancellationToken.None))
        {
            var by = answer[(answer.LastIndexOf(' ') + 1)..];
            return new(by, Math.Round(started.Elapsed.TotalMilliseconds - order.First(v => v.Id == by).Work.TotalMilliseconds));
        }
        throw new InvalidOperationException("No answer.");
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

        // The owner's network: three companion PCs, each with its host service; machine 4 a host for lip-sync and pictures.
        WorkPlace[] Voices(string own) => [.. new[] { "m1-host", "m3-host" }.Select(id => new WorkPlace(id, id == own, id == "m1-host" ? 1 : 0))];
        var none = new WorkSharingSettings();
        var ownFirst = none.With(new WorkSharingJob { Job = WorkSharingJobs.Speaking, Order = [WorkSharingSettings.ThisPc] });
        var desk1 = WorkSharing.Order(ownFirst, WorkSharingJobs.Speaking, "desk-1", "m1-host", Voices("m1-host"));
        var desk2 = WorkSharing.Order(ownFirst, WorkSharingJobs.Speaking, "desk-2", "m1-host", Voices("m2-host"));
        var desk3 = WorkSharing.Order(ownFirst, WorkSharingJobs.Speaking, "desk-3", "m1-host", Voices("m3-host"));
        Step("Each companion PC tries its own voice first; machine 2, without one, tries machine 1 then machine 3",
            desk1.SequenceEqual(["m1-host", "m3-host"]) && desk2.SequenceEqual(["m1-host", "m3-host"]) && desk3.SequenceEqual(["m3-host", "m1-host"]),
            new { desk1, desk2, desk3 });
        var thinking = WorkSharing.Order(none, WorkSharingJobs.Thinking, "desk-2", "m1-host", [new("m1-host"), new("m3-host")]);
        Step("Thinking stays on its own computer unless shared (prompt caches)", thinking.SequenceEqual(["m1-host"]), new { thinking });

        // Machines 1 and 3 each speak their own long segment; machine 2 asks for its voice meanwhile and whichever frees first
        // takes it. Each companion PC is its own process, so each has its own queue.
        var m1 = new Voice("m1-host", TimeSpan.FromMilliseconds(400));
        var m3 = new Voice("m3-host", TimeSpan.FromMilliseconds(250));
        var (q1, q2, q3) = (new WorkQueue(), new WorkQueue { Retry = TimeSpan.FromMilliseconds(25) }, new WorkQueue());
        var one = SpeakAsync(q1, "desk-1", [m1, m3]);
        var three = SpeakAsync(q3, "desk-3", [m3, m1]);
        await Task.Delay(30, cancellation);
        var two = await SpeakAsync(q2, "desk-2", [m1, m3]);
        await Task.WhenAll(one, three);
        Step("Both voices busy: machine 2's segment waits and the first to free (machine 3) speaks it",
            two.By == "m3-host" && one.Result.By == "m1-host" && three.Result.By == "m3-host" && two.WaitedMs is > 150 and < 400,
            new { desk1 = one.Result, desk2 = two, desk3 = three.Result });

        var a = new Voice("m1-host", TimeSpan.FromMilliseconds(300));
        var b = new Voice("m3-host", TimeSpan.FromMilliseconds(50));
        var holding = SpeakAsync(new WorkQueue(), "desk-1", [a, b]);
        await Task.Delay(20, cancellation);
        var passed = await SpeakAsync(new WorkQueue(), "desk-2", [a, b]);
        await holding;
        Step("Machine 1 busy: machine 2's segment goes straight to machine 3 without waiting", passed.By == "m3-host" && passed.WaitedMs < 100, passed);

        var four = new Voice("m1-host", TimeSpan.FromMilliseconds(120));
        var five = new Voice("m3-host", TimeSpan.FromMilliseconds(120));
        var queue = new WorkQueue { Retry = TimeSpan.FromMilliseconds(10) };
        var burst = await Task.WhenAll(Enumerable.Range(1, 4).Select(i => SpeakAsync(queue, "desk-2#" + i, [four, five])));
        Step("Four segments at once from one PC spread over both voices, two each, the rest in line",
            burst.Count(r => r.By == "m1-host") == 2 && burst.Count(r => r.By == "m3-host") == 2, burst);

        var kept = none.With(new WorkSharingHost { HostId = "m3-host", OnlyFor = ["desk-3"] });
        var keptOrder = WorkSharing.Order(kept, WorkSharingJobs.Speaking, "desk-2", "m1-host", Voices("m2-host"));
        var keptOwn = WorkSharing.Order(kept, WorkSharingJobs.Speaking, "desk-3", "m1-host", Voices("m3-host"));
        Step("Machine 3 kept for its own companion: machine 2 never sends it work, machine 3 still uses it",
            keptOrder.SequenceEqual(["m1-host"]) && keptOwn.Contains("m3-host"), new { desk2 = keptOrder, desk3 = keptOwn });

        var never = none.With(new WorkSharingJob { Job = WorkSharingJobs.Speaking, Never = ["m1-host"] });
        var neverOrder = WorkSharing.Order(never, WorkSharingJobs.Speaking, "desk-2", "m1-host", Voices("m2-host"));
        Step("A computer unticked for Speaking is left out, even as the planned one", neverOrder.SequenceEqual(["m3-host"]), new { neverOrder });

        var down = new Voice("m1-host", TimeSpan.FromMilliseconds(10)) { Down = true };
        var up = new Voice("m3-host", TimeSpan.FromMilliseconds(10));
        var failedOver = await SpeakAsync(new WorkQueue(), "desk-2", [down, up]);
        Step("A computer that doesn't answer is passed over at once", failedOver.By == "m3-host", failedOver);

        var deep = DeepThinkingPool.For(Host("m2-host").WithPool([Host("m4-host")]), [],
            none.With(new WorkSharingHost { HostId = "m4-host", OnlyFor = ["desk-4"] }), "desk-2");
        Step("Deep thinking leaves out a computer kept for another companion PC",
            deep.Usable.Select(s => s.Key).SequenceEqual(["host:m2-host"]), new { usable = deep.Usable.Select(s => s.Key), why = deep.Find("host:m4-host")?.Plan.Why });

        var shared = ownFirst.With(new WorkSharingHost { HostId = "m3-host", OnlyFor = ["desk-3"] }).Share();
        Step("The choices travel as the work-sharing shared setting and read back the same",
            WorkSharingSettings.Parse(shared)?.Share() == shared && Martlet.Core.Sync.SharedSettings.IsKey(WorkSharingSettings.SharedKey), new { shared });

        // Pools: the same network as lists on Companion › Voice (docs/CLUSTER.md#pools-one-ordered-list-of-members-per-area).
        var migrated = PoolMigration.FromWorkSharing(PoolAreas.Speaking, ownFirst.With(new WorkSharingHost { HostId = "m3-host", OnlyFor = ["desk-3"] }),
            PoolMember.Computer("m1-host"), Voices("m2-host"));
        Step("Sharing work's order and kept-for choices move into the Speaking list",
            migrated.Members.Select(m => m.Key).SequenceEqual(["this-pc", "host:m1-host", "host:m3-host"]) &&
            migrated.Find("host:m3-host")!.OnlyFor.SequenceEqual(["desk-3"]), new { members = migrated.Members.Select(m => m.Key) });
        var runs = new HashSet<string>(["m1-host", "m3-host"], StringComparer.Ordinal);
        var poolDesk2 = PoolRouting.Hosts(PoolAreas.Speaking, migrated, "desk-2", "m1-host", null, runs);
        var poolDesk3 = PoolRouting.Hosts(PoolAreas.Speaking, migrated, "desk-3", "m1-host", "m3-host", runs);
        Step("The list's order: machine 3 first on its own companion PC, kept from machine 2",
            poolDesk2.SequenceEqual(["m1-host"]) && poolDesk3.SequenceEqual(["m3-host", "m1-host"]), new { desk2 = poolDesk2, desk3 = poolDesk3 });
        var offList = migrated.With(migrated.Find("host:m1-host")! with { Off = true });
        var thinkingList = new PoolList { Area = PoolAreas.Thinking.Id, Members = [PoolMember.Computer("m3-host")] };
        Step("A member turned off takes no work; Thinking's own model always goes first",
            PoolRouting.Hosts(PoolAreas.Speaking, offList, "desk-3", "m1-host", "m3-host", runs).SequenceEqual(["m3-host"]) &&
            PoolRouting.Hosts(PoolAreas.Thinking, thinkingList, "desk-2", "m1-host", null, runs).SequenceEqual(["m1-host", "m3-host"]),
            new { speaking = PoolRouting.Hosts(PoolAreas.Speaking, offList, "desk-3", "m1-host", "m3-host", runs) });
        var empty = PoolRouting.Order(PoolAreas.Speaking, new PoolList { Area = PoolAreas.Speaking.Id }, "desk-2");
        var lipSync = PoolRouting.Order(PoolAreas.LipSync, new PoolList { Area = PoolAreas.LipSync.Id }, "desk-2");
        Step("An empty list is off for Speaking and voice loudness for lip-sync", empty.Off && !lipSync.Off && lipSync.Fallback,
            new { speakingOff = empty.Off, lipSyncFallback = lipSync.Fallback, lipSync = PoolAreas.LipSync.WhenEmpty });
        var cloud = PoolMember.Cloud("openai", "gpt-4o-mini-tts").WithConsent(PoolAreas.Speaking.Id, DateTimeOffset.UnixEpoch);
        var limited = new Voice(cloud.Key, TimeSpan.FromMilliseconds(10)) { RateLimited = true };
        var byCloud = await SpeakAsync(new WorkQueue(), "desk-2", [limited, new Voice("m3-host", TimeSpan.FromMilliseconds(10))]);
        Step("A cloud member at its rate limit is passed over like a busy computer (by its member key)", byCloud.By == "m3-host", byCloud);
        // The Speaking list machine 1, then the cloud member: a free machine 1 speaks at once and the cloud is never asked; a busy
        // machine 1 hands the segment to the cloud member in its turn (PoolRouting.Stops, NOT a real provider).
        var stops = PoolRouting.Stops(PoolAreas.Speaking, new PoolList { Area = PoolAreas.Speaking.Id, Members = [PoolMember.Computer("m1-host"), cloud] },
            "desk-2", "m1-host", null, new HashSet<string>(StringComparer.Ordinal), _ => true);
        var first = new Voice("m1-host", TimeSpan.FromMilliseconds(200));
        var provider = new Voice(cloud.Key, TimeSpan.FromMilliseconds(20));
        Voice[] turn = [.. stops.Select(s => s.HostId is null ? provider : first)];
        var free = await SpeakAsync(new WorkQueue(), "desk-2", turn);
        var hold = SpeakAsync(new WorkQueue(), "desk-1", [first]);
        await Task.Delay(20, cancellation);
        var inTurn = await SpeakAsync(new WorkQueue(), "desk-2", turn);
        await hold;
        Step("A cloud member in the list takes a segment only when the computer before it is busy (a free first computer adds no latency)",
            stops.Select(s => s.Key).SequenceEqual(["m1-host", cloud.Key]) && free.By == "m1-host" && inTurn.By == cloud.Key &&
            provider.Served.Count == 1 && inTurn.WaitedMs < 100, new { stops = stops.Select(s => s.Key), free, inTurn, cloudAsked = provider.Served.Count });
        var poolsShared = new PoolSettings().With(migrated).Share();
        Step("The lists travel as the pools shared setting and read back the same",
            PoolSettings.Parse(poolsShared)?.Share() == poolsShared && Martlet.Core.Sync.SharedSettings.IsKey(PoolSettings.SharedKey), new { poolsShared });

        return new { ok, fixture = "simulated computers (NOT real hosts or models)", steps };
    }

    private static DeepThinkingSettings Host(string id) => new()
    {
        Place = DeepThinkingPlace.Host, HostId = id, HostOrigin = "https://192.168.1.2:9443", ModelId = "qwen3:8b",
        HostSpkiFingerprint = "sha256:" + new string('0', 64), HostDeviceId = "desk-2", HostCredentialId = Guid.NewGuid(),
        HostRouteId = SelfHostSetup.DeepThinkingRouteId
    };
}
