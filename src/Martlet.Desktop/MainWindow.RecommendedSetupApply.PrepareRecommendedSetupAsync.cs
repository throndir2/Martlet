using System.IO;
using System.Windows;
using System.Windows.Threading;
using Martlet.HostClient;
using Martlet.Core.Cluster;
using Martlet.Core.Nodes;
using Martlet.Core.Planning;
using Martlet.Core.Settings;
using Martlet.Core.Sync;

namespace Martlet.Desktop;

/// <summary>More of MainWindow.RecommendedSetupApply.cs, in a file of its own so that each file stays under 20 KB.</summary>
public partial class MainWindow
{
    /// <summary>The run this PC started last (also published in the shared settings).</summary>
    private SetupRun? setupRun;
    private bool setupApplying;
    /// <summary>This PC switches a job to follow the shared plan right now (a plan change made elsewhere), in words.</summary>
    private string? clusterFollowingText;
    private string? configuringShown;
    /// <summary>The map's Configuring states as <see cref="ShowConfiguring"/> last worked them out.</summary>
    private IReadOnlyDictionary<string, string> configuringMachines = new Dictionary<string, string>();

    /// <summary>What the owner must see or accept before Reconfigure. Reads each role to install from its computer (martlet-host
    /// describe there); changes nothing.</summary>
    internal Task<SetupRunPreflight> PrepareRecommendedSetupAsync(NetworkRecommendation recommendation, CancellationToken cancel) =>
        SetupExecutor.PrepareAsync(recommendation, SetupRunTargets(), cancel);

    /// <summary>The paths a run takes: this PC's real ones, or the FIXTURE's simulated computers (<see cref="SimulatedRecommendedSetup"/>).</summary>
    private ISetupTargets SetupRunTargets() => SimulatedRecommendedSetup.Active
        ? new SimulatedRecommendedSetup.Targets(ClusterDevice, run =>
        {
            setupRun = run;
            ShowConfiguring();
        })
        : new SetupTargets(this);

    /// <summary>Reconfigure: applies <paramref name="recommendation"/> as a background task, in a run window over
    /// <paramref name="owner"/> (the review, which closes; the run stays with Martlet's main window). The window lists the
    /// changes, then each computer's steps and what its host engine prints, and ends with each change's outcome; Hide keeps it
    /// going in Background tasks, and Cancel task stops the changes not made yet (each says so). A second Reconfigure while it
    /// runs shows the same run.</summary>
    private Task<string?> ReconfigureAsync(Window owner, NetworkRecommendation recommendation, SetupRunPreflight preflight) =>
        HostRunWindow.RunAsync(owner, SetupRunTitle, async run =>
        {
            var changes = recommendation.Changes;
            run.Status($"Starting {changes.Count} change{(changes.Count == 1 ? "" : "s")} on your computers...");
            for (var i = 0; i < changes.Count; i++) run.Output.Report($"{i + 1}. {changes[i].Summary}");
            // Each computer's new state is a line, in order with what its host engine prints.
            SetupRun? shown = null;
            var progress = new RunProgress(next =>
            {
                foreach (var line in next.ChangesSince(shown)) run.Output.Report(line);
                shown = next;
                if (!next.Finished) run.Status(next.Summary(DateTimeOffset.UtcNow));
            });
            var outcome = await ApplyRecommendedSetupAsync(recommendation, preflight, progress, run.Output, run.Token);
            foreach (var step in outcome.Steps)
                run.Output.Report(step.State switch
                {
                    SetupMachineState.Done => "Done",
                    SetupMachineState.Failed => "Failed",
                    _ => "Needs you"
                } + $": {step.Change.Summary} {step.Text}");
            ErrorLog.Info($"Recommended setup: reconfigured ({(outcome.Succeeded ? "every change done" : "not every change done")}).");
            // Canceled: the changes not made are listed above, and the task shows it was canceled.
            run.Token.ThrowIfCancellationRequested();
            return outcome.Summary;
        }, join: true);

    /// <summary>Takes the executor's reports where it makes them (on the UI thread, which the run started on), without a
    /// second trip through the dispatcher.</summary>
    private sealed class RunProgress(Action<SetupRun> report) : IProgress<SetupRun>
    {
        public void Report(SetupRun value) => report(value);
    }

    /// <summary>Applies <paramref name="recommendation"/> after the owner's Reconfigure in the review that showed
    /// <paramref name="preflight"/> (the click accepts the terms it showed). Makes the changes in order, continues past a failed
    /// one and reports each computer (and what each role change's host engine prints, to <paramref name="output"/>); never
    /// throws for a failed step.</summary>
    internal async Task<SetupRunOutcome> ApplyRecommendedSetupAsync(NetworkRecommendation recommendation, SetupRunPreflight preflight,
        IProgress<SetupRun>? progress, IProgress<string>? output, CancellationToken cancel)
    {
        if (setupApplying) throw new InvalidOperationException("Martlet is already reconfiguring your computers.");
        setupApplying = true;
        try
        {
            ErrorLog.Info($"Recommended setup: reconfiguring your computers ({recommendation.Changes.Count} changes, " +
                $"{recommendation.Changes.Select(c => SetupExecutor.RunMachine(c, ClusterDevice)).Distinct(StringComparer.Ordinal).Count()} computers).");
            var outcome = await SetupExecutor.ApplyAsync(recommendation, preflight, SetupRunTargets(), progress, cancel, output: output);
            foreach (var step in outcome.Steps)
                if (step.State == SetupMachineState.Done) ErrorLog.Info($"Recommended setup: {step.Change.Summary} Done: {step.Text}");
                else ErrorLog.Warn($"Recommended setup: {step.Change.Summary} {step.State}: {step.Text}");
            if (!closing) ActionText.Text = outcome.Summary;
            return outcome;
        }
        finally
        {
            setupApplying = false;
            if (!closing) ShowConfiguring();
        }
    }

    // ---------- the Configuring state ----------

    /// <summary>What is being configured now: the text for Home (null: nothing) and, by computer (a host ID, a device ID or
    /// "this-pc"), the step it works on, for the Devices map. From every computer's published run, the role changes this PC
    /// sends a host (<see cref="HostActivity"/>), the commands other computers sent this PC's own host service, and this PC
    /// following a plan change.</summary>
    internal (string? Text, IReadOnlyDictionary<string, string> Machines, bool Active, bool Trouble) ConfiguringNow(DateTimeOffset now)
    {
        var machines = new Dictionary<string, string>(StringComparer.Ordinal);
        // This PC's own host service ID is left as it is: the map draws it on this PC (and Inputs() must not be called from here).
        string Local(string id) => id == ClusterDevice ? DeviceCapacityInputs.ThisPcId : id;
        var runs = settingsNode is null ? new List<SetupRun>() : SetupRun.All(settingsNode.Document).ToList();
        if (setupRun is { } mine && runs.All(r => r.RunId != mine.RunId)) runs.Add(mine);
        string? text = null;
        var active = false;
        var trouble = false;
        foreach (var run in runs.Where(r => r.Shown(now)).OrderByDescending(r => r.UpdatedAt))
        {
            if (text is null)
            {
                text = run.Summary(now) + (run.StartedBy == ClusterDevice ? "" : $" Started on {run.StartedBy}.");
                trouble = run.Finished && run.Machines.Any(m => m.State is SetupMachineState.Failed or SetupMachineState.NeedsAttention);
            }
            if (!run.Active(now)) continue;
            active = true;
            foreach (var machine in run.Configuring) machines.TryAdd(Local(machine.MachineId), machine.Step ?? "Working");
        }
        foreach (var (host, step) in HostActivity.Now) machines.TryAdd(Local(host), step);
        foreach (var command in NodeCommandsRunning.Where(c => c.Kind is NodeCommandKinds.AddRole or NodeCommandKinds.RemoveRole or NodeCommandKinds.Update))
            machines.TryAdd(DeviceCapacityInputs.ThisPcId, NodeCommandAgent.Describe(command));
        if (clusterFollowingText is { } following) machines.TryAdd(DeviceCapacityInputs.ThisPcId, following);
        if (machines.Count > 0) active = true;
        if (text is null && machines.Count > 0)
            text = "Configuring " + string.Join("; ", machines.Take(3).Select(m =>
                $"{(m.Key == DeviceCapacityInputs.ThisPcId ? "this PC" : m.Key)}: {m.Value}")) + (machines.Count > 3 ? $" and {machines.Count - 3} more." : ".");
        return (text, machines, active, trouble);
    }

    /// <summary>Shows <see cref="ConfiguringNow"/> on Home (both homes), the tray and, when it changed, the Devices map.</summary>
    private void ShowConfiguring()
    {
        if (closing) return;
        var (text, machines, active, trouble) = ConfiguringNow(DateTimeOffset.UtcNow);
        var brush = active ? "AccentBrush" : trouble ? "WarningBrush" : "SuccessBrush";
        foreach (var (indicator, status, dot) in new[] { (ConfiguringIndicator, ConfiguringStatusText, ConfiguringDot),
                     (HostConfiguringIndicator, HostConfiguringStatusText, HostConfiguringDot) })
        {
            indicator.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
            if (status.Text != (text ?? "")) status.Text = text ?? "";
            dot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, brush);
        }
        var signature = string.Join("|", machines.OrderBy(m => m.Key, StringComparer.Ordinal).Select(m => m.Key + "=" + m.Value));
        if (signature != configuringShown)
        {
            configuringShown = signature;
            configuringMachines = machines;
            RenderDevicePages();
        }
        UpdateTray();
    }

    /// <summary>The tray tooltip's line while computers are configured, or null.</summary>
    private string? TrayConfiguringText() => configuringMachines.Count switch
    {
        0 => null,
        1 => "Configuring a computer",
        var count => $"Configuring {count} computers"
    };

    private void Configuring_Click(object sender, RoutedEventArgs e) => Navigate(NavDevices);

    /// <summary>The map's Configuring states by computer, as last shown (see <see cref="ConfiguringNow"/>).</summary>
    private IReadOnlyDictionary<string, string> ConfiguringMachines() => configuringMachines;

    private void InitializeConfiguring()
    {
        HostActivity.Changed += () => Dispatcher.InvokeAsync(ShowConfiguring, DispatcherPriority.Background);
        // A run this PC started can't still be running when Martlet starts (it closed or stopped first): finish its entry, so no
        // computer keeps showing it as Configuring.
        if (settingsNode?.Document.Find(SetupRun.Key(ClusterDevice)) is { } own && own.UpdatedBy == ClusterDevice &&
            SetupRun.Read(own.Value) is { Finished: false } left)
        {
            var now = DateTimeOffset.UtcNow;
            var ended = left.Interrupted(now, $"Martlet closed on {ClusterDevice} before this change was made: check the recommended setup again.");
            try { settingsNode.Put(own.Key, ended.Write(), now); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or Martlet.Core.Contracts.ContractException)
            {
                ErrorLog.Warn("Couldn't finish the recommended setup run Martlet left when it closed.", error);
            }
            setupRun = ended;
            ErrorLog.Info($"Recommended setup: the run {left.RunId} ended when Martlet closed; the changes not made need you.");
        }
    }

    // ---------- the paths a run takes ----------
}
