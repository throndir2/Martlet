using System.Collections.Immutable;
using Martlet.Core.Contracts;

namespace Martlet.Core.Installation;

public enum InstallationFeature { Fixture, TypedConversation, MicrophoneInput, SpokenReplies, Perception, Memory, Avatar }
public enum InstallationRole { Llm, Stt, Tts, Capture, Output, ScreenCapture, Perception, Memory, Avatar }
public enum InstallationOwnership { GuidedManaged, UserManagedCompose, ExistingService }
public enum InstallationRuntimeKind { ExternalApi, Native, Compose }
public enum PlanningState { Eligible, Unknown, Blocked, Unavailable }
public enum InstallationPrerequisite
{
    HostQualification, RuntimeCompatibility, ArtifactEligibility, LicenseReview, LocalApproval,
    CredentialBinding, DataConsent, Pairing, CaptureDevice, OutputDevice, Reboot
}
public enum PlanningReason
{
    FutureFeature, DestinationMissing, CapabilityUnknown, CapabilityUnsupported, Eligibility,
    Prerequisite, ResourceCapacityUnknown, ResourceCapacityExceeded, ResourceConflict, OwnershipConflict
}
public enum InstallationReviewOperation
{
    ReviewRoleConfiguration, ReviewManagedRuntime, ReviewExternalConnection, ReviewManagedResource
}

/// <summary>Caller-supplied planning fact, never qualification evidence or consent.</summary>
public sealed record InstallationFact(PlanningState State, string ReasonId)
{
    public static InstallationFact Unknown { get; } = new(PlanningState.Unknown, "not-established");
}

public sealed record PrerequisiteFact(InstallationPrerequisite Kind, InstallationFact Fact);

/// <summary>Physical-machine totals or demands. Null means unknown, not zero.</summary>
public sealed record InstallationResources(long? CpuMilliCores, long? RamMiB, long? VramMiB, long? DiskMiB)
{
    public static InstallationResources Zero { get; } = new(0, 0, 0, 0);
}

public sealed record InstallationHost(Guid Id, string Label, InstallationResources Capacity);
public sealed record InstallationOwner(InstallationOwnership Mode, Guid Id);

/// <summary>A shared dependency or occupied resource. Dependencies stay on the same physical host.</summary>
public sealed record InstallationResource(
    Guid Id,
    Guid HostId,
    string Slot,
    InstallationOwner Owner,
    InstallationResources Demand,
    bool InUse,
    InstallationFact Eligibility,
    ImmutableArray<Guid> Dependencies);

/// <summary>
/// A runtime instance, not a host or provider route. HostId is absent only for an external API.
/// ResourceIds includes the runtime's own demand as well as its shared dependencies.
/// </summary>
public sealed record InstallationRuntime(
    Guid Id,
    Guid? HostId,
    string EngineId,
    string DefinitionId,
    InstallationRuntimeKind Kind,
    InstallationOwner Owner,
    InstallationFact Eligibility,
    ImmutableArray<Guid> ResourceIds,
    ImmutableArray<PrerequisiteFact> Prerequisites);

/// <summary>
/// A saved role choice. It participates only when a selected feature requires this role.
/// DefinitionId binds the caller's facts to the exact named adapter/model/device selection.
/// </summary>
public sealed record InstallationDestination(
    InstallationRole Role,
    Guid RuntimeId,
    string DefinitionId,
    CapabilitySupport Capability,
    InstallationFact Eligibility,
    ImmutableArray<PrerequisiteFact> Prerequisites);

/// <summary>In-memory planning input, deliberately separate from persisted application settings.</summary>
public sealed record InstallationRequest : IContract
{
    public const int MaxHosts = 16;
    public const int MaxRuntimes = 32;
    public const int MaxResources = 128;
    public const int MaxDependencies = 16;
    public const long MaxResourceQuantity = 1_000_000_000;

    public required Guid ClientHostId { get; init; }
    public required ImmutableArray<InstallationFeature> Features { get; init; }
    public required ImmutableArray<InstallationHost> Hosts { get; init; }
    public required ImmutableArray<InstallationRuntime> Runtimes { get; init; }
    public required ImmutableArray<InstallationResource> Resources { get; init; }
    public required ImmutableArray<InstallationDestination> Destinations { get; init; }

    public void Validate() => InstallationValidation.Validate(this);
}

public sealed record InstallationIssue(
    PlanningState State, PlanningReason Reason, string SubjectId, string DetailId);

public sealed record PlannedPrerequisite(
    InstallationPrerequisite Kind, string SubjectId, InstallationFact Fact,
    ImmutableArray<InstallationRole> Roles);

public sealed record PlannedOperation(
    InstallationReviewOperation Kind, Guid SubjectId, ImmutableArray<InstallationRole> Roles);

public sealed record PlannedRole(
    InstallationRole Role, Guid? RuntimeId, Guid? HostId, string? DefinitionId,
    PlanningState State, ImmutableArray<InstallationIssue> Issues);

public sealed record PlannedFeature(
    InstallationFeature Feature, PlanningState State, ImmutableArray<InstallationRole> RequiredRoles);

public sealed record PlannedResource(
    Guid Id, string Slot, InstallationOwner Owner, InstallationResources Demand, bool InUse,
    ImmutableArray<InstallationRole> Roles);

public sealed record PlannedRuntime(
    Guid Id, string EngineId, string DefinitionId, InstallationRuntimeKind Kind,
    InstallationOwner Owner, ImmutableArray<InstallationRole> Roles);

/// <summary>HostId null groups external APIs, not an invented local or remote machine.</summary>
public sealed record MachineInstallationPlan(
    Guid? HostId, string Label, InstallationResources? Capacity, InstallationResources Demand,
    ImmutableArray<PlannedRuntime> Runtimes, ImmutableArray<PlannedResource> Resources,
    ImmutableArray<PlannedPrerequisite> Prerequisites, ImmutableArray<PlannedOperation> Operations);

/// <summary>
/// Deterministic review data only. Eligible means no supplied planning blocker, not ready, qualified,
/// authorized, installed or safe to execute. There is intentionally no executor or permission token.
/// </summary>
public sealed record InstallationPlan(
    PlanningState State, ImmutableArray<PlannedFeature> Features, ImmutableArray<PlannedRole> Roles,
    ImmutableArray<MachineInstallationPlan> Machines);
