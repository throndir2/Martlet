using System.Windows;
using Martlet.Avatar.Hosting;
using Martlet.Core.Settings;

namespace Martlet.Desktop;

/// <summary>The services MainWindow gives Companion › Speech bubbles (<see cref="SpeechBubblesPage"/> in Pages\SpeechBubbles). The
/// speech captions that show the bubbles and subtitles stay in the window.</summary>
public partial class MainWindow : ISpeechBubblesPageServices
{
    private SpeechBubblesPage? speechBubblesPage;

    /// <summary>The Speech bubbles page, made once.</summary>
    private SpeechBubblesPage SpeechBubblesPage => speechBubblesPage ??= new(this);

    SpeechDisplayPreferences ISpeechBubblesPageServices.Preferences => captions.Preferences;
    bool ISpeechBubblesPageServices.Save(SpeechDisplayPreferences next) => captions.Update(next);
    bool ISpeechBubblesPageServices.AvatarShowing => avatar.IsShowing;
    bool ISpeechBubblesPageServices.Closing => closing;
    Task<RendererBubble?> ISpeechBubblesPageServices.PreviewAsync(string text) => captions.PreviewAsync(text);
    string ISpeechBubblesPageServices.BubbleTheme(RendererBubble bubble) =>
        BubbleTheme(bubble, SelectedTheme.Name(), key => TryFindResource(key) as System.Windows.Media.Brush, SystemParameters.HighContrast);

    /// <summary>Whether the shown bubble is drawn in the palette Martlet's windows use now (<paramref name="brush"/> by resource
    /// key): its fill, outline, text and halo are the palette's Surface, Accent, Text and Glow (no halo in high contrast).
    /// Empty when the overlay didn't say which colors it used.</summary>
    internal static string BubbleTheme(RendererBubble bubble, string palette, Func<string, System.Windows.Media.Brush?> brush, bool highContrast)
    {
        if (bubble.Colors is not { } drawn) return "";
        var differing = new List<string>();
        void Check(string part, string? actual, string? role)
        {
            var expected = role is null ? null
                : brush(role + "Brush") is System.Windows.Media.SolidColorBrush solid ? $"#{solid.Color.R:X2}{solid.Color.G:X2}{solid.Color.B:X2}" : "";
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase)) differing.Add($"{part} {actual ?? "none"}");
        }
        Check("fill", drawn.Fill, "Surface");
        Check("outline", drawn.Outline, "Accent");
        Check("text", drawn.Text, "Text");
        Check("halo", drawn.Halo, highContrast ? null : "Glow");
        return differing.Count == 0 ? $", in the {palette} colors" : $", but not in the {palette} colors ({string.Join(", ", differing)})";
    }
}
