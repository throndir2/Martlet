using System.Windows.Media;
using Martlet.Avatar.Hosting;
using Martlet.Desktop;

namespace Martlet.Desktop.Tests;

public sealed class TouchZoneLabelColorsTests
{
    [Theory]
    [InlineData("#E94F64")]
    [InlineData("#3B82F6")]
    [InlineData("#10B981")]
    [InlineData("#F59E0B")]
    [InlineData("#8B5CF6")]
    [InlineData("#EC4899")]
    [InlineData("#06B6D4")]
    [InlineData("#84CC16")]
    public void Every_zone_label_is_readable_on_its_zone_color(string zone)
    {
        var color = (Color)ColorConverter.ConvertFromString(zone);
        var text = TouchZoneLabelColors.TextOn(color);
        Assert.True(ThemeColor.Contrast(zone, $"#{text.R:X2}{text.G:X2}{text.B:X2}") >= 4.5);
    }
}
