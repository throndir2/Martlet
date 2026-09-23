using Martlet.Core.Contracts;
using static Martlet.HostArtifacts.ArtifactSourceRules;

namespace Martlet.HostArtifacts;

internal static class ArtifactManifestValidator
{
    internal static ArtifactKind[] RequiredComponents(RuntimeFamily family, ArtifactKind runtimeKind) => family == RuntimeFamily.Ollama
        ? [runtimeKind, ArtifactKind.LlmWeights]
        : [runtimeKind, ArtifactKind.TtsWeights, ArtifactKind.Vocabulary,
            ArtifactKind.VocoderWeights, ArtifactKind.VocoderConfiguration];

    internal static void Validate(ManifestDocument doc)
    {
        Require(doc.FormatVersion is 1 or 2, "manifest.unsupported_version");
        Require(doc.FormatVersion == 1 ? doc.ContainerImages is null : doc.ContainerImages is not null, "manifest.invalid_json");
        Require(doc.Kind == "host_artifact_candidate_lock" && doc.Scope == "metadata_only", "manifest.invalid_json");
        Id(doc.Id);
        var sources = Index(doc.Sources, 8, s => s.Id);
        var runtimes = Index(doc.Runtimes, 4, r => r.Id);
        var artifacts = Index(doc.Artifacts, 64, a => a.Id, allowEmpty: true);
        var images = Index(doc.ContainerImages ?? [], 4, a => a.Id, allowEmpty: true);
        Require(artifacts.Count + images.Count <= 64, "manifest.bounds_invalid");
        var nodes = Nodes(doc);
        var licenses = Index(doc.Licenses, 16, l => l.Id);
        var roles = Index(doc.Roles, 4, r => r.Id);
        var sourceKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in sources.Values)
        {
            Source(source);
            Require(sourceKeys.Add($"{source.Kind}:{source.Repository}:{source.Revision}"), "manifest.alias_invalid");
        }
        foreach (var license in licenses.Values)
        {
            var source = Reference(sources, license.SourceId);
            Require(license.Spdx is "MIT" or "CC-BY-NC-4.0" or "unknown", "manifest.license_invalid");
            Require(license.Scope == LicenseScope.ModelRepository ? source.Kind == SourceKind.HuggingFace :
                source.Kind == SourceKind.Github, "manifest.license_invalid");
            Require(doc.FormatVersion != 1 || license.Scope != LicenseScope.ContainerImageComponents, "manifest.license_invalid");
            Require(license.Scope is not (LicenseScope.RuntimeArchiveComponents or LicenseScope.ContainerImageComponents) || license.Spdx == "unknown",
                "manifest.license_invalid");
            if (license.Spdx == "unknown")
                Require(license.EvidencePath is null && license.EvidenceUrl is null, "manifest.license_invalid");
            else
            {
                Require(license.EvidencePath is not null && license.EvidenceUrl is not null, "manifest.license_invalid");
                ExactUrl(license.EvidenceUrl!, Evidence(source, license.EvidencePath!));
            }
        }
        var dependencyCount = 0;
        foreach (var runtime in runtimes.Values)
        {
            var source = Reference(sources, runtime.SourceId);
            Require(source.Kind == SourceKind.Github &&
                runtime.Role == (runtime.Family == RuntimeFamily.Ollama ? ProviderRole.Llm : ProviderRole.Tts),
                "manifest.role_invalid");
            Require(source.Repository == (runtime.Family == RuntimeFamily.Ollama ? "ollama/ollama" : "SWivid/F5-TTS"),
                "manifest.role_invalid");
            Version(runtime.UpstreamVersion);
            Id(runtime.Target);
            var license = Reference(licenses, runtime.LicenseId);
            Require(license.Scope == LicenseScope.SourceCode && license.SourceId == runtime.SourceId, "manifest.license_invalid");
            ExactUrl(runtime.DependencyEvidenceUrl, Evidence(source, runtime.DependencyEvidencePath));
            var dependencies = Index(runtime.Dependencies, 128, d => d.Name, allowEmpty: true);
            Require(runtime.Unresolved.Length <= 4 && runtime.Unresolved.Distinct().Count() == runtime.Unresolved.Length,
                "manifest.bounds_invalid");
            dependencyCount += dependencies.Count + runtime.Unresolved.Length;
            foreach (var dependency in dependencies.Values)
            {
                Require(dependency.Name.Split('-').All(part => part.Length != 0 && part.All(char.IsAsciiLetterOrDigit)),
                    "manifest.identifier_invalid");
                Require(dependency.Constraints.Length <= 4 &&
                    dependency.Constraints.All(c => c is not null) &&
                    dependency.Constraints.Select(c => c.Comparison).Distinct().Count() == dependency.Constraints.Length,
                    "manifest.dependency_invalid");
                ValidateConstraints(dependency.Constraints);
                Require(dependency.Condition == DependencyCondition.Always || dependency.Ecosystem == DependencyEcosystem.Python,
                    "manifest.dependency_invalid");
            }
        }
        Require(dependencyCount <= 128, "manifest.bounds_invalid");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var hashes = new HashSet<string>(StringComparer.Ordinal);
        var releaseAssets = new HashSet<(string Repository, long AssetId)>();
        var hfLfsPointers = new Dictionary<string, (string PayloadSha256, long PayloadBytes)>(StringComparer.Ordinal);
        long total = 0;
        var edgeCount = 0;
        foreach (var artifact in artifacts.Values)
        {
            var source = Reference(sources, artifact.SourceId);
            Artifact(artifact, source);
            if (source.Kind == SourceKind.HuggingFace && artifact.Sha256Evidence == HashEvidence.HuggingFaceLfsMetadata)
            {
                // A pointer blob identifies a payload declaration, not a blob with the payload's byte length.
                var declaration = (artifact.Sha256!, artifact.Bytes);
                Require(!hfLfsPointers.TryGetValue(artifact.GitBlobSha1!, out var previous) || previous == declaration,
                    "artifact.pin_invalid");
                hfLfsPointers[artifact.GitBlobSha1!] = declaration;
            }
            if (artifact.Release is { } release)
                Require(releaseAssets.Add((source.Repository.ToLowerInvariant(), release.AssetId)), "manifest.alias_invalid");
            Require(paths.Add($"{source.Kind}:{source.Repository}:{source.Revision}:{artifact.Path}"), "manifest.alias_invalid");
            Require(artifact.Sha256 is null || hashes.Add(artifact.Sha256), "manifest.alias_invalid");
            total = checked(total + artifact.Bytes);
            Require(total <= 70_368_744_177_664L, "manifest.bounds_invalid");
            UniqueReferences(artifact.DependsOn, 16, nodes, allowEmpty: true);
            UniqueReferences(artifact.LicenseIds, 16, licenses);
            edgeCount += artifact.DependsOn.Length;
            Require(edgeCount <= 256, "manifest.bounds_invalid");
            foreach (var licenseId in artifact.LicenseIds)
            {
                var license = licenses[licenseId];
                Require(license.SourceId == artifact.SourceId && license.Scope ==
                    (artifact.Kind == ArtifactKind.RuntimeArchive ? LicenseScope.RuntimeArchiveComponents : LicenseScope.ModelRepository),
                    "manifest.license_invalid");
            }
        }
        ContainerImageRules.Validate(doc, images.Values, artifacts.Values);
        foreach (var image in images.Values)
        {
            Require(Reference(sources, image.SourceId).Kind == SourceKind.Github, "artifact.source_invalid");
            UniqueReferences(image.DependsOn, 16, nodes, allowEmpty: true);
            UniqueReferences(image.LicenseIds, 16, licenses);
            edgeCount += image.DependsOn.Length;
            Require(edgeCount <= 256, "manifest.bounds_invalid");
            foreach (var licenseId in image.LicenseIds)
            {
                var license = licenses[licenseId];
                Require(license.SourceId == image.SourceId && license.Scope == LicenseScope.ContainerImageComponents,
                    "manifest.license_invalid");
            }
        }
        DetectCycles(nodes);
        var reachable = new HashSet<string>(StringComparer.Ordinal);
        var usedRuntimes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var role in roles.Values)
        {
            var runtime = Reference(runtimes, role.RuntimeId);
            Require(usedRuntimes.Add(runtime.Id) && role.Role == runtime.Role && role.Target == runtime.Target, "manifest.role_invalid");
            UniqueReferences(role.RootArtifactIds, 16, nodes, allowEmpty: true);
            Require(role.Components.All(c => c is not null), "manifest.role_invalid");
            var runtimeKind = role.Components.Any(c => c.Kind == ArtifactKind.ContainerImage)
                ? ArtifactKind.ContainerImage : ArtifactKind.RuntimeArchive;
            Require(doc.FormatVersion != 1 || runtimeKind == ArtifactKind.RuntimeArchive, "manifest.role_invalid");
            Require(role.Components.Length == RequiredComponents(runtime.Family, runtimeKind).Length &&
                role.Components.All(c => c is not null), "manifest.role_invalid");
            var components = role.Components.ToDictionarySafe(c => c.Kind);
            Require(RequiredComponents(runtime.Family, runtimeKind).All(components.ContainsKey), "manifest.role_invalid");
            var bound = new HashSet<string>(StringComparer.Ordinal);
            foreach (var component in components.Values)
            {
                if (component.ArtifactId is null) continue;
                var artifact = Reference(nodes, component.ArtifactId);
                Require(bound.Add(artifact.Id) && artifact.Kind == component.Kind, "manifest.role_invalid");
                if (artifact.Kind == ArtifactKind.RuntimeArchive)
                    Require(artifact.SourceId == runtime.SourceId && artifacts[artifact.Id].Release!.Tag == "v" + runtime.UpstreamVersion,
                        "manifest.role_invalid");
                if (artifact.Kind == ArtifactKind.ContainerImage)
                    Require(artifact.SourceId == runtime.SourceId, "manifest.role_invalid");
            }
            var closure = Closure(role.RootArtifactIds, nodes);
            Require(bound.SetEquals(closure), "manifest.role_invalid");
            if (runtime.Family == RuntimeFamily.F5Tts)
            {
                RequireEdge(ArtifactKind.TtsWeights, ArtifactKind.Vocabulary);
                RequireEdge(ArtifactKind.TtsWeights, ArtifactKind.VocoderWeights);
                RequireEdge(ArtifactKind.VocoderWeights, ArtifactKind.VocoderConfiguration);
            }
            else RequireEdge(ArtifactKind.LlmWeights, runtimeKind);
            reachable.UnionWith(closure);

            void RequireEdge(ArtifactKind from, ArtifactKind to)
            {
                if (components[from].ArtifactId is { } parent && components[to].ArtifactId is { } child)
                    Require(nodes[parent].DependsOn.Contains(child, StringComparer.Ordinal), "manifest.dependency_invalid");
            }
        }
        Require(reachable.Count == nodes.Count && usedRuntimes.Count == runtimes.Count, "manifest.unreachable");
        var usedLicenses = runtimes.Values.Select(r => r.LicenseId)
            .Concat(nodes.Values.SelectMany(a => a.LicenseIds)).ToHashSet(StringComparer.Ordinal);
        var usedSources = runtimes.Values.Select(r => r.SourceId).Concat(nodes.Values.Select(a => a.SourceId))
            .Concat(licenses.Values.Select(l => l.SourceId)).ToHashSet(StringComparer.Ordinal);
        Require(usedLicenses.Count == licenses.Count && usedSources.Count == sources.Count, "manifest.unreachable");
    }

    internal static Dictionary<string, ArtifactNode> Nodes(ManifestDocument doc) =>
        doc.Artifacts.Select(a => new ArtifactNode(a.Id, a.Kind, a.SourceId, a.LicenseIds, a.DependsOn))
            .Concat((doc.ContainerImages ?? []).Select(a =>
                new ArtifactNode(a.Id, ArtifactKind.ContainerImage, a.SourceId, a.LicenseIds, a.DependsOn)))
            .ToDictionarySafe(a => a.Id);

    internal static HashSet<string> Closure(IEnumerable<string> roots, IReadOnlyDictionary<string, ArtifactNode> artifacts)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>(roots);
        while (pending.TryPop(out var id))
            if (result.Add(id))
                foreach (var dependency in artifacts[id].DependsOn) pending.Push(dependency);
        return result;
    }

    private static void ValidateConstraints(VersionConstraint[] constraints)
    {
        var versions = new Dictionary<VersionComparison, System.Version>();
        foreach (var constraint in constraints)
        {
            Version(constraint.Version);
            Require(System.Version.TryParse(constraint.Version, out var version), "manifest.dependency_invalid");
            versions.Add(constraint.Comparison, new(version!.Major, version.Minor,
                Math.Max(0, version.Build), Math.Max(0, version.Revision)));
        }
        Require(!versions.ContainsKey(VersionComparison.Exact) || versions.Count == 1, "manifest.dependency_invalid");
        Require(!versions.ContainsKey(VersionComparison.AtLeast) || !versions.ContainsKey(VersionComparison.GreaterThan),
            "manifest.dependency_invalid");
        var lowerKind = versions.ContainsKey(VersionComparison.GreaterThan) ? VersionComparison.GreaterThan : VersionComparison.AtLeast;
        if (versions.TryGetValue(lowerKind, out var lower) && versions.TryGetValue(VersionComparison.AtMost, out var upper))
            Require(lower < upper || lower == upper && lowerKind == VersionComparison.AtLeast, "manifest.dependency_invalid");
    }

    private static Dictionary<string, T> Index<T>(T[] values, int maximum, Func<T, string> id, bool allowEmpty = false)
    {
        Require(values.Length <= maximum && (allowEmpty || values.Length != 0) && values.All(v => v is not null),
            "manifest.bounds_invalid");
        var result = new Dictionary<string, T>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            Id(id(value));
            Require(result.TryAdd(id(value), value), "manifest.alias_invalid");
        }
        return result;
    }

    private static Dictionary<TKey, T> ToDictionarySafe<T, TKey>(this IEnumerable<T> values, Func<T, TKey> key) where TKey : notnull
    {
        var result = new Dictionary<TKey, T>();
        foreach (var value in values) Require(result.TryAdd(key(value), value), "manifest.alias_invalid");
        return result;
    }

    private static T Reference<T>(IReadOnlyDictionary<string, T> values, string id)
    {
        Id(id);
        Require(values.ContainsKey(id), "manifest.reference_missing");
        return values[id];
    }

    private static void UniqueReferences<T>(string[] ids, int maximum, IReadOnlyDictionary<string, T> values, bool allowEmpty = false)
    {
        Require(ids.Length <= maximum && (allowEmpty || ids.Length != 0) && ids.All(id => id is not null),
            "manifest.bounds_invalid");
        Require(ids.Distinct(StringComparer.Ordinal).Count() == ids.Length, "manifest.alias_invalid");
        foreach (var id in ids) Reference(values, id);
    }

    private static void DetectCycles(IReadOnlyDictionary<string, ArtifactNode> artifacts)
    {
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in artifacts.Keys) Visit(id);
        void Visit(string id)
        {
            if (visited.Contains(id)) return;
            Require(visiting.Add(id), "manifest.dependency_cycle");
            foreach (var child in artifacts[id].DependsOn) Visit(child);
            visiting.Remove(id);
            visited.Add(id);
        }
    }
}
