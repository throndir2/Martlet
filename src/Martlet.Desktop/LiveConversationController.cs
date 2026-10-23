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
using Martlet.Participation;
using Martlet.Providers;
using Martlet.Memory;

namespace Martlet.Desktop;

internal sealed record LiveConversationStatus(string Code, bool Finished = false, bool Quarantined = false,
    PolicyReason? Policy = null, ProviderFailureCode? ProviderFailure = null, ErrorCode? AudioFailure = null);

/// <summary>Talking over Martlet stopped it: why (<see cref="BargeInPolicy"/>), how long after the user's voice began that was
/// decided, how many quick checks of their words it took and when (controller clock) their voice began. <paramref name="Dropped"/>:
/// it was a remark Martlet started on its own, dropped (<see cref="UnpromptedSpeech"/>) rather than stopped for words meant for it.</summary>
internal sealed record TalkOverResult(BargeInDecision Decision, TimeSpan After, int Checks, long StartedAt,
    BargeInRuling? Ruling = null, TimeSpan? Paused = null, bool Dropped = false);

/// <summary>A reply paused because the user talked over it (Pause and decide): the pause's state, the paused turn, what the
/// quick check decided, when the user's voice began and after how many checks, and what Martlet said on its own when it wasn't a
/// reply (<see cref="UnpromptedKind"/>; null for a reply). Finished once (stop or play on).</summary>
internal sealed class HeldReply(BargeInHold hold, ConversationTurn turn, BargeInDecision decision, long startedAt, int checks,
    UnpromptedKind? remark = null)
{
    private int finished;
    internal BargeInHold Hold { get; } = hold;
    internal ConversationTurn Turn { get; } = turn;
    internal BargeInDecision Decision { get; } = decision;
    internal long StartedAt { get; } = startedAt;
    internal int Checks { get; } = checks;
    internal UnpromptedKind? Remark { get; } = remark;
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
    /// <summary>How many sections of an app guide went in the notes of this message (Companion › App guides).</summary>
    internal int GuideSections { get; set; }
    /// <summary>The app whose guide this reply used (its notes, or search_guide found sections), or null: the speech bubble then
    /// offers to read more about it.</summary>
    internal string? GuideKey { get; set; }
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
    /// <summary>The reply may be the no-reply marker ([pass]), which is never shown or spoken: a reply to what always listening
    /// heard, and every reply Martlet starts on its own (a character may have nothing to say, such as no greeting).</summary>
    internal bool MayPass => Spoken || OnItsOwn;
    /// <summary>What Martlet says here on its own, without the user asking (<see cref="UnpromptedSpeech"/>): a report's reminder,
    /// finished work or check-in, or a screen or camera remark. Null for a reply (to the user, to what this PC played or to a
    /// touch, which answers what the user did).</summary>
    internal UnpromptedKind? Unprompted => Commentary ? UnpromptedKind.Remark
        : Report ? UnpromptedSpeech.Of(Delivery?.Jobs.Select(job => job.Kind) ?? []) : null;
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
    /// <summary>What a look went without because it didn't fit Thinking's context size (LiveConversationController.LookReadText
    /// and the rest), in the order it was left out; empty when everything fit.</summary>
    internal IReadOnlyList<string> LookLeftOut { get; set; } = [];
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
    /// <summary>Keeps this reply's exchange when the user stops it while Martlet says it (<see cref="LiveConversationController.Stop"/>
    /// with the context kept): the message and only what was said aloud. Set once the reply's turn starts; run under the
    /// controller's gate.</summary>
    [JsonIgnore] internal Action? KeepCutOff { get; set; }
    /// <summary>What the conversation kept of this reply when the user stopped it while Martlet said it, or null.</summary>
    [JsonIgnore] internal CutOffKept? CutOff { get; set; }
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

/// <summary>What the conversation kept of a reply the user stopped while Martlet said it: how many characters were said aloud
/// and kept, how many it hadn't said, and whether those went to the next request's notes.</summary>
internal sealed record CutOffKept(int SaidCharacters, int UnsaidCharacters, bool Noted);

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
    // The callback list the Callback list check-in writes after each reply (CallbackList): things worth bringing back later, which
    // Think ahead and Quiet moments read. In memory only, forgotten with the conversation.
    private readonly CallbackList callbacks = new();
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
    // The model and fallback choice: what the Thinking model refuses, failed jobs, retired models and Listening's stand-in.
    private readonly ConversationRouting routing;

    /// <summary>Each job's latest failed request that no later request of that job has made good, in job order.</summary>
    internal IReadOnlyList<ConversationRouting.JobFailure> RecentFailures => routing.RecentFailures;
    /// <summary>Raised off the dispatcher when <see cref="RecentFailures"/> changes.</summary>
    internal event Action? FailuresChanged
    {
        add => routing.FailuresChanged += value;
        remove => routing.FailuresChanged -= value;
    }
    // The image and audio models: routes, lanes, picture descriptions and voice notes (SenseSession.cs).
    private readonly SenseSession senseSession;
    // Hearing what this PC plays: its devices, slot, speech-to-text, sound digest and activity note (PcAudioSession.cs).
    private readonly PcAudioSession pcSession;
    /// <inheritdoc cref="ListeningSession.Listen"/>
    internal LiveListener Listen(ListeningOptions options) => listeningSession.Listen(options);

    /// <inheritdoc cref="ListeningSession.StopListening"/>
    internal void StopListening(LiveListener listening) => listeningSession.StopListening(listening);

    /// <inheritdoc cref="ListeningSession.TurnJudgeStatus"/>
    internal (string? Judge, bool Available, string? Problem, TimeSpan? LoadTime, EndOfTurnDecision[] Decisions) TurnJudgeStatus =>
        listeningSession.TurnJudgeStatus;

    /// <summary>Raised (on a background thread) after each end-of-turn decision.</summary>
    internal event Action? TurnDecided
    {
        add => listeningSession.TurnDecided += value;
        remove => listeningSession.TurnDecided -= value;
    }

    /// <inheritdoc cref="ListeningSession.MicrophoneRetry"/>
    internal static TimeSpan MicrophoneRetry(int failures) => ListeningSession.MicrophoneRetry(failures);
    // Revokes every utterance always listening authorized (the microphone's and what the PC plays) when it changes.
    private long listenEpoch;
    // Always listening: the loop, its utterances and their transcription, and the end-of-turn judge (ListeningSession.cs).
    private readonly ListeningSession listeningSession;
    // Background work Martlet started during the conversation (think_longer). Each place Deep thinking thinks on (this PC's
    // choice, deep-thinking.json, read when the conversation is set up or the page saves it) has its own text runtime and
    // credentials, bound to its one think's request at a time, so thinks on several places run at once; and who the last
    // spoken reply heard.
    private readonly BackgroundJobs jobs;
    private readonly Dictionary<string, ThinkSlot> thinkSlots = new(StringComparer.Ordinal);
    internal sealed class ThinkSlot
    {
        internal ICredentialAuthority? Authorization;
        internal ConversationCredentialSource? Credentials;
        internal ConversationRuntime? Runtime;
    }
    // The thinks running now, by job ID: where each works and whether it can run there (for background-jobs.json).
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, RunningThink> thinking = new(StringComparer.Ordinal);
    private sealed record RunningThink(BackgroundThink Think, string Where, string Computer, DeepThinkingPlan Plan);
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
    // The reply's own tools and what each does (ConversationTools.cs).
    private readonly ConversationTools conversationTools;
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

    private string? creationsDirectory;

    /// <summary>Where the signed-in account keeps its creations (docs/ACCOUNTS.md): its account folder, set by the desktop
    /// between replies; the data folder until then.</summary>
    internal string? CreationsDirectory
    {
        get => Volatile.Read(ref creationsDirectory) ?? dataDirectory;
        set
        {
            Volatile.Write(ref creationsDirectory, value);
            if (singing is not null) singing.CreationsDirectory = value;
        }
    }
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
    // ---------- the image and audio models: what other code calls (SenseSession.cs does the work) ----------

    /// <summary>The image and audio models' session, for its test knobs and status (<see cref="SenseSession"/>).</summary>
    internal SenseSession Sense => senseSession;

    /// <inheritdoc cref="SenseSession.SenseModels"/>
    internal SenseModels SenseModels { get => senseSession.SenseModels; set => senseSession.SenseModels = value; }

    /// <inheritdoc cref="SenseSession.ReloadSenseModels"/>
    internal void ReloadSenseModels() => senseSession.ReloadSenseModels();

    /// <inheritdoc cref="SenseSession.LoadSenseSetup"/>
    internal static SenseSetup LoadSenseSetup(string directory, ModelAbilities? abilities) => SenseSession.LoadSenseSetup(directory, abilities);

    /// <inheritdoc cref="SenseSession.SenseRoute(SenseKind)"/>
    internal SenseRoute SenseRoute(SenseKind kind) => senseSession.SenseRoute(kind);

    /// <inheritdoc cref="SenseSession.SenseDecision"/>
    internal SenseDecision SenseDecision(SenseJobKind job) => senseSession.SenseDecision(job);

    /// <inheritdoc cref="SenseSession.RunSenseAsync"/>
    internal Task<SenseJobResult> RunSenseAsync(SenseKind kind, SenseJob job, CancellationToken token) =>
        senseSession.RunSenseAsync(kind, job, token);

    /// <inheritdoc cref="SenseSession.TestSenseAsync"/>
    internal Task<SenseAnswer> TestSenseAsync(SenseKind kind, DeepThinkingSettings model, SenseJob job, CancellationToken token) =>
        senseSession.TestSenseAsync(kind, model, job, token);

    /// <inheritdoc cref="SenseSession.SenseSharesConversation"/>
    internal bool SenseSharesConversation(SenseKind kind) => senseSession.SenseSharesConversation(kind);

    /// <inheritdoc cref="SenseSession.RecordingStaysOnThisPc"/>
    internal bool RecordingStaysOnThisPc(LiveConversationConfiguration? configured = null) => senseSession.RecordingStaysOnThisPc(configured);

    /// <inheritdoc cref="SenseSession.ImageRoute"/>
    internal SenseRoute ImageRoute => senseSession.ImageRoute;

    /// <inheritdoc cref="SenseSession.CanSee"/>
    internal bool CanSee => senseSession.CanSee;

    /// <inheritdoc cref="SenseSession.ImageDescribed"/>
    internal bool ImageDescribed => senseSession.ImageDescribed;

    /// <inheritdoc cref="SenseSession.OwnImageModel"/>
    internal bool OwnImageModel => senseSession.OwnImageModel;

    /// <inheritdoc cref="SenseSession.ImageAdvice"/>
    internal string ImageAdvice() => senseSession.ImageAdvice();

    /// <inheritdoc cref="SenseSession.PictureMoment"/>
    internal void PictureMoment(PictureTrigger why, PictureShot? shot, bool fresh, TimeSpan? sinceActivity, Func<SeenScreen> picture) =>
        senseSession.PictureMoment(why, shot, fresh, sinceActivity, picture);

    /// <inheritdoc cref="SenseSession.ForgetPictures"/>
    internal void ForgetPictures() => senseSession.ForgetPictures();

    /// <inheritdoc cref="SenseSession.PictureNow"/>
    internal (TimeSpan? Age, TimeSpan? Took, string? Model, bool? LastReplyTook) PictureNow => senseSession.PictureNow;
    /// <inheritdoc cref="PcAudioSession.CanHearPc"/>
    internal bool CanHearPc => pcSession.CanHearPc;
    /// <inheritdoc cref="PcAudioSession.SoundDigest"/>
    internal PcSoundDigest? SoundDigest => pcSession.SoundDigest;
    /// <inheritdoc cref="PcAudioSession.PcActivity"/>
    internal PcActivityMonitor? PcActivity => pcSession.PcActivity;
    /// <inheritdoc cref="PcAudioSession.ActivityNoteAge"/>
    internal static TimeSpan ActivityNoteAge => PcAudioSession.ActivityNoteAge;
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
    /// <inheritdoc cref="PcAudioSession.PcWithoutMartlet"/>
    internal bool? PcWithoutMartlet => pcSession.PcWithoutMartlet;
    /// <inheritdoc cref="PcAudioSession.PcOutput"/>
    internal string? PcOutput => pcSession.PcOutput;
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

    private readonly ReplyPreparation replyPreparation;
    private readonly AfterReplyWork afterReplyWork;
    private readonly TurnCapture turnCapture;

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
        replyPreparation = new(this);
        afterReplyWork = new(this);
        turnCapture = new(this);
        this.operations = operations;
        senseSession = new(this);
        this.settings = settings;
        this.vault = vault;
        this.captureDevices = captureDevices;
        this.playbackDevices = playbackDevices;
        this.singing = singing;
        this.echoReducer = echoReducer;
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
        localTranscription = localListener is null ? null : new(localListener, this.clock);
        localWords = localListener;
        // The Parakeet model on this PC that hears an utterance when Listening's own route fails (LocalSpeechSetup.ListeningStandIn).
        routing = new(this.clock, dataDirectory, tools, localListener is null ? null : listeningStandIn, UseAbilities);
        context = new();
        captureCredentials = new(() => Volatile.Read(ref captureAuthorization));
        var credentials = new ConversationCredentialSource(() => Volatile.Read(ref active)?.Authorization);
        runtime = runtimeFactory?.Invoke(credentials, this.clock) ??
            ConversationRuntime.Create(credentials, playbackDevices, clock: this.clock, generatedSpeech: generatedSpeech,
                hostText: new HostTextClient(), hostSpeech: dataDirectory is null ? null : new HostSpeechClient(dataDirectory),
                spokenText: spokenText, characterCues: characterCues);
        transcription = transcriptionFactory?.Invoke(credentials, this.clock) ??
            OpenAiTranscriptionAdapter.Create(credentials, this.clock);
        listeningSession = new(this, turnJudge, transcriptionFactory);
        pcSession = new(this, pcAudio, pcActivity, transcriptionFactory);
        hostTranscription = new(hostListener ?? new HostTranscriptionClient(), this.clock);
        policy = new(runtime.SessionId, new ParticipationConfiguration(), new ParticipationState(), this.clock);
        jobs = new(this.clock);
        StartLiveFloor();
        StartPresence();
        StartRequests();
        // The image and audio models this PC uses (docs/SENSE_MODELS.md: the Vision and Hearing lists), before anything can send them
        // work, and what models were found to hear and see, which routes them before a talk window loads the settings.
        poolAbilities = dataDirectory is null ? null : ModelAbilities.Load(dataDirectory);
        if (dataDirectory is not null) senseSession.senseSetup = SenseSession.LoadSenseSetup(dataDirectory, poolAbilities);
        helperPool = new ThinkingPoolHelpers(() => ThinkingPool);
        helpers = new(() => Volatile.Read(ref helperPool), () => Replying || ReplySpeaking(), dataDirectory) { Floor = floor };
        StartMemoryUpkeep();
        StartMemoryBrief();
        StartMeaningSearch();
        conversationTools = new(this);
        if (singing is not null)
            songHandler = CreationRegistry.Shared.Handle(SongCreations.KindName, new CreationHandler(conversationTools.SingCreationAsync));
        if (dataDirectory is not null)
            pictureHandler = CreationRegistry.Shared.Handle(PictureCreations.KindName, new CreationHandler(conversationTools.ShowPictureCreationAsync));
        jobs.Changed += WriteJobsStatus;
        jobs.Changed += WritePoolStatus;
        jobs.Faulted += (id, error) => ErrorLog.Warn($"Background job {id} failed.", error);
        WriteJobsStatus();
        // sense-models-status.json from the start, before a talk window loads the settings (it says so).
        senseSession.QueueSenseStatus();
    }

    /// <summary>Saves what Martlet found out about a Thinking model (model-abilities.json) and lets the running conversation use
    /// it at once. Nothing is saved without a data directory (tests).</summary>
    internal void RecordAbility(ModelAbility ability) => routing.RecordAbility(ability);

    /// <summary>A reply found something out about a model and saved it (a model its server retired, say): Home proposes a
    /// replacement at once. Raised after the request ended, never on its path.</summary>
    internal event Action<ModelAbility>? AbilityRecorded
    {
        add => routing.AbilityRecorded += value;
        remove => routing.AbilityRecorded -= value;
    }

    /// <summary>Reads model-abilities.json again, after Martlet found out more or another computer shared it.</summary>
    internal void ReloadAbilities() => routing.ReloadAbilities();

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
            changed = configuration is not null && (configuration.Revision != next?.Revision ||
                configuration.Situation?.Key != next?.Situation?.Key);
            memory?.Invalidate();
            // A settings change continues the conversation: what was said so far stays as context for the next reply.
            if (configuration?.Revision != next?.Revision) CancelCapturesLocked();
            configuration = next;
            NameRequests(next);
            stop = RevokeLocked();
            stopListening = RevokeListeningLocked();
        }
        if (changed) revokeAvatar?.Invoke();
        // The live floor follows what the conversation runs on now.
        UseLiveResources(next);
        // Where pictures and recordings go follows the new Thinking model too (docs/SENSE_MODELS.md); without a data folder (tests)
        // the choices set on the controller stay.
        if (dataDirectory is not null) senseSession.ReloadSenseModels();
        else senseSession.SenseRoutesChanged();
        stop?.Cancel("conversation.configuration_changed");
        Cancel(stopListening);
        // Opening the talk window starts the MCP servers in the background, so their tools are ready by the first reply.
        if (next is { SupportsTools: true } && tools is { HasEnabledServers: true }) tools.EnsureStarted(retry: true);
        // Smart home's list of locks, doors and garages too, so the first reply has it without waiting for Home Assistant.
        smartHome?.RefreshSafetyLater();
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
        return new[] { listeningSession.listener, pcSession.pcListener }.OfType<LiveListener>().Where(running => running.Running).ToArray();
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
            var imagePath = senseSession.SenseRoute(SenseKind.Image, selected).Path;
            if (imagePath == SensePath.None) seen = null;
            // Hearing that is on only because it was never chosen holds only while the recording stays on this PC; an audio model
            // of its own takes recordings in Thinking's place, so then Thinking never gets one (docs/SENSE_MODELS.md).
            var stays = senseSession.RecordingStaysOnThisPc(selected);
            var hear = (microphone ? listening?.HearsWith(stays) == true : recording is not null && (!hearLocalOnly || stays)) &&
                senseSession.ThinkingTakesVoice(selected);
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
        if (microphone && operation is { ImagePath: SensePath.Described, Seen: { } pressed }) senseSession.DescribeAhead(pressed, PictureTrigger.Talking);
        return operation;
    }

    private void ClearContextLocked()
    {
        context.Clear();
        saidLately.Clear();
        callbacks.Clear();
        heardLately.Clear();
        lastKept = null;
        lastCache = null;
        conversationId = Guid.NewGuid();
        // The image model's descriptions belong to this conversation too.
        senseSession.pictures?.Forget();
        // The audio model's late words were about messages the conversation no longer keeps.
        Board.Clear(VoiceNotes.BoardSource);
        // And the rest of a reply cut off belongs to a reply it no longer keeps.
        Board.Clear(CutOffReply.BoardSource);
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

    /// <summary>The conversation's callback list for a check-in, oldest first, each with when it came up. In memory only.</summary>
    internal IReadOnlyList<Callback> RecentCallbacks(DateTimeOffset now) => callbacks.Recent(now);

    /// <summary>The callback list's version: it goes up when the conversation is forgotten.</summary>
    internal int CallbackVersion => callbacks.Version;

    /// <summary>Writes the callback list again with what the Callback list check-in answered, unless the conversation was
    /// forgotten since <paramref name="version"/>: then nothing changes and it returns null.</summary>
    internal IReadOnlyList<Callback>? KeepCallbacks(IReadOnlyList<string> items, int version) =>
        callbacks.Keep(items, clock.GetLocalNow(), version);

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
    /// glance message with the description instead of the picture. <paramref name="changed"/> is the look message's {changed}
    /// words (how much changed since the last look, <see cref="LookChange.Describe"/>); null: there is no earlier look.</summary>
    internal LiveConversationOperation StartCommentary(BoundedImage image, string windowTitle, ChattinessChoice chattiness, bool voice,
        bool screenApproved, WatchSource? source = null, CancellationToken caller = default, AttentionSignal? attention = null,
        bool look = false, string? screenText = null, string? app = null, bool fullScreen = false, long picture = 0,
        DateTimeOffset? takenAt = null, string? changed = null)
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
            var imagePath = senseSession.SenseRoute(SenseKind.Image, selected).Path;
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
            var prompt = CommentaryPromptLocked(windowTitle, camera, selected.Prompts, attention, looked, changed);
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
        AttentionSignal? attention = null, SeenScreen? looked = null, string? changed = null)
    {
        var title = new string(windowTitle.Where(c => !char.IsControl(c) && c != '"').Take(80).ToArray()).Trim();
        // What Martlet said lately goes in the look's notes (What you said lately); a glance prompt an older Martlet saved with
        // {remarks} gets nothing there.
        return PromptSettings.Fill(prompts, attention is not null ? PromptCatalog.GlanceAttention
                : camera ? PromptCatalog.GlanceCamera : PromptCatalog.GlanceScreen,
            ("title", title.Length > 0 ? title : "unknown"), ("remarks", ""),
            ("app", ActiveApp.Describe(ActiveApp.Clean(looked?.App), looked?.FullScreen == true)),
            ("changed", changed ?? LookChange.Describe(SinceLook.First, null, TimeSpan.Zero)),
            ("what", attention?.Describe() ?? ""), ("silent", LiveConversationConfiguration.SilentReply))!;
    }

    /// <summary>A screen glance's message: the glance prompt, then the Text on screen prompt with what Companion › Reading read
    /// on that screenshot. The read text goes last, so the request still starts like the one before, and the conversation keeps
    /// only the look's [Screen] line, never this text.</summary>
    internal static string GlanceMessage(string prompt, string? read) => read is null ? prompt : prompt + "\n\n" + read;

    // What a look too long for Thinking's context size goes without, in this order (RunCommentaryAsync), in the log's words.
    internal const string LookReadText = "the text read on the screen";
    internal const string LookNotes = "the context notes and what Martlet said lately";
    internal const string LookGaze = "the look tags";
    internal const string LookActions = "the character's emotes and motions";

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
                var first = await senseSession.DescribeLookAsync(looked, operation.Authorization.Configuration.Prompts, worker).ConfigureAwait(false);
                senseSession.NoteLookPicture(first);
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
            var lore = await replyPreparation.ScanLoreAsync(operation, prompt, earlier, operation.Authorization.Configuration.Persona, worker)
                .ConfigureAwait(false);
            ConversationTurn turn;
            ContextBoardSnapshot board;
            var boardSent = true;
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
                var gaze = look && note is null;
                // A look too long for Thinking's context size (the earlier exchanges and lore entries already left out) goes
                // without, in turn: the text read on the screen, the context board's notes with what Martlet said lately, the
                // look tags, then the character's emotes and motions. The picture and the persona always go.
                var optional = new List<string>();
                if (read is not null) optional.Add(LookReadText);
                if (board.Text is not null || lately is not null) optional.Add(LookNotes);
                if (gaze) optional.Add(LookGaze);
                if (characterActions?.Invoke(operation.Authorization.Voice ? configured.SpeakingEngine() : null, configured.Prompts) is not null)
                    optional.Add(LookActions);
                var leftOut = 0;
                ConversationRequest request;
                int usedHistory, usedLore;
                while (true)
                {
                    var without = optional.Take(leftOut).ToArray();
                    try
                    {
                        var withRead = without.Contains(LookReadText) ? null : read;
                        request = configured.Request(new(note is null ? GlanceMessage(prompt, withRead) : PictureDescriptions.GlanceMessage(prompt, note, withRead)),
                            operation.Authorization.Voice, history, null, lore,
                            out usedHistory, out _, out usedLore, note is null ? image : null,
                            Join(LiveConversationConfiguration.Moment(configured.Prompts), configured.AdultInstructions,
                                LiveConversationConfiguration.CommentaryInstructions(level, camera, configured.Prompts, decides, described: note is not null)),
                            LiveConversationConfiguration.SilentReply, characterActions: without.Contains(LookActions) ? null : characterActions,
                            gaze: gaze && !without.Contains(LookGaze) ? CharacterGaze.Prompt(configured.Prompts, LiveConversationConfiguration.SilentReply) : null,
                            chattiness: decides ? configured.ChattinessNote(level) : null,
                            activity: Activity(),
                            controlTags: LiveConversationConfiguration.ControlTags(decides, picture: note is null, configured.Prompts),
                            board: without.Contains(LookNotes) ? null : Join(board.Text, lately));
                        boardSent = !without.Contains(LookNotes);
                        operation.LookLeftOut = without;
                        if (without.Length > 0)
                            ErrorLog.Info($"Vision: the {(camera ? "camera look" : "screen glance")} didn't fit Thinking's context size " +
                                $"({configured.Context.Describe()}), so it went without {string.Join(", ", without)}.");
                        break;
                    }
                    catch (LiveActionException error) when (error.Code == "conversation.input_limit")
                    {
                        if (leftOut < optional.Count)
                        {
                            leftOut++;
                            continue;
                        }
                        ErrorLog.Warn($"Vision: a {(camera ? "camera look" : "screen glance")} doesn't fit Thinking's context size " +
                            $"({configured.Context.Describe()}: about {configured.TextInputTokens:N0} tokens for the request, at most " +
                            $"{configured.TextLimits.MaxInputBytes:N0} bytes of text), even without anything a look can leave out. The persona " +
                            $"is about {BoundedTextInput.TextTokens(persona?.Text):N0} tokens and the picture " +
                            $"{(note is null ? BoundedImage.TokenReservation : BoundedTextInput.TextTokens(note)):N0}.");
                        throw;
                    }
                }
                operation.SaidLately = boardSent ? latelyCount : 0;
                operation.LookOffered = request.CharacterTags.Any(CharacterGaze.IsTag);
                // Exchanges a look had to leave out are never sent again, so later requests start the same way.
                context.LetGoBefore(context.Start + (history.Count - usedHistory) / 2);
                operation.PersonaRevision = persona?.ConfigurationRevision;
                operation.ContextMessages = usedHistory;
                operation.ContextMessagesOmitted = history.Count - usedHistory;
                ReplyPreparation.RecordLore(operation, lore, usedLore);
                operation.Authorization.BindInput(request.Input);
                operation.Publish(new("commentary.looking"));
                turn = runtime.Start(request, operation.Authorization, operation.OriginalCaller, camera ? "camera glance" : "screen glance");
                operation.Attach(turn);
            }
            if (boardSent)
            {
                BoardSent(board);
                operation.BoardNotes = board.Notes.Count;
                operation.BoardKept = board.KeptText;
            }
            // Nothing else waited (that would have made it a reply that takes the look along): the look alone.
            operation.Inputs = MomentTurn.Describe(false, 0, described is null, operation.Attention?.Plain, 0, contextNotes: operation.BoardNotes,
                said: operation.SaidLately, described: described is not null);
            ErrorLog.Info($"Turn took: {operation.Inputs} (a look).");
            var terminal = await turn.Completion.ConfigureAwait(false);
            routing.NoteFallback(camera ? "Camera glance" : "Screen glance", operation.Authorization.Configuration, terminal);
            NoteInput(camera ? "Camera glance" : "Screen glance", terminal);
            if (decides) Decide(turn, terminal, camera ? "a camera look" : "a screen glance");
            var text = turn.Content.Text;
            var passed = terminal.State == ConversationState.Completed && IsSilentReply(text);
            operation.Passed = passed;
            if (ConversationRouting.IsFailure(terminal))
                routing.LogReplyFailure(camera ? "Camera glance" : "Screen glance", operation.Authorization.Configuration, terminal);
            else if (terminal.State == ConversationState.Completed) routing.Succeeded(SetupRole.Llm);
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
            // Stopped while Martlet said it: the conversation keeps what was said aloud before the next reply is built.
            else operation.KeepCutOff?.Invoke();
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
                var audio = await turnCapture.CaptureAsync(operation).ConfigureAwait(false);
                if (audio is null) return new(SetupWorkOutcome.Completed);
                operation.LatencyTimeline?.Mark("recording");
                // An audio model of its own hears it beside speech-to-text (never on the reply's path).
                if (senseSession.StartVoiceNote(operation, audio) is { } note) operation.VoiceNotes = [note];
                var result = await turnCapture.TranscribeAsync(operation, audio, transcription, worker).ConfigureAwait(false);
                if (result is null)
                {
                    SenseSession.CancelVoiceNotes(operation);
                    return new(operation.Transcription?.Outcome == TranscriptionOutcome.NoSpeech ? SetupWorkOutcome.Completed : SetupWorkOutcome.Failed);
                }
                operation.LatencyTimeline?.Mark("speech-to-text");
                operation.Heard = await turnCapture.HeardAsync(operation, worker).ConfigureAwait(false);
                if (operation.Recognition is not null) operation.LatencyTimeline?.Mark("voice recognition");
                operation.Transcript = result.Text;
                if (operation.Authorization.Hear && TurnCapture.Hearable(audio, "Thinking")) operation.Recording = audio;
                input = new(result.Text!);
            }
            // Straight to Thinking: the recording alone, unless it can't go (the model refused one since, or hearing that was on
            // only by default no longer stays on this PC): then the reply waits for the words, as when transcribing first.
            var straight = operation.StraightWords is { Count: > 0 };
            if (straight && (operation.Recording is null || !operation.Authorization.Hear || !routing.Hears(operation.Authorization.Configuration)))
            {
                // What went straight is at most a message's recording, heard in at most that many pieces.
                using var deadline = new CancellationTokenSource(
                    LiveConversationConfiguration.TranscriptionTime(TimeSpan.FromSeconds(BoundedTextInput.HardMaxAudioSeconds)), clock);
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

            // What the reply carries besides the conversation (memory, lore, tools, Home Assistant, earlier conversations, the app
            // guide, the memory brief): ReplyPreparation reads it, in the same order and with the same waits as before.
            if (await replyPreparation.PrepareAsync(operation, input, straight, history, sentHistory, persona, conversation, worker)
                .ConfigureAwait(false) is not { } prepared)
                return new(SetupWorkOutcome.Canceled);
            var (own, memoryResult, lore, configured, builtIns, toolset, songNote, home) =
                (prepared.Own, prepared.Memory, prepared.Lore, prepared.Configured, prepared.BuiltIns, prepared.Toolset, prepared.SongNote, prepared.Home);
            var (past, pastCount, guide, brief) = (prepared.Past, prepared.PastCount, prepared.Guide, prepared.Brief);

            lock (gate)
            {
                operation.Authorization.Check(worker);
                var prompts = operation.Authorization.Configuration.Prompts;
                // The recording goes only to a Thinking model that hears and hasn't refused one this session (checked above for a
                // recording sent alone; a model that refuses it after all is asked again with its words).
                var recording = straight ? operation.Recording : operation.Authorization.Hear && configured.Hearing() == HearingSupport.Supported &&
                    !routing.Deaf(configured) ? operation.Recording : null;
                // While vision is on, the newest picture of what it watches goes with the message, so the reply sees it too.
                // A message too long to fit beside the picture goes without it.
                var seen = operation.Authorization.Screen ? operation.Seen : null;
                // With an image model of its own (the Described path) no picture goes to Thinking. The reply takes the image model's
                // description of the picture it would have sent when one is ready now (it never waits for one), as a note sent with
                // this request only, and every reply is told what such notes are, the same way each time (docs/SENSE_MODELS.md).
                var describes = (operation.ImagePath ?? senseSession.SenseRoute(SenseKind.Image, configured).Path) == SensePath.Described;
                var description = describes ? senseSession.DescriptionFor(operation) : null;
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
                var described = senseSession.DescribesVoice(configured) ? SkillSettings.Guidance(prompts, SkillCatalog.VoiceDescribed) : null;
                heardHow = SenseSession.VoiceNow(operation, prompts);
                // Singing on a route without function calling: the reply writes song tags in place of the song tools, told the
                // same way every time (settings alone decide it), so the request starts the same.
                var singsWithTags = conversationTools.SingsWithTags(configured);
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
                            recording is null ? null : SkillSettings.Guidance(prompts, straight ? SkillCatalog.HeardVoiceOnly : SkillCatalog.HeardVoice),
                            described,
                            picture is null ? null : PromptSettings.Fill(prompts, PromptCatalog.SeenWithMessage, ("source", picture.Describe())),
                            picture is null ? null : SeenTags.Instructions(prompts, LiveConversationConfiguration.SilentReply),
                            describes ? PictureDescriptions.Instructions(prompts) : null,
                            singsWithTags ? SongTags.Instructions(prompts) : null,
                            // Every one of Martlet's own tools was handed off and no other tool is offered: what the reply is told
                            // instead still reaches it (with tools, it follows the tools prompt).
                            toolset is null && builtIns is { Tools.Count: 0 } ? builtIns.Guidance : null),
                        voices: VoicePromptContext.Block(operation.Heard),
                        activity: Activity(),
                        messageNotes: Join(brief, home is { Kind: HomeTurnKind.Tools } ? null : home?.Instructions, background,
                            picture is null && describedNote is null ? null : noticed, recalled, guide, songNote, whileSinging, touchNote),
                        silentReply: operation.MayPass ? LiveConversationConfiguration.SilentReply : null, tools: toolset,
                        closingInstructions: operation.Authorization.Configuration.ReplyClosing(operation.Authorization.Voice), audio: recording, imageOptional: true,
                        characterActions: characterActions, withoutReasoning: routing.RefusesReasoning(configured),
                        chattiness: decides ? operation.Authorization.Configuration.ChattinessNote(decided) : null,
                        controlTags: LiveConversationConfiguration.ControlTags(decides, picture is not null, prompts, singsWithTags),
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
                    // A message too long to fit goes without what was said in earlier conversations first, then without the guide's
                    // sections, then without the memory brief, then without the picture (or its description).
                    catch (LiveActionException error) when (error.Code == "conversation.input_limit" &&
                        (past is not null || guide is not null || brief is not null || picture is not null || describedNote is not null))
                    {
                        if (past is not null) (past, pastCount) = (null, 0);
                        else if (guide is not null) (guide, operation.GuideSections, operation.GuideKey) = (null, 0, null);
                        else if (brief is not null) brief = null;
                        else if (picture is not null) picture = null;
                        else describedNote = null;
                    }
                }
                if (brief is not null) ErrorLog.InfoLater("Memory brief: went with your message (the first in this conversation).");
                operation.PastExchanges = pastCount;
                operation.VoiceSent = request.Input.Audio is not null;
                operation.ScreenSent = request.Input.Image is not null;
                operation.Described = describedNote is null ? null : description;
                operation.Sent = request.Input;
                // How big the reply's start is (docs/CONVERSATION.md#the-director): its instructions and its tools, by Martlet's own
                // estimate; logged later, so the first words never wait for it.
                if ((own is not null || straight) && request.Parts is { } parts)
                {
                    var director = DirectorTakesTools(HandOffs(configured));
                    var sentTools = toolset?.Definitions ?? [];
                    var skillPrompts = configured.Prompts;
                    // Off the reply's path: which skills it carried, for Companion › Skills and Prompts and the log.
                    Task.Run(() => KeepReplyPrompt(parts, sentTools, skillPrompts, director)).Forget();
                }
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
                ReplyPreparation.RecordLore(operation, lore, usedLore);
                operation.Authorization.BindInput(request.Input, request.Limits.MaxToolRounds, request.ImageOptional);
                // Exact-content commit, pause/consent state and immediate Start share this short, non-awaiting gate.
                operation.ReplyStartedAt = clock.GetTimestamp();
                operation.LatencyTimeline?.Mark("building the request", operation.ReplyStartedAt);
                // Its name in the Thinking trace (docs/VOICE_LATENCY.md, Thinking trace).
                var purpose = operation.Report ? "background report" : operation.Touch ? "touch reaction"
                    : own is not null || straight ? early is null ? "reply" : "reply started early" : "reply to what this PC played";
                turn = early is null ? runtime.Start(request, operation.Authorization, operation.OriginalCaller, purpose)
                    : runtime.StartEarly(request, operation.Authorization, early.PrepareVoice, operation.OriginalCaller, purpose);
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
            // The director starts on the user's own words now, beside the reply (MainWindow fires it on the UI thread; nothing here
            // waits for it).
            if (own is not null && !operation.Report && !operation.Touch) UserTurnStarted?.Invoke(own);
            operation.BoardNotes = board.Notes.Count;
            // The audio model's words it carried leave a short line after the message in the conversation, before the board's.
            operation.BoardKept = heardHow.Kept is null ? board.KeptText : board.KeptText is null ? heardHow.Kept : heardHow.Kept + "\n" + board.KeptText;
            senseSession.SentVoiceNotes(operation, heardHow.Ready);
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
            // What came before the request in the Thinking trace: each wait from the moment that counts for the user.
            if (turn.TraceName is not null && operation.LatencyTimeline is { } before)
                turn.Note($"prepared {ReplyLatency.Before(before, operation.ReplyStartedAt)}.");
            // Whether a reply with a picture on the Described path took the image model's description (MCP's image_model_check reads
            // the newest line).
            senseSession.NotePicturePath(operation);
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
            // A message that came with a picture keeps where it was from and what the reply saw in it (its [seen: ...] words), on
            // a line after the message; the picture itself is never kept. In place of the picture, the image model's description:
            // its first line is what Martlet saw. The touches that came with a message stay noted after it too ("(touch: top of
            // head pat x3)"), so later replies know; a touch-only reply's message is that line itself.
            string? SawLine(ConversationSnapshot snapshot)
            {
                var line = operation.ScreenSent && !snapshot.ImageRejected && operation.Seen is { } pictured
                    ? pictured.HistoryLine(SeenTags.Description(turn.Controls), message: true)
                    : operation.Described is { } words && operation.Seen is { } shown
                    ? (shown with { Title = words.Shot.Title, App = words.Shot.App, FullScreen = words.Shot.FullScreen }).HistoryLine(words.Summary, message: true)
                    : null;
                if (!operation.Touch && operation.Touches?.HistoryLine is { Length: > 0 } touchLine)
                    line = line is null ? touchLine : line + "\n" + touchLine;
                return line;
            }
            // The user may stop the reply while Martlet says it (talking over it, Stop, Esc, a touch): Stop runs this under the
            // gate, before the next reply's request is built, so the conversation has the message and what was said aloud.
            if (!operation.OnItsOwn && turn.Spoken)
                operation.KeepCutOff = () => KeepCutOffLocked(operation, configured, conversation, turn, straight, sawLine: SawLine(turn.Snapshot),
                    said: straight ? null : operation.PcAudio ? input!.UserText : VoicePromptContext.Prefix(operation.Heard) + input!.UserText,
                    recorded: straight ? null : operation.PcAudio ? operation.UserWords : input!.UserText);
            var terminal = await turn.Completion.ConfigureAwait(false);
            // Touches a reply took but never answered (it was stopped or failed) wait for the next reply.
            if (terminal.State != ConversationState.Completed && operation.Touches is { } unanswered) touches.Restore(unanswered);
            routing.NoteFallback(operation.Report ? "Background report" : "Reply", configured, terminal);
            NoteInput(operation.Report ? "Background report" : "Reply", terminal);
            NoteFirstWords(configured, terminal);
            if (operation.BackgroundChattiness == ChattinessChoice.MartletDecides)
                Decide(turn, terminal, operation.Report ? "bringing up background work"
                    : operation.PcAudio && operation.UserWords is null ? "what this PC played" : "your message");
            routing.NoteRefusals(configured, terminal);
            if (ConversationRouting.IsFailure(terminal)) routing.LogReplyFailure("Reply", configured, terminal);
            else if (terminal.State == ConversationState.Completed) routing.Succeeded(SetupRole.Llm);
            // What always listening heard may not have been meant for Martlet: the model answers [pass] and stays quiet.
            var passed = operation.MayPass && terminal.State == ConversationState.Completed && IsSilentReply(turn.Content.Text);
            operation.Passed = passed;
            // Singing on a route without function calling: the reply's song tags start, play or stop a song now that it's done.
            IReadOnlyList<string> songTags = terminal.State == ConversationState.Completed ? turn.Controls : [];
            if (SongTags.Calls(songTags) is { Count: > 0 } songCalls) conversationTools.SingFromTags(operation, configured, songCalls, passed);
            // A reply that ends by asking something makes a short answer to it ("yes", "mm-hmm") count as words for a while.
            if (terminal.State == ConversationState.Completed && !passed && !string.IsNullOrWhiteSpace(turn.Content.Text))
                Interlocked.Exchange(ref askedAt, AsksSomething(turn.Content.Text) ? clock.GetTimestamp() : 0);
            var exchangeEnded = false;
            var spoke = false;
            if (terminal.State == ConversationState.Completed && !string.IsNullOrWhiteSpace(turn.Content.Text))
            {
                lock (gate)
                {
                    if (ReferenceEquals(active, operation) && !operation.Authorization.IsCanceled && operation.CutOff is null)
                    {
                        var earlier = context.Snapshot();
                        var kept = passed ? $"[{LiveConversationConfiguration.SilentReply}]"
                            : SongTags.Kept(songTags) is { } wrote ? turn.Content.Text.TrimEnd() + " " + wrote : turn.Content.Text;
                        var sawLine = SawLine(terminal);
                        string? Saw(string? text) => text is null || sawLine is null ? text : VisionHistory.After(text, sawLine);
                        string? said = null;
                        if (straight)
                        {
                            // Straight to Thinking: the exchange is kept now and its words replace what stands in for them once
                            // speech-to-text has them; the record of conversations, memory and learning names follow then.
                            var exchange = context.Add(Saw(VoicePromptContext.Prefix(operation.Heard) + LiveConversationConfiguration.VoiceOnlyText)!,
                                kept, Saw(operation.WithBoardKept(operation.Sent?.KeptUserText)));
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
                            context.Add(Saw(said)!, kept, Saw(operation.WithBoardKept(operation.Sent?.KeptUserText)));
                            // The check-ins that run after each exchange read it now (a pass said nothing to act on).
                            exchangeEnded = !passed;
                            // What always listening heard, answered or not, for Talking to someone else.
                            spoke = operation.Spoken && !operation.OnItsOwn &&
                                NoteHeardLocked(operation, operation.PcAudio ? operation.UserWords : input!.UserText, passed);
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
                            if (passed) foreach (var job in delivered.Jobs) quietJobs[job.Id] = true;
                            ErrorLog.Info($"Background work: {string.Join(", ", delivered.Jobs.Select(job => job.Id))} " +
                                (operation.Report ? "brought up by Martlet on its own"
                                    : own is null && !straight ? "brought up with what this PC played" : "brought up with your message") +
                                (passed ? " (it stayed quiet about it)." : "."));
                        }
                        if (!operation.OnItsOwn) lastAsked = (operation.Spoken, operation.Heard, operation.BackgroundChattiness);
                        // The conversation moved on: a check-in made before this exchange is out of date (UnpromptedSpeech).
                        if (!operation.Report) Interlocked.Exchange(ref lastExchangeTicks, clock.GetUtcNow().UtcTicks);
                        // What a check-in's background work continues after this exchange: its request and the reply as kept.
                        lastKept = new(operation.Sent, kept);
                        // The reply a check-in's sing_song continues to write lyrics, as the reply's own call would.
                        Volatile.Write(ref lastReply, operation);
                        // The note about the last song is in the conversation now.
                        if (songNote is not null) singing?.NoteDelivered(songNote);
                        // Memory and learning names only ever read what the user said themselves, never what the PC played.
                        var spokenOwn = operation.OnItsOwn || straight ? null : operation.PcAudio ? operation.UserWords : input!.UserText;
                        var remembered = spokenOwn is null ? null
                            : operation.PcAudio ? VoicePromptContext.Prefix(operation.Heard) + spokenOwn : said;
                        var remember = operation.MemoryRequested && !passed && remembered is not null;
                        // Learning names also runs for a message whose voice Martlet wasn't sure of (VoiceNaming.Worth).
                        var heard = !passed && remembered is not null && operation.Heard is { Voices.Count: > 0 } known &&
                            voices is { Active: true } && VoiceNaming.Worth(known, spokenOwn!, turn.Content.Text,
                                operation.Authorization.Configuration.CompanionNames) ? known : null;
                        // Whose new facts are: the voices recognized in the message (the speaker's, unless another is named).
                        var present = remember && operation.Heard is { Known.Count: > 0 } recognized ? recognized : null;
                        // Each after-reply request continues the reply's request (instructions, tools, earlier messages and the
                        // message), then the reply as the next reply's history has it, whether or not the reply called tools.
                        if (remember || heard is not null)
                            afterReplyWork.EnqueueAfterReplyLocked(operation.Authorization.Configuration, remember, heard, earlier, remembered!,
                                turn.Content.Text, operation.Sent, present);
                    }
                }
            }
            operation.Publish(new(passed ? "listen.passed" : "runtime." + terminal.State, Finished: true, Quarantined: terminal.Quarantined,
                Policy: PolicyReason.DispatchAccepted, ProviderFailure: terminal.ProviderFailure, AudioFailure: terminal.Playback?.Error?.Code));
            if (exchangeEnded) ExchangeEnded?.Invoke();
            if (spoke) Spoke?.Invoke(!passed);
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
            senseSession.EndVoiceNotes(operation);
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

    private sealed record StraightExchange(LiveConversationOperation Operation, LiveConversationConfiguration Configured, Guid Conversation,
        IReadOnlyList<TextHistoryMessage> Earlier, object Exchange, string Reply, bool Passed, string? SawLine = null, bool Cut = false)
    {
        public override string ToString() => nameof(StraightExchange);
    }

    // The user stopped the reply while Martlet said it (under the gate, from Stop): the conversation keeps the message and only
    // what was said aloud, cut off (CutOffReply), so the next reply never thinks they heard the rest. The rest goes once in the
    // notes of the next request, which may pick it up or drop it. A reply that finished, said nothing aloud or is no longer the
    // active one keeps nothing here. said: the message as the conversation keeps it (null when it went straight to Thinking).
    private void KeepCutOffLocked(LiveConversationOperation operation, LiveConversationConfiguration configured, Guid conversation,
        ConversationTurn turn, bool straight, string? sawLine, string? said, string? recorded)
    {
        if (operation.CutOff is not null || !ReferenceEquals(active, operation) || operation.Authorization.IsCanceled ||
            turn.Completion.IsCompleted) return;
        var aloud = turn.SaidAloud;
        if (CutOffReply.Kept(aloud) is not { } kept) return;
        var reply = turn.Content.Text;
        var unsaid = CutOffReply.Unsaid(reply, aloud);
        string? Saw(string? text) => text is null || sawLine is null ? text : VisionHistory.After(text, sawLine);
        if (straight)
        {
            // Its words replace what stands in for them once speech-to-text has them, as for any reply.
            var earlier = context.Snapshot();
            var exchange = context.Add(Saw(VoicePromptContext.Prefix(operation.Heard) + LiveConversationConfiguration.VoiceOnlyText)!,
                kept, Saw(operation.WithBoardKept(operation.Sent?.KeptUserText)));
            ConversationContextBuffer.Pending(exchange,
                KeepWordsAsync(new(operation, configured, conversation, earlier, exchange, kept, Passed: false, sawLine, Cut: true)));
        }
        else
        {
            context.Add(Saw(said)!, kept, Saw(operation.WithBoardKept(operation.Sent?.KeptUserText)));
            // The record of conversations keeps the user's own words (never what the PC played) and what was said aloud.
            if (history is { } historyRecord && historyRecord.Active(configured.Memory) &&
                recorded is { } recordedWords)
                historyRecord.Record(conversation, operation.Spoken || operation.Authorization.Microphone ? HistoryInputKind.Spoken : HistoryInputKind.Typed,
                    recordedWords, kept, operation.OriginSpeaker ?? (operation.Heard?.Speaker?.Voice is { Named: true } namedVoice ? namedVoice.DisplayName : null),
                    operation.Origin);
        }
        saidLately.Add(clock.GetLocalNow(), kept);
        var noted = false;
        if (CutOffReply.Note(configured.Prompts, unsaid) is { } note)
        {
            try { noted = Board.Post(CutOffReply.BoardSource, note, clock.GetLocalNow(), CutOffReply.NoteAge, consume: true) is not null; }
            catch (InvalidOperationException) { }
        }
        else Board.Clear(CutOffReply.BoardSource);
        operation.CutOff = new(aloud.Trim().Length, unsaid?.Length ?? 0, noted);
        // Sizes only, never the words.
        ErrorLog.Info($"Cut off: you stopped Martlet while it talked; the conversation keeps what it said aloud ({operation.CutOff.SaidCharacters} " +
            $"of {reply.Length} characters, marked as cut off)" + (noted ? $" and the {operation.CutOff.UnsaidCharacters} characters it hadn't said go once with the next request."
                : unsaid is null ? "; nothing was left unsaid." : "; the Cut off prompt is empty, so the rest is dropped."));
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
        bool filled = false, recorded = false, remembering = false, spoke = false;
        lock (gate)
        {
            if (disposed) return;
            var current = conversationId == keep.Conversation;
            // A picture that came with the message stays noted after its words (VisionHistory).
            if (current) filled = context.Fill(keep.Exchange, keep.SawLine is null ? said : VisionHistory.After(said, keep.SawLine),
                sent is not null ? keep.SawLine is null ? operation.WithBoardKept(sent.KeptUserText) : VisionHistory.After(operation.WithBoardKept(sent.KeptUserText)!, keep.SawLine) : null);
            // What always listening heard, answered or not, for Talking to someone else (only real words).
            spoke = filled && real && !keep.Cut && NoteHeardLocked(operation, userWords, keep.Passed);
            if (!keep.Passed && history is { } historyRecord && historyRecord.Active(keep.Configured.Memory))
            {
                historyRecord.Record(keep.Conversation, HistoryInputKind.Spoken, userWords, keep.Reply,
                    operation.Heard?.Speaker?.Voice is { Named: true } namedVoice ? namedVoice.DisplayName : null);
                recorded = true;
            }
            // A reply the user stopped while Martlet said it isn't remembered from.
            if (!keep.Passed && !keep.Cut && real && current)
            {
                var remember = operation.MemoryRequested;
                var heard = operation.Heard is { Voices.Count: > 0 } known && voices is { Active: true } &&
                    VoiceNaming.Worth(known, userWords, keep.Reply, keep.Configured.CompanionNames) ? known : null;
                var present = remember && operation.Heard is { Known.Count: > 0 } recognized ? recognized : null;
                if (remember || heard is not null)
                {
                    afterReplyWork.EnqueueAfterReplyLocked(keep.Configured, remember, heard, keep.Earlier, said, keep.Reply, sent ?? operation.Sent, present);
                    remembering = true;
                }
            }
        }
        // What happened to the words (never the words themselves).
        ErrorLog.Info($"Straight to Thinking: {(real ? "the transcript" : userWords == SpokenWords.NotTranscribed ? "no transcript" : "words the word check doesn't count")} " +
            $"{(filled ? "replaced the recording in the conversation" : "came after the conversation was cleared")}" +
            (recorded ? ", went to the record of conversations" : "") + (remembering ? " and to remembering" : "") +
            (keep.Passed ? " (Martlet had stayed quiet)" : "") + ".");
        // Its words are in the conversation now, so the check-ins that run after each exchange can read them.
        if (filled && !keep.Passed) ExchangeEnded?.Invoke();
        if (spoke) Spoke?.Invoke(!keep.Passed);
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

    // ---------- the reply's tools: what they use here, and what other code calls (ConversationTools.cs does the work) ----------

    /// <inheritdoc cref="ConversationTools.ReplyThinkTools"/>
    internal IReadOnlyList<TextToolDefinition> ReplyThinkTools(LiveConversationConfiguration configured) =>
        conversationTools.ReplyThinkTools(configured);

    /// <inheritdoc cref="ConversationTools.RunDiscordCheckInToolAsync"/>
    internal ValueTask<ConversationToolResult> RunDiscordCheckInToolAsync(TextToolCall call, CancellationToken token) =>
        conversationTools.RunDiscordCheckInToolAsync(call, token);

    /// <inheritdoc cref="ConversationTools.RemindAsync"/>
    internal Task<Reminders.ToolOutcome> RemindAsync(string tool, string argumentsJson, string? checkIn, CancellationToken token) =>
        conversationTools.RemindAsync(tool, argumentsJson, checkIn, token);

    /// <inheritdoc cref="ConversationTools.CheckInMemoryAsync"/>
    internal ValueTask<ConversationToolResult> CheckInMemoryAsync(TextToolCall call, string by, CancellationToken token) =>
        conversationTools.CheckInMemoryAsync(call, by, token);

    /// <inheritdoc cref="ConversationTools.CreationsCheckInAsync"/>
    internal ValueTask<ConversationToolResult> CreationsCheckInAsync(TextToolCall call, CheckInToolContext context, CancellationToken token) =>
        conversationTools.CreationsCheckInAsync(call, context, token);

    /// <inheritdoc cref="ConversationTools.SingsWithTags"/>
    internal bool SingsWithTags(LiveConversationConfiguration configured) => conversationTools.SingsWithTags(configured);

    /// <inheritdoc cref="ConversationTools.StopSong"/>
    internal SongStopRecord? StopSong(bool musical, string button) => conversationTools.StopSong(musical, button);

    /// <inheritdoc cref="ConversationTools.CancelJob"/>
    internal bool CancelJob(string id) => conversationTools.CancelJob(id);

    /// <inheritdoc cref="ConversationTools.EndBackgroundWork"/>
    internal void EndBackgroundWork() => conversationTools.EndBackgroundWork();

    /// <summary>Changes the webcam background in the owner's Discord calls; null while that isn't wired.</summary>
    internal ICallCamera? CallCamera { get; set; }

    /// <summary>Rings a Discord friend for call_on_discord; null while Discord isn't wired.</summary>
    internal IDiscordCaller? DiscordCaller { get; set; }

    /// <summary>Whether a check-in may run the Discord calls and camera set (<see cref="DiscordCheckInTools"/>) now: Martlet can
    /// call a Discord friend or is in the owner's Discord calls.</summary>
    internal bool RunsDiscordCheckInTools => DiscordCaller is { CanCall: true } || CallCamera is { Offered: true };

    /// <summary>Runs one reminders call (set, list, cancel) on this PC's reminders, which travel with the shared settings; set by
    /// the main window. Null where reminders can't be kept (no data folder).</summary>
    internal Func<string, CancellationToken, Task<Reminders.ToolOutcome>>? RemindersTool { get; set; }

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

    /// <summary>Brings Martlet's greeting after it starts into this conversation (<see cref="Greetings.Kind"/>, with the Greeting
    /// prompts): <paramref name="facts"/> is what it knows (<see cref="Greetings.Facts"/>). Null when the conversation is closing.</summary>
    internal BackgroundJob? Greet(string facts) =>
        jobs.Start(Greetings.Kind, "Greeting", (_, _) => Task.FromResult(BackgroundJobOutcome.Done(facts))).Job;

    // The finished jobs a reply carried that Martlet stayed quiet about ([pass]), by job ID.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> quietJobs = new(StringComparer.Ordinal);

    /// <summary>Whether the reply that carried <paramref name="job"/> stayed quiet ([pass]): a character that wouldn't greet.</summary>
    internal bool StayedQuiet(BackgroundJob job) => quietJobs.ContainsKey(job.Id);

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

    /// <summary>The kinds of creation Martlet knows and what performs them (docs/CREATIONS.md).</summary>
    internal CreationRegistry Creations { get; init; } = CreationRegistry.Shared;

    /// <summary>The VRM character's body motions (docs/AVATARS.md#motions-in-conversation); null while they aren't wired.</summary>
    internal CharacterMotions? Motions { get; set; }

    // The newest finished reply, which a check-in's sing_song continues to write lyrics, as the reply's own call would.
    private LiveConversationOperation? lastReply;

    /// <summary>Whether this PC runs the Songs, pictures and creations tool set: it keeps creations (a data folder).</summary>
    internal bool RunsCreationsCheckIn => dataDirectory is not null;

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
            var target = DeepThinkTarget.For(deep.Single, effort, configured.Routes.ThinkingRoute(),
                ModelLimits.Load(dataDirectory));
            var separate = target.Request(ThinkLonger.Fit(full, target.Bounds), effort, left, continuing);
            var own = new DeepThinkAuthorization(target, separate, configured.Profile,
                configured.Routes.ThinkingRoute(), vault, clock, clock.GetUtcNow() + left + TimeSpan.FromSeconds(5));
            bind(own);
            return (separate, own);
        }
        // With the Thinking model: within the reply's own bounds, its tools kept so the start is the reply's.
        var input = configured.FitsContext(full) ? full : ThinkLonger.Fit(full, new(configured.TextLimits.MaxInputBytes,
            configured.TextLimits.MaxHistoryMessages, configured.TextInputTokens, Tools: false));
        var withoutReasoning = routing.RefusesReasoning(configured);
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

    /// <summary>Starts a reply Martlet gives on its own to bring up finished background work, as soon as it is free (the talk
    /// window decides when: never while the user talks, a turn is pending or Martlet is replying). Its message is Martlet's
    /// note with the results (Companion › Prompts › Background work finished), which stays in the conversation; tools stay
    /// available, so a later tool can act on the user's yes. Null when nothing waits.</summary>
    internal LiveConversationOperation? StartReport(bool voice, bool noticesOnly = false, SeenScreen? seen = null,
        AttentionSignal? attention = null, bool look = false)
    {
        var delivery = TakeJobs(onItsOwn: true, noticesOnly);
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
                var imagePath = senseSession.SenseRoute(SenseKind.Image, selected).Path;
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

    /// <summary>How long Listening's stand-in hears every turn at once after Listening's own route failed, before the route is
    /// asked again.</summary>
    internal static TimeSpan StandInFor => ConversationRouting.StandInFor;

    private static string? Join(params string?[] parts) =>
        parts.Where(part => part is not null).ToArray() is { Length: > 0 } present ? string.Join("\n\n", present) : null;

    // The voices heard lately, for the check-ins (Who is here, Someone else is here): names and when, in memory only.
    private readonly List<CheckInVoice> voicesLately = [];

    private void NoteVoices(HeardVoices heard)
    {
        var at = clock.GetLocalNow();
        lock (gate)
        {
            foreach (var voice in heard.Voices)
                if (voice.Voice is { } known)
                    voicesLately.Add(new(known.Named ? known.DisplayName : null, voice.Mine, at) { Id = known.Id });
            voicesLately.RemoveAll(v => at - v.At > CheckIns.PeopleWindow);
            if (voicesLately.Count > 64) voicesLately.RemoveRange(0, voicesLately.Count - 64);
        }
    }

    /// <summary>The voices Martlet heard within <see cref="CheckIns.PeopleWindow"/>, oldest first, and whether a voice Martlet
    /// knows is linked to the signed-in account (yours), for a check-in. In memory only.</summary>
    internal (IReadOnlyList<CheckInVoice> Voices, bool OwnerKnown) RecentVoices(DateTimeOffset now)
    {
        var ownerKnown = voices?.Yours.Count > 0;
        lock (gate) return ([.. voicesLately.Where(v => now - v.At <= CheckIns.PeopleWindow)], ownerKnown);
    }

    // What always listening heard the user say lately, answered or not, for Talking to someone else: in memory only, forgotten
    // with the conversation.
    private readonly List<CheckInHeard> heardLately = [];

    /// <summary>Raised (off the UI thread) once what always listening heard is in the conversation, whether Martlet answered it
    /// (true) or stayed quiet: the main window fires the <see cref="CheckInTriggers.Spoke"/> check-ins with it.</summary>
    internal event Action<bool>? Spoke;

    // Notes what always listening heard (its words, who spoke, the other voices and this PC's sound with it, and whether Martlet
    // answered); false when it has no words of the user's.
    private bool NoteHeardLocked(LiveConversationOperation operation, string? words, bool passed)
    {
        if (string.IsNullOrWhiteSpace(words)) return false;
        var at = clock.GetLocalNow();
        var speaker = operation.Heard?.Speaker;
        heardLately.Add(new(at, words, !passed)
        {
            Speaker = speaker?.Voice is { Named: true } named ? named.DisplayName : null, Recognized = speaker?.Voice is not null,
            Owner = speaker?.Mine == true, OtherVoices = operation.Heard?.Others.Count ?? 0, Overlap = operation.Heard?.Overlap == true,
            PcPlayed = operation.PcAudio
        });
        heardLately.RemoveAll(h => at - h.At > CheckIns.HeardWindow);
        if (heardLately.Count > 2 * CheckIns.MaximumHeard) heardLately.RemoveRange(0, heardLately.Count - 2 * CheckIns.MaximumHeard);
        return true;
    }

    /// <summary>What always listening heard the user say within <see cref="CheckIns.HeardWindow"/>, oldest first, for a check-in.
    /// In memory only.</summary>
    internal IReadOnlyList<CheckInHeard> RecentHeard(DateTimeOffset now)
    {
        lock (gate) return [.. heardLately.Where(h => now - h.At <= CheckIns.HeardWindow)];
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

    /// <summary>What the user does now (<see cref="MemoryActivity"/>): recall leaves out the facts of other activities, and
    /// remembering keeps a new fact for this one when it matters only here. The game watch's game by default.</summary>
    internal Func<string?> Activity { get; set; } = () => CurrentActivity.Now;

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
            var turn = capture.Start(request, authorization, token, purpose.ToLowerInvariant());
            var terminal = await turn.Completion.ConfigureAwait(false);
            await turn.OwnershipRelease.ConfigureAwait(false);
            routing.NoteFallback(purpose, configuration, terminal);
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
            if (!token.IsCancellationRequested && ConversationRouting.IsFailure(terminal))
            {
                if (nothing || finished is not null) ErrorLog.Warn($"{purpose} got no usable answer ({ConversationRouting.Describe(terminal)}); nothing changed.");
                else routing.LogFailure(purpose, configuration, SetupRole.Llm, ConversationRouting.Describe(terminal));
            }
            else if (terminal.State == ConversationState.Completed) routing.Succeeded(SetupRole.Llm);
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
    /// once no reply runs or speaks. A job with a picture follows <see cref="SenseJobRouting"/>'s order for
    /// <see cref="SenseJobKind.HelperPicture"/>: after the pool, the image model of its own while pictures go to it
    /// (<see cref="AskImageModelAsync"/>), else Thinking.</summary>
    internal async Task<(string? Answer, string? Failure)> AskHelperAsync(HelperJobKind kind, string purpose, string instructions,
        string text, BoundedImage? image, CancellationToken token)
    {
        var (answer, failure, _) = await helpers.RunAsync(kind, purpose, image is null ? HelperCapability.Text : HelperCapability.Vision,
            () => new BoundedTextInput(text, instructions, image: image),
            worker => image is not null && senseSession.SenseDecision(SenseJobKind.HelperPicture).After(SenseTaker.PoolMember)?.Taker == SenseTaker.SenseModel
                ? senseSession.AskImageModelAsync(kind, purpose, instructions, text, image, worker)
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
                // A screen or camera remark Martlet started on its own is dropped at once: whatever the verdict, it wouldn't play on.
                if (!decision.Cue && mode == PlaybackMode.Reply && DropAtOnce(operation, decision, startedAt, checks)) return;
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

    // ---------- speak, wait or drop for what Martlet says on its own (UnpromptedSpeech) ----------

    /// <summary>How many things Martlet meant to say on its own were dropped in this conversation, and why (counts and words only,
    /// never what they said): too old, the conversation moved on, or the user talked over them. <see cref="Last"/> describes the
    /// newest drop.</summary>
    internal sealed record UnpromptedDrops(int TooOld, int MovedOn, int TalkedOver, string? Last)
    {
        internal int Total => TooOld + MovedOn + TalkedOver;
    }

    private UnpromptedDrops unpromptedDrops = new(0, 0, 0, null);
    // When the last exchange that wasn't Martlet's own was kept (UTC ticks; 0: none yet), for "the conversation moved on".
    private long lastExchangeTicks;

    /// <summary>What Martlet dropped of what it meant to say on its own (<see cref="UnpromptedSpeech"/>).</summary>
    internal UnpromptedDrops DroppedOnItsOwn => Volatile.Read(ref unpromptedDrops);

    /// <summary>When the last exchange that wasn't Martlet's own (a reply to the user, to what this PC played or to a touch) was
    /// kept in the conversation, or null.</summary>
    internal DateTimeOffset? LastExchange => Interlocked.Read(ref lastExchangeTicks) is > 0 and var ticks ? new DateTimeOffset(ticks, TimeSpan.Zero) : null;

    private void NoteDropped(UnpromptedKind kind, string code, string why, string? id = null)
    {
        var at = clock.GetLocalNow().ToString("T", System.Globalization.CultureInfo.CurrentCulture);
        var last = $"{id ?? UnpromptedSpeech.Describe(kind)}{(id is null ? "" : " (" + UnpromptedSpeech.Describe(kind) + ")")} at {at}: {why}";
        UnpromptedDrops before, after;
        do
        {
            before = Volatile.Read(ref unpromptedDrops);
            after = code switch
            {
                "too_old" => before with { TooOld = before.TooOld + 1, Last = last },
                "moved_on" => before with { MovedOn = before.MovedOn + 1, Last = last },
                _ => before with { TalkedOver = before.TalkedOver + 1, Last = last }
            };
        }
        while (!ReferenceEquals(Interlocked.CompareExchange(ref unpromptedDrops, after, before), before));
    }

    /// <summary>Checks the notices that wait again (<see cref="UnpromptedSpeech.DropStale"/>) and drops a check-in that is too old
    /// or that the conversation moved on from, before Martlet says what waits on its own or with the user's next message. Local
    /// rules only: no request and no wait. Each drop goes to the desktop log with its ID, kind and why (never its text).</summary>
    internal int DropStaleNotices()
    {
        var dropped = UnpromptedSpeech.DropStale(jobs, clock.GetUtcNow(), LastExchange);
        foreach (var (job, check) in dropped)
        {
            var kind = UnpromptedSpeech.Of(job.Kind);
            NoteDropped(kind, check.Code, check.Why, job.Id);
            ErrorLog.Info($"Said on its own: Martlet dropped {job.Id} ({UnpromptedSpeech.Describe(kind)}) before saying it: {check.Why}.");
        }
        return dropped.Count;
    }

    // The finished background work and notices one reply takes, after stale notices were dropped.
    private BackgroundDelivery? TakeJobs(bool onItsOwn, bool noticesOnly = false)
    {
        DropStaleNotices();
        return jobs.Take(onItsOwn, noticesOnly);
    }

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
        UnpromptedKind? remark;
        lock (gate)
        {
            var reply = active is { Worker: not null, Playback: PlaybackMode.Reply } speaking && !speaking.OwnershipReleased ? speaking : null;
            turn = reply?.Turn;
            remark = reply?.Unprompted;
        }
        if (turn is null || !turn.Pause()) return null;
        var held = new HeldReply(new BargeInHold(clock), turn, decision, startedAt, checks, remark);
        operation.Held = held;
        ErrorLog.Info($"Barge-in: Martlet paused its {(remark is { } kind ? "remark (" + UnpromptedSpeech.Describe(kind) + ")" : "reply")} " +
            $"{clock.GetElapsedTime(startedAt).TotalMilliseconds:0} ms after you started " +
            $"talking over it ({decision.Reason}); the {CurrentJudge().Name} judge decides whether it stops or plays on.");
        Task.Run(() => WatchHoldAsync(operation, held)).Forget();
        return held;
    }

    // What Martlet is saying on its own right now (UnpromptedSpeech), or null for a reply or when it isn't speaking.
    private UnpromptedKind? ActiveRemark()
    {
        lock (gate)
            return active is { Worker: not null, Playback: PlaybackMode.Reply } speaking && !speaking.OwnershipReleased ? speaking.Unprompted : null;
    }

    // A remark that is never paused for the judge (a screen or camera remark: UnpromptedSpeech.Judged) is dropped at once when
    // real words are said over it, whatever they were. The talk window stops it as it stops a reply (TakeHeldStop). False when
    // Martlet isn't saying such a remark.
    private bool DropAtOnce(LiveConversationOperation operation, BargeInDecision decision, long startedAt, int checks)
    {
        if (ActiveRemark() is not { } remark || UnpromptedSpeech.Judged(remark)) return false;
        // A later quick check of the same words, before the talk window stopped it: dropped already.
        if (operation.TalkOver is { Dropped: true }) return true;
        var why = $"{UnpromptedSpeech.Describe(remark)} is dropped when you talk over it";
        Remember(new(clock.GetUtcNow(), BargeInSource.Judge, BargeInVerdict.Interrupt, $"{decision.Reason}; {why}", "none", TimeSpan.Zero,
            null, "dropped"));
        NoteDropped(remark, "talked over", "you talked over it");
        ErrorLog.Info($"Barge-in: Martlet dropped its remark ({UnpromptedSpeech.Describe(remark)}) " +
            $"{clock.GetElapsedTime(startedAt).TotalMilliseconds:0} ms after you started talking over it ({decision.Reason}; no judge: {why}).");
        var result = new TalkOverResult(decision, clock.GetElapsedTime(startedAt), checks, startedAt, Dropped: true);
        operation.TalkOver = result;
        Volatile.Write(ref heldStop, result);
        return true;
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
        // Words not for Martlet over what it said on its own: a reminder plays on, a report or check-in only after a short pause,
        // a remark never (UnpromptedSpeech.AfterNotForMe). The rest is dropped; a report's news waits for the next message.
        var drop = outcome == BargeInOutcome.Resume && held.Remark is { } remark &&
            UnpromptedSpeech.AfterNotForMe(remark, hold.Paused) == UnpromptedAfterTalkOver.Drop;
        Remember(new(clock.GetUtcNow(), source, outcome == BargeInOutcome.Stop ? BargeInVerdict.Interrupt : BargeInVerdict.NotForMe,
            why, ruling?.Judge ?? "none", ruling?.JudgeTime ?? TimeSpan.Zero, hold.Paused,
            outcome == BargeInOutcome.Stop ? "stopped" : drop ? "dropped" : "resumed"));
        if (outcome == BargeInOutcome.Resume && !drop)
        {
            held.Turn.Resume();
            ErrorLog.Info($"Barge-in: Martlet resumed its {(held.Remark is { } kind ? "remark (" + UnpromptedSpeech.Describe(kind) + ")" : "reply")} " +
                $"after a {hold.Paused.TotalMilliseconds:0} ms pause: what you said " +
                $"wasn't for it ({why}; {(source == BargeInSource.Limit ? "the pause reached its limit" : judged)}; " +
                $"{hold.Voice.TotalMilliseconds:0} ms of your voice during the pause).");
            return;
        }
        if (drop)
        {
            NoteDropped(held.Remark!.Value, "talked over", "you talked over it");
            ErrorLog.Info($"Barge-in: Martlet dropped its remark ({UnpromptedSpeech.Describe(held.Remark.Value)}) after a " +
                $"{hold.Paused.TotalMilliseconds:0} ms pause: what you said wasn't for it ({why}; " +
                $"{(source == BargeInSource.Limit ? "the pause reached its limit" : judged)}), but " +
                (held.Remark == UnpromptedKind.Remark ? "a remark never plays on after you talked over it)."
                    : $"it plays on only after a pause of at most {UnpromptedSpeech.ResumeWithin.TotalMilliseconds:0} ms)."));
        }
        var result = new TalkOverResult(held.Decision with { Reason = why }, clock.GetElapsedTime(held.StartedAt), held.Checks,
            held.StartedAt, ruling, hold.Paused, drop);
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
        var spokeAt = utterance.SpeechStartedAt == 0 ? clock.GetTimestamp() : utterance.SpeechStartedAt;
        if (!decision.Cue && mode == PlaybackMode.Reply && DropAtOnce(utterance, decision, spokeAt, 0)) return decision;
        if (!decision.Cue && options.BargeInStyle == BargeInBehavior.PauseAndDecide && mode == PlaybackMode.Reply &&
            Hold(utterance, decision, spokeAt, 0) is { } paused)
        {
            paused.Hold.Ended();
            await JudgeHeldAsync(utterance, paused, text, words, options, confidence).ConfigureAwait(false);
            return utterance.TalkOver?.Decision;
        }
        Remember(Immediate(decision));
        return decision;
    }

    /// <summary>The most of a turn Voice ID and voice recognition look at: a minute of 16 kHz PCM16, what the speaker engine
    /// takes at once.</summary>
    private const int VoiceCheckBytes = Martlet.Sherpa.SpeakerEngine.MaximumSeconds * 16_000 * 2;

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
            microphone = listeningSession.listener;
            playing = pcSession.pcListener;
        }
        owned?.Cancel("conversation.closed");
        StopMemoryUpkeep();
        StopMemoryBrief();
        StopMeaningSearch();
        StopRequests();
        Cancel(stopListening);
        echoReducer?.Forget();
        pcSession.DisposeWatchers();
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
        DisposeAfterListening(microphone, listeningSession.listenTranscription);
        DisposeAfterListening(playing, pcSession.pcTranscription);
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
            owned = [.. thinkSlots.Values.Select(slot => slot.Runtime), .. senseSession.senseSlots.Values.Select(slot => slot.Runtime)];
            song = conversationTools.songRuntime;
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
