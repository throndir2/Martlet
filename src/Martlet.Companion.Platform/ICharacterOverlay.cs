namespace Martlet.Companion.Platform;

/// <summary>A native top-level window as Avalonia reports it (<c>TopLevel.TryGetPlatformHandle()</c>):
/// <see cref="Descriptor"/> is "XID" (X11 window id) on Linux, "NSWindow" on macOS, "HWND" on Windows.</summary>
public readonly record struct NativeWindow(nint Handle, string Descriptor);

/// <summary>A rectangle in the window's physical pixels, origin top-left.</summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height);

[Flags]
public enum OverlayBehavior
{
    None = 0,
    /// <summary>Above normal windows, including (where the system allows) borderless full-screen games.</summary>
    AlwaysOnTop = 1,
    /// <summary>Per-pixel transparent background (the app also sets Avalonia's transparency hint).</summary>
    Transparent = 2,
    /// <summary>Mouse input passes through everywhere except the regions given to
    /// <see cref="ICharacterOverlay.SetInteractiveRegions"/> (the character).</summary>
    ClickThroughExceptCharacter = 4,
    /// <summary>Clicking the character never takes focus from the game (macOS non-activating panel; X11
    /// _NET_WM_STATE_SKIP_TASKBAR plus no input focus hint).</summary>
    NonActivating = 8,
    /// <summary>macOS: visible on every Space and as a full-screen auxiliary window. Linux: sticky on all workspaces.</summary>
    AllSpacesAndFullScreen = 16,

    Character = AlwaysOnTop | Transparent | ClickThroughExceptCharacter | NonActivating | AllSpacesAndFullScreen
}

/// <summary>Turns the character window into an overlay. The app creates an Avalonia window (Topmost, transparent,
/// no decorations) and passes its native handle; the implementation applies what Avalonia cannot: click-through with
/// an interactive region, non-activating, all Spaces/workspaces. Linux: X11 or XWayland (XShape input region,
/// _NET_WM hints); native Wayland via layer-shell where supported. macOS: NSWindow level, collectionBehavior,
/// ignoresMouseEvents toggled by hit-testing. Call on the UI thread.</summary>
public interface ICharacterOverlay
{
    FeatureStatus Status { get; }

    /// <summary>Applies <paramref name="behavior"/>. Returns which behaviors could not be applied and why
    /// (Available=false when none beyond Avalonia's own topmost/transparency could be).</summary>
    FeatureStatus Attach(NativeWindow window, OverlayBehavior behavior);

    /// <summary>The parts of the window that take mouse input (the character's bounds); everything else clicks
    /// through. An empty list makes the whole window click-through.</summary>
    void SetInteractiveRegions(NativeWindow window, IReadOnlyList<PixelRect> regions);

    void Detach(NativeWindow window);
}
