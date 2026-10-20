using System.ComponentModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;
using Martlet.Core.Speakers;
using Martlet.Memory;
using Microsoft.Win32;

namespace Martlet.Desktop;

/// <summary>The Memory window's settings: memory on or off, where memory is stored, and checks that the shown settings are the saved ones.</summary>
public partial class MemoryWindow
{
    /// <summary>Saves the storage and on/off choices as soon as they change (there is no Save button), into the newest saved
    /// settings. Returns false to be tried again shortly while another Martlet action runs.</summary>
    private async Task<bool> SaveConfigurationAsync()
    {
        if (closed || loadedSettings is null || ConfigurationMatchesPersisted()) return true;
        if (operations.IsRunning) return false;
        var policy = CustomChoice.IsChecked == true
            ? MemoryStoragePolicy.CustomLocalDirectory
            : MemoryStoragePolicy.AppLocalData;
        var enabled = EnableChoice.IsChecked == true;
        var customDirectory = CustomDirectory.Text;
        if (policy == MemoryStoragePolicy.CustomLocalDirectory && string.IsNullOrWhiteSpace(customDirectory))
        {
            ConfigurationStatus.Text = "Choose a folder for memory (Browse), or use the Martlet folder.";
            return true;
        }
        MemoryConfigurationSaveResult? result = null;
        ConfigurationStatus.Text = "Saving memory settings...";
        var basis = loadedSettings;
        var basisRevision = loadedRevision;
        await RunAsync(async token =>
        {
            // Save into the newest settings, so a change made elsewhere meanwhile (or synced in) isn't overwritten.
            var fresh = await service.LoadAsync(token).ConfigureAwait(false);
            result = await service.SaveConfigurationAsync(
                fresh.Settings ?? basis,
                fresh.Settings is null ? basisRevision : fresh.Revision,
                enabled,
                policy,
                customDirectory,
                token).ConfigureAwait(false);
        }, "Couldn't save memory settings; your existing settings and facts are kept.");
        if (closed || result is null)
            return true;
        if (!result.Save.Save.Saved)
        {
            ConfigurationStatus.Text = result.Save.Save.Error?.Summary ??
                "Couldn't save memory settings; reload and try again.";
            return true;
        }
        loadedSettings = result.Settings;
        loadedRevision = result.Save.Save.Revision;
        configurationRevision = result.Settings.Memory!.ConfigurationRevision;
        DisposeExportPreview();
        ClearFacts();
        ConfigurationStatus.Text = result.Settings.Memory.Enabled
            ? "Memory is on and saved: Martlet will remember and recall lasting facts."
            : "Memory is off and saved: your facts stay on this PC; turn memory on to review or delete them.";
        RenderResolvedDirectory();
        RenderActions();
        if (result.Settings.Memory.Enabled && !closed)
            await RefreshFactsAsync();
        return true;
    }

    private void Configuration_Changed(object sender, RoutedEventArgs e)
    {
        if (rendering)
            return;
        InvalidateDraftPresentation();
        RenderResolvedDirectory();
        RenderActions();
        // Turning memory on or off, or choosing where it is stored, saves at once.
        if (CustomChoice.IsChecked != true || !string.IsNullOrWhiteSpace(CustomDirectory.Text))
            autoSave.SaveNowAsync().Forget();
        else
            ConfigurationStatus.Text = "Choose a folder for memory (Browse), or use the Martlet folder.";
    }

    private void ConfigurationText_Changed(object sender, TextChangedEventArgs e)
    {
        if (rendering)
            return;
        InvalidateDraftPresentation();
        RenderResolvedDirectory();
        RenderActions();
    }

    // A typed folder saves when you leave the field or press Enter, not while it is half typed.
    private void CustomDirectory_LostFocus(object sender, System.Windows.Input.KeyboardFocusChangedEventArgs e)
    {
        if (!rendering && !ConfigurationMatchesPersisted()) autoSave.SaveNowAsync().Forget();
    }

    private void CustomDirectory_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter && !ConfigurationMatchesPersisted()) autoSave.SaveNowAsync().Forget();
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
        if (string.IsNullOrWhiteSpace(selected))
            return;
        CustomDirectory.Text = selected;
        autoSave.SaveNowAsync().Forget();
    }

    /// <summary>Memory is on with the settings shown here saved, so its facts can be read and changed.</summary>
    private bool CurrentEnabledConfiguration() =>
        ConfigurationMatchesPersisted() &&
        loadedSettings?.Memory is { Enabled: true } memory &&
        memory.ConfigurationRevision == configurationRevision;

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
        if (CurrentEnabledConfiguration())
            return true;
        status.Text = loadedSettings?.Memory is { Enabled: false } && ConfigurationMatchesPersisted()
            ? "Turn memory on to add or change facts."
            : "Your memory settings aren't saved yet; finish the folder (press Enter) or wait a moment, then try again.";
        return false;
    }

    private void InvalidateDraftPresentation()
    {
        if (ConfigurationMatchesPersisted())
            return;
        ClearFacts();
        ResetEditor();
        DisposeExportPreview();
        FactStatus.Text = factsLine = "Memory settings changed; facts show again once they are saved.";
    }

    private static string? PickDirectory()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Choose a local memory directory",
            Multiselect = false
        };
        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }
}
