using System.Buffers.Binary;
using System.Text;
using Martlet.Core.Audio;
using Martlet.Core.Contracts;

namespace Martlet.Audio.Tests;

public sealed class PlaybackTests
{
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(5);
    private static PcmFormat Format(int rate = 24000, int channels = 1) =>
        new() { SampleRate = rate, Channels = channels, Encoding = PcmEncoding.Signed16LittleEndian };
    private static PlaybackRequest Request(int rate = 24000, int channels = 1, long epoch = 0, TimeProvider? time = null) =>
        new(new() { SessionId = Guid.NewGuid(), TurnId = Guid.NewGuid(), RequestId = Guid.NewGuid() },
            epoch, Format(rate, channels), new(OutputPolicy.FixedEndpoint, "test-headset"),
            (time ?? TimeProvider.System).GetUtcNow().AddSeconds(60));
    private static PcmFrame Frame(PlaybackRequest request, long sequence = 0, long offset = 0, int? samples = null)
    {
        var data = new byte[(samples ?? request.Format.SampleRate / 10) * request.Format.BlockAlignment];
        for (var i = 0; i < data.Length; i += 2)
            BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(i), (short)((i + offset) % 65535 - 32768));
        return new(request.Ids, request.Epoch, sequence, offset, request.Format, data);
    }
    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(WaitLimit);
        while (!condition()) await Task.Delay(1, timeout.Token);
    }
    private static Task<PlaybackSnapshot> End(PlaybackRun run) => run.Completion.WaitAsync(WaitLimit);
    private static void AssertTerminal(PlaybackState expected, PlaybackSnapshot result, PlaybackRun run,
        ControlledDevice device) =>
        Assert.True(result.State == expected,
            $"Expected {expected}; terminal={result}; release={run.DeviceRelease.Status}; current={run.Snapshot}; " +
            $"opens={device.Opens}, starts={device.Starts}, stops={device.Stops}, disposals={device.Disposals}");

    [Fact]
    public async Task ConstructionAndDisposalDoNotOpenDevices()
    {
        var device = new ControlledDevice();
        await using (var sink = new PcmPlaybackSink(device)) Assert.Equal(0, device.Opens);
        Assert.Equal(0, device.Opens);
#if WINDOWS
        await using var real = new PcmPlaybackSink(new Windows.WasapiDeviceFactory());
        var smoke = await Windows.SpeakerSmoke.RunAsync();
        Assert.False(smoke.PlaybackRequested);
        Assert.Null(smoke.Playback);
#endif
    }

    [Theory]
    [InlineData(16000, 1)]
    [InlineData(16000, 2)]
    [InlineData(24000, 1)]
    [InlineData(24000, 2)]
    [InlineData(44100, 1)]
    [InlineData(44100, 2)]
    [InlineData(48000, 1)]
    [InlineData(48000, 2)]
    public async Task FormatsReachTheDeviceByteExactWithPartialWrites(int rate, int channels)
    {
        var device = new ControlledDevice { MaximumWriteSamples = rate / 100 };
        await using var sink = new PcmPlaybackSink(device);
        var request = Request(rate, channels);
        var run = sink.Start(request);
        var first = Frame(request);
        var second = Frame(request, 1, first.SamplesPerChannel);
        run.Submit(first);
        run.Submit(second);
        run.CompleteInput(first.SamplesPerChannel + second.SamplesPerChannel);
        var result = await End(run);
        Assert.Equal(PlaybackState.Completed, result.State);
        Assert.Equal(first.Data.ToArray().Concat(second.Data.ToArray()).ToArray(), device.Bytes);
        Assert.Equal(rate / 5, result.AcceptedSamples);
        Assert.Equal(result.AcceptedSamples, result.ReadSamples);
        Assert.Equal(result.AcceptedSamples, result.SubmittedSamples);
        Assert.Equal(result.AcceptedSamples, result.DeviceConsumedSamples);
        Assert.Equal(0, result.QueuedFrames);
        Assert.Equal(0, result.QueuedSamples);
        Assert.True(result.DeviceDrainObserved);
        Assert.True(result.DeviceReleased);
        Assert.Null(result.AudibleSamples);
        Assert.Equal(1, device.Starts);
        Assert.Equal(1, device.Stops);
        Assert.Equal(1, device.Disposals);
        Assert.Equal(request.Output, device.Selection);
        Assert.Equal(48000, (await run.Ready)!.MixSampleRate);
    }

    [Fact]
    public async Task PrebufferDoesNotPlayUntilThresholdAndEmptyCompletionDoesNotStart()
    {
        var device = new ControlledDevice();
        await using var sink = new PcmPlaybackSink(device);
        var request = Request();
        var run = sink.Start(request);
        await run.Ready.WaitAsync(WaitLimit);
        run.Submit(Frame(request));
        Assert.Equal(2400, run.Snapshot.QueuedSamples);
        Assert.Equal(0, device.Starts);
        run.CompleteInput(2400);
        Assert.Equal(PlaybackState.Completed, (await End(run)).State);
        var empty = sink.Start(Request(epoch: 1));
        empty.CompleteInput(0);
        var result = await End(empty);
        Assert.Equal(PlaybackState.Completed, result.State);
        Assert.Equal(0, result.SubmittedSamples);
        Assert.False(result.MayHavePlayed);
        Assert.Equal(1, device.Starts);
    }

    [Fact]
    public async Task QueuedAndWrittenAreNotCompletionOrAudibility()
    {
        var device = new ControlledDevice { AutoConsume = false };
        await using var sink = new PcmPlaybackSink(device, new() { Prebuffer = TimeSpan.Zero });
        var request = Request();
        var run = sink.Start(request);
        run.Submit(Frame(request, samples: 1200));
        run.CompleteInput(1200);
        await Until(() => run.Snapshot.SubmittedSamples == 1200);
        Assert.False(run.Completion.IsCompleted);
        Assert.Equal(0, run.Snapshot.DeviceConsumedSamples);
        Assert.Null(run.Snapshot.AudibleSamples);
        device.Consume(1200);
        Assert.Equal(PlaybackState.Completed, (await End(run)).State);
    }

    [Fact]
    public async Task FiveSecondLimitIncludesInFlightDeviceAudioAndOverflowIsTerminal()
    {
        var device = new ControlledDevice { AutoConsume = false };
        await using var sink = new PcmPlaybackSink(device);
        var request = Request();
        var run = sink.Start(request);
        for (var i = 0; i < 50; i++) run.Submit(Frame(request, i, i * 2400));
        await Until(() => run.Snapshot.SubmittedSamples == 1200);
        Assert.Equal(120000, run.Snapshot.AcceptedSamples);
        Assert.Equal(118800, run.Snapshot.QueuedSamples);
        Assert.Equal(0, run.Snapshot.DeviceConsumedSamples);
        var ex = Assert.Throws<ContractException>(() => run.Submit(Frame(request, 50, 120000)));
        Assert.Equal(ErrorCode.PayloadTooLarge, ex.Code);
        var result = await End(run);
        Assert.Equal(ErrorCode.PayloadTooLarge, result.Error!.Code);
        Assert.Equal(0, result.QueuedSamples);
        Assert.InRange(result.SubmittedSamples, 0, 1200);
    }

    [Fact]
    public async Task OnlyDeviceConsumptionReleasesAdmissionCapacity()
    {
        var device = new ControlledDevice { AutoConsume = false };
        await using var sink = new PcmPlaybackSink(device);
        var request = Request();
        var run = sink.Start(request);
        for (var i = 0; i < 50; i++) run.Submit(Frame(request, i, i * 2400));
        await Until(() => run.Snapshot.SubmittedSamples == 1200);
        device.Consume(1200);
        await Until(() => run.Snapshot.DeviceConsumedSamples == 1200);
        Assert.Equal(FrameAcceptance.Accepted, run.Submit(Frame(request, 50, 120000, 1200)));
        Assert.Equal(120000, run.Snapshot.AcceptedSamples - run.Snapshot.DeviceConsumedSamples);
        var result = await run.StopAsync();
        Assert.Equal(121200, result.AcceptedSamples);
        Assert.Equal(1200, result.DeviceConsumedSamples);
    }

    [Fact]
    public async Task TinyFramesAlsoHitObjectCountLimit()
    {
        var device = new ControlledDevice();
        await using var sink = new PcmPlaybackSink(device, new() { MaximumQueuedFrames = 2 });
        var request = Request();
        var run = sink.Start(request);
        run.Submit(Frame(request, 0, 0, 1));
        run.Submit(Frame(request, 1, 1, 1));
        Assert.Equal(ErrorCode.PayloadTooLarge,
            Assert.Throws<ContractException>(() => run.Submit(Frame(request, 2, 2, 1))).Code);
        Assert.Equal(2, (await End(run)).AcceptedSamples);
    }

    [Fact]
    public async Task ExactDuplicatesAreDiscardedBeforeAndAfterDeviceConsumption()
    {
        var device = new ControlledDevice();
        await using var sink = new PcmPlaybackSink(device, new() { Prebuffer = TimeSpan.Zero });
        var request = Request();
        var frame = Frame(request);
        var run = sink.Start(request);
        Assert.Equal(FrameAcceptance.Accepted, run.Submit(frame));
        Assert.Equal(FrameAcceptance.DuplicateDiscarded, run.Submit(frame));
        await Until(() => run.Snapshot.DeviceConsumedSamples == 2400);
        Assert.Equal(FrameAcceptance.DuplicateDiscarded, run.Submit(Frame(request)));
        run.CompleteInput(2400);
        Assert.Equal(2400, (await End(run)).SubmittedSamples);
        Assert.Equal(FrameAcceptance.StaleDiscarded, run.Submit(frame));
    }

    [Theory]
    [InlineData("sequence-gap")]
    [InlineData("sample-gap")]
    [InlineData("sample-overlap")]
    [InlineData("conflicting-duplicate")]
    [InlineData("correlation")]
    [InlineData("future-epoch")]
    [InlineData("format-rate")]
    [InlineData("format-channels")]
    public async Task UnsafeFrameChangesFailTheAttemptWithoutAcceptingMoreSamples(string fault)
    {
        var device = new ControlledDevice { BlockOpen = true };
        await using var sink = new PcmPlaybackSink(device);
        var request = Request(epoch: 1);
        var run = sink.Start(request);
        run.Submit(Frame(request));
        var bad = fault switch
        {
            "sequence-gap" => Frame(request, 2, 2400),
            "sample-gap" => Frame(request, 1, 2401),
            "sample-overlap" => Frame(request, 1, 2399),
            "conflicting-duplicate" => Frame(request, 0, 0, 1),
            "correlation" => Frame(Request(epoch: 1), 1, 2400),
            "future-epoch" => Frame(request with { Epoch = 2 }, 1, 2400),
            "format-rate" => Frame(request with { Format = Format(16000) }, 1, 2400),
            "format-channels" => Frame(request with { Format = Format(channels: 2) }, 1, 2400),
            _ => throw new ArgumentOutOfRangeException(nameof(fault))
        };
        Assert.Equal(ErrorCode.InvalidContract, Assert.Throws<ContractException>(() => run.Submit(bad)).Code);
        var result = await End(run);
        Assert.Equal(PlaybackState.Failed, result.State);
        Assert.Equal(2400, result.AcceptedSamples);
        Assert.Equal(0, result.SubmittedSamples);
    }

    [Fact]
    public async Task ReplaceFlushesAndRejectsOldEpochsIncludingLateProducerData()
    {
        var devices = new ControlledDevice();
        await using var sink = new PcmPlaybackSink(devices);
        var request = Request(epoch: 4);
        var old = sink.Start(request);
        old.Submit(Frame(request));
        var newerRequest = Request(epoch: 5);
        var newer = await sink.ReplaceAsync(newerRequest);
        Assert.Equal(PlaybackState.Replaced, (await End(old)).State);
        Assert.Equal(FrameAcceptance.StaleDiscarded, old.Submit(Frame(request)));
        Assert.Equal(FrameAcceptance.StaleDiscarded, newer.Submit(Frame(request)));
        Assert.Throws<ContractException>(() => sink.Start(request));
        newer.Submit(Frame(newerRequest, samples: 1));
        newer.CompleteInput(1);
        Assert.Equal(1, (await End(newer)).SubmittedSamples);
        Assert.Equal(1, devices.Samples);
        Assert.Throws<ContractException>(() => sink.Start(newerRequest));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StopOrCancellationInterruptsBlockedWriteAndDisposesOnItsWorker(bool externalCancellation)
    {
        var device = new ControlledDevice { BlockWrite = true };
        await using var sink = new PcmPlaybackSink(device, new() { Prebuffer = TimeSpan.Zero });
        using var cancel = new CancellationTokenSource();
        var request = Request();
        var run = sink.Start(request, cancel.Token);
        run.Submit(Frame(request));
        await device.EnteredWrite.Task.WaitAsync(WaitLimit);
        if (externalCancellation) cancel.Cancel();
        else _ = run.StopAsync();
        var result = await End(run);
        Assert.Equal(PlaybackState.Canceled, result.State);
        Assert.Equal(0, result.SubmittedSamples);
        Assert.True(result.MayHavePlayed);
        Assert.Equal(0, result.QueuedSamples);
        Assert.Equal(1, device.Disposals);
        Assert.Equal(1, device.Stops);
        Assert.Equal(0, device.Starts);
        Assert.Equal(FrameAcceptance.StaleDiscarded, run.Submit(Frame(request, 1, 2400)));
        Assert.False(run.CompleteInput(2400));
    }

    [Fact]
    public async Task DisposeCancelsBlockedOpenWithoutStartingAndIsIdempotent()
    {
        var time = new ManualTime();
        var device = new ControlledDevice { BlockOpen = true };
        // This checks cooperative cancellation, not whether host scheduling fits the shutdown deadline.
        var sink = new PcmPlaybackSink(device, timeProvider: time);
        var run = sink.Start(Request(time: time));
        try
        {
            await device.EnteredOpen.Task.WaitAsync(WaitLimit);
            await sink.DisposeAsync().AsTask().WaitAsync(WaitLimit);
            await sink.DisposeAsync().AsTask().WaitAsync(WaitLimit);
            Assert.Null(await run.Ready);
            var result = await End(run);
            AssertTerminal(PlaybackState.Canceled, result, run, device);
            Assert.Null(result.Error);
            Assert.True(result.DeviceReleased);
            Assert.Null(await run.DeviceRelease.WaitAsync(WaitLimit));
            Assert.Equal(1, device.Opens);
            Assert.Equal(0, device.Starts);
            Assert.Equal(0, device.Stops);
            Assert.Equal(0, device.Disposals);
            Assert.Throws<ObjectDisposedException>(() => sink.Start(Request(epoch: 1, time: time)));
        }
        finally
        {
            device.Release.Set();
            await sink.DisposeAsync().AsTask().WaitAsync(WaitLimit);
        }
    }

    [Theory]
    [InlineData("open", false)]
    [InlineData("open", true)]
    [InlineData("dispose", false)]
    [InlineData("dispose", true)]
    [InlineData("cancellation-handler", false)]
    [InlineData("cancellation-handler", true)]
    public async Task BlockedShutdownHonorsTwoSecondDeadlineAndQuarantinesUntilRelease(string blocked, bool expire)
    {
        var time = new ManualTime();
        var device = new ControlledDevice
        {
            BlockOpen = blocked == "open",
            IgnoreCancellation = blocked == "open",
            BlockDispose = blocked == "dispose",
            BlockOpenCancellation = blocked == "cancellation-handler"
        };
        await using var sink = new PcmPlaybackSink(device, timeProvider: time);
        var run = sink.Start(Request(time: time));
        PlaybackSnapshot result;
        try
        {
            await device.EnteredOpen.Task.WaitAsync(WaitLimit);
            if (blocked == "dispose") Assert.NotNull(await run.Ready.WaitAsync(WaitLimit));
            _ = run.StopAsync();
            if (blocked == "dispose") await device.EnteredDispose.Task.WaitAsync(WaitLimit);
            if (blocked == "cancellation-handler") await device.EnteredCancellation.Task.WaitAsync(WaitLimit);
            await Until(() => time.TimerCount == 2);
            time.Advance(TimeSpan.FromMilliseconds(1999));
            Assert.False(run.Completion.IsCompleted);
            Assert.False(run.DeviceRelease.IsCompleted);
            Assert.False(run.Snapshot.DeviceReleased);
            Assert.Throws<InvalidOperationException>(() => sink.Start(Request(epoch: 1, time: time)));

            if (expire)
            {
                time.Advance(TimeSpan.FromMilliseconds(1));
                result = await End(run);
                AssertTerminal(PlaybackState.Failed, result, run, device);
                Assert.Equal(ErrorCode.AudioPlaybackFailed, result.Error!.Code);
                Assert.False(result.DeviceReleased);
                Assert.False(run.DeviceRelease.IsCompleted);
                if (blocked != "dispose") Assert.Null(await run.Ready.WaitAsync(WaitLimit));
                Assert.Throws<InvalidOperationException>(() => sink.Start(Request(epoch: 1, time: time)));
            }
        }
        finally { device.Release.Set(); }

        result = await End(run);
        AssertTerminal(expire ? PlaybackState.Failed : PlaybackState.Canceled, result, run, device);
        if (!expire) Assert.Null(result.Error);
        Assert.Null(await run.DeviceRelease.WaitAsync(WaitLimit));
        Assert.True(run.Snapshot.DeviceReleased);
        Assert.Equal(!expire, result.DeviceReleased);
        Assert.Equal(0, device.Starts);
        Assert.Equal(blocked == "dispose" ? 1 : 0, device.Stops);
        Assert.Equal(blocked == "dispose" ? 1 : 0, device.Disposals);
        if (blocked != "dispose") Assert.Null(await run.Ready);
        var events = new List<PlaybackEvent>();
        using var readTimeout = new CancellationTokenSource(WaitLimit);
        await foreach (var item in run.Events.ReadAllAsync(readTimeout.Token))
            events.Add(item);
        Assert.Contains(events, item => item.Kind == PlaybackEventKind.DeviceReleased && item.Snapshot.DeviceReleased);
        var terminal = Assert.Single(events, item => item.Kind == PlaybackEventKind.Terminal);
        Assert.Equal(result, terminal.Snapshot);
        var next = sink.Start(Request(epoch: 1, time: time));
        next.CompleteInput(0);
        Assert.Equal(PlaybackState.Completed, (await End(next)).State);
    }

    [Fact]
    public async Task InterruptedPartialWriteKeepsCommittedCountSeparateFromTheDiscardedRemainder()
    {
        var device = new ControlledDevice { MaximumWriteSamples = 5, BlockAfterSamples = 5 };
        await using var sink = new PcmPlaybackSink(device, new() { Prebuffer = TimeSpan.Zero });
        var request = Request();
        var run = sink.Start(request);
        run.Submit(Frame(request, samples: 20));
        await device.EnteredBlockedWrite.Task.WaitAsync(WaitLimit);
        var result = await run.StopAsync().WaitAsync(WaitLimit);
        Assert.Equal(PlaybackState.Canceled, result.State);
        Assert.Equal(20, result.AcceptedSamples);
        Assert.Equal(20, result.ReadSamples);
        Assert.Equal(5, result.SubmittedSamples);
        Assert.Equal(5, result.DeviceConsumedSamples);
        Assert.Equal(5, device.Samples);
        Assert.Equal(1, device.Stops);
        Assert.Equal(1, device.Disposals);
        Assert.False(result.DeviceDrainObserved);
    }

    [Fact]
    public async Task PreCanceledStartAndReplacementDoNotOpenOrStopAnExistingRun()
    {
        var device = new ControlledDevice();
        await using var sink = new PcmPlaybackSink(device);
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        Assert.Throws<OperationCanceledException>(() => sink.Start(Request(), cancel.Token));
        Assert.Equal(0, device.Opens);
        var run = sink.Start(Request());
        await run.Ready.WaitAsync(WaitLimit);
        await Assert.ThrowsAsync<OperationCanceledException>(() => sink.ReplaceAsync(Request(epoch: 1), cancel.Token));
        Assert.Equal(0, device.Stops);
        Assert.False(run.Completion.IsCompleted);
    }

    [Fact]
    public async Task UncooperativeNativeWriteTimesOutQuarantinesAndCannotRestartFromALateReturn()
    {
        var time = new ManualTime();
        var device = new ControlledDevice { BlockWrite = true, IgnoreCancellation = true };
        await using var sink = new PcmPlaybackSink(device, new() { Prebuffer = TimeSpan.Zero }, time);
        var request = Request(time: time);
        var run = sink.Start(request);
        run.Submit(Frame(request));
        await device.EnteredWrite.Task.WaitAsync(WaitLimit);
        _ = run.StopAsync();
        await Until(() => time.TimerCount >= 2);
        time.Advance(TimeSpan.FromSeconds(2));
        try
        {
            var result = await End(run);
            Assert.Equal(PlaybackState.Failed, result.State);
            Assert.Equal(ErrorCode.AudioPlaybackFailed, result.Error!.Code);
            Assert.False(result.DeviceReleased);
            Assert.Equal(0, result.SubmittedSamples);
            Assert.True(result.MayHavePlayed);
            Assert.Throws<InvalidOperationException>(() => sink.Start(Request(epoch: 1, time: time)));
            Assert.Equal(FrameAcceptance.StaleDiscarded, run.Submit(Frame(request, 1, 2400)));
        }
        finally { device.Release.Set(); }
        Assert.Null(await run.DeviceRelease.WaitAsync(WaitLimit));
        Assert.Equal(0, device.Starts);
        Assert.Equal(1, device.Disposals);
        Assert.Equal(0, run.Snapshot.SubmittedSamples);
        var next = sink.Start(Request(epoch: 1, time: time));
        next.CompleteInput(0);
        Assert.Equal(PlaybackState.Completed, (await End(next)).State);
    }

    [Fact]
    public async Task FailedNativeDisposalPreventsReopeningEvenWhenTheWorkerReturned()
    {
        var device = new ControlledDevice { FailDispose = true };
        await using var sink = new PcmPlaybackSink(device);
        var run = sink.Start(Request());
        await run.Ready.WaitAsync(WaitLimit);
        var result = await run.StopAsync().WaitAsync(WaitLimit);
        Assert.Equal(ErrorCode.AudioPlaybackFailed, result.Error!.Code);
        Assert.False(result.DeviceReleased);
        Assert.Throws<InvalidOperationException>(() => sink.Start(Request(epoch: 1)));
    }

    [Fact]
    public async Task StopIsInstanceScoped()
    {
        var a = new ControlledDevice();
        var b = new ControlledDevice { BlockWrite = true };
        await using var first = new PcmPlaybackSink(a);
        await using var second = new PcmPlaybackSink(b, new() { Prebuffer = TimeSpan.Zero });
        var request = Request();
        var runA = first.Start(Request());
        var runB = second.Start(request);
        runB.Submit(Frame(request, samples: 10));
        await b.EnteredWrite.Task.WaitAsync(WaitLimit);
        await runA.StopAsync();
        Assert.False(runB.Completion.IsCompleted);
        Assert.Equal(0, b.Stops);
        runB.CompleteInput(10);
        b.Release.Set();
        Assert.Equal(PlaybackState.Completed, (await End(runB)).State);
    }

    [Fact]
    public async Task OldDuplicatesOutsideTheBoundedHistoryFailRatherThanReplay()
    {
        var device = new ControlledDevice();
        await using var sink = new PcmPlaybackSink(device, new() { Prebuffer = TimeSpan.Zero });
        var request = Request();
        var run = sink.Start(request);
        for (var i = 0; i < 257; i++)
        {
            run.Submit(Frame(request, i, i, 1));
            if (i % 64 == 63) await Until(() => run.Snapshot.ReadSamples == i + 1);
        }
        Assert.Equal(ErrorCode.InvalidContract, Assert.Throws<ContractException>(() => run.Submit(Frame(request, samples: 1))).Code);
        Assert.Equal(257, (await End(run)).AcceptedSamples);
    }

    [Fact]
    public async Task EventBacklogIsBoundedAndReportsLossWithTerminalStateOutsideTheRing()
    {
        var device = new ControlledDevice { MaximumWriteSamples = 1 };
        await using var sink = new PcmPlaybackSink(device, new() { Prebuffer = TimeSpan.Zero });
        var request = Request();
        var run = sink.Start(request);
        run.Submit(Frame(request, samples: 100));
        run.CompleteInput(100);
        var result = await End(run);
        Assert.Equal(100, result.DeviceConsumedSamples);
        Assert.True(result.DroppedEvents > 0);
        var history = new List<PlaybackEvent>();
        while (run.Events.TryRead(out var item)) history.Add(item);
        Assert.InRange(history.Count, 1, 128);
        Assert.Contains(history, e => e.Kind == PlaybackEventKind.Terminal);
        Assert.True(history.Zip(history.Skip(1), (a, b) => a.Sequence < b.Sequence).All(v => v));
    }

    [Fact]
    public async Task UnderrunRecoveryUsesContiguousDataAndHasADeterministicDeadline()
    {
        var time = new ManualTime();
        var device = new ControlledDevice();
        await using var sink = new PcmPlaybackSink(device, new() { Prebuffer = TimeSpan.Zero }, time);
        var request = Request(time: time);
        var run = sink.Start(request);
        run.Submit(Frame(request, samples: 10));
        await Until(() => run.Snapshot.State == PlaybackState.Underrun);
        // A voice slower than real time keeps its pause: several seconds without audio is still a pause, not the end.
        time.Advance(TimeSpan.FromSeconds(9));
        Assert.False(run.Completion.IsCompleted);
        run.Submit(Frame(request, 1, 10, 10));
        await Until(() => run.Snapshot.DeviceConsumedSamples == 20 && run.Snapshot.State == PlaybackState.Underrun);
        time.Advance(TimeSpan.FromSeconds(10));
        var result = await End(run);
        Assert.Equal(PlaybackState.Failed, result.State);
        Assert.Equal(ErrorCode.StreamTruncated, result.Error!.Code);
        Assert.Equal(20, result.SubmittedSamples);
        Assert.Equal(2, result.Underruns);
        Assert.InRange(result.UnderrunTime, TimeSpan.FromSeconds(19), TimeSpan.FromSeconds(20));
        var kinds = new List<PlaybackEventKind>();
        while (run.Events.TryRead(out var item)) kinds.Add(item.Kind);
        Assert.Contains(PlaybackEventKind.Resumed, kinds);
        Assert.Equal(2, kinds.Count(k => k == PlaybackEventKind.Underrun));
    }

    [Fact]
    public async Task VoiceSlowerThanRealTimePausesBetweenBurstsAndPlaysEverySample()
    {
        var time = new ManualTime();
        var device = new ControlledDevice();
        await using var sink = new PcmPlaybackSink(device, new() { Prebuffer = TimeSpan.Zero }, time);
        var request = Request(time: time);
        var run = sink.Start(request);
        // Bursts of audio with longer gaps between them than the audio lasts, like a busy self-hosted engine streaming.
        for (var burst = 0; burst < 3; burst++)
        {
            run.Submit(Frame(request, burst, burst * 2_400L, 2_400));
            await Until(() => run.Snapshot.DeviceConsumedSamples == (burst + 1) * 2_400L && run.Snapshot.State == PlaybackState.Underrun);
            time.Advance(TimeSpan.FromSeconds(3));
            Assert.False(run.Completion.IsCompleted);
        }
        run.CompleteInput(7_200);
        var result = await End(run);
        Assert.Equal(PlaybackState.Completed, result.State);
        Assert.Null(result.Error);
        Assert.Equal(7_200, result.DeviceConsumedSamples);
        Assert.Equal(3, result.Underruns);
        Assert.InRange(result.UnderrunTime, TimeSpan.FromSeconds(9), TimeSpan.FromSeconds(10));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(21)]
    public void UnderrunRecoveryIsBounded(int seconds)
    {
        var device = new ControlledDevice();
        Assert.ThrowsAny<Exception>(() => new PcmPlaybackSink(device, new() { UnderrunTimeout = TimeSpan.FromSeconds(seconds) }));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FirstAudioAndOverallDeadlinesStopEvenWhileOpenIsBlocked(bool overall)
    {
        var time = new ManualTime();
        var device = new ControlledDevice { BlockOpen = true };
        await using var sink = new PcmPlaybackSink(device, timeProvider: time);
        var request = Request(time: time);
        if (overall) request = request with { Deadline = time.GetUtcNow().AddSeconds(2) };
        var run = sink.Start(request);
        await device.EnteredOpen.Task.WaitAsync(WaitLimit);
        time.Advance(TimeSpan.FromSeconds(overall ? 2 : 20));
        var result = await End(run);
        Assert.Equal(ErrorCode.DeadlineExceeded, result.Error!.Code);
        Assert.Equal(0, result.SubmittedSamples);
    }

    [Theory]
    [InlineData(ErrorCode.AudioDeviceUnavailable)]
    [InlineData(ErrorCode.AudioDeviceLost)]
    [InlineData(ErrorCode.AudioFormatUnsupported)]
    public async Task OpenFailuresAreSanitizedAndNeverBecomeSuccess(ErrorCode code)
    {
        await using var sink = new PcmPlaybackSink(new ControlledDevice { OpenError = code });
        var run = sink.Start(Request());
        var result = await End(run);
        Assert.Equal(PlaybackState.Failed, result.State);
        Assert.Equal(code, result.Error!.Code);
        Assert.DoesNotContain("PRIVATE", result.Error.Summary);
        Assert.Null(await run.Ready);
    }

    [Fact]
    public async Task LostDeviceAfterPartialPlaybackNeverFallsBack()
    {
        var device = new ControlledDevice { AutoConsume = false };
        await using var sink = new PcmPlaybackSink(device, new() { Prebuffer = TimeSpan.Zero });
        var request = Request() with { Output = new(OutputPolicy.DefaultAtStart) };
        var run = sink.Start(request);
        run.Submit(Frame(request));
        await Until(() => run.Snapshot.SubmittedSamples == 1200);
        device.PaddingError = ErrorCode.AudioDeviceLost;
        var result = await End(run);
        Assert.Equal(ErrorCode.AudioDeviceLost, result.Error!.Code);
        Assert.Equal(1200, result.SubmittedSamples);
        Assert.True(result.MayHavePlayed);
        Assert.False(result.DeviceDrainObserved);
        Assert.Null(result.AudibleSamples);
        Assert.Equal(1, device.Opens);
        Assert.Equal(1, device.Disposals);
    }

    [Fact]
    public async Task TruncatedCompletionAndCleanupFailureStayFailed()
    {
        var device = new ControlledDevice { FailCleanup = true };
        await using var sink = new PcmPlaybackSink(device);
        var run = sink.Start(Request());
        await run.Ready.WaitAsync(WaitLimit);
        Assert.Equal(ErrorCode.StreamTruncated, Assert.Throws<ContractException>(() => run.CompleteInput(1)).Code);
        var result = await End(run);
        Assert.Equal(PlaybackState.Failed, result.State);
        Assert.Equal(ErrorCode.AudioPlaybackFailed, result.Error!.Code);
        Assert.Equal(1, device.Disposals);
        Assert.DoesNotContain("PRIVATE", result.Error.Summary);
    }

    [Theory]
    [InlineData("buffer")]
    [InlineData("padding")]
    [InlineData("write")]
    public async Task InvalidDeviceAccountingIsRejected(string fault)
    {
        var device = new ControlledDevice
        {
            CapacityOverride = fault == "buffer" ? 24000 : null,
            InvalidPadding = fault == "padding" ? 1 : null,
            ReturnInvalidWriteCount = fault == "write"
        };
        await using var sink = new PcmPlaybackSink(device, new() { Prebuffer = TimeSpan.Zero });
        var request = Request();
        var run = sink.Start(request);
        run.Submit(Frame(request));
        var result = await End(run);
        Assert.Equal(PlaybackState.Failed, result.State);
        Assert.Equal(fault == "buffer" ? ErrorCode.AudioFormatUnsupported : ErrorCode.AudioPlaybackFailed, result.Error!.Code);
        Assert.Equal(1, device.Disposals);
    }

    [Fact]
    public async Task ExistingPcmBoundaryRejectsMisalignmentAndUnsupportedInputs()
    {
        await using var sink = new PcmPlaybackSink(new ControlledDevice());
        var request = Request(channels: 2);
        Assert.Throws<ContractException>(() => new PcmFrame(request.Ids, 0, 0, 0, request.Format, new byte[3]));
        Assert.Throws<ContractException>(() => new PcmFrame(request.Ids, 0, 0, 0, request.Format, new byte[19201]));
        Assert.Throws<ContractException>(() => new PcmFrame(request.Ids, 0, 0, 0, request.Format, new byte[10000]));
        Assert.Throws<ContractException>(() => sink.Start(request with { Format = Format(22050) }));
        Assert.Throws<ContractException>(() => sink.Start(request with { Format = Format(channels: 3) }));
        Assert.Throws<ContractException>(() => sink.Start(request with { Output = new(OutputPolicy.FixedEndpoint) }));
    }

    [Theory]
    [InlineData(ErrorCode.AudioDeviceUnavailable, "audio_device_unavailable")]
    [InlineData(ErrorCode.AudioDeviceLost, "audio_device_lost")]
    [InlineData(ErrorCode.AudioFormatUnsupported, "audio_format_unsupported")]
    [InlineData(ErrorCode.AudioPlaybackFailed, "audio_playback_failed")]
    [InlineData(ErrorCode.DeadlineExceeded, "deadline_exceeded")]
    public void AdditiveErrorCodesKeepCanonicalCoreJsonAndActionableMappings(ErrorCode code, string token)
    {
        var error = PlaybackErrors.Create(code);
        error.Validate();
        var json = ContractJson.Write(error);
        Assert.Contains($"\"{token}\"", Encoding.UTF8.GetString(json));
        Assert.Equal(error, ContractJson.Read<MartletError>(json));
        Assert.Equal(Stage.Playback, error.Stage);
        Assert.StartsWith("audio.", error.ActionId);
        var invalid = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(json).Replace(token, token.ToUpperInvariant()));
        Assert.Throws<ContractException>(() => ContractJson.Read<MartletError>(invalid));
        Assert.Equal(10, (int)ErrorCode.StreamTruncated);
    }
}
