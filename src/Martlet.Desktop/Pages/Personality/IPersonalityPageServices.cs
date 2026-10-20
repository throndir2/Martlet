using System.Windows;
using Martlet.Core.Settings;

namespace Martlet.Desktop;

/// <summary>Everything Companion › Personality (<see cref="PersonalityPage"/>) reads and starts in the app. MainWindow gives it to
/// the page (MainWindow.PersonalityPage.cs) and keeps the windows the buttons open.</summary>
internal interface IPersonalityPageServices
{
    /// <summary>The saved settings (the personas), or null before they are loaded.</summary>
    AppSettings? Settings { get; }

    /// <summary>The Profiles card that the Personality and Character pages share (MainWindow.CharacterProfiles.cs).</summary>
    UIElement ProfilesLinkCard();

    /// <summary>Opens the personality editor (Edit personality), or (<paramref name="importCard"/>) its character card import.</summary>
    void OpenPersonality(bool importCard);

    /// <summary>Opens Companion › Prompts.</summary>
    void OpenPrompts();
}
