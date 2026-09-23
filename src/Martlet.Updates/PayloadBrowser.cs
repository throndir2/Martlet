using System.Text.Json;
using System.Text.RegularExpressions;

namespace Martlet.Updates;

internal static class PayloadBrowser
{
    internal const string Host = "Desktop\\AvatarRenderer";
    internal const string SourceRoot = "src\\Martlet.Avatar.RendererHost\\web\\";
    private const string PackageRoot = "src\\Martlet.Avatar.Vrm\\";
    internal const string Reference = "AvatarRenderer|browser";
    private static readonly Dictionary<string, string[]> PackageDependencies = new(StringComparer.Ordinal)
    {
        ["@esbuild/win32-x64"] = [],
        ["@pixiv/three-vrm"] = ["@pixiv/three-vrm-core", "@pixiv/three-vrm-materials-hdr-emissive-multiplier",
            "@pixiv/three-vrm-materials-mtoon", "@pixiv/three-vrm-materials-v0compat", "@pixiv/three-vrm-node-constraint",
            "@pixiv/three-vrm-springbone", "three"],
        ["@pixiv/three-vrm-core"] = ["@pixiv/types-vrm-0.0", "@pixiv/types-vrmc-vrm-1.0", "three"],
        ["@pixiv/three-vrm-materials-hdr-emissive-multiplier"] = ["@pixiv/types-vrmc-materials-hdr-emissive-multiplier-1.0", "three"],
        ["@pixiv/three-vrm-materials-mtoon"] = ["@pixiv/types-vrm-0.0", "@pixiv/types-vrmc-materials-mtoon-1.0", "three"],
        ["@pixiv/three-vrm-materials-v0compat"] = ["@pixiv/types-vrm-0.0", "@pixiv/types-vrmc-materials-mtoon-1.0", "three"],
        ["@pixiv/three-vrm-node-constraint"] = ["@pixiv/types-vrmc-node-constraint-1.0", "three"],
        ["@pixiv/three-vrm-springbone"] = ["@pixiv/types-vrm-0.0", "@pixiv/types-vrmc-springbone-1.0",
            "@pixiv/types-vrmc-springbone-extended-collider-1.0", "three"],
        ["@pixiv/types-vrm-0.0"] = [],
        ["@pixiv/types-vrmc-materials-hdr-emissive-multiplier-1.0"] = [],
        ["@pixiv/types-vrmc-materials-mtoon-1.0"] = [],
        ["@pixiv/types-vrmc-node-constraint-1.0"] = [],
        ["@pixiv/types-vrmc-springbone-1.0"] = [],
        ["@pixiv/types-vrmc-springbone-extended-collider-1.0"] = [],
        ["@pixiv/types-vrmc-vrm-1.0"] = [],
        ["esbuild"] = ["@esbuild/win32-x64"],
        ["three"] = []
    };
    internal sealed record Tool(string Name, string Version, EvidenceReader.FileRecord[] Files);
    internal sealed record Input(string Path, long Bytes, string Hash, string? Package, string? Entry, string[] Roles);
    internal sealed record Package(string Key, string Name, string Version, string Scope, string Integrity, string Hash,
        string[] Dependencies, string[] Notices, string InputsHash, string NoticesHash)
    {
        internal string Reference => "AvatarRenderer|npm:" + Key;
        internal string Purl => $"pkg:npm/{Name.Replace("@", "%40", StringComparison.Ordinal)}@{Version}";
    }
    internal sealed record Output(string Path, long Bytes, string Hash, string Kind, string[] Inputs, string InputsHash);
    internal sealed record Facts(string Hash, string RecipeHash, string MetafileHash, string ReceiptHash, Tool[] Tools,
        Dictionary<string, Package> Packages, Dictionary<string, Input> Inputs, Dictionary<string, Output> Outputs);

    internal static T[] Sorted<T>(IEnumerable<T> values, Func<T, string> identity, EvidenceReader r)
    {
        var items = values.ToArray();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? previous = null;
        foreach (var item in items)
        {
            var key = identity(item);
            r.Require(seen.Add(key) && (previous is null || StringComparer.Ordinal.Compare(previous, key) < 0));
            previous = key;
        }
        return items;
    }

    internal static string[] Strings(JsonElement value, int maximum, EvidenceReader r, int minimum = 0) =>
        Sorted(r.Items(value, maximum, minimum).Select(e => r.Text(e, 1024)), e => e, r);

    internal static Facts Read(JsonElement value, Dictionary<string, EvidenceReader.FileRecord> sources,
        Dictionary<string, PayloadFile> payload, EvidenceReader r)
    {
        r.Keys(value, "schemaVersion context recipe lockFiles tools packages inputs outputs");
        r.Schema(r.Member(value, "schemaVersion"), 1);
        r.Equal(r.Text(value, "context"), "AvatarRenderer");
        var recipe = r.Member(value, "recipe");
        r.Keys(recipe, "script entryPoints metafileSha256 receiptSha256");
        r.Equal(r.Path(r.Member(recipe, "script")), SourceRoot + "build.mjs");
        r.Equal(r.Text(r.Items(r.Member(recipe, "entryPoints"), 1, 1).Single()), SourceRoot + "app.js");
        var metafile = r.Hex(r.Member(recipe, "metafileSha256"));
        var receipt = r.Hex(r.Member(recipe, "receiptSha256"));
        void Source(string path, long bytes, string hash)
        {
            r.Require(sources.TryGetValue(path, out var file) && file.Path == path && file.Bytes == bytes && file.Hash == hash);
        }
        string[] locks = ["src\\Martlet.Avatar.Live2D\\package-lock.json", PackageRoot + "package-lock.json"];
        var lockFiles = r.Items(r.Member(value, "lockFiles"), 2, 2).Select(e => r.File(e)).ToArray();
        r.Require(lockFiles.Select(e => e.Path).SequenceEqual(locks));
        foreach (var file in lockFiles) Source(file.Path, file.Bytes, file.Hash!);
        foreach (var path in locks.Select(p => p.Replace("package-lock.json", "package.json", StringComparison.Ordinal)))
            r.Require(sources.TryGetValue(path, out var file) && file.Path == path && file.Hash is not null);

        var tools = r.Items(r.Member(value, "tools"), 3, 3).Select(tool =>
        {
            r.Keys(tool, "name version files");
            var files = Sorted(r.Items(r.Member(tool, "files"), 32, 1).Select(e => r.File(e)), f => f.Path, r);
            r.Require(files.All(f => f.Bytes > 0));
            return new Tool(r.Text(tool, "name", 16), r.Text(tool, "version", 48), files);
        }).ToArray();
        r.Require(tools.Select(t => (t.Name, t.Version)).SequenceEqual(
            new[] { ("node", "20.11.1"), ("npm", "10.2.4"), ("esbuild", "0.25.12") }));

        var inputNodes = Sorted(r.Items(r.Member(value, "inputs"), 8192, 1),
            e => r.Text(e, "path"), r);
        var inputs = new Dictionary<string, Input>(StringComparer.Ordinal);
        string[] allowedRoles = ["build-script", "bundle-source", "lock", "notice", "package-metadata", "static"];
        foreach (var input in inputNodes)
        {
            r.Keys(input, "path bytes sha256 package entry roles");
            var path = r.Path(r.Member(input, "path"));
            var bytes = r.Integer(r.Member(input, "bytes"));
            r.Require(bytes >= 0);
            var hash = r.Hex(r.Member(input, "sha256"));
            var packageValue = r.Member(input, "package");
            var entryValue = r.Member(input, "entry");
            var package = packageValue.ValueKind == JsonValueKind.Null ? null : PackageKey(packageValue, r);
            var entry = entryValue.ValueKind == JsonValueKind.Null ? null : r.Text(entryValue);
            var roles = Strings(r.Member(input, "roles"), 6, r, 1);
            r.Require(roles.All(role => allowedRoles.Contains(role, StringComparer.Ordinal)));
            r.Require((package is null) == (entry is null));
            if (package is null) Source(path, bytes, hash);
            else
            {
                r.Require(entry!.StartsWith("package/", StringComparison.Ordinal) && !entry.Contains('\\'));
                r.Path(entry.Replace('/', '\\'));
                r.Equal(path, PackageRoot + package.Replace('/', '\\') + "\\" + entry["package/".Length..].Replace('/', '\\'));
                r.Require(!roles.Contains("build-script") && !roles.Contains("lock") && !roles.Contains("static"));
            }
            r.Require(inputs.TryAdd(path, new(path, bytes, hash, package, entry, roles)));
        }
        void Authored(string path, string role)
        {
            r.Require(inputs.TryGetValue(path, out var input) && input.Package is null && input.Roles.Contains(role));
        }
        Authored(SourceRoot + "build.mjs", "build-script");
        Authored(SourceRoot + "app.js", "bundle-source");
        Authored(SourceRoot + "index.html", "static");
        foreach (var file in lockFiles) Authored(file.Path, "lock");

        var packages = new Dictionary<string, Package>(StringComparer.Ordinal);
        Span<byte> digest = stackalloc byte[64];
        foreach (var package in Sorted(r.Items(r.Member(value, "packages"), 256, 1), e => r.Text(e, "key", 768), r))
        {
            r.Keys(package, "key name version scope integrity archiveSha512 dependencies notices");
            var key = PackageKey(r.Member(package, "key"), r);
            var name = r.Text(package, "name", 256);
            r.Require(Regex.IsMatch(name, @"\A(?:@[a-z0-9_.-]+/)?[a-z0-9_.-]+\z", RegexOptions.NonBacktracking));
            r.Require(key.EndsWith("node_modules/" + name, StringComparison.Ordinal));
            var version = r.Text(package, "version", 128);
            r.Require(Regex.IsMatch(version, @"\A[0-9]+\.[0-9]+\.[0-9]+(?:-[A-Za-z0-9.-]+)?(?:\+[A-Za-z0-9.-]+)?\z",
                RegexOptions.NonBacktracking));
            var scope = r.Text(package, "scope", 16);
            r.Require(scope is "runtime" or "build");
            r.Require(PackageDependencies.TryGetValue(name, out var expectedDependencies));
            r.Equal(key, "node_modules/" + name);
            var buildTool = name is "esbuild" or "@esbuild/win32-x64";
            r.Equal(scope, buildTool ? "build" : "runtime");
            r.Equal(version, buildTool ? "0.25.12" : name == "three" ? "0.180.0" : "3.5.5");
            var integrity = r.Text(package, "integrity", 95);
            r.Require(integrity.StartsWith("sha512-", StringComparison.Ordinal));
            var hash = r.Hex(r.Member(package, "archiveSha512"), 128);
            r.Require(Convert.TryFromBase64String(integrity["sha512-".Length..], digest, out var count) && count == 64 &&
                "sha512-" + Convert.ToBase64String(digest) == integrity && Convert.ToHexStringLower(digest) == hash);
            var edges = Strings(r.Member(package, "dependencies"), 256, r);
            r.Require(edges.SequenceEqual(expectedDependencies!.Select(n => "node_modules/" + n)));
            var notices = Strings(r.Member(package, "notices"), 8192, r);
            var members = inputs.Values.Where(i => i.Package == key).ToArray();
            r.Require(members.Any(i => i.Entry == "package/package.json" && i.Roles.Contains("package-metadata")));
            foreach (var notice in notices)
                r.Require(inputs.TryGetValue(notice, out var input) && input.Package == key && input.Roles.Contains("notice"));
            r.Require(notices.SequenceEqual(members.Where(i => i.Roles.Contains("notice")).Select(i => i.Path)));
            if (members.Any(i => i.Roles.Contains("bundle-source"))) r.Require(scope == "runtime" && notices.Length > 0);
            var record = new Package(key, name, version, scope, integrity, hash, edges, notices,
                PayloadEvidenceJson.HashArray(inputNodes.Where(i => r.Member(i, "package").ValueKind == JsonValueKind.String &&
                    r.Text(i, "package", 768) == key), r),
                PayloadEvidenceJson.Hash(r.Member(package, "notices"), r));
            r.Require(packages.TryAdd(key, record));
        }
        r.Require(packages.Keys.SequenceEqual(PackageDependencies.Keys.Select(n => "node_modules/" + n)));
        foreach (var input in inputs.Values) r.Require(input.Package is null || packages.ContainsKey(input.Package));
        foreach (var package in packages.Values)
            foreach (var edge in package.Dependencies)
                r.Require(edge != package.Key && packages.TryGetValue(edge, out var target) &&
                    (package.Scope != "runtime" || target.Scope == "runtime"));

        var expectedOutputs = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [Host + "\\web\\THIRD-PARTY-NOTICES.txt"] = "notices",
            [Host + "\\web\\app.js"] = "bundle",
            [Host + "\\web\\app.js.LEGAL.txt"] = "legal",
            [Host + "\\web\\index.html"] = "static"
        };
        var outputs = new Dictionary<string, Output>(StringComparer.Ordinal);
        foreach (var output in Sorted(r.Items(r.Member(value, "outputs"), 4, 4), e => r.Text(e, "path"), r))
        {
            r.Keys(output, "path bytes sha256 kind inputs");
            var path = r.Path(r.Member(output, "path"));
            var kind = r.Text(output, "kind", 16);
            r.Require(expectedOutputs.TryGetValue(path, out var expectedKind) && kind == expectedKind);
            var bytes = r.Integer(r.Member(output, "bytes"));
            var hash = r.Hex(r.Member(output, "sha256"));
            r.Require(payload.TryGetValue(path, out var file) && file.Bytes == bytes && file.Sha256 == hash);
            var paths = Strings(r.Member(output, "inputs"), 8192, r, 1);
            r.Require(paths.All(inputs.ContainsKey));
            if (kind == "static")
            {
                r.Require(paths.SequenceEqual(new[] { SourceRoot + "index.html" }));
                r.Require(inputs[paths[0]].Bytes == bytes && inputs[paths[0]].Hash == hash);
            }
            else
            {
                var role = kind == "notices" ? "notice" : "bundle-source";
                r.Require(paths.All(p => inputs[p].Roles.Contains(role)));
                if (kind is "bundle" or "legal")
                    r.Require(paths.All(p => inputs[p].Package is not { } key || packages[key].Scope == "runtime"));
            }
            r.Require(outputs.TryAdd(path, new(path, bytes, hash, kind, paths,
                PayloadEvidenceJson.Hash(r.Member(output, "inputs"), r))));
        }
        r.Require(payload.Keys.Where(p => p.StartsWith(Host + "\\web\\", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal).SequenceEqual(expectedOutputs.Keys));
        var bundle = outputs[Host + "\\web\\app.js"];
        r.Require(bundle.Inputs.SequenceEqual(inputs.Values.Where(i => i.Roles.Contains("bundle-source")).Select(i => i.Path)));
        var noticesOutput = outputs[Host + "\\web\\THIRD-PARTY-NOTICES.txt"];
        foreach (var package in packages.Values.Where(p => p.Scope == "runtime"))
            r.Require(package.Notices.All(n => noticesOutput.Inputs.Contains(n, StringComparer.Ordinal)));
        return new(PayloadEvidenceJson.Hash(value, r), inputs[SourceRoot + "build.mjs"].Hash,
            metafile, receipt, tools, packages, inputs, outputs);
    }

    private static string PackageKey(JsonElement value, EvidenceReader r)
    {
        var key = r.Text(value, 768);
        r.Require(!key.Contains('\\'));
        r.Require(Regex.IsMatch(key, @"\Anode_modules/(?:@[a-z0-9_.-]+/)?[a-z0-9_.-]+(?:/node_modules/(?:@[a-z0-9_.-]+/)?[a-z0-9_.-]+)*\z",
            RegexOptions.NonBacktracking));
        r.Path(key.Replace('/', '\\'));
        return key;
    }
}
