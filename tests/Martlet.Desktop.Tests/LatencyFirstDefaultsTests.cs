using Martlet.Desktop;

namespace Martlet.Desktop.Tests;

public sealed class LatencyFirstDefaultsTests
{
    [Fact]
    public void TheRecommendedLocalModelIsTheFastestOnEveryCardAndTheLargestThatFitsIsOfferedBeside()
    {
        foreach (var vram in new double?[] { null, 8, 11.6, 16, 24 })
            Assert.Equal("gemma4:e2b", MainWindow.RecommendedLocalModel(vram).Id);
        Assert.Equal("gemma4:e2b", MainWindow.LargestLocalModel(8).Id);
        Assert.Equal("gemma4:e4b", MainWindow.LargestLocalModel(11.6).Id);
        Assert.Equal("gemma4:12b", MainWindow.LargestLocalModel(16).Id);
        Assert.Equal("gemma4:26b", MainWindow.LargestLocalModel(24).Id);
    }
}
