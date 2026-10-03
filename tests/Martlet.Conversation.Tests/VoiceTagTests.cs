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
}
