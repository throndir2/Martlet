using Martlet.Core.Cluster;
using Martlet.Core.Installation;
using Martlet.Core.Planning;
using Martlet.Core.Reading;
using Martlet.Core.Settings;
#if !MARTLET_MCP
using Martlet.Core.Platforms;
#endif

// Also built into Martlet's MCP server (recommended_setup_status), in its own namespace, without the desktop adapter.
#if MARTLET_MCP
namespace Martlet.Mcp.Shared;
#else
namespace Martlet.Desktop;
#endif

/// <summary>One of the owner's computers as Home's Recommended setup sees it, before it becomes a
/// <see cref="NetworkMachine"/>. <see cref="Id"/> is the id the cluster plan uses for it: its host service's host id, else the
/// Martlet device id. <see cref="Specs"/> is this PC's own hardware, read here (it wins over <see cref="Hardware"/>, the
/// report a host service gave). <see cref="Offers"/> is what its last check found running (role kind to model); null when it
/// was not checked, and then the shared plan's record of its roles is used.</summary>
internal sealed record SetupComputer(string Id, string Name, NetworkMachineKind Kind)
{
    public MachineSpecs? Specs { get; init; }
    public HostHardware? Hardware { get; init; }
    public bool HasHostService { get; init; }
    public bool Manageable { get; init; } = true;
    /// <summary>Whether it answered its last check; null: not checked yet (planned as online).</summary>
    public bool? Reachable { get; init; }
    public TimeSpan? OfflineFor { get; init; }
    public IReadOnlyDictionary<string, string>? Offers { get; init; }
    /// <summary>The downloads its host service keeps (its machine report); null: <see cref="Hardware"/>'s, if any.</summary>
    public IReadOnlyList<HostDownload>? Downloads { get; init; }
    public bool ThisPc { get; init; }
}

/// <summary>Everything Home's Recommended setup plans from, read by the desktop (<c>RecommendedSetupInputs.Sources</c>) or by
/// the MCP server from a data directory. <see cref="LocalJobs"/> is what this PC does for each job when the shared plan has no
/// entry for it (sync off, or nobody chose yet). <see cref="JobOptions"/> maps a job to the FootprintCatalog option doing it
/// today, when known. <see cref="ThinkingPool"/> is the Thinking pool's members (host ids) and <see cref="PoolOptOut"/> the
/// hosts the owner took out of it.</summary>
internal sealed record SetupSources(IReadOnlyList<SetupComputer> Computers)
{
    public ClusterPlan? Plan { get; init; }
    public IReadOnlyList<JobPlan> LocalJobs { get; init; } = [];
    public IReadOnlyDictionary<string, string> JobOptions { get; init; } = new Dictionary<string, string>();
    public WorkSharingSettings Sharing { get; init; } = new();
    /// <summary>The shared pool lists (pools.json): Speaking's and Listening's members, once they have lists.</summary>
    public PoolSettings Pools { get; init; } = new();
    /// <summary>This PC's Martlet device id (the order work-sharing tries computers in depends on the companion PC asking).</summary>
    public string Device { get; init; } = "";
    public IReadOnlyCollection<string> ThinkingPool { get; init; } = [];
    public IReadOnlyCollection<string> PoolOptOut { get; init; } = [];
    /// <summary>The voice engine the owner chose, as a host role kind ("chatterbox").</summary>
    public string? VoiceEngine { get; init; }
    public IReadOnlyCollection<string> ConfiguredProviders { get; init; } = [];
    /// <summary>The parts the owner turned off in the review (RecommendedSetupMemory.Off).</summary>
    public IReadOnlyCollection<PlanComponent> Off { get; init; } = [];
    /// <summary>The chat models the owner's model apps on this PC serve (empty when "Use models your apps already run" is off);
    /// the request puts them on this PC.</summary>
    public IReadOnlyList<ServedModel> ServedModels { get; init; } = [];
    /// <summary>Prefer models your hosts already have (RecommendedSetupMemory.PreferHostModels).</summary>
    public bool PreferHostModels { get; init; }
    /// <summary>This PC's choices for the parts it sets on their Companion pages (<see cref="RecommendedSetupInputs.Choices"/>).</summary>
    public IReadOnlyList<PartChoice> Choices { get; init; } = [];
}

/// <summary>The recommender's request and what the review says about computers the request leaves out.
/// <see cref="Names"/> maps every computer's id (planned or not) to its name.</summary>
internal sealed record SetupRequestBuild(NetworkSetupRequest Request, IReadOnlyList<string> Notes, IReadOnlyDictionary<string, string> Names);

/// <summary>Turns the owner's computers, the shared plan, work-sharing and the Thinking pool into the network recommender's
/// request (<see cref="NetworkSetupRequest"/>). Pure: it reads nothing and contacts nothing.</summary>
internal static class RecommendedSetupInputs
{
    /// <summary>The host role that does <paramref name="job"/> (a ClusterJobs name); the voice engine the owner chose speaks.</summary>
    internal static string? JobRoleKind(string job, string? voiceEngine) => job switch
    {
        ClusterJobs.Thinking => "ollama",
        ClusterJobs.Listening => "stt",
        ClusterJobs.Speaking => voiceEngine ?? SpeechEngines.Default.HostRoleKind,
        ClusterJobs.LipSync => "audio2face",
        _ => null
    };

    /// <summary>The owner's stance on hosted providers: one who saved a provider key uses them (Balanced); one who didn't keeps
    /// everything on their computers (PreferLocal).</summary>
    internal static HostingPreference PreferenceFor(IReadOnlyCollection<string> configuredProviders) =>
        configuredProviders.Count > 0 ? HostingPreference.Balanced : HostingPreference.PreferLocal;

    internal static SetupRequestBuild Request(SetupSources sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        var notes = new List<string>();
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        var machines = new List<NetworkMachine>();
        foreach (var computer in sources.Computers.DistinctBy(c => c.Id))
        {
            names[computer.Id] = computer.Name;
            var specs = computer.Specs ?? (computer.Hardware is { } report ? MachineSpecs.FromHostHardware(report, computer.Name) : null);
            if (specs is null)
            {
                // Planning a computer with no known hardware would empty it; leave it as it is.
                if (computer.HasHostService)
                    notes.Add($"{computer.Name} hasn't reported its hardware yet, so the recommendation leaves it as it is.");
                else if (computer.Kind == NetworkMachineKind.Companion)
                    notes.Add($"{computer.Name} runs only the parts inside Martlet (no host service), so nothing changes there.");
                continue;
            }
            // This PC is always part of the network: the owner is at it. A computer that isn't answering is not: the
            // recommender plans without it and its jobs move.
            var online = computer.ThisPc || computer.Reachable != false;
            if (computer.ThisPc && computer.HasHostService && computer.Reachable == false)
                notes.Add("This PC's host service isn't answering, so changes to it wait until it runs again.");
            machines.Add(new NetworkMachine(specs with
            {
                Id = computer.Id, Name = computer.Name, IsPrimary = computer.Kind == NetworkMachineKind.Companion
            }, computer.Kind)
            {
                Online = online,
                OfflineFor = online ? null : computer.OfflineFor,
                HasHostService = computer.HasHostService,
                Manageable = computer.Manageable,
                Roles = computer.HasHostService ? Roles(computer, sources.Plan) : [],
                Downloaded = computer.HasHostService ? Downloaded(computer) : [],
                OnWindows = computer.HasHostService ? SharedGpu.OnWindows(computer.ThisPc, computer.Hardware) : null
            });
        }

        // A job with no entry is one nobody set up: the recommender may plan it.
        var jobs = ClusterJobs.All.Select(job => Job(job, sources)).OfType<JobPlan>().ToArray();
        var thisPc = sources.Computers.FirstOrDefault(c => c.ThisPc)?.Id ?? "";
        var request = new NetworkSetupRequest(machines)
        {
            Preference = PreferenceFor(sources.ConfiguredProviders),
            ConfiguredProviders = [.. sources.ConfiguredProviders],
            CurrentJobs = jobs,
            CurrentThinkingPool = [.. sources.ThinkingPool.Where(id => !sources.PoolOptOut.Contains(id, StringComparer.Ordinal)).Distinct(StringComparer.Ordinal)],
            ThinkingPoolOptOut = [.. sources.PoolOptOut.Distinct(StringComparer.Ordinal)],
            VoiceEngine = sources.VoiceEngine,
            Off = [.. sources.Off.Where(c => ComponentRanking.CanBeOff(c) && !ComponentRanking.SetOnPage(c)).Distinct()],
            Choices = [.. sources.Choices.Where(c => c is not null && ComponentRanking.SetOnPage(c.Component)).DistinctBy(c => c.Component)],
            CompanionPcs = sources.Computers.DistinctBy(c => c.Id).Count(c => c.Kind == NetworkMachineKind.Companion),
            ServedModels = [.. sources.ServedModels.Select(m => m with { MachineId = thisPc })],
            PreferHostModels = sources.PreferHostModels
        };
        return new(request, notes, names);
    }

    /// <summary>This PC's choices for the parts it sets on their Companion pages (<see cref="ComponentRanking.SetOnPage"/>), for
    /// their lines in the review. Vision: <paramref name="watch"/> (vision is on) and the image model in
    /// <paramref name="senses"/>. Hearing: <paramref name="hearVoice"/> (Let ... hear my voice; null: never chosen, which counts as
    /// on) and the audio model. Reading: <paramref name="reading"/>. Smart home: <paramref name="homeAddress"/>, the Home
    /// Assistant this PC connects to (empty: none). <paramref name="readingModel"/>: the model the Reading role runs on that
    /// computer, when known (null: RapidOCR, the first Reading role). Pure.</summary>
    internal static IReadOnlyList<PartChoice> Choices(bool watch, bool? hearVoice, SenseModels senses, ReadingSettings reading, string? homeAddress,
        string? readingModel = null)
    {
        ArgumentNullException.ThrowIfNull(senses);
        ArgumentNullException.ThrowIfNull(reading);
        var home = homeAddress?.Trim() ?? "";
        return
        [
            Sense(PlanComponent.Vision, watch, senses.Place(SenseKind.Image)),
            new PartChoice(PlanComponent.Reading, reading.On)
            {
                OptionId = reading.Place == ReadingPlace.Host
                    ? FootprintCatalog.Default.For(PlanComponent.Reading).FirstOrDefault(o => o.HostRoleKind == "ocr" && o.ModelId == readingModel)?.Id
                        ?? "reading:rapidocr"
                    : "reading:windows-ocr",
                HostId = reading.Place == ReadingPlace.Host ? reading.HostId : null
            },
            Sense(PlanComponent.Hearing, hearVoice != false, senses.Place(SenseKind.Audio)),
            new PartChoice(PlanComponent.SmartHome, home.Length > 0)
            {
                Where = home.Length == 0 ? null : $"Your own Home Assistant at {(Uri.TryCreate(home, UriKind.Absolute, out var uri) ? uri.Host : home)}"
            }
        ];
    }

    /// <summary>The image or audio model as a part choice: Thinking's own model (<paramref name="own"/> null), a model in Ollama
    /// on this PC or a paired computer, a catalog provider online, or another server in words.</summary>
    private static PartChoice Sense(PlanComponent part, bool on, DeepThinkingSettings? own)
    {
        var prefix = part == PlanComponent.Vision ? "vision:" : "hearing:";
        var catalog = FootprintCatalog.Default;
        if (own is null) return new(part, on) { OptionId = prefix + "thinking" };
        var model = string.IsNullOrWhiteSpace(own.ModelId) ? null : own.ModelId.Trim();
        var known = model is null ? null : catalog.Find(prefix + model)?.Id;
        if (own.Place == DeepThinkingPlace.Host) return new(part, on) { OptionId = known, Model = model ?? "Its model", HostId = own.HostId };
        if (ContextBudget.IsLocalOllama(SetupRouteType.ChatCompletions, own.Origin)) return new(part, on) { OptionId = known, Model = model ?? "Its model" };
        var host = Uri.TryCreate(own.Origin, UriKind.Absolute, out var uri) ? uri.Host : own.Origin ?? "";
        var provider = host.Contains("nvidia", StringComparison.OrdinalIgnoreCase) ? "nvidia-build"
            : host.Contains("openai.com", StringComparison.OrdinalIgnoreCase) ? "openai" : null;
        if (provider is not null && catalog.For(part).FirstOrDefault(o => !o.IsLocal && o.ProviderId == provider) is { } hosted)
            return new(part, on) { OptionId = hosted.Id, Model = model };
        return new(part, on)
        {
            Model = model,
            Where = uri is { IsLoopback: true } ? $"{model ?? "A model"} on a model server on this PC" : $"{model ?? "A model"}, online ({host})"
        };
    }

    /// <summary>What a host service runs: its last check's roles, else the shared plan's record of them.</summary>
    private static IReadOnlyList<HostedRolePlacement> Roles(SetupComputer computer, ClusterPlan? plan)
    {
        IEnumerable<(string Kind, string? Model)> roles = computer.Offers is { } offers
            ? offers.Select(o => (o.Key, (string?)o.Value))
            : plan?.Node(computer.Id) is { Removed: false } node ? node.Roles.Select(r => (r.Kind, (string?)r.Model)) : [];
        return [.. roles.OrderBy(r => r.Kind, StringComparer.Ordinal)
            .Select(r => new HostedRolePlacement(r.Kind, string.IsNullOrWhiteSpace(r.Model) ? null : r.Model, null))];
    }

    /// <summary>The role models a host service keeps downloaded, so turning a role back on there downloads nothing.</summary>
    private static IReadOnlyList<HostedRolePlacement> Downloaded(SetupComputer computer) =>
        [.. (computer.Downloads ?? computer.Hardware?.Downloads ?? [])
            .Where(d => !string.IsNullOrWhiteSpace(d.Role) && !string.IsNullOrWhiteSpace(d.Model))
            .Select(d => new HostedRolePlacement(d.Role, d.Model, null)).Distinct()];

    /// <summary>Who does <paramref name="job"/> today (the shared plan, else this PC's own choice; null when nobody set it up) and,
    /// for a job a host does, the other computers that take its requests when that host is busy, in the order this PC tries them
    /// (Devices › Sharing work).</summary>
    private static JobPlan? Job(string job, SetupSources sources)
    {
        // The plan's entry when it names a host or nobody; "each computer's own choice" is this PC's own choice.
        var current = sources.Plan?.For(job) is { } assignment && (assignment.HostId is not null || assignment.Off)
            ? new JobPlan(job, assignment.HostId, assignment.Off)
            : sources.LocalJobs.FirstOrDefault(j => j.Job == job);
        if (current is null) return null;
        current = current with { OptionId = sources.JobOptions.GetValueOrDefault(job) ?? current.OptionId };
        if (current.HostId is not { } host || !WorkSharingJobs.All.Contains(job) || JobRoleKind(job, sources.VoiceEngine) is not { } kind)
            return current;
        var runs = sources.Computers.Where(c => c.HasHostService && Roles(c, sources.Plan).Any(r => r.Kind == kind))
            .Select(c => new WorkPlace(c.Id, c.ThisPc,
                sources.Plan?.Assignments.Count(a => ClusterJobs.All.Contains(a.Job) && a.HostId == c.Id) ?? 0))
            .ToArray();
        // A job with a pool list: its members that run the engine, in the owner's order (this PC is its own host service).
        if (PoolAreas.Find(job) is { } area && sources.Pools.Has(area.Id))
        {
            var own = runs.FirstOrDefault(r => r.Own)?.HostId;
            return current with
            {
                Pool = [.. PoolRouting.Order(area, sources.Pools.Pool(area.Id), sources.Device).Members
                    .Select(m => m.Kind == PoolMemberKind.ThisPc ? own : m.OnHost ? m.HostId : null)
                    .Where(id => id is not null && id != host && runs.Any(r => r.HostId == id)).Select(id => id!).Distinct(StringComparer.Ordinal)]
            };
        }
        var order = WorkSharing.Order(sources.Sharing, job, sources.Device, host, runs);
        return current with { Pool = [.. order.Where(id => id != host)] };
    }

    /// <summary>FIXTURE, NOT real computers (recommended_setup_status's fixture "network", and the desktop's
    /// MARTLET_SIMULATE_RECOMMENDED_SETUP): this PC, a companion PC that does all three jobs on its own host service (too much
    /// for a PC that runs games); gpu-box, a strong Linux host PC with nothing installed; DIVA, another companion PC whose host
    /// service runs Deep thinking; and old-box, a host that never reported its hardware.</summary>
    internal static SetupSources Fixture(DateTimeOffset now)
    {
        HostHardware Report(string id, string gpu, int vramGb, double ramGb, int threads, string platform, string kernel) =>
            new(id, $"https://{id}.lan:8443", now, now, "docker", platform == "windows" ? "Windows 11" : "Ubuntu 24.04", kernel,
                "Fixture processor", threads, ramGb, "docker", "yes", [new HostGpu(gpu, "nvidia", vramGb * 1024, "570")]) { Platform = "linux" };
        var plan = ClusterPlan.Empty
            .Assign(ClusterJobs.Thinking, "desk-host", false, true, null, "fixture-desk", now)
            .Assign(ClusterJobs.Speaking, "desk-host", false, true, null, "fixture-desk", now)
            .Assign(ClusterJobs.Listening, "desk-host", false, true, null, "fixture-desk", now)
            .Assign(ClusterJobs.LipSync, null, true, false, null, "fixture-desk", now);
        return new SetupSources(
        [
            new("desk-host", "This PC", NetworkMachineKind.Companion)
            {
                Specs = MachineSpecs.ThisPc([new MachineGpu("NVIDIA GeForce RTX 4080", GpuVendor.Nvidia, 16)], 32, 24, diskFreeGb: 400),
                HasHostService = true, Reachable = true, ThisPc = true,
                Offers = new Dictionary<string, string> { ["ollama"] = "gemma4:12b", ["chatterbox"] = "chatterbox-turbo", ["stt"] = "whisper-large-v3-turbo" }
            },
            new("gpu-box", "gpu-box", NetworkMachineKind.Host)
            {
                Hardware = Report("gpu-box", "NVIDIA GeForce RTX 4090", 24, 64, 32, "linux", "6.8.0"), HasHostService = true, Reachable = true,
                Offers = new Dictionary<string, string>()
            },
            new("diva-host", "DIVA", NetworkMachineKind.Companion)
            {
                Hardware = Report("diva-host", "NVIDIA GeForce RTX 3080", 10, 32, 16, "windows", "5.15.167.4-microsoft-standard-WSL2"),
                HasHostService = true, Reachable = true, Offers = new Dictionary<string, string> { ["deep-thinking"] = "gemma4:e4b" }
            },
            new("old-box", "old-box", NetworkMachineKind.Host) { HasHostService = true, Reachable = true }
        ])
        {
            Plan = plan, Device = "fixture-desk", ThinkingPool = ["diva-host"], VoiceEngine = SpeechEngines.Chatterbox.HostRoleKind
        };
    }

    /// <summary>FIXTURE, NOT real computers (recommended_setup_status's fixture "offline", and the desktop's
    /// MARTLET_SIMULATE_RECOMMENDED_SETUP_NETWORK=offline): this PC, a small companion PC with no graphics card whose host
    /// service only listens; MIKU, the host that thinks, speaks and moves the face; and IMOUTO, the host that listens. Both hosts
    /// haven't answered for 155 minutes, so their jobs move and no computer that answers has room for a Thinking model.</summary>
    internal static SetupSources OfflineFixture(DateTimeOffset now)
    {
        HostHardware Report(string id, string gpu, int vramGb) =>
            new(id, $"https://{id}.lan:8443", now.AddMinutes(-155), now.AddMinutes(-155), "docker", "Windows 11",
                "5.15.167.4-microsoft-standard-WSL2", "Fixture processor", 16, 32, "docker", "yes", [new HostGpu(gpu, "nvidia", vramGb * 1024, "570")])
            { Platform = "windows" };
        var plan = ClusterPlan.Empty
            .Assign(ClusterJobs.Thinking, "miku-host", false, true, null, "fixture-desk", now)
            .Assign(ClusterJobs.Speaking, "miku-host", false, true, null, "fixture-desk", now)
            .Assign(ClusterJobs.Listening, "imouto-host", false, true, null, "fixture-desk", now)
            .Assign(ClusterJobs.LipSync, "miku-host", false, true, null, "fixture-desk", now);
        var away = TimeSpan.FromMinutes(155);
        return new SetupSources(
        [
            new("desk-host", "This PC", NetworkMachineKind.Companion)
            {
                Specs = MachineSpecs.ThisPc([], 4, 4, diskFreeGb: 100),
                HasHostService = true, Reachable = true, ThisPc = true,
                Offers = new Dictionary<string, string> { ["stt"] = "whisper-large-v3-turbo" }
            },
            new("miku-host", "MIKU", NetworkMachineKind.Host)
            {
                Hardware = Report("miku-host", "NVIDIA GeForce RTX 4090", 24), HasHostService = true, Reachable = false, OfflineFor = away,
                Offers = new Dictionary<string, string> { ["ollama"] = "gemma4:12b", ["chatterbox"] = "chatterbox-turbo", ["audio2face"] = "" }
            },
            new("imouto-host", "IMOUTO", NetworkMachineKind.Host)
            {
                Hardware = Report("imouto-host", "NVIDIA GeForce RTX 3060", 12), HasHostService = true, Reachable = false, OfflineFor = away,
                Offers = new Dictionary<string, string> { ["stt"] = "whisper-large-v3-turbo" }
            }
        ])
        {
            Plan = plan, Device = "fixture-desk", VoiceEngine = SpeechEngines.Chatterbox.HostRoleKind
        };
    }

    /// <summary>FIXTURE, NOT real computers (recommended_setup_status's fixture "served"): this PC alone, a companion PC with an
    /// RTX 5090 (32 GB) and nothing installed, whose Thinking is Gemma 4 E2B in Martlet's Ollama. LM Studio on it serves
    /// qwen3-32b and an embedding model, and Ollama serves llama3.3:70b, which is too big for the card.</summary>
    internal static SetupSources ServedFixture(DateTimeOffset now) => new(
    [
        new("desk-host", "This PC", NetworkMachineKind.Companion)
        {
            Specs = MachineSpecs.ThisPc([new MachineGpu("NVIDIA GeForce RTX 5090", GpuVendor.Nvidia, 32)], 64, 32, diskFreeGb: 800),
            HasHostService = true, Reachable = true, ThisPc = true, Offers = new Dictionary<string, string>()
        }
    ])
    {
        LocalJobs = [new JobPlan(ClusterJobs.Thinking, null)],
        JobOptions = new Dictionary<string, string> { [ClusterJobs.Thinking] = "gemma4:e2b" },
        Device = "fixture-desk", VoiceEngine = SpeechEngines.Chatterbox.HostRoleKind,
        ServedModels =
        [
            new("", "lmstudio", "LM Studio", "http://127.0.0.1:1234/v1", "qwen3-32b") { SizeGb = 19.8 },
            new("", "lmstudio", "LM Studio", "http://127.0.0.1:1234/v1", "text-embedding-nomic-embed-text-v1.5") { SizeGb = 0.1 },
            new("", "ollama", "Ollama", "http://127.0.0.1:11434/v1", "llama3.3:70b") { SizeGb = 42.5 }
        ]
    };

    /// <summary>FIXTURE, NOT real computers (recommended_setup_status's fixture "hostmodels", and the desktop's
    /// MARTLET_SIMULATE_RECOMMENDED_SETUP_NETWORK=hostmodels): this PC, a companion PC without a graphics card, and gpu-box, a
    /// Linux host with an RTX 4090 (24 GB) that thinks with Gemma 4 E4B and keeps qwen2.5:14b downloaded from before.</summary>
    internal static SetupSources HostModelsFixture(DateTimeOffset now)
    {
        var report = new HostHardware("gpu-box", "https://gpu-box.lan:8443", now, now, "docker", "Ubuntu 24.04", "6.8.0", "Fixture processor",
            32, 64, "docker", "yes", [new HostGpu("NVIDIA GeForce RTX 4090", "nvidia", 24 * 1024, "570")]) { Platform = "linux" };
        var plan = ClusterPlan.Empty.Assign(ClusterJobs.Thinking, "gpu-box", false, true, null, "fixture-desk", now);
        return new SetupSources(
        [
            new("desk", "This PC", NetworkMachineKind.Companion) { Specs = MachineSpecs.ThisPc([], 16, 8, diskFreeGb: 200), ThisPc = true },
            new("gpu-box", "gpu-box", NetworkMachineKind.Host)
            {
                Hardware = report, HasHostService = true, Reachable = true,
                Offers = new Dictionary<string, string> { ["ollama"] = "gemma4:e4b" },
                Downloads = [new HostDownload("ollama", "gemma4:e4b"), new HostDownload("ollama", "qwen2.5:14b")]
            }
        ])
        {
            Plan = plan, Device = "fixture-desk", VoiceEngine = SpeechEngines.Chatterbox.HostRoleKind, PreferHostModels = true
        };
    }

    /// <summary>The chat models the model apps found on this PC serve (<see cref="Martlet.Providers.LocalModelServers.DetectAsync"/>),
    /// as the recommender takes them; an app that asks for a key first lists none. The request puts them on this PC.</summary>
    internal static IReadOnlyList<ServedModel> Served(IEnumerable<Martlet.Providers.LocalModelServer>? servers) =>
        [.. (servers ?? []).Where(s => !s.NeedsKey).SelectMany(s => s.Models.Select(model =>
            new ServedModel("", s.Id, s.Name, s.ChatCompletionsBaseUrl, model)
            {
                SizeGb = s.SizesGb.TryGetValue(model, out var gb) && gb > 0 ? gb : null
            }))];

#if !MARTLET_MCP
    /// <summary>The desktop's view of the owner's computers as recommender sources: this PC (its own host service's id when it
    /// runs one, else its device id), every paired host (a companion PC when your Martlet network says that computer is one)
    /// and the member companion PCs without a host service. A host is manageable as on the Devices map: this PC's own host
    /// service, or a host whose roles Martlet changes from here (its platform allows it and Martlet can reach it: through
    /// Martlet there or over SSH). A host the presence record (<paramref name="offlineFor"/>) says isn't answering counts as
    /// offline even when its last connection check is older. <paramref name="nodes"/> (the Devices map) names the catalog
    /// option doing each job today.</summary>
    internal static SetupSources Sources(NetworkInputs inputs, IReadOnlyList<NetworkNode>? nodes, string device, string? ownHostId,
        double? diskFreeGb, Func<string, TimeSpan?>? offlineFor = null, WorkSharingSettings? sharing = null,
        IReadOnlyCollection<string>? thinkingPool = null, IReadOnlyCollection<string>? poolOptOut = null, string? voiceEngine = null,
        IReadOnlyCollection<string>? configuredProviders = null, IReadOnlyCollection<PlanComponent>? off = null,
        IReadOnlyList<PartChoice>? choices = null, PoolSettings? pools = null)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        var computers = new List<SetupComputer>();
        var members = (inputs.Computers ?? []).Where(c => c.Standing == ComputerStanding.Member).ToList();
        var thisId = ownHostId ?? device;
        var ownCheck = ownHostId is null ? null : inputs.HostChecks.GetValueOrDefault(ownHostId);
        computers.Add(new SetupComputer(thisId, "This PC", inputs.Role == DeviceRole.Host ? NetworkMachineKind.Host : NetworkMachineKind.Companion)
        {
            Specs = DeviceCapacityInputs.ThisPc(inputs.Machine, diskFreeGb),
            HasHostService = ownHostId is not null,
            Manageable = true,
            Reachable = ownCheck?.Reachable,
            OfflineFor = ownHostId is null ? null : offlineFor?.Invoke(ownHostId),
            Offers = ownCheck?.Offers,
            Downloads = ownHostId is null ? null : inputs.HostHardware?.FirstOrDefault(h => h.HostId == ownHostId)?.Downloads,
            ThisPc = true
        });
        foreach (var host in NetworkMap.Hosts(inputs).Where(h => h.HostId != ownHostId))
        {
            var id = host.HostId;
            var member = members.FirstOrDefault(c => (c.HostId ?? HostSetupCommands.SuggestedHostId(c.Name)) == id);
            var hardware = inputs.HostHardware?.FirstOrDefault(h => h.HostId == id);
            var check = inputs.HostChecks.GetValueOrDefault(id);
            var away = offlineFor?.Invoke(id);
            computers.Add(new SetupComputer(id, member?.Name ?? id,
                member?.Role == DeviceRole.Companion ? NetworkMachineKind.Companion : NetworkMachineKind.Host)
            {
                Hardware = hardware,
                HasHostService = true,
                Manageable = host.CanLaunch && PlatformCatalog.ManagesRolesRemotely(PlatformDevice.FromHost(id, hardware)),
                Reachable = away is not null ? false : check?.Reachable,
                OfflineFor = away,
                Offers = check?.Offers
            });
        }
        foreach (var member in members.Where(m => m.Role != DeviceRole.Host && computers.All(c => c.Name != m.Name && c.Id != m.DeviceId)))
            computers.Add(new SetupComputer(member.DeviceId, member.Name, NetworkMachineKind.Companion));

        // What this PC does for each job it set up (lip-sync always has a handler: this PC, a host or nobody). A job on a host a
        // friend shares with this PC is this PC's own choice, not one of your computers: the setup is planned without it.
        var routes = inputs.Settings?.Setup?.Routes ?? [];
        var shared = (inputs.SharedHosts ?? []).Select(h => h.HostId).ToHashSet(StringComparer.Ordinal);
        var jobs = ClusterJobs.All.Select(job =>
        {
            var local = ClusterSync.Local(job, inputs.Settings, inputs.Avatar, ownHostId, shared);
            if (local.Shared) local = new LocalJob(null, false);
            var role = job switch { ClusterJobs.Thinking => SetupRole.Llm, ClusterJobs.Listening => SetupRole.Stt, _ => SetupRole.Tts };
            return job != ClusterJobs.LipSync && local.HostId is null && routes.All(r => r.Role != role) ? null
                : new JobPlan(job, local.HostId, local.Off);
        }).OfType<JobPlan>().ToArray();
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        if (nodes is not null)
            foreach (var assignment in DeviceCapacityInputs.Current(inputs, nodes, FootprintCatalog.Default))
                if (JobOf(assignment.Component) is { } job && !options.ContainsKey(job))
                    options[job] = assignment.OptionId;
        var providers = configuredProviders ?? [];
        return new SetupSources(computers)
        {
            Plan = inputs.Plan, LocalJobs = jobs, JobOptions = options, Sharing = sharing ?? new(), Pools = pools ?? new(), Device = device,
            ThinkingPool = thinkingPool ?? [], PoolOptOut = poolOptOut ?? [], VoiceEngine = voiceEngine,
            ConfiguredProviders = providers, Off = off ?? [], Choices = choices ?? []
        };
    }

    private static string? JobOf(PlanComponent component) => component switch
    {
        PlanComponent.Thinking => ClusterJobs.Thinking,
        PlanComponent.Listening => ClusterJobs.Listening,
        PlanComponent.Voice => ClusterJobs.Speaking,
        PlanComponent.LipSync => ClusterJobs.LipSync,
        _ => null
    };
#endif
}
