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
using Martlet.Core.Contracts;
using Martlet.Core.Settings;
using Martlet.Providers;

namespace Martlet.Desktop;

// The app slot (typed text, what always listening heard, then a wanted look) and one moment that takes everything that waits.
public partial class LiveConversationWindow
{
    // ---------- the app slot: typed text, what always listening heard, then a wanted look ----------

    // Runs on the UI timer: settles what just finished, keeps listening, then hands the app slot to whatever comes next.
    private void Pump()
    {
        if (closed) return;
        Settle();
        Collect();
        FollowCall();
        FollowSoundDigest();
        FollowPcActivity();
        WatchTouches();
        if (loading is not null) return;
        // While Windows is locked only messages from paired chats are answered (text only, never aloud).
        if (locked)
        {
            if (!operations.IsRunning) TryRemote();
            return;
        }
        KeepListening();
        Interrupt();
        // What a reply started early is built with (Companion › Listening › Start replies early): what TryAnswer sends with the
        // next thing you say while nothing else waits.
        controller.EarlyPlan = EarlyPlanNow();
        if (operations.IsRunning)
        {
            // A reply started early holds the app slot, unseen and unheard, until your turn ends: only your words take it.
            // Typed text, a reload or a message from a chat lets it go first.
            if (controller.EarlyReply is not null)
            {
                if (pendingText is not null || reloadReason is not null || loadPending || remoteQueue.Count > 0)
                    controller.LetGoEarly("something else came first");
                else TryAnswer();
                return;
            }
            if (pendingText is not null || reloadReason is not null || remoteQueue.Count > 0) YieldSlot();
            return;
        }
        // The reply may have released the slot just now; settle it before anything replaces it.
        Settle();
        // A situation change (a game started or ended, a host went away or came back) waits until nobody talks and nothing is
        // being transcribed, so it never cuts off what you say, a reply started early or a remark.
        if (situationReason is { } moved && reloadReason is null && !loadPending &&
            listener is not { Hearing: true } && listener is not { Transcribing: > 0 } && pendingText is null)
        {
            situationReason = null;
            reloadReason = moved;
        }
        if (loadPending)
        {
            loadPending = false;
            notice = null;
            LoadAsync().Forget();
            return;
        }
        if (reloadReason is { } reason)
        {
            reloadReason = null;
            if (Messages.Count > 0) AddNote(reason + " Martlet picked it up and carries on.");
            following = true;
            LoadAsync().Forget();
            return;
        }
        if (!Available)
        {
            // Loaded without a Thinking setup: a message from a chat is told so instead of waiting for nothing.
            if (ready && controller.Configuration is null) FailRemote("Martlet isn't set up to think yet, so set up Thinking in Companion on your PC.");
            return;
        }
        if (pendingText is { } text)
        {
            pendingText = null;
            var message = pendingMessage;
            pendingMessage = null;
            if (!StartTurn(text))
            {
                message?.AddNote("Not sent.");
                if (InputText.Text.Length == 0) InputText.Text = text;
            }
            else if (message is not null && owned is { } sent) typed = (sent, message);
            return;
        }
        if (TryAnswer()) return;
        if (TryRemote()) return;
        if (TryTouch()) return;
        if (TryReport()) return;
        TryStartCommentary();
    }

    // ---------- one moment: whatever starts a reply takes everything else that waits ----------

    /// <summary>Finished background work Martlet may bring up on its own right now: something finished that the user didn't stop
    /// (Esc holds it for the next message), Thinking longer shares results as soon as Martlet is free or a reminder is due (it
    /// comes up when free either way), Martlet isn't paused and no song is playing.</summary>
    private bool JobsWaiting => !reportHeld && !Paused && controller.Singing?.Playing != true &&
        (WhenFree ? controller.Jobs.HasNews : controller.Jobs.HasNotice);

    /// <summary>Thinking longer shares results as soon as Martlet is free (otherwise only due reminders come up on their own).</summary>
    private bool WhenFree => controller.Configuration?.ThinkLonger.When == ThinkDelivery.WhenFree;

    /// <summary>The pacer wants a look (or one at what wants your attention) and its picture is there; while Martlet sings, a
    /// look waits until the song is over.</summary>
    private bool LookDue => lookWanted && watching && !closed && (lookAttention is not null ? attentionFrame : latestFrame) is not null &&
        controller.Singing?.Playing != true;

    /// <summary>Lines this PC played wait (none still being told apart from your own voice played back).</summary>
    private bool PcWaiting => playingQueue.Count > 0 && pcHeld.Count == 0;

    /// <summary>The picture a reply takes: the look's when it takes the look vision wanted (with what wants your attention, if
    /// that is what it was for), otherwise the newest (<see cref="SeenNow"/>).</summary>
    private (SeenScreen? Seen, AttentionSignal? About) Sight(bool look)
    {
        if (!look) return (SeenNow(), null);
        var about = lookAttention;
        var frame = about is not null ? attentionFrame : latestFrame;
        var shot = about is not null ? attentionShot : latestShot;
        lookWanted = false;
        lookAttention = null;
        waitNote = null;
        SeenScreen? seen = null;
        if (frame is not null && controller.CanSee)
        {
            try { seen = Seen(frame, shot); }
            catch (Exception error) when (error is ContractException or InvalidOperationException or NotSupportedException or
                System.Runtime.InteropServices.ExternalException) { }
            // The reply sees the look's picture: later pictures are compared with it.
            if (seen is not null) lookedLast = Looked(frame) ?? lookedLast;
        }
        if (about is not null) DropAttentionFrame();
        return seen is null ? (SeenNow(), null) : (seen, about);
    }

    // A reply that took a look at what wants your attention: the pacer doesn't look at one again right away.
    private void TookLook(LiveConversationOperation started)
    {
        if (started.Look && started.Attention is not null) pacer?.NoteAttention();
    }

    // A reply that took the look counts as one: the pacer's spacing and hourly budget, and the vision line's last look.
    private void NoteTakenLook(LiveConversationOperation done, string code)
    {
        var at = DateTime.Now.ToString("t") + (done.Attention is { } about ? $" ({about.Plain})" : "");
        var spoke = code == "runtime.Completed" && !done.Passed;
        pacer?.NoteLook(spoke);
        lookNote = $"Last look {at}: " + (spoke ? "answered together with the rest."
            : code is "runtime.Completed" or "listen.passed" ? "nothing to say." : "stopped.");
    }

    /// <summary>The talk window's LiveTurnInputs line: what Martlet's newest reply, report or look took together.</summary>
    internal static string TurnInputsLine(bool glance, bool report, string took, bool look) =>
        glance ? $"Last look took {took}."
        : $"Last {(report ? "report" : "reply")} took {took}{(look ? ", counted as a look" : "")}.";

    /// <summary>Starts a moment Martlet begins on its own (finished work came up, or a look is due) that is more than a plain
    /// glance: a reply to what this PC played with the finished work in its notes, or Martlet's report, either with the
    /// picture (and the look, when it takes it). False when it couldn't start (a glance is the caller's).</summary>
    private bool StartMoment(MomentPlan plan)
    {
        var playing = plan.PcAudio ? TakePlaying(-1) : [];
        if (plan.PcAudio && playing.Count == 0) plan = plan with { PcAudio = false };
        if (plan.Route == MomentRoute.Glance) return false;
        var (seen, about) = Sight(plan.Look);
        try
        {
            LiveConversationOperation started;
            if (plan.Route == MomentRoute.Reply)
            {
                started = controller.Start(PcMessage(playing), Voice, microphone: false, approved: true, spoken: true,
                    confidence: playing.Min(entry => entry.Confidence), seen: seen, pcAudio: true, chattiness: BackgroundChattiness,
                    bringUp: plan.Jobs, attention: about, look: plan.Look);
                answering = playing;
                answeredAt = clock.GetTimestamp();
            }
            else if (controller.StartReport(Voice, noticesOnly: !WhenFree, seen, about, plan.Look) is { } report)
            {
                started = report;
                if (report.Delivery is { } carried)
                    foreach (var job in carried.Jobs) AddNote(JobNote(job));
            }
            else return false;
            owned = started;
            yielded = null;
            notice = null;
            TookLook(started);
            Observe();
            return true;
        }
        catch (LiveActionException error) when (error.Code is "conversation.ownership_busy" or "conversation.controls_blocked")
        {
            Requeue(playing);
            return false;
        }
        catch (Exception error) when (error is LiveActionException or ContractException)
        {
            ErrorLog.Warn($"Background work: couldn't bring up finished work ({(error as LiveActionException)?.Code ?? "invalid input"}).");
            if (plan.Jobs) reportHeld = true;
            return false;
        }
    }

    // The note above Martlet's report: which job finished and how.
    private static string JobNote(BackgroundJob job) => job.Kind.Notice ? $"{job.Kind.Doing}: “{job.Label}”." : job.State switch
    {
        BackgroundJobState.Succeeded => $"Finished: {job.Kind.Doing.ToLowerInvariant()} “{job.Label}” ({BackgroundJobs.Clockface(job.Elapsed)}).",
        BackgroundJobState.TimedOut => $"Ran out of time {job.Kind.Doing.ToLowerInvariant()} “{job.Label}”.",
        BackgroundJobState.Canceled => $"Stopped {job.Kind.Doing.ToLowerInvariant()} “{job.Label}”.",
        _ => $"Couldn't finish {job.Kind.Doing.ToLowerInvariant()} “{job.Label}”."
    };

    private void Settle()
    {
        if (owned is { OwnershipReleased: true } done && !ReferenceEquals(handled, done))
        {
            handled = done;
            Finished(done);
        }
    }
}
