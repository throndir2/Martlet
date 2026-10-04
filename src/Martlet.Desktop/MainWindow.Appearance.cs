using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Martlet.Avatar.Hosting;
using Martlet.Core.Settings;
using Martlet.Presentation;

namespace Martlet.Desktop;

/// <summary>Settings › Appearance: Martlet's own palettes, and light and dark palettes made from the colors of the character
/// this PC shows, by Martlet's rules or by the Thinking model. A character palette follows the character: choosing another
/// character recolors every window and the character overlay.</summary>
public partial class MainWindow
{
    private readonly CharacterThemeService characterThemes;
    private bool loadingCharacterTheme, makingCharacterTheme;
    private string? renderedPreviews;

    private static readonly Uri[] SampleStyles =
    [
        new("pack://application:,,,/Martlet.Desktop;component/Themes/Controls.xaml"),
        new("pack://application:,,,/Martlet.Desktop;component/Themes/Motion.xaml")
    ];

    private static AppearanceTheme SelectedTheme => (Application.Current as App)?.SelectedTheme ?? AppearanceTheme.Light;

    private void WireCharacterThemes() => characterThemes.Changed += () => Dispatcher.InvokeAsync(() =>
    {
        if (closing) return;
        ApplyCharacterThemeAsync().Forget();
        RenderAppearance();
    });

    /// <summary>Keeps the loaded colors on the character this PC shows (or would show), while a character palette is chosen
    /// or Settings is open, and makes the Thinking palettes once for a new character while one of them is chosen. Runs from
    /// the character timer.</summary>
    private void FollowCharacterTheme()
    {
        if (closing || loadingCharacterTheme) return;
        if (!SelectedTheme.FromCharacter() && SettingsPage.Visibility != Visibility.Visible) return;
        // Wait for the saved character, so Martlet doesn't briefly take on the built-in character's colors.
        if (store is not null && homeSettingsState is null && !avatar.IsShowing) return;
        var profile = avatar.IsShowing ? avatar.InspectedProfile : homeAvatar;
        var modelPath = profile?.ModelPath ?? BundledLive2D.Prefix + BundledLive2D.DefaultCharacter;
        if (!string.Equals(characterThemes.Path, modelPath, StringComparison.OrdinalIgnoreCase))
        {
            loadingCharacterTheme = true;
            LoadAsync().Forget();
            return;
        }
        if (SelectedTheme.ByThinking() && CanAskThinking && !makingCharacterTheme && characterThemes.ClaimAutomatic())
            MakeCharacterThemeAsync().Forget();

        async Task LoadAsync()
        {
            try { await characterThemes.LoadAsync(profile, force: false, lifetime.Token); }
            catch (OperationCanceledException) { }
            finally { loadingCharacterTheme = false; }
        }
    }

    private bool CanAskThinking => conversation is not null && homeSettings?.Setup?.Routes.Any(r => r.Role == SetupRole.Llm) == true;

    private async void Theme_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || Application.Current is not App app || ThemeChoice.SelectedIndex < 0) return;
        var theme = (AppearanceTheme)ThemeChoice.SelectedIndex;
        var colors = characterThemes.Colors(theme);
        app.ApplyTheme(theme, colors);
        var note = !theme.FromCharacter() ? ""
            : colors is null ? " Reading your character's colors..."
            : theme.ByThinking() && characterThemes.Current?.Entry.Thinking is null ? " Until the Thinking model makes it, the rule-based palette is used."
            : "";
        if (store is null)
        {
            AppearanceStatus.Text = "Theme applied for this session. Choose a data folder to save it." + note;
            RenderAppearance();
            return;
        }
        try
        {
            Appearance.Save(store.DataDirectory, theme);
            if (colors is not null) Appearance.SaveColors(store.DataDirectory, theme, colors);
            AppearanceStatus.Text = $"{theme.Name()} saved." + note;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppearanceStatus.Text = "Theme applied for this session, but it could not be saved. Check access to Martlet's data folder.";
        }
        RenderAppearance();
        FollowCharacterTheme();
        await UpdateCharacterOverlayThemeAsync();
    }

    /// <summary>Applies the loaded character's colors when a character palette is chosen and they changed (another character,
    /// or the Thinking model's new palettes).</summary>
    private async Task ApplyCharacterThemeAsync()
    {
        if (Application.Current is not App app || !app.SelectedTheme.FromCharacter() || characterThemes.Colors(app.SelectedTheme) is not { } colors)
            return;
        if (app.ThemeColors is { } applied && AppearancePalette.Roles.All(role => applied.TryGetValue(role, out var value) && value == colors[role]))
            return;
        app.ApplyTheme(app.SelectedTheme, colors);
        if (store is not null)
            try { Appearance.SaveColors(store.DataDirectory, app.SelectedTheme, colors); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { ErrorLog.Warn("Couldn't keep the character's colors for the next start", error); }
        ErrorLog.Info($"Applied the {app.SelectedTheme.Name()} palette ({colors["Accent"]} accent on {colors["Canvas"]}).");
        await UpdateCharacterOverlayThemeAsync();
    }

    private async Task UpdateCharacterOverlayThemeAsync()
    {
        try { await avatar.UpdateThemeAsync(lifetime.Token); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or OperationCanceledException or TimeoutException or InvalidDataException)
        {
            if (!closing)
                AppearanceStatus.Text += " The character could not update its colors. Restart it to apply the theme.";
        }
    }

    private void AppearanceThinking_Click(object sender, RoutedEventArgs e) => MakeCharacterThemeAsync().Forget();

    private async Task MakeCharacterThemeAsync()
    {
        if (conversation is null || makingCharacterTheme) return;
        makingCharacterTheme = true;
        RenderAppearance();
        try
        {
            // The character as it shows now is the best picture of it; a hidden one is sent as its textures instead.
            byte[]? snapshot = null;
            if (avatar.IsShowing && string.Equals(avatar.InspectedProfile?.ModelPath, characterThemes.Path, StringComparison.OrdinalIgnoreCase))
                try { snapshot = await avatar.SnapshotAsync(lifetime.Token); }
                catch (Exception error) when (error is IOException or InvalidOperationException or InvalidDataException or TimeoutException or
                    OperationCanceledException or ObjectDisposedException)
                {
                    ErrorLog.Info("The character couldn't be pictured for its theme; its textures are sent instead.");
                }
            await characterThemes.MakeWithThinkingAsync(conversation.AskThinkingAsync, snapshot, CharacterThemeName(), homeSettings?.Prompts, lifetime.Token);
        }
        catch (OperationCanceledException) { }
        finally
        {
            makingCharacterTheme = false;
            if (!closing) RenderAppearance();
        }
    }

    /// <summary>The character's colors, its four palettes as small pictures of Martlet's window, and the Thinking model's
    /// status. Only drawn while Settings shows.</summary>
    private void RenderAppearance()
    {
        if (closing || SettingsPage.Visibility != Visibility.Visible) return;
        var loaded = characterThemes.Current;
        AppearanceCharacterStatus.Text = characterThemes.Problem is { } problem ? "Martlet couldn't read this character's colors: " + problem
            : loaded is null ? "Reading your character's colors..."
            : $"{loaded.Entry.Swatches.Count} main colors from its textures. The character palettes take their accent from " +
              $"{CharacterThemeRules.Pick(loaded.Entry.Swatches).Accent.Hex} and follow the character you show.";
        if (loaded is not null && loaded.ModelId != renderedAboutModel && !AppearanceCharacterAbout.IsKeyboardFocusWithin)
        {
            renderedAboutModel = loaded.ModelId;
            settingAbout = true;
            AppearanceCharacterAbout.Text = loaded.Entry.About ?? "";
            settingAbout = false;
        }
        AppearanceCharacterAbout.IsEnabled = loaded is not null;
        if (loaded is not null && !(aboutSave?.Pending ?? false)) AppearanceIdentity.Text = IdentityText(loaded);
        AppearanceSwatches.Children.Clear();
        if (loaded is not null)
            for (var i = 0; i < loaded.Entry.Swatches.Count; i++)
            {
                var swatch = loaded.Entry.Swatches[i];
                var tile = new TextBlock
                {
                    Width = 30, Height = 30, Background = Brush(swatch.Hex),
                    ToolTip = $"{swatch.Hex} · {swatch.Share:0.#%} of the model · {swatch.Name}"
                };
                AutomationProperties.SetAutomationId(tile, $"AppearanceColor-{i}");
                AutomationProperties.SetName(tile, $"{swatch.Hex} {swatch.Share:0.#%} {swatch.Name}");
                var frame = new Border { Child = tile, CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 6, 6), ClipToBounds = true };
                frame.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
                AppearanceSwatches.Children.Add(frame);
            }
        RenderPreviews(loaded);
        var thinking = loaded?.Entry.Thinking;
        AppearanceThinkingStatus.Text = characterThemes.Thinking ?? (thinking is not null
            ? $"Made by the Thinking model on {thinking.At.ToLocalTime():g} from {PictureWords(thinking.Picture)}" +
              (thinking.Why is { } why ? $": \"{why}\"" : ".") +
              (thinking.Choice is { } choice ? " " + CharacterThemeService.ChoiceWords(choice) : "") +
              (thinking.Fixes.Count == 0 ? "" : $" Martlet adjusted {thinking.Fixes.Count} color{(thinking.Fixes.Count == 1 ? "" : "s")} to keep it readable.")
            : !CanAskThinking ? "Set up Thinking in Companion to have it suggest palettes for your character."
            : "The Thinking model hasn't made palettes for this character yet. Make with Thinking sends its main colors and a picture " +
              "of it (as it shows, or its textures) to your Thinking model.");
        AppearanceThinkingButton.Content = characterThemes.Busy || makingCharacterTheme ? "Making..." : thinking is null ? "Make with _Thinking" : "Make again with _Thinking";
        AppearanceThinkingButton.IsEnabled = CanAskThinking && loaded is not null && !characterThemes.Busy && !makingCharacterTheme;
    }

    private static string PictureWords(string picture) => picture switch
    {
        CharacterThemePrompt.Character => "the character as it showed and its textures",
        CharacterThemePrompt.Thumbnail => "the model's thumbnail and textures",
        CharacterThemePrompt.Textures => "its textures",
        _ => "its colors"
    };

    private string? renderedAboutModel;
    private bool settingAbout;
    private AutoSave? aboutSave;

    /// <summary>The name of the character whose colors are loaded, as the owner's character list has it (the built-in one's
    /// own name, a shared character's name, or the model file's).</summary>
    private string? CharacterThemeName()
    {
        if (characterThemes.Path is not { } path) return null;
        if (BundledLive2D.IsBuiltIn(path)) return path[BundledLive2D.Prefix.Length..];
        try
        {
            if (store is not null && SharedCharacterModels.ForPath(store.DataDirectory, SharedCharacterModels.View(store.DataDirectory), path) is { } shared)
                return shared.Name;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        return SharedCharacterModels.NameFor(path);
    }

    /// <summary>Who the character is and where it's from saves on its own, for this character, after a short pause.</summary>
    private void AppearanceCharacterAbout_Changed(object sender, TextChangedEventArgs e)
    {
        if (settingAbout || !IsLoaded) return;
        aboutSave ??= new AutoSave(async () =>
        {
            var why = await characterThemes.SaveAboutAsync(AppearanceCharacterAbout.Text, lifetime.Token);
            AppearanceIdentity.Text = why is not null ? "Not saved: " + why
                : characterThemes.Current is { } loaded ? IdentityText(loaded) + " Saved for this character." : "Saved.";
            return true;
        });
        AppearanceIdentity.Text = "Saving...";
        aboutSave.Changed();
    }

    private string IdentityText(CharacterThemeService.Loaded loaded)
    {
        var (who, from) = CharacterThemeService.Identity(loaded, CharacterThemeName());
        return $"Thinking is told: {who ?? "an unnamed character"}" + (from is null
            ? ". Say who it is and where it's from above, and Thinking can use the colors it is known for."
            : $" ({from}).");
    }

    private void RenderPreviews(CharacterThemeService.Loaded? loaded)
    {
        if (renderingPreviews) return;
        var selected = SelectedTheme;
        var key = loaded is null ? "" : string.Join("|", loaded.ModelId, selected, loaded.RulesLight.Describe(), loaded.RulesDark.Describe(),
            loaded.ThinkingLight?.Describe(), loaded.ThinkingDark?.Describe(), SystemParameters.HighContrast);
        if (key == renderedPreviews) return;
        renderingPreviews = true;
        try { RenderPreviews(loaded, selected, key); }
        finally { renderingPreviews = false; }
    }

    private bool renderingPreviews;

    private void RenderPreviews(CharacterThemeService.Loaded? loaded, AppearanceTheme selected, string key)
    {
        renderedPreviews = key;
        AppearancePreviews.Children.Clear();
        if (loaded is null) return;
        foreach (var (theme, id, palette) in new[]
        {
            (AppearanceTheme.CharacterLight, "rules-light", loaded.RulesLight), (AppearanceTheme.CharacterDark, "rules-dark", loaded.RulesDark),
            (AppearanceTheme.ThinkingLight, "thinking-light", loaded.ThinkingLight), (AppearanceTheme.ThinkingDark, "thinking-dark", loaded.ThinkingDark)
        })
        {
            var panel = new StackPanel { Margin = new Thickness(0, 0, 12, 12), Width = 300 };
            var caption = new TextBlock { Text = theme.Name() + (theme == selected ? "  ·  in use" : ""), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) };
            AutomationProperties.SetAutomationId(caption, "AppearancePreview-" + id);
            AutomationProperties.SetName(caption, palette is null ? $"{theme.Name()}: not made yet" : $"{theme.Name()}: {palette.Describe()}");
            panel.Children.Add(caption);
            var frame = new Border { BorderThickness = new Thickness(theme == selected ? 2 : 1), CornerRadius = new CornerRadius(10), ClipToBounds = true, Padding = new Thickness(1) };
            frame.SetResourceReference(Border.BorderBrushProperty, theme == selected ? "AccentBrush" : "BorderBrush");
            if (palette is null)
            {
                frame.Height = 300.0 * ThemeSample.Height / ThemeSample.Width;
                frame.Child = new TextBlock
                {
                    Text = "Not made yet", VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center
                };
                ((TextBlock)frame.Child).SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            }
            else
            {
                try
                {
                    frame.Child = new Image
                    {
                        Source = ThemeSample.Render(palette.Colors, palette.Dark, theme.Name(), SampleStyles, 300.0 / ThemeSample.Width * 1.5),
                        Width = 296, Stretch = Stretch.Uniform
                    };
                }
                catch (Exception error) when (error is InvalidOperationException or IOException or ArgumentException)
                {
                    ErrorLog.Warn("Couldn't draw a palette preview", error);
                }
            }
            panel.Children.Add(frame);
            AppearancePreviews.Children.Add(panel);
        }
    }

    private static SolidColorBrush Brush(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}
