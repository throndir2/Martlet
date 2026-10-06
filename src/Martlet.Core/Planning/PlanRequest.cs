namespace Martlet.Core.Planning;

/// <summary>The user's stance on hosted providers. PreferLocal: keep everything on my computers (nothing hosted, even if
/// configured). Balanced: local first where it is as good, free hosted endpoints where local hardware falls short.
/// PreferHosted: I don't mind free endpoints or signing up (NVIDIA Build); spend local hardware on voice and face.</summary>
public enum HostingPreference { Balanced, PreferLocal, PreferHosted }

/// <summary>What runs today: a component, the catalog option id and the machine (null when hosted).</summary>
public sealed record CurrentAssignment(PlanComponent Component, string OptionId, string? MachineId);

/// <summary>Everything the engine plans from. <see cref="ConfiguredProviders"/> are provider ids with a saved key
/// ("nvidia-build", "openrouter", "openai"). <see cref="Wanted"/> null means every component.
/// <see cref="Current"/> is today's setup, for join/leave suggestions.</summary>
public sealed record PlanRequest(IReadOnlyList<MachineSpecs> Machines)
{
    public HostingPreference Preference { get; init; } = HostingPreference.Balanced;
    public IReadOnlyCollection<string> ConfiguredProviders { get; init; } = [];
    public IReadOnlyCollection<PlanComponent>? Wanted { get; init; }
    public IReadOnlyList<CurrentAssignment> Current { get; init; } = [];

    public bool Wants(PlanComponent component) => Wanted is null || Wanted.Contains(component);
}

/// <summary>Primary: does the job. Fallback: takes over when the primary (a free hosted endpoint) fails.</summary>
public enum AssignmentRole { Primary, Fallback }

/// <summary>Where a component runs: <see cref="MachineId"/> and <see cref="GpuIndex"/> (into that machine's Gpus) when
/// local, both null when hosted.</summary>
public sealed record Assignment(PlanComponent Component, ComponentOption Option, string? MachineId, int? GpuIndex,
    AssignmentRole Role, string Why)
{
    public bool IsExternal => !Option.IsLocal;
}

/// <summary>A capacity and how much of it the plan uses (GB for memory, threads for the processor).</summary>
public sealed record ResourceGauge(double Capacity, double Used)
{
    public double Free => Math.Max(0, Capacity - Used);
    /// <summary>0-100 (more when overcommitted); 0 when there is no capacity.</summary>
    public double Percent => Capacity <= 0 ? 0 : Math.Round(Used / Capacity * 100, 1);
}

public sealed record UsageItem(PlanComponent Component, string OptionId, int? GpuIndex, ResourceUse Use);

/// <summary>One card: <see cref="Vram"/> capacity is what the planner may use (after headroom for the driver, desktop and
/// <see cref="MachineGpu.UsedGb"/>), <see cref="TotalGb"/> the card's size.</summary>
public sealed record GpuUsage(int Index, string Name, GpuVendor Vendor, double TotalGb, ResourceGauge Vram, bool UnifiedMemory = false);

/// <summary>Per machine: capacities after headroom, what the plan uses and the per-component breakdown. Processor use may
/// pass 100%: components rarely work at the same moment, so the engine lets threads be shared up to 150%. Disk capacity is
/// 0 when the free space is unknown.</summary>
public sealed record MachineUsage(string MachineId, string Name, string Platform, bool IsPrimary, IReadOnlyList<GpuUsage> Gpus,
    ResourceGauge Ram, ResourceGauge Cpu, ResourceGauge Disk, IReadOnlyList<UsageItem> Items);

public enum DropReason { NotWanted, NoRoom, NeedsNvidia, NoProvider, KeptLocal, NotSetUp }

/// <summary>A component the plan leaves out, or an upgrade it could not give.</summary>
public sealed record DroppedComponent(PlanComponent Component, DropReason Reason, string Why);

/// <summary>RunLocally: replace a hosted option with a local one. Upgrade: a better option in the same component.
/// Add: a component that was missing. Move: same option, another machine. SignUp: a free provider to sign up for.
/// AddFallback: a backup for a flaky hosted primary. Drop: a component the new plan leaves out.</summary>
public enum SuggestionKind { RunLocally, Upgrade, Add, Move, SignUp, AddFallback, Drop }

public sealed record PlanSuggestion(SuggestionKind Kind, PlanComponent Component, string? MachineId, string? FromOptionId,
    string? ToOptionId, string Why);

public sealed record PlacementPlan(
    HostingPreference Preference,
    IReadOnlyList<Assignment> Assignments,
    IReadOnlyList<MachineUsage> Machines,
    IReadOnlyList<DroppedComponent> Dropped,
    IReadOnlyList<PlanSuggestion> Suggestions,
    IReadOnlyList<string> Notes)
{
    public Assignment? Primary(PlanComponent component) =>
        Assignments.FirstOrDefault(a => a.Component == component && a.Role == AssignmentRole.Primary);

    public Assignment? Fallback(PlanComponent component) =>
        Assignments.FirstOrDefault(a => a.Component == component && a.Role == AssignmentRole.Fallback);

    public IReadOnlyList<Assignment> On(string machineId) => Assignments.Where(a => a.MachineId == machineId).ToArray();

    public IReadOnlyList<Assignment> External => Assignments.Where(a => a.IsExternal).ToArray();

    public MachineUsage? Usage(string machineId) => Machines.FirstOrDefault(m => m.MachineId == machineId);

    /// <summary>Today's setup in the shape <see cref="PlanRequest.Current"/> takes.</summary>
    public IReadOnlyList<CurrentAssignment> AsCurrent() =>
        Assignments.Where(a => a.Role == AssignmentRole.Primary).Select(a => new CurrentAssignment(a.Component, a.Option.Id, a.MachineId)).ToArray();
}
