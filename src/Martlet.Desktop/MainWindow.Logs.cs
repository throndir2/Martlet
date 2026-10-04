using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using Martlet.Avatar.Audio2Face.Remote;
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

    private void QueueLogRefresh() => Dispatcher.BeginInvoke(() =>
    {
        if (!closing && DiagnosticsPage.IsVisible) RefreshLogsAsync(remote: false).Forget();
    });

    /// <summary>Records each status-line message in the desktop log, so the Diagnostics page shows what Martlet reported.
    /// A message that only differs in its numbers (download progress) is recorded at most every 10 seconds.</summary>
    private void LogStatusLine(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text == lastLoggedStatus) return;
        var shape = new string(text.Where(c => !char.IsAsciiDigit(c)).ToArray());
        var now = DateTimeOffset.UtcNow;
        if (shape == lastLoggedShape && now - lastLoggedAt < TimeSpan.FromSeconds(10)) return;
        lastLoggedStatus = text;
        lastLoggedShape = shape;
        lastLoggedAt = now;
        ErrorLog.Info("Status: " + text);
    }

    private void EnterDiagnostics()
    {
        ShowLogShareStatus();
        logViewTimer.Start();
        RefreshLogsAsync().Forget();
    }

    private void LeaveDiagnostics() => logViewTimer.Stop();

    /// <summary>Reads the logs again (this PC's, and each paired host's new lines); sends nothing.</summary>
    private void LogsRefresh_Click(object sender, RoutedEventArgs e) => RefreshLogsAsync().Forget();

    private void LogsOpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (!ErrorLog.OpenFolder()) ActionText.Text = "The logs folder isn't available. Check access to Martlet's data folder.";
    }

    private void LogsCopy_Click(object sender, RoutedEventArgs e)
    {
        var rows = LogList.ItemsSource as IReadOnlyList<LogRow> ?? [];
        if (rows.Count == 0)
        {
            ActionText.Text = "No log lines are shown, so nothing was copied.";
            return;
        }
        var text = string.Join(Environment.NewLine, rows.Select(r => $"{r.Record.At.ToLocalTime():yyyy-MM-dd HH:mm:ss.fff} {r.Level} {r.Origin}: {r.Record.Message}"));
        if (text.Length > 4_000_000) text = text[..4_000_000];
        try
        {
            Clipboard.SetText(text);
            ActionText.Text = $"Copied {rows.Count:N0} log line{(rows.Count == 1 ? "" : "s")}.";
        }
        catch (ExternalException)
        {
            ActionText.Text = "Another app is using the clipboard. Try again.";
        }
    }

    /// <summary>Saves every line this PC has, from every computer (all filters ignored), in one ZIP where the owner chooses
    /// (the Desktop by default), then shows it in File Explorer so it can be attached to a message or an issue.</summary>
    private async void LogsSave_Click(object sender, RoutedEventArgs e)
    {
        if (logSaving) return;
        var directory = LogDirectory;
        var now = DateTimeOffset.Now;
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Save Martlet's logs to share",
            Filter = "ZIP archive (*.zip)|*.zip",
            DefaultExt = ".zip",
            AddExtension = true,
            OverwritePrompt = true,
            FileName = LogBundle.SuggestedFileName(ClusterDevice, now),
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)
        };
        if (dialog.ShowDialog(this) != true) return;
        var path = dialog.FileName;
        logSaving = true;
        LogsSaveButton.IsEnabled = false;
        try
        {
            var share = await LogShareAsync();
            var info = new LogBundleInfo(ClusterDevice, ThisPcSources(), AppVersions.Current, RuntimeInformation.OSDescription,
                logShareStatus.Length > 0 ? logShareStatus : null, now);
            var summary = await Task.Run(() =>
            {
                var own = directory is null ? [] : LocalLogs.Read(directory, ClusterDevice);
                return LogBundle.Save(path, own.Concat(share?.Logs.Snapshot() ?? []), info, overwrite: true);
            });
            ErrorLog.Info($"Saved {summary.Lines:N0} log lines from {summary.Computers} computer{(summary.Computers == 1 ? "" : "s")} to share " +
                $"({summary.Bytes:N0} bytes).");
            ActionText.Text = $"Saved {summary.Lines:N0} lines from {summary.Computers} computer{(summary.Computers == 1 ? "" : "s")} in " +
                $"{Path.GetFileName(path)}. Attach it to your message. It never holds keys or what you said, but can include local paths, " +
                "computer names and provider error text.";
            try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = false })?.Dispose(); }
            catch (System.ComponentModel.Win32Exception) { }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            ActionText.Text = "Couldn't save the logs there: " + error.Message;
        }
        finally
        {
            logSaving = false;
            if (!closing) LogsSaveButton.IsEnabled = true;
        }
    }

    private void LogSearch_Changed(object sender, TextChangedEventArgs e)
    {
        logSearchTimer.Stop();
        logSearchTimer.Start();
    }

    private void LogList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LogList.SelectedItem is not LogRow row)
        {
            LogDetail.Text = "";
            LogDetail.Visibility = Visibility.Collapsed;
            return;
        }
        var record = row.Record;
        LogDetail.Visibility = Visibility.Visible;
        LogDetail.Text = $"{record.At.ToLocalTime():yyyy-MM-dd HH:mm:ss.fff zzz} · {record.Level} · {row.Origin}" +
            (record.RelayedBy is { } by ? $" · passed on by {by}" : "") + Environment.NewLine + Environment.NewLine + record.Message;
    }

    // ---------- sharing with every computer ----------

    /// <summary>The log sharing engine, with the other computers' lines this PC saved (read once, off the UI thread).</summary>
    private Task<LogShare?> LogShareAsync()
    {
        if (logShare is not null) return Task.FromResult<LogShare?>(logShare);
        if (store is null || LogDirectory is not { } directory) return Task.FromResult<LogShare?>(null);
        return logShareLoading ??= LoadLogShareAsync(directory);
    }

    private async Task<LogShare?> LoadLogShareAsync(string directory)
    {
        var device = ClusterDevice;
        var share = await Task.Run(() => new LogShare(directory, device));
        if (share.Logs.LoadState == "unreadable")
            ErrorLog.Warn($"The copy of your other computers' logs ({NetworkLogs.FileName}) couldn't be read; it fills again from your hosts.");
        logShare = share;
        if (!closing && DiagnosticsPage.IsVisible)
        {
            RenderLogFilters();
            RenderLogRows();
        }
        return share;
    }

    private void ShowLogShareStatus()
    {
        LogShareStatus.Text = store is null ? "Unavailable without a local data folder."
            : logShareStatus.Length > 0 ? logShareStatus
            : "Checking your hosts...";
    }

    /// <summary>One sharing run with every paired host: reads their new lines into this PC's copy and, when
    /// <paramref name="send"/>, gives each host what it lacks (this PC's own new lines and every other computer's). Never throws;
    /// the outcome is shown on the Diagnostics page.</summary>
    private async Task ShareLogsAsync(bool send)
    {
        if (closing || store is null) return;
        if (logShareBusy)
        {
            // A read for the page is running: the 30-second run follows it rather than waiting another 30 seconds.
            if (send && !logShareSending) logShareSendAgain = true;
            return;
        }
        logShareBusy = true;
        logShareSending = send;
        var token = lifetime.Token;
        var peers = Array.Empty<HostLogPeer>();
        try
        {
            if (await LogShareAsync() is not { } share || closing) return;
            var hosts = NetworkMap.Hosts(Inputs());
            if (hosts.Count == 0)
            {
                logShareStatus = "No Martlet hosts are paired with this PC yet, so it shows only its own logs. Pair one in Devices › Add a computer " +
                    "and every computer's logs are shared.";
                return;
            }
            peers = hosts.Select(h => new HostLogPeer(h.HostId, () => ClusterSync.Connect(h.Pairing))).ToArray();
            var directory = LogDirectory;
            var device = ClusterDevice;
            var list = peers;
            var result = await Task.Run(async () =>
            {
                var local = send && directory is not null ? LocalLogs.Read(directory, device, 512 * 1024) : [];
                return await share.RunAsync(list, local, send, DateTimeOffset.UtcNow, token);
            }, token);
            logShareStatus = result.Describe() + $" Checked at {DateTime.Now:t}.";
            if (result.Received > 0 && !closing && DiagnosticsPage.IsVisible)
            {
                RenderLogFilters();
                RenderLogRows();
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            foreach (var peer in peers) peer.Dispose();
            logShareBusy = false;
            if (!closing && DiagnosticsPage.IsVisible) ShowLogShareStatus();
            if (logShareSendAgain && !closing)
            {
                logShareSendAgain = false;
                ShareLogsAsync(send: true).Forget();
            }
        }
    }

    // ---------- reading and showing ----------

    /// <summary>Reads this PC's logs and, unless <paramref name="remote"/> is false, each paired host's new lines, then shows
    /// them with every other computer's lines this PC holds.</summary>
    private async Task RefreshLogsAsync(bool remote = true)
    {
        if (closing) return;
        if (logsBusy)
        {
            logsAgain = true;
            return;
        }
        logsBusy = true;
        LogsRefreshButton.IsEnabled = false;
        try
        {
            do
            {
                logsAgain = false;
                var directory = LogDirectory;
                localLogs = directory is null ? [] : await Task.Run(() => LocalLogs.Read(directory, ClusterDevice));
                if (remote && store is not null) await ShareLogsAsync(send: false);
                else await LogShareAsync();
                if (closing) return;
                RenderLogFilters();
                RenderLogRows();
            } while (logsAgain && !closing);
        }
        catch (OperationCanceledException) { }
        finally
        {
            logsBusy = false;
            if (!closing) LogsRefreshButton.IsEnabled = true;
        }
    }

    /// <summary>Every known line once (this PC's own copy wins), newest first.</summary>
    private List<LogRecord> AllLogs()
    {
        var seen = new HashSet<(string, long)>();
        var all = new List<LogRecord>();
        foreach (var record in localLogs.Concat(NetworkLogLines))
            if (seen.Add((record.Stream, record.Seq))) all.Add(record);
        all.Sort((a, b) => b.At != a.At ? b.At.CompareTo(a.At) : b.Seq.CompareTo(a.Seq));
        return all;
    }

    private string SourceName(string source, IReadOnlyCollection<string> thisPc) =>
        source == ClusterDevice ? $"This PC ({string.Join(", ", thisPc)})" : source;

    /// <summary>The log sources that are this PC: its desktop app (<see cref="ClusterDevice"/>, first) and its own host
    /// service, whose gateway writes under its host ID (paired here, or read from Docker on a host PC).</summary>
    private IReadOnlyList<string> ThisPcSources()
    {
        var ids = new List<string> { ClusterDevice };
        foreach (var id in new[] { ThisPcHost()?.HostId, hostState?.HostId })
            if (id is not null && !ids.Contains(id, StringComparer.Ordinal)) ids.Add(id);
        return ids;
    }

    /// <summary>The computer a source belongs to: this PC's sources all count as <see cref="ClusterDevice"/>.</summary>
    private static string Computer(string source, IReadOnlyList<string> thisPc) =>
        thisPc.Contains(source, StringComparer.Ordinal) ? ClusterDevice : source;

    private static string PartName(string component) => component switch
    {
        LogComponents.Desktop => "App",
        LogComponents.AvatarRenderer => "Character",
        LogComponents.HostRuns => "Host runs",
        LogComponents.Gateway => "Host gateway",
        _ => component
    };

    private void RenderLogFilters()
    {
        var all = localLogs.Concat(NetworkLogLines).ToArray();
        var thisPc = ThisPcSources();
        var sources = all.Select(r => Computer(r.Source, thisPc)).Append(ClusterDevice).Distinct(StringComparer.Ordinal)
            .OrderBy(s => s != ClusterDevice).ThenBy(s => s, StringComparer.Ordinal).ToArray();
        var parts = LogComponents.Local.Append(LogComponents.Gateway).Concat(all.Select(r => r.Component)).Distinct(StringComparer.Ordinal).ToArray();
        if (!sources.Contains(logSource) && logSource != "all") logSource = "all";
        if (!parts.Contains(logPart) && logPart != "all") logPart = "all";
        FillFilters(LogLevelFilters, "LogLevel-", "Show",
            [("all", "Everything"), ("warnings", "Warnings and errors"), ("errors", "Errors only")], logLevel, value => logLevel = value);
        FillFilters(LogSourceFilters, "LogSource-", "From",
            [("all", "All computers"), .. sources.Select(s => (s, SourceName(s, thisPc)))], logSource, value => logSource = value);
        FillFilters(LogPartFilters, "LogPart-", "Part",
            [("all", "All parts"), .. parts.Select(p => (p, PartName(p)))], logPart, value => logPart = value);
    }

    private void FillFilters(WrapPanel panel, string prefix, string label, IReadOnlyList<(string Value, string Text)> choices,
        string current, Action<string> choose)
    {
        var signature = string.Join("|", choices.Select(c => c.Value + "=" + c.Text));
        if (panel.Tag as string == signature)
        {
            foreach (var option in panel.Children.OfType<RadioButton>())
                option.IsChecked = AutomationProperties.GetAutomationId(option) == prefix + current;
            return;
        }
        panel.Tag = signature;
        panel.Children.Clear();
        var caption = new TextBlock { Text = label, Width = 44, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 6) };
        caption.SetResourceReference(StyleProperty, "Muted");
        panel.Children.Add(caption);
        foreach (var (value, text) in choices)
        {
            var option = new RadioButton { Content = text, GroupName = prefix, IsChecked = value == current, FontSize = 12 };
            option.SetResourceReference(StyleProperty, "FilterPill");
            AutomationProperties.SetAutomationId(option, prefix + value);
            AutomationProperties.SetName(option, $"{label}: {text}");
            option.Checked += (_, _) =>
            {
                choose(value);
                RenderLogRows();
            };
            panel.Children.Add(option);
        }
    }

    private void RenderLogRows()
    {
        var all = AllLogs();
        var thisPc = ThisPcSources();
        var minimum = logLevel switch { "errors" => 2, "warnings" => 1, _ => 0 };
        var search = LogSearch.Text.Trim();
        var matching = all.Where(r => LogLevels.Rank(r.Level) >= minimum &&
                (logSource == "all" || Computer(r.Source, thisPc) == logSource) &&
                (logPart == "all" || r.Component == logPart) &&
                (search.Length == 0 || r.Message.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                    r.Source.Contains(search, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        var shown = matching.Take(ShownLogLimit).ToList();
        var signature = $"{all.Count}|{(all.Count > 0 ? all[0].Stream + all[0].Seq : "")}|{minimum}|{logSource}|{logPart}|{search}|{string.Join(",", thisPc)}";
        if (signature != logRowsSignature)
        {
            logRowsSignature = signature;
            var selected = (LogList.SelectedItem as LogRow)?.Record;
            var rows = shown.Select((r, i) =>
            {
                var time = r.At.ToLocalTime().ToString(r.At.ToLocalTime().Date == DateTime.Today ? "HH:mm:ss.fff" : "MMM d HH:mm:ss");
                var mine = thisPc.Contains(r.Source, StringComparer.Ordinal);
                var where = $"{(mine ? "This PC" : r.Source)} · {PartName(r.Component)}";
                var origin = $"{(mine ? $"This PC ({r.Source})" : r.Source)} · {PartName(r.Component)}";
                var headline = r.Headline.Length > 400 ? r.Headline[..400] + "…" : r.Headline;
                return new LogRow(r, time, r.Level, LogLevels.Rank(r.Level), where, origin, headline, $"LogEntry-{i}",
                    $"{time} {r.Level} {where}: {(headline.Length > 240 ? headline[..240] + "…" : headline)}");
            }).ToList();
            LogList.ItemsSource = rows;
            if (selected is not null && rows.FirstOrDefault(r => r.Record.Stream == selected.Stream && r.Record.Seq == selected.Seq) is { } again)
                LogList.SelectedItem = again;
        }
        var day = DateTimeOffset.Now - TimeSpan.FromDays(1);
        var recent = all.Where(r => r.At >= day).ToArray();
        var errors = recent.Count(r => LogLevels.Rank(r.Level) >= 2);
        var warnings = recent.Count(r => LogLevels.Rank(r.Level) == 1);
        var computers = all.Select(r => Computer(r.Source, thisPc)).Distinct(StringComparer.Ordinal).Count();
        LogSummary.Text = (all.Count == 0 ? "No log lines yet." :
            $"Showing {shown.Count:N0} of {all.Count:N0} lines from {computers} computer{(computers == 1 ? "" : "s")}" +
            (matching.Count > shown.Count ? $" (the newest {ShownLogLimit:N0} that match)" : "") +
            $". Last 24 hours: {errors:N0} error{(errors == 1 ? "" : "s")}, {warnings:N0} warning{(warnings == 1 ? "" : "s")}.");
    }
}
