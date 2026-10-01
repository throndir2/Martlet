using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Martlet.Diagnostics;
using Martlet.Support;
using Microsoft.Win32;

namespace Martlet.Desktop;

public partial class TroubleshootingWindow : ThemedWindow
{
    private readonly SupportController support;
    private readonly Func<Task>? refresh;
    private readonly Func<string, bool>? confirm;
    private readonly TimeProvider clock;
    private readonly TimeSpan observationTimeout;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private SupportPreview? displayed;
    private bool initialized, closed, awaiting, confirming, transferring;
    private long selection, frozenSelection = -1, destinationRevision;
    internal bool IsObserving => timer.IsEnabled;

    internal TroubleshootingWindow(SupportController support, Func<Task>? refresh = null,
        Func<string, bool>? confirm = null, TimeProvider? clock = null, TimeSpan? observationTimeout = null)
    {
        this.support = support; this.refresh = refresh; this.confirm = confirm;
        this.clock = clock ?? TimeProvider.System;
        this.observationTimeout = observationTimeout ?? TimeSpan.FromSeconds(5);
        if (this.observationTimeout <= TimeSpan.Zero || this.observationTimeout > TimeSpan.FromSeconds(30))
            throw new ArgumentOutOfRangeException(nameof(observationTimeout));
        InitializeComponent();
        HelpText.Text = SupportHelp.Text;
        LocationText.Text = support.Location is { } path ? $"Journal scope: {path}\nNot inspected on opening. Storage readiness UNKNOWN until an explicit action."
            : "No valid settings data directory. Relaunch with an accessible absolute local --data-directory; no automatic alternative.";
        FromText.Text = this.clock.GetUtcNow().Subtract(TimeSpan.FromDays(7)).ToString("O");
        ThroughText.Text = this.clock.GetUtcNow().ToString("O");
        OmissionsText.Text = SupportSnapshot.OmissionSummary;
        ErrorLogText.Text = ErrorLog.Directory is { } logs
            ? $"Crash and error log (always on, local only, never uploaded; includes exception details and stack traces): {logs}"
            : "Crash and error log unavailable: the logs folder could not be created.";
        ResultText.Text = "No export selected or approved. No support contact/upload channel is configured.";
        initialized = true;
        timer.Tick += (_, _) => Render();
        timer.Start();
        Render();
    }

    private void OpenErrorLogs_Click(object sender, RoutedEventArgs e)
    {
        if (!ErrorLog.OpenFolder()) ErrorLogText.Text = "Could not open the logs folder. " + ErrorLogText.Text;
    }

    private void Render()
    {
        if (closed) return;
        WorkText.Text = support.Status;
        var report = support.Report;
        StatusText.Text = report is null ? "No shared report observed. Use Refresh shared local status; no fake pass is supplied."
            : ReportFormatter.Human(report);
        StatusText.Text += "\n\n" + support.LiveStatus;
        var available = !support.IsBusy && !support.NeedsCleanup && !awaiting && !confirming;
        RecordButton.IsEnabled = available && !support.Recording && support.Location is not null;
        StopButton.IsEnabled = support.HasResources;
        CleanupButton.IsEnabled = !support.IsBusy && support.NeedsCleanup && !awaiting;
        RefreshButton.IsEnabled = available && refresh is not null;
        FreezeButton.IsEnabled = available && report is not null && support.Report?.SettingsState is not null;
        ClearButton.IsEnabled = available && displayed is not null;
        ChooseButton.IsEnabled = available && displayed is not null;
        ExportButton.IsEnabled = available && displayed is not null && frozenSelection == selection &&
            !string.IsNullOrWhiteSpace(DestinationText.Text);
        LogsChoice.IsEnabled = FromText.IsEnabled = ThroughText.IsEnabled = available;
        DestinationText.IsEnabled = available;
    }

    private async Task<bool> Observe(SupportWork? work)
    {
        if (work is null) { Render(); return false; }
        awaiting = true;
        Render();
        var completed = await Task.WhenAny(work.Completion, Task.Delay(observationTimeout, clock));
        awaiting = false;
        if (closed) return false;
        if (completed != work.Completion)
        {
            support.CancelAndClose();
            ClearPresentation();
            ResultText.Text = "Support observation timed out. Cancellation requested; IO and callbacks still own resources until they finish. " +
                "No rollback or export failure/success is inferred. The ownership status will show the actual eventual outcome.";
            Render();
            return false;
        }
        var result = await work.Completion;
        ResultText.Text = result.Message;
        Render();
        return result.Failure is null;
    }
    private async void Record_Click(object sender, RoutedEventArgs e)
    {
        if (!RecordButton.IsEnabled) return;
        if (await Observe(support.StartRecording()) && support.Report is { } current)
            support.ObserveReport(current, record: true);
    }
    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        support.CancelAndClose();
        ClearPresentation();
        ResultText.Text = "Stop requested. Recording OFF; actual IO/callbacks and cleanup remain owned. No new work is queued.";
        Render();
    }
    private async void Cleanup_Click(object sender, RoutedEventArgs e) { if (CleanupButton.IsEnabled) await Observe(support.RetryCleanup()); }
    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (!RefreshButton.IsEnabled || refresh is null) return;
        await refresh();
        if (!closed) Render();
    }
    private async void Freeze_Click(object sender, RoutedEventArgs e)
    {
        if (!FreezeButton.IsEnabled) return;
        if (!DateTimeOffset.TryParse(FromText.Text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var from) ||
            !DateTimeOffset.TryParse(ThroughText.Text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var through) ||
            from.Offset != TimeSpan.Zero || through.Offset != TimeSpan.Zero)
        { ResultText.Text = "Enter explicit UTC times (for example 2026-09-13T12:00:00+00:00); no range was selected."; return; }
        ClearPresentation();
        var chosen = selection;
        if (await Observe(support.Freeze(LogsChoice.IsChecked == true, new(from, through))) &&
            !closed && chosen == selection && support.Preview is { } preview)
        {
            displayed = preview;
            frozenSelection = chosen;
            InventoryText.Text = $"Snapshot {preview.Id}\nSHA-256 {preview.Digest}\nFrozen UTC {preview.FrozenAt:O}\n" +
                $"Receipt range {preview.Range.FromUtc:O} through {preview.Range.ThroughUtc:O}\n{preview.Scope}\n" +
                string.Join("\n", preview.Files.Select(f => $"{f.Name}: {f.Bytes} bytes; SHA-256 {f.Sha256}\nSource: {f.Source}"));
            for (var i = 0; i < preview.Files.Count; i++)
                PreviewTabs.Items.Add(new TabItem { Header = preview.Files[i].Name, Content = new TextBox
                {
                    Text = preview.Contents[i], IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto
                } });
            Render();
        }
    }
    private void Choose_Click(object sender, RoutedEventArgs e)
    {
        if (!ChooseButton.IsEnabled) return;
        var picker = new SaveFileDialog
        {
            Title = "Choose local support ZIP destination (nothing exported yet)",
            Filter = "Support ZIP (*.zip)|*.zip", DefaultExt = ".zip", AddExtension = true,
            FileName = $"Martlet-support-{clock.GetUtcNow():yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.zip",
            OverwritePrompt = false, CheckPathExists = true
        };
        if (picker.ShowDialog(this) == true) DestinationText.Text = picker.FileName;
    }
    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (!ExportButton.IsEnabled || displayed is not { } preview) return;
        string destination;
        try
        {
            if (!Path.IsPathFullyQualified(DestinationText.Text)) throw new ArgumentException();
            destination = Path.GetFullPath(DestinationText.Text);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        { ResultText.Text = "Choose an absolute local .zip destination with an existing parent directory."; return; }
        var chosen = selection;
        var pathRevision = destinationRevision;
        var sourceRevision = support.Revision;
        confirming = true;
        Render();
        var text = $"Export ONLY this reviewed frozen snapshot locally?\nSnapshot: {preview.Id}\nSHA-256: {preview.Digest}\n" +
            $"Exact destination: {destination}\n" +
            string.Join("\n", preview.Files.Select(f => $"{f.Name}: {f.Bytes} bytes; SHA-256 {f.Sha256}")) +
            "\nNo files will be overwritten. This is NOT an upload. No support contact/channel is configured. Default is No.";
        bool approved;
        try
        {
            approved = confirm?.Invoke(text) ??
                ConfirmationDialog.Confirm(this, text, "Confirm exact local support export");
        }
        finally { confirming = false; }
        if (closed) return;
        if (!approved) { ResultText.Text = "No export approved. Nothing was written."; Render(); return; }
        if (selection != chosen || pathRevision != destinationRevision || support.Revision != sourceRevision || support.Preview?.Id != preview.Id)
        { ResultText.Text = "Selection, source or destination changed during confirmation. Review the frozen preview and confirm again."; Render(); return; }
        await Observe(support.Export(preview.Id, preview.Digest, destination, explicitlyApproved: true));
    }
    private void Selection_Changed(object sender, RoutedEventArgs e)
    {
        if (!initialized) return;
        selection++;
        ResultText.Text = "Selection changed. Freeze and review a new preview before export; no previous approval applies.";
        Render();
    }
    private void Destination_Changed(object sender, TextChangedEventArgs e)
    {
        if (!initialized) return;
        destinationRevision++;
        ResultText.Text = "Destination changed. A new exact-destination default-No confirmation is required.";
        Render();
    }
    private async void Clear_Click(object sender, RoutedEventArgs e)
    {
        if (!ClearButton.IsEnabled) return;
        ClearPresentation();
        await Observe(support.ClosePreview());
    }
    private void ClearPresentation()
    {
        foreach (TabItem tab in PreviewTabs.Items)
            if (tab.Content is TextBox text) text.Clear();
        PreviewTabs.Items.Clear();
        InventoryText.Clear();
        displayed = null;
        frozenSelection = -1;
    }
    internal void CloseForPresentationTransfer()
    {
        transferring = true;
        Close();
    }

    private void RetirePresentation()
    {
        if (closed) return;
        closed = true;
        timer.Stop();
        ClearPresentation();
        if (!transferring) support.CancelAndClose();
    }
    private void Window_Closing(object? sender, CancelEventArgs e) => RetirePresentation();
    protected override void OnClosed(EventArgs e)
    {
        // WPF owner closure bypasses a child's Closing event.
        RetirePresentation();
        base.OnClosed(e);
    }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
