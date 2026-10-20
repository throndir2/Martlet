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

/// <summary>App updates: GitHub Release checks every few minutes while Martlet runs and the update settings. Downloading,
/// installing beside Martlet and restarting are in MainWindow.UpdateInstall.cs; bringing paired hosts up to this PC's version is
/// in MainWindow.HostUpdates.cs.</summary>
public partial class MainWindow
{
    private readonly System.Windows.Threading.DispatcherTimer updateTimer = new() { Interval = TimeSpan.FromMinutes(1) };
    private UpdatePreferences updatePreferences = new();
    private DateTimeOffset? lastUpdateCycle;
    private (string Path, GitHubUpdate Update)? readyUpdate;
    private (string Installer, Version Version, bool Relaunch, bool Unattended)? installOnExit;
    /// <summary>The program folder the installer put this Martlet in; null for a development build. Updates are installed
    /// beside it (in its ".next" folder) while Martlet runs, so switching to them takes only a restart.</summary>
    private static readonly string? installRoot = AppUpdateInstaller.InstallRoot;
    /// <summary>The version installed beside this Martlet, ready to switch to with a restart of a few seconds.</summary>
    private Version? stagedVersion;
    /// <summary>A version that could not be installed beside this Martlet; it installs the classic way, with Martlet closed
    /// for the whole install.</summary>
    private Version? stagingFailedVersion;
    /// <summary>A version whose install beside Martlet you canceled (Background tasks); it starts again by itself only at the
    /// next update check.</summary>
    private Version? stagingCanceledVersion;
    private Task? stagingTask;
    /// <summary>Removing what the last update left beside the program folder, which installing the next one waits for.</summary>
    private Task besideCleanup = Task.CompletedTask;
    private string? announcedUpdate;
    /// <summary>A version whose install just failed; it is not installed automatically again until you press Install.</summary>
    private string? failedInstall;
    /// <summary>What the last install said when it failed, for Home.</summary>
    private string? failedInstallMessage;
    /// <summary>The status line last set to say what a downloaded automatic update waits for, so it is refreshed only while
    /// nothing else replaced it.</summary>
    private string? installWaitingText;
    /// <summary>You confirmed an install while host work was running; it starts when that work ends.</summary>
    private bool installAfterHostWork;

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
                problem = "Couldn't load update settings, so automatic checks stay on until you save again.";
            }
            try { updatePreferences = UpdatePreferences.Load(store.DataDirectory); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                problem = "Couldn't load update settings, so automatic installs stay off until you save again.";
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

    /// <summary>Whether the downloaded update is installed beside this Martlet, so a restart of a few seconds switches to it.</summary>
    private bool UpdateStaged => readyUpdate is { } ready && installRoot is not null && stagedVersion == ready.Update.Version;

    /// <summary>Whether the downloaded update is ready to restart into: installed beside this Martlet, or it installs the
    /// classic way (a development build, or installing it beside Martlet failed).</summary>
    private bool UpdatePrepared => readyUpdate is { } ready &&
        (installRoot is null || stagedVersion == ready.Update.Version || stagingFailedVersion == ready.Update.Version);

    private void ShowUpdateControls()
    {
        UpdateIntervalChoice.IsEnabled = store is not null && (updateChecksEnabled || updatePreferences.AutoUpdateHosts);
        AutomaticUpdateInstall.IsEnabled = store is not null && updateChecksEnabled;
    }

    private string DescribeUpdateSettings()
    {
        var every = UpdatePreferences.Describe(updatePreferences.IntervalMinutes);
        var text = updateChecksEnabled
            ? $"Martlet {Version}: checks every {every} while Martlet runs" +
              (updatePreferences.AutoInstall ? " and installs updates as soon as they're downloaded" : ", and you choose when to install")
            : $"Martlet {Version}: automatic checks are off, but Check for updates works any time";
        if (updatePreferences.AutoUpdateHosts) text += $", and paired hosts update every {every}";
        return text + ".";
    }

    /// <summary>Runs after launch: reports the last install (and copies what its helper did into Martlet's log), removes
    /// finished installers and starts the periodic timer.</summary>
    private async Task StartUpdatesAsync()
    {
        if (store is null) return;
        var result = AppUpdateInstaller.TakeLastResult(store.DataDirectory, Version);
        AppUpdateInstaller.LogLastRun(store.DataDirectory, result);
        if (result is { } last)
        {
            UpdateStatusText.Text = ActionText.Text = last.Message;
            failedInstall = last.Failed;
            if (last.Failed is not null) failedInstallMessage = last.Message;
        }
        besideCleanup = AppUpdateInstaller.CleanUp(store.DataDirectory, Version, installRoot);
        updateTimer.Start();
        if (updateChecksEnabled || updatePreferences.AutoUpdateHosts) await RunUpdateCycleAsync();
    }

    private async Task UpdateTickAsync()
    {
        if (closing || store is null) return;
        FollowOwnHostAsync().Forget();
        if (installAfterHostWork && readyUpdate is not null && HostWorkBlocker() is null && !updateBusy)
        {
            installAfterHostWork = false;
            InstallNow(unattended: false);
            return;
        }
        if (AutoInstallReady && !UpdatePrepared)
        {
            if (stagingTask is not { IsCompleted: false } && stagingCanceledVersion != readyUpdate?.Update.Version)
                StageAutomaticallyAsync().Forget();
        }
        else if (AutoInstallReady && CanInstallNow())
        {
            InstallNow(unattended: true);
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
        installWaitingText = (UpdateStaged
                ? $"Martlet {ready.Update.Version.ToString(3)} is installed beside this one, and Martlet restarts into it in a few " +
                  "seconds once it isn't busy"
                : $"Martlet {ready.Update.Version.ToString(3)} is downloaded and installs as soon as Martlet isn't busy") +
            (InstallBlocker() is { } why ? $" (waiting: {why})." : ".");
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
                if (OwnedWindows.Count == 0 && openConversation is null && !setupOperations.IsRunning)
                {
                    await ConfirmAndInstallAsync(offered, prompted: true);
                    if (closing) return;
                }
                else ActionText.Text = $"Martlet {offered.Version.ToString(3)} is available in Settings.";
            }
            if (AutoInstalling && availableUpdate is { } update && readyUpdate?.Update.Version != update.Version)
                await DownloadUpdateAsync();
            if (AutoInstallReady && !UpdatePrepared && !closing)
            {
                stagingCanceledVersion = null;
                await StageUpdateAsync();
            }
            if (AutoInstallReady && readyUpdate is { } ready && !closing)
            {
                if (UpdatePrepared && CanInstallNow())
                {
                    InstallNow(unattended: true);
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

    /// <summary>What installing an update now would cut short, in words, or null when Martlet may close for it: a reply or
    /// something you are saying, a question waiting for your answer, and work Martlet does for you or for your other computers
    /// (setup tasks, host updates, a command another computer sent, an update check or download, anything else exiting would
    /// interrupt). Martlet's window being in front, its other windows, the character and listening don't hold an update back:
    /// Martlet restarts into it within moments, with the character and listening as they were.
    /// <paramref name="asked"/>: the install is what a martlet.update command from another computer is doing, so that command
    /// doesn't count as work in the way.</summary>
    private string? InstallBlocker(bool asked = false) =>
        closing ? "Martlet is closing"
        : conversation?.Replying == true ? "Martlet is replying"
        : openConversation?.HearingYou == true ? "you're talking to Martlet"
        : ComponentDispatcher.IsThreadModal ? "a Martlet question is waiting for your answer"
        : HostWorkBlocker(asked) is { } work ? work
        : updateBusy ? "an update check or download is running"
        : !asked && nodeAgentBusy ? "Martlet is checking for commands from your other computers"
        // Anything else exiting would cut short, so an unattended install never has to ask on the way out.
        : ExitInterruptions(asked) is [var interrupted, ..] ? char.ToLowerInvariant(interrupted[0]) + interrupted[1..]
        : null;

    /// <summary>Work on hosts, or for your other computers, that closing Martlet would cut off, in words, or null. Even an
    /// install you confirmed waits for it.</summary>
    private string? HostWorkBlocker(bool asked = false) =>
        setupOperations.IsRunning ? "a setup task is running"
        : hostUpdatesRunning || hostUpdates.Running ? "a host service update is running"
        : setupApplying ? "Martlet is reconfiguring your computers"
        : !asked && NodeCommandsRunning.Count > 0 ? $"Martlet is running {NodeCommandsRunningText}"
        : null;

    private bool CanInstallNow(bool asked = false) => InstallBlocker(asked) is null;

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
            QueueSettingsSync();
            if (enabled) RunUpdateCycleAsync().Forget();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            changingUpdateChoice = true;
            AutomaticUpdateCheck.IsChecked = updateChecksEnabled;
            changingUpdateChoice = false;
            UpdateStatusText.Text = "Couldn't save update settings, so check access to Martlet's data folder.";
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
            QueueSettingsSync();
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
            UpdateStatusText.Text = "Couldn't save update settings, so check access to Martlet's data folder.";
        }
    }

    private async void CheckForUpdates_Click(object sender, RoutedEventArgs e) => await CheckForUpdatesAsync(background: false);

    /// <summary>Uses the update choices made on another of the owner's computers (Martlet updates the same way everywhere).</summary>
    private void ApplySharedUpdates(bool checks, UpdatePreferences next)
    {
        if (store is null) return;
        if (checks != updateChecksEnabled) UpdateCheckPreferences.Save(store.DataDirectory, checks);
        if (next != updatePreferences) UpdatePreferences.Save(store.DataDirectory, next);
        var startNow = checks && !updateChecksEnabled || next.AutoInstall && !updatePreferences.AutoInstall ||
            next.AutoUpdateHosts && !updatePreferences.AutoUpdateHosts;
        if (!checks) updateCheckCancellation?.Cancel();
        updateChecksEnabled = checks;
        updatePreferences = next;
        changingUpdateChoice = true;
        AutomaticUpdateCheck.IsChecked = updateChecksEnabled;
        AutomaticUpdateInstall.IsChecked = updatePreferences.AutoInstall;
        AutomaticHostUpdate.IsChecked = updatePreferences.AutoUpdateHosts;
        UpdateIntervalChoice.SelectedItem = UpdateIntervalChoice.Items.OfType<ComboBoxItem>()
            .First(item => (int)item.Tag == updatePreferences.IntervalMinutes);
        changingUpdateChoice = false;
        ShowUpdateControls();
        UpdateStatusText.Text = DescribeUpdateSettings();
        if (startNow && started) RunUpdateCycleAsync().Forget();
    }

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
            var result = SimulatedAppUpdate.Update ??
                await new GitHubReleaseClient(http).CheckAsync(typeof(App).Assembly.GetName().Version!, cancellation.Token);
            if (closing || cancellation.IsCancellationRequested) return;
            availableUpdate = result;
            if (readyUpdate is { } ready && ready.Update.Version != result?.Version) readyUpdate = null;
            ShowInstallButton();
            if (result is null)
            {
                UpdateStatusText.Text = $"You're up to date (checked {DateTime.Now:t}).";
                return;
            }
            UpdateStatusText.Text = UpdateStaged && !AutoInstalling
                ? StagedText(result.Version.ToString(3))
                : $"Martlet {result.Version.ToString(3)} is available ({Mib(result)})." + (AutoInstalling
                    ? " Downloading for automatic install."
                    : installRoot is null
                        ? " Press Install to download and restart."
                        : " Press Install to download it and install it while you keep using Martlet.");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (OperationCanceledException)
        {
            if (!closing) UpdateStatusText.Text = "Update check timed out, and Martlet tries again later.";
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
}
