using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using Martlet.Core.Access;
using Martlet.Core.Contracts;

namespace Martlet.Desktop;

/// <summary>
/// Apps and API keys (docs/API.md): keys that software outside the owner's Martlet network (Home Assistant, scripts,
/// future integrations) presents as Authorization: Bearer to call the hosts. This PC keeps the network's key list in
/// api-keys.json (names, scopes and SHA-256 verifiers; never a usable key) and merges it with every paired host every
/// 30 seconds and right after a change, so a key made or revoked on any of the owner's computers reaches every host. The
/// owner's own computers never need a key: they use their paired, signed connections.
/// </summary>
public partial class MainWindow
{
    internal const string ApiKeysFile = "api-keys.json";
    private static readonly object ApiKeysFileGate = new();
    private readonly DispatcherTimer apiKeysTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    private ApiKeyList apiKeys = ApiKeyList.Empty;
    /// <summary>When each key was last used, and on which host (key ID → newest report).</summary>
    private readonly Dictionary<string, (DateTimeOffset At, string HostId)> apiKeysUsed = new(StringComparer.Ordinal);
    private string? apiKeysSync;
    private bool apiKeysBusy;
    private bool apiKeysQueued;

    private void InitializeApiKeys()
    {
        apiKeysTimer.Tick += (_, _) => SyncApiKeysAsync().Forget();
        if (store is not null) apiKeys = LoadApiKeys(store.DataDirectory);
        RenderApiKeys();
    }

    private void StartApiKeys()
    {
        if (store is null || closing) return;
        apiKeysTimer.Start();
        SyncApiKeysAsync().Forget();
    }

    private void QueueApiKeySync()
    {
        if (closing || store is null) return;
        if (apiKeysBusy) { apiKeysQueued = true; return; }
        Dispatcher.InvokeAsync(() => SyncApiKeysAsync().Forget(), DispatcherPriority.ContextIdle);
    }

    internal static ApiKeyList LoadApiKeys(string dataDirectory)
    {
        lock (ApiKeysFileGate)
        {
            try { return ApiKeyList.Parse(File.ReadAllBytes(Path.Combine(dataDirectory, ApiKeysFile))); }
            catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { return ApiKeyList.Empty; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ContractException)
            {
                ErrorLog.Warn($"{ApiKeysFile} could not be read ({error.Message}); your hosts' copies restore it.");
                return ApiKeyList.Empty;
            }
        }
    }

    private static void SaveApiKeys(string dataDirectory, ApiKeyList list)
    {
        lock (ApiKeysFileGate)
        {
            Directory.CreateDirectory(dataDirectory);
            var path = Path.Combine(dataDirectory, ApiKeysFile);
            var temporary = Path.Combine(dataDirectory, $"api-keys.{Guid.NewGuid():N}.tmp");
            try
            {
                File.WriteAllBytes(temporary, list.Write());
                File.Move(temporary, path, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }
    }

    /// <summary>Reads every paired host's copy, merges it here and gives each host whose copy differs the merged list.
    /// Hosts older than API keys are named so the owner can update them.</summary>
    private async Task SyncApiKeysAsync()
    {
        if (apiKeysBusy || closing || store is null) return;
        var hosts = homeHosts;
        if (hosts.Count == 0)
        {
            apiKeysSync = null;
            RenderApiKeys();
            return;
        }
        apiKeysBusy = true;
        try
        {
            var directory = store.DataDirectory;
            var results = await Task.WhenAll(hosts.Select(async host =>
            {
                try
                {
                    var mine = LoadApiKeys(directory);
                    var copy = await ClusterSync.WithConnectionAsync(host.Pairing, connection => connection.ReadApiKeysAsync(lifetime.Token));
                    var merged = ApiKeyList.Merge(mine, copy.Keys);
                    if (copy.Keys.Digest() != merged.Digest())
                        copy = await ClusterSync.WithConnectionAsync(host.Pairing, connection => connection.MergeApiKeysAsync(merged, lifetime.Token));
                    return (host.HostId, Copy: copy, Old: false);
                }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { throw; }
                catch (Martlet.Avatar.Audio2Face.Remote.Audio2FaceHostException error) when (error.Code is "request.invalid" or "request.not_found" or "route.not_found")
                {
                    return (host.HostId, Copy: (Martlet.Avatar.Audio2Face.Remote.HostApiKeys?)null, Old: true);
                }
                catch (Exception error) when (error is OperationCanceledException || ClusterSync.IsHostFailure(error))
                {
                    return (host.HostId, Copy: (Martlet.Avatar.Audio2Face.Remote.HostApiKeys?)null, Old: false);
                }
            }));
            if (closing) return;
            var list = LoadApiKeys(directory);
            foreach (var result in results.Where(r => r.Copy is not null))
            {
                list = ApiKeyList.Merge(list, result.Copy!.Keys);
                foreach (var (id, at) in result.Copy.Used)
                    if (!apiKeysUsed.TryGetValue(id, out var seen) || seen.At < at) apiKeysUsed[id] = (at, result.HostId);
            }
            if (list.Digest() != LoadApiKeys(directory).Digest()) SaveApiKeys(directory, list);
            apiKeys = list;
            var ok = results.Count(r => r.Copy is not null);
            var old = results.Where(r => r.Old).Select(r => r.HostId).ToArray();
            apiKeysSync = $"On {ok} of {results.Length} host{(results.Length == 1 ? "" : "s")}, checked {DateTime.Now:t}." +
                (old.Length > 0 ? $" Update {string.Join(", ", old)} (Update host) to use keys there." : "");
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ContractException or InvalidOperationException)
        {
            apiKeysSync = "Couldn't sync your API keys this time: " + error.Message;
            ErrorLog.Warn("API key sync failed: " + error.Message);
        }
        finally
        {
            apiKeysBusy = false;
            if (!closing)
            {
                RenderApiKeys();
                if (apiKeysQueued)
                {
                    apiKeysQueued = false;
                    QueueApiKeySync();
                }
            }
        }
    }

    private void ApiKeyCreate_Click(object sender, RoutedEventArgs e)
    {
        if (store is null) return;
        if (ApiKeyCreateDialog.Ask(this) is not { } request) return;
        IssuedApiKey issued;
        try
        {
            var (next, key) = LoadApiKeys(store.DataDirectory).Create(request.Name, request.Scopes, request.ExpiresAt,
                NetworkIdentity.DeviceId(homeHosts), DateTimeOffset.UtcNow);
            SaveApiKeys(store.DataDirectory, next);
            apiKeys = next;
            issued = key;
        }
        catch (Exception error) when (error is ContractException or IOException or UnauthorizedAccessException)
        {
            ActionText.Text = "Couldn't create the API key: " + error.Message;
            return;
        }
        ErrorLog.Info($"API key \"{issued.Key.Name}\" created ({string.Join(", ", issued.Key.Scopes)}).");
        ActionText.Text = $"Created API key \"{issued.Key.Name}\"; it reaches your hosts within a minute.";
        RenderApiKeys();
        QueueApiKeySync();
        ApiKeyCreatedDialog.Show(this, issued, homeHosts);
    }

    private void RevokeApiKey(ApiKey key)
    {
        if (store is null) return;
        if (!ConfirmationDialog.Confirm(this,
                $"Revoke \"{key.Name}\"? Apps using it stop working on every host in your network as soon as each host hears about it " +
                "(right away for hosts that answer now). This can't be undone; make a new key if the app needs access again.",
                "Revoke key"))
            return;
        try
        {
            var next = LoadApiKeys(store.DataDirectory).Revoke(key.Id, NetworkIdentity.DeviceId(homeHosts), DateTimeOffset.UtcNow);
            SaveApiKeys(store.DataDirectory, next);
            apiKeys = next;
        }
        catch (Exception error) when (error is ContractException or IOException or UnauthorizedAccessException)
        {
            ActionText.Text = "Couldn't revoke the API key: " + error.Message;
            return;
        }
        ErrorLog.Info($"API key \"{key.Name}\" revoked.");
        ActionText.Text = $"Revoked API key \"{key.Name}\"; your hosts refuse it as soon as they hear about it.";
        RenderApiKeys();
        QueueApiKeySync();
    }

    // ---------- presentation ----------

    private void RenderApiKeys()
    {
        var now = DateTimeOffset.UtcNow;
        var shown = apiKeys.Keys.Where(k => !k.Revoked).OrderBy(k => k.Expired(now)).ThenBy(k => k.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
        ApiKeyCreateButton.IsEnabled = store is not null;
        ApiKeysStatusText.Text = store is null ? "Unavailable without a local data folder."
            : (shown.Length == 0 ? "No API keys yet. Your own computers never need one; make one for each app or script that should use your hosts."
                : $"{shown.Length} API key{(shown.Length == 1 ? "" : "s")}.") + " " +
              (homeHosts.Count == 0 ? "Pair a host first: keys work on your hosts." : apiKeysSync ?? "Checking your hosts...");
        ApiKeysPanel.Children.Clear();
        foreach (var key in shown) ApiKeysPanel.Children.Add(ApiKeyRow(key, now));
    }

    private FrameworkElement ApiKeyRow(ApiKey key, DateTimeOffset now)
    {
        var row = NetworkRowFrame();
        var revoke = new Button { Content = "Revoke", VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetAutomationId(revoke, "ApiKeyRevoke-" + key.Id);
        AutomationProperties.SetName(revoke, $"Revoke API key {key.Name}");
        revoke.Click += (_, _) => RevokeApiKey(key);
        DockPanel.SetDock(revoke, Dock.Right);
        row.Children.Add(revoke);
        var may = string.Join("; ", key.Scopes.Select(ApiKeyScopes.Title)) + ".";
        var made = $"Made on {key.CreatedBy} {key.CreatedAt.ToLocalTime():d}.";
        var used = apiKeysUsed.TryGetValue(key.Id, out var seen) ? $"Last used {seen.At.ToLocalTime():g} on {seen.HostId}." : "Not used since your hosts started.";
        var expires = key.ExpiresAt is not { } at ? "Never expires." : key.Expired(now) ? $"Expired {at.ToLocalTime():d}." : $"Expires {at.ToLocalTime():d}.";
        row.Children.Add(NetworkRowText("ApiKeyRow-" + key.Id, key.Name, $"{may} {made} {used} {expires} ID {key.Id[..6]}."));
        return NetworkRowCard(row, warning: key.Expired(now));
    }
}
