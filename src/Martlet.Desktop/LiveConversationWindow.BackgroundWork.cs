using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Shapes;
using System.Windows.Threading;
using Martlet.Audio;
using Martlet.Conversation;
using Martlet.Core.Reading;
using Martlet.Core.Settings;
using Martlet.Diagnostics;
using Martlet.Providers;

namespace Martlet.Desktop;

// Background work (think_longer, reminders, greetings) the window brings up.
public partial class LiveConversationWindow
{
    // ---------- background work (think_longer) ----------

    /// <summary>How long Martlet waits after the last thing said or answered before it brings up finished background work.</summary>
    internal static TimeSpan ReportQuiet => TimeSpan.FromSeconds(2);
    // When the conversation last did something (you spoke or sent, or a turn finished).
    private long activityAt;
    // Esc stopped a report: what it carried waits for your next message instead of coming straight back.
    private bool reportHeld;
    // The LiveTurnInputs line and the reply or look it describes.
    private string? turnInputs;
    private LiveConversationOperation? inputsFrom;

    /// <summary>You are talking, or something you said or typed is about to be answered.</summary>
    private bool UserBusy => MicBusy || heardQueue.Count > 0 || pendingText is not null || mouseHeld || keyHeld || Recording || pcHeld.Count > 0 ||
        remoteQueue.Count > 0;

    /// <summary>How long since you last talked with Martlet here (spoke, typed or a turn finished), or null before anything.</summary>
    internal TimeSpan? SinceActivity => UserBusy ? TimeSpan.Zero : activityAt == 0 ? null : clock.GetElapsedTime(activityAt);

    /// <summary>A due reminder for this conversation: it starts (hidden) when it hasn't yet, and Martlet brings the reminder up on
    /// its own as soon as it is free, or with what you say next.</summary>
    internal BackgroundJob? Remind(string label, string text)
    {
        if (closed) return null;
        if (!begun) StartInBackground();
        var job = controller.Remind(label, text);
        if (job is not null) ErrorLog.Info($"Reminders: {job.Id} is due; Martlet brings it up as soon as it's free.");
        RenderActions();
        return job;
    }

    /// <summary>What a check-in said to bring up, for this conversation: it starts (hidden) when it hasn't yet, and Martlet
    /// brings it up on its own as soon as it is free, or with what you say next.</summary>
    internal BackgroundJob? BringUp(string label, string text)
    {
        if (closed) return null;
        if (!begun) StartInBackground();
        var job = controller.BringUp(label, text);
        if (job is not null) ErrorLog.Info($"Check-ins: {job.Id} waits; Martlet brings it up as soon as it's free.");
        RenderActions();
        return job;
    }

    /// <summary>The conversation is ready to answer at full speed: its settings loaded, Thinking is set up and, when Thinking runs
    /// in Ollama on this PC, the model has loaded (Home adds Parakeet and the character).</summary>
    internal bool WarmedUp => !closed && Available && Warmup.Count == 0 && warmup?.Status.State is null or LocalModelState.Ready;

    /// <summary>Nothing was said in this conversation yet.</summary>
    internal bool Fresh => controller.RecentExchanges(0).Total == 0;

    /// <summary>Martlet's greeting after it starts (<paramref name="facts"/>: what it knows, <see cref="Greetings.Facts"/>): Martlet
    /// says it on its own as soon as it is free, or with what you say first.</summary>
    internal BackgroundJob? Greet(string facts)
    {
        if (closed) return null;
        var job = controller.Greet(facts);
        RenderActions();
        return job;
    }

    /// <summary>Whether the reply that carried <paramref name="job"/> stayed quiet ([pass]).</summary>
    internal bool StayedQuiet(BackgroundJob job) => controller.StayedQuiet(job);

    /// <summary>Brings up finished background work on Martlet's own, as soon as it is free: Thinking longer shares results as
    /// soon as Martlet is free (the default), something finished that the user didn't stop, nobody is talking or about to be
    /// answered, no reply, look or other work owns Martlet, Martlet isn't paused or singing, and the conversation has been quiet
    /// for <see cref="ReportQuiet"/>. It takes what else waits along (one moment): the lines this PC played (then it is a reply
    /// to them with the finished work in its notes), the newest picture and a look that is due.</summary>
    private bool TryReport()
    {
        // Just before Martlet says what waits on its own: a check-in that is too old, or that the conversation moved on from, is
        // dropped (UnpromptedSpeech; local rules only). What is left waits while you talk or the live floor is Live.
        if (!closed && Available) controller.DropStaleNotices();
        if (closed || !Available || !JobsWaiting || UserBusy || operations.IsRunning || owned is { OwnershipReleased: false } ||
            commentary is { OwnershipReleased: false } || activityAt != 0 && clock.GetElapsedTime(activityAt) < ReportQuiet ||
            controller.LiveFloor.Level == LiveFloorLevel.Live)
            return false;
        return StartMoment(MomentTurn.Plan(MomentTrigger.Report, PcWaiting, jobsWaiting: true, LookDue));
    }
}
