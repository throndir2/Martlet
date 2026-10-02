using System.Windows.Threading;
using Martlet.Avatar.Audio2Face.Remote;
using Martlet.Home;

namespace Martlet.Desktop;

/// <summary>Keeps one Home Assistant connection the same on all of the owner's computers. Each paired host keeps a copy
/// (address and access token, newest revision wins, see docs/CLUSTER.md); this PC takes a newer shared connection when it
/// has none or follows the shared one, and gives hosts with an older copy its own when it shares.</summary>
public partial class MainWindow
{
    private readonly DispatcherTimer homeShareTimer = new() { Interval = TimeSpan.FromSeconds(60) };
    private bool homeShareBusy;
    /// <summary>The newest connection shared through the hosts, and how many hosts keep it (for the Smart home page).</summary>
    private SharedHomeAssistant? homeShared;
    private int homeShareHosts;
    private string homeShareStatus = "Not checked yet.";

    private void InitializeHomeShare() => homeShareTimer.Tick += (_, _) => SyncHomeShareAsync().Forget();

    private void StartHomeShare()
    {
        if (store is null || closing) return;
        homeShareTimer.Start();
        SyncHomeShareAsync().Forget();
    }

    /// <summary>Reads every paired host's shared connection, applies a newer one here (<see cref="SmartHome.AdoptAsync"/>),
    /// and sends <paramref name="push"/> (or this PC's followed connection) to hosts whose copy is older. Returns how many
    /// hosts keep the newest value afterwards.</summary>
    private async Task<int> SyncHomeShareAsync(SharedHomeAssistant? push = null)
    {
        if (closing || store is null) return 0;
        if (homeShareBusy && push is null) return homeShareHosts;
        while (homeShareBusy) await Task.Delay(100);
        var hosts = NetworkMap.Hosts(Inputs());
        if (hosts.Count == 0)
        {
            homeShareStatus = "No Martlet hosts are paired, so only this PC uses this connection.";
            return 0;
        }
        homeShareBusy = true;
        try
        {
            var reads = await Task.WhenAll(hosts.Select(host => ReadShareAsync(host, push)));
            var reachable = reads.Where(r => r.Ok).ToArray();
            var newest = reachable.Select(r => r.Value).OfType<SharedHomeAssistant>()
                .OrderByDescending(v => v.Revision).ThenByDescending(v => v.UpdatedBy, StringComparer.Ordinal).FirstOrDefault();
            string? change = null;
            if (newest is not null && push is null)
            {
                var from = reachable.First(r => ReferenceEquals(r.Value, newest)).HostId;
                try { change = await smartHome.AdoptAsync(newest, from, lifetime.Token); }
                catch (Exception error) when (error is HomeAssistantException or Martlet.Core.Contracts.ContractException)
                {
                    change = $"Couldn't use the Home Assistant your other computers share: {error.Message}";
                }
            }
            // A followed connection newer than a host's copy goes to that host (for example a newly paired one).
            var saved = smartHome.Preferences;
            if (push is null && saved.FollowShare && smartHome.Connected &&
                reachable.Any(r => r.Value is null || r.Value.Revision < saved.SharedRevision) &&
                smartHome.ShareValue(saved.SharedRevision) is { } mine)
            {
                var again = await Task.WhenAll(reachable.Where(r => r.Value is null || r.Value.Revision < saved.SharedRevision)
                    .Select(r => hosts.First(h => h.HostId == r.HostId)).Select(host => ReadShareAsync(host, mine)));
                reachable = [.. reachable.Where(r => again.All(a => a.HostId != r.HostId)), .. again.Where(a => a.Ok)];
                newest = mine.Revision >= (newest?.Revision ?? 0) ? mine : newest;
            }
            homeShared = newest;
            homeShareHosts = newest is null ? 0 : reachable.Count(r => r.Value?.Revision == newest.Revision);
            var old = reads.Where(r => r.Old).Select(r => r.HostId).ToArray();
            homeShareStatus = (newest is null || newest.Address is null
                    ? $"Nothing is shared through your {hosts.Count} Martlet host{(hosts.Count == 1 ? "" : "s")}."
                    : $"{HomeAssistantEndpoint.Display(new Uri(newest.Address.TrimEnd('/') + "/"))} is shared through {homeShareHosts} of {hosts.Count} host{(hosts.Count == 1 ? "" : "s")}.") +
                (reads.Length - reachable.Length - old.Length is > 0 and var away ? $" {away} didn't answer." : "") +
                (old.Length > 0 ? $" Update {string.Join(", ", old)} to share Home Assistant there." : "");
            if (change is not null && !closing) ActionText.Text = change;
            return homeShareHosts;
        }
        catch (OperationCanceledException) { return 0; }
        finally
        {
            homeShareBusy = false;
            if (SmartHomeCanRender()) RenderTab();
        }
    }

    private async Task<(string HostId, SharedHomeAssistant? Value, bool Ok, bool Old)> ReadShareAsync(PairedHost host, SharedHomeAssistant? push)
    {
        try
        {
            var value = await ClusterSync.WithConnectionAsync(host.Pairing, connection => push is null
                ? connection.ReadHomeAssistantAsync(lifetime.Token) : connection.ShareHomeAssistantAsync(push, lifetime.Token));
            return (host.HostId, value, true, false);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { throw; }
        catch (Audio2FaceHostException error) when (error.Code is "request.invalid" or "request.not_found" or "route.not_found")
        {
            return (host.HostId, null, false, true);
        }
        catch (Exception error) when (error is OperationCanceledException or ArgumentException || ClusterSync.IsHostFailure(error))
        {
            return (host.HostId, null, false, false);
        }
    }

    /// <summary>A revision newer than anything this PC has seen, never behind the clock (like the shared who-does-what plan).</summary>
    private long NextShareRevision() =>
        Math.Max(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Math.Max(homeShared?.Revision ?? 0, smartHome.Preferences.SharedRevision) + 1);

    /// <summary>Shares this PC's connection with the owner's other computers through every paired host.</summary>
    private async Task<bool> ShareHomeAssistantAsync()
    {
        var revision = NextShareRevision();
        if (smartHome.ShareValue(revision) is not { } value) { ActionText.Text = "Connect Home Assistant first."; return false; }
        var kept = await SyncHomeShareAsync(value);
        if (kept == 0 || homeShared?.Revision != revision)
        {
            ActionText.Text = "No Martlet host took the shared connection. Check that your hosts are on and up to date.";
            return false;
        }
        smartHome.MarkShared(revision);
        ActionText.Text = $"Home Assistant is shared with your other computers through {kept} host{(kept == 1 ? "" : "s")}.";
        RenderTab();
        return true;
    }

    /// <summary>Stops sharing: the hosts forget the token, computers that took it from them disconnect, and this PC keeps it.</summary>
    private async Task StopSharingHomeAssistantAsync()
    {
        if (!ConfirmationDialog.Confirm(this, "Stop sharing Home Assistant? Your Martlet hosts forget its access token, and your other " +
                "computers that use the shared connection disconnect from it. This PC stays connected.", "Martlet - smart home"))
            return;
        var revision = NextShareRevision();
        await SyncHomeShareAsync(new SharedHomeAssistant(revision, null, null, null, null));
        smartHome.MarkUnshared(revision);
        ActionText.Text = "Home Assistant is no longer shared. Only this PC uses its connection now.";
        RenderTab();
    }

    /// <summary>Replaces this PC's own connection with the one the owner's other computers share.</summary>
    private async Task UseSharedHomeAssistantAsync()
    {
        if (homeShared is not { Address: not null } shared) return;
        try
        {
            ActionText.Text = await smartHome.AdoptAsync(shared, "your other computers", lifetime.Token, replace: true) ??
                "This PC already uses the shared connection.";
        }
        catch (Exception error) when (error is HomeAssistantException or Martlet.Core.Contracts.ContractException) { ActionText.Text = error.Message; }
        RenderTab();
    }
}
