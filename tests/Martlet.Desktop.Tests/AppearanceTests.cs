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
        Assert.Equal(PinkTheme.Light, Appearance.Load(directory));
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
            foreach (var theme in new[] { PinkTheme.Dark, PinkTheme.Light })
            {
                Appearance.Save(directory, theme);
                Assert.Equal(theme, Appearance.Load(directory));
                Assert.Equal("profile sentinel", File.ReadAllText(settings));
                Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
            }
            File.WriteAllText(Path.Combine(directory, "appearance.txt"), new string('x', 10000));
            Assert.Throws<InvalidDataException>(() => Appearance.Load(directory));
            var startup = Appearance.LoadForStartup(directory);
            Assert.Equal(PinkTheme.Light, startup.Theme);
            Assert.Contains("Could not read appearance.txt", startup.Notice);
            Assert.Equal(new string('x', 10000), File.ReadAllText(Path.Combine(directory, "appearance.txt")));
            File.WriteAllText(Path.Combine(directory, "appearance.txt"), "");
            Assert.Equal(PinkTheme.Light, Appearance.LoadForStartup(directory).Theme);
            Assert.NotNull(Appearance.LoadForStartup(directory).Notice);
            Appearance.Save(directory, PinkTheme.Dark);
            Assert.Equal(PinkTheme.Dark, Appearance.Load(directory));
            using var locked = new FileStream(Path.Combine(directory, "appearance.txt"), FileMode.Open, FileAccess.Read, FileShare.None);
            var error = Record.Exception(() => Appearance.Save(directory, PinkTheme.Light));
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
        var palette = Appearance.Palette(dark ? PinkTheme.Dark : PinkTheme.Light, highContrast: false);
        foreach (var background in new[] { "CanvasBrush", "SurfaceBrush", "SoftBrush" })
        {
            foreach (var foreground in new[] { "TextBrush", "MutedBrush", "AccentBrush" })
                Assert.True(Contrast(palette, foreground, background) >= 4.5, $"{foreground} / {background}");
            Assert.True(Contrast(palette, "FocusBrush", background) >= 3);
            Assert.True(Contrast(palette, "BorderBrush", background) >= 3, $"BorderBrush / {background}");
        }
        Assert.True(Contrast(palette, "OnAccentBrush", "AccentBrush") >= 4.5);
    }

    [Fact]
    public void HighContrastUsesWindowsColorsInsteadOfPink()
    {
        var light = Appearance.Palette(PinkTheme.Light, highContrast: true);
        var dark = Appearance.Palette(PinkTheme.Dark, highContrast: true);
        foreach (string key in light.Keys)
            Assert.Equal(((SolidColorBrush)light[key]).Color, ((SolidColorBrush)dark[key]).Color);
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
        var window = new Window { Content = panel, Width = 500, Height = 550, ShowActivated = false, ShowInTaskbar = false };
        window.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/Martlet.Desktop;component/Themes/Controls.xaml")
        });
        var light = Appearance.Palette(PinkTheme.Light, false);
        window.Resources.MergedDictionaries.Add(light);
        window.SetResourceReference(FrameworkElement.StyleProperty, "AppWindowStyle");
        try
        {
            window.Show();
            window.UpdateLayout();
            var icon = Assert.IsAssignableFrom<BitmapSource>(window.Icon);
            Assert.True(icon.PixelWidth >= 16);
            Assert.NotNull(input.Template.FindName("PART_ContentHost", input));
            Assert.NotNull(password.Template.FindName("PART_ContentHost", password));
            Assert.Same(light["SurfaceBrush"], input.Background);
            choice.IsDropDownOpen = true;
            window.UpdateLayout();
            var popup = Assert.IsType<Popup>(choice.Template.FindName("PART_Popup", choice));
            Assert.True(popup.IsOpen);
            var dark = Appearance.Palette(PinkTheme.Dark, false);
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
