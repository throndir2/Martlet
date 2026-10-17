using System.Windows;

namespace Martlet.Desktop;

/// <summary>Where the Devices map puts its devices around This PC, so the map uses its width and is only as tall as the
/// devices need. Up to <see cref="RingFrom"/> - 1 devices stand in a column on each side of This PC. More devices stand in a
/// ring around it: a row above, a row below and a short column on each side. The devices go clockwise in the order given
/// (the columns: down the right side, then up the left side; the ring: from the top left). Every connection is a straight line
/// from This PC to the nearest point of a device, so connections never cross and never run through another card. Reads
/// nothing itself.</summary>
internal static class MapLayout
{
    /// <summary>From this many devices around This PC, the map draws smaller cards.</summary>
    internal const int CompactFrom = 9;
    /// <summary>From this many devices around This PC, they stand in a ring around it.</summary>
    internal const int RingFrom = 7;
    internal const double Margin = 14, Gap = 12, MinimumHeight = 240;
    private const double ColumnReach = 380, RingReach = 480;

    /// <summary>The places of the devices from the center of This PC, in the order given; the height of the map; and the
    /// shape in words (for MCP).</summary>
    internal sealed record Result(IReadOnlyList<Point> Points, double Height, string Shape);

    internal static Result Place(int count, double width, Size card, Size hub)
    {
        double rowStep = card.Height + Gap, columnStep = card.Width + Gap;
        // The side columns are as far out as the map allows, but not so far that the connections get long.
        var room = Math.Max(hub.Width / 2 + Gap + card.Width / 2, width / 2 - Margin - card.Width / 2);
        var points = new List<Point>(count);
        string shape;
        double reach;
        if (count < RingFrom)
        {
            var x = Math.Min(room, ColumnReach);
            int right = (count + 1) / 2, left = count - right;
            for (var i = 0; i < right; i++) points.Add(new(x, (i - (right - 1) / 2.0) * rowStep));
            for (var i = left - 1; i >= 0; i--) points.Add(new(-x, (i - (left - 1) / 2.0) * rowStep));
            reach = (Math.Max(right, left) - 1) / 2.0 * rowStep;
            shape = $"columns ({left} left, {right} right)";
        }
        else
        {
            var x = Math.Min(room, RingReach);
            var perRow = Math.Max(2, (int)Math.Floor(2 * x / columnStep) + 1);
            // Each side column holds at least one device; the rows above and below take what they can, the columns the rest.
            var side = Math.Max(1, (int)Math.Ceiling((count - 2.0 * perRow) / 2));
            var rows = count - 2 * side;
            int top = (rows + 1) / 2, bottom = rows - top;
            var rowY = Math.Max(hub.Height / 2 + 2 * Gap + card.Height / 2, (side + 1) / 2.0 * rowStep);
            double Along(int i, int of) => of == 1 ? 0 : -x + 2 * x * i / (of - 1);
            for (var i = 0; i < top; i++) points.Add(new(Along(i, top), -rowY));
            for (var i = 0; i < side; i++) points.Add(new(x, (i - (side - 1) / 2.0) * rowStep));
            for (var i = bottom - 1; i >= 0; i--) points.Add(new(Along(i, bottom), rowY));
            for (var i = side - 1; i >= 0; i--) points.Add(new(-x, (i - (side - 1) / 2.0) * rowStep));
            reach = rowY;
            shape = $"ring ({top} above, {side} right, {bottom} below, {side} left)";
        }
        var height = Math.Max(MinimumHeight, 2 * Math.Max(reach + card.Height / 2, hub.Height / 2) + 2 * Margin);
        return new(points, height, shape);
    }

    /// <summary>Where the line from the center of a box toward <paramref name="toward"/> leaves the box.</summary>
    internal static Point Edge(Point center, Size box, Point toward)
    {
        double dx = toward.X - center.X, dy = toward.Y - center.Y;
        if (dx == 0 && dy == 0) return center;
        var t = Math.Min(dx == 0 ? double.MaxValue : box.Width / 2 / Math.Abs(dx), dy == 0 ? double.MaxValue : box.Height / 2 / Math.Abs(dy));
        return new(center.X + t * dx, center.Y + t * dy);
    }

    /// <summary>The point of a card nearest to <paramref name="from"/>, where its connection ends: a line from This PC to it
    /// passes no other card, because the rows stand outside the side columns.</summary>
    internal static Point Nearest(Point center, Size box, Point from) =>
        new(Math.Clamp(from.X, center.X - box.Width / 2, center.X + box.Width / 2),
            Math.Clamp(from.Y, center.Y - box.Height / 2, center.Y + box.Height / 2));
}
