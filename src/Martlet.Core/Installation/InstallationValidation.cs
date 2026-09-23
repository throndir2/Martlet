using System.Collections.Immutable;
using Martlet.Core.Contracts;

namespace Martlet.Core.Installation;

internal static class InstallationValidation
{
    internal static void Validate(InstallationRequest request)
    {
        Id(request.ClientHostId);
        Items(request.Features, 7);
        Items(request.Hosts, InstallationRequest.MaxHosts);
        Items(request.Runtimes, InstallationRequest.MaxRuntimes);
        Items(request.Resources, InstallationRequest.MaxResources);
        Items(request.Destinations, 9);
        Unique(request.Features);
        foreach (var feature in request.Features) ContractRules.Defined(feature);
        Unique(request.Hosts.Select(h => h.Id));
        Unique(request.Runtimes.Select(r => r.Id));
        Unique(request.Resources.Select(r => r.Id));
        Unique(request.Destinations.Select(d => d.Role));

        var hosts = request.Hosts.ToDictionary(h => h.Id);
        var runtimes = request.Runtimes.ToDictionary(r => r.Id);
        var resources = request.Resources.ToDictionary(r => r.Id);
        ContractRules.Require(hosts.ContainsKey(request.ClientHostId), "The client must name a declared physical host.");
        foreach (var host in request.Hosts)
        {
            Id(host.Id);
            ContractRules.Text(host.Label, 64);
            ContractRules.Require(!string.IsNullOrWhiteSpace(host.Label), "A host label is required.");
            Quantities(host.Capacity);
        }
        foreach (var resource in request.Resources)
        {
            Id(resource.Id);
            ContractRules.Require(hosts.ContainsKey(resource.HostId), "A resource must name a declared physical host.");
            ContractRules.Identifier(resource.Slot);
            Owner(resource.Owner);
            Quantities(resource.Demand);
            Fact(resource.Eligibility);
            Items(resource.Dependencies, InstallationRequest.MaxDependencies);
            Unique(resource.Dependencies);
            foreach (var id in resource.Dependencies)
                ContractRules.Require(resources.TryGetValue(id, out var dependency) && dependency.HostId == resource.HostId,
                    "A dependency must name a resource on the same physical host.");
        }
        var visiting = new HashSet<Guid>();
        var visited = new HashSet<Guid>();
        void Visit(Guid id)
        {
            if (visited.Contains(id)) return;
            ContractRules.Require(visiting.Add(id), "Resource dependency cycles are not supported.");
            foreach (var dependency in resources[id].Dependencies) Visit(dependency);
            visiting.Remove(id);
            visited.Add(id);
        }
        foreach (var resource in request.Resources) Visit(resource.Id);

        foreach (var runtime in request.Runtimes)
        {
            Id(runtime.Id);
            ContractRules.Identifier(runtime.EngineId);
            ContractRules.Identifier(runtime.DefinitionId);
            ContractRules.Defined(runtime.Kind);
            Owner(runtime.Owner);
            Fact(runtime.Eligibility);
            Prerequisites(runtime.Prerequisites);
            Items(runtime.ResourceIds, InstallationRequest.MaxDependencies);
            Unique(runtime.ResourceIds);
            if (runtime.Kind == InstallationRuntimeKind.ExternalApi)
                ContractRules.Require(runtime.HostId is null && runtime.ResourceIds.IsEmpty &&
                    runtime.Owner.Mode == InstallationOwnership.ExistingService && runtime.Prerequisites.IsEmpty,
                    "An external API has no local host, resources, lifecycle ownership or host prerequisites.");
            else
            {
                ContractRules.Require(runtime.HostId is { } hostId && hosts.ContainsKey(hostId),
                    "A hosted runtime must name a declared physical host.");
                ContractRules.Require(!runtime.ResourceIds.IsEmpty, "A hosted runtime must declare its resource demand; use unknown quantities if unmeasured.");
                ContractRules.Require(runtime.Owner.Mode != InstallationOwnership.UserManagedCompose ||
                    runtime.Kind == InstallationRuntimeKind.Compose, "User-managed Compose ownership requires a Compose runtime.");
                foreach (var id in runtime.ResourceIds)
                    ContractRules.Require(resources.TryGetValue(id, out var resource) && resource.HostId == runtime.HostId,
                        "Runtime resources must belong to its physical host.");
                ContractRules.Require(runtime.Prerequisites.All(p => p.Kind is not
                    (InstallationPrerequisite.CredentialBinding or InstallationPrerequisite.DataConsent or
                    InstallationPrerequisite.CaptureDevice or InstallationPrerequisite.OutputDevice)),
                    "Credential, data and device prerequisites belong to the exact role destination.");
            }
        }
        foreach (var destination in request.Destinations)
        {
            ContractRules.Defined(destination.Role);
            ContractRules.Identifier(destination.DefinitionId);
            ContractRules.Defined(destination.Capability);
            Fact(destination.Eligibility);
            Prerequisites(destination.Prerequisites);
            ContractRules.Require(destination.Prerequisites.All(p => p.Kind is
                InstallationPrerequisite.CredentialBinding or InstallationPrerequisite.DataConsent or
                InstallationPrerequisite.CaptureDevice or InstallationPrerequisite.OutputDevice),
                "Host prerequisites belong to the runtime, not a role destination.");
            ContractRules.Require(destination.Prerequisites.All(p =>
                (p.Kind != InstallationPrerequisite.CaptureDevice || destination.Role is InstallationRole.Capture or InstallationRole.ScreenCapture) &&
                (p.Kind != InstallationPrerequisite.OutputDevice || destination.Role == InstallationRole.Output)),
                "Device prerequisites must match the selected role.");
            ContractRules.Require(runtimes.ContainsKey(destination.RuntimeId), "A destination must name a declared runtime.");
            var runtime = runtimes[destination.RuntimeId];
            if (destination.Role is InstallationRole.Capture or InstallationRole.Output or InstallationRole.ScreenCapture or InstallationRole.Avatar)
                ContractRules.Require(runtime.HostId == request.ClientHostId && runtime.Kind == InstallationRuntimeKind.Native,
                    "Client capture, output and avatar roles must use a native runtime on the client host.");
        }
    }

    private static void Items<T>(ImmutableArray<T> items, int maximum)
    {
        ContractRules.Require(!items.IsDefault && items.Length <= maximum, "A planning collection is missing or exceeds its bound.");
        ContractRules.Require(items.All(item => item is not null), "Planning collections cannot contain null entries.");
    }

    private static void Unique<T>(IEnumerable<T> items) =>
        ContractRules.Require(items.Distinct().Count() == items.Count(), "Planning identities and selections must be unique.");

    private static void Id(Guid id) => ContractRules.Require(id != Guid.Empty, "A stable nonempty identity is required.");

    private static void Owner(InstallationOwner owner)
    {
        ContractRules.Require(owner is not null, "Explicit ownership is required.");
        ContractRules.Defined(owner!.Mode);
        Id(owner.Id);
    }

    private static void Quantities(InstallationResources quantities)
    {
        ContractRules.Require(quantities is not null, "Resource quantities are required.");
        foreach (var quantity in new[] { quantities!.CpuMilliCores, quantities.RamMiB, quantities.VramMiB, quantities.DiskMiB })
            ContractRules.Require(quantity is null or >= 0 and <= InstallationRequest.MaxResourceQuantity,
                "Resource quantities must be unknown or within the supported nonnegative bound.");
    }

    private static void Fact(InstallationFact fact)
    {
        ContractRules.Require(fact is not null, "An explicit planning fact is required.");
        ContractRules.Defined(fact!.State);
        ContractRules.Identifier(fact.ReasonId);
    }

    private static void Prerequisites(ImmutableArray<PrerequisiteFact> prerequisites)
    {
        Items(prerequisites, 11);
        Unique(prerequisites.Select(p => p.Kind));
        foreach (var prerequisite in prerequisites)
        {
            ContractRules.Defined(prerequisite.Kind);
            Fact(prerequisite.Fact);
        }
    }
}
