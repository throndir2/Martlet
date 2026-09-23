using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Martlet.Core.Contracts;
using Martlet.Core.Installation;
using Martlet.Host.Inventory;
using Martlet.HostArtifacts;

namespace Martlet.Host.Setup;

public sealed class SetupPlanBuilder
{
    public static readonly TimeSpan DefaultHostFactMaximumAge = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan DefaultPlanLifetime = TimeSpan.FromMinutes(10);

    private readonly TimeProvider clock;
    private readonly TimeSpan hostFactMaximumAge;
    private readonly TimeSpan planLifetime;

    public SetupPlanBuilder(
        TimeProvider? clock = null,
        TimeSpan? hostFactMaximumAge = null,
        TimeSpan? planLifetime = null)
    {
        this.clock = clock ?? TimeProvider.System;
        this.hostFactMaximumAge = hostFactMaximumAge ?? DefaultHostFactMaximumAge;
        this.planLifetime = planLifetime ?? DefaultPlanLifetime;
        SetupGuard.Require(this.hostFactMaximumAge > TimeSpan.Zero &&
            this.hostFactMaximumAge <= TimeSpan.FromHours(1) &&
            this.planLifetime > TimeSpan.Zero &&
            this.planLifetime <= TimeSpan.FromHours(1), SetupFailure.InvalidConfiguration);
    }

    public SetupPlan Build(
        SetupConfiguration configuration,
        HostReport hostReport,
        InspectionReport artifactReport) =>
        BuildCore(configuration, hostReport, artifactReport, null, null);

    public SetupPlan Build(
        SetupConfiguration configuration,
        InstallationRequest request,
        Guid targetHostId,
        HostReport hostReport,
        ArtifactManifest artifactManifest)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(artifactManifest);
        var installation = InstallationPlanner.Create(request);
        SetupGuard.Require(targetHostId != Guid.Empty && request.Hosts.Any(h => h.Id == targetHostId),
            SetupFailure.InvalidConfiguration);
        var machine = installation.Machines.SingleOrDefault(m => m.HostId == targetHostId);
        var managed = machine?.Runtimes.Where(r => r.Owner.Mode == InstallationOwnership.GuidedManaged)
            .SelectMany(r => r.Roles).Distinct().Order().ToArray() ?? [];
        var selected = configuration.Roles.Select(role => role == SetupRole.Llm
            ? InstallationRole.Llm : InstallationRole.Tts).Order().ToArray();
        var matches = managed.SequenceEqual(selected) && installation.Roles
            .Where(r => selected.Contains(r.Role) && r.HostId == targetHostId)
            .All(r => r.DefinitionId == (r.Role == InstallationRole.Llm ? "ollama-llm" : "f5-tts"));
        // This frozen H05a adapter is not a second role/dependency planner.
        SetupGuard.Require(matches, SetupFailure.InvalidConfiguration);
        var artifacts = ArtifactInspector.InspectRoles(artifactManifest,
            selected.Select(role => role == InstallationRole.Llm ? "ollama-llm" : "f5-tts"),
            "ubuntu-24.04-x64");
        return BuildCore(configuration, hostReport, artifacts,
            FingerprintBuilder.Bytes(ContractJson.Write(request)), targetHostId, machine);
    }

    private SetupPlan BuildCore(
        SetupConfiguration configuration,
        HostReport hostReport,
        InspectionReport artifactReport,
        string? installationRequestFingerprint,
        Guid? targetHostId,
        MachineInstallationPlan? installationMachine = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(hostReport);
        ArgumentNullException.ThrowIfNull(artifactReport);
        try
        {
            hostReport.Validate();
        }
        catch (InvalidDataException)
        {
            throw new SetupException(SetupFailure.InvalidPrerequisiteFacts);
        }

        var now = clock.GetUtcNow();
        var hostJson = HostJson.Serialize(hostReport);
        var artifactJson = artifactReport.ToJson();
        var hostFingerprint = FingerprintBuilder.Bytes(Encoding.UTF8.GetBytes(hostJson));
        var artifactFingerprint = FingerprintBuilder.Bytes(Encoding.UTF8.GetBytes(artifactJson));
        SetupGuard.Fingerprint(artifactReport.DocumentSha256, SetupFailure.InvalidPrerequisiteFacts);

        var prerequisites = hostReport.Probes
            .OrderBy(p => p.Id)
            .Select(ToPrerequisite)
            .ToList();
        prerequisites.Add(new(
            "artifact-manifest",
            artifactReport.ExecutionEligible ? SetupPrerequisiteState.Satisfied : SetupPrerequisiteState.Unknown,
            true,
            artifactReport.ExecutionEligible
                ? "The exact inspected H02 manifest reports an execution-eligible candidate."
                : "The exact inspected H02 manifest remains disabled or incomplete.",
            artifactReport.ExecutionEligible
                ? "Retain the exact manifest fingerprint through execution."
                : "Complete H02 runtime, payload, rights, dependency, host-fit, and disk facts; then inspect the exact new document."));

        var blockers = new List<SetupBlocker>();
        var requiredProbes = hostReport.Probes.Where(probe => probe.Required).ToArray();
        var missingObservationTime = requiredProbes.Any(probe => probe.ObservedAt is null);
        var oldestObservation = requiredProbes
            .Where(probe => probe.ObservedAt is not null)
            .Select(probe => probe.ObservedAt!.Value)
            .DefaultIfEmpty(hostReport.CreatedAt)
            .Min();
        var age = now - oldestObservation;
        blockers.Add(Blocker(SetupBlockerCode.HostFactsNotLive,
            "Every supplied Inventory report is unauthenticated metadata, including a LiveLocal tag.",
            "A report, timestamp, caller flag or planner eligibility is not observed ownership, qualification or execution consent."));
        if (installationRequestFingerprint is null)
            blockers.Add(Blocker(SetupBlockerCode.InstallationBindingRequired,
                "The legacy proposal has no bound installation request, physical host or resource owner identity.",
                "Use the InstallationRequest overload to freeze the shared planner selection; legacy proposals remain review metadata only."));
        if (missingObservationTime || age < TimeSpan.Zero || age > hostFactMaximumAge)
            blockers.Add(Blocker(SetupBlockerCode.HostFactsStale,
                "A required H01 observation time is missing or outside the 15-minute default planning window.",
                "Rerun read-only preflight and rebuild the plan; do not execute stale observations."));
        if (hostReport.Scope != DoctorScope.Prerequisites ||
            hostReport.Probes.Any(p => p.Required && p.State != FindingState.Observed))
            blockers.Add(Blocker(SetupBlockerCode.HostPrerequisitesIncomplete,
                "At least one required H01 prerequisite is missing, unknown, warning, unsupported, or not run.",
                "Review each exact H01 code/remedy. Unknown and warning states are not permission to install or reconfigure."));

        var platform = hostReport.Probes.Single(p => p.Id == ProbeId.Platform).Evidence?.Platform;
        if (platform is null || !platform.Linux || platform.Distribution != Distro.Ubuntu ||
            platform.Version != "24.04" || platform.OsArchitecture != "X64" ||
            platform.ProcessArchitecture != "X64")
            blockers.Add(Blocker(SetupBlockerCode.HostTargetMismatch,
                "The observed platform is not the initial Ubuntu 24.04 x86_64 target.",
                "Use the reviewed target or stop. H05a does not upgrade, reinstall, or reinterpret another platform."));

        var port = hostReport.Probes.Single(p => p.Id == ProbeId.GatewayPort).Evidence?.Port;
        if (port is null || port.Port != configuration.GatewayPort || port.OccupancyObserved ||
            !port.GatewayAvailabilityEstablished)
            blockers.Add(Blocker(SetupBlockerCode.GatewayPortConflict,
                "The H01 port observation does not establish the configured private gateway port as available.",
                "Rerun the exact reviewed availability probe when implemented and resolve any unrelated listener with its owner; do not stop it automatically."));

        if (!artifactReport.ExecutionEligible)
            blockers.Add(Blocker(SetupBlockerCode.ArtifactCandidateIncomplete,
                "H02 reports a disabled or incomplete artifact candidate.",
                "Complete immutable runtime/model pins, payload hashes, dependency closure, rights review, and host compatibility before setup."));

        var knownBytes = artifactReport.KnownPayloadBytes ?? 0;
        SetupGuard.Require(knownBytes >= 0, SetupFailure.InvalidPrerequisiteFacts);
        var reservationFloor = checked(knownBytes * 2);
        var diskBudget = new SetupDiskBudget(knownBytes, reservationFloor, null, Complete: false);
        if (!diskBudget.Complete)
            blockers.Add(Blocker(SetupBlockerCode.DiskBudgetIncomplete,
                "Only the H02 listed-payload subtotal and a provisional two-times reservation floor are known.",
                "Resolve complete download, expanded, staging, rollback, inode, and user-data requirements before provisioning."));
        if (!diskBudget.FitsObservedDisk)
            blockers.Add(Blocker(SetupBlockerCode.DiskInsufficient,
                "Actual storage availability is unknown; a supplied disk row is not a filesystem observation.",
                "Select and re-probe the intended storage filesystem or free reviewed space; H05a deletes nothing."));

        blockers.Add(Blocker(SetupBlockerCode.MutationDeferred,
            "All Ubuntu mutation remains deferred beyond H05a.",
            "Implement and review H05b/H05c before adding exact download, Compose, firewall, service, model, or package commands."));

        var manifestHash = artifactReport.DocumentSha256!;
        var resources = new List<SetupExpectedResource>
        {
            new("configuration-directory", SetupExpectedResourceKind.Directory,
                configuration.ConfigurationDirectory, null, true),
            new("data-directory", SetupExpectedResourceKind.Directory,
                configuration.DataDirectory, null, true),
            new("artifact-directory", SetupExpectedResourceKind.Directory,
                configuration.ArtifactDirectory, null, true),
            new("host-plan-file", SetupExpectedResourceKind.File,
                configuration.ConfigurationDirectory + "/host-plan.json", null, true),
            new("private-gateway-port", SetupExpectedResourceKind.TcpPort,
                $"tcp/{configuration.GatewayPort.ToString(CultureInfo.InvariantCulture)}", null, true),
            new("gateway-service", SetupExpectedResourceKind.Service,
                "martlet-gateway.service", null, true),
            new("artifact-manifest-input", SetupExpectedResourceKind.ArtifactManifest,
                $"sha256:{manifestHash}", knownBytes == 0 ? null : knownBytes, true)
        };
        foreach (var role in configuration.Roles)
            resources.Add(new(
                "role-" + role.ToString().ToLowerInvariant(),
                SetupExpectedResourceKind.RoleArtifactSet,
                role == SetupRole.Llm ? "ollama-llm" : "f5-tts",
                null,
                true));

        var requiredProbeIds = prerequisites.Where(p => p.Id != "artifact-manifest").Select(p => p.Id).ToImmutableArray();
        var roleResourceIds = resources.Where(r => r.Kind == SetupExpectedResourceKind.RoleArtifactSet).Select(r => r.Id).ToImmutableArray();
        var steps = new[]
        {
            new SetupStep("verify-h01-facts", 1, SetupStepKind.VerifyHostFacts,
                SetupStepExecution.ObservationOnly,
                "Validate the exact bounded H01 prerequisite report and retain its fingerprint.",
                SetupPrivilege.None, [], requiredProbeIds, []),
            new SetupStep("review-h02-manifest", 2, SetupStepKind.ReviewArtifactManifest,
                SetupStepExecution.ObservationOnly,
                "Review the exact H02 metadata manifest, listed-byte subtotal, unresolved facts, and rights gaps.",
                SetupPrivilege.None, [], ["artifact-manifest"], ["artifact-manifest-input", .. roleResourceIds]),
            new SetupStep("prepare-host-filesystem", 3, SetupStepKind.PrepareHostFilesystem,
                SetupStepExecution.Deferred,
                "Create only the reviewed Martlet configuration, data, and artifact paths with non-root runtime ownership.",
                SetupPrivilege.Administrator, [SetupConsentScope.HostFilesystem],
                ["platform", "disk"], ["configuration-directory", "data-directory", "artifact-directory", "host-plan-file"]),
            new SetupStep("configure-container-prerequisites", 4, SetupStepKind.ConfigureContainerPrerequisites,
                SetupStepExecution.Deferred,
                "Reconcile only exact reviewed engine, Compose-v2-family, toolkit, and driver prerequisites without group grants.",
                SetupPrivilege.Administrator,
                [SetupConsentScope.PackageRepositories, SetupConsentScope.ContainerRuntime, SetupConsentScope.NvidiaDriver],
                ["dockerengine", "compose", "containertoolkit", "dockeraccess", "nvidia"], []),
            new SetupStep("provision-selected-artifacts", 5, SetupStepKind.ProvisionArtifacts,
                SetupStepExecution.Deferred,
                "Provision only separately approved pinned artifacts after H05b adds resumable verified acquisition.",
                SetupPrivilege.Operator,
                [SetupConsentScope.ArtifactDownload, SetupConsentScope.ModelAndVoiceRights],
                ["artifact-manifest", "disk"], ["artifact-directory", "artifact-manifest-input", .. roleResourceIds]),
            new SetupStep("configure-gateway-service", 6, SetupStepKind.ConfigureGatewayService,
                SetupStepExecution.Deferred,
                "Configure the future paired private gateway service and narrow firewall rule after H03/H05c.",
                SetupPrivilege.Administrator,
                [SetupConsentScope.Firewall, SetupConsentScope.ServiceManagement],
                ["gatewayport", "network", "localclock"], ["private-gateway-port", "gateway-service"])
        };

        var hostFactDeadline = oldestObservation + hostFactMaximumAge;
        var expiresAt = !missingObservationTime && hostFactDeadline > now
            ? (hostFactDeadline < now + planLifetime ? hostFactDeadline : now + planLifetime)
            : now + planLifetime;
        return new SetupPlan(
            "h05a-single-host-v1",
            now,
            expiresAt,
            configuration.Fingerprint,
            hostFingerprint,
            artifactFingerprint,
            SetupPlanDisposition.Blocked,
            prerequisites,
            resources,
            diskBudget,
            steps,
            blockers,
            installationRequestFingerprint,
            targetHostId,
            installationMachine);
    }

    private static SetupPrerequisite ToPrerequisite(ProbeResult probe)
    {
        var state = probe.State switch
        {
            FindingState.Observed => SetupPrerequisiteState.Unknown,
            FindingState.Missing => SetupPrerequisiteState.Missing,
            FindingState.Warning => SetupPrerequisiteState.ReviewRequired,
            FindingState.Unsupported => SetupPrerequisiteState.Conflict,
            _ => SetupPrerequisiteState.Unknown
        };
        return new(
            probe.Id.ToString().ToLowerInvariant(),
            state,
            probe.Required,
            $"Unauthenticated report claim: {probe.Code}: {probe.Summary}",
            probe.Remedy.Instruction);
    }

    private static SetupBlocker Blocker(SetupBlockerCode code, string summary, string remedy) =>
        new(code, summary, remedy);
}
