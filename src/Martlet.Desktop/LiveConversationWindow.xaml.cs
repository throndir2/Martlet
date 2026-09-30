using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Martlet.Audio;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;
using Martlet.Diagnostics;
using Martlet.Providers;

namespace Martlet.Desktop;

public partial class LiveConversationWindow : ThemedWindow
{
    internal Action<Window>? Troubleshooting { get; init; }
    internal Action<Window>? ConfigurationRecovery { get; init; }
    internal Action<Window>? Avatar { get; init; }
    private void Avatar_Click(object sender, RoutedEventArgs e) => Avatar?.Invoke(this);
    internal SupportController? Support { get; init; }
    private readonly LiveSupportProjection supportProjection = new();
    private void Troubleshooting_Click(object sender, RoutedEventArgs e) => Troubleshooting?.Invoke(this);
    private readonly ISetupService settings;
    private readonly SetupOperationRunner operations;
    private readonly LiveConversationController controller;
    private readonly AudioSetupService? audio;
    private readonly IAudioSessionEvents sessionEvents;
    private readonly TimeProvider clock;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly VoiceIdentity? voiceIdentity;
    private readonly IScreenGlancer glancer;
    private SetupOperation? loading;
    private CancellationTokenSource? observation;
    private LiveConversationOperation? owned;
    private bool closed, rendering, ready, mouseHeld, keyHeld, listening, applyingPreferences;
    private LiveConversationOperation? handledListen;
    private string? listenNote;
    // Screen watching: separate from the user's own turns (owned), so a glance never replaces the visible reply.
    private bool watching, glancing, lookWanted;
    private ScreenCommentaryPacer? pacer;
    private ScreenFrame? pendingFrame;
    private LiveConversationOperation? commentary, handledCommentary;
    private long nextGlance;
    private string? watchNote;
    private volatile bool locked;
    private long generation;

    internal LiveConversationWindow(ISetupService settings, SetupOperationRunner operations, LiveConversationController controller,
        IAudioSessionEvents sessionEvents, AudioSetupService? audio = null, TimeProvider? clock = null, VoiceIdentity? voiceIdentity = null,
        IScreenGlancer? glancer = null)
    {
        this.settings = settings;
        this.operations = operations;
        this.controller = controller;
        this.sessionEvents = sessionEvents;
        this.audio = audio;
        this.clock = clock ?? TimeProvider.System;
        this.voiceIdentity = voiceIdentity;
        this.glancer = glancer ?? new ScreenGlancer();
        InitializeComponent();
        var controls = controller.Controls;
        locked = controls.Locked;
        PauseChoice.IsChecked = controls.Paused;
        MuteChoice.IsChecked = controls.Muted;
        ApplyPreferences(TalkPreferences.Load(voiceIdentity?.DataDirectory));
        timer.Tick += (_, _) => { Observe(); Watch(); ContinueListening(); RenderActions(); };
        timer.Start();
        sessionEvents.LockedChanged += SessionSwitch;
        StatusText.Text = "REAL API mode; NOT RUN. Mic/STT/policy/LLM/TTS/playback: not run. No effects authorized.";
        RenderConfiguration();
        RenderActions();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        Motion.Breathe(TalkHeart, 3.2);
        await LoadAsync();
    }
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
        ConfigurationText.Text = (selected is null ? "Select and save a cloud API profile in Setup / resume. Live actions unavailable."
            : $"Typed: {selected.Unavailable(voice, false) ?? "configured, NOT live verified"}\nPTT: {selected.Unavailable(voice, true) ?? "configured, NOT live verified"}") +
            "\nSupported LLM: OpenAI " + string.Join(", ", OpenAiTextGenerationCatalog.SupportedModelIds) +
            "; or OpenRouter, NVIDIA Build or any OpenAI-compatible Chat Completions endpoint with your exact model ID" +
            "\nSupported STT: " + string.Join(", ", OpenAiTranscriptionCatalog.SupportedModelIds) +
            "\nSupported TTS: " + string.Join(", ", OpenAiSpeechSynthesisCatalog.SupportedModelIds) +
            "; voices: " + string.Join(", ", OpenAiSpeechSynthesisCatalog.SupportedVoices) + ". No model discovery or fallback.";
        EnvelopeText.Text = selected is null
            ? "No active supported API configuration. Capture/upload/LLM/TTS permission is OFF. Use Setup / resume; the offline fixture remains available without credentials."
            : selected.Disclosure(voice) + "\n" + selected.ScreenDisclosure(SelectedChattiness, SelectedScope);
        VisionStatus.Text = selected is null ? "Load a saved Thinking model first (Setup / resume)."
            : (selected.Vision() switch
            {
                VisionSupport.Supported => "Ready: ",
                VisionSupport.Unsupported => "Can't see yet: ",
                _ => "Not sure: "
            }) + selected.VisionAdvice();
    }

    private Chattiness SelectedChattiness => (Chattiness)Math.Clamp(ChattinessBox?.SelectedIndex ?? 1, 0, 2);
    private ScreenScope SelectedScope => (ScreenScope)Math.Clamp(ScreenScopeBox?.SelectedIndex ?? 0, 0, 1);

    private void RenderActions()
    {
        if (closed || SendButton is null || ListenButton is null || WatchButton is null || AcceptScreen is null) return;
        bool ownRunning = owned is { OwnershipReleased: false };
        bool available = ready && !locked && PauseChoice.IsChecked != true && MuteChoice.IsChecked != true &&
            !operations.IsRunning && controller.Configuration is not null;
        bool accepted = AcceptAction.IsChecked == true;
        bool voice = VoiceChoice.IsChecked == true;
        bool handsFree = VoiceActivityMode.IsChecked == true;
        bool voiceIdReady = VoiceIdChoice.IsChecked != true || voiceIdentity?.Current is not null;
        bool microphoneReady = accepted && AcceptCapture.IsChecked == true && AcceptUpload.IsChecked == true && voiceIdReady &&
            controller.Configuration?.Unavailable(voice, true) is null;
        AcceptMemory.IsEnabled = available && controller.Configuration?.Memory is { Enabled: true };
        SendButton.IsEnabled = !listening && available && accepted && !string.IsNullOrWhiteSpace(InputText.Text) &&
            controller.Configuration!.Unavailable(voice, false) is null;
        // Keep a held control enabled until release; disabling it would lose capture and cancel.
        PttButton.IsEnabled = mouseHeld || keyHeld || available && microphoneReady;
        PttButton.Visibility = handsFree ? Visibility.Collapsed : Visibility.Visible;
        ListenButton.Visibility = LevelMeter.Visibility = handsFree ? Visibility.Visible : Visibility.Collapsed;
        ListenButton.IsEnabled = listening || available && microphoneReady;
        ListenButton.Content = listening ? "Stop _listening" : "Start _listening";
        ReleaseButton.IsEnabled = ownRunning && owned!.Authorization.Microphone && owned.Turn is null && owned.Transcription is null && !owned.Status.Finished;
        StopButton.IsEnabled = listening || watching || ownRunning || loading is not null || accepted || AcceptMemory.IsChecked == true ||
            AcceptCapture.IsChecked == true || AcceptUpload.IsChecked == true || AcceptScreen.IsChecked == true;
        ReloadButton.IsEnabled = !operations.IsRunning && !listening && !watching;
        SetupButton.IsEnabled = AudioButton.IsEnabled = !operations.IsRunning && !listening && !watching;
        AudioButton.IsEnabled &= audio is not null;
        bool watchable = ready && !locked && PauseChoice.IsChecked != true && MuteChoice.IsChecked != true &&
            AcceptScreen.IsChecked == true && controller.Configuration is { } configured &&
            configured.Unavailable(voice, false) is null && configured.Vision() != VisionSupport.Unsupported;
        WatchButton.IsEnabled = watching || watchable;
        WatchButton.Content = watching ? "Stop _watching" : "Start _watching";
        WatchDot.Visibility = watching ? Visibility.Visible : Visibility.Collapsed;
        Title = watching ? "Martlet - talk (watching your screen)" : "Martlet - talk";
        WatchStatus.Text = watching && pacer is { } pace
            ? $"Watching ({pace.Chattiness}). {watchNote} Looks this hour: {pace.LooksThisHour} of at most {pace.Settings.LooksPerHour}."
            : watchNote ?? "Not watching. Nothing on your screen is captured.";
        VoiceIdChoice.IsEnabled = !listening;
        VoiceIdStatus.Text = voiceIdentity is null ? "Voice ID is unavailable without a local data directory."
            : voiceIdentity.LoadError ?? (voiceIdentity.Current is { } print
                ? $"Enrolled {print.CreatedAt.LocalDateTime:d}; match threshold {print.Threshold:F2}. Runs on this PC; nothing about your voice is uploaded."
                : VoiceIdChoice.IsChecked == true ? "Not set up yet: enroll your voice first (Set up Voice ID), or turn this off to talk."
                : "Not set up. Enroll your voice once (about 20 seconds) to ignore other people and TV.");
    }

    private void ClearPermission()
    {
        rendering = true;
        AcceptAction.IsChecked = AcceptMemory.IsChecked = AcceptCapture.IsChecked = AcceptUpload.IsChecked = false;
        rendering = false;
    }

    private bool Start(bool microphone, bool handsFree = false)
    {
        if (closed || !ready || locked) return false;
        // You come first: a remark about your screen stops the moment you start talking or typing.
        if (commentary is { OwnershipReleased: false } glance)
        {
            controller.Stop(glance, "commentary.interrupted", keepContext: true);
            ResultText.Text = "Martlet stopped its remark about your screen so you can talk. Try again in a moment.";
            return false;
        }
        try
        {
            var listen = microphone ? CurrentListening(handsFree) : null;
            var next = controller.Start(microphone ? null : InputText.Text, VoiceChoice.IsChecked == true, microphone,
                AcceptAction.IsChecked == true, AcceptCapture.IsChecked == true, AcceptUpload.IsChecked == true,
                memoryApproved: AcceptMemory.IsChecked == true, listening: listen);
            owned = next;
            if (!handsFree) pacer?.NoteConversation();
            // Hands-free keeps the session's approvals until listening stops; memory retrieval is still one-shot.
            if (handsFree)
            {
                rendering = true;
                AcceptMemory.IsChecked = false;
                rendering = false;
            }
            else ClearPermission();
            // While listening, the previous exchange stays visible until new speech is transcribed.
            if (!handsFree || !listening)
            {
                AnswerText.Clear();
                RefusalText.Clear();
                TranscriptText.Clear();
            }
            ResultText.Text = handsFree ? "Listening... just start talking. Stop listening, Stop or Esc ends it."
                : "Explicit action accepted. Stop revokes it; local cleanup may outlive reporting. Cost UNKNOWN.";
            if (!handsFree) Motion.Enter(AnswerText, dy: 10);
            Observe();
            RenderActions();
            return true;
        }
        catch (LiveActionException error) { ResultText.Text = Remedy(error.Code); }
        catch (ContractException) { ResultText.Text = "Invalid or oversized input. Use at most 4096 valid Unicode characters / 16,384 UTF-8 bytes."; }
        catch (VoiceIdentityException error) { ResultText.Text = error.Message; }
        return false;
    }

    private ListeningOptions CurrentListening(bool handsFree) => new(handsFree,
        new VoiceActivitySettings
        {
            Sensitivity = SensitivitySlider.Value,
            EndSilence = TalkPreferences.Pauses[Math.Clamp(PauseChoiceBox.SelectedIndex, 0, TalkPreferences.Pauses.Length - 1)]
        },
        VoiceIdChoice.IsChecked == true);

    private void Listen_Click(object sender, RoutedEventArgs e)
    {
        if (listening) { StopListening("Hands-free listening stopped. Nothing is recorded now."); return; }
        handledListen = null;
        listening = Start(true, handsFree: true);
        RenderActions();
    }

    private void StopListening(string message)
    {
        listening = false;
        if (owned is { OwnershipReleased: false } operation && operation.HandsFree) controller.Stop(operation, "conversation.canceled");
        ClearPermission();
        ResultText.Text = message;
        RenderActions();
    }

    // Re-arms hands-free listening after each finished turn (never while a reply is still playing).
    private void ContinueListening()
    {
        if (!listening || closed || owned is not { } last || !last.OwnershipReleased) return;
        if (!ReferenceEquals(handledListen, last))
        {
            handledListen = last;
            var code = last.Status.Code;
            listenNote = last.SpeakerCheck is { Verdict: not SpeakerVerdict.User } check && last.Voiceprint is { } print
                ? VoiceIdentity.Describe(check, print.Threshold)
                : code == "stt.NoSpeech" ? "(The last sound had no words.)" : null;
            var keepGoing = code is "mic.no_speech" or "speaker.not_user" or "speaker.too_short" or "stt.NoSpeech" or
                "runtime.Completed" or "runtime.Refused" or "commentary.glance" ||
                code.StartsWith("policy.", StringComparison.Ordinal) && !last.Status.Quarantined;
            if (!keepGoing || last.Status.Quarantined)
            {
                listening = false;
                ClearPermission();
                RenderActions();
                return;
            }
        }
        if (operations.IsRunning) return;
        // A wanted glance at the screen goes first; listening re-arms as soon as it finishes.
        if (TryStartCommentary()) return;
        if (AcceptAction.IsChecked != true || AcceptCapture.IsChecked != true || AcceptUpload.IsChecked != true)
            return;
        if (!Start(true, handsFree: true))
        {
            listening = false;
            ClearPermission();
        }
    }

    private void ApplyPreferences(TalkPreferences preferences)
    {
        applyingPreferences = true;
        (preferences.HandsFree ? VoiceActivityMode : PushToTalkMode).IsChecked = true;
        SensitivitySlider.Value = preferences.Sensitivity;
        PauseChoiceBox.SelectedIndex = preferences.PauseIndex;
        VoiceIdChoice.IsChecked = preferences.VoiceId;
        ChattinessBox.SelectedIndex = preferences.ScreenChattiness;
        ScreenScopeBox.SelectedIndex = preferences.ScreenScope;
        VoiceActivityPanel.Visibility = preferences.HandsFree ? Visibility.Visible : Visibility.Collapsed;
        applyingPreferences = false;
    }

    private void SavePreferences()
    {
        if (applyingPreferences || voiceIdentity is null || SensitivitySlider is null || PauseChoiceBox is null || VoiceIdChoice is null ||
            ChattinessBox is null || ScreenScopeBox is null) return;
        var saved = new TalkPreferences(VoiceActivityMode.IsChecked == true, SensitivitySlider.Value,
            PauseChoiceBox.SelectedIndex, VoiceIdChoice.IsChecked == true, ChattinessBox.SelectedIndex, ScreenScopeBox.SelectedIndex)
            .Save(voiceIdentity.DataDirectory);
        if (!saved) ResultText.Text = "Could not save your talk preferences to talk-preferences.json; they apply for this window only.";
    }

    private void Watch_Click(object sender, RoutedEventArgs e)
    {
        if (watching) { StopWatching("Stopped watching your screen. Nothing is captured now."); return; }
        var selected = controller.Configuration;
        if (!ready || locked || selected is null) { ResultText.Text = Remedy("conversation.setup_required"); return; }
        if (selected.Vision() == VisionSupport.Unsupported) { ResultText.Text = selected.VisionAdvice(); return; }
        if (AcceptScreen.IsChecked != true) { ResultText.Text = Remedy("conversation.permission_required"); return; }
        watching = true;
        lookWanted = false;
        pacer = new(SelectedChattiness, clock);
        nextGlance = clock.GetTimestamp();
        watchNote = "First look in a few seconds.";
        ResultText.Text = "Watching your screen. Keep playing: Martlet only speaks up now and then. Stop watching, Stop or Esc ends it.";
        RenderActions();
    }

    private void Screen_Changed(object sender, RoutedEventArgs e)
    {
        if (ChattinessBox is null || ScreenScopeBox is null || WatchStatus is null || EnvelopeText is null || applyingPreferences) return;
        if (watching && pacer is not null && pacer.Chattiness != SelectedChattiness) pacer = new(SelectedChattiness, clock);
        SavePreferences();
        RenderConfiguration();
        RenderActions();
    }

    // Runs on the UI timer: notices conversation, collects finished glances and schedules the next capture.
    private void Watch()
    {
        if (!watching || closed || pacer is null) return;
        if (commentary is { OwnershipReleased: true } done && !ReferenceEquals(handledCommentary, done))
        {
            handledCommentary = done;
            HandleCommentary(done);
            if (!watching) return;
        }
        // Anything but an idle hands-free listen means you and Martlet are talking right now.
        if (owned is { OwnershipReleased: false } live && !IsIdleListen(live)) pacer.NoteConversation();
        if (glancing || clock.GetTimestamp() < nextGlance) return;
        nextGlance = clock.GetTimestamp() + (long)(ScreenCommentaryPacer.Tick.TotalSeconds * clock.TimestampFrequency);
        _ = GlanceAsync();
    }

    // A hands-free listen that has not heard anyone yet (including the moment it re-arms).
    private static bool IsIdleListen(LiveConversationOperation operation) =>
        operation.HandsFree && operation.Turn is null && operation.Transcription is null &&
        operation.Status.Code is "conversation.authorizing" or "mic.listening" or "mic.no_speech" or "commentary.glance";

    private async Task GlanceAsync()
    {
        glancing = true;
        try
        {
            var scope = SelectedScope;
            var result = await Task.Run(() => glancer.Capture(scope));
            if (!watching || closed || pacer is null)
            {
                result.Frame?.Clear();
                return;
            }
            if (result.Frame is not { } frame)
            {
                watchNote = result.Skip switch
                {
                    GlanceSkip.MartletInFront => "Martlet is in front, so it isn't looking.",
                    GlanceSkip.Private => "A password manager or private window is in front; not looking.",
                    GlanceSkip.Blank => "The screen reads back black (exclusive full-screen or protected video). Switch the game to borderless or windowed so Martlet can see it.",
                    GlanceSkip.Minimized or GlanceSkip.NoWindow => "No window in front to look at.",
                    _ => "Couldn't capture the screen this time."
                };
                return;
            }
            pacer.ObserveFrame(frame.Change);
            pendingFrame?.Clear();
            pendingFrame = frame;
            var idleListen = owned is { OwnershipReleased: false } live && IsIdleListen(live);
            var busy = commentary is { OwnershipReleased: false } || operations.IsRunning && !idleListen;
            var verdict = pacer.Decide(busy, glancer.UserIdle);
            lookWanted = verdict == PacerVerdict.Look;
            if (!lookWanted)
            {
                watchNote = verdict switch
                {
                    PacerVerdict.UserAway => "You seem to be away; waiting for you.",
                    PacerVerdict.HourlyLimit => "Hourly look budget used up; resting.",
                    PacerVerdict.AfterConversation or PacerVerdict.Busy => "You're talking; not interrupting.",
                    _ => watchNote
                };
                return;
            }
            if (!operations.IsRunning) TryStartCommentary();
            // An idle hands-free listen (nobody speaking) briefly yields; listening re-arms right after the glance.
            else if (idleListen && listening) controller.Stop(owned!, "commentary.glance", keepContext: true);
        }
        finally
        {
            glancing = false;
        }
    }

    private bool TryStartCommentary()
    {
        if (!lookWanted || !watching || closed || pendingFrame is not { } frame || operations.IsRunning) return false;
        lookWanted = false;
        pendingFrame = null;
        try
        {
            var image = frame.Encode();
            commentary = controller.StartCommentary(image, frame.Title, SelectedChattiness, VoiceChoice.IsChecked == true,
                AcceptScreen.IsChecked == true);
            watchNote = "Taking a look...";
            return true;
        }
        catch (LiveActionException error)
        {
            if (error.Code is "conversation.ownership_busy") return false;
            StopWatching(error.Code == "commentary.vision_unsupported" ? controller.Configuration?.VisionAdvice() ?? Remedy(error.Code) : Remedy(error.Code));
            return false;
        }
        catch (Exception error) when (error is ContractException or InvalidOperationException or NotSupportedException or System.Runtime.InteropServices.ExternalException)
        {
            watchNote = "Couldn't prepare the screen image this time.";
            return false;
        }
        finally
        {
            frame.Clear();
        }
    }

    private void HandleCommentary(LiveConversationOperation done)
    {
        var status = done.Status;
        var at = DateTime.Now.ToString("t");
        if (done.Passed)
        {
            pacer?.NoteLook(false);
            watchNote = $"Looked at {at}: nothing worth saying.";
            return;
        }
        if (status.Code is "runtime.Completed")
        {
            pacer?.NoteLook(true);
            AnswerText.Text = $"(Glanced at your screen at {at})\n{done.Turn?.Content.Text.Trim()}";
            RefusalText.Clear();
            watchNote = $"Said something at {at}.";
            return;
        }
        if (status.Code is "runtime.Refused" or "commentary.interrupted" or "conversation.canceled" or "conversation.revoked")
        {
            pacer?.NoteLook(false);
            return;
        }
        // No automatic retry of a failing paid request: stop and say what to change.
        var selected = controller.Configuration;
        var provider = done.Turn?.Snapshot.ProviderFailure ?? status.ProviderFailure;
        StopWatching(provider == ProviderFailureCode.InputLimit
            ? "The screenshot didn't fit the Thinking route. If Thinking runs on your Martlet host, update the host so its gateway accepts screen images, then start watching again."
            : provider is not null && selected is not null && selected.Vision() != VisionSupport.Supported
            ? $"The Thinking model rejected the screenshot ({provider}); it most likely can't see images. {selected.VisionAdvice()}"
            : "Stopped watching: " + (provider is { } code ? ProviderRemedy(code) : Remedy(status.Code)));
    }

    private void StopWatching(string? message)
    {
        var wasWatching = watching;
        watching = lookWanted = false;
        pacer = null;
        pendingFrame?.Clear();
        pendingFrame = null;
        if (commentary is { OwnershipReleased: false } glance) controller.Stop(glance, "commentary.stopped", keepContext: true);
        rendering = true;
        if (AcceptScreen is not null) AcceptScreen.IsChecked = false;
        rendering = false;
        if (message is not null && (wasWatching || watchNote is null))
        {
            watchNote = message;
            ResultText.Text = message;
        }
        else if (wasWatching) watchNote = "Stopped watching. Nothing on your screen is captured.";
        RenderActions();
    }

    private void Mode_Changed(object sender, RoutedEventArgs e)
    {
        if (VoiceActivityPanel is null || VoiceActivityMode is null || controller is null) return;
        VoiceActivityPanel.Visibility = VoiceActivityMode.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        if (applyingPreferences) return;
        if (listening) StopListening("Talk mode changed; hands-free listening stopped.");
        SavePreferences();
        RenderActions();
    }

    private void Activity_Changed(object sender, RoutedEventArgs e)
    {
        // Applies from the next utterance; the one being heard keeps its settings.
        SavePreferences();
    }

    private void VoiceId_Changed(object sender, RoutedEventArgs e)
    {
        if (applyingPreferences || controller is null) return;
        SavePreferences();
        RenderActions();
    }

    private void VoiceIdSetup_Click(object sender, RoutedEventArgs e)
    {
        if (voiceIdentity is null) { ResultText.Text = "Voice ID needs a local data directory."; return; }
        if (operations.IsRunning || listening) { ResultText.Text = Remedy("conversation.ownership_busy"); return; }
        Cancel("conversation.configuration_changed");
        var input = controller.Configuration?.Audio?.Input;
        new VoiceIdWindow(voiceIdentity, operations, sessionEvents, input) { Owner = this }.ShowDialog();
        RenderActions();
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
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        Cancel("conversation.canceled");
    }
    private void Cancel(string reason)
    {
        mouseHeld = keyHeld = listening = false;
        listenNote = null;
        PttButton.ReleaseMouseCapture();
        ClearPermission();
        if (watching || AcceptScreen.IsChecked == true) StopWatching(null);
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
        if (rendering || controller is null || AcceptAction is null || AcceptScreen is null) return;
        if (watching && AcceptScreen.IsChecked != true) StopWatching("Screen permission withdrawn; stopped watching. Nothing is captured now.");
        if (listening && (AcceptAction.IsChecked != true || AcceptCapture.IsChecked != true || AcceptUpload.IsChecked != true))
            listening = false;
        if (owned is { OwnershipReleased: false } && (AcceptAction.IsChecked != true ||
            owned.MemoryRequested && AcceptMemory.IsChecked != true ||
            owned.Authorization.Microphone && (AcceptCapture.IsChecked != true || AcceptUpload.IsChecked != true)))
            controller.Stop(owned, "conversation.revoked");
        RenderActions();
    }
    private void Input_Changed(object sender, System.Windows.Controls.TextChangedEventArgs e) => RenderActions();
    private void Controls_Changed(object sender, RoutedEventArgs e)
    {
        if (controller is null || PauseChoice is null || MuteChoice is null) return;
        listening = false;
        ClearPermission();
        if (watching) StopWatching(PauseChoice.IsChecked == true ? "Paused; stopped watching your screen." : "Muted; stopped watching your screen.");
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
            if (value)
            {
                listening = false;
                if (watching) StopWatching("Windows locked; stopped watching your screen.");
            }
            ClearPermission();
            controller.SetControls(PauseChoice.IsChecked == true, MuteChoice.IsChecked == true, locked);
            RenderActions();
        });
    }
    // Hands-free listening and screen watching are meant to keep working while you use other apps (or play);
    // lock, pause, mute, Stop and Close still end them.
    private void Window_Deactivated(object? sender, EventArgs e) { if (!closed && !listening && !watching) Cancel("conversation.deactivated"); }
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
        new SetupWindow(settings, operations)
            { Owner = this, Troubleshooting = Troubleshooting, ConfigurationRecovery = ConfigurationRecovery }.ShowDialog();
        await LoadAsync();
    }
    private async void Audio_Click(object sender, RoutedEventArgs e)
    {
        if (operations.IsRunning || audio is null) return;
        Cancel("conversation.configuration_changed");
        ready = false;
        new AudioSetupWindow(settings, operations, audio, sessionEvents: sessionEvents) { Owner = this, Troubleshooting = Troubleshooting }.ShowDialog();
        await LoadAsync();
    }

    private void Observe()
    {
        if (closed || owned is not { } operation) return;
        var snapshot = operation.Turn?.Snapshot;
        var content = operation.Turn?.Content;
        string evidence = snapshot?.TextProvenance == EvidenceProvenance.Live ? "REAL provider response (not account/device qualification)"
            : snapshot?.TextProvenance == EvidenceProvenance.Fixture ? "FIXTURE HTTP evidence - NOT real inference" : "No provider response";
        // Hands-free keeps the previous reply on screen until the next one starts.
        if (!operation.HandsFree || operation.Turn is not null)
        {
            AnswerText.Text = $"{evidence}\n{content?.Text}";
            RefusalText.Text = content?.Refusal ?? "";
        }
        var speaker = operation.SpeakerCheck is { } check && operation.Voiceprint is { } print
            ? VoiceIdentity.Describe(check, print.Threshold) : null;
        if (operation.Transcription is { } stt)
            TranscriptText.Text = $"{(stt.Provenance == EvidenceProvenance.Live ? "REAL STT" : "FIXTURE STT - NOT inference")}: {stt.Outcome}; confidence UNKNOWN\n{operation.Transcript}" +
                (speaker is null ? "" : "\n" + speaker);
        LevelMeter.Value = operation.HandsFree && !operation.Status.Finished ? Math.Clamp((operation.VoiceLevel + 60) / 50, 0, 1) : 0;
        var capture = operation.Capture?.Snapshot;
        var status = operation.Status;
        if (Support is { } support)
            supportProjection.Observe(support, new(operation.Id, snapshot?.TurnId, status,
                snapshot is null ? operation.Transcription?.Provenance ?? EvidenceProvenance.Live :
                    snapshot.TextProvenance ?? EvidenceProvenance.NotRun,
                snapshot?.CommittedSegments ?? 0, snapshot?.QueuedSegments ?? 0));
        StatusText.Text = $"{status.Code}; app worker released: {operation.OwnershipReleased}; quarantine: {status.Quarantined}.\n" +
            $"Mic: {capture?.State.ToString() ?? "not used"}; samples: {capture?.CanonicalSamples ?? 0}; retained PCM: {capture?.RetainedPcmBytes ?? 0}. " +
            $"Input: {(operation.HandsFree ? "hands-free voice activity" : operation.Authorization.Microphone ? "push-to-talk" : "typed")}; " +
            $"Voice ID: {(operation.Voiceprint is null ? "off" : speaker ?? "not checked yet")}. Wake words/unsolicited: OFF.\n" +
            $"STT: {operation.Transcription?.Outcome.ToString() ?? "not completed / not used"}; Policy: {status.Policy?.ToString() ?? "see timeline"}; LLM/TTS: {snapshot?.State.ToString() ?? "not dispatched"}.\n" +
            $"Persona revision/style: {operation.PersonaRevision?.ToString() ?? "not dispatched"} / {operation.ResponseStyle?.ToString() ?? "not selected"}.\n" +
            $"In-memory context messages used/omitted: {operation.ContextMessages}/{operation.ContextMessagesOmitted}; retained completed turns: {controller.ContextTurns}.\n" +
            $"Local memory retrieval requested: {operation.MemoryRequested}; store revision: {operation.MemoryStoreRevision?.ToString() ?? "not read"}; facts used/omitted: {operation.MemoryFactsUsed}/{operation.MemoryFactsOmitted}. Fact content is never shown in this metadata timeline.\n" +
            $"Reserved requests: {operation.Authorization.ReservedRequests}; segments: {snapshot?.CommittedSegments ?? 0}; queued: {snapshot?.QueuedSegments ?? 0}; suppressed fragments: {snapshot?.SuppressedFragments ?? 0}.\n" +
            $"Playback accepted/submitted/device-consumed: {snapshot?.AcceptedSamples ?? 0}/{snapshot?.SubmittedSamples ?? 0}/{snapshot?.DeviceConsumedSamples ?? 0}; drain: {snapshot?.Playback?.DeviceDrainObserved ?? false}; may have played: {snapshot?.MayHavePlayed ?? false}. NOT proof of heard audio; cost UNKNOWN.\n" +
            $"Runtime failure: {snapshot?.Failure}; provider: {snapshot?.ProviderFailure ?? status.ProviderFailure}; audio: {snapshot?.Playback?.Error?.Code ?? status.AudioFailure}.\n" + operation.Timeline;
        if (operation.Authorization.CredentialFailure is { } credential) ResultText.Text = CredentialMessages.Describe(credential);
        else if (status.AudioFailure is { } error) ResultText.Text = AudioSetupDiagnostics.Remedy(error) + " Typed fallback remains available.";
        else if ((snapshot?.ProviderFailure ?? status.ProviderFailure) is { } provider) ResultText.Text = ProviderRemedy(provider);
        else if (status.Code is "speaker.not_user" or "speaker.too_short" && speaker is not null) ResultText.Text = speaker;
        else if (status.Finished) ResultText.Text = Remedy(status.Code);
        else if (operation.OwnershipReleased) ResultText.Text = "The action failed at a dependency boundary. No automatic retry; review settings and authorize a fresh action.";
        else if (operation.HandsFree && ListeningMessage(status.Code) is { } message) ResultText.Text = message;
    }

    private string? ListeningMessage(string code) => code switch
    {
        "mic.listening" => "Listening... just start talking. Stop listening, Stop or Esc ends it." + (listenNote is null ? "" : " " + listenNote),
        "mic.hearing_speech" => "Hearing you... pause when you're done.",
        "speaker.checking" => "Checking it's you (Voice ID, on this PC)...",
        "speaker.verified" or "mic.transferred_and_cleared" or "stt.uploading" => "Got it. Transcribing...",
        _ when code.StartsWith("runtime.", StringComparison.Ordinal) => "Replying... listening resumes after the reply finishes.",
        _ => null
    };

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
            $"{code}: the origin/credential boundary was rejected. Use only the configured route destination; never redirect a key or disable TLS/protection.",
        ProviderFailureCode.OutputTokenLimit =>
            "OutputTokenLimit: the model used the whole reply token budget. Reasoning/thinking models can spend it on hidden thinking; choose an instruct/chat model or ask for a shorter reply. No automatic retry.",
        _ => $"{code}: the provider result exceeded bounds or did not match the supported request/response contract. Preserve the stage code, review the selected route and use a fresh explicit action only after cleanup; no hidden continuation."
    };

    internal static string Remedy(string code) => code switch
    {
        "conversation.permission_required" => "Permission missing. Review the displayed envelope; PTT additionally needs separate local-capture and STT-upload permission.",
        "memory.disabled" => "Local memory is OFF or not available for this loaded configuration. No memory store was opened. Use Local memory to review and enable a safe local scope, then Reload before a fresh action.",
        "memory.configuration_changed" => "Memory configuration changed. Retrieval and this action were revoked; Reload and review a fresh action.",
        "memory.retrieval_invalidated" => "Memory changed or permission was revoked while retrieval was active. No retrieved fact was accepted and no LLM request followed; review a fresh action.",
        "conversation.ownership_busy" => "An app operation still owns resources or cleanup. No queue or replacement started. Stop that action and wait for actual release; close Martlet if native cleanup remains stuck.",
        "conversation.setup_required" or "conversation.configuration_unsupported" => "Review the supported named routes, credential references, destination choices and selected audio policy in Setup. No provider was contacted by this rejection.",
        "conversation.configuration_changed" => "Configuration changed during the action. Permission revoked; Reload and review the new role/model/voice/key/output before a fresh action.",
        "conversation.input_limit" => "The user input plus selected persona/style exceeds the displayed LLM input budget. Shorten the input or persona, then review and authorize a fresh action. Nothing was truncated.",
        "conversation.expired" => "The original permission window expired. No permission was renewed. Discard old input/capture and explicitly authorize a new action.",
        "stt.deadline_exceeded" => "The original STT deadline expired. Upload/credential work may still own resources; no LLM follows. Wait for actual cleanup, then use typed fallback or authorize a fresh recording.",
        "stt.NoSpeech" => "STT returned no speech. No LLM or TTS request followed. Try a fresh PTT action or use typed input.",
        "mic.no_speech" => "No speech was heard, so nothing was uploaded. While hands-free listening is on it simply keeps listening.",
        "voiceid.not_enrolled" => "Only respond to my voice is on, but no voiceprint is saved. Use Set up Voice ID to enroll, or turn it off.",
        "runtime.Completed" => "Response completed. Device consumption/drain is not audibility. Any retry is a new potentially paid action.",
        "runtime.Refused" => "The provider refused. Refusal is shown separately, not routed as ordinary speech. Earlier partial response text can remain visible.",
        "runtime.Partial" or "runtime.Failed" => "The turn failed or is partial; response text remains visible. Check the stage/failure below, selected model/account limits and output. No automatic retry; earlier speech may have played.",
        "conversation.cleanup_quarantined" or "mic.cleanup_quarantined" => "Cleanup is unproven; the app-wide ownership slot is quarantined. No replacement work is permitted. Close Martlet and review the device/session before a fresh launch.",
        "commentary.glance" => "Listening paused for a moment while Martlet glances at your screen; it resumes right after.",
        "commentary.passed" => "Martlet looked at your screen and had nothing to say.",
        "commentary.interrupted" or "commentary.stopped" => "Martlet stopped its remark about your screen.",
        "commentary.vision_unsupported" => "Your Thinking model can't see images, so screen watching is unavailable. See Watch my screen for what to change.",
        _ => $"{code}. No automatic continuation. Review the stage, use typed/text-only fallback if audio failed, or explicitly authorize a fresh action after actual cleanup."
    };
}
