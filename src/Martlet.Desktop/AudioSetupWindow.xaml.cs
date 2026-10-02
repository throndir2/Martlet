using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;
using Martlet.Diagnostics;

namespace Martlet.Desktop;

/// <summary>Microphone and speakers: pick a device, run a quick local test, and the choice saves as you go.
/// Plain-language state is shown on each device card; exact technical evidence stays under Details.</summary>
public partial class AudioSetupWindow : ThemedWindow
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
    private AudioSettings? persisted;
    private string? revision;
    private AudioSetupOperation? ownedAudio;
    private SetupOperation? ownedSettings;
    private CancellationTokenSource? observation;
    private AudioDeviceList? devices;
    private AudioTestStatus? lastStatus;
    private long generation;
    private bool closed, busy, rendering, needsReload = true, confirming, loading, saving, saveQueued, discoveryQueued;
    private volatile bool locked;
    private Guid? confirmableOutput;
    private string message = "";
    private string? reloadHint;
    private string? micResult, outputResult;
    private bool micProblem;
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
        timer.Tick += (_, _) =>
        {
            RenderProgress();
            RenderActions();
            if (saveQueued && !busy && !operations.IsRunning && !locked) _ = SaveQueuedAsync();
            else if (discoveryQueued) _ = DiscoverQueuedAsync();
        };
        timer.Start();
        this.sessionEvents.LockedChanged += SessionSwitch;
        RenderStatus();
        RenderActions();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e) => await LoadAsync();
    private async void Reload_Click(object sender, RoutedEventArgs e) => await LoadAsync();

    private void Say(string text)
    {
        message = text;
        if (!closed) RenderActions();
    }

    private bool MayStart()
    {
        if (closed) return false;
        if (!busy && !operations.IsRunning && !locked) return true;
        ResultText.Text = "An earlier setup/fixture action still owns its worker, or this session is locked. No overlapping action was started. Wait for actual release.";
        Say(locked ? "Tests are paused while Windows is locked." : "Another setup task is still finishing. Try again in a moment.");
        return false;
    }

    private async Task LoadAsync()
    {
        if (!MayStart()) return;
        var backend = settings;
        ownedSettings = operations.TryStart(async token => new(SetupWorkOutcome.Completed, Loaded: await backend.LoadAsync(token).ConfigureAwait(false)));
        if (ownedSettings is null) return;
        loading = true;
        message = "";
        SetupWorkResult? result;
        try { result = await ObserveAsync(ownedSettings, TimeSpan.FromSeconds(5)); }
        finally { loading = false; }
        if (result?.Loaded is not { } loaded)
        {
            if (!closed) reloadHint = "Couldn't load your settings. Click Reload to try again.";
            return;
        }
        draft = loaded.Error is null ? SetupSettings.Begin(loaded.Settings) : null;
        persisted = loaded.Settings?.Audio;
        revision = loaded.Revision;
        needsReload = loaded.Error is not null;
        reloadHint = loaded.Error is { } error ? $"Couldn't load your settings: {error.Summary} Click Reload to try again." : null;
        saveQueued = false;
        confirmableOutput = null;
        devices = null;
        micResult = outputResult = null;
        MicMeter.Value = 0;
        DevicesText.Text = "Looking for microphones and speakers...";
        if (draft is not null) draft = draft with { Audio = draft.Audio ?? AudioSettings.Create() };
        ResultText.Text = loaded.Error?.Summary ??
            "Loaded local settings. Devices are listed automatically; tests have NOT run. Choices save automatically when you change or test them; existing v1 settings migrate with an exact original snapshot.";
        message = "";
        RenderChoices();
        RenderStatus();
        RenderActions();
        if (draft is not null && !needsReload) RequestDiscovery();
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
        var items = new List<SelectionItem> { new(null, defaultChoice.DisplayName, input ? "Windows default microphone" : "Windows default speakers") };
        if (endpoints is not null)
            items.AddRange(endpoints.Select(item => new SelectionItem(item.EndpointId, item.DisplayName, item.DisplayName)));
        if (selected.EndpointId is not null && items.All(item => item.Id != selected.EndpointId))
            items.Add(new(selected.EndpointId, selected.DisplayName, selected.DisplayName));
        box.ItemsSource = items;
        box.SelectedItem = items.First(item => item.Id == selected.EndpointId);
    }

    // Label is the stored display name; Shown is what the list displays.
    private sealed record SelectionItem(string? Id, string Label, string Shown)
    {
        public override string ToString() => Shown;
    }

    private void Choice_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (rendering || draft?.Audio is not { } choices) return;
        if (Testing || locked || needsReload || loading) { RenderChoices(); return; }
        if (InputChoice.SelectedItem is not SelectionItem input || OutputChoice.SelectedItem is not SelectionItem output) return;
        var next = choices with { Input = choices.Input.Select(input.Id, input.Label), Output = choices.Output.Select(output.Id, output.Label) };
        if (next == choices) return;
        if (next.Input != choices.Input) { micResult = null; MicMeter.Value = 0; }
        if (next.Output != choices.Output) { confirmableOutput = null; outputResult = null; }
        draft = draft with { Audio = next };
        ResultText.Text = "Choice changed. Its corresponding checkpoint was invalidated and the new choice saves automatically. Testing still needs fresh permission.";
        message = "Changed. Test it any time to check that it works.";
        QueueSave();
        RenderStatus();
        RenderActions();
    }

    private bool Discovering => ownedAudio?.Status.Action == AudioSetupAction.Discovery;
    private bool Testing => ownedAudio is not null && !Discovering;

    // Opening either list looks again, so a device plugged in since the window opened shows up without a button.
    private void Choice_DropDownOpened(object? sender, EventArgs e)
    {
        if (!Discovering) RequestDiscovery();
    }

    // Listing only reads endpoint names, so it needs no permission. It shares the setup worker, so it waits its turn.
    private void RequestDiscovery()
    {
        discoveryQueued = true;
        _ = DiscoverQueuedAsync();
    }

    private async Task DiscoverQueuedAsync()
    {
        if (!discoveryQueued || closed || busy || saveQueued || operations.IsRunning || locked || needsReload || loading || draft is null) return;
        discoveryQueued = false;
        await RunAudioAsync(AudioSetupAction.Discovery);
    }

    private async void Mic_Click(object sender, RoutedEventArgs e) => await RunAudioAsync(AudioSetupAction.Microphone);
    private async void Output_Click(object sender, RoutedEventArgs e) => await RunAudioAsync(AudioSetupAction.Output);

    private bool Confirm(string question, string title)
    {
        confirming = true;
        try
        {
            return confirm?.Invoke(question) ?? ConfirmationDialog.Confirm(this, question, title);
        }
        finally { confirming = false; }
    }

    private static string Named(AudioChoice choice, bool input) => choice.EndpointId is null
        ? input ? "your Windows default microphone" : "your Windows default speakers"
        : $"\"{choice.DisplayName}\"";

    private async Task RunAudioAsync(AudioSetupAction action)
    {
        if (!MayStart() || needsReload || draft?.Audio is not { } choices) return;
        var input = action == AudioSetupAction.Microphone;
        var choice = input ? choices.Input : choices.Output;
        if (action != AudioSetupAction.Discovery && !Confirm(input
                ? $"Test {Named(choice, true)}?{Environment.NewLine}{Environment.NewLine}Martlet listens for up to 5 seconds to check that sound comes through, so talk or hum while it runs. Nothing is recorded, saved or sent, and leaving this window stops the test."
                : $"Play a short, quiet beep on {Named(choice, false)}?{Environment.NewLine}{Environment.NewLine}Check your volume first. Martlet doesn't change your volume or Windows defaults.",
            input ? "Test microphone" : "Play test sound"))
        {
            ResultText.Text = "Permission declined. No endpoint was opened and no audio test ran.";
            Say("Test canceled. Nothing was opened.");
            return;
        }
        if (!MayStart()) return;
        if (input)
        {
            LevelText.Text = "No PCM received in this test.";
            micResult = null;
            MicMeter.Value = 0;
        }
        if (action == AudioSetupAction.Output)
        {
            confirmableOutput = null;
            outputResult = null;
        }
        var operation = audio.Start(action, choice, explicitlyApproved: true);
        if (operation is null)
        {
            if (action == AudioSetupAction.Discovery) discoveryQueued = true;
            else MayStart();
            return;
        }
        ownedAudio = operation;
        lastStatus = null;
        if (action != AudioSetupAction.Discovery) message = "";
        var result = await ObserveAsync(operation.Worker, action == AudioSetupAction.Discovery ? TimeSpan.FromSeconds(5) : observationTimeout);
        if (result is null) return;
        RenderProgress();
        var status = operation.Status;
        var passed = status.Succeeded && result.Outcome == SetupWorkOutcome.Completed;
        if (action == AudioSetupAction.Discovery)
        {
            if (passed)
            {
                var found = operation.Devices!;
                if (!found.SameAs(devices))
                {
                    devices = found;
                    RenderChoices();
                }
                ResultText.Text = $"Found {found.Inputs.Count} input(s) and {found.Outputs.Count} output(s). Listing is a snapshot, NOT permission or readiness; opening a device list refreshes it. " +
                    (found.Inputs.Count == 0 || found.Outputs.Count == 0 ? "A device category is missing. Reconnect or enable the intended endpoint manually. " : "") +
                    "Saved endpoints absent from this list remain unverified; no fallback occurs.";
                DevicesText.Text = Found(found);
            }
            else
            {
                ResultText.Text = "Local action failed or canceled: " + status.Stage + ". " + AudioSetupDiagnostics.Remedy(status.Error);
                if (devices is null) DevicesText.Text = "Couldn't list devices. Martlet uses your Windows defaults; open a list to look again.";
                if (result.Outcome != SetupWorkOutcome.Canceled) message = "Couldn't list devices. " + Problem(status.Error, input: true);
            }
        }
        else if (result.Outcome == SetupWorkOutcome.Canceled)
        {
            ResultText.Text = "Local action failed or canceled: " + status.Stage + ". " + AudioSetupDiagnostics.Remedy(status.Error);
            message = "Test stopped. Nothing changed.";
        }
        else
        {
            // A finished test replaces this choice's earlier result: a pass records it, anything else clears it.
            var checkpoint = passed ? status.Checkpoint : null;
            if (draft?.Audio is { } current && (input ? current.Input : current.Output).ConfigurationRevision == choice.ConfigurationRevision)
                draft = draft with { Audio = input
                    ? current with { Input = current.Input with { Checkpoint = checkpoint } }
                    : current with { Output = current.Output with { Checkpoint = checkpoint } } };
            if (checkpoint is not null)
            {
                if (input) { micResult = "Working. Martlet picked up sound from this microphone."; micProblem = false; }
                else confirmableOutput = checkpoint.ConfigurationRevision;
                ResultText.Text = status.Stage + ". Saved automatically as a historical checkpoint, not current readiness.";
            }
            else if (input && status.Signal is { } signal and (AudioInputSignal.NoFrames or AudioInputSignal.BelowAdvisoryThreshold
                or AudioInputSignal.InsufficientFrames or AudioInputSignal.IntermittentAmplitude))
            {
                ResultText.Text = $"Local microphone test did not meet the level advisory: {status.Stage}. " + InputSignalRemedy(signal);
                micResult = SignalProblem(signal);
                micProblem = true;
            }
            else
            {
                ResultText.Text = "Local action failed or canceled: " + status.Stage + ". " + AudioSetupDiagnostics.Remedy(status.Error);
                if (input) { micResult = Problem(status.Error, input: true); micProblem = true; }
                else outputResult = "The test sound didn't play. " + Problem(status.Error, input: false);
            }
            ownedAudio = null;
            QueueSave();
        }
        ownedAudio = null;
        RenderStatus();
        RenderActions();
    }

    private static string Found(AudioDeviceList list)
    {
        static string Count(int count, string one, string many) => count == 1 ? "1 " + one : $"{count} {many}";
        const string remedy = " Plug it in or turn it on in Windows Sound settings; the list checks again when you open it.";
        var inputs = Count(list.Inputs.Count, "microphone", "microphones");
        var outputs = Count(list.Outputs.Count, "speaker or headset", "speakers and headsets");
        return list.Inputs.Count == 0 && list.Outputs.Count == 0 ? "No microphones or speakers found." + remedy
            : list.Inputs.Count == 0 ? $"Found {outputs}, but no microphone." + remedy
            : list.Outputs.Count == 0 ? $"Found {inputs}, but no speakers or headset." + remedy
            : $"Found {inputs} and {outputs}. Martlet uses your Windows defaults unless you pick another below.";
    }

    private static string SignalProblem(AudioInputSignal signal) => signal switch
    {
        AudioInputSignal.NoFrames => "No sound came through. Check that the microphone is plugged in and that Windows lets desktop apps use it (Settings > Privacy & security > Microphone).",
        AudioInputSignal.BelowAdvisoryThreshold => "Too quiet. Check that the microphone isn't muted and its Windows input level is up, then talk during the test.",
        AudioInputSignal.InsufficientFrames => "The microphone stopped sending sound partway through. Check its connection, then test again.",
        AudioInputSignal.IntermittentAmplitude => "Only a short sound came through. Keep talking or humming for the whole test, then try again.",
        _ => throw new ArgumentOutOfRangeException(nameof(signal))
    };

    private static string Problem(ErrorCode? code, bool input) => code switch
    {
        ErrorCode.AudioAccessDenied => "Windows is blocking the microphone. Open Settings > Privacy & security > Microphone and let desktop apps use it.",
        ErrorCode.AudioDeviceBusy => "Another app is using this device on its own. Close that app, then try again.",
        ErrorCode.AudioFormatUnsupported => "This device uses an audio format Martlet can't open. Pick another device.",
        ErrorCode.AudioDeviceUnavailable or ErrorCode.AudioDeviceLost or ErrorCode.AudioDeviceChanged =>
            "The device was unplugged or changed. Reconnect it, then pick it again from the list.",
        ErrorCode.DeadlineExceeded => "The test took too long. Try again.",
        ErrorCode.AudioCaptureFailed or ErrorCode.AudioPlaybackFailed => "The device stopped responding. If tests stay unavailable, restart Martlet.",
        _ => input ? "The test didn't finish. Check the microphone, then try again." : "Check that your speakers or headset are connected and turned up, then try again."
    };

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
                var timedOut = !stop.IsCancellationRequested;
                worker.RequestCancellation();
                if (ReferenceEquals(worker, ownedSettings))
                {
                    needsReload = true;
                    reloadHint = "Saving or loading didn't finish. Click Reload to check your choices.";
                }
                else if (timedOut) message = "The device didn't respond in time. Wait a moment, then try again.";
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
                reloadHint = "Saving or loading didn't finish. Click Reload to check your choices.";
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

    private static double Meter(double rms) => Math.Clamp((20 * Math.Log10(Math.Max(rms, 1e-6)) + 60) / 50, 0, 1);

    private void RenderProgress()
    {
        if (closed || observation?.IsCancellationRequested == true ||
            ownedAudio is not { } operation || operation.Status == lastStatus) return;
        var status = operation.Status;
        lastStatus = status;
        var remedy = status.Signal is AudioInputSignal.NoFrames or AudioInputSignal.BelowAdvisoryThreshold
            or AudioInputSignal.InsufficientFrames or AudioInputSignal.IntermittentAmplitude
            ? InputSignalRemedy(status.Signal.Value) : AudioSetupDiagnostics.Remedy(status.Error);
        var stage = $"LOCAL {status.Action}: {status.Stage}; samples {status.Samples}; " +
            $"finished {status.Finished}; actual release {status.Released}; error {status.Error?.ToString() ?? "none"}. " +
            remedy;
        switch (status.Action)
        {
            case AudioSetupAction.Discovery: discoveryStage = stage; break;
            case AudioSetupAction.Microphone: microphoneStage = stage; break;
            case AudioSetupAction.Output: outputStage = stage; break;
        }
        stages = string.Join(Environment.NewLine, discoveryStage, microphoneStage, outputStage);
        if (status.Action == AudioSetupAction.Microphone)
        {
            if (status.Signal == AudioInputSignal.NoFrames)
            {
                LevelText.Text = "No PCM frames received in this test. No amplitude result or checkpoint.";
                MicMeter.Value = 0;
            }
            else if (status.Peak is { } peak && status.Rms is { } rms)
            {
                LevelText.Text = $"{(status.Finished ? "Whole-test" : "Live")} selected PCM: peak {peak:F6}; RMS {rms:F6}; canonical samples {status.Samples}. " +
                    (status.Signal == AudioInputSignal.BelowAdvisoryThreshold ? "Below 1% full-scale RMS advisory threshold; no checkpoint. " :
                    status.Signal is AudioInputSignal.InsufficientFrames or AudioInputSignal.IntermittentAmplitude ? "Insufficient frame or level coverage; no checkpoint. " :
                    status.Signal == AudioInputSignal.DetectableAmplitude ? "Met 1% RMS, 4 seconds PCM and 1.25 seconds above-threshold level advisory. " : "") +
                    (status.SamplesAtOrAboveThreshold is { } count ? $"Samples at/above 1% full-scale: {count}. " : "") +
                    "Amplitude only, NOT VAD, speech detection or audio quality.";
                MicMeter.Value = Meter(rms);
            }
            else if (status.Finished)
            {
                LevelText.Text = "Capture failed or canceled; earlier live level is not a valid test result. No checkpoint.";
                MicMeter.Value = 0;
            }
        }
        RenderStatus();
    }

    private static string InputSignalRemedy(AudioInputSignal signal) => signal switch
    {
        AudioInputSignal.NoFrames =>
            "Check the selected microphone or changed default, its connection and Windows microphone privacy/desktop-app access. Reopen the device list to refresh it, then authorize a fresh test; no automatic fallback.",
        AudioInputSignal.BelowAdvisoryThreshold =>
            "A quiet or brief valid phrase can fall below this local advisory; low PCM does not prove mute or absent speech. Check the intended microphone, hardware mute, Windows input level and microphone privacy/desktop-app access; authorize a fresh test.",
        AudioInputSignal.InsufficientFrames =>
            "The selected input did not supply enough frames. Check its connection, changed default/endpoint and Windows microphone privacy/desktop-app access; reopen the device list and authorize a fresh test, with no fallback.",
        AudioInputSignal.IntermittentAmplitude =>
            "A brief click is not a reliable level check. Check the intended input, hardware mute and Windows input level; try a sustained test sound, then authorize a fresh test. This does not detect speech.",
        _ => throw new ArgumentOutOfRangeException(nameof(signal))
    };

    private void RenderStatus()
    {
        StatusText.Text = AudioSetupDiagnostics.Describe(AudioSetupStatus.From(draft?.Audio), stages);
        observe?.Invoke(StatusText.Text);
    }

    private void RenderActions()
    {
        if (closed) return;
        var available = !busy && !operations.IsRunning && !locked;
        var ready = draft is not null && !needsReload;
        var testing = Testing;
        var discovering = Discovering;
        // Picking stays open while a choice saves or the list refreshes, so changing both devices in a row never bounces back.
        InputChoice.IsEnabled = OutputChoice.IsEnabled =
            ready && !locked && !testing && !loading && (!operations.IsRunning || saving || discovering);
        MicButton.IsEnabled = OutputButton.IsEnabled = available && ready;
        ReloadButton.IsEnabled = available;
        ReloadButton.Visibility = needsReload && !loading && !busy ? Visibility.Visible : Visibility.Collapsed;
        StopButton.IsEnabled = testing;
        StopButton.Visibility = testing ? Visibility.Visible : Visibility.Collapsed;
        var confirmable = confirmableOutput is { } id && draft?.Audio?.Output.ConfigurationRevision == id;
        HeardButton.IsEnabled = available && confirmable;
        HeardButton.Visibility = confirmable ? Visibility.Visible : Visibility.Collapsed;
        ActivityText.Text = operations.IsRunning
            ? "An app-shared setup/fixture worker is active or quarantined. Stop and Close stay responsive. Timeout is NOT ownership release."
            : locked ? "Session locked. Tests stay off; unlock and explicitly start a fresh action."
            : "Idle. No listening or playback is armed. Saved settings never authorize a test.";
        MessageText.Text = locked ? "Tests are paused while Windows is locked."
            : needsReload && reloadHint is not null ? reloadHint
            : operations.IsRunning && !busy && !saving ? "Another setup task is still finishing. Tests come back when it's done."
            : message;
        RenderDevices(confirmable);
    }

    private void RenderDevices(bool confirmable)
    {
        var choices = draft?.Audio;
        var running = ownedAudio?.Status.Action;
        // Windows defaults are assumed to work; only a device that is missing right now needs attention.
        var checking = devices is null && (Discovering || discoveryQueued);

        if (running == AudioSetupAction.Microphone)
            ShowState(MicBadge, "Testing", "AccentBrush", MicText, "Listening... talk or hum until the test ends.");
        else if (micResult is not null)
            ShowState(MicBadge, micProblem ? "Needs attention" : "Working", micProblem ? "WarningBrush" : "SuccessBrush", MicText, micResult);
        else if (choices is not null && AudioDeviceList.Present(choices.Input, devices?.Inputs) == false)
            ShowState(MicBadge, "Not found", "WarningBrush", MicText, choices.Input.EndpointId is null
                ? "No microphone found. Plug one in or turn it on in Windows Sound settings, then open the list above. You can still type."
                : $"{Named(choices.Input, true)} isn't connected. Plug it in, or pick another microphone above.");
        else if (choices?.Input.Checkpoint is { } heard)
            ShowState(MicBadge, "Working", "SuccessBrush", MicText, $"Working. Last tested {heard.TestedAt.ToLocalTime():d}.");
        else if (checking)
            ShowState(MicBadge, "Checking", null, MicText, "Looking for your microphone...");
        else
            ShowState(MicBadge, "Ready", "SuccessBrush", MicText,
                $"Using {Named(choices?.Input ?? AudioChoice.Default(true), true)}. Test it any time to check that Martlet hears you.");

        if (running == AudioSetupAction.Output)
            ShowState(OutputBadge, "Testing", "AccentBrush", OutputText, "Playing a short beep...");
        else if (confirmable)
            ShowState(OutputBadge, "Did you hear it?", "AccentBrush", OutputText,
                "Did you hear a short beep? If not, check your volume and the device picked above, then play it again.");
        else if (outputResult is not null)
            ShowState(OutputBadge, "Needs attention", "WarningBrush", OutputText, outputResult);
        else if (choices is not null && AudioDeviceList.Present(choices.Output, devices?.Outputs) == false)
            ShowState(OutputBadge, "Not found", "WarningBrush", OutputText, choices.Output.EndpointId is null
                ? "No speakers or headset found. Plug them in or turn them on in Windows Sound settings, then open the list above."
                : $"{Named(choices.Output, false)} isn't connected. Plug it in, or pick other speakers above.");
        else if (choices?.Output.Checkpoint is { Outcome: LocalAudioOutcome.Heard } played)
            ShowState(OutputBadge, "Working", "SuccessBrush", OutputText, $"Working. You heard the test sound on {played.TestedAt.ToLocalTime():d}.");
        else if (checking)
            ShowState(OutputBadge, "Checking", null, OutputText, "Looking for your speakers...");
        else
            ShowState(OutputBadge, "Ready", "SuccessBrush", OutputText,
                $"Using {Named(choices?.Output ?? AudioChoice.Default(false), false)}. Play a test sound any time to check that you can hear Martlet.");
    }

    private static void ShowState(TextBlock badge, string state, string? brush, TextBlock detail, string text)
    {
        badge.Text = state;
        badge.SetResourceReference(TextBlock.ForegroundProperty, brush ?? "MutedBrush");
        detail.Text = text;
    }

    private void Heard_Click(object sender, RoutedEventArgs e)
    {
        if (!MayStart() || draft?.Audio is not { } choices || confirmableOutput != choices.Output.ConfigurationRevision ||
            choices.Output.Checkpoint is not { Outcome: LocalAudioOutcome.ToneDrained } checkpoint) return;
        draft = draft with { Audio = choices with { Output = choices.Output with { Checkpoint = checkpoint with { Outcome = LocalAudioOutcome.Heard } } } };
        confirmableOutput = null;
        outputResult = null;
        ResultText.Text = "You confirmed hearing THIS test on THIS selected configuration. This is a historical human report, not evergreen readiness. It saves automatically.";
        message = "";
        QueueSave();
        RenderStatus();
        RenderActions();
    }

    private void QueueSave()
    {
        if (draft?.Audio is null || draft.Audio == persisted) return;
        saveQueued = true;
        _ = SaveQueuedAsync();
    }

    // Choices and test results save as they change. Saves run one at a time on the shared worker; a change made
    // while one is saving waits for the next pass. The timer retries a save that another worker blocked.
    private async Task SaveQueuedAsync()
    {
        if (saving) return;
        saving = true;
        try
        {
            while (saveQueued && !closed && !needsReload && draft is not null && !busy && !operations.IsRunning && !locked)
            {
                saveQueued = false;
                if (draft.Audio == persisted) continue;
                var snapshot = draft;
                var expected = revision;
                var backend = settings;
                var worker = operations.TryStart(async token => new(SetupWorkOutcome.Completed,
                    Saved: await backend.SaveAsync(snapshot, expected, token).ConfigureAwait(false)));
                if (worker is null) { saveQueued = true; return; }
                ownedSettings = worker;
                var result = await ObserveAsync(worker, TimeSpan.FromSeconds(5));
                if (result?.Saved is not { } saved) return;
                if (!saved.Save.Saved)
                {
                    needsReload = true;
                    reloadHint = "Couldn't save because your settings changed in another window. Click Reload, then pick again.";
                    ResultText.Text = saved.Summary;
                    return;
                }
                revision = saved.Save.Revision;
                persisted = saved.Settings.Audio;
                if (ReferenceEquals(draft, snapshot)) draft = saved.Settings;
                ResultText.Text = string.IsNullOrEmpty(ResultText.Text) ? saved.Summary : ResultText.Text + Environment.NewLine + saved.Summary;
            }
        }
        finally
        {
            saving = false;
            if (!closed) { RenderStatus(); RenderActions(); }
        }
    }

    private void StopOwned()
    {
        // Saves are short and atomic, so stopping, deactivating and closing end only the owned test.
        confirmableOutput = null;
        if (ownedAudio is null) return;
        if (Discovering) discoveryQueued = devices is null;
        else message = "Test stopped. Nothing changed.";
        ownedAudio.Stop();
        observation?.Cancel();
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
                MicMeter.Value = 0;
                break;
            case AudioSetupAction.Output: outputStage = stage; break;
        }
        stages = string.Join(Environment.NewLine, discoveryStage, microphoneStage, outputStage);
        RenderStatus();
    }

    private void Stop_Click(object sender, RoutedEventArgs e) => StopOwned();

    private async void Close_Click(object sender, RoutedEventArgs e)
    {
        // Let a just-made choice finish saving before the window goes away.
        DoneButton.IsEnabled = false;
        for (var i = 0; i < 50 && !closed && (saving || saveQueued && !needsReload && !locked); i++)
        {
            if (!saving) await SaveQueuedAsync();
            if (saving || saveQueued) await Task.Delay(100);
        }
        if (!closed) Close();
    }

    private void Window_Deactivated(object? sender, EventArgs e)
    {
        if (confirming) return;
        // Listing devices opens nothing, so switching away lets it finish; it still ends a pending "did you hear it?".
        if (Discovering) confirmableOutput = null;
        else StopOwned();
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
            else if (discoveryQueued) _ = DiscoverQueuedAsync();
            RenderActions();
        });
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        StopOwned();
        // Start a pending save before closing; the shared worker finishes it after the window is gone.
        if (saveQueued && !saving) _ = SaveQueuedAsync();
        closed = true;
        ++generation;
        timer.Stop();
        sessionEvents.LockedChanged -= SessionSwitch;
        if (ownsSessionEvents) sessionEvents.Dispose();
    }
}
