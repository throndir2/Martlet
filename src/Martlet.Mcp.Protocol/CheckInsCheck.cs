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
internal static class CheckInsCheck
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
        triggers = CheckIns.AllTriggers.ToString(), triggerAgeSeconds = CheckIns.TriggerAge.TotalSeconds,
        touchesSettleMs = TouchDebounce.Quiet.TotalMilliseconds, strokeZones = CheckIns.StrokeZones,
        oftenTouches = TouchLedger.OftenTouches, oftenWindowMinutes = TouchLedger.OftenWindow.TotalMinutes,
        signalWindowMinutes = CheckIns.SignalWindow.TotalMinutes, unansweredAfterMinutes = CheckIns.UnansweredAfter.TotalMinutes,
        unansweredWithinMinutes = CheckIns.UnansweredWithin.TotalMinutes, peopleWindowMinutes = CheckIns.PeopleWindow.TotalMinutes,
        mostPerHourChoices = CheckIns.MostPerHourChoices,
        maximumToolRounds = CheckIns.MaximumToolRounds, maximumToolCalls = CheckIns.MaximumToolCalls,
        maximumToolResultCharacters = CheckIns.MaximumToolResultCharacters,
        toolSets = CheckInToolSets.All.Select(s => new { id = s.Id, name = s.Name, does = s.Does, tools = s.Tools.Select(t => t.Name).ToArray() }).ToArray()
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
        var waits = new Dictionary<string, string?>
        {
            ["emotes: due"] = CheckIns.Wait(Built(CheckIns.Emotes), facts, null),
            ["emotes: only young ones"] = CheckIns.Wait(Built(CheckIns.Emotes), facts with { Emotes = [facts.Emotes[2]] }, null),
            ["emotes: character hidden"] = CheckIns.Wait(Built(CheckIns.Emotes), facts with { CharacterShows = false }, null),
            ["emotes: ran 2 min ago"] = CheckIns.Wait(Built(CheckIns.Emotes), facts, new(now.AddMinutes(-2), 4, "every emote still fits", false)),
            ["emotes: kept 6 min ago, nothing new"] = CheckIns.Wait(Built(CheckIns.Emotes), facts,
                new(now.AddMinutes(-6), 4, "every emote still fits", false) { Kept = true }),
            ["emotes: you talk"] = CheckIns.Wait(Built(CheckIns.Emotes), facts with { Quiet = TimeSpan.Zero }, null),
            ["emotes: nobody here"] = CheckIns.Wait(Built(CheckIns.Emotes), facts with { Away = TimeSpan.FromMinutes(25) }, null),
            ["gaze: due"] = CheckIns.Wait(Built(CheckIns.Gaze), facts, null),
            ["gaze: chosen 1 min ago"] = CheckIns.Wait(Built(CheckIns.Gaze), facts with { Gaze = facts.Gaze! with { Since = TimeSpan.FromMinutes(1) } }, null),
            ["promises: new exchanges"] = CheckIns.Wait(Built(CheckIns.Promises), facts, new(now.AddMinutes(-6), 3, "nothing to remind Martlet of", false)),
            ["promises: nothing new"] = CheckIns.Wait(Built(CheckIns.Promises), facts, new(now.AddMinutes(-6), 4, "nothing to remind Martlet of", false)),
            ["character: 4 new replies"] = CheckIns.Wait(Built(CheckIns.Character), facts, null),
            ["character: no personality"] = CheckIns.Wait(Built(CheckIns.Character), facts with { Persona = null }, null),
            ["repeats: said enough"] = CheckIns.Wait(Built(CheckIns.Repeats), facts, new(now.AddMinutes(-11), 3, "nothing to remind Martlet of", false)),
            ["repeats: said too little"] = CheckIns.Wait(Built(CheckIns.Repeats), facts with { Said = [.. facts.Said.Take(2)] }, null),
            ["repeats: nothing new"] = CheckIns.Wait(Built(CheckIns.Repeats), facts, new(now.AddMinutes(-11), 4, "nothing to remind Martlet of", false)),
            ["own: off, but Check now"] = CheckIns.Wait(CheckIns.Of(new() { Id = "c2", Name = "FIXTURE", Task = "Check something." }), facts,
                new(now.AddMinutes(-1), 4, "nothing to remind Martlet of", false), now: true)
        };
        Step("when check-ins wait", waits["emotes: due"] is null && waits["emotes: only young ones"]?.Contains("3 min") == true &&
            waits["emotes: character hidden"] == "the character isn't showing" && waits["emotes: ran 2 min ago"] == "next in 3 min" &&
            waits["emotes: kept 6 min ago, nothing new"] == "next in 9 min" &&
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
            [CheckIns.Call] = "REMIND: They just joined a call; stay quiet and keep any reply very short until it ends.",
            [CheckIns.Others] = "REMIND: Someone else is here; don't share private things about the user.",
            [CheckIns.DescribeTouches] = "KNOW: FIXTURE: Their slow strokes keep sliding from your tail down to your groin."
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
            var focused = CheckIns.Focus(checkIn, facts);
            var job = CheckIns.Prepare(checkIn, focused, null);
            if (job is null)
            {
                Step("message: " + checkIn.Id, false, "no job");
                continue;
            }
            // The fixture member finds which check-in asks by a marker the check adds to the message only here.
            var marked = job with { Text = job.Text + "\n" + Marker(checkIn.Id) };
            var result = await board.RunAsync(marked, cancellation);
            verdicts[checkIn.Id] = CheckIns.Read(checkIn, result.Text, focused);
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
                a.Text.Contains("- 10:15 PM (2 min ago):", StringComparison.Ordinal) && a.Text.Contains("keeps saying the same things", StringComparison.Ordinal)) &&
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
            ["emotes: KEEP"] = CheckIns.Read(Built(CheckIns.Emotes), "KEEP", CheckIns.Focus(Built(CheckIns.Emotes), facts)),
            ["emotes: a tag that isn't asked about"] = CheckIns.Read(Built(CheckIns.Emotes), "OFF {smile}", CheckIns.Focus(Built(CheckIns.Emotes), facts)),
            ["emotes: chatter"] = CheckIns.Read(Built(CheckIns.Emotes), "Sure! Here is what I think about the emotes.", CheckIns.Focus(Built(CheckIns.Emotes), facts)),
            ["promises: REMIND: nothing"] = CheckIns.Read(Built(CheckIns.Promises), "REMIND: nothing.", facts),
            ["gaze: KEEP after thinking"] = CheckIns.Read(Built(CheckIns.Gaze), "Usually it follows the mouse.\nKEEP", facts)
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
                Task = CheckIns.Template(builtIn, null) ?? "", Facts = builtIn.Facts, Conditions = builtIn.Conditions, Outcome = builtIn.Outcome,
                Triggers = builtIn.Triggers
            });
            var sameMessage = CheckIns.Message(builtIn, CheckIns.Focus(builtIn, facts), null) == CheckIns.Message(copy, CheckIns.Focus(copy, facts), null);
            var sameWaits = states.All(s => CheckIns.Wait(builtIn, s, null) == CheckIns.Wait(copy, s, null) &&
                CheckIns.Wait(builtIn, s, lastRun) == CheckIns.Wait(copy, s, lastRun));
            var builtInVerdict = CheckIns.Read(builtIn, answers[builtIn.Id], CheckIns.Focus(builtIn, facts));
            var copyVerdict = CheckIns.Read(copy, answers[builtIn.Id], CheckIns.Focus(copy, facts));
            var sameRead = builtInVerdict.Act == copyVerdict.Act && builtInVerdict.Tags.SequenceEqual(copyVerdict.Tags) && builtInVerdict.Text == copyVerdict.Text;
            same &= sameMessage && sameWaits && sameRead;
            recreated[builtIn.Id] = new { message = sameMessage, waits = sameWaits, read = sameRead };
        }
        var saidOwn = CheckIns.Message(CheckIns.Of(new CustomCheckIn
        {
            Id = "c8", Name = "FIXTURE", Task = "Check whether Martlet repeats itself.", Facts = CheckInFacts.Said | CheckInFacts.Replies
        }), facts, null) ?? "";
        Step("built-in ones recreated as your own", same && saidOwn.Contains("said in the last hour, oldest first", StringComparison.Ordinal) &&
            saidOwn.Contains("- 10:05 PM (12 min ago):", StringComparison.Ordinal) && saidOwn.Contains("last replies, oldest first", StringComparison.Ordinal) &&
            saidOwn.Contains("REMIND:", StringComparison.Ordinal),
            new { recreated, saidInTheLastHour = saidOwn });

        // {adult} says whether Companion › Replies › Adult content is on: the explicit line only while it is.
        var adultCheck = CheckIns.Of(new CustomCheckIn { Id = "c7", Name = "FIXTURE", Task = "Describe it. {adult}" });
        var adultOff = CheckIns.Message(adultCheck, facts, null) ?? "";
        var adultOn = CheckIns.Message(adultCheck, facts with { Adult = true }, null) ?? "";
        Step("adult content line", adultOff.Contains(PromptCatalog.DefaultCheckInAdultOffInstructions, StringComparison.Ordinal) &&
            !adultOff.Contains(PromptCatalog.DefaultCheckInAdultOnInstructions, StringComparison.Ordinal) &&
            adultOn.Contains(PromptCatalog.DefaultCheckInAdultOnInstructions, StringComparison.Ordinal) && !adultOn.Contains("{adult}", StringComparison.Ordinal),
            new { off = CheckIns.Adult(facts, null), on = CheckIns.Adult(facts with { Adult = true }, null) });

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
        Step("brought up in its own words", message is not null && message.Contains("your own check-in came up with something to bring up", StringComparison.Ordinal) &&
            message.Contains("stretch break", StringComparison.Ordinal) && message.Contains("a reminder they asked you for is due now", StringComparison.Ordinal) &&
            notes is not null && notes.Contains("Your own check-in came up with something to bring up", StringComparison.Ordinal) &&
            sayJob.Delivery == BackgroundDeliveryState.Delivered, new { message, notes });

        await OwnInputsAsync(Step, now, facts, cancellation);
        TouchesFact(Step, now, facts);
        Triggers(Step, now, facts);
        await ContextAsync(Step, now, facts, cancellation);
        await DescribeTouchesAsync(Step, now, facts, cancellation);
        Signals(Step, now, facts);
        await ToolsAsync(Step, facts, cancellation);
        return new { passed = ok, steps };
    }

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

    /// <summary>10. Check-in triggers with FIXTURE touches (NOT AI): saved and read back, what a burst of touches fires once it
    /// settles (never taking a touch from the production TouchLedger), and when a check-in that starts on a trigger waits or runs.</summary>
    private static void Triggers(Action<string, bool, object?> step, DateTimeOffset now, CheckInState facts)
    {
        var folder = Path.Combine(Path.GetTempPath(), "Martlet.CheckIns." + Guid.NewGuid().ToString("N"));
        var custom = new CustomCheckIn
        {
            Id = "c4", Name = "FIXTURE touches", On = true, EveryMinutes = 5, Task = "Describe the user's touches.",
            Facts = CheckInFacts.Conversation, Outcome = CheckInOutcome.Note, Triggers = CheckInTriggers.IntimateTouch | CheckInTriggers.StrokeAcrossZones
        };
        try
        {
            var saved = new CheckInSettings().With(custom).With(CheckIns.Gaze, new CheckInChoice(true, 5) { Triggers = CheckInTriggers.TouchesEnded });
            var wrote = saved.Save(folder);
            var (read, state) = CheckInSettings.Read(folder);
            var all = CheckIns.All(read);
            var refused = false;
            try { new CheckInSettings().With(custom with { Triggers = (CheckInTriggers)16 }).Validate(); }
            catch (Martlet.Core.Contracts.ContractException) { refused = true; }
            step("triggers: saved and read back", wrote && state == "loaded" && all.Single(c => c.Id == "c4").Triggers == custom.Triggers &&
                all.Single(c => c.Id == CheckIns.Gaze).Triggers == CheckInTriggers.TouchesEnded &&
                CheckIns.All(null).All(c => c.Triggers == (c.Id == CheckIns.DescribeTouches ? CheckInTriggers.TouchesEnded : CheckInTriggers.None)) && refused,
                new { state, c4 = all.Single(c => c.Id == "c4").Triggers.ToString(), gaze = all.Single(c => c.Id == CheckIns.Gaze).Triggers.ToString(), unknownRefused = refused });
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); }
            catch (IOException) { }
        }

        // FIXTURE touches through the production ledger: the groin was touched 5 times before (a reply took them), then a poke on
        // the chest and a stroke from the tail over the buttocks to the groin.
        var ledger = new TouchLedger();
        var watch = new TouchTriggers();
        var at = TimeSpan.FromMinutes(30);
        for (var i = 0; i < 5; i++)
            ledger.Record(new(PhysicalKind.Tap, at + TimeSpan.FromSeconds(i * 20), "FIXTURE your groin", "groin", Zones: ["FIXTURE your groin"], Intimate: true));
        ledger.Drain(at + TimeSpan.FromSeconds(100));
        PhysicalEvent[] burst =
        [
            new(PhysicalKind.Tap, at + TimeSpan.FromSeconds(110), "FIXTURE your chest", "chest", Zones: ["FIXTURE your chest"]),
            new(PhysicalKind.Moved, at + TimeSpan.FromSeconds(111), Detail: "to another monitor"),
            new(PhysicalKind.Stroke, at + TimeSpan.FromSeconds(112), Zones: ["FIXTURE your tail", "FIXTURE your buttocks", "FIXTURE your groin"],
                Label: "tail, buttocks and groin", Detail: "slowly", Intimate: true)
        ];
        foreach (var physical in burst)
        {
            ledger.Record(physical);
            watch.Touched(physical, ledger.Peek(physical.At)?.Often);
        }
        var waiting = watch.Waiting;
        var fired = watch.Settle(now);
        var kept = ledger.Peek(at + TimeSpan.FromSeconds(113));
        watch.Touched(new(PhysicalKind.Pat, at + TimeSpan.FromSeconds(200), "FIXTURE the top of your head", "top of head", Zones: ["FIXTURE the top of your head"]));
        var plain = watch.Settle(now);
        watch.Touched(new(PhysicalKind.Zoomed, at + TimeSpan.FromSeconds(210), Detail: "in"));
        var none = watch.Settle(now);
        step("triggers: fixture touches fire them", waiting && fired is { } all4 &&
            all4.Fired == (CheckInTriggers.TouchesEnded | CheckInTriggers.IntimateTouch | CheckInTriggers.StrokeAcrossZones | CheckInTriggers.KeepsComingBack) &&
            all4.What.StartsWith("2 touches, an intimate one, a stroke across 3 zones, one place touched 6 times", StringComparison.Ordinal) &&
            !all4.What.Contains("FIXTURE", StringComparison.Ordinal) && kept is { Touches: 3 } && plain?.Fired == CheckInTriggers.TouchesEnded &&
            none is null && !watch.Waiting,
            new
            {
                fired = fired?.Fired.ToString(), what = fired?.What, ledgerStillHolds = kept?.HistoryLine, plainPat = plain?.Fired.ToString(),
                zoomOnly = none?.Fired.ToString() ?? "nothing"
            });

        // When a check-in that starts on a trigger waits or runs: only on one of its own triggers, not older than TriggerAge, at
        // most once per its pace, and never held by the conversation being busy (the touch reply); its other waits still apply.
        var checkIn = CheckIns.Of(custom);
        var paced = CheckIns.Of(custom with { Triggers = CheckInTriggers.None });
        var busy = facts with { Quiet = TimeSpan.Zero };
        var trigger = fired! with { At = now.AddSeconds(-2) };
        var waits = new Dictionary<string, string?>
        {
            ["no trigger"] = CheckIns.Wait(checkIn, facts, null),
            ["only touches ended"] = CheckIns.Wait(checkIn, facts, null, fired: plain! with { At = now }),
            ["fired, while the conversation is busy"] = CheckIns.Wait(checkIn, busy, null, fired: trigger),
            ["fired, ran 2 min ago"] = CheckIns.Wait(checkIn, facts, new(now.AddMinutes(-2), 4, "nothing to remind Martlet of", false), fired: trigger),
            ["fired 3 min ago"] = CheckIns.Wait(checkIn, facts, null, fired: trigger with { At = now.AddMinutes(-3) }),
            ["fired, nobody here"] = CheckIns.Wait(checkIn, facts with { Away = TimeSpan.FromMinutes(25) }, null, fired: trigger),
            ["fired, but off"] = CheckIns.Wait(checkIn with { On = false }, facts, null, fired: trigger),
            ["Check now, no trigger"] = CheckIns.Wait(checkIn, busy, null, now: true),
            ["paced, while the conversation is busy"] = CheckIns.Wait(paced, busy, null, fired: trigger)
        };
        var job = CheckIns.Prepare(checkIn, facts, null);
        step("triggers: when a triggered check-in waits", waits["no trigger"] == "it waits for an intimate touch or a stroke across 3 zones" &&
            waits["only touches ended"] == waits["no trigger"] && waits["fired, while the conversation is busy"] is null &&
            waits["fired, ran 2 min ago"] == "next in 3 min" && waits["fired 3 min ago"] == waits["no trigger"] &&
            waits["fired, nobody here"]?.StartsWith("nobody used this PC", StringComparison.Ordinal) == true && waits["fired, but off"] == "it's off" &&
            waits["Check now, no trigger"] is null && waits["paced, while the conversation is busy"] == "the conversation is busy" &&
            job?.Kind == ThinkingJobKind.CheckIn && LiveFloorRules.Stops(ThinkingJobKind.CheckIn),
            new { waits = waits.ToDictionary(w => w.Key, w => w.Value ?? "runs"), jobKind = job is null ? null : ThinkingJobKinds.Name(job.Kind) });
    }

    // The built-in check-ins that wait for a signal: off until the owner turns them on.
    private static bool Signaled(string id) => id is CheckIns.Welcome or CheckIns.Unanswered or CheckIns.Call or CheckIns.Others;

    /// <summary>9. Signals (FIXTURE facts, canned answers, NOT AI): each signal condition waits and runs on the production
    /// <see cref="CheckIns.Wait"/>, the new facts are worded as the check reads them, the four signal check-ins ask and read their
    /// answers, the hours and the per-hour cap save and read back (a bad one refused), and a script whose output didn't change is
    /// told apart by its hash.</summary>
    private static void Signals(Action<string, bool, object?> step, DateTimeOffset now, CheckInState facts)
    {
        CheckIn Built(string id) => CheckIns.All(null).Single(c => c.Id == id) with { On = true };
        CheckIn Own(CheckInConditions when, string task = "FIXTURE", string script = "") =>
            CheckIns.Of(new CustomCheckIn { Id = "c6", Name = "FIXTURE signal", On = true, EveryMinutes = 1, Task = task, Conditions = when, Script = script });
        var ran = new CheckInRun(now.AddMinutes(-3), 4, "nothing to bring up", false) { Recent = [now.AddMinutes(-40), now.AddMinutes(-3)] };
        var question = facts with
        {
            Said = [.. facts.Said, new(now.AddMinutes(-8), "FIXTURE: {smile} So, which boss is next?")], Quiet = TimeSpan.FromMinutes(8)
        };
        var state = facts with
        {
            CameBackAt = now.AddMinutes(-1), WasAway = TimeSpan.FromMinutes(25), Activity = "in a call in Discord", OnCall = true,
            ActivityChangedAt = now.AddMinutes(-1), ActivityBefore = "playing a game (FIXTURE Quest), full screen",
            AttentionAt = now.AddMinutes(-1), SongEndedAt = now.AddMinutes(-1),
            Voices = [new("FIXTURE Owner", true, now.AddMinutes(-2)) { Id = "v1" }, new(null, false, now.AddMinutes(-1)) { Id = "v2" }],
            OwnerVoiceKnown = true, WhileAway = ["Thinking longer finished: FIXTURE trip plan"]
        };
        var waits = new Dictionary<string, string?>
        {
            ["came back"] = CheckIns.Wait(Built(CheckIns.Welcome), state, null),
            ["came back: before the last run"] = CheckIns.Wait(Built(CheckIns.Welcome), state with { CameBackAt = now.AddMinutes(-40) }, ran with { At = now.AddMinutes(-35) }),
            ["on a call"] = CheckIns.Wait(Own(CheckInConditions.NotOnCall), state, null),
            ["not on a call"] = CheckIns.Wait(Own(CheckInConditions.NotOnCall), state with { OnCall = false }, null),
            ["activity changed"] = CheckIns.Wait(Built(CheckIns.Call), state, null),
            ["activity same since the last run"] = CheckIns.Wait(Built(CheckIns.Call), state with { ActivityChangedAt = now.AddMinutes(-4) }, ran with { Recent = [] }),
            ["between 8 AM and 10 PM at 10:17 PM"] = CheckIns.Wait(Own(CheckInConditions.Between), state, null),
            ["between 10 PM and 6 AM at 10:17 PM"] = CheckIns.Wait(Own(CheckInConditions.Between) with { FromHour = 22, UntilHour = 6 }, state, null),
            ["unanswered"] = CheckIns.Wait(Built(CheckIns.Unanswered), question, null),
            ["unanswered: no question"] = CheckIns.Wait(Built(CheckIns.Unanswered), facts, null),
            ["unanswered: followed up"] = CheckIns.Wait(Built(CheckIns.Unanswered), question, ran with { At = now.AddMinutes(-6) }),
            ["attention"] = CheckIns.Wait(Own(CheckInConditions.Attention), state, null),
            ["no attention"] = CheckIns.Wait(Own(CheckInConditions.Attention), facts, null),
            ["song ended"] = CheckIns.Wait(Own(CheckInConditions.SongEnded), state, null),
            ["someone else"] = CheckIns.Wait(Built(CheckIns.Others), state, null),
            ["only the owner"] = CheckIns.Wait(Built(CheckIns.Others), state with { Voices = [state.Voices[0]] }, null),
            ["script changed: no script"] = CheckIns.Wait(Own(CheckInConditions.ScriptChanged), state, null),
            ["at most 2 an hour"] = CheckIns.Wait(Own(CheckInConditions.None) with { MostPerHour = 2 }, state, ran)
        };
        var hash = CheckIns.ScriptHash("FIXTURE 42");
        var scripted = Own(CheckInConditions.ScriptChanged, script: "Write-Output 42");
        var unchanged = CheckIns.ScriptUnchanged(scripted, hash, ran with { ScriptHash = hash }, now: false);
        var changed = CheckIns.ScriptUnchanged(scripted, CheckIns.ScriptHash("FIXTURE 43"), ran with { ScriptHash = hash }, now: false);
        var checkNow = CheckIns.ScriptUnchanged(scripted, hash, ran with { ScriptHash = hash }, now: true);
        step("signals: when they wait", waits["came back"] is null && waits["came back: before the last run"]?.StartsWith("it waits for you to come back", StringComparison.Ordinal) == true &&
            waits["on a call"] == "you're on a call" && waits["not on a call"] is null && waits["activity changed"] is null &&
            waits["activity same since the last run"] == "what you do hasn't changed since the last check" &&
            waits["between 8 AM and 10 PM at 10:17 PM"] == "it runs only between 8 AM and 10 PM" && waits["between 10 PM and 6 AM at 10:17 PM"] is null &&
            waits["unanswered"] is null && waits["unanswered: no question"] == "Martlet's last remark didn't ask anything" &&
            waits["unanswered: followed up"] == "it already followed up on Martlet's last question" &&
            waits["attention"] is null && waits["no attention"]?.StartsWith("no taskbar button", StringComparison.Ordinal) == true &&
            waits["song ended"] is null && waits["someone else"] is null && waits["only the owner"] == "Martlet heard nobody else lately" &&
            waits["script changed: no script"] == "it has no script whose output could change" &&
            waits["at most 2 an hour"]?.StartsWith("it ran 2 times in the last hour", StringComparison.Ordinal) == true &&
            unchanged && !changed && !checkNow,
            new { waits = waits.ToDictionary(w => w.Key, w => w.Value ?? "runs"), scriptUnchanged = unchanged, scriptChanged = !changed, checkNowAsksAnyway = !checkNow });

        var messages = new Dictionary<string, string>
        {
            [CheckIns.Welcome] = CheckIns.Message(Built(CheckIns.Welcome), state, null) ?? "",
            [CheckIns.Unanswered] = CheckIns.Message(Built(CheckIns.Unanswered), question with { Activity = "" }, null) ?? "",
            [CheckIns.Call] = CheckIns.Message(Built(CheckIns.Call), state, null) ?? "",
            [CheckIns.Others] = CheckIns.Message(Built(CheckIns.Others), state, null) ?? ""
        };
        var verdicts = new[] { CheckIns.Welcome, CheckIns.Unanswered, CheckIns.Call, CheckIns.Others }
            .ToDictionary(id => id, id => CheckIns.Read(Built(id), id switch
            {
                CheckIns.Welcome => "SAY: Welcome back!", CheckIns.Unanswered => "OK", CheckIns.Call => "REMIND: Stay quiet during the call.",
                _ => "REMIND: Someone else is here; keep private things private."
            }, state));
        step("signals: the facts and the check-ins", messages[CheckIns.Welcome].Contains("after 25 min away", StringComparison.Ordinal) &&
            messages[CheckIns.Welcome].Contains("- Thinking longer finished: FIXTURE trip plan", StringComparison.Ordinal) &&
            messages[CheckIns.Welcome].Contains("starts with SAY:", StringComparison.Ordinal) &&
            messages[CheckIns.Unanswered].Contains("So, which boss is next?", StringComparison.Ordinal) &&
            messages[CheckIns.Unanswered].Contains("doesn't seem to be doing anything with sound", StringComparison.Ordinal) &&
            messages[CheckIns.Call].Contains("in a call in Discord", StringComparison.Ordinal) &&
            messages[CheckIns.Call].Contains("before, they were playing a game (FIXTURE Quest)", StringComparison.Ordinal) &&
            messages[CheckIns.Call].Contains("starts with REMIND:", StringComparison.Ordinal) &&
            messages[CheckIns.Others].Contains("- FIXTURE Owner (the user's own voice), 2 min ago", StringComparison.Ordinal) &&
            messages[CheckIns.Others].Contains("- a voice Martlet doesn't know, 1 min ago", StringComparison.Ordinal) &&
            messages[CheckIns.Others].Contains("Someone other than the user seems to be here.", StringComparison.Ordinal) &&
            verdicts[CheckIns.Welcome] is { Act: true } && verdicts[CheckIns.Unanswered] is { Act: false, Readable: true } &&
            verdicts[CheckIns.Call] is { Act: true } && verdicts[CheckIns.Others] is { Act: true } &&
            CheckIns.Placed("{activity} {people} {away}") == (CheckInFacts.Activity | CheckInFacts.People | CheckInFacts.WhileAway),
            new { messages, verdicts = verdicts.ToDictionary(v => v.Key, v => new { v.Value.Act, v.Value.Readable }) });

        var folder = Path.Combine(Path.GetTempPath(), "Martlet.CheckIns." + Guid.NewGuid().ToString("N"));
        try
        {
            var saved = new CheckInSettings()
                .With(CheckIns.Welcome, new CheckInChoice(true, 30) { Conditions = CheckInConditions.CameBack | CheckInConditions.Between, FromHour = 7, UntilHour = 23, MostPerHour = 2 })
                .With(new CustomCheckIn { Id = "c6", Name = "FIXTURE night", Conditions = CheckInConditions.Between, FromHour = 22, UntilHour = 6, MostPerHour = 1 });
            var wrote = saved.Save(folder);
            var (read, loaded) = CheckInSettings.Read(folder);
            var all = CheckIns.All(read);
            bool Refused(CheckInSettings bad)
            {
                try { bad.Validate(); return false; }
                catch (Martlet.Core.Contracts.ContractException) { return true; }
            }
            var refused = Refused(new CheckInSettings().With(new CustomCheckIn { Id = "c6", Name = "FIXTURE", FromHour = 9, UntilHour = 9 })) &&
                Refused(new CheckInSettings().With(new CustomCheckIn { Id = "c6", Name = "FIXTURE", MostPerHour = 5 })) &&
                Refused(new CheckInSettings().With(new CustomCheckIn { Id = "c6", Name = "FIXTURE", Conditions = (CheckInConditions)(1 << 20) }));
            step("signals: hours and the per-hour cap saved and read back", wrote && loaded == "loaded" &&
                all.Single(c => c.Id == CheckIns.Welcome) is { On: true, FromHour: 7, UntilHour: 23, MostPerHour: 2 } &&
                all.Single(c => c.Id == "c6") is { FromHour: 22, UntilHour: 6, MostPerHour: 1 } && refused,
                new { state = loaded, badHoursCapOrConditionRefused = refused });
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); }
            catch (IOException) { }
        }
    }

    /// <summary>11. Tool sets: a check-in calls its sets' tools in a bounded loop, only on a member whose model can call tools
    /// (FIXTURE handlers and calls; NOT AI).</summary>
    private static async Task ToolsAsync(Action<string, bool, object?> step, CheckInState facts, CancellationToken cancellation)
    {
        // Saved and read back with its tool sets; a set Martlet doesn't offer and the same set twice are refused.
        var folder = Path.Combine(Path.GetTempPath(), "Martlet.CheckIns." + Guid.NewGuid().ToString("N"));
        var custom = new CustomCheckIn
        {
            Id = "c4", Name = "FIXTURE tools", On = true, EveryMinutes = 30, Task = "If the user seems upset, remind Martlet to be gentle. " + Marker("c4"),
            Facts = CheckInFacts.Conversation, Outcome = CheckInOutcome.Tools, ToolSets = [CheckInToolSets.NextReplyId, CheckInToolSets.CharacterId]
        };
        try
        {
            var wrote = new CheckInSettings().With(custom).With(CheckIns.Promises, new CheckInChoice(true, 30) { ToolSets = [CheckInToolSets.RemindersId] })
                .Save(folder);
            var (read, state) = CheckInSettings.Read(folder);
            var back = read.Custom.SingleOrDefault();
            var promises = CheckIns.All(read).Single(c => c.Id == CheckIns.Promises);
            bool Refused(IReadOnlyList<string> sets)
            {
                try { new CheckInSettings().With(custom with { ToolSets = sets }).Validate(); }
                catch (Martlet.Core.Contracts.ContractException) { return true; }
                return false;
            }
            var refusedUnknown = Refused(["delete-everything"]);
            var refusedTwice = Refused([CheckInToolSets.NextReplyId, CheckInToolSets.NextReplyId]);
            step("tools: sets saved and read back", wrote && state == "loaded" && back == custom &&
                back.ToolSets.SequenceEqual([CheckInToolSets.NextReplyId, CheckInToolSets.CharacterId]) &&
                promises.ToolSets.SequenceEqual([CheckInToolSets.RemindersId]) && promises.Needs.HasFlag(ThinkingCapability.Tools) &&
                refusedUnknown && refusedTwice,
                new
                {
                    state, sets = back?.ToolSets, promisesSets = promises.ToolSets, promisesNeeds = promises.Needs.ToString(), refusedUnknown, refusedTwice,
                    offered = CheckInToolSets.All.Select(s => new { s.Id, s.Name, tools = s.Tools.Select(t => t.Name) })
                });
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); }
            catch (IOException) { }
        }

        // The job offers the tools of its sets that have a handler on this PC, for a bounded number of rounds.
        var checkIn = CheckIns.Of(custom);
        var reminded = new List<string>();
        var handlers = new Dictionary<string, CheckInToolHandler>
        {
            [CheckInToolSets.NextReplyId] = (call, _, _) =>
            {
                if (call.Name != CheckInToolSets.RemindNextReply) return ValueTask.FromResult(new ConversationToolResult("FIXTURE: only reminders here.", true));
                lock (reminded) reminded.Add(CheckInToolSets.Argument(call, "text") ?? "");
                return ValueTask.FromResult(new ConversationToolResult("Reminded the next reply.\nFIXTURE private detail on line two"));
            },
            [CheckInToolSets.CharacterId] = (_, _, _) => throw new InvalidOperationException("FIXTURE handler failure")
        };
        var host = new CheckInToolHost(checkIn.ToolSets, new(checkIn.Id, checkIn.Name, null, facts.Now), handlers);
        var job = CheckIns.Prepare(checkIn, CheckIns.Focus(checkIn, facts), null, host);
        var message = job?.Text ?? "";
        step("tools: the job offers them", checkIn.Needs.HasFlag(ThinkingCapability.Tools) && CheckIns.Describe(checkIn.Needs).Contains("tool calls", StringComparison.Ordinal) &&
            job is { ToolHost: not null, MaxToolRounds: CheckIns.MaximumToolRounds } && job.Tools.Select(t => t.Name).SequenceEqual(
                [CheckInToolSets.RemindNextReply, CheckInToolSets.BringUp, CheckInToolSets.TurnOffEmote, CheckInToolSets.LookUsual]) &&
            job.Required.HasFlag(ThinkingCapability.Tools) && message.Contains("Use your tools for what needs doing", StringComparison.Ordinal) &&
            CheckIns.Wait(CheckIns.Of(custom with { ToolSets = [] }), facts, null) == "it has no tools to use",
            new { needs = checkIn.Needs.ToString(), described = CheckIns.Describe(checkIn.Needs), tools = job?.Tools.Select(t => t.Name), job?.MaxToolRounds });

        // Only a member whose model can call tools takes it; the run's calls are kept, an unknown tool and a failing handler are
        // errors the model reads, and after the most calls a run may make every call is refused.
        var places = new BackgroundPlaces();
        BackgroundPlace textMember = new("endpoint:text", "FIXTURE text member") { Slots = 1, Model = "fixture-text" };
        BackgroundPlace toolMember = new("endpoint:tools", "FIXTURE member that calls tools")
        {
            Slots = 1, Model = "fixture-tools", Can = ThinkingCapability.Text | ThinkingCapability.Tools
        };
        var members = new List<BackgroundPlace> { textMember };
        var answers = new List<ConversationToolResult>();
        var board = new ThinkingJobBoard(places, () => [.. members], async (_, asked, token) =>
        {
            async Task Call(string name, string arguments) =>
                answers.Add(await asked.ToolHost!.CallAsync(new TextToolCall("call-" + answers.Count, name, arguments), token));
            await Call(CheckInToolSets.RemindNextReply, """{"text":"FIXTURE: be gentle, they seem upset."}""");
            await Call("delete_files", "{}");
            await Call(CheckInToolSets.LookUsual, "{}");
            for (var i = 0; i < CheckIns.MaximumToolCalls; i++) await Call(CheckInToolSets.RemindNextReply, """{"text":"FIXTURE again"}""");
            return ThinkingAnswer.Done("Reminded Martlet to be gentle because the user seems upset.");
        });
        var none = job is null ? null : await board.RunAsync(job, cancellation);
        var canNone = board.CanRun(ThinkingJobKind.CheckIn, checkIn.Needs);
        members.Add(toolMember);
        var done = job is null ? null : await board.RunAsync(job, cancellation);
        var uses = host.Uses;
        var verdict = CheckIns.Read(checkIn, done?.Text, facts);
        step("tools: only a member that calls tools runs them", none?.Outcome == ThinkingJobOutcome.NoMember && !canNone &&
            board.CanRun(ThinkingJobKind.CheckIn, checkIn.Needs) && done is { Succeeded: true } && done.Member == toolMember.Name &&
            uses.Count == 3 + CheckIns.MaximumToolCalls && uses[0] is { Set: CheckInToolSets.NextReplyId, Tool: CheckInToolSets.RemindNextReply, Failed: false } &&
            uses[0].Result == "Reminded the next reply." && uses[1] is { Set: "", Tool: "delete_files", Failed: true } &&
            uses[2] is { Set: CheckInToolSets.CharacterId, Failed: true, Result: "The tool failed." } &&
            uses.Skip(3).Take(CheckIns.MaximumToolCalls - 3).All(u => !u.Failed) &&
            uses.Skip(CheckIns.MaximumToolCalls).All(u => u.Failed && u.Result.StartsWith("This check-in already made", StringComparison.Ordinal)) &&
            reminded.Count == CheckIns.MaximumToolCalls - 2 && reminded[0] == "FIXTURE: be gentle, they seem upset." &&
            !uses.Any(u => u.Result.Contains("private", StringComparison.Ordinal)) && verdict == CheckInVerdict.Nothing,
            new
            {
                textOnlyPool = none?.Outcome.ToString(), withOneThatCallsTools = done?.Member, calls = uses.Count, made = CheckIns.ToolsText(uses),
                answer = done?.Text
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

    /// <summary>8. A check-in that adds to what Martlet knows (FIXTURE answers, NOT AI): its message asks for a KNOW: line, the answer
    /// is read on a production job board, and the description goes on a production context board as a consume note that goes with
    /// exactly one request, only while fresh. "KNOW: nothing to add" and OK change nothing.</summary>
    private static async Task ContextAsync(Action<string, bool, object?> step, DateTimeOffset now, CheckInState facts,
        CancellationToken cancellation)
    {
        var describes = CheckIns.Of(new CustomCheckIn
        {
            Id = "c7", Name = "FIXTURE describes the moment", On = true, EveryMinutes = 1,
            Task = "Describe briefly and vividly what has been happening between the user and {name}.", Facts = CheckInFacts.Conversation,
            Outcome = CheckInOutcome.Context
        });
        const string answer = "<think>The user beat a boss, then went back to a bug.</think>\n" +
            "- KNOW: FIXTURE: Still glowing from the boss win, the user is now hunched over a stubborn bug while Mira cheers them on.";
        var job = CheckIns.Prepare(describes, CheckIns.Focus(describes, facts), null);
        BackgroundPlace member = new("endpoint:fixture-context", "FIXTURE member") { Slots = 1, Model = "fixture-model" };
        var board = new ThinkingJobBoard(new BackgroundPlaces(), () => [member], (_, _, _) => Task.FromResult(ThinkingAnswer.Done(answer)));
        var result = job is null ? null : await board.RunAsync(job, cancellation);
        var verdict = CheckIns.Read(describes, result?.Text, facts);
        step("context: asked and read", job is { Kind: ThinkingJobKind.CheckIn } && job.Text.Contains("starts with KNOW:", StringComparison.Ordinal) &&
            job.Text.Contains("write only: OK", StringComparison.Ordinal) && !job.Text.Contains("REMIND:", StringComparison.Ordinal) &&
            result is { Succeeded: true } && verdict is { Act: true, Readable: true, Text: { } text } && text.StartsWith("FIXTURE: Still glowing", StringComparison.Ordinal),
            new { does = describes.Does, message = job?.Text, verdict.Act, verdict.Readable, verdict.Text });

        var contextBoard = new ContextBoard();
        var note = CheckIns.Context(null, verdict.Text ?? "")!;
        contextBoard.Post(CheckIns.Source(describes.Id), note, now, CheckIns.ContextAge, consume: true);
        var first = contextBoard.Snapshot(now.AddMinutes(1));
        contextBoard.MarkSent(first);
        var second = contextBoard.Snapshot(now.AddMinutes(1.5));
        contextBoard.Post(CheckIns.Source(describes.Id), note, now, CheckIns.ContextAge, consume: true);
        var stale = contextBoard.Snapshot(now + CheckIns.ContextAge + TimeSpan.FromSeconds(1));
        var emptied = CheckIns.Context(new Martlet.Core.Settings.PromptSettings
        {
            Overrides = new Dictionary<string, string> { [Martlet.Core.Settings.PromptCatalog.CheckInContext] = "" }
        }, "x");
        step("context: goes with one request", first.Sources.Contains("check-in-c7") &&
            first.Text!.Contains("What is happening now, from your own check-in", StringComparison.Ordinal) &&
            first.Text.Contains("not a reminder to follow", StringComparison.Ordinal) && first.Text.Contains("hunched over a stubborn bug", StringComparison.Ordinal) &&
            !second.Sources.Contains("check-in-c7") && !stale.Sources.Contains("check-in-c7") && emptied is null &&
            CheckIns.ContextAge < CheckIns.NoteAge,
            new
            {
                firstNotes = first.Text, secondSources = second.Sources, staleSources = stale.Sources,
                contextAgeMinutes = CheckIns.ContextAge.TotalMinutes, emptiedPromptPosts = emptied is not null
            });

        var nothing = new Dictionary<string, CheckInVerdict>
        {
            ["KNOW: nothing to add."] = CheckIns.Read(describes, "KNOW: nothing to add.", facts),
            ["OK"] = CheckIns.Read(describes, "OK", facts),
            ["REMIND: a reminder instead"] = CheckIns.Read(describes, "REMIND: Be gentle.", facts),
            ["chatter"] = CheckIns.Read(describes, "Things look calm to me.", facts)
        };
        var untouched = new ContextBoard();
        foreach (var read in nothing.Values.Where(v => v.Act))
            untouched.Post(CheckIns.Source(describes.Id), CheckIns.Context(null, read.Text!), now, CheckIns.ContextAge, consume: true);
        step("context: nothing changes nothing", nothing["KNOW: nothing to add."] is { Act: false, Readable: true } &&
            nothing["OK"] is { Act: false, Readable: true } && nothing["REMIND: a reminder instead"] is { Act: false } &&
            nothing["chatter"] is { Act: false, Readable: false } && untouched.Snapshot(now).Notes.Count == 0,
            nothing.ToDictionary(v => v.Key, v => new { v.Value.Act, v.Value.Readable }));
    }

    /// <summary>6. One of the owner's check-ins with a model requirement and inputs (FIXTURE picture, sound and script; NOT AI).</summary>
    private static async Task OwnInputsAsync(Action<string, bool, object?> step, DateTimeOffset now, CheckInState facts,
        CancellationToken cancellation)
    {
        // Saved and read back; a recording length that isn't offered and a script with a null character are refused.
        var folder = Path.Combine(Path.GetTempPath(), "Martlet.CheckIns." + Guid.NewGuid().ToString("N"));
        var custom = new CustomCheckIn
        {
            Id = "c3", Name = "FIXTURE inputs", On = true, EveryMinutes = 30, Task = "Check what the user does. " + Marker("c3"),
            Facts = CheckInFacts.None, Outcome = CheckInOutcome.Note, Needs = ThinkingCapability.Text | ThinkingCapability.Audio,
            Screenshot = true, Recording = CheckInRecording.Microphone, RecordingSeconds = 15, Script = "Write-Output ('FIXTURE ' + (6 * 7))"
        };
        try
        {
            var wrote = new CheckInSettings().With(custom).Save(folder);
            var (read, state) = CheckInSettings.Read(folder);
            var back = read.Custom.SingleOrDefault();
            bool Refused(CustomCheckIn bad)
            {
                try { new CheckInSettings().With(bad).Validate(); }
                catch (Martlet.Core.Contracts.ContractException) { return true; }
                return false;
            }
            var refusedLength = Refused(custom with { RecordingSeconds = 7 });
            var refusedScript = Refused(custom with { Script = "Get-Date\0" });
            var refusedNeeds = Refused(custom with { Needs = (ThinkingCapability)8 });
            step("own: inputs saved and read back", wrote && state == "loaded" && back is
                {
                    Screenshot: true, Recording: CheckInRecording.Microphone, RecordingSeconds: 15
                } && back.Needs == custom.Needs && back.Script == custom.Script && refusedLength && refusedScript && refusedNeeds,
                new
                {
                    state, needs = back?.Needs.ToString(), screenshot = back?.Screenshot, recording = back?.Recording.ToString(),
                    back?.RecordingSeconds, scriptCharacters = back?.Script.Length, refusedLength, refusedScript, refusedNeeds
                });
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); }
            catch (IOException) { }
        }

        // A screenshot needs a model that sees pictures and a recording one that hears recordings, whatever the owner chose.
        var checkIn = CheckIns.Of(custom);
        var textOnly = CheckIns.Of(custom with { Screenshot = false, Recording = CheckInRecording.None, Needs = ThinkingCapability.Text });
        step("own: the model it needs", checkIn.Needs == (ThinkingCapability.Text | ThinkingCapability.Vision | ThinkingCapability.Audio) &&
            textOnly.Needs == ThinkingCapability.Text && CheckIns.Describe(checkIn.Needs) == "text, pictures and recordings" &&
            CheckIns.Describe(ThinkingCapability.Text | ThinkingCapability.Vision) == "text and pictures",
            new { needs = checkIn.Needs.ToString(), described = CheckIns.Describe(checkIn.Needs), textOnly = textOnly.Needs.ToString() });

        // The microphone's last seconds: kept only while a check-in wants them, never older than a pause, and forgotten when none does.
        var microphone = new Martlet.Audio.PcSoundBuffer(TimeSpan.FromSeconds(30));
        var second = new byte[Martlet.Audio.PcSoundBuffer.SampleRate * 2];
        for (var i = 0; i < second.Length; i += 2) second[i] = (byte)(i % 251);
        microphone.Append(second);
        var keptUnwanted = microphone.Buffered;
        microphone.Wanted = true;
        // Three seconds at once: the buffer dates them back from now, the way a capture's steady reads add up.
        microphone.Append([.. second, .. second, .. second]);
        var heard = microphone.Hears(TimeSpan.FromSeconds(2));
        var recent = microphone.Recent(TimeSpan.FromSeconds(custom.RecordingSeconds), TimeSpan.FromSeconds(2));
        var recentSeconds = recent.Length / (double)second.Length;
        microphone.Wanted = false;
        var forgot = microphone.Buffered == TimeSpan.Zero && !microphone.Hears(TimeSpan.FromSeconds(2));
        step("own: the microphone's last seconds", keptUnwanted == TimeSpan.Zero && heard && recentSeconds is > 2.9 and <= 3 && forgot,
            new { keptWhileNotWanted = keptUnwanted.TotalSeconds, heard, recentSeconds = Math.Round(recentSeconds, 2), forgotWhenNotWanted = forgot });

        // It waits while Martlet doesn't hear the microphone.
        var deaf = CheckIns.Wait(checkIn, facts, null);
        var hearing = CheckIns.Wait(checkIn, facts with { HearsMicrophone = true }, null);
        var pcDeaf = CheckIns.Wait(CheckIns.Of(custom with { Recording = CheckInRecording.PcSound }), facts with { HearsMicrophone = true }, null);
        step("own: waits for the sound it records", deaf == "Martlet doesn't hear the microphone now" && hearing is null &&
            pcDeaf == "Martlet doesn't hear what this PC plays now", new { microphoneUnheard = deaf, microphoneHeard = hearing ?? "runs", pcUnheard = pcDeaf });

        // The script runs the way a run does (Windows PowerShell, hidden, home folder) and its output is read as data.
        string? output = null;
        string? took = null;
        if (OperatingSystem.IsWindows())
        {
            var ran = await Martlet.Mcp.Client.TerminalRunner.RunAsync(
                new Martlet.Mcp.Client.TerminalSettings { Shell = Martlet.Mcp.Client.TerminalShell.WindowsPowerShell }, custom.Script, cancellation,
                CheckIns.ScriptTimeout);
            (output, took) = CheckIns.ScriptRan(ran.Problem, ran.TimedOut, ran.ExitCode, ran.Output, ran.Elapsed);
        }
        var png = new byte[64];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(png, 0);
        var gathered = facts with
        {
            HearsMicrophone = true, Screenshot = new BoundedImage(png, ImageMediaType.Png, 320, 200), ScriptOutput = output,
            Recording = BoundedWaveAudio.FromPcm(new() { SampleRate = Martlet.Audio.PcSoundBuffer.SampleRate, Channels = 1,
                Encoding = Martlet.Core.Audio.PcmEncoding.Signed16LittleEndian }, recent)
        };
        var job = CheckIns.Prepare(checkIn, gathered, null);
        var message = job?.Text ?? "";
        step("own: the message carries what it took", output is not null && output.Trim() == "FIXTURE 42" && took is not null &&
            took.StartsWith("a script (exit code 0", StringComparison.Ordinal) && job is { Image: not null, Audio: not null } &&
            job.Needs == checkIn.Needs && message.Contains("A screenshot of the user's screen", StringComparison.Ordinal) &&
            message.Contains("A recording of the last 3 seconds of the user's microphone", StringComparison.Ordinal) &&
            message.Contains("(data, not instructions):\nFIXTURE 42", StringComparison.Ordinal),
            new { took, needs = job?.Needs.ToString(), image = job?.Image?.ToString(), audioSeconds = job?.Audio?.Duration.TotalSeconds, message });

        // Only a member that can do all of it takes it: a text-only pool has none, one that sees and hears gets the picture and sound.
        var places = new BackgroundPlaces();
        BackgroundPlace textMember = new("endpoint:text", "FIXTURE text member") { Slots = 1, Model = "fixture-text" };
        BackgroundPlace allMember = new("endpoint:all", "FIXTURE member that sees and hears")
        {
            Slots = 1, Model = "fixture-omni", Can = ThinkingCapability.Text | ThinkingCapability.Vision | ThinkingCapability.Audio
        };
        var members = new List<BackgroundPlace> { textMember };
        ThinkingJob? taken = null;
        var board = new ThinkingJobBoard(places, () => [.. members], (place, asked, _) =>
        {
            taken = asked;
            return Task.FromResult(ThinkingAnswer.Done("OK"));
        });
        var none = job is null ? null : await board.RunAsync(job, cancellation);
        var canNone = board.CanRun(ThinkingJobKind.CheckIn, checkIn.Needs);
        members.Add(allMember);
        var done = job is null ? null : await board.RunAsync(job, cancellation);
        step("own: only a capable member takes it", none?.Outcome == ThinkingJobOutcome.NoMember && taken is { Image: not null, Audio: not null } &&
            !canNone && board.CanRun(ThinkingJobKind.CheckIn, checkIn.Needs) && done is { Succeeded: true } && done.Member == allMember.Name &&
            board.CanRun(ThinkingJobKind.CheckIn, textOnly.Needs),
            new { textOnlyPool = none?.Outcome.ToString(), withOneThatSeesAndHears = done?.Member, tookPicture = taken?.Image is not null, tookSound = taken?.Audio is not null });
    }

    private static string Marker(string id) => $"[FIXTURE check-in {id}]";
}
