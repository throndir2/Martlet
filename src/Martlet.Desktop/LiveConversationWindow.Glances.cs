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
using Martlet.Diagnostics;
using Martlet.Providers;

namespace Martlet.Desktop;

// Vision: glances and Martlet's remarks on what it sees.
public partial class LiveConversationWindow
{
    private async Task GlanceAsync()
    {
        glancing = true;
        try
        {
            var source = watchSource;
            var started = clock.GetTimestamp();
            var result = await Task.Run(() => source.IsScreen ? glancer.Capture(source.Scope) : video.Capture(source));
            if (!watching || closed || pacer is null)
            {
                result.Frame?.Clear();
                Task.Run(glancer.Release).Forget();
                Task.Run(video.Release).Forget();
                return;
            }
            captureNote = source.IsScreen ? result.Note : null;
            lastCheck = DateTime.Now;
            seeing = result.Frame is not null;
            if (result.Frame is not { } frame)
            {
                // Nothing current to look at or to send with your messages; the next screenshot is a new picture.
                DropLatest();
                latestShot = null;
                lookWanted = false;
                sight = !source.IsScreen ? result.Skip switch
                {
                    GlanceSkip.Blank => $"{char.ToUpperInvariant(source.Label[0])}{source.Label[1..]} is black, so check the lens cover or privacy shutter.",
                    _ => result.Note ?? "Couldn't read the camera this time; trying again shortly."
                } : result.Skip switch
                {
                    GlanceSkip.MartletInFront => "Only Martlet is visible.",
                    GlanceSkip.Private => "A private window is in front, so Martlet won't look.",
                    GlanceSkip.Blank when result.ProtectedContent => "Protected video is hidden by Windows.",
                    GlanceSkip.Blank when result.Note is { } why =>
                        $"The screen is black to Martlet ({why.TrimEnd('.')}), so try borderless or windowed mode.",
                    GlanceSkip.Blank => "The screen is black to Martlet.",
                    GlanceSkip.Minimized or GlanceSkip.NoWindow => "No window in front to look at.",
                    _ => "Couldn't capture the screen this time."
                };
                return;
            }
            // An image model of its own that refused a picture can't see (Martlet remembered it): watching stops with what to change.
            if (controller.OwnImageModel && !controller.CanSee)
            {
                frame.Clear();
                StopWatching(controller.ImageAdvice());
                return;
            }
            monitors = source.Kind == WatchKind.ActiveScreen ? result.Monitors : 0;
            sight = source.Kind == WatchKind.ActiveScreen ? "Watching your whole screen."
                : !result.BehindMartlet ? $"Watching {source.Label}." : "Watching the window behind Martlet.";
            if (!twinkling) Motion.Blink(VisionDot);
            pacer.ObserveFrame(frame.Change);
            ObserveReadText();
            var shot = KeyFor(frame);
            var changed = shot.Version != latestShot?.Version;
            DropLatest();
            latestFrame = frame;
            latestAt = clock.GetTimestamp();
            latestShot = shot;
            digest.Observe(frame.Width, frame.Height, frame.Title, frame.Change, ReadText(), frame.CopyPixels,
                ActiveApp.Label(frame.App, frame.FullScreen));
            // With each new screenshot of your screen Martlet may decide to look at something on it instead of your mouse.
            if (source.IsScreen) Gaze?.Observe(frame);
            // With an image model of its own, a changed picture is described ahead of time while you talk with Martlet (paced).
            if (changed) DescribeAhead(PictureTrigger.Changed);
            var busy = commentary is { OwnershipReleased: false } || pendingText is not null || operations.IsRunning || Conversing;
            // Keyboard/mouse idleness means "away" only for the screen; in front of a camera people often don't type at all.
            var idle = source.IsScreen ? glancer.UserIdle : TimeSpan.Zero;
            // Something wants your attention: the first picture taken after it noticed is kept for one look as soon as
            // Martlet is free, the way a friend would say "someone's messaging you".
            if (attention is { } signal)
            {
                if (attentionFrame is null)
                {
                    // This capture started before it noticed; the next one, right away, shows it.
                    if (started < attentionAt)
                    {
                        nextGlance = clock.GetTimestamp();
                        lookWanted = false;
                        return;
                    }
                    attentionFrame = frame;
                    attentionShot = latestShot;
                }
                var heed = pacer.DecideAttention(busy, idle);
                if (heed == PacerVerdict.Look)
                {
                    lookAttention = signal;
                    attention = null;
                    attentionNote = null;
                    lookWanted = true;
                    waitNote = null;
                    DescribeAhead(PictureTrigger.Look, attention: true);
                    if (!operations.IsRunning) TryStartCommentary();
                    return;
                }
                if (heed == PacerVerdict.Busy)
                {
                    lookWanted = false;
                    waitNote = $"Martlet noticed {signal.Plain} and looks once you're done talking.";
                    return;
                }
                // Not now (the hourly budget, a busy provider, an empty room, or it just looked at one): the usual pace goes on.
                attentionNote = $"Noticed {signal.Plain} at {DateTime.Now:t} but didn't look: " + heed switch
                {
                    PacerVerdict.UserAway => "you seem away.",
                    PacerVerdict.HourlyLimit => "Martlet is taking a break from looking.",
                    PacerVerdict.BackingOff => "the provider is busy.",
                    _ => "Martlet just looked at one."
                };
                attention = null;
                DropAttentionFrame();
            }
            var verdict = pacer.Decide(busy, idle, LookChange.Compare(lookedLast, Looked(frame)));
            lookWanted = verdict == PacerVerdict.Look;
            waitNote = verdict switch
            {
                PacerVerdict.UserAway => "Waiting until you're back.",
                PacerVerdict.HourlyLimit => "Taking a break from looking.",
                PacerVerdict.BackingOff => "The provider is busy; waiting before the next look.",
                PacerVerdict.Unchanged => "Nothing changed since the last look.",
                _ => null
            };
            if (!lookWanted) return;
            // With an image model of its own, the look's picture is described now: a look or a reply that takes it uses that.
            DescribeAhead(PictureTrigger.Look);
            if (!operations.IsRunning) TryStartCommentary();
        }
        finally
        {
            glancing = false;
        }
    }

    // The newest picture is zeroed when replaced, unless it is still kept for a look at what wants your attention.
    private void DropLatest()
    {
        if (latestFrame is { } old && !ReferenceEquals(old, attentionFrame)) old.Clear();
        latestFrame = null;
    }

    private void DropAttentionFrame()
    {
        if (attentionFrame is { } old && !ReferenceEquals(old, latestFrame)) old.Clear();
        attentionFrame = null;
    }

    private bool TryStartCommentary()
    {
        var frame = lookAttention is not null ? attentionFrame : latestFrame;
        var shot = lookAttention is not null ? attentionShot : latestShot;
        // While Martlet sings, a look waits until the song is over.
        if (!lookWanted || !watching || closed || frame is null || operations.IsRunning || controller.Singing?.Playing == true) return false;
        // One moment: a look that is due while lines this PC played or finished work wait is one reply that takes them all.
        var plan = MomentTurn.Plan(MomentTrigger.Look, PcWaiting, JobsWaiting, lookDue: true);
        if (plan.Route != MomentRoute.Glance && Available) return StartMoment(plan);
        lookWanted = false;
        var about = lookAttention;
        lookAttention = null;
        try
        {
            var image = frame.Encode();
            // How much changed since the last look goes in the look's message, so the model stays quiet when nothing really did.
            var picture = Looked(frame);
            var changed = LookChange.Describe(LookChange.Compare(lookedLast, picture),
                lookedLast is { } before ? clock.GetElapsedTime(before.At) : null, watchSource.IsScreen ? glancer.UserIdle : TimeSpan.Zero);
            // An address's host is not useful to the model; a camera's or window's name is, and so is the program in front. The
            // picture's version lets an image model's description of the same picture be used again.
            commentary = controller.StartCommentary(image, watchSource.Kind == WatchKind.Url ? "" : frame.Title, SavedChoice,
                Voice, screenApproved: true, watchSource, attention: about, look: watchSource.IsScreen && Gaze?.Offer(frame) == true,
                screenText: ReadText(), app: frame.App, fullScreen: frame.FullScreen, picture: shot?.Version ?? 0, takenAt: shot?.TakenAt,
                changed: changed);
            lookedNow = picture;
            if (about is not null) pacer?.NoteAttention();
            waitNote = null;
            return true;
        }
        catch (LiveActionException error)
        {
            if (error.Code is "conversation.ownership_busy")
            {
                // Martlet got busy first: a look at what wants your attention waits for the next chance.
                if (about is not null) attention ??= about;
                return false;
            }
            StopWatching(error.Code == "commentary.vision_unsupported" ? controller.ImageAdvice() : Remedy(error.Code));
            return false;
        }
        catch (Exception error) when (error is ContractException or InvalidOperationException or NotSupportedException or System.Runtime.InteropServices.ExternalException)
        {
            lookNote = "Couldn't prepare the image.";
            lookFailed = true;
            return false;
        }
        finally
        {
            if (about is not null && attention is null) DropAttentionFrame();
        }
    }

    private void HandleCommentary(LiveConversationOperation done)
    {
        var status = done.Status;
        var at = DateTime.Now.ToString("t") + (done.Attention is { } about ? $" ({about.Plain})" : "");
        var trimmed = LookTrimmed(done.LookLeftOut);
        waitNote = null;
        lookFailed = false;
        // A look the model answered is what later pictures are compared with; a failed one leaves the look before it.
        var looked = lookedNow;
        lookedNow = null;
        if (done.Passed || status.Code is "runtime.Completed" or "runtime.Refused" or "commentary.interrupted") lookedLast = looked ?? lookedLast;
        if (done.Passed)
        {
            pacer?.NoteLook(false);
            lookNote = $"Last look {at}: nothing to say.{trimmed}";
            return;
        }
        if (status.Code is "runtime.Completed")
        {
            pacer?.NoteLook(true);
            if (done.Turn?.Content.Text.Trim() is { Length: > 0 } remark)
                KeepContext(Add(ChatRole.Martlet, remark, $"{CharacterName}, about {watchSource.Label}"), done);
            lookNote = $"Last look {at}: commented.{trimmed}";
            return;
        }
        if (status.Code is "runtime.Refused" or "commentary.interrupted" or "commentary.stopped" or "conversation.canceled" or "conversation.revoked")
        {
            pacer?.NoteLook(false);
            lookNote = status.Code == "runtime.Refused" ? $"Last look {at}: no comment." : $"Last look {at}: stopped.";
            return;
        }
        // The image model refused the look's picture, so it can't see (Martlet remembered it): watching stops with what to change.
        if (status.Code == "commentary.image_refused")
        {
            StopWatching(controller.ImageAdvice());
            return;
        }
        // Even the picture with the persona and Martlet's instructions is too long for Thinking's context size (the look already
        // went without everything it can leave out), so every look would fail the same way.
        if (status.Code == "conversation.input_limit")
        {
            StopWatching(LookTooLong);
            return;
        }
        // The image model couldn't describe the look's picture (busy, too slow or failing): the next look waits a while, as for a
        // busy provider.
        if (status.Code == "commentary.not_described" && pacer is not null)
        {
            var later = pacer.NoteBackoff();
            lookFailed = true;
            lookNote = $"Last look {at}: the image model couldn't describe the picture, so Martlet looks again in " +
                $"{(int)later.TotalMinutes} minute{(later.TotalMinutes >= 2 ? "s" : "")}.";
            return;
        }
        var selected = controller.Configuration;
        var provider = done.Turn?.Snapshot.ProviderFailure ?? status.ProviderFailure;
        var job = FailedJob(done);
        // A busy or slow provider (NVIDIA Build's free endpoints limit requests often) is waited out, not a reason to stop vision.
        if (job != ProviderRole.Tts && provider is ProviderFailureCode.RateLimited or ProviderFailureCode.Server or
            ProviderFailureCode.Network or ProviderFailureCode.FirstDeltaTimeout or ProviderFailureCode.IdleTimeout or
            ProviderFailureCode.DeadlineExceeded && pacer is not null)
        {
            var wait = pacer!.NoteBackoff();
            lookFailed = true;
            lookNote = $"Last look {at}: " + (provider == ProviderFailureCode.RateLimited
                ? "the provider is limiting requests" : "the provider didn't answer") +
                $", so Martlet looks again in {(int)wait.TotalMinutes} minute{(wait.TotalMinutes >= 2 ? "s" : "")}.";
            return;
        }
        // No automatic retry of a failing request: stop and say what to change.
        StopWatching(job == ProviderRole.Tts
            ? Lead("Martlet stopped vision", ProviderRemedy(provider!.Value, done.Authorization.Configuration, job))
            : provider == ProviderFailureCode.InputLimit
            ? "The picture was too large for Thinking, so update the host if Thinking runs on a Martlet host."
            : provider is not (null or ProviderFailureCode.ModelRetired or ProviderFailureCode.ModelNotFound) &&
                selected is not null && selected.Vision() != VisionSupport.Supported
            ? Lead("The Thinking model rejected the picture", selected.VisionAdvice())
            : Lead("Martlet stopped vision", provider is { } code ? ProviderRemedy(code, done.Authorization.Configuration, job) : Remedy(status.Code)));
    }

    /// <summary>Why vision stopped when a look can't fit Thinking's context size even with only the picture, the persona and
    /// Martlet's instructions.</summary>
    internal const string LookTooLong = "Martlet stopped vision: the picture and the persona together are too long for Thinking's " +
        "context size, so shorten the persona or raise the context size in Companion › Replies.";

    /// <summary>What the last look's note adds when the look went without some of what it carries to fit Thinking's context
    /// size; empty when everything fit.</summary>
    internal static string LookTrimmed(IReadOnlyList<string> leftOut) =>
        leftOut.Count == 0 ? "" : $" To fit Thinking's context size, it went without {string.Join(", ", leftOut)}.";

    /// <summary>Stops looking and frees the screen capture, camera or stream. A <paramref name="problem"/> is shown and keeps
    /// watching stopped until you press Start watching again.</summary>
    private void StopWatching(string? problem)
    {
        watching = lookWanted = false;
        pacer = null;
        reader?.Dispose();
        reader = null;
        readNote = null;
        digest.Turn(false);
        // The image model's descriptions of what Martlet watched go too.
        controller.ForgetPictures();
        latestShot = attentionShot = null;
        Gaze?.Stop();
        DropAttentionFrame();
        DropLatest();
        attention = lookAttention = null;
        attentionNote = null;
        if (noticing)
        {
            attentionWatcher.Stop();
            noticing = false;
        }
        // Frees the open duplication, camera or stream (the camera light goes off); off the UI thread in case a capture is finishing.
        Task.Run(glancer.Release).Forget();
        Task.Run(video.Release).Forget();
        if (commentary is { OwnershipReleased: false } glance) controller.Stop(glance, "commentary.stopped", keepContext: true);
        if (problem is not null)
        {
            CantWatch(problem);
            notice = problem;
        }
        RenderActions();
    }

    /// <summary>Martlet can't see: says why, and watching stays stopped until Start watching is pressed again.</summary>
    private void CantWatch(string problem)
    {
        visionProblem = problem;
        watchPaused = true;
    }
}
