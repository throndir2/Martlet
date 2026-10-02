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

public partial class MemoryWindow : ThemedWindow
{
    private sealed record RetentionOption(string Label, TimeSpan? Duration, bool KeepCurrent = false)
    {
        public override string ToString() => Label;
    }

    private static readonly RetentionOption[] NewRetentionOptions =
    [
        new("Until I delete it", null),
        new("Delete after 30 days", TimeSpan.FromDays(30)),
        new("Delete after 90 days", TimeSpan.FromDays(90)),
        new("Delete after 1 year", TimeSpan.FromDays(365))
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
            "Couldn't load memory settings. No facts were opened.");
        if (closed || loaded is null)
            return;
        if (loaded.State != SettingsLoadState.Loaded || loaded.Error is not null || loaded.Settings is null)
        {
            loadedSettings = null;
            loadedRevision = null;
            ConfigurationStatus.Text = loaded.Error?.Summary ??
                "Finish Setup before configuring memory.";
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
            EnableChoice.IsChecked = true;
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
        rendering = false;
        DisposeExportPreview();
        FactsList.ItemsSource = null;
        FactContent.Clear();
        FactDetails.Clear();
        RetentionChoice.SelectedIndex = -1;
        ConfigurationStatus.Text = memory is null
            ? "Older memory settings loaded. Save to update them and turn memory on."
            : $"Memory is {(memory.Enabled ? "on" : "off")}.";
        RenderResolvedDirectory();
        RenderActions();
        if (memory is { Enabled: true } && !closed)
            await RefreshFactsAsync();
    }

    private async void SaveConfiguration_Click(object sender, RoutedEventArgs e)
    {
        if (loadedSettings is null)
            return;
        MemoryConfigurationSaveResult? result = null;
        ConfigurationStatus.Text = "Checking memory settings...";
        var policy = CustomChoice.IsChecked == true
            ? MemoryStoragePolicy.CustomLocalDirectory
            : MemoryStoragePolicy.AppLocalData;
        var enabled = EnableChoice.IsChecked == true;
        var customDirectory = CustomDirectory.Text;
        await RunAsync(async token =>
        {
            result = await service.SaveConfigurationAsync(
                loadedSettings,
                loadedRevision,
                enabled,
                policy,
                customDirectory,
                token).ConfigureAwait(false);
        }, "Couldn't save memory settings. Existing settings and facts were preserved.");
        if (closed || result is null)
            return;
        if (!result.Save.Save.Saved)
        {
            ConfigurationStatus.Text = result.Save.Save.Error?.Summary ??
                "Couldn't save memory settings. Reload and try again.";
            return;
        }
        loadedSettings = result.Settings;
        loadedRevision = result.Save.Save.Revision;
        configurationRevision = result.Settings.Memory!.ConfigurationRevision;
        DisposeExportPreview();
        FactsList.ItemsSource = null;
        ConfigurationStatus.Text = result.Settings.Memory.Enabled
            ? "Memory is on. Martlet will remember and recall lasting facts."
            : "Memory is off. Saved facts stay on this PC; turn memory on to review or delete them.";
        RenderResolvedDirectory();
        RenderActions();
        if (result.Settings.Memory.Enabled && !closed)
            await RefreshFactsAsync();
    }

    private async void RefreshFacts_Click(object sender, RoutedEventArgs e) => await RefreshFactsAsync();

    private async Task RefreshFactsAsync()
    {
        if (!RequireCurrentEnabledConfiguration(FactStatus))
            return;
        MemoryInspection? inspection = null;
        await RunAsync(async token =>
            inspection = await service.InspectAsync(configurationRevision, token).ConfigureAwait(false),
            "Couldn't refresh facts.");
        if (closed || inspection is null)
            return;
        FactsList.ItemsSource = inspection.Facts.OrderByDescending(fact => fact.UpdatedAtUtc).ToArray();
        FactStatus.Text = inspection.Facts.Count == 1 ? "1 fact remembered." : $"{inspection.Facts.Count} facts remembered.";
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
            FactStatus.Text = "Choose retention before saving.";
            return;
        }
        await RunAsync(async token =>
            receipt = await service.SaveFactAsync(
                configurationRevision, content, retention, token).ConfigureAwait(false),
            "Couldn't save the fact.");
        if (closed || receipt is null)
            return;
        DisposeExportPreview();
        FactContent.Clear();
        RetentionChoice.SelectedIndex = -1;
        await RefreshFactsAsync();
        FactStatus.Text = "Fact saved.";
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
            FactStatus.Text = "Choose retention before saving changes.";
            return;
        }
        await RunAsync(async token =>
            receipt = await service.EditFactAsync(
                configurationRevision, fact, content, retention, token).ConfigureAwait(false),
            "Couldn't edit the fact. Reload and try again.");
        if (closed || receipt is null)
            return;
        DisposeExportPreview();
        await RefreshFactsAsync();
        FactStatus.Text = "Fact updated.";
    }

    private async void DeleteFact_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireCurrentEnabledConfiguration(FactStatus))
            return;
        if (FactsList.SelectedItem is not MemoryFact fact ||
            !confirm(this,
                $"Delete this remembered fact?\n\n\"{PreviewFact(fact.Content)}\"",
                "Delete remembered fact"))
            return;
        MemoryDeleteReceipt? receipt = null;
        await RunAsync(async token =>
            receipt = await service.DeleteFactAsync(configurationRevision, fact, token).ConfigureAwait(false),
            "Couldn't delete the fact. Reload and try again.");
        if (closed || receipt is null)
            return;
        DisposeExportPreview();
        await RefreshFactsAsync();
        FactStatus.Text = "Fact deleted.";
    }

    private async void PurgeExpired_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireCurrentEnabledConfiguration(FactStatus))
            return;
        MemoryExpiryReceipt? receipt = null;
        await RunAsync(async token =>
            receipt = await service.PurgeExpiredAsync(configurationRevision, token).ConfigureAwait(false),
            "Couldn't delete expired facts. Reload and try again.");
        if (closed || receipt is null)
            return;
        DisposeExportPreview();
        await RefreshFactsAsync();
        FactStatus.Text = receipt.DeletedFacts == 1 ? "Deleted 1 expired fact." : $"Deleted {receipt.DeletedFacts} expired facts.";
    }

    private async void CreateExportPreview_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireCurrentEnabledConfiguration(ExportStatus))
            return;
        MemoryExportPreview? preview = null;
        await RunAsync(async token =>
            preview = await service.CreateExportPreviewAsync(configurationRevision, token).ConfigureAwait(false),
            "Couldn't prepare the export. No file was created.");
        if (closed || preview is null)
            return;
        DisposeExportPreview();
        exportPreview = preview;
        ExportPreviewText.Text = Encoding.UTF8.GetString(preview.Preview());
        ExportSummary.Text = preview.FactCount == 1 ? "Preview ready: 1 fact." : $"Preview ready: {preview.FactCount} facts.";
        rendering = true;
        AcceptExport.IsChecked = false;
        rendering = false;
        ExportStatus.Text = "Review the preview. Nothing has been exported.";
        RenderActions();
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireCurrentEnabledConfiguration(ExportStatus))
            return;
        if (exportPreview is null || AcceptExport.IsChecked != true)
        {
            ExportStatus.Text = "Review the preview and choose a destination before exporting.";
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
                "Couldn't create the export. No file was created.");
        }
        catch (Exception error) when (error is MemoryException or ArgumentException or NotSupportedException)
        {
            ExportStatus.Text = Describe(error);
        }
        if (closed || receipt is null)
            return;
        ExportStatus.Text = "Memory export created. It was not uploaded.";
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
            new RetentionOption("Keep current retention", null, KeepCurrent: true));
        RetentionChoice.SelectedIndex = NewRetentionOptions.Length;
        FactDetails.Text =
            $"Created: {When(fact.CreatedAtUtc)} ({MemoryPromptContext.Source(fact.CreatedFrom.SourceKind)})\n" +
            $"Updated: {When(fact.UpdatedAtUtc)} ({MemoryPromptContext.Source(fact.LastModifiedBy.SourceKind)})\n" +
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
        InvalidateDraftPresentation();
        RenderResolvedDirectory();
        RenderActions();
    }

    private void ConfigurationText_Changed(object sender, TextChangedEventArgs e)
    {
        if (rendering)
            return;
        InvalidateDraftPresentation();
        RenderResolvedDirectory();
        RenderActions();
    }

    private void RenderResolvedDirectory()
    {
        if (ResolvedDirectoryText is null)
            return;
        ResolvedDirectoryText.Text = CustomChoice.IsChecked == true
            ? $"Custom folder: {CustomDirectory.Text}"
            : $"Folder: {service.DefaultDirectory}";
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
            ConfigurationStatus.Text = "Another Martlet action is still finishing. Wait a moment and try again.";
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
            ConfigurationStatus.Text = "Memory cleanup is pending. Check folder access, then retry cleanup.";
        var enabled = ConfigurationMatchesPersisted() &&
            loadedSettings?.Memory is { Enabled: true } memory &&
            memory.ConfigurationRevision == configurationRevision;
        ReloadButton.IsEnabled = !busy;
        SaveConfigurationButton.IsEnabled = !busy && loadedSettings is not null;
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
        status.Text = "Save or reload memory settings before changing facts.";
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
        FactStatus.Text = "Memory settings changed. Save or reload before changing facts.";
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
        OperationCanceledException => "Memory action canceled. Reload if the window looks out of date.",
        _ => "Memory storage is unavailable. Check the selected folder and free space."
    };

    private static string RetentionText(MemoryRetention retention) =>
        retention.Kind == MemoryRetentionKind.UntilDeleted
            ? "kept until you delete it"
            : $"expires {When(retention.ExpiresAtUtc!.Value)}";

    private static string When(DateTimeOffset value) => value.LocalDateTime.ToString("g");

    private static string PreviewFact(string content)
    {
        var oneLine = string.Join(' ', content.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return oneLine.Length <= 160 ? oneLine : oneLine[..157] + "...";
    }

    private static bool Confirm(Window owner, string text, string title) =>
        ConfirmationDialog.Confirm(owner, text, title);

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
