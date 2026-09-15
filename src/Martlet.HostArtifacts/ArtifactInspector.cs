using Martlet.Core.Contracts;

namespace Martlet.HostArtifacts;

public static class ArtifactInspector
{
    public static InspectionReport Inspect(ArtifactManifest manifest, string? roleId = null, string? target = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        try
        {
            if (roleId is not null) ArtifactSourceRules.Id(roleId);
            if (target is not null) ArtifactSourceRules.Id(target);
        }
        catch (ContractException) { throw new ArtifactManifestException("inspection.invalid_invocation"); }
        catch (ArtifactManifestException) { throw new ArtifactManifestException("inspection.invalid_invocation"); }
        var doc = manifest.Document;
        var roles = doc.Roles.Where(r => roleId is null || r.Id == roleId).OrderBy(r => r.Id, StringComparer.Ordinal).ToArray();
        if (roles.Length == 0) throw new ArtifactManifestException("inspection.invalid_invocation");
        var artifactMap = doc.Artifacts.ToDictionary(a => a.Id, StringComparer.Ordinal);
        var runtimeMap = doc.Runtimes.ToDictionary(r => r.Id, StringComparer.Ordinal);
        var selectedIds = new HashSet<string>(StringComparer.Ordinal);
        var inspectedRoles = new List<RoleInspection>();
        foreach (var role in roles)
        {
            var runtime = runtimeMap[role.RuntimeId];
            var closure = ArtifactManifestValidator.Closure(role.RootArtifactIds, artifactMap);
            selectedIds.UnionWith(closure);
            inspectedRoles.Add(new(role.Id, role.Role, runtime.Family, runtime.Id, role.Target,
                target is null ? "unknown" : target == role.Target ? "declared_match_not_qualified" : "declared_mismatch",
                closure.Order(StringComparer.Ordinal).ToArray(),
                role.Components.Where(c => c.ArtifactId is null).Select(c => c.Kind).Order().ToArray(),
                closure.Sum(id => artifactMap[id].Bytes)));
        }
        var artifacts = doc.Artifacts.Where(a => selectedIds.Contains(a.Id)).OrderBy(a => a.Id, StringComparer.Ordinal)
            .Select(a => a with { DependsOn = a.DependsOn.Order(StringComparer.Ordinal).ToArray(),
                LicenseIds = a.LicenseIds.Order(StringComparer.Ordinal).ToArray() }).ToArray();
        var runtimes = roles.Select(r => runtimeMap[r.RuntimeId]).OrderBy(r => r.Id, StringComparer.Ordinal)
            .Select(r => r with
            {
                Dependencies = r.Dependencies.OrderBy(d => d.Name, StringComparer.Ordinal)
                    .Select(d => d with { Constraints = d.Constraints.OrderBy(c => c.Comparison).ToArray() }).ToArray(),
                Unresolved = r.Unresolved.Order().ToArray()
            }).ToArray();
        var licenseIds = artifacts.SelectMany(a => a.LicenseIds).Concat(runtimes.Select(r => r.LicenseId)).ToHashSet(StringComparer.Ordinal);
        var licenses = doc.Licenses.Where(l => licenseIds.Contains(l.Id)).OrderBy(l => l.Id, StringComparer.Ordinal).ToArray();
        var sourceIds = artifacts.Select(a => a.SourceId).Concat(runtimes.Select(r => r.SourceId))
            .Concat(licenses.Select(l => l.SourceId)).ToHashSet(StringComparer.Ordinal);
        var mismatch = inspectedRoles.Any(r => r.TargetComparison == "declared_mismatch");
        var codes = new List<string>
        {
            "candidate.disabled", "artifact.payload_unverified", "runtime.image_unpinned",
            "runtime.dependencies_unresolved", "runtime.compatibility_unknown", "license.review_required",
            "host.prerequisites_unverified", "gpu.fit_unknown", "disk.total_unknown"
        };
        if (target is null) codes.Add("target.unknown");
        if (mismatch) codes.Add("target.mismatch");
        if (inspectedRoles.Any(r => r.MissingComponents.Length != 0)) codes.Add("artifact.component_missing");
        if (artifacts.Any(a => a.Sha256 is null)) codes.Add("artifact.content_pin_missing");
        if (licenses.Any(l => l.Spdx == "CC-BY-NC-4.0")) codes.Add("license.noncommercial_review_required");
        if (runtimes.Any(r => r.Family == RuntimeFamily.F5Tts))
            codes.AddRange(["voice.transcript_required", "voice.rights_required"]);
        var result = new InspectionReport(new()
        {
            ExitCode = mismatch ? 1 : 2, Disposition = mismatch ? "declared_target_mismatch" : "disabled_incomplete",
            StructuralConsistency = "consistent_metadata_only", DocumentSha256 = manifest.DocumentSha256,
            InventoryProvenance = doc.Provenance == MetadataProvenance.UpstreamMetadata ? "supplied_upstream_metadata" : "synthetic_fixture",
            RequestedTarget = target, Roles = inspectedRoles.ToArray(),
            Sources = doc.Sources.Where(s => sourceIds.Contains(s.Id)).OrderBy(s => s.Id, StringComparer.Ordinal).ToArray(),
            Runtimes = runtimes, Artifacts = artifacts, Licenses = licenses,
            Prerequisites = Prerequisites(runtimes.Any(r => r.Family == RuntimeFamily.F5Tts)),
            Disk = new(artifacts.Sum(a => a.Bytes)),
            Findings = codes.Order(StringComparer.Ordinal).Select(InspectionFindings.Get).ToArray()
        });
        // Ensure a valid maximum-size input cannot escape the bounded output contract.
        try { _ = result.ToJson(); }
        catch (ContractException) { throw new ArtifactManifestException("manifest.bounds_invalid"); }
        return result;
    }

    private static PrerequisiteInspection[] Prerequisites(bool f5)
    {
        var prerequisites = new List<PrerequisiteInspection>
        {
            new("host.os_kernel", "Exact Ubuntu version, architecture, kernel and reboot evidence from separately authorized H01 observations."),
            new("host.cpu_features", "Actual CPU architecture and required native instruction-set compatibility."),
            new("host.ram_swap", "Actual RAM/swap and concurrent runtime peak requirements."),
            new("host.storage", "Actual free disk/inodes, full artifact closure, expansion, staging, rollback and user-data reserves."),
            new("gpu.identity_vram", "Actual GPU UUID/model/VRAM and required compute capability."),
            new("gpu.driver", "Actual driver/module state and its compatibility with the selected runtime binaries."),
            new("host.container_tools", "Exact Docker Engine, Compose and NVIDIA Container Toolkit versions and authorized access."),
            new("runtime.framework_cuda", "Pinned framework/CUDA/native dependencies and their compatibility with the exact host tuple."),
            new("gpu.combined_fit", "Witnessed combined-role model fit, peak VRAM and actual inference; file sizes cannot establish fit.")
        };
        if (f5)
        {
            prerequisites.Add(new("voice.reference_rights", "Separate rights and consent for the chosen reference voice, storage and processing."));
            prerequisites.Add(new("voice.reference_transcript", "Supplied reference text; blank-reference automatic Whisper is outside this candidate."));
        }
        return prerequisites.OrderBy(p => p.Id, StringComparer.Ordinal).ToArray();
    }
}
