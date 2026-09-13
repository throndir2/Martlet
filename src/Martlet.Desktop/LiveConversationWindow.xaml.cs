using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;
using Martlet.Diagnostics;
using Martlet.Providers;

namespace Martlet.Desktop;

public partial class LiveConversationWindow : Window
{
    private readonly ISetupService settings;
    private readonly SetupOperationRunner operations;
    private readonly LiveConversationController controller;
    private readonly AudioSetupService? audio;
    private readonly IAudioSessionEvents sessionEvents;
    private readonly TimeProvider clock;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private SetupOperation? loading;
    private CancellationTokenSource? observation;
    private LiveConversationOperation? owned;
    private bool closed, rendering, ready, mouseHeld, keyHeld;
    private volatile bool locked;
    private long generation;

    internal LiveConversationWindow(ISetupService settings, SetupOperationRunner operations, LiveConversationController controller,
        IAudioSessionEvents sessionEvents, AudioSetupService? audio = null, TimeProvider? clock = null)
    {
        this.settings = settings;
        this.operations = operations;
        this.controller = controller;
        this.sessionEvents = sessionEvents;
        this.audio = audio;
        this.clock = clock ?? TimeProvider.System;
        InitializeComponent();
        var controls = controller.Controls;
        locked = controls.Locked;
        PauseChoice.IsChecked = controls.Paused;
        MuteChoice.IsChecked = controls.Muted;
        timer.Tick += (_, _) => { Observe(); RenderActions(); };
        timer.Start();
        sessionEvents.LockedChanged += SessionSwitch;
        StatusText.Text = "REAL API mode; NOT RUN. Mic/STT/policy/LLM/TTS/playback: not run. No effects authorized.";
        RenderConfiguration();
        RenderActions();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e) => await LoadAsync();
    private async void Reload_Click(object sender, RoutedEventArgs e) => await LoadAsync();

    private async Task LoadAsync()
    {
        if (closed || operations.IsRunning) { ResultText.Text = Remedy("conversation.ownership_busy"); return; }
        ClearPermission();
        ready = false;
        var backend = settings;
        var worker = operations.TryStart(async token => new(SetupWorkOutcome.Completed, Loaded: await backend.LoadAsync(token).ConfigureAwait(false)));
        if (worker is null) return;
        loading = worker;
        long current = ++generation;
        using var stop = new CancellationTokenSource();
        observation = stop;
        RenderActions();
        var completed = await Task.WhenAny(worker.Completion, Task.Delay(TimeSpan.FromSeconds(5), clock, stop.Token));
        if (closed || generation != current) return;
        observation = null;
        loading = null;
        if (completed != worker.Completion || stop.IsCancellationRequested)
        {
            worker.RequestCancellation();
            ResultText.Text = "Settings observation ended; actual work may still own the app slot. Reload after release. No provider/device action started.";
        }
        else
        {
            var result = await worker.Completion;
            if (result.Loaded is { } loaded)
            {
                controller.Configure(loaded);
                ready = loaded.Error is null;
                ResultText.Text = loaded.Error?.Summary ?? "Choices loaded only. Accept the displayed envelope for each new Send/PTT. Account/key/model/device validity remains UNKNOWN.";
                if (owned is null && ready)
                    StatusText.Text = "REAL API mode; NOT RUN. Mic/STT/policy/LLM/TTS/playback: not run. No effects authorized. Choices loaded.";
            }
            else ResultText.Text = "Settings load failed or was canceled. Reload; no configuration was replaced.";
        }
        RenderConfiguration();
        RenderActions();
    }

    private void RenderConfiguration()
    {
        var selected = controller.Configuration;
        bool voice = VoiceChoice.IsChecked == true;
        ConfigurationText.Text = (selected is null ? "Select and save a Named OpenAI API profile in Setup / resume. Live actions unavailable."
            : $"Typed: {selected.Unavailable(voice, false) ?? "configured, NOT live verified"}\nPTT: {selected.Unavailable(voice, true) ?? "configured, NOT live verified"}") +
            "\nSupported LLM: " + string.Join(", ", OpenAiTextGenerationCatalog.SupportedModelIds) +
            "\nSupported STT: " + string.Join(", ", OpenAiTranscriptionCatalog.SupportedModelIds) +
            "\nSupported TTS: " + string.Join(", ", OpenAiSpeechSynthesisCatalog.SupportedModelIds) +
            "; voices: " + string.Join(", ", OpenAiSpeechSynthesisCatalog.SupportedVoices) + ". No model discovery or fallback.";
        EnvelopeText.Text = selected?.Disclosure(voice) ??
            "No active supported API configuration. Capture/upload/LLM/TTS permission is OFF. Use Setup / resume; the offline fixture remains available without credentials.";
    }

    private void RenderActions()
    {
        if (closed || SendButton is null) return;
        bool ownRunning = owned is { OwnershipReleased: false };
        bool available = ready && !locked && PauseChoice.IsChecked != true && MuteChoice.IsChecked != true &&
            !operations.IsRunning && controller.Configuration is not null;
        bool accepted = AcceptAction.IsChecked == true;
        bool voice = VoiceChoice.IsChecked == true;
        SendButton.IsEnabled = available && accepted && !string.IsNullOrWhiteSpace(InputText.Text) &&
            controller.Configuration!.Unavailable(voice, false) is null;
        // Keep a held control enabled until release; disabling it would lose capture and cancel.
        PttButton.IsEnabled = mouseHeld || keyHeld || available && accepted && AcceptCapture.IsChecked == true &&
            AcceptUpload.IsChecked == true && controller.Configuration!.Unavailable(voice, true) is null;
        ReleaseButton.IsEnabled = ownRunning && owned!.Authorization.Microphone && owned.Turn is null && owned.Transcription is null && !owned.Status.Finished;
        StopButton.IsEnabled = ownRunning || loading is not null;
        ReloadButton.IsEnabled = !operations.IsRunning;
        SetupButton.IsEnabled = AudioButton.IsEnabled = !operations.IsRunning;
        AudioButton.IsEnabled &= audio is not null;
    }

    private void ClearPermission()
    {
        rendering = true;
        AcceptAction.IsChecked = AcceptCapture.IsChecked = AcceptUpload.IsChecked = false;
        rendering = false;
    }

    private bool Start(bool microphone)
    {
        if (closed || !ready || locked) return false;
        try
        {
            var next = controller.Start(microphone ? null : InputText.Text, VoiceChoice.IsChecked == true, microphone,
                AcceptAction.IsChecked == true, AcceptCapture.IsChecked == true, AcceptUpload.IsChecked == true);
            owned = next;
            ClearPermission();
            AnswerText.Clear();
            RefusalText.Clear();
            TranscriptText.Clear();
            ResultText.Text = "Explicit action accepted. Stop revokes it; local cleanup may outlive reporting. Cost UNKNOWN.";
            Observe();
            RenderActions();
            return true;
        }
        catch (LiveActionException error) { ResultText.Text = Remedy(error.Code); }
        catch (ContractException) { ResultText.Text = "Invalid or oversized input. Use at most 4096 valid Unicode characters / 16,384 UTF-8 bytes."; }
        return false;
    }

    private void Send_Click(object sender, RoutedEventArgs e) => Start(false);
    private void Ptt_Click(object sender, RoutedEventArgs e) => Start(true);
    private void Ptt_Down(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        mouseHeld = true;
        if (!Start(true)) { mouseHeld = false; RenderActions(); return; }
        PttButton.IsEnabled = true;
        if (!PttButton.CaptureMouse()) Cancel("conversation.focus_lost");
    }
    private void Ptt_Up(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (mouseHeld) Release();
    }
    private void Ptt_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Space) return;
        e.Handled = true;
        if (!e.IsRepeat && !keyHeld)
        {
            keyHeld = true;
            if (!Start(true)) { keyHeld = false; RenderActions(); }
        }
    }
    private void Ptt_KeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Space) return;
        e.Handled = true;
        if (keyHeld) Release();
    }
    private void Ptt_LostCapture(object sender, MouseEventArgs e) { if (mouseHeld) Cancel("conversation.focus_lost"); }
    private void Ptt_LostFocus(object sender, KeyboardFocusChangedEventArgs e) { if (keyHeld) Cancel("conversation.focus_lost"); }
    private void Release_Click(object sender, RoutedEventArgs e) => Release();
    private void Release()
    {
        mouseHeld = keyHeld = false;
        PttButton.ReleaseMouseCapture();
        owned?.ReleasePress();
        RenderActions();
    }
    private void Stop_Click(object sender, RoutedEventArgs e) => Cancel("conversation.canceled");
    private void Cancel(string reason)
    {
        mouseHeld = keyHeld = false;
        PttButton.ReleaseMouseCapture();
        ClearPermission();
        if (owned is not null) controller.Stop(owned, reason);
        loading?.RequestCancellation();
        observation?.Cancel();
        Observe();
        RenderActions();
    }

    private void Output_Changed(object sender, RoutedEventArgs e)
    {
        if (controller is null || AcceptAction is null) return;
        Cancel("conversation.output_changed");
        RenderConfiguration();
    }
    private void Consent_Changed(object sender, RoutedEventArgs e)
    {
        if (rendering || controller is null || AcceptAction is null) return;
        if (owned is { OwnershipReleased: false } && (AcceptAction.IsChecked != true ||
            owned.Authorization.Microphone && (AcceptCapture.IsChecked != true || AcceptUpload.IsChecked != true)))
            controller.Stop(owned, "conversation.revoked");
        RenderActions();
    }
    private void Input_Changed(object sender, System.Windows.Controls.TextChangedEventArgs e) => RenderActions();
    private void Controls_Changed(object sender, RoutedEventArgs e)
    {
        if (controller is null || PauseChoice is null || MuteChoice is null) return;
        ClearPermission();
        controller.SetControls(PauseChoice.IsChecked == true, MuteChoice.IsChecked == true, locked);
        RenderActions();
    }

    private void SessionSwitch(bool value)
    {
        locked = value;
        // Revoke immediately on the OS signal, not after a potentially busy dispatcher.
        if (value) controller.Revoke("conversation.locked");
        Dispatcher.BeginInvoke(() =>
        {
            if (closed) return;
            ClearPermission();
            controller.SetControls(PauseChoice.IsChecked == true, MuteChoice.IsChecked == true, locked);
            RenderActions();
        });
    }
    private void Window_Deactivated(object? sender, EventArgs e) { if (!closed) Cancel("conversation.deactivated"); }
    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        Cancel("conversation.closed");
        closed = true;
        generation++;
        timer.Stop();
        sessionEvents.LockedChanged -= SessionSwitch;
    }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private async void Setup_Click(object sender, RoutedEventArgs e)
    {
        if (operations.IsRunning) return;
        Cancel("conversation.configuration_changed");
        ready = false;
        new SetupWindow(settings, operations) { Owner = this }.ShowDialog();
        await LoadAsync();
    }
    private async void Audio_Click(object sender, RoutedEventArgs e)
    {
        if (operations.IsRunning || audio is null) return;
        Cancel("conversation.configuration_changed");
        ready = false;
        new AudioSetupWindow(settings, operations, audio, sessionEvents: sessionEvents) { Owner = this }.ShowDialog();
        await LoadAsync();
    }

    private void Observe()
    {
        if (closed || owned is not { } operation) return;
        var snapshot = operation.Turn?.Snapshot;
        var content = operation.Turn?.Content;
        string evidence = snapshot?.TextProvenance == EvidenceProvenance.Live ? "REAL provider response (not account/device qualification)"
            : snapshot?.TextProvenance == EvidenceProvenance.Fixture ? "FIXTURE HTTP evidence - NOT real inference" : "No provider response";
        AnswerText.Text = $"{evidence}\n{content?.Text}";
        RefusalText.Text = content?.Refusal ?? "";
        if (operation.Transcription is { } stt)
            TranscriptText.Text = $"{(stt.Provenance == EvidenceProvenance.Live ? "REAL STT" : "FIXTURE STT - NOT inference")}: {stt.Outcome}; confidence UNKNOWN\n{operation.Transcript}";
        var capture = operation.Capture?.Snapshot;
        var status = operation.Status;
        StatusText.Text = $"{status.Code}; app worker released: {operation.OwnershipReleased}; quarantine: {status.Quarantined}.\n" +
            $"Mic: {capture?.State.ToString() ?? "not used"}; samples: {capture?.CanonicalSamples ?? 0}; retained PCM: {capture?.RetainedPcmBytes ?? 0}. VAD/wake/unsolicited: OFF.\n" +
            $"STT: {operation.Transcription?.Outcome.ToString() ?? "not completed / not used"}; Policy: {status.Policy?.ToString() ?? "see timeline"}; LLM/TTS: {snapshot?.State.ToString() ?? "not dispatched"}.\n" +
            $"Reserved requests: {operation.Authorization.ReservedRequests}; segments: {snapshot?.CommittedSegments ?? 0}; queued: {snapshot?.QueuedSegments ?? 0}; suppressed fragments: {snapshot?.SuppressedFragments ?? 0}.\n" +
            $"Playback accepted/submitted/device-consumed: {snapshot?.AcceptedSamples ?? 0}/{snapshot?.SubmittedSamples ?? 0}/{snapshot?.DeviceConsumedSamples ?? 0}; drain: {snapshot?.Playback?.DeviceDrainObserved ?? false}; may have played: {snapshot?.MayHavePlayed ?? false}. NOT proof of heard audio; cost UNKNOWN.\n" +
            $"Runtime failure: {snapshot?.Failure}; provider: {snapshot?.ProviderFailure ?? status.ProviderFailure}; audio: {snapshot?.Playback?.Error?.Code ?? status.AudioFailure}.\n" + operation.Timeline;
        if (operation.Authorization.CredentialFailure is { } credential) ResultText.Text = CredentialMessages.Describe(credential);
        else if (status.AudioFailure is { } error) ResultText.Text = AudioSetupDiagnostics.Remedy(error) + " Typed fallback remains available.";
        else if ((snapshot?.ProviderFailure ?? status.ProviderFailure) is { } provider) ResultText.Text = ProviderRemedy(provider);
        else if (status.Finished) ResultText.Text = Remedy(status.Code);
        else if (operation.OwnershipReleased) ResultText.Text = "The action failed at a dependency boundary. No automatic retry; review settings and authorize a fresh action.";
    }

    internal static string ProviderRemedy(ProviderFailureCode code) => code switch
    {
        ProviderFailureCode.Authentication or ProviderFailureCode.PermissionDenied or ProviderFailureCode.CredentialUnavailable =>
            $"{code}: review the selected role credential in Setup and account/model permissions. Local key readability is not API validity. Do not elevate or share a key.",
        ProviderFailureCode.QuotaExceeded => "QuotaExceeded: the provider reported an account/billing limit. Review the account's current usage and spending settings; no retry or zero-cost assumption.",
        ProviderFailureCode.RateLimited => "RateLimited: review provider request limits before a NEW deliberate action. No automatic retry; the earlier request may have cost money.",
        ProviderFailureCode.ModelUnsupported or ProviderFailureCode.ModelNotFound or ProviderFailureCode.VoiceUnsupported =>
            $"{code}: review the displayed exact supported model/voice IDs and your account's access. No model discovery or fallback was attempted.",
        ProviderFailureCode.ConsentExpired or ProviderFailureCode.DeadlineExceeded or ProviderFailureCode.FirstDeltaTimeout or
            ProviderFailureCode.FirstAudioTimeout or ProviderFailureCode.IdleTimeout =>
            $"{code}: the original request/permission window expired. Wait for actual cleanup, then explicitly authorize a NEW action. Earlier audio or charges may remain.",
        ProviderFailureCode.Network or ProviderFailureCode.Server =>
            $"{code}: check your connection and provider status. No automatic retry. Partial response text remains visible; select text-only if voice is unavailable.",
        ProviderFailureCode.OriginRejected or ProviderFailureCode.CredentialBindingMismatch or ProviderFailureCode.RedirectRejected =>
            $"{code}: the origin/credential boundary was rejected. Use only the named OpenAI route; never redirect a key or disable TLS/protection.",
        _ => $"{code}: the provider result exceeded bounds or did not match the supported request/response contract. Preserve the stage code, review the selected route and use a fresh explicit action only after cleanup; no hidden continuation."
    };

    internal static string Remedy(string code) => code switch
    {
        "conversation.permission_required" => "Permission missing. Review the displayed envelope; PTT additionally needs separate local-capture and STT-upload permission.",
        "conversation.ownership_busy" => "An app operation still owns resources or cleanup. No queue or replacement started. Stop that action and wait for actual release; close Martlet if native cleanup remains stuck.",
        "conversation.setup_required" or "conversation.configuration_unsupported" => "Review the supported named routes, credential references, destination choices and selected audio policy in Setup. No provider was contacted by this rejection.",
        "conversation.configuration_changed" => "Configuration changed during the action. Permission revoked; Reload and review the new role/model/voice/key/output before a fresh action.",
        "conversation.expired" => "The original permission window expired. No permission was renewed. Discard old input/capture and explicitly authorize a new action.",
        "stt.deadline_exceeded" => "The original STT deadline expired. Upload/credential work may still own resources; no LLM follows. Wait for actual cleanup, then use typed fallback or authorize a fresh recording.",
        "stt.NoSpeech" => "STT returned no speech. No LLM or TTS request followed. Try a fresh PTT action or use typed input.",
        "runtime.Completed" => "Response completed. Device consumption/drain is not audibility. Any retry is a new potentially paid action.",
        "runtime.Refused" => "The provider refused. Refusal is shown separately, not routed as ordinary speech. Earlier partial response text can remain visible.",
        "runtime.Partial" or "runtime.Failed" => "The turn failed or is partial; response text remains visible. Check the stage/failure below, selected model/account limits and output. No automatic retry; earlier speech may have played.",
        "conversation.cleanup_quarantined" or "mic.cleanup_quarantined" => "Cleanup is unproven; the app-wide ownership slot is quarantined. No replacement work is permitted. Close Martlet and review the device/session before a fresh launch.",
        _ => $"{code}. No automatic continuation. Review the stage, use typed/text-only fallback if audio failed, or explicitly authorize a fresh action after actual cleanup."
    };
}
