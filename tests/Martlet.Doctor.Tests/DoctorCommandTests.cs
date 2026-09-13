using System.Text;
using System.Text.Json;
using Martlet.Core.Settings;
using Martlet.Diagnostics;

namespace Martlet.Doctor.Tests;

public sealed class DoctorCommandTests : IDisposable
{
    private readonly string path = Path.Combine(Path.GetTempPath(), "Martlet.Doctor.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task JsonCommandUsesStableEnvelopeAndDoesNotInventReadiness()
    {
        using var output = new StringWriter();
        var code = await DoctorCommand.RunAsync(["status", "--data-directory", path, "--json"], output);
        using var json = JsonDocument.Parse(output.ToString());
        var root = json.RootElement;
        Assert.Equal(2, code);
        Assert.Equal(code, root.GetProperty("exit_code").GetInt32());
        Assert.False(root.GetProperty("ready").GetBoolean());
        Assert.Equal(1, root.GetProperty("version").GetProperty("major").GetInt32());
        Assert.Equal("first_run", root.GetProperty("settings_state").GetString());
        Assert.Equal("not_run", root.GetProperty("probes")[1].GetProperty("provenance").GetString());
        Assert.False(root.TryGetProperty("profile_id", out _));
        Assert.False(Directory.Exists(path));
        Assert.DoesNotContain(path, output.ToString());
    }

    [Fact]
    public async Task HumanAndJsonUseSameSharedReport()
    {
        var store = new SettingsStore(path);
        Assert.True((await store.SaveAsync(AppSettings.CreateUnconfigured(), null)).Saved);
        var shared = await new FoundationStatusService(store).GetReportAsync();
        using var human = new StringWriter();
        Assert.Equal(shared.ExitCode, await DoctorCommand.RunAsync(["--data-directory", path], human));
        foreach (var probe in shared.Probes)
            Assert.Contains(probe.Summary, human.ToString());
        Assert.Contains(shared.ProfileId!.ToString()!, human.ToString());
        Assert.Contains("not ready", human.ToString());
    }

    [Theory]
    [InlineData("self-test")]
    [InlineData("--profile")]
    [InlineData("--unknown=PRIVATE-CANARY")]
    [InlineData("--json")]
    public async Task InvalidInvocationIsSanitizedJson(string argument)
    {
        using var output = new StringWriter();
        var code = await DoctorCommand.RunAsync(["--json", argument], output);
        using var json = JsonDocument.Parse(output.ToString());
        Assert.Equal(3, code);
        Assert.Equal(3, json.RootElement.GetProperty("exit_code").GetInt32());
        Assert.True(json.RootElement.TryGetProperty("invocation_error", out _));
        Assert.DoesNotContain("PRIVATE-CANARY", output.ToString());
    }

    [Theory]
    [InlineData("--data-directory")]
    [InlineData("--data-directory", "relative")]
    [InlineData("--data-directory", "")]
    [InlineData("status", "status")]
    public async Task InvalidPathsAndDuplicateCommandsReturnThree(params string[] args)
    {
        using var output = new StringWriter();
        Assert.Equal(3, await DoctorCommand.RunAsync(args, output));
        Assert.Contains("Invalid command", output.ToString());
    }

    [Theory]
    [InlineData("{PRIVATE-CANARY", "settings_malformed")]
    [InlineData("{\"schema_version\":99}", "unsupported_version")]
    public async Task ConfigurationErrorsAreActionableAndNotLeaked(string contents, string expectedCode)
    {
        Directory.CreateDirectory(path);
        var file = Path.Combine(path, "settings.json");
        await File.WriteAllTextAsync(file, contents);
        using var output = new StringWriter();
        Assert.Equal(3, await DoctorCommand.RunAsync(["--json", "--data-directory", path], output));
        using var json = JsonDocument.Parse(output.ToString());
        var error = json.RootElement.GetProperty("probes")[0].GetProperty("error");
        Assert.Equal(expectedCode, error.GetProperty("code").GetString());
        Assert.DoesNotContain("PRIVATE-CANARY", output.ToString());
        Assert.Equal(contents, await File.ReadAllTextAsync(file));
    }

    [Theory]
    [InlineData("{\"\\uD800\":0}")]
    [InlineData("{\"\\uDC00\":0}")]
    [InlineData("{\"PRIVATE-CANARY\\uD800\":0}")]
    [InlineData("{\"nested\":{\"\\uD800\":0}}")]
    [InlineData("{\"unknown\":\"\\uD800\"}")]
    public async Task MalformedUnicodeReturnsSanitizedExitThreeJson(string json) =>
        await AssertMalformedEncodingReturnsJson(Encoding.UTF8.GetBytes(json));

    [Fact]
    public async Task MalformedUtf8PropertyNameReturnsSanitizedExitThreeJson() =>
        await AssertMalformedEncodingReturnsJson([(byte)'{', (byte)'"', 0xED, 0xA0, 0x80, (byte)'"', (byte)':', (byte)'0', (byte)'}']);

    private async Task AssertMalformedEncodingReturnsJson(byte[] bytes)
    {
        Directory.CreateDirectory(path);
        var file = Path.Combine(path, "settings.json");
        await File.WriteAllBytesAsync(file, bytes);
        using var output = new StringWriter();
        Assert.Equal(3, await DoctorCommand.RunAsync(["--json", "--data-directory", path], output));
        using var document = JsonDocument.Parse(output.ToString());
        var report = document.RootElement;
        Assert.Equal(3, report.GetProperty("exit_code").GetInt32());
        Assert.Equal("invalid", report.GetProperty("settings_state").GetString());
        Assert.Equal("settings_malformed", report.GetProperty("probes")[0].GetProperty("error").GetProperty("code").GetString());
        Assert.DoesNotContain("\\uD800", output.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PRIVATE-CANARY", output.ToString());
        Assert.Equal(bytes, await File.ReadAllBytesAsync(file));
    }

    [Theory]
    [InlineData("--help", "Usage:")]
    [InlineData("--version", "foundation")]
    public async Task InformationalCommandsSucceedWithoutLoadingSettings(string option, string expected)
    {
        using var output = new StringWriter();
        Assert.Equal(0, await DoctorCommand.RunAsync([option], output));
        Assert.Contains(expected, output.ToString());
    }

    public void Dispose()
    {
        if (Directory.Exists(path))
            Directory.Delete(path, recursive: true);
    }
}
