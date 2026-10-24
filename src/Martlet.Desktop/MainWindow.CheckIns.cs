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
    /// checks of the desktop (docs/mcp/apps.md#check-ins). Read before each run, so a check can change it.</summary>
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
    // The check-in the user's turn started that runs now (the director), in a lane of its own beside the paced and the other
    // triggered check-ins, so it never waits behind them; and the user's words it reads (never logged or saved).
    private string? checkInTurnRunning;
    // The user's turns the director hasn't read yet, oldest first (Director.Wait): a turn made while it runs waits for its next
    // run, never replaced by a newer one.
    private readonly List<string> checkInAsks = [];
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
    private (CheckIn? Next, CheckInState? State) LookAtCheckIns(string? now = null, bool triggered = false, bool turn = false)
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
            // An exchange (or a turn) that waited too long for its check-in is a promise Martlet didn't keep.
            if ((old.Fired & (CheckInTriggers.ExchangeEnded | CheckInTriggers.UserTurn)) != 0 && all.FirstOrDefault(c => c.Id == id) is { } waited &&
                CheckIns.ActsAfterExchanges(waited))
                ReportAfterExchange(waited, $"it waited {Reminders.Span(CheckIns.ExchangeAge)}" +
                    (checkInWaits.GetValueOrDefault(id) is { Length: > 0 } why ? ": " + why : ""));
        }
        var fixture = CheckInFixture() is not null;
        CheckIn? next = null;
        foreach (var checkIn in all)
        {
            var wait = CheckInSchedule.Wait(checkIn, state, checkInRuns.GetValueOrDefault(checkIn.Id), checkIn.Id == now, homeSettings?.Prompts,
                checkInFired.GetValueOrDefault(checkIn.Id));
            if (wait is null && !fixture && !conversation.ThinkingPool.CanRun(checkIn.JobKind, checkIn.Needs, checkIn.RunsOn))
                wait = CheckInNoMember(checkIn);
            checkInWaits[checkIn.Id] = wait ?? "";
            if (wait is null && next is null && (now is null ? CheckInLane(checkIn) == (turn ? 2 : triggered ? 1 : 0) : checkIn.Id == now) &&
                checkIn.Id != checkInRunning && checkIn.Id != checkInTriggeredRunning && checkIn.Id != checkInTurnRunning)
                next = checkIn;
        }
        return (next, state);
    }

    // Which lane a check-in runs in: 0 on its pace, 1 started by a trigger, 2 started by the user's turn (the director).
    private static int CheckInLane(CheckIn checkIn) =>
        checkIn.Triggers.HasFlag(CheckInTriggers.UserTurn) ? 2 : checkIn.Triggers != CheckInTriggers.None ? 1 : 0;

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
}
