using System.IO;
using System.Text.Json.Nodes;
using Martlet.Core.Cluster;
using Martlet.Core.Installation;
using Martlet.Core.Planning;
using Martlet.Core.Platforms;
using Martlet.Core.Settings;
using Martlet.Mcp.Shared;

namespace Martlet.Mcp;

/// <summary>recommended_setup_status: Home's Recommended setup without the desktop. It builds the network recommender's request
/// with the desktop's own builder (RecommendedSetupInputs) from a data directory (hosts.json, host-hardware.json, cluster.json,
/// settings.json, work-sharing.json, thinking-pool.json, speaking-engine.txt) or from the built-in fixture network, runs the
/// production recommender (<see cref="NetworkRecommender"/>) and lists the computers, today's jobs, the recommended changes
/// and whether a companion PC in use would ask (SetupAskRule, with this data directory's declined setups). Read-only: it
/// contacts nothing, and it reads no keys. A data directory has no live host checks: a host the presence report
/// (node-presence.json, written by the desktop) last saw not answering counts as offline, as the desktop plans it; every
/// other host counts as online, and its roles are the shared plan's record. This PC's own hardware is its host service's
/// report (the desktop reads it live).</summary>
internal static class RecommendedSetupStatus
{
    internal const string FixtureName = "network";
    internal const string OfflineFixtureName = "offline";

    internal static async Task<object> RunAsync(string? dataDirectory, string? fixture, CancellationToken cancellation)
    {
        SetupSources sources;
        string source;
        if (fixture is not null)
        {
            if (fixture == FixtureName)
            {
                sources = RecommendedSetupInputs.Fixture(DateTimeOffset.UtcNow);
                source = "fixture network (NOT real computers): this PC (a companion PC with an RTX 4080 that runs Thinking, Speaking and " +
                    "Listening on its own host service), gpu-box (a Linux host PC with an RTX 4090 and nothing installed), DIVA (a companion " +
                    "PC whose host service runs Deep thinking) and old-box (a host that hasn't reported its hardware)";
            }
            else if (fixture == OfflineFixtureName)
            {
                sources = RecommendedSetupInputs.OfflineFixture(DateTimeOffset.UtcNow);
                source = "fixture offline (NOT real computers): this PC (a companion PC without a graphics card) and two hosts, MIKU and " +
                    "IMOUTO, that haven't answered for 155 minutes; MIKU ran Thinking, Speaking and Lip-sync, IMOUTO ran Listening; no API key is saved";
            }
            else throw new ArgumentException($"fixture must be \"{FixtureName}\" or \"{OfflineFixtureName}\".");
        }
        else
        {
            ArgumentNullException.ThrowIfNull(dataDirectory);
            sources = await FromDataDirectoryAsync(dataDirectory, cancellation);
            source = "data directory";
        }
        var build = RecommendedSetupInputs.Request(sources);
        var recommendation = NetworkRecommender.Recommend(build.Request, FootprintCatalog.Default);
        var memory = fixture is null ? RecommendedSetupMemory.Load(dataDirectory) : new RecommendedSetupMemory();
        var (step, why) = SetupAskRule.Decide(recommendation, memory, companion: true, idle: TimeSpan.Zero);
        string Name(string? id) => id is null ? "" : build.Names.GetValueOrDefault(id) ?? id;
        return new
        {
            source,
            computers = sources.Computers.Select(c => new
            {
                id = c.Id, name = c.Name, kind = c.Kind.ToString(), thisPc = c.ThisPc, hasHostService = c.HasHostService, manageable = c.Manageable,
                online = c.Reachable != false, planned = build.Request.Machines.Any(m => m.Specs.Id == c.Id),
                hardware = build.Request.Machines.FirstOrDefault(m => m.Specs.Id == c.Id)?.Specs is { } specs
                    ? $"{string.Join(", ", specs.Gpus.Select(g => $"{g.Name} {g.VramGb:0.#} GB"))}{(specs.Gpus.Count == 0 ? "no graphics card" : "")}; " +
                      $"{specs.RamGb:0} GB memory; {specs.CpuThreads} threads"
                    : "not reported",
                roles = build.Request.Machines.FirstOrDefault(m => m.Specs.Id == c.Id)?.Roles.Select(r => r.Model is null ? r.Kind : $"{r.Kind}={r.Model}") ?? []
            }),
            notes = build.Notes,
            today = new
            {
                jobs = build.Request.CurrentJobs.Select(j => new { job = j.Job, host = j.HostId, off = j.Off, option = j.OptionId, pool = j.Pool }),
                thinkingPool = build.Request.CurrentThinkingPool,
                thinkingPoolOptOut = build.Request.ThinkingPoolOptOut,
                voiceEngine = build.Request.VoiceEngine,
                preference = build.Request.Preference.ToString()
            },
            recommendation = new
            {
                alreadyOptimal = recommendation.AlreadyOptimal, worthAsking = recommendation.WorthAsking, fingerprint = recommendation.Fingerprint,
                changes = recommendation.Changes.Select(c => new
                {
                    kind = c.Kind.ToString(), computer = Name(c.MachineId), summary = c.Summary, why = c.Why, away = c.Away, benefit = c.Benefit.ToString(),
                    roleKind = c.RoleKind, model = c.Model, job = c.Job, needsSomeoneThere = c.NeedsSomeoneThere, downloadGb = c.DownloadGb
                }),
                machines = recommendation.Target.Machines.Select(m => new
                {
                    computer = Name(m.MachineId), kind = m.Kind.ToString(), roles = m.Roles.Select(r => r.Model is null ? r.Kind : $"{r.Kind}={r.Model}"),
                    why = m.Why,
                    load = m.Usage is { } usage ? new
                    {
                        graphicsMemoryPercent = usage.Gpus.Count == 0 ? (double?)null : Math.Round(usage.Gpus.Sum(g => g.Vram.Used) / Math.Max(0.1, usage.Gpus.Sum(g => g.Vram.Capacity)) * 100),
                        memoryPercent = usage.Ram.Percent, processorPercent = usage.Cpu.Percent
                    } : null
                }),
                jobs = recommendation.Target.Jobs.Select(j => new { job = j.Job, host = j.HostId is null ? null : Name(j.HostId), off = j.Off, option = j.OptionId, pool = j.Pool.Select(Name), why = j.Why }),
                thinkingPool = recommendation.Target.ThinkingPool.Select(Name),
                cannotReply = recommendation.CannotReply, cannotReplyNote = recommendation.CannotReplyNote,
                cannotSpeak = recommendation.CannotSpeak, cannotSpeakNote = recommendation.CannotSpeakNote,
                offline = recommendation.Offline.Select(o => new { computer = Name(o.Id), minutes = Math.Round(o.For.TotalMinutes), note = o.Note }),
                notes = recommendation.Notes
            },
            companionInUseAsks = new { step = step.ToString(), why, declinedHere = memory.WasDeclined(recommendation.Fingerprint), declined = memory.Declined.Count }
        };
    }

    // ---------- a data directory ----------

    private static async Task<SetupSources> FromDataDirectoryAsync(string directory, CancellationToken cancellation)
    {
        var device = Martlet.Diagnostics.LocalLogs.ThisDeviceId();
        var hardware = new HostHardwareStore(directory).Load();
        var plan = Plan(directory);
        var hosts = PairedHosts(directory);
        var own = hosts.FirstOrDefault(h => h.Method == "ThisPcDocker").HostId;
        var presence = NodePresenceReport.Load(directory);
        TimeSpan? Away(string hostId) => presence?.Hosts.FirstOrDefault(h => h.HostId == hostId) is
            { State: not (NodePresenceState.Answering or NodePresenceState.Returning or NodePresenceState.Back) } away
            ? away.AwayFor ?? (away.Since is { } since ? presence.UpdatedAt - since : TimeSpan.Zero)
            : null;
        var computers = new List<SetupComputer>
        {
            new(own ?? device, "This PC", NetworkMachineKind.Companion)
            {
                Hardware = own is null ? null : hardware.FirstOrDefault(h => h.HostId == own), HasHostService = own is not null, ThisPc = true
            }
        };
        foreach (var host in hosts.Where(h => h.HostId != own))
        {
            var report = hardware.FirstOrDefault(h => h.HostId == host.HostId);
            var reachable = host.Method is "ThisPcDocker" or "Agent" || host.Method is "SshDocker" or "SshNative" && host.SshTarget is { Length: > 0 };
            var away = Away(host.HostId);
            computers.Add(new(host.HostId, presence?.Hosts.FirstOrDefault(h => h.HostId == host.HostId)?.Name ?? host.HostId, NetworkMachineKind.Host)
            {
                Hardware = report, HasHostService = true, Reachable = away is null ? null : false, OfflineFor = away,
                Manageable = reachable && PlatformCatalog.ManagesRolesRemotely(PlatformDevice.FromHost(host.HostId, report))
            });
        }
        var loaded = await new SettingsStore(directory).LoadAsync(cancellation);
        var routes = loaded.Settings?.Setup?.Routes ?? [];
        var jobs = new List<JobPlan>();
        foreach (var (job, role) in new[] { (ClusterJobs.Thinking, SetupRole.Llm), (ClusterJobs.Listening, SetupRole.Stt), (ClusterJobs.Speaking, SetupRole.Tts) })
            if (routes.FirstOrDefault(r => r.Role == role) is { } route)
                jobs.Add(new JobPlan(job, route.Gateway is { } gateway && SelfHostSetup.IsGateway(route.RouteType) ? gateway.HostId : null));
        var thinking = routes.FirstOrDefault(r => r.Role == SetupRole.Llm);
        var providers = thinking?.CredentialId is null ? []
            : thinking.RouteType == SetupRouteType.OpenAi ? ["openai"]
            : ChatCompletionsEndpointCatalog.Named(thinking.Origin)?.Id is { } id ? new[] { id } : [];
        var speaking = routes.FirstOrDefault(r => r.Role == SetupRole.Tts);
        string? chosen = null;
        try { chosen = File.ReadAllText(Path.Combine(directory, "speaking-engine.txt")).Trim(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        var voice = SpeechEngines.ForRoute(speaking?.GatewaySnapshot?.RouteId) ?? SpeechEngines.ForKey(chosen) ?? SpeechEngines.Default;
        var poolSettings = ThinkingPoolSettings.Read(directory, save: false).Settings;
        var pool = poolSettings.Places.Places.Where(p => p.OnHostRole && p.HostId is not null).Select(p => p.HostId!).ToArray();
        return new SetupSources(computers)
        {
            Plan = plan, LocalJobs = jobs, Sharing = WorkSharingSettings.Load(directory), Device = device, ThinkingPool = pool,
            PoolOptOut = poolSettings.LeftByOwner, VoiceEngine = voice.HostRoleKind, ConfiguredProviders = providers
        };
    }

    private static ClusterPlan? Plan(string directory)
    {
        try
        {
            var path = Path.Combine(directory, "cluster.json");
            return File.Exists(path) ? ClusterPlan.Parse(File.ReadAllBytes(path)) : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Martlet.Core.Contracts.ContractException) { return null; }
    }

    // The paired hosts in hosts.json: host ID, how Martlet reaches it and the SSH target (never a secret). A host a friend shares
    // with this PC isn't one of your computers, so the recommended setup never plans on it (as the desktop's NetworkMap.Hosts).
    private static IReadOnlyList<(string HostId, string? Method, string? SshTarget)> PairedHosts(string directory)
    {
        try
        {
            var path = Path.Combine(directory, "hosts.json");
            if (!File.Exists(path) || new FileInfo(path).Length > 65_536) return [];
            return [.. (JsonNode.Parse(File.ReadAllText(path))?["hosts"] as JsonArray ?? []).OfType<JsonObject>()
                .Where(h => h["access"]?.GetValue<string>() != "friend")
                .Select(h => (HostId: h["pairing"]?["hostId"]?.GetValue<string>(), Method: h["method"]?.GetValue<string>() ?? "Agent",
                    SshTarget: h["sshTarget"]?.GetValue<string>()))
                .Where(h => h.HostId is { Length: > 0 }).Select(h => (h.HostId!, (string?)h.Method, h.SshTarget))];
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException)
        {
            return [];
        }
    }
}
