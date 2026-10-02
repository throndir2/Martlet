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
/// pause and resume them, and Stop (Esc) stops Martlet's reply, any recording and vision at once (listening carries on).</summary>
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
    // Always listening runs whenever the window is open and the microphone is set up; only the mic button pauses it (Stop and
    // Esc quiet Martlet, not the microphone). It runs beside replies (listener) and never pauses by itself: a microphone that
    // can't be opened, or listening that can't start yet (Voice ID not set up), says why and is tried again shortly.
    private bool listening, listenPaused;
    private LiveListener? listener;
    private long listenRetryAt, lastHeard;
    private string? listenNote, micProblem, listenProblem;
    // What always listening heard that waits for a reply, and what the reply in progress answers: talking on before Martlet
    // says anything restarts that reply with everything said.
    private readonly List<HeardEntry> heardQueue = [];
    private List<HeardEntry>? answering;
    private int restarts;
    private sealed record HeardEntry(string Text, double? Confidence, HeardVoices? Voices, ChatMessage Bubble);
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
    // Shown in plain sight while vision is on: what the latest check saw (or why it skipped), how the last look went and
    // why the pacer is holding off. Checks every 3 s never go into the history.
    private string? sight, lookNote, waitNote, captureNote, visionProblem;
    private bool seeing, twinkling;
    private DateTime? lastCheck;
    // Thinking in Ollama on this PC: loads the model ahead of replies while this window is open.
    private LocalOllamaWarmup? warmup;

    public ObservableCollection<ChatMessage> Messages { get; } = [];
    internal bool IsReady => ready && loading is null;
    internal LiveConversationOperation? Current => owned;
    internal LiveListener? Listener => listener;

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
        timer.Tick += (_, _) => { Observe(); Watch(); Pump(); KeepWarm(); RenderActions(); RenderToolApproval(); };
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
        StopListening(keepHeard: true);
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
            Warm();
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

    /// <summary>When Thinking runs in Ollama on this PC, has it load the model now (and follows a change of model).</summary>
    private void Warm()
    {
        var model = controller.Configuration is { LocalOllama: true } selected ? selected.Route(SetupRole.Llm).ModelId : null;
        if (warmup?.Model != model)
        {
            warmup?.Dispose();
            warmup = model is null ? null : new(model, clock);
        }
        warmup?.Touch();
    }

    /// <summary>A reply or a look on its way, or someone talking, asks Ollama again after a quiet spell, so a model it unloaded is
    /// loading already.</summary>
    private void KeepWarm()
    {
        if (warmup is not null && Available &&
            (owned is { OwnershipReleased: false } || commentary is { OwnershipReleased: false } || listener is { Hearing: true }))
            warmup.Touch();
    }

    /// <summary>What Ollama on this PC is doing with the Thinking model when it matters: still loading it, or why it can't.</summary>
    private string? LocalModelNote(bool problems)
    {
        if (warmup is not { } local) return null;
        var status = local.Status;
        return status.State switch
        {
            LocalModelState.Loading when status.Elapsed >= TimeSpan.FromSeconds(1) =>
                $"Ollama is loading {local.Model} on this PC ({status.Elapsed.TotalSeconds:0} s)… the first reply waits for it.",
            _ when !problems => null,
            LocalModelState.NotRunning => "Ollama isn't running on this PC. Start Ollama from the Start menu; Martlet notices when it answers.",
            LocalModelState.MissingModel => $"Ollama on this PC doesn't have {local.Model}. Download it in Companion › Thinking.",
            LocalModelState.Failed => $"Ollama on this PC couldn't load {local.Model}: {status.Detail}. Choose a smaller model in " +
                "Companion › Thinking, or close programs that use a lot of memory.",
            _ => null
        };
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

    // ---------- the app slot: typed text, what always listening heard, then a wanted look ----------

    // Runs on the UI timer: settles what just finished, keeps listening, then hands the app slot to whatever comes next.
    private void Pump()
    {
        if (closed) return;
        Settle();
        Collect();
        if (loading is not null || locked) return;
        KeepListening();
        Interrupt();
        if (operations.IsRunning)
        {
            if (pendingText is not null || reloadReason is not null) YieldSlot();
            return;
        }
        // The reply may have released the slot just now; settle it before anything replaces it.
        Settle();
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
        if (TryAnswer()) return;
        TryStartCommentary();
    }

    private void Settle()
    {
        if (owned is { OwnershipReleased: true } done && !ReferenceEquals(handled, done))
        {
            handled = done;
            Finished(done);
        }
    }

    // A screen remark hands the app slot over right away; a reply in progress finishes first.
    private void YieldSlot()
    {
        if (commentary is { OwnershipReleased: false } glance && !ReferenceEquals(yielded, glance))
        {
            yielded = glance;
            controller.Stop(glance, "commentary.interrupted", keepContext: true);
        }
    }

    private bool StartTurn(string? text)
    {
        bool microphone = text is null;
        try
        {
            // Pressing Send or the talk button is the action; the destinations were chosen in Companion.
            owned = controller.Start(text, Voice, microphone, approved: true, localCaptureApproved: microphone, uploadApproved: microphone,
                listening: microphone ? Listening(false) : null);
            yielded = null;
            notice = null;
            pacer?.NoteConversation();
            Observe();
            return true;
        }
        catch (LiveActionException error) { notice = Remedy(error.Code); }
        catch (ContractException) { notice = Remedy("conversation.invalid_input"); }
        catch (VoiceIdentityException error) { notice = error.Message; }
        return false;
    }

    private ListeningOptions Listening(bool handsFree) => new(handsFree,
        new VoiceActivitySettings
        {
            Sensitivity = preferences.Sensitivity,
            EndSilence = TalkPreferences.Pauses[Math.Clamp(preferences.PauseIndex, 0, TalkPreferences.Pauses.Length - 1)]
        },
        preferences.VoiceId);

    // ---------- always listening ----------

    internal static TimeSpan ListenRetry => TimeSpan.FromSeconds(2);
    /// <summary>How much longer Martlet waits for the rest when what you said sounds unfinished ("so, um", "and", a trailing comma).</summary>
    internal static TimeSpan UnfinishedPause => TimeSpan.FromMilliseconds(1500);
    // How often one set of things you said may be restarted because you kept talking (a TV in the background must not loop it).
    private const int MaximumRestarts = 3;

    // Keeps one listener running while always listening is on; it holds off by itself while Martlet speaks.
    private void KeepListening()
    {
        if (!listening || !Available || listener is not null || clock.GetTimestamp() < listenRetryAt) return;
        try
        {
            listener = controller.Listen(Listening(true));
            listenProblem = null;
        }
        catch (LiveActionException error) when (error.Code is "conversation.ownership_busy" or "conversation.controls_blocked")
        {
            listenRetryAt = After(ListenRetry);
        }
        catch (LiveActionException error) { CantListen(ListenFailure(error.Code)); }
        catch (VoiceIdentityException error) { CantListen(error.Message); }
    }

    private string ListenFailure(string code) =>
        code is "conversation.configuration_unsupported" or "conversation.setup_required" ? ListeningProblem() : Remedy(code);

    // Listening can't run right now: it says why and keeps trying, so it carries on by itself once that is fixed.
    private void CantListen(string problem)
    {
        if (problem != listenProblem) notice = problem;
        listenProblem = problem;
        listenRetryAt = After(ListenRetry);
    }

    private void StopListening(bool keepHeard)
    {
        if (listener is { } live) controller.StopListening(live);
        listener = null;
        micProblem = listenProblem = null;
        listenRetryAt = 0;
        if (keepHeard) return;
        foreach (var entry in heardQueue) entry.Bubble.AddNote("Not answered.");
        heardQueue.Clear();
    }

    private long After(TimeSpan delay) => clock.GetTimestamp() + (long)(delay.TotalSeconds * clock.TimestampFrequency);

    // Takes what always listening made of each utterance: the words go into the history and wait for a reply.
    private void Collect()
    {
        if (listener is not { } live) return;
        while (live.TryTake(out var speech)) Heard(speech);
        if (live.MicrophoneWorks) micProblem = null;
        if (live.Running) return;
        // It ended by itself: the setup changed (reloaded) or can't be used right now (said, and tried again). Anything else
        // just starts it again shortly.
        listener = null;
        listenRetryAt = After(ListenRetry);
        switch (live.Ended)
        {
            case "voiceid.not_enrolled" or "conversation.configuration_unsupported" or "conversation.setup_required":
                CantListen(ListenFailure(live.Ended));
                break;
            case "conversation.configuration_changed":
                ReloadWhenIdle("Your setup changed.");
                break;
        }
    }

    private void Heard(HeardSpeech speech)
    {
        var status = speech.Status;
        if (speech.SpeakerCheck is { } check)
            listenNote = check.Verdict != SpeakerVerdict.User && speech.Voiceprint is { } print ? VoiceIdentity.Describe(check, print.Threshold) : null;
        if (status.AudioFailure is { } audio && status.Code.StartsWith("mic.", StringComparison.Ordinal))
        {
            micProblem = MicProblem(audio);
            return;
        }
        micProblem = null;
        if (speech.Text?.Trim() is not { Length: > 0 } text)
        {
            notice = ListenOutcome(status) ?? notice;
            return;
        }
        var bubble = Add(ChatRole.User, text, speech.Voices?.Speaker?.Voice is { } voice
            ? $"{voice.DisplayName}{(voice.Owner ? " (you)" : "")} (spoken)" : "You (spoken)");
        heardQueue.Add(new(text, speech.Confidence, speech.Voices, bubble));
        lastHeard = clock.GetTimestamp();
        notice = null;
        pacer?.NoteConversation();
    }

    // Why the microphone can't be opened right now, in words for always listening (which keeps trying it).
    private static string MicProblem(ErrorCode code) => code switch
    {
        ErrorCode.AudioAccessDenied =>
            "Windows isn't letting Martlet use the microphone: check Settings › Privacy & security › Microphone, including desktop-app access.",
        ErrorCode.AudioDeviceBusy => "Another app is using the microphone exclusively.",
        ErrorCode.AudioFormatUnsupported => "The microphone's sound format isn't supported; choose another in Companion › Listening.",
        _ => "Martlet can't open the microphone chosen in Companion › Listening (unplugged, disabled or missing?)."
    };

    private static string? ListenOutcome(LiveConversationStatus status)
    {
        if (status.Quarantined) return Remedy("conversation.cleanup_quarantined");
        if (status.ProviderFailure is { } provider) return "Speech-to-text: " + ProviderRemedy(provider);
        return status.Code is "listen.heard" or "listen.held" or "mic.no_speech" or "stt.NoSpeech" or "speaker.not_user" or
            "speaker.too_short" or "conversation.canceled" or "conversation.revoked" or "conversation.expired" ? null : Remedy(status.Code);
    }

    // You kept talking before Martlet answered (you are talking now, or something new was heard since the reply was asked for):
    // that reply (or remark) stops, and once you pause, everything you said is answered together. A reply that already acted
    // (Home Assistant or a tool) finishes; what you add is answered after it.
    private void Interrupt()
    {
        var talking = listener is { Hearing: true } or { Transcribing: > 0 };
        if (!talking && heardQueue.Count == 0) return;
        pacer?.NoteConversation();
        if (owned is { OwnershipReleased: false, Spoken: true } reply && answering is { } batch && !ReferenceEquals(yielded, reply) &&
            restarts < MaximumRestarts && Restartable(reply))
        {
            yielded = reply;
            restarts++;
            answering = null;
            heardQueue.InsertRange(0, batch);
            controller.Stop(reply, "conversation.continued", keepContext: true);
        }
        if (talking && commentary is { OwnershipReleased: false } glance && !ReferenceEquals(yielded, glance))
        {
            yielded = glance;
            controller.Stop(glance, "commentary.interrupted", keepContext: true);
        }
    }

    private static bool Restartable(LiveConversationOperation reply) =>
        reply.HomeSummary is null && reply.Status.Code != "home.asking" &&
        reply.Turn?.Snapshot is not ({ ToolCalls: > 0 } or { ActiveTool: not null });

    // Everything heard since the last reply goes to the Thinking model as one message once you pause; it decides whether to
    // answer. Waits while you are still talking or what you said is being transcribed, and a moment longer when it sounds
    // unfinished.
    private bool TryAnswer()
    {
        if (heardQueue.Count == 0 || listener is { } live && (live.Hearing || live.Transcribing > 0)) return false;
        if (Unfinished(heardQueue[^1].Text) && clock.GetElapsedTime(lastHeard) < UnfinishedPause) return false;
        var batch = new List<HeardEntry>();
        var length = -1;
        for (var i = heardQueue.Count - 1; i >= 0; i--)
        {
            length += heardQueue[i].Text.Length + 1;
            if (length > MaximumMessage && batch.Count > 0) break;
            batch.Insert(0, heardQueue[i]);
        }
        foreach (var skipped in heardQueue.Take(heardQueue.Count - batch.Count)) skipped.Bubble.AddNote("Not answered.");
        heardQueue.Clear();
        try
        {
            owned = controller.Start(string.Join(" ", batch.Select(entry => entry.Text)), Voice, microphone: false, approved: true,
                spoken: true, heard: batch[^1].Voices, confidence: batch.Min(entry => entry.Confidence));
            answering = batch;
            yielded = null;
            Observe();
            return true;
        }
        catch (LiveActionException error) when (error.Code == "conversation.ownership_busy")
        {
            heardQueue.InsertRange(0, batch);
            return false;
        }
        catch (LiveActionException error) { notice = Remedy(error.Code); }
        catch (ContractException) { notice = Remedy("conversation.invalid_input"); }
        batch[^1].Bubble.AddNote("Not answered.");
        return false;
    }

    private const int MaximumMessage = 4096;
    private static readonly HashSet<string> Continuations = new(StringComparer.OrdinalIgnoreCase)
    {
        "and", "but", "or", "so", "because", "cause", "um", "uh", "er", "erm", "like", "the", "a", "an", "to", "of", "with",
        "if", "then", "that", "which", "my", "your", "is", "was", "for", "in", "on", "at"
    };

    /// <summary>What you said sounds unfinished: it trails off with a comma, dash or ellipsis, or ends on a word like "and" or "um".</summary>
    internal static bool Unfinished(string text)
    {
        var trimmed = text.TrimEnd();
        if (trimmed.Length == 0 || trimmed.EndsWith('?') || trimmed.EndsWith('!')) return false;
        if (trimmed.EndsWith(',') || trimmed.EndsWith('…') || trimmed.EndsWith("...", StringComparison.Ordinal) ||
            trimmed.EndsWith('-') || trimmed.EndsWith('\u2014') || trimmed.EndsWith(':')) return true;
        var last = trimmed.TrimEnd('.', '"', '\'').Split(' ', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        return last is not null && Continuations.Contains(last);
    }

    // Once per finished turn: the last of its text and what to tell you. Listening carries on whatever happened.
    private void Finished(LiveConversationOperation done)
    {
        Observe(done);
        var status = done.Status;
        var code = status.Code;
        var continued = code == "conversation.continued";
        if (ReferenceEquals(shown, done) && reply is not null)
        {
            // A reply restarted because you kept talking is replaced by the next one, unless you already heard some of it.
            if (continued && done.Turn?.Snapshot.MayHavePlayed != true)
            {
                Messages.Remove(reply);
                if (ReferenceEquals(lastReply, reply)) lastReply = null;
                reply = null;
            }
            else
            {
                var refusal = done.Turn?.Content.Refusal?.Trim();
                if (!string.IsNullOrEmpty(refusal) && reply.Text != refusal) reply.AddNote("Martlet then declined: " + refusal);
                else if (done.Turn?.Snapshot.State is not (ConversationState.Completed or ConversationState.Refused)) reply.AddNote("Cut short.");
                else if (done.Turn?.Snapshot.SpeechLimitReached == true) reply.AddNote("Only the start was said aloud.");
            }
        }
        if (done.Spoken && !continued)
        {
            if (done.Passed) answering?[^1].Bubble.AddNote("Martlet stayed quiet.");
            answering = null;
            restarts = 0;
        }
        notice = Outcome(done) ?? (code is "runtime.Completed" or "listen.passed" ? null : notice);
    }

    private static string? Outcome(LiveConversationOperation done)
    {
        var status = done.Status;
        if (done.Authorization.CredentialFailure is { } credential) return CredentialMessages.Describe(credential);
        if (status.AudioFailure is { } audio) return AudioSetupDiagnostics.Remedy(audio) + " You can still type.";
        if ((done.Turn?.Snapshot.ProviderFailure ?? status.ProviderFailure) is { } provider)
            return ProviderRemedy(provider, done.Authorization.Configuration);
        if (status.Quarantined) return Remedy("conversation.cleanup_quarantined");
        return status.Code switch
        {
            "runtime.Completed" or "commentary.glance" or "conversation.typing" or "conversation.listening_paused" or
                "commentary.interrupted" or "conversation.interrupted" or "conversation.closed" or "mic.no_speech" or
                "listen.passed" or "conversation.continued" => null,
            "speaker.not_user" or "speaker.too_short" or "stt.NoSpeech" when done.HandsFree => null,
            var code when code.StartsWith("policy.", StringComparison.Ordinal) && (done.HandsFree || done.Spoken) => null,
            var code => Remedy(code)
        };
    }

    // ---------- typing ----------

    private void Input_Changed(object sender, TextChangedEventArgs e)
    {
        if (InputText.Text.Length > 0) warmup?.Touch();
        RenderActions();
    }

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
            StopListening(keepHeard: true);
        }
        else
        {
            listenPaused = false;
            listenRetryAt = 0;
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

    /// <summary>Stop (Esc): Martlet's reply, any recording and vision stop right away, and what was heard but not yet answered
    /// is dropped. Always listening carries on, so nothing you say next is missed; only the mic button pauses it. The vision
    /// button turns vision back on; the conversation so far is kept unless the window closes.</summary>
    private void StopAll(string reason, bool keepContext = true)
    {
        mouseHeld = keyHeld = false;
        PttButton.ReleaseMouseCapture();
        if (pendingText is not null) pendingMessage?.AddNote("Not sent.");
        pendingText = null;
        pendingMessage = null;
        reloadReason = null;
        homeQuestion?.TrySetResult(false);
        foreach (var entry in heardQueue) entry.Bubble.AddNote("Not answered.");
        heardQueue.Clear();
        answering = null;
        restarts = 0;
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
        if (!closed) LevelMeter.Value = listener is { } live ? Math.Clamp((live.VoiceLevel + 60) / 50, 0, 1) : 0;
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
        // A reply to what always listening heard may be [pass]: nothing is shown until it clearly isn't.
        if (operation.Spoken && LiveConversationController.MaybeSilent(text)) text = "";
        if (reply is null && (text.Length > 0 || refusal.Length > 0)) reply = lastReply = Add(ChatRole.Martlet, "", "Martlet");
        if (reply is not null) reply.Text = text.Length > 0 ? text : refusal;
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
        var micDown = listening && micProblem is not null;
        var cantListen = listening && listenProblem is not null;
        MicChip.IsEnabled = true;
        MicText.Text = micDown ? "Mic unavailable" : cantListen ? "Can't listen" : listening ? "Listening" : micUsable ? "Listening paused" : "Can't listen";
        MicDot.SetResourceReference(Shape.FillProperty, micDown || cantListen ? "WarningBrush" : listening ? "SuccessBrush" : micUsable ? "MutedBrush" : "WarningBrush");
        MicChip.ToolTip = micDown ? $"{micProblem} Martlet tries the microphone again every few seconds. Click to pause listening."
            : cantListen ? $"{listenProblem} Martlet keeps trying and listens as soon as it can. Click to pause listening."
            : listening ? (listener is { Held: true }
                ? "Not listening while Martlet speaks, so it never hears itself; it listens again right after. Click to pause listening."
                : "Martlet hears you whenever you speak, even while it thinks, and decides when to answer. Click to pause listening.")
            : micUsable ? "Click to listen again." : ListeningProblem();
        AutomationProperties.SetName(MicChip, MicText.Text + ". " + MicChip.ToolTip);

        VisionChip.Visibility = available && preferences.Watch ? Visibility.Visible : Visibility.Collapsed;
        VisionChip.IsEnabled = watching || visionProblem is null;
        var looking = watching && commentary is { OwnershipReleased: false };
        VisionText.Text = looking ? "Looking…" : watching ? "Watching" : visionProblem is not null ? "Can't see" : "Vision paused";
        VisionDot.SetResourceReference(Shape.FillProperty, watching ? "SuccessBrush" : visionProblem is not null ? "WarningBrush" : "MutedBrush");
        if (looking != twinkling)
        {
            twinkling = looking;
            if (looking) Motion.Twinkle(VisionDot, 0.9);
            else
            {
                VisionDot.BeginAnimation(OpacityProperty, null);
                VisionDot.Opacity = 1;
            }
        }
        VisionChip.ToolTip = watching
            ? $"Martlet checks {watchSource.Label} every {ScreenCommentaryPacer.Tick.TotalSeconds:0} s (the dot blinks) and now and then takes a look: " +
              "one picture goes to the Thinking model, which stays quiet unless something is worth a remark." +
              (lastCheck is { } checkedAt ? $" Last checked at {checkedAt:T}." : "") +
              (captureNote is { } why ? $" Full-screen game capture is unavailable ({why}); borderless and windowed still work." : "") + " Click to stop."
            : visionProblem ?? "Click to let Martlet look again.";
        AutomationProperties.SetName(VisionChip, VisionText.Text.TrimEnd('…') + ". " + VisionChip.ToolTip);
        var visionLine = VisionLine();
        VisionStatusText.Text = visionLine;
        VisionStatusText.Visibility = VisionChip.Visibility == Visibility.Visible && visionLine.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        // Stop quiets Martlet; listening is paused only from its own button.
        StopButton.IsEnabled = owned is { OwnershipReleased: false } || commentary is { OwnershipReleased: false } ||
            pendingText is not null || heardQueue.Count > 0 || watching || loading is not null;
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
        if (listener is { Hearing: true })
            return owned is { OwnershipReleased: false, Spoken: true } ? "Hearing you… Martlet waits until you're done." : "Hearing you… pause when you're done.";
        if (owned is { OwnershipReleased: false } live && !live.Status.Finished)
        {
            if (live.Authorization.Microphone && live.Transcription is null)
                return mouseHeld || keyHeld || Recording ? "Recording… release to send." : "Transcribing…";
            return live.Status.Code == "home.asking" ? "Checking with Home Assistant…" : Replying(live);
        }
        if (listener is { Transcribing: > 0 }) return "Got it. Transcribing…";
        if (heardQueue.Count > 0) return "Listening for the rest…";
        if (commentary is { OwnershipReleased: false }) return "Martlet is taking a look…";
        return Idle();
    }

    private string Idle() => !Available ? "" : LocalModelNote(true) ?? (listening && micProblem is not null ? "Type below; Martlet keeps trying the microphone."
        : listening && listenProblem is not null ? listenProblem
        : listening ? "Listening. Just talk, or type below." + (listenNote is null ? "" : " " + listenNote)
        : !preferences.HandsFree && MicrophoneUsable ? "Type below, or hold the talk button to speak." : "Type a message below.");

    private string Replying(LiveConversationOperation live)
    {
        if (live.Status.Code == "tools.preparing") return "Getting tools ready…";
        var snapshot = live.Turn?.Snapshot;
        if (snapshot?.ActiveTool is { } tool)
            return controller.Tools?.PendingApproval is { Answer.IsCompleted: false } ask
                ? $"May Martlet use {ask.Tool}? Answer above the message box." : $"Using {tool}…";
        return snapshot?.State == ConversationState.Playing ? "Martlet is speaking. Esc stops it." : LocalModelNote(false) ??
            (live.Spoken ? "Martlet is thinking… keep talking if you're not done." : "Martlet is thinking…");
    }

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
        sight = lookNote = waitNote = captureNote = null;
        seeing = false;
        lastCheck = null;
    }

    /// <summary>The vision line under the status: what the latest check saw, then the last look's outcome or why Martlet is
    /// holding off, and the looks used this hour. Paused vision shows nothing (the button says so); a problem shows here
    /// unless the status line already says it.</summary>
    private string VisionLine()
    {
        if (!watching) return visionProblem is { } problem && problem != notice ? problem : "";
        var looks = pacer is { } p && p.LooksThisHour is > 0 and var count ? $" Looks this hour: {count} of {p.Settings.LooksPerHour}." : "";
        if (sight is null) return $"Starting to watch {watchSource.Label}.";
        if (!seeing) return sight + (lookNote is null ? "" : " " + lookNote) + looks;
        var state = commentary is { OwnershipReleased: false } ? "Taking a look now…" : waitNote ?? lookNote ?? "First look soon.";
        return $"{sight} {state}{looks}";
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
        // A reply in progress, someone talking or something heard that waits for a reply means you and Martlet are talking.
        if (Conversing) pacer.NoteConversation();
        if (glancing || clock.GetTimestamp() < nextGlance) return;
        nextGlance = clock.GetTimestamp() + (long)(ScreenCommentaryPacer.Tick.TotalSeconds * clock.TimestampFrequency);
        GlanceAsync().Forget();
    }

    private bool Conversing => owned is { OwnershipReleased: false } || heardQueue.Count > 0 ||
        listener is { Hearing: true } or { Transcribing: > 0 };

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
            lastCheck = DateTime.Now;
            seeing = result.Frame is not null;
            if (result.Frame is not { } frame)
            {
                sight = !source.IsScreen ? result.Skip switch
                {
                    GlanceSkip.Blank => $"{char.ToUpperInvariant(source.Label[0])}{source.Label[1..]} shows only black (lens covered, privacy shutter closed or the camera is off).",
                    _ => result.Note ?? "Couldn't read the camera this time; trying again shortly."
                } : result.Skip switch
                {
                    GlanceSkip.MartletInFront => "Only Martlet's own windows are in view, so there's nothing to look at.",
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
            sight = !result.BehindMartlet ? $"Watching {source.Label}."
                : source.Scope == ScreenScope.ActiveWindow ? "Watching the window behind Martlet." : "Watching your screen behind Martlet.";
            if (!twinkling) Motion.Blink(VisionDot);
            pacer.ObserveFrame(frame.Change);
            pendingFrame?.Clear();
            pendingFrame = frame;
            var busy = commentary is { OwnershipReleased: false } || pendingText is not null || operations.IsRunning || Conversing;
            // Keyboard/mouse idleness means "away" only for the screen; in front of a camera people often don't type at all.
            var verdict = pacer.Decide(busy, source.IsScreen ? glancer.UserIdle : TimeSpan.Zero);
            lookWanted = verdict == PacerVerdict.Look;
            waitNote = verdict switch
            {
                PacerVerdict.UserAway => "You seem to be away; waiting for you.",
                PacerVerdict.HourlyLimit => "Hourly look budget used up; resting.",
                PacerVerdict.AfterConversation => "You're talking; not interrupting.",
                PacerVerdict.Busy when commentary is not { OwnershipReleased: false } => "You're talking; not interrupting.",
                _ => null
            };
            if (!lookWanted) return;
            if (!operations.IsRunning) TryStartCommentary();
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
            waitNote = null;
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
            lookNote = "Couldn't prepare the image this time.";
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
        waitNote = null;
        if (done.Passed)
        {
            pacer?.NoteLook(false);
            lookNote = $"Last look at {at}: nothing worth saying.";
            return;
        }
        if (status.Code is "runtime.Completed")
        {
            pacer?.NoteLook(true);
            if (done.Turn?.Content.Text.Trim() is { Length: > 0 } remark) Add(ChatRole.Martlet, remark, $"Martlet, about {watchSource.Label}");
            lookNote = $"Last look at {at}: said something.";
            return;
        }
        if (status.Code is "runtime.Refused" or "commentary.interrupted" or "commentary.stopped" or "conversation.canceled" or "conversation.revoked")
        {
            pacer?.NoteLook(false);
            lookNote = status.Code == "runtime.Refused" ? $"Last look at {at}: the model declined to comment." : $"Last look at {at}: stopped so you could talk.";
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
            : "Martlet stopped looking: " + (provider is { } code ? ProviderRemedy(code, done.Authorization.Configuration) : Remedy(status.Code)));
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
                StopListening(keepHeard: false);
                answering = null;
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
        listening = false;
        StopListening(keepHeard: false);
        StopAll("conversation.closed", keepContext: false);
        Task.Run(glancer.Release).Forget();
        Task.Run(video.Release).Forget();
        closed = true;
        generation++;
        timer.Stop();
        warmup?.Dispose();
        warmup = null;
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

    /// <summary>The remedy for a failed reply, in Ollama's terms when Thinking runs in Ollama on this PC.</summary>
    internal static string ProviderRemedy(ProviderFailureCode code, LiveConversationConfiguration? configuration) =>
        configuration is { LocalOllama: true } local && LocalOllamaRemedy(code, local) is { } remedy ? remedy : ProviderRemedy(code);

    internal static string? LocalOllamaRemedy(ProviderFailureCode code, LiveConversationConfiguration local)
    {
        var model = local.Route(SetupRole.Llm).ModelId;
        return code switch
        {
            ProviderFailureCode.DeadlineExceeded or ProviderFailureCode.FirstDeltaTimeout or ProviderFailureCode.IdleTimeout or
                ProviderFailureCode.ConsentExpired =>
                $"Ollama on this PC didn't answer within {local.TextLimits.MaxRequestTime.TotalMinutes:0} minutes. It may still be loading " +
                $"{model}, or the model is too big for this PC's memory. Try again, or choose a smaller model in Companion › Thinking.",
            ProviderFailureCode.Network => "Couldn't reach Ollama on this PC. Start Ollama from the Start menu, then try again.",
            ProviderFailureCode.Server => $"Ollama on this PC couldn't run {model}. It may not fit this PC's memory: choose a smaller " +
                "model in Companion › Thinking, or close programs that use a lot of memory, then try again.",
            ProviderFailureCode.ModelNotFound or ProviderFailureCode.ModelUnsupported =>
                $"Ollama on this PC doesn't have {model}. Download it in Companion › Thinking.",
            ProviderFailureCode.OutputTokenLimit =>
                "The model used the whole max reply length you set (thinking models spend part of it on hidden reasoning). " +
                "Raise it, or clear it for no limit, in Companion › Replies.",
            _ => null
        };
    }

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
