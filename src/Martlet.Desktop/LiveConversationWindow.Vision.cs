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
using Martlet.Core.Reading;
using Martlet.Core.Settings;
using Martlet.Providers;

namespace Martlet.Desktop;

// Vision: what Martlet watches and reads on the screen.
public partial class LiveConversationWindow
{
    // ---------- vision ----------

    /// <summary>What Companion › Vision looks at; a camera or address that isn't chosen yet has an empty Id.</summary>
    private WatchSource SavedSource() => (WatchKind)Math.Clamp(preferences.ScreenScope, 0, 3) switch
    {
        WatchKind.Camera => new(WatchKind.Camera, preferences.CameraId, preferences.CameraName),
        WatchKind.Url when WatchSource.Normalize(videoAddress ?? preferences.VideoAddress) is { Length: > 0 } address =>
            new(WatchKind.Url, address, WatchSource.SafeName(address)),
        var kind => new(kind)
    };

    /// <summary>Companion › Vision › How often it comments (also Listening › Watch along).</summary>
    private ChattinessChoice SavedChoice => ChattinessTags.Choice(preferences.ScreenChattiness);
    /// <summary>How chatty Martlet is now: the chosen level, or the one it picked while it decides.</summary>
    private Chattiness ChattinessNow => ChattinessTags.Level(SavedChoice, controller.DecidedChattiness);
    /// <summary>The choice replies follow while there is something in the background to be chatty about (vision or hearing
    /// what this PC plays is turned on in Companion); null otherwise, so a plain conversation's replies aren't told about it.
    /// It follows the saved choices rather than whether vision is paused right now, so replies keep the same instructions
    /// (and the model's prompt cache) while you pause and resume.</summary>
    private ChattinessChoice? BackgroundChattiness => BackgroundOn ? SavedChoice : null;
    private bool BackgroundOn => preferences.Watch || preferences.HearPc;
    // When Martlet last switched how chatty it is (while it decides), for the chattiness line.
    private DateTime? chattinessAt;

    /// <summary>Starts looking at what Companion › Vision chose. Not ready yet (loading, Windows locked) leaves it for
    /// <see cref="StartLive"/>; something that keeps Martlet from seeing says why and leaves watching stopped.</summary>
    private void StartWatching()
    {
        visionProblem = null;
        var selected = controller.Configuration;
        if (closed || !Available || selected is null) return;
        // Martlet can't see only when no model takes pictures (Thinking is text-only, or the image model of its own doesn't see).
        if (!controller.CanSee)
        {
            CantWatch(controller.ImageAdvice());
            return;
        }
        var source = SavedSource();
        if (!source.IsScreen && source.Id.Length == 0)
        {
            CantWatch(source.Kind == WatchKind.Camera ? "Choose a camera in Companion › Vision." : "Enter the camera address in Companion › Vision.");
            return;
        }
        watchSource = source;
        watching = true;
        lookWanted = false;
        pacer = new(ChattinessNow, clock);
        lookedLast = lookedNow = null;
        nextGlance = clock.GetTimestamp();
        sight = lookNote = waitNote = captureNote = attentionNote = null;
        seeing = lookFailed = false;
        monitors = 0;
        lastCheck = null;
        latestShot = attentionShot = null;
        StartReading();
        digest.Use(controller.ScreenDigestThinker, controller.ScreenDigestBoard);
        digest.Turn(preferences.ScreenSummary);
        wasHearing = false;
        // Watching the whole screen also notices what wants your attention: a notification, a flashing taskbar button.
        if (source.Kind == WatchKind.ActiveScreen)
        {
            attentionWatcher.Start();
            noticing = true;
        }
    }

    /// <summary>How fresh the newest picture must be to go with what you type or say.</summary>
    internal static TimeSpan SeenFreshness => TimeSpan.FromSeconds(10);

    /// <summary>Reads the text on the screen with Companion › Reading's list (pools-local.json): Windows OCR on this PC by default,
    /// your computers' Reading role, or not at all when nothing in the list is on. Only the screen is read, never a camera.</summary>
    private void StartReading()
    {
        reader?.Dispose();
        reader = null;
        readNote = null;
        fedRead = null;
        var directory = controller.DataDirectory;
        readingFile = ReadingWritten(directory);
        readingChecked = clock.GetTimestamp();
        if (watchSource.IsScreen) reader = ScreenReader.For(directory, clock);
    }

    private static DateTime ReadingWritten(string? directory)
    {
        if (ReadingList.File(directory) is not { } path) return default;
        try { return System.IO.File.GetLastWriteTimeUtc(path); }
        catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException) { return default; }
    }

    /// <summary>Runs on the UI timer while Martlet watches the screen: when Companion › Reading's reader is free and its pace
    /// (Read the screen) allows, it takes the screen at full size off the UI thread, because a look's downscaled picture is too
    /// small for OCR, and reads it when it changed (or a while passed). Nothing waits for it.</summary>
    private void ReadScreen()
    {
        if (!watchSource.IsScreen) return;
        if (clock.GetElapsedTime(readingChecked) >= ScreenCommentaryPacer.Tick)
        {
            readingChecked = clock.GetTimestamp();
            if (ReadingWritten(controller.DataDirectory) != readingFile) StartReading();
        }
        if (reader is null) return;
        reader.Every = TimeSpan.FromMilliseconds(ScreenReadPace.Normalize(preferences.ReadEveryMs));
        var scope = watchSource.Scope;
        if (reader.Offer(() => glancer.CaptureText(scope).Frame) is not { } reading) return;
        ReadDoneAsync(reader, reading).Forget();
    }

    private async Task ReadDoneAsync(ScreenReader from, Task<ScreenRead?> reading)
    {
        var read = await reading;
        if (closed || !watching || pacer is null || !ReferenceEquals(from, reader)) return;
        if (read is not null) readNote = read.Describe();
        else if (from.Problem is { } problem) readNote = problem;
        else return;
        ScreenReader.LastReport = readNote;
    }

    /// <summary>At each capture, how much the text that counts on the screen changed since the pacer last heard (the newest read
    /// against the one it heard of before), so reading more often, or OCR's flicker, doesn't make looks more likely.</summary>
    private void ObserveReadText()
    {
        if (pacer is null || reader?.Latest is not { } newest || ReferenceEquals(newest, fedRead)) return;
        pacer.ObserveText(fedRead is null ? newest.Change : ScreenText.Change(fedRead.Steady, newest.Steady));
        fedRead = newest;
    }

    /// <summary>The text Companion › Reading read on the screen in the last <see cref="SeenFreshness"/>, for a glance: only the
    /// lines that count (<see cref="ScreenRead.Steady"/>), so a character OCR misread once never reaches the model.</summary>
    private string? ReadText() =>
        watchSource.IsScreen && reader?.Latest is { Steady.Length: > 0 } read && clock.GetUtcNow() - read.At <= SeenFreshness ? read.Steady : null;
    /// <summary>How long something that wants your attention waits for Martlet to be free before it is let go.</summary>
    internal static TimeSpan AttentionPatience => TimeSpan.FromSeconds(60);

    /// <summary>While vision is on, the newest picture of what it watches goes with what you type or say, so the reply sees
    /// what you see: only a picture taken in the last <see cref="SeenFreshness"/> (a capture that was skipped, say a private
    /// window in front, leaves none).</summary>
    private SeenScreen? SeenNow()
    {
        if (!watching || latestFrame is not { } frame || clock.GetElapsedTime(latestAt) > SeenFreshness) return null;
        if (!controller.CanSee) return null;
        try
        {
            return Seen(frame, latestShot);
        }
        catch (Exception error) when (error is ContractException or InvalidOperationException or NotSupportedException or
            System.Runtime.InteropServices.ExternalException)
        {
            return null;
        }
    }

    // A picture to send with a reply: an address's host is not useful to the model; a camera's or window's name is, and for the
    // screen so are the program in front and whether it fills its monitor. Its picture version and time let a reply take the
    // image model's description of it.
    private SeenScreen Seen(ScreenFrame frame, PictureShot? shot) =>
        new(frame.Encode(), watchSource.Kind == WatchKind.Url ? "" : frame.Title, watchSource, frame.App, frame.FullScreen,
            shot?.Version ?? 0, shot?.TakenAt);

    // What a look at this picture saw, to compare later pictures with; null for a picture without a thumbnail.
    private LookedPicture? Looked(ScreenFrame frame) => frame.Signature is { } signature
        ? new(signature, $"{watchSource.Kind}|{watchSource.Id}", frame.Title, frame.App, frame.FullScreen, ReadText(), clock.GetTimestamp())
        : null;

    /// <summary>The vision line under the status, kept short: what Martlet watches, then only a look in progress, why it is
    /// holding off (you seem away, its break, a busy provider, nothing changed since the last look, something that wants your
    /// attention once you're done talking)
    /// or a look that failed. Paused vision shows nothing (the button says so); a problem shows here unless the status line
    /// already says it. The rest is in <see cref="VisionDetail"/>. It never contains window titles.</summary>
    private string VisionLine()
    {
        if (!watching) return visionProblem is { } problem && problem != notice ? problem : "";
        if (sight is null) return $"Watching {watchSource.Label} soon.";
        if (!seeing) return sight;
        var looking = commentary is { OwnershipReleased: false } glance ? glance
            : owned is { OwnershipReleased: false, Look: true } taking ? taking : null;
        var state = looking is not null
            ? looking.Attention is { } about ? $"Taking a look at {about.Plain}…" : "Taking a look…"
            : waitNote ?? (lookFailed ? lookNote : null);
        return state is null ? sight : $"{sight} {state}";
    }

    /// <summary>The vision line's tooltip: how many monitors the whole screen spans, the program in front (and whether it is full
    /// screen) as the Thinking model is told it, how the last look went, what wanted your attention but wasn't looked at, and
    /// the image model's last description while it describes the pictures. Empty while not watching; never window titles.</summary>
    private string VisionDetail() => !watching ? "" : string.Join(" ", new[]
    {
        monitors > 1 ? $"Your whole screen is {monitors} monitors." : null,
        watchSource.IsScreen && latestFrame is { } frame && (frame.App.Length > 0 || frame.FullScreen)
            ? $"Active app: {ActiveApp.Describe(frame.App, frame.FullScreen)}." : null,
        lookNote,
        attentionNote,
        readNote,
        ImageModelDetail()
    }.Where(note => note is not null));

    // Runs on the UI timer: notices conversation and what wants your attention, collects finished glances and schedules the
    // next capture.
    private void Watch()
    {
        if (!watching || closed || pacer is null) return;
        if (commentary is { OwnershipReleased: true } done && !ReferenceEquals(handledCommentary, done))
        {
            handledCommentary = done;
            HandleCommentary(done);
            if (!watching) return;
        }
        // A reply in progress, someone talking or something heard that waits for a reply means you and Martlet are talking.
        if (Conversing) pacer.NoteConversation();
        // Screen summary over time: a summary when the pictures changed, and one right after you start to speak when no
        // fresh one is there. Background only; nothing waits for it.
        var hearing = listener is { Hearing: true };
        if (hearing && !wasHearing) digest.UserSpeaking();
        wasHearing = hearing;
        digest.Tick();
        // With an image model of its own: while you talk or type, it describes the newest picture ahead of time, so the reply
        // takes the description without waiting (the controller asks only when none counts for that picture).
        if (Talking) DescribeAhead(PictureTrigger.Talking);
        // Something wants your attention: capture now, while the notification is still up.
        if (noticing && attentionWatcher.Check() is { } signal)
        {
            attention = signal;
            attentionAt = clock.GetTimestamp();
            DropAttentionFrame();
            nextGlance = attentionAt;
        }
        if (attention is not null && clock.GetElapsedTime(attentionAt) > AttentionPatience)
        {
            attention = null;
            DropAttentionFrame();
            waitNote = null;
        }
        // Companion › Reading reads the screen at its own pace (Read the screen), apart from the looks' screenshots.
        ReadScreen();
        if (glancing || clock.GetTimestamp() < nextGlance) return;
        nextGlance = clock.GetTimestamp() + (long)(ScreenCommentaryPacer.Tick.TotalSeconds * clock.TimestampFrequency);
        GlanceAsync().Forget();
    }

    private bool Conversing => owned is { OwnershipReleased: false } || heardQueue.Count > 0 ||
        listener is { Hearing: true } or { Transcribing: > 0 };
}
