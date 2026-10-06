using System.IO;
using Martlet.Avatar.Hosting;
using Martlet.Avatars;
using Martlet.Core.Contracts;
using Martlet.Presentation;

namespace Martlet.Desktop;

/// <summary>The colors of the character this PC shows, for Settings › Appearance's character palettes: read once per model
/// from its textures (kept in character-themes.json) and Martlet's rule-based light and dark palettes made from them.</summary>
internal sealed class CharacterThemeService(string? dataDirectory)
{
    private readonly SemaphoreSlim loading = new(1, 1);
    private Loaded? current;
    private string? path;
    private string? problem;

    /// <summary>One model's colors and palettes.</summary>
    internal sealed record Loaded(string ModelId, CharacterThemeEntry Entry, ThemePalette RulesLight, ThemePalette RulesDark);

    /// <summary>Raised (on any thread) when the colors or palettes change.</summary>
    internal event Action? Changed;

    /// <summary>The model path whose colors are loaded (or couldn't be read), or null.</summary>
    internal string? Path => Volatile.Read(ref path);
    internal Loaded? Current => Volatile.Read(ref current);
    /// <summary>Why the model's colors couldn't be read, or null.</summary>
    internal string? Problem => Volatile.Read(ref problem);

    /// <summary>The colors <paramref name="theme"/> uses with the loaded character, or null for Martlet's own palettes and while
    /// no character is loaded.</summary>
    internal IReadOnlyDictionary<string, string>? Colors(AppearanceTheme theme) => Current is not { } loaded || !theme.FromCharacter() ? null
        : theme == AppearanceTheme.CharacterLight ? loaded.RulesLight.Colors : loaded.RulesDark.Colors;

    /// <summary>Reads the colors of <paramref name="profile"/>'s model (the built-in character when null) unless they are
    /// already loaded. Decoding the textures happens once per model; afterwards the saved colors are used.</summary>
    internal async Task LoadAsync(AvatarProfile? profile, bool force, CancellationToken token)
    {
        var modelPath = profile?.ModelPath ?? BundledLive2D.Prefix + BundledLive2D.DefaultCharacter;
        var renderer = profile?.Renderer ?? AvatarRenderer.Live2D;
        await loading.WaitAsync(token);
        try
        {
            if (!force && string.Equals(Path, modelPath, StringComparison.OrdinalIgnoreCase)) return;
            Loaded? next = null;
            string? why = null;
            try
            {
                next = await Task.Run(async () =>
                {
                    var (id, images) = await CharacterThemeImages.ReadModelAsync(renderer, modelPath, token);
                    var saved = dataDirectory is null ? null : CharacterThemes.Load(dataDirectory, id);
                    var entry = saved;
                    if (saved is null || saved.Analyzer != CharacterColors.AnalyzerVersion || saved.Swatches.Count == 0)
                    {
                        var swatches = CharacterThemeImages.Analyze(images);
                        if (swatches.Count == 0) throw new InvalidDataException("Martlet found no colors in this model's textures.");
                        entry = new CharacterThemeEntry { ModelId = id, Analyzer = CharacterColors.AnalyzerVersion, Swatches = swatches };
                        if (dataDirectory is not null)
                            try { entry = await CharacterThemes.SaveAsync(dataDirectory, entry, DateTimeOffset.Now, token); }
                            catch (Exception error) when (error is ContractException or IOException or UnauthorizedAccessException)
                            {
                                ErrorLog.Warn("Couldn't save the character's colors", error);
                            }
                        ErrorLog.Info($"Read {swatches.Count} colors from the character's textures.");
                    }
                    return new Loaded(id, entry!, CharacterThemeRules.Build(entry!.Swatches, dark: false),
                        CharacterThemeRules.Build(entry.Swatches, dark: true));
                }, token);
            }
            catch (Exception error) when (error is ContractException or IOException or UnauthorizedAccessException or InvalidOperationException or
                InvalidDataException)
            {
                why = error.Message;
            }
            Volatile.Write(ref current, next);
            Volatile.Write(ref problem, why);
            Volatile.Write(ref path, modelPath);
        }
        finally { loading.Release(); }
        Changed?.Invoke();
    }
}
