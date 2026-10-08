using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using Martlet.Core.Planning;
using Martlet.Core.Settings;
using Martlet.Desktop;

namespace Martlet.Desktop.Tests;

public sealed class BackgroundTasksTests
{
    [Fact]
    public Task ClosingOrHidingARunningRunWindowOnlyHidesItAndTheTaskKeepsRunning() => OnDispatcher(async () =>
    {
        BackgroundTasks.Reset();
        var owner = Owner();
        try
        {
            var finish = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            HostRunWindow? run = null;
            var result = HostRunWindow.RunAsync(owner, "Set up test-pc", window =>
            {
                run = window;
                window.Status("Working on test-pc...");
                window.Output.Report("first line");
                return finish.Task;
            });
            Assert.NotNull(run);
            var task = Assert.Single(BackgroundTasks.All);
            Assert.Same(task, run.BackgroundTask);
            Assert.True(task.IsRunning);
            Assert.Equal("Working on test-pc...", task.Status);
            Assert.Equal(1, BackgroundTasks.RunningCount);
            Assert.True(run.IsVisible);
            Assert.Equal("_Hide", ById<Button>(run, "HostRunHide").Content);
            Assert.True(ById<Button>(run, "HostRunHide").IsCancel);
            Assert.False(ById<Button>(run, "HostRunCancel").IsCancel);
            Assert.Equal(Visibility.Visible, ById<Button>(run, "HostRunCancel").Visibility);
            await Until(() => Field<TextBox>(run, "OutputText").Text.Contains("first line", StringComparison.Ordinal));

            // The window's close button (and Esc, through Hide's IsCancel) hides the window; the task keeps going.
            run.Close();
            Assert.False(run.IsVisible);
            Assert.True(run.IsRunning);
            Assert.False(run.Token.IsCancellationRequested);
            Assert.Same(run, task.Window);

            // Background tasks shows the same window again; Hide hides it again.
            Assert.Same(run, HostRunWindow.ShowTask(owner, task));
            Assert.True(run.IsVisible);
            Click(run, "HostRunHide");
            Assert.False(run.IsVisible);
            Assert.True(run.IsRunning);

            // Finishing out of sight closes the window; the task keeps its output for Show output.
            finish.SetResult("test-pc is ready.");
            Assert.Equal("test-pc is ready.", await result);
            Assert.Equal(BackgroundTaskState.Done, task.State);
            Assert.Equal("test-pc is ready.", task.Status);
            Assert.NotNull(task.Ended);
            Assert.Null(task.Window);
            Assert.Equal(0, BackgroundTasks.RunningCount);
            Assert.Contains("first line", task.Output, StringComparison.Ordinal);
            Assert.Contains("Finished. test-pc is ready.", task.Output, StringComparison.Ordinal);

            var record = HostRunWindow.ShowTask(owner, task);
            Assert.NotSame(run, record);
            Assert.Same(record, task.Window);
            Assert.True(record.IsVisible);
            Assert.False(record.IsRunning);
            Assert.Contains("first line", Field<TextBox>(record, "OutputText").Text, StringComparison.Ordinal);
            Assert.Equal("test-pc is ready.", Field<TextBlock>(record, "StatusText").Text);
            Assert.Equal("_Close", ById<Button>(record, "HostRunHide").Content);
            Assert.Equal(Visibility.Collapsed, ById<Button>(record, "HostRunCancel").Visibility);
            Assert.Equal(Visibility.Collapsed, Field<TextBlock>(record, "HideHint").Visibility);
            Click(record, "HostRunHide");
            Assert.False(record.IsVisible);
            Assert.Null(task.Window);
        }
        finally
        {
            owner.Close();
            BackgroundTasks.Reset();
        }
    });

    [Fact]
    public Task CancelTaskAsksFirstAndOnlyStopsTheRunWhenConfirmed() => OnDispatcher(async () =>
    {
        BackgroundTasks.Reset();
        var confirm = BackgroundTasks.Confirm;
        var owner = Owner();
        try
        {
            var asked = new List<string>();
            var answer = false;
            BackgroundTasks.Confirm = (window, task) =>
            {
                asked.Add(task.Title);
                return answer;
            };
            HostRunWindow? run = null;
            var result = HostRunWindow.RunAsync(owner, "Download test-model", async window =>
            {
                run = window;
                await Task.Delay(Timeout.Infinite, window.Token);
                return "never";
            });
            Assert.NotNull(run);
            var task = Assert.Single(BackgroundTasks.All);

            Click(run, "HostRunCancel");
            Assert.Equal(["Download test-model"], asked);
            Assert.True(run.IsRunning);
            Assert.False(run.Token.IsCancellationRequested);
            Assert.True(task.IsRunning);

            // Background tasks' Cancel... asks the same question.
            Assert.False(BackgroundTasks.AskToCancel(owner, task));
            Assert.Equal(2, asked.Count);
            Assert.True(task.IsRunning);

            answer = true;
            Click(run, "HostRunCancel");
            Assert.Null(await result);
            Assert.Equal(3, asked.Count);
            Assert.Equal(BackgroundTaskState.Canceled, task.State);
            Assert.Equal("Canceled.", task.Status);
            Assert.StartsWith("Canceled at ", task.Describe(DateTimeOffset.Now), StringComparison.Ordinal);
            Assert.EndsWith(" s.", task.Describe(DateTimeOffset.Now), StringComparison.Ordinal);
            // A run that ends while shown stays open to read; its button is now Close and Cancel task is gone.
            Assert.True(run.IsVisible);
            Assert.Equal("_Close", ById<Button>(run, "HostRunHide").Content);
            Assert.Equal(Visibility.Collapsed, ById<Button>(run, "HostRunCancel").Visibility);
            Assert.False(BackgroundTasks.AskToCancel(owner, task));
            Assert.Equal(3, asked.Count);
            run.Close();
            Assert.Null(task.Window);
        }
        finally
        {
            BackgroundTasks.Confirm = confirm;
            owner.Close();
            BackgroundTasks.Reset();
        }
    });

    [Fact]
    public Task AQuestionFromAHiddenRunShowsItsWindowAndTheRunOutlivesTheWindowThatStartedIt() => OnDispatcher(async () =>
    {
        BackgroundTasks.Reset();
        var main = Owner();
        var wizard = Owner();
        wizard.Owner = main;
        var wizardClosed = false;
        wizard.Closed += (_, _) => wizardClosed = true;
        try
        {
            var finish = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            HostRunWindow? run = null;
            var result = HostRunWindow.RunAsync(wizard, "Pair with test-pc", window =>
            {
                run = window;
                return finish.Task;
            }, keeper: main);
            Assert.NotNull(run);

            // A question asked over the hidden run (a password, a role's choices) brings its window back with it, and the run's
            // own buttons wait for the answer; also when the run window was maximized.
            run.WindowState = WindowState.Maximized;
            Click(run, "HostRunHide");
            Assert.False(run.IsVisible);
            var question = new ConfirmationDialog("Trust computer", "Trust test-pc?") { Owner = run, ShowActivated = false };
            question.Show();
            Assert.True(run.IsVisible);
            Assert.False(run.IsEnabled);
            Assert.True(run.ShowActivated);
            question.Close();
            Assert.True(run.IsEnabled);
            run.WindowState = WindowState.Normal;

            // Closing the window that started it (the hosts wizard) leaves the run with Martlet's main window, still running.
            Click(run, "HostRunHide");
            wizard.Close();
            Assert.False(wizard.IsVisible);
            Assert.Same(main, run.Owner);
            Assert.True(run.IsRunning);
            Assert.False(run.Token.IsCancellationRequested);
            Assert.NotNull(run.BackgroundTask.Window);

            finish.SetResult("Paired.");
            Assert.Equal("Paired.", await result);
            Assert.Null(run.BackgroundTask.Window);
        }
        finally
        {
            if (!wizardClosed) wizard.Close();
            main.Close();
            BackgroundTasks.Reset();
        }
    });

    [Fact]
    public Task ARunShownWhenItEndsStaysOpenEvenIfItsOwnerWasMinimized() => OnDispatcher(async () =>
    {
        BackgroundTasks.Reset();
        var owner = Owner();
        try
        {
            var finish = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            HostRunWindow? run = null;
            var result = HostRunWindow.RunAsync(owner, "Download test-model", window =>
            {
                run = window;
                return finish.Task;
            });
            Assert.NotNull(run);
            // Windows hides owned windows while their owner is minimized; that isn't the owner hiding the run.
            owner.WindowState = WindowState.Minimized;
            finish.SetResult("Downloaded.");
            Assert.Equal("Downloaded.", await result);
            Assert.Same(run, run.BackgroundTask.Window);
            owner.WindowState = WindowState.Normal;
            await Until(() => run.IsVisible);
            Assert.Equal("_Close", ById<Button>(run, "HostRunHide").Content);
            run.Close();
            Assert.Null(run.BackgroundTask.Window);
        }
        finally
        {
            owner.Close();
            BackgroundTasks.Reset();
        }
    });

    [Fact]
    public Task ExitingInterruptsAHiddenRunAndClosesItsWindow() => OnDispatcher(async () =>
    {
        BackgroundTasks.Reset();
        var owner = Owner();
        try
        {
            HostRunWindow? run = null;
            var result = HostRunWindow.RunAsync(owner, "Update test-pc", async window =>
            {
                run = window;
                await Task.Delay(Timeout.Infinite, window.Token);
                return "never";
            });
            Assert.NotNull(run);
            Click(run, "HostRunHide");
            run.Interrupt();
            Assert.Null(await result);
            Assert.Equal(BackgroundTaskState.Canceled, run.BackgroundTask.State);
            Assert.Null(run.BackgroundTask.Window);
        }
        finally
        {
            owner.Close();
            BackgroundTasks.Reset();
        }
    });

    [Fact]
    public void ATaskSaysHowLongItRanAndHowItEnded()
    {
        var started = new DateTimeOffset(2026, 10, 4, 15, 0, 0, TimeSpan.Zero);
        var canceled = 0;
        var task = new BackgroundTask("Set up gpu-pc", () => canceled++, started);
        task.SetStatus("Waiting for Docker Desktop to start...");
        Assert.Equal("Running for 2 min. Waiting for Docker Desktop to start...", task.Describe(started.AddMinutes(2).AddSeconds(30)));
        Assert.Equal("Running for 12 s. Waiting for Docker Desktop to start...", task.Describe(started.AddSeconds(12)));
        task.Cancel();
        Assert.Equal(1, canceled);

        task.Finish(BackgroundTaskState.Stopped, "Docker Desktop didn't start.", started.AddHours(1).AddMinutes(5));
        var at = started.AddHours(1).AddMinutes(5).ToLocalTime().ToString("t", System.Globalization.CultureInfo.CurrentCulture);
        Assert.Equal($"Stopped at {at} after 1 h 5 min. Docker Desktop didn't start.", task.Describe(started.AddDays(1)));
        task.Cancel();
        Assert.Equal(1, canceled);
        // A finished task stays finished.
        task.Finish(BackgroundTaskState.Done, "Ready.");
        Assert.Equal(BackgroundTaskState.Stopped, task.State);

        Assert.Equal("1 s", BackgroundTask.Duration(TimeSpan.Zero));
        Assert.Equal("59 min", BackgroundTask.Duration(TimeSpan.FromMinutes(59.9)));
        Assert.Equal("2 h", BackgroundTask.Duration(TimeSpan.FromHours(2)));
    }

    [Fact]
    public void ClearFinishedKeepsRunningTasksAndOldFinishedTasksDropOff()
    {
        BackgroundTasks.Reset();
        try
        {
            var running = BackgroundTasks.Start("Running", () => { });
            for (var i = 0; i < BackgroundTasks.MaximumFinished + 3; i++)
                BackgroundTasks.Start($"Finished {i}", () => { }).Finish(BackgroundTaskState.Done, "Done.");
            Assert.Equal(BackgroundTasks.MaximumFinished + 1, BackgroundTasks.All.Count);
            Assert.Equal($"Finished {BackgroundTasks.MaximumFinished + 2}", BackgroundTasks.All[0].Title);
            Assert.Contains(running, BackgroundTasks.All);
            Assert.DoesNotContain(BackgroundTasks.All, task => task.Title == "Finished 0");

            BackgroundTasks.ClearFinished();
            Assert.Same(running, Assert.Single(BackgroundTasks.All));
        }
        finally { BackgroundTasks.Reset(); }
    }

    [Fact]
    public void DiscardDropsOnlyAFinishedTaskWhoseWindowIsClosed()
    {
        BackgroundTasks.Reset();
        try
        {
            var other = BackgroundTasks.Start("Download Martlet 9.9.9", () => { });
            var busy = BackgroundTasks.Start("Update gpu-box to Martlet 9.9.9", () => { });
            BackgroundTasks.Discard(busy);
            Assert.Contains(busy, BackgroundTasks.All);
            busy.Finish(BackgroundTaskState.Done, "gpu-box is busy with another change (installing ollama), so nothing was changed.");
            BackgroundTasks.Discard(busy);
            Assert.Same(other, Assert.Single(BackgroundTasks.All));
        }
        finally { BackgroundTasks.Reset(); }
    }

    [Fact]
    public Task ReconfigureStartsTheRunAndTheReviewClosesOrSaysWhyItCouldNot() => OnDispatcher(async () =>
    {
        var build = RecommendedSetupInputs.Request(RecommendedSetupInputs.Fixture(DateTimeOffset.UtcNow));
        var recommendation = NetworkRecommender.Recommend(build.Request, FootprintCatalog.Default);
        Assert.NotEmpty(recommendation.Changes);
        var review = RecommendedSetupReview.From(recommendation, build);
        var owners = new List<Window>();
        string? problem = null;
        RecommendedSetupWindow Open()
        {
            var window = new RecommendedSetupWindow(review, _ => Task.FromResult(new RecommendedSetupPreflightView(["Install it."], true)),
                (owner, _, _) =>
                {
                    owners.Add(owner);
                    return problem;
                }) { ShowActivated = false, ShowInTaskbar = false };
            window.Show();
            return window;
        }

        // Started: the run window takes over (Background tasks lists it) and the review closes.
        var started = Open();
        var closed = false;
        started.Closed += (_, _) => closed = true;
        await Until(() => Field<Button>(started, "ApplyButton").IsEnabled);
        Click(started, "ApplyButton");
        Assert.Same(started, Assert.Single(owners));
        Assert.True(closed);
        Assert.NotNull(started.Outcome);

        // It couldn't start: the review stays open and says why.
        problem = "Martlet is already reconfiguring your computers. Background tasks shows its progress.";
        var refused = Open();
        try
        {
            await Until(() => Field<Button>(refused, "ApplyButton").IsEnabled);
            Click(refused, "ApplyButton");
            Assert.True(refused.IsVisible);
            Assert.Equal(problem, Field<TextBlock>(refused, "StatusText").Text);
            Assert.Equal(Visibility.Collapsed, Field<Button>(refused, "ApplyButton").Visibility);
            Assert.Equal(Visibility.Visible, Field<Button>(refused, "CloseButton").Visibility);
            Assert.Null(refused.Outcome);
        }
        finally { refused.Close(); }
    });

    [Fact]
    public void FinishedTaskOutputHidesPairingCodes() =>
        Assert.Equal("Code (code hidden), token martlet-pair-v1.(hidden).",
            HostRunLog.Mask("Code K7QM-4XPA, token martlet-pair-v1.abc_DEF-123."));

    [Fact]
    public Task BackgroundTasksPageListsRunsShowsThemAndCountsTheRunningOnes() => OnDispatcher(async () =>
    {
        BackgroundTasks.Reset();
        var confirm = BackgroundTasks.Confirm;
        var root = Path.Combine(AppContext.BaseDirectory, "background-tasks", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        _ = System.IO.Packaging.PackUriHelper.UriSchemePack;
        // The main window stays unshown, so nothing it starts once shown (status, sync, updates) runs; the runs have their own owner.
        var main = new MainWindow(new SettingsStore(Path.Combine(root, "data")), null) { ShowActivated = false, ShowInTaskbar = false };
        var owner = Owner();
        try
        {
            Assert.Equal(Visibility.Collapsed, Field<Border>(main, "NavTasksBadge").Visibility);
            Field<RadioButton>(main, "NavTasks").IsChecked = true;
            Assert.Equal(Visibility.Visible, Field<ScrollViewer>(main, "TasksPage").Visibility);
            Assert.Equal(Visibility.Collapsed, Field<ScrollViewer>(main, "HomePage").Visibility);
            Assert.Equal("Nothing has run in the background since Martlet started.", Field<TextBlock>(main, "TasksSummaryText").Text);
            Assert.Equal(Visibility.Visible, Field<Border>(main, "TasksEmptyCard").Visibility);
            Assert.False(Field<Button>(main, "TasksClearButton").IsEnabled);

            var finish = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            HostRunWindow? run = null;
            var result = HostRunWindow.RunAsync(owner, "Set up test-pc", window =>
            {
                run = window;
                window.Status("Installing...");
                return finish.Task;
            });
            Assert.NotNull(run);
            var id = run.BackgroundTask.Id;
            Assert.Equal(Visibility.Visible, Field<Border>(main, "NavTasksBadge").Visibility);
            Assert.Equal("1", Field<TextBlock>(main, "NavTasksCount").Text);
            Assert.Equal("Background tasks, 1 running", AutomationProperties.GetName(Field<RadioButton>(main, "NavTasks")));
            Assert.Equal("1 running now.", Field<TextBlock>(main, "TasksSummaryText").Text);
            Assert.Equal(Visibility.Collapsed, Field<Border>(main, "TasksEmptyCard").Visibility);
            Assert.Equal("Set up test-pc", ById<TextBlock>(main, $"TaskTitle-{id}").Text);
            Assert.StartsWith("Running for ", ById<TextBlock>(main, $"TaskState-{id}").Text, StringComparison.Ordinal);
            Assert.EndsWith("Installing...", ById<TextBlock>(main, $"TaskState-{id}").Text, StringComparison.Ordinal);
            Assert.Equal("Show", ById<Button>(main, $"TaskShow-{id}").Content);
            Assert.Equal(Visibility.Visible, ById<Button>(main, $"TaskCancel-{id}").Visibility);

            Click(run, "HostRunHide");
            Assert.False(run.IsVisible);
            Click(main, $"TaskShow-{id}");
            Assert.True(run.IsVisible);

            BackgroundTasks.Confirm = (_, _) => false;
            Click(main, $"TaskCancel-{id}");
            Assert.True(run.IsRunning);

            Click(run, "HostRunHide");
            finish.SetResult("test-pc is ready.");
            Assert.Equal("test-pc is ready.", await result);
            Assert.Equal(Visibility.Collapsed, Field<Border>(main, "NavTasksBadge").Visibility);
            Assert.Equal("Background tasks", AutomationProperties.GetName(Field<RadioButton>(main, "NavTasks")));
            Assert.StartsWith("Nothing is running now. 1 finished", Field<TextBlock>(main, "TasksSummaryText").Text, StringComparison.Ordinal);
            Assert.StartsWith("Done at ", ById<TextBlock>(main, $"TaskState-{id}").Text, StringComparison.Ordinal);
            Assert.Equal("Show output", ById<Button>(main, $"TaskShow-{id}").Content);
            Assert.Equal(Visibility.Collapsed, ById<Button>(main, $"TaskCancel-{id}").Visibility);
            Assert.True(Field<Button>(main, "TasksClearButton").IsEnabled);

            Click(main, "TasksClearButton");
            Assert.Empty(BackgroundTasks.All);
            Assert.Equal(Visibility.Visible, Field<Border>(main, "TasksEmptyCard").Visibility);
            Assert.Empty(Field<StackPanel>(main, "TasksList").Children);
        }
        finally
        {
            BackgroundTasks.Confirm = confirm;
            main.Close();
            owner.Close();
            BackgroundTasks.Reset();
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    });

    private static Window Owner()
    {
        var window = new Window { Width = 400, Height = 300, ShowActivated = false, ShowInTaskbar = false };
        window.Show();
        return window;
    }

    private static T Field<T>(Window window, string name) where T : FrameworkElement => Assert.IsType<T>(window.FindName(name));

    private static void Click(Window window, string id)
    {
        var button = window.FindName(id) as Button ?? ById<Button>(window, id);
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    private static T ById<T>(DependencyObject root, string id) where T : FrameworkElement =>
        Descendants(root).OfType<T>().SingleOrDefault(element => AutomationProperties.GetAutomationId(element) == id)
        ?? throw new InvalidOperationException("Required element missing: " + id);

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
        {
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(10);
        Assert.True(condition());
    }

    private static async Task OnDispatcher(Func<Task> action)
    {
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.UnhandledException += (_, args) =>
            {
                args.Handled = true;
                finished.TrySetException(args.Exception);
                dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
            };
            _ = dispatcher.BeginInvoke(async () =>
            {
                try { await action(); finished.TrySetResult(); }
                catch (Exception error) { finished.TrySetException(error); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Send); }
            });
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(60));
        Assert.True(thread.Join(TimeSpan.FromSeconds(5)));
    }
}
