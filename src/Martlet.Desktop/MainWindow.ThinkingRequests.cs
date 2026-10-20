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
/// it waited and ran. Selecting one shows everything known about it, what it sent its model (its input) and what it answered (its
/// output); both are private to this PC: never in a log, a status file or MCP. Close (or Esc) hides the details again. Timing by
/// type sums up every request that ended. The
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
