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
}
