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
        AllowsTransparency = true, Placement = PlacementMode.Absolute, Focusable = false, IsHitTestVisible = false,
        PopupAnimation = PopupAnimation.Fade
    };
    private readonly TextBlock speechText = new()
    {
        TextWrapping = TextWrapping.Wrap, MaxWidth = 280, FontSize = 15, LineHeight = 21,
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"), TextAlignment = TextAlignment.Left
    };
    private readonly System.Windows.Shapes.Path speechShape = new() { StrokeThickness = 2, StrokeLineJoin = PenLineJoin.Round };
    private readonly Canvas speechCanvas = new();
    private readonly ScaleTransform speechPop = new(1, 1);
    private RendererSay speechPlacement = new(null);
    private RendererBubble speechShown = new("hidden", 0, 0, 0, 0);
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

        // Show/hide and reset-position controls live in the main Martlet window; the overlay shows only the character.
        AutomationProperties.SetAutomationId(viewport, "MoveAvatar");
        AutomationProperties.SetName(viewport,
            "Character. Drag to move; mouse wheel zooms; Ctrl+drag or middle-drag pans when zoomed in; right-click for zoom and reset options.");
        viewport.Children.Add(browser);
        var loading = new TextBlock
        {
            Text = "Loading character...",
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Padding = new Thickness(8)
        };
        loading.SetResourceReference(TextBlock.BackgroundProperty, "SurfaceBrush");
        loading.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        viewport.Children.Add(loading);
        viewport.Children.Add(CreateSpeechBubble());
        viewport.MouseLeftButtonDown += DragCharacter;
        viewport.MouseDown += (_, e) => { if (e.ChangedButton == MouseButton.Middle) StartPan(e); };
        viewport.MouseMove += (_, e) => Pan(e.GetPosition(viewport));
        viewport.MouseUp += (_, _) => EndPan();
        viewport.LostMouseCapture += (_, _) => panFrom = null;
        // Mouse wheel grows the overlay up to screen height, then keeps zooming into the character toward the cursor.
        viewport.MouseWheel += (_, e) =>
        {
            e.Handled = true;
            Zoom(e.Delta > 0 ? ZoomStep : 1 / ZoomStep, e.GetPosition(viewport));
        };
        viewport.ContextMenu = CreateZoomMenu();
        PreviewKeyDown += (_, e) =>
        {
            var step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 1 : 10;
            switch (e.Key)
            {
                case Key.Escape: Close(); break;
                case Key.Left: Left -= step; break;
                case Key.Right: Left += step; break;
                case Key.Up: Top -= step; break;
                case Key.Down: Top += step; break;
                case Key.Home: ResetToDefault(); break;
                case Key.OemPlus or Key.Add: Zoom(ZoomStep * ZoomStep, null); break;
                case Key.OemMinus or Key.Subtract: Zoom(1 / (ZoomStep * ZoomStep), null); break;
                case Key.D0 or Key.NumPad0: ResetZoom(); break;
                default: return;
            }
            e.Handled = true;
        };
        Content = viewport;
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

    private const double ZoomStep = 1.1, MaxViewZoom = 16, MinOverlayWidth = 180, HeadMargin = 0.05;
    private double viewZoom = 1, viewX, viewY;
    // Top of the character (top of the head) in the renderer's fitted clip space: 1 is the overlay's top edge unzoomed.
    private double contentTop = double.NaN;
    private Point? panFrom;

    private static double MaxOverlayWidth
    {
        get
        {
            var area = SystemParameters.WorkArea;
            return Math.Max(MinOverlayWidth, Math.Min(area.Width, area.Height * 0.75));
        }
    }

    private bool CanZoomIn => Width < MaxOverlayWidth - 0.5 || viewZoom < MaxViewZoom;
    private bool CanZoomOut => viewZoom > 1 || Width > MinOverlayWidth + 0.5;
    private bool IsDefaultZoom => viewZoom == 1 && Math.Abs(Width - Math.Min(420, SystemParameters.WorkArea.Width)) < 0.5;

    /// <summary>
    /// Zooms in by growing the overlay up to its screen-height limit, then by zooming the camera into the
    /// character (toward <paramref name="anchor"/>, or the face). Zooming out reverses that order.
    /// </summary>
    private void Zoom(double factor, Point? anchor)
    {
        if (factor > 1 && Width < MaxOverlayWidth - 0.5) ResizeOverlay(Width * factor);
        else if (factor > 1 || viewZoom > 1)
        {
            var point = anchor ?? new Point(viewport.ActualWidth / 2, viewport.ActualHeight * 0.3);
            var cx = viewport.ActualWidth > 0 ? point.X / viewport.ActualWidth * 2 - 1 : 0;
            var cy = viewport.ActualHeight > 0 ? 1 - point.Y / viewport.ActualHeight * 2 : 0;
            var zoom = Math.Clamp(viewZoom * factor, 1, MaxViewZoom);
            var applied = zoom / viewZoom;
            SetView(zoom, cx - (cx - viewX) * applied, cy - (cy - viewY) * applied);
        }
        else ResizeOverlay(Width * factor);
    }

    // Resizes the overlay keeping its bottom-center anchored, except that growing never pushes its top (and so the
    // character's head) above the top of the screen's work area; it grows downward from there instead.
    private void ResizeOverlay(double width)
    {
        width = Math.Clamp(width, MinOverlayWidth, MaxOverlayWidth);
        var height = width * 4 / 3;
        var centerX = Left + Width / 2;
        var bottom = Top + Height;
        var top = bottom - height;
        if (height > Height && WorkAreaTop() is { } screenTop) top = Math.Max(top, Math.Min(Top, screenTop));
        Width = width;
        Height = height;
        Left = centerX - width / 2;
        Top = top;
    }

    private void ResetZoom()
    {
        SetView(1, 0, 0);
        ResizeOverlay(Math.Min(420, SystemParameters.WorkArea.Width));
    }

    private void ResetToDefault()
    {
        SetView(1, 0, 0);
        PlaceOnDesktop();
    }

    // Pan is clamped so the zoomed view never leaves the character's fitted frame and the top of the head stays in
    // view: it never rises above the top edge (less a small margin), or above where it sits unzoomed if already cut off.
    private void SetView(double zoom, double x, double y)
    {
        viewZoom = zoom;
        viewX = Math.Clamp(x, 1 - zoom, zoom - 1);
        double minY = 1 - zoom, maxY = zoom - 1;
        if (double.IsFinite(contentTop))
        {
            maxY = Math.Min(maxY, Math.Max(1 - HeadMargin, contentTop) - contentTop * zoom);
            minY = Math.Min(minY, maxY);
        }
        viewY = Math.Clamp(y, minY, maxY);
        SendView();
        PlaceSpeech();
    }

    private void ApplyContentTop(double top)
    {
        contentTop = Math.Clamp(top, -1, 4);
        SetView(viewZoom, viewX, viewY);
    }

    /// <summary>The overlay's size, position and camera, and how far the top of the head sits below its top edge.</summary>
    private RendererView ViewState() => new(Math.Round(Width), Math.Round(Height),
        WorkAreaTop() is { } screenTop ? Math.Round(Top - screenTop) : null, Math.Round(viewZoom, 3),
        double.IsFinite(contentTop) ? Math.Round((1 - (contentTop * viewZoom + viewY)) / 2, 4) : null);

    private void SendView()
    {
        try
        {
            browser.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(
                new { kind = "view", data = new { zoom = viewZoom, x = viewX, y = viewY } }, RendererProtocol.Json));
        }
        catch (Exception error) when (error is InvalidOperationException or System.Runtime.InteropServices.COMException) { }
    }

    private void StartPan(MouseButtonEventArgs e)
    {
        if (viewZoom <= 1) return;
        e.Handled = true;
        panFrom = e.GetPosition(viewport);
        viewport.CaptureMouse();
    }

    private void Pan(Point position)
    {
        if (panFrom is not { } from || viewport.ActualWidth <= 0 || viewport.ActualHeight <= 0) return;
        panFrom = position;
        SetView(viewZoom, viewX + (position.X - from.X) * 2 / viewport.ActualWidth,
            viewY - (position.Y - from.Y) * 2 / viewport.ActualHeight);
    }

    private void EndPan()
    {
        if (panFrom is null) return;
        panFrom = null;
        viewport.ReleaseMouseCapture();
    }

    private ContextMenu CreateZoomMenu()
    {
        MenuItem Item(string header, string id, string gesture, Action action)
        {
            var item = new MenuItem { Header = header, InputGestureText = gesture };
            AutomationProperties.SetAutomationId(item, id);
            item.Click += (_, _) => action();
            return item;
        }
        var zoomIn = Item("Zoom _in", "ZoomIn", "+", () => Zoom(ZoomStep * ZoomStep, null));
        var zoomOut = Item("Zoom _out", "ZoomOut", "-", () => Zoom(1 / (ZoomStep * ZoomStep), null));
        var reset = Item("_Reset zoom", "ResetZoom", "0", ResetZoom);
        var home = Item("Reset _position and size", "ResetPosition", "Home", ResetToDefault);
        var menu = new ContextMenu { Items = { zoomIn, zoomOut, reset, new Separator(), home } };
        menu.Opened += (_, _) =>
        {
            zoomIn.IsEnabled = CanZoomIn;
            zoomOut.IsEnabled = CanZoomOut;
            reset.IsEnabled = !IsDefaultZoom;
        };
        return menu;
    }

    private void DragCharacter(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed) return;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && viewZoom > 1)
        {
            StartPan(e);
            return;
        }
        e.Handled = true;
        DragMove();
    }

    private const double BubbleRadius = 16, BubblePadX = 16, BubblePadY = 10, TailLength = 22, TailHalfBase = 9,
        BubbleMargin = 18, ScreenMargin = 6;

    // One continuous outline (rounded body unioned with a curved, tapering tail) with a soft shadow, so the bubble reads as
    // a single comic-style shape rather than a box with a triangle stuck on.
    private Popup CreateSpeechBubble()
    {
        speechText.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        AutomationProperties.SetAutomationId(speechText, "CharacterSpeech");
        AutomationProperties.SetLiveSetting(speechText, AutomationLiveSetting.Polite);
        speechShape.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "SurfaceBrush");
        speechShape.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "AccentBrush");
        speechShape.Effect = new System.Windows.Media.Effects.DropShadowEffect
        {
            BlurRadius = 14, ShadowDepth = 3, Direction = 270, Opacity = 0.3, Color = Colors.Black
        };
        speechCanvas.Children.Add(speechShape);
        speechCanvas.Children.Add(speechText);
        speechCanvas.RenderTransform = speechPop;
        speechBubble.Child = speechCanvas;
        LocationChanged += (_, _) => PlaceSpeech();
        SizeChanged += (_, _) => PlaceSpeech();
        Closed += (_, _) => speechBubble.IsOpen = false;
        return speechBubble;
    }

    private RendererBubble ShowSpeech(RendererSay say)
    {
        speechPlacement = say;
        if (string.IsNullOrWhiteSpace(say.Text))
        {
            speechBubble.IsOpen = false;
            speechText.Text = "";
            return speechShown = new("hidden", 0, 0, 0, 0);
        }
        speechText.Text = say.Text.Length > 600 ? say.Text[..600] + "…" : say.Text;
        PlaceSpeech();
        speechBubble.IsOpen = true;
        if (SystemParameters.ClientAreaAnimation)
        {
            var pop = new System.Windows.Media.Animation.DoubleAnimation(0.86, 1, TimeSpan.FromMilliseconds(180))
            {
                EasingFunction = new System.Windows.Media.Animation.BackEase
                {
                    Amplitude = 0.35, EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut
                }
            };
            speechPop.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
            speechPop.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
        }
        return speechShown;
    }

    // By default the bubble follows the character's head through moves, zoom and pan: beside the head on whichever side has
    // room on its screen (left first, then right), else above it, then shifted by the configured offsets and kept on screen.
    // Static keeps it at one spot on the character's screen, with its tail still pointing toward the character.
    private void PlaceSpeech()
    {
        if (speechText.Text.Length == 0) return;
        speechText.Measure(new Size(speechText.MaxWidth, double.PositiveInfinity));
        var width = Math.Max(48, Math.Ceiling(speechText.DesiredSize.Width) + BubblePadX * 2);
        var height = Math.Ceiling(speechText.DesiredSize.Height) + BubblePadY * 2;
        var screen = WorkArea() ?? SystemParameters.WorkArea;
        screen.Inflate(-ScreenMargin, -ScreenMargin);

        // The head in screen coordinates, followed through the camera: screen clip = fitted clip * zoom + pan.
        var top = double.IsFinite(contentTop) ? contentTop : 0.6;
        double ScreenY(double clip) => Top + Math.Clamp(Height * (1 - (clip * viewZoom + viewY)) / 2, 0, Height);
        double ScreenX(double fraction) => Left + Math.Clamp(fraction, 0, Width);
        var faceX = Width * (viewX + 1) / 2;
        var halfHead = Width * 0.11 * viewZoom;
        var faceY = ScreenY(top - 0.2);
        var headTop = ScreenY(top);
        var face = new Point(ScreenX(faceX), faceY);

        string placement;
        Rect body;
        Point tip;
        if (speechPlacement.Static)
        {
            placement = "static";
            body = new Rect(screen.Left + 10 + speechPlacement.OffsetX, screen.Top + 10 + speechPlacement.OffsetY, width, height);
            tip = face;
        }
        else
        {
            var candidates = new (string Name, Point Tip, Rect Body)[]
            {
                ("left", new Point(ScreenX(faceX - halfHead), faceY),
                    new Rect(ScreenX(faceX - halfHead) - TailLength - width, faceY + 14 - height, width, height)),
                ("right", new Point(ScreenX(faceX + halfHead), faceY),
                    new Rect(ScreenX(faceX + halfHead) + TailLength, faceY + 14 - height, width, height)),
                ("above", new Point(ScreenX(faceX), headTop - 4),
                    new Rect(ScreenX(faceX) - width * 0.4, headTop - 4 - TailLength - height, width, height))
            };
            for (var i = 0; i < candidates.Length; i++) candidates[i].Body.Offset(speechPlacement.OffsetX, speechPlacement.OffsetY);
            var chosen = candidates.FirstOrDefault(c => screen.Contains(c.Body));
            if (chosen.Name is null)
                chosen = candidates.OrderBy(c => Overflow(c.Body, screen)).First();
            (placement, tip, body) = chosen;
        }
        body = KeepInside(body, screen);
        speechShown = new(placement, Math.Round(body.Left), Math.Round(body.Top), Math.Round(body.Width), Math.Round(body.Height));

        // Lay the shape out on a canvas spanning the body and tail, with room for the shadow.
        var tail = Tail(body, tip, placement == "static" ? TailLength * 0.9 : TailLength * 2.5);
        var bounds = body;
        if (tail is not null) bounds.Union(tail.Value.Tip);
        bounds.Inflate(BubbleMargin, BubbleMargin);
        var origin = bounds.TopLeft;
        var local = new Rect(body.Left - origin.X, body.Top - origin.Y, width, height);
        var radius = Math.Min(BubbleRadius, height / 2);
        Geometry outline = new RectangleGeometry(local, radius, radius);
        if (tail is { } t)
            outline = new CombinedGeometry(GeometryCombineMode.Union, outline,
                TailGeometry(t.Base1 - (Vector)origin, t.Base2 - (Vector)origin, t.Tip - (Vector)origin));
        outline.Freeze();
        speechShape.Data = outline;
        speechCanvas.Width = bounds.Width;
        speechCanvas.Height = bounds.Height;
        Canvas.SetLeft(speechText, local.Left + BubblePadX);
        Canvas.SetTop(speechText, local.Top + BubblePadY);
        var pivot = tail?.Tip ?? new Point(body.Left + width / 2, body.Bottom);
        speechCanvas.RenderTransformOrigin = new Point((pivot.X - origin.X) / bounds.Width, (pivot.Y - origin.Y) / bounds.Height);
        speechBubble.HorizontalOffset = origin.X;
        speechBubble.VerticalOffset = origin.Y;
        if (speechBubble.IsOpen)
        {
            // Nudging the offset forces an open Popup to reposition after its owner moves.
            speechBubble.HorizontalOffset += 0.01;
            speechBubble.HorizontalOffset -= 0.01;
        }
    }

    private static double Overflow(Rect body, Rect screen) =>
        Math.Max(0, screen.Left - body.Left) + Math.Max(0, body.Right - screen.Right) +
        Math.Max(0, screen.Top - body.Top) + Math.Max(0, body.Bottom - screen.Bottom);

    private static Rect KeepInside(Rect body, Rect screen)
    {
        var x = Math.Max(screen.Left, Math.Min(body.Left, screen.Right - body.Width));
        var y = Math.Max(screen.Top, Math.Min(body.Top, screen.Bottom - body.Height));
        return new Rect(x, y, body.Width, body.Height);
    }

    // The tail leaves the body edge facing the target, as near to it as the rounded corners allow, and points at it
    // (shortened to the longest tail that still looks like a tail). None when the target is under the body.
    private static (Point Base1, Point Base2, Point Tip)? Tail(Rect body, Point target, double longest)
    {
        var dx = target.X < body.Left ? body.Left - target.X : target.X > body.Right ? target.X - body.Right : 0;
        var dy = target.Y < body.Top ? body.Top - target.Y : target.Y > body.Bottom ? target.Y - body.Bottom : 0;
        if (dx <= 0 && dy <= 0) return null;
        var inset = Math.Min(BubbleRadius, body.Height / 2) + TailHalfBase;
        Point center;
        Vector across;
        if (dx >= dy)
        {
            var y = body.Height > inset * 2 ? Math.Clamp(target.Y, body.Top + inset, body.Bottom - inset) : body.Top + body.Height / 2;
            center = new Point(target.X < body.Left ? body.Left + 3 : body.Right - 3, y);
            across = new Vector(0, TailHalfBase);
        }
        else
        {
            var x = body.Width > inset * 2 ? Math.Clamp(target.X, body.Left + inset, body.Right - inset) : body.Left + body.Width / 2;
            center = new Point(x, target.Y < body.Top ? body.Top + 3 : body.Bottom - 3);
            across = new Vector(TailHalfBase, 0);
        }
        var reach = target - center;
        if (reach.Length > longest) reach *= longest / reach.Length;
        if (reach.Length < 8) return null;
        return (center - across, center + across, center + reach);
    }

    private static PathGeometry TailGeometry(Point base1, Point base2, Point tip)
    {
        var middle = new Point((base1.X + base2.X) / 2, (base1.Y + base2.Y) / 2);
        var spine = middle + (tip - middle) * 0.5;
        Point Bend(Point from) => (from + (tip - from) * 0.5) + (spine - (from + (tip - from) * 0.5)) * 0.45;
        var figure = new PathFigure { StartPoint = base1, IsClosed = true, IsFilled = true };
        figure.Segments.Add(new QuadraticBezierSegment(Bend(base1), tip, true));
        figure.Segments.Add(new QuadraticBezierSegment(Bend(base2), base2, true));
        return new PathGeometry { Figures = { figure } };
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
                    if (document.RootElement.TryGetProperty("bounds", out var bounds))
                    {
                        // Unsolicited and never a command reply.
                        if (bounds.ValueKind != JsonValueKind.Object || !bounds.TryGetProperty("top", out var top) ||
                            top.ValueKind != JsonValueKind.Number || !double.IsFinite(top.GetDouble()))
                            throw new InvalidDataException("Browser bounds are invalid.");
                        ApplyContentTop(top.GetDouble());
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
            SendView();
            StartLookTracking();
            while (!lifetime.IsCancellationRequested)
            {
                message = await RendererProtocol.ReadAsync(input, lifetime.Token);
                if (message.Activation != activation || message.Kind is not ("configure" or "reset" or "apply" or "stop" or "theme" or "mouth" or "motion" or "home" or "zoom" or "say"))
                    throw new InvalidDataException("Renderer command is invalid.");
                if (message.Kind == "home")
                {
                    ResetToDefault();
                    await ReplyAsync("ok", new { });
                    continue;
                }
                if (message.Kind == "zoom")
                {
                    switch (RendererProtocol.Data<RendererZoom>(message).Action)
                    {
                        case "in": Zoom(ZoomStep * ZoomStep, null); break;
                        case "out": Zoom(1 / (ZoomStep * ZoomStep), null); break;
                        case "reset": ResetZoom(); break;
                        case "status": break;
                        default: throw new InvalidDataException("Zoom action is invalid.");
                    }
                    await ReplyAsync("view", ViewState());
                    continue;
                }
                if (message.Kind == "theme")
                {
                    ApplyOverlayTheme(RendererProtocol.Data<RendererTheme>(message).Dark);
                    await ReplyAsync("ok", new { });
                    continue;
                }
                if (message.Kind == "say")
                {
                    await ReplyAsync("bubble", ShowSpeech(RendererProtocol.Data<RendererSay>(message)));
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
            // The face sits at 30% height when unzoomed; follow it through the camera zoom.
            var faceX = (viewX + 1) / 2;
            var faceY = (1 - (0.4 * viewZoom + viewY)) / 2;
            try { face = viewport.PointToScreen(new Point(viewport.ActualWidth * faceX, viewport.ActualHeight * faceY)); }
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

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public uint Flags; }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GetMonitorInfoW(IntPtr monitor, ref MonitorInfo info);

    /// <summary>Top of the work area of the screen the overlay is on, in this window's coordinates.</summary>
    private double? WorkAreaTop() => WorkArea()?.Top;

    /// <summary>Work area of the screen the overlay is on, in this window's coordinates.</summary>
    private Rect? WorkArea()
    {
        var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero || PresentationSource.FromVisual(this)?.CompositionTarget is not { } target) return null;
        var info = new MonitorInfo { Size = System.Runtime.InteropServices.Marshal.SizeOf<MonitorInfo>() };
        var monitor = MonitorFromWindow(handle, 2);
        if (monitor == IntPtr.Zero || !GetMonitorInfoW(monitor, ref info)) return null;
        return new Rect(target.TransformFromDevice.Transform(new Point(info.Work.Left, info.Work.Top)),
            target.TransformFromDevice.Transform(new Point(info.Work.Right, info.Work.Bottom)));
    }

    private Task ReplyAsync<T>(string kind, T data) =>
        RendererProtocol.WriteAsync(output, RendererProtocol.Message(kind, activation, data), lifetime.Token);
}
