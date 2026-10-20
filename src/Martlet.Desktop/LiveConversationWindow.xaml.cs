using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Shapes;
using System.Windows.Threading;
using Martlet.Audio;
using Martlet.Conversation;
using Martlet.Core.Reading;
using Martlet.Core.Settings;
using Martlet.Providers;

namespace Martlet.Desktop;

/// <summary>The conversation: its history and the message box. It is modeless, so the rest of Martlet stays usable while it is
/// open, and optional: Home's Start listening and Start watching run it hidden, and closing it while Martlet listens or watches
/// only hides it. How Martlet listens (always or push-to-talk), whether it speaks and whether it may see (Vision) are chosen in
/// Companion and followed live; always listening starts only when you press Start listening and watching only when you press
/// Start watching (each stops from its own button, here, on Home or in the notification-area menu), and Stop (Esc) stops
/// Martlet's reply, any recording and watching at once (listening carries on).</summary>
public partial class LiveConversationWindow : ThemedWindow
{
    internal SupportController? Support { get; init; }
    /// <summary>Where the character looks: each new screenshot of your screen goes to it while Martlet decides.</summary>
    internal CharacterGazeService? Gaze { get; init; }
    private readonly LiveSupportProjection supportProjection = new();
    private readonly ISetupService settings;
    private readonly SetupOperationRunner operations;
    private readonly LiveConversationController controller;
    private readonly IAudioSessionEvents sessionEvents;
    private readonly TimeProvider clock;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly IScreenGlancer glancer;
    private readonly IVideoInput video;
    private TalkPreferences preferences;
    // A camera address as typed in Companion, password included; it lives only as long as the app.
    private string? videoAddress;
    private SetupOperation? loading;
    private CancellationTokenSource? observation;
    private LiveConversationOperation? owned, handled, yielded;
    private bool closed, ready, mouseHeld, keyHeld, follow = true;
    private volatile bool locked;
    private long generation;
    // Always listening starts when you press Start listening (never just because the window opened) and runs until you press
    // Stop listening (Stop and Esc quiet Martlet, not the microphone). It runs beside replies (listener) and never stops by
    // itself: a microphone that can't be opened, or listening that can't start yet (Voice ID not set up), says why and is tried
    // again shortly; a reload or unlocking Windows resumes it.
    private bool listening, listenPaused = true;
    // Whether Start listening shows as the primary button (null until first shown).
    private bool? micOffer;
    private LiveListener? listener;
    private long listenRetryAt, lastHeard;
    private string? listenNote, micProblem, listenProblem;
    // What always listening heard that waits for a reply, and what the reply in progress answers: talking on before Martlet
    // says anything restarts that reply with everything said.
    private readonly List<HeardEntry> heardQueue = [];
    private List<HeardEntry>? answering;
    private int restarts;
    // WhileYouTalked: a line the PC played that was heard while the microphone was still hearing or transcribing you. Words: it
    // goes straight to Thinking as the Recording alone, and its words (Text stays empty) come from speech-to-text beside the reply.
    // From: where a line the PC played came from (PcActivityMonitor), when Martlet can tell. Yours: the microphone heard a voice
    // Martlet knows as yours (voice recognition's owner, or Voice ID). SpokeStart and SpokeEnd: when the microphone's voice began
    // and its recording ended (controller clock; 0 when unknown). Voice: the audio model's words about how you sounded (VoiceNote).
    private sealed record HeardEntry(string Text, double? Confidence, HeardVoices? Voices, ChatMessage Bubble,
        BoundedWaveAudio? Recording = null, bool Pc = false, long At = 0, bool WhileYouTalked = false, ReplyTimeline? Timeline = null,
        SpokenWords? Words = null, PcHeardFrom? From = null, bool Yours = false, long SpokeStart = 0, long SpokeEnd = 0,
        VoiceNote? Voice = null);
    // Bubbles of what went straight to Thinking, waiting for their words (FollowWords).
    private readonly List<(SpokenWords Words, ChatMessage Bubble, bool Yours)> awaitingWords = [];
    /// <summary>What a bubble shows for what went straight to Thinking until its words come.</summary>
    internal const string StraightPlaceholder = "(your voice; transcribing…)";
    // Hearing what this PC plays (Companion › Listening › Hear what this PC plays): a second listener beside the microphone
    // while always listening runs. What it hears waits here, goes with the next thing you say, and on its own is offered to
    // Martlet at most every PcPace (or once the PC goes quiet), so a video never floods Thinking or interrupts you.
    private LiveListener? pcListener;
    private long pcRetryAt, pcHeardAt;
    private string? pcProblem;
    private readonly List<HeardEntry> playingQueue = [];
    // Your own voice played back on this PC (PcEcho): what the microphone heard you say lately is kept here (for EchoWindow),
    // and what the PC played while the microphone was still hearing or transcribing you waits in pcHeld (at most PcHoldWait),
    // so a line that is only your voice played back is left out (pcEchoes counts them) rather than shown and answered twice.
    // The other way round, what a video, show, game or music played lately (playedLately) tells the microphone hearing this PC's
    // speakers apart from you (speakersLeftOut counts what it left out).
    private readonly List<(string Text, long At, bool Yours)> saidLately = [];
    private readonly List<(HeardSpeech Speech, string Text, long At, PcHeardFrom From)> pcHeld = [];
    private readonly List<(PcPlayedLine Line, long At)> playedLately = [];
    private int pcEchoes, speakersLeftOut;
    // Where the PC bubble at the end of the history came from: the PC's next line joins it only from the same place.
    private string? pcBubbleWhere;
    // Typed text waits here while an idle listen or a screen remark hands the app slot over.
    private string? pendingText;
    private ChatMessage? pendingMessage;
    // Messages from paired chats in messaging apps (Companion › Messaging) wait here for the app slot, in order; each gets the
    // reply's text (or why there is none) once its turn finishes.
    private sealed record RemoteAsk(string Text, ChatMessage Bubble, bool Speak, TaskCompletionSource<string> Reply,
        Martlet.Conversation.HistorySource? Origin = null, string? Speaker = null);
    private readonly List<RemoteAsk> remoteQueue = [];
    private (LiveConversationOperation Operation, RemoteAsk Ask)? remoteAnswering;
    private string? reloadReason;
    // The load that runs now takes a changed setup in an open conversation (said once in the log).
    private bool following;
    // What the status line says after something finished or went wrong; the next thing you do clears it.
    private string? notice;
    // The bubbles the current operation fills in.
    private LiveConversationOperation? shown;
    private ChatMessage? heard, home, reply, lastReply;
    // Vision: separate from your own turns (owned), so a glance never replaces a reply. Like listening, watching starts only when
    // you press Start watching (never just because the window opened or vision is on in Companion) and runs until Stop watching,
    // Stop (Esc) or Pause; locking Windows or a reload stops it for a moment and it carries on afterwards. Something that keeps
    // Martlet from seeing (a text-only model, no camera chosen, a failing look) says why and leaves it stopped.
    private WatchSource watchSource = new(WatchKind.ActiveWindow);
    private bool watching, watchPaused = true, glancing, lookWanted;
    private ScreenCommentaryPacer? pacer;
    // What the model saw at Martlet's last look (lookedLast), and at the look under way until its answer comes (lookedNow): while
    // nothing changed since, Martlet doesn't look again, and each look tells the model how much changed (LookChange).
    private LookedPicture? lookedLast, lookedNow;
    // The newest picture (it also goes with what you type or say while it is fresh) and when it was taken; a skipped capture
    // clears it, so an old picture never stands in for what is on screen now.
    private ScreenFrame? latestFrame;
    private long latestAt;
    // Something on screen wants your attention (a notification, a flashing taskbar button) while Martlet watches the whole
    // screen: the picture taken right after it is kept for one look as soon as Martlet is free.
    private readonly IScreenAttention attentionWatcher;
    private bool noticing;
    private AttentionSignal? attention, lookAttention;
    private ScreenFrame? attentionFrame;
    private long attentionAt;
    private LiveConversationOperation? commentary, handledCommentary;
    private long nextGlance;
    // The vision line while vision is on: what the latest check saw (or why it skipped) and why the pacer is holding off; how
    // the last look went (lookFailed: shown on the line too) and what wanted your attention go in its tooltip. Checks every 3 s
    // never go into the history.
    private string? sight, lookNote, waitNote, captureNote, visionProblem, attentionNote;
    // Companion › Reading: reads the text on the screen (never a camera) off the UI thread at its own pace (Read the screen); what
    // it read last (or why it couldn't) goes in the vision line's tooltip and the newest text goes with a glance. readingFile:
    // when the Reading list was written, so a new list takes effect at once (looked at no more than once a capture tick,
    // readingChecked). fedRead: the read whose text change the pacer heard of last.
    private ScreenReader? reader;
    private DateTime readingFile;
    private long readingChecked;
    private ScreenRead? fedRead;
    private string? readNote;
    // Companion › Vision › Screen summary over time: the recent pictures that changed and the background summaries of them.
    private readonly ScreenDigester digest;
    private bool wasHearing;
    private bool seeing, twinkling, lookFailed;
    // How many monitors the whole screen spans at the latest check, for the vision line's tooltip.
    private int monitors;
    private DateTime? lastCheck;
    // The bubble of the message you typed that the current reply answers (for its "Martlet saw your screen" note).
    private (LiveConversationOperation Operation, ChatMessage Bubble)? typed;
    // Thinking in Ollama on this PC: loads the model ahead of replies while this window is open.
    private LocalOllamaWarmup? warmup;

    public ObservableCollection<ChatMessage> Messages { get; } = [];
    internal bool IsReady => ready && loading is null;
    internal LiveConversationOperation? Current => owned;
    internal LiveListener? Listener => listener;
    internal LiveListener? PcListener => pcListener;

    internal LiveConversationWindow(ISetupService settings, SetupOperationRunner operations, LiveConversationController controller,
        IAudioSessionEvents sessionEvents, TimeProvider? clock = null, VoiceIdentity? voiceIdentity = null,
        IScreenGlancer? glancer = null, IVideoInput? video = null, TalkPreferences? preferences = null, string? videoAddress = null,
        IScreenAttention? attention = null)
    {
        this.settings = settings;
        this.operations = operations;
        this.controller = controller;
        this.sessionEvents = sessionEvents;
        this.clock = clock ?? TimeProvider.System;
        this.glancer = glancer ?? new ScreenGlancer();
        attentionWatcher = attention ?? new ScreenAttention(this.clock);
        this.video = video ?? new VideoInput(controller.Home is { } home ? home.CameraAuthorization : null);
        this.preferences = preferences ?? TalkPreferences.Load(voiceIdentity?.DataDirectory);
        this.videoAddress = videoAddress;
        digest = new(controller.ScreenDigestThinker, controller.ScreenDigestBoard, this.clock,
            prompts: () => controller.Configuration?.Prompts);
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
        controller.AfterExchangeFailed += AfterExchangeFailed;
        controller.PictureShown += PictureShown;
        controller.VoicesNamed += VoicesNamed;
        controller.ChattinessDecided += ChattinessDecided;
        controller.TouchesSent += TouchesSent;
        if (controller.Home is { } smartHome) smartHome.Confirm = ConfirmHomeAsync;
        RenderActions();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        Motion.Sway(TalkMascot, 3, 4);
        InputText.Focus();
        StartFixtureTask();
        await BeginAsync();
    }

    // The conversation loads once: when the window first shows, or earlier when it starts hidden (Start listening on Home).
    private bool begun, loadPending, ending;
    // The situation route (While gaming, Host away) the conversation used at its last load, for the log line when it changes.
    private string? situationKey;

    private Task BeginAsync()
    {
        if (begun) return Task.CompletedTask;
        begun = true;
        return LoadAsync();
    }

    /// <summary>Runs the conversation without showing the window: Home's Start listening, the notification-area menu and
    /// starting with Martlet use it. Showing the window later only shows the history; it doesn't start again.</summary>
    internal void StartInBackground()
    {
        if (closed) return;
        new System.Windows.Interop.WindowInteropHelper(this).EnsureHandle();
        BeginAsync().Forget();
    }

    /// <summary>Shows the window in front (Show conversation, the notification-area menu's Talk). It is a window of its own, so
    /// Martlet's window can cover it; this brings it back over it even when Windows doesn't let Martlet take the focus.</summary>
    internal void ShowInFront()
    {
        if (closed) return;
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Topmost = true;
        Topmost = false;
        Activate();
    }

    /// <summary>Raised when the close button hid the window because the conversation keeps listening or watching.</summary>
    internal event Action? HiddenToBackground;

    /// <summary>Ends the conversation and closes the window, even while it listens (End the conversation, exiting Martlet).</summary>
    internal void End()
    {
        ending = true;
        Close();
    }

    /// <summary>Picks up who does what after it changed elsewhere (a synced change or a failover) as soon as Martlet is free.</summary>
    internal void ReloadWhenIdle(string reason)
    {
        if (closed) return;
        reloadReason = reason;
        Pump();
    }

    /// <summary>Picks up a situation's Thinking route (While gaming, Host away) once nothing runs and nobody talks: unlike
    /// <see cref="ReloadWhenIdle"/>, it never lets a reply started early or a remark go, and never stops what you say.</summary>
    internal void ReloadWhenQuiet(string reason)
    {
        if (closed) return;
        situationReason = reason;
        Pump();
    }

    private string? situationReason;

    // ---------- the history ----------

    /// <summary>What the conversation calls the character: the name of the persona Martlet uses (Companion › Personality), or
    /// Martlet without one. The title, the header, the message box and the history (each reply's label and notes such as "You
    /// touched Ivy") say it. Status lines MCP reads (LiveStatus, Home's listening line, the touch lines) keep saying Martlet,
    /// so MCP never returns the character's name.</summary>
    internal string CharacterName { get; private set; } = Martlet.Core.Speakers.CompanionNames.Default;

    /// <summary>Calls the character by <paramref name="persona"/>'s name from now on (each time the settings load). Messages
    /// already in the history keep the name they were shown with.</summary>
    private void ShowCharacter(string? persona)
    {
        CharacterName = Martlet.Core.Speakers.CompanionNames.Character(persona);
        CharacterNameText.Text = CharacterName;
        Placeholder.Text = "Message " + CharacterName;
        AutomationProperties.SetName(InputText, Placeholder.Text);
        EmptyTitle.Text = "Say hi to " + CharacterName;
    }

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

}
