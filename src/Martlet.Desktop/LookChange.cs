using Martlet.Conversation;
using Martlet.Core.Reading;

// Also built into Martlet's MCP server (active_app_check), in its own namespace.
#if MARTLET_MCP
namespace Martlet.Mcp.Shared;
#else
namespace Martlet.Desktop;
#endif

/// <summary>How much changed since Martlet's last look: there is no earlier look, nothing changed, only small things changed,
/// or it changed.</summary>
internal enum SinceLook { First, Same, Little, Changed }

/// <summary>What a look saw, to compare later pictures with: the picture's 16x9 grey thumbnail (the glancer's signature), where
/// it was taken (the source, the window's title, the program in front and whether it filled its monitor), the text read on it
/// (null: none read) and when (the talk window's clock). Never the picture itself.</summary>
internal sealed record LookedPicture(byte[] Signature, string Source, string Title, string App, bool FullScreen, string? Text, long At);

/// <summary>Tells a still screen from a changed one since Martlet's last look (docs/SCREEN_COMMENTARY.md). A picture that shows
/// the same thing as the last look (the same window, and the thumbnail and the text read on it all but the same) gets no look:
/// the model already saw it, and nothing new happened, so there is nothing to say. Every other look tells the model how much
/// changed since its last look and how long nobody used the keyboard and mouse (the {changed} words of Companion › Prompts ›
/// Screen glance message), so it stays quiet when nothing really changed.</summary>
internal static class LookChange
{
    /// <summary>Below this much change of the thumbnail (0 to 1), the picture shows the same thing: a clock, a blinking cursor
    /// or a loading icon changes less.</summary>
    internal const double Still = 0.002;
    /// <summary>Below this much change of the thumbnail, in the same program, only small things changed (a line of text, a
    /// pop-up).</summary>
    internal const double Small = 0.02;
    /// <summary>At most this many words read on the screen may come or go while the text still counts as the same: a clock that
    /// ticks over the hour changes four.</summary>
    internal const int TextStillWords = 4;
    /// <summary>Below this much change of the words read on the screen (0 to 1), only some of the text changed.</summary>
    internal const double TextSmall = 0.2;
    /// <summary>The look's message says how long nobody used the keyboard and mouse once it is at least this long.</summary>
    internal static TimeSpan IdleWorthSaying => TimeSpan.FromMinutes(1);

    /// <summary>How much changed from <paramref name="last"/> (the last look's picture; null: none) to <paramref name="now"/>
    /// (null: a picture without a thumbnail, which counts as changed). The text read on them counts only when both have some.</summary>
    internal static SinceLook Compare(LookedPicture? last, LookedPicture? now)
    {
        if (last is null) return SinceLook.First;
        if (now is null || last.Source != now.Source || last.Signature.Length == 0 || last.Signature.Length != now.Signature.Length)
            return SinceLook.Changed;
        var picture = Difference(last.Signature, now.Signature);
        // A few words that come or go (a clock) never count; more count by their share of the words.
        var words = last.Text is not null && now.Text is not null ? ChangedWords(last.Text, now.Text) : 0;
        var text = words > TextStillWords ? ScreenText.Change(last.Text, now.Text) : 0;
        if (last.App != now.App || last.FullScreen != now.FullScreen || picture >= Small || text >= TextSmall) return SinceLook.Changed;
        return picture < Still && words <= TextStillWords && last.Title == now.Title ? SinceLook.Same : SinceLook.Little;
    }

    /// <summary>How different two thumbnails of the same size are, 0 (identical) to 1.</summary>
    internal static double Difference(byte[] a, byte[] b) => a.Zip(b, (x, y) => Math.Abs(x - y)).Average() / 255.0;

    /// <summary>How many words came or went from <paramref name="before"/> to <paramref name="after"/>.</summary>
    internal static int ChangedWords(string? before, string? after)
    {
        var old = ScreenText.Words(before);
        var now = ScreenText.Words(after);
        return old.Count(word => !now.Contains(word)) + now.Count(word => !old.Contains(word));
    }

    /// <summary>Whether Martlet holds off a look: nothing changed since its last look.</summary>
    internal static bool Holds(SinceLook since) => since == SinceLook.Same;

    /// <summary>The {changed} words of a look's message: how much changed since the last look and how long ago it was, then,
    /// once it is at least <see cref="IdleWorthSaying"/>, how long nobody used the keyboard and mouse (zero for a camera).</summary>
    internal static string Describe(SinceLook since, TimeSpan? ago, TimeSpan idle)
    {
        var when = ago is { } before ? $" {Reminders.Span(before)} ago" : "";
        var text = since switch
        {
            SinceLook.First => "No earlier look to compare with.",
            SinceLook.Same => $"Nothing changed since your last look{when}.",
            SinceLook.Little => $"Only small things changed since your last look{when}.",
            _ => $"It changed since your last look{when}."
        };
        return idle >= IdleWorthSaying ? $"{text} Nobody has used the keyboard or mouse for {Reminders.Span(idle)}." : text;
    }
}
