using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Martlet.Avatar.Hosting;
using Martlet.Avatars;
using Martlet.Core.Settings;
using Martlet.Desktop;

namespace Martlet.Desktop.Tests;

/// <summary>Companion › Touch's zone map zooms (<see cref="ZoomFrame"/>): its zoom steps, a picture that grows while the frame keeps
/// its height, the middle (or the point under the pointer) kept at each zoom, the zoom and the part shown kept when the page draws
/// again, the wheel that scrolls the picture before the page, and where Add zone puts a zone on the zoomed-in map; and the Touch page
/// itself, in Martlet's main window, with its zoom buttons, boxes and Add zone.</summary>
public sealed class ZoomFrameTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [Fact]
    public void TheZoomGoesInStepsFromTheWholePictureToEightTimes()
    {
        Assert.Equal(new double[] { 1, 1.5, 2, 3, 4, 6, 8 }, ZoomFrame.Steps);
        Assert.Equal(1.5, ZoomFrame.Next(1, 1));
        Assert.Equal(3, ZoomFrame.Next(2.5, 1));
        Assert.Equal(2, ZoomFrame.Next(2.5, -1));
        Assert.Equal(8, ZoomFrame.Next(8, 1));
        Assert.Equal(1, ZoomFrame.Next(1, -1));
    }

    [Fact]
    public Task ZoomingInGrowsThePictureAndKeepsTheFramesHeightAndItsMiddle() => OnDispatcher(() =>
    {
        var picture = new Canvas();
        var frame = new ZoomFrame(picture, 400, 600);
        var window = Host(frame, 1000);
        try
        {
            window.Show();
            Settle();
            // 1x: the whole picture, as large as it fits, with nothing to scroll.
            Assert.Equal((1d, 400d, 600d), (frame.Zoom, picture.Width, picture.Height));
            Assert.Equal((400d, 600d), (frame.ActualWidth, frame.ActualHeight));
            Assert.Equal((0d, 0d), (frame.ScrollableWidth, frame.ScrollableHeight));
            Assert.Equal(new Point(0.5, 0.5), frame.Center);
            Assert.False(frame.CanZoomOut);
            Assert.False(frame.ZoomOut());

            var zoomed = 0;
            frame.Zoomed += () => zoomed++;
            Assert.True(frame.ZoomIn());
            Settle();
            // 1.5x: the picture grows; the frame keeps its height and grows as wide as the picture, so it scrolls only down.
            Assert.Equal((1.5, 600d, 900d, 1), (frame.Zoom, picture.Width, picture.Height, zoomed));
            Assert.Equal(600, frame.ActualHeight);
            Assert.Equal((600d, 0d, 300d), (frame.ViewportWidth, frame.ScrollableWidth, frame.ScrollableHeight));
            Assert.Equal(150, frame.VerticalOffset, 3);
            Near(new Point(0.5, 0.5), frame.Center);

            // Ctrl+wheel zooms where the pointer is: the picture's point under it stays under it. 4x is wider than the page, so the
            // frame stops at the page's width and scrolls both ways.
            var at = new Point(100, 100);
            var under = new Point((frame.HorizontalOffset + at.X) / picture.Width, (frame.VerticalOffset + at.Y) / picture.Height);
            Assert.True(frame.ZoomTo(4, at));
            Settle();
            Assert.Equal((1600d, 2400d), (picture.Width, picture.Height));
            Assert.Equal(1000, frame.ActualWidth);
            Assert.True(frame.ScrollableWidth > 0);
            Near(under, new Point((frame.HorizontalOffset + at.X) / picture.Width, (frame.VerticalOffset + at.Y) / picture.Height));

            // Reset zoom shows the whole picture again.
            Assert.True(frame.ZoomTo(1));
            Settle();
            Assert.Equal((400d, 600d, 0d, 0d), (frame.ActualWidth, frame.ActualHeight, frame.ScrollableWidth, frame.ScrollableHeight));
            Assert.Equal(new Point(0.5, 0.5), frame.Center);
            Assert.False(frame.ZoomTo(0.25));
            Assert.True(frame.ZoomTo(20));
            Assert.Equal(8, frame.Zoom);
            Assert.False(frame.CanZoomIn);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task APageDrawnAgainShowsTheZoomAndThePartItShowedEvenFromHidden() => OnDispatcher(() =>
    {
        var picture = new Canvas();
        var frame = new ZoomFrame(picture, 400, 600, zoom: 2, center: new Point(0.4, 0.6));
        var views = 0;
        frame.ViewChanged += () => views++;
        // Drawn while the page is hidden: it has no size yet, so it shows the part once it has one.
        var page = new Border { Child = frame, Visibility = Visibility.Collapsed };
        var window = Host(page, 500);
        try
        {
            window.Show();
            Settle();
            Assert.Equal(0, views);
            page.Visibility = Visibility.Visible;
            Settle();
            Assert.Equal((2d, 800d, 1200d), (frame.Zoom, picture.Width, picture.Height));
            Assert.Equal(500, frame.ActualWidth);
            Near(new Point(0.4, 0.6), frame.Center);
            Assert.True(views > 0);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task TheWheelScrollsTheZoomedPictureThenThePage() => OnDispatcher(() =>
    {
        var picture = new Canvas();
        var frame = new ZoomFrame(picture, 400, 600);
        var window = Host(frame, 1000);
        try
        {
            window.Show();
            Settle();
            // 1x: nothing to scroll, so the page scrolls.
            Assert.False(Wheel(frame, -120));

            frame.ZoomTo(2);
            Settle();
            var middle = frame.VerticalOffset;
            Assert.True(Wheel(frame, -120));
            Settle();
            Assert.True(frame.VerticalOffset > middle);

            // At the bottom of the picture a turn down goes on to the page, and a turn up still scrolls the picture.
            frame.ScrollToBottom();
            Settle();
            Assert.False(Wheel(frame, -120));
            Assert.True(Wheel(frame, 120));
        }
        finally { window.Close(); }
    });

    [Fact]
    public void AnAddedZoneStartsInTheMiddleOfThePartTheMapShows()
    {
        Assert.Equal(new TouchZoneBox(0.4, 0.4, 0.2, 0.2), MainWindow.AddedZoneBox(1, new Point(0.5, 0.5)));
        // Zoomed in 4x on the face: as large on the screen as at 1x, in the middle of what shows.
        var face = MainWindow.AddedZoneBox(4, new Point(0.3, 0.2));
        Near(0.275, face.X);
        Near(0.175, face.Y);
        Near(0.05, face.Width);
        Near(0.05, face.Height);
        Assert.True(face.Valid);
        // Near an edge it stays on the picture.
        var corner = MainWindow.AddedZoneBox(2, new Point(0.99, 0.01));
        Near(0.9, corner.X);
        Near(0, corner.Y);
        Assert.True(MainWindow.AddedZoneBox(8, new Point(0.5, 0.5)).Valid);
    }

    [Fact]
    public Task TheTouchPageZoomsItsZoneMapKeepsTheZoomWhenDrawnAgainAndAddsZonesWhereYouLook() => OnDispatcherAsync(async () =>
    {
        var root = Path.Combine(Path.GetTempPath(), "martlet-zone-map-" + Guid.NewGuid().ToString("N"));
        var data = Path.Combine(root, "data");
        Directory.CreateDirectory(data);
        _ = System.IO.Packaging.PackUriHelper.UriSchemePack;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        void Step(string what) => output.WriteLine($"{clock.ElapsedMilliseconds,6} ms  {what}");
        // The bundled character's zones and picture are saved already, so the page shows them at once: no first guess, no renderer.
        var model = (await CharacterActionInventory.ReadAsync(AvatarRenderer.Live2D, BundledLive2D.Prefix + BundledLive2D.DefaultCharacter,
            CancellationToken.None)).ModelId;
        Step("model read");
        await CharacterTouchZones.SaveAsync(data, new CharacterTouchZoneSettings
        {
            ModelId = model, DetectedBy = CharacterTouchZoneSettings.ByOwner, Zones = [new CharacterTouchZone { Id = "hair", Box = new(0.25, 0.1, 0.5, 0.2) }]
        }, DateTimeOffset.Now, CancellationToken.None);
        await CharacterTouchZones.SaveSnapshotAsync(data, model, Png(300, 400), CancellationToken.None);
        Step("zones saved");
        // The main window stays unshown, so nothing it starts once shown runs; the zone map isn't laid out, and keeps its middle.
        var main = new MainWindow(new SettingsStore(data), null) { ShowActivated = false, ShowInTaskbar = false };
        Step("main window made");
        try
        {
            typeof(MainWindow).GetField("companionTab", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(main, CompanionTab.Touch);
            Assert.IsType<RadioButton>(main.FindName("NavCompanion")).IsChecked = true;
            Step("page opened");
            var map = await Eventually(() => Find<ZoomFrame>(main, "TouchZonesMap"));
            Step("map shown");
            var picture = Find<Canvas>(main, "TouchZonesPicture")!;
            var (width, height) = (map.FitWidth, map.FitHeight);
            Assert.Equal((width, height), (picture.Width, picture.Height));
            var zoom = Find<TextBlock>(main, "TouchZonesZoom")!;
            Assert.Equal("Zoom 1x", zoom.Text);
            Button zoomIn = Find<Button>(main, "TouchZonesZoomIn")!, zoomOut = Find<Button>(main, "TouchZonesZoomOut")!,
                reset = Find<Button>(main, "TouchZonesZoomReset")!;
            Assert.Equal((true, false, false), (zoomIn.IsEnabled, zoomOut.IsEnabled, reset.IsEnabled));
            Assert.StartsWith("Zoom in to move and resize the boxes precisely.", AutomationProperties.GetHelpText(map), StringComparison.Ordinal);
            var hair = Find<Border>(main, "TouchZoneRect-0")!;
            Placed(hair, 0.25 * width, 0.1 * height, 0.5 * width, 0.2 * height);

            // Zoom in twice: 2x. The picture and the boxes on it grow; the boxes' lines and labels don't.
            Click(zoomIn);
            Click(zoomIn);
            Assert.Equal("Zoom 2x", zoom.Text);
            Assert.Equal((2 * width, 2 * height), (picture.Width, picture.Height));
            Assert.Equal(2 * width, picture.Children.OfType<Image>().Single().Width);
            Placed(hair, 0.5 * width, 0.2 * height, width, 0.4 * height);
            Assert.Equal(new Thickness(1.5), hair.BorderThickness);
            Assert.Equal((true, true, true), (zoomIn.IsEnabled, zoomOut.IsEnabled, reset.IsEnabled));

            // Add zone puts the zone in the middle of the part shown, as large on the screen as at 1x; the page draws again and keeps
            // the zoom.
            Click(await Eventually(() => Find<Button>(main, "TouchZonesAdd")));
            Step("zone added");
            var added = await Eventually(() => CharacterTouchZones.Load(data, model)?.Zones.FirstOrDefault(z => z.Added));
            Step("added zone saved");
            Near(0.45, added.Box.X);
            Near(0.45, added.Box.Y);
            Near(0.1, added.Box.Width);
            Near(0.1, added.Box.Height);
            var redrawn = await Eventually(() => Find<ZoomFrame>(main, "TouchZonesMap") is { } next && !ReferenceEquals(next, map) ? next : null);
            Step("page drawn again");
            Assert.Equal(2, redrawn.Zoom);
            Assert.Equal("Zoom 2x", Find<TextBlock>(main, "TouchZonesZoom")!.Text);
            Assert.Equal(2 * width, Find<Canvas>(main, "TouchZonesPicture")!.Width);
            Placed(await Eventually(() => Find<Border>(main, "TouchZoneRect-1")), 0.9 * width, 0.9 * height, 0.2 * width, 0.2 * height);
            Step("added box shown");

            // Reset zoom shows the whole picture, and so does opening the page again.
            Click(Find<Button>(main, "TouchZonesZoomIn")!);
            Assert.Equal("Zoom 3x", Find<TextBlock>(main, "TouchZonesZoom")!.Text);
            Assert.IsType<RadioButton>(main.FindName("NavHome")).IsChecked = true;
            Assert.IsType<RadioButton>(main.FindName("NavCompanion")).IsChecked = true;
            Step("page opened again");
            var opened = await Eventually(() => Find<ZoomFrame>(main, "TouchZonesMap") is { } next && !ReferenceEquals(next, redrawn) ? next : null);
            Step("map shown again");
            Assert.Equal(1, opened.Zoom);
            Assert.Equal("Zoom 1x", Find<TextBlock>(main, "TouchZonesZoom")!.Text);
            Click(Find<Button>(main, "TouchZonesZoomIn")!);
            Click(Find<Button>(main, "TouchZonesZoomReset")!);
            Assert.Equal((1d, width), (opened.Zoom, Find<Canvas>(main, "TouchZonesPicture")!.Width));
        }
        finally
        {
            Step("closing");
            main.Close();
            try { Directory.Delete(root, true); } catch (IOException) { }
            Step("closed");
        }
    });

    // A zone's box where it shows on the picture, in pixels of the picture.
    private static void Placed(Border box, double left, double top, double width, double height)
    {
        Near(left, Canvas.GetLeft(box));
        Near(top, Canvas.GetTop(box));
        Near(width, box.Width);
        Near(height, box.Height);
    }

    // Waits for the page: a cold start on a busy PC can take many seconds (the step log above says which step was slow).
    private static async Task<T> Eventually<T>(Func<T?> find) where T : class
    {
        for (var i = 0; i < 600; i++)
        {
            if (find() is { } found) return found;
            await Task.Delay(50);
        }
        throw new TimeoutException($"Nothing of type {typeof(T).Name} showed up in 30 seconds.");
    }

    private static T? Find<T>(DependencyObject root, string id) where T : FrameworkElement =>
        Descendants(root).OfType<T>().FirstOrDefault(element => AutomationProperties.GetAutomationId(element) == id);

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
        {
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private static void Click(Button button)
    {
        Assert.True(button.IsEnabled, AutomationProperties.GetAutomationId(button) + " is off.");
        button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
    }

    private static byte[] Png(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        for (var i = 0; i < pixels.Length; i += 4) (pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]) = (0xC0, 0x80, 0x60, 0xFF);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4)));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static Window Host(UIElement content, double width)
    {
        var page = new StackPanel { Width = width, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        page.Children.Add(content);
        return new Window
        {
            Content = page, Width = width + 100, Height = 900, ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000
        };
    }

    private static bool Wheel(UIElement target, int delta)
    {
        var turn = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, delta) { RoutedEvent = UIElement.MouseWheelEvent };
        target.RaiseEvent(turn);
        return turn.Handled;
    }

    // Lays the window out and runs what waits on the dispatcher until then (the scroll viewer's offsets follow its layout).
    private static void Settle()
    {
        var done = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, () => done.Continue = false);
        Dispatcher.PushFrame(done);
    }

    private static void Near(Point expected, Point actual)
    {
        Near(expected.X, actual.X);
        Near(expected.Y, actual.Y);
    }

    private static void Near(double expected, double actual) => Assert.InRange(actual, expected - 0.002, expected + 0.002);

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

    // For a test that awaits: the dispatcher runs between its steps, as in the app. Making Martlet's main window the first time in a
    // test process can take half a minute on a busy PC.
    private static async Task OnDispatcherAsync(Func<Task> action)
    {
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.UnhandledException += (_, args) =>
            {
                args.Handled = true;
                finished.TrySetException(args.Exception);
                dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
            };
            _ = dispatcher.BeginInvoke(async () =>
            {
                try { await action(); finished.TrySetResult(); }
                catch (Exception error) { finished.TrySetException(error); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Send); }
            });
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await finished.Task.WaitAsync(TimeSpan.FromMinutes(3));
        Assert.True(thread.Join(TimeSpan.FromSeconds(5)));
    }
}
