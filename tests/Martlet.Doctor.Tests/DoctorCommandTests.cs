using System.Text;
using System.Text.Json;
using Martlet.Core.Contracts;
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

    [Theory]
    [InlineData("list")]
    [InlineData("run", "application.version", "runtime.version")]
    [InlineData("run", "audio.input", "audio.playback", "host.connection")]
    public async Task SelectionAndCatalogNeverImplyUnrequestedEvidence(params string[] command)
    {
        using var output = new StringWriter();
        var code = await DoctorCommand.RunAsync([.. command, "--data-directory", path, "--json"], output);
        var report = ContractJson.Read<DoctorReport>(Encoding.UTF8.GetBytes(output.ToString()));
        Assert.Equal(command is ["run", "application.version", "runtime.version"] ? 0 : 2, code);
        Assert.Null(report.SettingsState);
        Assert.Null(report.ProfileId);
        Assert.False(Directory.Exists(path));
        if (command[0] == "run")
            Assert.Equal(command.Skip(1), report.Probes.Select(probe => probe.Id));
        else
            Assert.All(report.Probes, probe =>
            {
                Assert.Equal(ProbeExecution.NotRun, probe.Execution);
                Assert.Equal(EvidenceProvenance.NotRun, probe.Provenance);
                Assert.Null(probe.ObservedAt);
            });
    }

    [Theory]
    [InlineData("run")]
    [InlineData("run", "application.version", "application.version")]
    [InlineData("run", "Application.Version")]
    [InlineData("run", "PRIVATE-CANARY")]
    [InlineData("list", "application.version")]
    [InlineData("run", "application.version", "--json")]
    public async Task InvalidSelectionsReturnOneSanitizedErrorReport(params string[] command)
    {
        using var output = new StringWriter();
        Assert.Equal(3, await DoctorCommand.RunAsync([.. command, "--json", "--data-directory", path], output));
        var report = ContractJson.Read<DoctorReport>(Encoding.UTF8.GetBytes(output.ToString()));
        Assert.Empty(report.Probes);
        Assert.Equal("cli.help", report.InvocationError!.ActionId);
        Assert.DoesNotContain("PRIVATE-CANARY", output.ToString());
        Assert.DoesNotContain(path, output.ToString());
        Assert.False(Directory.Exists(path));
    }

    [Fact]
    public async Task ExactProductionJsonShapeAndUiModelShareRegistryResults()
    {
        var clock = new FrozenClock();
        var service = new FoundationStatusService(new SettingsStore(path), clock);
        var model = new DiagnosticStatusModel(service.Executor);
        Assert.True(await model.RefreshAsync());
        using var output = new StringWriter();
        Assert.Equal(model.Report.ExitCode, await DoctorCommand.RunAsync(["--json", "--data-directory", path], output,
            executor: new FoundationStatusService(new SettingsStore(path), clock).Executor));
        var report = ContractJson.Read<DoctorReport>(Encoding.UTF8.GetBytes(output.ToString()));
        Assert.Equal(ContractJson.Write(model.Report), ContractJson.Write(report));
        Assert.Equal(model.Text, ReportFormatter.Human(report));
        using var document = JsonDocument.Parse(output.ToString());
        Assert.Equal(new[] { "application_version", "completed_at", "created_at", "exit_code", "probes", "ready", "settings_state", "started_at", "version" },
            document.RootElement.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        var probe = document.RootElement.GetProperty("probes")[0];
        Assert.Equal(new[]
        {
            "action_id", "age_milliseconds", "completed_at", "diagnostic_code", "duration_milliseconds", "effects", "execution",
            "freshness", "id", "maximum_age_milliseconds", "observed_at", "operation_still_running", "outcome", "provenance",
            "remedy", "required", "stage", "started_at", "summary"
        }, probe.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal("settings.first_run", probe.GetProperty("diagnostic_code").GetString());
        Assert.Equal("settings.create", probe.GetProperty("action_id").GetString());
        Assert.Equal(DiagnosticCatalog.Remedy("settings.create").Guidance, probe.GetProperty("remedy").GetString());
        Assert.Equal("completed", probe.GetProperty("execution").GetString());
    }

    [Fact]
    public async Task InjectedFaultAndCallerCancellationUseActualCommandFormattingAndExits()
    {
        var registry = new ProbeRegistry(
        [
            new("test.fault", Stage.Application, true, [ProbeEffect.LocalReadOnly],
                _ => throw new IOException("PRIVATE-CANARY"))
        ]);
        using var output = new StringWriter();
        Assert.Equal(1, await DoctorCommand.RunAsync(["--json", "--data-directory", path], output, executor: new(registry)));
        Assert.DoesNotContain("PRIVATE-CANARY", output.ToString());
        var failed = ContractJson.Read<DoctorReport>(Encoding.UTF8.GetBytes(output.ToString()));
        Assert.Equal("probe.fault", failed.Probes[0].DiagnosticCode);
        Assert.Equal("diagnostics.report", failed.Probes[0].ActionId);
        using var canceledOutput = new StringWriter();
        Assert.Equal(2, await DoctorCommand.RunAsync(["--json", "--data-directory", path], canceledOutput,
            new CancellationToken(true), new(registry)));
        var canceled = ContractJson.Read<DoctorReport>(Encoding.UTF8.GetBytes(canceledOutput.ToString()));
        Assert.Equal("probe.canceled", canceled.Probes[0].DiagnosticCode);
        Assert.Equal(ProbeExecution.Canceled, canceled.Probes[0].Execution);
        Assert.Null(canceled.Probes[0].StartedAt);
    }

    [Fact]
    public async Task InaccessibleSettingsRemainExitThreeAndAreNotReplaced()
    {
        Directory.CreateDirectory(path);
        Directory.CreateDirectory(Path.Combine(path, "settings.json"));
        using var output = new StringWriter();
        Assert.Equal(3, await DoctorCommand.RunAsync(["run", "settings.load", "--json", "--data-directory", path], output));
        var report = ContractJson.Read<DoctorReport>(Encoding.UTF8.GetBytes(output.ToString()));
        Assert.Equal(SettingsLoadState.Inaccessible, report.SettingsState);
        Assert.Equal("settings.inaccessible", report.Probes[0].DiagnosticCode);
        Assert.Equal("settings.check_access", report.Probes[0].ActionId);
        Assert.True(Directory.Exists(Path.Combine(path, "settings.json")));
        Assert.DoesNotContain(path, output.ToString());
    }

    private sealed class FrozenClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 9, 13, 0, 0, 0, TimeSpan.Zero);
        public override long GetTimestamp() => 0;
    }

    public void Dispose()
    {
        if (Directory.Exists(path))
            Directory.Delete(path, recursive: true);
    }
}
