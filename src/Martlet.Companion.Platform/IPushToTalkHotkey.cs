namespace Martlet.Companion.Platform;

[Flags]
public enum HotkeyModifiers { None = 0, Control = 1, Alt = 2, Shift = 4, Meta = 8 }

/// <summary>A global key combination. <see cref="Key"/> uses Avalonia/W3C-style key names: "F1".."F24", "A".."Z",
/// "D0".."D9", "Space", "Pause", "ScrollLock", "Insert" and so on.</summary>
public sealed record HotkeyGesture(string Key, HotkeyModifiers Modifiers = HotkeyModifiers.None)
{
    public static HotkeyGesture Default { get; } = new("F8");

    public override string ToString() =>
        string.Concat(Enum.GetValues<HotkeyModifiers>().Where(m => m != HotkeyModifiers.None && Modifiers.HasFlag(m))
            .Select(m => m + "+")) + Key;
}

/// <summary>A system-wide push-to-talk key that works while a game or another app has focus. Hold to talk:
/// <see cref="Pressed"/> on key down, <see cref="Released"/> on key up (auto-repeat is swallowed, one Pressed per
/// hold). Linux: XGrabKey on X11, the GlobalShortcuts portal on Wayland (the portal reports activate/deactivate).
/// macOS: Carbon RegisterEventHotKey (no permission needed). Events may arrive on any thread.</summary>
public interface IPushToTalkHotkey : IDisposable
{
    FeatureStatus Status { get; }

    /// <summary>Registers (or replaces) the gesture. May show a system confirmation (Wayland portal). Returns why it
    /// failed, for example "F8 is already taken by another app".</summary>
    Task<FeatureStatus> RegisterAsync(HotkeyGesture gesture, CancellationToken cancellationToken);

    void Unregister();

    event EventHandler? Pressed;
    event EventHandler? Released;
}
