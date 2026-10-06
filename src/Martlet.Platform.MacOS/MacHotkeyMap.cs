using System.Collections.Frozen;
using Martlet.Companion.Platform;

namespace Martlet.Platform.MacOS;

/// <summary>Translates the companion's <see cref="HotkeyGesture"/> (Avalonia key names; Meta = Command, Alt = Option) to a
/// Carbon virtual key code and modifier mask for <c>RegisterEventHotKey</c>, and applies macOS's rules. Pure, unit tested.</summary>
public static class MacHotkeyMap
{
    public const uint CmdKey = 0x0100, ShiftKey = 0x0200, OptionKey = 0x0800, ControlKey = 0x1000;

    /// <summary>A good push-to-talk combination on a Mac: F-keys double as media keys on Mac keyboards (F8 is play/pause
    /// unless fn is held), and Control+Space / Control+Option+Space switch input sources.</summary>
    public static HotkeyGesture Suggested { get; } = new("T", HotkeyModifiers.Control | HotkeyModifiers.Alt);

    /// <summary>Maps <paramref name="gesture"/>, or explains why macOS can't use it as a global push-to-talk key.</summary>
    public static bool TryMap(HotkeyGesture gesture, out uint keyCode, out uint modifiers, out string problem)
    {
        keyCode = modifiers = 0;
        problem = "";
        var key = Normalize(gesture.Key);
        if (key is null || !KeyCodes.TryGetValue(key, out keyCode))
        {
            problem = $"{gesture.Key} can't be a push-to-talk key on a Mac; choose a letter, digit, F-key or Space with Control or Command.";
            return false;
        }
        if (gesture.Modifiers.HasFlag(HotkeyModifiers.Control)) modifiers |= ControlKey;
        if (gesture.Modifiers.HasFlag(HotkeyModifiers.Alt)) modifiers |= OptionKey;
        if (gesture.Modifiers.HasFlag(HotkeyModifiers.Shift)) modifiers |= ShiftKey;
        if (gesture.Modifiers.HasFlag(HotkeyModifiers.Meta)) modifiers |= CmdKey;
        var functionKey = key.Length > 1 && key[0] == 'F' && char.IsAsciiDigit(key[1]);
        if ((modifiers & (CmdKey | ControlKey)) == 0 && (modifiers != 0 || !functionKey))
        {
            problem = modifiers == 0
                ? $"{gesture} alone would stop working as {gesture.Key} in every app; add Control or Command (for example {Suggested})."
                : $"macOS doesn't allow global shortcuts made only of Option or Shift ({gesture}); add Control or Command.";
            return false;
        }
        return true;
    }

    /// <summary>A note for the settings page when the gesture works but needs care on a Mac.</summary>
    public static string Note(HotkeyGesture gesture) =>
        Normalize(gesture.Key) is { } key && key.Length > 1 && key[0] == 'F' && char.IsAsciiDigit(key[1])
            ? $"On a Mac keyboard, hold fn with {key} unless System Settings > Keyboard uses F1, F2 and so on as standard function keys."
            : "";

    private static string? Normalize(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;
        key = key.Trim();
        if (key.Length == 1 && char.IsAsciiLetter(key[0])) return key.ToUpperInvariant();
        if (key.Length == 1 && char.IsAsciiDigit(key[0])) return "D" + key;
        foreach (var (alias, canonical) in Aliases)
            if (string.Equals(alias, key, StringComparison.OrdinalIgnoreCase)) return canonical;
        return key.Length > 1 && (key[0] is 'f' or 'F') && int.TryParse(key.AsSpan(1), out _) ? "F" + key[1..] : key;
    }

    private static readonly (string Alias, string Canonical)[] Aliases =
    [
        ("Enter", "Return"), ("Esc", "Escape"), ("Back", "Backspace"), ("Prior", "PageUp"), ("Next", "PageDown"),
        ("Oem3", "OemTilde"), ("Grave", "OemTilde"), ("Backquote", "OemTilde"), ("`", "OemTilde"),
        ("-", "OemMinus"), ("Minus", "OemMinus"), ("=", "OemPlus"), ("Equal", "OemPlus"), (",", "OemComma"), ("Comma", "OemComma"),
        (".", "OemPeriod"), ("Period", "OemPeriod"), ("/", "OemQuestion"), ("Slash", "OemQuestion"), ("Oem2", "OemQuestion"),
        ("\\", "OemPipe"), ("Backslash", "OemPipe"), ("Oem5", "OemPipe"), (";", "OemSemicolon"), ("Oem1", "OemSemicolon"),
        ("'", "OemQuotes"), ("Oem7", "OemQuotes"), ("[", "OemOpenBrackets"), ("Oem4", "OemOpenBrackets"),
        ("]", "OemCloseBrackets"), ("Oem6", "OemCloseBrackets"), ("ArrowLeft", "Left"), ("ArrowRight", "Right"),
        ("ArrowUp", "Up"), ("ArrowDown", "Down"), ("Insert", "Help")
    ];

    // Carbon virtual key codes (HIToolbox Events.h kVK_*), ANSI layout positions.
    private static readonly FrozenDictionary<string, uint> KeyCodes = new Dictionary<string, uint>
    {
        ["A"] = 0x00, ["S"] = 0x01, ["D"] = 0x02, ["F"] = 0x03, ["H"] = 0x04, ["G"] = 0x05, ["Z"] = 0x06, ["X"] = 0x07,
        ["C"] = 0x08, ["V"] = 0x09, ["B"] = 0x0B, ["Q"] = 0x0C, ["W"] = 0x0D, ["E"] = 0x0E, ["R"] = 0x0F, ["Y"] = 0x10,
        ["T"] = 0x11, ["D1"] = 0x12, ["D2"] = 0x13, ["D3"] = 0x14, ["D4"] = 0x15, ["D6"] = 0x16, ["D5"] = 0x17, ["OemPlus"] = 0x18,
        ["D9"] = 0x19, ["D7"] = 0x1A, ["OemMinus"] = 0x1B, ["D8"] = 0x1C, ["D0"] = 0x1D, ["OemCloseBrackets"] = 0x1E, ["O"] = 0x1F,
        ["U"] = 0x20, ["OemOpenBrackets"] = 0x21, ["I"] = 0x22, ["P"] = 0x23, ["L"] = 0x25, ["J"] = 0x26, ["OemQuotes"] = 0x27,
        ["K"] = 0x28, ["OemSemicolon"] = 0x29, ["OemPipe"] = 0x2A, ["OemComma"] = 0x2B, ["OemQuestion"] = 0x2C, ["N"] = 0x2D,
        ["M"] = 0x2E, ["OemPeriod"] = 0x2F, ["OemTilde"] = 0x32,
        ["Return"] = 0x24, ["Tab"] = 0x30, ["Space"] = 0x31, ["Backspace"] = 0x33, ["Escape"] = 0x35,
        ["F1"] = 0x7A, ["F2"] = 0x78, ["F3"] = 0x63, ["F4"] = 0x76, ["F5"] = 0x60, ["F6"] = 0x61, ["F7"] = 0x62, ["F8"] = 0x64,
        ["F9"] = 0x65, ["F10"] = 0x6D, ["F11"] = 0x67, ["F12"] = 0x6F, ["F13"] = 0x69, ["F14"] = 0x6B, ["F15"] = 0x71,
        ["F16"] = 0x6A, ["F17"] = 0x40, ["F18"] = 0x4F, ["F19"] = 0x50, ["F20"] = 0x5A,
        ["Help"] = 0x72, ["Home"] = 0x73, ["PageUp"] = 0x74, ["Delete"] = 0x75, ["End"] = 0x77, ["PageDown"] = 0x79,
        ["Left"] = 0x7B, ["Right"] = 0x7C, ["Down"] = 0x7D, ["Up"] = 0x7E
    }.ToFrozenDictionary(StringComparer.Ordinal);
}
