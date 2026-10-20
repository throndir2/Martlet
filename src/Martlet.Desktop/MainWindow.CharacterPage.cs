using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Martlet.Avatar.Hosting;
using Martlet.Avatars;

namespace Martlet.Desktop;

/// <summary>The services MainWindow gives Companion › Character (<see cref="CharacterPage"/> in Pages\Character). The character on
/// the desktop stays in the window (MainWindow.xaml.cs), with Your characters (MainWindow.Characters.cs), VRoid Hub
/// (MainWindow.VRoidHub.cs) and the link to Profiles (MainWindow.CharacterProfiles.cs), which the page gets as builders.</summary>
public partial class MainWindow : ICharacterPageServices
{
    private CharacterPage? characterPage;

    /// <summary>The Character page, made once.</summary>
    private CharacterPage CharacterPage => characterPage ??=
        new(this, CharacterPlacementLine, CharacterViewLine, CharacterModelsCard, VRoidHubCard, ProfilesLinkCard);

    /// <summary>The line on where the character is remembered (SetupCharacterPlacement), which the window updates when it moves.</summary>
    private TextBlock CharacterPlacementLine()
    {
        var placementNote = characterPlacementNote = Note(CharacterPlacementText(), new Thickness(0, 4, 0, 0));
        AutomationProperties.SetAutomationId(placementNote, "SetupCharacterPlacement");
        return placementNote;
    }

    /// <summary>The zoom card's view line (SetupCharacterView) while the character shows, which each zoom updates; null (and no
    /// line to update) while it is hidden.</summary>
    private TextBlock? CharacterViewLine(bool showing)
    {
        characterViewText = null;
        if (!showing) return null;
        characterViewText = Note("", new Thickness(0, 8, 0, 0));
        AutomationProperties.SetAutomationId(characterViewText, "SetupCharacterView");
        return characterViewText;
    }

    string ICharacterPageServices.ModelName => CharacterModelName();
    AvatarRenderer? ICharacterPageServices.Renderer => homeAvatar?.Renderer;
    string? ICharacterPageServices.CleanupProblem => characterCleanupProblem;
    bool ICharacterPageServices.Showing => avatar.IsShowing;
    bool ICharacterPageServices.Locked => avatar.PlacementLocked;
    bool ICharacterPageServices.HasPlacement => avatar.Placement is not null;
    bool ICharacterPageServices.ClickThrough => avatar.ClickThrough;
    RendererCapabilities? ICharacterPageServices.Capabilities => avatar.Capabilities;

    void ICharacterPageServices.ToggleCharacter() => RunNodeAction(NodeAction.ToggleCharacter);
    void ICharacterPageServices.OpenCharacter() => RunNodeAction(NodeAction.Character);
    void ICharacterPageServices.ResetPosition() => ResetCharacterPositionAsync().Forget();
    void ICharacterPageServices.ResetZoom() => ResetCharacterZoomAsync().Forget();
    void ICharacterPageServices.SetLock(bool locked) => SetCharacterLockAsync(locked).Forget();
    void ICharacterPageServices.SetClickThrough(bool on) => SetCharacterClickThroughAsync(on).Forget();
    void ICharacterPageServices.Zoom(string action) => ZoomCharacterAsync(action).Forget();
}
