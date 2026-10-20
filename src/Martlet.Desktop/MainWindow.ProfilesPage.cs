using Martlet.Avatar.Hosting;
using Martlet.Core.Characters;
using Martlet.Core.Settings;
using Martlet.Core.Sharing;
using Martlet.Core.Voices;

namespace Martlet.Desktop;

/// <summary>The services MainWindow gives Companion › Profiles (<see cref="ProfilesPage"/> in Pages\Profiles). The work the page shows
/// stays in the window (MainWindow.CharacterProfiles.cs and MainWindow.Sharing.cs).</summary>
public partial class MainWindow : IProfilesPageServices
{
    private ProfilesPage? profilesPage;

    /// <summary>The Profiles page, made once.</summary>
    private ProfilesPage ProfilesPage => profilesPage ??= new(this, ProfileSharingControls, HouseholdCharactersCard);

    AppSettings? IProfilesPageServices.Settings => homeSettings;
    CharacterModelLibrary IProfilesPageServices.Library() => CharacterLibrary();
    SpeakingVoiceLibrary IProfilesPageServices.Voices() => VoiceLibrary();
    (string? ModelId, string? VoiceId, CharacterProfile? Current) IProfilesPageServices.CharacterNow() => CharacterNow();
    string IProfilesPageServices.LookName(string? modelId, CharacterModelLibrary library) => LookName(modelId, library);
    string? IProfilesPageServices.ProfileProblem(CharacterProfile profile, CharacterModelLibrary library, SpeakingVoiceLibrary voices) =>
        ProfileProblem(profile, library, voices);
    CharacterProfilesHere IProfilesPageServices.ProfilesHere() => ProfilesHere();
    HouseholdSharing? IProfilesPageServices.OwnSharing() => OwnSharing();
    IReadOnlyList<VRoidHubLook> IProfilesPageServices.VroidLooks() => vroidHub?.Looks() ?? [];
    Guid? IProfilesPageServices.Editing => editingProfile;

    void IProfilesPageServices.Use(Guid id) => UseCharacterProfileAsync(id).Forget();
    void IProfilesPageServices.Edit(Guid id) => EditProfile(id);
    void IProfilesPageServices.CancelEdit()
    {
        editingProfile = null;
        RenderTab();
    }
    void IProfilesPageServices.Save(Guid id, string name, Guid personaId, string? modelId, string? voiceId, Action<string> problem) =>
        SaveCharacterProfileAsync(id, name, personaId, modelId, voiceId, problem).Forget();
    void IProfilesPageServices.Remove(CharacterProfile profile) => RemoveCharacterProfileAsync(profile).Forget();
    void IProfilesPageServices.Leave(CharacterProfile profile) => LeaveSharedCharacterAsync(profile).Forget();
    void IProfilesPageServices.Open(CompanionTab tab) => OpenCompanion(tab);
    void IProfilesPageServices.MarkEdited() => tabEdited = true;
}
