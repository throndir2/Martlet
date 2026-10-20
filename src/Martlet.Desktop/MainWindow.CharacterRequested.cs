using System.Windows;
using System.Windows.Automation;
using System.ComponentModel;
using System.IO;
using System.Diagnostics;
using System.Net.Http;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;
using Martlet.Core.Settings;
using Martlet.Core.Lorebooks;
using Martlet.Core.Installation;
using Martlet.Diagnostics;
using Martlet.Audio;
using Martlet.Audio.Windows;
using Martlet.Credentials.Windows;
using Martlet.Avatar.Hosting;

namespace Martlet.Desktop;

/// <summary>More of MainWindow.xaml.cs, in a file of its own so that each file stays under 20 KB.</summary>
public partial class MainWindow
{
    /// <summary>Carries out a choice from the character's own right-click menu (or Esc on it): hide the character, open
    /// Martlet, talk, open Companion › Character, lock or unlock its position, mute or unmute Martlet's voice, or choose where
    /// its eyes go (Eyes). The overlay handles its zoom, position and keep-on-top itself.</summary>
    private void CharacterRequested(string action)
    {
        if (closing) return;
        if (action == "placed")
        {
            RememberCharacterPlacementAsync().Forget();
            return;
        }
        if (action == "framed")
        {
            RememberCallFramingAsync().Forget();
            return;
        }
        ErrorLog.Info($"The character's menu chose '{action}'.");
        if (action.StartsWith(Martlet.Avatar.Hosting.RendererRequest.LookPrefix, StringComparison.Ordinal))
        {
            ChooseCharacterGaze(action);
            return;
        }
        switch (action)
        {
            case "hide":
                if (avatar.IsShowing) Character_Click(this, new RoutedEventArgs());
                break;
            case "open": ShowFromTray(); break;
            case "talk": TrayTalk(); break;
            case "settings":
                ShowFromTray();
                // Another Martlet window waiting for an answer keeps the focus, as from the notification area.
                if (IsWindowEnabled(WindowHandle)) OpenCompanion(CompanionTab.Character);
                break;
            case "lock":
                if (!avatar.PlacementLocked) SetCharacterLockAsync(true).Forget();
                break;
            case "unlock":
                if (avatar.PlacementLocked) SetCharacterLockAsync(false).Forget();
                break;
            case "mute": SetVoiceMuted(true); break;
            case "unmute": SetVoiceMuted(false); break;
            case Martlet.Avatar.Hosting.RendererRequest.ClickThroughOn:
                if (!avatar.ClickThrough) SetCharacterClickThroughAsync(true).Forget();
                break;
            case Martlet.Avatar.Hosting.RendererRequest.ClickThroughOff:
                if (avatar.ClickThrough) SetCharacterClickThroughAsync(false).Forget();
                break;
            case "clear": ClearCharacterEmotesAsync().Forget(); break;
            case Martlet.Avatar.Hosting.RendererRequest.ReadMore: ReadMoreFromBubble(); break;
        }
    }
    private async void ResetCharacter_Click(object sender, RoutedEventArgs e) => await ResetCharacterPositionAsync();
    private async void ResetCharacterZoom_Click(object sender, RoutedEventArgs e) => await ResetCharacterZoomAsync();
    private Task ResetCharacterZoomAsync() => ZoomCharacterAsync("reset");

    private bool changingCharacterLock, changingCharacterClickThrough;

    /// <summary>Lets clicks pass through the character (from Companion › Character, the notification-area menu or the
    /// character's own menu) or makes it catch them again (only from Martlet: the mouse can't reach the character's menu then),
    /// and saves that on this PC so the character shows the same way next time.</summary>
    private async Task SetCharacterClickThroughAsync(bool on)
    {
        if (closing || changingCharacterClickThrough) return;
        changingCharacterClickThrough = true;
        UpdateCharacterButton();
        try
        {
            await avatar.SetClickThroughAsync(on, lifetime.Token);
            var saved = CharacterClickThroughStore.Save(store?.DataDirectory, on);
            if (closing) return;
            ErrorLog.Info(on ? "Click-through turned on: clicks pass through the character." : "Click-through turned off: the character catches clicks.");
            ActionText.Text = (on
                ? "Clicks now pass through the character: turn this off in Companion › Character or from Martlet's icon in the notification area."
                : "Click-through is off, so you can drag, zoom and right-click the character again.") +
                (saved ? "" : on ? " It couldn't be saved on this PC, so it turns off when Martlet restarts."
                    : " It couldn't be saved on this PC, so it may turn on again when Martlet restarts.");
        }
        catch (Exception error) when (error is System.IO.IOException or InvalidOperationException or TimeoutException or
            OperationCanceledException or ObjectDisposedException or System.IO.InvalidDataException or System.Text.Json.JsonException)
        {
            if (!closing) ActionText.Text = $"Couldn't turn click-through {(on ? "on" : "off")}: {error.Message}";
        }
        finally
        {
            changingCharacterClickThrough = false;
            UpdateCharacterButton();
            if (!closing) RenderHome();
        }
    }

    /// <summary>The character was moved or resized and has settled: saves where it is now (and on which monitor) on this PC,
    /// so it shows there again after Hide/Show, a restart, a shutdown or an update.</summary>
    private async Task RememberCharacterPlacementAsync()
    {
        try
        {
            if (await avatar.ReadPlacementAsync(lifetime.Token) is not { } place || closing) return;
            if (CharacterPlacementStore.Save(store?.DataDirectory, place))
                ErrorLog.Info($"Character position saved at {place.Left:0}, {place.Top:0} ({place.Width:0} × {place.Height:0})" +
                    (place.Screen is { } screen ? $" on {CharacterScreen(screen)}." : "."));
            RememberProfileHere();
            UpdateCharacterButton();
            if (characterPlacementNote is { } note) note.Text = CharacterPlacementText();
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) when (RendererFailures.Is(error, lifetime.Token))
        {
            if (!closing) RendererFailures.Log("The character's new position couldn't be read to save it", error);
        }
    }

    /// <summary>A monitor's device name as people read it: DISPLAY2 rather than \\.\DISPLAY2.</summary>
    internal static string CharacterScreen(string device) => device.StartsWith(@"\\.\", StringComparison.Ordinal) ? device[4..] : device;

    /// <summary>Locks the showing character where it is or unlocks it (from Companion › Character or the character's own
    /// menu), and saves that on this PC so a locked character shows in the same place.</summary>
    private async Task SetCharacterLockAsync(bool locked)
    {
        if (closing || changingCharacterLock) return;
        changingCharacterLock = true;
        UpdateCharacterButton();
        try
        {
            var place = await avatar.LockPlacementAsync(locked, lifetime.Token);
            var saved = CharacterPlacementStore.Save(store?.DataDirectory, place);
            if (closing) return;
            RememberProfileHere();
            NoticeCharacterChange(locked ? global::Martlet.Conversation.PhysicalKind.Locked : global::Martlet.Conversation.PhysicalKind.Unlocked);
            ErrorLog.Info(locked && place is { } at
                ? $"Character position locked at {at.Left:0}, {at.Top:0} ({at.Width:0} × {at.Height:0})" +
                    (at.Screen is { } screen ? $" on {CharacterScreen(screen)}." : ".")
                : "Character position unlocked.");
            ActionText.Text = (locked
                ? "Character position locked: unlock it in Companion › Character or on the character's right-click menu to move it."
                : "Character position unlocked: drag the character to move it.") +
                (saved ? "" : locked ? " It couldn't be saved on this PC, so it unlocks when the character hides."
                    : " It couldn't be saved on this PC, so the character may show locked next time.");
        }
        catch (Exception error) when (error is System.IO.IOException or InvalidOperationException or TimeoutException or
            OperationCanceledException or ObjectDisposedException or System.IO.InvalidDataException or System.Text.Json.JsonException)
        {
            if (!closing) ActionText.Text = $"Couldn't {(locked ? "lock" : "unlock")} the character's position: {error.Message}";
        }
        finally
        {
            changingCharacterLock = false;
            UpdateCharacterButton();
            if (!closing) RenderHome();
        }
    }

    /// <summary>Companion › Character's line on whether the character's position is locked, where it is remembered and on which
    /// monitor.</summary>
    private string CharacterPlacementText()
    {
        var saved = avatar.Placement;
        var where = saved is { } spot ? $"{spot.Left:0}, {spot.Top:0}" + (spot.Screen is { } device ? $" on {CharacterScreen(device)}" : "") : "";
        return (saved, avatar.IsShowing) switch
        {
            ({ Locked: true } at, true) => $"Position locked at {where} ({at.Width:0} × {at.Height:0}): it can't move or change size until you unlock it, but zoom still works.",
            ({ Locked: true }, false) => $"Position locked at {where}, where the character shows when it opens.",
            ({ }, true) => $"Position unlocked, remembered at {where} on this PC: drag the character where you want it, then lock it here or from its right-click menu.",
            ({ }, false) => $"Position unlocked: the character shows where you left it, at {where}, or use Reset position if it's lost.",
            (null, true) => "Position unlocked: drag the character where you want it, then lock it here or from its right-click menu.",
            _ => "Position unlocked: the character shows at the lower-right; show it to place and lock it."
        };
    }

    /// <summary>The Character page's line on where the character is remembered, refreshed when it is moved.</summary>
    private TextBlock? characterPlacementNote;

    /// <summary>The Character page's line describing the overlay's current size, zoom and head framing.</summary>
    private TextBlock? characterViewText;

    /// <summary>Zooms the character "in", "out", "reset"s it, or only reads its "status", then shows the resulting view.</summary>
    private async Task ZoomCharacterAsync(string action)
    {
        try
        {
            var view = await avatar.ZoomAsync(action, lifetime.Token);
            if (closing) return;
            if (characterViewText is { } line) line.Text = view is null ? "" : CharacterViewText(view);
            if (action == "reset") ActionText.Text = "Character view reset.";
            else if (action != "status" && view is not null) ActionText.Text = $"Character zoomed {action}. {CharacterViewText(view)}";
        }
        catch (Exception error) when (error is System.IO.IOException or InvalidOperationException or TimeoutException or
            OperationCanceledException or ObjectDisposedException or System.IO.InvalidDataException or System.Text.Json.JsonException)
        {
            if (!closing && action != "status")
                ActionText.Text = action == "reset" ? $"Couldn't reset character zoom: {error.Message}" : $"Couldn't change character zoom: {error.Message}";
        }
        finally { UpdateCharacterButton(); }
    }

    internal static string CharacterViewText(RendererView view) =>
        $"Character view: {view.Width:0} × {view.Height:0}" +
        (view.DrawWidth is double drawn && drawn > view.Width + 0.5 ? $" ({drawn:0} wide with room to move)" : "") + view.ScreenTop switch
        {
            null => "",
            double below when below >= 0 => ", on screen",
            _ => ", partly above the screen"
        } + $", zoom {view.Zoom:0.##}x. " + view.HeadTop switch
        {
            null => "head position not available.",
            double below when below >= 0 => "head is in view.",
            _ => "head may be cropped."
        } + (view.Locked == true ? " Position locked." : "") + (view.ClickThrough == true ? " Clicks pass through." : "");
    private async Task ResetCharacterPositionAsync()
    {
        try
        {
            var showing = avatar.IsShowing;
            var place = await avatar.ResetPositionAsync(lifetime.Token);
            var saved = CharacterPlacementStore.Save(store?.DataDirectory, place);
            if (closing) return;
            RememberProfileHere();
            ErrorLog.Info(place is { } at
                ? $"Character position reset to {at.Left:0}, {at.Top:0}" + (at.Screen is { } screen ? $" on {CharacterScreen(screen)}." : ".")
                : "Character position reset; it shows at its default spot next time.");
            ActionText.Text = (showing
                ? "Character moved back to the lower-right of your main screen." + (place is { Locked: true } ? " It stays locked there." : "")
                : "Character position reset: it shows unlocked at the lower-right of your main screen next time.") +
                (saved ? "" : " It couldn't be saved on this PC.");
        }
        catch (Exception error) when (error is System.IO.IOException or InvalidOperationException or TimeoutException or
            OperationCanceledException or ObjectDisposedException or System.IO.InvalidDataException or System.Text.Json.JsonException)
        {
            if (!closing) ActionText.Text = $"Couldn't reset character position: {error.Message}";
        }
        finally
        {
            UpdateCharacterButton();
            if (!closing) RenderHome();
        }
    }
    /// <summary>Shows the saved character, or the bundled default when none is configured.</summary>
    private async Task ShowSavedCharacterAsync(bool onlyIfAutoShow)
    {
        if (store is null || setupService is null || closing || avatar.IsShowing) return;
        if (Role == DeviceRole.Host)
        {
            if (!onlyIfAutoShow) ActionText.Text = HostHasNoCompanionText;
            UpdateCharacterButton();
            return;
        }
        try
        {
            var (saved, profile) = await SavedCharacterAsync();
            if (onlyIfAutoShow && saved?.AutoShow != true) return;
            characterStartingAt = System.Diagnostics.Stopwatch.GetTimestamp();
            try { await avatar.ShowAsync(profile with { ResourceRevision = null }, lifetime.Token); }
            finally { characterStartingAt = 0; }
            characterCleanupProblem = null;
            ActionText.Text = avatar.Status;
        }
        catch (Exception error) when (error is UnauthorizedAccessException or System.ComponentModel.Win32Exception or
            Martlet.Core.Contracts.ContractException or OperationCanceledException || RendererFailures.Is(error, lifetime.Token))
        {
            if (closing) return;
            ErrorLog.Warn("The character couldn't be shown.", error);
            ActionText.Text = $"Couldn't show the character: {error.Message}";
        }
        finally { UpdateCharacterButton(); }
    }

    /// <summary>The saved character profile (null when none is saved) and the character Martlet shows for it: that profile, or
    /// the bundled default when there is none.</summary>
    private async Task<(AvatarProfile? Saved, AvatarProfile Shown)> SavedCharacterAsync()
    {
        var loaded = await setupService!.LoadAsync(lifetime.Token);
        AvatarProfile? saved = null;
        if (loaded.Settings is { } settings)
            saved = (await new AvatarProfileStore(store!.DataDirectory).LoadAsync(settings.Profile.Id, lifetime.Token)).Profile;
        // Without a completed Setup profile the bundled character still shows; its choices are simply not saved.
        return (saved, saved ?? AvatarProfile.BuiltIn(loaded.Settings?.Profile.Id ?? Guid.NewGuid()));
    }
    private void OpenAvatar(Window owner)
    {
        if (store is null || setupService is null || closing) return;
        if (Role == DeviceRole.Host)
        {
            ActionText.Text = HostHasNoCompanionText;
            return;
        }
        avatarWindowOpen = true;
        try
        {
            new AvatarWindow(avatar, new AvatarProfileStore(store.DataDirectory), setupService, setupOperations, captions, ShareCharacterAsync)
                { Owner = owner }.ShowDialog();
        }
        finally { avatarWindowOpen = false; }
        UpdateCharacterButton();
        RefreshHomeAsync().Forget();
    }
    private void AvatarSessionLocked(bool locked)
    {
        // Revocation ends privileged Audio2Face analysis; the character itself and local lip-sync stay available.
        if (locked) avatar.Revoke();
    }
    /// <summary>Why the character's last stop did not finish, or null. Shown on Companion › Character until a stop succeeds.</summary>
    private string? characterCleanupProblem;
    private async Task<bool> StopAvatarSafelyAsync()
    {
        try
        {
            await avatar.StopAsync();
            characterCleanupProblem = null;
            return true;
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or UnauthorizedAccessException ||
            RendererFailures.Is(error, CancellationToken.None))
        {
            ErrorLog.Warn("The character could not be stopped cleanly.", error);
            characterCleanupProblem = "The character didn't close cleanly: press Hide or Show character to try again.";
            if (!closing) ActionText.Text = characterCleanupProblem;
            return false;
        }
    }
    private async void Recovery_Click(object sender, RoutedEventArgs e)
    {
        OpenRecovery(this);
        await RefreshAsync();
    }
    private void OpenRecovery(Window owner)
    {
        if (recovery is null || closing) return;
        avatar.Revoke();
        new ConfigurationRecoveryWindow(recovery) { Owner = owner }.ShowDialog();
    }
    private void OpenTroubleshooting(Window owner)
    {
        if (troubleshooting is { } existing && existing.Owner == owner) { existing.Activate(); return; }
        var previous = troubleshooting;
        var next = new TroubleshootingWindow(support, RefreshAsync) { Owner = owner };
        next.Closed += (_, _) => { if (ReferenceEquals(troubleshooting, next)) troubleshooting = null; };
        // A pre-existing HWND is disabled by ShowDialog. Create a presentation in the active
        // modal context, not a replacement resource owner or a globally re-enabled window.
        next.Show();
        troubleshooting = next;
        previous?.CloseForPresentationTransfer();
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => ExitMartlet();
}
