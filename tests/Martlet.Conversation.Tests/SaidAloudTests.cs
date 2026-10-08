using Martlet.Audio.Tests;

namespace Martlet.Conversation.Tests;

/// <summary>What Martlet has said aloud of a reply so far (<see cref="ConversationTurn.SaidAloud"/>), which a touch that stops it
/// passes on to its reaction.</summary>
public sealed class SaidAloudTests
{
    [Fact]
    public async Task What_was_said_aloud_is_each_sentence_that_started_playing_and_stays_once_the_reply_is_stopped()
    {
        await using var h = new Harness(new ControlledDevice { AutoConsume = false });
        h.Answer("First sentence. ", "Second sentence. ", "Third sentence.");
        var turn = h.Start();
        Assert.Equal("", turn.SaidAloud);
        // The first sentence is playing (it never finishes on its own) and the second is already synthesized but waits.
        await Harness.Until(() => h.Device.Opens == 1 && h.Tts.Calls == 2, h.Clock);
        Assert.Equal("First sentence.", turn.SaidAloud);
        _ = turn.StopAsync();
        var result = await Harness.Finish(turn, h.Clock);
        Assert.Equal(ConversationState.Canceled, result.State);
        Assert.Equal("First sentence.", turn.SaidAloud);
    }

    [Fact]
    public async Task A_reply_that_is_not_spoken_says_nothing_aloud()
    {
        await using var h = new Harness();
        h.Answer("Plain text response.");
        var turn = h.Start(Harness.Request(speech: false));
        await Harness.Finish(turn);
        Assert.Equal("Plain text response.", turn.Content.Text);
        Assert.Equal("", turn.SaidAloud);
    }
}
