using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Martlet.Core.Settings;
using Martlet.Updates;
using static Martlet.Updates.Tests.SignedPackageFixture;

namespace Martlet.Updates.Tests;

public sealed class StagingTests(SigningKeys keys) : IClassFixture<SigningKeys>
{
    private static AppSettings RecoverySettings()
    {
        var settings = AppSettings.CreateUnconfigured();
        return settings with { Profile = settings.Profile with { Id = Guid.Parse("cf87b4bb-89a4-4b08-8c47-c2d5a765a096") } };
    }

    private static StagingException Fails(StagingFailure failure, Action action)
    {
        var error = Assert.Throws<StagingException>(action);
        Assert.Equal(failure, error.Failure);
        return error;
    }

    [Fact]
    public void TrustedStagePreservesExactPayloadAndPersistsReverifiableReceipt()
    {
        using var f = new SignedPackageFixture(keys);
        var engine = f.Engine();
        var plan = f.Preview(engine);
        Assert.Empty(Directory.EnumerateFileSystemEntries(f.StagingRoot));
        Assert.Equal(AppSettings.CurrentSchemaVersion, plan.Installed.SettingsSchemaVersion);
        Assert.Equal(f.Files.Sum(p => p.Value.LongLength), plan.ExpandedBytes);
        Assert.Equal(new FileInfo(f.Archive).Length, plan.ArchiveBytes);
        Assert.True(plan.RequiredFreeBytes > plan.ArchiveBytes + plan.ExpandedBytes);
        Assert.Equal(Wire.Hash(keys.Approved.ExportSubjectPublicKeyInfo()), plan.SignerId);
        Assert.Equal("0.2.0.0", plan.Version);
        Assert.Throws<NotSupportedException>(() => ((IList<VerifiedFile>)plan.Files).Clear());
        var receipt = engine.Stage(plan, Approve(plan));
        foreach (var file in f.Files)
            Assert.Equal(file.Value, File.ReadAllBytes(Path.Combine(receipt.Destination, "payload",
                file.Key.Replace('/', Path.DirectorySeparatorChar))));
        Assert.Equal(File.ReadAllBytes(f.Archive), File.ReadAllBytes(Path.Combine(f.Destination, "candidate.zip")));
        Assert.Equal(File.ReadAllBytes(f.Envelope), File.ReadAllBytes(Path.Combine(f.Destination, "candidate.json")));
        var persisted = File.ReadAllBytes(Path.Combine(f.Destination, "staged.json"));
        Assert.Equal(Wire.Hash(persisted), receipt.ReceiptSha256);
        Assert.Contains(StagedReceipt.NextSteps, Encoding.UTF8.GetString(persisted));
        Assert.Equal(receipt.ReceiptSha256, f.Engine().InspectStaged(f.Destination).ReceiptSha256);
        Assert.Null(engine.PendingCleanupDirectory);
        f.AssertPrivateDataUnchanged();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RealDeflateAndSignedDataDescriptorsStageIncludingEmptyFiles(bool descriptors)
    {
        using var f = new SignedPackageFixture(keys);
        f.Files["help/empty"] = [];
        f.Files["Desktop/random"] = RandomNumberGenerator.GetBytes(100000);
        f.Build();
        f.WriteZip(f.Files.Select(p => new Entry(p.Key, p.Value, Compression: CompressionLevel.SmallestSize)), descriptors);
        f.ResignArchive();
        var engine = f.Engine();
        var plan = f.Preview(engine);
        engine.Stage(plan, Approve(plan));
        Assert.Equal(f.Files["Desktop/random"], File.ReadAllBytes(Path.Combine(f.Destination, "payload", "Desktop", "random")));
        Assert.Empty(File.ReadAllBytes(Path.Combine(f.Destination, "payload", "help", "empty")));
        f.Engine().InspectStaged(f.Destination);
    }

    [Theory]
    [InlineData("none", StagingFailure.TrustUnconfigured)]
    [InlineData("wrong-signer", StagingFailure.UntrustedSignature)]
    [InlineData("unknown-signer", StagingFailure.UntrustedSignature)]
    [InlineData("unsigned", StagingFailure.UntrustedSignature)]
    [InlineData("wrong-algorithm", StagingFailure.UntrustedSignature)]
    [InlineData("wrong-padding", StagingFailure.UntrustedSignature)]
    [InlineData("self-declared-key", StagingFailure.InvalidManifest)]
    [InlineData("mutated-signed-bytes", StagingFailure.UntrustedSignature)]
    [InlineData("noncanonical", StagingFailure.InvalidManifest)]
    [InlineData("duplicate-property", StagingFailure.InvalidManifest)]
    [InlineData("missing-property", StagingFailure.InvalidManifest)]
    [InlineData("null-files", StagingFailure.InvalidManifest)]
    [InlineData("null-file", StagingFailure.InvalidManifest)]
    [InlineData("future", StagingFailure.IncompatibleFormat)]
    [InlineData("reader", StagingFailure.IncompatibleFormat)]
    [InlineData("too-large", StagingFailure.CapacityExceeded)]
    public void ExplicitTrustAndStrictEnvelopeRequired(string scenario, StagingFailure failure)
    {
        using var f = new SignedPackageFixture(keys);
        var engine = f.Engine();
        switch (scenario)
        {
            case "none":
                engine = new(f.StagingRoot, new UpdateTrustPolicy([]), () => f.Current);
                File.Delete(f.Archive);
                break;
            case "wrong-signer": f.Sign(f.Manifest, keys.Stranger); break;
            case "unknown-signer":
                f.Sign(f.Manifest with { SignerId = Wire.Hash(keys.Stranger.ExportSubjectPublicKeyInfo()) }, keys.Stranger);
                break;
            case "unsigned":
                File.WriteAllBytes(f.Envelope, Wire.Write(new CandidateEnvelope { Manifest = Wire.Write(f.Manifest), Signature = [] }));
                break;
            case "wrong-algorithm": f.Sign(f.Manifest with { Algorithm = "SHA256" }); break;
            case "wrong-padding":
                var bytes = Wire.Write(f.Manifest);
                File.WriteAllBytes(f.Envelope, Wire.Write(new CandidateEnvelope
                {
                    Manifest = bytes, Signature = keys.Approved.SignData(bytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
                }));
                break;
            case "self-declared-key":
                var self = JsonNode.Parse(File.ReadAllBytes(f.Envelope))!.AsObject();
                self["approvedPublicKey"] = Convert.ToBase64String(keys.Stranger.ExportSubjectPublicKeyInfo());
                File.WriteAllText(f.Envelope, self.ToJsonString());
                break;
            case "mutated-signed-bytes":
                var original = Wire.Read<CandidateEnvelope>(File.ReadAllBytes(f.Envelope), Wire.MaximumEnvelopeBytes);
                File.WriteAllBytes(f.Envelope, Wire.Write(original with
                    { Manifest = Wire.Write(f.Manifest with { ApplicationVersion = "0.3.0.0" }) }));
                break;
            case "noncanonical": f.SignBytes(Encoding.UTF8.GetBytes(" " + Encoding.UTF8.GetString(Wire.Write(f.Manifest)))); break;
            case "duplicate-property":
                f.SignBytes(Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(Wire.Write(f.Manifest))
                    .Replace("\"formatVersion\":1", "\"formatVersion\":1,\"formatVersion\":1")));
                break;
            case "missing-property":
            case "null-files":
            case "null-file":
                var node = JsonNode.Parse(Wire.Write(f.Manifest))!.AsObject();
                if (scenario == "missing-property") node.Remove("files");
                else if (scenario == "null-files") node["files"] = null;
                else node["files"]![0] = null;
                f.SignBytes(Encoding.UTF8.GetBytes(node.ToJsonString()));
                break;
            case "future": f.Sign(f.Manifest with { FormatVersion = 2 }); break;
            case "reader": f.Sign(f.Manifest with { MinimumReaderFormat = 2 }); break;
            case "too-large": File.WriteAllBytes(f.Envelope, new byte[Wire.MaximumEnvelopeBytes + 1]); break;
        }
        var error = Fails(failure, () => f.Preview(engine));
        Assert.DoesNotContain(f.Root, error.ToString());
        Assert.DoesNotContain("PRIVATE-SENTINEL", error.ToString());
        Assert.Empty(Directory.EnumerateFileSystemEntries(f.StagingRoot));
        f.AssertPrivateDataUnchanged();
    }

    [Theory]
    [InlineData("0.1.0.0")]
    [InlineData("0.0.9.0")]
    [InlineData("0.2.0")]
    [InlineData("00.2.0.0")]
    [InlineData("0.2.0.0-beta")]
    public void WrongVersionCannotStage(string version)
    {
        using var f = new SignedPackageFixture(keys);
        f.Sign(f.Manifest with { ApplicationVersion = version });
        Fails(StagingFailure.InvalidVersion, () => f.Preview(f.Engine()));
    }

    [Theory]
    [InlineData(1, 1, 1, true)]
    [InlineData(2, 2, 2, true)]
    [InlineData(2, 1, 2, true)]
    [InlineData(2, 1, 1, false)]
    [InlineData(1, 2, 2, false)]
    [InlineData(2, 3, 4, false)]
    [InlineData(2, 2, 1, false)]
    [InlineData(2, 0, 2, false)]
    [InlineData(3, 1, 3, false)]
    public void CompatibilityUsesKnownPersistedSchemaWithoutReadingSettings(int schema, int minimum, int maximum, bool allowed)
    {
        using var f = new SignedPackageFixture(keys);
        f.Current = f.Current with { SettingsSchemaVersion = schema };
        f.Sign(f.Manifest with { SettingsMinimumReader = minimum, SettingsMaximumReader = maximum });
        var engine = f.Engine();
        if (allowed)
        {
            var plan = f.Preview(engine);
            engine.Stage(plan, Approve(plan));
        }
        else Fails(StagingFailure.IncompatibleSettings, () => f.Preview(engine));
        f.AssertPrivateDataUnchanged();
    }

    [Fact]
    public void RidMustMatchOwnerFacts()
    {
        using var f = new SignedPackageFixture(keys);
        f.Sign(f.Manifest with { Rid = "win-arm64" });
        Fails(StagingFailure.IncompatibleRid, () => f.Preview(f.Engine()));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("/absolute")]
    [InlineData("//host/share")]
    [InlineData("C:/absolute")]
    [InlineData("Desktop\\bad.dll")]
    [InlineData("Desktop/file:ads")]
    [InlineData("Desktop/CON.txt")]
    [InlineData("Desktop/COM1.dll")]
    [InlineData("Desktop/LPT0")]
    [InlineData("Desktop/file.")]
    [InlineData("Desktop/file ")]
    [InlineData("Desktop//file")]
    [InlineData("Desktop/./file")]
    [InlineData("Desktop/../file")]
    [InlineData("Desktop/caf\u00e9")]
    [InlineData("Desktop/")]
    [InlineData("models/weights.bin")]
    [InlineData("Desktop/settings.json")]
    [InlineData("Desktop/profiles/data")]
    public void SignedUnsafeNamesRejectedBeforeExtraction(string path)
    {
        using var f = new SignedPackageFixture(keys);
        f.Files[path] = "bad"u8.ToArray();
        f.WriteZip(f.Files.Select(p => new Entry(p.Key, p.Value)));
        f.ResignArchive(updateInventory: true);
        Fails(StagingFailure.UnsafeEntry, () => f.Preview(f.Engine()));
        Assert.False(Directory.Exists(f.Destination));
        f.AssertPrivateDataUnchanged();
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("case")]
    [InlineData("case-directory")]
    [InlineData("file-directory")]
    [InlineData("symlink")]
    [InlineData("fifo")]
    [InlineData("reparse")]
    [InlineData("directory-metadata")]
    public void AmbiguousOrLinkedEntriesRejected(string scenario)
    {
        using var f = new SignedPackageFixture(keys);
        var entries = f.Files.Select(p => new Entry(p.Key, p.Value)).ToList();
        switch (scenario)
        {
            case "duplicate": entries.Add(entries[0]); break;
            case "case": entries.Add(entries[0] with { Name = "Desktop/martlet.desktop.exe" }); break;
            case "case-directory":
                f.Files["Desktop/Sub/a"] = [1]; f.Files["Desktop/sub/b"] = [2];
                entries = f.Files.Select(p => new Entry(p.Key, p.Value)).ToList();
                break;
            case "file-directory":
                f.Files["Desktop/sub"] = [1]; f.Files["Desktop/sub/b"] = [2];
                entries = f.Files.Select(p => new Entry(p.Key, p.Value)).ToList();
                break;
            case "symlink": entries[0] = entries[0] with { Attributes = unchecked((int)0xa1ff0000) }; break;
            case "fifo": entries[0] = entries[0] with { Attributes = 0x11ff0000 }; break;
            case "reparse": entries[0] = entries[0] with { Attributes = 0x400 }; break;
            case "directory-metadata": entries[0] = entries[0] with { Attributes = 0x10 }; break;
        }
        f.WriteZip(entries);
        // Match signed entry count for duplicates so the real ZIP name checks, not a count shortcut, reject it.
        var inventory = entries.Select(e => new PayloadFile
        {
            Path = e.Name, Bytes = e.Bytes.Length, Sha256 = Wire.Hash(e.Bytes)
        }).OrderBy(e => e.Path, StringComparer.Ordinal).ToArray();
        if (scenario is "duplicate" or "case")
        {
            inventory = f.Manifest.Files.Append(new PayloadFile { Path = "help/unused", Bytes = 0, Sha256 = Wire.Hash([]) })
                .OrderBy(e => e.Path, StringComparer.Ordinal).ToArray();
        }
        f.Sign(f.Manifest with
        {
            ArchiveBytes = new FileInfo(f.Archive).Length, ArchiveSha256 = Wire.Hash(File.ReadAllBytes(f.Archive)), Files = inventory
        });
        var error = Assert.Throws<StagingException>(() => f.Preview(f.Engine()));
        Assert.Equal(StagingFailure.UnsafeEntry, error.Failure);
    }

    [Theory]
    [InlineData("archive-tamper", StagingFailure.CorruptArchive)]
    [InlineData("truncated", StagingFailure.CorruptArchive)]
    [InlineData("signed-truncated", StagingFailure.CorruptArchive)]
    [InlineData("file-hash", StagingFailure.CorruptArchive)]
    [InlineData("file-size", StagingFailure.CorruptArchive)]
    [InlineData("crc", StagingFailure.CorruptArchive)]
    [InlineData("method", StagingFailure.UnsafeEntry)]
    [InlineData("encrypted", StagingFailure.UnsafeEntry)]
    [InlineData("extra-hardlink", StagingFailure.UnsafeEntry)]
    [InlineData("local-name", StagingFailure.UnsafeEntry)]
    [InlineData("local-size", StagingFailure.CorruptArchive)]
    [InlineData("overlap", StagingFailure.UnsafeEntry)]
    [InlineData("comment", StagingFailure.UnsafeEntry)]
    [InlineData("zip64", StagingFailure.UnsafeEntry)]
    public void SignedArchiveStructureAndContentsAreChecked(string scenario, StagingFailure failure)
    {
        using var f = new SignedPackageFixture(keys);
        var bytes = File.ReadAllBytes(f.Archive);
        var central = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(bytes.Length - 6));
        switch (scenario)
        {
            case "archive-tamper": bytes[45] ^= 1; break;
            case "truncated":
            case "signed-truncated": bytes = bytes[..^7]; break;
            case "file-hash": f.Sign(f.Manifest with { Files = f.Manifest.Files.Select((p, i) => i == 0 ? p with { Sha256 = new('0', 64) } : p).ToArray() }); break;
            case "file-size": f.Sign(f.Manifest with { Files = f.Manifest.Files.Select((p, i) => i == 0 ? p with { Bytes = p.Bytes + 1 } : p).ToArray() }); break;
            case "crc": bytes[14] ^= 1; bytes[central + 16] ^= 1; break;
            case "method": bytes[central + 10] = 99; break;
            case "encrypted": bytes[central + 8] |= 1; break;
            case "extra-hardlink": bytes[central + 30] = 12; break;
            case "local-name": bytes[30] ^= 1; break;
            case "local-size": bytes[22] ^= 1; break;
            case "overlap": bytes[central + 42] = 1; break;
            case "comment": bytes[^2] = 1; break;
            case "zip64": bytes[central + 6] = 45; break;
        }
        File.WriteAllBytes(f.Archive, bytes);
        if (scenario is not ("archive-tamper" or "truncated" or "file-hash" or "file-size")) f.ResignArchive();
        Fails(failure, () => f.Preview(f.Engine()));
        Assert.Empty(Directory.EnumerateFileSystemEntries(f.StagingRoot));
    }

    [Theory]
    [InlineData("bomb")]
    [InlineData("archive")]
    [InlineData("expanded")]
    [InlineData("file")]
    [InlineData("count")]
    [InlineData("directory-count")]
    [InlineData("path")]
    public void ResourceLimitsAreAppliedBeforeExtraction(string scenario)
    {
        using var f = new SignedPackageFixture(keys);
        var limits = new StagingLimits();
        switch (scenario)
        {
            case "bomb":
                f.Files["Desktop/bomb"] = new byte[1000000];
                f.Build();
                f.WriteZip(f.Files.Select(p => new Entry(p.Key, p.Value, Compression: CompressionLevel.SmallestSize)));
                f.ResignArchive();
                break;
            case "archive": limits = limits with { MaximumArchiveBytes = 22 }; break;
            case "expanded": limits = limits with { MaximumExpandedBytes = 10 }; break;
            case "file": limits = limits with { MaximumFileBytes = 10 }; break;
            case "count": limits = limits with { MaximumEntries = 3 }; break;
            case "directory-count": limits = limits with { MaximumDirectories = 1 }; break;
            case "path":
                f.Files["help/" + new string('x', 241)] = [1];
                f.WriteZip(f.Files.Select(p => new Entry(p.Key, p.Value)));
                f.ResignArchive(updateInventory: true);
                break;
        }
        Fails(StagingFailure.CapacityExceeded, () => f.Preview(f.Engine(limits: limits)));
        Assert.Empty(Directory.EnumerateFileSystemEntries(f.StagingRoot));
    }

    [Theory]
    [InlineData("version")]
    [InlineData("channel")]
    [InlineData("inventory")]
    [InlineData("sums")]
    [InlineData("schema")]
    public void InternalUnsignedPayloadContractIsPreservedAndCrossChecked(string scenario)
    {
        using var f = new SignedPackageFixture(keys);
        var payload = Wire.Read<InternalPayloadManifest>(f.Files["manifest.json"], Wire.MaximumManifestBytes);
        switch (scenario)
        {
            case "version": payload = payload with { ApplicationVersion = "7.0.0.0" }; break;
            case "channel": payload = payload with { Channel = "TRUSTED RELEASE" }; break;
            case "inventory": payload = payload with { Files = [] }; break;
            case "schema": payload = payload with { SchemaVersion = 2 }; break;
            case "sums": f.Files["SHA256SUMS.txt"] = "wrong checksums"u8.ToArray(); break;
        }
        f.Files["manifest.json"] = Wire.Write(payload);
        f.WriteZip(f.Files.Select(p => new Entry(p.Key, p.Value)));
        f.ResignArchive(updateInventory: true);
        Fails(scenario == "schema" ? StagingFailure.IncompatibleFormat : StagingFailure.InvalidManifest,
            () => f.Preview(f.Engine()));
    }

    [Theory]
    [InlineData("archive")]
    [InlineData("envelope")]
    [InlineData("version")]
    [InlineData("revision")]
    [InlineData("settings")]
    [InlineData("installation")]
    [InlineData("destination")]
    [InlineData("superseded")]
    [InlineData("failed-preview")]
    [InlineData("different-engine")]
    public void FrozenPreviewRejectsChangedFactsOrSource(string scenario)
    {
        using var f = new SignedPackageFixture(keys);
        var engine = f.Engine();
        var plan = f.Preview(engine);
        var approval = Approve(plan);
        switch (scenario)
        {
            case "archive": File.AppendAllText(f.Archive, "changed"); break;
            case "envelope": File.AppendAllText(f.Envelope, " "); break;
            case "version": f.Current = f.Current with { Version = "0.1.1.0" }; break;
            case "revision": f.Current = f.Current with { InstallationRevision = new('1', 64) }; break;
            case "settings": f.Current = f.Current with { SettingsRevision = new('2', 64) }; break;
            case "installation": f.Current = f.Current with { InstallationDirectory = Path.Combine(f.Root, "other") }; break;
            case "destination":
                Directory.CreateDirectory(f.Destination);
                File.WriteAllText(Path.Combine(f.Destination, "KEEP"), "existing");
                break;
            case "superseded": f.Preview(engine); break;
            case "failed-preview": Assert.Throws<StagingException>(() => engine.Preview(f.Archive, f.Envelope, f.StagingRoot)); break;
            case "different-engine": engine = f.Engine(); break;
        }
        Fails(StagingFailure.Conflict, () => engine.Stage(plan, approval));
        if (scenario == "destination") Assert.Equal("existing", File.ReadAllText(Path.Combine(f.Destination, "KEEP")));
        else Assert.False(Directory.Exists(f.Destination));
        f.AssertPrivateDataUnchanged();
    }

    [Fact]
    public void ApprovalIsExactAndOneUseEvenAcrossFailureAndWrongPlan()
    {
        using var f = new SignedPackageFixture(keys);
        var engine = f.Engine();
        var first = f.Preview(engine);
        Fails(StagingFailure.Conflict, () => first.Approve(new('0', 64), first.EnvelopeSha256, first.Destination, first.Installed));
        Fails(StagingFailure.Conflict, () => first.Approve(first.ArchiveSha256, new('0', 64), first.Destination, first.Installed));
        Fails(StagingFailure.Conflict, () => first.Approve(first.ArchiveSha256, first.EnvelopeSha256, f.Root, first.Installed));
        Fails(StagingFailure.Conflict, () => first.Approve(first.ArchiveSha256, first.EnvelopeSha256, first.Destination,
            first.Installed with { SettingsRevision = new('0', 64) }));
        var approval = Approve(first);
        Fails(StagingFailure.Conflict, () => Approve(first));
        var second = f.Preview(engine);
        Fails(StagingFailure.Conflict, () => engine.Stage(second, approval));
        Fails(StagingFailure.Conflict, () => engine.Stage(first, approval));
        var good = Approve(second);
        engine.Stage(second, good);
        Fails(StagingFailure.Conflict, () => engine.Stage(second, good));
        Fails(StagingFailure.Conflict, () => f.Preview(engine));
    }

    [Theory]
    [InlineData((int)StagingIoPoint.BeforeCreateDirectory)]
    [InlineData((int)StagingIoPoint.BeforeCreateFile)]
    [InlineData((int)StagingIoPoint.BeforeWrite)]
    [InlineData((int)StagingIoPoint.BeforeFlush)]
    [InlineData((int)StagingIoPoint.BeforeFinalize)]
    [InlineData((int)StagingIoPoint.BeforeRename)]
    public void ControlledIoFailureNeverProducesSuccessAndCleansOnlyOwnedPaths(int point)
    {
        using var f = new SignedPackageFixture(keys);
        var sibling = Path.Combine(f.StagingRoot, "prior-stage");
        Directory.CreateDirectory(sibling);
        File.WriteAllText(Path.Combine(sibling, "KEEP"), "prior");
        var fired = false;
        var engine = f.Engine((at, _) =>
        {
            if ((int)at == point && !fired) { fired = true; throw new IOException("PRIVATE-IO-DETAIL"); }
        });
        var plan = f.Preview(engine);
        var error = Fails(StagingFailure.Unavailable, () => engine.Stage(plan, Approve(plan)));
        Assert.True(fired);
        Assert.DoesNotContain("PRIVATE-IO-DETAIL", error.ToString());
        Assert.False(Directory.Exists(f.Destination));
        Assert.Empty(Directory.GetDirectories(f.StagingRoot, ".pending-*"));
        Assert.Null(engine.PendingCleanupDirectory);
        Assert.Equal("prior", File.ReadAllText(Path.Combine(sibling, "KEEP")));
        f.AssertPrivateDataUnchanged();
    }

    [Fact]
    public void FailureAfterPartialPayloadWriteRetainsCleanupOwnershipAndCanRetry()
    {
        using var f = new SignedPackageFixture(keys);
        f.Files["Desktop/large"] = RandomNumberGenerator.GetBytes(200000);
        f.Build();
        var writes = 0;
        var failCleanup = true;
        var engine = f.Engine((at, _) =>
        {
            if (at == StagingIoPoint.BeforeWrite && ++writes == 10) throw new IOException("write-failed");
            if (at == StagingIoPoint.BeforeCleanup && failCleanup) throw new UnauthorizedAccessException("cleanup-failed");
        });
        var plan = f.Preview(engine);
        var error = Fails(StagingFailure.CleanupPending, () => engine.Stage(plan, Approve(plan)));
        Assert.Equal(StagingFailure.Unavailable, error.OriginalFailure);
        Assert.Equal(engine.PendingCleanupDirectory, error.RetainedDirectory);
        Assert.True(Directory.Exists(error.RetainedDirectory));
        Assert.DoesNotContain(f.Root, error.ToString());
        Assert.False(Directory.Exists(f.Destination));
        Fails(StagingFailure.Busy, () => f.Preview(engine));
        Fails(StagingFailure.CleanupPending, engine.RetryCleanup);
        failCleanup = false;
        engine.RetryCleanup();
        Assert.False(Directory.Exists(error.RetainedDirectory));
        Assert.Null(engine.PendingCleanupDirectory);
        f.AssertPrivateDataUnchanged();
    }

    [Theory]
    [InlineData((int)StagingIoPoint.BeforeCreateDirectory)]
    [InlineData((int)StagingIoPoint.BeforeCreateFile)]
    [InlineData((int)StagingIoPoint.BeforeWrite)]
    [InlineData((int)StagingIoPoint.BeforeFlush)]
    [InlineData((int)StagingIoPoint.BeforeFinalize)]
    [InlineData((int)StagingIoPoint.BeforeRename)]
    public void OriginalCancellationCheckedAtEachWriteBoundary(int point)
    {
        using var f = new SignedPackageFixture(keys);
        using var cancel = new CancellationTokenSource();
        var engine = f.Engine((at, _) => { if ((int)at == point) cancel.Cancel(); });
        var plan = f.Preview(engine);
        Fails(StagingFailure.Cancelled, () => engine.Stage(plan, Approve(plan), cancel.Token));
        Assert.False(Directory.Exists(f.Destination));
        Assert.Null(engine.PendingCleanupDirectory);
        f.AssertPrivateDataUnchanged();
    }

    [Fact]
    public async Task BlockedCancellationCallbackCannotPermitFinalization()
    {
        using var f = new SignedPackageFixture(keys);
        using var cancel = new CancellationTokenSource();
        using var atCommit = new ManualResetEventSlim();
        using var resumeCommit = new ManualResetEventSlim();
        using var callbackEntered = new ManualResetEventSlim();
        using var releaseCallback = new ManualResetEventSlim();
        using var registration = cancel.Token.Register(() =>
        {
            callbackEntered.Set();
            if (!releaseCallback.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException();
        });
        var engine = f.Engine((at, _) =>
        {
            if (at == StagingIoPoint.BeforeFinalize)
            {
                atCommit.Set();
                if (!resumeCommit.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException();
            }
        });
        var plan = f.Preview(engine);
        var approval = Approve(plan);
        var staging = Task.Run(() => Record.Exception(() => engine.Stage(plan, approval, cancel.Token)));
        Task? cancellation = null;
        try
        {
            Assert.True(atCommit.Wait(TimeSpan.FromSeconds(15)));
            Fails(StagingFailure.Busy, () => f.Preview(engine));
            cancellation = Task.Run(cancel.Cancel);
            Assert.True(callbackEntered.Wait(TimeSpan.FromSeconds(15)));
            resumeCommit.Set();
            var error = Assert.IsType<StagingException>(await staging.WaitAsync(TimeSpan.FromSeconds(15)));
            Assert.Equal(StagingFailure.Cancelled, error.Failure);
            Assert.False(Directory.Exists(f.Destination));
            Assert.False(cancellation.IsCompleted);
            f.AssertPrivateDataUnchanged();
        }
        finally
        {
            resumeCommit.Set();
            releaseCallback.Set();
            if (cancellation is not null) await cancellation;
            await staging;
        }
    }

    [Theory]
    [InlineData("space-preview")]
    [InlineData("space-stage")]
    [InlineData("disk-full")]
    [InlineData("access")]
    [InlineData("destination-race")]
    [InlineData("facts-at-commit")]
    [InlineData("corrupt-copy")]
    public void LateFailuresRemainTruthfulAndPreserveInstallation(string scenario)
    {
        using var f = new SignedPackageFixture(keys);
        var available = scenario == "space-preview" ? 0L : long.MaxValue;
        LocalStagingEngine? engine = null;
        engine = f.Engine((at, _) =>
        {
            if (at == StagingIoPoint.BeforeFlush && scenario == "disk-full") throw new IOException("disk", unchecked((int)0x80070070));
            if (at == StagingIoPoint.BeforeWrite && scenario == "access") throw new UnauthorizedAccessException();
            if (at == StagingIoPoint.BeforeFinalize && scenario == "facts-at-commit")
                f.Current = f.Current with { SettingsRevision = new('3', 64) };
            if (at == StagingIoPoint.BeforeFinalize && scenario == "destination-race")
            {
                Directory.CreateDirectory(f.Destination);
                File.WriteAllText(Path.Combine(f.Destination, "KEEP"), "racer");
            }
            if (at == StagingIoPoint.AfterCandidateCopy && scenario == "corrupt-copy")
                File.AppendAllText(Path.Combine(engine!.PendingCleanupDirectory!, "candidate.zip"), "tamper");
        }, availableBytes: () => available);
        if (scenario == "space-preview")
        {
            Fails(StagingFailure.InsufficientDisk, () => f.Preview(engine));
            return;
        }
        var plan = f.Preview(engine);
        if (scenario == "space-stage") available = 0;
        var expected = scenario switch
        {
            "space-stage" or "disk-full" => StagingFailure.InsufficientDisk,
            "access" => StagingFailure.AccessDenied,
            "corrupt-copy" => StagingFailure.CorruptArchive,
            _ => StagingFailure.Conflict
        };
        Fails(expected, () => engine.Stage(plan, Approve(plan)));
        if (scenario == "destination-race") Assert.Equal("racer", File.ReadAllText(Path.Combine(f.Destination, "KEEP")));
        else Assert.False(Directory.Exists(f.Destination));
        Assert.Empty(Directory.GetDirectories(f.StagingRoot, ".pending-*"));
        f.AssertPrivateDataUnchanged();
    }

    [Theory]
    [InlineData("receipt")]
    [InlineData("verified-flag")]
    [InlineData("payload")]
    [InlineData("extra")]
    [InlineData("empty-directory")]
    [InlineData("missing")]
    [InlineData("archive")]
    [InlineData("manifest")]
    public void PersistedReceiptNeverBypassesRealVerification(string scenario)
    {
        using var f = new SignedPackageFixture(keys);
        var engine = f.Engine();
        var plan = f.Preview(engine);
        engine.Stage(plan, Approve(plan));
        var receipt = Path.Combine(f.Destination, "staged.json");
        switch (scenario)
        {
            case "receipt": File.AppendAllText(receipt, "!"); break;
            case "verified-flag": File.WriteAllText(receipt, "{\"verified\":true}"); break;
            case "payload": File.AppendAllText(Path.Combine(f.Destination, "payload", "Desktop", "Martlet.Desktop.exe"), "!"); break;
            case "extra": File.WriteAllText(Path.Combine(f.Destination, "extra"), "unknown"); break;
            case "empty-directory": Directory.CreateDirectory(Path.Combine(f.Destination, "payload", "empty")); break;
            case "missing": File.Delete(Path.Combine(f.Destination, "payload", "manifest.json")); break;
            case "archive": File.AppendAllText(Path.Combine(f.Destination, "candidate.zip"), "!"); break;
            case "manifest": File.AppendAllText(Path.Combine(f.Destination, "candidate.json"), "!"); break;
        }
        Fails(StagingFailure.InvalidReceipt, () => f.Engine().InspectStaged(f.Destination));
        Assert.True(Directory.Exists(f.Destination));
        f.AssertPrivateDataUnchanged();
    }

    [Fact]
    public void ReopenedReceiptRequiresCurrentTrustAndInstalledFacts()
    {
        using var f = new SignedPackageFixture(keys);
        var engine = f.Engine();
        var plan = f.Preview(engine);
        engine.Stage(plan, Approve(plan));
        var untrusted = new LocalStagingEngine(f.StagingRoot,
            new UpdateTrustPolicy([keys.Stranger.ExportSubjectPublicKeyInfo()]), () => f.Current);
        Fails(StagingFailure.UntrustedSignature, () => untrusted.InspectStaged(f.Destination));
        f.Current = f.Current with { SettingsRevision = new('9', 64) };
        Fails(StagingFailure.InvalidReceipt, () => f.Engine().InspectStaged(f.Destination));
    }

    [Fact]
    public void SourceHandlesStayOwnedUntilOperationStopsAndRootIsExclusive()
    {
        if (!OperatingSystem.IsWindows()) return; // Windows share-denial behavior, not portable qualification.
        using var f = new SignedPackageFixture(keys);
        var observed = false;
        var second = f.Engine();
        var secondPlan = f.Preview(second);
        var engine = f.Engine((at, _) =>
        {
            if (at != StagingIoPoint.BeforeFinalize) return;
            observed = true;
            Assert.Throws<IOException>(() => File.Delete(f.Archive));
            Assert.Throws<IOException>(() => File.AppendAllText(f.Envelope, "bad"));
            Fails(StagingFailure.Busy, () => second.Stage(secondPlan, Approve(secondPlan)));
        });
        var plan = f.Preview(engine);
        engine.Stage(plan, Approve(plan));
        Assert.True(observed);
        f.AssertPrivateDataUnchanged();
    }

    [Fact]
    public void DestinationCannotBeInstalledRootOrOutsideSelectedScope()
    {
        using var f = new SignedPackageFixture(keys);
        var engine = f.Engine();
        Fails(StagingFailure.Conflict, () => engine.Preview(f.Archive, f.Envelope, f.InstalledRoot));
        Fails(StagingFailure.Conflict, () => engine.Preview(f.Archive, f.Envelope, Path.Combine(f.Root, "outside")));
        f.Current = f.Current with { InstallationDirectory = f.StagingRoot };
        Fails(StagingFailure.Conflict, () => f.Preview(engine));
        f.AssertPrivateDataUnchanged();
    }

    [Fact]
    public void PolicyCopiesKeysAndRejectsWeakOrAmbiguousKeyMaterial()
    {
        var bytes = keys.Approved.ExportSubjectPublicKeyInfo();
        var policy = new UpdateTrustPolicy([bytes]);
        bytes[0] ^= 1;
        using var f = new SignedPackageFixture(keys);
        f.Preview(new(f.StagingRoot, policy, () => f.Current));
        using var weak = RSA.Create(2048);
        Assert.Throws<ArgumentException>(() => new UpdateTrustPolicy([weak.ExportSubjectPublicKeyInfo()]));
        Assert.Throws<ArgumentException>(() => new UpdateTrustPolicy([keys.Approved.ExportSubjectPublicKeyInfo().Concat(new byte[] { 0 }).ToArray()]));
        Assert.Throws<ArgumentException>(() => new UpdateTrustPolicy([keys.Approved.ExportSubjectPublicKeyInfo(), keys.Approved.ExportSubjectPublicKeyInfo()]));
    }

    [Fact]
    public void CleanupRefusesUnknownFilesRatherThanRecursivelyDeletingThem()
    {
        using var f = new SignedPackageFixture(keys);
        string? unknown = null;
        LocalStagingEngine? engine = null;
        engine = f.Engine((at, _) =>
        {
            if (at == StagingIoPoint.BeforeRename)
            {
                unknown = Path.Combine(engine!.PendingCleanupDirectory!, "NOT-OWNED");
                File.WriteAllText(unknown, "KEEP");
                throw new IOException("rename failed");
            }
        });
        var plan = f.Preview(engine);
        var error = Fails(StagingFailure.CleanupPending, () => engine.Stage(plan, Approve(plan)));
        Assert.Equal("KEEP", File.ReadAllText(unknown!));
        Fails(StagingFailure.CleanupPending, engine.RetryCleanup);
        Assert.Equal("KEEP", File.ReadAllText(unknown!));
        File.Delete(unknown!); // Only the test which created this file is allowed to remove it.
        engine.RetryCleanup();
        Assert.False(Directory.Exists(error.RetainedDirectory));
    }

    [Fact]
    public void CancelledOperationRetainsCleanupAndConsumesApproval()
    {
        using var f = new SignedPackageFixture(keys);
        using var cancellation = new CancellationTokenSource();
        var denyCleanup = true;
        var engine = f.Engine((at, _) =>
        {
            if (at == StagingIoPoint.BeforeRename) cancellation.Cancel();
            if (at == StagingIoPoint.BeforeCleanup && denyCleanup) throw new IOException();
        });
        var plan = f.Preview(engine);
        var approval = Approve(plan);
        var error = Fails(StagingFailure.CleanupPending, () => engine.Stage(plan, approval, cancellation.Token));
        Assert.Equal(StagingFailure.Cancelled, error.OriginalFailure);
        Assert.False(Directory.Exists(f.Destination));
        denyCleanup = false;
        engine.RetryCleanup();
        Fails(StagingFailure.Conflict, () => engine.Stage(plan, approval));
        f.AssertPrivateDataUnchanged();
    }

    [Fact]
    public void ShortReadsAndTruncatedRealFileStreamsCannotBecomeSuccessfulCopies()
    {
        using var f = new SignedPackageFixture(keys);
        using var file = new FileStream(f.Archive, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var shortReads = new ShortReader(file, file.Length);
        BoundedIo.CopyAndHash(shortReads, null, f.Manifest.ArchiveBytes, f.Manifest.ArchiveSha256, CancellationToken.None);
        file.Position = 0;
        using var truncated = new ShortReader(file, file.Length - 1);
        Fails(StagingFailure.CorruptArchive, () => BoundedIo.CopyAndHash(truncated, null,
            f.Manifest.ArchiveBytes, f.Manifest.ArchiveSha256, CancellationToken.None));
    }

    [Fact]
    public async Task ActualSettingsStoreRevisionIsPreservedThroughApprovalAndReceiptWithoutReadingItsFile()
    {
        using var f = new SignedPackageFixture(keys);
        var store = new SettingsStore(Path.Combine(f.Root, "settings-owner"));
        Assert.True((await store.SaveAsync(RecoverySettings(), null)).Saved);
        var loaded = await store.LoadAsync();
        Assert.Equal(SettingsLoadState.Loaded, loaded.State);
        var sourceBytes = await File.ReadAllBytesAsync(store.FilePath);
        var revision = loaded.Revision!;
        Assert.Equal(Convert.ToHexString(SHA256.HashData(sourceBytes)), revision);
        f.Current = f.Current with { SettingsSchemaVersion = loaded.Settings!.SchemaVersion, SettingsRevision = revision };
        using var blockedSettings = new FileStream(store.FilePath, FileMode.Open, FileAccess.Read, FileShare.None);
        var engine = f.Engine();
        var plan = f.Preview(engine);
        Assert.Equal(revision, plan.Installed.SettingsRevision);
        var approval = plan.Approve(plan.ArchiveSha256, plan.EnvelopeSha256, plan.Destination, f.Current);
        var receipt = engine.Stage(plan, approval);
        Assert.Equal(revision, receipt.Installed.SettingsRevision);
        var reopened = f.Engine().InspectStaged(f.Destination);
        Assert.Equal(revision, reopened.Installed.SettingsRevision);
        var document = Wire.Read<ReceiptDocument>(File.ReadAllBytes(Path.Combine(f.Destination, "staged.json")),
            Wire.MaximumReceiptBytes, canonical: true);
        Assert.Equal(revision, document.Installed.SettingsRevision);
        Assert.Equal(sourceBytes, BoundedIo.Read(blockedSettings, AppSettings.MaxFileBytes, CancellationToken.None));
        f.AssertPrivateDataUnchanged();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealStoreChangeOrCallerCaseChangeRejectsStaleApproval(bool caseChangeOnly)
    {
        using var f = new SignedPackageFixture(keys);
        var store = new SettingsStore(Path.Combine(f.Root, "settings-owner"));
        Assert.True((await store.SaveAsync(RecoverySettings(), null)).Saved);
        var loaded = await store.LoadAsync();
        f.Current = f.Current with
        {
            SettingsSchemaVersion = loaded.Settings!.SchemaVersion, SettingsRevision = loaded.Revision!
        };
        var engine = f.Engine();
        StagingPlan plan;
        using (var blockedSettings = new FileStream(store.FilePath, FileMode.Open, FileAccess.Read, FileShare.None))
            plan = f.Preview(engine);
        var approval = Approve(plan);
        string changedRevision;
        if (caseChangeOnly)
        {
            changedRevision = loaded.Revision!.ToLowerInvariant();
            Assert.NotEqual(loaded.Revision, changedRevision);
        }
        else
        {
            var changed = loaded.Settings with { Profile = loaded.Settings.Profile with { Kind = ProfileKind.Fixture } };
            var saved = await store.SaveAsync(changed, loaded.Revision);
            Assert.True(saved.Saved);
            changedRevision = saved.Revision!;
        }
        f.Current = f.Current with { SettingsRevision = changedRevision };
        var changedBytes = await File.ReadAllBytesAsync(store.FilePath);
        using var lockedChangedSettings = new FileStream(store.FilePath, FileMode.Open, FileAccess.Read, FileShare.None);
        Fails(StagingFailure.Conflict, () => engine.Stage(plan, approval));
        Assert.False(Directory.Exists(f.Destination));
        Assert.Equal(changedBytes, BoundedIo.Read(lockedChangedSettings, AppSettings.MaxFileBytes, CancellationToken.None));
        f.AssertPrivateDataUnchanged();
    }

    [Fact]
    public void OpaqueSettingsRevisionIsNotInterpretedAsCandidateHash()
    {
        using var f = new SignedPackageFixture(keys);
        const string token = "SettingsOwner/Revision:AbCd-01";
        f.Current = f.Current with { SettingsRevision = token };
        var engine = f.Engine();
        var plan = f.Preview(engine);
        var receipt = engine.Stage(plan, Approve(plan));
        Assert.Equal(token, receipt.Installed.SettingsRevision);
        Assert.Equal(token, f.Engine().InspectStaged(f.Destination).Installed.SettingsRevision);
        Assert.Equal(f.Manifest.ArchiveSha256, plan.ArchiveSha256);
    }

    private sealed class ShortReader(Stream input, long remaining) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = input.Read(buffer, offset, (int)Math.Min(Math.Min(count, 7), remaining));
            remaining -= read;
            return read;
        }
        public override void Flush() => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
