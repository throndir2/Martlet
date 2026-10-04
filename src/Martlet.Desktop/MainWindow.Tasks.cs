using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Martlet.Desktop;

/// <summary>Background tasks: every run window's run this session (<see cref="BackgroundTasks"/>), newest first, with what it is
/// doing or how it ended. Show brings its window back (a finished one shows the output it kept), Cancel asks first and Clear
/// finished drops the finished ones. The navigation rail's count shows how many run now.</summary>
public partial class MainWindow
{
    /// <summary>Seconds (1-3600) a simulated background task runs once Martlet's window shows, for checking Background tasks
    /// and the run window through MCP. It only writes a line each second: it installs, downloads and sends nothing.</summary>
    internal const string SimulatedTaskVariable = "MARTLET_SIMULATE_BACKGROUND_TASK";

    private readonly DispatcherTimer tasksTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly Dictionary<int, (TextBlock Glyph, TextBlock State, Button Show, Button Cancel)> taskRows = [];
    private List<int> taskOrder = [];

    private void InitializeTasks()
    {
        BackgroundTasks.Changed += TasksChanged;
        tasksTimer.Tick += (_, _) =>
        {
            if (TasksPage.Visibility == Visibility.Visible && BackgroundTasks.RunningCount > 0) RenderTasks();
            else tasksTimer.Stop();
        };
        RenderTasksCount();
    }

    private void TasksChanged()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(TasksChanged);
            return;
        }
        if (closing) return;
        RenderTasksCount();
        if (TasksPage.Visibility == Visibility.Visible) RenderTasks();
    }

    private void EnterTasks()
    {
        RenderTasks();
        if (BackgroundTasks.RunningCount > 0) tasksTimer.Start();
    }

    private void LeaveTasks() => tasksTimer.Stop();

    private void RenderTasksCount()
    {
        var running = BackgroundTasks.RunningCount;
        NavTasksCount.Text = running > 0 ? running.ToString(CultureInfo.CurrentCulture) : "";
        NavTasksBadge.Visibility = running > 0 ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetName(NavTasks, running == 0 ? "Background tasks" : $"Background tasks, {running} running");
    }

    private void RenderTasks()
    {
        var tasks = BackgroundTasks.All;
        var now = DateTimeOffset.Now;
        var running = tasks.Count(task => task.IsRunning);
        var finished = tasks.Count - running;
        TasksSummaryText.Text = tasks.Count == 0 ? "Nothing has run in the background since Martlet started."
            : (running == 0 ? "Nothing is running now." : $"{running} running now.") +
              (finished == 0 ? "" : $" {finished} finished since Martlet started; Show output shows what each one did until you clear it.");
        TasksEmptyCard.Visibility = tasks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        TasksClearButton.IsEnabled = finished > 0;
        // Rows stay while their tasks stay listed, so the regular refresh never takes the keyboard focus away.
        var ids = tasks.Select(task => task.Id).ToList();
        if (!ids.SequenceEqual(taskOrder))
        {
            TasksList.Children.Clear();
            taskRows.Clear();
            taskOrder = ids;
            foreach (var task in tasks) TasksList.Children.Add(TaskRow(task));
        }
        foreach (var task in tasks) RenderTask(task, now);
        if (running > 0 && TasksPage.Visibility == Visibility.Visible) tasksTimer.Start();
    }

    private Border TaskRow(BackgroundTask task)
    {
        var glyph = new TextBlock { FontSize = 18, Margin = new Thickness(0, 2, 14, 0), VerticalAlignment = VerticalAlignment.Top };
        glyph.SetResourceReference(StyleProperty, "Icon");
        var title = new TextBlock { Text = task.Title, FontSize = 15, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetAutomationId(title, $"TaskTitle-{task.Id}");
        var state = Note("", new Thickness(0, 2, 0, 0));
        state.FontSize = 12.5;
        AutomationProperties.SetAutomationId(state, $"TaskState-{task.Id}");
        var show = PageButton("Show", () => HostRunWindow.ShowTask(this, task), id: $"TaskShow-{task.Id}");
        var cancel = PageButton("Cancel...", () => BackgroundTasks.AskToCancel(this, task), id: $"TaskCancel-{task.Id}");
        cancel.Margin = new Thickness(10, 0, 0, 0);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0) };
        buttons.Children.Add(show);
        buttons.Children.Add(cancel);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(title);
        text.Children.Add(state);
        var row = new DockPanel();
        DockPanel.SetDock(glyph, Dock.Left);
        DockPanel.SetDock(buttons, Dock.Right);
        row.Children.Add(glyph);
        row.Children.Add(buttons);
        row.Children.Add(text);
        var card = new Border { Child = row };
        card.SetResourceReference(StyleProperty, "CardStyle");
        card.Padding = new Thickness(18, 14, 18, 14);
        card.Margin = new Thickness(0, 0, 0, 10);
        AutomationProperties.SetName(card, task.Title);
        taskRows[task.Id] = (glyph, state, show, cancel);
        return card;
    }

    private void RenderTask(BackgroundTask task, DateTimeOffset now)
    {
        if (!taskRows.TryGetValue(task.Id, out var row)) return;
        row.State.Text = task.Describe(now);
        var (glyph, brush) = task.State switch
        {
            BackgroundTaskState.Running => ("\uE916", "AccentBrush"),
            BackgroundTaskState.Done => ("\uE73E", "SuccessBrush"),
            BackgroundTaskState.Canceled => ("\uE711", "MutedBrush"),
            BackgroundTaskState.Paused => ("\uE769", "MutedBrush"),
            _ => ("\uE7BA", "WarningBrush")
        };
        row.Glyph.Text = glyph;
        row.Glyph.SetResourceReference(TextBlock.ForegroundProperty, brush);
        var show = task.IsRunning ? "Show" : "Show output";
        row.Show.Content = show;
        AutomationProperties.SetName(row.Show, $"{show}: {task.Title}");
        AutomationProperties.SetName(row.Cancel, $"Cancel: {task.Title}");
        row.Cancel.Visibility = task.IsRunning ? Visibility.Visible : Visibility.Collapsed;
    }

    private void TasksClear_Click(object sender, RoutedEventArgs e) => BackgroundTasks.ClearFinished();

    /// <summary>Starts the simulated background task <see cref="SimulatedTaskVariable"/> asks for, if any.</summary>
    private void StartSimulatedTask()
    {
        if (!int.TryParse(Environment.GetEnvironmentVariable(SimulatedTaskVariable), NumberStyles.Integer, CultureInfo.InvariantCulture,
                out var seconds) || seconds is < 1 or > 3600) return;
        ErrorLog.Info($"Starting a simulated background task for {seconds} s ({SimulatedTaskVariable}); it changes nothing.");
        HostRunWindow.RunAsync(this, "Simulated background task", async run =>
        {
            for (var second = 1; second <= seconds; second++)
            {
                run.Status($"Simulating step {second} of {seconds}...");
                run.Output.Report($"Simulated output line {second}.");
                await Task.Delay(TimeSpan.FromSeconds(1), run.Token);
            }
            return "The simulated task finished. Nothing was changed.";
        }).Forget();
    }
}
