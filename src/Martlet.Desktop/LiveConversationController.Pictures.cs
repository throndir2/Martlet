using System.IO;
using System.Text.Json;
using Martlet.Conversation;
using Martlet.Core.Settings;
using Martlet.Providers;

namespace Martlet.Desktop;

// Pictures and the image model (docs/SENSE_MODELS.md, Pictures: the image model). When the image path is Described, no picture
// goes to Thinking: the image model describes the newest picture ahead of time (PictureDescriptions, on its sense lane), a reply
// takes a description only when one is ready for the picture it would have sent (it never waits), a look describes its own
// picture first (two stages), and the screen summary uses the image model first. With the default path (Thinking) none of this
// runs and no request changes.
internal sealed partial class LiveConversationController
{
    /// <summary>image-model-status.json in the data directory: where pictures go, how the image model's descriptions went and
    /// how many replies and looks took one (counts and times only; never a description), which MCP's image_model_check reads.</summary>
    internal const string PictureStatusFile = "image-model-status.json";

    private PictureDescriptions? pictures;
    private int describedReplies, repliesWithout, describedLooks, reusedLooks, failedLooks, picturesDescribed, picturesFailed;
    private (DateTimeOffset At, bool Took, double? Age)? lastReplyPicture;
    private int pictureStatusPending;

    /// <summary>The image model's descriptions of what Martlet watches (in memory only).</summary>
    internal PictureDescriptions Pictures => LazyInitializer.EnsureInitialized(ref pictures, NewPictures);

    private PictureDescriptions NewPictures()
    {
        var made = new PictureDescriptions(clock);
        made.Ended += PictureEnded;
        return made;
    }

    /// <summary>Where pictures go now for the conversation's setup (<see cref="SenseRoute(SenseKind)"/>).</summary>
    internal SenseRoute ImageRoute => SenseRoute(SenseKind.Image);

    /// <summary>Whether Martlet can see with the conversation's setup: its pictures go to Thinking or to the image model. Only the
    /// None path is "can't see".</summary>
    internal bool CanSee => Configuration is not null && ImageRoute.Path != SensePath.None;

    /// <summary>Whether the image model describes pictures for Thinking now (the Described path).</summary>
    internal bool ImageDescribed => Configuration is not null && ImageRoute.Path == SensePath.Described;

    /// <summary>Whether this PC chose an image model of its own (sense-models.json), whether or not it sees.</summary>
    internal bool OwnImageModel => SenseModels.Place(SenseKind.Image) is not null;

    /// <summary>Whether Martlet can see, and what to change when it can't, for where pictures go now.</summary>
    internal string ImageAdvice() => Configuration is { } configured
        ? configured.VisionAdvice(SenseRoute(SenseKind.Image, configured))
        : LiveConversationConfiguration.VisionAdvice(null);

    /// <summary>Whether the image model should describe <paramref name="shot"/> now for <paramref name="why"/> (the talk window asks
    /// before it encodes the picture): the path is Described and <see cref="PictureDescriptions.Wants"/> says so.</summary>
    internal bool WantsDescription(PictureShot shot, PictureTrigger why, bool active) => ImageDescribed && Pictures.Wants(shot, why, active);

    /// <summary>Has the image model describe <paramref name="seen"/> ahead of time, in the background, so a reply can take the
    /// description without waiting. Nothing happens unless the path is Described and the picture needs one.</summary>
    internal void DescribeAhead(SeenScreen seen, PictureTrigger why)
    {
        ArgumentNullException.ThrowIfNull(seen);
        if (seen.Picture <= 0 || !ImageDescribed) return;
        var shot = seen.Shot();
        if (!Pictures.Wants(shot, why)) return;
        IReadOnlyList<TextHistoryMessage> lately;
        lock (gate) lately = context.Snapshot();
        Pictures.Describe(shot, seen.Image, Configuration?.Prompts, lately, why, RunPictureJob).Forget();
    }

    private Task<SenseJobResult> RunPictureJob(SenseJob job, CancellationToken token) => RunSenseAsync(SenseKind.Image, job, token);

    /// <summary>The description a reply takes for its picture now, or null: never waits. A look at something that wants the
    /// user's attention needs a description of that exact picture.</summary>
    private PictureDescription? DescriptionFor(LiveConversationOperation operation) =>
        operation is { ImagePath: SensePath.Described, Seen: { Picture: > 0 } seen } ? Pictures.For(seen.Shot(), exact: operation.Attention is not null) : null;

    /// <summary>A look's first stage: the image model's description of the look's own picture (one already made of that exact
    /// picture, the one being made, or a new one).</summary>
    private Task<PictureDescribed> DescribeLookAsync(SeenScreen looked, PromptSettings? prompts, CancellationToken token)
    {
        IReadOnlyList<TextHistoryMessage> lately;
        lock (gate) lately = context.Snapshot();
        // A look without a picture version (it came from elsewhere) gets one of its own, so it is described once.
        var picture = looked.Picture > 0 ? looked : looked with { Picture = PictureDescriptions.NewVersion(), TakenAt = clock.GetUtcNow() };
        return Pictures.ForLookAsync(picture.Shot(), picture.Image, prompts, lately, RunPictureJob, token);
    }

    /// <summary>Lets every description go and stops the ones being made (the conversation was cleared, or watching stopped).</summary>
    internal void ForgetPictures()
    {
        pictures?.Forget();
        QueuePictureStatus();
    }

    // A reply with a picture on the Described path took a description, or went without: the log line MCP's image_model_check
    // reads (times only), and the counts.
    private void NotePicturePath(LiveConversationOperation operation)
    {
        if (operation.ImagePath != SensePath.Described || operation.Seen is not { } seen) return;
        var now = clock.GetUtcNow();
        if (operation.Described is { } described)
        {
            Interlocked.Increment(ref describedReplies);
            var age = Math.Max(0, (now - described.At).TotalSeconds);
            lastReplyPicture = (now, true, age);
            ErrorLog.Info($"Picture path: described (the reply took the image model's description of {Kind(seen)}, made {age:0.0} s earlier).");
        }
        else
        {
            Interlocked.Increment(ref repliesWithout);
            lastReplyPicture = (now, false, null);
            ErrorLog.Info($"Picture path: described, but no description of {Kind(seen)} was ready, so the reply went without the picture.");
        }
        QueuePictureStatus();
    }

    // A look's first stage ended.
    private void NoteLookPicture(PictureDescribed described)
    {
        if (described.Description is null) Interlocked.Increment(ref failedLooks);
        else if (described.Reused) Interlocked.Increment(ref reusedLooks);
        else Interlocked.Increment(ref describedLooks);
        QueuePictureStatus();
    }

    // What a picture shows, for the log: never a window title or a camera's name.
    private static string Kind(SeenScreen seen) => seen.Source.Kind switch
    {
        WatchKind.ActiveWindow => "your active window",
        WatchKind.ActiveScreen => "your whole screen",
        _ => "the camera picture"
    };

    private static string Kind(PictureShot shot) => shot.Camera ? "the camera picture"
        : shot.Source.StartsWith(nameof(WatchKind.ActiveScreen), StringComparison.Ordinal) ? "your whole screen" : "your active window";

    // One description job ended: the desktop log says how long it took and why it ran (never its words).
    private void PictureEnded(PictureDescribed ended)
    {
        var why = ended.Why switch
        {
            PictureTrigger.Talking => "you talked or typed",
            PictureTrigger.Look => "for a look",
            _ => "the picture changed"
        };
        var result = ended.Result;
        if (ended.Description is { } description)
        {
            Interlocked.Increment(ref picturesDescribed);
            ErrorLog.Info($"Image model: described {Kind(ended.Shot)} in {result.Took.TotalSeconds:0.0} s ({why}; {description.Model}).");
        }
        else if (result.Outcome is not SenseJobOutcome.NoModel && result.Problem != "the conversation was cleared")
        {
            if (result.Outcome is SenseJobOutcome.Failed or SenseJobOutcome.TimedOut or SenseJobOutcome.Refused) Interlocked.Increment(ref picturesFailed);
            ErrorLog.Info($"Image model: no description of {Kind(ended.Shot)} ({result.Outcome}: {result.Problem ?? "no words"}; {why}).");
        }
        QueuePictureStatus();
    }

    private void QueuePictureStatus()
    {
        if (dataDirectory is null || Interlocked.Exchange(ref pictureStatusPending, 1) == 1) return;
        Task.Run(() =>
        {
            Volatile.Write(ref pictureStatusPending, 0);
            WritePictureStatus();
        }).Forget();
    }

    private void WritePictureStatus()
    {
        if (dataDirectory is null || disposed) return;
        try
        {
            var path = Path.Combine(dataDirectory, PictureStatusFile);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temporary, PictureStatusJson());
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ObjectDisposedException) { }
    }

    /// <summary>The image model's work now, for the talk window: the newest description's age, how long it took and which model
    /// made it, and whether the last reply with a picture took one. Never the words.</summary>
    internal (TimeSpan? Age, TimeSpan? Took, string? Model, bool? LastReplyTook) PictureNow
    {
        get
        {
            var now = clock.GetUtcNow();
            var newest = pictures?.Now().Newest;
            return (newest is null ? null : now - newest.At, newest?.Took, newest?.Model, lastReplyPicture?.Took);
        }
    }

    /// <summary>What image-model-status.json says now.</summary>
    internal string PictureStatusJson()
    {
        var route = ImageRoute;
        var (newest, running) = pictures?.Now() ?? (null, 0);
        var now = clock.GetUtcNow();
        var last = lastReplyPicture;
        return JsonSerializer.Serialize(new
        {
            schemaVersion = 1, updated = now, path = route.Path.ToString(), model = route.Model?.Describe(), unknown = route.Unknown,
            why = route.Why, sharesConversation = SenseSharesConversation(SenseKind.Image),
            descriptions = new
            {
                made = Volatile.Read(ref picturesDescribed), failed = Volatile.Read(ref picturesFailed), running,
                newest = newest is null ? null : new
                {
                    ageSeconds = Math.Round((now - newest.At).TotalSeconds, 1), tookMs = Math.Round(newest.Took.TotalMilliseconds),
                    trigger = newest.Why.ToString(), model = newest.Model, source = Kind(newest.Shot)
                }
            },
            replies = new { described = Volatile.Read(ref describedReplies), without = Volatile.Read(ref repliesWithout) },
            looks = new { described = Volatile.Read(ref describedLooks), reused = Volatile.Read(ref reusedLooks), failed = Volatile.Read(ref failedLooks) },
            lastReply = last is { } reply ? new { at = reply.At, tookDescription = reply.Took, descriptionAgeSeconds = reply.Age is { } age ? Math.Round(age, 1) : (double?)null } : null
        }, new JsonSerializerOptions { WriteIndented = true });
    }
}
