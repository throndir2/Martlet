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

/// <summary>More of MainWindow.Network.cs, in a file of its own so that each file stays under 20 KB.</summary>
public partial class MainWindow
{
    private static HostConnection ConnectToHost(HostPairing pairing)
    {
        using var read = new WindowsCredentialStore().ReadAvatarHostSecret(pairing.HostId, pairing.CredentialId);
        if (read.Error != CredentialError.None || read.Secret is null)
            throw new InvalidOperationException("This PC's pairing secret is missing; pair again.");
        HostConnection? connection = null;
        read.Secret.Use(secret => connection = new HostConnection(pairing, secret));
        return connection!;
    }

    /// <summary>Keeps a pairing the network made: the secret in Windows Credential Manager and the host in hosts.json, reached
    /// only through its gateway (a host this PC already reached over SSH keeps that).</summary>
    private async Task KeepNetworkPairingAsync(HostPairing pairing, string secret)
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
            var next = change(new NetworkSyncEngine(key, Martlet.Core.Network.ThisComputer.Name), NetworkIdentity.Load(store.DataDirectory));
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
                $"Let {join.DisplayName} ({join.DeviceId}), which must show check number {join.CheckNumber}, into your Martlet network " +
                "to use all its hosts?",
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
        var text = count == 0 ? "No outside addresses" : count == 1 ? "1 outside address" : $"{count} outside addresses";
        return HostRoutes.For(member.Origin, member.Spki) switch
        {
            { Route: "home", MovedTo: not null } => text + "; reached at home, at the new address its computer got (Martlet found it by itself).",
            { Route: "home" } => text + "; reached at home.",
            { Route: "outside", Address: { } at } => text + $"; reached from outside home (outside address {Array.IndexOf(member.Addresses?.ToArray() ?? [], at) + 1}).",
            { Error: not null } when count > 0 => text + "; not reachable at home or outside right now (the log names each address tried).",
            { Error: not null } => text + "; not reachable at home right now.",
            _ => text + "."
        };
    }

    /// <summary>Asks for a host's outside addresses (overlay or port forward) and signs them into the roster.</summary>
    private void EditOutsideAddresses(NetworkMember member)
    {
        var dialog = new HostInputDialog("Outside addresses", $"Reach {member.Name} from outside home",
            $"Addresses that reach {member.Name}'s gateway (home address {member.Origin?[8..]}) when this computer isn't at home, such as " +
            "a Tailscale address or a forwarded port; separate up to four with commas, or leave it empty to remove them.", "Save");
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
            ? $"Remove {member.Name} ({member.Id}) from your Martlet network, so every host revokes it and it must ask again to come back?"
            : $"Remove {member.Id} from your Martlet network, so every computer forgets it and you must pair it again to bring it back?";
        if (!ConfirmationDialog.Confirm(this, question, "Remove from network")) return;
        ChangeNetwork((engine, state) => engine.Remove(state, member.Kind, member.Id),
            $"Removed {member.Name} from your Martlet network; your hosts hear about it within a minute.");
    }

    private void NetworkCheck_Click(object sender, RoutedEventArgs e)
    {
        NetworkStatusText.Text = "Checking your network...";
        networkFinder.LookSoon();
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
                users[hostId] = devices.OrderBy(d => !IsThisDevice(d.DeviceId)).ThenBy(d => d.Friend).ThenBy(d => d.DisplayName, StringComparer.OrdinalIgnoreCase)
                    .Select(d => IsThisDevice(d.DeviceId)
                        ? new HostUser("This PC, " + Seen(d.LastSeen, now), true)
                        : new HostUser((d.DisplayName == d.DeviceId ? d.DeviceId : $"{SharedComputerName(d.DeviceId, d.DisplayName)} ({d.DeviceId})") +
                            (d.Friend ? ", a friend's computer (engines only)" : "") + ", " + Seen(d.LastSeen, now), false))
                    .ToArray();
        return users;
    }

    private string? networkDevicesShown;

    /// <summary>The name another of your computers gives itself, as it shares it ("Imouto": its host name), else
    /// <paramref name="fallback"/>: the name it had when it joined the network or paired with a host (an older Martlet's "IMOUTO").</summary>
    private string SharedComputerName(string deviceId, string fallback) =>
        settingsNode?.Document.Find(SharedPc.Key(deviceId)) is { } entry && SharedPc.Read(entry.Value)?.ComputerName is { } name ? name : fallback;

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
            return new(id, pc?.ComputerName ?? name, standing, MemberActivity(id), seen is not null && Active(seen, now), check, through, pc?.DeviceRole, pc?.Host,
                asked.Role, asked.By, asked.At);
        }
        if (roster is not null)
            foreach (var member in roster.ActiveDesktops.Where(d => !IsThisDevice(d.Id)))
                computers.Add(Computer(member.Id, member.Name, ComputerStanding.Member));
        foreach (var join in networkJoins.Where(j => !IsThisDevice(j.DeviceId) && computers.All(c => c.DeviceId != j.DeviceId)))
            computers.Add(Computer(join.DeviceId, join.DisplayName, ComputerStanding.Asking, join.CheckNumber, join.HostId));
        foreach (var device in OutsideComputers(roster).Where(d => computers.All(c => c.DeviceId != d.DeviceId)))
            computers.Add(device.Friend
                ? Computer(device.DeviceId, device.DisplayName, ComputerStanding.Friend, through: string.Join(", ", FriendHosts(device.DeviceId)))
                : Computer(device.DeviceId, device.DisplayName, ComputerStanding.Outside));
        return computers;
    }
}
