using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using Martlet.Avatar.Hosting;
using Martlet.Conversation;
using Martlet.Core.Audio;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;
using Martlet.Mcp.Client;
using Martlet.Providers;

namespace Martlet.Desktop;

/// <summary>More of MainWindow.CheckIns.cs, in a file of its own so that each file stays under 20 KB.</summary>
public partial class MainWindow
{
    // The touches settled: they fire their triggers.
    private void CheckInTouchesSettled()
    {
        checkInTouchesSettle.Stop();
        if (closing || checkInTouches.Settle(DateTimeOffset.Now) is not { } fired) return;
        FireCheckIns(fired);
    }

    // A reply ended and its exchange is in the conversation: it fires the check-ins that run after each exchange (only those
    // that are on, so the log doesn't note every reply while none is).
    private void CheckInExchangeEnded()
    {
        if (closing || Role != DeviceRole.Companion) return;
        // Thoughts ahead were about the message after the exchange they read: a newer exchange makes them stale, whether they
        // wait on the context board (the reply's request was built before they came) or are still being thought.
        checkInPlanCancel?.Cancel();
        CheckIns.DropPlans(contextBoard, CheckIns.All(checkInSettings));
        if (!CheckIns.All(checkInSettings).Any(c => c.On && c.Triggers.HasFlag(CheckInTriggers.ExchangeEnded))) return;
        FireCheckIns(CheckIns.ExchangeTrigger(DateTimeOffset.Now));
    }

    /// <summary>Gives the conversation the saved check-in choices on a companion PC (where check-ins run), so the reply leaves
    /// the tools a check-in takes over to it (<see cref="LiveConversationController.HandedOffReplyTools"/>); none elsewhere.</summary>
    private void ShareCheckInChoices()
    {
        if (conversation is not null) conversation.CheckInChoices = Role == DeviceRole.Companion ? checkInSettings : null;
    }

    /// <summary>What a check-in should have done after an exchange didn't happen (<paramref name="broken"/>, a few words, or
    /// null when it worked): the talk window says so once, and the next reply gets a reminder to tell the user, so Martlet never
    /// silently breaks a promise.</summary>
    private void ReportAfterExchange(CheckIn checkIn, string? broken)
    {
        checkInAfterExchangeProblem = broken;
        conversation?.ReportAfterExchange(broken);
        if (broken is null) return;
        ErrorLog.Warn($"Check-ins: {checkIn.Name} didn't do what was asked after the reply ({broken}).");
        PostCheckInNote(checkIn.Id, $"What you said you would do after your last reply didn't happen ({broken}). Tell the user " +
            "briefly, and don't say it is done.", homeSettings?.Prompts);
    }

    /// <summary>Something that starts check-ins happened (<paramref name="fired"/>): each check-in that is on and starts on it
    /// keeps it until it runs or it is older than <see cref="CheckIns.TriggerAge"/> (a newer one replaces it), and the triggered
    /// check-ins are looked at at once, not at the next 15-second look. A new kind of trigger calls this with its flag.</summary>
    internal void FireCheckIns(CheckInTrigger fired)
    {
        if (closing) return;
        checkInFiredLast = fired;
        var started = CheckIns.All(checkInSettings).Where(c => c.On && (c.Triggers & fired.Fired) != 0).ToArray();
        // An exchange that ends while the last one still waits counts both, and the run reads both (CheckInFacts.Latest).
        foreach (var checkIn in started)
            checkInFired[checkIn.Id] = fired.Fired == CheckInTriggers.ExchangeEnded
                ? CheckIns.ExchangeTrigger(fired.At, checkInFired.GetValueOrDefault(checkIn.Id)) : fired;
        ErrorLog.Info($"Check-ins: {fired.Fired} fired ({fired.What}); " +
            (started.Length == 0 ? "no check-in that is on starts on it." : $"it starts {string.Join(", ", started.Select(c => c.Name))}."));
        if (started.Any(c => CheckInLane(c) == 2)) FollowCheckInsAsync(triggered: true, turn: true).Forget();
        if (started.Any(c => CheckInLane(c) == 1)) FollowCheckInsAsync(triggered: true).Forget();
        if (started.Length == 0)
        {
            ShowCheckInsNow();
            WriteCheckInStatus();
        }
    }

    /// <summary>The reply to the user's own <paramref name="words"/> started (on the UI thread): the check-ins the user's turn
    /// starts (the director) get the words and run at once in their own lane.</summary>
    private void CheckInUserTurn(string words)
    {
        if (closing || Role != DeviceRole.Companion) return;
        if (!CheckIns.All(checkInSettings).Any(c => c.On && c.Triggers.HasFlag(CheckInTriggers.UserTurn))) return;
        checkInAsked = words;
        FireCheckIns(CheckIns.TurnTrigger(DateTimeOffset.Now));
    }

    /// <summary>One look at the check-ins: each says why it waits, and the first one that may run (or the one the owner asked
    /// for <paramref name="now"/>) runs on the Thinking pool. Nothing while one runs. With <paramref name="triggered"/>, the look
    /// is for check-ins that start on a trigger: they run one at a time beside the paced ones, never behind them; with
    /// <paramref name="turn"/>, for those the user's turn starts (the director), in a lane of their own.</summary>
    private async Task FollowCheckInsAsync(string? now = null, bool triggered = false, bool turn = false)
    {
        if ((turn ? checkInTurnRunning : triggered ? checkInTriggeredRunning : checkInRunning) is not null || closing) return;
        var ran = false;
        try
        {
            var (next, state) = LookAtCheckIns(now, triggered, turn);
            if (next is not null)
            {
                await RunCheckInAsync(next, state!, next.Id == now, triggered, turn);
                ran = triggered;
            }
        }
        catch (Exception error) when (error is InvalidOperationException or IOException or UnauthorizedAccessException or ContractException)
        {
            ErrorLog.Warn("Check-ins: couldn't look at the check-ins.", error);
        }
        finally
        {
            ShowCheckInsNow();
            WriteCheckInStatus();
        }
        // Another check-in the same trigger started runs right after this one, not at the next look.
        if (ran && checkInFired.Count > 0 && !closing) _ = Dispatcher.InvokeAsync(() => FollowCheckInsAsync(triggered: true, turn: turn).Forget());
    }

    /// <summary>Runs <paramref name="checkIn"/> on the Thinking pool and acts on its answer; its run is kept for the page, the
    /// status file and when it is due next. With <paramref name="triggered"/> it runs in the triggered check-ins' place (or the
    /// user's turn's, <paramref name="turn"/>) and uses up the trigger it kept.</summary>
    private async Task RunCheckInAsync(CheckIn checkIn, CheckInState state, bool now, bool triggered = false, bool turn = false)
    {
        var focused = CheckInSchedule.Focus(checkIn, state, now) with { Since = checkInRuns.GetValueOrDefault(checkIn.Id)?.Exchanged };
        var prompts = homeSettings?.Prompts;
        if (CheckInPrompts.Prepare(checkIn, focused, prompts) is null)
        {
            checkInWaits[checkIn.Id] = "its prompt is empty";
            return;
        }
        if (turn) checkInTurnRunning = checkIn.Id;
        else if (triggered) checkInTriggeredRunning = checkIn.Id;
        else checkInRunning = checkIn.Id;
        var fired = triggered && checkInFired.Remove(checkIn.Id, out var trigger) ? trigger : null;
        ShowCheckInsNow();
        var began = Stopwatch.GetTimestamp();
        string result;
        bool acted = false, kept = false;
        string? member = null, gathered = null, scriptHash = null, broken = null;
        var previous = checkInRuns.GetValueOrDefault(checkIn.Id);
        // The callback list this run may write: an answer about a conversation forgotten meanwhile writes nothing.
        var callbackVersion = conversation?.CallbackVersion ?? 0;
        // The tools of its chosen sets that this PC runs, for every member the pool tries during this run. Optional sets (lookups
        // such as People) only when a member that calls tools can take it; without one it runs without them.
        var offered = CheckIns.Offered(checkIn, conversation?.ThinkingPool.CanRun(checkIn.JobKind,
            checkIn.Needs | ThinkingCapability.Tools, checkIn.RunsOn) == true || CheckInFixture() is not null);
        var tools = offered.Count > 0
            ? new CheckInToolHost(offered, new(checkIn.Id, checkIn.Name, homeSettings?.Companion?.ActivePersonaId.ToString(),
                DateTimeOffset.Now), CheckInToolHandlers())
            : null;
        // Thinking ahead stops when another reply ends first (CheckInExchangeEnded).
        using var planned = checkIn.Outcome == CheckInOutcome.Plan ? CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token) : null;
        if (planned is not null) checkInPlanCancel = planned;
        var token = planned?.Token ?? lifetime.Token;
        try
        {
            var (withInputs, missing, took) = await GatherForCheckInAsync(checkIn, focused, token);
            gathered = took;
            if (missing is null && checkIn.RunsScript) scriptHash = CheckIns.ScriptHash(withInputs.ScriptOutput);
            if (missing is not null) result = missing;
            else if (CheckIns.ScriptUnchanged(checkIn, scriptHash, previous, now))
            {
                result = "its script printed the same as last time, so it didn't ask";
                kept = true;
            }
            // Its tools are its work: without one that runs on this PC (memory off, no reminders here) it doesn't ask.
            else if (checkIn.Outcome == CheckInOutcome.Tools && tools is not { Tools.Count: > 0 })
                result = "none of its tools works on this PC now";
            else if (CheckInPrompts.Prepare(checkIn, withInputs, prompts, tools) is not { } job) result = "its prompt is empty";
            else
            {
                var done = CheckInFixture() is { } fixture
                    ? File.Exists(fixture)
                        ? new ThinkingJobResult(ThinkingJobOutcome.Succeeded, await CheckInFixtureAnswerAsync(fixture, job.ToolHost as CheckInToolHost,
                            token), "fixture", "FIXTURE - NOT AI", null, 1)
                        : new ThinkingJobResult(ThinkingJobOutcome.Failed, null, null, null, $"{CheckInFixtureVariable} names no file", 0)
                    : await conversation!.ThinkingPool.RunAsync(job, token);
                member = done.Member is { } name ? done.Model is { } model ? $"{name} ({model})" : name : null;
                var used = tools?.Uses ?? [];
                // Its tool calls were the action: they count even when the model failed to write its last line.
                if (checkIn.Outcome == CheckInOutcome.Tools && (done.Succeeded || used.Count > 0))
                {
                    acted = used.Any(u => !u.Failed);
                    kept = done.Succeeded && used.Count == 0;
                    result = used.Count == 0 ? "no tool needed calling"
                        : $"called {used.Count} tool{(used.Count == 1 ? "" : "s")}{(acted ? "" : ", and none of them worked")}";
                }
                else if (!done.Succeeded)
                    result = done.Outcome == ThinkingJobOutcome.NoMember ? CheckInNoMember(checkIn, ran: true)
                        : $"the Thinking pool didn't answer ({done.Problem ?? done.Outcome.ToString()})";
                // Its thoughts were about the message after the exchanges it read; another reply ended meanwhile.
                else if (checkIn.Outcome == CheckInOutcome.Plan && conversation?.RecentExchanges(0).Total is { } total && total != withInputs.Exchanged)
                    result = "another reply ended before its thoughts were ready, so they were dropped";
                else
                {
                    var verdict = CheckInAnswers.Read(checkIn, done.Text, withInputs);
                    kept = verdict is { Readable: true, Act: false } && used.Count == 0;
                    (result, acted) = await ActOnCheckInAsync(checkIn, verdict, prompts, callbackVersion);
                }
                if (checkIn.Outcome != CheckInOutcome.Tools && used.Count > 0)
                {
                    result += $"; it called {used.Count} tool{(used.Count == 1 ? "" : "s")}";
                    acted |= used.Any(u => !u.Failed);
                }
                // What didn't happen, for a check-in that does the reply's work after an exchange.
                broken = used.FirstOrDefault(u => u.Failed) is { } failed ? $"{failed.Tool} failed" + (failed.Result.Length > 0 ? $": {failed.Result}" : "")
                    : !done.Succeeded && used.Count == 0 ? result : null;
            }
        }
        catch (OperationCanceledException) when (planned is { IsCancellationRequested: true } && !lifetime.IsCancellationRequested)
        {
            ErrorLog.Info($"Check-ins: {checkIn.Name} stopped: another reply ended before its thoughts were ready.");
            return;
        }
        catch (OperationCanceledException) { return; }
        finally
        {
            if (turn) checkInTurnRunning = null;
            else if (triggered) checkInTriggeredRunning = null;
            else checkInRunning = null;
            if (planned is not null && ReferenceEquals(checkInPlanCancel, planned)) checkInPlanCancel = null;
        }
        var elapsed = Stopwatch.GetElapsedTime(began);
        var uses = tools?.Uses ?? [];
        var at = DateTimeOffset.Now;
        var run = new CheckInRun(at, state.Exchanged, result, acted)
        {
            Member = member, Took = elapsed, Kept = kept, Gathered = gathered, ScriptHash = scriptHash ?? previous?.ScriptHash,
            Recent = CheckIns.Recent(previous, at), Tools = uses, Trigger = fired?.What
        };
        checkInRuns[checkIn.Id] = run;
        checkInLast = (checkIn.Name, run);
        var counts = checkInCounts.GetValueOrDefault(checkIn.Id);
        checkInCounts[checkIn.Id] = (counts.Runs + 1, counts.Acted + (acted ? 1 : 0));
        checkInWaits[checkIn.Id] = "";
        if (fired is { Fired: var by } && (by & (CheckInTriggers.ExchangeEnded | CheckInTriggers.UserTurn)) != 0 && CheckIns.ActsAfterExchanges(checkIn))
            ReportAfterExchange(checkIn, broken);
        ErrorLog.Info($"Check-ins: {checkIn.Name}{(fired is null ? "" : $" (started by {fired.What})")}" +
            $"{(member is null ? "" : " ran on " + member)} in {elapsed.TotalSeconds:0.0} s" +
            $"{(gathered is null ? "" : " with " + gathered)}: {result}{(uses.Count == 0 ? "" : " (" + CheckInPrompts.ToolsText(uses) + ")")}.");
    }

    /// <summary>The check-ins fixture's answer (<see cref="CheckInFixtureVariable"/>, never AI): its lines, except that each line
    /// "TOOL name {json}" calls that tool of the check-in's sets first, as a model would.</summary>
    private static async Task<string> CheckInFixtureAnswerAsync(string path, CheckInToolHost? tools, CancellationToken token)
    {
        var answer = new List<string>();
        var calls = 0;
        foreach (var raw in (await File.ReadAllTextAsync(path, token)).Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (tools is null || !line.StartsWith("TOOL ", StringComparison.Ordinal))
            {
                answer.Add(line);
                continue;
            }
            var rest = line[5..].Trim();
            var space = rest.IndexOf(' ');
            var (name, arguments) = space < 0 ? (rest, "{}") : (rest[..space], rest[(space + 1)..].Trim());
            if (name.Length > 0) await tools.CallAsync(new TextToolCall($"fixture-{++calls}", name, arguments), token);
        }
        return string.Join("\n", answer);
    }

    /// <summary>What a check-in takes with this run: its script's output (Windows PowerShell, hidden, in the
    /// home folder, stopped after <see cref="CheckIns.ScriptTimeout"/>), a screenshot of the screen in front and the last seconds
    /// of the microphone or of what this PC plays. <c>Missing</c> says why the run can't go on (no screenshot, no recording);
    /// <c>Took</c> says what it took in a few words (never the content).</summary>
    private async Task<(CheckInState State, string? Missing, string? Took)> GatherForCheckInAsync(CheckIn checkIn, CheckInState state,
        CancellationToken token)
    {
        if (!checkIn.Screenshot && checkIn.Recording == CheckInRecording.None && !checkIn.RunsScript) return (state, null, null);
        var took = new List<string>();
        string? output = null;
        if (checkIn.RunsScript)
        {
            var ran = await TerminalRunner.RunAsync(new TerminalSettings { Shell = TerminalShell.WindowsPowerShell }, checkIn.Script!, token,
                CheckIns.ScriptTimeout);
            (output, var tookScript) = CheckInPrompts.ScriptRan(ran.Problem, ran.TimedOut, ran.ExitCode, ran.Output, ran.Elapsed);
            took.Add(tookScript);
        }
        BoundedImage? screenshot = null;
        if (checkIn.Screenshot)
        {
            var glancer = new ScreenGlancer();
            ScreenFrame? frame = null;
            try
            {
                var shot = await Task.Run(() => glancer.Capture(ScreenScope.ActiveScreen), token);
                frame = shot.Frame;
                if (frame is null)
                    return (state, "no screenshot: " + shot.Skip switch
                    {
                        GlanceSkip.MartletInFront => "Martlet's own windows cover the screen",
                        GlanceSkip.Private => "a private window is in front",
                        GlanceSkip.Blank => "the screen is black to Martlet (a locked screen, or protected video)",
                        _ => "Martlet couldn't take one" + (shot.Note is { } why ? $" ({why})" : "")
                    }, Joined(took));
                screenshot = frame.Encode();
                took.Add($"a screenshot ({screenshot.Width}x{screenshot.Height})");
            }
            catch (Exception error) when (error is InvalidOperationException or NotSupportedException or System.Runtime.InteropServices.ExternalException)
            {
                return (state, $"no screenshot: {error.Message}", Joined(took));
            }
            finally
            {
                frame?.Clear();
                Task.Run(glancer.Release).Forget();
            }
        }
        BoundedWaveAudio? recording = null;
        if (checkIn.Recording != CheckInRecording.None)
        {
            var microphone = checkIn.Recording == CheckInRecording.Microphone;
            var pcm = (microphone ? checkInMicrophone : checkInPcSound).Recent(TimeSpan.FromSeconds(checkIn.RecordingSeconds), CheckInFresh);
            try
            {
                if (pcm.Length < Martlet.Audio.PcSoundBuffer.SampleRate * 2)
                    return (state, "no recording: Martlet didn't hear " + (microphone ? "the microphone" : "what this PC plays") + " just now",
                        Joined(took));
                recording = BoundedWaveAudio.FromPcm(new PcmFormat
                {
                    SampleRate = Martlet.Audio.PcSoundBuffer.SampleRate, Channels = 1, Encoding = PcmEncoding.Signed16LittleEndian
                }, pcm);
                took.Add($"{recording.Duration.TotalSeconds:0.#} s of {(microphone ? "the microphone" : "what this PC plays")}");
            }
            finally { Array.Clear(pcm); }
        }
        return (state with { Screenshot = screenshot, Recording = recording, ScriptOutput = output }, null, Joined(took));

        static string? Joined(List<string> parts) => parts.Count == 0 ? null : string.Join(", ", parts);
    }
}
