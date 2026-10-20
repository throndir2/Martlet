using Martlet.Avatar.Hosting;
using Martlet.Core.Settings;

namespace Martlet.Desktop.Tests;

/// <summary>Companion › Speech bubbles' words and choices (<see cref="SpeechBubblesPageModel"/> in Pages\SpeechBubbles), without
/// WPF.</summary>
public sealed class SpeechBubblesPageModelTests
{
    [Fact]
    public void Speech_bubbles_offsets_read_only_numbers_in_range()
    {
        Assert.Null(SpeechBubblesPageModel.ReadOffset(null));
        Assert.Equal(20, SpeechBubblesPageModel.ReadOffset(" 20 "));
        Assert.True(double.IsNaN(SpeechBubblesPageModel.ReadOffset("-")!.Value));
        Assert.True(double.IsNaN(SpeechBubblesPageModel.ReadOffset((SpeechDisplayPreferences.MaximumOffset + 1).ToString())!.Value));
        Assert.True(SpeechBubblesPageModel.Shows("20", 20));
        Assert.False(SpeechBubblesPageModel.Shows("2", 20));
    }

    [Fact]
    public void Speech_bubbles_line_says_what_shows_and_where_the_bubble_goes()
    {
        Assert.Equal("Speech bubbles are on; subtitles are off; bubbles follow the character, offset by 0, 0.",
            SpeechBubblesPageModel.DisplayLine(new SpeechDisplayPreferences() with { BubbleOffsetX = 0, BubbleOffsetY = 0 }, true, null));
        Assert.Equal("Speech bubbles are off; subtitles are on; bubbles follow the character, offset by 0, 0. Saved.",
            SpeechBubblesPageModel.DisplayLine(new SpeechDisplayPreferences(SpeechBubbles: false, Subtitles: true) with
            { BubbleOffsetX = 0, BubbleOffsetY = 0 }, true, "Saved."));
        Assert.StartsWith("Speech bubbles appear when the character shows; subtitles are off; bubbles stay at 5, 6 from the top-left",
            SpeechBubblesPageModel.DisplayLine(new SpeechDisplayPreferences(StaticBubble: true) with { BubbleOffsetX = 5, BubbleOffsetY = 6 }, false, null));
        Assert.Equal(4, SpeechBubblesPageModel.Choices.Length);
        Assert.Equal("SetupCharacterSpeechOff", SpeechBubblesPageModel.Choices[^1].Id);
        Assert.Equal("to the left of the character's head, holding all its text",
            SpeechBubblesPageModel.BubbleWhere(new RendererBubble("left", 0, 0, 0, 0, true)));
    }
}
