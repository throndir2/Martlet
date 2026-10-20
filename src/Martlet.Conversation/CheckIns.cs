using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Martlet.Core.Settings;
using Martlet.Providers;
using static Martlet.Conversation.CheckInAnswers;
using static Martlet.Conversation.CheckInPrompts;
using static Martlet.Conversation.CheckInSchedule;
using static Martlet.Conversation.CheckInText;

namespace Martlet.Conversation;

/// <summary>What Martlet does with a check-in's answer.</summary>
public enum CheckInOutcome
{
    /// <summary>Turns off each lingering emote the answer names ("OFF {blush}").</summary>
    EmotesOff,
    /// <summary>Takes the character's eyes back to their usual gaze ("USUAL").</summary>
    GazeUsual,
    /// <summary>Puts the answer's "REMIND:" line in the notes of the next message, once, for the conversation model.</summary>
    Note,
    /// <summary>Brings the answer's "SAY:" line up on Martlet's own as soon as it is free, or with what the user says next.</summary>
    Say,
    /// <summary>Puts the answer's "KNOW:" line (a short description of what is happening) in the notes of the next message, once,
    /// as background the conversation model may draw on, not an instruction (<see cref="CheckIns.ContextAge"/>).</summary>
    Context = 4,
    /// <summary>Its tool calls are the action (<see cref="CheckIn.ToolSets"/>); the answer is only a short line that says what it
    /// did. The value is fixed, so other outcomes can take the values before it.</summary>
    Tools = 5,
    /// <summary>Puts the answer's "PLAN:" line (the character's own thoughts ahead: what the user will likely say next and what it
    /// means to say then) in the notes of the next message, once, while no newer exchange ended (<see cref="CheckIns.PlanAge"/>).</summary>
    Plan = 6,
    /// <summary>An "ASIDE:" line (the user talks to someone else, not to Martlet: who and how, in a few words) makes Martlet hold
    /// back while that side conversation lasts (<see cref="CheckInAside"/>): every request carries a note to stay quiet ([pass])
    /// unless the user talks to it. "BACK" ends it.</summary>
    HoldBack = 7,
    /// <summary>The answer's "CALLBACK:" lines become the conversation's callback list (<see cref="CallbackList"/>): the few
    /// things worth bringing back later, which Think ahead and Quiet moments read. "KEEP" leaves it, "NONE" empties it.</summary>
    Callbacks = 8
}

/// <summary>What a check-in gets to know besides the day and time. A fact whose placeholder the prompt names ({conversation},
/// {emotes}...) goes there; the other facts ticked go in {facts}.</summary>
[Flags]
public enum CheckInFacts
{
    None = 0,
    /// <summary>The end of the conversation and how long it has been quiet.</summary>
    Conversation = 1,
    /// <summary>The lingering emotes a reply turned on and where the character's eyes look.</summary>
    Character = 2,
    /// <summary>The character's name and personality.</summary>
    Persona = 4,
    /// <summary>The reminders set and the background work of this conversation.</summary>
    Work = 8,
    /// <summary>What Martlet last saw change on the screen, while it watches.</summary>
    Screen = 16,
    /// <summary>What this PC plays, while Martlet hears it.</summary>
    Sound = 32,
    /// <summary>How long since someone last used this PC.</summary>
    Presence = 64,
    /// <summary>What the character said in the last hour (replies, remarks, reactions), each with when.</summary>
    Said = 128,
    /// <summary>The character's last replies, numbered, oldest first.</summary>
    Replies = 256,
    /// <summary>What the user did to the desktop character lately (pokes, pats, holds, strokes with their path, moves), each
    /// with when, which were intimate, how the persona feels about them and the places the user keeps coming back to; read
    /// without taking it, so the next reply still gets the touches.</summary>
    Touches = 512,
    /// <summary>What the user seems to be doing on this PC (a game, a call, a video, full screen), while Martlet hears what it
    /// plays, and what changed last (<see cref="CheckInState.Activity"/>).</summary>
    Activity = 1024,
    /// <summary>Who Martlet heard lately (Voice ID): the names of the voices it knows and how many it doesn't, each with when.</summary>
    People = 2048,
    /// <summary>What happened while the user was away from this PC: what Martlet said, the background work that finished and the
    /// reminders that came due (<see cref="CheckInState.WhileAway"/>).</summary>
    WhileAway = 4096,
    /// <summary>The newest exchange of the conversation in full (what the user said and the character's reply), or every exchange
    /// since this check-in last ran (at most <see cref="CheckIns.MaximumLatest"/>), so a check-in that waited its pace misses
    /// nothing (<see cref="CheckInState.Since"/>).</summary>
    Latest = 8192,
    /// <summary>What always listening heard the user say lately, each with when, who spoke, the other voices and the sound of this
    /// PC with it, and whether Martlet answered or stayed quiet; and whether Martlet holds back now (<see cref="CheckInState.Heard"/>,
    /// <see cref="CheckInState.Aside"/>).</summary>
    Heard = 16384,
    /// <summary>The conversation's callback list: open topics, running jokes, the user's own words and facts worth bringing back
    /// later, each with when it came up (<see cref="CheckInState.Callbacks"/>).</summary>
    Callbacks = 32768
}

/// <summary>When a check-in runs: each condition chosen must hold, or it waits and says why on its card.</summary>
[Flags]
public enum CheckInConditions
{
    None = 0,
    /// <summary>The character shows on the desktop.</summary>
    CharacterShows = 1,
    /// <summary>A reply turned on an emote that still shows, for at least <see cref="CheckIns.MinimumShown"/> (only those count).</summary>
    EmoteShown = 2,
    /// <summary>A reply chose where the eyes look, at least <see cref="CheckIns.MinimumShown"/> ago.</summary>
    GazeChosen = 4,
    /// <summary>Something was said in the conversation, within <see cref="CheckIns.PromiseWindow"/>.</summary>
    Talked = 8,
    /// <summary>Something new was said since the last check.</summary>
    SomethingNew = 16,
    /// <summary>A personality is active.</summary>
    Persona = 32,
    /// <summary>The character gave at least 2 replies.</summary>
    Replies = 64,
    /// <summary><see cref="CheckIns.CharacterReplies"/> new exchanges since the last check.</summary>
    NewReplies = 128,
    /// <summary>The character said at least <see cref="CheckIns.RepeatsSayings"/> things in the last hour.</summary>
    Sayings = 256,
    /// <summary>After an answer that kept everything, with nothing new said since, it waits <see cref="CheckIns.KeptPace"/> times
    /// as long.</summary>
    SlowWhenKept = 512,
    /// <summary>Someone came back to this PC after <see cref="CheckIns.Idle"/> or more away, within <see cref="CheckIns.SignalWindow"/>,
    /// and it didn't run since.</summary>
    CameBack = 1024,
    /// <summary>The user isn't in a call or a voice chat (as far as Martlet can tell from what this PC plays).</summary>
    NotOnCall = 2048,
    /// <summary>What the user does changed since the last run: a game, a call or a full-screen app started or ended.</summary>
    ActivityChanged = 4096,
    /// <summary>Only between <see cref="CheckIn.FromHour"/> and <see cref="CheckIn.UntilHour"/> (local time).</summary>
    Between = 8192,
    /// <summary>Martlet's last remark asked the user something, the user is at the PC and didn't answer for
    /// <see cref="CheckIns.UnansweredAfter"/>; once for each question.</summary>
    Unanswered = 16384,
    /// <summary>A taskbar button flashed or a notification showed since the last run.</summary>
    Attention = 32768,
    /// <summary>A song Martlet sang played to its end since the last run.</summary>
    SongEnded = 65536,
    /// <summary>What its script printed changed since the last run; when it didn't, the run ends without asking the Thinking pool.</summary>
    ScriptChanged = 131072,
    /// <summary>Martlet heard someone else's voice lately (Voice ID), since the last run.</summary>
    SomeoneElse = 262144,
    /// <summary>The conversation went quiet: nothing for <see cref="CheckIns.LullAfter"/> (at most
    /// <see cref="CheckIns.PromiseWindow"/>), the user is at the PC, the callback list has something, Martlet's last remark
    /// asked nothing, and it didn't run in this quiet spell.</summary>
    Lull = 524288
}

/// <summary>What one of the owner's own check-ins hears with each run: nothing, the last seconds of the microphone, or the last
/// seconds of what this PC plays.</summary>
public enum CheckInRecording { None, Microphone, PcSound }

/// <summary>One check-in as Martlet runs it: a built-in one (its prompt is edited on its card on Companion › Check-ins) or one of
/// the owner's own (<see cref="Custom"/>: its <see cref="Task"/>). Both run the same way: the <see cref="Facts"/> they get, the
/// <see cref="Conditions"/> they wait for, what they gather for each run and what happens with the answer
/// (<see cref="CheckIn.Outcome"/>).</summary>
public sealed partial record CheckIn(string Id, string Name, string Does, CheckInOutcome Outcome, bool On, int EveryMinutes)
{
    public bool Custom { get; init; }
    /// <summary>A built-in check-in's prompt (on its card on Companion › Check-ins, and on Companion › Prompts).</summary>
    public string? PromptId { get; init; }
    /// <summary>What the owner wrote for their own check-in.</summary>
    public string? Task { get; init; }
    public CheckInFacts Facts { get; init; }
    public CheckInConditions Conditions { get; init; }
    /// <summary>What a Thinking pool member must handle to take the check-in: text, and pictures or recordings when the owner
    /// asks for them or the check-in sends a screenshot or a recording, and tool calls when it has <see cref="ToolSets"/> that
    /// aren't all <see cref="CheckInToolSet.Optional"/>.</summary>
    public ThinkingCapability Needs
    {
        get => needs | (CheckInToolSets.NeedTools(ToolSets) ? ThinkingCapability.Tools : ThinkingCapability.None);
        init => needs = value;
    }
    private readonly ThinkingCapability needs = ThinkingCapability.Text;
    /// <summary>The tool sets it may call (<see cref="CheckInToolSets"/> IDs); empty: none. Compared by value.</summary>
    public IReadOnlyList<string> ToolSets { get => toolSets; init => toolSets = value is null ? CheckInToolSetIds.Empty : new(value); }
    private readonly CheckInToolSetIds toolSets = CheckInToolSetIds.Empty;
    /// <summary>A screenshot goes with each run.</summary>
    public bool Screenshot { get; init; }
    /// <summary>A recording of the last <see cref="RecordingSeconds"/> goes with each run.</summary>
    public CheckInRecording Recording { get; init; }
    public int RecordingSeconds { get; init; } = 10;
    /// <summary>The owner's PowerShell script, run before each run; null or empty: none.</summary>
    public string? Script { get; init; }
    public bool RunsScript => !string.IsNullOrWhiteSpace(Script);
    public TimeSpan Every => TimeSpan.FromMinutes(EveryMinutes);
    /// <summary>What starts it at once (<see cref="CheckInTriggers"/>). With one or more, it runs only when one fires, at most
    /// once per <see cref="Every"/>; with none, it runs on its pace.</summary>
    public CheckInTriggers Triggers { get; init; }
    /// <summary>Where it runs in the Thinking pool (Runs on, on its card); null: as the check-in kind runs.</summary>
    public ThinkingRunsOn? RunsOn { get; init; }
}

/// <summary>One reply tool a check-in takes over (<see cref="CheckIns.HandOffs"/>): the tool, the check-in that does it after
/// the reply and the tool set that does it.</summary>
public sealed record CheckInHandOff(string Tool, string CheckInId, string CheckInName, string SetId, string SetName);

/// <summary>One exchange of the conversation: what the user said and what the character answered.</summary>
public sealed record CheckInExchange(string User, string Martlet);

/// <summary>A lingering emote a reply turned on: its tag (without braces), its When to use hint and how long it has shown.</summary>
public sealed record CheckInEmote(string Tag, string Hint, TimeSpan Shown);

/// <summary>Where the character's eyes are while a reply's choice holds them: what they do now and usually, in words ("look
/// straight ahead and ignore the pointer"), and how long ago the reply chose it.</summary>
public sealed record CheckInGaze(string Looking, string Usual, TimeSpan Since);

/// <summary>What Martlet knows on this PC when a check-in may run (the desktop gathers it on its UI thread). It is private: it goes
/// only to a Thinking pool member with the check, never to logs or status files.</summary>
public sealed partial record CheckInState
{
    /// <summary>The local time.</summary>
    public required DateTimeOffset Now { get; init; }
    /// <summary>The active personality's name, or null (the character is then called Martlet).</summary>
    public string? Name { get; init; }
    public string? Persona { get; init; }
    /// <summary>A conversation runs on this PC (with the talk window or without it).</summary>
    public bool Conversation { get; init; }
    /// <summary>The newest exchanges of that conversation, oldest first.</summary>
    public IReadOnlyList<CheckInExchange> Exchanges { get; init; } = [];
    /// <summary>How many exchanges the conversation has had in all: a count that only grows, so a check-in can tell whether
    /// anything new was said since it last ran.</summary>
    public long Exchanged { get; init; }
    /// <summary>How many exchanges the conversation had when this check-in last ran (<see cref="CheckInRun.Exchanged"/>), or null
    /// before its first run: <see cref="CheckInFacts.Latest"/> reads the exchanges since then.</summary>
    public long? Since { get; init; }
    /// <summary>How long since the conversation last did something (the user spoke or typed, or a reply finished); zero while
    /// the user talks, null before anything.</summary>
    public TimeSpan? Quiet { get; init; }
    /// <summary>How long since someone last used this PC (keyboard, mouse or talking with Martlet here).</summary>
    public TimeSpan? Away { get; init; }
    public bool CharacterShows { get; init; }
    /// <summary>The lingering emotes a reply turned on (never the owner's tries or a touch's).</summary>
    public IReadOnlyList<CheckInEmote> Emotes { get; init; } = [];
    public CheckInGaze? Gaze { get; init; }
    /// <summary>The reminders set and the background work of this conversation, one short line each.</summary>
    public IReadOnlyList<string> Work { get; init; } = [];
    /// <summary>The newest summary of what changed on the screen (the context board's screen note), or null.</summary>
    public string? Screen { get; init; }
    /// <summary>The newest line about what this PC plays (the context board's sound note), or null.</summary>
    public string? Sound { get; init; }
    /// <summary>What the character said lately (replies, remarks, reactions; never a [pass]), oldest first, each with when
    /// (<see cref="SaidLately"/>).</summary>
    public IReadOnlyList<Saying> Said { get; init; } = [];
    /// <summary>The conversation's callback list, oldest first, each with when it came up (<see cref="CallbackList"/>).</summary>
    public IReadOnlyList<Callback> Callbacks { get; init; } = [];
    /// <summary>Martlet keeps the last seconds of the microphone now (it hears it, and a check-in that is on asks for them).</summary>
    public bool HearsMicrophone { get; init; }
    /// <summary>Martlet keeps the last seconds of what this PC plays now.</summary>
    public bool HearsPc { get; init; }
    /// <summary>The screenshot taken for this run of one of the owner's check-ins, or null.</summary>
    public BoundedImage? Screenshot { get; init; }
    /// <summary>The recording taken for this run of one of the owner's check-ins, or null.</summary>
    public BoundedWaveAudio? Recording { get; init; }
    /// <summary>What the owner's script printed for this run (or why it didn't run), or null.</summary>
    public string? ScriptOutput { get; init; }
    /// <summary>What the user did to the desktop character over the last <see cref="TouchLedger.OftenWindow"/>, from the
    /// conversation's touch ledger, read without taking it (<see cref="TouchLedger.History"/>); null when they did nothing.</summary>
    public TouchHistory? Touches { get; init; }
    /// <summary>Companion › Replies › Adult content is on: {adult} fills with Check-in: adult content on, else with its off
    /// line.</summary>
    public bool Adult { get; init; }

    /// <summary>The character's name for the check: the personality's, or Martlet.</summary>
    public string Who => string.IsNullOrWhiteSpace(Name) ? "Martlet" : Name.Trim();
}

/// <summary>The last time a check-in ran on this PC: when, how many exchanges the conversation had then, what came of it in a few
/// words (never what was said), whether Martlet acted on it, the pool member that answered and how long it took.
/// <see cref="Kept"/>: the answer was read and asked for no change (KEEP, OK). <see cref="Gathered"/>: what one of the owner's
/// check-ins took with it, in a few words (never the screenshot, the recording or what the script printed).</summary>
public sealed partial record CheckInRun(DateTimeOffset At, long Exchanged, string Result, bool Acted)
{
    public string? Member { get; init; }
    public TimeSpan? Took { get; init; }
    public bool Kept { get; init; }
    public string? Gathered { get; init; }
    /// <summary>The tool calls it made, in order (none without tool sets).</summary>
    public IReadOnlyList<CheckInToolUse> Tools { get; init; } = [];
    /// <summary>What started this run, in a few words (<see cref="CheckInTrigger.What"/>), or null when its pace or Check now did.</summary>
    public string? Trigger { get; init; }
}

/// <summary>What a check-in's answer asks for: <see cref="Act"/> with the emote <see cref="Tags"/> to turn off (or the items of
/// the new callback list), or the <see cref="Text"/> of a reminder or of what to bring up; nothing (KEEP, OK); or nothing because
/// the answer couldn't be read (<see cref="Readable"/> false).</summary>
public sealed record CheckInVerdict(bool Act, IReadOnlyList<string> Tags, string? Text, bool Readable)
{
    public static CheckInVerdict Nothing { get; } = new(false, [], null, true);
    public static CheckInVerdict Unreadable { get; } = new(false, [], null, false);
    public override string ToString() => $"{nameof(CheckInVerdict)} (act: {Act}, readable: {Readable})";
}

/// <summary>
/// Check-ins (Companion › Check-ins; docs/CONVERSATION.md#check-ins): short questions the Thinking pool answers for Martlet
/// every few minutes, each with the facts that matter for it, because a small conversation model forgets what it left on.
/// Built in: <see cref="Emotes"/> (do the lingering emotes a reply turned on still fit? Martlet turns off those that don't),
/// <see cref="Gaze"/> (does the gaze a reply chose still fit? Martlet takes the eyes back to their usual), <see cref="Promises"/>
/// (did the character say it would do something it never started?), <see cref="Character"/> (did its last replies drift from
/// its personality?) and <see cref="Repeats"/> (does it keep saying the same things?), whose answers become a reminder in the
/// notes of the next message. The owner adds their own: a task, the
/// facts it gets and whether its answer reminds the character in its next reply or is brought up on Martlet's own. A check-in
/// runs only on a Thinking pool member (<see cref="ThinkingJobKind.CheckIn"/>, never the conversation's own Thinking route),
/// one at a time, when it is due and has something to check (<see cref="Wait"/>).
/// </summary>
public static partial class CheckIns
{
    public const string Emotes = "emotes", Gaze = "gaze", Promises = "promises", Character = "character", Repeats = "repeats",
        Reactions = "reactions";
    /// <summary>Act on what was said: after each exchange, a pool member calls the tools for what the user asked for or Martlet
    /// promised, so the reply model needs fewer tools (<see cref="CheckInToolSet.Replaces"/>, <see cref="HandOffs"/>).</summary>
    public const string Actions = "actions";
    /// <summary>How many exchanges <see cref="CheckInFacts.Latest"/> shows at most.</summary>
    public const int MaximumLatest = 6;
    /// <summary>Describe touches: right after the user touches the character, a pool member describes what they have been doing,
    /// for the next reply (<see cref="CheckInOutcome.Context"/>).</summary>
    public const string DescribeTouches = "touches";
    /// <summary>Think ahead: right after each reply, while the user hasn't answered yet, a pool member thinks ahead as the
    /// character (how the conversation goes, what the user will likely say next, what it means to say then), for the next reply
    /// (<see cref="CheckInOutcome.Plan"/>).</summary>
    public const string Ahead = "ahead";
    /// <summary>Callback list: right after each reply, a pool member writes the conversation's callback list again with what was
    /// just said (<see cref="CheckInOutcome.Callbacks"/>, <see cref="CallbackList"/>), for Think ahead and Quiet moments.</summary>
    public const string Callbacks = "callbacks";
    /// <summary>Quiet moments: when the conversation went quiet for a few minutes and the user is still at the PC, Martlet may
    /// pick it back up with one thing from the callback list (<see cref="CheckInConditions.Lull"/>, <see cref="CheckInOutcome.Say"/>).</summary>
    public const string Lull = "lull";
    /// <summary>The background job kind of what a check-in brings up on Martlet's own (checkin-1...).</summary>
    public const string SayKindName = "checkin";
    /// <summary>Memory care: every hour while something new was said, a pool member looks after Martlet's long-term memory in
    /// several steps with the Memory tools: facts that don't say which game, app or thing they are about, facts saved to the wrong
    /// person, duplicates, outdated facts and facts the conversation showed are wrong (<see cref="CheckInOutcome.Tools"/>).</summary>
    public const string MemoryCare = "memory";
    public const int MaximumCustom = 8, MaximumNameCharacters = 60, MaximumTaskCharacters = PromptSettings.MaximumTextCharacters,
        MaximumAnswerCharacters = 300;
    /// <summary>How many new exchanges Staying in character waits for between two checks.</summary>
    public const int CharacterReplies = 4;
    /// <summary>How many things the character must have said in the last hour before Saying the same things reads them.</summary>
    public const int RepeatsSayings = 3;
    /// <summary>How long an emote or a gaze a reply chose shows before a check-in looks at it (unless the owner asks now).</summary>
    public static TimeSpan MinimumShown => TimeSpan.FromMinutes(3);
    /// <summary>How long a check-in waits after the conversation last did something, so it never races a reply.</summary>
    public static TimeSpan Settle => TimeSpan.FromSeconds(10);
    /// <summary>Promises reads a conversation only while it was active within this time.</summary>
    public static TimeSpan PromiseWindow => TimeSpan.FromMinutes(30);
    /// <summary>Check-ins wait while nobody used this PC for this long, so the Thinking pool isn't asked again and again while
    /// nobody is there.</summary>
    public static TimeSpan Idle => TimeSpan.FromMinutes(10);
    /// <summary>How long a reminder for the next reply waits on the context board for a message to carry it.</summary>
    public static TimeSpan NoteAge => TimeSpan.FromMinutes(30);
    /// <summary>How long what a check-in adds to what Martlet knows waits on the context board for a message to carry it: short,
    /// because it describes the moment.</summary>
    public static TimeSpan ContextAge => TimeSpan.FromMinutes(3);
    /// <summary>How long a check-in's thoughts ahead wait on the context board for the next message. A newer exchange drops them
    /// before that, because they were about the message after the exchange they read.</summary>
    public static TimeSpan PlanAge => TimeSpan.FromMinutes(10);
    /// <summary>How many times its pace Lingering emotes and Where the character looks wait after an answer that kept everything,
    /// while nothing new was said.</summary>
    public const int KeptPace = 3;
    /// <summary>How long a check-in may wait for a free member and run, after which it is dropped.</summary>
    public static TimeSpan Timeout => TimeSpan.FromMinutes(2);
    public const int MaximumOutputTokens = 600;
    /// <summary>How often a check-in may run: every minute to every 2 hours.</summary>
    public static IReadOnlyList<int> EveryChoices { get; } = [1, 2, 5, 10, 15, 30, 60, 120];
    /// <summary>How long a check-in's recording may be, in seconds (at most a minute: the PC sound buffers keep no more).</summary>
    public static IReadOnlyList<int> RecordingChoices { get; } = [5, 10, 15, 30, 60];
    /// <summary>The words for <see cref="EveryChoices"/>: "1, 2, 5, 10, 15, 30, 60 or 120".</summary>
    internal static string EveryChoicesText => $"{string.Join(", ", EveryChoices.SkipLast(1))} or {EveryChoices[^1]}";
    public const int MaximumScriptCharacters = 4_000, MaximumScriptOutputCharacters = 4_000;
    /// <summary>How long the owner's script may run before Martlet stops it.</summary>
    public static TimeSpan ScriptTimeout => TimeSpan.FromSeconds(20);
    /// <summary>A run with tool sets works in several steps, as long as it needs, until it is done: at most this many tool rounds
    /// (requests that call tools, each reading what the calls before it returned) before the model must answer, and at most this
    /// many calls in all; later calls get an error.</summary>
    public const int MaximumToolRounds = 16, MaximumToolCalls = 64;
    /// <summary>What one request of a run with tools may write (tokens): room for many tool calls in one round.</summary>
    public const int MaximumToolOutputTokens = 2_048;
    /// <summary>How long a run with tools may wait for a free member and work through its steps, after which it is dropped.</summary>
    public static TimeSpan ToolTimeout => TimeSpan.FromMinutes(8);
    /// <summary>How much of a tool's answer the run record keeps (its first line).</summary>
    public const int MaximumToolResultCharacters = 120;

    /// <summary>The built-in check-ins with their defaults: the first five on, every 5 minutes (Lingering emotes every minute, Saying the same things every 10,
    /// Staying in character every 15); How I react on, after your touches (at most every 5 minutes); Unanswered question and
    /// Quiet moments on; Welcome back and Someone else is here off until the owner turns them on; Talking to someone else on, right after always
    /// listening hears the user, at most once a minute; Describe touches on, started by touches at
    /// most once a minute; Think ahead, Act on what was said and Callback list on, after each reply, at most once a minute. Each is only data (a
    /// prompt, facts, conditions, triggers, tool sets and an outcome) that the owner can
    /// change on its card or copy as their own.</summary>
    public static IReadOnlyList<CheckIn> BuiltIn { get; } =
    [
        new(Emotes, "Lingering emotes", "Checks whether the emotes a reply turned on and left on (such as a blush or glasses) still " +
            "fit the moment, and turns off those that don't.", CheckInOutcome.EmotesOff, true, 1)
        {
            PromptId = PromptCatalog.CheckInEmotes, Facts = CheckInFacts.Character | CheckInFacts.Conversation,
            Conditions = CheckInConditions.CharacterShows | CheckInConditions.EmoteShown | CheckInConditions.SlowWhenKept
        },
        new(Gaze, "Where the character looks", "Checks whether the gaze a reply chose (such as looking straight ahead) still fits, " +
            "and takes the eyes back to their usual when it doesn't.", CheckInOutcome.GazeUsual, true, 5)
        {
            PromptId = PromptCatalog.CheckInGaze, Facts = CheckInFacts.Character | CheckInFacts.Conversation,
            Conditions = CheckInConditions.CharacterShows | CheckInConditions.GazeChosen | CheckInConditions.SlowWhenKept
        },
        new(Promises, "Promises", "Reads the end of the conversation for something Martlet said it would do (a reminder, thinking it " +
            "over, a song) but never started, and reminds it in its next reply.", CheckInOutcome.Note, true, 5)
        {
            PromptId = PromptCatalog.CheckInPromises, Facts = CheckInFacts.Conversation | CheckInFacts.Work,
            Conditions = CheckInConditions.Talked | CheckInConditions.SomethingNew
        },
        new(Character, "Staying in character", "Reads Martlet's last replies against its personality and, when they drift (out " +
            "of character, saying the same things, too long), reminds it how to talk in its next reply.", CheckInOutcome.Note, true, 15)
        {
            PromptId = PromptCatalog.CheckInCharacter, Facts = CheckInFacts.Persona | CheckInFacts.Replies,
            Conditions = CheckInConditions.Persona | CheckInConditions.Replies | CheckInConditions.NewReplies
        },
        new(Repeats, "Saying the same things", "Reads what Martlet said in the last hour, each with when it said it, and when it " +
            "keeps saying the same things (the same remark, joke or question again and again), reminds it in its next reply to " +
            "say something new.", CheckInOutcome.Note, true, 10)
        {
            PromptId = PromptCatalog.CheckInRepeats, Facts = CheckInFacts.Said,
            Conditions = CheckInConditions.Sayings | CheckInConditions.SomethingNew
        },
        new(Reactions, "How I react", "After your touches, reads the conversation and how you touched the character, and when its " +
            "feelings toward you changed (it got angry or hurt, or it warmed up to you), changes how it reacts to your touches for " +
            "a while with its Touch reactions tools. See and undo its changes on Companion › Touch.", CheckInOutcome.Tools, true, 5)
        {
            PromptId = PromptCatalog.CheckInReactions, Facts = CheckInFacts.Persona | CheckInFacts.Conversation | CheckInFacts.Touches,
            Conditions = CheckInConditions.Persona, Triggers = ByTouches, ToolSets = [TouchReactions.SetId]
        },
        new(Welcome, "Welcome back", "When you come back to the PC after 10 minutes or more away, has Martlet welcome you back " +
            "briefly, with what happened while you were away when it matters.", CheckInOutcome.Say, false, 30)
        {
            PromptId = PromptCatalog.CheckInWelcome, Facts = CheckInFacts.WhileAway | CheckInFacts.Activity | CheckInFacts.Persona,
            Conditions = CheckInConditions.CameBack
        },
        new(Unanswered, "Unanswered question", "When Martlet asked you something, you're at the PC and didn't answer for a few " +
            "minutes, has it follow up once, softly, or let it go.", CheckInOutcome.Say, true, 5)
        {
            PromptId = PromptCatalog.CheckInUnanswered, Facts = CheckInFacts.Conversation | CheckInFacts.Said | CheckInFacts.Activity,
            Conditions = CheckInConditions.Unanswered | CheckInConditions.NotOnCall
        },
        new(Lull, "Quiet moments", "When your conversation has been quiet for a few minutes and you're still at the PC, lets " +
            "Martlet pick it back up the way a friend does: one thing from its callback list (something you said, a running joke, " +
            "something you're in the middle of) with a fresh question or a tease. Not while you're on a call, not at night and at " +
            "most twice an hour.", CheckInOutcome.Say, true, 10)
        {
            PromptId = PromptCatalog.CheckInLull,
            Facts = CheckInFacts.Persona | CheckInFacts.Conversation | CheckInFacts.Callbacks | CheckInFacts.Said | CheckInFacts.Activity,
            Conditions = CheckInConditions.Lull | CheckInConditions.NotOnCall | CheckInConditions.Between, MostPerHour = 2
        },
        new(Aside, "Talking to someone else", "Right after always listening hears you, checks whether you're talking to someone " +
            "else and not to Martlet: someone in the room, a call on this PC, or a phone Martlet can't see (it then hears only your " +
            "side). While that goes on, Martlet holds back and stays quiet, and it talks again when you talk to it or that " +
            "conversation ends. No reply waits for it.", CheckInOutcome.HoldBack, true, 1)
        {
            PromptId = PromptCatalog.CheckInAside,
            Facts = CheckInFacts.Heard | CheckInFacts.Conversation | CheckInFacts.Activity | CheckInFacts.People,
            Conditions = CheckInConditions.SomethingNew, Triggers = CheckInTriggers.Spoke,
            // Who the voices are, when a member calls tools; it runs without them otherwise (an optional set).
            ToolSets = [PeopleTools.SetId]
        },
        new(Others, "Someone else is here", "When Martlet hears a voice that isn't yours, reminds it in its next reply not to share " +
            "private things it knows about you in front of others.", CheckInOutcome.Note, false, 10)
        {
            PromptId = PromptCatalog.CheckInOthers, Facts = CheckInFacts.People | CheckInFacts.Persona,
            Conditions = CheckInConditions.SomeoneElse, ToolSets = [PeopleTools.SetId]
        },
        new(DescribeTouches, "Describe touches", "Right after you touch the character, describes vividly what you have been doing " +
            "to it, true to its personality, so Martlet's next reply can draw on it. The touch reaction never waits for it.",
            CheckInOutcome.Context, true, 1)
        {
            PromptId = PromptCatalog.CheckInTouches,
            Facts = CheckInFacts.Touches | CheckInFacts.Conversation | CheckInFacts.Persona | CheckInFacts.Character,
            Conditions = CheckInConditions.CharacterShows, Triggers = CheckInTriggers.TouchesEnded
        },
        new(Ahead, "Think ahead", "Right after each reply, while you haven't answered yet, thinks ahead as the character: how " +
            "the conversation is going, what you'll likely say next and the one move it makes then (a callback to something " +
            "from earlier, a tease, a question), true to its personality. Its next reply gets these thoughts and uses them " +
            "where they fit. No reply waits for it.", CheckInOutcome.Plan, true, 1)
        {
            PromptId = PromptCatalog.CheckInAhead, Facts = CheckInFacts.Persona | CheckInFacts.Conversation | CheckInFacts.Callbacks,
            Conditions = CheckInConditions.Talked | CheckInConditions.SomethingNew, Triggers = CheckInTriggers.ExchangeEnded
        },
        new(Actions, "Act on what was said", "Right after each reply, reads what you said and what Martlet answered, and does " +
            "what you asked for or Martlet promised with its tools (such as setting a reminder), so Martlet's reply can stay " +
            "short and quick. While it's on and a Thinking pool member calls tools, the reply leaves those tools to it.",
            CheckInOutcome.Tools, true, 1)
        {
            PromptId = PromptCatalog.CheckInActions, Facts = CheckInFacts.Latest | CheckInFacts.Work,
            Conditions = CheckInConditions.SomethingNew, Triggers = CheckInTriggers.ExchangeEnded, ToolSets = ActionSets
        },
        new(Callbacks, "Callback list", "Right after each reply, keeps a short list of what's worth bringing back later: what " +
            "you're in the middle of, running jokes, things you said in your own words, each with when it came up. Think ahead " +
            "and Quiet moments use it, so Martlet can call back to something from much earlier. No reply waits for it.",
            CheckInOutcome.Callbacks, true, 1)
        {
            PromptId = PromptCatalog.CheckInCallbacks, Facts = CheckInFacts.Latest | CheckInFacts.Callbacks,
            Conditions = CheckInConditions.SomethingNew, Triggers = CheckInTriggers.ExchangeEnded
        },
        new(MemoryCare, "Memory care", "Every hour, while something new was said, looks after what Martlet remembers, in as many " +
            "steps as it takes: it adds which game, app or thing a fact is about when the fact doesn't say, gives facts to the " +
            "right person, merges facts that say the same thing, keeps facts a newer one replaced as history and fixes facts the " +
            "conversation showed are wrong. Every correction keeps the earlier version as history. It needs memory on and a " +
            "Thinking pool member that calls tools.", CheckInOutcome.Tools, true, 60)
        {
            PromptId = PromptCatalog.CheckInMemory, Facts = CheckInFacts.Latest | CheckInFacts.Activity,
            Conditions = CheckInConditions.SomethingNew, ToolSets = [MemoryToolSet.Id]
        }
    ];

    /// <summary>The tool sets Act on what was said uses by default: every set that takes over reply tools
    /// (<see cref="CheckInToolSet.Replaces"/> not empty), in the order of <see cref="CheckInToolSets.All"/>. Computed, so a new
    /// set that replaces reply tools joins it without a change here.</summary>
    public static IReadOnlyList<string> ActionSets => [.. CheckInToolSets.All.Where(s => s.Replaces.Count > 0).Select(s => s.Id)];

    /// <summary>Whether <paramref name="checkIn"/> runs after exchanges and takes over reply tools: an
    /// <see cref="CheckInTriggers.ExchangeEnded"/> trigger and at least one set that replaces reply tools.</summary>
    public static bool ActsAfterExchanges(CheckIn checkIn)
    {
        ArgumentNullException.ThrowIfNull(checkIn);
        return checkIn.Triggers.HasFlag(CheckInTriggers.ExchangeEnded) &&
            checkIn.ToolSets.Any(id => CheckInToolSets.Find(id) is { Replaces.Count: > 0 });
    }

    /// <summary>The reply tools the check-ins take over (docs/CONVERSATION.md#check-ins), each with the check-in and the set:
    /// those a set names in <see cref="CheckInToolSet.Replaces"/> when a check-in that is on, starts after each exchange and has a
    /// prompt ticks it, and only while <paramref name="poolCallsTools"/> (the configured Thinking pool has a member that takes
    /// check-ins and calls tools, whether its computer answers now or not). From saved settings only, so the reply's tools stay
    /// the same while computers come and go. Empty: the reply keeps every tool.</summary>
    public static IReadOnlyList<CheckInHandOff> HandOffs(CheckInSettings? settings, PromptSettings? prompts, bool poolCallsTools) =>
        HandOffs(settings, prompts, () => poolCallsTools);

    /// <summary><see cref="HandOffs(CheckInSettings?, PromptSettings?, bool)"/> that asks <paramref name="poolCallsTools"/> only
    /// when a check-in would take over a reply tool.</summary>
    public static IReadOnlyList<CheckInHandOff> HandOffs(CheckInSettings? settings, PromptSettings? prompts, Func<bool> poolCallsTools)
    {
        ArgumentNullException.ThrowIfNull(poolCallsTools);
        var handed = new List<CheckInHandOff>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var checkIn in All(settings))
        {
            if (!checkIn.On || !ActsAfterExchanges(checkIn) || string.IsNullOrWhiteSpace(Template(checkIn, prompts))) continue;
            foreach (var id in checkIn.ToolSets)
                if (CheckInToolSets.Find(id) is { } set)
                    foreach (var tool in set.Replaces)
                        if (seen.Add(tool)) handed.Add(new(tool, checkIn.Id, checkIn.Name, set.Id, set.Name));
        }
        return handed.Count > 0 && poolCallsTools() ? handed : [];
    }

    /// <summary>What the reply's instructions say about the tools handed off (Every reply › Things done after the reply): the
    /// sets' names separated by semicolons, or null when none are handed off or the owner emptied that prompt.</summary>
    public static string? HandOffGuidance(IReadOnlyList<CheckInHandOff> handed, PromptSettings? prompts)
    {
        ArgumentNullException.ThrowIfNull(handed);
        // Set names may hold commas ("Songs, pictures and creations"), so semicolons separate them.
        var names = handed.Select(h => h.SetName).Distinct(StringComparer.Ordinal).ToArray();
        if (names.Length == 0) return null;
        var things = string.Join("; ", names);
        return PromptSettings.Fill(prompts, PromptCatalog.HandedOffTools, ("things", things));
    }

    /// <summary>The background job kind that brings up what a check-in said to bring up: a notice, always brought up as soon as
    /// Martlet is free (or with what the user says next), with the Check-in: brought up prompts.</summary>
    public static BackgroundJobKind SayKind { get; } = new(SayKindName, 4, 30, TimeSpan.FromMinutes(1), Doing: "Check-in", Notice: true)
    {
        Wording = new(PromptCatalog.CheckInDue, PromptCatalog.CheckInDueNotes, "items")
    };

    /// <summary>Every check-in with the owner's choices: the built-in ones (with what the owner changed on their cards), then the
    /// owner's own.</summary>
    public static IReadOnlyList<CheckIn> All(CheckInSettings? settings) =>
    [
        .. BuiltIn.Select(checkIn => settings?.Choice(checkIn.Id) is { } choice ? With(checkIn, choice) : checkIn),
        .. (settings?.Custom ?? []).Select(Of)
    ];

    /// <summary>The built-in <paramref name="checkIn"/> with what the owner changed on its card.</summary>
    public static CheckIn With(CheckIn checkIn, CheckInChoice choice)
    {
        var changed = checkIn with
        {
            On = choice.On, EveryMinutes = choice.EveryMinutes, Facts = choice.Facts ?? checkIn.Facts,
            Conditions = choice.Conditions ?? checkIn.Conditions, Outcome = choice.Outcome ?? checkIn.Outcome,
            Screenshot = choice.Screenshot ?? checkIn.Screenshot, Recording = choice.Recording ?? checkIn.Recording,
            RecordingSeconds = choice.RecordingSeconds ?? checkIn.RecordingSeconds, Script = choice.Script ?? checkIn.Script,
            Triggers = choice.Triggers ?? checkIn.Triggers,
            FromHour = choice.FromHour ?? checkIn.FromHour, UntilHour = choice.UntilHour ?? checkIn.UntilHour,
            MostPerHour = choice.MostPerHour ?? checkIn.MostPerHour, ToolSets = choice.ToolSets ?? checkIn.ToolSets,
            RunsOn = choice.RunsOn ?? checkIn.RunsOn
        };
        return changed with { Needs = Needs(choice.Needs ?? checkIn.Needs, changed.Screenshot, changed.Recording) };
    }

    /// <summary>One of the owner's own check-ins as Martlet runs it.</summary>
    public static CheckIn Of(CustomCheckIn custom) =>
        new(custom.Id, custom.Name, custom.Outcome switch
        {
            CheckInOutcome.Say => "Your own check-in: Martlet brings up what it says, on its own.",
            CheckInOutcome.EmotesOff => "Your own check-in: Martlet turns off the lingering emotes it names.",
            CheckInOutcome.GazeUsual => "Your own check-in: Martlet takes the eyes back to their usual when it says so.",
            CheckInOutcome.Tools => "Your own check-in: Martlet lets it use its tools.",
            CheckInOutcome.Context => "Your own check-in: what it describes adds to what Martlet knows in its next reply.",
            CheckInOutcome.Plan => "Your own check-in: what it thinks ahead goes with Martlet's next reply.",
            CheckInOutcome.HoldBack => "Your own check-in: Martlet holds back while it says you talk to someone else.",
            CheckInOutcome.Callbacks => "Your own check-in: what it lists becomes Martlet's callback list.",
            _ => "Your own check-in: what it says reminds Martlet in its next reply."
        }, custom.Outcome, custom.On, custom.EveryMinutes)
        {
            Custom = true, Task = custom.Task, Facts = custom.Facts, Conditions = custom.Conditions, Needs = Needs(custom),
            Screenshot = custom.Screenshot, Recording = custom.Recording, RecordingSeconds = custom.RecordingSeconds, Script = custom.Script,
            Triggers = custom.Triggers,
            FromHour = custom.FromHour, UntilHour = custom.UntilHour, MostPerHour = custom.MostPerHour, ToolSets = custom.ToolSets,
            RunsOn = custom.RunsOn
        };

    /// <summary>What a Thinking pool member must handle to take <paramref name="custom"/>: text, what the owner chose, pictures
    /// for a screenshot, recordings for a recording and tool calls for tool sets that aren't all optional.</summary>
    public static ThinkingCapability Needs(CustomCheckIn custom) => Needs(custom.Needs, custom.Screenshot, custom.Recording) |
        (CheckInToolSets.NeedTools(custom.ToolSets) ? ThinkingCapability.Tools : ThinkingCapability.None);

    /// <summary>The tool sets a run of <paramref name="checkIn"/> is offered: all it ticks when the pool has a member that calls
    /// tools for it (<paramref name="poolCallsTools"/>), otherwise only those that aren't <see cref="CheckInToolSet.Optional"/>
    /// (a check-in with such sets waits for a member that calls tools anyway).</summary>
    public static IReadOnlyList<string> Offered(CheckIn checkIn, bool poolCallsTools)
    {
        ArgumentNullException.ThrowIfNull(checkIn);
        return poolCallsTools ? checkIn.ToolSets : [.. checkIn.ToolSets.Where(id => CheckInToolSets.Find(id) is not { Optional: true })];
    }

    /// <summary>What a member must handle: text, what the owner <paramref name="chose"/>, pictures for a screenshot and recordings
    /// for a recording (<see cref="SenseJobKind.CheckInScreenshot"/> and <see cref="SenseJobKind.CheckInRecording"/> in
    /// <see cref="SenseJobRouting.Rows"/>: a Thinking pool member that sees or hears takes them, else they wait).</summary>
    public static ThinkingCapability Needs(ThinkingCapability chose, bool screenshot, CheckInRecording recording) =>
        ThinkingCapability.Text | chose & (ThinkingCapability.Vision | ThinkingCapability.Audio) |
        (screenshot ? SenseJobRouting.Row(SenseJobKind.CheckInScreenshot).Sense : ThinkingCapability.None) |
        (recording != CheckInRecording.None ? SenseJobRouting.Row(SenseJobKind.CheckInRecording).Sense : ThinkingCapability.None);

    /// <summary>What a member must handle, in plain words: "text", "text and pictures", "text, pictures and recordings", "text and
    /// tool calls".</summary>
    public static string Describe(ThinkingCapability needs)
    {
        var parts = new List<string> { "text" };
        if (needs.HasFlag(ThinkingCapability.Vision)) parts.Add("pictures");
        if (needs.HasFlag(ThinkingCapability.Audio)) parts.Add("recordings");
        if (needs.HasFlag(ThinkingCapability.Tools)) parts.Add("tool calls");
        return parts.Count == 1 ? parts[0] : string.Join(", ", parts.SkipLast(1)) + " and " + parts[^1];
    }

    /// <summary>The context board source of a check-in's reminder for the next reply, or of what it adds to what Martlet knows
    /// ("check-in-promises").</summary>
    public static string Source(string id) => "check-in-" + id;

    /// <summary>What a reminder for the next reply says in the notes of that message (Companion › Prompts › Check-in: reminder
    /// for the next reply), or null when the owner emptied that prompt.</summary>
    public static string? Note(PromptSettings? prompts, string reminder) =>
        PromptSettings.Fill(prompts, PromptCatalog.CheckInNote, ("reminder", reminder));

    /// <summary>What a check-in adds to what Martlet knows says in the notes of the next message (Companion › Prompts › Check-in:
    /// adds to what Martlet knows): background the reply may draw on, not a reminder to follow. Null when the owner emptied that
    /// prompt.</summary>
    public static string? Context(PromptSettings? prompts, string context) =>
        PromptSettings.Fill(prompts, PromptCatalog.CheckInContext, ("context", context));

    /// <summary>What a check-in's thoughts ahead say in the notes of the next message (Companion › Prompts › Check-in: thoughts
    /// ahead for the next reply): the character's own plan, which the reply uses only where it fits what was said. Null when the
    /// owner emptied that prompt.</summary>
    public static string? Plan(PromptSettings? prompts, string plan) =>
        PromptSettings.Fill(prompts, PromptCatalog.CheckInPlan, ("plan", plan));

    /// <summary>Another exchange ended: the thoughts ahead of every check-in in <paramref name="checkIns"/> that thinks ahead
    /// (<see cref="CheckInOutcome.Plan"/>) leave <paramref name="board"/>, because they were about the message after the exchange
    /// they read. Returns how many left.</summary>
    public static int DropPlans(ContextBoard board, IEnumerable<CheckIn> checkIns)
    {
        ArgumentNullException.ThrowIfNull(board);
        ArgumentNullException.ThrowIfNull(checkIns);
        return checkIns.Where(c => c.Outcome == CheckInOutcome.Plan).Count(c => board.Clear(Source(c.Id)));
    }
}
