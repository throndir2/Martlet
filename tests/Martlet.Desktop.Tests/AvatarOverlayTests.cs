using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Martlet.Avatar.Hosting;
using Martlet.Avatar.RendererHost;
using Microsoft.Web.WebView2.Wpf;

namespace Martlet.Desktop.Tests;

public sealed class AvatarOverlayTests
{
    [Fact]
    public Task Shared_renderer_is_transparent_topmost_and_exposes_a_drag_surface_without_startup_effects() => OnDispatcher(async () =>
    {
        using var input = new PendingInput();
        using var output = new MemoryStream();
        var window = new RendererWindow(input, output);
        try
        {
            Assert.Equal(WindowStyle.None, window.WindowStyle);
            Assert.Equal(ResizeMode.NoResize, window.ResizeMode);
            Assert.True(window.AllowsTransparency);
            Assert.True(window.Topmost);
            Assert.False(window.ShowActivated);
            Assert.Equal(Colors.Transparent, ((SolidColorBrush)window.Background).Color);
            Assert.Null(window.Owner);
            var viewport = Assert.IsAssignableFrom<Grid>(window.Content);
            Assert.Equal("MoveAvatar", AutomationProperties.GetAutomationId(viewport));
            var browser = Assert.IsType<WebView2CompositionControl>(viewport.Children[0]);
            Assert.Equal(0, browser.DefaultBackgroundColor.A);
            Assert.False(browser.IsHitTestVisible);
            Assert.False(browser.Focusable);
            Assert.Null(browser.CoreWebView2);
            Assert.Equal(Cursors.SizeAll, viewport.Cursor);
            Assert.Equal(Colors.Transparent, ((SolidColorBrush)viewport.Background).Color);
            window.Show();
            await Dispatcher.Yield();
            Assert.Empty(FindButtons(window));
            var loading = Assert.IsType<TextBlock>(viewport.Children[1]);
            Assert.Same(window.TryFindResource("SurfaceBrush"), loading.Background);
            window.ApplyOverlayTheme(dark: true);
            window.UpdateLayout();
            Assert.Same(window.TryFindResource("SurfaceBrush"), loading.Background);
            Assert.Equal(((SolidColorBrush)Appearance.Palette(AppearanceTheme.Dark, SystemParameters.HighContrast)["SurfaceBrush"]).Color,
                ((SolidColorBrush)loading.Background).Color);
            Assert.Equal(Colors.Transparent, ((SolidColorBrush)window.Background).Color);
            Assert.True(input.Waiting);
            Assert.Null(browser.CoreWebView2);
            Assert.Empty(output.ToArray());
            // The character's frame opens wholly on screen; the transparent room beside it (half the frame's width again on
            // each side) may run past the screen's edge.
            Assert.True(SystemParameters.WorkArea.Contains(window.Frame));
            Assert.Equal(window.Frame.Width * 2, window.Width, 3);
            Assert.Equal(window.Left + window.Width / 2, window.Frame.Left + window.Frame.Width / 2, 3);
            Assert.Same(viewport, viewport.InputHitTest(new Point(viewport.ActualWidth / 2, viewport.ActualHeight / 2)));
        }
        finally { window.Close(); }
    });

    [Fact]
    public void ThemeMessagePreservesExplicitDarkChoice()
    {
        var message = RendererProtocol.Message("theme", Guid.NewGuid(), new RendererTheme(true));
        Assert.True(RendererProtocol.Data<RendererTheme>(message).Dark);
    }

    [Fact]
    public Task Speech_bubble_is_drawn_in_the_overlay_palette_and_follows_it_in_Martlets_typeface() => OnDispatcher(async () =>
    {
        using var input = new PendingInput();
        using var output = new MemoryStream();
        var window = new RendererWindow(input, output);
        window.Show();
        try
        {
            await Dispatcher.Yield();
            var characterLight = new Dictionary<string, string>
            {
                ["Canvas"] = "#F2F8F8", ["Surface"] = "#F7FBFC", ["Soft"] = "#DDEFEF", ["Text"] = "#1F2E30", ["Muted"] = "#4C6466",
                ["Border"] = "#5E8689", ["Accent"] = "#00707A", ["OnAccent"] = "#FFFFFF", ["Focus"] = "#005A62", ["Success"] = "#2E6B2E",
                ["Warning"] = "#8A4F00", ["Glow"] = "#B9E2F0"
            };
            var characterDark = new Dictionary<string, string>
            {
                ["Canvas"] = "#161E24", ["Surface"] = "#212C33", ["Soft"] = "#2C3A44", ["Text"] = "#ECEFF1", ["Muted"] = "#B8BFC4",
                ["Border"] = "#7C858B", ["Accent"] = "#D194AE", ["OnAccent"] = "#211018", ["Focus"] = "#E8AAC4", ["Success"] = "#83D494",
                ["Warning"] = "#F8BF6C", ["Glow"] = "#294050"
            };
            // Martlet's own palettes and two character palettes, each applied while the previous one's bubble still shows.
            foreach (var (theme, colors) in new (AppearanceTheme, IReadOnlyDictionary<string, string>?)[]
            {
                (AppearanceTheme.Light, null), (AppearanceTheme.Dark, null),
                (AppearanceTheme.CharacterLight, characterLight), (AppearanceTheme.CharacterDark, characterDark)
            })
            {
                window.ApplyOverlayTheme(theme.IsDark(), colors);
                var shown = window.ShowSpeech(new RendererSay("Hi! While I talk, what I say shows up here."));
                var palette = Appearance.Palette(theme, SystemParameters.HighContrast, colors);
                var drawn = Assert.IsType<RendererBubbleColors>(shown.Colors);
                Assert.Equal(Hex(palette, "SurfaceBrush"), drawn.Fill);
                Assert.Equal(Hex(palette, "AccentBrush"), drawn.Outline);
                Assert.Equal(Hex(palette, "TextBrush"), drawn.Text);
                Assert.Equal(SystemParameters.HighContrast ? null : Hex(palette, "GlowBrush"), drawn.Halo);
                Assert.Equal(", in the " + theme.Name() + " colors",
                    MainWindow.BubbleTheme(shown, theme.Name(), key => palette[key] as Brush, SystemParameters.HighContrast));
            }
            // A bubble that isn't in the palette Martlet's windows use says what differs.
            if (!SystemParameters.HighContrast)
            {
                var stale = window.ShowSpeech(new RendererSay("Still me."));
                var rose = Appearance.Palette(AppearanceTheme.Dark, highContrast: false);
                Assert.StartsWith(", but not in the Rose dark colors (fill #212C33, outline #D194AE, text #ECEFF1, halo #294050",
                    MainWindow.BubbleTheme(stale, "Rose dark", key => rose[key] as Brush, highContrast: false));
            }
            Assert.Equal("", MainWindow.BubbleTheme(new RendererBubble("left", 1, 2, 3, 4, true), "Pink light", _ => null, false));

            // Martlet's own typeface, as its windows use it.
            var viewport = Assert.IsAssignableFrom<Grid>(window.Content);
            var bubble = Assert.IsType<System.Windows.Controls.Primitives.Popup>(viewport.Children[2]);
            var speech = Assert.Single(Assert.IsType<Canvas>(bubble.Child).Children.OfType<TextBlock>());
            var appFont = Assert.IsType<Style>(window.FindResource("AppWindowStyle")).Setters.OfType<Setter>()
                .Single(setter => setter.Property == Control.FontFamilyProperty).Value;
            Assert.Equal(appFont.ToString(), speech.FontFamily.Source);
            Assert.Equal("CharacterSpeech", AutomationProperties.GetAutomationId(speech));
        }
        finally { window.Close(); }

        static string Hex(ResourceDictionary palette, string key) =>
            ((SolidColorBrush)palette[key]).Color is var c ? $"#{c.R:X2}{c.G:X2}{c.B:X2}" : "";
    });

    [Fact]
    public Task Overlay_supports_keyboard_positioning_recovery_and_escape_close() => OnDispatcher(async () =>
    {
        using var input = new PendingInput();
        using var output = new MemoryStream();
        var window = new RendererWindow(input, output);
        window.Show();
        try
        {
            await Dispatcher.Yield();
            var move = (UIElement)window.Content;
            var left = window.Left;
            var top = window.Top;
            Press(window, move, Key.Left);
            Press(window, move, Key.Up);
            Assert.Equal(left - 10, window.Left);
            Assert.Equal(top - 10, window.Top);
            Press(window, move, Key.Right);
            Press(window, move, Key.Down);
            Assert.Equal(left, window.Left);
            Assert.Equal(top, window.Top);
            window.Left = -10000;
            window.Top = -10000;
            Press(window, move, Key.Home);
            Assert.Equal(left, window.Left);
            Assert.Equal(top, window.Top);
            Press(window, move, Key.Escape);
            Assert.False(window.IsVisible);
            Assert.False(input.CanRead);
            Assert.False(output.CanWrite);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task Camera_view_frames_the_character_freely_and_gives_the_overlay_its_view_back() => OnDispatcher(async () =>
    {
        using var input = new PendingInput();
        using var output = new MemoryStream();
        var window = new RendererWindow(input, output);
        // Framing needs no browser; without it this also runs where WebView2 can't create its Direct3D device (no display).
        ((Panel)window.Content).Children.RemoveAt(0);
        window.Show();
        try
        {
            await Dispatcher.Yield();
            var overlay = window.ViewState();
            var place = (window.Left, window.Top, window.Width, window.Height);
            Assert.False(overlay.Camera);
            window.UseCamera(new RendererCamera(true, "#00B140", 0.5, 0.1, -0.2));
            window.UpdateLayout();
            var framed = window.ViewState();
            Assert.True(framed.Camera);
            Assert.Equal("Martlet camera", window.Title);
            Assert.Equal(0.5, framed.Zoom);
            Assert.Equal(0.1, framed.X!.Value, 3);
            Assert.Equal(-0.2, framed.Y!.Value, 3);

            // The arrow keys move the character within the view, not the window, even unzoomed.
            var view = (FrameworkElement)window.Content;
            var left = window.Left;
            Press(window, view, Key.Right);
            Press(window, view, Key.Up);
            Assert.Equal(left, window.Left);
            var nudged = window.ViewState();
            Assert.Equal(0.1 + 10 / view.ActualWidth, nudged.X!.Value, 0.0005);
            Assert.Equal(-0.2 + 10 / view.ActualHeight, nudged.Y!.Value, 0.0005);

            // It zooms smaller than it fits, down to a quarter, and a background change keeps the framing.
            for (var i = 0; i < 20; i++) Press(window, view, Key.Subtract);
            Assert.Equal(RendererCamera.MinimumZoom, window.ViewState().Zoom);
            window.UseCamera(new RendererCamera(true, "#FF00FF"));
            Assert.Equal(RendererCamera.MinimumZoom, window.ViewState().Zoom);
            Assert.Equal(Color.FromRgb(0xFF, 0, 0xFF), ((SolidColorBrush)window.Background).Color);

            // Far-off framing stays partly in sight; Home recenters it at its fitted size.
            window.UseCamera(new RendererCamera(false));
            window.UseCamera(new RendererCamera(true, "#00B140", 1, 100, -100));
            var far = window.ViewState();
            Assert.InRange(far.X!.Value, 0.5, RendererCamera.Farthest);
            Assert.InRange(far.Y!.Value, -RendererCamera.Farthest, -0.5);
            Press(window, view, Key.Home);
            Assert.Equal((1d, 0d, 0d), (window.ViewState().Zoom, window.ViewState().X!.Value, window.ViewState().Y!.Value));

            window.UseCamera(new RendererCamera(false));
            var back = window.ViewState();
            Assert.False(back.Camera);
            Assert.Equal(overlay.Zoom, back.Zoom);
            Assert.Equal(place, (window.Left, window.Top, window.Width, window.Height));
            Assert.Equal("Martlet character overlay", window.Title);
            Assert.Throws<InvalidDataException>(() => window.UseCamera(new RendererCamera(true, "#00B140", double.NaN)));
        }
        finally { window.Close(); }
    });

    private static IEnumerable<Button> FindButtons(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is Button button) yield return button;
            foreach (var nested in FindButtons(child)) yield return nested;
        }
    }

    private static void Press(Window window, UIElement target, Key key)
    {
        var args = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), 0, key)
            { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        target.RaiseEvent(args);
        Assert.True(args.Handled);
    }

    private sealed class PendingInput : MemoryStream
    {
        internal bool Waiting { get; private set; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Waiting = true;
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
    }

    private static async Task OnDispatcher(Func<Task> action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.UnhandledException += (_, e) =>
            {
                e.Handled = true;
                completion.TrySetException(e.Exception);
                dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
            };
            dispatcher.BeginInvoke(async () =>
            {
                try { await action(); await Dispatcher.Yield(); completion.TrySetResult(); }
                catch (Exception e) { completion.TrySetException(e); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Send); }
            });
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.True(thread.Join(TimeSpan.FromSeconds(3)));
    }
}
