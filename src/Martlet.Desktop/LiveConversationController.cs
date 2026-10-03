using System.IO;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using Martlet.Audio;
using Martlet.Conversation;
using Martlet.Core.Contracts;
using Martlet.Core.Lorebooks;
using Martlet.Core.Settings;
using Martlet.Participation;
using Martlet.Providers;
using Martlet.Memory;

namespace Martlet.Desktop;

internal sealed record LiveConversationStatus(string Code, bool Finished = false, bool Quarantined = false,
    PolicyReason? Policy = null, ProviderFailureCode? ProviderFailure = null, ErrorCode? AudioFailure = null);

// HandsFree: voice activity endpoints each utterance. RequireVoiceId: only the enrolled voice is uploaded. Hear: the recording
// is kept for a Thinking model that hears (Companion › Listening › Let Thinking hear my voice). BargeIn: keep listening while
// Martlet speaks, so talking over a reply (a sustained voice on the microphone, TalkOverDetector) stops it. ReduceEcho: what
// the PC plays (Martlet's voice included) is removed from the
// microphone first (Companion › Listening › Reduce echo from my speakers), so speakers work without headphones. Pc: listens to
// what this PC plays instead of the microphone (Companion › Listening › Hear what this PC plays): never Voice ID, voice
// recognition, a recording for Thinking or memory.
internal sealed record ListeningOptions(bool HandsFree, VoiceActivitySettings Activity, bool RequireVoiceId, bool Hear = false,
    bool BargeIn = false, bool ReduceEcho = false, bool Pc = false)
{
    internal static TimeSpan IdleRestart => TimeSpan.FromSeconds(12);
    internal static TimeSpan MinimumUtterance => TimeSpan.FromMilliseconds(450);

    /// <summary>Listening to what this PC plays: the default voice activity (a video's sound, not the user's microphone).</summary>
    internal static ListeningOptions PcAudio { get; } = new(true, new VoiceActivitySettings(), RequireVoiceId: false, Pc: true);
}

/// <summary>The newest picture of what vision watches (taken at most a few seconds earlier), sent along with what the user
/// types or says while vision is on, so the reply sees what they see. <paramref name="Title"/> is the active window's title or
/// the camera's name (empty for an address).</summary>
internal sealed record SeenScreen(BoundedImage Image, string Title, WatchSource Source)
{
    /// <summary>What the picture shows, for the Screen with your message prompt.</summary>
    internal string Describe()
    {
        var title = new string(Title.Where(c => !char.IsControl(c) && c != '"').Take(80).ToArray()).Trim();
        return Source.Kind switch
        {
            WatchKind.ActiveWindow => title.Length > 0 ? $"the user's active window (\"{title}\")" : "the user's active window",
            WatchKind.ActiveScreen => "the user's whole screen: every monitor, with the taskbar and any pop-up notifications" +
                (title.Length > 0 ? $" (active window: \"{title}\")" : ""),
            WatchKind.Camera => title.Length > 0 ? $"what the user's camera \"{title}\" sees" : "what the user's camera sees",
            _ => "what the user's phone or network camera sees"
        };
    }

    public override string ToString() => nameof(SeenScreen);
}

internal sealed class LiveConversationOperation
{
    private readonly object gate = new();
    private readonly Queue<string> timeline = new();
    private LiveConversationStatus status = new("conversation.authorizing");
    private CaptureRun? capture;
    private ConversationTurn? turn;
    private int releasedPress;
    private int executionFinished;
    private string? cancellationReason;
    private TranscriptionWindow? transcriptionWindow;
    private sealed record TranscriptionWindow(TimeProvider Clock, long Timestamp, DateTimeOffset Deadline, TimeSpan Duration);
    internal ConversationAuthorization Authorization { get; }
    internal SetupOperation Worker { get; set; } = null!;
    internal CancellationToken OriginalCaller { get; }
    internal Guid Id { get; } = Guid.NewGuid();
    internal LiveConversationStatus Status => Volatile.Read(ref status);
    internal CaptureRun? Capture => Volatile.Read(ref capture);
    internal ConversationTurn? Turn => Volatile.Read(ref turn);
    internal TranscriptionResult? Transcription { get; set; }
    [JsonIgnore] internal string? Transcript { get; set; }
    /// <summary>What the user said, kept only while Thinking may hear it (never saved); null otherwise.</summary>
    [JsonIgnore] internal BoundedWaveAudio? Recording { get; set; }
    /// <summary>The reply's request carried the user's recording with the transcript.</summary>
    internal bool VoiceSent { get; set; }
    /// <summary>The picture of what vision watches that goes with the user's message, when vision is on.</summary>
    [JsonIgnore] internal SeenScreen? Seen { get; init; }
    /// <summary>The reply's request carried <see cref="Seen"/>'s picture.</summary>
    internal bool ScreenSent { get; set; }
    internal Guid? PersonaRevision { get; set; }
    internal ResponseStyle? ResponseStyle { get; set; }
    internal int ContextMessages { get; set; }
    internal int ContextMessagesOmitted { get; set; }
    internal bool MemoryRequested { get; set; }
    internal int MemoryFactsUsed { get; set; }
    internal int MemoryFactsOmitted { get; set; }
    internal long? MemoryStoreRevision { get; set; }
    /// <summary>Why memory could not be read for this turn (the reply went ahead without it).</summary>
    internal string? MemoryProblem { get; set; }
    internal int LoreEntriesUsed { get; set; }

    /// <summary>Triggered entries left out by the lorebook budget or to fit the request.</summary>
    internal int LoreEntriesOmitted { get; set; }
    internal IReadOnlyList<string> LoreTitles { get; set; } = [];
    /// <summary>Why the lorebooks could not be read for this turn (the reply went ahead without them).</summary>
    internal string? LoreProblem { get; set; }
    /// <summary>What Home Assistant did or answered for this turn, shown above the reply. Never logged or saved.</summary>
    [JsonIgnore] internal string? HomeSummary { get; set; }
    /// <summary>The MCP tools offered to this turn's reply, if any.</summary>
    internal DesktopToolset? Toolset { get; set; }
    internal ListeningOptions? Listening { get; init; }
    /// <summary>One utterance recorded by always listening (<see cref="LiveListener"/>): capture and speech-to-text only.</summary>
    internal bool Listen { get; init; }
    /// <summary>A reply to what always listening heard: the model may stay quiet ([pass]) when it wasn't meant for it.</summary>
    internal bool Spoken { get; init; }
    internal double? SpokenConfidence { get; init; }
    /// <summary>The message includes lines heard from what the PC plays (each starts with
    /// <see cref="LiveConversationConfiguration.PcAudioMarker"/>): no tools or Home Assistant without the user's own words, and
    /// memory and learning names read only <see cref="UserWords"/>.</summary>
    internal bool PcAudio { get; init; }
    /// <summary>What the user said themselves in a message with <see cref="PcAudio"/>; null when it is only what the PC played.</summary>
    [JsonIgnore] internal string? UserWords { get; init; }
    private int hearing;
    /// <summary>Speech longer than a cough or click is being recorded right now.</summary>
    internal bool Hearing { get => Volatile.Read(ref hearing) != 0; set => Volatile.Write(ref hearing, value ? 1 : 0); }
    private int talkingOver;
    /// <summary>The microphone has heard the user's own voice for long enough to stop Martlet talking
    /// (<see cref="TalkOverDetector"/>): never a short sound, and never what this PC plays.</summary>
    internal bool TalkingOver { get => Volatile.Read(ref talkingOver) != 0; set => Volatile.Write(ref talkingOver, value ? 1 : 0); }
    /// <summary>Frame by frame, whether this utterance's sound was what the speakers played (echo reduction only).</summary>
    [JsonIgnore] internal EchoTimeline? Echo { get; set; }
    internal Voiceprint? Voiceprint { get; init; }
    internal SpeakerCheck? SpeakerCheck { get; set; }
    /// <summary>Who is being recognized in this utterance (runs alongside speech-to-text).</summary>
    internal Task<HeardVoices>? Recognition { get; set; }
    /// <summary>Who spoke, once known; null when voice recognition is off or did not finish in time.</summary>
    [JsonIgnore] internal HeardVoices? Heard { get; set; }
    /// <summary>An unprompted screen glance rather than a reply to the user.</summary>
    internal bool Commentary { get; init; }
    /// <summary>A glance taken right away because something wanted the user's attention (a notification, a flashing taskbar button).</summary>
    [JsonIgnore] internal AttentionSignal? Attention { get; init; }
    /// <summary>The glance ended in silence: the model answered [pass].</summary>
    internal bool Passed { get; set; }
    private double voiceLevel = -100;
    internal double VoiceLevel { get => Volatile.Read(ref voiceLevel); set => Volatile.Write(ref voiceLevel, value); }
    internal bool HandsFree => Listening?.HandsFree == true;
    internal bool OwnershipReleased => Worker.Completion.IsCompleted;
    internal bool ExecutionFinished => Volatile.Read(ref executionFinished) != 0;
    internal void FinishExecution() => Interlocked.Exchange(ref executionFinished, 1);
    internal void BeginTranscription(TimeProvider clock, DateTimeOffset deadline) =>
        Volatile.Write(ref transcriptionWindow, new(clock, clock.GetTimestamp(), deadline, deadline - clock.GetUtcNow()));
    internal void EndTranscription() => Volatile.Write(ref transcriptionWindow, null);
    internal bool TranscriptionExpired => Volatile.Read(ref transcriptionWindow) is { } window &&
        (window.Clock.GetUtcNow() >= window.Deadline || window.Clock.GetElapsedTime(window.Timestamp) >= window.Duration);
    internal string Timeline { get { lock (gate) return string.Join(Environment.NewLine, timeline); } }
    internal LiveConversationOperation(ConversationAuthorization authorization, CancellationToken caller)
    {
        Authorization = authorization;
        OriginalCaller = caller;
    }
    internal void Publish(LiveConversationStatus value)
    {
        lock (gate)
        {
            if (Volatile.Read(ref cancellationReason) is { } reason && !value.Quarantined)
                value = value with { Code = reason, Finished = true };
            var previous = status;
            if (previous == value || previous.Finished && !value.Finished) return;
            Volatile.Write(ref status, value);
            if (timeline.Count == 32) timeline.Dequeue();
            timeline.Enqueue($"{value.Code}; policy={value.Policy}; provider={value.ProviderFailure}; audio={value.AudioFailure}");
        }
    }
    internal void Attach(CaptureRun run)
    {
        Volatile.Write(ref capture, run);
        if (Authorization.IsCanceled) run.CancelAsync().Forget();
        else if (Volatile.Read(ref releasedPress) != 0) run.ReleaseAsync().Forget();
    }
    internal void Attach(ConversationTurn run)
    {
        Volatile.Write(ref turn, run);
        if (Authorization.IsCanceled) run.StopAsync().Forget();
    }
    internal void ReleasePress()
    {
        Interlocked.Exchange(ref releasedPress, 1);
        if (!Authorization.IsCanceled) _ = Capture?.ReleaseAsync();
    }
    internal void Cancel(string code)
    {
        Interlocked.CompareExchange(ref cancellationReason, code, null);
        Authorization.Revoke();
        Publish(new(code, Finished: true, Quarantined: Status.Quarantined));
        Worker.RequestCancellation();
        _ = Capture?.CancelAsync();
        _ = Turn?.StopAsync();
    }
    public override string ToString() => nameof(LiveConversationOperation);
}

// App-lifetime owner; setup, fixture and live work all reserve the SAME reviewed operation runner.
internal sealed class LiveConversationController : IAsyncDisposable
{
    private const int MaximumPendingCaptures = 4;
    private readonly object gate = new();
    private readonly SetupOperationRunner operations;
    private readonly ISetupService settings;
    private readonly ICredentialStore vault;
    private readonly ICaptureDeviceFactory captureDevices;
    private readonly EchoReducer? echoReducer;
    private int echoState = -1;
    private readonly ConversationRuntime runtime;
    private readonly OpenAiTranscriptionAdapter transcription;
    private readonly HostTranscriptionAdapter hostTranscription;
    private readonly LocalTranscriptionAdapter? localTranscription;
    private readonly LocalVoices? voices;
    private readonly ParticipationPolicy policy;
    private readonly ConversationContextBuffer context;
    private readonly string? dataDirectory;
    private readonly TimeProvider clock;
    private readonly Func<int, int> nextStyle;
    private readonly Action? revokeAvatar;
    private readonly DesktopMemoryService? memory;
    private readonly LorebookStore? lorebooks;
    private readonly VoiceIdentity? voiceIdentity;
    private readonly SmartHome? smartHome;
    private readonly McpToolService? tools;
    private readonly TaskCompletionSource quarantine = new(TaskCreationOptions.RunContinuationsAsynchronously);
    // What Martlet said while watching the screen (last 30 minutes), so it does not repeat itself. In memory only.
    private readonly Queue<(long At, string Text)> remarks = new();
    // Remembering runs after a reply on its own text-only runtime, one exchange at a time, so it never delays the next turn.
    private readonly Func<IProviderCredentialSource, TimeProvider, ConversationRuntime>? runtimeFactory;
    private readonly ConversationCredentialSource captureCredentials;
    private ConversationRuntime? captureRuntime;
    private ConversationAuthorization? captureAuthorization;
    private CancellationTokenSource captureCancel = new();
    private Task captureTail = Task.CompletedTask;
    private int capturesPending;
    private bool captureQuarantined;
    private string? lastCaptureFailure;
    private LiveConversationOperation? active;
    private LiveConversationConfiguration? configuration;
    private long revision, captureEpoch;
    private bool paused, muted, locked, disposed;
    private readonly Dictionary<SetupRole, JobFailure> failures = [];

    /// <summary>A request that failed since Martlet started and has not worked since: what it was, Martlet's own outcome codes
    /// (never provider text or anything said) and when.</summary>
    internal sealed record JobFailure(SetupRole Role, string What, string Outcome, DateTimeOffset At);

    /// <summary>Each job's latest failed request that no later request of that job has made good, in job order.</summary>
    internal IReadOnlyList<JobFailure> RecentFailures { get { lock (gate) return [.. failures.Values.OrderBy(f => f.Role)]; } }
    /// <summary>Raised off the dispatcher when <see cref="RecentFailures"/> changes.</summary>
    internal event Action? FailuresChanged;
    // Always listening runs on its own slot beside replies (so it keeps hearing while Martlet thinks), with its own
    // speech-to-text credentials bound to the one utterance being transcribed.
    private readonly SetupOperationRunner listenSlot = new();
    private readonly OpenAiTranscriptionAdapter listenTranscription;
    private LiveListener? listener;
    private LiveConversationOperation? transcribing;
    // Hearing what this PC plays runs the same way on a slot of its own, with its own speech-to-text credentials.
    private readonly PcAudioCaptureFactory? pcAudio;
    private readonly SetupOperationRunner pcSlot = new();
    private readonly OpenAiTranscriptionAdapter pcTranscription;
    private LiveListener? pcListener;
    private LiveConversationOperation? pcTranscribing;
    private long listenEpoch, spokeUntil;
    // Thinking models that rejected a recording this app session; they get the transcript only until Martlet restarts.
    private readonly HashSet<string> deafModels = new(StringComparer.Ordinal);

    internal bool IsRunning => operations.IsRunning;
    internal int ContextTurns { get { lock (gate) return context.Count; } }
    /// <summary>The exchanges kept in mind and their estimated tokens (<see cref="BoundedTextInput.TextReservation"/>).</summary>
    internal (int Turns, int Tokens) ContextUse
    {
        get
        {
            lock (gate) return (context.Count, context.Count == 0 ? 0 : BoundedTextInput.TextReservation(context.Utf8Bytes, context.Count * 2));
        }
    }
    internal LiveConversationConfiguration? Configuration { get { lock (gate) return configuration; } }
    /// <summary>Martlet's data directory (null in tests), where model-limits.json is kept.</summary>
    internal string? DataDirectory => dataDirectory;
    internal ParticipationSnapshot PolicySnapshot => policy.Snapshot;
    internal (bool Paused, bool Muted, bool Locked) Controls { get { lock (gate) return (paused, muted, locked); } }
    /// <summary>Raised off the dispatcher after a finished exchange changed memory or could not be remembered.</summary>
    internal event Action<MemoryCaptureReport>? MemoryCaptured;
    /// <summary>Raised off the dispatcher after names were picked up for voices from a finished exchange.</summary>
    internal event Action<IReadOnlyList<(Martlet.Core.Speakers.KnownVoice Voice, string Name)>>? VoicesNamed;
    /// <summary>Tests turn background remembering off to inspect only the reply request.</summary>
    internal bool AutoCapture { get; set; } = true;
    internal Task MemoryCaptureIdle { get { lock (gate) return captureTail; } }
    /// <summary>The Home Assistant connection consulted on user-started turns while its control is on.</summary>
    internal SmartHome? Home => smartHome;
    /// <summary>The MCP servers whose tools user-started replies may call.</summary>
    internal McpToolService? Tools => tools;
    /// <summary>How echo reduction went the last time Martlet listened; null when this controller has none.</summary>
    internal EchoReductionReport? EchoReport => echoReducer?.Report;
    /// <summary>Martlet can hear what this PC plays (Companion › Listening › Hear what this PC plays).</summary>
    internal bool CanHearPc => pcAudio is not null;
    /// <summary>Whether hearing what this PC plays leaves Martlet's own voice out (null until it first listened); without it,
    /// that listening holds off while Martlet speaks.</summary>
    internal bool? PcWithoutMartlet => pcAudio?.WithoutMartlet;

    // Logs each change of state once (on the capture's worker thread), never the audio or device names.
    private void EchoReported(EchoReductionReport report)
    {
        if (Interlocked.Exchange(ref echoState, (int)report.State) == (int)report.State) return;
        if (report.State == EchoReductionState.Active)
            ErrorLog.Info("Echo reduction is on: what the speakers play is removed from the microphone (WebRTC AEC3).");
        else if (report.Problem is not null) ErrorLog.Warn("Echo reduction: " + report.Problem);
    }

    internal LiveConversationController(SetupOperationRunner operations, ISetupService settings, ICredentialStore vault,
        ICaptureDeviceFactory captureDevices, IPlaybackDeviceFactory playbackDevices, TimeProvider? clock = null,
        Func<IProviderCredentialSource, TimeProvider, ConversationRuntime>? runtimeFactory = null,
        Func<IProviderCredentialSource, TimeProvider, OpenAiTranscriptionAdapter>? transcriptionFactory = null,
        Func<int, int>? nextStyle = null,
        DesktopMemoryService? memory = null,
        GeneratedSpeechObserver? generatedSpeech = null, Action? revokeAvatar = null, VoiceIdentity? voiceIdentity = null,
        IHostTranscriptionClient? hostListener = null, string? dataDirectory = null, SpokenTextFeed? spokenText = null,
        SmartHome? smartHome = null, LorebookStore? lorebooks = null, McpToolService? tools = null,
        LocalVoices? voices = null, ILocalTranscriber? localListener = null, EchoReducer? echoReducer = null,
        PcAudioCaptureFactory? pcAudio = null)

    {
        this.operations = operations;
        this.settings = settings;
        this.vault = vault;
        this.captureDevices = captureDevices;
        this.echoReducer = echoReducer;
        this.pcAudio = pcAudio;
        if (echoReducer is not null) echoReducer.Reported += EchoReported;
        this.clock = clock ?? TimeProvider.System;
        this.nextStyle = nextStyle ?? RandomNumberGenerator.GetInt32;
        this.revokeAvatar = revokeAvatar;
        this.memory = memory;
        this.lorebooks = lorebooks;
        this.voiceIdentity = voiceIdentity;
        this.voices = voices;
        this.smartHome = smartHome;
        this.tools = tools;
        this.runtimeFactory = runtimeFactory;
        this.dataDirectory = dataDirectory;
        localTranscription = localListener is null ? null : new(localListener, this.clock);
        context = new();
        captureCredentials = new(() => Volatile.Read(ref captureAuthorization));
        var credentials = new ConversationCredentialSource(() => Volatile.Read(ref active)?.Authorization);
        runtime = runtimeFactory?.Invoke(credentials, this.clock) ??
            ConversationRuntime.Create(credentials, playbackDevices, clock: this.clock, generatedSpeech: generatedSpeech,
                hostText: new HostTextClient(), hostSpeech: dataDirectory is null ? null : new HostSpeechClient(dataDirectory),
                spokenText: spokenText, windowsVoice: new WindowsVoiceClient());
        transcription = transcriptionFactory?.Invoke(credentials, this.clock) ??
            OpenAiTranscriptionAdapter.Create(credentials, this.clock);
        var listenCredentials = new ConversationCredentialSource(() => Volatile.Read(ref transcribing)?.Authorization);
        listenTranscription = transcriptionFactory?.Invoke(listenCredentials, this.clock) ??
            OpenAiTranscriptionAdapter.Create(listenCredentials, this.clock);
        var pcCredentials = new ConversationCredentialSource(() => Volatile.Read(ref pcTranscribing)?.Authorization);
        pcTranscription = transcriptionFactory?.Invoke(pcCredentials, this.clock) ??
            OpenAiTranscriptionAdapter.Create(pcCredentials, this.clock);
        hostTranscription = new(hostListener ?? new HostTranscriptionClient(), this.clock);
        policy = new(runtime.SessionId, new ParticipationConfiguration(), new ParticipationState(), this.clock);
    }

    internal void Configure(SettingsLoadResult loaded)
    {
        // The context windows found on this PC (Companion › Replies › Check) keep the context size within the model's own.
        var next = LiveConversationConfiguration.From(loaded, ModelLimits.Load(dataDirectory));
        LiveConversationOperation? stop;
        LiveListener[] stopListening;
        bool changed;
        lock (gate)
        {
            changed = configuration is not null && configuration.Revision != next?.Revision;
            memory?.Invalidate();
            // A settings change continues the conversation: what was said so far stays as context for the next reply.
            if (configuration?.Revision != next?.Revision) CancelCapturesLocked();
            configuration = next;
            stop = RevokeLocked();
            stopListening = RevokeListeningLocked();
        }
        if (changed) revokeAvatar?.Invoke();
        stop?.Cancel("conversation.configuration_changed");
        Cancel(stopListening);
        // Opening the talk window starts the MCP servers in the background, so their tools are ready by the first reply.
        if (next is { SupportsTools: true } && tools is { HasEnabledServers: true }) tools.EnsureStarted(retry: true);
    }

    internal void SetControls(bool pause, bool mute, bool sessionLocked)
    {
        if (pause || mute || sessionLocked) revokeAvatar?.Invoke();
        LiveConversationOperation? stop;
        LiveListener[] stopListening;
        lock (gate)
        {
            if (paused == pause && muted == mute && locked == sessionLocked) return;
            memory?.Invalidate();
            ClearContextLocked();
            if (pause || mute || sessionLocked) CancelCapturesLocked();
            paused = pause;
            muted = mute;
            locked = sessionLocked;
            stop = RevokeLocked();
            stopListening = RevokeListeningLocked();
        }
        stop?.Cancel(sessionLocked ? "conversation.locked" : pause ? "conversation.paused" : "conversation.muted");
        Cancel(stopListening);
        if (pause || mute || sessionLocked) echoReducer?.Forget();
    }

    internal void SetSessionLocked(bool value)
    {
        if (value) revokeAvatar?.Invoke();
        LiveConversationOperation? stop;
        LiveListener[] stopListening;
        lock (gate)
        {
            if (locked == value) return;
            memory?.Invalidate();
            ClearContextLocked();
            if (value) CancelCapturesLocked();
            locked = value;
            stop = RevokeLocked();
            stopListening = RevokeListeningLocked();
        }
        stop?.Cancel("conversation.locked");
        Cancel(stopListening);
        if (value) echoReducer?.Forget();
    }

    internal void Revoke(string code)
    {
        revokeAvatar?.Invoke();
        LiveConversationOperation? stop;
        LiveListener[] stopListening;
        lock (gate)
        {
            memory?.Invalidate();
            ClearContextLocked();
            CancelCapturesLocked();
            stop = RevokeLocked();
            stopListening = RevokeListeningLocked();
        }
        stop?.Cancel(code);
        Cancel(stopListening);
    }

    // Revokes every utterance always listening has authorized (the microphone's and what the PC plays); the caller then stops
    // their loops outside the gate.
    private LiveListener[] RevokeListeningLocked()
    {
        Interlocked.Increment(ref listenEpoch);
        return new[] { listener, pcListener }.OfType<LiveListener>().Where(running => running.Running).ToArray();
    }

    private static void Cancel(LiveListener[] listeners)
    {
        foreach (var listening in listeners) listening.Worker.RequestCancellation();
    }

    private LiveConversationOperation? RevokeLocked()
    {
        revision = checked(revision + 1);
        var owned = active is { OwnershipReleased: false } ? active : null;
        owned?.Authorization.Revoke();
        // SetState may require StopActiveTurn; ownership is deliberately NOT released here.
        var change = policy.SetState(new() { AuthorizationRevision = revision, Paused = paused || locked || disposed, Muted = muted });
        return change.Action == PolicyAction.StopActiveTurn || owned is not null ? owned : null;
    }

    internal LiveConversationOperation Start(string? text, bool voice, bool microphone, bool approved,
        bool localCaptureApproved = false, bool uploadApproved = false, CancellationToken caller = default,
        ListeningOptions? listening = null, bool spoken = false, HeardVoices? heard = null, double? confidence = null,
        BoundedWaveAudio? recording = null, SeenScreen? seen = null, bool pcAudio = false, string? userWords = null)
    {
        if (!approved || microphone && (!localCaptureApproved || !uploadApproved))
            throw new LiveActionException("conversation.permission_required");
        if (listening is not null && !microphone || spoken && microphone || recording is not null && !spoken ||
            listening?.Pc == true || pcAudio && (!spoken || recording is not null) || !pcAudio && userWords is not null)
            throw new LiveActionException("conversation.invalid_input");
        listening?.Activity.Validate();
        Voiceprint? voiceprint = null;
        if (listening?.RequireVoiceId == true)
            voiceprint = voiceIdentity?.Current ?? throw new LiveActionException("voiceid.not_enrolled");
        caller.ThrowIfCancellationRequested();
        BoundedTextInput? input = microphone ? null : new(text ?? "");
        if (input is { UserText.Length: > 4096 }) throw new LiveActionException("conversation.input_limit");
        var published = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        LiveConversationOperation operation;
        lock (gate)
        {
            if (disposed || paused || muted || locked) throw new LiveActionException("conversation.controls_blocked");
            if (operations.IsRunning) throw new LiveActionException("conversation.ownership_busy");
            var selected = configuration ?? throw new LiveActionException("conversation.setup_required");
            if (selected.Unavailable(voice, microphone) is not null) throw new LiveActionException("conversation.configuration_unsupported");
            long acceptedRevision = revision = checked(revision + 1);
            // Vision being on is the permission for its pictures; a text-only Thinking model never gets one.
            if (selected.Vision() == VisionSupport.Unsupported) seen = null;
            var authorization = new ConversationAuthorization(selected, voice, microphone, clock,
                () => Volatile.Read(ref revision) == acceptedRevision, settings.LoadAsync, vault, caller,
                screen: seen is not null, hear: microphone ? listening?.Hear == true : recording is not null);
            operation = new(authorization, caller)
            {
                MemoryRequested = memory is not null && selected.Memory is { Enabled: true },
                Listening = listening, Voiceprint = voiceprint, Spoken = spoken, Heard = spoken ? heard : null,
                SpokenConfidence = spoken ? confidence : null, Recording = recording, Seen = seen,
                PcAudio = pcAudio, UserWords = string.IsNullOrWhiteSpace(userWords) ? null : userWords.Trim()
            };
            active = operation;
            var worker = operations.TryStart(async token =>
            {
                await published.Task.ConfigureAwait(false);
                authorization.BindWorker(token);
                return await RunAsync(operation, input, token).ConfigureAwait(false);
            });
            if (worker is null)
            {
                authorization.Revoke();
                throw new LiveActionException("conversation.ownership_busy");
            }
            operation.Worker = worker;
            policy.SetState(new()
            {
                AuthorizationRevision = acceptedRevision, CaptureAuthorized = microphone || spoken, TranscriptionAuthorized = microphone || spoken,
                TextDestinationAuthorized = true, SpeechOutputRequested = voice, SpeechDestinationAuthorized = voice
            });
        }
        published.SetResult();
        SuperviseAsync(operation).Forget();
        return operation;
    }

    /// <summary>Starts always listening: one loop on its own slot beside replies that records one utterance at a time and
    /// transcribes each (in order) while it already listens for the next, so nothing said while Martlet thinks is lost. Each
    /// utterance is still its own action (fresh authorization, capture epoch, Voice ID check and STT request). It holds off only
    /// while Martlet speaks, so it never hears itself, or while other setup work owns the app slot; microphone and
    /// speech-to-text failures are reported and listening carries on.</summary>
    internal LiveListener Listen(ListeningOptions options)
    {
        if (!options.HandsFree || options.Pc && (options.RequireVoiceId || options.Hear || options.ReduceEcho))
            throw new LiveActionException("conversation.invalid_input");
        if (options.Pc && pcAudio is null) throw new LiveActionException("conversation.configuration_unsupported");
        options.Activity.Validate();
        Voiceprint? voiceprint = null;
        if (options.RequireVoiceId) voiceprint = voiceIdentity?.Current ?? throw new LiveActionException("voiceid.not_enrolled");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var listening = new LiveListener(options, voiceprint);
        lock (gate)
        {
            if (disposed || paused || muted || locked) throw new LiveActionException("conversation.controls_blocked");
            var selected = configuration ?? throw new LiveActionException("conversation.setup_required");
            if (selected.Unavailable(false, true) is not null) throw new LiveActionException("conversation.configuration_unsupported");
            // What the PC plays is heard on a slot of its own, beside the microphone.
            listening.Worker = (options.Pc ? pcSlot : listenSlot).TryStart(async token =>
            {
                await started.Task.ConfigureAwait(false);
                return await ListenLoopAsync(listening, token).ConfigureAwait(false);
            }) ?? throw new LiveActionException("conversation.ownership_busy");
            if (options.Pc) pcListener = listening;
            else listener = listening;
        }
        started.SetResult();
        return listening;
    }

    /// <summary>Stops always listening: the utterance being recorded is discarded and one being transcribed is canceled.</summary>
    internal void StopListening(LiveListener listening)
    {
        listening.Revoke();
        listening.Worker.RequestCancellation();
    }

    /// <summary>Whether this listener holds off right now. Hearing what this PC plays holds off only while Martlet speaks and
    /// Windows can't leave Martlet's own voice out of it (never for barge-in: only the user interrupts).</summary>
    private bool Held(ListeningOptions options) => options.Pc ? pcAudio?.WithoutMartlet != true && Held(false) : Held(options.BargeIn);

    /// <summary>Always listening holds off while Martlet speaks (a reply or a remark, plus a short tail for the room's echo), so
    /// it never hears itself, and while other setup work (a microphone test, Voice ID enrollment) owns the app slot. With
    /// barge-in it keeps listening while Martlet speaks, so you can talk over a reply to stop it.</summary>
    internal bool Held(bool bargeIn)
    {
        lock (gate)
        {
            var now = clock.GetTimestamp();
            if (active is { Worker: not null } current && !current.OwnershipReleased)
            {
                if (bargeIn) return false;
                if (current.Turn?.Snapshot is { State: ConversationState.Playing } or { MayHavePlayed: true, OwnershipReleased: false })
                {
                    spokeUntil = now + (long)(SpeechTail.TotalSeconds * clock.TimestampFrequency);
                    return true;
                }
                return now < spokeUntil;
            }
            return operations.IsRunning || !bargeIn && now < spokeUntil;
        }
    }

    internal static TimeSpan SpeechTail => TimeSpan.FromMilliseconds(300);
    internal static TimeSpan MicrophoneRetry => TimeSpan.FromSeconds(5);

    private async Task<SetupWorkResult> ListenLoopAsync(LiveListener listening, CancellationToken token)
    {
        var pending = Task.CompletedTask;
        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                if (Held(listening.Options))
                {
                    listening.Held = true;
                    await Task.Delay(TimeSpan.FromMilliseconds(50), clock, token).ConfigureAwait(false);
                    continue;
                }
                listening.Held = false;
                var utterance = Utterance(listening, token);
                listening.Utterance = utterance;
                byte[]? speech;
                try
                {
                    await utterance.Authorization.ValidateSettingsAsync(token).ConfigureAwait(false);
                    speech = await CaptureSpeechAsync(utterance).ConfigureAwait(false);
                }
                catch (LiveActionException error) when (error.Code is "conversation.revoked" or "conversation.expired")
                {
                    token.ThrowIfCancellationRequested();
                    utterance.Publish(new(error.Code, Finished: true));
                    continue;
                }
                catch (Exception error) when (error is ContractException or InvalidOperationException && !token.IsCancellationRequested)
                {
                    // A microphone that can't be pressed right now is reported like a failed one and tried again shortly.
                    utterance.Publish(new("mic.Failed", Finished: true, AudioFailure: (error as ContractException)?.Code ?? ErrorCode.AudioDeviceUnavailable));
                    speech = null;
                }
                if (speech is null)
                {
                    utterance.Hearing = false;
                    utterance.TalkingOver = false;
                    var status = utterance.Status;
                    if (status.Code is not ("mic.no_speech" or "listen.held"))
                        listening.Post(Result(utterance));
                    if (status.AudioFailure is not null)
                        await Task.Delay(MicrophoneRetry, clock, token).ConfigureAwait(false);
                    continue;
                }
                listening.BeginTranscribing();
                utterance.Hearing = false;
                utterance.TalkingOver = false;
                var previous = pending;
                pending = Task.Run(() => TranscribeHeardAsync(previous, listening, utterance, speech, token), CancellationToken.None);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return new(SetupWorkOutcome.Canceled);
        }
        catch (LiveActionException error)
        {
            listening.Ended = error.Code;
            return new(SetupWorkOutcome.Failed);
        }
        finally
        {
            listening.Held = false;
            await pending.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    // One utterance: its own authorization (revoked with listening), recorded with the listener's options.
    private LiveConversationOperation Utterance(LiveListener listening, CancellationToken token)
    {
        lock (gate)
        {
            if (disposed || paused || muted || locked) throw new LiveActionException("conversation.controls_blocked");
            var selected = configuration ?? throw new LiveActionException("conversation.setup_required");
            if (selected.Unavailable(false, true) is not null) throw new LiveActionException("conversation.configuration_unsupported");
            var epoch = Volatile.Read(ref listenEpoch);
            var revision = listening.Revision;
            var authorization = new ConversationAuthorization(selected, voice: false, microphone: true, clock,
                () => Volatile.Read(ref listenEpoch) == epoch && listening.Revision == revision, settings.LoadAsync, vault, token);
            authorization.BindWorker(token);
            return new(authorization, token)
            {
                Worker = listening.Worker, Listen = true, Listening = listening.Options, Voiceprint = listening.Voiceprint
            };
        }
    }

    private static HeardSpeech Result(LiveConversationOperation utterance) => new(utterance.Status,
        utterance.Status.Code == "listen.heard" ? utterance.Transcript : null, utterance.Transcription?.Confidence, utterance.Heard,
        utterance.SpeakerCheck, utterance.Voiceprint, utterance.Status.Code == "listen.heard" ? utterance.Recording : null);

    // Voice ID, then speech-to-text, one utterance after another (so what you said stays in order) while the next is recorded.
    private async Task TranscribeHeardAsync(Task previous, LiveListener listening, LiveConversationOperation utterance, byte[] speech,
        CancellationToken token)
    {
        try
        {
            await previous.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            BoundedWaveAudio? audio;
            try { audio = Screen(utterance, speech); }
            finally { CryptographicOperations.ZeroMemory(speech); }
            if (audio is null) return;
            // Never longer than the upload's own 30 s deadline, even if a native boundary ignores it.
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(35), clock);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
            // What the PC plays is transcribed beside the microphone, with its own speech-to-text credentials.
            var pc = listening.Options.Pc;
            if (pc) Volatile.Write(ref pcTranscribing, utterance);
            else Volatile.Write(ref transcribing, utterance);
            TranscriptionResult? result;
            try { result = await TranscribeAsync(utterance, audio, pc ? pcTranscription : listenTranscription, linked.Token).ConfigureAwait(false); }
            finally
            {
                if (pc) Interlocked.CompareExchange(ref pcTranscribing, null, utterance);
                else Interlocked.CompareExchange(ref transcribing, null, utterance);
            }
            if (result is null) return;
            utterance.Heard = await HeardAsync(utterance, linked.Token).ConfigureAwait(false);
            utterance.Transcript = result.Text;
            if (listening.Options.Hear) utterance.Recording = audio;
            utterance.Publish(new("listen.heard", Finished: true));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            utterance.Publish(new("conversation.canceled", Finished: true));
        }
        catch (OperationCanceledException)
        {
            utterance.Publish(new("stt.deadline_exceeded", Finished: true));
        }
        catch (LiveActionException error)
        {
            utterance.Publish(new(error.Code, Finished: true));
        }
        catch (ContractException error)
        {
            utterance.Publish(new("conversation.invalid_input", Finished: true, AudioFailure: error.Code));
        }
        finally
        {
            if (!token.IsCancellationRequested) listening.Post(Result(utterance));
            listening.EndTranscribing();
        }
    }

    private void ClearContextLocked()
    {
        context.Clear();
        remarks.Clear();
    }

    /// <summary>The user's Refresh context: forget the kept exchanges and screen remarks; nothing else stops.</summary>
    internal bool ForgetContext()
    {
        lock (gate)
        {
            if (context.Count == 0 && remarks.Count == 0) return false;
            memory?.Invalidate();
            ClearContextLocked();
            return true;
        }
    }

    internal static TimeSpan RemarkMemory => TimeSpan.FromMinutes(30);

    /// <summary>One unprompted screen glance: the image, the window title and recent context go to the Thinking model,
    /// which either answers [pass] (silence) or one short remark that is spoken like any reply. It bypasses the
    /// participation policy (that decides whether to answer the user); the caller's pacer decides when to look.</summary>
    internal LiveConversationOperation StartCommentary(BoundedImage image, string windowTitle, Chattiness chattiness, bool voice,
        bool screenApproved, WatchSource? source = null, CancellationToken caller = default, AttentionSignal? attention = null)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (!screenApproved) throw new LiveActionException("conversation.permission_required");
        caller.ThrowIfCancellationRequested();
        var published = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        LiveConversationOperation operation;
        lock (gate)
        {
            if (disposed || paused || muted || locked) throw new LiveActionException("conversation.controls_blocked");
            if (operations.IsRunning) throw new LiveActionException("conversation.ownership_busy");
            var selected = configuration ?? throw new LiveActionException("conversation.setup_required");
            if (selected.Unavailable(voice, false) is not null) throw new LiveActionException("conversation.configuration_unsupported");
            if (selected.Vision() == VisionSupport.Unsupported) throw new LiveActionException("commentary.vision_unsupported");
            long acceptedRevision = revision = checked(revision + 1);
            var authorization = new ConversationAuthorization(selected, voice, false, clock,
                () => Volatile.Read(ref revision) == acceptedRevision, settings.LoadAsync, vault, caller, screen: true);
            operation = new(authorization, caller) { Commentary = true, Attention = attention };
            active = operation;
            var camera = source is { IsScreen: false };
            var prompt = CommentaryPromptLocked(windowTitle, camera, selected.Prompts, attention);
            var worker = operations.TryStart(async token =>
            {
                await published.Task.ConfigureAwait(false);
                authorization.BindWorker(token);
                return await RunCommentaryAsync(operation, prompt, image, chattiness, camera, token).ConfigureAwait(false);
            });
            if (worker is null)
            {
                authorization.Revoke();
                throw new LiveActionException("conversation.ownership_busy");
            }
            operation.Worker = worker;
        }
        published.SetResult();
        SuperviseAsync(operation).Forget();
        return operation;
    }

    private string CommentaryPromptLocked(string windowTitle, bool camera = false, PromptSettings? prompts = null,
        AttentionSignal? attention = null)
    {
        while (remarks.TryPeek(out var oldest) && clock.GetElapsedTime(oldest.At) >= RemarkMemory) remarks.Dequeue();
        var title = new string(windowTitle.Where(c => !char.IsControl(c) && c != '"').Take(80).ToArray()).Trim();
        var said = remarks.Count == 0 ? null : PromptSettings.Fill(prompts, PromptCatalog.GlanceRemarks,
            ("remarks", string.Join(" | ", remarks.Select(r => $"\"{r.Text}\""))));
        return PromptSettings.Fill(prompts, attention is not null ? PromptCatalog.GlanceAttention
                : camera ? PromptCatalog.GlanceCamera : PromptCatalog.GlanceScreen,
            ("title", title.Length > 0 ? title : "unknown"), ("remarks", said is null ? "" : " " + said),
            ("what", attention?.Describe() ?? ""), ("silent", LiveConversationConfiguration.SilentReply))!;
    }

    internal static bool IsSilentReply(string text)
    {
        var trimmed = text.Trim();
        return trimmed.Length == 0 || trimmed.StartsWith("[" + LiveConversationConfiguration.SilentReply, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmed.Trim('[', ']', '(', ')', '<', '>', '*', '"', '\'', '.', '!', ' '),
                LiveConversationConfiguration.SilentReply, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A reply still streaming that may turn out to be [pass]; it isn't shown until it clearly isn't.</summary>
    internal static bool MaybeSilent(string text)
    {
        var trimmed = text.Trim();
        return IsSilentReply(trimmed) || ("[" + LiveConversationConfiguration.SilentReply + "]").StartsWith(trimmed, StringComparison.OrdinalIgnoreCase) ||
            LiveConversationConfiguration.SilentReply.StartsWith(trimmed, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<SetupWorkResult> RunCommentaryAsync(LiveConversationOperation operation, string prompt, BoundedImage image,
        Chattiness chattiness, bool camera, CancellationToken worker)
    {
        try
        {
            await operation.Authorization.ValidateSettingsAsync(worker).ConfigureAwait(false);
            IReadOnlyList<TextHistoryMessage> earlier;
            lock (gate) earlier = context.Snapshot();
            var lore = await ScanLoreAsync(operation, prompt, earlier, operation.Authorization.Configuration.Persona, worker)
                .ConfigureAwait(false);
            ConversationTurn turn;
            lock (gate)
            {
                operation.Authorization.Check(worker);
                var configured = operation.Authorization.Configuration;
                var persona = configured.Persona;
                ResponseStyle? style = persona is null ? null : ResponseStyleSelector.Select(persona.Styles, nextStyle);
                var history = context.Snapshot();
                var request = configured.Request(new(prompt), operation.Authorization.Voice, style, history, null, lore,
                    out var usedHistory, out _, out var usedLore, image, LiveConversationConfiguration.CommentaryInstructions(chattiness, camera, configured.Prompts),
                    LiveConversationConfiguration.SilentReply);
                operation.PersonaRevision = persona?.ConfigurationRevision;
                operation.ResponseStyle = style;
                operation.ContextMessages = usedHistory;
                operation.ContextMessagesOmitted = history.Count - usedHistory;
                RecordLore(operation, lore, usedLore);
                operation.Authorization.BindInput(request.Input);
                operation.Publish(new("commentary.looking"));
                turn = runtime.Start(request, operation.Authorization, operation.OriginalCaller);
                operation.Attach(turn);
            }
            var terminal = await turn.Completion.ConfigureAwait(false);
            NoteFallback(camera ? "Camera glance" : "Screen glance", operation.Authorization.Configuration, terminal);
            var text = turn.Content.Text;
            var passed = terminal.State == ConversationState.Completed && IsSilentReply(text);
            operation.Passed = passed;
            if (IsFailure(terminal))
                LogReplyFailure(camera ? "Camera glance" : "Screen glance", operation.Authorization.Configuration, terminal);
            else if (terminal.State == ConversationState.Completed) Succeeded(SetupRole.Llm);
            if (terminal.State == ConversationState.Completed && !passed)
            {
                lock (gate)
                {
                    if (ReferenceEquals(active, operation) && !operation.Authorization.IsCanceled)
                    {
                        var remark = text.Trim();
                        context.Add("(You glanced at my screen.)", remark);
                        remarks.Enqueue((clock.GetTimestamp(), remark.Length > 200 ? remark[..200] : remark));
                        while (remarks.Count > 4) remarks.Dequeue();
                    }
                }
            }
            operation.Publish(new(passed ? "commentary.passed" : "runtime." + terminal.State, Finished: true,
                Quarantined: terminal.Quarantined, ProviderFailure: terminal.ProviderFailure, AudioFailure: terminal.Playback?.Error?.Code));
            await turn.OwnershipRelease.ConfigureAwait(false);
            if (turn.Snapshot.Quarantined)
            {
                operation.Publish(operation.Status with { Code = "conversation.cleanup_quarantined", Quarantined = true });
                await quarantine.Task.ConfigureAwait(false);
            }
            return new(terminal.State is ConversationState.Completed or ConversationState.Refused ? SetupWorkOutcome.Completed : SetupWorkOutcome.Failed);
        }
        catch (OperationCanceledException) when (worker.IsCancellationRequested || operation.OriginalCaller.IsCancellationRequested)
        {
            operation.Publish(new("conversation.canceled", Finished: true));
            return new(SetupWorkOutcome.Canceled);
        }
        catch (LiveActionException error)
        {
            operation.Publish(new(error.Code, Finished: true));
            return new(SetupWorkOutcome.Failed);
        }
        catch (ContractException error)
        {
            operation.Publish(new("conversation.invalid_input", Finished: true, AudioFailure: error.Code));
            return new(SetupWorkOutcome.Failed);
        }
        finally
        {
            if (operation.Turn is { } turn)
            {
                await turn.StopAsync().ConfigureAwait(false);
                await turn.OwnershipRelease.ConfigureAwait(false);
                if (turn.Snapshot.Quarantined) await quarantine.Task.ConfigureAwait(false);
            }
            lock (gate)
            {
                operation.FinishExecution();
                if (ReferenceEquals(active, operation)) RevokeLocked();
            }
        }
    }

    // keepContext: a brief hand-over (an idle listen yielding to a screen glance, or a glance yielding to the user),
    // not the user ending the conversation.
    internal void Stop(LiveConversationOperation operation, string reason = "conversation.canceled", bool keepContext = false)
    {
        lock (gate)
        {
            if (!ReferenceEquals(operation, active)) return;
            memory?.Invalidate();
            if (!keepContext) ClearContextLocked();
            if (operation.OwnershipReleased || operation.ExecutionFinished) return;
            RevokeLocked();
        }
        // Exact handle, never a delayed sink-wide Stop or cancellation of the next setup/turn.
        operation.Cancel(reason);
    }

    private async Task SuperviseAsync(LiveConversationOperation operation)
    {
        while (!operation.Worker.Completion.IsCompleted && !operation.ExecutionFinished)
        {
            try { operation.Authorization.Check(); }
            catch (OperationCanceledException) { Stop(operation); return; }
            catch (LiveActionException error) { Stop(operation, error.Code); return; }
            if (operation.TranscriptionExpired) { Stop(operation, "stt.deadline_exceeded"); return; }
            var turn = operation.Turn;
            if (turn is not null && !operation.Status.Finished)
            {
                var snapshot = turn.Snapshot;
                operation.Publish(new("runtime." + snapshot.State, ProviderFailure: snapshot.ProviderFailure,
                    AudioFailure: snapshot.Playback?.Error?.Code));
                lock (gate)
                {
                    if (ReferenceEquals(active, operation) && !operation.Authorization.IsCanceled)
                        policy.SetState(new()
                        {
                            AuthorizationRevision = revision, CaptureAuthorized = operation.Authorization.Microphone || operation.Spoken,
                            TranscriptionAuthorized = operation.Authorization.Microphone || operation.Spoken, TextDestinationAuthorized = true,
                            SpeechOutputRequested = operation.Authorization.Voice, SpeechDestinationAuthorized = operation.Authorization.Voice,
                            Activity = snapshot.State == ConversationState.Playing ? ResponseActivity.Playing : ResponseActivity.Responding
                        });
                }
            }
            await Task.WhenAny(operation.Worker.Completion, Task.Delay(TimeSpan.FromMilliseconds(25), clock)).ConfigureAwait(false);
        }
    }

    private async Task<SetupWorkResult> RunAsync(LiveConversationOperation operation, BoundedTextInput? input, CancellationToken worker)
    {
        DispatchLease? lease = null;
        try
        {
            await operation.Authorization.ValidateSettingsAsync(worker).ConfigureAwait(false);
            if (operation.Authorization.Microphone)
            {
                var audio = await CaptureAsync(operation).ConfigureAwait(false);
                if (audio is null) return new(SetupWorkOutcome.Completed);
                var result = await TranscribeAsync(operation, audio, transcription, worker).ConfigureAwait(false);
                if (result is null)
                    return new(operation.Transcription?.Outcome == TranscriptionOutcome.NoSpeech ? SetupWorkOutcome.Completed : SetupWorkOutcome.Failed);
                operation.Heard = await HeardAsync(operation, worker).ConfigureAwait(false);
                operation.Transcript = result.Text;
                if (operation.Authorization.Hear) operation.Recording = audio;
                input = new(result.Text!);
            }
            operation.Authorization.Check(worker);
            ConversationTurn turn;
            PersonaProfile? persona;
            ResponseStyle? style;
            IReadOnlyList<TextHistoryMessage> history;
            lock (gate)
            {
                operation.Authorization.Check(worker);
                // Receipt is NOW for a newly received transcript. Never renew a queued/busy/expired intent.
                var source = operation.Spoken ? InputSource.HandsFreeListening
                    : !operation.Authorization.Microphone ? InputSource.TypedControl
                    : operation.HandsFree ? InputSource.HandsFreeListening : InputSource.PushToTalkControl;
                var intent = policy.CreateIntent(new(source,
                    new Transcript(input!.UserText, confidence: operation.Transcription?.Confidence ?? operation.SpokenConfidence),
                    trustedTypedAddress: !operation.Authorization.Microphone && !operation.Spoken));
                var decision = policy.Evaluate(intent);
                var commit = policy.TryCommit(decision);
                operation.Publish(new("policy." + commit.Reason, Policy: commit.Reason, Finished: !commit.Accepted));
                if (!commit.Accepted) return new(SetupWorkOutcome.Completed);
                lease = commit.Lease;
                persona = operation.Authorization.Configuration.Persona;
                style = persona is null ? null :
                    ResponseStyleSelector.Select(persona.Styles, nextStyle);
                history = context.Snapshot();
            }

            // Keeps Smart home's list of locks, doors and garages current before the model may call Home Assistant's tools.
            if (smartHome is { ModelToolsEnabled: true } safety) await safety.RefreshSafetyAsync(worker).ConfigureAwait(false);

            // What the PC played is never the user: memory, tools and Home Assistant only go by the user's own words, and a
            // message that is only what the PC played gets none of them.
            var own = operation.PcAudio ? operation.UserWords : input!.UserText;
            DesktopMemoryRecall? memoryResult = null;
            if (operation.MemoryRequested && own is not null)
            {
                operation.Publish(new("memory.recalling"));
                memoryResult = await RecallAsync(operation, own, worker).ConfigureAwait(false);
                operation.Authorization.Check(worker);
                await operation.Authorization.ValidateSettingsAsync(worker).ConfigureAwait(false);
                operation.MemoryStoreRevision = memoryResult?.StoreRevision;
            }
            var lore = await ScanLoreAsync(operation, input!.UserText, history, persona, worker).ConfigureAwait(false);

            // Tools from MCP servers on this PC, only for the user's own turns and routes that do function calling.
            DesktopToolset? toolset = null;
            var configured = operation.Authorization.Configuration;
            if (own is not null && tools is { HasEnabledServers: true } && configured.SupportsTools && !tools.IsUnsupported(configured.ToolModelKey()))
            {
                operation.Publish(new("tools.preparing"));
                toolset = await tools.PrepareAsync(worker).ConfigureAwait(false);
                operation.Authorization.Check(worker);
                operation.Toolset = toolset;
            }

            // Only the user's own typed or spoken words ever reach Home Assistant (glances use RunCommentaryAsync). When the
            // reply is offered Home Assistant's own tools, the model acts through them instead of Assist, so nothing runs twice.
            HomeTurn? home = null;
            if (own is not null && smartHome is { ControlEnabled: true } house)
            {
                if (house.ModelToolsEnabled && toolset?.Servers.Contains(SmartHome.ServerName) == true &&
                    tools!.ManagedConflicts.All(s => s.Name != SmartHome.ServerName))
                    home = house.ToolsTurn(configured.Prompts);
                else
                {
                    operation.Publish(new("home.asking"));
                    home = await house.HandleAsync(own, worker, configured.Prompts).ConfigureAwait(false);
                    operation.Authorization.Check(worker);
                }
                operation.HomeSummary = home.Summary;
                operation.Publish(new(home.Code));
            }

            lock (gate)
            {
                operation.Authorization.Check(worker);
                var prompts = operation.Authorization.Configuration.Prompts;
                // The recording goes only to a Thinking model that hears and hasn't refused one this session.
                var recording = operation.Authorization.Hear && configured.Hearing() == HearingSupport.Supported &&
                    !deafModels.Contains(configured.ToolModelKey()) ? operation.Recording : null;
                // While vision is on, the newest picture of what it watches goes with the message, so the reply sees it too.
                // A message too long to fit beside the picture goes without it.
                var seen = operation.Authorization.Screen ? operation.Seen : null;
                ConversationRequest Ask(SeenScreen? picture, out int keptHistory, out int keptFacts, out int keptEntries) =>
                    operation.Authorization.Configuration.Request(
                        input!, operation.Authorization.Voice, style, history, memoryResult, lore,
                        out keptHistory, out keptFacts, out keptEntries, image: picture?.Image,
                        extraInstructions: Join(home?.Instructions,
                            VoicePromptContext.Instructions(operation.Heard, prompts),
                            operation.Spoken ? LiveConversationConfiguration.Listening(prompts) : null,
                            operation.PcAudio ? LiveConversationConfiguration.PcAudio(prompts) : null,
                            recording is null ? null : PromptSettings.Fill(prompts, PromptCatalog.HeardVoice),
                            picture is null ? null : PromptSettings.Fill(prompts, PromptCatalog.SeenWithMessage, ("source", picture.Describe()))),
                        silentReply: operation.Spoken ? LiveConversationConfiguration.SilentReply : null, tools: toolset,
                        closingInstructions: operation.Authorization.Configuration.ReplyLength, audio: recording, imageOptional: true);
                ConversationRequest request;
                int usedHistory, usedMemory, usedLore;
                try { request = Ask(seen, out usedHistory, out usedMemory, out usedLore); }
                catch (LiveActionException error) when (error.Code == "conversation.input_limit" && seen is not null)
                {
                    request = Ask(null, out usedHistory, out usedMemory, out usedLore);
                }
                operation.VoiceSent = request.Input.Audio is not null;
                operation.ScreenSent = request.Input.Image is not null;
                operation.PersonaRevision = persona?.ConfigurationRevision;
                operation.ResponseStyle = style;
                operation.ContextMessages = usedHistory;
                operation.ContextMessagesOmitted = history.Count - usedHistory;
                operation.MemoryFactsUsed = usedMemory;
                operation.MemoryFactsOmitted = (memoryResult?.Facts.Count ?? 0) - usedMemory;
                RecordLore(operation, lore, usedLore);
                operation.Authorization.BindInput(request.Input, request.Limits.MaxToolRounds, request.ImageOptional);
                // Exact-content commit, pause/consent state and immediate Start share this short, non-awaiting gate.
                turn = runtime.Start(request, operation.Authorization, operation.OriginalCaller);
                operation.Attach(turn);
            }
            var terminal = await turn.Completion.ConfigureAwait(false);
            NoteFallback("Reply", configured, terminal);
            // A model that rejected tools is asked without them from now on (this app session).
            if (terminal.ToolsRejected)
            {
                tools?.MarkUnsupported(configured.ToolModelKey());
                ErrorLog.Info($"The Thinking model {configured.Route(SetupRole.Llm).ModelId} rejected the request with tools; " +
                    "Martlet asked again without tools and stops offering them to it until it restarts.");
            }
            // A model that rejected the recording gets the transcript only from now on (this app session).
            if (terminal.AudioRejected)
            {
                lock (gate) deafModels.Add(configured.ToolModelKey());
                ErrorLog.Info($"The Thinking model {configured.Route(SetupRole.Llm).ModelId} rejected the request with your recording; " +
                    "Martlet asked again with the transcript only and sends it only the transcript until it restarts.");
            }
            if (terminal.ImageRejected)
                ErrorLog.Info($"The Thinking model {configured.Route(SetupRole.Llm).ModelId} rejected the picture of your screen sent " +
                    "with your message; Martlet asked again with your words only.");
            if (IsFailure(terminal)) LogReplyFailure("Reply", configured, terminal);
            else if (terminal.State == ConversationState.Completed) Succeeded(SetupRole.Llm);
            // What always listening heard may not have been meant for Martlet: the model answers [pass] and stays quiet.
            var passed = operation.Spoken && terminal.State == ConversationState.Completed && IsSilentReply(turn.Content.Text);
            operation.Passed = passed;
            if (terminal.State == ConversationState.Completed && !string.IsNullOrWhiteSpace(turn.Content.Text))
            {
                lock (gate)
                {
                    if (ReferenceEquals(active, operation) && !operation.Authorization.IsCanceled)
                    {
                        var earlier = context.Snapshot();
                        // Who said it travels with the words, so later replies (and memory) know who said what. A message with
                        // what the PC played keeps its marked lines as they are.
                        var said = operation.PcAudio ? input!.UserText : VoicePromptContext.Prefix(operation.Heard) + input!.UserText;
                        // A pass stays in the conversation too, so later replies know what was said around Martlet.
                        context.Add(said, passed ? $"[{LiveConversationConfiguration.SilentReply}]" : turn.Content.Text);
                        // Memory and learning names only ever read what the user said themselves, never what the PC played.
                        var spokenOwn = operation.PcAudio ? operation.UserWords : input.UserText;
                        var remembered = spokenOwn is null ? null
                            : operation.PcAudio ? VoicePromptContext.Prefix(operation.Heard) + spokenOwn : said;
                        if (operation.MemoryRequested && !passed && remembered is not null)
                            EnqueueCaptureLocked(operation.Authorization.Configuration, earlier, remembered, turn.Content.Text);
                        if (!passed && remembered is not null && operation.Heard is { Known.Count: > 0 } heard && voices is { Active: true } &&
                            VoiceNaming.Worth(heard, spokenOwn!, turn.Content.Text))
                            EnqueueNamingLocked(operation.Authorization.Configuration, heard, earlier, remembered, turn.Content.Text);
                    }
                }
            }
            operation.Publish(new(passed ? "listen.passed" : "runtime." + terminal.State, Finished: true, Quarantined: terminal.Quarantined,
                Policy: PolicyReason.DispatchAccepted, ProviderFailure: terminal.ProviderFailure, AudioFailure: terminal.Playback?.Error?.Code));
            await turn.OwnershipRelease.ConfigureAwait(false);
            if (turn.Snapshot.Quarantined)
            {
                operation.Publish(operation.Status with { Code = "conversation.cleanup_quarantined", Quarantined = true });
                await quarantine.Task.ConfigureAwait(false);
            }
            return new(terminal.State is ConversationState.Completed or ConversationState.Refused ? SetupWorkOutcome.Completed : SetupWorkOutcome.Failed);
        }
        catch (OperationCanceledException) when (worker.IsCancellationRequested || operation.OriginalCaller.IsCancellationRequested)
        {
            operation.Publish(new("conversation.canceled", Finished: true));
            return new(SetupWorkOutcome.Canceled);
        }
        catch (LiveActionException error)
        {
            operation.Publish(new(error.Code, Finished: true));
            return new(SetupWorkOutcome.Failed);
        }
        catch (DesktopMemoryException error)
        {
            operation.Publish(new(error.Code, Finished: true));
            return new(SetupWorkOutcome.Failed);
        }
        catch (MemoryException error)
        {
            operation.Publish(new($"memory.{error.Failure}", Finished: true));
            return new(SetupWorkOutcome.Failed);
        }
        catch (ContractException error)
        {
            operation.Publish(new("conversation.invalid_input", Finished: true, AudioFailure: error.Code));
            return new(SetupWorkOutcome.Failed);
        }
        catch (PolicyValidationException)
        {
            operation.Publish(new("policy.invalid_input", Finished: true));
            return new(SetupWorkOutcome.Failed);
        }
        finally
        {
            if (operation.Turn is { } turn)
            {
                await turn.StopAsync().ConfigureAwait(false);
                await turn.OwnershipRelease.ConfigureAwait(false);
                if (turn.Snapshot.Quarantined) await quarantine.Task.ConfigureAwait(false);
            }
            lock (gate)
            {
                if (lease is not null) policy.Release(lease);
                operation.FinishExecution();
                if (ReferenceEquals(active, operation)) RevokeLocked();
            }
        }
    }

    private CorrelationIds Ids() => new() { SessionId = runtime.SessionId, TurnId = Guid.NewGuid(), RequestId = Guid.NewGuid() };

    // Speech-to-text for one recorded utterance through its one-use upload permission; null (with the reason published) when
    // nothing usable came back.
    private async Task<TranscriptionResult?> TranscribeAsync(LiveConversationOperation operation, BoundedWaveAudio audio,
        OpenAiTranscriptionAdapter openAi, CancellationToken worker)
    {
        operation.Authorization.Check(worker);
        await operation.Authorization.ValidateSettingsAsync(worker).ConfigureAwait(false);
        var context = new ProviderRequestContext
        {
            Ids = Ids(), Epoch = operation.Capture!.Snapshot.Epoch,
            Deadline = operation.Authorization.Deadline(TimeSpan.FromSeconds(30))
        };
        var permission = operation.Authorization.AuthorizeAudio(context);
        operation.Publish(new("stt.uploading"));
        operation.BeginTranscription(clock, context.Deadline);
        TranscriptionResult result;
        try
        {
            var stt = operation.Authorization.Configuration.Route(SetupRole.Stt);
            // Listening handed to a paired host: the utterance goes only to its pinned gateway.
            result = operation.Authorization.Configuration.SttHostTarget() is { } listener
                ? await hostTranscription.TranscribeAsync(context, listener, stt.ModelId, audio,
                    LiveConversationConfiguration.TranscriptionLimits, permission, operation.OriginalCaller, worker).ConfigureAwait(false)
                // Parakeet on this PC: transcribed in memory here, nothing is sent anywhere.
                : operation.Authorization.Configuration.LocalStt()
                    ? await (localTranscription ?? throw new LiveActionException("conversation.configuration_unsupported"))
                        .TranscribeAsync(context, stt.ModelId, audio, LiveConversationConfiguration.TranscriptionLimits, permission,
                            operation.OriginalCaller, worker).ConfigureAwait(false)
                : await openAi.TranscribeAsync(context, stt.ModelId, audio,
                    LiveConversationConfiguration.TranscriptionLimits, permission, operation.OriginalCaller, worker).ConfigureAwait(false);
        }
        finally { operation.EndTranscription(); }
        operation.Authorization.Check(worker);
        operation.Transcription = result;
        if (result.Outcome == TranscriptionOutcome.Completed)
        {
            Succeeded(SetupRole.Stt);
            return result;
        }
        if (result.Outcome != TranscriptionOutcome.NoSpeech)
            LogFailure("Transcription", operation.Authorization.Configuration, SetupRole.Stt,
                $"outcome {result.Outcome}" + (result.Failure?.Code is { } sttCode ? $", provider {sttCode}" : ""));
        operation.Publish(new("stt." + result.Outcome, Finished: true, ProviderFailure: result.Failure?.Code));
        return null;
    }

    private static string? Join(params string?[] parts) =>
        parts.Where(part => part is not null).ToArray() is { Length: > 0 } present ? string.Join("\n\n", present) : null;

    // Recognition runs alongside speech-to-text and is usually done first; a slow one never holds the reply back for long.
    private async Task<HeardVoices?> HeardAsync(LiveConversationOperation operation, CancellationToken worker)
    {
        if (operation.Recognition is not { } pending) return null;
        var done = await Task.WhenAny(pending, Task.Delay(TimeSpan.FromSeconds(3), clock)).ConfigureAwait(false);
        operation.Authorization.Check(worker);
        if (done != pending) return null;
        var heard = await pending.ConfigureAwait(false);
        if (heard.Voices.Count > 0) operation.Publish(new("voices.recognized"));
        return heard;
    }

    /// <summary>Starts recognizing who spoke in the utterance on this PC while it is transcribed. The samples are a private
    /// copy that is cleared when recognition ends; a failure only means nobody is recognized this time.</summary>
    private void Recognize(LiveConversationOperation operation, ReadOnlySpan<byte> speech)
    {
        if (voices is not { Active: true } recognizer || speech.Length < 2) return;
        var samples = Pcm.ToFloats(speech);
        operation.Recognition = Task.Run(() =>
        {
            try { return recognizer.Recognize(samples); }
            // Recognition is best effort: any failure (native, model or file) only means nobody is named this time.
            catch (Exception error)
            {
                ErrorLog.Warn("Recognizing who spoke failed.", error);
                return HeardVoices.None;
            }
            finally { Array.Clear(samples); }
        });
    }

    private static bool IsFailure(ConversationSnapshot terminal) =>
        terminal.State is ConversationState.Failed or ConversationState.Partial || terminal.ProviderFailure is not null ||
        terminal.SpeechFailed;

    // A reply whose voice failed is a Speaking failure: its text still completes, so Thinking answered and counts as working.
    // The voice stopping never cuts the reply short; a reply that itself failed is a Thinking failure too.
    private void LogReplyFailure(string what, LiveConversationConfiguration configured, ConversationSnapshot terminal)
    {
        if (terminal.SpeechFailed || terminal.FailedProvider == ProviderRole.Tts)
            LogFailure("Spoken " + what.ToLowerInvariant(), configured, SetupRole.Tts, Describe(terminal));
        if (terminal.State is ConversationState.Failed or ConversationState.Partial && terminal.FailedProvider != ProviderRole.Tts)
            LogFailure(what, configured, SetupRole.Llm, Describe(terminal));
        else if (terminal.TextComplete) Succeeded(SetupRole.Llm);
    }

    private static string Describe(ConversationSnapshot terminal) =>
        $"state {terminal.State}, failure {terminal.Failure}" +
        (terminal.SpeechFailed ? $", voice stopped {terminal.SpeechFailure}" : "") +
        (terminal.ProviderFailure is { } provider ? $", provider {provider}" : "") +
        (terminal.SequenceFailure is { } sequence ? $", stream {sequence.Issue}" : "") +
        (terminal.Playback?.Error?.Code is { } audio ? $", audio {audio}" : "") +
        (terminal.ToolCalls > 0 ? $", {terminal.ToolCalls} tool call(s)" : "") +
        (terminal.ToolsRejected ? ", tools rejected" : "") +
        (terminal.FellBackAfter is { } after ? $", Thinking fallback asked after {after}" : "");

    // A reply the fallback answered: Thinking itself failed, so say so in the log (the provider's own explanation is
    // logged just before it by ProviderDiagnostics); the reply still counts as working.
    private static void NoteFallback(string what, LiveConversationConfiguration configured, ConversationSnapshot terminal)
    {
        if (terminal.FellBackAfter is not { } after || configured.Fallback is not { } fallback) return;
        var route = configured.Route(SetupRole.Llm);
        var answered = terminal.State is ConversationState.Completed or ConversationState.Refused;
        ErrorLog.Warn($"{what}: Thinking failed ({after}) on {route.Origin}, model {route.ModelId}; the Thinking fallback " +
            $"{fallback.Origin}, model {fallback.ModelId}, {(answered ? "answered instead" : "failed too")}.");
    }

    // One local log line per failed request naming the route it used, never what was said. The provider's own
    // explanation (HTTP status and message) is logged just before it by ProviderDiagnostics.
    private void LogFailure(string what, LiveConversationConfiguration configured, SetupRole role, string outcome)
    {
        var route = configured.Routes.SingleOrDefault(r => r.Role == role);
        ErrorLog.Warn($"{what} failed ({outcome}). {role} route: {route?.RouteType?.ToString() ?? "OpenAi"}, " +
            $"{route?.Origin ?? "no destination"}, model {route?.ModelId ?? "none"}.");
        lock (gate) failures[role] = new(role, what, outcome, clock.GetLocalNow());
        FailuresChanged?.Invoke();
    }

    private void Succeeded(SetupRole role)
    {
        bool cleared;
        lock (gate) cleared = failures.Remove(role);
        if (cleared) FailuresChanged?.Invoke();
    }

    // Lorebooks help but are never required: if lorebooks.json can't be used right now, the reply goes ahead without lore.
    private async Task<LorebookScanResult?> ScanLoreAsync(LiveConversationOperation operation, string current,
        IReadOnlyList<TextHistoryMessage> history, PersonaProfile? persona, CancellationToken worker)
    {
        if (lorebooks is null) return null;
        var loaded = await lorebooks.LoadAsync(worker).ConfigureAwait(false);
        operation.Authorization.Check(worker);
        if (!loaded.Loaded)
        {
            operation.LoreProblem = loaded.Error;
            operation.Publish(new("lorebook.unavailable"));
            return null;
        }
        var result = LorebookScanner.Scan(loaded.Library,
            new(current, history.Select(message => message.Text).ToArray(), persona?.Id, persona?.Name), Random.Shared.Next);
        return result.Included.Count == 0 && result.OverBudget.Count == 0 ? null : result;
    }

    private static void RecordLore(LiveConversationOperation operation, LorebookScanResult? lore, int used)
    {
        operation.LoreEntriesUsed = used;
        operation.LoreEntriesOmitted = (lore?.Included.Count ?? 0) - used + (lore?.OverBudget.Count ?? 0);
        operation.LoreTitles = lore?.Included.Take(used).Select(hit => hit.Entry.Label).ToArray() ?? [];
    }

    // Memory helps but is never required: if the store can't be read right now, the reply goes ahead without it.
    private async Task<DesktopMemoryRecall?> RecallAsync(LiveConversationOperation operation, string query, CancellationToken worker)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await memory!.RecallAsync(operation.Authorization.Configuration.Memory!, query,
                    DesktopMemoryService.MaximumRecalledFacts, worker).ConfigureAwait(false);
            }
            catch (DesktopMemoryException error) when (error.Code == "memory.retrieval_invalidated" && attempt == 0)
            {
                // A fact changed while it was read; read the current facts once more unless this action was revoked.
                operation.Authorization.Check(worker);
            }
            catch (Exception error) when (error is DesktopMemoryException or MemoryException or ContractException or
                IOException or UnauthorizedAccessException or ObjectDisposedException)
            {
                operation.Authorization.Check(worker);
                operation.MemoryProblem = error switch
                {
                    DesktopMemoryException app => app.Code,
                    MemoryException store => "memory." + store.Failure,
                    _ => "memory.unavailable"
                };
                operation.Publish(new("memory.unavailable"));
                return null;
            }
        }
    }

    private void EnqueueCaptureLocked(LiveConversationConfiguration configured, IReadOnlyList<TextHistoryMessage> earlier,
        string user, string reply)
    {
        if (!AutoCapture || memory is null || disposed || captureQuarantined || configured.Memory is not { Enabled: true } ||
            capturesPending >= MaximumPendingCaptures)
            return;
        capturesPending++;
        // Earlier lines heard from what the PC played are left out: memory only learns from the user.
        var job = new MemoryCaptureJob(configured,
            LiveConversationConfiguration.WithoutPcAudio(earlier.LastOrDefault(message => message.Role == TextHistoryRole.User)?.Text),
            earlier.LastOrDefault(message => message.Role == TextHistoryRole.Assistant)?.Text,
            user, reply, captureCancel.Token);
        captureTail = CaptureAfterAsync(captureTail, job);
    }

    private sealed record MemoryCaptureJob(LiveConversationConfiguration Configuration, string? EarlierUser, string? EarlierReply,
        string User, string Reply, CancellationToken Token)
    {
        public override string ToString() => nameof(MemoryCaptureJob);
    }

    private async Task CaptureAfterAsync(Task previous, MemoryCaptureJob job)
    {
        await previous.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        MemoryCaptureReport? report;
        try
        {
            report = await Task.Run(() => CaptureAsync(job)).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            ErrorLog.Warn("Remembering a conversation exchange failed.", error);
            report = new(Failure: "memory." + error.GetType().Name);
        }
        finally
        {
            lock (gate) capturesPending--;
        }
        if (report is { Failure: { } failure })
        {
            ErrorLog.Warn($"Remembering failed ({failure}).");
            // Say why once; the same problem again on later exchanges stays in the log until remembering works again.
            lock (gate)
            {
                if (failure == lastCaptureFailure) return;
                lastCaptureFailure = failure;
            }
        }
        else if (!job.Token.IsCancellationRequested)
            lock (gate) lastCaptureFailure = null;
        if (report is not null) MemoryCaptured?.Invoke(report);
    }

    // The store is shared with recall and the Memory page, and a fact can change while the model reads it: wait and try again.
    private static async Task<T> RetryStoreAsync<T>(Func<Task<T>> action, CancellationToken token)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await action().ConfigureAwait(false);
            }
            catch (Exception error) when (attempt < 2 && error is MemoryException { Failure: MemoryFailure.Busy or MemoryFailure.Conflict } or
                DesktopMemoryException { Code: "memory.busy" })
            {
                await Task.Delay(TimeSpan.FromSeconds(1 + attempt), token).ConfigureAwait(false);
            }
        }
    }

    private async Task<MemoryCaptureReport?> CaptureAsync(MemoryCaptureJob job)
    {
        var token = job.Token;
        try
        {
            token.ThrowIfCancellationRequested();
            var expected = job.Configuration.Memory!;
            var known = await RetryStoreAsync(() => memory!.KnownFactsAsync(expected, job.User, MemoryCapture.MaximumShownFacts, token),
                token).ConfigureAwait(false);
            var prompt = MemoryCapture.Prompt(job.EarlierUser, job.EarlierReply, job.User, job.Reply, known.Facts,
                job.Configuration.Prompts);
            var (answer, failure) = await AskAsync("Remembering", job.Configuration, prompt.Input, token).ConfigureAwait(false);
            if (answer is null) return token.IsCancellationRequested || failure is null ? null : new(Failure: failure);
            var operations = MemoryCapture.Parse(answer, prompt.ShownFacts);
            if (operations.Count == 0) return null;
            var shown = known.Facts.Take(prompt.ShownFacts).ToArray();
            var changes = await RetryStoreAsync(() => memory!.RememberAsync(expected.ConfigurationRevision, shown, operations, token),
                token).ConfigureAwait(false);
            return changes.Count == 0 ? null : new(changes);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception error) when (error is LiveActionException or DesktopMemoryException or MemoryException or
            ContractException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // Turning memory off, changing settings or revoking live work simply drops what was still being remembered.
            var code = error switch
            {
                LiveActionException live => live.Code,
                DesktopMemoryException app => app.Code,
                MemoryException store => "memory." + store.Failure,
                _ => "memory.unavailable"
            };
            return token.IsCancellationRequested || code is "memory.disabled" or "memory.configuration_changed" or
                "conversation.configuration_changed" or "conversation.revoked" ? null : new(Failure: code);
        }
    }

    /// <summary>One extra text-only request to the Thinking model on the background runtime (after a reply, never during
    /// one): the answer, or null with why it failed.</summary>
    private async Task<(string? Answer, string? Failure)> AskAsync(string purpose, LiveConversationConfiguration configuration,
        BoundedTextInput input, CancellationToken token)
    {
        var request = configuration.MemoryCaptureRequest(input);
        var capture = CaptureRuntime();
        var authorization = new ConversationAuthorization(configuration, voice: false, microphone: false, clock,
            () => !token.IsCancellationRequested, settings.LoadAsync, vault, token);
        authorization.BindInput(request.Input);
        Volatile.Write(ref captureAuthorization, authorization);
        try
        {
            var turn = capture.Start(request, authorization, token);
            var terminal = await turn.Completion.ConfigureAwait(false);
            await turn.OwnershipRelease.ConfigureAwait(false);
            NoteFallback(purpose, configuration, terminal);
            if (turn.Snapshot.Quarantined)
                lock (gate) captureQuarantined = true;
            var text = turn.Content.Text;
            // A model that declines or answers with nothing has nothing to add: that is not a failure. Chat Completions servers
            // report an empty answer as a malformed response.
            var nothing = terminal.State == ConversationState.Refused || string.IsNullOrWhiteSpace(text) &&
                (terminal.ProviderFailure is null && terminal.SequenceFailure?.Issue == Martlet.Core.Streaming.SequenceIssue.EmptyCompletion ||
                 terminal.ProviderFailure == ProviderFailureCode.ResponseSchema);
            // Cut off by the max reply length: the lines it finished still count.
            var finished = terminal.ProviderFailure == ProviderFailureCode.OutputTokenLimit && text.LastIndexOf('\n') is > 0 and var end
                ? text[..end] : null;
            if (!token.IsCancellationRequested && IsFailure(terminal))
            {
                if (nothing || finished is not null) ErrorLog.Warn($"{purpose} got no usable answer ({Describe(terminal)}); nothing changed.");
                else LogFailure(purpose, configuration, SetupRole.Llm, Describe(terminal));
            }
            else if (terminal.State == ConversationState.Completed) Succeeded(SetupRole.Llm);
            return terminal.State == ConversationState.Completed ? (text, null)
                : nothing ? (null, null)
                : finished is not null ? (finished, null)
                : (null, terminal.ProviderFailure?.ToString() ?? "runtime." + terminal.State);
        }
        finally
        {
            Interlocked.CompareExchange(ref captureAuthorization, null, authorization);
        }
    }

    private void EnqueueNamingLocked(LiveConversationConfiguration configured, HeardVoices heard, IReadOnlyList<TextHistoryMessage> earlier,
        string user, string reply)
    {
        if (!AutoCapture || voices is null || disposed || captureQuarantined || capturesPending >= MaximumPendingCaptures)
            return;
        capturesPending++;
        var job = new NamingJob(configured, heard,
            LiveConversationConfiguration.WithoutPcAudio(earlier.LastOrDefault(message => message.Role == TextHistoryRole.User)?.Text),
            earlier.LastOrDefault(message => message.Role == TextHistoryRole.Assistant)?.Text,
            user, reply, captureCancel.Token);
        captureTail = NameAfterAsync(captureTail, job);
    }

    private sealed record NamingJob(LiveConversationConfiguration Configuration, HeardVoices Heard, string? EarlierUser,
        string? EarlierReply, string User, string Reply, CancellationToken Token)
    {
        public override string ToString() => nameof(NamingJob);
    }

    private async Task NameAfterAsync(Task previous, NamingJob job)
    {
        await previous.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        IReadOnlyList<(Martlet.Core.Speakers.KnownVoice, string)>? learned = null;
        try
        {
            learned = await Task.Run(() => NameAsync(job)).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            ErrorLog.Warn("Learning names from a conversation exchange failed.", error);
        }
        finally
        {
            lock (gate) capturesPending--;
        }
        if (learned is { Count: > 0 }) VoicesNamed?.Invoke(learned);
    }

    /// <summary>Asks the Thinking model which names the voices in a finished exchange go by and adds them to those voices.</summary>
    private async Task<IReadOnlyList<(Martlet.Core.Speakers.KnownVoice, string)>?> NameAsync(NamingJob job)
    {
        var token = job.Token;
        try
        {
            token.ThrowIfCancellationRequested();
            var prompt = VoiceNaming.Prompt(job.Heard, job.EarlierUser, job.EarlierReply, job.User, job.Reply, job.Configuration.Prompts);
            var (answer, _) = await AskAsync("Learning names", job.Configuration, prompt.Input, token).ConfigureAwait(false);
            if (answer is null || voices is null) return null;
            var learned = new List<(Martlet.Core.Speakers.KnownVoice, string)>();
            var persona = job.Configuration.Persona?.Name;
            foreach (var (id, name) in VoiceNaming.Parse(answer, prompt.Voices, persona is null ? [] : [persona]))
            {
                token.ThrowIfCancellationRequested();
                voices.AddHeardName(id, name);
                if (voices.Roster.Resolve(id) is { } voice) learned.Add((voice, name));
            }
            return learned;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception error) when (error is LiveActionException or ContractException or IOException or UnauthorizedAccessException or
            InvalidOperationException)
        {
            return null;
        }
    }

    private ConversationRuntime CaptureRuntime()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return captureRuntime ??= runtimeFactory?.Invoke(captureCredentials, clock) ??
                ConversationRuntime.Create(captureCredentials, clock: clock, hostText: new HostTextClient());
        }
    }

    private void CancelCapturesLocked()
    {
        var previous = captureCancel;
        captureCancel = new();
        previous.CancelAsync().Forget();
    }

    // Reads the capture's own 20 ms frames (no second audio queue) and releases it when the speaker pauses.
    // Returns the speech range to send, or null when nobody spoke before the idle restart. With echo reduction, sound that is
    // mostly what the speakers played (Martlet's own voice, a video) is let go like a cough: it is never you. Only the
    // microphone talks over Martlet, and only with a sustained voice (TalkOverDetector).
    private async Task<SpeechRange?> EndpointAsync(LiveConversationOperation operation, CaptureRun run)
    {
        var settings = operation.Listening!.Activity;
        var detector = new EnergyVoiceActivityDetector(settings);
        var talkOver = operation.Listening.Pc ? null : new TalkOverDetector();
        var echo = operation.Echo;
        var minimumFrames = (int)(ListeningOptions.MinimumUtterance.TotalMilliseconds / 20);
        var frame = new byte[EnergyVoiceActivityDetector.FrameBytes];
        // Running counts of loud frames that were a voice the speakers don't explain, and that were the speakers' sound.
        var userSum = new List<int> { 0 };
        var speakerSum = new List<int> { 0 };
        var started = clock.GetTimestamp();
        int index = 0, accepted = -1;
        try
        {
            while (!run.Completion.IsCompleted)
            {
                bool copied;
                do
                {
                    try { copied = run.TryCopyMonoFrame(index, frame); }
                    catch (OperationCanceledException) { copied = false; }
                    if (!copied) break;
                    var speakers = echo?.Speakers((long)index * EnergyVoiceActivityDetector.FrameSamples, EnergyVoiceActivityDetector.FrameSamples) == true;
                    index++;
                    var transition = detector.Process(frame);
                    operation.VoiceLevel = detector.LastLevelDb;
                    var loud = detector.LastFrameLoud;
                    userSum.Add(userSum[^1] + (loud && !speakers ? 1 : 0));
                    speakerSum.Add(speakerSum[^1] + (loud && speakers ? 1 : 0));
                    if (talkOver?.Process(loud, speakers) == true) operation.TalkingOver = true;
                    if (transition == VoiceActivityTransition.SpeechStarted)
                    {
                        if (accepted < 0) operation.Publish(new("mic.hearing_speech"));
                    }
                    else if (transition == VoiceActivityTransition.SpeechEnded)
                    {
                        // A cough or click, or what the speakers played, is ignored; keep listening for real speech.
                        if (accepted < 0 && (detector.SpeechEndFrame - detector.SpeechStartFrame < minimumFrames || !Voice()))
                        {
                            operation.Publish(new("mic.listening"));
                            continue;
                        }
                        if (accepted < 0) accepted = Onset();
                        operation.Hearing = true;
                        await run.ReleaseAsync().ConfigureAwait(false);
                        return Range(accepted, detector.SpeechEndFrame);
                    }
                    if (detector.Speaking && accepted < 0 && index - detector.SpeechStartFrame >= minimumFrames && Voice())
                    {
                        accepted = Onset();
                        operation.Hearing = true;
                    }
                }
                while (true);
                // Always listening stops listening the moment Martlet starts speaking, unless you were already talking.
                if (accepted < 0 && operation.Listen && Held(operation.Listening!))
                {
                    operation.Publish(new("listen.held"));
                    return null;
                }
                if (accepted < 0 && !detector.Speaking && clock.GetElapsedTime(started) >= ListeningOptions.IdleRestart)
                    return null;
                await Task.WhenAny(run.Completion, Task.Delay(TimeSpan.FromMilliseconds(20), clock)).ConfigureAwait(false);
            }
            // Duration limit or Finish: send everything from the onset to the end of the recording.
            if (accepted < 0 && detector.Speaking && Voice()) accepted = Onset();
            if (accepted >= 0) operation.Hearing = true;
            return accepted < 0 ? null : Range(accepted, 0) with { EndSampleExclusive = int.MaxValue };
        }
        finally
        {
            CryptographicOperations.ZeroMemory(frame);
            operation.VoiceLevel = -100;
        }

        // The speech under way is someone's voice, not mostly what the speakers played (or the user has talked over them).
        bool Voice() => talkOver?.Sustained == true || !SpeakersMostly(detector.SpeechStartFrame);
        bool SpeakersMostly(int from) => from >= 0 && from < userSum.Count &&
            speakerSum[^1] - speakerSum[from] > userSum[^1] - userSum[from];
        // Where what is sent starts: the speech's onset, or where the user's own voice began over what the speakers played.
        int Onset() => SpeakersMostly(detector.SpeechStartFrame) && talkOver is { StretchStartFrame: >= 0 } over
            ? Math.Max(detector.SpeechStartFrame, over.StretchStartFrame) : detector.SpeechStartFrame;

        SpeechRange Range(int startFrame, int endFrame) => new(
            Math.Max(0, startFrame * EnergyVoiceActivityDetector.FrameSamples - EnergyVoiceActivityDetector.Samples(settings.PreRoll)),
            endFrame * EnergyVoiceActivityDetector.FrameSamples + EnergyVoiceActivityDetector.Samples(settings.Tail));
    }

    private async Task<BoundedWaveAudio?> CaptureAsync(LiveConversationOperation operation)
    {
        var speech = await CaptureSpeechAsync(operation).ConfigureAwait(false);
        if (speech is null) return null;
        try { return Screen(operation, speech); }
        finally { CryptographicOperations.ZeroMemory(speech); }
    }

    // Records one utterance and returns a private copy of the speech to send (the caller clears it), or null with the reason
    // published. The microphone is released before this returns.
    private async Task<byte[]?> CaptureSpeechAsync(LiveConversationOperation operation)
    {
        var permission = operation.Authorization;
        permission.Check();
        var audio = permission.Configuration.Audio!;
        var selected = audio.Input;
        // What the PC plays is its own device; echo reduction hears the output Martlet's own voice plays on (the chosen one, or
        // Windows' default).
        var pc = operation.Listening?.Pc == true;
        // Hands-free listening also learns, frame by frame, which of what it hears was the speakers' sound.
        if (!pc && operation.HandsFree && operation.Listening?.ReduceEcho == true && echoReducer is not null)
            operation.Echo = new EchoTimeline(LiveConversationConfiguration.CaptureDuration);
        var devices = pc ? pcAudio ?? throw new LiveActionException("conversation.configuration_unsupported")
            : operation.Listening?.ReduceEcho == true && echoReducer is not null
            ? echoReducer.For(audio.Output.EndpointId, operation.Echo) : captureDevices;
        await using var microphone = new MicrophoneCapture(runtime.SessionId, devices,
            new() { MaximumDuration = LiveConversationConfiguration.CaptureDuration, MaximumPcmBytes = 800_000 }, clock);
        var request = new CaptureRequest(Ids(), Interlocked.Increment(ref captureEpoch),
            pc ? new(InputPolicy.FollowDefaultOnNextPress)
                : new(selected.EndpointId is null ? InputPolicy.FollowDefaultOnNextPress : InputPolicy.FixedEndpoint, selected.EndpointId),
            LiveConversationConfiguration.CaptureDuration, permission.Deadline(LiveConversationConfiguration.CapturePermission, fromAcceptance: true));
        var run = microphone.Press(request, new(request, true), operation.OriginalCaller);
        operation.Attach(run);
        operation.Publish(new(operation.HandsFree ? "mic.listening" : "mic.capturing"));
        try
        {
            SpeechRange? heard = null;
            if (operation.HandsFree)
            {
                heard = await EndpointAsync(operation, run).ConfigureAwait(false);
                if (heard is null)
                {
                    // A microphone that failed (absent, busy, denied) ends the run by itself; say so instead of "no speech",
                    // which would quietly re-arm listening on a microphone that can't work.
                    var failed = run.Completion.IsCompleted ? await run.Completion.ConfigureAwait(false) : null;
                    var held = operation.Status.Code == "listen.held";
                    await run.CancelAsync().ConfigureAwait(false);
                    await run.Completion.ConfigureAwait(false);
                    permission.Check();
                    operation.Publish(failed is { State: CaptureState.Failed }
                        ? new LiveConversationStatus("mic." + failed.State, Finished: true, AudioFailure: failed.Error?.Code)
                        : new LiveConversationStatus(held ? "listen.held" : "mic.no_speech", Finished: true));
                    return null;
                }
            }
            var terminal = await run.Completion.ConfigureAwait(false);
            permission.Check();
            using var utterance = run.TakeUtterance();
            if (terminal.State != CaptureState.Completed || utterance is null)
            {
                operation.Publish(new("mic." + terminal.State, Finished: true, AudioFailure: terminal.Error?.Code));
                return null;
            }
            byte[] pcm = new byte[utterance.ByteCount];
            try
            {
                permission.Check();
                utterance.CopyPcmTo(pcm);
                var total = pcm.Length / 2;
                // Hands-free uploads only the detected speech (with pre-roll/tail), not the idle wait before it.
                var start = Math.Min(heard?.StartSample ?? 0, total);
                var end = Math.Min(heard?.EndSampleExclusive ?? total, total);
                return pcm.AsSpan(start * 2, Math.Max(0, end - start) * 2).ToArray();
            }
            finally
            {
                CryptographicOperations.ZeroMemory(pcm);
                utterance.Dispose();
            }
        }
        finally
        {
            await run.CancelAsync().ConfigureAwait(false);
            var release = await run.DeviceRelease.ConfigureAwait(false);
            if (!release.Released)
            {
                operation.Publish(new("mic.cleanup_quarantined", Finished: true, Quarantined: true, AudioFailure: release.Error?.Code));
                await quarantine.Task.ConfigureAwait(false);
            }
        }
    }

    // Voice ID (before anything is uploaded), then who is talking alongside speech-to-text: the audio to transcribe, or null
    // with the reason published.
    private BoundedWaveAudio? Screen(LiveConversationOperation operation, byte[] speech)
    {
        var permission = operation.Authorization;
        permission.Check();
        if (operation.Voiceprint is { } voiceprint)
        {
            operation.Publish(new("speaker.checking"));
            var check = SpeakerVerifier.Check(VoiceIdentity.Encoder, voiceprint.Embedding, voiceprint.Threshold, speech);
            permission.Check();
            operation.SpeakerCheck = check;
            if (check.Verdict != SpeakerVerdict.User)
            {
                operation.Publish(new(check.Verdict == SpeakerVerdict.TooShort ? "speaker.too_short" : "speaker.not_user", Finished: true));
                return null;
            }
            operation.Publish(new("speaker.verified"));
        }
        // Voices are recognized (and learned) only from the microphone, never from what the PC plays.
        if (operation.Listening?.Pc != true) Recognize(operation, speech);
        if (speech.Length < 2)
        {
            operation.Publish(new("mic.no_speech", Finished: true));
            return null;
        }
        var wave = BoundedWaveAudio.FromPcm(CapturedUtterance.Format, speech);
        operation.Publish(new("mic.transferred_and_cleared"));
        return wave;
    }

    public async ValueTask DisposeAsync()
    {
        LiveConversationOperation? owned;
        LiveListener[] stopListening;
        LiveListener? microphone, playing;
        lock (gate)
        {
            disposed = true;
            memory?.Invalidate();
            ClearContextLocked();
            CancelCapturesLocked();
            owned = RevokeLocked();
            stopListening = RevokeListeningLocked();
            microphone = listener;
            playing = pcListener;
        }
        owned?.Cancel("conversation.closed");
        Cancel(stopListening);
        echoReducer?.Forget();
        DisposeCaptureRuntimeAsync().Forget();
        // Never wait for native cleanup on the dispatcher. The shared slot remains reserved until real exit.
        await runtime.DisposeAsync().ConfigureAwait(false);
        if (owned is null || owned.Worker.Completion.IsCompleted) transcription.Dispose();
        else DisposeAfterReleaseAsync(owned).Forget();
        DisposeAfterListening(microphone, listenTranscription);
        DisposeAfterListening(playing, pcTranscription);
    }

    // A listener's speech-to-text is disposed once its loop has let go of it.
    private static void DisposeAfterListening(LiveListener? listening, OpenAiTranscriptionAdapter adapter)
    {
        if (listening is null || listening.Worker.Completion.IsCompleted) adapter.Dispose();
        else Release(listening.Worker.Completion).Forget();

        async Task Release(Task loop)
        {
            await loop.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            adapter.Dispose();
        }
    }
    private async Task DisposeCaptureRuntimeAsync()
    {
        Task tail;
        lock (gate) tail = captureTail;
        await tail.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        ConversationRuntime? owned;
        lock (gate) owned = captureRuntime;
        if (owned is not null) await owned.DisposeAsync().ConfigureAwait(false);
    }
    private async Task DisposeAfterReleaseAsync(LiveConversationOperation operation)
    {
        await operation.Worker.Completion.ConfigureAwait(false);
        transcription.Dispose();
    }
}
