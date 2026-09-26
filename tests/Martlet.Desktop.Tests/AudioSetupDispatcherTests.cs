using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using Martlet.Audio;
using Martlet.Core.Audio;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;
using Martlet.Core.Tests;
using Martlet.Desktop;

namespace Martlet.Desktop.Tests;

public sealed class AudioSetupDispatcherTests
{
    [Fact]
    public Task OpeningDecliningAndSavingNeverAccessesDevicesOrVault() => OnDispatcher(async () =>
    {
        using var fixture = new Fixture();
        var window = fixture.Open(approve: false);
        try
        {
            await Ready(window);
            Assert.Contains("never tested", Text(window, "StatusText"));
            Assert.False(Directory.Exists(fixture.Directory));
            Click(window, "MicButton");
            Click(window, "OutputButton");
            Assert.Contains("Permission declined", Text(window, "ResultText"));
            Assert.Equal(0, fixture.Catalog.Calls);
            Assert.Equal(0, fixture.Capture.Opens);
            Assert.Equal(0, fixture.Output.Opens);
            Assert.Equal(2, fixture.Confirmations);
            Click(window, "SaveButton");
            await Wait(() => Text(window, "ResultText").Contains("Setup saved", StringComparison.Ordinal));
            window.Close();
            window = fixture.Open();
            await Ready(window);
            Assert.Equal(0, fixture.Capture.Opens);
            Assert.Equal(0, fixture.Output.Opens);
            Assert.Equal(0, fixture.Catalog.Calls);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task FindDevicesRunsOffDispatcherAndFixedChoicePersistsWithoutOpening() => OnDispatcher(async () =>
    {
        using var fixture = new Fixture();
        var window = fixture.Open();
        try
        {
            await Ready(window);
            Click(window, "FindButton");
            await Wait(() => Text(window, "ResultText").Contains("Found 1 input", StringComparison.Ordinal));
            Assert.NotEqual(Environment.CurrentManagedThreadId, fixture.Catalog.Thread);
            Control<ComboBox>(window, "InputChoice").SelectedIndex = 1;
            Control<ComboBox>(window, "OutputChoice").SelectedIndex = 1;
            Click(window, "SaveButton");
            await Wait(() => Text(window, "ResultText").Contains("Setup saved", StringComparison.Ordinal));
            var stored = (await fixture.Store.LoadAsync()).Settings!.Audio!;
            Assert.Equal("PRIVATE-input", stored.Input.EndpointId);
            Assert.Equal("PRIVATE-output", stored.Output.EndpointId);
            Assert.DoesNotContain("PRIVATE", Text(window, "StatusText"));
            Assert.Equal(0, fixture.Capture.Opens);
            Assert.Equal(0, fixture.Output.Opens);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task MeterUsesRealLibraryPcmAndDiscardedCheckpointIsLocalOnly() => OnDispatcher(async () =>
    {
        using var fixture = new Fixture();
        fixture.Capture.Packet();
        var window = fixture.Open();
        try
        {
            await Ready(window);
            Click(window, "MicButton");
            await Wait(() => Text(window, "LevelText").Contains("peak 0.5000", StringComparison.Ordinal));
            Assert.Contains("NOT VAD", Text(window, "LevelText"));
            Assert.False(Control<Button>(window, "OutputButton").IsEnabled);
            Click(window, "OutputButton");
            Assert.Equal(0, fixture.Output.Opens);
            Assert.Equal(InputPolicy.FollowDefaultOnNextPress, fixture.Capture.Selected!.Policy);
            fixture.Capture.Packet(count: 49);
            await Wait(() => Text(window, "ResultText").Contains("Samples received and discarded", StringComparison.Ordinal));
            Assert.Contains("Whole-test selected PCM: peak 0.500000; RMS 0.500000; canonical samples 80000", Text(window, "LevelText"));
            Assert.Contains("Samples at/above 1% full-scale: 80000", Text(window, "LevelText"));
            Assert.Contains("1% full-scale", Text(window, "LevelText"));
            Click(window, "SaveButton");
            await Wait(() => Text(window, "ResultText").Contains("Setup saved", StringComparison.Ordinal));
            var saved = (await fixture.Store.LoadAsync()).Settings!.Audio!;
            Assert.Equal(LocalAudioOutcome.SamplesReceived, saved.Input.Checkpoint!.Outcome);
            Assert.Null(saved.Output.Checkpoint);
            Assert.Contains("UNVERIFIED", Text(window, "StatusText"));
            Assert.DoesNotContain("PRIVATE", Text(window, "StatusText"));
            Assert.Equal(1, fixture.Capture.Disposals);
            Assert.Equal(0, fixture.Output.Opens);
        }
        finally { window.Close(); }
    });

    [Fact]
    public async Task ServiceDisposesAllTransferredOrUnclaimedCapturePcm()
    {
        using var fixture = new Fixture();
        fixture.Capture.ObservePcmZeroing = true;
        fixture.Capture.Packet(count: 50);
        var operation = fixture.Audio.Start(AudioSetupAction.Microphone, AudioChoice.Default(true), true)!;
        await operation.Worker.Completion.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(operation.Status.Succeeded);
        Assert.Equal(AudioInputSignal.DetectableAmplitude, operation.Status.Signal);
        Assert.Equal(0.5, operation.Status.Peak);
        Assert.Equal(0.5, operation.Status.Rms);
        Assert.Equal(0, operation.CaptureSnapshot!.RetainedPcmBytes);
        Assert.Equal(80000, operation.CaptureSnapshot.CanonicalSamples);
        Assert.Equal(80000L, operation.Status.SamplesAtOrAboveThreshold);
        Assert.True(fixture.Capture.SawNonzeroPcm);
        Assert.All(Assert.IsType<byte[]>(fixture.Capture.ObservedPcm), value => Assert.Equal((byte)0, value));
        fixture.Capture.Packet();
        var next = fixture.Audio.Start(AudioSetupAction.Microphone, AudioChoice.Default(true), true)!;
        await Wait(() => next.Status.Samples > 0);
        next.Stop();
        await next.Worker.Completion.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Null(next.Status.Signal);
        Assert.Null(next.Status.Peak);
        Assert.Null(next.Status.Checkpoint);
        Assert.Equal(0, next.CaptureSnapshot!.RetainedPcmBytes);
        Assert.False(next.Status.Succeeded);
        Assert.True(next.CaptureSnapshot.Epoch > operation.CaptureSnapshot.Epoch);
        Assert.NotEqual(next.CaptureSnapshot.Ids, operation.CaptureSnapshot.Ids);
    }

    [Theory]
    [InlineData((short)0)]
    [InlineData((short)128)]
    [InlineData((short)327)]
    public Task SilentOrNearSilentInputShowsWholeTestRemedyAndCannotSaveCheckpoint(short amplitude) => OnDispatcher(async () =>
    {
        using var fixture = new Fixture();
        fixture.Capture.Packet(amplitude, count: 50);
        var window = fixture.Open();
        try
        {
            await Ready(window);
            Click(window, "MicButton");
            await Wait(() => Text(window, "ResultText").Contains("below 1% full-scale", StringComparison.Ordinal));
            Assert.Contains("hardware mute", Text(window, "ResultText"));
            Assert.Contains("privacy", Text(window, "ResultText"));
            Assert.Contains("Whole-test selected PCM", Text(window, "LevelText"));
            Assert.Contains("no checkpoint", Text(window, "LevelText"));
            Assert.DoesNotContain("SamplesReceived", Text(window, "StatusText"));
            Click(window, "SaveButton");
            await Wait(() => Text(window, "ResultText").Contains("Setup saved", StringComparison.Ordinal));
            Assert.Null((await fixture.Store.LoadAsync()).Settings!.Audio!.Input.Checkpoint);
            Assert.Equal(1, fixture.Capture.Opens);
            Assert.Equal(0, fixture.Output.Opens);
            Assert.DoesNotContain("PRIVATE", Text(window, "ResultText") + Text(window, "LevelText") + Text(window, "StatusText"));
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task NoInputFramesHasSpecificRemedyAndNeverBecomesSavedEvidence() => OnDispatcher(async () =>
    {
        using var fixture = new Fixture();
        var window = fixture.Open();
        try
        {
            await Ready(window);
            Click(window, "MicButton");
            await Wait(() => fixture.Capture.Reads > 0);
            fixture.Clock.Advance(TimeSpan.FromSeconds(5));
            await Wait(() => Text(window, "ResultText").Contains("No microphone PCM frames", StringComparison.Ordinal));
            Assert.Contains("selected microphone or changed default", Text(window, "ResultText"));
            Assert.Contains("No PCM frames received in this test", Text(window, "LevelText"));
            Click(window, "SaveButton");
            await Wait(() => Text(window, "ResultText").Contains("Setup saved", StringComparison.Ordinal));
            Assert.Null((await fixture.Store.LoadAsync()).Settings!.Audio!.Input.Checkpoint);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task SilentRetestClearsPriorHistoricalCheckpointOnlyOnExplicitSave() => OnDispatcher(async () =>
    {
        using var fixture = new Fixture();
        fixture.Capture.Packet();
        var window = fixture.Open();
        try
        {
            await Ready(window);
            Click(window, "MicButton");
            await Wait(() => Text(window, "LevelText").Contains("Live selected PCM: peak 0.5000", StringComparison.Ordinal));
            fixture.Capture.Packet(count: 49);
            await Wait(() => Text(window, "ResultText").Contains("Samples received and discarded", StringComparison.Ordinal));
            Click(window, "SaveButton");
            await Wait(() => Text(window, "ResultText").Contains("Setup saved", StringComparison.Ordinal));
            Assert.Equal(LocalAudioOutcome.SamplesReceived, (await fixture.Store.LoadAsync()).Settings!.Audio!.Input.Checkpoint!.Outcome);

            fixture.Capture.Packet(0, count: 50);
            Click(window, "MicButton");
            await Wait(() => Text(window, "ResultText").Contains("below 1% full-scale", StringComparison.Ordinal));
            Assert.DoesNotContain("SamplesReceived", Text(window, "StatusText"));
            Click(window, "SaveButton");
            await Wait(() => Text(window, "ResultText").Contains("Setup saved", StringComparison.Ordinal));
            Assert.Null((await fixture.Store.LoadAsync()).Settings!.Audio!.Input.Checkpoint);
        }
        finally { window.Close(); }
    });

    [Fact]
    public async Task AdvisoryThresholdSeparatesNearSilentFromDetectablePcm()
    {
        using var fixture = new Fixture();
        fixture.Capture.Packet(327, count: 50);
        var low = fixture.Audio.Start(AudioSetupAction.Microphone, AudioChoice.Default(true), true)!;
        await low.Worker.Completion.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(AudioInputSignal.BelowAdvisoryThreshold, low.Status.Signal);
        Assert.False(low.Status.Succeeded);
        Assert.Null(low.Status.Checkpoint);
        Assert.Equal(327 / 32768.0, low.Status.Rms);
        Assert.Equal(0, low.CaptureSnapshot!.RetainedPcmBytes);

        fixture.Capture.Packet(328, count: 50);
        var detectable = fixture.Audio.Start(AudioSetupAction.Microphone, AudioChoice.Default(true), true)!;
        await detectable.Worker.Completion.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(AudioInputSignal.DetectableAmplitude, detectable.Status.Signal);
        Assert.True(detectable.Status.Succeeded);
        Assert.Equal(LocalAudioOutcome.SamplesReceived, detectable.Status.Checkpoint!.Outcome);
        Assert.Equal(0, detectable.CaptureSnapshot!.RetainedPcmBytes);
        Assert.NotEqual(low.CaptureSnapshot.Ids, detectable.CaptureSnapshot.Ids);
    }

    [Theory]
    [InlineData(1, AudioInputSignal.InsufficientFrames)]
    [InlineData(39, AudioInputSignal.InsufficientFrames)]
    [InlineData(40, AudioInputSignal.DetectableAmplitude)]
    public async Task FrameCoverageRequiresFourSecondsOfFiveSecondInput(int packets, AudioInputSignal expected)
    {
        using var fixture = new Fixture();
        fixture.Capture.Packet(count: packets);
        var operation = fixture.Audio.Start(AudioSetupAction.Microphone, AudioChoice.Default(true), true)!;
        await Wait(() => operation.Status.Samples == packets * 1600);
        fixture.Clock.Advance(TimeSpan.FromSeconds(5));
        await operation.Worker.Completion.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(expected, operation.Status.Signal);
        Assert.Equal(0.5, operation.Status.Rms);
        Assert.Equal(packets * 1600L, operation.Status.SamplesAtOrAboveThreshold);
        Assert.Equal(expected == AudioInputSignal.DetectableAmplitude, operation.Status.Succeeded);
        Assert.Equal(expected == AudioInputSignal.DetectableAmplitude, operation.Status.Checkpoint is not null);
        Assert.Equal(0, operation.CaptureSnapshot!.RetainedPcmBytes);
    }

    [Theory]
    [InlineData(1, AudioInputSignal.IntermittentAmplitude)]
    [InlineData(10, AudioInputSignal.IntermittentAmplitude)]
    [InlineData(12, AudioInputSignal.IntermittentAmplitude)]
    [InlineData(13, AudioInputSignal.DetectableAmplitude)]
    [InlineData(20, AudioInputSignal.DetectableAmplitude)]
    public Task ShortClickAndIntermittentLevelsDoNotMasqueradeAsSustainedInput(
        int loudPackets, AudioInputSignal expected) => OnDispatcher(async () =>
    {
        using var fixture = new Fixture();
        var window = fixture.Open();
        try
        {
            await Ready(window);
            for (var i = 0; i < 50; i++)
                fixture.Capture.Packet(i % (50 / loudPackets) == 0 && i / (50 / loudPackets) < loudPackets
                    ? (short)16384 : (short)0);
            Click(window, "MicButton");
            await Wait(() => Text(window, "ResultText").Contains("level too intermittent", StringComparison.Ordinal) ||
                Text(window, "ResultText").Contains("Samples received and discarded", StringComparison.Ordinal));
            var lowCoverage = expected == AudioInputSignal.IntermittentAmplitude;
            Assert.Contains(lowCoverage ? "brief click" : "Samples received and discarded", Text(window, "ResultText"), StringComparison.OrdinalIgnoreCase);
            Assert.Contains($"Samples at/above 1% full-scale: {loudPackets * 1600}", Text(window, "LevelText"));
            Assert.Contains("canonical samples 80000", Text(window, "LevelText"));
            Click(window, "SaveButton");
            await Wait(() => Text(window, "ResultText").Contains("Setup saved", StringComparison.Ordinal));
            var checkpoint = (await fixture.Store.LoadAsync()).Settings!.Audio!.Input.Checkpoint;
            if (lowCoverage) Assert.Null(checkpoint);
            else Assert.Equal(LocalAudioOutcome.SamplesReceived, checkpoint!.Outcome);
            Assert.DoesNotContain("PRIVATE", Text(window, "ResultText") + Text(window, "LevelText") + Text(window, "StatusText"));
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task FailedCaptureAfterPositiveMeterClearsLevelAndFreshSilentTestCannotReplay() => OnDispatcher(async () =>
    {
        using var fixture = new Fixture();
        fixture.Capture.Packet();
        fixture.Capture.Block = "CaptureReadAfterPacket";
        fixture.Capture.Failure = ErrorCode.AudioDeviceChanged;
        fixture.Capture.FailureAfterPacket = true;
        var window = fixture.Open();
        try
        {
            await Ready(window);
            Click(window, "MicButton");
            await fixture.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await Wait(() => Text(window, "LevelText").Contains("Live selected PCM: peak 0.5000", StringComparison.Ordinal));
            fixture.Release.Set();
            await Wait(() => Text(window, "ResultText").Contains("Local action failed", StringComparison.Ordinal));
            Assert.Contains("No replacement", Text(window, "ResultText"));
            Assert.Contains("earlier live level is not a valid test result", Text(window, "LevelText"));
            Assert.DoesNotContain("0.5000", Text(window, "LevelText"));
            Assert.Equal(1, fixture.Capture.Opens);
            Assert.Equal(0, fixture.Capture.PacketCount);
            fixture.Capture.Block = null;
            fixture.Capture.Failure = null;
            fixture.Capture.FailureAfterPacket = false;
            fixture.Capture.Packet(0, count: 50);
            Click(window, "MicButton");
            await Wait(() => Text(window, "ResultText").Contains("below 1% full-scale", StringComparison.Ordinal));
            Assert.Contains("Whole-test selected PCM: peak 0.000000; RMS 0.000000", Text(window, "LevelText"));
            Assert.Equal(2, fixture.Capture.Opens);
            Assert.DoesNotContain("PRIVATE", Text(window, "StatusText") + Text(window, "ResultText"));
            Click(window, "SaveButton");
            await Wait(() => Text(window, "ResultText").Contains("Setup saved", StringComparison.Ordinal));
            Assert.Null((await fixture.Store.LoadAsync()).Settings!.Audio!.Input.Checkpoint);
        }
        finally { fixture.Release.Set(); window.Close(); }
    });

    [Fact]
    public Task StopAfterPositiveMeterDiscardsItAndRequiresFreshMicPermission() => OnDispatcher(async () =>
    {
        using var fixture = new Fixture();
        fixture.Capture.Packet();
        var window = fixture.Open();
        try
        {
            await Ready(window);
            Click(window, "MicButton");
            await Wait(() => Text(window, "LevelText").Contains("Live selected PCM: peak 0.5000", StringComparison.Ordinal));
            Click(window, "StopButton");
            await Wait(() => Text(window, "ResultText").Contains("Observation stopped", StringComparison.Ordinal));
            await Wait(() => !fixture.Runner.IsRunning);
            Assert.True(Text(window, "LevelText").Contains("no longer live", StringComparison.Ordinal) ||
                Text(window, "LevelText").Contains("not a valid test result", StringComparison.Ordinal));
            Assert.DoesNotContain("0.500000", Text(window, "LevelText"));
            Assert.DoesNotContain("SamplesReceived", Text(window, "StatusText"));
            fixture.Capture.Packet(0, count: 50);
            Click(window, "MicButton");
            await Wait(() => Text(window, "ResultText").Contains("below 1% full-scale", StringComparison.Ordinal));
            Assert.Equal(2, fixture.Capture.Opens);
            Assert.Equal(2, fixture.Confirmations);
            Click(window, "SaveButton");
            await Wait(() => Text(window, "ResultText").Contains("Setup saved", StringComparison.Ordinal));
            Assert.Null((await fixture.Store.LoadAsync()).Settings!.Audio!.Input.Checkpoint);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task OutputIsExactToneAndHeardRequiresThisSuccessfulUnchangedSelection() => OnDispatcher(async () =>
    {
        using var fixture = new Fixture();
        var window = fixture.Open();
        try
        {
            await Ready(window);
            Click(window, "FindButton");
            await Wait(() => Text(window, "ResultText").Contains("Found 1 input", StringComparison.Ordinal));
            Control<ComboBox>(window, "OutputChoice").SelectedIndex = 1;
            Assert.False(Control<Button>(window, "HeardButton").IsEnabled);
            Click(window, "HeardButton");
            Click(window, "OutputButton");
            await Wait(() => Control<Button>(window, "HeardButton").IsEnabled);
            Assert.Equal(OutputPolicy.FixedEndpoint, fixture.Output.Selected!.Policy);
            Assert.Equal("PRIVATE-output", fixture.Output.Selected.EndpointId);
            var ids = new CorrelationIds { SessionId = Guid.NewGuid(), TurnId = Guid.NewGuid(), RequestId = Guid.NewGuid() };
            Assert.Equal(SyntheticTone.Frames(ids, 0).SelectMany(frame => frame.Data.ToArray()).ToArray(), fixture.Output.Bytes.ToArray());
            Assert.Contains("audibility UNCONFIRMED", Text(window, "ResultText"));
            Click(window, "HeardButton");
            Click(window, "SaveButton");
            await Wait(() => Text(window, "ResultText").Contains("Setup saved", StringComparison.Ordinal));
            Assert.Equal(LocalAudioOutcome.Heard, (await fixture.Store.LoadAsync()).Settings!.Audio!.Output.Checkpoint!.Outcome);
            Control<ComboBox>(window, "OutputChoice").SelectedIndex = 0;
            Assert.False(Control<Button>(window, "HeardButton").IsEnabled);
            Click(window, "SaveButton");
            await Wait(() => !fixture.Runner.IsRunning && Text(window, "ResultText").Contains("Setup saved", StringComparison.Ordinal));
            Assert.Null((await fixture.Store.LoadAsync()).Settings!.Audio!.Output.Checkpoint);
            Assert.Equal(0, fixture.Capture.Opens);
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData("Discovery")]
    [InlineData("CaptureOpen")]
    [InlineData("CaptureRead")]
    [InlineData("CaptureDispose")]
    [InlineData("OutputOpen")]
    [InlineData("OutputDispose")]
    [InlineData("CaptureCallback")]
    [InlineData("OutputCallback")]
    public Task BlockedNativeKeepsHeartbeatCloseAndReopenResponsiveWithoutEarlyRelease(string block) => OnDispatcher(async () =>
    {
        using var fixture = new Fixture();
        fixture.ConfigureBlock(block);
        var window = fixture.Open();
        AudioSetupWindow? reopened = null;
        try
        {
            await Ready(window);
            var action = block == "Discovery" ? "FindButton" : block.StartsWith("Output", StringComparison.Ordinal) ? "OutputButton" : "MicButton";
            Click(window, action);
            if (block == "CaptureDispose")
            {
                await Wait(() => fixture.Capture.Reads > 0);
                fixture.Clock.Advance(TimeSpan.FromSeconds(5));
            }
            if (block is "CaptureCallback" or "OutputCallback")
            {
                await Wait(() => block == "CaptureCallback" ? fixture.Capture.Reads > 0 : fixture.Output.Opens > 0);
                Click(window, "StopButton");
            }
            await fixture.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await Heartbeat();
            Assert.True(fixture.Runner.IsRunning);
            if (block is not ("CaptureCallback" or "OutputCallback")) Click(window, action);
            Click(window, "PauseButton");
            await Wait(() => Text(window, "ResultText").Contains("Observation stopped", StringComparison.Ordinal));
            var oldText = Text(window, "ResultText");
            var oldStatus = Text(window, "StatusText");
            window.Close();
            Assert.True(fixture.Runner.IsRunning);
            reopened = fixture.Open();
            await Heartbeat();
            Assert.False(Control<Button>(reopened, "FindButton").IsEnabled);
            fixture.Release.Set();
            await Wait(() => !fixture.Runner.IsRunning);
            await Heartbeat();
            Assert.Equal(oldText, Text(window, "ResultText"));
            Assert.Equal(oldStatus, Text(window, "StatusText"));
            if (block == "CaptureOpen") Assert.Equal(0, fixture.Capture.Starts);
            if (block == "OutputOpen") Assert.Equal(0, fixture.Output.Starts);
            Assert.Equal(block == "Discovery" ? 1 : 0, fixture.Catalog.Calls);
        }
        finally
        {
            fixture.Release.Set();
            reopened?.Close();
            window.Close();
            await Wait(() => !fixture.Runner.IsRunning);
        }
    });

    [Fact]
    public Task DiscoveryTimeoutDiscardsLateGenerationWithoutPretendingRelease() => OnDispatcher(async () =>
    {
        using var fixture = new Fixture();
        fixture.ConfigureBlock("Discovery");
        var window = fixture.Open();
        try
        {
            await Ready(window);
            Click(window, "FindButton");
            await fixture.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            fixture.Clock.Advance(TimeSpan.FromSeconds(6));
            await Wait(() => Text(window, "ResultText").Contains("timed out", StringComparison.Ordinal));
            Assert.True(fixture.Runner.IsRunning);
            Assert.Single(Control<ComboBox>(window, "InputChoice").Items);
            fixture.Release.Set();
            await Wait(() => !fixture.Runner.IsRunning);
            Assert.Single(Control<ComboBox>(window, "InputChoice").Items);
            fixture.Catalog.Block = false;
            Click(window, "FindButton");
            await Wait(() => Text(window, "ResultText").Contains("Found 1 input", StringComparison.Ordinal));
            Assert.Equal(2, Control<ComboBox>(window, "InputChoice").Items.Count);
        }
        finally { fixture.Release.Set(); window.Close(); }
    });

    [Fact]
    public Task SessionLockStopsOwnedInputAndUnlockNeverRearms() => OnDispatcher(async () =>
    {
        using var fixture = new Fixture();
        fixture.Capture.Packet();
        var window = fixture.Open();
        try
        {
            await Ready(window);
            Click(window, "MicButton");
            await Wait(() => fixture.Capture.Reads > 0);
            fixture.Events.Signal(true);
            await Wait(() => !fixture.Runner.IsRunning);
            await Heartbeat();
            Assert.False(Control<Button>(window, "MicButton").IsEnabled);
            Assert.Equal(1, fixture.Capture.Opens);
            fixture.Events.Signal(false);
            await Wait(() => Control<Button>(window, "MicButton").IsEnabled);
            Assert.Equal(1, fixture.Capture.Opens);
            Assert.Equal(0, fixture.Output.Opens);
            Assert.Contains("Observation stopped", Text(window, "ResultText"));
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task OwnedWindowDeactivationCancelsThroughActualWpfEvent() => OnDispatcher(async () =>
    {
        using var fixture = new Fixture();
        fixture.Capture.Packet();
        var window = fixture.Open();
        try
        {
            await Ready(window);
            var handle = new WindowInteropHelper(window).Handle;
            Assert.NotEqual(IntPtr.Zero, handle);
            // WM_ACTIVATE targets only this test-owned HWND, not system/global keyboard or mouse input.
            SendMessage(handle, 0x0006, new IntPtr(1), IntPtr.Zero);
            Click(window, "MicButton");
            await Wait(() => fixture.Capture.Reads > 0);
            SendMessage(handle, 0x0006, IntPtr.Zero, IntPtr.Zero);
            await Wait(() => !fixture.Runner.IsRunning);
            await Wait(() => Text(window, "ResultText").Contains("Observation stopped", StringComparison.Ordinal));
            Assert.Contains("no longer live", Text(window, "LevelText"));
            Assert.Equal(1, fixture.Capture.Opens);
        }
        finally { window.Close(); }
    });

    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [Fact]
    public async Task RetiredHandleCannotStopNextEpochAndNoFramesIsNotPass()
    {
        using var fixture = new Fixture();
        var first = fixture.Audio.Start(AudioSetupAction.Microphone, AudioChoice.Default(true), true)!;
        await Wait(() => fixture.Capture.Reads > 0);
        fixture.Clock.Advance(TimeSpan.FromSeconds(5));
        await first.Worker.Completion.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(first.Status.Succeeded);
        Assert.Equal(AudioInputSignal.NoFrames, first.Status.Signal);
        Assert.Equal(0, first.Status.Samples);
        Assert.Null(first.Status.Rms);
        Assert.Null(first.Status.Checkpoint);
        fixture.Capture.Packet();
        var next = fixture.Audio.Start(AudioSetupAction.Microphone, AudioChoice.Default(true), true)!;
        await Wait(() => next.Status.Samples > 0);
        first.Stop();
        Assert.False(next.Worker.Completion.IsCompleted);
        Assert.True(fixture.Runner.IsRunning);
        next.Stop();
        await next.Worker.Completion.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(0, next.CaptureSnapshot!.RetainedPcmBytes);
    }

    [Theory]
    [InlineData(ErrorCode.AudioDeviceUnavailable)]
    [InlineData(ErrorCode.AudioDeviceLost)]
    [InlineData(ErrorCode.AudioFormatUnsupported)]
    public Task FailedOutputCannotBeConfirmedOrAutoReplaced(ErrorCode code) => OnDispatcher(async () =>
    {
        using var fixture = new Fixture();
        fixture.Output.Failure = code;
        var window = fixture.Open();
        try
        {
            await Ready(window);
            Click(window, "OutputButton");
            await Wait(() => Text(window, "ResultText").Contains("Local action failed", StringComparison.Ordinal));
            Assert.False(Control<Button>(window, "HeardButton").IsEnabled);
            Assert.Equal(1, fixture.Output.Opens);
            Assert.Equal(0, fixture.Capture.Opens);
            Assert.DoesNotContain("PRIVATE", Text(window, "ResultText"));
        }
        finally { window.Close(); }
    });

    [Fact]
    public async Task FailedDisposalKeepsSharedSlotQuarantinedAfterTerminal()
    {
        using var fixture = new Fixture();
        fixture.Output.FailDispose = true;
        var run = fixture.Audio.Start(AudioSetupAction.Output, AudioChoice.Default(false), true)!;
        await Wait(() => run.Status.Error == ErrorCode.AudioPlaybackFailed);
        Assert.False(run.Status.Released);
        Assert.True(fixture.Runner.IsRunning);
        Assert.False(run.Worker.Completion.IsCompleted);
        Assert.Null(fixture.Audio.Start(AudioSetupAction.Microphone, AudioChoice.Default(true), true));
        Assert.Equal(0, fixture.Capture.Opens);
        run.Stop();
    }

    [Fact]
    public async Task ExpiredReturnedOutputWithSuccessfulCleanupReleasesWorkerForFreshAction()
    {
        using var fixture = new Fixture();
        fixture.Output.BeforeReturn = () => fixture.Clock.Advance(TimeSpan.FromSeconds(6), fireTimers: false);
        // No observing window, Stop, or caller cancellation: only the owned post-open guard expires.
        var run = fixture.Audio.Start(AudioSetupAction.Output, AudioChoice.Default(false), true)!;
        await Wait(() => fixture.Output.Disposals == 1);
        Assert.Equal(0, fixture.Output.Starts);
        Assert.Empty(fixture.Output.Bytes);
        await run.Worker.Completion.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(fixture.Runner.IsRunning);
        Assert.True(run.Status.Released);
        Assert.False(run.Status.Succeeded);
        Assert.Null(run.Status.Checkpoint);
        fixture.Output.BeforeReturn = null;
        var fresh = fixture.Audio.Start(AudioSetupAction.Output, AudioChoice.Default(false), true);
        Assert.NotNull(fresh);
        await fresh.Worker.Completion.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(fresh.Status.Succeeded);
        Assert.Equal(2, fixture.Output.Opens);
    }

    [Fact]
    public async Task ExpiredReturnedOutputPreservesPreciseAuthorizationFailure()
    {
        using var fixture = new Fixture();
        fixture.Output.BeforeReturn = () => fixture.Clock.Advance(TimeSpan.FromSeconds(6), fireTimers: false);
        var run = fixture.Audio.Start(AudioSetupAction.Output, AudioChoice.Default(false), true)!;
        await Wait(() => run.Status.Finished);
        Assert.Equal(1, fixture.Output.Disposals);
        Assert.Equal(0, fixture.Output.Starts);
        Assert.Empty(fixture.Output.Bytes);
        Assert.Equal(ErrorCode.DeadlineExceeded, run.Status.Error);
    }

    [Fact]
    public async Task ExpiredReturnedOutputFailedCleanupRemainsQuarantined()
    {
        using var fixture = new Fixture();
        fixture.Output.BeforeReturn = () => fixture.Clock.Advance(TimeSpan.FromSeconds(6), fireTimers: false);
        fixture.Output.FailDispose = true;
        var run = fixture.Audio.Start(AudioSetupAction.Output, AudioChoice.Default(false), true)!;
        await Wait(() => run.Status.Finished);
        Assert.False(run.Status.Released);
        Assert.Equal(ErrorCode.AudioPlaybackFailed, run.Status.Error);
        Assert.Equal(0, fixture.Output.Starts);
        Assert.Empty(fixture.Output.Bytes);
        Assert.False(run.Worker.Completion.IsCompleted);
        Assert.Null(fixture.Audio.Start(AudioSetupAction.Output, AudioChoice.Default(false), true));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExpiredReturnedOutputWaitsForNoncooperativeCallbackBeforeRelease(bool callbackOutlivesDispose)
    {
        using var fixture = new Fixture();
        fixture.Output.Block = "OutputCallback";
        fixture.Output.CallbackOutlivesDispose = callbackOutlivesDispose;
        var run = fixture.Audio.Start(AudioSetupAction.Output, AudioChoice.Default(false), true)!;
        try
        {
            await Wait(() => run.Status.Stage.StartsWith("Opening selected output", StringComparison.Ordinal));
            fixture.Clock.Advance(TimeSpan.FromSeconds(6));
            await fixture.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await Wait(() =>
            {
                fixture.Clock.Advance(TimeSpan.FromMilliseconds(100));
                return run.Status.Stage.Contains("waiting for actual native release", StringComparison.Ordinal);
            });
            Assert.True(fixture.Runner.IsRunning);
            Assert.False(run.Worker.Completion.IsCompleted);
            Assert.Equal(callbackOutlivesDispose ? 1 : 0, fixture.Output.Disposals);
            Assert.Equal(0, fixture.Output.Starts);
            Assert.Empty(fixture.Output.Bytes);
            Assert.Null(fixture.Audio.Start(AudioSetupAction.Microphone, AudioChoice.Default(true), true));
            fixture.Release.Set();
            await run.Worker.Completion.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.True(run.Status.Released);
            Assert.Equal(ErrorCode.DeadlineExceeded, run.Status.Error);
            Assert.Equal(1, fixture.Output.Disposals);
        }
        finally { fixture.Release.Set(); }
    }

    [Fact]
    public async Task ExpiredReturnedOutputWaitsForActualDisposalBeforeRelease()
    {
        using var fixture = new Fixture();
        fixture.Output.Block = "OutputDispose";
        fixture.Output.BeforeReturn = () => fixture.Clock.Advance(TimeSpan.FromSeconds(6), fireTimers: false);
        var run = fixture.Audio.Start(AudioSetupAction.Output, AudioChoice.Default(false), true)!;
        try
        {
            await fixture.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.True(fixture.Runner.IsRunning);
            Assert.False(run.Worker.Completion.IsCompleted);
            Assert.Equal(0, fixture.Output.Disposals);
            Assert.Equal(0, fixture.Output.Starts);
            Assert.Empty(fixture.Output.Bytes);
            Assert.Null(fixture.Audio.Start(AudioSetupAction.Output, AudioChoice.Default(false), true));
            fixture.Release.Set();
            await run.Worker.Completion.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.True(run.Status.Released);
            Assert.Equal(ErrorCode.DeadlineExceeded, run.Status.Error);
        }
        finally { fixture.Release.Set(); }
    }

    [Theory]
    [InlineData(ErrorCode.DeadlineExceeded)]
    [InlineData(ErrorCode.AudioPlaybackFailed)]
    public async Task NativeOutputFailureCannotClaimOwnedExpiryCleanup(ErrorCode nativeFailure)
    {
        using var fixture = new Fixture();
        fixture.Output.Failure = nativeFailure;
        var run = fixture.Audio.Start(AudioSetupAction.Output, AudioChoice.Default(false), true)!;
        await Wait(() => run.Status.Finished);
        Assert.False(run.Status.Released);
        Assert.Equal(ErrorCode.AudioPlaybackFailed, run.Status.Error);
        Assert.Equal(0, fixture.Output.Disposals);
        Assert.Equal(0, fixture.Output.Starts);
        Assert.Empty(fixture.Output.Bytes);
        Assert.False(run.Worker.Completion.IsCompleted);
        Assert.Null(fixture.Audio.Start(AudioSetupAction.Output, AudioChoice.Default(false), true));
    }

    [Fact]
    public Task StaleAudioSavePreservesConcurrentSettingsAndRequiresReload() => OnDispatcher(async () =>
    {
        using var fixture = new Fixture();
        var window = fixture.Open();
        try
        {
            await Ready(window);
            var concurrent = SetupSettings.SelectRoute(SetupSettings.Begin(null), SetupRole.Llm, "new-model", null);
            Assert.True((await fixture.Store.SaveAsync(concurrent, null)).Saved);
            var bytes = await File.ReadAllBytesAsync(fixture.Store.FilePath);
            Click(window, "SaveButton");
            await Wait(() => Text(window, "ResultText").Contains("Settings changed", StringComparison.Ordinal));
            Assert.False(Control<Button>(window, "SaveButton").IsEnabled);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(fixture.Store.FilePath));
            Click(window, "ReloadButton");
            await Ready(window);
            Assert.Equal(0, fixture.Catalog.Calls);
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData(ErrorCode.AudioAccessDenied, "Privacy")]
    [InlineData(ErrorCode.AudioDeviceBusy, "competing")]
    [InlineData(ErrorCode.AudioDeviceUnavailable, "Reconnect")]
    [InlineData(ErrorCode.AudioDeviceLost, "Reconnect")]
    [InlineData(ErrorCode.AudioDeviceChanged, "No replacement")]
    [InlineData(ErrorCode.AudioFormatUnsupported, "format")]
    public Task InputFailureHasSpecificRemedyAndNeverRearms(ErrorCode code, string remedy) => OnDispatcher(async () =>
    {
        using var fixture = new Fixture();
        fixture.Capture.Failure = code;
        var window = fixture.Open();
        try
        {
            await Ready(window);
            Click(window, "MicButton");
            await Wait(() => Text(window, "ResultText").Contains("Local action failed", StringComparison.Ordinal));
            Assert.Contains(remedy, Text(window, "ResultText"), StringComparison.OrdinalIgnoreCase);
            Assert.False(Control<Button>(window, "HeardButton").IsEnabled);
            Assert.Equal(1, fixture.Capture.Opens);
            Assert.Equal(0, fixture.Output.Opens);
            Assert.Contains("Capture failed or canceled", Text(window, "LevelText"));
            Assert.DoesNotContain("SamplesReceived", Text(window, "StatusText"));
            Assert.DoesNotContain("PRIVATE", Text(window, "StatusText"));
        }
        finally { window.Close(); }
    });

    [Fact]
    public async Task PermissionFalseAndSharedFixtureOwnerRejectAudioWithoutAccess()
    {
        using var fixture = new Fixture();
        Assert.Null(fixture.Audio.Start(AudioSetupAction.Discovery, null, false));
        Assert.Null(fixture.Audio.Start(AudioSetupAction.Microphone, AudioChoice.Default(true), false));
        Assert.Null(fixture.Audio.Start(AudioSetupAction.Output, AudioChoice.Default(false), false));
        fixture.Audio.SetSessionLocked(true);
        Assert.Null(fixture.Audio.Start(AudioSetupAction.Microphone, AudioChoice.Default(true), true));
        fixture.Audio.SetSessionLocked(false);
        Assert.Equal(0, fixture.Capture.Opens);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fixtureOwner = fixture.Runner.TryStart(async _ => { await release.Task; return new(SetupWorkOutcome.Completed); })!;
        Assert.Null(fixture.Audio.Start(AudioSetupAction.Output, AudioChoice.Default(false), true));
        Assert.Equal(0, fixture.Catalog.Calls);
        Assert.Equal(0, fixture.Output.Opens);
        release.SetResult();
        await fixtureOwner.Completion;
    }

    private static T Control<T>(Window window, string name) => Assert.IsType<T>(window.FindName(name));
    private static string Text(Window window, string name) => Control<TextBox>(window, name).Text;
    private static void Click(Window window, string name) => Control<Button>(window, name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static Task Ready(Window window) => Wait(() => Control<Button>(window, "FindButton").IsEnabled);
    private static async Task Wait(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Bounded audio condition not reached.");
            await Task.Delay(10);
        }
    }
    private static async Task Heartbeat()
    {
        var heartbeat = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = Dispatcher.CurrentDispatcher.BeginInvoke(() => heartbeat.TrySetResult());
        await heartbeat.Task.WaitAsync(TimeSpan.FromSeconds(1));
    }
    private static async Task OnDispatcher(Func<Task> action)
    {
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.UnhandledException += (_, args) =>
            {
                args.Handled = true;
                finished.TrySetException(args.Exception);
                dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
            };
            _ = dispatcher.BeginInvoke(async () =>
            {
                try { await action(); finished.TrySetResult(); }
                catch (Exception error) { finished.TrySetException(error); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Send); }
            });
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.True(thread.Join(TimeSpan.FromSeconds(5)));
    }

    private sealed class Fixture : IDisposable
    {
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "Martlet.Audio.Dispatcher." + Guid.NewGuid().ToString("N"));
        public SettingsStore Store { get; }
        public SetupOperationRunner Runner { get; } = new();
        public ManualClock Clock { get; } = new();
        public ManualResetEventSlim Release { get; } = new();
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Catalog Catalog { get; }
        public Capture Capture { get; }
        public Output Output { get; }
        public SessionEvents Events { get; } = new();
        public AudioSetupService Audio { get; }
        public int Confirmations { get; private set; }
        public Fixture()
        {
            Store = new(Directory);
            Catalog = new(Release, Entered);
            Capture = new(Release, Entered);
            Output = new(Release, Entered);
            Audio = new(Runner, Catalog, Capture, Output, Clock);
        }
        public AudioSetupWindow Open(bool approve = true)
        {
            var window = new AudioSetupWindow(new SetupService(Store, new NoVault()), Runner, Audio, _ =>
            {
                Confirmations++;
                return approve;
            }, Clock,
                sessionEvents: Events) { ShowActivated = false, ShowInTaskbar = false };
            window.Show();
            return window;
        }
        public void ConfigureBlock(string value)
        {
            Catalog.Block = value == "Discovery";
            Capture.Block = value;
            Output.Block = value;
        }
        public void Dispose()
        {
            Release.Set();
            if (System.IO.Directory.Exists(Directory)) System.IO.Directory.Delete(Directory, true);
        }
    }

    private sealed class Catalog(ManualResetEventSlim release, TaskCompletionSource entered) : IAudioDeviceCatalog
    {
        public bool Block;
        public int Calls, Thread;
        public AudioDeviceList Discover(CancellationToken token)
        {
            Interlocked.Increment(ref Calls);
            Thread = Environment.CurrentManagedThreadId;
            if (Block) { entered.TrySetResult(); release.Wait(); }
            token.ThrowIfCancellationRequested();
            return new([new("PRIVATE-input", "Microphone", true)], [new("PRIVATE-output", "Headset", true)]);
        }
    }

    private sealed class Capture(ManualResetEventSlim release, TaskCompletionSource entered) : ICaptureDeviceFactory, ICaptureDevice
    {
        public string? Block;
        public ErrorCode? Failure;
        public bool FailureAfterPacket;
        public bool ObservePcmZeroing;
        public byte[]? ObservedPcm { get; private set; }
        public bool SawNonzeroPcm { get; private set; }
        public int Opens, Starts, Disposals, Reads;
        public int PacketCount => packets.Count;
        private CaptureRun? observedRun;
        public InputSelection? Selected;
        private CancellationTokenRegistration callback;
        private readonly ConcurrentQueue<byte[]> packets = new();
        public CaptureSourceFormat Format => new(16000, 1, 16, DeviceSampleEncoding.IntegerPcm);
        public void Packet(short amplitude = 16384, int count = 1)
        {
            for (var packet = 0; packet < count; packet++)
            {
                var bytes = new byte[3200];
                for (var i = 0; i < bytes.Length; i += 2) BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i), amplitude);
                packets.Enqueue(bytes);
            }
        }
        public ICaptureDevice Open(CaptureDeviceAccess access, CancellationToken token)
        {
            access.CheckAuthorization();
            Interlocked.Increment(ref Opens);
            Selected = access.Input;
            if (ObservePcmZeroing)
            {
                var check = Assert.IsType<Action>(typeof(CaptureDeviceAccess)
                    .GetField("check", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(access));
                observedRun = Assert.IsType<CaptureRun>(check.Target);
            }
            if (Block == "CaptureOpen") { entered.TrySetResult(); release.Wait(); }
            if (Block == "CaptureCallback")
                callback = token.Register(() => { entered.TrySetResult(); release.Wait(); });
            access.CheckAuthorization();
            return this;
        }
        public void Start(CancellationToken token) => Interlocked.Increment(ref Starts);
        public CapturePacket Read(Span<byte> destination, CancellationToken token)
        {
            Interlocked.Increment(ref Reads);
            if (observedRun is not null)
            {
                ObservedPcm ??= Assert.IsType<byte[]>(typeof(CaptureRun)
                    .GetField("pcm", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(observedRun));
                if (!SawNonzeroPcm) SawNonzeroPcm = ObservedPcm.Any(value => value != 0);
            }
            if (Block == "CaptureRead") { entered.TrySetResult(); release.Wait(); }
            if (Block == "CaptureReadAfterPacket" && packets.IsEmpty) { entered.TrySetResult(); release.Wait(); }
            if (Failure is { } code && (!FailureAfterPacket || packets.IsEmpty)) throw new CaptureDeviceException(code);
            if (!packets.TryDequeue(out var bytes)) return new(0);
            bytes.CopyTo(destination);
            return new(bytes.Length);
        }
        public void Stop() { }
        public void Dispose()
        {
            if (Block == "CaptureDispose") { entered.TrySetResult(); release.Wait(); }
            callback.Dispose();
            Interlocked.Increment(ref Disposals);
        }
    }

    private sealed class Output(ManualResetEventSlim release, TaskCompletionSource entered) : IPlaybackDeviceFactory, IPlaybackDevice
    {
        public string? Block;
        public ErrorCode? Failure;
        public bool FailDispose;
        public bool CallbackOutlivesDispose;
        public Action? BeforeReturn;
        public int Opens, Starts, Disposals;
        private CancellationTokenRegistration callback;
        public OutputSelection? Selected;
        public ConcurrentQueue<byte> Bytes { get; } = new();
        public PlaybackDeviceInfo Info => new(48000, 2, 32, DeviceSampleEncoding.IeeeFloat, 1200, true);
        public IPlaybackDevice Open(OutputSelection selection, PcmFormat format, CancellationToken token)
        {
            Selected = selection;
            if (Block == "OutputCallback")
                callback = token.Register(() => { entered.TrySetResult(); release.Wait(); });
            Interlocked.Increment(ref Opens);
            if (Block == "OutputOpen") { entered.TrySetResult(); release.Wait(); }
            BeforeReturn?.Invoke();
            if (Block == "OutputCallback") { entered.Task.GetAwaiter().GetResult(); }
            if (Failure is { } code) throw new ContractException(code, "PRIVATE native details");
            return this;
        }
        public int GetPadding(CancellationToken token) => 0;
        public int Write(ReadOnlySpan<byte> bytes, CancellationToken token)
        {
            foreach (var value in bytes) Bytes.Enqueue(value);
            return bytes.Length / 2;
        }
        public void Start(CancellationToken token) => Interlocked.Increment(ref Starts);
        public void StopAndReset() { }
        public void Dispose()
        {
            if (Block == "OutputDispose") { entered.TrySetResult(); release.Wait(); }
            if (CallbackOutlivesDispose) callback.Unregister();
            else callback.Dispose();
            if (FailDispose) throw new InvalidOperationException("PRIVATE native disposal failure");
            Interlocked.Increment(ref Disposals);
        }
    }

    private sealed class SessionEvents : IAudioSessionEvents
    {
        public event Action<bool>? LockedChanged;
        public void Signal(bool value) => LockedChanged?.Invoke(value);
        public void Dispose() { }
    }

    private sealed class NoVault : ICredentialStore
    {
        public CredentialReadResult Read(CredentialBinding binding) => throw new InvalidOperationException("Vault access forbidden");
        public CredentialError Write(CredentialBinding binding, SecretLease secret) => throw new InvalidOperationException("Vault access forbidden");
        public CredentialError Delete(CredentialBinding binding) => throw new InvalidOperationException("Vault access forbidden");
    }
}
