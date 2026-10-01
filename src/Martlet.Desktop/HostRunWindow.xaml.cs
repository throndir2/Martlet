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

    internal void Status(string text) => StatusText.Text = text;

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

/// <summary>Runs martlet-host actions (status, add or remove a role) on an SSH host from anywhere in the desktop.</summary>
internal static class HostSshActions
{
    internal static Task<string?> RunAsync(Window owner, string dataDirectory, HostSetupTarget target, string? pinnedHostKey,
        HostAction action)
    {
        var engine = HostSetupCommands.Engine(action);
        var words = engine.Split(' ');
        var role = words.Length == 2 && words[0] is "add" or "remove" ? words[1] : null;
        var add = words[0] == "add";
        var ssh = HostShellTarget.Parse(target.SshTarget);
        var title = role is null ? $"{ssh}: martlet-host {engine}" : $"{(add ? "Add" : "Remove")} {role} on {ssh}";
        return HostRunWindow.RunAsync(owner, title, async run =>
        {
            var remote = new HostRemote(new HostShell(dataDirectory, run.Prompts));
            run.Status($"Connecting to {ssh}...");
            var (probe, hostKey) = await remote.ProbeAsync(ssh, pinnedHostKey, run.Token);
            var sudo = HostRemote.NeedsSudo(target.Method, probe);
            IReadOnlyDictionary<string, string>? input = null;
            if (role is not null && add)
            {
                run.Status($"Reading what {role} needs...");
                var inputs = await remote.DescribeAsync(target, role, sudo, hostKey, run.Output, run.Token);
                input = HostInputDialog.ForRole(run, ssh.ToString(), role, inputs) ?? throw new OperationCanceledException();
            }
            else if (role is not null && !ConfirmationDialog.Confirm(run,
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
}
