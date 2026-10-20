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

/// <summary>Where the overlay sits: placing it on the desktop, zoom and pan, the camera view, locking, click-through, voice muting and the screens.</summary>
internal sealed partial class RendererWindow
{
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
        // The camera view always catches clicks; back on the overlay, click-through applies again.
        ApplyClickThrough();
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

    // ---------- click-through ----------

    // Clicks pass through the character to the windows under it until Martlet turns this off. The camera view always catches them.
    private bool clickThrough;
    private const int ExtendedStyleIndex = -20;
    private const nint TransparentStyle = 0x20;

    private void UseClickThrough(bool on)
    {
        if (clickThrough != on)
        {
            clickThrough = on;
            if (on)
            {
                // A menu, drag, pan or stroke under way ends: the mouse no longer reaches the character.
                if (viewport.ContextMenu is { IsOpen: true } menu) menu.IsOpen = false;
                if (viewport.IsMouseCaptured) viewport.ReleaseMouseCapture();
                press = null;
            }
            ErrorLog.Info(on ? "Clicks pass through the character." : "The character catches clicks again.");
        }
        ApplyClickThrough();
        ShowPlacementLock();
    }

    /// <summary>Sets or clears WS_EX_TRANSPARENT on the layered overlay (and on its speech bubble), so the mouse's clicks, wheel
    /// and right-clicks go to the window under it. Never on the camera view or the still renderer.</summary>
    private void ApplyClickThrough()
    {
        var on = clickThrough && camera is null && !still;
        SetClickThrough(new System.Windows.Interop.WindowInteropHelper(this).Handle, on);
        if (speechBubble.Child is { } bubble && PresentationSource.FromVisual(bubble) is System.Windows.Interop.HwndSource popup)
            SetClickThrough(popup.Handle, on);
    }

    private static void SetClickThrough(IntPtr window, bool on)
    {
        if (window == IntPtr.Zero) return;
        var style = GetWindowLongPtrW(window, ExtendedStyleIndex);
        var wanted = on ? style | TransparentStyle : style & ~TransparentStyle;
        if (wanted != style) SetWindowLongPtrW(window, ExtendedStyleIndex, wanted);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern nint GetWindowLongPtrW(IntPtr window, int index);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern nint SetWindowLongPtrW(IntPtr window, int index, nint value);

    private void ShowPlacementLock()
    {
        viewport.PlacementLocked = placementLocked;
        viewport.Cursor = placementLocked && camera is null ? Cursors.Arrow : Cursors.SizeAll;
        AutomationProperties.SetName(viewport, camera is not null
            ? "Martlet camera. Drag to move the character in the view; mouse wheel zooms it in or out; arrow keys nudge it; Home or 0 resets the framing; Shift+drag moves the window; right-click for more options."
            : clickThrough
            ? "Character. Clicks pass through it to the windows under it; turn this off in Martlet."
            : placementLocked
            ? "Character. Position locked; unlock it in Martlet. Mouse wheel zooms; Ctrl+drag or middle-drag pans when zoomed in; right-click for talk, mute, settings, zoom, click-through and hide options."
            : "Character. Drag to move; mouse wheel zooms; Ctrl+drag or middle-drag pans when zoomed in; right-click for talk, mute, settings, zoom, position, lock, click-through and hide options.");
    }

    /// <summary>Puts the overlay back where it was saved, at that size, and locks it again when it was locked. On the same
    /// monitor (by name) when it is still connected, at the same spot on it, with the character's middle kept on that monitor's
    /// work area; otherwise where it was when that spot is still on a screen; otherwise at its default spot.</summary>
    private void RestorePlacement(RendererPlacement placement)
    {
        if (PlacedBounds(placement, moving: false) is { } bounds)
        {
            Width = bounds.Width;
            Height = bounds.Height;
            Left = bounds.Left;
            Top = bounds.Top;
        }
        LockPlacement(placement.Locked);
    }

    /// <summary>Where the overlay goes for a saved place (<see cref="RestorePlacement"/>): its window's top-left corner and size,
    /// or null when the place isn't valid or isn't on a screen now. <paramref name="moving"/>: a character profile's place for the
    /// showing character (<see cref="MovePlacement"/>), which otherwise stays where it is.</summary>
    private (double Left, double Top, double Width, double Height)? PlacedBounds(RendererPlacement placement, bool moving)
    {
        if (!placement.IsValid) return null;
        var frame = Math.Clamp(placement.Width, MinFrameWidth, MaxFrameWidth);
        var height = Math.Clamp(placement.Height, MinFrameWidth, Math.Max(MinFrameWidth, SystemParameters.VirtualScreenHeight));
        var width = OverlayWidth(frame);
        if (placement is { Screen: { } name, ScreenLeft: { } dx, ScreenTop: { } dy } && ScreenNamed(name) is { } screen)
        {
            var work = screen.Work;
            var middle = new Point(work.Left + dx + FrameOffset(width) + frame / 2, work.Top + dy + height / 2);
            var kept = new Point(Math.Clamp(middle.X, work.Left, Math.Max(work.Left, work.Right)),
                Math.Clamp(middle.Y, work.Top, Math.Max(work.Top, work.Bottom)));
            ErrorLog.Info(moving ? $"The character moved to where its profile left it on {ScreenLabel(name)}."
                : $"The character is back where it was left on {ScreenLabel(name)}.");
            return (work.Left + dx + (kept.X - middle.X), work.Top + dy + (kept.Y - middle.Y), width, height);
        }
        if (OnAScreen(new Point(placement.Left + FrameOffset(width) + frame / 2, placement.Top + height / 2)))
        {
            ErrorLog.Info(placement.Screen is { } gone
                ? $"The character's screen ({ScreenLabel(gone)}) isn't connected; it {(moving ? "moved to" : "shows")} where it was on the desktop."
                : moving ? "The character moved to where its profile left it." : "The character is back where it was left.");
            return (placement.Left, placement.Top, width, height);
        }
        ErrorLog.Warn($"The character's saved position isn't on a screen now; {(moving ? "it stays where it is" : "it shows at its default spot")}.");
        return null;
    }

    /// <summary>Martlet's character profiles: puts the showing overlay where that profile's character was left on this PC, at
    /// that size, locked or not (as <see cref="RestorePlacement"/> does). It isn't a move of the user's, so the character doesn't
    /// react to it and Martlet saves the place itself. While the camera view shows, the overlay goes there when the camera view
    /// closes.</summary>
    private void MovePlacement(RendererPlacement placement)
    {
        EndPan();
        var bounds = PlacedBounds(placement, moving: true);
        if (camera is { } open)
        {
            if (bounds is { } at) camera = (at.Left, at.Top, at.Width, at.Height, open.Topmost);
        }
        else if (bounds is { } at)
        {
            Width = at.Width;
            Height = at.Height;
            Left = at.Left;
            Top = at.Top;
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
    /// sits in the view, whether this is the camera view and whether clicks pass through the character now.</summary>
    internal RendererView ViewState() => new(Math.Round(FrameWidth), Math.Round(Height),
        WorkAreaTop() is { } screenTop ? Math.Round(Top - screenTop) : null, Math.Round(viewZoom, 3),
        double.IsFinite(contentTop) ? Math.Round((1 - (contentTop * viewZoom + viewY)) / 2, 4) : null, Math.Round(Width), placementLocked,
        Math.Round(viewX * FrameFraction / 2, 4), Math.Round(viewY / 2, 4), camera is not null, clickThrough && camera is null);

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
}
