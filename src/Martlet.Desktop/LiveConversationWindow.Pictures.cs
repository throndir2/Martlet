using Martlet.Conversation;

namespace Martlet.Desktop;

// The talk window's side of the image model (docs/SENSE_MODELS.md, Pictures: the image model). Each screenshot gets a picture
// version that stays the same while nothing changes on it. While the image model describes pictures (the Described path), the
// newest picture is described ahead of time: while you talk or type, when a look is due, and when it changes while you talk with
// Martlet. A reply then takes the description without waiting for it.
public partial class LiveConversationWindow
{
    // The newest screenshot's key (and the one kept for a look at what wants your attention), as the image model gets them.
    private PictureShot? latestShot, attentionShot;
    // When you last typed in the message box (window clock; 0: not yet).
    private long typedAt;

    /// <summary>How long after your last keystroke typing still counts as talking to Martlet.</summary>
    internal static TimeSpan TypingCounts => TimeSpan.FromSeconds(5);

    /// <summary>A key for a new screenshot of what Martlet watches: the picture version of the screenshot before while nothing
    /// changed on it, else a new one.</summary>
    private PictureShot KeyFor(ScreenFrame frame)
    {
        var next = SeenScreen.ShotOf(watchSource.Kind == WatchKind.Url ? "" : frame.Title, watchSource, frame.App, frame.FullScreen, 0,
            clock.GetUtcNow());
        return next with { Version = latestShot is { } before && !PictureDescriptions.Changed(before, next, frame.Change) ? before.Version
            : PictureDescriptions.NewVersion() };
    }

    // You talk or type, or something you said or typed is about to be answered.
    private bool Talking => listener is { Hearing: true } || MicBusy || Recording || pendingText is not null || heardQueue.Count > 0 ||
        typedAt != 0 && clock.GetElapsedTime(typedAt) < TypingCounts;

    /// <summary>Tells the conversation's image and audio models (<see cref="LiveConversationController.PictureMoment"/>) about a
    /// picture moment: the newest picture (or, with <paramref name="attention"/>, the one kept for a look at what wants your
    /// attention), whether it is fresh, and how to encode it. The image model decides whether it describes the picture now.</summary>
    private void DescribeAhead(PictureTrigger why, bool attention = false)
    {
        var (frame, shot) = attention ? (attentionFrame, attentionShot) : (latestFrame, latestShot);
        if (frame is null) return;
        controller.PictureMoment(why, shot, attention || clock.GetElapsedTime(latestAt) <= SeenFreshness, SinceActivity,
            () => Seen(frame, shot!));
    }

    /// <summary>The start of the vision button's tooltip while Martlet watches: where its pictures go.</summary>
    private string WatchingTip() => controller.ImageDescribed
        ? $"Martlet checks {watchSource.Label}. Your image model, {controller.ImageRoute.Name}, describes the pictures in words for the " +
          "Thinking model: one now and then for a look, and the newest while you type or talk."
        : $"Martlet checks {watchSource.Label}, occasionally sends one picture to the Thinking model and sends the newest " +
          "with what you type or say.";

    /// <summary>The vision line's tooltip about the image model while it describes pictures: which model, how long its last
    /// description took and how long ago, and whether the last reply with a picture took one. Never the words.</summary>
    private string? ImageModelDetail()
    {
        if (!controller.ImageDescribed) return null;
        var (age, took, _, replyTook) = controller.PictureNow;
        var last = age is { } ago && took is { } time ? $"; its last description took {time.TotalSeconds:0.0} s, {Ago(ago)}" : "";
        return $"Image model: {controller.ImageRoute.Name} describes the pictures for Thinking{last}." + replyTook switch
        {
            true => " The last reply took its description.",
            false => " The last reply went without one: it wasn't ready yet.",
            _ => ""
        };
    }

    private static string Ago(TimeSpan age) => age.TotalSeconds < 2 ? "just now"
        : age.TotalMinutes < 2 ? $"{(int)age.TotalSeconds} s ago" : $"{(int)age.TotalMinutes} min ago";
}
