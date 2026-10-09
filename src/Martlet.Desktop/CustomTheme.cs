using System.Globalization;
using Martlet.Avatar.Hosting;

namespace Martlet.Desktop;

/// <summary>The parts of Martlet a custom palette colors (Settings › Appearance › Custom), one per <see cref="ThemeRoles"/>
/// role, with the name and help the editor shows.</summary>
internal static class CustomThemeParts
{
    internal static IReadOnlyList<(string Role, string Name, string Help)> All { get; } =
    [
        (ThemeRoles.Canvas, "Window background", "Behind everything in Martlet's windows."),
        (ThemeRoles.Surface, "Cards", "Cards, lists, text boxes, menus and the speech bubble."),
        (ThemeRoles.Soft, "Buttons and side bar", "Buttons, chips, read-only boxes, panels and the navigation rail."),
        (ThemeRoles.Text, "Text", "Most of the text."),
        (ThemeRoles.Muted, "Quiet text", "Hints, details and second lines."),
        (ThemeRoles.Border, "Outlines", "Lines around cards, buttons and boxes."),
        (ThemeRoles.Accent, "Accent", "Main buttons, selected items, links, icons and headings. The mascot's badge takes its hue."),
        (ThemeRoles.OnAccent, "Text on accent", "Text on main buttons and selected items."),
        (ThemeRoles.Focus, "Focus ring", "The ring around the control the keyboard is on, and around what the mouse points at."),
        (ThemeRoles.Success, "Good news", "Ready and OK states."),
        (ThemeRoles.Warning, "Warnings", "Things that need your attention."),
        (ThemeRoles.Glow, "Glow", "The soft halo behind the mascot and the speech bubble, and hover backgrounds.")
    ];

    internal static string Name(string role) => All.First(part => part.Role == role).Name;
}

/// <summary>A color as hue (0-360 degrees), saturation and lightness (0-100 %), as the custom palette's sliders show it.</summary>
internal readonly record struct Hsl(double H, double S, double L)
{
    internal static Hsl FromHex(string hex)
    {
        if (!ThemeColor.TryParse(hex, out var normalized)) throw new FormatException("Colors are written #RRGGBB.");
        var value = int.Parse(normalized.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        double r = ((value >> 16) & 0xFF) / 255d, g = ((value >> 8) & 0xFF) / 255d, b = (value & 0xFF) / 255d;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), l = (max + min) / 2, d = max - min;
        if (d < 1e-9) return new(0, 0, l * 100);
        var s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
        var h = max == r ? (g - b) / d + (g < b ? 6 : 0) : max == g ? (b - r) / d + 2 : (r - g) / d + 4;
        return new(h * 60, s * 100, l * 100);
    }

    /// <summary>The color as upper-case #RRGGBB.</summary>
    internal string Hex
    {
        get
        {
            double s = Math.Clamp(S, 0, 100) / 100, l = Math.Clamp(L, 0, 100) / 100, h = (H % 360 + 360) % 360 / 60;
            var c = (1 - Math.Abs(2 * l - 1)) * s;
            var x = c * (1 - Math.Abs(h % 2 - 1));
            var (r, g, b) = (int)h switch
            {
                0 => (c, x, 0d), 1 => (x, c, 0d), 2 => (0d, c, x), 3 => (0d, x, c), 4 => (x, 0d, c), _ => (c, 0d, x)
            };
            var m = l - c / 2;
            static int Channel(double value) => (int)Math.Round(Math.Clamp(value, 0, 1) * 255);
            return string.Create(CultureInfo.InvariantCulture, $"#{Channel(r + m):X2}{Channel(g + m):X2}{Channel(b + m):X2}");
        }
    }
}
