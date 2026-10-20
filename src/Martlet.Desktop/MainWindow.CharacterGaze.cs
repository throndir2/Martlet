using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Martlet.Avatar.Hosting;

namespace Martlet.Desktop;

/// <summary>Where the character looks: the character's right-click Eyes menu and the line on Companion › Eyes (the card is in
/// Pages\Eyes\EyesPage.cs). Its usual gaze (as the personality decides, the default, or your choice: your mouse, your mouse when
/// it's near, straight ahead or the window you're using) and whether the character may change where it looks in its replies.
/// Saved on this PC in talk-preferences.json.</summary>
public partial class MainWindow
{
    private void WireCharacterGaze() => avatar.Gaze.Changed += () => Dispatcher.InvokeAsync(() => eyesPage?.ShowGaze());

    /// <summary>A choice from the character's Eyes menu: its usual gaze (<c>look-personality</c>, <c>look-mouse</c>...) or whether
    /// it may change where it looks (<c>look-free-on</c>, <c>look-free-off</c>). Saved like the same choice on Companion ›
    /// Character, which the overlay then shows checked.</summary>
    private void ChooseCharacterGaze(string action)
    {
        if (action == RendererRequest.FreeOn || action == RendererRequest.FreeOff)
        {
            SaveTalk(Talk with { GazeFree = action == RendererRequest.FreeOn }, render: openTab == CompanionTab.Eyes);
            return;
        }
        var choice = action[RendererRequest.LookPrefix.Length..];
        if (!RendererGaze.Choices.Contains(choice)) return;
        SaveTalk(Talk with { GazeUsual = choice == RendererGaze.Personality ? null : CharacterGaze.ModeOf(choice) },
            render: openTab == CompanionTab.Eyes);
    }
}
