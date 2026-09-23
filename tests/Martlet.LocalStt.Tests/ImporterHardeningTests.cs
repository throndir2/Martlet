using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace Martlet.LocalStt.Tests;

public sealed class ImporterHardeningTests
{
    [Fact]
    public async Task Payload_path_handoff_preserves_strict_verifier_and_public_gate()
    {
        using var fixture = new ImportFixture();
        var importer = fixture.Importer();
        var plan = importer.Preview(fixture.Request());
        importer.Import(plan, Approve(plan));
        var inspection = importer.InspectInstalled(fixture.Destination);
        Assert.Equal(Path.Combine(fixture.Destination, "payload"), inspection.PayloadPath);
        Assert.False(inspection.CanLaunch);
        Assert.False(inspection.RightsQualified);
        Assert.False(inspection.RuntimeQualified);
        Assert.Equal(LocalSttCandidateStatus.DisabledPendingQualification, inspection.Status);
        var exact = new PhysicalLocalSttPackageVerifier(inspection.PayloadPath, fixture.Manifest,
            new PhysicalLocalPathInspector());
        var result = await exact.VerifyForLaunchAsync(CancellationToken.None);
        await using var package = Assert.IsType<VerifiedLocalSttPackage>(result.Package);
        Assert.Equal(Path.Combine(inspection.PayloadPath, "runtime", "whisper-cli.exe"), package.ExecutablePath);
        var wrongRoot = new PhysicalLocalSttPackageVerifier(fixture.Destination, fixture.Manifest,
            new PhysicalLocalPathInspector());
        Assert.NotEqual(PackageVerificationStatus.Verified,
            (await wrongRoot.VerifyForLaunchAsync(CancellationToken.None)).Status);
        // Public physical verification still accepts only the embedded production pins.
        Assert.NotEqual(PackageVerificationStatus.Verified,
            (await new PhysicalLocalSttPackageVerifier(inspection.PayloadPath)
                .VerifyForLaunchAsync(CancellationToken.None)).Status);
    }

    [Theory]
    [InlineData("NUL")]
    [InlineData("CON.txt")]
    [InlineData("COM1.bin")]
    [InlineData("trailing.")]
    [InlineData("trailing ")]
    [InlineData("file:stream")]
    public void Unsafe_destination_components_are_rejected_before_creation(string name)
    {
        using var fixture = new ImportFixture();
        var request = fixture.Request() with { DestinationPath = Path.Combine(fixture.Root, name) };
        AssertFailure(LocalSttProvisioningFailure.SourceUnsafe, () => fixture.Importer().Preview(request));
    }

    [Fact]
    public void Same_bytes_and_timestamp_replacement_still_invalidates_approval()
    {
        using var fixture = new ImportFixture();
        var importer = fixture.Importer();
        var plan = importer.Preview(fixture.Request());
        var timestamp = File.GetLastWriteTimeUtc(fixture.ModelPath);
        var replacement = fixture.ModelPath + ".replacement";
        File.Copy(fixture.ModelPath, replacement);
        File.SetLastWriteTimeUtc(replacement, timestamp);
        File.Move(replacement, fixture.ModelPath, overwrite: true);
        AssertFailure(LocalSttProvisioningFailure.SourceChanged, () => importer.Import(plan, Approve(plan)));
        Assert.False(Directory.Exists(plan.StagingPath));
    }

    [Fact]
    public void Hardlinked_input_and_bounded_cli_input_are_rejected()
    {
        using var fixture = new ImportFixture();
        var link = fixture.ModelPath + ".link";
        Assert.True(CreateHardLinkW(link, fixture.ModelPath, IntPtr.Zero));
        AssertFailure(LocalSttProvisioningFailure.SourceUnsafe,
            () => fixture.Importer().Preview(fixture.Request()));
        AssertFailure(LocalSttProvisioningFailure.SourceUnsafe,
            () => LocalSttOfflinePackageInput.ReadBoundedLocalFile(fixture.ModelPath, 1024));
    }

    [Fact]
    public void Input_handles_and_ancestors_are_retained_through_copy_and_finalize()
    {
        using var fixture = new ImportFixture();
        var checks = 0;
        var importer = fixture.Importer(io: (point, _) =>
        {
            if (point != LocalSttImportIoPoint.BeforeFinalize) return;
            Assert.Throws<IOException>(() => File.WriteAllText(fixture.ModelPath, "foreign"));
            Assert.Throws<IOException>(() => File.Move(fixture.ArchivePath, fixture.ArchivePath + ".moved"));
            Assert.Throws<IOException>(() => Directory.Move(fixture.NoticeDirectory, fixture.NoticeDirectory + ".moved"));
            checks++;
        });
        var plan = importer.Preview(fixture.Request());
        importer.Import(plan, Approve(plan));
        Assert.Equal(1, checks);
    }

    [Fact]
    public void Tampering_at_finalization_is_detected_and_owned_files_cleaned()
    {
        using var fixture = new ImportFixture();
        LocalSttImportPlan? plan = null;
        var importer = fixture.Importer(io: (point, _) =>
        {
            if (point == LocalSttImportIoPoint.BeforeFinalize)
                File.AppendAllText(Path.Combine(plan!.StagingPath, "payload", "runtime", "ggml.dll"), "changed");
        });
        plan = importer.Preview(fixture.Request());
        AssertFailure(LocalSttProvisioningFailure.InvalidReceipt, () => importer.Import(plan, Approve(plan)));
        Assert.False(Directory.Exists(plan.StagingPath));
        Assert.False(Directory.Exists(fixture.Destination));
    }

    [Theory]
    [InlineData((int)LocalSttImportIoPoint.BeforeWrite)]
    [InlineData((int)LocalSttImportIoPoint.BeforeFlush)]
    [InlineData((int)LocalSttImportIoPoint.BeforeFinalize)]
    public void Early_and_late_cancellation_never_abandon_partial_owner(int pointValue)
    {
        var target = (LocalSttImportIoPoint)pointValue;
        using var fixture = new ImportFixture();
        using var cancellation = new CancellationTokenSource();
        var importer = fixture.Importer(io: (point, _) =>
        {
            if (point == target) cancellation.Cancel();
        });
        var plan = importer.Preview(fixture.Request());
        AssertFailure(LocalSttProvisioningFailure.Canceled,
            () => importer.Import(plan, Approve(plan), cancellation.Token));
        Assert.False(Directory.Exists(plan.StagingPath));
    }

    [Fact]
    public void Other_importer_cannot_clean_an_active_transaction_even_on_same_thread()
    {
        using var fixture = new ImportFixture();
        var checks = 0;
        var importer = fixture.Importer(io: (point, _) =>
        {
            if (point != LocalSttImportIoPoint.AfterSourceCopy) return;
            AssertFailure(LocalSttProvisioningFailure.Busy,
                () => fixture.Importer().CleanupOwnedStaging(fixture.Root));
            checks++;
        });
        var plan = importer.Preview(fixture.Request());
        importer.Import(plan, Approve(plan));
        Assert.Equal(1, checks);
    }

    [Fact]
    public void Plan_and_authorization_are_snapshot_bound_one_use_and_owner_bound()
    {
        using var fixture = new ImportFixture();
        var importer = fixture.Importer();
        var plan = importer.Preview(fixture.Request());
        var authorization = Approve(plan);
        fixture.Evidence.RuntimeFiles[0] = fixture.Evidence.RuntimeFiles[0] with { Sha256 = new string('0', 64) };
        importer.Import(plan, authorization);
        AssertFailure(LocalSttProvisioningFailure.AuthorizationConsumed, () => importer.Import(plan, authorization));
        AssertFailure(LocalSttProvisioningFailure.AuthorizationMismatch,
            () => fixture.Importer().Import(plan, Approve(plan)));
    }

    [Fact]
    public void New_preview_invalidates_old_approval_without_creating_files()
    {
        using var fixture = new ImportFixture();
        var importer = fixture.Importer();
        var first = importer.Preview(fixture.Request());
        importer.Preview(fixture.Request());
        AssertFailure(LocalSttProvisioningFailure.AuthorizationMismatch, () => importer.Import(first, Approve(first)));
        Assert.False(Directory.Exists(first.StagingPath));
    }

    [Fact]
    public void Legacy_v1_manifest_retains_exact_bytes_hash_and_safe_offline_defaults()
    {
        var current = LocalSttPackageManifest.Current.CopyDocumentBytes();
        var json = JsonNode.Parse(current)!.AsObject();
        json.Remove("acquisition");
        var legacy = System.Text.Encoding.UTF8.GetBytes(json.ToJsonString());
        var read = LocalSttPackageManifest.Read(legacy);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(legacy)), read.DocumentSha256);
        Assert.Equal(legacy, read.CopyDocumentBytes());
        Assert.False(read.AutomaticDownloadAllowed);
        Assert.Equal(LocalSttPackageAcquisitionMode.CallerSuppliedOfflineImportOnly, read.AcquisitionMode);
        Assert.Equal(LocalSttPackageManifest.Current.ModelSha256, read.ModelSha256);
        json["acquisition"] = JsonNode.Parse(current)!["acquisition"]!.DeepClone();
        json["acquisition"]!["automatic_download"] = true;
        Assert.Throws<LocalSttContractException>(() => LocalSttPackageManifest.Read(
            System.Text.Encoding.UTF8.GetBytes(json.ToJsonString())));
    }

    [Fact]
    public void Build_plan_collections_cannot_mutate_hash_bound_plan()
    {
        using var fixture = new ImportFixture();
        var plan = LocalSttBuildPlan.Create(fixture.Manifest, fixture.BuildFacts());
        var before = plan.ToCanonicalJson();
        Assert.Throws<NotSupportedException>(() =>
            ((IList<string>)plan.ConfigureDefinitions)[0] = "arbitrary");
        Assert.Throws<NotSupportedException>(() =>
            ((IList<LocalSttBuildOutput>)plan.ExpectedOutputs).Clear());
        Assert.Throws<NotSupportedException>(() =>
            ((IList<LocalSttToolchainComponentFacts>)plan.Toolchain).Clear());
        Assert.Equal(before, plan.ToCanonicalJson());
    }

    private static LocalSttImportAuthorization Approve(LocalSttImportPlan plan) =>
        plan.Authorize(LocalSttRightsDecision.ApproveExactRuntimeModelAndNoticeRights);

    [Fact]
    public void Cleanup_preflight_preserves_foreign_notices_and_every_owned_file()
    {
        using var fixture = new ImportFixture();
        LocalSttImportPlan? plan = null;
        var importer = fixture.Importer(io: (point, _) =>
        {
            if (point != LocalSttImportIoPoint.AfterSourceCopy) return;
            File.WriteAllText(Path.Combine(plan!.StagingPath, "notices", "Foreign.txt"), "foreign");
            throw new IOException();
        });
        plan = importer.Preview(fixture.Request());
        var error = Assert.Throws<LocalSttProvisioningException>(() => importer.Import(plan, Approve(plan)));
        Assert.Equal(LocalSttProvisioningFailure.CleanupPending, error.Failure);
        Assert.Equal("foreign", File.ReadAllText(Path.Combine(plan.StagingPath, "notices", "Foreign.txt")));
        Assert.True(File.Exists(Path.Combine(plan.StagingPath, "payload", "models", "ggml-base.en.bin")));
        Assert.True(File.Exists(Path.Combine(plan.StagingPath, "package-owner.v1.json")));
        File.Delete(Path.Combine(plan.StagingPath, "notices", "Foreign.txt"));
        Assert.Equal(1, fixture.Importer().CleanupOwnedStaging(fixture.Root).RemovedOwnedDirectories);
    }

    [Fact]
    public void Cleanup_checks_the_actual_delete_handle_identity_after_interference()
    {
        using var fixture = new ImportFixture();
        LocalSttImportPlan? plan = null;
        var changed = false;
        var importer = fixture.Importer(io: (point, _) =>
        {
            if (point == LocalSttImportIoPoint.AfterSourceCopy) throw new IOException();
            if (point != LocalSttImportIoPoint.BeforeCleanupEntry || changed) return;
            changed = true;
            var path = Path.Combine(plan!.StagingPath, "payload", "models", "ggml-base.en.bin");
            var replacement = Path.Combine(fixture.Root, "replacement.bin");
            File.WriteAllText(replacement, "foreign replacement");
            File.Move(replacement, path, overwrite: true);
        });
        plan = importer.Preview(fixture.Request());
        var error = Assert.Throws<LocalSttProvisioningException>(() => importer.Import(plan, Approve(plan)));
        Assert.Equal(LocalSttProvisioningFailure.CleanupPending, error.Failure);
        Assert.Equal("foreign replacement", File.ReadAllText(Path.Combine(plan.StagingPath,
            "payload", "models", "ggml-base.en.bin")));
        Assert.True(File.Exists(Path.Combine(plan.StagingPath, "package-owner.v1.json")));
    }

    [Theory]
    [InlineData("entry-count")]
    [InlineData("central-size")]
    [InlineData("local-name")]
    [InlineData("multidisk")]
    public void ZIP_preflight_rejects_malformed_metadata_before_entry_materialization(string variant)
    {
        using var fixture = new ImportFixture();
        var bytes = File.ReadAllBytes(fixture.ArchivePath);
        var end = bytes.Length - 22;
        switch (variant)
        {
            case "entry-count":
                System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(end + 10), ushort.MaxValue);
                break;
            case "central-size":
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(end + 12), uint.MaxValue);
                break;
            case "local-name": bytes[30] ^= 1; break;
            case "multidisk": bytes[end + 4] = 1; break;
        }
        using var stream = new MemoryStream(bytes);
        Assert.Throws<LocalSttProvisioningException>(() =>
            RestrictedRuntimeArchive.Verify(stream, fixture.Manifest, fixture.Evidence, null, null, CancellationToken.None));
    }

    private static void AssertFailure(LocalSttProvisioningFailure expected, Action action) =>
        Assert.Equal(expected, Assert.Throws<LocalSttProvisioningException>(action).Failure);

    [Fact]
    public void Verified_payload_bytes_remain_locked_during_source_revalidation()
    {
        using var fixture = new ImportFixture();
        LocalSttImportPlan? plan = null;
        var checks = 0;
        var importer = fixture.Importer(io: (point, _) =>
        {
            if (point != LocalSttImportIoPoint.AfterReadBack) return;
            var root = plan!.StagingPath;
            Assert.Throws<IOException>(() =>
                File.WriteAllText(Path.Combine(root, "payload", "runtime", "ggml.dll"), "changed"));
            checks++;
        });
        plan = importer.Preview(fixture.Request());
        importer.Import(plan, Approve(plan));
        Assert.Equal(1, checks);
        Assert.Equal(PackageVerificationStatus.Verified, importer.InspectInstalled(fixture.Destination).PackageVerifierStatus);
    }

    [Fact]
    public void Name_substitution_during_publication_cannot_return_a_verified_receipt()
    {
        using var fixture = new ImportFixture();
        var importer = fixture.Importer(io: (point, _) =>
        {
            if (point != LocalSttImportIoPoint.AfterPublish) return;
            var replacement = Path.Combine(fixture.Root, "foreign.bin");
            File.WriteAllText(replacement, "foreign");
            File.Move(replacement, Path.Combine(fixture.Destination, "payload", "runtime", "ggml.dll"), overwrite: true);
        });
        var plan = importer.Preview(fixture.Request());
        var error = Assert.Throws<LocalSttProvisioningException>(() => importer.Import(plan, Approve(plan)));
        Assert.Contains(error.Failure, new[] { LocalSttProvisioningFailure.InvalidReceipt, LocalSttProvisioningFailure.SourceUnsafe });
        Assert.False(Directory.Exists(fixture.Destination));
        Assert.False(Directory.Exists(plan.StagingPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Same_instance_recovery_handles_partial_marker_or_removed_pending_directory(bool externallyRemoved)
    {
        using var fixture = new ImportFixture();
        var fail = true;
        var importer = fixture.Importer(io: (point, _) =>
        {
            if (fail && point is LocalSttImportIoPoint.BeforeWrite or LocalSttImportIoPoint.BeforeCleanup)
                throw new IOException();
        });
        var plan = importer.Preview(fixture.Request());
        AssertFailure(LocalSttProvisioningFailure.CleanupPending, () => importer.Import(plan, Approve(plan)));
        Assert.Empty(File.ReadAllBytes(Path.Combine(plan.StagingPath, "package-owner.v1.json")));
        if (externallyRemoved)
        {
            File.Delete(Path.Combine(plan.StagingPath, "package-owner.v1.json"));
            Directory.Delete(plan.StagingPath);
        }
        fail = false;
        var result = importer.CleanupOwnedStaging(fixture.Root);
        Assert.Equal(externallyRemoved ? 0 : 1, result.RemovedOwnedDirectories);
        Assert.True(importer.Preview(fixture.Request()).CanImport);
    }

    [Fact]
    public void Same_instance_recovery_never_adopts_a_replacement_root()
    {
        using var fixture = new ImportFixture();
        var fail = true;
        var importer = fixture.Importer(io: (point, _) =>
        {
            if (fail && point is LocalSttImportIoPoint.BeforeWrite or LocalSttImportIoPoint.BeforeCleanup)
                throw new IOException();
        });
        var plan = importer.Preview(fixture.Request());
        AssertFailure(LocalSttProvisioningFailure.CleanupPending, () => importer.Import(plan, Approve(plan)));
        Directory.Move(plan.StagingPath, plan.StagingPath + ".original");
        Directory.CreateDirectory(plan.StagingPath);
        File.WriteAllText(Path.Combine(plan.StagingPath, "package-owner.v1.json"), "foreign");
        fail = false;
        AssertFailure(LocalSttProvisioningFailure.StagingConflict, () => importer.CleanupOwnedStaging(fixture.Root));
        Assert.Equal("foreign", File.ReadAllText(Path.Combine(plan.StagingPath, "package-owner.v1.json")));
    }

    [Fact]
    public void Cleanup_pins_root_before_reading_owner_and_comparing_identity()
    {
        using var fixture = new ImportFixture();
        LocalSttImportPlan? plan = null;
        var fail = true;
        var probes = 0;
        var inspector = new ProbeInspector((path, directory) =>
        {
            if (!fail && !directory && path == Path.Combine(plan!.StagingPath, "package-owner.v1.json"))
            {
                Assert.Throws<IOException>(() => Directory.Move(plan.StagingPath, plan.StagingPath + ".moved"));
                probes++;
            }
        });
        var importer = new PhysicalLocalSttPackageImporter(fixture.Manifest, inspector, _ => long.MaxValue)
        {
            Io = (point, _) =>
            {
                if (fail && point is LocalSttImportIoPoint.BeforeWrite or LocalSttImportIoPoint.BeforeCleanup)
                    throw new IOException();
            }
        };
        plan = importer.Preview(fixture.Request());
        AssertFailure(LocalSttProvisioningFailure.CleanupPending, () => importer.Import(plan, Approve(plan)));
        fail = false;
        Assert.Equal(1, importer.CleanupOwnedStaging(fixture.Root).RemovedOwnedDirectories);
        Assert.True(probes > 0);
    }

    private sealed class ProbeInspector(Action<string, bool> probe) : ILocalPathInspector
    {
        public void AssertSafeExisting(string path, bool directory)
        {
            probe(path, directory);
            new PhysicalLocalPathInspector().AssertSafeExisting(path, directory);
        }
    }

    [Fact]
    public void Cancellation_after_rename_discards_receipt_and_removes_only_owned_destination()
    {
        using var fixture = new ImportFixture();
        using var cancellation = new CancellationTokenSource();
        var importer = fixture.Importer(io: (point, _) =>
        {
            if (point == LocalSttImportIoPoint.AfterPublish) cancellation.Cancel();
        });
        var plan = importer.Preview(fixture.Request());
        AssertFailure(LocalSttProvisioningFailure.Canceled,
            () => importer.Import(plan, Approve(plan), cancellation.Token));
        Assert.False(Directory.Exists(fixture.Destination));
        Assert.False(Directory.Exists(plan.StagingPath));
        Assert.True(File.Exists(fixture.ModelPath));
    }

    [Fact]
    public void Failed_postrename_cleanup_can_retry_the_exact_owned_destination()
    {
        using var fixture = new ImportFixture();
        var fail = true;
        var importer = fixture.Importer(io: (point, _) =>
        {
            if (fail && point is LocalSttImportIoPoint.AfterPublish or LocalSttImportIoPoint.BeforeCleanup)
                throw new IOException();
        });
        var plan = importer.Preview(fixture.Request());
        var error = Assert.Throws<LocalSttProvisioningException>(() => importer.Import(plan, Approve(plan)));
        Assert.Equal(LocalSttProvisioningFailure.CleanupPending, error.Failure);
        Assert.Equal(fixture.Destination, error.RetainedDirectory);
        Assert.Equal(LocalSttProvisioningFailure.StorageFailure, error.OriginalFailure);
        fail = false;
        Assert.Equal(1, importer.CleanupOwnedStaging(fixture.Root).RemovedOwnedDirectories);
        Assert.False(Directory.Exists(fixture.Destination));
        Assert.True(importer.Preview(fixture.Request()).CanImport);
    }

    [Fact]
    public void Postrename_foreign_root_substitution_is_quarantined_and_never_deleted()
    {
        using var fixture = new ImportFixture();
        var importer = fixture.Importer(io: (point, _) =>
        {
            if (point != LocalSttImportIoPoint.AfterPublish) return;
            Directory.Move(fixture.Destination, fixture.Destination + "-moved");
            Directory.CreateDirectory(fixture.Destination);
            File.WriteAllText(Path.Combine(fixture.Destination, "package-owner.v1.json"), "foreign");
        });
        var plan = importer.Preview(fixture.Request());
        var error = Assert.Throws<LocalSttProvisioningException>(() => importer.Import(plan, Approve(plan)));
        Assert.Equal(LocalSttProvisioningFailure.CleanupPending, error.Failure);
        Assert.Equal(LocalSttProvisioningFailure.StagingConflict, error.OriginalFailure);
        Assert.Equal("foreign", File.ReadAllText(Path.Combine(fixture.Destination, "package-owner.v1.json")));
        AssertFailure(LocalSttProvisioningFailure.StagingConflict, () => importer.CleanupOwnedStaging(fixture.Root));
        Assert.Equal("foreign", File.ReadAllText(Path.Combine(fixture.Destination, "package-owner.v1.json")));
    }

    [Fact]
    public void Failure_before_root_identity_capture_never_authorizes_markerless_cleanup()
    {
        using var fixture = new ImportFixture();
        var fail = true;
        var inspector = new ProbeInspector((path, directory) =>
        {
            if (fail && directory && path.EndsWith(".pending", StringComparison.Ordinal))
                throw new IOException();
        });
        var importer = new PhysicalLocalSttPackageImporter(fixture.Manifest, inspector, _ => long.MaxValue);
        var plan = importer.Preview(fixture.Request());
        AssertFailure(LocalSttProvisioningFailure.CleanupPending, () => importer.Import(plan, Approve(plan)));
        Directory.Move(plan.StagingPath, plan.StagingPath + ".original");
        Directory.CreateDirectory(plan.StagingPath);
        var marker = Path.Combine(plan.StagingPath, "package-owner.v1.json");
        File.WriteAllText(marker, "foreign");
        fail = false;
        AssertFailure(LocalSttProvisioningFailure.StagingConflict, () => importer.CleanupOwnedStaging(fixture.Root));
        Assert.Equal("foreign", File.ReadAllText(marker));
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string path, string existing, IntPtr security);
}
