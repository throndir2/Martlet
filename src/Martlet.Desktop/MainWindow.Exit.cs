using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Threading;
using Martlet.Avatar.Audio2Face.Remote;

namespace Martlet.Desktop;

/// <summary>Exiting Martlet. An exit that would cut work short (an update download, a run window's install, backup and
/// restore, a troubleshooting report, a download, a command from another computer...) names it and asks first: Exit anyway
/// interrupts it, Keep Martlet open doesn't. While Martlet closes, its window says what it is finishing; when that takes
/// longer than usual (the window shows even from the notification area), Exit now asks once and closes without waiting.
/// A step that fails is logged and skipped, so closing never stays stuck on it.</summary>
public partial class MainWindow
{
    /// <summary>Seconds (1-600) closing waits on a simulated slow last step, for checking the closing panel through MCP.</summary>
    internal const string SimulatedSlowExitVariable = "MARTLET_SIMULATE_SLOW_EXIT";

    private static readonly TimeSpan SimulatedSlowExit =
        int.TryParse(Environment.GetEnvironmentVariable(SimulatedSlowExitVariable), NumberStyles.Integer, CultureInfo.InvariantCulture,
            out var seconds) && seconds is >= 1 and <= 600 ? TimeSpan.FromSeconds(seconds) : TimeSpan.Zero;

    /// <summary>Closing shows Exit now once it has taken this long.</summary>
    private static readonly TimeSpan SlowClosing = TimeSpan.FromSeconds(3);
    /// <summary>Quick work (a save, a check) usually finishes within this; exit asks only about what still runs then.</summary>
    private static readonly TimeSpan ExitSettle = TimeSpan.FromSeconds(1.5);
    /// <summary>The longest closing waits for interrupted work to let go of its files.</summary>
    private static readonly TimeSpan InterruptWait = TimeSpan.FromSeconds(5);

    private readonly DispatcherTimer closingTimer = new() { Interval = SlowClosing };
    /// <summary>Windows is signing out or shutting down: nothing asks, everything stops.</summary>
    private bool sessionEnding;
    private bool askingToExit, confirmingExitNow, closingFinished;
    /// <summary>What closing is doing now ("stopping the character") and what exiting without waiting leaves undone.</summary>
    private string closingStep = "getting ready to exit", closingEffect = "";

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (mayClose)
            return;
        e.Cancel = true;
        if (closing)
        {
            // Closing again while Martlet closes offers to exit without waiting (after WPF's Closing event, which can't close).
            _ = Dispatcher.InvokeAsync(ConfirmExitNow);
            return;
        }
        if (askingToExit)
            return;
        // The close button keeps Martlet running in the notification area unless you chose to exit.
        if (!exiting && tray is { Added: true } && background.CloseToTray)
        {
            HideToTray();
            return;
        }
        if (!await ConfirmInterruptionsAsync())
        {
            RefuseExit();
            ActionText.Text = "Martlet stays open. Exit again when it's done, or to interrupt it.";
            return;
        }
        await CloseMartletAsync();
    }

    /// <summary>What exiting now would cut short, each as a capitalized phrase; empty when nothing would be.</summary>
    private List<string> ExitInterruptions()
    {
        var busy = new List<string>();
        if (recovery is { NeedsCleanup: true })
            busy.Add("Backup and restore still has a temporary file to clean up (Retry cleanup in Backup and restore)");
        else if (recovery is { HasResources: true })
            busy.Add("Backing up or restoring your settings");
        // A reply shares the setup slot; exiting simply ends the conversation, so it isn't asked about.
        else if (setupOperations.IsRunning && conversation?.Replying != true)
            busy.Add("A setup task (saving a change or checking a device)");
        if (support.IsBusy)
            busy.Add("Troubleshooting is preparing or exporting a report");
        else if (support.Preview is not null)
            busy.Add("A troubleshooting report is waiting for you to export it");
        else if (support.NeedsCleanup)
            busy.Add("Troubleshooting is waiting to clean up its files");
        if (updateDownloadCancellation is not null)
            busy.Add(availableUpdate is { } update ? $"Downloading Martlet {update.Version.ToString(3)}" : "Downloading a Martlet update");
        if (installingParakeet)
            busy.Add("Downloading Parakeet speech recognition");
        if (installingVoices)
            busy.Add("Downloading voice recognition");
        if (hostUpdatesRunning || hostUpdates.Running)
            busy.Add("Updating Martlet on your hosts");
        if (nodeCommandRunning is { } command)
            busy.Add($"A command from another computer: {NodeCommandAgent.Describe(command)}");
        foreach (var window in Application.Current.Windows.OfType<Window>())
        {
            if (window is HostRunWindow { IsRunning: true } run)
                busy.Add($"{run.Heading} (in its run window)");
            else if (window is PrepareHostWindow { IsBusy: true } prepare)
                busy.Add(prepare.Heading);
        }
        return busy;
    }

    /// <summary>Asks before an exit that would cut work short; true to exit. Windows signing out never waits for an answer.</summary>
    private async Task<bool> ConfirmInterruptionsAsync()
    {
        if (sessionEnding) return true;
        var busy = ExitInterruptions();
        if (busy.Count == 0) return true;
        askingToExit = true;
        try
        {
            var settled = DateTime.UtcNow + ExitSettle;
            while (busy.Count > 0 && DateTime.UtcNow < settled)
            {
                await Task.Delay(100);
                if (sessionEnding) return true;
                busy = ExitInterruptions();
            }
            if (busy.Count == 0) return true;
            var wasInTray = inTray;
            ShowFromTray();
            var them = busy.Count == 1 ? "it" : "them";
            ErrorLog.Info($"Exit asks first: Martlet is still busy with {string.Join("; ", busy)}.");
            var confirmed = ConfirmationDialog.Confirm(this,
                "Martlet is still busy with:\n" + string.Join("\n", busy.Select(item => "\u2022 " + item)) +
                $"\n\nYou can still exit now, but that interrupts {them}: whatever isn't finished is canceled, so you may need to " +
                $"do {them} again.\n\nExit anyway?",
                "Exit Martlet", yes: "E_xit anyway", no: "_Keep Martlet open", questionId: "ExitBusyQuestion");
            ErrorLog.Info(confirmed ? "Exiting anyway; that interrupts what Martlet was doing." : "Martlet stays open; nothing was interrupted.");
            // An update installed on the way out starts Martlet again where it was.
            if (confirmed) inTray = wasInTray;
            return confirmed;
        }
        finally { askingToExit = false; }
    }

    /// <summary>Closes Martlet: stops what the exit interrupts, then each part in turn, saying which; Exit now ends it early.</summary>
    private async Task CloseMartletAsync()
    {
        closing = true;
        exiting = true;
        ErrorLog.Info("Martlet is exiting.");
        BeginClosingPresentation();
        // What the owner agreed to interrupt (or Windows ending the session) stops now.
        updateCheckCancellation?.Cancel();
        updateDownloadCancellation?.Cancel();
        if (recovery?.HasResources == true) recovery.StopObserving();
        if (support.HasResources) support.CancelAndClose();
        foreach (var window in Application.Current.Windows.OfType<Window>())
        {
            if (window is HostRunWindow run) run.Interrupt();
            else if (window is PrepareHostWindow prepare) prepare.Interrupt();
        }
        await StepAsync("stopping Martlet's background work", "", () =>
        {
            // The talk window stops listening, vision and any reply before the conversation it uses is disposed below.
            openConversation?.End();
            ReleaseShell();
            ageTimer.Stop();
            characterTimer.Stop();
            updateTimer.Stop();
            clusterTimer.Stop();
            settingsTimer.Stop();
            networkTimer.Stop();
            apiKeysTimer.Stop();
            voiceSyncTimer.Stop();
            homeShareTimer.Stop();
            StopNearby();
            StopLogs();
            audioSessionEvents.LockedChanged -= audioSetup.SetSessionLocked;
            audioSessionEvents.LockedChanged -= AvatarSessionLocked;
            if (conversation is not null) audioSessionEvents.LockedChanged -= conversation.SetSessionLocked;
            audioSessionEvents.Dispose();
            lifetime.Cancel();
            setupOperations.RequestCancellation();
            return Task.CompletedTask;
        });
        if (updateDrain is { Task.IsCompleted: false } update)
            await StepAsync("stopping the update download", "The update downloads again later.",
                () => update.Task.WaitAsync(InterruptWait));
        if (support.HasResources)
            await StepAsync("stopping troubleshooting", "The records it already saved stay on this PC.",
                () => UntilAsync(() => !support.HasResources, InterruptWait));
        if (model is not null)
            await StepAsync("finishing a status check", "The check is skipped.", () => model.CloseAsync());
        if (conversation is not null)
            await StepAsync("ending the conversation", "Windows releases the microphone and speakers when Martlet exits.",
                () => Task.Run(async () => await conversation.DisposeAsync()));
        // Ends every MCP server Martlet started (they also end with Martlet's process through its job object).
        await StepAsync("stopping your tool servers", "Windows ends any still running along with Martlet.",
            () => Task.Run(async () => await mcpTools.DisposeAsync()));
        await StepAsync("closing speech captions", "", () => { captions.Dispose(); return Task.CompletedTask; });
        // The renderer runs in a kill-on-close job object, so Windows ends it with Martlet even when its cleanup cannot
        // finish: a stuck character never keeps Martlet open or holds back an update.
        const string character = "Windows ends the character's renderer along with Martlet.";
        await StepAsync("closing the character", character, async () =>
        {
            if (!await StopAvatarSafelyAsync())
                ErrorLog.Warn("Exiting with the character's cleanup unfinished; Windows ends its renderer with Martlet.");
        });
        await StepAsync("closing the character", character, async () => await avatar.DisposeAsync());
        if (SimulatedSlowExit > TimeSpan.Zero)
            await StepAsync($"waiting on a simulated slow step ({SimulatedSlowExitVariable})", "Nothing is lost; this step only waits.",
                () => Task.Delay(SimulatedSlowExit));
        FinishClosing();
    }

    /// <summary>One closing step: shown while it runs; a failure is logged and closing goes on. Skipped after Exit now.</summary>
    private async Task StepAsync(string doing, string effect, Func<Task> work)
    {
        if (closingFinished) return;
        closingStep = doing;
        closingEffect = effect;
        RenderClosing();
        try { await work(); }
        catch (Exception error) when (!ErrorLog.IsFatal(error))
        {
            ErrorLog.Warn($"While exiting, {doing} didn't finish; Martlet exits anyway.", error);
        }
    }

    private static async Task UntilAsync(Func<bool> done, TimeSpan limit)
    {
        var deadline = DateTime.UtcNow + limit;
        while (!done())
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException($"Still running after {limit.TotalSeconds:0} seconds.");
            await Task.Delay(100);
        }
    }

    /// <summary>The last of closing, also when Exit now cuts it short: memory, a pending update install, the icon, the window.</summary>
    private void FinishClosing()
    {
        if (closingFinished) return;
        closingFinished = true;
        closingTimer.Stop();
        trayTimer.Stop();
        try { memory?.Dispose(); }
        catch (Exception error) when (!ErrorLog.IsFatal(error)) { ErrorLog.Warn("Couldn't close memory while exiting.", error); }
        LaunchPendingInstall();
        tray?.Dispose();
        // WPF OnMainWindowClose exits the process, including any non-cooperative in-process callback.
        // Even absent or synchronous cleanup must leave WPF's original Closing event before closing again.
        _ = Dispatcher.InvokeAsync(() =>
        {
            mayClose = true;
            Close();
        });
    }

    // ---------- the closing panel ----------

    private void BeginClosingPresentation()
    {
        NavRail.IsEnabled = PageArea.IsEnabled = Tour.IsEnabled = false;
        ClosingSlowPanel.Visibility = Visibility.Collapsed;
        ClosingOverlay.Visibility = Visibility.Visible;
        RenderClosing();
        closingTimer.Tick += (_, _) => ClosingIsSlow();
        closingTimer.Start();
    }

    private void RenderClosing()
    {
        ClosingStatusText.Text = char.ToUpperInvariant(closingStep[0]) + closingStep[1..] + "...";
        ClosingSlowText.Text = "This is taking longer than usual. You can exit now without waiting, but that interrupts it." +
            (closingEffect.Length == 0 ? "" : " " + closingEffect);
        UpdateTray();
    }

    /// <summary>Closing has taken longer than usual: Exit now shows, and so does the window when it was hidden.</summary>
    private void ClosingIsSlow()
    {
        closingTimer.Stop();
        if (closingFinished) return;
        ErrorLog.Info($"Exiting is taking longer than usual: Martlet is {closingStep}.");
        ClosingSlowPanel.Visibility = Visibility.Visible;
        RevealClosing();
    }

    /// <summary>Shows the window with the closing panel (from the icon, a second start, or when closing is slow). An automatic
    /// update's quiet install doesn't take the focus.</summary>
    private void RevealClosing()
    {
        if (closingFinished) return;
        var quiet = installOnExit?.Quiet == true;
        if (!IsVisible)
        {
            ShowActivated = !quiet;
            Show();
        }
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        if (!quiet) Activate();
        if (ClosingSlowPanel.IsVisible) ClosingExitNowButton.Focus();
    }

    private void ClosingExitNow_Click(object sender, RoutedEventArgs e) => ConfirmExitNow();

    /// <summary>Exit now: says what Martlet is still finishing and what exiting without waiting leaves undone, then exits if
    /// you agree.</summary>
    private void ConfirmExitNow()
    {
        if (!closing || closingFinished || confirmingExitNow) return;
        confirmingExitNow = true;
        bool confirmed;
        try
        {
            RevealClosing();
            confirmed = ConfirmationDialog.Confirm(this,
                $"Martlet is still {closingStep}.\n\nYou can exit now without waiting, but that interrupts it." +
                (closingEffect.Length == 0 ? "" : " " + closingEffect) + "\n\nExit now?",
                "Exit Martlet now", yes: "E_xit now", no: "_Keep waiting", questionId: "ExitNowQuestion");
        }
        finally { confirmingExitNow = false; }
        if (!confirmed || closingFinished) return;
        ErrorLog.Warn($"Exited without waiting: Martlet was still {closingStep}.");
        FinishClosing();
    }
}
