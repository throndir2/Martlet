using System.Windows;
using System.Windows.Media;

namespace Martlet.Presentation;

internal static class AppearancePalette
{
    internal static ResourceDictionary Create(bool dark, bool highContrast)
    {
        var colors = new Dictionary<string, Color>
        {
            ["CanvasBrush"] = Color(dark ? "#211923" : "#FFF5F8"),
            ["SurfaceBrush"] = Color(dark ? "#302432" : "#FFFFFF"),
            ["SoftBrush"] = Color(dark ? "#402D3F" : "#FCE5EE"),
            ["TextBrush"] = Color(dark ? "#FFF0F5" : "#432C3A"),
            ["MutedBrush"] = Color(dark ? "#D4B8C9" : "#785468"),
            ["BorderBrush"] = Color(dark ? "#AC829D" : "#9B6780"),
            ["AccentBrush"] = Color(dark ? "#F5A6CA" : "#A52D64"),
            ["OnAccentBrush"] = Color(dark ? "#321A29" : "#FFFFFF"),
            ["FocusBrush"] = Color(dark ? "#FFD0E5" : "#8A2152")
        };
        if (highContrast)
        {
            colors["CanvasBrush"] = colors["SurfaceBrush"] = colors["SoftBrush"] = SystemColors.WindowColor;
            colors["TextBrush"] = colors["MutedBrush"] = SystemColors.WindowTextColor;
            colors["BorderBrush"] = colors["FocusBrush"] = SystemColors.WindowTextColor;
            colors["AccentBrush"] = SystemColors.HighlightColor;
            colors["OnAccentBrush"] = SystemColors.HighlightTextColor;
        }
        var resources = new ResourceDictionary();
        foreach (var (key, color) in colors)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            resources.Add(key, brush);
        }
        return resources;
    }

    private static Color Color(string value) => (Color)ColorConverter.ConvertFromString(value);
}
