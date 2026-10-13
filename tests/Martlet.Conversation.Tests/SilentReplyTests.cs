using Martlet.Conversation;

namespace Martlet.Conversation.Tests;

public sealed class SilentReplyTests
{
    [Theory]
    [InlineData("[pass]")]
    [InlineData("Pass.")]
    [InlineData("pass")]
    public void A_reply_that_is_only_the_silent_word_is_never_spoken(string reply)
    {
        var segmenter = new SpeechSegmenter(1536, 4096, "pass");
        var pieces = segmenter.Push(reply).Concat(segmenter.Finish()).ToArray();
        Assert.All(pieces, piece => Assert.Null(piece.Text));
    }

    [Fact]
    public void Ordinary_remarks_and_replies_without_a_silent_word_are_spoken()
    {
        var watching = new SpeechSegmenter(1536, 4096, "pass");
        Assert.Equal(["Passing through the castle again?"],
            watching.Push("Passing through the castle again?").Concat(watching.Finish()).Select(piece => piece.Text));
        var talking = new SpeechSegmenter(1536, 4096);
        Assert.Equal(["Pass."], talking.Push("Pass.").Concat(talking.Finish()).Select(piece => piece.Text));
    }

    [Theory]
    [InlineData("I didn't hear any music. [pass]", "I didn't hear any music.")]
    [InlineData("I didn't hear any music [PASS]", "I didn't hear any music")]
    [InlineData("Nice one [pass].", "Nice one.")]
    public void A_marker_added_after_words_is_neither_shown_nor_spoken(string reply, string words)
    {
        var preview = SpeechTextPreview.For(reply, null, silentWord: StayQuiet.Marker);
        Assert.Equal(words, preview.Shown);
        Assert.False(StayQuiet.IsQuiet(preview.Shown));
        Assert.Equal([words], preview.Spoken);
    }

    [Theory]
    [InlineData("[pass]")]
    [InlineData("  [pass]")]
    [InlineData("[pass] Hmm, okay.")]
    public void A_reply_that_starts_with_the_marker_stays_a_pass(string reply)
    {
        // Streamed one character at a time, as a slow model sends it.
        var segmenter = new SpeechSegmenter(1536, 4096, StayQuiet.Marker);
        var stripper = new Martlet.Core.Settings.VoiceTagStripper(quietMarker: SpeechSegmenter.QuietTag);
        var pieces = new List<SpeechPiece>();
        var shown = "";
        foreach (var c in reply)
        {
            pieces.AddRange(segmenter.Push(c.ToString()));
            shown += stripper.Push(c.ToString());
        }
        pieces.AddRange(segmenter.Finish());
        shown += stripper.Finish();
        Assert.True(StayQuiet.IsQuiet(shown));
        Assert.All(pieces, piece => Assert.Null(piece.Text));
    }
}
