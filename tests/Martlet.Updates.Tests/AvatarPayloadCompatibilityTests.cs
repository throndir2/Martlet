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

public sealed class AvatarPayloadCompatibilityTests(SigningKeys keys, AvatarPayloadFixture avatar,
    ProductionPayloadFixture legacy) : IClassFixture<SigningKeys>, IClassFixture<AvatarPayloadFixture>,
    IClassFixture<ProductionPayloadFixture>
{
    private SignedPackageFixture Fixture() => new(keys, production: avatar.Production);

    private static void Rewrite(SignedPackageFixture fixture, Action<JsonObject>? manifest = null,
        Action<JsonObject>? sbom = null, Func<byte[], byte[]>? rawManifest = null)
    {
        if (sbom is not null)
        {
            var node = JsonNode.Parse(fixture.Files["sbom.cdx.json"])!.AsObject();
            sbom(node);
            fixture.Files["sbom.cdx.json"] = Encoding.UTF8.GetBytes(node.ToJsonString());
        }
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
    [InlineData("undeclared-omission")]
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
                case "undeclared-omission": apps[0]!["buildOnlyLibraries"]!.AsArray().Clear(); break;
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
