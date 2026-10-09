using System.Globalization;

namespace Martlet.Core.Planning;

/// <summary>Places Martlet's components on the user's machines and hosted providers by the priority ranking
/// (<see cref="ComponentRanking"/>), the footprint catalog and the user's <see cref="HostingPreference"/>. Pure: it reads
/// nothing and contacts nothing. The design and its reasoning are in docs/RECOMMENDATIONS.md.</summary>
public static class PlacementEngine
{
    /// <summary>Graphics memory always left for the driver and desktop (at least; 10% of bigger cards).</summary>
    public const double GpuReserveGb = 0.8;
    /// <summary>Processor threads may be shared up to this factor: components rarely work at the same moment.</summary>
    public const double CpuOversubscription = 1.5;
    /// <summary>Score bonus for doing audio-path work (voice, listening, lip-sync, character) and what reads your screen or hears
    /// your voice (vision, reading, hearing) locally: no per-turn network delay, no cost, nothing leaves the network.</summary>
    private const int LocalAudioBonus = 20;

    public static PlacementPlan Plan(PlanRequest request, FootprintCatalog? catalog = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new Planner(request, catalog ?? FootprintCatalog.Default).Run();
    }

    /// <summary>The usage of <see cref="PlanRequest.Current"/> as it is today, without re-placing anything: what each
    /// machine runs now and how full it is (gauges may pass 100% when today's setup overfills a machine). Unknown option ids
    /// and machines are skipped. No suggestions; notes list what was skipped.</summary>
    public static PlacementPlan Measure(PlanRequest request, FootprintCatalog? catalog = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new Planner(request, catalog ?? FootprintCatalog.Default).Measure();
    }

    /// <summary>What changes when <paramref name="newMachine"/> joins the network described by <paramref name="network"/>
    /// (its machines, preference, providers and <see cref="PlanRequest.Current"/> setup; with no current setup, the plan
    /// for the network without the new machine stands in).</summary>
    public static IReadOnlyList<PlanSuggestion> SuggestForJoiningMachine(PlanRequest network, MachineSpecs newMachine, FootprintCatalog? catalog = null)
    {
        ArgumentNullException.ThrowIfNull(network);
        ArgumentNullException.ThrowIfNull(newMachine);
        catalog ??= FootprintCatalog.Default;
        var current = network.Current.Count > 0 ? network.Current : Plan(network, catalog).AsCurrent();
        var joined = network with
        {
            Machines = [.. network.Machines.Where(m => m.Id != newMachine.Id), newMachine],
            Current = current
        };
        return Compare(current, Plan(joined, catalog), catalog);
    }

    /// <summary>What changes when the machine <paramref name="machineId"/> leaves (rebalancing its components).</summary>
    public static IReadOnlyList<PlanSuggestion> SuggestForLeavingMachine(PlanRequest network, string machineId, FootprintCatalog? catalog = null)
    {
        ArgumentNullException.ThrowIfNull(network);
        catalog ??= FootprintCatalog.Default;
        var current = network.Current.Count > 0 ? network.Current : Plan(network, catalog).AsCurrent();
        var left = network with
        {
            Machines = network.Machines.Where(m => m.Id != machineId).ToArray(),
            Current = current.Where(c => c.MachineId != machineId).ToArray()
        };
        return Compare(current, Plan(left, catalog), catalog);
    }

    /// <summary>The changes from <paramref name="current"/> to <paramref name="proposed"/>, in rank order, each with a reason.</summary>
    public static IReadOnlyList<PlanSuggestion> Compare(IReadOnlyList<CurrentAssignment> current, PlacementPlan proposed, FootprintCatalog? catalog = null)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(proposed);
        catalog ??= FootprintCatalog.Default;
        var suggestions = new List<PlanSuggestion>();
        string Where(string? machineId) => machineId is null ? "a hosted provider"
            : proposed.Usage(machineId)?.Name ?? machineId;
        foreach (var info in ComponentRanking.All)
        {
            var now = current.FirstOrDefault(c => c.Component == info.Component);
            var next = proposed.Primary(info.Component);
            var nowOption = now is null ? null : catalog.Find(now.OptionId);
            if (next is null)
            {
                if (now is not null)
                {
                    var dropped = proposed.Dropped.FirstOrDefault(d => d.Component == info.Component);
                    suggestions.Add(new(SuggestionKind.Drop, info.Component, now.MachineId, now.OptionId, null,
                        dropped?.Why ?? $"{info.Name} no longer fits anywhere."));
                }
                continue;
            }
            var to = next.Option;
            if (now is null)
            {
                suggestions.Add(new(SuggestionKind.Add, info.Component, next.MachineId, null, to.Id,
                    $"{Where(next.MachineId)} can run {info.Name.ToLowerInvariant()} with {to.DisplayName}. {next.Why}"));
                continue;
            }
            if (now.OptionId == to.Id)
            {
                if (now.MachineId != next.MachineId)
                    suggestions.Add(new(SuggestionKind.Move, info.Component, next.MachineId, now.OptionId, to.Id,
                        $"Move {to.DisplayName} from {Where(now.MachineId)} to {Where(next.MachineId)}. {next.Why}"));
                continue;
            }
            if (nowOption is { IsLocal: false } && to.IsLocal)
                suggestions.Add(new(SuggestionKind.RunLocally, info.Component, next.MachineId, now.OptionId, to.Id,
                    $"{Where(next.MachineId)} can run {to.DisplayName} locally instead of {nowOption.DisplayName}: " +
                    $"{LocalGains(nowOption, to)}."));
            else if (nowOption is null || to.QualityTier > nowOption.QualityTier)
                suggestions.Add(new(SuggestionKind.Upgrade, info.Component, next.MachineId, now.OptionId, to.Id,
                    $"{Where(next.MachineId)} can run {to.DisplayName}" +
                    (nowOption is null ? "." : $", better than {nowOption.DisplayName}.") + $" {next.Why}"));
            else
                suggestions.Add(new(SuggestionKind.Move, info.Component, next.MachineId, now.OptionId, to.Id,
                    $"Use {to.DisplayName} on {Where(next.MachineId)}" +
                    (nowOption is null ? "." : $" instead of {nowOption.DisplayName}.") + $" {next.Why}"));
        }
        foreach (var fallback in proposed.Assignments.Where(a => a.Role == AssignmentRole.Fallback))
            suggestions.Add(new(SuggestionKind.AddFallback, fallback.Component, fallback.MachineId, null, fallback.Option.Id, fallback.Why));
        suggestions.AddRange(proposed.Suggestions.Where(s => s.Kind == SuggestionKind.SignUp));
        return suggestions;
    }

    /// <summary>How many more copies of <paramref name="option"/> fit in the plan's spare capacity: on
    /// <paramref name="machineId"/>, or across the network when null ("2 more Deep thinking models fit").</summary>
    public static int Afford(PlacementPlan plan, ComponentOption option, string? machineId = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(option);
        if (!option.IsLocal || option.UsesThinking) return 0;
        var count = 0;
        foreach (var machine in plan.Machines.Where(m => machineId is null || m.MachineId == machineId))
        {
            if (!option.RunsOn(machine.Platform) || option.RunsInApp && !machine.IsPrimary) continue;
            var ram = machine.Ram.Free;
            var cpu = machine.Cpu.Capacity * CpuOversubscription - machine.Cpu.Used;
            var disk = machine.Disk.Capacity > 0 ? machine.Disk.Free : double.MaxValue;
            var gpus = machine.Gpus.Select(g => g.Vram.Free).ToArray();
            for (var i = 0; i < 64; i++)
            {
                var ramNeed = option.Peak.RamGb;
                var gpu = -1;
                if (option.UsesGpu)
                {
                    gpu = Enumerable.Range(0, gpus.Length)
                        .Where(g => GpuMatches(option, machine.Gpus[g].Vendor, machine.Gpus[g].TotalGb) && gpus[g] >= option.GpuGb)
                        .DefaultIfEmpty(-1).First();
                    if (gpu < 0) break;
                    if (machine.Gpus[gpu].UnifiedMemory) ramNeed += option.GpuGb;
                }
                if (ram < ramNeed || cpu < option.Steady.CpuThreads || disk < option.Peak.DiskGb) break;
                ram -= ramNeed;
                cpu -= option.Steady.CpuThreads;
                disk -= option.Peak.DiskGb;
                if (gpu >= 0) gpus[gpu] -= option.GpuGb;
                count++;
            }
        }
        return count;
    }

    /// <summary>The graphics memory the planner may use on <paramref name="gpu"/>: its size less what other programs use or
    /// the headroom kept for the driver and desktop (<see cref="GpuReserveGb"/>, or 10% of bigger cards; none for unified
    /// memory), and nothing when the card is kept for games.</summary>
    public static double GpuCapacityGb(MachineGpu gpu, bool keepForGames = false)
    {
        ArgumentNullException.ThrowIfNull(gpu);
        var reserve = gpu.UnifiedMemory ? 0 : Math.Max(GpuReserveGb, gpu.VramGb * 0.1);
        var capacity = keepForGames ? 0 : Math.Max(0, gpu.VramGb - Math.Max(gpu.UsedGb, reserve));
        return Math.Round(capacity, 2);
    }

    /// <summary>The main memory the planner may use on <paramref name="spec"/> (a quarter, at least 4 GB, stays for the PC
    /// the user talks to; 15%, at least 2 GB, on other computers).</summary>
    public static double RamCapacityGb(MachineSpecs spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var reserve = spec.IsPrimary ? Math.Max(4, spec.RamGb * 0.25) : Math.Max(2, spec.RamGb * 0.15);
        return Math.Round(Math.Max(0, spec.RamGb - reserve), 2);
    }

    /// <summary>The processor threads the planner may use on <paramref name="spec"/> (before <see cref="CpuOversubscription"/>).</summary>
    public static double CpuCapacity(MachineSpecs spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        return Math.Max(spec.CpuThreads > 0 ? 1 : 0, spec.CpuThreads - (spec.IsPrimary ? 2 : 1));
    }

    /// <summary>The disk space the planner may use on <paramref name="spec"/>: 0 when its free space is unknown.</summary>
    public static double DiskCapacityGb(MachineSpecs spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        return spec.DiskFreeGb is { } disk ? Math.Max(0, disk - 10) : 0;
    }

    /// <summary>Whether <paramref name="option"/> can run on a card of <paramref name="vendor"/> and <paramref name="totalGb"/>.</summary>
    internal static bool GpuMatches(ComponentOption option, GpuVendor vendor, double totalGb) =>
        option.Gpu switch
        {
            GpuRequirement.Nvidia => vendor == GpuVendor.Nvidia,
            GpuRequirement.AnyGpu => true,
            _ => false
        } && totalGb >= option.MinGpuGb;

    private static string LocalGains(ComponentOption hosted, ComponentOption local)
    {
        var gains = new List<string>();
        if (local.FirstWordMs is { } l && hosted.FirstWordMs is { } h && l < h)
            gains.Add($"its first word comes sooner (about {Seconds(l)} instead of {Seconds(h)})");
        gains.Add("your conversation stays on your computers");
        if (hosted.Reliability != OptionReliability.High) gains.Add("it keeps working when the free endpoint is down or rate-limited");
        if (!hosted.FreeTier) gains.Add("it costs nothing per request");
        return gains.Count == 1 ? gains[0] : string.Join(", ", gains.Take(gains.Count - 1)) + " and " + gains[^1];
    }

    internal static string Seconds(int ms) => (ms / 1000d).ToString("0.##", CultureInfo.InvariantCulture) + " s";

    internal static string Gb(double gb) => gb.ToString("0.#", CultureInfo.InvariantCulture);

    private sealed class Card(int index, MachineGpu spec, double capacity)
    {
        public int Index { get; } = index;
        public MachineGpu Spec { get; } = spec;
        public double Capacity { get; } = capacity;
        public double Used { get; set; }
        public bool Exclusive { get; set; }
        public double Free => Capacity - Used;
    }

    private sealed class Node
    {
        public required MachineSpecs Spec { get; init; }
        public required List<Card> Cards { get; init; }
        public double RamCapacity { get; init; }
        public double CpuCapacity { get; init; }
        public double DiskCapacity { get; init; }
        public double RamUsed { get; set; }
        public double CpuUsed { get; set; }
        public double DiskUsed { get; set; }
        public List<UsageItem> Items { get; } = [];
    }

    private sealed class Planner
    {
        private readonly PlanRequest request;
        private readonly FootprintCatalog catalog;
        private readonly List<Node> nodes;
        private readonly List<Assignment> assignments = [];
        private readonly List<DroppedComponent> dropped = [];
        private readonly List<PlanSuggestion> suggestions = [];
        private readonly List<string> notes = [];

        public Planner(PlanRequest request, FootprintCatalog catalog)
        {
            this.request = request;
            this.catalog = catalog;
            nodes = request.Machines.Select(Build).ToList();
        }

        private HostingPreference Preference => request.Preference;

        private static Node Build(MachineSpecs spec)
        {
            var cards = spec.Gpus.Select((gpu, i) => new Card(i, gpu, GpuCapacityGb(gpu, spec.KeepGpuForGames))).ToList();
            return new Node
            {
                Spec = spec, Cards = cards, RamCapacity = RamCapacityGb(spec), CpuCapacity = CpuCapacity(spec),
                DiskCapacity = DiskCapacityGb(spec)
            };
        }

        public PlacementPlan Run()
        {
            foreach (var step in ComponentRanking.ClaimOrder(Preference))
            {
                switch (step)
                {
                    case PlanStep.Character: Simple(PlanComponent.Character); break;
                    case PlanStep.Voice: Simple(PlanComponent.Voice); break;
                    case PlanStep.Listening: Simple(PlanComponent.Listening, gpu: false); break;
                    case PlanStep.LipSync: Simple(PlanComponent.LipSync); break;
                    case PlanStep.ThinkingPrimary: ThinkingPrimary(); break;
                    case PlanStep.ThinkingFallback: ThinkingFallback(); break;
                    case PlanStep.ListeningUpgrade: ListeningUpgrade(); break;
                    case PlanStep.Vision: Sense(PlanComponent.Vision); break;
                    case PlanStep.Reading: InAppFirst(PlanComponent.Reading); break;
                    case PlanStep.Hearing: Sense(PlanComponent.Hearing); break;
                    case PlanStep.DeepThinking: Simple(PlanComponent.DeepThinking); break;
                    case PlanStep.SmartHome: Simple(PlanComponent.SmartHome); break;
                    case PlanStep.Singing: Simple(PlanComponent.Singing); break;
                    case PlanStep.Pictures: Simple(PlanComponent.Pictures); break;
                }
            }
            UpgradeSuggestions();
            AddNotes();
            var ordered = assignments.OrderBy(a => ComponentRanking.Of(a.Component).Rank).ThenBy(a => a.Role).ToArray();
            var droppedOrdered = dropped.OrderBy(d => ComponentRanking.Of(d.Component).Rank).ToArray();
            return new(Preference, ordered, nodes.Select(Usage).ToArray(), droppedOrdered, suggestions.ToArray(), notes.ToArray());
        }

        public PlacementPlan Measure()
        {
            var seen = new HashSet<PlanComponent>();
            foreach (var current in request.Current)
            {
                var option = catalog.Find(current.OptionId);
                if (option is null)
                {
                    notes.Add($"Unknown option '{current.OptionId}' for {ComponentRanking.Name(current.Component)} was skipped.");
                    continue;
                }
                var role = seen.Add(current.Component) ? AssignmentRole.Primary : AssignmentRole.Fallback;
                if (!option.IsLocal || option.UsesThinking)
                {
                    assignments.Add(new(current.Component, option, option.IsLocal ? current.MachineId : null, null, role, "Runs today."));
                    continue;
                }
                var node = nodes.FirstOrDefault(n => n.Spec.Id == current.MachineId);
                if (node is null)
                {
                    notes.Add($"{option.DisplayName} runs on '{current.MachineId}', which is not in the network; it was skipped.");
                    continue;
                }
                var card = option.UsesGpu
                    ? node.Cards.Where(c => GpuMatches(option, c.Spec.Vendor, c.Spec.VramGb)).OrderByDescending(c => c.Free).FirstOrDefault()
                      ?? node.Cards.OrderByDescending(c => c.Free).FirstOrDefault()
                    : null;
                var use = option.Reserve;
                var usual = option.Usual;
                if (card is null && option.UsesGpu)
                {
                    use = use with { RamGb = use.RamGb + use.VramGb, VramGb = 0 };
                    usual = usual with { RamGb = usual.RamGb + usual.VramGb, VramGb = 0 };
                }
                if (card is not null)
                {
                    card.Used += use.VramGb;
                    if (card.Spec.UnifiedMemory) node.RamUsed += use.VramGb;
                }
                node.RamUsed += use.RamGb;
                node.CpuUsed += use.CpuThreads;
                node.DiskUsed += use.DiskGb;
                node.Items.Add(new(option.Component, option.Id, card?.Index, use) { Usual = usual });
                assignments.Add(new(current.Component, option, node.Spec.Id, card?.Index, role, "Runs today."));
            }
            foreach (var info in ComponentRanking.All.Where(i => !seen.Contains(i.Component)))
                dropped.Add(new(info.Component, request.Wants(info.Component) ? DropReason.NotSetUp : DropReason.NotWanted,
                    $"{info.Name} is not set up."));
            return new(Preference, assignments.ToArray(), nodes.Select(Usage).ToArray(), dropped.ToArray(), [], notes.ToArray());
        }

        private static MachineUsage Usage(Node node) => new(node.Spec.Id, node.Spec.Name, node.Spec.Platform, node.Spec.IsPrimary,
            node.Cards.Select(c => new GpuUsage(c.Index, c.Spec.Name, c.Spec.Vendor, c.Spec.VramGb,
                new(c.Capacity, Math.Round(c.Used, 2)), c.Spec.UnifiedMemory)).ToArray(),
            new(node.RamCapacity, Math.Round(node.RamUsed, 2)), new(node.CpuCapacity, Math.Round(node.CpuUsed, 2)),
            new(node.DiskCapacity, Math.Round(node.DiskUsed, 2)), node.Items.ToArray());

        private bool Configured(ComponentOption option) =>
            option.ProviderId is { } provider && request.ConfiguredProviders.Contains(provider, StringComparer.OrdinalIgnoreCase);

        /// <summary>Whether a hosted option may be planned: never when the user keeps everything local; configured providers
        /// always; free sign-up providers for Thinking and Deep thinking (the user can get a key for free).</summary>
        private bool ExternalAllowed(ComponentOption option) =>
            !option.IsLocal && Preference != HostingPreference.PreferLocal &&
            (Configured(option) || option.FreeTier && option.Component is PlanComponent.Thinking or PlanComponent.DeepThinking);

        private static int ReliabilityPenalty(ComponentOption option) => option.Reliability switch
        {
            OptionReliability.Low => 10,
            OptionReliability.Medium => 5,
            _ => 0
        };

        private int Score(ComponentOption option)
        {
            var score = option.QualityTier * 10 - ReliabilityPenalty(option);
            if (option.IsLocal)
            {
                score += option.Component is PlanComponent.Voice or PlanComponent.Listening or PlanComponent.LipSync or PlanComponent.Character
                    or PlanComponent.Vision or PlanComponent.Reading or PlanComponent.Hearing
                    ? LocalAudioBonus
                    : Preference == HostingPreference.PreferHosted ? 0 : 5;
            }
            else if (!Configured(option)) score -= 3;
            return score;
        }

        // Placement on machines.

        private (Node Node, Card? Card)? FindSlot(ComponentOption option)
        {
            var currentMachine = request.Current.FirstOrDefault(c => c.Component == option.Component && c.OptionId == option.Id)?.MachineId;
            (Node, Card?)? best = null;
            var bestKey = (0, 0, 0, 0d);
            foreach (var node in nodes)
            {
                if (!option.RunsOn(node.Spec.Platform) || option.RunsInApp && !node.Spec.IsPrimary) continue;
                if (node.Spec.Platform == "macos" && option.Gpu == GpuRequirement.Nvidia) continue;
                var ramNeed = option.Peak.RamGb;
                Card? card = null;
                if (option.UsesGpu)
                {
                    card = node.Cards.Where(c => GpuMatches(option, c.Spec.Vendor, c.Spec.VramGb) && c.Free >= option.GpuGb &&
                            !c.Exclusive && (option.CanShareGpu || c.Used == 0))
                        .OrderBy(c => c.Free).FirstOrDefault();
                    if (card is null) continue;
                    if (card.Spec.UnifiedMemory) ramNeed += option.GpuGb;
                }
                if (node.RamCapacity - node.RamUsed < ramNeed) continue;
                if (node.CpuCapacity * CpuOversubscription - node.CpuUsed < option.Steady.CpuThreads) continue;
                if (node.DiskCapacity > 0 && node.DiskCapacity - node.DiskUsed < option.Peak.DiskGb) continue;
                // Keep what runs where it runs; prefer always-on machines, then hosts over the PC the user sits at (its
                // games and desktop stay smooth), then the tightest fit so bigger cards stay free.
                var key = (node.Spec.Id == currentMachine ? 0 : 1, node.Spec.OnBattery ? 1 : 0, node.Spec.IsPrimary ? 1 : 0,
                    card is null ? -(node.CpuCapacity - node.CpuUsed) : card.Free);
                if (best is null || key.CompareTo(bestKey) < 0)
                {
                    best = (node, card);
                    bestKey = key;
                }
            }
            return best;
        }

        private bool Fits(ComponentOption option) => !option.IsLocal || FindSlot(option) is not null;

        private Assignment Place(ComponentOption option, AssignmentRole role, string why)
        {
            Assignment assignment;
            if (!option.IsLocal) assignment = new(option.Component, option, null, null, role, why);
            else
            {
                var (node, card) = FindSlot(option) ?? throw new InvalidOperationException($"{option.Id} does not fit.");
                var use = option.Reserve;
                if (card is not null)
                {
                    card.Used += option.GpuGb;
                    if (!option.CanShareGpu) card.Exclusive = true;
                    if (card.Spec.UnifiedMemory) node.RamUsed += option.GpuGb;
                }
                node.RamUsed += use.RamGb;
                node.CpuUsed += use.CpuThreads;
                node.DiskUsed += use.DiskGb;
                node.Items.Add(new(option.Component, option.Id, card?.Index, use) { Usual = option.Usual });
                assignment = new(option.Component, option, node.Spec.Id, card?.Index, role, why);
            }
            assignments.Add(assignment);
            return assignment;
        }

        private void Unplace(Assignment assignment)
        {
            assignments.Remove(assignment);
            if (assignment.MachineId is null || assignment.Option.UsesThinking) return;
            var node = nodes.First(n => n.Spec.Id == assignment.MachineId);
            var item = node.Items.First(i => i.Component == assignment.Component && i.OptionId == assignment.Option.Id);
            node.Items.Remove(item);
            node.RamUsed -= item.Use.RamGb;
            node.CpuUsed -= item.Use.CpuThreads;
            node.DiskUsed -= item.Use.DiskGb;
            if (item.GpuIndex is { } index)
            {
                var card = node.Cards[index];
                card.Used -= item.Use.VramGb;
                if (card.Spec.UnifiedMemory) node.RamUsed -= item.Use.VramGb;
                if (!assignment.Option.CanShareGpu) card.Exclusive = false;
            }
        }

        private string MachineName(string? id) => id is null ? "a hosted provider" : nodes.First(n => n.Spec.Id == id).Spec.Name;

        private string WhereText(ComponentOption option) => option.IsLocal
            ? option.UsesGpu ? "on the graphics card" : "on the processor"
            : $"hosted by {option.DisplayName}";

        // Steps.

        private void Simple(PlanComponent component, bool gpu = true)
        {
            var info = ComponentRanking.Of(component);
            if (!request.Wants(component))
            {
                dropped.Add(new(component, DropReason.NotWanted, $"{info.Name} is turned off."));
                return;
            }
            var candidates = catalog.For(component)
                .Where(o => !o.UsesThinking && (o.IsLocal ? gpu || !o.UsesGpu : ExternalAllowed(o)))
                .OrderByDescending(Score).ThenBy(o => o.IsLocal ? 0 : 1).ThenBy(o => o.GpuGb).ToList();
            var chosen = candidates.FirstOrDefault(Fits);
            if (chosen is null)
            {
                dropped.Add(new(component, DropReasonFor(component), WhyNothingFits(component)));
                return;
            }
            var better = candidates.TakeWhile(o => o != chosen).FirstOrDefault(o => o.IsLocal);
            Place(chosen, AssignmentRole.Primary, Why(chosen, better));
            if (!chosen.IsLocal && chosen.NeedsSignup && !Configured(chosen)) SignUp(chosen);
        }

        /// <summary>Vision or Hearing: Thinking's own model when it sees (or hears), so nothing more runs and it takes no room;
        /// otherwise an image or audio model of its own, as <see cref="Simple"/> picks one.</summary>
        private void Sense(PlanComponent component)
        {
            if (!request.Wants(component))
            {
                Simple(component);
                return;
            }
            var thinking = assignments.FirstOrDefault(a => a.Component == PlanComponent.Thinking && a.Role == AssignmentRole.Primary);
            var vision = component == PlanComponent.Vision;
            if (thinking is not null && (vision ? thinking.Option.SeesImages : thinking.Option.HearsAudio) &&
                catalog.For(component).FirstOrDefault(o => o.UsesThinking) is { } itself)
            {
                assignments.Add(new(component, itself, thinking.MachineId, thinking.GpuIndex, AssignmentRole.Primary,
                    $"{thinking.Option.DisplayName}, Thinking's own model, {(vision ? "sees" : "hears")} itself, so nothing more runs."));
                return;
            }
            Simple(component);
        }

        /// <summary>Reading: inside Martlet on the PC you talk to first (Windows OCR: fast, free and nothing leaves the PC), else
        /// as <see cref="Simple"/> picks (the Reading role on a processor).</summary>
        private void InAppFirst(PlanComponent component)
        {
            if (request.Wants(component) &&
                catalog.For(component).FirstOrDefault(o => o.IsLocal && o.RunsInApp && Fits(o)) is { } inApp)
            {
                Place(inApp, AssignmentRole.Primary, $"{inApp.DisplayName} inside Martlet on the processor: fast, free, and nothing leaves this PC.");
                return;
            }
            Simple(component);
        }

        private DropReason DropReasonFor(PlanComponent component)
        {
            var local = catalog.For(component).Where(o => o.IsLocal && !o.UsesThinking).ToList();
            if (local.Count > 0 && local.All(o => o.Gpu == GpuRequirement.Nvidia) && !nodes.Any(n => n.Spec.HasNvidia && !n.Spec.KeepGpuForGames))
                return DropReason.NeedsNvidia;
            if (Preference == HostingPreference.PreferLocal && catalog.For(component).Any(o => !o.IsLocal)) return DropReason.KeptLocal;
            return local.Count == 0 ? DropReason.NoProvider : DropReason.NoRoom;
        }

        private string WhyNothingFits(PlanComponent component)
        {
            var name = ComponentRanking.Name(component);
            var smallest = catalog.For(component).Where(o => o.IsLocal && !o.UsesThinking).OrderBy(o => o.GpuGb).ThenBy(o => o.Peak.RamGb).FirstOrDefault();
            var need = smallest is null ? "" : smallest.UsesGpu
                ? $" {smallest.DisplayName} needs {(smallest.Gpu == GpuRequirement.Nvidia ? "an NVIDIA" : "a")} graphics card with about {Gb(smallest.GpuGb)} GB free."
                : $" {smallest.DisplayName} needs about {Gb(smallest.Peak.RamGb)} GB of free memory.";
            return DropReasonFor(component) switch
            {
                DropReason.KeptLocal => $"{name} has no room on your computers, and you chose to keep everything local.{need}",
                DropReason.NeedsNvidia => $"{name} needs an NVIDIA graphics card that is free for Martlet.{need}",
                _ => $"No computer has room for {name.ToLowerInvariant()} after the more important parts.{need}"
            };
        }

        private string Why(ComponentOption chosen, ComponentOption? betterLocal)
        {
            var text = chosen.IsLocal
                ? $"{chosen.DisplayName} {WhereText(chosen)}" + (chosen.FirstWordMs is { } ms ? $" (first word in about {Seconds(ms)})." : ".")
                : $"{chosen.DisplayName}: no local option that fits is as good.";
            if (betterLocal is not null)
                text += $" {betterLocal.DisplayName} would be better but has no room" +
                    (betterLocal.UsesGpu ? $" (needs about {Gb(betterLocal.GpuGb)} GB free on {(betterLocal.Gpu == GpuRequirement.Nvidia ? "an NVIDIA" : "a")} graphics card)." : ".");
            return text;
        }

        private void SignUp(ComponentOption option)
        {
            if (suggestions.Any(s => s.Kind == SuggestionKind.SignUp && s.ToOptionId == option.Id)) return;
            suggestions.Add(new(SuggestionKind.SignUp, option.Component, null, null, option.Id,
                $"Sign up for {option.DisplayName} (free) and save its API key in Companion > Thinking."));
        }

        private IEnumerable<ComponentOption> LocalThinking(bool hearingOnly) => catalog.For(PlanComponent.Thinking)
            .Where(o => o.IsLocal && (!hearingOnly || o.HearsAudio))
            .OrderBy(o => o.UsesGpu ? 0 : 1).ThenByDescending(o => o.HearsAudio).ThenBy(o => o.FirstWordMs ?? int.MaxValue);

        private IEnumerable<ComponentOption> HostedThinking() => catalog.For(PlanComponent.Thinking)
            .Where(ExternalAllowed)
            .OrderByDescending(o => (Configured(o) ? 20 : 0) + (o.HearsAudio ? 10 : 0) + o.QualityTier * 10 - ReliabilityPenalty(o) * 2)
            .ThenBy(o => o.FirstWordMs ?? int.MaxValue);

        private void ThinkingPrimary()
        {
            if (!request.Wants(PlanComponent.Thinking))
            {
                dropped.Add(new(PlanComponent.Thinking, DropReason.NotWanted, "Thinking is turned off."));
                return;
            }
            // The fastest local model that hears, on a graphics card: about 0.15 s to its first sentence, private and always up.
            var localGpu = LocalThinking(hearingOnly: false).Where(o => o.UsesGpu).FirstOrDefault(Fits);
            var hosted = HostedThinking().FirstOrDefault();
            var localCpu = LocalThinking(hearingOnly: false).Where(o => !o.UsesGpu).FirstOrDefault(Fits);
            ComponentOption? chosen;
            string why;
            if (Preference == HostingPreference.PreferHosted && hosted is not null)
            {
                chosen = hosted;
                why = $"{hosted.DisplayName}: you're happy with hosted endpoints, so local hardware goes to the voice and face.";
            }
            else if (localGpu is not null)
            {
                chosen = localGpu;
                why = $"{localGpu.DisplayName} on the graphics card: the fastest first word (about {Seconds(localGpu.FirstWordMs ?? 0)}), " +
                    "private, and it keeps working without the internet." + (localGpu.HearsAudio ? " It hears your voice itself." : "");
            }
            else if (hosted is not null)
            {
                chosen = hosted;
                why = $"{hosted.DisplayName}: no graphics card has room for a local model after the voice" +
                    (request.Wants(PlanComponent.LipSync) ? " and lip-sync" : "") + ", and a hosted endpoint answers well for free.";
            }
            else if (localCpu is not null)
            {
                chosen = localCpu;
                why = $"{localCpu.DisplayName}: no graphics card has room" +
                    (Preference == HostingPreference.PreferLocal ? " and you keep everything local" : "") +
                    $", so it runs on the processor; replies start slowly (about {Seconds(localCpu.FirstWordMs ?? 0)}).";
            }
            else
            {
                var lack = !catalog.For(PlanComponent.Thinking).Any(o => !o.IsLocal && Configured(o)) ? "no free API key is saved"
                    : Preference == HostingPreference.PreferLocal ? "you keep everything on your computers"
                    : "no hosted provider is allowed";
                dropped.Add(new(PlanComponent.Thinking, Preference == HostingPreference.PreferLocal ? DropReason.KeptLocal : DropReason.NoRoom,
                    $"No computer has room for a Thinking model, and {lack}. Martlet can't reply until one is set up."));
                return;
            }
            Place(chosen, AssignmentRole.Primary, why);
            if (!chosen.IsLocal && chosen.NeedsSignup && !Configured(chosen)) SignUp(chosen);
        }

        private void ThinkingFallback()
        {
            var primary = assignments.FirstOrDefault(a => a.Component == PlanComponent.Thinking);
            if (primary is null || primary.Option.IsLocal || primary.Option.Reliability == OptionReliability.High) return;
            var local = LocalThinking(hearingOnly: false).FirstOrDefault(Fits);
            if (local is not null)
            {
                Place(local, AssignmentRole.Fallback,
                    $"{local.DisplayName} {WhereText(local)} takes over when {primary.Option.DisplayName} is down or rate-limited" +
                    (local.UsesGpu ? "." : $" (slower, about {Seconds(local.FirstWordMs ?? 0)} to the first word)."));
                return;
            }
            var other = HostedThinking().FirstOrDefault(o => o.ProviderId != primary.Option.ProviderId);
            if (other is not null)
            {
                Place(other, AssignmentRole.Fallback, $"{other.DisplayName} takes over when {primary.Option.DisplayName} is down or rate-limited.");
                if (other.NeedsSignup && !Configured(other)) SignUp(other);
                return;
            }
            notes.Add($"Nothing can stand in when {primary.Option.DisplayName} is down: Martlet can't reply until it is back.");
        }

        private void ListeningUpgrade()
        {
            var current = assignments.FirstOrDefault(a => a.Component == PlanComponent.Listening && a.Role == AssignmentRole.Primary);
            if (current is null || !current.Option.IsLocal || current.Option.UsesGpu) return;
            var upgrade = catalog.For(PlanComponent.Listening)
                .Where(o => o.UsesGpu && o.QualityTier > current.Option.QualityTier)
                .OrderByDescending(o => o.QualityTier).ThenBy(o => o.GpuGb).FirstOrDefault(Fits);
            if (upgrade is null) return;
            Unplace(current);
            Place(upgrade, AssignmentRole.Primary,
                $"{upgrade.DisplayName}: a card had room left after the more important parts, and it frees the processor.");
        }

        private void UpgradeSuggestions()
        {
            foreach (var assignment in assignments.Where(a => a.Role == AssignmentRole.Primary).ToList())
            {
                var option = assignment.Option;
                // Thinking's own model sees or hears: a model of its own would only add work.
                if (option.UsesThinking) continue;
                // Thinking: a smarter local model that hears, when it fits beside everything else in place of the current one.
                var better = catalog.For(assignment.Component)
                    .Where(o => o.IsLocal && !o.UsesThinking && o.QualityTier > option.QualityTier &&
                        (assignment.Component != PlanComponent.Thinking || o.HearsAudio || !option.HearsAudio))
                    .OrderBy(o => o.QualityTier).ToList();
                if (better.Count == 0) continue;
                Unplace(assignment);
                var fits = better.Where(Fits).OrderByDescending(o => o.QualityTier).FirstOrDefault();
                var restored = Place(option, assignment.Role, assignment.Why);
                assignments.Remove(restored);
                assignments.Insert(0, restored);
                if (fits is not null && assignment.Component is PlanComponent.Thinking or PlanComponent.DeepThinking)
                    suggestions.Add(new(SuggestionKind.Upgrade, assignment.Component, MachineOf(fits), option.Id, fits.Id,
                        $"{fits.DisplayName} also fits: smarter than {option.DisplayName}" +
                        (fits.FirstWordMs is { } ms && option.FirstWordMs is { } now && ms > now
                            ? $", but its first word comes later (about {Seconds(ms)} instead of {Seconds(now)})." : ".")));
                else if (fits is null)
                {
                    var next = better[0];
                    suggestions.Add(new(SuggestionKind.Upgrade, assignment.Component, null, option.Id, next.Id,
                        $"{next.DisplayName} would be better than {option.DisplayName}; it needs " +
                        (next.UsesGpu
                            ? $"{(next.Gpu == GpuRequirement.Nvidia ? "an NVIDIA" : "a")} graphics card with about {Gb(next.GpuGb)} GB free."
                            : $"about {Gb(next.Peak.RamGb)} GB of free memory.")));
                }
            }
        }

        private string? MachineOf(ComponentOption option) => FindSlot(option)?.Node.Spec.Id;

        private void AddNotes()
        {
            var primary = assignments.FirstOrDefault(a => a.Component == PlanComponent.Thinking && a.Role == AssignmentRole.Primary);
            if (primary is { IsExternal: true } && primary.Option.Reliability != OptionReliability.High)
                notes.Add($"{primary.Option.DisplayName} is free but can be slow, rate-limited or retire its model without notice.");
            if (nodes.Any(n => n.Spec.KeepGpuForGames && n.Spec.Gpus.Count > 0))
                notes.Add("Graphics cards kept for games are left alone; Martlet uses only the processor there.");
            if (nodes.Any(n => n.Cards.Any(c => c.Spec.UnifiedMemory)))
                notes.Add("Apple Silicon shares memory between the processor and graphics; Martlet counts both against main memory.");
            if (nodes.Any(n => n.Spec.Gpus.Any(g => g.Vendor is GpuVendor.Amd or GpuVendor.Intel)))
                notes.Add("AMD and Intel graphics cards can run Thinking models; voice engines and advanced lip-sync need NVIDIA.");
            if (nodes.Any(n => n.Spec.OnBattery && n.Items.Count > 0 && !n.Spec.IsPrimary))
                notes.Add("A computer on battery runs some parts; they stop when it sleeps.");
            foreach (var node in nodes.Where(n => n.Spec.Platform == "windows"))
                foreach (var card in node.Cards)
                {
                    var onCard = node.Items.Where(i => i.GpuIndex == card.Index).ToList();
                    if (onCard.Any(i => i.Component == PlanComponent.Voice) && onCard.Count > 1)
                        notes.Add($"{node.Spec.Name} runs on Windows, so its voice shares the graphics card with " +
                            string.Join(", ", onCard.Where(i => i.Component != PlanComponent.Voice).Select(i => ComponentRanking.Name(i.Component).ToLowerInvariant())) +
                            "; if the card's memory runs short the voice can start late.");
                }
            notes.Add("Sizes are planning estimates (docs/RESOURCE_FOOTPRINTS.md).");
        }
    }
}
