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
/// Martlet bring something up on its own. The page sets each check-in on or off and how often it runs, adds the owner's own,
/// and says why each waits and what its last run did. check-ins-status.json says the same for MCP (never what was said or
/// answered).</summary>
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
    // Refreshes the open Check-ins page's status lines.
    private Action? showCheckIns;
    // The last seconds of the microphone and of what this PC plays: kept in memory only while a check-in that is on asks for them
    // (the PC sound also while the sound digest describes it), never saved or logged.
    private readonly Martlet.Audio.PcSoundBuffer checkInMicrophone = new(CheckInKept), checkInPcSound = new(CheckInKept);
    private static TimeSpan CheckInKept => TimeSpan.FromSeconds(CheckIns.RecordingChoices[^1]);
    // A buffer that kept nothing new for this long doesn't hear now (the capture stopped).
    private static TimeSpan CheckInFresh => TimeSpan.FromSeconds(2);

    private static readonly (CheckInFacts Fact, string Label, string Help)[] CheckInFactChoices =
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
            "it: its next reply still gets your touches. Placeholder: {touches}.")
    ];

    private static readonly (CheckInConditions Condition, string Label, string Help)[] CheckInConditionChoices =
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
            $"After an answer that changed nothing, and while nothing new was said, it waits {CheckIns.KeptPace} times as long.")
    ];

    // The answers a check-in can give, in the order of its Its answer list.
    private static readonly (CheckInOutcome Outcome, string Label)[] CheckInOutcomeChoices =
    [
        (CheckInOutcome.Note, "Reminds Martlet in its next reply"),
        (CheckInOutcome.Say, "Martlet brings it up"),
        (CheckInOutcome.Context, "Adds to what Martlet knows"),
        (CheckInOutcome.EmotesOff, "Turns off the emotes it names"),
        (CheckInOutcome.GazeUsual, "Takes the eyes back to their usual"),
        (CheckInOutcome.Tools, "Its tools act")
    ];

    private const string CheckInOutcomeHelp = "Martlet adds the answer format to the prompt. A reminder: a REMIND: line goes in " +
        "the notes of your next message, so Martlet's next reply follows it. Brings it up: a SAY: line, which Martlet says on its " +
        "own as soon as it's free. Adds to what Martlet knows: a KNOW: line, a short description of what is happening, goes in " +
        "the notes of your next message (within a few minutes) as background Martlet may draw on, not a reminder to follow. " +
        "Turns off emotes: OFF lines with the tags of lingering emotes to turn off (tick Emotes and " +
        "gaze). Takes the eyes back: USUAL ends the gaze a reply chose. OK or KEEP changes nothing. Its tools act: the tools it " +
        "calls (tick at least one set under It may use these tools) are what it does, and its answer only says what it did.";

    private const string CheckInPlaceholderHelp = "Placeholders put a fact where you want it: {name}, {time}, {conversation}, " +
        "{persona}, {replies}, {said}, {emotes}, {example}, {looking}, {usual}, {since}, {work}, {screen}, {sound}, " +
        "{presence} and {touches}. The facts you tick that the prompt doesn't name go after it, then the day and time and the answer format.";

    private void InitializeCheckIns()
    {
        var (settings, state) = CheckInSettings.Read(store?.DataDirectory);
        checkInSettings = settings;
        if (state == "unreadable") ErrorLog.Warn($"Check-ins: {CheckInSettings.FileName} couldn't be read, so the check-ins use their defaults.");
        checkInTimer.Tick += (_, _) => FollowCheckInsAsync().Forget();
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
    /// <paramref name="now"/>) with the facts it would get. Nothing runs.</summary>
    private (CheckIn? Next, CheckInState? State) LookAtCheckIns(string? now = null)
    {
        var all = CheckIns.All(checkInSettings);
        KeepCheckInSound(all);
        var blocked = Role != DeviceRole.Companion ? "this PC is a Martlet host" : conversation is null ? "Martlet can't talk on this PC" : null;
        if (blocked is not null || now is null && !all.Any(c => c.On))
        {
            foreach (var checkIn in all) checkInWaits[checkIn.Id] = blocked is not null && (checkIn.On || checkIn.Id == now) ? blocked : "it's off";
            return (null, null);
        }
        conversation!.ReadThinkingPoolOnce();
        var state = CheckInStateNow();
        checkInExchanged = state.Exchanged;
        var fixture = CheckInFixture() is not null;
        CheckIn? next = null;
        foreach (var checkIn in all)
        {
            var wait = CheckIns.Wait(checkIn, state, checkInRuns.GetValueOrDefault(checkIn.Id), checkIn.Id == now, homeSettings?.Prompts);
            if (wait is null && !fixture && !conversation.ThinkingPool.CanRun(ThinkingJobKind.CheckIn, checkIn.Needs))
                wait = CheckInNoMember(checkIn);
            checkInWaits[checkIn.Id] = wait ?? "";
            if (wait is null && next is null && (now is null || checkIn.Id == now)) next = checkIn;
        }
        return (next, state);
    }

    private static string CheckInNoMember(CheckIn checkIn, bool ran = false) =>
        $"no Thinking pool member {(ran ? "could" : "can")} take it" +
        (checkIn.Needs == ThinkingCapability.Text ? "" : $" (it needs a model for {CheckIns.Describe(checkIn.Needs)})");

    /// <summary>The microphone and what this PC plays keep their last seconds only while a check-in that is on asks for them, on
    /// a companion PC.</summary>
    private void KeepCheckInSound(IReadOnlyList<CheckIn> all)
    {
        var companion = Role == DeviceRole.Companion;
        checkInMicrophone.Wanted = companion && all.Any(c => c is { On: true, Recording: CheckInRecording.Microphone });
        checkInPcSound.Wanted = companion && all.Any(c => c is { On: true, Recording: CheckInRecording.PcSound });
    }

    /// <summary>One look at the check-ins: each says why it waits, and the first one that may run (or the one the owner asked
    /// for <paramref name="now"/>) runs on the Thinking pool. Nothing while one runs.</summary>
    private async Task FollowCheckInsAsync(string? now = null)
    {
        if (checkInRunning is not null || closing) return;
        try
        {
            var (next, state) = LookAtCheckIns(now);
            if (next is not null) await RunCheckInAsync(next, state!, next.Id == now);
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
    }

    /// <summary>Runs <paramref name="checkIn"/> on the Thinking pool and acts on its answer; its run is kept for the page, the
    /// status file and when it is due next.</summary>
    private async Task RunCheckInAsync(CheckIn checkIn, CheckInState state, bool now)
    {
        var focused = CheckIns.Focus(checkIn, state, now);
        var prompts = homeSettings?.Prompts;
        if (CheckIns.Prepare(checkIn, focused, prompts) is null)
        {
            checkInWaits[checkIn.Id] = "its prompt is empty";
            return;
        }
        checkInRunning = checkIn.Id;
        ShowCheckInsNow();
        var began = Stopwatch.GetTimestamp();
        string result;
        bool acted = false, kept = false;
        string? member = null, gathered = null;
        // The tools of its chosen sets that this PC runs, for every member the pool tries during this run.
        var tools = checkIn.ToolSets.Count > 0
            ? new CheckInToolHost(checkIn.ToolSets, new(checkIn.Id, checkIn.Name, homeSettings?.Companion?.ActivePersonaId.ToString(),
                DateTimeOffset.Now), CheckInToolHandlers())
            : null;
        try
        {
            var (withInputs, missing, took) = await GatherForCheckInAsync(checkIn, focused, lifetime.Token);
            gathered = took;
            if (missing is not null) result = missing;
            else if (CheckIns.Prepare(checkIn, withInputs, prompts, tools) is not { } job) result = "its prompt is empty";
            else
            {
                var done = CheckInFixture() is { } fixture
                    ? File.Exists(fixture)
                        ? new ThinkingJobResult(ThinkingJobOutcome.Succeeded, await CheckInFixtureAnswerAsync(fixture, job.ToolHost as CheckInToolHost,
                            lifetime.Token), "fixture", "FIXTURE - NOT AI", null, 1)
                        : new ThinkingJobResult(ThinkingJobOutcome.Failed, null, null, null, $"{CheckInFixtureVariable} names no file", 0)
                    : await conversation!.ThinkingPool.RunAsync(job, lifetime.Token);
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
                else
                {
                    var verdict = CheckIns.Read(checkIn, done.Text, withInputs);
                    kept = verdict is { Readable: true, Act: false } && used.Count == 0;
                    (result, acted) = await ActOnCheckInAsync(checkIn, verdict, prompts);
                }
                if (checkIn.Outcome != CheckInOutcome.Tools && used.Count > 0)
                {
                    result += $"; it called {used.Count} tool{(used.Count == 1 ? "" : "s")}";
                    acted |= used.Any(u => !u.Failed);
                }
            }
        }
        catch (OperationCanceledException) { return; }
        finally { checkInRunning = null; }
        var elapsed = Stopwatch.GetElapsedTime(began);
        var uses = tools?.Uses ?? [];
        var run = new CheckInRun(DateTimeOffset.Now, state.Exchanged, result, acted)
        {
            Member = member, Took = elapsed, Kept = kept, Gathered = gathered, Tools = uses
        };
        checkInRuns[checkIn.Id] = run;
        checkInLast = (checkIn.Name, run);
        var counts = checkInCounts.GetValueOrDefault(checkIn.Id);
        checkInCounts[checkIn.Id] = (counts.Runs + 1, counts.Acted + (acted ? 1 : 0));
        checkInWaits[checkIn.Id] = "";
        ErrorLog.Info($"Check-ins: {checkIn.Name}{(member is null ? "" : " ran on " + member)} in {elapsed.TotalSeconds:0.0} s" +
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

    /// <summary>Does what the answer asks for and says what came of it in a few words (never the answer's own text).</summary>
    private async Task<(string Result, bool Acted)> ActOnCheckInAsync(CheckIn checkIn, CheckInVerdict verdict, PromptSettings? prompts)
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
                _ => "nothing to remind Martlet of"
            }, false);
        switch (checkIn.Outcome)
        {
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
        return new()
        {
            Now = now, Name = persona?.Name, Persona = persona?.Text, Conversation = openConversation is not null, Exchanges = exchanges,
            Exchanged = total, Quiet = openConversation?.SinceActivity, Away = TimeSpan.FromSeconds(ReminderIdleSeconds()),
            CharacterShows = showing, Emotes = emotes, Gaze = gaze, Work = CheckInWork(now),
            Screen = board.Notes.FirstOrDefault(n => n.Source == ContextBoard.Screen)?.Text,
            Sound = board.Notes.FirstOrDefault(n => n.Source == ContextBoard.Sound)?.Text,
            Said = conversation?.RecentSayings(now) ?? [],
            // Read without taking: the next reply still drains these touches as before.
            Touches = conversation?.Touches.History(conversation.TouchNow),
            HearsMicrophone = checkInMicrophone.Hears(CheckInFresh), HearsPc = checkInPcSound.Hears(CheckInFresh)
        };
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
        if (checkInRunning is not null)
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

    // ---------- the page ----------

    private void RenderCheckInsTab(Panel page)
    {
        conversation?.ReadThinkingPoolOnce();
        var now = new TextBlock { FontSize = 15, TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetAutomationId(now, "CheckInsNow");
        var last = Note("", new Thickness(0, 4, 0, 0));
        AutomationProperties.SetAutomationId(last, "CheckInsLast");
        AutomationProperties.SetLiveSetting(last, AutomationLiveSetting.Polite);
        page.Children.Add(Card(Heading("Now"), now, last,
            Row(PageButton("Open Thinking pool", () => OpenCompanion(CompanionTab.DeepThinking), link: true, id: "CheckInsOpenPool"))));
        page.Children.Add(Card(Heading("How check-ins work"),
            Note("A check-in is a short question that the Thinking pool answers for Martlet every few minutes, with only the facts " +
                "it needs: the end of the conversation, what Martlet said lately, the emotes that stay on, where the eyes look, " +
                "and the reminders and work Martlet started. Martlet then acts on the answer. It turns off an emote, takes the " +
                "eyes back to their usual gaze, or puts a short reminder in the notes of your next message, so its next reply " +
                "follows it. A check-in can also call tools from the sets you tick on its card, and with Its tools act, those " +
                "calls are what it does. Its card shows each tool it called and what came of it.", new Thickness(0, 0, 0, 0)),
            Note("Check-ins run only on Thinking pool members, never on the conversation's own Thinking model, so replies never " +
                "wait for them. They wait while you talk and while nobody uses this PC, and they stay on this PC. The built-in " +
                "check-ins work like your own: change their prompt, facts, conditions and answer on their card, and Use built-in " +
                "settings puts Martlet's own back. Copy as your own makes a check-in of your own from any card.", new Thickness(0, 6, 0, 0))));
        var lines = new List<Action>();
        // Edits to the built-in check-ins' prompts and to your own check-ins save on their own, a moment after typing stops.
        var promptBoxes = new Dictionary<string, TextBox>(StringComparer.Ordinal);
        var promptsTyped = new HashSet<string>(StringComparer.Ordinal);
        var promptStates = new List<Action>();
        var promptSave = new AutoSave(() => SaveCheckInPromptsAsync(promptBoxes, promptsTyped, () =>
        {
            foreach (var show in promptStates) show();
        }));
        var builtIns = new List<(string Id, Func<CheckInChoice> Read)>();
        var rows = new List<Func<CustomCheckIn>>();
        // Synchronous on purpose: Remove saves the other rows first, and a save still running must not bring the removed row back.
        var autoSave = new AutoSave(() =>
        {
            var next = checkInSettings;
            foreach (var (id, read) in builtIns)
            {
                var choice = read();
                var standard = CheckIns.BuiltIn.First(b => b.Id == id);
                if (choice != (next.Choice(id) ?? new CheckInChoice(standard.On, standard.EveryMinutes))) next = next.With(id, choice);
            }
            CustomCheckIn[] custom = [.. rows.Select(read => read())];
            if (!custom.SequenceEqual(next.Custom)) next = next with { Custom = custom };
            if (!ReferenceEquals(next, checkInSettings)) SaveCheckIns(next, null);
            if (promptsTyped.Count > 0) promptSave.SaveNowAsync().Forget();
            return Task.FromResult(true);
        });
        tabAutoSave = autoSave;
        async Task SaveAllAsync()
        {
            await autoSave.SaveNowAsync();
            await promptSave.SaveNowAsync();
        }
        foreach (var checkIn in CheckIns.All(checkInSettings).Where(c => !c.Custom))
            page.Children.Add(BuiltInCheckInCard(checkIn, lines, promptBoxes, promptStates, id =>
            {
                tabEdited = true;
                promptsTyped.Add(id);
                autoSave.Changed();
            }, builtIns, autoSave, SaveAllAsync));
        page.Children.Add(OwnCheckInsCard(lines, rows, autoSave, SaveAllAsync));
        showCheckIns = () =>
        {
            now.Text = CheckInsNowText();
            last.Text = checkInLast is { } done
                ? $"Last check-in: {done.Name} at {done.Run.At.ToLocalTime():t}{(done.Run.Member is { } member ? " on " + member : "")}: {done.Run.Result}."
                : "No check-in has run since Martlet started.";
            foreach (var show in lines) show();
        };
        // Each line says why its check-in waits right away, not only after the next look.
        if (checkInRunning is null)
            try { LookAtCheckIns(); }
            catch (Exception error) when (error is InvalidOperationException or IOException or UnauthorizedAccessException or ContractException)
            {
                ErrorLog.Warn("Check-ins: couldn't look at the check-ins.", error);
            }
        showCheckIns();
    }

    private void ShowCheckInsNow()
    {
        if (openTab == CompanionTab.CheckIns) showCheckIns?.Invoke();
    }

    /// <summary>The page's Now line: how many check-ins are on and where they run, or why they can't.</summary>
    private string CheckInsNowText()
    {
        var on = CheckIns.All(checkInSettings).Count(c => c.On);
        var count = $"{on} check-in{(on == 1 ? " is" : "s are")} on";
        if (Role != DeviceRole.Companion) return $"{count}, but check-ins run only on a companion PC, and this PC is a Martlet host.";
        if (conversation is null) return $"{count}, but they can't run: Martlet can't talk on this PC.";
        if (CheckInFixture() is not null) return $"{count}. FIXTURE - NOT AI: {CheckInFixtureVariable} answers them, not the Thinking pool.";
        if (conversation.ThinkingPool.Find(ThinkingJobKind.CheckIn) is not { } member)
            return $"{count}, but they wait: the Thinking pool has no member that can take them. Add one on Thinking pool.";
        var unable = CheckIns.All(checkInSettings).Where(c => c.On && !conversation.ThinkingPool.CanRun(ThinkingJobKind.CheckIn, c.Needs)).ToArray();
        return $"{count}. They run on the Thinking pool ({member.Name}{(member.Model is { } model ? ", " + model : "")} first), never on the " +
            "conversation's own Thinking model." + (unable.Length == 0 ? ""
                : $" {string.Join(", ", unable.Select(c => c.Name))} {(unable.Length == 1 ? "waits" : "wait")} for a member that can take " +
                  $"{(unable.Length == 1 ? CheckIns.Describe(unable[0].Needs) : "what they need")}.");
    }

    private static string? CheckInFixture() => Environment.GetEnvironmentVariable(CheckInFixtureVariable) is { Length: > 0 } path ? path : null;

    /// <summary>A check-in's status line: running, off, why it waits or that it runs at the next chance, then its last run.</summary>
    private string CheckInLine(string id)
    {
        if (CheckIns.All(checkInSettings).FirstOrDefault(c => c.Id == id) is not { } checkIn) return "";
        var now = checkInRunning == id ? "Checking now."
            : !checkIn.On ? "Off."
            : checkInWaits.GetValueOrDefault(id) is { Length: > 0 } wait ? $"Waits: {wait}."
            : "Runs at the next chance.";
        if (checkInRuns.GetValueOrDefault(id) is not { } last) return now + " It hasn't run since Martlet started.";
        var counts = checkInCounts.GetValueOrDefault(id);
        return $"{now} Last at {last.At.ToLocalTime():t}{(last.Member is { } member ? " on " + member : "")}: {last.Result}. " +
            (last.Tools.Count > 0 ? $"Tools it called: {CheckIns.ToolsText(last.Tools)}. " : "") +
            $"{counts.Runs} run{(counts.Runs == 1 ? "" : "s")} since Martlet started, {counts.Acted} acted on.";
    }

    private Border BuiltInCheckInCard(CheckIn checkIn, List<Action> lines, Dictionary<string, TextBox> promptBoxes,
        List<Action> promptStates, Action<string> promptTyped, List<(string Id, Func<CheckInChoice> Read)> builtIns, AutoSave autoSave,
        Func<Task> saveAll)
    {
        var id = checkIn.Id;
        var standard = CheckIns.BuiltIn.First(b => b.Id == id);
        var on = new CheckBox { Content = "On", IsChecked = checkIn.On, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetAutomationId(on, "CheckInOn-" + id);
        AutomationProperties.SetName(on, checkIn.Name + " on");
        var every = EveryChoice(checkIn.EveryMinutes);
        AutomationProperties.SetAutomationId(every, "CheckInEvery-" + id);
        AutomationProperties.SetName(every, checkIn.Name + ": how often");
        var status = Note("", new Thickness(0, 6, 0, 0));
        AutomationProperties.SetAutomationId(status, "CheckInStatus-" + id);
        lines.Add(() => status.Text = CheckInLine(id));
        var (outcome, choices, read) = CheckInEditor(id, checkIn.Name, new CheckInEdit(checkIn.Facts, checkIn.Conditions, checkIn.Outcome,
            checkInSettings.Choice(id)?.Needs ?? ThinkingCapability.Text, checkIn.Screenshot, checkIn.Recording, checkIn.RecordingSeconds,
            checkIn.Script ?? "", checkIn.ToolSets), autoSave);
        // Only what differs from Martlet's own is kept, so a later Martlet can improve the rest.
        builtIns.Add((id, () =>
        {
            var edit = read();
            return new CheckInChoice(on.IsChecked == true, CheckIns.EveryChoices[Math.Max(0, every.SelectedIndex)])
            {
                Facts = edit.Facts == standard.Facts ? null : edit.Facts,
                Conditions = edit.Conditions == standard.Conditions ? null : edit.Conditions,
                Outcome = edit.Outcome == standard.Outcome ? null : edit.Outcome,
                Needs = edit.Needs == ThinkingCapability.Text ? null : edit.Needs,
                Screenshot = edit.Screenshot == standard.Screenshot ? null : edit.Screenshot,
                Recording = edit.Recording == standard.Recording ? null : edit.Recording,
                RecordingSeconds = edit.RecordingSeconds == standard.RecordingSeconds ? null : edit.RecordingSeconds,
                Script = edit.Script == (standard.Script ?? "") ? null : edit.Script,
                ToolSets = edit.ToolSets.SequenceEqual(standard.ToolSets) ? null : edit.ToolSets
            };
        }));
        on.Checked += (_, _) => autoSave.SaveNowAsync().Forget();
        on.Unchecked += (_, _) => autoSave.SaveNowAsync().Forget();
        every.SelectionChanged += (_, _) => autoSave.SaveNowAsync().Forget();
        var row = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        row.Children.Add(RowGroup(on));
        row.Children.Add(RowGroup(RowLabel("Every", every), every));
        row.Children.Add(RowGroup(RowLabel("Its answer", outcome), outcome));
        var children = new List<UIElement> { Heading(checkIn.Name), Note(checkIn.Does, new Thickness(0, 0, 0, 0)), row };
        // Its prompt, like the task of your own check-ins: edited here, saved with Martlet's other prompts (Companion › Prompts
        // lists it too).
        TextBox? promptBox = null;
        string? promptDefault = null;
        if (checkIn.PromptId is { } promptId && PromptCatalog.Find(promptId) is { } prompt)
        {
            var box = new TextBox
            {
                Text = PromptSettings.Text(homeSettings?.Prompts, promptId), AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
                MaxLength = PromptSettings.MaximumTextCharacters, MinHeight = 56, MaxHeight = 260,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 4, 0, 0)
            };
            AutomationProperties.SetAutomationId(box, "CheckInPrompt-" + id);
            AutomationProperties.SetName(box, checkIn.Name + ": what it asks");
            AutomationProperties.SetHelpText(box, prompt.Help);
            var state = Note("", new Thickness(0, 2, 0, 0));
            AutomationProperties.SetAutomationId(state, "CheckInPromptState-" + id);
            void ShowState() => state.Text = PromptState(prompt, box.Text, homeSettings?.Prompts);
            ShowState();
            promptStates.Add(ShowState);
            promptBoxes[promptId] = box;
            box.TextChanged += (_, _) =>
            {
                ShowState();
                promptTyped(promptId);
            };
            promptBox = box;
            promptDefault = prompt.Default;
            children.Add(new Label { Content = "_What it asks", Target = box, Padding = new Thickness(0, 6, 0, 0) });
            children.Add(box);
            children.Add(Note(CheckInPlaceholderHelp + " Empty it to stop this check-in from asking anything.", new Thickness(0, 2, 0, 0)));
            children.Add(state);
        }
        children.Add(choices);
        children.Add(status);
        var copy = PageButton("Copy as your own", () => CopyCheckInAsync(id, saveAll).Forget(), id: "CheckInCopy-" + id);
        AutomationProperties.SetHelpText(copy, $"Makes a check-in of your own with this one's prompt and choices, off until you turn it on.");
        var reset = PageButton("Use built-in settings", () => ResetAsync().Forget(), link: true, id: "CheckInReset-" + id);
        AutomationProperties.SetHelpText(reset, $"Puts Martlet's own prompt, facts, conditions, answer, inputs and tools for {checkIn.Name} " +
            "back. On and Every stay as they are.");
        children.Add(Row(PageButton("Check now", () => SaveThenCheckAsync().Forget(), id: "CheckInRun-" + id), copy, reset));
        return Card([.. children]);

        // A prompt still saving is saved first, so Check now asks what you typed.
        async Task SaveThenCheckAsync()
        {
            await saveAll();
            await CheckInNowAsync(id);
        }

        async Task ResetAsync()
        {
            if (promptBox is not null && promptDefault is not null) promptBox.Text = promptDefault;
            await saveAll();
            var kept = checkInSettings.Choice(id) is { } choice ? new CheckInChoice(choice.On, choice.EveryMinutes) : null;
            var next = kept is null ? checkInSettings : checkInSettings.With(id, kept);
            if (ReferenceEquals(next, checkInSettings) || SaveCheckIns(next, null))
            {
                ActionText.Text = $"{checkIn.Name} uses Martlet's own settings again.";
                RenderTab();
            }
        }
    }

    /// <summary>What a check-in's shared controls hold: what it gets to know, when it waits, its answer, what its model must
    /// handle (as chosen; a screenshot and a recording add theirs on their own), what it takes with each run and its script.</summary>
    private sealed record CheckInEdit(CheckInFacts Facts, CheckInConditions Conditions, CheckInOutcome Outcome, ThinkingCapability Needs,
        bool Screenshot, CheckInRecording Recording, int RecordingSeconds, string Script, IReadOnlyList<string> ToolSets);

    /// <summary>The controls every check-in card shares, built-in or your own: Its answer, then what it gets to know, when it
    /// waits, what it takes with each run, a script and what its model must handle. A switch or a choice saves at once, and
    /// typing saves after a short pause.</summary>
    private (ComboBox Outcome, StackPanel Choices, Func<CheckInEdit> Read) CheckInEditor(string id, string name, CheckInEdit start,
        AutoSave autoSave)
    {
        void SaveNow() => autoSave.SaveNowAsync().Forget();
        var outcome = Compact(new ComboBox
        {
            ItemsSource = CheckInOutcomeChoices.Select(c => c.Label).ToArray(),
            SelectedIndex = Math.Max(0, Array.FindIndex(CheckInOutcomeChoices, c => c.Outcome == start.Outcome)), Width = 250,
            ToolTip = CheckInOutcomeHelp
        });
        AutomationProperties.SetAutomationId(outcome, "CheckInOutcome-" + id);
        AutomationProperties.SetName(outcome, name + ": what happens with its answer");
        AutomationProperties.SetHelpText(outcome, CheckInOutcomeHelp);
        outcome.SelectionChanged += (_, _) => SaveNow();
        CheckBox Choice(string label, bool on, string help, string automationId)
        {
            var box = new CheckBox { Content = label, IsChecked = on, Margin = new Thickness(0, 0, 16, 6), ToolTip = help };
            AutomationProperties.SetAutomationId(box, automationId);
            AutomationProperties.SetHelpText(box, help);
            box.Checked += (_, _) => SaveNow();
            box.Unchecked += (_, _) => SaveNow();
            return box;
        }
        var facts = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        var factBoxes = new List<(CheckInFacts Fact, CheckBox Box)>();
        foreach (var (fact, label, help) in CheckInFactChoices)
        {
            var box = Choice(label, start.Facts.HasFlag(fact), help, $"CheckInFact-{id}-{fact}");
            factBoxes.Add((fact, box));
            facts.Children.Add(box);
        }
        var conditions = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        var conditionBoxes = new List<(CheckInConditions Condition, CheckBox Box)>();
        foreach (var (condition, label, help) in CheckInConditionChoices)
        {
            var box = Choice(label, start.Conditions.HasFlag(condition), help, $"CheckInWhen-{id}-{condition}");
            conditionBoxes.Add((condition, box));
            conditions.Children.Add(box);
        }
        // The tool sets it may call; a set it chose that this Martlet doesn't know is dropped on the next save.
        var toolSets = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        var toolBoxes = new List<(string Set, CheckBox Box)>();
        foreach (var set in CheckInToolSets.All)
        {
            var box = Choice(set.Name, start.ToolSets.Contains(set.Id), $"{set.Does} Its tools: {string.Join(", ", set.Tools.Select(t => t.Name))}.",
                $"CheckInTools-{id}-{set.Id}");
            toolBoxes.Add((set.Id, box));
            toolSets.Children.Add(box);
        }
        // The model it needs: text always; pictures and recordings by choice, and always for a screenshot or a recording.
        var ownVision = start.Needs.HasFlag(ThinkingCapability.Vision);
        var ownAudio = start.Needs.HasFlag(ThinkingCapability.Audio);
        var text = new CheckBox { Content = "Text", IsChecked = true, IsEnabled = false, Margin = new Thickness(0, 0, 16, 6) };
        AutomationProperties.SetAutomationId(text, $"CheckInNeeds-{id}-Text");
        var vision = new CheckBox { Content = "Sees pictures", Margin = new Thickness(0, 0, 16, 6) };
        AutomationProperties.SetAutomationId(vision, $"CheckInNeeds-{id}-Vision");
        AutomationProperties.SetHelpText(vision, "Only Thinking pool members whose model sees pictures take this check-in.");
        var audio = new CheckBox { Content = "Hears recordings", Margin = new Thickness(0, 0, 16, 6) };
        AutomationProperties.SetAutomationId(audio, $"CheckInNeeds-{id}-Audio");
        AutomationProperties.SetHelpText(audio, "Only Thinking pool members whose model hears recordings take this check-in.");
        var needs = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        needs.Children.Add(text);
        needs.Children.Add(vision);
        needs.Children.Add(audio);
        var screenshot = new CheckBox
        {
            Content = "A screenshot of the screen in front", IsChecked = start.Screenshot, Margin = new Thickness(0, 0, 16, 6),
            VerticalAlignment = VerticalAlignment.Center
        };
        AutomationProperties.SetAutomationId(screenshot, "CheckInScreenshot-" + id);
        AutomationProperties.SetHelpText(screenshot, "Each run takes a screenshot of the screen you work on. It doesn't run while a private window is in front.");
        var recording = Compact(new ComboBox
        {
            ItemsSource = new[] { "No recording", "The last seconds of the microphone", "The last seconds of what this PC plays" },
            SelectedIndex = (int)start.Recording, Width = 280
        });
        AutomationProperties.SetAutomationId(recording, "CheckInRecording-" + id);
        AutomationProperties.SetName(recording, "Recording with each run");
        var seconds = Compact(new ComboBox
        {
            ItemsSource = CheckIns.RecordingChoices.Select(s => $"{s} seconds").ToArray(),
            SelectedIndex = Math.Max(0, CheckIns.RecordingChoices.ToList().IndexOf(start.RecordingSeconds)), Width = 120
        });
        AutomationProperties.SetAutomationId(seconds, "CheckInSeconds-" + id);
        AutomationProperties.SetName(seconds, "How many seconds it records");
        seconds.ToolTip = CheckInLengthHelp;
        AutomationProperties.SetHelpText(seconds, CheckInLengthHelp);
        var script = new TextBox
        {
            Text = start.Script, AcceptsReturn = true, AcceptsTab = true, TextWrapping = TextWrapping.NoWrap,
            MaxLength = CheckIns.MaximumScriptCharacters, MinHeight = 40, MaxHeight = 160, FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            Margin = new Thickness(0, 4, 0, 0)
        };
        AutomationProperties.SetAutomationId(script, "CheckInScript-" + id);
        AutomationProperties.SetName(script, "A script it runs first");
        AutomationProperties.SetHelpText(script, "Windows PowerShell runs it hidden in your home folder before each run, " +
            $"for at most {CheckIns.ScriptTimeout.TotalSeconds:0} seconds. What it prints goes to the check-in.");
        void ShowNeeds()
        {
            // Disable before checking, so a forced tick isn't taken as the owner's choice.
            vision.IsEnabled = screenshot.IsChecked != true;
            vision.IsChecked = !vision.IsEnabled || ownVision;
            audio.IsEnabled = recording.SelectedIndex <= 0;
            audio.IsChecked = !audio.IsEnabled || ownAudio;
            seconds.IsEnabled = recording.SelectedIndex > 0;
        }
        ShowNeeds();
        script.TextChanged += (_, _) =>
        {
            tabEdited = true;
            autoSave.Changed();
        };
        void Chose(CheckBox box, bool on)
        {
            if (!box.IsEnabled) return;
            if (box == vision) ownVision = on;
            else ownAudio = on;
            SaveNow();
        }
        vision.Checked += (_, _) => Chose(vision, true);
        vision.Unchecked += (_, _) => Chose(vision, false);
        audio.Checked += (_, _) => Chose(audio, true);
        audio.Unchecked += (_, _) => Chose(audio, false);
        void Gathers()
        {
            ShowNeeds();
            SaveNow();
        }
        screenshot.Checked += (_, _) => Gathers();
        screenshot.Unchecked += (_, _) => Gathers();
        recording.SelectionChanged += (_, _) => Gathers();
        seconds.SelectionChanged += (_, _) => SaveNow();

        var view = new StackPanel();
        view.Children.Add(new TextBlock
        {
            Text = "It gets to know (point at each one to see what it gets)", Margin = new Thickness(0, 8, 0, 0),
            ToolTip = "It always gets the day and time. Each fact you tick adds a few lines of text to its question; " +
                "a fact Martlet doesn't have now (for example the screen, while Martlet doesn't watch it) says so instead."
        });
        view.Children.Add(facts);
        view.Children.Add(new TextBlock
        {
            Text = "It waits until (point at each one to see when)", Margin = new Thickness(0, 4, 0, 0),
            ToolTip = "Every check-in also waits for its time, while you talk and while nobody uses this PC. Check now skips the " +
                "waits for time and something new, but not the others."
        });
        view.Children.Add(conditions);
        view.Children.Add(new TextBlock { Text = "With each run it takes", Margin = new Thickness(0, 4, 0, 0) });
        var inputs = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        inputs.Children.Add(RowGroup(screenshot));
        inputs.Children.Add(RowGroup(recording));
        inputs.Children.Add(RowGroup(RowLabel("Length", seconds), seconds));
        view.Children.Add(inputs);
        view.Children.Add(new Label
        {
            Content = "A _script it runs first (Windows PowerShell, optional); the check-in reads what it prints", Target = script,
            Padding = new Thickness(0, 6, 0, 0)
        });
        view.Children.Add(script);
        view.Children.Add(Note("For example, the busiest programs: Get-Process | Sort-Object CPU -Descending | Select-Object -First 10 " +
            $"Name, CPU. It runs hidden in your home folder, as you, and stops after {CheckIns.ScriptTimeout.TotalSeconds:0} seconds.",
            new Thickness(0, 2, 0, 0)));
        view.Children.Add(new TextBlock
        {
            Text = "It may use these tools (point at each one to see what it does)", Margin = new Thickness(0, 8, 0, 0),
            ToolTip = $"The model can call these tools while it answers, at most {CheckIns.MaximumToolCalls} calls in " +
                $"{CheckIns.MaximumToolRounds} rounds. Only Thinking pool members on an endpoint, whose model calls tools, take a " +
                "check-in with tools. With Its tools act, the tools are what it does."
        });
        view.Children.Add(toolSets);
        view.Children.Add(new TextBlock { Text = "The model must handle", Margin = new Thickness(0, 8, 0, 0) });
        view.Children.Add(needs);
        view.Children.Add(Note("Only Thinking pool members that can do all of these take this check-in. A screenshot needs a model " +
            "that sees pictures, and a recording needs one that hears recordings. They go only to that member, which can be a " +
            "cloud service. The microphone and what this PC plays are kept in memory, only while a check-in that is on asks for " +
            "them and Martlet hears them. " + CheckInLengthHelp, new Thickness(0, 0, 0, 0)));
        return (outcome, view, () => new CheckInEdit(
            factBoxes.Where(b => b.Box.IsChecked == true).Aggregate(CheckInFacts.None, (all, b) => all | b.Fact),
            conditionBoxes.Where(b => b.Box.IsChecked == true).Aggregate(CheckInConditions.None, (all, b) => all | b.Condition),
            CheckInOutcomeChoices[Math.Max(0, outcome.SelectedIndex)].Outcome,
            ThinkingCapability.Text | (ownVision ? ThinkingCapability.Vision : ThinkingCapability.None) |
                (ownAudio ? ThinkingCapability.Audio : ThinkingCapability.None),
            screenshot.IsChecked == true, (CheckInRecording)Math.Max(0, recording.SelectedIndex),
            CheckIns.RecordingChoices[Math.Max(0, seconds.SelectedIndex)], script.Text.Replace("\r\n", "\n", StringComparison.Ordinal),
            [.. toolBoxes.Where(b => b.Box.IsChecked == true).Select(b => b.Set)]));
    }

    /// <summary>Copy as your own: a check-in of your own with <paramref name="id"/>'s prompt (as it is now, built-in or edited) and
    /// every choice, off until you turn it on.</summary>
    private async Task CopyCheckInAsync(string id, Func<Task> saveAll)
    {
        await saveAll();
        if (CheckIns.All(checkInSettings).FirstOrDefault(c => c.Id == id) is not { } current) return;
        if (checkInSettings.Custom.Count >= CheckIns.MaximumCustom || checkInSettings.NewId() is not { } newId)
        {
            ActionText.Text = $"{current.Name} not copied: you have {CheckIns.MaximumCustom} check-ins of your own, the most Martlet " +
                "keeps. Remove one first.";
            return;
        }
        var name = current.Name + " (copy)";
        var task = CheckIns.Template(current, homeSettings?.Prompts) ?? "";
        var copy = new CustomCheckIn
        {
            Id = newId, Name = name.Length > CheckIns.MaximumNameCharacters ? name[..CheckIns.MaximumNameCharacters] : name, On = false,
            EveryMinutes = current.EveryMinutes,
            Task = task.Length > CheckIns.MaximumTaskCharacters ? task[..CheckIns.MaximumTaskCharacters] : task,
            Facts = current.Facts, Conditions = current.Conditions, Outcome = current.Outcome,
            Needs = current.Custom ? checkInSettings.Custom.First(c => c.Id == id).Needs : checkInSettings.Choice(id)?.Needs ?? ThinkingCapability.Text,
            Screenshot = current.Screenshot, Recording = current.Recording, RecordingSeconds = current.RecordingSeconds,
            Script = current.Script ?? "", ToolSets = current.ToolSets
        };
        if (SaveCheckIns(checkInSettings.With(copy), $"Copied {current.Name} as {copy.Name}, a check-in of your own. It's off until you turn it on."))
            RenderTab();
    }

    /// <summary>The Check-ins page's prompt save: writes only the built-in check-in prompts typed on this page into the newest saved
    /// settings, so every other prompt (and an edit from Companion › Prompts) stays as saved. Returns false to be tried again
    /// shortly when another window saved the settings meanwhile. Check-ins use the saved text from their next run.</summary>
    private async Task<bool> SaveCheckInPromptsAsync(IReadOnlyDictionary<string, TextBox> boxes, HashSet<string> typed, Action saved)
    {
        if (typed.Count == 0) return true;
        if (store is null || setupService is null || closing) return true;
        var edits = typed.Where(boxes.ContainsKey).ToDictionary(id => id,
            id => boxes[id].Text.Replace("\r\n", "\n", StringComparison.Ordinal), StringComparer.Ordinal);
        ChangeTurns.Turn? turn = null;
        var token = lifetime.Token;
        try
        {
            turn = await ChangeTurnAsync();
            var loaded = await setupService.LoadAsync(token);
            if (loaded.Error is not null) throw new InvalidOperationException(loaded.Error.Summary);
            var all = new Dictionary<string, string>(StringComparer.Ordinal);
            if (loaded.Settings?.Prompts is { } current)
                foreach (var (id, text) in current.Overrides) all[id] = text;
            foreach (var (id, text) in edits) all[id] = text;
            var prompts = PromptSettings.Normalize(all);
            prompts?.Validate();
            var updated = SetupSettings.Begin(loaded.Settings) with { Prompts = prompts };
            updated.Validate();
            var result = await setupService.SaveAsync(updated, loaded.Revision, token);
            if (!result.Save.Saved)
            {
                if (result.Save.Error?.Code == ErrorCode.SettingsConflict) return false;
                throw new InvalidOperationException(result.Summary);
            }
            homeSettings = updated;
            // A prompt typed again while this saved stays to save next.
            foreach (var (id, text) in edits)
                if (boxes.TryGetValue(id, out var box) && box.Text.Replace("\r\n", "\n", StringComparison.Ordinal) == text) typed.Remove(id);
            saved();
            ActionText.Text = "Check-in prompts saved. Check-ins use them from their next run.";
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or ContractException or JsonException)
        {
            ActionText.Text = "Check-in prompts not saved: " + error.Message;
        }
        finally
        {
            turn?.Dispose();
            // The page stays as typed (tabEdited); only Home and the other summaries refresh.
            if (!closing) RenderHome();
        }
        return true;
    }

    /// <summary>Your own check-ins: a row for each and Add a check-in. Their edits save on their own (one save for all of them, at
    /// once for a switch or a choice and after a short pause for typing).</summary>
    private Border OwnCheckInsCard(List<Action> lines, List<Func<CustomCheckIn>> rows, AutoSave autoSave, Func<Task> saveAll)
    {
        var stack = new List<UIElement>
        {
            Heading("Your own check-ins"),
            Note("Write what the Thinking pool should check, such as \"If the user has been at it for hours, suggest a short break\" or " +
                "\"If it's late at night, remind Martlet to talk more softly\". Choose what it gets to know (it always gets the day " +
                "and time), when it waits, how often it runs and what happens with its answer: a reminder in Martlet's next reply, " +
                "Martlet brings it up on its own as soon as it's free, it turns off lingering emotes, it takes the eyes back to " +
                "their usual, or its tools act. A check-in can also take a screenshot, the last seconds of the microphone or of what this PC plays, " +
                "and what a script of yours prints, and you choose the tool sets it may call and what its model must handle. Copy as your own on a built-in " +
                "check-in starts from that one. A new check-in starts off.", new Thickness(0, 0, 0, 4))
        };
        foreach (var custom in checkInSettings.Custom) stack.Add(OwnCheckInRow(custom, lines, rows, autoSave, saveAll));
        var add = PageButton("Add a check-in", AddOwnCheckIn, id: "CheckInAdd");
        add.IsEnabled = checkInSettings.Custom.Count < CheckIns.MaximumCustom;
        AutomationProperties.SetHelpText(add, $"Adds a check-in of your own, off until you turn it on (at most {CheckIns.MaximumCustom}).");
        stack.Add(Row(add));
        return Card([.. stack]);
    }

    private void AddOwnCheckIn()
    {
        if (tabAutoSave is { Pending: true } pending) pending.SaveNowAsync().Forget();
        if (checkInSettings.Custom.Count >= CheckIns.MaximumCustom || checkInSettings.NewId() is not { } id) return;
        var custom = new CustomCheckIn
        {
            Id = id, Name = "My check-in", On = false, EveryMinutes = 30, Task = "",
            Facts = CheckInFacts.Conversation | CheckInFacts.Persona, Outcome = CheckInOutcome.Note
        };
        if (SaveCheckIns(checkInSettings.With(custom), "Added a check-in. Write what it checks, then turn it on.")) RenderTab();
    }

    private StackPanel OwnCheckInRow(CustomCheckIn custom, List<Action> lines, List<Func<CustomCheckIn>> rows, AutoSave autoSave,
        Func<Task> saveAll)
    {
        var id = custom.Id;
        var view = new StackPanel { Margin = new Thickness(0, 14, 0, 0) };
        var name = Compact(new TextBox { Text = custom.Name, MaxLength = CheckIns.MaximumNameCharacters, Width = 220 });
        AutomationProperties.SetAutomationId(name, "CheckInName-" + id);
        AutomationProperties.SetName(name, "Check-in name");
        var on = new CheckBox { Content = "On", IsChecked = custom.On, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetAutomationId(on, "CheckInOn-" + id);
        AutomationProperties.SetName(on, "Check-in on");
        var every = EveryChoice(custom.EveryMinutes);
        AutomationProperties.SetAutomationId(every, "CheckInEvery-" + id);
        AutomationProperties.SetName(every, "How often it runs");
        var (outcome, choices, read) = CheckInEditor(id, custom.Name, new CheckInEdit(custom.Facts, custom.Conditions, custom.Outcome,
            custom.Needs, custom.Screenshot, custom.Recording, custom.RecordingSeconds, custom.Script, custom.ToolSets), autoSave);
        var task = new TextBox
        {
            Text = custom.Task, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MaxLength = CheckIns.MaximumTaskCharacters,
            MinHeight = 56, MaxHeight = 260, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 4, 0, 0)
        };
        AutomationProperties.SetAutomationId(task, "CheckInTask-" + id);
        AutomationProperties.SetName(task, "What it checks");
        AutomationProperties.SetHelpText(task, CheckInPlaceholderHelp);
        var status = Note("", new Thickness(0, 4, 0, 0));
        AutomationProperties.SetAutomationId(status, "CheckInStatus-" + id);
        lines.Add(() => status.Text = CheckInLine(id));
        rows.Add(() =>
        {
            var edit = read();
            return new CustomCheckIn
            {
                Id = id, Name = name.Text.Trim() is { Length: > 0 } named ? named : "My check-in", On = on.IsChecked == true,
                EveryMinutes = CheckIns.EveryChoices[Math.Max(0, every.SelectedIndex)],
                Task = task.Text.Replace("\r\n", "\n", StringComparison.Ordinal), Facts = edit.Facts, Conditions = edit.Conditions,
                Outcome = edit.Outcome, Needs = edit.Needs, Screenshot = edit.Screenshot, Recording = edit.Recording,
                RecordingSeconds = edit.RecordingSeconds, Script = edit.Script, ToolSets = edit.ToolSets
            };
        });
        void Typed()
        {
            tabEdited = true;
            autoSave.Changed();
        }
        name.TextChanged += (_, _) => Typed();
        task.TextChanged += (_, _) => Typed();
        on.Checked += (_, _) => autoSave.SaveNowAsync().Forget();
        on.Unchecked += (_, _) => autoSave.SaveNowAsync().Forget();
        every.SelectionChanged += (_, _) => autoSave.SaveNowAsync().Forget();
        var top = new WrapPanel();
        top.Children.Add(RowGroup(name));
        top.Children.Add(RowGroup(on));
        top.Children.Add(RowGroup(RowLabel("Every", every), every));
        top.Children.Add(RowGroup(RowLabel("Its answer", outcome), outcome));
        view.Children.Add(top);
        view.Children.Add(new Label { Content = "_What it checks", Target = task, Padding = new Thickness(0, 6, 0, 0) });
        view.Children.Add(task);
        view.Children.Add(Note(CheckInPlaceholderHelp, new Thickness(0, 2, 0, 0)));
        view.Children.Add(choices);
        view.Children.Add(status);
        async Task SaveThenCheckAsync()
        {
            await autoSave.SaveNowAsync();
            await CheckInNowAsync(id);
        }
        view.Children.Add(Row(PageButton("Check now", () => SaveThenCheckAsync().Forget(), id: "CheckInRun-" + id),
            PageButton("Copy as your own", () => CopyCheckInAsync(id, saveAll).Forget(), id: "CheckInCopy-" + id),
            PageButton("Remove", () =>
            {
                // The other rows' edits save first; the save is synchronous, so nothing brings the removed row back.
                autoSave.SaveNowAsync().Forget();
                if (!SaveCheckIns(checkInSettings.Without(id), "Removed the check-in.")) return;
                // A new check-in may get its ID again; it starts with no runs.
                checkInRuns.Remove(id);
                checkInCounts.Remove(id);
                checkInWaits.Remove(id);
                RenderTab();
            }, id: "CheckInRemove-" + id)));
        return view;
    }

    private static ComboBox EveryChoice(int minutes)
    {
        var index = 0;
        for (var i = 0; i < CheckIns.EveryChoices.Count; i++)
            if (CheckIns.EveryChoices[i] == minutes) index = i;
        return Compact(new ComboBox { ItemsSource = CheckIns.EveryChoices.Select(EveryText).ToArray(), SelectedIndex = index, Width = 130 });
    }

    private static string EveryText(int minutes) => minutes == 1 ? "1 minute" : minutes < 60 ? $"{minutes} minutes"
        : minutes == 60 ? "1 hour" : $"{minutes / 60} hours";

    // What models hear of a long recording (docs/CONVERSATION.md#check-ins).
    private const string CheckInLengthHelp = "Gemma 4 and Gemma 3n hear only the first 30 seconds of a recording. " +
        "Gemini, OpenAI's audio models and Voxtral hear a whole minute.";

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
        KeepCheckInSound(CheckIns.All(next));
        if (said is not null) ActionText.Text = said;
        ShowCheckInsNow();
        WriteCheckInStatus();
        return true;
    }

    // ---------- check-ins-status.json ----------

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
            pool = new { canRun = member is not null, member = member?.Name, model = member?.Model, fixture = CheckInFixture() is not null },
            checkIns = CheckIns.All(checkInSettings).Select(c =>
            {
                var last = checkInRuns.GetValueOrDefault(c.Id);
                var counts = checkInCounts.GetValueOrDefault(c.Id);
                return new
                {
                    id = c.Id, name = c.Name, custom = c.Custom, on = c.On, everyMinutes = c.EveryMinutes, outcome = c.Outcome.ToString(),
                    facts = c.Facts.ToString(), conditions = c.Conditions.ToString(), prompt = c.PromptId,
                    changed = !c.Custom && checkInSettings.Choice(c.Id) is { } choice && choice != new CheckInChoice(choice.On, choice.EveryMinutes),
                    needs = c.Needs.ToString(), canRun = member is not null && conversation!.ThinkingPool.CanRun(ThinkingJobKind.CheckIn, c.Needs),
                    screenshot = c.Screenshot, recording = c.Recording.ToString(),
                    recordingSeconds = c.Recording == CheckInRecording.None ? (int?)null : c.RecordingSeconds, script = c.RunsScript,
                    toolSets = c.ToolSets,
                    waiting = checkInWaits.GetValueOrDefault(c.Id) is { Length: > 0 } wait ? wait : null,
                    nextAt = last is null ? (DateTimeOffset?)null : last.At + CheckIns.Pace(c, last, checkInExchanged),
                    runs = counts.Runs, acted = counts.Acted,
                    last = last is null ? null : new
                    {
                        at = last.At, result = last.Result, acted = last.Acted, member = last.Member, gathered = last.Gathered,
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
            }
        }, new JsonSerializerOptions { WriteIndented = true });
    }
}
