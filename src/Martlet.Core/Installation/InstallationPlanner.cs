using System.Collections.Immutable;
using Martlet.Core.Contracts;

namespace Martlet.Core.Installation;

public static class InstallationPlanner
{
    public static InstallationPlan Create(InstallationRequest request)
    {
        ContractRules.Require(request is not null, "An installation request is required.");
        request!.Validate();
        var runtimes = request.Runtimes.ToDictionary(r => r.Id);
        var resources = request.Resources.ToDictionary(r => r.Id);
        var destinations = request.Destinations.ToDictionary(d => d.Role);
        var required = request.Features.SelectMany(f => RequiredRoles(f)).Distinct().Order().ToArray();
        var issues = required.ToDictionary(r => r, _ => new List<InstallationIssue>());
        var active = required.Where(r => !FutureRole(r) && destinations.ContainsKey(r))
            .ToDictionary(r => r, r => destinations[r]);
        var prerequisites = new Dictionary<(string Subject, InstallationPrerequisite Kind), PlannedPrerequisite>();
        var resourceRoles = new Dictionary<Guid, HashSet<InstallationRole>>();
        var runtimeRoles = active.GroupBy(d => d.Value.RuntimeId)
            .ToDictionary(g => g.Key, g => g.Select(d => d.Key).Order().ToImmutableArray());

        foreach (var role in required)
        {
            if (FutureRole(role))
                Add(role, PlanningState.Unavailable, PlanningReason.FutureFeature, role.ToString(), "product-not-implemented");
            else if (!destinations.ContainsKey(role))
                Add(role, PlanningState.Blocked, PlanningReason.DestinationMissing, role.ToString(), "select-destination");
        }
        foreach (var (role, destination) in active)
        {
            var runtime = runtimes[destination.RuntimeId];
            var subject = RoleSubject(role);
            if (destination.Capability != CapabilitySupport.Supported)
                Add(role, destination.Capability == CapabilitySupport.Unknown ? PlanningState.Unknown : PlanningState.Unavailable,
                    destination.Capability == CapabilitySupport.Unknown ? PlanningReason.CapabilityUnknown : PlanningReason.CapabilityUnsupported,
                    subject, "selected-role-capability");
            AddFact(role, destination.Eligibility, PlanningReason.Eligibility, subject);
            AddFact(role, runtime.Eligibility, PlanningReason.Eligibility, runtime.Id.ToString());

            var roleGates = new List<InstallationPrerequisite>();
            if (runtime.Kind == InstallationRuntimeKind.ExternalApi)
                roleGates.AddRange([InstallationPrerequisite.CredentialBinding, InstallationPrerequisite.DataConsent]);
            if (role is InstallationRole.Llm or InstallationRole.Stt or InstallationRole.Tts)
                roleGates.Add(InstallationPrerequisite.DataConsent);
            if (role == InstallationRole.Capture)
                roleGates.AddRange([InstallationPrerequisite.CaptureDevice, InstallationPrerequisite.DataConsent]);
            if (role == InstallationRole.Output) roleGates.Add(InstallationPrerequisite.OutputDevice);
            AddPrerequisites([role], subject, destination.Prerequisites, roleGates);

            var closure = Closure(runtime.ResourceIds);
            foreach (var id in closure)
            {
                var resource = resources[id];
                if (!resourceRoles.TryGetValue(id, out var consumers))
                    resourceRoles[id] = consumers = [];
                consumers.Add(role);
                AddFact(role, resource.Eligibility, PlanningReason.Eligibility, id.ToString());
                if (resource.Owner.Mode == InstallationOwnership.GuidedManaged &&
                    resource.Owner != runtime.Owner)
                    Add(role, PlanningState.Blocked, PlanningReason.OwnershipConflict, id.ToString(), "managed-owner-mismatch");
            }
        }

        foreach (var (id, roles) in runtimeRoles)
        {
            var runtime = runtimes[id];
            if (runtime.Kind == InstallationRuntimeKind.ExternalApi) continue;
            var gates = new List<InstallationPrerequisite>
            {
                InstallationPrerequisite.HostQualification, InstallationPrerequisite.RuntimeCompatibility
            };
            if (runtime.HostId != request.ClientHostId) gates.Add(InstallationPrerequisite.Pairing);
            if (runtime.Owner.Mode == InstallationOwnership.GuidedManaged)
                gates.AddRange([InstallationPrerequisite.LocalApproval, InstallationPrerequisite.ArtifactEligibility,
                    InstallationPrerequisite.LicenseReview]);
            AddPrerequisites(roles, id.ToString(), runtime.Prerequisites, gates);
        }

        var machines = new List<MachineInstallationPlan>();
        foreach (var hostId in runtimeRoles.Keys.Select(id => runtimes[id].HostId).Distinct().Order())
        {
            var selectedRuntimes = runtimeRoles.Keys.Where(id => runtimes[id].HostId == hostId).Order().ToArray();
            var roles = selectedRuntimes.SelectMany(id => runtimeRoles[id]).Order().ToImmutableArray();
            var host = hostId is { } id ? request.Hosts.Single(h => h.Id == id) : null;
            var occupied = hostId is null ? new HashSet<Guid>() :
                Closure(request.Resources.Where(r => r.HostId == hostId && r.InUse).Select(r => r.Id));
            var relevant = resourceRoles.Keys.Where(id => resources[id].HostId == hostId).Concat(occupied).Distinct().Order().ToArray();
            var demand = Sum(relevant.Select(id => resources[id].Demand));
            if (host is not null)
            {
                CheckCapacity("cpu-millicores", host.Capacity.CpuMilliCores, demand.CpuMilliCores);
                CheckCapacity("ram-mib", host.Capacity.RamMiB, demand.RamMiB);
                CheckCapacity("vram-mib", host.Capacity.VramMiB, demand.VramMiB);
                CheckCapacity("disk-mib", host.Capacity.DiskMiB, demand.DiskMiB);
                foreach (var collision in relevant.GroupBy(id => resources[id].Slot, StringComparer.Ordinal).Where(g => g.Count() > 1))
                    foreach (var resourceId in collision)
                        if (resourceRoles.TryGetValue(resourceId, out var consumers))
                            foreach (var role in consumers)
                                Add(role, PlanningState.Blocked, PlanningReason.ResourceConflict, resourceId.ToString(), collision.Key);
            }
            var machinePrerequisites = prerequisites.Values.Where(p => roles.Any(p.Roles.Contains))
                .OrderBy(p => p.SubjectId, StringComparer.Ordinal).ThenBy(p => p.Kind).ToImmutableArray();
            // Operations are assembled only after every host conflict has been evaluated below.
            machines.Add(new(hostId, host?.Label ?? "External APIs", host?.Capacity, demand,
                selectedRuntimes.Select(id =>
                {
                    var runtime = runtimes[id];
                    return new PlannedRuntime(id, runtime.EngineId, runtime.DefinitionId, runtime.Kind, runtime.Owner, runtimeRoles[id]);
                }).ToImmutableArray(),
                relevant.Select(id => new PlannedResource(id, resources[id].Slot, resources[id].Owner, resources[id].Demand, resources[id].InUse,
                    resourceRoles.TryGetValue(id, out var consumers) ? consumers.Order().ToImmutableArray() : []))
                    .ToImmutableArray(),
                machinePrerequisites, []));

            void CheckCapacity(string dimension, long? capacity, long? used)
            {
                // A declared zero demand needs no capacity evidence for that dimension.
                if (used == 0) return;
                var state = used is null || capacity is null ? PlanningState.Unknown :
                    used > capacity ? PlanningState.Blocked : PlanningState.Eligible;
                if (state == PlanningState.Eligible) return;
                foreach (var role in roles)
                    Add(role, state, state == PlanningState.Unknown ? PlanningReason.ResourceCapacityUnknown : PlanningReason.ResourceCapacityExceeded,
                        hostId!.Value.ToString(), dimension);
            }
        }

        var plannedRoles = required.Select(role =>
        {
            var destination = active.GetValueOrDefault(role);
            var sorted = issues[role].Distinct().OrderBy(i => i.Reason).ThenBy(i => i.SubjectId, StringComparer.Ordinal)
                .ThenBy(i => i.DetailId, StringComparer.Ordinal).ThenBy(i => i.State).ToImmutableArray();
            return new PlannedRole(role, destination?.RuntimeId,
                destination is null ? null : runtimes[destination.RuntimeId].HostId, destination?.DefinitionId,
                Combine(sorted.Select(i => i.State)), sorted);
        }).ToImmutableArray();
        var states = plannedRoles.ToDictionary(r => r.Role, r => r.State);
        for (var index = 0; index < machines.Count; index++)
        {
            var machine = machines[index];
            var operations = new List<PlannedOperation>();
            foreach (var runtime in machine.Runtimes)
            {
                var eligible = runtime.Roles.Where(r => states[r] == PlanningState.Eligible).ToImmutableArray();
                if (eligible.IsEmpty) continue;
                operations.Add(new(InstallationReviewOperation.ReviewRoleConfiguration, runtime.Id, eligible));
                operations.Add(new(runtime.Owner.Mode == InstallationOwnership.GuidedManaged ?
                    InstallationReviewOperation.ReviewManagedRuntime : InstallationReviewOperation.ReviewExternalConnection,
                    runtime.Id, eligible));
            }
            foreach (var resource in machine.Resources.Where(r => r.Owner.Mode == InstallationOwnership.GuidedManaged))
            {
                var eligible = resource.Roles.Where(r => states[r] == PlanningState.Eligible).ToImmutableArray();
                if (!eligible.IsEmpty)
                    operations.Add(new(InstallationReviewOperation.ReviewManagedResource, resource.Id, eligible));
            }
            machines[index] = machine with
            {
                Operations = operations.OrderBy(o => o.Kind).ThenBy(o => o.SubjectId).ToImmutableArray()
            };
        }
        var features = request.Features.Order().Select(f =>
        {
            var roles = RequiredRoles(f);
            return new PlannedFeature(f, Combine(roles.Select(r => states[r])), roles);
        }).ToImmutableArray();
        return new(Combine(features.Select(f => f.State)), features, plannedRoles, machines.ToImmutableArray());

        void Add(InstallationRole role, PlanningState state, PlanningReason reason, string subject, string detail) =>
            issues[role].Add(new(state, reason, subject, detail));

        void AddFact(InstallationRole role, InstallationFact fact, PlanningReason reason, string subject)
        {
            if (fact.State != PlanningState.Eligible) Add(role, fact.State, reason, subject, fact.ReasonId);
        }

        void AddPrerequisites(ImmutableArray<InstallationRole> roles, string subject,
            ImmutableArray<PrerequisiteFact> supplied, IEnumerable<InstallationPrerequisite> mandatory)
        {
            var facts = supplied.ToDictionary(p => p.Kind, p => p.Fact);
            foreach (var kind in mandatory.Concat(facts.Keys).Distinct().Order())
            {
                var fact = facts.GetValueOrDefault(kind) ?? InstallationFact.Unknown;
                prerequisites.Add((subject, kind), new(kind, subject, fact, roles));
                foreach (var role in roles) AddFact(role, fact, PlanningReason.Prerequisite, $"{subject}.{kind}");
            }
        }

        HashSet<Guid> Closure(IEnumerable<Guid> roots)
        {
            var result = new HashSet<Guid>();
            void Include(Guid id)
            {
                if (!result.Add(id)) return;
                foreach (var dependency in resources[id].Dependencies) Include(dependency);
            }
            foreach (var id in roots) Include(id);
            return result;
        }
    }

    private static string RoleSubject(InstallationRole role) => $"role-{role}";

    private static bool FutureRole(InstallationRole role) =>
        role is InstallationRole.ScreenCapture or InstallationRole.Perception or InstallationRole.Memory or InstallationRole.Avatar;

    private static ImmutableArray<InstallationRole> RequiredRoles(InstallationFeature feature) => feature switch
    {
        InstallationFeature.Fixture => [],
        InstallationFeature.TypedConversation => [InstallationRole.Llm],
        InstallationFeature.MicrophoneInput => [InstallationRole.Llm, InstallationRole.Stt, InstallationRole.Capture],
        InstallationFeature.SpokenReplies => [InstallationRole.Llm, InstallationRole.Tts, InstallationRole.Output],
        InstallationFeature.Perception => [InstallationRole.ScreenCapture, InstallationRole.Perception],
        InstallationFeature.Memory => [InstallationRole.Memory],
        InstallationFeature.Avatar => [InstallationRole.Avatar],
        _ => throw new ContractException(ErrorCode.InvalidContract, "Unsupported installation feature.")
    };

    private static PlanningState Combine(IEnumerable<PlanningState> states) => states.DefaultIfEmpty(PlanningState.Eligible).Max();

    private static InstallationResources Sum(IEnumerable<InstallationResources> demands)
    {
        var values = demands.ToArray();
        return new(Total(r => r.CpuMilliCores), Total(r => r.RamMiB), Total(r => r.VramMiB), Total(r => r.DiskMiB));
        long? Total(Func<InstallationResources, long?> select) =>
            values.Any(v => select(v) is null) ? null : values.Sum(v => select(v)!.Value);
    }
}
