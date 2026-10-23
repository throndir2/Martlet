using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using Martlet.HostClient;
using Martlet.Avatar.Hosting;
using Martlet.Core.Contracts;
using Martlet.Core.Network;
using Martlet.Core.Settings;
using Martlet.Credentials.Windows;

namespace Martlet.Desktop;

/// <summary>
/// The owner's Martlet network (docs/NETWORK.md). Every 20 seconds while Martlet runs (and right after a pairing or a
/// change here) this PC syncs with its paired hosts through <see cref="NetworkSyncEngine"/>: the first host it pairs starts
/// a network, hosts it pairs later join it, its other computers ask to join and a member allows them, and every member
/// pairs with every host of the network by itself. The Share page lists the network's computers, the requests to join
/// (with their check numbers) and Remove from network.
/// </summary>
public partial class MainWindow
{
    private static readonly TimeSpan NetworkInterval = TimeSpan.FromSeconds(20);
    private readonly DispatcherTimer networkTimer = new() { Interval = NetworkInterval };
    private NetworkLocalState networkState = NetworkLocalState.Empty;
    private IReadOnlyList<HostJoinRequest> networkJoins = [];
    private IReadOnlyDictionary<string, string> networkNotes = new Dictionary<string, string>();
    /// <summary>What each paired host said about the network on the last sync, including the computers paired with it.</summary>
    private IReadOnlyDictionary<string, HostNetworkView> networkViews = new Dictionary<string, HostNetworkView>();
    /// <summary>The last network picture written to the desktop log (only changes are logged).</summary>
    private string? networkLogged;
    private readonly Dictionary<string, string> networkNotesLogged = new(StringComparer.Ordinal);
    private bool networkWatchOnly;
    private readonly HashSet<string> networkAnnounced = new(StringComparer.Ordinal);
    /// <summary>Computers the owner allowed from Add a computer › Martlet on your network (device ID → until when): their
    /// request to join the network is approved without a second Allow.</summary>
    private readonly Dictionary<string, DateTimeOffset> networkPreapproved = new(StringComparer.Ordinal);
    private static readonly TimeSpan NetworkPreapproval = TimeSpan.FromMinutes(15);
    private DateTimeOffset? networkCheckedAt;
    private string? networkProblem;
    private bool networkBusy;
    private bool networkQueued;
    /// <summary>Hosts of your network with the same name as a host a friend shares with this PC (logged once each).</summary>
    private readonly HashSet<string> networkSameNames = new(StringComparer.Ordinal);
    /// <summary>Looks on the home network for hosts of your network that no host this PC reaches has heard of (kept across syncs).</summary>
    private readonly NetworkHostFinder networkFinder = NetworkHostFinder.HomeNetwork();
    /// <summary>Where your computers don't see the same network yet, as the last sync could tell.</summary>
    private NetworkReach networkReach = NetworkReach.None;
    private string? networkReachLogged;

    private void InitializeNetwork()
    {
        networkTimer.Tick += (_, _) => SyncNetworkAsync().Forget();
        InitializeOutsideRoutes();
        if (store is not null) networkState = NetworkIdentity.Load(store.DataDirectory);
        RenderNetwork();
    }

    private void StartNetwork()
    {
        if (store is null || closing) return;
        networkTimer.Start();
        SyncNetworkAsync().Forget();
    }

    /// <summary>Runs a network sync soon (after the current one, if one is running).</summary>
    private void QueueNetworkSync()
    {
        if (closing || store is null) return;
        if (networkBusy) { networkQueued = true; return; }
        Dispatcher.InvokeAsync(() => SyncNetworkAsync().Forget(), DispatcherPriority.ContextIdle);
    }

    private async Task SyncNetworkAsync()
    {
        // A host PC takes part too: it may be the only member that can let your other computers in (it started the network
        // while it was a companion), and it shows which computers use its host service.
        if (networkBusy || closing || store is null || setupService is null) return;
        networkBusy = true;
        var hostsChanged = false;
        var shownBefore = HostDashboardNetworkSignature();
        try
        {
            var directory = store.DataDirectory;
            var hosts = homeHosts;
            var before = NetworkIdentity.Load(directory);
            // Nothing to sync (and no key to make) before the first host is paired.
            if (hosts.Count == 0 && before.Roster is null && before.Waiting is null)
            {
                networkViews = new Dictionary<string, HostNetworkView>();
                networkCheckedAt = DateTimeOffset.Now;
                return;
            }
            var pairings = hosts.Select(h => GatewayAvatarHostLink.Pairing(h.Pairing)).ToArray();
            // A host PC outside a network only watches: it never starts or asks to join one by itself, so the network of the
            // main PC that pairs with its host service takes that host.
            networkWatchOnly = Role == DeviceRole.Host && before.Roster is null && before.Waiting is null;
            if (networkWatchOnly)
            {
                var watched = await NetworkSyncEngine.ReadOnlyAsync(before, pairings, ConnectToHost, lifetime.Token);
                if (closing) return;
                networkProblem = null;
                networkViews = watched.Views;
                networkNotes = watched.Notes;
                networkJoins = [];
                networkReach = NetworkReach.None;
                networkCheckedAt = DateTimeOffset.Now;
                LogNetworkPicture(before);
                ObserveHostReleases(watched.Views);
                return;
            }
            NetworkSyncResult result;
            // Your network never pairs a host of yours named like a host a friend shares with this PC: that would replace it.
            var sharedNames = sharedHosts.Select(h => h.HostId).Except(before.Ignored, StringComparer.Ordinal).ToArray();
            using (var key = NetworkIdentity.LoadOrCreate(directory, NetworkIdentity.DeviceId(hosts)))
            {
                var engine = new NetworkSyncEngine(key, Martlet.Core.Network.ThisComputer.Name);
                result = await engine.SyncAsync(before with { Ignored = [.. before.Ignored, .. sharedNames] }, pairings, ConnectToHost,
                    lifetime.Token, networkFinder);
            }
            foreach (var id in sharedNames.Where(id => result.State.Roster?.Host(id) is { Removed: false }))
                if (networkSameNames.Add(id))
                    ErrorLog.Warn($"Martlet network: your host {id} has the same name as a host a friend shares with this PC, so this PC " +
                        "doesn't pair it. Forget the shared one under Share › Hosts shared with this PC to use yours.");
            if (closing) return;
            networkProblem = null;
            foreach (var (pairing, secret) in result.Paired)
            {
                try
                {
                    await KeepNetworkPairingAsync(pairing, secret);
                    hostsChanged = true;
                }
                catch (Exception error) when (error is InvalidOperationException or IOException or UnauthorizedAccessException or ContractException)
                {
                    ErrorLog.Warn($"Martlet network: couldn't keep the pairing with {pairing.HostId}: {error.Message}");
                    networkProblem = $"Couldn't keep the pairing with {pairing.HostId}: {error.Message}";
                }
            }
            if (result.Forget.Count > 0 && changes.TryTake() is { } turn)
            {
                using (turn)
                {
                    foreach (var id in result.Forget)
                        if (FindHost(id) is { } host)
                        {
                            await ForgetPairingAsync(host, lifetime.Token);
                            hostsChanged = true;
                        }
                }
            }
            // Keep what changed here while this sync ran: hosts paired on purpose, hosts forgotten, roster changes.
            var latest = NetworkIdentity.Load(directory);
            var state = result.State with
            {
                Adopt = result.State.Adopt.Union(latest.Adopt.Except(before.Adopt, StringComparer.Ordinal), StringComparer.Ordinal).ToArray(),
                Ignored = result.State.Ignored.Except(sharedNames, StringComparer.Ordinal)
                    .Union(latest.Ignored.Except(before.Ignored, StringComparer.Ordinal), StringComparer.Ordinal).ToArray()
            };
            if (state.Roster is { } merged && latest.Roster is { } local && local.NetworkId == merged.NetworkId && local.Digest() != before.Roster?.Digest())
                state = state with { Roster = NetworkRoster.Accept(merged, local).Roster };
            NetworkIdentity.Save(directory, state);
            networkState = state;
            // Outside addresses travel with each pairing too, so a PC that has never been home reconnects whatever its network state.
            try
            {
                foreach (var id in Pairings().KeepRosterAddresses(state.Roster))
                    ErrorLog.Info($"Martlet network: kept {id}'s outside addresses from the network with its pairing.");
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
            {
                ErrorLog.Warn("Martlet network: couldn't keep the hosts' outside addresses with their pairings: " + error.Message);
            }
            if (result.RetireKey) NetworkIdentity.Retire(directory);
            networkJoins = result.Joins;
            networkNotes = result.Notes;
            networkViews = result.Views;
            networkCheckedAt = DateTimeOffset.Now;
            networkReach = state.Roster is { } current
                ? NetworkReach.Of(current, NetworkIdentity.DeviceId(homeHosts),
                    homeHosts.Select(h => h.HostId).Concat(result.Paired.Select(p => p.Pairing.HostId)).Except(result.Forget).ToArray(),
                    state.Ignored, result.Views, result.Notes)
                : NetworkReach.None;
            foreach (var line in result.Events) ErrorLog.Info("Martlet network: " + line);
            LogNetworkPicture(state);
            LogNetworkReach();
            // A PC that joined by signing in to a household account signs that account in here once it is a member.
            if (state.Roster is not null) SignInJoinedAccountAsync().Forget();
            ObserveHostReleases(result.Views);
            CheckReinstalledHostsAsync().Forget();
            await ReadSecurityAuditsAsync(state.Roster);
            var messages = result.Events.ToList();
            IReadOnlyList<HostJoinRequest> pending = result.Joins;
            // A computer that paired with this PC's own host service was approved here already (the pairing code or Allow shows
            // only on this PC): it joins without a second Allow.
            var own = ThisPcHost() is { } mine ? new[] { mine.HostId } : [];
            if (state.Roster is not null && pending.Any(j => own.Contains(j.HostId, StringComparer.Ordinal)))
            {
                var through = pending.Where(j => own.Contains(j.HostId, StringComparer.Ordinal)).ToArray();
                pending = pending.Except(through).ToArray();
                networkJoins = networkJoins.Where(j => through.All(t => t.DeviceId != j.DeviceId)).ToArray();
                foreach (var join in through) networkPreapproved.Remove(join.DeviceId);
                var names = string.Join(" and ", through.Select(j => j.DisplayName));
                ChangeNetwork((engine, current) => engine.ApproveThrough(current, through, own).State,
                    $"{names} joined your Martlet network through this PC's host service ({through[0].HostId}), which you approved here; " +
                    "it pairs with your other hosts automatically.");
                messages.Clear();
            }
            // A computer that paired by signing in (the owner account or an identity the owner allowed on that host) was
            // approved by that sign-in: it joins without a check number.
            if (state.Roster is not null && pending.Any(j => j.SignIn is not null))
            {
                var signedIn = pending.Where(j => j.SignIn is not null && state.Roster.Host(j.HostId) is { Removed: false }).ToArray();
                if (signedIn.Length > 0)
                {
                    pending = pending.Except(signedIn).ToArray();
                    networkJoins = networkJoins.Where(j => signedIn.All(s => s.DeviceId != j.DeviceId)).ToArray();
                    var who = string.Join(" and ", signedIn.Select(j => $"{j.DisplayName} (signed in as {j.SignIn!.Label ?? j.SignIn.Subject}" +
                        (j.SignIn.AccountId is { } account ? $", {HouseholdAccountName(directory, account)}'s computer" : "") + ")"));
                    ChangeNetwork((engine, current) => engine.ApproveSignedIn(current, signedIn).State,
                        $"{who} joined your Martlet network with a sign-in you allowed on {signedIn[0].HostId}; it pairs with your " +
                        "other hosts automatically.");
                    messages.Clear();
                }
            }
            foreach (var stale in networkPreapproved.Where(p => p.Value <= DateTimeOffset.Now).Select(p => p.Key).ToArray())
                networkPreapproved.Remove(stale);
            if (state.Roster is not null && pending.FirstOrDefault(j => networkPreapproved.ContainsKey(j.DeviceId)) is { } allowed)
            {
                networkPreapproved.Remove(allowed.DeviceId);
                pending = pending.Where(j => j.DeviceId != allowed.DeviceId).ToArray();
                networkJoins = networkJoins.Where(j => j.DeviceId != allowed.DeviceId).ToArray();
                ChangeNetwork((engine, current) => engine.Approve(current, allowed),
                    $"{allowed.DisplayName} joined your Martlet network (you allowed it with check number {allowed.CheckNumber}); it pairs with your other hosts automatically.");
                messages.Clear();
            }
            if (pending.FirstOrDefault(j => networkAnnounced.Add(j.DeviceId + "|" + j.Key)) is { } fresh)
            {
                ErrorLog.Info($"Martlet network: {fresh.DeviceId} ({fresh.DisplayName}) asks to join (check number {fresh.CheckNumber}).");
                messages.Add($"{fresh.DisplayName} wants to join your Martlet network (check number {fresh.CheckNumber}); allow it " +
                    (Role == DeviceRole.Host ? "on Home, or " : "") + "under Share, Your Martlet network.");
            }
            if (messages.Count > 0) ActionText.Text = string.Join(" ", messages);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or ContractException or
            CryptographicException or ArgumentException)
        {
            networkProblem = error.Message;
            ErrorLog.Warn("Martlet network sync failed: " + error.Message);
        }
        finally
        {
            networkBusy = false;
            if (!closing)
            {
                if (hostsChanged)
                {
                    await RefreshHomeAsync();
                    QueueClusterSync();
                    QueueApiKeySync();
                    // A host new to this PC gets the household's sign-in providers (docs/NETWORK.md).
                    QueueHouseholdSignIn();
                }
                RenderNetwork();
                // Whether this PC's own host service serves another computer, and so whether this PC stays awake, follows each sync.
                UpdateStayAwake();
                if (HostDashboardNetworkSignature() != shownBefore) RenderHost();
                if (DevicesPage.IsVisible && NetworkDevicesSignature() != networkDevicesShown) RenderMap();
                if (networkQueued)
                {
                    networkQueued = false;
                    QueueNetworkSync();
                }
            }
        }
    }

    private static DockPanel NetworkRowFrame() => new() { LastChildFill = true };
}
