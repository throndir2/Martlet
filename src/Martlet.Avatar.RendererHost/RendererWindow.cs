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
using Martlet.Avatar.RendererHost.Logging;
using Martlet.Presentation;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Martlet.Avatar.RendererHost;

internal sealed class RendererWindow : Window
{
    private readonly Stream input, output;
    // Menu choices Martlet itself carries out (hide, open, talk, settings); null when started without it (tests).
    private readonly Stream? requests;
    private readonly SemaphoreSlim requesting = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly Dictionary<string, AvatarAsset> resources = new(StringComparer.Ordinal);
    // Composition avoids the child-HWND airspace/opacity of the ordinary WPF WebView2.
    private readonly WebView2CompositionControl browser = new()
    {
        DefaultBackgroundColor = System.Drawing.Color.Transparent,
        IsHitTestVisible = false,
        Focusable = false
    };
    private readonly CharacterViewport viewport = new() { Background = Brushes.Transparent, Cursor = Cursors.SizeAll };
    private readonly Popup speechBubble = new()
    {
        AllowsTransparency = true, Placement = PlacementMode.Absolute, Focusable = false, IsHitTestVisible = false,
        PopupAnimation = PopupAnimation.Fade
    };
    private readonly TextBlock speechText = SpeechText();
    // Sizes the bubble. The shown text can't: while the bubble is hidden its closed popup suspends layout, so measuring the
    // shown text then returns its old size (an empty bubble) and the next speech would overflow a tiny bubble.
    private readonly TextBlock speechMeasure = SpeechText();
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
    // Why the browser couldn't load the selected model (a bounded Live2D/VRM reason), reported back to Martlet.
    private volatile string? modelRejection;
    private string? userData;

    internal RendererWindow(Stream input, Stream output, Stream? requests = null)
    {
        this.input = input;
        this.output = output;
        this.requests = requests;
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

        // The main Martlet window also shows, hides and resets the character; the overlay shows only the character and its menu.
        AutomationProperties.SetAutomationId(viewport, "MoveAvatar");
        viewport.MoveTo = MoveOverlayTo;
        ShowPlacementLock();
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
        viewport.ContextMenu = CreateCharacterMenu();
        PreviewKeyDown += (_, e) =>
        {
            var step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 1 : 10;
            // A locked place ignores the arrow keys and Home; only Martlet's window unlocks it.
            switch (e.Key)
            {
                case Key.Escape: Request("hide"); break;
                case Key.Left when !placementLocked: Left -= step; break;
                case Key.Right when !placementLocked: Left += step; break;
                case Key.Up when !placementLocked: Top -= step; break;
                case Key.Down when !placementLocked: Top += step; break;
                case Key.Home when !placementLocked: ResetToDefault(); break;
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
            requests?.Dispose();
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

    // The character is fitted into a portrait frame centered in the overlay. Each side of the frame has this much of its
    // width again as transparent room the model can move into (swinging tails, hair, arms), so motion isn't cut off at the
    // frame's edges. Clicks pass through the transparent room; only the character itself is a drag surface.
    private const double SideRoom = 0.5, FrameFraction = 1 / (1 + 2 * SideRoom), DefaultFrameWidth = 420;

    /// <summary>Width of the character's frame; the overlay is this plus the side room on each side.</summary>
    private double FrameWidth => Width * FrameFraction;

    /// <summary>The character's frame within the overlay, in screen coordinates (device-independent pixels).</summary>
    internal Rect Frame => new(Left + FrameOffset(Width), Top, FrameWidth, Height);

    private static double OverlayWidth(double frameWidth) => frameWidth / FrameFraction;

    /// <summary>Where the frame starts inside an overlay (or viewport) of the given full width.</summary>
    private static double FrameOffset(double width) => width * FrameFraction * SideRoom;

    private void PlaceOnDesktop()
    {
        var area = SystemParameters.WorkArea;
        var frame = Math.Min(DefaultFrameWidth, area.Width);
        Width = OverlayWidth(frame);
        Height = Math.Min(560, area.Height);
        // The frame (the character) sits near the lower-right corner; the room beside it may run past the screen's edge.
        Left = Math.Max(area.Left, area.Right - frame - 24) - FrameOffset(Width);
        Top = Math.Max(area.Top, area.Bottom - Height - 24);
    }

    private const double ZoomStep = 1.1, MaxViewZoom = 16, MinFrameWidth = 180, HeadMargin = 0.05;
    private double viewZoom = 1, viewX, viewY;
    // Top of the character (top of the head) in the renderer's fitted clip space: 1 is the overlay's top edge unzoomed.
    private double contentTop = double.NaN;
    private Point? panFrom;

    private static double MaxFrameWidth
    {
        get
        {
            var area = SystemParameters.WorkArea;
            return Math.Max(MinFrameWidth, Math.Min(area.Width, area.Height * 0.75));
        }
    }

    private bool CanZoomIn => (!placementLocked && FrameWidth < MaxFrameWidth - 0.5) || viewZoom < MaxViewZoom;
    private bool CanZoomOut => viewZoom > 1 || (!placementLocked && FrameWidth > MinFrameWidth + 0.5);
    private bool IsDefaultZoom => viewZoom == 1 && Math.Abs(FrameWidth - Math.Min(DefaultFrameWidth, SystemParameters.WorkArea.Width)) < 0.5;
    private bool CanResetZoom => placementLocked ? viewZoom != 1 : !IsDefaultZoom;

    /// <summary>
    /// Zooms in by growing the overlay up to its screen-height limit, then by zooming the camera into the
    /// character (toward <paramref name="anchor"/>, or the face). Zooming out reverses that order. While the place is locked
    /// the overlay keeps its size and only the camera zooms.
    /// </summary>
    private void Zoom(double factor, Point? anchor)
    {
        if (factor > 1 && !placementLocked && FrameWidth < MaxFrameWidth - 0.5) ResizeOverlay(FrameWidth * factor);
        else if (factor > 1 || viewZoom > 1)
        {
            var point = anchor ?? new Point(viewport.ActualWidth / 2, viewport.ActualHeight * 0.3);
            var frame = viewport.ActualWidth * FrameFraction;
            var cx = frame > 0 ? (point.X - FrameOffset(viewport.ActualWidth)) / frame * 2 - 1 : 0;
            var cy = viewport.ActualHeight > 0 ? 1 - point.Y / viewport.ActualHeight * 2 : 0;
            var zoom = Math.Clamp(viewZoom * factor, 1, MaxViewZoom);
            var applied = zoom / viewZoom;
            SetView(zoom, cx - (cx - viewX) * applied, cy - (cy - viewY) * applied);
        }
        else if (!placementLocked) ResizeOverlay(FrameWidth * factor);
    }

    // Resizes the character's frame (and the room beside it) keeping the overlay's bottom-center anchored, except that
    // growing never pushes its top (and so the character's head) above the top of the screen's work area; it grows
    // downward from there instead.
    private void ResizeOverlay(double frameWidth)
    {
        frameWidth = Math.Clamp(frameWidth, MinFrameWidth, MaxFrameWidth);
        var width = OverlayWidth(frameWidth);
        var height = frameWidth * 4 / 3;
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
        if (!placementLocked) ResizeOverlay(Math.Min(DefaultFrameWidth, SystemParameters.WorkArea.Width));
    }

    private void ResetToDefault()
    {
        if (placementLocked) return;
        SetView(1, 0, 0);
        PlaceOnDesktop();
    }

    // ---------- locked placement ----------

    // Locked, the overlay can't be dragged, nudged, sent home or resized from here; only Martlet's window unlocks it.
    private bool placementLocked;

    private RendererPlacement Placement() =>
        new(placementLocked, Math.Round(Left, 2), Math.Round(Top, 2), Math.Round(FrameWidth, 2), Math.Round(Height, 2));

    private void LockPlacement(bool locked)
    {
        if (placementLocked == locked) return;
        placementLocked = locked;
        EndPan();
        ShowPlacementLock();
        ErrorLog.Info(locked ? "The character's position is locked." : "The character's position is unlocked.");
    }

    private void ShowPlacementLock()
    {
        viewport.PlacementLocked = placementLocked;
        viewport.Cursor = placementLocked ? Cursors.Arrow : Cursors.SizeAll;
        AutomationProperties.SetName(viewport, placementLocked
            ? "Character. Position locked; unlock it in Martlet. Mouse wheel zooms; Ctrl+drag or middle-drag pans when zoomed in; right-click for talk, settings, zoom and hide options."
            : "Character. Drag to move; mouse wheel zooms; Ctrl+drag or middle-drag pans when zoomed in; right-click for talk, settings, zoom, position, lock and hide options.");
    }

    /// <summary>Puts the overlay back where it was locked, at that size, when the character's frame would still be on a screen;
    /// otherwise it stays at its default spot. Either way it is locked again.</summary>
    private void RestorePlacement(RendererPlacement placement)
    {
        if (placement.IsValid)
        {
            var frame = Math.Clamp(placement.Width, MinFrameWidth, MaxFrameWidth);
            var height = Math.Clamp(placement.Height, MinFrameWidth, Math.Max(MinFrameWidth, SystemParameters.VirtualScreenHeight));
            var width = OverlayWidth(frame);
            var center = new Point(placement.Left + FrameOffset(width) + frame / 2, placement.Top + height / 2);
            if (OnAScreen(center))
            {
                Width = width;
                Height = height;
                Left = placement.Left;
                Top = placement.Top;
            }
            else ErrorLog.Warn("The character's locked position isn't on a screen now; it shows at its default spot, still locked.");
        }
        LockPlacement(true);
    }

    /// <summary>Whether a point (device-independent pixels, like Left and Top) lies on one of the screens.</summary>
    private bool OnAScreen(Point point)
    {
        if (PresentationSource.FromVisual(this)?.CompositionTarget is not { } target) return true;
        var device = target.TransformToDevice.Transform(point);
        return MonitorFromPoint(new CursorPoint { X = (int)Math.Round(device.X), Y = (int)Math.Round(device.Y) }, 0) != IntPtr.Zero;
    }

    /// <summary>UI Automation's move (Martlet's MCP ui_move): like a drag, it puts the overlay's top-left at a screen point in
    /// device pixels.</summary>
    private void MoveOverlayTo(Point screen)
    {
        if (placementLocked) throw new InvalidOperationException("The character's position is locked. Unlock it in Martlet.");
        if (PresentationSource.FromVisual(this)?.CompositionTarget is not { } target) return;
        var offset = target.TransformFromDevice.Transform(screen - viewport.PointToScreen(new Point(0, 0)));
        Left += offset.X;
        Top += offset.Y;
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

    /// <summary>The character frame's size, position and camera, how far the top of the head sits below its top edge, the
    /// overlay's full width including the room beside the frame, and whether its place is locked.</summary>
    private RendererView ViewState() => new(Math.Round(FrameWidth), Math.Round(Height),
        WorkAreaTop() is { } screenTop ? Math.Round(Top - screenTop) : null, Math.Round(viewZoom, 3),
        double.IsFinite(contentTop) ? Math.Round((1 - (contentTop * viewZoom + viewY)) / 2, 4) : null, Math.Round(Width), placementLocked);

    // The camera is in the frame's clip space; frame tells the renderer how much of its canvas width the frame spans.
    private void SendView()
    {
        try
        {
            browser.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(
                new { kind = "view", data = new { zoom = viewZoom, x = viewX, y = viewY, frame = FrameFraction } }, RendererProtocol.Json));
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
        SetView(viewZoom, viewX + (position.X - from.X) * 2 / (viewport.ActualWidth * FrameFraction),
            viewY - (position.Y - from.Y) * 2 / viewport.ActualHeight);
    }

    private void EndPan()
    {
        if (panFrom is null) return;
        panFrom = null;
        viewport.ReleaseMouseCapture();
    }

    // Martlet's own actions first (they go to Martlet over the request pipe), then the overlay's view, then Hide.
    private ContextMenu CreateCharacterMenu()
    {
        MenuItem Item(string header, string id, string? gesture, Action action)
        {
            var item = new MenuItem { Header = header, InputGestureText = gesture ?? "" };
            AutomationProperties.SetAutomationId(item, id);
            item.Click += (_, _) =>
            {
                CloseMenu(item);
                action();
            };
            return item;
        }
        // A menu opened through UI Automation stays open on its own (see CharacterViewport), so a choice closes it here.
        static void CloseMenu(MenuItem item)
        {
            if (item.Parent is ContextMenu { IsOpen: true } owner) owner.IsOpen = false;
        }
        var talk = Item("_Talk to Martlet", "CharacterTalk", null, () => Request("talk"));
        var open = Item("Open _Martlet", "CharacterOpenMartlet", null, () => Request("open"));
        var settings = Item("Character _settings", "CharacterSettings", null, () => Request("settings"));
        var zoomIn = Item("Zoom _in", "CharacterZoomIn", "+", () => Zoom(ZoomStep * ZoomStep, null));
        var zoomOut = Item("Zoom _out", "CharacterZoomOut", "-", () => Zoom(1 / (ZoomStep * ZoomStep), null));
        var reset = Item("_Reset zoom", "CharacterResetZoom", "0", ResetZoom);
        var home = Item("Reset _position and size", "CharacterResetPosition", "Home", ResetToDefault);
        // Locking goes through Martlet, which saves the place; a locked character only opens Martlet, where it unlocks.
        var placeLock = Item("_Lock position", "CharacterLockPosition", null, () => Request(placementLocked ? "settings" : "lock"));
        var onTop = new MenuItem { Header = "_Keep on top", IsCheckable = true, IsChecked = Topmost };
        AutomationProperties.SetAutomationId(onTop, "CharacterOnTop");
        onTop.Checked += (_, _) => Topmost = true;
        onTop.Unchecked += (_, _) => Topmost = false;
        onTop.Click += (_, _) => CloseMenu(onTop);
        var hide = Item("_Hide character", "CharacterHide", "Esc", () => Request("hide"));
        var menu = new ContextMenu
        {
            Items = { talk, open, settings, new Separator(), zoomIn, zoomOut, reset, home, placeLock, onTop, new Separator(), hide }
        };
        AutomationProperties.SetAutomationId(menu, "CharacterMenu");
        AutomationProperties.SetName(menu, "Character");
        menu.Closed += (_, _) => menu.StaysOpen = false;
        menu.Opened += (_, _) =>
        {
            // Until Martlet has loaded the character there is no one to ask; Hide still closes the overlay then.
            talk.IsEnabled = open.IsEnabled = settings.IsEnabled = placeLock.IsEnabled = CanRequest;
            zoomIn.IsEnabled = CanZoomIn;
            zoomOut.IsEnabled = CanZoomOut;
            reset.IsEnabled = CanResetZoom;
            home.IsEnabled = !placementLocked;
            placeLock.Header = placementLocked ? "Position locked: _unlock in Martlet..." : "_Lock position";
            placeLock.IsChecked = placementLocked;
            AutomationProperties.SetName(placeLock, placementLocked ? "Position locked: unlock in Martlet" : "Lock position");
            onTop.IsChecked = Topmost;
        };
        return menu;
    }

    private bool CanRequest => requests is { CanWrite: true } && activation != Guid.Empty && !closed && !failure.Failed;

    /// <summary>Asks Martlet to carry out a menu choice. Hiding goes through Martlet so it stops the character cleanly (and
    /// its buttons and tray say so); if Martlet can't be asked, the overlay just closes.</summary>
    private async void Request(string action)
    {
        if (!CanRequest)
        {
            if (action == "hide") Close();
            return;
        }
        try
        {
            await requesting.WaitAsync(lifetime.Token);
            try
            {
                await RendererProtocol.WriteAsync(requests!, RendererProtocol.Message("request", activation, new RendererRequest(action)),
                    lifetime.Token).WaitAsync(TimeSpan.FromSeconds(2), lifetime.Token);
            }
            finally { requesting.Release(); }
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException or OperationCanceledException or TimeoutException)
        {
            ErrorLog.Warn($"The character's '{action}' choice couldn't reach Martlet.", error);
            if (action == "hide" && !closed) Close();
        }
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
        if (!placementLocked) DragMove();
    }

    private const double BubbleRadius = 16, BubblePadX = 16, BubblePadY = 10, TailLength = 22, TailHalfBase = 9,
        BubbleMargin = 18, ScreenMargin = 6, SpeechWidth = 280, WidestSpeech = 640, SpeechWidthStep = 60;
    // Beyond the longest sentence Martlet speaks at once (1,536 UTF-8 bytes); only bounds an arbitrary request.
    private const int MaximumSpeechCharacters = 2000;

    private static TextBlock SpeechText() => new()
    {
        TextWrapping = TextWrapping.Wrap, MaxWidth = SpeechWidth, FontSize = 15, LineHeight = 21,
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"), TextAlignment = TextAlignment.Left
    };

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
        speechText.Text = say.Text.Length > MaximumSpeechCharacters ? say.Text[..MaximumSpeechCharacters] + "…" : say.Text;
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
        // Lay the open bubble out now so the reply says whether the text as shown really lies inside the bubble's body.
        speechCanvas.UpdateLayout();
        return speechShown = speechShown with
        {
            TextFits = speechText.ActualWidth <= speechShown.Width - BubblePadX * 2 + 1 &&
                speechText.ActualHeight <= speechShown.Height - BubblePadY * 2 + 1
        };
    }

    // By default the bubble follows the character's head through moves, zoom and pan: beside the head on whichever side has
    // room on its screen (left first, then right), else above it, then shifted by the configured offsets and kept on screen.
    // Static keeps it at one spot on the character's screen, with its tail still pointing toward the character.
    private void PlaceSpeech()
    {
        if (speechText.Text.Length == 0) return;
        var screen = WorkArea() ?? SystemParameters.WorkArea;
        screen.Inflate(-ScreenMargin, -ScreenMargin);
        var (width, height) = MeasureSpeech(screen);

        // The head in screen coordinates, followed through the camera: screen clip = fitted clip * zoom + pan. X is measured
        // from the frame's left edge and kept within the overlay (frame plus the room beside it).
        var top = double.IsFinite(contentTop) ? contentTop : 0.6;
        var frame = FrameWidth;
        double ScreenY(double clip) => Top + Math.Clamp(Height * (1 - (clip * viewZoom + viewY)) / 2, 0, Height);
        double ScreenX(double fraction) => Left + Math.Clamp(FrameOffset(Width) + fraction, 0, Width);
        var faceX = frame * (viewX + 1) / 2;
        var halfHead = frame * 0.11 * viewZoom;
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

    // The bubble's body size for the shown text, measured on a stand-in that is never inside the (closable) popup. It keeps a
    // comfortable reading width, widening only as far as a long speech needs to stay within half the screen's height.
    private (double Width, double Height) MeasureSpeech(Rect screen)
    {
        speechMeasure.Text = speechText.Text;
        var widest = Math.Max(SpeechWidth, Math.Min(WidestSpeech, screen.Width - BubblePadX * 2 - TailLength));
        var tallest = Math.Max(screen.Height / 2 - BubblePadY * 2, speechMeasure.LineHeight);
        for (var wrap = SpeechWidth; ; wrap = Math.Min(widest, wrap + SpeechWidthStep))
        {
            speechMeasure.MaxWidth = wrap;
            speechMeasure.Measure(new Size(wrap, double.PositiveInfinity));
            if (speechMeasure.DesiredSize.Height <= tallest || wrap >= widest) break;
        }
        speechText.MaxWidth = speechMeasure.MaxWidth;
        return (Math.Max(48, Math.Ceiling(speechMeasure.DesiredSize.Width) + BubblePadX * 2),
            Math.Ceiling(speechMeasure.DesiredSize.Height) + BubblePadY * 2);
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
            if (load.Placement is { Locked: true } locked) RestorePlacement(locked);
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
                        if (rendererError.ValueKind == JsonValueKind.String && rendererError.GetString() == "avatar.model_rejected" &&
                            document.RootElement.TryGetProperty("detail", out var detail) && detail.ValueKind == JsonValueKind.String)
                        {
                            modelRejection = new string((detail.GetString() ?? "").Take(300)
                                .Select(c => char.IsControl(c) ? ' ' : c).ToArray()).Trim();
                            ErrorLog.Warn($"The character model couldn't be shown: {modelRejection}");
                        }
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
                assets = assets.Assets.Select(a => a.Name).ToArray(),
                extras = load.Profile.Renderer == Martlet.Avatars.AvatarRenderer.Live2D ? LocalAvatarFiles.Extras(assets.Assets, assets.ModelFile) : null });
            await ReplyAsync("capabilities", loaded);
            SendView();
            StartLookTracking();
            while (!lifetime.IsCancellationRequested)
            {
                message = await RendererProtocol.ReadAsync(input, lifetime.Token);
                if (message.Activation != activation || message.Kind is not ("configure" or "reset" or "apply" or "stop" or "theme" or "mouth" or "motion" or "action" or "home" or "zoom" or "say" or "lock"))
                    throw new InvalidDataException("Renderer command is invalid.");
                if (message.Kind == "home")
                {
                    ResetToDefault();
                    await ReplyAsync("ok", new { });
                    continue;
                }
                if (message.Kind == "lock")
                {
                    LockPlacement(RendererProtocol.Data<RendererLock>(message).Locked);
                    await ReplyAsync("placement", Placement());
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
            System.Runtime.InteropServices.COMException or TimeoutException or JsonException or InvalidDataException or
            Martlet.Core.Contracts.ContractException or OperationCanceledException)
        {
            try
            {
                if (activation != Guid.Empty)
                    await RendererProtocol.WriteAsync(output, RendererProtocol.Message("error", activation, modelRejection is { Length: > 0 } rejected
                        ? new { code = "avatar.model_rejected", message = $"This model can't be shown: {rejected}" }
                        : new { code = "avatar.renderer_unavailable", message = "Renderer/runtime/resource operation failed. Inspect local prerequisites and retry explicitly." }),
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
            // The face sits at 30% height when unzoomed; follow it through the camera zoom, within the character's frame.
            var faceX = (viewX + 1) / 2;
            var faceY = (1 - (0.4 * viewZoom + viewY)) / 2;
            try
            {
                face = viewport.PointToScreen(new Point(FrameOffset(viewport.ActualWidth) + viewport.ActualWidth * FrameFraction * faceX,
                    viewport.ActualHeight * faceY));
            }
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
    private static extern IntPtr MonitorFromPoint(CursorPoint point, uint flags);

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
