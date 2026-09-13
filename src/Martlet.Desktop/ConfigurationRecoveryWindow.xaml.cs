using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Martlet.Core.Settings;
using Microsoft.Win32;

namespace Martlet.Desktop;

public partial class ConfigurationRecoveryWindow : Window
{
    private readonly ConfigurationRecoveryController controller;
    private readonly Func<string, bool>? confirm;
    private readonly TimeProvider clock;
    private readonly TimeSpan timeout;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private CancellationTokenSource? observation;
    private bool closed, observing;
    private string? observationMessage;
    private ConfigurationRestorePlan? rendered;
    internal static MessageBoxResult ConfirmationDefault => MessageBoxResult.No;

    internal ConfigurationRecoveryWindow(ConfigurationRecoveryController controller, Func<string, bool>? confirm = null,
        TimeProvider? clock = null, TimeSpan? observationTimeout = null)
    {
        this.controller = controller;
        this.confirm = confirm;
        this.clock = clock ?? TimeProvider.System;
        timeout = observationTimeout ?? TimeSpan.FromSeconds(5);
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromSeconds(30))
            throw new ArgumentOutOfRangeException(nameof(observationTimeout));
        InitializeComponent();
        ScopeText.Text = ConfigurationSnapshot.Scope;
        controller.ClearPreview();
        timer.Tick += (_, _) => Render();
        timer.Start();
        Closed += (_, _) => Retire();
        Render();
    }

    private void Render()
    {
        if (closed) return;
        var active = controller.IsBusy;
        Actions.IsEnabled = !observing && !active && !controller.NeedsCleanup;
        RestoreButton.IsEnabled = Actions.IsEnabled && controller.Preview is not null;
        CancelButton.IsEnabled = active || observing;
        CleanupButton.IsEnabled = !active && !observing && controller.NeedsCleanup;
        ResultText.Text = observationMessage ?? controller.Message;
        ActivityText.Text = active
            ? "An app-shared worker still owns actual IO/cancellation callbacks. Timeout/Stop/Close do NOT release it or prove rollback. Reopening cannot start another writer."
            : controller.NeedsCleanup ? "Owned staging cleanup pending; no new recovery action allowed."
            : "No worker active. No action resumes automatically. Inspect the actual receipt/result after interruption.";
        var plan = controller.Preview;
        if (ReferenceEquals(plan, rendered)) return;
        rendered = plan;
        PreviewText.Text = plan?.Summary ?? "";
        CandidateText.Text = plan?.CandidateJson ?? "";
    }

    private bool MayStart()
    {
        if (closed) return false;
        if (!observing && !controller.IsBusy) return true;
        observationMessage = "An app action still owns IO. Wait for actual release; no overlapping recovery started.";
        Render();
        return false;
    }

    private bool Confirm(string text) => confirm?.Invoke(text) ?? MessageBox.Show(this, text,
        "Review local configuration action", MessageBoxButton.YesNo, MessageBoxImage.Warning, ConfirmationDefault) == MessageBoxResult.Yes;

    private async Task Observe(SetupOperation? operation)
    {
        observationMessage = null;
        if (operation is null) { Render(); return; }
        using var stop = new CancellationTokenSource();
        observation = stop;
        observing = true;
        Render();
        try
        {
            var delay = Task.Delay(timeout, clock, stop.Token);
            var finished = await Task.WhenAny(operation.Completion, delay);
            if (closed) return;
            if (stop.IsCancellationRequested || finished != operation.Completion)
            {
                controller.StopObserving();
                observationMessage = "Recovery observation stopped/timed out; cancellation requested, NOT rollback or completion. Keep Martlet open. After release, close/reopen to inspect the actual receipt; read a fresh preview to retry.";
            }
            else
            {
                var completed = await operation.Completion;
                if (completed.Outcome == SetupWorkOutcome.Failed && controller.Preview is not null)
                {
                    controller.ClearPreview();
                    observationMessage = "Recovery worker or cancellation callbacks failed. No success is assumed; reopen and inspect actual retained receipt/files, then read a fresh preview.";
                }
            }
        }
        finally
        {
            stop.Cancel();
            if (ReferenceEquals(observation, stop)) observation = null;
            observing = false;
            Render();
        }
    }

    private async void Backup_Click(object sender, RoutedEventArgs e)
    {
        if (!MayStart()) return;
        var destination = BackupPath.Text;
        if (!Confirm(ConfigurationSnapshot.Scope + $"\n\nCreate-only output: {destination}\nProceed?")) return;
        if (closed || destination != BackupPath.Text || !MayStart()) return;
        await Observe(controller.Backup(destination));
    }

    private async void Preview_Click(object sender, RoutedEventArgs e)
    {
        if (MayStart()) await Observe(controller.ReadPreview(SourcePath.Text));
    }

    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (!MayStart() || controller.Preview is not { } plan) return;
        var source = SourcePath.Text;
        if (!Confirm(plan.Summary + "\n\nReplace with the EXACT candidate JSON shown in the preview? No keys, permissions or audio readiness will be restored.")) return;
        if (closed || source != SourcePath.Text || !ReferenceEquals(plan, controller.Preview) || !MayStart())
        {
            observationMessage = "Preview/source changed during confirmation. Read a fresh preview; restore rejected.";
            Render();
            return;
        }
        try { await Observe(controller.Restore(plan, plan.Approve(plan.SnapshotDigest, plan.Destination, plan.ExpectedRevision))); }
        catch (RecoveryException error) { observationMessage = error.Message; controller.ClearPreview(); Render(); }
    }

    private void Source_Changed(object sender, TextChangedEventArgs e)
    {
        controller.ClearPreview();
        if (IsInitialized) Render();
    }

    private void ChooseBackup_Click(object sender, RoutedEventArgs e)
    {
        if (!MayStart()) return;
        var dialog = new SaveFileDialog
        {
            Filter = "Martlet configuration snapshot (*.martlet-config)|*.martlet-config",
            FileName = $"Martlet-{Guid.NewGuid():N}.martlet-config", AddExtension = true,
            DefaultExt = ".martlet-config", OverwritePrompt = true
        };
        if (dialog.ShowDialog(this) == true) BackupPath.Text = dialog.FileName;
    }

    private void ChooseSource_Click(object sender, RoutedEventArgs e)
    {
        if (!MayStart()) return;
        var dialog = new OpenFileDialog { Filter = "Martlet configuration snapshot (*.martlet-config)|*.martlet-config", Multiselect = false };
        if (dialog.ShowDialog(this) == true) SourcePath.Text = dialog.FileName;
    }

    private async void Cleanup_Click(object sender, RoutedEventArgs e)
    {
        if (MayStart()) await Observe(controller.RetryCleanup());
    }
    private void Cancel_Click(object sender, RoutedEventArgs e) { controller.StopObserving(); observation?.Cancel(); Render(); }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void Window_Closing(object? sender, CancelEventArgs e) => Retire();
    private void Retire()
    {
        if (closed) return;
        closed = true;
        timer.Stop();
        controller.StopObserving();
        observation?.Cancel();
        rendered = null;
        PreviewText.Clear();
        CandidateText.Clear();
    }
}
