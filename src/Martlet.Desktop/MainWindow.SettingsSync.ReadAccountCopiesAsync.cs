using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Threading;
using Martlet.HostClient;
using Martlet.Avatar.Hosting;
using Martlet.Avatars;
using Martlet.Core.Cluster;
using Martlet.Core.Contracts;
using Martlet.Core.Lorebooks;
using Martlet.Core.Network;
using Martlet.Core.Settings;
using Martlet.Core.Sync;
using Martlet.Credentials.Windows;
using Martlet.Providers;

namespace Martlet.Desktop;

/// <summary>More of MainWindow.SettingsSync.cs, in a file of its own so that each file stays under 20 KB.</summary>
public partial class MainWindow
{
    /// <summary>The hosts' copies of <paramref name="account"/>'s settings, read in parallel for a few seconds at most: a switch
    /// takes what they have, and the next sync brings anything slower.</summary>
    private async Task<IReadOnlyList<SharedSettings>> ReadAccountCopiesAsync(Guid account, CancellationToken token)
    {
        var reads = await Task.WhenAll(NetworkMap.Hosts(Inputs()).Select(async host =>
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(3));
                return await ClusterSync.WithConnectionAsync(host.Pairing, connection => connection.ReadAccountSettingsAsync(account, timeout.Token));
            }
            catch (Exception error) when (error is OperationCanceledException or ArgumentException or HostConnectionException || ClusterSync.IsHostFailure(error))
            {
                return null;
            }
        }));
        return [.. reads.OfType<SharedSettings>()];
    }

    /// <summary>Starts the settings sync. It runs (recording changes made here) even while sync is off; only then does it
    /// leave the hosts alone.</summary>
    private void StartSettingsSync()
    {
        if (settingsNode is null || closing) return;
        settingsTimer.Start();
        SyncSettingsAsync().Forget();
    }

    private void QueueSettingsSync()
    {
        if (settingsNode is not null && !closing) Dispatcher.InvokeAsync(() => SyncSettingsAsync().Forget(), DispatcherPriority.ContextIdle);
    }

    private async Task SyncSettingsAsync()
    {
        if (settingsNode is null || settingsBusy || closing) return;
        // Pages, Setup and who does what change settings.json too; the next check runs once they are done.
        if (changes.Busy || setupOperations.IsRunning) return;
        settingsBusy = true;
        ChangeTurns.Turn? turn = null;
        SharedSettingsResult? result = null;
        try
        {
            var shared = clusterEnabled;
            var hosts = shared ? NetworkMap.Hosts(Inputs()) : [];
            var reads = await Task.WhenAll(hosts.Select(ReadSettingsCopyAsync));
            var account = accountNode;
            var accountId = accountNodeFor;
            var accountReads = account is not null && accountId is { } readFor
                ? await Task.WhenAll(hosts.Select(host => ReadAccountCopyAsync(host, readFor)))
                : [];
            if (closing) return;
            if (setupOperations.IsRunning || (turn = changes.TryTake()) is null) return;
            // The owner keeps the account entries older Martlets leave in the household's copy (docs/ACCOUNTS.md).
            var owner = accounts is { } session && accountId is { } signedIn && session.OwnerId == signedIn;
            SharedSettingsResult? accountResult;
            // What the sync takes from another computer and saves here is a remote change, not one made on this PC.
            using (settingsChanges.From(SettingsChangeSource.RemoteSync))
                (result, accountResult) = await AccountSettingsSync.SyncAsync(settingsNode, account, owner,
                    [.. reads.Select(r => r.Copy).OfType<SharedSettings>()], [.. accountReads.Select(r => r.Copy).OfType<SharedSettings>()],
                    shared, DateTimeOffset.UtcNow, lifetime.Token);
            turn.Dispose();
            if (shared) await PushSettingsAsync(reads, result.Document);
            if (shared && accountResult is not null && accountId is { } pushFor) await PushAccountSettingsAsync(accountReads, pushFor, accountResult.Document);
            if (shared) settingsCheckedAt = DateTimeOffset.UtcNow;
            var digest = settingsNode.Document.Digest();
            var reachable = reads.Where(r => r.Ok).ToArray();
            settingsHosts = (reachable.Count(r => settingsCopies.GetValueOrDefault(r.HostId).Digest == digest), hosts.Count,
                reads.Count(r => !r.Ok && !r.Old), reads.Count(r => r.Old));
            if (accountResult is not null)
            {
                var accountDigest = accountResult.Document.Digest();
                accountHosts = (accountReads.Count(r => r.Ok && accountSettingsCopies.GetValueOrDefault(r.HostId).Digest == accountDigest), hosts.Count,
                    accountReads.Count(r => r.Old));
            }
            foreach (var key in result.Recorded.Where(k => !SharedPc.IsRoleKey(k)))
                ErrorLog.Info(SharedPc.IsKey(key)
                    ? $"Shared settings: told your other computers that this PC is a {(Role == DeviceRole.Host ? "host" : "companion")} PC" +
                      (OwnHostServiceId() is { } own ? $" and runs {own}." : ".")
                    : $"Shared settings: {SharedTitle(key)} changed on this PC; your other computers follow it.");
            foreach (var key in accountResult?.Recorded.Where(k => !k.StartsWith(SharedSettings.RemindersPrefix, StringComparison.Ordinal)) ?? [])
                ErrorLog.Info($"Shared settings: {SharedTitle(key)} changed on this PC for account {AccountSession.Short(accountId!.Value)}; " +
                    "the computers where it is signed in follow it.");
            IReadOnlyList<SharedSettingsChange> applied = [.. result.Applied, .. accountResult?.Applied ?? []];
            if (applied.Count > 0) await AfterSettingsAppliedAsync(applied);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ContractException or InvalidOperationException or JsonException)
        {
            if (!closing) ActionText.Text = "Couldn't sync settings with your other computers: " + error.Message;
            ErrorLog.Warn("Shared settings sync failed.", error);
        }
        finally
        {
            turn?.Dispose();
            settingsBusy = false;
            if (!closing)
            {
                ShowSettingsStatus();
                // Another computer's run applying the recommended setup arrives with the settings.
                ShowConfiguring();
                // Another computer's role (companion or host PC) arrives with the settings and changes how the map draws it.
                if (DevicesPage.IsVisible && NetworkDevicesSignature() != networkDevicesShown) RenderMap();
                // An ask made here that the other computer followed is said once; Settings lists what each computer is now.
                ObserveRoleAsks();
                if (SettingsPage.IsVisible) RenderOtherRoles();
            }
        }
    }

    private static string SharedTitle(string key) => key switch
    {
        AppSettingsSections.Thinking => "Thinking",
        AppSettingsSections.Listening => "Listening",
        AppSettingsSections.Speaking => "Speaking",
        AppSettingsSections.ThinkingFallback => "the Thinking fallback",
        AppSettingsSections.ThinkingPool => "the Thinking pool's cloud models",
        AppSettingsSections.ThinkingPoolChoices => "the Thinking pool's choices",
        AppSettingsSections.Companion => "the personality",
        AppSettingsSections.Replies => "reply settings",
        AppSettingsSections.Prompts => "prompts",
        AppSettingsSections.Memory => "memory",
        AppSettingsSections.Lorebooks => "lorebooks",
        CharacterKey => "the character",
        TalkKey => "how you talk",
        SpeechDisplayKey => "speech bubbles and subtitles",
        AppearanceKey => "the theme",
        AppearanceCustomKey => "your custom palette",
        CharacterActionsKey => "emotes and motions",
        TouchTemperamentKey => "touch temperaments",
        ConversationHistoryKey => "your conversation history choices",
        MemoryBriefFile.SharedKey => "the memory brief",
        VoiceRecognitionKey => "recognizing voices",
        VoiceIdKey => "Voice ID",
        SmartHomeKey => "what Martlet may do with Home Assistant",
        UpdatesKey => "app updates",
        ModelAbilitiesKey => "what Thinking models hear and see",
        WorkSharingSettings.SharedKey => "sharing work between your computers",
        Martlet.Core.Motion.MotionSettings.SharedKey => "where Martlet makes motions",
        Martlet.Core.Planning.RecommendationPreferences.SharedKey => "your recommended setup preferences",
        _ when SharedPc.IsKey(key) || SharedPc.IsRoleKey(key) => "whether this PC is a companion or a host",
        _ => key
    };

    /// <summary>A host's copy of the shared settings: read again only when its digest changed. Hosts older than shared settings
    /// answer request.invalid.</summary>
    private async Task<(string HostId, PairedHost Host, SharedSettings? Copy, bool Ok, bool Old)> ReadSettingsCopyAsync(PairedHost host)
    {
        try
        {
            var copy = await ClusterSync.WithConnectionAsync(host.Pairing, async connection =>
            {
                var digest = await connection.ReadSettingsDigestAsync(lifetime.Token);
                if (settingsCopies.TryGetValue(host.HostId, out var known) && known.Digest == digest) return known.Copy;
                var read = await connection.ReadSettingsAsync(lifetime.Token);
                settingsCopies[host.HostId] = (read.Digest(), read);
                return read;
            });
            return (host.HostId, host, copy, true, false);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { throw; }
        catch (HostConnectionException error) when (error.Code is "request.invalid" or "request.not_found" or "route.not_found")
        {
            return (host.HostId, host, null, false, true);
        }
        catch (Exception error) when (error is OperationCanceledException or ArgumentException || ClusterSync.IsHostFailure(error))
        {
            return (host.HostId, host, null, false, false);
        }
    }

    /// <summary>Gives every reachable host whose copy differs the merged copy (with the API keys this PC knows); a host's reply
    /// may carry newer changes, which the next check follows.</summary>
    private async Task PushSettingsAsync(IEnumerable<(string HostId, PairedHost Host, SharedSettings? Copy, bool Ok, bool Old)> reads,
        SharedSettings merged)
    {
        var digest = merged.Digest();
        foreach (var read in reads.Where(r => r.Ok))
        {
            if (closing) return;
            if (settingsCopies.GetValueOrDefault(read.HostId).Digest == digest) continue;
            try
            {
                var copy = await ClusterSync.WithConnectionAsync(read.Host.Pairing, connection => connection.MergeSettingsAsync(merged, lifetime.Token));
                settingsCopies[read.HostId] = (copy.Digest(), copy);
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { throw; }
            catch (Exception error) when (error is OperationCanceledException or ArgumentException || ClusterSync.IsHostFailure(error))
            {
                ErrorLog.Warn($"Couldn't give {read.HostId} the shared settings; trying again on the next check.", error);
            }
        }
    }

    /// <summary>A host's copy of <paramref name="account"/>'s own settings: read again only when its digest changed. Hosts older
    /// than account settings answer route.not_found or request.invalid (Old); a host that refuses this PC for the account
    /// answers settings.account_denied (not Ok).</summary>
    private async Task<(string HostId, PairedHost Host, SharedSettings? Copy, bool Ok, bool Old)> ReadAccountCopyAsync(PairedHost host, Guid account)
    {
        try
        {
            var copy = await ClusterSync.WithConnectionAsync(host.Pairing, async connection =>
            {
                var digest = await connection.ReadAccountSettingsDigestAsync(account, lifetime.Token);
                if (accountSettingsCopies.TryGetValue(host.HostId, out var known) && known.Account == account && known.Digest == digest) return known.Copy;
                var read = await connection.ReadAccountSettingsAsync(account, lifetime.Token);
                accountSettingsCopies[host.HostId] = (account, read.Digest(), read);
                return read;
            });
            return (host.HostId, host, copy, true, false);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { throw; }
        catch (HostConnectionException error) when (error.Code is "request.invalid" or "request.not_found" or "route.not_found")
        {
            return (host.HostId, host, null, false, true);
        }
        catch (Exception error) when (error is OperationCanceledException or ArgumentException or HostConnectionException || ClusterSync.IsHostFailure(error))
        {
            return (host.HostId, host, null, false, false);
        }
    }

    /// <summary>Gives every reachable host whose copy of <paramref name="account"/>'s settings differs the merged copy.</summary>
    private async Task PushAccountSettingsAsync(IEnumerable<(string HostId, PairedHost Host, SharedSettings? Copy, bool Ok, bool Old)> reads,
        Guid account, SharedSettings merged)
    {
        var digest = merged.Digest();
        foreach (var read in reads.Where(r => r.Ok))
        {
            if (closing || accountNodeFor != account) return;
            if (accountSettingsCopies.GetValueOrDefault(read.HostId) is var known && known.Account == account && known.Digest == digest) continue;
            try
            {
                var copy = await ClusterSync.WithConnectionAsync(read.Host.Pairing, connection =>
                    connection.MergeAccountSettingsAsync(account, merged, lifetime.Token));
                accountSettingsCopies[read.HostId] = (account, copy.Digest(), copy);
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { throw; }
            catch (Exception error) when (error is OperationCanceledException or ArgumentException || ClusterSync.IsHostFailure(error))
            {
                ErrorLog.Warn($"Couldn't give {read.HostId} account {AccountSession.Short(account)}'s settings; trying again on the next check.", error);
            }
        }
    }

    /// <summary>Brings the rest of Martlet up to date after this PC took settings from another computer.</summary>
    private async Task AfterSettingsAppliedAsync(IReadOnlyList<SharedSettingsChange> applied)
    {
        foreach (var change in applied)
            ErrorLog.Info($"Shared settings: took {SharedTitle(change.Key)} from {change.By} (changed there {change.At.ToLocalTime():g}).");
        // A switch between companion and host PC asked from another computer says so itself (FollowRoleRequest).
        var shown = applied.Where(a => !SharedPc.IsRoleKey(a.Key)).ToArray();
        if (shown.Length > 0)
        {
            var by = shown.Select(a => a.By).Distinct(StringComparer.Ordinal).ToArray();
            settingsLastChange = $"{Sentence(string.Join(", ", shown.Select(a => SharedTitle(a.Key))))} now match{(shown.Length == 1 ? "es" : "")} " +
                $"{(by.Length == 1 && by[0] != ClusterDevice ? by[0] : "your other computers")} ({DateTimeOffset.Now:t}).";
            ActionText.Text = settingsLastChange;
        }
        if (applied.Any(a => a.Key == AppSettingsSections.Lorebooks)) homeLore = null;
        if (applied.Any(a => a.Key == AdvancedSettingsFile.SharedKey)) LoadAdvanced();
        if (applied.Any(a => a.Key is AppSettingsSections.ThinkingPool or AppSettingsSections.ThinkingPoolChoices)) conversation?.ReloadThinkingPool();
        await FollowSectionsAsync(applied.Select(a => a.Key).ToHashSet(StringComparer.Ordinal));
        await RefreshHomeAsync(SettingsChangeSource.RemoteSync);
        // A route taken from another computer is not a choice made here, so who does what doesn't record it as one.
        if (applied.Any(a => a.Key is AppSettingsSections.Thinking or AppSettingsSections.Listening or AppSettingsSections.Speaking))
            foreach (var job in clusterObserved.Keys.ToArray()) clusterObserved[job] = ObservedJob(job);
        if (characterChanged && avatar.IsShowing && Role == DeviceRole.Companion && !closing)
        {
            characterChanged = false;
            if (await StopAvatarSafelyAsync()) await ShowSavedCharacterAsync(onlyIfAutoShow: false);
        }
        characterChanged = false;
        if (openTab is not null && !tabEdited) RenderTab();
    }
}
