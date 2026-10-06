namespace Martlet.Platform.MacOS;

/// <summary>A rectangle in window points with a top-left origin, as Avalonia lays out the character window.</summary>
public readonly record struct OverlayRect(double X, double Y, double Width, double Height)
{
    public bool Contains(double x, double y) => Width > 0 && Height > 0 && x >= X && x < X + Width && y >= Y && y < Y + Height;
}

/// <summary>The window settings and click-through math for the character overlay; no native calls, so it is unit tested.</summary>
public static class MacOverlayRules
{
    /// <summary>NSFloatingWindowLevel: above normal app windows, below menus and alerts.</summary>
    public const long FloatingLevel = 3;

    /// <summary>NSWindowCollectionBehavior: canJoinAllSpaces (1) | stationary (16) | ignoresCycle (64) |
    /// fullScreenAuxiliary (256). On every Space, beside full-screen games, untouched by Mission Control and Cmd+`.</summary>
    public const ulong CollectionBehavior = 1 | 16 | 64 | 256;

    /// <summary>Whether the mouse, in Cocoa screen coordinates (bottom-left origin, points), is over one of the character's
    /// <paramref name="regions"/> inside a window whose Cocoa frame is (<paramref name="frameX"/>, <paramref name="frameY"/>,
    /// <paramref name="frameWidth"/>, <paramref name="frameHeight"/>). Everywhere else the overlay lets clicks through.</summary>
    public static bool IsOverCharacter(IReadOnlyList<OverlayRect> regions, double frameX, double frameY, double frameWidth,
        double frameHeight, double mouseX, double mouseY)
    {
        var x = mouseX - frameX;
        var fromBottom = mouseY - frameY;
        if (x < 0 || x >= frameWidth || fromBottom < 0 || fromBottom >= frameHeight) return false;
        var y = frameHeight - fromBottom;
        foreach (var region in regions)
            if (region.Contains(x, y)) return true;
        return false;
    }

    /// <summary>Whether the overlay should take the mouse: over the character, or still holding a press that started there
    /// (dragging the character to move it must not drop through to the game mid-drag).</summary>
    public static bool TakesMouse(bool overCharacter, bool buttonDown, bool takingNow) => overCharacter || buttonDown && takingNow;

    /// <summary>Avalonia reports regions in physical pixels; AppKit works in points (pixels / backingScaleFactor).</summary>
    public static OverlayRect ToPoints(Martlet.Companion.Platform.PixelRect pixels, double scale)
    {
        if (scale <= 0 || double.IsNaN(scale)) scale = 1;
        return new(pixels.X / scale, pixels.Y / scale, pixels.Width / scale, pixels.Height / scale);
    }

    /// <summary>What <see cref="Martlet.Companion.Platform.ICharacterOverlay.Attach"/> reports: all behaviors are applied
    /// on a Mac, so only a missing handle or wrong descriptor fails.</summary>
    public static string? AttachProblem(nint handle, string descriptor) =>
        handle == 0 ? "The character window has no native handle yet; attach it after the window is shown."
        : !string.Equals(descriptor, "NSWindow", StringComparison.Ordinal)
            ? $"The character window handle is a {descriptor}, not an NSWindow; Martlet can't make it an overlay."
            : null;
}
