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

public enum ChatRole { User, Martlet, Note, PcAudio }

/// <summary>One entry in the talk window's history: what you typed or said, Martlet's reply, what Martlet heard playing on the PC
/// (marked, never shown as you), or a short note.</summary>
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
    public bool IsPcAudio => Role == ChatRole.PcAudio;
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

/// <summary>The conversation: its history and the message box. It is modeless, so the rest of Martlet stays usable while it is
/// open, and optional: Home's Start listening runs it hidden, and closing it while Martlet listens or watches only hides it. How
/// Martlet listens (always or push-to-talk), whether it speaks and whether it may see (Vision) are chosen in Companion
/// and followed live; always listening starts only when you press Start listening (and stops from the same button), vision runs
/// while the conversation runs and its button pauses it, and Stop (Esc) stops Martlet's reply, any recording and vision at once
/// (listening carries on).</summary>
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
    // WhileYouTalked: a line the PC played that was heard while the microphone was still hearing or transcribing you.
    private sealed record HeardEntry(string Text, double? Confidence, HeardVoices? Voices, ChatMessage Bubble,
        BoundedWaveAudio? Recording = null, bool Pc = false, long At = 0, bool WhileYouTalked = false, ReplyTimeline? Timeline = null);
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
    private readonly List<(string Text, long At)> saidLately = [];
    private readonly List<(HeardSpeech Speech, string Text, long At)> pcHeld = [];
    private int pcEchoes;
    // Typed text waits here while an idle listen or a screen remark hands the app slot over.
    private string? pendingText;
    private ChatMessage? pendingMessage;
    private string? reloadReason;
    // The load that runs now takes a changed setup in an open conversation (said once in the log).
    private bool following;
    // What the status line says after something finished or went wrong; the next thing you do clears it.
    private string? notice;
    // The bubbles the current operation fills in.
    private LiveConversationOperation? shown;
    private ChatMessage? heard, home, reply, lastReply;
    // Vision: separate from your own turns (owned), so a glance never replaces a reply.
    private WatchSource watchSource = new(WatchKind.ActiveWindow);
    private bool watching, watchPaused, glancing, lookWanted;
    private ScreenCommentaryPacer? pacer;
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
    // Shown in plain sight while vision is on: what the latest check saw (or why it skipped), how the last look went and
    // why the pacer is holding off. Checks every 3 s never go into the history.
    private string? sight, lookNote, waitNote, captureNote, visionProblem, sentNote, attentionNote;
    private bool seeing, twinkling;
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
        await BeginAsync();
    }

    // The conversation loads once: when the window first shows, or earlier when it starts hidden (Start listening on Home).
    private bool begun, loadPending, ending;

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

    private async Task LoadAsync()
    {
        if (closed) return;
        if (operations.IsRunning)
        {
            // Another Martlet task holds settings (often right after Martlet starts): load as soon as it finishes.
            notice = Remedy("conversation.ownership_busy");
            loadPending = true;
            return;
        }
        loadPending = false;
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
            notice = "Couldn't load settings. Close this window and try again.";
        }
        else if ((await worker.Completion).Loaded is { } loaded)
        {
            controller.Configure(loaded);
            ready = loaded.Error is null;
            notice = loaded.Error?.Summary ?? (controller.Configuration is null
                ? "Set up Thinking in Companion, then come back to talk." : null);
            if (following && controller.Configuration is { } now)
                ErrorLog.Info("The open conversation follows the changed setup between replies: " +
                    string.Join(", ", now.Routes.Select(r => $"{r.Role} {r.RouteType}{(r.ModelId is { Length: > 0 } id ? " " + id : "")}")) + ".");
            following = false;
            Warm();
            StartLive();
            if (listenWhenReady && preferences.HandsFree && listenPaused && Available) Mic_Click(this, new RoutedEventArgs());
        }
        else notice = "Couldn't load settings. Close this window and try again.";
        listenWhenReady = false;
        RenderActions();
    }

    private bool listenWhenReady;

    /// <summary>Opened by Start listening in the notification-area menu: listening starts as soon as the window is ready, as if
    /// its own Start listening button were pressed.</summary>
    internal void ListenWhenReady()
    {
        if (!ready || loading is not null) listenWhenReady = true;
        else if (listenPaused) Mic_Click(this, new RoutedEventArgs());
    }

    private bool Voice => preferences.SpeakReplies && controller.Configuration is { } selected && selected.Unavailable(true, false) is null;
    // Listening uses the microphone chosen in Companion › Listening (the Windows default unless another is picked); testing it
    // there is optional, and a microphone that can't be opened says so here.
    private bool MicrophoneUsable => controller.Configuration is { } selected && selected.Unavailable(Voice, true) is null;
    private bool Available => ready && !locked && loading is null && controller.Configuration is not null;

    private bool Recording => owned is { OwnershipReleased: false, HandsFree: false } live && live.Authorization.Microphone &&
        live.Turn is null && live.Transcription is null && !live.Status.Finished;

    /// <summary>Starts what was chosen: always listening (once you pressed Start listening and listening is set up) and vision.</summary>
    private void StartLive()
    {
        if (closed || !Available) return;
        listening = preferences.HandsFree && !listenPaused && MicrophoneUsable;
        if (preferences.Watch && !watchPaused && !watching) StartWatching();
    }

    /// <summary>Follows a change made in Companion while this window is open: how you talk, whether replies are spoken and what
    /// Martlet may look at. A running listener or look keeps the options it started with, so either starts again with the new
    /// ones; turning vision on in Companion starts looking now, and switching to push-to-talk stops always listening.</summary>
    internal void UsePreferences(TalkPreferences next, string? address)
    {
        if (closed) return;
        var before = preferences;
        var addressChanged = address != videoAddress;
        preferences = next;
        videoAddress = address;
        if (before.HandsFree != next.HandsFree || before.Sensitivity != next.Sensitivity || before.PauseIndex != next.PauseIndex ||
            before.VoiceId != next.VoiceId || before.HearVoice != next.HearVoice || before.BargeIn != next.BargeIn ||
            before.ReduceEcho != next.ReduceEcho || before.WordCheck != next.WordCheck)
        {
            StopListening(keepHeard: true);
            listening = Available && next.HandsFree && !listenPaused && MicrophoneUsable;
        }
        // Hearing what this PC plays starts or stops beside the microphone, which carries on.
        if (before.HearPc != next.HearPc) StopPcListening(keepHeard: next.HearPc);
        if (!next.Watch)
        {
            watchPaused = false;
            visionProblem = null;
            if (watching) StopWatching(null);
        }
        else if (!watchPaused && (addressChanged || !before.Watch || before.ScreenScope != next.ScreenScope ||
            before.CameraId != next.CameraId || before.VideoAddress != next.VideoAddress || before.ScreenChattiness != next.ScreenChattiness))
        {
            if (watching) StopWatching(null);
            StartWatching();
            // Typing a camera address saves on each keystroke: look once it has settled.
            if (watching) nextGlance = After(ScreenCommentaryPacer.Tick);
        }
        RenderActions();
    }

    /// <summary>When Thinking runs in Ollama on this PC, has it load the model now (and follows a change of model).</summary>
    private void Warm()
    {
        var model = controller.Configuration is { LocalOllama: true } selected ? selected.Route(SetupRole.Llm).ModelId : null;
        if (warmup?.Model != model)
        {
            warmup?.Dispose();
            warmup = model is null ? null : new(model, clock, controller.DataDirectory);
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
                $"Starting local model {local.Model}… the first reply may take a moment.",
            _ when !problems => null,
            LocalModelState.NotRunning => "Start Ollama, then try again.",
            LocalModelState.MissingModel => $"Download {local.Model} in Companion › Thinking.",
            LocalModelState.Failed => $"Couldn't start {local.Model}: {status.Detail}. Choose a smaller model in " +
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
        if (loadPending)
        {
            loadPending = false;
            notice = null;
            LoadAsync().Forget();
            return;
        }
        if (reloadReason is { } reason)
        {
            reloadReason = null;
            if (Messages.Count > 0) AddNote(reason + " Martlet picked it up and carries on.");
            following = true;
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
            else if (message is not null && owned is { } sent) typed = (sent, message);
            return;
        }
        if (TryAnswer()) return;
        if (TryReport()) return;
        TryStartCommentary();
    }

    // ---------- background work (think_longer) ----------

    /// <summary>How long Martlet waits after the last thing said or answered before it brings up finished background work.</summary>
    internal static TimeSpan ReportQuiet => TimeSpan.FromSeconds(2);
    // When the conversation last did something (you spoke or sent, or a turn finished).
    private long activityAt;
    // Esc stopped a report: what it carried waits for your next message instead of coming straight back.
    private bool reportHeld;

    /// <summary>You are talking, or something you said or typed is about to be answered.</summary>
    private bool UserBusy => MicBusy || heardQueue.Count > 0 || pendingText is not null || mouseHeld || keyHeld || Recording || pcHeld.Count > 0;

    /// <summary>Brings up finished background work on Martlet's own, as soon as it is free: Thinking longer shares results as
    /// soon as Martlet is free (the default), something finished that the user didn't stop, nobody is talking or about to be
    /// answered, no reply, look or other work owns Martlet, Martlet isn't paused, and the conversation has been quiet for
    /// <see cref="ReportQuiet"/>.</summary>
    private bool TryReport()
    {
        if (closed || !Available || Paused || reportHeld || controller.Configuration?.ThinkLonger.When != ThinkDelivery.WhenFree ||
            !controller.Jobs.HasNews || UserBusy || operations.IsRunning || owned is { OwnershipReleased: false } ||
            commentary is { OwnershipReleased: false } || activityAt != 0 && clock.GetElapsedTime(activityAt) < ReportQuiet)
            return false;
        try
        {
            if (controller.StartReport(Voice) is not { } report) return false;
            owned = report;
            yielded = null;
            notice = null;
            if (report.Delivery is { } carried)
                foreach (var job in carried.Jobs) AddNote(JobNote(job));
            Observe();
            return true;
        }
        catch (LiveActionException error) when (error.Code is "conversation.ownership_busy" or "conversation.controls_blocked") { return false; }
        catch (Exception error) when (error is LiveActionException or ContractException)
        {
            ErrorLog.Warn($"Background work: couldn't bring up finished work ({(error as LiveActionException)?.Code ?? "invalid input"}).");
            reportHeld = true;
            return false;
        }
    }

    // The note above Martlet's report: which job finished and how.
    private static string JobNote(BackgroundJob job) => job.State switch
    {
        BackgroundJobState.Succeeded => $"Finished: {job.Kind.Doing.ToLowerInvariant()} “{job.Label}” ({BackgroundJobs.Clockface(job.Elapsed)}).",
        BackgroundJobState.TimedOut => $"Ran out of time {job.Kind.Doing.ToLowerInvariant()} “{job.Label}”.",
        BackgroundJobState.Canceled => $"Stopped {job.Kind.Doing.ToLowerInvariant()} “{job.Label}”.",
        _ => $"Couldn't finish {job.Kind.Doing.ToLowerInvariant()} “{job.Label}”."
    };

    // The chips of the jobs shown, by job ID.
    private readonly Dictionary<string, (DockPanel Chip, TextBlock Text, Button Cancel)> jobChips = new(StringComparer.Ordinal);

    /// <summary>The background work panel: one chip per job that runs or finished and wasn't brought up yet, with what it is
    /// about, how long it has run and Cancel; the line above says it without what the job is about (MCP reads it).</summary>
    private void RenderJobs()
    {
        var shown = controller.Jobs.Active.Concat(controller.Jobs.Undelivered.Where(job => !job.Quiet)).ToList();
        foreach (var gone in jobChips.Keys.Where(id => shown.All(job => job.Id != id)).ToArray())
        {
            JobChips.Children.Remove(jobChips[gone].Chip);
            jobChips.Remove(gone);
        }
        foreach (var job in shown)
        {
            if (!jobChips.TryGetValue(job.Id, out var chip))
            {
                var text = new TextBlock { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, FontSize = 13 };
                AutomationProperties.SetAutomationId(text, "LiveJob-" + job.Id);
                var id = job.Id;
                var cancel = new Button
                {
                    Content = "Cancel", Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
                    ToolTip = "Stop this background work. Martlet hears that you stopped it next time you talk."
                };
                AutomationProperties.SetAutomationId(cancel, "LiveJobCancel-" + job.Id);
                cancel.Click += (_, _) => { controller.CancelJob(id); RenderActions(); };
                var row = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
                DockPanel.SetDock(cancel, Dock.Right);
                row.Children.Add(cancel);
                row.Children.Add(text);
                JobChips.Children.Add(row);
                jobChips[job.Id] = chip = (row, text, cancel);
            }
            var state = job.State switch
            {
                BackgroundJobState.Waiting or BackgroundJobState.Paused or BackgroundJobState.Running when job.Progress is { } note => " · " + note,
                BackgroundJobState.Succeeded => " · done",
                BackgroundJobState.TimedOut => " · ran out of time",
                BackgroundJobState.Canceled => " · stopped",
                BackgroundJobState.Failed => " · couldn't finish",
                _ => ""
            };
            chip.Text.Text = $"{job.Kind.Doing}: {job.Label} · {BackgroundJobs.Clockface(job.Elapsed)}{state}";
            chip.Cancel.Visibility = job.Finished ? Visibility.Collapsed : Visibility.Visible;
            AutomationProperties.SetName(chip.Cancel, $"Cancel {job.Id}");
        }
        JobsPanel.Visibility = shown.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        JobsText.Text = JobsLine(shown, controller.Configuration?.ThinkLonger.When ?? ThinkLongerSettings.DefaultDelivery);
    }

    /// <summary>The background work line, without what any job is about: each job's ID, state and time, and when finished work
    /// is brought up.</summary>
    internal static string JobsLine(IReadOnlyList<BackgroundJob> jobs, ThinkDelivery when)
    {
        if (jobs.Count == 0) return "";
        var parts = jobs.Select(job => job.State switch
        {
            BackgroundJobState.Running => $"{job.Id} running for {BackgroundJobs.Clockface(job.Elapsed)}",
            BackgroundJobState.Waiting => job.Progress is { } note ? $"{job.Id} {note}" : $"{job.Id} waiting to start",
            BackgroundJobState.Paused => $"{job.Id} paused ({BackgroundJobs.Clockface(job.Elapsed)})",
            BackgroundJobState.Succeeded => $"{job.Id} done after {BackgroundJobs.Clockface(job.Elapsed)}",
            BackgroundJobState.TimedOut => $"{job.Id} ran out of time",
            BackgroundJobState.Canceled => $"{job.Id} stopped",
            _ => $"{job.Id} couldn't finish"
        });
        var finished = jobs.Any(job => job.Finished && !job.Quiet);
        return "Working in the background: " + string.Join("; ", parts) + "." + (finished
            ? when == ThinkDelivery.WhenFree ? " Martlet brings it up as soon as it's free." : " Martlet brings it up when you talk next."
            : " You can keep talking; Stop doesn't end it.");
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
                listening: microphone ? Listening(false) : null, seen: SeenNow());
            yielded = null;
            notice = null;
            answeredAt = clock.GetTimestamp();
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
        preferences.VoiceId, preferences.HearVoice, preferences.BargeIn, preferences.ReduceEcho, WordCheck: preferences.WordCheck);

    // ---------- always listening ----------

    internal static TimeSpan ListenRetry => TimeSpan.FromSeconds(2);
    /// <summary>How much longer Martlet waits for the rest when what you said sounds unfinished ("so, um", "and", a trailing comma).</summary>
    internal static TimeSpan UnfinishedPause => TimeSpan.FromMilliseconds(1500);
    // How often one set of things you said may be restarted because you kept talking (a TV in the background must not loop it).
    private const int MaximumRestarts = 3;

    // Keeps one listener running while always listening is on; it holds off by itself while Martlet speaks. With Hear what this PC
    // plays on, a second one hears the PC beside it.
    private void KeepListening()
    {
        KeepHearingPc();
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

    private bool HearingPc => listening && preferences.HearPc && controller.CanHearPc;

    private void KeepHearingPc()
    {
        if (!HearingPc || !Available || pcListener is not null || clock.GetTimestamp() < pcRetryAt) return;
        try { pcListener = controller.Listen(ListeningOptions.PcAudio with { WordCheck = preferences.WordCheck }); }
        catch (LiveActionException error)
        {
            if (error.Code is not ("conversation.ownership_busy" or "conversation.controls_blocked")) pcProblem = ListenFailure(error.Code);
            pcRetryAt = After(ListenRetry);
        }
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
        StopPcListening(keepHeard);
        if (keepHeard) return;
        saidLately.Clear();
        pcEchoes = 0;
        foreach (var entry in heardQueue) entry.Bubble.AddNote("Not answered.");
        heardQueue.Clear();
    }

    // What the PC played and nobody answered yet is simply let go: it was never said to Martlet.
    private void StopPcListening(bool keepHeard)
    {
        if (pcListener is { } live) controller.StopListening(live);
        pcListener = null;
        pcProblem = null;
        pcRetryAt = 0;
        if (keepHeard) return;
        playingQueue.Clear();
        pcHeld.Clear();
    }

    private long After(TimeSpan delay) => clock.GetTimestamp() + (long)(delay.TotalSeconds * clock.TimestampFrequency);

    // Takes what always listening made of each utterance: the words go into the history and wait for a reply. Your own words
    // come first, so what the PC played back of them is recognized before it shows.
    private void Collect()
    {
        CollectMic();
        CollectPc();
        ReleasePc();
    }

    private void CollectMic()
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

    private void CollectPc()
    {
        if (pcListener is not { } live) return;
        while (live.TryTake(out var speech)) HeardPc(speech);
        if (live.MicrophoneWorks) pcProblem = null;
        if (live.Running) return;
        pcListener = null;
        pcRetryAt = After(ListenRetry);
        if (live.Ended is "conversation.configuration_unsupported" or "conversation.setup_required") pcProblem = ListenFailure(live.Ended);
    }

    /// <summary>What the PC played, heard and transcribed: marked as the PC's in the history (never as you), joined to the PC's
    /// last line while nothing else came between, and kept to go with the next reply. It never touches Voice ID, voice
    /// recognition or memory. While the microphone is still on your words it waits (ReleasePc), and a line that repeats what
    /// you just said is your own voice played back on this PC: it is left out.</summary>
    private void HeardPc(HeardSpeech speech)
    {
        if (speech.Status.AudioFailure is { } audio && speech.Status.Code.StartsWith("mic.", StringComparison.Ordinal))
        {
            pcProblem = PcProblem(audio);
            return;
        }
        pcProblem = speech.Status.ProviderFailure is not null ? ListenOutcome(speech.Status, controller.Configuration) : null;
        // What the PC played that isn't words (music, a sound) is simply let go.
        if (speech.Ignored is not null || speech.Text?.Trim() is not { Length: > 0 } text) return;
        var now = clock.GetTimestamp();
        // In order: once one line waits for your words, the next waits behind it.
        if (MicBusy || pcHeld.Count > 0) pcHeld.Add((speech, text, now));
        else Played(speech, text, now, whileYouTalked: false);
    }

    private void Played(HeardSpeech speech, string text, long at, bool whileYouTalked)
    {
        if (RepeatsYou(text))
        {
            LeftOutYourVoice();
            return;
        }
        var bubble = Messages.Count > 0 && Messages[^1] is { IsPcAudio: true } last && last.Text.Length + text.Length < MaximumPcBubble
            ? last : Add(ChatRole.PcAudio, "", "Playing on this PC");
        bubble.Text = bubble.Text.Length == 0 ? text : bubble.Text + " " + text;
        // A very long stretch goes to Martlet by its end, so it always fits in one message.
        var kept = text.Length <= MaximumPcLine ? text : "…" + text[^(MaximumPcLine - 1)..].TrimStart();
        playingQueue.Add(new(kept, speech.Confidence, null, bubble, Pc: true, At: at, WhileYouTalked: whileYouTalked));
        pcHeardAt = at;
    }

    /// <summary>How long after the microphone heard you a line the PC plays may still be your own voice played back (both are
    /// transcribed separately, so one can come a few seconds after the other).</summary>
    internal static TimeSpan EchoWindow => TimeSpan.FromSeconds(15);
    /// <summary>How long a line the PC played waits, at most, for the microphone to finish what it is hearing from you.</summary>
    internal static TimeSpan PcHoldWait => TimeSpan.FromSeconds(8);

    // The microphone is still on something that may turn out to be the words the PC just played back: you are talking, or what
    // you said is being transcribed or waits to be taken.
    private bool MicBusy => listener is { Hearing: true } or { Transcribing: > 0 } or { HasResults: true };

    // What the PC played while you talked goes on once the microphone has your words (or it waited PcHoldWait): your own voice
    // played back is left out, the rest shows as the PC's in the order it was heard. A line let go while you still talk is
    // checked again when your words come (LeaveOutYourVoice).
    private void ReleasePc()
    {
        if (pcHeld.Count == 0 || MicBusy && clock.GetElapsedTime(pcHeld[0].At) < PcHoldWait) return;
        var busy = MicBusy;
        foreach (var (speech, text, at) in pcHeld) Played(speech, text, at, busy);
        pcHeld.Clear();
    }

    private bool RepeatsYou(string played)
    {
        saidLately.RemoveAll(said => clock.GetElapsedTime(said.At) > EchoWindow);
        return saidLately.Count > 0 && PcEcho.Repeats(played, saidLately.Select(said => said.Text));
    }

    // Your words just came: what the PC played while you talked and that repeats them leaves the queue and the history.
    private void LeaveOutYourVoice()
    {
        for (var i = playingQueue.Count - 1; i >= 0; i--)
        {
            var entry = playingQueue[i];
            if (!entry.WhileYouTalked || !RepeatsYou(entry.Text)) continue;
            playingQueue.RemoveAt(i);
            var bubble = entry.Bubble;
            var at = bubble.Text.LastIndexOf(entry.Text, StringComparison.Ordinal);
            if (at >= 0)
            {
                var rest = string.Join(" ", (bubble.Text[..at] + " " + bubble.Text[(at + entry.Text.Length)..])
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries));
                if (rest.Length == 0) Messages.Remove(bubble);
                else bubble.Text = rest;
            }
            LeftOutYourVoice();
        }
    }

    private void LeftOutYourVoice()
    {
        if (pcEchoes++ == 0)
            ErrorLog.Info("Hear what this PC plays: this PC plays your own voice back (a voice changer's or headset app's " +
                "\"hear myself\", Windows' \"Listen to this device\" or a call), so Martlet leaves your words out of what the PC played.");
    }

    // Why what the PC plays can't be heard right now (Martlet keeps trying).
    private static string PcProblem(ErrorCode code) => code switch
    {
        ErrorCode.AudioAccessDenied => "Windows is blocking Martlet from hearing this PC's sound.",
        ErrorCode.AudioDeviceUnavailable or ErrorCode.AudioDeviceLost => "This PC has no speakers or headphones to hear right now.",
        ErrorCode.AudioFormatUnsupported => "Martlet can't read the sound format of this PC's speakers.",
        _ => "Martlet can't hear what this PC plays right now."
    };

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
        // What isn't words (mm, a cough, "Thank you." made up from noise) shows only as a muted note, so you can see what was
        // let go and tune Word check.
        if (speech.Ignored is { } ignored)
        {
            ShowIgnored(ignored.Describe(speech.Text));
            return;
        }
        if (speech.Text?.Trim() is not { Length: > 0 } text)
        {
            notice = ListenOutcome(status, controller.Configuration) ?? notice;
            return;
        }
        if (speech.Interrupt is { } stop) heardInterrupt = (stop, speech.SpeechStartedAt);
        var bubble = Add(ChatRole.User, text, speech.Voices?.Speaker?.Voice is { } voice
            ? $"{voice.DisplayName}{(voice.Owner ? " (you)" : "")} (spoken)" : "You (spoken)");
        lastHeard = clock.GetTimestamp();
        activityAt = lastHeard;
        reportHeld = false;
        heardQueue.Add(new(text, speech.Confidence, speech.Voices, bubble, preferences.HearVoice ? speech.Recording : null, At: lastHeard,
            Timeline: speech.Timeline));
        saidLately.Add((text, lastHeard));
        LeaveOutYourVoice();
        notice = null;
        pacer?.NoteConversation();
    }
    // Why the microphone can't be opened right now, in words for always listening (which keeps trying it).
    private static string MicProblem(ErrorCode code) => code switch
    {
        ErrorCode.AudioAccessDenied =>
            "Windows is blocking the microphone. Allow desktop apps to use it in Windows Settings.",
        ErrorCode.AudioDeviceBusy => "Another app is using the microphone.",
        ErrorCode.AudioFormatUnsupported => "This microphone isn't supported. Choose another in Companion › Listening.",
        _ => "Martlet can't open the microphone. Check that it is connected and enabled."
    };

    private static string? ListenOutcome(LiveConversationStatus status, LiveConversationConfiguration? configuration)
    {
        if (status.Quarantined) return Remedy("conversation.cleanup_quarantined");
        if (status.ProviderFailure is { } provider)
            return configuration?.SttHostTarget() is { } host && HostRemedy(provider, host.HostId, ProviderRole.Stt) is { } remedy
                ? remedy : "Couldn't transcribe speech. " + ProviderRemedy(provider);
        return status.Code is "listen.heard" or "listen.held" or "listen.ignored" or "mic.no_speech" or "stt.NoSpeech" or "speaker.not_user" or
            "speaker.too_short" or "conversation.canceled" or "conversation.revoked" or "conversation.expired" ? null : Remedy(status.Code);
    }

    // You kept talking before Martlet answered (you are talking now, or something new was heard since the reply was asked for):
    // that reply (or remark) stops, and once you pause, everything you said is answered together. A reply that already acted
    // (Home Assistant or a tool) finishes; what you add is answered after it. Once Martlet is speaking, only talking over it
    // with real words stops it (BargeInPolicy): a quick check of what you are saying while you say it (Parakeet on this PC), or
    // what you said once you paused; never a hum, a cough, laughter, a quick "yeah", a queued word or what this PC plays (the PC
    // listener never interrupts anything).
    private void Interrupt()
    {
        var quick = listener is { TalkingOver: true } live ? live.TalkOver : null;
        var said = heardInterrupt;
        heardInterrupt = null;
        var over = quick is not null || said is not null;
        var talking = over || listener is { Hearing: true } or { Transcribing: > 0 };
        if (!talking && heardQueue.Count == 0) return;
        pacer?.NoteConversation();
        // Barge-in: you talked over a reply Martlet is already saying. It stops at once (the rest of the reply and its queued
        // audio are dropped) and what you say now is answered next, with the reply so far kept in context.
        if (preferences.BargeIn && over && owned is { OwnershipReleased: false } speaking && !ReferenceEquals(yielded, speaking) &&
            Speaking(speaking))
        {
            yielded = speaking;
            controller.Stop(speaking, "conversation.interrupted", keepContext: true);
            LogBargeIn(quick, said);
        }
        if (owned is { OwnershipReleased: false, Spoken: true } reply && answering is { } batch && !ReferenceEquals(yielded, reply) &&
            restarts < MaximumRestarts && Restartable(reply) && !Speaking(reply))
        {
            yielded = reply;
            restarts++;
            answering = null;
            Requeue(batch);
            controller.Stop(reply, "conversation.continued", keepContext: true);
        }
        if (talking && commentary is { OwnershipReleased: false } glance && !ReferenceEquals(yielded, glance) && (over || !Speaking(glance)))
        {
            yielded = glance;
            controller.Stop(glance, "commentary.interrupted", keepContext: true);
        }
        // You come first: Martlet bringing up its background work gives way when you start talking before it says anything (or
        // you talk over it); what it carried goes with what you say instead.
        if (talking && owned is { OwnershipReleased: false, Report: true } report && !ReferenceEquals(yielded, report) &&
            (over || !Speaking(report)))
        {
            yielded = report;
            controller.Stop(report, "report.interrupted", keepContext: true);
        }
    }

    /// <summary>Martlet is saying it (or may already have said some of it).</summary>
    private static bool Speaking(LiveConversationOperation operation) =>
        operation.Turn?.Snapshot is { State: ConversationState.Playing } or { MayHavePlayed: true };

    // What you said, once you paused, that stops Martlet (BargeInPolicy), and when your voice began; taken by the next Interrupt.
    private (BargeInDecision Decision, long StartedAt)? heardInterrupt;
    // The muted "Ignored ..." note the last ignored sounds went into, while nothing else came after it.
    private ChatMessage? ignoredNote;

    /// <summary>The desktop log's barge-in line: how long after you started talking over Martlet it stopped (from when your voice
    /// began to the stop), why, and how it knew (never what you said).</summary>
    private void LogBargeIn(TalkOverResult? quick, (BargeInDecision Decision, long StartedAt)? said)
    {
        var startedAt = quick?.StartedAt ?? said?.StartedAt ?? 0;
        if (startedAt == 0) return;
        var stopped = controller.Clock.GetElapsedTime(startedAt);
        ErrorLog.Info(quick is not null
            ? $"Barge-in: Martlet stopped its reply {stopped.TotalMilliseconds:0} ms after you started talking over it " +
              $"({quick.Decision.Reason}; decided {quick.After.TotalMilliseconds:0} ms in, after {quick.Checks} quick " +
              $"check{(quick.Checks == 1 ? "" : "s")} of your words; word check {preferences.WordCheck})."
            : $"Barge-in: Martlet stopped its reply {stopped.TotalMilliseconds:0} ms after you started talking over it " +
              $"({said!.Value.Decision.Reason}; from what you said once you paused; word check {preferences.WordCheck}).");
    }

    // A sound always listening ignored shows as a muted note; ignored sounds in a row share one.
    private void ShowIgnored(string line)
    {
        if (ignoredNote is not null && Messages.Count > 0 && ReferenceEquals(Messages[^1], ignoredNote) && ignoredNote.Text.Length < 300)
            ignoredNote.Text += "  " + line;
        else ignoredNote = Add(ChatRole.Note, line, "");
    }

    private static bool Restartable(LiveConversationOperation reply) =>
        reply.HomeSummary is null && reply.Status.Code != "home.asking" &&
        reply.Turn?.Snapshot is not ({ ToolCalls: > 0 } or { ActiveTool: not null });

    // Everything heard since the last reply goes to the Thinking model as one message once you pause; it decides whether to
    // answer. Waits while you are still talking or what you said is being transcribed, and a moment longer when it sounds
    // unfinished. What the PC played meanwhile goes with it, each line marked as the PC's; on its own it goes only every
    // PcPace (or once the PC goes quiet for PcLull), never while you talk.
    private bool TryAnswer()
    {
        if (listener is { } live && (live.Hearing || live.Transcribing > 0)) return false;
        // A line the PC played that may still be your own voice played back is told apart first.
        if (pcHeld.Count > 0) return false;
        if (heardQueue.Count > 0)
        {
            if (Unfinished(heardQueue[^1].Text) && clock.GetElapsedTime(lastHeard) < UnfinishedPause) return false;
        }
        else if (!PcDue()) return false;
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
        // The newest of what the PC played that fits beside your own words.
        var playing = new List<HeardEntry>();
        var pcLength = 0;
        for (var i = playingQueue.Count - 1; i >= 0; i--)
        {
            var size = playingQueue[i].Text.Length + PcLine.Length + 1;
            if (pcLength + size > MaximumPcMessage || Math.Max(length, 0) + pcLength + size > MaximumMessage) break;
            pcLength += size;
            playing.Insert(0, playingQueue[i]);
        }
        playingQueue.Clear();
        if (batch.Count == 0 && playing.Count == 0) return false;
        var everything = batch.Concat(playing).OrderBy(entry => entry.At).ToList();
        try
        {
            // Every utterance's recording together, or none when one is missing or they run too long: never half of what was said.
            // What the PC played has no recording, so a message with it goes as words only.
            var recordings = batch.Select(entry => entry.Recording).ToArray();
            var recording = preferences.HearVoice && playing.Count == 0 && recordings.All(r => r is not null)
                ? BoundedWaveAudio.Join(recordings.Select(r => r!).ToArray(), HeardGap,
                    TimeSpan.FromSeconds(BoundedTextInput.HardMaxAudioSeconds)) : null;
            // The reply's wait counts from when you last stopped talking (a copy, so a restarted reply counts from there again).
            var timeline = batch.Count > 0 ? batch[^1].Timeline?.Copy() : null;
            owned = playing.Count == 0
                ? controller.Start(string.Join(" ", batch.Select(entry => entry.Text)), Voice, microphone: false, approved: true,
                    spoken: true, heard: batch[^1].Voices, confidence: batch.Min(entry => entry.Confidence), recording: recording,
                    seen: SeenNow(), timeline: timeline)
                : controller.Start(PcMessage(everything), Voice, microphone: false, approved: true, spoken: true,
                    heard: batch.Count > 0 ? batch[^1].Voices : null,
                    confidence: batch.Count > 0 ? batch.Min(entry => entry.Confidence) : playing.Min(entry => entry.Confidence),
                    seen: SeenNow(), pcAudio: true, userWords: batch.Count > 0 ? string.Join(" ", batch.Select(entry => entry.Text)) : null,
                    timeline: timeline);
            answering = everything;
            yielded = null;
            answeredAt = clock.GetTimestamp();
            Observe();
            return true;
        }
        catch (LiveActionException error) when (error.Code == "conversation.ownership_busy")
        {
            Requeue(everything);
            return false;
        }
        catch (LiveActionException error) { notice = Remedy(error.Code); }
        catch (ContractException) { notice = Remedy("conversation.invalid_input"); }
        if (batch.Count > 0) batch[^1].Bubble.AddNote("Not answered.");
        return false;
    }

    // What waited for a reply goes back in line (a restart, or the app slot was busy).
    private void Requeue(List<HeardEntry> entries)
    {
        heardQueue.InsertRange(0, entries.Where(entry => !entry.Pc));
        playingQueue.InsertRange(0, entries.Where(entry => entry.Pc));
    }

    /// <summary>One message in the order things were heard: each line the PC played starts with
    /// <see cref="LiveConversationConfiguration.PcAudioMarker"/>, and the user's own words run together between them.</summary>
    internal static string PcMessage(IEnumerable<(string Text, bool Pc)> heard)
    {
        var lines = new List<string>();
        var own = new List<string>();
        foreach (var (text, pc) in heard)
        {
            if (!pc)
            {
                own.Add(text);
                continue;
            }
            if (own.Count > 0) lines.Add(string.Join(" ", own));
            own.Clear();
            lines.Add(PcLine + text);
        }
        if (own.Count > 0) lines.Add(string.Join(" ", own));
        return string.Join("\n", lines);
    }

    private static string PcMessage(List<HeardEntry> heard) => PcMessage(heard.Select(entry => (entry.Text, entry.Pc)));

    /// <summary>What the PC played, on its own, is offered to Martlet once PcPace passed since Martlet last answered anything
    /// (and since the oldest line waited), or once the PC has gone quiet for PcLull; never while it is still mid-sentence, and
    /// never as a second reply right after Martlet answered you.</summary>
    private bool PcDue()
    {
        if (playingQueue.Count == 0 || pcListener is { Hearing: true } or { Transcribing: > 0 }) return false;
        if (answeredAt != 0 && clock.GetElapsedTime(answeredAt) < PcPace) return false;
        return clock.GetElapsedTime(playingQueue[0].At) >= PcPace || clock.GetElapsedTime(pcHeardAt) >= PcLull;
    }

    // When Martlet was last asked to answer: what you said or typed, or what the PC played.
    private long answeredAt;
    /// <summary>At most how often what the PC played, on its own, goes to Martlet, counted from its last answer.</summary>
    internal static TimeSpan PcPace => TimeSpan.FromSeconds(20);
    /// <summary>How long the PC must stay quiet before what it played goes to Martlet sooner than PcPace.</summary>
    internal static TimeSpan PcLull => TimeSpan.FromSeconds(4);
    private static readonly string PcLine = LiveConversationConfiguration.PcAudioMarker + " ";
    // Of a message, at most this much is what the PC played (the newest), so your own words always fit.
    private const int MaximumPcMessage = 1500;
    private const int MaximumPcLine = MaximumPcMessage - 16;
    // A PC bubble in the history takes the PC's next lines until it is about this long.
    private const int MaximumPcBubble = 1200;

    private const int MaximumMessage = 4096;
    // Silence between utterances answered together, when their recordings go to a Thinking model that hears.
    private static readonly TimeSpan HeardGap = TimeSpan.FromMilliseconds(300);
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
        activityAt = clock.GetTimestamp();
        var status = done.Status;
        var code = status.Code;
        var continued = code == "conversation.continued";
        // Voice latency for every reply, in the desktop log (logs_tail and MCP's latency_report read it): how long from when you
        // stopped talking (or sent your message) to the first audio, step by step (ReplyLatency). Martlet bringing up its
        // background work on its own isn't a wait of yours, so it has no line.
        if (!done.Report && done.Turn?.Snapshot is { } finishedReply &&
            ReplyLatency.Describe(done.LatencyTimeline, done.ReplyStartedAt, done.LatencyTimeline?.Clock ?? clock, finishedReply,
                done.Authorization.Configuration.LatencyModels(done.Spoken || done.Authorization.Microphone),
                interrupted: ReferenceEquals(yielded, done) && code == "conversation.interrupted",
                passed: done.Passed, restarted: continued) is { } latency)
            ErrorLog.Info(latency);
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
                if (!string.IsNullOrEmpty(refusal) && reply.Text != refusal) reply.AddNote("Martlet declined: " + refusal);
                else if (done.Turn?.Snapshot.State is not (ConversationState.Completed or ConversationState.Refused)) reply.AddNote("Cut short.");
                else if (done.Turn?.Snapshot is { SpeechFailed: true } voiceless)
                    reply.AddNote(voiceless.MayHavePlayed ? "The voice stopped partway, so only the beginning was spoken."
                        : "The voice failed, so this wasn't spoken.");
                else if (done.Turn?.Snapshot.SpeechLimitReached == true) reply.AddNote("Only the beginning was spoken.");
            }
        }
        // Whether Thinking got the recording of what you said with the transcript (Companion › Listening). Notes go on your own
        // words, never on what the PC played.
        var asked = done.Spoken ? answering?.LastOrDefault(entry => !entry.Pc)?.Bubble
            : ReferenceEquals(shown, done) ? heard ?? (typed is { } sent && ReferenceEquals(sent.Operation, done) ? sent.Bubble : null) : null;
        if (!continued && done.VoiceSent && asked is { } said)
        {
            if (done.Turn?.Snapshot.AudioRejected == true) said.AddNote("Thinking couldn't take your recording, so it got the transcript.");
            else if (done.Turn?.Snapshot.State == ConversationState.Completed) said.AddNote("Thinking heard your voice.");
        }
        // Whether the reply saw the picture of what vision watches that went with your message.
        if (!continued)
        {
            var rejected = done.ScreenSent && done.Turn?.Snapshot.ImageRejected == true;
            var seenIt = done.ScreenSent && !rejected && done.Turn?.Snapshot.State == ConversationState.Completed;
            if (rejected) asked?.AddNote("Thinking couldn't take the picture of your screen, so it got your words only.");
            else if (seenIt && done.Seen is { } seen) asked?.AddNote($"Martlet saw {seen.Source.Label}.");
            sentNote = rejected ? $"Your message at {DateTime.Now:t} went without it: Thinking couldn't take the picture."
                : seenIt ? $"Your message at {DateTime.Now:t} went with it." : null;
            if (rejected && watching && controller.Configuration is { } selected && selected.Vision() != VisionSupport.Supported)
                StopWatching($"The Thinking model rejected the picture. {selected.VisionAdvice()}");
        }
        if (typed is { } finished && ReferenceEquals(finished.Operation, done)) typed = null;
        if (done.Spoken && !continued)
        {
            if (done.Passed) answering?.LastOrDefault(entry => !entry.Pc)?.Bubble.AddNote("Martlet stayed quiet.");
            answering = null;
            restarts = 0;
        }
        notice = Outcome(done) ?? (code is "runtime.Completed" or "listen.passed" ? null : notice);
        // The setup was changed elsewhere (Companion is usable while this window is open): load it before the next turn.
        if (code == "conversation.configuration_changed") reloadReason ??= "Your setup changed.";
    }

    /// <summary>Which job's request failed: the reply's own record, or speech-to-text for a failed transcription.</summary>
    private static ProviderRole? FailedJob(LiveConversationOperation done) =>
        done.Turn?.Snapshot is { ProviderFailure: not null } snapshot ? snapshot.FailedProvider
        : done.Status.Code.StartsWith("stt.", StringComparison.Ordinal) ? ProviderRole.Stt : null;

    private static string? Outcome(LiveConversationOperation done)
    {
        var status = done.Status;
        if (done.Authorization.CredentialFailure is { } credential) return CredentialMessages.Describe(credential);
        if (status.AudioFailure is { } audio) return AudioSetupDiagnostics.Remedy(audio) + " You can still type.";
        if ((done.Turn?.Snapshot.ProviderFailure ?? status.ProviderFailure) is { } provider)
            return ProviderRemedy(provider, done.Authorization.Configuration, FailedJob(done));
        if (status.Quarantined) return Remedy("conversation.cleanup_quarantined");
        return status.Code switch
        {
            "runtime.Completed" or "commentary.glance" or "conversation.typing" or "conversation.listening_paused" or
                "commentary.interrupted" or "conversation.interrupted" or "conversation.closed" or "mic.no_speech" or
                "listen.passed" or "conversation.continued" or "report.interrupted" => null,
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

    /// <summary>Puts the "Message Martlet" hint where typed text starts. A TextBox applies its padding inside its content host
    /// as well as through the theme's template, so the caret's own position is the only reliable offset.</summary>
    private void Input_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (InputText.Text.Length > 0) return;
        var start = InputText.GetRectFromCharacterIndex(0);
        if (start.IsEmpty) return;
        Placeholder.Margin = new Thickness(start.Left, start.Top, start.Left, 0);
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
        activityAt = clock.GetTimestamp();
        reportHeld = false;
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
            notice = "Martlet stopped its remark. Hold to talk again.";
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

    // Start listening / Stop listening.
    private void Mic_Click(object sender, RoutedEventArgs e)
    {
        notice = null;
        if (!listenPaused)
        {
            listening = false;
            listenPaused = true;
            StopListening(keepHeard: true);
        }
        else
        {
            listenRetryAt = 0;
            if (MicrophoneUsable)
            {
                pausedWith = null;
                listenPaused = false;
                listening = true;
            }
            else notice = ListeningProblem();
        }
        RenderActions();
    }

    /// <summary>Why always listening can't start, with where to fix it.</summary>
    private string ListeningProblem() =>
        controller.Configuration?.Unavailable(Voice, true) is { } why
            ? $"Martlet can't listen yet. {why} You can still type."
            : "Martlet can't listen yet. Check Companion › Listening. You can still type.";

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
            pausedWith = null;
            watchPaused = false;
            StartWatching();
        }
        RenderActions();
    }

    private void Stop_Click(object sender, RoutedEventArgs e) => StopAll("conversation.canceled");

    // ---------- Pause and Resume Martlet (the notification-area menu) ----------

    /// <summary>What Pause Martlet stopped (always listening, vision), restored by Resume; null while not paused.</summary>
    private (bool Listening, bool Watching)? pausedWith;
    internal bool Paused => pausedWith is not null;
    internal bool IsListening => listening;
    internal bool IsWatching => watching;
    /// <summary>Always listening is chosen (the window's Start listening / Stop listening button shows).</summary>
    internal bool HandsFree => preferences.HandsFree;
    /// <summary>Start listening was pressed and listening hasn't been stopped since.</summary>
    internal bool ListeningStarted => !listenPaused;
    /// <summary>Always listening is hearing you, or what you just said is still being transcribed or taken.</summary>
    internal bool HearingYou => MicBusy;

    /// <summary>Home's listening indicator: what listening is doing now, and whether that is a problem.</summary>
    internal (string Text, bool Problem) ListeningStatus
    {
        get
        {
            if (Paused) return ("Paused. Resume Martlet from its notification-area icon.", false);
            if (listenPaused)
                return listenWhenReady || loading is not null || loadPending ? ("Getting ready to listen…", false)
                    : notice is not null && (!Available || !MicrophoneUsable) ? (notice, true) : ("Not listening", false);
            if (loading is not null || loadPending || !ready && notice is null) return ("Getting ready to listen…", false);
            if (locked) return ("Windows is locked. Martlet listens again when you unlock it.", false);
            if (listening && micProblem is not null) return ($"{micProblem} Martlet keeps trying.", true);
            if (listenProblem is not null) return ($"{listenProblem} Martlet keeps trying.", true);
            if (!listening) return (ListeningProblem(), true);
            if (listener is { Hearing: true }) return ("Hearing you…", false);
            if (owned is { OwnershipReleased: false } live && !live.Status.Finished) return ("Martlet is replying…", false);
            if (listener is { Held: true }) return ("Martlet is speaking…", false);
            return ("Listening. Just start talking.", false);
        }
    }

    /// <summary>Start listening / Stop listening, as the window's own button.</summary>
    internal void ToggleListening() => Mic_Click(this, new RoutedEventArgs());

    /// <summary>Pause Martlet: stops a reply, a recording and vision like Stop, and also stops listening, until Resume.</summary>
    internal void Pause()
    {
        if (closed) return;
        pausedWith ??= (!listenPaused, preferences.Watch && !watchPaused);
        StopAll("conversation.canceled");
        listening = false;
        listenPaused = true;
        StopListening(keepHeard: false);
        watchPaused = true;
        notice = "Paused. Martlet isn't listening or looking until you resume it from its notification-area icon.";
        RenderActions();
    }

    /// <summary>Resume Martlet: listening and vision come back if Pause stopped them, as their buttons would start them.</summary>
    internal void Resume()
    {
        if (closed || pausedWith is not { } was) return;
        pausedWith = null;
        notice = null;
        if (was.Listening && preferences.HandsFree && listenPaused)
        {
            listenRetryAt = 0;
            if (MicrophoneUsable)
            {
                listenPaused = false;
                listening = true;
            }
            else notice = ListeningProblem();
        }
        if (was.Watching && preferences.Watch && !watching)
        {
            watchPaused = false;
            StartWatching();
        }
        RenderActions();
    }

    private bool ownTaskbarButton;

    /// <summary>While Martlet's main window is hidden in the notification area the talk window gets its own taskbar button, so
    /// it can't get lost behind other windows.</summary>
    internal void UseOwnTaskbarButton()
    {
        ownTaskbarButton = true;
        if (PresentationSource.FromVisual(this) is not System.Windows.Interop.HwndSource source || !AddAppWindowStyle(source.Handle) ||
            !IsVisible) return;
        // The taskbar reads the style when a window shows.
        Hide();
        Show();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (ownTaskbarButton && PresentationSource.FromVisual(this) is System.Windows.Interop.HwndSource source) AddAppWindowStyle(source.Handle);
    }

    private static bool AddAppWindowStyle(nint window)
    {
        const int GWL_EXSTYLE = -20, WS_EX_APPWINDOW = 0x40000;
        var style = GetWindowLongPtrW(window, GWL_EXSTYLE);
        if ((style & WS_EX_APPWINDOW) != 0) return false;
        SetWindowLongPtrW(window, GWL_EXSTYLE, style | WS_EX_APPWINDOW);
        return true;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern nint GetWindowLongPtrW(nint window, int index);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern nint SetWindowLongPtrW(nint window, int index, nint value);

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
        playingQueue.Clear();
        answering = null;
        restarts = 0;
        if (watching)
        {
            watchPaused = true;
            StopWatching(null);
        }
        // Stop quiets Martlet bringing up its background work too; what it carried waits for your next message. Background work
        // itself keeps going (its own Cancel stops it).
        if (owned is { OwnershipReleased: false, Report: true }) reportHeld = true;
        if (owned is not null) controller.Stop(owned, reason, keepContext);
        loading?.RequestCancellation();
        observation?.Cancel();
        Observe();
        RenderActions();
    }

    // ---------- what the window shows ----------

    /// <summary>Refresh context: Martlet forgets the exchanges it kept in mind (and its recent screen remarks), so the next
    /// reply starts fresh. Listening, vision and the messages on screen carry on unchanged.</summary>
    private void RefreshContext_Click(object sender, RoutedEventArgs e)
    {
        if (!controller.ForgetContext()) return;
        AddNote("Context refreshed. Martlet's next reply starts fresh.");
        RenderActions();
        InputText.Focus();
    }

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

        // Always listening starts and stops here (it is off when the window opens); the dot and the name say how it is going.
        MicChip.Visibility = available && preferences.HandsFree ? Visibility.Visible : Visibility.Collapsed;
        var started = !listenPaused;
        var micUsable = MicrophoneUsable;
        var micDown = listening && micProblem is not null;
        var cantListen = started ? !listening || listenProblem is not null : !micUsable;
        MicChip.IsEnabled = true;
        var micState = micDown ? "Mic unavailable" : cantListen ? "Can't listen" : listening ? "Listening" : "Not listening";
        MicText.Text = started ? "Stop listening" : micUsable ? "Start listening" : "Can't listen";
        var offer = !started && micUsable;
        if (offer != micOffer)
        {
            micOffer = offer;
            if (offer) MicChip.SetResourceReference(StyleProperty, "PrimaryButton");
            else MicChip.ClearValue(StyleProperty);
        }
        MicDot.Visibility = offer ? Visibility.Collapsed : Visibility.Visible;
        MicDot.SetResourceReference(Shape.FillProperty, micDown || cantListen ? "WarningBrush" : "SuccessBrush");
        MicChip.ToolTip = micDown ? $"{micProblem} Martlet will try again soon. Click to stop listening."
            : started && listenProblem is not null ? $"{listenProblem} Martlet will listen when it can. Click to stop listening."
            : started && !listening ? $"{ListeningProblem()} Martlet will listen when it can. Click to stop listening."
            : listening ? (listener is { Held: true }
                ? "Not listening while Martlet speaks. Click to stop listening."
                : preferences.BargeIn ? "Martlet listens for you, even while it speaks: talk over it to stop it. Click to stop listening."
                : "Martlet listens for you and answers when you pause. Click to stop listening.")
            : micUsable ? "Click to have Martlet listen and answer when you pause. You can keep using the rest of Martlet."
            : ListeningProblem();
        AutomationProperties.SetName(MicChip, micState + ". " + MicChip.ToolTip);

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
            ? $"Martlet checks {watchSource.Label}, occasionally sends one picture to the Thinking model and sends the newest " +
              "with what you type or say." +
              (noticing ? " It looks right away when a notification pops up or a taskbar button flashes." : "") +
              (lastCheck is { } checkedAt ? $" Last checked at {checkedAt:T}." : "") +
              (captureNote is { } why ? $" Full-screen capture is unavailable: {why}. Try borderless or windowed mode." : "") + " Click to stop."
            : visionProblem ?? "Click to let Martlet look again.";
        AutomationProperties.SetName(VisionChip, VisionText.Text.TrimEnd('…') + ". " + VisionChip.ToolTip);
        var visionLine = VisionLine();
        VisionStatusText.Text = visionLine;
        VisionStatusText.Visibility = VisionChip.Visibility == Visibility.Visible && visionLine.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        var pcLine = PcAudioLine();
        PcAudioText.Text = pcLine;
        PcAudioText.Visibility = available && preferences.HandsFree && pcLine.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        var (turns, contextTokens) = controller.ContextUse;
        ContextText.Text = ContextLine(turns, contextTokens, controller.Configuration?.Context, controller.LastCache);
        ContextRow.Visibility = turns > 0 ? Visibility.Visible : Visibility.Collapsed;
        // Not mid-reply or mid-glance: a finishing turn would put its exchange straight back.
        RefreshContextButton.IsEnabled = owned is not { OwnershipReleased: false } && commentary is not { OwnershipReleased: false };

        // Stop quiets Martlet; listening is paused only from its own button.
        StopButton.IsEnabled = owned is { OwnershipReleased: false } || commentary is { OwnershipReleased: false } ||
            pendingText is not null || heardQueue.Count > 0 || watching || loading is not null;
        LevelMeter.Visibility = listening ? Visibility.Visible : Visibility.Collapsed;
        Title = watching ? $"Talk with Martlet (watching {watchSource.Label})" : "Talk with Martlet";
        ResultText.Text = notice ?? Activity();
        EmptyDetail.Text = !available ? "" : listening ? "Just start talking, or type below."
            : pushToTalk ? "Type below, or hold the talk button to speak."
            : preferences.HandsFree && micUsable ? "Type below, or press Start listening to talk." : "Type a message below.";
        RenderJobs();
    }

    /// <summary>The line under the status while Hear what this PC plays is on: whether Martlet hears the PC now (every app but
    /// Martlet, or only the output you hear while it pauses for Martlet's voice), and whether it left your own voice out, or why
    /// it can't. Empty while it is off.</summary>
    private string PcAudioLine()
    {
        if (!preferences.HearPc) return "";
        if (!controller.CanHearPc) return "Martlet can't hear what this PC plays here.";
        if (!listening) return "Also hears what this PC plays once you start listening.";
        if (pcProblem is { } problem) return problem + " Martlet keeps trying.";
        if (pcListener is null) return "Getting ready to hear this PC…";
        var own = controller.PcWithoutMartlet switch
        {
            true => " (not Martlet's own voice)",
            false => controller.PcOutput is { } output ? $" on {output} (paused while Martlet speaks)" : " (paused while Martlet speaks)",
            null => ""
        };
        var line = pcListener.Hearing ? $"Hearing this PC play something{own}…" : $"Also hearing what this PC plays{own}.";
        return pcEchoes == 0 ? line
            : line + $" This PC plays your voice back too; Martlet left out {pcEchoes} {(pcEchoes == 1 ? "line" : "lines")} of it.";
    }

    /// <summary>The context row: how many exchanges Martlet keeps in mind, about how many tokens they are and the context size
    /// replies fit them into (the newest that fit are sent), and how much of the last reply's input the model read from its
    /// prompt cache, when it said.</summary>
    internal static string ContextLine(int turns, int tokens, ContextBudget? budget, (long Input, long Cached)? cache = null)
    {
        var kept = turns == 1 ? "Keeps the last exchange in mind" : $"Keeps the last {turns} exchanges in mind";
        var cached = cache is { Input: > 0 } last
            ? $" Last reply: {Math.Min(last.Cached, last.Input) * 100 / last.Input}% of its {last.Input:N0} input tokens came from the model's cache."
            : "";
        if (budget is null) return kept + "." + cached;
        return (tokens <= budget.InputTokens
            ? $"{kept}, about {tokens:N0} tokens of its {budget.Tokens:N0}-token context."
            : $"{kept}. Replies send the newest that fit its {budget.Tokens:N0}-token context.") + cached;
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
        if (heardQueue.Count > 0) return "Waiting for you to finish…";
        if (commentary is { OwnershipReleased: false }) return "Martlet is taking a look…";
        return Idle();
    }

    private string Idle() => !Available ? "" : LocalModelNote(true) ?? (listening && micProblem is not null ? "Type below; Martlet keeps trying the microphone."
        : listening && listenProblem is not null ? listenProblem
        : listening ? "Listening. Just talk, or type below." + (listenNote is null ? "" : " " + listenNote)
        : !preferences.HandsFree && MicrophoneUsable ? "Type below, or hold the talk button to speak."
        : preferences.HandsFree && listenPaused && MicrophoneUsable ? "Type below, or press Start listening to talk." : "Type a message below.");

    private string Replying(LiveConversationOperation live)
    {
        if (live.Status.Code == "tools.preparing") return "Getting tools ready…";
        var snapshot = live.Turn?.Snapshot;
        if (snapshot?.ActiveTool is { } tool)
            return controller.Tools?.PendingApproval is { Answer.IsCompleted: false } ask
                ? $"Allow {ask.Label}? Answer above." : tool == Martlet.Mcp.Client.TerminalTool.Name ? "Running a command…"
                : tool == ThinkLonger.Name ? "Starting to think it over in the background…" : $"Using {tool}…";
        if (live.Report && snapshot?.State != ConversationState.Playing) return "Martlet is bringing up what it worked on…";
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
        sight = lookNote = waitNote = captureNote = sentNote = attentionNote = null;
        seeing = false;
        lastCheck = null;
        // Watching the whole screen also notices what wants your attention: a notification, a flashing taskbar button.
        if (source.Kind == WatchKind.ActiveScreen)
        {
            attentionWatcher.Start();
            noticing = true;
        }
    }

    /// <summary>How fresh the newest picture must be to go with what you type or say.</summary>
    internal static TimeSpan SeenFreshness => TimeSpan.FromSeconds(10);
    /// <summary>How long something that wants your attention waits for Martlet to be free before it is let go.</summary>
    internal static TimeSpan AttentionPatience => TimeSpan.FromSeconds(60);

    /// <summary>While vision is on, the newest picture of what it watches goes with what you type or say, so the reply sees
    /// what you see: only a picture taken in the last <see cref="SeenFreshness"/> (a capture that was skipped, say a private
    /// window in front, leaves none).</summary>
    private SeenScreen? SeenNow()
    {
        if (!watching || latestFrame is not { } frame || clock.GetElapsedTime(latestAt) > SeenFreshness) return null;
        if (controller.Configuration?.Vision() is null or VisionSupport.Unsupported) return null;
        try
        {
            return new(frame.Encode(), watchSource.Kind == WatchKind.Url ? "" : frame.Title, watchSource);
        }
        catch (Exception error) when (error is ContractException or InvalidOperationException or NotSupportedException or
            System.Runtime.InteropServices.ExternalException)
        {
            return null;
        }
    }

    /// <summary>The vision line under the status: what the latest check saw, then the last look's outcome or why Martlet is
    /// holding off, and whether your last message went with the picture. Paused vision shows nothing (the button says so); a
    /// problem shows here unless the status line already says it. It never contains window titles.</summary>
    private string VisionLine()
    {
        if (!watching) return visionProblem is { } problem && problem != notice ? problem : "";
        if (sight is null) return $"Watching {watchSource.Label} soon.";
        if (!seeing) return sight + (lookNote is null ? "" : " " + lookNote);
        var state = commentary is { OwnershipReleased: false } glance
            ? glance.Attention is { } about ? $"Taking a look at {about.Plain}…" : "Taking a look…"
            : waitNote ?? lookNote ?? "First look soon.";
        return $"{sight} {state}" + (attentionNote is null ? "" : " " + attentionNote) + (sentNote is null ? "" : " " + sentNote);
    }

    // Runs on the UI timer: notices conversation and what wants your attention, collects finished glances and schedules the
    // next capture.
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
        // Something wants your attention: capture now, while the notification is still up.
        if (noticing && attentionWatcher.Check() is { } signal)
        {
            attention = signal;
            attentionAt = clock.GetTimestamp();
            DropAttentionFrame();
            nextGlance = attentionAt;
        }
        if (attention is not null && clock.GetElapsedTime(attentionAt) > AttentionPatience)
        {
            attention = null;
            DropAttentionFrame();
            waitNote = null;
        }
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
            var started = clock.GetTimestamp();
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
                // Nothing current to look at or to send with your messages.
                DropLatest();
                lookWanted = false;
                sight = !source.IsScreen ? result.Skip switch
                {
                    GlanceSkip.Blank => $"{char.ToUpperInvariant(source.Label[0])}{source.Label[1..]} is black. Check the lens cover or privacy shutter.",
                    _ => result.Note ?? "Couldn't read the camera this time; trying again shortly."
                } : result.Skip switch
                {
                    GlanceSkip.MartletInFront => "Only Martlet is visible.",
                    GlanceSkip.Private => "A private window is in front, so Martlet won't look.",
                    GlanceSkip.Blank when result.ProtectedContent => "Protected video is hidden by Windows.",
                    GlanceSkip.Blank when result.Note is { } why =>
                        $"The screen is black to Martlet. Try borderless or windowed mode. {why}.",
                    GlanceSkip.Blank => "The screen is black to Martlet.",
                    GlanceSkip.Minimized or GlanceSkip.NoWindow => "No window in front to look at.",
                    _ => "Couldn't capture the screen this time."
                };
                return;
            }
            sight = source.Kind == WatchKind.ActiveScreen
                ? result.Monitors > 1 ? $"Watching your whole screen ({result.Monitors} monitors)." : "Watching your whole screen."
                : !result.BehindMartlet ? $"Watching {source.Label}." : "Watching the window behind Martlet.";
            if (!twinkling) Motion.Blink(VisionDot);
            pacer.ObserveFrame(frame.Change);
            DropLatest();
            latestFrame = frame;
            latestAt = clock.GetTimestamp();
            var busy = commentary is { OwnershipReleased: false } || pendingText is not null || operations.IsRunning || Conversing;
            // Keyboard/mouse idleness means "away" only for the screen; in front of a camera people often don't type at all.
            var idle = source.IsScreen ? glancer.UserIdle : TimeSpan.Zero;
            // Something wants your attention: the first picture taken after it noticed is kept for one look as soon as
            // Martlet is free, the way a friend would say "someone's messaging you".
            if (attention is { } signal)
            {
                if (attentionFrame is null)
                {
                    // This capture started before it noticed; the next one, right away, shows it.
                    if (started < attentionAt)
                    {
                        nextGlance = clock.GetTimestamp();
                        lookWanted = false;
                        return;
                    }
                    attentionFrame = frame;
                }
                var heed = pacer.DecideAttention(busy, idle);
                if (heed == PacerVerdict.Look)
                {
                    lookAttention = signal;
                    attention = null;
                    attentionNote = null;
                    lookWanted = true;
                    waitNote = null;
                    if (!operations.IsRunning) TryStartCommentary();
                    return;
                }
                if (heed == PacerVerdict.Busy)
                {
                    lookWanted = false;
                    waitNote = $"Martlet noticed {signal.Plain} and looks once you're done talking.";
                    return;
                }
                // Not now (the hourly budget, a busy provider, an empty room, or it just looked at one): the usual pace goes on.
                attentionNote = $"Noticed {signal.Plain} at {DateTime.Now:t} but didn't look: " + heed switch
                {
                    PacerVerdict.UserAway => "you seem away.",
                    PacerVerdict.HourlyLimit => "Martlet is taking a break from looking.",
                    PacerVerdict.BackingOff => "the provider is busy.",
                    _ => "Martlet just looked at one."
                };
                attention = null;
                DropAttentionFrame();
            }
            var verdict = pacer.Decide(busy, idle);
            lookWanted = verdict == PacerVerdict.Look;
            waitNote = verdict switch
            {
                PacerVerdict.UserAway => "Waiting until you're back.",
                PacerVerdict.HourlyLimit => "Taking a break from looking.",
                PacerVerdict.BackingOff => "The provider is busy; waiting before the next look.",
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

    // The newest picture is zeroed when replaced, unless it is still kept for a look at what wants your attention.
    private void DropLatest()
    {
        if (latestFrame is { } old && !ReferenceEquals(old, attentionFrame)) old.Clear();
        latestFrame = null;
    }

    private void DropAttentionFrame()
    {
        if (attentionFrame is { } old && !ReferenceEquals(old, latestFrame)) old.Clear();
        attentionFrame = null;
    }

    private bool TryStartCommentary()
    {
        var frame = lookAttention is not null ? attentionFrame : latestFrame;
        if (!lookWanted || !watching || closed || frame is null || operations.IsRunning) return false;
        lookWanted = false;
        var about = lookAttention;
        lookAttention = null;
        try
        {
            var image = frame.Encode();
            // An address's host is not useful to the model; a camera's or window's name is.
            commentary = controller.StartCommentary(image, watchSource.Kind == WatchKind.Url ? "" : frame.Title, SavedChattiness,
                Voice, screenApproved: true, watchSource, attention: about);
            if (about is not null) pacer?.NoteAttention();
            waitNote = null;
            return true;
        }
        catch (LiveActionException error)
        {
            if (error.Code is "conversation.ownership_busy")
            {
                // Martlet got busy first: a look at what wants your attention waits for the next chance.
                if (about is not null) attention ??= about;
                return false;
            }
            StopWatching(error.Code == "commentary.vision_unsupported" ? controller.Configuration?.VisionAdvice() ?? Remedy(error.Code) : Remedy(error.Code));
            return false;
        }
        catch (Exception error) when (error is ContractException or InvalidOperationException or NotSupportedException or System.Runtime.InteropServices.ExternalException)
        {
            lookNote = "Couldn't prepare the image.";
            return false;
        }
        finally
        {
            if (about is not null && attention is null) DropAttentionFrame();
        }
    }

    private void HandleCommentary(LiveConversationOperation done)
    {
        var status = done.Status;
        var at = DateTime.Now.ToString("t") + (done.Attention is { } about ? $" ({about.Plain})" : "");
        waitNote = null;
        if (done.Passed)
        {
            pacer?.NoteLook(false);
            lookNote = $"Last look {at}: nothing to say.";
            return;
        }
        if (status.Code is "runtime.Completed")
        {
            pacer?.NoteLook(true);
            if (done.Turn?.Content.Text.Trim() is { Length: > 0 } remark) Add(ChatRole.Martlet, remark, $"Martlet, about {watchSource.Label}");
            lookNote = $"Last look {at}: commented.";
            return;
        }
        if (status.Code is "runtime.Refused" or "commentary.interrupted" or "commentary.stopped" or "conversation.canceled" or "conversation.revoked")
        {
            pacer?.NoteLook(false);
            lookNote = status.Code == "runtime.Refused" ? $"Last look {at}: no comment." : $"Last look {at}: stopped.";
            return;
        }
        var selected = controller.Configuration;
        var provider = done.Turn?.Snapshot.ProviderFailure ?? status.ProviderFailure;
        var job = FailedJob(done);
        // A busy or slow provider (NVIDIA Build's free endpoints limit requests often) is waited out, not a reason to stop vision.
        if (job != ProviderRole.Tts && provider is ProviderFailureCode.RateLimited or ProviderFailureCode.Server or
            ProviderFailureCode.Network or ProviderFailureCode.FirstDeltaTimeout or ProviderFailureCode.IdleTimeout or
            ProviderFailureCode.DeadlineExceeded && pacer is not null)
        {
            var wait = pacer!.NoteBackoff();
            lookNote = $"Last look {at}: " + (provider == ProviderFailureCode.RateLimited
                ? "the provider is limiting requests." : "the provider didn't answer.") +
                $" Looking again in {(int)wait.TotalMinutes} minute{(wait.TotalMinutes >= 2 ? "s" : "")}.";
            return;
        }
        // No automatic retry of a failing request: stop and say what to change.
        StopWatching(job == ProviderRole.Tts
            ? "Martlet stopped vision. " + ProviderRemedy(provider!.Value, done.Authorization.Configuration, job)
            : provider == ProviderFailureCode.InputLimit
            ? "The picture was too large for Thinking. If Thinking runs on a Martlet host, update the host."
            : provider is not (null or ProviderFailureCode.ModelRetired or ProviderFailureCode.ModelNotFound) &&
                selected is not null && selected.Vision() != VisionSupport.Supported
            ? $"The Thinking model rejected the picture. {selected.VisionAdvice()}"
            : "Martlet stopped vision. " + (provider is { } code ? ProviderRemedy(code, done.Authorization.Configuration, job) : Remedy(status.Code)));
    }

    /// <summary>Stops looking and frees the screen capture, camera or stream. A <paramref name="problem"/> is shown and keeps
    /// vision off until you turn it back on.</summary>
    private void StopWatching(string? problem)
    {
        watching = lookWanted = false;
        pacer = null;
        DropAttentionFrame();
        DropLatest();
        attention = lookAttention = null;
        sentNote = attentionNote = null;
        if (noticing)
        {
            attentionWatcher.Stop();
            noticing = false;
        }
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
                if (Messages.Count > 0) AddNote("Windows was locked, so Martlet stopped and started a fresh conversation.");
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
            ? "Couldn't update memory. " + MemoryFailureReason(failure, controller.Configuration)
            : string.Join("  ", report.Changes!.Select(change => change.Kind switch
            {
                MemoryCaptureKind.Remember => "Remembered: ",
                MemoryCaptureKind.Update => "Updated memory: ",
                _ => "Forgot: "
            } + change.Content + (change.Person is { } person ? $" ({person})" : "")));
        if (lastReply is not null) lastReply.AddNote(text);
        else AddNote(text);
    });

    // Raised off the dispatcher once learning names changed voices from an exchange.
    private void VoicesNamed(IReadOnlyList<Martlet.Core.Speakers.VoiceUpdateResult> learned) => Dispatcher.BeginInvoke(() =>
    {
        if (closed) return;
        var text = string.Join("  ", learned.Select(l => l.Text));
        if (lastReply is not null) lastReply.AddNote(text);
        else AddNote(text);
    });

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        // The window only shows the conversation: while Martlet listens or watches, closing it hides it and Martlet carries on.
        if (!ending && !closed && (!listenPaused || watching))
        {
            e.Cancel = true;
            Hide();
            HiddenToBackground?.Invoke();
            return;
        }
        listening = false;
        StopListening(keepHeard: false);
        StopAll("conversation.closed", keepContext: false);
        // Ending the conversation ends its background work: nothing is brought up any more.
        controller.EndBackgroundWork();
        attentionWatcher.Stop();
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
        Reveal();
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
        ToolApprovalTitle.Text = request.Title;
        ToolApprovalText.Text = $"{request.Question} Automatically denied in {left} s.";
        if (ReferenceEquals(shownApproval, request)) return;
        shownApproval = request;
        ToolApprovalArguments.Text = request.Arguments;
        ToolAlwaysButton.Visibility = request.AllowAlways ? Visibility.Visible : Visibility.Collapsed;
        ToolApprovalPanel.Visibility = Visibility.Visible;
        Motion.Enter(ToolApprovalPanel, dy: 10);
        Reveal();
    }

    /// <summary>A question that needs your answer shows the window when it runs hidden in the background.</summary>
    private void Reveal()
    {
        if (closed || IsVisible) return;
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
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

    /// <summary>The remedy for a failed reply. <paramref name="job"/> is the job whose request failed (null when unknown): a job
    /// handed to a paired Martlet host gets the host's remedy, Thinking in Ollama on this PC gets Ollama's, and a voice failure
    /// says so, since the reply's text may already be on screen.</summary>
    internal static string ProviderRemedy(ProviderFailureCode code, LiveConversationConfiguration? configuration, ProviderRole? job = null)
    {
        var host = job switch
        {
            ProviderRole.Tts => configuration?.HostSpeechTarget()?.HostId,
            ProviderRole.Stt => configuration?.SttHostTarget()?.HostId,
            _ => configuration?.HostTarget()?.HostId
        };
        if (host is not null && HostRemedy(code, host, job ?? ProviderRole.Llm) is { } hosted) return hosted;
        if (job != ProviderRole.Tts && configuration is { LocalOllama: true } local && LocalOllamaRemedy(code, local) is { } remedy) return remedy;
        return job switch
        {
            ProviderRole.Tts when code is ProviderFailureCode.ModelUnsupported or ProviderFailureCode.ModelNotFound or ProviderFailureCode.VoiceUnsupported =>
                "Martlet couldn't speak. Choose another voice in Companion › Voice.",
            ProviderRole.Tts => "Martlet couldn't speak. " + ProviderRemedy(code),
            ProviderRole.Stt => "Couldn't transcribe speech. " + ProviderRemedy(code),
            _ => ProviderRemedy(code)
        };
    }

    /// <summary>The job's process on that computer, not a provider account, is at fault when a paired Martlet host fails:
    /// the gateway answers that the worker is unavailable (stopped, still starting, or cut off from the gateway) as ModelNotFound.</summary>
    internal static string? HostRemedy(ProviderFailureCode code, string hostId, ProviderRole job)
    {
        var what = job switch
        {
            ProviderRole.Tts => "voice",
            ProviderRole.Stt => "listening",
            _ => "Thinking"
        };
        return code switch
        {
            ProviderFailureCode.ModelNotFound or ProviderFailureCode.ModelUnsupported or ProviderFailureCode.Server =>
                $"Your Martlet host {hostId} didn't answer for {what}. Try again in a minute. If it keeps happening, update or set up that host again on Devices.",
            ProviderFailureCode.Network =>
                $"Couldn't reach your Martlet host {hostId}. Check that the computer is on, then try again.",
            ProviderFailureCode.VoiceUnsupported when job == ProviderRole.Tts =>
                $"Martlet couldn't use the voice from host {hostId}. Choose a voice again in Companion › Voice.",
            ProviderFailureCode.CredentialUnavailable or ProviderFailureCode.Authentication or ProviderFailureCode.PermissionDenied =>
                $"Martlet couldn't use this PC's pairing with host {hostId}. Pair it again on Devices.",
            _ => null
        };
    }

    internal static string? LocalOllamaRemedy(ProviderFailureCode code, LiveConversationConfiguration local)
    {
        var model = local.Route(SetupRole.Llm).ModelId;
        return code switch
        {
            ProviderFailureCode.DeadlineExceeded or ProviderFailureCode.FirstDeltaTimeout or ProviderFailureCode.IdleTimeout or
                ProviderFailureCode.ConsentExpired =>
                $"Local model {model} didn't answer in time. Try again, or choose a smaller model in Companion › Thinking.",
            ProviderFailureCode.Network => "Couldn't reach Ollama. Start it, then try again.",
            ProviderFailureCode.Server => $"Couldn't run local model {model}. Choose a smaller " +
                "model in Companion › Thinking, or close programs that use a lot of memory, then try again.",
            ProviderFailureCode.ModelNotFound or ProviderFailureCode.ModelUnsupported =>
                $"Download local model {model} in Companion › Thinking.",
            ProviderFailureCode.OutputTokenLimit =>
                "The model hit the max reply length. Raise it in Companion › Replies, or ask for a shorter answer.",
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
            "The model hit the max reply length. Ask for a shorter answer, or raise the limit in Companion › Replies.",
        _ => "Martlet couldn't use the provider's answer. Try again."
    };

    /// <summary>Why remembering an exchange failed, in words: the Thinking request's own remedy or what is wrong with the store.</summary>
    internal static string MemoryFailureReason(string code, LiveConversationConfiguration? configuration) =>
        Enum.TryParse<ProviderFailureCode>(code, out var provider) && !int.TryParse(code, out _)
            ? ProviderRemedy(provider, configuration, ProviderRole.Llm)
            : code switch
            {
                "memory.Busy" or "memory.busy" or "memory.Conflict" =>
                    "Memory was in use by something else. Martlet tries again with the next thing you say.",
                "memory.AccessDenied" => "Martlet can't write to the memory folder. Choose a folder you can write to in Companion › Memory.",
                "memory.CorruptStore" or "memory.UnsupportedVersion" =>
                    "The memory store can't be read. Check it in Companion › Memory.",
                "memory.LimitExceeded" => "Memory is full. Delete some facts in Companion › Memory.",
                "memory.IoFailure" or "memory.InvalidPath" or "memory.unavailable" =>
                    "The memory folder couldn't be used. Check it in Companion › Memory.",
                "memory.clock_invalid" => "The system clock needs fixing before Martlet can save facts.",
                "runtime.Failed" or "runtime.Partial" or "runtime.Canceled" =>
                    "Thinking didn't finish its answer. Martlet tries again with the next thing you say.",
                "conversation.input_limit" => "That exchange was too long to remember.",
                _ => Remedy(code)
            };

    internal static string Remedy(string code) => code switch
    {
        "conversation.canceled" => "Stopped.",
        "conversation.focus_lost" => "The recording was thrown away because the talk button lost focus. Nothing was sent.",
        "conversation.ownership_busy" => "Martlet is still finishing something else. Try again in a moment.",
        "conversation.setup_required" or "conversation.configuration_unsupported" =>
            "Martlet can't use its current setup. Review Thinking (and Voice or Listening) in Companion.",
        "conversation.configuration_changed" => "Your setup changed, so Martlet stopped. Try again.",
        "conversation.controls_blocked" or "conversation.locked" => "Windows is locked, so Martlet stopped.",
        "conversation.revoked" => "Stopped.",
        "conversation.input_limit" => "That's too long for Martlet's Thinking model. Try a shorter message.",
        "conversation.invalid_input" => "That message can't be sent. Use shorter plain text.",
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
            "Martlet couldn't confirm that the microphone or speakers stopped. Restart Martlet before talking.",
        "commentary.vision_unsupported" => "Your Thinking model can't see images, so vision is off. Companion › Vision says what to change.",
        "commentary.interrupted" or "commentary.stopped" => "Martlet stopped its remark.",
        _ when code.StartsWith("policy.", StringComparison.Ordinal) => "Martlet didn't reply to that.",
        _ => "Something went wrong. Try again. Details are in Settings › Troubleshooting."
    };
}
