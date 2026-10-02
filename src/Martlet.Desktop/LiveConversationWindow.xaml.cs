using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Shapes;
using System.Windows.Threading;
using Martlet.Audio;
using Martlet.Conversation;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;
using Martlet.Diagnostics;
using Martlet.Providers;

namespace Martlet.Desktop;

public enum ChatRole { User, Martlet, Note }

/// <summary>One entry in the talk window's history: what you typed or said, Martlet's reply, or a short note.</summary>
public sealed class ChatMessage : INotifyPropertyChanged
{
    private string text;
    private string note = "";

    internal ChatMessage(ChatRole role, string text, string caption = "")
    {
        Role = role;
        this.text = text;
        Caption = caption;
    }

    public ChatRole Role { get; }
    public bool IsUser => Role == ChatRole.User;
    public bool IsNote => Role == ChatRole.Note;
    public string Caption { get; }
    public string Text { get => text; set => Set(ref text, value); }
    public string Note { get => note; set { if (Set(ref note, value)) Changed(nameof(HasNote)); } }
    public bool HasNote => note.Length > 0;
    public event PropertyChangedEventHandler? PropertyChanged;

    internal void AddNote(string line) => Note = note.Length == 0 ? line : note + "  " + line;

    private bool Set(ref string field, string value, [CallerMemberName] string? name = null)
    {
        if (field == value) return false;
        field = value;
        Changed(name);
        return true;
    }

    private void Changed(string? name) => PropertyChanged?.Invoke(this, new(name));
}

/// <summary>The conversation: its history and the message box. How Martlet listens (always or push-to-talk), whether it
/// speaks and whether it may see (Vision) are chosen in Companion and run while this window is open; the mic and vision buttons
/// pause and resume them, and Stop (Esc) stops everything at once.</summary>
public partial class LiveConversationWindow : ThemedWindow
{
    internal SupportController? Support { get; init; }
    private readonly LiveSupportProjection supportProjection = new();
    private readonly ISetupService settings;
    private readonly SetupOperationRunner operations;
    private readonly LiveConversationController controller;
    private readonly IAudioSessionEvents sessionEvents;
    private readonly TimeProvider clock;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly IScreenGlancer glancer;
    private readonly IVideoInput video;
    private readonly TalkPreferences preferences;
    // A camera address as typed in Companion, password included; it lives only as long as the app.
    private readonly string? videoAddress;
    private SetupOperation? loading;
    private CancellationTokenSource? observation;
    private LiveConversationOperation? owned, handled, yielded;
    private bool closed, ready, mouseHeld, keyHeld, follow = true;
    private volatile bool locked;
    private long generation;
    // Always listening runs whenever the window is open and the microphone is set up; the mic button or Stop pauses it.
    private bool listening, listenPaused;
    private string? listenNote;
    // Typed text waits here while an idle listen or a screen remark hands the app slot over.
    private string? pendingText;
    private ChatMessage? pendingMessage;
    private string? reloadReason;
    // What the status line says after something finished or went wrong; the next thing you do clears it.
    private string? notice;
    // The bubbles the current operation fills in.
    private LiveConversationOperation? shown;
    private ChatMessage? heard, home, reply, lastReply;
    // Vision: separate from your own turns (owned), so a glance never replaces a reply.
    private WatchSource watchSource = new(WatchKind.ActiveWindow);
    private bool watching, watchPaused, glancing, lookWanted;
    private ScreenCommentaryPacer? pacer;
    private ScreenFrame? pendingFrame;
    private LiveConversationOperation? commentary, handledCommentary;
    private long nextGlance;
    private string? watchNote, captureNote, visionProblem;

    public ObservableCollection<ChatMessage> Messages { get; } = [];
    internal bool IsReady => ready && loading is null;
    internal LiveConversationOperation? Current => owned;

    internal LiveConversationWindow(ISetupService settings, SetupOperationRunner operations, LiveConversationController controller,
        IAudioSessionEvents sessionEvents, TimeProvider? clock = null, VoiceIdentity? voiceIdentity = null,
        IScreenGlancer? glancer = null, IVideoInput? video = null, TalkPreferences? preferences = null, string? videoAddress = null)
    {
        this.settings = settings;
        this.operations = operations;
        this.controller = controller;
        this.sessionEvents = sessionEvents;
        this.clock = clock ?? TimeProvider.System;
        this.glancer = glancer ?? new ScreenGlancer();
        this.video = video ?? new VideoInput(controller.Home is { } home ? home.CameraAuthorization : null);
        this.preferences = preferences ?? TalkPreferences.Load(voiceIdentity?.DataDirectory);
        this.videoAddress = videoAddress;
        InitializeComponent();
        History.ItemsSource = Messages;
        Messages.CollectionChanged += (_, _) => EmptyHint.Visibility = Messages.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        locked = controller.Controls.Locked;
        // Pause and mute used to be switches in this window; listening and vision now pause from their own buttons.
        controller.SetControls(false, false, locked);
        timer.Tick += (_, _) => { Observe(); Watch(); Pump(); RenderActions(); RenderToolApproval(); };
        timer.Start();
        sessionEvents.LockedChanged += SessionSwitch;
        controller.MemoryCaptured += MemoryCaptured;
        controller.VoicesNamed += VoicesNamed;
        if (controller.Home is { } smartHome) smartHome.Confirm = ConfirmHomeAsync;
        RenderActions();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        Motion.Sway(TalkMascot, 3, 4);
        InputText.Focus();
        await LoadAsync();
    }

    /// <summary>Picks up who does what after it changed elsewhere (a synced change or a failover) as soon as Martlet is free.</summary>
    internal void ReloadWhenIdle(string reason)
    {
        if (closed) return;
        reloadReason = reason;
        Pump();
    }

    private async Task LoadAsync()
    {
        if (closed) return;
        if (operations.IsRunning) { notice = Remedy("conversation.ownership_busy"); return; }
        ready = false;
        listening = false;
        if (watching) StopWatching(null);
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
            notice = "Martlet couldn't read your settings in time. Close this window and open it again.";
        }
        else if ((await worker.Completion).Loaded is { } loaded)
        {
            controller.Configure(loaded);
            ready = loaded.Error is null;
            notice = loaded.Error?.Summary ?? (controller.Configuration is null
                ? "Set up how Martlet thinks in Companion › Thinking, then come back to talk." : null);
            StartLive();
        }
        else notice = "Martlet couldn't read your settings. Close this window and open it again.";
        RenderActions();
    }

    private bool Voice => preferences.SpeakReplies && controller.Configuration is { } selected && selected.Unavailable(true, false) is null;
    // Listening uses the microphone chosen in Companion › Listening (the Windows default unless another is picked); testing it
    // there is optional, and a microphone that can't be opened says so here.
    private bool MicrophoneUsable => controller.Configuration is { } selected && selected.Unavailable(Voice, true) is null;
    private bool Available => ready && !locked && loading is null && controller.Configuration is not null;

    private bool Recording => owned is { OwnershipReleased: false, HandsFree: false } live && live.Authorization.Microphone &&
        live.Turn is null && live.Transcription is null && !live.Status.Finished;

    /// <summary>Starts what was chosen in Companion: always listening (once listening is set up) and vision.</summary>
    private void StartLive()
    {
        if (closed || !Available) return;
        listening = preferences.HandsFree && !listenPaused && MicrophoneUsable;
        if (preferences.Watch && !watchPaused && !watching) StartWatching();
    }

    // ---------- the history ----------

    private ChatMessage Add(ChatRole role, string text, string label)
    {
        var message = new ChatMessage(role, text, role == ChatRole.Note ? "" : $"{label} · {DateTime.Now:t}");
        Messages.Add(message);
        return message;
    }

    private void AddNote(string text) => Add(ChatRole.Note, text, "");

    private void History_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        // Follow new messages while you are at the bottom; scrolling up to read earlier ones stops following.
        if (e.ExtentHeightChange == 0) follow = HistoryScroll.VerticalOffset >= HistoryScroll.ScrollableHeight - 8;
        else if (follow) HistoryScroll.ScrollToEnd();
    }

    // ---------- the app slot: typed text, a wanted look, then listening ----------

    // Runs on the UI timer: settles what just finished, then hands the app slot to whatever comes next.
    private void Pump()
    {
        if (closed) return;
        if (owned is { OwnershipReleased: true } done && !ReferenceEquals(handled, done))
        {
            handled = done;
            Finished(done);
        }
        if (loading is not null || locked) return;
        if (operations.IsRunning)
        {
            if (pendingText is not null || reloadReason is not null) YieldSlot();
            return;
        }
        if (reloadReason is { } reason)
        {
            reloadReason = null;
            if (Messages.Count > 0) AddNote(reason + " Martlet switched over and started a fresh conversation.");
            LoadAsync().Forget();
            return;
        }
        if (!Available) return;
        if (pendingText is { } text)
        {
            pendingText = null;
            var message = pendingMessage;
            pendingMessage = null;
            if (!StartTurn(text))
            {
                message?.AddNote("Not sent.");
                if (InputText.Text.Length == 0) InputText.Text = text;
            }
            return;
        }
        if (TryStartCommentary()) return;
        if (listening && !mouseHeld && !keyHeld) StartTurn(null);
    }

    // An idle listen or a screen remark hands the app slot over right away; a reply in progress finishes first.
    private void YieldSlot()
    {
        if (owned is { OwnershipReleased: false } live && IsIdleListen(live) && !ReferenceEquals(yielded, live))
        {
            yielded = live;
            controller.Stop(live, "conversation.typing", keepContext: true);
        }
        if (commentary is { OwnershipReleased: false } glance && !ReferenceEquals(yielded, glance))
        {
            yielded = glance;
            controller.Stop(glance, "commentary.interrupted", keepContext: true);
        }
    }

    private bool StartTurn(string? text)
    {
        bool microphone = text is null;
        bool handsFree = microphone && !mouseHeld && !keyHeld;
        try
        {
            // Pressing Send or the talk button is the action; always listening and the destinations were chosen in Companion.
            owned = controller.Start(text, Voice, microphone, approved: true, localCaptureApproved: microphone, uploadApproved: microphone,
                listening: microphone ? Listening(handsFree) : null);
            yielded = null;
            if (!handsFree)
            {
                notice = null;
                pacer?.NoteConversation();
            }
            Observe();
            return true;
        }
        catch (LiveActionException error) { notice = Remedy(error.Code); }
        catch (ContractException) { notice = Remedy("conversation.invalid_input"); }
        catch (VoiceIdentityException error) { notice = error.Message; }
        if (handsFree) listening = false;
        return false;
    }

    private ListeningOptions Listening(bool handsFree) => new(handsFree,
        new VoiceActivitySettings
        {
            Sensitivity = preferences.Sensitivity,
            EndSilence = TalkPreferences.Pauses[Math.Clamp(preferences.PauseIndex, 0, TalkPreferences.Pauses.Length - 1)]
        },
        preferences.VoiceId);

    // Once per finished turn: the last of its text, what to tell you, and whether listening goes on.
    private void Finished(LiveConversationOperation done)
    {
        Observe(done);
        var status = done.Status;
        var code = status.Code;
        if (ReferenceEquals(shown, done) && reply is not null)
        {
            var refusal = done.Turn?.Content.Refusal?.Trim();
            if (!string.IsNullOrEmpty(refusal) && reply.Text != refusal) reply.AddNote("Martlet then declined: " + refusal);
            else if (done.Turn?.Snapshot.State is not (ConversationState.Completed or ConversationState.Refused)) reply.AddNote("Cut short.");
            else if (done.Turn?.Snapshot.SpeechLimitReached == true) reply.AddNote("Only the start was said aloud.");
        }
        if (done.HandsFree)
        {
            listenNote = done.SpeakerCheck is { Verdict: not SpeakerVerdict.User } check && done.Voiceprint is { } print
                ? VoiceIdentity.Describe(check, print.Threshold) : null;
            var keepGoing = !status.Quarantined && (code is "mic.no_speech" or "speaker.not_user" or "speaker.too_short" or "stt.NoSpeech" or
                "runtime.Completed" or "runtime.Refused" or "commentary.glance" or "conversation.typing" ||
                code.StartsWith("policy.", StringComparison.Ordinal));
            if (!keepGoing) listening = false;
        }
        notice = Outcome(done) ?? (code == "runtime.Completed" ? null : notice);
    }

    private static string? Outcome(LiveConversationOperation done)
    {
        var status = done.Status;
        if (done.Authorization.CredentialFailure is { } credential) return CredentialMessages.Describe(credential);
        if (status.AudioFailure is { } audio) return AudioSetupDiagnostics.Remedy(audio) + " You can still type.";
        if ((done.Turn?.Snapshot.ProviderFailure ?? status.ProviderFailure) is { } provider) return ProviderRemedy(provider);
        if (status.Quarantined) return Remedy("conversation.cleanup_quarantined");
        return status.Code switch
        {
            "runtime.Completed" or "commentary.glance" or "conversation.typing" or "conversation.listening_paused" or
                "commentary.interrupted" or "conversation.interrupted" or "conversation.closed" or "mic.no_speech" => null,
            "speaker.not_user" or "speaker.too_short" or "stt.NoSpeech" when done.HandsFree => null,
            var code when code.StartsWith("policy.", StringComparison.Ordinal) && done.HandsFree => null,
            var code => Remedy(code)
        };
    }

    // ---------- typing ----------

    private void Input_Changed(object sender, TextChangedEventArgs e) => RenderActions();

    private void Input_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || (Keyboard.Modifiers & ModifierKeys.Shift) != 0) return;
        e.Handled = true;
        Send();
    }

    private void Send_Click(object sender, RoutedEventArgs e) => Send();

    private void Send()
    {
        var text = InputText.Text.Trim();
        if (text.Length == 0 || pendingText is not null || !Available) return;
        notice = null;
        pendingText = text;
        pendingMessage = Add(ChatRole.User, text, "You");
        InputText.Clear();
        follow = true;
        Pump();
        RenderActions();
    }

    // ---------- push-to-talk ----------

    private bool StartPushToTalk()
    {
        if (closed || !Available) return false;
        // You come first: talking stops a remark about your screen or a reply that is still playing.
        if (commentary is { OwnershipReleased: false } glance)
        {
            controller.Stop(glance, "commentary.interrupted", keepContext: true);
            notice = "Martlet stopped its remark so you can talk. Hold to talk again.";
            return false;
        }
        if (owned is { OwnershipReleased: false } live)
        {
            controller.Stop(live, "conversation.interrupted", keepContext: true);
            notice = "Martlet stopped. Hold to talk again.";
            return false;
        }
        return StartTurn(null);
    }

    private void Ptt_Click(object sender, RoutedEventArgs e)
    {
        if (Recording) Release();
        else StartPushToTalk();
        RenderActions();
    }

    private void Ptt_Down(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        mouseHeld = true;
        if (!StartPushToTalk()) { mouseHeld = false; RenderActions(); return; }
        if (!PttButton.CaptureMouse()) Discard("conversation.focus_lost");
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
            if (!StartPushToTalk()) { keyHeld = false; RenderActions(); }
        }
    }

    private void Ptt_KeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Space) return;
        e.Handled = true;
        if (keyHeld) Release();
    }

    private void Ptt_LostCapture(object sender, MouseEventArgs e) { if (mouseHeld) Discard("conversation.focus_lost"); }
    private void Ptt_LostFocus(object sender, KeyboardFocusChangedEventArgs e) { if (keyHeld) Discard("conversation.focus_lost"); }

    private void Release()
    {
        mouseHeld = keyHeld = false;
        PttButton.ReleaseMouseCapture();
        owned?.ReleasePress();
        RenderActions();
    }

    // A held recording that lost the button is thrown away, never sent.
    private void Discard(string reason)
    {
        mouseHeld = keyHeld = false;
        PttButton.ReleaseMouseCapture();
        if (owned is { OwnershipReleased: false, HandsFree: false } live) controller.Stop(live, reason, keepContext: true);
        RenderActions();
    }

    // ---------- the header buttons ----------

    private void Mic_Click(object sender, RoutedEventArgs e)
    {
        notice = null;
        if (listening)
        {
            listening = false;
            listenPaused = true;
            if (owned is { OwnershipReleased: false } live && IsIdleListen(live))
                controller.Stop(live, "conversation.listening_paused", keepContext: true);
        }
        else
        {
            listenPaused = false;
            if (MicrophoneUsable) listening = true;
            else notice = ListeningProblem();
        }
        RenderActions();
    }

    /// <summary>Why always listening can't start, with where to fix it.</summary>
    private string ListeningProblem() =>
        controller.Configuration?.Unavailable(Voice, true) is { } why
            ? $"Martlet can't listen yet ({why}) Fix it in Companion › Listening; you can still type."
            : "Martlet can't listen yet. Set up listening in Companion › Listening; you can still type.";

    private void Vision_Click(object sender, RoutedEventArgs e)
    {
        notice = null;
        if (watching)
        {
            watchPaused = true;
            StopWatching(null);
        }
        else
        {
            watchPaused = false;
            StartWatching();
        }
        RenderActions();
    }

    private void Stop_Click(object sender, RoutedEventArgs e) => StopAll("conversation.canceled");

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        StopAll("conversation.canceled");
    }

    /// <summary>Stop (Esc): Martlet's reply, any recording, listening and vision stop right away. The mic and vision buttons turn
    /// them back on; the conversation so far is kept unless the window closes.</summary>
    private void StopAll(string reason, bool keepContext = true)
    {
        mouseHeld = keyHeld = false;
        PttButton.ReleaseMouseCapture();
        if (pendingText is not null) pendingMessage?.AddNote("Not sent.");
        pendingText = null;
        pendingMessage = null;
        reloadReason = null;
        homeQuestion?.TrySetResult(false);
        listenNote = null;
        if (listening) listenPaused = true;
        listening = false;
        if (watching)
        {
            watchPaused = true;
            StopWatching(null);
        }
        if (owned is not null) controller.Stop(owned, reason, keepContext);
        loading?.RequestCancellation();
        observation?.Cancel();
        Observe();
        RenderActions();
    }

    // ---------- what the window shows ----------

    private void Observe()
    {
        if (owned is { } operation) Observe(operation);
    }

    private void Observe(LiveConversationOperation operation)
    {
        if (closed) return;
        if (!ReferenceEquals(shown, operation))
        {
            shown = operation;
            heard = home = reply = null;
        }
        if (heard is null && operation.Transcript is { } said && !string.IsNullOrWhiteSpace(said))
            heard = Add(ChatRole.User, said.Trim(), operation.Heard?.Speaker?.Voice is { } voice
                ? $"{voice.DisplayName}{(voice.Owner ? " (you)" : "")} (spoken)" : "You (spoken)");
        // What Home Assistant did or answered for this turn, above the reply.
        if (home is null && operation.HomeSummary is { } summary && !string.IsNullOrWhiteSpace(summary))
            home = Add(ChatRole.Note, summary.Trim(), "");
        var content = operation.Turn?.Content;
        var text = content?.Text?.Trim() ?? "";
        var refusal = content?.Refusal?.Trim() ?? "";
        if (reply is null && (text.Length > 0 || refusal.Length > 0)) reply = lastReply = Add(ChatRole.Martlet, "", "Martlet");
        if (reply is not null) reply.Text = text.Length > 0 ? text : refusal;
        LevelMeter.Value = operation.HandsFree && !operation.Status.Finished ? Math.Clamp((operation.VoiceLevel + 60) / 50, 0, 1) : 0;
        var snapshot = operation.Turn?.Snapshot;
        if (Support is { } support)
            supportProjection.Observe(support, new(operation.Id, snapshot?.TurnId, operation.Status,
                snapshot is null ? operation.Transcription?.Provenance ?? EvidenceProvenance.Live :
                    snapshot.TextProvenance ?? EvidenceProvenance.NotRun,
                snapshot?.CommittedSegments ?? 0, snapshot?.QueuedSegments ?? 0));
    }

    private void RenderActions()
    {
        if (closed || SendButton is null || EmptyDetail is null) return;
        bool available = Available;
        bool held = mouseHeld || keyHeld;
        SendButton.IsEnabled = available && pendingText is null && !string.IsNullOrWhiteSpace(InputText.Text);
        Placeholder.Visibility = InputText.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

        bool pushToTalk = available && !preferences.HandsFree && MicrophoneUsable;
        PttButton.Visibility = pushToTalk || held ? Visibility.Visible : Visibility.Collapsed;
        // Keep a held button enabled until release; disabling it would lose capture and discard the recording.
        PttButton.IsEnabled = held || pushToTalk;
        var talkLabel = held || Recording ? "Release to send" : "Hold to _talk";
        if (!Equals(PttButton.Content, talkLabel)) PttButton.Content = talkLabel;

        // Always listening shows its state here; when listening can't start, the button says why.
        MicChip.Visibility = available && preferences.HandsFree ? Visibility.Visible : Visibility.Collapsed;
        var micUsable = MicrophoneUsable;
        MicChip.IsEnabled = true;
        MicText.Text = listening ? "Listening" : micUsable ? "Listening paused" : "Can't listen";
        MicDot.SetResourceReference(Shape.FillProperty, listening ? "SuccessBrush" : micUsable ? "MutedBrush" : "WarningBrush");
        MicChip.ToolTip = listening ? "Martlet hears you whenever you speak. Click to pause listening."
            : micUsable ? "Click to listen again." : ListeningProblem();
        AutomationProperties.SetName(MicChip, MicText.Text + ". " + MicChip.ToolTip);

        VisionChip.Visibility = available && preferences.Watch ? Visibility.Visible : Visibility.Collapsed;
        VisionChip.IsEnabled = watching || visionProblem is null;
        VisionText.Text = watching ? "Vision on" : visionProblem is not null ? "Can't see" : "Vision paused";
        VisionDot.SetResourceReference(Shape.FillProperty, watching ? "SuccessBrush" : visionProblem is not null ? "WarningBrush" : "MutedBrush");
        VisionChip.ToolTip = watching
            ? $"Martlet looks at {watchSource.Label} now and then. {watchNote}{(captureNote is { } why ? $" Full-screen game capture is unavailable ({why}); borderless and windowed still work." : "")} Click to stop."
            : visionProblem ?? "Click to let Martlet look again.";
        AutomationProperties.SetName(VisionChip, VisionText.Text + ". " + VisionChip.ToolTip);

        StopButton.IsEnabled = owned is { OwnershipReleased: false } || commentary is { OwnershipReleased: false } ||
            pendingText is not null || listening || watching || loading is not null;
        LevelMeter.Visibility = listening ? Visibility.Visible : Visibility.Collapsed;
        Title = watching ? $"Martlet - talk (looking at {watchSource.Label})" : "Martlet - talk";
        ResultText.Text = notice ?? Activity();
        EmptyDetail.Text = !available ? "" : listening ? "Just start talking, or type below."
            : pushToTalk ? "Type below, or hold the talk button to speak." : "Type a message below.";
    }

    private string Activity()
    {
        if (loading is not null || !ready && notice is null) return "Getting ready…";
        if (locked) return "Windows is locked.";
        if (pendingText is not null) return "Sending…";
        if (owned is { OwnershipReleased: false } live && !live.Status.Finished)
        {
            if (live.HandsFree) return ListeningMessage(live) ?? Idle();
            if (live.Authorization.Microphone && live.Transcription is null)
                return mouseHeld || keyHeld || Recording ? "Recording… release to send." : "Transcribing…";
            return live.Status.Code == "home.asking" ? "Checking with Home Assistant…" : Replying(live);
        }
        if (commentary is { OwnershipReleased: false }) return "Martlet is taking a look…";
        return Idle();
    }

    private string Idle() => !Available ? "" : listening ? "Listening. Just talk, or type below." + (listenNote is null ? "" : " " + listenNote)
        : !preferences.HandsFree && MicrophoneUsable ? "Type below, or hold the talk button to speak." : "Type a message below.";

    private string Replying(LiveConversationOperation live)
    {
        if (live.Status.Code == "tools.preparing") return "Getting tools ready…";
        var snapshot = live.Turn?.Snapshot;
        if (snapshot?.ActiveTool is { } tool)
            return controller.Tools?.PendingApproval is { Answer.IsCompleted: false } ask
                ? $"May Martlet use {ask.Tool}? Answer above the message box." : $"Using {tool}…";
        return snapshot?.State == ConversationState.Playing ? "Martlet is speaking. Esc stops it." : "Martlet is thinking…";
    }

    private string? ListeningMessage(LiveConversationOperation live) => live.Status.Code switch
    {
        "mic.hearing_speech" => "Hearing you… pause when you're done.",
        "speaker.checking" => "Checking it's you…",
        "speaker.verified" or "mic.transferred_and_cleared" or "stt.uploading" or "voices.recognized" => "Got it. Transcribing…",
        "home.asking" => "Checking with Home Assistant…",
        "tools.preparing" => Replying(live),
        var code when code.StartsWith("runtime.", StringComparison.Ordinal) => Replying(live),
        _ => null
    };

    // ---------- vision ----------

    /// <summary>What Companion › Vision looks at; a camera or address that isn't chosen yet has an empty Id.</summary>
    private WatchSource SavedSource() => (WatchKind)Math.Clamp(preferences.ScreenScope, 0, 3) switch
    {
        WatchKind.Camera => new(WatchKind.Camera, preferences.CameraId, preferences.CameraName),
        WatchKind.Url when WatchSource.Normalize(videoAddress ?? preferences.VideoAddress) is { Length: > 0 } address =>
            new(WatchKind.Url, address, WatchSource.SafeName(address)),
        var kind => new(kind)
    };

    private Chattiness SavedChattiness => (Chattiness)Math.Clamp(preferences.ScreenChattiness, 0, 2);

    private void StartWatching()
    {
        visionProblem = null;
        var selected = controller.Configuration;
        if (closed || !Available || selected is null) return;
        if (selected.Vision() == VisionSupport.Unsupported) { visionProblem = selected.VisionAdvice(); return; }
        var source = SavedSource();
        if (!source.IsScreen && source.Id.Length == 0)
        {
            visionProblem = source.Kind == WatchKind.Camera ? "Choose a camera in Companion › Vision." : "Enter the camera address in Companion › Vision.";
            return;
        }
        watchSource = source;
        watching = true;
        lookWanted = false;
        pacer = new(SavedChattiness, clock);
        nextGlance = clock.GetTimestamp();
        watchNote = "First look in a few seconds.";
        captureNote = null;
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
        // Anything but an idle listen means you and Martlet are talking right now.
        if (owned is { OwnershipReleased: false } live && !IsIdleListen(live)) pacer.NoteConversation();
        if (glancing || clock.GetTimestamp() < nextGlance) return;
        nextGlance = clock.GetTimestamp() + (long)(ScreenCommentaryPacer.Tick.TotalSeconds * clock.TimestampFrequency);
        GlanceAsync().Forget();
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
            var source = watchSource;
            var result = await Task.Run(() => source.IsScreen ? glancer.Capture(source.Scope) : video.Capture(source));
            if (!watching || closed || pacer is null)
            {
                result.Frame?.Clear();
                Task.Run(glancer.Release).Forget();
                Task.Run(video.Release).Forget();
                return;
            }
            captureNote = source.IsScreen ? result.Note : null;
            if (result.Frame is not { } frame)
            {
                watchNote = !source.IsScreen ? result.Skip switch
                {
                    GlanceSkip.Blank => $"{char.ToUpperInvariant(source.Label[0])}{source.Label[1..]} shows only black (lens covered, privacy shutter closed or the camera is off).",
                    _ => result.Note ?? "Couldn't read the camera this time; trying again shortly."
                } : result.Skip switch
                {
                    GlanceSkip.MartletInFront => "Martlet is in front, so it isn't looking.",
                    GlanceSkip.Private => "A password manager or private window is in front; not looking.",
                    GlanceSkip.Blank when result.ProtectedContent => "Windows blacks out protected video, so Martlet can't see it.",
                    GlanceSkip.Blank when result.Note is { } why =>
                        $"The screen reads back black and full-screen capture is unavailable: {why}. Borderless or windowed mode still works.",
                    GlanceSkip.Blank => "The screen reads back black (protected content or a game that blocks capture).",
                    GlanceSkip.Minimized or GlanceSkip.NoWindow => "No window in front to look at.",
                    _ => "Couldn't capture the screen this time."
                };
                return;
            }
            pacer.ObserveFrame(frame.Change);
            pendingFrame?.Clear();
            pendingFrame = frame;
            var idleListen = owned is { OwnershipReleased: false } live && IsIdleListen(live);
            var busy = commentary is { OwnershipReleased: false } || pendingText is not null || operations.IsRunning && !idleListen;
            // Keyboard/mouse idleness means "away" only for the screen; in front of a camera people often don't type at all.
            var verdict = pacer.Decide(busy, source.IsScreen ? glancer.UserIdle : TimeSpan.Zero);
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
            // An idle listen (nobody speaking) briefly yields; listening re-arms right after the glance.
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
            // An address's host is not useful to the model; a camera's or window's name is.
            commentary = controller.StartCommentary(image, watchSource.Kind == WatchKind.Url ? "" : frame.Title, SavedChattiness,
                Voice, screenApproved: true, watchSource);
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
            watchNote = "Couldn't prepare the image this time.";
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
            if (done.Turn?.Content.Text.Trim() is { Length: > 0 } remark) Add(ChatRole.Martlet, remark, $"Martlet, about {watchSource.Label}");
            watchNote = $"Said something at {at}.";
            return;
        }
        if (status.Code is "runtime.Refused" or "commentary.interrupted" or "commentary.stopped" or "conversation.canceled" or "conversation.revoked")
        {
            pacer?.NoteLook(false);
            return;
        }
        // No automatic retry of a failing request: stop and say what to change.
        var selected = controller.Configuration;
        var provider = done.Turn?.Snapshot.ProviderFailure ?? status.ProviderFailure;
        StopWatching(provider == ProviderFailureCode.InputLimit
            ? "The picture didn't fit the Thinking route. If Thinking runs on your Martlet host, update the host so its gateway accepts images."
            : provider is not (null or ProviderFailureCode.ModelRetired or ProviderFailureCode.ModelNotFound) &&
                selected is not null && selected.Vision() != VisionSupport.Supported
            ? $"The Thinking model rejected the picture ({provider}); it most likely can't see images. {selected.VisionAdvice()}"
            : "Martlet stopped looking: " + (provider is { } code ? ProviderRemedy(code) : Remedy(status.Code)));
    }

    /// <summary>Stops looking and frees the screen capture, camera or stream. A <paramref name="problem"/> is shown and keeps
    /// vision off until you turn it back on.</summary>
    private void StopWatching(string? problem)
    {
        watching = lookWanted = false;
        pacer = null;
        pendingFrame?.Clear();
        pendingFrame = null;
        // Frees the open duplication, camera or stream (the camera light goes off); off the UI thread in case a capture is finishing.
        Task.Run(glancer.Release).Forget();
        Task.Run(video.Release).Forget();
        if (commentary is { OwnershipReleased: false } glance) controller.Stop(glance, "commentary.stopped", keepContext: true);
        if (problem is not null)
        {
            visionProblem = problem;
            notice = problem;
        }
        RenderActions();
    }

    // ---------- Windows lock, memory and closing ----------

    private void SessionSwitch(bool value)
    {
        locked = value;
        // Revoke immediately on the OS signal, not after a potentially busy dispatcher.
        if (value) controller.Revoke("conversation.locked");
        Dispatcher.BeginInvoke(() =>
        {
            if (closed) return;
            controller.SetControls(false, false, locked);
            if (value)
            {
                mouseHeld = keyHeld = false;
                if (pendingText is not null) pendingMessage?.AddNote("Not sent.");
                pendingText = null;
                pendingMessage = null;
                listening = false;
                if (watching) StopWatching(null);
                if (Messages.Count > 0) AddNote("Windows was locked, so Martlet stopped listening and looking and started a fresh conversation.");
            }
            else
            {
                notice = null;
                StartLive();
            }
            RenderActions();
        });
    }

    // Raised off the dispatcher once background remembering finishes for an exchange.
    private void MemoryCaptured(MemoryCaptureReport report) => Dispatcher.BeginInvoke(() =>
    {
        if (closed) return;
        var text = report.Failure is { } failure
            ? $"Couldn't update memory for that exchange ({failure})."
            : string.Join("  ", report.Changes!.Select(change => change.Kind switch
            {
                MemoryCaptureKind.Remember => "Remembered: ",
                MemoryCaptureKind.Update => "Updated memory: ",
                _ => "Forgot: "
            } + change.Content));
        if (lastReply is not null) lastReply.AddNote(text);
        else AddNote(text);
    });

    // Raised off the dispatcher once names were picked up for voices from an exchange.
    private void VoicesNamed(IReadOnlyList<(Martlet.Core.Speakers.KnownVoice Voice, string Name)> learned) => Dispatcher.BeginInvoke(() =>
    {
        if (closed) return;
        var text = string.Join("  ", learned.Select(l => $"Learned: {l.Voice.Tag.Replace("V", "voice ")} is {l.Name}."));
        if (lastReply is not null) lastReply.AddNote(text);
        else AddNote(text);
    });

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        StopAll("conversation.closed", keepContext: false);
        Task.Run(glancer.Release).Forget();
        Task.Run(video.Release).Forget();
        closed = true;
        generation++;
        timer.Stop();
        sessionEvents.LockedChanged -= SessionSwitch;
        controller.MemoryCaptured -= MemoryCaptured;
        controller.VoicesNamed -= VoicesNamed;
        homeQuestion?.TrySetResult(false);
        if (controller.Home is { } smartHome && smartHome.Confirm == ConfirmHomeAsync) smartHome.Confirm = null;
    }

    private TaskCompletionSource<bool>? homeQuestion;

    /// <summary>Asks above the message box before a request about a lock, door, garage, gate, alarm or valve goes to Home
    /// Assistant. Stop, Close or the timeout answer no.</summary>
    private Task<bool> ConfirmHomeAsync(string question, CancellationToken token) => Dispatcher.InvokeAsync(() =>
    {
        if (closed || token.IsCancellationRequested) return Task.FromResult(false);
        homeQuestion?.TrySetResult(false);
        var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        homeQuestion = answer;
        HomeQuestionText.Text = question;
        HomeQuestionPanel.Visibility = Visibility.Visible;
        Motion.Enter(HomeQuestionPanel, dy: 10);
        if (IsActive) HomeNoButton.Focus();
        var registration = token.Register(() => answer.TrySetResult(false));
        answer.Task.ContinueWith(_ =>
        {
            registration.Dispose();
            Dispatcher.BeginInvoke(() =>
            {
                if (!ReferenceEquals(homeQuestion, answer)) return;
                homeQuestion = null;
                HomeQuestionPanel.Visibility = Visibility.Collapsed;
            });
        }, TaskScheduler.Default);
        return answer.Task;
    }).Task.Unwrap();

    private void HomeYes_Click(object sender, RoutedEventArgs e) => homeQuestion?.TrySetResult(true);
    private void HomeNo_Click(object sender, RoutedEventArgs e) => homeQuestion?.TrySetResult(false);

    private ToolApprovalRequest? shownApproval;

    /// <summary>Shows the MCP tool call waiting for an answer above the message box. The request declines itself when the
    /// reply stops or after 60 seconds.</summary>
    private void RenderToolApproval()
    {
        if (closed || ToolApprovalPanel is null) return;
        if (controller.Tools?.PendingApproval is not { Answer.IsCompleted: false } request)
        {
            shownApproval = null;
            ToolApprovalPanel.Visibility = Visibility.Collapsed;
            return;
        }
        var left = Math.Max(0, (int)Math.Ceiling((request.Expires - clock.GetUtcNow()).TotalSeconds));
        ToolApprovalText.Text = $"{request.Tool}, from the {request.Server} server, with what's below. It runs on this PC with your " +
            $"permissions. Declined automatically in {left} s.";
        if (ReferenceEquals(shownApproval, request)) return;
        shownApproval = request;
        ToolApprovalArguments.Text = request.Arguments;
        ToolAlwaysButton.Visibility = request.AllowAlways ? Visibility.Visible : Visibility.Collapsed;
        ToolApprovalPanel.Visibility = Visibility.Visible;
        Motion.Enter(ToolApprovalPanel, dy: 10);
    }

    private void AnswerTool(ToolApprovalChoice choice)
    {
        if (shownApproval is { } request) controller.Tools?.Answer(request, choice);
        RenderToolApproval();
    }

    private void ToolAllow_Click(object sender, RoutedEventArgs e) => AnswerTool(ToolApprovalChoice.AllowOnce);
    private void ToolAlways_Click(object sender, RoutedEventArgs e) => AnswerTool(ToolApprovalChoice.AlwaysAllow);
    private void ToolDeny_Click(object sender, RoutedEventArgs e) => AnswerTool(ToolApprovalChoice.Deny);

    // ---------- plain-language messages ----------

    internal static string ProviderRemedy(ProviderFailureCode code) => code switch
    {
        ProviderFailureCode.Authentication or ProviderFailureCode.PermissionDenied or ProviderFailureCode.CredentialUnavailable =>
            "The provider didn't accept the API key. Check the key and the account's model access in Companion.",
        ProviderFailureCode.QuotaExceeded => "The provider says the account is out of quota or credit. Check its billing and usage.",
        ProviderFailureCode.RateLimited => "The provider is limiting requests right now. Wait a moment, then try again.",
        ProviderFailureCode.ModelUnsupported or ProviderFailureCode.ModelNotFound or ProviderFailureCode.VoiceUnsupported =>
            "The provider doesn't offer that model or voice to this account. Choose another in Companion.",
        ProviderFailureCode.ModelRetired =>
            "The provider has retired this model, so it no longer answers. Choose another model in Companion › Thinking.",
        ProviderFailureCode.ConsentExpired or ProviderFailureCode.DeadlineExceeded or ProviderFailureCode.FirstDeltaTimeout or
            ProviderFailureCode.FirstAudioTimeout or ProviderFailureCode.IdleTimeout =>
            "The provider took too long to answer. Try again.",
        ProviderFailureCode.Network or ProviderFailureCode.Server => "Couldn't reach the provider. Check your connection, then try again.",
        ProviderFailureCode.OriginRejected or ProviderFailureCode.CredentialBindingMismatch or ProviderFailureCode.RedirectRejected =>
            "The provider's address didn't match the one you set up, so nothing was sent there. Review it in Companion.",
        ProviderFailureCode.OutputTokenLimit =>
            "The model used its whole reply budget (thinking models can spend it on hidden reasoning). Ask for a shorter answer, or choose a chat model in Companion.",
        _ => $"Martlet couldn't use the provider's answer ({code}). Try again."
    };

    internal static string Remedy(string code) => code switch
    {
        "conversation.canceled" => "Stopped.",
        "conversation.focus_lost" => "The recording was thrown away because the talk button lost focus. Nothing was sent.",
        "conversation.ownership_busy" => "Martlet is still finishing something else. Try again in a moment; if it stays stuck, close Martlet and open it again.",
        "conversation.setup_required" or "conversation.configuration_unsupported" =>
            "Martlet can't use its current setup. Review Thinking (and Voice or Listening) in Companion.",
        "conversation.configuration_changed" => "Your setup changed, so Martlet stopped. Try again.",
        "conversation.controls_blocked" or "conversation.locked" => "Windows is locked, so Martlet stopped.",
        "conversation.revoked" => "Stopped.",
        "conversation.input_limit" => "That's too long for Martlet's Thinking model. Try a shorter message.",
        "conversation.invalid_input" => "That message can't be sent. Use ordinary text of at most 4096 characters.",
        "conversation.expired" => "That took too long, so Martlet gave up. Try again.",
        "memory.disabled" => "Memory is off. Turn it on in Companion › Memory.",
        "memory.configuration_changed" => "Memory settings changed during the reply. Try again.",
        "memory.retrieval_invalidated" => "Memory changed while Martlet was reading it. Try again.",
        "stt.deadline_exceeded" => "Transcribing took too long. Try again, or type instead.",
        "stt.NoSpeech" => "Martlet didn't catch any words. Try again, or type instead.",
        "mic.no_speech" => "Martlet didn't hear anything, so nothing was sent.",
        "voiceid.not_enrolled" => "Only respond to my voice is on, but Voice ID isn't set up. Set it up (or turn it off) in Companion › Listening.",
        "speaker.not_user" => "Voice ID didn't recognize your voice, so nothing was sent.",
        "speaker.too_short" => "That was too short for Voice ID to recognize you, so nothing was sent.",
        "runtime.Refused" => "Martlet declined to answer that.",
        "runtime.Partial" or "runtime.Failed" or "runtime.Canceled" => "Martlet's reply didn't finish. Try again.",
        "conversation.cleanup_quarantined" or "mic.cleanup_quarantined" =>
            "Martlet couldn't confirm that the microphone or speakers were released. Close Martlet and open it again before talking.",
        "commentary.vision_unsupported" => "Your Thinking model can't see images, so vision is off. Companion › Vision says what to change.",
        "commentary.interrupted" or "commentary.stopped" => "Martlet stopped its remark.",
        _ when code.StartsWith("policy.", StringComparison.Ordinal) => "Martlet didn't reply to that.",
        _ => $"Something went wrong ({code}). Try again; Settings › Troubleshooting has the details."
    };
}
