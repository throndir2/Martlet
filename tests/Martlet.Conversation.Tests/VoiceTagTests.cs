using Martlet.Core.Settings;

namespace Martlet.Conversation.Tests;

public sealed class VoiceTagTests
{
    private static readonly IReadOnlyList<VoiceTag> Chatterbox = SpeechEngines.Chatterbox.Tags;

    private static string Segment(string input, IReadOnlyList<VoiceTag>? tags, int split)
    {
        var segmenter = new SpeechSegmenter(1536, 16_384, tags: tags);
        var pieces = segmenter.Push(input[..split]).Concat(segmenter.Push(input[split..])).Concat(segmenter.Finish()).ToArray();
        return string.Join('|', pieces.Where(x => x.Text is not null).Select(x => x.Text));
    }

    [Theory]
    [InlineData("That's hilarious [laugh] okay, so. Next.", "That's hilarious [laugh] okay, so.|Next.")]
    [InlineData("Oh no [sigh]. Fine.", "Oh no [sigh].|Fine.")]
    [InlineData("Ahem [clear throat] listen. Done.", "Ahem [clear throat] listen.|Done.")]
    [InlineData("[whispering] It's a secret. Shh.", "[whispering] It's a secret.|Shh.")]
    [InlineData("Ha.[chuckle] Right.", "Ha.|[chuckle] Right.")]
    [InlineData("Wow [GASP] really.", "Wow [gasp] really.")]
    [InlineData("A [link](x) here.\nPlain.", "Plain.")]
    [InlineData("Not a tag [laughing] here.\nPlain.", "Plain.")]
    [InlineData("Ends with [la", "")]
    public void Chatterbox_tags_pass_through_on_every_split(string input, string expected)
    {
        for (int split = 0; split <= input.Length; split++)
            Assert.Equal(expected, Segment(input, Chatterbox, split));
    }

    [Theory]
    [InlineData("That's hilarious [laugh] okay. Next.", "That's hilarious okay.|Next.")]
    [InlineData("Oh no [sigh]. Fine.", "Oh no.|Fine.")]
    [InlineData("A [link](x) here.\nPlain.", "Plain.")]
    public void Engines_without_tags_get_known_tags_removed_not_the_sentence(string input, string expected)
    {
        for (int split = 0; split <= input.Length; split++)
        {
            Assert.Equal(expected, Segment(input, null, split));
            Assert.Equal(expected, Segment(input, SpeechEngines.F5.Tags, split));
        }
    }

    [Theory]
    [InlineData("That's hilarious [laugh] okay.", "That's hilarious okay.")]
    [InlineData("Oh no [sigh]. Fine.", "Oh no. Fine.")]
    [InlineData("[chuckle] Hi there.", "Hi there.")]
    [InlineData("Hmm[sigh] fine.", "Hmm fine.")]
    [InlineData("Keep [brackets] and (parens).", "Keep [brackets] and (parens).")]
    [InlineData("Unfinished [lau", "Unfinished [lau")]
    public void Chat_text_never_shows_tags_on_every_split(string input, string expected)
    {
        Assert.Equal(expected, VoiceTags.Strip(input));
        for (int split = 0; split <= input.Length; split++)
        {
            var stripper = new VoiceTagStripper();
            Assert.Equal(expected, stripper.Push(input[..split]) + stripper.Push(input[split..]) + stripper.Finish());
        }
    }

    [Fact]
    public void Catalog_lists_the_pinned_chatterbox_tags_and_chatterbox_is_the_default()
    {
        Assert.Same(SpeechEngines.Chatterbox, SpeechEngines.Default);
        Assert.Same(SpeechEngines.Chatterbox, SpeechEngines.All[0]);
        Assert.Contains(Chatterbox, tag => tag.Text == "[laugh]" && tag.Kind == VoiceTagKind.Sound);
        Assert.Contains(Chatterbox, tag => tag.Text == "[whispering]" && tag.Kind == VoiceTagKind.Emotion);
        Assert.DoesNotContain(Chatterbox, tag => tag.Text is "[advertisement]" or "[narration]");
        Assert.Empty(SpeechEngines.F5.Tags);
        Assert.Empty(SpeechEngines.Xtts.Tags);
        Assert.Same(Chatterbox, SpeechEngines.TagsForModel("chatterbox-turbo"));
        Assert.Empty(SpeechEngines.TagsForModel("gpt-4o-mini-tts"));
    }

    [Theory]
    [InlineData("Sure, I'll keep it down. [chattiness:quiet]", "Sure, I'll keep it down.", "[chattiness:quiet]")]
    [InlineData("[Chattiness: Chatty] Ooh, look at that!", "Ooh, look at that!", "[chattiness: chatty]")]
    [InlineData("[pass] [chattiness:normal]", "[pass]", "[chattiness:normal]")]
    [InlineData("Ha [laugh] fine. [chattiness:quiet]", "Ha fine.", "[chattiness:quiet]")]
    [InlineData("No tag [chattiness:loud] here.", "No tag [chattiness:loud] here.", null)]
    public void Control_tags_are_never_shown_and_are_reported_on_every_split(string input, string shown, string? control)
    {
        Assert.Equal(shown, VoiceTags.Strip(input, controlTags: ChattinessTags.All));
        for (int split = 0; split <= input.Length; split++)
        {
            var found = new List<string>();
            var stripper = new VoiceTagStripper(controlTags: ChattinessTags.All, droppedControl: found.Add);
            Assert.Equal(shown, stripper.Push(input[..split]) + stripper.Push(input[split..]) + stripper.Finish());
            Assert.Equal(control is null ? [] : [control], found);
        }
    }

    [Theory]
    [InlineData("Sure, I'll keep it down. [chattiness:quiet]", "Sure, I'll keep it down.")]
    [InlineData("[chattiness:chatty] Ooh, look at that! Wow.", "Ooh, look at that!|Wow.")]
    [InlineData("That's hilarious [laugh] okay. [chattiness: normal]", "That's hilarious [laugh] okay.")]
    [InlineData("[pass] [chattiness:quiet]", "")]
    public void Control_tags_are_never_spoken_and_never_silence_the_sentence(string input, string expected)
    {
        for (int split = 0; split <= input.Length; split++)
        {
            var segmenter = new SpeechSegmenter(1536, 16_384, "pass", tags: Chatterbox, controlTags: ChattinessTags.All);
            var pieces = segmenter.Push(input[..split]).Concat(segmenter.Push(input[split..])).Concat(segmenter.Finish()).ToArray();
            Assert.Equal(expected, string.Join('|', pieces.Where(x => x.Text is not null).Select(x => x.Text)));
            Assert.DoesNotContain(pieces, piece => piece.Cues?.Any(cue => cue.Tag.Contains("chattiness", StringComparison.OrdinalIgnoreCase)) == true);
        }
    }

    [Theory]
    [InlineData("Whoa, nice combo! ")]
    [InlineData("Whoa, nice combo!")]
    public void A_control_tag_never_holds_back_the_words_before_it(string sentence)
    {
        // The persona's default speech breaks hold a piece until more words follow it: without a tag, a one-piece reply goes
        // to the voice only when the reply ends.
        var plain = new SpeechSegmenter(1536, 16_384, "pass", eagerFirstClause: true, breaks: SpeechBreaks.Default,
            controlTags: ChattinessTags.All);
        Assert.DoesNotContain(plain.Push(sentence), piece => piece.Text is not null);
        // As soon as what follows can only be a control tag, the sentence goes, before the tag's last token arrives.
        var tagged = new SpeechSegmenter(1536, 16_384, "pass", eagerFirstClause: true, breaks: SpeechBreaks.Default,
            controlTags: ChattinessTags.All);
        var early = tagged.Push(sentence).Concat(tagged.Push("[cha")).Where(piece => piece.Text is not null).Select(piece => piece.Text);
        Assert.Equal(["Whoa, nice combo!"], early);
        Assert.DoesNotContain(tagged.Push("ttiness:chatty]").Concat(tagged.Finish()), piece => piece.Text is not null);
        // A bracket that turns out not to be a tag leaves the sentence before it spoken as before.
        var untagged = new SpeechSegmenter(1536, 16_384, "pass", eagerFirstClause: true, breaks: SpeechBreaks.Default,
            controlTags: ChattinessTags.All);
        var all = untagged.Push(sentence).Concat(untagged.Push("[chat] Okay.")).Concat(untagged.Finish())
            .Where(piece => piece.Text is not null).Select(piece => piece.Text).ToArray();
        Assert.Equal("Whoa, nice combo!", all[0]);
    }

    [Fact]
    public void A_request_takes_only_bracketed_control_tags()
    {
        static ConversationRequest With(IReadOnlyList<string> tags) => new(new Martlet.Providers.BoundedTextInput("Hi."),
            Martlet.Providers.Tests.TextFixtures.Selection, new(), new(), controlTags: tags);
        With(ChattinessTags.All).Validate();
        Assert.Throws<Martlet.Core.Contracts.ContractException>(() => With(["{blush}"]).Validate());
        Assert.Throws<Martlet.Core.Contracts.ContractException>(() => With([.. Enumerable.Range(0, 17).Select(i => $"[tag{i}]")]).Validate());
    }
}
