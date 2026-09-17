using System.Globalization;
using System.Text.Json;

namespace Martlet.Updates;

internal static class PayloadSbom
{
    private const string Root = "martlet-internal-payload";
    private const string FileLicense = "NOT ASSESSED - no file-level license conclusion";
    private const string ProjectOrigin = "Project build output; not a copy of source bytes";
    private const string GeneratedOrigin = "SDK-generated publish output; not an unchanged archive asset";
    private const string ArchiveOrigin = "Verified upstream archive entry";
    private const string DocumentOrigin = "Document; source or upstream ownership not inferred";

    internal static void Verify(byte[] bytes, string version, PayloadFile[] files,
        PayloadProvenance.Facts facts, CancellationToken token)
    {
        using var document = Wire.ReadDocument(bytes, PayloadMetadata.MaximumV2Bytes, token);
        var r = new EvidenceReader(token);
        var root = document.RootElement;
        r.Keys(root, "bomFormat specVersion version metadata components dependencies compositions");
        r.Equal(r.Text(root, "bomFormat"), "CycloneDX");
        if (r.Text(root, "specVersion", 16) != "1.6") throw new StagingException(StagingFailure.IncompatibleFormat);
        r.Schema(r.Member(root, "version"), 1);
        Metadata(r.Member(root, "metadata"), version, facts, r);
        var payload = files.Where(f => f.Path != "sbom.cdx.json").ToDictionary(f => f.Path, StringComparer.Ordinal);
        var subjects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var software = new HashSet<string>(StringComparer.Ordinal);
        var owners = new Dictionary<string, string>(StringComparer.Ordinal);
        var origins = facts.Archives.Values.SelectMany(a => a.Origins).ToDictionary(o => o.Path, StringComparer.Ordinal);
        foreach (var library in facts.Libraries.Values)
        {
            IEnumerable<string> paths = library.Type == "project"
                ? library.Assets.Select(a => a.Path)
                : origins.Values.Where(o => o.Component == library.Reference).Select(o => o.Path);
            if (library.IsRoot) paths = paths.Concat(Generated(library.Context));
            foreach (var path in paths) r.Require(owners.TryAdd(path, library.Reference));
        }
        void File(JsonElement file, string? owner)
        {
            r.Keys(file, "type bom-ref name hashes properties");
            r.Equal(r.Text(file, "type", 16), "file");
            var name = r.Text(file, "name");
            var path = name.Replace('/', '\\');
            r.Require(payload.TryGetValue(path, out var expected));
            r.Equal(name, expected!.Path.Replace('\\', '/'));
            r.Equal(r.Text(file, "bom-ref", 1029), "file:" + name);
            r.Require(subjects.Add(path));
            r.Require(owners.TryGetValue(path, out var declaredOwner) ? declaredOwner == owner : owner is null);
            var hashes = r.Items(r.Member(file, "hashes"), 1, 1).Single();
            r.Keys(hashes, "alg content");
            r.Equal(r.Text(hashes, "alg"), "SHA-256");
            r.Equal(r.Hex(r.Member(hashes, "content")), expected.Sha256);
            var props = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["martlet:payload:path"] = path,
                ["martlet:payload:bytes"] = expected.Bytes.ToString(CultureInfo.InvariantCulture),
                ["martlet:license:status"] = FileLicense,
                ["martlet:origin:classification"] = owner is null ? DocumentOrigin : ProjectOrigin
            };
            if (origins.TryGetValue(path, out var origin))
            {
                props["martlet:origin:classification"] = ArchiveOrigin;
                props["martlet:nuget:archive-entry"] = origin.Entry;
            }
            else if (owner is not null && Generated(facts.Libraries[owner].Context).Contains(path, StringComparer.Ordinal))
                props["martlet:origin:classification"] = GeneratedOrigin;
            Properties(r.Member(file, "properties"), props, r);
        }
        foreach (var component in r.Items(r.Member(root, "components"), payload.Count + facts.Libraries.Count))
        {
            if (r.Text(component, "type", 16) == "file") { File(component, null); continue; }
            var reference = r.Text(component, "bom-ref", 264);
            r.Require(facts.Libraries.TryGetValue(reference, out var library) && software.Add(reference));
            Software(component, library!, facts, r);
            var nested = r.Items(r.Member(component, "components"), payload.Count).ToArray();
            foreach (var file in nested) File(file, reference);
        }
        r.Require(subjects.Count == payload.Count && software.Count == facts.Libraries.Count);
        var references = facts.Libraries.Keys.Append(Root).ToHashSet(StringComparer.Ordinal);
        var dependencies = new HashSet<string>(StringComparer.Ordinal);
        foreach (var dependency in r.Items(r.Member(root, "dependencies"), references.Count, references.Count))
        {
            r.Keys(dependency, "ref dependsOn");
            var reference = r.Text(dependency, "ref", 264);
            r.Require(references.Contains(reference) && dependencies.Add(reference));
            var edges = r.Edges(r.Member(dependency, "dependsOn"), references);
            var expected = reference == Root
                ? new[] { "Desktop", "Doctor" }.Select(c => facts.Libraries.Values.Single(l => l.Context == c && l.IsRoot).Reference)
                : facts.Libraries[reference].Dependencies.Select(d => facts.Libraries[reference].Context + "|" + d);
            r.Require(edges.SequenceEqual(expected));
        }
        var composition = r.Items(r.Member(root, "compositions"), 1, 1).Single();
        r.Keys(composition, "aggregate assemblies dependencies");
        r.Equal(r.Text(composition, "aggregate"), "incomplete");
        foreach (var name in new[] { "assemblies", "dependencies" })
            r.Equal(r.Text(r.Items(r.Member(composition, name), 1, 1).Single()), Root);
    }

    private static string[] Generated(string context) =>
        [$"{context}\\Martlet.{context}.exe", $"{context}\\Martlet.{context}.deps.json", $"{context}\\Martlet.{context}.runtimeconfig.json"];

    private static void Metadata(JsonElement value, string version, PayloadProvenance.Facts facts, EvidenceReader r)
    {
        r.Keys(value, "component properties tools");
        var component = r.Member(value, "component");
        r.Keys(component, "type bom-ref name version");
        r.Equal(r.Text(component, "type"), "application");
        r.Equal(r.Text(component, "bom-ref"), Root);
        r.Equal(r.Text(component, "name"), "Martlet INTERNAL UNSIGNED payload");
        r.Equal(r.Text(component, "version", 48), version);
        var props = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["martlet:assurance"] = PayloadProvenance.Assurance,
            ["martlet:source:commit"] = facts.Source,
            ["martlet:source:tree"] = facts.Tree,
            ["martlet:source:dirty"] = facts.Dirty ? "true" : "false",
            ["martlet:source:inputs-sha256"] = facts.SourceHash,
            ["martlet:scope"] = "Actual packaged files and resolved .NET publish dependencies; upstream vendored/native internals and OS dependencies are not fully decomposed.",
            ["martlet:excluded-metadata"] = "sbom.cdx.json, manifest.json, SHA256SUMS.txt (avoids circular hashes)",
            ["martlet:reproducibility"] = "Canonical metadata only; no hermetic-build or reproducible-binary claim."
        };
        Properties(r.Member(value, "properties"), props, r, declaredRestoreHash: true);
        var tools = r.Member(value, "tools");
        r.Keys(tools, "components");
        var entries = r.Items(r.Member(tools, "components"), 2, 2).ToArray();
        r.Keys(entries[0], "type name version");
        r.Equal(r.Text(entries[0], "type"), "application");
        r.Equal(r.Text(entries[0], "name"), "Martlet Windows packaging");
        r.Equal(r.Text(entries[0], "version", 40), facts.Source);
        r.Keys(entries[1], "type name version properties");
        r.Equal(r.Text(entries[1], "type"), "application");
        r.Equal(r.Text(entries[1], "name"), ".NET SDK");
        r.Equal(r.Text(entries[1], "version", 48), facts.Sdk);
        var sdk = facts.Tools.ToDictionary(f => "martlet:tool:sha256:" + f.Path, f => f.Hash!, StringComparer.Ordinal);
        sdk.Add("martlet:tool:observation", PayloadProvenance.Observation);
        Properties(r.Member(entries[1], "properties"), sdk, r);
    }

    private static void Software(JsonElement value, PayloadProvenance.Library library, PayloadProvenance.Facts facts,
        EvidenceReader r)
    {
        var props = new Dictionary<string, string>(StringComparer.Ordinal) { ["martlet:publish:context"] = library.Context };
        var extra = "";
        PayloadProvenance.Archive? archive = null;
        if (library.Type == "project") props["martlet:license:status"] = "UNKNOWN - no project license granted";
        else
        {
            var id = library.Type == "runtimepack" ? library.Id["runtimepack.".Length..] : library.Id;
            archive = facts.Archives[id];
            extra = " purl";
            props["martlet:nuget:archive-sha512"] = archive.Hash;
            props["martlet:nuget:nuspec-sha256"] = archive.Nuspec;
            props["martlet:license:status"] = "UPSTREAM DECLARATION ONLY - rights not assessed";
            if (library.ContentHash is { } hash) props["martlet:nuget:lock-content-hash"] = hash;
            if (!string.IsNullOrEmpty(archive.LicenseFile)) props["martlet:nuget:license-file"] = archive.LicenseFile;
            if (!string.IsNullOrEmpty(archive.LicenseExpression)) extra += " licenses";
            if (!string.IsNullOrEmpty(archive.RepositoryUrl))
            {
                extra += " externalReferences";
                if (!string.IsNullOrEmpty(archive.RepositoryCommit))
                    props["martlet:nuget:declared-repository-commit"] = archive.RepositoryCommit;
            }
            r.Equal(r.Text(value, "purl", 280), $"pkg:nuget/{archive.Id.ToLowerInvariant()}@{archive.Version}");
        }
        r.Keys(value, "type bom-ref name version properties components" + extra);
        r.Equal(r.Text(value, "type", 16), library.IsRoot ? "application" : library.Type == "runtimepack" ? "framework" : "library");
        r.Equal(r.Text(value, "name", 256), library.Id);
        r.Equal(r.Text(value, "version", 256), library.Version);
        Properties(r.Member(value, "properties"), props, r);
        if (archive is not null && !string.IsNullOrEmpty(archive.LicenseExpression))
        {
            var license = r.Items(r.Member(value, "licenses"), 1, 1).Single();
            r.Keys(license, "expression acknowledgement");
            r.Equal(r.Text(license, "expression"), archive.LicenseExpression);
            r.Equal(r.Text(license, "acknowledgement"), "declared");
        }
        if (archive is not null && !string.IsNullOrEmpty(archive.RepositoryUrl))
        {
            var reference = r.Items(r.Member(value, "externalReferences"), 1, 1).Single();
            r.Keys(reference, "type url");
            r.Equal(r.Text(reference, "type"), "vcs");
            r.Equal(r.Text(reference, "url"), archive.RepositoryUrl);
        }
    }

    private static void Properties(JsonElement value, Dictionary<string, string> expected, EvidenceReader r,
        bool declaredRestoreHash = false)
    {
        var maximum = expected.Count + (declaredRestoreHash ? 1 : 0);
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in r.Items(value, maximum, maximum))
        {
            r.Keys(property, "name value");
            var name = r.Text(property, "name", 1048);
            r.Require(names.Add(name));
            if (declaredRestoreHash && name == "martlet:resolved-evidence:sha256")
                r.Hex(r.Member(property, "value"));
            else
            {
                r.Require(expected.TryGetValue(name, out var expectedValue));
                r.Equal(r.Text(property, "value"), expectedValue!);
            }
        }
    }
}
