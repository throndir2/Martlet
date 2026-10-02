using System.ComponentModel;
using System.Windows;
using Martlet.Core.Installation;

namespace Martlet.Desktop;

/// <summary>Shows one run on a Martlet host: the remote output as it streams, a status line and Cancel. Reuse it for any
/// <see cref="HostShell"/> work: <c>await HostRunWindow.RunAsync(owner, "Prepare gpu-pc", async run =&gt; { ...
/// await new HostShell(dataDirectory, run.Prompts).RunAsync(target, command, run.Output, run.Token); return "Done."; })</c>.
/// The window stays open afterwards so the owner can read the output.</summary>
public partial class HostRunWindow : ThemedWindow
{
    private const int MaximumCharacters = 400_000;
    private readonly CancellationTokenSource cancel = new();
    private bool running;

    private HostRunWindow(string title)
    {
        InitializeComponent();
        Title = "Martlet - " + title;
        HeadingText.Text = title;
        this.title = title;
        Output = new Progress<string>(Append);
        Prompts = new HostShellDialogs(this);
    }

    private readonly string title;

    /// <summary>What this run does, as its window shows it (for example "Set up this PC's host service").</summary>
    internal string Heading => title;

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
        HostRunLog.Write(title, "status: " + text);
    }

    /// <summary>Opens a run window over <paramref name="owner"/> and runs <paramref name="job"/> (on the UI thread; await
    /// the runner). Returns the job's summary, or null when it failed or was canceled (the reason is shown).</summary>
    internal static async Task<string?> RunAsync(Window owner, string title, Func<HostRunWindow, Task<string>> job)
    {
        var window = new HostRunWindow(title) { Owner = owner };
        window.Show();
        return await window.RunAsync(job);
    }

    private async Task<string?> RunAsync(Func<HostRunWindow, Task<string>> job)
    {
        running = true;
        ErrorLog.Info($"Host run started: {title} (output: {HostRunLog.Path ?? "unavailable"})");
        HostRunLog.Write(title, "--- started");
        try
        {
            var summary = await job(this);
            Status(summary);
            Append("Finished. " + summary);
            ErrorLog.Info($"Host run finished: {title}: {summary}");
            return summary;
        }
        catch (OperationCanceledException)
        {
            Status("Canceled.");
            HostRunLog.Write(title, "--- canceled");
            ErrorLog.Info($"Host run canceled: {title}");
            return null;
        }
        catch (PausedForRestartException paused)
        {
            Status(paused.Message);
            Append("Paused: " + paused.Message);
            ErrorLog.Info($"Host run paused for a Windows restart: {title}");
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
            CancelButton.Content = "_Close";
        }
    }

    private void Append(string line)
    {
        HostRunLog.Write(title, line);
        Show(line);
    }

    /// <summary>Shows a line that must not reach the run log (for example a one-use pairing code).</summary>
    internal void Reveal(string line) => Show(line);

    /// <summary>Shows the address and one-use code another desktop types to pair (never logged); null hides them.</summary>
    internal void ShowPairingCode(string? address, string? code)
    {
        PairingAddressText.Text = address ?? "";
        PairingCodeText.Text = code ?? "";
        PairingPanel.Visibility = code is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Show(string line)
    {
        OutputText.AppendText(line + Environment.NewLine);
        if (OutputText.Text.Length > MaximumCharacters)
            OutputText.Text = OutputText.Text[^(MaximumCharacters / 2)..];
        OutputText.ScrollToEnd();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (!running) { Close(); return; }
        Status("Canceling...");
        cancel.Cancel();
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (running) cancel.Cancel();
    }
}

/// <summary>Runs martlet-host actions (setup, status, update, add or remove a role) on a host Martlet reaches, from anywhere
/// in the desktop, in a run window: over SSH, or on this PC's Docker Desktop. Never in a console window.</summary>
internal static class HostActions
{
    /// <summary>Runs <paramref name="action"/>. <paramref name="answers"/> are role answers the owner already chose in Martlet
    /// (then no role dialog is shown); otherwise adding a role asks for its secrets and choices, preselecting
    /// <paramref name="recommended"/>. Returns a summary, or null when it stopped (the window shows why).</summary>
    internal static Task<string?> RunAsync(Window owner, string dataDirectory, HostSetupTarget target, string? pinnedHostKey,
        HostAction action, IReadOnlyDictionary<string, string>? answers = null,
        IReadOnlyDictionary<string, (string Value, string Why)>? recommended = null) => target.Method switch
    {
        HostSetupMethod.SshDocker or HostSetupMethod.SshNative =>
            RunOverSshAsync(owner, dataDirectory, target, pinnedHostKey, action, answers, recommended),
        HostSetupMethod.ThisPcDocker => RunOnThisPcAsync(owner, target, action, answers, recommended),
        _ => throw new InvalidOperationException("Martlet can't reach this host from here. Run the command on the host instead.")
    };

    private static (string Engine, string? Role, bool Add) Parse(HostAction action)
    {
        var engine = HostSetupCommands.Engine(action);
        var words = engine.Split(' ');
        return (engine, words.Length == 2 && words[0] is "add" or "remove" ? words[1] : null, words[0] == "add");
    }

    private static Task<string?> RunOverSshAsync(Window owner, string dataDirectory, HostSetupTarget target, string? pinnedHostKey,
        HostAction action, IReadOnlyDictionary<string, string>? answers, IReadOnlyDictionary<string, (string Value, string Why)>? recommended)
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
            var input = answers;
            if (role is not null && add && input is null)
            {
                run.Status($"Checking what {role} needs...");
                var inputs = await remote.DescribeAsync(target, role, sudo, hostKey, run.Output, run.Token);
                input = HostInputDialog.ForRole(run, ssh.ToString(), role, inputs, recommended) ?? throw new OperationCanceledException();
            }
            else if (role is not null && !add && !ConfirmationDialog.Confirm(run,
                         $"Remove {role} from {ssh}? Its saved data stays so you can add it again later.",
                         "Remove role"))
                throw new OperationCanceledException();
            run.Status(role is null ? $"Working on {ssh}..." :
                add ? $"{role} is installing on {ssh}. This can take a while..."
                    : $"Removing {role} from {ssh}...");
            var result = await remote.RunAsync(target, engine, action == HostAction.Setup, sudo, input, hostKey, run.Output, run.Token);
            if (result.ExitCode != 0)
                throw new InvalidOperationException($"The host action did not finish on {ssh} (exit {result.ExitCode}). Check the output for details.");
            return role is null ? $"Finished on {ssh}."
                : add ? $"{role} is ready on {ssh}."
                : $"{role} was removed from {ssh}.";
        });
    }

    private static Task<string?> RunOnThisPcAsync(Window owner, HostSetupTarget target, HostAction action,
        IReadOnlyDictionary<string, string>? answers, IReadOnlyDictionary<string, (string Value, string Why)>? recommended)
    {
        var (engine, role, add) = Parse(action);
        var title = action.Verb switch
        {
            HostVerb.Add => $"Add {role} to this PC",
            HostVerb.Remove => $"Remove {role} from this PC",
            HostVerb.Setup => "Set up this PC as a host",
            HostVerb.Update => "Update this PC's host",
            HostVerb.Status => "Check this PC's host",
            _ => "Work on this PC's host"
        };
        if (action.Verb == HostVerb.Pair) throw new InvalidOperationException("Use the Pair step to pair a desktop.");
        return HostRunWindow.RunAsync(owner, title, async run =>
        {
            var input = answers;
            if (role is not null && !add && !ConfirmationDialog.Confirm(run,
                    $"Remove {role} from this PC? Its saved data stays so you can add it again later.",
                    "Remove role"))
                throw new OperationCanceledException();
            await HostLocal.EnsureDockerAsync(run, action.Verb == HostVerb.Setup ? ContinueSetupKind.HostService : ContinueSetupKind.Docker);
            await HostLocal.EnsureImageAsync(target, run.Status, run.Output, run.Token);
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
            var exit = await HostLocal.EngineAsync(target, engine.Split(' '), run.Output, run.Token, answers: input);
            if (exit != 0) throw new InvalidOperationException($"The host action did not finish on this PC (exit {exit}). Check the output for details.");
            return action.Verb switch
            {
                HostVerb.Add => $"{role} is ready on this PC.",
                HostVerb.Remove => $"{role} was removed from this PC.",
                HostVerb.Setup => "This PC is set up as a host. Pair your main PC next.",
                HostVerb.Update => "This PC's host is up to date.",
                _ => "Finished on this PC."
            };
        });
    }

    /// <summary>Lets another desktop pair with this PC's host service: shows this PC's address and a short one-use code
    /// (never logged) and waits up to five minutes for that desktop to type them.</summary>
    internal static Task<string?> PairOtherDesktopAsync(Window owner, HostSetupTarget target) =>
        HostRunWindow.RunAsync(owner, "Pair your main PC", async run =>
        {
            await HostLocal.EnsureDockerAsync(run, ContinueSetupKind.Docker);
            await HostLocal.EnsureImageAsync(target, run.Status, run.Output, run.Token);
            run.Status("Getting a pairing code...");
            var shown = false;
            int exit;
            try
            {
                exit = await HostLocal.PairOtherAsync(target, (address, code) => run.Dispatcher.Invoke(() =>
                {
                    shown = true;
                    run.ShowPairingCode(address, code);
                    run.Status("Enter the address and code on your main PC within five minutes. Waiting...");
                }), run.Output, run.Token);
            }
            finally { run.Dispatcher.Invoke(() => run.ShowPairingCode(null, null)); }
            if (!shown) throw new InvalidOperationException($"Couldn't get a pairing code (exit {exit}). Check the output for details.");
            if (exit != 0) throw new InvalidOperationException($"Pairing didn't finish (exit {exit}). Show a new code and enter it within five minutes.");
            return "Your main PC is paired with this host.";
        });
}