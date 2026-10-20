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

    private void InitializeHouseholdSharing()
    {
        sharingTimer.Tick += (_, _) => FollowHouseholdSharingAsync().Forget();
        // Another account's characters and sharing: its character in use may remember in a space of its own.
        if (accounts is not null) accounts.AccountChanged += () => Dispatcher.InvokeAsync(FollowCharacterSpace);
        // Share new memories about me: remembering sends a new fact about you to the household's memories (after a reply).
        if (memory is not null && accounts is not null)
            memory.NewFactSpace = voice => SharingAccount is null ? null : MemorySharing.SpaceForNewFact(voice, MyVoices(), ShareNewMemoriesAboutMe);
    }

    private void StartHouseholdSharing()
    {
        if (closing) return;
        sharingTimer.Start();
        FollowHouseholdSharingAsync().Forget();
    }

    // ---------- the account and the household entries ----------

    /// <summary>The account signed in now, or null without accounts.</summary>
    private Guid? SharingAccount => accounts?.AccountId;

    /// <summary>Whether this PC's settings files hold the signed-in account's settings now (never in the middle of a switch), so
    /// what it shares is read from its own characters.</summary>
    private bool SharingFilesReady() => accounts is { Switching: false } && store is not null &&
        AccountWorkingCopy.Load(store.DataDirectory)?.Account == accounts.AccountId;

    /// <summary>A household member's name, for *Shared by*: from the account directory, else an account signed in on this PC.</summary>
    private string HouseholdMemberName(Guid account) =>
        accounts?.Directory.Find(account) is { Removed: false } entry ? entry.Name
        : accounts?.SignedIn.FirstOrDefault(a => a.Id == account)?.Name ?? "someone in your household";

    /// <summary>Every account's household sharing entry in this PC's copy of the household settings.</summary>
    private IReadOnlyDictionary<Guid, HouseholdSharing> HouseholdEntries() =>
        settingsNode is null ? new Dictionary<Guid, HouseholdSharing>() : HouseholdSharing.All(settingsNode.Document.Settings.Select(s => (s.Key, s.Value)));

    /// <summary>What the signed-in account shares (empty when it shares nothing yet), or null without accounts.</summary>
    private HouseholdSharing? OwnSharing(IReadOnlyDictionary<Guid, HouseholdSharing>? entries = null) =>
        SharingAccount is { } me ? (entries ?? HouseholdEntries()).GetValueOrDefault(me) ?? HouseholdSharing.Empty(me) : null;

    /// <summary>The memory space of the character in use when it remembers on its own (one of yours shared together now or before,
    /// or one you talk to together), else null: then remembering uses the account's own space.</summary>
    private string? SharedCharacterSpace()
    {
        if (homeSettings?.Companion is not { ActiveCharacterId: { } id } companion || OwnSharing() is not { } own) return null;
        if (companion.CharacterList.FirstOrDefault(c => c.Id == id) is not { } profile || profile.PersonaId != companion.ActivePersonaId) return null;
        return own.RemembersOnItsOwn(id) || own.HasJoined(id) ? MemorySpaceId.Character(id) : null;
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
                notes.Add($"{HouseholdMemberName(joined.AccountId)} no longer shares '{name}' together, so it left your characters.");
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
                FollowCharacterSpace();
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
        FollowCharacterSpace();
        ActionText.Text = mode switch
        {
            CharacterShareMode.Copy => $"People in your household can now use a copy of '{profile.Name}'.",
            CharacterShareMode.Together => $"People in your household can now talk to '{profile.Name}'. It remembers everyone it talks to, in its own memories.",
            _ => $"'{profile.Name}' is private again. Copies people made stay theirs."
        };
        if (openTab == CompanionTab.Profiles) RenderTab();
    }

    // ---------- your memories ----------

    /// <summary>The Memory window's sharing: where facts can go, giving them through the hosts and *Share new memories about me*.
    /// Null without accounts.</summary>
    private MemorySharingOptions? MemorySharingOptionsNow() => SharingAccount is null ? null
        : new(MemoryShareTargets, GiveMemoriesToHostsAsync, ClusterDevice, ShareNewMemoriesAboutMe, SetShareNewMemoriesAboutMeAsync);
}
