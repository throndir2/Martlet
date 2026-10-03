using System.Text;

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
