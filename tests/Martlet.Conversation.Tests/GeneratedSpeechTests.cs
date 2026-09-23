using Martlet.Audio.Tests;
using Martlet.Providers.Tests;

namespace Martlet.Conversation.Tests;

public sealed class GeneratedSpeechTests
{
    [Fact]
    public async Task Real_generated_pcm_tee_uses_playback_identity_and_disable_never_stops_voice()
    {
        using var observer = new GeneratedSpeechObserver();
        var device = new ControlledDevice { AutoConsume = false };
        await using var harness = new Harness(device, generatedSpeech: observer);
        harness.Answer("One generated segment.");
        observer.Enable();
        var turn = harness.Start();
        var segment = await observer.Segments.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        var chunks = new List<byte>();
        await foreach (var frame in segment.Frames.ReadAllAsync())
        {
            Assert.Equal(segment.Playback.Snapshot.Ids, frame.Ids);
            Assert.Equal(segment.Playback.Snapshot.Epoch, frame.Epoch);
            Assert.Equal(turn.TurnId, frame.Ids.TurnId);
            chunks.AddRange(frame.Data.ToArray());
        }
        Assert.True(segment.InputCompleted);
        Assert.Equal(SpeechFixtures.Audio(), chunks.ToArray());
        observer.Disable();
        Assert.Equal(SpeechObservationFailure.Disabled, segment.Failure);
        Assert.False(turn.Completion.IsCompleted);
        device.AutoConsume = true;
        Assert.Equal(ConversationState.Completed, (await Harness.Finish(turn)).State);
        Assert.Equal(SpeechFixtures.Audio(), device.Bytes);
    }

    [Fact]
    public async Task Off_does_not_expose_pcm_or_enable_device_clock()
    {
        using var observer = new GeneratedSpeechObserver();
        await using var harness = new Harness(generatedSpeech: observer);
        harness.Answer("Voice remains ordinary.");
        Assert.Equal(ConversationState.Completed, (await Harness.Finish(harness.Start())).State);
        Assert.False(observer.Segments.TryRead(out _));
        Assert.Equal(0, harness.Device.ClockReads);
    }
}
