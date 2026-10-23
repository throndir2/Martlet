using System.IO;
using System.Net;
using System.Net.Http;
using Martlet.Core.Hardware;
using Martlet.HostClient;

namespace Martlet.Desktop;

/// <summary>Finds hosts whose computer was reinstalled and sets them up again by themselves (docs/NETWORK.md, "When a host
/// is reinstalled"). While a host answers with its pinned key, this PC saves what recognizes its computer later: its TPM keys,
/// its hardware facts (over SSH) and its roles. When it comes back with a new SSH or TLS key, the computer must prove it is
/// the same one (<see cref="ReinstallCheck"/>) before Martlet pairs it, pins its new keys and puts its roles back.</summary>
public partial class MainWindow
{
    private readonly Dictionary<string, DateTimeOffset> reinstallChecked = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> hostGoneSince = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> reinstallTried = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> factsTried = new(StringComparer.Ordinal);
    private bool reinstallBusy;

    /// <summary>Checks each of your hosts at most every <see cref="HostReinstall.CheckEvery"/> (after a network sync).</summary>
    private async Task CheckReinstalledHostsAsync()
    {
        if (reinstallBusy || closing || store is null || setupService is null) return;
        reinstallBusy = true;
        try
        {
            foreach (var host in homeHosts.Where(h => !h.Shared && h.Method != HostSetupMethod.ThisPcDocker).ToList())
            {
                if (closing) return;
                var now = DateTimeOffset.Now;
                if (reinstallChecked.TryGetValue(host.HostId, out var at) && now - at < HostReinstall.CheckEvery) continue;
                reinstallChecked[host.HostId] = now;
                try { await CheckReinstalledAsync(host, lifetime.Token); }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { return; }
                catch (Exception error) when (error is InvalidOperationException or IOException or HostShellException or
                    UnauthorizedAccessException or InvalidDataException or HttpRequestException or OperationCanceledException)
                {
                    ErrorLog.Warn($"Reinstall check of {host.HostId}: {error.Message}");
                }
            }
        }
        finally { reinstallBusy = false; }
    }

    private async Task CheckReinstalledAsync(PairedHost host, CancellationToken token)
    {
        var id = host.HostId;
        var now = DateTimeOffset.Now;
        var probe = await HostRoutes.ProbeAsync(host.Pairing.Origin, host.Pairing.SpkiFingerprint, host.MovedTo, TimeSpan.FromSeconds(5), token);
        if (probe.Reachable)
        {
            hostGoneSince.Remove(id);
            await LearnHostAsync(host, token);
            return;
        }
        if (!hostGoneSince.TryGetValue(id, out var since)) hostGoneSince[id] = since = now;
        var otherKey = probe.Problem == "another key";
        if (!otherKey && now - since < HostReinstall.GoneFor) return;
        if (reinstallTried.TryGetValue(id, out var tried) && now - tried < HostReinstall.RetryEvery) return;
        if (host.SshTarget is { } sshText && host.Method is HostSetupMethod.SshDocker or HostSetupMethod.SshNative)
        {
            // A reinstalled computer has a new SSH key. The same key means the host is only down (or turned off). Only a computer
            // this PC reaches on its home subnet or over Tailscale is looked at (not, say, the same address on a café's network).
            var ssh = HostShellTarget.Parse(sshText);
            if (await HostReinstall.DialedAddressAsync(ssh.Host, token) is not { } dialed ||
                RouteTrusts.Of(dialed) is var route && route == RouteTrust.Other) return;
            // Another network numbered like home (a café, a friend's house): another network card answers at the address.
            var (before, _) = HostReinstall.BeforePassword(route, host.Recognition?.Facts,
                route == RouteTrust.Home ? HostReinstall.MacAt(id, dialed) : null);
            if (before == "Stop") return;
            // Without a card to compare, the owner is asked to trust the new key: at most once a day.
            if (before == "Ask" && reinstallTried.TryGetValue(id, out var asked) && now - asked < TimeSpan.FromDays(1)) return;
            var watch = new HostReinstall.Watch();
            var shell = new HostShell(store!.DataDirectory, watch) { Provisional = true };
            if ((host.SshHostKey ?? shell.PinnedHostKey(ssh)) is null) return;
            try { await shell.RunAsync(ssh, new() { Command = "true", PinnedHostKey = host.SshHostKey }, null, token); }
            catch (Exception error) when (error is HostShellException || error is OperationCanceledException && !token.IsCancellationRequested) { }
            if (watch.OfferedKey is not { } offered) return;
            reinstallTried[id] = now;
            ErrorLog.Info($"Martlet network: {id}'s computer ({ssh}) shows a new SSH key, so it was reinstalled. Setting it up again.");
            await RecoverHostAsync(host, offered);
            return;
        }
        if (!otherKey) return;
        // A host Martlet doesn't reach over SSH: it can tell whether it is the same computer, but it can't set it up.
        reinstallTried[id] = now;
        var answer = await HostComputerProof.AskAsync(host.Pairing.Origin, host.MovedTo, TimeSpan.FromSeconds(15), token);
        var check = answer.Proof is null ? (HardwareCheck?)null : HostComputerProof.Check(answer, id, host.Recognition?.Keys);
        var text = check == HardwareCheck.Proven
            ? $"{id} was reinstalled. It proved it is the same computer, so set it up there again and pair it in Devices › Add a computer: its jobs come back."
            : $"{id} answers with another key and can't prove it is the same computer ({answer.Problem ?? check?.ToString()}). " +
              "If you reinstalled it, set it up there again and pair it in Devices › Add a computer.";
        Reinstalled(id, text);
    }

    /// <summary>Saves what recognizes a reachable host's computer later: its TPM keys (proved over its pinned TLS key), its
    /// roles and, for an SSH host, its hardware facts.</summary>
    private async Task LearnHostAsync(PairedHost host, CancellationToken token)
    {
        var id = host.HostId;
        var pairings = Pairings();
        var saved = host.Recognition ?? new();
        var now = DateTimeOffset.Now;
        if (hostChecks.GetValueOrDefault(id) is { Reachable: true, Offers: { Count: > 0 } offers } &&
            !offers.Keys.Order().SequenceEqual(saved.Roles.Order()))
            saved = pairings.KeepRecognition(id, r => r with { Roles = [.. offers.Keys.Order()] })?.Recognition ?? saved;
        if (saved.KeysCheckedAt is not { } keysAt || now - keysAt > HostReinstall.KeysEvery)
        {
            var answer = await HostComputerProof.AskAsync(host.Pairing.Origin, host.MovedTo, TimeSpan.FromSeconds(15), token);
            if (answer.Spki == host.Pairing.SpkiFingerprint)
            {
                var keys = HostComputerProof.Keys(answer, id);
                var noKey = keys.Count > 0 ? null : answer.Proof?.Unavailable ?? answer.Problem ?? "The host showed no TPM key.";
                // Only keys found count as checked: a host without them (an older Martlet, no TPM access yet) is asked again soon.
                saved = pairings.KeepRecognition(id, r => r with
                {
                    Keys = keys.Count > 0 ? keys : r.Keys, NoKey = noKey, KeysCheckedAt = keys.Count > 0 ? now : r.KeysCheckedAt
                })?.Recognition ?? saved;
                if (keys.Count > 0 && !keys.SequenceEqual(host.Recognition?.Keys ?? []))
                    ErrorLog.Info($"Martlet network: saved {id}'s TPM key ({string.Join(", ", keys.Select(k => k.Kind))}), so a reinstall of its computer is recognized.");
                else if (noKey is not null && host.Recognition?.NoKey != noKey)
                    ErrorLog.Info($"Martlet network: {id} has no TPM key Martlet can use ({noKey}). Martlet recognizes it after a reinstall by its hardware facts instead.");
            }
        }
        if (host.SshTarget is { } sshText && host.Method is HostSetupMethod.SshDocker or HostSetupMethod.SshNative &&
            (saved.FactsCheckedAt is not { } factsAt || now - factsAt > HostReinstall.FactsEvery) &&
            !(factsTried.TryGetValue(id, out var factsTriedAt) && now - factsTriedAt < HostReinstall.RetryEvery))
        {
            factsTried[id] = now;
            var ssh = HostShellTarget.Parse(sshText);
            var text = new System.Text.StringBuilder();
            var result = await new HostShell(store!.DataDirectory, NoHostShellPrompts.Instance).RunAsync(ssh,
                new() { Command = HostReinstall.FactsCommand, PinnedHostKey = host.SshHostKey, Sudo = true },
                new LineSink(line => { lock (text) text.AppendLine(line); }), token);
            var facts = MachineFacts.Hashes(id, text.ToString());
            if (result.ExitCode == 0 && facts.Count > 0)
            {
                pairings.KeepRecognition(id, r => r with { Facts = facts, FactsCheckedAt = now });
                if (saved.Facts.Count == 0)
                    ErrorLog.Info($"Martlet network: saved {facts.Count} hardware facts of {id} (hashes only), so a reinstall of its computer is recognized.");
            }
        }
    }

    /// <summary>Sets a reinstalled SSH host up again in Background tasks, without asking (<paramref name="offered"/>: the new
    /// SSH key Martlet saw).</summary>
    private async Task RecoverHostAsync(PairedHost host, string offered)
    {
        string? summary;
        try
        {
            summary = await HostRunWindow.RunAsync(this, $"Set up {host.HostId} again after a reinstall",
                run => RecoverOverSshAsync(run, host, offered), join: true, hidden: true);
        }
        catch (Exception error) when (error is InvalidOperationException or OperationCanceledException) { summary = null; }
        Reinstalled(host.HostId, summary ?? $"{host.HostId} was reinstalled, but Martlet couldn't set it up again by itself. " +
            "Background tasks shows why; choose Reset SSH trust for it and set it up again in Devices › Add a computer.");
        if (summary is not null) QueueNetworkSync();
    }

    private void Reinstalled(string id, string text)
    {
        ErrorLog.Info("Martlet network: " + text);
        try { Pairings().KeepRecognition(id, r => r with { LastReinstall = text, LastReinstallAt = DateTimeOffset.Now }); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException) { }
        tray?.ShowNotice("A host was reinstalled", text);
    }

    private async Task<string> RecoverOverSshAsync(HostRunWindow run, PairedHost host, string offered)
    {
        var token = run.Token;
        var directory = store!.DataDirectory;
        var id = host.HostId;
        var ssh = HostShellTarget.Parse(host.SshTarget!);
        var saved = host.Recognition ?? new();

        // Where this PC itself connects decides the route, never an address the computer reports. A password goes only to a
        // computer on your home subnet whose network card Martlet saw before, or over Tailscale; otherwise the owner first
        // trusts its new SSH key (the window shows its fingerprint), or Martlet stops.
        var dialed = await HostReinstall.DialedAddressAsync(ssh.Host, token);
        var route = dialed is null ? RouteTrust.Other : RouteTrusts.Of(dialed);
        var mac = route == RouteTrust.Home && dialed is not null ? HostReinstall.MacAt(id, dialed) : null;
        var (before, beforeWhy) = HostReinstall.BeforePassword(route, saved.Facts, mac);
        if (before == "Stop") throw new InvalidOperationException($"Martlet didn't sign in to {ssh}: {beforeWhy}.");
        run.Output.Report(before == "Go" ? $"{ssh} has a new SSH key ({offered}); {beforeWhy}, so Martlet signs in."
            : $"{ssh} has a new SSH key ({offered}). {beforeWhy}, so trust the key only if you reinstalled {id}.");
        var prompts = new HostReinstall.Prompts(HostShell.RememberedLogin(ssh), run.Prompts, offered, approved: before == "Go");
        var shell = new HostShell(directory, prompts) { Provisional = true };
        var remote = new HostRemote(shell);

        run.Status($"Connecting to {ssh}...");
        HostProbe probe;
        string key;
        try { (probe, key) = await remote.ProbeAsync(ssh, host.SshHostKey, token, run.Output); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new InvalidOperationException($"Martlet couldn't sign in to {ssh} (its new key wasn't trusted or no password was " +
                "entered), so it stopped. It asks again in an hour; tick Remember then, so Martlet does this by itself next time.");
        }
        if (key == host.SshHostKey)
            throw new InvalidOperationException($"{ssh} still has the SSH key Martlet saved, so it wasn't reinstalled.");
        run.Output.Report($"Martlet uses {ssh}'s new SSH key for this run only, until the computer proves it is {id}.");

        run.Status($"Reading {ssh}'s hardware facts...");
        var text = new System.Text.StringBuilder();
        await shell.RunAsync(ssh, new() { Command = HostReinstall.FactsCommand, PinnedHostKey = key, Sudo = true },
            new LineSink(line => { lock (text) text.AppendLine(line); }), token);
        var facts = MachineFacts.Hashes(id, text.ToString());
        // The address the gateway listens on: the one this PC dials when it is a home address, else the one the computer has.
        var address = dialed is not null && HostSetupCommands.IsPrivate(dialed.ToString()) ? dialed.ToString() : probe.Address ?? host.Address;
        run.Output.Report($"{ssh}: {facts.Count} hardware facts, reached {(route switch { RouteTrust.Home => "on your home network", RouteTrust.Tailnet => "over Tailscale", _ => "from outside your network" })}.");
        // Stop before setup when it is clearly another computer, or when there is no TPM key to check later and the facts don't show it.
        var early = ReinstallCheck.Decide(new(null, saved.Facts, facts, route, mac));
        if (early.Verdict == ReinstallVerdict.NotSame || saved.Keys.Count == 0 && early.Verdict != ReinstallVerdict.Same)
            throw new InvalidOperationException($"Martlet didn't set up {ssh} again: {early.Why}. If it is {id}, choose Reset SSH trust for it and set it up again in Devices › Add a computer.");

        // The way it ran before, unless the new system lacks what that needs (a fresh install without Docker runs natively).
        var method = HostRemote.Blocker(host.Method, probe, ssh.ToString()) is null ? host.Method : HostRemote.Choose(probe);
        if (HostRemote.Blocker(method, probe, ssh.ToString()) is { } blocker) throw new InvalidOperationException(blocker);
        var target = host.Target(Version) with { Method = method, Address = HostSetupCommands.IsPrivate(address) ? address : host.Address };
        var sudo = HostRemote.NeedsSudo(method, probe);
        var supplied = await remote.SupplyIfOfflineAsync(target, HostVerb.Setup, directory, key, run.Output, token);
        run.Status($"Setting up {id} on {ssh} again (this can take a few minutes)...");
        var setup = await remote.RunAsync(target, "setup", true, sudo, null, key, run.Output, token, supplied: supplied);
        if (setup.ExitCode != 0) throw new InvalidOperationException($"Setup stopped on {ssh} (exit {setup.ExitCode}); the output shows why.");

        run.Status($"Checking that {ssh} is {id}...");
        var origin = $"https://{target.Address}:{new Uri(host.Pairing.Origin).Port}";
        // Over Tailscale the home origin may not be reachable from here: dial the address this PC reaches it at instead.
        var through = dialed is not null && dialed.ToString() != target.Address ? $"{dialed}:{new Uri(host.Pairing.Origin).Port}" : null;
        HostProofAnswer answer = new(null, null, "", "not asked");
        for (var attempt = 0; attempt < 6; attempt++)
        {
            answer = await HostComputerProof.AskAsync(origin, attempt % 2 == 1 ? through : null, TimeSpan.FromSeconds(15), token);
            if (answer.Proof is not null) break;
            await Task.Delay(TimeSpan.FromSeconds(5), token);
        }
        var check = answer.Proof is null ? (HardwareCheck?)null : HostComputerProof.Check(answer, id, saved.Keys);
        var (verdict, why) = ReinstallCheck.Decide(new(check, saved.Facts, facts, route, mac));
        run.Output.Report($"{(verdict == ReinstallVerdict.Same ? "It is" : "It isn't shown to be")} {id}: {why}.");
        if (verdict != ReinstallVerdict.Same)
            throw new InvalidOperationException($"Martlet set up {ssh} but didn't pair it or give it {id}'s jobs: {why}. " +
                $"If it is {id}, pair it in Devices › Add a computer.");

        run.Status($"Pairing this PC with {id} again...");
        var (pairing, secret, _) = await remote.PairAsync(target, NetworkIdentity.DeviceId(homeHosts), Martlet.Core.Network.ThisComputer.Name,
            sudo, key, run.Output, token);
        if (answer.Spki is { } proved && pairing.SpkiFingerprint != proved)
            throw new InvalidOperationException($"{ssh} paired with another TLS key than the one it proved, so Martlet didn't keep the pairing.");
        await HostsWindow.KeepPairingAsync(Pairings(), pairing, secret, method, ssh.ToString(), key, token);
        shell.PinKey(ssh, key);
        var now = DateTimeOffset.Now;
        var keys = HostComputerProof.Keys(answer, id);
        Pairings().KeepRecognition(id, r => r with
        {
            Keys = keys.Count > 0 ? keys : r.Keys, NoKey = keys.Count > 0 ? null : answer.Proof?.Unavailable, KeysCheckedAt = now,
            Facts = facts.Count > 0 ? facts : r.Facts, FactsCheckedAt = facts.Count > 0 ? now : r.FactsCheckedAt
        });

        var back = new List<string>();
        var skipped = new List<string>();
        foreach (var role in saved.Roles)
        {
            run.Status($"Putting {role} back on {id}...");
            try
            {
                var inputs = await remote.DescribeAsync(target, role, sudo, key, run.Output, token);
                if (inputs.Secrets.Any(s => !s.Stored)) { skipped.Add(role + " (it needs a key or token)"); continue; }
                var added = await remote.RunAsync(target, "add " + role, false, sudo, new Dictionary<string, string>(), key,
                    new EngineOutput(run.Output), token);
                if (added.ExitCode == 0) back.Add(role);
                else skipped.Add(role + $" (exit {added.ExitCode})");
            }
            catch (InvalidOperationException error)
            {
                skipped.Add(role);
                run.Output.Report(error.Message);
            }
        }
        return $"{id} was reinstalled and proved it is the same computer ({why}). Martlet set it up again and paired it" +
            (back.Count > 0 ? $", and put back {HostRoles.Names(back)}" : "") +
            (skipped.Count > 0 ? $". Add these again in Devices: {string.Join(", ", skipped)}." : ".");
    }
}
