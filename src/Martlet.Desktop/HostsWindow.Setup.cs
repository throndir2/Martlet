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

// The Add a computer wizard's new hosts: set one up over SSH or on this PC, open the firewall, the role cards and installing Docker Desktop.
public partial class HostsWindow
{
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
            StatusText.Text = done ?? "Stopped; the run window shows why.";
        }
        catch (InvalidOperationException error) { StatusText.Text = error.Message; }
    }

    /// <summary>Add a Linux computer: connect (password once, host key pinned), choose Docker or native from what it
    /// has (keeping how an existing host there runs), run setup unattended, pair automatically and read its machine report.</summary>
    private async Task<string> AddLinuxAsync(HostRunWindow run, HostShellTarget ssh, string? pinnedHostKey, HostSetupMethod? existing)
    {
        var remote = new HostRemote(new HostShell(pairings.DataDirectory, run.Prompts));
        run.Status($"Connecting to {ssh}...");
        var (probe, hostKey) = await remote.ProbeAsync(ssh, pinnedHostKey, run.Token, run.Output);
        var method = existing ?? HostRemote.Choose(probe);
        run.Output.Report($"{ssh}: connected ({(probe.Nas is { } nas ? $"{nas} NAS, " : "")}{probe.OperatingSystem ?? "unknown system"}). Docker " +
            (probe.Docker ? probe.DockerAccess ? "is ready" : "needs sudo for this account" : "is not installed") +
            $", so Martlet sets it up {(method == HostSetupMethod.SshDocker ? "in Docker" : "natively")}" +
            (probe.Nas is null ? "." : " and installs nothing into the NAS system."));
        if (probe.Martlet is { } there)
            run.Output.Report($"A Martlet host is already on {ssh} ({(there == "native" ? "without Docker" : there)}); setup keeps its " +
                "identity, pairings and roles" + (there == $"martlet-host:{version}" ? "." : $" and brings it to Martlet {version}."));
        if (HostRemote.Blocker(method, probe, ssh.ToString()) is { } blocker) throw new InvalidOperationException(blocker);
        var address = AddressText.Text.Trim();
        if (!HostSetupCommands.IsPrivate(address))
            address = probe.Address ?? await HostSetupCommands.ResolveAsync(ssh.ToString(), run.Token) ?? "";
        if (!HostSetupCommands.IsPrivate(address))
            throw new InvalidOperationException($"Martlet couldn't find {ssh}'s private network address. Enter it under Its private network address.");
        AddressText.Text = address;
        // Its own name goes with it: a Docker-method host on Docker Desktop can't read it (Docker Desktop names its engine
        // docker-desktop), and its host ID is made from it by the one rule every host uses.
        var target = new HostSetupTarget(method, ssh.ToString(), address, null, version, HostSetupCommands.SafeComputerName(probe.Hostname));
        var sudo = HostRemote.NeedsSudo(method, probe);
        run.Status($"Checking whether {ssh} reaches the internet...");
        var supplied = await remote.SupplyIfOfflineAsync(target, HostVerb.Setup, pairings.DataDirectory, hostKey, run.Output, run.Token);
        run.Status($"Setting up the host on {ssh} (this can take a few minutes)...");
        string? reason = null;
        var output = new LineSink(line =>
        {
            var text = line.Trim();
            if (text.Length > 0 && (reason is null || !reason.StartsWith("Stopped:", StringComparison.Ordinal) || text.StartsWith("Stopped:", StringComparison.Ordinal)))
                reason = text;
            run.Output.Report(line);
        });
        var result = await remote.RunAsync(target, "setup", true, sudo, null, hostKey, output, run.Token, supplied: supplied);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"Setup stopped on {ssh} (exit {result.ExitCode})" +
                (reason is null ? "; the output shows why." : $": {reason}"));
        run.Status($"Pairing this PC with {ssh}...");
        var device = DeviceIdText.Text.Trim();
        var (pairing, secret, key) = await remote.PairAsync(target, device, Martlet.Core.Network.ThisComputer.Name, sudo, hostKey, run.Output, run.Token);
        var host = await SavePairingAsync(pairing, secret, method, ssh.ToString(), key, run.Token);
        run.Status($"Checking {host.HostId}'s hardware...");
        try { run.Output.Report(await CheckAsync(host.Pairing, Hardware, run.Status, run.Token)); }
        catch (InvalidOperationException error)
        {
            return $"{host.HostId} is set up and paired, but it did not answer yet: {error.Message}";
        }
        return $"{host.HostId} is set up and paired, and your other computers pair with it by themselves.";
    }

    private async void ResetSshTrust_Click(object sender, RoutedEventArgs e) => await ActionAsync("trust", async () =>
    {
        var ssh = HostShellTarget.Parse(SshTargetText.Text);
        if (!ConfirmationDialog.Confirm(this, $"Reset SSH trust for {ssh.Host} and forget its saved sudo password " +
                "(do this only after you reinstall that computer)?", "Reset trust"))
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
            StatusText.Text = summary ?? "Stopped; the run window shows why.";
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
            ? "; Windows Firewall also blocks Docker Desktop, so allow it in Windows Security if other PCs still can't connect"
            : "";
        int? makePrivate = null;
        if (state.Category == "Public")
        {
            if (!ConfirmationDialog.Confirm(owner,
                    "Windows treats this network as Public: mark it as Private and let your other computers connect to and find " +
                    "this PC (Windows asks for administrator approval once)?", "Allow connections"))
                return "Firewall unchanged, so other PCs may not reach this host" + blocked + ".";
            makePrivate = state.InterfaceIndex;
        }
        else if (state.RuleExists && state.NearbyRulesExist)
            return blocked.Length == 0 ? null : "Windows Firewall blocks Docker Desktop, so allow it in Windows Security if other PCs can't connect.";
        progress("Windows asks for administrator approval to allow Martlet on your private network...");
        return await WindowsFirewall.ApplyAsync(makePrivate, token) switch
        {
            WindowsFirewall.Outcome.Applied => "Windows Firewall now allows Martlet on your private network" +
                (makePrivate is null ? "" : ", and the network is Private") + blocked + ".",
            WindowsFirewall.Outcome.Declined => "Firewall unchanged, so other PCs may not reach this host" + blocked + ".",
            _ => "Windows Firewall could not be changed, so other PCs may not reach this host" + blocked + "."
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
                "Install Docker Desktop and accept Docker's Subscription Service Agreement (free for personal use; Windows may ask " +
                "for administrator approval and a restart)?", "Install Docker Desktop"))
            return (null, false);
        var summary = await HostRunWindow.RunAsync(owner, InstallDockerTitle, async run =>
        {
            await HostLocal.InstallDockerDesktopAsync(run);
            await WindowsVirtualizationSetup.EnsureReadyAsync(run, resume);
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(MachineInfo.DockerDesktopPath) { UseShellExecute = true })?.Dispose(); }
            catch (Exception error) when (error is System.ComponentModel.Win32Exception or IOException) { run.Output.Report("Start Docker Desktop yourself: " + error.Message); }
            return "Docker Desktop is installed and starting; follow it if it asks you to finish setup.";
        }, join: true);
        if (summary is not null) return (summary, true);
        return (MachineInfo.DockerDesktopInstalled()
            ? "Docker Desktop is installed, but Windows isn't ready to start it yet; the run window shows why."
            : "Docker Desktop was not installed; the run window shows why.", false);
    }
}
