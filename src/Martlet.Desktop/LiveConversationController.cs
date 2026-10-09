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
using Martlet.Core.Pictures;
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
internal sealed record TalkOverResult(BargeInDecision Decision, TimeSpan After, int Checks, long StartedAt,
    BargeInRuling? Ruling = null, TimeSpan? Paused = null);

/// <summary>A reply paused because the user talked over it (Pause and decide): the pause's state, the paused turn, what the
/// quick check decided, when the user's voice began and after how many checks. Finished once (stop or play on).</summary>
internal sealed class HeldReply(BargeInHold hold, ConversationTurn turn, BargeInDecision decision, long startedAt, int checks)
{
    private int finished;
    internal BargeInHold Hold { get; } = hold;
    internal ConversationTurn Turn { get; } = turn;
    internal BargeInDecision Decision { get; } = decision;
    internal long StartedAt { get; } = startedAt;
    internal int Checks { get; } = checks;
    internal bool Finished => Volatile.Read(ref finished) != 0;
    internal bool TryFinish() => Interlocked.Exchange(ref finished, 1) == 0;
}

/// <summary>One barge-in decision, for the talk window's line and MCP (never what was said): when, what decided it, the
/// verdict and why, which judge and how long it took, how long the reply was paused and what happened to it.</summary>
internal sealed record BargeInRecord(DateTimeOffset At, BargeInSource Source, BargeInVerdict Verdict, string Reason, string Judge,
    TimeSpan JudgeTime, TimeSpan? Paused, string Outcome);

/// <summary>A picture Martlet shows in the talk window: its creation key, title, encoded bytes and whether it is a FIXTURE.</summary>
internal sealed record ShownPicture(string Key, string Title, byte[] Image, bool Fixture);

// HandsFree: voice activity endpoints each utterance. RequireVoiceId: only the enrolled voice is uploaded. Hear: the recording
// may go to a Thinking model that hears, or to the audio model of its own (Companion › Listening › Let ... hear my voice);
// HearLocalOnly: only because it was never chosen, so only while the recording stays on this PC (checked again for each message). Straight: with Hear, what was
// said goes straight to a Thinking model that hears as the recording alone, and speech-to-text runs beside the reply
// (Companion › Listening › When Thinking can hear you). BargeIn: talking over a reply with real words (BargeInPolicy) stops
// it; without it, always listening still listens while Martlet speaks whenever echo reduction works, and what it hears waits
// for the reply to finish. ReduceEcho: what
// the PC plays (Martlet's voice included) is removed from the
// microphone first (Companion › Listening › Reduce echo from my speakers), so speakers work without headphones. Pc: listens to
// what this PC plays instead of the microphone (Companion › Listening › Hear what this PC plays): never Voice ID, voice
// recognition, a recording for Thinking or memory. WordCheck: how readily what was heard counts as words (Companion › Listening ›
// Word check; UtteranceFilter and BargeInPolicy). BargeInStyle: with BargeIn, whether words that aren't a clear cue pause the
// reply while a judge decides (PauseAndDecide, the default; BargeInJudging) or stop it at once (StopAtOnce). JudgeTurns: the
// end-of-turn judge decides when the user finished talking (Companion › Listening › Judge when I finish talking, on by default;
// EndOfTurnGate), with the plain pause as its fallback. Early: Companion › Listening › Start replies early (EarlyReplyOptions;
// off unless the talk window passes the choice): the reply starts at the end-of-turn check point on the quick transcript and is
// promoted when the turn ends with the same words.
internal sealed record ListeningOptions(bool HandsFree, VoiceActivitySettings Activity, bool RequireVoiceId, bool Hear = false,
    bool BargeIn = false, bool ReduceEcho = false, bool Pc = false, ListeningSensitivity WordCheck = ListeningSensitivity.Normal,
    bool Straight = false, bool HearLocalOnly = false, BargeInBehavior BargeInStyle = BargeInBehavior.PauseAndDecide,
    bool JudgeTurns = true, EarlyReplyOptions? Early = null)
{
    internal static TimeSpan IdleRestart => TimeSpan.FromSeconds(12);
    internal static TimeSpan MinimumUtterance => TimeSpan.FromMilliseconds(450);

    /// <summary>Companion › Listening › Start replies early, as the talk window passed it (off when it didn't).</summary>
    internal EarlyReplyOptions EarlyReplies => Early ?? EarlyReplyOptions.Off;

    /// <summary>The recording may go where it would go (Thinking, or the audio model of its own): Hear, and, when that is only the
    /// never-chosen default, the recording stays on this PC (<paramref name="staysOnThisPc"/>, checked again for each message).</summary>
    internal bool HearsWith(bool staysOnThisPc) => Hear && (!HearLocalOnly || staysOnThisPc);

    /// <summary>Listening to what this PC plays: the default voice activity (a video's sound, not the user's microphone).</summary>
    internal static ListeningOptions PcAudio { get; } = new(true, new VoiceActivitySettings(), RequireVoiceId: false, Pc: true);
}

/// <summary>The newest picture of what vision watches (taken at most a few seconds earlier), sent along with what the user
/// types or says while vision is on, so the reply sees what they see. <paramref name="Title"/> is the active window's title or
/// the camera's name (empty for an address); <paramref name="App"/> is the program in front, by name, and
/// <paramref name="FullScreen"/> says its window fills its monitor (<see cref="ActiveApp"/>; a screen only).
/// <paramref name="Picture"/> is its picture version (the same while nothing changes on it; 0 when unknown) and
/// <paramref name="TakenAt"/> when it was taken: an image model's description counts for it by these (<see cref="PictureDescriptions"/>).</summary>
internal sealed record SeenScreen(BoundedImage Image, string Title, WatchSource Source, string App = "", bool FullScreen = false,
    long Picture = 0, DateTimeOffset? TakenAt = null)
{
    /// <summary>This picture as the image model gets it, without its pixels (<see cref="PictureShot"/>).</summary>
    internal PictureShot Shot() => ShotOf(Title, Source, App, FullScreen, Picture, TakenAt);

    /// <summary>A picture of <paramref name="source"/> as the image model gets it, before its pixels are encoded: the talk window
    /// keys its screenshots with this, and <see cref="Shot"/> gives the same for the picture that goes with a message.</summary>
    internal static PictureShot ShotOf(string title, WatchSource source, string app, bool fullScreen, long picture, DateTimeOffset? takenAt)
    {
        var clean = Clean(title);
        return new(picture, takenAt ?? DateTimeOffset.MinValue, $"{source.Kind}:{source.Id}", !source.IsScreen,
            source.IsScreen ? clean : "", source.IsScreen ? ActiveApp.Clean(app) : "", source.IsScreen && fullScreen,
            Describe(source.Kind, clean), Short(source.Kind, clean));
    }

    /// <summary>What the picture shows in a few words, for the note with the image model's description.</summary>
    private static string Short(WatchKind kind, string title) => kind switch
    {
        WatchKind.ActiveWindow => "the user's active window",
        WatchKind.ActiveScreen => "the user's whole screen",
        WatchKind.Camera => title.Length > 0 ? $"the user's camera \"{title}\"" : "the user's camera",
        _ => "the user's phone or network camera"
    };

    /// <summary>What the picture shows, for the Screen with your message prompt. It goes in the instructions, so it never
    /// names the window or the program: those change as the user switches windows and go in the notes (<see cref="Active"/>).</summary>
    internal string Describe() => Describe(Source.Kind, CleanTitle);

    private static string Describe(WatchKind kind, string title) => kind switch
    {
        WatchKind.ActiveWindow => "the user's active window",
        WatchKind.ActiveScreen => "the user's whole screen: every monitor, with the taskbar and any pop-up notifications",
        WatchKind.Camera => title.Length > 0 ? $"what the user's camera \"{title}\" sees" : "what the user's camera sees",
        _ => "what the user's phone or network camera sees"
    };

    /// <summary>The Active app with your message note for a picture of the screen: the program in front, whether it is full
    /// screen and the window's title. It goes in the notes that are sent but not kept, after the user's words, so the start of
    /// the request stays the same. Null for a camera, when none of them is known, when the prompt is emptied, or when the
    /// latest [Screen] line of the <paramref name="conversation"/> already says the same (nothing changed since, so the
    /// request grows by nothing).</summary>
    internal string? Active(PromptSettings? prompts, IEnumerable<TextHistoryMessage>? conversation = null)
    {
        var app = ActiveApp.Clean(App);
        var title = CleanTitle;
        if (!Source.IsScreen || app.Length == 0 && title.Length == 0 && !FullScreen) return null;
        if (conversation?.LastOrDefault(m => m.Role == TextHistoryRole.User && VisionHistory.Has(m.Text)) is { } last &&
            VisionHistory.LastSaw(last.Text, camera: false, Where()))
            return null;
        return PromptSettings.Fill(prompts, PromptCatalog.SeenApp, ("app", ActiveApp.Describe(app, FullScreen)),
            ("title", title.Length > 0 ? title : "unknown"));
    }

    /// <summary>Where the picture was taken, as the conversation keeps it (<see cref="VisionHistory"/>): the source, the
    /// window's title (or the camera's name) and the program in front.</summary>
    internal string Where()
    {
        var title = CleanTitle;
        return Source.Kind switch
        {
            WatchKind.ActiveWindow or WatchKind.ActiveScreen => VisionHistory.Screen(Source.Kind == WatchKind.ActiveScreen, title,
                ActiveApp.Label(ActiveApp.Clean(App), FullScreen)),
            WatchKind.Camera => title.Length > 0 ? $"the user's camera \"{title}\"" : "the user's camera",
            _ => "the user's phone or network camera"
        };
    }

    /// <summary>The line the conversation keeps for this picture: a look Martlet took on its own (with <paramref name="why"/>
    /// when something drew its attention), or, with <paramref name="message"/>, the picture that came with a message.</summary>
    internal string HistoryLine(string? seen, bool message = false, string? why = null) => message
        ? VisionHistory.WithMessage(!Source.IsScreen, Where(), seen)
        : VisionHistory.Look(!Source.IsScreen, Where(), why, seen);

    private string CleanTitle => Clean(Title);

    private static string Clean(string title) => new string(title.Where(c => !char.IsControl(c) && c != '"').Take(80).ToArray()).Trim();

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
    /// <summary>The Parakeet model on this PC that heard this utterance because Listening's own route (a paired host or OpenAI)
    /// failed; null when the route heard it, or nothing stood in.</summary>
    internal string? StandIn { get; set; }
    /// <summary>When each step before the reply happened (<see cref="ReplyTimeline"/>), for the desktop log's reply latency line.</summary>
    [JsonIgnore] internal ReplyTimeline? LatencyTimeline { get; set; }
    /// <summary>The controller-clock timestamp the reply's turn started at (0 until it starts).</summary>
    [JsonIgnore] internal long ReplyStartedAt { get; set; }
    [JsonIgnore] internal string? Transcript { get; set; }
    /// <summary>What the user said, kept only while Thinking may hear it (never saved); null otherwise.</summary>
    [JsonIgnore] internal BoundedWaveAudio? Recording { get; set; }
    /// <summary>The reply's request carried the user's recording with the transcript.</summary>
    internal bool VoiceSent { get; set; }
    /// <summary>An utterance the audio model of its own hears beside speech-to-text (the audio path is Described): its words about
    /// how the user sounded, which the reply to it may take (<see cref="VoiceNote"/>).</summary>
    [JsonIgnore] internal VoiceNote? VoiceNote { get; set; }
    /// <summary>A reply to what the audio model heard: each answered utterance's <see cref="VoiceNote"/>, in order.</summary>
    [JsonIgnore] internal IReadOnlyList<VoiceNote>? VoiceNotes { get; set; }
    /// <summary>How many of <see cref="VoiceNotes"/> were ready when the request was built and went with it.</summary>
    internal int VoiceNotesTaken { get; set; }
    /// <summary>Martlet doesn't answer this message (the participation policy turned it down before its request).</summary>
    internal bool Declined { get; set; }
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
    /// <summary>Where this reply's or look's picture goes (docs/SENSE_MODELS.md): in Thinking's own request, to the image model to be
    /// described in words, or nowhere. Fixed when a reply or look with a picture starts; null for a reply without one (a touch, a
    /// reply started early), which follows the route when its request is built.</summary>
    internal SensePath? ImagePath { get; init; }
    /// <summary>In place of the picture, the request carried the image model's description of it (<see cref="Described"/>).</summary>
    internal bool ScreenDescribed => Described is not null;
    /// <summary>The image model's description this reply or look took; in memory only, never saved or logged.</summary>
    [JsonIgnore] internal PictureDescription? Described { get; set; }
    /// <summary>The reply's request as it was sent, which the request after the reply continues on a Thinking model on this
    /// PC (<see cref="AfterReply"/>). In memory only, never saved.</summary>
    [JsonIgnore] internal BoundedTextInput? Sent { get; set; }
    internal Guid? PersonaRevision { get; set; }
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
    /// <summary>A short reply Martlet starts on its own because the user touched the desktop character and said nothing; its
    /// message is the touches (Companion › Prompts › Touched).</summary>
    internal bool Touch { get; init; }
    /// <summary>Martlet started this reply on its own (a report or a reaction to being touched), not an answer to the user.</summary>
    internal bool OnItsOwn => Report || Touch;
    /// <summary>A message from a paired messaging chat (or another remote ask): text in, text out.</summary>
    internal bool Remote { get; init; }
    /// <summary>What the user did to the desktop character that this reply carries (its message for a touch-only reply, its
    /// notes otherwise), put back when the reply never completes.</summary>
    [JsonIgnore] internal Martlet.Conversation.TouchBurst? Touches { get; set; }
    /// <summary>What Thinking was told about <see cref="Touches"/>: the touch reply's message or the note on the user's.</summary>
    [JsonIgnore] internal string? TouchText { get; set; }
    /// <summary>The user's own words this reply answers (typed, or said and transcribed), once its request was built; null for
    /// what Martlet starts on its own, what only the PC played, or what went straight to Thinking. A touch that stops the reply
    /// tells the next one what it was answering (<see cref="Martlet.Conversation.TouchCut"/>).</summary>
    [JsonIgnore] internal string? Asked { get; set; }
    /// <summary>The finished background jobs this reply brings into the conversation (its own message for a report, or the notes
    /// of the user's message); completed once the exchange is kept, otherwise returned for the next reply.</summary>
    [JsonIgnore] internal BackgroundDelivery? Delivery { get; set; }
    /// <summary>A reply Martlet starts on its own (to what this PC played) may bring up finished background work that waits,
    /// in its notes, as a report would (Thinking longer shares results as soon as Martlet is free).</summary>
    internal bool BringUp { get; init; }
    /// <summary>Where a message from a paired messaging chat came from (the app, the chat and the app's ID of the message), and
    /// who sent it, for the record of conversations; null for this PC.</summary>
    [JsonIgnore] internal Martlet.Conversation.HistorySource? Origin { get; init; }
    [JsonIgnore] internal string? OriginSpeaker { get; init; }
    /// <summary>This reply took the look vision wanted (its picture, and what wants the user's attention if anything does), so it
    /// counts as a look.</summary>
    internal bool Look { get; init; }
    /// <summary>What this reply took (MomentTurn.Describe: never what was said, seen or found), once its request started.</summary>
    internal string? Inputs { get; set; }
    /// <summary>How many context board notes this reply's request carried (ContextBoard).</summary>
    internal int BoardNotes { get; set; }
    /// <summary>The kept lines of the context board notes this reply's request carried (ContextNote.Kept), or null.</summary>
    internal string? BoardKept { get; set; }
    /// <summary>How many things Martlet said lately went in this request's notes (What you said lately; SaidLately): only what
    /// Martlet says on its own carries them.</summary>
    internal int SaidLately { get; set; }
    /// <summary><paramref name="text"/> (a message as the conversation keeps it) with <see cref="BoardKept"/> as its last line.</summary>
    internal string? WithBoardKept(string? text) => text is null || BoardKept is null ? text : text + "\n" + BoardKept;
    internal ListeningOptions? Listening { get; init; }
    /// <summary>One utterance recorded by always listening (<see cref="LiveListener"/>): capture and speech-to-text only.</summary>
    internal bool Listen { get; init; }
    /// <summary>A reply to what always listening heard: the model may stay quiet ([pass]) when it wasn't meant for it.</summary>
    internal bool Spoken { get; init; }
    internal double? SpokenConfidence { get; set; }
    /// <summary>A reply started early (Companion › Listening › Start replies early): what it was started with, and whether the
    /// talk window took it as the reply once the turn ended. Null for a reply that started normally.</summary>
    [JsonIgnore] internal EarlyReplyState? Early { get; init; }
    /// <summary>For an utterance always listening records: the reply it started early, if any (the newest).</summary>
    [JsonIgnore] internal LiveConversationOperation? EarlyStarted { get => Volatile.Read(ref earlyStarted); set => Volatile.Write(ref earlyStarted, value); }
    private LiveConversationOperation? earlyStarted;
    /// <summary>The message includes lines heard from what the PC plays (each starts with
    /// <see cref="LiveConversationConfiguration.PcAudioMarker"/>): no Home Assistant without the user's own words and no tools
    /// unless they are there or the message brings up finished background work, and
    /// memory and learning names read only <see cref="UserWords"/>.</summary>
    internal bool PcAudio { get; init; }
    /// <summary>What the user said themselves in a message with <see cref="PcAudio"/>; null when it is only what the PC played.</summary>
    [JsonIgnore] internal string? UserWords { get; init; }
    /// <summary>Martlet in your own Discord calls is on: the reply is told it is in the call (Companion's Discord call prompt in
    /// place of What this PC plays), and lines from the PC are people in the call talking.</summary>
    internal bool DiscordCall { get; init; }
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
    /// <summary>The reply this utterance paused (Pause and decide) until the judge, the user's silence or their talking on
    /// decides; null when it paused nothing.</summary>
    internal HeldReply? Held { get => Volatile.Read(ref held); set => Volatile.Write(ref held, value); }
    private HeldReply? held;
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
    /// <summary>The controller-clock timestamp its recording ended at (0 when unknown).</summary>
    internal long SpeechEndedAt { get; set; }
    /// <summary>The end-of-turn judge's quick transcript of exactly the speech kept (Parakeet on this PC); speech-to-text reuses
    /// it. Null when there is none or the kept audio differs from what it transcribed.</summary>
    [JsonIgnore] internal QuickWords? QuickWords { get; set; }
    /// <summary>This reply's hold on the live floor (a reply to the user), ended once its voice is made or it stops.</summary>
    [JsonIgnore] internal LiveFloorReply? FloorReply { get; set; }
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
    // The Parakeet model on this PC that hears an utterance when Listening's own route fails (LocalSpeechSetup.ListeningStandIn).
    private readonly Func<SetupRoute, string?>? listeningStandIn;
    private readonly IEndOfTurnJudge? turnJudge;
    private readonly SmartTurnJudge? smartTurn;
    private readonly object turnGate = new();
    private readonly Queue<EndOfTurnDecision> turnDecisions = new();
    private int turnJudgeMissingLogged;
    // When Martlet last finished a reply that asked something (controller clock; 0: not lately), so a short answer counts.
    private long askedAt;
    private readonly LocalVoices? voices;
    private readonly ParticipationPolicy policy;
    private readonly ConversationContextBuffer context;
    private readonly string? dataDirectory;
    private readonly TimeProvider clock;
    private readonly Action? revokeAvatar;
    // The desktop character's emotes and motions a reply may use, for the speaking engine (null: a reply that isn't spoken).
    private readonly Func<SpeechEngine?, PromptSettings?, CharacterActionPrompt?>? characterActions;

    /// <summary>The context board: background sources post their newest short note here, and every reply and look takes the
    /// fresh ones in its notes, without waiting (docs/CONVERSATION.md, Context board).</summary>
    internal ContextBoard Board { get; }

    // How long the character's note stays fresh: Martlet posts it again as it builds each request.
    private static readonly TimeSpan CharacterNoteAge = TimeSpan.FromMinutes(1);

    /// <summary>The context board's fresh notes for one request, the character's first: the lingering emotes it shows now
    /// (for the voice that speaks the reply, or none for a reply that isn't spoken) and where its eyes are while a reply's choice
    /// holds them are posted again, or cleared, first.</summary>
    private ContextBoardSnapshot BoardFor(LiveConversationConfiguration configured, bool voice)
    {
        var now = clock.GetLocalNow();
        var character = characterActions?.Invoke(voice ? configured.SpeakingEngine() : null, configured.Prompts);
        if (character?.Showing is not { } showing) Board.Clear(ContextBoard.Character);
        else Board.Post(ContextBoard.Character, showing, now, CharacterNoteAge);
        if (character?.Looking is not { } looking) Board.Clear(ContextBoard.Gaze);
        else Board.Post(ContextBoard.Gaze, looking, now, CharacterNoteAge);
        return Board.Snapshot(now);
    }

    // The request carrying the board's notes was sent: consume-on-read notes go, and the desktop log says what went.
    private void BoardSent(ContextBoardSnapshot sent)
    {
        var consumed = Board.MarkSent(sent);
        if (sent.Notes.Count > 0)
            ErrorLog.Info($"Context board: the request took {sent.Notes.Count} note{(sent.Notes.Count == 1 ? "" : "s")} " +
                $"({string.Join(", ", sent.Sources)}; {sent.Utf8Bytes} bytes{(consumed > 0 ? $"; {consumed} consumed" : "")}).");
    }
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
    // What Martlet said lately (replies, remarks, reactions; never a [pass]), each with when, so what it says on its own can check
    // whether something is worth saying again (SaidLately). In memory only, forgotten with the conversation.
    private readonly SaidLately saidLately = new();
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
    // Memory, emote naming and touch zones: on a Thinking pool member when one can take them, else here after the reply.
    private readonly HelperJobs helpers;
    private IHelperJobPool? helperPool;
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
    // Companion › Listening › Describe PC sounds: the sound digest of what this PC plays (null when Martlet can't hear the PC).
    private readonly PcSoundDigest? soundDigest;
    // What makes sound on this PC (which app, what kind), while Martlet hears it: labels each line it hears and keeps the
    // context board's "activity" note (null when Martlet can't hear the PC).
    private readonly PcActivityMonitor? pcActivity;
    private string? activityNote;
    private long activityPostedAt;
    private long listenEpoch, spokeUntil;
    // Thinking models that rejected a recording this app session; they get the transcript only until Martlet restarts.
    private readonly HashSet<string> deafModels = new(StringComparer.Ordinal);
    // Thinking models that refused the Thinking steps choice this app session; they get their own default until Martlet restarts.
    private readonly HashSet<string> reasoningRefused = new(StringComparer.Ordinal);
    // Background work Martlet started during the conversation (think_longer). Each place Deep thinking thinks on (this PC's
    // choice, deep-thinking.json, read when the conversation is set up or the page saves it) has its own text runtime and
    // credentials, bound to its one think's request at a time, so thinks on several places run at once; and who the last
    // spoken reply heard.
    private readonly BackgroundJobs jobs;
    private readonly Dictionary<string, ThinkSlot> thinkSlots = new(StringComparer.Ordinal);
    private sealed class ThinkSlot
    {
        internal ICredentialAuthority? Authorization;
        internal ConversationCredentialSource? Credentials;
        internal ConversationRuntime? Runtime;
    }
    // The thinks running now, by job ID: where each works and whether it can run there (for background-jobs.json).
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, RunningThink> thinking = new(StringComparer.Ordinal);
    private sealed record RunningThink(BackgroundThink Think, string Where, string Computer, DeepThinkingPlan Plan);
    // A song's lyrics writer on the Deep thinking place it holds while it writes.
    private sealed record LyricsWriter(BackgroundThink Writer, (string Thinking, string Deep)? Beside, BackgroundPlaceLease Place);
    private (bool Spoken, HeardVoices? Heard, ChattinessChoice? Chattiness) lastAsked;
    // What the user did to the desktop character that no reply took yet (touches on zones Martlet notices, and what the strokes
    // and window log add).
    private readonly Martlet.Conversation.TouchLedger touches = new();

    /// <summary>What the user did to the desktop character that waits for the next reply.</summary>
    internal Martlet.Conversation.TouchLedger Touches => touches;

    /// <summary>Now, on the clock the touch ledger's times use.</summary>
    internal TimeSpan TouchNow => clock.GetElapsedTime(0);

    /// <summary>A reply's request carried touches (true: a touch-only reply; false: in the notes of the user's message). Raised
    /// off the UI thread, after the request started.</summary>
    internal event Action<bool, Martlet.Conversation.TouchBurst, string?>? TouchesSent;
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
    // How perform_creation shows a picture in this conversation's talk window.
    private readonly IDisposable? pictureHandler;
    /// <summary>Raised off the dispatcher when Martlet shows a picture (one it just drew, or one shown again).</summary>
    internal event Action<ShownPicture>? PictureShown;

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
    /// <summary>Who makes the screen summaries over time: the image model first while it describes pictures, otherwise the
    /// Thinking pool's digest jobs on a member that sees.</summary>
    internal IScreenDigestThinker ScreenDigestThinker => screenDigestThinker ??=
        new ImageModelScreenDigestThinker(this, new PoolScreenDigestThinker(() => ThinkingPool));
    private IScreenDigestThinker? screenDigestThinker;
    /// <summary>Where screen summaries go: the context board, for the next reply's notes.</summary>
    internal IScreenDigestBoard ScreenDigestBoard => screenDigestBoard ??= new BoardScreenDigest(Board);
    private IScreenDigestBoard? screenDigestBoard;
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
    /// <summary>The sound digest of what this PC plays (Companion › Listening › Describe PC sounds), or null when Martlet can't
    /// hear the PC here.</summary>
    internal PcSoundDigest? SoundDigest => soundDigest;
    /// <summary>What makes sound on this PC while Martlet hears it (a YouTube video in Chrome, a game, a voice chat in Discord):
    /// the talk window turns it on with the PC listener; null when Martlet can't hear the PC here.</summary>
    internal PcActivityMonitor? PcActivity => pcActivity;

    /// <summary>How long the context board's note on what the user is doing stays fresh (the monitor posts it again sooner).</summary>
    internal static TimeSpan ActivityNoteAge => TimeSpan.FromSeconds(30);

    // The monitor's newest look (on its own thread): the context board's "activity" note, posted when it changed or every 10
    // seconds so it stays fresh, and cleared when the monitor stops or nothing is known. The desktop log says when it changed.
    private void PostActivity(PcActivityState state)
    {
        var note = state.Note;
        var now = clock.GetTimestamp();
        if (note is null)
        {
            Board.Clear(ContextBoard.Activity);
            activityNote = null;
            return;
        }
        if (note == activityNote && clock.GetElapsedTime(activityPostedAt, now) < TimeSpan.FromSeconds(10)) return;
        Board.Post(ContextBoard.Activity, note, clock.GetLocalNow(), ActivityNoteAge);
        if (note != activityNote)
            ErrorLog.Info($"What you're doing on this PC, as Martlet guesses it: {string.Join("; ", state.Activities.Select(entry =>
                $"{entry.Source.Kind} ({(entry.Source.Kind == PcActivityKind.Game ? "a game" : entry.Source.App)}" +
                $"{(entry.FullScreen ? ", full screen" : "")}{(entry.Audible ? "" : ", quiet")})"))}.");
        activityNote = note;
        activityPostedAt = now;
    }
    /// <summary>Martlet's background work in this conversation (think_longer): what runs, what finished and what waits to be
    /// brought up.</summary>
    internal BackgroundJobs Jobs => jobs;
    /// <summary>Where memory, emote naming and touch-zone detection ran last (a Thinking pool member or the conversation's own
    /// Thinking model after the reply).</summary>
    internal HelperJobs Helpers => helpers;
    /// <summary>Where helper jobs go first: the Thinking pool (<see cref="ThinkingPool"/>); tests put a fixture pool here.</summary>
    internal IHelperJobPool? HelperPool { get => Volatile.Read(ref helperPool); set => Volatile.Write(ref helperPool, value); }
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
        DesktopMemoryService? memory = null,
        GeneratedSpeechObserver? generatedSpeech = null, Action? revokeAvatar = null, VoiceIdentity? voiceIdentity = null,
        IHostTranscriptionClient? hostListener = null, string? dataDirectory = null, SpokenTextFeed? spokenText = null,
        SmartHome? smartHome = null, LorebookStore? lorebooks = null, McpToolService? tools = null,
        LocalVoices? voices = null, ILocalTranscriber? localListener = null, EchoReducer? echoReducer = null,
        PcAudioCaptureFactory? pcAudio = null, CharacterCueFeed? characterCues = null,
        Func<SpeechEngine?, PromptSettings?, CharacterActionPrompt?>? characterActions = null,
        DesktopConversationHistory? history = null, ConversationSinging? singing = null, ContextBoard? board = null,
        IEndOfTurnJudge? turnJudge = null, Func<SetupRoute, string?>? listeningStandIn = null,
        PcActivityMonitor? pcActivity = null)

    {
        this.operations = operations;
        // Smart Turn on this PC first; a Thinking-pool member only when it is missing or fails.
        smartTurn = turnJudge as SmartTurnJudge;
        this.turnJudge = smartTurn is not null ? EndOfTurnJudges.WithFallback(smartTurn, new PoolTurnJudge(() => ThinkingPool)) : turnJudge;
        this.settings = settings;
        this.vault = vault;
        this.captureDevices = captureDevices;
        this.playbackDevices = playbackDevices;
        this.singing = singing;
        this.echoReducer = echoReducer;
        this.pcAudio = pcAudio;
        this.characterActions = characterActions;
        this.history = history;
        Board = board ?? new();
        if (echoReducer is not null) echoReducer.Reported += EchoReported;
        this.clock = clock ?? TimeProvider.System;
        this.revokeAvatar = revokeAvatar;
        this.memory = memory;
        this.lorebooks = lorebooks;
        this.voiceIdentity = voiceIdentity;
        this.voices = voices;
        this.smartHome = smartHome;
        this.tools = tools;
        this.runtimeFactory = runtimeFactory;
        this.dataDirectory = dataDirectory;
        if (pcAudio?.Sound is { } sound)
            // The audio model of its own judges first while it takes recordings, then a Thinking pool member that hears.
            soundDigest = new PcSoundDigest(sound, () => pcAudio.WithoutMartlet != true && Speaking is not null,
                SoundJudge, Board, dataDirectory, held: SoundJudgeHeld);
        // What makes sound on this PC is followed only while Martlet hears the PC; its clock must be this controller's, which
        // times each utterance.
        if (pcAudio is not null && pcActivity is not null)
        {
            this.pcActivity = pcActivity;
            pcActivity.Updated += PostActivity;
        }
        localTranscription = localListener is null ? null : new(localListener, this.clock);
        localWords = localListener;
        this.listeningStandIn = listeningStandIn;
        context = new();
        captureCredentials = new(() => Volatile.Read(ref captureAuthorization));
        var credentials = new ConversationCredentialSource(() => Volatile.Read(ref active)?.Authorization);
        runtime = runtimeFactory?.Invoke(credentials, this.clock) ??
            ConversationRuntime.Create(credentials, playbackDevices, clock: this.clock, generatedSpeech: generatedSpeech,
                hostText: new HostTextClient(), hostSpeech: dataDirectory is null ? null : new HostSpeechClient(dataDirectory),
                spokenText: spokenText, characterCues: characterCues);
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
        StartLiveFloor();
        StartPresence();
        // The image and audio models this PC uses (docs/SENSE_MODELS.md), before anything can send them work, and what models were
        // found to hear and see, which routes them before a talk window loads the settings.
        senseModels = SenseModels.Load(dataDirectory);
        poolAbilities = dataDirectory is null ? null : ModelAbilities.Load(dataDirectory);
        helperPool = new ThinkingPoolHelpers(() => ThinkingPool);
        helpers = new(() => Volatile.Read(ref helperPool), () => Replying || ReplySpeaking(), dataDirectory) { Floor = floor };
        songCredentials = new(() => Volatile.Read(ref songAuthorization));
        if (singing is not null)
            songHandler = CreationRegistry.Shared.Handle(SongCreations.KindName, new CreationHandler(SingCreationAsync));
        if (dataDirectory is not null)
            pictureHandler = CreationRegistry.Shared.Handle(PictureCreations.KindName, new CreationHandler(ShowPictureCreationAsync));
        jobs.Changed += WriteJobsStatus;
        jobs.Changed += WritePoolStatus;
        WriteJobsStatus();
        // sense-models-status.json from the start, before a talk window loads the settings (it says so).
        QueueSenseStatus();
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
        UseAbilities(abilities);
    }

    /// <summary>Reads model-abilities.json again, after Martlet found out more or another computer shared it.</summary>
    internal void ReloadAbilities()
    {
        if (dataDirectory is not null) UseAbilities(ModelAbilities.Load(dataDirectory));
    }

    // The conversation, the Thinking pool's members and the image and audio models' routes (also before a talk window loads the
    // settings) follow what was found out at once.
    private void UseAbilities(ModelAbilities abilities)
    {
        Volatile.Write(ref poolAbilities, abilities);
        Configuration?.UseAbilities(abilities);
    }

    /// <summary>Whether the Thinking pool (or the conversation model while it is empty) can run a think, for <paramref name="configured"/>'s
    /// routes, with every computer counted as online: what the reply's tools come from.</summary>
    private DeepThinkingPlan DeepPlan(LiveConversationConfiguration configured) => DeepPool(configured).Plan;

    /// <summary>Every Thinking pool member that takes long jobs (or the conversation model while none does and that is allowed), each with
    /// whether a think can run there, with every computer counted as online. The tools a reply offers come from this, so they stay
    /// the same while computers come and go (<see cref="LivePool"/> places the work).</summary>
    private DeepThinkingPool DeepPool(LiveConversationConfiguration configured) => PoolPlan(configured.Routes, longJobs: true);

    /// <summary>As <see cref="DeepPool"/>, but members whose computers are offline now can't run, and the conversation model
    /// stands in when none can and that is allowed: whether a think, research or a song's lyrics can start now, and where.</summary>
    private DeepThinkingPool LivePool(LiveConversationConfiguration configured) => PoolPlan(configured.Routes, live: true, longJobs: true);

    internal void Configure(SettingsLoadResult loaded)
    {
        // The context windows found on this PC (Companion › Replies › Check) keep the context size within the model's own, and
        // what Thinking models were found to hear and see decides whether a recording or picture goes with a message.
        var next = LiveConversationConfiguration.From(loaded, ModelLimits.Load(dataDirectory), ModelAbilities.Load(dataDirectory));
        ReloadThinkingPool();
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
        // The live floor follows what the conversation runs on now.
        UseLiveResources(next);
        // Where pictures and recordings go follows the new Thinking model too (docs/SENSE_MODELS.md); without a data folder (tests)
        // the choices set on the controller stay.
        if (dataDirectory is not null) ReloadSenseModels();
        else SenseRoutesChanged();
        stop?.Cancel("conversation.configuration_changed");
        Cancel(stopListening);
        // Opening the talk window starts the MCP servers in the background, so their tools are ready by the first reply.
        if (next is { SupportsTools: true } && tools is { HasEnabledServers: true }) tools.EnsureStarted(retry: true);
        // The record of conversations is read in the background now, so a message that mentions an earlier one finds it.
        if (history?.Active(next?.Memory) == true) history.Warm();
        // Quick sounds follow the voice: kept ones are read, missing ones made when the voice is free to use.
        FollowQuickSounds();
        // So do the touch zones' voice sounds.
        FollowVoiceSounds();
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
        IReadOnlyList<SpokenWords>? words = null, bool hearLocalOnly = false, bool remote = false, bool bringUp = false,
        AttentionSignal? attention = null, bool look = false, bool discordCall = false, HistorySource? origin = null, string? originSpeaker = null,
        IReadOnlyList<VoiceNote>? voiceNotes = null)
    {
        if (!approved || microphone && (!localCaptureApproved || !uploadApproved))
            throw new LiveActionException("conversation.permission_required");
        // What the PC played goes as words (typed or heard beside it), never with a recording; only a message that is all the
        // PC's may bring up finished work on Martlet's own.
        if (listening is not null && !microphone || spoken && microphone || recording is not null && !spoken ||
            listening?.Pc == true || pcAudio && (microphone || recording is not null) || !pcAudio && userWords is not null ||
            words is not null && (words.Count == 0 || recording is null || pcAudio) || bringUp && (!pcAudio || userWords is not null) ||
            remote && (microphone || spoken || seen is not null || pcAudio || look))
            throw new LiveActionException("conversation.invalid_input");
        listening?.Activity.Validate();
        Voiceprint? voiceprint = null;
        if (listening?.RequireVoiceId == true)
            voiceprint = voiceIdentity?.Current ?? throw new LiveActionException("voiceid.not_enrolled");
        caller.ThrowIfCancellationRequested();
        // A reply started early for exactly this ask (Companion › Listening › Start replies early) is taken as the reply: no
        // second request. One for anything else is let go, and this reply starts once it has left the app slot.
        if (TryTakeEarly(text, voice, microphone, listening, spoken, heard, confidence, recording, seen, pcAudio, userWords, timeline,
                playback, chattiness, words, hearLocalOnly, remote, bringUp, attention, look, discordCall, voiceNotes) is { } early)
            return early;
        // What goes straight to Thinking is the recording alone; its text only marks it until the words come.
        BoundedTextInput? input = microphone ? null : new(words is not null ? LiveConversationConfiguration.VoiceOnlyText : text ?? "");
        if (input is { UserText.Length: > 4096 }) throw new LiveActionException("conversation.input_limit");
        var published = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        LiveConversationOperation operation;
        lock (gate)
        {
            // A message from a paired chat in a messaging app (text in, text out: no microphone, voice or screen) is answered
            // while Windows is locked too, since that is when you're away from this PC.
            if (disposed || paused || muted || locked && !(remote && !voice)) throw new LiveActionException("conversation.controls_blocked");
            if (operations.IsRunning) throw new LiveActionException("conversation.ownership_busy");
            var selected = configuration ?? throw new LiveActionException("conversation.setup_required");
            if (selected.Unavailable(voice, microphone) is not null) throw new LiveActionException("conversation.configuration_unsupported");
            long acceptedRevision = revision = checked(revision + 1);
            // Vision being on is the permission for its pictures. They go in Thinking's own request, to the image model to be
            // described in words for Thinking, or nowhere when no model takes them (docs/SENSE_MODELS.md).
            var imagePath = SenseRoute(SenseKind.Image, selected).Path;
            if (imagePath == SensePath.None) seen = null;
            // Hearing that is on only because it was never chosen holds only while the recording stays on this PC; an audio model
            // of its own takes recordings in Thinking's place, so then Thinking never gets one (docs/SENSE_MODELS.md).
            var stays = RecordingStaysOnThisPc(selected);
            var hear = (microphone ? listening?.HearsWith(stays) == true : recording is not null && (!hearLocalOnly || stays)) &&
                ThinkingTakesVoice(selected);
            if (!hear) recording = null;
            var authorization = new ConversationAuthorization(selected, voice, microphone, clock,
                () => Volatile.Read(ref revision) == acceptedRevision, settings.LoadAsync, vault, caller,
                screen: seen is not null && imagePath == SensePath.Thinking, hear: hear);
            operation = new(authorization, caller)
            {
                MemoryRequested = memory is not null && selected.Memory is { Enabled: true },
                Listening = listening, Voiceprint = voiceprint, Spoken = spoken, Heard = spoken ? heard : null,
                SpokenConfidence = spoken ? confidence : null, Recording = recording, Seen = seen, StraightWords = words,
                VoiceNotes = spoken && voiceNotes is { Count: > 0 } ? voiceNotes : null,
                PcAudio = pcAudio, DiscordCall = discordCall && spoken, UserWords = string.IsNullOrWhiteSpace(userWords) ? null : userWords.Trim(), Playback = playback,
                BringUp = bringUp, Attention = seen is null ? null : attention, Look = look,
                Origin = remote ? origin : null, OriginSpeaker = remote ? originSpeaker : null, Remote = remote,
                WhileSinging = spoken ? singing?.Now() : null,
                BackgroundChattiness = chattiness, ImagePath = imagePath,
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
        // Pressing the talk button: recording and speech-to-text give the image model time to describe the picture first.
        if (microphone && operation is { ImagePath: SensePath.Described, Seen: { } pressed }) DescribeAhead(pressed, PictureTrigger.Talking);
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
        // The end-of-turn judge loads in the background so the first pause is judged too.
        if (!options.Pc && options.JudgeTurns && smartTurn is { } smart) smart.WarmAsync().Forget();
        return listening;
    }

    /// <summary>The end-of-turn judge's state for Companion › Listening and MCP: its name, whether it can answer and why not, how
    /// long it took to load, and the newest decisions (newest last; no words, no audio).</summary>
    internal (string? Judge, bool Available, string? Problem, TimeSpan? LoadTime, EndOfTurnDecision[] Decisions) TurnJudgeStatus
    {
        get
        {
            lock (turnGate)
                return (turnJudge?.Name, turnJudge?.Available == true, turnJudge is null ? "no judge" : smartTurn?.Problem,
                    smartTurn?.LoadTime, turnDecisions.ToArray());
        }
    }

    /// <summary>Raised (on a background thread) after each end-of-turn decision.</summary>
    internal event Action? TurnDecided;

    /// <summary>When the end-of-turn judge is asked and how long an unfinished pause may run (EndOfTurnGate).</summary>
    internal EndOfTurnOptions EndOfTurn { get; set; } = new();

    private void RecordTurn(EndOfTurnDecision decision)
    {
        lock (turnGate)
        {
            turnDecisions.Enqueue(decision);
            while (turnDecisions.Count > 20) turnDecisions.Dequeue();
        }
        ErrorLog.Info(decision.Describe());
        TurnDecided?.Invoke();
    }

    /// <summary>The judge for this utterance, or null for the plain pause rule (judge off, what the PC plays, or no judge that can
    /// answer, which the log says once).</summary>
    private IEndOfTurnJudge? TurnJudge(LiveConversationOperation operation)
    {
        if (operation.Listening is not { JudgeTurns: true, Pc: false } || turnJudge is null) return null;
        if (turnJudge.Available) return turnJudge;
        if (Interlocked.Exchange(ref turnJudgeMissingLogged, 1) == 0)
            ErrorLog.Warn($"End-of-turn judge: {turnJudge.Name} can't answer ({smartTurn?.Problem ?? "unavailable"}, and no Thinking-pool member can judge); " +
                "the plain pause decides when you finished talking.");
        return null;
    }

    /// <summary>Stops always listening: the utterance being recorded is discarded and one being transcribed is canceled, and a
    /// reply started early for what it heard is let go.</summary>
    internal void StopListening(LiveListener listening)
    {
        listening.Revoke();
        listening.Worker.RequestCancellation();
        if (!listening.Pc) LetGoEarly("listening stopped");
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
        options.BargeIn || captureDevices is SimulatedMicrophone or KeptCaptureDeviceFactory { Inner: SimulatedMicrophone } ||
        options.ReduceEcho && echoReducer?.Works(echo) == true;

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

    /// <summary>How long always listening waits before it opens the microphone (or this PC's sound) again after this many
    /// failures in a row: soon after the first (a device that was busy or changing a moment ago often works at once), then
    /// longer, so a microphone that keeps failing isn't opened and dropped every few seconds.</summary>
    internal static TimeSpan MicrophoneRetry(int failures) =>
        TimeSpan.FromSeconds(failures switch { <= 1 => 1, 2 => 5, 3 => 10, 4 => 20, _ => 30 });

    private async Task<SetupWorkResult> ListenLoopAsync(LiveListener listening, CancellationToken token)
    {
        var pending = Task.CompletedTask;
        var failures = 0;
        ErrorCode? lastFailure = null;
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
                    TimeSpan? wait = null;
                    if (status.AudioFailure is { } failure)
                    {
                        wait = MicrophoneRetry(++failures);
                        listening.Retry = wait;
                        // Every failure for a while, then each change and every tenth, so a broken microphone can't flood the log.
                        if (failures <= 5 || failure != lastFailure || failures % 10 == 0)
                            LogMicrophoneFailure(listening, utterance, failure, failures, wait.Value);
                        lastFailure = failure;
                    }
                    else if (utterance.Capture?.Snapshot is { CanonicalSamples: > 0 }) Recovered();
                    if (status.Code is not ("mic.no_speech" or "listen.held"))
                        listening.Post(Result(utterance));
                    if (wait is { } delay)
                        await Task.Delay(delay, clock, token).ConfigureAwait(false);
                    continue;
                }
                Recovered();
                listening.BeginTranscribing();
                utterance.SpeechEndedAt = clock.GetTimestamp();
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

        // The microphone delivered sound and ended without failing: the failures in a row are over.
        void Recovered()
        {
            if (failures == 0) return;
            ErrorLog.Info($"Always listening: {(listening.Pc ? "hearing this PC's sound" : "the microphone")} works again after " +
                $"{failures} failure{(failures == 1 ? "" : "s")} in a row.");
            failures = 0;
            lastFailure = null;
            listening.Retry = null;
        }
    }

    // Why always listening lost the microphone, for the log (a user's log is the only place that shows it): the error, whether
    // the microphone gave any sound first, which microphone and whether echo reduction wrapped it, and the wait before the next
    // try. Never a device name or anything heard.
    private void LogMicrophoneFailure(LiveListener listening, LiveConversationOperation utterance, ErrorCode failure, int failures,
        TimeSpan wait)
    {
        var samples = utterance.Capture?.Snapshot.CanonicalSamples ?? 0;
        var when = samples > 0 ? $"after {samples / 16000.0:0.0} s of sound" : "before any sound";
        var details = listening.Pc ? failure.ToString()
            : $"{failure}, " + (utterance.Authorization.Configuration.Audio?.Input.EndpointId is null
                ? "Windows' default microphone" : "the microphone chosen in Companion")
            + (listening.Options.ReduceEcho && echoReducer is not null ? ", echo reduction on" : "");
        ErrorLog.Warn($"Always listening: {(listening.Pc ? "hearing this PC's sound" : "the microphone")} failed {when} ({details}); " +
            $"Martlet opens it again in {wait.TotalSeconds:0} s ({failures} failure{(failures == 1 ? "" : "s")} in a row).");
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
        utterance.Status.Code == "listen.heard" ? utterance.Words : null, utterance.SpeechEndedAt,
        utterance.Status.Code == "listen.heard" ? utterance.VoiceNote : null);

    /// <summary>Whether what was just heard goes straight to Thinking as the recording alone, with speech-to-text beside the
    /// reply: Thinking may hear it and the straight path is chosen (Companion › Listening), no audio model of its own takes
    /// recordings, the Thinking model hears and hasn't
    /// refused a recording this session, Home Assistant's Assist doesn't need the words first, and it wasn't said over Martlet
    /// while it speaks without a quick check deciding (its words decide whether that stops Martlet).</summary>
    private bool GoesStraight(ListeningOptions options, LiveConversationOperation utterance)
    {
        if (options is not { Straight: true, Hear: true, Pc: false }) return false;
        var configured = utterance.Authorization.Configuration;
        if (!ThinkingTakesVoice(configured)) return false;
        if (!options.HearsWith(RecordingStaysOnThisPc(configured)) || configured.Hearing() != HearingSupport.Supported) return false;
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
            // An audio model of its own hears it beside speech-to-text and puts what the words miss into words (never on the
            // reply's path: the reply takes them only if they are ready in time).
            utterance.VoiceNote = StartVoiceNote(utterance, audio);
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
                {
                    // Only a sound or filler: the live floor stops listening for it.
                    floor.NotWords(ignored.Reason);
                    ErrorLog.Info($"Always listening ignored what it heard: {ignored.Reason} ({ignored.Kind}" +
                        (utterance.Voiced is { } voiced ? $", {voiced.TotalMilliseconds:0} ms of voice" : "") +
                        (utterance.Speech is { } spoken ? $" in {spoken.TotalMilliseconds:0} ms of speech" : "") +
                        (result.Evidence is { } evidence ? ", " + Describe(evidence) : "") + $", word check {options.WordCheck}).");
                }
                utterance.Publish(new("listen.ignored", Finished: true));
                return;
            }
            // Real words from the user's microphone: the live turn comes first from now on.
            if (!pc) FloorWords(utterance, result.Text, words, options.WordCheck, "the transcript had real words");
            // Said over Martlet: its words may stop the reply (a quick check while it was said may already have decided, or paused
            // it for the judge).
            if (options is { BargeIn: true, Pc: false } && Speaking is { } mode)
                utterance.Interrupts = await InterruptsAsync(utterance, result.Text, words, options, mode,
                    result.Evidence?.MeanProbability).ConfigureAwait(false);
            // Said while Martlet sings: asking it to stop ends the song musically; a quick check may already have, and then
            // the note quotes everything that was said.
            if (!options.Pc && !StopSongIfAsked(utterance, result.Text, words, options.WordCheck) && utterance.StoppedSong)
                singing?.Heard(result.Text ?? "");
            utterance.Heard = await HeardAsync(utterance, linked.Token).ConfigureAwait(false);
            if (utterance.Recognition is not null) utterance.LatencyTimeline?.Mark("voice recognition");
            var heardBy = utterance.Authorization.Configuration;
            if (listening.Options.HearsWith(RecordingStaysOnThisPc(heardBy)) && ThinkingTakesVoice(heardBy)) utterance.Recording = audio;
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
            // What always listening heard was let go (not words, another voice, a failure): a reply started early for it goes too.
            if (utterance.EarlyStarted is { } early && utterance.Status.Code != "listen.heard")
                LetGoEarly(early, EarlyReplyRecord.Changed, "what you said was let go");
            // Nobody needs the audio model's words about what was let go.
            if (utterance.Status.Code != "listen.heard") utterance.VoiceNote?.Cancel();
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
        saidLately.Clear();
        lastCache = null;
        conversationId = Guid.NewGuid();
        // The image model's descriptions belong to this conversation too.
        pictures?.Forget();
        // The audio model's late words were about messages the conversation no longer keeps.
        Board.Clear(VoiceNotes.BoardSource);
    }

    /// <summary>The user's Refresh context: forget the kept exchanges and what Martlet said lately; nothing else stops.</summary>
    internal bool ForgetContext()
    {
        // A reply started early was built with what is forgotten now: it goes, and the reply starts again from the fresh context.
        LetGoEarly("the context was refreshed");
        lock (gate)
        {
            if (context.Count == 0 && saidLately.Count == 0) return false;
            memory?.Invalidate();
            ClearContextLocked();
            return true;
        }
    }

    /// <summary>What Martlet said in the last hour (the newest <see cref="SaidLately.MaximumSayings"/>), oldest first, each with
    /// when, for a check-in. In memory only.</summary>
    internal IReadOnlyList<Saying> RecentSayings(DateTimeOffset now)
    {
        lock (gate) return saidLately.Recent(now);
    }

    // What a request Martlet makes on its own says in its notes about what it said lately (null: nothing, or the prompt is
    // emptied), and how many things that is.
    private (string? Note, int Count) SaidLatelyLocked(PromptSettings? prompts)
    {
        var now = clock.GetLocalNow();
        var recent = saidLately.Recent(now);
        return SaidLately.Note(prompts, recent, now, LiveConversationConfiguration.SilentReply) is { } note ? (note, recent.Count) : (null, 0);
    }

    /// <summary>One unprompted screen glance: the image, the window title, the program in front (<paramref name="app"/>, and
    /// whether it is <paramref name="fullScreen"/>) and recent context go to the Thinking model,
    /// which either answers [pass] (silence) or one short remark that is spoken like any reply. It bypasses the
    /// participation policy (that decides whether to answer the user); the caller's pacer decides when to look. With
    /// <paramref name="look"/> (Martlet decides where the character looks) the model may also start its answer with a look tag
    /// that turns the character's eyes to part of the picture. When the image model describes pictures (the Described path) the
    /// look has two stages: the image model describes the picture (<paramref name="picture"/> is its picture version and
    /// <paramref name="takenAt"/> when it was taken, so a description of the same picture is used again), then Thinking gets the
    /// glance message with the description instead of the picture.</summary>
    internal LiveConversationOperation StartCommentary(BoundedImage image, string windowTitle, ChattinessChoice chattiness, bool voice,
        bool screenApproved, WatchSource? source = null, CancellationToken caller = default, AttentionSignal? attention = null,
        bool look = false, string? screenText = null, string? app = null, bool fullScreen = false, long picture = 0,
        DateTimeOffset? takenAt = null)
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
            // Martlet can't see only when no model takes pictures (docs/SENSE_MODELS.md).
            var imagePath = SenseRoute(SenseKind.Image, selected).Path;
            if (imagePath == SensePath.None) throw new LiveActionException("commentary.vision_unsupported");
            long acceptedRevision = revision = checked(revision + 1);
            var authorization = new ConversationAuthorization(selected, voice, false, clock,
                () => Volatile.Read(ref revision) == acceptedRevision, settings.LoadAsync, vault, caller,
                screen: imagePath == SensePath.Thinking);
            operation = new(authorization, caller) { Commentary = true, Attention = attention, ImagePath = imagePath };
            active = operation;
            var camera = source is { IsScreen: false };
            var looked = new SeenScreen(image, windowTitle, source ?? new(WatchKind.ActiveWindow), camera ? "" : app ?? "", !camera && fullScreen,
                picture, takenAt);
            var prompt = CommentaryPromptLocked(windowTitle, camera, selected.Prompts, attention, looked);
            var read = camera ? null : ReadOnScreen(selected.Prompts, screenText);
            var worker = operations.TryStart(async token =>
            {
                await published.Task.ConfigureAwait(false);
                authorization.BindWorker(token);
                return await RunCommentaryAsync(operation, prompt, image, chattiness, camera, look && !camera, token, looked, read).ConfigureAwait(false);
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
        AttentionSignal? attention = null, SeenScreen? looked = null)
    {
        var title = new string(windowTitle.Where(c => !char.IsControl(c) && c != '"').Take(80).ToArray()).Trim();
        // What Martlet said lately goes in the look's notes (What you said lately); a glance prompt an older Martlet saved with
        // {remarks} gets nothing there.
        return PromptSettings.Fill(prompts, attention is not null ? PromptCatalog.GlanceAttention
                : camera ? PromptCatalog.GlanceCamera : PromptCatalog.GlanceScreen,
            ("title", title.Length > 0 ? title : "unknown"), ("remarks", ""),
            ("app", ActiveApp.Describe(ActiveApp.Clean(looked?.App), looked?.FullScreen == true)),
            ("what", attention?.Describe() ?? ""), ("silent", LiveConversationConfiguration.SilentReply))!;
    }

    /// <summary>A screen glance's message: the glance prompt, then the Text on screen prompt with what Companion › Reading read
    /// on that screenshot. The read text goes last, so the request still starts like the one before, and the conversation keeps
    /// only the look's [Screen] line, never this text.</summary>
    internal static string GlanceMessage(string prompt, string? read) => read is null ? prompt : prompt + "\n\n" + read;

    /// <summary>The Text on screen prompt for a look's read text; null when there is none or the prompt is emptied.</summary>
    internal static string? ReadOnScreen(PromptSettings? prompts, string? screenText) =>
        string.IsNullOrWhiteSpace(screenText) ? null
            : PromptSettings.Fill(prompts, PromptCatalog.ReadOnScreen, ("text", screenText)) is { Length: > 0 } read ? read : null;

    internal static bool IsSilentReply(string text) => StayQuiet.IsQuiet(text);

    /// <summary>A reply still streaming that may turn out to be [pass]; it isn't shown until it clearly isn't.</summary>
    internal static bool MaybeSilent(string text) => StayQuiet.MaybeQuiet(text);

    private async Task<SetupWorkResult> RunCommentaryAsync(LiveConversationOperation operation, string prompt, BoundedImage image,
        ChattinessChoice chattiness, bool camera, bool look, CancellationToken worker, SeenScreen looked, string? read = null)
    {
        // While Martlet decides how chatty it is, the look is told how to switch the level (the same at every level) and the
        // level it is at goes in the notes.
        var decides = chattiness == ChattinessChoice.MartletDecides;
        try
        {
            await operation.Authorization.ValidateSettingsAsync(worker).ConfigureAwait(false);
            // Two stages when the image model describes pictures (docs/SENSE_MODELS.md): it describes the look's own picture first
            // (a description already made of that exact picture is used again), then Thinking gets the glance message with the
            // description instead of the picture.
            PictureDescription? described = null;
            if (operation.ImagePath == SensePath.Described)
            {
                operation.Publish(new("commentary.describing"));
                var first = await DescribeLookAsync(looked, operation.Authorization.Configuration.Prompts, worker).ConfigureAwait(false);
                NoteLookPicture(first);
                if (first.Description is not { } words)
                {
                    ErrorLog.Info($"Vision: the image model couldn't describe the picture for a {(camera ? "camera look" : "screen glance")} " +
                        $"({first.Result.Outcome}: {first.Result.Problem ?? "no words"}), so Martlet didn't look this time.");
                    operation.Publish(new(first.Result.Outcome == SenseJobOutcome.Refused ? "commentary.image_refused" : "commentary.not_described",
                        Finished: true));
                    return new(SetupWorkOutcome.Failed);
                }
                described = operation.Described = words;
            }
            IReadOnlyList<TextHistoryMessage> earlier;
            lock (gate) earlier = context.Snapshot();
            var lore = await ScanLoreAsync(operation, prompt, earlier, operation.Authorization.Configuration.Persona, worker)
                .ConfigureAwait(false);
            ConversationTurn turn;
            ContextBoardSnapshot board;
            lock (gate)
            {
                operation.Authorization.Check(worker);
                var configured = operation.Authorization.Configuration;
                var persona = configured.Persona;
                // Earlier messages go exactly as they were sent (with their notes), so the request starts like the one before.
                var history = context.Snapshot(sent: true);
                var level = ChattinessTags.Level(chattiness, decided);
                board = BoardFor(configured, operation.Authorization.Voice);
                // What Martlet said lately goes last in the look's notes, sent with this request only (never kept), so it can
                // tell whether a remark is worth saying again.
                var (lately, latelyCount) = SaidLatelyLocked(configured.Prompts);
                // Described: the image model's words go in the message in place of the picture, with no seen tag and no look
                // tags (only the picture tells where to look); the instructions say what the words are.
                var note = described is null ? null : PictureDescriptions.Note(configured.Prompts, described) ?? described.Text;
                var request = configured.Request(new(note is null ? GlanceMessage(prompt, read) : PictureDescriptions.GlanceMessage(prompt, note, read)),
                    operation.Authorization.Voice, history, null, lore,
                    out var usedHistory, out _, out var usedLore, note is null ? image : null,
                    Join(LiveConversationConfiguration.Moment(configured.Prompts), configured.AdultInstructions,
                        LiveConversationConfiguration.CommentaryInstructions(level, camera, configured.Prompts, decides, described: note is not null)),
                    LiveConversationConfiguration.SilentReply, characterActions: characterActions,
                    gaze: look && note is null ? CharacterGaze.Prompt(configured.Prompts, LiveConversationConfiguration.SilentReply) : null,
                    chattiness: decides ? configured.ChattinessNote(level) : null,
                    controlTags: LiveConversationConfiguration.ControlTags(decides, picture: note is null, configured.Prompts),
                    board: Join(board.Text, lately));
                operation.SaidLately = latelyCount;
                operation.LookOffered = request.CharacterTags.Any(CharacterGaze.IsTag);
                // Exchanges a look had to leave out are never sent again, so later requests start the same way.
                context.LetGoBefore(context.Start + (history.Count - usedHistory) / 2);
                operation.PersonaRevision = persona?.ConfigurationRevision;
                operation.ContextMessages = usedHistory;
                operation.ContextMessagesOmitted = history.Count - usedHistory;
                RecordLore(operation, lore, usedLore);
                operation.Authorization.BindInput(request.Input);
                operation.Publish(new("commentary.looking"));
                turn = runtime.Start(request, operation.Authorization, operation.OriginalCaller);
                operation.Attach(turn);
            }
            BoardSent(board);
            operation.BoardNotes = board.Notes.Count;
            operation.BoardKept = board.KeptText;
            // Nothing else waited (that would have made it a reply that takes the look along): the look alone.
            operation.Inputs = MomentTurn.Describe(false, 0, described is null, operation.Attention?.Plain, 0, contextNotes: operation.BoardNotes,
                said: operation.SaidLately, described: described is not null);
            ErrorLog.Info($"Turn took: {operation.Inputs} (a look).");
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
            if (terminal.State == ConversationState.Completed)
            {
                lock (gate)
                {
                    if (ReferenceEquals(active, operation) && !operation.Authorization.IsCanceled)
                    {
                        // Every look stays in the conversation, passed or not: where Martlet looked and what it saw (the look's
                        // [seen: ...] words, or the first line of the image model's description; never the picture), then its remark
                        // or [pass]. Passes in a row keep only the last.
                        var remark = passed ? $"[{LiveConversationConfiguration.SilentReply}]" : text.Trim();
                        var seen = described?.Summary ?? SeenTags.Description(turn.Controls);
                        var replaced = context.AddLook(operation.WithBoardKept(looked.HistoryLine(seen, why: operation.Attention?.Describe()))!, remark, passed);
                        ErrorLog.Info($"Vision: the conversation keeps a {(camera ? "camera look" : "screen glance")} " +
                            $"({(passed ? "passed" : "remark")}, {(described is not null ? "described by the image model" : seen is null ? "no description" : "described")}" +
                            $"{(replaced ? ", in place of the passed look before it" : "")}).");
                        if (!passed) saidLately.Add(clock.GetLocalNow(), remark);
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
            catch (Exception error) when (error is OperationCanceledException or LiveActionException)
            {
                // A reply started early and not taken is let go: the conversation, memory and history stay as they were.
                if (operation.Early is { Promoted: false }) LetGoEarly(operation, EarlyReplyRecord.Cancelled, "the conversation stopped it");
                else Stop(operation, (error as LiveActionException)?.Code ?? "conversation.canceled");
                return;
            }
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
                // A reply started early isn't under way for the policy until it is taken: it commits then.
                lock (gate)
                {
                    if (ReferenceEquals(active, operation) && !operation.Authorization.IsCanceled && !turn.Held)
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

    // The participation policy for a reply to the user: receipt is NOW for a newly received transcript (never renew a
    // queued, busy or expired intent). With commit, Martlet commits to answering (the reply's dispatch lease, the reason
    // published); without, only whether it would answer now (a reply started early commits once it is taken). The reason says
    // why, for the live floor (Dismisses).
    private (bool Accepted, DispatchLease? Lease, PolicyReason Reason) Participate(LiveConversationOperation operation, BoundedTextInput input,
        bool commit)
    {
        var source = operation.Spoken ? InputSource.HandsFreeListening
            : !operation.Authorization.Microphone ? InputSource.TypedControl
            : operation.HandsFree ? InputSource.HandsFreeListening : InputSource.PushToTalkControl;
        var intent = policy.CreateIntent(new(source,
            new Transcript(input.UserText, confidence: operation.Transcription?.Confidence ?? operation.SpokenConfidence),
            trustedTypedAddress: !operation.Authorization.Microphone && !operation.Spoken));
        var decision = policy.Evaluate(intent);
        if (!commit) return (decision.Kind == DecisionKind.Allow, null, decision.Reason);
        var dispatch = policy.TryCommit(decision);
        operation.Publish(new("policy." + dispatch.Reason, Policy: dispatch.Reason, Finished: !dispatch.Accepted));
        return (dispatch.Accepted, dispatch.Lease, dispatch.Reason);
    }

    private async Task<SetupWorkResult> RunAsync(LiveConversationOperation operation, BoundedTextInput? input, CancellationToken worker)
    {
        DispatchLease? lease = null;
        // A reply started early (Companion › Listening › Start replies early): built and started as usual, but held, with what
        // only a reply that is taken may do (committing to answer, letting go of old history, consuming the context board's
        // notes, the log's lines) put off until the talk window takes it (taken).
        var early = operation.Early;
        var taken = false;
        var dismissed = false;
        try
        {
            await operation.Authorization.ValidateSettingsAsync(worker).ConfigureAwait(false);
            if (early is not null && !await PrepareEarlyAsync(operation, early, worker).ConfigureAwait(false))
                return new(SetupWorkOutcome.Canceled);
            if (operation.Authorization.Microphone)
            {
                // Pressing the talk button is talking to Martlet: the live turn comes first while it records.
                if (!operation.HandsFree) floor.Words("you pressed the talk button");
                var audio = await CaptureAsync(operation).ConfigureAwait(false);
                if (audio is null) return new(SetupWorkOutcome.Completed);
                operation.LatencyTimeline?.Mark("recording");
                // An audio model of its own hears it beside speech-to-text (never on the reply's path).
                if (StartVoiceNote(operation, audio) is { } note) operation.VoiceNotes = [note];
                var result = await TranscribeAsync(operation, audio, transcription, worker).ConfigureAwait(false);
                if (result is null)
                {
                    CancelVoiceNotes(operation);
                    return new(operation.Transcription?.Outcome == TranscriptionOutcome.NoSpeech ? SetupWorkOutcome.Completed : SetupWorkOutcome.Failed);
                }
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
            ContextBoardSnapshot board = ContextBoardSnapshot.Empty;
            (string? Note, string? Kept, VoiceNote[] Ready) heardHow = (null, null, []);
            PersonaProfile? persona;
            IReadOnlyList<TextHistoryMessage> history, sentHistory;
            long historyStart;
            Guid conversation;
            lock (gate)
            {
                operation.Authorization.Check(worker);
                // A report of finished background work (or a reaction to being touched) is Martlet's own, like a screen glance:
                // the participation policy decides whether to answer the user, so it doesn't apply. A reply started early only
                // checks that Martlet would answer now; it commits once it is taken.
                if (!operation.OnItsOwn)
                {
                    var (accepted, committed, reason) = Participate(operation, input!, commit: early is null);
                    // Not now: no reply (one started early goes, unless the talk window just took it: it commits then).
                    if (!accepted && (early is null || LetGoEarly(operation, EarlyReplyRecord.Refused, "Martlet wouldn't answer it now")))
                    {
                        // Martlet won't answer it: what was heard no longer holds the live floor (after the lock). Before the turn
                        // ends, that is for the turn's own reply to say.
                        dismissed = early is null && Dismisses(reason);
                        // The audio model's words about it go to the next request (EndVoiceNotes, after the lock).
                        operation.Declined = early is null;
                        return new(SetupWorkOutcome.Completed);
                    }
                    lease = committed;
                }
                persona = operation.Authorization.Configuration.Persona;
                history = context.Snapshot();
                // Earlier messages go exactly as they were sent (with their notes), so the request starts like the one before;
                // lore, memory and learning names read what was said (history).
                sentHistory = context.Snapshot(sent: true);
                historyStart = context.Start;
                conversation = conversationId;
            }
            // Martlet answers: the live turn comes first until the reply's voice is made (docs/CONVERSATION.md, Live floor).
            BeginFloorReply(operation);

            // Keeps Smart home's list of locks, doors and garages current before the model may call Home Assistant's tools.
            if (smartHome is { ModelToolsEnabled: true } safety) await safety.RefreshSafetyAsync(worker).ConfigureAwait(false);
            operation.LatencyTimeline?.Mark("preparing");

            // What the PC played is never the user: memory, tools and Home Assistant only go by the user's own words, and a
            // message that is only what the PC played gets none of them. A report of background work has no words of the user's.
            // A message that went straight to Thinking has no words yet: memory recalls by the conversation so far (and the
            // speaker), tools and finished background work go with it, and what needs its words (Assist, earlier conversations,
            // lorebook keywords in it) waits for the next message.
            var own = operation.OnItsOwn || straight ? null : operation.PcAudio ? operation.UserWords : input!.UserText;
            operation.Asked = own;
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
            // and perform_creation while any kind of creation is registered), only for the user's own turns (and replies that bring
            // up background work: Martlet's reports, and what this PC played when finished work goes with it) and routes that do
            // function calling. While they are on they are always offered, the same
            // way, so every request starts the same.
            DesktopToolset? toolset = null;
            var configured = operation.Authorization.Configuration;
            // Finished background work that wasn't brought up yet goes with what the user says (its notes), and with what this PC
            // played when Martlet may bring it up on its own.
            if (!operation.Report && operation.Delivery is null)
            {
                if (own is not null || straight) operation.Delivery = jobs.Take(onItsOwn: false);
                // A due reminder comes up as soon as Martlet is free even when other finished work waits for the user's next message.
                else if (operation.BringUp)
                    operation.Delivery = jobs.Take(onItsOwn: true, noticesOnly: configured.ThinkLonger.When != ThinkDelivery.WhenFree);
            }
            var builtIns = BuiltIns(operation, configured, conversation);
            // A message carrying finished work gets the tools a report gets, so a later tool can act on the user's yes.
            if ((own is not null || straight || operation.OnItsOwn || operation.Delivery is not null) && tools is not null &&
                (tools.HasTools || builtIns is not null) && configured.SupportsTools && !tools.IsUnsupported(configured.ToolModelKey()))
            {
                operation.Publish(new("tools.preparing"));
                toolset = await tools.PrepareAsync(worker, builtIns).ConfigureAwait(false);
                operation.Authorization.Check(worker);
                operation.Toolset = toolset;
                operation.LatencyTimeline?.Mark("tools");
            }

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
                    // Assist acts on the house as it answers: a reply started early never asks it before it is taken.
                    if (early is not null && LetGoEarly(operation, EarlyReplyRecord.Refused, "Home Assistant's Assist answers it"))
                        return new(SetupWorkOutcome.Canceled);
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
                past = pastRecord.RecallNotes(own, conversation, sentHistory, configured.Prompts, out pastCount, configured.CharacterName);
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
                // With an image model of its own (the Described path) no picture goes to Thinking. The reply takes the image model's
                // description of the picture it would have sent when one is ready now (it never waits for one), as a note sent with
                // this request only, and every reply is told what such notes are, the same way each time (docs/SENSE_MODELS.md).
                var describes = (operation.ImagePath ?? SenseRoute(SenseKind.Image, configured).Path) == SensePath.Described;
                var description = describes ? DescriptionFor(operation) : null;
                // The note and the [Screen] line say the window and program the description was made of.
                var describedSeen = description is null ? null
                    : operation.Seen! with { Title = description.Shot.Title, App = description.Shot.App, FullScreen = description.Shot.FullScreen };
                var describedNote = description is null ? null : PictureDescriptions.Note(prompts, description);
                // A report keeps the instructions of the reply before it (who was heard, always listening), so it starts the same.
                var heardBy = operation.OnItsOwn ? lastAsked.Heard : operation.Heard;
                var background = !operation.Report && operation.Delivery is { } carried
                    ? BackgroundJobs.ReportNotes(prompts, carried.Jobs) : null;
                // The look this reply took was at something that wants the user's attention (a notification, a flashing taskbar
                // button): a quick heads-up fits in the same reply.
                var noticed = operation.Attention is { } about
                    ? PromptSettings.Fill(prompts, PromptCatalog.MomentAttention, ("what", about.Describe())) : null;
                // Heard while Martlet sings: it keeps singing and answers only when talked to ([pass] otherwise).
                var whileSinging = operation.WhileSinging is { } sung ? SongTools.WhileSinging(prompts, sung.Title, sung.Where) : null;
                // While Martlet decides how chatty it is (and vision is on or it hears this PC), every reply is told how to switch
                // the level, the same way every time; the level goes in the notes when the conversation's notes don't say it yet.
                var decides = operation.BackgroundChattiness == ChattinessChoice.MartletDecides;
                // The context board's fresh notes, taken once (never waited for), go last in the notes and never into history.
                board = BoardFor(configured, operation.Authorization.Voice);
                // What the user did to the desktop character since the last reply goes in the notes of their own message (after
                // their words, never the instructions, so the request starts the same); nothing is there when they did nothing.
                // A touch-only reply already carries it as its message.
                if (!operation.OnItsOwn && !operation.Remote && (own is not null || straight) && operation.Touches is null)
                    operation.Touches = touches.Drain(TouchNow);
                var touchNote = !operation.Touch && operation.Touches is { } touched
                    ? PromptSettings.Fill(prompts, PromptCatalog.TouchedNotes, ("touches", LiveConversationConfiguration.TouchWords(prompts, touched))) : null;
                operation.TouchText = operation.Touch ? input!.UserText : touchNote;
                // Adult content (Companion › Replies), the same in every reply and remark, never in a Discord call.
                var adult = operation.DiscordCall ? null : configured.AdultInstructions;
                // Backup Thinking: once this reply's Thinking request is slow to start, a pool member may answer it instead.
                var backup = BackupFor(operation);
                // What Martlet says on its own (a report, a due reminder, a remark on what this PC played) gets what it said lately
                // last in its notes, sent with this request only. A reply to the user's words or touches never does, so its
                // request, and the time to its first words, stay as they were.
                var trigger = own is not null || straight ? MomentTrigger.User : operation.Touch ? MomentTrigger.Touch
                    : operation.Report ? MomentTrigger.Report : MomentTrigger.PcAudio;
                var (lately, latelyCount) = SaidLately.Carries(trigger) ? SaidLatelyLocked(prompts) : (null, 0);
                operation.SaidLately = latelyCount;
                // An audio model of its own puts the user's voice into words (the audio path is Described): every reply is told
                // what its notes are, the same way each time, and its words about how the user sounded go with this request only
                // when they are ready now (never waited for).
                var described = DescribesVoice(configured) ? PromptSettings.Fill(prompts, PromptCatalog.HeardVoiceDescribed) : null;
                heardHow = VoiceNow(operation, prompts);
                ConversationRequest Ask(SeenScreen? picture, string? recalled, out int keptHistory, out int keptFacts, out int keptEntries) =>
                    operation.Authorization.Configuration.Request(
                        input!, operation.Authorization.Voice, sentHistory, memoryResult, lore,
                        out keptHistory, out keptFacts, out keptEntries, image: picture?.Image,
                        extraInstructions: Join(LiveConversationConfiguration.Moment(prompts), adult,
                            home is { Kind: HomeTurnKind.Tools } ? home.Instructions : null,
                            VoicePromptContext.Preamble(heardBy, prompts),
                            operation.Spoken ? LiveConversationConfiguration.Listening(prompts) : null,
                            operation.DiscordCall ? LiveConversationConfiguration.DiscordCall(prompts)
                                : operation.PcAudio ? LiveConversationConfiguration.PcAudio(prompts) : null,
                            decides ? LiveConversationConfiguration.ChattinessDecides(prompts) : null,
                            recording is null ? null : PromptSettings.Fill(prompts, straight ? PromptCatalog.HeardVoiceOnly : PromptCatalog.HeardVoice),
                            described,
                            picture is null ? null : PromptSettings.Fill(prompts, PromptCatalog.SeenWithMessage, ("source", picture.Describe())),
                            picture is null ? null : SeenTags.Instructions(prompts, LiveConversationConfiguration.SilentReply),
                            describes ? PictureDescriptions.Instructions(prompts) : null),
                        voices: VoicePromptContext.Block(operation.Heard),
                        messageNotes: Join(home is { Kind: HomeTurnKind.Tools } ? null : home?.Instructions, background,
                            picture is null && describedNote is null ? null : noticed, recalled, songNote, whileSinging, touchNote),
                        silentReply: operation.Spoken ? LiveConversationConfiguration.SilentReply : null, tools: toolset,
                        closingInstructions: operation.Authorization.Configuration.ReplyClosing(operation.Authorization.Voice), audio: recording, imageOptional: true,
                        characterActions: characterActions, withoutReasoning: reasoningRefused.Contains(configured.ToolModelKey()),
                        chattiness: decides ? operation.Authorization.Configuration.ChattinessNote(decided) : null,
                        controlTags: LiveConversationConfiguration.ControlTags(decides, picture is not null, prompts),
                        spokenWords: straight ? token => SpokenWords.TranscriptAsync(operation.StraightWords!, token) : null,
                        // The program in front and the window's title change as the user switches windows: they go with the
                        // picture (or its description) in the notes that are sent but not kept, never in the instructions, and only
                        // when the conversation's latest [Screen] line doesn't already say them.
                        board: Join(heardHow.Note, board.Text,
                            picture?.Active(prompts, sentHistory) ?? (describedNote is null ? null : describedSeen?.Active(prompts, sentHistory)),
                            describedNote, lately), backup: backup);
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
                    // A message too long to fit goes without what was said in earlier conversations first, then without the picture
                    // (or its description).
                    catch (LiveActionException error) when (error.Code == "conversation.input_limit" &&
                        (past is not null || picture is not null || describedNote is not null))
                    {
                        if (past is not null) (past, pastCount) = (null, 0);
                        else if (picture is not null) picture = null;
                        else describedNote = null;
                    }
                }
                operation.PastExchanges = pastCount;
                operation.VoiceSent = request.Input.Audio is not null;
                operation.ScreenSent = request.Input.Image is not null;
                operation.Described = describedNote is null ? null : description;
                operation.Sent = request.Input;
                // Exchanges this reply had to leave out are never sent again, so the next replies start the same way (a reply
                // started early lets go of them once it is taken).
                var letGo = historyStart + (history.Count - usedHistory) / 2;
                if (early is null) context.LetGoBefore(letGo);
                else early.LetGoBefore = letGo;
                operation.PersonaRevision = persona?.ConfigurationRevision;
                operation.ContextMessages = usedHistory;
                operation.ContextMessagesOmitted = history.Count - usedHistory;
                operation.MemoryFactsUsed = usedMemory;
                operation.MemoryFactsOmitted = (memoryResult?.Facts.Count ?? 0) - usedMemory;
                RecordLore(operation, lore, usedLore);
                operation.Authorization.BindInput(request.Input, request.Limits.MaxToolRounds, request.ImageOptional);
                // Exact-content commit, pause/consent state and immediate Start share this short, non-awaiting gate.
                operation.ReplyStartedAt = clock.GetTimestamp();
                operation.LatencyTimeline?.Mark("building the request", operation.ReplyStartedAt);
                turn = early is null ? runtime.Start(request, operation.Authorization, operation.OriginalCaller)
                    : runtime.StartEarly(request, operation.Authorization, early.PrepareVoice, operation.OriginalCaller);
                operation.Attach(turn);
                EndFloorReplyWhenMade(operation, turn);
                QuickSoundWhenSlow(operation, turn);
                if (straight && early is null)
                    foreach (var words in operation.StraightWords!) words.ReplyStarted(operation.ReplyStartedAt);
            }
            // Started early: the request streams, held, until the talk window takes it as the reply or it is let go. Taken, it
            // keeps the live floor's hold it took when it started (it ends once its voice is made, as any reply's).
            if (early is not null)
            {
                var (end, committed, refused) = await TakenAsync(operation, early, turn, input!, worker).ConfigureAwait(false);
                dismissed = refused;
                if (end is { } outcome) return new(outcome);
                lease = committed;
                taken = true;
                if (straight)
                    foreach (var words in operation.StraightWords!) words.ReplyStarted(operation.ReplyStartedAt);
            }
            // The request is sent: the board's consume-on-read notes go with this reply only.
            BoardSent(board);
            operation.BoardNotes = board.Notes.Count;
            // The audio model's words it carried leave a short line after the message in the conversation, before the board's.
            operation.BoardKept = heardHow.Kept is null ? board.KeptText : board.KeptText is null ? heardHow.Kept : heardHow.Kept + "\n" + board.KeptText;
            SentVoiceNotes(operation, heardHow.Ready);
            // What this reply took (MomentTurn): the talk window's LiveTurnInputs line and the desktop log, after the request
            // started so the first words never wait for it.
            operation.Inputs = MomentTurn.Describe(own is not null || straight,
                operation.PcAudio ? input!.UserText.Split('\n').Count(line => line.StartsWith(LiveConversationConfiguration.PcAudioMarker, StringComparison.Ordinal)) : 0,
                operation.ScreenSent, operation.ScreenSent || operation.ScreenDescribed ? operation.Attention?.Plain : null, operation.Delivery?.Jobs.Count ?? 0, operation.Report,
                operation.Touches?.Touches ?? 0, operation.BoardNotes, operation.SaidLately, operation.VoiceNotesTaken > 0, operation.ScreenDescribed);
            if (operation.Touches is { } carriedTouches)
            {
                ErrorLog.Info($"Touches: {carriedTouches.Count} went to Thinking " +
                    (operation.Touch ? "as a short reply of their own." : "in the notes of your message."));
                TouchesSent?.Invoke(operation.Touch, carriedTouches, operation.TouchText);
            }
            ErrorLog.Info($"Turn took: {operation.Inputs} ({(operation.Report ? "Martlet's report" : operation.Touch ? "a reaction to being touched" : own is not null || straight ? "a reply to you" : "a reply to what this PC played")}" +
                $"{(operation.Look ? ", counted as a look" : "")}).");
            // Whether a reply with a picture on the Described path took the image model's description (MCP's image_model_check reads
            // the newest line).
            NotePicturePath(operation);
            // Which way what was said went to Thinking (MCP's hearing_check reads the newest line), and once speech-to-text beside
            // the reply has the words, how long after the reply started they came.
            if (straight)
            {
                ErrorLog.Info("Voice path: straight to Thinking (your recording alone, no transcript); speech-to-text runs beside the reply.");
                NoteWordsAsync(operation.StraightWords!).Forget();
                // Something short might not be words (a cough, mm): Parakeet checks it now, beside the request, and the reply is
                // dropped if it isn't words and nothing has played yet. A reply started early was checked on its words already.
                if (early is null && QuickCheck(operation)) CheckWordsBesideAsync(operation, turn).Forget();
            }
            else if (operation.VoiceSent) ErrorLog.Info("Voice path: transcribe first (your recording with the transcript).");
            var terminal = await turn.Completion.ConfigureAwait(false);
            // Touches a reply took but never answered (it was stopped or failed) wait for the next reply.
            if (terminal.State != ConversationState.Completed && operation.Touches is { } unanswered) touches.Restore(unanswered);
            NoteFallback(operation.Report ? "Background report" : "Reply", configured, terminal);
            NoteInput(operation.Report ? "Background report" : "Reply", terminal);
            NoteFirstWords(configured, terminal);
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
                // A paired computer older than recordings refused it before it left this PC: the model may hear, so update that
                // computer instead of remembering the model as deaf.
                if (thinking.RouteType == SetupRouteType.GatewayOllama && HostAudio.IsOld(thinking.Gateway?.HostId))
                    ErrorLog.Info($"Martlet on {thinking.Gateway?.HostId} is older than recordings, so the Thinking model " +
                        $"{thinking.ModelId} got the transcript only; update the Martlet host so Thinking hears your voice.");
                else
                {
                    RecordAbility(new() { Origin = thinking.Origin, ModelId = thinking.ModelId, Hears = false, Source = "a refused recording",
                        CheckedAt = DateTimeOffset.UtcNow });
                    ErrorLog.Info($"The Thinking model {thinking.ModelId} rejected the request with your recording; " +
                        "Martlet asked again with the transcript only and sends it only the transcript from now on.");
                }
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
                        // A message that came with a picture keeps where it was from and what the reply saw in it (its [seen: ...]
                        // words), on a line after the message; the picture itself is never kept.
                        var sawLine = operation.ScreenSent && !terminal.ImageRejected && operation.Seen is { } pictured
                            ? pictured.HistoryLine(SeenTags.Description(turn.Controls), message: true)
                            // In place of the picture, the image model's description: its first line is what Martlet saw.
                            : operation.Described is { } words && operation.Seen is { } shown
                            ? (shown with { Title = words.Shot.Title, App = words.Shot.App, FullScreen = words.Shot.FullScreen }).HistoryLine(words.Summary, message: true)
                            : null;
                        // The touches that came with a message stay noted after it too ("(touch: top of head pat x3)"), so later
                        // replies know; a touch-only reply's message is that line itself.
                        if (!operation.Touch && operation.Touches?.HistoryLine is { Length: > 0 } touchLine)
                            sawLine = sawLine is null ? touchLine : sawLine + "\n" + touchLine;
                        string? Saw(string? text) => text is null || sawLine is null ? text : VisionHistory.After(text, sawLine);
                        string? said = null;
                        if (straight)
                        {
                            // Straight to Thinking: the exchange is kept now and its words replace what stands in for them once
                            // speech-to-text has them; the record of conversations, memory and learning names follow then.
                            var exchange = context.Add(Saw(VoicePromptContext.Prefix(operation.Heard) + LiveConversationConfiguration.VoiceOnlyText)!,
                                kept, configured.HostTarget() is null ? Saw(operation.WithBoardKept(operation.Sent?.KeptUserText)) : null);
                            ConversationContextBuffer.Pending(exchange,
                                KeepWordsAsync(new(operation, configured, conversation, earlier, exchange, turn.Content.Text, passed, sawLine)));
                        }
                        else
                        {
                            // Who said it travels with the words, so later replies (and memory) know who said what. A message with
                            // what the PC played keeps its marked lines as they are.
                            said = operation.Touch && operation.Touches is { } reacted ? reacted.HistoryLine
                                : operation.PcAudio ? input!.UserText : VoicePromptContext.Prefix(operation.Heard) + input!.UserText;
                            // A pass stays in the conversation too, so later replies know what was said around Martlet.
                            context.Add(Saw(said)!, kept, configured.HostTarget() is null ? Saw(operation.WithBoardKept(operation.Sent?.KeptUserText)) : null);
                            // The record of conversations keeps the user's own words (never what the PC played) and the reply,
                            // written in the background after the reply. A pass wasn't said to Martlet, and glances never get here.
                            if (!passed && this.history is { } historyRecord && historyRecord.Active(configured.Memory) &&
                                (operation.Report ? "" : operation.Touch ? said : operation.PcAudio ? operation.UserWords : input!.UserText) is { } recordedWords)
                                historyRecord.Record(conversation, operation.Report ? HistoryInputKind.Report : operation.Touch ? HistoryInputKind.Touch
                                        : operation.Spoken || operation.Authorization.Microphone ? HistoryInputKind.Spoken : HistoryInputKind.Typed,
                                    recordedWords, turn.Content.Text,
                                    operation.OriginSpeaker ?? (operation.Heard?.Speaker?.Voice is { Named: true } namedVoice ? namedVoice.DisplayName : null),
                                    operation.Origin);
                        }
                        // What Martlet said, with when, for what it says on its own next (a [pass] isn't noted).
                        saidLately.Add(clock.GetLocalNow(), kept);
                        // The finished background work this reply carried is in the conversation now.
                        if (operation.Delivery is { } delivered)
                        {
                            delivered.Complete();
                            ErrorLog.Info($"Background work: {string.Join(", ", delivered.Jobs.Select(job => job.Id))} " +
                                (operation.Report ? "brought up by Martlet on its own"
                                    : own is null && !straight ? "brought up with what this PC played" : "brought up with your message") +
                                (passed ? " (it stayed quiet about it)." : "."));
                        }
                        if (!operation.OnItsOwn) lastAsked = (operation.Spoken, operation.Heard, operation.BackgroundChattiness);
                        // The note about the last song is in the conversation now.
                        if (songNote is not null) singing?.NoteDelivered(songNote);
                        // Memory and learning names only ever read what the user said themselves, never what the PC played.
                        var spokenOwn = operation.OnItsOwn || straight ? null : operation.PcAudio ? operation.UserWords : input!.UserText;
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
            // A reply started early that was never taken said and did nothing: the touches it took wait for the next reply.
            if (early is not null && !taken)
            {
                if (early.Waiting) LetGoEarly(operation, EarlyReplyRecord.Cancelled, "the conversation stopped it");
                if (operation.Touches is { } untaken)
                {
                    touches.Restore(untaken);
                    operation.Touches = null;
                }
            }
            // The reply no longer holds the live floor (its voice may have been made already).
            operation.FloorReply?.End(early is not null && !taken ? EarlyLetGoFloor : "the reply ended");
            if (dismissed) floor.Dismiss("Martlet won't reply to it");
            // Whatever happened to the reply, the words of what it carried are transcribed now.
            if (operation.StraightWords is { } spoken)
                foreach (var words in spoken) words.Release();
            // And the audio model may hear what it carried now (or its words go to the next request when Martlet didn't answer).
            EndVoiceNotes(operation);
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
        LiveConversationConfiguration.WithoutMarked(history.LastOrDefault(message => message.Role == TextHistoryRole.User)?.Text) ?? "";

    private sealed record StraightExchange(LiveConversationOperation Operation, LiveConversationConfiguration Configured, Guid Conversation,
        IReadOnlyList<TextHistoryMessage> Earlier, object Exchange, string Reply, bool Passed, string? SawLine = null)
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
            // A picture that came with the message stays noted after its words (VisionHistory).
            if (current) filled = context.Fill(keep.Exchange, keep.SawLine is null ? said : VisionHistory.After(said, keep.SawLine),
                hostless && sent is not null ? keep.SawLine is null ? operation.WithBoardKept(sent.KeptUserText) : VisionHistory.After(operation.WithBoardKept(sent.KeptUserText)!, keep.SawLine) : null);
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

    /// <summary>think_longer and cancel_thinking as a reply carries them while Thinking longer is on and the pool can think, else
    /// none. How many think at once comes from the configured members alone (<see cref="DeepPool"/>: every computer counted as
    /// online), never from what answers or is busy now, so the start of every request stays the same for prompt caches while
    /// computers come and go.</summary>
    internal IReadOnlyList<TextToolDefinition> ReplyThinkTools(LiveConversationConfiguration configured) =>
        configured.OffersThinkLonger && DeepPool(configured) is { Plan.Available: true } pool
            ? ThinkLonger.Definitions(configured.ThinkLonger, ThinkLonger.Slots(ThinkLonger.Places(pool))) : [];

    /// <summary>Martlet's own tools for one reply, always the same ones in the same order while their settings stay, so the start
    /// of every request stays the same: think_longer and cancel_thinking while Thinking longer is on (with the Thinking longer
    /// prompt), research while Web research is on too (with its prompt), then the song tools while singing is set up, then draw_picture while pictures are set up (Companion › Pictures),
    /// then search_conversations while the owner lets Martlet search the record of conversations (Companion › Memory,
    /// off by default), then list_creations and perform_creation while any kind of creation is registered (CreationRegistry,
    /// docs/CREATIONS.md), then manage_memories while memory is on, then reminders, call_on_discord and set_camera_background
    /// while they apply. Null when there are none.</summary>
    private BuiltInTools? BuiltIns(LiveConversationOperation operation, LiveConversationConfiguration configured, Guid conversation)
    {
        var own = new List<(TextToolDefinition, Func<TextToolCall, CancellationToken, ValueTask<ConversationToolResult>>)>();
        string? guidance = null;
        if (ReplyThinkTools(configured) is { Count: > 0 } thinkTools)
        {
            var settings = configured.ThinkLonger;
            own.Add((thinkTools[0], (call, token) => ThinkLongerAsync(operation, configured, call)));
            own.Add((thinkTools[1], (call, token) => ValueTask.FromResult(CancelThinking(call))));
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
        // draw_picture while pictures are set up (Companion › Pictures).
        if (configured.SupportsTools && dataDirectory is not null && PictureClient.IsSetUp(dataDirectory))
            own.Add((PictureTools.Definition, (call, token) => ValueTask.FromResult(DrawPicture(operation, configured, call))));
        if (configured.SupportsTools && history?.Searchable(configured.Memory) == true)
            own.Add((PastConversations.Definition, (call, token) => SearchConversationsAsync(call, conversation, configured.CharacterName, token)));
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
        // call_on_discord after them while Martlet can call a Discord friend (saved choices only, so it doesn't come and go with the
        // connection).
        if (configured.SupportsTools && DiscordCaller is { CanCall: true } caller)
            own.Add((DiscordCallTool.Definition, (call, token) => CallOnDiscordAsync(caller, call, token)));
        // set_camera_background last, while Martlet is in the owner's Discord calls (the saved choice only).
        if (configured.SupportsTools && CallCamera is { Offered: true } camera)
            own.Add((CameraBackgroundTool.Definition, (call, token) => SetCameraBackgroundAsync(operation, configured, camera, call, token)));
        return own.Count == 0 ? null : new(own, guidance);
    }

    /// <summary>Changes the webcam background in the owner's Discord calls; null while that isn't wired.</summary>
    internal ICallCamera? CallCamera { get; set; }

    /// <summary>set_camera_background: a color or a kept picture at once; a new picture is drawn first (draw_picture's job) and
    /// used when it's ready.</summary>
    private async ValueTask<ConversationToolResult> SetCameraBackgroundAsync(LiveConversationOperation operation,
        LiveConversationConfiguration configured, ICallCamera camera, TextToolCall call, CancellationToken token)
    {
        var (arguments, problem) = CameraBackgroundTool.Parse(call.ArgumentsJson);
        if (arguments is null)
        {
            tools?.Record("Martlet", CameraBackgroundTool.Name, "invalid arguments", "", true);
            return new(problem!, true);
        }
        if (arguments.Draw is { } draw)
            return StartPicture(operation, configured, draw, CameraBackgroundTool.Name,
                (creation, later) => camera.SetBackgroundAsync(null, creation.Key, drawn: true, later));
        var result = await camera.SetBackgroundAsync(arguments.Color, arguments.Picture, drawn: false, token).ConfigureAwait(false);
        tools?.Record("Martlet", CameraBackgroundTool.Name, arguments.Color is { } color ? "color " + color : "picture", "", false);
        ErrorLog.Info($"Discord call: set_camera_background chose {(arguments.Color is { } chosen ? chosen.ToString().ToLowerInvariant() : "a picture")}.");
        return new(result);
    }

    /// <summary>Rings a Discord friend for call_on_discord; null while Discord isn't wired.</summary>
    internal IDiscordCaller? DiscordCaller { get; set; }

    private async ValueTask<ConversationToolResult> CallOnDiscordAsync(IDiscordCaller caller, TextToolCall call, CancellationToken token)
    {
        if (DiscordCallTool.Person(call.ArgumentsJson) is not { Length: > 0 } person)
            return new("Say which friend to call (person).", true);
        var result = await caller.CallAsync(person, token).ConfigureAwait(false);
        tools?.Record("Martlet", DiscordCallTool.Name, "called", "", false);
        ErrorLog.Info("Discord: call_on_discord ran.");
        return new(result);
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

    /// <summary>Brings what a check-in said to bring up into this conversation, like a due reminder (<see cref="CheckIns.SayKind"/>,
    /// with the Check-in: brought up prompts). Null when the conversation is closing or too many wait.</summary>
    internal BackgroundJob? BringUp(string label, string text)
    {
        var start = jobs.Start(CheckIns.SayKind, label.Length > 80 ? label[..80] + "…" : label,
            (_, _) => Task.FromResult(BackgroundJobOutcome.Done(text)));
        return start.Job;
    }

    /// <summary>The newest <paramref name="count"/> exchanges of this conversation (oldest first: what the user said and the reply)
    /// and how many it has had in all, a count that only grows, for a check-in. In memory only.</summary>
    internal (IReadOnlyList<CheckInExchange> Exchanges, long Total) RecentExchanges(int count)
    {
        lock (gate)
        {
            var messages = context.Snapshot();
            var exchanges = new List<CheckInExchange>();
            for (var i = 0; i + 1 < messages.Count; i += 2) exchanges.Add(new(messages[i].Text, messages[i + 1].Text));
            return ([.. exchanges.Skip(Math.Max(0, exchanges.Count - count))], context.Start + context.Count);
        }
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

    /// <summary>search_conversations: searches the record of earlier conversations (not this one, which the model has), the
    /// replies under <paramref name="companion"/>'s name.</summary>
    private async ValueTask<ConversationToolResult> SearchConversationsAsync(TextToolCall call, Guid conversation, string companion,
        CancellationToken token)
    {
        var (result, outcome) = await history!.SearchAsync(call, conversation, token, companion).ConfigureAwait(false);
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
        // Where it thinks (Companion › Thinking pool, this PC's choice): every member that can run a think (a model of its own; a
        // second model in Ollama on this PC only while both fit on the graphics card) and whose computer answers now; the
        // conversation model stands in while none answers, when that is allowed.
        var live = LivePool(configured);
        var plan = live.Plan;
        if (!plan.Available)
        {
            tools?.Record(server, ThinkLonger.Name, "not started: unavailable", ThinkLonger.Label(task!), false);
            ErrorLog.Info($"Background thinking: a new think wasn't started ({plan.Why})");
            return ValueTask.FromResult(new ConversationToolResult(ThinkLonger.Unavailable(plan.Why), true));
        }
        var toldUser = !string.IsNullOrWhiteSpace(operation.Turn?.Content.Text);
        var sent = operation.Sent;
        var thinkingModel = configured.Route(SetupRole.Llm).ModelId;
        // Members that are offline now stay in the list: the broker passes over them, and a think waiting in line goes to one as
        // soon as it answers again.
        var pool = Placing(DeepPool(configured), live);
        var places = ThinkLonger.Places(pool, BackgroundDuties.Of(dataDirectory), PoolCan, HostRouteGpus.For, Volatile.Read(ref thinkingPool));
        var thinkingRoute = configured.Routes.SingleOrDefault(r => r.Role == SetupRole.Llm);
        var start = jobs.Start(ThinkLonger.Kind(settings, ThinkLonger.Slots(places)), ThinkLonger.Label(task!), async (job, token) =>
        {
            // The place the broker picked for it: a free slot on the place sharing least with the conversation and kept free
            // for no other work, waiting in line for the first that frees up when every one is busy.
            var spot = pool.Find(job.Place!.Id)!;
            var place = spot.Settings;
            var attempts = 0;
            // The think on a place, going on from what it wrote when the live floor stopped it elsewhere (or there) before.
            BackgroundThink On(BackgroundPlace at, ThinkResume? resume)
            {
                var here = pool.Find(at.Id) ?? spot;
                var where = here.Settings.Separate ? here.Settings.Describe() : thinkingModel;
                var slot = ThinkSlotFor(here.Key);
                var think = new BackgroundThink(ThinkRuntime(slot),
                    left => PrepareThink(configured, here.Settings, sent, () => operation.Turn?.Content.Text, task!, reason, left,
                        own => Volatile.Write(ref slot.Authorization, own), resume), clock)
                {
                    AttemptFinished = terminal =>
                    {
                        attempts++;
                        NoteFallback("Background thinking", configured, terminal);
                        NoteInput("Background thinking", terminal, reply: false);
                        if (IsFailure(terminal) && terminal.State != ConversationState.Canceled)
                        {
                            if (here.Settings.Separate) ErrorLog.Warn($"Background thinking on {where} failed ({Describe(terminal)}).");
                            else LogReplyFailure("Background thinking", configured, terminal);
                            // Its computer didn't answer: offline at once, so the think (and the next job) goes elsewhere.
                            if (terminal.ProviderFailure == ProviderFailureCode.Network) NoteUnreachable(here.Settings, job.Id);
                        }
                    }
                };
                thinking[job.Id] = new(think, where, here.Computer, here.Plan);
                if (resume is not null)
                    ErrorLog.Info($"Background thinking: {job.Id} goes on on {where} (placed on {here.Computer}), " +
                        (resume.InPlace ? "continuing what it wrote." : "starting again with what it wrote as context."));
                return think;
            }
            using var guard = CancellationTokenSource.CreateLinkedTokenSource(token);
            var watch = Task.CompletedTask;
            string? pushed = null;
            try
            {
                if (spot.Plan.ChecksFit)
                {
                    // A second model in the same Ollama: it starts only when both fit on the graphics card (Thinking's loaded
                    // first, so its own size counts), and stops if loading it pushed Thinking's off the card after all.
                    job.Report(BackgroundJobState.Waiting, "checking it fits beside Thinking");
                    var fit = await LocalDeepThinking.CheckAsync(thinkingModel, place.ModelId!, loadThinking: true, token).ConfigureAwait(false);
                    ErrorLog.Info($"Background thinking: {job.Id} {(fit.Fits ? "can" : "can't")} run in Ollama on this PC beside Thinking. {fit.Why}");
                    if (!fit.Fits) return BackgroundJobOutcome.Failed(fit.Why.TrimEnd('.'));
                    watch = LocalDeepThinking.WatchAsync(thinkingModel, place.ModelId!, why =>
                    {
                        Volatile.Write(ref pushed, why);
                        guard.Cancel();
                    }, guard.Token);
                }
                // It yields to the live conversation: stopped where the conversation needs the hardware, it goes on later.
                return await YieldingThink.RunAsync(jobs, job, On,
                    at => pool.Find(at.Id) is { } next && ThinkLonger.ContinuesInPlace(next.Settings, thinkingRoute), guard.Token,
                    (at, kept) => ErrorLog.Info($"Background thinking: {job.Id} stopped on {at.Name} for the conversation" +
                        (kept is null ? "" : $", keeping the {kept.Partial.Length} characters it wrote") + "; it goes on later."),
                    (at, began) => HeldOnHost(pool, at, began), GoesOnElsewhere("Background thinking", job.Id)).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested && Volatile.Read(ref pushed) is { } pushedOut)
            {
                ErrorLog.Warn($"Background thinking: {job.Id} stopped. {pushedOut}");
                LocalDeepThinking.RecoverAsync(thinkingModel, place.ModelId!).Forget();
                return BackgroundJobOutcome.Failed(pushedOut.TrimEnd('.'));
            }
            finally
            {
                await guard.CancelAsync().ConfigureAwait(false);
                await watch.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                thinking.TryRemove(job.Id, out _);
                ErrorLog.Info($"Background thinking: {job.Id} ended on {job.Place?.Name ?? spot.Computer} after {BackgroundJobs.Duration(job.Elapsed)} " +
                    $"({attempts} request{(attempts == 1 ? "" : "s")}, alongside the conversation" +
                    (job.Preemptions > 0 ? $"; stopped {job.Preemptions} time{(job.Preemptions == 1 ? "" : "s")} for the conversation" : "") + ").");
            }
        }, places, wait: true);
        if (start.Job is not { } started)
        {
            tools?.Record(server, ThinkLonger.Name, "not started: " + start.Refusal, ThinkLonger.Label(task!), false);
            ErrorLog.Info($"Background thinking: a new think wasn't started ({start.Refusal}: {start.Message})");
            return ValueTask.FromResult(new ConversationToolResult(ThinkLonger.Refused(start), true));
        }
        var slots = ThinkLonger.Slots(places);
        var busy = jobs.Places.Leases.Count(lease => places.Any(p => p.Id == lease.Place.Id));
        var terms = $"thinking steps on, {settings.HowHard} effort, no time limit, " +
            $"{jobs.StartedWithinHour(ThinkLonger.KindName)} this hour (no hourly limit)";
        if (started.Place is not { } seat)
        {
            tools?.Record(server, ThinkLonger.Name, "queued " + started.Id, ThinkLonger.Label(task!), false);
            ErrorLog.Info($"Background thinking: {started.Id} waits in line ({jobs.Places.Position(started.Id)} in line) for one of " +
                $"{places.Count} place{(places.Count == 1 ? "" : "s")} ({ThinkLonger.AtOnce(places)} at once; " +
                (start.ForConversation ? "the live floor keeps them free for the conversation" : $"busy: {start.Queued}") + $"; {terms})" +
                (toldUser ? "." : " The reply hadn't told you yet, so it was asked to."));
            return ValueTask.FromResult(new ConversationToolResult(ThinkLonger.Started(started, toldUser, start.Queued, start.ForConversation)));
        }
        tools?.Record(server, ThinkLonger.Name, "started " + started.Id, ThinkLonger.Label(task!), false);
        var chosen = pool.Find(seat.Id)!;
        ErrorLog.Info($"Background thinking: started {started.Id} on {(chosen.Settings.Separate ? chosen.Settings.Describe() : thinkingModel)} " +
            $"(placed on {chosen.Computer}{(seat.Duties.Count > 0 ? $", also kept for {string.Join(" and ", seat.Duties)}" : "")}, " +
            $"{busy} of {slots} slot{(slots == 1 ? "" : "s")} on {places.Count} place{(places.Count == 1 ? "" : "s")} busy; {terms}; " +
            $"in parallel with the conversation: {chosen.Plan.Why})" +
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
        floor.Clear("the conversation ended");
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
        Func<string, CancellationToken, Task<LyricsWriter>>? writer = null;
        var where = "";
        if (arguments.Lyrics is null)
        {
            // The lyrics are written where Deep thinking thinks, alongside the conversation (never on Thinking's own model, so
            // replies never wait): on the free place that shares least with the conversation, else the least busy one, held while
            // it writes. Without a Deep thinking place, the reply writes them itself.
            var live = LivePool(configured);
            var plan = live.Plan;
            if (!plan.Available)
            {
                tools?.Record(server, SongTools.SingName, "not started: lyrics needed", label, false);
                return new(SongTools.WriteLyricsYourself(plan.Why), true);
            }
            var thinkingModel = configured.Route(SetupRole.Llm).ModelId;
            // Members that are offline now stay in the list: the broker passes over them until they answer again.
            var pool = Placing(DeepPool(configured), live);
            var places = ThinkLonger.Places(pool, BackgroundDuties.Of(dataDirectory), PoolCan, choices: Volatile.Read(ref thinkingPool));
            where = places.Count == 1
                ? pool.Usable[0].Settings is { Separate: true } only ? only.Describe() : thinkingModel
                : $"whichever of {places.Count} Thinking pool members is free";
            var task = SongTools.WritingTask(configured.Prompts, arguments);
            writer = async (holder, wait) =>
            {
                var runtime = SongRuntime();
                // The broker's choice, as for a think: waits in line while every place is busy.
                var lease = await jobs.Places.AcquireAsync(places, holder, wait, ThinkingDemand.For(ThinkingJobKind.ThinkLonger, places)).ConfigureAwait(false);
                var spot = pool.Find(lease.Place.Id)!;
                var place = spot.Settings;
                var at = place.Separate ? place.Describe() : thinkingModel;
                ErrorLog.Info($"Singing: {holder} writes its lyrics on {at} (placed on {spot.Computer}, " +
                    $"{jobs.Places.Load(lease.Place.Id)} on it now).");
                var think = new BackgroundThink(runtime,
                    left => PrepareThink(configured, place, sent, () => operation.Turn?.Content.Text, task, null, left,
                        own => Volatile.Write(ref songAuthorization, own)), clock)
                {
                    Doing = "Writing the lyrics",
                    AttemptFinished = terminal =>
                    {
                        NoteFallback("Song lyrics", configured, terminal);
                        NoteInput("Song lyrics", terminal, reply: false);
                        if (IsFailure(terminal) && terminal.State != ConversationState.Canceled)
                            ErrorLog.Warn($"Singing: writing the lyrics on {at} failed ({Describe(terminal)}).");
                    }
                };
                return new(think, spot.Plan.ChecksFit ? (thinkingModel, place.ModelId!) : null, lease);
            };
        }
        // Writing the lyrics may take the song's whole time limit (a think has none of its own).
        var writing = SongTools.Kind.TimeLimit!.Value;
        var author = new CreationAuthor
        {
            Device = HostSetupCommands.SuggestedDeviceId(), Computer = Environment.MachineName, Voice = setup.VoiceId,
            Persona = configured.Persona?.Name is { Length: > 0 } persona ? persona : null
        };
        var start = jobs.Start(SongTools.Kind, label, (job, token) =>
            MakeSongAsync(job, arguments, setup, library, author, writer, writing, token));
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
        CreationAuthor author, Func<string, CancellationToken, Task<LyricsWriter>>? writerFor, TimeSpan writing, CancellationToken token)
    {
        job.Report(BackgroundJobState.Running, "Checking the singing computer");
        var availability = await setup.Maker.GetAvailabilityAsync(token).ConfigureAwait(false);
        if (!availability.Available) return BackgroundJobOutcome.Failed(availability.Reason ?? "singing isn't available right now");
        // VevoSing chosen where only SoulX-Singer is set up (Companion > Singing offers Add VevoSing there): sing with SoulX.
        var voiceMatch = setup.VoiceMatch;
        if (!availability.VoiceMatches.Contains(voiceMatch))
        {
            ErrorLog.Info($"Singing: {voiceMatch} isn't set up on {availability.Host}; {job.Id} uses SoulX-Singer.");
            voiceMatch = SongVoiceMatch.SoulX;
        }
        WrittenSong? written;
        string? problem;
        if (writerFor is not null)
        {
            LyricsWriter seated;
            using (var patience = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                patience.CancelAfter(writing);
                job.Report(BackgroundJobState.Waiting, "waiting for a free computer to write the lyrics");
                try { seated = await writerFor(job.Id, patience.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    return BackgroundJobOutcome.Failed($"no computer came free to write the lyrics within {BackgroundJobs.Duration(writing)}");
                }
            }
            job.Report(BackgroundJobState.Running);
            var (writer, beside, place) = seated;
            using var held = place;
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
        // The singing computer is busy with this song until it is done (after the lyrics, which may be written there): no
        // background think is placed there meanwhile.
        using var singer = BackgroundDuties.Singer(dataDirectory) is { } computer ? jobs.Places.Hold(computer, job.Id) : null;
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

    // ---------- pictures (draw_picture) ----------

    /// <summary>draw_picture: starts the picture job where Companion › Pictures says and returns at once, telling the model to
    /// tell the user now unless it already did. The picture is kept as a creation and shown in the talk window when it's done.</summary>
    private ConversationToolResult DrawPicture(LiveConversationOperation operation, LiveConversationConfiguration configured, TextToolCall call)
    {
        const string server = "Martlet";
        var (arguments, problem) = PictureTools.Parse(call.ArgumentsJson);
        if (arguments is null)
        {
            tools?.Record(server, PictureTools.DrawName, "invalid arguments", "", true);
            return new(problem!, true);
        }
        return StartPicture(operation, configured, arguments, PictureTools.DrawName, then: null);
    }

    /// <summary>Starts a picture job for <paramref name="tool"/>; <paramref name="then"/> runs once the picture is kept (and its
    /// words are added to the note the conversation gets).</summary>
    private ConversationToolResult StartPicture(LiveConversationOperation operation, LiveConversationConfiguration configured,
        DrawArguments arguments, string tool, Func<Creation, CancellationToken, Task<string>>? then)
    {
        const string server = "Martlet";
        var maker = dataDirectory is null ? null
            : PictureClient.For(dataDirectory, configured.Profile, configured.Routes.SingleOrDefault(r => r.Role == SetupRole.Llm));
        if (maker is null)
        {
            tools?.Record(server, tool, "not started: pictures off", arguments.About, false);
            return new(PictureTools.Unavailable("pictures aren't set up (Companion › Pictures)"), true);
        }
        var toldUser = !string.IsNullOrWhiteSpace(operation.Turn?.Content.Text);
        var author = new CreationAuthor
        {
            Device = HostSetupCommands.SuggestedDeviceId(), Computer = Environment.MachineName,
            Persona = configured.Persona?.Name is { Length: > 0 } persona ? persona : null
        };
        var start = jobs.Start(PictureTools.Kind, arguments.About, (job, token) => MakePictureAsync(job, arguments, maker, author, then, token));
        if (start.Job is not { } started)
        {
            (maker as IDisposable)?.Dispose();
            tools?.Record(server, tool, "not started: " + start.Refusal, arguments.About, false);
            ErrorLog.Info($"Pictures: a new picture wasn't started ({start.Refusal}).");
            return new(PictureTools.Refused(start), true);
        }
        tools?.Record(server, tool, "started " + started.Id, arguments.About, false);
        ErrorLog.Info($"Pictures: started {started.Id} ({PictureShapes.Name(arguments.Shape)}, {arguments.Description.Length} characters) on {maker.Where}; " +
            $"{jobs.StartedWithinHour(PictureTools.KindName)} of {PictureTools.PerHour} this hour.");
        return new(PictureTools.Started(started, toldUser, maker.Where) + (then is null ? "" : CameraBackgroundTool.WillUse));
    }

    // The picture job: the place is checked, the picture drawn, kept as a creation (shared with every paired Martlet computer)
    // and shown in the talk window.
    private async Task<BackgroundJobOutcome> MakePictureAsync(BackgroundJob job, DrawArguments arguments, IPictureMaker maker,
        CreationAuthor author, Func<Creation, CancellationToken, Task<string>>? then, CancellationToken token)
    {
        try
        {
            // The pictures computer is busy with this picture until it is drawn: no background think is placed there meanwhile.
            using var painter = BackgroundDuties.Painter(dataDirectory) is { } computer ? jobs.Places.Hold(computer, job.Id) : null;
            job.Report(BackgroundJobState.Running, "Checking where it's drawn");
            var availability = await maker.GetAvailabilityAsync(token).ConfigureAwait(false);
            if (!availability.Available) return BackgroundJobOutcome.Failed((availability.Reason ?? "pictures aren't available right now").TrimEnd('.'));
            var request = PictureTools.Request(arguments);
            PictureResult result;
            try
            {
                result = await maker.GenerateAsync(request, new Progress<PictureProgress>(p => job.Report(BackgroundJobState.Running, p.Describe())), token)
                    .ConfigureAwait(false);
            }
            catch (PictureException error)
            {
                ErrorLog.Warn($"Pictures: {job.Id} failed on {maker.Where} ({error.Code}: {error.Message}).");
                return BackgroundJobOutcome.Failed(PictureClient.Problem(error));
            }
            job.Report(BackgroundJobState.Running, "Keeping the picture");
            Creation creation;
            try
            {
                creation = await CreationStore.AddAsync(dataDirectory!, PictureCreations.Draft(result, arguments.Title, arguments.About, request, author),
                    CreationRegistry.Shared, clock.GetUtcNow(), token).ConfigureAwait(false);
            }
            catch (Exception error) when (CreationStore.IsFailure(error))
            {
                ErrorLog.Warn($"Pictures: {job.Id} couldn't keep its picture ({error.Message}).");
                return BackgroundJobOutcome.Failed("there was no room to keep the picture on this PC");
            }
            ErrorLog.Info($"Pictures: {job.Id} drew {creation.Key} on {result.Where} ({result.Width}x{result.Height} {result.MediaType}, " +
                $"{result.Image.Length / 1024} KiB, {result.Model}{(result.Fixture ? ", FIXTURE - NOT AI" : "")}) in {result.Took.TotalSeconds:0.0} s.");
            PictureShown?.Invoke(new(creation.Key, creation.Title ?? arguments.Title, result.Image, result.Fixture));
            var used = then is null ? "" : " " + await then(creation, token).ConfigureAwait(false);
            return BackgroundJobOutcome.Done(PictureTools.Ready(creation.Key, creation.Title ?? arguments.Title, result) + used);
        }
        finally
        {
            PictureClient.FreeLater(maker);
            (maker as IDisposable)?.Dispose();
        }
    }

    /// <summary>The picture kind's handler for perform_creation: shows it in the talk window again.</summary>
    private async ValueTask<CreationActionResult> ShowPictureCreationAsync(CreationAction action, CancellationToken token)
    {
        var (image, _, problem) = await PictureCreations.LoadAsync(action.Creation, action.Assets, token).ConfigureAwait(false);
        if (image is null) return new($"{problem} Say you'll show it in a moment.", true);
        var title = action.Creation.Title ?? "a picture";
        PictureShown?.Invoke(new(action.Creation.Key, title, image, PictureCreations.Metadata(action.Creation)?.Fixture == true));
        return new(PictureTools.Shown(title));
    }

    /// <summary>The talk window stops the song: Stop and Esc quickly (a 300 ms fade), the talk button musically. The note says
    /// which.</summary>
    internal SongStopRecord? StopSong(bool musical, string button) =>
        singing?.Stop(SongStopCause.Button, musical, reason: button);

    // A background think's request: the reply that called think_longer (as said so far) continued and fitted to the place it
    // thinks on (<paramref name="deep"/>, a single place), with its own authorization bound to exactly this request and the time
    // left, handed to <paramref name="bind"/> (the place's slot, or the song job's for a song's lyrics). <paramref name="resume"/>:
    // what it wrote before the live floor stopped it, continued in place or sent again as context.
    private (ConversationRequest, IConversationAuthorizationSource) PrepareThink(LiveConversationConfiguration configured,
        DeepThinkingSettings deep, BoundedTextInput? sent, Func<string?> reply, string task, string? reason, TimeSpan left,
        Action<ICredentialAuthority> bind, ThinkResume? resume = null)
    {
        var full = ThinkLonger.Input(sent, reply(), task, reason, configured.Prompts, sent?.Personality, resume);
        var continuing = resume is { InPlace: true };
        var effort = configured.ThinkLonger.HowHard;
        if (deep.Separate)
        {
            var target = DeepThinkTarget.For(deep.Single, effort, configured.Routes.SingleOrDefault(r => r.Role == SetupRole.Llm),
                ModelLimits.Load(dataDirectory));
            var separate = target.Request(ThinkLonger.Fit(full, target.Bounds), effort, left, continuing);
            var own = new DeepThinkAuthorization(target, separate, configured.Profile,
                configured.Routes.SingleOrDefault(r => r.Role == SetupRole.Llm), vault, clock, clock.GetUtcNow() + left + TimeSpan.FromSeconds(5));
            bind(own);
            return (separate, own);
        }
        // With the Thinking model: within the reply's own bounds, its tools kept so the start is the reply's.
        var input = configured.FitsContext(full) ? full : ThinkLonger.Fit(full, new(configured.TextLimits.MaxInputBytes,
            configured.TextLimits.MaxHistoryMessages, configured.TextInputTokens, Tools: false));
        bool withoutReasoning;
        lock (gate) withoutReasoning = reasoningRefused.Contains(configured.ToolModelKey());
        var request = configured.ThinkRequest(input, left, withoutReasoning, continuing);
        var authorization = new ConversationAuthorization(configured, voice: false, microphone: false, clock, () => true,
            settings.LoadAsync, vault, CancellationToken.None, textLimits: request.TextLimits, lifetime: left + TimeSpan.FromSeconds(5));
        // A tool round (declined) and a retry without Thinking steps may each take one more request.
        authorization.BindInput(request.Input, request.Limits.MaxToolRounds + 1);
        bind(authorization);
        return (request, authorization);
    }

    // The slot of the place with key <paramref name="place"/>: its own runtime and credentials, made on first use.
    private ThinkSlot ThinkSlotFor(string place)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!thinkSlots.TryGetValue(place, out var slot))
            {
                slot = new ThinkSlot();
                var bound = slot;
                slot.Credentials = new(() => Volatile.Read(ref bound.Authorization));
                thinkSlots[place] = slot;
            }
            return slot;
        }
    }

    private ConversationRuntime ThinkRuntime(ThinkSlot slot)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return slot.Runtime ??= runtimeFactory?.Invoke(slot.Credentials!, clock) ??
                ConversationRuntime.Create(slot.Credentials!, clock: clock, hostText: new HostTextClient());
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
    internal LiveConversationOperation? StartReport(bool voice, bool noticesOnly = false, SeenScreen? seen = null,
        AttentionSignal? attention = null, bool look = false)
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
                // While vision is on, the newest picture goes with it too (or the image model's description of it), so the report
                // sees what the user is doing.
                var imagePath = SenseRoute(SenseKind.Image, selected).Path;
                if (imagePath == SensePath.None) seen = null;
                long acceptedRevision = revision = checked(revision + 1);
                var authorization = new ConversationAuthorization(selected, voice, false, clock,
                    () => Volatile.Read(ref revision) == acceptedRevision, settings.LoadAsync, vault, CancellationToken.None,
                    screen: seen is not null && imagePath == SensePath.Thinking);
                operation = new(authorization, CancellationToken.None)
                {
                    Report = true, Delivery = delivery, Spoken = lastAsked.Spoken, BackgroundChattiness = lastAsked.Chattiness,
                    Seen = seen, Attention = seen is null ? null : attention, Look = look, ImagePath = imagePath
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

    /// <summary>Starts a short reply Martlet gives on its own because the user touched, stroked or moved the desktop character and
    /// said nothing (the talk window decides when: <see cref="Martlet.Conversation.TouchDebounce"/>). Its message is Martlet's note
    /// with the touches (Companion › Prompts › Touched), which asks for words out loud; it starts like the reply before it (same
    /// instructions and tools), takes nothing else, and the conversation keeps only the short touch line. Null when nothing that
    /// starts a reply waits.</summary>
    internal LiveConversationOperation? StartTouch(bool voice)
    {
        var published = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        LiveConversationOperation operation;
        Martlet.Conversation.TouchBurst? burst = null;
        try
        {
            lock (gate)
            {
                if (disposed || paused || muted || locked) throw new LiveActionException("conversation.controls_blocked");
                if (operations.IsRunning) throw new LiveActionException("conversation.ownership_busy");
                var selected = configuration ?? throw new LiveActionException("conversation.setup_required");
                if (selected.Unavailable(voice, false) is not null) throw new LiveActionException("conversation.configuration_unsupported");
                if (touches.Peek(TouchNow) is not { StartsTurn: true }) return null;
                burst = touches.Drain(TouchNow)!;
                var input = new BoundedTextInput(PromptSettings.Fill(selected.Prompts, PromptCatalog.Touched,
                    ("touches", LiveConversationConfiguration.TouchWords(selected.Prompts, burst)),
                    ("silent", LiveConversationConfiguration.SilentReply)) ?? burst.Line);
                long acceptedRevision = revision = checked(revision + 1);
                var authorization = new ConversationAuthorization(selected, voice, false, clock,
                    () => Volatile.Read(ref revision) == acceptedRevision, settings.LoadAsync, vault, CancellationToken.None);
                operation = new(authorization, CancellationToken.None)
                {
                    Touch = true, Touches = burst, Spoken = lastAsked.Spoken, BackgroundChattiness = lastAsked.Chattiness,
                    LatencyTimeline = new ReplyTimeline(clock, ReplyTimeline.YouSent)
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
            if (burst is not null) touches.Restore(burst);
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
            id = job.Id, kind = job.Kind.Name, state = job.State.ToString(), progress = job.Progress, place = job.Place?.Name,
            inLine = jobs.Places.Position(job.Id),
            startedAt = job.StartedUtc, finishedAt = job.FinishedUtc, elapsedSeconds = Math.Round(job.Elapsed.TotalSeconds, 1),
            timeLimitSeconds = job.Kind.TimeLimit?.TotalSeconds, offer = job.Kind.Offer,
            resultCharacters = job.Result?.Length, cut = job.Cut, problem = job.Problem, canceledBy = job.CanceledBy,
            delivery = job.Delivery.ToString()
        };
        var running = thinking.ToArray().OrderBy(pair => pair.Key, StringComparer.Ordinal).ToArray();
        object Think(KeyValuePair<string, RunningThink> pair) => new
        {
            id = pair.Key, where = pair.Value.Where, computer = pair.Value.Computer, available = pair.Value.Plan.Available,
            checksFit = pair.Value.Plan.ChecksFit, why = pair.Value.Plan.Why, rank = pair.Value.Plan.Rank, parallel = true,
            attempts = pair.Value.Think.Attempts
        };
        var configured = Configuration;
        // Whether each place can take a think now: a member whose computer is offline can't until it answers again.
        var pool = configured is null ? null : LivePool(configured);
        return System.Text.Json.JsonSerializer.Serialize(new
        {
            updatedAt = clock.GetUtcNow(),
            active = jobs.Active.Select(Describe),
            recent = jobs.Recent.Select(Describe),
            startedLastHour = new { think = jobs.StartedWithinHour(ThinkLonger.KindName), song = jobs.StartedWithinHour(SongTools.KindName),
                picture = jobs.StartedWithinHour(PictureTools.KindName) },
            thinking = running.Length == 0 ? null : Think(running[0]),
            thinks = running.Select(Think),
            // Where Deep thinking can think (names only) and what holds each place now.
            places = pool?.Spots.Select(spot => new
            {
                computer = spot.Computer, where = spot.Settings.Separate ? spot.Settings.Describe() : "the Thinking model",
                available = spot.Plan.Available, offline = spot.Plan.Offline, rank = spot.Plan.Rank, slots = spot.Settings.ThinksAtOnce,
                heldBy = jobs.Places.Leases.Where(lease => lease.Place.Id == spot.Key).Select(lease => lease.Holder)
            }),
            maxThinks = pool is null ? 0 : ThinkLonger.Slots(ThinkLonger.Places(pool)),
            // Who waits for a free place now, first in line first, and the other work each computer is kept free for.
            line = jobs.Places.Line,
            keptFor = BackgroundDuties.Of(dataDirectory)
        });
    }

    // Speech-to-text for one recorded utterance through its one-use upload permission; null (with the reason published) when
    // nothing usable came back.
    private async Task<TranscriptionResult?> TranscribeAsync(LiveConversationOperation operation, BoundedWaveAudio audio,
        OpenAiTranscriptionAdapter openAi, CancellationToken worker)
    {
        operation.Authorization.Check(worker);
        await operation.Authorization.ValidateSettingsAsync(worker).ConfigureAwait(false);
        var stt = operation.Authorization.Configuration.Route(SetupRole.Stt);
        // Listening's own route failed moments ago: Parakeet on this PC hears this turn at once, instead of waiting for the route
        // to fail again (a computer that is off takes seconds to time out). The route is asked again after StandInFor.
        if (RouteFailedAgo(stt) is { } ago && StandInModel(stt) is { } standIn)
        {
            operation.Publish(new("stt.uploading"));
            return await StandInAsync(operation, audio, null, standIn, worker, ago).ConfigureAwait(false);
        }
        var context = new ProviderRequestContext
        {
            Ids = Ids(), Epoch = operation.Capture!.Snapshot.Epoch,
            Deadline = operation.Authorization.Deadline(TimeSpan.FromSeconds(30))
        };
        var permission = operation.Authorization.AuthorizeAudio(context);
        operation.Publish(new("stt.uploading"));
        operation.BeginTranscription(clock, context.Deadline);
        TranscriptionResult result;
        // The end-of-turn judge's quick transcript of exactly this speech (Parakeet on this PC) is used instead of a second one.
        var quick = operation.QuickWords;
        operation.QuickWords = null;
        ReusedWords? reused = null;
        try
        {
            // Listening handed to a paired host: the utterance goes only to its pinned gateway.
            result = operation.Authorization.Configuration.SttHostTarget() is { } listener
                ? await hostTranscription.TranscribeAsync(context, listener, stt.ModelId, audio,
                    LiveConversationConfiguration.TranscriptionLimits, permission, operation.OriginalCaller, worker).ConfigureAwait(false)
                // Parakeet on this PC: transcribed in memory here, nothing is sent anywhere.
                : operation.Authorization.Configuration.LocalStt()
                    ? await (quick is not null && quick.ModelId == stt.ModelId && localWords is not null
                            ? new LocalTranscriptionAdapter(reused = new ReusedWords(quick.Transcript, localWords), clock)
                            : localTranscription ?? throw new LiveActionException("conversation.configuration_unsupported"))
                        .TranscribeAsync(context, stt.ModelId, audio, LiveConversationConfiguration.TranscriptionLimits, permission,
                            operation.OriginalCaller, worker).ConfigureAwait(false)
                : await openAi.TranscribeAsync(context, stt.ModelId, audio,
                    LiveConversationConfiguration.TranscriptionLimits, permission, operation.OriginalCaller, worker).ConfigureAwait(false);
        }
        finally
        {
            operation.EndTranscription();
            if (quick is not null) CryptographicOperations.ZeroMemory(quick.Pcm);
        }
        if (reused?.Reused == true)
            ErrorLog.Info($"End of turn: speech-to-text reused the quick transcript started {clock.GetElapsedTime(quick!.StartedAt).TotalMilliseconds:0} ms ago (no second transcription).");
        operation.Authorization.Check(worker);
        // Listening's own route (a paired host or OpenAI) failed: Parakeet on this PC's processor hears the same utterance, still
        // in memory, when a model is downloaded here. A route that answers is never held up by it.
        if (result.Outcome is TranscriptionOutcome.Failed or TranscriptionOutcome.DeadlineExceeded &&
            result.Failure?.Code != ProviderFailureCode.AudioLimit && StandInModel(stt) is { } model)
            return await StandInAsync(operation, audio, result, model, worker).ConfigureAwait(false);
        operation.Transcription = result;
        // The route answered: turns ask it first again.
        if (result.Outcome is TranscriptionOutcome.Completed or TranscriptionOutcome.NoSpeech) lock (gate) routeFailed = null;
        if (result.Outcome == TranscriptionOutcome.Completed)
        {
            Succeeded(SetupRole.Stt);
            return result;
        }
        if (result.Outcome != TranscriptionOutcome.NoSpeech)
            LogFailure("Transcription", operation.Authorization.Configuration, SetupRole.Stt, Outcome(result));
        operation.Publish(new("stt." + result.Outcome, Finished: true, ProviderFailure: result.Failure?.Code));
        return null;
    }

    /// <summary>How long Listening's stand-in hears every turn at once after Listening's own route failed, before the route is
    /// asked again.</summary>
    internal static readonly TimeSpan StandInFor = TimeSpan.FromSeconds(60);
    // Listening's own route as it was when it last failed, and when (controller clock); null once it answers.
    private (SetupRoute Route, long At)? routeFailed;

    /// <summary>How long ago <paramref name="stt"/> failed, while that is less than <see cref="StandInFor"/>; null otherwise.</summary>
    private TimeSpan? RouteFailedAgo(SetupRoute stt)
    {
        lock (gate)
        {
            if (routeFailed is not { } failed || failed.Route != stt) return null;
            var ago = clock.GetElapsedTime(failed.At);
            return ago < StandInFor ? ago : null;
        }
    }

    /// <summary>The Parakeet model on this PC that hears an utterance in place of <paramref name="stt"/>, or null. Asked only once
    /// the route failed, so a route that answers costs nothing more.</summary>
    private string? StandInModel(SetupRoute stt) => localTranscription is null ? null : listeningStandIn?.Invoke(stt);

    /// <summary>Listening's own route failed (<paramref name="failed"/>), or failed <paramref name="ago"/> and isn't asked again
    /// yet (<paramref name="failed"/> null): Parakeet <paramref name="model"/> on this PC's processor hears the utterance with its
    /// own local-only permission, so the turn goes on and nothing is sent anywhere. The route's failure stays on Home until the
    /// route answers again. Null (with the reason published) when nothing usable came back; a stand-in that fails after the
    /// route did publishes the route's own failure.</summary>
    private async Task<TranscriptionResult?> StandInAsync(LiveConversationOperation operation, BoundedWaveAudio audio,
        TranscriptionResult? failed, string model, CancellationToken worker, TimeSpan? ago = null)
    {
        var context = new ProviderRequestContext
        {
            Ids = Ids(), Epoch = operation.Capture!.Snapshot.Epoch,
            Deadline = operation.Authorization.Deadline(TimeSpan.FromSeconds(30))
        };
        var permission = operation.Authorization.AuthorizeStandIn(context, model);
        operation.StandIn = model;
        operation.BeginTranscription(clock, context.Deadline);
        var started = clock.GetTimestamp();
        TranscriptionResult heard;
        try
        {
            heard = await localTranscription!.TranscribeAsync(context, model, audio, LiveConversationConfiguration.TranscriptionLimits,
                permission, operation.OriginalCaller, worker).ConfigureAwait(false);
        }
        finally { operation.EndTranscription(); }
        var took = $"{clock.GetElapsedTime(started).TotalMilliseconds:0} ms";
        var stoodIn = heard.Outcome is TranscriptionOutcome.Completed or TranscriptionOutcome.NoSpeech;
        // Later turns skip the route only while the stand-in hears them; one that fails sends them back to the route.
        if (stoodIn && failed is not null)
            lock (gate) routeFailed = (operation.Authorization.Configuration.Route(SetupRole.Stt), started);
        else if (!stoodIn && heard.Outcome != TranscriptionOutcome.Canceled)
            lock (gate) routeFailed = null;
        var standIn = stoodIn ? $"Parakeet {model} on this PC heard it instead in {took}"
            : heard.Outcome == TranscriptionOutcome.Canceled ? null
            : $"Parakeet {model} on this PC couldn't hear it either ({Outcome(heard)}, {took})";
        if (failed is not null)
            LogFailure("Transcription", operation.Authorization.Configuration, SetupRole.Stt, Outcome(failed) + (standIn is null ? "" : "; " + standIn));
        else if (standIn is not null)
        {
            var line = $"Listening: {standIn}, without asking Listening's own route: it failed {ago!.Value.TotalSeconds:0} s ago " +
                $"and is asked again {StandInFor.TotalSeconds:0} s after that.";
            if (stoodIn) ErrorLog.Info(line);
            else ErrorLog.Warn(line);
        }
        operation.Authorization.Check(worker);
        var result = failed is not null && heard.Outcome is TranscriptionOutcome.Failed or TranscriptionOutcome.DeadlineExceeded ? failed : heard;
        operation.Transcription = result;
        if (result.Outcome == TranscriptionOutcome.Completed)
        {
            // The reply latency line names the model that heard the turn.
            if (operation.LatencyTimeline is { } timeline) timeline.StandIn = model;
            return result;
        }
        operation.Publish(new("stt." + result.Outcome, Finished: true, ProviderFailure: result.Failure?.Code));
        return null;
    }

    private static string Outcome(TranscriptionResult result) =>
        $"outcome {result.Outcome}" + (result.Failure?.Code is { } code ? $", provider {code}" : "");

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
            LiveConversationConfiguration.WithoutMarked(earlier.LastOrDefault(message => message.Role == TextHistoryRole.User)?.Text),
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
                remember ? MemoryPeople.Labels(known!, roster) : null, job.Configuration.CharacterName);
            var purpose = remember && job.Heard is not null ? "Remembering and learning names" : remember ? "Remembering" : "Learning names";
            // A Thinking pool member reads the short excerpt (it has no copy of this conversation in its cache); the conversation's
            // own model continues the reply's request as before, after the reply finished speaking.
            AfterReplyPrompt? pooled = null;
            var (answer, failure, onPool) = await helpers.RunAsync(HelperJobKind.Memory, purpose, HelperCapability.Text,
                () => (pooled = AfterReply.Prompt(remember ? known : null, naming, job.EarlierUser, job.EarlierReply, job.User, job.Reply,
                    job.Configuration.Prompts, null, null, remember ? job.Present : null,
                    remember ? MemoryPeople.Labels(known!, roster) : null, job.Configuration.CharacterName)).Input,
                worker => AskAsync(purpose, job.Configuration, prompt.Input, worker), token).ConfigureAwait(false);
            if (onPool) prompt = pooled!;
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
        BoundedTextInput input, CancellationToken token, bool picture = false)
    {
        var request = configuration.MemoryCaptureRequest(input);
        var capture = CaptureRuntime();
        var authorization = new ConversationAuthorization(configuration, voice: false, microphone: false, clock,
            () => !token.IsCancellationRequested, settings.LoadAsync, vault, token, screen: picture);
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

    /// <summary>A helper job outside a conversation (naming a character's emotes, finding its touch zones): on a Thinking pool
    /// member that can take it, else on the saved Thinking model (<see cref="AskThinkingAsync(string, string, string, BoundedImage?, CancellationToken)"/>)
    /// once no reply runs or speaks. A job with a picture goes to the image model of its own instead of Thinking while pictures go
    /// to it (<see cref="AskImageModelAsync"/>, docs/SENSE_MODELS.md).</summary>
    internal async Task<(string? Answer, string? Failure)> AskHelperAsync(HelperJobKind kind, string purpose, string instructions,
        string text, BoundedImage? image, CancellationToken token)
    {
        var (answer, failure, _) = await helpers.RunAsync(kind, purpose, image is null ? HelperCapability.Text : HelperCapability.Vision,
            () => new BoundedTextInput(text, instructions, image: image),
            worker => image is not null && SenseRoute(SenseKind.Image).Described
                ? AskImageModelAsync(kind, purpose, instructions, text, image, worker)
                : AskThinkingAsync(purpose, instructions, text, image, worker), token).ConfigureAwait(false);
        return (answer, failure);
    }

    /// <summary>One request to the saved Thinking model outside a conversation (naming a character's emotes, finding its touch
    /// zones): <paramref name="instructions"/> and <paramref name="text"/> (and <paramref name="image"/>, which the owner chose to
    /// send) go to it on the background runtime. Returns its answer, or null with why not (Thinking isn't set up, can't see a
    /// picture, or the request failed).</summary>
    internal async Task<(string? Answer, string? Failure)> AskThinkingAsync(string purpose, string instructions, string text,
        CancellationToken token) => await AskThinkingAsync(purpose, instructions, text, null, token).ConfigureAwait(false);

    internal async Task<(string? Answer, string? Failure)> AskThinkingAsync(string purpose, string instructions, string text,
        BoundedImage? image, CancellationToken token)
    {
        var loaded = await settings.LoadAsync(token).ConfigureAwait(false);
        if (LiveConversationConfiguration.From(loaded, ModelLimits.Load(dataDirectory)) is not { } configured)
            return (null, "Thinking isn't set up yet");
        if (image is not null && configured.Vision() == VisionSupport.Unsupported) return (null, configured.VisionAdvice());
        BoundedTextInput input;
        try { input = new(text, instructions, image: image); }
        catch (ContractException) { return (null, "the request is too large"); }
        try { return await AskAsync(purpose, configured, input, token, picture: image is not null).ConfigureAwait(false); }
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
        // With the end-of-turn judge, the detector itself only ends speech after the longer pause for unfinished speech; the
        // gate ends it sooner when the judge says the turn is complete, and at the plain pause otherwise.
        var judge = TurnJudge(operation);
        var turn = judge is null ? null : new EndOfTurnGate(settings.EndSilence, EndOfTurn);
        var detector = new EnergyVoiceActivityDetector(turn is null ? settings : settings with { EndSilence = turn.DetectorEndSilence });
        Task<(EndOfTurnJudgement? Judgement, Exception? Error, long At)>? judging = null;
        using var judgeCancel = CancellationTokenSource.CreateLinkedTokenSource(operation.OriginalCaller);
        QuickWords? quick = null;
        CancellationTokenSource? quickCancel = null;
        int judgedPause = 0, quiet = 0;
        long judgeStartedAt = 0, judgeEndedAt = 0;
        EndOfTurnJudgement? judgement = null;
        Exception? judgeError = null;
        var decided = EndOfTurnStep.Wait;
        // Companion › Listening › Start replies early: with Parakeet on this PC, a reply starts early on the quick transcript of
        // the pause under way (without a judge, the quick transcript still starts at the same short pause) and is let go when
        // the user's own voice comes back. Pauses are counted here for both.
        var early = EarlyGateFor(operation);
        LiveConversationOperation? earlyReply = null;
        int pauses = 0, quickPause = -1, quickEnd = 0;
        bool quickTried = false, earlyKept = false;
        bool? quickWorth = null;
        var quickFrames = Math.Max(1, (int)Math.Ceiling(EndOfTurn.JudgeAfter.TotalMilliseconds / 20));
        var plainFrames = Math.Max(1, (int)Math.Round(settings.EndSilence.TotalMilliseconds / 20));
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
                    var silenceBefore = detector.SilenceFrames;
                    var transition = detector.Process(frame);
                    if (transition == VoiceActivityTransition.None && silenceBefore == 0 && detector.SilenceFrames > 0) pauses++;
                    operation.VoiceLevel = detector.LastLevelDb;
                    var loud = detector.LastFrameLoud;
                    userSum.Add(userSum[^1] + (loud && !speakers ? 1 : 0));
                    speakerSum.Add(speakerSum[^1] + (loud && speakers ? 1 : 0));
                    explainedSum.Add(explainedSum[^1] + (speakers ? 1 : 0));
                    talkOver?.Process(loud, speakers);
                    // The user's own voice (never the speakers' sound or what this PC plays) puts the live floor at Listening.
                    if (loud && !speakers && !operation.Listening.Pc) floor.Heard();
                    // Paused for what was said over Martlet: how long the user talks on decides too (BargeInHold).
                    operation.Held?.Hold.Frame(loud && !speakers);
                    // Talking over Martlet: once the voice has gone on long enough (or a short word just ended), what was said so
                    // far is checked for words without waiting for the pause.
                    if (bargeIn.Gate?.Process(loud, speakers, checking is { IsCompleted: false }) == true && !operation.TalkingOver &&
                        Speaking is { } mode && (operation.Listening.BargeIn || mode == PlaybackMode.Song))
                        checking = CheckWordsAsync(operation, run, bargeIn.Gate, bargeIn.Check, index, mode);
                    if (turn is not null && transition == VoiceActivityTransition.None && detector.Speaking)
                    {
                        TakeJudgement();
                        var step = turn.Step(detector.SilenceFrames, accepted >= 0);
                        if (turn.WentOn is { } unfinished)
                        {
                            // The judge was right that it wasn't over: the next pause is judged again.
                            RecordTurn(new(clock.GetUtcNow(), EndOfTurnDecision.WentOn, TimeSpan.FromMilliseconds(quiet * 20),
                                settings.EndSilence, Took(), unfinished.Probability, judge!.Name));
                            DropQuick();
                        }
                        quiet = detector.SilenceFrames;
                        if (step == EndOfTurnStep.Judge) AskJudge();
                        else if (step is EndOfTurnStep.Complete or EndOfTurnStep.Fallback && detector.EndSpeech())
                        {
                            decided = step;
                            transition = VoiceActivityTransition.SpeechEnded;
                        }
                    }
                    if (early is not null && transition == VoiceActivityTransition.None && detector.Speaking && accepted >= 0)
                    {
                        if (detector.SilenceFrames == 0)
                        {
                            // The user's own voice came back in the pause (never what the speakers played): the reply started
                            // early goes, and the next pause may start another.
                            if (silenceBefore > 0 && !speakers && early.VoiceResumed() && earlyReply is { } resumed)
                                LetGoEarly(resumed, EarlyReplyRecord.Cancelled, "you went on talking");
                        }
                        else
                        {
                            // Without a judge the quick transcript starts at the same short pause: a reply may start early on
                            // it, and speech-to-text reuses it when the turn ends in this pause.
                            if (turn is null && detector.SilenceFrames == quickFrames && quickFrames < plainFrames) QuickAtPause();
                            if (!quickTried && quick is { Transcript.IsCompleted: true } && quickPause == pauses) TryEarly();
                        }
                    }
                    if (transition == VoiceActivityTransition.SpeechStarted)
                    {
                        if (accepted < 0) operation.Publish(new("mic.hearing_speech"));
                    }
                    else if (transition == VoiceActivityTransition.SpeechEnded)
                    {
                        // A cough or click, or what the speakers played, is ignored; keep listening for real speech.
                        if (accepted < 0 && (detector.SpeechEndFrame - detector.SpeechStartFrame < minimumFrames || !Voice()))
                        {
                            decided = EndOfTurnStep.Wait;
                            operation.Publish(new("mic.listening"));
                            continue;
                        }
                        if (accepted < 0) Accept();
                        operation.Hearing = true;
                        if (!operation.Listening.Pc)
                        {
                            // The speech ended where the silence began; the detector noticed after the end-of-speech pause, or the
                            // end-of-turn judge decided sooner.
                            var now = clock.GetTimestamp();
                            var silence = (long)((index - detector.SpeechEndFrame) * 0.02 * clock.TimestampFrequency);
                            operation.LatencyTimeline = new ReplyTimeline(clock, ReplyTimeline.YouStopped, now - Math.Max(0, silence));
                            if (turn is not null) TurnEnded(now, silence);
                            else operation.LatencyTimeline.Mark(ReplyLatency.EndOfSpeech, now);
                            if (early is not null) EarlyTurnEnded();
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
                // A quick transcript that came between frames, or a start that found the conversation busy a moment ago (the
                // reply let go before it still leaving), starts its reply early now rather than at the next frame.
                if (early is not null && !quickTried && quick is { Transcript.IsCompleted: true } && quickPause == pauses &&
                    detector is { Speaking: true, SilenceFrames: > 0 } && accepted >= 0)
                    TryEarly();
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
                // The judge's answer is acted on at the next frame, so wake for it as well.
                await (judging is { IsCompleted: false } pending
                    ? Task.WhenAny(run.Completion, pending, Task.Delay(TimeSpan.FromMilliseconds(20), clock))
                    : Task.WhenAny(run.Completion, Task.Delay(TimeSpan.FromMilliseconds(20), clock))).ConfigureAwait(false);
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
            judgeCancel.Cancel();
            if (!ReferenceEquals(operation.QuickWords, quick)) DropQuick();
            // Listening stopped (or the turn ended in another pause) before a reply started early was kept for this turn.
            if (earlyReply is { } left && !earlyKept) LetGoEarly(left, EarlyReplyRecord.Cancelled, "your turn didn't end in that pause");
            // The user's voice ended (or listening stopped): a paused reply plays on once its verdict is not for Martlet.
            operation.Held?.Hold.Ended();
        }

        // The end-of-turn judge's answer, once it is in, for the pause it was asked about.
        void TakeJudgement()
        {
            if (judging is not { IsCompleted: true } done) return;
            judging = null;
            (judgement, judgeError, judgeEndedAt) = done.Result;
            if (!turn!.Judged(judgedPause, judgeError is null ? judgement : null))
            {
                judgement = null;
                judgeError = null;
            }
        }

        TimeSpan? Took() => judgeEndedAt > judgeStartedAt && judgeStartedAt != 0
            ? clock.GetElapsedTime(judgeStartedAt, judgeEndedAt) : null;

        // What was said so far, from its pre-roll to now (with the pause), and where in it the speech that would be kept lies
        // (pre-roll to the pause, with the tail). The caller clears it.
        (byte[] Heard, int Offset, int Length, int Kept) HeardSoFar()
        {
            var end = index - detector.SilenceFrames;
            var startSample = Math.Max(0, accepted * EnergyVoiceActivityDetector.FrameSamples - EnergyVoiceActivityDetector.Samples(settings.PreRoll));
            var endSample = end * EnergyVoiceActivityDetector.FrameSamples + EnergyVoiceActivityDetector.Samples(settings.Tail);
            var firstFrame = startSample / EnergyVoiceActivityDetector.FrameSamples;
            var heard = new byte[(index - firstFrame) * EnergyVoiceActivityDetector.FrameBytes];
            var frames = 0;
            try
            {
                for (var at = firstFrame; at < index; at++, frames++)
                    if (!run.TryCopyMonoFrame(at, heard.AsSpan(frames * EnergyVoiceActivityDetector.FrameBytes, EnergyVoiceActivityDetector.FrameBytes)))
                        break;
            }
            catch (OperationCanceledException) { }
            var offset = (startSample - firstFrame * EnergyVoiceActivityDetector.FrameSamples) * 2;
            return (heard, offset, frames * EnergyVoiceActivityDetector.FrameBytes - offset, (endSample - startSample) * 2);
        }

        // With Parakeet on this PC, a quick transcript of exactly the speech that would be kept starts now: speech-to-text reuses
        // it when the turn ends in this pause, and a reply may start early on it.
        void StartQuick(byte[] heard, int offset, int length, int keptBytes, long at)
        {
            var configured = operation.Authorization.Configuration;
            if (localWords is not { } local || !configured.LocalStt() || configured.SttHostTarget() is not null || length < keptBytes || keptBytes <= 0)
                return;
            var kept = heard.AsSpan(offset, keptBytes).ToArray();
            var model = configured.Route(SetupRole.Stt).ModelId;
            quickCancel = CancellationTokenSource.CreateLinkedTokenSource(operation.OriginalCaller);
            Task<LocalTranscript> transcript;
            try { transcript = local.TranscribeAsync(model, kept, quickCancel.Token); }
            catch (Exception error) when (error is InvalidOperationException or ObjectDisposedException) { transcript = Task.FromException<LocalTranscript>(error); }
            quick = new(model, kept, transcript, at);
            // Real words in it make the live floor Live before the turn ends (docs/CONVERSATION.md, Live floor).
            if (operation.Listening?.Pc != true) FloorWords(operation, transcript);
            quickPause = pauses;
            quickEnd = index - detector.SilenceFrames;
            quickTried = false;
            quickWorth = null;
        }

        // Without a judge (off, or none can answer), the quick transcript still starts at the short pause the judge would be
        // asked at, for a reply started early.
        void QuickAtPause()
        {
            DropQuick();
            var (heard, offset, length, kept) = HeardSoFar();
            try { StartQuick(heard, offset, length, kept, clock.GetTimestamp()); }
            finally { CryptographicOperations.ZeroMemory(heard); }
        }

        // The quick transcript of the pause under way is in: a reply starts early when it has real words and the conversation
        // is free (StartEarly decides the rest; a busy slot, say the reply let go a moment ago, is tried again at the next
        // frame).
        void TryEarly()
        {
            if (quick!.Transcript is not { IsCompletedSuccessfully: true } done)
            {
                quickTried = true;
                return;
            }
            var text = done.Result.Text?.Trim() ?? "";
            var (voiced, speech) = Measure(accepted, quickEnd);
            quickWorth ??= EarlyReplyGate.Worth(text, WordsContext(operation, voiced, done.Result.Evidence, speech), operation.Listening!.WordCheck);
            if (!early!.TryStart(quickPause, pauses, detector.SilenceFrames > 0, quickWorth.Value))
            {
                quickTried = true;
                return;
            }
            var pauseStartedAt = clock.GetTimestamp() - (long)(detector.SilenceFrames * 0.02 * clock.TimestampFrequency);
            if (StartEarly(operation, quick, text, quickPause, early.Starts, pauseStartedAt, voiced) is { } started)
            {
                quickTried = true;
                operation.EarlyStarted = earlyReply = started;
            }
            else early.NotStarted();
        }

        // The turn ended: a reply started early in this very pause waits for the talk window to take it (the same words and
        // what goes with them are checked then); one from an earlier pause goes. Without a judge, speech-to-text reuses the
        // quick transcript of this pause. The reply latency line says how many replies started early.
        void EarlyTurnEnded()
        {
            if (turn is null && quick is not null && quickPause == pauses)
            {
                operation.QuickWords = quick;
                quickCancel = null;
            }
            if (early!.Ended(pauses)) earlyKept = earlyReply is not null;
            else if (earlyReply is { } waiting) LetGoEarly(waiting, EarlyReplyRecord.Cancelled, "you went on talking");
            if (early.Starts > 0 && operation.LatencyTimeline is { } line) line.Early = new(false, early.Starts, early.Cancelled);
        }

        // After a short pause: the judge hears what was said so far (from its pre-roll to now, with the pause), and with Parakeet
        // on this PC a quick transcript of exactly the speech that would be kept starts beside it, for speech-to-text to reuse.
        void AskJudge()
        {
            DropQuick();
            judgement = null;
            judgeError = null;
            judgeEndedAt = 0;
            judgedPause = turn!.Pause;
            var (heard, offset, length, keptBytes) = HeardSoFar();
            var silence = TimeSpan.FromMilliseconds(detector.SilenceFrames * 20);
            judgeStartedAt = clock.GetTimestamp();
            StartQuick(heard, offset, length, keptBytes, judgeStartedAt);
            var request = new EndOfTurnRequest(heard.AsMemory(offset, Math.Max(0, length)), silence,
                quick?.Transcript.ContinueWith(t => t.IsCompletedSuccessfully ? (string?)t.Result.Text : null, TaskScheduler.Default));
            var token = judgeCancel.Token;
            judging = Task.Run(async () =>
            {
                try
                {
                    var answer = await judge!.JudgeAsync(request, token).ConfigureAwait(false);
                    return ((EndOfTurnJudgement?)answer, (Exception?)null, clock.GetTimestamp());
                }
                catch (Exception error) when (error is not OutOfMemoryException) { return (null, error, clock.GetTimestamp()); }
                finally { CryptographicOperations.ZeroMemory(heard); }
            }, CancellationToken.None);
        }

        void DropQuick()
        {
            if (quick is null) return;
            quickCancel?.Cancel();
            quickCancel?.Dispose();
            CryptographicOperations.ZeroMemory(quick.Pcm);
            quick = null;
            quickCancel = null;
        }

        // The turn ended while the judge was on: the decision goes to the log and the status, and the reply latency line gets the
        // pause before the judge was asked and its answer. The quick transcript of this pause is kept for speech-to-text.
        void TurnEnded(long now, long silenceTicks)
        {
            TakeJudgement();
            var timeline = operation.LatencyTimeline!;
            var silence = TimeSpan.FromSeconds(silenceTicks / (double)clock.TimestampFrequency);
            var asked = turn!.Asked && judgeStartedAt != 0 && judgedPause == turn.Pause;
            if (asked) timeline.Mark(ReplyLatency.EndOfTurnWait, judgeStartedAt);
            string outcome;
            if (decided == EndOfTurnStep.Complete)
            {
                outcome = EndOfTurnDecision.Complete;
                timeline.Mark(ReplyLatency.EndOfTurnJudge, now);
            }
            else
            {
                if (asked && judgeEndedAt != 0 && judgeEndedAt <= now) timeline.Mark(ReplyLatency.EndOfTurnJudge, judgeEndedAt);
                timeline.Mark(ReplyLatency.EndOfSpeech, now);
                outcome = decided != EndOfTurnStep.Fallback ? EndOfTurnDecision.Incomplete : turn.Fallback switch
                {
                    EndOfTurnFallback.Failed => EndOfTurnDecision.Failed,
                    EndOfTurnFallback.Slow => EndOfTurnDecision.Slow,
                    _ => EndOfTurnDecision.NotJudged
                };
            }
            RecordTurn(new(clock.GetUtcNow(), outcome, silence, settings.EndSilence, asked ? Took() : null,
                asked ? judgement?.Probability : null, judge!.Name,
                outcome == EndOfTurnDecision.Failed ? judgeError?.GetBaseException().Message : outcome == EndOfTurnDecision.NotJudged ? "your voice wasn't accepted yet" : null));
            if (asked && quick is not null)
            {
                // Its own cancellation stays with it: the transcript may still be on its way.
                operation.QuickWords = quick;
                quickCancel = null;
            }
        }

        // The utterance starts here: when its voice began, on the controller's clock (each frame is 20 ms of it). A reply started
        // early for the turn before this one is let go: the talk window answers both together.
        void Accept()
        {
            accepted = Onset();
            operation.SpeechStartedAt = clock.GetTimestamp() - (long)((index - accepted) * 0.02 * clock.TimestampFrequency);
            // Your voice starting a new utterance lets go of a reply started early for the one before; what this PC plays never
            // does (lines of it that go with your words are caught when the reply is taken).
            if (operation.Listening is { Pc: false } && EarlyReply is { Early: { } other } waiting && !ReferenceEquals(other.Utterance, operation))
                LetGoEarly(waiting, EarlyReplyRecord.Cancelled, "you went on talking");
        }

        // The speech under way is someone's voice, not mostly what the speakers played (or the user has talked over them).
        bool Voice() => talkOver?.Sustained == true || !SpeakersMostly(detector.SpeechStartFrame);
        bool SpeakersMostly(int from) => from >= 0 && from < userSum.Count &&
            speakerSum[^1] - speakerSum[from] > userSum[^1] - userSum[from];
        // Where what is sent starts: the speech's onset, or where the user's own voice began over what the speakers played.
        int Onset() => SpeakersMostly(detector.SpeechStartFrame) && talkOver is { StretchStartFrame: >= 0 } over
            ? Math.Max(detector.SpeechStartFrame, over.StretchStartFrame) : detector.SpeechStartFrame;

        // How much of the speech between two frames was the user's voice (loud frames the speakers don't explain) and how long
        // that voice went on (every frame from its onset to the silence that the speakers don't explain).
        (TimeSpan Voiced, TimeSpan Speech) Measure(int startFrame, int endFrame)
        {
            var last = Math.Clamp(endFrame <= startFrame ? userSum.Count - 1 : endFrame, 0, userSum.Count - 1);
            var first = Math.Clamp(startFrame, 0, last);
            return (TimeSpan.FromMilliseconds((userSum[last] - userSum[first]) * 20),
                TimeSpan.FromMilliseconds((last - first - (explainedSum[last] - explainedSum[first])) * 20));
        }

        // What is sent, with how much of it was the user's voice and how long that voice went on (Measure).
        SpeechRange Range(int startFrame, int endFrame)
        {
            (operation.Voiced, operation.Speech) = Measure(startFrame, endFrame);
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
                // Real words said over Martlet put the live floor at Live again (its voice may be made already).
                FloorWords(operation, heard.Text, context, options.WordCheck, "real words said over Martlet");
                // Asked to stop singing: the song ends musically at once, whatever else is being said over it.
                StopSongIfAsked(operation, heard.Text, context, options.WordCheck);
                if (!options.BargeIn) return;
                var decision = BargeInPolicy.Decide(heard.Text, context, options.WordCheck, mode);
                // Already paused for earlier words: the judge looks again at the longer ones (a stop word stops it now). Once
                // that pause decided to stop, the talk window stops the reply; later checks change nothing.
                if (operation.Held is { } held)
                {
                    if (held.Finished)
                    {
                        if (held.Hold.Outcome == BargeInOutcome.Stop) return;
                    }
                    else
                    {
                        await JudgeHeldAsync(operation, held, heard.Text, context, options, heard.Evidence?.MeanProbability).ConfigureAwait(false);
                        return;
                    }
                }
                if (!decision.Interrupt) return;
                // Pause and decide: words that aren't a clear cue pause the reply at once, and a judge decides.
                if (!decision.Cue && options.BargeInStyle == BargeInBehavior.PauseAndDecide && mode == PlaybackMode.Reply &&
                    Hold(operation, decision, startedAt, checks) is { } paused)
                {
                    await JudgeHeldAsync(operation, paused, heard.Text, context, options, heard.Evidence?.MeanProbability).ConfigureAwait(false);
                    return;
                }
                Remember(Immediate(decision));
                operation.TalkOver = new(decision, clock.GetElapsedTime(startedAt), checks, startedAt);
                operation.TalkingOver = true;
            }
            // A check that fails (the model unloading, a stop) just doesn't stop Martlet; the utterance's transcript still decides.
            catch (Exception error) when (error is not OutOfMemoryException) { }
            finally { CryptographicOperations.ZeroMemory(pcm); }
        });
    }

    // ---------- pause and decide (BargeInJudging) ----------

    // The judge of words said over a paused reply: null means the Thinking pool's model judge when the pool has a member that
    // can run it, otherwise the local rules (BargeInJudge sets another).
    private IBargeInJudge? bargeInJudge;
    private ModelBargeInJudge? poolJudge;
    // A paused reply the judge (or the user talking on) decided to stop: the talk window takes it (TakeHeldStop) and stops the
    // reply the way talking over it always did.
    private TalkOverResult? heldStop;
    private readonly Queue<BargeInRecord> bargeIns = new();

    /// <summary>The judge of words said over a paused reply: the Thinking pool's model judge (<see cref="ModelBargeInJudge"/>
    /// as a <see cref="ThinkingJobKind.BargeInJudge"/> job, the pool's highest priority) when a pool member can run it, else
    /// the local rules. Setting it chooses another; null goes back to that default.</summary>
    internal IBargeInJudge? BargeInJudge
    {
        get => Volatile.Read(ref bargeInJudge);
        set => Volatile.Write(ref bargeInJudge, value);
    }

    // The judge for the next ruling. Asking whether the pool can run it is cheap and takes no slot. The pool judge never uses
    // the conversation's own route, so the reply's prompt cache is left alone.
    private IBargeInJudge CurrentJudge() => BargeInJudge ??
        (ThinkingPool.CanRun(ThinkingJobKind.BargeInJudge) ? poolJudge ??= ModelBargeInJudge.ForPool(ThinkingPool.RunAsync) : RulesBargeInJudge.Instance);

    /// <summary>The last few barge-in decisions, newest last (never what was said).</summary>
    internal IReadOnlyList<BargeInRecord> BargeIns { get { lock (bargeIns) return [.. bargeIns]; } }

    /// <summary>A paused reply that is to stop now, taken once by the talk window.</summary>
    internal TalkOverResult? TakeHeldStop() => Interlocked.Exchange(ref heldStop, null);

    private void Remember(BargeInRecord record)
    {
        lock (bargeIns)
        {
            if (bargeIns.Count == 5) bargeIns.Dequeue();
            bargeIns.Enqueue(record);
        }
    }

    // A clear cue, Stop at once or a song: stopped without a judge.
    private BargeInRecord Immediate(BargeInDecision decision) => new(clock.GetUtcNow(),
        decision.Cue ? BargeInSource.Cue : BargeInSource.Judge, BargeInVerdict.Interrupt, decision.Reason,
        decision.Cue ? "none" : "stop at once", TimeSpan.Zero, null, "stopped");

    // Pauses the reply being said for words said over it (Pause and decide) and watches the pause until it stops or plays on.
    // Returns null when nothing could be paused (no reply speaking, or it is paused already).
    private HeldReply? Hold(LiveConversationOperation operation, BargeInDecision decision, long startedAt, int checks)
    {
        ConversationTurn? turn;
        lock (gate)
            turn = active is { Worker: not null, Playback: PlaybackMode.Reply } reply && !reply.OwnershipReleased ? reply.Turn : null;
        if (turn is null || !turn.Pause()) return null;
        var held = new HeldReply(new BargeInHold(clock), turn, decision, startedAt, checks);
        operation.Held = held;
        ErrorLog.Info($"Barge-in: Martlet paused its reply {clock.GetElapsedTime(startedAt).TotalMilliseconds:0} ms after you started " +
            $"talking over it ({decision.Reason}); the {CurrentJudge().Name} judge decides whether it stops or plays on.");
        Task.Run(() => WatchHoldAsync(operation, held)).Forget();
        return held;
    }

    // The judge rules on what was said so far (within BargeInJudging.Deadline, or the local rules decide); the pause then stops
    // or plays on as soon as it can tell.
    private async Task JudgeHeldAsync(LiveConversationOperation operation, HeldReply held, string? text, UtteranceContext context,
        ListeningOptions options, double? confidence)
    {
        var reply = held.Turn.Content.Text;
        var input = new BargeInJudgeInput(text ?? "", held.Turn.Sentence, reply.Length <= 400 ? reply : reply[^400..], context,
            options.WordCheck, confidence);
        BargeInRuling ruling;
        try { ruling = await BargeInJudging.RuleAsync(CurrentJudge(), input, clock, cancellationToken: operation.OriginalCaller).ConfigureAwait(false); }
        // Listening stopped: the pause's own limit decides.
        catch (OperationCanceledException) { return; }
        held.Hold.Rule(ruling);
        Settle(operation, held);
    }

    // Checks a paused reply every 20 ms until it stops or plays on, so the time limit and the user's silence after their
    // utterance ended decide even when nothing else happens. A pause never outlasts BargeInJudging.MaximumPause.
    private async Task WatchHoldAsync(LiveConversationOperation operation, HeldReply held)
    {
        try
        {
            while (!held.Finished)
            {
                Settle(operation, held);
                if (held.Finished) return;
                await Task.Delay(TimeSpan.FromMilliseconds(BargeInHold.FrameMilliseconds), clock).ConfigureAwait(false);
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            if (held.TryFinish()) held.Turn.Resume();
        }
    }

    // Acts once on a pause's outcome: plays the reply on from where it paused, or hands the stop to the talk window.
    private void Settle(LiveConversationOperation operation, HeldReply held)
    {
        if (held.Turn.Completion.IsCompleted)
        {
            held.TryFinish();
            return;
        }
        var hold = held.Hold;
        var outcome = hold.Evaluate();
        if (outcome == BargeInOutcome.Pending || !held.TryFinish()) return;
        var ruling = hold.Ruling;
        var why = hold.Why ?? held.Decision.Reason;
        var source = hold.Source ?? BargeInSource.Limit;
        var judged = ruling is null ? "no verdict" : ruling.Source == BargeInSource.Cue ? "a clear cue"
            : $"the {ruling.Judge} judge in {ruling.JudgeTime.TotalMilliseconds:0} ms";
        Remember(new(clock.GetUtcNow(), source, outcome == BargeInOutcome.Stop ? BargeInVerdict.Interrupt : BargeInVerdict.NotForMe,
            why, ruling?.Judge ?? "none", ruling?.JudgeTime ?? TimeSpan.Zero, hold.Paused, outcome == BargeInOutcome.Stop ? "stopped" : "resumed"));
        if (outcome == BargeInOutcome.Resume)
        {
            held.Turn.Resume();
            ErrorLog.Info($"Barge-in: Martlet resumed its reply after a {hold.Paused.TotalMilliseconds:0} ms pause: what you said " +
                $"wasn't for it ({why}; {(source == BargeInSource.Limit ? "the pause reached its limit" : judged)}; " +
                $"{hold.Voice.TotalMilliseconds:0} ms of your voice during the pause).");
            return;
        }
        var result = new TalkOverResult(held.Decision with { Reason = why }, clock.GetElapsedTime(held.StartedAt), held.Checks,
            held.StartedAt, ruling, hold.Paused);
        operation.TalkOver = result;
        Volatile.Write(ref heldStop, result);
    }

    // What an utterance's whole transcript does to a reply it was said over: a quick check may already have decided; a reply it
    // paused is left to the judge (a stop word still stops it); otherwise Pause and decide pauses and judges it now (the user
    // is quiet, so it stops or plays on at once), and Stop at once (or a cue, or a song) stops it.
    private async Task<BargeInDecision?> InterruptsAsync(LiveConversationOperation utterance, string? text, UtteranceContext words,
        ListeningOptions options, PlaybackMode mode, double? confidence)
    {
        if (utterance.TalkOver is { } over) return over.Decision;
        var decision = BargeInPolicy.Decide(text, words, options.WordCheck, mode);
        if (utterance.Held is { } held)
        {
            if (held.Finished) return utterance.TalkOver?.Decision ?? (decision.Cue ? decision : null);
            await JudgeHeldAsync(utterance, held, text, words, options, confidence).ConfigureAwait(false);
            return utterance.TalkOver?.Decision;
        }
        if (!decision.Interrupt) return null;
        if (!decision.Cue && options.BargeInStyle == BargeInBehavior.PauseAndDecide && mode == PlaybackMode.Reply &&
            Hold(utterance, decision, utterance.SpeechStartedAt == 0 ? clock.GetTimestamp() : utterance.SpeechStartedAt, 0) is { } paused)
        {
            paused.Hold.Ended();
            await JudgeHeldAsync(utterance, paused, text, words, options, confidence).ConfigureAwait(false);
            return utterance.TalkOver?.Decision;
        }
        Remember(Immediate(decision));
        return decision;
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
                var speech = pcm.AsSpan(start * 2, Math.Max(0, end - start) * 2).ToArray();
                // The end-of-turn judge's quick transcript is reused only for exactly this audio.
                if (operation.QuickWords is { } quick && !quick.Pcm.AsSpan().SequenceEqual(speech))
                {
                    operation.QuickWords = null;
                    ErrorLog.Info("End of turn: the speech kept differs from the quick transcript's; speech-to-text transcribes it again.");
                }
                return speech;
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
        soundDigest?.Dispose();
        pcActivity?.Dispose();
        // Background work ends with Martlet, and so does the live floor (a hold on a host's graphics cards is let go).
        StopPresence();
        jobs.Dispose();
        ReleaseGpus();
        floorRules.Dispose();
        floor.Dispose();
        DisposeThinkRuntimeAsync().Forget();
        DisposeCaptureRuntimeAsync().Forget();
        DisposeQuickSoundsAsync().Forget();
        DisposeVoiceSoundsAsync().Forget();
        singing?.DisposeAsync().AsTask().Forget();
        songHandler?.Dispose();
        pictureHandler?.Dispose();
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
        ConversationRuntime?[] owned;
        ConversationRuntime? song;
        lock (gate)
        {
            owned = [.. thinkSlots.Values.Select(slot => slot.Runtime), .. senseSlots.Values.Select(slot => slot.Runtime)];
            song = songRuntime;
        }
        foreach (var runtime in owned)
            if (runtime is not null) await runtime.DisposeAsync().ConfigureAwait(false);
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
