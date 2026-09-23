using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;
using Martlet.Core.Contracts;

namespace Martlet.HostArtifacts;

public sealed class InspectionReport
{
    private readonly ReportDocument document;
    internal InspectionReport(ReportDocument document) => this.document = document;
    public int ExitCode => document.ExitCode;
    public string? DocumentSha256 => document.DocumentSha256;
    public long? KnownPayloadBytes => document.Disk?.KnownListedPayloadBytes;
    public bool ExecutionEligible => false;
    public string ToJson() => Encoding.UTF8.GetString(ContractJson.Write(document));

    public string ToHuman()
    {
        var lines = new List<string>
        {
            "Host artifact metadata inspection - NOT installation, download consent or host qualification.",
            $"Result: {document.Disposition}; exit {document.ExitCode}. Execution eligible: false. Payload verified: false.",
            $"Document SHA-256 (local input bytes only): {document.DocumentSha256 ?? "not available"}.",
            $"Supplied inventory provenance: {document.InventoryProvenance ?? "not evaluated"}. Source assertions are not authenticated."
        };
        foreach (var role in document.Roles)
            lines.Add($"{role.Id}: {role.TargetComparison}; missing components: " +
                (role.MissingComponents.Length == 0 ? "none in listed subset" : string.Join(", ", role.MissingComponents)));
        foreach (var source in document.Sources)
            lines.Add($"Source {source.Id}: {source.Repository}, Git commit {source.Revision} (supplied identity).");
        foreach (var runtime in document.Runtimes)
        {
            lines.Add($"Runtime {runtime.Id}: {runtime.Family} {runtime.UpstreamVersion}, declared target {runtime.Target}; dependency closure not established.");
            foreach (var dependency in runtime.Dependencies)
                lines.Add($"  Dependency {dependency.Name} ({dependency.Ecosystem}; {dependency.Condition}): " +
                    (dependency.Constraints.Length == 0 ? "no version constraint supplied" :
                        string.Join(", ", dependency.Constraints.Select(c => $"{c.Comparison} {c.Version}"))) +
                    "; declaration only, not resolved.");
        }
        foreach (var license in document.Licenses)
            lines.Add($"License {license.Id}: {license.Spdx}, scope {license.Scope}, source {license.SourceId}; unreviewed.");
        foreach (var artifact in document.Artifacts)
        {
            lines.Add(string.Create(CultureInfo.InvariantCulture,
                $"{artifact.Id}: {artifact.Bytes} declared bytes; payload SHA-256 {artifact.Sha256 ?? "unknown"} ({artifact.Sha256Evidence}); repository Git blob SHA-1 {artifact.GitBlobSha1 ?? "not supplied"}."));
        }
        foreach (var image in document.ContainerImages ?? [])
        {
            var metadata = image.Metadata;
            var platform = metadata.Platform is { } p
                ? $"{p.Os}/{p.Architecture}" + (p.Variant is null ? "" : "/" + p.Variant)
                : "unknown";
            lines.Add($"Image {metadata.Id}: {metadata.Registry}/{metadata.Repository}@{metadata.Digest}; " +
                $"index {metadata.Index?.Digest ?? "not supplied"}; platform {platform} ({image.PlatformComparison}); " +
                $"{metadata.Evidence}, not locally verified; dependencies and component rights unresolved.");
        }
        if (document.Disk?.ContainerContent is { } container)
            lines.Add(string.Create(CultureInfo.InvariantCulture,
                $"Unique container content: {container.KnownCompressedBytes} known compressed bytes ({container.UnknownCompressedCount} unknown sizes); " +
                $"{container.KnownExpandedBlobBytes} known expanded blob bytes ({container.UnknownExpandedBlobCount} unknown); " +
                $"{container.KnownStagingBlobBytes} known staging blob bytes ({container.UnknownStagingBlobCount} unknown). " +
                $"Incomplete image inventories: {container.IncompleteImageCount}. These are not installed snapshots or peak disk."));
        if (document.Disk is { } disk)
            lines.Add(string.Create(CultureInfo.InvariantCulture,
                $"Known listed payload subtotal: {disk.KnownListedPayloadBytes} bytes. Complete download, expanded/peak disk and free disk: unknown."));
        foreach (var prerequisite in document.Prerequisites)
            lines.Add($"Prerequisite {prerequisite.Id}: {prerequisite.Status}. Needed: {prerequisite.EvidenceNeeded}");
        foreach (var finding in document.Findings)
            lines.Add($"{finding.Code}: {finding.Summary} Remedy: {finding.Remedy}");
        return string.Join("\n", lines);
    }

    public static InspectionReport Failure(string code)
    {
        var finding = InspectionFindings.Get(code);
        var canceled = code is "inspection.canceled" or "inspection.input_timeout";
        return new(new()
        {
            ExitCode = canceled ? 2 : 3, Disposition = canceled ? "inspection_incomplete" : "invalid_input",
            StructuralConsistency = "not_evaluated", Findings = [finding]
        });
    }
}

internal sealed record ReportDocument : IContract
{
    public int FormatVersion { get; init; } = 1;
    public string Scope => "artifact_metadata_only";
    public required int ExitCode { get; init; }
    public required string Disposition { get; init; }
    public required string StructuralConsistency { get; init; }
    public string? DocumentSha256 { get; init; }
    public string DocumentHashEvidence => "locally_computed_input_bytes";
    public string? InventoryProvenance { get; init; }
    public bool SourceAssertionsAuthenticated => false;
    public bool ExecutionEligible => false;
    public bool DownloadAuthorized => false;
    public bool HostQualified => false;
    public bool PayloadVerified => false;
    public string GpuFit => "not_measured";
    public string RightsApproval => "not_reviewed";
    public string RuntimeDependencyClosure => "not_established";
    public string PrerequisiteObservations => "not_performed";
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? RequestedTarget { get; init; }
    public string? RequestedPlatform { get; init; }
    public RoleInspection[] Roles { get; init; } = [];
    public SourceDocument[] Sources { get; init; } = [];
    public RuntimeDocument[] Runtimes { get; init; } = [];
    public ArtifactDocument[] Artifacts { get; init; } = [];
    public ImageInspection[]? ContainerImages { get; init; }
    public LicenseDocument[] Licenses { get; init; } = [];
    public PrerequisiteInspection[] Prerequisites { get; init; } = [];
    public DiskInventory? Disk { get; init; }
    public required InspectionFinding[] Findings { get; init; }

    public void Validate()
    {
        ContractRules.Require(Findings.Length <= 128 && ExitCode is >= 1 and <= 3, "Invalid metadata report.");
        foreach (var finding in Findings)
        {
            ContractRules.Identifier(finding.Code);
            ContractRules.Text(finding.Summary, 512);
            ContractRules.Text(finding.Remedy, 512);
        }
        ContractRules.Require(Prerequisites.Length <= 16, "Too many prerequisite records.");
        foreach (var prerequisite in Prerequisites)
        {
            ContractRules.Identifier(prerequisite.Id);
            ContractRules.Text(prerequisite.EvidenceNeeded, 512);
        }
    }
}

internal sealed record RoleInspection(string Id, ProviderRole Role, RuntimeFamily Family, string RuntimeId,
    string DeclaredTarget, string TargetComparison, string[] ArtifactIds, ArtifactKind[] MissingComponents,
    long KnownListedPayloadBytes);

internal sealed record PrerequisiteInspection(string Id, string EvidenceNeeded)
{
    public string Status => "not_observed";
}

internal sealed record DiskInventory(long KnownListedPayloadBytes)
{
    public string Scope => "unique_listed_payload_subset_not_installation_estimate";
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public long? CompleteDownloadBytes => null;
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public long? ExpandedArchiveBytes => null;
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public long? PeakDiskBytes => null;
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public long? FreeDiskBytes => null;
    public ImageByteInventory? ContainerContent { get; init; }
}

internal sealed record ImageInspection(ContainerImageDocument Metadata, string PlatformComparison)
{
    public bool PayloadVerified => false;
    public string SourceBinding => "declared_recipe_not_build_attestation";
    public string IndexSelection => Metadata.Index is null ? "not_supplied" : "declared_not_verified";
    public string DependencyClosure => "not_established";
    public string RightsApproval => "not_reviewed";
}

internal sealed record ImageByteInventory(long KnownCompressedBytes, int UnknownCompressedCount,
    long KnownExpandedBlobBytes, int UnknownExpandedBlobCount, long KnownStagingBlobBytes, int UnknownStagingBlobCount,
    int UniqueContentCount, int IncompleteImageCount)
{
    public string Scope => "unique_content_metadata_not_snapshot_or_peak_disk";
    public string SizeEvidence => "supplied_metadata_not_locally_measured";
}

internal sealed record InspectionFinding(string Code, string Summary, string Remedy)
{
    public string ActionId => Code;
}

internal static class InspectionFindings
{
    internal static InspectionFinding Get(string code) => code switch
    {
        "manifest.too_large" => new(code, "The document exceeds 256 KiB.", "Select a smaller complete v1 or v2 document; no content was inspected."),
        "manifest.unsupported_version" => new(code, "The artifact document version is unsupported.", "Use a reviewed format-version 1 or 2 document and a matching reader."),
        "manifest.invalid_json" => new(code, "JSON is malformed or has missing, null or unsupported fields/values.", "Use the exact bounded versioned contract; keep the original document for local review."),
        "manifest.identifier_invalid" => new(code, "An identifier is noncanonical.", "Use bounded lowercase ASCII IDs; do not rely on normalization or case aliases."),
        "manifest.bounds_invalid" => new(code, "The inventory exceeds its count or value bounds.", "Review the documented versioned limits and provide a bounded complete subset."),
        "manifest.alias_invalid" => new(code, "Duplicate or ambiguous inventory identities were supplied.", "Represent a shared source or artifact once and reference its unique ID."),
        "manifest.reference_missing" => new(code, "A referenced source, license, runtime or artifact is missing.", "Supply the referenced record or an explicit missing component slot, not a dangling reference."),
        "manifest.role_invalid" => new(code, "Role, runtime, component slots or selected closure disagree.", "Use the required typed family slots and exact artifact kinds; aliases cannot replace role requirements."),
        "manifest.license_invalid" => new(code, "License scope or evidence does not match its subject.", "Keep source code, model repositories and bundled archive components separate and unreviewed."),
        "manifest.dependency_invalid" => new(code, "Declared dependency constraints or required component edges are inconsistent.", "Restore the required dependency relationships; a listed subset is not a resolved runtime lock."),
        "manifest.dependency_cycle" => new(code, "The artifact graph contains a cycle.", "Provide a directed acyclic dependency graph, including all transitive references."),
        "manifest.unreachable" => new(code, "The document contains unreferenced inventory records.", "Remove unrelated records or bind them explicitly to the role inventory."),
        "artifact.pin_invalid" => new(code, "Artifact identity, hash or exact byte count is invalid.", "Use immutable commits and exact positive metadata sizes; distinguish payload hashes from Git object IDs."),
        "artifact.path_invalid" => new(code, "An upstream relative path is unsafe or noncanonical.", "Use bounded ASCII relative paths without traversal, encoding aliases or device names."),
        "artifact.source_invalid" => new(code, "A source URL does not match the exact supported origin/repository/revision/path.", "Use the canonical pinned locator; credentials, queries, redirects and alternate origins are unsupported."),
        "image.reference_invalid" => new(code, "An image registry, repository or digest is noncanonical or mutable.", "Use an explicit DNS registry, lowercase repository and nonzero sha256 digest; tags and implicit registries are unsupported."),
        "image.platform_invalid" => new(code, "An image platform is malformed.", "Use canonical lowercase OS/architecture[/variant] tokens or explicit null for an unknown declaration."),
        "image.evidence_invalid" => new(code, "Synthetic image facts were labeled as upstream inventory.", "Keep authored synthetic metadata in a synthetic_fixture document; it is not upstream evidence."),
        "image.inventory_invalid" => new(code, "An image blob inventory has inconsistent configuration declarations.", "Supply at most one configuration blob, exactly one when claiming a complete listed blob inventory."),
        "image.facts_conflict" => new(code, "Repeated content digests have conflicting kind or byte facts.", "Use identical facts for shared content, including explicit unknown sizes; do not choose one conflicting observation."),
        "image.platform_unknown" => new(code, "The image platform or requested platform is unknown.", "Supply --platform and a reviewed selected-image platform; target labels alone do not establish OCI compatibility."),
        "image.platform_mismatch" => new(code, "A selected image platform differs from the requested platform.", "Select matching immutable platform metadata; no host observation or override is performed."),
        "image.metadata_unverified" => new(code, "Image digests and byte facts are supplied metadata, not verified content.", "Separately authorized acquisition must verify manifests, blobs and runtime closure; parsing authorizes nothing."),
        "image.index_selection_unverified" => new(code, "The index-to-platform-image relationship is only declared.", "Verify the index descriptor and selected manifest separately; the index digest is not the platform-image digest."),
        "image.inventory_incomplete" => new(code, "At least one image has an explicitly incomplete blob inventory.", "Resolve missing configuration/layer descriptors before any future download or disk plan."),
        "inspection.invalid_invocation" => new(code, "The command or selection is invalid.", "Use --help, one explicit absolute local JSON document, and optional exact role/target IDs."),
        "inspection.input_unreadable" => new(code, "The selected document could not be read.", "Select an accessible local regular JSON file; no path, file content or settings were logged or changed."),
        "inspection.input_disallowed" => new(code, "The selected input is not an allowed local regular document.", "Use a direct local JSON file, not a network/device/alternate-stream path or link/reparse ancestor."),
        "inspection.canceled" => new(code, "Inspection was canceled; no completed inventory is reported.", "Run a new explicit inspection when ready; cancellation grants no permission."),
        "inspection.input_timeout" => new(code, "The bounded document read exceeded its deadline.", "Use a responsive local regular file and retry explicitly."),
        "candidate.disabled" => new(code, "Every candidate is disabled; metadata consistency is not installability.", "Complete the separate runtime, rights, host and fit gates before any future enabling decision."),
        "artifact.component_missing" => new(code, "Required family component slots are missing.", "Review each role's missing_components, including unselected models/runtime payloads; do not substitute defaults."),
        "artifact.content_pin_missing" => new(code, "Some listed files lack a published payload SHA-256.", "Obtain qualifying content evidence through separately authorized work; Git blob SHA-1 is not plain-file SHA-256."),
        "artifact.payload_unverified" => new(code, "No listed payload was downloaded or verified.", "Future authorized acquisition must verify actual bytes; this report is neither acquisition nor consent."),
        "runtime.image_unpinned" => new(code, "No selected immutable runtime image is established.", "H02 must select and review an exact image digest and its full component inventory."),
        "runtime.dependencies_unresolved" => new(code, "Declared dependencies are not a resolved native/transitive runtime closure.", "Resolve the complete version/platform/artifact/license graph in H02; do not execute these declarations."),
        "runtime.compatibility_unknown" => new(code, "Framework, CUDA, driver, CPU-feature and runtime compatibility are unverified.", "Record the exact selected runtime and actual host evidence before qualified local execution."),
        "license.review_required" => new(code, "Upstream license declarations are not use or distribution approval.", "Review exact code, weights, bundled components and intended use; no Martlet license grant is supplied."),
        "license.noncommercial_review_required" => new(code, "A selected model declares CC-BY-NC-4.0.", "Resolve intended use and noncommercial/attribution restrictions with the rights owner before enabling or distributing."),
        "voice.rights_required" => new(code, "Reference-voice rights and consent are not established.", "Obtain separate consent for the chosen reference voice, transcript, storage and processing."),
        "voice.transcript_required" => new(code, "The F5 candidate requires supplied reference text; automatic Whisper is excluded.", "H04 must reject a blank reference transcript or separately provision/review its full ASR dependency path."),
        "host.prerequisites_unverified" => new(code, "OS, CPU/RAM, storage, driver and container/toolkit prerequisites were not observed.", "Use separately authorized H01 observations; do not infer host facts from a target selector or source recipe."),
        "gpu.fit_unknown" => new(code, "GPU model/VRAM/kernel support and combined-role fit are not measured.", "H06 must measure an exact selected host/runtime/model tuple; file size is not a VRAM estimate."),
        "disk.total_unknown" => new(code, "Only listed payload bytes are known; complete download and expanded/peak disk are unknown.", "Resolve missing artifacts and measure expansion/staging/rollback/data reserves before a future disk plan."),
        "target.unknown" => new(code, "No target was supplied; no platform comparison was performed.", "Supply a target selector for declared metadata comparison only, not host discovery."),
        "target.mismatch" => new(code, "The requested target differs from at least one selected candidate declaration.", "Select an appropriate declared candidate; a mismatch cannot be repaired by a readiness override."),
        _ => throw new ArgumentException("Unknown inspection diagnostic code.", nameof(code))
    };
}
