using System.Diagnostics;
using System.Reflection;
using System.Text.Json;

namespace Martlet.LocalStt.Tests;

public sealed class PackageToolProcessTests
{
    [Theory]
    [InlineData("--help", 0)]
    [InlineData("help", 0)]
    [InlineData("unknown-command", 2)]
    public async Task Actual_CLI_passive_paths_do_not_create_files(string argument, int expected)
    {
        using var fixture = new ImportFixture();
        var before = Directory.GetFileSystemEntries(fixture.Root, "*", SearchOption.AllDirectories).Order().ToArray();
        var result = await Run(fixture.Root, argument);
        Assert.Equal(expected, result.ExitCode);
        Assert.Contains("never downloads", result.Output + result.Error);
        Assert.Equal(before, Directory.GetFileSystemEntries(fixture.Root, "*", SearchOption.AllDirectories).Order().ToArray());
    }

    [Fact]
    public async Task Actual_CLI_import_requires_explicit_inputs_and_rejects_synthetic_pins()
    {
        using var fixture = new ImportFixture();
        var missing = await Run(fixture.Root, "import");
        Assert.Equal(2, missing.ExitCode);
        Assert.Contains("Missing required option", missing.Error);
        var evidence = Path.Combine(fixture.Root, "evidence.json");
        await File.WriteAllBytesAsync(evidence, fixture.Evidence.ToCanonicalJson());
        var result = await Run(fixture.Root, "import", "--archive", fixture.ArchivePath,
            "--model", fixture.ModelPath, "--notices", fixture.NoticeDirectory,
            "--evidence", evidence, "--destination", fixture.Destination, "--approve-rights",
            "--approve-plan", new string('a', 64));
        Assert.Equal(4, result.ExitCode);
        Assert.Contains("InvalidRequest", result.Error);
        Assert.False(Directory.Exists(fixture.Destination));
        Assert.Empty(Directory.GetDirectories(fixture.Root, "*.pending"));
    }

    [Fact]
    public async Task Actual_CLI_inspect_does_not_accept_synthetic_receipts_as_current_manifest()
    {
        using var fixture = new ImportFixture();
        var importer = fixture.Importer();
        var plan = importer.Preview(fixture.Request());
        importer.Import(plan, plan.Authorize(LocalSttRightsDecision.ApproveExactRuntimeModelAndNoticeRights));
        var result = await Run(fixture.Root, "inspect", "--destination", fixture.Destination);
        Assert.Equal(4, result.ExitCode);
        Assert.Contains("InvalidReceipt", result.Error);
    }

    [Fact]
    public async Task Actual_CLI_build_plan_only_outputs_hash_bound_data()
    {
        using var fixture = new ImportFixture();
        var current = LocalSttPackageManifest.Current;
        var facts = fixture.BuildFacts() with
        {
            ManifestSha256 = current.DocumentSha256,
            Repository = current.Document.Runtime.Repository,
            Revision = current.Document.Runtime.Revision
        };
        var path = Path.Combine(fixture.Root, "facts.json");
        await File.WriteAllBytesAsync(path, facts.ToCanonicalJson());
        var before = Directory.GetFileSystemEntries(fixture.Root, "*", SearchOption.AllDirectories).Order().ToArray();
        var result = await Run(fixture.Root, "build-plan", "--facts", path);
        Assert.Equal(0, result.ExitCode);
        using var document = JsonDocument.Parse(result.Output);
        Assert.False(document.RootElement.GetProperty("compiler_execution_allowed_by_default").GetBoolean());
        Assert.False(document.RootElement.GetProperty("network_allowed").GetBoolean());
        Assert.Equal(64, document.RootElement.GetProperty("plan_sha256").GetString()!.Length);
        Assert.Equal(before, Directory.GetFileSystemEntries(fixture.Root, "*", SearchOption.AllDirectories).Order().ToArray());
    }

    private static async Task<(int ExitCode, string Output, string Error)> Run(string workingDirectory, params string[] args)
    {
        var sdk = Environment.GetEnvironmentVariable("DOTNET_ROOT")
            ?? throw new InvalidOperationException("Set DOTNET_ROOT to the local pinned SDK.");
        var tool = Environment.GetEnvironmentVariable("MARTLET_PACKAGE_TOOL")
            ?? typeof(PackageToolProcessTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                .FirstOrDefault(attribute => attribute.Key == "MartletPackageTool")?.Value
            ?? throw new InvalidOperationException("Build PackageTool locally and set MARTLET_PACKAGE_TOOL to its DLL.");
        tool = Path.GetFullPath(tool);
        Assert.True(File.Exists(tool), $"PackageTool output is missing: {tool}");
        var start = new ProcessStartInfo(Path.Combine(sdk, "dotnet.exe"))
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory
        };
        start.ArgumentList.Add(tool);
        foreach (var argument in args) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await process.WaitForExitAsync(deadline.Token);
            return (process.ExitCode, await output, await error);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
    }
}
