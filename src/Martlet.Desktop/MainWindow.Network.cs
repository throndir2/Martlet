using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using Martlet.Avatar.Audio2Face.Remote;
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
/// pairs with every host of the network by itself. The Devices page lists the network's computers, the requests to join
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
                networkCheckedAt = DateTimeOffset.Now;
                LogNetworkPicture(before);
                ObserveHostReleases(watched.Views);
                return;
            }
            NetworkSyncResult result;
            using (var key = NetworkIdentity.LoadOrCreate(directory, NetworkIdentity.DeviceId(hosts)))
            {
                var engine = new NetworkSyncEngine(key, Environment.MachineName);
                result = await engine.SyncAsync(before, pairings, ConnectToHost, lifetime.Token);
            }
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
                Ignored = result.State.Ignored.Union(latest.Ignored.Except(before.Ignored, StringComparer.Ordinal), StringComparer.Ordinal).ToArray()
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
            foreach (var line in result.Events) ErrorLog.Info("Martlet network: " + line);
            LogNetworkPicture(state);
            ObserveHostReleases(result.Views);
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
                    $"{names} joined your Martlet network by itself: it paired with this PC's host service ({through[0].HostId}), which you " +
                    "approved here, so it needs no second Allow. It pairs with your other hosts automatically.");
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
                    var who = string.Join(" and ", signedIn.Select(j => $"{j.DisplayName} (signed in as {j.SignIn!.Label ?? j.SignIn.Subject})"));
                    ChangeNetwork((engine, current) => engine.ApproveSignedIn(current, signedIn).State,
                        $"{who} joined your Martlet network by itself: it signed in to {signedIn[0].HostId} with a sign-in you allowed, so it " +
                        "needs no check number. It pairs with your other hosts automatically.");
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
                messages.Add($"{fresh.DisplayName} wants to join your Martlet network (check number {fresh.CheckNumber}). Allow it " +
                    (Role == DeviceRole.Host ? "on Home, or " : "") + "under Devices, Your Martlet network.");
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

    private static Audio2FaceHostConnection ConnectToHost(Audio2FaceHostPairing pairing)
    {
        using var read = new WindowsCredentialStore().ReadAvatarHostSecret(pairing.HostId, pairing.CredentialId);
        if (read.Error != CredentialError.None || read.Secret is null)
            throw new InvalidOperationException("This PC's pairing secret is missing; pair again.");
        Audio2FaceHostConnection? connection = null;
        read.Secret.Use(secret => connection = new Audio2FaceHostConnection(pairing, secret));
        return connection!;
    }

    /// <summary>Keeps a pairing the network made: the secret in Windows Credential Manager and the host in hosts.json, reached
    /// only through its gateway (a host this PC already reached over SSH keeps that).</summary>
    private async Task KeepNetworkPairingAsync(Audio2FaceHostPairing pairing, string secret)
    {
        var pairings = Pairings();
        // Fails before anything is stored when this PC's settings can't be read. A PC with no settings yet pairs fine.
        await pairings.CheckCanKeepAsync(lifetime.Token);
        var vault = new WindowsCredentialStore();
        using (var lease = new SecretLease(secret))
        {
            var stored = vault.WriteAvatarHostSecret(pairing.HostId, pairing.CredentialId, lease);
            if (stored != CredentialError.None) throw new InvalidOperationException(CredentialMessages.Describe(stored));
        }
        try
        {
            await pairings.AddAsync(new AvatarRemoteHost
            {
                Origin = pairing.Origin, HostId = pairing.HostId, SpkiFingerprint = pairing.SpkiFingerprint,
                DeviceId = pairing.DeviceId, CredentialId = pairing.CredentialId
            }, HostSetupMethod.OnHost, null, lifetime.Token, adopt: false);
        }
        catch
        {
            vault.DeleteAvatarHostSecret(pairing.HostId, pairing.CredentialId);
            throw;
        }
    }

    /// <summary>Notes that the owner forgot a network host here, so the network does not pair this PC with it again.</summary>
    private void IgnoreNetworkHost(string hostId)
    {
        if (store is null) return;
        try
        {
            networkState = NetworkIdentity.Load(store.DataDirectory).WithIgnored(hostId);
            NetworkIdentity.Save(store.DataDirectory, networkState);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ContractException)
        {
            ErrorLog.Warn($"Could not note that {hostId} was forgotten in {NetworkLocalState.FileName}: {error.Message}");
        }
    }

    /// <summary>Notes that the owner just allowed <paramref name="deviceId"/> (Add a computer › Martlet on your network): when it
    /// asks to join the network in the next minutes, this PC approves it without asking again.</summary>
    private void PreapproveNetworkJoin(string deviceId, string name)
    {
        networkPreapproved[deviceId] = DateTimeOffset.Now + NetworkPreapproval;
        ErrorLog.Info($"Martlet network: {deviceId} ({name}) was allowed from Add a computer; its request to join will be approved here.");
        QueueNetworkSync();
    }

    /// <summary>Signs a change to the roster with this PC's network key, saves it and shares it on the next sync.</summary>
    private void ChangeNetwork(Func<NetworkSyncEngine, NetworkLocalState, NetworkLocalState> change, string done)
    {
        if (store is null) return;
        try
        {
            using var key = NetworkIdentity.LoadOrCreate(store.DataDirectory, NetworkIdentity.DeviceId(homeHosts));
            var next = change(new NetworkSyncEngine(key, Environment.MachineName), NetworkIdentity.Load(store.DataDirectory));
            NetworkIdentity.Save(store.DataDirectory, next);
            networkState = next;
            ActionText.Text = done;
            ErrorLog.Info("Martlet network: " + done);
            RenderNetwork();
            // A request allowed (or turned into a member) leaves the host dashboard's Allow step at once, not on the next sync.
            RenderHost();
            QueueNetworkSync();
        }
        catch (Exception error) when (error is InvalidOperationException or IOException or UnauthorizedAccessException or ContractException or
            CryptographicException)
        {
            ActionText.Text = error.Message;
        }
    }

    private void AllowJoin(HostJoinRequest join)
    {
        if (networkState.Roster?.Trusts(join.DeviceId, join.Key) == true)
        {
            // Already let in (on another screen, or by itself through this PC's host service): nothing left to allow.
            networkJoins = networkJoins.Where(j => j.DeviceId != join.DeviceId).ToArray();
            ActionText.Text = $"{join.DisplayName} is already in your Martlet network.";
            RenderNetwork();
            RenderHost();
            return;
        }
        if (!ConfirmationDialog.Confirm(this,
                $"Let {join.DisplayName} ({join.DeviceId}) into your Martlet network? Only allow it if that computer shows check number " +
                $"{join.CheckNumber}. It can then use every host in your network and pairs with them by itself; you can remove it here later.",
                "Allow into your network"))
            return;
        networkJoins = networkJoins.Where(j => j.DeviceId != join.DeviceId).ToArray();
        ChangeNetwork((engine, state) => engine.Approve(state, join),
            $"{join.DisplayName} is in your Martlet network now; it pairs with your hosts within a minute.");
    }

    private async Task DenyJoinAsync(HostJoinRequest join)
    {
        networkJoins = networkJoins.Where(j => j.DeviceId != join.DeviceId).ToArray();
        RenderNetwork();
        var told = 0;
        foreach (var host in homeHosts.Where(h => networkState.Roster?.Host(h.HostId) is { Removed: false }))
        {
            try
            {
                using var connection = ConnectToHost(GatewayAvatarHostLink.Pairing(host.Pairing));
                if (await connection.DenyJoinAsync(join.DeviceId, lifetime.Token)) told++;
            }
            catch (OperationCanceledException) when (!lifetime.IsCancellationRequested) { }
            catch (Exception error) when (ClusterSync.IsHostFailure(error)) { }
        }
        ActionText.Text = told > 0 ? $"Turned down {join.DisplayName}'s request to join your Martlet network."
            : $"Hid {join.DisplayName}'s request here; your hosts didn't answer, so it may show again until it expires (an hour).";
        ErrorLog.Info($"Martlet network: turned down {join.DeviceId}'s request to join ({told} host(s) told).");
    }

    /// <summary>The host's outside addresses and how this PC last reached it (home or outside), for its row and MCP.</summary>
    internal static string HostRouteText(NetworkMember member)
    {
        // Counts only: the addresses themselves show in the Outside addresses dialog, not in the row (or MCP snapshots).
        var count = member.Addresses?.Count ?? 0;
        var text = count == 0 ? "No outside addresses." : count == 1 ? "1 outside address." : $"{count} outside addresses.";
        return HostRoutes.For(member.Origin) switch
        {
            { Route: "home" } => text + " Reached at home.",
            { Route: "outside", Address: { } at } => text + $" Reached from outside home (outside address {Array.IndexOf(member.Addresses?.ToArray() ?? [], at) + 1}).",
            { Error: not null } when count > 0 => text + " Not reachable at home or outside right now (the log names each address tried).",
            { Error: not null } => text + " Not reachable at home right now.",
            _ => text
        };
    }

    /// <summary>Asks for a host's outside addresses (overlay or port forward) and signs them into the roster.</summary>
    private void EditOutsideAddresses(NetworkMember member)
    {
        var dialog = new HostInputDialog("Outside addresses", $"Reach {member.Name} from outside home",
            $"Addresses that reach {member.Name}'s gateway (home address {member.Origin?[8..]}) when this computer isn't at home: an overlay " +
            "network address (Tailscale, ZeroTier, WireGuard; recommended) or your router's public name with a forwarded port. Martlet " +
            "tries the home address first and these only when it doesn't answer, always checking the same host key, so a proxy that " +
            "ends TLS (an HTTP tunnel) won't work. Separate up to four with commas; leave empty to remove them. With any set, the host " +
            "limits failed attempts and pairing codes from every address (see docs/NETWORK.md).", "Save");
        dialog.AddText("addresses", "Outside addresses", string.Join(", ", member.Addresses ?? []),
            "For example gpu-box.tailnet.ts.net:9443, 100.101.102.103:9443 or home.example.net:9443.", optional: true, maxLength: 600);
        if (dialog.Ask(this) is not { } values) return;
        var addresses = values["addresses"].Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        ChangeNetwork((engine, state) => engine.SetHostAddresses(state, member.Id, addresses),
            addresses.Length == 0 ? $"{member.Name} has no outside addresses now; your hosts hear about it within a minute."
                : $"{member.Name} can be reached from outside home at {string.Join(", ", addresses.Select(a => NetworkRoster.NormalizeAddress(a) ?? a))}.");
    }

    private void RemoveFromNetwork(NetworkMember member)
    {
        var question = member.IsDesktop
            ? $"Remove {member.Name} ({member.Id}) from your Martlet network? Every host revokes it and it forgets your hosts. To come back it has to ask again and be allowed."
            : $"Remove {member.Id} from your Martlet network? It stops trusting every computer in your network, and they forget it. Pair it again with Add a computer to bring it back.";
        if (!ConfirmationDialog.Confirm(this, question, "Remove from network")) return;
        ChangeNetwork((engine, state) => engine.Remove(state, member.Kind, member.Id),
            $"Removed {member.Name} from your Martlet network; your hosts hear about it within a minute.");
    }

    private void NetworkCheck_Click(object sender, RoutedEventArgs e)
    {
        NetworkStatusText.Text = "Checking your network...";
        QueueNetworkSync();
    }

    // ---------- who uses which host ----------

    /// <summary>The computers paired with a host as it said on the last network sync, or null when unknown (not read yet,
    /// unreachable, or a host older than that list).</summary>
    private IReadOnlyList<HostPairedDevice>? PairedWith(string hostId) => networkViews.GetValueOrDefault(hostId)?.Devices;

    /// <summary>Whether a device ID is this PC (its network identity or the ID one of its pairings uses).</summary>
    private bool IsThisDevice(string deviceId) =>
        deviceId == ClusterDevice || deviceId == NetworkIdentity.DeviceId(homeHosts) || homeHosts.Any(h => h.Pairing.DeviceId == deviceId);

    /// <summary>"active now" (a signed request in the last two minutes), when it was last active, or that the host hasn't
    /// heard from it since it started.</summary>
    internal static string Seen(DateTimeOffset? lastSeen, DateTimeOffset now) => lastSeen is not { } at
        ? "not seen since the host started"
        : now - at < TimeSpan.FromMinutes(2) ? "active now"
        : "last active " + at.ToLocalTime().ToString(at.ToLocalTime().Date == now.ToLocalTime().Date ? "t" : "g", System.Globalization.CultureInfo.CurrentCulture);

    private static bool Active(HostPairedDevice device, DateTimeOffset now) => device.LastSeen is { } at && now - at < TimeSpan.FromMinutes(2);

    /// <summary>The computers using each paired host, for the Devices map ("IMOUTO (desktop-imouto), active now").</summary>
    private IReadOnlyDictionary<string, IReadOnlyList<HostUser>> HostUsers()
    {
        var now = DateTimeOffset.UtcNow;
        var users = new Dictionary<string, IReadOnlyList<HostUser>>(StringComparer.Ordinal);
        foreach (var (hostId, view) in networkViews)
            if (view.Devices is { } devices)
                users[hostId] = devices.OrderBy(d => !IsThisDevice(d.DeviceId)).ThenBy(d => d.DisplayName, StringComparer.OrdinalIgnoreCase)
                    .Select(d => IsThisDevice(d.DeviceId)
                        ? new HostUser("This PC, " + Seen(d.LastSeen, now), true)
                        : new HostUser((d.DisplayName == d.DeviceId ? d.DeviceId : $"{d.DisplayName} ({d.DeviceId})") + ", " + Seen(d.LastSeen, now), false))
                    .ToArray();
        return users;
    }

    private string? networkDevicesShown;

    /// <summary>The other Martlet computers this PC knows of, for the Devices map, so every computer draws the same picture:
    /// the network's member desktops, computers asking to join it and computers that use one of its hosts outside it, each
    /// with where it was last active.</summary>
    private IReadOnlyList<MartletComputer> OtherComputers()
    {
        var roster = networkState.Roster;
        var now = DateTimeOffset.UtcNow;
        var computers = new List<MartletComputer>();
        MartletComputer Computer(string id, string name, ComputerStanding standing, string? check = null, string? through = null)
        {
            var seen = networkViews.Values.SelectMany(v => v.Devices ?? []).Where(d => d.DeviceId == id)
                .OrderByDescending(d => d.LastSeen ?? DateTimeOffset.MinValue).FirstOrDefault();
            // What that computer says it is (companion or host PC, and its host service), shared by it with the settings.
            var pc = settingsNode?.Document.Find(SharedPc.Key(id)) is { } entry ? SharedPc.Read(entry.Value) : null;
            // An ask from one of your computers that it become a companion or a host PC, while it hasn't switched yet.
            (DeviceRole? Role, string? By, DateTimeOffset? At) asked = default;
            if (settingsNode?.Document.Find(SharedPc.RoleKey(id)) is { } ask && ask.UpdatedBy != id &&
                SharedPc.ReadRole(ask.Value) is { } wanted && wanted != pc?.DeviceRole)
                asked = (wanted, IsThisDevice(ask.UpdatedBy) ? "this PC" : ComputerName(ask.UpdatedBy), ask.UpdatedAt);
            return new(id, name, standing, MemberActivity(id), seen is not null && Active(seen, now), check, through, pc?.DeviceRole, pc?.Host,
                asked.Role, asked.By, asked.At);
        }
        if (roster is not null)
            foreach (var member in roster.ActiveDesktops.Where(d => !IsThisDevice(d.Id)))
                computers.Add(Computer(member.Id, member.Name, ComputerStanding.Member));
        foreach (var join in networkJoins.Where(j => !IsThisDevice(j.DeviceId) && computers.All(c => c.DeviceId != j.DeviceId)))
            computers.Add(Computer(join.DeviceId, join.DisplayName, ComputerStanding.Asking, join.CheckNumber, join.HostId));
        foreach (var device in OutsideComputers(roster).Where(d => computers.All(c => c.DeviceId != d.DeviceId)))
            computers.Add(Computer(device.DeviceId, device.DisplayName, ComputerStanding.Outside));
        return computers;
    }

    /// <summary>Changes when a computer pairs with or leaves a host, joins or leaves the network, asks to join, or becomes
    /// active or idle: then the Devices map is drawn again (not on every sync, so the map doesn't animate every 20 seconds).</summary>
    private string NetworkDevicesSignature()
    {
        var now = DateTimeOffset.UtcNow;
        return string.Join(";", networkViews.OrderBy(v => v.Key, StringComparer.Ordinal).Select(v => v.Key + ":" +
            string.Join(",", (v.Value.Devices ?? []).Select(d => d.DeviceId + (Active(d, now) ? "+" : "-"))))) + "|" +
            string.Join(",", networkState.Roster?.ActiveDesktops.Select(d => d.Id + "=" +
                (settingsNode?.Document.Find(SharedPc.Key(d.Id))?.Value ?? "") + "/" +
                (settingsNode?.Document.Find(SharedPc.RoleKey(d.Id)) is { } role ? role.Value + role.UpdatedBy : "")) ?? []) + "|" +
            string.Join(",", networkJoins.Select(j => j.DeviceId));
    }

    /// <summary>What the host dashboard shows about the network: requests to join and the computers paired with this PC's
    /// host service.</summary>
    private string HostDashboardNetworkSignature() =>
        string.Join(",", networkJoins.Select(j => j.DeviceId + j.CheckNumber)) + "|" + NetworkDevicesSignature() + "|" +
        string.Join(",", networkState.Roster?.ActiveDesktops.Select(d => d.Id) ?? []);

    /// <summary>Writes the network as this PC sees it to the desktop log when it changes (membership, requests to join, the
    /// computers paired with each host and each host's note), so the Diagnostics page shows why computers do or don't see
    /// each other.</summary>
    private void LogNetworkPicture(NetworkLocalState state)
    {
        var me = NetworkIdentity.DeviceId(homeHosts);
        var parts = new List<string>();
        if (state.Roster is { } roster)
            parts.Add($"{(Role == DeviceRole.Host ? "this host PC" : "this PC")} ({me}) is in network {roster.NetworkId} with " +
                string.Join(", ", roster.ActiveDesktops.Select(d => d.Id == me ? $"{d.Name} (this PC)" : $"{d.Name} ({d.Id})")) +
                "; hosts " + (roster.ActiveHosts.Any() ? string.Join(", ", roster.ActiveHosts.Select(h => h.Id)) : "none"));
        else if (state.Waiting is { } waiting)
            parts.Add($"this PC ({me}) waits to join network {waiting.NetworkId} through {waiting.HostId} (check number {waiting.CheckNumber}); " +
                "a member computer must allow it");
        else parts.Add(networkWatchOnly
            ? $"this host PC ({me}) is in no network and only watches its hosts; the main PC's network takes them"
            : $"this PC ({me}) is in no network");
        if (networkJoins.Count > 0)
            parts.Add("waiting to be allowed: " + string.Join(", ", networkJoins.Select(j => $"{j.DisplayName} ({j.DeviceId}, check number {j.CheckNumber}, through {j.HostId})")));
        foreach (var (hostId, view) in networkViews.OrderBy(v => v.Key, StringComparer.Ordinal))
            parts.Add($"{hostId} ({view.State}{(view.Bound ? " in " + view.Roster!.NetworkId : "")}) is paired with " + (view.Devices is not { } devices
                ? "(not reported; it runs an older Martlet)"
                : devices.Count == 0 ? "nobody"
                : string.Join(", ", devices.Select(d => IsThisDevice(d.DeviceId) ? $"{d.DeviceId} (this PC)" : $"{d.DeviceId} ({d.DisplayName})"))));
        var picture = string.Join("; ", parts) + ".";
        if (picture != networkLogged)
        {
            networkLogged = picture;
            ErrorLog.Info("Martlet network: " + picture);
        }
        foreach (var hostId in networkNotesLogged.Keys.Where(id => !networkNotes.ContainsKey(id)).ToArray()) networkNotesLogged.Remove(hostId);
        foreach (var (hostId, note) in networkNotes)
            if (networkNotesLogged.GetValueOrDefault(hostId) != note)
            {
                networkNotesLogged[hostId] = note;
                ErrorLog.Info($"Martlet network: {hostId}: {note}");
            }
    }

    // ---------- presentation ----------

    private void RenderNetwork()
    {
        NetworkStatusText.Text = NetworkStatusLine();
        NetworkCheckButton.IsEnabled = store is not null;
        NetworkJoinsPanel.Children.Clear();
        foreach (var join in networkJoins) NetworkJoinsPanel.Children.Add(JoinRow(join));
        NetworkMembersPanel.Children.Clear();
        var roster = networkState.Roster;
        if (roster is not null)
        {
            var me = roster.Desktop(NetworkIdentity.DeviceId(homeHosts));
            foreach (var member in roster.Members.Where(m => !m.Removed).OrderBy(m => m.IsHost).ThenBy(m => m.Id != me?.Id).ThenBy(m => m.Id, StringComparer.Ordinal))
                NetworkMembersPanel.Children.Add(MemberRow(roster, member, member.IsDesktop && member.Id == me?.Id));
        }
        // Computers that use one of these hosts but aren't (yet) in the network still show, so every PC sees who is connected.
        foreach (var device in OutsideComputers(roster)) NetworkMembersPanel.Children.Add(PairedRow(device));
        RenderWorkSharing();
    }

    /// <summary>Each computer paired with one of this PC's hosts that is not a member of the network (or of any network, on a
    /// host PC that only watches), not this PC and not already asking to join: its most recently active pairing.</summary>
    private IEnumerable<HostPairedDevice> OutsideComputers(NetworkRoster? roster) => networkViews.Values
        .SelectMany(v => v.Devices ?? [])
        .Where(d => !IsThisDevice(d.DeviceId) && roster?.Desktop(d.DeviceId) is not { Removed: false } && networkJoins.All(j => j.DeviceId != d.DeviceId))
        .GroupBy(d => d.DeviceId, StringComparer.Ordinal)
        .Select(g => g.OrderByDescending(d => d.LastSeen ?? DateTimeOffset.MinValue).First())
        .OrderBy(d => d.DisplayName, StringComparer.OrdinalIgnoreCase);

    /// <summary>Where a member computer was last active, from its hosts' reports ("active now on diva-host"), or null.</summary>
    private string? MemberActivity(string deviceId)
    {
        var seen = networkViews.Values.SelectMany(v => v.Devices ?? []).Where(d => d.DeviceId == deviceId)
            .OrderByDescending(d => d.LastSeen ?? DateTimeOffset.MinValue).FirstOrDefault();
        if (seen is null) return null;
        var text = Seen(seen.LastSeen, DateTimeOffset.UtcNow);
        return $"{char.ToUpperInvariant(text[0])}{text[1..]} on {seen.HostId}.";
    }

    private string NetworkStatusLine()
    {
        if (store is null) return "Unavailable without a local data folder.";
        if (networkProblem is not null) return "Couldn't check your network this time: " + networkProblem;
        if (networkState.Roster is { } roster)
        {
            var others = roster.ActiveDesktops.Count() - 1;
            var hosts = roster.ActiveHosts.Count();
            var away = roster.ActiveHosts.Count(h => HostRoutes.For(h.Origin) is { Route: "outside" });
            return $"This PC is in your Martlet network with {Count(others, "other computer")} and {Count(hosts, "host")}. " +
                (away > 0 ? $"{Count(away, "host")} {(away == 1 ? "is" : "are")} reached from outside home right now. " : "") +
                "Hosts you pair on any of them are shared with all of them" +
                (networkCheckedAt is { } at ? $"; checked {at:t}." : ".");
        }
        if (networkState.Waiting is { } wait)
            return $"Waiting to join: on one of your other computers open Devices and allow {NetworkIdentity.DeviceId(homeHosts)} under Your Martlet network. " +
                $"Check that it shows {wait.CheckNumber}. (Asked through {wait.HostId}.)";
        if (homeHosts.Count == 0)
            return "Not in a network yet. Add a computer: the first host you pair starts your Martlet network, and your other computers can join it.";
        if (networkState.RemovedFrom is not null)
            return "This PC was removed from its Martlet network. Pair a host of that network again to ask to join, or pair a new host to start a network.";
        if (networkWatchOnly)
            return "This host PC is in no Martlet network of its own: the network of the main PC that pairs with its host service takes it. " +
                "Computers that use its hosts are listed below as its hosts report them" + (networkCheckedAt is { } seen ? $"; checked {seen:t}." : ".");
        return networkCheckedAt is null ? "Checking your hosts..."
            : "Not in a network: your hosts run an older Martlet or didn't answer. Update them (Update host) to share them with your other computers.";

        static string Count(int count, string what) => count == 1 ? $"1 {what}" : $"{count} {what}s";
    }

    private FrameworkElement JoinRow(HostJoinRequest join)
    {
        var row = NetworkRowFrame();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var allow = new Button { Content = "Allow", Margin = new Thickness(8, 0, 0, 0) };
        allow.SetResourceReference(StyleProperty, "PrimaryButton");
        AutomationProperties.SetAutomationId(allow, "NetworkAllow-" + join.DeviceId);
        AutomationProperties.SetName(allow, $"Allow {join.DisplayName} into your Martlet network");
        allow.Click += (_, _) => AllowJoin(join);
        var deny = new Button { Content = "Turn down", Margin = new Thickness(8, 0, 0, 0) };
        AutomationProperties.SetAutomationId(deny, "NetworkDeny-" + join.DeviceId);
        AutomationProperties.SetName(deny, $"Turn down {join.DisplayName}'s request to join");
        deny.Click += (_, _) => DenyJoinAsync(join).Forget();
        buttons.Children.Add(allow);
        buttons.Children.Add(deny);
        DockPanel.SetDock(buttons, Dock.Right);
        row.Children.Add(buttons);
        row.Children.Add(NetworkRowText("NetworkJoin-" + join.DeviceId, $"{join.DisplayName} asks to join",
            $"{join.DeviceId}, through {join.HostId}. Check number {join.CheckNumber}: allow it only if that computer shows the same number."));
        return NetworkRowCard(row, warning: true);
    }

    private FrameworkElement MemberRow(NetworkRoster roster, NetworkMember member, bool thisPc)
    {
        var row = NetworkRowFrame();
        if (!thisPc)
        {
            var remove = new Button { Content = "Remove from network", VerticalAlignment = VerticalAlignment.Center };
            AutomationProperties.SetAutomationId(remove, $"NetworkRemove-{member.Kind}-{member.Id}");
            AutomationProperties.SetName(remove, $"Remove {member.Name} from your Martlet network");
            remove.Click += (_, _) => RemoveFromNetwork(member);
            DockPanel.SetDock(remove, Dock.Right);
            row.Children.Add(remove);
        }
        if (member.IsHost)
        {
            var outside = new Button { Content = "Outside addresses", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
            AutomationProperties.SetAutomationId(outside, "NetworkOutside-" + member.Id);
            AutomationProperties.SetName(outside, $"Set how {member.Name} is reached from outside home");
            outside.Click += (_, _) => EditOutsideAddresses(member);
            DockPanel.SetDock(outside, Dock.Right);
            row.Children.Add(outside);
        }
        string detail;
        if (member.IsDesktop)
            detail = (thisPc ? "This PC" : "Computer") + (roster.Founder?.Id == member.Id ? ", started the network" : $", allowed on {member.UpdatedBy}") + "." +
                (!thisPc && MemberActivity(member.Id) is { } activity ? " " + activity : "");
        else
        {
            var paired = FindHost(member.Id) is not null;
            detail = "Host, " + (paired ? "paired with this PC" : networkState.Ignored.Contains(member.Id) ? "forgotten on this PC (it stays in the network)" : "not paired with this PC yet") +
                $"; added on {member.UpdatedBy}." + " " + HostRouteText(member) + HostSecurityText(member.Id) + (networkNotes.GetValueOrDefault(member.Id) is { } note ? " " + note : "");
        }
        row.Children.Add(NetworkRowText($"NetworkMember-{member.Kind}-{member.Id}",
            member.IsDesktop && member.Name != member.Id ? $"{member.Name} ({member.Id})" : member.Name, detail));
        return NetworkRowCard(row, warning: false);
    }

    /// <summary>A computer that uses one of this PC's hosts but is not in the network: it can use the hosts it paired with, but
    /// isn't paired with the network's other hosts by itself.</summary>
    private FrameworkElement PairedRow(HostPairedDevice device)
    {
        var row = NetworkRowFrame();
        var hosts = networkViews.Values.Where(v => v.Devices?.Any(d => d.DeviceId == device.DeviceId) == true).Select(v => v.HostId).Order(StringComparer.Ordinal);
        var detail = $"Uses {string.Join(", ", hosts)}; {Seen(device.LastSeen, DateTimeOffset.UtcNow)}. " + (networkState.Roster is null
            ? "It is paired with this PC's host service; this PC is in no Martlet network itself."
            : "Not in your Martlet network, so it isn't paired with your other hosts by itself. It asks to join when it runs Martlet 0.18 or newer; allow it here then.");
        row.Children.Add(NetworkRowText("NetworkPaired-" + device.DeviceId,
            device.DisplayName != device.DeviceId ? $"{device.DisplayName} ({device.DeviceId})" : device.DeviceId, detail));
        return NetworkRowCard(row, warning: false);
    }

    private static DockPanel NetworkRowFrame() => new() { LastChildFill = true };

    private static FrameworkElement NetworkRowText(string automationId, string title, string detail)
    {
        var text = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };
        var heading = new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
        // The heading carries the whole row for UI Automation (and MCP snapshots): who it is and what it means.
        AutomationProperties.SetAutomationId(heading, automationId);
        AutomationProperties.SetName(heading, $"{title}. {detail}");
        text.Children.Add(heading);
        var line = new TextBlock { Text = detail, TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 2, 0, 0) };
        line.SetResourceReference(StyleProperty, "Muted");
        text.Children.Add(line);
        return text;
    }

    private static Border NetworkRowCard(FrameworkElement row, bool warning)
    {
        var border = new Border { Child = row, CornerRadius = new CornerRadius(14), Padding = new Thickness(16, 12, 16, 12), Margin = new Thickness(0, 10, 0, 0) };
        border.SetResourceReference(Border.BackgroundProperty, "CanvasBrush");
        if (warning)
        {
            border.BorderThickness = new Thickness(1);
            border.SetResourceReference(Border.BorderBrushProperty, "WarningBrush");
        }
        return border;
    }
}
