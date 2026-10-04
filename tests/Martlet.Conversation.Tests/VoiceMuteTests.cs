using Martlet.Audio;
using Martlet.Audio.Tests;
using Martlet.Core.Contracts;
using Martlet.Providers;
using Martlet.Providers.Tests;

namespace Martlet.Conversation.Tests;

public sealed class VoiceMuteTests
{
    private static async Task<List<string>> ReadCaptions(SpokenTextFeed captions, RuntimeClock clock, int count)
    {
        var shown = new List<string>();
        while (shown.Count < count)
        {
            await Harness.Until(() => captions.Lines.TryPeek(out _));
            Assert.True(captions.Lines.TryRead(out var line));
            shown.Add(line!.Text);
            clock.Advance(ConversationTurn.ReadingTime(line.Text));
        }
        return shown;
    }

    [Fact]
    public async Task Muting_the_voice_mid_reply_stops_speaking_but_the_reply_completes_and_shows_in_the_captions()
    {
        var captions = new SpokenTextFeed();
        await using var h = new Harness(new ControlledDevice { AutoConsume = false }, spokenText: captions);
        h.Answer("First sentence. ", "Second sentence. ", "Third sentence.");
        var turn = h.Start();
        // The first sentence is playing (it never finishes on its own) and the second is already synthesized.
        await Harness.Until(() => h.Device.Opens == 1 && h.Tts.Calls == 2, h.Clock);
        turn.MuteVoice();
        var result = await Harness.Finish(turn, h.Clock);
        Assert.Equal(ConversationState.Completed, result.State);
        Assert.Equal(ConversationFailure.None, result.Failure);
        Assert.True(result.VoiceMuted);
        Assert.False(result.SpeechFailed);
        Assert.Null(result.FailedProvider);
        Assert.True(result.TextComplete);
        Assert.Equal("First sentence. Second sentence. Third sentence.", turn.Content.Text);
        // Nothing more is synthesized or played once the voice is muted.
        Assert.Equal(2, h.Tts.Calls);
        Assert.Equal(1, h.Device.Opens);
        Assert.Equal(["First sentence.", "Second sentence.", "Third sentence."], await ReadCaptions(captions, h.Clock, 3));
        Assert.False(captions.Lines.TryRead(out _));
    }

    [Fact]
    public async Task Muting_before_the_voice_starts_speaks_nothing_and_is_not_a_failure()
    {
        await using var h = new Harness();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Permissions.Text = async (action, token) =>
        {
            await release.Task.WaitAsync(token);
            return h.Permissions.Allow(action);
        };
        h.Answer("Never said aloud.");
        var turn = h.Start();
        await Harness.Until(() => !h.Permissions.TextActions.IsEmpty);
        turn.MuteVoice();
        release.SetResult();
        var result = await Harness.Finish(turn);
        Assert.Equal(ConversationState.Completed, result.State);
        Assert.True(result.VoiceMuted);
        Assert.False(result.SpeechFailed);
        Assert.Equal("Never said aloud.", turn.Content.Text);
        Assert.Empty(h.Permissions.SpeechActions);
        Assert.Equal(0, h.Tts.Calls);
        Assert.Equal(0, h.Device.Opens);
    }

    [Fact]
    public async Task Muting_a_reply_that_is_not_spoken_or_has_finished_changes_nothing()
    {
        await using var h = new Harness();
        h.Answer("Plain text response.");
        var silent = h.Start(Harness.Request(speech: false));
        silent.MuteVoice();
        var text = await Harness.Finish(silent);
        Assert.False(text.VoiceMuted);
        Assert.Equal(ConversationState.Completed, text.State);

        var spoken = h.Start();
        var finished = await Harness.Finish(spoken);
        spoken.MuteVoice();
        Assert.False(finished.VoiceMuted);
        Assert.False(spoken.Snapshot.VoiceMuted);
        Assert.Equal(1, h.Tts.Calls);
    }

    [Fact]
    public async Task A_reply_that_is_not_spoken_shows_each_sentence_in_the_captions_without_any_voice()
    {
        var captions = new SpokenTextFeed();
        await using var h = new Harness(textOnly: true, spokenText: captions);
        h.Answer("One. ", "Two [laugh] ", "too. Three");
        var result = await Harness.Finish(h.Start(Harness.Request(speech: false)));
        Assert.Equal(ConversationState.Completed, result.State);
        Assert.Equal(["One.", "Two too.", "Three"], await ReadCaptions(captions, h.Clock, 3));
        Assert.False(captions.Lines.TryRead(out _));
        Assert.Empty(h.Permissions.SpeechActions);
        Assert.Equal(0, h.Tts.Calls);
        Assert.Equal(0, h.Device.Opens);
        Assert.Null(result.Playback);
        Assert.False(result.VoiceMuted);
    }

    [Fact]
    public async Task A_quiet_reply_that_is_not_spoken_shows_nothing()
    {
        var captions = new SpokenTextFeed();
        await using var h = new Harness(textOnly: true, spokenText: captions);
        h.Answer("[pass]");
        var request = new ConversationRequest(new BoundedTextInput("An explicit typed fixture input."), TextFixtures.Selection,
            new(), new(), silentReply: "pass");
        var result = await Harness.Finish(h.Start(request));
        Assert.Equal(ConversationState.Completed, result.State);
        await Task.Delay(50);
        Assert.False(captions.Lines.TryRead(out _));
    }
}
