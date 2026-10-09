using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Martlet.Avatar.Hosting;
using Martlet.Presentation;

namespace Martlet.Desktop;

/// <summary>Settings › Appearance › Custom: the owner's own palette. It starts as a copy of the palette in use (or of the one
/// chosen under Start from), and each of its twelve colors (<see cref="CustomThemeParts"/>) changes with a color code, hue,
/// saturation and lightness sliders or one of the character's colors. Every window and the character overlay follow within a
/// moment; the colors are saved in appearance-custom.json after a short pause and shared with the owner's other computers.
/// A line under the editor says what may be hard to read, and Make it easy to read fixes it by Martlet's palette rules.</summary>
public partial class MainWindow
{
    private enum CustomSource { None, Hex, Sliders }

    /// <summary>The custom palette as it is being edited (null until Custom is chosen or shown).</summary>
    private Dictionary<string, string>? customColors;
    private string customRole = ThemeRoles.Accent;
    /// <summary>Set while the editor shows a color, so the change events of its own controls don't change the color again.</summary>
    private bool showingCustomColor;
    private readonly Dictionary<string, (ListBoxItem Item, Border Swatch, TextBlock Code)> customRoleRows = new(StringComparer.Ordinal);
    private string? customPicksShown;
    private readonly DispatcherTimer customApplyTimer = new() { Interval = TimeSpan.FromMilliseconds(40) };
    private readonly DispatcherTimer customSaveTimer = new() { Interval = TimeSpan.FromMilliseconds(700) };

    private void WireCustomTheme()
    {
        customApplyTimer.Tick += (_, _) =>
        {
            customApplyTimer.Stop();
            ApplyCustomTheme();
        };
        customSaveTimer.Tick += (_, _) =>
        {
            customSaveTimer.Stop();
            SaveCustomThemeAsync().Forget();
        };
        CustomThemeHue.Background = Scale(Enumerable.Range(0, 7).Select(step => new Hsl(step * 60, 100, 50).Hex).ToArray());
    }

    /// <summary>The custom palette being edited, else the one applied or saved; null when there is none yet.</summary>
    private Dictionary<string, string>? CustomColors()
    {
        if (customColors is not null) return customColors;
        var known = Application.Current is App { SelectedTheme: AppearanceTheme.Custom, ThemeColors: var applied } ? Appearance.Normalize(applied) : null;
        known ??= store is null ? null : Appearance.LoadCustom(store.DataDirectory);
        return known is null ? null : customColors = new(known, StringComparer.Ordinal);
    }

    /// <summary>The colors to apply when Custom is chosen: the custom palette, else (the first time) a copy of
    /// <paramref name="previous"/>, the palette in use until now, which Start from then names.</summary>
    private (IReadOnlyDictionary<string, string> Colors, bool Created) OpenCustomTheme(AppearanceTheme previous)
    {
        if (CustomColors() is { } colors) return (Snapshot(colors), false);
        customColors = new(PaletteColors(previous), StringComparer.Ordinal);
        if (previous != AppearanceTheme.Custom) CustomThemeBase.SelectedIndex = (int)previous;
        return (Snapshot(customColors), true);
    }

    /// <summary>The twelve colors <paramref name="theme"/> draws with now: a character palette's are Martlet's own palette of
    /// the same lightness until the character's colors are read.</summary>
    private IReadOnlyDictionary<string, string> PaletteColors(AppearanceTheme theme)
    {
        if (theme == AppearanceTheme.Custom && CustomColors() is { } custom) return custom;
        if (theme.FromCharacter() && Appearance.Normalize(characterThemes.Colors(theme) ??
                (Application.Current is App { SelectedTheme: var selected, ThemeColors: var applied } && selected == theme ? applied : null)) is { } colors)
            return colors;
        return AppearancePalette.Pink(theme.IsDark());
    }

    private static Dictionary<string, string> Snapshot(IReadOnlyDictionary<string, string> colors) => new(colors, StringComparer.Ordinal);

    private static bool Same(IReadOnlyDictionary<string, string> first, IReadOnlyDictionary<string, string> second) =>
        AppearancePalette.Roles.All(role => first.TryGetValue(role, out var a) && second.TryGetValue(role, out var b) &&
            string.Equals(a, b, StringComparison.OrdinalIgnoreCase));

    private void ApplyCustomTheme()
    {
        if (closing || customColors is null || Application.Current is not App { SelectedTheme: AppearanceTheme.Custom } app) return;
        app.ApplyTheme(AppearanceTheme.Custom, Snapshot(customColors));
    }

    /// <summary>Every window takes the edited palette within a moment (at once with <paramref name="now"/>); it is saved after
    /// a short pause.</summary>
    private void CustomThemeChanged(string? status = null, bool now = false, CustomSource source = CustomSource.None)
    {
        if (now)
        {
            customApplyTimer.Stop();
            ApplyCustomTheme();
        }
        else if (!customApplyTimer.IsEnabled) customApplyTimer.Start();
        customSaveTimer.Stop();
        customSaveTimer.Start();
        if (status is not null) AppearanceStatus.Text = status;
        RenderCustomTheme(source);
    }

    private async Task SaveCustomThemeAsync()
    {
        if (customColors is null) return;
        if (store is not null)
            try { Appearance.SaveCustom(store.DataDirectory, customColors); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                ErrorLog.Warn("Couldn't save the custom palette", error);
                if (!closing) AppearanceStatus.Text = "Your colors apply now, but they could not be saved. Check access to Martlet's data folder.";
            }
        if (!closing && SelectedTheme == AppearanceTheme.Custom) await UpdateCharacterOverlayThemeAsync();
    }

    /// <summary>Saves a change still waiting for its pause (another palette was chosen, or Martlet closes).</summary>
    private void FlushCustomTheme()
    {
        customApplyTimer.Stop();
        if (!customSaveTimer.IsEnabled) return;
        customSaveTimer.Stop();
        if (store is null || customColors is null) return;
        try { Appearance.SaveCustom(store.DataDirectory, customColors); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { ErrorLog.Warn("Couldn't save the custom palette", error); }
    }

    /// <summary>Takes the custom palette another computer shared: the editor and, while Custom is chosen, every window follow.</summary>
    private void FollowCustomTheme(IReadOnlyDictionary<string, string> colors)
    {
        customSaveTimer.Stop();
        customColors = Snapshot(colors);
        if (SelectedTheme == AppearanceTheme.Custom)
        {
            ApplyCustomTheme();
            UpdateCharacterOverlayThemeAsync().Forget();
        }
        RenderAppearance();
    }

    /// <summary>The editor, shown while Custom is chosen: the twelve parts with their colors, the selected part's color code
    /// and sliders, the character's colors to take one from, and whether everything is easy to read.</summary>
    private void RenderCustomTheme(CustomSource source = CustomSource.None)
    {
        var show = SelectedTheme == AppearanceTheme.Custom;
        CustomThemePanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show || closing || CustomColors() is not { } colors) return;
        if (customRoleRows.Count == 0) BuildCustomRoles();
        foreach (var (role, name, _) in CustomThemeParts.All)
        {
            var (item, swatch, code) = customRoleRows[role];
            swatch.Background = Brush(colors[role]);
            code.Text = colors[role];
            AutomationProperties.SetName(item, $"{name}: {colors[role]}");
        }
        if (CustomThemeRoles.SelectedItem is not ListBoxItem { Tag: string selected } || selected != customRole)
            CustomThemeRoles.SelectedItem = customRoleRows[customRole].Item;
        ShowCustomColor(colors, source);
        RenderCustomPicks();
        RenderCustomCheck(colors);
    }

    private void BuildCustomRoles()
    {
        foreach (var (role, name, help) in CustomThemeParts.All)
        {
            var swatch = new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 10, 0) };
            swatch.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
            var code = new TextBlock { FontFamily = new FontFamily("Consolas"), Opacity = 0.85, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            var label = new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            var row = new DockPanel();
            DockPanel.SetDock(swatch, Dock.Left);
            DockPanel.SetDock(code, Dock.Right);
            row.Children.Add(swatch);
            row.Children.Add(code);
            row.Children.Add(label);
            var item = new ListBoxItem { Content = row, Tag = role, ToolTip = help };
            AutomationProperties.SetAutomationId(item, "CustomThemeRole-" + role);
            CustomThemeRoles.Items.Add(item);
            customRoleRows[role] = (item, swatch, code);
        }
    }

    private void ShowCustomColor(IReadOnlyDictionary<string, string> colors, CustomSource source)
    {
        var hex = colors[customRole];
        var part = CustomThemeParts.All.First(p => p.Role == customRole);
        CustomThemeRoleName.Text = part.Name;
        CustomThemeRoleHelp.Text = part.Help;
        CustomThemeSwatch.Background = Brush(hex);
        showingCustomColor = true;
        try
        {
            if (source != CustomSource.Hex)
            {
                CustomThemeHex.Text = hex;
                CustomThemeHexHint.Visibility = Visibility.Collapsed;
            }
            if (source != CustomSource.Sliders)
            {
                var color = Hsl.FromHex(hex);
                // A gray looks the same at every hue, so the hue slider stays where it was.
                if (color.S >= 0.5 && color.L is > 0.5 and < 99.5) CustomThemeHue.Value = Math.Round(color.H) % 360;
                CustomThemeSaturation.Value = Math.Round(color.S);
                CustomThemeLightness.Value = Math.Round(color.L);
            }
            double h = CustomThemeHue.Value, s = CustomThemeSaturation.Value, l = CustomThemeLightness.Value;
            CustomThemeSaturation.Background = Scale(new Hsl(h, 0, l).Hex, new Hsl(h, 100, l).Hex);
            CustomThemeLightness.Background = Scale("#000000", new Hsl(h, s, 50).Hex, "#FFFFFF");
        }
        finally { showingCustomColor = false; }
    }

    /// <summary>The character's main colors, to give the selected part one of them.</summary>
    private void RenderCustomPicks()
    {
        var loaded = characterThemes.Current;
        if (loaded?.ModelId == customPicksShown && (loaded is not null || CustomThemePicks.Children.Count == 0)) return;
        customPicksShown = loaded?.ModelId;
        CustomThemePicks.Children.Clear();
        CustomThemePicksLabel.Visibility = loaded is null ? Visibility.Collapsed : Visibility.Visible;
        if (loaded is null) return;
        for (var i = 0; i < loaded.Entry.Swatches.Count; i++)
        {
            var swatch = loaded.Entry.Swatches[i];
            var tile = new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(5), Background = Brush(swatch.Hex) };
            var button = new Button { Content = tile, Padding = new Thickness(4), MinHeight = 0, Margin = new Thickness(0, 0, 6, 6), ToolTip = $"{swatch.Hex} · {swatch.Name}" };
            AutomationProperties.SetAutomationId(button, $"CustomThemePick-{i}");
            AutomationProperties.SetName(button, $"Use {swatch.Hex} {swatch.Name}");
            var hex = swatch.Hex;
            button.Click += (_, _) => SetCustomColor(hex, CustomSource.None);
            CustomThemePicks.Children.Add(button);
        }
    }

    private void RenderCustomCheck(IReadOnlyDictionary<string, string> colors)
    {
        var palette = ThemePalette.From(AppearanceTheme.Custom.IsDark(colors), colors)!;
        var kind = palette.Dark ? "dark" : "light";
        var problems = CharacterThemeRules.WrongBackgrounds(palette)
            .Select(role => $"{CustomThemeParts.Name(role)}: too {(palette.Dark ? "light" : "dark")} for a {kind} palette")
            .Concat(CharacterThemeRules.Check(palette).Where(check => !check.Ok).Select(check =>
                (check.Role == ThemeRoles.OnAccent ? "Text on accent" : $"{CustomThemeParts.Name(check.Role)} on {CustomThemeParts.Name(check.On).ToLowerInvariant()}") +
                $": {check.Ratio:0.##}:1 (needs {check.Minimum:0.#}:1)"))
            .ToArray();
        CustomThemeCheck.Text = problems.Length == 0
            ? $"Easy to read: every color keeps Martlet's contrast rules for a {kind} palette."
            : $"{problems.Length} {(problems.Length == 1 ? "thing" : "things")} may be hard to read in this {kind} palette: " +
              string.Join("; ", problems.Take(4)) + (problems.Length > 4 ? $"; and {problems.Length - 4} more." : ".");
        CustomThemeFixButton.Visibility = problems.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void SetCustomColor(string hex, CustomSource source)
    {
        if (SelectedTheme != AppearanceTheme.Custom || CustomColors() is not { } colors) return;
        if (string.Equals(colors[customRole], hex, StringComparison.OrdinalIgnoreCase))
        {
            RenderCustomTheme(source);
            return;
        }
        colors[customRole] = hex;
        CustomThemeChanged(source: source);
    }

    private void CustomThemeRole_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (CustomThemeRoles.SelectedItem is not ListBoxItem { Tag: string role } || CustomColors() is not { } colors) return;
        customRole = role;
        ShowCustomColor(colors, CustomSource.None);
    }

    private void CustomThemeHex_Changed(object sender, TextChangedEventArgs e)
    {
        if (showingCustomColor || !IsLoaded) return;
        var digits = CustomThemeHex.Text.Trim().TrimStart('#');
        if (digits.Length == 6 && ThemeColor.TryParse(digits, out var hex))
        {
            CustomThemeHexHint.Visibility = Visibility.Collapsed;
            SetCustomColor(hex, CustomSource.Hex);
        }
        else CustomThemeHexHint.Visibility = digits.Length < 6 && digits.All(char.IsAsciiHexDigit) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void CustomThemeHex_LostFocus(object sender, KeyboardFocusChangedEventArgs e) => CommitCustomHex();

    private void CustomThemeHex_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        CommitCustomHex();
        e.Handled = true;
    }

    /// <summary>Takes the typed color code (#RGB too) or, when it isn't a color, shows the selected part's code again and says
    /// how to type one.</summary>
    private void CommitCustomHex()
    {
        if (showingCustomColor || SelectedTheme != AppearanceTheme.Custom || CustomColors() is not { } colors) return;
        var valid = ThemeColor.TryParse(CustomThemeHex.Text, out var hex);
        if (valid) SetCustomColor(hex, CustomSource.None);
        else ShowCustomColor(colors, CustomSource.None);
        CustomThemeHexHint.Visibility = valid ? Visibility.Collapsed : Visibility.Visible;
    }

    private void CustomThemeSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (showingCustomColor || !IsLoaded) return;
        SetCustomColor(new Hsl(CustomThemeHue.Value, CustomThemeSaturation.Value, CustomThemeLightness.Value).Hex, CustomSource.Sliders);
    }

    private void CustomThemeStartFrom_Click(object sender, RoutedEventArgs e)
    {
        if (CustomThemeBase.SelectedIndex < 0 || CustomColors() is not { } colors) return;
        var basis = (AppearanceTheme)CustomThemeBase.SelectedIndex;
        if (basis.FromCharacter() && characterThemes.Colors(basis) is null)
        {
            AppearanceStatus.Text = characterThemes.Problem is { } problem ? "Martlet couldn't read your character's colors: " + problem
                : "Martlet is still reading your character's colors. Try again in a moment.";
            return;
        }
        var next = PaletteColors(basis);
        if (Same(colors, next))
        {
            AppearanceStatus.Text = $"Your custom palette already has the colors of {basis.Name()}.";
            return;
        }
        // Colors the owner chose are asked about first; a palette that is still a copy of another one is simply replaced.
        var edited = !Appearance.Choices.Where(choice => choice.Theme != AppearanceTheme.Custom).Any(choice => Same(colors, PaletteColors(choice.Theme)));
        if (edited && !ConfirmationDialog.Confirm(this,
                $"Replace all twelve colors of your custom palette with the colors of {basis.Name()}? The colors you chose are lost.",
                "Start from " + basis.Name(), yes: "_Use its colors", no: "_Keep my colors", questionId: "CustomThemeStartFromQuestion"))
            return;
        foreach (var role in AppearancePalette.Roles) colors[role] = next[role];
        CustomThemeChanged($"Your custom palette now starts from {basis.Name()}: choose any color below to change it.", now: true);
    }

    private void CustomThemeFix_Click(object sender, RoutedEventArgs e)
    {
        if (CustomColors() is not { } colors) return;
        var (repaired, fixes) = CharacterThemeRules.Repair(ThemePalette.From(AppearanceTheme.Custom.IsDark(colors), colors)!);
        var changed = AppearancePalette.Roles.Count(role => !string.Equals(repaired[role], colors[role], StringComparison.OrdinalIgnoreCase));
        foreach (var role in AppearancePalette.Roles) colors[role] = repaired[role];
        ErrorLog.Info($"Made the custom palette easy to read: {string.Join("; ", fixes)}.");
        CustomThemeChanged($"Martlet changed {changed} {(changed == 1 ? "color" : "colors")} so that every part is easy to read.", now: true);
    }

    private static LinearGradientBrush Scale(params string[] hexes)
    {
        var brush = new LinearGradientBrush { StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5) };
        for (var i = 0; i < hexes.Length; i++)
            brush.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString(hexes[i]), (double)i / (hexes.Length - 1)));
        brush.Freeze();
        return brush;
    }
}
