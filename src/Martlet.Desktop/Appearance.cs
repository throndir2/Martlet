using System.IO;
using System.Text.Json;
using System.Windows;
using Martlet.Avatar.Hosting;
using Martlet.Presentation;

namespace Martlet.Desktop;

/// <summary>The palette every Martlet window uses (Settings › Appearance): Martlet's own Pink light or Rose dark, a light or
/// dark palette made by Martlet's rules from the colors of the character this PC shows, or the owner's own custom palette.</summary>
internal enum AppearanceTheme { Light, Dark, CharacterLight, CharacterDark, Custom }

internal static class Appearance
{
    /// <summary>The choices in Settings › Appearance, in order, with their names.</summary>
    internal static IReadOnlyList<(AppearanceTheme Theme, string Name)> Choices { get; } =
    [
        (AppearanceTheme.Light, "Pink light"), (AppearanceTheme.Dark, "Rose dark"),
        (AppearanceTheme.CharacterLight, "Character light"), (AppearanceTheme.CharacterDark, "Character dark"),
        (AppearanceTheme.Custom, "Custom")
    ];

    internal static string Name(this AppearanceTheme theme) => Choices.First(c => c.Theme == theme).Name;

    /// <summary>Whether <paramref name="theme"/> is a dark palette. A custom palette is dark when its window background
    /// (<paramref name="colors"/>' Canvas) is: light text reads better on it than dark text.</summary>
    internal static bool IsDark(this AppearanceTheme theme, IReadOnlyDictionary<string, string>? colors = null) => theme switch
    {
        AppearanceTheme.Custom => colors is not null && colors.TryGetValue(ThemeRoles.Canvas, out var canvas) &&
            ThemeColor.TryParse(canvas, out var hex) && ThemeColor.Contrast(hex, "#FFFFFF") > ThemeColor.Contrast(hex, "#000000"),
        _ => theme is AppearanceTheme.Dark or AppearanceTheme.CharacterDark
    };

    internal static bool FromCharacter(this AppearanceTheme theme) => theme is AppearanceTheme.CharacterLight or AppearanceTheme.CharacterDark;

    /// <summary>Whether <paramref name="theme"/> draws with colors of its own (the character's or the owner's) rather than
    /// Martlet's.</summary>
    internal static bool HasColors(this AppearanceTheme theme) => theme.FromCharacter() || theme == AppearanceTheme.Custom;

    /// <summary>The brushes for <paramref name="theme"/>: a character or custom theme's <paramref name="colors"/> (Martlet's own
    /// palette of the same lightness until they are known), or Martlet's own.</summary>
    internal static ResourceDictionary Palette(AppearanceTheme theme, bool highContrast, IReadOnlyDictionary<string, string>? colors = null) =>
        AppearancePalette.Create(theme.IsDark(colors), highContrast, theme.HasColors() ? colors : null);

    internal static (AppearanceTheme Theme, string? Notice) LoadForStartup(string directory)
    {
        try
        {
            var theme = Load(directory);
            return (theme, theme == AppearanceTheme.Custom && File.Exists(CustomPath(directory)) && LoadCustom(directory) is null
                ? "Couldn't load your custom palette. Using pink light colors for now." : null);
        }
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

    /// <summary>The colors <paramref name="theme"/> starts with: the owner's custom palette (Pink light's colors when there is
    /// none or it can't be read), or the character colors last applied with a character theme, so Martlet starts in them before
    /// it has read the character again (null when they were saved for another theme or can't be read).</summary>
    internal static IReadOnlyDictionary<string, string>? LoadColors(string directory, AppearanceTheme theme)
    {
        if (theme == AppearanceTheme.Custom) return LoadCustom(directory) ?? AppearancePalette.Pink(dark: false);
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

    private const string CustomFile = "appearance-custom.json";

    private sealed record SavedCustom(Dictionary<string, string> Colors);

    internal static string CustomPath(string directory) => Path.Combine(directory, CustomFile);

    /// <summary><paramref name="colors"/> as upper-case #RRGGBB in role order, or null when a role is missing or isn't a
    /// color.</summary>
    internal static IReadOnlyDictionary<string, string>? Normalize(IReadOnlyDictionary<string, string>? colors) =>
        ThemePalette.From(dark: false, colors)?.Colors;

    /// <summary>The owner's custom palette (appearance-custom.json), or null when there is none or it can't be read.</summary>
    internal static IReadOnlyDictionary<string, string>? LoadCustom(string directory)
    {
        try
        {
            var path = CustomPath(directory);
            if (!File.Exists(path) || new FileInfo(path).Length > 4096) return null;
            return ParseCustom(File.ReadAllText(path));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>A custom palette as <see cref="ShareCustom"/> writes it, or null when it isn't one.</summary>
    internal static IReadOnlyDictionary<string, string>? ParseCustom(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return Normalize(JsonSerializer.Deserialize<SavedCustom>(json)?.Colors); }
        catch (Exception error) when (error is JsonException or NotSupportedException) { return null; }
    }

    /// <summary>A custom palette as it is saved and shared with the owner's other computers: the same text for the same
    /// colors.</summary>
    internal static string ShareCustom(IReadOnlyDictionary<string, string> colors) =>
        JsonSerializer.Serialize(new SavedCustom(new Dictionary<string, string>(Normalize(colors)
            ?? throw new ArgumentException("A custom palette has a #RRGGBB color for every role.", nameof(colors)), StringComparer.Ordinal)));

    /// <summary>Keeps the owner's custom palette for the next start.</summary>
    internal static void SaveCustom(string directory, IReadOnlyDictionary<string, string> colors) =>
        Write(directory, CustomFile, ShareCustom(colors));

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
