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
            "The last 6 exchanges of the conversation (you and Martlet, oldest first) and how long it has been quiet since. " +
            "Placeholder: {conversation}."),
        (CheckInFacts.Persona, "Its personality",
            "The active personality: the character's name and the text you wrote for it. Placeholder: {persona}."),
        (CheckInFacts.Replies, "Martlet's last replies",
            "Martlet's last 6 replies, numbered, oldest first, without what you said. Placeholder: {replies}."),
        (CheckInFacts.Said, "What Martlet said in the last hour",
            "Everything Martlet said in the last hour (replies, remarks and reactions; the newest 10), each with when it said it, " +
            "such as \"10:05 PM (12 min ago)\". Placeholder: {said}."),
        (CheckInFacts.Character, "Emotes and gaze",
            "The emotes a reply turned on that still show on the character, with their hints and how long each has shown, and " +
            "where its eyes look now and usually. Placeholders: {emotes}, {example} (the first emote's tag), {looking}, {usual} " +
            "and {since}."),
        (CheckInFacts.Work, "Reminders and background work",
            "The reminders that are set and the background work Martlet started or finished in this conversation. Placeholder: {work}."),
        (CheckInFacts.Screen, "What changed on screen",
            "Martlet's newest words about what changed on your screen, while it watches the screen. It isn't a screenshot: " +
            "tick A screenshot below for that. Placeholder: {screen}."),
        (CheckInFacts.Sound, "What the PC plays",
            "Martlet's newest words about what this PC plays (music, videos, games), while it hears it. It isn't a recording: " +
            "choose one below for that. Placeholder: {sound}."),
        (CheckInFacts.Presence, "Whether you're at the PC",
            "Whether someone uses this PC now, or how long since someone last used the keyboard or mouse. Placeholder: {presence}."),
        (CheckInFacts.Touches, "How you touched the character",
            $"What you did to the character on the desktop in the last {TouchLedger.OftenWindow.TotalMinutes:0} minutes, oldest " +
            "first, each with when: pokes, pats, holds, strokes with their path and direction, and moves. It says which touches " +
            "were intimate, how the personality feels about them and the places you keep coming back to. Martlet only reads " +
            "it: its next reply still gets your touches. Placeholder: {touches}."),
        (CheckInFacts.Activity, "What you are doing",
            "What you seem to be doing on this PC (a game, a call or voice chat, a video, music, full screen), what it was before " +
            "and when it changed. Martlet knows it only while it hears what this PC plays. Placeholder: {activity}."),
        (CheckInFacts.People, "Who is here",
            $"The voices Martlet heard in the last {CheckIns.PeopleWindow.TotalMinutes:0} minutes (Voice ID): the names of the " +
            "voices it knows, a voice it doesn't know, which one is yours, each with when. Never what they said. Placeholder: {people}."),
        (CheckInFacts.WhileAway, "What happened while you were away",
            $"After you come back from {CheckIns.Idle.TotalMinutes:0} minutes or more away: how long you were away, what Martlet " +
            "said meanwhile, the background work that finished and the reminders that came due. Placeholder: {away}."),
        (CheckInFacts.Latest, "The latest exchange",
            "What you said last and Martlet's reply, in full, or every exchange since it last ran (at most " +
            $"{CheckIns.MaximumLatest}), so a check-in that runs after each reply misses nothing. Placeholder: {{exchange}}."),
        (CheckInFacts.Heard, "What Martlet heard you say",
            $"What always listening heard you say in the last {CheckIns.HeardWindow.TotalMinutes:0} minutes, oldest first, each " +
            "with when, who spoke (Voice ID), other voices, people talking over each other, lines this PC played with it, and " +
            "whether Martlet answered or stayed quiet; then whether Martlet holds back now while you talk to someone else. " +
            "Placeholder: {heard}."),
        (CheckInFacts.Callbacks, "The callback list",
            $"The few things from this conversation worth bringing back later (at most {CallbackList.MaximumItems}): what you're " +
            "in the middle of, running jokes, things you said in your own words and facts that fit a tease, each with when it " +
            "came up. The Callback list check-in writes it after each reply. Placeholder: {callbacks}.")
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
            "It waits until a game, a call or a full-screen app started or ended since it last ran. Martlet tells only while it " +
            "hears what this PC plays."),
        (CheckInConditions.Between, "Only between these hours",
            "It runs only between the hours you choose below, such as in the morning, or not at night."),
        (CheckInConditions.Unanswered, "Martlet asked something you didn't answer",
            $"It waits until Martlet's last remark asked you something, you're at the PC and didn't answer for " +
            $"{CheckIns.UnansweredAfter.TotalMinutes:0} minutes. It runs once for each question."),
        (CheckInConditions.Attention, "A taskbar button flashed or a notification showed",
            "It waits until a taskbar button flashed or a pop-up notification showed since it last ran. Martlet notices them " +
            "only while a check-in that is on waits for them, and never reads what they say."),
        (CheckInConditions.SongEnded, "A song ended", "It waits until a song Martlet sang played to its end since it last ran."),
        (CheckInConditions.ScriptChanged, "The script's output changed",
            "Its script runs at its pace; when what it prints is the same as last time, the run ends there without asking the " +
            "Thinking pool."),
        (CheckInConditions.SomeoneElse, "Someone else spoke",
            $"It waits until Martlet heard a voice that isn't yours in the last {CheckIns.PeopleWindow.TotalMinutes:0} minutes " +
            "(Voice ID), since it last ran."),
        (CheckInConditions.Lull, "The conversation went quiet",
            $"It waits until nothing was said for {CheckIns.LullAfter.TotalMinutes:0} minutes (but not more than " +
            $"{CheckIns.PromiseWindow.TotalMinutes:0}), you're at the PC, the callback list has something and Martlet's last " +
            "remark didn't ask you something. It runs once in each quiet spell.")
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
            "It runs right after each of Martlet's replies (once it was said), with what you said and the reply. Exchanges " +
            "that end while it waits for its Every are kept, and it reads them all when it runs."),
        (CheckInTriggers.Spoke, "You say something",
            "It runs right after always listening hears you say something, whether Martlet answered or stayed quiet. Only what " +
            "the microphone hears counts, not what you type.")
    ];

    internal const string TriggersHelp = "With one ticked, it runs only when one of them happens (a moment after your " +
        "touches stop, right after a reply, or right after you say something), and at most once per Every (Every is then the " +
        "time it waits before it can run " +
        "again). It runs on the Thinking pool, so a reply never waits for it. It still waits while it's off, while nobody uses " +
        "this PC, for its conditions and for a pool member. With none ticked, it runs every few minutes.";

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

    internal const string OutcomeHelp = "Martlet adds the answer format to the prompt. A reminder: a REMIND: line goes in " +
        "the notes of your next message, so Martlet's next reply follows it. Brings it up: a SAY: line, which Martlet says on its " +
        "own as soon as it's free. Adds to what Martlet knows: a KNOW: line, a short description of what is happening, goes in " +
        "the notes of your next message (within a few minutes) as background Martlet may draw on, not a reminder to follow. " +
        "Thinks ahead: a PLAN: line, the character's own thoughts about what you'll likely say next and what it means to say " +
        "then, goes in the notes of your next message, once; it's dropped when another reply ends first. " +
        "Holds back: an ASIDE: line (you talk to someone else) makes Martlet stay quiet while that lasts, unless you talk to " +
        "it; BACK ends it, and so do a few quiet minutes. " +
        "Keeps the callback list: CALLBACK: lines are the whole new list of things worth bringing back later, which Think ahead " +
        "and Quiet moments read; KEEP leaves it and NONE empties it. " +
        "Turns off emotes: OFF lines with the tags of lingering emotes to turn off (tick Emotes and " +
        "gaze). Takes the eyes back: USUAL ends the gaze a reply chose. OK or KEEP changes nothing. Its tools act: the tools it " +
        "calls (tick at least one set under It may use these tools) are what it does, and its answer only says what it did.";

    internal const string PlaceholderHelp = "Placeholders put a fact where you want it: {name}, {time}, {adult} (whether " +
        "Adult content is on: an explicit or a non-explicit line, from Companion › Prompts), {conversation}, " +
        "{persona}, {replies}, {said}, {emotes}, {example}, {looking}, {usual}, {since}, {work}, {screen}, {sound}, " +
        "{presence}, {touches}, {activity}, {people}, {away}, {exchange}, {heard} and {callbacks}. The facts you tick that the prompt doesn't name go after it, then the day and time and the answer format.";

    internal const string About = "A check-in is a short question that the Thinking pool answers for Martlet every few " +
        "minutes, with only the facts it needs: the end of the conversation, what Martlet said lately, the emotes that stay on, " +
        "where the eyes look, and the reminders and work Martlet started. Martlet then acts on the answer. It turns off an emote, " +
        "takes the eyes back to their usual gaze, or puts a short reminder in the notes of your next message, so its next reply " +
        "follows it. A check-in can also call tools from the sets you tick on its card, and with Its tools act, those calls are " +
        "what it does. Its card shows each tool it called and what came of it.\n\n" +
        "Check-ins run only on Thinking pool members, never on the conversation's own Thinking model, so replies never wait for " +
        "them. They wait while you talk and while nobody uses this PC, and they stay on this PC. The built-in check-ins work like " +
        "your own: change their prompt, facts, conditions and answer under More settings on their card, and Use built-in settings " +
        "puts Martlet's own back. Copy as your own makes a check-in of your own from any card.";

    // What models hear of a long recording (docs/CONVERSATION.md#check-ins).
    internal const string LengthHelp = "Gemma 4 and Gemma 3n hear only the first 30 seconds of a recording. " +
        "Gemini, OpenAI's audio models and Voxtral hear a whole minute.";

}
