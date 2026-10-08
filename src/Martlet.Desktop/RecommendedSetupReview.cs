using System.Globalization;
using Martlet.Core.Cluster;
using Martlet.Core.Planning;
using Martlet.Core.Settings;

namespace Martlet.Desktop;

/// <summary>One computer in the review: what it is, what it runs today and in the recommended setup, and how full the
/// recommended setup makes it (<see cref="Bars"/>, the Devices page's resource bars; empty when not measured).</summary>
internal sealed record ReviewComputer(string Id, string Name, string Kind, string Today, string Recommended, string Load,
    IReadOnlyList<CapacityBar> Bars, bool Changes);

/// <summary>One change in the review, in the recommender's words, with how much it matters.</summary>
internal sealed record ReviewChange(string Summary, string Why, string Benefit, string Computer, bool NeedsSomeoneThere);

/// <summary>Home's Recommended setup review, in words: the computers (companion PCs kept light), who does each job and the
/// Thinking pool, every change with why, notes, downloads and the changes someone has to make at a computer.</summary>
internal sealed record RecommendedSetupReview(string Title, string Summary, IReadOnlyList<ReviewComputer> Computers,
    IReadOnlyList<string> Jobs, IReadOnlyList<ReviewChange> Changes, IReadOnlyList<string> Notes, string? Downloads,
    IReadOnlyList<string> Manual, bool AlreadyOptimal, string Fingerprint)
{
    internal const string OptimalTitle = "Your computers already use the recommended setup.";

    /// <summary>The review of <paramref name="recommendation"/> for the computers in <paramref name="build"/>. Pure.</summary>
    internal static RecommendedSetupReview From(NetworkRecommendation recommendation, SetupRequestBuild build, FootprintCatalog? catalog = null)
    {
        ArgumentNullException.ThrowIfNull(recommendation);
        ArgumentNullException.ThrowIfNull(build);
        catalog ??= FootprintCatalog.Default;
        var request = build.Request;
        string Name(string? id) => id is null or "" ? "your companion PCs" : build.Names.GetValueOrDefault(id)
            ?? request.Machines.FirstOrDefault(m => m.Specs.Id == id)?.Specs.Name ?? id;

        var changes = recommendation.Changes
            .Select((change, index) => (change, index)).OrderBy(c => c.change.Benefit).ThenBy(c => c.index)
            .Select(c => new ReviewChange(c.change.Summary, c.change.Why, BenefitWord(c.change.Benefit), Name(c.change.MachineId),
                c.change.NeedsSomeoneThere))
            .ToArray();

        var computers = new List<ReviewComputer>();
        foreach (var machine in request.Machines)
        {
            var id = machine.Specs.Id;
            var target = recommendation.Target.Machine(id);
            var today = machine.Roles;
            var planned = target?.Roles ?? today;
            var kind = machine.Kind == NetworkMachineKind.Companion ? "Companion PC, kept light for games" : "Host PC";
            if (!machine.Online)
                kind += machine.OfflineFor is { } away ? $", not answering for {Minutes(away)}" : ", not answering";
            if (!machine.Manageable && machine.HasHostService) kind += ", changes there need someone at it";
            var bars = target?.Usage is { } usage ? Bars(machine.Specs, usage, catalog) : [];
            var load = Load(bars, "Recommended load");
            if (recommendation.Current.Machine(id)?.Usage is { } now && Load(Bars(machine.Specs, now, catalog), "today") is { Length: > 0 } before)
                load = load.Length == 0 ? char.ToUpperInvariant(before[0]) + before[1..] : $"{load} ({before})";
            computers.Add(new ReviewComputer(id, Name(id), kind,
                "Today: " + Roles(today, machine),
                "Recommended: " + (Same(today, planned) ? "no change" : Roles(planned, machine)),
                load.Length == 0 ? "" : load + ".", bars, recommendation.Changes.Any(c => c.MachineId == id)));
        }

        var jobs = new List<string>();
        foreach (var job in recommendation.Target.Jobs)
        {
            var before = recommendation.Current.Job(job.Job) ?? request.CurrentJobs.FirstOrDefault(j => j.Job == job.Job);
            var who = Who(job, Name, catalog);
            var line = $"{ClusterSync.Title(job.Job)}: {who}";
            if (before is not null && (before.HostId != job.HostId || before.Off != job.Off ||
                    before.OptionId is not null && job.OptionId is not null && before.OptionId != job.OptionId))
                line += $" (today: {Who(before, Name, catalog)})";
            if (job.Pool.Count > 0) line += $". When it is busy: {string.Join(", ", job.Pool.Select(Name))}";
            jobs.Add(line + ".");
        }
        var pool = recommendation.Target.ThinkingPool;
        var poolBefore = recommendation.Current.ThinkingPool.Count > 0 ? recommendation.Current.ThinkingPool : request.CurrentThinkingPool;
        var poolLine = "Thinking pool (background thinking): " + (pool.Count == 0 ? "no computers" : string.Join(", ", pool.Select(Name)));
        if (!pool.Order(StringComparer.Ordinal).SequenceEqual(poolBefore.Order(StringComparer.Ordinal)))
            poolLine += $" (today: {(poolBefore.Count == 0 ? "no computers" : string.Join(", ", poolBefore.Select(Name)))})";
        jobs.Add(poolLine + ".");

        var downloads = recommendation.Changes.Where(c => c.DownloadGb is > 0).GroupBy(c => c.MachineId)
            .Select(g => (Name: Name(g.Key), Gb: g.Sum(c => c.DownloadGb!.Value))).ToArray();
        var downloadText = downloads.Length == 0 ? null
            : $"Downloads about {Gb(downloads.Sum(d => d.Gb))}: " + string.Join(", ", downloads.Select(d => $"{d.Name} {Gb(d.Gb)}")) + ".";
        var manual = recommendation.Changes.Where(c => c.NeedsSomeoneThere).Select(c => $"On {Name(c.MachineId)}: {c.Summary}").ToArray();

        var notes = recommendation.Notes.Concat(build.Notes).Distinct(StringComparer.Ordinal).ToArray();
        var count = recommendation.Changes.Count;
        // A change with no computer ("") is a job no host does before or after (a hosted provider, or each companion PC itself).
        var places = recommendation.Changes.Select(c => c.MachineId).Where(id => id.Length > 0).Distinct(StringComparer.Ordinal).Count();
        var computersCount = request.Machines.Count;
        var summary = recommendation.AlreadyOptimal
            ? $"Martlet checked {Count(computersCount, "computer")} against the recommended setup. Nothing needs to change."
            : $"{Count(count, "change")}{(places > 0 ? $" on {Count(places, "computer")}" : "")}. Companion PCs stay light so games keep their graphics card, " +
              "and each graphics card runs at most one language model. Nothing changes until you choose Reconfigure.";
        return new(recommendation.AlreadyOptimal ? OptimalTitle : "A better setup is ready for your computers", summary, computers, jobs,
            changes, notes, downloadText, manual, recommendation.AlreadyOptimal, recommendation.Fingerprint);
    }

    internal static string BenefitWord(SetupChangeBenefit benefit) => benefit switch
    {
        SetupChangeBenefit.Required => "Needed",
        SetupChangeBenefit.Improvement => "Improvement",
        _ => "Tidier"
    };

    /// <summary>"Thinking (gemma4:12b), Chatterbox Turbo on GPU 2"; or why a computer runs no host roles.</summary>
    internal static string Roles(IReadOnlyList<HostedRolePlacement> roles, NetworkMachine machine)
    {
        if (!machine.HasHostService) return "only the parts inside Martlet";
        if (roles.Count == 0) return "no host roles";
        return string.Join(", ", roles.Select(role =>
        {
            var engine = SpeechEngines.ForRoleKind(role.Kind);
            var name = engine?.Name ?? HostRoles.All.FirstOrDefault(r => r.Kind == role.Kind)?.Name ?? role.Kind;
            var text = engine is null && role.Model is { Length: > 0 } model ? $"{name} ({model})" : name;
            if (role.GpuIndex is { } gpu && machine.Specs.Gpus.Count > 1 && gpu >= 0 && gpu < machine.Specs.Gpus.Count)
                text += $" on GPU {gpu + 1}";
            return text;
        }));
    }

    private static bool Same(IReadOnlyList<HostedRolePlacement> a, IReadOnlyList<HostedRolePlacement> b) =>
        a.OrderBy(r => r.Kind, StringComparer.Ordinal).SequenceEqual(b.OrderBy(r => r.Kind, StringComparer.Ordinal));

    private static string Who(JobPlan job, Func<string?, string> name, FootprintCatalog catalog) =>
        job.Off ? "nobody (the character moves its mouth with the voice's loudness)"
        : job.HostId is { } host ? name(host)
        : job.OptionId is { } option && catalog.Find(option) is { } found
            ? found.IsLocal ? $"each companion PC itself ({found.DisplayName})" : found.DisplayName
        : "each companion PC itself";

    /// <summary>The Devices page's bars for a machine's planned use: graphics memory, memory and processor.</summary>
    internal static IReadOnlyList<CapacityBar> Bars(MachineSpecs spec, MachineUsage usage, FootprintCatalog catalog)
    {
        var specs = new CapacitySpecs(spec.Gpus.Count == 0 ? null : spec.Gpus.Sum(g => g.VramGb), spec.RamGb > 0 ? spec.RamGb : null,
            spec.CpuThreads > 0 ? spec.CpuThreads : null, null);
        var usable = new CapacitySpecs(usage.Gpus.Count == 0 ? null : usage.Gpus.Sum(g => g.Vram.Capacity), usage.Ram.Capacity,
            usage.Cpu.Capacity, null);
        var components = usage.Items.Select(i => new CapacityComponent(i.OptionId,
            catalog.Find(i.OptionId) is { } option ? DeviceCapacityInputs.Named(option) : ComponentRanking.Name(i.Component),
            Need(i.Use), Need(i.Usual))).ToArray();
        return [.. DeviceCapacity.Bars(specs, components, null, usable).Where(b => b.Resource != CapacityResource.Disk && (b.Total is > 0 || b.Planned > 0))];
    }

    private static CapacityNeed Need(ResourceUse use) => new(use.VramGb, use.RamGb, use.CpuThreads, 0);

    /// <summary>"Recommended load: 58% graphics memory, 20% memory, 12% processor", or "" when nothing is measured.</summary>
    private static string Load(IReadOnlyList<CapacityBar> bars, string label)
    {
        var parts = bars.Where(b => b.PlannedPercent is not null)
            .Select(b => $"{b.PlannedPercent!.Value.ToString("0", CultureInfo.CurrentCulture)}% {DeviceCapacity.Word(b.Resource)}").ToArray();
        return parts.Length == 0 ? "" : $"{label}: {string.Join(", ", parts)}";
    }

    private static string Minutes(TimeSpan away) => away.TotalMinutes < 1 ? "less than a minute"
        : away.TotalHours >= 2 ? $"{(int)away.TotalHours} hours" : Count((int)away.TotalMinutes, "minute");

    private static string Gb(double gb) => gb.ToString(gb >= 10 ? "0" : "0.#", CultureInfo.CurrentCulture) + " GB";

    private static string Count(int count, string noun) => $"{count} {noun}{(count == 1 ? "" : "s")}";
}
