using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Martlet.Core.Contracts;
using static Martlet.Audio.Tests.CaptureNormalizerTests;

namespace Martlet.Audio.Tests;

public sealed class CaptureTests
{
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(5);
    private static readonly Guid Session = Guid.Parse("08a97245-e36f-48b9-8fd3-42c9c7d62e11");
    private static CaptureRequest Request(CaptureClock? clock = null, long epoch = 0, double seconds = 30) =>
        new(new() { SessionId = Session, TurnId = Guid.NewGuid(), RequestId = Guid.NewGuid() }, epoch,
            new(InputPolicy.FixedEndpoint, "private-microphone-canary"), TimeSpan.FromSeconds(seconds),
            (clock?.GetUtcNow() ?? DateTimeOffset.UtcNow).AddSeconds(seconds));
    internal static async Task Until(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(WaitLimit);
        while (!predicate()) await Task.Delay(1, timeout.Token);
    }
    private static CaptureRun Press(MicrophoneCapture capture, CaptureRequest request) => capture.Press(request, new(request, true));
    private static Task<CaptureSnapshot> End(CaptureRun run) => run.Completion.WaitAsync(WaitLimit);

    [Fact]
    public async Task ConstructorsAndMissingAuthorizationNeverTouchADevice()
    {
        var device = new ControlledCapture();
        await using var capture = new MicrophoneCapture(Session, device);
        var request = Request();
        Assert.Equal(ErrorCode.NotConfigured, Assert.Throws<CaptureDeviceException>(() => capture.Press(request, null)).Code);
        Assert.Throws<CaptureDeviceException>(() => capture.Press(request, new(request)));
        Assert.Throws<CaptureDeviceException>(() => capture.Press(request, new(request with { Epoch = 1 }, true)));
        Assert.Throws<CaptureDeviceException>(() => capture.Press(request, new(request with { Input = new(InputPolicy.FixedEndpoint, "different") }, true)));
        Assert.Throws<CaptureDeviceException>(() => capture.Press(request, new(request with { MaximumDuration = TimeSpan.FromSeconds(1) }, true)));
        Assert.Throws<CaptureDeviceException>(() => capture.Press(request, new(request with { Ids = request.Ids with { TurnId = Guid.NewGuid() } }, true)));
        await capture.DisposeAsync();
        Assert.Empty(device.Calls);
#if WINDOWS
        await using var real = new MicrophoneCapture(Session, new Windows.WasapiCaptureDeviceFactory());
        await using var monitor = new InputDeviceMonitor(new Windows.WasapiInputDeviceDiscoveryFactory());
#endif
    }

    [Fact]
    public async Task PressReleaseTransfersOneOwnedImmutableUtteranceWithExactIdsAndFrames()
    {
        var device = new ControlledCapture();
        await using var capture = new MicrophoneCapture(Session, device);
        var request = Request();
        var run = Press(capture, request);
        await run.Ready.WaitAsync(WaitLimit);
        var bytes = Pcm(Enumerable.Range(0, 650).Select(i => (short)(i - 300)).ToArray());
        device.Packets.Enqueue(bytes[..3]);
        device.Packets.Enqueue(bytes[3..1007]);
        device.Packets.Enqueue(bytes[1007..]);
        await Until(() => run.Snapshot.CanonicalSamples == 650);
        var frameBuffer = new byte[640];
        Assert.True(run.TryCopyMonoFrame(0, frameBuffer));
        Assert.Equal(bytes[..640], frameBuffer);
        var ending = run.ReleaseAsync();
        Assert.Same(ending, run.ReleaseAsync());
        var snapshot = await End(run);
        Assert.Equal(CaptureState.Completed, snapshot.State);
        Assert.Equal(CaptureEndReason.Released, snapshot.EndReason);
        Assert.Equal(650, snapshot.SourceSamples);
        Assert.Equal(650, snapshot.CanonicalSamples);
        Assert.Equal(1300, snapshot.RetainedPcmBytes);
        Assert.True((await run.DeviceRelease).Released);
        using var utterance = run.TakeUtterance()!;
        Assert.NotNull(utterance);
        Assert.Null(run.TakeUtterance());
        Assert.Equal(0, run.Snapshot.RetainedPcmBytes);
        Assert.Equal(request.Ids, utterance.Ids);
        Assert.Equal(request.Epoch, utterance.Epoch);
        Assert.Equal(16000, CapturedUtterance.Format.SampleRate);
        var copy = new byte[utterance.ByteCount];
        utterance.CopyPcmTo(copy);
        Assert.Equal(bytes, copy);
        var firstFrame = utterance.GetFrame(0);
        Assert.True(MemoryMarshal.TryGetArray(firstFrame.Data, out var array));
        array.Array![array.Offset] ^= 0xff;
        Assert.Equal(bytes[..640], utterance.GetFrame(0).Data.ToArray());
        Assert.Equal(640, utterance.GetFrame(2).SampleOffset);
        Assert.Equal(10, utterance.GetFrame(2).SamplesPerChannel);
        Assert.False(run.TryCopyMonoFrame(0, frameBuffer));
        Assert.All(frameBuffer, value => Assert.Equal(0, value));
        Assert.Equal(1, device.Starts);
        Assert.Equal(1, device.Stops);
        Assert.Equal(1, device.Disposals);
        Assert.Single(device.Calls.Select(c => c.Thread).Distinct());
        if (OperatingSystem.IsWindows()) Assert.All(device.Calls, c => Assert.Equal(ApartmentState.MTA, c.Apartment));
        utterance.Dispose();
        Assert.Throws<ObjectDisposedException>(() => utterance.CopyPcmTo(copy));
    }

    [Fact]
    public async Task NoPacketsDiffersFromRealZeroValuedFramesAndNoSpeechIsNotInferred()
    {
        var device = new ControlledCapture();
        await using var capture = new MicrophoneCapture(Session, device);
        var empty = Press(capture, Request());
        await empty.Ready.WaitAsync(WaitLimit);
        Assert.Equal(CaptureState.NoFrames, (await empty.ReleaseAsync()).State);
        Assert.Null(empty.TakeUtterance());
        var silence = Press(capture, Request(epoch: 1));
        await silence.Ready.WaitAsync(WaitLimit);
        device.Packets.Enqueue(new byte[640]);
        await Until(() => silence.Snapshot.CanonicalSamples == 320);
        Assert.Equal(CaptureState.Completed, (await silence.ReleaseAsync()).State);
        using var utterance = silence.TakeUtterance();
        Assert.Equal(320, utterance!.SampleCount);
        Assert.Equal(new PcmAmplitude(0, 0, 0), utterance.MeasureAmplitude(0.01));
    }

    [Theory]
    [InlineData((short)128)]
    [InlineData((short)328)]
    [InlineData(short.MinValue)]
    public async Task CompletedLeaseMeasuresWholeCanonicalPcmWithoutAnAdditionalBuffer(short level)
    {
        var device = new ControlledCapture();
        await using var capture = new MicrophoneCapture(Session, device);
        var run = Press(capture, Request());
        await run.Ready.WaitAsync(WaitLimit);
        device.Packets.Enqueue(Pcm(Enumerable.Repeat(level, 320).ToArray()));
        await Until(() => run.Snapshot.CanonicalSamples == 320);
        Assert.Equal(CaptureState.Completed, (await run.ReleaseAsync()).State);
        var utterance = run.TakeUtterance()!;
        var expected = Math.Abs(level / 32768.0);
        Assert.Throws<ArgumentOutOfRangeException>(() => utterance.MeasureAmplitude(double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => utterance.MeasureAmplitude(0));
        Assert.Equal(new PcmAmplitude(expected, expected, level == 128 ? 0 : 320), utterance.MeasureAmplitude(0.01));
        Assert.Equal(0, run.Snapshot.RetainedPcmBytes);
        utterance.Dispose();
        Assert.Throws<ObjectDisposedException>(() => utterance.MeasureAmplitude(0.01));
    }

    [Theory]
    [InlineData(CaptureEndReason.Stopped)]
    [InlineData(CaptureEndReason.Muted)]
    [InlineData(CaptureEndReason.Paused)]
    [InlineData(CaptureEndReason.Locked)]
    [InlineData(CaptureEndReason.Disposed)]
    [InlineData(CaptureEndReason.CallerCanceled)]
    public async Task StopControlsClearSensitiveStorageAndNeverRestart(CaptureEndReason reason)
    {
        var device = new ControlledCapture { Format = new(48000, 1, 16, DeviceSampleEncoding.IntegerPcm) };
        await using var capture = new MicrophoneCapture(Session, device);
        using var cancel = new CancellationTokenSource();
        var request = Request();
        var run = capture.Press(request, new(request, true), cancel.Token);
        await run.Ready.WaitAsync(WaitLimit);
        device.Packets.Enqueue(Pcm(Enumerable.Repeat((short)1234, 480).ToArray()));
        await Until(() => run.Snapshot.SourceSamples == 480);
        var privatePcm = (byte[])typeof(CaptureRun).GetField("pcm", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(run)!;
        Assert.Contains(privatePcm, value => value != 0);
        switch (reason)
        {
            case CaptureEndReason.Stopped: await capture.StopAsync(); break;
            case CaptureEndReason.Muted: await capture.SetMutedAsync(true); break;
            case CaptureEndReason.Paused: await capture.SetPausedAsync(true); break;
            case CaptureEndReason.Locked: await capture.SetSessionLockedAsync(true); break;
            case CaptureEndReason.Disposed: await capture.DisposeAsync(); break;
            case CaptureEndReason.CallerCanceled: cancel.Cancel(); break;
        }
        var snapshot = await End(run);
        Assert.Equal(CaptureState.Canceled, snapshot.State);
        Assert.Equal(reason, snapshot.EndReason);
        Assert.Null(run.TakeUtterance());
        Assert.All(privatePcm, value => Assert.Equal(0, value));
        Assert.Equal(0, snapshot.RetainedPcmBytes);
        Assert.Equal(1, device.Opens);
        Assert.Equal(1, device.Starts);
        if (reason is CaptureEndReason.Muted or CaptureEndReason.Paused or CaptureEndReason.Locked)
            Assert.Throws<InvalidOperationException>(() => Press(capture, Request(epoch: 1)));
        await capture.SetMutedAsync(false);
        await capture.SetPausedAsync(false);
        await capture.SetSessionLockedAsync(false);
        Assert.Equal(1, device.Opens);
    }

    [Fact]
    public async Task CancellationCanDiscardSealedButUnclaimedUtterance()
    {
        var device = new ControlledCapture();
        await using var capture = new MicrophoneCapture(Session, device);
        var run = Press(capture, Request());
        await run.Ready.WaitAsync(WaitLimit);
        device.Packets.Enqueue(Pcm(1234));
        await Until(() => run.Snapshot.CanonicalSamples == 1);
        await run.ReleaseAsync();
        await capture.SetMutedAsync(true);
        Assert.Null(run.TakeUtterance());
    }

    [Fact]
    public async Task MuteDuringSealedNativeCleanupWinsOverReleaseAndClearsAudioImmediately()
    {
        using var barrier = new ManualResetEventSlim();
        var device = new ControlledCapture { DisposeBlock = barrier };
        await using var capture = new MicrophoneCapture(Session, device);
        var run = Press(capture, Request());
        try
        {
            await run.Ready.WaitAsync(WaitLimit);
            device.Packets.Enqueue(Pcm(1234));
            await Until(() => run.Snapshot.CanonicalSamples == 1);
            var ending = run.ReleaseAsync();
            await Until(() => device.DisposeEntered.IsSet);
            var muted = capture.SetMutedAsync(true);
            Assert.Equal(0, run.Snapshot.RetainedPcmBytes);
            barrier.Set();
            var snapshot = await ending.WaitAsync(WaitLimit);
            await muted.WaitAsync(WaitLimit);
            Assert.Equal(CaptureState.Canceled, snapshot.State);
            Assert.Equal(CaptureEndReason.Muted, snapshot.EndReason);
            Assert.Null(run.TakeUtterance());
        }
        finally { barrier.Set(); }
    }

    [Fact]
    public async Task FreshEpochIsRequiredAndOldRunCannotSupplyNextTurnFrames()
    {
        var device = new ControlledCapture();
        await using var capture = new MicrophoneCapture(Session, device);
        var firstRequest = Request();
        var first = Press(capture, firstRequest);
        await first.Ready.WaitAsync(WaitLimit);
        await first.CancelAsync();
        Assert.Throws<ContractException>(() => Press(capture, firstRequest));
        var secondRequest = Request(epoch: 1);
        var second = Press(capture, secondRequest);
        await second.Ready.WaitAsync(WaitLimit);
        await first.ReleaseAsync();
        await first.CancelAsync();
        device.Packets.Enqueue(Pcm(4321));
        await Until(() => second.Snapshot.CanonicalSamples == 1);
        await second.ReleaseAsync();
        using var result = second.TakeUtterance();
        Assert.Equal(secondRequest.Ids, result!.Ids);
        Assert.Null(first.TakeUtterance());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MonotonicDeadlineStopsEvenWhenTimerDeliveryIsDelayed(bool blockRead)
    {
        var clock = new CaptureClock();
        using var barrier = new ManualResetEventSlim();
        var device = new ControlledCapture { ReadBlock = blockRead ? barrier : null };
        await using var capture = new MicrophoneCapture(Session, device, timeProvider: clock);
        var run = Press(capture, Request(clock, seconds: 1) with { ExpiresAt = clock.GetUtcNow().AddSeconds(30) });
        await run.Ready.WaitAsync(WaitLimit);
        await Until(() => device.ReadEntered.IsSet);
        clock.Advance(TimeSpan.FromSeconds(2), deliverTimers: false);
        device.Packets.Enqueue(Pcm(123));
        barrier.Set();
        var snapshot = await End(run);
        Assert.Equal(CaptureState.NoFrames, snapshot.State);
        Assert.Equal(CaptureEndReason.DurationLimit, snapshot.EndReason);
        Assert.Equal(0, snapshot.CanonicalSamples);
        Assert.Null(run.TakeUtterance());
    }

    [Fact]
    public async Task AbortTimerBoundsBlockedOpenWithoutLateStartAndQuarantinesUntilRealRelease()
    {
        var clock = new CaptureClock();
        using var barrier = new ManualResetEventSlim();
        var device = new ControlledCapture { OpenBlock = barrier };
        await using var capture = new MicrophoneCapture(Session, device, timeProvider: clock);
        var run = Press(capture, Request(clock, seconds: 1));
        try
        {
            await Until(() => device.OpenEntered.IsSet);
            clock.Advance(TimeSpan.FromSeconds(1));
            await Until(() => clock.TimerCount >= 2);
            clock.Advance(TimeSpan.FromSeconds(2));
            var snapshot = await End(run);
            Assert.Equal(CaptureState.Failed, snapshot.State);
            Assert.Equal(ErrorCode.AudioCaptureFailed, snapshot.Error!.Code);
            Assert.False(run.DeviceRelease.IsCompleted);
            Assert.Throws<InvalidOperationException>(() => Press(capture, Request(clock, epoch: 1)));
            Assert.Equal(0, device.Starts);
            var events = new List<CaptureEvent>();
            await foreach (var item in run.Events.ReadAllAsync()) events.Add(item);
            Assert.Equal(CaptureEventKind.Terminal, events[^1].Kind);
            barrier.Set();
            Assert.True((await run.DeviceRelease.WaitAsync(WaitLimit)).Released);
            Assert.Equal(snapshot, run.Snapshot);
            Assert.False(run.Events.TryRead(out _));
            Assert.Equal(0, device.Starts);
            Assert.Equal(1, device.Disposals);
        }
        finally { barrier.Set(); }
    }

    [Fact]
    public async Task ReleaseDuringBlockedOpenProducesNoFramesAndNeverStarts()
    {
        using var barrier = new ManualResetEventSlim();
        var device = new ControlledCapture { OpenBlock = barrier };
        await using var capture = new MicrophoneCapture(Session, device);
        var run = Press(capture, Request());
        try
        {
            await Until(() => device.OpenEntered.IsSet);
            var end = run.ReleaseAsync();
            barrier.Set();
            Assert.Equal(CaptureState.NoFrames, (await end.WaitAsync(WaitLimit)).State);
            Assert.Equal(0, device.Starts);
            Assert.True((await run.DeviceRelease).Released);
        }
        finally { barrier.Set(); }
    }

    [Fact]
    public async Task LateReadAfterCancelIsDiscardedBeforeANewEpochCanOpen()
    {
        using var barrier = new ManualResetEventSlim();
        var device = new ControlledCapture { ReadBlock = barrier };
        await using var capture = new MicrophoneCapture(Session, device);
        var run = Press(capture, Request());
        try
        {
            await run.Ready.WaitAsync(WaitLimit);
            await Until(() => device.ReadEntered.IsSet);
            device.Packets.Enqueue(Pcm(12345));
            var ending = run.CancelAsync();
            Assert.Throws<InvalidOperationException>(() => Press(capture, Request(epoch: 1)));
            barrier.Set();
            Assert.Equal(CaptureState.Canceled, (await ending.WaitAsync(WaitLimit)).State);
            Assert.Equal(0, run.Snapshot.CanonicalSamples);
            Assert.Null(run.TakeUtterance());
            var next = Press(capture, Request(epoch: 1));
            await next.Ready.WaitAsync(WaitLimit);
            Assert.Equal(CaptureState.NoFrames, (await next.ReleaseAsync()).State);
            Assert.Null(next.TakeUtterance());
        }
        finally { barrier.Set(); }
    }

    [Fact]
    public async Task BlockingNativeCancellationHandlerCannotHoldStopOrFreeTheWorkerSlot()
    {
        var clock = new CaptureClock();
        using var barrier = new ManualResetEventSlim();
        using var entered = new ManualResetEventSlim();
        var device = new ControlledCapture();
        await using var capture = new MicrophoneCapture(Session, device, timeProvider: clock);
        var run = Press(capture, Request(clock));
        await run.Ready.WaitAsync(WaitLimit);
        using var registration = device.NativeToken.Register(() => { entered.Set(); barrier.Wait(); });
        try
        {
            var stopping = run.CancelAsync();
            await Until(() => entered.IsSet && clock.TimerCount >= 2);
            clock.Advance(TimeSpan.FromSeconds(2));
            Assert.Equal(CaptureState.Failed, (await stopping.WaitAsync(WaitLimit)).State);
            Assert.False(run.DeviceRelease.IsCompleted);
            Assert.Throws<InvalidOperationException>(() => Press(capture, Request(clock, epoch: 1)));
        }
        finally
        {
            barrier.Set();
            Assert.True((await run.DeviceRelease.WaitAsync(WaitLimit)).Released);
        }
    }

    [Fact]
    public async Task StopFailureDoesNotHideSuccessfulDisposalOrPretendAnUtteranceCompleted()
    {
        var device = new ControlledCapture { StopFailure = new Exception("private stop error") };
        await using var capture = new MicrophoneCapture(Session, device);
        var run = Press(capture, Request());
        await run.Ready.WaitAsync(WaitLimit);
        device.Packets.Enqueue(Pcm(123));
        await Until(() => run.Snapshot.CanonicalSamples == 1);
        var result = await run.ReleaseAsync();
        Assert.Equal(CaptureState.Failed, result.State);
        Assert.Equal(ErrorCode.AudioCaptureFailed, result.Error!.Code);
        Assert.True((await run.DeviceRelease).Released);
        Assert.Null(run.TakeUtterance());
        Assert.Equal(1, device.Disposals);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeCleanupFailureAndNoncooperationAreNotReportedReleased(bool blocks)
    {
        var clock = new CaptureClock();
        using var barrier = new ManualResetEventSlim();
        var device = new ControlledCapture
        {
            DisposeBlock = blocks ? barrier : null,
            DisposeFailure = blocks ? null : new InvalidOperationException("private-native-message")
        };
        await using var capture = new MicrophoneCapture(Session, device, timeProvider: clock);
        var run = Press(capture, Request(clock));
        try
        {
            await run.Ready.WaitAsync(WaitLimit);
            var ending = run.CancelAsync();
            await Until(() => device.DisposeEntered.IsSet);
            if (blocks)
            {
                await Until(() => clock.TimerCount >= 2);
                clock.Advance(TimeSpan.FromSeconds(2));
            }
            var result = await ending.WaitAsync(WaitLimit);
            Assert.Equal(CaptureState.Failed, result.State);
            Assert.Equal(ErrorCode.AudioCaptureFailed, result.Error!.Code);
            Assert.Throws<InvalidOperationException>(() => Press(capture, Request(clock, epoch: 1)));
            if (blocks) Assert.False(run.DeviceRelease.IsCompleted);
            else Assert.False((await run.DeviceRelease).Released);
        }
        finally
        {
            barrier.Set();
            await run.DeviceRelease.WaitAsync(WaitLimit);
        }
    }

    [Theory]
    [InlineData(ErrorCode.AudioDeviceUnavailable)]
    [InlineData(ErrorCode.AudioDeviceLost)]
    [InlineData(ErrorCode.AudioAccessDenied)]
    [InlineData(ErrorCode.AudioDeviceBusy)]
    [InlineData(ErrorCode.AudioFormatUnsupported)]
    [InlineData(ErrorCode.AudioDeviceChanged)]
    public async Task NativeFailuresAreActionableAndNeverBecomeSilence(ErrorCode code)
    {
        var device = new ControlledCapture { OpenFailure = new CaptureDeviceException(code) };
        await using var capture = new MicrophoneCapture(Session, device);
        var run = Press(capture, Request());
        var result = await End(run);
        Assert.Equal(CaptureState.Failed, result.State);
        Assert.Equal(code, result.Error!.Code);
        Assert.Equal(Stage.Capture, result.Error.Stage);
        Assert.False(result.Error.Retryable);
        Assert.Null(run.TakeUtterance());
        Assert.True((await run.DeviceRelease).Released);
        Assert.Equal(0, device.Starts);
    }

    [Theory]
    [InlineData(ErrorCode.AudioDeviceLost)]
    [InlineData(ErrorCode.AudioDeviceChanged)]
    [InlineData(ErrorCode.AudioFormatUnsupported)]
    public async Task LossAndFormatChurnDiscardPartialCaptureWithoutReconnect(ErrorCode code)
    {
        var device = new ControlledCapture();
        await using var capture = new MicrophoneCapture(Session, device);
        var run = Press(capture, Request());
        await run.Ready.WaitAsync(WaitLimit);
        device.Packets.Enqueue(Pcm(5000));
        await Until(() => run.Snapshot.CanonicalSamples == 1);
        device.ReadFailure = new CaptureDeviceException(code);
        var result = await End(run);
        Assert.Equal(code, result.Error!.Code);
        Assert.Null(run.TakeUtterance());
        device.ReadFailure = null;
        device.Packets.Enqueue(Pcm(111));
        Assert.Equal(1, device.Opens);
        Assert.Equal(result, run.Snapshot);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DurationAndByteLimitsSealExactlyWithoutExceedingBound(bool byteLimit)
    {
        var clock = new CaptureClock();
        var device = new ControlledCapture();
        var options = new CaptureOptions { MaximumPcmBytes = byteLimit ? 642 : 2 * 1024 * 1024 };
        await using var capture = new MicrophoneCapture(Session, device, options, clock);
        var run = Press(capture, Request(clock, seconds: 0.02));
        // A 20 ms request is the smaller bound; use a longer request for the independent byte bound.
        if (byteLimit)
        {
            await run.CancelAsync();
            run = Press(capture, Request(clock, epoch: 1, seconds: 1));
        }
        await run.Ready.WaitAsync(WaitLimit);
        device.Packets.Enqueue(Pcm(Enumerable.Repeat((short)1000, 1600).ToArray()));
        var result = await End(run);
        Assert.Equal(CaptureState.Completed, result.State);
        Assert.Equal(byteLimit ? CaptureEndReason.ByteLimit : CaptureEndReason.DurationLimit, result.EndReason);
        Assert.Equal(byteLimit ? 321 : 320, result.CanonicalSamples);
        using var utterance = run.TakeUtterance();
        Assert.Equal(result.CanonicalSamples * 2, utterance!.ByteCount);
    }

    [Fact]
    public async Task ThirtySecondSourceBoundIsEnforcedWithoutWallClockAdvance()
    {
        var clock = new CaptureClock();
        var device = new ControlledCapture { Format = new(44100, 2, 32, DeviceSampleEncoding.IeeeFloat) };
        await using var capture = new MicrophoneCapture(Session, device, timeProvider: clock);
        var run = Press(capture, Request(clock));
        await run.Ready.WaitAsync(WaitLimit);
        var packet = Float(Enumerable.Repeat(0.25f, 4410 * 2).ToArray());
        for (var i = 0; i < 302; i++) device.Packets.Enqueue(packet);
        var result = await End(run);
        Assert.Equal(CaptureState.Completed, result.State);
        Assert.Equal(CaptureEndReason.DurationLimit, result.EndReason);
        Assert.Equal(44100 * 30, result.SourceSamples);
        Assert.Equal(16000 * 30, result.CanonicalSamples);
        using var utterance = run.TakeUtterance();
        Assert.Equal(960000, utterance!.ByteCount);
        Assert.True(result.DroppedEvents > 0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OversizedOrLostPacketsFailAndEraseAudio(bool discontinuity)
    {
        var device = new ControlledCapture { Discontinuous = discontinuity, Oversized = !discontinuity };
        device.Packets.Enqueue(Pcm(1234));
        await using var capture = new MicrophoneCapture(Session, device);
        var run = Press(capture, Request());
        var result = await End(run);
        Assert.Equal(discontinuity ? ErrorCode.StreamTruncated : ErrorCode.PayloadTooLarge, result.Error!.Code);
        Assert.Null(run.TakeUtterance());
        Assert.Equal(0, result.RetainedPcmBytes);
    }

    [Fact]
    public async Task OddFinalSourceFragmentIsTruncationNotSuccessfulAudio()
    {
        var device = new ControlledCapture();
        await using var capture = new MicrophoneCapture(Session, device);
        var run = Press(capture, Request());
        await run.Ready.WaitAsync(WaitLimit);
        device.Packets.Enqueue(new byte[] { 1, 2, 3 });
        await Until(() => run.Snapshot.CanonicalSamples == 1);
        Assert.Equal(ErrorCode.StreamTruncated, (await run.ReleaseAsync()).Error!.Code);
        Assert.Null(run.TakeUtterance());
    }

    [Fact]
    public async Task MetadataRingContainsOnlyRealMetersAndSanitizedFields()
    {
        var device = new ControlledCapture();
        await using var capture = new MicrophoneCapture(Session, device);
        var run = Press(capture, Request());
        await run.Ready.WaitAsync(WaitLimit);
        var events = new List<CaptureEvent>();
        while (run.Events.TryRead(out var item)) events.Add(item);
        Assert.DoesNotContain(events, item => item.Kind == CaptureEventKind.Meter);
        device.Packets.Enqueue(Pcm(16384, -16384));
        await Until(() => run.Snapshot.CanonicalSamples == 2);
        device.ReadFailure = new InvalidOperationException("secret exception canary");
        await End(run);
        await foreach (var item in run.Events.ReadAllAsync()) events.Add(item);
        var meter = Assert.Single(events, e => e.Kind == CaptureEventKind.Meter);
        Assert.Equal(0.5, meter.Peak);
        Assert.Equal(0.5, meter.Rms);
        var json = JsonSerializer.Serialize(events);
        Assert.DoesNotContain("private-microphone-canary", json);
        Assert.DoesNotContain("secret exception canary", json);
        Assert.DoesNotContain("EndpointId", json);
        Assert.DoesNotContain("Pcm", json.Replace("RetainedPcmBytes", ""));
        Assert.Equal(events.Select(e => e.Sequence).OrderBy(s => s), events.Select(e => e.Sequence));
    }

    [Fact]
    public async Task HalfDuplexRequestsCoordinationButNeverStopsAnotherPlayer()
    {
        var device = new ControlledCapture();
        await using var capture = new MicrophoneCapture(Session, device);
        await capture.SetOwnOutputActiveAsync(true);
        Assert.Equal(CaptureArmingDecision.OwnOutputSuppressed, capture.ArmingDecision(false));
        Assert.Equal(CaptureArmingDecision.PlaybackStopRequired, capture.ArmingDecision(true));
        Assert.Throws<InvalidOperationException>(() => Press(capture, Request()));
        Assert.Equal(0, device.Opens);
        await capture.SetOwnOutputActiveAsync(false);
        Assert.Equal(CaptureArmingDecision.Allowed, capture.ArmingDecision(true));
        Assert.Equal(0, device.Opens);
        var run = Press(capture, Request());
        await run.Ready.WaitAsync(WaitLimit);
        await capture.SetOwnOutputActiveAsync(true);
        Assert.Equal(CaptureState.Canceled, (await End(run)).State);
    }

    [Fact]
    public async Task CompetingPressesCannotCreateTwoNativeWorkers()
    {
        var device = new ControlledCapture();
        await using var capture = new MicrophoneCapture(Session, device);
        var request = Request();
        var attempts = Enumerable.Range(0, 16).Select(_ => Task.Run(() =>
        {
            try { return Press(capture, request); }
            catch (ContractException) { return null; }
        }));
        var run = Assert.Single((await Task.WhenAll(attempts)).OfType<CaptureRun>());
        await run.Ready.WaitAsync(WaitLimit);
        await Task.WhenAll(Enumerable.Range(0, 16).Select(i => i % 2 == 0 ? run.ReleaseAsync() : run.CancelAsync()));
        Assert.Equal(1, device.Opens);
        Assert.Equal(1, device.Disposals);
    }

    [Fact]
    public async Task InvalidRequestAndLimitsAreRejectedBeforeDeviceAccess()
    {
        var device = new ControlledCapture();
        var clock = new CaptureClock();
        await using var capture = new MicrophoneCapture(Session, device, timeProvider: clock);
        var request = Request(clock);
        foreach (var invalid in new[]
        {
            request with { Epoch = -1 }, request with { Epoch = (long)int.MaxValue + 1 },
            request with { Ids = request.Ids with { SessionId = Guid.NewGuid() } },
            request with { MaximumDuration = TimeSpan.FromSeconds(31) },
            request with { ExpiresAt = clock.GetUtcNow() },
            request with { Input = new(InputPolicy.FixedEndpoint) },
            request with { Input = new(InputPolicy.FollowDefaultOnNextPress, "not-allowed") }
        })
            Assert.Throws<ContractException>(() => Press(capture, invalid));
        foreach (var options in new[]
        {
            new CaptureOptions { MaximumDuration = TimeSpan.FromSeconds(31) },
            new CaptureOptions { MaximumPcmBytes = 2 * 1024 * 1024 + 2 },
            new CaptureOptions { MaximumPcmBytes = 3 },
            new CaptureOptions { ShutdownTimeout = TimeSpan.FromSeconds(3) }
        })
            Assert.Throws<ContractException>(() => new MicrophoneCapture(Session, device, options));
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        Assert.Throws<OperationCanceledException>(() => capture.Press(request, new(request, true), canceled.Token));
        Assert.Equal(0, device.Opens);
    }

    [Theory]
    [InlineData(ErrorCode.AudioCaptureFailed, "audio_capture_failed")]
    [InlineData(ErrorCode.AudioAccessDenied, "audio_access_denied")]
    [InlineData(ErrorCode.AudioDeviceBusy, "audio_device_busy")]
    [InlineData(ErrorCode.AudioDeviceChanged, "audio_device_changed")]
    public void AppendedCaptureErrorsHaveCanonicalCoreJson(ErrorCode code, string serialized)
    {
        var error = CaptureErrors.Create(code);
        error.Validate();
        var json = ContractJson.Write(error);
        Assert.Contains($"\"code\": \"{serialized}\"", Encoding.UTF8.GetString(json));
        Assert.Contains("\"stage\": \"capture\"", Encoding.UTF8.GetString(json));
        Assert.Equal(error, ContractJson.Read<MartletError>(json));
        Assert.Equal(15, (int)ErrorCode.DeadlineExceeded);
        Assert.Equal(7, (int)Stage.Provider);
        Assert.Equal(8, (int)Stage.Capture);
    }

    [Theory]
    [InlineData(ErrorCode.AudioDeviceLost)]
    [InlineData(ErrorCode.AudioDeviceChanged)]
    public void DeviceChangeSummariesFitListeningThatOpensTheMicrophoneAgain(ErrorCode code)
    {
        // Always listening opens the microphone again by itself, and a Windows reset after a format change is AudioDeviceLost.
        var error = CaptureErrors.Create(code);
        error.Validate();
        Assert.Contains("This recording was discarded", error.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("will not restart", error.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("press again", error.Summary, StringComparison.Ordinal);
    }
}
