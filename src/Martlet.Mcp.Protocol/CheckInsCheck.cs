using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Martlet.Avatar.Hosting;
using Martlet.Conversation;
using Martlet.Core.Settings;
using Martlet.Providers;

namespace Martlet.Mcp;

/// <summary>check_ins_status and check_ins_check: Martlet's check-ins (Companion › Check-ins; docs/CONVERSATION.md#check-ins).
/// The status reads a data directory's check-ins.json (the owner's choices and own check-ins) and check-ins-status.json (written
/// by the desktop on a companion PC: the pool member that can take them, why each waits and its last run; never what was said or
/// answered). The check rehearses the production code end to end with FIXTURE facts and canned answers (NOT AI): the job kind's
/// rules, check-ins.json saved and read back (and a bad one refused), when each built-in check-in waits or runs, the message it
/// sends, its run on a production ThinkingJobBoard with a fixture member, reading the answers (OFF tags, KEEP, USUAL, REMIND:,
/// SAY:, OK, a &lt;think&gt; block, an answer that can't be read), and what Martlet then does: a reply's lingering emotes off on a
/// production HeldEmotes (never the owner's try), a reminder on a production ContextBoard that goes with exactly one request,
/// and something to bring up worded through BackgroundJobs beside a due reminder. Then the owner's inputs (screenshot, sound,
/// script) and tool sets: a check-in's tools called in a bounded loop only on a member whose model can call tools, with each call
/// kept for the run record. No model, network or credential is used.</summary>
internal static partial class CheckInsCheck
{
    internal const string StatusFile = "check-ins-status.json";

    internal static Task<object> StatusAsync(string dataDirectory, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var (settings, state) = CheckInSettings.Read(dataDirectory);
        object desktop;
        var path = Path.Combine(dataDirectory, StatusFile);
        try
        {
            desktop = File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) ?? (object)new { state = "empty" }
                : new { state = "none", why = "The desktop writes check-ins-status.json once it runs as a companion PC." };
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            desktop = new { state = "unreadable", why = error.Message };
        }
        return Task.FromResult<object>(new
        {
            settings = new
            {
                state, file = CheckInSettings.FileName,
                checkIns = CheckIns.All(settings).Select(c => new
                {
                    id = c.Id, name = c.Name, custom = c.Custom, on = c.On, everyMinutes = c.EveryMinutes, outcome = c.Outcome.ToString(),
                    prompt = c.PromptId, facts = c.Facts.ToString(), conditions = c.Conditions.ToString(), task = c.Task, does = c.Does,
                    changed = !c.Custom && settings.Choice(c.Id) is { } choice && choice != new CheckInChoice(choice.On, choice.EveryMinutes),
                    needs = c.Needs.ToString(), screenshot = c.Screenshot, recording = c.Recording.ToString(),
                    recordingSeconds = c.Recording == CheckInRecording.None ? (int?)null : c.RecordingSeconds,
                    // Whether it runs a script and how long it is; never the script (the owner's own words).
                    script = c.RunsScript, scriptCharacters = c.Script?.Length ?? 0, triggers = c.Triggers.ToString(),
                    hours = c.Conditions.HasFlag(CheckInConditions.Between) ? $"{CheckIns.Clock(c.FromHour)}-{CheckIns.Clock(c.UntilHour)}" : null,
                    mostPerHour = c.MostPerHour == 0 ? (int?)null : c.MostPerHour, toolSets = c.ToolSets
                }).ToArray()
            },
            desktop,
            rules = Rules()
        });
    }

    /// <summary>The fixed rules check-ins follow, from the production code.</summary>
    internal static object Rules() => new
    {
        tickSeconds = 15, minimumShownMinutes = CheckIns.MinimumShown.TotalMinutes, settleSeconds = CheckIns.Settle.TotalSeconds,
        idleMinutes = CheckIns.Idle.TotalMinutes, promiseWindowMinutes = CheckIns.PromiseWindow.TotalMinutes,
        noteAgeMinutes = CheckIns.NoteAge.TotalMinutes, timeoutSeconds = CheckIns.Timeout.TotalSeconds, keptPace = CheckIns.KeptPace,
        characterReplies = CheckIns.CharacterReplies, repeatsSayings = CheckIns.RepeatsSayings,
        saidLatelyMinutes = SaidLately.Window.TotalMinutes, everyChoices = CheckIns.EveryChoices, maximumCustom = CheckIns.MaximumCustom,
        recordingChoices = CheckIns.RecordingChoices, maximumScriptCharacters = CheckIns.MaximumScriptCharacters,
        maximumScriptOutputCharacters = CheckIns.MaximumScriptOutputCharacters, scriptTimeoutSeconds = CheckIns.ScriptTimeout.TotalSeconds,
        jobKind = ThinkingJobKinds.Name(ThinkingJobKind.CheckIn), priority = ThinkingJobKinds.Priority(ThinkingJobKind.CheckIn).ToString(),
        fast = ThinkingJobKinds.IsFast(ThinkingJobKind.CheckIn), stoppedWhenLive = LiveFloorRules.Stops(ThinkingJobKind.CheckIn),
        triggers = CheckIns.AllTriggers.ToString(), triggerAgeSeconds = CheckIns.TriggerAge.TotalSeconds, exchangeAgeSeconds = CheckIns.ExchangeAge.TotalSeconds,
        touchesSettleMs = TouchDebounce.Quiet.TotalMilliseconds, strokeZones = CheckIns.StrokeZones,
        oftenTouches = TouchLedger.OftenTouches, oftenWindowMinutes = TouchLedger.OftenWindow.TotalMinutes,
        signalWindowMinutes = CheckIns.SignalWindow.TotalMinutes, unansweredAfterMinutes = CheckIns.UnansweredAfter.TotalMinutes,
        unansweredWithinMinutes = CheckIns.UnansweredWithin.TotalMinutes, peopleWindowMinutes = CheckIns.PeopleWindow.TotalMinutes,
        lullAfterMinutes = CheckIns.LullAfter.TotalMinutes, maximumCallbacks = CallbackList.MaximumItems,
        maximumCallbackCharacters = CallbackList.MaximumCharacters, callbackWindowHours = CallbackList.Window.TotalHours,
        mostPerHourChoices = CheckIns.MostPerHourChoices,
        maximumToolRounds = CheckIns.MaximumToolRounds, maximumToolCalls = CheckIns.MaximumToolCalls,
        maximumToolOutputTokens = CheckIns.MaximumToolOutputTokens, toolTimeoutSeconds = CheckIns.ToolTimeout.TotalSeconds,
        hardMaxToolRounds = BoundedTextInput.HardMaxToolRounds,
        maximumToolResultCharacters = CheckIns.MaximumToolResultCharacters,
        toolSets = CheckInToolSets.All.Select(s => new { id = s.Id, name = s.Name, does = s.Does, tools = s.Tools.Select(t => t.Name).ToArray(), replaces = s.Replaces.ToArray(), optional = s.Optional }).ToArray()
    };

    internal static async Task<object> RunAsync(CancellationToken cancellation)
    {
        var steps = new List<object>();
        var ok = true;
        void Step(string name, bool passed, object? detail = null)
        {
            ok &= passed;
            steps.Add(new { name, passed, detail });
        }

        // 1. The job kind: at the helpers' priority, never the fast slot, stopped (and queued again) while the conversation is live.
        Step("job kind", ThinkingJobKinds.Name(ThinkingJobKind.CheckIn) == "check-in" &&
            ThinkingJobKinds.Priority(ThinkingJobKind.CheckIn) == ThinkingPriority.Helper && !ThinkingJobKinds.IsFast(ThinkingJobKind.CheckIn) &&
            LiveFloorRules.Stops(ThinkingJobKind.CheckIn), Rules());

        // 2. check-ins.json: saved and read back; a bad pace is refused; the built-in ones are on by default.
        var folder = Path.Combine(Path.GetTempPath(), "Martlet.CheckIns." + Guid.NewGuid().ToString("N"));
        try
        {
            var mine = new CustomCheckIn
            {
                Id = "c1", Name = "FIXTURE break", On = true, EveryMinutes = 60, Task = "If the user has been at it for hours, suggest a short break.",
                Facts = CheckInFacts.Conversation | CheckInFacts.Presence, Outcome = CheckInOutcome.Say
            };
            var saved = new CheckInSettings().With(CheckIns.Gaze, false, 10).With(mine)
                .With(CheckIns.Promises, new CheckInChoice(true, 1) { Outcome = CheckInOutcome.Say, Facts = CheckInFacts.Conversation | CheckInFacts.Said });
            var wrote = saved.Save(folder);
            var (read, state) = CheckInSettings.Read(folder);
            var all = CheckIns.All(read);
            var refused = false;
            try { new CheckInSettings().With(CheckIns.Emotes, true, 7).Validate(); }
            catch (Martlet.Core.Contracts.ContractException) { refused = true; }
            Step("settings saved and read back", wrote && state == "loaded" && all.Count == CheckIns.BuiltIn.Count + 1 &&
                all.Single(c => c.Id == CheckIns.Gaze) is { On: false, EveryMinutes: 10 } && all.Single(c => c.Id == "c1") is { Custom: true, Outcome: CheckInOutcome.Say } &&
                all.Single(c => c.Id == CheckIns.Promises) is { EveryMinutes: 1, Outcome: CheckInOutcome.Say, Facts: CheckInFacts.Conversation | CheckInFacts.Said } promises &&
                promises.Conditions == CheckIns.BuiltIn.Single(c => c.Id == CheckIns.Promises).Conditions &&
                CheckIns.All(new CheckInSettings()).All(c => c.On == !Signaled(c.Id)) && refused,
                new
                {
                    state, checkIns = all.Select(c => $"{c.Id}: {(c.On ? "on" : "off")}, every {c.EveryMinutes} min, {c.Outcome}, facts {c.Facts}, when {c.Conditions}"),
                    badPaceRefused = refused
                });
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); }
            catch (IOException) { }
        }

        // 3. When each check-in waits and when it runs (FIXTURE facts on this PC at 22:17).
        var now = new DateTimeOffset(2026, 10, 7, 22, 17, 0, TimeSpan.FromHours(-7));
        CheckIn Built(string id) => CheckIns.All(null).Single(c => c.Id == id);
        var exchanges = new CheckInExchange[]
        {
            new("FIXTURE: I finally beat that boss!", "{blush} No way, you did it! I'm so proud of you!"),
            new("FIXTURE: Remind me to check the oven in 10 minutes.", "{glasses} Sure thing, I'll remind you in 10 minutes!"),
            new("FIXTURE: Okay, back to work now.", "Got it, good luck with the report!"),
            new("FIXTURE: This bug is driving me crazy.", "Ooh, bugs are the worst. Want to talk it through?")
        };
        var facts = new CheckInState
        {
            Now = now, Name = "Mira", Persona = "FIXTURE: Mira is playful and teasing, and keeps her replies short.", Conversation = true,
            Exchanges = exchanges, Exchanged = 4, Quiet = TimeSpan.FromMinutes(2), Away = TimeSpan.FromSeconds(20), CharacterShows = true,
            Emotes = [new("blush", "when flattered or embarrassed", TimeSpan.FromMinutes(14)), new("glasses", "when reading or thinking", TimeSpan.FromMinutes(9)),
                new("smile", "when happy", TimeSpan.FromSeconds(40))],
            Gaze = new("look straight ahead and ignore the pointer", "follow the user's mouse pointer wherever it goes", TimeSpan.FromMinutes(12)),
            Work = ["Thinking longer (running): FIXTURE plan for the trip"],
            Said =
            [
                new(now.AddMinutes(-31), "FIXTURE: Ooh, that boss is almost down!"),
                new(now.AddMinutes(-20), "{blush} No way, you did it! I'm so proud of you!"),
                new(now.AddMinutes(-12), "FIXTURE: Ooh, that boss is almost down!"),
                new(now.AddMinutes(-2), "FIXTURE: Ooh, that boss is almost down!")
            ]
        };
        // The emotes check-in's pace comes from its built-in; KeptPace slows it after an answer that kept every emote.
        var emotesPace = Built(CheckIns.Emotes).Every;
        var waits = new Dictionary<string, string?>
        {
            ["emotes: due"] = CheckInSchedule.Wait(Built(CheckIns.Emotes), facts, null),
            ["emotes: only young ones"] = CheckInSchedule.Wait(Built(CheckIns.Emotes), facts with { Emotes = [facts.Emotes[2]] }, null),
            ["emotes: character hidden"] = CheckInSchedule.Wait(Built(CheckIns.Emotes), facts with { CharacterShows = false }, null),
            ["emotes: ran half its pace ago"] = CheckInSchedule.Wait(Built(CheckIns.Emotes), facts,
                new(now - emotesPace / 2, 4, "every emote still fits", false)),
            ["emotes: kept twice its pace ago, nothing new"] = CheckInSchedule.Wait(Built(CheckIns.Emotes), facts,
                new(now - emotesPace * 2, 4, "every emote still fits", false) { Kept = true }),
            ["emotes: you talk"] = CheckInSchedule.Wait(Built(CheckIns.Emotes), facts with { Quiet = TimeSpan.Zero }, null),
            ["emotes: nobody here"] = CheckInSchedule.Wait(Built(CheckIns.Emotes), facts with { Away = TimeSpan.FromMinutes(25) }, null),
            ["gaze: due"] = CheckInSchedule.Wait(Built(CheckIns.Gaze), facts, null),
            ["gaze: chosen 1 min ago"] = CheckInSchedule.Wait(Built(CheckIns.Gaze), facts with { Gaze = facts.Gaze! with { Since = TimeSpan.FromMinutes(1) } }, null),
            ["promises: new exchanges"] = CheckInSchedule.Wait(Built(CheckIns.Promises), facts, new(now.AddMinutes(-6), 3, "nothing to remind Martlet of", false)),
            ["promises: nothing new"] = CheckInSchedule.Wait(Built(CheckIns.Promises), facts, new(now.AddMinutes(-6), 4, "nothing to remind Martlet of", false)),
            ["character: 4 new replies"] = CheckInSchedule.Wait(Built(CheckIns.Character), facts, null),
            ["character: no personality"] = CheckInSchedule.Wait(Built(CheckIns.Character), facts with { Persona = null }, null),
            ["repeats: said enough"] = CheckInSchedule.Wait(Built(CheckIns.Repeats), facts, new(now.AddMinutes(-11), 3, "nothing to remind Martlet of", false)),
            ["repeats: said too little"] = CheckInSchedule.Wait(Built(CheckIns.Repeats), facts with { Said = [.. facts.Said.Take(2)] }, null),
            ["repeats: nothing new"] = CheckInSchedule.Wait(Built(CheckIns.Repeats), facts, new(now.AddMinutes(-11), 4, "nothing to remind Martlet of", false)),
            ["own: off, but Check now"] = CheckInSchedule.Wait(CheckIns.Of(new() { Id = "c2", Name = "FIXTURE", Task = "Check something." }), facts,
                new(now.AddMinutes(-1), 4, "nothing to remind Martlet of", false), now: true)
        };
        Step("when check-ins wait", waits["emotes: due"] is null && waits["emotes: only young ones"]?.Contains("3 min") == true &&
            waits["emotes: character hidden"] == "the character isn't showing" &&
            waits["emotes: ran half its pace ago"] == "next in " + Reminders.Span(emotesPace / 2) &&
            waits["emotes: kept twice its pace ago, nothing new"] == "next in " + Reminders.Span(emotesPace * (CheckIns.KeptPace - 2)) &&
            waits["emotes: you talk"] == "the conversation is busy" && waits["emotes: nobody here"]?.StartsWith("nobody used this PC", StringComparison.Ordinal) == true &&
            waits["gaze: due"] is null && waits["gaze: chosen 1 min ago"] is not null && waits["promises: new exchanges"] is null &&
            waits["promises: nothing new"] == "nothing new was said since the last check" && waits["character: 4 new replies"] is null &&
            waits["character: no personality"] is not null && waits["own: off, but Check now"] is null &&
            waits["repeats: said enough"] is null && waits["repeats: said too little"] == "it needs at least 3 things Martlet said in the last hour" &&
            waits["repeats: nothing new"] == "nothing new was said since the last check",
            waits.ToDictionary(w => w.Key, w => w.Value ?? "runs"));

        // 4. The messages and their run on a production job board with a fixture member (canned answers, NOT AI).
        var answers = new Dictionary<string, string>
        {
            [CheckIns.Emotes] = "<think>The blush was for the win, which is over; glasses fit the bug hunt.</think>\nOFF {blush}",
            [CheckIns.Gaze] = "Hmm, she is helping with a bug now, so following the mouse fits better.\n**USUAL**",
            [CheckIns.Promises] = "- REMIND: You said you'd remind them to check the oven in 10 minutes but never set the reminder; set it now, or tell them you can't.",
            [CheckIns.Character] = "OK",
            [CheckIns.Repeats] = "<think>The boss remark came three times.</think>\nREMIND: You said the boss was almost down three times in " +
                "30 minutes; don't bring it up again unless something changes, and talk about something new.",
            ["c1"] = "SAY: They've been at it for hours; suggest a short stretch break.",
            [CheckIns.Welcome] = "SAY: Welcome back! Your trip plan finished while you were away.",
            [CheckIns.Unanswered] = "SAY: No pressure, but I'm still curious what you think!",
            [CheckIns.Aside] = "ASIDE: FIXTURE on the phone with their mom",
            [CheckIns.Others] = "REMIND: Someone else is here; don't share private things about the user.",
            [CheckIns.DescribeTouches] = "KNOW: FIXTURE: Their slow strokes keep sliding from your tail down to your groin.",
            [CheckIns.Reactions] = "Sulking for a while after being teased, so touches are less welcome.",
            [CheckIns.Ahead] = "PLAN: FIXTURE: They'll probably say the bug is fixed; I'll cheer, then tease them about how long it took.",
            [CheckIns.Actions] = "OK",
            [CheckIns.MemoryCare] = "FIXTURE: Added (in Minecraft) to 2 facts about the castle.",
            [CheckIns.Lull] = "SAY: FIXTURE: So did that \"one more try\" ever end?",
            [CheckIns.Callbacks] = "CALLBACK: quote - FIXTURE they said \"one more try\" about the boss\nCALLBACK: open - FIXTURE a bug drives them crazy"
        };
        var places = new BackgroundPlaces();
        BackgroundPlace member = new("endpoint:fixture", "FIXTURE member") { Slots = 2, Model = "fixture-model" };
        var asked = new List<(ThinkingJobKind Kind, ThinkingPriority? Priority, string Instructions, string Text)>();
        var board = new ThinkingJobBoard(places, () => [member], (_, job, _) =>
        {
            lock (asked) asked.Add((job.Kind, job.Priority, job.Instructions, job.Text));
            var id = answers.Keys.First(key => job.Text.Contains(Marker(key), StringComparison.Ordinal));
            return Task.FromResult(ThinkingAnswer.Done(answers[id]));
        });
        var own = CheckIns.Of(new()
        {
            Id = "c1", Name = "FIXTURE break", On = true, EveryMinutes = 60, Task = "If the user has been at it for hours, suggest a short break. " + Marker("c1"),
            Facts = CheckInFacts.Conversation | CheckInFacts.Presence, Outcome = CheckInOutcome.Say
        });
        var verdicts = new Dictionary<string, CheckInVerdict>();
        var messages = new Dictionary<string, object>();
        foreach (var checkIn in new[] { Built(CheckIns.Emotes), Built(CheckIns.Gaze), Built(CheckIns.Promises), Built(CheckIns.Character),
            Built(CheckIns.Repeats), own })
        {
            var focused = CheckInSchedule.Focus(checkIn, facts);
            var job = CheckInPrompts.Prepare(checkIn, focused, null);
            if (job is null)
            {
                Step("message: " + checkIn.Id, false, "no job");
                continue;
            }
            // The fixture member finds which check-in asks by a marker the check adds to the message only here.
            var marked = job with { Text = job.Text + "\n" + Marker(checkIn.Id) };
            var result = await board.RunAsync(marked, cancellation);
            verdicts[checkIn.Id] = CheckInAnswers.Read(checkIn, result.Text, focused);
            messages[checkIn.Id] = new { member = result.Member, outcome = result.Outcome.ToString(), message = job.Text };
        }
        var emotesMessage = asked.FirstOrDefault(a => a.Text.Contains(Marker(CheckIns.Emotes), StringComparison.Ordinal)).Text ?? "";
        Step("messages carry the facts each needs", asked.Count == 6 && asked.All(a => a.Kind == ThinkingJobKind.CheckIn && a.Instructions.Length > 0) &&
            emotesMessage.Contains("{blush} - when flattered", StringComparison.Ordinal) && emotesMessage.Contains("{glasses}", StringComparison.Ordinal) &&
            !emotesMessage.Contains("{smile}", StringComparison.Ordinal) && emotesMessage.Contains("OFF {blush}", StringComparison.Ordinal) &&
            emotesMessage.Contains("Wednesday, October 7, 10:17 PM", StringComparison.Ordinal) &&
            asked.Any(a => a.Text.Contains("look straight ahead", StringComparison.Ordinal) && a.Text.Contains("12 min ago", StringComparison.Ordinal)) &&
            asked.Any(a => a.Text.Contains("FIXTURE plan for the trip", StringComparison.Ordinal) && a.Text.Contains("check the oven", StringComparison.Ordinal)) &&
            asked.Any(a => a.Text.Contains("playful and teasing", StringComparison.Ordinal) && a.Text.Contains("1. {blush} No way", StringComparison.Ordinal)) &&
            asked.Any(a => a.Text.Contains("- 10:05 PM (12 min ago): \"FIXTURE: Ooh, that boss is almost down!\"", StringComparison.Ordinal) &&
                a.Text.Contains("- 10:15 PM (2 min ago):", StringComparison.Ordinal) && a.Text.Contains("keep saying the same", StringComparison.Ordinal)) &&
            asked.Any(a => a.Text.Contains("SAY:", StringComparison.Ordinal) && a.Text.Contains("Nobody has used this PC", StringComparison.Ordinal) == false &&
                a.Text.Contains("The user is using this PC now.", StringComparison.Ordinal)),
            messages);
        Step("answers read", verdicts.GetValueOrDefault(CheckIns.Emotes) is { Act: true, Tags: ["blush"] } &&
            verdicts.GetValueOrDefault(CheckIns.Gaze) is { Act: true } &&
            verdicts.GetValueOrDefault(CheckIns.Promises) is { Act: true, Text: { } remind } && remind.StartsWith("You said you'd remind them", StringComparison.Ordinal) &&
            verdicts.GetValueOrDefault(CheckIns.Character) is { Act: false, Readable: true } &&
            verdicts.GetValueOrDefault(CheckIns.Repeats) is { Act: true, Text: { } repeated } && repeated.StartsWith("You said the boss was almost down", StringComparison.Ordinal) &&
            verdicts.GetValueOrDefault("c1") is { Act: true, Text: { } say } && say.Contains("stretch break", StringComparison.Ordinal),
            verdicts.ToDictionary(v => v.Key, v => new { v.Value.Act, v.Value.Readable, v.Value.Tags, v.Value.Text }));
        var odd = new Dictionary<string, CheckInVerdict>
        {
            ["emotes: KEEP"] = CheckInAnswers.Read(Built(CheckIns.Emotes), "KEEP", CheckInSchedule.Focus(Built(CheckIns.Emotes), facts)),
            ["emotes: a tag that isn't asked about"] = CheckInAnswers.Read(Built(CheckIns.Emotes), "OFF {smile}", CheckInSchedule.Focus(Built(CheckIns.Emotes), facts)),
            ["emotes: chatter"] = CheckInAnswers.Read(Built(CheckIns.Emotes), "Sure! Here is what I think about the emotes.", CheckInSchedule.Focus(Built(CheckIns.Emotes), facts)),
            ["promises: REMIND: nothing"] = CheckInAnswers.Read(Built(CheckIns.Promises), "REMIND: nothing.", facts),
            ["gaze: KEEP after thinking"] = CheckInAnswers.Read(Built(CheckIns.Gaze), "Usually it follows the mouse.\nKEEP", facts)
        };
        Step("odd answers change nothing", odd["emotes: KEEP"] is { Act: false, Readable: true } && odd["emotes: a tag that isn't asked about"] is { Act: false } &&
            odd["emotes: chatter"] is { Readable: false } && odd["promises: REMIND: nothing"] is { Act: false, Readable: true } &&
            odd["gaze: KEEP after thinking"] is { Act: false, Readable: true }, odd.ToDictionary(v => v.Key, v => new { v.Value.Act, v.Value.Readable }));

        // 4b. Every built-in check-in is only data: one of your own with its prompt and choices asks, waits and reads the same,
        //     and your own can know what Martlet said in the last hour.
        var recreated = new Dictionary<string, object>();
        var same = true;
        var lastRun = new CheckInRun(now.AddMinutes(-6), 4, "nothing to remind Martlet of", false);
        CheckInState[] states =
        [
            facts, facts with { CharacterShows = false }, facts with { Persona = null }, facts with { Said = [] }, facts with { Gaze = null },
            facts with { Emotes = [] }, facts with { Exchanges = [], Exchanged = 0 }, facts with { Quiet = TimeSpan.FromMinutes(45) }
        ];
        foreach (var builtIn in CheckIns.BuiltIn.Select(b => b with { On = true }))
        {
            var copy = CheckIns.Of(new CustomCheckIn
            {
                Id = "c9", Name = builtIn.Name + " (copy)", On = true, EveryMinutes = builtIn.EveryMinutes,
                Task = CheckInPrompts.Template(builtIn, null) ?? "", Facts = builtIn.Facts, Conditions = builtIn.Conditions, Outcome = builtIn.Outcome,
                Triggers = builtIn.Triggers, ToolSets = builtIn.ToolSets
            });
            var sameMessage = CheckInPrompts.Message(builtIn, CheckInSchedule.Focus(builtIn, facts), null) == CheckInPrompts.Message(copy, CheckInSchedule.Focus(copy, facts), null);
            var sameWaits = states.All(s => CheckInSchedule.Wait(builtIn, s, null) == CheckInSchedule.Wait(copy, s, null) &&
                CheckInSchedule.Wait(builtIn, s, lastRun) == CheckInSchedule.Wait(copy, s, lastRun));
            var builtInVerdict = CheckInAnswers.Read(builtIn, answers[builtIn.Id], CheckInSchedule.Focus(builtIn, facts));
            var copyVerdict = CheckInAnswers.Read(copy, answers[builtIn.Id], CheckInSchedule.Focus(copy, facts));
            var sameRead = builtInVerdict.Act == copyVerdict.Act && builtInVerdict.Tags.SequenceEqual(copyVerdict.Tags) && builtInVerdict.Text == copyVerdict.Text;
            same &= sameMessage && sameWaits && sameRead;
            recreated[builtIn.Id] = new { message = sameMessage, waits = sameWaits, read = sameRead };
        }
        var saidOwn = CheckInPrompts.Message(CheckIns.Of(new CustomCheckIn
        {
            Id = "c8", Name = "FIXTURE", Task = "Check whether Martlet repeats itself.", Facts = CheckInFacts.Said | CheckInFacts.Replies
        }), facts, null) ?? "";
        Step("built-in ones recreated as your own", same && saidOwn.Contains("said in the last hour, oldest first", StringComparison.Ordinal) &&
            saidOwn.Contains("- 10:05 PM (12 min ago):", StringComparison.Ordinal) && saidOwn.Contains("last replies, oldest first", StringComparison.Ordinal) &&
            saidOwn.Contains("REMIND:", StringComparison.Ordinal),
            new { recreated, saidInTheLastHour = saidOwn });

        // {adult} says whether Companion › Replies › Adult content is on: the explicit line only while it is.
        var adultCheck = CheckIns.Of(new CustomCheckIn { Id = "c7", Name = "FIXTURE", Task = "Describe it. {adult}" });
        var adultOff = CheckInPrompts.Message(adultCheck, facts, null) ?? "";
        var adultOn = CheckInPrompts.Message(adultCheck, facts with { Adult = true }, null) ?? "";
        Step("adult content line", adultOff.Contains(PromptCatalog.DefaultCheckInAdultOffInstructions, StringComparison.Ordinal) &&
            !adultOff.Contains(PromptCatalog.DefaultCheckInAdultOnInstructions, StringComparison.Ordinal) &&
            adultOn.Contains(PromptCatalog.DefaultCheckInAdultOnInstructions, StringComparison.Ordinal) && !adultOn.Contains("{adult}", StringComparison.Ordinal),
            new { off = CheckInPrompts.Adult(facts, null), on = CheckInPrompts.Adult(facts with { Adult = true }, null) });

        // 5. What Martlet does: emotes a reply turned on go off (never the owner's try), a reminder goes with exactly one request,
        //    and what to bring up is worded as the check-in's own, beside a due reminder.
        var held = new HeldEmotes();
        CharacterActionSource Source(string name) => new($"expression:{name}", CharacterActionKind.Expression, name, "FIXTURE");
        held.Add(Source("blush"), "fixture.model3.json", now.AddMinutes(-14), "{blush}");
        held.Add(Source("glasses"), "fixture.model3.json", now.AddMinutes(-9), "{glasses}");
        held.Add(Source("hearts"), "fixture.model3.json", now.AddMinutes(-20), "a try");
        foreach (var tag in verdicts[CheckIns.Emotes].Tags)
            foreach (var emote in held.Current.Where(h => h.ByReply && h.Source.Name == tag).ToArray())
                held.Remove(emote.Source.Id);
        Step("emotes off", held.Current.Select(h => h.Source.Name).SequenceEqual(["glasses", "hearts"]) && !held.Current.Single(h => h.Source.Name == "hearts").ByReply,
            held.Current.Select(h => $"{h.Source.Name} ({h.Why})"));

        var contextBoard = new ContextBoard();
        var note = CheckIns.Note(null, verdicts[CheckIns.Promises].Text!)!;
        contextBoard.Post(CheckIns.Source(CheckIns.Promises), note, now, CheckIns.NoteAge, consume: true);
        var first = contextBoard.Snapshot(now.AddMinutes(1));
        contextBoard.MarkSent(first);
        var second = contextBoard.Snapshot(now.AddMinutes(2));
        Step("reminder goes with one request", first.Sources.Contains("check-in-promises") && first.Text!.Contains("A reminder from your own check-in", StringComparison.Ordinal) &&
            !second.Sources.Contains("check-in-promises"), new { firstNotes = first.Text, secondSources = second.Sources });

        using var jobs = new BackgroundJobs();
        var sayJob = jobs.Start(CheckIns.SayKind, own.Name, (_, _) => Task.FromResult(BackgroundJobOutcome.Done(verdicts["c1"].Text!))).Job!;
        var reminderJob = jobs.Start(Reminders.Kind, "check the oven", (_, _) => Task.FromResult(BackgroundJobOutcome.Done("check the oven (FIXTURE)"))).Job!;
        for (var i = 0; i < 300 && !(sayJob.Finished && reminderJob.Finished); i++) await Task.Delay(10, cancellation);
        var notes = BackgroundJobs.ReportNotes(null, jobs.Undelivered);
        var delivery = jobs.Take(onItsOwn: true, noticesOnly: true);
        var message = delivery is null ? null : BackgroundJobs.ReportMessage(null, delivery.Jobs).UserText;
        delivery?.Complete();
        Step("brought up in its own words", message is not null && message.Contains("your own check-in found something to bring up", StringComparison.Ordinal) &&
            message.Contains("stretch break", StringComparison.Ordinal) && message.Contains("a reminder they asked for is due now", StringComparison.Ordinal) &&
            notes is not null && notes.Contains("Your own check-in found something to bring up", StringComparison.Ordinal) &&
            sayJob.Delivery == BackgroundDeliveryState.Delivered, new { message, notes });

        await OwnInputsAsync(Step, now, facts, cancellation);
        TouchesFact(Step, now, facts);
        Triggers(Step, now, facts);
        AfterExchange(Step, now, facts);
        await ContextAsync(Step, now, facts, cancellation);
        await DescribeTouchesAsync(Step, now, facts, cancellation);
        await ThinkAheadAsync(Step, now, facts, cancellation);
        await CallbacksAsync(Step, now, facts, cancellation);
        Signals(Step, now, facts);
        await AsideAsync(Step, now, facts, cancellation);
        await ToolsAsync(Step, facts, cancellation);
        await ReactionsAsync(Step, facts, cancellation);
        return new { passed = ok, steps };
    }

    private static string Marker(string id) => $"[FIXTURE check-in {id}]";
}
