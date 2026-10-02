using System.Collections.Concurrent;
using System.IO;
using System.Net.Sockets;
using System.Windows;
using Martlet.Core.Installation;

namespace Martlet.Desktop;

/// <summary>Letting Martlet on the owner's other computers find this PC and use its hosts (<see cref="Nearby"/>). While
/// "Let my other computers find this PC" is on (the default) and this PC runs a host or reaches one over SSH, it answers
/// finds on port 9444; each request shows its check number here and needs the owner's Allow, then this PC asks each host for
/// a one-use pairing code in a run window and sends the codes to the asking computer.</summary>
public partial class MainWindow
{
    private sealed record ShareableHost(string HostId, HostSetupTarget Target, string? SshHostKey);

    private NearbyResponder? nearby;
    private bool nearbyEnabled = true;
    private bool? nearbyFirewall;
    private string? nearbyCategory;
    private bool nearbyCheckingFirewall;
    private string? nearbyProblem;
    private string? nearbyLast;
    private volatile NearbyOffer? nearbyOffer;
    private IReadOnlyList<ShareableHost> nearbyHosts = [];
    private string? nearbyLocalService;
    private DateTime nearbyLocalCheckedAt;
    private bool nearbyCheckingLocal;
    private JoinRequestWindow? joinPrompt;
    private bool joinRunning;

    private void InitializeNearby()
    {
        if (store is null)
        {
            NearbyShareChoice.IsEnabled = false;
            ShowNearbyStatus();
            return;
        }
        nearbyEnabled = Nearby.LoadEnabled(store.DataDirectory);
        NearbyShareChoice.IsChecked = nearbyEnabled;
        ShowNearbyStatus();
    }

    /// <summary>The hosts this PC can get a pairing code from: hosts it runs (paired here, or this PC's host service set up
    /// from the host dashboard) and hosts it reaches over SSH. Hosts reached only through Martlet on that computer can't be
    /// asked for a pairing code from here.</summary>
    private IReadOnlyList<ShareableHost> ShareableHosts()
    {
        var hosts = homeHosts.Where(h => h.CanLaunch && h.Method is HostSetupMethod.ThisPcDocker or HostSetupMethod.SshDocker or HostSetupMethod.SshNative)
            .Select(h => new ShareableHost(h.HostId, h.Target(Version), h.SshHostKey)).ToList();
        var local = ThisPcTarget();
        if (hosts.All(h => h.Target.Method != HostSetupMethod.ThisPcDocker) && (thisPcHostVersion ?? nearbyLocalService) is not null &&
            HostSetupCommands.IsPrivate(local.Address) && local.HostId is { } id && hosts.All(h => h.HostId != id))
            hosts.Insert(0, new(id, local, null));
        return hosts;
    }

    /// <summary>Starts, restarts or stops answering to match the choice, the shareable hosts and Windows Firewall.</summary>
    private void UpdateNearby()
    {
        if (store is null || closing) return;
        if (nearbyEnabled && !nearbyCheckingLocal && DateTime.UtcNow - nearbyLocalCheckedAt > TimeSpan.FromMinutes(2) &&
            homeHosts.All(h => h.Method != HostSetupMethod.ThisPcDocker || !h.CanLaunch))
            CheckLocalServiceAsync().Forget();
        nearbyHosts = nearbyEnabled ? ShareableHosts() : [];
        nearbyOffer = nearbyHosts.Count == 0 ? null
            : new(Nearby.Label(Environment.MachineName), ClusterDevice, Version, nearbyHosts.Select(h => h.HostId).Take(Nearby.MaximumHosts).ToArray());
        if (nearbyOffer is null) StopNearby();
        else
        {
            if (nearbyFirewall is null && !nearbyCheckingFirewall) CheckNearbyFirewallAsync().Forget();
            var lan = nearbyFirewall == true;
            if (nearby is null || nearby.Lan != lan) StartNearby(lan);
        }
        ShowNearbyStatus();
    }

    private void StartNearby(bool lan)
    {
        StopNearby();
        try
        {
            nearby = NearbyResponder.Start(lan, () => nearbyOffer,
                session => Dispatcher.InvokeAsync(() => HandleJoinAsync(session)).Task.Unwrap());
            nearbyProblem = null;
            ErrorLog.Info($"Nearby: answering on port {Nearby.Port} ({(lan ? "private network" : "this PC only, until Windows Firewall allows it")}).");
        }
        catch (SocketException error)
        {
            nearbyProblem = error.SocketErrorCode is SocketError.AddressAlreadyInUse or SocketError.AccessDenied
                ? $"port {Nearby.Port} is already used by another program (or another Martlet on this PC)"
                : error.Message;
            ErrorLog.Warn($"Nearby: could not answer on port {Nearby.Port}: {error.Message}");
        }
    }

    private void StopNearby()
    {
        nearby?.Dispose();
        nearby = null;
    }

    private async Task CheckLocalServiceAsync()
    {
        nearbyCheckingLocal = true;
        try { nearbyLocalService = await HostSetupCommands.ThisPcGatewayVersionAsync(lifetime.Token); }
        finally
        {
            nearbyCheckingLocal = false;
            nearbyLocalCheckedAt = DateTime.UtcNow;
        }
        if (closing || nearbyLocalService is null) return;
        UpdateNearby();
        RenderHost();
    }

    private async Task CheckNearbyFirewallAsync()
    {
        var address = machine.LanAddress ?? HostSetupCommands.ThisPcAddress();
        if (!HostSetupCommands.IsPrivate(address))
        {
            nearbyFirewall = false;
            return;
        }
        nearbyCheckingFirewall = true;
        try
        {
            var state = await WindowsFirewall.ProbeAsync(address!, lifetime.Token);
            nearbyFirewall = state.NearbyRulesExist;
            nearbyCategory = state.Category;
        }
        catch (OperationCanceledException) { return; }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            nearbyFirewall = false;
        }
        finally { nearbyCheckingFirewall = false; }
        if (closing) return;
        UpdateNearby();
        RenderHost();
    }

    /// <summary>Checked/Unchecked rather than Click, so UI Automation's toggle changes the choice too.</summary>
    private void NearbyShare_Changed(object sender, RoutedEventArgs e)
    {
        var on = NearbyShareChoice.IsChecked == true;
        if (store is null || on == nearbyEnabled) return;
        try { Nearby.SaveEnabled(store.DataDirectory, on); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            NearbyShareChoice.IsChecked = nearbyEnabled;
            ActionText.Text = $"Could not save {Nearby.PreferenceFile} in Martlet's data folder.";
            return;
        }
        nearbyEnabled = on;
        UpdateNearby();
        RenderHost();
        ActionText.Text = on ? "Martlet on your other computers can find this PC and ask to use its hosts; you allow each one here."
            : "Other computers can no longer find this PC or ask to use its hosts.";
    }

    /// <summary>Adds Windows Firewall rules for port 9444 on private networks (and, when Windows treats the network as Public,
    /// marks it Private after asking): one administrator prompt.</summary>
    private async void NearbyFirewall_Click(object sender, RoutedEventArgs e)
    {
        var address = machine.LanAddress ?? HostSetupCommands.ThisPcAddress();
        if (!HostSetupCommands.IsPrivate(address))
        {
            ActionText.Text = "This PC has no private network address (10.x, 172.16-31.x or 192.168.x). Connect it to your home network first.";
            return;
        }
        NearbyFirewallButton.IsEnabled = false;
        try
        {
            var state = await WindowsFirewall.ProbeAsync(address!, lifetime.Token);
            int? makePrivate = null;
            if (state.Category == "Public")
            {
                if (!ConfirmationDialog.Confirm(this,
                        "Windows treats this PC's network as Public, which stops your other computers from finding it. Mark it as a " +
                        $"Private (home or work) network and let Martlet on your local network reach port {Nearby.Port}? Windows asks " +
                        "for administrator approval once.", "Let your other computers find this PC"))
                    return;
                makePrivate = state.InterfaceIndex;
            }
            var outcome = await WindowsFirewall.ApplyAsync(makePrivate, lifetime.Token, gateway: false);
            ActionText.Text = outcome switch
            {
                WindowsFirewall.Outcome.Applied => $"Windows Firewall now lets Martlet on your private network reach port {Nearby.Port}.",
                WindowsFirewall.Outcome.Declined => "Firewall unchanged (administrator approval declined).",
                _ => "Windows Firewall could not be changed."
            };
            nearbyFirewall = null;
            UpdateNearby();
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            ActionText.Text = $"Windows Firewall was not changed ({error.Message}).";
        }
        finally { if (!closing) NearbyFirewallButton.IsEnabled = true; }
    }

    /// <summary>This PC has hosts to share and wants to be found, but Windows Firewall (or a Public network) keeps other
    /// computers from reaching port 9444.</summary>
    private bool NearbyBlocked => nearbyEnabled && nearbyHosts.Count > 0 && nearbyProblem is null &&
        (nearbyFirewall == false || nearbyFirewall == true && nearbyCategory == "Public");

    private void ShowNearbyStatus()
    {
        string text;
        if (store is null) text = "Unavailable without a local data folder.";
        else if (!nearbyEnabled) text = "Off: Martlet on your other computers can't find this PC or ask to use its hosts.";
        else if (nearbyHosts.Count == 0)
            text = "On. Nothing to share yet: other computers find this PC once it runs a host or reaches one over SSH.";
        else if (nearbyProblem is not null) text = $"On, but {nearbyProblem}, so other computers can't find this PC.";
        else if (nearbyFirewall is null) text = "On. Checking Windows Firewall...";
        else if (nearbyFirewall == false)
            text = $"On, but Windows Firewall doesn't let your other computers reach port {Nearby.Port} yet, so only Martlet on this PC can find it.";
        else if (nearbyCategory == "Public") text = "On, but Windows treats this network as Public, so your other computers can't find this PC.";
        else
            text = $"On. Martlet on your other computers can find this PC and ask to use {string.Join(", ", nearbyHosts.Select(h => h.HostId))}; " +
                "you allow each one here after comparing a check number.";
        NearbyShareStatusText.Text = nearbyLast is null ? text : text + " Last request: " + nearbyLast;
        NearbyFirewallButton.Visibility = NearbyBlocked ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>A request that passed the key agreement: ask the owner, then share. Runs on the UI thread; the request stays
    /// open until this finishes.</summary>
    private async Task HandleJoinAsync(JoinSession session)
    {
        var hosts = nearbyHosts;
        var name = session.Name;
        if (closing || hosts.Count == 0)
        {
            await TryAsync(() => session.FailAsync("It has no host it can share right now.", lifetime.Token));
            return;
        }
        if (joinPrompt is not null || joinRunning)
        {
            await TryAsync(() => session.FailAsync("It is already answering another computer. Try again in a minute.", lifetime.Token));
            return;
        }
        ErrorLog.Info($"Nearby: {name} ({session.From}, {session.Device}) asks to use {string.Join(", ", hosts.Select(h => h.HostId))}.");
        var prompt = new JoinRequestWindow(name, session.From.ToString(), session.Number, hosts.Select(h => h.HostId).ToArray());
        joinPrompt = prompt;
        bool allowed;
        try { allowed = await prompt.AskAsync(session.Incoming, Nearby.DecisionTimeout); }
        finally { joinPrompt = null; }
        if (!allowed || closing)
        {
            var withdrawn = session.Incoming.IsCompleted;
            if (!withdrawn) await TryAsync(() => session.DenyAsync(lifetime.Token));
            nearbyLast = withdrawn ? $"{name} stopped asking at {DateTime.Now:t}." : $"denied {name} at {DateTime.Now:t}.";
            ErrorLog.Info($"Nearby: {(withdrawn ? $"{name} stopped asking." : $"denied {name}.")}");
            ShowNearbyStatus();
            return;
        }
        joinRunning = true;
        try
        {
            await session.ApproveAsync(lifetime.Token);
            var summary = await HostRunWindow.RunAsync(this, $"Let {name} use your hosts", run => ShareHostsAsync(run, session, hosts));
            nearbyLast = summary is null ? $"sharing with {name} stopped at {DateTime.Now:t}; the run window shows why." : $"{summary} ({DateTime.Now:t})";
            if (!closing) ActionText.Text = summary ?? $"Sharing hosts with {name} stopped. The run window shows why.";
        }
        catch (Exception error) when (error is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
        {
            nearbyLast = $"{name} stopped answering at {DateTime.Now:t}.";
        }
        finally
        {
            joinRunning = false;
            if (!closing) ShowNearbyStatus();
        }
    }

    /// <summary>Asks every shareable host for a one-use pairing code (as Show a pairing code does), sends the codes to the
    /// asking computer, waits for it to pair and withdraws any code it did not use. Codes never reach the run output or log.</summary>
    private async Task<string> ShareHostsAsync(HostRunWindow run, JoinSession session, IReadOnlyList<ShareableHost> hosts)
    {
        var name = session.Name;
        var withdraw = hosts.ToDictionary(h => h.HostId, _ => CancellationTokenSource.CreateLinkedTokenSource(run.Token));
        var shown = hosts.ToDictionary(h => h.HostId, _ => new TaskCompletionSource<SharedCode?>(TaskCreationOptions.RunContinuationsAsynchronously));
        var problems = new ConcurrentQueue<string>();
        var note = $"(sent to {name}; never shown)";
        run.Status($"Asking {string.Join(", ", hosts.Select(h => h.HostId))} for a one-use pairing code for {name}...");
        var engines = hosts.Select(host => MintAsync(host)).ToArray();

        async Task MintAsync(ShareableHost host)
        {
            var slot = shown[host.HostId];
            var token = withdraw[host.HostId].Token;
            try
            {
                void Shown(string address, string code) => slot.TrySetResult(new(host.HostId, address, code));
                int exit;
                if (host.Target.Method == HostSetupMethod.ThisPcDocker)
                {
                    await HostLocal.EnsureDockerAsync(run, ContinueSetupKind.Docker);
                    await HostLocal.EnsureImageAsync(host.Target, run.Status, run.Output, token);
                    exit = await HostLocal.PairOtherAsync(host.Target, Shown, run.Output, token, note);
                }
                else
                {
                    var remote = new HostRemote(new HostShell(store!.DataDirectory, run.Prompts));
                    var ssh = HostShellTarget.Parse(host.Target.SshTarget);
                    run.Output.Report($"Connecting to {ssh} for {host.HostId}...");
                    var (probe, hostKey) = await remote.ProbeAsync(ssh, host.SshHostKey, token);
                    exit = await remote.PairOtherAsync(host.Target, HostRemote.NeedsSudo(host.Target.Method, probe), hostKey, Shown, run.Output, token, note);
                }
                if (!slot.Task.IsCompleted) problems.Enqueue($"{host.HostId} did not show a pairing code (exit {exit}).");
                else if (exit != 0 && !token.IsCancellationRequested) run.Output.Report($"{host.HostId}: pairing ended with exit {exit}.");
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                if (!slot.Task.IsCompleted) problems.Enqueue($"{host.HostId}: {error.Message}");
                run.Output.Report($"{host.HostId}: {error.Message}");
            }
            finally { slot.TrySetResult(null); }
        }

        async Task WithdrawAsync(IEnumerable<string> ids)
        {
            foreach (var id in ids) withdraw[id].Cancel();
            await Task.WhenAll(engines);
        }

        try
        {
            SharedCode[] codes;
            try
            {
                codes = (await Task.WhenAll(shown.Values.Select(s => s.Task)).WaitAsync(run.Token)).OfType<SharedCode>().ToArray();
                if (codes.Length == 0)
                    throw new InvalidOperationException("No host gave a pairing code. " + string.Join(" ", problems));
                await session.SendCodesAsync(codes, problems.ToArray(), run.Token);
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                await TryAsync(() => session.FailAsync(error is OperationCanceledException
                    ? "The owner canceled sharing." : error.Message, CancellationToken.None));
                await WithdrawAsync(withdraw.Keys);
                throw;
            }
            run.Status($"Sent {name} a one-use code for {string.Join(", ", codes.Select(c => c.HostId))}. Waiting for it to pair...");
            var paired = await session.ReadDoneAsync(Nearby.CodeLifetime, run.Token) ?? [];
            // A redeemed code ends its host's pairing run by itself (the gateway comes back); withdraw the others.
            var unused = codes.Select(c => c.HostId).Where(id => !paired.Contains(id)).ToArray();
            if (unused.Length > 0) run.Output.Report($"Withdrawing the codes {name} didn't use: {string.Join(", ", unused)}.");
            foreach (var id in unused) withdraw[id].Cancel();
            try { await Task.WhenAll(engines).WaitAsync(TimeSpan.FromMinutes(1), run.Token); }
            catch (TimeoutException) { await WithdrawAsync(withdraw.Keys); }
            var done = codes.Select(c => c.HostId).Where(paired.Contains).ToArray();
            if (done.Length == 0) throw new InvalidOperationException($"{name} did not pair with any host. " + string.Join(" ", problems));
            ErrorLog.Info($"Nearby: {name} paired with {string.Join(", ", done)}.");
            return $"{name} is paired with {string.Join(", ", done)}." + (problems.IsEmpty ? "" : " " + string.Join(" ", problems));
        }
        finally
        {
            foreach (var source in withdraw.Values) source.Dispose();
        }
    }

    private static async Task TryAsync(Func<Task> send)
    {
        try { await send(); }
        catch (Exception error) when (error is IOException or SocketException or ObjectDisposedException or OperationCanceledException or
            System.Text.Json.JsonException) { }
    }
}
