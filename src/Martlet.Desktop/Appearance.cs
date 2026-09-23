using System.IO;
using System.Windows;
using System.Windows.Media;

namespace Martlet.Desktop;

internal enum PinkTheme { Light, Dark }

internal static class Appearance
{
    internal static ResourceDictionary Palette(PinkTheme theme, bool highContrast)
    {
        var dark = theme == PinkTheme.Dark;
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

    internal static (PinkTheme Theme, string? Notice) LoadForStartup(string directory)
    {
        try { return (Load(directory), null); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return (PinkTheme.Light, "Could not read appearance.txt. Using pink light for now; choose a theme to save a new preference. Profile settings were not changed.");
        }
    }

    internal static PinkTheme Load(string directory)
    {
        var path = Path.Combine(directory, "appearance.txt");
        try
        {
            using var reader = new StreamReader(path);
            var buffer = new char[16];
            var count = reader.ReadBlock(buffer, 0, buffer.Length);
            return new string(buffer, 0, count) switch
            {
                "Light" => PinkTheme.Light,
                "Dark" => PinkTheme.Dark,
                _ => throw new InvalidDataException("Unrecognized appearance preference.")
            };
        }
        catch (FileNotFoundException) { return PinkTheme.Light; }
        catch (DirectoryNotFoundException) { return PinkTheme.Light; }
    }

    internal static void Save(string directory, PinkTheme theme)
    {
        if (!Enum.IsDefined(theme)) throw new ArgumentOutOfRangeException(nameof(theme));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "appearance.txt");
        var temporary = Path.Combine(directory, $"appearance.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporary, theme.ToString());
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
