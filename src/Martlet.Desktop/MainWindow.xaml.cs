using System.Windows;
using System.ComponentModel;
using System.Windows.Threading;
using Martlet.Core.Settings;
using Martlet.Diagnostics;

namespace Martlet.Desktop;

public partial class MainWindow : Window
{
    private readonly SettingsStore? store;
    private readonly string? startupError;
    private readonly DiagnosticStatusModel? model;
    private readonly DispatcherTimer ageTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly CancellationTokenSource lifetime = new();
    private bool saving;
    private bool closing;
    private bool mayClose;

    public MainWindow(SettingsStore? store, string? startupError)
    {
        InitializeComponent();
        this.store = store;
        this.startupError = startupError;
        DataPathText.Text = "Local settings only. Reports never include the data directory, credentials or settings contents.";
        StatusText.Text = "Foundation: loading local status. No audio or network services are active.";
        if (store is not null)
        {
            model = new(new FoundationStatusService(store).Executor);
            model.PropertyChanged += (_, _) => Render();
            ageTimer.Tick += (_, _) => model.UpdateAge();
            ageTimer.Start();
        }
        else
        {
            PipelineText.Text = "Mic / VAD / STT / Policy / LLM / TTS / Playback: unavailable; not run. Correct the launch data directory first.";
        }
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e) => await RefreshAsync();
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async Task RefreshAsync()
    {
        if (model is null)
        {
            StatusText.Text = startupError;
            RefreshButton.IsEnabled = false;
            return;
        }
        if (!saving && !closing)
            await model.RefreshAsync();
    }

    private void Render()
    {
        if (closing || model is null)
            return;
        StatusText.Text = model.Text;
        PipelineText.Text = string.Join(Environment.NewLine, model.Pipeline.Select(node => node.Description));
        ActivityText.Text = model.Activity;
        CreateButton.IsEnabled = !saving && model.CanCreateProfile;
        RefreshButton.IsEnabled = !saving && model.CanRefresh;
        StopButton.IsEnabled = !saving && model.IsRunning;
    }

    private async void Create_Click(object sender, RoutedEventArgs e)
    {
        if (store is null || model is null)
        {
            ActionText.Text = startupError;
            return;
        }
        if (saving || closing || !model.CanCreateProfile)
            return;
        saving = true;
        Render();
        try
        {
            var result = await Task.Run(() => store.SaveAsync(AppSettings.CreateUnconfigured(), expectedRevision: null, lifetime.Token));
            if (!closing)
                ActionText.Text = result.Saved
                    ? "Unconfigured profile saved. Nothing was connected or enabled."
                    : $"{result.Error!.Summary} Next action ({result.Error.ActionId}): {DiagnosticCatalog.Remedy(result.Error.ActionId).Guidance}";
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        finally { saving = false; }
        if (!closing)
            await RefreshAsync();
    }

    private void Stop_Click(object sender, RoutedEventArgs e) => model?.Stop();

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (mayClose)
            return;
        e.Cancel = true;
        if (closing)
            return;
        closing = true;
        ageTimer.Stop();
        lifetime.Cancel();
        IsEnabled = false;
        if (model is not null)
            await model.CloseAsync();
        // WPF OnMainWindowClose exits the process, including any non-cooperative in-process callback.
        // No worker process or device operation is launched by this build.
        mayClose = true;
        Close();
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => Close();
}
