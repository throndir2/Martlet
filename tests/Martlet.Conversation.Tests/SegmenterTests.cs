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
    [InlineData("Well, that is a really good question, and here is more; still more. Next one, with a comma, here.",
        "Well, that is a really good question, and here is more; still more.|Next one, with a comma, here.")]
    [InlineData("Short, then a full stop. After.", "Short, then a full stop.|After.")]
    [InlineData("I thought about it for a long while \u2014 a really long while \u2013 and then I answered. Done.",
        "I thought about it for a long while \u2014 a really long while \u2013 and then I answered.|Done.")]
    [InlineData("In the sky tonight we counted 1,000,000 stars. Done.", "In the sky tonight we counted 1,000,000 stars.|Done.")]
    // Even a long piece only ends at a sentence end, never at a comma, semicolon or dash.
    [InlineData("This first clause runs on for quite a while, and the second clause keeps going and going, then a third one; " +
        "a fourth \u2014 and a fifth, until it finally stops. Next.",
        "This first clause runs on for quite a while, and the second clause keeps going and going, then a third one; " +
        "a fourth \u2014 and a fifth, until it finally stops.|Next.")]
    public void Commas_semicolons_and_dashes_never_end_a_piece(string input, string expected)
    {
        foreach (var breaks in new SpeechBreaks?[] { null, SpeechBreaks.Default with { ShortEndingWords = 0 } })
            for (int split = 0; split <= input.Length; split++)
            {
                var segmenter = new SpeechSegmenter(1536, 16_384, breaks: breaks);
                var pieces = segmenter.Push(input[..split]).Concat(segmenter.Push(input[split..])).Concat(segmenter.Finish()).ToArray();
                Assert.Equal(expected, string.Join('|', pieces.Where(x => x.Text is not null).Select(x => x.Text)));
            }
    }

    [Theory]
    // A short ending joins the piece before it.
    [InlineData("That was a wonderful idea. Cutie! And now we can get going.", true, true, true, 2,
        "That was a wonderful idea. Cutie!|And now we can get going.")]
    [InlineData("That was a wonderful idea. Cutie! And now we can get going.", true, true, true, 0,
        "That was a wonderful idea.|Cutie!|And now we can get going.")]
    // A comma never breaks, so a comma-led ending stays with its sentence.
    [InlineData("I'm so glad you're here with me today, cutie. Shall we start?", true, true, true, 0,
        "I'm so glad you're here with me today, cutie.|Shall we start?")]
    // Longer endings stand on their own.
    [InlineData("That was a wonderful idea. My favorite person! Hi.", true, true, true, 2,
        "That was a wonderful idea.|My favorite person! Hi.")]
    // A stop that is off doesn't break.
    [InlineData("It is a lovely day outside. Shall we go for a walk? Yes!", false, true, true, 0,
        "It is a lovely day outside. Shall we go for a walk?|Yes!")]
    [InlineData("It is a lovely day outside. Shall we go for a walk? Yes! Great.", true, false, false, 0,
        "It is a lovely day outside.|Shall we go for a walk? Yes! Great.")]
    // With every stop off, a long piece still ends at its next sentence end, never at a comma.
    [InlineData("This first sentence runs on for a while to make the piece long. And this second one also keeps going on and on, until here. Then more.",
        false, false, false, 0,
        "This first sentence runs on for a while to make the piece long. And this second one also keeps going on and on, until here.|Then more.")]
    public void Persona_speech_breaks_choose_where_pieces_end(string input, bool periods, bool questions,
        bool exclamations, int shortEnding, string expected)
    {
        var breaks = new SpeechBreaks
        {
            Periods = periods, QuestionMarks = questions, ExclamationMarks = exclamations, ShortEndingWords = shortEnding
        };
        for (int split = 0; split <= input.Length; split++)
        {
            var segmenter = new SpeechSegmenter(1536, 16_384, breaks: breaks);
            var pieces = segmenter.Push(input[..split]).Concat(segmenter.Push(input[split..])).Concat(segmenter.Finish()).ToArray();
            Assert.Equal(expected, string.Join('|', pieces.Where(x => x.Text is not null).Select(x => x.Text)));
        }
    }

    [Fact]
    public void A_joined_ending_keeps_its_character_cues_and_a_line_end_lets_a_waiting_piece_go()
    {
        var segmenter = new SpeechSegmenter(1536, 16_384, characterTags: ["{blush}"], breaks: SpeechBreaks.Default);
        var pieces = segmenter.Push("That was a wonderful idea. {blush}Cutie! ").ToArray();
        Assert.Empty(pieces);
        pieces = segmenter.Push("\n").ToArray();
        var piece = Assert.Single(pieces);
        Assert.Equal("That was a wonderful idea. Cutie!", piece.Text);
        var cue = Assert.Single(piece.Cues!);
        Assert.Equal("{blush}", cue.Tag);
        Assert.Equal(piece.Text!.IndexOf("Cutie", StringComparison.Ordinal), cue.Offset);
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
