using Martlet.Core.Planning;
using Martlet.Core.Settings;

namespace Martlet.Desktop;

/// <summary>Turns what the Devices map shows (its nodes, the saved routes and what each paired host offers) into the
/// placement engine's inputs, and the engine's measure of today's setup into the Devices page's resource views. The
/// numbers are the engine's and its footprint catalog's (<see cref="PlacementEngine"/>, <see cref="FootprintCatalog"/>);
/// this only maps names and ids. Reads nothing itself.</summary>
internal static class DeviceCapacityInputs
{
    internal const string ThisPcId = "this-pc";

    /// <summary>The engine's machine id for a device on the map: "this-pc", a paired host's id, or null (a cloud service,
    /// an OpenAI-compatible server on the network or anything without a hardware report).</summary>
    internal static string? MachineId(NetworkNode node) => node.Kind switch
    {
        NodeKind.ThisPc => ThisPcId,
        NodeKind.Host when node.Id.StartsWith("host:", StringComparison.Ordinal) => node.Id[5..],
        _ => null
    };

    internal static MachineSpecs ThisPc(MachineInfo machine, double? diskFreeGb) => MachineSpecs.ThisPc(
        [.. machine.Gpus.Select(g => new MachineGpu(g.Name, MachineGpu.VendorOf(g.Name), g.MemoryGb ?? 0)).Where(g => g.IsNvidia || g.VramGb >= 2.5)],
        machine.MemoryGb ?? 0, machine.Threads, diskFreeGb: diskFreeGb);

    /// <summary>This PC and every host on the map that has reported its hardware.</summary>
    internal static IReadOnlyList<MachineSpecs> Machines(NetworkInputs inputs, IReadOnlyList<NetworkNode> nodes, double? diskFreeGb)
    {
        var machines = new List<MachineSpecs> { ThisPc(inputs.Machine, diskFreeGb) };
        foreach (var node in nodes.Where(n => n.Kind == NodeKind.Host))
            if (MachineId(node) is { } id && inputs.HostHardware?.FirstOrDefault(h => h.HostId == id) is { } report)
                machines.Add(MachineSpecs.FromHostHardware(report, node.Title));
        return machines;
    }

    /// <summary>The network this PC is joining, for the welcome wizard's join suggestion: the other computers that reported
    /// their hardware and what they and hosted providers run today (this PC's own local parts left out: it is the newcomer).</summary>
    internal static PlanRequest JoinNetwork(NetworkInputs inputs, IReadOnlyList<NetworkNode> nodes, FootprintCatalog catalog)
    {
        var machines = Machines(inputs, nodes, null).Where(m => m.Id != ThisPcId).ToArray();
        var current = Current(inputs, nodes, catalog)
            .Where(c => c.MachineId is null || c.MachineId != ThisPcId && machines.Any(m => m.Id == c.MachineId)).ToArray();
        return new PlanRequest(machines) { Current = current };
    }

    /// <summary>Today's setup as the engine takes it: every job and host role on the map, with the catalog option that runs
    /// it and the machine (null when a hosted provider does it).</summary>
    internal static IReadOnlyList<CurrentAssignment> Current(NetworkInputs inputs, IReadOnlyList<NetworkNode> nodes, FootprintCatalog catalog)
    {
        var current = new List<CurrentAssignment>();
        foreach (var node in nodes.Where(n => n.Kind is NodeKind.ThisPc or NodeKind.Host or NodeKind.Cloud))
        {
            var machine = MachineId(node);
            if (node.Kind != NodeKind.Cloud && machine is null) continue;
            foreach (var role in node.Roles)
                if (Option(role.Component, node, machine, inputs, catalog) is { } option)
                {
                    var assignment = new CurrentAssignment(option.Component, option.Id, option.IsLocal ? machine : null);
                    if (!current.Contains(assignment)) current.Add(assignment);
                }
        }
        // Primary first: a job's route before a host role standing by with the same component.
        return [.. current.OrderBy(c => nodes.Any(n => MachineId(n) == c.MachineId && n.Roles.Any(r => r.Component?.StartsWith("role:", StringComparison.Ordinal) == true &&
            KindComponent(r.Component[5..]) == c.Component && Option(r.Component, n, c.MachineId, inputs, catalog)?.Id == c.OptionId)) ? 1 : 0)];
    }

    internal static PlanComponent? KindComponent(string kind) => kind switch
    {
        HostRoles.Audio2Face => PlanComponent.LipSync,
        HostRoles.Ollama => PlanComponent.Thinking,
        HostRoles.DeepThinking => PlanComponent.DeepThinking,
        HostRoles.Stt => PlanComponent.Listening,
        HostRoles.Singing => PlanComponent.Singing,
        HostRoles.Pictures => PlanComponent.Pictures,
        _ when HostRoles.Speaks(kind) => PlanComponent.Voice,
        _ => null
    };

    private static ComponentOption? Option(string? component, NetworkNode node, string? machine, NetworkInputs inputs, FootprintCatalog catalog)
    {
        var offers = machine is null || machine == ThisPcId && node.PairedHostId is null ? null
            : inputs.HostChecks.GetValueOrDefault(node.PairedHostId ?? machine)?.Offers;
        var local = node.Kind != NodeKind.Cloud;
        if (DeviceComponent.JobRole(component) is { } job)
        {
            var route = inputs.Settings?.Setup?.Routes.FirstOrDefault(r => r.Role == job);
            var plan = job switch { SetupRole.Llm => PlanComponent.Thinking, SetupRole.Stt => PlanComponent.Listening, _ => PlanComponent.Voice };
            if (!local) return route is null ? null : Hosted(catalog, plan, route);
            return (job, route?.RouteType) switch
            {
                (SetupRole.Stt, SetupRouteType.LocalParakeet) => catalog.For(plan).FirstOrDefault(o => o.IsLocal && o.RunsInApp),
                (SetupRole.Stt, SetupRouteType.LocalWindowsStt) => null,
                (SetupRole.Llm, _) => Find(catalog, plan, route?.ModelId ?? offers?.GetValueOrDefault(HostRoles.Ollama), HostRoles.Ollama),
                (SetupRole.Stt, _) => Find(catalog, plan, route?.ModelId ?? offers?.GetValueOrDefault(HostRoles.Stt), HostRoles.Stt),
                _ => offers?.Keys.FirstOrDefault(HostRoles.Speaks) is { } engine
                    ? Find(catalog, plan, offers[engine], engine)
                    : Find(catalog, plan, route?.ModelId, null)
            };
        }
        if (component == DeviceComponent.LipSync)
            return node.Kind == NodeKind.ThisPc && NetworkMap.LipSync(inputs.Avatar) == LipSyncHandler.Loudness
                ? catalog.For(PlanComponent.LipSync).FirstOrDefault(o => o.IsLocal && o.RunsInApp)
                : Find(catalog, PlanComponent.LipSync, null, HostRoles.Audio2Face);
        if (component == DeviceComponent.Character)
            return catalog.For(PlanComponent.Character).FirstOrDefault(o => o.IsLocal);
        if (component?.StartsWith("role:", StringComparison.Ordinal) == true && KindComponent(component[5..]) is { } hosted)
        {
            var kind = component[5..];
            return Find(catalog, hosted, offers?.GetValueOrDefault(kind), kind);
        }
        return null;
    }

    /// <summary>The local option running <paramref name="model"/>, else the first one of the host role <paramref name="kind"/>.</summary>
    private static ComponentOption? Find(FootprintCatalog catalog, PlanComponent component, string? model, string? kind)
    {
        if (model is { Length: > 0 } && catalog.FindModel(component, model) is { IsLocal: true } found &&
            (kind is null || found.HostRoleKind is null || found.HostRoleKind == kind))
            return found;
        return catalog.For(component).FirstOrDefault(o => o.IsLocal && kind is not null && o.HostRoleKind == kind);
    }

    /// <summary>The hosted option for a cloud route, by its provider (OpenAI, NVIDIA Build, OpenRouter), or null.</summary>
    private static ComponentOption? Hosted(FootprintCatalog catalog, PlanComponent component, SetupRoute route)
    {
        var origin = route.Origin.ToLowerInvariant();
        var provider = route.RouteType == SetupRouteType.OpenAi || origin.Contains("openai.com", StringComparison.Ordinal) ? "openai"
            : origin.Contains("nvidia", StringComparison.Ordinal) ? "nvidia-build"
            : origin.Contains("openrouter", StringComparison.Ordinal) ? "openrouter" : null;
        return provider is null ? null : catalog.For(component).FirstOrDefault(o => !o.IsLocal && o.ProviderId == provider);
    }

    // ---------- the engine's measure → the Devices page ----------

    /// <summary>What one copy of a component is called in "Room for 2 more ..." lines.</summary>
    internal static string Noun(PlanComponent component) => component switch
    {
        PlanComponent.Thinking => "Thinking model",
        PlanComponent.Voice => "voice engine",
        PlanComponent.Listening => "listening model",
        PlanComponent.LipSync => "advanced lip-sync service",
        PlanComponent.DeepThinking => "Deep thinking model",
        PlanComponent.Singing => "singing service",
        PlanComponent.Pictures => "picture service",
        _ => ComponentRanking.Name(component)
    };

    /// <summary>"Thinking (Gemma 4 E2B)": the component and the option running it; just the option when its name says the
    /// component already ("Advanced lip-sync (Audio2Face-3D)").</summary>
    internal static string Named(ComponentOption option)
    {
        var name = ComponentRanking.Name(option.Component);
        return option.DisplayName.Contains(name, StringComparison.OrdinalIgnoreCase) ? option.DisplayName : $"{name} ({option.DisplayName})";
    }

    /// <summary>Parts where more than one copy helps (another model to think with); of the rest one is enough.</summary>
    internal static bool Many(PlanComponent component) => component is PlanComponent.Thinking or PlanComponent.DeepThinking;

    /// <summary>What else fits on <paramref name="machineId"/> (null: across the network), most important part first: for
    /// Thinking and Deep thinking how many more of the model in use (or the best one that fits); for the other parts, the best
    /// option, only while none of your computers runs that part. Things that run inside the app are left out.</summary>
    internal static IReadOnlyList<CapacityFit> Fits(PlacementPlan plan, FootprintCatalog catalog, string? machineId)
    {
        var fits = new List<CapacityFit>();
        foreach (var info in ComponentRanking.All.Where(i => i.Component != PlanComponent.Character))
        {
            var candidates = catalog.For(info.Component).Where(o => o.IsLocal && !o.RunsInApp).ToList();
            if (candidates.Count == 0) continue;
            var many = Many(info.Component);
            var local = plan.Assignments.Where(a => a.Component == info.Component && !a.IsExternal).Select(a => a.Option).ToList();
            if (!many && local.Any(o => !o.RunsInApp)) continue;
            var used = local.FirstOrDefault(o => !o.RunsInApp);
            var option = used is not null && PlacementEngine.Afford(plan, used, machineId) > 0 ? used
                : candidates.Select(o => (Option: o, Count: PlacementEngine.Afford(plan, o, machineId))).Where(x => x.Count > 0)
                    .OrderByDescending(x => x.Option.QualityTier).ThenByDescending(x => x.Count).Select(x => x.Option).FirstOrDefault();
            if (option is null) continue;
            fits.Add(new(option.Id, Noun(info.Component), many ? Short(option) : Named(option),
                PlacementEngine.Afford(plan, option, machineId), many));
        }
        return fits;
    }

    /// <summary>The option's name without a "(deep thinking)" that the noun already says.</summary>
    private static string Short(ComponentOption option)
    {
        var suffix = $" ({ComponentRanking.Name(option.Component)})";
        return option.DisplayName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) ? option.DisplayName[..^suffix.Length] : option.DisplayName;
    }

    /// <summary>One device's resource view from the engine's measure of today's setup, or null when the engine doesn't know
    /// the device (a host that hasn't reported its hardware still shows its jobs, with unknown shares).</summary>
    internal static DeviceCapacityView? Device(NetworkNode node, IReadOnlyList<MachineSpecs> machines, PlacementPlan plan,
        IReadOnlyList<CurrentAssignment> current, FootprintCatalog catalog, CapacityLive? live)
    {
        if (MachineId(node) is not { } id) return null;
        var spec = machines.FirstOrDefault(m => m.Id == id);
        var usage = plan.Usage(id);
        var components = usage is not null
            ? usage.Items.Select(i => (Option: catalog.Find(i.OptionId), i.Use, i.Usual)).Where(x => x.Option is not null)
                .Select(x => new CapacityComponent(x.Option!.Id, Named(x.Option), Need(x.Use), Need(x.Usual))).ToList()
            : current.Where(c => c.MachineId == id).Select(c => catalog.Find(c.OptionId)).OfType<ComponentOption>()
                .Select(o => new CapacityComponent(o.Id, Named(o), Need(o.Reserve), Need(o.Usual))).ToList();
        if (spec is null && components.Count == 0) return null;
        var specs = spec is null ? new CapacitySpecs(null, null, null, null)
            : new CapacitySpecs(spec.Gpus.Count == 0 ? null : spec.Gpus.Sum(g => g.VramGb), spec.RamGb > 0 ? spec.RamGb : null,
                spec.CpuThreads > 0 ? spec.CpuThreads : null, spec.DiskFreeGb);
        var usable = usage is null ? null
            : new CapacitySpecs(usage.Gpus.Count == 0 ? null : usage.Gpus.Sum(g => g.Vram.Capacity), usage.Ram.Capacity,
                usage.Cpu.Capacity, usage.Disk.Capacity > 0 ? usage.Disk.Capacity : null);
        var bars = DeviceCapacity.Bars(specs, components, live, usable);
        var fits = usage is null ? [] : DeviceCapacity.AlsoFits(Fits(plan, catalog, id), "here");
        return new(SpecsText(spec), bars, components, fits);
    }

    private static CapacityNeed Need(ResourceUse use) => new(use.VramGb, use.RamGb, use.CpuThreads, use.DiskGb);

    /// <summary>"NVIDIA GeForce RTX 4090 (24 GB) · 64 GB memory · 32 processor threads · 812 GB free disk".</summary>
    internal static string SpecsText(MachineSpecs? spec)
    {
        if (spec is null) return "This device hasn't reported its hardware yet. Use Check connection.";
        var parts = new List<string>();
        parts.AddRange(spec.Gpus.Count == 0 ? ["No graphics card for Martlet"]
            : spec.Gpus.Select(g => g.UnifiedMemory ? $"{g.Name} (shares memory, up to {g.VramGb:0.#} GB)" : $"{g.Name} ({g.VramGb:0.#} GB)"));
        if (spec.RamGb > 0) parts.Add($"{spec.RamGb:0} GB memory");
        if (spec.CpuThreads > 0) parts.Add($"{spec.CpuThreads} processor threads");
        if (spec.DiskFreeGb is { } disk) parts.Add($"{disk:0} GB free disk");
        return string.Join(" \u00b7 ", parts);
    }

    /// <summary>The network card: what runs where, the totals and what else fits, from the engine's measure of today's setup.</summary>
    internal static NetworkCapacityView Network(IReadOnlyList<NetworkNode> nodes, IReadOnlyList<MachineSpecs> machines, PlacementPlan plan,
        FootprintCatalog catalog)
    {
        var primaries = plan.Assignments.Where(a => a.Role == AssignmentRole.Primary).ToList();
        var local = primaries.Where(a => !a.IsExternal)
            .Select(a => $"{ComponentRanking.Name(a.Component)} ({machines.FirstOrDefault(m => m.Id == a.MachineId)?.Name ?? a.MachineId})").ToList();
        var hosted = primaries.Where(a => a.IsExternal).Select(a => Named(a.Option)).ToList();
        var covered = primaries.Select(a => a.Component).ToHashSet();
        // A cloud service the catalog doesn't know (any OpenAI-compatible endpoint) still covers its job.
        foreach (var cloud in nodes.Where(n => n.Kind == NodeKind.Cloud))
            foreach (var role in cloud.Roles)
                if (DeviceComponent.JobRole(role.Component) is { } job &&
                    (job switch { SetupRole.Llm => PlanComponent.Thinking, SetupRole.Stt => PlanComponent.Listening, _ => PlanComponent.Voice }) is var c &&
                    covered.Add(c))
                    hosted.Add($"{ComponentRanking.Name(c)} ({cloud.Title})");
        var missing = ComponentRanking.All.Where(i => !covered.Contains(i.Component)).Select(i => i.Name).ToList();
        var totals = machines.Select(m => new CapacitySpecs(m.Gpus.Sum(g => g.VramGb), m.RamGb, m.CpuThreads, m.DiskFreeGb)).ToList();
        return new(DeviceCapacity.Coverage(local, hosted, missing), DeviceCapacity.Totals(totals), DeviceCapacity.NetworkFits(Fits(plan, catalog, null)));
    }
}
