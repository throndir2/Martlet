using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using Martlet.Updates;
using static Martlet.Updates.Tests.SignedPackageFixture;

namespace Martlet.Updates.Tests;

public sealed class PayloadCompatibilityTests(SigningKeys keys, ProductionPayloadFixture production)
    : IClassFixture<SigningKeys>, IClassFixture<ProductionPayloadFixture>
{
    [Fact]
    public void ProductionSerializedPayloadStagesAndReopensWithoutChangingBytes()
    {
        using var f = new SignedPackageFixture(keys, production: production);
        var engine = f.Engine();
        var plan = f.Preview(engine);
        var receipt = engine.Stage(plan, Approve(plan));
        Assert.Equal(receipt.ReceiptSha256, f.Engine().InspectStaged(f.Destination).ReceiptSha256);
        foreach (var file in f.Files)
            Assert.Equal(file.Value, File.ReadAllBytes(Path.Combine(f.Destination, "payload",
                file.Key.Replace('/', Path.DirectorySeparatorChar))));
        Assert.Equal(File.ReadAllBytes(f.Archive), File.ReadAllBytes(Path.Combine(f.Destination, "candidate.zip")));
        Assert.Equal(File.ReadAllBytes(f.Envelope), File.ReadAllBytes(Path.Combine(f.Destination, "candidate.json")));
        f.AssertPrivateDataUnchanged();
    }

    private static byte[] Json(JsonNode value) => Encoding.UTF8.GetBytes(value.ToJsonString());

    private static void Rewrite(SignedPackageFixture f, Action<JsonObject>? manifest = null,
        Action<JsonObject>? sbom = null, Func<byte[], byte[]>? rawManifest = null, Func<byte[], byte[]>? rawSbom = null)
    {
        if (sbom is not null)
        {
            var node = JsonNode.Parse(f.Files["sbom.cdx.json"])!.AsObject();
            sbom(node);
            f.Files["sbom.cdx.json"] = Json(node);
        }
        if (rawSbom is not null) f.Files["sbom.cdx.json"] = rawSbom(f.Files["sbom.cdx.json"]);
        var files = f.Inventory().Where(e => e.Path is not ("manifest.json" or "SHA256SUMS.txt"))
            .Select(e => e with { Path = e.Path.Replace('/', '\\') }).OrderBy(e => e.Path, StringComparer.Ordinal).ToArray();
        var payload = JsonNode.Parse(f.Files["manifest.json"])!.AsObject();
        payload["files"] = JsonNode.Parse(Wire.Write(files));
        manifest?.Invoke(payload);
        var bytes = Json(payload);
        f.Files["manifest.json"] = rawManifest is null ? bytes : rawManifest(bytes);
        f.Files["SHA256SUMS.txt"] = Encoding.UTF8.GetBytes(string.Concat(files.Select(e => $"{e.Sha256}  {e.Path}\n")) +
            $"{Wire.Hash(f.Files["manifest.json"])}  manifest.json\n");
        f.WriteZip(f.Files.Select(e => new Entry(e.Key, e.Value)));
        f.ResignArchive(updateInventory: true);
    }

    private static JsonObject At(JsonNode root, string path)
    {
        var current = root;
        foreach (var segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
            current = current is JsonArray ? current[int.Parse(segment)]! : current[segment]!;
        return current.AsObject();
    }

    private static void Fails(SignedPackageFixture f, StagingFailure failure, LocalStagingEngine? engine = null)
    {
        var error = Assert.Throws<StagingException>(() => f.Preview(engine ?? f.Engine()));
        Assert.Equal(failure, error.Failure);
        Assert.DoesNotContain(f.Root, error.ToString());
        Assert.DoesNotContain("PRIVATE-SENTINEL", error.ToString());
        Assert.Empty(Directory.EnumerateFileSystemEntries(f.StagingRoot));
        f.AssertPrivateDataUnchanged();
    }

    public static IEnumerable<object[]> ClosedObjects()
    {
        string[] manifests = ["", "files/0", "provenance", "provenance/source", "provenance/source/files/0",
            "provenance/sdk", "provenance/sdk/files/0", "provenance/publish", "provenance/applications/0",
            "provenance/applications/0/libraries/0", "provenance/applications/0/libraries/0/assets/0",
            "provenance/restores/0", "provenance/restores/0/targets/0", "provenance/restores/0/targets/0/libraries/0",
            "provenance/restores/0/targets/0/frameworkDownloads/0", "provenance/archives/0", "provenance/archives/0/origins/0"];
        string[] sboms = ["", "metadata", "metadata/component", "metadata/properties/0", "metadata/tools",
            "metadata/tools/components/0", "metadata/tools/components/1", "metadata/tools/components/1/properties/0",
            "components/0", "components/0/properties/0", "components/0/components/0",
            "components/0/components/0/hashes/0", "components/0/components/0/properties/0",
            "components/1/licenses/0", "components/1/externalReferences/0", "dependencies/0", "compositions/0"];
        foreach (var (file, paths) in new[] { ("manifest.json", manifests), ("sbom.cdx.json", sboms) })
            foreach (var path in paths)
                foreach (var change in new[] { "unknown", "missing", "null", "duplicate" })
                    yield return [file, path, change];
    }

    [Theory]
    [MemberData(nameof(ClosedObjects))]
    public void SignedRechecksummedObjectsRemainClosed(string file, string path, string change)
    {
        using var f = new SignedPackageFixture(keys, production: production);
        byte[] Mutate(byte[] bytes)
        {
            var root = JsonNode.Parse(bytes)!;
            var target = At(root, path);
            var first = target.First();
            if (change == "unknown") target.Add("unsupported", true);
            if (change == "missing") target.Remove(first.Key);
            if (change == "null") target[first.Key] = null;
            if (change != "duplicate") return Json(root);
            var value = first.Value!.ToJsonString();
            target[first.Key] = "DUPLICATE-FIXTURE-MARKER";
            var text = root.ToJsonString().Replace($"\"{first.Key}\":\"DUPLICATE-FIXTURE-MARKER\"",
                $"\"{first.Key}\":{value},\"{first.Key}\":{value}", StringComparison.Ordinal);
            return Encoding.UTF8.GetBytes(text);
        }
        Rewrite(f, rawManifest: file == "manifest.json" ? Mutate : null, rawSbom: file == "sbom.cdx.json" ? Mutate : null);
        Fails(f, StagingFailure.InvalidManifest);
    }

    [Theory]
    [InlineData("future-manifest", StagingFailure.IncompatibleFormat)]
    [InlineData("future-provenance", StagingFailure.IncompatibleFormat)]
    [InlineData("future-sbom", StagingFailure.IncompatibleFormat)]
    [InlineData("future-cyclonedx", StagingFailure.IncompatibleFormat)]
    [InlineData("v2-relabeled-v1", StagingFailure.InvalidManifest)]
    [InlineData("missing-sbom", StagingFailure.InvalidManifest)]
    [InlineData("case-sbom", StagingFailure.UnsafeEntry)]
    [InlineData("extra-root", StagingFailure.UnsafeEntry)]
    [InlineData("source-commit", StagingFailure.InvalidManifest)]
    [InlineData("source-dirty", StagingFailure.InvalidManifest)]
    [InlineData("sdk-version", StagingFailure.InvalidManifest)]
    [InlineData("runtime-version", StagingFailure.InvalidManifest)]
    [InlineData("publish-rid", StagingFailure.InvalidManifest)]
    [InlineData("sbom-version", StagingFailure.InvalidManifest)]
    [InlineData("sbom-source", StagingFailure.InvalidManifest)]
    [InlineData("sbom-tool", StagingFailure.InvalidManifest)]
    [InlineData("file-hash", StagingFailure.InvalidManifest)]
    [InlineData("file-size", StagingFailure.InvalidManifest)]
    [InlineData("duplicate-file", StagingFailure.InvalidManifest)]
    [InlineData("missing-file", StagingFailure.InvalidManifest)]
    [InlineData("circular-file", StagingFailure.InvalidManifest)]
    [InlineData("dangling-edge", StagingFailure.InvalidManifest)]
    [InlineData("duplicate-edge", StagingFailure.InvalidManifest)]
    [InlineData("restore-version", StagingFailure.InvalidManifest)]
    [InlineData("restore-lock", StagingFailure.InvalidManifest)]
    [InlineData("origin-hash", StagingFailure.InvalidManifest)]
    [InlineData("source-null-nonzero", StagingFailure.InvalidManifest)]
    [InlineData("inventory-order", StagingFailure.InvalidManifest)]
    [InlineData("checksum", StagingFailure.InvalidManifest)]
    public void VersionAndEvidenceBindingsSurviveResigning(string change, StagingFailure failure)
    {
        using var f = new SignedPackageFixture(keys, production: production);
        if (change == "missing-sbom") f.Files.Remove("sbom.cdx.json");
        if (change == "case-sbom")
        {
            f.Files["SBOM.cdx.json"] = f.Files["sbom.cdx.json"];
            f.Files.Remove("sbom.cdx.json");
        }
        if (change == "extra-root") f.Files["provenance.json"] = "{}"u8.ToArray();
        Rewrite(f, m =>
        {
            switch (change)
            {
                case "future-manifest": m["schemaVersion"] = 3; break;
                case "future-provenance": m["provenance"]!["schemaVersion"] = 2; break;
                case "v2-relabeled-v1": m["schemaVersion"] = 1; break;
                case "source-commit": m["provenance"]!["source"]!["commit"] = new string('d', 40); break;
                case "source-dirty": m["provenance"]!["source"]!["dirty"] = false; break;
                case "sdk-version": m["provenance"]!["sdk"]!["version"] = "10.0.999"; break;
                case "runtime-version": m["runtimeVersion"] = "10.0.999"; break;
                case "publish-rid": m["provenance"]!["publish"]!["rid"] = "win-arm64"; break;
                case "dangling-edge": m["provenance"]!["applications"]![0]!["libraries"]![0]!["dependencies"]![0] = "missing/1"; break;
                case "duplicate-edge":
                    var edges = m["provenance"]!["applications"]![0]!["libraries"]![0]!["dependencies"]!.AsArray();
                    edges.Add(edges[0]!.DeepClone()); break;
                case "restore-version": m["provenance"]!["restores"]![0]!["version"] = "9.0.0"; break;
                case "restore-lock": m["provenance"]!["restores"]![0]!["lockSha256"] = new string('1', 64); break;
                case "origin-hash": m["provenance"]!["archives"]![0]!["origins"]![0]!["sha256"] = new string('1', 64); break;
                case "source-null-nonzero": m["provenance"]!["source"]!["files"]![0]!["sha256"] = null; break;
                case "inventory-order":
                    var files = m["files"]!.AsArray();
                    var first = files[0]!.DeepClone(); files[0] = files[1]!.DeepClone(); files[1] = first; break;
            }
        }, change is "missing-sbom" or "case-sbom" ? null : s =>
        {
            var components = s["components"]!.AsArray();
            switch (change)
            {
                case "future-sbom": s["version"] = 2; break;
                case "future-cyclonedx": s["specVersion"] = "1.7"; break;
                case "sbom-version": s["metadata"]!["component"]!["version"] = "9.0.0.0"; break;
                case "sbom-source": s["metadata"]!["properties"]![1]!["value"] = new string('d', 40); break;
                case "sbom-tool": s["metadata"]!["tools"]!["components"]![1]!["version"] = "10.0.999"; break;
                case "file-hash": components[0]!["components"]![0]!["hashes"]![0]!["content"] = new string('1', 64); break;
                case "file-size": components[0]!["components"]![0]!["properties"]![1]!["value"] = "0"; break;
                case "duplicate-file": components.Add(components[^1]!.DeepClone()); break;
                case "missing-file": components.RemoveAt(components.Count - 1); break;
                case "circular-file":
                    var file = components[^1]!;
                    file["name"] = "sbom.cdx.json"; file["bom-ref"] = "file:sbom.cdx.json"; break;
            }
        });
        if (change == "checksum")
        {
            f.Files["SHA256SUMS.txt"][0] ^= 1;
            f.WriteZip(f.Files.Select(e => new Entry(e.Key, e.Value)));
            f.ResignArchive(updateInventory: true);
        }
        Fails(f, failure);
    }

    private static byte[] Pad(byte[] bytes, int length) =>
        bytes.Concat(Enumerable.Repeat((byte)' ', length - bytes.Length)).ToArray();

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void VersionedManifestByteCeilingsAreInclusive(bool current, bool overflow)
    {
        using var f = new SignedPackageFixture(keys, production: current ? production : null);
        var maximum = current ? PayloadMetadata.MaximumV2Bytes : Wire.MaximumManifestBytes;
        Rewrite(f, rawManifest: bytes => Pad(bytes, maximum + (overflow ? 1 : 0)));
        if (overflow) Fails(f, StagingFailure.CapacityExceeded);
        else
        {
            var engine = f.Engine();
            var plan = f.Preview(engine);
            engine.Stage(plan, Approve(plan));
            f.Engine().InspectStaged(f.Destination);
            f.AssertPrivateDataUnchanged();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SbomByteCeilingIsIndependentOfOuterBudget(bool overflow)
    {
        using var f = new SignedPackageFixture(keys, production: production);
        Rewrite(f, rawSbom: bytes => Pad(bytes, PayloadMetadata.MaximumV2Bytes + (overflow ? 1 : 0)));
        Assert.True(new FileInfo(f.Envelope).Length < Wire.MaximumEnvelopeBytes);
        if (overflow) Fails(f, StagingFailure.CapacityExceeded);
        else f.Preview(f.Engine());
        f.AssertPrivateDataUnchanged();
    }

    [Theory]
    [InlineData("manifest.json")]
    [InlineData("sbom.cdx.json")]
    [InlineData("SHA256SUMS.txt")]
    public void DeclaredMetadataOverflowRefusedBeforeArchiveOpen(string name)
    {
        using var f = new SignedPackageFixture(keys, production: production);
        f.Sign(f.Manifest with { Files = f.Manifest.Files.Select(e => e.Path == name
            ? e with { Bytes = PayloadMetadata.MaximumV2Bytes + 1L } : e).ToArray() });
        File.Delete(f.Archive);
        Fails(f, StagingFailure.CapacityExceeded);
    }

    [Theory]
    [InlineData(16384, true)]
    [InlineData(16385, false)]
    public void SourceInventoryCountHasItsOwnBound(int count, bool accepted)
    {
        using var f = new SignedPackageFixture(keys, production: production);
        Rewrite(f, m =>
        {
            var files = m["provenance"]!["source"]!["files"]!.AsArray();
            while (files.Count > 3) files.RemoveAt(files.Count - 1);
            while (files.Count < count) files.Add(new JsonObject
            {
                ["path"] = $"source\\{files.Count:D5}.txt", ["bytes"] = 0, ["sha256"] = null
            });
        });
        if (accepted) f.Preview(f.Engine()); else Fails(f, StagingFailure.CapacityExceeded);
        f.AssertPrivateDataUnchanged();
    }

    [Theory]
    [InlineData(1024, true)]
    [InlineData(1025, false)]
    public void DescriptiveSourcePathsNeverBecomeExtractionPaths(int length, bool accepted)
    {
        using var f = new SignedPackageFixture(keys, production: production);
        Rewrite(f, m => m["provenance"]!["source"]!["files"]![3]!["path"] = "source\\" + new string('\u00e9', length - 7));
        if (accepted) f.Preview(f.Engine()); else Fails(f, StagingFailure.CapacityExceeded);
        f.AssertPrivateDataUnchanged();
    }

    [Theory]
    [InlineData(16, true)]
    [InlineData(17, false)]
    public void SharedStrictParserDepthIsNotWidened(int depth, bool accepted)
    {
        var bytes = Encoding.UTF8.GetBytes(new string('[', depth) + "0" + new string(']', depth));
        if (accepted)
        {
            using var parsed = Wire.ReadDocument(bytes, PayloadMetadata.MaximumV2Bytes, default);
            Assert.Equal(depth, bytes.Count(b => b == '['));
        }
        else Assert.Equal(StagingFailure.InvalidManifest,
            Assert.Throws<StagingException>(() => Wire.ReadDocument(bytes, PayloadMetadata.MaximumV2Bytes, default)).Failure);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ObservedMetadataLengthAndCancellationAreChecked(bool cancel)
    {
        using var f = new SignedPackageFixture(keys, production: production);
        Rewrite(f, rawManifest: bytes => Pad(bytes, 256 * 1024));
        using var cancellation = new CancellationTokenSource();
        var reads = 0;
        Stream Open(string name)
        {
            var bytes = f.Files[name];
            if (name != "manifest.json") return new MemoryStream(bytes, writable: false);
            if (!cancel) return new MemoryStream(bytes.Append((byte)' ').ToArray(), writable: false);
            return new ObservedStream(bytes, () => { reads++; cancellation.Cancel(); });
        }
        var error = Assert.ThrowsAny<Exception>(() => PayloadMetadata.Verify(f.Manifest, Open, cancellation.Token));
        if (cancel) { Assert.IsType<OperationCanceledException>(error); Assert.Equal(1, reads); }
        else Assert.Equal(StagingFailure.CorruptArchive, Assert.IsType<StagingException>(error).Failure);
        f.AssertPrivateDataUnchanged();
    }

    private sealed class ObservedStream(byte[] bytes, Action observed) : MemoryStream(bytes, writable: false)
    {
        public override int Read(byte[] buffer, int offset, int count)
        {
            var result = base.Read(buffer, offset, count);
            observed();
            return result;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MixedLegacyCurrentSelectionsRetainHistoricalBytesAndNeverBecomeRunnable(bool olderCurrent)
    {
        using var f = new SelectionFixture(keys);
        f.Initialize();
        var older = f.Stage(production: olderCurrent ? production : null);
        f.Select(older);
        var oldFiles = Directory.GetFiles(older, "*", SearchOption.AllDirectories).ToDictionary(p => p, File.ReadAllBytes);
        var history = Directory.GetDirectories(f.Root, "transaction-*").SelectMany(d =>
            Directory.GetFiles(d, "*", SearchOption.AllDirectories)).ToDictionary(p => p, File.ReadAllBytes);
        var latest = f.Select(f.Stage("0.3.0.0", production: olderCurrent ? null : production));
        f.Package.Current = f.Package.Current with { Version = "0.3.0.0" };
        Assert.Equal(StagingFailure.InvalidVersion, Assert.Throws<StagingException>(() => f.Staging.InspectStaged(older)).Failure);
        Assert.False(f.Engine().Inspect().IsRunnable);
        f.ChangeSettings();
        var engine = f.Engine();
        var plan = engine.PrepareRollback(latest.Revision, f.Snapshot());
        var rolledBack = engine.CommitSelection(plan, SelectionFixture.Approve(plan));
        Assert.Equal("0.2.0.0", rolledBack.CurrentSelection!.Version);
        Assert.False(rolledBack.IsRunnable);
        Assert.Equal(SelectionStatus.AwaitingConfigurationRestore, rolledBack.Status);
        Assert.False(f.Engine().Inspect().IsRunnable);
        foreach (var file in oldFiles.Concat(history)) Assert.Equal(file.Value, File.ReadAllBytes(file.Key));
        Assert.Equal(f.Package.Current.SettingsRevision.ToUpperInvariant(), f.Package.Current.SettingsRevision);
        f.Package.AssertPrivateDataUnchanged();
        production.AssertNotExecuted();
    }

    [Theory]
    [InlineData("manifest.json")]
    [InlineData("sbom.cdx.json")]
    public void MalformedEscapedUnicodeIsAnExplicitManifestFailure(string file)
    {
        using var f = new SignedPackageFixture(keys, production: production);
        byte[] Change(byte[] bytes) => Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(bytes).Replace(
            file == "manifest.json" ? PayloadMetadata.Channel : "CycloneDX", "\\ud800", StringComparison.Ordinal));
        Rewrite(f, rawManifest: file == "manifest.json" ? Change : null, rawSbom: file == "sbom.cdx.json" ? Change : null);
        Fails(f, StagingFailure.InvalidManifest);
    }

    [Theory]
    [InlineData("sdk", 4)]
    [InlineData("applications", 3)]
    [InlineData("libraries", 2049)]
    [InlineData("restores", 129)]
    [InlineData("targets", 9)]
    [InlineData("downloads", 17)]
    public void NestedEvidenceCollectionsCannotEscapeTheirOwnBudget(string area, int count)
    {
        using var f = new SignedPackageFixture(keys, production: production);
        Rewrite(f, m =>
        {
            var p = m["provenance"]!;
            var array = (area switch
            {
                "sdk" => p["sdk"]!["files"],
                "applications" => p["applications"],
                "libraries" => p["applications"]![0]!["libraries"],
                "restores" => p["restores"],
                "targets" => p["restores"]![0]!["targets"],
                _ => p["restores"]![0]!["targets"]![0]!["frameworkDownloads"]
            })!.AsArray();
            while (array.Count < count) array.Add(array[0]!.DeepClone());
        });
        Fails(f, StagingFailure.CapacityExceeded);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LegacyCannotGainEvidenceByRelabelingOrAddingSbom(bool relabel)
    {
        using var f = new SignedPackageFixture(keys);
        if (!relabel) f.Files["sbom.cdx.json"] = "{}"u8.ToArray();
        Rewrite(f, m => { if (relabel) m["schemaVersion"] = 2; });
        Fails(f, StagingFailure.InvalidManifest);
    }

    [Theory]
    [InlineData("manifest.json")]
    [InlineData("sbom.cdx.json")]
    public void MetadataStillRequiresActualZipCrcEvenAfterResigning(string name)
    {
        using var f = new SignedPackageFixture(keys, production: production);
        var bytes = File.ReadAllBytes(f.Archive);
        var position = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(bytes.Length - 6));
        var found = false;
        for (var index = 0; index < f.Manifest.Files.Length; index++)
        {
            var length = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(position + 28));
            if (Encoding.ASCII.GetString(bytes, position + 46, length) == name)
            {
                var local = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(position + 42));
                bytes[position + 16] ^= 1;
                bytes[local + 14] ^= 1;
                found = true;
                break;
            }
            position += 46 + length;
        }
        Assert.True(found);
        File.WriteAllBytes(f.Archive, bytes);
        f.ResignArchive();
        Fails(f, StagingFailure.CorruptArchive);
    }

    [Fact]
    public void MetadataIterationChecksOriginalCancellation()
    {
        using var cancel = new CancellationTokenSource();
        using var document = Wire.ReadDocument("[1,2]"u8.ToArray(), 16, default);
        using var records = new EvidenceReader(cancel.Token).Items(document.RootElement, 2).GetEnumerator();
        Assert.True(records.MoveNext());
        cancel.Cancel();
        Assert.Throws<OperationCanceledException>(() => records.MoveNext());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LargeMetadataFailureRetainsNormalExactCleanupOwnership(bool cancel)
    {
        using var f = new SignedPackageFixture(keys, production: production);
        Rewrite(f, rawManifest: bytes => Pad(bytes, 256 * 1024));
        using var cancellation = new CancellationTokenSource();
        var holdCleanup = true;
        var writes = 0;
        LocalStagingEngine? engine = null;
        engine = f.Engine((point, _) =>
        {
            if (point == StagingIoPoint.BeforeCleanup && holdCleanup) throw new IOException("retain owned cleanup");
            if (point != StagingIoPoint.BeforeWrite || engine!.PendingCleanupDirectory is not { } directory ||
                !File.Exists(Path.Combine(directory, "payload", "manifest.json")) || ++writes != 2) return;
            if (cancel) cancellation.Cancel();
            else throw new IOException("controlled metadata write failure");
        });
        var plan = f.Preview(engine);
        var error = Assert.Throws<StagingException>(() => engine.Stage(plan, Approve(plan), cancellation.Token));
        Assert.Equal(StagingFailure.CleanupPending, error.Failure);
        Assert.Equal(cancel ? StagingFailure.Cancelled : StagingFailure.Unavailable, error.OriginalFailure);
        Assert.Equal(engine.PendingCleanupDirectory, error.RetainedDirectory);
        Assert.False(Directory.Exists(f.Destination));
        holdCleanup = false;
        engine.RetryCleanup();
        Assert.Null(engine.PendingCleanupDirectory);
        f.AssertPrivateDataUnchanged();
    }

    [Fact]
    public void CurrentStageReinspectionStillRequiresCurrentSignerPolicyAndExactMetadata()
    {
        using var f = new SignedPackageFixture(keys, production: production);
        var engine = f.Engine();
        var plan = f.Preview(engine);
        var receipt = engine.Stage(plan, Approve(plan));
        var retained = File.ReadAllBytes(Path.Combine(f.Destination, "staged.json"));
        var revoked = new LocalStagingEngine(f.StagingRoot, new([keys.Stranger.ExportSubjectPublicKeyInfo()]), () => f.Current);
        Assert.Equal(StagingFailure.UntrustedSignature,
            Assert.Throws<StagingException>(() => revoked.InspectStaged(f.Destination)).Failure);
        File.AppendAllText(Path.Combine(f.Destination, "payload", "sbom.cdx.json"), " ");
        Assert.Equal(StagingFailure.InvalidReceipt,
            Assert.Throws<StagingException>(() => f.Engine().InspectStaged(f.Destination)).Failure);
        Assert.Equal(retained, File.ReadAllBytes(Path.Combine(f.Destination, "staged.json")));
        Assert.Equal(Wire.Hash(retained), receipt.ReceiptSha256);
        f.AssertPrivateDataUnchanged();
    }

    [Fact]
    public void LargerInternalDocumentsDoNotWidenExternalOrCallerBudgets()
    {
        Assert.Equal(2 * 1024 * 1024, Wire.MaximumManifestBytes);
        Assert.Equal(3 * 1024 * 1024, Wire.MaximumEnvelopeBytes);
        Assert.Equal(3 * 1024 * 1024, Wire.MaximumReceiptBytes);
        using var f = new SignedPackageFixture(keys, production: production);
        Rewrite(f, rawManifest: bytes => Pad(bytes, 256 * 1024));
        Fails(f, StagingFailure.CapacityExceeded, f.Engine(limits: new() { MaximumFileBytes = 128 * 1024 }));
        Assert.Equal(StagingFailure.CapacityExceeded, Assert.Throws<StagingException>(() =>
            Wire.Read<CandidateManifest>(new byte[Wire.MaximumManifestBytes + 1], Wire.MaximumManifestBytes)).Failure);
        Assert.Equal(StagingFailure.CapacityExceeded, Assert.Throws<StagingException>(() =>
            Wire.Read<ReceiptDocument>(new byte[Wire.MaximumReceiptBytes + 1], Wire.MaximumReceiptBytes)).Failure);
    }

    [Theory]
    [InlineData("Failure")]
    [InlineData("OutputOverflow")]
    [InlineData("Wait")]
    public async Task ProducerFailuresRetireTheirExactChildAndKeepItsArtifacts(string mode)
    {
        var root = Path.Combine(Path.GetTempPath(), "Martlet.ProducerFailure-" + Guid.NewGuid().ToString("N"));
        var retiredPid = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() => ProductionPayloadFixture.Produce(root, mode,
            mode == "Wait" ? TimeSpan.FromSeconds(3) : null, pid => retiredPid = pid));
        Assert.NotEqual(0, retiredPid);
        try
        {
            using var process = Process.GetProcessById(retiredPid);
            Assert.True(process.HasExited);
        }
        catch (ArgumentException) { }
        var marker = Path.Combine(root, "KEEP.fixture");
        Assert.Equal("Retain failed child output until its owner retires.", File.ReadAllText(marker));
        File.Delete(marker);
        Directory.Delete(root);
    }
}
