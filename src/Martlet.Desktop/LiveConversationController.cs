using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using Martlet.Audio;
using Martlet.Avatar.Hosting;
using Martlet.Conversation;
using Martlet.Core.Contracts;
using Martlet.Core.Creations;
using Martlet.Core.Lorebooks;
using Martlet.Core.Settings;
using Martlet.Core.Singing;
using Martlet.Participation;
using Martlet.Providers;
using Martlet.Memory;

namespace Martlet.Desktop;

internal sealed record LiveConversationStatus(string Code, bool Finished = false, bool Quarantined = false,
    PolicyReason? Policy = null, ProviderFailureCode? ProviderFailure = null, ErrorCode? AudioFailure = null);

/// <summary>Talking over Martlet stopped it: why (<see cref="BargeInPolicy"/>), how long after the user's voice began that was
/// decided, how many quick checks of their words it took and when (controller clock) their voice began.</summary>
internal sealed record TalkOverResult(BargeInDecision Decision, TimeSpan After, int Checks, long StartedAt);

// HandsFree: voice activity endpoints each utterance. RequireVoiceId: only the enrolled voice is uploaded. Hear: the recording
// is kept for a Thinking model that hears (Companion › Listening › Let Thinking hear my voice); HearLocalOnly: only because it
// was never chosen, so only while the recording stays on this PC (checked again for each message). Straight: with Hear, what was
// said goes straight to a Thinking model that hears as the recording alone, and speech-to-text runs beside the reply
// (Companion › Listening › When Thinking can hear you). BargeIn: talking over a reply with real words (BargeInPolicy) stops
// it; without it, always listening still listens while Martlet speaks whenever echo reduction works, and what it hears waits
// for the reply to finish. ReduceEcho: what
// the PC plays (Martlet's voice included) is removed from the
// microphone first (Companion › Listening › Reduce echo from my speakers), so speakers work without headphones. Pc: listens to
// what this PC plays instead of the microphone (Companion › Listening › Hear what this PC plays): never Voice ID, voice
// recognition, a recording for Thinking or memory. WordCheck: how readily what was heard counts as words (Companion › Listening ›
// Word check; UtteranceFilter and BargeInPolicy).
internal sealed record ListeningOptions(bool HandsFree, VoiceActivitySettings Activity, bool RequireVoiceId, bool Hear = false,
    bool BargeIn = false, bool ReduceEcho = false, bool Pc = false, ListeningSensitivity WordCheck = ListeningSensitivity.Normal,
    bool Straight = false, bool HearLocalOnly = false)
{
    internal static TimeSpan IdleRestart => TimeSpan.FromSeconds(12);
    internal static TimeSpan MinimumUtterance => TimeSpan.FromMilliseconds(450);

    /// <summary>Thinking may hear the recording with this configuration: Hear, and, when that is only the never-chosen default,
    /// the recording stays on this PC.</summary>
    internal bool HearsWith(LiveConversationConfiguration configuration) => Hear && (!HearLocalOnly || configuration.RecordingStaysOnThisPc());

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
    private bool voiceMuted;
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
    /// <summary>When each step before the reply happened (<see cref="ReplyTimeline"/>), for the desktop log's reply latency line.</summary>
    [JsonIgnore] internal ReplyTimeline? LatencyTimeline { get; set; }
    /// <summary>The controller-clock timestamp the reply's turn started at (0 until it starts).</summary>
    [JsonIgnore] internal long ReplyStartedAt { get; set; }
    [JsonIgnore] internal string? Transcript { get; set; }
    /// <summary>What the user said, kept only while Thinking may hear it (never saved); null otherwise.</summary>
    [JsonIgnore] internal BoundedWaveAudio? Recording { get; set; }
    /// <summary>The reply's request carried the user's recording with the transcript.</summary>
    internal bool VoiceSent { get; set; }
    /// <summary>An utterance that goes straight to Thinking: its words, which speech-to-text makes beside the reply.</summary>
    [JsonIgnore] internal SpokenWords? Words { get; set; }
    /// <summary>A reply to what went straight to Thinking (the recording alone): the words of each utterance it answers, in order.</summary>
    [JsonIgnore] internal IReadOnlyList<SpokenWords>? StraightWords { get; set; }
    /// <summary>The reply's request carried the user's recording alone, with no transcript (straight to Thinking).</summary>
    internal bool Straight { get; set; }
    /// <summary>What went straight to Thinking turned out not to be words, and the reply was dropped before it played.</summary>
    internal bool NotWords { get; set; }
    /// <summary>The picture of what vision watches that goes with the user's message, when vision is on.</summary>
    [JsonIgnore] internal SeenScreen? Seen { get; init; }
    /// <summary>The reply's request carried <see cref="Seen"/>'s picture.</summary>
    internal bool ScreenSent { get; set; }
    /// <summary>The reply's request as it was sent, which the request after the reply continues on a Thinking model on this
    /// PC (<see cref="AfterReply"/>). In memory only, never saved.</summary>
    [JsonIgnore] internal BoundedTextInput? Sent { get; set; }
    internal Guid? PersonaRevision { get; set; }
    internal ResponseStyle? ResponseStyle { get; set; }
    internal int ContextMessages { get; set; }
    internal int ContextMessagesOmitted { get; set; }
    internal bool MemoryRequested { get; set; }
    internal int MemoryFactsUsed { get; set; }
    internal int MemoryFactsOmitted { get; set; }
    /// <summary>How many exchanges of earlier conversations went in this message's notes (it referred to one).</summary>
    internal int PastExchanges { get; set; }
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
    /// <summary>A reply Martlet starts on its own to bring up finished background work (think_longer), not an answer to the user.</summary>
    internal bool Report { get; init; }
    /// <summary>The finished background jobs this reply brings into the conversation (its own message for a report, or the notes
    /// of the user's message); completed once the exchange is kept, otherwise returned for the next reply.</summary>
    [JsonIgnore] internal BackgroundDelivery? Delivery { get; set; }
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
    /// <summary>Companion › Vision › How often it comments while vision or hearing this PC is turned on (null otherwise): with
    /// Martlet decides, the reply is told how to switch the level and may end with a chattiness tag.</summary>
    internal ChattinessChoice? BackgroundChattiness { get; init; }
    private int hearing;
    /// <summary>Speech longer than a cough or click is being recorded right now.</summary>
    internal bool Hearing { get => Volatile.Read(ref hearing) != 0; set => Volatile.Write(ref hearing, value ? 1 : 0); }
    private int talkingOver;
    /// <summary>The user has talked over Martlet with real words (<see cref="BargeInPolicy"/>, from a quick transcript of what
    /// they said so far): never a hum, a cough, laughter or what this PC plays.</summary>
    internal bool TalkingOver { get => Volatile.Read(ref talkingOver) != 0; set => Volatile.Write(ref talkingOver, value ? 1 : 0); }
    /// <summary>Why talking over Martlet stopped it, and how long after the user's voice began that was decided.</summary>
    internal TalkOverResult? TalkOver { get => Volatile.Read(ref talkOver); set => Volatile.Write(ref talkOver, value); }
    private TalkOverResult? talkOver;
    private int stoppedSong;
    /// <summary>This utterance asked Martlet to stop singing, and the song stopped.</summary>
    internal bool StoppedSong { get => Volatile.Read(ref stoppedSong) != 0; set => Volatile.Write(ref stoppedSong, value ? 1 : 0); }
    /// <summary>What Martlet was singing when this message was heard (its title and where it was), for the Said while you were
    /// singing note; null when it wasn't singing.</summary>
    internal (string Title, string Where)? WhileSinging { get; init; }
    /// <summary>What this utterance's sound was: how much was a voice (loud 20 ms frames the speakers don't explain).</summary>
    internal TimeSpan? Voiced { get; set; }
    /// <summary>How long the utterance's voice went on, from its onset to where the silence began, leaving out frames the
    /// speakers explain (<see cref="UtteranceContext.Speech"/>).</summary>
    internal TimeSpan? Speech { get; set; }
    /// <summary>The controller-clock timestamp the utterance's voice began at (0 when unknown).</summary>
    internal long SpeechStartedAt { get; set; }
    /// <summary>Always listening dropped this utterance (it wasn't words); the transcript is kept only to show it as ignored.</summary>
    internal UtteranceDecision? Ignored { get; set; }
    /// <summary>The utterance's words, said over Martlet, stop it (where speech-to-text isn't on this PC, or a quick check missed them).</summary>
    internal BargeInDecision? Interrupts { get; set; }
    /// <summary>What this reply is, said aloud: a reply (stops for real words) or a song (stops only when asked to).</summary>
    internal PlaybackMode Playback { get; init; } = PlaybackMode.Reply;
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
    /// <summary>The glance offered the Thinking model the look tags that turn the character's eyes (Martlet decides where the
    /// character looks).</summary>
    internal bool LookOffered { get; set; }
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
        bool mute;
        lock (gate)
        {
            Volatile.Write(ref turn, run);
            mute = voiceMuted;
        }
        if (Authorization.IsCanceled) run.StopAsync().Forget();
        else if (mute) run.MuteVoice();
    }
    /// <summary>Martlet's voice was muted while this reply ran: its turn (now, or once it starts) stops saying it aloud, and the
    /// rest shows as text.</summary>
    internal void MuteVoice()
    {
        ConversationTurn? attached;
        lock (gate)
        {
            voiceMuted = true;
            attached = Volatile.Read(ref turn);
        }
        attached?.MuteVoice();
    }
    internal void ReleasePress()
    {
        Interlocked.Exchange(ref releasedPress, 1);
        // Push-to-talk's wait counts from letting go of the talk button.
        LatencyTimeline?.Restart(ReplyTimeline.YouLetGo);
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
internal sealed partial class LiveConversationController : IAsyncDisposable
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
    // Parakeet on this PC, also used for the quick check of what is said over Martlet (BargeInGate): free and private.
    private readonly ILocalTranscriber? localWords;
    // When Martlet last finished a reply that asked something (controller clock; 0: not lately), so a short answer counts.
    private long askedAt;
    private readonly LocalVoices? voices;
    private readonly ParticipationPolicy policy;
    private readonly ConversationContextBuffer context;
    private readonly string? dataDirectory;
    private readonly TimeProvider clock;
    private readonly Func<int, int> nextStyle;
    private readonly Action? revokeAvatar;
    // The desktop character's emotes and motions a reply may use, for the speaking engine (null: a reply that isn't spoken).
    private readonly Func<SpeechEngine?, PromptSettings?, CharacterActionPrompt?>? characterActions;
    private readonly DesktopMemoryService? memory;
    // The record of conversations on this PC (Companion › Memory › Conversation history), and which conversation this is: a
    // conversation runs until the exchanges kept in mind are cleared (Refresh context, pause, lock, closing the talk window).
    private readonly DesktopConversationHistory? history;
    private Guid conversationId = Guid.NewGuid();
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
    // How many input tokens the last reply or glance read and how many of them came from the model's prompt cache, when the
    // provider said; shown on the talk window's context line.
    private (long Input, long Cached)? lastCache;
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
    // Speech-to-text for what went straight to Thinking: one utterance after another, beside the replies (TranscribeWordsAsync).
    private Task wordsTail = Task.CompletedTask;
    // Hearing what this PC plays runs the same way on a slot of its own, with its own speech-to-text credentials.
    private readonly PcAudioCaptureFactory? pcAudio;
    private readonly SetupOperationRunner pcSlot = new();
    private readonly OpenAiTranscriptionAdapter pcTranscription;
    private LiveListener? pcListener;
    private LiveConversationOperation? pcTranscribing;
    private long listenEpoch, spokeUntil;
    // Thinking models that rejected a recording this app session; they get the transcript only until Martlet restarts.
    private readonly HashSet<string> deafModels = new(StringComparer.Ordinal);
    // Thinking models that refused the Thinking steps choice this app session; they get their own default until Martlet restarts.
    private readonly HashSet<string> reasoningRefused = new(StringComparer.Ordinal);
    // Background work Martlet started during the conversation (think_longer), its own text runtime and credentials (bound to the
    // one think's request at a time), where Deep thinking thinks on this PC (deep-thinking.json, read when the conversation is
    // set up or the page saves it), and who the last spoken reply heard.
    private readonly BackgroundJobs jobs;
    private readonly ConversationCredentialSource thinkCredentials;
    private ConversationRuntime? thinkRuntime;
    private ICredentialAuthority? thinkAuthorization;
    private DeepThinkingSettings deepThinking = new();
    // Where the running think works and whether it can run there (for background-jobs.json).
    private ThinkPlace? thinkingWhere;
    private sealed record ThinkPlace(string Where, DeepThinkingPlan Plan);
    private BackgroundThink? thinking;
    private (bool Spoken, HeardVoices? Heard, ChattinessChoice? Chattiness) lastAsked;
    // The chattiness level Martlet picked while it decides how chatty it is: Normal until a reply or glance switches it, then
    // kept until Martlet closes.
    private Chattiness decided = Chattiness.Normal;
    private readonly object statusGate = new();
    // Singing (sing_song, play_song, stop_singing): the songs and the one playing, the output device speech plays through, and
    // a text runtime of the song job's own for writing lyrics (bound to that one request at a time), whose think yields like
    // think_longer's.
    private readonly ConversationSinging? singing;
    private readonly IPlaybackDeviceFactory playbackDevices;
    private readonly ConversationCredentialSource songCredentials;
    private ConversationRuntime? songRuntime;
    private ICredentialAuthority? songAuthorization;
    // How the shared Creations library's perform_creation sings a song in this conversation.
    private readonly IDisposable? songHandler;

    internal bool IsRunning => operations.IsRunning;
    /// <summary>A reply (or a comment on the screen) is running on the shared setup slot.</summary>
    internal bool Replying { get { lock (gate) return active is { Worker.Completion.IsCompleted: false }; } }
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
    /// <summary>The input tokens the last reply or glance read and how many came from the model's prompt cache; null until a
    /// provider reported both.</summary>
    internal (long Input, long Cached)? LastCache { get { lock (gate) return lastCache; } }
    /// <summary>Martlet's data directory (null in tests), where model-limits.json is kept.</summary>
    internal string? DataDirectory => dataDirectory;
    internal ParticipationSnapshot PolicySnapshot => policy.Snapshot;
    internal (bool Paused, bool Muted, bool Locked) Controls { get { lock (gate) return (paused, muted, locked); } }
    /// <summary>Raised off the dispatcher after a finished exchange changed memory or could not be remembered.</summary>
    internal event Action<MemoryCaptureReport>? MemoryCaptured;
    /// <summary>Raised off the dispatcher after learning names changed voices from a finished exchange (names learned, a name
    /// dropped, two voices merged).</summary>
    internal event Action<IReadOnlyList<Martlet.Core.Speakers.VoiceUpdateResult>>? VoicesNamed;
    /// <summary>Tests turn background remembering off to inspect only the reply request.</summary>
    internal bool AutoCapture { get; set; } = true;
    internal Task MemoryCaptureIdle { get { lock (gate) return captureTail; } }
    /// <summary>The Home Assistant connection consulted on user-started turns while its control is on.</summary>
    internal SmartHome? Home => smartHome;
    /// <summary>The MCP servers whose tools user-started replies may call.</summary>
    internal McpToolService? Tools => tools;
    /// <summary>The record of conversations on this PC, when Martlet has one.</summary>
    internal DesktopConversationHistory? History => history;
    /// <summary>The conversation going on: a new one starts whenever the exchanges kept in mind are cleared.</summary>
    internal Guid ConversationId { get { lock (gate) return conversationId; } }
    /// <summary>How echo reduction went the last time Martlet listened; null when this controller has none.</summary>
    internal EchoReductionReport? EchoReport => echoReducer?.Report;
    /// <summary>Martlet can hear what this PC plays (Companion › Listening › Hear what this PC plays).</summary>
    internal bool CanHearPc => pcAudio is not null;
    /// <summary>Martlet's background work in this conversation (think_longer): what runs, what finished and what waits to be
    /// brought up.</summary>
    internal BackgroundJobs Jobs => jobs;
    /// <summary>The background-jobs.json status file in the data directory (kinds, states and times only; never a task or
    /// result), which MCP's think_longer_status reads.</summary>
    internal const string JobsStatusFile = "background-jobs.json";
    /// <summary>Whether hearing what this PC plays leaves Martlet's own voice out (null until it first listened); without it,
    /// that listening holds off while Martlet speaks.</summary>
    internal bool? PcWithoutMartlet => pcAudio?.WithoutMartlet;
    /// <summary>The one output hearing what this PC plays heard last time (another output was in use, or Windows can't leave
    /// Martlet out), or null.</summary>
    internal string? PcOutput => pcAudio?.Output;
    /// <summary>The controller's clock, for timing what it measured (such as when the user's voice began).</summary>
    internal TimeProvider Clock => clock;

    /// <summary>The level Martlet picked while it decides how chatty it is (Companion › Vision › How often it comments: Martlet
    /// decides): Normal until a reply or glance switches it with a chattiness tag, then kept until Martlet closes.</summary>
    internal Chattiness DecidedChattiness { get { lock (gate) return decided; } }
    /// <summary>Raised off the dispatcher when a reply or glance switched <see cref="DecidedChattiness"/>: the level before and
    /// the new one.</summary>
    internal event Action<Chattiness, Chattiness>? ChattinessDecided;

    /// <summary>Takes the level a finished reply or glance switched to (the last chattiness tag it wrote) while Martlet decides
    /// how chatty it is. A reply cut off before its words were all written (restarted, stopped early) switches nothing.</summary>
    private void Decide(ConversationTurn turn, ConversationSnapshot terminal, string what)
    {
        if (!terminal.TextComplete || ChattinessTags.Last(turn.Controls) is not { } level) return;
        Chattiness before;
        lock (gate)
        {
            before = decided;
            decided = level;
        }
        if (before == level) return;
        ErrorLog.Info($"Chattiness: Martlet went from {ChattinessTags.Name(before)} to {ChattinessTags.Name(level)} " +
            $"({what}; Martlet decides).");
        ChattinessDecided?.Invoke(before, level);
    }

    /// <summary>How long after Martlet asks something a short answer ("yes", "mm-hmm") counts as one.</summary>
    internal static TimeSpan AnswerWindow => TimeSpan.FromSeconds(30);

    /// <summary>What Martlet is saying aloud right now (a reply or remark, or a song), or null while it isn't speaking. A reply
    /// over a song counts as the reply (it stops for real words; the song keeps going unless asked to stop).</summary>
    internal PlaybackMode? Speaking
    {
        get
        {
            lock (gate)
                if (active is { Worker: not null } current && !current.OwnershipReleased &&
                    current.Turn?.Snapshot is { State: ConversationState.Playing } or { MayHavePlayed: true })
                    return current.Playback;
            return singing?.Playing == true ? PlaybackMode.Song : null;
        }
    }

    /// <summary>A reply or remark is being said (or may still be): a song ducks under it, and a lead-in waits for it.</summary>
    private bool ReplySpeaking()
    {
        lock (gate)
            return active is { Worker: not null } current && !current.OwnershipReleased &&
                current.Turn?.Snapshot is { State: ConversationState.Playing } or { MayHavePlayed: true };
    }

    /// <summary>Martlet's songs in this conversation (sing_song, play_song, stop_singing), or null where it can't sing.</summary>
    internal ConversationSinging? Singing => singing;

    /// <summary>What the utterance filter and barge-in policy know besides the words: the voice, the engine's evidence, whether
    /// Martlet just asked something, and the persona's name (which, like "Martlet", addresses it).</summary>
    private UtteranceContext WordsContext(LiveConversationOperation operation, TimeSpan? voiced, TranscriptionEvidence? evidence,
        TimeSpan? speech = null)
    {
        var asked = Interlocked.Read(ref askedAt);
        return new()
        {
            Voiced = voiced, Speech = speech, Evidence = evidence,
            AfterQuestion = asked != 0 && clock.GetElapsedTime(asked) < AnswerWindow,
            Names = operation.Authorization.Configuration.Persona?.Name is { Length: > 0 } name ? [name] : []
        };
    }

    /// <summary>A reply that ends by asking something: its last sentence has a question mark.</summary>
    internal static bool AsksSomething(string reply)
    {
        var text = reply.TrimEnd().TrimEnd('"', '\'', ')', ']', '*', '\u201D', '\u2019', ' ');
        var last = text.LastIndexOfAny(['.', '!', '\n']);
        return text.EndsWith('?') || last >= 0 && last < text.Length - 1 && text[(last + 1)..].Contains('?');
    }

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
        PcAudioCaptureFactory? pcAudio = null, CharacterCueFeed? characterCues = null,
        Func<SpeechEngine?, PromptSettings?, CharacterActionPrompt?>? characterActions = null,
        DesktopConversationHistory? history = null, ConversationSinging? singing = null)

    {
        this.operations = operations;
        this.settings = settings;
        this.vault = vault;
        this.captureDevices = captureDevices;
        this.playbackDevices = playbackDevices;
        this.singing = singing;
        this.echoReducer = echoReducer;
        this.pcAudio = pcAudio;
        this.characterActions = characterActions;
        this.history = history;
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
        localWords = localListener;
        context = new();
        captureCredentials = new(() => Volatile.Read(ref captureAuthorization));
        var credentials = new ConversationCredentialSource(() => Volatile.Read(ref active)?.Authorization);
        runtime = runtimeFactory?.Invoke(credentials, this.clock) ??
            ConversationRuntime.Create(credentials, playbackDevices, clock: this.clock, generatedSpeech: generatedSpeech,
                hostText: new HostTextClient(), hostSpeech: dataDirectory is null ? null : new HostSpeechClient(dataDirectory),
                spokenText: spokenText, windowsVoice: new WindowsVoiceClient(), characterCues: characterCues);
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
        jobs = new(this.clock);
        thinkCredentials = new(() => Volatile.Read(ref thinkAuthorization));
        songCredentials = new(() => Volatile.Read(ref songAuthorization));
        if (singing is not null)
            songHandler = CreationRegistry.Shared.Handle(SongCreations.KindName, new CreationHandler(SingCreationAsync));
        jobs.Changed += WriteJobsStatus;
        WriteJobsStatus();
    }

    /// <summary>Saves what Martlet found out about a Thinking model (model-abilities.json) and lets the running conversation use
    /// it at once. Nothing is saved without a data directory (tests).</summary>
    internal void RecordAbility(ModelAbility ability)
    {
        if (dataDirectory is null) return;
        var abilities = ModelAbilities.Load(dataDirectory).With(ability);
        if (!abilities.Save(dataDirectory))
        {
            ErrorLog.Warn("Martlet couldn't save what it found out about the Thinking model (model-abilities.json).");
            return;
        }
        Configuration?.UseAbilities(abilities);
    }

    /// <summary>Reads model-abilities.json again, after Martlet found out more or another computer shared it.</summary>
    internal void ReloadAbilities()
    {
        if (dataDirectory is not null) Configuration?.UseAbilities(ModelAbilities.Load(dataDirectory));
    }

    /// <summary>Reads where Deep thinking thinks on this PC (deep-thinking.json) again, after Companion › Deep thinking saved it:
    /// the next reply offers think_longer only where Deep thinking can run, and the next think goes there.</summary>
    internal void ReloadDeepThinking() => Volatile.Write(ref deepThinking, DeepThinkingSettings.Load(dataDirectory));

    /// <summary>Whether Deep thinking can run where it is set to think, for <paramref name="configured"/>'s routes.</summary>
    private DeepThinkingPlan DeepPlan(LiveConversationConfiguration configured) =>
        DeepThinkingPlan.For(Volatile.Read(ref deepThinking), configured.Routes);

    internal void Configure(SettingsLoadResult loaded)
    {
        // The context windows found on this PC (Companion › Replies › Check) keep the context size within the model's own, and
        // what Thinking models were found to hear and see decides whether a recording or picture goes with a message.
        var next = LiveConversationConfiguration.From(loaded, ModelLimits.Load(dataDirectory), ModelAbilities.Load(dataDirectory));
        ReloadDeepThinking();
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
        // The record of conversations is read in the background now, so a message that mentions an earlier one finds it.
        if (history?.Active(next?.Memory) == true) history.Warm();
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
        BoundedWaveAudio? recording = null, SeenScreen? seen = null, bool pcAudio = false, string? userWords = null,
        ReplyTimeline? timeline = null, PlaybackMode playback = PlaybackMode.Reply, ChattinessChoice? chattiness = null,
        IReadOnlyList<SpokenWords>? words = null, bool hearLocalOnly = false)
    {
        if (!approved || microphone && (!localCaptureApproved || !uploadApproved))
            throw new LiveActionException("conversation.permission_required");
        if (listening is not null && !microphone || spoken && microphone || recording is not null && !spoken ||
            listening?.Pc == true || pcAudio && (!spoken || recording is not null) || !pcAudio && userWords is not null ||
            words is not null && (words.Count == 0 || recording is null || pcAudio))
            throw new LiveActionException("conversation.invalid_input");
        listening?.Activity.Validate();
        Voiceprint? voiceprint = null;
        if (listening?.RequireVoiceId == true)
            voiceprint = voiceIdentity?.Current ?? throw new LiveActionException("voiceid.not_enrolled");
        caller.ThrowIfCancellationRequested();
        // What goes straight to Thinking is the recording alone; its text only marks it until the words come.
        BoundedTextInput? input = microphone ? null : new(words is not null ? LiveConversationConfiguration.VoiceOnlyText : text ?? "");
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
            // Hearing that is on only because it was never chosen holds only while the recording stays on this PC.
            var hear = microphone ? listening?.HearsWith(selected) == true
                : recording is not null && (!hearLocalOnly || selected.RecordingStaysOnThisPc());
            if (!hear) recording = null;
            var authorization = new ConversationAuthorization(selected, voice, microphone, clock,
                () => Volatile.Read(ref revision) == acceptedRevision, settings.LoadAsync, vault, caller,
                screen: seen is not null, hear: hear);
            operation = new(authorization, caller)
            {
                MemoryRequested = memory is not null && selected.Memory is { Enabled: true },
                Listening = listening, Voiceprint = voiceprint, Spoken = spoken, Heard = spoken ? heard : null,
                SpokenConfidence = spoken ? confidence : null, Recording = recording, Seen = seen, StraightWords = words,
                PcAudio = pcAudio, UserWords = string.IsNullOrWhiteSpace(userWords) ? null : userWords.Trim(), Playback = playback,
                WhileSinging = spoken ? singing?.Now() : null,
                BackgroundChattiness = chattiness,
                LatencyTimeline = timeline ?? new ReplyTimeline(clock, microphone ? ReplyTimeline.YouPressed
                    : spoken ? ReplyTimeline.Asked : ReplyTimeline.YouSent)
            };
            // What was heard waited (to be collected, for more words or for the app slot) until now.
            timeline?.Mark("waiting to answer");
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
    /// transcribes each (in order) while it already listens for the next, so nothing said while Martlet thinks or speaks is
    /// lost. Each utterance is still its own action (fresh authorization, capture epoch, Voice ID check and STT request). It
    /// holds off only while other setup work owns the app slot, and while Martlet speaks when it can't tell Martlet's own voice
    /// from yours (no echo reduction and no barge-in), so it never hears itself; microphone and speech-to-text failures are
    /// reported and listening carries on.</summary>
    internal LiveListener Listen(ListeningOptions options)
    {
        if (!options.HandsFree || options.Pc && (options.RequireVoiceId || options.Hear || options.ReduceEcho || options.Straight))
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

    /// <summary>Whether this listener holds off right now. Hearing what this PC plays holds off only while Martlet speaks (or
    /// sings) and Windows can't leave Martlet's own voice out of it (never for barge-in: only the user interrupts). The
    /// microphone keeps listening while Martlet speaks or sings whenever it can tell Martlet's own voice from yours
    /// (<see cref="ListensOverMartlet"/>), so what you say then is heard and answered after; otherwise it holds off.
    /// <paramref name="echo"/>: the capture under way, whose own echo reduction decides once it has opened.</summary>
    private bool Held(ListeningOptions options, EchoTimeline? echo = null)
    {
        if (options.Pc) return pcAudio?.WithoutMartlet != true && (Held(false) || singing?.Playing == true);
        var over = ListensOverMartlet(options, echo);
        return Held(over) || !over && singing?.Playing == true;
    }

    /// <summary>Whether the microphone can go on listening while Martlet speaks or sings: barge-in is on (talking over it is the
    /// point), echo reduction works (what the speakers play is taken out of what the microphone hears, and what is mostly their
    /// sound is let go like a cough), or the microphone is the FIXTURE one that hears only its clips. Echo reduction that is off,
    /// couldn't start or was lost can't tell Martlet's voice from yours: listening would hear Martlet and answer its own words.</summary>
    private bool ListensOverMartlet(ListeningOptions options, EchoTimeline? echo = null) =>
        options.BargeIn || captureDevices is SimulatedMicrophone || options.ReduceEcho && echoReducer?.Works(echo) == true;

    /// <summary>Always listening holds off while other setup work (a microphone test, Voice ID enrollment) owns the app slot,
    /// and, when it can't tell Martlet's own voice from yours (<paramref name="overMartlet"/> false), while Martlet speaks (a
    /// reply or a remark, plus a short tail for the room's echo), so it never hears itself.</summary>
    internal bool Held(bool overMartlet)
    {
        lock (gate)
        {
            var now = clock.GetTimestamp();
            if (active is { Worker: not null } current && !current.OwnershipReleased)
            {
                if (overMartlet) return false;
                if (current.Turn?.Snapshot is { State: ConversationState.Playing } or { MayHavePlayed: true, OwnershipReleased: false })
                {
                    spokeUntil = now + (long)(SpeechTail.TotalSeconds * clock.TimestampFrequency);
                    return true;
                }
                return now < spokeUntil;
            }
            return operations.IsRunning || !overMartlet && now < spokeUntil;
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
                if (!listening.Options.Pc && !listening.Options.BargeIn && Speaking == PlaybackMode.Reply)
                    ErrorLog.Info("Always listening heard you while Martlet spoke; without barge-in that doesn't stop the reply, " +
                        "and what you said is answered after it.");
                if (!listening.Options.Pc)
                {
                    // An utterance that ran to the recording limit has no detected end: count from now.
                    utterance.LatencyTimeline ??= new ReplyTimeline(clock, ReplyTimeline.YouStopped);
                    utterance.LatencyTimeline.Mark("recording");
                }
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
            if (!listening.Pc) await wordsTail.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
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
        utterance.Status.Code is "listen.heard" or "listen.ignored" ? utterance.Transcript : null, utterance.Transcription?.Confidence, utterance.Heard,
        utterance.SpeakerCheck, utterance.Voiceprint, utterance.Status.Code == "listen.heard" ? utterance.Recording : null,
        utterance.LatencyTimeline, utterance.Status.Code == "listen.ignored" ? utterance.Ignored : null,
        utterance.Status.Code == "listen.heard" ? utterance.Interrupts : null, utterance.SpeechStartedAt,
        utterance.Status.Code == "listen.heard" ? utterance.Words : null);

    /// <summary>Whether what was just heard goes straight to Thinking as the recording alone, with speech-to-text beside the
    /// reply: Thinking may hear it and the straight path is chosen (Companion › Listening), the Thinking model hears and hasn't
    /// refused a recording this session, Home Assistant's Assist doesn't need the words first, and it wasn't said over Martlet
    /// while it speaks without a quick check deciding (its words decide whether that stops Martlet).</summary>
    private bool GoesStraight(ListeningOptions options, LiveConversationOperation utterance)
    {
        if (options is not { Straight: true, Hear: true, Pc: false }) return false;
        var configured = utterance.Authorization.Configuration;
        if (!options.HearsWith(configured) || configured.Hearing() != HearingSupport.Supported) return false;
        lock (gate)
            if (deafModels.Contains(configured.ToolModelKey())) return false;
        if (smartHome is { ControlEnabled: true, ModelToolsEnabled: false }) return false;
        return !(options.BargeIn && Speaking is not null && utterance.TalkOver is null);
    }

    // Voice ID, then speech-to-text, one utterance after another (so what you said stays in order) while the next is recorded.
    // Straight to Thinking, the recording is handed on right after Voice ID and who spoke, and speech-to-text runs on its own
    // chain (wordsTail), off the reply's path.
    private async Task TranscribeHeardAsync(Task previous, LiveListener listening, LiveConversationOperation utterance, byte[] speech,
        CancellationToken token)
    {
        var handedOn = false;
        try
        {
            await previous.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            BoundedWaveAudio? audio;
            try { audio = Screen(utterance, speech); }
            finally { CryptographicOperations.ZeroMemory(speech); }
            if (audio is null) return;
            if (utterance.Voiceprint is not null) utterance.LatencyTimeline?.Mark("Voice ID");
            if (GoesStraight(listening.Options, utterance))
            {
                utterance.Heard = await HeardAsync(utterance, token).ConfigureAwait(false);
                if (utterance.Recognition is not null) utterance.LatencyTimeline?.Mark("voice recognition");
                utterance.Recording = audio;
                // A quick check while it was said over Martlet may already have decided that it stops Martlet.
                utterance.Interrupts = utterance.TalkOver?.Decision;
                var spokenWords = utterance.Words = new SpokenWords(clock) { Voiced = utterance.Voiced };
                utterance.Publish(new("listen.heard", Finished: true));
                listening.Post(Result(utterance));
                handedOn = true;
                listening.EndTranscribing();
                var before = wordsTail;
                wordsTail = Task.Run(() => TranscribeWordsAsync(before, listening, utterance, audio, spokenWords, token), CancellationToken.None);
                return;
            }
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
            utterance.LatencyTimeline?.Mark("speech-to-text");
            utterance.Transcript = result.Text;
            // What isn't words (mm, a cough, "Thank you." made up from noise) never becomes a turn or stops Martlet. Local and
            // instant: it adds nothing to the time until Martlet answers.
            var options = listening.Options;
            var words = WordsContext(utterance, utterance.Voiced, result.Evidence, utterance.Speech);
            if (UtteranceFilter.Check(result.Text, words, options.WordCheck) is { Keep: false } ignored)
            {
                utterance.Ignored = ignored;
                if (!pc)
                    ErrorLog.Info($"Always listening ignored what it heard: {ignored.Reason} ({ignored.Kind}" +
                        (utterance.Voiced is { } voiced ? $", {voiced.TotalMilliseconds:0} ms of voice" : "") +
                        (utterance.Speech is { } spoken ? $" in {spoken.TotalMilliseconds:0} ms of speech" : "") +
                        (result.Evidence is { } evidence ? ", " + Describe(evidence) : "") + $", word check {options.WordCheck}).");
                utterance.Publish(new("listen.ignored", Finished: true));
                return;
            }
            // Said over Martlet: its words may stop the reply (a quick check while it was said may already have decided).
            if (options is { BargeIn: true, Pc: false } && Speaking is { } mode)
                utterance.Interrupts = utterance.TalkOver?.Decision ??
                    (BargeInPolicy.Decide(result.Text, words, options.WordCheck, mode) is { Interrupt: true } decision ? decision : null);
            // Said while Martlet sings: asking it to stop ends the song musically; a quick check may already have, and then
            // the note quotes everything that was said.
            if (!options.Pc && !StopSongIfAsked(utterance, result.Text, words, options.WordCheck) && utterance.StoppedSong)
                singing?.Heard(result.Text ?? "");
            utterance.Heard = await HeardAsync(utterance, linked.Token).ConfigureAwait(false);
            if (utterance.Recognition is not null) utterance.LatencyTimeline?.Mark("voice recognition");
            if (listening.Options.HearsWith(utterance.Authorization.Configuration)) utterance.Recording = audio;
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
            if (!handedOn)
            {
                if (!token.IsCancellationRequested) listening.Post(Result(utterance));
                listening.EndTranscribing();
            }
        }
    }

    // Speech-to-text for what went straight to Thinking, beside the reply and one utterance after another: the words go to the
    // talk window, the conversation and memory once they come. The word check only labels them (Thinking already heard it).
    private async Task TranscribeWordsAsync(Task previous, LiveListener listening, LiveConversationOperation utterance,
        BoundedWaveAudio audio, SpokenWords words, CancellationToken token)
    {
        try
        {
            await previous.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            // Speech-to-text and Thinking both on this PC: transcribe once the reply is under way, so the two never compete for
            // the processor before the first audio (at most TranscribeAfter later, for words no reply asked for yet).
            if (utterance.Authorization.Configuration is { LocalThinking: true } local && local.LocalStt())
                await Task.WhenAny(words.MayTranscribe, Task.Delay(TranscribeAfter, clock, token)).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(35), clock);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
            var started = clock.GetTimestamp();
            Volatile.Write(ref transcribing, utterance);
            TranscriptionResult? result;
            try { result = await TranscribeAsync(utterance, audio, listenTranscription, linked.Token).ConfigureAwait(false); }
            finally { Interlocked.CompareExchange(ref transcribing, null, utterance); }
            if (result is null)
            {
                words.Failed(utterance.Status.Code);
                return;
            }
            var context = WordsContext(utterance, utterance.Voiced, result.Evidence, utterance.Speech);
            var ignored = UtteranceFilter.Check(result.Text, context, listening.Options.WordCheck) is { Keep: false } filtered ? filtered : null;
            words.Heard(result.Text ?? "", result.Confidence, ignored, clock.GetElapsedTime(started));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { words.Failed("conversation.canceled"); }
        catch (OperationCanceledException) { words.Failed("stt.deadline_exceeded"); }
        catch (LiveActionException error) { words.Failed(error.Code); }
        catch (ContractException) { words.Failed("conversation.invalid_input"); }
        finally
        {
            // Never left waiting: whatever happened, the words are ready (with or without any).
            words.Failed("conversation.canceled");
        }
    }

    private void ClearContextLocked()
    {
        context.Clear();
        remarks.Clear();
        lastCache = null;
        conversationId = Guid.NewGuid();
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
    /// participation policy (that decides whether to answer the user); the caller's pacer decides when to look. With
    /// <paramref name="look"/> (Martlet decides where the character looks) the model may also start its answer with a look tag
    /// that turns the character's eyes to part of the picture.</summary>
    internal LiveConversationOperation StartCommentary(BoundedImage image, string windowTitle, ChattinessChoice chattiness, bool voice,
        bool screenApproved, WatchSource? source = null, CancellationToken caller = default, AttentionSignal? attention = null,
        bool look = false)
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
                return await RunCommentaryAsync(operation, prompt, image, chattiness, camera, look && !camera, token).ConfigureAwait(false);
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

    internal static bool IsSilentReply(string text) => StayQuiet.IsQuiet(text);

    /// <summary>A reply still streaming that may turn out to be [pass]; it isn't shown until it clearly isn't.</summary>
    internal static bool MaybeSilent(string text) => StayQuiet.MaybeQuiet(text);

    private async Task<SetupWorkResult> RunCommentaryAsync(LiveConversationOperation operation, string prompt, BoundedImage image,
        ChattinessChoice chattiness, bool camera, bool look, CancellationToken worker)
    {
        // While Martlet decides how chatty it is, the look is told how to switch the level (the same at every level) and the
        // level it is at goes in the notes.
        var decides = chattiness == ChattinessChoice.MartletDecides;
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
                // Earlier messages go exactly as they were sent (with their notes), so the request starts like the one before.
                var history = context.Snapshot(sent: true);
                var level = ChattinessTags.Level(chattiness, decided);
                var request = configured.Request(new(prompt), operation.Authorization.Voice, style, history, null, lore,
                    out var usedHistory, out _, out var usedLore, image,
                    LiveConversationConfiguration.CommentaryInstructions(level, camera, configured.Prompts, decides),
                    LiveConversationConfiguration.SilentReply, characterActions: characterActions,
                    gaze: look ? CharacterGaze.Prompt(configured.Prompts, LiveConversationConfiguration.SilentReply) : null,
                    chattiness: decides ? configured.ChattinessNote(level) : null, controlTags: decides ? ChattinessTags.All : null);
                operation.LookOffered = request.CharacterTags.Any(CharacterGaze.IsTag);
                // Exchanges a look had to leave out are never sent again, so later requests start the same way.
                context.LetGoBefore(context.Start + (history.Count - usedHistory) / 2);
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
            NoteInput(camera ? "Camera glance" : "Screen glance", terminal);
            if (decides) Decide(turn, terminal, camera ? "a camera look" : "a screen glance");
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

    /// <summary>Martlet's voice was muted (Speak Martlet's replies aloud turned off): the reply or comment running now stops
    /// saying it aloud and finishes as text, with the rest in the captions. Nothing is canceled or forgotten, and what comes
    /// next is text only because it starts without a voice.</summary>
    internal void MuteVoice()
    {
        LiveConversationOperation? running;
        lock (gate) running = active is { OwnershipReleased: false } ? active : null;
        running?.MuteVoice();
    }

    /// <summary>Companion › Voice › Voice volume (0 to 1): how loud Martlet speaks and sings. A reply or song playing now follows
    /// a change at once; Windows' own volume is never touched.</summary>
    internal double VoiceVolume
    {
        get => runtime.VoiceVolume;
        set
        {
            runtime.VoiceVolume = value;
            if (singing is not null) singing.VoiceVolume = value;
        }
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
                // The reply carrying a recording sent alone is under way: its words may be transcribed now.
                if (operation.StraightWords is { } spoken && (snapshot.FirstAudioAfter is not null || snapshot.MayHavePlayed ||
                    snapshot.TextComplete || !operation.Authorization.Voice && snapshot.FirstTextAfter is not null))
                    foreach (var words in spoken) words.Release();
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
                operation.LatencyTimeline?.Mark("recording");
                var result = await TranscribeAsync(operation, audio, transcription, worker).ConfigureAwait(false);
                if (result is null)
                    return new(operation.Transcription?.Outcome == TranscriptionOutcome.NoSpeech ? SetupWorkOutcome.Completed : SetupWorkOutcome.Failed);
                operation.LatencyTimeline?.Mark("speech-to-text");
                operation.Heard = await HeardAsync(operation, worker).ConfigureAwait(false);
                if (operation.Recognition is not null) operation.LatencyTimeline?.Mark("voice recognition");
                operation.Transcript = result.Text;
                if (operation.Authorization.Hear) operation.Recording = audio;
                input = new(result.Text!);
            }
            // Straight to Thinking: the recording alone, unless it can't go (the model refused one since, or hearing that was on
            // only by default no longer stays on this PC): then the reply waits for the words, as when transcribing first.
            var straight = operation.StraightWords is { Count: > 0 };
            if (straight && (operation.Recording is null || !operation.Authorization.Hear || !Hears(operation.Authorization.Configuration)))
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(35), clock);
                using var bound = CancellationTokenSource.CreateLinkedTokenSource(worker, deadline.Token);
                string? words;
                try { words = await SpokenWords.TranscriptAsync(operation.StraightWords!, bound.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!worker.IsCancellationRequested) { words = null; }
                operation.LatencyTimeline?.Mark("speech-to-text");
                // Not words (a cough, mm): like any utterance the word check lets go, it gets no reply.
                if (operation.StraightWords!.All(spoken => spoken.NotWords))
                {
                    operation.Publish(new(NotWordsCode, Finished: true));
                    return new(SetupWorkOutcome.Completed);
                }
                if (words is null)
                {
                    operation.Publish(new("stt.NoSpeech", Finished: true));
                    return new(SetupWorkOutcome.Completed);
                }
                input = new(words);
                straight = false;
            }
            operation.Straight = straight;
            // Earlier messages that went straight to Thinking carry their words once speech-to-text has them: wait a moment for
            // any still on their way, so this request carries them instead of what stood in for them.
            Task[] filling;
            lock (gate) filling = context.Filling();
            if (filling.Length > 0)
            {
                await Task.WhenAny(Task.WhenAll(filling), Task.Delay(EarlierWordsWait, clock, worker)).ConfigureAwait(false);
                operation.LatencyTimeline?.Mark("earlier words");
            }
            operation.Authorization.Check(worker);
            ConversationTurn turn;
            PersonaProfile? persona;
            ResponseStyle? style;
            IReadOnlyList<TextHistoryMessage> history, sentHistory;
            long historyStart;
            Guid conversation;
            lock (gate)
            {
                operation.Authorization.Check(worker);
                // A report of finished background work is Martlet's own, like a screen glance: the participation policy decides
                // whether to answer the user, so it doesn't apply.
                if (!operation.Report)
                {
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
                }
                persona = operation.Authorization.Configuration.Persona;
                style = persona is null ? null :
                    ResponseStyleSelector.Select(persona.Styles, nextStyle);
                history = context.Snapshot();
                // Earlier messages go exactly as they were sent (with their notes), so the request starts like the one before;
                // lore, memory and learning names read what was said (history).
                sentHistory = context.Snapshot(sent: true);
                historyStart = context.Start;
                conversation = conversationId;
            }

            // Keeps Smart home's list of locks, doors and garages current before the model may call Home Assistant's tools.
            if (smartHome is { ModelToolsEnabled: true } safety) await safety.RefreshSafetyAsync(worker).ConfigureAwait(false);
            operation.LatencyTimeline?.Mark("preparing");

            // What the PC played is never the user: memory, tools and Home Assistant only go by the user's own words, and a
            // message that is only what the PC played gets none of them. A report of background work has no words of the user's.
            // A message that went straight to Thinking has no words yet: memory recalls by the conversation so far (and the
            // speaker), tools and finished background work go with it, and what needs its words (Assist, earlier conversations,
            // lorebook keywords in it) waits for the next message.
            var own = operation.Report || straight ? null : operation.PcAudio ? operation.UserWords : input!.UserText;
            DesktopMemoryRecall? memoryResult = null;
            if (operation.MemoryRequested && (own ?? (straight ? StraightRecallQuery(history) : null)) is { } query)
            {
                operation.Publish(new("memory.recalling"));
                memoryResult = await RecallAsync(operation, query, worker).ConfigureAwait(false);
                operation.Authorization.Check(worker);
                await operation.Authorization.ValidateSettingsAsync(worker).ConfigureAwait(false);
                operation.MemoryStoreRevision = memoryResult?.StoreRevision;
                operation.LatencyTimeline?.Mark("memory");
            }
            var lore = await ScanLoreAsync(operation, straight ? "" : input!.UserText, history, persona, worker).ConfigureAwait(false);
            if (lore is not null) operation.LatencyTimeline?.Mark("lore");

            // Tools from MCP servers on this PC, the terminal when it is on and Martlet's own (think_longer while Thinking longer
            // is on and Deep thinking can run where it is set to think, search_conversations while it is allowed, list_creations
            // and perform_creation while any kind of creation is registered), only for the user's own turns (and Martlet's reports
            // of its background work) and routes that do function calling. While they are on they are always offered, the same
            // way, so every request starts the same.
            DesktopToolset? toolset = null;
            var configured = operation.Authorization.Configuration;
            var builtIns = BuiltIns(operation, configured, conversation);
            if ((own is not null || straight || operation.Report) && tools is not null && (tools.HasTools || builtIns is not null) &&
                configured.SupportsTools && !tools.IsUnsupported(configured.ToolModelKey()))
            {
                operation.Publish(new("tools.preparing"));
                toolset = await tools.PrepareAsync(worker, builtIns).ConfigureAwait(false);
                operation.Authorization.Check(worker);
                operation.Toolset = toolset;
                operation.LatencyTimeline?.Mark("tools");
            }

            // Finished background work that wasn't brought up yet goes with what the user says (its notes).
            if ((own is not null || straight) && !operation.Report && operation.Delivery is null) operation.Delivery = jobs.Take(onItsOwn: false);
            // Where the last song stopped and why (or that it ended) goes at the end of the conversation once, with the next
            // message Martlet answers.
            var songNote = singing?.PendingNote;

            // Only the user's own typed or spoken words ever reach Home Assistant (glances use RunCommentaryAsync). When the
            // reply is offered Home Assistant's own tools, the model acts through them instead of Assist, so nothing runs twice.
            // Assist needs the words, so a message that went straight to Thinking gets Home Assistant only through its tools.
            HomeTurn? home = null;
            if ((own is not null || straight) && smartHome is { ControlEnabled: true } house)
            {
                if (house.ModelToolsEnabled && toolset?.Servers.Contains(SmartHome.ServerName) == true &&
                    tools!.ManagedConflicts.All(s => s.Name != SmartHome.ServerName))
                    home = house.ToolsTurn(configured.Prompts);
                else if (own is not null)
                {
                    operation.Publish(new("home.asking"));
                    home = await house.HandleAsync(own, worker, configured.Prompts).ConfigureAwait(false);
                    operation.Authorization.Check(worker);
                    operation.LatencyTimeline?.Mark("Home Assistant");
                }
                if (home is not null)
                {
                    operation.HomeSummary = home.Summary;
                    operation.Publish(new(home.Code));
                }
            }

            // A message that refers to an earlier conversation brings back what was said then (Companion › Memory › Conversation
            // history) in its notes, read from memory only. Any other message gets nothing, so its request is what it always was.
            string? past = null;
            var pastCount = 0;
            if (own is not null && this.history is { } pastRecord && pastRecord.Active(configured.Memory))
            {
                past = pastRecord.RecallNotes(own, conversation, sentHistory, configured.Prompts, out pastCount);
                if (past is not null) operation.LatencyTimeline?.Mark("past conversations");
            }

            lock (gate)
            {
                operation.Authorization.Check(worker);
                var prompts = operation.Authorization.Configuration.Prompts;
                // The recording goes only to a Thinking model that hears and hasn't refused one this session (checked above for a
                // recording sent alone; a model that refuses it after all is asked again with its words).
                var recording = straight ? operation.Recording : operation.Authorization.Hear && configured.Hearing() == HearingSupport.Supported &&
                    !deafModels.Contains(configured.ToolModelKey()) ? operation.Recording : null;
                // While vision is on, the newest picture of what it watches goes with the message, so the reply sees it too.
                // A message too long to fit beside the picture goes without it.
                var seen = operation.Authorization.Screen ? operation.Seen : null;
                // A report keeps the instructions of the reply before it (who was heard, always listening), so it starts the same.
                var heardBy = operation.Report ? lastAsked.Heard : operation.Heard;
                var background = !operation.Report && operation.Delivery is { } carried
                    ? BackgroundJobs.ReportNotes(prompts, carried.Jobs) : null;
                // Heard while Martlet sings: it keeps singing and answers only when talked to ([pass] otherwise).
                var whileSinging = operation.WhileSinging is { } sung ? SongTools.WhileSinging(prompts, sung.Title, sung.Where) : null;
                // While Martlet decides how chatty it is (and vision is on or it hears this PC), every reply is told how to switch
                // the level, the same way every time; the level goes in the notes when the conversation's notes don't say it yet.
                var decides = operation.BackgroundChattiness == ChattinessChoice.MartletDecides;
                ConversationRequest Ask(SeenScreen? picture, string? recalled, out int keptHistory, out int keptFacts, out int keptEntries) =>
                    operation.Authorization.Configuration.Request(
                        input!, operation.Authorization.Voice, style, sentHistory, memoryResult, lore,
                        out keptHistory, out keptFacts, out keptEntries, image: picture?.Image,
                        extraInstructions: Join(home is { Kind: HomeTurnKind.Tools } ? home.Instructions : null,
                            VoicePromptContext.Preamble(heardBy, prompts),
                            operation.Spoken ? LiveConversationConfiguration.Listening(prompts) : null,
                            operation.PcAudio ? LiveConversationConfiguration.PcAudio(prompts) : null,
                            decides ? LiveConversationConfiguration.ChattinessDecides(prompts) : null,
                            recording is null ? null : PromptSettings.Fill(prompts, straight ? PromptCatalog.HeardVoiceOnly : PromptCatalog.HeardVoice),
                            picture is null ? null : PromptSettings.Fill(prompts, PromptCatalog.SeenWithMessage, ("source", picture.Describe()))),
                        voices: VoicePromptContext.Block(operation.Heard),
                        messageNotes: Join(home is { Kind: HomeTurnKind.Tools } ? null : home?.Instructions, background, recalled, songNote, whileSinging),
                        silentReply: operation.Spoken ? LiveConversationConfiguration.SilentReply : null, tools: toolset,
                        closingInstructions: operation.Authorization.Configuration.ReplyLength, audio: recording, imageOptional: true,
                        characterActions: characterActions, withoutReasoning: reasoningRefused.Contains(configured.ToolModelKey()),
                        chattiness: decides ? operation.Authorization.Configuration.ChattinessNote(decided) : null,
                        controlTags: decides ? ChattinessTags.All : null,
                        spokenWords: straight ? token => SpokenWords.TranscriptAsync(operation.StraightWords!, token) : null);
                ConversationRequest request;
                int usedHistory, usedMemory, usedLore;
                var picture = seen;
                while (true)
                {
                    try
                    {
                        request = Ask(picture, past, out usedHistory, out usedMemory, out usedLore);
                        break;
                    }
                    // A message too long to fit goes without what was said in earlier conversations first, then without the picture.
                    catch (LiveActionException error) when (error.Code == "conversation.input_limit" && (past is not null || picture is not null))
                    {
                        if (past is not null) (past, pastCount) = (null, 0);
                        else picture = null;
                    }
                }
                operation.PastExchanges = pastCount;
                operation.VoiceSent = request.Input.Audio is not null;
                operation.ScreenSent = request.Input.Image is not null;
                operation.Sent = request.Input;
                // Exchanges this reply had to leave out are never sent again, so the next replies start the same way.
                context.LetGoBefore(historyStart + (history.Count - usedHistory) / 2);
                operation.PersonaRevision = persona?.ConfigurationRevision;
                operation.ResponseStyle = style;
                operation.ContextMessages = usedHistory;
                operation.ContextMessagesOmitted = history.Count - usedHistory;
                operation.MemoryFactsUsed = usedMemory;
                operation.MemoryFactsOmitted = (memoryResult?.Facts.Count ?? 0) - usedMemory;
                RecordLore(operation, lore, usedLore);
                operation.Authorization.BindInput(request.Input, request.Limits.MaxToolRounds, request.ImageOptional);
                // Exact-content commit, pause/consent state and immediate Start share this short, non-awaiting gate.
                operation.ReplyStartedAt = clock.GetTimestamp();
                operation.LatencyTimeline?.Mark("building the request", operation.ReplyStartedAt);
                turn = runtime.Start(request, operation.Authorization, operation.OriginalCaller);
                operation.Attach(turn);
                if (straight)
                    foreach (var words in operation.StraightWords!) words.ReplyStarted(operation.ReplyStartedAt);
            }
            // Which way what was said went to Thinking (MCP's hearing_check reads the newest line), and once speech-to-text beside
            // the reply has the words, how long after the reply started they came.
            if (straight)
            {
                ErrorLog.Info("Voice path: straight to Thinking (your recording alone, no transcript); speech-to-text runs beside the reply.");
                NoteWordsAsync(operation.StraightWords!).Forget();
                // Something short might not be words (a cough, mm): Parakeet checks it now, beside the request, and the reply is
                // dropped if it isn't words and nothing has played yet.
                if (QuickCheck(operation)) CheckWordsBesideAsync(operation, turn).Forget();
            }
            else if (operation.VoiceSent) ErrorLog.Info("Voice path: transcribe first (your recording with the transcript).");
            var terminal = await turn.Completion.ConfigureAwait(false);
            NoteFallback(operation.Report ? "Background report" : "Reply", configured, terminal);
            NoteInput(operation.Report ? "Background report" : "Reply", terminal);
            if (operation.BackgroundChattiness == ChattinessChoice.MartletDecides)
                Decide(turn, terminal, operation.Report ? "bringing up background work"
                    : operation.PcAudio && operation.UserWords is null ? "what this PC played" : "your message");
            // A model that rejected tools is asked without them from now on (for a week, on this PC).
            if (terminal.ToolsRejected)
            {
                tools?.MarkUnsupported(configured.ToolModelKey());
                ErrorLog.Info($"The Thinking model {configured.Route(SetupRole.Llm).ModelId} rejected the request with tools; " +
                    "Martlet asked again without tools and stops offering them to it for a week.");
            }
            // A model that rejected the recording gets the transcript only from now on (this app session), and Martlet remembers
            // it can't hear (model-abilities.json, shared with the owner's other computers) until a check or test says otherwise.
            if (terminal.AudioRejected)
            {
                lock (gate) deafModels.Add(configured.ToolModelKey());
                var thinking = configured.Route(SetupRole.Llm);
                RecordAbility(new() { Origin = thinking.Origin, ModelId = thinking.ModelId, Hears = false, Source = "a refused recording",
                    CheckedAt = DateTimeOffset.UtcNow });
                ErrorLog.Info($"The Thinking model {configured.Route(SetupRole.Llm).ModelId} rejected the request with your recording; " +
                    "Martlet asked again with the transcript only and sends it only the transcript from now on.");
            }
            if (terminal.ImageRejected)
                ErrorLog.Info($"The Thinking model {configured.Route(SetupRole.Llm).ModelId} rejected the picture of your screen sent " +
                    "with your message; Martlet asked again with your words only.");
            // A model that always thinks refused Thinking steps Off (or its server refused the control): it gets its own default
            // from now on (this app session).
            if (terminal.ReasoningRejected)
            {
                lock (gate) reasoningRefused.Add(configured.ToolModelKey());
                ErrorLog.Info($"The Thinking model {configured.Route(SetupRole.Llm).ModelId} refused Thinking steps " +
                    $"{(GenerationSettings.ThinkingSteps(configured.Generation) ? "On" : "Off")}; Martlet asked again with the model's own default and " +
                    "uses it until it restarts. This model always thinks, so choose a model that can answer without thinking for the fastest replies.");
            }
            if (IsFailure(terminal)) LogReplyFailure("Reply", configured, terminal);
            else if (terminal.State == ConversationState.Completed) Succeeded(SetupRole.Llm);
            // What always listening heard may not have been meant for Martlet: the model answers [pass] and stays quiet.
            var passed = operation.Spoken && terminal.State == ConversationState.Completed && IsSilentReply(turn.Content.Text);
            operation.Passed = passed;
            // A reply that ends by asking something makes a short answer to it ("yes", "mm-hmm") count as words for a while.
            if (terminal.State == ConversationState.Completed && !passed && !string.IsNullOrWhiteSpace(turn.Content.Text))
                Interlocked.Exchange(ref askedAt, AsksSomething(turn.Content.Text) ? clock.GetTimestamp() : 0);
            if (terminal.State == ConversationState.Completed && !string.IsNullOrWhiteSpace(turn.Content.Text))
            {
                lock (gate)
                {
                    if (ReferenceEquals(active, operation) && !operation.Authorization.IsCanceled)
                    {
                        var earlier = context.Snapshot();
                        var kept = passed ? $"[{LiveConversationConfiguration.SilentReply}]" : turn.Content.Text;
                        string? said = null;
                        if (straight)
                        {
                            // Straight to Thinking: the exchange is kept now and its words replace what stands in for them once
                            // speech-to-text has them; the record of conversations, memory and learning names follow then.
                            var exchange = context.Add(VoicePromptContext.Prefix(operation.Heard) + LiveConversationConfiguration.VoiceOnlyText,
                                kept, configured.HostTarget() is null ? operation.Sent?.SentUserText : null);
                            ConversationContextBuffer.Pending(exchange,
                                KeepWordsAsync(new(operation, configured, conversation, earlier, exchange, turn.Content.Text, passed)));
                        }
                        else
                        {
                            // Who said it travels with the words, so later replies (and memory) know who said what. A message with
                            // what the PC played keeps its marked lines as they are.
                            said = operation.PcAudio ? input!.UserText : VoicePromptContext.Prefix(operation.Heard) + input!.UserText;
                            // A pass stays in the conversation too, so later replies know what was said around Martlet.
                            context.Add(said, kept, configured.HostTarget() is null ? operation.Sent?.SentUserText : null);
                            // The record of conversations keeps the user's own words (never what the PC played) and the reply,
                            // written in the background after the reply. A pass wasn't said to Martlet, and glances never get here.
                            if (!passed && this.history is { } historyRecord && historyRecord.Active(configured.Memory) &&
                                (operation.Report ? "" : operation.PcAudio ? operation.UserWords : input.UserText) is { } recordedWords)
                                historyRecord.Record(conversation, operation.Report ? HistoryInputKind.Report
                                        : operation.Spoken || operation.Authorization.Microphone ? HistoryInputKind.Spoken : HistoryInputKind.Typed,
                                    recordedWords, turn.Content.Text,
                                    operation.Heard?.Speaker?.Voice is { Named: true } namedVoice ? namedVoice.DisplayName : null);
                        }
                        // The finished background work this reply carried is in the conversation now.
                        if (operation.Delivery is { } delivered)
                        {
                            delivered.Complete();
                            ErrorLog.Info($"Background work: {string.Join(", ", delivered.Jobs.Select(job => job.Id))} " +
                                (operation.Report ? "brought up by Martlet on its own" : "brought up with your message") +
                                (passed ? " (it stayed quiet about it)." : "."));
                        }
                        if (!operation.Report) lastAsked = (operation.Spoken, operation.Heard, operation.BackgroundChattiness);
                        // The note about the last song is in the conversation now.
                        if (songNote is not null) singing?.NoteDelivered(songNote);
                        // Memory and learning names only ever read what the user said themselves, never what the PC played.
                        var spokenOwn = operation.Report || straight ? null : operation.PcAudio ? operation.UserWords : input!.UserText;
                        var remembered = spokenOwn is null ? null
                            : operation.PcAudio ? VoicePromptContext.Prefix(operation.Heard) + spokenOwn : said;
                        var remember = operation.MemoryRequested && !passed && remembered is not null;
                        var heard = !passed && remembered is not null && operation.Heard is { Known.Count: > 0 } known &&
                            voices is { Active: true } && VoiceNaming.Worth(known, spokenOwn!, turn.Content.Text,
                                operation.Authorization.Configuration.CompanionNames) ? known : null;
                        // Whose new facts are: the voices recognized in the message (the speaker's, unless another is named).
                        var present = remember && operation.Heard is { Known.Count: > 0 } recognized ? recognized : null;
                        // The after-reply request continues the reply's request (instructions, tools, earlier messages and the
                        // message), then the reply as the next reply's history has it, whether or not the reply called tools.
                        if (remember || heard is not null)
                            EnqueueAfterReplyLocked(operation.Authorization.Configuration, remember, heard, earlier, remembered!,
                                turn.Content.Text, operation.Sent, present);
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
            // Whatever happened to the reply, the words of what it carried are transcribed now.
            if (operation.StraightWords is { } spoken)
                foreach (var words in spoken) words.Release();
            if (operation.Turn is { } turn)
            {
                await turn.StopAsync().ConfigureAwait(false);
                await turn.OwnershipRelease.ConfigureAwait(false);
                if (turn.Snapshot.Quarantined) await quarantine.Task.ConfigureAwait(false);
            }
            // Finished background work a reply didn't get into the conversation waits for the next one.
            operation.Delivery?.Return();
            lock (gate)
            {
                if (lease is not null) policy.Release(lease);
                operation.FinishExecution();
                if (ReferenceEquals(active, operation)) RevokeLocked();
            }
        }
    }

    private CorrelationIds Ids() => new() { SessionId = runtime.SessionId, TurnId = Guid.NewGuid(), RequestId = Guid.NewGuid() };

    // ---------- straight to Thinking (the recording alone, speech-to-text beside the reply) ----------

    /// <summary>How long a reply waits, at most, for the words of an earlier message that went straight to Thinking and are still
    /// being transcribed, so it carries them; after that the earlier message goes as a spoken message without its words.</summary>
    internal static TimeSpan EarlierWordsWait => TimeSpan.FromMilliseconds(1500);

    /// <summary>The outcome of a reply to what went straight to Thinking that turned out not to be words (a cough, mm): dropped
    /// before it played, or never asked when the words were needed first.</summary>
    internal const string NotWordsCode = "listen.not_words";

    /// <summary>What went straight to Thinking with less voice than this in all (loud 20 ms frames) gets the quick check of
    /// whether it is words, when Parakeet runs on this PC (<see cref="CheckWordsBesideAsync"/>); longer speech is rarely not words.</summary>
    internal static TimeSpan QuickCheckVoice => TimeSpan.FromSeconds(1);

    // Short enough for the quick check, with Parakeet on this PC.
    private bool QuickCheck(LiveConversationOperation operation) =>
        localTranscription is not null && operation.Authorization.Configuration.LocalStt() &&
        operation.StraightWords is { Count: > 0 } spoken && spoken.All(words => words.Voiced is not null) &&
        spoken.Sum(words => words.Voiced!.Value.TotalMilliseconds) < QuickCheckVoice.TotalMilliseconds;

    /// <summary>The quick check of something short that went straight to Thinking: Parakeet transcribes it now, beside the
    /// request that has already started (never before it), and the word check decides. Not words (a cough, mm, laughter, nothing)
    /// and nothing played yet: the reply stops silently and its exchange is never kept; the talk window shows it as ignored.
    /// Once its first audio has started, the reply finishes and the words are only labeled. The desktop log says which.</summary>
    private async Task CheckWordsBesideAsync(LiveConversationOperation operation, ConversationTurn turn)
    {
        var spoken = operation.StraightWords!;
        foreach (var words in spoken) words.Release();
        await Task.WhenAll(spoken.Select(words => words.Ready)).ConfigureAwait(false);
        if (!spoken.All(words => words.NotWords)) return;
        var after = spoken.Max(words => words.ReadyAfterReply ?? TimeSpan.Zero).TotalMilliseconds;
        var took = spoken.Sum(words => words.Took?.TotalMilliseconds ?? 0);
        var why = spoken.Select(words => words.Ignored?.Reason ?? "no speech").Distinct().ToArray();
        var snapshot = turn.Snapshot;
        if (snapshot.MayHavePlayed || snapshot.FirstAudioAfter is not null || snapshot.State is ConversationState.Completed or
                ConversationState.Failed or ConversationState.Canceled or ConversationState.Refused || operation.Status.Finished)
        {
            ErrorLog.Info($"Not words, too late: the quick check found {string.Join(", ", why)} {after:0} ms after the reply started " +
                $"(speech-to-text {took:0} ms), after Martlet had begun answering; the reply goes on and the words are labeled.");
            return;
        }
        operation.NotWords = true;
        Stop(operation, NotWordsCode, keepContext: true);
        ErrorLog.Info($"Not words: Martlet dropped its reply before it played; the quick check found {string.Join(", ", why)} " +
            $"{after:0} ms after the reply started (speech-to-text {took:0} ms).");
    }

    /// <summary>How long speech-to-text waits, at most, for the reply carrying a recording sent alone to get under way, where both
    /// run on this PC (<see cref="SpokenWords.MayTranscribe"/>).</summary>
    internal static TimeSpan TranscribeAfter => TimeSpan.FromSeconds(4);

    // The Thinking model hears and hasn't refused a recording this app session.
    private bool Hears(LiveConversationConfiguration configured)
    {
        if (configured.Hearing() != HearingSupport.Supported) return false;
        lock (gate) return !deafModels.Contains(configured.ToolModelKey());
    }

    // What memory recalls by for a message that has no words yet: the user's last message in the conversation (without what the
    // PC played), or nothing (then the speaker's and the newest facts).
    private static string StraightRecallQuery(IReadOnlyList<TextHistoryMessage> history) =>
        LiveConversationConfiguration.WithoutPcAudio(history.LastOrDefault(message => message.Role == TextHistoryRole.User)?.Text) ?? "";

    private sealed record StraightExchange(LiveConversationOperation Operation, LiveConversationConfiguration Configured, Guid Conversation,
        IReadOnlyList<TextHistoryMessage> Earlier, object Exchange, string Reply, bool Passed)
    {
        public override string ToString() => nameof(StraightExchange);
    }

    /// <summary>Once speech-to-text beside the reply has the words of what went straight to Thinking: they replace what stood in
    /// for them in the conversation (so the next replies carry the transcript, not the recording), and go to the record of
    /// conversations, memory and learning names like any spoken message. Words the word check wouldn't count are kept marked
    /// and never remembered; none at all keep the message as spoken without its words.</summary>
    private async Task KeepWordsAsync(StraightExchange keep)
    {
        var operation = keep.Operation;
        var spoken = operation.StraightWords!;
        await Task.WhenAll(spoken.Select(words => words.Ready)).ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        var real = spoken.Any(words => words is { Text.Length: > 0, Ignored: null });
        var (said, sent, userWords) = SpokenWords.Kept(spoken, VoicePromptContext.Prefix(operation.Heard), operation.Sent);
        bool filled = false, recorded = false, remembering = false;
        lock (gate)
        {
            if (disposed) return;
            var hostless = keep.Configured.HostTarget() is null;
            var current = conversationId == keep.Conversation;
            if (current) filled = context.Fill(keep.Exchange, said, hostless ? sent?.SentUserText : null);
            if (!keep.Passed && history is { } historyRecord && historyRecord.Active(keep.Configured.Memory))
            {
                historyRecord.Record(keep.Conversation, HistoryInputKind.Spoken, userWords, keep.Reply,
                    operation.Heard?.Speaker?.Voice is { Named: true } namedVoice ? namedVoice.DisplayName : null);
                recorded = true;
            }
            if (!keep.Passed && real && current)
            {
                var remember = operation.MemoryRequested;
                var heard = operation.Heard is { Known.Count: > 0 } known && voices is { Active: true } &&
                    VoiceNaming.Worth(known, userWords, keep.Reply, keep.Configured.CompanionNames) ? known : null;
                var present = remember && operation.Heard is { Known.Count: > 0 } recognized ? recognized : null;
                if (remember || heard is not null)
                {
                    EnqueueAfterReplyLocked(keep.Configured, remember, heard, keep.Earlier, said, keep.Reply, sent ?? operation.Sent, present);
                    remembering = true;
                }
            }
        }
        // What happened to the words (never the words themselves).
        ErrorLog.Info($"Straight to Thinking: {(real ? "the transcript" : userWords == SpokenWords.NotTranscribed ? "no transcript" : "words the word check doesn't count")} " +
            $"{(filled ? "replaced the recording in the conversation" : "came after the conversation was cleared")}" +
            (recorded ? ", went to the record of conversations" : "") + (remembering ? " and to remembering" : "") +
            (keep.Passed ? " (Martlet had stayed quiet)" : "") + ".");
    }

    /// <summary>The desktop log's line for the words of what went straight to Thinking, once speech-to-text beside the reply has
    /// them: how long after the reply started they were ready and how long transcribing took (never the words). Once per
    /// utterance, even when a reply restarts with it.</summary>
    private static async Task NoteWordsAsync(IReadOnlyList<SpokenWords> spoken)
    {
        await Task.WhenAll(spoken.Select(words => words.Ready)).ConfigureAwait(false);
        var fresh = spoken.Where(words => words.TryNote()).ToArray();
        if (fresh.Length == 0) return;
        var last = fresh.MaxBy(words => words.ReadyAfterReply ?? TimeSpan.MinValue)!;
        if (fresh.All(words => words.Text is not { Length: > 0 }))
        {
            ErrorLog.Info($"Background transcript: speech-to-text couldn't transcribe what went straight to Thinking ({last.Problem ?? "no words"}); " +
                "the conversation keeps it as spoken without its words.");
            return;
        }
        var after = last.ReadyAfterReply is { } ready
            ? ready >= TimeSpan.Zero ? $"{ready.TotalMilliseconds:0} ms after the reply started" : $"{-ready.TotalMilliseconds:0} ms before the reply started"
            : "(the reply didn't start)";
        var took = fresh.Where(words => words.Took is not null).Sum(words => words.Took!.Value.TotalMilliseconds);
        var ignored = fresh.Count(words => words.Ignored is not null);
        ErrorLog.Info($"Background transcript ready {after} (speech-to-text {took:0} ms" +
            (fresh.Length > 1 ? $" for {fresh.Length} utterances" : "") +
            (ignored > 0 ? $"; word check: {ignored} not words" : "") + ").");
    }

    // ---------- background work (think_longer) ----------

    /// <summary>Martlet's own tools for one reply, always the same ones in the same order while their settings stay, so the start
    /// of every request stays the same: think_longer and cancel_thinking while Thinking longer is on (with the Thinking longer
    /// prompt), research while Web research is on too (with its prompt), then search_conversations while the owner lets Martlet search the record of conversations (Companion › Memory,
    /// off by default), then list_creations and perform_creation while any kind of creation is registered (CreationRegistry,
    /// docs/CREATIONS.md), then manage_memories while memory is on. Null when there are none.</summary>
    private BuiltInTools? BuiltIns(LiveConversationOperation operation, LiveConversationConfiguration configured, Guid conversation)
    {
        var own = new List<(TextToolDefinition, Func<TextToolCall, CancellationToken, ValueTask<ConversationToolResult>>)>();
        string? guidance = null;
        if (configured.OffersThinkLonger && DeepPlan(configured).Available)
        {
            var settings = configured.ThinkLonger;
            var definitions = ThinkLonger.Definitions(settings);
            own.Add((definitions[0], (call, token) => ThinkLongerAsync(operation, configured, call)));
            own.Add((definitions[1], (call, token) => ValueTask.FromResult(CancelThinking(call))));
            guidance = ThinkLonger.Instructions(settings, configured.Prompts);
        }
        // research while Web research is on (Companion › Deep thinking, off by default) and Deep thinking can think.
        if (OffersResearch(configured))
        {
            own.Add((WebResearch.Definition, (call, token) => ValueTask.FromResult(Research(operation, configured, call))));
            guidance = Join(guidance, WebResearch.Instructions(configured.Prompts));
        }
        // sing_song, play_song and stop_singing while singing is set up (with the Singing prompt).
        if (singing is { Offered: true } && configured.SupportsTools)
        {
            var songs = SongTools.Definitions;
            own.Add((songs[0], (call, token) => ValueTask.FromResult(SingSong(operation, configured, call))));
            own.Add((songs[1], (call, token) => PlaySongAsync(configured, call, token)));
            own.Add((songs[2], (call, token) => ValueTask.FromResult(StopSinging(call))));
            guidance = Join(guidance, SongTools.Instructions(configured.Prompts));
        }
        if (configured.SupportsTools && history?.Searchable(configured.Memory) == true)
            own.Add((PastConversations.Definition, (call, token) => SearchConversationsAsync(call, conversation, token)));
        var kinds = Creations.Kinds;
        if (kinds.Count > 0 && configured.SupportsTools && dataDirectory is not null)
        {
            var definitions = CreationTools.Definitions(kinds);
            own.Add((definitions[0], (call, token) => ValueTask.FromResult(ListCreations(call))));
            own.Add((definitions[1], PerformCreationAsync));
        }
        // manage_memories while memory is on: last, so the tools before it start every request the same as before it existed.
        if (configured.SupportsTools && memory is not null && configured.Memory is { Enabled: true } remembered)
            own.Add((MemoryTools.Definition, (call, token) => ManageMemoriesAsync(operation, remembered.ConfigurationRevision, call, token)));
        // reminders after it, on a PC that keeps reminders (always the same text, so the start of every request stays the same).
        if (configured.SupportsTools && RemindersTool is { } remind)
            own.Add((Reminders.Definition, (call, token) => RemindAsync(remind, call, token)));
        return own.Count == 0 ? null : new(own, guidance);
    }

    /// <summary>Runs one reminders call (set, list, cancel) on this PC's reminders, which travel with the shared settings; set by
    /// the main window. Null where reminders can't be kept (no data folder).</summary>
    internal Func<string, CancellationToken, Task<Reminders.ToolOutcome>>? RemindersTool { get; set; }

    private async ValueTask<ConversationToolResult> RemindAsync(Func<string, CancellationToken, Task<Reminders.ToolOutcome>> remind,
        TextToolCall call, CancellationToken token)
    {
        Reminders.ToolOutcome outcome;
        try { outcome = await remind(call.ArgumentsJson, token).ConfigureAwait(false); }
        catch (Exception error) when (!token.IsCancellationRequested && error is IOException or UnauthorizedAccessException or
            ContractException or InvalidOperationException or TaskCanceledException)
        {
            outcome = new(new("Reminders can't be changed right now. Tell the user briefly.", true), "failed", null);
        }
        tools?.Record("Martlet", Reminders.ToolName, outcome.Outcome, "", outcome.Result.IsError);
        if (outcome.Own is not null) ErrorLog.Info($"Reminders: {Reminders.ToolName} {outcome.Outcome}.");
        return outcome.Result;
    }

    /// <summary>Brings a due reminder into this conversation: a finished notice job that Martlet brings up on its own as soon as
    /// it is free, or with what the user says next. Null when the conversation is closing.</summary>
    internal BackgroundJob? Remind(string label, string text)
    {
        var start = jobs.Start(Reminders.Kind, label.Length > 80 ? label[..80] + "…" : label,
            (_, _) => Task.FromResult(BackgroundJobOutcome.Done(text)));
        return start.Job;
    }

    /// <summary>manage_memories: the model finds, adds, corrects, reassigns or forgets facts when the user asks. Changes are noted
    /// in the talk window like background remembering's.</summary>
    private async ValueTask<ConversationToolResult> ManageMemoriesAsync(LiveConversationOperation operation, Guid revision, TextToolCall call,
        CancellationToken token)
    {
        var roster = voices?.Roster;
        MemoryToolOutcome outcome;
        try
        {
            outcome = await RetryStoreAsync(() => MemoryTools.RunAsync(memory!, revision, call.ArgumentsJson, roster,
                operation.Heard?.Speaker?.Voice, token), token).ConfigureAwait(false);
        }
        catch (Exception error) when (!token.IsCancellationRequested && error is DesktopMemoryException or MemoryException or
            ContractException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            var why = error switch
            {
                DesktopMemoryException app => app.Message,
                MemoryException { Failure: MemoryFailure.Conflict or MemoryFailure.NotFound } => "The fact changed meanwhile; call find again.",
                _ => "Memory can't be changed right now."
            };
            outcome = new(new(why + " Tell the user briefly.", true), "failed", []);
        }
        tools?.Record("Martlet", MemoryTools.Name, outcome.Outcome, "", outcome.Result.IsError);
        if (outcome.Changes.Count > 0)
        {
            ErrorLog.Info($"Memory: manage_memories {outcome.Outcome}.");
            MemoryCaptured?.Invoke(new(outcome.Changes.Select(change => change with { Person = MemoryPeople.Label(change.VoiceId, roster) }).ToArray()));
        }
        return outcome.Result;
    }

    /// <summary>search_conversations: searches the record of earlier conversations (not this one, which the model has).</summary>
    private async ValueTask<ConversationToolResult> SearchConversationsAsync(TextToolCall call, Guid conversation, CancellationToken token)
    {
        var (result, outcome) = await history!.SearchAsync(call, conversation, token).ConfigureAwait(false);
        tools?.Record("Martlet", PastConversations.ToolName, outcome, ConversationHistory.Preview(call.ArgumentsJson, 120), result.IsError);
        return result;
    }

    /// <summary>The kinds of creation Martlet knows and what performs them (docs/CREATIONS.md).</summary>
    internal CreationRegistry Creations { get; init; } = CreationRegistry.Shared;

    /// <summary>list_creations: what Martlet made, from this PC's copy of the shared list (read only when called).</summary>
    private ConversationToolResult ListCreations(TextToolCall call)
    {
        var library = CreationStore.View(dataDirectory!);
        var result = CreationTools.List(library, Creations, call.ArgumentsJson, c => CreationStore.IsComplete(dataDirectory!, c));
        tools?.Record("Martlet", CreationTools.ListName, result.IsError ? "invalid arguments" : $"{library.Live.Count} creations", "", result.IsError);
        return result;
    }

    /// <summary>perform_creation: hands the creation to its kind's handler (a song is sung by the conversation that attached the
    /// song handler), or says clearly why it can't.</summary>
    private async ValueTask<ConversationToolResult> PerformCreationAsync(TextToolCall call, CancellationToken token)
    {
        var library = CreationStore.View(dataDirectory!);
        var result = await CreationTools.PerformAsync(library, Creations, call.ArgumentsJson, c => CreationStore.Assets(dataDirectory!, c), token)
            .ConfigureAwait(false);
        tools?.Record("Martlet", CreationTools.PerformName, result.IsError ? "not performed" : "performed", "", result.IsError);
        ErrorLog.Info($"Creations: perform_creation {(result.IsError ? "didn't start" : "started")}.");
        return result;
    }

    /// <summary>think_longer: starts the background think and returns at once (never waits for it), telling the model to tell
    /// the user now unless it already did. The think continues this reply's request (what was said before the call included).</summary>
    private ValueTask<ConversationToolResult> ThinkLongerAsync(LiveConversationOperation operation, LiveConversationConfiguration configured,
        TextToolCall call)
    {
        const string server = "Martlet";
        var (task, reason, problem) = ThinkLonger.Parse(call.ArgumentsJson);
        if (problem is not null)
        {
            tools?.Record(server, ThinkLonger.Name, "invalid arguments", "", true);
            return ValueTask.FromResult(new ConversationToolResult(problem, true));
        }
        var settings = configured.ThinkLonger;
        if (!settings.On) return ValueTask.FromResult(new ConversationToolResult(ThinkLonger.TurnedOff, true));
        // Where it thinks (Companion › Deep thinking, this PC's choice). Deep thinking is parallel thinking: it runs only where it
        // has a model of its own, and on a second model in Ollama on this PC only while both fit on the graphics card.
        var deep = Volatile.Read(ref deepThinking);
        var plan = DeepThinkingPlan.For(deep, configured.Routes);
        if (!plan.Available)
        {
            tools?.Record(server, ThinkLonger.Name, "not started: unavailable", ThinkLonger.Label(task!), false);
            ErrorLog.Info($"Background thinking: a new think wasn't started ({plan.Why})");
            return ValueTask.FromResult(new ConversationToolResult(ThinkLonger.Unavailable(plan.Why), true));
        }
        var toldUser = !string.IsNullOrWhiteSpace(operation.Turn?.Content.Text);
        var sent = operation.Sent;
        var thinkingModel = configured.Route(SetupRole.Llm).ModelId;
        var where = deep.Separate ? deep.Describe() : thinkingModel;
        var think = new BackgroundThink(ThinkRuntime(),
            left => PrepareThink(configured, deep, sent, () => operation.Turn?.Content.Text, task!, reason, left), clock)
        {
            AttemptFinished = terminal =>
            {
                NoteFallback("Background thinking", configured, terminal);
                NoteInput("Background thinking", terminal, reply: false);
                if (IsFailure(terminal) && terminal.State != ConversationState.Canceled)
                {
                    if (deep.Separate) ErrorLog.Warn($"Background thinking on {where} failed ({Describe(terminal)}).");
                    else LogReplyFailure("Background thinking", configured, terminal);
                }
            }
        };
        var start = jobs.Start(ThinkLonger.Kind(settings), ThinkLonger.Label(task!), async (job, token) =>
        {
            Volatile.Write(ref thinking, think);
            Volatile.Write(ref thinkingWhere, new ThinkPlace(where, plan));
            using var guard = CancellationTokenSource.CreateLinkedTokenSource(token);
            var watch = Task.CompletedTask;
            string? pushed = null;
            try
            {
                if (plan.ChecksFit)
                {
                    // A second model in the same Ollama: it starts only when both fit on the graphics card (Thinking's loaded
                    // first, so its own size counts), and stops if loading it pushed Thinking's off the card after all.
                    job.Report(BackgroundJobState.Waiting, "checking it fits beside Thinking");
                    var fit = await LocalDeepThinking.CheckAsync(thinkingModel, deep.ModelId!, loadThinking: true, token).ConfigureAwait(false);
                    ErrorLog.Info($"Background thinking: {job.Id} {(fit.Fits ? "can" : "can't")} run in Ollama on this PC beside Thinking. {fit.Why}");
                    if (!fit.Fits) return BackgroundJobOutcome.Failed(fit.Why.TrimEnd('.'));
                    watch = LocalDeepThinking.WatchAsync(thinkingModel, deep.ModelId!, why =>
                    {
                        Volatile.Write(ref pushed, why);
                        guard.Cancel();
                    }, guard.Token);
                }
                return await think.RunAsync(job, guard.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested && Volatile.Read(ref pushed) is { } pushedOut)
            {
                ErrorLog.Warn($"Background thinking: {job.Id} stopped. {pushedOut}");
                LocalDeepThinking.RecoverAsync(thinkingModel, deep.ModelId!).Forget();
                return BackgroundJobOutcome.Failed(pushedOut.TrimEnd('.'));
            }
            finally
            {
                await guard.CancelAsync().ConfigureAwait(false);
                await watch.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                Interlocked.CompareExchange(ref thinking, null, think);
                ErrorLog.Info($"Background thinking: {job.Id} ended after {BackgroundJobs.Duration(job.Elapsed)} " +
                    $"({think.Attempts} request{(think.Attempts == 1 ? "" : "s")}, alongside the conversation).");
            }
        });
        if (start.Job is not { } started)
        {
            tools?.Record(server, ThinkLonger.Name, "not started: " + start.Refusal, ThinkLonger.Label(task!), false);
            ErrorLog.Info($"Background thinking: a new think wasn't started ({start.Refusal}).");
            return ValueTask.FromResult(new ConversationToolResult(ThinkLonger.Refused(start), true));
        }
        tools?.Record(server, ThinkLonger.Name, "started " + started.Id, ThinkLonger.Label(task!), false);
        ErrorLog.Info($"Background thinking: started {started.Id} on {where} (thinking steps on, {settings.HowHard} effort, " +
            $"{BackgroundJobs.Duration(settings.TimeLimit)} limit, {jobs.StartedWithinHour(ThinkLonger.KindName)} of {settings.Hourly} " +
            $"this hour; in parallel with the conversation: {plan.Why})" +
            (toldUser ? "." : " The reply hadn't told you yet, so it was asked to."));
        return ValueTask.FromResult(new ConversationToolResult(ThinkLonger.Started(started, toldUser)));
    }

    /// <summary>cancel_thinking: stops the running think; nothing about it is brought up later (Martlet knows).</summary>
    private ConversationToolResult CancelThinking(TextToolCall call)
    {
        var stopped = jobs.Cancel(ThinkLonger.CancelId(call.ArgumentsJson), BackgroundJob.CanceledByMartlet, ThinkLonger.KindName);
        tools?.Record("Martlet", ThinkLonger.CancelName, stopped is null ? "nothing to stop" : "stopped " + stopped.Id, "", false);
        if (stopped is not null) ErrorLog.Info($"Background thinking: Martlet stopped {stopped.Id}.");
        return new(ThinkLonger.Canceled(stopped));
    }

    /// <summary>The talk window's Cancel on a background job: it stops, and the next thing the user says tells Martlet so.</summary>
    internal bool CancelJob(string id)
    {
        var stopped = jobs.Cancel(id, BackgroundJob.CanceledByYou);
        if (stopped is not null) ErrorLog.Info($"Background work: you canceled {stopped.Id}.");
        return stopped is not null;
    }

    /// <summary>The conversation ended (the talk window closed): background work stops and nothing more is brought up, and a song
    /// stops at once.</summary>
    internal void EndBackgroundWork()
    {
        if (jobs.Active.Count > 0) ErrorLog.Info("Background work: the conversation ended, so its background work stopped.");
        jobs.CancelAll();
        singing?.Stop(SongStopCause.Button, musical: false, reason: "closing the conversation");
    }

    // ---------- singing (sing_song, play_song, stop_singing) ----------

    /// <summary>sing_song: starts the song job and returns at once, telling the model to tell the user now unless it already did.
    /// Without lyrics, the job first writes them (and the style, tempo and key) with a background think that continues this
    /// reply's request like think_longer's (Thinking steps on, where Deep thinking thinks, waiting for quiet moments when it shares
    /// the conversation's hardware); then the song maker makes the song in the chosen voice, and the library keeps it.</summary>
    private ConversationToolResult SingSong(LiveConversationOperation operation, LiveConversationConfiguration configured, TextToolCall call)
    {
        const string server = "Martlet";
        var (arguments, problem) = SongTools.ParseSing(call.ArgumentsJson);
        if (arguments is null)
        {
            tools?.Record(server, SongTools.SingName, "invalid arguments", "", true);
            return new(problem!, true);
        }
        var label = SongTools.Label(arguments.About);
        var (setup, unavailable) = singing?.Source?.Current() ?? (null, "singing isn't set up.");
        if (setup is null || singing?.DataDirectory is not { } library)
        {
            tools?.Record(server, SongTools.SingName, "not started: singing unavailable", label, false);
            ErrorLog.Info($"Singing: a song wasn't started ({unavailable}).");
            return new(SongTools.Unavailable(unavailable ?? "singing isn't set up."), true);
        }
        var toldUser = !string.IsNullOrWhiteSpace(operation.Turn?.Content.Text);
        var sent = operation.Sent;
        BackgroundThink? writer = null;
        (string Thinking, string Deep)? beside = null;
        var where = "";
        if (arguments.Lyrics is null)
        {
            // The lyrics are written where Deep thinking thinks, alongside the conversation (never on Thinking's own model, so
            // replies never wait); without a Deep thinking place, the reply writes them itself.
            var deep = Volatile.Read(ref deepThinking);
            var plan = DeepThinkingPlan.For(deep, configured.Routes);
            if (!plan.Available)
            {
                tools?.Record(server, SongTools.SingName, "not started: lyrics needed", label, false);
                return new(SongTools.WriteLyricsYourself(plan.Why), true);
            }
            var thinkingModel = configured.Route(SetupRole.Llm).ModelId;
            where = deep.Separate ? deep.Describe() : thinkingModel;
            if (plan.ChecksFit) beside = (thinkingModel, deep.ModelId!);
            var task = SongTools.WritingTask(configured.Prompts, arguments);
            writer = new BackgroundThink(SongRuntime(),
                left => PrepareThink(configured, deep, sent, () => operation.Turn?.Content.Text, task, null, left, song: true), clock)
            {
                Doing = "Writing the lyrics",
                AttemptFinished = terminal =>
                {
                    NoteFallback("Song lyrics", configured, terminal);
                    NoteInput("Song lyrics", terminal, reply: false);
                    if (IsFailure(terminal) && terminal.State != ConversationState.Canceled)
                        ErrorLog.Warn($"Singing: writing the lyrics on {where} failed ({Describe(terminal)}).");
                }
            };
        }
        var writing = configured.ThinkLonger.TimeLimit;
        var author = new CreationAuthor
        {
            Device = HostSetupCommands.SuggestedDeviceId(), Computer = Environment.MachineName, Voice = setup.VoiceId,
            Persona = configured.Persona?.Name is { Length: > 0 } persona ? persona : null
        };
        var start = jobs.Start(SongTools.Kind, label, (job, token) =>
            MakeSongAsync(job, arguments, setup, library, author, writer, beside, writing, token));
        if (start.Job is not { } started)
        {
            tools?.Record(server, SongTools.SingName, "not started: " + start.Refusal, label, false);
            ErrorLog.Info($"Singing: a new song wasn't started ({start.Refusal}).");
            return new(SongTools.Refused(start), true);
        }
        tools?.Record(server, SongTools.SingName, "started " + started.Id, label, false);
        ErrorLog.Info($"Singing: started {started.Id} ({arguments.Seconds} s, {(writer is null ? "lyrics given" : $"lyrics written on {where}")}, " +
            $"made on {setup.Where} with {setup.Quality} quality and {setup.VoiceMatch} voice match; " +
            $"{jobs.StartedWithinHour(SongTools.KindName)} of {SongTools.PerHour} this hour)" +
            (toldUser ? "." : " The reply hadn't told you yet, so it was asked to."));
        return new(SongTools.Started(started, toldUser, writer is not null));
    }

    // The song job: the singing computer is checked, the lyrics written (when none were given), the song made, its mouth timed
    // to its vocals and the song kept as a creation (shared with every paired Martlet computer).
    private async Task<BackgroundJobOutcome> MakeSongAsync(BackgroundJob job, SingArguments arguments, SongSetup setup, string library,
        CreationAuthor author, BackgroundThink? writer, (string Thinking, string Deep)? beside, TimeSpan writing, CancellationToken token)
    {
        job.Report(BackgroundJobState.Running, "Checking the singing computer");
        var availability = await setup.Maker.GetAvailabilityAsync(token).ConfigureAwait(false);
        if (!availability.Available) return BackgroundJobOutcome.Failed(availability.Reason ?? "singing isn't available right now");
        // VevoSing chosen where only SoulX-Singer is set up (Companion > Voice > Singing offers Add VevoSing there): sing with SoulX.
        var voiceMatch = setup.VoiceMatch;
        if (!availability.VoiceMatches.Contains(voiceMatch))
        {
            ErrorLog.Info($"Singing: {voiceMatch} isn't set up on {availability.Host}; {job.Id} uses SoulX-Singer.");
            voiceMatch = SongVoiceMatch.SoulX;
        }
        WrittenSong? written;
        string? problem;
        if (writer is not null)
        {
            BackgroundJobOutcome lyrics;
            using (var limit = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                limit.CancelAfter(writing);
                var watch = Task.CompletedTask;
                string? pushed = null;
                try
                {
                    if (beside is { } models)
                    {
                        // A second model in the same Ollama writes only while both fit on the graphics card, as a think does.
                        job.Report(BackgroundJobState.Waiting, "checking it fits beside Thinking");
                        var fit = await LocalDeepThinking.CheckAsync(models.Thinking, models.Deep, loadThinking: true, limit.Token).ConfigureAwait(false);
                        if (!fit.Fits) return BackgroundJobOutcome.Failed(fit.Why.TrimEnd('.'));
                        watch = LocalDeepThinking.WatchAsync(models.Thinking, models.Deep, why =>
                        {
                            Volatile.Write(ref pushed, why);
                            limit.Cancel();
                        }, limit.Token);
                    }
                    lyrics = await writer.RunAsync(job, limit.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    if (Volatile.Read(ref pushed) is { } pushedOut && beside is { } models)
                    {
                        LocalDeepThinking.RecoverAsync(models.Thinking, models.Deep).Forget();
                        return BackgroundJobOutcome.Failed(pushedOut.TrimEnd('.'));
                    }
                    return BackgroundJobOutcome.Failed($"writing the lyrics took longer than {BackgroundJobs.Duration(writing)}");
                }
                finally
                {
                    await limit.CancelAsync().ConfigureAwait(false);
                    await watch.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                }
            }
            if (lyrics.Result is not { } text) return BackgroundJobOutcome.Failed(lyrics.Problem ?? "the lyrics couldn't be written");
            (written, problem) = SongTools.ParseWritten(text, arguments);
        }
        else (written, problem) = SongTools.ParseWritten("LYRICS:\n" + arguments.Lyrics, arguments);
        if (written is null) return BackgroundJobOutcome.Failed(problem ?? "the lyrics didn't come out right");
        job.Report(BackgroundJobState.Running, "Writing the music");
        var request = new SongRequest
        {
            Lyrics = written.Lyrics, Style = written.Style, VoiceId = setup.VoiceId, DurationSeconds = arguments.Seconds,
            Bpm = written.Bpm, Key = written.Key, Quality = setup.Quality, VoiceMatch = voiceMatch
        };
        SongResult result;
        try { result = await setup.Maker.GenerateAsync(request, new SongJobProgress(job), token).ConfigureAwait(false); }
        catch (SongException error) { return BackgroundJobOutcome.Failed(SongProblem(error)); }
        job.Report(BackgroundJobState.Running, "Timing the mouth to the singing");
        var (mouth, words, estimated, timing) = await singing!.MouthAsync(result, token).ConfigureAwait(false);
        job.Report(BackgroundJobState.Running, "Saving the song");
        Creation creation;
        try
        {
            creation = await CreationStore.AddAsync(library, SongCreations.Draft(result, written.Title, arguments.About, written.Lyrics,
                written.Style, words, estimated, mouth, author), CreationRegistry.Shared, clock.GetUtcNow(), token).ConfigureAwait(false);
        }
        catch (Exception error) when (CreationStore.IsFailure(error))
        {
            ErrorLog.Warn($"Singing: {job.Id} couldn't keep its song ({error.Message}).");
            return BackgroundJobOutcome.Failed("there was no room to keep the song on this PC");
        }
        var (song, _, _, unreadable) = await SongCreations.LoadAsync(creation, CreationStore.Assets(library, creation), token).ConfigureAwait(false);
        if (song is null) return BackgroundJobOutcome.Failed(unreadable ?? "the song couldn't be read back");
        singing.Made(song);
        ErrorLog.Info($"Singing: {song.Id} mouth track from {mouth.Note} ({mouth.Frames} frames): mouth opens {timing.MedianOffsetMs:+0;-0;0} ms " +
            $"from the vocal onsets (median; mean {timing.MeanAbsoluteOffsetMs:0} ms, 90% within {timing.P90AbsoluteOffsetMs:0} ms; " +
            $"{timing.Matched} of {timing.Onsets} onsets; words {(estimated ? "spread over each line's singing" : "timed by " + result.WordTimingSource)}).");
        ErrorLog.Info($"Singing: {job.Id} made {song.Id} ({SongClock.Of(TimeSpan.FromSeconds(song.DurationSeconds))}, " +
            $"{song.Lines.Count} lines, {song.Generator} + {song.Converter}{(song.Fixture ? ", FIXTURE - NOT AI" : "")}; " +
            $"{creation.Bytes / 1024} KiB kept as a creation) after " +
            $"{BackgroundJobs.Duration(job.Elapsed)}: " + string.Join(", ", result.StageTimings.Select(stage => $"{stage.Stage} {stage.Duration.TotalSeconds:0.0} s")) + ".");
        return BackgroundJobOutcome.Done(SongTools.Ready(song));
    }

    private static string SongProblem(SongException error) => error.Code switch
    {
        SongErrorCodes.Unavailable => "the singing computer isn't reachable or ready",
        SongErrorCodes.Busy => "the singing computer is busy with other songs",
        SongErrorCodes.VoiceMissing => "the voice isn't on the singing computer yet",
        SongErrorCodes.VoiceMatchUnavailable => "the chosen voice match isn't set up on the singing computer",
        SongErrorCodes.RequestInvalid => "the song request wasn't valid (" + error.Message.TrimEnd('.') + ")",
        SongErrorCodes.TimedOut => "making it took too long",
        _ => "making the music failed on the singing computer"
    };

    // The song maker's stages, on the job's chip ("Writing the music", "Matching the singing to the voice").
    private sealed class SongJobProgress(BackgroundJob job) : IProgress<SongProgress>
    {
        public void Report(SongProgress value)
        {
            if (value.Stage != SongStage.Completed) job.Report(BackgroundJobState.Running, value.Describe());
        }
    }

    /// <summary>play_song: plays a finished song (a song creation, which may have been made on another computer) through
    /// Martlet's voice output from where the model chose, with a musical lead-in anywhere but the top; returns at once.</summary>
    private async ValueTask<ConversationToolResult> PlaySongAsync(LiveConversationConfiguration configured, TextToolCall call,
        CancellationToken token)
    {
        const string server = "Martlet";
        var (id, from, problem) = SongTools.ParsePlay(call.ArgumentsJson);
        if (problem is not null)
        {
            tools?.Record(server, SongTools.PlayName, "invalid arguments", "", true);
            return new(problem, true);
        }
        var directory = singing?.DataDirectory;
        if (directory is null || SongCreations.Find(directory, id) is not { } creation)
        {
            tools?.Record(server, SongTools.PlayName, "no such song", "", true);
            var kept = directory is null ? [] : SongCreations.List(directory).Take(5).Select(s => $"{s.Key} (\"{s.Title}\")").ToArray();
            return new(kept.Length == 0 ? $"There's no song {id}, and no finished songs yet."
                : $"There's no song {id}. The newest songs are {string.Join(", ", kept)}.", true);
        }
        var result = await PerformSongAsync(configured, creation, CreationStore.Assets(directory, creation), from, token).ConfigureAwait(false);
        tools?.Record(server, SongTools.PlayName, result.IsError ? "not played" : "playing " + creation.Key, creation.Key, result.IsError);
        return new(result.Text, result.IsError);
    }

    // Sings a song creation from where the model chose (play_song, or perform_creation's from).
    private async ValueTask<CreationActionResult> PerformSongAsync(LiveConversationConfiguration? configured, Creation creation,
        ICreationAssets assets, string? from, CancellationToken token)
    {
        if (singing is null || configured is null) return new("Martlet can't sing in this conversation.", true);
        var (song, audio, mouth, missing) = await SongCreations.LoadAsync(creation, assets, token).ConfigureAwait(false);
        if (song is null || audio is null) return new($"{missing} Say you'll sing it in a moment.", true);
        var speaking = ReplySpeaking();
        var (player, trouble) = singing.Play(song, audio, mouth, from, configured.SpeechOutput(), ReplySpeaking);
        return player is null ? new(trouble ?? "It couldn't play.", true) : new(SongTools.Playing(song, player.Plan, speaking));
    }

    /// <summary>The song kind's handler for perform_creation: options {"from": ...} as play_song's.</summary>
    private ValueTask<CreationActionResult> SingCreationAsync(CreationAction action, CancellationToken token)
    {
        var from = action.Options.ValueKind == System.Text.Json.JsonValueKind.Object && action.Options.TryGetProperty("from", out var value) &&
            value.ValueKind == System.Text.Json.JsonValueKind.String ? value.GetString() : null;
        return PerformSongAsync(Configuration, action.Creation, action.Assets, from, token);
    }

    /// <summary>stop_singing: the song ends musically; the result says where and why (Martlet knows, so no note follows).</summary>
    private ConversationToolResult StopSinging(TextToolCall call)
    {
        var record = singing?.Stop(SongStopCause.Martlet, musical: true, reason: SongTools.ParseStop(call.ArgumentsJson));
        tools?.Record("Martlet", SongTools.StopName, record is null ? "nothing to stop" : "stopped " + record.SongId, "", false);
        return new(SongTools.Stopped(record));
    }

    /// <summary>The talk window stops the song: Stop and Esc quickly (a 300 ms fade), the talk button musically. The note says
    /// which.</summary>
    internal SongStopRecord? StopSong(bool musical, string button) =>
        singing?.Stop(SongStopCause.Button, musical, reason: button);

    // A background think's request: the reply that called think_longer (as said so far) continued and fitted to where it thinks,
    // with its own authorization bound to exactly this request and the time left. A song's lyrics are written the same way, with
    // the song job's own authorization (<paramref name="song"/>).
    private (ConversationRequest, IConversationAuthorizationSource) PrepareThink(LiveConversationConfiguration configured,
        DeepThinkingSettings deep, BoundedTextInput? sent, Func<string?> reply, string task, string? reason, TimeSpan left, bool song = false,
        Action<ICredentialAuthority>? keep = null)
    {
        var full = ThinkLonger.Input(sent, reply(), task, reason, configured.Prompts, sent?.Personality);
        var effort = configured.ThinkLonger.HowHard;
        if (deep.Separate)
        {
            var target = DeepThinkTarget.For(deep, effort, configured.Routes.SingleOrDefault(r => r.Role == SetupRole.Llm),
                ModelLimits.Load(dataDirectory));
            var separate = target.Request(ThinkLonger.Fit(full, target.Bounds), effort, left);
            var own = new DeepThinkAuthorization(target, separate, configured.Profile,
                configured.Routes.SingleOrDefault(r => r.Role == SetupRole.Llm), vault, clock, clock.GetUtcNow() + left + TimeSpan.FromSeconds(5));
            if (keep is not null) keep(own);
            else if (song) Volatile.Write(ref songAuthorization, own);
            else Volatile.Write(ref thinkAuthorization, own);
            return (separate, own);
        }
        // With the Thinking model: within the reply's own bounds, its tools kept so the start is the reply's.
        var input = configured.FitsContext(full) ? full : ThinkLonger.Fit(full, new(configured.TextLimits.MaxInputBytes,
            configured.TextLimits.MaxHistoryMessages, configured.TextInputTokens, Tools: false));
        bool withoutReasoning;
        lock (gate) withoutReasoning = reasoningRefused.Contains(configured.ToolModelKey());
        var request = configured.ThinkRequest(input, left, withoutReasoning);
        var authorization = new ConversationAuthorization(configured, voice: false, microphone: false, clock, () => true,
            settings.LoadAsync, vault, CancellationToken.None, textLimits: request.TextLimits, lifetime: left + TimeSpan.FromSeconds(5));
        // A tool round (declined) and a retry without Thinking steps may each take one more request.
        authorization.BindInput(request.Input, request.Limits.MaxToolRounds + 1);
        if (keep is not null) keep(authorization);
        else if (song) Volatile.Write(ref songAuthorization, authorization);
        else Volatile.Write(ref thinkAuthorization, authorization);
        return (request, authorization);
    }

    private ConversationRuntime ThinkRuntime()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return thinkRuntime ??= runtimeFactory?.Invoke(thinkCredentials, clock) ??
                ConversationRuntime.Create(thinkCredentials, clock: clock, hostText: new HostTextClient());
        }
    }

    // The song job's own text runtime (for writing lyrics), so a think and a song can each have one request at a time.
    private ConversationRuntime SongRuntime()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return songRuntime ??= runtimeFactory?.Invoke(songCredentials, clock) ??
                ConversationRuntime.Create(songCredentials, clock: clock, hostText: new HostTextClient());
        }
    }

    /// <summary>Starts a reply Martlet gives on its own to bring up finished background work, as soon as it is free (the talk
    /// window decides when: never while the user talks, a turn is pending or Martlet is replying). Its message is Martlet's
    /// note with the results (Companion › Prompts › Background work finished), which stays in the conversation; tools stay
    /// available, so a later tool can act on the user's yes. Null when nothing waits.</summary>
    internal LiveConversationOperation? StartReport(bool voice, bool noticesOnly = false)
    {
        var delivery = jobs.Take(onItsOwn: true, noticesOnly);
        if (delivery is null) return null;
        var published = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        LiveConversationOperation operation;
        try
        {
            lock (gate)
            {
                if (disposed || paused || muted || locked) throw new LiveActionException("conversation.controls_blocked");
                if (operations.IsRunning) throw new LiveActionException("conversation.ownership_busy");
                var selected = configuration ?? throw new LiveActionException("conversation.setup_required");
                if (selected.Unavailable(voice, false) is not null) throw new LiveActionException("conversation.configuration_unsupported");
                var input = BackgroundJobs.ReportMessage(selected.Prompts, delivery.Jobs);
                long acceptedRevision = revision = checked(revision + 1);
                var authorization = new ConversationAuthorization(selected, voice, false, clock,
                    () => Volatile.Read(ref revision) == acceptedRevision, settings.LoadAsync, vault, CancellationToken.None);
                operation = new(authorization, CancellationToken.None)
                {
                    Report = true, Delivery = delivery, Spoken = lastAsked.Spoken, BackgroundChattiness = lastAsked.Chattiness
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
            }
        }
        catch
        {
            delivery.Return();
            throw;
        }
        published.SetResult();
        SuperviseAsync(operation).Forget();
        return operation;
    }

    // background-jobs.json in the data directory: each job's kind, ID, state and times, never its task or result. Written off
    // the caller's thread, the newest state last.
    private int statusPending;

    private void WriteJobsStatus()
    {
        if (dataDirectory is null || Interlocked.Exchange(ref statusPending, 1) != 0) return;
        Task.Run(() =>
        {
            Interlocked.Exchange(ref statusPending, 0);
            var status = JobsStatus();
            lock (statusGate)
            {
                try
                {
                    var path = Path.Combine(dataDirectory, JobsStatusFile);
                    File.WriteAllText(path + ".tmp", status);
                    File.Move(path + ".tmp", path, overwrite: true);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
        }).Forget();
    }

    private string JobsStatus()
    {
        object Describe(BackgroundJob job) => new
        {
            id = job.Id, kind = job.Kind.Name, state = job.State.ToString(), progress = job.Progress,
            startedAt = job.StartedUtc, finishedAt = job.FinishedUtc, elapsedSeconds = Math.Round(job.Elapsed.TotalSeconds, 1),
            timeLimitSeconds = job.Kind.TimeLimit.TotalSeconds, offer = job.Kind.Offer,
            resultCharacters = job.Result?.Length, cut = job.Cut, problem = job.Problem, canceledBy = job.CanceledBy,
            delivery = job.Delivery.ToString()
        };
        var running = Volatile.Read(ref thinking);
        var place = Volatile.Read(ref thinkingWhere);
        return System.Text.Json.JsonSerializer.Serialize(new
        {
            updatedAt = clock.GetUtcNow(),
            active = jobs.Active.Select(Describe),
            recent = jobs.Recent.Select(Describe),
            startedLastHour = new { think = jobs.StartedWithinHour(ThinkLonger.KindName), song = jobs.StartedWithinHour(SongTools.KindName) },
            thinking = running is null ? null : new
            {
                where = place?.Where, available = place?.Plan.Available, checksFit = place?.Plan.ChecksFit, why = place?.Plan.Why,
                parallel = true, attempts = running.Attempts
            }
        });
    }

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

    /// <summary>The engine's evidence in a few numbers for the log (never the words).</summary>
    internal static string Describe(TranscriptionEvidence evidence) => string.Join(", ", new[]
    {
        evidence.MeanProbability is { } mean ? $"{evidence.Engine} mean probability {mean:0.00}" : null,
        evidence.MinimumProbability is { } least ? $"lowest {least:0.00}" : null,
        evidence.NoSpeechProbability is { } silence ? $"no-speech {silence:0.00}" : null,
        evidence.AverageLogProbability is { } average ? $"average log probability {average:0.00}" : null
    }.OfType<string>());

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
        var configuration = operation.Authorization.Configuration;
        operation.Recognition = Task.Run(() =>
        {
            try { return recognizer.Recognize(samples, configuration.CompanionNames); }
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
        $"state {terminal.State}, failure {terminal.Failure}" + TimeLimit(terminal.Failure) +
        (terminal.SpeechFailed ? $", voice stopped {terminal.SpeechFailure}" + TimeLimit(terminal.SpeechFailure) : "") +
        (terminal.ProviderFailure is { } provider ? $", provider {provider}" : "") +
        (terminal.SequenceFailure is { } sequence ? $", stream {sequence.Issue}" : "") +
        (terminal.Playback?.Error?.Code is { } audio ? $", audio {audio}" : "") +
        (terminal.ToolCalls > 0 ? $", {terminal.ToolCalls} tool call(s)" : "") +
        (terminal.ToolsRejected ? ", tools rejected" : "") +
        (terminal.FellBackAfter is { } after ? $", Thinking fallback asked after {after}" : "");

    // AuthorizationExpired and BudgetExpired are the request's own time window running out (a slow or loading model), never a
    // key, sign-in or pairing problem.
    internal static string TimeLimit(ConversationFailure? failure) => failure is ConversationFailure.AuthorizationExpired or
        ConversationFailure.BudgetExpired or ConversationFailure.DeadlineExceeded
        ? " (it ran out of time: the model took too long to answer; this is not a key or sign-in problem)" : "";

    // One local log line per Thinking request that answered: when its first words came and how much of its input the model
    // read from its prompt cache (never what was said). Replies and glances also update the talk window's context line.
    private void NoteInput(string what, ConversationSnapshot terminal, bool reply = true)
    {
        if (terminal.FirstTextAfter is null && terminal.InputTokens is null) return;
        var first = terminal.FirstTextAfter is { } after ? $"first words after {after.TotalMilliseconds:0} ms" : "no words";
        var input = terminal.InputTokens is not { } read ? "the model didn't say how many input tokens it read"
            : terminal.CachedInputTokens is { } cached
                ? string.Create(CultureInfo.InvariantCulture, $"{read:N0} input tokens, {cached:N0} of them ({terminal.CachedShare:P0}) from the model's prompt cache")
                : string.Create(CultureInfo.InvariantCulture, $"{read:N0} input tokens (the model didn't say how many came from its cache)");
        ErrorLog.Info($"Thinking input ({what}): {first}; {input}.");
        if (reply && terminal.InputTokens is { } tokens && terminal.CachedInputTokens is { } fromCache)
            lock (gate) lastCache = (tokens, fromCache);
    }

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

    // Memory helps but is never required: if the store can't be read right now, the reply goes ahead without it. The facts of the
    // person speaking (and those about no one in particular) fill the recall before other people's, and each fact says whose it is.
    private async Task<DesktopMemoryRecall?> RecallAsync(LiveConversationOperation operation, string query, CancellationToken worker)
    {
        var roster = voices?.Roster;
        var speaker = MemoryPeople.Ids(operation.Heard?.Speaker?.Voice, roster);
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var recalled = await memory!.RecallAsync(operation.Authorization.Configuration.Memory!, query,
                    DesktopMemoryService.MaximumRecalledFacts, speaker, worker).ConfigureAwait(false);
                return recalled with { People = MemoryPeople.Labels(recalled.Facts, roster) };
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

    /// <summary>Queues the one request after a reply: remembering (<paramref name="remember"/>) and learning the names of the
    /// <paramref name="heard"/> voices, together when both are due. On a Thinking model on this PC it continues
    /// <paramref name="sent"/>, the reply's own request, so the model's prompt cache keeps the conversation for the next reply.
    /// New facts belong to the speaker among the <paramref name="present"/> voices (unless the model names another).</summary>
    private void EnqueueAfterReplyLocked(LiveConversationConfiguration configured, bool remember, HeardVoices? heard,
        IReadOnlyList<TextHistoryMessage> earlier, string user, string reply, BoundedTextInput? sent, HeardVoices? present = null)
    {
        remember &= memory is not null && configured.Memory is { Enabled: true };
        if (voices is null) heard = null;
        if (!remember) present = null;
        if (!remember && heard is null || !AutoCapture || disposed || captureQuarantined || capturesPending >= MaximumPendingCaptures)
            return;
        capturesPending++;
        // Earlier lines heard from what the PC played are left out: memory and learning names only read the user.
        var job = new AfterReplyJob(configured, remember, heard,
            LiveConversationConfiguration.WithoutPcAudio(earlier.LastOrDefault(message => message.Role == TextHistoryRole.User)?.Text),
            earlier.LastOrDefault(message => message.Role == TextHistoryRole.Assistant)?.Text,
            user, reply, configured.LocalThinking ? sent : null, captureCancel.Token, present);
        captureTail = AfterReplyAsync(captureTail, job);
    }

    private sealed record AfterReplyJob(LiveConversationConfiguration Configuration, bool Remember, HeardVoices? Heard, string? EarlierUser,
        string? EarlierReply, string User, string Reply, BoundedTextInput? Conversation, CancellationToken Token, HeardVoices? Present = null)
    {
        public override string ToString() => nameof(AfterReplyJob);
        public string Purpose => Remember && Heard is not null ? "Remembering and learning names" : Remember ? "Remembering" : "Learning names";
    }

    private async Task AfterReplyAsync(Task previous, AfterReplyJob job)
    {
        await previous.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        MemoryCaptureReport? report = null;
        IReadOnlyList<Martlet.Core.Speakers.VoiceUpdateResult>? learned = null;
        try
        {
            (report, learned) = await Task.Run(() => AfterReplyRunAsync(job)).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            ErrorLog.Warn($"{job.Purpose} from a conversation exchange failed.", error);
            if (job.Remember) report = new(Failure: "memory." + error.GetType().Name);
        }
        finally
        {
            lock (gate) capturesPending--;
        }
        if (learned is { Count: > 0 }) VoicesNamed?.Invoke(learned);
        if (!job.Remember) return;
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

    private async Task<(MemoryCaptureReport? Report, IReadOnlyList<Martlet.Core.Speakers.VoiceUpdateResult>? Learned)> AfterReplyRunAsync(
        AfterReplyJob job)
    {
        var token = job.Token;
        var remember = job.Remember;
        MemoryCaptureReport? report = null;
        IReadOnlyList<MemoryFact>? known = null;
        var roster = voices?.Roster;
        // Whose new facts are: the speaker among the voices recognized in the message, when remembering has them.
        var speaker = job.Present?.Speaker?.Voice;
        try
        {
            token.ThrowIfCancellationRequested();
            if (remember)
            {
                try
                {
                    known = (await RetryStoreAsync(() => memory!.KnownFactsAsync(job.Configuration.Memory!, job.User,
                        MemoryCapture.MaximumShownFacts, MemoryPeople.Ids(speaker, roster), token), token).ConfigureAwait(false)).Facts;
                }
                catch (Exception error) when (!token.IsCancellationRequested && error is DesktopMemoryException or MemoryException or
                    IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    // Memory can't be read now; learning names still goes ahead on its own.
                    report = RememberingFailure(error, token);
                    remember = false;
                    if (job.Heard is null) return (report, null);
                }
            }
            var naming = job.Heard is not null && voices is not null
                ? VoiceNaming.Context(job.Heard, voices.Roster, job.User, job.Configuration.CompanionNames) : null;
            var prompt = AfterReply.Prompt(remember ? known : null, naming, job.EarlierUser, job.EarlierReply, job.User, job.Reply,
                job.Configuration.Prompts, job.Conversation, job.Configuration.FitsContext, remember ? job.Present : null,
                remember ? MemoryPeople.Labels(known!, roster) : null);
            var purpose = remember && job.Heard is not null ? "Remembering and learning names" : remember ? "Remembering" : "Learning names";
            var (answer, failure) = await AskAsync(purpose, job.Configuration, prompt.Input, token).ConfigureAwait(false);
            if (answer is null)
                return (remember && !token.IsCancellationRequested && failure is not null ? new(Failure: failure) : report, null);
            IReadOnlyList<Martlet.Core.Speakers.VoiceUpdateResult>? learned = null;
            if (naming is not null && voices is not null)
            {
                token.ThrowIfCancellationRequested();
                var asked = VoiceNaming.Parse(answer, naming, job.Reply);
                var (applied, refused) = voices.Apply(asked.Updates);
                learned = applied;
                // Why lines were left out, never the names (they are personal).
                if (asked.Refused.Count + refused.Count > 0)
                    ErrorLog.Info($"Learning names: {applied.Count} change(s) made; left out: " +
                        string.Join(", ", asked.Refused.Concat(refused).Select(r => r.Reason).Distinct()) + ".");
            }
            if (remember && MemoryCapture.Parse(answer, prompt.ShownFacts, prompt.Voices, speaker?.Id) is { Count: > 0 } operations)
            {
                try
                {
                    var shown = known!.Take(prompt.ShownFacts).ToArray();
                    var changes = await RetryStoreAsync(() => memory!.RememberAsync(job.Configuration.Memory!.ConfigurationRevision, shown,
                        operations, id => MemoryPeople.Canonical(id, roster), token), token).ConfigureAwait(false);
                    // Whose each change is, as the talk window names them (with any name learned from this same answer).
                    var whose = voices?.Roster ?? roster;
                    report = changes.Count == 0 ? null
                        : new(changes.Select(change => change with { Person = MemoryPeople.Label(change.VoiceId, whose) }).ToArray());
                }
                catch (Exception error) when (error is LiveActionException or DesktopMemoryException or MemoryException or
                    ContractException or IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    report = RememberingFailure(error, token);
                }
            }
            return (report, learned);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return (null, null);
        }
        catch (Exception error) when (error is LiveActionException or DesktopMemoryException or MemoryException or
            ContractException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return (remember ? RememberingFailure(error, token) : report, null);
        }
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

    // Turning memory off, changing settings or revoking live work simply drops what was still being remembered.
    private static MemoryCaptureReport? RememberingFailure(Exception error, CancellationToken token)
    {
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
    /// <summary>One extra text-only request to the Thinking model on the background runtime (after a reply, never during
    /// one): the answer, or null with why it failed.</summary>
    private async Task<(string? Answer, string? Failure)> AskAsync(string purpose, LiveConversationConfiguration configuration,
        BoundedTextInput input, CancellationToken token)
    {
        var request = configuration.MemoryCaptureRequest(input);
        var capture = CaptureRuntime();
        var authorization = new ConversationAuthorization(configuration, voice: false, microphone: false, clock,
            () => !token.IsCancellationRequested, settings.LoadAsync, vault, token);
        authorization.BindInput(request.Input, request.Limits.MaxToolRounds);
        Volatile.Write(ref captureAuthorization, authorization);
        try
        {
            var turn = capture.Start(request, authorization, token);
            var terminal = await turn.Completion.ConfigureAwait(false);
            await turn.OwnershipRelease.ConfigureAwait(false);
            NoteFallback(purpose, configuration, terminal);
            NoteInput(purpose, terminal, reply: false);
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

    /// <summary>One request to the saved Thinking model outside a conversation (naming a character's emotes):
    /// <paramref name="instructions"/> and <paramref name="text"/> go to it on the background runtime. Returns its answer, or
    /// null with why not (Thinking isn't set up, or the request failed).</summary>
    internal async Task<(string? Answer, string? Failure)> AskThinkingAsync(string purpose, string instructions, string text,
        CancellationToken token)
    {
        var loaded = await settings.LoadAsync(token).ConfigureAwait(false);
        if (LiveConversationConfiguration.From(loaded, ModelLimits.Load(dataDirectory)) is not { } configured)
            return (null, "Thinking isn't set up yet");
        BoundedTextInput input;
        try { input = new(text, instructions); }
        catch (ContractException) { return (null, "the request is too large"); }
        try { return await AskAsync(purpose, configured, input, token).ConfigureAwait(false); }
        catch (Exception error) when (error is LiveActionException or ContractException or InvalidOperationException)
        {
            return (null, error is LiveActionException live ? live.Code : "the request failed");
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
    // microphone talks over Martlet, and only with real words: while Martlet speaks, BargeInGate has what was said so far
    // transcribed by Parakeet on this PC and BargeInPolicy decides (elsewhere the utterance's own transcript decides).
    private async Task<SpeechRange?> EndpointAsync(LiveConversationOperation operation, CaptureRun run)
    {
        var settings = operation.Listening!.Activity;
        var detector = new EnergyVoiceActivityDetector(settings);
        var talkOver = operation.Listening.Pc ? null : new TalkOverDetector();
        // Quick checks of the words said over Martlet: for barge-in, and while it sings (to hear "stop singing" at once).
        var bargeIn = operation is { Listen: true, Listening.Pc: false } && (operation.Listening.BargeIn || singing is not null) &&
            WordsCheck(operation) is { } check ? (Gate: new BargeInGate(operation.Listening.WordCheck), Check: check) : default;
        Task? checking = null;
        var echo = operation.Echo;
        var minimumFrames = (int)(ListeningOptions.MinimumUtterance.TotalMilliseconds / 20);
        var frame = new byte[EnergyVoiceActivityDetector.FrameBytes];
        // Running counts of loud frames that were a voice the speakers don't explain, of those that were the speakers' sound, and
        // of all frames (loud or not) the speakers explain.
        var userSum = new List<int> { 0 };
        var speakerSum = new List<int> { 0 };
        var explainedSum = new List<int> { 0 };
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
                    explainedSum.Add(explainedSum[^1] + (speakers ? 1 : 0));
                    talkOver?.Process(loud, speakers);
                    // Talking over Martlet: once the voice has gone on long enough (or a short word just ended), what was said so
                    // far is checked for words without waiting for the pause.
                    if (bargeIn.Gate?.Process(loud, speakers, checking is { IsCompleted: false }) == true && !operation.TalkingOver &&
                        Speaking is { } mode && (operation.Listening.BargeIn || mode == PlaybackMode.Song))
                        checking = CheckWordsAsync(operation, run, bargeIn.Gate, bargeIn.Check, index, mode);
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
                        if (accepted < 0) Accept();
                        operation.Hearing = true;
                        if (!operation.Listening.Pc)
                        {
                            // The speech ended where the silence began; the detector noticed after the end-of-speech pause.
                            var now = clock.GetTimestamp();
                            var silence = (long)((index - detector.SpeechEndFrame) * 0.02 * clock.TimestampFrequency);
                            operation.LatencyTimeline = new ReplyTimeline(clock, ReplyTimeline.YouStopped, now - Math.Max(0, silence));
                            operation.LatencyTimeline.Mark("end of speech", now);
                        }
                        await run.ReleaseAsync().ConfigureAwait(false);
                        return Range(accepted, detector.SpeechEndFrame);
                    }
                    if (detector.Speaking && accepted < 0 && index - detector.SpeechStartFrame >= minimumFrames && Voice())
                    {
                        Accept();
                        operation.Hearing = true;
                    }
                }
                while (true);
                // Always listening that can't tell Martlet's own voice from yours stops listening the moment Martlet starts
                // speaking, unless you were already talking. Hearing the output you hear (Martlet's voice included) ends what it
                // was hearing right there instead, so a video that was talking goes on without Martlet's own words.
                if (operation.Listen && (accepted < 0 || operation.Listening!.Pc) && Held(operation.Listening!, operation.Echo))
                {
                    if (accepted < 0)
                    {
                        operation.Publish(new("listen.held"));
                        return null;
                    }
                    if (operation.Listening!.Pc)
                    {
                        await run.ReleaseAsync().ConfigureAwait(false);
                        return Range(accepted, index) with { EndSampleExclusive = index * EnergyVoiceActivityDetector.FrameSamples };
                    }
                }
                if (accepted < 0 && !detector.Speaking && clock.GetElapsedTime(started) >= ListeningOptions.IdleRestart)
                    return null;
                await Task.WhenAny(run.Completion, Task.Delay(TimeSpan.FromMilliseconds(20), clock)).ConfigureAwait(false);
            }
            // Duration limit or Finish: send everything from the onset to the end of the recording.
            if (accepted < 0 && detector.Speaking && Voice()) Accept();
            if (accepted >= 0) operation.Hearing = true;
            return accepted < 0 ? null : Range(accepted, 0) with { EndSampleExclusive = int.MaxValue };
        }
        finally
        {
            CryptographicOperations.ZeroMemory(frame);
            operation.VoiceLevel = -100;
        }

        // The utterance starts here: when its voice began, on the controller's clock (each frame is 20 ms of it).
        void Accept()
        {
            accepted = Onset();
            operation.SpeechStartedAt = clock.GetTimestamp() - (long)((index - accepted) * 0.02 * clock.TimestampFrequency);
        }

        // The speech under way is someone's voice, not mostly what the speakers played (or the user has talked over them).
        bool Voice() => talkOver?.Sustained == true || !SpeakersMostly(detector.SpeechStartFrame);
        bool SpeakersMostly(int from) => from >= 0 && from < userSum.Count &&
            speakerSum[^1] - speakerSum[from] > userSum[^1] - userSum[from];
        // Where what is sent starts: the speech's onset, or where the user's own voice began over what the speakers played.
        int Onset() => SpeakersMostly(detector.SpeechStartFrame) && talkOver is { StretchStartFrame: >= 0 } over
            ? Math.Max(detector.SpeechStartFrame, over.StretchStartFrame) : detector.SpeechStartFrame;

        // What is sent, how much of it was the user's voice (loud frames the speakers don't explain) and how long that voice went
        // on (every frame from its onset to the silence that the speakers don't explain).
        SpeechRange Range(int startFrame, int endFrame)
        {
            var last = Math.Clamp(endFrame <= startFrame ? userSum.Count - 1 : endFrame, 0, userSum.Count - 1);
            var first = Math.Clamp(startFrame, 0, last);
            operation.Voiced = TimeSpan.FromMilliseconds((userSum[last] - userSum[first]) * 20);
            operation.Speech = TimeSpan.FromMilliseconds((last - first - (explainedSum[last] - explainedSum[first])) * 20);
            return new(
                Math.Max(0, startFrame * EnergyVoiceActivityDetector.FrameSamples - EnergyVoiceActivityDetector.Samples(settings.PreRoll)),
                endFrame * EnergyVoiceActivityDetector.FrameSamples + EnergyVoiceActivityDetector.Samples(settings.Tail));
        }
    }

    /// <summary>The quick words check for talking over Martlet: Parakeet on this PC (free, private and fast), or null when
    /// Listening uses a host or the cloud, or only the user's voice may be answered (Voice ID needs the whole utterance), where
    /// the utterance's own transcript decides instead.</summary>
    private Func<ReadOnlyMemory<byte>, CancellationToken, Task<LocalTranscript>>? WordsCheck(LiveConversationOperation operation)
    {
        var configured = operation.Authorization.Configuration;
        if (localWords is not { } local || !configured.LocalStt() || operation.Voiceprint is not null) return null;
        var model = configured.Route(SetupRole.Stt).ModelId;
        return (pcm, token) => local.TranscribeAsync(model, pcm, token);
    }

    /// <summary>What was said while Martlet sings asks it to stop ("okay okay Martlet, stop singing": a stop word with its name, or
    /// with sing, singing, song or music; BargeInPolicy's song rules): the song ends musically and the note quotes the words.
    /// Returns whether this stopped the song.</summary>
    private bool StopSongIfAsked(LiveConversationOperation operation, string? text, UtteranceContext context, ListeningSensitivity sensitivity)
    {
        if (singing is not { Playing: true } songs || operation.StoppedSong) return false;
        if (!BargeInPolicy.Decide(text, context, sensitivity, PlaybackMode.Song).Interrupt) return false;
        if (songs.Stop(SongStopCause.UserWords, musical: true, words: text?.Trim()) is null) return false;
        operation.StoppedSong = true;
        return true;
    }

    // One quick check of what was said over Martlet so far (the current stretch of voice with its pre-roll): Parakeet transcribes
    // it off the microphone loop and BargeInPolicy decides. Real words stop Martlet (TalkingOver); a hum, a cough or laughter
    // never does. Decided only while the utterance is still being recorded: after that, its own transcript decides.
    private Task CheckWordsAsync(LiveConversationOperation operation, CaptureRun run, BargeInGate gate,
        Func<ReadOnlyMemory<byte>, CancellationToken, Task<LocalTranscript>> check, int index, PlaybackMode mode)
    {
        var options = operation.Listening!;
        var from = Math.Max(0, gate.StretchStartFrame - EnergyVoiceActivityDetector.Samples(options.Activity.PreRoll) /
            EnergyVoiceActivityDetector.FrameSamples);
        var pcm = new byte[(index - from) * EnergyVoiceActivityDetector.FrameBytes];
        var frames = 0;
        for (var frame = from; frame < index; frame++, frames++)
            if (!run.TryCopyMonoFrame(frame, pcm.AsSpan(frames * EnergyVoiceActivityDetector.FrameBytes, EnergyVoiceActivityDetector.FrameBytes)))
                break;
        var voice = gate.Voice;
        var speech = gate.Speech;
        var checks = gate.Checks;
        var startedAt = clock.GetTimestamp() - (long)((index - gate.StretchStartFrame) * 0.02 * clock.TimestampFrequency);
        return Task.Run(async () =>
        {
            try
            {
                if (frames == 0) return;
                var heard = await check(pcm.AsMemory(0, frames * EnergyVoiceActivityDetector.FrameBytes), operation.OriginalCaller).ConfigureAwait(false);
                var context = WordsContext(operation, voice, heard.Evidence, speech);
                if (run.Completion.IsCompleted || operation.Authorization.IsCanceled) return;
                // Asked to stop singing: the song ends musically at once, whatever else is being said over it.
                StopSongIfAsked(operation, heard.Text, context, options.WordCheck);
                if (!options.BargeIn) return;
                var decision = BargeInPolicy.Decide(heard.Text, context, options.WordCheck, mode);
                if (!decision.Interrupt) return;
                operation.TalkOver = new(decision, clock.GetElapsedTime(startedAt), checks, startedAt);
                operation.TalkingOver = true;
            }
            // A check that fails (the model unloading, a stop) just doesn't stop Martlet; the utterance's transcript still decides.
            catch (Exception error) when (error is not OutOfMemoryException) { }
            finally { CryptographicOperations.ZeroMemory(pcm); }
        });
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
        // Background work ends with Martlet.
        jobs.Dispose();
        DisposeThinkRuntimeAsync().Forget();
        DisposeResearchRuntimeAsync().Forget();
        DisposeCaptureRuntimeAsync().Forget();
        singing?.DisposeAsync().AsTask().Forget();
        songHandler?.Dispose();
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
    private async Task DisposeThinkRuntimeAsync()
    {
        ConversationRuntime? owned, song;
        lock (gate)
        {
            owned = thinkRuntime;
            song = songRuntime;
        }
        if (owned is not null) await owned.DisposeAsync().ConfigureAwait(false);
        if (song is not null) await song.DisposeAsync().ConfigureAwait(false);
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
