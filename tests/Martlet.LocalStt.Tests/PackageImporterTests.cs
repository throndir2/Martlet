using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Martlet.LocalStt;

namespace Martlet.LocalStt.Tests;

public sealed class PackageImporterTests
{
    [Fact]
    public void Exact_offline_import_finalizes_deterministic_layout_and_hands_off_to_verifier()
    {
        using var fixture = new ImportFixture();
        var importer = fixture.Importer();
        var plan = importer.Preview(fixture.Request());

        Assert.True(plan.CanImport);
        Assert.True(plan.NetworkAcquisitionBlocked);
        Assert.False(plan.AutomaticAcquisitionAllowed);
        Assert.False(plan.DeniedEgressEvidenceIncluded);
        var receipt = importer.Import(
            plan,
            plan.Authorize(LocalSttRightsDecision
                .ApproveExactRuntimeModelAndNoticeRights));

        Assert.False(receipt.AutomaticAcquisitionPerformed);
        Assert.False(receipt.DeniedEgressEvidenceIncluded);
        Assert.Contains(receipt.Files,
            file => file.Path == "payload/runtime/whisper-cli.exe");
        Assert.Contains(receipt.Files,
            file => file.Path == "metadata/sbom.cdx.json");
        Assert.True(File.Exists(Path.Combine(
            fixture.Destination,
            "metadata",
            "package-receipt.v1.json")));

        var inspection = importer.InspectInstalled(fixture.Destination);
        Assert.Equal(PackageVerificationStatus.Verified,
            inspection.PackageVerifierStatus);
        Assert.Equal(receipt.IntegritySha256,
            inspection.Receipt.IntegritySha256);
        var sbom = ProvisioningWire.Read<CycloneDxDocument>(
            File.ReadAllBytes(Path.Combine(
                fixture.Destination,
                "metadata",
                "sbom.cdx.json")),
            ProvisioningWire.MaximumSbomBytes,
            LocalSttProvisioningFailure.InvalidReceipt,
            canonical: true);
        Assert.Contains(sbom.Components,
            component => component.BomRef ==
                "component:license:Runtime-LICENSE.txt");
        Assert.Contains(sbom.Dependencies,
            dependency =>
                dependency.Ref ==
                    "component:license:Runtime-LICENSE.txt" &&
                dependency.DependsOn.Contains(
                    "file:payload/runtime/whisper-cli.exe",
                    StringComparer.Ordinal));
    }

    [Fact]
    public void Import_defaults_to_no_and_incomplete_content_evidence_stays_blocked()
    {
        using var fixture = new ImportFixture();
        var importer = fixture.Importer();
        var plan = importer.Preview(fixture.Request());
        var refused = Assert.Throws<LocalSttProvisioningException>(() =>
            importer.Import(plan, plan.Authorize()));
        Assert.Equal(LocalSttProvisioningFailure.RightsNotApproved,
            refused.Failure);
        Assert.False(Directory.Exists(fixture.Destination));

        var blockedEvidence = fixture.Evidence with
        {
            RuntimeFileHashesComplete = false,
            RuntimeFiles = []
        };
        var blocked = importer.Preview(
            fixture.Request(blockedEvidence));
        Assert.False(blocked.CanImport);
        Assert.Equal(LocalSttProvisioningFailure.ContentPinIncomplete,
            blocked.Blocker);
    }

    [Theory]
    [InlineData(ArchiveVariant.Traversal,
        LocalSttProvisioningFailure.ArchiveUnsafeEntry)]
    [InlineData(ArchiveVariant.CaseCollision,
        LocalSttProvisioningFailure.ArchiveUnsafeEntry)]
    [InlineData(ArchiveVariant.Symlink,
        LocalSttProvisioningFailure.ArchiveUnsafeEntry)]
    [InlineData(ArchiveVariant.Device,
        LocalSttProvisioningFailure.ArchiveUnsafeEntry)]
    [InlineData(ArchiveVariant.ZipBomb,
        LocalSttProvisioningFailure.ArchiveLimitExceeded)]
    [InlineData(ArchiveVariant.TooManyEntries,
        LocalSttProvisioningFailure.ArchiveLimitExceeded)]
    public void Restricted_archive_rejects_unsafe_entries_and_bombs(
        ArchiveVariant variant,
        LocalSttProvisioningFailure expected)
    {
        using var fixture = new ImportFixture(variant);

        var error = Assert.Throws<LocalSttProvisioningException>(() =>
            fixture.Importer().Preview(fixture.Request()));

        Assert.Equal(expected, error.Failure);
        Assert.False(Directory.Exists(fixture.Destination));
    }

    [Fact]
    public void Corrupt_archive_wrong_architecture_and_wrong_file_pin_are_rejected()
    {
        using var corrupt = new ImportFixture();
        File.AppendAllText(corrupt.ArchivePath, "corrupt");
        AssertFailure(
            LocalSttProvisioningFailure.ArchiveHashMismatch,
            () => corrupt.Importer().Preview(corrupt.Request()));

        using var architecture = new ImportFixture(
            ArchiveVariant.WrongArchitecture);
        AssertFailure(
            LocalSttProvisioningFailure.UnsupportedBinary,
            () => architecture.Importer().Preview(
                architecture.Request()));

        using var pin = new ImportFixture();
        var changedPin = pin.Evidence with
        {
            RuntimeFiles =
            [
                pin.Evidence.RuntimeFiles[0] with
                {
                    Sha256 = new string('0', 64)
                },
                pin.Evidence.RuntimeFiles[1]
            ]
        };
        AssertFailure(
            LocalSttProvisioningFailure.RuntimeFileMismatch,
            () => pin.Importer().Preview(pin.Request(changedPin)));
    }

    [Fact]
    public void Revision_model_hash_header_and_license_notice_fail_closed()
    {
        using var revision = new ImportFixture();
        AssertFailure(
            LocalSttProvisioningFailure.InvalidRequest,
            () => revision.Importer().Preview(revision.Request(
                revision.Evidence with
                {
                    RuntimeRevision = new string('c', 40)
                })));

        using var modelHash = new ImportFixture();
        File.AppendAllText(modelHash.ModelPath, "changed");
        AssertFailure(
            LocalSttProvisioningFailure.ModelHashMismatch,
            () => modelHash.Importer().Preview(modelHash.Request()));

        using var modelHeader = new ImportFixture(
            ArchiveVariant.InvalidModelHeader);
        AssertFailure(
            LocalSttProvisioningFailure.ModelInvalid,
            () => modelHeader.Importer().Preview(
                modelHeader.Request()));

        using var notice = new ImportFixture();
        File.AppendAllText(notice.RuntimeNoticePath, "changed");
        AssertFailure(
            LocalSttProvisioningFailure.LicenseNoticeMismatch,
            () => notice.Importer().Preview(notice.Request()));

        using var missing = new ImportFixture();
        File.Delete(missing.ModelNoticePath);
        AssertFailure(
            LocalSttProvisioningFailure.LicenseNoticeMissing,
            () => missing.Importer().Preview(missing.Request()));
    }

    [Fact]
    public void Disk_access_interruption_and_cancellation_leave_no_destination()
    {
        using var disk = new ImportFixture();
        AssertFailure(
            LocalSttProvisioningFailure.InsufficientDisk,
            () => disk.Importer(availableBytes: 0)
                .Preview(disk.Request()));

        using var access = new ImportFixture();
        var accessImporter = access.Importer(io: (point, _) =>
        {
            if (point == LocalSttImportIoPoint.BeforeCreateFile)
                throw new UnauthorizedAccessException();
        });
        var accessPlan = accessImporter.Preview(access.Request());
        AssertFailure(
            LocalSttProvisioningFailure.AccessDenied,
            () => accessImporter.Import(
                accessPlan,
                accessPlan.Authorize(LocalSttRightsDecision
                    .ApproveExactRuntimeModelAndNoticeRights)));
        Assert.False(Directory.Exists(access.Destination));
        Assert.False(Directory.Exists(accessPlan.StagingPath));

        using var interrupted = new ImportFixture();
        var interruptedImporter = interrupted.Importer(io: (point, _) =>
        {
            if (point == LocalSttImportIoPoint.AfterSourceCopy)
                throw new IOException();
        });
        var interruptedPlan = interruptedImporter.Preview(
            interrupted.Request());
        AssertFailure(
            LocalSttProvisioningFailure.StorageFailure,
            () => interruptedImporter.Import(
                interruptedPlan,
                interruptedPlan.Authorize(LocalSttRightsDecision
                    .ApproveExactRuntimeModelAndNoticeRights)));
        Assert.False(Directory.Exists(interruptedPlan.StagingPath));

        using var canceled = new ImportFixture();
        using var cancellation = new CancellationTokenSource();
        var canceledImporter = canceled.Importer(io: (point, _) =>
        {
            if (point == LocalSttImportIoPoint.AfterSourceCopy)
                cancellation.Cancel();
        });
        var canceledPlan = canceledImporter.Preview(canceled.Request());
        AssertFailure(
            LocalSttProvisioningFailure.Canceled,
            () => canceledImporter.Import(
                canceledPlan,
                canceledPlan.Authorize(LocalSttRightsDecision
                    .ApproveExactRuntimeModelAndNoticeRights),
                cancellation.Token));
        Assert.False(Directory.Exists(canceledPlan.StagingPath));
    }

    [Fact]
    public void Source_replacement_and_same_path_aliases_are_rejected()
    {
        using var replacement = new ImportFixture();
        var importer = replacement.Importer();
        var plan = importer.Preview(replacement.Request());
        File.AppendAllText(replacement.ModelPath, "replacement");

        AssertFailure(
            LocalSttProvisioningFailure.SourceChanged,
            () => importer.Import(
                plan,
                plan.Authorize(LocalSttRightsDecision
                    .ApproveExactRuntimeModelAndNoticeRights)));
        Assert.False(Directory.Exists(replacement.Destination));

        using var alias = new ImportFixture();
        AssertFailure(
            LocalSttProvisioningFailure.SourceUnsafe,
            () => alias.Importer().Preview(alias.Request(
                modelPath: alias.ArchivePath)));

        AssertFailure(
            LocalSttProvisioningFailure.SourceUnsafe,
            () => LocalSttOfflinePackageInput.ReadBoundedLocalFile(
                @"\\server\share\facts.json",
                1_024));
    }

    [Fact]
    public void Staging_path_preemption_is_not_adopted_or_cleaned()
    {
        using var fixture = new ImportFixture();
        LocalSttImportPlan? plan = null;
        var injected = false;
        var importer = fixture.Importer(io: (point, _) =>
        {
            if (point != LocalSttImportIoPoint.BeforeCreateDirectory ||
                injected ||
                plan is null)
                return;
            injected = true;
            Directory.CreateDirectory(plan.StagingPath);
            File.WriteAllText(
                Path.Combine(plan.StagingPath, "foreign.txt"),
                "foreign");
        });
        plan = importer.Preview(fixture.Request());

        AssertFailure(
            LocalSttProvisioningFailure.StagingConflict,
            () => importer.Import(
                plan,
                plan.Authorize(LocalSttRightsDecision
                    .ApproveExactRuntimeModelAndNoticeRights)));
        Assert.True(File.Exists(Path.Combine(
            plan.StagingPath,
            "foreign.txt")));
        Assert.False(Directory.Exists(fixture.Destination));
    }

    [Fact]
    public void Failed_cleanup_is_retained_then_restart_cleanup_removes_only_owned_stage()
    {
        using var fixture = new ImportFixture();
        var failCleanup = true;
        var cleanupEntries = 0;
        var importer = fixture.Importer(io: (point, _) =>
        {
            if (point == LocalSttImportIoPoint.AfterSourceCopy)
                throw new IOException();
            if (point == LocalSttImportIoPoint.BeforeCleanupEntry &&
                failCleanup &&
                ++cleanupEntries == 2)
                throw new UnauthorizedAccessException();
        });
        var plan = importer.Preview(fixture.Request());

        var error = Assert.Throws<LocalSttProvisioningException>(() =>
            importer.Import(
                plan,
                plan.Authorize(LocalSttRightsDecision
                    .ApproveExactRuntimeModelAndNoticeRights)));
        Assert.Equal(LocalSttProvisioningFailure.CleanupPending,
            error.Failure);
        Assert.Equal(LocalSttProvisioningFailure.StorageFailure,
            error.OriginalFailure);
        Assert.True(Directory.Exists(plan.StagingPath));
        Assert.True(File.Exists(Path.Combine(
            plan.StagingPath,
            "package-owner.v1.json")));

        var unowned = Path.Combine(
            fixture.Root,
            ".martlet-local-stt-00000000000000000000000000000000.pending");
        Directory.CreateDirectory(unowned);
        File.WriteAllBytes(
            Path.Combine(unowned, "package-owner.v1.json"),
            ProvisioningWire.Write(new PackageStageOwnerDocument
            {
                FormatVersion = 1,
                Kind = "martlet_local_stt_owned_package",
                TransactionId =
                    "00000000000000000000000000000000",
                PackageId = fixture.Manifest.Id,
                ManifestSha256 = fixture.Manifest.DocumentSha256,
                ImportEvidenceSha256 = new string('f', 64),
                NoticeFiles = ["Runtime-LICENSE.txt", "Model-LICENSE.txt"]
            }));
        failCleanup = false;
        var cleanup = importer.CleanupOwnedStaging(fixture.Root);

        Assert.Equal(1, cleanup.RemovedOwnedDirectories);
        Assert.Equal(1, cleanup.PreservedUnownedDirectories);
        Assert.False(Directory.Exists(plan.StagingPath));
        Assert.True(Directory.Exists(unowned));
    }

    [Fact]
    public void Installed_drift_invalidates_receipt_before_launch_handoff()
    {
        using var fixture = new ImportFixture();
        var importer = fixture.Importer();
        var plan = importer.Preview(fixture.Request());
        importer.Import(
            plan,
            plan.Authorize(LocalSttRightsDecision
                .ApproveExactRuntimeModelAndNoticeRights));
        File.AppendAllText(
            Path.Combine(
                fixture.Destination,
                "payload",
                "runtime",
                "ggml.dll"),
            "changed");

        AssertFailure(
            LocalSttProvisioningFailure.InvalidReceipt,
            () => importer.InspectInstalled(fixture.Destination));
    }

    [Fact]
    public void Resealed_receipt_cannot_override_canonical_notice_evidence()
    {
        using var fixture = new ImportFixture();
        var importer = fixture.Importer();
        var plan = importer.Preview(fixture.Request());
        importer.Import(
            plan,
            plan.Authorize(LocalSttRightsDecision
                .ApproveExactRuntimeModelAndNoticeRights));
        var noticePath = Path.Combine(
            fixture.Destination,
            "notices",
            "Runtime-LICENSE.txt");
        var changed = File.ReadAllBytes(noticePath);
        changed[^2] ^= 1;
        File.WriteAllBytes(noticePath, changed);

        var receiptPath = Path.Combine(
            fixture.Destination,
            "metadata",
            "package-receipt.v1.json");
        var receipt = ProvisioningWire.ReadReceipt(
            File.ReadAllBytes(receiptPath));
        var changedFiles = receipt.Files
            .Select(file => file.Path == "notices/Runtime-LICENSE.txt"
                ? file with { Sha256 = Hash(changed) }
                : file)
            .ToArray();
        File.WriteAllBytes(
            receiptPath,
            ProvisioningWire.WriteReceipt(
                receipt with
                {
                    Files = changedFiles,
                    IntegritySha256 = ""
                }));

        AssertFailure(
            LocalSttProvisioningFailure.InvalidReceipt,
            () => importer.InspectInstalled(fixture.Destination));
    }

    private static string Hash(byte[] bytes) =>
        Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static void AssertFailure(
        LocalSttProvisioningFailure expected,
        Action action)
    {
        var error = Assert.Throws<LocalSttProvisioningException>(action);
        Assert.Equal(expected, error.Failure);
    }
}

public sealed class PackageBuildPlanTests
{
    [Fact]
    public void Exact_source_and_toolchain_facts_create_a_nonexecuting_offline_plan()
    {
        using var fixture = new ImportFixture();
        var plan = LocalSttBuildPlan.Create(
            fixture.Manifest,
            fixture.BuildFacts());

        Assert.False(plan.CompilerExecutionAllowedByDefault);
        Assert.False(plan.AutomaticDependencyAcquisitionAllowed);
        Assert.False(plan.NetworkAllowed);
        Assert.True(plan.RuntimeFileHashQualificationRequired);
        Assert.Equal(64, plan.PlanSha256.Length);
        Assert.Equal(
            fixture.Manifest.Document.Runtime.Files.Length,
            plan.ExpectedOutputs.Count);
        Assert.NotEmpty(plan.ToCanonicalJson());
    }

    [Fact]
    public void Wrong_source_revision_or_incomplete_toolchain_hash_is_rejected()
    {
        using var fixture = new ImportFixture();
        var revision = fixture.BuildFacts() with
        {
            Revision = new string('c', 40)
        };
        var sourceError = Assert.Throws<LocalSttProvisioningException>(() =>
            LocalSttBuildPlan.Create(fixture.Manifest, revision));
        Assert.Equal(LocalSttProvisioningFailure.BuildSourceMismatch,
            sourceError.Failure);

        var toolchain = fixture.BuildFacts() with
        {
            Compiler = fixture.BuildFacts().Compiler with
            {
                Sha256 = "incomplete"
            }
        };
        var toolchainError = Assert.Throws<LocalSttProvisioningException>(() =>
            LocalSttBuildPlan.Create(fixture.Manifest, toolchain));
        Assert.Equal(LocalSttProvisioningFailure.ToolchainInvalid,
            toolchainError.Failure);
    }
}

public enum ArchiveVariant
{
    Valid,
    Traversal,
    CaseCollision,
    Symlink,
    Device,
    ZipBomb,
    TooManyEntries,
    WrongArchitecture,
    InvalidModelHeader
}

internal sealed class ImportFixture : IDisposable
{
    private const string RuntimeRevision =
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string ModelRevision =
        "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private readonly byte[] executable;
    private readonly byte[] dependency;
    private readonly byte[] model;
    private readonly byte[] runtimeNotice;
    private readonly byte[] modelNotice;

    internal string Root { get; }
    internal string ArchivePath { get; }
    internal string ModelPath { get; }
    internal string NoticeDirectory { get; }
    internal string RuntimeNoticePath { get; }
    internal string ModelNoticePath { get; }
    internal string Destination { get; }
    internal LocalSttPackageManifest Manifest { get; }
    internal LocalSttImportEvidence Evidence { get; }

    internal ImportFixture(ArchiveVariant variant = ArchiveVariant.Valid)
    {
        Root = Path.Combine(
            Path.GetTempPath(),
            "Martlet.LocalStt.ImportTests",
            Guid.NewGuid().ToString("N"));
        var inputs = Path.Combine(Root, "inputs");
        NoticeDirectory = Path.Combine(inputs, "notices");
        Directory.CreateDirectory(NoticeDirectory);
        ArchivePath = Path.Combine(inputs, "whisper-bin-x64.zip");
        ModelPath = Path.Combine(inputs, "ggml-base.en.bin");
        RuntimeNoticePath = Path.Combine(
            NoticeDirectory,
            "Runtime-LICENSE.txt");
        ModelNoticePath = Path.Combine(
            NoticeDirectory,
            "Model-LICENSE.txt");
        Destination = Path.Combine(Root, "installed-package");

        executable = Pe(
            dynamicLibrary: false,
            machine: variant == ArchiveVariant.WrongArchitecture
                ? (ushort)0x014c
                : (ushort)0x8664,
            seed: 0x31);
        dependency = Pe(
            dynamicLibrary: true,
            machine: 0x8664,
            seed: 0x52);
        model = Model(
            validHeader: variant != ArchiveVariant.InvalidModelHeader);
        runtimeNotice = Encoding.UTF8.GetBytes(
            "Synthetic MIT runtime notice for inert package tests.\n");
        modelNotice = Encoding.UTF8.GetBytes(
            "Synthetic MIT model notice for inert package tests.\n");
        File.WriteAllBytes(ModelPath, model);
        File.WriteAllBytes(RuntimeNoticePath, runtimeNotice);
        File.WriteAllBytes(ModelNoticePath, modelNotice);
        WriteArchive(variant);

        var archiveBytes = new FileInfo(ArchivePath).Length;
        var archiveSha256 = Hash(File.ReadAllBytes(ArchivePath));
        var modelSha256 = Hash(model);
        var maximumEntries = variant == ArchiveVariant.TooManyEntries
            ? 8
            : 16;
        var maximumExpanded = 4 * 1024 * 1024L;
        var downloadBytes = archiveBytes + model.Length;
        var json = $$"""
        {
          "format_version": 1,
          "kind": "local_stt_package_candidate",
          "id": "synthetic-whisper-base-en-import",
          "status": "disabled_pending_qualification",
          "target": "win-x64",
          "runtime": {
            "repository": "ggml-org/whisper.cpp",
            "revision": "{{RuntimeRevision}}",
            "release_tag": "v1.0",
            "release_id": 1,
            "archive_file_name": "whisper-bin-x64.zip",
            "asset_id": 2,
            "archive_bytes": {{archiveBytes}},
            "archive_sha256": "{{archiveSha256}}",
            "archive_sha256_evidence": "github_release_metadata",
            "archive_url": "https://github.com/ggml-org/whisper.cpp/releases/download/v1.0/whisper-bin-x64.zip",
            "file_layout_evidence": "source_workflow_unverified",
            "files": [
              { "purpose": "executable", "archive_entry": "bundle/whisper-cli.exe", "installed_name": "whisper-cli.exe" },
              { "purpose": "dependency", "archive_entry": "bundle/ggml.dll", "installed_name": "ggml.dll" }
            ]
          },
          "model": {
            "id": "ggml-base.en",
            "repository": "ggerganov/whisper.cpp",
            "revision": "{{ModelRevision}}",
            "file_name": "ggml-base.en.bin",
            "bytes": {{model.Length}},
            "sha256": "{{modelSha256}}",
            "sha256_evidence": "hugging_face_lfs_metadata",
            "source_url": "https://huggingface.co/ggerganov/whisper.cpp/resolve/{{ModelRevision}}/ggml-base.en.bin",
            "language": "en"
          },
          "execution": {
            "executable": "whisper-cli.exe",
            "input_format": "wav_pcm16_mono_16000",
            "input_mode": "ephemeral_file",
            "output_mode": "utf8_text_file",
            "network_policy": "no_network",
            "threads": 4,
            "processors": 1,
            "timeout_seconds": 30,
            "maximum_audio_bytes": 800044,
            "maximum_audio_seconds": 25,
            "maximum_transcript_bytes": 16384,
            "maximum_transcript_characters": 4096,
            "maximum_standard_output_bytes": 4096,
            "maximum_standard_error_bytes": 8192
          },
          "provisioning": {
            "maximum_archive_entries": {{maximumEntries}},
            "maximum_expanded_runtime_bytes": {{maximumExpanded}},
            "maximum_download_bytes": {{downloadBytes}},
            "maximum_staging_bytes": {{downloadBytes + maximumExpanded}},
            "allowed_origins": [ "github.com", "huggingface.co" ]
          },
          "acquisition": {
            "mode": "caller_supplied_offline_import_only",
            "automatic_download": false,
            "runtime_locator": "redirecting",
            "model_locator": "redirecting",
            "runtime_archive_content_hash": "complete",
            "model_content_hash": "complete",
            "runtime_file_hashes": "caller_supplied_required",
            "license_notices": "caller_supplied_required"
          },
          "rights": {
            "runtime_spdx": "MIT",
            "model_spdx": "MIT",
            "disposition": "unreviewed",
            "runtime_evidence_url": "https://github.com/ggml-org/whisper.cpp/blob/{{RuntimeRevision}}/LICENSE",
            "model_evidence_url": "https://github.com/openai/whisper/blob/{{ModelRevision}}/LICENSE"
          }
        }
        """;
        Manifest = LocalSttPackageManifest.Read(
            Encoding.UTF8.GetBytes(json));
        Evidence = new LocalSttImportEvidence
        {
            FormatVersion = 1,
            ManifestSha256 = Manifest.DocumentSha256,
            RuntimeRepository = "ggml-org/whisper.cpp",
            RuntimeRevision = RuntimeRevision,
            ArchiveSha256 = archiveSha256,
            ArchiveContentHashComplete = true,
            ModelRepository = "ggerganov/whisper.cpp",
            ModelRevision = ModelRevision,
            ModelSha256 = modelSha256,
            ModelContentHashComplete = true,
            RuntimeLocatorBehavior =
                LocalSttSourceLocatorBehavior.Redirecting,
            ModelLocatorBehavior =
                LocalSttSourceLocatorBehavior.Redirecting,
            RuntimeFileHashesComplete = true,
            LicenseNoticesComplete = true,
            RuntimeFiles =
            [
                new()
                {
                    ArchiveEntry = "bundle/whisper-cli.exe",
                    InstalledName = "whisper-cli.exe",
                    Bytes = executable.Length,
                    Sha256 = Hash(executable)
                },
                new()
                {
                    ArchiveEntry = "bundle/ggml.dll",
                    InstalledName = "ggml.dll",
                    Bytes = dependency.Length,
                    Sha256 = Hash(dependency)
                }
            ],
            LicenseNotices =
            [
                new()
                {
                    FileName = "Runtime-LICENSE.txt",
                    Component = "whisper.cpp runtime",
                    Spdx = "MIT",
                    SourceRevision = RuntimeRevision,
                    EvidenceUrl =
                        $"https://github.com/ggml-org/whisper.cpp/blob/{RuntimeRevision}/LICENSE",
                    Bytes = runtimeNotice.Length,
                    Sha256 = Hash(runtimeNotice),
                    AppliesTo =
                    [
                        "runtime/whisper-cli.exe",
                        "runtime/ggml.dll"
                    ]
                },
                new()
                {
                    FileName = "Model-LICENSE.txt",
                    Component = "whisper model",
                    Spdx = "MIT",
                    SourceRevision = ModelRevision,
                    EvidenceUrl =
                        $"https://github.com/openai/whisper/blob/{ModelRevision}/LICENSE",
                    Bytes = modelNotice.Length,
                    Sha256 = Hash(modelNotice),
                    AppliesTo = ["models/ggml-base.en.bin"]
                }
            ]
        };
    }

    internal LocalSttImportRequest Request(
        LocalSttImportEvidence? evidence = null,
        string? modelPath = null) => new(
            ArchivePath,
            modelPath ?? ModelPath,
            NoticeDirectory,
            Destination,
            evidence ?? Evidence);

    internal PhysicalLocalSttPackageImporter Importer(
        long availableBytes = long.MaxValue,
        Action<LocalSttImportIoPoint, CancellationToken>? io = null) =>
        new(
            Manifest,
            new PhysicalLocalPathInspector(),
            _ => availableBytes)
        {
            Io = io
        };

    internal LocalSttSourceBuildFacts BuildFacts() => new()
    {
        FormatVersion = 1,
        ManifestSha256 = Manifest.DocumentSha256,
        Repository = "ggml-org/whisper.cpp",
        Revision = RuntimeRevision,
        SourceArchiveBytes = 4_096,
        SourceArchiveSha256 = new string('d', 64),
        SourceContentHashComplete = true,
        SourceTreeClean = true,
        Target = "win-x64",
        Architecture = "x64",
        Generator = "Visual Studio 18 2026",
        Compiler = Tool("msvc", "19.50.10000.0", '1'),
        Cmake = Tool("cmake", "4.1.0", '2'),
        WindowsSdk = Tool("windows-sdk", "10.0.26100.0", '3')
    };

    private static LocalSttToolchainComponentFacts Tool(
        string name,
        string version,
        char hash) => new()
        {
            Name = name,
            Version = version,
            Sha256 = new string(hash, 64),
            Observation = "caller-supplied inert test fact"
        };

    private void WriteArchive(ArchiveVariant variant)
    {
        using var output = new FileStream(
            ArchivePath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None);
        using var zip = new ZipArchive(output, ZipArchiveMode.Create);
        WriteEntry(zip, "bundle/whisper-cli.exe", executable,
            CompressionLevel.NoCompression);
        WriteEntry(zip, "bundle/ggml.dll", dependency,
            CompressionLevel.NoCompression);
        switch (variant)
        {
            case ArchiveVariant.Traversal:
                WriteEntry(zip, "../escape.bin", [1, 2, 3],
                    CompressionLevel.NoCompression);
                break;
            case ArchiveVariant.CaseCollision:
                WriteEntry(zip, "extra/File.bin", [1, 2, 3],
                    CompressionLevel.NoCompression);
                WriteEntry(zip, "extra/file.bin", [4, 5, 6],
                    CompressionLevel.NoCompression);
                break;
            case ArchiveVariant.Symlink:
            {
                var entry = WriteEntry(zip, "extra/link.bin", [1, 2, 3],
                    CompressionLevel.NoCompression);
                entry.ExternalAttributes = (0xA000 | 0x1ff) << 16;
                break;
            }
            case ArchiveVariant.Device:
            {
                var entry = WriteEntry(zip, "extra/device.bin", [1, 2, 3],
                    CompressionLevel.NoCompression);
                entry.ExternalAttributes = (int)FileAttributes.Device;
                break;
            }
            case ArchiveVariant.ZipBomb:
                WriteEntry(zip, "extra/bomb.bin", new byte[1_048_576],
                    CompressionLevel.SmallestSize);
                break;
            case ArchiveVariant.TooManyEntries:
                for (var index = 0; index < 9; index++)
                    WriteEntry(
                        zip,
                        $"extra/file-{index}.bin",
                        Enumerable.Repeat((byte)(index + 1), 256).ToArray(),
                        CompressionLevel.NoCompression);
                break;
        }
    }

    private static ZipArchiveEntry WriteEntry(
        ZipArchive zip,
        string name,
        byte[] bytes,
        CompressionLevel compression)
    {
        var entry = zip.CreateEntry(name, compression);
        entry.LastWriteTime = new DateTimeOffset(
            1980,
            1,
            1,
            0,
            0,
            0,
            TimeSpan.Zero);
        using var output = entry.Open();
        output.Write(bytes);
        return entry;
    }

    internal static byte[] Pe(
        bool dynamicLibrary,
        ushort machine,
        byte seed)
    {
        var bytes = new byte[1024];
        bytes.AsSpan(512).Fill(seed);
        bytes[0] = (byte)'M';
        bytes[1] = (byte)'Z';
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0x3c), 0x80);
        "PE\0\0"u8.CopyTo(bytes.AsSpan(0x80));
        BinaryPrimitives.WriteUInt16LittleEndian(
            bytes.AsSpan(0x84),
            machine);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0x86), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0x94), 0xf0);
        BinaryPrimitives.WriteUInt16LittleEndian(
            bytes.AsSpan(0x96),
            (ushort)(0x0002 | (dynamicLibrary ? 0x2000 : 0)));
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0x98), 0x020b);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x98 + 60), 512);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x98 + 108), 16);
        var section = 0x98 + 0xf0;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(section + 8), 512);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(section + 12), 0x1000);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(section + 16), 512);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(section + 20), 512);
        return bytes;
    }

    private static byte[] Model(bool validHeader)
    {
        var bytes = Enumerable.Repeat((byte)0x41, 512).ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(
            bytes,
            validHeader ? 0x67676d6cU : 0x11111111U);
        var values = new[]
        {
            51_864,
            1_500,
            512,
            8,
            6,
            448,
            512,
            8,
            6,
            80,
            1
        };
        for (var index = 0; index < values.Length; index++)
            BinaryPrimitives.WriteInt32LittleEndian(
                bytes.AsSpan(4 + index * sizeof(int)),
                values[index]);
        return bytes;
    }

    private static string Hash(byte[] bytes) =>
        Convert.ToHexStringLower(SHA256.HashData(bytes));

    public void Dispose()
    {
        if (Directory.Exists(Root))
            Directory.Delete(Root, recursive: true);
    }
}
