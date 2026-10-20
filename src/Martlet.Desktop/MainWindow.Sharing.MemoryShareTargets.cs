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

/// <summary>More of MainWindow.Sharing.cs, in a file of its own so that each file stays under 20 KB.</summary>
public partial class MainWindow
{
    /// <summary>The places the Memory window can share facts to: the household's memories, each other person's memories (from the
    /// account directory and the people on this PC) and each character shared together that this account uses.</summary>
    private IReadOnlyList<MemoryShareTarget> MemoryShareTargets()
    {
        var targets = new List<MemoryShareTarget> { new(MemorySpaceId.Household, "The household's memories") };
        if (accounts is null || SharingAccount is not { } me) return targets;
        var people = accounts.Directory.Live.Where(a => a.Id != me).Select(a => (a.Id, a.Name))
            .Concat(accounts.SignedIn.Where(a => a.Id != me).Select(a => (a.Id, a.Name)))
            .DistinctBy(p => p.Id).OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase);
        foreach (var (id, name) in people) targets.Add(new(MemorySpaceId.Account(id), $"{name}'s memories"));
        var entries = HouseholdEntries();
        if (OwnSharing(entries) is not { } own) return targets;
        var together = own.Characters.Where(c => c.Mode == CharacterShareMode.Together)
            .Concat(own.Joined.Select(j => entries.GetValueOrDefault(j.AccountId)?.Find(j.CharacterId)).OfType<Martlet.Core.Sharing.SharedCharacter>()
                .Where(c => c.Mode == CharacterShareMode.Together));
        foreach (var character in together.DistinctBy(c => c.Id))
            targets.Add(new(character.Space, $"{character.Name}'s memories (shared together)"));
        return targets;
    }

    /// <summary>Gives facts to a memory space this PC doesn't keep (another person's memories) through every reachable paired
    /// host; returns how many the space took. A host only adds facts and never shows the space.</summary>
    private async Task<int> GiveMemoriesToHostsAsync(string space, SharedMemories gift, CancellationToken token)
    {
        var hosts = await Dispatcher.InvokeAsync(() => NetworkMap.Hosts(Inputs()));
        if (hosts.Count == 0)
            throw new DesktopMemoryException("memory.no_host", "Pair a Martlet host to share facts with someone else's memories.");
        var taken = -1;
        foreach (var host in hosts)
        {
            try { taken = Math.Max(taken, await ClusterSync.WithConnectionAsync(host.Pairing, connection => connection.GiveMemoriesAsync(space, gift, token))); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception error) when (error is OperationCanceledException or ArgumentException || ClusterSync.IsHostFailure(error))
            {
                ErrorLog.Warn($"Memory: {host.HostId} didn't take the shared facts.", error);
            }
        }
        return taken >= 0 ? taken
            : throw new DesktopMemoryException("memory.no_host", "None of your hosts took the facts. Check that they run an up-to-date Martlet, then try again.");
    }

    /// <summary>Makes memory use the space of the character in use: its own space when it is shared together, else the
    /// account's. Called after a switch of character or a change in sharing, between replies; the other spaces load again in the
    /// background, so recall never waits.</summary>
    private void FollowCharacterSpace()
    {
        if (memory?.Account is not { } account || closing) return;
        var character = SharedCharacterSpace();
        if (account.Character == character) return;
        memory.UseAccount(account with { Character = character });
        ErrorLog.Info(character is null ? "Memory: the character in use remembers in your own memories."
            : $"Memory: the character in use is shared together, so it remembers in its own memory space ({character[..18]}).");
        LoadMemorySpacesAsync().Forget();
        QueueMemorySync();
    }

    /// <summary>Whether new facts about the signed-in person go to the household memory space (*Share new memories about me*).</summary>
    internal bool ShareNewMemoriesAboutMe => OwnSharing()?.NewFactsAboutMe == true;

    /// <summary>The voices that are the signed-in person (People's links, W11).</summary>
    internal IReadOnlySet<string> MyVoices() => localVoices.Yours.Select(v => v.Id).ToHashSet(StringComparer.Ordinal);

    /// <summary>Turns *Share new memories about me* on or off: in the household entry (it works on every PC), and in the account
    /// directory entry's <c>sharing.memories_about_me</c> when the account is there already.</summary>
    internal async Task<bool> SetShareNewMemoriesAboutMeAsync(bool on)
    {
        if (!await ChangeOwnSharingAsync(own => own.WithNewFactsAboutMe(on))) return false;
        ErrorLog.Info($"Sharing: new facts about this person {(on ? "go to the household's memories" : "stay in the account's memories")}.");
        if (accounts?.Directory.Find(accounts.AccountId) is { Removed: false } entry && entry.Sharing.MemoriesAboutMe != on && store is not null &&
            networkState.Roster is not null)
        {
            try
            {
                using var key = NetworkIdentity.LoadOrCreate(store.DataDirectory, NetworkIdentity.DeviceId(homeHosts));
                accounts.Follow(accounts.Directory.Put(key, entry with { Sharing = entry.Sharing with { MemoriesAboutMe = on } }, DateTimeOffset.UtcNow), []);
                QueueAccountsSync();
            }
            catch (Exception error) when (error is ContractException or System.IO.IOException or UnauthorizedAccessException or
                InvalidOperationException or System.Security.Cryptography.CryptographicException)
            {
                ErrorLog.Warn("Sharing: couldn't write Share new memories about me into the household's account directory.", error);
            }
        }
        return true;
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
            _ => own.RemembersOnItsOwn(profile.Id) ? "Private: only you see and use it. It keeps the memories it made while shared." : "Private: only you see and use it."
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
            HelpTip.Explain("Characters other people in your household share. Use a copy to make one your own, with its own memories. Talk " +
                "to a character shared together and it remembers everyone it talks to.", new Thickness(0, 0, 0, 8), "HouseholdCharacters",
                "household characters", wiki: "Personality-and-Characters#sharing-a-character-with-your-household")
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
            text.Children.Add(Note($"Shared by {HouseholdMemberName(owner)} · Look: {LookName(character.ModelId, library)} · Voice: {VoiceName(character.VoiceId, voices)}",
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
        FollowCharacterSpace();
        return true;
    }
}
