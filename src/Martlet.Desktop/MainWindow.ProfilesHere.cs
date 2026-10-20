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

/// <summary>Character profiles, part 2: what each profile keeps on this PC, the links from Character and Personality, the
/// profiles on Home and in the notification-area menu.</summary>
public partial class MainWindow
{
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
        if (kept is null) return "On this PC: nothing yet (while you use it, it keeps where its character stands, where it looks and which touches stop it).";
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
                : $"You have {count} profile{(count == 1 ? "" : "s")}, each switching the look, voice and personality together.", new Thickness(0, 0, 0, 8)),
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
