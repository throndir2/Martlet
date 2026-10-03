using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using Martlet.Avatar.Audio2Face.Remote;
using Martlet.Core.Nodes;

namespace Martlet.Desktop;

/// <summary>App and host updates: GitHub Release checks every few minutes while Martlet runs, automatic or one-click
/// installs, and bringing paired hosts up to this PC's version.</summary>
public partial class MainWindow
{
    private readonly System.Windows.Threading.DispatcherTimer updateTimer = new() { Interval = TimeSpan.FromMinutes(1) };
    private UpdatePreferences updatePreferences = new();
    private DateTimeOffset? lastUpdateCycle;
    private (string Path, GitHubUpdate Update)? readyUpdate;
    private (string Installer, Version Version, bool Relaunch, bool Quiet)? installOnExit;
    private string? announcedUpdate;
    /// <summary>A version whose install just failed; it is not installed automatically again until you press Install.</summary>
    private string? failedInstall;
    /// <summary>What the last install said when it failed, for Home.</summary>
    private string? failedInstallMessage;
    private bool hostUpdatesRunning;
    private string? thisPcHostVersion;
    /// <summary>What happened to each host's latest update, shown on its Devices card.</summary>
    private readonly Dictionary<string, string> hostUpdateNotes = new(StringComparer.Ordinal);
    /// <summary>host@version pairs already updated automatically this session, so a failing host is not retried every check.</summary>
    private readonly HashSet<string> hostUpdateAttempts = new(StringComparer.Ordinal);
    /// <summary>Hosts whose update found them busy with another change (or their gateway restarting), by host ID
    /// (<see cref="ThisPcHostId"/> for this PC's own host service). They are tried again from <see cref="hostRetryAt"/>.</summary>
    private readonly HashSet<string> hostsWaiting = new(StringComparer.Ordinal);
    private DateTimeOffset? hostRetryAt;
    private static readonly TimeSpan HostRetryDelay = TimeSpan.FromMinutes(3);
    private const string ThisPcHostId = "this-pc";
    /// <summary>The status line last set to say what a downloaded automatic update waits for, so it is refreshed only while
    /// nothing else replaced it.</summary>
    private string? installWaitingText;
    /// <summary>You confirmed an install while host work was running; it starts when that work ends.</summary>
    private bool installAfterHostWork;

    private enum HostUpdateResult { Updated, Failed, Busy }

    private static string Mib(GitHubUpdate update) => $"{update.Bytes / (1024d * 1024d):F0} MB";

    private void InitializeUpdates()
    {
        foreach (var minutes in UpdatePreferences.Intervals)
            UpdateIntervalChoice.Items.Add(new ComboBoxItem
            {
                Tag = minutes,
                Content = minutes < 60 ? $"{minutes} minutes" : minutes == 60 ? "1 hour" : minutes < 1440 ? $"{minutes / 60} hours" : "24 hours"
            });
        string? problem = null;
        if (store is not null)
        {
            try { updateChecksEnabled = UpdateCheckPreferences.Load(store.DataDirectory); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                problem = "Couldn't load update settings. Automatic checks stay on until you save again.";
            }
            try { updatePreferences = UpdatePreferences.Load(store.DataDirectory); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                problem = "Couldn't load update settings. Automatic installs stay off until you save again.";
            }
        }
        CurrentVersionText.Text = $"Current version: Martlet {Version}";
        changingUpdateChoice = true;
        AutomaticUpdateCheck.IsChecked = updateChecksEnabled;
        AutomaticUpdateInstall.IsChecked = updatePreferences.AutoInstall;
        AutomaticHostUpdate.IsChecked = updatePreferences.AutoUpdateHosts;
        UpdateIntervalChoice.SelectedItem = UpdateIntervalChoice.Items.OfType<ComboBoxItem>()
            .First(item => (int)item.Tag == updatePreferences.IntervalMinutes);
        changingUpdateChoice = false;
        ShowUpdateControls();
        UpdateStatusText.Text = problem ?? DescribeUpdateSettings();
        updateTimer.Tick += (_, _) => UpdateTickAsync().Forget();
    }

    private bool AutoInstalling => updateChecksEnabled && updatePreferences.AutoInstall;

    /// <summary>Whether the downloaded update may install without asking now or at exit.</summary>
    private bool AutoInstallReady => AutoInstalling && readyUpdate is { } ready && ready.Update.Version.ToString(3) != failedInstall;

    private void ShowUpdateControls()
    {
        UpdateIntervalChoice.IsEnabled = store is not null && (updateChecksEnabled || updatePreferences.AutoUpdateHosts);
        AutomaticUpdateInstall.IsEnabled = store is not null && updateChecksEnabled;
    }

    private string DescribeUpdateSettings()
    {
        var every = UpdatePreferences.Describe(updatePreferences.IntervalMinutes);
        var text = updateChecksEnabled
            ? $"Martlet {Version}. Checks every {every} while Martlet runs" +
              (updatePreferences.AutoInstall ? " and installs updates automatically." : ". You choose when to install.")
            : $"Martlet {Version}. Automatic checks are off. Use Check for updates any time.";
        if (updatePreferences.AutoUpdateHosts) text += $" Paired hosts update every {every}.";
        return text;
    }

    /// <summary>Runs after launch: reports the last install, removes finished installers and starts the periodic timer.</summary>
    private async Task StartUpdatesAsync()
    {
        if (store is null) return;
        if (AppUpdateInstaller.TakeLastResult(store.DataDirectory, Version) is { } result)
        {
            UpdateStatusText.Text = ActionText.Text = result.Message;
            failedInstall = result.Failed;
            if (result.Failed is not null) failedInstallMessage = result.Message;
        }
        AppUpdateInstaller.CleanUp(store.DataDirectory, Version);
        updateTimer.Start();
        if (updateChecksEnabled || updatePreferences.AutoUpdateHosts) await RunUpdateCycleAsync();
    }

    private async Task UpdateTickAsync()
    {
        if (closing || store is null) return;
        if (installAfterHostWork && readyUpdate is not null && HostWorkBlocker() is null && !updateBusy)
        {
            installAfterHostWork = false;
            InstallNow(quiet: false);
            return;
        }
        if (AutoInstallReady && IsIdleForUpdate())
        {
            InstallNow(quiet: true);
            return;
        }
        if (AutoInstallReady && installWaitingText is not null && UpdateStatusText.Text == installWaitingText) ShowInstallWaiting();
        var due = lastUpdateCycle is not { } last || DateTimeOffset.UtcNow - last >= TimeSpan.FromMinutes(updatePreferences.IntervalMinutes);
        if (due && (updateChecksEnabled || updatePreferences.AutoUpdateHosts)) await RunUpdateCycleAsync();
        else if (hostRetryAt is { } retry && DateTimeOffset.UtcNow >= retry && !updateBusy && !SelfUpdatePending)
        {
            hostRetryAt = null;
            await UpdateHostsAsync(automatic: true, retrying: true);
        }
    }

    /// <summary>Whether this PC is about to install its own update; hosts follow this PC's version, so they wait for it.</summary>
    private bool SelfUpdatePending => AutoInstalling && availableUpdate is { } pending && pending.Version.ToString(3) != failedInstall;

    /// <summary>Says that the downloaded update waits and for what (the status line only).</summary>
    private void ShowInstallWaiting()
    {
        if (readyUpdate is not { } ready) return;
        installWaitingText = $"Martlet {ready.Update.Version.ToString(3)} is downloaded. It installs when Martlet is idle or when you exit." +
            (InstallBlocker() is { } why ? $" Waiting: {why}." : "");
        UpdateStatusText.Text = installWaitingText;
    }

    /// <summary>One automatic round: check GitHub, download and install when allowed, then bring older hosts up to date.</summary>
    private async Task RunUpdateCycleAsync()
    {
        if (closing || updateBusy || store is null) return;
        lastUpdateCycle = DateTimeOffset.UtcNow;
        if (updateChecksEnabled)
        {
            await CheckForUpdatesAsync(background: true);
            if (closing) return;
            if (!AutoInstalling && availableUpdate is { } offered && announcedUpdate != offered.Tag)
            {
                announcedUpdate = offered.Tag;
                if (OwnedWindows.Count == 0 && !setupOperations.IsRunning)
                {
                    await ConfirmAndInstallAsync(offered, prompted: true);
                    if (closing) return;
                }
                else ActionText.Text = $"Martlet {offered.Version.ToString(3)} is available in Settings.";
            }
            if (AutoInstalling && availableUpdate is { } update && readyUpdate?.Update.Version != update.Version)
                await DownloadUpdateAsync();
            if (AutoInstallReady && readyUpdate is { } ready && !closing)
            {
                if (IsIdleForUpdate())
                {
                    InstallNow(quiet: true);
                    return;
                }
                ShowInstallWaiting();
            }
        }
        // Hosts follow this PC's version, so wait while this PC is about to update itself.
        var selfUpdating = AutoInstalling && availableUpdate is { } pending && pending.Version.ToString(3) != failedInstall;
        if (updatePreferences.AutoUpdateHosts && !selfUpdating && !closing)
            await UpdateHostsAsync(automatic: true);
    }

    /// <summary>Why installing now would interrupt you, or null when Martlet is idle.</summary>
    private string? BusyReason() =>
        avatar.IsShowing ? "The character"
        : conversation?.IsRunning == true ? "The conversation"
        : null;

    /// <summary>What installing an update now would interrupt, in words, or null when Martlet may close for it: you, the
    /// character or a conversation, and work Martlet does for you or for your other computers (setup tasks, host updates,
    /// a command another computer sent, an update check or download). <paramref name="asked"/>: the install is what a
    /// martlet.update command from another computer is doing, so that command doesn't count as work in the way.</summary>
    private string? InstallBlocker(bool asked = false) =>
        closing ? "Martlet is closing"
        : saving ? "Martlet is saving your changes"
        : avatar.IsShowing ? "the character is showing"
        : conversation?.IsRunning == true ? "a conversation is running"
        : HostWorkBlocker(asked) is { } work ? work
        : updateBusy ? "an update check or download is running"
        : !asked && nodeAgentBusy ? "Martlet is checking for commands from your other computers"
        : OwnedWindows.Count > 0 ? "a Martlet window is open"
        : IsActive ? "you're using Martlet"
        : null;

    /// <summary>Work on hosts, or for your other computers, that closing Martlet would cut off, in words, or null. Even an
    /// install you confirmed waits for it.</summary>
    private string? HostWorkBlocker(bool asked = false) =>
        setupOperations.IsRunning ? "a setup task is running"
        : hostUpdatesRunning ? "a host service update is running"
        : !asked && nodeCommandRunning is { } command ? $"Martlet is running {NodeCommandAgent.Describe(command)}"
        : null;

    private bool IsIdleForUpdate(bool asked = false) => InstallBlocker(asked) is null;

    private void UpdateCheckSetting_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || changingUpdateChoice || store is null) return;
        var enabled = AutomaticUpdateCheck.IsChecked == true;
        try
        {
            UpdateCheckPreferences.Save(store.DataDirectory, enabled);
            updateChecksEnabled = enabled;
            if (!enabled) updateCheckCancellation?.Cancel();
            ShowUpdateControls();
            UpdateStatusText.Text = DescribeUpdateSettings();
            if (enabled) RunUpdateCycleAsync().Forget();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            changingUpdateChoice = true;
            AutomaticUpdateCheck.IsChecked = updateChecksEnabled;
            changingUpdateChoice = false;
            UpdateStatusText.Text = "Couldn't save update settings. Check access to Martlet's data folder.";
        }
    }

    private void UpdatePreference_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || changingUpdateChoice || store is null) return;
        var next = new UpdatePreferences
        {
            IntervalMinutes = UpdateIntervalChoice.SelectedItem is ComboBoxItem { Tag: int minutes } ? minutes : 60,
            AutoInstall = AutomaticUpdateInstall.IsChecked == true,
            AutoUpdateHosts = AutomaticHostUpdate.IsChecked == true
        };
        try
        {
            UpdatePreferences.Save(store.DataDirectory, next);
            var startNow = next.AutoInstall && !updatePreferences.AutoInstall || next.AutoUpdateHosts && !updatePreferences.AutoUpdateHosts;
            updatePreferences = next;
            ShowUpdateControls();
            UpdateStatusText.Text = DescribeUpdateSettings();
            if (startNow) RunUpdateCycleAsync().Forget();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            changingUpdateChoice = true;
            AutomaticUpdateInstall.IsChecked = updatePreferences.AutoInstall;
            AutomaticHostUpdate.IsChecked = updatePreferences.AutoUpdateHosts;
            UpdateIntervalChoice.SelectedItem = UpdateIntervalChoice.Items.OfType<ComboBoxItem>()
                .First(item => (int)item.Tag == updatePreferences.IntervalMinutes);
            changingUpdateChoice = false;
            UpdateStatusText.Text = "Couldn't save update settings. Check access to Martlet's data folder.";
        }
    }

    private async void CheckForUpdates_Click(object sender, RoutedEventArgs e) => await CheckForUpdatesAsync(background: false);

    /// <summary>Waits until no update check or download is under way (the periodic one, or one you started), so work that
    /// needs one joins it instead of finding Martlet busy and giving up.</summary>
    private async Task WaitForUpdateWorkAsync(CancellationToken token)
    {
        while (updateBusy && !closing)
        {
            if (updateDrain is { } drain) await drain.Task.WaitAsync(token);
            else await Task.Delay(TimeSpan.FromMilliseconds(200), token);
        }
    }

    private async Task CheckForUpdatesAsync(bool background)
    {
        if (closing || updateBusy) return;
        updateBusy = true;
        updateDrain = new(TaskCreationOptions.RunContinuationsAsynchronously);
        CheckForUpdatesButton.IsEnabled = false;
        if (!background) UpdateStatusText.Text = "Checking for updates...";
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        updateCheckCancellation = cancellation;
        try
        {
            using var http = GitHubReleaseClient.CreateHttpClient();
            var result = await new GitHubReleaseClient(http).CheckAsync(typeof(App).Assembly.GetName().Version!, cancellation.Token);
            if (closing || cancellation.IsCancellationRequested) return;
            availableUpdate = result;
            if (readyUpdate is { } ready && ready.Update.Version != result?.Version) readyUpdate = null;
            DownloadUpdateButton.Content = result is null ? "_Install update" : $"_Install {result.Version.ToString(3)} now";
            DownloadUpdateButton.Visibility = ReviewUpdateButton.Visibility = result is null ? Visibility.Collapsed : Visibility.Visible;
            if (result is null)
            {
                UpdateStatusText.Text = $"You're up to date. Checked {DateTime.Now:t}.";
                return;
            }
            UpdateStatusText.Text = $"Martlet {result.Version.ToString(3)} is available ({Mib(result)})." + (AutoInstalling
                ? " Downloading for automatic install."
                : " Press Install to download and restart.");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (OperationCanceledException)
        {
            if (!closing) UpdateStatusText.Text = "Update check timed out. Martlet will try again later.";
        }
        catch (Exception error) when (error is HttpRequestException or InvalidDataException or IOException)
        {
            if (!closing) UpdateStatusText.Text = $"Couldn't check for updates: {UpdateError(error)}";
        }
        finally
        {
            updateCheckCancellation = null;
            updateBusy = false;
            if (!closing) CheckForUpdatesButton.IsEnabled = true;
            updateDrain.TrySetResult();
            updateDrain = null;
            if (!closing) RenderHealth();
        }
    }

    /// <summary>Downloads the available installer into Martlet's updates folder and checks it against GitHub's digest.</summary>
    private async Task DownloadUpdateAsync()
    {
        if (closing || updateBusy || store is null || availableUpdate is not { } update) return;
        updateBusy = true;
        updateDrain = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        updateDownloadCancellation = cancellation;
        DownloadUpdateButton.IsEnabled = CheckForUpdatesButton.IsEnabled = false;
        UpdateStatusText.Text = $"Downloading Martlet {update.Version.ToString(3)} ({Mib(update)})...";
        try
        {
            using var http = GitHubReleaseClient.CreateHttpClient();
            var path = await AppUpdateInstaller.DownloadAsync(new GitHubReleaseClient(http), update, store.DataDirectory, cancellation.Token);
            readyUpdate = (path, update);
            if (!closing) UpdateStatusText.Text = $"Martlet {update.Version.ToString(3)} is ready to install.";
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception error) when (error is HttpRequestException or InvalidDataException or IOException or
            UnauthorizedAccessException or ArgumentException or OperationCanceledException)
        {
            if (error is UpdateCleanupException) interruptedUpdateCleanup = error.Message;
            if (!closing) UpdateStatusText.Text = $"Couldn't download update: {UpdateError(error)}";
        }
        finally
        {
            updateDownloadCancellation = null;
            updateBusy = false;
            if (!closing) CheckForUpdatesButton.IsEnabled = DownloadUpdateButton.IsEnabled = true;
            updateDrain.TrySetResult();
            updateDrain = null;
        }
    }

    private async void DownloadUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (closing || updateBusy || availableUpdate is not { } update) return;
        await ConfirmAndInstallAsync(update, prompted: false);
    }

    /// <summary>Asks once, then downloads and verifies the installer, closes Martlet, runs the installer and starts again. Work
    /// on hosts or for your other computers that is running then finishes first.</summary>
    private async Task ConfirmAndInstallAsync(GitHubUpdate update, bool prompted)
    {
        var busy = BusyReason();
        var work = HostWorkBlocker();
        if (!ConfirmationDialog.Confirm(this,
                $"Martlet {update.Version.ToString(3)} is available. Install it now?\n\n" +
                $"The update is {Mib(update)}. Martlet will close, install it and restart." +
                (busy is null ? "" : $"\n\n{busy} will pause until Martlet restarts.") +
                (work is null ? "" : $"\n\nIt waits until this finishes: {work}.") +
                (prompted ? "\n\nYou can also install it later from Settings." : ""),
                "Install Martlet update"))
            return;
        if (readyUpdate?.Update.Version != update.Version) await DownloadUpdateAsync();
        if (readyUpdate?.Update.Version != update.Version || closing) return;
        if (HostWorkBlocker() is { } still)
        {
            installAfterHostWork = true;
            UpdateStatusText.Text = ActionText.Text = $"Martlet {update.Version.ToString(3)} installs as soon as this finishes: {still}.";
            return;
        }
        InstallNow(quiet: false);
    }

    /// <summary>Closes Martlet; the installer runs once it has exited and then starts Martlet again (minimized after an
    /// automatic install, so it does not take focus from what you are doing, and in the notification area when it was there).</summary>
    private void InstallNow(bool quiet)
    {
        if (readyUpdate is not { } ready || closing) return;
        installOnExit = (ready.Path, ready.Update.Version, true, quiet);
        UpdateStatusText.Text = ActionText.Text =
            $"Installing Martlet {ready.Update.Version.ToString(3)}. Martlet will restart when it's done.";
        ExitMartlet();
    }

    /// <summary>At exit: runs the requested install, or a downloaded automatic update without restarting Martlet.</summary>
    private void LaunchPendingInstall()
    {
        if (store is null) return;
        var install = installOnExit ?? (AutoInstallReady && readyUpdate is { } ready ? (ready.Path, ready.Update.Version, false, false) : null);
        if (install is not { } run) return;
        try
        {
            AppUpdateInstaller.Launch(run.Installer, run.Version, store.DataDirectory, run.Relaunch, run.Quiet,
                (Application.Current as App)?.DataDirectoryArgument, inTray);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException) { }
    }

    private void ReviewUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (availableUpdate is not { } update || closing) return;
        try { Process.Start(new ProcessStartInfo(update.ReleasePage.AbsoluteUri) { UseShellExecute = true })?.Dispose(); }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException)
        {
            UpdateStatusText.Text = $"Couldn't open the release notes. Copy this URL into your browser: {update.ReleasePage}";
        }
    }

    private static string UpdateError(Exception error) => error switch
    {
        InvalidDataException or ArgumentException => error.Message,
        HttpRequestException => "The update service could not be reached.",
        UpdateCleanupException => error.Message,
        OperationCanceledException => "The download timed out. Martlet will try again later.",
        UnauthorizedAccessException or IOException => "Martlet can't save the update. Check access and free space.",
        _ => "The update couldn't be checked or downloaded."
    };

    // ---------- hosts ----------

    private void UpdateHosts_Click(object sender, RoutedEventArgs e) => UpdateHostsAsync(automatic: false).Forget();

    /// <summary>Brings every paired host that runs an older Martlet (or does not report its version) to this PC's version,
    /// one at a time and without a console window. A host that would need a password, sudo or an approval there keeps an
    /// Update host command on its Devices card, which runs the same update in a run window. A host busy with another change
    /// (an install, another computer's update, its console) is not interrupted: nothing changes there, and Martlet tries
    /// it again every few minutes until it is free (<paramref name="retrying"/> runs only those).</summary>
    private async Task UpdateHostsAsync(bool automatic, bool retrying = false)
    {
        if (store is null || closing) return;
        if (hostUpdatesRunning)
        {
            if (retrying) hostRetryAt = DateTimeOffset.UtcNow + HostRetryDelay;
            return;
        }
        hostUpdatesRunning = true;
        UpdateHostsButton.IsEnabled = false;
        int updated = 0, failed = 0, current = 0, unreachable = 0, asked = 0, busy = 0;
        var only = retrying ? hostsWaiting.ToHashSet(StringComparer.Ordinal) : null;
        var hosts = NetworkMap.Hosts(Inputs()).Where(h => only is null || only.Contains(h.HostId)).ToList();
        if (only is null) hostsWaiting.Clear();
        else hostsWaiting.ExceptWith(only);

        void Record(string id, string attempt, HostUpdateResult result)
        {
            switch (result)
            {
                case HostUpdateResult.Updated: updated++; break;
                case HostUpdateResult.Busy:
                    busy++;
                    hostsWaiting.Add(id);
                    hostUpdateAttempts.Remove(attempt);
                    break;
                default: failed++; break;
            }
        }

        try
        {
            if (!automatic) UpdateStatusText.Text = hosts.Count == 0 && Role != DeviceRole.Host
                ? "No host is paired yet." : "Checking host versions...";
            foreach (var host in hosts)
            {
                if (closing) return;
                var check = await HostControl.CheckAsync(host.Pairing, HardwareStore, lifetime.Token);
                hostChecks[host.HostId] = check;
                if (check.Reachable != true) { unreachable++; continue; }
                if (!AppVersions.IsOlder(check.MartletVersion, Version)) { current++; continue; }
                var attempt = host.HostId + "@" + Version;
                if (automatic && !hostUpdateAttempts.Add(attempt)) continue;
                if (host.Method == HostSetupMethod.Agent)
                {
                    var result = await AskHostToUpdateAsync(host);
                    if (result == HostUpdateResult.Updated) asked++;
                    else Record(host.HostId, attempt, result);
                    continue;
                }
                if (!host.CanLaunch)
                {
                    hostUpdateNotes[host.HostId] = "This host needs an update. Set how Martlet signs in over SSH, or choose Through Martlet on that computer.";
                    failed++;
                    continue;
                }
                var outcome = await UpdateHostQuietlyAsync(host.HostId, host.Target(Version), host.SshHostKey);
                Record(host.HostId, attempt, outcome);
                if (outcome == HostUpdateResult.Updated) hostChecks[host.HostId] = await HostControl.CheckAsync(host.Pairing, HardwareStore, lifetime.Token);
            }
            if (Role == DeviceRole.Host && !closing && (only is null || only.Contains(ThisPcHostId)))
            {
                thisPcHostVersion = await HostSetupCommands.ThisPcGatewayVersionAsync(lifetime.Token);
                var attempt = ThisPcHostId + "@" + Version;
                if (thisPcHostVersion is not null && AppVersions.IsOlder(thisPcHostVersion, Version) &&
                    (!automatic || hostUpdateAttempts.Add(attempt)))
                {
                    var outcome = await UpdateHostQuietlyAsync(ThisPcHostId, ThisPcTarget());
                    Record(ThisPcHostId, attempt, outcome);
                    if (outcome == HostUpdateResult.Updated) thisPcHostVersion = await HostSetupCommands.ThisPcGatewayVersionAsync(lifetime.Token);
                }
                else if (thisPcHostVersion is not null) current++;
            }
        }
        catch (OperationCanceledException) { return; }
        finally
        {
            hostUpdatesRunning = false;
            hostRetryAt = hostsWaiting.Count > 0 && !closing ? DateTimeOffset.UtcNow + HostRetryDelay : null;
            if (!closing)
            {
                UpdateHostsButton.IsEnabled = true;
                RenderHome();
                if (DevicesPage.IsVisible) RenderMap();
            }
        }
        if (closing || automatic && updated + failed + asked + busy == 0) return;
        if (!automatic && hosts.Count == 0 && Role != DeviceRole.Host) return;
        var summary = $"Host updates: {updated} updated to Martlet {Version}, {current} already current" +
            (asked > 0 ? $", {asked} asked to update through Martlet there" : "") +
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
                    catch (Audio2FaceHostException) { }
                }
                return (sent, commands);
            });
            hostUpdateNotes[host.HostId] = command.State == NodeCommandState.Running
                ? $"Martlet on {host.HostId} is updating to {Version}."
                : list?.WaitingText(command, host.HostId) is { } behind
                    ? $"Asked Martlet on {host.HostId} to update to {Version} ({DateTime.Now:t}). {behind}"
                    : $"Asked Martlet on {host.HostId} to update to {Version} ({DateTime.Now:t}); it does when Martlet runs there.";
            return HostUpdateResult.Updated;
        }
        catch (Audio2FaceHostException error) when (error.Code == "request.invalid")
        {
            hostUpdateNotes[host.HostId] = $"Its host service is older than commands between computers. Open Martlet on {host.HostId} once: " +
                "it brings its host service up to date by itself, and from then on this PC updates it from here.";
            return HostUpdateResult.Failed;
        }
        catch (Exception error) when (error is Audio2FaceHostException { Code: "host.unreachable" or "gateway.internal" } ||
            error is OperationCanceledException && !lifetime.IsCancellationRequested)
        {
            hostUpdateNotes[host.HostId] = $"{host.HostId}'s host service didn't answer (it may be restarting for an update). " +
                "Martlet asks it again in a few minutes.";
            return HostUpdateResult.Busy;
        }
        catch (Exception error) when (ClusterSync.IsHostFailure(error))
        {
            hostUpdateNotes[host.HostId] = $"Could not ask Martlet on {host.HostId} to update: {error.Message}";
            return HostUpdateResult.Failed;
        }
    }

    /// <summary>Runs martlet-host update there unattended. It doesn't queue behind another change running on that host: the
    /// engine then stops at once without changing anything (<see cref="HostEngineBusy"/>) and this returns
    /// <see cref="HostUpdateResult.Busy"/>, so the caller tries again a few minutes later.</summary>
    private async Task<HostUpdateResult> UpdateHostQuietlyAsync(string id, HostSetupTarget target, string? sshHostKey = null)
    {
        hostUpdateNotes[id] = $"Updating to Martlet {Version}...";
        if (DevicesPage.IsVisible) RenderMap();
        try
        {
            var (code, log) = await HostSetupCommands.RunUnattendedAsync(target, HostAction.Update, lifetime.Token, store?.DataDirectory, sshHostKey);
            if (HostEngineBusy.Read(code, ReadLog(log)) is { } what)
            {
                hostUpdateNotes[id] = $"Waiting to update to Martlet {Version}: that host is busy ({what}). Nothing was changed; " +
                    $"Martlet tries again every few minutes until it is free (last try {DateTime.Now:t}).";
                return HostUpdateResult.Busy;
            }
            hostUpdateNotes[id] = code == 0
                ? $"Updated to Martlet {Version} at {DateTime.Now:t}."
                : "The update needs your attention on that computer. Press Update host to finish it.";
            return code == 0 ? HostUpdateResult.Updated : HostUpdateResult.Failed;
        }
        catch (Exception error) when (error is InvalidOperationException or IOException or UnauthorizedAccessException or Win32Exception)
        {
            hostUpdateNotes[id] = $"Couldn't start the update: {error.Message}. Press Update host to run it from Martlet.";
            return HostUpdateResult.Failed;
        }
    }

    private static string[] ReadLog(string path)
    {
        try { return File.ReadAllLines(path); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return []; }
    }
}
