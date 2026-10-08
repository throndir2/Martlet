using System.Globalization;
using System.Windows;

namespace Martlet.Desktop;

internal enum BackgroundTaskState { Running, Done, Stopped, Canceled, Paused }

/// <summary>One long step Martlet runs in a run window (<see cref="HostRunWindow"/>): setting up a host, a download, pairing...
/// Hiding its window (Hide, Esc or the window's close button) keeps it running; Background tasks in Martlet's main window
/// lists it, shows its window again and cancels it, always asking first. A finished task stays listed with its output until
/// it is cleared or Martlet exits; host-runs.log keeps every run's output.</summary>
internal sealed class BackgroundTask
{
    private static int lastId;
    private readonly Action cancel;

    internal BackgroundTask(string title, Action cancel, DateTimeOffset? started = null)
    {
        Id = Interlocked.Increment(ref lastId);
        Title = title;
        this.cancel = cancel;
        Started = started ?? DateTimeOffset.Now;
    }

    internal int Id { get; }
    /// <summary>What it does, as its window shows it ("Set up gpu-pc").</summary>
    internal string Title { get; }
    internal DateTimeOffset Started { get; }
    internal DateTimeOffset? Ended { get; private set; }
    internal BackgroundTaskState State { get; private set; }
    /// <summary>Its status line: the step it is on, or how it ended.</summary>
    internal string Status { get; private set; } = "Starting...";
    internal bool IsRunning => State == BackgroundTaskState.Running;
    /// <summary>The output its window showed, kept once the window closes (pairing codes masked).</summary>
    internal string Output { get; set; } = "";
    /// <summary>Its open window, shown or hidden; null once closed.</summary>
    internal HostRunWindow? Window { get; set; }

    internal void SetStatus(string status)
    {
        if (Status == status) return;
        Status = status;
        BackgroundTasks.Notify();
    }

    internal void Finish(BackgroundTaskState state, string status, DateTimeOffset? ended = null)
    {
        if (!IsRunning || state == BackgroundTaskState.Running) return;
        State = state;
        Status = status;
        Ended = ended ?? DateTimeOffset.Now;
        BackgroundTasks.Notify();
    }

    /// <summary>Stops it now, without asking (<see cref="BackgroundTasks.AskToCancel"/> asks).</summary>
    internal void Cancel()
    {
        if (IsRunning) cancel();
    }

    /// <summary>Its line in Background tasks: whether it runs (and for how long) or how it ended, then its status.</summary>
    internal string Describe(DateTimeOffset now)
    {
        var at = (Ended ?? now).ToLocalTime().ToString("t", CultureInfo.CurrentCulture);
        var took = Duration((Ended ?? now) - Started);
        var state = State switch
        {
            BackgroundTaskState.Running => $"Running for {took}",
            BackgroundTaskState.Done => $"Done at {at} after {took}",
            BackgroundTaskState.Canceled => $"Canceled at {at} after {took}",
            BackgroundTaskState.Paused => $"Paused at {at} for a Windows restart",
            _ => $"Stopped at {at} after {took}"
        };
        return Status.Length == 0 || State == BackgroundTaskState.Canceled && Status == "Canceled." ? state + "." : $"{state}. {Status}";
    }

    internal static string Duration(TimeSpan span)
    {
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;
        if (span < TimeSpan.FromMinutes(1)) return $"{Math.Max(1, (int)span.TotalSeconds)} s";
        if (span < TimeSpan.FromHours(1)) return $"{(int)span.TotalMinutes} min";
        return span.Minutes == 0 ? $"{(int)span.TotalHours} h" : $"{(int)span.TotalHours} h {span.Minutes} min";
    }
}

/// <summary>Martlet's background tasks this session, newest first (on the UI thread): every run window's run, running or
/// finished. Changed tells Background tasks (and its count in the navigation rail) to follow.</summary>
internal static class BackgroundTasks
{
    /// <summary>Finished tasks kept with their output; older ones drop off the list (host-runs.log keeps them).</summary>
    internal const int MaximumFinished = 20;

    private static readonly List<BackgroundTask> tasks = [];

    internal static event Action? Changed;

    internal static IReadOnlyList<BackgroundTask> All => [.. tasks];
    internal static int RunningCount => tasks.Count(task => task.IsRunning);

    /// <summary>Asks before canceling a task (owner, task); true cancels. Tests replace it.</summary>
    internal static Func<Window, BackgroundTask, bool> Confirm { get; set; } = (owner, task) =>
        ConfirmationDialog.Confirm(owner,
            $"Cancel \"{task.Title}\"?\n\nWhatever it hasn't finished stops now, so you may need to start it again.\n\n" +
            "Keep running lets it finish. Hiding its window doesn't stop it: it stays in Background tasks.",
            "Cancel task", yes: "_Cancel task", no: "_Keep running", questionId: "CancelTaskQuestion");

    internal static BackgroundTask Start(string title, Action cancel) => Add(new BackgroundTask(title, cancel));

    /// <summary>Lists <paramref name="task"/> first, as it starts.</summary>
    internal static BackgroundTask Add(BackgroundTask task)
    {
        if (!tasks.Contains(task)) tasks.Insert(0, task);
        Notify();
        return task;
    }

    /// <summary>Asks first, then cancels <paramref name="task"/>; true when it was canceled.</summary>
    internal static bool AskToCancel(Window owner, BackgroundTask task)
    {
        if (!task.IsRunning || !Confirm(owner, task) || !task.IsRunning) return false;
        ErrorLog.Info($"Background task canceled by the owner: {task.Title}");
        task.Cancel();
        return true;
    }

    /// <summary>Drops finished tasks and their kept output from the list; running tasks stay, and an open window stays open.</summary>
    internal static void ClearFinished()
    {
        if (tasks.RemoveAll(task => !task.IsRunning) > 0) Notify();
    }

    /// <summary>Drops one finished task that changed nothing (an automatic host update that found the host busy and tries again
    /// a few minutes later), so the retries don't fill the list; host-runs.log keeps its output. One whose window is open stays.</summary>
    internal static void Discard(BackgroundTask task)
    {
        if (!task.IsRunning && task.Window is null && tasks.Remove(task)) Notify();
    }

    /// <summary>Forgets every task, without stopping any (tests).</summary>
    internal static void Reset()
    {
        tasks.Clear();
        Notify();
    }

    internal static void Notify()
    {
        // The oldest finished tasks drop off once there are too many (one whose window is still open stays until it closes).
        foreach (var old in tasks.Where(task => !task.IsRunning).Skip(MaximumFinished).Where(task => task.Window is null).ToList())
            tasks.Remove(old);
        Changed?.Invoke();
    }
}
