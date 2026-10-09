using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Martlet.Avatar.Hosting;
using Martlet.Core.Settings;
using Martlet.Presentation;

namespace Martlet.Desktop;

/// <summary>Settings › Appearance: Martlet's own palettes, light and dark palettes made by Martlet's rules from the colors
/// of the character this PC shows, and the owner's custom palette (MainWindow.CustomTheme.cs). A character palette follows the
/// character: choosing another character recolors every window and the character overlay.</summary>
public partial class MainWindow
{
    private readonly CharacterThemeService characterThemes;
    private bool loadingCharacterTheme;
    private string? renderedPreviews;

    private static readonly Uri[] SampleStyles =
    [
        new("pack://application:,,,/Martlet.Desktop;component/Themes/Controls.xaml"),
        new("pack://application:,,,/Martlet.Desktop;component/Themes/Motion.xaml")
    ];

    private static AppearanceTheme SelectedTheme => (Application.Current as App)?.SelectedTheme ?? AppearanceTheme.Light;

    private void WireCharacterThemes()
    {
        characterThemes.Changed += () => Dispatcher.InvokeAsync(() =>
        {
            if (closing) return;
            ApplyCharacterThemeAsync().Forget();
            RenderAppearance();
        });
        WireCustomTheme();
    }

    /// <summary>Keeps the loaded colors on the character this PC shows (or would show), while a character palette is chosen
    /// or Settings is open. Runs from the character timer.</summary>
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
        }

        async Task LoadAsync()
        {
            try { await characterThemes.LoadAsync(profile, force: false, lifetime.Token); }
            catch (OperationCanceledException) { }
            finally { loadingCharacterTheme = false; }
        }
    }

    private async void Theme_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || Application.Current is not App app || ThemeChoice.SelectedIndex < 0) return;
        var previous = app.SelectedTheme;
        var theme = (AppearanceTheme)ThemeChoice.SelectedIndex;
        var created = false;
        IReadOnlyDictionary<string, string>? colors;
        if (theme == AppearanceTheme.Custom) (colors, created) = OpenCustomTheme(previous);
        else
        {
            FlushCustomTheme();
            colors = characterThemes.Colors(theme);
        }
        app.ApplyTheme(theme, colors);
        var note = theme.FromCharacter() && colors is null ? " Reading your character's colors..." : "";
        var saved = created ? $"Custom saved. It starts from {previous.Name()}: choose any color below to change it." : $"{theme.Name()} saved." + note;
        if (store is null)
        {
            AppearanceStatus.Text = "Theme applied for this session. Choose a data folder to save it." + note;
            RenderAppearance();
            return;
        }
        try
        {
            Appearance.Save(store.DataDirectory, theme);
            if (theme == AppearanceTheme.Custom) { if (created) Appearance.SaveCustom(store.DataDirectory, colors!); }
            else if (colors is not null) Appearance.SaveColors(store.DataDirectory, theme, colors);
            AppearanceStatus.Text = saved;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppearanceStatus.Text = "Theme applied for this session, but it could not be saved. Check access to Martlet's data folder.";
        }
        RenderAppearance();
        FollowCharacterTheme();
        await UpdateCharacterOverlayThemeAsync();
    }

    /// <summary>Applies the loaded character's colors when a character palette is chosen and they changed (another
    /// character).</summary>
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
        catch (Exception ex) when (ex is OperationCanceledException || RendererFailures.Is(ex, lifetime.Token))
        {
            if (!closing)
                AppearanceStatus.Text += " The character could not update its colors. Restart it to apply the theme.";
        }
    }

    /// <summary>The character's colors and its two palettes as small pictures of Martlet's window. Only drawn while Settings
    /// shows.</summary>
    private void RenderAppearance()
    {
        if (closing || SettingsPage.Visibility != Visibility.Visible) return;
        var loaded = characterThemes.Current;
        AppearanceCharacterStatus.Text = characterThemes.Problem is { } problem ? "Martlet couldn't read this character's colors: " + problem
            : loaded is null ? "Reading your character's colors..."
            : $"{loaded.Entry.Swatches.Count} main colors from its textures. The character palettes take their accent from " +
              $"{CharacterThemeRules.Pick(loaded.Entry.Swatches).Accent.Hex} and follow the character you show.";
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
        RenderCustomTheme();
    }

    private void RenderPreviews(CharacterThemeService.Loaded? loaded)
    {
        if (renderingPreviews) return;
        var selected = SelectedTheme;
        var key = loaded is null ? "" : string.Join("|", loaded.ModelId, selected, loaded.RulesLight.Describe(), loaded.RulesDark.Describe(),
            SystemParameters.HighContrast);
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
            (AppearanceTheme.CharacterLight, "rules-light", loaded.RulesLight), (AppearanceTheme.CharacterDark, "rules-dark", loaded.RulesDark)
        })
        {
            var panel = new StackPanel { Margin = new Thickness(0, 0, 12, 12), Width = 300 };
            var caption = new TextBlock { Text = theme.Name() + (theme == selected ? "  ·  in use" : ""), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) };
            AutomationProperties.SetAutomationId(caption, "AppearancePreview-" + id);
            AutomationProperties.SetName(caption, $"{theme.Name()}: {palette.Describe()}");
            panel.Children.Add(caption);
            var frame = new Border { BorderThickness = new Thickness(theme == selected ? 2 : 1), CornerRadius = new CornerRadius(10), ClipToBounds = true, Padding = new Thickness(1) };
            frame.SetResourceReference(Border.BorderBrushProperty, theme == selected ? "AccentBrush" : "BorderBrush");
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
