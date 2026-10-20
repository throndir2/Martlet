using Martlet.Avatar.Hosting;
using Martlet.Avatars;

namespace Martlet.Desktop;

/// <summary>Everything Companion › Character (<see cref="CharacterPage"/> and <see cref="CharacterPageModel"/>) reads and starts in
/// the app. MainWindow gives it to the page (MainWindow.CharacterPage.cs) and keeps the work the page shows: the character on the
/// desktop (showing, hiding, placing, zooming and click-through, in MainWindow.xaml.cs), your characters and VRoid Hub. It has no
/// WPF types.</summary>
internal interface ICharacterPageServices
{
    // ---------- what the page reads ----------

    /// <summary>The name of the character this PC shows.</summary>
    string ModelName { get; }

    /// <summary>The renderer of this PC's saved character, or null before it is read.</summary>
    AvatarRenderer? Renderer { get; }

    /// <summary>Why the character didn't close cleanly, or null.</summary>
    string? CleanupProblem { get; }

    /// <summary>Whether the character shows on the desktop.</summary>
    bool Showing { get; }

    /// <summary>Whether the character's position is locked.</summary>
    bool Locked { get; }

    /// <summary>Whether a place for the character is saved.</summary>
    bool HasPlacement { get; }

    /// <summary>Whether clicks pass through the character.</summary>
    bool ClickThrough { get; }

    /// <summary>What the showing model drives, or null.</summary>
    RendererCapabilities? Capabilities { get; }

    // ---------- what the page does ----------

    /// <summary>Shows or hides the character.</summary>
    void ToggleCharacter();

    /// <summary>Opens the character settings window (Choose and customize).</summary>
    void OpenCharacter();

    /// <summary>Puts the character back at its first place.</summary>
    void ResetPosition();

    /// <summary>Resets the character's zoom.</summary>
    void ResetZoom();

    /// <summary>Locks or unlocks the character's position.</summary>
    void SetLock(bool locked);

    /// <summary>Turns click-through on or off.</summary>
    void SetClickThrough(bool on);

    /// <summary>Zooms the character "in", "out", or only reads its "status", then shows the view on the page's view line.</summary>
    void Zoom(string action);
}
