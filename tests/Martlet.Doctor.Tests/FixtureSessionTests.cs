using System.Text;
using Martlet.Audio;
using Martlet.Audio.Tests;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;
using Martlet.Core.Tests;
using Martlet.Diagnostics;
using Martlet.Sessions;

namespace Martlet.Doctor.Tests;

public sealed class FixtureSessionTests
{
    [Theory]
    [InlineData("complete", TurnOutcome.Completed, 0, "fixture.completed")]
    [InlineData("streaming", TurnOutcome.Completed, 0, "fixture.completed")]
    [InlineData("refused", TurnOutcome.Refused, 2, "fixture.refused")]
    [InlineData("refused-after-partial", TurnOutcome.Refused, 2, "fixture.refused")]
    [InlineData("no-speech", TurnOutcome.Suppressed, 2, "fixture.no_speech")]
    [InlineData("not-addressed", TurnOutcome.Suppressed, 2, "fixture.not_addressed")]
    [InlineData("canceled", TurnOutcome.Canceled, 2, "fixture.stopped")]
    [InlineData("truncated", TurnOutcome.Failed, 1, "fixture.failed")]
    [InlineData("slow", TurnOutcome.Failed, 1, "fixture.deadline")]
    [InlineData("failed", TurnOutcome.Failed, 1, "fixture.failed")]
    public async Task ActualCommandRunsBoundedFixtureAndSharedReport(string scenario, TurnOutcome outcome, int exit, string code)
    {
        var path = Path.Combine(Path.GetTempPath(), "Martlet.Fixture.Tests", Guid.NewGuid().ToString("N"));
        using var output = new StringWriter();
        Assert.Equal(exit, await DoctorCommand.RunAsync(["self-test", "--scenario", scenario, "--json", "--data-directory", path], output));
        var report = ContractJson.Read<DoctorReport>(Encoding.UTF8.GetBytes(output.ToString()));
        Assert.Equal(exit, report.ExitCode);
        Assert.Equal(code, Assert.Single(report.Probes).DiagnosticCode);
        Assert.NotNull(report.Fixture!.Trace);
        Assert.Equal(outcome, report.Fixture.Sequence.Result!.Outcome);
        Assert.Equal(report.Fixture.Sequence, report.Fixture.Trace.Final);
        Assert.Equal(EvidenceProvenance.Fixture, report.Fixture.Sequence.Result.Provenance);
        Assert.Null(report.Fixture.Playback);
        Assert.False(report.Fixture.ToneRequested);
        Assert.Null(report.SettingsState);
        Assert.False(Directory.Exists(path));
        Assert.DoesNotContain(path, output.ToString());
        var model = new DiagnosticStatusModel(new FoundationStatusService(new SettingsStore(path)).Executor);
        model.ObserveFixture(report.Fixture);
        Assert.Equal(ReportFormatter.Human(model.FixtureReport!), model.FixtureText);
        Assert.False(model.Report.Ready);
        Assert.Contains("FIXTURE - NOT AI", model.FixtureText);
        Assert.Contains("Audio OFF", model.FixtureText);
        Assert.True(Encoding.UTF8.GetByteCount(output.ToString()) < 32_768);
    }

    [Fact]
    public async Task PermissionIsPerRunAndRealSinkCompletionIsNotAudibility()
    {
        var device = new ControlledDevice();
        await using var session = new FixtureSession(new PcmPlaybackSink(device));
        var first = await session.RunAsync("complete");
        Assert.Equal(0, device.Opens);
        var second = await session.RunAsync("streaming", new(OutputPolicy.DefaultAtStart));
        Assert.Equal("Synthetic text.", second.Text);
        Assert.Equal(PlaybackState.Completed, second.Playback!.State);
        Assert.Equal(SyntheticTone.SampleCount, second.Playback.DeviceConsumedSamples);
        Assert.Equal(SyntheticTone.SampleCount, second.Playback.SubmittedSamples);
        Assert.True(second.Playback.DeviceDrainObserved);
        Assert.True(second.Playback.DeviceReleased);
        Assert.Null(second.Playback.AudibleSamples);
        Assert.Equal(second.Sequence.Ids, second.Playback.Ids);
        Assert.Equal(second.Sequence.RequestEpoch, second.Playback.Epoch);
        Assert.Equal(SyntheticTone.Frames(second.Sequence.Ids, second.Sequence.RequestEpoch)
            .SelectMany(frame => frame.Data.ToArray()), device.Bytes);
        var third = await session.RunAsync("complete");
        Assert.Equal(1, device.Opens);
        Assert.False(third.ToneRequested);
        Assert.NotEqual(first.Sequence.Ids.TurnId, third.Sequence.Ids.TurnId);
        Assert.NotEqual(first.Sequence.Ids.RequestId, third.Sequence.Ids.RequestId);
        Assert.True(third.Sequence.RequestEpoch > second.Sequence.RequestEpoch);
    }

    [Theory]
    [InlineData("refused")]
    [InlineData("refused-after-partial")]
    [InlineData("truncated")]
    [InlineData("no-speech")]
    [InlineData("canceled")]
    public async Task NonSuccessNeverRoutesToToneEvenWithPermission(string scenario)
    {
        var device = new ControlledDevice();
        await using var session = new FixtureSession(new PcmPlaybackSink(device));
        var result = await session.RunAsync(scenario, new(OutputPolicy.DefaultAtStart));
        Assert.Equal(0, device.Opens);
        Assert.Null(result.Playback);
        if (scenario.StartsWith("refused", StringComparison.Ordinal))
        {
            Assert.NotNull(result.RefusalText);
            Assert.DoesNotContain(result.RefusalText, result.Text);
        }
        if (scenario is "refused-after-partial" or "truncated")
        {
            Assert.True(result.Partial);
            Assert.Equal("Partial fixture.", result.Text);
        }
    }

    [Fact]
    public async Task TextTerminalPrecedesPlaybackAndStopInvalidatesBothBeforeExplicitNewRun()
    {
        var device = new ControlledDevice { BlockWrite = true };
        await using var session = new FixtureSession(new PcmPlaybackSink(device));
        var active = session.RunAsync("complete", new(OutputPolicy.DefaultAtStart));
        await device.EnteredWrite.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var before = session.Snapshot!;
        Assert.Equal(TurnOutcome.Completed, before.Sequence.Result!.Outcome);
        Assert.Equal(FixtureSessionStage.Playback, before.Stage);
        Assert.Equal(0, before.Playback!.SubmittedSamples);
        Assert.Equal(2, FixtureDiagnostics.Report(before).ExitCode);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.RunAsync("complete"));
        await session.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
        var stopped = await active;
        Assert.True(stopped.Stopped);
        Assert.Empty(stopped.Text);
        Assert.Equal(0, stopped.Sequence.QueuedChunks);
        Assert.Equal(0, stopped.Playback!.QueuedFrames);
        Assert.Equal(PlaybackState.Canceled, stopped.Playback.State);
        Assert.True(stopped.Sequence.CurrentEpoch > before.Sequence.RequestEpoch);
        Assert.Equal(stopped.Sequence, stopped.Trace!.Final);
        device.BlockWrite = false;
        var next = await session.RunAsync("complete");
        Assert.False(next.Stopped);
        Assert.False(next.ToneRequested);
        Assert.Equal(1, device.Opens);
        Assert.NotEqual(stopped.Sequence.Ids.RequestId, next.Sequence.Ids.RequestId);
        Assert.True(next.Sequence.RequestEpoch > stopped.Sequence.CurrentEpoch);
        Assert.Equal(next, session.Snapshot);
    }

    [Theory]
    [InlineData(ErrorCode.AudioDeviceUnavailable)]
    [InlineData(ErrorCode.AudioDeviceLost)]
    [InlineData(ErrorCode.AudioFormatUnsupported)]
    public async Task DeviceErrorsPreserveTextAndDoNotPoisonNextTextRun(ErrorCode code)
    {
        var device = new ControlledDevice { OpenError = code };
        await using var session = new FixtureSession(new PcmPlaybackSink(device));
        var result = await session.RunAsync("complete", new(OutputPolicy.DefaultAtStart));
        Assert.Equal("A synthetic fixture response.", result.Text);
        Assert.Equal(code, result.PlaybackError!.Code);
        var report = FixtureDiagnostics.Report(result);
        Assert.Equal(1, report.ExitCode);
        Assert.Equal("fixture.audio", report.Probes[0].ActionId);
        Assert.DoesNotContain("PRIVATE", ReportFormatter.Human(report));
        Assert.Equal(0, FixtureDiagnostics.Report(await session.RunAsync("complete")).ExitCode);
        Assert.Equal(1, device.Opens);
    }

    [Fact]
    public async Task BoundedSinkOverflowBecomesActionableReportNotUnhandledException()
    {
        var device = new ControlledDevice { BlockWrite = true };
        await using var session = new FixtureSession(new PcmPlaybackSink(device,
            new() { Capacity = TimeSpan.FromMilliseconds(100), Prebuffer = TimeSpan.FromMilliseconds(100) }));
        var result = await session.RunAsync("complete", new(OutputPolicy.DefaultAtStart)).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(ErrorCode.PayloadTooLarge, result.PlaybackError!.Code);
        Assert.Equal(1, FixtureDiagnostics.Report(result).ExitCode);
        Assert.Equal(0, result.Playback!.QueuedFrames);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeviceLossOrExternalStopAfterSubmissionNeverBecomesPlaybackSuccess(bool stop)
    {
        var device = new ControlledDevice { AutoConsume = false };
        var sink = new PcmPlaybackSink(device);
        await using var session = new FixtureSession(sink);
        var active = session.RunAsync("streaming", new(OutputPolicy.DefaultAtStart));
        await device.EnteredWrite.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitUntilAsync(() => session.Snapshot!.Playback?.SubmittedSamples > 0);
        if (stop)
            await sink.StopAsync();
        else
            device.PaddingError = ErrorCode.AudioDeviceLost;
        var result = await active.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("Synthetic text.", result.Text);
        Assert.True(result.Playback!.MayHavePlayed);
        Assert.Null(result.Playback.AudibleSamples);
        Assert.False(result.Playback.DeviceDrainObserved);
        var report = FixtureDiagnostics.Report(result);
        Assert.Equal(stop ? 2 : 1, report.ExitCode);
        Assert.Equal(stop ? "fixture.audio_incomplete" : "fixture.audio_failed", report.Probes[0].DiagnosticCode);
        Assert.Equal(0, result.Playback.QueuedFrames);
        Assert.Equal(1, device.Opens);
    }

    [Fact]
    public async Task FailedDeviceReleaseQuarantinesAudioButFreshTextStillWorks()
    {
        var device = new ControlledDevice { FailDispose = true };
        await using var session = new FixtureSession(new PcmPlaybackSink(device));
        var failed = await session.RunAsync("complete", new(OutputPolicy.DefaultAtStart));
        Assert.False(failed.Playback!.DeviceReleased);
        Assert.Equal(ErrorCode.AudioPlaybackFailed, failed.PlaybackError!.Code);
        var text = await session.RunAsync("streaming");
        Assert.Equal(0, FixtureDiagnostics.Report(text).ExitCode);
        var retry = await session.RunAsync("complete", new(OutputPolicy.DefaultAtStart));
        Assert.Equal(ErrorCode.AudioPlaybackFailed, retry.PlaybackError!.Code);
        Assert.Equal(1, device.Opens);
        Assert.Equal(1, FixtureDiagnostics.Report(retry).ExitCode);
    }

    [Fact]
    public async Task DelayedOldStopCannotCancelNewPlaybackAfterItsCapturedSessionCompletes()
    {
        var firstDevice = new ControlledDevice { BlockWrite = true };
        var nextDevice = new ControlledDevice { BlockWrite = true };
        var stopEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseStop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sink = new PcmPlaybackSink(new DeviceSequence(firstDevice, nextDevice));
        await using var session = new FixtureSession(sink, async () =>
        {
            stopEntered.TrySetResult();
            await releaseStop.Task;
        });
        var first = session.RunAsync("complete", new(OutputPolicy.DefaultAtStart));
        await firstDevice.EnteredWrite.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var oldStop = session.StopAsync();
        await stopEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            var canceled = await first.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(canceled.Playback!.DeviceReleased);
            var next = session.RunAsync("streaming", new(OutputPolicy.DefaultAtStart));
            await nextDevice.EnteredWrite.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var nextIds = session.Snapshot!.Sequence.Ids;
            releaseStop.TrySetResult();
            await oldStop.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(nextIds, session.Snapshot!.Sequence.Ids);
            Assert.NotEqual(PlaybackState.Canceled, session.Snapshot.Playback!.State);
            Assert.False(next.IsCompleted);
            nextDevice.Release.Set();
            Assert.Equal(PlaybackState.Completed, (await next.WaitAsync(TimeSpan.FromSeconds(5))).Playback!.State);
        }
        finally
        {
            releaseStop.TrySetResult();
            nextDevice.Release.Set();
        }
    }

    [Fact]
    public async Task OverlappingStopAndDisposeRejectStartWhileRetainingCapturedCleanup()
    {
        var device = new ControlledDevice { BlockWrite = true, IgnoreCancellation = true };
        var releaseStops = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = 0;
        var session = new FixtureSession(new PcmPlaybackSink(device), () =>
        {
            if (Interlocked.Increment(ref entered) == 3)
                allEntered.TrySetResult();
            return releaseStops.Task;
        });
        var first = session.RunAsync("complete", new(OutputPolicy.DefaultAtStart));
        await device.EnteredWrite.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var stopOne = session.StopAsync();
        var stopTwo = session.StopAsync();
        var disposal = session.DisposeAsync().AsTask();
        try
        {
            await allEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            device.Release.Set();
            await first.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.ThrowsAsync<ObjectDisposedException>(() => session.RunAsync("complete"));
        }
        finally
        {
            device.Release.Set();
            releaseStops.TrySetResult();
        }
        await Task.WhenAll(stopOne, stopTwo, disposal).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, device.Opens);
        Assert.True(session.Snapshot!.Playback!.DeviceReleased);
        Assert.Equal(0, session.Snapshot.Playback.QueuedFrames);
        await session.DisposeAsync();
    }

    [Fact]
    public async Task CallerCancellationDuringNativeWriteClearsTextAndNeverAcceptsLateOutput()
    {
        var device = new ControlledDevice { BlockWrite = true };
        using var cancellation = new CancellationTokenSource();
        await using var session = new FixtureSession(new PcmPlaybackSink(device));
        var active = session.RunAsync("complete", new(OutputPolicy.DefaultAtStart), cancellation.Token);
        await device.EnteredWrite.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        var stopped = await active.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(stopped.Stopped);
        Assert.Empty(stopped.Text);
        Assert.Equal(0, stopped.Playback!.QueuedFrames);
        Assert.Equal(PlaybackState.Canceled, stopped.Playback.State);
        Assert.Equal(stopped.Sequence, stopped.Trace!.Final);
        var next = await session.RunAsync("streaming");
        device.Release.Set();
        Assert.Equal(next, session.Snapshot);
        Assert.Equal("Synthetic text.", next.Text);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public async Task StopAtEachScriptBoundaryDiscardsPendingDeliveryAndAllowsFreshRetry(int cut)
    {
        var clock = new ManualClock();
        await using var session = new FixtureSession(timeProvider: clock, pacing: TimeSpan.FromMilliseconds(100));
        var active = session.RunAsync("streaming");
        for (var index = 0; index < cut; index++)
        {
            var previous = session.Snapshot;
            await AdvanceUntilAsync(clock, () => session.Snapshot != previous);
        }
        await session.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
        var stopped = await active;
        Assert.True(stopped.Stopped);
        Assert.Empty(stopped.Text);
        Assert.Equal(0, stopped.Sequence.QueuedChunks);
        var retired = stopped.Sequence.Ids;
        var retry = session.RunAsync("complete");
        await AdvanceUntilAsync(clock, () => retry.IsCompleted);
        var result = await retry;
        Assert.NotEqual(retired.RequestId, result.Sequence.Ids.RequestId);
        Assert.Equal("A synthetic fixture response.", result.Text);
        Assert.Null(result.RefusalText);
        Assert.Equal(result, session.Snapshot);
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(result, session.Snapshot);
    }

    [Fact]
    public async Task PreCanceledCommandReportsCancellationWithoutDataOrAudio()
    {
        using var output = new StringWriter();
        Assert.Equal(2, await DoctorCommand.RunAsync(["self-test", "--json"], output, new CancellationToken(true)));
        var report = ContractJson.Read<DoctorReport>(Encoding.UTF8.GetBytes(output.ToString()));
        Assert.True(report.Fixture!.Stopped);
        Assert.Equal(TurnOutcome.Canceled, report.Fixture.Sequence.Result!.Outcome);
        Assert.Null(report.Fixture.Playback);
    }

    [Fact]
    public async Task FixtureEvidenceAgesWithoutRefreshingOrChangingRealReadiness()
    {
        var clock = new ManualClock();
        var model = new DiagnosticStatusModel(new FoundationStatusService(
            new SettingsStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))), clock).Executor);
        await using var session = new FixtureSession();
        model.ObserveFixture(await session.RunAsync("complete"));
        Assert.Equal(0, model.FixtureReport!.ExitCode);
        Assert.False(model.Report.Ready);
        clock.Advance(TimeSpan.FromMinutes(1));
        model.UpdateAge();
        Assert.Equal(2, model.FixtureReport.ExitCode);
        Assert.Equal(EvidenceFreshness.Stale, model.FixtureReport.Probes[0].Freshness);
        Assert.False(model.Report.Ready);
    }

    [Theory]
    [InlineData("fixture.completed")]
    [InlineData("fixture.passed")]
    public async Task FixtureFindingCannotBeLabeledLiveByRegistryCallback(string finding)
    {
        var registry = new ProbeRegistry(
        [
            new("test.fixture", Stage.Application, true, [ProbeEffect.LocalReadOnly],
                _ => Task.FromResult(new ProbeObservation(finding, EvidenceProvenance.Live)))
        ]);
        var report = await new ProbeExecutor(registry).RunAsync();
        Assert.Equal("probe.invalid_evidence", report.Probes[0].DiagnosticCode);
        Assert.Equal(1, report.ExitCode);
    }

    [Theory]
    [InlineData("self-test", "--scenario", "PRIVATE-CANARY")]
    [InlineData("self-test", "--scenario")]
    [InlineData("self-test", "--play-tone", "--play-tone")]
    [InlineData("status", "--play-tone")]
    [InlineData("run", "fixture.session")]
    public async Task InvalidFixtureSelectionsCannotInvokeAnEffect(params string[] args)
    {
        using var output = new StringWriter();
        Assert.Equal(3, await DoctorCommand.RunAsync([.. args, "--json"], output));
        Assert.DoesNotContain("PRIVATE-CANARY", output.ToString());
        Assert.Null(ContractJson.Read<DoctorReport>(Encoding.UTF8.GetBytes(output.ToString())).Fixture);
    }

    private static async Task AdvanceUntilAsync(ManualClock clock, Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            clock.Advance(TimeSpan.FromMilliseconds(100));
            await Task.Delay(1);
        }
        Assert.True(condition(), "The production session did not advance with injected time.");
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(1);
        Assert.True(condition(), "The controlled device did not reach the requested boundary.");
    }

    private sealed class DeviceSequence(params ControlledDevice[] devices) : IPlaybackDeviceFactory
    {
        private int next;
        public IPlaybackDevice Open(OutputSelection output, Core.Audio.PcmFormat format, CancellationToken token) =>
            devices[Interlocked.Increment(ref next) - 1].Open(output, format, token);
    }
}
