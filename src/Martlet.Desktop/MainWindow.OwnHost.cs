using System.ComponentModel;
using System.IO;
using System.Windows;
using Martlet.Core.Nodes;

namespace Martlet.Desktop;

/// <summary>This PC's own host service (Docker Desktop) follows this app's version, whatever Update paired hosts
/// automatically says. Martlet updates itself first and is usable again as soon as it restarts; only then, in the
/// background, it brings its host service up to the same version (building its image and running martlet-host update),
/// and every minute after that it checks again until the host service runs this version (Docker Desktop started later, a
/// host busy with another change, a host service that was stopped). It doesn't start while Martlet replies or hears you,
/// leaves the host service to another route already updating it, and waits for this PC's own pending update.</summary>
public partial class MainWindow
{
    private const string OwnHostRun = "Keep this PC's host service current";
    private readonly OwnHostFollower ownHost = new(SimulatedOwnHost.Active ? TimeSpan.FromSeconds(30) : null);
    private Task? ownHostPass;
    /// <summary>What the last look at this PC's own host service found, and the version it reported.</summary>
    private (OwnHostStep Step, string? Version)? ownHostSeen;

    /// <summary>Whether this PC runs a host service of its own: it is a host PC, or paired with its host service on Docker
    /// Desktop here.</summary>
    private bool RunsOwnHostService => Role == DeviceRole.Host || OwnHostPairing() is not null;

    private PairedHost? OwnHostPairing() => NetworkMap.Hosts(Inputs()).FirstOrDefault(h => h.Method == HostSetupMethod.ThisPcDocker);

    /// <summary>Looks at this PC's own host service and updates it in the background when it runs an older Martlet; a call
    /// while one runs returns that one.</summary>
    private Task FollowOwnHostAsync() =>
        ownHostPass is { IsCompleted: false } running ? running : ownHostPass = FollowOwnHostOnceAsync();

    private async Task FollowOwnHostOnceAsync()
    {
        if (closing || exiting || store is null) return;
        if (!RunsOwnHostService)
        {
            OwnHostUpdateText.Visibility = Visibility.Collapsed;
            return;
        }
        if (OwnHostUpdateText.Text.Length > 0) OwnHostUpdateText.Visibility = Visibility.Visible;
        if (!ownHost.Due(Version, DateTimeOffset.UtcNow)) return;
        var reading = SimulatedOwnHost.Active ? SimulatedOwnHost.Read() : await HostSetupCommands.ThisPcGatewayAsync(lifetime.Token);
        if (closing || exiting) return;
        // Decided and claimed with no await in between, so no other route starts a second update of it meanwhile.
        var step = ownHost.Decide(Version, reading, hostUpdates.IsUpdating(ThisPcHostId), SelfUpdatePending || installOnExit is not null,
            conversation?.Replying == true || openConversation?.HearingYou == true, DateTimeOffset.UtcNow);
        if (step == OwnHostStep.Idle) return;
        var before = ownHostSeen;
        ownHostSeen = (step, reading?.Version);
        var reported = reading?.Version is { } found ? $"Martlet {found}" : "an older Martlet";
        switch (step)
        {
            case OwnHostStep.NotFound:
                ShowOwnHost("This PC's host service isn't running (Docker Desktop is stopped, or it isn't set up). When it runs, " +
                    $"Martlet brings it to Martlet {Version} by itself.");
                break;
            case OwnHostStep.Stopped:
                ShowOwnHost($"This PC's host service is stopped and runs {reported}. Martlet updates it to {Version} by itself " +
                    "once it runs again.");
                break;
            case OwnHostStep.Current:
                if (before?.Step != OwnHostStep.Current)
                    ErrorLog.Info($"This PC's host service runs Martlet {reading!.Version}, the same as this app.");
                ShowOwnHostCurrent(reading!.Version!);
                if (!SimulatedOwnHost.Active) HostFoundCurrent(ThisPcHostId, reading.Version);
                break;
            case OwnHostStep.OtherRoute:
                ShowOwnHost($"Martlet is updating this PC's host service from {reported} to {Version} (Update hosts, a run window " +
                    "or a command from another computer).");
                break;
            case OwnHostStep.AppUpdateFirst:
                ShowOwnHost($"This PC's host service runs {reported}. Martlet installs its own update first; the host service " +
                    "follows that version right after.");
                break;
            case OwnHostStep.Conversation:
                ShowOwnHost($"This PC's host service runs {reported}. Martlet updates it to {Version} once it isn't replying or " +
                    "hearing you.");
                break;
            case OwnHostStep.Update:
                await UpdateOwnHostAsync(reported);
                break;
        }
    }

    /// <summary>Builds this version's martlet-host image when needed and runs martlet-host update on this PC's Docker Desktop,
    /// with its output in the host-runs log. Automatic: it doesn't queue behind another change running on this host; it stops
    /// at once without changing anything and Martlet tries again a few minutes later.</summary>
    private async Task UpdateOwnHostAsync(string from)
    {
        var updating = hostUpdates.Begin(ThisPcHostId);
        var started = DateTimeOffset.UtcNow;
        var output = new EngineOutput(new LineSink(line => HostRunLog.Write(OwnHostRun, line)));
        ErrorLog.Info($"Updating this PC's host service from {from} to {Version} in the background.");
        HostRunLog.Write(OwnHostRun, $"--- started: {from} -> {Version}");
        ShowOwnHost($"Updating this PC's host service from {from} to {Version} in the background. Martlet stays usable; the " +
            "host service restarts at the end, so it stops answering for a moment.");
        RenderHost();
        var updated = false;
        try
        {
            int exit;
            if (SimulatedOwnHost.Active) exit = await SimulatedOwnHost.UpdateAsync(Version, output, lifetime.Token);
            else
            {
                var target = ThisPcTarget();
                await HostLocal.EnsureImageAsync(target, status => HostRunLog.Write(OwnHostRun, "status: " + status), output, lifetime.Token,
                    OwnHostRun);
                exit = await HostLocal.EngineAsync(target, ["update"], output, lifetime.Token, waitForOtherChanges: false);
            }
            HostRunLog.Write(OwnHostRun, $"--- exit {exit}");
            if (closing) return;
            if (output.Busy(exit) is { } busy)
            {
                ownHost.Busy(DateTimeOffset.UtcNow);
                var again = ownHost.RetryAt!.Value.ToLocalTime();
                ErrorLog.Info($"This PC's host service is busy ({busy}); updating it to {Version} waits until {again:t}.");
                ShowOwnHost($"This PC's host service is busy ({busy}), so updating it to Martlet {Version} waits; nothing was changed. " +
                    $"Martlet tries again at {again:t}.");
                return;
            }
            if (exit != 0)
            {
                ownHost.Failed(Version);
                ErrorLog.Warn($"Updating this PC's host service from {from} to {Version} stopped (exit {exit}); the host-runs log shows why.");
                ShowOwnHost($"Updating this PC's host service to Martlet {Version} stopped (exit {exit}); the host-runs log shows why. " +
                    "Update hosts (above) or the host dashboard's Update host service tries again.");
                return;
            }
            ownHost.Updated(Version);
            ownHostSeen = (OwnHostStep.Current, Version);
            updated = true;
            // The FIXTURE's host service is only the simulated one: notes and retries about the real one stay as they are.
            if (!SimulatedOwnHost.Active)
            {
                thisPcHostVersion = Version;
                HostUpdateSettled(ThisPcHostId);
            }
            ErrorLog.Info($"Updated this PC's host service from {from} to {Version} in the background " +
                $"({(DateTimeOffset.UtcNow - started).TotalSeconds:0} s).");
            ShowOwnHost($"Updated this PC's host service from {from} to {Version} at {DateTime.Now:t}. Martlet keeps it on this " +
                "app's version after every update.");
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is InvalidOperationException or IOException or UnauthorizedAccessException or Win32Exception)
        {
            HostRunLog.Write(OwnHostRun, "--- stopped: " + error.Message);
            ownHost.Failed(Version);
            ErrorLog.Warn($"Couldn't update this PC's host service to {Version}", error);
            if (!closing)
                ShowOwnHost($"Couldn't update this PC's host service to Martlet {Version}: {error.Message} Update hosts (above) or the " +
                    "host dashboard's Update host service tries again.");
        }
        finally
        {
            updating.Dispose();
            if (!closing)
            {
                RenderHost();
                if (updated && !SimulatedOwnHost.Active)
                {
                    if (Role == DeviceRole.Host) CheckThisPcHostAsync().Forget();
                    if (OwnHostPairing() is { } pairing) CheckHostsAsync([pairing]).Forget();
                }
            }
        }
    }

    /// <summary>Another route or check found this PC's own host service on <paramref name="version"/>, at least this app's:
    /// keeping it current has nothing left to do for this version.</summary>
    private void OwnHostFoundCurrent(string version)
    {
        // The FIXTURE's host service is only the simulated one; what Docker reports about a real one doesn't change it.
        if (SimulatedOwnHost.Active || OwnHostFollower.IsOlder(version, Version)) return;
        ownHost.Updated(Version);
        if (ownHostSeen is { Step: OwnHostStep.Current } seen && seen.Version == version) return;
        ownHostSeen = (OwnHostStep.Current, version);
        if (RunsOwnHostService) ShowOwnHostCurrent(version);
    }

    private void ShowOwnHostCurrent(string version) =>
        ShowOwnHost($"This PC's host service runs Martlet {version}, like this app. Martlet keeps it on this app's version after " +
            "every update.");

    /// <summary>Settings › App updates' line about this PC's own host service (hidden while this PC runs none).</summary>
    private void ShowOwnHost(string text)
    {
        OwnHostUpdateText.Text = text;
        OwnHostUpdateText.Visibility = Visibility.Visible;
    }
}
