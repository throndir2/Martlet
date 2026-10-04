using System.Windows.Threading;
using Martlet.Avatar.Audio2Face.Remote;
using Martlet.Home;

namespace Martlet.Desktop;

/// <summary>Home Assistant is one connection for the whole app: each paired host keeps a copy (address and access token,
/// newest revision wins, see docs/CLUSTER.md). While Martlet is the same on all the owner's computers, a connection made on
/// any of them is shared at once, every computer takes the newest one, and disconnecting disconnects everywhere.</summary>
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

    /// <summary>Reads every paired host's shared connection and applies a newer one here (<see cref="SmartHome.AdoptAsync"/>);
    /// a connection this PC has that no host keeps yet (one made before it was shared, or while no host answered) is shared.
    /// <paramref name="push"/> sends that value instead. Returns how many hosts keep the newest value afterwards.</summary>
    private async Task<int> SyncHomeShareAsync(SharedHomeAssistant? push = null)
    {
        var (kept, shareMine) = await SyncHomeShareCoreAsync(push);
        if (shareMine && !closing) return await ShareHomeAssistantAsync(quiet: true) ? homeShareHosts : kept;
        return kept;
    }

    private async Task<(int Kept, bool ShareMine)> SyncHomeShareCoreAsync(SharedHomeAssistant? push)
    {
        if (closing || store is null) return (0, false);
        if (!clusterEnabled)
        {
            homeShareStatus = "Keep Martlet the same on all my computers is off, so only this PC uses this connection.";
            return (0, false);
        }
        if (homeShareBusy && push is null) return (homeShareHosts, false);
        while (homeShareBusy) await Task.Delay(100);
        var hosts = NetworkMap.Hosts(Inputs());
        if (hosts.Count == 0)
        {
            homeShareStatus = "No Martlet hosts are paired, so only this PC uses this connection.";
            return (0, false);
        }
        homeShareBusy = true;
        var shareMine = false;
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
                    change = $"Couldn't use the Home Assistant your other computers use: {error.Message}";
                }
            }
            var saved = smartHome.Preferences;
            // A connection this PC has that the hosts don't know yet becomes the one every computer uses.
            shareMine = push is null && reachable.Length > 0 && smartHome.Connected && !saved.FollowShare && saved.SharedRevision >= (newest?.Revision ?? 0);
            // A followed connection newer than a host's copy goes to that host (for example a newly paired one).
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
                    ? $"No Home Assistant is kept by your {hosts.Count} Martlet host{(hosts.Count == 1 ? "" : "s")}."
                    : $"{HomeAssistantEndpoint.Display(new Uri(newest.Address.TrimEnd('/') + "/"))} is kept by {homeShareHosts} of {hosts.Count} host{(hosts.Count == 1 ? "" : "s")}.") +
                (reads.Length - reachable.Length - old.Length is > 0 and var away ? $" {away} didn't answer." : "") +
                (old.Length > 0 ? $" Update {string.Join(", ", old)} to share Home Assistant there." : "");
            if (change is not null && !closing) ActionText.Text = change;
            return (homeShareHosts, shareMine);
        }
        catch (OperationCanceledException) { return (0, false); }
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

    /// <summary>Makes this PC's connection the one every computer uses, through every paired host.</summary>
    private async Task<bool> ShareHomeAssistantAsync(bool quiet = false)
    {
        if (!clusterEnabled) return false;
        var revision = NextShareRevision();
        if (smartHome.ShareValue(revision) is not { } value) { if (!quiet) ActionText.Text = "Connect Home Assistant first."; return false; }
        var kept = await SyncHomeShareAsync(value);
        if (kept == 0 || homeShared?.Revision != revision)
        {
            if (!quiet) ActionText.Text = "No Martlet host took the connection. Check that your hosts are on and up to date.";
            return false;
        }
        smartHome.MarkShared(revision);
        if (!quiet) ActionText.Text = $"Your other computers use this Home Assistant too ({kept} host{(kept == 1 ? "" : "s")} keep it).";
        else ErrorLog.Info("Home Assistant: this PC's connection is now the one your other computers use.");
        RenderTab();
        return true;
    }

    /// <summary>Disconnects every computer: the hosts forget the token and the other computers disconnect too.</summary>
    private async Task<bool> DisconnectEverywhereAsync()
    {
        if (!clusterEnabled || NetworkMap.Hosts(Inputs()).Count == 0) return false;
        var revision = NextShareRevision();
        var kept = await SyncHomeShareAsync(new SharedHomeAssistant(revision, null, null, null, null));
        smartHome.Disconnect();
        smartHome.SeenShared(revision);
        return kept > 0;
    }
}
