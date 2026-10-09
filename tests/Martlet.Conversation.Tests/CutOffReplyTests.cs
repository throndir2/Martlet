using Martlet.Conversation;
using Martlet.Core.Settings;
using Xunit;

namespace Martlet.Conversation.Tests;

public sealed class CutOffReplyTests
{
    [Fact]
    public void TheConversationKeepsOnlyWhatWasSaidAloudMarkedAsCutOff()
    {
        Assert.Equal("Once upon a time there was a fox." + CutOffReply.Marker, CutOffReply.Kept(" Once upon a time there was a fox. "));
        Assert.Null(CutOffReply.Kept(""));
        Assert.Null(CutOffReply.Kept(null));
        Assert.Null(CutOffReply.Kept("   "));
    }

    [Fact]
    public void TheRestIsWhatComesAfterTheWordsSaidAloud()
    {
        const string reply = "Once upon a time there was a fox. It lived in a quiet wood. One day it met a crow.";
        Assert.Equal("It lived in a quiet wood. One day it met a crow.", CutOffReply.Unsaid(reply, "Once upon a time there was a fox."));
        // Said aloud without its voice tags and with other spacing; the sentence playing counts as said.
        Assert.Equal("One day it met a crow.",
            CutOffReply.Unsaid(reply, "Once upon a time there was a fox.  It lived in a quiet wood."));
        // A word the voice said that the text doesn't have (a tag's word) is skipped.
        Assert.Equal("It lived in a quiet wood. One day it met a crow.", CutOffReply.Unsaid(reply, "Once upon a time laugh there was a fox."));
        // All of it was said: nothing is left.
        Assert.Null(CutOffReply.Unsaid(reply, reply));
        // Nothing said aloud: all of it is left.
        Assert.Equal(reply, CutOffReply.Unsaid(reply, ""));
        // Words not in the reply: where it stopped isn't known, so nothing is guessed.
        Assert.Null(CutOffReply.Unsaid(reply, "Something else entirely was said here."));
        Assert.Null(CutOffReply.Unsaid("", "Hi."));
    }

    [Fact]
    public void TheNoteTellsTheNextReplyWhatItHadNotSaidWithAnEditablePrompt()
    {
        var note = CutOffReply.Note(null, "It lived in a quiet wood.");
        Assert.NotNull(note);
        Assert.Contains("\"It lived in a quiet wood.\"", note);
        Assert.Contains("as I was saying", note);
        Assert.Null(CutOffReply.Note(null, null));
        Assert.Null(CutOffReply.Note(null, "  "));
        Assert.Equal(["unsaid"], PromptCatalog.All.Single(p => p.Id == PromptCatalog.CutOff).Placeholders);

        var emptied = new PromptSettings { Overrides = new Dictionary<string, string> { [PromptCatalog.CutOff] = "" } };
        Assert.Null(CutOffReply.Note(emptied, "It lived in a quiet wood."));
        var mine = new PromptSettings { Overrides = new Dictionary<string, string> { [PromptCatalog.CutOff] = "Unsaid: {unsaid}" } };
        Assert.Equal("Unsaid: It lived in a quiet wood.", CutOffReply.Note(mine, "It lived in a quiet wood."));

        // A long rest goes in by its start, cut at a word.
        var longRest = string.Join(" ", Enumerable.Repeat("word", 200));
        var cut = CutOffReply.Note(mine, longRest)!;
        Assert.EndsWith("word…", cut);
        Assert.True(cut.Length <= "Unsaid: ".Length + CutOffReply.MaximumUnsaidCharacters + 1);
    }
}
