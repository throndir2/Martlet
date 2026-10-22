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
            Aside = checkInAside is { } holding && holding.HoldsAt(now) ? holding : null,
            // What the user just said, for the director (private: it goes only to a pool member with the check), and the skill
            // index it may load from.
            Asked = checkInAsked,
            Skills = Director.SkillIndex(homeSettings?.Skills)
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
        if (checkInRunning is not null || checkInTriggeredRunning == id || checkInTurnRunning == id)
        {
            ActionText.Text = "Another check-in runs now, so try again in a moment.";
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
            runningTurn = checkInTurnRunning,
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
                    needs = c.Needs.ToString(), jobKind = ThinkingJobKinds.Name(c.JobKind),
                    canRun = member is not null && conversation!.ThinkingPool.CanRun(c.JobKind, c.Needs, c.RunsOn),
                    // Its own Runs on (null: the check-in kind's, on Companion › Thinking pool) and the member it would go to first.
                    runsOn = c.RunsOn is { } where ? new { mode = where.Name, members = where.Members } : null,
                    firstMember = member is null ? null : conversation!.ThinkingPool.Find(c.JobKind, c.Needs, c.RunsOn)?.Name,
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
                    nextAt = last is null ? (DateTimeOffset?)null : last.At + CheckInSchedule.Pace(c, last, checkInExchanged),
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
