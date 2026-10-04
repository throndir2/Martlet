using System.Windows;
using System.Windows.Media;

namespace Martlet.Presentation;

/// <summary>The twelve brushes every Martlet window is drawn with (<c>CanvasBrush</c>, <c>AccentBrush</c>...) and the mascot
/// (<c>MascotImage</c>). Pink light and Rose dark are Martlet's own; a character palette (<paramref name="colors"/> in
/// <see cref="Create"/>, #RRGGBB by role: Canvas, Surface, Soft, Text, Muted, Border, Accent, OnAccent, Focus, Success, Warning,
/// Glow) replaces them role by role and gives the mascot's badge the accent's hue. Windows' high contrast always wins.</summary>
internal static class AppearancePalette
{
    internal static IReadOnlyList<string> Roles { get; } =
        ["Canvas", "Surface", "Soft", "Text", "Muted", "Border", "Accent", "OnAccent", "Focus", "Success", "Warning", "Glow"];

    /// <summary>Martlet's own palette (Pink light or Rose dark) by role, as #RRGGBB.</summary>
    internal static IReadOnlyDictionary<string, string> Pink(bool dark) => new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["Canvas"] = dark ? "#211923" : "#FFF5F8",
        ["Surface"] = dark ? "#302432" : "#FFFFFF",
        ["Soft"] = dark ? "#402D3F" : "#FCE5EE",
        ["Text"] = dark ? "#FFF0F5" : "#432C3A",
        ["Muted"] = dark ? "#D4B8C9" : "#785468",
        ["Border"] = dark ? "#AC829D" : "#9B6780",
        ["Accent"] = dark ? "#F5A6CA" : "#A52D64",
        ["OnAccent"] = dark ? "#321A29" : "#FFFFFF",
        ["Focus"] = dark ? "#FFD0E5" : "#8A2152",
        ["Success"] = dark ? "#8FD9A8" : "#25693F",
        ["Warning"] = dark ? "#FFC977" : "#8A4F00",
        ["Glow"] = dark ? "#6B3A5C" : "#F7C6DA"
    };

    internal static ResourceDictionary Create(bool dark, bool highContrast, IReadOnlyDictionary<string, string>? colors = null)
    {
        var pink = Pink(dark);
        var chosen = new Dictionary<string, Color>();
        foreach (var role in Roles)
            chosen[role + "Brush"] = colors is not null && colors.TryGetValue(role, out var custom) && TryColor(custom, out var color)
                ? color : Color(pink[role]);
        if (highContrast)
        {
            chosen["CanvasBrush"] = chosen["SurfaceBrush"] = chosen["SoftBrush"] = SystemColors.WindowColor;
            chosen["TextBrush"] = chosen["MutedBrush"] = SystemColors.WindowTextColor;
            chosen["BorderBrush"] = chosen["FocusBrush"] = SystemColors.WindowTextColor;
            chosen["SuccessBrush"] = chosen["WarningBrush"] = SystemColors.WindowTextColor;
            chosen["GlowBrush"] = SystemColors.HighlightColor;
            chosen["AccentBrush"] = SystemColors.HighlightColor;
            chosen["OnAccentBrush"] = SystemColors.HighlightTextColor;
        }
        // The mascot's badge: Martlet's pink, or in a character palette the accent's hue (a mid ring, a pale disc), like the pink
        // badge is to Pink light's accent. Windows' high contrast keeps the mascot as it is.
        var badge = colors is not null && colors.TryGetValue("Accent", out var accentHex) && TryColor(accentHex, out _) ? accentHex : null;
        Color ring, disc;
        if (badge is null || highContrast) (ring, disc) = (Color("#A52D64"), Color("#F6AACD"));
        else
        {
            var accent = Martlet.Avatar.Hosting.Oklch.FromHex(badge);
            ring = Color(accent.WithL(Math.Clamp(accent.L, 0.42, 0.56)).WithC(Math.Max(accent.C, 0.06)).Hex);
            disc = Color(new Martlet.Avatar.Hosting.Oklch(0.84, Math.Clamp(accent.C * 0.6, 0.04, 0.1), accent.H).Hex);
        }
        var resources = new ResourceDictionary();
        foreach (var (key, color) in chosen)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            resources.Add(key, brush);
        }
        resources.Add("MascotImage", Mascot(ring, disc));
        return resources;
    }

    /// <summary>The Martlet mascot: the cream bird from Assets\Martlet.svg on a round badge whose ring, disc, wing, beak and
    /// feet take <paramref name="ring"/> and <paramref name="disc"/>. Keep in step with the app icon.</summary>
    private static DrawingImage Mascot(Color ring, Color disc)
    {
        var group = new DrawingGroup();
        void Add(Color color, Geometry geometry) => group.Children.Add(new GeometryDrawing(new SolidColorBrush(color), null, geometry));
        Add(ring, new EllipseGeometry(new Point(128, 136), 114, 114));
        Add(disc, new EllipseGeometry(new Point(128, 136), 106, 106));
        Add(Color("#FFE8F1"), Geometry.Parse("M 45 127 C 45 80 80 47 126 47 C 172 47 211 82 211 132 C 211 180 180 219 132 219 C 81 219 45 185 45 127 Z"));
        Add(ring, Geometry.Parse("M 100 201 L 110 216 L 123 201 Z M 139 201 L 151 216 L 160 200 Z"));
        Add(Color("#FFF9EE"), Geometry.Parse("M 64 138 C 64 107 78 84 104 77 C 106 64 118 59 132 61 L 125 76 C 163 73 188 102 188 140 C 188 158 193 185 209 192 " +
            "L 187 197 L 196 210 C 178 212 169 204 163 199 C 143 211 110 211 91 200 L 60 210 L 69 190 C 54 178 53 154 64 138 Z"));
        Add(disc, Geometry.Parse("M 65 148 C 79 151 93 168 100 187 C 83 188 65 174 65 148 Z"));
        Add(Color("#432C3A"), Geometry.Parse("M 91 130 C 91 119 108 119 108 130 C 108 141 91 141 91 130 Z M 147 130 C 147 119 164 119 164 130 C 164 141 147 141 147 130 Z"));
        Add(Color("#F28DB9"), Geometry.Parse("M 78 150 C 78 139 102 139 102 150 C 102 161 78 161 78 150 Z M 153 150 C 153 139 177 139 177 150 C 177 161 153 161 153 150 Z"));
        Add(ring, Geometry.Parse("M 117 145 Q 128 139 139 145 L 128 157 Z"));
        var image = new DrawingImage(group);
        image.Freeze();
        return image;
    }

    /// <summary>Whether <paramref name="colors"/> has a #RRGGBB color for every role.</summary>
    internal static bool IsComplete(IReadOnlyDictionary<string, string>? colors) =>
        colors is not null && Roles.All(role => colors.TryGetValue(role, out var value) && TryColor(value, out _));

    private static bool TryColor(string? value, out Color color)
    {
        color = default;
        if (value is not { Length: 7 } || value[0] != '#' || !value.AsSpan(1).ContainsOnlyHex()) return false;
        color = Color(value);
        return true;
    }

    private static Color Color(string value) => (Color)ColorConverter.ConvertFromString(value);
}

file static class HexSpan
{
    internal static bool ContainsOnlyHex(this ReadOnlySpan<char> text)
    {
        foreach (var c in text) if (!char.IsAsciiHexDigit(c)) return false;
        return true;
    }
}
