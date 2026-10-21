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

        // Background work: the check-in's think_longer and research take the reply's arguments and take those reply tools over;
        // cancel_thinking stays on the reply.
        var work = CheckInToolSets.Find(BackgroundWorkTools.SetId);
        step("tools: background work takes over the reply's think_longer and research",
            work is not null && work.Replaces.SequenceEqual([ThinkLonger.Name, WebResearch.Name]) && !work.Replaces.Contains(ThinkLonger.CancelName) &&
            work.Tools.Single(t => t.Name == ThinkLonger.Name).ParametersJson == ThinkLonger.Definitions(new()).First().ParametersJson &&
            work.Tools.Single(t => t.Name == WebResearch.Name).ParametersJson == WebResearch.Definition.ParametersJson &&
            BackgroundWorkTools.NotStarted("Web research is off.") == "Not started: Web research is off.",
            new { set = work?.Id, tools = work?.Tools.Select(t => t.Name), replaces = work?.Replaces });

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
        var job = CheckInPrompts.Prepare(checkIn, CheckInSchedule.Focus(checkIn, facts), null, host);
        var message = job?.Text ?? "";
        step("tools: the job offers them", checkIn.Needs.HasFlag(ThinkingCapability.Tools) && CheckIns.Describe(checkIn.Needs).Contains("tool calls", StringComparison.Ordinal) &&
            job is { ToolHost: not null, MaxToolRounds: CheckIns.MaximumToolRounds } && job.Tools.Select(t => t.Name).SequenceEqual(
                [CheckInToolSets.RemindNextReply, CheckInToolSets.BringUp, CheckInToolSets.TurnOffEmote, CheckInToolSets.LookUsual]) &&
            job.Required.HasFlag(ThinkingCapability.Tools) && message.Contains("Use your tools for what needs doing", StringComparison.Ordinal) &&
            CheckInSchedule.Wait(CheckIns.Of(custom with { ToolSets = [] }), facts, null) == "it has no tools to use",
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
        var verdict = CheckInAnswers.Read(checkIn, done?.Text, facts);
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
                textOnlyPool = none?.Outcome.ToString(), withOneThatCallsTools = done?.Member, calls = uses.Count, made = CheckInPrompts.ToolsText(uses),
                answer = done?.Text
            });

        // Memory care: a built-in check-in that works on memory in several steps, with every Memory tool, more time and more room
        // to write than a check-in without tools. A FIXTURE handler answers the calls (NOT AI, no memory store).
        var care = CheckIns.BuiltIn.SingleOrDefault(c => c.Id == CheckIns.MemoryCare);
        var cared = new List<string>();
        var careHost = care is null ? null : new CheckInToolHost(care.ToolSets, new(care.Id, care.Name, null, facts.Now),
            new Dictionary<string, CheckInToolHandler>
            {
                [MemoryToolSet.Id] = (call, _, _) =>
                {
                    lock (cared) cared.Add(call.Name);
                    return ValueTask.FromResult(new ConversationToolResult($"Memory: {MemoryToolSet.Action(call.Name)}.\nFIXTURE"));
                }
            });
        var careJob = care is null ? null : CheckInPrompts.Prepare(care, CheckInSchedule.Focus(care, facts), null, careHost);
        // More calls than the old limit of 8, in the steps a model would take: look, find, then fix many facts.
        string[] plan = [MemoryToolSet.Overview, MemoryToolSet.Find, MemoryToolSet.Find, MemoryToolSet.History, MemoryToolSet.Note,
            MemoryToolSet.UpdateMany, MemoryToolSet.Merge, MemoryToolSet.Outdated, MemoryToolSet.Update, MemoryToolSet.Forget,
            MemoryToolSet.Find, MemoryToolSet.Note];
        var careAnswers = new List<ConversationToolResult>();
        if (careHost is not null)
            foreach (var name in plan) careAnswers.Add(await careHost.CallAsync(new("care-" + careAnswers.Count, name, "{}"), cancellation));
        step("memory care: a built-in check-in that works on memory in several steps",
            care is { On: true, Outcome: CheckInOutcome.Tools, EveryMinutes: 60, Conditions: CheckInConditions.SomethingNew } &&
            care.ToolSets.SequenceEqual([MemoryToolSet.Id]) && care.Needs.HasFlag(ThinkingCapability.Tools) &&
            CheckInPrompts.Placed(PromptSettings.Text(null, PromptCatalog.CheckInMemory)) == (CheckInFacts.Latest | CheckInFacts.Activity) &&
            careJob is { MaxToolRounds: CheckIns.MaximumToolRounds, MaxOutputTokens: CheckIns.MaximumToolOutputTokens } &&
            careJob.Timeout == CheckIns.ToolTimeout && careJob.Tools.Select(t => t.Name).SequenceEqual(MemoryToolSet.Set.Tools.Select(t => t.Name)) &&
            careJob.Text.Contains(CheckInPrompts.Steps, StringComparison.Ordinal) && careJob.Text.Contains("memory_note", StringComparison.Ordinal) &&
            careJob.Text.Contains("with where", StringComparison.Ordinal) &&
            new[] { MemoryToolSet.Remember, MemoryToolSet.Update, MemoryToolSet.UpdateMany }.All(name =>
                careJob.Tools.Single(t => t.Name == name).ParametersJson.Contains("\"where\"", StringComparison.Ordinal)) &&
            CheckIns.MaximumToolRounds <= BoundedTextInput.HardMaxToolRounds && plan.Length > 8 &&
            careAnswers.All(a => !a.IsError) && cared.SequenceEqual(plan),
            new
            {
                care?.Name, care?.On, care?.EveryMinutes, tools = careJob?.Tools.Select(t => t.Name), careJob?.MaxToolRounds,
                maximumToolCalls = CheckIns.MaximumToolCalls, timeoutMinutes = careJob?.Timeout.TotalMinutes, careJob?.MaxOutputTokens,
                calls = cared.Count, made = careHost is null ? null : CheckInPrompts.ToolsText(careHost.Uses)
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
        var job = CheckInPrompts.Prepare(describes, CheckInSchedule.Focus(describes, facts), null);
        BackgroundPlace member = new("endpoint:fixture-context", "FIXTURE member") { Slots = 1, Model = "fixture-model" };
        var board = new ThinkingJobBoard(new BackgroundPlaces(), () => [member], (_, _, _) => Task.FromResult(ThinkingAnswer.Done(answer)));
        var result = job is null ? null : await board.RunAsync(job, cancellation);
        var verdict = CheckInAnswers.Read(describes, result?.Text, facts);
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
            first.Text.Contains("Use it as background", StringComparison.Ordinal) && first.Text.Contains("hunched over a stubborn bug", StringComparison.Ordinal) &&
            !second.Sources.Contains("check-in-c7") && !stale.Sources.Contains("check-in-c7") && emptied is null &&
            CheckIns.ContextAge < CheckIns.NoteAge,
            new
            {
                firstNotes = first.Text, secondSources = second.Sources, staleSources = stale.Sources,
                contextAgeMinutes = CheckIns.ContextAge.TotalMinutes, emptiedPromptPosts = emptied is not null
            });

        var nothing = new Dictionary<string, CheckInVerdict>
        {
            ["KNOW: nothing to add."] = CheckInAnswers.Read(describes, "KNOW: nothing to add.", facts),
            ["OK"] = CheckInAnswers.Read(describes, "OK", facts),
            ["REMIND: a reminder instead"] = CheckInAnswers.Read(describes, "REMIND: Be gentle.", facts),
            ["chatter"] = CheckInAnswers.Read(describes, "Things look calm to me.", facts)
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
        var deaf = CheckInSchedule.Wait(checkIn, facts, null);
        var hearing = CheckInSchedule.Wait(checkIn, facts with { HearsMicrophone = true }, null);
        var pcDeaf = CheckInSchedule.Wait(CheckIns.Of(custom with { Recording = CheckInRecording.PcSound }), facts with { HearsMicrophone = true }, null);
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
            (output, took) = CheckInPrompts.ScriptRan(ran.Problem, ran.TimedOut, ran.ExitCode, ran.Output, ran.Elapsed);
        }
        var png = new byte[64];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(png, 0);
        var gathered = facts with
        {
            HearsMicrophone = true, Screenshot = new BoundedImage(png, ImageMediaType.Png, 320, 200), ScriptOutput = output,
            Recording = BoundedWaveAudio.FromPcm(new() { SampleRate = Martlet.Audio.PcSoundBuffer.SampleRate, Channels = 1,
                Encoding = Martlet.Core.Audio.PcmEncoding.Signed16LittleEndian }, recent)
        };
        var job = CheckInPrompts.Prepare(checkIn, gathered, null);
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

        // Runs on: a check-in's own choice (on its card) goes with its job and comes before the check-in kind's. The kind runs on
        // Smart only here; the check-in chose These members, so the less smart member takes it.
        BackgroundPlace smart = new("endpoint:smart", "FIXTURE smart member") { Slots = 1, Model = "fixture-70b", Smarts = ThinkingSmarts.Smart };
        BackgroundPlace fast = new("endpoint:fast", "FIXTURE fast member") { Slots = 1, Model = "fixture-2b", Smarts = ThinkingSmarts.Fast };
        var runsOnBoard = new ThinkingJobBoard(new BackgroundPlaces(), () => [fast, smart], (place, _, _) => Task.FromResult(ThinkingAnswer.Done("OK")))
        {
            RunsOn = _ => ThinkingRunsOn.SmartOnly
        };
        var own = custom with { Id = "c2", RunsOn = ThinkingRunsOn.Only([fast.Id]), Screenshot = false, Recording = CheckInRecording.None, Needs = ThinkingCapability.Text };
        var saved = new CheckInSettings().With(own);
        var reread = saved.Custom[0];
        var ownJob = CheckInPrompts.Prepare(CheckIns.Of(reread), gathered, null);
        var byKind = ownJob is null ? null : await runsOnBoard.RunAsync(ownJob with { RunsOn = null }, cancellation);
        var byCard = ownJob is null ? null : await runsOnBoard.RunAsync(ownJob, cancellation);
        step("own: Runs on on its card comes before the check-in kind's",
            reread.RunsOn == own.RunsOn && ownJob?.RunsOn == own.RunsOn && byKind?.Member == smart.Name && byCard?.Member == fast.Name &&
            byCard?.RunsOn == "members",
            new { kind = "smart-only", byKind = byKind?.Member, card = own.RunsOn?.Name, byCard = byCard?.Member, why = byCard?.Placed });
    }
}
