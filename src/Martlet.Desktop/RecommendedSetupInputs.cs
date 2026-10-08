using Martlet.Core.Cluster;
using Martlet.Core.Installation;
using Martlet.Core.Planning;
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
    /// <summary>This PC's Martlet device id (the order work-sharing tries computers in depends on the companion PC asking).</summary>
    public string Device { get; init; } = "";
    public IReadOnlyCollection<string> ThinkingPool { get; init; } = [];
    public IReadOnlyCollection<string> PoolOptOut { get; init; } = [];
    /// <summary>The voice engine the owner chose, as a host role kind ("chatterbox").</summary>
    public string? VoiceEngine { get; init; }
    public IReadOnlyCollection<string> ConfiguredProviders { get; init; } = [];
    public TimeSpan OfflineGrace { get; init; } = TimeSpan.FromMinutes(10);
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
            var online = computer.Reachable != false;
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
                OnWindows = computer.HasHostService ? SharedGpu.OnWindows(computer.ThisPc, computer.Hardware) : null
            });
        }

        // A job with no entry is one nobody set up: the recommender may plan it.
        var jobs = ClusterJobs.All.Select(job => Job(job, sources)).OfType<JobPlan>().ToArray();
        var request = new NetworkSetupRequest(machines)
        {
            Preference = PreferenceFor(sources.ConfiguredProviders),
            ConfiguredProviders = [.. sources.ConfiguredProviders],
            CurrentJobs = jobs,
            CurrentThinkingPool = [.. sources.ThinkingPool.Where(id => !sources.PoolOptOut.Contains(id, StringComparer.Ordinal)).Distinct(StringComparer.Ordinal)],
            ThinkingPoolOptOut = [.. sources.PoolOptOut.Distinct(StringComparer.Ordinal)],
            VoiceEngine = sources.VoiceEngine,
            OfflineGrace = sources.OfflineGrace
        };
        return new(request, notes, names);
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
        var order = WorkSharing.Order(sources.Sharing, job, sources.Device, host, runs);
        return current with { Pool = [.. order.Where(id => id != host)] };
    }

#if !MARTLET_MCP
    /// <summary>The desktop's view of the owner's computers as recommender sources: this PC (its own host service's id when it
    /// runs one, else its device id), every paired host (a companion PC when your Martlet network says that computer is one)
    /// and the member companion PCs without a host service. A host is manageable as on the Devices map: this PC's own host
    /// service, or a host whose roles Martlet changes from here (its platform allows it and Martlet can reach it: through
    /// Martlet there or over SSH). <paramref name="nodes"/> (the Devices map) names the catalog option doing each job today.</summary>
    internal static SetupSources Sources(NetworkInputs inputs, IReadOnlyList<NetworkNode>? nodes, string device, string? ownHostId,
        double? diskFreeGb, Func<string, TimeSpan?>? offlineFor = null, WorkSharingSettings? sharing = null,
        IReadOnlyCollection<string>? thinkingPool = null, IReadOnlyCollection<string>? poolOptOut = null, string? voiceEngine = null,
        IReadOnlyCollection<string>? configuredProviders = null, TimeSpan? offlineGrace = null)
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
            ThisPc = true
        });
        foreach (var host in NetworkMap.Hosts(inputs).Where(h => h.HostId != ownHostId))
        {
            var id = host.HostId;
            var member = members.FirstOrDefault(c => (c.HostId ?? HostSetupCommands.SuggestedHostId(c.Name)) == id);
            var hardware = inputs.HostHardware?.FirstOrDefault(h => h.HostId == id);
            var check = inputs.HostChecks.GetValueOrDefault(id);
            computers.Add(new SetupComputer(id, member?.Name ?? id,
                member?.Role == DeviceRole.Companion ? NetworkMachineKind.Companion : NetworkMachineKind.Host)
            {
                Hardware = hardware,
                HasHostService = true,
                Manageable = host.CanLaunch && PlatformCatalog.ManagesRolesRemotely(PlatformDevice.FromHost(id, hardware)),
                Reachable = check?.Reachable,
                OfflineFor = offlineFor?.Invoke(id),
                Offers = check?.Offers
            });
        }
        foreach (var member in members.Where(m => m.Role != DeviceRole.Host && computers.All(c => c.Name != m.Name && c.Id != m.DeviceId)))
            computers.Add(new SetupComputer(member.DeviceId, member.Name, NetworkMachineKind.Companion));

        // What this PC does for each job it set up (lip-sync always has a handler: this PC, a host or nobody).
        var routes = inputs.Settings?.Setup?.Routes ?? [];
        var jobs = ClusterJobs.All.Select(job =>
        {
            var local = ClusterSync.Local(job, inputs.Settings, inputs.Avatar, ownHostId);
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
            Plan = inputs.Plan, LocalJobs = jobs, JobOptions = options, Sharing = sharing ?? new(), Device = device,
            ThinkingPool = thinkingPool ?? [], PoolOptOut = poolOptOut ?? [], VoiceEngine = voiceEngine,
            ConfiguredProviders = providers, OfflineGrace = offlineGrace ?? TimeSpan.FromMinutes(10)
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
