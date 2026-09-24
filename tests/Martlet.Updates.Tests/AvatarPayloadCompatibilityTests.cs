using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Martlet.Updates;
using static Martlet.Updates.Tests.SignedPackageFixture;

namespace Martlet.Updates.Tests;

public sealed class AvatarPayloadFixture : IDisposable
{
    internal ProductionPayloadFixture Production { get; } = new(3);
    public void Dispose() => Production.Dispose();
}

public sealed class WindowsSdkPayloadFixture : IDisposable
{
    internal ProductionPayloadFixture Production { get; } = new(3, "WindowsSdk");
    public void Dispose() => Production.Dispose();
}

public sealed class AvatarPayloadCompatibilityTests(SigningKeys keys, AvatarPayloadFixture avatar,
    ProductionPayloadFixture legacy, WindowsSdkPayloadFixture overlay) : IClassFixture<SigningKeys>, IClassFixture<AvatarPayloadFixture>,
    IClassFixture<ProductionPayloadFixture>, IClassFixture<WindowsSdkPayloadFixture>
{
    private SignedPackageFixture Fixture() => new(keys, production: avatar.Production);

    private static void Rewrite(SignedPackageFixture fixture, Action<JsonObject>? manifest = null,
        Action<JsonObject>? sbom = null, Func<byte[], byte[]>? rawManifest = null, Func<byte[], byte[]>? rawSbom = null)
    {
        if (sbom is not null)
        {
            var node = JsonNode.Parse(fixture.Files["sbom.cdx.json"])!.AsObject();
            sbom(node);
            fixture.Files["sbom.cdx.json"] = Encoding.UTF8.GetBytes(node.ToJsonString());
        }
        if (rawSbom is not null) fixture.Files["sbom.cdx.json"] = rawSbom(fixture.Files["sbom.cdx.json"]);
        var files = fixture.Inventory().Where(f => f.Path is not ("manifest.json" or "SHA256SUMS.txt"))
            .Select(f => f with { Path = f.Path.Replace('/', '\\') }).OrderBy(f => f.Path, StringComparer.Ordinal).ToArray();
        var payload = JsonNode.Parse(fixture.Files["manifest.json"])!.AsObject();
        payload["files"] = JsonNode.Parse(Wire.Write(files));
        manifest?.Invoke(payload);
        var bytes = Encoding.UTF8.GetBytes(payload.ToJsonString());
        fixture.Files["manifest.json"] = rawManifest is null ? bytes : rawManifest(bytes);
        fixture.Files["SHA256SUMS.txt"] = Encoding.UTF8.GetBytes(
            string.Concat(files.Select(f => $"{f.Sha256}  {f.Path}\n")) +
            $"{Wire.Hash(fixture.Files["manifest.json"])}  manifest.json\n");
        fixture.WriteZip(fixture.Files.Select(f => new Entry(f.Key, f.Value)));
        fixture.ResignArchive(updateInventory: true);
    }

    private static void Refused(SignedPackageFixture fixture, StagingFailure expected = StagingFailure.InvalidManifest)
    {
        var error = Assert.Throws<StagingException>(() => fixture.Preview(fixture.Engine()));
        Assert.Equal(expected, error.Failure);
        Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.StagingRoot));
        fixture.AssertPrivateDataUnchanged();
    }

    [Fact]
    public void ActualAvatarConstructorDocumentsStageAndReopenByteExactly()
    {
        using var fixture = Fixture();
        var engine = fixture.Engine();
        var plan = fixture.Preview(engine);
        var receipt = engine.Stage(plan, Approve(plan));
        Assert.Equal(receipt.ReceiptSha256, fixture.Engine().InspectStaged(fixture.Destination).ReceiptSha256);
        foreach (var (name, bytes) in fixture.Files)
            Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(fixture.Destination, "payload", name.Replace('/', '\\'))));
        fixture.AssertPrivateDataUnchanged();
    }

    [Fact]
    public void VersionedRendererProjectionDocumentsStageAndReopenByteExactly()
    {
        using var fixture = new SignedPackageFixture(keys, production: overlay.Production);
        var engine = fixture.Engine();
        var plan = fixture.Preview(engine);
        var receipt = engine.Stage(plan, Approve(plan));
        Assert.Equal(receipt.ReceiptSha256, fixture.Engine().InspectStaged(fixture.Destination).ReceiptSha256);
        foreach (var (name, bytes) in fixture.Files)
            Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(fixture.Destination, "payload", name.Replace('/', '\\'))));
        fixture.AssertPrivateDataUnchanged();
    }

    [Theory]
    [InlineData("framework")]
    [InlineData("target")]
    [InlineData("desktop-target")]
    [InlineData("desktop-download")]
    [InlineData("download-version")]
    [InlineData("missing-download")]
    [InlineData("duplicate-download")]
    [InlineData("runtime-version")]
    [InlineData("archive-digest")]
    [InlineData("asset-source")]
    [InlineData("asset-kind")]
    [InlineData("archive-entry")]
    [InlineData("archive-owner")]
    [InlineData("missing-origin")]
    public void WindowsSdkEvidenceMutationsFailClosed(string change)
    {
        using var fixture = new SignedPackageFixture(keys, production: overlay.Production);
        Rewrite(fixture, m =>
        {
            var p = m["provenance"]!;
            var host = p["applications"]![2]!;
            var library = host["libraries"]!.AsArray().Single(l => l!["key"]!.GetValue<string>().StartsWith("runtimepack.Microsoft.Windows.SDK.NET.Ref/", StringComparison.Ordinal))!;
            var archive = p["archives"]!.AsArray().Single(a => a!["id"]!.GetValue<string>() == "Microsoft.Windows.SDK.NET.Ref")!;
            var restores = p["restores"]!.AsArray();
            var target = restores.Single(a => a!["project"]!.GetValue<string>() == "Martlet.Avatar.RendererHost")!["targets"]![0]!;
            var downloads = target["frameworkDownloads"]!.AsArray();
            var download = downloads.Single(d => d!["id"]!.GetValue<string>() == "Microsoft.Windows.SDK.NET.Ref")!;
            var desktop = restores.Single(a => a!["project"]!.GetValue<string>() == "Martlet.Desktop")!["targets"]![0]!;
            switch (change)
            {
                case "framework": target["framework"] = "net10.0-windows10.0.22621"; break;
                case "target": target["name"] = "net10.0-windows/win-x64"; break;
                case "desktop-target":
                    desktop["name"] = target["name"]!.DeepClone();
                    desktop["framework"] = target["framework"]!.DeepClone();
                    break;
                case "desktop-download": desktop["frameworkDownloads"]!.AsArray().Add(download.DeepClone()); break;
                case "download-version": download["requested"] = "[10.0.12, 10.0.12]"; break;
                case "missing-download": downloads.Remove(download); break;
                case "duplicate-download": downloads.Add(download.DeepClone()); break;
                case "runtime-version": library["key"] = "runtimepack.Microsoft.Windows.SDK.NET.Ref/10.0.12"; break;
                case "archive-digest": archive["archiveSha512"] = new string('0', 128); break;
                case "asset-source": library["assets"]![0]!["source"] = "Microsoft.Windows.UI.Xaml.dll"; break;
                case "asset-kind": library["assets"]![0]!["kind"] = "native"; break;
                case "archive-entry": archive["origins"]![0]!["entry"] = "lib/net9.0/Microsoft.Windows.SDK.NET.dll"; break;
                case "archive-owner": archive["origins"]![0]!["component"] = "Desktop|runtimepack.Microsoft.Windows.SDK.NET.Ref/10.0.19041.57"; break;
                case "missing-origin": archive["origins"]!.AsArray().RemoveAt(0); break;
            }
        });
        Refused(fixture);
    }

    [Theory]
    [InlineData("ProjectLoader")]
    [InlineData("ProjectWebViewAlias")]
    [InlineData("ProjectWebViewXml")]
    [InlineData("ProjectRuntimeInstaller")]
    [InlineData("ProjectSiblingLoader")]
    [InlineData("OmittedRuntimeEdge")]
    public void ActualProducerRegenerationCannotHideUnsupportedBinariesOrRuntimeEdges(string mutation)
    {
        using var changed = new ProductionPayloadFixture(3, mutation);
        using var fixture = new SignedPackageFixture(keys, production: changed);
        Refused(fixture);
        changed.AssertNotExecuted();
    }

    [Fact]
    public void CanonicalEncoderMatchesActualPowerShellBytes()
    {
        var vectors = avatar.Production.CanonicalVectors();
        Assert.Equal(2, vectors.Length);
        Assert.Equal("401912d3eb94fc534d70853f7ed75d7d848d62a1ec5be6748a7c8b953bb9bdb2", Wire.Hash(vectors[0]));
        foreach (var bytes in vectors)
        {
            using var document = Wire.ReadDocument(bytes, PayloadMetadata.MaximumV2Bytes, default);
            Assert.Equal(bytes, PayloadEvidenceJson.Encode(document.RootElement, new(default)));
        }
    }

    public static IEnumerable<object[]> ClosedShapes()
    {
        string[] paths =
        [
            "provenance", "provenance/applications/2", "provenance/buildArchives/0", "provenance/buildArchives/0/uses/0",
            "provenance/browser", "provenance/browser/recipe", "provenance/browser/lockFiles/0",
            "provenance/browser/tools/0", "provenance/browser/tools/0/files/0", "provenance/browser/packages/0",
            "provenance/browser/inputs/0", "provenance/browser/outputs/0"
        ];
        foreach (var path in paths)
            foreach (var change in new[] { "unknown", "missing", "duplicate", "wrong-case", "null" })
                yield return [path, change];
    }

    private static JsonObject At(JsonNode root, string path)
    {
        foreach (var part in path.Split('/')) root = root is JsonArray ? root[int.Parse(part)]! : root[part]!;
        return root.AsObject();
    }

    [Theory]
    [MemberData(nameof(ClosedShapes))]
    public void NewObjectsStayClosedDespiteResigning(string path, string change)
    {
        using var fixture = Fixture();
        Rewrite(fixture, rawManifest: bytes =>
        {
            var root = JsonNode.Parse(bytes)!;
            var node = At(root, path);
            var property = node.First();
            if (change == "unknown") node.Add("extra", true);
            if (change == "missing") node.Remove(property.Key);
            if (change == "null") node[property.Key] = null;
            if (change == "wrong-case")
            {
                node.Remove(property.Key);
                node.Add(property.Key.ToUpperInvariant(), property.Value?.DeepClone());
            }
            if (change != "duplicate") return Encoding.UTF8.GetBytes(root.ToJsonString());
            var value = property.Value!.ToJsonString();
            node[property.Key] = "DUPLICATE-MARKER";
            return Encoding.UTF8.GetBytes(root.ToJsonString().Replace(
                $"\"{property.Key}\":\"DUPLICATE-MARKER\"", $"\"{property.Key}\":{value},\"{property.Key}\":{value}",
                StringComparison.Ordinal));
        });
        Refused(fixture);
    }

    [Theory]
    [InlineData("missing-host")]
    [InlineData("wrong-project")]
    [InlineData("wrong-parent")]
    [InlineData("sibling-prefix")]
    [InlineData("desktop-child-theft")]
    [InlineData("alias-version")]
    [InlineData("alias-kind")]
    [InlineData("alias-content-hash")]
    [InlineData("alias-dependency")]
    [InlineData("alias-asset")]
    [InlineData("xml-entry")]
    [InlineData("loader-hash")]
    [InlineData("missing-copy")]
    [InlineData("desktop-omission")]
    [InlineData("host-omission")]
    [InlineData("doctor-omission")]
    [InlineData("runtime-omission")]
    [InlineData("build-use-project")]
    [InlineData("build-use-hash")]
    [InlineData("build-archive-name")]
    [InlineData("doctor-graph")]
    public void ManagedContextAndBuildToolMutationsFailClosed(string change)
    {
        using var fixture = Fixture();
        Rewrite(fixture, m =>
        {
            var p = m["provenance"]!;
            var apps = p["applications"]!.AsArray();
            var host = apps[2]!;
            var reference = host["libraries"]!.AsArray().Single(l => l!["type"]!.GetValue<string>() == "reference" &&
                l["key"]!.GetValue<string>().Contains(".Core/"))!;
            var web = p["archives"]!.AsArray().Single(a => a!["id"]!.GetValue<string>() == "Microsoft.Web.WebView2")!;
            switch (change)
            {
                case "missing-host": apps.RemoveAt(2); break;
                case "wrong-project": host["project"] = "Martlet.Desktop"; break;
                case "wrong-parent": host["parent"] = "Doctor"; break;
                case "sibling-prefix": host["directory"] = "Desktop\\AvatarRendererX"; break;
                case "desktop-child-theft": apps[0]!["libraries"]![0]!["assets"]![0]!["path"] = "Desktop\\AvatarRenderer\\Martlet.Avatar.RendererHost.dll"; break;
                case "alias-version": reference["key"] = "Microsoft.Web.WebView2.Core/1.0.0"; break;
                case "alias-kind": reference["type"] = "project"; break;
                case "alias-content-hash": reference["contentHash"] = Convert.ToBase64String(new byte[64]); break;
                case "alias-dependency": reference["dependencies"]!.AsArray().Add("Microsoft.Web.WebView2/1.0.4191.47"); break;
                case "alias-asset": reference["assets"]![0]!["source"] = "other.dll"; break;
                case "xml-entry": web["origins"]![1]!["entry"] = "lib/net462/Microsoft.Web.WebView2.Core.xml"; break;
                case "loader-hash": web["origins"]![6]!["sha256"] = new string('f', 64); break;
                case "missing-copy": web["origins"]!.AsArray().RemoveAt(7); break;
                case "desktop-omission": apps[0]!["buildOnlyLibraries"]!.AsArray().Add("Martlet.Avatar.RendererHost/0.1.0"); break;
                case "host-omission": apps[2]!["buildOnlyLibraries"]!.AsArray().Add("Martlet.Core/0.1.0"); break;
                case "doctor-omission": apps[1]!["buildOnlyLibraries"]!.AsArray().Add("Martlet.Avatar.RendererHost/0.1.0"); break;
                case "runtime-omission": apps[0]!["buildOnlyLibraries"]!.AsArray().Add("Martlet.Avatar.Audio2Face/0.1.0"); break;
                case "build-use-project": p["buildArchives"]![0]!["uses"]![0]!["project"] = "Martlet.Desktop"; break;
                case "build-use-hash": p["buildArchives"]![0]!["uses"]![0]!["contentHash"] = Convert.ToBase64String(Enumerable.Repeat((byte)1, 64).ToArray()); break;
                case "build-archive-name": p["buildArchives"]![0]!["id"] = "Grpc.AspNetCore.Server"; break;
                case "doctor-graph": apps[1]!["libraries"]![0]!["dependencies"]!.AsArray().Clear(); break;
            }
        });
        Refused(fixture);
    }

    [Theory]
    [InlineData("scope")]
    [InlineData("sri")]
    [InlineData("package-key")]
    [InlineData("dangling-edge")]
    [InlineData("duplicate-edge")]
    [InlineData("source-hash")]
    [InlineData("lock-hash")]
    [InlineData("tool-version")]
    [InlineData("role")]
    [InlineData("package-entry")]
    [InlineData("no-notice")]
    [InlineData("output-hash")]
    [InlineData("missing-bundle-input")]
    [InlineData("missing-notice-input")]
    [InlineData("static-input")]
    [InlineData("metafile-sbom-binding")]
    [InlineData("missing-package")]
    [InlineData("missing-resolved-edge")]
    [InlineData("package-version")]
    public void BrowserGraphMutationsFailClosed(string change)
    {
        using var fixture = Fixture();
        Rewrite(fixture, m =>
        {
            var b = m["provenance"]!["browser"]!;
            var package = b["packages"]!.AsArray().Single(p => p!["name"]!.GetValue<string>() == "@pixiv/three-vrm")!;
            switch (change)
            {
                case "scope": package["scope"] = "build"; break;
                case "sri": package["archiveSha512"] = new string('1', 128); break;
                case "package-key": package["key"] = "node_modules/../three"; break;
                case "dangling-edge": package["dependencies"]![0] = "node_modules/missing"; break;
                case "duplicate-edge": package["dependencies"]!.AsArray().Add("node_modules/three"); break;
                case "source-hash": b["inputs"]![0]!["sha256"] = new string('f', 64); break;
                case "lock-hash": b["lockFiles"]![0]!["sha256"] = new string('f', 64); break;
                case "tool-version": b["tools"]![0]!["version"] = "99.0.0"; break;
                case "role": b["inputs"]![0]!["roles"]![0] = "arbitrary-origin"; break;
                case "package-entry":
                    b["inputs"]!.AsArray().First(i => i!["package"] is not null)!["entry"] = "package/../escape"; break;
                case "no-notice": package["notices"]!.AsArray().Clear(); break;
                case "output-hash": b["outputs"]![1]!["sha256"] = new string('f', 64); break;
                case "missing-bundle-input": b["outputs"]![1]!["inputs"]!.AsArray().RemoveAt(0); break;
                case "missing-notice-input": b["outputs"]![0]!["inputs"]!.AsArray().RemoveAt(0); break;
                case "static-input": b["outputs"]![3]!["inputs"]![0] = b["recipe"]!["script"]!.DeepClone(); break;
                case "metafile-sbom-binding": b["recipe"]!["metafileSha256"] = new string('f', 64); break;
                case "missing-package": b["packages"]!.AsArray().RemoveAt(0); break;
                case "missing-resolved-edge": package["dependencies"]!.AsArray().RemoveAt(0); break;
                case "package-version": package["version"] = "3.5.6"; break;
            }
        });
        Refused(fixture);
    }

    [Theory]
    [InlineData("missing-native")]
    [InlineData("missing-javascript")]
    [InlineData("wrong-package")]
    [InlineData("wrong-entry")]
    [InlineData("wrong-role")]
    [InlineData("extra-role")]
    [InlineData("native-hash")]
    [InlineData("native-length")]
    [InlineData("tool-path")]
    [InlineData("tool-hash")]
    [InlineData("tool-length")]
    [InlineData("runtime-bundle")]
    public void ObservedEsbuildMaterialHasExactBuildOnlyIdentityAndFingerprint(string change)
    {
        using var fixture = Fixture();
        Rewrite(fixture, manifest =>
        {
            var browser = manifest["provenance"]!["browser"]!;
            var inputs = browser["inputs"]!.AsArray();
            var native = inputs.Single(i => i!["entry"]?.GetValue<string>() == "package/esbuild.exe")!;
            var script = inputs.Single(i => i!["entry"]?.GetValue<string>() == "package/lib/main.js")!;
            var fingerprint = browser["tools"]![2]!["files"]![0]!;
            switch (change)
            {
                case "missing-native": inputs.Remove(native); break;
                case "missing-javascript": inputs.Remove(script); break;
                case "wrong-package": native["package"] = "node_modules/three"; break;
                case "wrong-entry": native["entry"] = "package/bin/other.exe"; break;
                case "wrong-role": native["roles"]![0] = "package-metadata"; break;
                case "extra-role": native["roles"]!.AsArray().Add("notice"); break;
                case "native-hash": native["sha256"] = new string('f', 64); break;
                case "native-length": native["bytes"] = native["bytes"]!.GetValue<long>() + 1; break;
                case "tool-path": fingerprint["path"] = "other.exe"; break;
                case "tool-hash": fingerprint["sha256"] = new string('f', 64); break;
                case "tool-length": fingerprint["bytes"] = fingerprint["bytes"]!.GetValue<long>() + 1; break;
                case "runtime-bundle": native["roles"]![0] = "bundle-source"; break;
            }
        });
        Refused(fixture);
    }

    [Theory]
    [InlineData("extra-web")]
    [InlineData("extra-exe")]
    [InlineData("extra-xml")]
    [InlineData("missing-web")]
    public void UnexpectedOrMissingFilesCannotBecomeDocuments(string change)
    {
        using var fixture = Fixture();
        if (change == "missing-web") fixture.Files.Remove("Desktop/AvatarRenderer/web/app.js");
        else fixture.Files["Desktop/AvatarRenderer/" + (change switch
        {
            "extra-web" => "web/extra.txt", "extra-exe" => "install.exe", _ => "extra.xml"
        })] = "inert"u8.ToArray();
        Rewrite(fixture);
        Refused(fixture);
    }

    private static JsonObject SbomObject(JsonObject root, string name)
    {
        var browser = root["components"]!.AsArray().Single(c => c!["bom-ref"]!.GetValue<string>() == PayloadBrowser.Reference)!;
        var npm = root["components"]!.AsArray().First(c => c!["bom-ref"]!.GetValue<string>().StartsWith("AvatarRenderer|npm:", StringComparison.Ordinal))!;
        return (name switch
        {
            "browser" => browser,
            "npm" => npm,
            "file" => browser["components"]![0]!,
            "file-property" => browser["components"]![0]!["properties"]![4]!,
            "browser-property" => browser["properties"]![2]!,
            "npm-property" => npm["properties"]![5]!,
            "browser-tool" => root["metadata"]!["tools"]!["components"]![2]!,
            "build-tool" => root["metadata"]!["tools"]!["components"]![5]!,
            "metadata-property" => root["metadata"]!["properties"]!.AsArray().Single(p => p!["name"]!.GetValue<string>() == "martlet:browser-evidence:sha256")!,
            _ => root["dependencies"]!.AsArray().Single(d => d!["ref"]!.GetValue<string>() == PayloadBrowser.Reference)!
        }).AsObject();
    }

    public static IEnumerable<object[]> ClosedSbomShapes()
    {
        foreach (var name in new[] { "browser", "npm", "file", "file-property", "browser-property",
                     "npm-property", "browser-tool", "build-tool", "metadata-property", "dependency" })
            foreach (var change in new[] { "unknown", "missing", "null", "duplicate", "wrong-case" })
                yield return [name, change];
    }

    [Theory]
    [MemberData(nameof(ClosedSbomShapes))]
    public void NewSbomObjectsRemainClosed(string name, string change)
    {
        using var fixture = Fixture();
        Rewrite(fixture, rawSbom: bytes =>
        {
            var root = JsonNode.Parse(bytes)!.AsObject();
            var node = SbomObject(root, name);
            var property = node.First();
            var value = property.Value!.ToJsonString();
            switch (change)
            {
                case "unknown": node.Add("unknown", true); break;
                case "missing": node.Remove(property.Key); break;
                case "null": node[property.Key] = null; break;
                case "wrong-case":
                    node.Remove(property.Key);
                    node.Add(property.Key.ToUpperInvariant(), property.Value.DeepClone());
                    break;
                case "duplicate": node[property.Key] = "DUPLICATE-MARKER"; break;
            }
            var json = root.ToJsonString();
            if (change == "duplicate")
                json = json.Replace($"\"{property.Key}\":\"DUPLICATE-MARKER\"",
                    $"\"{property.Key}\":{value},\"{property.Key}\":{value}", StringComparison.Ordinal);
            return Encoding.UTF8.GetBytes(json);
        });
        Refused(fixture);
    }

    [Theory]
    [InlineData("browser-digest")]
    [InlineData("input-digest")]
    [InlineData("notices-digest")]
    [InlineData("uses-digest")]
    [InlineData("tool-hash")]
    [InlineData("fake-reference-purl")]
    [InlineData("duplicate-owner")]
    [InlineData("runtime-build-edge")]
    [InlineData("missing-host-edge")]
    [InlineData("dangling-software")]
    public void NewSbomEvidenceCannotDriftDespiteResigning(string change)
    {
        using var fixture = Fixture();
        Rewrite(fixture, sbom: root =>
        {
            var components = root["components"]!.AsArray();
            switch (change)
            {
                case "browser-digest": SbomObject(root, "metadata-property")["value"] = new string('f', 64); break;
                case "input-digest": SbomObject(root, "file-property")["value"] = new string('f', 64); break;
                case "notices-digest": SbomObject(root, "npm")["properties"]![6]!["value"] = new string('f', 64); break;
                case "uses-digest": SbomObject(root, "build-tool")["properties"]![2]!["value"] = new string('f', 64); break;
                case "tool-hash": SbomObject(root, "browser-tool")["properties"]![1]!["value"] = new string('f', 64); break;
                case "fake-reference-purl":
                    components.Single(c => c!["bom-ref"]!.GetValue<string>() == "AvatarRenderer|Microsoft.Web.WebView2.Core/1.0.4191.47")!["purl"] =
                        "pkg:nuget/microsoft.web.webview2.core@1.0.4191.47"; break;
                case "duplicate-owner": SbomObject(root, "browser")["components"]![1] = SbomObject(root, "file").DeepClone(); break;
                case "runtime-build-edge": SbomObject(root, "dependency")["dependsOn"]!.AsArray().Insert(0, "AvatarRenderer|npm:node_modules/@esbuild/win32-x64"); break;
                case "missing-host-edge":
                    root["dependencies"]!.AsArray().Single(d => d!["ref"]!.GetValue<string>() == "Desktop|Martlet.Desktop/0.1.0")!["dependsOn"]!
                        .AsArray().RemoveAt(0); break;
                case "dangling-software": SbomObject(root, "npm")["bom-ref"] = "AvatarRenderer|npm:node_modules/unknown"; break;
            }
        });
        Refused(fixture);
    }

    [Theory]
    [InlineData("manifest.json", false)]
    [InlineData("manifest.json", true)]
    [InlineData("sbom.cdx.json", false)]
    [InlineData("sbom.cdx.json", true)]
    public void AvatarMetadataRetainsExactExistingByteCeilings(string name, bool overflow)
    {
        using var fixture = Fixture();
        byte[] Pad(byte[] bytes) => bytes.Concat(Enumerable.Repeat((byte)' ',
            PayloadMetadata.MaximumV2Bytes + (overflow ? 1 : 0) - bytes.Length)).ToArray();
        Rewrite(fixture, rawManifest: name == "manifest.json" ? Pad : null, rawSbom: name == "sbom.cdx.json" ? Pad : null);
        if (overflow) Refused(fixture, StagingFailure.CapacityExceeded);
        else fixture.Preview(fixture.Engine());
        fixture.AssertPrivateDataUnchanged();
    }

    [Theory]
    [InlineData("tool-files", 33)]
    [InlineData("packages", 257)]
    [InlineData("inputs", 8193)]
    [InlineData("roles", 7)]
    [InlineData("output-inputs", 8193)]
    [InlineData("build-archives", 17)]
    [InlineData("build-uses", 1025)]
    public void NewCollectionsHaveIndependentFiniteLimits(string area, int count)
    {
        using var fixture = Fixture();
        Rewrite(fixture, manifest =>
        {
            var p = manifest["provenance"]!;
            var b = p["browser"]!;
            var collection = (area switch
            {
                "tool-files" => b["tools"]![0]!["files"],
                "packages" => b["packages"],
                "inputs" => b["inputs"],
                "roles" => b["inputs"]![0]!["roles"],
                "output-inputs" => b["outputs"]![0]!["inputs"],
                "build-archives" => p["buildArchives"],
                _ => p["buildArchives"]![0]!["uses"]
            })!.AsArray();
            while (collection.Count < count) collection.Add(collection[0]!.DeepClone());
        });
        Refused(fixture, StagingFailure.CapacityExceeded);
    }

    [Theory]
    [InlineData("manifest", StagingFailure.IncompatibleFormat)]
    [InlineData("provenance", StagingFailure.IncompatibleFormat)]
    [InlineData("browser", StagingFailure.IncompatibleFormat)]
    [InlineData("sbom", StagingFailure.IncompatibleFormat)]
    [InlineData("legacy-relabel", StagingFailure.InvalidManifest)]
    public void ExplicitVersionsNeverFallThroughToOtherFormats(string area, StagingFailure failure)
    {
        using var fixture = Fixture();
        Rewrite(fixture, manifest =>
        {
            if (area == "manifest") manifest["schemaVersion"] = 4;
            if (area == "provenance") manifest["provenance"]!["schemaVersion"] = 3;
            if (area == "browser") manifest["provenance"]!["browser"]!["schemaVersion"] = 2;
            if (area == "legacy-relabel") manifest["schemaVersion"] = 2;
        }, sbom => { if (area == "sbom") sbom["version"] = 2; });
        Refused(fixture, failure);
    }

    [Theory]
    [InlineData(1, 3)]
    [InlineData(2, 3)]
    [InlineData(3, 1)]
    [InlineData(3, 2)]
    [InlineData(3, 3)]
    public void MixedRetainedHistoriesRemainByteExactAndNonRunnable(int older, int newer)
    {
        ProductionPayloadFixture? Producer(int format) => format switch { 1 => null, 2 => legacy, _ => avatar.Production };
        using var fixture = new SelectionFixture(keys);
        fixture.Initialize();
        var old = fixture.Stage(production: Producer(older));
        fixture.Select(old);
        var bytes = Directory.GetFiles(old, "*", SearchOption.AllDirectories).ToDictionary(p => p, File.ReadAllBytes);
        var current = fixture.Select(fixture.Stage("0.3.0.0", production: Producer(newer)));
        Assert.False(current.IsRunnable);
        fixture.Package.Current = fixture.Package.Current with { Version = "0.3.0.0" };
        fixture.ChangeSettings();
        var engine = fixture.Engine();
        var plan = engine.PrepareRollback(current.Revision, fixture.Snapshot());
        var result = engine.CommitSelection(plan, SelectionFixture.Approve(plan));
        Assert.False(result.IsRunnable);
        Assert.Equal(SelectionStatus.AwaitingConfigurationRestore, result.Status);
        Assert.False(fixture.Engine().Inspect().IsRunnable);
        foreach (var (path, content) in bytes) Assert.Equal(content, File.ReadAllBytes(path));
        fixture.Package.AssertPrivateDataUnchanged();
        avatar.Production.AssertNotExecuted();
    }
}
