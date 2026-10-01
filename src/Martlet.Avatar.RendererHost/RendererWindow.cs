using System.ComponentModel;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Martlet.Avatar.Hosting;
using Martlet.Presentation;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Martlet.Avatar.RendererHost;

internal sealed class RendererWindow : Window
{
    private readonly Stream input, output;
    private readonly CancellationTokenSource lifetime = new();
    private readonly Dictionary<string, AvatarAsset> resources = new(StringComparer.Ordinal);
    // Composition avoids the child-HWND airspace/opacity of the ordinary WPF WebView2.
    private readonly WebView2CompositionControl browser = new()
    {
        DefaultBackgroundColor = System.Drawing.Color.Transparent,
        IsHitTestVisible = false,
        Focusable = false
    };
    private readonly Grid viewport = new() { Background = Brushes.Transparent, Cursor = Cursors.SizeAll };
    private readonly Popup speechBubble = new()
    {
        AllowsTransparency = true, Placement = PlacementMode.Absolute, Focusable = false, IsHitTestVisible = false
    };
    private readonly TextBlock speechText = new() { TextWrapping = TextWrapping.Wrap, MaxWidth = 300, FontSize = 15 };
    private readonly System.Windows.Shapes.Polygon speechTail = new();
    private ResourceDictionary? palette;
    private bool darkTheme;
    private bool highContrast;
    private bool closed;
    private TaskCompletionSource<JsonElement>? response;
    private Guid activation;
    private readonly RendererFailureLatch failure = new();
    private string? userData;

    internal RendererWindow(Stream input, Stream output)
    {
        this.input = input;
        this.output = output;
        Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/Martlet.Avatar.RendererHost;component/Themes/Controls.xaml")
        });
        ApplyOverlayTheme(false);
        Loaded += (_, _) =>
        {
            if (highContrast != SystemParameters.HighContrast) ApplyOverlayTheme(darkTheme);
            SystemParameters.StaticPropertyChanged += SystemAppearanceChanged;
        };
        Dispatcher.ShutdownStarted += (_, _) => SystemParameters.StaticPropertyChanged -= SystemAppearanceChanged;
        Title = "Martlet character overlay";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        ShowActivated = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        PlaceOnDesktop();

        var move = new Button
        {
            Content = "Move character", Cursor = Cursors.SizeAll, Padding = new Thickness(10, 5, 10, 5),
            ToolTip = "Drag the character or this handle. Mouse wheel resizes. Arrow keys move; Shift makes fine adjustments. Home returns to the primary screen."
        };
        AutomationProperties.SetAutomationId(move, "MoveAvatar");
        AutomationProperties.SetName(move, "Move character");
        AutomationProperties.SetHelpText(move, (string)move.ToolTip);
        move.PreviewMouseLeftButtonDown += DragCharacter;
        move.PreviewKeyDown += (_, e) =>
        {
            var step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 1 : 10;
            switch (e.Key)
            {
                case Key.Left: Left -= step; break;
                case Key.Right: Left += step; break;
                case Key.Up: Top -= step; break;
                case Key.Down: Top += step; break;
                case Key.Home: PlaceOnDesktop(); break;
                default: return;
            }
            e.Handled = true;
        };
        var close = new Button
        {
            Content = "Close", Padding = new Thickness(10, 5, 10, 5),
            ToolTip = "Close the avatar only. Voice continues."
        };
        AutomationProperties.SetAutomationId(close, "CloseAvatar");
        AutomationProperties.SetName(close, "Close character overlay");
        close.Click += (_, _) => Close();
        var controls = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        controls.Children.Add(move);
        controls.Children.Add(close);
        controls.Children.Add(CreateSpeechBubble());
        DockPanel.SetDock(controls, Dock.Top);
        var layout = new DockPanel();
        layout.Children.Add(controls);
        layout.Children.Add(viewport);
        viewport.Children.Add(browser);
        var loading = new TextBlock
        {
            Text = "Loading character...",
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Padding = new Thickness(8)
        };
        loading.SetResourceReference(TextBlock.BackgroundProperty, "SurfaceBrush");
        loading.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        viewport.Children.Add(loading);
        viewport.MouseLeftButtonDown += DragCharacter;
        viewport.MouseWheel += (_, e) =>
        {
            // Mouse wheel over the character resizes the overlay, keeping its bottom-center anchored.
            e.Handled = true;
            var area = SystemParameters.WorkArea;
            var width = Math.Clamp(Width * (e.Delta > 0 ? 1.1 : 1 / 1.1), 180, Math.Min(area.Width, area.Height * 0.75));
            var height = width * 4 / 3;
            var centerX = Left + Width / 2;
            var bottom = Top + Height;
            Width = width;
            Height = height;
            Left = centerX - width / 2;
            Top = bottom - height;
        };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; Close(); } };
        Content = layout;
        Loaded += async (_, _) => await RunAsync();
        Closed += (_, _) =>
        {
            closed = true;
            SystemParameters.StaticPropertyChanged -= SystemAppearanceChanged;
            lifetime.Cancel();
            input.Dispose();
            output.Dispose();
            browser.Dispose();
        };
    }

    internal void ApplyOverlayTheme(bool dark)
    {
        darkTheme = dark;
        highContrast = SystemParameters.HighContrast;
        if (palette is not null) Resources.MergedDictionaries.Remove(palette);
        palette = AppearancePalette.Create(dark, highContrast);
        Resources.MergedDictionaries.Add(palette);
    }

    private void SystemAppearanceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SystemParameters.HighContrast) &&
            !closed && !Dispatcher.HasShutdownStarted && !Dispatcher.HasShutdownFinished)
            Dispatcher.InvokeAsync(() => { if (!closed) ApplyOverlayTheme(darkTheme); });
    }

    private void PlaceOnDesktop()
    {
        var area = SystemParameters.WorkArea;
        Width = Math.Min(420, area.Width);
        Height = Math.Min(560, area.Height);
        Left = Math.Max(area.Left, area.Right - Width - 24);
        Top = Math.Max(area.Top, area.Bottom - Height - 24);
    }

    private void DragCharacter(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed) return;
        e.Handled = true;
        if (sender is Button handle) handle.Focus();
        DragMove();
    }

    private Popup CreateSpeechBubble()
    {
        speechText.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        AutomationProperties.SetAutomationId(speechText, "CharacterSpeech");
        AutomationProperties.SetLiveSetting(speechText, AutomationLiveSetting.Polite);
        var body = new Border
        {
            CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(2), Padding = new Thickness(12, 8, 12, 9),
            Child = speechText
        };
        body.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
        body.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        speechTail.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "SurfaceBrush");
        speechTail.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "BorderBrush");
        speechTail.StrokeThickness = 2;
        speechTail.VerticalAlignment = VerticalAlignment.Bottom;
        speechTail.Margin = new Thickness(-2, 0, -2, 14);
        var shape = new DockPanel { LastChildFill = true };
        shape.Children.Add(speechTail);
        shape.Children.Add(body);
        speechBubble.Child = shape;
        LocationChanged += (_, _) => PlaceSpeech();
        SizeChanged += (_, _) => PlaceSpeech();
        Closed += (_, _) => speechBubble.IsOpen = false;
        return speechBubble;
    }

    // The bubble sits beside the character's head: to its left when there is room on screen, otherwise to its right.
    private void ShowSpeech(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            speechBubble.IsOpen = false;
            return;
        }
        speechText.Text = text.Length > 600 ? text[..600] + "…" : text;
        PlaceSpeech();
        speechBubble.IsOpen = true;
    }

    private void PlaceSpeech()
    {
        if (speechText.Text.Length == 0) return;
        var shape = (FrameworkElement)speechBubble.Child;
        shape.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = shape.DesiredSize;
        var left = Left + Width * 0.18 - size.Width >= SystemParameters.VirtualScreenLeft;
        DockPanel.SetDock(speechTail, left ? Dock.Right : Dock.Left);
        speechTail.Points = left
            ? new PointCollection { new(0, 0), new(14, 10), new(0, 18) }
            : new PointCollection { new(14, 0), new(0, 10), new(14, 18) };
        var head = Top + Height * 0.32;
        speechBubble.HorizontalOffset = left ? Left + Width * 0.18 - size.Width : Left + Width * 0.82;
        speechBubble.VerticalOffset = Math.Max(SystemParameters.VirtualScreenTop, head - size.Height);
        if (speechBubble.IsOpen)
        {
            // Nudging the offset forces an open Popup to reposition after its owner moves.
            speechBubble.HorizontalOffset += 0.01;
            speechBubble.HorizontalOffset -= 0.01;
        }
    }

    private async Task RunAsync()
    {
        try
        {
            using var handshake = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            handshake.CancelAfter(TimeSpan.FromSeconds(15));
            var message = await RendererProtocol.ReadAsync(input, handshake.Token);
            if (message.Kind != "load") throw new InvalidDataException("Private renderer initialization required.");
            activation = message.Activation;
            var load = RendererProtocol.Data<RendererLoad>(message);
            ApplyOverlayTheme(load.DarkTheme);
            var assets = await LocalAvatarFiles.SnapshotAsync(load.Profile, handshake.Token);
            if (assets.Revision != load.ResourceRevision) throw new InvalidDataException("Selected resources changed.");
            foreach (var asset in assets.Assets) resources.Add(RendererResourcePolicy.CanonicalName("asset/" + asset.Name), asset);
            var web = Path.Combine(AppContext.BaseDirectory, "web");
            foreach (var name in new[] { "index.html", "app.js" })
                resources.Add(name, new(name, await LocalAvatarFiles.ReadBoundedAsync(Path.Combine(web, name),
                    16 * 1024 * 1024, handshake.Token), name.EndsWith(".html", StringComparison.Ordinal) ? "text/html" : "text/javascript"));
            _ = CoreWebView2Environment.GetAvailableBrowserVersionString();
            userData = Path.Combine(Path.GetTempPath(), "Martlet.Avatar", activation.ToString("N"));
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: userData,
                options: new CoreWebView2EnvironmentOptions { AllowSingleSignOnUsingOSPrimaryAccount = false });
            await browser.EnsureCoreWebView2Async(environment);
            var core = browser.CoreWebView2;
            core.Settings.AreHostObjectsAllowed = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreDefaultScriptDialogsEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;
            core.PermissionRequested += (_, args) => args.State = CoreWebView2PermissionState.Deny;
            core.NewWindowRequested += (_, args) => args.Handled = true;
            core.DownloadStarting += (_, args) => args.Cancel = true;
            core.NavigationStarting += (_, args) => args.Cancel = args.Uri != RendererResourcePolicy.Document;
            core.FrameNavigationStarting += (_, args) => args.Cancel = true;
            core.ProcessFailed += (_, _) => FailRenderer();
            core.WebMessageReceived += (_, args) =>
            {
                if (args.Source != RendererResourcePolicy.Document) return;
                try
                {
                    if (args.WebMessageAsJson.Length > RendererProtocol.MaximumMessageBytes)
                        throw new InvalidDataException("Browser reply exceeds its limit.");
                    using var document = JsonDocument.Parse(args.WebMessageAsJson);
                    if (document.RootElement.TryGetProperty("error", out var rendererError))
                    {
                        FailRenderer();
                        return;
                    }
                    failure.ThrowIfFailed();
                    response?.TrySetResult(document.RootElement.Clone());
                }
                catch (Exception error) when (error is JsonException or InvalidDataException)
                { FailRenderer(); }
            };
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += (_, args) =>
            {
                var uri = args.Request.Uri;
                if (RendererResourcePolicy.ResourceName(uri, args.Request.Method) is { } name &&
                    resources.TryGetValue(name, out var asset))
                    args.Response = environment.CreateWebResourceResponse(new MemoryStream(asset.Bytes, writable: false), 200, "OK",
                        $"Content-Type: {asset.ContentType}\r\nCache-Control: no-store\r\n" +
                        $"Content-Security-Policy: {RendererResourcePolicy.ContentSecurityPolicy(load.Profile.Renderer == Martlet.Avatars.AvatarRenderer.Live2D)}\r\n");
                else args.Response = environment.CreateWebResourceResponse(Stream.Null, 403, "Blocked", "Content-Type: text/plain");
            };
            viewport.Children.RemoveAt(1);
            response = new(TaskCreationOptions.RunContinuationsAsynchronously);
            core.Navigate(RendererResourcePolicy.Document);
            await response.Task.WaitAsync(TimeSpan.FromSeconds(15), lifetime.Token);
            var loaded = await BrowserAsync("load", new { renderer = load.Profile.Renderer.ToString(),
                modelFile = assets.ModelFile, resourceRevision = assets.Revision,
                assets = assets.Assets.Select(a => a.Name).ToArray() });
            await ReplyAsync("capabilities", loaded);
            StartLookTracking();
            while (!lifetime.IsCancellationRequested)
            {
                message = await RendererProtocol.ReadAsync(input, lifetime.Token);
                if (message.Activation != activation || message.Kind is not ("configure" or "reset" or "apply" or "stop" or "theme" or "mouth" or "motion" or "say"))
                    throw new InvalidDataException("Renderer command is invalid.");
                if (message.Kind == "theme")
                {
                    ApplyOverlayTheme(RendererProtocol.Data<RendererTheme>(message).Dark);
                    await ReplyAsync("ok", new { });
                    continue;
                }
                if (message.Kind == "say")
                {
                    ShowSpeech(RendererProtocol.Data<RendererSay>(message).Text);
                    await ReplyAsync("ok", new { });
                    continue;
                }
                var result = await BrowserAsync(message.Kind, message.Data);
                await ReplyAsync("ok", result);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested && !failure.Failed) { }
        catch (Exception error) when (error is IOException or ArgumentException or InvalidOperationException or
            System.Runtime.InteropServices.COMException or TimeoutException or JsonException or
            Martlet.Core.Contracts.ContractException or OperationCanceledException)
        {
            try
            {
                if (activation != Guid.Empty)
                    await RendererProtocol.WriteAsync(output, RendererProtocol.Message("error", activation,
                        new { code = "avatar.renderer_unavailable", message = "Renderer/runtime/resource operation failed. Inspect local prerequisites and retry explicitly." }),
                        CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(1));
            }
            catch (Exception failure) when (failure is IOException or ObjectDisposedException or TimeoutException) { }
        }
        finally { Close(); }
    }

    private async Task<JsonElement> BrowserAsync<T>(string kind, T data)
    {
        failure.ThrowIfFailed();
        response = new(TaskCreationOptions.RunContinuationsAsynchronously);
        browser.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { kind, data }, RendererProtocol.Json));
        var result = await response.Task.WaitAsync(TimeSpan.FromSeconds(kind == "load" ? 30 : 2), lifetime.Token);
        failure.ThrowIfFailed();
        if (result.TryGetProperty("error", out _)) throw new InvalidDataException("Browser rejected the selected resource or controls.");
        return result;
    }

    private void FailRenderer()
    {
        failure.Fail();
        response?.TrySetException(new InvalidDataException("Renderer failed; fresh inspection required."));
        lifetime.Cancel();
    }

    // The character's head and eyes follow the mouse cursor; messages are fire-and-forget and never replied to.
    private void StartLookTracking()
    {
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        double lastX = double.NaN, lastY = double.NaN;
        timer.Tick += (_, _) =>
        {
            if (closed || failure.Failed || browser.CoreWebView2 is null) { timer.Stop(); return; }
            if (!GetCursorPos(out var cursor)) return;
            Point face;
            try { face = viewport.PointToScreen(new Point(viewport.ActualWidth / 2, viewport.ActualHeight * 0.3)); }
            catch (InvalidOperationException) { return; }
            var x = Math.Clamp((cursor.X - face.X) / 700, -1, 1);
            var y = Math.Clamp((face.Y - cursor.Y) / 700, -1, 1);
            if (Math.Abs(x - lastX) < 0.01 && Math.Abs(y - lastY) < 0.01) return;
            lastX = x;
            lastY = y;
            try
            {
                browser.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { kind = "look", data = new { x, y } },
                    RendererProtocol.Json));
            }
            catch (Exception error) when (error is InvalidOperationException or System.Runtime.InteropServices.COMException)
            { timer.Stop(); }
        };
        Closed += (_, _) => timer.Stop();
        timer.Start();
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct CursorPoint { public int X, Y; }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out CursorPoint point);

    private Task ReplyAsync<T>(string kind, T data) =>
        RendererProtocol.WriteAsync(output, RendererProtocol.Message(kind, activation, data), lifetime.Token);
}
