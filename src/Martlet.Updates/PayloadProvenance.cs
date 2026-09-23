using System.Text.Json;
using System.Text.RegularExpressions;

namespace Martlet.Updates;

internal static class PayloadProvenance
{
    internal const string Assurance = "UNSIGNED INTERNAL OBSERVATION - NOT PUBLISHER ATTESTATION";
    internal const string Observation = "Local tool-file fingerprints; not an SDK distribution attestation.";
    internal sealed record Asset(string Path, string Kind, string Source);
    internal sealed record Library(string Context, string Key, string Type, string? ContentHash,
        string[] Dependencies, Asset[] Assets, string? Project = null, string? Directory = null)
    {
        internal string Reference => Context + "|" + Key;
        internal string Id => Key[..Key.IndexOf('/')];
        internal string Version => Key[(Key.IndexOf('/') + 1)..];
        internal bool IsRoot => Id == (Project ?? "Martlet." + Context);
        internal string RootDirectory => Directory ?? Context;
        internal string ArchiveId => Type == "reference" ? "Microsoft.Web.WebView2" :
            Type == "runtimepack" ? Id["runtimepack.".Length..] : Id;
        internal string[] Generated =>
            [$"{RootDirectory}\\{Id}.exe", $"{RootDirectory}\\{Id}.deps.json", $"{RootDirectory}\\{Id}.runtimeconfig.json"];
    }
    internal sealed record Origin(string Path, string Component, string Entry, string Hash);
    internal sealed record Archive(string Id, string Version, string Hash, string Nuspec,
        string? LicenseExpression, string? LicenseFile, string? RepositoryUrl, string? RepositoryCommit, Origin[] Origins);
    internal sealed record Facts(string Source, string Tree, bool Dirty, string SourceHash, string Sdk,
        EvidenceReader.FileRecord[] Tools, Dictionary<string, Library> Libraries, Dictionary<string, Archive> Archives,
        AvatarFacts? Avatar = null);
    internal sealed record Application(string Name, string Project, string Directory, string? Parent, string[] BuildOnly);
    internal sealed record BuildArchive(string Id, string Version, string Hash, string Nuspec, string UsesHash);
    internal sealed record AvatarFacts(Application[] Applications, BuildArchive[] BuildArchives, string BuildArchivesHash,
        PayloadBrowser.Facts Browser);
    private static readonly string[] Projects =
    [
        "Martlet.Desktop", "Martlet.Doctor", "Martlet.Core", "Martlet.Diagnostics", "Martlet.Fixtures", "Martlet.Sessions",
        "Martlet.Audio", "Martlet.Credentials.Windows", "Martlet.Conversation", "Martlet.Providers", "Martlet.Participation",
        "Martlet.Support", "Martlet.Avatars", "Martlet.Avatar.Hosting", "Martlet.Avatar.Audio2Face", "Martlet.Avatar.RendererHost"
    ];
    private static readonly string[] WebViewReferences =
    [
        "Microsoft.Web.WebView2.Core/1.0.4191.47", "Microsoft.Web.WebView2.WinForms/1.0.4191.47",
        "Microsoft.Web.WebView2.Wpf/1.0.4191.47"
    ];

    internal static Facts Read(JsonElement value, string source, bool dirty, string sdk, string runtime,
        PayloadFile[] files, EvidenceReader r, int schema = 1)
    {
        r.Keys(value, "schemaVersion assurance source sdk publish applications restores archives" +
            (schema == 2 ? " buildArchives browser" : ""));
        r.Schema(r.Member(value, "schemaVersion"), schema);
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
        var contexts = schema == 2 ? Contexts(r.Member(value, "applications"), r) : null;
        var libraries = Applications(r.Member(value, "applications"), payload, runtime, r, contexts);
        Restores(r.Member(value, "restores"), libraries, sourceFiles, runtime, r, contexts);
        var archives = Archives(r.Member(value, "archives"), libraries, payload, r, contexts is not null);
        AvatarFacts? avatar = null;
        if (contexts is not null)
        {
            VerifyWebView(libraries, archives, payload, r);
            var build = r.Member(value, "buildArchives");
            avatar = new(contexts, BuildArchives(build, r.Member(value, "restores"), libraries, r),
                PayloadEvidenceJson.Hash(build, r), PayloadBrowser.Read(r.Member(value, "browser"), sourceFiles, payload, r));
        }
        return new(source, tree, dirty, sourceHash, sdk, tools, libraries, archives, avatar);
    }

    private static Application[] Contexts(JsonElement value, EvidenceReader r)
    {
        Application[] expected =
        [
            new("Desktop", "Martlet.Desktop", "Desktop", null, []),
            new("Doctor", "Martlet.Doctor", "Doctor", null, []),
            new("AvatarRenderer", "Martlet.Avatar.RendererHost", PayloadBrowser.Host, "Desktop", [])
        ];
        var items = r.Items(value, 3, 3).ToArray();
        for (var i = 0; i < items.Length; i++)
        {
            var item = items[i];
            r.Keys(item, "name project directory parent target buildOnlyLibraries libraries");
            var context = expected[i];
            r.Equal(r.Text(item, "name", 16), context.Name);
            r.Equal(r.Text(item, "project", 256), context.Project);
            r.Equal(r.Path(r.Member(item, "directory")), context.Directory);
            var parent = r.Member(item, "parent");
            if (context.Parent is null) r.Require(parent.ValueKind == JsonValueKind.Null);
            else r.Equal(r.Text(parent), context.Parent);
            var buildOnly = PayloadBrowser.Strings(r.Member(item, "buildOnlyLibraries"), 2048, r);
            if (context.Name != "Desktop") r.Require(buildOnly.Length == 0);
            expected[i] = context with { BuildOnly = buildOnly };
        }
        return expected;
    }

    private static bool Owns(Library library, string path, bool avatar) =>
        path.StartsWith(library.RootDirectory + "\\", StringComparison.Ordinal) &&
        (!avatar || library.Context != "Desktop" || !path.StartsWith(PayloadBrowser.Host + "\\", StringComparison.OrdinalIgnoreCase));

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
        string runtime, EvidenceReader r, Application[]? applications = null)
    {
        var result = new Dictionary<string, Library>(StringComparer.Ordinal);
        var contexts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var owned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var app in r.Items(value, applications?.Length ?? 2, applications?.Length ?? 2))
        {
            if (applications is null) r.Keys(app, "name target libraries");
            var context = r.Text(app, "name", applications is null ? 7 : 16);
            var descriptor = applications?.Single(a => a.Name == context);
            r.Require((applications is not null || context is "Desktop" or "Doctor") && contexts.Add(context));
            r.Equal(r.Text(app, "target"), ".NETCoreApp,Version=v10.0/win-x64");
            var items = r.Items(r.Member(app, "libraries"), 2048, 1).ToArray();
            if (applications is not null) PayloadBrowser.Sorted(items, e => r.Text(e, "key", 256), r);
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
                r.Require(type is "project" or "package" or "runtimepack" ||
                    applications is not null && type == "reference" && context == "AvatarRenderer" && WebViewReferences.Contains(key));
                var hash = Hash(item, type, r);
                var dependencies = r.Edges(r.Member(item, "dependencies"), keys);
                var assets = r.Items(r.Member(item, "assets"), payload.Count).Select(asset =>
                {
                    r.Keys(asset, "path kind source");
                    var path = r.Path(r.Member(asset, "path"));
                    r.Require(path.StartsWith((descriptor?.Directory ?? context) + "\\", StringComparison.Ordinal) &&
                        (applications is null || context != "Desktop" || !path.StartsWith(PayloadBrowser.Host + "\\", StringComparison.OrdinalIgnoreCase)) &&
                        payload.ContainsKey(path) && owned.Add(path));
                    var kind = r.Text(asset, "kind", 16);
                    r.Require(kind is "native" or "runtime" or "resources");
                    var source = r.Text(asset, "source");
                    r.Path(source.Replace('/', '\\'));
                    return new Asset(path, kind, source);
                }).ToArray();
                var library = new Library(context, key, type, hash, dependencies, assets, descriptor?.Project, descriptor?.Directory);
                if (type == "reference")
                    r.Require(dependencies.Length == 0 && assets.Length == 1 &&
                        assets[0] == new Asset(PayloadBrowser.Host + "\\" + library.Id + ".dll", "runtime", library.Id + ".dll"));
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
        Dictionary<string, EvidenceReader.FileRecord> sourceFiles, string runtime, EvidenceReader r, Application[]? contexts = null)
    {
        var restores = r.Items(value, 128, 1).ToArray();
        var projects = new Dictionary<string, string>(StringComparer.Ordinal);
        var insensitive = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var restore in restores)
        {
            r.Keys(restore, "project path version lockPath lockSha256 sourceSetSha256 targets");
            var project = r.Text(restore, "project", 256);
            r.Require((contexts is null
                ? Regex.IsMatch(project, @"\AMartlet\.[A-Za-z.]+\z", RegexOptions.NonBacktracking)
                : Projects.Contains(project, StringComparer.Ordinal)) && insensitive.Add(project));
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
            var published = applications.Values.Where(l => l.Context == app.Context && !l.IsRoot && l.Type != "runtimepack" &&
                (contexts is null || l.Type != "reference")).ToArray();
            var restored = r.Items(r.Member(target, "libraries"), 2048).ToDictionary(e => r.Text(e, "key", 256), StringComparer.Ordinal);
            var omitted = contexts?.Single(c => c.Name == app.Context).BuildOnly ?? [];
            foreach (var key in omitted)
            {
                var host = applications.Values.SingleOrDefault(l => l.Context == "AvatarRenderer" && l.Key == key &&
                    l.Type is "project" or "package");
                r.Require(host is not null && !published.Any(l => l.Key == key) && restored.TryGetValue(key, out _));
                var item = restored[key];
                r.Equal(r.Text(item, "type", 16), host!.Type);
                r.Require(Hash(item, host.Type, r) == host.ContentHash);
                r.Require(r.Items(r.Member(item, "dependencies"), 2048).Select(e => r.Text(e, 264))
                    .SequenceEqual(host.Dependencies.Where(d => !WebViewReferences.Contains(d) &&
                        !d.StartsWith("runtimepack.", StringComparison.Ordinal))));
            }
            var allKeys = restored.Keys.ToHashSet(StringComparer.Ordinal);
            foreach (var key in omitted) restored.Remove(key);
            r.Require(published.Length == restored.Count);
            var keys = restored.Keys.ToHashSet(StringComparer.Ordinal);
            r.Require(r.Edges(r.Member(target, "rootDependencies"), allKeys).Where(d => !omitted.Contains(d)).SequenceEqual(
                app.Dependencies.Where(d => !d.StartsWith("runtimepack.", StringComparison.Ordinal) &&
                    (contexts is null || !WebViewReferences.Contains(d)))));
            foreach (var library in published)
            {
                r.Require(restored.TryGetValue(library.Key, out var item));
                r.Equal(r.Text(item, "type", 16), library.Type);
                r.Require(Hash(item, library.Type, r) == library.ContentHash &&
                    r.Edges(r.Member(item, "dependencies"), allKeys).Where(d => !omitted.Contains(d)).SequenceEqual(library.Dependencies));
            }
            foreach (var library in applications.Values.Where(l => l.Context == app.Context && l.Type == "runtimepack"))
                r.Require(r.Items(r.Member(target, "frameworkDownloads"), 16).Any(d =>
                    r.Text(d, "id") == library.Id["runtimepack.".Length..] &&
                    r.Text(d, "requested") == $"[{library.Version}, {library.Version}]"));
        }
    }

    private static Dictionary<string, Archive> Archives(JsonElement value, Dictionary<string, Library> libraries,
        Dictionary<string, PayloadFile> payload, EvidenceReader r, bool avatar = false)
    {
        var nonproject = libraries.Values.Where(l => l.Type != "project").ToArray();
        var result = new Dictionary<string, Archive>(StringComparer.OrdinalIgnoreCase);
        var owned = new Dictionary<string, Origin>(StringComparer.OrdinalIgnoreCase);
        foreach (var archive in r.Items(value, nonproject.Length))
        {
            r.Keys(archive, "id version archiveSha512 nuspecSha256 licenseExpression licenseFile repositoryUrl repositoryCommit origins");
            var id = r.Text(archive, "id", 256);
            var version = r.Text(archive, "version", 48);
            var matching = nonproject.Where(l => l.ArchiveId
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
                    matching.Any(l => l.Reference == component && Owns(l, path, avatar)));
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
            var id = library.ArchiveId;
            r.Require(result.TryGetValue(id, out var archive) && archive.Version == library.Version);
            foreach (var asset in library.Assets)
                r.Require(owned.TryGetValue(asset.Path, out var origin) && origin.Component == library.Reference);
        }
        return result;
    }

    private static void VerifyWebView(Dictionary<string, Library> libraries, Dictionary<string, Archive> archives,
        Dictionary<string, PayloadFile> payload, EvidenceReader r)
    {
        var root = libraries.Values.Single(l => l.Context == "AvatarRenderer" && l.IsRoot);
        foreach (var reference in WebViewReferences)
            r.Require(libraries.TryGetValue("AvatarRenderer|" + reference, out var library) && library.Type == "reference" &&
                root.Dependencies.Contains(reference));
        r.Require(libraries.TryGetValue("AvatarRenderer|Microsoft.Web.WebView2/1.0.4191.47", out var package) &&
            package.Type == "package" && root.Dependencies.Contains(package.Key));
        r.Require(package!.Assets.SequenceEqual(new[]
        {
            new Asset(PayloadBrowser.Host + "\\WebView2Loader.dll", "native", "runtimes/win-x64/native/WebView2Loader.dll")
        }));
        var expected = new Dictionary<string, (string Component, string Entry)>(StringComparer.Ordinal);
        foreach (var name in new[] { "Core", "WinForms", "Wpf" })
        {
            var prefix = name == "Wpf" ? "lib_manual/net5.0-windows10.0.17763.0/" : "lib_manual/netcoreapp3.0/";
            foreach (var extension in new[] { "dll", "xml" })
            {
                var file = $"Microsoft.Web.WebView2.{name}.{extension}";
                expected.Add(PayloadBrowser.Host + "\\" + file,
                    ($"AvatarRenderer|Microsoft.Web.WebView2.{name}/1.0.4191.47", prefix + file));
            }
        }
        var loader = PayloadBrowser.Host + "\\WebView2Loader.dll";
        var nested = PayloadBrowser.Host + "\\runtimes\\win-x64\\native\\WebView2Loader.dll";
        foreach (var path in new[] { loader, nested })
            expected.Add(path, ("AvatarRenderer|Microsoft.Web.WebView2/1.0.4191.47", "runtimes/win-x64/native/WebView2Loader.dll"));
        r.Require(archives.TryGetValue("Microsoft.Web.WebView2", out var archive) && archive.Id == "Microsoft.Web.WebView2" &&
            archive.Version == "1.0.4191.47" && archive.Origins.Length == expected.Count);
        foreach (var origin in archive!.Origins)
            r.Require(expected.TryGetValue(origin.Path, out var item) && item == (origin.Component, origin.Entry));
        r.Require(payload.TryGetValue(loader, out var first) && payload.TryGetValue(nested, out var second) &&
            first.Bytes == second.Bytes && first.Sha256 == second.Sha256);
    }

    private static BuildArchive[] BuildArchives(JsonElement value, JsonElement restores,
        Dictionary<string, Library> libraries, EvidenceReader r)
    {
        var records = r.Items(value, 16, 1).ToArray();
        r.Require(records.Length == 1);
        var archive = records[0];
        r.Keys(archive, "id version archiveSha512 nuspecSha256 licenseExpression licenseFile repositoryUrl repositoryCommit uses");
        r.Equal(r.Text(archive, "id", 256), "Grpc.Tools");
        r.Equal(r.Text(archive, "version", 48), "2.84.0");
        r.Require(libraries.Values.All(l => !l.Id.Equals("Grpc.Tools", StringComparison.OrdinalIgnoreCase)));
        foreach (var name in new[] { "licenseExpression", "licenseFile", "repositoryUrl", "repositoryCommit" })
        {
            var item = r.Member(archive, name);
            if (item.ValueKind != JsonValueKind.Null) r.Text(item, empty: true);
        }
        var expected = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var restore in r.Items(restores, 128))
            foreach (var target in r.Items(r.Member(restore, "targets"), 8))
                foreach (var library in r.Items(r.Member(target, "libraries"), 2048))
                    if (r.Text(library, "key", 256).StartsWith("Grpc.Tools/", StringComparison.OrdinalIgnoreCase))
                    {
                        r.Equal(r.Text(restore, "project", 256), "Martlet.Avatar.Audio2Face");
                        r.Equal(r.Text(library, "key", 256), "Grpc.Tools/2.84.0");
                        r.Equal(r.Text(library, "type", 16), "package");
                        expected.Add(r.Text(target, "name"), r.ContentHash(r.Member(library, "contentHash")));
                    }
        var usesValue = r.Member(archive, "uses");
        var uses = PayloadBrowser.Sorted(r.Items(usesValue, 1024, 1),
            u => r.Text(u, "project", 256) + "|" + r.Text(u, "target"), r);
        r.Require(uses.Length == expected.Count);
        foreach (var use in uses)
        {
            r.Keys(use, "project target key contentHash");
            r.Equal(r.Text(use, "project", 256), "Martlet.Avatar.Audio2Face");
            r.Equal(r.Text(use, "key", 256), "Grpc.Tools/2.84.0");
            r.Require(expected.TryGetValue(r.Text(use, "target"), out var hash));
            r.Equal(r.ContentHash(r.Member(use, "contentHash")), hash!);
        }
        return [new("Grpc.Tools", "2.84.0", r.Hex(r.Member(archive, "archiveSha512"), 128),
            r.Hex(r.Member(archive, "nuspecSha256")), PayloadEvidenceJson.Hash(usesValue, r))];
    }
}
