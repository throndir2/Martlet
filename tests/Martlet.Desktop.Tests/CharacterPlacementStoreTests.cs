using System.IO;
using Martlet.Avatar.Hosting;
using Martlet.Desktop;

namespace Martlet.Desktop.Tests;

public sealed class CharacterPlacementStoreTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "martlet-placement-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }

    [Fact]
    public void UnlockedPlacementWithItsMonitorIsRemembered()
    {
        var place = new RendererPlacement(false, 2120.5, 400, 420, 560, @"\\.\DISPLAY2", 200.5, 360);
        Assert.True(CharacterPlacementStore.Save(directory, place));
        Assert.Equal(place, CharacterPlacementStore.Load(directory));
    }

    [Fact]
    public void LockedPlacementSavedBeforeMonitorsWereRememberedStillLoads()
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, CharacterPlacementStore.FileName),
            """{"Locked":true,"Left":10,"Top":20,"Width":420,"Height":560}""");
        Assert.Equal(new RendererPlacement(true, 10, 20, 420, 560), CharacterPlacementStore.Load(directory));
    }

    [Fact]
    public void ResettingForgetsThePlace()
    {
        Assert.True(CharacterPlacementStore.Save(directory, new RendererPlacement(true, 10, 20, 420, 560, @"\\.\DISPLAY1", 10, 20)));
        Assert.True(CharacterPlacementStore.Save(directory, null));
        Assert.Null(CharacterPlacementStore.Load(directory));
    }

    [Theory]
    [InlineData("")]
    [InlineData("DISPLAY\n1")]
    public void UnusableMonitorNamesAreRejected(string screen) =>
        Assert.False(new RendererPlacement(false, 0, 0, 420, 560, screen, 0, 0).IsValid);

    [Fact]
    public void ScreenLabelDropsTheDevicePrefix() =>
        Assert.Equal("DISPLAY2", MainWindow.CharacterScreen(@"\\.\DISPLAY2"));
}
