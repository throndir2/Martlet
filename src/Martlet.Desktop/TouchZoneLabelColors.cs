using System.Windows;
using System.Windows.Media;
using Martlet.Avatar.Hosting;

namespace Martlet.Desktop;

/// <summary>The colors of a touch zone's name label on the Touch page: an opaque zone color with black or white text, whichever
/// reads better on it, or the system colors in Windows high contrast.</summary>
internal static class TouchZoneLabelColors
{
    internal static (Brush Foreground, Brush Background) For(Color zone, bool highContrast)
    {
        if (highContrast) return (SystemColors.HighlightTextBrush, SystemColors.HighlightBrush);
        return (new SolidColorBrush(TextOn(zone)), new SolidColorBrush(Color.FromRgb(zone.R, zone.G, zone.B)));
    }

    /// <summary>Black or white, whichever has more contrast on <paramref name="zone"/>.</summary>
    internal static Color TextOn(Color zone)
    {
        var hex = $"#{zone.R:X2}{zone.G:X2}{zone.B:X2}";
        return ThemeColor.Contrast(hex, "#FFFFFF") >= ThemeColor.Contrast(hex, "#000000") ? Colors.White : Colors.Black;
    }
}
