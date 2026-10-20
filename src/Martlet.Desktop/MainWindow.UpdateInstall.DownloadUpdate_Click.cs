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

/// <summary>More of MainWindow.UpdateInstall.cs, in a file of its own so that each file stays under 20 KB.</summary>
public partial class MainWindow
{
    private async void DownloadUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (closing || updateBusy || availableUpdate is not { } update) return;
        await ConfirmAndInstallAsync(update, prompted: false);
    }

    /// <summary>The Install button: hidden when there is no update, Restart once the update is installed beside Martlet.</summary>
    private void ShowInstallButton()
    {
        var update = availableUpdate;
        DownloadUpdateButton.Content = update is null ? "_Install update"
            : UpdateStaged && readyUpdate?.Update.Version == update.Version ? $"_Restart into {update.Version.ToString(3)}"
            : $"_Install {update.Version.ToString(3)} now";
        DownloadUpdateButton.Visibility = ReviewUpdateButton.Visibility = update is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private static string StagedText(string version) =>
        $"Martlet {version} is installed, so restart Martlet to use it (a few seconds), or it switches over when Martlet exits.";

    /// <summary>Asks once, then downloads and verifies the installer. An installed Martlet then installs it beside itself while
    /// you keep using it and asks to restart, which takes a few seconds; a development build (or when that install fails)
    /// closes Martlet, runs the installer and starts again. Work on hosts or for your other computers that is running then
    /// finishes first. Once the update is installed beside Martlet, this only restarts into it (Restart into x.y.z).</summary>
    private async Task ConfirmAndInstallAsync(GitHubUpdate update, bool prompted)
    {
        var version = update.Version.ToString(3);
        // Asked or chosen once: the periodic check doesn't ask again for this version.
        announcedUpdate = update.Tag;
        if (UpdateStaged && readyUpdate?.Update.Version == update.Version)
        {
            // Restart into x.y.z restarts at once; the periodic check only asks.
            if (prompted && !AskToRestart(update))
            {
                UpdateStatusText.Text = ActionText.Text = StagedText(version);
                return;
            }
        }
        else
        {
            var beside = installRoot is not null && stagingFailedVersion != update.Version;
            var busy = BusyReason();
            var work = HostWorkBlocker();
            if (!ConfirmationDialog.Confirm(this,
                    $"Install Martlet {version} ({Mib(update)}) now?\n\n" +
                    (beside
                        ? "It installs in the background while you keep using Martlet, then asks you to restart."
                        : "Martlet closes, installs it and restarts" + (busy is null ? "" : $", and {busy} pauses until then") +
                          (work is null ? "." : $" once this finishes: {work}.")),
                    "Install Martlet update"))
                return;
            if (readyUpdate?.Update.Version != update.Version) await DownloadUpdateAsync(shown: true);
            if (readyUpdate?.Update.Version != update.Version || closing) return;
            if (beside)
            {
                stagingCanceledVersion = null;
                await StageUpdateAsync(shown: true);
                if (readyUpdate?.Update.Version != update.Version || closing) return;
                // Canceled in Background tasks: nothing more happens until you press Install again.
                if (!UpdatePrepared) return;
                if (!UpdateStaged)
                {
                    if (!ConfirmationDialog.Confirm(this,
                            $"Martlet couldn't install {version} while it runs, so install it the usual way?\n\n" +
                            "Martlet closes, installs it and restarts" +
                            (BusyReason() is { } busyNow ? $", and {busyNow} pauses until then." : "."),
                            "Install Martlet update"))
                        return;
                }
                else if (!AskToRestart(update))
                {
                    UpdateStatusText.Text = ActionText.Text = StagedText(version);
                    return;
                }
            }
        }
        if (HostWorkBlocker() is { } still)
        {
            installAfterHostWork = true;
            UpdateStatusText.Text = ActionText.Text = UpdateStaged
                ? $"Martlet restarts into {version} as soon as this finishes: {still}."
                : $"Martlet {version} installs as soon as this finishes: {still}.";
            return;
        }
        InstallNow(unattended: false);
    }

    /// <summary>The update is installed beside Martlet: asks whether to restart into it now; true to restart.</summary>
    private bool AskToRestart(GitHubUpdate update)
    {
        var busy = BusyReason();
        var work = HostWorkBlocker();
        return ConfirmationDialog.Confirm(this,
            $"Restart Martlet now to start using {update.Version.ToString(3)}?\n\n" +
            "The restart takes a few seconds" + (busy is null ? "" : $", and {busy} pauses until then") +
            (work is null ? "." : $"; it waits until this finishes: {work}."),
            "Restart Martlet", yes: "_Restart now", no: "_Later", questionId: "RestartIntoUpdateQuestion");
    }

    /// <summary>Installs the downloaded update beside this Martlet (its program folder's ".next" folder) as a background task,
    /// Install Martlet x.y.z: out of sight unless <paramref name="shown"/>, with no installer window and at idle priority,
    /// while Martlet keeps working. Restarting into it then takes seconds instead of the whole install. When it fails, the
    /// update installs the classic way. Unless you asked for it (<paramref name="shown"/>) or another computer did
    /// (<paramref name="asked"/>), it starts only while Martlet isn't replying or hearing you. Joins one already running.</summary>
    private async Task StageUpdateAsync(bool shown = false, bool asked = false)
    {
        if (stagingTask is { IsCompleted: false } running)
        {
            await running;
            return;
        }
        if (closing || store is null || installRoot is not { } root || readyUpdate is not { } ready || UpdatePrepared) return;
        if (!shown && !asked && (conversation?.Replying == true || openConversation?.HearingYou == true)) return;
        stagingTask = StageAsync(store.DataDirectory, root, ready, shown);
        await stagingTask;
    }

    /// <summary>The automatic install's first step (from the one-minute tick): installs the update beside Martlet, then
    /// restarts into it when Martlet isn't busy.</summary>
    private async Task StageAutomaticallyAsync()
    {
        await StageUpdateAsync();
        if (closing || !AutoInstallReady || !UpdatePrepared) return;
        if (CanInstallNow()) InstallNow(unattended: true);
        else ShowInstallWaiting();
    }

    private async Task StageAsync(string dataDirectory, string root, (string Path, GitHubUpdate Update) ready, bool shown)
    {
        var version = ready.Update.Version.ToString(3);
        var updates = AppUpdateInstaller.UpdatesDirectory(dataDirectory);
        var started = Stopwatch.StartNew();
        string? failure = null;
        BackgroundTask? task = null;
        UpdateStatusText.Text = $"Installing Martlet {version} beside this one, which takes a minute or less while you keep using Martlet.";
        await HostRunWindow.RunAsync(this, $"Install Martlet {version}", async run =>
        {
            task = run.BackgroundTask;
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, run.Token);
            run.Status(UpdateStatusText.Text);
            run.Output.Report($"Installing Martlet {version} into {AppUpdateStaging.StagedRoot(root)} while Martlet keeps running, " +
                "with no installer window and at low priority, so restarting into it takes only a few seconds.");
            try
            {
                await besideCleanup;
                var code = SimulatedAppUpdate.InstallRoot is not null && SimulatedAppUpdate.Update?.Version == ready.Update.Version
                    ? await SimulatedAppUpdate.StageAsync(root, cancellation.Token)
                    : await AppUpdateStaging.PrepareAsync(ready.Path, root, updates, cancellation.Token);
                if (code != 0)
                {
                    var tail = AppUpdateHelper.InstallerLogTail(updates, 15, AppUpdateStaging.StageLogFile);
                    throw new InvalidDataException($"The installer ended with exit code {code}" + (tail.Count == 0 ? "."
                        : $". Last lines of its log ({AppUpdateStaging.StageLogFile} in the updates folder):\n" + string.Join("\n", tail)));
                }
                stagedVersion = ready.Update.Version;
                ErrorLog.Info($"Installed Martlet {version} beside this one in {started.Elapsed.TotalSeconds:0} s; restarting into it takes a few seconds.");
                return $"Martlet {version} is installed, and restarting into it takes a few seconds.";
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception or
                InvalidDataException or InvalidOperationException)
            {
                stagingFailedVersion = ready.Update.Version;
                failure = $"Couldn't install Martlet {version} while Martlet runs, so it installs the usual way: Martlet closes for " +
                    "the whole install.";
                ErrorLog.Warn(failure, error);
                throw new InvalidOperationException(failure, error);
            }
        }, hidden: !shown);
        if (closing) return;
        if (task?.State == BackgroundTaskState.Canceled)
        {
            stagingCanceledVersion = ready.Update.Version;
            UpdateStatusText.Text = $"Installing Martlet {version} was canceled, so press Install to try again.";
        }
        else if (failure is not null) UpdateStatusText.Text = failure;
        else if (!AutoInstalling) UpdateStatusText.Text = StagedText(version);
        ShowInstallButton();
    }

    /// <summary>Closes Martlet; the installer runs once it has exited and then starts Martlet again. <paramref name="unattended"/>
    /// (an automatic install, or one another computer asked for): the installer shows no window at all and Martlet restarts
    /// minimized, so it does not take focus from what you are doing (in the notification area when it was there); its steps
    /// still go to Martlet's log. Otherwise the installer shows its progress window. When the update is already installed
    /// beside Martlet, closing only switches folders and Martlet is back in seconds.</summary>
    private void InstallNow(bool unattended)
    {
        if (readyUpdate is not { } ready || closing) return;
        var version = ready.Update.Version.ToString(3);
        var staged = UpdateStaged;
        installOnExit = (ready.Path, ready.Update.Version, true, unattended);
        resumeAfterInstall = (avatar.IsShowing, openConversation is { HandsFree: true, ListeningStarted: true, Paused: false },
            openConversation is { WatchingStarted: true, Paused: false });
        var again = ResumePhrase(resumeAfterInstall) is { } on ? $", {on} again." : ".";
        ErrorLog.Info(staged
            ? $"Restarting into Martlet {version}, installed beside this one, {(unattended ? "automatically" : "as you confirmed")}. " +
              "Martlet closes, switches folders and starts again in a few seconds" + again
            : $"Installing Martlet {version} {(unattended ? "automatically, with no installer window" : "as you confirmed")}. " +
              "Martlet closes and restarts into it" + again);
        UpdateStatusText.Text = ActionText.Text = staged
            ? $"Restarting into Martlet {version}, back in a few seconds."
            : unattended
                ? $"Installing Martlet {version} in the background with no installer window, and Martlet restarts by itself when it's done."
                : $"Installing Martlet {version}, and Martlet restarts when it's done.";
        ExitMartlet();
    }

    /// <summary>What to pick up again after the restart into an update: the character showing, always listening, watching.</summary>
    private (bool Character, bool Listening, bool Watching) resumeAfterInstall;

    /// <summary>"showing the character, listening and watching" for what is on; null when nothing is.</summary>
    private static string? ResumePhrase((bool Character, bool Listening, bool Watching) on)
    {
        string[] parts = [.. new[] { (on.Character, "showing the character"), (on.Listening, "listening"), (on.Watching, "watching") }
            .Where(part => part.Item1).Select(part => part.Item2)];
        return parts.Length switch
        {
            0 => null,
            1 => parts[0],
            _ => string.Join(", ", parts[..^1]) + " and " + parts[^1]
        };
    }

    /// <summary>At exit: runs the requested install, or without restarting Martlet a downloaded automatic update or one already
    /// installed beside Martlet (you agreed to it; switching to it takes seconds).</summary>
    private void LaunchPendingInstall()
    {
        if (store is null) return;
        var install = installOnExit ?? ((AutoInstallReady || UpdateStaged) && readyUpdate is { } ready
            ? (ready.Path, ready.Update.Version, false, false) : null);
        if (install is not { } run) return;
        var updates = AppUpdateInstaller.UpdatesDirectory(store.DataDirectory);
        var staged = installRoot is not null && stagedVersion == run.Version && AppUpdateStaging.IsStaged(installRoot);
        try
        {
            if (run.Relaunch) AppUpdateResume.Save(updates, resumeAfterInstall.Character, resumeAfterInstall.Listening, DateTimeOffset.UtcNow,
                resumeAfterInstall.Watching);
            AppUpdateInstaller.Launch(run.Installer, run.Version, store.DataDirectory, run.Relaunch, run.Unattended,
                (Application.Current as App)?.DataDirectoryArgument, inTray, installRoot, staged);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException)
        {
            ErrorLog.Warn($"Could not start installing Martlet {run.Version.ToString(3)}", error);
            try { AppUpdateResume.Discard(updates); }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>After restarting into an update: shows the character and starts listening and watching again when they were on as
    /// Martlet closed for it (unless Show at startup or Settings' startup choice already did). A note older than a few minutes,
    /// from an install that didn't restart Martlet, is ignored.</summary>
    private async Task ResumeAfterUpdateAsync()
    {
        if (store is null) return;
        (bool Character, bool Listening, bool Watching)? resume;
        try { resume = AppUpdateResume.Take(AppUpdateInstaller.UpdatesDirectory(store.DataDirectory), DateTimeOffset.UtcNow); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            ErrorLog.Warn("Couldn't read what to pick up again after the update", error);
            return;
        }
        if (resume is not { } again || closing) return;
        if (Role != DeviceRole.Companion)
        {
            ErrorLog.Info("Martlet restarted after its update as a Martlet host, so the character, listening and watching stay off on this PC.");
            return;
        }
        if (again.Character && !avatar.IsShowing) await ShowSavedCharacterAsync(onlyIfAutoShow: false);
        if (closing) return;
        if (again.Listening && Talk.HandsFree && openConversation is not { ListeningStarted: true }) StartListening();
        if (again.Watching && Talk.Watch && openConversation is not { WatchingStarted: true }) StartWatching();
        ErrorLog.Info($"Martlet restarted after its update and is {ResumePhrase(again) ?? "running"} again, as before the update.");
    }

    private void ReviewUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (availableUpdate is not { } update || closing) return;
        try { Process.Start(new ProcessStartInfo(update.ReleasePage.AbsoluteUri) { UseShellExecute = true })?.Dispose(); }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException)
        {
            UpdateStatusText.Text = $"Couldn't open the release notes, so copy this URL into your browser: {update.ReleasePage}";
        }
    }

    private static string UpdateError(Exception error) => error switch
    {
        InvalidDataException or ArgumentException => error.Message,
        HttpRequestException => "The update service could not be reached.",
        UpdateCleanupException => error.Message,
        OperationCanceledException => "The download timed out, and Martlet tries again later.",
        UnauthorizedAccessException or IOException => "Martlet can't save the update, so check access and free space.",
        _ => "The update couldn't be checked or downloaded."
    };
}
