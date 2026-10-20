using System.IO;
using System.Net.Sockets;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Martlet.HostClient;
using Martlet.Avatar.Hosting;
using Martlet.Core.Contracts;
using Martlet.Core.Installation;
using Martlet.Core.Settings;
using Martlet.Credentials.Windows;

namespace Martlet.Desktop;

// The Add a computer wizard's Martlet on your network: find the Martlets nearby and ask one to share its hosts.
public partial class HostsWindow
{
    // ---------- Martlet on your network ----------

    private IReadOnlyList<NearbyMartlet> nearbyFound = [];
    private CancellationTokenSource? nearbyRequest;
    private bool finding;
    private bool searched;

    private void NearbyFind_Click(object sender, RoutedEventArgs e) => FindNearbyAsync().Forget();

    private void NearbyCancel_Click(object sender, RoutedEventArgs e) => nearbyRequest?.Cancel();

    /// <summary>Asks the local network which Martlet desktops can share hosts (<see cref="Nearby.FindWindow"/>) and lists those
    /// with a host this PC isn't paired with yet, each as soon as it answers.</summary>
    private async Task FindNearbyAsync()
    {
        if (finding || nearbyRequest is not null) return;
        finding = true;
        searched = true;
        NearbyFindButton.IsEnabled = false;
        NearbyStatusText.Text = "Looking for Martlet on your network...";
        try
        {
            nearbyFound = await Nearby.FindAsync(lifetime.Token, sofar =>
            {
                nearbyFound = sofar;
                // A request already under way keeps its own status; the list shows once it ends.
                if (nearbyRequest is null) ShowNearby();
            });
        }
        catch (OperationCanceledException) { return; }
        catch (Exception error) when (error is SocketException or IOException)
        {
            nearbyFound = [];
            NearbyStatusText.Text = $"Couldn't look on your network ({error.Message}).";
            return;
        }
        finally
        {
            finding = false;
            NearbyFindButton.IsEnabled = nearbyRequest is null;
        }
        if (nearbyRequest is null) ShowNearby();
        ErrorLog.Info($"Nearby: found {nearbyFound.Count} Martlet desktop(s) that can share hosts" +
            (nearbyFound.Count == 0 ? "." : ": " + string.Join(", ", nearbyFound.Select(m => $"{m.Name} at {m.Where} ({string.Join(", ", m.Hosts)})")) + "."));
    }

    private IReadOnlySet<string> KnownHostIds()
    {
        try { return HostRegistry.Load(pairings.DataDirectory, null, HostSetupCommands.ThisPcAddress()).Select(h => h.HostId).ToHashSet(StringComparer.Ordinal); }
        catch (Exception error) when (error is InvalidDataException or IOException or UnauthorizedAccessException) { return new HashSet<string>(); }
    }

    private void ShowNearby()
    {
        var known = KnownHostIds();
        NearbyList.Children.Clear();
        var offers = nearbyFound.Select(m => (Martlet: m, New: m.Hosts.Where(h => !known.Contains(h)).ToArray())).ToArray();
        var listed = offers.Where(o => o.New.Length > 0).ToArray();
        for (var i = 0; i < listed.Length; i++)
        {
            var (martlet, hosts) = listed[i];
            var row = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
            var connect = new Button { Content = "Connect", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 0, 0), IsEnabled = nearbyRequest is null };
            connect.SetResourceReference(StyleProperty, "PrimaryButton");
            System.Windows.Automation.AutomationProperties.SetAutomationId(connect, $"NearbyConnect-{i}");
            System.Windows.Automation.AutomationProperties.SetName(connect, $"Connect to {martlet.Name}");
            connect.Click += (_, _) => ConnectNearbyAsync(martlet, hosts).Forget();
            DockPanel.SetDock(connect, Dock.Right);
            row.Children.Add(connect);
            var text = new TextBlock
            {
                Text = $"{martlet.Name} ({martlet.Where}): {string.Join(", ", hosts)} · Martlet {martlet.Version}",
                TextWrapping = TextWrapping.Wrap, FontSize = 15, VerticalAlignment = VerticalAlignment.Center
            };
            System.Windows.Automation.AutomationProperties.SetAutomationId(text, $"NearbyItem-{i}");
            row.Children.Add(text);
            NearbyList.Children.Add(row);
        }
        var connected = offers.Length - listed.Length;
        var already = connected == 0 ? "" : connected == 1 ? " (1 more is already connected to this PC)" : $" ({connected} more are already connected to this PC)";
        NearbyStatusText.Text = (listed.Length > 0
            ? $"Found {listed.Length} {(listed.Length == 1 ? "computer" : "computers")} with a host this PC doesn't use yet{already}"
            : offers.Length > 0
                ? "This PC already uses every host Martlet found on your network"
                : "No other Martlet answered: open Martlet on the computer with the host, turn on Let my other computers find this PC " +
                  "there, then choose Find again, or enter its address and code") +
            (finding ? "; still looking for more..." : ".");
        if (listed.Length == 0 && offers.Length == 0) ManualPanel.Visibility = Visibility.Visible;
    }

    /// <summary>Asks <paramref name="martlet"/> to share <paramref name="hosts"/>: both computers show the same check number
    /// and its owner allows the request there; this PC then pairs with each host using the one-use code it sends.</summary>
    private async Task ConnectNearbyAsync(NearbyMartlet martlet, IReadOnlyList<string> hosts)
    {
        if (nearbyRequest is not null) { NearbyStatusText.Text = "Martlet is still asking another computer; stop that first, then connect."; return; }
        using var request = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        nearbyRequest = request;
        SetNearbyEnabled(false);
        var device = DeviceIdText.Text.Trim();
        var name = Martlet.Core.Network.ThisComputer.Name;
        try
        {
            await pairings.CheckCanKeepAsync(request.Token);
            NearbyStatusText.Text = $"Asking Martlet on {martlet.Name}...";
            using var join = await NearbyJoin.ConnectAsync(martlet, device, name, request.Token);
            NearbyNumberText.Text = join.Number;
            NearbyCheckPanel.Visibility = Visibility.Visible;
            NearbyStatusText.Text = $"Martlet on {martlet.Name} now asks whether to let {Nearby.Label(name)} use {string.Join(", ", hosts)}: " +
                $"press Allow there if it shows check number {join.Number}.";
            ErrorLog.Info($"Nearby: asked {martlet.Name} at {martlet.Where} to share its hosts.");
            await join.WaitApprovalAsync(request.Token);
            NearbyStatusText.Text = $"{martlet.Name} allowed this PC, which now gets a one-use pairing code from each host (this can take a minute)...";
            var (codes, problems) = await join.WaitCodesAsync(request.Token);
            NearbyCheckPanel.Visibility = Visibility.Collapsed;
            var done = new List<string>();
            var notes = problems.ToList();
            foreach (var code in codes)
            {
                NearbyStatusText.Text = $"Pairing with {code.HostId}...";
                try
                {
                    var (pairing, secret) = await HostTransport.PairWithCodeAsync(HostPairingInput.Origin(code.Address), code.Code, device, name,
                        request.Token);
                    var host = await SavePairingAsync(pairing, secret, HostSetupMethod.Agent, null, null);
                    done.Add(host.HostId);
                    try { await CheckAsync(host.Pairing, Hardware, _ => { }, request.Token); }
                    catch (Exception error) when (error is InvalidOperationException or HostConnectionException) { }
                }
                catch (OperationCanceledException) when (request.IsCancellationRequested) { throw; }
                catch (Exception error) when (error is HostConnectionException or InvalidOperationException or IOException or
                    UnauthorizedAccessException or ContractException or ArgumentException or JsonException or TimeoutException or
                    System.Net.Http.HttpRequestException or OperationCanceledException)
                {
                    notes.Add($"{code.HostId}: {error.Message}");
                }
            }
            try { await join.DoneAsync(done, request.Token); }
            catch (Exception error) when (error is IOException or SocketException or ObjectDisposedException) { }
            var extra = notes.Count == 0 ? "." : " (" + string.Join("; ", notes.Select(note => note.TrimEnd('.'))) + ").";
            NearbyStatusText.Text = done.Count == 0
                ? $"This PC isn't paired with {martlet.Name}'s hosts{extra}"
                : $"Paired with {string.Join(", ", done)} through {martlet.Name}{extra}";
            StatusText.Text = NearbyStatusText.Text;
            ErrorLog.Info($"Nearby: {NearbyStatusText.Text}");
            if (done.Count > 0 && !lifetime.IsCancellationRequested) ShowStep(1);
        }
        catch (OperationCanceledException) when (!lifetime.IsCancellationRequested)
        {
            NearbyStatusText.Text = $"Stopped asking {martlet.Name}.";
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is InvalidOperationException or IOException or SocketException or InvalidDataException or
            JsonException or System.Security.Cryptography.CryptographicException or TimeoutException or HostConnectionException)
        {
            NearbyStatusText.Text = error.Message;
            StatusText.Text = error.Message;
            ErrorLog.Info($"Nearby: asking {martlet.Name} stopped: {error.Message}");
        }
        finally
        {
            nearbyRequest = null;
            if (!lifetime.IsCancellationRequested)
            {
                NearbyCheckPanel.Visibility = Visibility.Collapsed;
                SetNearbyEnabled(true);
                var status = NearbyStatusText.Text;
                ShowNearby();
                NearbyStatusText.Text = status;
            }
        }
    }

    private void SetNearbyEnabled(bool enabled)
    {
        NearbyFindButton.IsEnabled = enabled && !finding;
        foreach (var row in NearbyList.Children.OfType<DockPanel>())
            foreach (var button in row.Children.OfType<Button>()) button.IsEnabled = enabled;
    }
}
