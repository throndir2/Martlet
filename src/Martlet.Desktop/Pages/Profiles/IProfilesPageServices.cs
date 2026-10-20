using Martlet.Avatar.Hosting;
using Martlet.Core.Characters;
using Martlet.Core.Settings;
using Martlet.Core.Sharing;
using Martlet.Core.Voices;

namespace Martlet.Desktop;

/// <summary>Everything Companion › Profiles (<see cref="ProfilesPage"/> and <see cref="ProfilesPageModel"/>) reads and starts in the
/// app. MainWindow gives it to the page (MainWindow.ProfilesPage.cs) and keeps the work the page shows (rule 4 of Pages\README.md):
/// saving, using, removing and leaving profiles, what each profile keeps on this PC and the household sharing
/// (MainWindow.CharacterProfiles.cs and MainWindow.Sharing.cs). It has no WPF types.</summary>
internal interface IProfilesPageServices
{
    // ---------- what the page reads ----------

    /// <summary>The saved settings (the personas and the profiles), or null before they are loaded.</summary>
    AppSettings? Settings { get; }

    /// <summary>Your characters (the looks), as this PC sees them.</summary>
    CharacterModelLibrary Library();

    /// <summary>Your voices.</summary>
    SpeakingVoiceLibrary Voices();

    /// <summary>What Martlet is now: the look shown, the voice chosen and the profile that matches both and the personality.</summary>
    (string? ModelId, string? VoiceId, CharacterProfile? Current) CharacterNow();

    /// <summary>A look's name.</summary>
    string LookName(string? modelId, CharacterModelLibrary library);

    /// <summary>Why a profile can't switch everything here yet, or null.</summary>
    string? ProfileProblem(CharacterProfile profile, CharacterModelLibrary library, SpeakingVoiceLibrary voices);

    /// <summary>What each profile keeps on this PC.</summary>
    CharacterProfilesHere ProfilesHere();

    /// <summary>How this account shares characters with the household, or null.</summary>
    HouseholdSharing? OwnSharing();

    /// <summary>The VRoid Hub looks linked on this PC.</summary>
    IReadOnlyList<VRoidHubLook> VroidLooks();

    /// <summary>The profile being edited (<see cref="Guid.Empty"/> for a new one), or null.</summary>
    Guid? Editing { get; }

    // ---------- what the page starts ----------

    void Use(Guid id);

    /// <summary>Opens the editor for the profile with <paramref name="id"/> (<see cref="Guid.Empty"/>: a new one).</summary>
    void Edit(Guid id);

    void CancelEdit();

    /// <summary>Saves the profile with <paramref name="id"/>; <paramref name="problem"/> gets why it couldn't.</summary>
    void Save(Guid id, string name, Guid personaId, string? modelId, string? voiceId, Action<string> problem);

    void Remove(CharacterProfile profile);

    /// <summary>Leaves a character someone else shares with you.</summary>
    void Leave(CharacterProfile profile);

    /// <summary>Opens another Companion page.</summary>
    void Open(CompanionTab tab);

    /// <summary>Tells the window that the page has edits that are not saved yet.</summary>
    void MarkEdited();
}
