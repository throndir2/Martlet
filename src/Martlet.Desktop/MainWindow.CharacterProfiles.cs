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
    private TextBox? profileNameBox;
    private ComboBox? profilePersonaChoice, profileLookChoice, profileVoiceChoice;
    private TextBlock? profileEditorProblem;
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

    private static string BuiltInLookName => BundledLive2D.DefaultCharacter + " (built-in)";

    private string LookName(string? modelId, CharacterModelLibrary library) => modelId switch
    {
        null => "the current look",
        CharacterProfile.BuiltInModel => BuiltInLookName,
        _ => library.Live.FirstOrDefault(m => m.Id == modelId)?.Name ?? "a character no longer in your list"
    };

    private static string VoiceName(string? voiceId, Martlet.Core.Voices.SpeakingVoiceLibrary voices) => voiceId switch
    {
        null => "the current voice",
        _ => voices.Find(voiceId) is { Removed: false } voice ? voice.Name! : "a voice no longer in your list"
    };

    private static string PersonaName(Guid personaId, CompanionSettings? companion) =>
        companion?.Personas.FirstOrDefault(p => p.Id == personaId)?.Name ?? "a removed personality";

    /// <summary>Why a profile can't switch everything here yet (its look still copying or removed, its voice removed), or null.
    /// It still switches what it can.</summary>
    private string? ProfileProblem(CharacterProfile profile, CharacterModelLibrary library, Martlet.Core.Voices.SpeakingVoiceLibrary voices)
    {
        if (profile.ModelId is { } modelId && modelId != CharacterProfile.BuiltInModel)
        {
            if (library.Live.FirstOrDefault(m => m.Id == modelId) is not { } model) return "Its look is no longer in your characters.";
            if (store is not null && !SharedCharacterModels.IsComplete(store.DataDirectory, model)) return "Its look is still copying to this PC.";
        }
        if (profile.VoiceId is { } voiceId && voices.Find(voiceId) is not { Removed: false }) return "Its voice is no longer in your voices.";
        return null;
    }

    // ---------- Companion › Profiles ----------

    private void RenderProfilesTab(Panel page)
    {
        var companion = homeSettings?.Companion;
        var profiles = companion?.CharacterList ?? [];
        var library = CharacterLibrary();
        var voices = VoiceLibrary();
        var (modelId, voiceId, current) = CharacterNow();
        var lookNow = modelId is null ? "a model file outside your characters" : LookName(modelId, library);
        var mix = $"{lookNow}, the voice {(voiceId is null ? "Martlet's engine picks" : VoiceName(voiceId, voices))} and the personality " +
            $"{companion?.ActivePersona.Name ?? "Martlet"}";
        page.Children.Add(PageNowCard(current is not null ? $"{current.Name}: {mix}."
            : profiles.Count == 0 ? $"Martlet is {mix}. Save this as a profile to switch back to it in one step."
            : $"No profile matches what Martlet uses now: {mix}.", null, "CharacterProfilesNow"));

        var stack = new List<UIElement>
        {
            Heading("Your profiles"),
            HelpTip.Explain("Each profile is a whole character: its look, its voice and its personality. Use one to switch all three at once, " +
                "here, on Home or from Martlet's icon by the clock. Profiles are shared with your other Martlet computers.",
                new Thickness(0, 0, 0, 6), "Profiles", "profiles"),
            HelpTip.Explain("On each computer, a profile also keeps what you set there while you use it: where its character stands and how " +
                "big it is, where it looks, and which touches stop it while it talks. Switch back to it and they come back.",
                new Thickness(0, 0, 0, 10), "ProfilesPerComputer", "profiles on each computer")
        };
        var status = Note(profiles.Count == 0 ? "No profiles yet."
            : $"{profiles.Count} profile{(profiles.Count == 1 ? "" : "s")}. " +
              (current is not null ? "One of them is in use." : "None matches what Martlet uses now."), new Thickness(0, 0, 0, 10));
        AutomationProperties.SetAutomationId(status, "CharacterProfilesStatus");
        stack.Add(status);
        var here = ProfilesHere();
        var sharing = OwnSharing();
        foreach (var profile in profiles)
        {
            var inUse = profile.Id == current?.Id;
            var detail = $"Look: {LookName(profile.ModelId, library)} · Voice: {VoiceName(profile.VoiceId, voices)} · " +
                $"Personality: {PersonaName(profile.PersonaId, companion)}";
            var problem = ProfileProblem(profile, library, voices);
            stack.Add(ProfileRow(profile, detail, inUse ? "In use." : problem is null ? "Ready." : problem + " Using it switches the rest.",
                ProfileHereText(here.For(profile.Id)), inUse, ProfileSharingControls(profile, sharing), sharing?.HasJoined(profile.Id) == true));
        }
        stack.Add(Row(PageButton(profiles.Count == 0 ? "Save what Martlet uses now as a profile..." : "New profile...",
            () => EditProfile(Guid.Empty), primary: profiles.Count == 0, id: "CharacterProfileNew")));
        page.Children.Add(Card([.. stack]));

        if (editingProfile is { } editing)
        {
            page.Children.Add(ProfileEditorCard(editing, companion, library, voices, modelId, voiceId));
            // Keep the half-filled form while Martlet refreshes.
            tabEdited = true;
        }

        page.Children.Add(HouseholdCharactersCard(companion, library, voices));

        page.Children.Add(Card(Heading("The parts"),
            Note("Add looks in Character, voices in Voice and personalities in Personality; every profile can use them.", new Thickness(0, 0, 0, 8)),
            Row(PageButton("Character", () => OpenCompanion(CompanionTab.Character), id: "ProfilesOpenCharacter"),
                PageButton("Voice", () => OpenCompanion(CompanionTab.Voice), id: "ProfilesOpenVoice"),
                PageButton("Personality", () => OpenCompanion(CompanionTab.Personality), id: "ProfilesOpenPersonality"))));
    }

    /// <summary>One profile: its name (with the in-use mark), what it sets, a readable state line
    /// (<c>CharacterProfileState-key</c>: never a name), what it keeps on this PC (<c>CharacterProfileHere-key</c>), how it is shared
    /// with the household (<paramref name="sharing"/>) and Use, Edit and Remove. A character someone else shares with you together
    /// (<paramref name="joined"/>) can't be edited here, and Leave (<c>CharacterProfileLeave-key</c>) replaces Remove.</summary>
    private UIElement ProfileRow(CharacterProfile profile, string detail, string state, string here, bool inUse, UIElement? sharing = null,
        bool joined = false)
    {
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock
        {
            Text = profile.Name + (inUse ? "  \u00b7  in use" : ""), FontSize = 15, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap
        });
        text.Children.Add(Note(detail, new Thickness(0, 2, 0, 0)));
        var hereText = Note(here, new Thickness(0, 2, 0, 0));
        AutomationProperties.SetAutomationId(hereText, "CharacterProfileHere-" + profile.Key);
        text.Children.Add(hereText);
        var stateText = Note(state, new Thickness(0, 2, 0, 0));
        AutomationProperties.SetAutomationId(stateText, "CharacterProfileState-" + profile.Key);
        text.Children.Add(stateText);
        if (sharing is not null) text.Children.Add(sharing);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        Button Small(string label, Action run, string id)
        {
            var button = PageButton(label, run, id: $"CharacterProfile{id}-{profile.Key}");
            button.MinWidth = 72;
            button.Margin = new Thickness(8, 0, 0, 0);
            AutomationProperties.SetName(button, $"{label} {profile.Name}");
            return button;
        }
        var use = Small(inUse ? "In use" : "Use", () => UseCharacterProfileAsync(profile.Id).Forget(), "Use");
        use.IsEnabled = !inUse;
        buttons.Children.Add(use);
        var edit = Small("Edit", () => EditProfile(profile.Id), "Edit");
        edit.IsEnabled = !joined;
        if (joined) edit.ToolTip = "Only the person who shares it changes it.";
        buttons.Children.Add(edit);
        buttons.Children.Add(joined ? Small("Leave", () => LeaveSharedCharacterAsync(profile).Forget(), "Leave")
            : Small("Remove", () => RemoveCharacterProfileAsync(profile).Forget(), "Remove"));
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
        DockPanel.SetDock(buttons, Dock.Right);
        row.Children.Add(buttons);
        row.Children.Add(text);
        return row;
    }

    private void EditProfile(Guid id)
    {
        editingProfile = id;
        if (openTab != CompanionTab.Profiles) OpenCompanion(CompanionTab.Profiles);
        else RenderTab();
        profileNameBox?.Focus();
        profileNameBox?.SelectAll();
    }

    /// <summary>The form for a new profile (filled in with what Martlet uses now) or an existing one: its name, personality,
    /// look and voice. "Keep the current" look or voice leaves that part alone when the profile is used.</summary>
    private Border ProfileEditorCard(Guid id, CompanionSettings? companion, CharacterModelLibrary library,
        Martlet.Core.Voices.SpeakingVoiceLibrary voices, string? modelNow, string? voiceNow)
    {
        var existing = companion?.CharacterList.FirstOrDefault(c => c.Id == id);
        var personaId = existing?.PersonaId ?? companion?.ActivePersonaId ?? Guid.Empty;
        var lookId = existing is null ? modelNow : existing.ModelId;
        var voiceId = existing is null ? voiceNow : existing.VoiceId;
        var name = existing?.Name ?? UniqueProfileName(companion?.ActivePersona.Name ?? "Martlet", companion);

        profileNameBox = new TextBox { Text = name, MaxLength = CharacterProfile.MaximumNameCharacters, MinWidth = 260, MaxWidth = 420, HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetAutomationId(profileNameBox, "CharacterProfileName");
        AutomationProperties.SetName(profileNameBox, "Profile name");

        ComboBox Choice(string automationId, string accessibleName, IEnumerable<(object? Tag, string Text)> options, object? selected)
        {
            var box = new ComboBox { MinHeight = 30, MinWidth = 260, MaxWidth = 420, HorizontalAlignment = HorizontalAlignment.Left };
            foreach (var (tag, text) in options)
            {
                var item = new ComboBoxItem { Content = text, Tag = tag };
                box.Items.Add(item);
                if (Equals(tag, selected)) box.SelectedItem = item;
            }
            box.SelectedIndex = box.SelectedIndex < 0 ? 0 : box.SelectedIndex;
            AutomationProperties.SetAutomationId(box, automationId);
            AutomationProperties.SetName(box, accessibleName);
            return box;
        }
        profilePersonaChoice = Choice("CharacterProfilePersona", "Personality",
            companion is null ? [((object?)Guid.Empty, "Martlet (default)")] : companion.Personas.Select(p => ((object?)p.Id, p.Name)),
            companion is null ? Guid.Empty : personaId);
        var looks = new List<(object?, string)> { (null, "Keep the current look"), (CharacterProfile.BuiltInModel, BuiltInLookName) };
        looks.AddRange(library.Live.Select(m => ((object?)m.Id, m.Name!)));
        if (lookId is not null && looks.All(l => !Equals(l.Item1, lookId))) looks.Add((lookId, "A character no longer in your list"));
        profileLookChoice = Choice("CharacterProfileLook", "Look", looks, lookId);
        var voiceOptions = new List<(object?, string)> { (null, "Keep the current voice") };
        voiceOptions.AddRange(voices.Live.Select(v => ((object?)v.Id, v.Name!)));
        if (voiceId is not null && voiceOptions.All(v => !Equals(v.Item1, voiceId))) voiceOptions.Add((voiceId, "A voice no longer in your list"));
        profileVoiceChoice = Choice("CharacterProfileVoice", "Voice", voiceOptions, voiceId);

        profileEditorProblem = Note("", new Thickness(0, 8, 0, 0));
        profileEditorProblem.SetResourceReference(TextBlock.ForegroundProperty, "WarningBrush");
        AutomationProperties.SetAutomationId(profileEditorProblem, "CharacterProfileEditorProblem");
        Label Labeled(string text, Control target) => new() { Content = text, Target = target, Padding = new Thickness(0, 12, 0, 4) };
        return Card(Heading(existing is null ? "New profile" : $"Edit '{existing.Name}'"),
            Note(existing is null ? "It starts as what Martlet uses now. Change any part, then save." : "Change any part, then save.",
                new Thickness(0, 0, 0, 0)),
            Labeled("_Name", profileNameBox), profileNameBox,
            Labeled("_Personality", profilePersonaChoice), profilePersonaChoice,
            Labeled("_Look", profileLookChoice), profileLookChoice,
            Labeled("_Voice", profileVoiceChoice), profileVoiceChoice,
            Note("Voices are the ones in Companion › Voice › Voices, spoken by voice-cloning engines.", new Thickness(0, 6, 0, 0)),
            profileEditorProblem,
            Row(PageButton(existing is null ? "Save profile" : "Save changes", () => SaveCharacterProfileAsync(id).Forget(), primary: true, id: "CharacterProfileSave"),
                PageButton("Cancel", () => { editingProfile = null; RenderTab(); }, id: "CharacterProfileCancel")));
    }

    private static string UniqueProfileName(string name, CompanionSettings? companion)
    {
        var taken = new HashSet<string>((companion?.CharacterList ?? []).Select(c => c.Name), StringComparer.OrdinalIgnoreCase);
        if (!taken.Contains(name)) return name;
        for (var i = 2; ; i++)
            if (!taken.Contains($"{name} {i}")) return $"{name} {i}";
    }

    private async Task SaveCharacterProfileAsync(Guid id)
    {
        if (profileNameBox is null || profilePersonaChoice is null || profileLookChoice is null || profileVoiceChoice is null) return;
        var name = profileNameBox.Text.Trim();
        if (name.Length == 0)
        {
            if (profileEditorProblem is not null) profileEditorProblem.Text = "Give the profile a name.";
            return;
        }
        if ((profilePersonaChoice.SelectedItem as ComboBoxItem)?.Tag is not Guid personaId)
        {
            if (profileEditorProblem is not null) profileEditorProblem.Text = "Choose a personality.";
            return;
        }
        var modelId = (profileLookChoice.SelectedItem as ComboBoxItem)?.Tag as string;
        var voiceId = (profileVoiceChoice.SelectedItem as ComboBoxItem)?.Tag as string;
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
            if (profileEditorProblem is not null) profileEditorProblem.Text = ActionText.Text;
            return;
        }
        // A new profile that Martlet is now (the first that matches) takes on this PC's place, eyes and touch choice.
        RememberProfileHere();
        // A shared character is shared as it is now.
        FollowHouseholdSharingAsync().Forget();
        editingProfile = null;
        ActionText.Text = id == Guid.Empty ? $"Saved the profile '{name}'. Use it to switch to it in one step." : $"Saved '{name}'.";
        if (!closing) RenderHome();
        if (!closing && openTab == CompanionTab.Profiles) RenderTab();
    }

    private async Task RemoveCharacterProfileAsync(CharacterProfile profile)
    {
        if (closing) return;
        if (!ConfirmationDialog.Confirm(this, $"Remove the profile '{profile.Name}'?\n\nIts look, voice and personality stay; only the profile " +
                "that switches to them goes, on all your computers.", "Remove profile"))
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
                if (avatarWindowOpen) notes.Add("The character settings window is open, so the look stayed as it was. Close it and use the profile again.");
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

    // ---------- what each profile keeps on this PC ----------

    /// <summary>Set while Martlet switches profiles or puts back what one keeps on this PC, so that isn't kept for another one.</summary>
    private bool applyingProfileHere;

    /// <summary>What Martlet uses on this PC now, as a profile keeps it: the character's place, eyes and touch choice.</summary>
    private CharacterProfileLocal ProfileHereNow() => new(avatar.Placement, Talk.GazeUsual, Talk.GazeFree, Talk.TouchInterrupts);

    /// <summary>What the saved profiles keep on this PC and the one whose choices this PC uses (removed profiles left out).</summary>
    private CharacterProfilesHere ProfilesHere()
    {
        var here = CharacterProfileLocalStore.Load(store?.DataDirectory);
        return homeSettings?.Companion is { } companion ? here.Of([.. companion.CharacterList.Select(c => c.Id)]) : here;
    }

    /// <summary>The character was moved, resized, locked or reset, or its eyes or touch choice changed, on this PC: the profile
    /// Martlet is now keeps it here (<see cref="CharacterProfilesHere.Remember"/>), so switching back to it brings it back.</summary>
    private void RememberProfileHere()
    {
        if (applyingProfileHere || closing || store is null || CharacterNow().Current is not { } current) return;
        if (ProfilesHere().Remember(current.Id, ProfileHereNow()) is { } next) CharacterProfileLocalStore.Save(store.DataDirectory, next);
    }

    /// <summary>Before switching profiles: the profile in use keeps what it has on this PC now, with the place read from the
    /// showing character (a move may still be settling).</summary>
    private async Task RememberBeforeSwitchAsync(CancellationToken token)
    {
        // The camera view is its own window; the overlay's place is the one from before it opened.
        if (avatar.IsShowing && avatar.Camera is null)
            try
            {
                if (await avatar.ReadPlacementAsync(token) is { } place) CharacterPlacementStore.Save(store?.DataDirectory, place);
            }
            catch (Exception error) when (RendererFailures.Is(error, token))
            {
                RendererFailures.Log("The character's position couldn't be read before switching character profiles", error);
            }
        RememberProfileHere();
    }

    /// <summary>Puts back what a profile keeps on this PC: where it looks and which touches stop it, then its place. A showing
    /// character moves there now, unless it <paramref name="opensSoon"/> again with a new look: it then opens there.</summary>
    private async Task ApplyProfileHereAsync(CharacterProfileLocal kept, bool opensSoon, CancellationToken token)
    {
        var applying = applyingProfileHere;
        applyingProfileHere = true;
        try
        {
            if (Talk.GazeUsual != kept.GazeUsual || Talk.GazeFree != kept.GazeFree || Talk.TouchInterrupts != kept.TouchInterrupts)
                SaveTalk(Talk with { GazeUsual = kept.GazeUsual, GazeFree = kept.GazeFree, TouchInterrupts = kept.TouchInterrupts },
                    render: openTab is CompanionTab.Eyes or CompanionTab.Touch);
            if (kept.Placement is not { } place) return;
            if (avatar.IsShowing && !opensSoon)
                try { await avatar.PlaceAsync(place, token); }
                catch (Exception error) when (RendererFailures.Is(error, token))
                {
                    RendererFailures.Log("The character couldn't move to its profile's place", error);
                    avatar.Placement = place;
                }
            else avatar.Placement = place;
            if (closing) return;
            CharacterPlacementStore.Save(store?.DataDirectory, avatar.Placement);
            if (characterPlacementNote is { } note) note.Text = CharacterPlacementText();
        }
        finally { applyingProfileHere = applying; }
    }

    /// <summary>Follows the profile switched to last (the shared settings' <see cref="CompanionSettings.ActiveCharacterId"/>) when
    /// this PC doesn't use its choices yet: a switch made on another computer, also one that arrived while Martlet was closed here.
    /// What that profile keeps on this PC comes back; a profile that keeps nothing here yet takes on what this PC uses now. A
    /// switch made here is followed by <see cref="UseCharacterProfileAsync"/> itself.</summary>
    private void FollowCharacterProfile()
    {
        if (applyingProfileHere || closing || store is null || homeSettings?.Companion?.ActiveCharacterId is not { } id) return;
        var here = ProfilesHere();
        if (here.InUse == id) return;
        var (next, kept) = here.Switch(id, ProfileHereNow());
        if (!CharacterProfileLocalStore.Save(store.DataDirectory, next) || kept is null) return;
        var profile = homeSettings.Companion.CharacterList.FirstOrDefault(c => c.Id == id);
        ErrorLog.Info($"Following the character profile switched to last ({profile?.Key}); on this PC: {ProfileHereLog(kept)}.");
        FollowCharacterSpace();
        // A new look from another computer restarts a showing character (AfterSettingsAppliedAsync): it opens at the place.
        ApplyProfileHereAsync(kept, opensSoon: characterChanged && avatar.IsShowing, lifetime.Token).Forget();
    }

    /// <summary>A profile row's line on what it keeps on this PC (<c>CharacterProfileHere-key</c>; never a name).</summary>
    internal static string ProfileHereText(CharacterProfileLocal? kept)
    {
        if (kept is null) return "On this PC: nothing yet. While you use it, it keeps where its character stands, where it looks and which touches stop it.";
        var place = kept.Placement is { } at ? $"its own spot and size ({at.Width:0} × {at.Height:0}{(at.Locked ? ", locked" : "")})" : "no spot of its own";
        var eyes = (kept.GazeUsual is { } mode ? CharacterGaze.Label(mode) : "As the personality decides") + (kept.GazeFree ? "" : ", replies can't change it");
        var touch = kept.TouchInterrupts switch
        {
            Martlet.Conversation.TouchInterrupts.Intimate => "only intimate touches stop it",
            Martlet.Conversation.TouchInterrupts.Never => "it finishes first",
            _ => "any touch stops it"
        };
        return $"On this PC: {place} · Eyes: {eyes} · While it talks: {touch}.";
    }

    /// <summary>What a profile keeps on this PC, for the desktop log: no names, only the size, lock, monitor and choices.</summary>
    private static string ProfileHereLog(CharacterProfileLocal kept) =>
        (kept.Placement is { } at ? $"place {at.Width:0} × {at.Height:0}{(at.Locked ? " locked" : "")}{(at.Screen is { } screen ? $" on {CharacterScreen(screen)}" : "")}"
            : "no place") +
        $", gaze {(kept.GazeUsual is { } mode ? CharacterGaze.Word(mode) : RendererGaze.Personality)}{(kept.GazeFree ? "" : " (fixed)")}" +
        $", touches while talking {kept.TouchInterrupts.ToString().ToLowerInvariant()}";

    // ---------- links from Character and Personality ----------

    /// <summary>Character and Personality point here: a profile ties the look and personality to a voice.</summary>
    private Border ProfilesLinkCard()
    {
        var count = homeSettings?.Companion?.CharacterList.Count ?? 0;
        return Card(Heading("Character profiles"),
            Note(count == 0 ? "Save a look, a voice and a personality together as a profile to switch between whole characters in one step."
                : $"You have {count} profile{(count == 1 ? "" : "s")}. Each switches the look, voice and personality together.", new Thickness(0, 0, 0, 8)),
            Row(PageButton(count == 0 ? "Make a profile" : "Open profiles", () => OpenCompanion(CompanionTab.Profiles), id: "OpenProfiles")));
    }

    // ---------- Home ----------

    /// <summary>Home's character switch: every profile, the one in use selected ("A mix of your own" when none matches), and
    /// Manage profiles, which opens Companion › Profiles.</summary>
    private void RenderHomeCharacters()
    {
        if (HomeCharacterRow is null) return;
        var companion = Role == DeviceRole.Companion;
        HomeCharacterRow.Visibility = companion ? Visibility.Visible : Visibility.Collapsed;
        if (!companion || HomeCharacterChoice.IsDropDownOpen) return;
        var profiles = homeSettings?.Companion?.CharacterList ?? [];
        var current = profiles.Count == 0 ? null : CharacterNow().Current;
        HomeCharacterChoice.Visibility = profiles.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        HomeCharacterLabel.Visibility = HomeCharacterChoice.Visibility;
        HomeCharacterManage.Content = profiles.Count == 0 ? "Make character profiles" : "Manage profiles";
        AutomationProperties.SetName(HomeCharacterManage, profiles.Count == 0
            ? "Make character profiles to switch look, voice and personality at once" : "Manage character profiles");
        renderingHomeCharacters = true;
        try
        {
            HomeCharacterChoice.Items.Clear();
            if (current is null && profiles.Count > 0)
                HomeCharacterChoice.Items.Add(new ComboBoxItem { Content = "A mix of your own", Tag = null, IsEnabled = false });
            foreach (var profile in profiles)
                HomeCharacterChoice.Items.Add(new ComboBoxItem { Content = profile.Name, Tag = profile.Id });
            HomeCharacterChoice.SelectedIndex = current is null ? 0 : profiles.ToList().FindIndex(p => p.Id == current.Id);
        }
        finally { renderingHomeCharacters = false; }
    }

    private void HomeCharacter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (renderingHomeCharacters || (HomeCharacterChoice.SelectedItem as ComboBoxItem)?.Tag is not Guid id) return;
        if (CharacterNow().Current?.Id == id) return;
        UseCharacterProfileAsync(id).Forget();
    }

    private void HomeCharacterManage_Click(object sender, RoutedEventArgs e) => OpenCompanion(CompanionTab.Profiles);

    // ---------- the notification-area menu ----------

    /// <summary>The icon menu's Character submenu: each profile, the one in use ticked. Null without profiles.</summary>
    private MenuItem? TrayCharacterProfiles(ContextMenu menu, bool enabled)
    {
        var profiles = homeSettings?.Companion?.CharacterList ?? [];
        if (profiles.Count == 0) return null;
        var current = CharacterNow().Current;
        var parent = new MenuItem { Header = "Character profile", IsEnabled = enabled };
        AutomationProperties.SetAutomationId(parent, "TrayCharacterProfiles");
        foreach (var profile in profiles)
        {
            var item = new MenuItem { Header = profile.Name, IsCheckable = false, IsChecked = profile.Id == current?.Id };
            AutomationProperties.SetAutomationId(item, "TrayCharacterProfile-" + profile.Key);
            var id = profile.Id;
            item.Click += (_, _) =>
            {
                menu.IsOpen = false;
                if (id != CharacterNow().Current?.Id)
                    Dispatcher.InvokeAsync(() => UseCharacterProfileAsync(id).Forget(), System.Windows.Threading.DispatcherPriority.Background);
            };
            parent.Items.Add(item);
        }
        return parent;
    }
}
