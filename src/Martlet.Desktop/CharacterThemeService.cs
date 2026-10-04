using System.IO;
using Martlet.Avatar.Hosting;
using Martlet.Avatars;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;
using Martlet.Presentation;
using Martlet.Providers;

namespace Martlet.Desktop;

/// <summary>The colors of the character this PC shows, for Settings › Appearance's character palettes: read once per model
/// from its textures (kept in character-themes.json), Martlet's rule-based light and dark palettes made from them, and the
/// palettes the Thinking model made from them and a picture of the character, when it has.</summary>
internal sealed class CharacterThemeService(string? dataDirectory)
{
    private readonly SemaphoreSlim loading = new(1, 1);
    private readonly HashSet<string> autoMade = new(StringComparer.Ordinal);
    private Loaded? current;
    private string? path;
    private string? problem;
    private string? thinking;
    private bool busy;

    /// <summary>One model's colors and palettes, and who its own files say it is.</summary>
    internal sealed record Loaded(string ModelId, AvatarRenderer Renderer, string ModelPath, CharacterThemeEntry Entry,
        ThemePalette RulesLight, ThemePalette RulesDark, CharacterIdentity Identity)
    {
        internal ThemePalette? ThinkingLight => Entry.Thinking?.Light;
        internal ThemePalette? ThinkingDark => Entry.Thinking?.Dark;
    }

    /// <summary>Raised (on any thread) when the colors, palettes or the Thinking status change.</summary>
    internal event Action? Changed;

    /// <summary>The model path whose colors are loaded (or couldn't be read), or null.</summary>
    internal string? Path => Volatile.Read(ref path);
    internal Loaded? Current => Volatile.Read(ref current);
    /// <summary>Why the model's colors couldn't be read, or null.</summary>
    internal string? Problem => Volatile.Read(ref problem);
    /// <summary>How making a palette with the Thinking model went (or is going), or null before it was asked.</summary>
    internal string? Thinking => Volatile.Read(ref thinking);
    internal bool Busy => Volatile.Read(ref busy);

    /// <summary>The colors <paramref name="theme"/> uses with the loaded character: the Thinking model's palette when it made
    /// one (else the rule-based one), or null for Martlet's own palettes and while no character is loaded.</summary>
    internal IReadOnlyDictionary<string, string>? Colors(AppearanceTheme theme) => Current is not { } loaded || !theme.FromCharacter() ? null
        : theme switch
        {
            AppearanceTheme.CharacterLight => loaded.RulesLight.Colors,
            AppearanceTheme.CharacterDark => loaded.RulesDark.Colors,
            AppearanceTheme.ThinkingLight => (loaded.ThinkingLight ?? loaded.RulesLight).Colors,
            _ => (loaded.ThinkingDark ?? loaded.RulesDark).Colors
        };

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
                    var (id, images, identity) = await CharacterThemeImages.ReadModelAsync(renderer, modelPath, token);
                    var saved = dataDirectory is null ? null : CharacterThemes.Load(dataDirectory, id);
                    var entry = saved;
                    if (saved is null || saved.Analyzer != CharacterColors.AnalyzerVersion || saved.Swatches.Count == 0)
                    {
                        var swatches = CharacterThemeImages.Analyze(images);
                        if (swatches.Count == 0) throw new InvalidDataException("Martlet found no colors in this model's textures.");
                        entry = new CharacterThemeEntry
                        {
                            ModelId = id, Analyzer = CharacterColors.AnalyzerVersion, Swatches = swatches, Thinking = saved?.Thinking, About = saved?.About
                        };
                        if (dataDirectory is not null)
                            try { entry = await CharacterThemes.SaveAsync(dataDirectory, entry, DateTimeOffset.Now, token); }
                            catch (Exception error) when (error is ContractException or IOException or UnauthorizedAccessException)
                            {
                                ErrorLog.Warn("Couldn't save the character's colors", error);
                            }
                        ErrorLog.Info($"Read {swatches.Count} colors from the character's textures.");
                    }
                    return new Loaded(id, renderer, modelPath, entry!, CharacterThemeRules.Build(entry!.Swatches, dark: false),
                        CharacterThemeRules.Build(entry.Swatches, dark: true), identity);
                }, token);
            }
            catch (Exception error) when (error is ContractException or IOException or UnauthorizedAccessException or InvalidOperationException or
                InvalidDataException)
            {
                why = error.Message;
            }
            Volatile.Write(ref current, next);
            Volatile.Write(ref problem, why);
            Volatile.Write(ref thinking, null);
            Volatile.Write(ref path, modelPath);
        }
        finally { loading.Release(); }
        Changed?.Invoke();
    }

    /// <summary>Whether the loaded model's Thinking palette should be made on its own: none made yet and not tried since
    /// Martlet started. Marks it tried.</summary>
    internal bool ClaimAutomatic()
    {
        lock (autoMade)
            return Current is { Entry.Thinking: null } loaded && autoMade.Add(loaded.ModelId);
    }

    /// <summary>Asks the Thinking model (through <paramref name="ask"/>: purpose, instructions, message, picture) for a light
    /// and a dark palette made from the loaded character: who it is (<paramref name="name"/>, the name the owner's character
    /// list gives it, with the name its files give it) and where it is from (the owner's words, else its files), its colors,
    /// Martlet's rule-based palettes and a picture of it (<paramref name="snapshot"/>: the character as it shows, or null for
    /// its own thumbnail or textures); gives plain grays the character's hue, keeps Martlet's rules and saves them. Call on the
    /// UI thread (the picture is drawn with WPF). Returns what happened, in words.</summary>
    internal async Task<string> MakeWithThinkingAsync(
        Func<string, string, string, BoundedImage?, CancellationToken, Task<(string? Answer, string? Failure)>> ask,
        byte[]? snapshot, string? name, PromptSettings? prompts, CancellationToken token)
    {
        if (Current is not { } loaded) return Report(Problem is null ? "Martlet is still reading the character's colors." : "Martlet couldn't read this character's colors.");
        if (CharacterThemePrompt.Instructions(prompts) is not { } instructions)
            return Report("Thinking palettes are off: Companion › Prompts › Character theme colors is empty.");
        Volatile.Write(ref busy, true);
        Report("Asking the Thinking model for the character's palettes...");
        try
        {
            var (_, images, _) = await Task.Run(() => CharacterThemeImages.ReadModelAsync(loaded.Renderer, loaded.ModelPath, token), token);
            var sheet = CharacterThemeImages.Sheet(snapshot, images);
            var picture = sheet is null ? CharacterThemePrompt.NoPicture
                : snapshot is not null ? CharacterThemePrompt.Character
                : images.Any(i => i.Thumbnail) ? CharacterThemePrompt.Thumbnail : CharacterThemePrompt.Textures;
            var (who, from) = Identity(loaded, name);
            var message = CharacterThemePrompt.Message(loaded.Entry.Swatches, loaded.Renderer, picture, who, from);
            var (answer, failure) = await ask("Making the character's theme", instructions, message, sheet, token);
            var (theme, why) = CharacterThemePrompt.Parse(answer, picture, DateTimeOffset.Now, loaded.Entry.Swatches);
            if (theme is null)
                return Report(failure is not null ? $"Couldn't ask the Thinking model ({failure}), so the rule-based palettes stay."
                    : $"The Thinking model's answer couldn't be read ({why}), so the rule-based palettes stay. Try again.");
            var entry = loaded.Entry with { Thinking = theme };
            if (dataDirectory is not null)
                try { entry = await CharacterThemes.SaveAsync(dataDirectory, entry, DateTimeOffset.Now, token); }
                catch (Exception error) when (error is ContractException or IOException or UnauthorizedAccessException)
                {
                    ErrorLog.Warn("Couldn't save the Thinking model's palettes", error);
                }
            if (Current?.ModelId == loaded.ModelId) Volatile.Write(ref current, loaded with { Entry = entry });
            ErrorLog.Info($"The Thinking model made the character's palettes from {picture} ({theme.Fixes.Count} rule fixes)" +
                (theme.Choice is { } chose ? $": accent {chose.Accent}, glow {chose.Glow ?? "Martlet's"}, tint {chose.Tint ?? "Martlet's"}, {chose.Strength}." : "."));
            return Report($"The Thinking model made these palettes at {DateTime.Now:t}" + (theme.Why is { } reason ? $": \"{reason}\"" : ".") +
                (theme.Choice is { } choice ? " " + ChoiceWords(choice) : "") +
                (theme.Fixes.Count == 0 ? "" : $" Martlet adjusted {theme.Fixes.Count} color{(theme.Fixes.Count == 1 ? "" : "s")} to keep them readable."));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return Report("Making the palettes was stopped."); }
        catch (Exception error) when (error is ContractException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return Report("Couldn't make the palettes: " + error.Message);
        }
        finally { Volatile.Write(ref busy, false); Changed?.Invoke(); }
    }

    /// <summary>Who the character is and where it is from for the Thinking model: the list's name (with the files' own name
    /// when it differs) and the owner's words, else what the files say.</summary>
    internal static (string? Name, string? Source) Identity(Loaded loaded, string? name)
    {
        var own = loaded.Identity.Name;
        var who = name is null ? own
            : own is not null && !string.Equals(own, name, StringComparison.OrdinalIgnoreCase) ? $"{name} ({own})" : name;
        return (who, loaded.Entry.About ?? loaded.Identity.Source);
    }

    /// <summary>Saves who the loaded character is and where it is from, in the owner's words (null or blank forgets it).
    /// Returns why it couldn't be saved, or null.</summary>
    internal async Task<string?> SaveAboutAsync(string? about, CancellationToken token)
    {
        if (Current is not { } loaded) return "No character is loaded.";
        about = string.IsNullOrWhiteSpace(about) ? null : about.Trim();
        if (about == loaded.Entry.About) return null;
        var entry = loaded.Entry with { About = about };
        if (dataDirectory is not null)
            try { entry = await CharacterThemes.SaveAsync(dataDirectory, entry, DateTimeOffset.Now, token); }
            catch (Exception error) when (error is ContractException or IOException or UnauthorizedAccessException) { return error.Message; }
        if (Current?.ModelId == loaded.ModelId) Volatile.Write(ref current, loaded with { Entry = entry });
        return null;
    }

    /// <summary>What the Thinking model chose, in words: "It chose #8E344A as the accent, #4CB9D9 for the glow and #1A2228
    /// for the backgrounds' tint (bold)."</summary>
    internal static string ChoiceWords(CharacterThemeChoice choice) =>
        $"It chose {choice.Accent} as the accent" + (choice.Glow is { } glow ? $", {glow} for the glow" : "") +
        (choice.Tint is { } tint ? $" and {tint} for the backgrounds' tint" : "") + $" ({choice.Strength}); Martlet built both palettes around them.";

    private string Report(string text)
    {
        Volatile.Write(ref thinking, text);
        Changed?.Invoke();
        return text;
    }
}
