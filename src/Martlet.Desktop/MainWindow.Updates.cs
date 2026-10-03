using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using NodeCommandKinds = Martlet.Core.Nodes.NodeCommandKinds;

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
        if (AutoInstallReady && IsIdleForUpdate())
        {
            InstallNow(quiet: true);
            return;
        }
        var due = lastUpdateCycle is not { } last || DateTimeOffset.UtcNow - last >= TimeSpan.FromMinutes(updatePreferences.IntervalMinutes);
        if (due && (updateChecksEnabled || updatePreferences.AutoUpdateHosts)) await RunUpdateCycleAsync();
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
                UpdateStatusText.Text = $"Martlet {ready.Update.Version.ToString(3)} is downloaded. It installs when Martlet is idle or when you exit.";
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

    private bool IsIdleForUpdate() =>
        !closing && !saving && BusyReason() is null && OwnedWindows.Count == 0 && !IsActive && !setupOperations.IsRunning;

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

    /// <summary>Asks once, then downloads and verifies the installer, closes Martlet, runs the installer and starts again.</summary>
    private async Task ConfirmAndInstallAsync(GitHubUpdate update, bool prompted)
    {
        var busy = BusyReason();
        if (!ConfirmationDialog.Confirm(this,
                $"Martlet {update.Version.ToString(3)} is available. Install it now?\n\n" +
                $"The update is {Mib(update)}. Martlet will close, install it and restart." +
                (busy is null ? "" : $"\n\n{busy} will pause until Martlet restarts.") +
                (prompted ? "\n\nYou can also install it later from Settings." : ""),
                "Install Martlet update"))
            return;
        if (readyUpdate?.Update.Version != update.Version) await DownloadUpdateAsync();
        if (readyUpdate?.Update.Version == update.Version) InstallNow(quiet: false);
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
    /// Update host command on its Devices card, which runs the same update in a run window.</summary>
    private async Task UpdateHostsAsync(bool automatic)
    {
        if (store is null || closing || hostUpdatesRunning) return;
        hostUpdatesRunning = true;
        UpdateHostsButton.IsEnabled = false;
        int updated = 0, failed = 0, current = 0, unreachable = 0, asked = 0;
        var hosts = NetworkMap.Hosts(Inputs());
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
                if (automatic && !hostUpdateAttempts.Add(host.HostId + "@" + Version)) continue;
                if (host.Method == HostSetupMethod.Agent)
                {
                    if (await AskHostToUpdateAsync(host)) asked++;
                    else failed++;
                    continue;
                }
                if (!host.CanLaunch)
                {
                    hostUpdateNotes[host.HostId] = "This host needs an update. Set how Martlet signs in over SSH, or choose Through Martlet on that computer.";
                    failed++;
                    continue;
                }
                if (await UpdateHostQuietlyAsync(host.HostId, host.Target(Version), host.Reach, host.SshHostKey))
                {
                    updated++;
                    hostChecks[host.HostId] = await HostControl.CheckAsync(host.Pairing, HardwareStore, lifetime.Token);
                }
                else failed++;
            }
            if (Role == DeviceRole.Host && !closing)
            {
                thisPcHostVersion = await HostSetupCommands.ThisPcGatewayVersionAsync(lifetime.Token);
                if (thisPcHostVersion is not null && AppVersions.IsOlder(thisPcHostVersion, Version) &&
                    (!automatic || hostUpdateAttempts.Add("this-pc@" + Version)))
                {
                    if (await UpdateHostQuietlyAsync("this-pc", ThisPcTarget(), "this PC's Docker Desktop"))
                    {
                        updated++;
                        thisPcHostVersion = await HostSetupCommands.ThisPcGatewayVersionAsync(lifetime.Token);
                    }
                    else failed++;
                }
                else if (thisPcHostVersion is not null) current++;
            }
        }
        catch (OperationCanceledException) { return; }
        finally
        {
            hostUpdatesRunning = false;
            if (!closing)
            {
                UpdateHostsButton.IsEnabled = true;
                RenderHome();
                if (DevicesPage.IsVisible) RenderMap();
            }
        }
        if (closing || automatic && updated + failed + asked == 0) return;
        if (!automatic && hosts.Count == 0 && Role != DeviceRole.Host) return;
        var summary = $"Host updates: {updated} updated to Martlet {Version}, {current} already current" +
            (asked > 0 ? $", {asked} asked to update through Martlet there" : "") +
            (failed > 0 ? $", {failed} need you (see their cards on the Devices map)" : "") +
            (unreachable > 0 ? $", {unreachable} not reachable" : "") + ".";
        UpdateStatusText.Text = summary;
        if (automatic || failed > 0) ActionText.Text = summary;
    }

    /// <summary>Asks Martlet on a host reached through its paired connection to bring itself to this PC's version (app and host
    /// service). The command waits there until Martlet runs; asking again while it waits changes nothing.</summary>
    private async Task<bool> AskHostToUpdateAsync(PairedHost host)
    {
        try
        {
            var command = await ClusterSync.WithConnectionAsync(host.Pairing, connection => connection.SendCommandAsync(NodeCommandKinds.Update,
                new Dictionary<string, string> { ["version"] = Version }, null, lifetime.Token));
            hostUpdateNotes[host.HostId] = command.State == Martlet.Core.Nodes.NodeCommandState.Running
                ? $"Martlet on {host.HostId} is updating to {Version}."
                : $"Asked Martlet on {host.HostId} to update to {Version} ({DateTime.Now:t}); it does when Martlet runs there.";
            return true;
        }
        catch (Martlet.Avatar.Audio2Face.Remote.Audio2FaceHostException error) when (error.Code == "request.invalid")
        {
            hostUpdateNotes[host.HostId] = $"Its host service is older than commands between computers. Open Martlet on {host.HostId} once: " +
                "it brings its host service up to date by itself, and from then on this PC updates it from here.";
            return false;
        }
        catch (Exception error) when (ClusterSync.IsHostFailure(error))
        {
            hostUpdateNotes[host.HostId] = $"Could not ask Martlet on {host.HostId} to update: {error.Message}";
            return false;
        }
    }

    private async Task<bool> UpdateHostQuietlyAsync(string id, HostSetupTarget target, string reach, string? sshHostKey = null)
    {
        hostUpdateNotes[id] = $"Updating to Martlet {Version}...";
        if (DevicesPage.IsVisible) RenderMap();
        try
        {
            var (code, _) = await HostSetupCommands.RunUnattendedAsync(target, HostAction.Update, lifetime.Token, store?.DataDirectory, sshHostKey);
            hostUpdateNotes[id] = code == 0
                ? $"Updated to Martlet {Version} at {DateTime.Now:t}."
                : "The update needs your attention on that computer. Press Update host to finish it.";
            return code == 0;
        }
        catch (Exception error) when (error is InvalidOperationException or IOException or UnauthorizedAccessException or Win32Exception)
        {
            hostUpdateNotes[id] = $"Couldn't start the update: {error.Message}. Press Update host to run it from Martlet.";
            return false;
        }
    }
}
