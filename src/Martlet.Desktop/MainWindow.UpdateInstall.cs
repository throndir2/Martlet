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

/// <summary>App updates, part 2: downloading an update, installing it beside Martlet or the classic way, restarting into it and
/// resuming what ran before.</summary>
public partial class MainWindow
{
    /// <summary>Downloads the available installer into Martlet's updates folder and checks it against GitHub's digest, as a
    /// background task (Download Martlet x.y.z): out of sight when Martlet downloads by itself, in its run window when
    /// <paramref name="shown"/> (you chose Install). Cancel task stops it; the next automatic check or Install downloads it
    /// again. Returns whether you canceled it, so a caller that tries again doesn't start it over.</summary>
    private async Task<bool> DownloadUpdateAsync(bool shown = false)
    {
        if (closing || updateBusy || store is null || availableUpdate is not { } update) return false;
        updateBusy = true;
        updateDrain = new(TaskCreationOptions.RunContinuationsAsynchronously);
        DownloadUpdateButton.IsEnabled = CheckForUpdatesButton.IsEnabled = false;
        var version = update.Version.ToString(3);
        var directory = store.DataDirectory;
        UpdateStatusText.Text = $"Downloading Martlet {version} ({Mib(update)})...";
        string? failure = null;
        BackgroundTask? task = null;
        try
        {
            await HostRunWindow.RunAsync(this, $"Download Martlet {version}", async run =>
            {
                task = run.BackgroundTask;
                using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, run.Token);
                updateDownloadCancellation = cancellation;
                try
                {
                    run.Status(UpdateStatusText.Text);
                    run.Output.Report($"Downloading {update.AssetName} ({Mib(update)}) from Martlet's GitHub Releases and checking it " +
                        "against GitHub's SHA-256 digest...");
                    var received = new Progress<long>(bytes =>
                    {
                        var text = $"Downloading Martlet {version}: {bytes * 100 / Math.Max(1, update.Bytes)}% of {Mib(update)}...";
                        run.Status(text);
                        if (!closing) UpdateStatusText.Text = text;
                    });
                    using var http = GitHubReleaseClient.CreateHttpClient();
                    var path = SimulatedAppUpdate.Update?.Version == update.Version
                        ? SimulatedAppUpdate.Write(directory, update)
                        : await AppUpdateInstaller.DownloadAsync(new GitHubReleaseClient(http), update, directory, cancellation.Token, received);
                    readyUpdate = (path, update);
                    run.Output.Report("The download matches GitHub's SHA-256 digest, which detects a damaged download but not who " +
                        "published it (the installer is not code-signed).");
                    if (!closing) UpdateStatusText.Text = $"Martlet {version} is ready to install.";
                    return $"Martlet {version} is downloaded and ready to install.";
                }
                // Canceled by you or by Martlet exiting: the task shows it was canceled.
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
                catch (Exception error) when (error is HttpRequestException or InvalidDataException or IOException or
                    UnauthorizedAccessException or ArgumentException or OperationCanceledException)
                {
                    if (error is UpdateCleanupException) interruptedUpdateCleanup = error.Message;
                    failure = $"Couldn't download update: {UpdateError(error)}";
                    throw new InvalidOperationException(failure, error);
                }
                finally { updateDownloadCancellation = null; }
            }, hidden: !shown);
            if (task?.State == BackgroundTaskState.Canceled && !closing) UpdateStatusText.Text = $"The download of Martlet {version} was canceled.";
        }
        finally
        {
            updateBusy = false;
            if (!closing)
            {
                CheckForUpdatesButton.IsEnabled = DownloadUpdateButton.IsEnabled = true;
                if (failure is not null) UpdateStatusText.Text = failure;
            }
            updateDrain.TrySetResult();
            updateDrain = null;
        }
        return task?.State == BackgroundTaskState.Canceled && !closing;
    }
}
