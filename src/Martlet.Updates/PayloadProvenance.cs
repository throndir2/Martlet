using System.Text.Json;
using System.Text.RegularExpressions;

namespace Martlet.Updates;

internal static class PayloadProvenance
{
    internal const string Assurance = "UNSIGNED INTERNAL OBSERVATION - NOT PUBLISHER ATTESTATION";
    internal const string Observation = "Local tool-file fingerprints; not an SDK distribution attestation.";
    internal sealed record Asset(string Path, string Kind, string Source);
    internal sealed record Library(string Context, string Key, string Type, string? ContentHash,
        string[] Dependencies, Asset[] Assets)
    {
        internal string Reference => Context + "|" + Key;
        internal string Id => Key[..Key.IndexOf('/')];
        internal string Version => Key[(Key.IndexOf('/') + 1)..];
        internal bool IsRoot => Id == "Martlet." + Context;
    }
    internal sealed record Origin(string Path, string Component, string Entry, string Hash);
    internal sealed record Archive(string Id, string Version, string Hash, string Nuspec,
        string? LicenseExpression, string? LicenseFile, string? RepositoryUrl, string? RepositoryCommit, Origin[] Origins);
    internal sealed record Facts(string Source, string Tree, bool Dirty, string SourceHash, string Sdk,
        EvidenceReader.FileRecord[] Tools, Dictionary<string, Library> Libraries, Dictionary<string, Archive> Archives);

    internal static Facts Read(JsonElement value, string source, bool dirty, string sdk, string runtime,
        PayloadFile[] files, EvidenceReader r)
    {
        r.Keys(value, "schemaVersion assurance source sdk publish applications restores archives");
        r.Schema(r.Member(value, "schemaVersion"), 1);
        r.Equal(r.Text(value, "assurance"), Assurance);
        var sourceValue = r.Member(value, "source");
        r.Keys(sourceValue, "commit tree dirty files sha256");
        r.Equal(r.Hex(r.Member(sourceValue, "commit"), 40), source);
        r.Require(r.Boolean(r.Member(sourceValue, "dirty")) == dirty);
        var tree = r.Hex(r.Member(sourceValue, "tree"), 40);
        var sourceHash = r.Hex(r.Member(sourceValue, "sha256"));
        var sourceFiles = new Dictionary<string, EvidenceReader.FileRecord>(StringComparer.OrdinalIgnoreCase);
        string? previous = null;
        foreach (var item in r.Items(r.Member(sourceValue, "files"), 16384))
        {
            var file = r.File(item, missing: true);
            r.Require(!file.Path.Split('\\').Any(p => new[] { ".git", "bin", "obj", "artifacts", ".vs", "TestResults" }
                .Contains(p, StringComparer.OrdinalIgnoreCase)));
            r.Require(sourceFiles.TryAdd(file.Path, file) &&
                (previous is null || StringComparer.Ordinal.Compare(previous, file.Path) < 0));
            previous = file.Path;
        }
        var sdkValue = r.Member(value, "sdk");
        r.Keys(sdkValue, "version observation files");
        r.Equal(r.Text(sdkValue, "version", 48), sdk);
        r.Equal(r.Text(sdkValue, "observation"), Observation);
        var tools = r.Items(r.Member(sdkValue, "files"), 3, 3).Select(e => r.File(e)).ToArray();
        var paths = new[] { "dotnet.exe", $"sdk\\{sdk}\\MSBuild.dll", $"sdk\\{sdk}\\Roslyn\\bincore\\csc.dll" };
        r.Require(tools.Select(f => f.Path).SequenceEqual(paths) && tools.All(f => f.Bytes > 0));
        var publish = r.Member(value, "publish");
        r.Keys(publish, "configuration framework rid selfContained trimmed singleFile readyToRun");
        r.Equal(r.Text(publish, "configuration"), "Release");
        r.Equal(r.Text(publish, "framework"), "net10.0-windows");
        r.Equal(r.Text(publish, "rid"), "win-x64");
        r.Require(r.Boolean(r.Member(publish, "selfContained")));
        foreach (var name in new[] { "trimmed", "singleFile", "readyToRun" }) r.Require(!r.Boolean(r.Member(publish, name)));

        var payload = files.Where(f => f.Path != "sbom.cdx.json").ToDictionary(f => f.Path, StringComparer.Ordinal);
        var libraries = Applications(r.Member(value, "applications"), payload, runtime, r);
        Restores(r.Member(value, "restores"), libraries, sourceFiles, runtime, r);
        var archives = Archives(r.Member(value, "archives"), libraries, payload, r);
        return new(source, tree, dirty, sourceHash, sdk, tools, libraries, archives);
    }

    private static string Key(JsonElement value, EvidenceReader r)
    {
        var key = r.Text(value, 256);
        r.Require(Regex.IsMatch(key, @"\A[A-Za-z0-9_.-]+/[A-Za-z0-9_.+-]+\z", RegexOptions.NonBacktracking));
        return key;
    }

    private static string? Hash(JsonElement library, string type, EvidenceReader r)
    {
        var hash = r.Member(library, "contentHash");
        if (type == "package") return r.ContentHash(hash);
        r.Require(hash.ValueKind == JsonValueKind.Null);
        return null;
    }

    private static Dictionary<string, Library> Applications(JsonElement value, Dictionary<string, PayloadFile> payload,
        string runtime, EvidenceReader r)
    {
        var result = new Dictionary<string, Library>(StringComparer.Ordinal);
        var contexts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var owned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var app in r.Items(value, 2, 2))
        {
            r.Keys(app, "name target libraries");
            var context = r.Text(app, "name", 7);
            r.Require(context is "Desktop" or "Doctor" && contexts.Add(context));
            r.Equal(r.Text(app, "target"), ".NETCoreApp,Version=v10.0/win-x64");
            var items = r.Items(r.Member(app, "libraries"), 2048, 1).ToArray();
            var keys = new HashSet<string>(StringComparer.Ordinal);
            var insensitive = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in items)
            {
                r.Keys(item, "key type contentHash dependencies assets");
                var key = Key(r.Member(item, "key"), r);
                r.Require(keys.Add(key) && insensitive.Add(key));
            }
            foreach (var item in items)
            {
                var key = r.Text(item, "key", 256);
                var type = r.Text(item, "type", 16);
                r.Require(type is "project" or "package" or "runtimepack");
                var hash = Hash(item, type, r);
                var dependencies = r.Edges(r.Member(item, "dependencies"), keys);
                var assets = r.Items(r.Member(item, "assets"), payload.Count).Select(asset =>
                {
                    r.Keys(asset, "path kind source");
                    var path = r.Path(r.Member(asset, "path"));
                    r.Require(path.StartsWith(context + "\\", StringComparison.Ordinal) &&
                        payload.ContainsKey(path) && owned.Add(path));
                    var kind = r.Text(asset, "kind", 16);
                    r.Require(kind is "native" or "runtime" or "resources");
                    var source = r.Text(asset, "source");
                    r.Path(source.Replace('/', '\\'));
                    return new Asset(path, kind, source);
                }).ToArray();
                var library = new Library(context, key, type, hash, dependencies, assets);
                if (type == "runtimepack")
                {
                    r.Require(library.Id.StartsWith("runtimepack.", StringComparison.Ordinal));
                    r.Equal(library.Version, runtime);
                }
                r.Require(result.TryAdd(library.Reference, library));
            }
            var roots = result.Values.Where(l => l.Context == context && l.IsRoot).ToArray();
            r.Require(roots.Length == 1 && roots[0].Type == "project");
        }
        return result;
    }

    private static void Restores(JsonElement value, Dictionary<string, Library> applications,
        Dictionary<string, EvidenceReader.FileRecord> sourceFiles, string runtime, EvidenceReader r)
    {
        var restores = r.Items(value, 128, 1).ToArray();
        var projects = new Dictionary<string, string>(StringComparer.Ordinal);
        var insensitive = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var restore in restores)
        {
            r.Keys(restore, "project path version lockPath lockSha256 sourceSetSha256 targets");
            var project = r.Text(restore, "project", 256);
            r.Require(Regex.IsMatch(project, @"\AMartlet\.[A-Za-z.]+\z", RegexOptions.NonBacktracking) && insensitive.Add(project));
            var version = r.Text(restore, "version", 48);
            r.Require(projects.TryAdd(project, version));
            r.Equal(r.Path(r.Member(restore, "path")), $"src\\{project}\\{project}.csproj");
            var lockPath = r.Path(r.Member(restore, "lockPath"));
            r.Equal(lockPath, $"packaging\\windows\\locks\\{project}.packages.lock.json");
            var lockHash = r.Hex(r.Member(restore, "lockSha256"));
            r.Require(sourceFiles.TryGetValue(lockPath, out var source) && source.Path == lockPath && source.Hash == lockHash);
            r.Hex(r.Member(restore, "sourceSetSha256"));
        }
        foreach (var restore in restores)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var target in r.Items(r.Member(restore, "targets"), 8, 1))
            {
                r.Keys(target, "name framework rootDependencies libraries frameworkDownloads");
                var name = r.Text(target, "name");
                r.Require(name is "net10.0/win-x64" or "net10.0-windows/win-x64" && names.Add(name));
                r.Equal(r.Text(target, "framework"), name == "net10.0/win-x64" ? "net10.0" : "net10.0-windows7.0");
                var libraries = r.Items(r.Member(target, "libraries"), 2048).ToArray();
                var keys = new HashSet<string>(StringComparer.Ordinal);
                var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var library in libraries)
                {
                    r.Keys(library, "key type contentHash dependencies");
                    var key = Key(r.Member(library, "key"), r);
                    r.Require(keys.Add(key) && unique.Add(key));
                    var type = r.Text(library, "type", 16);
                    r.Require(type is "project" or "package");
                    Hash(library, type, r);
                    if (type == "project")
                        r.Require(projects.TryGetValue(key[..key.IndexOf('/')], out var version) &&
                            version == key[(key.IndexOf('/') + 1)..]);
                }
                r.Edges(r.Member(target, "rootDependencies"), keys);
                foreach (var library in libraries) r.Edges(r.Member(library, "dependencies"), keys);
                var downloads = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var download in r.Items(r.Member(target, "frameworkDownloads"), 16))
                {
                    r.Keys(download, "id requested");
                    var id = r.Text(download, "id");
                    r.Require(id is "Microsoft.NETCore.App.Runtime.win-x64" or
                        "Microsoft.WindowsDesktop.App.Runtime.win-x64" or "Microsoft.AspNetCore.App.Runtime.win-x64" && downloads.Add(id));
                    r.Equal(r.Text(download, "requested"), $"[{runtime}, {runtime}]");
                }
            }
        }
        foreach (var library in applications.Values.Where(l => l.Type == "project"))
            r.Require(projects.TryGetValue(library.Id, out var version) && version == library.Version);
        foreach (var app in applications.Values.Where(l => l.IsRoot))
        {
            var restore = restores.Single(e => r.Text(e, "project", 256) == app.Id);
            var targets = r.Items(r.Member(restore, "targets"), 8).Where(e =>
                r.Text(e, "name") == "net10.0-windows/win-x64").ToArray();
            r.Require(targets.Length == 1);
            var target = targets[0];
            var published = applications.Values.Where(l => l.Context == app.Context && !l.IsRoot && l.Type != "runtimepack").ToArray();
            var restored = r.Items(r.Member(target, "libraries"), 2048).ToDictionary(e => r.Text(e, "key", 256), StringComparer.Ordinal);
            r.Require(published.Length == restored.Count);
            var keys = restored.Keys.ToHashSet(StringComparer.Ordinal);
            r.Require(r.Edges(r.Member(target, "rootDependencies"), keys).SequenceEqual(
                app.Dependencies.Where(d => !d.StartsWith("runtimepack.", StringComparison.Ordinal))));
            foreach (var library in published)
            {
                r.Require(restored.TryGetValue(library.Key, out var item));
                r.Equal(r.Text(item, "type", 16), library.Type);
                r.Require(Hash(item, library.Type, r) == library.ContentHash &&
                    r.Edges(r.Member(item, "dependencies"), keys).SequenceEqual(library.Dependencies));
            }
            foreach (var library in applications.Values.Where(l => l.Context == app.Context && l.Type == "runtimepack"))
                r.Require(r.Items(r.Member(target, "frameworkDownloads"), 16).Any(d =>
                    r.Text(d, "id") == library.Id["runtimepack.".Length..] &&
                    r.Text(d, "requested") == $"[{library.Version}, {library.Version}]"));
        }
    }

    private static Dictionary<string, Archive> Archives(JsonElement value, Dictionary<string, Library> libraries,
        Dictionary<string, PayloadFile> payload, EvidenceReader r)
    {
        var nonproject = libraries.Values.Where(l => l.Type != "project").ToArray();
        var result = new Dictionary<string, Archive>(StringComparer.OrdinalIgnoreCase);
        var owned = new Dictionary<string, Origin>(StringComparer.OrdinalIgnoreCase);
        foreach (var archive in r.Items(value, nonproject.Length))
        {
            r.Keys(archive, "id version archiveSha512 nuspecSha256 licenseExpression licenseFile repositoryUrl repositoryCommit origins");
            var id = r.Text(archive, "id", 256);
            var version = r.Text(archive, "version", 48);
            var matching = nonproject.Where(l => (l.Type == "runtimepack" ? l.Id["runtimepack.".Length..] : l.Id)
                .Equals(id, StringComparison.OrdinalIgnoreCase) && l.Version == version).ToArray();
            r.Require(matching.Length != 0);
            string? Declaration(string name)
            {
                var declaration = r.Member(archive, name);
                return declaration.ValueKind == JsonValueKind.Null ? null : r.Text(declaration, empty: true);
            }
            var origins = r.Items(r.Member(archive, "origins"), payload.Count).Select(origin =>
            {
                r.Keys(origin, "path component entry sha256");
                var path = r.Path(r.Member(origin, "path"));
                var component = r.Text(origin, "component", 264);
                var entry = r.Text(origin, "entry");
                r.Path(entry.Replace('/', '\\'));
                var hash = r.Hex(r.Member(origin, "sha256"));
                r.Require(payload.TryGetValue(path, out var file) && file.Sha256 == hash &&
                    matching.Any(l => l.Reference == component && path.StartsWith(l.Context + "\\", StringComparison.Ordinal)));
                var item = new Origin(path, component, entry, hash);
                r.Require(owned.TryAdd(path, item));
                return item;
            }).ToArray();
            var record = new Archive(id, version, r.Hex(r.Member(archive, "archiveSha512"), 128),
                r.Hex(r.Member(archive, "nuspecSha256")), Declaration("licenseExpression"), Declaration("licenseFile"),
                Declaration("repositoryUrl"), Declaration("repositoryCommit"), origins);
            r.Require(result.TryAdd(id, record));
        }
        foreach (var library in nonproject)
        {
            var id = library.Type == "runtimepack" ? library.Id["runtimepack.".Length..] : library.Id;
            r.Require(result.TryGetValue(id, out var archive) && archive.Version == library.Version);
            foreach (var asset in library.Assets)
                r.Require(owned.TryGetValue(asset.Path, out var origin) && origin.Component == library.Reference);
        }
        return result;
    }
}
