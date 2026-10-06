using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using Martlet.Conversation;
using Martlet.Core.Settings;

namespace Martlet.Desktop;

/// <summary>Background tasks in the talk window: the header's chip says how many tasks Martlet is running (or has ready to
/// bring up), and opens the task list over the conversation with each task's details and Cancel.</summary>
public partial class LiveConversationWindow
{
    /// <summary>Set to 1 before Martlet starts: opening the talk window starts one FIXTURE - NOT AI background task that works
    /// until it is canceled (or 30 minutes pass), so automated checks can see the task chip and list without a model.</summary>
    internal const string BackgroundFixtureVariable = "MARTLET_BACKGROUND_FIXTURE";

    private static readonly BackgroundJobKind FixtureKind = new("fixture", 1, 60, TimeSpan.FromMinutes(30), Doing: "Fixture task");

    private sealed record TaskCard(Border Card, TextBlock Glyph, TextBlock Meta, TextBlock Title, TextBlock State, Button Cancel,
        Button ResultToggle, TextBox Result);

    // The task list's cards, by job ID.
    private readonly Dictionary<string, TaskCard> taskCards = new(StringComparer.Ordinal);
    private bool tasksOpen;
    private int tasksReady;

    private void StartFixtureTask()
    {
        if (Environment.GetEnvironmentVariable(BackgroundFixtureVariable) != "1") return;
        controller.Jobs.Start(FixtureKind, "FIXTURE - NOT AI background task", async (job, token) =>
        {
            job.Report(BackgroundJobState.Running, "FIXTURE - NOT AI: pretending to work");
            await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);
            return BackgroundJobOutcome.Done("FIXTURE - NOT AI");
        });
    }

    private void TasksChip_Click(object sender, RoutedEventArgs e)
    {
        tasksOpen = !tasksOpen;
        RenderJobs();
        if (tasksOpen) Motion.Enter(TasksPanel, dy: -8, milliseconds: 180);
    }

    private void TasksClose_Click(object sender, RoutedEventArgs e) => CloseTasks();

    // A click in the conversation closes the task list, like any flyout.
    private void History_PreviewMouseDown(object sender, MouseButtonEventArgs e) => CloseTasks();

    /// <summary>Closes the task list; false when it wasn't open.</summary>
    private bool CloseTasks()
    {
        if (!tasksOpen) return false;
        tasksOpen = false;
        RenderJobs();
        return true;
    }

    /// <summary>The task chip and list: the chip shows once Martlet has started a task in this conversation, the list every
    /// task still running (oldest first) and then those finished (newest first).</summary>
    private void RenderJobs()
    {
        var jobs = controller.Jobs;
        var active = jobs.Active;
        var shown = active.Concat(jobs.Recent).ToList();
        var ready = jobs.Undelivered.Count(job => !job.Quiet);
        var when = controller.Configuration?.ThinkLonger.When ?? ThinkLongerSettings.DefaultDelivery;

        if (shown.Count == 0) tasksOpen = false;
        TasksChip.Visibility = shown.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        var line = TasksChipLine(active.Count, ready, shown.Count - active.Count);
        TasksText.Text = line;
        AutomationProperties.SetName(TasksChip, "Background tasks: " + line);
        var running = active.Count > 0;
        TasksTrack.Visibility = TasksSpinner.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        Motion.Spin(TasksSpinner, running);
        TasksDot.Visibility = !running && ready > 0 ? Visibility.Visible : Visibility.Collapsed;
        TasksGlyph.Visibility = !running && ready == 0 ? Visibility.Visible : Visibility.Collapsed;
        TasksText.SetResourceReference(TextBlock.ForegroundProperty, running || ready > 0 ? "TextBrush" : "MutedBrush");
        TasksChip.SetResourceReference(BorderBrushProperty, tasksOpen ? "AccentBrush" : "BorderBrush");
        if (ready > tasksReady && TasksDot.Visibility == Visibility.Visible) Motion.Blink(TasksDot);
        tasksReady = ready;

        TasksPanel.Visibility = tasksOpen ? Visibility.Visible : Visibility.Collapsed;
        JobsText.Text = JobsLine(shown, when);
        foreach (var gone in taskCards.Keys.Where(id => shown.All(job => job.Id != id)).ToArray())
        {
            JobCards.Children.Remove(taskCards[gone].Card);
            taskCards.Remove(gone);
        }
        for (var index = 0; index < shown.Count; index++)
        {
            var job = shown[index];
            if (!taskCards.TryGetValue(job.Id, out var card)) taskCards[job.Id] = card = NewCard(job);
            if (JobCards.Children.IndexOf(card.Card) != index)
            {
                JobCards.Children.Remove(card.Card);
                JobCards.Children.Insert(Math.Min(index, JobCards.Children.Count), card.Card);
            }
            card.Meta.Text = $"{KindTitle(job.Kind)} · {job.Id}{(job.Place is { } place ? " · on " + place.Name : "")} · {BackgroundJobs.Clockface(job.Elapsed)}";
            card.State.Text = JobStatus(job, when);
            card.Glyph.SetResourceReference(TextBlock.ForegroundProperty,
                job.State is BackgroundJobState.Failed or BackgroundJobState.TimedOut or BackgroundJobState.Canceled ? "MutedBrush" : "AccentBrush");
            card.Cancel.Visibility = job.Finished ? Visibility.Collapsed : Visibility.Visible;
            var result = job.State == BackgroundJobState.Succeeded ? job.Result : null;
            card.ResultToggle.Visibility = string.IsNullOrWhiteSpace(result) ? Visibility.Collapsed : Visibility.Visible;
            if (result is not null && card.Result.Text != result) card.Result.Text = result;
            if (card.ResultToggle.Visibility != Visibility.Visible) card.Result.Visibility = Visibility.Collapsed;
            card.ResultToggle.Content = card.Result.Visibility == Visibility.Visible ? "Hide result" : "Show result";
        }
    }

    private TaskCard NewCard(BackgroundJob job)
    {
        var id = job.Id;
        var bubble = new Border { Width = 30, Height = 30, CornerRadius = new CornerRadius(15), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 10, 0) };
        bubble.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
        var glyph = new TextBlock { Text = KindGlyph(job.Kind), FontSize = 14 };
        glyph.SetResourceReference(StyleProperty, "Icon");
        bubble.Child = glyph;

        var meta = new TextBlock { FontSize = 11, TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis };
        meta.SetResourceReference(StyleProperty, "Muted");
        // What the task is about comes from the conversation, so its text is not a value MCP returns.
        var title = new TextBlock { Text = job.Label, FontSize = 13, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 0) };
        AutomationProperties.SetAutomationId(title, "LiveJob-" + id);
        AutomationProperties.SetName(title, $"{job.Kind.Doing}: {job.Label}");
        var state = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
        state.SetResourceReference(StyleProperty, "Muted");
        AutomationProperties.SetAutomationId(state, "LiveJobState-" + id);
        AutomationProperties.SetLiveSetting(state, AutomationLiveSetting.Polite);

        var result = new TextBox
        {
            IsReadOnly = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 180, FontSize = 12, Margin = new Thickness(0, 6, 0, 0),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Visibility = Visibility.Collapsed
        };
        AutomationProperties.SetAutomationId(result, "LiveJobResult-" + id);
        AutomationProperties.SetName(result, "Result");
        var toggle = new Button { Content = "Show result", FontSize = 12, Padding = new Thickness(0, 4, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, Visibility = Visibility.Collapsed };
        toggle.SetResourceReference(StyleProperty, "LinkButton");
        AutomationProperties.SetAutomationId(toggle, "LiveJobResultToggle-" + id);
        toggle.Click += (_, _) =>
        {
            result.Visibility = result.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
            RenderJobs();
        };

        var cancel = new Button
        {
            Content = "Cancel", FontSize = 12, Padding = new Thickness(10, 3, 10, 3), MinHeight = 0, Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Top,
            ToolTip = "Stop this task. Martlet hears that you stopped it next time you talk."
        };
        AutomationProperties.SetAutomationId(cancel, "LiveJobCancel-" + id);
        AutomationProperties.SetName(cancel, $"Cancel {id}");
        cancel.Click += (_, _) => { controller.CancelJob(id); RenderActions(); };

        var text = new StackPanel();
        text.Children.Add(meta);
        text.Children.Add(title);
        text.Children.Add(state);
        text.Children.Add(toggle);
        text.Children.Add(result);
        var row = new DockPanel();
        DockPanel.SetDock(bubble, Dock.Left);
        DockPanel.SetDock(cancel, Dock.Right);
        row.Children.Add(bubble);
        row.Children.Add(cancel);
        row.Children.Add(text);
        var card = new Border { CornerRadius = new CornerRadius(12), Padding = new Thickness(10, 9, 10, 9), Margin = new Thickness(0, 0, 0, 6), Child = row };
        card.SetResourceReference(Border.BackgroundProperty, "SoftBrush");
        AutomationProperties.SetAutomationId(card, "LiveTask-" + id);
        return new(card, glyph, meta, title, state, cancel, toggle, result);
    }

    /// <summary>The chip's words: how many tasks run, how many finished ones wait to be brought up, or how many are done.</summary>
    internal static string TasksChipLine(int running, int ready, int done) =>
        running > 0 ? ready > 0 ? $"{running} running · {ready} ready" : $"{running} running"
        : ready > 0 ? $"{ready} ready" : $"{done} done";

    /// <summary>The line under the task list's title (MCP reads it): that tasks keep going while you talk, and when finished
    /// work is brought up. Never what a task is about.</summary>
    internal static string JobsLine(IReadOnlyList<BackgroundJob> jobs, ThinkDelivery when)
    {
        if (jobs.Count == 0) return "";
        var running = jobs.Any(job => !job.Finished);
        var ready = jobs.Any(job => job.Delivery == BackgroundDeliveryState.Pending && !job.Quiet);
        var parts = new List<string>();
        if (running) parts.Add("Martlet keeps working on these while you talk. Stop (Esc) doesn't end them.");
        // Which computers the running ones are on (names only), such as "think-1 on diva, think-2 on ripley".
        var placed = jobs.Where(job => !job.Finished && job.Place is not null).Select(job => $"{job.Id} on {job.Place!.Name}").ToArray();
        if (placed.Length > 0) parts.Add($"Running {string.Join(", ", placed)}.");
        if (ready) parts.Add(when == ThinkDelivery.WhenFree ? "Finished work comes up as soon as Martlet is free." : "Finished work comes up when you talk next.");
        if (parts.Count == 0) parts.Add("What Martlet worked on in the background during this conversation.");
        return string.Join(" ", parts);
    }

    /// <summary>A task's status line (MCP reads it as LiveJobState-&lt;id&gt;): what it is doing now, or how it ended and whether
    /// Martlet has brought it up. Never what it is about or what it found.</summary>
    internal static string JobStatus(BackgroundJob job, ThinkDelivery when)
    {
        var status = Status(job, when);
        return job.Place is { } place && !job.Finished ? $"{status} On {place.Name}." : status;
    }

    private static string Status(BackgroundJob job, ThinkDelivery when)
    {
        var took = BackgroundJobs.Clockface(job.Elapsed);
        return job.State switch
        {
            BackgroundJobState.Waiting => job.Progress is { } note ? Sentence(note) : "Waiting to start.",
            BackgroundJobState.Running => job.Progress is { } note ? Sentence(note) : "Working on it.",
            BackgroundJobState.Paused => job.Progress is { } note ? $"Paused. {Sentence(note)}" : "Paused.",
            BackgroundJobState.Succeeded => $"Done after {took}{(job.Cut ? ", cut short" : "")}. " + job.Delivery switch
            {
                BackgroundDeliveryState.Pending => job.Kind.Offer ? "Martlet will offer it." :
                    when == ThinkDelivery.WhenFree ? "Martlet brings it up as soon as it's free." : "Martlet brings it up when you talk next.",
                BackgroundDeliveryState.Reserved => "Martlet is bringing it up.",
                BackgroundDeliveryState.Delivered => "Martlet brought it up.",
                _ => "The conversation ended before Martlet brought it up."
            },
            BackgroundJobState.TimedOut => "Ran out of time" + (job.Problem is { } why ? $": {Clause(why)}" : "."),
            BackgroundJobState.Canceled => job.CanceledBy switch
            {
                BackgroundJob.CanceledByYou => "You stopped it.",
                BackgroundJob.CanceledByMartlet => "Martlet stopped it.",
                _ => "Stopped when the conversation ended."
            },
            _ => "Couldn't finish" + (job.Problem is { } problem ? $": {Clause(problem)}" : ".")
        };
    }

    // "checking it fits" → "Checking it fits."
    private static string Sentence(string note)
    {
        note = Clause(note);
        return note.Length <= 1 ? note : char.ToUpperInvariant(note[0]) + note[1..];
    }

    // "it failed on this PC" → "it failed on this PC."
    private static string Clause(string note)
    {
        note = note.Trim().TrimEnd('.');
        return note.Length == 0 ? "" : note + ".";
    }

    /// <summary>What the task list calls a kind of task.</summary>
    internal static string KindTitle(BackgroundJobKind kind) => kind.Name switch
    {
        "think" => "Thinking longer",
        "song" => "Song",
        "image" => "Image",
        "research" => "Research",
        _ => kind.Doing
    };

    // Segoe Fluent Icons: a light bulb for thinking, a music note for a song, a picture, a magnifier, otherwise a gear.
    private static string KindGlyph(BackgroundJobKind kind) => kind.Name switch
    {
        "think" => "\uE82F",
        "song" => "\uEC4F",
        "image" => "\uEB9F",
        "research" => "\uE721",
        _ => "\uE713"
    };
}
