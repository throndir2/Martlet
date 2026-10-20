using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Martlet.Avatar.Hosting;
using Martlet.Core.Characters;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;
using Martlet.F5;
using Martlet.Presentation;

namespace Martlet.Desktop;

/// <summary>Character profiles: one choice that switches who Martlet is all at once, its look (one of your characters or the
/// built-in one), its voice (one of your voices) and its personality (a persona). They are made and edited in Companion ›
/// Profiles and switched there, on Home and in the notification-area menu. Profiles travel with the shared settings and name
/// the look and voice by their shared IDs, so every computer switches to its own copy of the same character; the look is
/// saved per PC as Companion › Character does and the voice is chosen on all your computers as Companion › Voice does. On each
/// PC a profile also keeps what you leave there while it is in use (<see cref="CharacterProfileLocal"/>): where its character
/// stands and how big it is, where it looks and which touches stop it while it talks; switching to it here, or on another
/// computer, brings them back on this PC.</summary>
public partial class MainWindow
{
    /// <summary>The profile being edited in Companion › Profiles (<see cref="Guid.Empty"/> for a new one), or null.</summary>
    private Guid? editingProfile;
    private bool renderingHomeCharacters;

    /// <summary>What Martlet is now, as profiles see it: the look shown, the voice chosen and the profile that matches both
    /// and the active personality (null when none does).</summary>
    private (string? ModelId, string? VoiceId, CharacterProfile? Current) CharacterNow()
    {
        var modelId = ShownCharacterModelId();
        var voiceId = CurrentVoiceId();
        return (modelId, voiceId, homeSettings?.Companion?.CurrentCharacter(modelId, voiceId));
    }

    private CharacterModelLibrary CharacterLibrary()
    {
        if (store is null) return CharacterModelLibrary.Empty;
        try { return SharedCharacterModels.View(store.DataDirectory); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return CharacterModelLibrary.Empty; }
    }

    private Martlet.Core.Voices.SpeakingVoiceLibrary VoiceLibrary() =>
        store is null ? Martlet.Core.Voices.SpeakingVoiceLibrary.Empty : F5Voices.View(store.DataDirectory);

    internal static string BuiltInLookName => BundledLive2D.DefaultCharacter + " (built-in)";

    private string LookName(string? modelId, CharacterModelLibrary library) => modelId switch
    {
        null => "the current look",
        CharacterProfile.BuiltInModel => BuiltInLookName,
        _ when RemoteCharacterModels.IsRemote(modelId) => (vroidHub?.Look(modelId)?.Name ?? "a VRoid Hub character") + " (VRoid Hub)",
        _ => library.Live.FirstOrDefault(m => m.Id == modelId)?.Name ?? "a character no longer in your list"
    };

    internal static string VoiceName(string? voiceId, Martlet.Core.Voices.SpeakingVoiceLibrary voices) => voiceId switch
    {
        null => "the current voice",
        _ => voices.Find(voiceId) is { Removed: false } voice ? voice.Name! : "a voice no longer in your list"
    };

    internal static string PersonaName(Guid personaId, CompanionSettings? companion) =>
        companion?.Personas.FirstOrDefault(p => p.Id == personaId)?.Name ?? "a removed personality";

    /// <summary>Why a profile can't switch everything here yet (its look still copying or removed, its voice removed), or null.
    /// It still switches what it can.</summary>
    private string? ProfileProblem(CharacterProfile profile, CharacterModelLibrary library, Martlet.Core.Voices.SpeakingVoiceLibrary voices)
    {
        if (profile.ModelId is { } modelId && RemoteCharacterModels.IsRemote(modelId))
        {
            if (vroidHub?.Tokens() is null) return "Its look is from VRoid Hub: link VRoid Hub (Account › Linked services) to show it here.";
        }
        else if (profile.ModelId is { } shared && shared != CharacterProfile.BuiltInModel)
        {
            if (library.Live.FirstOrDefault(m => m.Id == shared) is not { } model) return "Its look is no longer in your characters.";
            if (store is not null && !SharedCharacterModels.IsComplete(store.DataDirectory, model)) return "Its look is still copying to this PC.";
        }
        if (profile.VoiceId is { } voiceId && voices.Find(voiceId) is not { Removed: false }) return "Its voice is no longer in your voices.";
        return null;
    }

    // ---------- Companion › Profiles (ProfilesPage in Pages\Profiles; MainWindow.ProfilesPage.cs gives it its services) ----------

    private void EditProfile(Guid id)
    {
        editingProfile = id;
        if (openTab != CompanionTab.Profiles) OpenCompanion(CompanionTab.Profiles);
        else RenderTab();
        ProfilesPage.FocusName();
    }

    private async Task SaveCharacterProfileAsync(Guid id, string name, Guid personaId, string? modelId, string? voiceId, Action<string> problem)
    {
        var saved = await ChangeCompanionAsync(companion =>
        {
            // Before any personality is saved, the form offers the default one, which is created as this saves.
            var persona = personaId == Guid.Empty ? companion.ActivePersonaId : personaId;
            return id == Guid.Empty
                ? companion.AddCharacter(name, persona, modelId, voiceId, out _)
                : companion.UpdateCharacter(id, name, persona, modelId, voiceId);
        }, "Your character profiles changed.");
        if (saved is null)
        {
            problem(ActionText.Text);
            return;
        }
        // A new profile that Martlet is now (the first that matches) takes on this PC's place, eyes and touch choice.
        RememberProfileHere();
        // A shared character is shared as it is now.
        FollowHouseholdSharingAsync().Forget();
        editingProfile = null;
        ActionText.Text = id == Guid.Empty ? $"Saved the profile '{name}'; use it to switch to it in one step." : $"Saved '{name}'.";
        if (!closing) RenderHome();
        if (!closing && openTab == CompanionTab.Profiles) RenderTab();
    }

    private async Task RemoveCharacterProfileAsync(CharacterProfile profile)
    {
        if (closing) return;
        if (!ConfirmationDialog.Confirm(this, $"Remove the profile '{profile.Name}' on all your computers (its look, voice and personality stay)?",
                "Remove profile"))
            return;
        if (await ChangeCompanionAsync(companion => companion.CharacterList.Any(c => c.Id == profile.Id) ? companion.RemoveCharacter(profile.Id) : companion,
                "Your character profiles changed.") is null)
            return;
        // What it kept on this PC goes with it, and it is no longer shared with the household.
        CharacterProfileLocalStore.Save(store?.DataDirectory, ProfilesHere());
        await StopSharingRemovedAsync(profile.Id);
        if (editingProfile == profile.Id) editingProfile = null;
        ActionText.Text = $"Removed the profile '{profile.Name}'.";
        if (!closing) RenderHome();
        if (!closing && openTab == CompanionTab.Profiles) RenderTab();
    }

    /// <summary>Applies <paramref name="change"/> to the newest saved personality settings and saves them (another change
    /// that lands in between is kept). Returns the saved settings, or null with the problem in the action line.</summary>
    private async Task<CompanionSettings?> ChangeCompanionAsync(Func<CompanionSettings, CompanionSettings> change, string reason)
    {
        if (setupService is null || companionService is null || closing) return null;
        ChangeTurns.Turn? turn = null;
        try
        {
            turn = await ChangeTurnAsync();
            return await SaveCompanionChangeAsync(change, reason, lifetime.Token);
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or ContractException or JsonException)
        {
            ActionText.Text = error.Message;
            return null;
        }
        finally { turn?.Dispose(); }
    }

    /// <summary>The caller holds the change turn.</summary>
    private async Task<CompanionSettings> SaveCompanionChangeAsync(Func<CompanionSettings, CompanionSettings> change, string reason, CancellationToken token)
    {
        var loaded = await setupService!.LoadAsync(token);
        if (loaded.Error is not null) throw new InvalidOperationException(loaded.Error.Summary);
        var companion = CompanionSettings.Begin(loaded.Settings).Companion
            ?? throw new InvalidOperationException("Martlet's personality isn't set up yet. Open Companion › Personality first.");
        var next = change(companion);
        if (ReferenceEquals(next, companion)) return companion;
        var saved = await companionService!.SaveCompanionAsync(next, token);
        if (!saved.Save.Saved) throw new InvalidOperationException(saved.Summary);
        homeSettings = saved.Settings;
        FollowSavedSetup(saved.Save.Revision, reason);
        // Another persona may be in use now, with another usual gaze.
        avatar.Gaze.Refresh();
        return saved.Settings.Companion!;
    }

    /// <summary>Switches Martlet to a character profile: its look on this PC, its voice on all your computers and its
    /// personality, in one step, then what it keeps on this PC (its place, eyes and touch choice). The profile in use first
    /// keeps what it has on this PC now. A part that can't switch yet (a look still copying here, a removed voice) is left as
    /// it was and the action line says why; the rest still switches. An open conversation takes the new voice and personality
    /// before its next reply.</summary>
    private async Task UseCharacterProfileAsync(Guid id)
    {
        if (store is null || setupService is null || companionService is null || closing) return;
        if (homeSettings?.Companion?.CharacterList.FirstOrDefault(c => c.Id == id) is not { } profile) return;
        ChangeTurns.Turn? turn = null;
        var token = lifetime.Token;
        var notes = new List<string>();
        try
        {
            turn = await ChangeTurnAsync();
            await RememberBeforeSwitchAsync(token);
            // From now on this PC uses the profile's own choices here; one that keeps none yet takes on what this PC uses now.
            var (here, kept) = ProfilesHere().Switch(id, ProfileHereNow());
            applyingProfileHere = true;
            if (profile.ModelId is { } modelId && !string.Equals(modelId, ShownCharacterModelId(), StringComparison.OrdinalIgnoreCase))
            {
                CharacterModel? model = null;
                if (avatarWindowOpen) notes.Add("The character settings window is open, so the look stayed as it was; close it and use the profile again.");
                else if (RemoteCharacterModels.IsRemote(modelId))
                {
                    if (kept?.Placement is { } place) avatar.Placement = place;
                    try { await SwitchRemoteLookAsync(modelId, token); }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or ContractException or JsonException)
                    {
                        notes.Add("The look didn't change: " + error.Message);
                    }
                }
                else if (modelId != CharacterProfile.BuiltInModel && (model = CharacterLibrary().Live.FirstOrDefault(m => m.Id == modelId)) is null)
                    notes.Add("Its look is no longer in your characters, so the look stayed as it was.");
                else
                {
                    // A showing character opens with its new look where this profile left it on this PC.
                    if (kept?.Placement is { } place) avatar.Placement = place;
                    try { await SwitchCharacterModelAsync(model, token); }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or ContractException or JsonException ||
                        SharedCharacterModels.IsFailure(error))
                    {
                        notes.Add("The look didn't change: " + error.Message);
                    }
                }
            }
            if (profile.VoiceId is { } voiceId && !string.Equals(voiceId, CurrentVoiceId(), StringComparison.OrdinalIgnoreCase))
            {
                try { await SwitchVoiceAsync(voiceId, VoiceDestination(), token); }
                catch (F5Exception error) { notes.Add("The voice didn't change: " + F5Voices.Describe(error)); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or ContractException or JsonException)
                {
                    notes.Add("The voice didn't change: " + error.Message);
                }
            }
            await SaveCompanionChangeAsync(companion => companion.CharacterList.Any(c => c.Id == id) ? companion.SelectCharacter(id) : companion,
                "Martlet's character changed.", token);
            CharacterProfileLocalStore.Save(store.DataDirectory, here);
            if (kept is not null) await ApplyProfileHereAsync(kept, opensSoon: false, token);
            ErrorLog.Info($"Switched to a character profile ({profile.Key}){(notes.Count == 0 ? "" : $" with {notes.Count} part(s) left as they were")}" +
                (kept is null ? "; it takes on this PC's place, eyes and touch choice." : $"; on this PC: {ProfileHereLog(kept)}."));
            ActionText.Text = $"Martlet is now '{profile.Name}'." + (notes.Count == 0 ? OpenConversationFollows : " " + string.Join(" ", notes));
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or ContractException or JsonException)
        {
            ActionText.Text = $"Couldn't switch to '{profile.Name}': {error.Message}";
        }
        finally
        {
            applyingProfileHere = false;
            turn?.Dispose();
            if (!closing)
            {
                UpdateCharacterButton();
                RenderHome();
                // A character shared together remembers in its own memory space.
                FollowCharacterSpace();
            }
        }
    }
}
