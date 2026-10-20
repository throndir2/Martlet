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

/// <summary>The Memory window's export: the frozen preview, the destination, its consent and writing the file on this PC.</summary>
public partial class MemoryWindow
{
    private async void CreateExportPreview_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireCurrentEnabledConfiguration(ExportStatus))
            return;
        MemoryExportPreview? preview = null;
        await RunAsync(async token =>
            preview = await service.CreateExportPreviewAsync(configurationRevision, token).ConfigureAwait(false),
            "Couldn't prepare the export, so no file was created.");
        if (closed || preview is null)
            return;
        DisposeExportPreview();
        exportPreview = preview;
        ExportPreviewText.Text = Encoding.UTF8.GetString(preview.Preview());
        ExportSummary.Text = preview.FactCount == 1 ? "Preview ready: 1 fact." : $"Preview ready: {preview.FactCount} facts.";
        rendering = true;
        AcceptExport.IsChecked = false;
        rendering = false;
        ExportStatus.Text = "Review the preview; nothing has been exported yet.";
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
                "Couldn't create the export, so no file was created.");
        }
        catch (Exception error) when (error is MemoryException or ArgumentException or NotSupportedException)
        {
            ExportStatus.Text = Describe(error);
        }
        if (closed || receipt is null)
            return;
        ExportStatus.Text = "Memory export created on this PC; it was not uploaded.";
        DisposeExportPreview();
        RenderActions();
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
}
