using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Martlet.Core.Cluster;

namespace Martlet.Core.Planning;

/// <summary>
/// Recommends the setup for all the owner's computers (Home's Recommended setup) from the network's computers, their
/// hardware and today's setup. Pure and deterministic: it reads nothing and contacts nothing, and the same request always
/// gives the same recommendation. docs/RECOMMENDED_SETUPS.md#recommended-setup-for-all-your-computers explains the rules:
/// <list type="number">
/// <item>One language model per graphics card: Thinking's Ollama (host role "ollama") and a Thinking pool model
/// ("deep-thinking") never share a card, and a host runs at most one of each. On a host with two or more NVIDIA cards each
/// role is pinned to its own card (deploy/host/README.md, choice.gpu and MARTLET_GPU; docs/CLUSTER.md, "Live turn first on a
/// shared graphics card").</item>
/// <item>One voice engine per card and per host (role.conf exclusive=voice; docs/VOICE_LATENCY.md, "Keep one voice engine per
/// GPU"), and the whole network uses the owner's voice engine (Chatterbox Nano, on a card or the processor, while no computer
/// has room for it), because Speaking's pool needs the same engine on every
/// computer (docs/CLUSTER.md, "Sharing work between your computers").</item>
/// <item>On a Windows host (Martlet.Core.Installation.SharedGpu.OnWindows) the voice gets a card of its own when another
/// place exists: Windows pages a full card's memory into main memory and the voice starts late
/// (docs/CHATTERBOX_VOICE.md#sharing-the-graphics-card).</item>
/// <item>Every card keeps headroom (<see cref="PlacementEngine.GpuCapacityGb"/>: <see cref="PlacementEngine.GpuReserveGb"/> or
/// 10%), and nothing is planned over capacity: every language model stays in graphics memory (catalog peak plus context).</item>
/// <item>Live lane before pool lane: Deep thinking goes on a card that no live role (thinking, listening, voice, lip-sync)
/// uses when one exists. Needed jobs before optional extras (<see cref="ComponentRanking"/>): Thinking, then the voice,
/// listening and lip-sync, each on a graphics card first and on the processor when no card has room; singing and pictures
/// keep only the room those leave, and new roles nothing needs never push them out.</item>
/// <item>Companion PCs stay light (they often run games): no host roles there while a host can take the work. Only what no
/// host can run and Martlet needs (Thinking without a hosted provider, the owner's voice engine) goes to the companion PC with
/// the most free hardware, and a companion PC's graphics card only ever takes Thinking and the voice
/// (<see cref="ComponentRanking.UsesCompanionCard"/>); everything else runs there on the processor or is off. A network of one
/// companion PC alone (computers that aren't answering don't count) does those itself: Thinking on its card first, then the
/// voice engine if it fits (else Chatterbox Nano), listening in the app and lip-sync by the voice's loudness.</item>
/// <item>Never add conversation latency (AGENTS.md): a live job never moves to an option with a later first word
/// (<see cref="ComponentOption.FirstWordMs"/>) or to a busier card than today's, unless its computer stays away or its card
/// is too full. New jobs get the fastest options (a small model that hears on an idle card; docs/RECOMMENDED_SETUPS.md).</item>
/// <item>The owner's choices stand: the hosting preference, a hosted Thinking provider (unless they keep everything local),
/// the voice engine, loudness lip-sync. The recommender changes where things run and adds pool places, not what runs.</item>
/// <item>Pools after the primaries: the voice engine and listening on more hosts (useful up to the number of companion PCs),
/// then Deep thinking on every host with a card free (the Thinking pool; more is better; never on a host the owner left out).
/// Thinking's own job has no pool (its prompt cache; docs/CLUSTER.md).</item>
/// <item>A computer that isn't answering (<see cref="NetworkMachine.Online"/> false) is not part of the network: the
/// recommendation plans without it, changes nothing there, and its jobs and pool places move (Required). Martlet's presence
/// notices decide when a computer that stays away is worth a new automatic check (docs/CLUSTER.md).</item>
/// <item>Stability: what runs stays where it runs unless the gain matters; tidy-ups are Minor; today's setup equal to the
/// recommended one gives no changes. Ties break by computer id. <see cref="NetworkRecommendation.Fingerprint"/> hashes the
/// sorted target.</item>
/// <item>Off is a state: the owner can turn off the parts <see cref="ComponentRanking.CanBeOff"/> allows
/// (<see cref="NetworkSetupRequest.Off"/>), and their roles go. The parts this PC sets on their Companion pages (Vision,
/// Reading, Hearing, Smart home; <see cref="ComponentRanking.SetOnPage"/>) are off by <see cref="NetworkSetupRequest.Choices"/>:
/// Reading's role goes when no other companion PC may use it, and Home Assistant always stays (it runs the owner's home).
/// <see cref="NetworkRecommendation.Components"/> lists every part in priority order with where it runs or that it is off.</item>
/// <item>Setup order: the changes follow the priority list, Thinking first (make before break within each job); see
/// <c>SetupOrder</c>.</item>
/// </list>
/// </summary>
public static partial class NetworkRecommender
{
    /// <summary>The host role kinds the recommender places, besides the voice engines.</summary>
    public const string ThinkingRole = "ollama", DeepThinkingRole = "deep-thinking", ListeningRole = "stt", LipSyncRole = "audio2face";

    /// <summary>The host role kinds of the optional extras, which get only the room the needed jobs leave: singing, pictures
    /// and Reading (RapidOCR on a processor).</summary>
    private const string SingingRole = "singing", PicturesRole = "pictures", ReadingRole = "ocr";

    /// <summary>Home Assistant (Smart home): the recommender counts it where it runs and never moves or removes it, because it
    /// runs the owner's home.</summary>
    private const string SmartHomeRole = "home-assistant";

    private const string SpeakingPool = "speaking-pool", ListeningPool = "listening-pool", ThinkingPoolPlace = "thinking-pool", Kept = "kept";
    private const double Epsilon = 1e-6;

    /// <summary>The recommended setup for <paramref name="request"/>'s computers and the changes that get there from today's.</summary>
    public static NetworkRecommendation Recommend(NetworkSetupRequest request, FootprintCatalog? catalog = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new Planner(request, catalog ?? FootprintCatalog.Default).Run();
    }

    /// <summary>Today's setup as <paramref name="request"/> describes it: each computer's roles and how full they make it,
    /// who does each job, and the Thinking pool.</summary>
    public static NetworkSetup Today(NetworkSetupRequest request, FootprintCatalog? catalog = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new Planner(request, catalog ?? FootprintCatalog.Default).Current;
    }

    /// <summary>A stable hash of <paramref name="setup"/>'s computers, roles, jobs, pools and Thinking pool (sorted; reasons
    /// and usage left out), so the same recommended setup always has the same fingerprint.</summary>
    public static string FingerprintOf(NetworkSetup setup)
    {
        ArgumentNullException.ThrowIfNull(setup);
        var text = new StringBuilder();
        foreach (var machine in setup.Machines.OrderBy(m => m.MachineId, StringComparer.Ordinal))
        {
            text.Append("m|").Append(machine.MachineId).Append('|').Append(machine.Kind).Append('\n');
            foreach (var role in machine.Roles.OrderBy(r => r.Kind, StringComparer.Ordinal).ThenBy(r => r.Model, StringComparer.OrdinalIgnoreCase))
                text.Append("r|").Append(role.Kind).Append('|').Append(role.Model?.ToLowerInvariant()).Append('|')
                    .Append(role.GpuIndex?.ToString(CultureInfo.InvariantCulture)).Append('\n');
        }
        foreach (var job in setup.Jobs.OrderBy(j => j.Job, StringComparer.Ordinal))
            text.Append("j|").Append(job.Job).Append('|').Append(job.HostId).Append('|').Append(job.Off).Append('|').Append(job.OptionId)
                .Append('|').Append(string.Join(',', job.Pool.Order(StringComparer.Ordinal))).Append('\n');
        text.Append("t|").Append(string.Join(',', setup.ThinkingPool.Order(StringComparer.Ordinal)));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())))[..16];
    }

    private enum Presence { Here, Gone }

    private enum Problem { None, NoRoom, SharesModel, SharesVoice }

    /// <summary>One role on a computer: today's (pending until a step keeps it) or the recommended one.</summary>
    private sealed class Role
    {
        public required string Kind { get; init; }
        public string? Model { get; init; }
        public ComponentOption? Option { get; init; }
        /// <summary>The card it runs on (an index into the computer's Gpus), or null for the processor.</summary>
        public int? Card { get; set; }
        /// <summary>Thinking in Ollama inside a companion PC itself (not a host role).</summary>
        public bool Native { get; init; }
        /// <summary>Not the recommender's to change: a role it doesn't manage, or on a computer that isn't answering.</summary>
        public bool Fixed { get; init; }
        /// <summary>Today's role it continues (null: new).</summary>
        public HostedRolePlacement? Was { get; init; }
        public string Purpose { get; set; } = Kept;
        public SetupChangeBenefit Benefit { get; set; } = SetupChangeBenefit.Minor;
        public string Why { get; set; } = "";
        /// <summary>Why it goes, when a step decided that it goes.</summary>
        public (SetupChangeBenefit Benefit, string Why)? Leave { get; set; }
        /// <summary>Why it goes if nothing else keeps it: its job moved to a host, but a pool may still keep it.</summary>
        public (SetupChangeBenefit Benefit, string Why)? Moved { get; set; }
        public bool IsModel => Kind is ThinkingRole or DeepThinkingRole;
        public double Gb => Card is not null && Option is { UsesGpu: true } option ? option.GpuGb : 0;
    }

    private sealed class Node
    {
        public required NetworkMachine Machine { get; init; }
        public required Presence Presence { get; init; }
        /// <summary>What the planner may use on each card (<see cref="PlacementEngine.GpuCapacityGb"/>).</summary>
        public required double[] Capacity { get; init; }
        /// <summary>Two or more NVIDIA cards: each role can be pinned to one (choice.gpu).</summary>
        public required bool Pinnable { get; init; }
        public required int[] Nvidia { get; init; }
        /// <summary>The card roles use on a computer that can't pin them: its biggest NVIDIA card, else its biggest card.</summary>
        public required int? MainCard { get; init; }
        public required double RamCapacity { get; init; }
        public required double CpuCapacity { get; init; }
        public required double DiskCapacity { get; init; }
        /// <summary>The recommended roles so far.</summary>
        public List<Role> Roles { get; } = [];
        /// <summary>Today's managed roles no step has kept yet.</summary>
        public List<Role> Pending { get; } = [];
        /// <summary>Today's roles, as they run now.</summary>
        public List<Role> Today { get; } = [];

        public MachineSpecs Spec => Machine.Specs;
        public string Id => Machine.Specs.Id;
        public string Name => string.IsNullOrWhiteSpace(Machine.Specs.Name) ? Machine.Specs.Id : Machine.Specs.Name;
        public bool Companion => Machine.Kind == NetworkMachineKind.Companion;
        public bool CanHost => Machine.Kind == NetworkMachineKind.Host || Machine.HasHostService;
        public bool OnWindows => Machine.OnWindows ?? string.Equals(Spec.Platform, "windows", StringComparison.OrdinalIgnoreCase);
        public double Free(int card) => Capacity[card] - Roles.Where(r => r.Card == card).Sum(r => r.Gb);
        public double RamUsed => Roles.Sum(r => r.Option is { IsLocal: true } option
            ? option.Peak.RamGb + (r.Card is { } card && Spec.Gpus[card].UnifiedMemory ? r.Gb : 0) : 0);
        public double CpuUsed => Roles.Sum(r => r.Option is { IsLocal: true } option ? option.Steady.CpuThreads : 0);
        public double DiskUsed => Roles.Where(r => r.Was is null).Sum(r => r.Option?.Peak.DiskGb ?? 0);
        /// <summary>The live jobs and pool places it has, for "least loaded first".</summary>
        public int Load => Roles.Count(r => !r.Fixed && r.Purpose != Kept);

        /// <summary>The cards <paramref name="option"/> may run on here.</summary>
        public IEnumerable<int> Cards(ComponentOption option) =>
            (Pinnable ? Nvidia : MainCard is { } main ? [main] : [])
            .Where(i => PlacementEngine.GpuMatches(option, Spec.Gpus[i].Vendor, Spec.Gpus[i].VramGb));
    }

    /// <summary>Who does one job in the recommended setup.</summary>
    private sealed class Decision
    {
        public required string Job { get; init; }
        public string? HostId { get; set; }
        public bool Off { get; set; }
        public string? OptionId { get; set; }
        public List<string> Pool { get; } = [];
        public string Why { get; set; } = "";
        public SetupChangeBenefit Benefit { get; set; } = SetupChangeBenefit.Minor;
        /// <summary>Kept exactly as today: the part isn't wanted, or its computer isn't answering or isn't known.</summary>
        public bool Frozen { get; set; }
    }

    private sealed record Slot(Node Node, int? Card, ComponentOption Option, bool Native);

    /// <summary>Where a step may put a role.</summary>
    private sealed record Query(string Kind)
    {
        /// <summary>Thinking in Ollama inside the companion PC itself.</summary>
        public bool Native { get; init; }
        public bool Hosts { get; init; } = true;
        public bool Companions { get; init; }
        /// <summary>Only this computer.</summary>
        public string? Only { get; init; }
        /// <summary>A new role nothing needs: only on computers Martlet can change from here.</summary>
        public bool Optional { get; init; }
        /// <summary>Rule 7: no option with a later first word.</summary>
        public int? MaxMs { get; init; }
        /// <summary>Rule 7: no card busier than today's.</summary>
        public int? MaxContention { get; init; }
        /// <summary>Rule 3: never a voice beside other roles, or other roles beside a voice, on a Windows card.</summary>
        public bool StrictWindows { get; init; }
        public bool LeastLoaded { get; init; }
        /// <summary>The card with the most room left (the companion PC with the most free hardware).</summary>
        public bool MostRoom { get; init; }
        /// <summary>A computer to prefer when all else is equal (where the job runs today).</summary>
        public string? Prefer { get; init; }
        /// <summary>Today's optional extras (singing, pictures) keep their room against this query: it places a role nothing
        /// needs, or it moves a job only to make it better (rule 7). A job Martlet needs to talk takes their room.</summary>
        public bool SparesExtras => Optional || MaxContention is not null;
    }

    private static bool Same(string? a, string? b) => a is not null && b is not null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static string Title(string job) => job switch
    {
        ClusterJobs.Thinking => "Thinking",
        ClusterJobs.Speaking => "Speaking",
        ClusterJobs.Listening => "Listening",
        ClusterJobs.LipSync => "Lip-sync",
        _ => job
    };

    private static string Lower(string job) => Title(job).ToLowerInvariant();

    private static PlanComponent ComponentOf(string job) => job switch
    {
        ClusterJobs.Thinking => PlanComponent.Thinking,
        ClusterJobs.Speaking => PlanComponent.Voice,
        ClusterJobs.Listening => PlanComponent.Listening,
        _ => PlanComponent.LipSync
    };

    /// <summary>An option's name without where it runs ("Whisper large-v3 turbo").</summary>
    private static string Plain(ComponentOption option) =>
        option.DisplayName.Replace(" on the graphics card", "", StringComparison.Ordinal).Replace(" on the processor", "", StringComparison.Ordinal);

    /// <summary>An option's name without where it runs, with the job for a host role whose name doesn't say it
    /// ("Gemma 4 E2B (Thinking)", "Whisper large-v3 turbo (Listening)").</summary>
    private static string Label(ComponentOption? option, string kind = "")
    {
        if (option is null) return kind;
        var name = Plain(option);
        return option.HostRoleKind switch
        {
            ThinkingRole when option.Component == PlanComponent.Thinking => name + " (Thinking)",
            ListeningRole => name + " (Listening)",
            _ => name
        };
    }

    private static string Seconds(int ms) => PlacementEngine.Seconds(ms);

    private static string Gb(double gb) => PlacementEngine.Gb(gb);

    private static string FirstWord(ComponentOption? option) =>
        option?.FirstWordMs is { } ms ? $" (first word in about {Seconds(ms)})" : "";

    /// <summary>"isn't answering", or "hasn't answered for 7 minutes" once that is a minute or more.</summary>
    private static string Absence(NetworkMachine machine) => machine.OfflineFor is { TotalMinutes: >= 1 } away
        ? $"hasn't answered for {Minutes(away)}" : "isn't answering";

    private static string Minutes(TimeSpan? away) => away is { } time
        ? time.TotalMinutes < 1.5 ? "a minute" : $"{Math.Round(time.TotalMinutes).ToString(CultureInfo.InvariantCulture)} minutes"
        : "a while";

    private static string List(IReadOnlyList<string> items) => items.Count switch
    {
        0 => "",
        1 => items[0],
        _ => string.Join(", ", items.Take(items.Count - 1)) + " and " + items[^1]
    };
}
