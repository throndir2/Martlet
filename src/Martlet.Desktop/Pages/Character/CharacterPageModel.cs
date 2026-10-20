namespace Martlet.Desktop;

/// <summary>Companion › Character's decisions, without WPF: the Now line, which buttons show and what they and the click-through
/// note say. <see cref="CharacterPage"/> makes it again each time it draws.</summary>
internal sealed class CharacterPageModel(ICharacterPageServices services)
{
    /// <summary>Whether the character shows on the desktop, read once when the page draws.</summary>
    internal bool Showing { get; } = services.Showing;

    /// <summary>Whether the character's position is locked, read once when the page draws.</summary>
    internal bool Locked { get; } = services.Locked;

    /// <summary>The Now line: the character's name, its renderer and whether it shows.</summary>
    internal string NowText => services.ModelName + (services.Renderer is { } renderer ? $" ({renderer})" : "") +
        (Showing ? ", on your desktop." : ", hidden.");

    internal string ToggleLabel => Showing ? "Hide character" : "Show character";

    // Reset stays available while hidden once a place is saved, in case the character got lost off screen.
    internal bool ShowsResetPosition => Showing || services.HasPlacement;

    // Unlocking also works here while the character is hidden.
    internal bool ShowsLock => Showing || Locked;

    internal string LockLabel => Locked ? "Unlock position" : "Lock position";

    // The same goes for click-through: the mouse can't reach the character to turn it off.
    internal bool ShowsClickThrough => Showing || services.ClickThrough;

    internal string ClickThroughLabel => services.ClickThrough ? "Turn off click-through" : "Turn on click-through";

    internal string ClickThroughNote => services.ClickThrough
        ? "Click-through is on: clicks pass through the character to the windows under it, so you can't drag, zoom or right-click it. Turn it off here or from Martlet's icon in the notification area."
        : "Click-through is off: the character catches clicks. Turn it on here or from the character's right-click menu to let clicks pass through it, for example while you play a game.";

    /// <summary>What the showing model drives: textures (and any downscaling), blinking, mouth, motions and physics; null while
    /// it is hidden or not loaded.</summary>
    internal string? ModelLine => Showing && services.Capabilities is { } loaded ? "Model: " + AvatarRendererProcess.Describe(loaded) : null;
}
