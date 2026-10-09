using System.Windows.Threading;
using Martlet.Audio;
using Martlet.Conversation;
using Martlet.Core.Contracts;

namespace Martlet.Desktop;

/// <summary>The signals check-ins wait for and know about (docs/CONVERSATION.md#check-ins), followed on the UI thread at each look:
/// someone came back after <see cref="CheckIns.Idle"/> away and what happened meanwhile, what the user does (a game, a call, full
/// screen; while Martlet hears what this PC plays) and when that changed, a taskbar button that flashed or a notification
/// (watched only while a check-in that is on waits for it), a song that played to its end, and the voices heard lately. None of
/// it is saved or logged.</summary>
public partial class MainWindow
{
    // The start of the newest time nobody used this PC for CheckIns.Idle or more, and when someone last came back after one.
    private DateTimeOffset? checkInAwayFrom;
    private (DateTimeOffset At, TimeSpan Away)? checkInCameBack;
    // What the user does: the kinds seen last (a game, a call, full screen), its words, and when the kinds last changed from what.
    private string? checkInActivityKinds, checkInActivity;
    private (DateTimeOffset At, string Before)? checkInActivityChanged;
    // Taskbar buttons that flash and pop-up notifications, watched once a second while a check-in that is on waits for them.
    private IScreenAttention? checkInAttention;
    private readonly DispatcherTimer checkInAttentionTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private DateTimeOffset? checkInAttentionAt;

    /// <summary>Follows the signals at one look (on the UI thread).</summary>
    private void TrackCheckInSignals(IReadOnlyList<CheckIn> all, DateTimeOffset now)
    {
        var idle = TimeSpan.FromSeconds(ReminderIdleSeconds());
        if (idle >= CheckIns.Idle) checkInAwayFrom ??= now - idle;
        else if (checkInAwayFrom is { } from)
        {
            var back = now - idle;
            checkInCameBack = (back, back - from);
            checkInAwayFrom = null;
        }

        if (conversation?.PcActivity is { On: true } monitor)
        {
            var state = monitor.Now;
            var kinds = ActivityKinds(state);
            // The first look only learns what the user does; a change is a game, a call or full screen starting or ending.
            if (checkInActivityKinds is { } before && before != kinds) checkInActivityChanged = (now, checkInActivity ?? "");
            checkInActivityKinds = kinds;
            checkInActivity = state.Summary ?? "";
        }
        else checkInActivityKinds = checkInActivity = null;

        WatchCheckInAttention(Role == DeviceRole.Companion && all.Any(c => c.On && c.Conditions.HasFlag(CheckInConditions.Attention)));
    }

    // "game,call,full": the kinds of activity a change is about.
    private static string ActivityKinds(PcActivityState state) => string.Join(",", new[]
    {
        state.Activities.Any(a => a.Source.Kind == PcActivityKind.Game) ? "game" : null,
        state.Activities.Any(a => a.Source.People) ? "call" : null,
        state.Activities.Any(a => a.FullScreen) ? "full" : null
    }.OfType<string>());

    private bool OnCallNow() => conversation?.PcActivity is { On: true } monitor && monitor.Now.Activities.Any(a => a.Source.People);

    private void WatchCheckInAttention(bool wanted)
    {
        if (wanted == checkInAttentionTimer.IsEnabled) return;
        if (wanted)
        {
            checkInAttention ??= new ScreenAttention();
            checkInAttention.Start();
            checkInAttentionTimer.Tick -= CheckInAttentionTick;
            checkInAttentionTimer.Tick += CheckInAttentionTick;
            checkInAttentionTimer.Start();
        }
        else
        {
            checkInAttentionTimer.Stop();
            checkInAttention?.Stop();
        }
    }

    private void CheckInAttentionTick(object? sender, EventArgs e)
    {
        if (closing)
        {
            WatchCheckInAttention(false);
            return;
        }
        if (checkInAttention?.Check() is not null) checkInAttentionAt = DateTimeOffset.Now;
    }

    /// <summary>The <paramref name="state"/> with the signals followed so far, and what happened while the user was away.</summary>
    private CheckInState WithCheckInSignals(CheckInState state)
    {
        var now = state.Now;
        var (voices, ownerKnown) = conversation?.RecentVoices(now) ?? ([], false);
        return state with
        {
            CameBackAt = checkInCameBack?.At, WasAway = checkInCameBack?.Away, WhileAway = CheckInWhileAway(now),
            Activity = checkInActivity, OnCall = OnCallNow(), ActivityChangedAt = checkInActivityChanged?.At,
            ActivityBefore = checkInActivityChanged?.Before, AttentionAt = checkInAttentionAt, SongEndedAt = conversation?.Singing?.EndedAt,
            Voices = voices, OwnerVoiceKnown = ownerKnown
        };
    }

    // What happened while the user was away, before they last came back: what Martlet said, the background work that finished
    // and the reminders that came due.
    private IReadOnlyList<string> CheckInWhileAway(DateTimeOffset now)
    {
        if (checkInCameBack is not { } back) return [];
        var from = back.At - back.Away;
        bool During(DateTimeOffset at) => at >= from && at <= back.At;
        var lines = new List<string>();
        if (conversation is { } talk)
        {
            foreach (var saying in talk.RecentSayings(now).Where(s => During(s.At)).TakeLast(4))
                lines.Add($"Martlet said at {SaidLately.Clock(saying.At)}: \"{saying.Text}\"");
            foreach (var job in talk.Jobs.Active.Concat(talk.Jobs.Recent).Where(j => j.FinishedUtc is { } done && During(done)).Take(4))
                lines.Add($"{LiveConversationWindow.KindTitle(job.Kind)} {(job.State == BackgroundJobState.Succeeded ? "finished" : "ended")}: {job.Label}");
        }
        foreach (var due in RemindersNow().Pending.Where(p => During(p.Reminder.Due)).Take(4))
            lines.Add($"A reminder came due at {SaidLately.Clock(due.Reminder.Due.ToLocalTime())}: {due.Reminder.Text}");
        return lines;
    }
}
