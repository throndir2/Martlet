using System.Globalization;
using Martlet.Avatar.Hosting;
using Martlet.Core.Settings;

namespace Martlet.Desktop;

/// <summary>Companion › Speech bubbles' words and choices, without WPF: the four places Martlet's words can show, how an offset box
/// reads, the line on what shows now and where a preview bubble went. <see cref="SpeechBubblesPage"/> draws them; the Now line's
/// words are <see cref="MainWindow.SpeechBubblesNow"/> and <see cref="MainWindow.SpeechBubblesProblem"/>.</summary>
internal static class SpeechBubblesPageModel
{
    internal const string BubbleFollows = "Follows the character", BubbleStays = "Stays in one place";

    /// <summary>The page's main choice, in order: speech bubbles, subtitles, both or Off, each with its automation ID.</summary>
    internal static readonly (bool Bubbles, bool Subtitles, string Title, string Detail, string Id)[] Choices =
    [
        (true, false, "Speech bubbles", "Beside the character, while it shows.", "SetupCharacterSpeechBubbles"),
        (false, true, "Subtitles", "At the bottom of the active screen, hidden from screen capture.", "SetupCharacterSubtitles"),
        (true, true, "Speech bubbles and subtitles", "Both: a bubble beside the character and subtitles at the bottom of the screen.",
            "SetupCharacterSpeechBoth"),
        (false, false, "Off", "No speech bubbles or subtitles; the talk window still shows what Martlet says.", "SetupCharacterSpeechOff")
    ];

    /// <summary>An offset box's number of pixels: null for no box, NaN when the text isn't a number in range.</summary>
    internal static double? ReadOffset(string? text) =>
        text is null ? null
        : double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out var value) &&
          double.IsFinite(value) && Math.Abs(value) <= SpeechDisplayPreferences.MaximumOffset ? value
        : double.NaN;

    /// <summary>What the offsets must be, while one of them doesn't read.</summary>
    internal static string OffsetsProblem =>
        $"Offsets must be numbers of pixels from -{SpeechDisplayPreferences.MaximumOffset:0} to {SpeechDisplayPreferences.MaximumOffset:0}.";

    /// <summary>An offset as its box shows it.</summary>
    internal static string Offset(double value) => value.ToString("0.##", CultureInfo.CurrentCulture);

    /// <summary>Whether an offset box already shows <paramref name="value"/>, so typing in it isn't interrupted.</summary>
    internal static bool Shows(string text, double value) =>
        double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out var shown) && shown == value;

    /// <summary>The line on what shows now and where the bubble goes, with the last <paramref name="outcome"/>.</summary>
    internal static string DisplayLine(SpeechDisplayPreferences prefs, bool showing, string? outcome)
    {
        var bubbles = !prefs.SpeechBubbles ? "Speech bubbles are off"
            : showing ? "Speech bubbles are on"
            : "Speech bubbles appear when the character shows";
        var position = prefs.StaticBubble
            ? $"bubbles stay at {prefs.BubbleOffsetX:0.##}, {prefs.BubbleOffsetY:0.##} from the top-left of the character's screen"
            : $"bubbles follow the character, offset by {prefs.BubbleOffsetX:0.##}, {prefs.BubbleOffsetY:0.##}";
        return $"{bubbles}; subtitles are {(prefs.Subtitles ? "on" : "off")}; {position}." + (outcome is null ? "" : " " + outcome);
    }

    /// <summary>Where a preview bubble showed, its size and whether its text fit.</summary>
    internal static string BubbleWhere(RendererBubble bubble) => bubble.Placement switch
    {
        "left" => "to the left of the character's head",
        "right" => "to the right of the character's head",
        "above" => "above the character's head",
        "static" => "in its fixed place",
        "hidden" => "hidden",
        _ => "beside the character"
    } + (bubble.Width > 0 ? $" at {bubble.Left:0}, {bubble.Top:0} ({bubble.Width:0} × {bubble.Height:0})" : "") + (bubble.TextFits switch
    {
        true => ", holding all its text",
        false => ", but its text doesn't fit inside it",
        null => ""
    });
}
