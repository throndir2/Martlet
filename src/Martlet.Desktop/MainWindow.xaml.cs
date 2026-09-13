using System.Windows;
using System.ComponentModel;
using System.Windows.Threading;
using Martlet.Core.Settings;
using Martlet.Diagnostics;
using Martlet.Audio;
using Martlet.Audio.Windows;
using Martlet.Sessions;
using Martlet.Credentials.Windows;

namespace Martlet.Desktop;

public partial class MainWindow : Window
{
    private readonly SettingsStore? store;
    private readonly SupportController support;
    private TroubleshootingWindow? troubleshooting;
    private readonly SetupOperationRunner setupOperations = new();
    private readonly ISetupService? setupService;
    private readonly AudioSetupService audioSetup;
    private readonly LiveConversationController? conversation;
    private readonly WindowsAudioSessionEvents audioSessionEvents = new();
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
    private SetupOperation? fixtureOperation;
    private readonly TaskCompletionSource fixtureQuarantine = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public MainWindow(SettingsStore? store, string? startupError)
    {
        InitializeComponent();
        this.store = store;
        support = new(store?.DataDirectory);
        audioSetup = new(setupOperations, new WindowsAudioDeviceCatalog(), new WasapiCaptureDeviceFactory(), new WasapiDeviceFactory());
        audioSessionEvents.LockedChanged += audioSetup.SetSessionLocked;
        var vault = new WindowsCredentialStore();
        setupService = store is null ? null : new SetupService(store, vault);
        if (setupService is not null)
        {
            conversation = new(setupOperations, setupService, vault, new WasapiCaptureDeviceFactory(), new WasapiDeviceFactory());
            audioSessionEvents.LockedChanged += conversation.SetSessionLocked;
        }
        this.startupError = startupError;
        ScenarioChoice.ItemsSource = FixtureSession.Scenarios;
        ScenarioChoice.SelectedIndex = 0;
        FixtureText.Text = "FIXTURE - NOT AI. Choose a scenario; no fixture or audio has run.";
        fixtureTimer.Tick += (_, _) => ObserveFixture();
        fixtureTimer.Start();
        DataPathText.Text = "Local settings only. Reports never include the data directory, credentials or settings contents.";
        StatusText.Text = "Foundation: loading local status. No audio or network services are active.";
        AudioStatusText.Text = AudioSetupDiagnostics.Describe(null);
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
            DemoButton.IsEnabled = ToneButton.IsEnabled = ScenarioChoice.IsEnabled = SetupButton.IsEnabled = AudioSetupButton.IsEnabled = ConversationButton.IsEnabled = false;
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
        {
            await model.RefreshAsync();
            support.ObserveReport(model.Report, record: true);
        }
    }

    private void Render()
    {
        if (closing || model is null)
            return;
        StatusText.Text = model.Text;
        support.ObserveReport(model.Report);
        if (model.FixtureReport is { } observedFixture) support.ObserveReport(observedFixture, fixture: true);
        PipelineText.Text = string.Join(Environment.NewLine, model.Pipeline.Select(node => node.Description));
        ActivityText.Text = runningFixture ? "Offline fixture active. Stop fixture is available. No real provider or microphone is active."
            : setupOperations.IsRunning ? "An app-shared setup/audio/conversation worker owns resources. Check its action window; new effects wait for actual cleanup."
            : model.Activity;
        CreateButton.IsEnabled = !saving && !runningFixture && !setupOperations.IsRunning && model.CanCreateProfile;
        SetupButton.IsEnabled = !saving && !runningFixture && !model.IsRunning;
        AudioSetupButton.IsEnabled = SetupButton.IsEnabled;
        ConversationButton.IsEnabled = SetupButton.IsEnabled;
        RefreshButton.IsEnabled = !saving && !runningFixture && model.CanRefresh;
        StopButton.IsEnabled = !saving && model.IsRunning;
        DemoButton.IsEnabled = ToneButton.IsEnabled = !saving && !runningFixture && !model.IsRunning && !setupOperations.IsRunning;
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
        if (closing || saving || runningFixture || model?.IsRunning == true || setupOperations.IsRunning)
            return;
        var reported = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = fixture;
        var quarantine = fixtureQuarantine.Task;
        fixtureOperation = setupOperations.TryStart(async token =>
        {
            try
            {
                var result = await session.RunAsync(scenario, tone ? new(OutputPolicy.DefaultAtStart) : null, token).ConfigureAwait(false);
                reported.TrySetResult();
                // Fixture's public report cannot establish a late release after a frozen failed terminal.
                // Keep the shared slot quarantined instead of allowing a second native audio owner.
                if (result.Playback is { DeviceReleased: false })
                    await quarantine.ConfigureAwait(false);
                return new SetupWorkResult(SetupWorkOutcome.Completed);
            }
            finally { reported.TrySetResult(); }
        });
        if (fixtureOperation is null) return;
        runningFixture = true;
        Render();
        try
        {
            await Task.WhenAny(reported.Task, fixtureOperation.Completion);
            if (fixture.Snapshot?.Playback is not { DeviceReleased: false })
                await fixtureOperation.Completion;
            ObserveFixture();
            if (model?.FixtureReport is { } report) support.ObserveReport(report, fixture: true, record: true);
        }
        finally
        {
            runningFixture = false;
            if (!closing)
                Render();
        }
    }

    private void FixtureStop_Click(object sender, RoutedEventArgs e)
    {
        var owned = fixtureOperation;
        owned?.RequestCancellation();
        ObserveFixture();
    }

    private async void Create_Click(object sender, RoutedEventArgs e)
    {
        if (store is null || model is null)
        {
            ActionText.Text = startupError;
            return;
        }
        if (saving || closing || setupOperations.IsRunning || !model.CanCreateProfile)
            return;
        saving = true;
        Render();
        try
        {
            var backend = store;
            var initial = AppSettings.CreateUnconfigured();
            var worker = setupOperations.TryStart(async token => new(SetupWorkOutcome.Completed,
                Saved: new(await backend.SaveAsync(initial, expectedRevision: null, token).ConfigureAwait(false), initial)));
            if (worker is null) return;
            if (await Task.WhenAny(worker.Completion, Task.Delay(TimeSpan.FromSeconds(5), lifetime.Token)) != worker.Completion)
            {
                worker.RequestCancellation();
                if (!closing) ActionText.Text = "Save observation ended. Actual work may still own the app slot; refresh after release. No rollback is claimed.";
                return;
            }
            var completed = await worker.Completion;
            var result = completed.Saved?.Save;
            if (!closing)
                ActionText.Text = result is null ? "Profile save failed or was canceled. Refresh local status."
                    : result.Saved
                    ? "Unconfigured profile saved. Nothing was connected or enabled."
                    : $"{result.Error!.Summary} Next action ({result.Error.ActionId}): {DiagnosticCatalog.Remedy(result.Error.ActionId).Guidance}";
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        finally { saving = false; }
        if (!closing)
            await RefreshAsync();
    }

    private async void Setup_Click(object sender, RoutedEventArgs e)
    {
        if (store is null || closing || saving || runningFixture || model?.IsRunning == true) return;
        new SetupWindow(setupService!, setupOperations) { Owner = this, Troubleshooting = OpenTroubleshooting }.ShowDialog();
        await RefreshAsync();
    }

    private async void AudioSetup_Click(object sender, RoutedEventArgs e)
    {
        if (store is null || closing || saving || runningFixture || model?.IsRunning == true) return;
        new AudioSetupWindow(setupService!, setupOperations, audioSetup,
            observe: text => { if (!closing) AudioStatusText.Text = text; }, sessionEvents: audioSessionEvents)
            { Owner = this, Troubleshooting = OpenTroubleshooting }.ShowDialog();
        await RefreshAsync();
    }

    private async void Conversation_Click(object sender, RoutedEventArgs e)
    {
        if (conversation is null || closing || saving || runningFixture || model?.IsRunning == true) return;
        new LiveConversationWindow(setupService!, setupOperations, conversation, audioSessionEvents, audioSetup)
            { Owner = this, Troubleshooting = OpenTroubleshooting, Support = support }.ShowDialog();
        await RefreshAsync();
    }

    private void Troubleshooting_Click(object sender, RoutedEventArgs e) => OpenTroubleshooting(this);
    private void OpenTroubleshooting(Window owner)
    {
        if (troubleshooting is not null) { troubleshooting.Activate(); return; }
        troubleshooting = new(support, RefreshAsync) { Owner = owner };
        troubleshooting.Closed += (_, _) => troubleshooting = null;
        // Nonmodal: explicit local recording can observe actions in the existing setup/live surfaces.
        troubleshooting.Show();
    }

    private void Stop_Click(object sender, RoutedEventArgs e) => model?.Stop();

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (mayClose)
            return;
        e.Cancel = true;
        if (closing)
            return;
        if (support.HasResources)
        {
            support.CancelAndClose();
            ActionText.Text = "Exit is waiting for owned support IO/cancellation/cleanup. Keep Martlet open; use Troubleshooting to retry cleanup, then Exit again. No rollback or completed cleanup is assumed.";
            return;
        }
        closing = true;
        ageTimer.Stop();
        fixtureTimer.Stop();
        audioSessionEvents.LockedChanged -= audioSetup.SetSessionLocked;
        if (conversation is not null) audioSessionEvents.LockedChanged -= conversation.SetSessionLocked;
        audioSessionEvents.Dispose();
        lifetime.Cancel();
        fixtureOperation?.RequestCancellation();
        setupOperations.RequestCancellation();
        IsEnabled = false;
        if (model is not null)
            await model.CloseAsync();
        await Task.Run(async () => await fixture.DisposeAsync());
        if (conversation is not null) await Task.Run(async () => await conversation.DisposeAsync());
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
