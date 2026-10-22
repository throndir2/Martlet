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
            try { new CheckInSettings().With(custom with { Triggers = (CheckInTriggers)128 }).Validate(); }
            catch (Martlet.Core.Contracts.ContractException) { refused = true; }
            step("triggers: saved and read back", wrote && state == "loaded" && all.Single(c => c.Id == "c4").Triggers == custom.Triggers &&
                all.Single(c => c.Id == CheckIns.Gaze).Triggers == CheckInTriggers.TouchesEnded &&
                CheckIns.All(null).All(c => c.Triggers == (c.Id == CheckIns.DescribeTouches ? CheckInTriggers.TouchesEnded
                    : c.Id == CheckIns.Reactions ? CheckIns.ByTouches
                    : c.Id == CheckIns.Director ? CheckInTriggers.UserTurn : c.Id is CheckIns.Ahead or CheckIns.Callbacks ? CheckInTriggers.ExchangeEnded
                    : c.Id == CheckIns.Aside ? CheckInTriggers.Spoke : CheckInTriggers.None)) && refused,
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
            ["no trigger"] = CheckInSchedule.Wait(checkIn, facts, null),
            ["only touches ended"] = CheckInSchedule.Wait(checkIn, facts, null, fired: plain! with { At = now }),
            ["fired, while the conversation is busy"] = CheckInSchedule.Wait(checkIn, busy, null, fired: trigger),
            ["fired, ran 2 min ago"] = CheckInSchedule.Wait(checkIn, facts, new(now.AddMinutes(-2), 4, "nothing to remind Martlet of", false), fired: trigger),
            ["fired 3 min ago"] = CheckInSchedule.Wait(checkIn, facts, null, fired: trigger with { At = now.AddMinutes(-3) }),
            ["fired, nobody here"] = CheckInSchedule.Wait(checkIn, facts with { Away = TimeSpan.FromMinutes(25) }, null, fired: trigger),
            ["fired, but off"] = CheckInSchedule.Wait(checkIn with { On = false }, facts, null, fired: trigger),
            ["Check now, no trigger"] = CheckInSchedule.Wait(checkIn, busy, null, now: true),
            ["paced, while the conversation is busy"] = CheckInSchedule.Wait(paced, busy, null, fired: trigger)
        };
        var job = CheckInPrompts.Prepare(checkIn, facts, null);
        step("triggers: when a triggered check-in waits", waits["no trigger"] == "it waits for an intimate touch or a stroke across 3 zones" &&
            waits["only touches ended"] == waits["no trigger"] && waits["fired, while the conversation is busy"] is null &&
            waits["fired, ran 2 min ago"] == "next in 3 min" && waits["fired 3 min ago"] == waits["no trigger"] &&
            waits["fired, nobody here"]?.StartsWith("nobody used this PC", StringComparison.Ordinal) == true && waits["fired, but off"] == "it's off" &&
            waits["Check now, no trigger"] is null && waits["paced, while the conversation is busy"] == "the conversation is busy" &&
            job?.Kind == ThinkingJobKind.CheckIn && LiveFloorRules.Stops(ThinkingJobKind.CheckIn),
            new { waits = waits.ToDictionary(w => w.Key, w => w.Value ?? "runs"), jobKind = job is null ? null : ThinkingJobKinds.Name(job.Kind) });
    }

    /// <summary>10b. The director (NOT AI): the user's turn fires it with no cooldown, it waits as long as an exchange, it runs as
    /// its own pool kind, it reads what the user just said, the exchanges since it last ran and the skill index, its default tool
    /// sets are next-reply, skills, knowledge and the sets that take over reply tools, and the reply hands those tools off only
    /// while it is on and the configured pool has a member that takes director jobs and calls tools.</summary>
    private static void AfterExchange(Action<string, bool, object?> step, DateTimeOffset now, CheckInState facts)
    {
        var director = CheckIns.All(null).Single(c => c.Id == CheckIns.Director);
        var turn = CheckIns.TurnTrigger(now.AddSeconds(-2));
        var exchange = CheckIns.ExchangeTrigger(now.AddSeconds(-30));
        var expectedSets = CheckInToolSets.All.Where(s => s.Replaces.Count > 0 || s.Id is CheckInToolSets.NextReplyId or SkillTools.SetId or
            KnowledgeTools.SetId or PeopleTools.SetId).Select(s => s.Id).ToArray();
        var justRan = new CheckInRun(now.AddSeconds(-5), facts.Exchanged, "called 1 tool", true);
        step("director: the built-in check-in, its trigger and its pool kind", director is { On: true, Outcome: CheckInOutcome.Tools,
                Triggers: CheckInTriggers.UserTurn, JobKind: ThinkingJobKind.Director } &&
            director.Facts == (CheckInFacts.Asked | CheckInFacts.Latest | CheckInFacts.Work | CheckInFacts.Skills) &&
            director.ToolSets.SequenceEqual(expectedSets) && CheckIns.Age(turn) == CheckIns.ExchangeAge &&
            CheckIns.Fires(director, turn, now) && !CheckIns.Fires(director, exchange, now) &&
            CheckInSchedule.Wait(director, facts, justRan, fired: turn) is null &&
            CheckIns.TriggerWords(CheckInTriggers.UserTurn) == "you to say or type something to Martlet" &&
            CheckIns.All(null).All(c => c.Id != CheckIns.Actions) && LiveFloorRules.Stops(ThinkingJobKind.Director),
            new { sets = director.ToolSets, kind = ThinkingJobKinds.Name(director.JobKind), ageMinutes = CheckIns.ExchangeAge.TotalMinutes });

        // It reads what the user just said, the exchanges since it last ran and the skill index (never the pinned skills).
        var index = Director.SkillIndex(null) ?? "";
        var message = CheckInPrompts.Message(director, facts with { Since = 3, Asked = "FIXTURE what's on my calendar tomorrow?", Skills = index }, null) ?? "";
        var job = CheckInPrompts.Prepare(director, facts with { Asked = "FIXTURE hi", Skills = index }, null);
        step("director: what it reads", message.Contains("The user just said: FIXTURE what's on my calendar tomorrow?", StringComparison.Ordinal) &&
            message.Contains("This bug is driving me crazy", StringComparison.Ordinal) && message.Contains("web-research: ", StringComparison.Ordinal) &&
            !index.Contains(SkillCatalog.LookIntoIt, StringComparison.Ordinal) && !message.Contains("{skills}", StringComparison.Ordinal) &&
            message.Contains("Use your tools for what needs doing", StringComparison.Ordinal) && job?.Kind == ThinkingJobKind.Director &&
            Director.Load(null, SkillCatalog.Songs, "Mira", "pass").Output.StartsWith("Loaded the skill songs.", StringComparison.Ordinal),
            new { skillIndex = index.Split('\n'), jobKind = job is null ? null : ThinkingJobKinds.Name(job.Kind), messageCharacters = message.Length });

        // Which reply tools it takes over: the sets' Replaces, only while it is on and the configured pool has a member that takes
        // director jobs and calls tools (a member for check-ins only is not enough).
        var replaced = CheckInToolSets.All.Where(s => s.Replaces.Count > 0).SelectMany(s => s.Replaces).Distinct(StringComparer.Ordinal).ToArray();
        var handed = CheckIns.HandOffs(null, null, poolCallsTools: true);
        var noPool = CheckIns.HandOffs(null, null, poolCallsTools: false);
        var checkInsOnly = CheckIns.HandOffs(null, null, kind => kind == ThinkingJobKind.CheckIn);
        var off = CheckIns.HandOffs(new CheckInSettings().With(CheckIns.Director, false, 1), null, poolCallsTools: true);
        var asked = false;
        var lazy = CheckIns.HandOffs(new CheckInSettings().With(CheckIns.Director, false, 1), null, () => asked = true);
        var moved = CheckIns.All(new CheckInSettings { BuiltIn = new Dictionary<string, CheckInChoice> { [CheckIns.Actions] = new(false, 1) } })
            .Single(c => c.Id == CheckIns.Director).On;
        var guidance = CheckIns.HandOffGuidance([new("fixture_tool", CheckIns.Director, director.Name, "fixture-set", "Reminders")], null) ?? "";
        step("director: the reply hands tools off", handed.Select(h => h.Tool).SequenceEqual(replaced) && replaced.Contains(PcTools.ServersName) &&
            handed.All(h => h.CheckInId == CheckIns.Director) && noPool.Count == 0 && checkInsOnly.Count == 0 && off.Count == 0 && lazy.Count == 0 &&
            !asked && !moved && guidance.Contains("while you answer: Reminders.", StringComparison.Ordinal) && CheckIns.HandOffGuidance([], null) is null,
            new { handedOff = handed.Select(h => $"{h.Tool} ({h.SetId})"), guidance, actionsOffMovesToDirector = !moved });
    }
    // The built-in check-ins that wait for a signal and are off until the owner turns them on.
    private static bool Signaled(string id) => id is CheckIns.Welcome or CheckIns.Others;

    /// <summary>9. Signals (FIXTURE facts, canned answers, NOT AI): each signal condition waits and runs on the production
    /// <see cref="CheckInSchedule.Wait"/>, the new facts are worded as the check reads them, the four signal check-ins ask and read their
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
            ["came back"] = CheckInSchedule.Wait(Built(CheckIns.Welcome), state, null),
            ["came back: before the last run"] = CheckInSchedule.Wait(Built(CheckIns.Welcome), state with { CameBackAt = now.AddMinutes(-40) }, ran with { At = now.AddMinutes(-35) }),
            ["on a call"] = CheckInSchedule.Wait(Own(CheckInConditions.NotOnCall), state, null),
            ["not on a call"] = CheckInSchedule.Wait(Own(CheckInConditions.NotOnCall), state with { OnCall = false }, null),
            ["activity changed"] = CheckInSchedule.Wait(Own(CheckInConditions.ActivityChanged), state, null),
            ["activity same since the last run"] = CheckInSchedule.Wait(Own(CheckInConditions.ActivityChanged), state with { ActivityChangedAt = now.AddMinutes(-4) }, ran with { Recent = [] }),
            ["between 8 AM and 10 PM at 10:17 PM"] = CheckInSchedule.Wait(Own(CheckInConditions.Between), state, null),
            ["between 10 PM and 6 AM at 10:17 PM"] = CheckInSchedule.Wait(Own(CheckInConditions.Between) with { FromHour = 22, UntilHour = 6 }, state, null),
            ["unanswered"] = CheckInSchedule.Wait(Built(CheckIns.Unanswered), question, null),
            ["unanswered: no question"] = CheckInSchedule.Wait(Built(CheckIns.Unanswered), facts, null),
            ["unanswered: followed up"] = CheckInSchedule.Wait(Built(CheckIns.Unanswered), question, ran with { At = now.AddMinutes(-6) }),
            ["attention"] = CheckInSchedule.Wait(Own(CheckInConditions.Attention), state, null),
            ["no attention"] = CheckInSchedule.Wait(Own(CheckInConditions.Attention), facts, null),
            ["song ended"] = CheckInSchedule.Wait(Own(CheckInConditions.SongEnded), state, null),
            ["someone else"] = CheckInSchedule.Wait(Built(CheckIns.Others), state, null),
            ["only the owner"] = CheckInSchedule.Wait(Built(CheckIns.Others), state with { Voices = [state.Voices[0]] }, null),
            ["script changed: no script"] = CheckInSchedule.Wait(Own(CheckInConditions.ScriptChanged), state, null),
            ["at most 2 an hour"] = CheckInSchedule.Wait(Own(CheckInConditions.None) with { MostPerHour = 2 }, state, ran)
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
            [CheckIns.Welcome] = CheckInPrompts.Message(Built(CheckIns.Welcome), state, null) ?? "",
            [CheckIns.Unanswered] = CheckInPrompts.Message(Built(CheckIns.Unanswered), question with { Activity = "" }, null) ?? "",
            [CheckIns.Aside] = CheckInPrompts.Message(Built(CheckIns.Aside), state, null) ?? "",
            [CheckIns.Others] = CheckInPrompts.Message(Built(CheckIns.Others), state, null) ?? ""
        };
        var verdicts = new[] { CheckIns.Welcome, CheckIns.Unanswered, CheckIns.Aside, CheckIns.Others }
            .ToDictionary(id => id, id => CheckInAnswers.Read(Built(id), id switch
            {
                CheckIns.Welcome => "SAY: Welcome back!", CheckIns.Unanswered => "OK", CheckIns.Aside => "ASIDE: FIXTURE in a Discord call.",
                _ => "REMIND: Someone else is here; keep private things private."
            }, state));
        step("signals: the facts and the check-ins", messages[CheckIns.Welcome].Contains("after 25 min away", StringComparison.Ordinal) &&
            messages[CheckIns.Welcome].Contains("- Thinking longer finished: FIXTURE trip plan", StringComparison.Ordinal) &&
            messages[CheckIns.Welcome].Contains("starts with SAY:", StringComparison.Ordinal) &&
            messages[CheckIns.Unanswered].Contains("So, which boss is next?", StringComparison.Ordinal) &&
            messages[CheckIns.Unanswered].Contains("doesn't seem to be doing anything with sound", StringComparison.Ordinal) &&
            messages[CheckIns.Aside].Contains("in a call in Discord", StringComparison.Ordinal) &&
            messages[CheckIns.Aside].Contains("before, they were playing a game (FIXTURE Quest)", StringComparison.Ordinal) &&
            messages[CheckIns.Aside].Contains("starts with ASIDE:", StringComparison.Ordinal) &&
            messages[CheckIns.Others].Contains("- FIXTURE Owner (the user's own voice), 2 min ago", StringComparison.Ordinal) &&
            messages[CheckIns.Others].Contains("- a voice Martlet doesn't know, 1 min ago", StringComparison.Ordinal) &&
            messages[CheckIns.Others].Contains("Someone other than the user seems to be here.", StringComparison.Ordinal) &&
            verdicts[CheckIns.Welcome] is { Act: true } && verdicts[CheckIns.Unanswered] is { Act: false, Readable: true } &&
            verdicts[CheckIns.Aside] is { Act: true, Text: "FIXTURE in a Discord call." } && verdicts[CheckIns.Others] is { Act: true } &&
            CheckInPrompts.Placed("{activity} {people} {away}") == (CheckInFacts.Activity | CheckInFacts.People | CheckInFacts.WhileAway),
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
}
