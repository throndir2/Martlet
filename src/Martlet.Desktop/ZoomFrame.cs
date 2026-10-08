using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace Martlet.Desktop;

/// <summary>A picture in a frame that zooms in and out: Companion › Touch's zone map. At 1x the picture has the size it fits
/// (<see cref="FitWidth"/> by <see cref="FitHeight"/>). Zoomed in, the picture grows and the frame shows a part of it: the frame keeps
/// its height and grows as wide as the picture, or the page. Only the picture's size changes. Its owner places what is drawn on it
/// again at each zoom (<see cref="Zoomed"/>), so lines, labels and handles keep their size on the screen and a drag moves a box by
/// smaller steps. Ctrl+wheel zooms where the pointer is; <see cref="ZoomIn"/>, <see cref="ZoomOut"/> and <see cref="ZoomTo"/> keep
/// the middle of the frame. The scroll bars, the wheel and a drag move around a zoomed picture: the wheel scrolls up and down, then
/// the page once the picture can't scroll further that way, and Shift+wheel scrolls sideways. A drag moves the picture where nothing
/// on it takes the click (its boxes do), and a drag with Ctrl or the middle button moves it from anywhere on it.</summary>
internal sealed class ZoomFrame : ScrollViewer
{
    /// <summary>The zoom steps, in times the size the picture fits.</summary>
    internal static readonly double[] Steps = [1, 1.5, 2, 3, 4, 6, 8];

    // A wheel's notch. A touchpad sends smaller turns, which add up to a notch.
    private const double Notch = 120;
    // How far a notch of Shift+wheel scrolls sideways: as far as a notch scrolls up or down (3 lines of 16 pixels).
    private const double SidewaysNotch = 48;

    private readonly FrameworkElement picture;
    // The picture's point (fractions of it) to bring to the middle of the frame once the frame is laid out.
    private Point? restore;
    private Point? panFrom;
    private Point panStart;
    private MouseButton panButton;
    private double turned;

    /// <summary>Shows <paramref name="picture"/> (the frame sizes it) zoomed to <paramref name="zoom"/>, with its point
    /// <paramref name="center"/> (fractions of it; by default its middle) in the middle of the frame.</summary>
    internal ZoomFrame(FrameworkElement picture, double fitWidth, double fitHeight, double zoom = 1, Point? center = null)
    {
        if (!(fitWidth > 0 && fitHeight > 0) || !double.IsFinite(fitWidth) || !double.IsFinite(fitHeight))
            throw new ArgumentOutOfRangeException(nameof(fitWidth), "The picture needs a size to fit.");
        this.picture = picture;
        FitWidth = fitWidth;
        FitHeight = fitHeight;
        Zoom = Clamp(zoom);
        SizePicture();
        Content = picture;
        Height = fitHeight;
        HorizontalAlignment = HorizontalAlignment.Left;
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        // Not a keyboard stop: a frame with the focus would hold the page back from drawing again, and the boxes' fields take exact
        // places from the keyboard.
        Focusable = false;
        // Where both scroll bars show, the square between them takes the card's color, not the system's grey.
        Resources[SystemColors.ControlBrushKey] = Brushes.Transparent;
        restore = center ?? new Point(0.5, 0.5);
        // At its first size, in the same layout pass and before it shows: also when the page was drawn while it was hidden.
        SizeChanged += (_, _) => Restore();
        ScrollChanged += (_, e) =>
        {
            // The frame's own scrolling: the page's scroller never hears it.
            e.Handled = true;
            if (restore is null && IsVisible) ViewChanged?.Invoke();
        };
    }

    /// <summary>The picture's width at 1x.</summary>
    internal double FitWidth { get; }

    /// <summary>The picture's height at 1x, and the frame's height.</summary>
    internal double FitHeight { get; }

    /// <summary>How far the picture is zoomed in, one of <see cref="Steps"/>: 1 shows it whole.</summary>
    internal double Zoom { get; private set; }

    internal double PictureWidth => FitWidth * Zoom;
    internal double PictureHeight => FitHeight * Zoom;
    internal bool CanZoomIn => Zoom < Steps[^1];
    internal bool CanZoomOut => Zoom > Steps[0];

    /// <summary>The picture's point at the middle of the frame, in fractions of the picture (before the frame is laid out, the point
    /// it will show).</summary>
    internal Point Center
    {
        get
        {
            if (restore is { } pending) return pending;
            var (offset, view) = (Offset, View);
            return new(Math.Clamp((offset.X + view.Width / 2) / PictureWidth, 0, 1), Math.Clamp((offset.Y + view.Height / 2) / PictureHeight, 0, 1));
        }
    }

    /// <summary>The zoom changed, so the picture's size did: its owner places what it draws on it again.</summary>
    internal event Action? Zoomed;

    /// <summary>The zoom, or the part of the picture the frame shows, changed.</summary>
    internal event Action? ViewChanged;

    internal bool ZoomIn(Point? at = null) => ZoomTo(Next(Zoom, 1), at);

    internal bool ZoomOut(Point? at = null) => ZoomTo(Next(Zoom, -1), at);

    /// <summary>Zooms to <paramref name="zoom"/> (kept within <see cref="Steps"/>). The picture's point under <paramref name="at"/>
    /// (a point in the frame) stays under it; without it, the picture's point at the middle of the frame stays at its middle. False
    /// when the zoom doesn't change.</summary>
    internal bool ZoomTo(double zoom, Point? at = null)
    {
        zoom = Clamp(zoom);
        if (zoom == Zoom) return false;
        if (restore is not null)
        {
            // Not laid out yet: it shows the point it was given once it is.
            Zoom = zoom;
            SizePicture();
            Zoomed?.Invoke();
            ViewChanged?.Invoke();
            return true;
        }
        var offset = Offset;
        var kept = at is { } point ? new Point((offset.X + point.X) / PictureWidth, (offset.Y + point.Y) / PictureHeight) : Center;
        Zoom = zoom;
        SizePicture();
        Zoomed?.Invoke();
        // Laid out at once, so the frame's new size and the scrolling after it are known now (several notches of the wheel zoom
        // one after another).
        UpdateLayout();
        var view = View;
        var anchor = at ?? new Point(view.Width / 2, view.Height / 2);
        ScrollTo(kept.X * PictureWidth - anchor.X, kept.Y * PictureHeight - anchor.Y);
        UpdateLayout();
        ViewChanged?.Invoke();
        return true;
    }

    /// <summary>The next zoom step from <paramref name="zoom"/>: in for a positive <paramref name="direction"/>, else out.</summary>
    internal static double Next(double zoom, int direction) => direction > 0
        ? Steps.FirstOrDefault(step => step > zoom + 0.001, Steps[^1])
        : Steps.LastOrDefault(step => step < zoom - 0.001, Steps[0]);

    private static double Clamp(double zoom) => double.IsFinite(zoom) ? Math.Clamp(zoom, Steps[0], Steps[^1]) : Steps[0];

    // The scrolling as laid out now (the frame's own properties follow it a moment later).
    private Point Offset => ScrollInfo is { } info ? new(info.HorizontalOffset, info.VerticalOffset) : new(HorizontalOffset, VerticalOffset);
    private Size View => ScrollInfo is { } info ? new(info.ViewportWidth, info.ViewportHeight) : new(ViewportWidth, ViewportHeight);
    private Size Scrollable => ScrollInfo is { } info
        ? new(Math.Max(0, info.ExtentWidth - info.ViewportWidth), Math.Max(0, info.ExtentHeight - info.ViewportHeight))
        : new(ScrollableWidth, ScrollableHeight);

    private void SizePicture()
    {
        picture.Width = PictureWidth;
        picture.Height = PictureHeight;
        // Zoomed in, a drag on the picture moves it around: the pointer shows it.
        picture.Cursor = Zoom > Steps[0] ? Cursors.ScrollAll : null;
    }

    private void ScrollTo(double x, double y)
    {
        if (ScrollInfo is { } info)
        {
            info.SetHorizontalOffset(Math.Max(0, x));
            info.SetVerticalOffset(Math.Max(0, y));
            return;
        }
        ScrollToHorizontalOffset(Math.Max(0, x));
        ScrollToVerticalOffset(Math.Max(0, y));
    }

    private void Restore()
    {
        if (restore is not { } center) return;
        var view = View;
        if (!(view.Width > 0 && view.Height > 0)) return;
        restore = null;
        ScrollTo(center.X * PictureWidth - view.Width / 2, center.Y * PictureHeight - view.Height / 2);
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        if (e.Handled) return;
        var keys = Keyboard.Modifiers;
        if ((keys & ModifierKeys.Control) != 0)
        {
            // Ctrl+wheel zooms where the pointer is, a step a notch; never during a drag. The page never scrolls for it.
            e.Handled = true;
            if (panFrom is not null || Mouse.LeftButton == MouseButtonState.Pressed) return;
            turned += e.Delta;
            var at = e.GetPosition(this);
            while (Math.Abs(turned) >= Notch)
            {
                var direction = Math.Sign(turned);
                turned -= direction * Notch;
                ZoomTo(Next(Zoom, direction), at);
            }
            return;
        }
        turned = 0;
        var (offset, scrollable) = (Offset, Scrollable);
        if ((keys & ModifierKeys.Shift) != 0)
        {
            // Sideways while the picture is wider than the frame; at its edge it stops, as the page has no sideways to go on to.
            if (scrollable.Width <= 0) return;
            e.Handled = true;
            if (e.Delta > 0 ? offset.X > 0 : offset.X < scrollable.Width) ScrollTo(offset.X - e.Delta / Notch * SidewaysNotch, offset.Y);
            return;
        }
        // The wheel scrolls the picture while it can scroll that way, then the page.
        if (e.Delta > 0 ? offset.Y > 0 : offset.Y < scrollable.Height) base.OnMouseWheel(e);
    }

    protected override void OnPreviewMouseDown(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseDown(e);
        // A drag with the middle button, or with Ctrl, moves the zoomed picture from anywhere on it, its boxes too.
        if (!e.Handled && (e.ChangedButton == MouseButton.Middle ||
            e.ChangedButton == MouseButton.Left && (Keyboard.Modifiers & ModifierKeys.Control) != 0))
            StartPan(e);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        // A drag where nothing on the picture took the click moves the zoomed picture.
        StartPan(e);
        if (!e.Handled) base.OnMouseLeftButtonDown(e);
    }

    private void StartPan(MouseButtonEventArgs e)
    {
        if (Zoom <= Steps[0] || panFrom is not null || !OnPicture(e.OriginalSource)) return;
        var (at, offset) = (e.GetPosition(this), Offset);
        if (!CaptureMouse()) return;
        (panFrom, panStart, panButton) = (at, offset, e.ChangedButton);
        Cursor = Cursors.ScrollAll;
        ForceCursor = true;
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (panFrom is not { } from) return;
        var at = e.GetPosition(this);
        ScrollTo(panStart.X - (at.X - from.X), panStart.Y - (at.Y - from.Y));
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        if (panFrom is not null && e.ChangedButton == panButton)
        {
            ReleaseMouseCapture();
            e.Handled = true;
            return;
        }
        base.OnMouseUp(e);
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        // A box on the picture that lets go of the mouse is heard here too; only the frame's own drag ends.
        if (panFrom is null || IsMouseCaptured) return;
        panFrom = null;
        ClearValue(CursorProperty);
        ForceCursor = false;
    }

    private bool OnPicture(object source) =>
        source is Visual or Visual3D && (ReferenceEquals(source, picture) || picture.IsAncestorOf((DependencyObject)source));
}
