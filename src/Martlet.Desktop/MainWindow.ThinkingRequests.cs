using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using Martlet.Conversation;

namespace Martlet.Desktop;

/// <summary>Thinking requests: every request for the Thinking pool's slots since Martlet started (<see cref="ThinkingRequests"/>):
/// the waiting and running ones first, then the newest ended ones, each with its type, task, companion, member, tries and how long
/// it waited and ran. Selecting one shows everything known about it; Timing by type sums up every request that ended. The
/// navigation rail's count shows how many wait or run now. Nothing here sends anything: Clear finished only forgets the list.</summary>
public partial class MainWindow
{
    /// <summary>How many simulated Thinking requests (1-50) to post once Martlet's window shows, for checking Thinking requests
    /// through MCP. They only go in the list: no model is asked and nothing is sent.</summary>
    internal const string SimulatedRequestsVariable = "MARTLET_SIMULATE_THINKING_REQUESTS";

    private readonly DispatcherTimer requestsTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly ObservableCollection<ThinkingRequestRow> requestRows = [];
    private int requestsQueued;
    private DateTimeOffset poolLineAt;
    private ThinkingRequests? Requests => conversation?.ThinkingRequests;

    private void InitializeThinkingRequests()
    {
        ThinkingRequestsList.ItemsSource = requestRows;
        ThinkingRequestsKind.Items.Add(new ComboBoxItem { Content = "All types", Tag = null });
        foreach (var kind in ThinkingJobKinds.All)
            ThinkingRequestsKind.Items.Add(new ComboBoxItem { Content = ThinkingRequestWords.Kind(kind), Tag = kind });
        ThinkingRequestsKind.SelectedIndex = 0;
        requestsTimer.Tick += (_, _) =>
        {
            if (ThinkingRequestsPage.Visibility == Visibility.Visible) RenderThinkingRequests();
            else requestsTimer.Stop();
        };
        if (Requests is { } requests) requests.Changed += ThinkingRequestsChanged;
        RenderThinkingRequestsCount();
    }

    // Any thread, often on the reply's path: it only queues one render on the window's thread.
    private void ThinkingRequestsChanged()
    {
        if (Interlocked.Exchange(ref requestsQueued, 1) != 0) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            Interlocked.Exchange(ref requestsQueued, 0);
            if (closing) return;
            RenderThinkingRequestsCount();
            if (ThinkingRequestsPage.Visibility == Visibility.Visible) RenderThinkingRequests();
        });
    }

    private void EnterThinkingRequests()
    {
        poolLineAt = default;
        RenderThinkingRequests();
        requestsTimer.Start();
    }

    private void LeaveThinkingRequests() => requestsTimer.Stop();

    private void RenderThinkingRequestsCount()
    {
        var active = Requests?.ActiveCount ?? 0;
        NavThinkingRequestsCount.Text = active > 0 ? active.ToString(CultureInfo.CurrentCulture) : "";
        NavThinkingRequestsBadge.Visibility = active > 0 ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetName(NavThinkingRequests, active == 0 ? "Thinking requests" : $"Thinking requests, {active} waiting or running");
    }

    private void RenderThinkingRequests()
    {
        if (Requests is not { } requests)
        {
            ThinkingRequestsSummary.Text = "Thinking requests show on a companion PC.";
            ThinkingRequestsEmptyCard.Visibility = Visibility.Visible;
            return;
        }
        var all = requests.List(conversation!.ThinkingPool.Board.Places);
        var running = all.Count(r => r.State == ThinkingRequestState.Running);
        var waiting = all.Count(r => r.State is ThinkingRequestState.Waiting or ThinkingRequestState.Paused);
        var totals = requests.Totals.Values.ToArray();
        var ended = totals.Sum(t => t.Count);
        var problems = totals.Sum(t => t.Problems);
        var averageWait = ended == 0 ? TimeSpan.Zero : TimeSpan.FromTicks(totals.Sum(t => t.Waited.Ticks) / ended);
        var averageRun = ended == 0 ? TimeSpan.Zero : TimeSpan.FromTicks(totals.Sum(t => t.Ran.Ticks) / ended);
        ThinkingRequestsSummary.Text = (running == 0 && waiting == 0 ? "Nothing is waiting or running now." : $"{running} running, {waiting} waiting.") +
            (ended == 0 ? " No request ended since Martlet started."
                : $" {ended} ended since Martlet started ({problems} with a problem): they waited {ThinkingRequestWords.Time(averageWait)} " +
                  $"and ran {ThinkingRequestWords.Time(averageRun)} on average.");
        RenderPoolLine();
        RenderTiming(requests.Totals);
        ThinkingRequestsClearButton.IsEnabled = all.Any(r => r.Done);

        var kind = (ThinkingRequestsKind.SelectedItem as ComboBoxItem)?.Tag as ThinkingJobKind?;
        var shown = all.Where(r => kind is null || r.Kind == kind)
            .Where(r => ThinkingRequestsShowActive.IsChecked == true ? r.Active
                : ThinkingRequestsShowProblems.IsChecked != true || r.Retries > 0 || r.Preemptions > 0 ||
                  r.State is not (ThinkingRequestState.Succeeded or ThinkingRequestState.Running or ThinkingRequestState.Waiting))
            .ToArray();
        ThinkingRequestsEmptyCard.Visibility = shown.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ThinkingRequestsEmpty.Text = all.Count == 0 ? "Requests show here as soon as a companion asks the Thinking pool for something."
            : "No request matches what is chosen to show.";
        // Rows stay while their requests stay listed, so the regular refresh keeps the selection and the keyboard focus.
        var selected = (ThinkingRequestsList.SelectedItem as ThinkingRequestRow)?.Id;
        if (!shown.Select(r => r.Id).SequenceEqual(requestRows.Select(r => r.Id)))
        {
            requestRows.Clear();
            foreach (var request in shown) requestRows.Add(new ThinkingRequestRow(request));
            if (selected is not null) ThinkingRequestsList.SelectedItem = requestRows.FirstOrDefault(r => r.Id == selected);
        }
        else
            for (var i = 0; i < shown.Length; i++) requestRows[i].Show(shown[i]);
        RenderRequestDetail();
    }

    // The pool line reads the pool's members, so it is refreshed at most every few seconds.
    private void RenderPoolLine()
    {
        var now = DateTimeOffset.UtcNow;
        if (now - poolLineAt < TimeSpan.FromSeconds(5) || conversation is null) return;
        poolLineAt = now;
        var status = conversation.ThinkingPool.Status();
        var offline = status.Members.Count(m => !m.Online);
        ThinkingRequestsPool.Text = status.Members.Count == 0
            ? "The Thinking pool has no member: requests use their own fallbacks. Add members in Companion › Thinking pool."
            : $"Thinking pool: {status.Free} of {status.Slots} slot{(status.Slots == 1 ? "" : "s")} free on " +
              $"{status.Members.Count - offline} member{(status.Members.Count - offline == 1 ? "" : "s")}" +
              (offline > 0 ? $" ({offline} offline)" : "") + (status.KeepsFastSlot ? "; one stays free for quick jobs" : "") +
              (status.Floor is { } floor && floor != "Idle" ? $". The conversation is {floor.ToLowerInvariant()} now." : ".");
    }

    private void RenderTiming(IReadOnlyDictionary<ThinkingJobKind, ThinkingRequestTotals> totals)
    {
        var grid = ThinkingRequestsTiming;
        grid.Children.Clear();
        grid.RowDefinitions.Clear();
        grid.ColumnDefinitions.Clear();
        string[] heads = ["Type", "Ended", "Done", "Problems", "Retries", "Average wait", "Longest wait", "Average run", "Longest run"];
        foreach (var _ in heads) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        void Cell(int row, int column, string text, bool head = false)
        {
            var cell = new TextBlock { Text = text, Margin = new Thickness(0, 2, 18, 2), FontWeight = head ? FontWeights.SemiBold : FontWeights.Normal };
            Grid.SetRow(cell, row);
            Grid.SetColumn(cell, column);
            grid.Children.Add(cell);
        }
        grid.RowDefinitions.Add(new RowDefinition());
        for (var c = 0; c < heads.Length; c++) Cell(0, c, heads[c], head: true);
        var row = 1;
        foreach (var (kind, total) in totals.OrderByDescending(t => t.Value.Count))
        {
            grid.RowDefinitions.Add(new RowDefinition());
            string[] cells = [ThinkingRequestWords.Kind(kind), total.Count.ToString(CultureInfo.CurrentCulture),
                total.Succeeded.ToString(CultureInfo.CurrentCulture), total.Problems.ToString(CultureInfo.CurrentCulture),
                total.Retries.ToString(CultureInfo.CurrentCulture), ThinkingRequestWords.Time(total.AverageWait),
                ThinkingRequestWords.Time(total.MaxWaited), ThinkingRequestWords.Time(total.AverageRun), ThinkingRequestWords.Time(total.MaxRan)];
            for (var c = 0; c < cells.Length; c++) Cell(row, c, cells[c]);
            row++;
        }
        if (row == 1)
        {
            grid.RowDefinitions.Add(new RowDefinition());
            var none = new TextBlock { Text = "No request ended yet." };
            none.SetResourceReference(StyleProperty, "Muted");
            Grid.SetRow(none, 1);
            Grid.SetColumnSpan(none, heads.Length);
            grid.Children.Add(none);
        }
    }

    private void RenderRequestDetail()
    {
        if (ThinkingRequestsList.SelectedItem is not ThinkingRequestRow { Request: var request })
        {
            ThinkingRequestDetailPanel.Visibility = Visibility.Collapsed;
            return;
        }
        ThinkingRequestDetailPanel.Visibility = Visibility.Visible;
        // What the conversation asked for (a think's or a search's label): shown on this PC only, never in a status file.
        ThinkingRequestTopic.Text = request.Start.Topic is { } topic ? $"About: {topic}" : "";
        ThinkingRequestTopic.Visibility = request.Start.Topic is null ? Visibility.Collapsed : Visibility.Visible;
        var text = Describe(request);
        if (ThinkingRequestDetail.Text != text) ThinkingRequestDetail.Text = text;
    }

    /// <summary>Everything known about <paramref name="request"/> in plain lines (never its text, answer or topic).</summary>
    internal static string Describe(ThinkingRequestInfo request)
    {
        static string At(DateTimeOffset? at) => at?.ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.CurrentCulture) ?? "-";
        var start = request.Start;
        var lines = new StringBuilder();
        lines.AppendLine($"{request.Id}  {ThinkingRequestWords.Task(request)}");
        lines.AppendLine($"State:       {ThinkingRequestWords.State(request)}{(request.Note is { } note ? $": {note}" : "")}");
        lines.AppendLine($"Type:        {ThinkingRequestWords.Kind(request.Kind)} ({request.KindName}), {(request.Fast ? "quick" : "long")} job, priority {request.Priority}");
        lines.AppendLine($"For:         {ThinkingRequestWords.Origin(request)} ({ThinkingRequestWords.Source(start.Source)}{(start.JobId is { } job ? $" {job}" : "")})");
        lines.AppendLine($"Needs:       {ThinkingRequestWords.Needs(start.Needs)}");
        lines.AppendLine($"Times:       posted {At(request.Posted)}, first try {At(request.Started)}, ended {At(request.Finished)}");
        lines.AppendLine($"Timing:      waited {ThinkingRequestWords.Time(request.Waited)} (first wait {ThinkingRequestWords.Time(request.FirstWait)}), " +
            $"ran {ThinkingRequestWords.Time(request.Ran)}, {(request.Done ? "total" : "so far")} {ThinkingRequestWords.Time(request.Total)}");
        lines.AppendLine($"Tries:       {request.Attempts.Count} ({request.Retries} retr{(request.Retries == 1 ? "y" : "ies")}), " +
            $"stopped for the conversation {request.Preemptions} time{(request.Preemptions == 1 ? "" : "s")}");
        List<string> limits = [];
        if (start.Timeout is { } timeout) limits.Add($"{(start.DropWhenStale ? "dropped after" : "time limit")} {ThinkingRequestWords.Time(timeout)}");
        if (start.MaxOutputTokens is { } tokens) limits.Add($"up to {tokens} tokens");
        if (start.Reasoning is { } reasoning) limits.Add($"step-by-step thinking {(reasoning ? "on" : "off")}");
        if (start.Tools > 0) limits.Add($"{start.Tools} tool{(start.Tools == 1 ? "" : "s")}");
        if (limits.Count > 0) lines.AppendLine($"Limits:      {string.Join(", ", limits)}");
        if (request.AnswerLength is { } length) lines.AppendLine($"Answer:      {length} characters{(request.Cut ? " (cut at the token limit)" : "")}");
        for (var i = 0; i < request.Attempts.Count; i++)
        {
            var attempt = request.Attempts[i];
            if (i == 0) lines.AppendLine("Each try:");
            lines.AppendLine($"  {i + 1}. {attempt.Member}{(attempt.Model is { } model ? $" ({model})" : "")} at {At(attempt.Started)}, " +
                $"{ThinkingRequestWords.Time(attempt.Duration(request.Finished ?? request.Now))}: {attempt.Ending ?? "running"}");
        }
        return lines.ToString().TrimEnd();
    }

    private void ThinkingRequestsList_SelectionChanged(object sender, SelectionChangedEventArgs e) => RenderRequestDetail();

    private void ThinkingRequestsFilter_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || ThinkingRequestsPage.Visibility != Visibility.Visible) return;
        RenderThinkingRequests();
    }

    private void ThinkingRequestsClear_Click(object sender, RoutedEventArgs e) => Requests?.ClearFinished();

    private void ThinkingRequestsCopy_Click(object sender, RoutedEventArgs e)
    {
        var text = string.Join(Environment.NewLine + Environment.NewLine, requestRows.Select(row => Describe(row.Request)));
        if (text.Length == 0) return;
        try { Clipboard.SetText(text); }
        catch (System.Runtime.InteropServices.COMException) { ActionText.Text = "Couldn't copy: another app is using the clipboard. Try again."; }
    }

    /// <summary>Posts the simulated requests <see cref="SimulatedRequestsVariable"/> asks for, if any: they wait, run on made-up
    /// members, retry and end in each way, over about half a minute. No model is asked and nothing is sent.</summary>
    private void StartSimulatedRequests()
    {
        if (!int.TryParse(Environment.GetEnvironmentVariable(SimulatedRequestsVariable), NumberStyles.Integer, CultureInfo.InvariantCulture,
                out var count) || count is < 1 or > 50 || Requests is not { } requests) return;
        ErrorLog.Info($"Posting {count} simulated Thinking requests ({SimulatedRequestsVariable}); they ask no model and send nothing.");
        BackgroundPlace gpu = new("sim:gpu-box", "gpu-box (simulated)") { Model = "qwen3:8b" },
            laptop = new("sim:laptop", "laptop (simulated)") { Model = "gemma3:4b" };
        for (var i = 0; i < count; i++)
        {
            var kind = ThinkingJobKinds.All[i % ThinkingJobKinds.All.Count];
            var number = i;
            Task.Run(async () =>
            {
                var request = requests.Post(new(kind, ThinkingRequestSource.Simulated, $"simulated-{number + 1}")
                {
                    Task = $"Simulated {ThinkingRequestWords.Kind(kind).ToLowerInvariant()}", Timeout = TimeSpan.FromSeconds(30),
                    MaxOutputTokens = 256, Reasoning = false
                });
                await Task.Delay(TimeSpan.FromMilliseconds(400 + number * 700));
                request.Begin(number % 2 == 0 ? gpu : laptop);
                await Task.Delay(TimeSpan.FromMilliseconds(900 + number % 4 * 600));
                switch (number % 5)
                {
                    case 1:
                        request.End($"{laptop.Name} failed (simulated)");
                        await Task.Delay(TimeSpan.FromMilliseconds(500));
                        request.Begin(gpu);
                        await Task.Delay(TimeSpan.FromMilliseconds(1200));
                        request.Finish(ThinkingRequestState.Succeeded, answer: 180);
                        break;
                    case 3:
                        request.Finish(ThinkingRequestState.Failed, $"{laptop.Name} came back empty (simulated)");
                        break;
                    case 4:
                        // Stays running a while, so the page shows a request in progress.
                        await Task.Delay(TimeSpan.FromSeconds(20));
                        request.Finish(ThinkingRequestState.Succeeded, answer: 1200, wasCut: true);
                        break;
                    default:
                        request.Finish(ThinkingRequestState.Succeeded, answer: 40 + number);
                        break;
                }
            }).Forget();
        }
    }
}

/// <summary>One line of the Thinking requests list; it changes in place as its request goes on, so the list keeps the selection.</summary>
internal sealed class ThinkingRequestRow : INotifyPropertyChanged
{
    internal ThinkingRequestRow(ThinkingRequestInfo request) => Request = request;

    public event PropertyChangedEventHandler? PropertyChanged;

    internal ThinkingRequestInfo Request { get; private set; }
    public string Id => Request.Id;
    public string AutomationId => $"ThinkingRequest-{Request.Id}";
    public string Kind => ThinkingRequestWords.Kind(Request.Kind);
    public string State => ThinkingRequestWords.State(Request);
    public string Task => ThinkingRequestWords.Task(Request);
    public string Where => Request.Last is { } last
        ? $"{last.Member}{(last.Model is { } model ? $" · {model}" : "")} · priority {Request.Priority}"
        : $"no member yet · priority {Request.Priority}";
    public string Origin => ThinkingRequestWords.Origin(Request);
    public string Waited => ThinkingRequestWords.Time(Request.Waited);
    public string Ran => Request.Attempts.Count == 0 ? "-" : ThinkingRequestWords.Time(Request.Ran);
    public string Tries => Request.Attempts.Count.ToString(CultureInfo.CurrentCulture);
    public string Glyph => Request.State switch
    {
        ThinkingRequestState.Running => "\uE916",
        ThinkingRequestState.Waiting or ThinkingRequestState.Paused => "\uE823",
        ThinkingRequestState.Succeeded => "\uE73E",
        ThinkingRequestState.Canceled => "\uE711",
        _ => "\uE7BA"
    };
    /// <summary>0 waiting or running, 1 done, 2 a problem, 3 canceled.</summary>
    public int Severity => Request.State switch
    {
        ThinkingRequestState.Waiting or ThinkingRequestState.Running or ThinkingRequestState.Paused => 0,
        ThinkingRequestState.Succeeded => 1,
        ThinkingRequestState.Canceled => 3,
        _ => 2
    };
    public string Spoken => $"{Kind}: {Task}, {State}, for {Origin}, waited {Waited}, ran {Ran}, {Tries} tr{(Request.Attempts.Count == 1 ? "y" : "ies")}";

    internal void Show(ThinkingRequestInfo request)
    {
        Request = request;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }

    public override string ToString() => Spoken;
}
