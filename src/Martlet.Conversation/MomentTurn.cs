namespace Martlet.Conversation;

/// <summary>What made Martlet start a reply: the user's own words (typed or heard), what this PC played (its pace came up),
/// finished background work it brings up on its own, a look at what vision watches, or the user touching the desktop character
/// with nothing said (<see cref="TouchDebounce"/>).</summary>
public enum MomentTrigger { User, PcAudio, Report, Look, Touch }

/// <summary>How the talk window starts a moment: a reply to a message (the user's words and/or what this PC played, with the
/// finished work in its notes), Martlet's own report of finished work (its note is the message), a plain glance, or a short
/// reaction to being touched (the touches are the message).</summary>
public enum MomentRoute { Reply, Report, Glance, Touch }

/// <summary>One moment Martlet answers in one reply: what started it and everything else waiting that it takes along.
/// <paramref name="User"/>: the user's words; <paramref name="PcAudio"/>: the lines this PC played that wait;
/// <paramref name="Jobs"/>: finished background work; <paramref name="Look"/>: the look vision wanted (its picture and anything
/// that wants the user's attention), counted as a look.</summary>
public sealed record MomentPlan(MomentTrigger Trigger, bool User, bool PcAudio, bool Jobs, bool Look)
{
    /// <summary>A reply when there are words (the user's or the PC's), Martlet's report when only finished work waits besides a
    /// look, and a plain glance only when the look is all there is.</summary>
    public MomentRoute Route => User || PcAudio ? MomentRoute.Reply : Jobs ? MomentRoute.Report :
        Trigger == MomentTrigger.Touch ? MomentRoute.Touch : MomentRoute.Glance;

    /// <summary>Takes more than what started it.</summary>
    public bool Combined => (User ? 1 : 0) + (PcAudio ? 1 : 0) + (Jobs ? 1 : 0) + (Look ? 1 : 0) > 1;
}

/// <summary>The four things Martlet hears and sees (the user, what this PC plays, what vision watches and background work that
/// finished) are one conversation: whatever starts a reply takes everything else that waits with it, so Martlet answers them
/// together in one reply instead of one after another. The user always comes first and is never kept waiting: a reply to them
/// only takes what is already there.</summary>
public static class MomentTurn
{
    /// <summary>What a reply started by <paramref name="trigger"/> takes along. <paramref name="pcWaiting"/>: lines this PC played
    /// wait (not necessarily due on their own); <paramref name="jobsWaiting"/>: finished work Martlet may bring up on its own
    /// right now (Thinking longer shares results as soon as it is free, nothing the user stopped, no song playing);
    /// <paramref name="lookDue"/>: the pacer wants a look and there is a picture. A reply to the user always takes finished work
    /// (it waits for the user's next message too).</summary>
    public static MomentPlan Plan(MomentTrigger trigger, bool pcWaiting, bool jobsWaiting, bool lookDue) => trigger switch
    {
        MomentTrigger.User => new(trigger, User: true, PcAudio: pcWaiting, Jobs: true, Look: lookDue),
        MomentTrigger.PcAudio => new(trigger, User: false, PcAudio: true, Jobs: jobsWaiting, Look: lookDue),
        MomentTrigger.Report => new(trigger, User: false, PcAudio: pcWaiting, Jobs: true, Look: lookDue),
        // Touches on their own take nothing else: the reaction stays short.
        MomentTrigger.Touch => new(trigger, User: false, PcAudio: false, Jobs: false, Look: false),
        _ => new(trigger, User: false, PcAudio: pcWaiting, Jobs: jobsWaiting, Look: true)
    };

    /// <summary>What one reply took, for the talk window and the desktop log (never what was said, seen or found): "your words,
    /// 2 lines this PC played, the picture (a notification), 1 finished job and 2 context notes". <paramref name="contextNotes"/>
    /// counts the context board's notes the request carried (<see cref="ContextBoard"/>), and <paramref name="said"/> the things
    /// Martlet said lately that went in its notes (<see cref="SaidLately"/>). <paramref name="described"/>: in place of the
    /// picture, the reply took the image model's description of it (docs/SENSE_MODELS.md).</summary>
    public static string Describe(bool user, int pcLines, bool picture, string? attention, int jobs, bool report = false, int touches = 0,
        int contextNotes = 0, int said = 0, bool described = false)
    {
        var parts = new List<string>();
        if (user) parts.Add("your words");
        if (touches > 0) parts.Add(touches == 1 ? "1 touch" : $"{touches} touches");
        if (pcLines > 0) parts.Add(pcLines == 1 ? "1 line this PC played" : $"{pcLines} lines this PC played");
        if (picture) parts.Add(attention is { Length: > 0 } about ? $"the picture ({about})" : "the picture");
        else if (described)
            parts.Add(attention is { Length: > 0 } about ? $"the image model's description of the picture ({about})"
                : "the image model's description of the picture");
        else if (attention is { Length: > 0 } noticed) parts.Add(noticed);
        if (jobs > 0) parts.Add(jobs == 1 ? "1 finished job" : $"{jobs} finished jobs");
        else if (report) parts.Add("finished work");
        if (contextNotes > 0) parts.Add(contextNotes == 1 ? "1 context note" : $"{contextNotes} context notes");
        if (said > 0) parts.Add(said == 1 ? "1 thing Martlet said lately" : $"{said} things Martlet said lately");
        return parts.Count switch
        {
            0 => "nothing",
            1 => parts[0],
            _ => string.Join(", ", parts.Take(parts.Count - 1)) + " and " + parts[^1]
        };
    }
}
