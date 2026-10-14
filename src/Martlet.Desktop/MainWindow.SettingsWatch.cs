using System.IO;
using System.Windows.Threading;
using Martlet.Core.Settings;

namespace Martlet.Desktop;

/// <summary>Follows settings.json when it changes: a short pause after the last change, Martlet reads it again, so Home, the
/// open Companion page and an open conversation (before its next reply) use it without a restart. <see cref="settingsChanges"/>
/// knows where each version came from: a version Martlet applied already (after a page's save or a setting taken from another
/// computer) is not applied again, and only a version this Martlet didn't write (Martlet's MCP server, such as character_create
/// or settings_set, or an edit by hand) is logged as a change from outside.</summary>
public partial class MainWindow
{
    private FileSystemWatcher? settingsWatcher;
    private readonly DispatcherTimer settingsFileTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };
    /// <summary>Where each version of settings.json came from and which one Martlet applied (local edit, settings sync, account
    /// switch or the file).</summary>
    private readonly SettingsChanges settingsChanges = new();

    private void StartSettingsWatch()
    {
        if (store is null || closing || settingsWatcher is not null) return;
        settingsFileTimer.Tick += SettingsFileTimer_Tick;
        try
        {
            Directory.CreateDirectory(store.DataDirectory);
            var watcher = new FileSystemWatcher(store.DataDirectory, "settings.json")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.CreationTime
            };
            // Settings are written to a new file that then replaces settings.json.
            watcher.Changed += SettingsFileChanged;
            watcher.Created += SettingsFileChanged;
            watcher.Renamed += SettingsFileChanged;
            watcher.EnableRaisingEvents = true;
            settingsWatcher = watcher;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or PlatformNotSupportedException)
        {
            ErrorLog.Warn("Settings changed outside Martlet won't show until it starts again: couldn't watch settings.json", error);
        }
    }

    private void StopSettingsWatch()
    {
        settingsFileTimer.Stop();
        settingsWatcher?.Dispose();
        settingsWatcher = null;
    }

    private void SettingsFileChanged(object sender, FileSystemEventArgs e) =>
        Dispatcher.BeginInvoke(() =>
        {
            if (closing) return;
            settingsFileTimer.Stop();
            settingsFileTimer.Start();
        });

    private async void SettingsFileTimer_Tick(object? sender, EventArgs e)
    {
        settingsFileTimer.Stop();
        if (closing || store is null) return;
        // Setup or another refresh is still writing or reading: look again shortly, so the newest file is read once.
        if (refreshingHome || setupOperations.IsRunning)
        {
            settingsFileTimer.Start();
            return;
        }
        SettingsLoadResult loaded;
        try { loaded = await store.LoadAsync(lifetime.Token); }
        catch (OperationCanceledException) { return; }
        if (closing || loaded.Revision is null || settingsChanges.IsApplied(loaded.Revision)) return;
        var source = settingsChanges.SourceOf(loaded.Revision);
        if (source == SettingsChangeSource.File) ErrorLog.Info("settings.json changed; Martlet read it again.");
        await RefreshHomeAsync(source);
    }
}
