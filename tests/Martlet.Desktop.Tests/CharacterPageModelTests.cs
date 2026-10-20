using Martlet.Avatar.Hosting;
using Martlet.Avatars;

namespace Martlet.Desktop.Tests;

/// <summary>Companion › Character's page model (Pages\Character), without WPF.</summary>
public sealed class CharacterPageModelTests
{
    [Fact]
    public void CharacterNowAndButtonsFollowTheCharacter()
    {
        var hidden = new CharacterPageModel(new FakeCharacter { ModelName = "Mira", Renderer = AvatarRenderer.Vrm });
        Assert.Equal("Mira (Vrm), hidden.", hidden.NowText);
        Assert.Equal("Show character", hidden.ToggleLabel);
        Assert.False(hidden.ShowsResetPosition);
        Assert.False(hidden.ShowsLock);
        Assert.False(hidden.ShowsClickThrough);
        Assert.Null(hidden.ModelLine);
        Assert.StartsWith("Click-through is off", hidden.ClickThroughNote, StringComparison.Ordinal);

        // Hidden, the character can still be found again, unlocked and made to catch clicks.
        var lost = new CharacterPageModel(new FakeCharacter { ModelName = "Mira", HasPlacement = true, Locked = true, ClickThrough = true });
        Assert.Equal("Mira, hidden.", lost.NowText);
        Assert.True(lost.ShowsResetPosition);
        Assert.True(lost.ShowsLock);
        Assert.Equal("Unlock position", lost.LockLabel);
        Assert.True(lost.ShowsClickThrough);
        Assert.Equal("Turn off click-through", lost.ClickThroughLabel);
        Assert.StartsWith("Click-through is on", lost.ClickThroughNote, StringComparison.Ordinal);

        var shown = new CharacterPageModel(new FakeCharacter { ModelName = "Hiyori (built-in)", Renderer = AvatarRenderer.Live2D, Showing = true });
        Assert.Equal("Hiyori (built-in) (Live2D), on your desktop.", shown.NowText);
        Assert.Equal("Hide character", shown.ToggleLabel);
        Assert.True(shown.ShowsResetPosition && shown.ShowsLock && shown.ShowsClickThrough);
        Assert.Equal("Lock position", shown.LockLabel);
        Assert.Equal("Turn on click-through", shown.ClickThroughLabel);
    }

    private sealed class FakeCharacter : ICharacterPageServices
    {
        public string ModelName { get; init; } = "";
        public AvatarRenderer? Renderer { get; init; }
        public string? CleanupProblem { get; init; }
        public bool Showing { get; init; }
        public bool Locked { get; init; }
        public bool HasPlacement { get; init; }
        public bool ClickThrough { get; init; }
        public RendererCapabilities? Capabilities { get; init; }
        public void ToggleCharacter() { }
        public void OpenCharacter() { }
        public void ResetPosition() { }
        public void ResetZoom() { }
        public void SetLock(bool locked) { }
        public void SetClickThrough(bool on) { }
        public void Zoom(string action) { }
    }
}
