using System.ComponentModel;
using System.Windows;
using Martlet.Core.Installation;
using Martlet.Presentation;

namespace Martlet.Desktop;

/// <summary>Shows one run on a Martlet host: the remote output as it streams, a status line, Hide and Cancel task. Reuse it for
/// any <see cref="HostShell"/> work: <c>await HostRunWindow.RunAsync(owner, "Prepare gpu-pc", async run =&gt; { ...
/// await new HostShell(dataDirectory, run.Prompts).RunAsync(target, command, run.Output, run.Token); return "Done."; })</c>.
/// Each run is a background task (<see cref="BackgroundTasks"/>): while it runs, Hide, Esc and the window's close button only
/// hide the window, Background tasks in the main window shows it again, and Cancel task asks first. A question the run asks
/// (a password, a role's choices) shows the window again with it. The window stays open afterwards so the owner can read the
/// output; a run that finishes hidden closes its window, and Background tasks keeps its output.</summary>
public partial class HostRunWindow : ThemedWindow
{
    private const int MaximumCharacters = 400_000;
    private static readonly List<HostRunWindow> runs = [];
    private readonly CancellationTokenSource cancel = new();
    private readonly TaskCompletionSource<string?> finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly string title;
    private readonly BackgroundTask task;
    private bool running, closed;
    /// <summary>The owner hid it (Hide, Esc or its close button), unlike Windows hiding it while its owner is minimized.</summary>
    private bool hiddenByUser;
    /// <summary>Martlet is exiting: the run stops and closing no longer hides the window.</summary>
    private bool interrupted;

    private HostRunWindow(string title)
    {
        InitializeComponent();
        this.title = title;
        Title = "Martlet - " + title;
        HeadingText.Text = title;
        Output = new Progress<string>(Append);
        Prompts = new HostShellDialogs(this);
        task = new BackgroundTask(title, RequestCancel) { Window = this };
        Closed += (_, _) => Forget();
    }

    /// <summary>A finished task's output again, from Background tasks.</summary>
    private HostRunWindow(BackgroundTask done)
    {
        InitializeComponent();
        task = done;
        title = done.Title;
        Title = "Martlet - " + title;
        HeadingText.Text = title;
        Output = new Progress<string>(_ => { });
        Prompts = new HostShellDialogs(this);
        OutputText.Text = done.Output;
        StatusText.Text = done.Status;
        ShowFinished();
        task.Window = this;
        Closed += (_, _) => Forget();
        Loaded += (_, _) => OutputText.ScrollToEnd();
    }

    /// <summary>What this run does, as its window shows it (for example "Set up this PC's host service").</summary>
    internal string Heading => title;

    /// <summary>The run is still working (exiting Martlet would interrupt it), shown or hidden.</summary>
    internal bool IsRunning => running;

    /// <summary>This run in Background tasks.</summary>
    internal BackgroundTask BackgroundTask => task;

    /// <summary>The runs still working, oldest first (UI thread). Several run side by side: setup steps they share wait for
    /// each other (<see cref="SharedSteps"/>) and changes to one host take turns in its engine lock.</summary>
    internal static IReadOnlyList<HostRunWindow> Running => runs.ToArray();

    /// <summary>Whether a run titled <paramref name="title"/> is still working.</summary>
    internal static bool IsRunningTitled(string title) => runs.Any(run => run.title == title);

    /// <summary>A run started or ended, or a running run's status changed (UI thread).</summary>
    internal static event Action? RunsChanged;

    /// <summary>The status line as it reads now.</summary>
    internal string CurrentStatus => StatusText.Text;

    /// <summary>Martlet is exiting: the run stops (its window closes with Martlet).</summary>
    internal void Interrupt()
    {
        interrupted = true;
        if (!running) return;
        Status("Canceling: Martlet is exiting...");
        cancel.Cancel();
    }

    /// <summary>Remote output lines; posts to this window.</summary>
    internal IProgress<string> Output { get; }
    /// <summary>Password, sudo and host-key questions, asked over this window.</summary>
    internal IHostShellPrompts Prompts { get; }
    internal CancellationToken Token => cancel.Token;

    /// <summary>Sets the status line; each change is also written to the run log, so a run that stalls shows its last step.</summary>
    internal void Status(string text)
    {
        if (StatusText.Text == text) return;
        StatusText.Text = text;
        task.SetStatus(text);
        HostRunLog.Write(title, "status: " + text);
        if (running) RunsChanged?.Invoke();
    }

    /// <summary>Opens a run window over <paramref name="owner"/> and runs <paramref name="job"/> (on the UI thread; await
    /// the runner). Returns the job's summary, or null when it failed or was canceled (the reason is shown). With
    /// <paramref name="join"/>, a run with the same title that is still working is brought forward (shown again when it was
    /// hidden) and waited for instead of starting the same work a second time (a second click on the same step); its summary
    /// is returned. <paramref name="keeper"/>: the window the run stays with when <paramref name="owner"/> closes first
    /// (Martlet's main window unless given).</summary>
    internal static async Task<string?> RunAsync(Window owner, string title, Func<HostRunWindow, Task<string>> job, bool join = false,
        Window? keeper = null)
    {
        if (join && runs.FirstOrDefault(run => run.title == title) is { } same)
        {
            ErrorLog.Info($"Host run already working, waiting for it: {title}");
            same.BringForward();
            return await same.finished.Task;
        }
        var window = new HostRunWindow(title) { Owner = owner };
        window.OutliveOwner(owner, keeper ?? Application.Current?.MainWindow);
        window.Show();
        return await window.RunAsync(job);
    }

    /// <summary>Shows <paramref name="task"/>'s window again over <paramref name="owner"/>: the run itself while it is open
    /// (hidden or not), otherwise the finished task's output.</summary>
    internal static HostRunWindow ShowTask(Window owner, BackgroundTask task)
    {
        var window = task.Window ?? new HostRunWindow(task) { Owner = owner };
        window.BringForward();
        return window;
    }

    /// <summary>Shows the window again (also when it was hidden) and activates it.</summary>
    private void BringForward()
    {
        hiddenByUser = false;
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        BackgroundTasks.Notify();
    }

    private async Task<string?> RunAsync(Func<HostRunWindow, Task<string>> job)
    {
        running = true;
        runs.Add(this);
        BackgroundTasks.Add(task);
        RunsChanged?.Invoke();
        ErrorLog.Info($"Host run started: {title} (output: {HostRunLog.Path ?? "unavailable"})");
        HostRunLog.Write(title, "--- started");
        var state = BackgroundTaskState.Stopped;
        string? summary = null;
        try
        {
            summary = await job(this);
            Status(summary);
            Append("Finished. " + summary);
            ErrorLog.Info($"Host run finished: {title}: {summary}");
            state = BackgroundTaskState.Done;
            return summary;
        }
        catch (OperationCanceledException)
        {
            Status("Canceled.");
            HostRunLog.Write(title, "--- canceled");
            ErrorLog.Info($"Host run canceled: {title}");
            state = BackgroundTaskState.Canceled;
            return null;
        }
        catch (PausedForRestartException paused)
        {
            Status(paused.Message);
            Append("Paused: " + paused.Message);
            ErrorLog.Info($"Host run paused for a Windows restart: {title}");
            state = BackgroundTaskState.Paused;
            return null;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            Status(error.Message);
            Append("Stopped: " + error.Message);
            ErrorLog.Warn($"Host run failed: {title}", error);
            return null;
        }
        finally
        {
            running = false;
            runs.Remove(this);
            ShowFinished();
            task.Finish(state, StatusText.Text);
            finished.TrySetResult(summary);
            RunsChanged?.Invoke();
            // A run that finished while hidden closes its window; Background tasks keeps its output.
            if (hiddenByUser && !closed) Close();
        }
    }

    private void Append(string line)
    {
        HostRunLog.Write(title, line);
        Show(line);
    }

    /// <summary>Shows a line that must not reach the run log (for example a one-use pairing code).</summary>
    internal void Reveal(string line) => Show(line);

    /// <summary>Shows the address and one-use code another desktop types to pair (never logged); null hides them and takes a
    /// copied code back off the clipboard.</summary>
    internal void ShowPairingCode(string? address, string? code)
    {
        if (code != PairingCodeText.Text && copiedCode is { } copied)
        {
            SecretClipboard.Withdraw(copied);
            copiedCode = null;
        }
        PairingAddressText.Text = address ?? "";
        PairingCodeText.Text = code ?? "";
        PairingPanel.Visibility = code is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private string? copiedCode;

    private void CopyPairingCode_Click(object sender, RoutedEventArgs e)
    {
        var code = PairingCodeText.Text;
        if (code.Length == 0) return;
        var copied = SecretClipboard.Copy(code);
        if (copied) copiedCode = code;
        CopyText.Acknowledge(PairingCopyButton, copied);
    }

    private void Show(string line)
    {
        OutputText.AppendText(line + Environment.NewLine);
        if (OutputText.Text.Length > MaximumCharacters)
            OutputText.Text = OutputText.Text[^(MaximumCharacters / 2)..];
        OutputText.ScrollToEnd();
    }

    /// <summary>The window that started the run (the hosts wizard, a dialog) can close while the run goes on, shown or hidden:
    /// the run then stays with Martlet's main window instead of closing with it.</summary>
    private void OutliveOwner(Window owner, Window? main)
    {
        if (main is null || ReferenceEquals(owner, main)) return;
        void OwnerClosing(object? sender, CancelEventArgs e)
        {
            if (e.Cancel || !running || closed || !ReferenceEquals(Owner, owner)) return;
            try { Owner = main; }
            catch (InvalidOperationException) { Owner = null; }
        }
        owner.Closing += OwnerClosing;
        Closed += (_, _) => owner.Closing -= OwnerClosing;
    }

    /// <summary>A question about this run (a password, a role's choices, a confirmation) shows its hidden window with it.</summary>
    private protected override void OwnedWindowShowing(Window owned)
    {
        if (!hiddenByUser || !running || closed) return;
        hiddenByUser = false;
        // The question takes the focus; WPF can't show a maximized window without activating it.
        var activated = ShowActivated;
        if (WindowState != WindowState.Maximized) ShowActivated = false;
        try { Show(); }
        finally { ShowActivated = activated; }
        // Like a window a question blocks, the run's own buttons wait for the answer.
        IsEnabled = false;
        owned.Closed += (_, _) => IsEnabled = true;
        BackgroundTasks.Notify();
    }

    private void ShowFinished()
    {
        HideButton.Content = "_Close";
        HideButton.ToolTip = null;
        CancelButton.Visibility = Visibility.Collapsed;
        HideHint.Visibility = Visibility.Collapsed;
    }

    /// <summary>Hides the window of a run that keeps going; Background tasks shows it again.</summary>
    private void HideRun()
    {
        hiddenByUser = true;
        Hide();
        ErrorLog.Info($"Run window hidden; the run keeps going in Background tasks: {title}");
        BackgroundTasks.Notify();
    }

    /// <summary>Stops the run now (Cancel task, after its question, or Background tasks' Cancel).</summary>
    private void RequestCancel()
    {
        if (!running || cancel.IsCancellationRequested) return;
        Status("Canceling...");
        cancel.Cancel();
    }

    private void Forget()
    {
        closed = true;
        task.Output = HostRunLog.Mask(OutputText.Text);
        if (ReferenceEquals(task.Window, this)) task.Window = null;
        BackgroundTasks.Notify();
    }

    private void Hide_Click(object sender, RoutedEventArgs e)
    {
        if (running) HideRun();
        else Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => BackgroundTasks.AskToCancel(this, task);

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!running || interrupted) return;
        // Closing a running task's window only hides it: the task keeps going in Background tasks.
        e.Cancel = true;
        HideRun();
    }
}
/// <summary>Runs martlet-host actions (setup, status, update, add or remove a role) on a host Martlet reaches, from anywhere
/// in the desktop, in a run window: over SSH, on this PC's Docker Desktop, or through Martlet on that computer (commands
/// sent over its paired gateway, <see cref="HostAgentRun"/>). Never in a console window.</summary>
internal static class HostActions
{
    /// <summary>The run that sets up this PC's own host service (from the host dashboard, the Devices map or Add a computer).</summary>
    internal const string ThisPcSetupTitle = "Set up this PC as a host";

    /// <summary>Runs <paramref name="action"/>. <paramref name="answers"/> are role answers the owner already chose in Martlet
    /// (then no role dialog is shown); otherwise adding a role asks for its secrets and choices, preselecting
    /// <paramref name="recommended"/>. <paramref name="pairing"/> is the host's pairing, which the Martlet-on-that-computer
    /// route uses. <paramref name="confirmed"/>: the owner already agreed to this removal (switching voice engines on a
    /// host stops the others), so the run window doesn't ask again. Returns a summary, or null when it stopped (the window
    /// shows why).</summary>
    internal static Task<string?> RunAsync(Window owner, string dataDirectory, HostSetupTarget target, string? pinnedHostKey,
        HostAction action, IReadOnlyDictionary<string, string>? answers = null,
        IReadOnlyDictionary<string, (string Value, string Why)>? recommended = null,
        Martlet.Avatar.Hosting.AvatarRemoteHost? pairing = null, bool confirmed = false) => target.Method switch
    {
        HostSetupMethod.SshDocker or HostSetupMethod.SshNative =>
            RunOverSshAsync(owner, dataDirectory, target, pinnedHostKey, action, answers, recommended, confirmed),
        HostSetupMethod.ThisPcDocker => RunOnThisPcAsync(owner, target, action, answers, recommended, confirmed),
        HostSetupMethod.Agent or HostSetupMethod.OnHost when pairing is not null =>
            HostAgentRun.RunAsync(owner, pairing, target.Version, action, answers, recommended, confirmed),
        _ => throw new InvalidOperationException("Pair this host first; then Martlet reaches it through Martlet on that computer.")
    };

    private static (string Engine, string? Role, bool Add) Parse(HostAction action)
    {
        var engine = HostSetupCommands.Engine(action);
        var words = engine.Split(' ');
        return (engine, words.Length == 2 && words[0] is "add" or "remove" ? words[1] : null, words[0] == "add");
    }

    private static Task<string?> RunOverSshAsync(Window owner, string dataDirectory, HostSetupTarget target, string? pinnedHostKey,
        HostAction action, IReadOnlyDictionary<string, string>? answers, IReadOnlyDictionary<string, (string Value, string Why)>? recommended,
        bool confirmed)
    {
        var (engine, role, add) = Parse(action);
        var ssh = HostShellTarget.Parse(target.SshTarget);
        var title = role is null ? action.Verb switch
        {
            HostVerb.Status => $"Check {ssh}",
            HostVerb.Update => $"Update {ssh}",
            HostVerb.Setup => $"Set up {ssh}",
            HostVerb.Pair => $"Pair with {ssh}",
            _ => $"Work on {ssh}"
        } : $"{(add ? "Add" : "Remove")} {role} on {ssh}";
        return HostRunWindow.RunAsync(owner, title, async run =>
        {
            var remote = new HostRemote(new HostShell(dataDirectory, run.Prompts));
            run.Status($"Connecting to {ssh}...");
            var (probe, hostKey) = await remote.ProbeAsync(ssh, pinnedHostKey, run.Token);
            var sudo = HostRemote.NeedsSudo(target.Method, probe);
            var supplied = await remote.SupplyIfOfflineAsync(target, action.Verb, dataDirectory, hostKey, run.Output, run.Token);
            var input = answers;
            if (role is not null && add && input is null)
            {
                run.Status($"Checking what {role} needs...");
                var inputs = await remote.DescribeAsync(target, role, sudo, hostKey, run.Output, run.Token);
                input = HostInputDialog.ForRole(run, ssh.ToString(), role, inputs, recommended) ?? throw new OperationCanceledException();
            }
            else if (role is not null && !add && !confirmed && !ConfirmationDialog.Confirm(run,
                         $"Remove {role} from {ssh}? Its saved data stays so you can add it again later.",
                         "Remove role"))
                throw new OperationCanceledException();
            run.Status(role is null ? $"Working on {ssh}..." :
                add ? $"{role} is installing on {ssh}. This can take a while..."
                    : $"Removing {role} from {ssh}...");
            var output = new EngineOutput(run.Output);
            var result = await remote.RunAsync(target, engine, action == HostAction.Setup, sudo, input, hostKey, output, run.Token,
                supplied: supplied);
            if (output.Busy(result.ExitCode) is { } busy)
                throw new InvalidOperationException($"{ssh} stayed busy with another change ({busy}), so nothing was changed. Try again when it finishes.");
            if (result.ExitCode != 0)
                throw new InvalidOperationException($"The host action did not finish on {ssh} (exit {result.ExitCode}). Check the output for details.");
            return role is null ? $"Finished on {ssh}."
                : add ? $"{role} is ready on {ssh}."
                : $"{role} was removed from {ssh}.";
        }, join: true);
    }

    private static Task<string?> RunOnThisPcAsync(Window owner, HostSetupTarget target, HostAction action,
        IReadOnlyDictionary<string, string>? answers, IReadOnlyDictionary<string, (string Value, string Why)>? recommended, bool confirmed)
    {
        var (engine, role, add) = Parse(action);
        var title = action.Verb switch
        {
            HostVerb.Add => $"Add {role} to this PC",
            HostVerb.Remove => $"Remove {role} from this PC",
            HostVerb.Setup => ThisPcSetupTitle,
            HostVerb.Update => "Update this PC's host",
            HostVerb.Status => "Check this PC's host",
            _ => "Work on this PC's host"
        };
        if (action.Verb == HostVerb.Pair) throw new InvalidOperationException("Use the Pair step to pair a desktop.");
        return HostRunWindow.RunAsync(owner, title, async run =>
        {
            var input = answers;
            if (role is not null && !add && !confirmed && !ConfirmationDialog.Confirm(run,
                    $"Remove {role} from this PC? Its saved data stays so you can add it again later.",
                    "Remove role"))
                throw new OperationCanceledException();
            await HostLocal.EnsureDockerAsync(run, action.Verb == HostVerb.Setup ? ContinueSetupKind.HostService : ContinueSetupKind.Docker);
            await HostLocal.EnsureImageAsync(target, run);
            if (role is not null && add && input is null)
            {
                run.Status($"Checking what {role} needs...");
                var inputs = await HostLocal.DescribeAsync(target, role, run.Output, run.Token);
                input = HostInputDialog.ForRole(run, "this PC", role, inputs, recommended, local: true) ?? throw new OperationCanceledException();
            }
            run.Status(action.Verb switch
            {
                HostVerb.Add => $"{role} is installing on this PC. This can take a while...",
                HostVerb.Remove => $"Removing {role} from this PC...",
                HostVerb.Setup => "Setting up this PC as a host...",
                HostVerb.Update => "Updating this PC's host. Pairings and roles stay...",
                _ => "Working on this PC..."
            });
            var output = new EngineOutput(run.Output);
            var exit = await HostLocal.EngineAsync(target, engine.Split(' '), output, run.Token, answers: input);
            if (output.Busy(exit) is { } busy)
                throw new InvalidOperationException($"This PC's host stayed busy with another change ({busy}), so nothing was changed. Try again when it finishes.");
            if (exit != 0) throw new InvalidOperationException($"The host action did not finish on this PC (exit {exit}). Check the output for details.");
            return action.Verb switch
            {
                HostVerb.Add => $"{role} is ready on this PC.",
                HostVerb.Remove => $"{role} was removed from this PC.",
                HostVerb.Setup => "This PC is set up as a host. Pair your main PC next.",
                HostVerb.Update => "This PC's host is up to date.",
                _ => "Finished on this PC."
            };
        }, join: true);
    }

    /// <summary>Lets another desktop pair with this PC's host service: shows this PC's address and a short one-use code
    /// (never logged) and waits until that desktop types them. The code doesn't expire; Cancel (or closing the window)
    /// withdraws it, and this PC's host jobs pause until then.</summary>
    internal static Task<string?> PairOtherDesktopAsync(Window owner, HostSetupTarget target) =>
        HostRunWindow.RunAsync(owner, "Pair your main PC", async run =>
        {
            await HostLocal.EnsureDockerAsync(run, ContinueSetupKind.Docker);
            await HostLocal.EnsureImageAsync(target, run);
            run.Status("Getting a pairing code...");
            var shown = false;
            int exit;
            try
            {
                exit = await HostLocal.PairOtherAsync(target, (address, code) => run.Dispatcher.Invoke(() =>
                {
                    shown = true;
                    run.ShowPairingCode(address, code);
                    run.Status("Enter the address and code on your main PC. Waiting (Cancel withdraws the code)...");
                }), run.Output, run.Token);
            }
            finally { run.Dispatcher.Invoke(() => run.ShowPairingCode(null, null)); }
            if (!shown) throw new InvalidOperationException($"Couldn't get a pairing code (exit {exit}). Check the output for details.");
            if (exit != 0) throw new InvalidOperationException($"Pairing didn't finish (exit {exit}). Check the output, then show a new code.");
            return "Your main PC is paired with this host.";
        }, join: true);
}