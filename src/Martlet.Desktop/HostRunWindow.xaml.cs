using System.ComponentModel;
using System.Windows;

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
            Append("Done. " + summary);
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
        _ => throw new InvalidOperationException("Martlet does not know how to reach this host; run the command on it instead.")
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
        var title = role is null ? $"{ssh}: martlet-host {engine}" : $"{(add ? "Add" : "Remove")} {role} on {ssh}";
        return HostRunWindow.RunAsync(owner, title, async run =>
        {
            var remote = new HostRemote(new HostShell(dataDirectory, run.Prompts));
            run.Status($"Connecting to {ssh}...");
            var (probe, hostKey) = await remote.ProbeAsync(ssh, pinnedHostKey, run.Token);
            var sudo = HostRemote.NeedsSudo(target.Method, probe);
            var input = answers;
            if (role is not null && add && input is null)
            {
                run.Status($"Reading what {role} needs...");
                var inputs = await remote.DescribeAsync(target, role, sudo, hostKey, run.Output, run.Token);
                input = HostInputDialog.ForRole(run, ssh.ToString(), role, inputs, recommended) ?? throw new OperationCanceledException();
            }
            else if (role is not null && !add && !ConfirmationDialog.Confirm(run,
                         $"Stop {role} on {ssh} and remove it from the gateway? Its data volumes are kept, so adding it again is quick.",
                         "Remove role"))
                throw new OperationCanceledException();
            run.Status(role is null ? $"Running {engine} on {ssh}..." :
                add ? $"Installing {role} on {ssh}. Large downloads can take a while; this PC picks the role up when it runs."
                    : $"Removing {role} from {ssh}...");
            var result = await remote.RunAsync(target, engine, action == HostAction.Setup, sudo, input, hostKey, run.Output, run.Token);
            if (result.ExitCode != 0)
                throw new InvalidOperationException($"martlet-host {engine} stopped on {ssh} (exit {result.ExitCode}). The output shows why.");
            return role is null ? $"Finished {engine} on {ssh}."
                : add ? $"{role} is running on {ssh} and published through its gateway. Check the host to see it here."
                : $"{role} was removed from {ssh}.";
        });
    }

    private static Task<string?> RunOnThisPcAsync(Window owner, HostSetupTarget target, HostAction action,
        IReadOnlyDictionary<string, string>? answers, IReadOnlyDictionary<string, (string Value, string Why)>? recommended)
    {
        var (engine, role, add) = Parse(action);
        var title = action.Verb switch
        {
            HostVerb.Add => $"Add {role} to this PC's host service",
            HostVerb.Remove => $"Remove {role} from this PC's host service",
            HostVerb.Setup => "Set up this PC's host service",
            HostVerb.Update => "Update this PC's host service",
            HostVerb.Status => "This PC's host service",
            _ => $"This PC's host service: {engine}"
        };
        if (action.Verb == HostVerb.Pair) throw new InvalidOperationException("Pair a desktop from Martlet's pairing step instead.");
        return HostRunWindow.RunAsync(owner, title, async run =>
        {
            var input = answers;
            if (role is not null && !add && !ConfirmationDialog.Confirm(run,
                    $"Stop {role} in this PC's host service and remove it from the gateway? Its data volumes are kept, so adding it again is quick.",
                    "Remove role"))
                throw new OperationCanceledException();
            await HostLocal.EnsureDockerAsync(run.Status, run.Output, run.Token);
            await HostLocal.EnsureImageAsync(target, run.Status, run.Output, run.Token);
            if (role is not null && add && input is null)
            {
                run.Status($"Reading what {role} needs...");
                var inputs = await HostLocal.DescribeAsync(target, role, run.Output, run.Token);
                input = HostInputDialog.ForRole(run, "this PC", role, inputs, recommended, local: true) ?? throw new OperationCanceledException();
            }
            run.Status(action.Verb switch
            {
                HostVerb.Add => $"Installing {role} on this PC. Large downloads can take a while; Martlet picks the role up when it runs.",
                HostVerb.Remove => $"Removing {role} from this PC's host service...",
                HostVerb.Setup => "Setting up the host service on this PC...",
                HostVerb.Update => "Updating this PC's host service. Its pairings and roles stay...",
                _ => $"Running {engine} on this PC..."
            });
            var exit = await HostLocal.EngineAsync(target, engine.Split(' '), run.Output, run.Token, answers: input);
            if (exit != 0) throw new InvalidOperationException($"martlet-host {engine} stopped on this PC (exit {exit}). The output shows why.");
            return action.Verb switch
            {
                HostVerb.Add => $"{role} is running in this PC's host service.",
                HostVerb.Remove => $"{role} was removed from this PC's host service.",
                HostVerb.Setup => "This PC's host service is set up. Pair your main PC next.",
                HostVerb.Update => "This PC's host service is up to date.",
                _ => $"Finished {engine} on this PC."
            };
        });
    }

    /// <summary>Lets another desktop pair with this PC's host service: asks for its device ID, shows the one-use pairing
    /// code (copied to the clipboard, never logged) and waits up to five minutes for that desktop to use it.</summary>
    internal static Task<string?> PairOtherDesktopAsync(Window owner, HostSetupTarget target)
    {
        var dialog = new HostInputDialog("Pair your main PC", "Pair your main PC with this PC's host service",
            "On your main PC open Devices > Add a computer > Pair and copy its device ID. Martlet then shows a one-use code here " +
            "(also copied to the clipboard) to paste on the main PC within five minutes.", "_Show the code");
        dialog.AddText("device", "Main PC's device ID", "", "It looks like desktop-main-pc.");
        dialog.AddText("name", "A name for it", "Main PC");
        if (dialog.Ask(owner) is not { } values) return Task.FromResult<string?>(null);
        return HostRunWindow.RunAsync(owner, "Pair your main PC", async run =>
        {
            await HostLocal.EnsureDockerAsync(run.Status, run.Output, run.Token);
            await HostLocal.EnsureImageAsync(target, run.Status, run.Output, run.Token);
            run.Status("Asking this PC's host service for a one-use pairing code...");
            var shown = false;
            var exit = await HostLocal.PairOtherAsync(target, values["device"], values["name"], code => run.Dispatcher.Invoke(() =>
            {
                shown = true;
                try { Clipboard.SetText(code); }
                catch (System.Runtime.InteropServices.ExternalException) { }
                run.Reveal("Pairing code (copied to the clipboard): " + code);
                run.Status("Paste the code on your main PC (Devices > Add a computer > Pair) within five minutes. Waiting for it...");
            }), new LineSink(line =>
            {
                if (!line.Contains("martlet-pair-v1.", StringComparison.Ordinal)) run.Output.Report(line);
            }), run.Token);
            if (!shown) throw new InvalidOperationException($"This PC's host service did not show a pairing code (exit {exit}). The output shows why.");
            if (exit != 0) throw new InvalidOperationException($"Pairing did not complete (exit {exit}). Show the code again and paste it within five minutes.");
            return $"{values["name"]} is paired with this PC's host service.";
        });
    }
}