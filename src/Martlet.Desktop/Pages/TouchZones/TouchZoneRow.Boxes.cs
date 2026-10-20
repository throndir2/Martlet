using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Martlet.Avatar.Hosting;
using Martlet.Core.Settings;
using Martlet.Presentation;

namespace Martlet.Desktop;

/// <summary>A zone row's box: the box field read and written, and each area's box drawn on the zone map, which a drag moves or resizes.</summary>
internal sealed partial class TouchZoneRow
{
    // A tenth of a percent at 1x; a hundredth once the zoomed-in picture is over a thousand pixels, so a drag's steps stay under a
    // pixel.
    private string BoxText(TouchZoneBox b) =>
        string.Join(", ", new[] { b.X, b.Y, b.Width, b.Height }.Select(v => (v * 100).ToString(
            frame is { } map && Math.Max(map.PictureWidth, map.PictureHeight) > 1000 ? "0.##" : "0.#", CultureInfo.CurrentCulture)));

    private string BoxesText(IEnumerable<TouchZoneBox> boxes) => string.Join(" | ", boxes.Select(BoxText));

    private static TouchZoneBox? ParsedBox(string text)
    {
        var values = text.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries)
            .Select(t => double.TryParse(t, NumberStyles.Float, CultureInfo.CurrentCulture, out var v) ? v / 100 : double.NaN).ToArray();
        if (values.Length != 4 || values.Any(v => !double.IsFinite(v))) return null;
        var parsed = new TouchZoneBox(values[0], values[1], values[2], values[3]);
        return parsed.Valid ? parsed : null;
    }

    /// <summary>Each area's box as the field reads now (one to <see cref="CharacterTouchZones.MaximumAreas"/>), or null when one of
    /// them can't be read.</summary>
    private List<TouchZoneBox>? ParsedBoxes()
    {
        var parts = box.Text.Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is 0 or > CharacterTouchZones.MaximumAreas) return null;
        var boxes = parts.Select(ParsedBox).ToList();
        return boxes.All(b => b is not null) ? [.. boxes.Select(b => b!)] : null;
    }

    /// <summary>The zone as the row shows it now (an unreadable box or rest keeps the saved one). A zone that follows the model's
    /// own part keeps its areas until the owner moves one: the zone is then that one box, and saving finds what it follows
    /// from there.</summary>
    internal CharacterTouchZone Read()
    {
        IReadOnlyList<string> actions = [.. reactions];
        var given = name.Text.Trim();
        var line = narration.Text.Trim();
        var shaped = zone;
        if (box.Text != shown && ParsedBoxes() is { } boxes)
        {
            if (follows)
            {
                var place = boxes[moved >= 0 && moved < boxes.Count ? moved : 0];
                shaped = zone with { Box = place, Areas = null, Follows = null, Drawables = [], Bones = [] };
            }
            else if (boxes.Count == 1) shaped = zone with { Box = boxes[0], Areas = null };
            else shaped = CharacterTouchZones.Compose(zone, [.. boxes.Select((b, i) =>
                (zone.Areas is { } had && i < had.Count ? had[i] : new() { Box = b }) with { Box = b, FromModel = false })], null);
        }
        return shaped with
        {
            Enabled = on.IsChecked == true,
            Label = given.Length == 0 || given == CharacterTouchZones.Kind(zone.Id)?.Label ? null : given,
            Reaction = new()
            {
                Actions = actions, Notices = notices.IsChecked == true,
                Narration = line.Length == 0 || line == CharacterTouchZones.Kind(zone.Id)?.Narration ? null : line,
                CooldownSeconds = double.TryParse(rest.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var seconds) &&
                    seconds is >= 0 and <= CharacterTouchReaction.MaximumCooldown ? seconds : zone.Reaction.CooldownSeconds
            }
        };
    }

    // Each area's box on the picture, and its name tag (on the first area only).
    private readonly List<(Border Rectangle, TextBlock? Label)> drawn = [];
    private Canvas? picture;
    private Color color;
    private ZoomFrame? frame;

    /// <summary>Draws the zone's areas on the picture in <paramref name="map"/>; dragging one moves it, dragging its corner
    /// resizes it. Zoomed in, the same drag moves it by smaller steps.</summary>
    internal void Draw(Canvas canvas, ZoomFrame map, Color color)
    {
        (picture, frame, this.color) = (canvas, map, color);
        Place();
    }

    // One area's box on the picture: the first holds the zone's name; an area that follows the model's own part is drawn lighter.
    private (Border Rectangle, TextBlock? Label) AreaBox(int index)
    {
        var grip = new Rectangle
        {
            Width = 8, Height = 8, Fill = new SolidColorBrush(color), HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom, Cursor = Cursors.SizeNWSE
        };
        var inside = new Grid();
        inside.Children.Add(grip);
        var areas = zone.AllAreas.Count;
        var rectangle = new SeenBorder
        {
            BorderBrush = new SolidColorBrush(color), BorderThickness = new Thickness(follows ? 1 : 1.5), Child = inside, Cursor = Cursors.SizeAll,
            Background = new SolidColorBrush(Color.FromArgb(follows ? (byte)0x1C : (byte)0x30, color.R, color.G, color.B)),
            ToolTip = index == 0 && areas <= 1 ? zone.Name : $"{zone.Name}, area {index + 1}" + (follows ? $" of the model's own {zone.Follows}" : "")
        };
        AutomationProperties.SetAutomationId(rectangle, index == 0 ? $"TouchZoneRect-{Number}" : $"TouchZoneRect-{Number}-{index + 1}");
        AutomationProperties.SetName(rectangle, index == 0 ? zone.Name : $"{zone.Name} {index + 1}");
        TextBlock? label = null;
        if (index == 0)
        {
            var (text, back) = TouchZoneLabelColors.For(color, SystemParameters.HighContrast);
            label = new TextBlock
            {
                Text = zone.Name, Foreground = text, FontSize = 10, Padding = new Thickness(2, 0, 2, 0), TextWrapping = TextWrapping.NoWrap,
                Background = back, VerticalAlignment = VerticalAlignment.Top,
                HorizontalAlignment = HorizontalAlignment.Left, IsHitTestVisible = false
            };
        }
        picture!.Children.Add(rectangle);
        if (label is not null) picture.Children.Add(label);
        Point? from = null;
        var resizing = false;
        var start = new TouchZoneBox(0, 0, 0.1, 0.1);
        rectangle.MouseLeftButtonDown += (_, e) =>
        {
            if (ParsedBoxes() is not { } boxes || index >= boxes.Count) return;
            from = e.GetPosition(picture);
            resizing = ReferenceEquals(e.OriginalSource, grip);
            start = boxes[index];
            rectangle.CaptureMouse();
            e.Handled = true;
        };
        rectangle.MouseMove += (_, e) =>
        {
            if (from is not { } origin || frame is not { } map || ParsedBoxes() is not { } boxes || index >= boxes.Count) return;
            var at = e.GetPosition(picture);
            double dx = (at.X - origin.X) / map.PictureWidth, dy = (at.Y - origin.Y) / map.PictureHeight;
            // At least a fiftieth of the picture each way at 1x; zoomed in, as small as that shows on the screen.
            var least = 0.02 / map.Zoom;
            boxes[index] = resizing
                ? start with { Width = Within(start.Width + dx, least, 1 - start.X), Height = Within(start.Height + dy, least, 1 - start.Y) }
                : start with { X = Within(start.X + dx, 0, 1 - start.Width), Y = Within(start.Y + dy, 0, 1 - start.Height) };
            moved = index;
            box.Text = BoxesText(boxes);
        };
        rectangle.MouseLeftButtonUp += (_, e) =>
        {
            from = null;
            rectangle.ReleaseMouseCapture();
            e.Handled = true;
        };
        return (rectangle, label);
    }

    // Math.Clamp that a box typed past the picture's edge can't make throw.
    private static double Within(double value, double least, double most) => Math.Clamp(value, least, Math.Max(least, most));

    /// <summary>Places each area's box on the picture at the frame's zoom, where the field says: it adds the boxes of areas
    /// added and hides those taken away.</summary>
    internal void Place()
    {
        ShowAreaButtons();
        if (picture is null || frame is null || ParsedBoxes() is not { } boxes) return;
        double width = frame.PictureWidth, height = frame.PictureHeight;
        while (drawn.Count < boxes.Count) drawn.Add(AreaBox(drawn.Count));
        for (var i = 0; i < drawn.Count; i++)
        {
            var (rectangle, label) = drawn[i];
            var visible = i < boxes.Count && !Deleted;
            rectangle.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            if (label is not null) label.Visibility = rectangle.Visibility;
            if (!visible) continue;
            var b = boxes[i];
            Canvas.SetLeft(rectangle, b.X * width);
            Canvas.SetTop(rectangle, b.Y * height);
            rectangle.Width = Math.Max(4, b.Width * width);
            rectangle.Height = Math.Max(4, b.Height * height);
            if (label is null) continue;
            Canvas.SetLeft(label, b.X * width + 1);
            Canvas.SetTop(label, b.Y * height + 1);
        }
    }

    // Add area shows while the zone has room for one more, Remove area while it has more than one.
    private void ShowAreaButtons()
    {
        var count = ParsedBoxes()?.Count ?? 1;
        if (addArea is not null) addArea.Visibility = count < CharacterTouchZones.MaximumAreas ? Visibility.Visible : Visibility.Collapsed;
        if (removeArea is not null) removeArea.Visibility = count > 1 ? Visibility.Visible : Visibility.Collapsed;
    }
}
