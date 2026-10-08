using System.IO;
using Martlet.Desktop;

namespace Martlet.Desktop.Tests;

public sealed class CharacterClickThroughStoreTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "martlet-click-through-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }

    [Fact]
    public void ClickThroughIsOffWithoutAFile() => Assert.False(CharacterClickThroughStore.Load(directory));

    [Fact]
    public void TurningClickThroughOnIsRememberedAndTurningItOffForgetsIt()
    {
        Assert.True(CharacterClickThroughStore.Save(directory, true));
        Assert.True(CharacterClickThroughStore.Load(directory));
        Assert.Equal("""{"ClickThrough":true}""", File.ReadAllText(Path.Combine(directory, CharacterClickThroughStore.FileName)));

        Assert.True(CharacterClickThroughStore.Save(directory, false));
        Assert.False(File.Exists(Path.Combine(directory, CharacterClickThroughStore.FileName)));
        Assert.False(CharacterClickThroughStore.Load(directory));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"ClickThrough":false}""")]
    [InlineData("null")]
    public void UnreadableOrOffFilesCatchClicks(string text)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, CharacterClickThroughStore.FileName), text);
        Assert.False(CharacterClickThroughStore.Load(directory));
    }

    [Fact]
    public void ResettingThePositionLeavesClickThroughAlone()
    {
        Assert.True(CharacterClickThroughStore.Save(directory, true));
        Assert.True(CharacterPlacementStore.Save(directory, null));
        Assert.True(CharacterClickThroughStore.Load(directory));
    }

    [Fact]
    public void WithoutADataDirectoryNothingIsSaved()
    {
        Assert.False(CharacterClickThroughStore.Save(null, true));
        Assert.False(CharacterClickThroughStore.Load(null));
    }
}
