using Martlet.Companion.Platform;
using static Martlet.Platform.Linux.Native.Xlib;

namespace Martlet.Platform.Linux.Hotkeys;

/// <summary>Turns a <see cref="HotkeyGesture"/> (Avalonia key names) into what Linux understands: an X keysym name plus a
/// modifier mask for XGrabKey, and an XDG shortcut trigger ("CTRL+ALT+space") for the GlobalShortcuts portal.</summary>
internal static class LinuxKeys
{
    private static readonly Dictionary<string, string> Named = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Space"] = "space", ["Pause"] = "Pause", ["ScrollLock"] = "Scroll_Lock", ["Scroll"] = "Scroll_Lock",
        ["Insert"] = "Insert", ["Home"] = "Home", ["End"] = "End", ["PageUp"] = "Prior", ["Prior"] = "Prior",
        ["PageDown"] = "Next", ["Next"] = "Next", ["Delete"] = "Delete", ["Back"] = "BackSpace", ["Backspace"] = "BackSpace",
        ["Tab"] = "Tab", ["Enter"] = "Return", ["Return"] = "Return", ["Escape"] = "Escape", ["Up"] = "Up", ["Down"] = "Down",
        ["Left"] = "Left", ["Right"] = "Right", ["PrintScreen"] = "Print", ["Print"] = "Print", ["Snapshot"] = "Print",
        ["Apps"] = "Menu", ["Menu"] = "Menu", ["CapsLock"] = "Caps_Lock", ["Capital"] = "Caps_Lock", ["NumLock"] = "Num_Lock",
        ["Multiply"] = "KP_Multiply", ["Add"] = "KP_Add", ["Subtract"] = "KP_Subtract", ["Divide"] = "KP_Divide",
        ["Decimal"] = "KP_Decimal", ["OemTilde"] = "grave", ["Oem3"] = "grave", ["OemMinus"] = "minus", ["OemPlus"] = "equal",
        ["OemComma"] = "comma", ["OemPeriod"] = "period", ["OemQuestion"] = "slash", ["OemSemicolon"] = "semicolon",
        ["OemQuotes"] = "apostrophe", ["OemOpenBrackets"] = "bracketleft", ["OemCloseBrackets"] = "bracketright",
        ["OemPipe"] = "backslash", ["OemBackslash"] = "backslash"
    };

    /// <summary>The X keysym name, or null for a key Linux can't grab.</summary>
    public static string? Keysym(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;
        key = key.Trim();
        if (Named.TryGetValue(key, out var named)) return named;
        if (key.Length == 1 && char.IsAsciiLetter(key[0])) return key.ToLowerInvariant();
        if (key.Length == 1 && char.IsAsciiDigit(key[0])) return key;
        if (key.Length == 2 && key[0] is 'D' or 'd' && char.IsAsciiDigit(key[1])) return key[1..];
        if (key.Length is 2 or 3 && key[0] is 'F' or 'f' && int.TryParse(key[1..], out var f) && f is >= 1 and <= 35) return "F" + f;
        if (key.StartsWith("NumPad", StringComparison.OrdinalIgnoreCase) && key.Length == 7 && char.IsAsciiDigit(key[6])) return "KP_" + key[6];
        return null;
    }

    public static uint X11Mask(HotkeyModifiers modifiers) =>
        (modifiers.HasFlag(HotkeyModifiers.Control) ? ControlMask : 0) | (modifiers.HasFlag(HotkeyModifiers.Alt) ? Mod1Mask : 0) |
        (modifiers.HasFlag(HotkeyModifiers.Shift) ? ShiftMask : 0) | (modifiers.HasFlag(HotkeyModifiers.Meta) ? Mod4Mask : 0);

    /// <summary>XGrabKey matches modifiers exactly, so grab the gesture with and without Caps Lock and Num Lock.</summary>
    public static uint[] GrabMasks(uint mask) => [mask, mask | LockMask, mask | Mod2Mask, mask | LockMask | Mod2Mask];

    /// <summary>The XDG shortcuts-spec trigger for the portal's preferred_trigger, for example "CTRL+ALT+space" or "F8".</summary>
    public static string? PortalTrigger(HotkeyGesture gesture)
    {
        var keysym = Keysym(gesture.Key);
        if (keysym is null) return null;
        var parts = new List<string>();
        if (gesture.Modifiers.HasFlag(HotkeyModifiers.Control)) parts.Add("CTRL");
        if (gesture.Modifiers.HasFlag(HotkeyModifiers.Alt)) parts.Add("ALT");
        if (gesture.Modifiers.HasFlag(HotkeyModifiers.Shift)) parts.Add("SHIFT");
        if (gesture.Modifiers.HasFlag(HotkeyModifiers.Meta)) parts.Add("LOGO");
        parts.Add(keysym);
        return string.Join('+', parts);
    }
}
