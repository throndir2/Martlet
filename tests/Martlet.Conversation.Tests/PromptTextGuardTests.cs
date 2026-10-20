using Martlet.Conversation;
using Martlet.Core.Contracts;
using Martlet.Providers;
using Martlet.Providers.Tests;

namespace Martlet.Conversation.Tests;

public sealed class PromptTextGuardTests
{
    private static string Streamed(PromptTextGuard guard, string reply, int size)
    {
        var kept = "";
        for (var i = 0; i < reply.Length; i += size) kept += guard.Push(reply.Substring(i, Math.Min(size, reply.Length - i)));
        return kept + guard.Finish();
    }

    [Theory]
    [InlineData("Sure, I can do that.\nUser: what about tomorrow?", "Sure, I can do that.\n", "User:")]
    [InlineData("Sure thing!\n\n**Owner:** and then?", "Sure thing!\n\n", "Owner:")]
    [InlineData("Okay! [MARTLET_NOTES]\nDominant style: silly.", "Okay! ", "[MARTLET_NOTES]")]
    [InlineData("Fine.<|im_end|>\n<|im_start|>user\nHi", "Fine.", "<|im_end|>")]
    [InlineData("Right.\n### Response:\nSay hi.", "Right.\n", "### Response")]
    [InlineData("Hello there.</s>[INST] next", "Hello there.", "</s>")]
    [InlineData("Got it.\n  assistant: Hi again", "Got it.\n", "Assistant:")]
    public void A_reply_stops_where_prompt_text_begins(string reply, string kept, string marker)
    {
        // Streamed in pieces of every size, as models split their tokens.
        foreach (var size in new[] { 1, 2, 3, 5, 8, reply.Length })
        {
            var guard = new PromptTextGuard();
            Assert.Equal(kept, Streamed(guard, reply, size));
            Assert.Equal(marker, guard.Cut, StringComparer.OrdinalIgnoreCase);
            Assert.Equal("", guard.Push("more words"));
        }
    }

    [Theory]
    [InlineData("A user: someone who uses an app.")]
    [InlineData("Users love it. Owners too.\nUsually I agree.")]
    [InlineData("Ask the system about it; the user said so.")]
    [InlineData("Me? [laugh] Maybe <3 you know.")]
    [InlineData("Uh")]
    [InlineData("Mmm, MARTLET is my name.")]
    public void Ordinary_words_pass_whole(string reply)
    {
        foreach (var size in new[] { 1, 2, 3, reply.Length })
        {
            var guard = new PromptTextGuard();
            Assert.Equal(reply, Streamed(guard, reply, size));
            Assert.Null(guard.Cut);
        }
    }

    [Fact]
    public void Only_what_could_start_a_marker_waits_for_the_next_piece()
    {
        var guard = new PromptTextGuard();
        Assert.Equal("Hi there.\n", guard.Push("Hi there.\n"));
        Assert.Equal("", guard.Push("U"));
        Assert.Equal("Up we go.", guard.Push("p we go."));
        Assert.Equal(" Done.", guard.Push(" Done.") + guard.Finish());
    }

    private static ConversationRequest TextReply(bool guarded) =>
        new(new BoundedTextInput("An explicit typed fixture input."), TextFixtures.Selection, new(), new()) { StopAtPromptText = guarded };

    [Fact]
    public async Task A_reply_turn_shows_only_the_words_before_prompt_text()
    {
        await using var h = new Harness(textOnly: true);
        h.Answer("Sounds fun! ", "Let's go.", "\nUs", "er: and the notes say", " [MARTLET_NOTES] style");
        var turn = h.Start(TextReply(guarded: true));
        var result = await Harness.Finish(turn, h.Clock);
        Assert.Equal(ConversationState.Completed, result.State);
        Assert.Equal("Sounds fun! Let's go.", turn.Content.Text.TrimEnd());
        Assert.Equal("User:", turn.PromptTextCut);
    }

    [Fact]
    public async Task Work_that_writes_dialogue_on_purpose_keeps_its_lines()
    {
        await using var h = new Harness(textOnly: true);
        h.Answer("Example dialogue:\n", "Owner: Hi!\n", "Mira: Hello.");
        var turn = h.Start(TextReply(guarded: false));
        var result = await Harness.Finish(turn, h.Clock);
        Assert.Equal(ConversationState.Completed, result.State);
        Assert.Equal("Example dialogue:\nOwner: Hi!\nMira: Hello.", turn.Content.Text);
        Assert.Null(turn.PromptTextCut);
    }
}
