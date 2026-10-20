using Martlet.Avatar.Hosting;
using Martlet.Core.Settings;

namespace Martlet.Desktop;

/// <summary>Everything Companion › Speech bubbles (<see cref="SpeechBubblesPage"/>) reads and starts in the app. MainWindow gives it
/// to the page (MainWindow.SpeechBubblesPage.cs) and keeps the speech captions that show the bubbles and subtitles and save their
/// choices. It has no WPF types.</summary>
internal interface ISpeechBubblesPageServices
{
    /// <summary>Where Martlet's spoken words show, as saved.</summary>
    SpeechDisplayPreferences Preferences { get; }

    /// <summary>Applies <paramref name="next"/> from Martlet's next sentence and saves it; false when it couldn't be saved.</summary>
    bool Save(SpeechDisplayPreferences next);

    /// <summary>Whether the character shows on the desktop.</summary>
    bool AvatarShowing { get; }

    /// <summary>Whether the window is closing.</summary>
    bool Closing { get; }

    /// <summary>Shows <paramref name="text"/> in a speech bubble for a moment; null when it couldn't.</summary>
    Task<RendererBubble?> PreviewAsync(string text);

    /// <summary>Whether the shown bubble is drawn in the palette Martlet's windows use now, in words (see
    /// <see cref="MainWindow.BubbleTheme"/>).</summary>
    string BubbleTheme(RendererBubble bubble);
}
