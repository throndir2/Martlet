namespace Martlet.Core.Planning;

// The recommended setup for all your computers: the shared vocabulary of the network recommender (NetworkRecommender),
// the executor that applies a recommendation to every computer, the presence monitor that asks for a new recommendation
// when a computer comes back or stays away, and Home's Recommended setup review. Pure data: no I/O, no secrets.

/// <summary>What a computer in the owner's Martlet network is for: a companion PC (the owner talks to Martlet there, and it
/// often runs games, so the recommender keeps it light) or a host PC (it lends its hardware to the others).</summary>
public enum NetworkMachineKind { Companion, Host }

/// <summary>One host role on a computer's host service: its kind (a HostRoles kind such as "ollama", "deep-thinking",
/// "stt", "chatterbox", "audio2face"), its model (null when the role has no model choice) and the graphics card it runs on
/// (an index into that computer's <see cref="MachineSpecs.Gpus"/>; null: the processor, every card, or not known).</summary>
public sealed record HostedRolePlacement(string Kind, string? Model, int? GpuIndex);

/// <summary>One computer the recommender plans for. <see cref="MachineSpecs.Id"/> is the id the cluster plan uses for it
/// (its host service's host id when it has one, else the Martlet device id).</summary>
public sealed record NetworkMachine(MachineSpecs Specs, NetworkMachineKind Kind)
{
    /// <summary>It answered its last check. A computer that isn't answering is not part of the network for the
    /// recommendation: it is planned without, and its jobs move.</summary>
    public bool Online { get; init; } = true;
    /// <summary>How long it has not answered, when it is offline and that is known.</summary>
    public TimeSpan? OfflineFor { get; init; }
    /// <summary>It has a host service (a paired Martlet gateway) that can run host roles. A companion PC without one runs
    /// only the parts inside the app (the character, Parakeet).</summary>
    public bool HasHostService { get; init; }
    /// <summary>Martlet can change its host service from another computer (through Martlet on that computer, or this PC's
    /// own host service). False: a change there needs someone at that computer.</summary>
    public bool Manageable { get; init; } = true;
    /// <summary>The host roles its host service runs now.</summary>
    public IReadOnlyList<HostedRolePlacement> Roles { get; init; } = [];
    /// <summary>Its host roles run on Windows (Martlet.Core.Installation.SharedGpu.OnWindows: this PC's Docker Desktop, or a
    /// host whose report names Windows or a WSL 2 kernel), where the graphics card's memory pages into main memory instead of
    /// failing. Null: <see cref="MachineSpecs.Platform"/> says.</summary>
    public bool? OnWindows { get; init; }
}

/// <summary>Who does one shared job (a ClusterJobs name: thinking, listening, speaking or lip-sync).
/// <see cref="HostId"/> is the computer in charge; null means no host does it (a hosted provider, or each companion PC
/// itself, as <see cref="OptionId"/> says). <see cref="Off"/> is lip-sync by nobody (voice loudness).
/// <see cref="OptionId"/> is the FootprintCatalog option that does it, when known. <see cref="Pool"/> lists the other
/// computers that run the same engine and take the job's requests when the one in charge is busy (Devices › Sharing
/// work), in the order they are tried.</summary>
public sealed record JobPlan(string Job, string? HostId, bool Off = false, string? OptionId = null)
{
    public IReadOnlyList<string> Pool { get; init; } = [];
    public string Why { get; init; } = "";
}

/// <summary>What one computer runs in a setup: its host roles and, for the review, why and how full it is.</summary>
public sealed record MachinePlan(string MachineId, NetworkMachineKind Kind, IReadOnlyList<HostedRolePlacement> Roles)
{
    public string Why { get; init; } = "";
    /// <summary>How full the setup makes it (planned footprints), when the recommender measured it.</summary>
    public MachineUsage? Usage { get; init; }
}

/// <summary>A whole network's setup: each computer's host roles, who does each job and which computers are in the
/// Thinking pool (they run the deep-thinking role and take background thinks).</summary>
public sealed record NetworkSetup(IReadOnlyList<MachinePlan> Machines, IReadOnlyList<JobPlan> Jobs)
{
    public IReadOnlyList<string> ThinkingPool { get; init; } = [];

    public MachinePlan? Machine(string machineId) => Machines.FirstOrDefault(m => m.MachineId == machineId);

    public JobPlan? Job(string job) => Jobs.FirstOrDefault(j => j.Job == job);
}

/// <summary>Everything the recommender plans from: the computers, the owner's stance on hosted providers and which
/// optional parts they use, and today's setup.</summary>
public sealed record NetworkSetupRequest(IReadOnlyList<NetworkMachine> Machines)
{
    public HostingPreference Preference { get; init; } = HostingPreference.Balanced;
    /// <summary>Provider ids with a saved key ("nvidia-build", "openrouter", "openai").</summary>
    public IReadOnlyCollection<string> ConfiguredProviders { get; init; } = [];
    /// <summary>The parts to plan; null: every part (PlanComponent).</summary>
    public IReadOnlyCollection<PlanComponent>? Wanted { get; init; }
    /// <summary>Today's setup: who does each job, and the Thinking pool. Each computer's roles are on
    /// <see cref="NetworkMachine.Roles"/>.</summary>
    public IReadOnlyList<JobPlan> CurrentJobs { get; init; } = [];
    public IReadOnlyList<string> CurrentThinkingPool { get; init; } = [];
    /// <summary>The voice engine the owner chose (a host role kind such as "chatterbox"); its voices are made for it, so the
    /// recommender keeps it unless it cannot run anywhere.</summary>
    public string? VoiceEngine { get; init; }
    /// <summary>Hosts the owner left out of the Thinking pool (ThinkingPoolSettings.LeftByOwner): the recommender never plans
    /// a Deep thinking role for the pool there.</summary>
    public IReadOnlyCollection<string> ThinkingPoolOptOut { get; init; } = [];
    /// <summary>The parts the owner turned off (<see cref="ComponentRanking.CanBeOff"/>: lip-sync means advanced lip-sync, so
    /// the face follows the voice's loudness; Deep thinking, singing and pictures). The recommender removes their roles and
    /// plans without them. Unlike <see cref="Wanted"/>, which leaves a part it doesn't plan as it is.</summary>
    public IReadOnlyCollection<PlanComponent> Off { get; init; } = [];
}

/// <summary>AddRole / RemoveRole: install or remove a host role on a computer's host service. ChangeModel: the same role
/// with another model. MoveToGpu: the same role pinned to another graphics card. AssignJob: another computer (or none)
/// does a job. JoinPool / LeavePool: a computer starts or stops taking a job's requests when the one in charge is busy
/// (<see cref="SetupChange.Job"/>), or background thinks (Job null: the Thinking pool).</summary>
public enum SetupChangeKind { AddRole, RemoveRole, ChangeModel, MoveToGpu, AssignJob, JoinPool, LeavePool }

/// <summary>How much a change matters. Required: something is missing, overfilled or on a computer that isn't answering.
/// Improvement: faster replies, a better model, or more computers sharing the work. Minor: tidier, but nobody would notice.
/// Automatic checks only ask the owner about Required and Improvement changes.</summary>
public enum SetupChangeBenefit { Required, Improvement, Minor }

/// <summary>One step from today's setup to the recommended one, on <see cref="MachineId"/>. <see cref="Summary"/> is one
/// plain sentence for the review ("Install Chatterbox Turbo on gpu-box's RTX 4090."), <see cref="Why"/> the reason.
/// AssignJob: <see cref="MachineId"/> is the computer that does the job next, or "" when no host does it next (a hosted
/// provider, or each companion PC itself; <see cref="OptionId"/> says which). The Thinking pool has no JoinPool or LeavePool
/// changes: a host joins it by itself once it runs a Deep thinking role (AddRole "deep-thinking").</summary>
public sealed record SetupChange(SetupChangeKind Kind, string MachineId, string Summary, string Why)
{
    /// <summary>AssignJob: the FootprintCatalog option that does the job next (as <see cref="JobPlan.OptionId"/>).</summary>
    public string? OptionId { get; init; }
    public SetupChangeBenefit Benefit { get; init; } = SetupChangeBenefit.Improvement;
    /// <summary>AddRole, RemoveRole, ChangeModel, MoveToGpu: the host role kind.</summary>
    public string? RoleKind { get; init; }
    /// <summary>AddRole, ChangeModel: the model to run; ChangeModel: <see cref="FromModel"/> is the one it runs now.</summary>
    public string? Model { get; init; }
    public string? FromModel { get; init; }
    /// <summary>AddRole, MoveToGpu: the card to pin the role to (an index into that computer's MachineSpecs.Gpus).</summary>
    public int? GpuIndex { get; init; }
    /// <summary>AddRole, ChangeModel: the role runs on the processor although the computer has a graphics card (its card has no
    /// room for it), so the host installs its processor variant (choice.accelerator=cpu).</summary>
    public bool OnProcessor { get; init; }
    /// <summary>AssignJob, JoinPool, LeavePool: the ClusterJobs name (null for the Thinking pool).</summary>
    public string? Job { get; init; }
    /// <summary>AssignJob: the computer that does the job now (null: no host).</summary>
    public string? FromMachineId { get; init; }
    /// <summary>About how much this downloads on that computer, when known.</summary>
    public double? DownloadGb { get; init; }
    /// <summary>The computer can't be changed from here (<see cref="NetworkMachine.Manageable"/> is false): someone has to
    /// make this change at that computer.</summary>
    public bool NeedsSomeoneThere { get; init; }
    /// <summary>The sentence in <see cref="Why"/> about a computer that stays away ("MIKU hasn't answered for 155 minutes, so
    /// thinking moves."), or null. <see cref="NetworkRecommendation.Offline"/> lists those computers.</summary>
    public string? Away { get; init; }

    /// <summary><see cref="Why"/> without <see cref="Away"/>: the reason for a review that names the computers that stay away
    /// once (empty when the computer staying away is the only reason).</summary>
    public string Detail => Away is { Length: > 0 } away
        ? Why.Replace(away, "", StringComparison.Ordinal).Replace("  ", " ", StringComparison.Ordinal).Trim()
        : Why;
}

/// <summary>A computer that isn't answering: the recommendation plans without it. <see cref="For"/> is how long it hasn't
/// answered (zero when not known). <see cref="Note"/> is its sentence in <see cref="NetworkRecommendation.Notes"/>.</summary>
public sealed record OfflineComputer(string Id, TimeSpan For, string Note);

/// <summary>One part of Martlet in the recommended setup, in the priority list's order (<see cref="ComponentRanking"/>).
/// <see cref="On"/> false: the part is off (<see cref="Where"/> says what that means). <see cref="Where"/> is where it runs
/// ("Gemma 4 E2B in Ollama on This PC's NVIDIA GeForce RTX 4070"), <see cref="Why"/> the reason. <see cref="CanBeOff"/>: the
/// owner can turn it off; <see cref="OwnerOff"/>: they did.</summary>
public sealed record ComponentStatus(PlanComponent Component, int Rank, ComponentNecessity Necessity, bool On, string Where, string Why)
{
    public bool CanBeOff => ComponentRanking.CanBeOff(Component);
    public bool OwnerOff { get; init; }
    public string Name => ComponentRanking.Name(Component);
}

/// <summary>The recommended setup for all the owner's computers and the changes that get there from today's.
/// <see cref="Fingerprint"/> is the same for the same recommended setup, so a suggestion the owner declined isn't asked
/// about again until something changes.</summary>
public sealed record NetworkRecommendation(NetworkSetup Current, NetworkSetup Target, IReadOnlyList<SetupChange> Changes)
{
    public string Fingerprint { get; init; } = "";
    /// <summary>Plain sentences about the whole network ("gpu-box hasn't answered for 12 minutes, so its jobs move.").</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];
    /// <summary>The computers the recommendation plans without (they aren't answering), in the order of their ids.</summary>
    public IReadOnlyList<OfflineComputer> Offline { get; init; } = [];
    /// <summary>Every part of Martlet in priority order, with where it runs in the recommended setup or that it is off.</summary>
    public IReadOnlyList<ComponentStatus> Components { get; init; } = [];
    /// <summary>The note in <see cref="Notes"/> that says Martlet can't reply ("No computer has room for a Thinking model, and
    /// no free API key is saved. ..."), or null when it can.</summary>
    public string? CannotReplyNote { get; init; }

    /// <summary>In the recommended setup nobody does Thinking (no computer has room, and no hosted provider can), and
    /// today's setup isn't simply kept: Martlet can't reply until Thinking is set up.</summary>
    public bool CannotReply => CannotReplyNote is not null;

    /// <summary>The note in <see cref="Notes"/> that says Martlet can't speak ("Martlet can't speak yet: no computer can run
    /// ..."), or null when a voice speaks. Replies still show as text.</summary>
    public string? CannotSpeakNote { get; init; }

    /// <summary>In the recommended setup no computer and no hosted voice with a saved key can speak.</summary>
    public bool CannotSpeak => CannotSpeakNote is not null;

    /// <summary>Today's setup is already the recommended one.</summary>
    public bool AlreadyOptimal => Changes.Count == 0;

    /// <summary>Worth asking about without the owner asking: a change that is required or improves something.</summary>
    public bool WorthAsking => Changes.Any(c => c.Benefit != SetupChangeBenefit.Minor);
}
