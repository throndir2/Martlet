using System.Windows;
using Martlet.Desktop;

namespace Martlet.Desktop.Tests;

public sealed class MapLayoutTests
{
    private static Rect Box(Point center, Size size) => new(center.X - size.Width / 2, center.Y - size.Height / 2, size.Width, size.Height);

    [Fact]
    public void Cards_never_overlap_and_connections_run_only_through_the_gaps()
    {
        var random = new Random(7);
        foreach (var columns in new[] { 4, 2 })
        foreach (var count in Enumerable.Range(1, 40))
        foreach (var (width, height) in new[] { (500.0, 300.0), (700.0, 380.0), (1046.0, 2400.0), (1600.0, 900.0) })
        {
            var hub = new Size(MapLayout.HubWidth, random.Next(80, 180));
            var slots = Enumerable.Range(0, count).Select(i => i % 9 == 4 ? MapSlot.Cloud : i == count - 1 ? MapSlot.Add : MapSlot.Device).ToArray();
            var cells = MapLayout.Assign(slots, columns);
            Assert.Equal(count, cells.Distinct().Count());
            Assert.All(cells, cell => Assert.True(Math.Abs(cell.Column) <= columns / 2 && cell.Column != 0));
            var cards = cells.Select(cell => (cell, new Size(MapLayout.CardWidth, random.Next(60, 200)))).ToArray();
            var (center, centers, routes) = MapLayout.Place(width, height, hub, cards);
            var canvas = new Rect(0, 0, Math.Max(width, MapLayout.MinWidth(columns)), Math.Max(height, MapLayout.Height(hub, cards)));
            var boxes = centers.Select((c, i) => Box(c, cards[i].Item2)).Prepend(Box(center, hub)).ToArray();
            for (var i = 0; i < boxes.Length; i++)
            {
                Assert.True(canvas.Contains(boxes[i]), $"{count} devices on {width}x{height}: card {i} leaves the map");
                for (var j = i + 1; j < boxes.Length; j++)
                    Assert.False(boxes[i].IntersectsWith(boxes[j]) && Rect.Intersect(boxes[i], boxes[j]) is { Width: > 0, Height: > 0 },
                        $"{count} devices on {width}x{height}: cards {i} and {j} overlap");
            }
            for (var i = 0; i < routes.Length; i++)
            {
                for (var s = 1; s < routes[i].Length; s++)
                {
                    var (a, b) = (routes[i][s - 1], routes[i][s]);
                    Assert.True(a.X == b.X || a.Y == b.Y, "connections run straight through the gaps");
                    var line = new Rect(a, b);
                    // Never through another card (inset by a pixel: a connection starts on This PC's edge and ends on its card's).
                    for (var k = 0; k < boxes.Length; k++)
                    {
                        if (k == i + 1) continue;
                        var inner = boxes[k];
                        inner.Inflate(-1, -1);
                        Assert.False(inner.IntersectsWith(line), $"{count} devices: connection {i} runs under card {k}");
                    }
                }
            }
        }
    }

    [Fact]
    public void Devices_fill_the_places_around_this_pc_nearest_first_with_cloud_services_on_the_left()
    {
        Assert.Equal([new MapCell(2, 0), new MapCell(-2, 0)], MapLayout.Cells(2));
        Assert.Equal([new(2, 0), new(-2, 0), new(1, -1), new(1, 1), new(-1, -1), new(-1, 1)], MapLayout.Cells(6));
        Assert.Equal(0, MapLayout.RowsAround(2));
        Assert.Equal(1, MapLayout.RowsAround(10));
        Assert.Equal(2, MapLayout.RowsAround(12));
        Assert.Equal(4, MapLayout.RowsAround(34));

        // The owner's network: a job nobody does, four computers and Add a computer.
        var cells = MapLayout.Assign([MapSlot.Cloud, MapSlot.Device, MapSlot.Device, MapSlot.Device, MapSlot.Device, MapSlot.Add]);
        Assert.Equal(new MapCell(-2, 0), cells[0]);
        Assert.Equal(new MapCell(-1, 1), cells[5]);
        Assert.Equal([new(2, 0), new(1, -1), new(1, 1), new(-1, -1)], cells[1..5]);
        // Thirty-four cards take nine rows; the "more" card takes the farthest place.
        var many = MapLayout.Assign([.. Enumerable.Repeat(MapSlot.Device, 32), MapSlot.More, MapSlot.Add]);
        Assert.Equal(4, Math.Abs(many[32].Row));
        Assert.Equal(34, many.Distinct().Count());
        // A narrow map: two columns, two devices in every row above and below This PC, none beside it.
        Assert.Equal(4, MapLayout.Columns(0));
        Assert.Equal(4, MapLayout.Columns(1046));
        Assert.Equal(2, MapLayout.Columns(640));
        Assert.Equal([new(1, -1), new(1, 1), new(-1, -1), new(-1, 1), new(1, -2)], MapLayout.Cells(5, columns: 2));
        Assert.Equal(3, MapLayout.RowsAround(12, columns: 2));
    }
}
