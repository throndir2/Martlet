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

/// <summary>One part of Martlet in the review's priority list: its place in the list, whether a conversation needs it
/// ("Needed") or it is optional and can be off, where it runs in the recommended setup or that it is off, and why.
/// <see cref="OwnerOff"/>: the owner turned it off here.</summary>
internal sealed record ReviewPart(PlanComponent Component, int Rank, string Name, string Need, bool On, string Where, string Why,
    bool CanBeOff, bool OwnerOff)
{
    internal string Key => Component.ToString();

    /// <summary>"1. Thinking (needed): Gemma 4 E2B in Ollama on This PC's NVIDIA GeForce RTX 4070."</summary>
    internal string Text => $"{Rank}. {Name} ({Need.ToLowerInvariant()}): {Where.TrimEnd('.')}.";
}

/// <summary>Home's Recommended setup review, in words: the computers (companion PCs kept light), who does each job and the
/// Thinking pool, every change with why, notes, downloads and the changes someone has to make at a computer.
/// <see cref="CannotReply"/>: in the recommended setup nobody does Thinking, so Martlet can't reply (shown as a problem at the
/// top). <see cref="OffersFreeKey"/>: no hosted provider has a saved key, so the review offers a free one (FreeKeyPrompt).
/// <see cref="Offline"/>: one sentence about the computers that stay away, instead of the same words on every change.</summary>
internal sealed record RecommendedSetupReview(string Title, string Summary, IReadOnlyList<ReviewComputer> Computers,
    IReadOnlyList<string> Jobs, IReadOnlyList<ReviewChange> Changes, IReadOnlyList<string> Notes, string? Downloads,
    IReadOnlyList<string> Manual, bool AlreadyOptimal, string Fingerprint, bool CannotReply = false, bool OffersFreeKey = false,
    string? Offline = null)
{
    internal const string OptimalTitle = "Your computers already use the recommended setup.";

    /// <summary>Every part of Martlet in priority order: where it runs, or that it is off.</summary>
    public IReadOnlyList<ReviewPart> Parts { get; init; } = [];

    /// <summary>Use models your apps already run is on: the review lists <see cref="Served"/>, the chat models found.</summary>
    public bool UseServed { get; init; } = true;

    /// <summary>The chat models the model apps on this PC serve: "qwen3:32b in LM Studio (about 21 GB)".</summary>
    public IReadOnlyList<string> Served { get; init; } = [];

    /// <summary>The review of <paramref name="recommendation"/> for the computers in <paramref name="build"/>. Pure.</summary>
    internal static RecommendedSetupReview From(NetworkRecommendation recommendation, SetupRequestBuild build, FootprintCatalog? catalog = null)
    {
        ArgumentNullException.ThrowIfNull(recommendation);
        ArgumentNullException.ThrowIfNull(build);
        var request = build.Request;
        catalog = (catalog ?? FootprintCatalog.Default).WithServed(request.ServedModels);
        string Name(string? id) => id is null or "" ? "your companion PCs" : build.Names.GetValueOrDefault(id)
            ?? request.Machines.FirstOrDefault(m => m.Specs.Id == id)?.Specs.Name ?? id;

        // Computers that aren't answering: one sentence says so, so each change's reason (SetupChange.Detail) and the notes
        // leave it out.
        var ids = request.Machines.Select(m => m.Specs.Id).ToList();
        var gone = recommendation.Offline.OrderBy(o => ids.IndexOf(o.Id)).ToArray();
        var cannotReply = recommendation.CannotReply;

        // In the order Reconfigure makes them: the priority list's setup order (Thinking first).
        var changes = recommendation.Changes
            .Select(change => new ReviewChange(change.Summary, change.Detail, BenefitWord(change.Benefit), Name(change.MachineId),
                change.NeedsSomeoneThere))
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
            var companion = machine.Kind == NetworkMachineKind.Companion;
            var todayText = Runs(today, machine, companion ? recommendation.Current.Jobs : [], catalog);
            var plannedText = Runs(planned, machine, companion ? recommendation.Target.Jobs : [], catalog);
            computers.Add(new ReviewComputer(id, Name(id), kind,
                "Today: " + todayText,
                "Recommended: " + (!machine.Online ? "left out while it isn't answering"
                    : todayText == plannedText ? "no change" : plannedText),
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

        var hidden = gone.Select(o => o.Note).Append(recommendation.CannotReplyNote).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var notes = recommendation.Notes.Concat(build.Notes).Distinct(StringComparer.Ordinal).Where(note => !hidden.Contains(note)).ToArray();
        var count = recommendation.Changes.Count;
        // A change with no computer ("") is a job no host does before or after (a hosted provider, or each companion PC itself).
        var places = recommendation.Changes.Select(c => c.MachineId).Where(id => id.Length > 0).Distinct(StringComparer.Ordinal).Count();
        var computersCount = request.Machines.Count;
        var summary = recommendation.AlreadyOptimal
            ? $"Martlet checked {Count(computersCount, "computer")} against the recommended setup. Nothing needs to change."
            : $"{Count(count, "change")}{(places > 0 ? $" on {Count(places, "computer")}" : "")}. Companion PCs stay light so games keep their graphics card, " +
              "and each graphics card runs at most one language model. Nothing changes until you choose Reconfigure.";
        return new(recommendation.AlreadyOptimal ? OptimalTitle : "A better setup is ready for your computers", summary, computers, jobs,
            changes, notes, downloadText, manual, recommendation.AlreadyOptimal, recommendation.Fingerprint, cannotReply,
            FreeKeyPrompt.Shows(request.ConfiguredProviders), OfflineSentence(gone.Select(o => (Name(o.Id), o.For)).ToArray()))
        {
            Parts = [.. recommendation.Components.Select(c => new ReviewPart(c.Component, c.Rank, c.Name, c.CanBeOff ? "Optional" : "Needed",
                c.On, c.Where, c.Why, c.CanBeOff, c.OwnerOff))],
            Served = ServedLines(request.ServedModels)
        };
    }

    /// <summary>One line per chat model the owner's model apps serve, biggest first: "qwen3:32b in LM Studio (about 21 GB)".</summary>
    internal static IReadOnlyList<string> ServedLines(IEnumerable<ServedModel> served) =>
        [.. served.Where(m => ServedModels.Chats(m.ModelId)).DistinctBy(m => (m.ModelId, m.AppId))
            .OrderByDescending(m => ServedModels.Gb(m) ?? 0).ThenBy(m => m.ModelId, StringComparer.OrdinalIgnoreCase)
            .Select(m => $"{m.ModelId} in {(m.AppName.Length > 0 ? m.AppName : "your model app")}" +
                (ServedModels.Gb(m) is { } gb ? $" (about {gb.ToString("0", System.Globalization.CultureInfo.InvariantCulture)} GB)" : ""))];

    /// <summary>A computer's host roles and, on a companion PC, the jobs it does itself (Thinking in Ollama, Parakeet in Martlet):
    /// "Chatterbox Turbo; on the PC itself: Thinking (Gemma 4 E2B in Ollama), Listening (Parakeet on the processor)".</summary>
    internal static string Runs(IReadOnlyList<HostedRolePlacement> roles, NetworkMachine machine, IReadOnlyList<JobPlan> jobs, FootprintCatalog catalog)
    {
        var itself = jobs.Where(j => j.HostId is null && !j.Off && ClusterJobs.All.Contains(j.Job))
            .Select(j => (Job: j.Job, Option: j.OptionId is { } id ? catalog.Find(id) : null))
            .Where(j => j.Option is { IsLocal: true })
            .Select(j => $"{ClusterSync.Title(j.Job)} ({j.Option!.DisplayName}{(j.Job == ClusterJobs.Thinking && !j.Option.RunsInApp ? $" in {j.Option.ServedBy ?? "Ollama"}" : "")})")
            .ToList();
        var hostRoles = Roles(roles, machine);
        if (itself.Count == 0) return hostRoles;
        return (machine.HasHostService && roles.Count > 0 ? hostRoles + "; " : "") + "on the PC itself: " + string.Join(", ", itself);
    }

    /// <summary>"MIKU and IMOUTO haven't answered for 2 hours, so Martlet plans without them.", or null when every computer
    /// answers. A computer that isn't answering is never part of the plan, however short the time.</summary>
    internal static string? OfflineSentence(IReadOnlyList<(string Name, TimeSpan Away)> gone)
    {
        if (gone.Count == 0) return null;
        var times = gone.Select(g => Minutes(g.Away)).ToArray();
        var them = gone.Count == 1 ? "it" : "them";
        if (gone.All(g => g.Away.TotalMinutes < 1))
            return $"{Join(gone.Select(g => g.Name).ToArray())} {(gone.Count == 1 ? "isn't" : "aren't")} answering, so Martlet plans without {them}.";
        if (times.Distinct(StringComparer.Ordinal).Count() == 1)
            return $"{Join(gone.Select(g => g.Name).ToArray())} {(gone.Count == 1 ? "hasn't" : "haven't")} answered for {times[0]}, so Martlet plans without {them}.";
        return $"{Join(gone.Select((g, i) => $"{g.Name} ({times[i]})").ToArray())} haven't answered, so Martlet plans without {them}.";
    }

    private static string Join(IReadOnlyList<string> items) => items.Count switch
    {
        1 => items[0],
        2 => $"{items[0]} and {items[1]}",
        _ => $"{string.Join(", ", items.Take(items.Count - 1))} and {items[^1]}"
    };

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

    private static string Who(JobPlan job, Func<string?, string> name, FootprintCatalog catalog) =>
        job.Off ? "nobody (the character moves its mouth with the voice's loudness)"
        : job.HostId is { } host ? name(host)
        : job.OptionId is { } option && catalog.Find(option) is { } found
            ? found.ServedOn is { } on ? $"{name(on)} itself ({found.DisplayName} in {found.ServedBy})"
            : found.IsLocal ? $"each companion PC itself ({found.DisplayName}{(found.ServedBy is { } app ? $" in {app}" : "")})" : found.DisplayName
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
