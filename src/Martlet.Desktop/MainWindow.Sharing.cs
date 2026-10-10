using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using Martlet.Core.Characters;
using Martlet.Core.Contracts;
using Martlet.Core.Lorebooks;
using Martlet.Core.Settings;
using Martlet.Core.Sharing;
using Martlet.Core.Sync;

namespace Martlet.Desktop;

/// <summary>Sharing characters with the household (docs/ACCOUNTS.md, "Sharing"). Each account's devices publish what it shares in
/// the household entry <c>sharing.&lt;account&gt;</c> (<see cref="HouseholdSharing"/>): its shared characters, as a copy or
/// together, refreshed after each change, and the characters of other people it talks to together. Companion › Profiles sets how
/// each of your characters is shared and lists the household's characters: Use a copy makes your own copy with its own memories;
/// Talk to it adds the character shared together (the same IDs as its owner's, so it uses that character's memory space) and it
/// follows the owner's changes after every sync. Everything here runs between replies, never on a reply's path.</summary>
public partial class MainWindow
{
    private readonly DispatcherTimer sharingTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    private bool sharingBusy;

    private void InitializeHouseholdSharing() => sharingTimer.Tick += (_, _) => FollowHouseholdSharingAsync().Forget();

    private void StartHouseholdSharing()
    {
        if (closing) return;
        sharingTimer.Start();
        FollowHouseholdSharingAsync().Forget();
    }

    // ---------- the account and the household entries ----------

    /// <summary>The account signed in now, or null without accounts.</summary>
    private Guid? SharingAccount => null;

    /// <summary>Whether this PC's settings files hold the signed-in account's settings now (never in the middle of a switch), so
    /// what it shares is read from its own characters.</summary>
    private bool SharingFilesReady() => SharingAccount is not null;

    /// <summary>A household member's name, for *Shared by*.</summary>
    private string AccountName(Guid account) => "someone in your household";

    /// <summary>Every account's household sharing entry in this PC's copy of the household settings.</summary>
    private IReadOnlyDictionary<Guid, HouseholdSharing> HouseholdEntries() =>
        settingsNode is null ? new Dictionary<Guid, HouseholdSharing>() : HouseholdSharing.All(settingsNode.Document.Settings.Select(s => (s.Key, s.Value)));

    /// <summary>What the signed-in account shares (empty when it shares nothing yet), or null without accounts.</summary>
    private HouseholdSharing? OwnSharing(IReadOnlyDictionary<Guid, HouseholdSharing>? entries = null) =>
        SharingAccount is { } me ? (entries ?? HouseholdEntries()).GetValueOrDefault(me) ?? HouseholdSharing.Empty(me) : null;

    /// <summary>The memory space of the character in use when it is shared together (one of yours, or one you talk to), else
    /// null: then remembering uses the account's own space.</summary>
    private string? SharedCharacterSpace()
    {
        if (homeSettings?.Companion is not { ActiveCharacterId: { } id } companion || OwnSharing() is not { } own) return null;
        if (companion.CharacterList.FirstOrDefault(c => c.Id == id) is not { } profile || profile.PersonaId != companion.ActivePersonaId) return null;
        return own.ModeOf(id) == CharacterShareMode.Together || own.HasJoined(id) ? MemorySpaceId.Character(id) : null;
    }

    /// <summary>Changes the signed-in account's household entry between settings syncs and gives it to the hosts soon. Throws
    /// <see cref="ContractException"/> from <paramref name="change"/>.</summary>
    private async Task<bool> ChangeOwnSharingAsync(Func<HouseholdSharing, HouseholdSharing> change)
    {
        while (settingsBusy && !closing) await Task.Delay(100);
        if (closing || settingsNode is null) return false;
        var entries = HouseholdEntries();
        if (OwnSharing(entries) is not { } own) return false;
        var next = change(own);
        var value = next.Write();
        var key = HouseholdSharing.Key(own.AccountId);
        if (settingsNode.Document.Find(key)?.Value == value) return true;
        if (!entries.ContainsKey(own.AccountId) && value == HouseholdSharing.Empty(own.AccountId).Write()) return true;
        settingsNode.Put(key, value, DateTimeOffset.UtcNow);
        QueueSettingsSync();
        return true;
    }

    /// <summary>The signed-in account's personalities and character profiles and its lorebooks as saved now; null when either
    /// can't be read (nothing is shared or followed from a half-read state).</summary>
    private async Task<(CompanionSettings Companion, LorebookLibrary Lorebooks)?> SharingSourcesAsync(CancellationToken token)
    {
        if (setupService is null) return null;
        var loaded = await setupService.LoadAsync(token);
        if (loaded.Error is not null || CompanionSettings.Begin(loaded.Settings).Companion is not { } companion) return null;
        if (lorebooks is null) return (companion, LorebookLibrary.Create());
        var lore = await lorebooks.LoadAsync(token);
        return lore.Loaded ? (companion, lore.Library) : null;
    }

    // ---------- keeping it the same ----------

    /// <summary>Every 30 seconds and after a change here: characters you talk to together follow their owners (a character its
    /// owner no longer shares together leaves your characters), and what you share is refreshed from your characters as they
    /// are now. Skipped while Martlet replies or hears you, and during an account switch.</summary>
    private async Task FollowHouseholdSharingAsync()
    {
        if (sharingBusy || closing || conversation?.Replying == true || openConversation?.HearingYou == true || !SharingFilesReady()) return;
        sharingBusy = true;
        var changed = false;
        try
        {
            if (await SharingSourcesAsync(lifetime.Token) is not { } sources) return;
            var (companion, library) = sources;
            var entries = HouseholdEntries();
            if (OwnSharing(entries) is not { } own) return;
            var notes = new List<string>();
            foreach (var joined in own.Joined)
            {
                // The owner's entry hasn't reached this PC yet: wait for it rather than take the character away.
                if (entries.GetValueOrDefault(joined.AccountId) is not { } owner) continue;
                if (owner.Find(joined.CharacterId) is { Mode: CharacterShareMode.Together } shared)
                {
                    CompanionSettings next;
                    try { next = SharedCharacters.Join(companion, shared); }
                    catch (ContractException error)
                    {
                        notes.Add($"'{shared.Name}' couldn't follow its owner's changes: {error.Message}");
                        continue;
                    }
                    if (!ReferenceEquals(next, companion))
                    {
                        if (await ChangeCompanionAsync(current => SharedCharacters.Join(current, shared), "A character you talk to together changed.") is not { } saved)
                            return;
                        companion = saved;
                        changed = true;
                    }
                    if (lorebooks is not null && !ReferenceEquals(SharedCharacters.MirrorLorebooks(library, shared), library))
                    {
                        var result = await lorebooks.UpdateAsync(current => SharedCharacters.MirrorLorebooks(current, shared), lifetime.Token);
                        if (result.Saved) library = result.Library;
                        changed = true;
                    }
                    continue;
                }
                var name = companion.CharacterList.FirstOrDefault(c => c.Id == joined.CharacterId)?.Name ?? "A character";
                if (!await LeaveMirrorAsync(joined.CharacterId)) return;
                notes.Add($"{AccountName(joined.AccountId)} no longer shares '{name}' together, so it left your characters.");
                ErrorLog.Info($"Sharing: a character shared together ({joined.CharacterId.ToString("N")[..8]}) is no longer shared; it left this account's characters.");
                changed = true;
                if (await SharingSourcesAsync(lifetime.Token) is not { } after) return;
                (companion, library) = after;
            }
            // What you share follows your characters (a renamed or edited one, a removed one no longer shared).
            await ChangeOwnSharingAsync(current => current.Refresh(companion, library));
            if (notes.Count > 0 && !closing) ActionText.Text = string.Join(" ", notes);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is ContractException or InvalidOperationException or System.IO.IOException or UnauthorizedAccessException)
        {
            ErrorLog.Warn("Sharing: couldn't keep the household's characters the same.", error);
        }
        finally
        {
            sharingBusy = false;
            if (changed && !closing)
            {
                RenderHome();
                if (openTab == CompanionTab.Profiles && !tabEdited) RenderTab();
            }
        }
    }

    // ---------- your characters ----------

    /// <summary>Shares one of your characters as <paramref name="mode"/>, or makes it private again (null).</summary>
    private async Task SetCharacterSharingAsync(CharacterProfile profile, CharacterShareMode? mode)
    {
        if (closing) return;
        if (!SharingFilesReady() || await SharingSourcesAsync(lifetime.Token) is not { } sources)
        {
            ActionText.Text = "Martlet can't share characters right now. Try again in a moment.";
            if (openTab == CompanionTab.Profiles) RenderTab();
            return;
        }
        try { await ChangeOwnSharingAsync(own => own.WithMode(profile.Id, mode, sources.Companion, sources.Lorebooks)); }
        catch (ContractException error)
        {
            ActionText.Text = $"Couldn't share '{profile.Name}': {error.Message}";
            if (openTab == CompanionTab.Profiles) RenderTab();
            return;
        }
        ErrorLog.Info($"Sharing: character {profile.Key} is now {mode?.ToString().ToLowerInvariant() ?? "private"}.");
        ActionText.Text = mode switch
        {
            CharacterShareMode.Copy => $"People in your household can now use a copy of '{profile.Name}'.",
            CharacterShareMode.Together => $"People in your household can now talk to '{profile.Name}'. It remembers everyone it talks to, in its own memories.",
            _ => $"'{profile.Name}' is private again. Copies people made stay theirs."
        };
        if (openTab == CompanionTab.Profiles) RenderTab();
    }

    /// <summary>A removed character of yours is no longer shared.</summary>
    private async Task StopSharingRemovedAsync(Guid character)
    {
        if (OwnSharing() is not { } own || own.ModeOf(character) is null) return;
        try { await ChangeOwnSharingAsync(current => current.WithMode(character, null, CompanionSettings.Create(), LorebookLibrary.Create())); }
        catch (ContractException error) { ErrorLog.Warn("Sharing: couldn't stop sharing a removed character.", error); }
    }

    /// <summary>The sharing choice and its state for one of your characters, or the state of a character you talk to together.
    /// The state line (<c>CharacterProfileShared-key</c>) never names anyone.</summary>
    private UIElement? ProfileSharingControls(CharacterProfile profile, HouseholdSharing? own)
    {
        if (own is null) return null;
        var panel = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
        TextBlock State(string text)
        {
            var state = Note(text, new Thickness(0, 2, 0, 0));
            AutomationProperties.SetAutomationId(state, "CharacterProfileShared-" + profile.Key);
            return state;
        }
        if (own.HasJoined(profile.Id))
        {
            panel.Children.Add(State("Shared with you together: everyone who talks to it shares its memories. Only its owner changes it."));
            return panel;
        }
        var mode = own.ModeOf(profile.Id);
        var choice = new ComboBox { MinHeight = 30, MinWidth = 220, HorizontalAlignment = HorizontalAlignment.Left };
        foreach (var (tag, text) in new (CharacterShareMode?, string)[]
                 {
                     (null, "Private"), (CharacterShareMode.Copy, "Share a copy with the household"),
                     (CharacterShareMode.Together, "Share together with the household")
                 })
        {
            var item = new ComboBoxItem { Content = text, Tag = tag };
            choice.Items.Add(item);
            if (tag == mode) choice.SelectedItem = item;
        }
        AutomationProperties.SetAutomationId(choice, "CharacterProfileSharing-" + profile.Key);
        AutomationProperties.SetName(choice, $"How {profile.Name} is shared");
        choice.SelectionChanged += (_, _) =>
        {
            var chosen = (choice.SelectedItem as ComboBoxItem)?.Tag as CharacterShareMode?;
            if (chosen != mode) SetCharacterSharingAsync(profile, chosen).Forget();
        };
        panel.Children.Add(choice);
        panel.Children.Add(State(mode switch
        {
            CharacterShareMode.Copy => "Shared as a copy: people in your household can use a copy, with its own memories.",
            CharacterShareMode.Together => "Shared together: people in your household talk to this character, and it remembers everyone in its own memories.",
            _ => "Private: only you see and use it."
        }));
        return panel;
    }

    // ---------- the household's characters ----------

    /// <summary>Companion › Profiles › Household characters: the characters other people share, with Use a copy or Talk to it.
    /// Its status line (<c>HouseholdCharactersStatus</c>) and each character's state (<c>HouseholdCharacterState-key</c>) are
    /// counts and fixed text only.</summary>
    private Border HouseholdCharactersCard(CompanionSettings? companion, CharacterModelLibrary library, Martlet.Core.Voices.SpeakingVoiceLibrary voices)
    {
        var stack = new List<UIElement>
        {
            Heading("Household characters"),
            Note("Characters other people in your household share. Use a copy to make one your own, with its own memories. Talk to a " +
                "character shared together and it remembers everyone it talks to.", new Thickness(0, 0, 0, 8))
        };
        TextBlock Status(string text)
        {
            var status = Note(text, new Thickness(0, 0, 0, 10));
            AutomationProperties.SetAutomationId(status, "HouseholdCharactersStatus");
            return status;
        }
        if (SharingAccount is not { } me)
        {
            stack.Add(Status("Household characters need an account for each person. They show once Martlet knows who uses it."));
            return Card([.. stack]);
        }
        var entries = HouseholdEntries();
        var own = OwnSharing(entries)!;
        var shared = entries.Where(e => e.Key != me).SelectMany(e => e.Value.Characters.Select(c => (Owner: e.Key, Character: c)))
            .OrderBy(s => s.Character.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
        var people = shared.Select(s => s.Owner).Distinct().Count();
        stack.Add(Status(shared.Length == 0 ? "Nobody in your household shares a character yet."
            : $"{shared.Length} character{(shared.Length == 1 ? "" : "s")} shared by {people} {(people == 1 ? "person" : "people")} in your household."));
        foreach (var (owner, character) in shared)
        {
            var joined = own.HasJoined(character.Id) && companion?.CharacterList.Any(c => c.Id == character.Id) == true;
            var asProfile = new CharacterProfile
            {
                Id = character.Id, Name = character.Name, PersonaId = character.Persona.Id, ModelId = character.ModelId, VoiceId = character.VoiceId
            };
            var problem = ProfileProblem(asProfile, library, voices);
            var state = character.Mode == CharacterShareMode.Copy ? "Shared as a copy."
                : joined ? "Shared together. It is in your profiles; use it there or here."
                : "Shared together. Talk to it to add it to your profiles.";
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = character.Name, FontSize = 15, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
            text.Children.Add(Note($"Shared by {AccountName(owner)} · Look: {LookName(character.ModelId, library)} · Voice: {VoiceName(character.VoiceId, voices)}",
                new Thickness(0, 2, 0, 0)));
            var stateText = Note(state + (problem is null ? "" : " " + problem), new Thickness(0, 2, 0, 0));
            AutomationProperties.SetAutomationId(stateText, "HouseholdCharacterState-" + character.Key);
            text.Children.Add(stateText);
            var (label, id, run) = character.Mode == CharacterShareMode.Copy
                ? ("Use a copy", "HouseholdCharacterCopy-", (Action)(() => UseSharedCopyAsync(owner, character.Id).Forget()))
                : (joined ? "Use" : "Talk to it", "HouseholdCharacterJoin-", (Action)(() => JoinSharedAsync(owner, character.Id).Forget()));
            var button = PageButton(label, run, id: id + character.Key);
            button.Margin = new Thickness(8, 0, 0, 0);
            button.VerticalAlignment = VerticalAlignment.Center;
            AutomationProperties.SetName(button, $"{label}: {character.Name}");
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
            DockPanel.SetDock(button, Dock.Right);
            row.Children.Add(button);
            row.Children.Add(text);
            stack.Add(row);
        }
        return Card([.. stack]);
    }

    /// <summary>Use a copy: the character becomes this account's own (new IDs, its own memories) and Martlet switches to it.</summary>
    private async Task UseSharedCopyAsync(Guid owner, Guid character)
    {
        if (closing) return;
        if (!SharingFilesReady() || HouseholdEntries().GetValueOrDefault(owner)?.Find(character) is not { Mode: CharacterShareMode.Copy } shared)
        {
            ActionText.Text = "That character isn't shared as a copy anymore.";
            if (openTab == CompanionTab.Profiles) RenderTab();
            return;
        }
        CharacterProfile? added = null;
        if (await ChangeCompanionAsync(companion =>
            {
                var (next, profile) = SharedCharacters.UseCopy(companion, shared);
                added = profile;
                return next;
            }, "You copied a character from your household.") is null || added is not { } copy)
            return;
        var note = "";
        if (lorebooks is not null && shared.Lorebooks.Count > 0)
        {
            var lore = await lorebooks.UpdateAsync(current => SharedCharacters.CopyLorebooks(current, shared, copy.PersonaId), lifetime.Token);
            if (!lore.Saved) note = " Its lorebooks couldn't be copied: " + lore.Error;
        }
        ErrorLog.Info($"Sharing: used a copy of a household character ({shared.Key}) as {copy.Key}.");
        await UseCharacterProfileAsync(copy.Id);
        ActionText.Text = $"'{copy.Name}' is your own copy now, with its own memories." + note;
        if (openTab == CompanionTab.Profiles) RenderTab();
    }

    /// <summary>Talk to it: the character shared together joins this account's characters (or, when it is there, is used) and
    /// Martlet switches to it.</summary>
    private async Task JoinSharedAsync(Guid owner, Guid character)
    {
        if (closing) return;
        if (!SharingFilesReady() || HouseholdEntries().GetValueOrDefault(owner)?.Find(character) is not { Mode: CharacterShareMode.Together } shared)
        {
            ActionText.Text = "That character isn't shared together anymore.";
            if (openTab == CompanionTab.Profiles) RenderTab();
            return;
        }
        if (await ChangeCompanionAsync(companion => SharedCharacters.Join(companion, shared), "A character shared together joined your characters.") is null)
            return;
        if (lorebooks is not null)
        {
            var lore = await lorebooks.UpdateAsync(current => SharedCharacters.MirrorLorebooks(current, shared), lifetime.Token);
            if (!lore.Saved) ErrorLog.Warn("Sharing: a character shared together joined without its lorebooks: " + lore.Error);
        }
        try { await ChangeOwnSharingAsync(own => own.WithJoined(owner, character)); }
        catch (ContractException error)
        {
            ActionText.Text = $"Couldn't add '{shared.Name}': {error.Message}";
            return;
        }
        ErrorLog.Info($"Sharing: this account talks to a character shared together ({shared.Key}).");
        await UseCharacterProfileAsync(character);
        if (!ActionText.Text.StartsWith("Couldn't", StringComparison.Ordinal))
            ActionText.Text = $"You talk to '{shared.Name}' together with your household now. It remembers everyone it talks to.";
        if (openTab == CompanionTab.Profiles) RenderTab();
    }

    /// <summary>Leave: a character you talk to together leaves your characters, after a question. What it remembers stays with
    /// it, for the others who talk to it.</summary>
    private async Task LeaveSharedCharacterAsync(CharacterProfile profile)
    {
        if (closing) return;
        if (!ConfirmationDialog.Confirm(this, $"Leave '{profile.Name}'?\n\nIt goes from your characters on all your computers. What it " +
                "remembers stays with it for the others who talk to it. You can talk to it again from Household characters.", "Leave character"))
            return;
        if (!await LeaveMirrorAsync(profile.Id)) return;
        ActionText.Text = $"'{profile.Name}' left your characters.";
        if (!closing) RenderHome();
        if (!closing && openTab == CompanionTab.Profiles) RenderTab();
    }

    /// <summary>Takes a character shared together out of this account: its profile, personality and lorebooks, and the record
    /// that this account talks to it.</summary>
    private async Task<bool> LeaveMirrorAsync(Guid character)
    {
        Guid? persona = null;
        if (await ChangeCompanionAsync(companion =>
            {
                var (next, gone) = SharedCharacters.Leave(companion, character);
                persona = gone;
                return next;
            }, "A character shared together left your characters.") is null)
            return false;
        if (persona is { } gone && lorebooks is not null)
            await lorebooks.UpdateAsync(current => SharedCharacters.DropLorebooks(current, gone), lifetime.Token);
        await ChangeOwnSharingAsync(own => own.WithoutJoined(character));
        CharacterProfileLocalStore.Save(store?.DataDirectory, ProfilesHere());
        return true;
    }
}
