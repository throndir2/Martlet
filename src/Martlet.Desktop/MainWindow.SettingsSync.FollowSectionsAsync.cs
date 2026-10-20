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
    /// <summary>What this PC does after it took the settings with <paramref name="keys"/> from another computer: the sections
    /// themselves only save files (#955), so the running parts and the pages follow here, once the sync finished.</summary>
    private async Task FollowSectionsAsync(IReadOnlySet<string> keys)
    {
        if (keys.Contains(CharacterActionsKey) && characterActions.Current is not null)
            await characterActions.LoadAsync(avatar.IsShowing ? avatar.InspectedProfile : homeAvatar, force: true, lifetime.Token);
        if (keys.Contains(TouchTemperamentKey)) characterTemperaments.Reload();
        if (keys.Contains(SmartHomeKey) && smartHome.Preferences is { Control: true, ModelTools: true }) mcpTools.EnsureStarted(retry: true);
        if (keys.Contains(ModelAbilities.SharedKey)) conversation?.ReloadAbilities();
        if (keys.Contains(PoolSettings.SharedKey))
        {
            // Speaking and Listening turn off (or on again) with their lists, as on the computer that changed them; the running
            // conversation takes the shared Vision and Hearing lists (#805).
            try
            {
                await FollowPoolOnOffAsync(PoolAreas.Speaking, SetupRole.Tts);
                await FollowPoolOnOffAsync(PoolAreas.Listening, SetupRole.Stt);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or ContractException)
            {
                ErrorLog.Info($"Pools: couldn't follow the shared lists' on and off here yet: {error.Message}");
            }
            conversation?.ReloadSenseModels();
        }
        if ((keys.Contains(WorkSharingSettings.SharedKey) || keys.Contains(PoolSettings.SharedKey)) && DevicesPage.IsVisible) RenderWorkSharing();
        if (keys.Contains(Martlet.Core.Access.OpenAiEndpointSettings.SharedKey) && SharePage.IsVisible) RenderOpenAi();
        if (keys.Contains(Martlet.Core.Planning.RecommendationPreferences.SharedKey) && SettingsPage.IsVisible) RenderRecommendationPreferences();
    }

    private void ShowSettingsStatus()
    {
        if (settingsNode is null)
        {
            SettingsSyncStatusText.Text = "Settings can't be shared until Martlet can use its data folder.";
            SettingsSyncWaitingText.Visibility = Visibility.Collapsed;
            return;
        }
        var shared = settingsNode.Document.Settings.Count(s => !SharedSettings.IsDeviceKey(s.Key) && (accounts is null || !SettingScopes.IsAccountKey(s.Key)));
        var own = accountNode?.Document.Settings.Count(s => !SharedSettings.IsDeviceKey(s.Key)) ?? 0;
        var parts = new List<string>();
        if (!clusterEnabled) parts.Add("Settings stay on this PC while this is off; changes made here are shared when you turn it on");
        else if (settingsCheckedAt is not { } checkedAt) parts.Add("Settings: checking your hosts");
        else if (settingsHosts.Hosts == 0) parts.Add("Settings: pair a Martlet host to share them with your other computers");
        else
        {
            parts.Add($"Settings: {shared} shared{(accounts is null ? "" : $" by the household and {own} of yours")}, the same on " +
                $"{settingsHosts.Current} of {settingsHosts.Hosts} host{(settingsHosts.Hosts == 1 ? "" : "s")}, " +
                $"checked {checkedAt.ToLocalTime():t}" +
                (settingsHosts.Down > 0 ? $", {settingsHosts.Down} not responding" : ""));
            if (settingsHosts.Old > 0) parts.Add($"update {(settingsHosts.Old == 1 ? "one host" : settingsHosts.Old + " hosts")} to share settings");
            if (accountNode is not null && accountHosts.Old > settingsHosts.Old)
                parts.Add($"update {(accountHosts.Old == 1 ? "one host" : accountHosts.Old + " hosts")} to keep each person's settings on all " +
                    "your computers");
        }
        if (accounts is not null && accountNode is null) parts.Add("your own settings don't sync until this PC finishes switching to you");
        if (settingsLastChange is not null && clusterEnabled) parts.Add(settingsLastChange.TrimEnd('.'));
        SettingsSyncStatusText.Text = string.Join("; ", parts) + ".";
        var waiting = settingsNode.Waiting.Concat(accountNode?.Waiting ?? new Dictionary<string, string>()).ToArray();
        SettingsSyncWaitingText.Text = waiting.Length == 0 ? "" :
            "Not followed here yet: " + string.Join("; ", waiting.Select(w => $"{SharedTitle(w.Key)} ({w.Value.TrimEnd('.')})")) + ".";
        SettingsSyncWaitingText.Visibility = waiting.Length == 0 || !clusterEnabled ? Visibility.Collapsed : Visibility.Visible;
        SettingsSyncClaimButton.IsEnabled = clusterEnabled;
    }
}
