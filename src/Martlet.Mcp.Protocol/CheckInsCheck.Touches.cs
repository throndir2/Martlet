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
    /// <summary>11. The built-in Describe touches check-in end to end with FIXTURE touches and a canned answer (NOT AI): it waits
    /// for touches, a fired trigger runs it even while the touch reply keeps the conversation busy, its message carries the touches,
    /// the conversation, the personality, the emotes and the Adult content line, the answer is read on a production job board, and
    /// the description goes on a production context board for exactly one request.</summary>
    private static async Task DescribeTouchesAsync(Action<string, bool, object?> step, DateTimeOffset now, CheckInState facts,
        CancellationToken cancellation)
    {
        var describe = CheckIns.All(null).Single(c => c.Id == CheckIns.DescribeTouches);
        var ledger = new TouchLedger();
        var clock = TimeSpan.FromMinutes(30);
        for (var i = 0; i < 2; i++)
            ledger.Record(new(PhysicalKind.Stroke, clock - TimeSpan.FromSeconds(20 - i), "down from your tail over your buttocks to your groin",
                "tail → buttocks → groin", "slowly", Zones: ["your tail", "your buttocks", "your groin"], Intimate: true));
        var state = facts with { Touches = ledger.History(clock) };
        var fired = new CheckInTrigger(CheckInTriggers.TouchesEnded, now.AddSeconds(-2), "2 touches, an intimate one");
        var waits = new Dictionary<string, string?>
        {
            ["no touches yet"] = CheckIns.Wait(describe, state, null),
            ["touches ended, the touch reply talks"] = CheckIns.Wait(describe, state with { Quiet = TimeSpan.Zero }, null, fired: fired),
            ["touches ended, ran 10 s ago"] = CheckIns.Wait(describe, state, new(now.AddSeconds(-10), 4, "nothing to add to what Martlet knows", false), fired: fired),
            ["touches ended, character hidden"] = CheckIns.Wait(describe, state with { CharacterShows = false }, null, fired: fired)
        };
        var job = CheckIns.Prepare(describe, CheckIns.Focus(describe, state), null);
        var adultJob = CheckIns.Prepare(describe, CheckIns.Focus(describe, state with { Adult = true }), null);
        BackgroundPlace member = new("endpoint:fixture-touches", "FIXTURE member") { Slots = 1, Model = "fixture-model" };
        const string answer = "<think>Two slow strokes, tail to groin.</think>\n" +
            "KNOW: FIXTURE: Their slow strokes keep sliding from your tail over your buttocks down to your groin.";
        var board = new ThinkingJobBoard(new BackgroundPlaces(), () => [member], (_, _, _) => Task.FromResult(ThinkingAnswer.Done(answer)));
        var result = job is null ? null : await board.RunAsync(job, cancellation);
        var verdict = CheckIns.Read(describe, result?.Text, state);
        var context = new ContextBoard();
        if (verdict is { Act: true, Text: { } text } && CheckIns.Context(null, text) is { } note)
            context.Post(CheckIns.Source(describe.Id), note, now, CheckIns.ContextAge, consume: true);
        var first = context.Snapshot(now.AddSeconds(30));
        context.MarkSent(first);
        var second = context.Snapshot(now.AddSeconds(40));
        var message = job?.Text ?? "";
        step("describe touches", describe is { On: true, EveryMinutes: 1, Outcome: CheckInOutcome.Context, Triggers: CheckInTriggers.TouchesEnded } &&
            waits["no touches yet"] is not null && waits["touches ended, the touch reply talks"] is null &&
            waits["touches ended, ran 10 s ago"] == "next in 50 s" && waits["touches ended, character hidden"] == "the character isn't showing" &&
            job is { Kind: ThinkingJobKind.CheckIn } && message.Contains("has been touching it", StringComparison.Ordinal) &&
            message.Contains("slowly stroked down from your tail over your buttocks to your groin twice", StringComparison.Ordinal) &&
            message.Contains("playful and teasing", StringComparison.Ordinal) && message.Contains("KNOW:", StringComparison.Ordinal) &&
            message.Contains(PromptCatalog.DefaultCheckInAdultOffInstructions, StringComparison.Ordinal) &&
            !message.Contains("{touches}", StringComparison.Ordinal) && !message.Contains("{adult}", StringComparison.Ordinal) &&
            adultJob?.Text.Contains(PromptCatalog.DefaultCheckInAdultOnInstructions, StringComparison.Ordinal) == true &&
            result is { Succeeded: true } && verdict is { Act: true } &&
            first.Sources.Contains(CheckIns.Source(CheckIns.DescribeTouches)) &&
            first.Text!.Contains("Their slow strokes keep sliding", StringComparison.Ordinal) &&
            !second.Sources.Contains(CheckIns.Source(CheckIns.DescribeTouches)),
            new
            {
                waits = waits.ToDictionary(w => w.Key, w => w.Value ?? "runs"), message, verdict.Act, verdict.Text,
                firstRequestNotes = first.Text, secondRequestSources = second.Sources
            });
    }

    /// <summary>12. How I react: the built-in check-in that changes how the character reacts to touches, as itself, with the Touch
    /// reactions tools. It waits for touches, then runs on a FIXTURE member that calls the tools (NOT AI) through the production
    /// tool host and Martlet.Avatar.Hosting's CharacterReactionTools, on FIXTURE zones; every change is bounded, and the run
    /// record keeps only each call's first line, never the character's reasons.</summary>
    private static async Task ReactionsAsync(Action<string, bool, object?> step, CheckInState facts, CancellationToken cancellation)
    {
        var checkIn = CheckIns.All(null).Single(c => c.Id == CheckIns.Reactions);
        var state = facts with { Persona = string.IsNullOrWhiteSpace(facts.Persona) ? "FIXTURE: proud and easily flustered." : facts.Persona };
        var waits = CheckIns.Wait(checkIn, state, null);
        var fired = CheckIns.Wait(checkIn, state, null, fired: new CheckInTrigger(CheckInTriggers.TouchesEnded, state.Now, "FIXTURE: 3 touches"));
        var persona = Guid.NewGuid();
        var zones = new CharacterTouchZoneSettings
        {
            ModelId = "fixture-model",
            Zones = [new() { Id = "top_of_head", Box = new(0.4, 0, 0.2, 0.1) }, new() { Id = "hand_left", Box = new(0.7, 0.5, 0.1, 0.1) }]
        };
        var inventory = new CharacterActionInventory("fixture-model", Martlet.Avatars.AvatarRenderer.Live2D,
            [.. CharacterActionInventory.AllGestures.Select(g => new CharacterActionSource(g.Id, CharacterActionKind.Gesture, g.Name, g.Does))]);
        var catalog = new CharacterActionCatalog(inventory, CharacterActions.Merge(inventory, null));
        IReadOnlyList<CharacterReactionChange> changes = [];
        var handlers = new Dictionary<string, CheckInToolHandler>
        {
            [TouchReactions.SetId] = (call, context, _) =>
            {
                var answer = CharacterReactionTools.Call(call.Name, call.ArgumentsJson, new ReactionToolContext
                {
                    PersonaId = Guid.Parse(context.PersonaId!), Who = state.Who, Zones = zones, Catalog = catalog,
                    CheckInId = context.CheckInId, Run = context.Now, Now = context.Now
                }, changes);
                if (answer.Changes is { } next) changes = next;
                return ValueTask.FromResult(new ConversationToolResult(answer.Result, answer.Failed));
            }
        };
        var host = new CheckInToolHost(checkIn.ToolSets, new(checkIn.Id, checkIn.Name, persona.ToString(), state.Now), handlers);
        var job = CheckIns.Prepare(checkIn, CheckIns.Focus(checkIn, state), null, host);
        var message = job?.Text ?? "";
        BackgroundPlace toolMember = new("endpoint:tools", "FIXTURE member that calls tools")
        {
            Slots = 1, Model = "fixture-tools", Can = ThinkingCapability.Text | ThinkingCapability.Tools
        };
        var board = new ThinkingJobBoard(new BackgroundPlaces(), () => [toolMember], async (_, asked, token) =>
        {
            var n = 0;
            async Task Call(string name, string arguments) =>
                await asked.ToolHost!.CallAsync(new TextToolCall("call-" + n++, name, arguments), token);
            await Call(TouchReactions.Read, "{}");
            await Call(TouchReactions.Mood, """{"shift":-1,"hours":3,"why":"FIXTURE private: they mocked my ears."}""");
            await Call(TouchReactions.Feel, """{"target":"head","feeling":"hates","why":"FIXTURE private: no head pats now."}""");
            await Call(TouchReactions.Feel, """{"target":"arms","feeling":"dislikes","why":"FIXTURE private: and the arms."}""");
            await Call(TouchReactions.React, """{"zone":"hand_left","plays":["shake","sound:sigh"],"why":"FIXTURE private: hands off."}""");
            await Call(TouchReactions.Mood, """{"shift":-2}""");
            await Call(TouchReactions.Feel, """{"target":"torso","feeling":"dislikes","why":"FIXTURE private: one too many."}""");
            return ThinkingAnswer.Done("Sulking after being teased, so touches are less welcome for a while.");
        });
        var done = job is null ? null : await board.RunAsync(job, cancellation);
        var uses = host.Uses;
        var active = CharacterReactionChanges.Active(changes, persona, state.Now);
        var felt = CharacterReactionChanges.Temperament(null, active, persona);
        var hand = CharacterReactionChanges.Zone(zones.Zones[1], null, active, catalog);
        step("how I react: the built-in check-in changes how the character reacts, bounded",
            checkIn is { On: true, Outcome: CheckInOutcome.Tools, Triggers: CheckIns.ByTouches } && checkIn.ToolSets.SequenceEqual([TouchReactions.SetId]) &&
            checkIn.Needs.HasFlag(ThinkingCapability.Tools) && waits is not null && waits.StartsWith("it waits for", StringComparison.Ordinal) && fired is null &&
            job is not null && job.Tools.Select(t => t.Name).SequenceEqual(CharacterReactionTools.Names) && message.Contains("read_touch_reactions", StringComparison.Ordinal) &&
            message.Contains("How the user touched", StringComparison.Ordinal) && done is { Succeeded: true } &&
            uses.Count == 7 && uses[0].Result.StartsWith("How ", StringComparison.Ordinal) && !uses[0].Failed &&
            uses.Skip(1).Take(4).All(u => !u.Failed && u.Set == TouchReactions.SetId) && uses[5].Failed &&
            uses[6] is { Failed: true } && uses[6].Result.Contains("already made 4 changes", StringComparison.Ordinal) &&
            !uses.Any(u => u.Result.Contains("private", StringComparison.Ordinal)) && active.Count == 4 &&
            felt?.Groups["head"].Attitude == -2 && felt.Groups["arms"].Attitude == -1 && felt.Groups["lower_body"].Attitude == -1 &&
            hand.Reaction.Actions is ["gesture:shake", "sound:sigh"],
            new
            {
                waits, fired, tools = job?.Tools.Select(t => t.Name), member = done?.Member, made = CheckIns.ToolsText(uses),
                changes = active.Select(c => CharacterReactionChanges.Describe(c, zones, catalog)),
                hand = hand.Reaction.Actions
            });
    }

    /// <summary>7. The Touches fact ({touches}): FIXTURE touches on a production <see cref="TouchLedger"/>, some taken by replies
    /// and some still waiting, read without taking them and filled into one of the owner's check-ins (NOT AI).</summary>
    private static void TouchesFact(Action<string, bool, object?> step, DateTimeOffset now, CheckInState facts)
    {
        var ledger = new TouchLedger();
        var clock = TimeSpan.FromMinutes(30);
        TimeSpan Ago(double seconds) => clock - TimeSpan.FromSeconds(seconds);
        PhysicalEvent Stroke(TimeSpan at) => new(PhysicalKind.Stroke, at, "down from your tail over your buttocks to your groin",
            "tail → buttocks → groin", "slowly", Zones: ["your tail", "your buttocks", "your groin"], Intimate: true,
            Feeling: "FIXTURE: you love being touched there");
        // Earlier touches, each taken by a reply as the desktop's ledger does; then strokes that wait for the next reply.
        for (var i = 0; i < 3; i++) ledger.Record(new(PhysicalKind.Pat, Ago(480 - i), "the top of your head", "top of head", Zones: ["the top of your head"]));
        ledger.Drain(Ago(470));
        for (var i = 0; i < 2; i++) ledger.Record(Stroke(Ago(300)));
        ledger.Drain(Ago(290));
        ledger.Record(new(PhysicalKind.Moved, Ago(180), Detail: "to their other monitor"));
        ledger.Record(new(PhysicalKind.Tap, Ago(60), "your groin", "groin", Zones: ["your groin"], Intimate: true));
        ledger.Drain(Ago(50));
        for (var i = 0; i < 3; i++) ledger.Record(Stroke(Ago(10)));
        var waitingBefore = ledger.Peek(clock)?.Line;
        var history = ledger.History(clock);
        var waitingAfter = ledger.Peek(clock)?.Line;
        var taken = ledger.Drain(clock);
        var kept = ledger.History(clock);
        var state = facts with { Touches = history };
        var placed = CheckIns.Message(CheckIns.Of(new CustomCheckIn
        {
            Id = "c7", Name = "FIXTURE touches", Task = "How the user touched {name} lately:\n{touches}\nDescribe it.", Facts = CheckInFacts.Touches
        }), state, null) ?? "";
        var ticked = CheckIns.Message(CheckIns.Of(new CustomCheckIn
        {
            Id = "c7", Name = "FIXTURE touches", Task = "Describe how the user touched the character.", Facts = CheckInFacts.Touches
        }), state, null) ?? "";
        var none = CheckIns.Touches(facts with { Touches = null });
        var refused = false;
        try { new CheckInSettings().With(new CustomCheckIn { Id = "c7", Name = "FIXTURE", Facts = (CheckInFacts)(1 << 20) }).Validate(); }
        catch (Martlet.Core.Contracts.ContractException) { refused = true; }
        var accepted = true;
        try { new CheckInSettings().With(new CustomCheckIn { Id = "c7", Name = "FIXTURE", Facts = CheckInFacts.Touches }).Validate(); }
        catch (Martlet.Core.Contracts.ContractException) { accepted = false; }
        const string header = "What the user did to Mira's character on the desktop in the last 10 minutes, oldest first";
        step("touches fact", history is { Count: 10, Intimate: 6, Entries.Count: 5 } && history.Often is [{ Place: "your groin", Count: 6 }, ..] &&
            waitingBefore is not null && waitingBefore == waitingAfter && taken is { Count: 3 } && kept?.Entries.Count == 5 &&
            CheckIns.Placed("{touches}") == CheckInFacts.Touches &&
            placed.StartsWith("How the user touched Mira lately:\n" + header, StringComparison.Ordinal) &&
            placed.Contains("- 10:09 PM (8 min ago): They patted the top of your head 3 times over 2 seconds.", StringComparison.Ordinal) &&
            placed.Contains("- 10:12 PM (5 min ago, intimate): They slowly stroked down from your tail over your buttocks to your groin twice " +
                "(FIXTURE: you love being touched there).", StringComparison.Ordinal) &&
            placed.Contains("- 10:14 PM (3 min ago): They moved you to their other monitor.", StringComparison.Ordinal) &&
            placed.Contains("They keep coming back to your groin (6 times)", StringComparison.Ordinal) &&
            placed.IndexOf(header, StringComparison.Ordinal) == placed.LastIndexOf(header, StringComparison.Ordinal) &&
            ticked.Contains(header, StringComparison.Ordinal) && none == "The user didn't touch Mira's character on the desktop in the last 10 minutes." &&
            refused && accepted,
            new
            {
                runs = history?.Entries.Count, things = history?.Count, intimate = history?.Intimate, often = history?.Often?.Count,
                readingLeftTheNextReply = waitingBefore is not null && waitingBefore == waitingAfter, replyTook = taken?.Count,
                keptAfterTheReply = kept?.Entries.Count, filled = placed.Contains(header, StringComparison.Ordinal) && !placed.Contains("{touches}", StringComparison.Ordinal),
                unknownFactRefused = refused, touchesFactAccepted = accepted, fixtureMessage = placed, nothingTouched = none
            });
    }
}
