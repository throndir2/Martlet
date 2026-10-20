using Martlet.Avatar.Hosting;
using Martlet.Conversation;

namespace Martlet.Desktop;

/// <summary>Companion › Check-ins' fixed choices and help texts, in plain words: the facts a check-in can get to know, the
/// conditions it waits for, the triggers that start it, the answers it can give, and the help for placeholders and recordings.</summary>
internal sealed partial class CheckInsPageModel
{
    internal static readonly (CheckInFacts Fact, string Label, string Help)[] FactChoices =
    [
        (CheckInFacts.Conversation, "The conversation",
            "The last 6 exchanges of the conversation (you and Martlet, oldest first) and how long it has been quiet since " +
            "({conversation})."),
        (CheckInFacts.Persona, "Its personality",
            "The active personality: the character's name and the text you wrote for it ({persona})."),
        (CheckInFacts.Replies, "Martlet's last replies",
            "Martlet's last 6 replies, numbered, oldest first, without what you said ({replies})."),
        (CheckInFacts.Said, "What Martlet said in the last hour",
            "Everything Martlet said in the last hour (replies, remarks and reactions; the newest 10), each with when it said it, " +
            "such as \"10:05 PM (12 min ago)\" ({said})."),
        (CheckInFacts.Character, "Emotes and gaze",
            "The emotes a reply turned on that still show, with their hints and how long each has shown, and where the eyes look " +
            "now and usually ({emotes}, {example} for the first emote's tag, {looking}, {usual} and {since})."),
        (CheckInFacts.Work, "Reminders and background work",
            "The reminders that are set and the background work Martlet started or finished in this conversation ({work})."),
        (CheckInFacts.Screen, "What changed on screen",
            "Martlet's newest words about what changed on your screen while it watches the screen, not a screenshot ({screen})."),
        (CheckInFacts.Sound, "What the PC plays",
            "Martlet's newest words about what this PC plays (music, videos, games) while it hears it, not a recording ({sound})."),
        (CheckInFacts.Presence, "Whether you're at the PC",
            "Whether someone uses this PC now, or how long since someone last used the keyboard or mouse ({presence})."),
        (CheckInFacts.Touches, "How you touched the character",
            $"What you did to the character in the last {TouchLedger.OftenWindow.TotalMinutes:0} minutes (pokes, pats, holds, " +
            "strokes and moves, each with when), which touches were intimate, how the personality feels about them and the places " +
            "you keep coming back to ({touches})."),
        (CheckInFacts.Activity, "What you are doing",
            "What you seem to be doing on this PC (a game, a call, a video, music, full screen), what it was before and when it " +
            "changed, while Martlet hears what this PC plays ({activity})."),
        (CheckInFacts.People, "Who is here",
            $"The voices Martlet heard in the last {CheckIns.PeopleWindow.TotalMinutes:0} minutes (Voice ID), each with when: the " +
            "names of the voices it knows, a voice it doesn't know and which one is yours, never what they said ({people})."),
        (CheckInFacts.WhileAway, "What happened while you were away",
            $"After you come back from {CheckIns.Idle.TotalMinutes:0} minutes or more away: how long you were away, what Martlet " +
            "said meanwhile, the background work that finished and the reminders that came due ({away})."),
        (CheckInFacts.Latest, "The latest exchange",
            "What you said last and Martlet's reply in full, or every exchange since it last ran (at most " +
            $"{CheckIns.MaximumLatest}), so a check-in that runs after each reply misses nothing ({{exchange}})."),
        (CheckInFacts.Heard, "What Martlet heard you say",
            $"What always listening heard you say in the last {CheckIns.HeardWindow.TotalMinutes:0} minutes, each with when, who " +
            "spoke (Voice ID) and whether Martlet answered, and whether Martlet holds back now while you talk to someone else " +
            "({heard})."),
        (CheckInFacts.Callbacks, "The callback list",
            $"The few things from this conversation worth bringing back later (at most {CallbackList.MaximumItems}), such as what " +
            "you're in the middle of and running jokes, which the Callback list check-in writes after each reply ({callbacks}).")
    ];

    internal static readonly (CheckInConditions Condition, string Label, string Help)[] ConditionChoices =
    [
        (CheckInConditions.Talked, "You talked lately",
            $"It waits until something was said in the conversation, and while it has been quiet for more than " +
            $"{CheckIns.PromiseWindow.TotalMinutes:0} minutes."),
        (CheckInConditions.SomethingNew, "Something new was said",
            "After it runs, it waits until something new was said in the conversation."),
        (CheckInConditions.NewReplies, $"{CheckIns.CharacterReplies} new replies",
            $"After it runs, it waits until the conversation had {CheckIns.CharacterReplies} new exchanges."),
        (CheckInConditions.Replies, "Martlet replied twice", "It waits until Martlet gave at least 2 replies in this conversation."),
        (CheckInConditions.Sayings, $"Martlet said {CheckIns.RepeatsSayings} things lately",
            $"It waits until Martlet said at least {CheckIns.RepeatsSayings} things in the last hour."),
        (CheckInConditions.Persona, "A personality is active", "It waits while no personality is active."),
        (CheckInConditions.CharacterShows, "The character shows", "It waits while the character isn't on the desktop."),
        (CheckInConditions.EmoteShown, "An emote a reply turned on shows",
            $"It waits until an emote a reply turned on has shown for {CheckIns.MinimumShown.TotalMinutes:0} minutes, and it " +
            "reads only those emotes (Check now reads all of them)."),
        (CheckInConditions.GazeChosen, "A reply chose where the eyes look",
            $"It waits until a reply chose where the eyes look, at least {CheckIns.MinimumShown.TotalMinutes:0} minutes ago."),
        (CheckInConditions.SlowWhenKept, "Slower when nothing changes",
            $"After an answer that changed nothing, and while nothing new was said, it waits {CheckIns.KeptPace} times as long."),
        (CheckInConditions.CameBack, "You came back",
            $"It waits until you use this PC again after {CheckIns.Idle.TotalMinutes:0} minutes or more away, and runs within " +
            $"{CheckIns.SignalWindow.TotalMinutes:0} minutes of that, once each time."),
        (CheckInConditions.NotOnCall, "Not on a call",
            "It waits while you're in a call or a voice chat (Discord, Zoom, Teams...), as far as Martlet can tell from what this PC plays."),
        (CheckInConditions.ActivityChanged, "What you do changed",
            "It waits until a game, a call or a full-screen app started or ended since it last ran (Martlet tells only while it " +
            "hears what this PC plays)."),
        (CheckInConditions.Between, "Only between these hours",
            "It runs only between the hours you choose below, such as in the morning, or not at night."),
        (CheckInConditions.Unanswered, "Martlet asked something you didn't answer",
            $"It waits until Martlet's last remark asked you something and you're at the PC but didn't answer for " +
            $"{CheckIns.UnansweredAfter.TotalMinutes:0} minutes, once for each question."),
        (CheckInConditions.Attention, "A taskbar button flashed or a notification showed",
            "It waits until a taskbar button flashed or a pop-up notification showed since it last ran (Martlet never reads what " +
            "they say)."),
        (CheckInConditions.SongEnded, "A song ended", "It waits until a song Martlet sang played to its end since it last ran."),
        (CheckInConditions.ScriptChanged, "The script's output changed",
            "Its script runs at its pace; when what it prints is the same as last time, the run ends there without asking the " +
            "Thinking pool."),
        (CheckInConditions.SomeoneElse, "Someone else spoke",
            $"It waits until Martlet heard a voice that isn't yours in the last {CheckIns.PeopleWindow.TotalMinutes:0} minutes " +
            "(Voice ID), since it last ran."),
        (CheckInConditions.Lull, "The conversation went quiet",
            $"It waits until nothing was said for {CheckIns.LullAfter.TotalMinutes:0} minutes (but not more than " +
            $"{CheckIns.PromiseWindow.TotalMinutes:0}) while you're at the PC, the callback list has something and Martlet's last " +
            "remark didn't ask you something, once in each quiet spell.")
    ];

    // What can start a check-in at once, in the order of its It starts when boxes.
    internal static readonly (CheckInTriggers Trigger, string Label, string Help)[] TriggerChoices =
    [
        (CheckInTriggers.TouchesEnded, "Your touches end",
            $"It runs about {TouchDebounce.Quiet.TotalSeconds:0.#} seconds after you stop touching the character (taps, pats, holds " +
            "and strokes on zones Martlet notices)."),
        (CheckInTriggers.IntimateTouch, "An intimate touch",
            $"It runs after your touches stop, when they touched an intimate part ({CharacterTouchZones.IntimateParts})."),
        (CheckInTriggers.StrokeAcrossZones, $"A stroke across {CheckIns.StrokeZones} zones",
            $"It runs after your touches stop, when a stroke crossed {CheckIns.StrokeZones} or more zones Martlet notices."),
        (CheckInTriggers.KeepsComingBack, "You keep coming back to one place",
            $"It runs after your touches stop, when they touched a place you touched {TouchLedger.OftenTouches} times or more in the " +
            $"last {TouchLedger.OftenWindow.TotalMinutes:0} minutes."),
        (CheckInTriggers.ExchangeEnded, "A reply ends",
            "It runs right after each of Martlet's replies with what you said and the reply, and it reads every exchange that " +
            "ended while it waited for its Every."),
        (CheckInTriggers.Spoke, "You say something",
            "It runs right after always listening hears you say something (not what you type), whether Martlet answered or " +
            "stayed quiet.")
    ];

    internal const string TriggersHelp = "With one ticked, it runs only when one of them happens, at most once per Every, and a " +
        "reply never waits for it. With none ticked, it runs every few minutes.";

    // The answers a check-in can give, in the order of its Its answer list.
    internal static readonly (CheckInOutcome Outcome, string Label)[] OutcomeChoices =
    [
        (CheckInOutcome.Note, "Reminds Martlet in its next reply"),
        (CheckInOutcome.Say, "Martlet brings it up"),
        (CheckInOutcome.Context, "Adds to what Martlet knows"),
        (CheckInOutcome.Plan, "Thinks ahead for Martlet's next reply"),
        (CheckInOutcome.HoldBack, "Martlet holds back while you talk to someone else"),
        (CheckInOutcome.Callbacks, "Keeps Martlet's callback list"),
        (CheckInOutcome.EmotesOff, "Turns off the emotes it names"),
        (CheckInOutcome.GazeUsual, "Takes the eyes back to their usual"),
        (CheckInOutcome.Tools, "Its tools act")
    ];

    // The tooltip of Its answer; the "?" next to it shows OutcomeHelp, and the wiki has what each answer does.
    internal const string OutcomeSummary = "What Martlet does with the check-in's answer, such as remind itself in its next reply.";

    internal const string OutcomeHelp = "Martlet adds the answer format to the prompt, so the model answers with a line, such as " +
        "a REMIND: line for Martlet's next reply. Martlet says a SAY: line on its own as soon as it's free. " +
        "With Its tools act, the tools it calls are what it does.";

    internal const string PlaceholderHelp = "Placeholders put a fact where you want it: {name}, {time}, {adult} (whether " +
        "Adult content is on: an explicit or a non-explicit line, from Companion › Prompts), {conversation}, " +
        "{persona}, {replies}, {said}, {emotes}, {example}, {looking}, {usual}, {since}, {work}, {screen}, {sound}, " +
        "{presence}, {touches}, {activity}, {people}, {away}, {exchange}, {heard} and {callbacks}. The facts you tick that the prompt doesn't name go after it, then the day and time and the answer format.";

    // The "?" after Now: one short line; the full instructions are in the wiki (docs/wiki/Thinking.md#check-ins).
    internal const string About = "A check-in is a short question that the Thinking pool answers for Martlet every few minutes. " +
        "Martlet then acts on the answer, so its next reply follows it.";

    // What models hear of a long recording (docs/CONVERSATION.md#check-ins).
    internal const string LengthHelp = "Gemma 4 and Gemma 3n hear only the first 30 seconds of a recording, while " +
        "Gemini, OpenAI's audio models and Voxtral hear a whole minute.";

}
