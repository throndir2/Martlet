using Martlet.Audio;
using Martlet.Audio.Tests;
using Martlet.Providers;
using Martlet.Providers.Tests;

namespace Martlet.Conversation.Tests;

// A character cue written partway into a sentence ("Hey cutie {wink} I like how you are.") acts when the reply's speech gets
// there: a pause holds it, and a stop drops it.
public sealed class CharacterCueTimingTests
{
    private static ConversationRequest Request() => new(new BoundedTextInput("An explicit typed fixture input."),
        TextFixtures.Selection, new(), new(), new(SpeechFixtures.Selection, new(OutputPolicy.DefaultAtStart), Harness.SpeechLimits),
        characterTags: ["{wink}"]);

    // Starts the reply and returns its first sentence's cues as that sentence starts playing.
    private static async Task<(ConversationTurn Turn, CharacterCueLine Line, CharacterCue Wink)> StartAsync(Harness h, CharacterCueFeed cues)
    {
        h.Answer("Hey cutie {wink} I like how you are. ", "Second sentence.");
        var turn = h.Start(Request());
        CharacterCueLine? line = null;
        await Harness.Until(() => cues.Lines.TryRead(out line), h.Clock);
        var wink = Assert.Single(line!.Cues);
        Assert.Equal("{wink}", wink.Tag);
        // Partway into the sentence, not at its start.
        Assert.True(wink.Delay > TimeSpan.FromMilliseconds(10), $"The wink falls {wink.Delay} into its sentence.");
        return (turn, line, wink);
    }

    [Fact]
    public async Task A_cue_waits_out_a_pause_and_keeps_its_place_in_the_speech()
    {
        var cues = new CharacterCueFeed();
        await using var h = new Harness(new ControlledDevice { AutoConsume = false }, characterCues: cues);
        var (turn, line, wink) = await StartAsync(h, cues);
        var reached = line.ReachedAsync(wink, CancellationToken.None);
        Assert.False(reached.IsCompleted);
        Assert.True(turn.Pause());
        // Paused long past the wink's moment: it waits.
        for (var i = 0; i < 40; i++)
        {
            h.Clock.Advance(TimeSpan.FromMilliseconds(50));
            await Task.Delay(1);
        }
        await Task.Delay(50);
        Assert.False(reached.IsCompleted);

        var resumedAt = h.Clock.GetTimestamp();
        Assert.True(turn.Resume());
        await Harness.Until(() => reached.IsCompleted, h.Clock);
        Assert.True(await reached);
        // About its delay after the resume (less the few milliseconds played before the pause): where it was written.
        Assert.True(h.Clock.GetElapsedTime(resumedAt) >= wink.Delay - TimeSpan.FromMilliseconds(10));

        while (!turn.Completion.IsCompleted)
        {
            h.Device.Consume(int.MaxValue);
            await Task.Delay(1);
            h.Clock.Advance(TimeSpan.FromMilliseconds(5));
        }
        Assert.Equal(ConversationState.Completed, (await turn.Completion).State);
        // A reply that ends by itself drops nothing: a cue still to come acts.
        Assert.True(await line.ReachedAsync(new CharacterCue("{wink}", TimeSpan.Zero), CancellationToken.None));
    }

    [Fact]
    public async Task A_cue_still_waiting_when_the_reply_is_stopped_is_never_acted()
    {
        var cues = new CharacterCueFeed();
        await using var h = new Harness(new ControlledDevice { AutoConsume = false }, characterCues: cues);
        var (turn, line, wink) = await StartAsync(h, cues);
        var reached = line.ReachedAsync(wink, CancellationToken.None);
        Assert.False(reached.IsCompleted);

        var result = await turn.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(ConversationState.Canceled, result.State);
        Assert.False(await reached.WaitAsync(TimeSpan.FromSeconds(5)));
        // Nothing of the stopped reply acts any more, even a cue due at once.
        Assert.False(await line.ReachedAsync(new CharacterCue("{wink}", TimeSpan.Zero), CancellationToken.None));
    }

    [Fact]
    public async Task A_paused_reply_that_is_stopped_drops_its_waiting_cue()
    {
        var cues = new CharacterCueFeed();
        await using var h = new Harness(new ControlledDevice { AutoConsume = false }, characterCues: cues);
        var (turn, line, wink) = await StartAsync(h, cues);
        var reached = line.ReachedAsync(wink, CancellationToken.None);
        Assert.True(turn.Pause());
        h.Clock.Advance(wink.Delay + TimeSpan.FromSeconds(1));
        await turn.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(await reached.WaitAsync(TimeSpan.FromSeconds(5)));
    }
}
