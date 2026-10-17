using System.Windows;
using Martlet.Desktop;

namespace Martlet.Desktop.Tests;

public sealed class MapLayoutTests
{
    private static readonly Size Card = new(204, 85), Compact = new(176, 58), Hub = new(220, 95);

    [Theory]
    [InlineData(1, 900, false)]
    [InlineData(2, 900, false)]
    [InlineData(6, 900, false)]
    [InlineData(8, 900, false)]
    [InlineData(14, 760, true)]
    [InlineData(14, 1000, true)]
    [InlineData(14, 1600, true)]
    [InlineData(19, 900, true)]
    public void No_card_touches_another_or_this_pc(int count, double width, bool compact)
    {
        var card = compact ? Compact : Card;
        var layout = MapLayout.Place(count, width, card, Hub);
        var points = layout.Points;
        Assert.Equal(count, points.Count);
        for (var i = 0; i < points.Count; i++)
        {
            Assert.True(Math.Abs(points[i].X) >= (card.Width + Hub.Width) / 2 || Math.Abs(points[i].Y) >= (card.Height + Hub.Height) / 2);
            Assert.True(Math.Abs(points[i].X) + card.Width / 2 <= width / 2);
            Assert.True(Math.Abs(points[i].Y) + card.Height / 2 <= layout.Height / 2);
            for (var j = i + 1; j < points.Count; j++)
                Assert.True(Math.Abs(points[i].X - points[j].X) >= card.Width || Math.Abs(points[i].Y - points[j].Y) >= card.Height);
            // The connection to this card runs through no other card.
            var end = MapLayout.Nearest(points[i], card, new(0, 0));
            for (var j = 0; j < points.Count; j++)
            {
                if (j == i) continue;
                for (var s = 0; s <= 400; s++)
                {
                    var p = new Point(end.X * s / 400, end.Y * s / 400);
                    Assert.False(Math.Abs(p.X - points[j].X) < card.Width / 2 - 0.5 && Math.Abs(p.Y - points[j].Y) < card.Height / 2 - 0.5,
                        $"The line to device {i} runs through device {j}.");
                }
            }
        }
    }

    [Fact]
    public void A_few_devices_stand_on_both_sides_of_this_pc()
    {
        var layout = MapLayout.Place(3, 900, Card, Hub);
        Assert.Equal(2, layout.Points.Count(p => p.X > 0));
        Assert.Equal(1, layout.Points.Count(p => p.X < 0));
        Assert.Equal(MapLayout.MinimumHeight, MapLayout.Place(2, 900, Card, Hub).Height);
    }

    [Fact]
    public void Twelve_hosts_stand_in_a_low_ring()
    {
        // This PC, 12 hosts, one cloud service and Add a computer.
        var layout = MapLayout.Place(14, 1000, Compact, Hub);
        Assert.StartsWith("ring", layout.Shape);
        Assert.True(layout.Height <= 320, $"Height {layout.Height}");
        Assert.True(layout.Points.Count(p => p.Y < 0 && Math.Abs(p.X) < 300) >= 2, "Devices stand above This PC too.");
    }

    [Fact]
    public void A_connection_leaves_a_card_at_its_edge()
    {
        var side = MapLayout.Edge(new(0, 0), Hub, new(400, 0));
        Assert.Equal(110, side.X, 6);
        Assert.Equal(-47.5, MapLayout.Edge(new(0, 0), Hub, new(0, -300)).Y, 6);
    }
}
