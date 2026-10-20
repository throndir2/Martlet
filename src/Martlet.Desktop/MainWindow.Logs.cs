using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using Martlet.HostClient;
using Martlet.Core.Logs;
using Martlet.Diagnostics;

namespace Martlet.Desktop;

/// <summary>One line on the Diagnostics page. <see cref="Where"/> names the computer as shown ("This PC · App"),
/// <see cref="Origin"/> also gives its ID ("This PC (diva-host) · Host gateway"). <see cref="Text"/> is its accessible name
/// (time, level, where and the first line of the message).</summary>
internal sealed record LogRow(LogRecord Record, string Time, string Level, int Severity, string Where, string Origin, string Headline,
    string AutomationId, string Text);

/// <summary>Diagnostics: every line Martlet's parts wrote on every computer of the owner's Martlet network (desktop apps, avatar
/// renderers, host runs, hosts' gateways), newest first, with level, computer, part and text filters. Logs are shared between
/// all the computers, with no choice to make: every 30 seconds this PC reads each paired host's new lines (every computer's)
/// into its own copy and gives every host the lines it lacks, its own and every other computer's (<see cref="LogShare"/>), so
/// each PC shows them all. Save logs to share puts everything in one ZIP (<see cref="LogBundle"/>).</summary>
public partial class MainWindow
{
    private const int ShownLogLimit = 5_000;
    private readonly DispatcherTimer logShareTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    private readonly DispatcherTimer logViewTimer = new() { Interval = TimeSpan.FromSeconds(15) };
    private readonly DispatcherTimer logSearchTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private IReadOnlyList<LogRecord> localLogs = [];
    private LogShare? logShare;
    private Task<LogShare?>? logShareLoading;
    private string logLevel = "all", logSource = "all", logPart = "all";
    private bool logsBusy, logsAgain, logShareBusy, logShareSending, logShareSendAgain, logSaving;
    private string logShareStatus = "";
    private string? lastLoggedStatus, lastLoggedShape;
    private DateTimeOffset lastLoggedAt;
    private string logRowsSignature = "";

    private string? LogDirectory => ErrorLog.Directory ?? (store is null ? null : LocalLogs.Directory(store.DataDirectory));

    /// <summary>The other computers' lines this PC holds (empty until the saved copy is read).</summary>
    private IReadOnlyList<LogRecord> NetworkLogLines => logShare?.Logs.Snapshot() ?? [];

    private void InitializeLogs()
    {
        logShareTimer.Tick += (_, _) => ShareLogsAsync(send: true).Forget();
        logViewTimer.Tick += (_, _) => RefreshLogsAsync().Forget();
        logSearchTimer.Tick += (_, _) =>
        {
            logSearchTimer.Stop();
            RenderLogRows();
        };
        ErrorLog.ErrorRecorded += QueueLogRefresh;
        RenderLogFilters();
        ShowLogShareStatus();
    }

    private void StartLogSharing()
    {
        if (store is null || closing) return;
        logShareTimer.Start();
        ShareLogsAsync(send: true).Forget();
    }

    private void StopLogs()
    {
        ErrorLog.ErrorRecorded -= QueueLogRefresh;
        logShareTimer.Stop();
        logViewTimer.Stop();
        logSearchTimer.Stop();
    }
}
