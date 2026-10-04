using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace Martlet.Presentation;

/// <summary>A small picture of Martlet's window in a palette: the navigation rail, the home card and a settings card drawn
/// with Martlet's real styles (Themes\Controls.xaml and Motion.xaml) and the palette's twelve brushes. Settings › Appearance
/// shows one per character palette; character_theme (Martlet.Mcp.Protocol links this file) writes them as PNGs. Call on an
/// STA thread with a dispatcher.</summary>
internal static class ThemeSample
{
    internal const int Width = 760, Height = 470;

    /// <summary>The sample rendered at <paramref name="scale"/> (1 is <see cref="Width"/> by <see cref="Height"/> pixels).
    /// <paramref name="styles"/> are the pack URIs of Controls.xaml and Motion.xaml in the calling assembly.</summary>
    internal static BitmapSource Render(IReadOnlyDictionary<string, string> colors, bool dark, string title, IReadOnlyList<Uri> styles,
        double scale = 1)
    {
        var root = Build(title);
        foreach (var style in styles) root.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = style });
        root.Resources.MergedDictionaries.Add(AppearancePalette.Create(dark, highContrast: false, colors));
        root.Measure(new Size(Width, Height));
        root.Arrange(new Rect(0, 0, Width, Height));
        root.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Round(Width * scale), (int)Math.Round(Height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(root);
        // A dispatcher that hasn't drawn yet can hand back an empty picture once; let it run its queued work and draw again.
        for (var attempt = 0; attempt < 3 && Blank(bitmap); attempt++)
        {
            var frame = new System.Windows.Threading.DispatcherFrame();
            root.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ContextIdle, () => frame.Continue = false);
            System.Windows.Threading.Dispatcher.PushFrame(frame);
            root.UpdateLayout();
            bitmap.Clear();
            bitmap.Render(root);
        }
        bitmap.Freeze();
        return bitmap;
    }

    private static bool Blank(BitmapSource bitmap)
    {
        var stride = bitmap.PixelWidth * 4;
        var row = new byte[stride];
        // The window background covers every pixel, so one row in the middle tells.
        bitmap.CopyPixels(new Int32Rect(0, bitmap.PixelHeight / 2, bitmap.PixelWidth, 1), row, stride, 0);
        for (var i = 3; i < row.Length; i += 4) if (row[i] != 0) return false;
        return true;
    }

    internal static byte[] Png(BitmapSource bitmap)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static FrameworkElement Build(string title)
    {
        var window = new Grid { Width = Width, Height = Height, UseLayoutRounding = true, SnapsToDevicePixels = true };
        TextOptions.SetTextFormattingMode(window, TextFormattingMode.Display);
        window.SetResourceReference(Panel.BackgroundProperty, "CanvasBrush");
        window.SetValue(TextElement.FontFamilyProperty, new FontFamily("Segoe UI"));
        window.SetValue(TextElement.FontSizeProperty, 13.0);
        window.SetResourceReference(TextElement.ForegroundProperty, "TextBrush");
        window.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(176) });
        window.ColumnDefinitions.Add(new ColumnDefinition());

        var rail = new Border { BorderThickness = new Thickness(0, 0, 1, 0), Padding = new Thickness(10, 16, 10, 12) };
        rail.SetResourceReference(Border.BackgroundProperty, "SoftBrush");
        rail.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        var railStack = new StackPanel();
        var brand = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 0, 0, 18) };
        var mark = new Image { Width = 30, Height = 30 };
        mark.SetResourceReference(FrameworkElement.StyleProperty, "Mascot");
        brand.Children.Add(mark);
        var name = new TextBlock { Text = "martlet", FontSize = 18, FontWeight = FontWeights.Bold, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        name.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
        brand.Children.Add(name);
        railStack.Children.Add(brand);
        foreach (var (text, glyph, selected) in new[] { ("Home", "\uE80F", true), ("Devices", "\uE772", false), ("Companion", "\uE76E", false), ("Settings", "\uE713", false) })
        {
            var item = new RadioButton { Content = text, Tag = glyph, IsChecked = selected, GroupName = "SampleNav" + title };
            item.SetResourceReference(FrameworkElement.StyleProperty, "NavItem");
            railStack.Children.Add(item);
        }
        rail.Child = railStack;
        window.Children.Add(rail);

        var page = new StackPanel { Margin = new Thickness(22, 18, 22, 12) };
        Grid.SetColumn(page, 1);
        var hero = new Border { Margin = new Thickness(0, 0, 0, 12) };
        hero.SetResourceReference(FrameworkElement.StyleProperty, "HeroCard");
        var heroGrid = new Grid();
        heroGrid.ColumnDefinitions.Add(new ColumnDefinition());
        heroGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var heroText = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        heroText.Children.Add(Styled(new TextBlock { Text = title.ToUpperInvariant() }, "Eyebrow"));
        heroText.Children.Add(Styled(new TextBlock { Text = "Good evening", FontSize = 24, Margin = new Thickness(0, 4, 0, 0) }, "HeroTitle"));
        heroText.Children.Add(Styled(new TextBlock { Text = "Your companion is ready. Start talking or show the character.", Margin = new Thickness(0, 6, 0, 12) }, "Muted"));
        var actions = new WrapPanel();
        actions.Children.Add(Styled(new Button { Content = "Start talking", Margin = new Thickness(0, 0, 10, 0) }, "PrimaryButton"));
        actions.Children.Add(new Button { Content = "Show character" });
        heroText.Children.Add(actions);
        heroGrid.Children.Add(heroText);
        var art = new Canvas { Width = 110, Height = 110, Margin = new Thickness(12, 0, 0, 0) };
        var glow = new Ellipse { Width = 96, Height = 96, Opacity = 0.7 };
        Canvas.SetLeft(glow, 7);
        Canvas.SetTop(glow, 8);
        glow.SetResourceReference(Shape.FillProperty, "GlowBrush");
        art.Children.Add(glow);
        var mascot = new Image { Width = 84, Height = 84 };
        Canvas.SetLeft(mascot, 13);
        Canvas.SetTop(mascot, 14);
        mascot.SetResourceReference(FrameworkElement.StyleProperty, "Mascot");
        art.Children.Add(mascot);
        var sparkle = new System.Windows.Shapes.Path { Data = Geometry.Parse("M 9 0 L 11.5 6.5 L 18 9 L 11.5 11.5 L 9 18 L 6.5 11.5 L 0 9 L 6.5 6.5 Z") };
        Canvas.SetLeft(sparkle, 82);
        sparkle.SetResourceReference(Shape.FillProperty, "AccentBrush");
        art.Children.Add(sparkle);
        Grid.SetColumn(art, 1);
        heroGrid.Children.Add(art);
        hero.Child = heroGrid;
        page.Children.Add(hero);

        var card = new Border { Padding = new Thickness(18), Margin = new Thickness(0) };
        card.SetResourceReference(FrameworkElement.StyleProperty, "CardStyle");
        var cardStack = new StackPanel();
        cardStack.Children.Add(Styled(new TextBlock { Text = "Appearance", FontSize = 18 }, "SectionHeading"));
        var row = new WrapPanel { Margin = new Thickness(0, 2, 0, 8) };
        var combo = new ComboBox { Width = 170, Margin = new Thickness(0, 0, 10, 0), ItemsSource = new[] { title }, SelectedIndex = 0 };
        row.Children.Add(combo);
        row.Children.Add(new TextBox { Text = "Character name", Width = 150, Margin = new Thickness(0, 0, 10, 0) });
        row.Children.Add(new CheckBox { Content = "Start with Windows", IsChecked = true, VerticalAlignment = VerticalAlignment.Center });
        cardStack.Children.Add(row);
        var status = new WrapPanel();
        status.Children.Add(Dot("SuccessBrush"));
        status.Children.Add(Colored(new TextBlock { Text = "Ready on this PC", Margin = new Thickness(0, 0, 16, 0) }, "SuccessBrush"));
        status.Children.Add(Dot("WarningBrush"));
        status.Children.Add(Colored(new TextBlock { Text = "Needs attention", Margin = new Thickness(0, 0, 16, 0) }, "WarningBrush"));
        status.Children.Add(Styled(new Button { Content = "Learn more" }, "LinkButton"));
        cardStack.Children.Add(status);
        cardStack.Children.Add(Styled(new TextBlock { Text = "Secondary text reads like this.", Margin = new Thickness(0, 6, 0, 0) }, "Muted"));
        card.Child = cardStack;
        page.Children.Add(card);
        window.Children.Add(page);
        return window;
    }

    private static T Styled<T>(T element, string style) where T : FrameworkElement
    {
        element.SetResourceReference(FrameworkElement.StyleProperty, style);
        return element;
    }

    private static TextBlock Colored(TextBlock text, string brush)
    {
        text.SetResourceReference(TextBlock.ForegroundProperty, brush);
        text.VerticalAlignment = VerticalAlignment.Center;
        return text;
    }

    private static Ellipse Dot(string brush)
    {
        var dot = new Ellipse { Width = 9, Height = 9, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
        dot.SetResourceReference(Shape.FillProperty, brush);
        return dot;
    }
}
