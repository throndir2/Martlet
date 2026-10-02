using System.IO;
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

/// <summary>Add-a-computer wizard: choose where a Martlet host runs (this PC via Docker Desktop, another PC over SSH with
/// Docker or native Ubuntu, or by hand on the host), install it, pair this desktop with its one-use code and add roles.</summary>
public partial class HostsWindow : ThemedWindow
{
    private readonly HostPairings pairings;
    private readonly CancellationTokenSource lifetime = new();
    private readonly string version = typeof(App).Assembly.GetName().Version is { } v ? v.ToString(3) : "0.0.0";
    private PairedHost? paired;
    private bool busy;
    private bool choosingCode;
    private int step;

    internal HostsWindow(AvatarProfileStore profiles, ISetupService settings, HostSetupMethod? method = null, int startStep = 0,
        PairedHost? manage = null)
    {
        InitializeComponent();
        BuildRoleCards();
        pairings = new(Path.GetDirectoryName(profiles.FilePath)!, profiles, settings);
        paired = manage;
        DeviceIdText.Text = manage?.Pairing.DeviceId ?? HostSetupCommands.SuggestedDeviceId();
        method ??= manage?.Method;
        (method switch
        {
            HostSetupMethod.SshDocker => SshDockerMethod,
            HostSetupMethod.SshNative => SshNativeMethod,
            HostSetupMethod.OnHost => OnHostMethod,
            _ => ThisPcMethod
        }).IsChecked = true;
        if (manage is not null)
        {
            SshTargetText.Text = manage.SshTarget ?? "";
            AddressText.Text = manage.Address;
            PairAddressText.Text = manage.Address;
        }
        ShowStep(Math.Clamp(startStep, 0, 3), animate: false);
    }

    private HostSetupMethod Method =>
        SshDockerMethod.IsChecked == true ? HostSetupMethod.SshDocker
        : SshNativeMethod.IsChecked == true ? HostSetupMethod.SshNative
        : OnHostMethod.IsChecked == true ? HostSetupMethod.OnHost
        : HostSetupMethod.ThisPcDocker;

    private HostSetupTarget Target() => new(Method, SshTargetText.Text.Trim(), AddressText.Text.Trim(),
        Method == HostSetupMethod.ThisPcDocker ? HostSetupCommands.SuggestedHostId(Environment.MachineName) : null, version);

    private async void Window_Loaded(object sender, RoutedEventArgs e) => await ActionAsync(async () =>
    {
        var (hosts, profile) = await pairings.LoadAsync(lifetime.Token);
        paired = paired is { } manage ? hosts.FirstOrDefault(h => h.HostId == manage.HostId) ?? manage
            : hosts.FirstOrDefault(h => h.HostId == profile.RemoteHost?.HostId) ?? hosts.LastOrDefault();
        if (paired is not null) DeviceIdText.Text = paired.Pairing.DeviceId;
        ShowPaired(hosts.Count);
    });

    private void Window_Closed(object? sender, EventArgs e) => lifetime.Cancel();

    // ---------- wizard navigation ----------

    private RadioButton[] Rail => [Rail0, Rail1, Rail2, Rail3];
    private StackPanel[] Steps => [MethodStep, InstallStep, PairStep, RolesStep];

    private void ShowStep(int value, bool animate = true)
    {
        step = value;
        var steps = Steps;
        for (var i = 0; i < steps.Length; i++) steps[i].Visibility = i == step ? Visibility.Visible : Visibility.Collapsed;
        if (Rail[step].IsChecked != true) Rail[step].IsChecked = true;
        BackButton.IsEnabled = step > 0;
        NextButton.Content = step == steps.Length - 1 ? "_Done" : "_Next";
        MethodSummaryText.Text = Method == HostSetupMethod.OnHost
            ? "Enter the host's address, then run the command shown below on that computer."
            : Ssh
                ? "Enter user@computer, then choose Add this computer. Martlet signs in once and sets up the host."
                : Method == HostSetupMethod.ThisPcDocker
                    ? "Choose Set up host. Martlet starts Docker Desktop and pairs this PC automatically."
                    : "Enter the host address, then choose Set up host and follow the prompts.";
        RolesSummaryText.Text = Ssh
            ? "Add or remove jobs over SSH. Martlet asks for anything each job needs and shows progress."
            : Method == HostSetupMethod.ThisPcDocker
                ? "Add or remove jobs on this PC. Martlet asks for anything each job needs and shows progress."
                : "Add or remove jobs on the host itself. Martlet shows a command for each job.";
        SetupButton.Content = Ssh ? "_Add this computer" : "Set _up host";
        var byCode = Method == HostSetupMethod.OnHost;
        PairAutoCard.Visibility = byCode ? Visibility.Collapsed : Visibility.Visible;
        PairCommandSection.Visibility = byCode ? Visibility.Visible : Visibility.Collapsed;
        PairConsoleButton.Content = Ssh ? "_Pair over SSH" : "_Pair automatically";
        PairConsoleText.Text = Ssh
            ? "Martlet gets a one-use code over SSH and pairs this PC automatically."
            : "Martlet gets a one-use code from this PC's host service and pairs automatically.";
        PairCodeTitle.Text = byCode ? "Enter the code shown on the host" : "Or enter a code from the host";
        PairCodeHelp.Text = byCode
            ? "On the host, run the command below, or choose Show a pairing code on a Windows host's Home page. " +
              "Enter the address and code it shows within five minutes."
            : "If Martlet can't reach the host, show a pairing code on the host and enter its address and code here.";
        if (byCode) PairCommandText.Text = CommandFor(HostAction.Pair);
        if (PairAddressText.Text.Length == 0 && Method != HostSetupMethod.ThisPcDocker) PairAddressText.Text = AddressText.Text.Trim();
        Scroller.ScrollToTop();
        if (animate) Motion.Enter(steps[step], dx: 28, dy: 0, milliseconds: 280);
    }

    private string CommandFor(HostAction action)
    {
        try { return HostSetupCommands.Preview(Target(), action); }
        catch (InvalidOperationException error) { return error.Message; }
    }

    /// <summary>Straight to pairing with a code a host already shows: Martlet can't reach that host to run commands, so the
    /// pairing is saved as one it reaches only through its gateway.</summary>
    private void EnterCode_Click(object sender, RoutedEventArgs e)
    {
        choosingCode = true;
        try { OnHostMethod.IsChecked = true; }
        finally { choosingCode = false; }
        ShowStep(2);
        Dispatcher.BeginInvoke(() => (PairAddressText.Text.Length == 0 ? PairAddressText : PairingCodeBox).Focus(),
            System.Windows.Threading.DispatcherPriority.Input);
    }

    private void Rail_Checked(object sender, RoutedEventArgs e)
    {
        if (MethodStep is null) return;
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

    private void Method_Changed(object sender, RoutedEventArgs e)
    {
        if (SshPanel is null || AddressText is null) return;
        var ssh = Ssh;
        SshPanel.Visibility = ssh ? Visibility.Visible : Visibility.Collapsed;
        DockerPanel.Visibility = Method == HostSetupMethod.ThisPcDocker ? Visibility.Visible : Visibility.Collapsed;
        DockerStateText.Text = MachineInfo.DockerDesktopRunning() ? "Docker Desktop is running."
            : MachineInfo.DockerDesktopInstalled() ? "Docker Desktop is installed. Set up host starts it."
            : "Docker Desktop is not installed. Martlet can install it.";
        if (Method == HostSetupMethod.ThisPcDocker) AddressText.Text = HostSetupCommands.ThisPcAddress() ?? "";
        else if (AddressText.Text == HostSetupCommands.ThisPcAddress()) AddressText.Text = "";
        ShowCommand();
        if (IsLoaded && step == 0 && !choosingCode) Dispatcher.BeginInvoke(() => ShowStep(1), System.Windows.Threading.DispatcherPriority.Background);
    }

    // ---------- install ----------

    private async void SshTarget_LostFocus(object sender, RoutedEventArgs e)
    {
        if (AddressText.Text.Length > 0 || SshTargetText.Text.Trim().Length == 0) return;
        try { AddressText.Text = await HostSetupCommands.ResolveAsync(SshTargetText.Text.Trim(), lifetime.Token) ?? ""; }
        catch (OperationCanceledException) { }
    }

    private void Target_Changed(object sender, TextChangedEventArgs e) => ShowCommand();

    private void ShowCommand(HostAction? action = null)
    {
        if (CommandText is null) return;
        try { CommandText.Text = HostSetupCommands.Preview(Target(), action ?? HostAction.Setup); }
        catch (InvalidOperationException error) { CommandText.Text = error.Message; }
    }

    private bool Ssh => Method is HostSetupMethod.SshDocker or HostSetupMethod.SshNative;

    // The saved host key for this SSH target when it is the paired host being managed.
    private string? PinnedHostKey => paired is { } host && host.SshTarget == SshTargetText.Text.Trim() ? host.SshHostKey : null;

    private void Run(HostAction action)
    {
        ShowCommand(action);
        if (Method == HostSetupMethod.OnHost)
        {
            StatusText.Text = "Run the command shown in Install on the host.";
            return;
        }
        RunInMartletAsync(action).Forget();
    }

    /// <summary>SSH hosts and this PC run in Martlet: setup and pairing as one flow, other actions in a run window.
    /// Nothing opens a console window.</summary>
    private async Task RunInMartletAsync(HostAction action)
    {
        if (busy) { StatusText.Text = "Another host action is still running."; return; }
        if (!Ssh)
        {
            busy = true;
            try
            {
                if (action.Verb == HostVerb.Pair) await PairThisPcAsync();
                else StatusText.Text = await HostActions.RunAsync(this, pairings.DataDirectory, Target(), null, action)
                    ?? "Stopped. Check the run window for details.";
            }
            catch (InvalidOperationException error) { StatusText.Text = error.Message; }
            finally { busy = false; }
            return;
        }
        HostShellTarget ssh;
        try { ssh = HostShellTarget.Parse(SshTargetText.Text); }
        catch (InvalidOperationException error) { StatusText.Text = error.Message; return; }
        busy = true;
        try
        {
            if (action.Verb is HostVerb.Setup or HostVerb.Pair)
            {
                var summary = await HostRunWindow.RunAsync(this, action.Verb == HostVerb.Setup ? $"Add {ssh} to Martlet" : $"Pair with {ssh}",
                    run => AddLinuxAsync(run, ssh, Method, pair: true, setup: action.Verb == HostVerb.Setup));
                if (summary is not null)
                {
                    StatusText.Text = summary;
                    if (action.Verb == HostVerb.Setup) ShowStep(3);
                }
                else StatusText.Text = "Stopped. Check the run window for details.";
                return;
            }
            var target = Target() with { HostId = null };
            var done = await HostActions.RunAsync(this, pairings.DataDirectory, target, PinnedHostKey, action);
            StatusText.Text = done ?? "Stopped. Check the run window for details.";
        }
        catch (InvalidOperationException error) { StatusText.Text = error.Message; }
        finally { busy = false; }
    }

    /// <summary>Pairs this desktop with this PC's host service (already set up) without a console: the host shows a one-use
    /// code, Martlet reads and redeems it and keeps the pairing.</summary>
    private async Task PairThisPcAsync()
    {
        var target = Target();
        var device = DeviceIdText.Text.Trim();
        PairedHost? host = null;
        var summary = await HostRunWindow.RunAsync(this, "Pair with this PC's host", async run =>
        {
            await HostLocal.EnsureDockerAsync(run, ContinueSetupKind.Docker);
            await HostLocal.EnsureImageAsync(target, run.Status, run.Output, run.Token);
            run.Status("Pairing this PC with its host...");
            var (pairing, secret) = await HostLocal.PairAsync(target, device, Environment.MachineName, run.Output, run.Token);
            host = (await KeepPairingAsync(pairings, pairing, secret, HostSetupMethod.ThisPcDocker, null, null, run.Token)).Host;
            return $"Paired with {host.HostId}.";
        });
        if (host is not null)
        {
            paired = host;
            ShowPaired((await pairings.LoadAsync(lifetime.Token)).Hosts.Count);
            ShowStep(3);
        }
        StatusText.Text = summary ?? "Stopped. Check the run window for details.";
    }

    /// <summary>Add a Linux computer: connect (password once, host key pinned), check Docker, run setup unattended,
    /// pair automatically and read its machine report. With setup false it only pairs again.</summary>
    private async Task<string> AddLinuxAsync(HostRunWindow run, HostShellTarget ssh, HostSetupMethod method, bool pair, bool setup)
    {
        var remote = new HostRemote(new HostShell(pairings.DataDirectory, run.Prompts));
        run.Status($"Connecting to {ssh}...");
        var (probe, hostKey) = await remote.ProbeAsync(ssh, PinnedHostKey, run.Token);
        run.Output.Report($"{ssh}: connected. Docker " +
            (probe.Docker ? probe.DockerAccess ? "is ready" : "needs sudo for this account" : "is not installed") + ".");
        if (HostRemote.Blocker(method, probe, ssh.ToString()) is { } blocker) throw new InvalidOperationException(blocker);
        var address = AddressText.Text.Trim();
        if (!HostSetupCommands.IsPrivate(address))
            address = probe.Address ?? await HostSetupCommands.ResolveAsync(ssh.ToString(), run.Token) ?? "";
        if (setup && !HostSetupCommands.IsPrivate(address))
            throw new InvalidOperationException("Enter the host's private network address under Install.");
        AddressText.Text = address;
        var target = new HostSetupTarget(method, ssh.ToString(), address, null, version);
        var sudo = HostRemote.NeedsSudo(method, probe);
        if (setup)
        {
            run.Status($"Setting up the host on {ssh}. This can take a few minutes...");
            var result = await remote.RunAsync(target, "setup", true, sudo, null, hostKey, run.Output, run.Token);
            if (result.ExitCode != 0)
                throw new InvalidOperationException($"Setup stopped on {ssh} (exit {result.ExitCode}). Check the output for details.");
        }
        if (!pair) return $"{ssh} is set up.";
        run.Status($"Pairing this PC with {ssh}...");
        var device = DeviceIdText.Text.Trim();
        var (pairing, secret, key) = await remote.PairAsync(target, device, Environment.MachineName, sudo, hostKey, run.Output, run.Token);
        var host = await SavePairingAsync(pairing, secret, method, ssh.ToString(), key);
        run.Status($"Checking {host.HostId}'s hardware...");
        string check;
        try { check = await CheckAsync(host.Pairing, Hardware, run.Status, run.Token); }
        catch (InvalidOperationException error) { check = "The host did not answer yet: " + error.Message; }
        return $"{host.HostId} is set up and paired. {check} Assign jobs on the Devices map.";
    }

    private async void ResetSshTrust_Click(object sender, RoutedEventArgs e) => await ActionAsync(async () =>
    {
        var ssh = HostShellTarget.Parse(SshTargetText.Text);
        if (!ConfirmationDialog.Confirm(this, $"Reset saved SSH trust for {ssh.Host} and forget any remembered sudo password? " +
                "Use this only after reinstalling that computer. The next connection will ask you to trust it again.", "Reset trust"))
            return;
        await new HostShell(pairings.DataDirectory, new HostShellDialogs(this)).ForgetHostKeyAsync(ssh, lifetime.Token);
        await pairings.ClearSshHostKeyAsync(ssh, lifetime.Token);
        HostShell.ForgetSudo(ssh);
        if (paired?.SshTarget is { } current && current == ssh.ToString()) paired = paired with { SshHostKey = null };
        StatusText.Text = $"Martlet will ask you to trust {ssh.Host} again next time.";
    });

    private async void Setup_Click(object sender, RoutedEventArgs e)
    {
        if (Method == HostSetupMethod.ThisPcDocker)
        {
            await ActionAsync(async () =>
            {
                var (host, status) = await SetUpThisPcAsync(this, pairings, text => StatusText.Text = text, lifetime.Token);
                if (host is not null)
                {
                    paired = host;
                    DeviceIdText.Text = host.Pairing.DeviceId;
                    PairedText.Text = $"Paired with {host.HostId} ({host.Pairing.Origin}). This PC is {host.Pairing.DeviceId}. Connection: {host.Reach}.";
                    ShowStep(3);
                }
                if (status is not null) StatusText.Text = status;
            });
            return;
        }
        Run(HostAction.Setup);
    }

    /// <summary>Lets other PCs on the private network reach this PC's host port: one UAC prompt, only when needed.</summary>
    internal static async Task<string?> OpenFirewallAsync(Window owner, string address, Action<string> progress, CancellationToken token)
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
                    "and allow connections from your private network. Windows asks for administrator approval once.", "Allow connections"))
                return "Firewall unchanged. Other PCs may not reach this host." + blocked;
            makePrivate = state.InterfaceIndex;
        }
        else if (state.RuleExists) return blocked.Length == 0 ? null : blocked.Trim();
        progress("Windows asks for administrator approval to allow Martlet on your private network...");
        return await WindowsFirewall.ApplyAsync(makePrivate, token) switch
        {
            WindowsFirewall.Outcome.Applied => "Windows Firewall now allows Martlet on your private network" +
                (makePrivate is null ? "." : ", and the network is Private.") + blocked,
            WindowsFirewall.Outcome.Declined => "Firewall unchanged. Other PCs may not reach this host." + blocked,
            _ => "Windows Firewall could not be changed; other PCs may not reach this host." + blocked
        };
    }

    private void Status_Click(object sender, RoutedEventArgs e) => Run(HostAction.Status);

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
    private void PairConsole_Click(object sender, RoutedEventArgs e) => Run(HostAction.Pair);
    private void UpdateHost_Click(object sender, RoutedEventArgs e) => Run(HostAction.Update);

    private async void InstallDocker_Click(object sender, RoutedEventArgs e)
    {
        if (busy) { StatusText.Text = "Another host action is still running."; return; }
        busy = true;
        try
        {
            if ((await InstallDockerDesktopAsync(this)).Status is { } status) StatusText.Text = status;
        }
        finally { busy = false; }
    }

    /// <summary>Installs Docker Desktop with winget in a run window (no console) after the user accepts the listed terms,
    /// turns on what it needs from Windows (virtualization features and WSL; a restart, when needed, continues with
    /// <paramref name="resume"/> after the next sign-in), then starts it. Status is null when declined; Ready is true once
    /// Docker Desktop is installed and starting.</summary>
    internal static async Task<(string? Status, bool Ready)> InstallDockerDesktopAsync(Window owner,
        ContinueSetupKind resume = ContinueSetupKind.Docker)
    {
        if (!ConfirmationDialog.Confirm(owner,
                "Install Docker Desktop now? Martlet uses Windows' package installer and may turn on WSL 2 and virtualization features. " +
                "Windows may ask for administrator approval and a restart. After you sign in, Martlet continues setup.\n\n" +
                "By continuing, you accept Docker's Subscription Service Agreement (free for personal use).", "Install Docker Desktop"))
            return (null, false);
        var summary = await HostRunWindow.RunAsync(owner, "Install Docker Desktop", async run =>
        {
            await HostLocal.InstallDockerDesktopAsync(run.Status, run.Output, run.Token);
            await WindowsVirtualizationSetup.EnsureReadyAsync(run, resume);
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(MachineInfo.DockerDesktopPath) { UseShellExecute = true })?.Dispose(); }
            catch (Exception error) when (error is System.ComponentModel.Win32Exception or IOException) { run.Output.Report("Start Docker Desktop yourself: " + error.Message); }
            return "Docker Desktop is installed and starting. Follow Docker Desktop if it asks you to finish setup.";
        });
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

    private void ShowPaired(int count)
    {
        var others = count > 1 ? $" {count} hosts are paired. Assign jobs on the Devices map." : "";
        PairedText.Text = paired is { } host
            ? $"Paired with {host.HostId} ({host.Pairing.Origin}). This PC is {host.Pairing.DeviceId}. Connection: {host.Reach}.{others}"
            : "No Martlet host paired yet.";
        StatusText.Text = paired is null ? "No Martlet host paired." : $"Showing {paired.HostId}.";
    }

    private void PairingCode_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter) return;
        e.Handled = true;
        Pair_Click(sender, e);
    }

    /// <summary>Pairs with the address and short code a host shows, or with an older host's whole pasted
    /// martlet-pair-v1 card.</summary>
    private async void Pair_Click(object sender, RoutedEventArgs e) => await ActionAsync(async () =>
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
            await pairings.LoadProfileAsync(lifetime.Token);
            (pairing, secret) = await code.PairAsync(device, lifetime.Token);
        }
        else
        {
            var origin = HostPairingInput.Origin(PairAddressText.Text);
            HostPairingInput.NormalizeCode(text);
            StatusText.Text = $"Pairing with the host at {new Uri(origin).Authority}...";
            await pairings.LoadProfileAsync(lifetime.Token);
            (pairing, secret) = await Audio2FaceHostClient.PairWithCodeAsync(origin, text, device, Environment.MachineName, lifetime.Token);
        }
        PairingCodeBox.Clear();
        var shown = new Uri(pairing.Origin);
        PairAddressText.Text = shown.Port == HostPairingInput.DefaultPort ? shown.Host : shown.Authority;
        // A code shown by another computer (its host dashboard or martlet-host pair) pairs that computer, even while the wizard
        // still shows This PC; Martlet then doesn't know how to reach it to run commands there.
        var method = Method == HostSetupMethod.ThisPcDocker && !HostRegistry.IsThisPc(new Uri(pairing.Origin).Host, HostSetupCommands.ThisPcAddress())
            ? HostSetupMethod.OnHost : Method;
        var host = await SavePairingAsync(pairing, secret, method, Ssh ? SshTargetText.Text.Trim() : null, PinnedHostKey);
        // An older host's pairing console stays open until it is stopped there.
        StatusText.Text += card && Method == HostSetupMethod.OnHost ? " To close the pairing console on the host, press a key, type stop and confirm." : "";
        // Read what the host is like for the map and the advisor; a host that restarts its gateway after pairing may not answer yet.
        try { StatusText.Text += " " + await CheckAsync(host.Pairing, Hardware, _ => { }, lifetime.Token); }
        catch (Exception error) when (error is InvalidOperationException or OperationCanceledException or Audio2FaceHostException) { }
    });

    /// <summary>Keeps a new pairing: the secret in Windows Credential Manager, the host in hosts.json (with how Martlet
    /// reaches it and its pinned SSH host key).</summary>
    private async Task<PairedHost> SavePairingAsync(Audio2FaceHostPairing pairing, string secret, HostSetupMethod method, string? ssh,
        string? sshHostKey)
    {
        var (host, lipSync) = await KeepPairingAsync(pairings, pairing, secret, method, ssh, sshHostKey, lifetime.Token);
        paired = host;
        ShowPaired((await pairings.LoadAsync(lifetime.Token)).Hosts.Count);
        StatusText.Text = $"Paired with {host.HostId}. " + (lipSync
            ? "It keeps handling lip-sync."
            : "It's ready. Assign jobs on the Devices map.");
        return host;
    }

    private static async Task<(PairedHost Host, bool LipSync)> KeepPairingAsync(HostPairings pairings, Audio2FaceHostPairing pairing,
        string secret, HostSetupMethod method, string? ssh, string? sshHostKey, CancellationToken token)
    {
        await pairings.LoadProfileAsync(token);
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
        string? firewall;
        try { firewall = await OpenFirewallAsync(owner, address!, progress, token); }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        { firewall = $"Couldn't update Windows Firewall: {error.Message}. Other PCs may not reach this host."; }
        var version = typeof(App).Assembly.GetName().Version is { } v ? v.ToString(3) : "0.0.0";
        var target = new HostSetupTarget(HostSetupMethod.ThisPcDocker, "", address!,
            HostSetupCommands.SuggestedHostId(Environment.MachineName), version);
        var (hosts, _) = await pairings.LoadAsync(token);
        var deviceId = hosts.FirstOrDefault()?.Pairing.DeviceId ?? HostSetupCommands.SuggestedDeviceId();
        PairedHost? host = null;
        progress("Setting up this PC as a host...");
        var summary = await HostRunWindow.RunAsync(owner, title ?? "Set up this PC as a host", async run =>
        {
            await HostLocal.EnsureDockerAsync(run, ContinueSetupKind.ThisPc);
            await HostLocal.EnsureImageAsync(target, run.Status, run.Output, run.Token);
            run.Status("Setting up this PC as a host...");
            var exit = await HostLocal.EngineAsync(target, ["setup"], run.Output, run.Token);
            if (exit != 0) throw new InvalidOperationException($"Setup stopped (exit {exit}). Check the output for details.");
            run.Status("Pairing this PC with the host...");
            var (pairing, secret) = await HostLocal.PairAsync(target, deviceId, Environment.MachineName, run.Output, run.Token);
            host = (await KeepPairingAsync(pairings, pairing, secret, HostSetupMethod.ThisPcDocker, null, null, run.Token)).Host;
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

    private async void Check_Click(object sender, RoutedEventArgs e) => await ActionAsync(async () =>
    {
        if (paired is not { } host) { ShowPaired(0); return; }
        StatusText.Text = $"Host {host.HostId}: " + await CheckAsync(host.Pairing, Hardware, text => StatusText.Text = text, lifetime.Token);
    });

    private async void Forget_Click(object sender, RoutedEventArgs e) => await ActionAsync(async () =>
    {
        if (paired is not { } host) { ShowPaired(0); return; }
        await pairings.ForgetAsync(host.HostId, lifetime.Token);
        var (hosts, profile) = await pairings.LoadAsync(lifetime.Token);
        paired = hosts.FirstOrDefault(h => h.HostId == profile.RemoteHost?.HostId) ?? hosts.LastOrDefault();
        ShowPaired(hosts.Count);
        StatusText.Text = $"Forgot {host.HostId} on this PC. To remove this PC from the host too, revoke {host.Pairing.DeviceId} in the host console.";
    });

    private async Task ActionAsync(Func<Task> action)
    {
        if (busy) { StatusText.Text = "Another host action is still running."; return; }
        busy = true;
        try { await action(); }
        catch (OperationCanceledException) { }
        catch (Audio2FaceHostException error) { StatusText.Text = error.Message; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ContractException or
            InvalidOperationException or ArgumentException or JsonException or TimeoutException)
        { StatusText.Text = error.Message; }
        finally { busy = false; }
    }
}
