using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Martlet.Desktop;

namespace Martlet.Desktop.Tests;

public sealed class AppearanceTests
{
    [Fact]
    public void MissingPreferenceIsLightAndDoesNotCreateFiles()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Martlet.Appearance." + Guid.NewGuid().ToString("N"));
        Assert.Equal(AppearanceTheme.Light, Appearance.Load(directory));
        Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public void PreferenceRoundTripsWithoutChangingProfileAndReportsInvalidData()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Martlet.Appearance." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var settings = Path.Combine(directory, "settings.json");
            File.WriteAllText(settings, "profile sentinel");
            foreach (var theme in Enum.GetValues<AppearanceTheme>())
            {
                Appearance.Save(directory, theme);
                Assert.Equal(theme, Appearance.Load(directory));
                Assert.Equal("profile sentinel", File.ReadAllText(settings));
                Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
            }
            File.WriteAllText(Path.Combine(directory, "appearance.txt"), new string('x', 10000));
            Assert.Throws<InvalidDataException>(() => Appearance.Load(directory));
            var startup = Appearance.LoadForStartup(directory);
            Assert.Equal(AppearanceTheme.Light, startup.Theme);
            Assert.Contains("Couldn't load your theme", startup.Notice);
            Assert.Equal(new string('x', 10000), File.ReadAllText(Path.Combine(directory, "appearance.txt")));
            File.WriteAllText(Path.Combine(directory, "appearance.txt"), "");
            Assert.Equal(AppearanceTheme.Light, Appearance.LoadForStartup(directory).Theme);
            Assert.NotNull(Appearance.LoadForStartup(directory).Notice);
            Appearance.Save(directory, AppearanceTheme.Dark);
            Assert.Equal(AppearanceTheme.Dark, Appearance.Load(directory));
            using var locked = new FileStream(Path.Combine(directory, "appearance.txt"), FileMode.Open, FileAccess.Read, FileShare.None);
            var error = Record.Exception(() => Appearance.Save(directory, AppearanceTheme.Light));
            Assert.True(error is IOException or UnauthorizedAccessException);
            Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PaletteMeetsTextAndControlContrast(bool dark)
    {
        var palette = Appearance.Palette(dark ? AppearanceTheme.Dark : AppearanceTheme.Light, highContrast: false);
        foreach (var background in new[] { "CanvasBrush", "SurfaceBrush", "SoftBrush" })
        {
            foreach (var foreground in new[] { "TextBrush", "MutedBrush", "AccentBrush" })
                Assert.True(Contrast(palette, foreground, background) >= 4.5, $"{foreground} / {background}");
            Assert.True(Contrast(palette, "FocusBrush", background) >= 3);
            Assert.True(Contrast(palette, "BorderBrush", background) >= 3, $"BorderBrush / {background}");
        }
        Assert.True(Contrast(palette, "OnAccentBrush", "AccentBrush") >= 4.5);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CharacterPalettesKeepTheRulesAndRepairBrokenOnes(bool dark)
    {
        // A slate model with a plum accent and a cream highlight, as Martlet reads them from textures.
        Martlet.Avatar.Hosting.CharacterSwatch[] swatches =
        [
            new("#1A2228", 0.3, "neutral"), new("#5E656C", 0.3, "neutral"), new("#FBE9DF", 0.1, "neutral"),
            new("#BC978B", 0.1, "skin"), new("#77435B", 0.05, "muted"), new("#4CB9D9", 0.01, "vivid")
        ];
        var rules = Martlet.Avatar.Hosting.CharacterThemeRules.Build(swatches, dark);
        Assert.Empty(Martlet.Avatar.Hosting.CharacterThemeRules.Problems(rules));
        var palette = Appearance.Palette(dark ? AppearanceTheme.CharacterDark : AppearanceTheme.CharacterLight, false, rules.Colors);
        Assert.Equal(rules["Canvas"], ((SolidColorBrush)palette["CanvasBrush"]).Color.ToString().Remove(1, 2));
        Assert.IsType<DrawingImage>(palette["MascotImage"]);
        // A palette that breaks every rule: wrong-side backgrounds and unreadable text come back readable in their own hues.
        var broken = Martlet.Avatar.Hosting.ThemePalette.From(dark, Martlet.Avatar.Hosting.ThemeRoles.All.ToDictionary(role => role, _ => "#808080"))!;
        var (repaired, fixes) = Martlet.Avatar.Hosting.CharacterThemeRules.Repair(broken);
        Assert.NotEmpty(fixes);
        Assert.Empty(Martlet.Avatar.Hosting.CharacterThemeRules.Problems(repaired));
        // Martlet's own palettes ignore character colors.
        Assert.Equal(((SolidColorBrush)Appearance.Palette(AppearanceTheme.Dark, false)["CanvasBrush"]).Color,
            ((SolidColorBrush)Appearance.Palette(AppearanceTheme.Dark, false, rules.Colors)["CanvasBrush"]).Color);
    }

    [Fact]
    public void RetiredThinkingPalettesReadAsTheCharacterPalettes()
    {
        Assert.Equal(AppearanceTheme.CharacterLight, Appearance.Parse("ThinkingLight"));
        Assert.Equal(AppearanceTheme.CharacterDark, Appearance.Parse("ThinkingDark"));
        Assert.Null(Appearance.Parse("4"));
        var directory = Path.Combine(Path.GetTempPath(), "Martlet.Appearance." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "appearance.txt"), "ThinkingDark");
            Assert.Equal(AppearanceTheme.CharacterDark, Appearance.Load(directory));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void CustomPaletteRoundTripsCanonicallyAndStartsAsPinkLightWithoutOne()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Martlet.Appearance." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            Assert.Null(Appearance.LoadCustom(directory));
            Assert.Equal(Own(dark: false), Appearance.LoadColors(directory, AppearanceTheme.Custom));
            var colors = Own(dark: true);
            colors["Accent"] = "3cb";
            colors["Canvas"] = "#0a0b0c";
            Appearance.SaveCustom(directory, colors);
            var loaded = Assert.IsAssignableFrom<IReadOnlyDictionary<string, string>>(Appearance.LoadCustom(directory));
            Assert.Equal("#33CCBB", loaded["Accent"]);
            Assert.Equal("#0A0B0C", loaded["Canvas"]);
            Assert.Equal(Martlet.Avatar.Hosting.ThemeRoles.All, loaded.Keys);
            Assert.Equal(loaded, Appearance.LoadColors(directory, AppearanceTheme.Custom));
            // The same colors are always the same text, so another computer reads back exactly what it shared.
            var shared = File.ReadAllText(Appearance.CustomPath(directory));
            Assert.Equal(shared, Appearance.ShareCustom(Appearance.ParseCustom(shared)!));
            Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
            colors.Remove("Glow");
            Assert.Throws<ArgumentException>(() => Appearance.SaveCustom(directory, colors));
            Assert.Null(Appearance.ParseCustom("""{"Colors":{"Canvas":"#FFFFFF"}}"""));
            Assert.Null(Appearance.ParseCustom("not json"));
            Assert.Null(Appearance.ParseCustom(null));
            // A custom palette that can't be read starts as Pink light's colors, and Martlet says so.
            Appearance.Save(directory, AppearanceTheme.Custom);
            Assert.Null(Appearance.LoadForStartup(directory).Notice);
            File.WriteAllText(Appearance.CustomPath(directory), "{");
            var startup = Appearance.LoadForStartup(directory);
            Assert.Equal(AppearanceTheme.Custom, startup.Theme);
            Assert.Contains("custom palette", startup.Notice);
            Assert.Equal(Own(dark: false), Appearance.LoadColors(directory, AppearanceTheme.Custom));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void CustomPaletteDrawsWithItsOwnColorsAndIsDarkWhenItsBackgroundIs()
    {
        var light = Own(dark: false);
        light["Accent"] = "#1F6F8B";
        Assert.False(AppearanceTheme.Custom.FromCharacter());
        Assert.True(AppearanceTheme.Custom.HasColors());
        Assert.False(AppearanceTheme.Custom.IsDark(light));
        Assert.Equal("Custom", AppearanceTheme.Custom.Name());
        Assert.Equal(AppearanceTheme.Custom, Appearance.Parse("Custom"));
        var palette = Appearance.Palette(AppearanceTheme.Custom, false, light);
        Assert.Equal((Color)ColorConverter.ConvertFromString("#1F6F8B"), ((SolidColorBrush)palette["AccentBrush"]).Color);
        Assert.IsType<DrawingImage>(palette["MascotImage"]);
        var dark = new Dictionary<string, string>(light) { ["Canvas"] = "#202020" };
        Assert.True(AppearanceTheme.Custom.IsDark(dark));
        Assert.True(AppearanceTheme.Custom.IsDark(new Dictionary<string, string>(light) { ["Canvas"] = "#5A5A5A" }));
        Assert.False(AppearanceTheme.Custom.IsDark(new Dictionary<string, string>(light) { ["Canvas"] = "#A0A0A0" }));
        Assert.False(AppearanceTheme.Custom.IsDark(null));
        // Without colors a custom palette looks like Pink light; Martlet's own palettes still ignore colors.
        Assert.Equal(((SolidColorBrush)Appearance.Palette(AppearanceTheme.Light, false)["CanvasBrush"]).Color,
            ((SolidColorBrush)Appearance.Palette(AppearanceTheme.Custom, false)["CanvasBrush"]).Color);
        Assert.False(AppearanceTheme.Light.HasColors());
        Assert.True(AppearanceTheme.Dark.IsDark(light));
        // Windows' high contrast wins over custom colors too.
        Assert.Equal(SystemColors.WindowColor, ((SolidColorBrush)Appearance.Palette(AppearanceTheme.Custom, true, dark)["CanvasBrush"]).Color);
    }

    [Fact]
    public void CustomPaletteReadabilityRulesFindAndFixUnreadableColors()
    {
        var colors = Own(dark: false);
        var good = Martlet.Avatar.Hosting.ThemePalette.From(AppearanceTheme.Custom.IsDark(colors), colors)!;
        Assert.Empty(Martlet.Avatar.Hosting.CharacterThemeRules.Problems(good));
        colors["Text"] = "#C8C8C8";
        colors["Soft"] = "#303030";
        var bad = Martlet.Avatar.Hosting.ThemePalette.From(AppearanceTheme.Custom.IsDark(colors), colors)!;
        Assert.Equal(new[] { "Soft" }, Martlet.Avatar.Hosting.CharacterThemeRules.WrongBackgrounds(bad));
        Assert.Contains(Martlet.Avatar.Hosting.CharacterThemeRules.Problems(bad), problem => problem.StartsWith("Text on Canvas", StringComparison.Ordinal));
        var (repaired, fixes) = Martlet.Avatar.Hosting.CharacterThemeRules.Repair(bad);
        Assert.NotEmpty(fixes);
        Assert.Empty(Martlet.Avatar.Hosting.CharacterThemeRules.Problems(repaired));
        Assert.False(AppearanceTheme.Custom.IsDark(repaired.Colors));
        Assert.Equal(Martlet.Avatar.Hosting.ThemeRoles.All, CustomThemeParts.All.Select(part => part.Role));
        Assert.Equal("Buttons and side bar", CustomThemeParts.Name("Soft"));
    }

    [Theory]
    [InlineData("#FF0000", 0, 100, 50)]
    [InlineData("#00FF00", 120, 100, 50)]
    [InlineData("#0000FF", 240, 100, 50)]
    [InlineData("#FFFFFF", 0, 0, 100)]
    [InlineData("#000000", 0, 0, 0)]
    [InlineData("#808080", 0, 0, 50.2)]
    [InlineData("#A52D64", 332.5, 57.1, 41.2)]
    public void HslMatchesTheColorAndRoundTrips(string hex, double h, double s, double l)
    {
        var color = Hsl.FromHex(hex);
        Assert.Equal(h, color.H, 0.1);
        Assert.Equal(s, color.S, 0.1);
        Assert.Equal(l, color.L, 0.1);
        Assert.Equal(hex, color.Hex);
        Assert.Equal(hex, new Hsl(color.H + 360, color.S, color.L).Hex);
    }

    [Fact]
    public void HslSlidersReachEveryEndOfTheScale()
    {
        Assert.Equal("#FF0000", new Hsl(360, 100, 50).Hex);
        Assert.Equal("#FFFFFF", new Hsl(200, 40, 100).Hex);
        Assert.Equal("#000000", new Hsl(200, 40, 0).Hex);
        Assert.Equal("#808080", new Hsl(200, 0, 50).Hex);
        Assert.Equal("#FF00FF", new Hsl(300, 120, 50).Hex);
        Assert.Throws<FormatException>(() => Hsl.FromHex("pink"));
    }

    [Fact]
    public void HighContrastUsesWindowsColorsInsteadOfPink()
    {
        var light = Appearance.Palette(AppearanceTheme.Light, highContrast: true);
        var dark = Appearance.Palette(AppearanceTheme.Dark, highContrast: true);
        foreach (string key in light.Keys)
            if (light[key] is SolidColorBrush brush) Assert.Equal(brush.Color, ((SolidColorBrush)dark[key]).Color);
        Assert.Equal(SystemColors.WindowColor, ((SolidColorBrush)dark["CanvasBrush"]).Color);
        Assert.Equal(SystemColors.WindowTextColor, ((SolidColorBrush)dark["TextBrush"]).Color);
        Assert.Equal(SystemColors.HighlightColor, ((SolidColorBrush)dark["AccentBrush"]).Color);
        Assert.Equal(SystemColors.HighlightTextColor, ((SolidColorBrush)dark["OnAccentBrush"]).Color);
    }

    [Fact]
    public Task AppIconContainsAllWindowsSizesWithTransparentCorners() => OnDispatcher(() =>
    {
        var source = Application.GetResourceStream(new Uri("/Martlet.Desktop;component/Assets/Martlet.ico", UriKind.Relative));
        Assert.NotNull(source);
        using var stream = source.Stream;
        var decoder = new IconBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        Assert.Equal(new[] { 16, 20, 24, 32, 40, 48, 64, 128, 256 }, decoder.Frames.Select(frame => frame.PixelWidth));
        foreach (var frame in decoder.Frames)
        {
            Assert.Equal(frame.PixelWidth, frame.PixelHeight);
            var bitmap = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
            var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
            bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
            Assert.Equal(0, pixels[3]);
            var center = (bitmap.PixelHeight / 2 * bitmap.PixelWidth + bitmap.PixelWidth / 2) * 4;
            Assert.Equal(255, pixels[center + 3]);
        }
    });

    [Fact]
    public Task RealTemplatesKeepInputsTabsAndOpenDropdownThemedAfterSwitch() => OnDispatcher(() =>
    {
        var panel = new StackPanel();
        var button = new Button { Content = "_Continue" };
        var input = new TextBox { Text = "Editable text" };
        var password = new PasswordBox { Password = "fixture" };
        var choice = new ComboBox { ItemsSource = new[] { "Light", "Dark" }, SelectedIndex = 0 };
        var check = new CheckBox { Content = "I permit this explicit action", IsChecked = false };
        var radio = new RadioButton { Content = "Fixture only", IsChecked = true };
        var facts = new ListBox { ItemsSource = new[] { "Explicit local fact" }, SelectedIndex = 0 };
        var tabText = new TextBlock { Text = "Configuration only" };
        var tabs = new TabControl { Items = { new TabItem { Header = "_Setup", Content = tabText } } };
        foreach (var control in new Control[] { button, input, password, choice, check, radio, tabs, facts })
            panel.Children.Add(control);
        var window = new ThemedWindow { Content = panel, Width = 500, Height = 550, ShowActivated = false, ShowInTaskbar = false };
        var light = window.Resources.MergedDictionaries.Last();
        window.SetResourceReference(FrameworkElement.StyleProperty, "AppWindowStyle");
        try
        {
            window.Show();
            window.UpdateLayout();
            Assert.Same(light["CanvasBrush"], window.Background);
            var icon = Assert.IsAssignableFrom<BitmapSource>(window.Icon);
            Assert.True(icon.PixelWidth >= 16);
            Assert.NotNull(input.Template.FindName("PART_ContentHost", input));
            Assert.NotNull(password.Template.FindName("PART_ContentHost", password));
            Assert.Same(light["SurfaceBrush"], input.Background);
            choice.IsDropDownOpen = true;
            window.UpdateLayout();
            var popup = Assert.IsType<Popup>(choice.Template.FindName("PART_Popup", choice));
            Assert.True(popup.IsOpen);
            var dark = Appearance.Palette(AppearanceTheme.Dark, false);
            window.Resources.MergedDictionaries.Remove(light);
            window.Resources.MergedDictionaries.Add(dark);
            window.UpdateLayout();
            Assert.Same(dark["CanvasBrush"], window.Background);
            Assert.Same(icon, window.Icon);
            Assert.Same(dark["TextBrush"], button.Foreground);
            Assert.Same(dark["SurfaceBrush"], input.Background);
            Assert.Same(dark["SurfaceBrush"], Assert.IsType<Border>(popup.Child).Background);
            Assert.Same(dark["TextBrush"], check.Foreground);
            Assert.Same(dark["TextBrush"], tabs.Foreground);
            Assert.Same(dark["TextBrush"], tabText.Foreground);
            Assert.Same(dark["SurfaceBrush"], facts.Background);
            var selectedFact = Assert.IsType<ListBoxItem>(facts.ItemContainerGenerator.ContainerFromIndex(0));
            Assert.Same(dark["AccentBrush"], selectedFact.Background);
            Assert.Same(dark["OnAccentBrush"], selectedFact.Foreground);
            choice.SelectedIndex = 1;
            Assert.Equal("Dark", choice.SelectedItem);
            Assert.False(check.IsChecked);
            Assert.True(radio.IsChecked);
            Assert.Equal("fixture", password.Password);
            Assert.Equal("Editable text", input.Text);
            Assert.NotNull(button.FocusVisualStyle);
            Assert.NotNull(check.FocusVisualStyle);
            input.IsReadOnly = true;
            Assert.Same(dark["SoftBrush"], input.Background);
        }
        finally { choice.IsDropDownOpen = false; window.Close(); }
    });

    [Fact]
    public void EveryDesktopWindowInheritsSharedTheme()
    {
        var windows = typeof(MainWindow).Assembly.GetTypes()
            .Where(type => type.IsClass && !type.IsAbstract && type != typeof(ThemedWindow) &&
                typeof(Window).IsAssignableFrom(type));
        Assert.All(windows, type => Assert.True(type.IsSubclassOf(typeof(ThemedWindow)), type.FullName));
    }

    [Theory]
    [InlineData("yes", true)]
    [InlineData("no", false)]
    [InlineData("close", false)]
    public Task ConfirmationIsThemedAndDefaultsToNo(string action, bool approved) => OnDispatcher(() =>
    {
        var owner = new ThemedWindow { Width = 200, Height = 100, ShowActivated = false, ShowInTaskbar = false };
        owner.SetResourceReference(FrameworkElement.StyleProperty, "AppWindowStyle");
        owner.Show();
        try
        {
            var dialog = new ConfirmationDialog("Explicit local action", "Review this exact request.") { Owner = owner };
            dialog.Loaded += (_, _) =>
            {
                Assert.Same(owner, dialog.Owner);
                Assert.Same(dialog.TryFindResource("CanvasBrush"), dialog.Background);
                var no = Assert.IsType<Button>(dialog.FindName("NoButton"));
                var yes = Assert.IsType<Button>(dialog.FindName("YesButton"));
                Assert.True(no.IsDefault);
                Assert.True(no.IsCancel);
                Assert.False(yes.IsDefault);
                Assert.Same(dialog.TryFindResource("AccentBrush"), yes.Background);
                dialog.Dispatcher.BeginInvoke(() =>
                {
                    if (action == "close") dialog.Close();
                    else (action == "yes" ? yes : no).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                });
            };
            Assert.Equal(approved, dialog.ShowDialog() == true);
        }
        finally { owner.Close(); }
    });

    /// <summary>Pink light's or Rose dark's colors by role, as their brushes draw them.</summary>
    private static Dictionary<string, string> Own(bool dark)
    {
        var palette = Appearance.Palette(dark ? AppearanceTheme.Dark : AppearanceTheme.Light, highContrast: false);
        return Martlet.Avatar.Hosting.ThemeRoles.All.ToDictionary(role => role, role =>
            ((SolidColorBrush)palette[role + "Brush"]).Color is var c ? $"#{c.R:X2}{c.G:X2}{c.B:X2}" : "");
    }

    private static double Contrast(ResourceDictionary palette, string foreground, string background)
    {
        static double Luminance(Color color)
        {
            static double Linear(byte channel)
            {
                var value = channel / 255d;
                return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
            }
            return 0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);
        }
        var a = Luminance(((SolidColorBrush)palette[foreground]).Color);
        var b = Luminance(((SolidColorBrush)palette[background]).Color);
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }

    private static async Task OnDispatcher(Action action)
    {
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { action(); finished.SetResult(); }
            catch (Exception error) { finished.SetException(error); }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.True(thread.Join(TimeSpan.FromSeconds(5)));
    }
}
