using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using Martlet.Avatar.Audio2Face.Remote;
using Martlet.Core.Cluster;
using Martlet.Core.Contracts;
using Martlet.Core.Logs;
using Martlet.Diagnostics;

namespace Martlet.Desktop;

/// <summary>One line on the Diagnostics page. <see cref="Text"/> is its accessible name (time, level, where and the first
/// line of the message).</summary>
internal sealed record LogRow(LogRecord Record, string Time, string Level, int Severity, string Where, string Headline,
    string AutomationId, string Text);

/// <summary>Diagnostics: every line Martlet's parts wrote (desktop app, avatar renderer, host runs, hosts' gateways), newest
/// first, with level, computer, part and text filters. Each computer keeps its own logs. The owner can choose one paired host
/// as the log host (the "logs" entry of the shared plan): every companion PC then sends it this PC's new lines and relays
/// the other paired hosts' own lines every 30 seconds, so the page shows all computers' logs in one place. The log host
/// keeps each line once (per computer and part, only lines newer than the ones it has).</summary>
public partial class MainWindow
{
    private const int LogsPerStream = 400;
    private const int ShownLogLimit = 5_000;
    private const int CachedRemoteLogLimit = 20_000;
    private const int LogBatchBudget = 360 * 1024;
    private readonly DispatcherTimer logShipTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    private readonly DispatcherTimer logViewTimer = new() { Interval = TimeSpan.FromSeconds(15) };
    private readonly DispatcherTimer logSearchTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private IReadOnlyList<LogRecord> localLogs = [];
    private readonly List<LogRecord> collectedLogs = [];
    private string? collectedFrom;
    private long collectedAfter;
    private readonly Dictionary<string, List<LogRecord>> hostOwnLogs = new(StringComparer.Ordinal);
    private Dictionary<string, long>? logMarks;
    private string? logMarksHost;
    private string logLevel = "all", logSource = "all", logPart = "all";
    private bool logsBusy, logsAgain, logShipBusy, choosingLogHost;
    private string logShipStatus = "";
    private string? logReadNote;
    private string? lastLoggedStatus, lastLoggedShape;
    private DateTimeOffset lastLoggedAt;
    private string logRowsSignature = "";
    private string? logHostSignature;

    private string? LogHostId => clusterPlan.For(ClusterJobs.Logs)?.HostId;

    private string? LogDirectory => ErrorLog.Directory ?? (store is null ? null : LocalLogs.Directory(store.DataDirectory));

    private void InitializeLogs()
    {
        logShipTimer.Tick += (_, _) => ShipLogsAsync().Forget();
        logViewTimer.Tick += (_, _) => RefreshLogsAsync().Forget();
        logSearchTimer.Tick += (_, _) =>
        {
            logSearchTimer.Stop();
            RenderLogRows();
        };
        ErrorLog.ErrorRecorded += QueueLogRefresh;
        RenderLogFilters();
        ShowLogHostStatus();
    }

    private void StartLogShipping()
    {
        if (store is null || closing) return;
        logShipTimer.Start();
        ShipLogsAsync().Forget();
    }

    private void StopLogs()
    {
        ErrorLog.ErrorRecorded -= QueueLogRefresh;
        logShipTimer.Stop();
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
        RenderLogHostChoice();
        ShowLogHostStatus();
        logViewTimer.Start();
        RefreshLogsAsync().Forget();
    }

    private void LeaveDiagnostics() => logViewTimer.Stop();

    /// <summary>Reads the logs again (this PC's, and the log host's or each paired host's own); sends nothing.</summary>
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
        var text = string.Join(Environment.NewLine, rows.Select(r => $"{r.Record.At.ToLocalTime():yyyy-MM-dd HH:mm:ss.fff} {r.Level} {r.Where}: {r.Record.Message}"));
        if (text.Length > 4_000_000) text = text[..4_000_000];
        try
        {
            Clipboard.SetText(text);
            ActionText.Text = $"Copied {rows.Count:N0} log line{(rows.Count == 1 ? "" : "s")}.";
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            ActionText.Text = "Another app is using the clipboard; try Copy shown again.";
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
        LogDetail.Text = $"{record.At.ToLocalTime():yyyy-MM-dd HH:mm:ss.fff zzz} · {record.Level} · {row.Where}" +
            (record.RelayedBy is { } by ? $" · passed on by {by}" : "") + Environment.NewLine + Environment.NewLine + record.Message;
    }

    // ---------- the log host ----------

    private void RenderLogHostChoice()
    {
        var current = LogHostId;
        var hosts = NetworkMap.Hosts(Inputs()).Select(host => (host.HostId,
            Roles: clusterProbes.GetValueOrDefault(host.HostId)?.Routes is { } routes ? ClusterSync.Roles(routes).Count : -1)).ToArray();
        var signature = current + "|" + string.Join(",", hosts.Select(h => h.HostId + ":" + h.Roles)) + "|" + (store is null);
        if (LogHostChoice.IsDropDownOpen || signature == logHostSignature) return;
        logHostSignature = signature;
        choosingLogHost = true;
        try
        {
            LogHostChoice.Items.Clear();
            var none = new ComboBoxItem { Content = "Nobody: each computer keeps its own", Tag = null };
            LogHostChoice.Items.Add(none);
            var selected = none;
            foreach (var (hostId, roles) in hosts)
            {
                var item = new ComboBoxItem { Content = hostId + (roles == 0 ? " (runs no jobs)" : ""), Tag = hostId };
                LogHostChoice.Items.Add(item);
                if (hostId == current) selected = item;
            }
            if (current is not null && ReferenceEquals(selected, none))
            {
                selected = new ComboBoxItem { Content = current + " (not paired with this PC)", Tag = current };
                LogHostChoice.Items.Add(selected);
            }
            LogHostChoice.SelectedItem = selected;
            LogHostChoice.IsEnabled = store is not null;
        }
        finally { choosingLogHost = false; }
    }

    private void LogHost_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (choosingLogHost || store is null || LogHostChoice.SelectedItem is not ComboBoxItem { Tag: var tag }) return;
        var hostId = tag as string;
        if (hostId == LogHostId) return;
        clusterPlan = clusterPlan.Assign(ClusterJobs.Logs, hostId, false, false, null, ClusterDevice, DateTimeOffset.UtcNow);
        SaveClusterPlan();
        QueueClusterSync();
        logMarks = null;
        logShipStatus = "";
        ErrorLog.Info(hostId is null ? "Log host cleared: each computer keeps only its own logs."
            : $"Log host set to {hostId}: companion PCs send it their logs and pass on every other host's log.");
        ActionText.Text = hostId is null ? "Each computer keeps only its own logs now."
            : $"{hostId} now collects the logs of all your computers. This PC sends its logs now and every 30 seconds.";
        ShowLogHostStatus();
        ShipLogsAsync().Forget();
        RefreshLogsAsync().Forget();
    }

    private void ShowLogHostStatus()
    {
        var host = LogHostId;
        LogHostStatus.Text = store is null ? "Unavailable without a local data folder."
            : host is null ? "Nobody collects logs: this page shows this PC's logs and each paired host's own log. Choose a host to also see your other companion PCs' logs here."
            : logShipStatus.Length > 0 ? logShipStatus
            : $"{host} collects the logs of all your computers. This PC sends its logs within 30 seconds.";
    }

    /// <summary>Sends this PC's new log lines to the log host and passes on each other paired host's own new lines, starting
    /// after the newest line the log host already has from each (its marks). The first send from a PC includes at most the
    /// last week. Never throws; the outcome is shown under the log host choice.</summary>
    private async Task ShipLogsAsync()
    {
        if (logShipBusy || closing || store is null) return;
        var hostId = LogHostId;
        if (hostId is null)
        {
            logShipStatus = "";
            if (DiagnosticsPage.IsVisible) ShowLogHostStatus();
            return;
        }
        var hosts = NetworkMap.Hosts(Inputs());
        if (hosts.FirstOrDefault(h => h.HostId == hostId) is not { } target)
        {
            logShipStatus = $"{hostId} collects your logs, but it isn't paired with this PC, so this PC's logs stay here. Pair it on this PC (Devices › Add a computer) to send them.";
            ShowLogHostStatus();
            return;
        }
        logShipBusy = true;
        var token = lifetime.Token;
        try
        {
            if (logMarksHost != hostId)
            {
                logMarks = null;
                logMarksHost = hostId;
            }
            var others = hosts.Where(h => h.HostId != hostId).ToArray();
            var streams = LogComponents.Local.Select(c => new LogStream { Source = ClusterDevice, Component = c })
                .Concat(others.Select(h => new LogStream { Source = h.HostId, Component = LogComponents.Gateway }))
                .Take(LogBatch.MaximumStreams).ToArray();
            if (logMarks is null)
            {
                var (_, known) = await ClusterSync.WithConnectionAsync(target.Pairing,
                    c => c.PushLogsAsync(new LogBatch { SchemaVersion = 1, Streams = streams }, token));
                logMarks = known.ToDictionary(m => m.Source + "/" + m.Component, m => m.Seq, StringComparer.Ordinal);
            }
            var marks = logMarks;
            var directory = LogDirectory;
            var since = DateTimeOffset.UtcNow - TimeSpan.FromDays(7);
            var pending = new List<List<LogRecord>>();
            if (directory is not null)
            {
                var local = await Task.Run(() => LocalLogs.Read(directory, ClusterDevice, 512 * 1024), token);
                foreach (var stream in local.GroupBy(r => r.Stream))
                {
                    var mark = marks.GetValueOrDefault(stream.Key);
                    pending.Add(stream.Where(r => r.Seq > mark && (mark > 0 || r.At >= since)).OrderBy(r => r.Seq).Take(LogsPerStream).ToList());
                }
            }
            var unreachable = new List<string>();
            var older = new List<string>();
            foreach (var host in others)
            {
                try
                {
                    var page = await ClusterSync.WithConnectionAsync(host.Pairing,
                        c => c.ReadOwnLogsAsync(marks.GetValueOrDefault(host.HostId + "/" + LogComponents.Gateway), LogsPerStream, token));
                    pending.Add(page.Entries.Where(r => r.Source == host.HostId && r.Component == LogComponents.Gateway)
                        .Select(r => r with { RelayedBy = null }).OrderBy(r => r.Seq).ToList());
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Audio2FaceHostException error) when (error.Code is "request.invalid" or "request.not_found" or "route.not_found")
                {
                    older.Add(host.HostId);
                }
                catch (Exception error) when (error is OperationCanceledException || ClusterSync.IsHostFailure(error))
                {
                    unreachable.Add(host.HostId);
                }
            }
            var entries = FitLogBatch(pending);
            var accepted = 0;
            if (entries.Count > 0)
            {
                var (kept, updated) = await ClusterSync.WithConnectionAsync(target.Pairing,
                    c => c.PushLogsAsync(new LogBatch { SchemaVersion = 1, Streams = streams, Entries = entries }, token));
                accepted = kept;
                foreach (var mark in updated) marks[mark.Source + "/" + mark.Component] = mark.Seq;
            }
            var waiting = pending.Sum(p => p.Count) - entries.Count;
            logShipStatus = $"{hostId} collects the logs of all your computers. Checked {DateTime.Now:t}: sent {accepted:N0} new line{(accepted == 1 ? "" : "s")}" +
                (others.Length > 0 ? $", including {others.Length - unreachable.Count - older.Count} other host{(others.Length == 1 ? "" : "s")}' own log" : "") + "." +
                (waiting > 0 ? $" {waiting:N0} more go with the next send." : "") +
                (unreachable.Count > 0 ? $" {string.Join(", ", unreachable)} didn't answer; {(unreachable.Count == 1 ? "its" : "their")} lines follow when {(unreachable.Count == 1 ? "it answers" : "they answer")}." : "") +
                (older.Count > 0 ? $" {string.Join(", ", older)} {(older.Count == 1 ? "runs" : "run")} an older Martlet without a shared log; update {(older.Count == 1 ? "it" : "them")}." : "");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Audio2FaceHostException error) when (error.Code is "request.invalid" or "request.not_found" or "route.not_found")
        {
            logMarks = null;
            logShipStatus = $"{hostId} runs an older Martlet that can't collect logs. Update it (Devices › {hostId} › Update host), or choose another host.";
        }
        catch (Exception error) when (error is OperationCanceledException || ClusterSync.IsHostFailure(error))
        {
            logMarks = null;
            logShipStatus = $"{hostId} didn't take this PC's logs at {DateTime.Now:t} ({error.Message.TrimEnd('.')}). They stay on this PC and are sent when it answers again.";
        }
        finally
        {
            logShipBusy = false;
            if (!closing) ShowLogHostStatus();
        }
    }

    /// <summary>Takes the oldest pending lines across streams that fit one batch. Each stream gives a prefix of its lines (in
    /// order), so the log host's mark never skips a line that wasn't sent.</summary>
    private static List<LogRecord> FitLogBatch(List<List<LogRecord>> pending)
    {
        var result = new List<LogRecord>();
        var next = new int[pending.Count];
        var used = 0L;
        while (result.Count < LogBatch.MaximumEntries)
        {
            var pick = -1;
            for (var i = 0; i < pending.Count; i++)
                if (next[i] < pending[i].Count && (pick < 0 || pending[i][next[i]].At < pending[pick][next[pick]].At)) pick = i;
            if (pick < 0) break;
            var record = pending[pick][next[pick]];
            // JSON escapes non-ASCII text as \uXXXX, so a character can take six bytes.
            var size = record.Message.Length * 6L + 320;
            if (used + size > LogBatchBudget) break;
            used += size;
            result.Add(record);
            next[pick]++;
        }
        return result;
    }

    // ---------- reading and showing ----------

    /// <summary>Reads this PC's logs and, unless <paramref name="remote"/> is false, the new lines from the log host (or, with
    /// no log host, each paired host's own log), then shows them.</summary>
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
                if (remote && store is not null) await ReadRemoteLogsAsync();
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

    private async Task ReadRemoteLogsAsync()
    {
        var token = lifetime.Token;
        var hosts = NetworkMap.Hosts(Inputs());
        var hostId = LogHostId;
        if (hostId is not null)
        {
            if (collectedFrom != hostId)
            {
                collectedLogs.Clear();
                collectedAfter = 0;
                collectedFrom = hostId;
            }
            if (hosts.FirstOrDefault(h => h.HostId == hostId) is not { } target)
            {
                logReadNote = $"{hostId} collects the logs but isn't paired with this PC, so only this PC's logs are shown.";
                return;
            }
            try
            {
                for (var page = 0; page < 40 && !closing; page++)
                {
                    var read = await ClusterSync.WithConnectionAsync(target.Pairing, c => c.ReadLogsAsync(collectedAfter, 1000, token));
                    collectedLogs.AddRange(read.Entries);
                    collectedAfter = read.Next;
                    if (!read.More) break;
                }
                if (collectedLogs.Count > CachedRemoteLogLimit) collectedLogs.RemoveRange(0, collectedLogs.Count - CachedRemoteLogLimit);
                logReadNote = $"Includes everything {hostId} collected (read {DateTime.Now:t}).";
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Audio2FaceHostException error) when (error.Code is "request.invalid" or "request.not_found" or "route.not_found")
            {
                logReadNote = $"{hostId} runs an older Martlet that can't collect logs; update it. Only this PC's logs are shown.";
            }
            catch (Exception error) when (error is OperationCanceledException || ClusterSync.IsHostFailure(error))
            {
                logReadNote = $"{hostId} didn't answer ({error.Message.TrimEnd('.')}); showing this PC's logs and what was read from it before.";
            }
            return;
        }
        collectedLogs.Clear();
        collectedFrom = null;
        var problems = new List<string>();
        foreach (var host in hosts)
        {
            var seen = hostOwnLogs.TryGetValue(host.HostId, out var list) ? list : hostOwnLogs[host.HostId] = [];
            try
            {
                for (var page = 0; page < 10 && !closing; page++)
                {
                    var read = await ClusterSync.WithConnectionAsync(host.Pairing,
                        c => c.ReadOwnLogsAsync(seen.Count == 0 ? 0 : seen[^1].Seq, 1000, token));
                    seen.AddRange(read.Entries);
                    if (!read.More) break;
                }
                if (seen.Count > 3000) seen.RemoveRange(0, seen.Count - 3000);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Audio2FaceHostException error) when (error.Code is "request.invalid" or "request.not_found" or "route.not_found")
            {
                problems.Add($"{host.HostId} runs an older Martlet without a log");
            }
            catch (Exception error) when (error is OperationCanceledException || ClusterSync.IsHostFailure(error))
            {
                problems.Add($"{host.HostId} didn't answer");
            }
        }
        foreach (var gone in hostOwnLogs.Keys.Where(k => hosts.All(h => h.HostId != k)).ToArray()) hostOwnLogs.Remove(gone);
        logReadNote = hosts.Count == 0 ? "No Martlet host is paired, so only this PC's logs are shown."
            : $"Includes each paired host's own log." + (problems.Count > 0 ? $" {string.Join("; ", problems)}." : "");
    }

    /// <summary>Every known line once (this PC's own copy wins), newest first.</summary>
    private List<LogRecord> AllLogs()
    {
        var seen = new HashSet<(string, long)>();
        var all = new List<LogRecord>();
        foreach (var record in localLogs.Concat(collectedLogs).Concat(hostOwnLogs.Values.SelectMany(v => v)))
            if (seen.Add((record.Stream, record.Seq))) all.Add(record);
        all.Sort((a, b) => b.At != a.At ? b.At.CompareTo(a.At) : b.Seq.CompareTo(a.Seq));
        return all;
    }

    private string SourceName(string source) => source == ClusterDevice ? $"This PC ({source})" : source;

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
        var all = localLogs.Concat(collectedLogs).Concat(hostOwnLogs.Values.SelectMany(v => v)).ToArray();
        var sources = all.Select(r => r.Source).Append(ClusterDevice).Distinct(StringComparer.Ordinal)
            .OrderBy(s => s != ClusterDevice).ThenBy(s => s, StringComparer.Ordinal).ToArray();
        var parts = LogComponents.Local.Append(LogComponents.Gateway).Concat(all.Select(r => r.Component)).Distinct(StringComparer.Ordinal).ToArray();
        if (!sources.Contains(logSource) && logSource != "all") logSource = "all";
        if (!parts.Contains(logPart) && logPart != "all") logPart = "all";
        FillFilters(LogLevelFilters, "LogLevel-", "Show",
            [("all", "Everything"), ("warnings", "Warnings and errors"), ("errors", "Errors only")], logLevel, value => logLevel = value);
        FillFilters(LogSourceFilters, "LogSource-", "From",
            [("all", "All computers"), .. sources.Select(s => (s, SourceName(s)))], logSource, value => logSource = value);
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
        var minimum = logLevel switch { "errors" => 2, "warnings" => 1, _ => 0 };
        var search = LogSearch.Text.Trim();
        var matching = all.Where(r => LogLevels.Rank(r.Level) >= minimum &&
                (logSource == "all" || r.Source == logSource) &&
                (logPart == "all" || r.Component == logPart) &&
                (search.Length == 0 || r.Message.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                    r.Source.Contains(search, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        var shown = matching.Take(ShownLogLimit).ToList();
        var signature = $"{all.Count}|{(all.Count > 0 ? all[0].Stream + all[0].Seq : "")}|{minimum}|{logSource}|{logPart}|{search}";
        if (signature != logRowsSignature)
        {
            logRowsSignature = signature;
            var selected = (LogList.SelectedItem as LogRow)?.Record;
            var rows = shown.Select((r, i) =>
            {
                var time = r.At.ToLocalTime().ToString(r.At.ToLocalTime().Date == DateTime.Today ? "HH:mm:ss.fff" : "MMM d HH:mm:ss");
                var where = $"{(r.Source == ClusterDevice ? "This PC" : r.Source)} · {PartName(r.Component)}";
                var headline = r.Headline.Length > 400 ? r.Headline[..400] + "…" : r.Headline;
                return new LogRow(r, time, r.Level, LogLevels.Rank(r.Level), where, headline, $"LogEntry-{i}",
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
        var computers = all.Select(r => r.Source).Distinct(StringComparer.Ordinal).Count();
        LogSummary.Text = (all.Count == 0 ? "No log lines yet." :
            $"Showing {shown.Count:N0} of {all.Count:N0} lines from {computers} computer{(computers == 1 ? "" : "s")}" +
            (matching.Count > shown.Count ? $" (the newest {ShownLogLimit:N0} that match)" : "") +
            $". Last 24 hours: {errors:N0} error{(errors == 1 ? "" : "s")}, {warnings:N0} warning{(warnings == 1 ? "" : "s")}.") +
            (logReadNote is { } note ? " " + note : "");
    }
}
