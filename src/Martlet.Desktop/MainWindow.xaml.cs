using System.Windows;
using System.ComponentModel;
using System.Windows.Threading;
using Martlet.Core.Settings;
using Martlet.Diagnostics;
using Martlet.Audio;
using Martlet.Audio.Windows;
using Martlet.Sessions;

namespace Martlet.Desktop;

public partial class MainWindow : Window
{
    private readonly SettingsStore? store;
    private readonly string? startupError;
    private readonly DiagnosticStatusModel? model;
    private readonly DispatcherTimer ageTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly CancellationTokenSource lifetime = new();
    private readonly FixtureSession fixture = new(new PcmPlaybackSink(new WasapiDeviceFactory()),
        pacing: TimeSpan.FromMilliseconds(200));
    private readonly DispatcherTimer fixtureTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private FixtureSessionSnapshot? lastFixture;
    private bool runningFixture;
    private bool saving;
    private bool closing;
    private bool mayClose;

    public MainWindow(SettingsStore? store, string? startupError)
    {
        InitializeComponent();
        this.store = store;
        this.startupError = startupError;
        ScenarioChoice.ItemsSource = FixtureSession.Scenarios;
        ScenarioChoice.SelectedIndex = 0;
        FixtureText.Text = "FIXTURE - NOT AI. Choose a scenario; no fixture or audio has run.";
        fixtureTimer.Tick += (_, _) => ObserveFixture();
        fixtureTimer.Start();
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
            DemoButton.IsEnabled = ToneButton.IsEnabled = ScenarioChoice.IsEnabled = false;
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
        ActivityText.Text = runningFixture ? "Offline fixture active. Stop fixture is available. No real provider or microphone is active." : model.Activity;
        CreateButton.IsEnabled = !saving && !runningFixture && model.CanCreateProfile;
        RefreshButton.IsEnabled = !saving && !runningFixture && model.CanRefresh;
        StopButton.IsEnabled = !saving && model.IsRunning;
        DemoButton.IsEnabled = ToneButton.IsEnabled = !saving && !runningFixture && !model.IsRunning;
        ScenarioChoice.IsEnabled = !runningFixture;
        FixtureStopButton.IsEnabled = runningFixture;
        FixtureText.Text = model.FixtureText;
    }

    private void ObserveFixture()
    {
        if (closing || fixture.Snapshot is not { } current || current == lastFixture)
            return;
        lastFixture = current;
        if (model is not null)
            model.ObserveFixture(current);
        else
            FixtureText.Text = ReportFormatter.Human(FixtureDiagnostics.Report(current));
    }

    private async void Demo_Click(object sender, RoutedEventArgs e) =>
        await RunFixtureAsync((string)ScenarioChoice.SelectedItem, tone: false);

    private async void Tone_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this,
            "Play a 200 ms synthetic tone, NOT speech, after a completed offline fixture? Check the current Windows output, volume and audience first. The default is fixed at start, with no fallback. This permission applies only to this action and is not saved.",
            "Explicit output permission", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes)
            await RunFixtureAsync("complete", tone: true);
    }

    private async Task RunFixtureAsync(string scenario, bool tone)
    {
        if (closing || saving || runningFixture || model?.IsRunning == true)
            return;
        runningFixture = true;
        Render();
        try
        {
            await fixture.RunAsync(scenario, tone ? new(OutputPolicy.DefaultAtStart) : null, lifetime.Token);
            ObserveFixture();
        }
        finally
        {
            runningFixture = false;
            if (!closing)
                Render();
        }
    }

    private async void FixtureStop_Click(object sender, RoutedEventArgs e)
    {
        await fixture.StopAsync();
        ObserveFixture();
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
        fixtureTimer.Stop();
        lifetime.Cancel();
        IsEnabled = false;
        if (model is not null)
            await model.CloseAsync();
        await fixture.DisposeAsync();
        // WPF OnMainWindowClose exits the process, including any non-cooperative in-process callback.
        // Even absent or synchronous cleanup must leave WPF's original Closing event before closing again.
        await Dispatcher.InvokeAsync(() =>
        {
            mayClose = true;
            Close();
        });
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => Close();
}
