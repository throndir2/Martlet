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

// The Add a computer wizard's pairing: pair with a code, keep the pairing, set up this PC's host service, check a host and forget it.
public partial class HostsWindow
{
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
        HostPairing pairing;
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
            (pairing, secret) = await HostTransport.PairWithCodeAsync(origin, text, device, Martlet.Core.Network.ThisComputer.Name, lifetime.Token);
        }
        PairingCodeBox.Clear();
        var shown = new Uri(pairing.Origin);
        PairAddressText.Text = shown.Port == HostPairingInput.DefaultPort ? shown.Host : shown.Authority;
        // A code shown by another computer (its host dashboard or martlet-host pair) pairs that computer; Martlet then reaches
        // it through Martlet on that computer. This PC's own host keeps running here.
        var method = HostRegistry.IsThisPc(shown.Host, HostSetupCommands.ThisPcAddress()) ? HostSetupMethod.ThisPcDocker : HostSetupMethod.Agent;
        var host = await SavePairingAsync(pairing, secret, method, null, null);
        // An older host's pairing console stays open until it is stopped there.
        if (card) StatusText.Text = StatusText.Text.TrimEnd('.') + "; to close the pairing console on the host, press a key, type stop and confirm.";
        // Read what the host is like for the map and the advisor; a host that restarts its gateway after pairing may not answer yet.
        try { await CheckAsync(host.Pairing, Hardware, _ => { }, lifetime.Token); }
        catch (Exception error) when (error is InvalidOperationException or OperationCanceledException or HostConnectionException) { }
        if (!lifetime.IsCancellationRequested) ShowStep(1);
    });

    /// <summary>Keeps a new pairing: the secret in Windows Credential Manager, the host in hosts.json (with how Martlet
    /// reaches it and its pinned SSH host key). <paramref name="token"/>: a run window's, so a run that outlives this wizard
    /// (hidden in Background tasks) still keeps its pairing; this wizard's lifetime otherwise.</summary>
    private async Task<PairedHost> SavePairingAsync(HostPairing pairing, string secret, HostSetupMethod method, string? ssh,
        string? sshHostKey, CancellationToken? token = null, IReadOnlyList<string>? outsideAddresses = null)
    {
        var (host, lipSync) = await KeepPairingAsync(pairings, pairing, secret, method, ssh, sshHostKey, token ?? lifetime.Token, outsideAddresses);
        paired = host;
        ShowHosts((await pairings.LoadAsync(token ?? lifetime.Token)).Hosts);
        StatusText.Text = lipSync ? $"Paired with {host.HostId}, which keeps handling lip-sync." : $"Paired with {host.HostId}; choose its jobs.";
        return host;
    }

    internal static async Task<(PairedHost Host, bool LipSync)> KeepPairingAsync(HostPairings pairings, HostPairing pairing,
        string secret, HostSetupMethod method, string? ssh, string? sshHostKey, CancellationToken token,
        IReadOnlyList<string>? outsideAddresses = null, string? access = null, string? signedInAs = null)
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
        return await pairings.AddAsync(remote, method, ssh, token, sshHostKey, outsideAddresses: outsideAddresses, access: access,
            signedInAs: signedInAs);
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
            return (null, "This PC is not on a private network; connect it to your home network first.");
        var heading = title ?? HostActions.ThisPcSetupTitle;
        // Windows Firewall doesn't depend on Docker Desktop or the host image: it opens while those get ready.
        var opening = OpenFirewallQuietlyAsync();
        async Task<string?> OpenFirewallQuietlyAsync()
        {
            try { return await OpenFirewallAsync(owner, address!, progress, token, heading); }
            catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
            { return $"Couldn't update Windows Firewall, so other PCs may not reach this host: {error.Message}"; }
        }
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
                var (pairing, secret) = await HostLocal.PairAsync(target, deviceId, Martlet.Core.Network.ThisComputer.Name, run.Output, run.Token);
                return (await KeepPairingAsync(pairings, pairing, secret, HostSetupMethod.ThisPcDocker, null, null, run.Token)).Host;
            }, run.Status, run.Output, run.Token);
            run.Status("Checking this PC's hardware...");
            string ready;
            try
            {
                run.Output.Report(await CheckAsync(host.Pairing, new HostHardwareStore(pairings.DataDirectory), run.Status, run.Token));
                ready = "This PC is set up as a host.";
            }
            catch (InvalidOperationException error) { ready = "This PC is set up as a host, but it did not answer yet: " + error.Message; }
            if (then is null) return ready;
            run.Output.Report(ready);
            return await then(run, host);
        });
        var firewall = await opening;
        var status = summary ?? (host is null ? "This PC was not set up as a host; the run window shows why."
            : "This PC is set up as a host, but the next step stopped; the run window shows why.");
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

    internal static async Task<(string Text, string? MartletVersion)> ReadHardwareAsync(HostConnection connection,
        HostHardwareStore? store, CancellationToken token)
    {
        HostHardware? report;
        string? version;
        try { (report, version) = await connection.ReadMachineReportAsync(token); }
        catch (HostConnectionException error) when (error.Code == "request.invalid")
        {
            return ("Hardware details aren't available yet; update the host from the Devices map.", null);
        }
        if (report is null) return ("Hardware details aren't available yet; check the host and try again.", version);
        try { store?.Save(report); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        // The host's cards or roles may have changed: its conversation model's window is planned again at its next request.
        HostContextWindow.Forget(report.HostId);
        RecordLoaded(report, store?.DataDirectory);
        return ("Hardware: " + DescribeHardware(report) + ".", version);
    }

    /// <summary>Keeps what a host's Ollama roles had loaded (its machine report's loaded models, from their /api/ps) in
    /// model-memory.json under the host's address, so the planner uses the measured memory instead of the estimate. A host check
    /// does it, never a reply. Older hosts don't report it.</summary>
    internal static int RecordLoaded(HostHardware report, string? dataDirectory)
    {
        if (dataDirectory is null || report.Loaded is not { Count: > 0 } loaded ||
            Martlet.Core.Planning.MeasuredFirstWords.HostKey(report.Origin) is not { } host) return 0;
        var saved = 0;
        foreach (var model in loaded)
            if (Martlet.Core.Planning.MeasuredModelMemory.Record(dataDirectory, new Martlet.Core.Planning.MeasuredModelUse
                {
                    Host = host, Model = model.Model, Bytes = model.Bytes, GraphicsBytes = model.GraphicsBytes, ContextTokens = model.ContextTokens,
                    Digest = model.Digest, MeasuredAt = report.ReceivedAt
                }))
                saved++;
        if (saved > 0)
            ErrorLog.Info($"{report.HostId} reported the memory of {saved} loaded model{(saved == 1 ? "" : "s")} ({string.Join(", ", loaded.Select(m =>
                $"{m.Model} {m.Bytes / 1e9:0.0} GB, {m.GraphicsBytes / 1e9:0.0} GB on the graphics card"))}); Martlet plans with it.");
        return saved;
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
        var (all, profile) = await pairings.LoadAsync(lifetime.Token);
        var hosts = all.Where(h => !h.Shared).ToList();
        paired = hosts.FirstOrDefault(h => h.HostId == profile?.RemoteHost?.HostId) ?? hosts.LastOrDefault();
        ShowHosts(hosts);
        StatusText.Text = $"Forgot {host.HostId} on this PC; to remove this PC from the host too, revoke {host.Pairing.DeviceId} in the host console.";
    });
}
