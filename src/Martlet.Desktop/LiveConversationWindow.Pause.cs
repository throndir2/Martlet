using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Martlet.Audio;
using Martlet.Conversation;
using Martlet.Core.Contracts;
using Martlet.Core.Reading;
using Martlet.Core.Settings;
using Martlet.Diagnostics;
using Martlet.Providers;

namespace Martlet.Desktop;

// Pause and Resume Martlet (the notification-area menu).
public partial class LiveConversationWindow
{
    // ---------- Pause and Resume Martlet (the notification-area menu) ----------

    /// <summary>What Pause Martlet stopped (always listening, vision), restored by Resume; null while not paused.</summary>
    private (bool Listening, bool Watching)? pausedWith;
    internal bool Paused => pausedWith is not null;
    internal bool IsListening => listening;
    internal bool IsWatching => watching;
    /// <summary>Always listening is chosen (the window's Start listening / Stop listening button shows).</summary>
    internal bool HandsFree => preferences.HandsFree;
    /// <summary>Start listening was pressed and listening hasn't been stopped since.</summary>
    internal bool ListeningStarted => !listenPaused;
    /// <summary>Always listening is hearing you, or what you just said is still being transcribed or taken.</summary>
    internal bool HearingYou => MicBusy;
    /// <summary>Always listening hears your voice right now (a touch's voice sound waits for you).</summary>
    internal bool UserTalking => listener is { Hearing: true };

    /// <summary>Home's listening indicator: what listening is doing now, and whether that is a problem.</summary>
    internal (string Text, bool Problem) ListeningStatus
    {
        get
        {
            if (Paused) return ("Paused until you resume Martlet from its notification-area icon.", false);
            if (listenPaused)
                return listenWhenReady || loading is not null || loadPending ? ("Getting ready to listen…", false)
                    : notice is not null && (!Available || !MicrophoneUsable) ? (notice, true) : ("Not listening", false);
            if (loading is not null || loadPending || !ready && notice is null) return ("Getting ready to listen…", false);
            if (locked) return ("Windows is locked; Martlet listens again when you unlock it.", false);
            if (listening && micProblem is not null)
                return (Noted(micProblem, listener?.Retry is { } wait ? $"Martlet keeps trying every {wait.TotalSeconds:0} s" : "Martlet keeps trying"), true);
            if (listenProblem is not null) return (Noted(listenProblem, "Martlet keeps trying"), true);
            if (!listening) return (ListeningProblem(), true);
            if (listener is { Hearing: true }) return ("Hearing you…", false);
            if (owned is { OwnershipReleased: false } live && !live.Status.Finished) return ("Martlet is replying…", false);
            if (listener is { Held: true }) return ("Martlet is speaking…", false);
            return ("Listening, so just start talking.", false);
        }
    }

    /// <summary>Start listening / Stop listening, as the window's own button.</summary>
    internal void ToggleListening() => Mic_Click(this, new RoutedEventArgs());

    /// <summary>What the listening icon button does now ("Start listening", "Stop listening" or "Can't listen"); its tooltip's first line.</summary>
    internal string MicLabel { get; private set; } = "";
    /// <summary>What the watching icon button does now ("Start watching", "Stop watching" or "Can't see"); its tooltip's first line.</summary>
    internal string VisionLabel { get; private set; } = "";

    /// <summary>Vision is on in Companion (the window's Start watching / Stop watching button shows).</summary>
    internal bool VisionOn => preferences.Watch;
    /// <summary>Start watching was pressed and watching hasn't been stopped since (it may still be getting ready).</summary>
    internal bool WatchingStarted => preferences.Watch && !watchPaused;

    /// <summary>Start watching on Home or in the notification-area menu: as the window's own Start watching button, and once the
    /// conversation is ready when it is still loading.</summary>
    internal void WatchWhenReady()
    {
        if (!closed && watchPaused) Vision_Click(this, new RoutedEventArgs());
    }

    /// <summary>Stop watching on Home or in the notification-area menu, as the window's own button.</summary>
    internal void StopWatchingNow()
    {
        if (!closed && !watchPaused) Vision_Click(this, new RoutedEventArgs());
    }

    /// <summary>Home's watching indicator: what vision is doing now, and whether that is a problem.</summary>
    internal (string Text, bool Problem) WatchingStatus
    {
        get
        {
            if (Paused) return ("Paused until you resume Martlet from its notification-area icon.", false);
            if (watching) return commentary is { OwnershipReleased: false } ? ("Taking a look…", false) : ($"Watching {watchSource.Label}.", false);
            if (watchPaused) return visionProblem is { } problem ? (problem, true) : ("Not watching", false);
            if (loading is not null || loadPending || !ready && notice is null) return ("Getting ready to watch…", false);
            if (locked) return ("Windows is locked; Martlet watches again when you unlock it.", false);
            return notice is not null ? (notice, true) : ("Getting ready to watch…", false);
        }
    }

    /// <summary>Pause Martlet: stops a reply, a recording and vision like Stop, and also stops listening, until Resume.</summary>
    internal void Pause()
    {
        if (closed) return;
        pausedWith ??= (!listenPaused, preferences.Watch && !watchPaused);
        StopAll("conversation.canceled");
        listening = false;
        listenPaused = true;
        StopListening(keepHeard: false);
        watchPaused = true;
        notice = "Paused: Martlet isn't listening or looking until you resume it from its notification-area icon.";
        RenderActions();
    }

    /// <summary>Resume Martlet: listening and vision come back if Pause stopped them, as their buttons would start them.</summary>
    internal void Resume()
    {
        if (closed || pausedWith is not { } was) return;
        pausedWith = null;
        notice = null;
        if (was.Listening && preferences.HandsFree && listenPaused)
        {
            listenRetryAt = 0;
            if (MicrophoneUsable)
            {
                listenPaused = false;
                listening = true;
            }
            else notice = ListeningProblem();
        }
        if (was.Watching && preferences.Watch && !watching)
        {
            watchPaused = false;
            StartWatching();
        }
        RenderActions();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        // An open task list closes first, like any flyout; the next Esc stops Martlet.
        if (CloseTasks()) return;
        StopAll("conversation.canceled", button: "Esc");
    }

    /// <summary>Stop (Esc): Martlet's reply, any recording, watching and a song stop right away (the song with a quick fade), and
    /// what was heard but not yet answered is dropped. Always listening carries on, so nothing you say next is missed; only the
    /// mic button pauses it. Start watching turns watching back on; the conversation so far is kept unless the window closes.</summary>
    private void StopAll(string reason, bool keepContext = true, string button = "Stop")
    {
        mouseHeld = keyHeld = false;
        PttButton.ReleaseMouseCapture();
        controller.StopSong(musical: false, button);
        if (pendingText is not null) pendingMessage?.AddNote("Not sent.");
        pendingText = null;
        pendingMessage = null;
        reloadReason = null;
        homeQuestion?.TrySetResult(false);
        foreach (var entry in heardQueue) entry.Bubble.AddNote("Not answered.");
        heardQueue.Clear();
        playingQueue.Clear();
        answering = null;
        restarts = 0;
        // A reply started early for what you are saying goes too (nothing of it was shown or said).
        controller.LetGoEarly("you pressed " + button);
        if (watching)
        {
            watchPaused = true;
            StopWatching(null);
        }
        // Stop quiets Martlet bringing up its background work too; what it carried waits for your next message. Background work
        // itself keeps going (its own Cancel stops it).
        if (owned is { OwnershipReleased: false } bringing && (bringing.Report || bringing.BringUp && bringing.Delivery is not null)) reportHeld = true;
        if (owned is not null) controller.Stop(owned, reason, keepContext);
        loading?.RequestCancellation();
        observation?.Cancel();
        Observe();
        RenderActions();
    }
}
