using Martlet.Core.Contracts;
using Martlet.Core.Settings;
using Martlet.Diagnostics;

namespace Martlet.Core.Tests;

public sealed class DiagnosticTests
{
    private static ProbeResult Passed => new()
    {
        Id = "test.probe", Stage = Stage.Application, Required = true, Outcome = ProbeOutcome.Passed,
        Provenance = EvidenceProvenance.Fixture, Freshness = EvidenceFreshness.Current,
        ObservedAt = DateTimeOffset.UtcNow, Summary = "Synthetic metadata-only contract check."
    };

    private static DoctorReport Report(ProbeResult[] probes) => new()
    {
        Version = ContractVersion.Current, ApplicationVersion = "0.1.0", CreatedAt = DateTimeOffset.UtcNow, Probes = probes
    };

    [Fact]
    public async Task ServiceConstructionAndFirstStatusUseNoAdaptersAndWriteNothing()
    {
        var path = Path.Combine(Path.GetTempPath(), "Martlet.Tests", Guid.NewGuid().ToString("N"));
        var report = await new FoundationStatusService(new SettingsStore(path)).GetReportAsync();
        Assert.False(report.Ready);
        Assert.Equal(2, report.ExitCode);
        Assert.Equal(SettingsLoadState.FirstRun, report.SettingsState);
        Assert.False(Directory.Exists(path));
        Assert.All(report.Probes.Where(p => p.Id is not ("settings.load" or "application.version" or "runtime.version" or "platform.architecture")),
            p => Assert.Equal(EvidenceProvenance.NotRun, p.Provenance));
        Assert.DoesNotContain(typeof(FoundationStatusService).Assembly.GetReferencedAssemblies(), a =>
            a.Name is "PresentationFramework" or "NAudio" or "System.Net.Http");
        Assert.DoesNotContain(typeof(SettingsStore).Assembly.GetReferencedAssemblies(), a =>
            a.Name is "PresentationFramework" or "NAudio" or "System.Net.Http");
        Assert.Contains("No AI conversation", ReportFormatter.Human(report));
        Assert.Equal(report.ExitCode, ContractJson.Read<DoctorReport>(ContractJson.Write(report)).ExitCode);
    }

    [Theory]
    [InlineData(ProbeOutcome.Warning)]
    [InlineData(ProbeOutcome.Unknown)]
    [InlineData(ProbeOutcome.Running)]
    [InlineData(ProbeOutcome.Skipped)]
    [InlineData(ProbeOutcome.NotConfigured)]
    public void IncompleteOutcomesAreNeverGreen(ProbeOutcome outcome)
    {
        var report = Report([Passed with { Outcome = outcome }]);
        report.Validate();
        Assert.Equal(2, report.ExitCode);
        Assert.False(report.Ready);
    }

    [Fact]
    public void ExitCodesDistinguishPassedFailedIncompleteAndInvalid()
    {
        Assert.Equal(0, Report([Passed]).ExitCode);
        Assert.Equal(2, Report([]).ExitCode);
        Assert.Equal(2, Report([Passed with { Required = false }]).ExitCode);
        Assert.Equal(2, Report([Passed with { Freshness = EvidenceFreshness.Stale }]).ExitCode);
        var error = new MartletError { Code = ErrorCode.ProviderFailed, Stage = Stage.Application, Retryable = false, Summary = "Failed.", ActionId = "test.retry" };
        var failed = Report([Passed with { Outcome = ProbeOutcome.Failed, Error = error }]);
        failed.Validate();
        Assert.Equal(1, failed.ExitCode);
        Assert.Equal(3, (failed with { SettingsState = SettingsLoadState.Invalid }).ExitCode);
        Assert.Equal(3, (failed with { InvocationError = error }).ExitCode);
    }

    [Theory]
    [InlineData(EvidenceProvenance.Unknown)]
    [InlineData(EvidenceProvenance.NotRun)]
    public void UnobservedPassIsInvalid(EvidenceProvenance provenance) =>
        Assert.Throws<ContractException>(() => (Passed with { Provenance = provenance, ObservedAt = null, Freshness = EvidenceFreshness.Unknown }).Validate());

    [Fact]
    public void ReportAndProbeBoundsAreEnforced()
    {
        Assert.Throws<ContractException>(() => Report(Enumerable.Repeat(Passed, 65).ToArray()).Validate());
        Assert.Throws<ContractException>(() => Report([Passed, Passed]).Validate());
        Assert.Throws<ContractException>(() => (Passed with { Summary = new string('x', 513) }).Validate());
        Assert.Throws<ContractException>(() => (Passed with { ObservedAt = null }).Validate());
        Assert.Throws<ContractException>(() => DoctorExitCodes.Evaluate(Report([Passed with { ObservedAt = null }])));
        Assert.Throws<ContractException>(() => (Report([Passed]) with { SettingsState = SettingsLoadState.Loaded }).Validate());
        Assert.Throws<ContractException>(() => (Passed with { AgeMilliseconds = 60_000, MaximumAgeMilliseconds = 60_000 }).Validate());
        Assert.Throws<ContractException>(() => (Passed with { Execution = ProbeExecution.NotRun }).Validate());
        Assert.Throws<ContractException>(() => (Passed with { Execution = ProbeExecution.Faulted }).Validate());
        Assert.Throws<ContractException>(() => (Passed with { OperationStillRunning = true }).Validate());
    }
}
