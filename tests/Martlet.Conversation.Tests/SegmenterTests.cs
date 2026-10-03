using System.Text;
using Martlet.Core.Settings;

namespace Martlet.Conversation.Tests;

public sealed class SegmenterTests
{
    [Theory]
    [InlineData("Hello world. Next sentence! Final fragment", "Hello world.|Next sentence!|Final fragment")]
    [InlineData("One\nTwo\r\nThree?", "One|Two|Three?")]
    [InlineData("https://example.test/a. Do not voice this line.\nSafe again.", "Safe again.")]
    [InlineData("Visit example.test please.\nPlain prose.", "Plain prose.")]
    [InlineData("**bold. Still bold**\nPlain.", "Plain.")]
    [InlineData("[link](target)\nPlain.", "Plain.")]
    [InlineData("`inline code`\nPlain.", "Plain.")]
    [InlineData("```csharp\nHello. Do not speak!\n```\nPlain.", "Plain.")]
    [InlineData("~~~\nHello world\n~~~\nPlain.", "Plain.")]
    [InlineData("```\n~~~\nStill code.\n```\nPlain.", "Plain.")]
    [InlineData("````\n```\nStill code.\n````\nPlain.", "Plain.")]
    [InlineData("```\nQuoted ``` inside code.\nStill code.\n```\nPlain.", "Plain.")]
    [InlineData("```\n```not a closing fence\nStill code.\n```   \nPlain.", "Plain.")]
    [InlineData("- item\n1. item\n    code\nPlain.", "Plain.")]
    [InlineData("{\"tool\":\"call\"}\nPlain.", "Plain.")]
    [InlineData("<b>markup</b>\nPlain.", "Plain.")]
    [InlineData("email@example.test\nPlain.", "Plain.")]
    [InlineData("Hello. ```\nNever speak this.", "Hello.")]
    public void Formatting_and_boundaries_survive_every_token_split(string input, string expected)
    {
        for (int split = 0; split <= input.Length; split++)
        {
            var segmenter = new SpeechSegmenter(1536, 16_384);
            var pieces = segmenter.Push(input[..split]).Concat(segmenter.Push(input[split..])).Concat(segmenter.Finish()).ToArray();
            Assert.Equal(expected, string.Join('|', pieces.Where(x => x.Text is not null).Select(x => x.Text)));
        }
        var incremental = new SpeechSegmenter(1536, 16_384);
        var singles = input.SelectMany(c => incremental.Push(c.ToString())).Concat(incremental.Finish()).ToArray();
        Assert.Equal(expected, string.Join('|', singles.Where(x => x.Text is not null).Select(x => x.Text)));
    }

    [Theory]
    [InlineData("Well, that is a really good question, and here is more, still more. Next one, with a comma, here.",
        "Well, that is a really good question,|and here is more, still more.|Next one, with a comma, here.")]
    [InlineData("Short, then a full stop. After.", "Short, then a full stop.|After.")]
    [InlineData("In the sky tonight we counted 1,000,000 stars. Done.", "In the sky tonight we counted 1,000,000 stars.|Done.")]
    public void Eager_first_clause_breaks_only_the_first_piece_at_a_long_enough_clause(string input, string expected)
    {
        for (int split = 0; split <= input.Length; split++)
        {
            var segmenter = new SpeechSegmenter(1536, 16_384, eagerFirstClause: true);
            var pieces = segmenter.Push(input[..split]).Concat(segmenter.Push(input[split..])).Concat(segmenter.Finish()).ToArray();
            Assert.Equal(expected, string.Join('|', pieces.Where(x => x.Text is not null).Select(x => x.Text)));
        }
    }

    [Theory]
    // A short ending joins the piece before it, whether a comma or a sentence end came before it.
    [InlineData("I'm so glad you're here with me today, cutie. Shall we start?", true, true, true, true, 2,
        "I'm so glad you're here with me today, cutie.|Shall we start?")]
    [InlineData("That was a wonderful idea. Cutie! And now we can get going.", true, true, true, true, 2,
        "That was a wonderful idea. Cutie!|And now we can get going.")]
    [InlineData("I'm so glad you're here with me today, cutie. Shall we start?", true, true, true, true, 0,
        "I'm so glad you're here with me today,|cutie.|Shall we start?")]
    // Longer endings stand on their own.
    [InlineData("I'm so glad you're here with me today, my favorite person. Hi.", true, true, true, true, 2,
        "I'm so glad you're here with me today,|my favorite person. Hi.")]
    // A stop that is off doesn't break.
    [InlineData("I'm so glad you're here with me today, cutie. Shall we start?", false, true, true, true, 0,
        "I'm so glad you're here with me today, cutie.|Shall we start?")]
    [InlineData("It is a lovely day outside. Shall we go for a walk? Yes!", true, false, true, true, 0,
        "It is a lovely day outside. Shall we go for a walk?|Yes!")]
    [InlineData("It is a lovely day outside. Shall we go for a walk? Yes! Great.", true, true, false, false, 0,
        "It is a lovely day outside.|Shall we go for a walk? Yes! Great.")]
    // With every stop off, a long piece still ends at its next stop.
    [InlineData("This first sentence runs on for a while to make the piece long. And this second one also keeps going on and on, until here, then more.",
        false, false, false, false, 0,
        "This first sentence runs on for a while to make the piece long. And this second one also keeps going on and on,|until here, then more.")]
    public void Persona_speech_breaks_choose_where_pieces_end(string input, bool commas, bool periods, bool questions,
        bool exclamations, int shortEnding, string expected)
    {
        var breaks = new SpeechBreaks
        {
            Commas = commas, Periods = periods, QuestionMarks = questions, ExclamationMarks = exclamations, ShortEndingWords = shortEnding
        };
        for (int split = 0; split <= input.Length; split++)
        {
            var segmenter = new SpeechSegmenter(1536, 16_384, eagerFirstClause: true, breaks: breaks);
            var pieces = segmenter.Push(input[..split]).Concat(segmenter.Push(input[split..])).Concat(segmenter.Finish()).ToArray();
            Assert.Equal(expected, string.Join('|', pieces.Where(x => x.Text is not null).Select(x => x.Text)));
        }
    }

    [Fact]
    public void A_joined_ending_keeps_its_character_cues_and_a_line_end_lets_a_waiting_piece_go()
    {
        var segmenter = new SpeechSegmenter(1536, 16_384, eagerFirstClause: true, characterTags: ["{blush}"], breaks: SpeechBreaks.Default);
        var pieces = segmenter.Push("I'm so glad you're here with me today, {blush}cutie. ").ToArray();
        Assert.Empty(pieces);
        pieces = segmenter.Push("\n").ToArray();
        var piece = Assert.Single(pieces);
        Assert.Equal("I'm so glad you're here with me today, cutie.", piece.Text);
        var cue = Assert.Single(piece.Cues!);
        Assert.Equal("{blush}", cue.Tag);
        Assert.Equal(piece.Text!.IndexOf("cutie", StringComparison.Ordinal), cue.Offset);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(511)]
    [InlineData(1535)]
    [InlineData(1536)]
    public void Utf8_caps_never_split_surrogate_pairs(int limit)
    {
        var text = string.Concat(Enumerable.Repeat("a\u00e9\u6f22\U0001f600", 512)) + ".";
        var parts = SpeechSegmenter.Split(text, limit).ToArray();
        Assert.Equal(text, string.Concat(parts));
        Assert.All(parts, part => Assert.InRange(new UTF8Encoding(false, true).GetByteCount(part), 1, limit));
        var segmenter = new SpeechSegmenter(limit, 16_384);
        var streamed = text.SelectMany(c => segmenter.Push(c.ToString())).Concat(segmenter.Finish())
            .Where(x => x.Text is not null).Select(x => x.Text);
        Assert.Equal(text, string.Concat(streamed));
    }

    [Fact]
    public void Long_unfinished_sentence_is_bounded_and_invalid_unicode_fails()
    {
        var segmenter = new SpeechSegmenter(1536, 10);
        Assert.Empty(segmenter.Push("1234567890"));
        Assert.Throws<ConversationException>(() => segmenter.Push("x").ToArray());
        Assert.Throws<ConversationException>(() => SpeechSegmenter.Split("\ud800", 1536).ToArray());
    }
}
