using System.IO;
using System.Text.Json;
using System.Windows;
using Martlet.Presentation;

namespace Martlet.Desktop;

/// <summary>The palette every Martlet window uses (Settings › Appearance): Martlet's own Pink light or Rose dark, or a light or
/// dark palette made by Martlet's rules from the colors of the character this PC shows.</summary>
internal enum AppearanceTheme { Light, Dark, CharacterLight, CharacterDark }

internal static class Appearance
{
    /// <summary>The choices in Settings › Appearance, in order, with their names.</summary>
    internal static IReadOnlyList<(AppearanceTheme Theme, string Name)> Choices { get; } =
    [
        (AppearanceTheme.Light, "Pink light"), (AppearanceTheme.Dark, "Rose dark"),
        (AppearanceTheme.CharacterLight, "Character light"), (AppearanceTheme.CharacterDark, "Character dark")
    ];

    internal static string Name(this AppearanceTheme theme) => Choices.First(c => c.Theme == theme).Name;
    internal static bool IsDark(this AppearanceTheme theme) => theme is AppearanceTheme.Dark or AppearanceTheme.CharacterDark;
    internal static bool FromCharacter(this AppearanceTheme theme) => theme >= AppearanceTheme.CharacterLight;

    /// <summary>The brushes for <paramref name="theme"/>: a character theme's <paramref name="colors"/> (Martlet's own palette
    /// of the same lightness until they are known), or Martlet's own.</summary>
    internal static ResourceDictionary Palette(AppearanceTheme theme, bool highContrast, IReadOnlyDictionary<string, string>? colors = null) =>
        AppearancePalette.Create(theme.IsDark(), highContrast, theme.FromCharacter() ? colors : null);

    internal static (AppearanceTheme Theme, string? Notice) LoadForStartup(string directory)
    {
        try { return (Load(directory), null); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return (AppearanceTheme.Light, "Couldn't load your theme. Using pink light for now.");
        }
    }

    internal static AppearanceTheme Load(string directory)
    {
        var path = Path.Combine(directory, "appearance.txt");
        try
        {
            using var reader = new StreamReader(path);
            var buffer = new char[32];
            var count = reader.ReadBlock(buffer, 0, buffer.Length);
            return Parse(new string(buffer, 0, count)) ?? throw new InvalidDataException("Unrecognized appearance preference.");
        }
        catch (FileNotFoundException) { return AppearanceTheme.Light; }
        catch (DirectoryNotFoundException) { return AppearanceTheme.Light; }
    }

    /// <summary>The theme a saved or shared name stands for, or null for a name this Martlet doesn't know. The retired palettes
    /// the Thinking model made read as the rule-based character palettes.</summary>
    internal static AppearanceTheme? Parse(string? name) => name switch
    {
        "ThinkingLight" => AppearanceTheme.CharacterLight,
        "ThinkingDark" => AppearanceTheme.CharacterDark,
        _ => Enum.TryParse<AppearanceTheme>(name, out var theme) && Enum.IsDefined(theme) && name == theme.ToString() ? theme : null
    };

    internal static void Save(string directory, AppearanceTheme theme)
    {
        if (!Enum.IsDefined(theme)) throw new ArgumentOutOfRangeException(nameof(theme));
        Write(directory, "appearance.txt", theme.ToString());
    }

    private const string ColorsFile = "appearance-colors.json";

    private sealed record SavedColors(string Theme, Dictionary<string, string> Colors);

    /// <summary>The character colors last applied with <paramref name="theme"/>, so Martlet starts in them before it has read
    /// the character again; null when they were saved for another theme or can't be read.</summary>
    internal static IReadOnlyDictionary<string, string>? LoadColors(string directory, AppearanceTheme theme)
    {
        if (!theme.FromCharacter()) return null;
        try
        {
            var path = Path.Combine(directory, ColorsFile);
            if (!File.Exists(path) || new FileInfo(path).Length > 4096) return null;
            var saved = JsonSerializer.Deserialize<SavedColors>(File.ReadAllBytes(path));
            return saved is not null && saved.Theme == theme.ToString() && AppearancePalette.IsComplete(saved.Colors) ? saved.Colors : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or NotSupportedException) { return null; }
    }

    /// <summary>Keeps the character colors applied with <paramref name="theme"/> for the next start.</summary>
    internal static void SaveColors(string directory, AppearanceTheme theme, IReadOnlyDictionary<string, string> colors)
    {
        if (!theme.FromCharacter() || !AppearancePalette.IsComplete(colors)) return;
        Write(directory, ColorsFile, JsonSerializer.Serialize(new SavedColors(theme.ToString(),
            AppearancePalette.Roles.ToDictionary(role => role, role => colors[role], StringComparer.Ordinal))));
    }

    private static void Write(string directory, string name, string text)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name);
        var temporary = Path.Combine(directory, $"{Path.GetFileNameWithoutExtension(name)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporary, text);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
