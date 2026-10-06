using System.IO;
using System.Net.Sockets;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Martlet.Avatar.Audio2Face.Remote;
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

    internal HostsWindow(AvatarProfileStore profiles, ISetupService settings, int startStep = 0, PairedHost? manage = null)
    {
        InitializeComponent();
        BuildRoleCards();
        pairings = new(Path.GetDirectoryName(profiles.FilePath)!, profiles, settings);
        paired = manage;
        DeviceIdText.Text = manage?.Pairing.DeviceId ?? HostSetupCommands.SuggestedDeviceId();
        if (manage?.SshTarget is { } ssh) SshTargetText.Text = ssh;
        DockerStateText.Text = "Runs the host in Docker Desktop. " + (MachineInfo.DockerDesktopRunning() ? "Docker Desktop is running."
            : MachineInfo.DockerDesktopInstalled() ? "Docker Desktop is installed; Martlet starts it."
            : "Docker Desktop is not installed; Martlet offers to install it.");
        ShowStep(Math.Clamp(startStep, 0, 1), animate: false);
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (step == 0) FindNearbyAsync().Forget();
        await ActionAsync("load", async () =>
        {
            var (hosts, profile) = await pairings.LoadAsync(lifetime.Token);
            paired = paired is { } manage ? hosts.FirstOrDefault(h => h.HostId == manage.HostId) ?? manage
                : hosts.FirstOrDefault(h => h.HostId == profile?.RemoteHost?.HostId) ?? hosts.LastOrDefault();
            ShowHosts(hosts);
            StatusText.Text = paired is null ? "No Martlet host paired." : $"Showing {paired.HostId}.";
        });
    }

    private void Window_Closed(object? sender, EventArgs e) => lifetime.Cancel();

    // ---------- Martlet on your network ----------

    private IReadOnlyList<NearbyMartlet> nearbyFound = [];
    private CancellationTokenSource? nearbyRequest;
    private bool finding;
    private bool searched;

    private void NearbyFind_Click(object sender, RoutedEventArgs e) => FindNearbyAsync().Forget();

    private void NearbyCancel_Click(object sender, RoutedEventArgs e) => nearbyRequest?.Cancel();

    /// <summary>Asks the local network which Martlet desktops can share hosts (about two seconds) and lists those with a host
    /// this PC isn't paired with yet.</summary>
    private async Task FindNearbyAsync()
    {
        if (finding || nearbyRequest is not null) return;
        finding = true;
        searched = true;
        NearbyFindButton.IsEnabled = false;
        NearbyStatusText.Text = "Looking for Martlet on your network...";
        try { nearbyFound = await Nearby.FindAsync(lifetime.Token); }
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
        ShowNearby();
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
        var already = connected == 0 ? "" : connected == 1 ? " 1 more is already connected to this PC." : $" {connected} more are already connected to this PC.";
        NearbyStatusText.Text = listed.Length > 0
            ? $"Found {listed.Length} {(listed.Length == 1 ? "computer" : "computers")} with a host this PC doesn't use yet.{already}"
            : offers.Length > 0
                ? $"This PC already uses every host Martlet found on your network.{already}"
                : "No other Martlet answered. Open Martlet on the computer with the host (it must run a host, with Let my other " +
                  "computers find this PC on), then Find again, or enter its address and code.";
        if (listed.Length == 0 && offers.Length == 0) ManualPanel.Visibility = Visibility.Visible;
    }

    /// <summary>Asks <paramref name="martlet"/> to share <paramref name="hosts"/>: both computers show the same check number
    /// and its owner allows the request there; this PC then pairs with each host using the one-use code it sends.</summary>
    private async Task ConnectNearbyAsync(NearbyMartlet martlet, IReadOnlyList<string> hosts)
    {
        if (nearbyRequest is not null) { NearbyStatusText.Text = "Martlet is still asking another computer. Stop that first, then connect."; return; }
        using var request = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        nearbyRequest = request;
        SetNearbyEnabled(false);
        var device = DeviceIdText.Text.Trim();
        var name = Environment.MachineName;
        try
        {
            await pairings.CheckCanKeepAsync(request.Token);
            NearbyStatusText.Text = $"Asking Martlet on {martlet.Name}...";
            using var join = await NearbyJoin.ConnectAsync(martlet, device, name, request.Token);
            NearbyNumberText.Text = join.Number;
            NearbyCheckPanel.Visibility = Visibility.Visible;
            NearbyStatusText.Text = $"Martlet on {martlet.Name} now asks whether to let {Nearby.Label(name)} use {string.Join(", ", hosts)}. " +
                $"Press Allow there if it shows check number {join.Number}.";
            ErrorLog.Info($"Nearby: asked {martlet.Name} at {martlet.Where} to share its hosts.");
            await join.WaitApprovalAsync(request.Token);
            NearbyStatusText.Text = $"{martlet.Name} allowed this PC. It is getting a one-use pairing code from each host (this can take a minute)...";
            var (codes, problems) = await join.WaitCodesAsync(request.Token);
            NearbyCheckPanel.Visibility = Visibility.Collapsed;
            var done = new List<string>();
            var notes = problems.ToList();
            foreach (var code in codes)
            {
                NearbyStatusText.Text = $"Pairing with {code.HostId}...";
                try
                {
                    var (pairing, secret) = await Audio2FaceHostClient.PairWithCodeAsync(HostPairingInput.Origin(code.Address), code.Code, device, name,
                        request.Token);
                    var host = await SavePairingAsync(pairing, secret, HostSetupMethod.Agent, null, null);
                    done.Add(host.HostId);
                    try { await CheckAsync(host.Pairing, Hardware, _ => { }, request.Token); }
                    catch (Exception error) when (error is InvalidOperationException or Audio2FaceHostException) { }
                }
                catch (OperationCanceledException) when (request.IsCancellationRequested) { throw; }
                catch (Exception error) when (error is Audio2FaceHostException or InvalidOperationException or IOException or
                    UnauthorizedAccessException or ContractException or ArgumentException or JsonException or TimeoutException or
                    System.Net.Http.HttpRequestException or OperationCanceledException)
                {
                    notes.Add($"{code.HostId}: {error.Message}");
                }
            }
            try { await join.DoneAsync(done, request.Token); }
            catch (Exception error) when (error is IOException or SocketException or ObjectDisposedException) { }
            var extra = notes.Count == 0 ? "" : " " + string.Join(" ", notes);
            NearbyStatusText.Text = done.Count == 0
                ? $"This PC isn't paired with {martlet.Name}'s hosts.{extra}"
                : $"Paired with {string.Join(", ", done)} through {martlet.Name}.{extra}";
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
            JsonException or System.Security.Cryptography.CryptographicException or TimeoutException or Audio2FaceHostException)
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
        var others = hostList.Count > 1 ? $" {hostList.Count} hosts are paired; pick one above." : "";
        PairedText.Text = paired is { } shown
            ? $"{shown.HostId} at {shown.Pairing.Origin}, reached by {shown.Reach}. This PC is {shown.Pairing.DeviceId}.{others}"
            : "No Martlet host paired yet. Go back to Connect and pick or set up one.";
        RolesSummaryText.Text = paired switch
        {
            null => "Connect a computer first, then choose its jobs here.",
            { Method: HostSetupMethod.SshDocker or HostSetupMethod.SshNative } =>
                "Add or remove jobs over SSH. Martlet asks for anything each job needs and shows progress.",
            { Method: HostSetupMethod.ThisPcDocker } =>
                "Add or remove jobs on this PC. Martlet asks for anything each job needs and shows progress.",
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

    // ---------- set up a new host ----------

    private async void SshTarget_LostFocus(object sender, RoutedEventArgs e)
    {
        if (AddressText.Text.Length > 0 || SshTargetText.Text.Trim().Length == 0) return;
        try { AddressText.Text = await HostSetupCommands.ResolveAsync(SshTargetText.Text.Trim(), lifetime.Token) ?? ""; }
        catch (OperationCanceledException) { }
    }

    private void SshTarget_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter) return;
        e.Handled = true;
        Setup_Click(sender, e);
    }

    /// <summary>The saved host for <paramref name="ssh"/>, if this PC already set one up there.</summary>
    private PairedHost? SshHost(string ssh) =>
        hostList.FirstOrDefault(h => h.SshTarget == ssh && h.Method is HostSetupMethod.SshDocker or HostSetupMethod.SshNative);

    /// <summary>Roles, update, check and status for the connected host: over SSH, on this PC, or through Martlet on that
    /// computer. Nothing opens a console window.</summary>
    private void Run(HostAction action)
    {
        if (paired is not { } host) { StatusText.Text = "Connect a computer first."; return; }
        RunOnHostAsync(host, action).Forget();
    }

    private async Task RunOnHostAsync(PairedHost host, HostAction action)
    {
        var local = host.Method == HostSetupMethod.ThisPcDocker;
        var ssh = host.Method is HostSetupMethod.SshDocker or HostSetupMethod.SshNative;
        var target = local
            ? new HostSetupTarget(HostSetupMethod.ThisPcDocker, "", HostSetupCommands.ThisPcAddress() ?? host.Address,
                HostSetupCommands.SuggestedHostId(Environment.MachineName), version)
            : ssh ? host.Target(version) with { HostId = null }
            : host.Target(version) with { Method = HostSetupMethod.Agent };
        using var updating = action.Verb == HostVerb.Update ? MainWindow.BeginHostUpdateElsewhere(local ? "" : host.HostId, onThisPc: local) : null;
        try
        {
            var done = await HostActions.RunAsync(this, pairings.DataDirectory, target, ssh ? host.SshHostKey : null, action,
                pairing: ssh || local ? null : host.Pairing);
            if (done is not null && action.Verb == HostVerb.Update) MainWindow.HostUpdatedElsewhere(local ? "" : host.HostId, onThisPc: local);
            StatusText.Text = done ?? "Stopped. The run window shows why.";
        }
        catch (InvalidOperationException error) { StatusText.Text = error.Message; }
    }

    /// <summary>Add a Linux computer: connect (password once, host key pinned), choose Docker or native from what it
    /// has (keeping how an existing host there runs), run setup unattended, pair automatically and read its machine report.</summary>
    private async Task<string> AddLinuxAsync(HostRunWindow run, HostShellTarget ssh, string? pinnedHostKey, HostSetupMethod? existing)
    {
        var remote = new HostRemote(new HostShell(pairings.DataDirectory, run.Prompts));
        run.Status($"Connecting to {ssh}...");
        var (probe, hostKey) = await remote.ProbeAsync(ssh, pinnedHostKey, run.Token);
        var method = existing ?? HostRemote.Choose(probe);
        run.Output.Report($"{ssh}: connected ({probe.OperatingSystem ?? "unknown system"}). Docker " +
            (probe.Docker ? probe.DockerAccess ? "is ready" : "needs sudo for this account" : "is not installed") +
            $", so Martlet sets it up {(method == HostSetupMethod.SshDocker ? "in Docker" : "natively")}.");
        if (HostRemote.Blocker(method, probe, ssh.ToString()) is { } blocker) throw new InvalidOperationException(blocker);
        var address = AddressText.Text.Trim();
        if (!HostSetupCommands.IsPrivate(address))
            address = probe.Address ?? await HostSetupCommands.ResolveAsync(ssh.ToString(), run.Token) ?? "";
        if (!HostSetupCommands.IsPrivate(address))
            throw new InvalidOperationException($"Martlet couldn't find {ssh}'s private network address. Enter it under Its private network address.");
        AddressText.Text = address;
        var target = new HostSetupTarget(method, ssh.ToString(), address, null, version);
        var sudo = HostRemote.NeedsSudo(method, probe);
        run.Status($"Checking whether {ssh} reaches the internet...");
        var supplied = await remote.SupplyIfOfflineAsync(target, HostVerb.Setup, pairings.DataDirectory, hostKey, run.Output, run.Token);
        run.Status($"Setting up the host on {ssh}. This can take a few minutes...");
        var result = await remote.RunAsync(target, "setup", true, sudo, null, hostKey, run.Output, run.Token, supplied: supplied);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"Setup stopped on {ssh} (exit {result.ExitCode}). Check the output for details.");
        run.Status($"Pairing this PC with {ssh}...");
        var device = DeviceIdText.Text.Trim();
        var (pairing, secret, key) = await remote.PairAsync(target, device, Environment.MachineName, sudo, hostKey, run.Output, run.Token);
        var host = await SavePairingAsync(pairing, secret, method, ssh.ToString(), key, run.Token);
        run.Status($"Checking {host.HostId}'s hardware...");
        string check;
        try { check = await CheckAsync(host.Pairing, Hardware, run.Status, run.Token); }
        catch (InvalidOperationException error) { check = "The host did not answer yet: " + error.Message; }
        return $"{host.HostId} is set up and paired. {check} Your other computers pair with it automatically.";
    }

    private async void ResetSshTrust_Click(object sender, RoutedEventArgs e) => await ActionAsync("trust", async () =>
    {
        var ssh = HostShellTarget.Parse(SshTargetText.Text);
        if (!ConfirmationDialog.Confirm(this, $"Reset saved SSH trust for {ssh.Host} and forget any remembered sudo password? " +
                "Use this only after reinstalling that computer. The next connection will ask you to trust it again.", "Reset trust"))
            return;
        await new HostShell(pairings.DataDirectory, new HostShellDialogs(this)).ForgetHostKeyAsync(ssh, lifetime.Token);
        await pairings.ClearSshHostKeyAsync(ssh, lifetime.Token);
        HostShell.ForgetSudo(ssh);
        hostList = hostList.Select(h => h.SshTarget == ssh.ToString() ? h with { SshHostKey = null } : h).ToList();
        if (paired?.SshTarget is { } current && current == ssh.ToString()) paired = paired with { SshHostKey = null };
        StatusText.Text = $"Martlet will ask you to trust {ssh.Host} again next time.";
    });

    /// <summary>Sets up a host on the Linux computer named in SSH target and connects to it.</summary>
    private async void Setup_Click(object sender, RoutedEventArgs e)
    {
        HostShellTarget ssh;
        try { ssh = HostShellTarget.Parse(SshTargetText.Text); }
        catch (InvalidOperationException error) { StatusText.Text = error.Message; return; }
        var existing = SshHost(ssh.ToString());
        try
        {
            var summary = await HostRunWindow.RunAsync(this, $"Add {ssh} to Martlet",
                run => AddLinuxAsync(run, ssh, existing?.SshHostKey, existing?.Method), join: true);
            StatusText.Text = summary ?? "Stopped. Check the run window for details.";
            if (summary is not null && paired is { } host && !lifetime.IsCancellationRequested) await ConnectedAsync(host, lifetime.Token);
        }
        catch (InvalidOperationException error) { StatusText.Text = error.Message; }
        catch (OperationCanceledException) { }
    }

    private async void SetupThisPc_Click(object sender, RoutedEventArgs e) => await ActionAsync("setup", async () =>
    {
        var (host, status) = await SetUpThisPcAsync(this, pairings, text => StatusText.Text = text, lifetime.Token);
        if (status is not null) StatusText.Text = status;
        if (host is not null) await ConnectedAsync(host, lifetime.Token);
    });

    /// <summary>Lets other PCs on the private network reach this PC's host port and find this PC (<see cref="Nearby"/>): one
    /// UAC prompt, only when needed. A caller that asks while another one checks the firewall waits for that one
    /// (<paramref name="by"/> names this caller for the next).</summary>
    internal static Task<string?> OpenFirewallAsync(Window owner, string address, Action<string> progress, CancellationToken token,
        string by) =>
        SharedSteps.RunAsync(SharedSteps.Firewall, by, "checking Windows Firewall", () => ApplyFirewallAsync(owner, address, progress, token),
            progress, null, token);

    private static async Task<string?> ApplyFirewallAsync(Window owner, string address, Action<string> progress, CancellationToken token)
    {
        progress("Checking Windows Firewall...");
        var state = await WindowsFirewall.ProbeAsync(address, token);
        var blocked = state.DockerBlocked
            ? " Windows Firewall also blocks Docker Desktop. Allow it in Windows Security if other PCs still can't connect."
            : "";
        int? makePrivate = null;
        if (state.Category == "Public")
        {
            if (!ConfirmationDialog.Confirm(owner,
                    "Windows treats this network as Public. To use this PC as a host, Martlet needs to mark it as Private " +
                    "and allow connections from your private network (so your other computers can also find this PC). " +
                    "Windows asks for administrator approval once.", "Allow connections"))
                return "Firewall unchanged. Other PCs may not reach this host." + blocked;
            makePrivate = state.InterfaceIndex;
        }
        else if (state.RuleExists && state.NearbyRulesExist) return blocked.Length == 0 ? null : blocked.Trim();
        progress("Windows asks for administrator approval to allow Martlet on your private network...");
        return await WindowsFirewall.ApplyAsync(makePrivate, token) switch
        {
            WindowsFirewall.Outcome.Applied => "Windows Firewall now allows Martlet on your private network" +
                (makePrivate is null ? "." : ", and the network is Private.") + blocked,
            WindowsFirewall.Outcome.Declined => "Firewall unchanged. Other PCs may not reach this host." + blocked,
            _ => "Windows Firewall could not be changed; other PCs may not reach this host." + blocked
        };
    }


    /// <summary>One card per role in <see cref="HostRoles.All"/>, each with Add and Remove running the same martlet-host flow.</summary>
    private void BuildRoleCards()
    {
        RoleCards.Children.Clear();
        foreach (var role in HostRoles.All)
        {
            var card = new Border();
            card.SetResourceReference(StyleProperty, "CardStyle");
            var stack = new StackPanel();
            var header = new DockPanel();
            var chip = new Border { VerticalAlignment = VerticalAlignment.Top };
            chip.SetResourceReference(StyleProperty, "Chip");
            DockPanel.SetDock(chip, Dock.Right);
            var chipText = new TextBlock { Text = "Available", FontWeight = FontWeights.SemiBold };
            chipText.SetResourceReference(StyleProperty, "ChipText");
            chipText.SetResourceReference(TextBlock.ForegroundProperty, "SuccessBrush");
            chip.Child = chipText;
            header.Children.Add(chip);
            header.Children.Add(new TextBlock { Text = role.Name, FontSize = 17, FontWeight = FontWeights.SemiBold });
            stack.Children.Add(header);
            var about = new TextBlock { Text = role.Description, Margin = new Thickness(0, 4, 0, 12) };
            about.SetResourceReference(StyleProperty, "Muted");
            stack.Children.Add(about);
            var buttons = new WrapPanel();
            var add = new Button { Content = $"Add {role.Name}", Margin = new Thickness(0, 0, 10, 0) };
            add.SetResourceReference(StyleProperty, "PrimaryButton");
            System.Windows.Automation.AutomationProperties.SetAutomationId(add, "AddRole-" + role.Kind);
            add.Click += (_, _) => Run(role.Add);
            var remove = new Button { Content = $"Remove {role.Name}" };
            System.Windows.Automation.AutomationProperties.SetAutomationId(remove, "RemoveRole-" + role.Kind);
            remove.Click += (_, _) => Run(role.Remove);
            buttons.Children.Add(add);
            buttons.Children.Add(remove);
            stack.Children.Add(buttons);
            card.Child = stack;
            RoleCards.Children.Add(card);
        }
    }
    private void UpdateHost_Click(object sender, RoutedEventArgs e) => Run(HostAction.Update);

    internal const string InstallDockerTitle = "Install Docker Desktop";

    /// <summary>Whether Docker Desktop is being installed now (by Install Docker Desktop or the prerequisites installer).</summary>
    internal static bool InstallingDocker => SharedSteps.IsRunning(SharedSteps.DockerInstall) || HostRunWindow.IsRunningTitled(InstallDockerTitle);

    /// <summary>Installs Docker Desktop with winget in a run window (no console) after the user accepts the listed terms,
    /// turns on what it needs from Windows (virtualization features and WSL; a restart, when needed, continues with
    /// <paramref name="resume"/> after the next sign-in), then starts it. Status is null when declined; Ready is true once
    /// Docker Desktop is installed and starting. While it is being installed already, this waits for that install (its
    /// window comes forward) instead of asking again.</summary>
    internal static async Task<(string? Status, bool Ready)> InstallDockerDesktopAsync(Window owner,
        ContinueSetupKind resume = ContinueSetupKind.Docker)
    {
        if (!InstallingDocker && !ConfirmationDialog.Confirm(owner,
                "Install Docker Desktop now? Martlet uses Windows' package installer and may turn on WSL 2 and virtualization features. " +
                "Windows may ask for administrator approval and a restart. After you sign in, Martlet continues setup.\n\n" +
                "By continuing, you accept Docker's Subscription Service Agreement (free for personal use).", "Install Docker Desktop"))
            return (null, false);
        var summary = await HostRunWindow.RunAsync(owner, InstallDockerTitle, async run =>
        {
            await HostLocal.InstallDockerDesktopAsync(run);
            await WindowsVirtualizationSetup.EnsureReadyAsync(run, resume);
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(MachineInfo.DockerDesktopPath) { UseShellExecute = true })?.Dispose(); }
            catch (Exception error) when (error is System.ComponentModel.Win32Exception or IOException) { run.Output.Report("Start Docker Desktop yourself: " + error.Message); }
            return "Docker Desktop is installed and starting. Follow Docker Desktop if it asks you to finish setup.";
        }, join: true);
        if (summary is not null) return (summary, true);
        return (MachineInfo.DockerDesktopInstalled()
            ? "Docker Desktop is installed, but Windows isn't ready to start it yet. Check the run window for details."
            : "Docker Desktop was not installed. Check the run window for details.", false);
    }

    // ---------- pairing ----------

    private void CopyDeviceId_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(DeviceIdText.Text.Trim()); StatusText.Text = "Device ID copied."; }
        catch (System.Runtime.InteropServices.ExternalException) { StatusText.Text = "Could not copy; select the device ID and copy it."; }
    }


    private void PairingCode_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter) return;
        e.Handled = true;
        Pair_Click(sender, e);
    }

    /// <summary>Pairs with the address and short code a host shows, or with an older host's whole pasted
    /// martlet-pair-v1 card.</summary>
    private async void Pair_Click(object sender, RoutedEventArgs e) => await ActionAsync("pair", async () =>
    {
        var text = PairingCodeBox.Text;
        var device = DeviceIdText.Text.Trim();
        var card = HostPairingInput.IsCard(text);
        Audio2FaceHostPairing pairing;
        string secret;
        if (card)
        {
            var code = HostPairingCode.Parse(text);
            StatusText.Text = $"Pairing with {code.HostId} at {code.Origin}...";
            await pairings.CheckCanKeepAsync(lifetime.Token);
            (pairing, secret) = await code.PairAsync(device, lifetime.Token);
        }
        else
        {
            var origin = HostPairingInput.Origin(PairAddressText.Text);
            HostPairingInput.NormalizeCode(text);
            StatusText.Text = $"Pairing with the host at {new Uri(origin).Authority}...";
            await pairings.CheckCanKeepAsync(lifetime.Token);
            (pairing, secret) = await Audio2FaceHostClient.PairWithCodeAsync(origin, text, device, Environment.MachineName, lifetime.Token);
        }
        PairingCodeBox.Clear();
        var shown = new Uri(pairing.Origin);
        PairAddressText.Text = shown.Port == HostPairingInput.DefaultPort ? shown.Host : shown.Authority;
        // A code shown by another computer (its host dashboard or martlet-host pair) pairs that computer; Martlet then reaches
        // it through Martlet on that computer. This PC's own host keeps running here.
        var method = HostRegistry.IsThisPc(shown.Host, HostSetupCommands.ThisPcAddress()) ? HostSetupMethod.ThisPcDocker : HostSetupMethod.Agent;
        var host = await SavePairingAsync(pairing, secret, method, null, null);
        // An older host's pairing console stays open until it is stopped there.
        StatusText.Text += card ? " To close the pairing console on the host, press a key, type stop and confirm." : "";
        // Read what the host is like for the map and the advisor; a host that restarts its gateway after pairing may not answer yet.
        try { StatusText.Text += " " + await CheckAsync(host.Pairing, Hardware, _ => { }, lifetime.Token); }
        catch (Exception error) when (error is InvalidOperationException or OperationCanceledException or Audio2FaceHostException) { }
        if (!lifetime.IsCancellationRequested) ShowStep(1);
    });

    /// <summary>Keeps a new pairing: the secret in Windows Credential Manager, the host in hosts.json (with how Martlet
    /// reaches it and its pinned SSH host key). <paramref name="token"/>: a run window's, so a run that outlives this wizard
    /// (hidden in Background tasks) still keeps its pairing; this wizard's lifetime otherwise.</summary>
    private async Task<PairedHost> SavePairingAsync(Audio2FaceHostPairing pairing, string secret, HostSetupMethod method, string? ssh,
        string? sshHostKey, CancellationToken? token = null)
    {
        var (host, lipSync) = await KeepPairingAsync(pairings, pairing, secret, method, ssh, sshHostKey, token ?? lifetime.Token);
        paired = host;
        ShowHosts((await pairings.LoadAsync(token ?? lifetime.Token)).Hosts);
        StatusText.Text = $"Paired with {host.HostId}. " + (lipSync
            ? "It keeps handling lip-sync."
            : "It's ready. Choose its jobs.");
        return host;
    }

    private static async Task<(PairedHost Host, bool LipSync)> KeepPairingAsync(HostPairings pairings, Audio2FaceHostPairing pairing,
        string secret, HostSetupMethod method, string? ssh, string? sshHostKey, CancellationToken token)
    {
        await pairings.CheckCanKeepAsync(token);
        var store = new WindowsCredentialStore();
        using (var lease = new SecretLease(secret))
        {
            var stored = store.WriteAvatarHostSecret(pairing.HostId, pairing.CredentialId, lease);
            if (stored != CredentialError.None) throw new InvalidOperationException(CredentialMessages.Describe(stored));
        }
        var remote = new AvatarRemoteHost
        {
            Origin = pairing.Origin, HostId = pairing.HostId, SpkiFingerprint = pairing.SpkiFingerprint,
            DeviceId = pairing.DeviceId, CredentialId = pairing.CredentialId
        };
        return await pairings.AddAsync(remote, method, ssh, token, sshHostKey);
    }

    /// <summary>One click sets up Martlet's host service on this PC: offers to install Docker Desktop when it is missing,
    /// opens the firewall port for the private network (one UAC prompt, only when needed), then in a run window starts
    /// Docker, builds the host image, runs setup unattended, pairs this desktop and reads the host's hardware.
    /// Returns the paired host (null when it did not finish) and a status line for the caller to show. <paramref name="then"/>
    /// continues in the same run window once the host service is paired (for example installing a role and switching a job
    /// to it), so the whole chain is one window; its summary is the status.</summary>
    internal static Task<(PairedHost? Host, string? Status)> SetUpThisPcAsync(Window owner, AvatarProfileStore profiles,
        ISetupService settings, Action<string> progress, CancellationToken token,
        Func<HostRunWindow, PairedHost, Task<string>>? then = null, string? title = null) =>
        SetUpThisPcAsync(owner, new HostPairings(Path.GetDirectoryName(profiles.FilePath)!, profiles, settings), progress, token, then, title);

    private static async Task<(PairedHost? Host, string? Status)> SetUpThisPcAsync(Window owner, HostPairings pairings,
        Action<string> progress, CancellationToken token, Func<HostRunWindow, PairedHost, Task<string>>? then = null, string? title = null)
    {
        if (!MachineInfo.DockerDesktopInstalled())
        {
            var (installing, ready) = await InstallDockerDesktopAsync(owner, ContinueSetupKind.ThisPc);
            if (!ready) return (null, installing);
            progress(installing ?? "Docker Desktop is installed.");
        }
        var address = HostSetupCommands.ThisPcAddress();
        if (!HostSetupCommands.IsPrivate(address))
            return (null, "This PC is not on a private network. Connect it to your home network first.");
        var heading = title ?? HostActions.ThisPcSetupTitle;
        string? firewall;
        try { firewall = await OpenFirewallAsync(owner, address!, progress, token, heading); }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        { firewall = $"Couldn't update Windows Firewall: {error.Message}. Other PCs may not reach this host."; }
        var version = typeof(App).Assembly.GetName().Version is { } v ? v.ToString(3) : "0.0.0";
        var target = new HostSetupTarget(HostSetupMethod.ThisPcDocker, "", address!,
            HostSetupCommands.SuggestedHostId(Environment.MachineName), version);
        var (hosts, _) = await pairings.LoadAsync(token);
        var deviceId = hosts.FirstOrDefault()?.Pairing.DeviceId ?? HostSetupCommands.SuggestedDeviceId();
        PairedHost? host = null;
        progress("Setting up this PC as a host...");
        var summary = await HostRunWindow.RunAsync(owner, heading, async run =>
        {
            // Several flows can set this PC up at once (the Devices map, Listening or Speaking on this PC, Singing): one sets
            // up and pairs the host service, the others wait for it and carry on with their own next step.
            host = await SharedSteps.RunAsync(SharedSteps.ThisPcHost, run.Heading, "setting up and pairing this PC's host service", async () =>
            {
                await HostLocal.EnsureDockerAsync(run, ContinueSetupKind.ThisPc);
                await HostLocal.EnsureImageAsync(target, run);
                run.Status("Setting up this PC as a host...");
                var exit = await HostLocal.EngineAsync(target, ["setup"], run.Output, run.Token);
                if (exit != 0) throw new InvalidOperationException($"Setup stopped (exit {exit}). Check the output for details.");
                run.Status("Pairing this PC with the host...");
                var (pairing, secret) = await HostLocal.PairAsync(target, deviceId, Environment.MachineName, run.Output, run.Token);
                return (await KeepPairingAsync(pairings, pairing, secret, HostSetupMethod.ThisPcDocker, null, null, run.Token)).Host;
            }, run.Status, run.Output, run.Token);
            run.Status("Checking this PC's hardware...");
            string check;
            try { check = await CheckAsync(host.Pairing, new HostHardwareStore(pairings.DataDirectory), run.Status, run.Token); }
            catch (InvalidOperationException error) { check = "The host did not answer yet: " + error.Message; }
            var ready = $"This PC is set up as a host. {check}";
            if (then is null) return ready;
            run.Output.Report(ready);
            return await then(run, host);
        });
        var status = summary ?? (host is null ? "This PC was not set up as a host. Check the run window for details."
            : "This PC is set up as a host, but the next step stopped. Check the run window for details.");
        return (host, firewall is null ? status : firewall + " " + status);
    }

    /// <summary>Checks a paired host over its pinned pairing, reports which roles it offers and saves the hardware it
    /// reports (GPUs, CPU, memory) for the devices map and the setup advisor.</summary>
    internal static async Task<string> CheckAsync(AvatarRemoteHost host, HostHardwareStore? hardware, Action<string> progress,
        CancellationToken token)
    {
        progress($"Checking {host.HostId}...");
        var check = await HostControl.CheckAsync(host, hardware, token);
        if (check.Reachable != true) throw new InvalidOperationException(check.Text);
        return check.Text;
    }

    internal static async Task<(string Text, string? MartletVersion)> ReadHardwareAsync(Audio2FaceHostConnection connection,
        HostHardwareStore? store, CancellationToken token)
    {
        HostHardware? report;
        string? version;
        try { (report, version) = await connection.ReadMachineReportAsync(token); }
        catch (Audio2FaceHostException error) when (error.Code == "request.invalid")
        {
            return ("Hardware details aren't available yet. Update the host from the Devices map.", null);
        }
        if (report is null) return ("Hardware details aren't available yet. Check the host and try again.", version);
        try { store?.Save(report); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        return ("Hardware: " + DescribeHardware(report) + ".", version);
    }

    internal static string DescribeHardware(HostHardware report)
    {
        var parts = new List<string>
        {
            report.Gpus.Count == 0 ? "no dedicated GPU" : string.Join(" + ", report.Gpus.Select(g => g.Describe()))
        };
        if (report.MemoryGb is { } memory) parts.Add($"{memory:0} GB memory");
        parts.Add(report.OperatingSystem);
        return string.Join(", ", parts);
    }

    private HostHardwareStore Hardware => new(pairings.DataDirectory);

    private async void Check_Click(object sender, RoutedEventArgs e) => await ActionAsync("check", async () =>
    {
        if (paired is not { } host) { StatusText.Text = "No Martlet host paired."; return; }
        StatusText.Text = $"Host {host.HostId}: " + await CheckAsync(host.Pairing, Hardware, text => StatusText.Text = text, lifetime.Token);
    });

    private async void Forget_Click(object sender, RoutedEventArgs e) => await ActionAsync("forget", async () =>
    {
        if (paired is not { } host) { StatusText.Text = "No Martlet host paired."; return; }
        await pairings.ForgetAsync(host.HostId, lifetime.Token);
        var (hosts, profile) = await pairings.LoadAsync(lifetime.Token);
        paired = hosts.FirstOrDefault(h => h.HostId == profile?.RemoteHost?.HostId) ?? hosts.LastOrDefault();
        ShowHosts(hosts);
        StatusText.Text = $"Forgot {host.HostId} on this PC. To remove this PC from the host too, revoke {host.Pairing.DeviceId} in the host console.";
    });

    /// <summary>Away from home: paste the owner's invite and sign in (<see cref="SignInJoinWindow"/>); the pairing is kept
    /// like any other.</summary>
    private void JoinWithInvite_Click(object sender, RoutedEventArgs e)
    {
        var device = DeviceIdText.Text.Trim();
        new SignInJoinWindow(device, async (pairing, secret) =>
        {
            var host = await SavePairingAsync(pairing, secret, HostSetupMethod.Agent, null, null);
            try { await CheckAsync(host.Pairing, Hardware, _ => { }, lifetime.Token); }
            catch (Exception error) when (error is InvalidOperationException or Audio2FaceHostException) { }
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
        catch (Audio2FaceHostException error) { StatusText.Text = error.Message; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ContractException or
            InvalidOperationException or ArgumentException or JsonException or TimeoutException)
        { StatusText.Text = error.Message; }
        finally { acting.Remove(what); }
    }
}
