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

internal sealed partial class RendererWindow : Window
{
    private readonly Stream input, output;
    // Menu choices Martlet itself carries out (hide, open, talk, settings, lock, mute and unmute, where the eyes go); null when
    // started without it (tests).
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
    // The bubble's soft halo takes the palette's Glow, like the halos behind Martlet's mascot; Windows' high contrast drops it.
    private readonly System.Windows.Media.Effects.DropShadowEffect speechHalo = new()
    {
        BlurRadius = 18, ShadowDepth = 2, Direction = 270, Opacity = 0.9
    };
    private readonly Canvas speechCanvas = new();
    private readonly ScaleTransform speechPop = new(1, 1);
    private RendererSay speechPlacement = new(null);
    private RendererBubble speechShown = new("hidden", 0, 0, 0, 0);
    private ResourceDictionary? palette;
    private bool darkTheme;
    private IReadOnlyDictionary<string, string>? themeColors;
    private bool highContrast;
    private bool closed;
    private TaskCompletionSource<JsonElement>? response;
    private Guid activation;
    // A still renderer draws Martlet's touch zones picture: never on screen, never animated (the model's rest pose).
    private readonly bool still;
    private readonly RendererFailureLatch failure = new();
    // Why the browser couldn't load the selected model (a bounded Live2D/VRM reason), reported back to Martlet.
    private volatile string? modelRejection;
    private string? userData;

    internal RendererWindow(Stream input, Stream output, Stream? requests = null, bool still = false)
    {
        this.input = input;
        this.output = output;
        this.requests = requests;
        this.still = still;
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
        if (still)
        {
            // Never seen and never touched: fully transparent (clicks pass through), off every screen and out of the taskbar.
            Title = "Martlet character picture";
            Topmost = false;
            ShowInTaskbar = false;
            Opacity = 0;
            IsHitTestVisible = false;
            Left = SystemParameters.VirtualScreenLeft - Width - 100;
            Top = SystemParameters.VirtualScreenTop - Height - 100;
        }

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
        // A left press that comes up where it went down, soon enough, is a tap on the character (dragging, locked or panning).
        viewport.MouseMove += (_, e) => TrackPress(e.GetPosition(viewport));
        viewport.MouseLeftButtonUp += (_, e) => EndPress(e.GetPosition(viewport));
        viewport.TouchAt = TouchAt;
        // A press and drag on a locked character can't move it: it strokes the character instead.
        viewport.MouseMove += (_, e) => TrackStroke(e.GetPosition(viewport), Environment.TickCount64);
        viewport.MouseLeftButtonUp += (_, e) => EndStroke(e.GetPosition(viewport), Environment.TickCount64);
        viewport.StrokeAlong = StrokeAlong;
        viewport.ReadFace = ReadFace;
        viewport.LostMouseCapture += (_, _) =>
        {
            if (stroke is { } lost && !strokeReleasing) EndStroke(lost.Last, Environment.TickCount64);
        };
        viewport.LostMouseCapture += (_, _) =>
        {
            if (panFrom is null) return;
            panFrom = null;
            FramingChanged();
        };
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
            // A locked place ignores the arrow keys and Home until it is unlocked. In the camera view they frame the character.
            switch (e.Key)
            {
                case Key.Escape: Request("hide"); break;
                case Key.Left when camera is not null: Nudge(-step, 0); break;
                case Key.Right when camera is not null: Nudge(step, 0); break;
                case Key.Up when camera is not null: Nudge(0, -step); break;
                case Key.Down when camera is not null: Nudge(0, step); break;
                case Key.Home when camera is not null: ResetZoom(); break;
                case Key.Left when !placementLocked: Physically("moved", () => Left -= step); PlacementChanged(); break;
                case Key.Right when !placementLocked: Physically("moved", () => Left += step); PlacementChanged(); break;
                case Key.Up when !placementLocked: Physically("moved", () => Top -= step); PlacementChanged(); break;
                case Key.Down when !placementLocked: Physically("moved", () => Top += step); PlacementChanged(); break;
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

    internal void ApplyOverlayTheme(bool dark, IReadOnlyDictionary<string, string>? colors = null)
    {
        darkTheme = dark;
        themeColors = colors;
        highContrast = SystemParameters.HighContrast;
        if (palette is not null) Resources.MergedDictionaries.Remove(palette);
        palette = AppearancePalette.Create(dark, highContrast, colors);
        Resources.MergedDictionaries.Add(palette);
        speechHalo.Color = ((SolidColorBrush)palette["GlowBrush"]).Color;
        speechShape.Effect = highContrast ? null : speechHalo;
    }

    private void SystemAppearanceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SystemParameters.HighContrast) &&
            !closed && !Dispatcher.HasShutdownStarted && !Dispatcher.HasShutdownFinished)
            Dispatcher.InvokeAsync(() => { if (!closed) ApplyOverlayTheme(darkTheme, themeColors); });
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

    private bool CanZoomIn => (!FixedSize && FrameWidth < MaxFrameWidth - 0.5) || viewZoom < MaxViewZoom;
    private bool CanZoomOut => camera is not null ? viewZoom > RendererCamera.MinimumZoom + 1e-9
        : viewZoom > 1 || (!FixedSize && FrameWidth > MinFrameWidth + 0.5);
    private bool IsDefaultZoom => viewZoom == 1 && Math.Abs(FrameWidth - Math.Min(DefaultFrameWidth, SystemParameters.WorkArea.Width)) < 0.5;
    private bool CanResetZoom => camera is not null ? viewZoom != 1 || viewX != 0 || viewY != 0
        : FixedSize ? viewZoom != 1 : !IsDefaultZoom;

    /// <summary>
    /// Zooms in by growing the overlay up to its screen-height limit, then by zooming the camera into the
    /// character (toward <paramref name="anchor"/>, or the face). Zooming out reverses that order. While the place is locked
    /// the overlay keeps its size and only the camera zooms. In the camera view the character zooms in or out (smaller than
    /// it fits, too) around the cursor, or else its own face, which then stays where it is.
    /// </summary>
    private void Zoom(double factor, Point? anchor)
    {
        if (camera is null)
        {
            var at = anchor ?? new Point(viewport.ActualWidth / 2, viewport.ActualHeight * 0.3);
            Physically("zoomed", () => ZoomView(factor, anchor), Fraction(at));
            return;
        }
        ZoomView(factor, anchor);
    }

    private void ZoomView(double factor, Point? anchor)
    {
        if (camera is not null)
        {
            var zoom = Math.Clamp(viewZoom * factor, RendererCamera.MinimumZoom, RendererCamera.MaximumZoom);
            var applied = zoom / viewZoom;
            double cx = viewX, cy = 0.4 * viewZoom + viewY;
            if (anchor is { } at && viewport.ActualWidth > 0 && viewport.ActualHeight > 0)
            {
                cx = (at.X - FrameOffset(viewport.ActualWidth)) / (viewport.ActualWidth * FrameFraction) * 2 - 1;
                cy = 1 - at.Y / viewport.ActualHeight * 2;
            }
            SetView(zoom, cx - (cx - viewX) * applied, cy - (cy - viewY) * applied);
            FramingChanged();
        }
        else if (factor > 1 && !FixedSize && FrameWidth < MaxFrameWidth - 0.5) ResizeOverlay(FrameWidth * factor);
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
        else if (!FixedSize) ResizeOverlay(FrameWidth * factor);
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
        PlacementChanged();
    }

    private void ResetZoom()
    {
        if (camera is null && CanResetZoom)
        {
            Physically("zoom_reset", ResetZoomView);
            return;
        }
        ResetZoomView();
    }

    private void ResetZoomView()
    {
        SetView(1, 0, 0);
        if (camera is not null) FramingChanged();
        else if (!FixedSize) ResizeOverlay(Math.Min(DefaultFrameWidth, SystemParameters.WorkArea.Width));
    }

    /// <summary>Moves the character within its view by screen pixels (+x right, +y down): anywhere in the camera view, or
    /// within its frame while zoomed in on the overlay.</summary>
    private void Nudge(double dx, double dy)
    {
        if (viewport.ActualWidth <= 0 || viewport.ActualHeight <= 0) return;
        if (camera is null && viewZoom > 1)
        {
            Physically("panned", () => SetView(viewZoom, viewX + dx * 2 / (viewport.ActualWidth * FrameFraction), viewY - dy * 2 / viewport.ActualHeight));
            return;
        }
        SetView(viewZoom, viewX + dx * 2 / (viewport.ActualWidth * FrameFraction), viewY - dy * 2 / viewport.ActualHeight);
        FramingChanged();
    }

    /// <summary>Back to the default spot, size and zoom on the main screen. The overlay's own Home and menu leave a locked
    /// place alone; Martlet's Reset position (<paramref name="evenLocked"/>) moves it there too, keeping it locked.</summary>
    private void ResetToDefault(bool evenLocked = false)
    {
        if (placementLocked && !evenLocked || camera is not null) return;
        Physically("home", () =>
        {
            SetView(1, 0, 0);
            PlaceOnDesktop();
        });
        PlacementChanged();
    }

    // ---------- locked placement ----------

    // Locked, the overlay can't be dragged, nudged, sent home or resized until it is unlocked.
    private bool placementLocked;
    // The camera view (RendererCamera): the overlay as it was before, while the character shows in its 16:9 window.
    private (double Left, double Top, double Width, double Height, bool Topmost)? camera;
    // The overlay's own zoom and pan, kept while the camera view frames the character its own way.
    private (double Zoom, double X, double Y) overlayView = (1, 0, 0);
    private bool FixedSize => placementLocked || camera is not null;

    internal void UseCamera(RendererCamera request)
    {
        if (request.On)
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(request.Background ?? "", "^#[0-9A-Fa-f]{6}$"))
                throw new InvalidDataException("The camera background is invalid.");
            if (!double.IsFinite(request.Zoom) || !double.IsFinite(request.X) || !double.IsFinite(request.Y))
                throw new InvalidDataException("The camera framing is invalid.");
            var color = (Color)ColorConverter.ConvertFromString(request.Background);
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            var opening = camera is null;
            if (opening)
            {
                EndPan();
                camera = (Left, Top, Width, Height, Topmost);
                overlayView = (viewZoom, viewX, viewY);
            }
            Background = CameraPicture(request.Picture) ?? (Brush)brush;
            viewport.Background = request.Picture is null ? brush : Brushes.Transparent;
            if (opening)
            {
                var area = SystemParameters.WorkArea;
                Width = Math.Min(960, area.Width);
                Height = Width * 9 / 16;
                Left = area.Left + (area.Width - Width) / 2;
                Top = area.Top + (area.Height - Height) / 2;
                Topmost = false;
                ShowInTaskbar = true;
                Title = "Martlet camera";
                const double farthest = RendererCamera.Farthest;
                SetView(request.Zoom, Math.Clamp(request.X, -farthest, farthest) * 2 / FrameFraction, Math.Clamp(request.Y, -farthest, farthest) * 2);
            }
        }
        else if (camera is { } before)
        {
            EndPan();
            camera = null;
            Background = Brushes.Transparent;
            viewport.Background = Brushes.Transparent;
            (Left, Top, Width, Height, Topmost) = before;
            ShowInTaskbar = false;
            Title = "Martlet character overlay";
            SetView(overlayView.Zoom, overlayView.X, overlayView.Y);
        }
        ShowPlacementLock();
        SendView();
    }

    // The camera view's picture, filling the window; null (the color shows) when there's none or it can't be read.
    private static ImageBrush? CameraPicture(string? path)
    {
        if (path is null) return null;
        try
        {
            if (!Path.IsPathFullyQualified(path)) throw new InvalidDataException("The camera picture's path isn't a full path.");
            var file = new FileInfo(path);
            if (!file.Exists || file.Length is 0 or > 24 * 1024 * 1024) throw new InvalidDataException("The camera picture is missing or too large.");
            var bytes = File.ReadAllBytes(path);
            int width;
            using (var probe = new MemoryStream(bytes, writable: false))
                width = System.Windows.Media.Imaging.BitmapDecoder.Create(probe, System.Windows.Media.Imaging.BitmapCreateOptions.IgnoreColorProfile |
                    System.Windows.Media.Imaging.BitmapCreateOptions.DelayCreation, System.Windows.Media.Imaging.BitmapCacheOption.None).Frames[0].PixelWidth;
            var image = new System.Windows.Media.Imaging.BitmapImage();
            using (var stream = new MemoryStream(bytes, writable: false))
            {
                image.BeginInit();
                image.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                image.CreateOptions = System.Windows.Media.Imaging.BitmapCreateOptions.IgnoreColorProfile;
                if (width > 1920) image.DecodePixelWidth = 1920;
                image.StreamSource = stream;
                image.EndInit();
            }
            image.Freeze();
            var brush = new ImageBrush(image) { Stretch = Stretch.UniformToFill, AlignmentX = AlignmentX.Center, AlignmentY = AlignmentY.Center };
            brush.Freeze();
            ErrorLog.Info($"Camera view: on a {image.PixelWidth}x{image.PixelHeight} picture.");
            return brush;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException or InvalidDataException or
            FileFormatException or ArgumentException or InvalidOperationException)
        {
            ErrorLog.Warn($"Camera view: couldn't show its picture ({error.Message}), so it shows its color.");
            return null;
        }
    }
    // Martlet's voice is muted (its replies aren't spoken); Martlet says so on load and whenever it changes.
    private bool voiceMuted;
    private MenuItem? muteItem;

    private void UseVoice(bool muted)
    {
        if (voiceMuted == muted) return;
        voiceMuted = muted;
        ShowVoice();
        ErrorLog.Info(muted ? "Martlet's voice is muted." : "Martlet's voice is unmuted.");
    }

    // The menu item carries the state: Mute voice while Martlet speaks, Unmute voice while it is muted.
    private void ShowVoice()
    {
        if (muteItem is null) return;
        muteItem.Header = voiceMuted ? "Unmute _voice" : "Mute _voice";
        AutomationProperties.SetName(muteItem, voiceMuted ? "Unmute voice" : "Mute voice");
    }

    private RendererPlacement Placement()
    {
        var screen = CurrentScreen();
        return new(placementLocked, Math.Round(Left, 2), Math.Round(Top, 2), Math.Round(FrameWidth, 2), Math.Round(Height, 2),
            screen?.Name, screen is { } on ? Math.Round(Left - on.Work.Left, 2) : null, screen is { } at ? Math.Round(Top - at.Work.Top, 2) : null);
    }

    // Moves and resizes settle for a moment before Martlet is told, so a drag or a spin of the wheel saves once.
    private System.Windows.Threading.DispatcherTimer? placedTimer;

    /// <summary>The user moved or resized the character (or sent it home): once it settles, Martlet asks where it is and saves
    /// that on this PC, so it shows there again after Hide/Show, a restart, a shutdown or an update.</summary>
    private void PlacementChanged()
    {
        if (!CanRequest || camera is not null) return;
        if (placedTimer is null)
        {
            placedTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
            placedTimer.Tick += (_, _) =>
            {
                placedTimer.Stop();
                if (!closed) Request("placed");
            };
            Closed += (_, _) => placedTimer.Stop();
        }
        placedTimer.Stop();
        placedTimer.Start();
    }

    private System.Windows.Threading.DispatcherTimer? framedTimer;

    /// <summary>The user moved or zoomed the character within the camera view: once it settles, Martlet reads the view and
    /// saves the framing, so the camera view opens framed the same way next time.</summary>
    private void FramingChanged()
    {
        if (!CanRequest || camera is null) return;
        if (framedTimer is null)
        {
            framedTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
            framedTimer.Tick += (_, _) =>
            {
                framedTimer.Stop();
                if (!closed && camera is not null) Request("framed");
            };
            Closed += (_, _) => framedTimer.Stop();
        }
        framedTimer.Stop();
        framedTimer.Start();
    }

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
        viewport.Cursor = placementLocked && camera is null ? Cursors.Arrow : Cursors.SizeAll;
        AutomationProperties.SetName(viewport, camera is not null
            ? "Martlet camera. Drag to move the character in the view; mouse wheel zooms it in or out; arrow keys nudge it; Home or 0 resets the framing; Shift+drag moves the window; right-click for more options."
            : placementLocked
            ? "Character. Position locked; unlock it in Martlet. Mouse wheel zooms; Ctrl+drag or middle-drag pans when zoomed in; right-click for talk, mute, settings, zoom and hide options."
            : "Character. Drag to move; mouse wheel zooms; Ctrl+drag or middle-drag pans when zoomed in; right-click for talk, mute, settings, zoom, position, lock and hide options.");
    }

    /// <summary>Puts the overlay back where it was saved, at that size, and locks it again when it was locked. On the same
    /// monitor (by name) when it is still connected, at the same spot on it, with the character's middle kept on that monitor's
    /// work area; otherwise where it was when that spot is still on a screen; otherwise at its default spot.</summary>
    private void RestorePlacement(RendererPlacement placement)
    {
        if (placement.IsValid)
        {
            var frame = Math.Clamp(placement.Width, MinFrameWidth, MaxFrameWidth);
            var height = Math.Clamp(placement.Height, MinFrameWidth, Math.Max(MinFrameWidth, SystemParameters.VirtualScreenHeight));
            var width = OverlayWidth(frame);
            Point? at = null;
            if (placement is { Screen: { } name, ScreenLeft: { } dx, ScreenTop: { } dy } && ScreenNamed(name) is { } screen)
            {
                var work = screen.Work;
                var middle = new Point(work.Left + dx + FrameOffset(width) + frame / 2, work.Top + dy + height / 2);
                var kept = new Point(Math.Clamp(middle.X, work.Left, Math.Max(work.Left, work.Right)),
                    Math.Clamp(middle.Y, work.Top, Math.Max(work.Top, work.Bottom)));
                at = new(work.Left + dx + (kept.X - middle.X), work.Top + dy + (kept.Y - middle.Y));
                ErrorLog.Info($"The character is back where it was left on {ScreenLabel(name)}.");
            }
            else if (OnAScreen(new Point(placement.Left + FrameOffset(width) + frame / 2, placement.Top + height / 2)))
            {
                at = new(placement.Left, placement.Top);
                ErrorLog.Info(placement.Screen is { } gone
                    ? $"The character's screen ({ScreenLabel(gone)}) isn't connected; it shows where it was on the desktop."
                    : "The character is back where it was left.");
            }
            else ErrorLog.Warn("The character's saved position isn't on a screen now; it shows at its default spot.");
            if (at is { } place)
            {
                Width = width;
                Height = height;
                Left = place.X;
                Top = place.Y;
            }
        }
        LockPlacement(placement.Locked);
    }

    /// <summary>A monitor's device name without Windows' <c>\\.\</c> prefix, such as DISPLAY2.</summary>
    internal static string ScreenLabel(string device) => device.StartsWith(@"\\.\", StringComparison.Ordinal) ? device[4..] : device;

    private readonly record struct ScreenArea(string Name, Rect Work);

    /// <summary>The monitor the character's frame is mostly on (its middle), with its work area in this window's coordinates.</summary>
    private ScreenArea? CurrentScreen()
    {
        if (PresentationSource.FromVisual(this)?.CompositionTarget is not { } target) return null;
        var frame = Frame;
        var device = target.TransformToDevice.Transform(new Point(frame.Left + frame.Width / 2, frame.Top + frame.Height / 2));
        return Describe(MonitorFromPoint(new CursorPoint { X = (int)Math.Round(device.X), Y = (int)Math.Round(device.Y) }, 2));
    }

    /// <summary>The connected monitor with this device name, or null.</summary>
    private ScreenArea? ScreenNamed(string name)
    {
        var monitors = new List<IntPtr>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (monitor, _, _, _) => { monitors.Add(monitor); return true; }, IntPtr.Zero);
        foreach (var monitor in monitors)
            if (Describe(monitor) is { } screen && string.Equals(screen.Name, name, StringComparison.OrdinalIgnoreCase)) return screen;
        return null;
    }

    private ScreenArea? Describe(IntPtr monitor)
    {
        if (monitor == IntPtr.Zero || PresentationSource.FromVisual(this)?.CompositionTarget is not { } target) return null;
        var info = new MonitorInfoEx { Size = System.Runtime.InteropServices.Marshal.SizeOf<MonitorInfoEx>() };
        if (!GetMonitorInfoEx(monitor, ref info) || string.IsNullOrEmpty(info.Device)) return null;
        return new(info.Device, new Rect(target.TransformFromDevice.Transform(new Point(info.Work.Left, info.Work.Top)),
            target.TransformFromDevice.Transform(new Point(info.Work.Right, info.Work.Bottom))));
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
        Physically("moved", () =>
        {
            Left += offset.X;
            Top += offset.Y;
        });
        PlacementChanged();
    }

    // Pan is clamped so the zoomed view never leaves the character's fitted frame and the top of the head stays in
    // view: it never rises above the top edge (less a small margin), or above where it sits unzoomed if already cut off.
    // The camera view frames the character freely: smaller than it fits too, and with its middle anywhere in the view or a
    // little past its edges, never wholly out of sight.
    private void SetView(double zoom, double x, double y)
    {
        if (camera is not null)
        {
            viewZoom = Math.Clamp(zoom, RendererCamera.MinimumZoom, RendererCamera.MaximumZoom);
            var reachX = 1 / FrameFraction + viewZoom * 0.9;
            var reachY = 1 + viewZoom * 0.9;
            viewX = Math.Clamp(x, -reachX, reachX);
            viewY = Math.Clamp(y, -reachY, reachY);
            SendView();
            PlaceSpeech();
            return;
        }
        zoom = Math.Max(1, zoom);
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
    /// overlay's full width including the room beside the frame, whether its place is locked, where the character's middle
    /// sits in the view and whether this is the camera view.</summary>
    internal RendererView ViewState() => new(Math.Round(FrameWidth), Math.Round(Height),
        WorkAreaTop() is { } screenTop ? Math.Round(Top - screenTop) : null, Math.Round(viewZoom, 3),
        double.IsFinite(contentTop) ? Math.Round((1 - (contentTop * viewZoom + viewY)) / 2, 4) : null, Math.Round(Width), placementLocked,
        Math.Round(viewX * FrameFraction / 2, 4), Math.Round(viewY / 2, 4), camera is not null);

    // The camera is in the frame's clip space; frame tells the renderer how much of its canvas width the frame spans.
    private void SendView() => PostView(viewZoom, viewX, viewY);

    private void PostView(double zoom, double x, double y)
    {
        try
        {
            browser.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(
                new { kind = "view", data = new { zoom, x, y, frame = FrameFraction } }, RendererProtocol.Json));
        }
        catch (Exception error) when (error is InvalidOperationException or System.Runtime.InteropServices.COMException) { }
    }

    private void StartPan(MouseButtonEventArgs e)
    {
        if (viewZoom <= 1 && camera is null) return;
        e.Handled = true;
        panFrom = e.GetPosition(viewport);
        viewport.CaptureMouse();
    }

    private void Pan(Point position)
    {
        if (panFrom is not { } from || viewport.ActualWidth <= 0 || viewport.ActualHeight <= 0) return;
        panFrom = position;
        void Step() => SetView(viewZoom, viewX + (position.X - from.X) * 2 / (viewport.ActualWidth * FrameFraction),
            viewY - (position.Y - from.Y) * 2 / viewport.ActualHeight);
        if (camera is null) Physically("panned", Step);
        else Step();
    }

    private void EndPan()
    {
        if (panFrom is null) return;
        panFrom = null;
        viewport.ReleaseMouseCapture();
        FramingChanged();
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
        // A menu opened through UI Automation stays open on its own (see CharacterViewport), so a choice closes it here (from a
        // submenu too).
        static void CloseMenu(MenuItem item)
        {
            DependencyObject? at = item;
            while (at is MenuItem { Parent: var parent }) at = parent;
            if (at is ContextMenu { IsOpen: true } owner) owner.IsOpen = false;
        }
        var talk = Item("_Talk to Martlet", "CharacterTalk", null, () => Request("talk"));
        // Muting goes through Martlet, which saves it (Speak Martlet's replies aloud) and silences a reply it is speaking.
        var mute = muteItem = Item("Mute _voice", "CharacterMuteVoice", null, () => Request(voiceMuted ? "unmute" : "mute"));
        var open = Item("Open _Martlet", "CharacterOpenMartlet", null, () => Request("open"));
        var settings = Item("Character _settings", "CharacterSettings", null, () => Request("settings"));
        // Lingering emotes (glasses, a blush...) stay until a reply turns them off; this turns them all off at once.
        var clearEmotes = Item("_Clear emotes", "CharacterClearEmotes", null, () => Request("clear"));
        // Where the eyes usually go, and whether the character may change that in its replies. Both go through Martlet, which
        // saves them and sends them back (gaze), so the checks show what applies.
        var eyes = new MenuItem { Header = "_Eyes" };
        AutomationProperties.SetAutomationId(eyes, "CharacterEyes");
        var eyeLabels = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [RendererGaze.Personality] = "As the _personality decides", [CharacterGaze.Word(GazeMode.Mouse)] = "Follow your _mouse",
            [CharacterGaze.Word(GazeMode.Near)] = "Follow your mouse when it's _near", [CharacterGaze.Word(GazeMode.Ahead)] = "Look straight _ahead",
            [CharacterGaze.Word(GazeMode.Window)] = "Watch the _window you're using"
        };
        var eyeChoices = RendererGaze.Choices.Select(choice =>
        {
            var item = Item(eyeLabels[choice], "CharacterEyes-" + choice, null, () => Request(RendererRequest.LookPrefix + choice));
            item.IsCheckable = true;
            eyes.Items.Add(item);
            return (Choice: choice, Item: item);
        }).ToArray();
        var eyesFree = Item("_Let the character change it", "CharacterEyes-free", null,
            () => Request(gazeFree ? RendererRequest.FreeOff : RendererRequest.FreeOn));
        eyesFree.IsCheckable = true;
        eyes.Items.Add(new Separator());
        eyes.Items.Add(eyesFree);
        // The checks show what Martlet last said applies (gaze), also when its answer comes while the menu is open.
        void ShowEyes()
        {
            foreach (var (choice, item) in eyeChoices) item.IsChecked = choice == gazeChoice;
            eyesFree.IsChecked = gazeFree;
        }
        eyes.SubmenuOpened += (_, _) => ShowEyes();
        var zoomIn = Item("Zoom _in", "CharacterZoomIn", "+", () => Zoom(ZoomStep * ZoomStep, null));
        var zoomOut = Item("Zoom _out", "CharacterZoomOut", "-", () => Zoom(1 / (ZoomStep * ZoomStep), null));
        var reset = Item("_Reset zoom", "CharacterResetZoom", "0", ResetZoom);
        var home = Item("Reset _position and size", "CharacterResetPosition", "Home", () => ResetToDefault());
        // Locking and unlocking go through Martlet, which saves the place.
        var placeLock = Item("_Lock position", "CharacterLockPosition", null, () => Request(placementLocked ? "unlock" : "lock"));
        var onTop = new MenuItem { Header = "_Keep on top", IsCheckable = true, IsChecked = Topmost };
        AutomationProperties.SetAutomationId(onTop, "CharacterOnTop");
        onTop.Checked += (_, _) => Topmost = true;
        onTop.Unchecked += (_, _) => Topmost = false;
        onTop.Click += (_, _) => CloseMenu(onTop);
        var hide = Item("_Hide character", "CharacterHide", "Esc", () => Request("hide"));
        var menu = new ContextMenu
        {
            Items = { talk, mute, open, settings, clearEmotes, eyes, new Separator(), zoomIn, zoomOut, reset, home, placeLock, onTop, new Separator(), hide }
        };
        AutomationProperties.SetAutomationId(menu, "CharacterMenu");
        AutomationProperties.SetName(menu, "Character");
        menu.Closed += (_, _) => menu.StaysOpen = false;
        menu.Opened += (_, _) =>
        {
            // Until Martlet has loaded the character there is no one to ask; Hide still closes the overlay then.
            talk.IsEnabled = mute.IsEnabled = open.IsEnabled = settings.IsEnabled = clearEmotes.IsEnabled = placeLock.IsEnabled = eyes.IsEnabled = CanRequest;
            ShowVoice();
            ShowEyes();
            zoomIn.IsEnabled = CanZoomIn;
            zoomOut.IsEnabled = CanZoomOut;
            reset.IsEnabled = CanResetZoom;
            reset.Header = camera is not null ? "_Reset framing" : "_Reset zoom";
            home.IsEnabled = !placementLocked && camera is null;
            placeLock.Header = placementLocked ? "_Unlock position" : "_Lock position";
            placeLock.IsChecked = placementLocked;
            AutomationProperties.SetName(placeLock, placementLocked ? "Unlock position" : "Lock position");
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
        press = (e.GetPosition(viewport), Environment.TickCount64);
        // In the camera view a drag moves the character within it; Shift+drag moves the window itself.
        if (camera is not null)
        {
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            {
                e.Handled = true;
                DragWindow();
            }
            else StartPan(e);
            return;
        }
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && viewZoom > 1)
        {
            StartPan(e);
            return;
        }
        e.Handled = true;
        if (!placementLocked) DragWindow();
        else BeginStroke(e.GetPosition(viewport), Environment.TickCount64);
    }

    // ---------- tapping the character ----------

    // Where (in the viewport) and when the left button went down on the character; cleared once it moves too far to be a tap.
    private (Point At, long Since)? press;

    private int touchId;
    private (int Id, double X, double Y, int Held)? touchPending;

    private static bool Moved(Point from, Point to) =>
        Math.Abs(to.X - from.X) >= SystemParameters.MinimumHorizontalDragDistance ||
        Math.Abs(to.Y - from.Y) >= SystemParameters.MinimumVerticalDragDistance;

    /// <summary>Drags the overlay (Windows' own move loop, which returns on release). A press that moved it less than the system
    /// drag distance is a tap instead: the jitter is undone and the place isn't saved.</summary>
    private void DragWindow()
    {
        double left = Left, top = Top;
        var before = OverlayNow();
        var held = press;
        press = null;
        DragMove();
        if (held is { } h && !Moved(new Point(left, top), new Point(Left, Top)))
        {
            if (Left != left || Top != top) (Left, Top) = (left, top);
            // Windows' move loop can end before the button comes up; then the release (or a move) decides.
            if (PrimaryButtonDown()) press = h;
            else Tap(h.At, Environment.TickCount64 - h.Since);
            return;
        }
        NoteOverlay("moved", before);
        PlacementChanged();
    }

    private static bool PrimaryButtonDown() =>
        (GetAsyncKeyState(GetSystemMetrics(23) != 0 ? 0x02 : 0x01) & 0x8000) != 0;

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);

    private void TrackPress(Point position)
    {
        if (press is { } held && Moved(held.At, position)) press = null;
    }

    /// <summary>The left button came up at <paramref name="at"/>: a short press that didn't move is a tap there.</summary>
    private void EndPress(Point at)
    {
        if (press is not { } held) return;
        press = null;
        if (!Moved(held.At, at)) Tap(held.At, Environment.TickCount64 - held.Since);
    }

    // A press that came up where it went down: a tap, or a hold when it lasted CharacterTouch.HoldMilliseconds or more. A press
    // longer than CharacterTouch.MaximumHeldMilliseconds is let go.
    private void Tap(Point at, long heldMilliseconds)
    {
        if (viewport.ActualWidth <= 0 || viewport.ActualHeight <= 0) return;
        if (heldMilliseconds is < 0 or > CharacterTouch.MaximumHeldMilliseconds) return;
        TouchAt(at.X / viewport.ActualWidth, at.Y / viewport.ActualHeight, (int)heldMilliseconds);
    }

    /// <summary>Asks the page what of the character is at <paramref name="x"/>, <paramref name="y"/> (fractions of the page, +y
    /// down); its answer arrives unprompted (see <see cref="Touched"/>). A tap through UI Automation (Martlet's MCP
    /// character_touch) comes here too.</summary>
    private void TouchAt(double x, double y, int held)
    {
        if (browser.CoreWebView2 is null || failure.Failed || closed) return;
        x = Math.Round(Math.Clamp(x, 0, 1), 4);
        y = Math.Round(Math.Clamp(y, 0, 1), 4);
        var id = ++touchId;
        touchPending = (id, x, y, Math.Clamp(held, 0, CharacterTouch.MaximumHeldMilliseconds));
        try
        {
            browser.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { kind = "touch", data = new { id, x, y } },
                RendererProtocol.Json));
        }
        catch (Exception error) when (error is InvalidOperationException or System.Runtime.InteropServices.COMException) { }
    }

    private int faceId;
    private int? facePending;

    /// <summary>Asks the page where Martlet draws over the face now (Martlet's MCP character_face, through UI Automation); its
    /// answer arrives unprompted (see <see cref="FaceAnswered"/>). Reading it changes nothing on the character.</summary>
    private void ReadFace()
    {
        if (browser.CoreWebView2 is null || failure.Failed || closed) return;
        var id = ++faceId;
        facePending = id;
        try
        {
            browser.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { kind = "face", data = new { id } }, RendererProtocol.Json));
        }
        catch (Exception error) when (error is InvalidOperationException or System.Runtime.InteropServices.COMException) { }
    }

    /// <summary>The page's reading of the face, remembered for UI Automation; a stale or malformed one is ignored and never
    /// fails the renderer.</summary>
    private void FaceAnswered(JsonElement answer)
    {
        if (facePending is not { } pending || answer.ValueKind != JsonValueKind.Object || !answer.TryGetProperty("id", out var id) ||
            id.ValueKind != JsonValueKind.Number || !id.TryGetInt32(out var number) || number != pending) return;
        facePending = null;
        viewport.LastFace = CharacterFaceReading.From(answer, number);
    }

    /// <summary>The page's hit test for the last tap: remembered for UI Automation and, when it found the character, sent to
    /// Martlet on the request pipe. A malformed or stale answer is ignored; it never fails the renderer.</summary>
    private void Touched(JsonElement answer)
    {
        if (touchPending is not { } pending || answer.ValueKind != JsonValueKind.Object ||
            !answer.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.Number || !id.TryGetInt32(out var number) ||
            number != pending.Id) return;
        touchPending = null;
        var (hit, touch) = ReadTouch(answer, pending.X, pending.Y);
        touch = touch with { HeldMilliseconds = pending.Held };
        viewport.LastTouch = JsonSerializer.Serialize(new
        {
            n = pending.Id, x = touch.X, y = touch.Y, hit, zone = hit ? touch.CoarseZone : null, touch.HitAreas, touch.Drawables,
            touch.Bone, touch.Node, touch.Hair, touch.Mesh, touch.Material, held = touch.HeldMilliseconds
        }, RendererProtocol.Json);
        if (hit) SendTouch(touch);
    }

    /// <summary>One answer of the page's hit test at <paramref name="x"/>, <paramref name="y"/>: whether it found the character
    /// and what is there, and where that point sits with the character framed whole. Malformed names are dropped.</summary>
    private (bool Hit, CharacterTouch Touch) ReadTouch(JsonElement answer, double x, double y)
    {
        static string? Name(JsonElement owner, string property) =>
            owner.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String &&
            value.GetString() is { Length: > 0 } text && !text.Any(char.IsControl)
                ? text[..Math.Min(text.Length, CharacterTouch.MaximumName)] : null;
        static string[] Names(JsonElement owner, string property, int most) =>
            owner.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Array
                ? value.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String)
                    .Select(item => item.GetString()!).Where(text => text.Length is > 0 and <= CharacterTouch.MaximumName && !text.Any(char.IsControl))
                    .Take(most).ToArray()
                : [];
        var (wholeX, wholeY) = Unframed(x, y);
        if (answer.ValueKind != JsonValueKind.Object) return (false, new(x, y, [], [], null, null, false, null, null, 0, wholeX, wholeY));
        var hit = answer.TryGetProperty("hit", out var found) && found.ValueKind == JsonValueKind.True;
        return (hit, new CharacterTouch(x, y, Names(answer, "hitAreas", CharacterTouch.MaximumHitAreas),
            Names(answer, "drawables", CharacterTouch.MaximumDrawables), Name(answer, "bone"), Name(answer, "node"),
            answer.TryGetProperty("hair", out var hair) && hair.ValueKind == JsonValueKind.True, Name(answer, "mesh"), Name(answer, "material"),
            0, wholeX, wholeY));
    }

    private async void SendTouch(CharacterTouch touch)
    {
        if (!CanRequest) return;
        try
        {
            await requesting.WaitAsync(lifetime.Token);
            try
            {
                await RendererProtocol.WriteAsync(requests!, RendererProtocol.Message("touch", activation, touch), lifetime.Token)
                    .WaitAsync(TimeSpan.FromSeconds(2), lifetime.Token);
            }
            finally { requesting.Release(); }
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException or OperationCanceledException or TimeoutException)
        {
            ErrorLog.Warn("A tap on the character couldn't reach Martlet.", error);
        }
    }

    private const double BubbleRadius = 16, BubblePadX = 16, BubblePadY = 10, TailLength = 22, TailHalfBase = 9,
        BubbleMargin = 18, ScreenMargin = 6, SpeechWidth = 280, WidestSpeech = 640, SpeechWidthStep = 60;
    // Beyond the longest sentence Martlet speaks at once (1,536 UTF-8 bytes); only bounds an arbitrary request.
    private const int MaximumSpeechCharacters = 2000;

    // Martlet's own typeface (its windows' AppWindowStyle), a little larger to read over a game.
    private static TextBlock SpeechText() => new()
    {
        TextWrapping = TextWrapping.Wrap, MaxWidth = SpeechWidth, FontSize = 15, LineHeight = 21,
        FontFamily = new FontFamily("Segoe UI"), TextAlignment = TextAlignment.Left
    };

    // One continuous outline (rounded body unioned with a curved, tapering tail) with a soft halo, so the bubble reads as a
    // single comic-style shape rather than a box with a triangle stuck on. It is drawn wholly in Martlet's palette (Martlet's
    // own or the character's): a Surface card with an Accent outline, Text and a Glow halo (see ApplyOverlayTheme).
    private Popup CreateSpeechBubble()
    {
        speechText.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        AutomationProperties.SetAutomationId(speechText, "CharacterSpeech");
        AutomationProperties.SetLiveSetting(speechText, AutomationLiveSetting.Polite);
        speechShape.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "SurfaceBrush");
        speechShape.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "AccentBrush");
        speechCanvas.Children.Add(speechShape);
        speechCanvas.Children.Add(speechText);
        speechCanvas.RenderTransform = speechPop;
        speechBubble.Child = speechCanvas;
        LocationChanged += (_, _) => PlaceSpeech();
        SizeChanged += (_, _) => PlaceSpeech();
        Closed += (_, _) => speechBubble.IsOpen = false;
        return speechBubble;
    }

    internal RendererBubble ShowSpeech(RendererSay say)
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
        // Lay the open bubble out now so the reply says whether the text as shown really lies inside the bubble's body, and in
        // which colors it is drawn.
        speechCanvas.UpdateLayout();
        return speechShown = speechShown with
        {
            TextFits = speechText.ActualWidth <= speechShown.Width - BubblePadX * 2 + 1 &&
                speechText.ActualHeight <= speechShown.Height - BubblePadY * 2 + 1,
            Colors = new(BrushHex(speechShape.Fill), BrushHex(speechShape.Stroke), BrushHex(speechText.Foreground),
                speechShape.Effect is System.Windows.Media.Effects.DropShadowEffect halo ? Hex(halo.Color) : null)
        };

        static string BrushHex(Brush? brush) => brush is SolidColorBrush solid ? Hex(solid.Color) : "";
        static string Hex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";
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
            ApplyOverlayTheme(load.DarkTheme, load.ThemeColors);
            if (load.Placement is { } saved) RestorePlacement(saved);
            UseVoice(load.VoiceMuted);
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
                    if (document.RootElement.TryGetProperty("touch", out var touch))
                    {
                        // Unsolicited answer to a tap; never a command reply.
                        Touched(touch);
                        return;
                    }
                    if (document.RootElement.TryGetProperty("touches", out var touches))
                    {
                        // Unsolicited answer to a batch of hit tests (a stroke's path, a zoom's focus); never a command reply.
                        TouchesAnswered(touches);
                        return;
                    }
                    if (document.RootElement.TryGetProperty("faceReading", out var faceReading))
                    {
                        // Unsolicited answer to a reading of the face (MCP's character_face); never a command reply.
                        FaceAnswered(faceReading);
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
                extras = load.Profile.Renderer == Martlet.Avatars.AvatarRenderer.Live2D ? LocalAvatarFiles.Extras(assets.Assets, assets.ModelFile) : null,
                still });
            await ReplyAsync("capabilities", loaded);
            SendView();
            if (!still) StartLookTracking();
            while (!lifetime.IsCancellationRequested)
            {
                message = await RendererProtocol.ReadAsync(input, lifetime.Token);
                if (message.Activation != activation || message.Kind is not ("configure" or "reset" or "apply" or "stop" or "theme" or "mouth" or "motion" or "action" or "home" or "zoom" or "say" or "lock" or "voice" or "gaze" or "where" or "camera" or "snapshot" or "zones"))
                    throw new InvalidDataException("Renderer command is invalid.");
                if (message.Kind == "camera")
                {
                    UseCamera(RendererProtocol.Data<RendererCamera>(message));
                    await ReplyAsync("ok", new { camera = camera is not null });
                    continue;
                }
                if (message.Kind == "snapshot")
                {
                    RendererPicture picture;
                    // A picture that can't be taken never stops the character; the reply is then empty.
                    try { picture = await SnapshotAsync(RendererProtocol.Data<RendererSnapshot>(message)); }
                    catch (Exception error) when (error is IOException or NotSupportedException or ArgumentException or InvalidDataException or
                        FileFormatException or InvalidOperationException or System.Runtime.InteropServices.COMException)
                    {
                        ErrorLog.Warn($"Couldn't take a picture of the character: {error.Message}");
                        picture = new("", 0, 0);
                    }
                    await ReplyAsync("picture", picture);
                    continue;
                }
                if (message.Kind == "gaze")
                {
                    await ReplyAsync("look", Gaze(RendererProtocol.Data<RendererGaze>(message)));
                    continue;
                }
                // Martlet's Reset position: back to the default spot even when locked (it stays locked there).
                if (message.Kind == "home")
                {
                    ResetToDefault(evenLocked: true);
                    await ReplyAsync("placement", Placement());
                    continue;
                }
                if (message.Kind == "where")
                {
                    await ReplyAsync("placement", Placement());
                    continue;
                }
                if (message.Kind == "lock")
                {
                    LockPlacement(RendererProtocol.Data<RendererLock>(message).Locked);
                    await ReplyAsync("placement", Placement());
                    continue;
                }
                if (message.Kind == "voice")
                {
                    UseVoice(RendererProtocol.Data<RendererVoice>(message).Muted);
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
                        case "left": Nudge(-10, 0); break;
                        case "right": Nudge(10, 0); break;
                        case "up": Nudge(0, -10); break;
                        case "down": Nudge(0, 10); break;
                        case "status": break;
                        default: throw new InvalidDataException("Zoom action is invalid.");
                    }
                    await ReplyAsync("view", ViewState());
                    continue;
                }
                if (message.Kind == "theme")
                {
                    var theme = RendererProtocol.Data<RendererTheme>(message);
                    ApplyOverlayTheme(theme.Dark, theme.Colors);
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
        var result = await response.Task.WaitAsync(TimeSpan.FromSeconds(kind switch { "load" => 30, "picture" => 15, _ => 2 }), lifetime.Token);
        failure.ThrowIfFailed();
        if (result.TryGetProperty("error", out _)) throw new InvalidDataException("Browser rejected the selected resource or controls.");
        return result;
    }

    /// <summary>A picture of the character, cropped to its opaque pixels (a head-and-shoulders square for a portrait), scaled
    /// down and encoded as a PNG small enough for one message. A portrait or an ordinary picture is WebView2's capture of the page
    /// as it shows now. A whole picture (touch zones) is drawn by the page itself (<see cref="PictureAsync"/>), never on screen:
    /// framed whole (no zoom, no pan) on a page of the overlay's shape, and drawn again zoomed out when the model draws past its
    /// own canvas and the page cuts it off (<see cref="WholeFraming"/>). Its crop and probe are given with the character framed
    /// whole.</summary>
    private async Task<RendererPicture> SnapshotAsync(RendererSnapshot request)
    {
        failure.ThrowIfFailed();
        var edge = Math.Clamp(request.Edge, RendererSnapshot.MinimumEdge, RendererSnapshot.MaximumEdge);
        RendererZoneProbe? probe = null;
        // The framing the picture is drawn in, when it is a whole picture.
        var (zoom, panX, panY) = (1d, 0d, 0d);
        PageCapture shot;
        if (request.Whole)
        {
            (shot, probe) = await PictureAsync(zoom, panX, panY);
            // Parts drawn past the model's canvas (legs below it, say) are cut off at the page's edge: zoom out until all shows.
            if (probe?.Drawables is { Length: > 0 } drawables &&
                WholeFraming.Fit(shot.Seen, shot.Cut, drawables, FrameFraction) is var fit && fit != (1, 0, 0))
            {
                (zoom, panX, panY) = fit;
                (shot, var framed) = await PictureAsync(zoom, panX, panY);
                probe = framed is null ? probe : WholeFraming.Unframed(framed, zoom, panX, panY, FrameFraction);
            }
        }
        else shot = await CaptureAsync();
        var (source, width, height) = (shot.Source, shot.Width, shot.Height);
        var (left, top, right, bottom) = (shot.Left, shot.Top, shot.Right, shot.Bottom);
        int boxWidth = right - left + 1, boxHeight = bottom - top + 1;
        Int32Rect crop;
        if (request.Portrait)
        {
            // A square as wide as the character's shoulders, from just above the head.
            var side = Math.Min(Math.Max(1, Math.Min(boxWidth, boxHeight)), Math.Min(width, height));
            var x0 = Math.Clamp(left + boxWidth / 2 - side / 2, 0, width - side);
            var y0 = Math.Clamp(top - side / 20, 0, height - side);
            crop = new(x0, y0, side, side);
        }
        else
        {
            var pad = Math.Max(boxWidth, boxHeight) / 20;
            var x0 = Math.Max(0, left - pad);
            var y0 = Math.Max(0, top - pad);
            crop = new(x0, y0, Math.Min(width, right + pad + 1) - x0, Math.Min(height, bottom + pad + 1) - y0);
        }
        var cropped = new System.Windows.Media.Imaging.CroppedBitmap(source, crop);
        var at = new TouchZoneBox((double)crop.X / width, (double)crop.Y / height, (double)crop.Width / width, (double)crop.Height / height);
        if (zoom != 1 || panX != 0 || panY != 0) at = WholeFraming.Unframed(at, zoom, panX, panY, FrameFraction);
        // The probe travels in the same message as the picture.
        var reserve = probe is null ? 0 : JsonSerializer.SerializeToUtf8Bytes(probe, RendererProtocol.Json).Length;
        foreach (var size in new[] { edge, 1536, 1024, 768, 512, 384, 256, 160 }.Where(size => size <= edge).Distinct())
        {
            var scale = Math.Min(1, (double)size / Math.Max(crop.Width, crop.Height));
            var scaled = new System.Windows.Media.Imaging.TransformedBitmap(cropped, new ScaleTransform(scale, scale));
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(scaled));
            using var png = new MemoryStream();
            encoder.Save(png);
            // Base64 grows by a third; the reply must stay well inside one renderer message.
            if (png.Length * 4 / 3 < RendererProtocol.MaximumMessageBytes - 4096 - reserve)
                return new(Convert.ToBase64String(png.GetBuffer(), 0, (int)png.Length), scaled.PixelWidth, scaled.PixelHeight,
                    at.X, at.Y, at.Width, at.Height, probe, Math.Round(zoom, 4));
        }
        throw new InvalidDataException("The character's picture is too large.");
    }

    // A capture of the page: its pixels, and the box of the character's opaque pixels in them (the whole page when there are none).
    private sealed record PageCapture(System.Windows.Media.Imaging.BitmapSource Source, int Width, int Height, int Left, int Top, int Right,
        int Bottom, bool Empty)
    {
        /// <summary>The character's opaque pixels as a box in fractions of the page.</summary>
        internal TouchZoneBox Seen => new((double)Left / Width, (double)Top / Height, (double)(Right - Left + 1) / Width, (double)(Bottom - Top + 1) / Height);

        /// <summary>The page edges the character's opaque pixels reach, where the page may cut it off.</summary>
        internal PageEdges Cut => Empty ? PageEdges.None
            : (Left <= 1 ? PageEdges.Left : 0) | (Top <= 1 ? PageEdges.Top : 0) | (Right >= Width - 2 ? PageEdges.Right : 0) |
              (Bottom >= Height - 2 ? PageEdges.Bottom : 0);
    }

    private async Task<PageCapture> CaptureAsync()
    {
        using var captured = new MemoryStream();
        await browser.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, captured);
        captured.Position = 0;
        return Decode(captured);
    }

    // The overlay's shape (its 3:4 frame with the room beside it), as large as the Live2D canvas allows (2048 pixels).
    private const int PictureHeight = 1364, PictureWidth = (int)(PictureHeight / FrameFraction * 3 / 4);

    /// <summary>The page draws the character on its canvas, PictureWidth by PictureHeight pixels in the framing
    /// <paramref name="zoom"/>, <paramref name="x"/>, <paramref name="y"/>, and reads it back in the same step, so it never shows
    /// on screen (in a still renderer, in the model's rest pose). Also where its drawables or bones are in that picture, as
    /// fractions of it (null when the page can't say).</summary>
    private async Task<(PageCapture Shot, RendererZoneProbe? Probe)> PictureAsync(double zoom, double x, double y)
    {
        const string Prefix = "data:image/png;base64,";
        var drawn = await BrowserAsync("picture", new { width = PictureWidth, height = PictureHeight, zoom, x, y, frame = FrameFraction });
        if (!drawn.TryGetProperty("png", out var png) || png.ValueKind != JsonValueKind.String || png.GetString() is not { } url ||
            !url.StartsWith(Prefix, StringComparison.Ordinal))
            throw new InvalidDataException("The page couldn't draw the character's picture.");
        using var bytes = new MemoryStream(Convert.FromBase64String(url[Prefix.Length..]), writable: false);
        RendererZoneProbe? probe = null;
        try
        {
            probe = new(drawn.TryGetProperty("drawables", out var drawables) ? drawables.Deserialize<RendererDrawableBox[]>(RendererProtocol.Json) : null,
                drawn.TryGetProperty("bones", out var bones) ? bones.Deserialize<RendererBonePoint[]>(RendererProtocol.Json) : null);
        }
        catch (JsonException error) { ErrorLog.Warn($"Couldn't read where the character's parts are: {error.Message}"); }
        return (Decode(bytes), probe);
    }

    // A picture of the page as Bgra32 pixels, with the box of the character's opaque pixels in it.
    private static PageCapture Decode(Stream png)
    {
        var frame = System.Windows.Media.Imaging.BitmapFrame.Create(png, System.Windows.Media.Imaging.BitmapCreateOptions.IgnoreColorProfile,
            System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
        var source = new System.Windows.Media.Imaging.FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        int width = source.PixelWidth, height = source.PixelHeight;
        var pixels = new byte[width * height * 4];
        source.CopyPixels(pixels, width * 4, 0);
        var (left, top, right, bottom) = (width, height, -1, -1);
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
                if (pixels[(y * width + x) * 4 + 3] > 24)
                {
                    if (x < left) left = x;
                    if (x > right) right = x;
                    if (y < top) top = y;
                    if (y > bottom) bottom = y;
                }
        return right < 0 ? new(source, width, height, 0, 0, width - 1, height - 1, Empty: true)
            : new(source, width, height, left, top, right, bottom, Empty: false);
    }

    /// <summary>Where a point of the page (fractions) sits with the character framed whole (no zoom, no pan).</summary>
    private (double X, double Y) Unframed(double x, double y) => CharacterTouch.Unframed(x, y, viewZoom, viewX, viewY, FrameFraction);

    private void FailRenderer()
    {
        failure.Fail();
        response?.TrySetException(new InvalidDataException("Renderer failed; fresh inspection required."));
        lifetime.Cancel();
    }

    // The character's head and eyes follow its usual gaze (the mouse, the mouse when it's near, straight ahead or the window the
    // user is using), or for a while a point on the desktop Martlet asked it to look at, or the mouse after a touch ("gaze");
    // messages to the browser are fire-and-forget and never replied to.
    private void StartLookTracking()
    {
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        timer.Tick += (_, _) =>
        {
            if (closed || failure.Failed || browser.CoreWebView2 is null || !Look()) timer.Stop();
        };
        Closed += (_, _) => timer.Stop();
        timer.Start();
    }

    // Where Martlet asked the character to look (physical screen pixels, like its screenshots) and until when; until when a touch
    // keeps its eyes on the mouse; its usual gaze and what the Eyes menu shows checked; the direction the browser was last given
    // (+x right, +y up, -1 to 1) and what it was toward; and the window the user was last using (for GazeMode.Window).
    private Point? gazePoint;
    private long gazeUntil, attendUntil;
    private GazeMode gazeMode = GazeMode.Mouse;
    private string gazeChoice = RendererGaze.Personality;
    private bool gazeFree = true;
    private double lookX = double.NaN, lookY = double.NaN;
    private string lookTarget = "mouse";
    private ScreenRect? userWindow;

    /// <summary>Turns the head and eyes toward what the gaze says now. False once the browser can't take messages any more.</summary>
    private bool Look(bool always = false)
    {
        if (LookDirection() is not { } look) return true;
        if (!always && Math.Abs(look.X - lookX) < 0.01 && Math.Abs(look.Y - lookY) < 0.01) return true;
        lookX = look.X;
        lookY = look.Y;
        if (browser.CoreWebView2 is null || failure.Failed) return true;
        try
        {
            browser.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { kind = "look", data = new { x = look.X, y = look.Y } },
                RendererProtocol.Json));
            return true;
        }
        catch (Exception error) when (error is InvalidOperationException or System.Runtime.InteropServices.COMException) { return false; }
    }

    private (double X, double Y)? LookDirection()
    {
        Point face, frameTopLeft, frameBottomRight;
        // The face sits at 30% height when unzoomed; follow it through the camera zoom, within the character's frame.
        var faceX = (viewX + 1) / 2;
        var faceY = (1 - (0.4 * viewZoom + viewY)) / 2;
        var frameLeft = FrameOffset(viewport.ActualWidth);
        var frameWidth = viewport.ActualWidth * FrameFraction;
        try
        {
            face = viewport.PointToScreen(new Point(frameLeft + frameWidth * faceX, viewport.ActualHeight * faceY));
            frameTopLeft = viewport.PointToScreen(new Point(frameLeft, 0));
            frameBottomRight = viewport.PointToScreen(new Point(frameLeft + frameWidth, viewport.ActualHeight));
        }
        catch (InvalidOperationException) { return null; }
        var now = Environment.TickCount64;
        if (gazePoint is not null && now >= gazeUntil) gazePoint = null;
        ScreenPoint? mouse = GetCursorPos(out var cursor) ? new ScreenPoint(cursor.X, cursor.Y) : null;
        var frame = new ScreenRect((int)Math.Round(frameTopLeft.X), (int)Math.Round(frameTopLeft.Y),
            (int)Math.Round(frameBottomRight.X - frameTopLeft.X), (int)Math.Round(frameBottomRight.Y - frameTopLeft.Y));
        // The window the user is using is kept track of in every gaze, so watching it starts at once (also from the Eyes menu,
        // which is in front then).
        var used = UserWindow();
        var (target, at) = CharacterGaze.Aim(gazeMode, gazePoint is { } point ? new ScreenPoint(point.X, point.Y) : null, now < attendUntil,
            mouse, frame, used);
        lookTarget = target;
        if (target == "ahead") return (0, 0);
        if (at is not { } aim) return null;
        // Martlet's points are physical pixels; the mouse and windows are read in this process's own coordinates, like the face.
        if (target == "point") face = Physical(face);
        return (Math.Clamp((aim.X - face.X) / 700, -1, 1), Math.Clamp((face.Y - aim.Y) / 700, -1, 1));
    }

    /// <summary>The window the user is using: the one in front, unless it is one of the character's own (its menus; then the one
    /// before stays), minimized, or the desktop or taskbar (then none). Null when none is known.</summary>
    private ScreenRect? UserWindow()
    {
        var front = GetForegroundWindow();
        if (front == IntPtr.Zero || GetWindowThreadProcessId(front, out var owner) == 0 || owner == (uint)Environment.ProcessId) return userWindow;
        var name = new StringBuilder(64);
        var shell = GetClassName(front, name, name.Capacity) > 0 &&
            name.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd";
        userWindow = !shell && !IsIconic(front) && GetWindowRect(front, out var rect) && rect.Right > rect.Left && rect.Bottom > rect.Top
            ? new ScreenRect(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top) : null;
        return userWindow;
    }

    /// <summary>A screen point as this window's process sees it, in physical pixels (unchanged where Windows doesn't scale it).</summary>
    private Point Physical(Point point)
    {
        var native = new CursorPoint { X = (int)Math.Round(point.X), Y = (int)Math.Round(point.Y) };
        var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        return handle != IntPtr.Zero && LogicalToPhysicalPointForPerMonitorDPI(handle, ref native) ? new(native.X, native.Y) : point;
    }

    /// <summary>Sets the usual gaze and the Eyes menu's choices (when given), then looks at the asked-for point for a while, or at
    /// the mouse for a while, or (with neither) the usual way at once; says what the character looks at.</summary>
    internal RendererLook Gaze(RendererGaze gaze)
    {
        if ((gaze.Mode is { } mode && !Enum.IsDefined(mode)) || (gaze.Choice is { } choice && !RendererGaze.Choices.Contains(choice)) ||
            !double.IsFinite(gaze.Seconds) || (gaze.X is null) != (gaze.Y is null) || (gaze.Mouse && gaze.X is not null))
            throw new InvalidDataException("Gaze is invalid.");
        var seconds = (long)(Math.Clamp(gaze.Seconds, RendererGaze.MinimumSeconds, RendererGaze.MaximumSeconds) * 1000);
        if (gaze.X is { } x && gaze.Y is { } y)
        {
            if (!double.IsFinite(x) || !double.IsFinite(y) || Math.Abs(x) >= 1_000_000 || Math.Abs(y) >= 1_000_000)
                throw new InvalidDataException("Gaze is invalid.");
            gazePoint = new(x, y);
            gazeUntil = Environment.TickCount64 + seconds;
        }
        else
        {
            gazePoint = null;
            attendUntil = gaze.Mouse ? Environment.TickCount64 + seconds : 0;
        }
        if (gaze.Mode is { } usual) gazeMode = usual;
        if (gaze.Choice is { } chosen) gazeChoice = chosen;
        if (gaze.Free is { } free) gazeFree = free;
        Look(always: true);
        return new(lookTarget, double.IsFinite(lookX) ? Math.Round(lookX, 3) : 0, double.IsFinite(lookY) ? Math.Round(lookY, 3) : 0, gazeMode);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr window);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window, StringBuilder name, int capacity);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct CursorPoint { public int X, Y; }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out CursorPoint point);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool LogicalToPhysicalPointForPerMonitorDPI(IntPtr window, ref CursorPoint point);

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

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential,
        CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        public int Size;
        public NativeRect Monitor, Work;
        public uint Flags;
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 32)]
        public string Device;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GetMonitorInfoEx(IntPtr monitor, ref MonitorInfoEx info);

    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr deviceContext, IntPtr bounds, IntPtr data);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(IntPtr deviceContext, IntPtr clip, MonitorEnumProc callback, IntPtr data);

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
