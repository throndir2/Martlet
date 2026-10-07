using System.Buffers.Binary;
using Martlet.Core.Audio;

namespace Martlet.Audio.Tests;

public sealed class PlaybackPauseTests
{
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(5);
    private static PlaybackRequest Request() =>
        new(new() { SessionId = Guid.NewGuid(), TurnId = Guid.NewGuid(), RequestId = Guid.NewGuid() }, 0,
            new() { SampleRate = 24000, Channels = 1, Encoding = PcmEncoding.Signed16LittleEndian },
            new(OutputPolicy.FixedEndpoint, "test-headset"), DateTimeOffset.UtcNow.AddSeconds(60));
    private static PcmFrame Frame(PlaybackRequest request, long sequence, long offset, int samples, short value)
    {
        var data = new byte[samples * 2];
        for (var i = 0; i < data.Length; i += 2) BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(i), value);
        return new(request.Ids, request.Epoch, sequence, offset, request.Format, data);
    }
    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(WaitLimit);
        while (!condition()) await Task.Delay(1, timeout.Token);
    }
    private static short Sample(byte[] bytes, int index) => BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(index * 2));

    [Fact]
    public async Task A_paused_run_writes_nothing_counts_no_underrun_and_resumes_from_the_same_sample_with_a_short_fade_in()
    {
        var device = new ControlledDevice { AutoConsume = false };
        await using var sink = new PcmPlaybackSink(device, new() { Prebuffer = TimeSpan.Zero });
        var request = Request();
        var run = sink.Start(request);
        Assert.Equal(FrameAcceptance.Accepted, run.Submit(Frame(request, 0, 0, 2400, 1000)));
        await Until(() => device.Samples >= 1200);
        Assert.True(run.Pause());
        Assert.False(run.Pause());
        Assert.True(run.Paused);
        // The device plays what it already took, then runs dry: nothing more is read while paused, and it isn't an underrun.
        device.Consume(int.MaxValue);
        await Task.Delay(60);
        var before = device.Samples;
        device.Consume(int.MaxValue);
        await Task.Delay(60);
        Assert.Equal(before, device.Samples);
        Assert.Equal(0, run.Snapshot.Underruns);
        Assert.Equal(PlaybackState.Playing, run.Snapshot.State);
        // Input is still accepted while paused.
        Assert.Equal(FrameAcceptance.Accepted, run.Submit(Frame(request, 1, 2400, 2400, 1000)));
        Assert.True(run.PausedTime > TimeSpan.Zero);
        Assert.True(run.Resume());
        Assert.False(run.Resume());
        Assert.True(run.CompleteInput(4800));
        while (!run.Completion.IsCompleted)
        {
            device.Consume(1200);
            await Task.Delay(1);
        }
        var result = await run.Completion.WaitAsync(WaitLimit);
        Assert.Equal(PlaybackState.Completed, result.State);
        Assert.Equal(4800, device.Samples);
        Assert.Equal(0, result.Underruns);
        var bytes = device.Bytes;
        Assert.Equal(1000, Sample(bytes, before - 1));
        // Resumed from the exact sample, fading in over ResumeFade (10 ms, 240 samples at 24 kHz).
        Assert.True(Sample(bytes, before) < 50);
        Assert.True(Sample(bytes, before + 120) is > 400 and < 600);
        Assert.Equal(1000, Sample(bytes, before + 240));
        Assert.Equal(1000, Sample(bytes, 4799));
    }

    [Fact]
    public async Task Time_paused_before_the_first_audio_never_counts_against_the_first_audio_limit()
    {
        var device = new ControlledDevice();
        await using var sink = new PcmPlaybackSink(device, new() { Prebuffer = TimeSpan.Zero, FirstAudioTimeout = TimeSpan.FromMilliseconds(150) });
        var request = Request();
        var run = sink.Start(request);
        Assert.True(run.Pause());
        run.Submit(Frame(request, 0, 0, 2400, 500));
        await Task.Delay(400);
        Assert.False(run.Completion.IsCompleted);
        Assert.Equal(0, device.Samples);
        Assert.True(run.Resume());
        run.CompleteInput(2400);
        var result = await run.Completion.WaitAsync(WaitLimit);
        Assert.Equal(PlaybackState.Completed, result.State);
        Assert.Equal(2400, device.Samples);
    }

    [Fact]
    public async Task A_stopped_run_cannot_pause_or_resume()
    {
        var device = new ControlledDevice();
        await using var sink = new PcmPlaybackSink(device);
        var run = sink.Start(Request());
        Assert.True(run.Pause());
        await run.StopAsync().WaitAsync(WaitLimit);
        Assert.False(run.Resume());
        Assert.False(run.Pause());
    }
}
