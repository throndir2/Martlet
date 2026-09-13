using Martlet.Core.Contracts;
using Martlet.Core.Settings;
using Martlet.Diagnostics;

namespace Martlet.Core.Tests;

public sealed class ProbeExecutorTests
{
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static TaskCompletionSource<ProbeObservation> Observation() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static ProbeObservation Fixture(DateTimeOffset? at = null) => new("fixture.passed", EvidenceProvenance.Fixture, at);
    private static ProbeDefinition Definition(Func<CancellationToken, Task<ProbeObservation>> execute, string id = "test.local",
        bool required = true) => new(id, Stage.Application, required, [ProbeEffect.LocalReadOnly], execute, timeout: TimeSpan.FromSeconds(1));
    private static async Task<T> Bounded<T>(Task<T> task) => await task.WaitAsync(TimeSpan.FromSeconds(5));

    [Fact]
    public async Task CatalogIsNotEvidenceAndPermissionedCallbacksCannotRun()
    {
        var called = false;
        var definition = new ProbeDefinition("test.device", Stage.Application, true,
            [ProbeEffect.Device, ProbeEffect.Permissioned], _ => { called = true; throw new InvalidOperationException("PRIVATE-CANARY"); });
        var executor = new ProbeExecutor(new([definition]), new ManualClock());
        var catalog = executor.Catalog();
        Assert.Equal(2, catalog.ExitCode);
        Assert.Equal("probe.not_run", catalog.Probes[0].DiagnosticCode);
        Assert.Null(catalog.Probes[0].StartedAt);
        var report = await executor.RunAsync();
        Assert.False(called);
        Assert.Equal("probe.effects_blocked", report.Probes[0].DiagnosticCode);
        Assert.Equal(EvidenceProvenance.NotRun, report.Probes[0].Provenance);
        Assert.Equal(2, report.ExitCode);
    }

    [Fact]
    public void DuplicateUnknownEmptyAndOversizedSelectionAreRejected()
    {
        var definition = Definition(_ => Task.FromResult(Fixture()));
        Assert.Throws<ArgumentException>(() => new ProbeRegistry([definition, definition]));
        var registry = new ProbeRegistry([definition]);
        Assert.Throws<ArgumentException>(() => registry.Select([]));
        Assert.Throws<ArgumentException>(() => registry.Select(["test.local", "test.local"]));
        Assert.Throws<ArgumentException>(() => registry.Select(["TEST.local"]));
        Assert.Throws<ArgumentException>(() => registry.Select(Enumerable.Repeat("test.local", 65)));
        Assert.Throws<ArgumentException>(() => new ProbeRegistry(Enumerable.Range(0, 65).Select(i => Definition(_ => Task.FromResult(Fixture()), $"test.{i}"))));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ProbeExecutor(registry, maximumConcurrency: 9));
    }

    [Fact]
    public async Task TimeoutKeepsActualLifetimeAndDiscardsLatePassAndBlocksRerun()
    {
        var clock = new ManualClock();
        var started = Signal();
        var finish = Observation();
        var canceled = Signal();
        var calls = 0;
        var executor = new ProbeExecutor(new([Definition(token =>
        {
            Interlocked.Increment(ref calls);
            token.Register(() => canceled.TrySetResult());
            started.SetResult();
            return finish.Task; // Deliberately ignores cancellation until this test releases it.
        })]), clock);
        var run = executor.RunAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, executor.ActiveOperationCount);
        clock.Advance(TimeSpan.FromSeconds(1));
        var report = await Bounded(run);
        await canceled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var result = Assert.Single(report.Probes);
        Assert.Equal(2, report.ExitCode);
        Assert.Equal("probe.timeout", result.DiagnosticCode);
        Assert.Equal(ProbeExecution.TimedOut, result.Execution);
        Assert.Equal(1000, result.DurationMilliseconds);
        Assert.True(result.OperationStillRunning);
        Assert.Null(result.CompletedAt);
        Assert.Null(result.ObservedAt);
        Assert.Equal(1, executor.ActiveOperationCount);
        Assert.Equal("probe.busy", (await executor.RunAsync()).Probes[0].DiagnosticCode);
        Assert.Equal(1, calls);
        finish.SetResult(Fixture());
        Assert.True(await executor.WaitForIdleAsync(TimeSpan.FromSeconds(1)));
        Assert.Equal("probe.timeout", report.Probes[0].DiagnosticCode);
        Assert.False(executor.RefreshAge(report).Ready);
    }

    [Fact]
    public async Task CallerCancellationIsNotTimeoutOrFailureAndSpontaneousCancelIsUnknown()
    {
        var started = Signal();
        using var caller = new CancellationTokenSource();
        var executor = new ProbeExecutor(new([Definition(async token =>
        {
            started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Fixture();
        })]), new ManualClock());
        var run = executor.RunAsync(cancellationToken: caller.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        caller.Cancel();
        var report = await Bounded(run);
        Assert.Equal(2, report.ExitCode);
        Assert.Equal("probe.canceled", report.Probes[0].DiagnosticCode);
        Assert.Equal(ProbeExecution.Canceled, report.Probes[0].Execution);
        Assert.True(await executor.WaitForIdleAsync(TimeSpan.FromSeconds(1)));
        var spontaneous = new ProbeExecutor(new([Definition(_ => Task.FromCanceled<ProbeObservation>(new CancellationToken(true)))]));
        Assert.Equal("probe.unknown", (await spontaneous.RunAsync()).Probes[0].DiagnosticCode);
    }

    [Fact]
    public async Task BoundedConcurrencyIncludesNonCooperativeCallbacksAndWholeRunDeadline()
    {
        var clock = new ManualClock();
        var bothStarted = Signal();
        var finish = Observation();
        var count = 0;
        var executor = new ProbeExecutor(new(Enumerable.Range(0, 4).Select(index => Definition(_ =>
        {
            if (Interlocked.Increment(ref count) == 2)
                bothStarted.SetResult();
            return finish.Task;
        }, $"test.{index}"))), clock, maximumConcurrency: 2, runTimeout: TimeSpan.FromMilliseconds(500));
        var run = executor.RunAsync();
        await bothStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<InvalidOperationException>(() => executor.RunAsync());
        clock.Advance(TimeSpan.FromMilliseconds(500));
        var report = await Bounded(run);
        Assert.Equal(2, count);
        Assert.Equal(2, executor.ActiveOperationCount);
        Assert.All(report.Probes, result => Assert.Equal("probe.timeout", result.DiagnosticCode));
        Assert.Null(report.Probes[2].StartedAt);
        finish.SetResult(Fixture());
        Assert.True(await executor.WaitForIdleAsync(TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task CancellationHandlersCannotHoldTheReportOpen()
    {
        var started = Signal();
        var handlerStarted = Signal();
        using var release = new ManualResetEventSlim();
        using var caller = new CancellationTokenSource();
        var executor = new ProbeExecutor(new([Definition(token =>
        {
            token.Register(() => { handlerStarted.SetResult(); release.Wait(); });
            started.SetResult();
            return Task.Delay(Timeout.InfiniteTimeSpan, token).ContinueWith(_ => Fixture(), TaskScheduler.Default);
        })]));
        try
        {
            var run = executor.RunAsync(cancellationToken: caller.Token);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            caller.Cancel();
            var report = await Bounded(run);
            await handlerStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(2, report.ExitCode);
            Assert.True(report.Probes[0].OperationStillRunning);
            Assert.Equal(1, executor.ActiveOperationCount);
        }
        finally { release.Set(); }
        Assert.True(await executor.WaitForIdleAsync(TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task StaleFutureUnknownAndFixtureEvidenceCannotImplyLiveReadiness()
    {
        var clock = new ManualClock();
        var executor = new ProbeExecutor(new([Definition(_ => Task.FromResult(Fixture(clock.GetUtcNow())))]), clock);
        var report = await executor.RunAsync();
        Assert.Equal(0, report.ExitCode);
        Assert.Equal(EvidenceProvenance.Fixture, report.Probes[0].Provenance);
        clock.Advance(TimeSpan.FromMinutes(1));
        report = executor.RefreshAge(report);
        Assert.Equal(EvidenceFreshness.Stale, report.Probes[0].Freshness);
        Assert.Equal(60_000, report.Probes[0].AgeMilliseconds);
        Assert.Equal(2, report.ExitCode);
        clock.Advance(TimeSpan.FromSeconds(-30));
        Assert.Equal(EvidenceFreshness.Stale, executor.RefreshAge(report).Probes[0].Freshness);
        var future = new ProbeExecutor(new([Definition(_ => Task.FromResult(Fixture(clock.GetUtcNow().AddMinutes(1))))]), clock);
        Assert.Equal(EvidenceFreshness.Unknown, (await future.RunAsync()).Probes[0].Freshness);
        var unknown = new ProbeExecutor(new([Definition(_ => Task.FromResult(new ProbeObservation("probe.unknown", EvidenceProvenance.Unknown)))]), clock);
        Assert.Equal(2, (await unknown.RunAsync()).ExitCode);
        var optional = new ProbeExecutor(new([Definition(_ => Task.FromResult(Fixture()), required: false)]), clock);
        Assert.Equal(2, (await optional.RunAsync()).ExitCode);
        Assert.Equal(2, (await new ProbeExecutor(new([]), clock).RunAsync()).ExitCode);
    }

    [Fact]
    public async Task LateCompletionCannotBeatAnUnscheduledDeadlineTimer()
    {
        var clock = new ManualClock();
        var executor = new ProbeExecutor(new([Definition(_ =>
        {
            clock.Advance(TimeSpan.FromSeconds(1), fireTimers: false);
            return Task.FromResult(Fixture());
        })]), clock);
        var report = await executor.RunAsync();
        Assert.Equal("probe.timeout", report.Probes[0].DiagnosticCode);
        Assert.Equal(2, report.ExitCode);
        Assert.Null(report.Probes[0].ObservedAt);
        Assert.False(report.Probes[0].OperationStillRunning);
    }

    [Fact]
    public async Task WholeRunMonotonicDeadlineRejectsLatePassAndDoesNotScheduleMoreWork()
    {
        var clock = new ManualClock();
        var secondRan = false;
        var executor = new ProbeExecutor(new(
        [
            Definition(_ =>
            {
                clock.Advance(TimeSpan.FromMilliseconds(600), fireTimers: false);
                return Task.FromResult(Fixture());
            }, "test.first"),
            Definition(_ => { secondRan = true; return Task.FromResult(Fixture()); }, "test.second")
        ]), clock, maximumConcurrency: 1, runTimeout: TimeSpan.FromMilliseconds(500));
        var report = await executor.RunAsync();
        Assert.False(secondRan);
        Assert.Equal(2, report.ExitCode);
        Assert.All(report.Probes, probe => Assert.Equal("probe.timeout", probe.DiagnosticCode));
        Assert.Null(report.Probes[1].StartedAt);
    }

    [Theory]
    [InlineData("settings.valid", SettingsLoadState.FirstRun)]
    [InlineData("probe.fault", SettingsLoadState.Invalid)]
    [InlineData("settings.inaccessible", SettingsLoadState.FirstRun)]
    public async Task ContradictorySettingsEvidenceIsAProbeFailureNotGreenOrConfigurationFailure(string finding, SettingsLoadState state)
    {
        var executor = new ProbeExecutor(new(
        [
            new("settings.load", Stage.Settings, true, [ProbeEffect.LocalReadOnly], _ =>
                Task.FromResult(new ProbeObservation(finding, EvidenceProvenance.Live,
                    Settings: new(state, null, null, null))))
        ]));
        var report = await executor.RunAsync();
        Assert.Equal(1, report.ExitCode);
        Assert.Null(report.SettingsState);
        Assert.Equal("probe.invalid_evidence", report.Probes[0].DiagnosticCode);
        Assert.False(ContractJson.Read<DoctorReport>(ContractJson.Write(report)).Ready);
    }

    [Fact]
    public async Task LateFaultIsObservedWithoutChangingCanceledSnapshot()
    {
        using var caller = new CancellationTokenSource();
        var started = Signal();
        var finish = Observation();
        var executor = new ProbeExecutor(new([Definition(_ =>
        {
            started.SetResult();
            return finish.Task;
        })]));
        var run = executor.RunAsync(cancellationToken: caller.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        caller.Cancel();
        var report = await Bounded(run);
        finish.SetException(new IOException("PRIVATE-CANARY"));
        Assert.True(await executor.WaitForIdleAsync(TimeSpan.FromSeconds(1)));
        Assert.Equal(2, report.ExitCode);
        Assert.Equal("probe.canceled", report.Probes[0].DiagnosticCode);
        Assert.DoesNotContain("PRIVATE-CANARY", ReportFormatter.Human(report));
    }

    [Fact]
    public async Task FaultsAndInvalidEvidenceUseAuthoredCatalogWithoutLeakingCallbackContent()
    {
        var executor = new ProbeExecutor(new(
        [
            Definition(_ => throw new IOException("PRIVATE-CANARY"), "test.fault"),
            Definition(_ => Task.FromResult(new ProbeObservation("PRIVATE-CANARY", EvidenceProvenance.Live)), "test.invalid"),
            Definition(_ => Task.FromResult(new ProbeObservation("fixture.passed", EvidenceProvenance.Live)), "test.fake_live"),
            Definition(_ => Task.FromResult(new ProbeObservation("fixture.passed", EvidenceProvenance.NotRun)), "test.unobserved")
        ]));
        var report = await executor.RunAsync();
        Assert.Equal(1, report.ExitCode);
        Assert.Equal("probe.fault", report.Probes[0].DiagnosticCode);
        Assert.All(report.Probes.Skip(1), probe => Assert.Equal("probe.invalid_evidence", probe.DiagnosticCode));
        Assert.All(report.Probes, probe => Assert.Equal(ProbeExecution.Faulted, probe.Execution));
        var json = System.Text.Encoding.UTF8.GetString(ContractJson.Write(report));
        Assert.DoesNotContain("PRIVATE-CANARY", json);
        Assert.DoesNotContain("PRIVATE-CANARY", ReportFormatter.Human(report));
        Assert.Equal(1, ContractJson.Read<DoctorReport>(ContractJson.Write(report)).ExitCode);
        Assert.Equal(3, (report with { SettingsState = SettingsLoadState.Invalid }).ExitCode);
    }

    [Fact]
    public void EveryCatalogKeyAndRemedyIsUniqueBoundedAndActionable()
    {
        Assert.Equal(DiagnosticCatalog.Findings.Count, DiagnosticCatalog.Findings.Select(f => f.Id).Distinct().Count());
        Assert.Equal(DiagnosticCatalog.Remedies.Count, DiagnosticCatalog.Remedies.Select(f => f.Id).Distinct().Count());
        foreach (var finding in DiagnosticCatalog.Findings)
        {
            ContractRules.Identifier(finding.Id);
            ContractRules.Text(finding.Summary, 512);
            Assert.False(string.IsNullOrWhiteSpace(DiagnosticCatalog.Remedy(finding.ActionId).Guidance));
            Assert.Equal(finding.Outcome == ProbeOutcome.Failed, finding.ErrorCode is not null);
        }
    }
}
