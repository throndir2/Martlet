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

/// <summary>Companion › Check-ins (docs/CONVERSATION.md#check-ins): every 15 seconds on a companion PC, the first check-in that is
/// due and has something to check runs on the Thinking pool (<see cref="ThinkingJobKind.CheckIn"/>, never the conversation's own
/// Thinking route), one at a time, with the facts it needs gathered here (<see cref="CheckInState"/>). Martlet acts on the
/// answer: it turns off the lingering emotes a reply left on that no longer fit, takes the eyes back to their usual gaze, puts a
/// reminder for the next reply on the context board (consumed by the next request, so prompt caches keep working) or has
/// Martlet bring something up on its own. The page (Companion › Check-ins) is <see cref="CheckInsPage"/> in Pages\CheckIns: it sets
/// each check-in on or off and how often it runs, adds the owner's own, and says why each waits and what its last run did.
/// MainWindow gives it its services in MainWindow.CheckInsPage.cs. check-ins-status.json says the same for MCP (never what was
/// said or answered).</summary>
public partial class MainWindow
{
    /// <summary>check-ins-status.json in the data directory: each check-in's choices, why it waits and its last run (never what
    /// was said or answered), which MCP's check_ins_status reads.</summary>
    internal const string CheckInStatusFile = "check-ins-status.json";
    /// <summary>A text file whose content answers every check-in instead of the Thinking pool (FIXTURE - NOT AI), for automated
    /// checks of the desktop (docs/MCP.md#check-ins). Read before each run, so a check can change it.</summary>
    internal const string CheckInFixtureVariable = "MARTLET_CHECK_INS_FIXTURE";
    private readonly DispatcherTimer checkInTimer = new() { Interval = TimeSpan.FromSeconds(15) };
    private CheckInSettings checkInSettings = new();
    private readonly Dictionary<string, CheckInRun> checkInRuns = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> checkInWaits = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (int Runs, int Acted)> checkInCounts = new(StringComparer.Ordinal);
    private string? checkInRunning;
    // The conversation's exchange count at the last look, for when each check-in is due next.
    private long checkInExchanged;
    private (string Name, CheckInRun Run)? checkInLast;
    private string? checkInStatusWritten;
    // The last seconds of the microphone and of what this PC plays: kept in memory only while a check-in that is on asks for them
    // (the PC sound also while the sound digest describes it), never saved or logged.
    private readonly Martlet.Audio.PcSoundBuffer checkInMicrophone = new(CheckInKept), checkInPcSound = new(CheckInKept);
    private static TimeSpan CheckInKept => TimeSpan.FromSeconds(CheckIns.RecordingChoices[^1]);
    // A buffer that kept nothing new for this long doesn't hear now (the capture stopped).
    private static TimeSpan CheckInFresh => TimeSpan.FromSeconds(2);
    // Check-in triggers: the touches of a burst until they settle (TouchDebounce.Quiet after the last one), the trigger each
    // check-in keeps until it runs or it is too old (a newer one replaces it), the last one fired, and the triggered check-in that
    // runs now, beside a paced one, so a triggered check-in never waits behind a paced one.
    private readonly TouchTriggers checkInTouches = new();
    private readonly DispatcherTimer checkInTouchesSettle = new() { Interval = TouchDebounce.Quiet };
    private readonly Dictionary<string, CheckInTrigger> checkInFired = new(StringComparer.Ordinal);
    private CheckInTrigger? checkInFiredLast;
    private string? checkInTriggeredRunning;
    // The run of a check-in that thinks ahead (CheckInOutcome.Plan) now, stopped when another reply ends: its thoughts were about
    // the message after the exchange it read.
    private CancellationTokenSource? checkInPlanCancel;
    // What the last check-in after an exchange couldn't do, in a few words, or null when it worked (check-ins-status.json).
    private string? checkInAfterExchangeProblem;

    private void InitializeCheckIns()
    {
        var (settings, state) = CheckInSettings.Read(store?.DataDirectory);
        checkInSettings = settings;
        if (state == "unreadable") ErrorLog.Warn($"Check-ins: {CheckInSettings.FileName} couldn't be read, so the check-ins use their defaults.");
        checkInTimer.Tick += (_, _) => FollowCheckInsAsync().Forget();
        // A trigger kept while its check-in waits (its pace, nobody at the PC...) runs at the next look once it may.
        checkInTimer.Tick += (_, _) =>
        {
            if (checkInFired.Count > 0) FollowCheckInsAsync(triggered: true).Forget();
        };
        checkInTouchesSettle.Tick += (_, _) => CheckInTouchesSettled();
    }

    private void StartCheckIns()
    {
        if (closing) return;
        checkInTimer.Start();
        try { LookAtCheckIns(); }
        catch (Exception error) when (error is InvalidOperationException or IOException or UnauthorizedAccessException or ContractException)
        {
            ErrorLog.Warn("Check-ins: couldn't look at the check-ins.", error);
        }
        WriteCheckInStatus();
    }

    /// <summary>Notes why each check-in waits now and returns the first one that may run (or the one the owner asked for
    /// <paramref name="now"/>) with the facts it would get: with <paramref name="triggered"/> only a check-in that starts on a
    /// trigger (<see cref="CheckIn.Triggers"/>), otherwise only one that runs on its pace. Nothing runs.</summary>
    private (CheckIn? Next, CheckInState? State) LookAtCheckIns(string? now = null, bool triggered = false)
    {
        var all = CheckIns.All(checkInSettings);
        KeepCheckInSound(all);
        TrackCheckInSignals(all, DateTimeOffset.Now);
        FollowAside(all, DateTimeOffset.Now);
        ShareCheckInChoices();
        var blocked = Role != DeviceRole.Companion ? "this PC is a Martlet host" : conversation is null ? "Martlet can't talk on this PC" : null;
        if (blocked is not null || now is null && !all.Any(c => c.On))
        {
            foreach (var checkIn in all) checkInWaits[checkIn.Id] = blocked is not null && (checkIn.On || checkIn.Id == now) ? blocked : "it's off";
            return (null, null);
        }
        conversation!.ReadThinkingPoolOnce();
        var state = CheckInStateNow();
        checkInExchanged = state.Exchanged;
        foreach (var (id, old) in checkInFired.ToArray())
        {
            if (state.Now - old.At <= CheckIns.Age(old)) continue;
            checkInFired.Remove(id);
            // An exchange that waited too long for its check-in is a promise Martlet didn't keep.
            if (old.Fired.HasFlag(CheckInTriggers.ExchangeEnded) && all.FirstOrDefault(c => c.Id == id) is { } waited &&
                CheckIns.ActsAfterExchanges(waited))
                ReportAfterExchange(waited, $"it waited {Reminders.Span(CheckIns.ExchangeAge)}" +
                    (checkInWaits.GetValueOrDefault(id) is { Length: > 0 } why ? ": " + why : ""));
        }
        var fixture = CheckInFixture() is not null;
        CheckIn? next = null;
        foreach (var checkIn in all)
        {
            var wait = CheckIns.Wait(checkIn, state, checkInRuns.GetValueOrDefault(checkIn.Id), checkIn.Id == now, homeSettings?.Prompts,
                checkInFired.GetValueOrDefault(checkIn.Id));
            if (wait is null && !fixture && !conversation.ThinkingPool.CanRun(ThinkingJobKind.CheckIn, checkIn.Needs, checkIn.RunsOn))
                wait = CheckInNoMember(checkIn);
            checkInWaits[checkIn.Id] = wait ?? "";
            if (wait is null && next is null && (now is null ? (checkIn.Triggers != CheckInTriggers.None) == triggered : checkIn.Id == now) &&
                checkIn.Id != checkInRunning && checkIn.Id != checkInTriggeredRunning)
                next = checkIn;
        }
        return (next, state);
    }

    private static string CheckInNoMember(CheckIn checkIn, bool ran = false) =>
        $"no Thinking pool member {(ran ? "could" : "can")} take it" +
        (checkIn.Needs == ThinkingCapability.Text ? "" : $" (it needs a model for {CheckIns.Describe(checkIn.Needs)})") +
        (checkIn.RunsOn is { Mode: not ThinkingRunsOnMode.Any } where ? $" on its Runs on ({where.Describe()})" : "");

    /// <summary>The microphone and what this PC plays keep their last seconds only while a check-in that is on asks for them, on
    /// a companion PC.</summary>
    private void KeepCheckInSound(IReadOnlyList<CheckIn> all)
    {
        var companion = Role == DeviceRole.Companion;
        checkInMicrophone.Wanted = companion && all.Any(c => c is { On: true, Recording: CheckInRecording.Microphone });
        checkInPcSound.Wanted = companion && all.Any(c => c is { On: true, Recording: CheckInRecording.PcSound });
    }

    /// <summary>A touch Martlet noticed on the desktop character (<see cref="NoticePhysical"/>): while a check-in that is on starts
    /// on touches, it counts toward the triggers of this burst, which fire once the touches settle
    /// (<see cref="TouchDebounce.Quiet"/> after the last one). It reads the touch ledger and never takes from it.</summary>
    private void CheckInTouched(PhysicalEvent physical)
    {
        if (closing || conversation is null || !PhysicalKinds.IsTouch(physical.Kind)) return;
        if (!CheckIns.All(checkInSettings).Any(c => c.On && (c.Triggers & CheckIns.ByTouches) != 0)) return;
        checkInTouches.Touched(physical, conversation.Touches.Peek(conversation.TouchNow)?.Often);
        checkInTouchesSettle.Stop();
        checkInTouchesSettle.Start();
        WriteCheckInStatus();
    }

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
        if (started.Length > 0) FollowCheckInsAsync(triggered: true).Forget();
        else
        {
            ShowCheckInsNow();
            WriteCheckInStatus();
        }
    }

    /// <summary>One look at the check-ins: each says why it waits, and the first one that may run (or the one the owner asked
    /// for <paramref name="now"/>) runs on the Thinking pool. Nothing while one runs. With <paramref name="triggered"/>, the look
    /// is for check-ins that start on a trigger: they run one at a time beside the paced ones, never behind them.</summary>
    private async Task FollowCheckInsAsync(string? now = null, bool triggered = false)
    {
        if ((triggered ? checkInTriggeredRunning : checkInRunning) is not null || closing) return;
        var ran = false;
        try
        {
            var (next, state) = LookAtCheckIns(now, triggered);
            if (next is not null)
            {
                await RunCheckInAsync(next, state!, next.Id == now, triggered);
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
        if (ran && checkInFired.Count > 0 && !closing) _ = Dispatcher.InvokeAsync(() => FollowCheckInsAsync(triggered: true).Forget());
    }

    /// <summary>Runs <paramref name="checkIn"/> on the Thinking pool and acts on its answer; its run is kept for the page, the
    /// status file and when it is due next. With <paramref name="triggered"/> it runs in the triggered check-ins' place and uses
    /// up the trigger it kept.</summary>
    private async Task RunCheckInAsync(CheckIn checkIn, CheckInState state, bool now, bool triggered = false)
    {
        var focused = CheckIns.Focus(checkIn, state, now) with { Since = checkInRuns.GetValueOrDefault(checkIn.Id)?.Exchanged };
        var prompts = homeSettings?.Prompts;
        if (CheckIns.Prepare(checkIn, focused, prompts) is null)
        {
            checkInWaits[checkIn.Id] = "its prompt is empty";
            return;
        }
        if (triggered) checkInTriggeredRunning = checkIn.Id;
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
        var offered = CheckIns.Offered(checkIn, conversation?.ThinkingPool.CanRun(ThinkingJobKind.CheckIn,
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
            else if (CheckIns.Prepare(checkIn, withInputs, prompts, tools) is not { } job) result = "its prompt is empty";
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
                    var verdict = CheckIns.Read(checkIn, done.Text, withInputs);
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
            if (triggered) checkInTriggeredRunning = null;
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
        if (fired is { Fired: var by } && by.HasFlag(CheckInTriggers.ExchangeEnded) && CheckIns.ActsAfterExchanges(checkIn))
            ReportAfterExchange(checkIn, broken);
        ErrorLog.Info($"Check-ins: {checkIn.Name}{(fired is null ? "" : $" (started by {fired.What})")}" +
            $"{(member is null ? "" : " ran on " + member)} in {elapsed.TotalSeconds:0.0} s" +
            $"{(gathered is null ? "" : " with " + gathered)}: {result}{(uses.Count == 0 ? "" : " (" + CheckIns.ToolsText(uses) + ")")}.");
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
            (output, var tookScript) = CheckIns.ScriptRan(ran.Problem, ran.TimedOut, ran.ExitCode, ran.Output, ran.Elapsed);
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

    /// <summary>Does what the answer asks for and says what came of it in a few words (never the answer's own text).
    /// <paramref name="callbackVersion"/> is the callback list's version when the run started.</summary>
    private async Task<(string Result, bool Acted)> ActOnCheckInAsync(CheckIn checkIn, CheckInVerdict verdict, PromptSettings? prompts,
        int callbackVersion = 0)
    {
        if (!verdict.Readable) return ("its answer couldn't be read, so nothing changed", false);
        if (!verdict.Act)
            return (checkIn.Outcome switch
            {
                CheckInOutcome.EmotesOff => "every emote still fits",
                CheckInOutcome.GazeUsual => "the gaze still fits",
                CheckInOutcome.Say => "nothing to bring up",
                CheckInOutcome.Tools => "its tools acted",
                CheckInOutcome.Context => "nothing to add to what Martlet knows",
                CheckInOutcome.Plan => "nothing worth thinking ahead about",
                CheckInOutcome.Callbacks => "the callback list stays as it is",
                CheckInOutcome.HoldBack => checkInAside is { } holding && holding.HoldsAt(DateTimeOffset.Now)
                    ? "nothing changed, so Martlet still holds back" : "you don't seem to be talking to someone else",
                _ => "nothing to remind Martlet of"
            }, false);
        switch (checkIn.Outcome)
        {
            case CheckInOutcome.HoldBack:
                return ActOnAside(checkIn, verdict, prompts);
            case CheckInOutcome.Callbacks:
                return conversation?.KeepCallbacks(verdict.Tags, callbackVersion) switch
                {
                    null => ("the conversation was forgotten meanwhile, so the callback list didn't change", false),
                    { Count: 0 } => ("the callback list is empty now", true),
                    var kept => ($"the callback list has {kept.Count} {(kept.Count == 1 ? "thing" : "things")} now", true)
                };
            case CheckInOutcome.EmotesOff:
            {
                var off = await TurnOffReplyEmotesAsync(tag => verdict.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase));
                return off.Count == 0 ? ("the emotes it named were already off", false) : ("turned off " + string.Join(" and ", off), true);
            }
            case CheckInOutcome.GazeUsual:
                return avatar.Gaze.BackToUsual("A check-in") ? ("took the eyes back to their usual gaze", true) : ("the eyes already did their usual", false);
            case CheckInOutcome.Note:
            {
                if (PostCheckInNote(checkIn.Id, verdict.Text!, prompts) is { } problem) return (problem, false);
                return ($"a reminder waits for the next reply ({verdict.Text!.Length} characters)", true);
            }
            case CheckInOutcome.Context:
            {
                if (CheckIns.Context(prompts, verdict.Text!) is not { } context)
                    return ("the Check-in: adds to what Martlet knows prompt is empty, so nothing went to the conversation", false);
                try { contextBoard.Post(CheckIns.Source(checkIn.Id), context, DateTimeOffset.Now, CheckIns.ContextAge, consume: true); }
                catch (InvalidOperationException) { return ("the context board is full, so nothing went to the conversation", false); }
                return ($"background for the next reply waits {CheckIns.ContextAge.TotalMinutes:0} minutes at most " +
                    $"({verdict.Text!.Length} characters)", true);
            }
            case CheckInOutcome.Plan:
            {
                if (CheckIns.Plan(prompts, verdict.Text!) is not { } plan)
                    return ("the Check-in: thoughts ahead for the next reply prompt is empty, so nothing went to the conversation", false);
                try { contextBoard.Post(CheckIns.Source(checkIn.Id), plan, DateTimeOffset.Now, CheckIns.PlanAge, consume: true); }
                catch (InvalidOperationException) { return ("the context board is full, so nothing went to the conversation", false); }
                return ($"thoughts ahead wait for the next reply ({verdict.Text!.Length} characters)", true);
            }
            default:
                return ConversationSession()?.BringUp(checkIn.Name, verdict.Text!) is null
                    ? ("Martlet can't talk now, so it was dropped", false)
                    : ($"Martlet brings it up as soon as it's free ({verdict.Text!.Length} characters)", true);
        }
    }

    /// <summary>What the check-ins may know now (on the UI thread): the time, the personality, the conversation's newest
    /// exchanges, what Martlet said lately with when, how long it and this PC have been quiet, the lingering emotes a reply
    /// turned on, a gaze a reply chose, the reminders and background work, the context board's screen and sound notes, and what
    /// the user did to the character lately (read without taking it from the next reply).</summary>
    private CheckInState CheckInStateNow()
    {
        var now = DateTimeOffset.Now;
        var persona = homeSettings?.Companion?.ActivePersona;
        var (exchanges, total) = conversation?.RecentExchanges(8) ?? ([], 0);
        var showing = avatar.IsShowing;
        var catalog = showing ? characterActions.For(avatar.InspectedProfile?.ModelPath) : null;
        var emotes = catalog is null ? [] : avatar.Held.Current.Where(h => h.ByReply)
            .Select(h => catalog.Entries.FirstOrDefault(e => e.Source.Id == h.Source.Id) is { Action.Tag: { Length: > 0 } tag } entry
                ? new CheckInEmote(tag, CharacterActions.Hint(entry.Source, entry.Action), now - h.Since) : null)
            .OfType<CheckInEmote>().ToArray();
        CheckInGaze? gaze = null;
        if (showing && avatar.Gaze.ChosenSince is { } since)
        {
            var settings = avatar.Gaze.Settings;
            gaze = new(CharacterGaze.Does(settings.Mode), CharacterGaze.Does(settings.Usual), now - since);
        }
        var board = contextBoard.Snapshot(now);
        return WithCheckInSignals(new()
        {
            Now = now, Name = persona?.Name, Persona = persona?.Text, Conversation = openConversation is not null, Exchanges = exchanges,
            Exchanged = total, Quiet = openConversation?.SinceActivity, Away = TimeSpan.FromSeconds(ReminderIdleSeconds()),
            CharacterShows = showing, Emotes = emotes, Gaze = gaze, Work = CheckInWork(now),
            Screen = board.Notes.FirstOrDefault(n => n.Source == ContextBoard.Screen)?.Text,
            Sound = board.Notes.FirstOrDefault(n => n.Source == ContextBoard.Sound)?.Text,
            Said = conversation?.RecentSayings(now) ?? [],
            Callbacks = conversation?.RecentCallbacks(now) ?? [],
            // Read without taking: the next reply still drains these touches as before.
            Touches = conversation?.Touches.History(conversation.TouchNow),
            HearsMicrophone = checkInMicrophone.Hears(CheckInFresh), HearsPc = checkInPcSound.Hears(CheckInFresh),
            Adult = GenerationSettings.Adult(homeSettings?.Generation),
            Heard = conversation?.RecentHeard(now) ?? [],
            Aside = checkInAside is { } holding && holding.HoldsAt(now) ? holding : null
        });
    }

    // The reminders waiting and this conversation's background work, one short line each.
    private IReadOnlyList<string> CheckInWork(DateTimeOffset now)
    {
        var lines = new List<string>();
        foreach (var due in RemindersNow().Pending.Take(8))
            lines.Add($"A reminder for {Reminders.When(due.Reminder.Due, now, TimeZoneInfo.Local)}: {due.Reminder.Text}");
        if (conversation is { } talk)
            foreach (var job in talk.Jobs.Active.Concat(talk.Jobs.Recent).Take(8))
                lines.Add($"{LiveConversationWindow.KindTitle(job.Kind)} ({(job.Finished ? job.State == BackgroundJobState.Succeeded ? "done" : "ended" : "running")}): {job.Label}");
        return lines;
    }

    /// <summary>Check now on the page: runs that check-in at once (whether it is on or due), when it has something to check and
    /// no other check-in runs, and says what came of it.</summary>
    private async Task CheckInNowAsync(string id)
    {
        if (CheckIns.All(checkInSettings).FirstOrDefault(c => c.Id == id) is not { } checkIn) return;
        if (checkInRunning is not null || checkInTriggeredRunning == id)
        {
            ActionText.Text = "Another check-in runs now. Try again in a moment.";
            return;
        }
        ActionText.Text = $"Checking {checkIn.Name}...";
        var before = checkInRuns.GetValueOrDefault(id);
        await FollowCheckInsAsync(id);
        ActionText.Text = checkInRuns.GetValueOrDefault(id) is { } after && !ReferenceEquals(before, after)
            ? $"{checkIn.Name}: {after.Result}."
            : $"{checkIn.Name} didn't run: {(checkInWaits.GetValueOrDefault(id) is { Length: > 0 } wait ? wait : "it couldn't start")}.";
    }

    /// <summary>Says again what each check-in does now on Companion › Check-ins, when that page is open.</summary>
    private void ShowCheckInsNow()
    {
        if (openTab == CompanionTab.CheckIns) checkInsPage?.ShowNow();
    }

    private static string? CheckInFixture() => Environment.GetEnvironmentVariable(CheckInFixtureVariable) is { Length: > 0 } path ? path : null;

    /// <summary>Saves check-ins.json and uses it at once; says why when it can't.</summary>
    private bool SaveCheckIns(CheckInSettings next, string? said)
    {
        try { next.Validate(); }
        catch (ContractException error)
        {
            ActionText.Text = "Check-ins not saved: " + error.Message;
            return false;
        }
        if (!next.Save(store?.DataDirectory))
        {
            ActionText.Text = "Check-ins not saved: Martlet can't write to its data folder.";
            return false;
        }
        checkInSettings = next;
        ShareCheckInChoices();
        KeepCheckInSound(CheckIns.All(next));
        if (said is not null) ActionText.Text = said;
        ShowCheckInsNow();
        WriteCheckInStatus();
        return true;
    }

    // ---------- check-ins-status.json ----------

    // Which reply tools the check-ins that run after each exchange take over, and why (never what was said).
    private object AfterExchangeStatus()
    {
        var companion = Role == DeviceRole.Companion && conversation is not null;
        var candidates = CheckIns.HandOffs(checkInSettings, homeSettings?.Prompts, poolCallsTools: true);
        var poolCalls = companion && conversation!.PoolCallsTools();
        var handed = companion && poolCalls ? candidates : [];
        return new
        {
            poolCallsTools = poolCalls,
            handedOff = handed.Select(h => new { tool = h.Tool, checkIn = h.CheckInId, set = h.SetId }).ToArray(),
            why = !companion ? "check-ins run only on a companion PC that talks"
                : candidates.Count == 0 ? "no check-in that is on and runs after each reply ticks a set that takes over reply tools"
                : !poolCalls ? "no configured Thinking pool member takes check-ins and calls tools, so the reply keeps its tools"
                : "a check-in that runs after each reply does this work, so the reply isn't offered these tools",
            lastProblem = checkInAfterExchangeProblem
        };
    }

    private void WriteCheckInStatus()
    {
        if (store?.DataDirectory is not { } directory) return;
        try
        {
            var body = CheckInStatusBody();
            if (body == checkInStatusWritten) return;
            var path = Path.Combine(directory, CheckInStatusFile);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temporary, body);
            File.Move(temporary, path, overwrite: true);
            checkInStatusWritten = body;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    // What check-ins-status.json says: choices, waits and last runs in a few words. Never what was said, answered or reminded.
    private string CheckInStatusBody()
    {
        var member = Role == DeviceRole.Companion ? conversation?.ThinkingPool.Find(ThinkingJobKind.CheckIn) : null;
        return JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            role = Role.ToString(),
            running = checkInRunning,
            runningTriggered = checkInTriggeredRunning,
            touches = new
            {
                settling = checkInTouches.Waiting, settleMs = (long)TouchDebounce.Quiet.TotalMilliseconds,
                lastFired = checkInFiredLast is { } lastFired ? new { triggers = lastFired.Fired.ToString(), at = lastFired.At, what = lastFired.What } : null
            },
            pool = new { canRun = member is not null, member = member?.Name, model = member?.Model, fixture = CheckInFixture() is not null },
            // The reply tools a check-in that runs after each exchange takes over, and why (saved settings only).
            afterExchange = AfterExchangeStatus(),
            // Whether Martlet holds back now while you talk to someone else, and until when (never who or what was said).
            aside = AsideStatus(),
            // How many things the callback list keeps now (never what they are).
            callbacks = new { count = conversation?.RecentCallbacks(DateTimeOffset.Now).Count ?? 0, maximum = CallbackList.MaximumItems },
            checkIns = CheckIns.All(checkInSettings).Select(c =>
            {
                var last = checkInRuns.GetValueOrDefault(c.Id);
                var counts = checkInCounts.GetValueOrDefault(c.Id);
                return new
                {
                    id = c.Id, name = c.Name, custom = c.Custom, on = c.On, everyMinutes = c.EveryMinutes, outcome = c.Outcome.ToString(),
                    facts = c.Facts.ToString(), conditions = c.Conditions.ToString(), prompt = c.PromptId,
                    changed = !c.Custom && checkInSettings.Choice(c.Id) is { } choice && choice != new CheckInChoice(choice.On, choice.EveryMinutes),
                    needs = c.Needs.ToString(), canRun = member is not null && conversation!.ThinkingPool.CanRun(ThinkingJobKind.CheckIn, c.Needs, c.RunsOn),
                    // Its own Runs on (null: the check-in kind's, on Companion › Thinking pool) and the member it would go to first.
                    runsOn = c.RunsOn is { } where ? new { mode = where.Name, members = where.Members } : null,
                    firstMember = member is null ? null : conversation!.ThinkingPool.Find(ThinkingJobKind.CheckIn, c.Needs, c.RunsOn)?.Name,
                    screenshot = c.Screenshot, recording = c.Recording.ToString(),
                    recordingSeconds = c.Recording == CheckInRecording.None ? (int?)null : c.RecordingSeconds, script = c.RunsScript,
                    toolSets = c.ToolSets,
                    triggers = c.Triggers.ToString(),
                    pending = checkInFired.GetValueOrDefault(c.Id) is { } fired
                        ? new { triggers = fired.Fired.ToString(), at = fired.At, what = fired.What, expiresAt = fired.At + CheckIns.TriggerAge }
                        : null,
                    waiting = checkInWaits.GetValueOrDefault(c.Id) is { Length: > 0 } wait ? wait : null,
                    hours = c.Conditions.HasFlag(CheckInConditions.Between) ? $"{CheckIns.Clock(c.FromHour)}-{CheckIns.Clock(c.UntilHour)}" : null,
                    mostPerHour = c.MostPerHour == 0 ? (int?)null : c.MostPerHour,
                    lastHour = last?.Recent.Count(at => DateTimeOffset.Now - at < TimeSpan.FromHours(1)) ?? 0,
                    nextAt = last is null ? (DateTimeOffset?)null : last.At + CheckIns.Pace(c, last, checkInExchanged),
                    runs = counts.Runs, acted = counts.Acted,
                    last = last is null ? null : new
                    {
                        at = last.At, result = last.Result, acted = last.Acted, member = last.Member, gathered = last.Gathered,
                        trigger = last.Trigger,
                        ms = last.Took is { } took ? (long?)took.TotalMilliseconds : null,
                        // Each tool call: its set, the tool, the first line of what it answered and whether it failed.
                        tools = last.Tools.Select(t => new { set = t.Set, tool = t.Tool, result = t.Result, failed = t.Failed }).ToArray()
                    }
                };
            }).ToArray(),
            sound = new
            {
                microphoneKept = checkInMicrophone.Keeps, microphoneHeard = checkInMicrophone.Hears(CheckInFresh),
                pcKept = checkInPcSound.Keeps, pcHeard = checkInPcSound.Hears(CheckInFresh)
            },
            // Which signals Martlet follows now, in counts and yes or no only (never what you do, who spoke or what they said).
            signals = new
            {
                attentionWatched = checkInAttentionTimer.IsEnabled, activityKnown = checkInActivity is not null,
                cameBackMinutesAgo = checkInCameBack is { } back ? (int?)(DateTimeOffset.Now - back.At).TotalMinutes : null,
                voicesLately = conversation?.RecentVoices(DateTimeOffset.Now).Voices.Count ?? 0,
                heardLately = conversation?.RecentHeard(DateTimeOffset.Now).Count ?? 0
            }
        }, new JsonSerializerOptions { WriteIndented = true });
    }
}
