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
}
