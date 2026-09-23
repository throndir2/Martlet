using System.ComponentModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;
using Martlet.Memory;
using Microsoft.Win32;

namespace Martlet.Desktop;

public partial class MemoryWindow : Window
{
    private sealed record RetentionOption(string Label, TimeSpan? Duration, bool KeepCurrent = false)
    {
        public override string ToString() => Label;
    }

    private static readonly RetentionOption[] NewRetentionOptions =
    [
        new("Until I explicitly delete it", null),
        new("Expire 30 days after this save/edit", TimeSpan.FromDays(30)),
        new("Expire 90 days after this save/edit", TimeSpan.FromDays(90)),
        new("Expire 365 days after this save/edit", TimeSpan.FromDays(365))
    ];

    private readonly DesktopMemoryService service;
    private readonly SetupOperationRunner operations;
    private readonly Func<string?> chooseDirectory;
    private readonly Func<string?> chooseExport;
    private readonly Func<Window, string, string, bool> confirm;
    private AppSettings? loadedSettings;
    private string? loadedRevision;
    private Guid configurationRevision;
    private SetupOperation? active;
    private MemoryExportPreview? exportPreview;
    private bool closed;
    private bool rendering;

    internal MemoryWindow(
        DesktopMemoryService service,
        SetupOperationRunner operations,
        Func<string?>? chooseDirectory = null,
        Func<string?>? chooseExport = null,
        Func<Window, string, string, bool>? confirm = null)
    {
        this.service = service;
        this.operations = operations;
        this.chooseDirectory = chooseDirectory ?? PickDirectory;
        this.chooseExport = chooseExport ?? PickExport;
        this.confirm = confirm ?? Confirm;
        InitializeComponent();
        RetentionChoice.ItemsSource = NewRetentionOptions;
        RenderActions();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e) => await LoadAsync();
    private async void Reload_Click(object sender, RoutedEventArgs e) => await LoadAsync();
    private void RetryCleanup_Click(object sender, RoutedEventArgs e) => service.RetryCleanup();

    private async Task LoadAsync()
    {
        SettingsLoadResult? loaded = null;
        await RunAsync(async token => loaded = await service.LoadAsync(token).ConfigureAwait(false),
            "Memory settings load failed or was canceled. No fact store was opened.");
        if (closed || loaded is null)
            return;
        if (loaded.State != SettingsLoadState.Loaded || loaded.Error is not null || loaded.Settings is null)
        {
            loadedSettings = null;
            loadedRevision = null;
            ConfigurationStatus.Text = loaded.Error?.Summary ??
                "Create or complete a local profile before configuring memory. No fact store was opened.";
            RenderActions();
            return;
        }

        loadedSettings = loaded.Settings;
        loadedRevision = loaded.Revision;
        var memory = loaded.Settings.Memory;
        rendering = true;
        if (memory is null)
        {
            AppLocalChoice.IsChecked = true;
            CustomChoice.IsChecked = false;
            CustomDirectory.Text = "";
            EnableChoice.IsChecked = false;
            configurationRevision = Guid.Empty;
        }
        else
        {
            AppLocalChoice.IsChecked = memory.StoragePolicy == MemoryStoragePolicy.AppLocalData;
            CustomChoice.IsChecked = memory.StoragePolicy == MemoryStoragePolicy.CustomLocalDirectory;
            CustomDirectory.Text = memory.CustomDirectory ?? "";
            EnableChoice.IsChecked = memory.Enabled;
            configurationRevision = memory.ConfigurationRevision;
        }
        AcceptEnable.IsChecked = false;
        rendering = false;
        DisposeExportPreview();
        FactsList.ItemsSource = null;
        FactContent.Clear();
        FactDetails.Clear();
        RetentionChoice.SelectedIndex = -1;
        ConfigurationStatus.Text = memory is null
            ? "Legacy settings loaded. Memory is OFF; saving explicitly migrates settings with an atomic original snapshot. The fact store was not opened."
            : $"Memory settings loaded: {(memory.Enabled ? "ENABLED" : "OFF")}. The fact store was not opened.";
        RenderResolvedDirectory();
        RenderActions();
    }

    private async void SaveConfiguration_Click(object sender, RoutedEventArgs e)
    {
        if (loadedSettings is null)
            return;
        MemoryConfigurationSaveResult? result = null;
        ConfigurationStatus.Text = "Validating the selected local memory scope; no store is opened by this configuration action.";
        var policy = CustomChoice.IsChecked == true
            ? MemoryStoragePolicy.CustomLocalDirectory
            : MemoryStoragePolicy.AppLocalData;
        var enabled = EnableChoice.IsChecked == true;
        var enableApproved = AcceptEnable.IsChecked == true;
        var customDirectory = CustomDirectory.Text;
        await RunAsync(async token =>
        {
            result = await service.SaveConfigurationAsync(
                loadedSettings,
                loadedRevision,
                enabled,
                enableApproved,
                policy,
                customDirectory,
                token).ConfigureAwait(false);
        }, "Memory configuration was not saved. Existing settings and memory data were preserved.");
        if (closed || result is null)
            return;
        if (!result.Save.Save.Saved)
        {
            ConfigurationStatus.Text = result.Save.Save.Error?.Summary ??
                "Memory configuration was not saved. Reload and review the current settings.";
            return;
        }
        loadedSettings = result.Settings;
        loadedRevision = result.Save.Save.Revision;
        configurationRevision = result.Settings.Memory!.ConfigurationRevision;
        rendering = true;
        AcceptEnable.IsChecked = false;
        rendering = false;
        DisposeExportPreview();
        FactsList.ItemsSource = null;
        ConfigurationStatus.Text = result.Settings.Memory.Enabled
            ? "Local memory enabled for this reviewed scope. No fact was read or written. Each retrieval still needs fresh per-action permission."
            : "Local memory is OFF. No store open/read/write is permitted while disabled.";
        RenderResolvedDirectory();
        RenderActions();
    }

    private async void RefreshFacts_Click(object sender, RoutedEventArgs e) => await RefreshFactsAsync();

    private async Task RefreshFactsAsync()
    {
        if (!RequireCurrentEnabledConfiguration(FactStatus))
            return;
        MemoryInspection? inspection = null;
        await RunAsync(async token =>
            inspection = await service.InspectAsync(configurationRevision, token).ConfigureAwait(false),
            "Fact inspection failed. No success is assumed.");
        if (closed || inspection is null)
            return;
        FactsList.ItemsSource = inspection.Facts;
        FactStatus.Text = $"Inspected {inspection.Facts.Count} fact(s) at store revision {inspection.StoreRevision}. Content remains local unless a later turn receives separate retrieval permission.";
        RenderActions();
    }

    private async void SaveFact_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireCurrentEnabledConfiguration(FactStatus))
            return;
        MemoryMutationReceipt? receipt = null;
        var retention = SelectedRetention(existing: null);
        var content = FactContent.Text;
        if (retention is null)
        {
            FactStatus.Text = "Choose explicit retention before saving a fact.";
            return;
        }
        await RunAsync(async token =>
            receipt = await service.SaveFactAsync(
                configurationRevision, content, retention, token).ConfigureAwait(false),
            "Fact save failed. No saved fact is claimed.");
        if (closed || receipt is null)
            return;
        DisposeExportPreview();
        FactContent.Clear();
        RetentionChoice.SelectedIndex = -1;
        await RefreshFactsAsync();
        FactStatus.Text = $"Fact saved explicitly at store revision {receipt.StoreRevision}. No transcript was ingested.";
    }

    private async void EditFact_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireCurrentEnabledConfiguration(FactStatus))
            return;
        if (FactsList.SelectedItem is not MemoryFact fact)
            return;
        MemoryMutationReceipt? receipt = null;
        var retention = SelectedRetention(fact);
        var content = FactContent.Text;
        if (retention is null)
        {
            FactStatus.Text = "Choose explicit retention before editing the selected fact.";
            return;
        }
        await RunAsync(async token =>
            receipt = await service.EditFactAsync(
                configurationRevision, fact, content, retention, token).ConfigureAwait(false),
            "Fact edit failed. Reload the current fact; no edit is claimed.");
        if (closed || receipt is null)
            return;
        DisposeExportPreview();
        await RefreshFactsAsync();
        FactStatus.Text = $"Fact edited explicitly at store revision {receipt.StoreRevision}; creation and last-modified provenance remain visible.";
    }

    private async void DeleteFact_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireCurrentEnabledConfiguration(FactStatus))
            return;
        if (FactsList.SelectedItem is not MemoryFact fact ||
            !confirm(this,
                $"Delete fact {fact.Id} revision {fact.Revision}? This removes it from the local source, lexical index and cache. Existing user-created exports or storage remnants are outside immediate erasure.",
                "Delete local memory fact"))
            return;
        MemoryDeleteReceipt? receipt = null;
        await RunAsync(async token =>
            receipt = await service.DeleteFactAsync(configurationRevision, fact, token).ConfigureAwait(false),
            "Fact deletion failed. Reload the current facts; no deletion is claimed.");
        if (closed || receipt is null)
            return;
        DisposeExportPreview();
        await RefreshFactsAsync();
        FactStatus.Text = $"Fact {receipt.FactId} deleted at store revision {receipt.StoreRevision}. In-flight app retrieval was invalidated.";
    }

    private async void PurgeExpired_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireCurrentEnabledConfiguration(FactStatus))
            return;
        MemoryExpiryReceipt? receipt = null;
        await RunAsync(async token =>
            receipt = await service.PurgeExpiredAsync(configurationRevision, token).ConfigureAwait(false),
            "Expiry purge failed. Reload current facts; no deletion is claimed.");
        if (closed || receipt is null)
            return;
        DisposeExportPreview();
        await RefreshFactsAsync();
        FactStatus.Text = $"Purged {receipt.DeletedFacts} expired fact(s); store revision {receipt.StoreRevision}.";
    }

    private async void CreateExportPreview_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireCurrentEnabledConfiguration(ExportStatus))
            return;
        MemoryExportPreview? preview = null;
        await RunAsync(async token =>
            preview = await service.CreateExportPreviewAsync(configurationRevision, token).ConfigureAwait(false),
            "Export preview failed. No export was authorized or created.");
        if (closed || preview is null)
            return;
        DisposeExportPreview();
        exportPreview = preview;
        ExportPreviewText.Text = Encoding.UTF8.GetString(preview.Preview());
        ExportSummary.Text = $"Frozen preview {preview.Id}; store revision {preview.StoreRevision}; facts {preview.FactCount}; bytes {preview.Bytes}; SHA-256 {preview.Sha256}. Export approval default: {(preview.ExportApprovedByDefault ? "YES" : "NO")}.";
        rendering = true;
        AcceptExport.IsChecked = false;
        rendering = false;
        ExportStatus.Text = "Review the exact frozen JSON. Nothing was exported.";
        RenderActions();
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireCurrentEnabledConfiguration(ExportStatus))
            return;
        if (exportPreview is null || AcceptExport.IsChecked != true)
        {
            ExportStatus.Text = "Export remains NO. Review the exact frozen JSON and explicitly authorize this destination.";
            return;
        }
        MemoryExportReceipt? receipt = null;
        try
        {
            var destination = ExportDestination.Text;
            var authorization = exportPreview.Authorize(
                destination, MemoryExportDecision.Export);
            await RunAsync(async token =>
                receipt = await service.ExportAsync(
                    configurationRevision, exportPreview, authorization,
                    destination, token).ConfigureAwait(false),
                "Memory export failed. No created export is claimed.");
        }
        catch (Exception error) when (error is MemoryException or ArgumentException or NotSupportedException)
        {
            ExportStatus.Text = Describe(error);
        }
        if (closed || receipt is null)
            return;
        ExportStatus.Text = $"Created reviewed local export {receipt.PreviewId}; revision {receipt.StoreRevision}; bytes {receipt.Bytes}; SHA-256 {receipt.Sha256}. It was not uploaded.";
        DisposeExportPreview();
        RenderActions();
    }

    private void FactsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (FactsList.SelectedItem is not MemoryFact fact)
        {
            FactDetails.Clear();
            RenderActions();
            return;
        }
        FactContent.Text = fact.Content;
        RetentionChoice.ItemsSource = NewRetentionOptions.Append(
            new RetentionOption("Keep the selected fact's current retention exactly", null, KeepCurrent: true));
        RetentionChoice.SelectedIndex = NewRetentionOptions.Length;
        FactDetails.Text =
            $"Fact ID: {fact.Id}; revision: {fact.Revision}\n" +
            $"Created: {fact.CreatedAtUtc:O}; updated: {fact.UpdatedAtUtc:O}\n" +
            $"Created provenance: {fact.CreatedFrom.SourceKind}; consent: {fact.CreatedFrom.ConsentId}; observed: {fact.CreatedFrom.ObservedAtUtc:O}\n" +
            $"Last-modified provenance: {fact.LastModifiedBy.SourceKind}; consent: {fact.LastModifiedBy.ConsentId}; observed: {fact.LastModifiedBy.ObservedAtUtc:O}\n" +
            $"Retention: {RetentionText(fact.Retention)}";
        RenderActions();
    }

    private MemoryRetention? SelectedRetention(MemoryFact? existing)
    {
        if (RetentionChoice.SelectedItem is not RetentionOption selected)
            return null;
        if (selected.KeepCurrent)
            return existing?.Retention;
        return selected.Duration is { } duration
            ? MemoryRetention.ExpiringAt(DateTimeOffset.UtcNow + duration)
            : MemoryRetention.UntilDeleted();
    }

    private void Configuration_Changed(object sender, RoutedEventArgs e)
    {
        if (rendering)
            return;
        if (!ReferenceEquals(sender, AcceptEnable))
        {
            rendering = true;
            AcceptEnable.IsChecked = false;
            rendering = false;
            InvalidateDraftPresentation();
        }
        RenderResolvedDirectory();
        RenderActions();
    }

    private void ConfigurationText_Changed(object sender, TextChangedEventArgs e)
    {
        if (rendering)
            return;
        rendering = true;
        AcceptEnable.IsChecked = false;
        rendering = false;
        InvalidateDraftPresentation();
        RenderResolvedDirectory();
        RenderActions();
    }

    private void RenderResolvedDirectory()
    {
        if (ResolvedDirectoryText is null)
            return;
        ResolvedDirectoryText.Text = CustomChoice.IsChecked == true
            ? $"Selected custom scope: {CustomDirectory.Text}"
            : $"App-owned scope: {service.DefaultDirectory}";
        CustomDirectory.IsEnabled = CustomChoice.IsChecked == true;
        BrowseDirectoryButton.IsEnabled = CustomChoice.IsChecked == true && !operations.IsRunning;
    }

    private void BrowseDirectory_Click(object sender, RoutedEventArgs e)
    {
        var selected = chooseDirectory();
        if (!string.IsNullOrWhiteSpace(selected))
            CustomDirectory.Text = selected;
    }

    private void BrowseExport_Click(object sender, RoutedEventArgs e)
    {
        var selected = chooseExport();
        if (!string.IsNullOrWhiteSpace(selected))
            ExportDestination.Text = selected;
    }

    private void ExportDestination_Changed(object sender, TextChangedEventArgs e)
    {
        if (!rendering && AcceptExport is not null)
        {
            rendering = true;
            AcceptExport.IsChecked = false;
            rendering = false;
        }
        RenderActions();
    }
    private void ExportConsent_Changed(object sender, RoutedEventArgs e)
    {
        if (!rendering)
            RenderActions();
    }

    private async Task RunAsync(Func<CancellationToken, Task> action, string failureText)
    {
        if (closed) return;
        if (operations.IsRunning)
        {
            ConfigurationStatus.Text = "Another app effect still owns resources or cleanup. Wait for actual release; no memory action was queued.";
            while (!closed && operations.IsRunning)
            {
                RenderActions();
                await Task.Delay(250);
            }
            if (!closed) RenderActions();
            return;
        }
        Exception? failure = null;
        var worker = operations.TryStart(async token =>
        {
            try
            {
                await action(token).ConfigureAwait(false);
                return new SetupWorkResult(SetupWorkOutcome.Completed);
            }
            catch (Exception error) when (error is MemoryException or DesktopMemoryException or
                ContractException or OperationCanceledException or ArgumentException or
                NotSupportedException or IOException or UnauthorizedAccessException)
            {
                failure = error;
                return new SetupWorkResult(error is OperationCanceledException
                    ? SetupWorkOutcome.Canceled : SetupWorkOutcome.Failed);
            }
        });
        if (worker is null)
            return;
        active = worker;
        RenderActions();
        while (!worker.Completion.IsCompleted)
        {
            await Task.WhenAny(worker.Completion, Task.Delay(250));
            if (!closed) RenderActions();
        }
        var completed = await worker.Completion;
        active = null;
        if (closed)
            return;
        if (failure is not null)
        {
            var text = Describe(failure);
            ConfigurationStatus.Text = FactStatus.Text = ExportStatus.Text =
                string.IsNullOrWhiteSpace(text) ? failureText : text;
        }
        else if (completed.Outcome != SetupWorkOutcome.Completed)
        {
            ConfigurationStatus.Text = FactStatus.Text = ExportStatus.Text = failureText;
        }
        RenderActions();
    }

    private void RenderActions()
    {
        if (SaveConfigurationButton is null)
            return;
        var busy = operations.IsRunning;
        RetryCleanupButton.IsEnabled = service.HasPendingCleanup;
        if (service.HasPendingCleanup)
            ConfigurationStatus.Text = "Private memory cleanup is pending. The store and app effect slot remain owned. " +
                "Resolve local file access, then Retry owned cleanup. Closing this window does not release ownership; " +
                "exiting Martlet may leave private partial bytes on disk.";
        var enabled = ConfigurationMatchesPersisted() &&
            loadedSettings?.Memory is { Enabled: true } memory &&
            memory.ConfigurationRevision == configurationRevision;
        ReloadButton.IsEnabled = !busy;
        SaveConfigurationButton.IsEnabled = !busy && loadedSettings is not null &&
            (EnableChoice.IsChecked != true || AcceptEnable.IsChecked == true);
        RefreshFactsButton.IsEnabled = PurgeExpiredButton.IsEnabled =
            CreateExportPreviewButton.IsEnabled = enabled && !busy;
        SaveFactButton.IsEnabled = enabled && !busy;
        EditFactButton.IsEnabled = DeleteFactButton.IsEnabled =
            enabled && !busy && FactsList.SelectedItem is MemoryFact;
        BrowseExportButton.IsEnabled = !busy;
        ExportButton.IsEnabled = enabled && !busy && exportPreview is not null &&
            AcceptExport.IsChecked == true && !string.IsNullOrWhiteSpace(ExportDestination.Text);
        RenderResolvedDirectory();
    }

    private bool ConfigurationMatchesPersisted()
    {
        if (loadedSettings?.Memory is not { } persisted)
            return false;
        var policy = CustomChoice.IsChecked == true
            ? MemoryStoragePolicy.CustomLocalDirectory
            : MemoryStoragePolicy.AppLocalData;
        if ((EnableChoice.IsChecked == true) != persisted.Enabled ||
            policy != persisted.StoragePolicy)
            return false;
        if (policy == MemoryStoragePolicy.AppLocalData)
            return true;
        try
        {
            return string.Equals(
                MemorySettings.NormalizeCustomDirectory(CustomDirectory.Text),
                persisted.CustomDirectory,
                StringComparison.Ordinal);
        }
        catch (ContractException)
        {
            return false;
        }
    }

    private bool RequireCurrentEnabledConfiguration(TextBlock status)
    {
        if (ConfigurationMatchesPersisted() &&
            loadedSettings?.Memory is { Enabled: true } memory &&
            memory.ConfigurationRevision == configurationRevision)
            return true;
        status.Text = "Save or reload the displayed memory configuration before using the fact store. No store action was started.";
        return false;
    }

    private void InvalidateDraftPresentation()
    {
        if (ConfigurationMatchesPersisted())
            return;
        FactsList.ItemsSource = null;
        FactContent.Clear();
        FactDetails.Clear();
        RetentionChoice.ItemsSource = NewRetentionOptions;
        RetentionChoice.SelectedIndex = -1;
        DisposeExportPreview();
        FactStatus.Text = "Memory configuration has an unsaved scope change. Store actions are disabled until Save or Reload.";
    }

    private void DisposeExportPreview()
    {
        exportPreview?.Dispose();
        exportPreview = null;
        if (ExportPreviewText is null)
            return;
        ExportPreviewText.Clear();
        ExportSummary.Text = "";
        rendering = true;
        AcceptExport.IsChecked = false;
        rendering = false;
    }

    private static string Describe(Exception error) => error switch
    {
        DesktopMemoryException app => app.Message,
        MemoryException memory => memory.Message,
        ContractException contract => contract.Message,
        OperationCanceledException => "Memory action canceled. No unverified completion is claimed; reload current settings/facts.",
        _ => "Local memory storage is unavailable. Check the selected local path, access and free space; do not elevate or use network/link storage."
    };

    private static string RetentionText(MemoryRetention retention) =>
        retention.Kind == MemoryRetentionKind.UntilDeleted
            ? "until explicitly deleted"
            : $"expires at {retention.ExpiresAtUtc:O}";

    private static bool Confirm(Window owner, string text, string title) =>
        MessageBox.Show(owner, text, title, MessageBoxButton.YesNo,
            MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;

    private static string? PickDirectory()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Choose a local memory directory",
            Multiselect = false
        };
        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }

    private static string? PickExport()
    {
        var dialog = new SaveFileDialog
        {
            Title = "Create a local memory JSON export",
            Filter = "JSON files (*.json)|*.json",
            DefaultExt = ".json",
            AddExtension = true,
            OverwritePrompt = true
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        closed = true;
        active?.RequestCancellation();
        DisposeExportPreview();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
