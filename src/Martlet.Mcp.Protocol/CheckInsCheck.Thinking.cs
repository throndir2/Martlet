using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Martlet.Avatar.Hosting;
using Martlet.Conversation;
using Martlet.Core.Settings;
using Martlet.Providers;

namespace Martlet.Mcp;

internal static partial class CheckInsCheck
{
    /// <summary>11b. The built-in Think ahead check-in end to end with FIXTURE facts and a canned answer (NOT AI): it waits for a
    /// reply to end, a fired trigger runs it even while the conversation is busy, its message carries the personality, the
    /// conversation and the PLAN: format, the answer is read on a production job board, the thoughts go on a production context
    /// board for exactly one request, and another exchange that ends first drops them (<see cref="CheckIns.DropPlans"/>).</summary>
    private static async Task ThinkAheadAsync(Action<string, bool, object?> step, DateTimeOffset now, CheckInState facts,
        CancellationToken cancellation)
    {
        var ahead = CheckIns.All(null).Single(c => c.Id == CheckIns.Ahead);
        var fired = CheckIns.ExchangeTrigger(now.AddSeconds(-1));
        var waits = new Dictionary<string, string?>
        {
            ["no reply ended"] = CheckIns.Wait(ahead, facts, null),
            ["a reply ended, the user talks"] = CheckIns.Wait(ahead, facts with { Quiet = TimeSpan.Zero }, null, fired: fired),
            ["a reply ended, ran 10 s ago"] = CheckIns.Wait(ahead, facts, new(now.AddSeconds(-10), 3, "thoughts ahead wait for the next reply", false),
                fired: fired),
            ["nothing said yet"] = CheckIns.Wait(ahead, facts with { Exchanges = [], Exchanged = 0 }, null, fired: fired)
        };
        var job = CheckIns.Prepare(ahead, CheckIns.Focus(ahead, facts), null);
        BackgroundPlace member = new("endpoint:fixture-ahead", "FIXTURE member") { Slots = 1, Model = "fixture-model" };
        const string answer = "<think>They're stuck on a bug; they'll vent or say it's fixed.</think>\n" +
            "- PLAN: FIXTURE: They'll probably say the bug is fixed; I'll cheer, then tease them about how long it took.";
        var board = new ThinkingJobBoard(new BackgroundPlaces(), () => [member], (_, _, _) => Task.FromResult(ThinkingAnswer.Done(answer)));
        var result = job is null ? null : await board.RunAsync(job, cancellation);
        var verdict = CheckIns.Read(ahead, result?.Text, facts);
        var context = new ContextBoard();
        var source = CheckIns.Source(ahead.Id);
        if (verdict is { Act: true, Text: { } text } && CheckIns.Plan(null, text) is { } note)
            context.Post(source, note, now, CheckIns.PlanAge, consume: true);
        var first = context.Snapshot(now.AddSeconds(30));
        context.MarkSent(first);
        var second = context.Snapshot(now.AddSeconds(40));
        // Thoughts that came after the reply's request was built wait on the board; the reply's end drops them.
        context.Post(source, CheckIns.Plan(null, verdict.Text ?? "x")!, now, CheckIns.PlanAge, consume: true);
        var dropped = CheckIns.DropPlans(context, CheckIns.All(null));
        var afterNextExchange = context.Snapshot(now.AddSeconds(50));
        context.Post(source, CheckIns.Plan(null, verdict.Text ?? "x")!, now, CheckIns.PlanAge, consume: true);
        var stale = context.Snapshot(now + CheckIns.PlanAge + TimeSpan.FromSeconds(1));
        var nothing = new Dictionary<string, CheckInVerdict>
        {
            ["OK"] = CheckIns.Read(ahead, "OK", facts),
            ["PLAN: nothing to plan"] = CheckIns.Read(ahead, "PLAN: nothing to plan.", facts),
            ["KNOW: instead"] = CheckIns.Read(ahead, "KNOW: They're coding.", facts)
        };
        var emptied = CheckIns.Plan(new Martlet.Core.Settings.PromptSettings
        {
            Overrides = new Dictionary<string, string> { [Martlet.Core.Settings.PromptCatalog.CheckInPlan] = "" }
        }, "x");
        var message = job?.Text ?? "";
        step("think ahead", ahead is { On: true, EveryMinutes: 1, Outcome: CheckInOutcome.Plan, Triggers: CheckInTriggers.ExchangeEnded } &&
            waits["no reply ended"] == "it waits for a reply to end" && waits["a reply ended, the user talks"] is null &&
            waits["a reply ended, ran 10 s ago"] == "next in 50 s" && waits["nothing said yet"] == "nothing was said in a conversation here yet" &&
            job is { Kind: ThinkingJobKind.CheckIn, Reasoning: false } && message.Contains("Think ahead as Mira", StringComparison.Ordinal) &&
            message.Contains("playful and teasing", StringComparison.Ordinal) && message.Contains("This bug is driving me crazy", StringComparison.Ordinal) &&
            message.Contains("starts with PLAN:", StringComparison.Ordinal) && message.Contains("write only: OK", StringComparison.Ordinal) &&
            !message.Contains("{persona}", StringComparison.Ordinal) && !message.Contains("{conversation}", StringComparison.Ordinal) &&
            result is { Succeeded: true } && verdict is { Act: true, Text: { } read } && read.StartsWith("FIXTURE: They'll probably", StringComparison.Ordinal) &&
            first.Sources.Contains(source) && first.Text!.Contains("Your own thoughts from just before this message", StringComparison.Ordinal) &&
            first.Text.Contains("tease them about how long it took", StringComparison.Ordinal) && !second.Sources.Contains(source) &&
            dropped == 1 && !afterNextExchange.Sources.Contains(source) && !stale.Sources.Contains(source) &&
            nothing["OK"] is { Act: false, Readable: true } && nothing["PLAN: nothing to plan"] is { Act: false, Readable: true } &&
            nothing["KNOW: instead"] is { Act: false, Readable: false } && emptied is null,
            new
            {
                waits = waits.ToDictionary(w => w.Key, w => w.Value ?? "runs"), message, verdict.Act, verdict.Text,
                firstRequestNotes = first.Text, secondRequestSources = second.Sources, droppedByTheNextExchange = dropped,
                planAgeMinutes = CheckIns.PlanAge.TotalMinutes,
                nothing = nothing.ToDictionary(v => v.Key, v => new { v.Value.Act, v.Value.Readable })
            });
    }

    /// <summary>11c. The callback list and Quiet moments (FIXTURE exchanges, canned answers, NOT AI): Callback list runs after each
    /// reply with the list and what was said since it last ran, its CALLBACK: lines are read on a production job board and kept on
    /// a production <see cref="CallbackList"/> (a thing already on it keeps when it first came up; a list forgotten meanwhile isn't
    /// written again), Think ahead reads the list and picks one move, Quiet moments waits for a quiet spell with the user at the PC
    /// and brings one thing back (SAY:), and the reply's prompts keep the conversation going, let callbacks through the repeat
    /// check and remember running jokes.</summary>
    private static async Task CallbacksAsync(Action<string, bool, object?> step, DateTimeOffset now, CheckInState facts,
        CancellationToken cancellation)
    {
        var keeper = CheckIns.All(null).Single(c => c.Id == CheckIns.Callbacks);
        var list = new CallbackList();
        list.Keep(["open - FIXTURE a bug drives them crazy"], now.AddMinutes(-40));
        var before = list.Recent(now);
        var state = facts with { Callbacks = before, Since = 3 };
        var job = CheckIns.Prepare(keeper, CheckIns.Focus(keeper, state), null);
        BackgroundPlace member = new("endpoint:fixture-callbacks", "FIXTURE member") { Slots = 1, Model = "fixture-model" };
        const string answer = "<think>The boss quote is worth keeping.</think>\nCALLBACKS:\n" +
            "- CALLBACK: quote - FIXTURE they said \"one more try\" about the boss\n- CALLBACK: open - FIXTURE a bug drives them crazy";
        var board = new ThinkingJobBoard(new BackgroundPlaces(), () => [member], (_, _, _) => Task.FromResult(ThinkingAnswer.Done(answer)));
        var result = job is null ? null : await board.RunAsync(job, cancellation);
        var verdict = CheckIns.Read(keeper, result?.Text, state);
        var version = list.Version;
        var kept = list.Keep(verdict.Tags, now, version) ?? [];
        var forgotten = new CallbackList();
        var stale = forgotten.Version;
        forgotten.Clear();
        var notWritten = forgotten.Keep(["joke - FIXTURE"], now, stale);
        var odd = new Dictionary<string, CheckInVerdict>
        {
            ["KEEP"] = CheckIns.Read(keeper, "KEEP", state),
            ["NONE"] = CheckIns.Read(keeper, "NONE", state),
            ["chatter"] = CheckIns.Read(keeper, "Sure, here is the list you asked for.", state)
        };
        var message = job?.Text ?? "";
        var lines = CallbackList.Lines(kept, now);
        step("callback list", keeper is { On: true, EveryMinutes: 1, Outcome: CheckInOutcome.Callbacks, Triggers: CheckInTriggers.ExchangeEnded } &&
            keeper.Facts == (CheckInFacts.Latest | CheckInFacts.Callbacks) && keeper.Conditions == CheckInConditions.SomethingNew &&
            CheckIns.Wait(keeper, state, null) == "it waits for a reply to end" &&
            CheckIns.Wait(keeper, state, null, fired: CheckIns.ExchangeTrigger(now.AddSeconds(-1))) is null &&
            message.Contains("You keep Mira's callback list", StringComparison.Ordinal) &&
            message.Contains("- 9:37 PM (40 min ago): open - FIXTURE a bug drives them crazy", StringComparison.Ordinal) &&
            message.Contains("This bug is driving me crazy", StringComparison.Ordinal) && message.Contains("CALLBACK:", StringComparison.Ordinal) &&
            message.Contains("write only: KEEP", StringComparison.Ordinal) && !message.Contains("{callbacks}", StringComparison.Ordinal) &&
            !message.Contains("{exchange}", StringComparison.Ordinal) &&
            verdict is { Act: true, Readable: true } && verdict.Tags.Count == 2 && kept.Count == 2 &&
            kept[0] is { Text: "open - FIXTURE a bug drives them crazy" } first && first.Since == now.AddMinutes(-40) &&
            kept[1].Since == now && lines.Contains("(40 min ago): open", StringComparison.Ordinal) &&
            notWritten is null && forgotten.Count == 0 &&
            odd["KEEP"] is { Act: false, Readable: true } && odd["NONE"] is { Act: true, Tags.Count: 0 } && odd["chatter"] is { Readable: false },
            new
            {
                message, items = verdict.Tags, kept = lines, forgottenListWritten = notWritten is not null, maximum = CallbackList.MaximumItems,
                odd = odd.ToDictionary(v => v.Key, v => new { v.Value.Act, v.Value.Readable, items = v.Value.Tags.Count })
            });

        // Think ahead reads the list and names one move.
        var ahead = CheckIns.All(null).Single(c => c.Id == CheckIns.Ahead);
        var withList = facts with { Callbacks = kept };
        var plan = CheckIns.Message(ahead, CheckIns.Focus(ahead, withList), null) ?? "";
        var planNote = CheckIns.Plan(null, "FIXTURE Move: callback.") ?? "";
        step("think ahead reads the callback list", ahead.Facts.HasFlag(CheckInFacts.Callbacks) &&
            plan.Contains("callback list", StringComparison.Ordinal) && plan.Contains("FIXTURE they said \"one more try\"", StringComparison.Ordinal) &&
            plan.Contains("pick the one move", StringComparison.Ordinal) && !plan.Contains("{callbacks}", StringComparison.Ordinal) &&
            planNote.Contains("make that move", StringComparison.Ordinal),
            new { message = plan, note = planNote });

        // Quiet moments: one thing from the list after a quiet spell, with the user at the PC, before 10 PM.
        var lull = CheckIns.All(null).Single(c => c.Id == CheckIns.Lull);
        var evening = withList with { Now = now.AddHours(-2), Quiet = TimeSpan.FromMinutes(5), Away = TimeSpan.FromSeconds(20) };
        var asked = evening with { Said = [.. evening.Said, new(evening.Now.AddMinutes(-5), "FIXTURE: So, which boss is next?")] };
        var waits = new Dictionary<string, string?>
        {
            ["quiet 5 min"] = CheckIns.Wait(lull, evening, null),
            ["quiet 1 min"] = CheckIns.Wait(lull, evening with { Quiet = TimeSpan.FromMinutes(1) }, null),
            ["quiet 45 min"] = CheckIns.Wait(lull, evening with { Quiet = TimeSpan.FromMinutes(45) }, null),
            ["empty list"] = CheckIns.Wait(lull, evening with { Callbacks = [] }, null),
            ["away"] = CheckIns.Wait(lull, evening with { Away = TimeSpan.FromMinutes(5) }, null),
            ["ran in this quiet spell"] = CheckIns.Wait(lull, evening with { Quiet = TimeSpan.FromMinutes(15) },
                new(evening.Now.AddMinutes(-12), 4, "nothing to bring up", false)),
            ["ran before it"] = CheckIns.Wait(lull, evening, new(evening.Now.AddMinutes(-20), 4, "nothing to bring up", false)),
            ["a question waits"] = CheckIns.Wait(lull, asked, null),
            ["on a call"] = CheckIns.Wait(lull, evening with { OnCall = true }, null),
            ["10:17 PM"] = CheckIns.Wait(lull, evening with { Now = now }, null)
        };
        var lullMessage = CheckIns.Message(lull, evening, null) ?? "";
        var lullVerdict = CheckIns.Read(lull, "SAY: FIXTURE: So did that \"one more try\" ever end?", evening);
        var unanswered = CheckIns.All(null).Single(c => c.Id == CheckIns.Unanswered);
        step("quiet moments", lull is { On: true, EveryMinutes: 10, Outcome: CheckInOutcome.Say, MostPerHour: 2 } &&
            lull.Conditions == (CheckInConditions.Lull | CheckInConditions.NotOnCall | CheckInConditions.Between) && unanswered.On &&
            waits["quiet 5 min"] is null && waits["quiet 1 min"] == "it waits for 3 min of quiet" &&
            waits["quiet 45 min"]?.StartsWith("the conversation has been quiet for more than", StringComparison.Ordinal) == true &&
            waits["empty list"] == "the callback list is empty" && waits["away"] == "you aren't at the PC" &&
            waits["ran in this quiet spell"] == "it already picked this quiet spell up" && waits["ran before it"] is null &&
            waits["a question waits"]?.StartsWith("Martlet's last remark asked something", StringComparison.Ordinal) == true &&
            waits["on a call"] == "you're on a call" && waits["10:17 PM"] == "it runs only between 8 AM and 10 PM" &&
            lullMessage.Contains("pick the conversation back up", StringComparison.Ordinal) &&
            lullMessage.Contains("FIXTURE they said \"one more try\"", StringComparison.Ordinal) &&
            lullMessage.Contains("starts with SAY:", StringComparison.Ordinal) && lullVerdict is { Act: true, Text: { } say } &&
            say.Contains("one more try", StringComparison.Ordinal),
            new { waits = waits.ToDictionary(w => w.Key, w => w.Value ?? "runs"), message = lullMessage, lullVerdict.Text });

        // The reply's own prompts: keep the conversation going (stable, before Short first sentence and Reply length), callbacks
        // aren't repeats, and running jokes are remembered.
        var closing = Martlet.Core.Settings.PromptSettings.ReplyClosing(null, null, true, StayQuiet.Marker) ?? "";
        var emptied = Martlet.Core.Settings.PromptSettings.ReplyClosing(new Martlet.Core.Settings.PromptSettings
        {
            Overrides = new Dictionary<string, string> { [PromptCatalog.ConversationMoves] = "" }
        }, null, true, StayQuiet.Marker) ?? "";
        var repeats = CheckIns.Message(CheckIns.All(null).Single(c => c.Id == CheckIns.Repeats), facts, null) ?? "";
        step("prompts keep the conversation going", closing.StartsWith(PromptCatalog.DefaultConversationMovesInstructions, StringComparison.Ordinal) &&
            closing.EndsWith(PromptCatalog.DefaultReplyLengthInstructions, StringComparison.Ordinal) &&
            closing == Martlet.Core.Settings.PromptSettings.ReplyClosing(null, null, true, StayQuiet.Marker) &&
            !emptied.Contains("fun friend", StringComparison.Ordinal) && emptied.Length > 0 &&
            repeats.Contains("A callback is not a repeat", StringComparison.Ordinal) &&
            PromptCatalog.DefaultMemoryCaptureInstructions.Contains("running joke", StringComparison.Ordinal) &&
            PromptCatalog.DefaultMemoryBriefWritingInstructions.Contains("running jokes", StringComparison.Ordinal) &&
            PromptCatalog.DefaultCharacterWritingInstructions.Contains("example dialogue", StringComparison.Ordinal),
            new
            {
                conversationMoves = PromptCatalog.DefaultConversationMovesInstructions, closingCharacters = closing.Length,
                movesCharacters = PromptCatalog.DefaultConversationMovesInstructions.Length
            });
    }

    /// <summary>9b. Talking to someone else (FIXTURE words and voices, a canned answer, NOT AI): the built-in check-in is on and
    /// starts when always listening hears the user (at most once a minute, never on a reply alone), its message carries what was
    /// heard with who spoke and whether Martlet answered, the activity, the voices and the conversation, the answer is read on a
    /// production job board (ASIDE:, BACK, OK), and the hold goes on a production context board with every request while the
    /// user keeps talking, until <see cref="CheckIns.AsideQuiet"/> of quiet. The retired On a call's saved choice is dropped.</summary>
    private static async Task AsideAsync(Action<string, bool, object?> step, DateTimeOffset now, CheckInState facts, CancellationToken cancellation)
    {
        var aside = CheckIns.All(null).Single(c => c.Id == CheckIns.Aside);
        // A one-sided phone call Martlet can't see: only the user's voice, answers to questions Martlet never asked.
        var phone = facts with
        {
            Exchanges = [.. facts.Exchanges, new("FIXTURE: Hello? Oh, hi Mom.", "[pass]"), new("FIXTURE: Yeah, uh-huh. No, I already ate.", "Ooh, what did you have?")],
            Exchanged = 6, Activity = "", OnCall = false,
            Voices = [new("FIXTURE Owner", true, now.AddSeconds(-20)) { Id = "v1" }], OwnerVoiceKnown = true,
            Heard =
            [
                new(now.AddSeconds(-50), "FIXTURE: Hello? Oh, hi Mom.", false) { Speaker = "FIXTURE Owner", Recognized = true, Owner = true },
                new(now.AddSeconds(-20), "FIXTURE: Yeah, uh-huh. No, I already ate.", true) { Speaker = "FIXTURE Owner", Recognized = true, Owner = true }
            ]
        };
        var fired = CheckIns.SpokeTrigger(now.AddSeconds(-1), answered: true);
        var waits = new Dictionary<string, string?>
        {
            ["nothing heard"] = CheckIns.Wait(aside, phone, null),
            ["heard you, while the reply is busy"] = CheckIns.Wait(aside, phone with { Quiet = TimeSpan.Zero }, null, fired: fired),
            ["heard you, ran 20 s ago"] = CheckIns.Wait(aside, phone, new(now.AddSeconds(-20), 5, "you don't seem to be talking to someone else", false), fired: fired),
            ["a reply ended only"] = CheckIns.Wait(aside, phone, null, fired: CheckIns.ExchangeTrigger(now))
        };
        var job = CheckIns.Prepare(aside, phone, null);
        BackgroundPlace member = new("endpoint:fixture-aside", "FIXTURE member") { Slots = 1, Model = "fixture-model" };
        const string answer = "<think>Only the user's voice, a greeting to Mom, answers to questions nobody here asked.</think>\n" +
            "ASIDE: FIXTURE on the phone with their mom";
        var board = new ThinkingJobBoard(new BackgroundPlaces(), () => [member], (_, _, _) => Task.FromResult(ThinkingAnswer.Done(answer)));
        var result = job is null ? null : await board.RunAsync(job, cancellation);
        var verdict = CheckIns.Read(aside, result?.Text, phone);

        // The hold: its note goes with every request (not once) while the user keeps talking, and ends after quiet minutes.
        var context = new ContextBoard();
        var source = CheckIns.Source(aside.Id);
        var hold = CheckIns.HoldBack(null, verdict.Text ?? "x", aside.Id, now);
        context.Post(source, CheckIns.AsideNote(null, hold)!, hold.Heard, CheckIns.AsideQuiet);
        var first = context.Snapshot(now.AddSeconds(10));
        context.MarkSent(first);
        var second = context.Snapshot(now.AddSeconds(20));
        var longer = CheckIns.Spoke(hold, now.AddMinutes(2))!;
        context.Post(source, CheckIns.AsideNote(null, longer)!, longer.Heard, CheckIns.AsideQuiet);
        var stillTalking = context.Snapshot(now.AddMinutes(4));
        var quiet = context.Snapshot(now.AddMinutes(5).AddSeconds(1));
        var ended = CheckIns.Spoke(longer, now.AddMinutes(6));
        var renewed = CheckIns.HoldBack(longer, "FIXTURE still on the phone", "c1", now.AddMinutes(3));
        var heard = CheckIns.Heard(phone with { Aside = hold });
        var answers = new Dictionary<string, CheckInVerdict>
        {
            ["BACK"] = CheckIns.Read(aside, "They said bye and hung up.\nBACK", phone),
            ["OK"] = CheckIns.Read(aside, "OK", phone),
            ["ASIDE: nobody"] = CheckIns.Read(aside, "ASIDE: nobody", phone),
            ["REMIND: instead"] = CheckIns.Read(aside, "REMIND: Hush.", phone)
        };
        var emptied = CheckIns.AsideNote(new Martlet.Core.Settings.PromptSettings
        {
            Overrides = new Dictionary<string, string> { [Martlet.Core.Settings.PromptCatalog.CheckInAsideNote] = "" }
        }, hold);
        var retired = new CheckInSettings { BuiltIn = new Dictionary<string, CheckInChoice> { ["call"] = new(true, 2), [CheckIns.Gaze] = new(false, 5) } };
        var retiredRead = false;
        try
        {
            retired.Validate();
            retiredRead = retired.Choice("call") is null && retired.Choice(CheckIns.Gaze) is { On: false };
        }
        catch (Martlet.Core.Contracts.ContractException) { }
        var message = job?.Text ?? "";
        step("talking to someone else", aside is { On: true, EveryMinutes: 1, Outcome: CheckInOutcome.HoldBack, Triggers: CheckInTriggers.Spoke } &&
            aside.Facts == (CheckInFacts.Heard | CheckInFacts.Conversation | CheckInFacts.Activity | CheckInFacts.People) &&
            waits["nothing heard"] == "it waits for you to say something" && waits["heard you, while the reply is busy"] is null &&
            waits["heard you, ran 20 s ago"] == "next in 40 s" && waits["a reply ended only"] == waits["nothing heard"] &&
            job is { Kind: ThinkingJobKind.CheckIn, Reasoning: false } && LiveFloorRules.Stops(ThinkingJobKind.CheckIn) &&
            message.Contains("FIXTURE Owner (the user's own voice): \"FIXTURE: Hello? Oh, hi Mom.\" (Mira stayed quiet)", StringComparison.Ordinal) &&
            message.Contains("(Mira answered)", StringComparison.Ordinal) && message.Contains("Mira doesn't hold back now.", StringComparison.Ordinal) &&
            message.Contains("doesn't seem to be doing anything with sound", StringComparison.Ordinal) &&
            message.Contains("Only the user seems to be here.", StringComparison.Ordinal) &&
            message.Contains("User: FIXTURE: Yeah, uh-huh.", StringComparison.Ordinal) && message.Contains("starts with ASIDE:", StringComparison.Ordinal) &&
            message.Contains("write only: BACK", StringComparison.Ordinal) && !message.Contains("{heard}", StringComparison.Ordinal) &&
            result is { Succeeded: true } && verdict is { Act: true, Text: "FIXTURE on the phone with their mom" } &&
            first.Sources.Contains(source) && first.Text!.Contains("talking to someone else now, not to you (FIXTURE on the phone with their mom)", StringComparison.Ordinal) &&
            first.Text.Contains("reply only [pass]", StringComparison.Ordinal) && second.Sources.Contains(source) &&
            stillTalking.Sources.Contains(source) && !quiet.Sources.Contains(source) && ended is null &&
            renewed.Since == now && renewed.By == aside.Id && renewed.About == "FIXTURE still on the phone" &&
            heard.Contains("Mira holds back now (since 10:17 PM, 0 s ago), because the user seems to be talking to someone else: FIXTURE on the phone with their mom.", StringComparison.Ordinal) &&
            answers["BACK"] is { Act: true, Text: null, Readable: true } && answers["OK"] is { Act: false, Readable: true } &&
            answers["ASIDE: nobody"] is { Act: false, Readable: true } && answers["REMIND: instead"] is { Readable: false } &&
            emptied is null && retiredRead,
            new
            {
                waits = waits.ToDictionary(w => w.Key, w => w.Value ?? "runs"), message, verdict.Act, verdict.Text,
                firstRequestNotes = first.Text, secondRequestSources = second.Sources, afterTwoMoreMinutesOfTalk = stillTalking.Sources,
                afterQuiet = quiet.Sources, quietMinutes = CheckIns.AsideQuiet.TotalMinutes, heardWhileHolding = heard,
                answers = answers.ToDictionary(v => v.Key, v => new { v.Value.Act, v.Value.Readable, v.Value.Text }),
                retiredOnACallDropped = retiredRead
            });
    }
}
