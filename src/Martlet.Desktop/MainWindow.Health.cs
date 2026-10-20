using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using Martlet.Avatar.Hosting;
using Martlet.Core.Cluster;
using Martlet.Core.Installation;
using Martlet.Core.Platforms;
using Martlet.Core.Settings;
using Martlet.Providers;

namespace Martlet.Desktop;

/// <summary>How much a Home health item matters: a problem stops Martlet replying; a warning means something chosen doesn't
/// work (or works in a reduced way); a notice is good to know (optional setup, an update, the last run ending badly).</summary>
internal enum HealthLevel { Problem, Warning, Notice }

/// <summary>A fix offered next to a health item. A <paramref name="Passive"/> fix only opens a page or hides the item.
/// <paramref name="Enabled"/> false shows it greyed out while a run already does it (its label then says so).</summary>
internal sealed record HealthFix(string Id, string Label, Action Run, bool Passive = false, bool Enabled = true);

/// <summary>Something on Home that is wrong, missing or worth knowing. <paramref name="Headline"/> replaces the hero's title
/// when this is the top problem.</summary>
internal sealed record HealthIssue(string Id, HealthLevel Level, string Title, string Detail, IReadOnlyList<HealthFix> Fixes,
    string? Headline = null);

/// <summary>One part Martlet checks, for the Health tiles: its state in a few words and the page where it changes.</summary>
internal sealed record HealthTile(string Id, string Name, string Glyph, NodeHealth Health, string Status, Action Open);

/// <summary>Home: what is wrong, missing or worth knowing, each with its fixes, then a tile per part Martlet checks (the checks are in
/// MainWindow.HealthChecks.cs and the drawing in MainWindow.HealthView.cs). Everything
/// comes from what this PC already knows (saved settings, the last host checks, Windows' device and app lists, the local log
/// and this PC's own loopback services); nothing here contacts another computer or a provider.</summary>
public partial class MainWindow
{
    private enum LocalOllamaState { NotUsed, NotInstalled, NotRunning, ModelMissing, Ready }

    private SettingsLoadState? homeSettingsState;
    private string? homeSettingsProblem;
    private LocalOllamaState ollamaState;
    /// <summary>Why the model app on this PC that Thinking uses can't answer (see <see cref="LocalServerHealth"/>), or null.</summary>
    private string? localServerProblem;
    private bool webView2Missing, microphoneBlocked, localCheckAgain;
    private Task? localCheck;
    private string? localCheckKey;
    private readonly HashSet<string> dismissedHealth = new(StringComparer.Ordinal);
    private int errorsAcknowledged;
    private string? healthSignature;
    private Action? stageFix;
    private DispatcherTimer? healthTimer;

    private void InitializeHealth()
    {
        healthTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromSeconds(1) };
        healthTimer.Tick += (_, _) =>
        {
            healthTimer.Stop();
            if (!closing) RenderHealth();
        };
        ErrorLog.ErrorRecorded += QueueHealth;
        ErrorLog.PreviousRunDescribed += QueueHealth;
        LocalOllamaRecovery.Changed += OllamaRecoveryChanged;
        if (conversation is not null) conversation.FailuresChanged += QueueHealth;
    }

    private void ReleaseHealth()
    {
        ErrorLog.ErrorRecorded -= QueueHealth;
        ErrorLog.PreviousRunDescribed -= QueueHealth;
        LocalOllamaRecovery.Changed -= OllamaRecoveryChanged;
        if (conversation is not null) conversation.FailuresChanged -= QueueHealth;
        healthTimer?.Stop();
    }

    /// <summary>Why Ollama doesn't answer, or Martlet's repair of it, changed (any thread): shows it, and checks Ollama again after
    /// a repair made elsewhere (a run window, the talk window) so Home doesn't keep saying it isn't running.</summary>
    private void OllamaRecoveryChanged() => Dispatcher.BeginInvoke(() =>
    {
        if (closing) return;
        QueueHealth();
        if (!LocalOllamaRecovery.Repairing && LocalOllamaRecovery.LastRepair is { Repaired: true } && ollamaState == LocalOllamaState.NotRunning)
            CheckLocalServicesAsync().Forget();
    });

    /// <summary>Re-renders Home's health at most once a second after something changed on another thread.</summary>
    private void QueueHealth() => Dispatcher.BeginInvoke(() =>
    {
        if (!closing && healthTimer is { IsEnabled: false }) healthTimer.Start();
    });

    private async void HealthRecheck_Click(object sender, RoutedEventArgs e)
    {
        if (closing) return;
        HealthRecheckButton.IsEnabled = false;
        try
        {
            ActionText.Text = "Checking again on this PC...";
            await ReadMachineAsync();
            await RefreshAsync();
            await CheckLocalServicesAsync();
            if (HostsHere) await (hostProbe is { IsCompleted: false } reading ? reading : CheckThisPcHostAsync());
            if (closing) return;
            var (issues, _) = BuildHealth();
            var open = issues.Count(i => i.Level != HealthLevel.Notice);
            ActionText.Text = open == 0 ? "Checked again: nothing needs your attention."
                : $"Checked again: {open} thing{(open == 1 ? "" : "s")} need{(open == 1 ? "s" : "")} your attention.";
        }
        finally { if (!closing) HealthRecheckButton.IsEnabled = true; }
    }

    private void HealthChecks_SizeChanged(object sender, SizeChangedEventArgs e) =>
        HealthChecks.Columns = Math.Clamp((int)(HealthChecks.ActualWidth / 240), 1, 4);

    /// <summary>Checks this PC's own pieces that Home reports on: whether Ollama answers on its loopback port with the Thinking
    /// model (when Thinking uses it), and the WebView2 runtime and Windows microphone permission (registry only). A call while
    /// a check runs returns that check, which then runs once more so it sees the latest settings.</summary>
    private Task CheckLocalServicesAsync()
    {
        if (localCheck is { IsCompleted: false } running)
        {
            localCheckAgain = true;
            return running;
        }
        return localCheck = RunLocalChecksAsync();
    }

    /// <summary>Starts a local check when the Thinking route changed since the last one (a new choice in Companion, a sync).</summary>
    private void FollowLocalServices()
    {
        var thinking = homeSettings.ThinkingRoute();
        var key = thinking is null ? "" : $"{thinking.RouteType}|{thinking.Origin}|{thinking.ModelId}|{thinking.Enabled}";
        if (key == localCheckKey) return;
        localCheckKey = key;
        ollamaState = LocalOllamaState.NotUsed;
        localServerProblem = null;
        CheckLocalServicesAsync().Forget();
    }

    /// <summary>Why the model app on this PC that Thinking uses can't answer, from what it said when asked for its models, or
    /// null when it can. Asked without the key: an app that wants one is fine when the route has one saved.</summary>
    internal static string? LocalServerHealth(SetupRoute route, LocalServerAnswer answer, string name) => answer.Kind switch
    {
        LocalServerAnswerKind.NoAnswer => $"Thinking uses {route.ModelId} in {name} on this PC, but nothing answers at {route.Origin}: " +
            $"start {(LocalModelServers.AppAt(route.Origin) is { } app ? app.Name + "'s" : "the app's")} server, or choose another way to think.",
        LocalServerAnswerKind.NotAModelServer => $"Thinking uses {route.ModelId} in {name} on this PC, but {route.Origin} doesn't answer like a model app; " +
            "another program may be using its port.",
        LocalServerAnswerKind.NeedsKey when route.CredentialId is null =>
            $"{char.ToUpperInvariant(name[0])}{name[1..]} asks for an API key: enter the key you set in the app in Companion › Thinking › This PC.",
        LocalServerAnswerKind.Models when answer.Models.Count > 0 && !answer.Models.Contains(route.ModelId, StringComparer.Ordinal) =>
            $"Thinking uses {route.ModelId}, but {name} on this PC doesn't list it now (it lists {string.Join(", ", answer.Models.Take(4))}): " +
            "load it in the app, or choose another model.",
        _ => null
    };

    private async Task RunLocalChecksAsync()
    {
        if (closing || Role != DeviceRole.Companion) return;
        do
        {
            localCheckAgain = false;
            var state = LocalOllamaState.NotUsed;
            webView2Missing = Prerequisites.IsMissing(Prerequisites.WebView2);
            microphoneBlocked = Prerequisites.IsMissing(Prerequisites.Microphone);
            var thinking = homeSettings.ThinkingRoute();
            if (IsLocalOllama(thinking) && thinking!.Enabled != false)
            {
                IReadOnlyList<string>? models;
                try { models = await LocalOllama.ModelsAsync(TimeSpan.FromSeconds(2), lifetime.Token); }
                catch (OperationCanceledException) { return; }
                state = models is null
                    ? Prerequisites.IsMissing(Prerequisites.Ollama) && !SimulatedOllamaCrashLoop.Active ? LocalOllamaState.NotInstalled : LocalOllamaState.NotRunning
                    : LocalOllama.Serves(models, thinking.ModelId) ? LocalOllamaState.Ready : LocalOllamaState.ModelMissing;
                // Why it doesn't answer, from Ollama's own logs (its app, processes and models folder too); reads only.
                if (state == LocalOllamaState.NotRunning)
                {
                    try { await LocalOllamaRecovery.DiagnoseAsync(lifetime.Token); }
                    catch (OperationCanceledException) { return; }
                }
            }
            string? serverProblem = null;
            if (IsLocalServer(thinking) && thinking!.Enabled != false)
            {
                LocalServerAnswer answer;
                try { answer = await LocalModelServers.AskAsync(thinking.Origin, null, timeout: TimeSpan.FromSeconds(3), cancellationToken: lifetime.Token); }
                catch (OperationCanceledException) { return; }
                serverProblem = LocalServerHealth(thinking, answer, LocalServerName(thinking.Origin));
            }
            if (closing) return;
            ollamaState = state;
            localServerProblem = serverProblem;
        } while (localCheckAgain);
        RenderHealth();
        // Ollama keeps stopping because of its linked models folder: point it at the real folder now, once by itself.
        if (ollamaState == LocalOllamaState.NotRunning && LocalOllamaRecovery.RepairsAutomatically(LocalOllamaRecovery.Last))
            RepairOllamaAsync(automatic: true).Forget();
    }

    /// <summary>Points Ollama at its real models folder when it keeps stopping because of a link (see
    /// <see cref="LocalOllamaRecovery"/>). <paramref name="automatic"/>: Martlet found it by itself (once for each cause in a
    /// run); otherwise you asked, so it reads Ollama's state again first.</summary>
    private async Task RepairOllamaAsync(bool automatic)
    {
        var trouble = LocalOllamaRecovery.Last;
        try
        {
            if (!automatic)
            {
                ActionText.Text = "Checking Ollama on this PC again...";
                trouble = await LocalOllamaRecovery.DiagnoseAsync(lifetime.Token);
            }
            if (closing) return;
            if (trouble is not { CanRepair: true })
            {
                if (!automatic)
                {
                    ActionText.Text = trouble is null || trouble.Kind == OllamaTroubleKind.Answering
                        ? "Ollama answers on this PC now." : $"{trouble.Message} {trouble.Guidance}".Trim();
                    await CheckLocalServicesAsync();
                }
                return;
            }
            ActionText.Text = "Ollama keeps stopping because of its linked models folder, so Martlet is pointing it at the real folder...";
            var running = LocalOllamaRecovery.RepairAsync(trouble, automatic, lifetime.Token);
            RenderHealth();
            var result = await running;
            if (closing || result is null) return;
            ActionText.Text = result.Summary;
            await CheckLocalServicesAsync();
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>What to say when Ollama on this PC doesn't answer: why, in Ollama's own words when its logs have them, after
    /// Martlet repaired the known cause by itself (once in a run). <paramref name="then"/> ends the general advice ("check
    /// again"). Repaired is true when Ollama answers after the repair.</summary>
    private async Task<(string Text, bool Repaired)> OllamaNotAnsweringAsync(string then)
    {
        var general = $"Ollama didn't answer on this PC: start Ollama from the Start menu, then {then}.";
        OllamaDiagnosis trouble;
        try
        {
            // A repair running now (Home's, by itself) stops and starts Ollama: its result says more than Ollama's state meanwhile.
            if (LocalOllamaRecovery.Repairing)
            {
                await LocalOllamaRecovery.WaitForRepairAsync(lifetime.Token);
                if (LocalOllamaRecovery.LastRepair is { Repaired: true } repaired) return (repaired.Summary, true);
            }
            trouble = await LocalOllamaRecovery.DiagnoseAsync(lifetime.Token);
        }
        catch (OperationCanceledException) { return (general, false); }
        if (trouble.Kind == OllamaTroubleKind.Answering) return ("", true);
        if (!trouble.Stops) return (general, false);
        if (LocalOllamaRecovery.RepairsAutomatically(trouble))
        {
            ActionText.Text = "Ollama keeps stopping because of its linked models folder, so Martlet is pointing it at the real folder...";
            OllamaRepairResult? result;
            try { result = await LocalOllamaRecovery.RepairAsync(trouble, automatic: true, lifetime.Token); }
            catch (OperationCanceledException) { return (general, false); }
            if (result is { Repaired: true }) return (result.Summary, true);
            if (result is not null) return ($"{trouble.Message} Martlet tried to fix it: {result.Summary}", false);
        }
        return ($"{trouble.Message} {trouble.Guidance}".Trim(), false);
    }

    /// <summary>Home's item while Thinking uses Ollama on this PC and it doesn't answer: its title and detail, with Ollama's own
    /// words and what Martlet does about them when its logs say why (<paramref name="trouble"/>), the repair running
    /// (<paramref name="repairing"/>) and the last one Martlet tried (<paramref name="repair"/>).</summary>
    internal static (string Title, string Detail) OllamaHealthText(string model, OllamaDiagnosis? trouble,
        bool repairing, OllamaRepairResult? repair)
    {
        if (trouble is not { Stops: true }) return ("Ollama isn't running on this PC", $"Thinking uses {model} on this PC, but Ollama isn't running.");
        var looping = trouble.Kind == OllamaTroubleKind.CrashLoop;
        var title = looping ? "Ollama keeps stopping on this PC" : "Ollama stops when it starts on this PC";
        var starts = trouble.Facts?.Log.FailedStarts ?? 0;
        var opening = looping ? $"Ollama keeps stopping{(starts > 1 ? $" ({starts} failed starts in its log)" : "")}"
            : "Ollama stopped the last time it started";
        var tried = repair is { Repaired: false } && repair.After?.RepairKey == trouble.RepairKey;
        var next = repairing ? $"Martlet is pointing Ollama at {trouble.RealPath} now..."
            : tried ? $"Martlet tried to fix it: {repair!.Summary}"
            : trouble.CanRepair ? $"Choose Fix Ollama's models folder to point Ollama at {trouble.RealPath}."
            : trouble.Guidance ?? "";
        return (title, $"Thinking uses {model} on this PC, but {opening}. {trouble.Explanation} {next}".TrimEnd());
    }

    private void StartOllama()
    {
        if (!LocalOllama.Start())
        {
            ActionText.Text = "Ollama couldn't start: start it from the Start menu, or install it again.";
            return;
        }
        ActionText.Text = "Starting Ollama on this PC...";
        WaitForOllamaAsync().Forget();
    }

    private async Task WaitForOllamaAsync()
    {
        for (var attempt = 0; attempt < 15 && !closing; attempt++)
        {
            await Task.Delay(TimeSpan.FromSeconds(2), lifetime.Token);
            await CheckLocalServicesAsync();
            if (ollamaState != LocalOllamaState.NotRunning) break;
            // It started and keeps stopping: waiting longer won't help (Home says why, and repairs the known cause).
            if (LocalOllamaRecovery.Last is { Kind: OllamaTroubleKind.CrashLoop }) break;
        }
        if (!closing && ollamaState == LocalOllamaState.NotRunning && !LocalOllamaRecovery.Repairing)
            ActionText.Text = LocalOllamaRecovery.Last is { Stops: true } trouble
                ? $"{trouble.Message} {(trouble.CanRepair ? "" : trouble.Guidance)}".TrimEnd()
                : "Ollama didn't start: start it from the Start menu, then press Check again.";
    }

    private async Task PullThinkingModelAsync(string model)
    {
        await PullOllamaModelAsync(model);
        if (!closing) await CheckLocalServicesAsync();
    }

    private async Task InstallThinkingOllamaAsync(string model)
    {
        await InstallPrerequisitesAsync([Prerequisites.Ollama], model);
        if (!closing) await CheckLocalServicesAsync();
    }

    private async Task InstallPrerequisiteAsync(Prerequisite item)
    {
        await InstallPrerequisitesAsync([item]);
        if (!closing) await CheckLocalServicesAsync();
    }

    private void OpenLogsFolder()
    {
        if (!ErrorLog.OpenFolder()) ActionText.Text = "The logs folder isn't available: check access to Martlet's data folder.";
    }

    private void OpenOllamaLogs()
    {
        if (!LocalOllamaRecovery.OpenLogs()) ActionText.Text = "Ollama's logs folder isn't available on this PC.";
    }

    private void OpenMicrophonePrivacy()
    {
        try { Process.Start(new ProcessStartInfo("ms-settings:privacy-microphone") { UseShellExecute = true })?.Dispose(); }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            ActionText.Text = "Couldn't open Windows Settings: open Microphone privacy settings yourself and allow desktop apps.";
        }
    }

    private void ShowDevice(string? nodeId)
    {
        Navigate(NavDevices);
        if (nodeId is not null) Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => SelectNode(nodeId, animate: true));
    }
}
