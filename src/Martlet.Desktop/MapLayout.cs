using System.Windows;

namespace Martlet.Desktop;

/// <summary>A device's place on the Devices map. <paramref name="Column"/> is -2 or -1 (left of This PC) or 1 or 2 (right);
/// <paramref name="Row"/> 0 is This PC's row, negative rows are above it and positive rows below.</summary>
internal readonly record struct MapCell(int Column, int Row);

/// <summary>What a card on the map is, for where it goes: cloud services take the left-most places, Add a computer the
/// left-most of the rest, the "N more" card the farthest place, and your computers the places nearest This PC.</summary>
internal enum MapSlot { Device, Cloud, Add, More }

/// <summary>Where the Devices map draws each card: a grid around This PC, so devices fill the space on every side and cards
/// never overlap, whatever their number, their height or the window size. A wide map has four columns: This PC's own row
/// holds one device on each side (the outer columns) and every row above and below holds four. A narrow map has the two inner
/// columns only, two devices in every row above and below This PC, so its cards stay large enough to read. Devices fill the
/// places nearest This PC first, so the map grows evenly up and down. Connections run only in the gaps between cards: a spine
/// up and down from This PC to the inner columns and a bus beside each inner column to the outer columns, so a line never runs
/// under another card.</summary>
internal static class MapLayout
{
    internal const double CardWidth = 232;
    internal const double HubWidth = 236;
    private const double MinGapX = 28;
    private const double MostGapX = 64;
    internal const double GapY = 16;
    private const double MostGapY = 36;
    private const double Margin = 16;
    /// <summary>From this many devices on, cards are compact (name, computer and status, no chips) so the map stays short.</summary>
    internal const int CompactFrom = 19;
    /// <summary>A map at least this wide (before it shrinks to fit) has four columns; a narrower one two.</summary>
    private const double WideFrom = 860;

    /// <summary>Four columns on a map <paramref name="available"/> wide enough, else two; four when the width isn't known yet.</summary>
    internal static int Columns(double available) => available <= 0 || available >= WideFrom ? 4 : 2;

    /// <summary>The narrowest canvas: the columns of cards with the smallest gaps. A narrower window shrinks the whole map.</summary>
    internal static double MinWidth(int columns) => columns * CardWidth + (columns - 1) * MinGapX + 2 * Margin;

    /// <summary>How many rows above (and below) This PC's row <paramref name="count"/> devices need.</summary>
    internal static int RowsAround(int count, int columns = 4) =>
        columns == 2 ? (count + 3) / 4 : count <= 2 ? 0 : (count - 2 + 7) / 8;

    /// <summary>The places in the order devices fill them: This PC's row first, then the inner columns, then the outer ones,
    /// row by row away from This PC; right before left and above before below.</summary>
    internal static IReadOnlyList<MapCell> Cells(int count, int columns = 4)
    {
        var around = RowsAround(count, columns);
        var cells = new List<MapCell>();
        if (columns == 4) cells.AddRange([new(2, 0), new(-2, 0)]);
        for (var row = 1; row <= around; row++)
            foreach (var column in columns == 4 ? new[] { 1, -1, 2, -2 } : [1, -1])
            {
                cells.Add(new(column, -row));
                cells.Add(new(column, row));
            }
        return [.. cells.OrderBy(Cost).ThenBy(c => c.Column < 0).ThenBy(c => c.Row > 0).Take(count)];
    }

    private static int Cost(MapCell cell) => cell.Row == 0 ? 1 : 2 * Math.Abs(cell.Row) + (Math.Abs(cell.Column) == 2 ? 1 : 0);

    /// <summary>A place for each card, in the order of <paramref name="slots"/>.</summary>
    internal static MapCell[] Assign(IReadOnlyList<MapSlot> slots, int columns = 4)
    {
        var free = Cells(slots.Count, columns).ToList();
        var placed = new MapCell[slots.Count];
        void Take(MapSlot slot, Func<IEnumerable<MapCell>, IOrderedEnumerable<MapCell>> order)
        {
            for (var i = 0; i < slots.Count; i++)
            {
                if (slots[i] != slot) continue;
                var cell = order(free).First();
                free.Remove(cell);
                placed[i] = cell;
            }
        }
        Take(MapSlot.Cloud, cells => cells.OrderBy(c => c.Column).ThenBy(Cost));
        Take(MapSlot.Add, cells => cells.OrderBy(c => c.Column).ThenByDescending(Cost).ThenByDescending(c => c.Row));
        Take(MapSlot.More, cells => cells.OrderByDescending(Cost).ThenByDescending(c => c.Column));
        // The rest keep the order Cells gives (nearest first).
        Take(MapSlot.Device, cells => cells.OrderBy(_ => 0));
        return placed;
    }

    /// <summary>The canvas height the map needs: every row as tall as its tallest card, the gaps and a margin above and below.</summary>
    internal static double Height(Size hub, IReadOnlyList<(MapCell Cell, Size Size)> cards)
    {
        var rows = RowHeights(hub, cards);
        return rows.Values.Sum() + GapY * (rows.Count - 1) + 2 * Margin;
    }

    private static SortedDictionary<int, double> RowHeights(Size hub, IReadOnlyList<(MapCell Cell, Size Size)> cards)
    {
        var around = cards.Count == 0 ? 0 : cards.Max(c => Math.Abs(c.Cell.Row));
        var rows = new SortedDictionary<int, double>();
        for (var row = -around; row <= around; row++) rows[row] = row == 0 ? hub.Height : 0;
        foreach (var (cell, size) in cards) rows[cell.Row] = Math.Max(rows[cell.Row], size.Height);
        return rows;
    }

    /// <summary>The map on a canvas <paramref name="width"/> wide and <paramref name="height"/> high (at least
    /// <see cref="MinWidth"/> and <see cref="Height"/>): the centre of This PC, of each card, and each card's connection as a
    /// line through the gaps from This PC's edge to the card's edge (corners to round).</summary>
    internal static (Point Hub, Point[] Centers, Point[][] Routes) Place(double width, double height, Size hub,
        IReadOnlyList<(MapCell Cell, Size Size)> cards)
    {
        var columns = cards.Any(c => Math.Abs(c.Cell.Column) == 2) ? 4 : 2;
        width = Math.Max(width, MinWidth(columns));
        height = Math.Max(height, Height(hub, cards));
        var gapX = Math.Clamp((width - 2 * Margin - columns * CardWidth) / (columns - 1), MinGapX, MostGapX);
        var rows = RowHeights(hub, cards);
        var gapY = rows.Count == 1 ? 0 : Math.Clamp((height - 2 * Margin - rows.Values.Sum()) / (rows.Count - 1), GapY, MostGapY);
        var cx = width / 2;
        var y = (height - rows.Values.Sum() - gapY * (rows.Count - 1)) / 2;
        var middle = new Dictionary<int, double>();
        foreach (var (row, rowHeight) in rows)
        {
            middle[row] = y + rowHeight / 2;
            y += rowHeight + gapY;
        }
        double X(int column) => cx + Math.Sign(column) * (Math.Abs(column) == 1 ? gapX / 2 + CardWidth / 2 : gapX * 1.5 + CardWidth * 1.5);
        var cy = middle[0];
        var centers = new Point[cards.Count];
        var routes = new Point[cards.Count][];
        for (var i = 0; i < cards.Count; i++)
        {
            var (cell, size) = cards[i];
            var side = Math.Sign(cell.Column);
            var center = centers[i] = new(X(cell.Column), middle[cell.Row]);
            var edge = center.X - side * size.Width / 2;
            routes[i] = Math.Abs(cell.Column) == 1
                // The spine: up or down the gap between the inner columns, then into the card's inner side.
                ? [new(cx, cy + Math.Sign(cell.Row) * hub.Height / 2), new(cx, center.Y), new(edge, center.Y)]
                : cell.Row == 0
                    ? [new(cx + side * hub.Width / 2, cy), new(edge, cy)]
                    // The bus: out along This PC's row, up or down the gap between the inner and outer column, into the card.
                    : [new(cx + side * hub.Width / 2, cy), new(cx + side * (gapX + CardWidth), cy),
                        new(cx + side * (gapX + CardWidth), center.Y), new(edge, center.Y)];
        }
        return (new(cx, cy), centers, routes);
    }
}
