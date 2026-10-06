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
    [InlineData("Not a tag [yodeling] here.\nPlain.", "Plain.")]
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
        With([.. ChattinessTags.All, .. SeenTags.All]).Validate();
        Assert.Throws<Martlet.Core.Contracts.ContractException>(() => With(["{blush}"]).Validate());
        Assert.Throws<Martlet.Core.Contracts.ContractException>(() => With([.. Enumerable.Range(0, 17).Select(i => $"[tag{i}]")]).Validate());
    }

    private static readonly IReadOnlyList<string> SeenAndChattiness = [.. ChattinessTags.All, .. SeenTags.All];

    [Theory]
    [InlineData("Nice combo! [seen: a fighting game, final round]", "Nice combo!", "[seen: a fighting game, final round]")]
    [InlineData("[pass] [Seen: a code editor] [chattiness:quiet]", "[pass]", "[Seen: a code editor]|[chattiness:quiet]")]
    [InlineData("[pass] [seen:]", "[pass]", "[seen:]")]
    [InlineData("Hmm [seen: two lines\nnope] ok.", "Hmm [seen: two lines\nnope] ok.", "")]
    [InlineData("Oh [seen: nested [x] brackets] ok.", "Oh [seen: nested [x] brackets] ok.", "")]
    [InlineData("Unfinished [seen: a game", "Unfinished [seen: a game", "")]
    public void The_seen_tag_is_never_shown_and_is_reported_as_written_on_every_split(string input, string shown, string controls)
    {
        Assert.Equal(shown, VoiceTags.Strip(input, controlTags: SeenAndChattiness));
        for (int split = 0; split <= input.Length; split++)
        {
            var found = new List<string>();
            var stripper = new VoiceTagStripper(controlTags: SeenAndChattiness, droppedControl: found.Add);
            Assert.Equal(shown, stripper.Push(input[..split]) + stripper.Push(input[split..]) + stripper.Finish());
            Assert.Equal(controls, string.Join('|', found));
        }
    }

    [Theory]
    [InlineData("Nice combo! [seen: a fighting game, final round]", "Nice combo!")]
    [InlineData("[pass] [seen: a code editor with a C# file]", "")]
    [InlineData("Whoa. That's close. [seen: racing game] [chattiness:chatty]", "Whoa.|That's close.")]
    public void The_seen_tag_is_never_spoken(string input, string expected)
    {
        for (int split = 0; split <= input.Length; split++)
        {
            var segmenter = new SpeechSegmenter(1536, 16_384, "pass", tags: Chatterbox, controlTags: SeenAndChattiness);
            var pieces = segmenter.Push(input[..split]).Concat(segmenter.Push(input[split..])).Concat(segmenter.Finish()).ToArray();
            Assert.Equal(expected, string.Join('|', pieces.Where(x => x.Text is not null).Select(x => x.Text)));
        }
    }

    [Fact]
    public void The_seen_tag_never_holds_back_the_words_before_it()
    {
        var tagged = new SpeechSegmenter(1536, 16_384, "pass", tags: Chatterbox, breaks: SpeechBreaks.Default,
            controlTags: SeenAndChattiness);
        Assert.DoesNotContain(tagged.Push("Whoa, nice combo! "), piece => piece.Text is not null);
        // "[see" can only be the seen tag: the sentence goes to the voice before the description is written.
        var early = tagged.Push("[see").Where(piece => piece.Text is not null).Select(piece => piece.Text);
        Assert.Equal(["Whoa, nice combo!"], early);
        Assert.DoesNotContain(tagged.Push("n: a fighting game, final round]").Concat(tagged.Finish()), piece => piece.Text is not null);
    }

    [Fact]
    public void A_turn_reports_the_seen_tag_and_its_description()
    {
        var preview = SpeechTextPreview.For("[pass] [seen:  a  racing game,\tlast lap. ]", null, silentWord: "pass",
            controlTags: SeenAndChattiness);
        Assert.Empty(preview.Spoken);
        Assert.Equal("[pass]", preview.Shown);
        Assert.Equal("a racing game, last lap", SeenTags.Description(preview.Controls));
    }

    private static readonly string[] Character = ["{nod}", "{shake_head}", "{blush}", "{happy}"];

    private static (string Spoken, string Cues) SegmentWithCues(string input, IReadOnlyList<VoiceTag>? tags, int split)
    {
        var segmenter = new SpeechSegmenter(1536, 16_384, tags: tags, characterTags: Character);
        var pieces = segmenter.Push(input[..split]).Concat(segmenter.Push(input[split..])).Concat(segmenter.Finish()).ToArray();
        return (string.Join('|', pieces.Where(x => x.Text is not null).Select(x => x.Text)),
            string.Join(' ', pieces.SelectMany(piece => piece.Cues ?? []).Select(cue => cue.Tag)));
    }

    [Theory]
    [InlineData("Oh, look at all that activity! [nod] What are you working on right now?",
        "Oh, look at all that activity!|What are you working on right now?", "{nod}")]
    [InlineData("Sure *nods* let's go.", "Sure let's go.", "{nod}")]
    [InlineData("(Nods) Okay.", "Okay.", "{nod}")]
    [InlineData("Stop it <blush> you.", "Stop it you.", "{blush}")]
    [InlineData("No [shakes head] never.", "No never.", "{shake_head}")]
    [InlineData("No *shakes_head* never.", "No never.", "{shake_head}")]
    [InlineData("Aww *blushes* thanks.", "Aww thanks.", "{blush}")]
    [InlineData("Right {nod} sure.", "Right sure.", "{nod}")]
    public void Other_spellings_of_a_character_tag_act_and_never_silence_their_line_on_every_split(string input, string spoken, string cues)
    {
        for (int split = 0; split <= input.Length; split++)
            Assert.Equal((spoken, cues), SegmentWithCues(input, Chatterbox, split));
    }

    [Theory]
    [InlineData("Ha *laughs* okay.", "Ha [laugh] okay.")]
    [InlineData("(sighs) Fine.", "[sigh] Fine.")]
    [InlineData("Ahem (clears throat) listen.", "Ahem [clear throat] listen.")]
    [InlineData("{whispering} It's a secret.", "[whispering] It's a secret.")]
    [InlineData("Hm [chuckles] sure.", "Hm [chuckle] sure.")]
    [InlineData("[whisper] It's a secret.", "[whispering] It's a secret.")]
    [InlineData("(whisper) It's a secret.", "[whispering] It's a secret.")]
    [InlineData("{Whisper} It's a secret.", "[whispering] It's a secret.")]
    [InlineData("*whispers* It's a secret.", "[whispering] It's a secret.")]
    [InlineData("*whispering* It's a secret.", "[whispering] It's a secret.")]
    [InlineData("[whispered] It's a secret.", "[whispering] It's a secret.")]
    [InlineData("(in a whisper) It's a secret.", "[whispering] It's a secret.")]
    [InlineData("[hushed] It's a secret.", "[whispering] It's a secret.")]
    [InlineData("(sobbing) I miss her.", "[crying] I miss her.")]
    [InlineData("Ha [laughing] okay.", "Ha [laugh] okay.")]
    [InlineData("*giggles* Stop.", "[chuckle] Stop.")]
    public void Other_spellings_of_the_voices_own_tags_are_spoken_as_the_engine_spells_them(string input, string expected)
    {
        for (int split = 0; split <= input.Length; split++)
            Assert.Equal(expected, Segment(input, Chatterbox, split));
    }

    [Theory]
    [InlineData("That's funny [laugh] yes.", "That's funny (laughs) yes.")]
    [InlineData("Ha *laughs* okay.", "Ha (laughs) okay.")]
    [InlineData("[sigh] Fine.", "(sighs) Fine.")]
    public void Another_engines_spelling_of_a_sound_the_voice_makes_is_spoken_its_way(string input, string expected)
    {
        for (int split = 0; split <= input.Length; split++)
            Assert.Equal(expected, Segment(input, SpeechEngines.Dia.Tags, split));
    }

    [Theory]
    [InlineData("*laughs* That's great.", "That's great.")]
    [InlineData("Oh (sighs) fine.", "Oh fine.")]
    [InlineData("[whisper] It's a secret.", "It's a secret.")]
    public void A_voice_without_tags_drops_stage_directions_of_known_sounds_instead_of_the_line(string input, string expected)
    {
        for (int split = 0; split <= input.Length; split++)
            Assert.Equal(expected, Segment(input, SpeechEngines.F5.Tags, split));
    }

    [Fact]
    public void A_spelling_the_voice_and_the_character_share_is_the_voices_tone_and_otherwise_the_emote()
    {
        Assert.Equal(("[happy] Yay.", "[happy]"), SegmentWithCues("[happy] Yay.", Chatterbox, 0));
        Assert.Equal(("Yay.", "{happy}"), SegmentWithCues("[happy] Yay.", SpeechEngines.F5.Tags, 0));
        Assert.Equal(("Yay.", "{happy}"), SegmentWithCues("{happy} Yay.", Chatterbox, 0));
    }

    [Theory]
    [InlineData("I'm *so* happy.")]
    [InlineData("I'm *happy* to help.")]
    [InlineData("I'm *quietly* *afraid* of it.")]
    [InlineData("A [nodding] dog (blushing) here.")]
    [InlineData("Keep [brackets] and (parens).")]
    public void Emphasis_and_words_that_arent_a_tags_spelling_are_left_alone(string input)
    {
        Assert.Equal(input, VoiceTags.Strip(input, Character));
        var stripper = new VoiceTagStripper(Character, voiceTags: Chatterbox, removed: tag => Assert.Fail(tag.Text));
        Assert.Equal(input, stripper.Push(input) + stripper.Finish());
    }

    [Theory]
    [InlineData("Oh, look at that! [nod] What are you doing?", "Oh, look at that! What are you doing?", "{nod}")]
    [InlineData("Aww *blushes* thanks.", "Aww thanks.", "{blush}")]
    [InlineData("No (shakes head).", "No.", "{shake_head}")]
    public void The_chat_never_shows_another_spelling_and_reports_the_character_tag_as_listed(string input, string shown, string tag)
    {
        for (int split = 0; split <= input.Length; split++)
        {
            var dropped = new List<string>();
            var stripper = new VoiceTagStripper(Character, dropped.Add);
            Assert.Equal(shown, stripper.Push(input[..split]) + stripper.Push(input[split..]) + stripper.Finish());
            Assert.Equal([tag], dropped);
        }
    }

    [Fact]
    public void Spellings_cover_other_brackets_the_cue_joined_words_and_actions_but_tones_get_no_actions()
    {
        var shake = VoiceTags.Spellings(new VoiceTag("{shake_head}", VoiceTagKind.Character, "")).Select(t => t.Text).ToArray();
        Assert.Contains("[shake head]", shake);
        Assert.Contains("(shake-head)", shake);
        Assert.Contains("*shakes head*", shake);
        Assert.Contains("[shakes_head]", shake);
        Assert.DoesNotContain("{shake_head}", shake);
        Assert.DoesNotContain("*shake head*", shake);
        Assert.All(VoiceTags.Spellings(new VoiceTag("{blush}", VoiceTagKind.Character, "")), t =>
        {
            Assert.Equal("{blush}", t.Canonical);
            Assert.Equal("{blush}", t.AliasOf);
        });
        var laugh = VoiceTags.Spellings(Chatterbox.Single(t => t.Text == "[laugh]")).Select(t => t.Text).ToArray();
        Assert.Contains("(laughs)", laugh);
        Assert.Contains("*laughs*", laugh);
        Assert.Contains("{laugh}", laugh);
        var dia = VoiceTags.Spellings(SpeechEngines.Dia.Tags.Single(t => t.Text == "(laughs)"));
        Assert.Contains(dia, t => t.Text == "[laugh]" && t.Canonical == "(laughs)" && t.Cue == "laugh");
        var happy = VoiceTags.Spellings(Chatterbox.Single(t => t.Text == "[happy]")).Select(t => t.Text).ToArray();
        Assert.Contains("(happy)", happy);
        Assert.Contains("<happy>", happy);
        Assert.Contains("{happy}", happy);
        Assert.Contains("[cheerfully]", happy);
        Assert.DoesNotContain(happy, t => t.StartsWith('*'));
        Assert.Empty(VoiceTags.Spellings(new VoiceTag("[chattiness:quiet]", VoiceTagKind.Control, "")));
    }

    [Fact]
    public void Acted_lists_the_character_tags_and_what_the_voice_performed_and_the_note_says_it()
    {
        var preview = SpeechTextPreview.For("[happy] Oh, look! *nods* Ha (laughs) and [cough] {blush}.", SpeechEngines.Chatterbox, Character);
        Assert.Equal(["[happy]", "{nod}", "[laugh]", "[cough]", "{blush}"], preview.Acted.Select(t => t.Tag));
        Assert.Equal([null, "*nods*", "(laughs)", null, null], preview.Acted.Select(t => t.Written));
        Assert.Equal("Tone: happy. Sounds: laugh, cough. Emotes: nod, blush.", preview.Note);
        Assert.Equal("[happy], {nod} (written *nods*), [laugh] (written (laughs)), [cough], {blush}", ReplyTag.Describe(preview.Acted));
        // A voice that reads words only performs no sounds or tones; the character still acts.
        var plain = SpeechTextPreview.For("Ha [laugh] {shake_head} no.", null, Character);
        Assert.Equal("Emote: shake head.", plain.Note);
        Assert.Null(SpeechTextPreview.For("Just words.", SpeechEngines.Chatterbox, Character).Note);
    }
}
