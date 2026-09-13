using Martlet.Core.Contracts;
using Martlet.Core.Settings;
using Martlet.Diagnostics;

namespace Martlet.Core.Tests;

public sealed class DiagnosticStatusModelTests
{
    [Fact]
    public async Task ProductionRegistryDrivesPipelineAndFirstRunWithoutEffects()
    {
        var path = Path.Combine(Path.GetTempPath(), "Martlet.Status.Tests", Guid.NewGuid().ToString("N"));
        var clock = new ManualClock();
        var service = new FoundationStatusService(new SettingsStore(path), clock);
        var model = new DiagnosticStatusModel(service.Executor);
        Assert.All(model.Report.Probes, probe => Assert.Equal("probe.not_run", probe.DiagnosticCode));
        Assert.True(await model.RefreshAsync());
        Assert.True(model.CanCreateProfile);
        Assert.Equal(SettingsLoadState.FirstRun, model.Report.SettingsState);
        Assert.Equal(ReportFormatter.Human(model.Report), model.Text);
        Assert.Equal(8, model.Pipeline.Count);
        Assert.All(model.Pipeline, node =>
        {
            Assert.Same(model.Report.Probes.Single(probe => probe.Id == node.Probe!.Id), node.Probe);
            Assert.Contains("NotRun", node.Description);
            Assert.Contains("Next:", node.Description);
        });
        Assert.DoesNotContain(path, model.Text);
        Assert.False(Directory.Exists(path));
        clock.Advance(TimeSpan.FromMinutes(1));
        model.UpdateAge();
        Assert.Equal(EvidenceFreshness.Stale, model.Report.Probes[0].Freshness);
        Assert.False(model.Report.Ready);
    }

    [Fact]
    public async Task StopPreventsOverlapAndClosedModelCannotBeOverwrittenByLateCompletion()
    {
        var clock = new ManualClock();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource<ProbeObservation>(TaskCreationOptions.RunContinuationsAsynchronously);
        var executor = new ProbeExecutor(new(
        [
            new("test.held", Stage.Application, true, [ProbeEffect.LocalReadOnly], _ =>
            {
                started.SetResult();
                return finish.Task;
            })
        ]), clock);
        var model = new DiagnosticStatusModel(executor);
        var refresh = model.RefreshAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(model.IsRunning);
        Assert.False(await model.RefreshAsync());
        model.Stop();
        await refresh.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(model.CanRefresh);
        Assert.Contains("still active", model.Activity);
        Assert.Equal("probe.canceled", model.Report.Probes[0].DiagnosticCode);
        var closing = model.CloseAsync();
        clock.Advance(TimeSpan.FromMilliseconds(250));
        Assert.False(await closing.WaitAsync(TimeSpan.FromSeconds(5)));
        var closedReport = model.Report;
        finish.SetResult(new("fixture.passed", EvidenceProvenance.Fixture));
        Assert.True(await executor.WaitForIdleAsync(TimeSpan.FromSeconds(1)));
        model.UpdateAge();
        Assert.Same(closedReport, model.Report);
        Assert.False(await model.RefreshAsync());
    }
}
