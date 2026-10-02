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
        }
        ShowStep(Math.Clamp(startStep, 0, 3), animate: false);
    }

    private HostSetupMethod Method =>
        SshDockerMethod.IsChecked == true ? HostSetupMethod.SshDocker
        : SshNativeMethod.IsChecked == true ? HostSetupMethod.SshNative
        : OnHostMethod.IsChecked == true ? HostSetupMethod.OnHost
        : HostSetupMethod.ThisPcDocker;

    private static string MethodName(HostSetupMethod method) => method switch
    {
        HostSetupMethod.SshDocker => "another computer over SSH, using Docker there",
        HostSetupMethod.SshNative => "another computer over SSH, as a native Ubuntu install",
        HostSetupMethod.OnHost => "another computer, where you type the commands yourself",
        _ => "this PC, with Docker Desktop"
    };

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
        MethodSummaryText.Text = $"Installing on {MethodName(Method)}. " + (Method == HostSetupMethod.OnHost
            ? "Enter the host's address, then run the command shown below in a terminal on that computer."
            : Ssh
                ? "Enter user@computer and press Add this computer. Martlet asks for that account's password once, adds its own SSH key, " +
                  "checks Docker, sets the host up and pairs this PC by itself. You never need to log in there."
                : Method == HostSetupMethod.ThisPcDocker
                    ? "Press Set up host. Martlet starts Docker Desktop, sets the host up and pairs this PC by itself; nothing to type."
                    : "Fill in the address, then press Set up host and answer the questions in the console window.");
        RolesSummaryText.Text = Ssh
            ? $"Roles install from here over SSH; Martlet asks for what each role needs and shows the progress. This host runs on {MethodName(Method)}."
            : Method == HostSetupMethod.ThisPcDocker
                ? $"Roles install from here; Martlet asks for what each role needs and shows the progress. This host runs on {MethodName(Method)}."
                : $"Roles are added on the host itself, one at a time, with the same flow for every role. This host runs on {MethodName(Method)}.";
        SetupButton.Content = Ssh ? "_Add this computer" : "Set _up host";
        PairConsoleButton.Content = Ssh ? "_Pair automatically over SSH" : Method == HostSetupMethod.ThisPcDocker ? "_Pair automatically" : "_Open pairing console";
        PairConsoleText.Text = Ssh
            ? "Martlet asks the host for a one-use code over SSH and pairs this PC with it; nothing to type or paste."
            : Method == HostSetupMethod.ThisPcDocker
                ? "Martlet asks this PC's host service for a one-use code and pairs this PC with it; nothing to type or paste."
                : "On the host run martlet-host pair, confirm opening it, type start, then pair with the device ID above and role voice. It shows a code starting with martlet-pair-v1.";
        Scroller.ScrollToTop();
        if (animate) Motion.Enter(steps[step], dx: 28, dy: 0, milliseconds: 280);
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
        DockerStateText.Text = MachineInfo.DockerDesktopRunning() ? "Running. You're ready to set up the host."
            : MachineInfo.DockerDesktopInstalled() ? "Installed. Set up host starts it if needed."
            : "Not installed. Martlet can install it with winget; Windows asks for approval.";
        if (Method == HostSetupMethod.ThisPcDocker) AddressText.Text = HostSetupCommands.ThisPcAddress() ?? "";
        else if (AddressText.Text == HostSetupCommands.ThisPcAddress()) AddressText.Text = "";
        ShowCommand();
        if (IsLoaded && step == 0) Dispatcher.BeginInvoke(() => ShowStep(1), System.Windows.Threading.DispatcherPriority.Background);
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
            StatusText.Text = "Run the command shown under Install on the host itself (for example in a terminal there).";
            return;
        }
        RunInMartletAsync(action).Forget();
    }

    /// <summary>SSH hosts and this PC run in Martlet: setup and pairing as one flow, other actions in a run window.
    /// Nothing opens a console window.</summary>
    private async Task RunInMartletAsync(HostAction action)
    {
        if (busy) { StatusText.Text = "Another host action is still finishing."; return; }
        if (!Ssh)
        {
            busy = true;
            try
            {
                if (action.Verb == HostVerb.Pair) await PairThisPcAsync();
                else StatusText.Text = await HostActions.RunAsync(this, pairings.DataDirectory, Target(), null, action)
                    ?? "Stopped. The run window shows why.";
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
                else StatusText.Text = "Stopped. The run window shows why.";
                return;
            }
            var target = Target() with { HostId = null };
            var done = await HostActions.RunAsync(this, pairings.DataDirectory, target, PinnedHostKey, action);
            StatusText.Text = done ?? "Stopped. The run window shows why.";
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
        var summary = await HostRunWindow.RunAsync(this, "Pair with this PC's host service", async run =>
        {
            await HostLocal.EnsureDockerAsync(run, ContinueSetupKind.Docker);
            await HostLocal.EnsureImageAsync(target, run.Status, run.Output, run.Token);
            run.Status("Pairing this PC with its host service...");
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
        StatusText.Text = summary ?? "Stopped. The run window shows why.";
    }

    /// <summary>Add a Linux computer: connect (password once, host key pinned), check Docker, run setup unattended,
    /// pair automatically and read its machine report. With setup false it only pairs again.</summary>
    private async Task<string> AddLinuxAsync(HostRunWindow run, HostShellTarget ssh, HostSetupMethod method, bool pair, bool setup)
    {
        var remote = new HostRemote(new HostShell(pairings.DataDirectory, run.Prompts));
        run.Status($"Connecting to {ssh}...");
        var (probe, hostKey) = await remote.ProbeAsync(ssh, PinnedHostKey, run.Token);
        run.Output.Report($"{ssh}: {probe.OperatingSystem ?? "Linux"} ({probe.Architecture}), Docker " +
            (probe.Docker ? probe.DockerAccess ? "ready" : "installed (this account uses it through sudo)" : "not installed") +
            $", SSH host key {hostKey}.");
        if (HostRemote.Blocker(method, probe, ssh.ToString()) is { } blocker) throw new InvalidOperationException(blocker);
        var address = AddressText.Text.Trim();
        if (!HostSetupCommands.IsPrivate(address))
            address = probe.Address ?? await HostSetupCommands.ResolveAsync(ssh.ToString(), run.Token) ?? "";
        if (setup && !HostSetupCommands.IsPrivate(address))
            throw new InvalidOperationException("Enter the host's private LAN address (10.x, 172.16-31.x or 192.168.x) under Install.");
        AddressText.Text = address;
        var target = new HostSetupTarget(method, ssh.ToString(), address, null, version);
        var sudo = HostRemote.NeedsSudo(method, probe);
        if (setup)
        {
            run.Status($"Setting up the Martlet host on {ssh}. The first time it builds the host image there (a few minutes)...");
            var result = await remote.RunAsync(target, "setup", true, sudo, null, hostKey, run.Output, run.Token);
            if (result.ExitCode != 0)
                throw new InvalidOperationException($"Setup stopped on {ssh} (exit {result.ExitCode}). The output shows why.");
        }
        if (!pair) return $"{ssh} is set up.";
        run.Status($"Pairing this PC with {ssh}...");
        var device = DeviceIdText.Text.Trim();
        var (pairing, secret, key) = await remote.PairAsync(target, device, Environment.MachineName, sudo, hostKey, run.Output, run.Token);
        var host = await SavePairingAsync(pairing, secret, method, ssh.ToString(), key);
        run.Status($"Reading {host.HostId}'s hardware...");
        string check;
        try { check = await CheckAsync(host.Pairing, Hardware, run.Status, run.Token); }
        catch (InvalidOperationException error) { check = "Its check did not answer yet: " + error.Message; }
        return $"{host.HostId} is set up and paired. {check} It is on the Devices map; select it there to hand it jobs.";
    }

    private async void ResetSshTrust_Click(object sender, RoutedEventArgs e) => await ActionAsync(async () =>
    {
        var ssh = HostShellTarget.Parse(SshTargetText.Text);
        if (!ConfirmationDialog.Confirm(this, $"Forget the SSH host key Martlet pinned for {ssh.Host} and any remembered sudo password? " +
                "Do this only if that computer was reinstalled; the next connection shows its new key to confirm.", "Reset SSH trust"))
            return;
        await new HostShell(pairings.DataDirectory, new HostShellDialogs(this)).ForgetHostKeyAsync(ssh, lifetime.Token);
        await pairings.ClearSshHostKeyAsync(ssh, lifetime.Token);
        HostShell.ForgetSudo(ssh);
        if (paired?.SshTarget is { } current && current == ssh.ToString()) paired = paired with { SshHostKey = null };
        StatusText.Text = $"Martlet no longer trusts {ssh.Host}'s old SSH host key.";
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
                    PairedText.Text = $"{host.HostId} at {host.Pairing.Origin}, paired as {host.Pairing.DeviceId}. Reached via: {host.Reach}.";
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
            ? " Windows Firewall also has a rule blocking Docker Desktop Backend on private networks; allow it in Windows Security > Firewall > Allow an app."
            : "";
        int? makePrivate = null;
        if (state.Category == "Public")
        {
            if (!ConfirmationDialog.Confirm(owner,
                    "Windows treats this PC's network as Public, which blocks other PCs from reaching a Martlet host here. " +
                    $"Mark it as a Private (home or work) network and allow Martlet's host port {WindowsFirewall.Port} from your local network? " +
                    "Windows asks for administrator approval once.", "Allow other PCs to connect"))
                return "Firewall unchanged: this PC's network stays Public, so only this PC can use the host." + blocked;
            makePrivate = state.InterfaceIndex;
        }
        else if (state.RuleExists) return blocked.Length == 0 ? null : blocked.Trim();
        progress($"Windows asks for administrator approval to allow TCP {WindowsFirewall.Port} from your private network...");
        return await WindowsFirewall.ApplyAsync(makePrivate, token) switch
        {
            WindowsFirewall.Outcome.Applied => $"Windows Firewall now allows TCP {WindowsFirewall.Port} from your private network" +
                (makePrivate is null ? "." : ", and the network is Private.") + blocked,
            WindowsFirewall.Outcome.Declined => "Firewall unchanged (administrator approval declined); other PCs may not reach this host." + blocked,
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
        if (busy) { StatusText.Text = "Another host action is still finishing."; return; }
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
                "Install Docker Desktop with winget? Docker Desktop is free for personal use under Docker's Subscription Service " +
                "Agreement (docker.com/legal); continuing accepts the winget source and package agreements. It uses WSL 2, so Martlet " +
                "also turns on Virtual Machine Platform and Windows Subsystem for Linux when they're off. Windows asks for administrator " +
                "approval, and a restart may follow; Martlet then continues by itself after you sign in.", "Install Docker Desktop"))
            return (null, false);
        var summary = await HostRunWindow.RunAsync(owner, "Install Docker Desktop", async run =>
        {
            await HostLocal.InstallDockerDesktopAsync(run.Status, run.Output, run.Token);
            await WindowsVirtualizationSetup.EnsureReadyAsync(run, resume);
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(MachineInfo.DockerDesktopPath) { UseShellExecute = true })?.Dispose(); }
            catch (Exception error) when (error is System.ComponentModel.Win32Exception or IOException) { run.Output.Report("Start Docker Desktop yourself: " + error.Message); }
            return "Docker Desktop is installed and starting. Accept Docker's terms if it asks.";
        });
        if (summary is not null) return (summary, true);
        return (MachineInfo.DockerDesktopInstalled()
            ? "Docker Desktop is installed, but Windows isn't ready to start it yet. The run window shows why."
            : "Docker Desktop was not installed. The run window shows why.", false);
    }

    // ---------- pairing ----------

    private void CopyDeviceId_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(DeviceIdText.Text.Trim()); StatusText.Text = "Device ID copied."; }
        catch (System.Runtime.InteropServices.ExternalException) { StatusText.Text = "Could not copy; select the device ID and copy it."; }
    }

    private void ShowPaired(int count)
    {
        var others = count > 1 ? $" {count} hosts are paired in all; hand each one its jobs on the Devices map." : "";
        PairedText.Text = paired is { } host
            ? $"{host.HostId} at {host.Pairing.Origin}, paired as {host.Pairing.DeviceId}. Reached via: {host.Reach}.{others}"
            : "No Martlet host paired yet.";
        StatusText.Text = paired is null ? "No Martlet host paired." : $"Showing {paired.HostId}.";
    }

    private async void Pair_Click(object sender, RoutedEventArgs e) => await ActionAsync(async () =>
    {
        var code = HostPairingCode.Parse(PairingCodeBox.Password);
        var device = DeviceIdText.Text.Trim();
        StatusText.Text = $"Pairing with {code.HostId} at {code.Origin}...";
        await pairings.LoadProfileAsync(lifetime.Token);
        var (pairing, secret) = await code.PairAsync(device, lifetime.Token);
        PairingCodeBox.Clear();
        // A code shown by another computer (its host dashboard or pairing console) pairs that computer, even while the wizard
        // still shows This PC; Martlet then doesn't know how to reach it to run commands there.
        var method = Method == HostSetupMethod.ThisPcDocker && !HostRegistry.IsThisPc(new Uri(pairing.Origin).Host, HostSetupCommands.ThisPcAddress())
            ? HostSetupMethod.OnHost : Method;
        var host = await SavePairingAsync(pairing, secret, method, Ssh ? SshTargetText.Text.Trim() : null, PinnedHostKey);
        StatusText.Text += Method == HostSetupMethod.OnHost ? " In the host console press a key, then type stop and confirm." : "";
        // The pairing listener is still open, so read what the host is like right away for the map and the advisor.
        try { StatusText.Text += " " + await CheckAsync(host.Pairing, Hardware, _ => { }, lifetime.Token); }
        catch (Exception error) when (error is InvalidOperationException or OperationCanceledException) { }
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
            : "It stands by; select it on the Devices map to hand it jobs.");
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
            return (null, "This PC has no private network address (10.x, 172.16-31.x or 192.168.x). Connect it to your home network first.");
        string? firewall;
        try { firewall = await OpenFirewallAsync(owner, address!, progress, token); }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        { firewall = $"Windows Firewall was not changed ({error.Message}). Other PCs may not reach this host."; }
        var version = typeof(App).Assembly.GetName().Version is { } v ? v.ToString(3) : "0.0.0";
        var target = new HostSetupTarget(HostSetupMethod.ThisPcDocker, "", address!,
            HostSetupCommands.SuggestedHostId(Environment.MachineName), version);
        var (hosts, _) = await pairings.LoadAsync(token);
        var deviceId = hosts.FirstOrDefault()?.Pairing.DeviceId ?? HostSetupCommands.SuggestedDeviceId();
        PairedHost? host = null;
        progress("Setting up this PC's host service...");
        var summary = await HostRunWindow.RunAsync(owner, title ?? "Set up this PC's host service", async run =>
        {
            await HostLocal.EnsureDockerAsync(run, ContinueSetupKind.ThisPc);
            await HostLocal.EnsureImageAsync(target, run.Status, run.Output, run.Token);
            run.Status("Setting up the host service on this PC...");
            var exit = await HostLocal.EngineAsync(target, ["setup"], run.Output, run.Token);
            if (exit != 0) throw new InvalidOperationException($"Setup stopped (exit {exit}). The output shows why.");
            run.Status("Pairing this PC with its host service...");
            var (pairing, secret) = await HostLocal.PairAsync(target, deviceId, Environment.MachineName, run.Output, run.Token);
            host = (await KeepPairingAsync(pairings, pairing, secret, HostSetupMethod.ThisPcDocker, null, null, run.Token)).Host;
            run.Status("Reading this PC's hardware...");
            string check;
            try { check = await CheckAsync(host.Pairing, new HostHardwareStore(pairings.DataDirectory), run.Status, run.Token); }
            catch (InvalidOperationException error) { check = "Its check did not answer yet: " + error.Message; }
            var ready = $"This PC's host service ({host.HostId}) is set up and paired. {check}";
            if (then is null) return ready;
            run.Output.Report(ready);
            return await then(run, host);
        });
        var status = summary ?? (host is null ? "This PC's host service was not set up. The run window shows why."
            : $"This PC's host service ({host.HostId}) is set up, but the next step stopped. The run window shows why.");
        return (host, firewall is null ? status : firewall + " " + status);
    }

    /// <summary>Checks a paired host over its pinned pairing, reports which roles it offers and saves the hardware it
    /// reports (GPUs, CPU, memory) for the devices map and the setup advisor.</summary>
    internal static async Task<string> CheckAsync(AvatarRemoteHost host, HostHardwareStore? hardware, Action<string> progress,
        CancellationToken token)
    {
        progress($"Checking {host.HostId} at {host.Origin}...");
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
            return ("Its hardware is not reported: update the host (Update host on its card on the Devices map).", null);
        }
        if (report is null) return ("Its hardware is not reported yet: run 'martlet-host machine' on the host.", version);
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
        StatusText.Text = $"Forgot {host.HostId} here; also revoke {host.Pairing.DeviceId} on the host (pairing console: revoke).";
    });

    private async Task ActionAsync(Func<Task> action)
    {
        if (busy) { StatusText.Text = "Another host action is still finishing."; return; }
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
