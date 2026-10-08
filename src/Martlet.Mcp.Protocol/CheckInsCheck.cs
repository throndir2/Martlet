using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Martlet.Avatar.Hosting;
using Martlet.Conversation;

namespace Martlet.Mcp;

/// <summary>check_ins_status and check_ins_check: Martlet's check-ins (Companion › Check-ins; docs/CONVERSATION.md#check-ins).
/// The status reads a data directory's check-ins.json (the owner's choices and own check-ins) and check-ins-status.json (written
/// by the desktop on a companion PC: the pool member that can take them, why each waits and its last run; never what was said or
/// answered). The check rehearses the production code end to end with FIXTURE facts and canned answers (NOT AI): the job kind's
/// rules, check-ins.json saved and read back (and a bad one refused), when each built-in check-in waits or runs, the message it
/// sends, its run on a production ThinkingJobBoard with a fixture member, reading the answers (OFF tags, KEEP, USUAL, REMIND:,
/// SAY:, OK, a &lt;think&gt; block, an answer that can't be read), and what Martlet then does: a reply's lingering emotes off on a
/// production HeldEmotes (never the owner's try), a reminder on a production ContextBoard that goes with exactly one request,
/// and something to bring up worded through BackgroundJobs beside a due reminder. No model, network or credential is used.</summary>
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
                    prompt = c.PromptId, facts = c.Custom ? c.Facts.ToString() : null, task = c.Task, does = c.Does
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
        characterReplies = CheckIns.CharacterReplies, everyChoices = CheckIns.EveryChoices, maximumCustom = CheckIns.MaximumCustom,
        jobKind = ThinkingJobKinds.Name(ThinkingJobKind.CheckIn), priority = ThinkingJobKinds.Priority(ThinkingJobKind.CheckIn).ToString(),
        fast = ThinkingJobKinds.IsFast(ThinkingJobKind.CheckIn), stoppedWhenLive = LiveFloorRules.Stops(ThinkingJobKind.CheckIn)
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
            var saved = new CheckInSettings().With(CheckIns.Gaze, false, 10).With(mine);
            var wrote = saved.Save(folder);
            var (read, state) = CheckInSettings.Read(folder);
            var all = CheckIns.All(read);
            var refused = false;
            try { new CheckInSettings().With(CheckIns.Emotes, true, 7).Validate(); }
            catch (Martlet.Core.Contracts.ContractException) { refused = true; }
            Step("settings saved and read back", wrote && state == "loaded" && all.Count == 5 &&
                all.Single(c => c.Id == CheckIns.Gaze) is { On: false, EveryMinutes: 10 } && all.Single(c => c.Id == "c1") is { Custom: true, Outcome: CheckInOutcome.Say } &&
                CheckIns.All(new CheckInSettings()).All(c => c.On) && refused,
                new { state, checkIns = all.Select(c => $"{c.Id}: {(c.On ? "on" : "off")}, every {c.EveryMinutes} min, {c.Outcome}"), badPaceRefused = refused });
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
            Work = ["Thinking longer (running): FIXTURE plan for the trip"]
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
            ["own: off, but Check now"] = CheckIns.Wait(CheckIns.Of(new() { Id = "c2", Name = "FIXTURE", Task = "Check something." }), facts,
                new(now.AddMinutes(-1), 4, "nothing to remind Martlet of", false), now: true)
        };
        Step("when check-ins wait", waits["emotes: due"] is null && waits["emotes: only young ones"]?.Contains("3 min") == true &&
            waits["emotes: character hidden"] == "the character isn't showing" && waits["emotes: ran 2 min ago"] == "next in 3 min" &&
            waits["emotes: kept 6 min ago, nothing new"] == "next in 9 min" &&
            waits["emotes: you talk"] == "the conversation is busy" && waits["emotes: nobody here"]?.StartsWith("nobody used this PC", StringComparison.Ordinal) == true &&
            waits["gaze: due"] is null && waits["gaze: chosen 1 min ago"] is not null && waits["promises: new exchanges"] is null &&
            waits["promises: nothing new"] == "nothing new was said since the last check" && waits["character: 4 new replies"] is null &&
            waits["character: no personality"] is not null && waits["own: off, but Check now"] is null,
            waits.ToDictionary(w => w.Key, w => w.Value ?? "runs"));

        // 4. The messages and their run on a production job board with a fixture member (canned answers, NOT AI).
        var answers = new Dictionary<string, string>
        {
            [CheckIns.Emotes] = "<think>The blush was for the win, which is over; glasses fit the bug hunt.</think>\nOFF {blush}",
            [CheckIns.Gaze] = "Hmm, she is helping with a bug now, so following the mouse fits better.\n**USUAL**",
            [CheckIns.Promises] = "- REMIND: You said you'd remind them to check the oven in 10 minutes but never set the reminder; set it now, or tell them you can't.",
            [CheckIns.Character] = "OK",
            ["c1"] = "SAY: They've been at it for hours; suggest a short stretch break."
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
        foreach (var checkIn in new[] { Built(CheckIns.Emotes), Built(CheckIns.Gaze), Built(CheckIns.Promises), Built(CheckIns.Character), own })
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
        Step("messages carry the facts each needs", asked.Count == 5 && asked.All(a => a.Kind == ThinkingJobKind.CheckIn && a.Instructions.Length > 0) &&
            emotesMessage.Contains("{blush} - when flattered", StringComparison.Ordinal) && emotesMessage.Contains("{glasses}", StringComparison.Ordinal) &&
            !emotesMessage.Contains("{smile}", StringComparison.Ordinal) && emotesMessage.Contains("OFF {blush}", StringComparison.Ordinal) &&
            emotesMessage.Contains("Wednesday, October 7, 10:17 PM", StringComparison.Ordinal) &&
            asked.Any(a => a.Text.Contains("look straight ahead", StringComparison.Ordinal) && a.Text.Contains("12 min ago", StringComparison.Ordinal)) &&
            asked.Any(a => a.Text.Contains("FIXTURE plan for the trip", StringComparison.Ordinal) && a.Text.Contains("check the oven", StringComparison.Ordinal)) &&
            asked.Any(a => a.Text.Contains("playful and teasing", StringComparison.Ordinal) && a.Text.Contains("1. {blush} No way", StringComparison.Ordinal)) &&
            asked.Any(a => a.Text.Contains("SAY:", StringComparison.Ordinal) && a.Text.Contains("Nobody has used this PC", StringComparison.Ordinal) == false &&
                a.Text.Contains("The user is using this PC now.", StringComparison.Ordinal)),
            messages);
        Step("answers read", verdicts.GetValueOrDefault(CheckIns.Emotes) is { Act: true, Tags: ["blush"] } &&
            verdicts.GetValueOrDefault(CheckIns.Gaze) is { Act: true } &&
            verdicts.GetValueOrDefault(CheckIns.Promises) is { Act: true, Text: { } remind } && remind.StartsWith("You said you'd remind them", StringComparison.Ordinal) &&
            verdicts.GetValueOrDefault(CheckIns.Character) is { Act: false, Readable: true } &&
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

        return new { passed = ok, steps };
    }

    private static string Marker(string id) => $"[FIXTURE check-in {id}]";
}
