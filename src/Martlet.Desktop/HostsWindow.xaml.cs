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

/// <summary>Add-a-computer wizard in two steps. Connect: pick a Martlet host found on the network (no code: both computers
/// show a check number and the owner allows it there), enter the address and code a host shows when it isn't found, or set
/// up a new host on this PC (Docker Desktop) or on a Linux computer over SSH (Docker when it has it, otherwise native
/// Ubuntu). Roles: choose the jobs the connected host runs.</summary>
public partial class HostsWindow : ThemedWindow
{
    private readonly HostPairings pairings;
    private readonly CancellationTokenSource lifetime = new();
    private readonly string version = typeof(App).Assembly.GetName().Version is { } v ? v.ToString(3) : "0.0.0";
    private PairedHost? paired;
    private IReadOnlyList<PairedHost> hostList = [];
    private bool showingHosts;
    /// <summary>The wizard's own actions still working (load, check, pair, ...): a second click on the same one is ignored
    /// while it works; different actions and run windows go side by side.</summary>
    private readonly HashSet<string> acting = new(StringComparer.Ordinal);
    private int step;

    /// <summary>A Martlet the welcome wizard already found and the owner chose to join: asked as soon as the window opens.</summary>
    private NearbyMartlet? connectFirst;

    internal HostsWindow(AvatarProfileStore profiles, ISetupService settings, int startStep = 0, PairedHost? manage = null,
        NearbyMartlet? connect = null)
    {
        connectFirst = connect;
        InitializeComponent();
        BuildRoleCards();
        pairings = new(Path.GetDirectoryName(profiles.FilePath)!, profiles, settings);
        paired = manage;
        DeviceIdText.Text = manage?.Pairing.DeviceId ?? HostSetupCommands.SuggestedDeviceId();
        if (manage?.SshTarget is { } ssh) SshTargetText.Text = ssh;
        DockerStateText.Text = "Runs the host in Docker Desktop, " + (MachineInfo.DockerDesktopRunning() ? "which is running."
            : MachineInfo.DockerDesktopInstalled() ? "which Martlet starts."
            : "which Martlet offers to install.");
        ShowStep(Math.Clamp(startStep, 0, 1), animate: false);
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (connectFirst is { } chosen)
        {
            // The welcome wizard's Join: the same check-number request as Connect here, for the computer the owner picked there.
            connectFirst = null;
            searched = true;
            nearbyFound = [chosen];
            ShowNearby();
            var known = KnownHostIds();
            var hosts = chosen.Hosts.Where(h => !known.Contains(h)).ToArray();
            if (hosts.Length > 0) ConnectNearbyAsync(chosen, hosts).Forget();
        }
        else if (step == 0) FindNearbyAsync().Forget();
        await ActionAsync("load", async () =>
        {
            var (all, profile) = await pairings.LoadAsync(lifetime.Token);
            var hosts = all.Where(h => !h.Shared).ToList();
            paired = paired is { } manage ? hosts.FirstOrDefault(h => h.HostId == manage.HostId) ?? manage
                : hosts.FirstOrDefault(h => h.HostId == profile?.RemoteHost?.HostId) ?? hosts.LastOrDefault();
            ShowHosts(hosts);
            StatusText.Text = paired is null ? "No Martlet host paired." : $"Showing {paired.HostId}.";
        });
    }

    private void Window_Closed(object? sender, EventArgs e) => lifetime.Cancel();

    // ---------- wizard navigation ----------

    private RadioButton[] Rail => [Rail0, Rail1];
    private StackPanel[] Steps => [ConnectStep, RolesStep];

    private void ShowStep(int value, bool animate = true)
    {
        step = value;
        var steps = Steps;
        for (var i = 0; i < steps.Length; i++) steps[i].Visibility = i == step ? Visibility.Visible : Visibility.Collapsed;
        if (Rail[step].IsChecked != true) Rail[step].IsChecked = true;
        BackButton.IsEnabled = step > 0;
        NextButton.Content = step == steps.Length - 1 ? "_Done" : "_Next";
        ShowPaired();
        if (step == 0 && IsLoaded && !searched) FindNearbyAsync().Forget();
        Scroller.ScrollToTop();
        if (animate) Motion.Enter(steps[step], dx: 28, dy: 0, milliseconds: 280);
    }

    /// <summary>Shows the address-and-code fields for a host Martlet didn't find on the network.</summary>
    private void EnterCode_Click(object sender, RoutedEventArgs e)
    {
        ManualPanel.Visibility = Visibility.Visible;
        Dispatcher.BeginInvoke(() => (PairAddressText.Text.Length == 0 ? PairAddressText : PairingCodeBox).Focus(),
            System.Windows.Threading.DispatcherPriority.Input);
    }

    private void Rail_Checked(object sender, RoutedEventArgs e)
    {
        if (ConnectStep is null) return;
        var index = Array.IndexOf(Rail, sender);
        if (index >= 0 && index != step) ShowStep(index);
    }

    private void Next_Click(object sender, RoutedEventArgs e)
    {
        if (step == Steps.Length - 1) Close();
        else ShowStep(step + 1);
    }

    private void Back_Click(object sender, RoutedEventArgs e) { if (step > 0) ShowStep(step - 1); }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    // ---------- the connected host ----------

    private void ShowHosts(IReadOnlyList<PairedHost> hosts)
    {
        // Hosts friends share with this PC are not yours to set up: Share › Hosts shared with this PC lists them.
        hosts = hosts.Where(h => !h.Shared).ToList();
        hostList = hosts;
        showingHosts = true;
        try
        {
            HostChoice.ItemsSource = hosts.Select(h => h.HostId).ToList();
            HostChoice.SelectedItem = paired?.HostId;
        }
        finally { showingHosts = false; }
        HostChoice.Visibility = hosts.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        ShowPaired();
    }

    private void HostChoice_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (showingHosts || HostChoice.SelectedItem is not string id || hostList.FirstOrDefault(h => h.HostId == id) is not { } host) return;
        paired = host;
        ShowPaired();
        StatusText.Text = $"Showing {host.HostId}.";
    }

    private void ShowPaired()
    {
        if (PairedText is null) return;
        if (paired is { } host) DeviceIdText.Text = host.Pairing.DeviceId;
        var others = hostList.Count > 1 ? $" ({hostList.Count} hosts are paired; pick one above)" : "";
        PairedText.Text = paired is { } shown
            ? $"{shown.HostId} at {shown.Pairing.Origin}, reached by {shown.Reach}; this PC is {shown.Pairing.DeviceId}{others}."
            : "No Martlet host paired yet: go back to Connect and pick or set up one.";
        RolesSummaryText.Text = paired switch
        {
            null => "Connect a computer first, then choose its jobs here.",
            { Method: HostSetupMethod.SshDocker or HostSetupMethod.SshNative } => "Add or remove jobs over SSH.",
            { Method: HostSetupMethod.ThisPcDocker } => "Add or remove jobs on this PC.",
            _ => "Add or remove jobs from here: Martlet on that computer runs them and shows progress here."
        };
        RoleCards.IsEnabled = paired?.CanLaunch == true;
        HostButtons.IsEnabled = paired is not null;
    }

    /// <summary>A host this wizard just connected: reload the list, show it and move on to its roles.</summary>
    private async Task ConnectedAsync(PairedHost host, CancellationToken token)
    {
        paired = host;
        ShowHosts((await pairings.LoadAsync(token)).Hosts);
        if (!lifetime.IsCancellationRequested) ShowStep(1);
    }

    /// <summary>Away from home: paste the owner's invite and sign in (<see cref="SignInJoinWindow"/>); the pairing is kept
    /// like any other. A friend's host (the sign-in gave friend access) is kept apart as a host shared with this PC: never
    /// adopted into this PC's network, never in this wizard's list, and only its engines are read.</summary>
    private void JoinWithInvite_Click(object sender, RoutedEventArgs e)
    {
        var device = DeviceIdText.Text.Trim();
        new SignInJoinWindow(device, pairings.DataDirectory, async (pairing, secret, outside, who) =>
        {
            if (who.Friend)
            {
                // A friend's host named like one of your own would replace that pairing: keep yours.
                if ((await pairings.LoadAsync(lifetime.Token)).Hosts.FirstOrDefault(h => h.HostId == pairing.HostId && !h.Shared) is not null)
                    throw new InvalidOperationException($"{pairing.HostId} is already one of your own hosts on this PC, so Martlet can't keep " +
                        "a friend's host with the same name here. Ask your friend for an invite to a host with another name.");
                await KeepPairingAsync(pairings, pairing, secret, HostSetupMethod.Agent, null, null, lifetime.Token, outside,
                    HostSignInAccess.Friend, who.ToString());
                StatusText.Text = $"{pairing.HostId} is shared with this PC by a friend; choose it for a job under Share › Hosts shared with this PC.";
                return;
            }
            var host = await SavePairingAsync(pairing, secret, HostSetupMethod.Agent, null, null, outsideAddresses: outside);
            try { await CheckAsync(host.Pairing, Hardware, _ => { }, lifetime.Token); }
            catch (Exception error) when (error is InvalidOperationException or HostConnectionException) { }
        }, acceptShareLink: async text =>
        {
            // A friend's share link: the main window signs in to every host it names; this wizard closes.
            if (Application.Current.MainWindow is MainWindow main && !ReferenceEquals(main, this))
            {
                Close();
                await main.AcceptShareLinkAsync(text);
            }
        }) { Owner = this }.ShowDialog();
    }

    /// <summary>The selected host's sign-in settings and invite (<see cref="SignInSettingsWindow"/>).</summary>
    private void SignInSettings_Click(object sender, RoutedEventArgs e)
    {
        if (paired is not { } host) { StatusText.Text = "No Martlet host paired."; return; }
        new SignInSettingsWindow(host.Pairing, pairings.DataDirectory) { Owner = this }.ShowDialog();
    }

    private async Task ActionAsync(string what, Func<Task> action)
    {
        if (!acting.Add(what)) { StatusText.Text = "That is still running."; return; }
        try { await action(); }
        catch (OperationCanceledException) { }
        catch (HostConnectionException error) { StatusText.Text = error.Message; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ContractException or
            InvalidOperationException or ArgumentException or JsonException or TimeoutException)
        { StatusText.Text = error.Message; }
        finally { acting.Remove(what); }
    }
}
