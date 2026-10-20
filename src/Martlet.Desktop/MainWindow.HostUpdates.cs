using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Martlet.HostClient;
using Martlet.Core.Installation;
using Martlet.Core.Nodes;

namespace Martlet.Desktop;

/// <summary>Host updates: bringing every paired host that runs an older Martlet up to this PC's version, and tracking which
/// hosts this Martlet updates by any route.</summary>
public partial class MainWindow
{
    /// <summary>The automatic host pass, a command from another computer updating this PC's host service, or keeping it
    /// current: one of them at a time.</summary>
    private bool hostUpdatesRunning;
    private string? thisPcHostVersion;
    /// <summary>Which hosts this Martlet is updating by any route (run windows included), the notes their Devices cards show
    /// and the hosts waiting for a retry because they were busy with another change (or their gateway was restarting).
    /// They are tried again from <see cref="hostRetryAt"/>.</summary>
    private readonly HostUpdateTracker hostUpdates = new();
    /// <summary>host@version pairs already updated automatically this session, so a failing host is not retried every check.</summary>
    private readonly HashSet<string> hostUpdateAttempts = new(StringComparer.Ordinal);
    private DateTimeOffset? hostRetryAt;
    private static readonly TimeSpan HostRetryDelay = TimeSpan.FromMinutes(3);
    private const string ThisPcHostId = HostUpdateTracker.ThisPc;

    /// <summary>The update key of a paired host: this PC's own host service when Martlet runs it on this PC's Docker Desktop.</summary>
    private static string UpdateKey(PairedHost host) => HostUpdateTracker.Key(host.HostId, host.Method == HostSetupMethod.ThisPcDocker);

    /// <summary>This PC's own pairings with its host service (their Devices notes describe that host service).</summary>
    private static IReadOnlyList<string> ThisPcHostIds(IEnumerable<PairedHost> hosts) =>
        hosts.Where(h => h.Method == HostSetupMethod.ThisPcDocker).Select(h => h.HostId).ToList();

    /// <summary>A host runs this PC's release (<paramref name="version"/>, default this PC's), updated by any route or found so by
    /// a check (<paramref name="seen"/>): its retry goes and a note about an earlier try says it is updated; true when anything
    /// changed.</summary>
    private bool HostUpdateSettled(string key, string? version = null, bool seen = false)
    {
        var changed = hostUpdates.Current(key, version ?? Version, DateTime.Now, seen, ThisPcHostIds(homeHosts));
        if (hostUpdates.Waiting.Count == 0) hostRetryAt = null;
        if (key == ThisPcHostId) OwnHostFoundCurrent(version ?? Version);
        return changed;
    }

    /// <summary>A check found a host running this PC's version: unless a route is updating it right now, a stale "waiting to
    /// update" note says it is updated and its retry goes (and the Devices map shows it).</summary>
    private void HostFoundCurrent(string key, string? version)
    {
        if (version is null || AppVersions.IsOlder(version, Version) || hostUpdates.IsUpdating(key)) return;
        if (HostUpdateSettled(key, version, seen: true) && DevicesPage.IsVisible) RenderMap();
    }

    /// <summary>For an Update run from another window (Manage host): marks that host as being updated, so Martlet's automatic
    /// pass leaves it to that run, until the result is disposed. Null when Martlet's main window isn't up.</summary>
    internal static IDisposable? BeginHostUpdateElsewhere(string hostId, bool onThisPc) =>
        Application.Current?.MainWindow is MainWindow main ? main.hostUpdates.Begin(HostUpdateTracker.Key(hostId, onThisPc)) : null;

    /// <summary>An Update run from another window finished: that host's retry and stale note go away.</summary>
    internal static void HostUpdatedElsewhere(string hostId, bool onThisPc)
    {
        if (Application.Current?.MainWindow is MainWindow main) main.HostUpdateSettled(HostUpdateTracker.Key(hostId, onThisPc));
    }

    private enum HostUpdateResult { Updated, Failed, Busy }

    private void UpdateHosts_Click(object sender, RoutedEventArgs e) => UpdateHostsAsync(automatic: false).Forget();

    /// <summary>Brings every paired host that runs an older Martlet (or does not report its version) to this PC's version,
    /// side by side (each host is its own computer) and without a console window. A host that would need a password, sudo or an approval there keeps an
    /// Update host command on its Devices card, which runs the same update in a run window. A host this Martlet is already
    /// updating by another route (an Update host run window, a command from another computer) is left to that run. A host
    /// busy with another change (an install, another computer's update, its console) is not interrupted: nothing changes
    /// there; an automatic pass stops at once and tries it again every few minutes until it is free
    /// (<paramref name="retrying"/> runs only those), while Update hosts now (not <paramref name="automatic"/>) waits up to
    /// <see cref="HostUpdateTracker.AskedLockWaitSeconds"/> for that change to finish first.</summary>
    private async Task UpdateHostsAsync(bool automatic, bool retrying = false)
    {
        if (store is null || closing) return;
        if (hostUpdatesRunning)
        {
            if (retrying) hostRetryAt = DateTimeOffset.UtcNow + HostRetryDelay;
            else if (!automatic) UpdateStatusText.Text = "A host update is already running, and its result shows here and on each host's Devices card.";
            return;
        }
        hostUpdatesRunning = true;
        UpdateHostsButton.IsEnabled = false;
        int updated = 0, failed = 0, current = 0, unreachable = 0, asked = 0, busy = 0, already = 0;
        var waited = hostUpdates.TakeWaiting();
        var only = retrying ? waited.ToHashSet(StringComparer.Ordinal) : null;
        var paired = NetworkMap.Hosts(Inputs());
        var hosts = paired.Where(h => only is null || only.Contains(h.HostId)).ToList();
        var thisPcIds = ThisPcHostIds(paired);

        void Record(string id, string attempt, HostUpdateResult result)
        {
            switch (result)
            {
                case HostUpdateResult.Updated: updated++; break;
                case HostUpdateResult.Busy:
                    busy++;
                    hostUpdates.Wait(id);
                    hostUpdateAttempts.Remove(attempt);
                    break;
                default: failed++; break;
            }
        }

        try
        {
            if (!automatic) UpdateStatusText.Text = hosts.Count == 0 && Role != DeviceRole.Host
                ? "No host is paired yet." : "Checking host versions...";
            // Hosts are separate computers: each is checked and updated side by side, so a slow one never holds up the rest.
            async Task UpdateOneAsync(PairedHost host)
            {
                if (closing) return;
                var key = UpdateKey(host);
                var check = await HostControl.CheckAsync(host.Pairing, HardwareStore, lifetime.Token);
                hostChecks[host.HostId] = check;
                if (check.Reachable != true) { unreachable++; return; }
                // A computer with a Thinking model joins the Thinking pool by itself, also while Keep in sync is off.
                NoteThinkingPoolHost(host.HostId);
                if (!AppVersions.IsOlder(check.MartletVersion, Version))
                {
                    current++;
                    if (!hostUpdates.IsUpdating(key)) hostUpdates.Current(key, check.MartletVersion ?? Version, DateTime.Now, seen: true, thisPcIds);
                    if (key == ThisPcHostId) OwnHostFoundCurrent(check.MartletVersion ?? Version);
                    return;
                }
                // Another route of this Martlet updates it right now; a second run would only find the host locked by it.
                if (hostUpdates.IsUpdating(key)) { already++; return; }
                var attempt = host.HostId + "@" + Version;
                if (automatic && !hostUpdateAttempts.Add(attempt)) return;
                if (host.Method == HostSetupMethod.Agent)
                {
                    HostUpdateResult result;
                    using (hostUpdates.Begin(key)) result = await AskHostToUpdateAsync(host);
                    if (result == HostUpdateResult.Updated) asked++;
                    else Record(host.HostId, attempt, result);
                    return;
                }
                if (!host.CanLaunch)
                {
                    hostUpdates.Note(host.HostId, "This host needs an update, so set how Martlet signs in over SSH or choose Through Martlet on that computer.");
                    failed++;
                    return;
                }
                HostUpdateResult outcome;
                using (hostUpdates.Begin(key)) outcome = await UpdateHostQuietlyAsync(host.HostId, host.Target(Version), host.SshHostKey, asked: !automatic);
                Record(host.HostId, attempt, outcome);
                if (outcome == HostUpdateResult.Updated && key == ThisPcHostId) OwnHostFoundCurrent(Version);
                if (outcome == HostUpdateResult.Updated) hostChecks[host.HostId] = await HostControl.CheckAsync(host.Pairing, HardwareStore, lifetime.Token);
            }

            // This PC's own host service when this PC isn't paired with it (otherwise the hosts above already cover it).
            async Task UpdateThisPcAsync()
            {
                if (Role != DeviceRole.Host || closing || thisPcIds.Count > 0 || only is not null && !only.Contains(ThisPcHostId)) return;
                thisPcHostVersion = await HostSetupCommands.ThisPcGatewayVersionAsync(lifetime.Token);
                var attempt = ThisPcHostId + "@" + Version;
                var service = thisPcHostVersion;
                if (service is not null && !AppVersions.IsOlder(service, Version))
                {
                    current++;
                    if (!hostUpdates.IsUpdating(ThisPcHostId)) hostUpdates.Current(ThisPcHostId, service, DateTime.Now, seen: true);
                    OwnHostFoundCurrent(service);
                }
                else if (service is not null && hostUpdates.IsUpdating(ThisPcHostId)) already++;
                else if (service is not null && (!automatic || hostUpdateAttempts.Add(attempt)))
                {
                    HostUpdateResult outcome;
                    using (hostUpdates.Begin(ThisPcHostId)) outcome = await UpdateHostQuietlyAsync(ThisPcHostId, ThisPcTarget(), asked: !automatic);
                    Record(ThisPcHostId, attempt, outcome);
                    if (outcome == HostUpdateResult.Updated)
                    {
                        thisPcHostVersion = await HostSetupCommands.ThisPcGatewayVersionAsync(lifetime.Token);
                        if (thisPcHostVersion is { } now) OwnHostFoundCurrent(now);
                    }
                }
            }

            await Task.WhenAll(hosts.Select(UpdateOneAsync).Append(UpdateThisPcAsync()));
            if (closing) return;
        }
        catch (OperationCanceledException) { return; }
        finally
        {
            hostUpdatesRunning = false;
            hostRetryAt = hostUpdates.Waiting.Count > 0 && !closing ? DateTimeOffset.UtcNow + HostRetryDelay : null;
            if (!closing)
            {
                UpdateHostsButton.IsEnabled = true;
                RenderHome();
                RenderDevicePages();
            }
        }
        if (closing || automatic && updated + failed + asked + busy == 0) return;
        if (!automatic && hosts.Count == 0 && Role != DeviceRole.Host) return;
        var summary = $"Host updates: {updated} updated to Martlet {Version}, {current} already current" +
            (asked > 0 ? $", {asked} asked to update through Martlet there" : "") +
            (already > 0 ? $", {already} already being updated by Martlet (its run window or a command shows the result)" : "") +
            (busy > 0 ? $", {busy} busy with another change (Martlet tries again at {hostRetryAt?.ToLocalTime():t})" : "") +
            (failed > 0 ? $", {failed} need you (see their cards on the Devices map)" : "") +
            (unreachable > 0 ? $", {unreachable} not reachable" : "") + ".";
        UpdateStatusText.Text = summary;
        if (automatic ? updated + failed + asked > 0 : failed > 0) ActionText.Text = summary;
    }

    /// <summary>Asks Martlet on a host reached through its paired connection to bring itself to this PC's version (app and host
    /// service). The command waits there until Martlet runs and has finished what it is doing; asking again while it waits
    /// changes nothing. <see cref="HostUpdateResult.Busy"/> when the host's gateway didn't answer, for example while it
    /// restarts for an update another computer started.</summary>
    private async Task<HostUpdateResult> AskHostToUpdateAsync(PairedHost host)
    {
        try
        {
            var (command, list) = await ClusterSync.WithConnectionAsync(host.Pairing, async connection =>
            {
                var sent = await connection.SendCommandAsync(NodeCommandKinds.Update, new Dictionary<string, string> { ["version"] = Version }, null,
                    lifetime.Token);
                HostCommandList? commands = null;
                if (sent.State == NodeCommandState.Queued)
                {
                    try { commands = await connection.ReadCommandsAsync(lifetime.Token); }
                    catch (HostConnectionException) { }
                }
                return (sent, commands);
            });
            hostUpdates.Note(host.HostId, command.State == NodeCommandState.Running
                ? $"Martlet on {host.HostId} is updating to {Version}."
                : list?.WaitingText(command, host.HostId) is { } behind
                    ? $"Asked Martlet on {host.HostId} to update to {Version} ({DateTime.Now:t}). {behind}"
                    : $"Asked Martlet on {host.HostId} to update to {Version} ({DateTime.Now:t}); it does when Martlet runs there.");
            return HostUpdateResult.Updated;
        }
        catch (HostConnectionException error) when (error.Code == "request.invalid")
        {
            hostUpdates.Note(host.HostId, $"Its host service is older than commands between computers, so open Martlet on {host.HostId} once " +
                "to bring it up to date (from then on this PC updates it from here).");
            return HostUpdateResult.Failed;
        }
        catch (Exception error) when (error is HostConnectionException { Code: "host.unreachable" or "gateway.internal" } ||
            error is OperationCanceledException && !lifetime.IsCancellationRequested)
        {
            hostUpdates.Note(host.HostId, $"{host.HostId}'s host service didn't answer (it may be restarting for an update), so " +
                "Martlet asks it again in a few minutes.");
            return HostUpdateResult.Busy;
        }
        catch (Exception error) when (ClusterSync.IsHostFailure(error))
        {
            hostUpdates.Note(host.HostId, $"Could not ask Martlet on {host.HostId} to update: {error.Message}");
            return HostUpdateResult.Failed;
        }
    }

    /// <summary>Runs martlet-host update there unattended, as a background task out of sight ("Update gpu-box to Martlet x.y.z",
    /// with the update's log as its output). An automatic run doesn't queue behind another change running on that host: the
    /// engine then stops at once without changing anything (<see cref="HostEngineBusy"/>), this returns
    /// <see cref="HostUpdateResult.Busy"/> so the caller tries again a few minutes later, and that try leaves Background
    /// tasks. One you <paramref name="asked"/> for (Update hosts now) waits up to
    /// <see cref="HostUpdateTracker.AskedLockWaitSeconds"/> for that change to finish and then updates. Cancel task stops it;
    /// Martlet doesn't try that version there again by itself, and the host's Devices card offers Update host.</summary>
    private async Task<HostUpdateResult> UpdateHostQuietlyAsync(string id, HostSetupTarget target, string? sshHostKey = null, bool asked = false)
    {
        var waiting = asked
            ? $"Updating to Martlet {Version} (if something else is changing that host, this waits for it to finish first)..."
            : $"Updating to Martlet {Version}...";
        hostUpdates.Note(id, waiting);
        RenderDevicePages();
        var result = HostUpdateResult.Failed;
        var name = id == ThisPcHostId ? "this PC's host service" : id;
        BackgroundTask? task = null;
        await HostRunWindow.RunAsync(this, $"Update {name} to Martlet {Version}", async run =>
        {
            task = run.BackgroundTask;
            run.Status(waiting);
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, run.Token);
            int code;
            string log;
            try
            {
                (code, log) = await HostSetupCommands.RunUnattendedAsync(target, HostAction.Update, cancellation.Token, store?.DataDirectory,
                    sshHostKey, asked ? HostUpdateTracker.AskedLockWaitSeconds : 0);
            }
            catch (OperationCanceledException) when (run.Token.IsCancellationRequested && !closing)
            {
                hostUpdates.Note(id, $"You canceled the update to Martlet {Version}, so press Update host to run it again.");
                // This PC's own host service: keeping it current doesn't start the same update again by itself either.
                if (target.Method == HostSetupMethod.ThisPcDocker) ownHost.Failed(Version);
                throw;
            }
            catch (Exception error) when (error is InvalidOperationException or IOException or UnauthorizedAccessException or Win32Exception)
            {
                hostUpdates.Note(id, $"Couldn't start the update ({error.Message.TrimEnd('.')}), so press Update host to run it from Martlet.");
                throw;
            }
            var lines = ReadLog(log);
            foreach (var line in lines.TakeLast(400)) run.Output.Report(line);
            if (HostEngineBusy.Read(code, lines) is { } what)
            {
                hostUpdates.Note(id, HostUpdateTracker.BusyNote(Version, what, DateTime.Now));
                result = HostUpdateResult.Busy;
                return $"{name} is busy with another change ({what}), so nothing was changed and Martlet tries again in a few minutes.";
            }
            if (code != 0)
            {
                hostUpdates.Note(id, "The update needs your attention on that computer, so press Update host to finish it.");
                throw new InvalidOperationException($"The update stopped on {name} (exit {code}), so press Update host on its Devices card to finish it.");
            }
            hostUpdates.Note(id, $"{HostUpdateTracker.UpdatedNote}{Version} at {DateTime.Now:t}.");
            result = HostUpdateResult.Updated;
            return $"{name} runs Martlet {Version}.";
        }, hidden: true);
        if (result == HostUpdateResult.Busy && task is not null) BackgroundTasks.Discard(task);
        return result;
    }

    private static string[] ReadLog(string path)
    {
        try { return File.ReadAllLines(path); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return []; }
    }
}
