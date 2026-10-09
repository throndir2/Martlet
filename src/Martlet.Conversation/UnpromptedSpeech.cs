namespace Martlet.Conversation;

/// <summary>What Martlet says on its own, without the user asking (docs/CONVERSATION.md, What Martlet says on its own):
/// <see cref="Reminder"/> (a due reminder the user set), <see cref="FinishedWork"/> (background work the user asked for),
/// <see cref="CheckIn"/> (a check-in's notice or another notice Martlet decided on itself) and <see cref="Remark"/> (a
/// screen or camera remark). Replies to the user, to what this PC played and to a touch are never unprompted speech.</summary>
public enum UnpromptedKind { Reminder, FinishedWork, CheckIn, Remark }

/// <summary>What a remark Martlet started on its own does when the user talked over it and the judge said the words were not
/// for Martlet: play on from where it paused, or drop the rest.</summary>
public enum UnpromptedAfterTalkOver { Resume, Drop }

/// <summary>What a notice that waits does when it is checked again just before Martlet says it.</summary>
public enum NoticeAction { Say, Wait, Drop }

/// <summary>Why a notice waits or was dropped: a short code (<c>too_old</c>, <c>moved_on</c>, <c>mid_utterance</c>, <c>live</c> or
/// <c>free</c>) and words and numbers only (never the notice's text).</summary>
public sealed record NoticeCheck(NoticeAction Action, string Code, string Why);

/// <summary>Speak, wait or drop for what Martlet says on its own. Only local rules; no model call and no wait on the speaking path,
/// and replies to the user never pass through here.</summary>
public static class UnpromptedSpeech
{
    /// <summary>A finished-work report or a check-in the user talked over (with words not for Martlet) plays on only when the pause
    /// was at most this long; after a longer pause the rest is dropped and what it carried waits for the user's next message.</summary>
    public static TimeSpan ResumeWithin { get; } = TimeSpan.FromMilliseconds(1500);

    /// <summary>How long a check-in's notice may wait to be said. A check-in is Martlet's own idea about the moment it was made.</summary>
    public static TimeSpan CheckInMaxWait { get; } = TimeSpan.FromMinutes(10);

    /// <summary>The kind of what a report brings up: a reminder when one is due among its jobs, else finished work when it
    /// carries any, else a check-in (every other notice).</summary>
    public static UnpromptedKind Of(IEnumerable<BackgroundJobKind> kinds)
    {
        ArgumentNullException.ThrowIfNull(kinds);
        var all = kinds.Select(Of).ToArray();
        return all.Contains(UnpromptedKind.Reminder) ? UnpromptedKind.Reminder
            : all.Contains(UnpromptedKind.FinishedWork) || all.Length == 0 ? UnpromptedKind.FinishedWork : UnpromptedKind.CheckIn;
    }

    /// <summary>The kind of one background job kind: a due reminder, finished work (not a notice) or a check-in (every other
    /// notice, so a new kind of notice Martlet decides on itself is a check-in until it says otherwise).</summary>
    public static UnpromptedKind Of(BackgroundJobKind kind)
    {
        ArgumentNullException.ThrowIfNull(kind);
        return kind.Name == Reminders.KindName ? UnpromptedKind.Reminder : kind.Notice ? UnpromptedKind.CheckIn : UnpromptedKind.FinishedWork;
    }

    /// <summary>Whether words said over a remark of <paramref name="kind"/> pause it for the judge (Pause and decide). A screen or
    /// camera remark is never paused: whatever the verdict, it is dropped, so it stops at once and no judge runs.</summary>
    public static bool Judged(UnpromptedKind kind) => kind != UnpromptedKind.Remark;

    /// <summary>What a remark of <paramref name="kind"/>, paused for <paramref name="paused"/>, does when the words said over it
    /// were not for Martlet: a reminder plays on (the user asked for it); a finished-work report or a check-in plays on only after a
    /// short pause (<see cref="ResumeWithin"/>), else it is dropped; a screen or camera remark is always dropped.</summary>
    public static UnpromptedAfterTalkOver AfterNotForMe(UnpromptedKind kind, TimeSpan paused) => kind switch
    {
        UnpromptedKind.Reminder => UnpromptedAfterTalkOver.Resume,
        UnpromptedKind.Remark => UnpromptedAfterTalkOver.Drop,
        _ => paused <= ResumeWithin ? UnpromptedAfterTalkOver.Resume : UnpromptedAfterTalkOver.Drop
    };

    /// <summary>How long a notice of <paramref name="kind"/> may wait to be said, or null for no limit (a due reminder and finished
    /// work are what the user asked for, so they are never dropped for their age).</summary>
    public static TimeSpan? MaxWait(UnpromptedKind kind) => kind == UnpromptedKind.CheckIn ? CheckInMaxWait : null;

    /// <summary>Checks a waiting job again just before Martlet says it (on its own, or in the notes of the user's next message).
    /// A check-in is dropped when it waited longer than <see cref="CheckInMaxWait"/>, or when the conversation moved on: an exchange
    /// that didn't carry it was kept after it became due (<paramref name="lastExchange"/>), so it was made before that exchange.
    /// Otherwise any job waits while the user is mid-utterance or the live floor is Live, and is said once neither holds.</summary>
    public static NoticeCheck Recheck(BackgroundJob job, DateTimeOffset now, DateTimeOffset? lastExchange, bool midUtterance = false,
        LiveFloorLevel floor = LiveFloorLevel.Idle)
    {
        ArgumentNullException.ThrowIfNull(job);
        var kind = Of(job.Kind);
        if (kind == UnpromptedKind.CheckIn && job.FinishedUtc is { } due)
        {
            var waited = now - due;
            if (MaxWait(kind) is { } most && waited > most)
                return new(NoticeAction.Drop, "too_old", $"too old: it waited {BackgroundJobs.Duration(waited)}, and a check-in waits at most " +
                    BackgroundJobs.Duration(most));
            if (lastExchange is { } exchange && exchange > due)
                return new(NoticeAction.Drop, "moved_on", "the conversation moved on: an exchange was kept after it became due");
        }
        if (midUtterance) return new(NoticeAction.Wait, "mid_utterance", "you are mid-utterance");
        if (floor == LiveFloorLevel.Live) return new(NoticeAction.Wait, "live", "the live floor is Live");
        return new(NoticeAction.Say, "free", "nothing holds it");
    }

    /// <summary>Drops every waiting job of <paramref name="jobs"/> that <see cref="Recheck"/> drops now, and returns each with why.
    /// Cheap (a few waiting jobs, one lock), so it may run just before a reply takes what waits.</summary>
    public static IReadOnlyList<(BackgroundJob Job, NoticeCheck Check)> DropStale(BackgroundJobs jobs, DateTimeOffset now, DateTimeOffset? lastExchange)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        var why = new Dictionary<BackgroundJob, NoticeCheck>();
        var dropped = jobs.Drop(job =>
        {
            var check = Recheck(job, now, lastExchange);
            if (check.Action != NoticeAction.Drop) return false;
            why[job] = check;
            return true;
        });
        return [.. dropped.Select(job => (job, why[job]))];
    }

    /// <summary>The kind in a few words for logs and status ("a check-in").</summary>
    public static string Describe(UnpromptedKind kind) => kind switch
    {
        UnpromptedKind.Reminder => "a reminder",
        UnpromptedKind.FinishedWork => "finished work",
        UnpromptedKind.CheckIn => "a check-in",
        _ => "a screen or camera remark"
    };
}
