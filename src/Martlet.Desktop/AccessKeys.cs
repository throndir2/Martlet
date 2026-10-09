using System.Windows;
using System.Windows.Input;

namespace Martlet.Desktop;

/// <summary>An underlined letter (an access key) presses its control only while Alt is held, as in other Windows apps. Without
/// this, WPF presses it on the plain letter whenever no text box has the keyboard, so a stray key could exit Martlet or delete
/// something. Enter and Esc still press a window's default and cancel buttons, and an open menu (which handles its own keys)
/// still takes plain letters.</summary>
internal static class AccessKeys
{
    private static bool requireAlt;

    /// <summary>Once per process, before the first window shows.</summary>
    internal static void RequireAlt()
    {
        if (requireAlt) return;
        requireAlt = true;
        EventManager.RegisterClassHandler(typeof(Window), AccessKeyManager.AccessKeyPressedEvent,
            new AccessKeyPressedEventHandler(OnAccessKeyPressed));
    }

    private static void OnAccessKeyPressed(object sender, AccessKeyPressedEventArgs e)
    {
        if (e.Key is "\r" or "\u001b" || (Keyboard.Modifiers & ModifierKeys.Alt) == ModifierKeys.Alt) return;
        e.Target = null;
        e.Handled = true;
    }
}
