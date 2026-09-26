using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;
using Martlet.Core.Voices;
using Microsoft.Win32;

namespace Martlet.Desktop;

public partial class VoiceLibraryWindow : ThemedWindow
{
    private readonly VoiceLibrary library;
    private readonly SetupOperationRunner operations;
    private readonly Func<string?> chooseFile;
    private SetupOperation? operation;
    private string? source;
    private bool closed;
    private bool busy;
    internal Action<SetupOperation>? OperationStarted { get; init; }

    public VoiceLibraryWindow(VoiceLibrary library, SetupOperationRunner operations)
        : this(library, operations, null) { }

    internal VoiceLibraryWindow(VoiceLibrary library, SetupOperationRunner operations, Func<string?>? chooseFile)
    {
        InitializeComponent();
        this.library = library;
        this.operations = operations;
        this.chooseFile = chooseFile ?? ChooseFile;
        PurposeChoice.ItemsSource = new[]
        {
            new PurposeItem(VoiceAssetPurpose.Reference, "Reference clip (no training)"),
            new PurposeItem(VoiceAssetPurpose.TrainingMaterial, "Training material (preparation only)")
        };
        PurposeChoice.SelectedIndex = 0;
        RightsChoice.ItemsSource = new[]
        {
            new RightsItem(VoiceRightsBasis.OwnVoice, "This is my own voice"),
            new RightsItem(VoiceRightsBasis.ExplicitPermission, "I have explicit permission from the speaker")
        };
        EngineChoice.ItemsSource = VoiceEngineCatalog.All;
        EngineChoice.SelectedIndex = 0;
        if (operations.IsRunning)
            ResultText.Text = "Another setup/audio/conversation operation owns resources. You can inspect engine requirements; retry local actions after it finishes.";
    }

    private string? ChooseFile()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select authorized voice audio (local WAV only)",
            Filter = "PCM WAV audio (*.wav)|*.wav",
            CheckFileExists = true,
            Multiselect = false
        };
        return dialog.ShowDialog(this) == true ? dialog.FileName : null;
    }

    private void Engine_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (EngineChoice.SelectedItem is not VoiceEngineInfo engine) return;
        EngineDetails.Text = $"{engine.Name} / {engine.Candidate}\n{engine.License}\n{engine.ReferenceGuidance}\n{engine.TrainingGuidance}\n{engine.SetupGuidance}\n{engine.ExecutionStatus}";
        DescribeAsset();
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        if (busy || closed) return;
        var selected = chooseFile();
        if (selected is null) return;
        source = selected;
        SourceText.Text = selected;
        RightsConfirmed.IsChecked = false;
    }

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        if (busy || closed) return;
        if (source is null || EngineChoice.SelectedItem is not VoiceEngineInfo engine ||
            PurposeChoice.SelectedItem is not PurposeItem purpose || RightsChoice.SelectedItem is not RightsItem rights ||
            RightsConfirmed.IsChecked != true)
        {
            ResultText.Text = "Choose a source, engine, purpose and rights basis, then confirm local storage.";
            return;
        }
        var request = new VoiceImportRequest(NameInput.Text, source, TranscriptInput.Text,
            engine.Id, purpose.Id, rights.Id, true);
        RightsConfirmed.IsChecked = false;
        VoiceAsset? imported = null;
        await RunAsync(async token => { imported = await library.ImportAsync(request, token); }, () =>
        {
            AssetChoice.ItemsSource = new[] { imported! };
            AssetChoice.SelectedIndex = 0;
            ResultText.Text = "Local copy saved. Only the newly imported asset is shown; Reload lists all copies. No upload, training, model loading or speech took place.";
        });
    }

    private async void Reload_Click(object sender, RoutedEventArgs e)
    {
        IReadOnlyList<VoiceAsset>? assets = null;
        await RunAsync(async token => { assets = await library.ListAsync(token); }, () =>
        {
            AssetChoice.ItemsSource = assets;
            AssetChoice.SelectedIndex = assets!.Count > 0 ? 0 : -1;
            ResultText.Text = $"{assets.Count} saved local asset(s) inspected. Engine execution and conversation selection are unchanged.";
        });
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (busy || closed || AssetChoice.SelectedItem is not VoiceAsset selected) return;
        if (!ConfirmationDialog.Confirm(this,
            "Remove the selected imported audio and transcript from this library? The original file is preserved. This removes only the local copy, not any future worker copies or checkpoints.",
            "Remove imported voice copy")) return;
        await RunAsync(token => library.DeleteAsync(selected, token), () =>
        {
            AssetChoice.ItemsSource = null;
            DescribeAsset();
            ResultText.Text = "Selected local copy removed; source preserved. Reload to inspect remaining assets.";
        });
    }

    private async Task RunAsync(Func<CancellationToken, Task> action, Action completed)
    {
        if (busy || closed) return;
        string? failure = null;
        var next = operations.TryStart(async token =>
        {
            try
            {
                await action(token);
                return new(SetupWorkOutcome.Completed);
            }
            catch (ContractException ex) { failure = ex.Message; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                failure = "The local voice file/library could not be accessed or decoded. Check the file format, permissions, free space and whether another window owns the library. Reload before retrying; inspect any reported .pending staging files. No engine was started.";
            }
            return new(SetupWorkOutcome.Failed);
        });
        if (next is null)
        {
            ResultText.Text = "Another setup/audio/conversation operation owns resources. Wait for its actual cleanup, then retry.";
            return;
        }
        operation = next;
        OperationStarted?.Invoke(next);
        busy = true;
        ImportPanel.IsEnabled = ReloadButton.IsEnabled = AssetChoice.IsEnabled = DeleteButton.IsEnabled = false;
        CancelButton.IsEnabled = true;
        ResultText.Text = "Local file operation in progress. Cancel or close requests cancellation; ownership is retained until file cleanup finishes.";
        var result = await next.Completion;
        if (closed) return;
        operation = null;
        busy = false;
        ImportPanel.IsEnabled = ReloadButton.IsEnabled = AssetChoice.IsEnabled = true;
        CancelButton.IsEnabled = false;
        DeleteButton.IsEnabled = AssetChoice.SelectedItem is VoiceAsset;
        if (result.Outcome == SetupWorkOutcome.Completed) completed();
        else ResultText.Text = result.Outcome == SetupWorkOutcome.Canceled
            ? "Local operation canceled. Reload to inspect saved copies; no engine was started."
            : failure ?? "The local operation failed. Reload and inspect the library before retrying; no engine was started.";
    }

    private void Asset_Changed(object sender, SelectionChangedEventArgs e) => DescribeAsset();

    private void DescribeAsset()
    {
        if (AssetDetails is null || DeleteButton is null) return;
        DeleteButton.IsEnabled = !busy && AssetChoice.SelectedItem is VoiceAsset;
        if (AssetChoice.SelectedItem is not VoiceAsset asset)
        {
            AssetDetails.Text = "No saved asset selected.";
            return;
        }
        var engine = EngineChoice.SelectedItem as VoiceEngineInfo;
        AssetDetails.Text = $"{asset.Name} / {asset.Purpose} / saved for {VoiceEngineCatalog.Get(asset.Engine).Name}\n" +
            $"{asset.Wave.DurationMilliseconds / 1000d:F2} seconds, {asset.Wave.SampleRate} Hz, mono PCM16. Rights: {asset.Rights}; local storage only.\n" +
            $"Transcript: {asset.Transcript}\n" +
            (engine is null ? "" : VoiceEngineCatalog.DescribePreparation(engine.Id, asset.Purpose, asset.Wave));
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        operation?.RequestCancellation();
        ResultText.Text = "Cancellation requested. Waiting for local IO and cleanup; no replacement work can start yet.";
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        closed = true;
        operation?.RequestCancellation();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private sealed record PurposeItem(VoiceAssetPurpose Id, string Name);
    private sealed record RightsItem(VoiceRightsBasis Id, string Name);
}
