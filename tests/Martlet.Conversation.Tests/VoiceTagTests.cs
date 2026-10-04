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

    [Fact]
    public void Chatterbox_sounds_are_the_documented_event_tags_and_tones_the_other_conversational_style_tokens()
    {
        // Resemble's Turbo apps offer exactly these as EVENT_TAGS; the tones are the pinned tokenizer's other style tokens
        // without [advertisement] and [narration].
        string[] sounds = ["[clear throat]", "[sigh]", "[shush]", "[cough]", "[groan]", "[sniff]", "[gasp]", "[chuckle]", "[laugh]"];
        string[] tones = ["[angry]", "[fear]", "[surprised]", "[whispering]", "[dramatic]", "[crying]", "[happy]", "[sarcastic]"];
        Assert.Equal(sounds.Order(), Chatterbox.Where(tag => tag.Kind == VoiceTagKind.Sound).Select(tag => tag.Text).Order());
        Assert.Equal(tones.Order(), Chatterbox.Where(tag => tag.Kind == VoiceTagKind.Emotion).Select(tag => tag.Text).Order());
    }

    [Fact]
    public void Thinking_prompt_lists_every_chatterbox_sound_then_every_tone_under_where_each_goes()
    {
        var prompt = VoiceTags.Instructions(SpeechEngines.Chatterbox, null)!;
        var sounds = prompt.IndexOf(VoiceTags.SoundsHeading, StringComparison.Ordinal);
        var tones = prompt.IndexOf(VoiceTags.TonesHeading, StringComparison.Ordinal);
        Assert.True(sounds > 0 && tones > sounds);
        foreach (var tag in Chatterbox)
        {
            var line = prompt.IndexOf($"\n{tag.Text} - {tag.Usage}\n", StringComparison.Ordinal);
            Assert.True(line > sounds, tag.Text);
            Assert.Equal(tag.Kind == VoiceTagKind.Sound, line < tones);
        }
        Assert.StartsWith("Your replies are spoken aloud by Chatterbox Turbo", prompt);
        Assert.Contains("\"That's hilarious [laugh] okay, so...\"", prompt);
        Assert.DoesNotContain("[advertisement]", prompt);
        Assert.DoesNotContain("[narration]", prompt);
    }

    [Fact]
    public void Thinking_prompt_leaves_out_a_group_the_engine_lacks_and_edited_prompts_get_the_groups_too()
    {
        var dia = VoiceTags.Instructions(SpeechEngines.Dia, null)!;
        Assert.Contains(VoiceTags.SoundsHeading + "\n(laughs) - ", dia);
        Assert.DoesNotContain(VoiceTags.TonesHeading, dia);
        Assert.Null(VoiceTags.Instructions(SpeechEngines.F5, null));
        Assert.Null(VoiceTags.Instructions(null, null));
        var edited = new PromptSettings { Overrides = new Dictionary<string, string> { [PromptCatalog.VoiceTags] = "Tags for {engine}:\n{tags}" } };
        Assert.Equal("Tags for Chatterbox Turbo:\n" + VoiceTags.Catalog(SpeechEngines.Chatterbox),
            VoiceTags.Instructions(SpeechEngines.Chatterbox, edited));
        var emptied = new PromptSettings { Overrides = new Dictionary<string, string> { [PromptCatalog.VoiceTags] = "" } };
        Assert.Null(VoiceTags.Instructions(SpeechEngines.Chatterbox, emptied));
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
        var plain = new SpeechSegmenter(1536, 16_384, "pass", breaks: SpeechBreaks.Default,
            controlTags: ChattinessTags.All);
        Assert.DoesNotContain(plain.Push(sentence), piece => piece.Text is not null);
        // As soon as what follows can only be a control tag, the sentence goes, before the tag's last token arrives.
        var tagged = new SpeechSegmenter(1536, 16_384, "pass", breaks: SpeechBreaks.Default,
            controlTags: ChattinessTags.All);
        var early = tagged.Push(sentence).Concat(tagged.Push("[cha")).Where(piece => piece.Text is not null).Select(piece => piece.Text);
        Assert.Equal(["Whoa, nice combo!"], early);
        Assert.DoesNotContain(tagged.Push("ttiness:chatty]").Concat(tagged.Finish()), piece => piece.Text is not null);
        // A bracket that turns out not to be a tag leaves the sentence before it spoken as before.
        var untagged = new SpeechSegmenter(1536, 16_384, "pass", breaks: SpeechBreaks.Default,
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
