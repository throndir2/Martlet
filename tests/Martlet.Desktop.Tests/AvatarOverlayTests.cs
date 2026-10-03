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
            Assert.Equal(((SolidColorBrush)Appearance.Palette(PinkTheme.Dark, SystemParameters.HighContrast)["SurfaceBrush"]).Color,
                ((SolidColorBrush)loading.Background).Color);
            Assert.Equal(Colors.Transparent, ((SolidColorBrush)window.Background).Color);
            Assert.True(input.Waiting);
            Assert.Null(browser.CoreWebView2);
            Assert.Empty(output.ToArray());
            Assert.True(SystemParameters.WorkArea.Contains(new Rect(window.Left, window.Top, window.Width, window.Height)));
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
