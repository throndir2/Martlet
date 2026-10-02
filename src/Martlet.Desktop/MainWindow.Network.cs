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
        if (networkBusy || closing || store is null || setupService is null || Role != DeviceRole.Companion) return;
        networkBusy = true;
        var hostsChanged = false;
        try
        {
            var directory = store.DataDirectory;
            var hosts = homeHosts;
            var before = NetworkIdentity.Load(directory);
            // Nothing to sync (and no key to make) before the first host is paired.
            if (hosts.Count == 0 && before.Roster is null && before.Waiting is null)
            {
                networkCheckedAt = DateTimeOffset.Now;
                return;
            }
            NetworkSyncResult result;
            using (var key = NetworkIdentity.LoadOrCreate(directory, NetworkIdentity.DeviceId(hosts)))
            {
                var engine = new NetworkSyncEngine(key, Environment.MachineName);
                result = await engine.SyncAsync(before, hosts.Select(h => GatewayAvatarHostLink.Pairing(h.Pairing)).ToArray(),
                    ConnectToHost, lifetime.Token);
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
            if (result.Forget.Count > 0 && !assigningRole)
            {
                assigningRole = true;
                try
                {
                    foreach (var id in result.Forget)
                        if (FindHost(id) is { } host)
                        {
                            await ForgetPairingAsync(host, lifetime.Token);
                            hostsChanged = true;
                        }
                }
                finally { assigningRole = false; }
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
            if (result.RetireKey) NetworkIdentity.Retire(directory);
            networkJoins = result.Joins;
            networkNotes = result.Notes;
            networkCheckedAt = DateTimeOffset.Now;
            foreach (var line in result.Events) ErrorLog.Info("Martlet network: " + line);
            var messages = result.Events.ToList();
            foreach (var stale in networkPreapproved.Where(p => p.Value <= DateTimeOffset.Now).Select(p => p.Key).ToArray())
                networkPreapproved.Remove(stale);
            if (state.Roster is not null && result.Joins.FirstOrDefault(j => networkPreapproved.ContainsKey(j.DeviceId)) is { } allowed)
            {
                networkPreapproved.Remove(allowed.DeviceId);
                networkJoins = networkJoins.Where(j => j.DeviceId != allowed.DeviceId).ToArray();
                ChangeNetwork((engine, current) => engine.Approve(current, allowed),
                    $"{allowed.DisplayName} joined your Martlet network (you allowed it with check number {allowed.CheckNumber}); it pairs with your other hosts automatically.");
                messages.Clear();
            }
            if (result.Joins.FirstOrDefault(j => networkAnnounced.Add(j.DeviceId + "|" + j.Key)) is { } fresh)
            {
                ErrorLog.Info($"Martlet network: {fresh.DeviceId} ({fresh.DisplayName}) asks to join (check number {fresh.CheckNumber}).");
                messages.Add($"{fresh.DisplayName} wants to join your Martlet network (check number {fresh.CheckNumber}). Allow it under Devices, Your Martlet network.");
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
        // Fails before anything is stored when Setup was never completed (hosts.json needs its profile).
        await pairings.LoadProfileAsync(lifetime.Token);
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

    // ---------- presentation ----------

    private void RenderNetwork()
    {
        NetworkStatusText.Text = NetworkStatusLine();
        NetworkCheckButton.IsEnabled = store is not null;
        NetworkJoinsPanel.Children.Clear();
        foreach (var join in networkJoins) NetworkJoinsPanel.Children.Add(JoinRow(join));
        NetworkMembersPanel.Children.Clear();
        if (networkState.Roster is not { } roster) return;
        var me = roster.Desktop(NetworkIdentity.DeviceId(homeHosts));
        foreach (var member in roster.Members.Where(m => !m.Removed).OrderBy(m => m.IsHost).ThenBy(m => m.Id != me?.Id).ThenBy(m => m.Id, StringComparer.Ordinal))
            NetworkMembersPanel.Children.Add(MemberRow(roster, member, member.IsDesktop && member.Id == me?.Id));
    }

    private string NetworkStatusLine()
    {
        if (store is null) return "Unavailable without a local data folder.";
        if (networkProblem is not null) return "Couldn't check your network this time: " + networkProblem;
        if (networkState.Roster is { } roster)
        {
            var others = roster.ActiveDesktops.Count() - 1;
            var hosts = roster.ActiveHosts.Count();
            return $"This PC is in your Martlet network with {Count(others, "other computer")} and {Count(hosts, "host")}. " +
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
        string detail;
        if (member.IsDesktop)
            detail = (thisPc ? "This PC" : "Computer") + (roster.Founder?.Id == member.Id ? ", started the network" : $", allowed on {member.UpdatedBy}") + ".";
        else
        {
            var paired = FindHost(member.Id) is not null;
            detail = "Host, " + (paired ? "paired with this PC" : networkState.Ignored.Contains(member.Id) ? "forgotten on this PC (it stays in the network)" : "not paired with this PC yet") +
                $"; added on {member.UpdatedBy}." + (networkNotes.GetValueOrDefault(member.Id) is { } note ? " " + note : "");
        }
        row.Children.Add(NetworkRowText($"NetworkMember-{member.Kind}-{member.Id}",
            member.IsDesktop && member.Name != member.Id ? $"{member.Name} ({member.Id})" : member.Name, detail));
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
