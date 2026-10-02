using System.IO;
using System.Windows;
using Martlet.Presentation;

namespace Martlet.Desktop;

internal enum PinkTheme { Light, Dark }

internal static class Appearance
{
    internal static ResourceDictionary Palette(PinkTheme theme, bool highContrast) =>
        AppearancePalette.Create(theme == PinkTheme.Dark, highContrast);

    internal static (PinkTheme Theme, string? Notice) LoadForStartup(string directory)
    {
        try { return (Load(directory), null); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return (PinkTheme.Light, "Couldn't load your theme. Using pink light for now.");
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
