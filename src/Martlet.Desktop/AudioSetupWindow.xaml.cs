using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Martlet.Core.Settings;
using Martlet.Diagnostics;

namespace Martlet.Desktop;

public partial class AudioSetupWindow : Window
{
    internal Action<Window>? Troubleshooting { get; init; }
    private void Troubleshooting_Click(object sender, RoutedEventArgs e) => Troubleshooting?.Invoke(this);
    private readonly ISetupService settings;
    private readonly SetupOperationRunner operations;
    private readonly AudioSetupService audio;
    private readonly Func<string, bool>? confirm;
    private readonly TimeProvider clock;
    private readonly TimeSpan observationTimeout;
    private readonly Action<string>? observe;
    private readonly IAudioSessionEvents sessionEvents;
    private readonly bool ownsSessionEvents;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private AppSettings? draft;
    private string? revision;
    private AudioSetupOperation? ownedAudio;
    private SetupOperation? ownedSettings;
    private CancellationTokenSource? observation;
    private AudioDeviceList? devices;
    private AudioTestStatus? lastStatus;
    private long generation;
    private bool closed, busy, rendering, needsReload = true, confirming;
    private volatile bool locked;
    private Guid? confirmableOutput;
    private string stages = "Mic: never tested. Output: never tested.";
    private string discoveryStage = "Discovery: not run.";
    private string microphoneStage = "Mic: never tested.";
    private string outputStage = "Output: never tested.";

    public AudioSetupWindow(ISetupService settings, SetupOperationRunner operations, AudioSetupService audio,
        Func<string, bool>? confirm = null, TimeProvider? clock = null, TimeSpan? observationTimeout = null,
        Action<string>? observe = null, IAudioSessionEvents? sessionEvents = null)
    {
        this.settings = settings;
        this.operations = operations;
        this.audio = audio;
        this.confirm = confirm;
        this.clock = clock ?? TimeProvider.System;
        this.observationTimeout = observationTimeout ?? TimeSpan.FromSeconds(9);
        this.observe = observe;
        if (this.observationTimeout <= TimeSpan.Zero || this.observationTimeout > TimeSpan.FromSeconds(10))
            throw new ArgumentOutOfRangeException(nameof(observationTimeout));
        InitializeComponent();
        this.sessionEvents = sessionEvents ?? new WindowsAudioSessionEvents();
        ownsSessionEvents = sessionEvents is null;
        locked = audio.IsSessionLocked;
        timer.Tick += (_, _) => { RenderProgress(); RenderActions(); };
        timer.Start();
        this.sessionEvents.LockedChanged += SessionSwitch;
        RenderStatus();
        RenderActions();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e) => await LoadAsync();
    private async void Reload_Click(object sender, RoutedEventArgs e) => await LoadAsync();

    private bool MayStart()
    {
        if (closed) return false;
        if (!busy && !operations.IsRunning && !locked) return true;
        ResultText.Text = "An earlier setup/fixture action still owns its worker, or this session is locked. No overlapping action was started. Wait for actual release.";
        return false;
    }

    private async Task LoadAsync()
    {
        if (!MayStart()) return;
        var backend = settings;
        ownedSettings = operations.TryStart(async token => new(SetupWorkOutcome.Completed, Loaded: await backend.LoadAsync(token).ConfigureAwait(false)));
        if (ownedSettings is null) return;
        var result = await ObserveAsync(ownedSettings, TimeSpan.FromSeconds(5));
        if (result?.Loaded is not { } loaded) return;
        draft = loaded.Error is null ? SetupSettings.Begin(loaded.Settings) : null;
        revision = loaded.Revision;
        needsReload = loaded.Error is not null;
        confirmableOutput = null;
        devices = null;
        if (draft is not null) draft = draft with { Audio = draft.Audio ?? AudioSettings.Create() };
        ResultText.Text = loaded.Error?.Summary ??
            "Loaded local settings only. Find devices and tests have NOT run. Save is explicit; existing v1 settings migrate with an exact original snapshot.";
        RenderChoices();
        RenderStatus();
        RenderActions();
    }

    private void RenderChoices()
    {
        if (draft?.Audio is not { } choices) return;
        rendering = true;
        Fill(InputChoice, choices.Input, true, devices?.Inputs);
        Fill(OutputChoice, choices.Output, false, devices?.Outputs);
        rendering = false;
    }

    private static void Fill(ComboBox box, AudioChoice selected, bool input, IReadOnlyList<AudioEndpoint>? endpoints)
    {
        var defaultChoice = AudioChoice.Default(input);
        var items = new List<SelectionItem> { new(null, defaultChoice.DisplayName) };
        if (endpoints is not null)
            items.AddRange(endpoints.Select(item => new SelectionItem(item.EndpointId, item.DisplayName)));
        if (selected.EndpointId is not null && items.All(item => item.Id != selected.EndpointId))
            items.Add(new(selected.EndpointId, selected.DisplayName));
        box.ItemsSource = items;
        box.SelectedItem = items.First(item => item.Id == selected.EndpointId);
    }

    private sealed record SelectionItem(string? Id, string Label)
    {
        public override string ToString() => Label;
    }

    private void Choice_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (rendering || draft?.Audio is not { } choices) return;
        if (busy || operations.IsRunning) { RenderChoices(); return; }
        var input = (SelectionItem)InputChoice.SelectedItem;
        var output = (SelectionItem)OutputChoice.SelectedItem;
        var next = choices with { Input = choices.Input.Select(input.Id, input.Label), Output = choices.Output.Select(output.Id, output.Label) };
        if (next.Output != choices.Output) confirmableOutput = null;
        draft = draft with { Audio = next };
        ResultText.Text = "Choice changed in memory. Its corresponding checkpoint was invalidated; Save explicitly to persist. Testing still needs fresh permission.";
        RenderStatus();
        RenderActions();
    }

    private async void Find_Click(object sender, RoutedEventArgs e) => await RunAudioAsync(AudioSetupAction.Discovery);
    private async void Mic_Click(object sender, RoutedEventArgs e) => await RunAudioAsync(AudioSetupAction.Microphone);
    private async void Output_Click(object sender, RoutedEventArgs e) => await RunAudioAsync(AudioSetupAction.Output);

    private bool Confirm(string message)
    {
        confirming = true;
        try
        {
            return confirm?.Invoke(message) ?? MessageBox.Show(this, message, "One local action only",
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;
        }
        finally { confirming = false; }
    }

    private async Task RunAudioAsync(AudioSetupAction action)
    {
        if (!MayStart() || needsReload || draft?.Audio is not { } choices) return;
        var choice = action == AudioSetupAction.Microphone ? choices.Input : choices.Output;
        if (action != AudioSetupAction.Discovery && !Confirm(action == AudioSetupAction.Microphone
            ? $"Capture from '{choice.DisplayName}' locally for at most 5 seconds? Only actual amplitude is shown, not speech detection. All PCM is discarded. Nothing is saved or uploaded. Stop, Pause, leaving this window or locking the session cancels. Permission expires within 20 seconds and is not saved."
            : $"Play a quiet 200 ms synthetic tone, NOT speech, on '{choice.DisplayName}'? Check your volume and audience first. Output binds once with no fallback. This is not an audibility pass; separately confirm 'I heard it'. Permission is for this action only."))
        {
            ResultText.Text = "Permission declined. No endpoint was opened and no audio test ran.";
            return;
        }
        if (!MayStart()) return;
        if (action == AudioSetupAction.Microphone)
        {
            draft = draft with { Audio = choices with { Input = choices.Input with { Checkpoint = null } } };
            LevelText.Text = "No PCM received in this test.";
        }
        if (action == AudioSetupAction.Output)
        {
            confirmableOutput = null;
            draft = draft with { Audio = choices with { Output = choices.Output with { Checkpoint = null } } };
        }
        var operation = audio.Start(action, choice, explicitlyApproved: true);
        if (operation is null) { MayStart(); return; }
        ownedAudio = operation;
        lastStatus = null;
        var result = await ObserveAsync(operation.Worker, action == AudioSetupAction.Discovery ? TimeSpan.FromSeconds(5) : observationTimeout);
        if (result is null) return;
        RenderProgress();
        if (operation.Status.Succeeded && result.Outcome == SetupWorkOutcome.Completed)
        {
            if (action == AudioSetupAction.Discovery)
            {
                devices = operation.Devices;
                RenderChoices();
                ResultText.Text = $"Found {devices!.Inputs.Count} input(s) and {devices.Outputs.Count} output(s). Listing is a snapshot, NOT permission or readiness; use Find again to refresh. " +
                    (devices.Inputs.Count == 0 || devices.Outputs.Count == 0 ? "A device category is missing. Reconnect or enable the intended endpoint manually. " : "") +
                    "Saved endpoints absent from this list remain unverified; no fallback occurs.";
            }
            else if (operation.Status.Checkpoint is { } checkpoint && draft?.Audio is { } current)
            {
                draft = draft with { Audio = action == AudioSetupAction.Microphone
                    ? current with { Input = current.Input with { Checkpoint = checkpoint } }
                    : current with { Output = current.Output with { Checkpoint = checkpoint } } };
                if (action == AudioSetupAction.Output) confirmableOutput = checkpoint.ConfigurationRevision;
                ResultText.Text = operation.Status.Stage + ". Save explicitly to keep this historical checkpoint.";
            }
        }
        else ResultText.Text = "Local action failed or canceled: " + operation.Status.Stage + ". " + AudioSetupDiagnostics.Remedy(operation.Status.Error);
        ownedAudio = null;
        RenderStatus();
        RenderActions();
    }

    private async Task<SetupWorkResult?> ObserveAsync(SetupOperation worker, TimeSpan timeout)
    {
        var current = ++generation;
        using var stop = new CancellationTokenSource();
        observation = stop;
        busy = true;
        RenderActions();
        var delay = Task.Delay(timeout, clock, stop.Token);
        try
        {
            var finished = await Task.WhenAny(worker.Completion, delay);
            if (closed || current != generation) return null;
            if (stop.IsCancellationRequested || finished != worker.Completion)
            {
                worker.RequestCancellation();
                if (ReferenceEquals(worker, ownedSettings)) needsReload = true;
                ResultText.Text = "Observation stopped or timed out; cancellation requested. Native work/callbacks may STILL OWN resources. No replacement can start until actual release; late results are discarded. Interrupted saves require Reload.";
                RetireAudioObservation();
                ownedAudio = null;
                confirmableOutput = null;
                return null;
            }
            var result = await worker.Completion;
            if (result.Outcome != SetupWorkOutcome.Completed && ReferenceEquals(worker, ownedSettings))
            {
                needsReload = true;
                ResultText.Text = "Settings action interrupted or failed. Reload and review; no rollback is claimed. Original exception details are not displayed.";
            }
            return result;
        }
        finally
        {
            stop.Cancel();
            if (ReferenceEquals(observation, stop)) observation = null;
            busy = false;
            if (ReferenceEquals(worker, ownedSettings)) ownedSettings = null;
            if (!closed) { RenderStatus(); RenderActions(); }
        }
    }

    private void RenderProgress()
    {
        if (closed || ownedAudio is not { } operation || operation.Status == lastStatus) return;
        var status = operation.Status;
        lastStatus = status;
        var stage = $"LOCAL {status.Action}: {status.Stage}; samples {status.Samples}; " +
            $"finished {status.Finished}; actual release {status.Released}; error {status.Error?.ToString() ?? "none"}. " +
            AudioSetupDiagnostics.Remedy(status.Error);
        switch (status.Action)
        {
            case AudioSetupAction.Discovery: discoveryStage = stage; break;
            case AudioSetupAction.Microphone: microphoneStage = stage; break;
            case AudioSetupAction.Output: outputStage = stage; break;
        }
        stages = string.Join(Environment.NewLine, discoveryStage, microphoneStage, outputStage);
        if (status.Peak is { } peak && status.Rms is { } rms)
            LevelText.Text = $"Actual selected PCM: peak {peak:F4}; RMS {rms:F4}; canonical samples {status.Samples}. Amplitude only, NOT VAD.";
        RenderStatus();
    }

    private void RenderStatus()
    {
        StatusText.Text = AudioSetupDiagnostics.Describe(AudioSetupStatus.From(draft?.Audio), stages);
        observe?.Invoke(StatusText.Text);
    }

    private void RenderActions()
    {
        if (closed) return;
        var available = !busy && !operations.IsRunning && !locked;
        ChoicesPanel.IsEnabled = available && !needsReload && draft is not null;
        FindButton.IsEnabled = ChoicesPanel.IsEnabled;
        ReloadButton.IsEnabled = available;
        StopButton.IsEnabled = PauseButton.IsEnabled = ownedAudio is not null || ownedSettings is not null;
        HeardButton.IsEnabled = available && confirmableOutput is { } id && draft?.Audio?.Output.ConfigurationRevision == id;
        ActivityText.Text = operations.IsRunning
            ? "An app-shared setup/fixture worker is active or quarantined. Stop and Close stay responsive. Timeout is NOT ownership release."
            : locked ? "Session locked. Tests stay off; unlock and explicitly start a fresh action."
            : "Idle. No listening or playback is armed. Saved settings never authorize a test.";
    }

    private void Heard_Click(object sender, RoutedEventArgs e)
    {
        if (!MayStart() || draft?.Audio is not { } choices || confirmableOutput != choices.Output.ConfigurationRevision ||
            choices.Output.Checkpoint is not { Outcome: LocalAudioOutcome.ToneDrained } checkpoint) return;
        draft = draft with { Audio = choices with { Output = choices.Output with { Checkpoint = checkpoint with { Outcome = LocalAudioOutcome.Heard } } } };
        confirmableOutput = null;
        ResultText.Text = "You confirmed hearing THIS test on THIS selected configuration. This is a historical human report, not evergreen readiness. Save explicitly to persist.";
        RenderStatus();
        RenderActions();
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!MayStart() || needsReload || draft is null) return;
        var snapshot = draft;
        var expected = revision;
        var backend = settings;
        ownedSettings = operations.TryStart(async token => new(SetupWorkOutcome.Completed,
            Saved: await backend.SaveAsync(snapshot, expected, token).ConfigureAwait(false)));
        if (ownedSettings is null) return;
        var result = await ObserveAsync(ownedSettings, TimeSpan.FromSeconds(5));
        if (result?.Saved is not { } saved) return;
        needsReload = !saved.Save.Saved;
        if (saved.Save.Saved) { draft = saved.Settings; revision = saved.Save.Revision; }
        ResultText.Text = saved.Summary;
        RenderStatus();
        RenderActions();
    }

    private void StopOwned()
    {
        ownedAudio?.Stop();
        ownedSettings?.RequestCancellation();
        observation?.Cancel();
        confirmableOutput = null;
        RetireAudioObservation();
    }

    private void RetireAudioObservation()
    {
        if (ownedAudio is not { } operation) return;
        var stage = $"LOCAL {operation.Status.Action}: observation retired; cancellation requested. Actual native/callback release UNVERIFIED; no replacement is armed.";
        switch (operation.Status.Action)
        {
            case AudioSetupAction.Discovery: discoveryStage = stage; break;
            case AudioSetupAction.Microphone:
                microphoneStage = stage;
                LevelText.Text = "Test observation stopped; meter is no longer live.";
                break;
            case AudioSetupAction.Output: outputStage = stage; break;
        }
        stages = string.Join(Environment.NewLine, discoveryStage, microphoneStage, outputStage);
        RenderStatus();
    }

    private void Stop_Click(object sender, RoutedEventArgs e) => StopOwned();
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void Window_Deactivated(object? sender, EventArgs e)
    {
        if (!confirming) StopOwned();
    }

    private void SessionSwitch(bool isLocked)
    {
        // Revoke the service-owned handle before dispatch, even while a new UI action publishes its handle.
        locked = isLocked;
        audio.SetSessionLocked(isLocked);
        _ = Dispatcher.BeginInvoke(() =>
        {
            if (closed) return;
            if (locked) StopOwned();
            RenderActions();
        });
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        StopOwned();
        closed = true;
        ++generation;
        timer.Stop();
        sessionEvents.LockedChanged -= SessionSwitch;
        if (ownsSessionEvents) sessionEvents.Dispose();
    }
}
