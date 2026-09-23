using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Martlet.LocalStt;

namespace Martlet.LocalStt.Tests;

public sealed class PackageVerifierTests
{
    [Fact]
    public async Task Exact_archive_entries_executable_dependencies_and_model_are_locked_for_launch()
    {
        using var fixture = new PackageFixture();
        var verifier = fixture.Verifier();

        var result = await verifier.VerifyForLaunchAsync(CancellationToken.None);

        Assert.Equal(PackageVerificationStatus.Verified, result.Status);
        var package = Assert.IsType<VerifiedLocalSttPackage>(result.Package);
        Assert.Equal(fixture.ExecutablePath, package.ExecutablePath);
        Assert.Equal(fixture.ModelPath, package.ModelPath);
        Assert.Equal(fixture.RuntimeDirectory, package.RuntimeDirectory);
        Assert.Equal(fixture.Manifest.DocumentSha256, package.ManifestSha256);
        await package.DisposeAsync();
    }

    [Theory]
    [InlineData("archive")]
    [InlineData("executable")]
    [InlineData("dependency")]
    [InlineData("model")]
    public async Task Changed_runtime_or_model_bytes_are_refused(string artifact)
    {
        using var fixture = new PackageFixture();
        File.AppendAllText(artifact switch
        {
            "archive" => fixture.ArchivePath,
            "executable" => fixture.ExecutablePath,
            "dependency" => fixture.DependencyPath,
            _ => fixture.ModelPath
        }, "changed");

        var result = await fixture.Verifier().VerifyForLaunchAsync(CancellationToken.None);

        Assert.Equal(PackageVerificationStatus.Changed, result.Status);
        Assert.Null(result.Package);
    }

    [Fact]
    public async Task Missing_or_extra_runtime_files_fail_closed()
    {
        using var missing = new PackageFixture();
        File.Delete(missing.DependencyPath);
        Assert.Equal(
            PackageVerificationStatus.Invalid,
            (await missing.Verifier().VerifyForLaunchAsync(CancellationToken.None)).Status);

        using var extra = new PackageFixture();
        File.WriteAllText(Path.Combine(extra.RuntimeDirectory, "unlisted.dll"), "unlisted");
        Assert.Equal(
            PackageVerificationStatus.Invalid,
            (await extra.Verifier().VerifyForLaunchAsync(CancellationToken.None)).Status);
    }

    [Fact]
    public async Task Reparse_and_access_denied_paths_have_distinct_safe_results()
    {
        using var fixture = new PackageFixture();
        var inspector = new RejectingPathInspector { RejectAsReparse = true };
        Assert.Equal(
            PackageVerificationStatus.UnsafePath,
            (await fixture.Verifier(inspector).VerifyForLaunchAsync(CancellationToken.None)).Status);

        inspector.RejectAsReparse = false;
        inspector.RejectAsAccessDenied = true;
        Assert.Equal(
            PackageVerificationStatus.AccessDenied,
            (await fixture.Verifier(inspector).VerifyForLaunchAsync(CancellationToken.None)).Status);
    }

    [Fact]
    public void Physical_path_policy_rejects_reparse_device_and_network_drive_metadata()
    {
        Assert.True(PhysicalLocalPathInspector.IsUnsafe(FileAttributes.ReparsePoint));
        Assert.True(PhysicalLocalPathInspector.IsUnsafe(FileAttributes.Device));
        Assert.False(PhysicalLocalPathInspector.IsUnsafe(FileAttributes.Normal));
        Assert.False(PhysicalLocalPathInspector.IsAcceptedDriveType(DriveType.Network));
        Assert.False(PhysicalLocalPathInspector.IsAcceptedDriveType(DriveType.CDRom));
        Assert.True(PhysicalLocalPathInspector.IsAcceptedDriveType(DriveType.Fixed));
    }

    [Fact]
    public void Package_root_syntax_rejects_unc_aliases_streams_and_drive_root()
    {
        Assert.Throws<LocalPathException>(() => LocalPathRules.NormalizeRoot(@"\\server\share\package"));
        Assert.Throws<LocalPathException>(() => LocalPathRules.NormalizeRoot(@"C:\"));
        Assert.Throws<LocalPathException>(() => LocalPathRules.NormalizeRoot(@"C:\package:stream"));
    }

    [Fact]
    public async Task Unsupported_host_and_precanceled_verification_never_open_artifacts()
    {
        using var fixture = new PackageFixture();
        var unsupported = new PhysicalLocalSttPackageVerifier(
            fixture.Root,
            fixture.Manifest,
            new PhysicalLocalPathInspector(),
            () => false);
        Assert.Equal(
            PackageVerificationStatus.UnsupportedHost,
            (await unsupported.VerifyForLaunchAsync(CancellationToken.None)).Status);

        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        Assert.Equal(
            PackageVerificationStatus.Canceled,
            (await fixture.Verifier().VerifyForLaunchAsync(cancel.Token)).Status);
    }

    [Fact]
    public async Task Ephemeral_workspace_owns_delete_on_close_audio_and_exact_transcript()
    {
        var root = Path.Combine(Path.GetTempPath(), "Martlet.LocalStt.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.GetDirectoryName(root)!);
        try
        {
            using var audio = CanonicalWaveAudio.FromWave(LocalSttTestData.Wave());
            var factory = new EphemeralLocalSttWorkspaceFactory(root, new PhysicalLocalPathInspector());
            var created = await factory.CreateAsync(Guid.NewGuid(), audio, CancellationToken.None);
            Assert.Equal(WorkspaceStatus.Ready, created.Status);
            var workspace = Assert.IsType<EphemeralLocalSttWorkspace>(created.Workspace);
            Assert.True(File.Exists(workspace.AudioPath));
            File.WriteAllText(workspace.TranscriptPrefixPath + ".txt", " private fixture text ");

            var transcript = await workspace.ReadTranscriptAsync(CancellationToken.None);
            Assert.Equal(WorkspaceStatus.Ready, transcript.Status);
            Assert.Equal(" private fixture text ", Encoding.UTF8.GetString(transcript.Bytes!));
            Assert.Equal(WorkspaceStatus.Ready, await workspace.CleanupAsync());
            Assert.False(File.Exists(workspace.AudioPath));
            Assert.False(Directory.Exists(workspace.WorkingDirectory));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Ephemeral_workspace_rejects_reparse_policy_before_writing_audio()
    {
        var root = Path.Combine(Path.GetTempPath(), "Martlet.LocalStt.Tests", Guid.NewGuid().ToString("N"));
        using var audio = CanonicalWaveAudio.FromWave(LocalSttTestData.Wave());
        var factory = new EphemeralLocalSttWorkspaceFactory(
            root,
            new RejectingPathInspector { RejectAsReparse = true });

        var created = await factory.CreateAsync(Guid.NewGuid(), audio, CancellationToken.None);

        Assert.Equal(WorkspaceStatus.UnsafePath, created.Status);
        Assert.Null(created.Workspace);
        Assert.False(Directory.Exists(Path.Combine(root, "input.wav")));
    }

    [Fact]
    public void Failed_workspace_creation_removes_an_already_written_private_audio_file()
    {
        var root = Path.Combine(Path.GetTempPath(), "Martlet.LocalStt.Tests", Guid.NewGuid().ToString("N"));
        var operation = Path.Combine(root, Guid.NewGuid().ToString("N"));
        var input = Path.Combine(operation, "input.wav");
        Directory.CreateDirectory(operation);
        try
        {
            File.WriteAllBytes(input, LocalSttTestData.Wave());
            var factory = new EphemeralLocalSttWorkspaceFactory(root, new PhysicalLocalPathInspector());

            Assert.True(factory.TryCleanupFailedCreation(null, input, operation));
            Assert.False(File.Exists(input));
            Assert.False(Directory.Exists(operation));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }
}

internal sealed class PackageFixture : IDisposable
{
    internal string Root { get; }
    internal string RuntimeDirectory { get; }
    internal string ArchivePath { get; }
    internal string ExecutablePath { get; }
    internal string DependencyPath { get; }
    internal string ModelPath { get; }
    internal LocalSttPackageManifest Manifest { get; }

    internal PackageFixture()
    {
        Root = Path.Combine(Path.GetTempPath(), "Martlet.LocalStt.PackageTests", Guid.NewGuid().ToString("N"));
        var downloads = Path.Combine(Root, "downloads");
        RuntimeDirectory = Path.Combine(Root, "runtime");
        var models = Path.Combine(Root, "models");
        Directory.CreateDirectory(downloads);
        Directory.CreateDirectory(RuntimeDirectory);
        Directory.CreateDirectory(models);

        var executable = Encoding.ASCII.GetBytes(string.Concat(
            Enumerable.Repeat("synthetic executable bytes", 4096)));
        var dependency = Encoding.ASCII.GetBytes(string.Concat(
            Enumerable.Repeat("synthetic dependency bytes", 2048)));
        var model = Encoding.ASCII.GetBytes("synthetic model bytes");
        ArchivePath = Path.Combine(downloads, "whisper-bin-x64.zip");
        using (var stream = new FileStream(ArchivePath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            WriteEntry(zip, "bundle/whisper-cli.exe", executable);
            WriteEntry(zip, "bundle/ggml.dll", dependency);
        }
        ExecutablePath = Path.Combine(RuntimeDirectory, "whisper-cli.exe");
        DependencyPath = Path.Combine(RuntimeDirectory, "ggml.dll");
        ModelPath = Path.Combine(models, "ggml-base.en.bin");
        File.WriteAllBytes(ExecutablePath, executable);
        File.WriteAllBytes(DependencyPath, dependency);
        File.WriteAllBytes(ModelPath, model);

        var archiveBytes = new FileInfo(ArchivePath).Length;
        var archiveSha = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(ArchivePath)));
        var modelSha = Convert.ToHexStringLower(SHA256.HashData(model));
        var downloadBytes = archiveBytes + model.Length;
        var expandedBytes = executable.Length + dependency.Length;
        var json = $$"""
        {
          "format_version": 1,
          "kind": "local_stt_package_candidate",
          "id": "synthetic-whisper-base-en",
          "status": "disabled_pending_qualification",
          "target": "win-x64",
          "runtime": {
            "repository": "ggml-org/whisper.cpp",
            "revision": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            "release_tag": "v1.0",
            "release_id": 1,
            "archive_file_name": "whisper-bin-x64.zip",
            "asset_id": 2,
            "archive_bytes": {{archiveBytes}},
            "archive_sha256": "{{archiveSha}}",
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
            "revision": "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            "file_name": "ggml-base.en.bin",
            "bytes": {{model.Length}},
            "sha256": "{{modelSha}}",
            "sha256_evidence": "hugging_face_lfs_metadata",
            "source_url": "https://huggingface.co/ggerganov/whisper.cpp/resolve/bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb/ggml-base.en.bin",
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
            "maximum_archive_entries": 8,
            "maximum_expanded_runtime_bytes": {{expandedBytes}},
            "maximum_download_bytes": {{downloadBytes}},
            "maximum_staging_bytes": {{downloadBytes + expandedBytes}},
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
            "runtime_evidence_url": "https://github.com/ggml-org/whisper.cpp/blob/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/LICENSE",
            "model_evidence_url": "https://github.com/openai/whisper/blob/bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb/LICENSE"
          }
        }
        """;
        Manifest = LocalSttPackageManifest.Read(Encoding.UTF8.GetBytes(json));
    }

    internal PhysicalLocalSttPackageVerifier Verifier(ILocalPathInspector? inspector = null) =>
        new(Root, Manifest, inspector ?? new PhysicalLocalPathInspector(), () => true);

    private static void WriteEntry(ZipArchive zip, string name, byte[] bytes)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.SmallestSize);
        using var stream = entry.Open();
        stream.Write(bytes);
    }

    public void Dispose()
    {
        if (Directory.Exists(Root))
            Directory.Delete(Root, recursive: true);
    }
}
