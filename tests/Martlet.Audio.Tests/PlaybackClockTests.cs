using Martlet.Core.Audio;

namespace Martlet.Audio.Tests;

public sealed class PlaybackClockTests
{
    private static PlaybackRequest Request(long epoch = 1, bool observe = true) => new(
        new() { SessionId = Guid.NewGuid(), TurnId = Guid.NewGuid(), RequestId = Guid.NewGuid() },
        epoch, new() { SampleRate = 24000, Channels = 1, Encoding = PcmEncoding.Signed16LittleEndian },
        new(OutputPolicy.DefaultAtStart), DateTimeOffset.UtcNow.AddSeconds(30))
        { ObserveDeviceClock = observe };

    private static PcmFrame Frame(PlaybackRequest request, long sequence = 0, long offset = 0) =>
        new(request.Ids, request.Epoch, sequence, offset, request.Format, new byte[2400]);

    private static async Task Until(Func<bool> condition)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(1, deadline.Token);
    }

    [Fact]
    public async Task Native_clock_is_opt_in_and_not_padding_or_submission()
    {
        var device = new ControlledDevice { AutoConsume = false };
        await using var sink = new PcmPlaybackSink(device, new() { Prebuffer = TimeSpan.Zero });
        var request = Request(observe: false);
        var off = sink.Start(request);
        off.Submit(Frame(request));
        await Until(() => device.Starts == 1);
        Assert.Equal(0, device.ClockReads);
        Assert.Equal(PlaybackClockState.NotRequested, off.DeviceClock.State);
        await off.StopAsync();
        await off.DeviceRelease;

        request = Request(2);
        var run = sink.Start(request);
        run.Submit(Frame(request));
        await Until(() => run.DeviceClock.State == PlaybackClockState.Available);
        Assert.Equal(1200, run.Snapshot.SubmittedSamples);
        Assert.Equal(0, run.Snapshot.DeviceConsumedSamples);
        Assert.Equal(0, run.DeviceClock.SampleOffset);
        device.ClockReading = new(1000, 48000, PlaybackClockOrigin.ControlledTest);
        await Until(() => run.DeviceClock.SampleOffset == 500);
        Assert.Equal(0, run.Snapshot.DeviceConsumedSamples);
        Assert.Equal(request.Ids, run.DeviceClock.Ids);
        Assert.Equal(PlaybackClockOrigin.ControlledTest, run.DeviceClock.Origin);
        Assert.Null(run.Snapshot.AudibleSamples);
        device.ClockReading = new(999, 48000, PlaybackClockOrigin.ControlledTest);
        await Until(() => run.DeviceClock.State == PlaybackClockState.Regressed);
        Assert.Null(run.DeviceClock.SampleOffset);
        Assert.False(run.Completion.IsCompleted);
        await run.StopAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Missing_or_failed_clock_does_not_fail_voice(bool throws)
    {
        var device = new ControlledDevice { AutoConsume = false, FailClock = throws, ClockReading = null };
        await using var sink = new PcmPlaybackSink(device, new() { Prebuffer = TimeSpan.Zero });
        var request = Request();
        var run = sink.Start(request);
        run.Submit(Frame(request));
        run.CompleteInput(1200);
        await Until(() => run.DeviceClock.State == PlaybackClockState.Unavailable);
        Assert.False(run.Completion.IsCompleted);
        device.Consume(1200);
        Assert.Equal(PlaybackState.Completed, (await run.Completion.WaitAsync(TimeSpan.FromSeconds(5))).State);
    }

    [Fact]
    public async Task Underrun_invalidates_source_clock_even_after_voice_resumes()
    {
        var device = new ControlledDevice { AutoConsume = false };
        await using var sink = new PcmPlaybackSink(device, new() { Prebuffer = TimeSpan.Zero });
        var request = Request();
        var run = sink.Start(request);
        run.Submit(Frame(request));
        await Until(() => run.DeviceClock.State == PlaybackClockState.Available);
        device.Consume(1200);
        await Until(() => run.DeviceClock.State == PlaybackClockState.Underrun);
        device.ClockReading = new(48000, 48000, PlaybackClockOrigin.ControlledTest);
        run.Submit(Frame(request, 1, 1200));
        run.CompleteInput(2400);
        await Until(() => run.Snapshot.SubmittedSamples == 2400);
        Assert.Null(run.DeviceClock.SampleOffset);
        device.Consume(1200);
        Assert.Equal(PlaybackState.Completed, (await run.Completion.WaitAsync(TimeSpan.FromSeconds(5))).State);
        Assert.Equal(PlaybackClockState.Underrun, run.DeviceClock.State);
    }

    [Fact]
    public async Task Empty_endpoint_with_queued_refill_invalidates_clock_before_new_commit()
    {
        var device = new ControlledDevice { AutoConsume = false };
        await using var sink = new PcmPlaybackSink(device, new() { Prebuffer = TimeSpan.Zero });
        var request = Request();
        var run = sink.Start(request);
        run.Submit(Frame(request));
        run.Submit(Frame(request, 1, 1200));
        await Until(() => run.DeviceClock.State == PlaybackClockState.Available);
        Assert.Equal(1200, run.Snapshot.SubmittedSamples);
        device.ClockReading = new(96000, 48000, PlaybackClockOrigin.ControlledTest);
        device.Consume(1200);
        await Until(() => run.Snapshot.SubmittedSamples == 2400);
        Assert.Equal(PlaybackClockState.Underrun, run.DeviceClock.State);
        Assert.Null(run.DeviceClock.SampleOffset);
        run.CompleteInput(2400);
        device.Consume(1200);
        Assert.Equal(PlaybackState.Completed, (await run.Completion.WaitAsync(TimeSpan.FromSeconds(5))).State);
    }
}
