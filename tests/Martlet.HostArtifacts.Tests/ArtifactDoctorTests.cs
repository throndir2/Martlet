using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Martlet.ArtifactDoctor;
using static Martlet.HostArtifacts.Tests.SyntheticManifests;

namespace Martlet.HostArtifacts.Tests;

public sealed class ArtifactDoctorTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "Martlet.HostArtifacts.Tests", Guid.NewGuid().ToString("N"));
    private string Input => Path.Combine(root, "candidate.json");

    private async Task CreateInput()
    {
        Directory.CreateDirectory(root);
        await File.WriteAllBytesAsync(Input, CandidateBytes());
        await File.WriteAllTextAsync(Path.Combine(root, "settings.json"), "PRIVATE-CANARY");
        await File.WriteAllTextAsync(Path.Combine(root, "model.safetensors"), "DO-NOT-OPEN");
    }

    [Theory]
    [InlineData("--help", "Usage:")]
    [InlineData("-h", "Usage:")]
    [InlineData("--version", "metadata-only")]
    public async Task InformationOnlyCommandsNeverCreateFiles(string argument, string expected)
    {
        using var output = new StringWriter();
        Assert.Equal(0, await ArtifactDoctorCommand.RunAsync([argument], output));
        Assert.Contains(expected, output.ToString());
        Assert.False(Directory.Exists(root));
    }

    [Theory]
    [InlineData(null, 2)]
    [InlineData("ubuntu-24.04-x64", 2)]
    [InlineData("windows-11-x64", 1)]
    public async Task ActualCommandReadsOneDocumentAndPreservesSentinels(string? target, int expected)
    {
        await CreateInput();
        var before = Directory.GetFiles(root).ToDictionary(p => p, File.ReadAllBytes);
        using var output = new StringWriter();
        string[] args = target is null ? ["inspect", "--manifest", Input, "--json"] :
            ["inspect", "--manifest", Input, "--target", target, "--json"];
        Assert.Equal(expected, await ArtifactDoctorCommand.RunAsync(args, output));
        using var json = JsonDocument.Parse(output.ToString());
        Assert.Equal(expected, json.RootElement.GetProperty("exit_code").GetInt32());
        Assert.False(json.RootElement.GetProperty("execution_eligible").GetBoolean());
        Assert.Equal(2_836_353_046L, json.RootElement.GetProperty("disk").GetProperty("known_listed_payload_bytes").GetInt64());
        Assert.DoesNotContain(root, output.ToString());
        Assert.DoesNotContain("PRIVATE-CANARY", output.ToString());
        Assert.DoesNotContain("DO-NOT-OPEN", output.ToString());
        Assert.Equal(before.Keys.Order(), Directory.GetFiles(root).Order());
        foreach (var pair in before) Assert.Equal(pair.Value, File.ReadAllBytes(pair.Key));
    }

    [Theory]
    [InlineData("")]
    [InlineData("inspect")]
    [InlineData("status")]
    [InlineData("install")]
    [InlineData("download")]
    [InlineData("--profile")]
    [InlineData("--manifest")]
    [InlineData("PRIVATE-CANARY")]
    public async Task InvalidCommandsAreSanitized(string argument)
    {
        using var output = new StringWriter();
        Assert.Equal(3, await ArtifactDoctorCommand.RunAsync([argument, "--json"], output));
        using var json = JsonDocument.Parse(output.ToString());
        Assert.Equal("inspection.invalid_invocation", json.RootElement.GetProperty("findings")[0].GetProperty("code").GetString());
        Assert.DoesNotContain("PRIVATE-CANARY", output.ToString());
        Assert.False(Directory.Exists(root));
    }

    [Theory]
    [InlineData("--json")]
    [InlineData("--role")]
    [InlineData("--target")]
    [InlineData("--manifest")]
    public async Task DuplicateOptionsCannotChangeInterpretation(string option)
    {
        using var output = new StringWriter();
        string[] args = option == "--json" ? ["inspect", "--manifest", Input, "--json", "--json"] :
            ["inspect", "--manifest", Input, option, "PRIVATE-CANARY", option, "PRIVATE-CANARY", "--json"];
        Assert.Equal(3, await ArtifactDoctorCommand.RunAsync(args, output));
        Assert.DoesNotContain("PRIVATE-CANARY", output.ToString());
        Assert.False(Directory.Exists(root));
    }

    [Theory]
    [InlineData("relative.json")]
    [InlineData("https://example.com/PRIVATE-CANARY.json")]
    [InlineData("\\\\localhost\\PRIVATE-CANARY.json")]
    [InlineData("\\\\?\\C:\\PRIVATE-CANARY.json")]
    [InlineData("C:\\PRIVATE-CANARY.json:stream")]
    public async Task DisallowedPathsAreRejectedWithoutReading(string path)
    {
        using var output = new StringWriter();
        Assert.Equal(3, await ArtifactDoctorCommand.RunAsync(["inspect", "--manifest", path, "--json"], output));
        Assert.DoesNotContain("PRIVATE-CANARY", output.ToString());
        Assert.False(Directory.Exists(root));
    }

    [Theory]
    [InlineData("{PRIVATE-CANARY", "manifest.invalid_json")]
    [InlineData("{\"format_version\":99,\"PRIVATE-CANARY\":true}", "manifest.unsupported_version")]
    [InlineData("{\"format_version\":1,\"secret\":\"PRIVATE-CANARY\\uD800\"}", "manifest.invalid_json")]
    public async Task MalformedDocumentsAreNotModifiedOrEchoed(string contents, string code)
    {
        await CreateInput();
        await File.WriteAllTextAsync(Input, contents);
        using var output = new StringWriter();
        Assert.Equal(3, await ArtifactDoctorCommand.RunAsync(["inspect", "--manifest", Input, "--json"], output));
        using var json = JsonDocument.Parse(output.ToString());
        Assert.Equal(code, json.RootElement.GetProperty("findings")[0].GetProperty("code").GetString());
        Assert.Equal(contents, await File.ReadAllTextAsync(Input));
        Assert.DoesNotContain("PRIVATE-CANARY", output.ToString());
        Assert.DoesNotContain(root, output.ToString());
    }

    [Fact]
    public async Task OversizedInaccessibleDirectoryAndCanceledInputsRemainNonReady()
    {
        using var missing = new StringWriter();
        Assert.Equal(3, await ArtifactDoctorCommand.RunAsync(["inspect", "--manifest", Input, "--json"], missing));
        Assert.False(Directory.Exists(root));
        await CreateInput();
        await File.WriteAllBytesAsync(Input, new byte[262_145]);
        using var oversized = new StringWriter();
        Assert.Equal(3, await ArtifactDoctorCommand.RunAsync(["inspect", "--manifest", Input, "--json"], oversized));
        Assert.Contains("manifest.too_large", oversized.ToString());
        using var directory = new StringWriter();
        Assert.Equal(3, await ArtifactDoctorCommand.RunAsync(["inspect", "--manifest", root, "--json"], directory));
        using var canceled = new StringWriter();
        Assert.Equal(2, await ArtifactDoctorCommand.RunAsync(["inspect", "--manifest", Input, "--json"], canceled, new(true)));
        Assert.Contains("inspection.canceled", canceled.ToString());
    }

    [Fact]
    public async Task ExclusiveWriterIsReportedWithoutReadingOrLeakingPath()
    {
        await CreateInput();
        using var writer = new FileStream(Input, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using var output = new StringWriter();
        Assert.Equal(3, await ArtifactDoctorCommand.RunAsync(["inspect", "--manifest", Input, "--json"], output));
        Assert.Contains("inspection.input_unreadable", output.ToString());
        Assert.DoesNotContain(root, output.ToString());
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    public async Task ActualExecutableReportsExitAndStableJson(bool invalid, bool container, bool catalog)
    {
        await CreateInput();
        if (container) await File.WriteAllBytesAsync(Input,
            await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "fixtures", "container-images.v2.json")));
        if (catalog) await File.WriteAllBytesAsync(Input,
            await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "host-artifacts.v2.json")));
        if (invalid) await File.WriteAllTextAsync(Input, "{\"format_version\":99,\"secret\":\"PRIVATE-CANARY\"}");
        var start = new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ??
                Path.Combine(Environment.GetEnvironmentVariable("DOTNET_ROOT") ?? throw new InvalidOperationException("Set DOTNET_ROOT to the local SDK."), "dotnet.exe"),
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = root, CreateNoWindow = true
        };
        start.ArgumentList.Add(typeof(ArtifactDoctorCommand).Assembly.Location);
        foreach (var argument in new[] { "inspect", "--manifest", Input, "--json" }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("The local CLI did not start.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            Assert.Equal(invalid ? 3 : 2, process.ExitCode);
            Assert.Equal("", await stderr);
            var text = await stdout;
            using var json = JsonDocument.Parse(text);
            Assert.Equal(process.ExitCode, json.RootElement.GetProperty("exit_code").GetInt32());
            Assert.False(json.RootElement.GetProperty("host_qualified").GetBoolean());
            if (container)
            {
                Assert.Equal(2, json.RootElement.GetProperty("format_version").GetInt32());
                Assert.Equal(catalog ? 16_879_012_039L : 660, json.RootElement.GetProperty("disk").GetProperty("known_listed_payload_bytes").GetInt64());
            }
            Assert.DoesNotContain("PRIVATE-CANARY", text);
            Assert.DoesNotContain(root, text);
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

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
