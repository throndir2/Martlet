using Martlet.Conversation;
using Martlet.Conversation.Guides;

namespace Martlet.Desktop;

/// <summary>App guides in the talk window (docs/APP_GUIDES.md): the offer to read up on a game that came to the front, and
/// reading up started from Companion › App guides while this conversation runs (its task list shows "Reading up on").</summary>
public partial class LiveConversationWindow
{
    /// <summary>Martlet offers to read up on <paramref name="offer"/>: a notice it brings up as soon as it is free, or with what
    /// you say next. Only in a conversation that runs (never started for it); null otherwise.</summary>
    internal BackgroundJob? OfferGuide(AppGuideOfferCandidate offer)
    {
        if (closed || !begun) return null;
        var job = controller.OfferGuide(offer);
        if (job is not null) ErrorLog.Info($"App guides: {job.Id} waits; Martlet offers to read up on the app in front as soon as it's free.");
        RenderActions();
        return job;
    }

    /// <summary>Reads up on <paramref name="app"/> as this conversation's background job, or null when the conversation doesn't
    /// run (the page reads up by itself then).</summary>
    internal BackgroundJobStart? ReadUp(AppGuideService guides, string app, IReadOnlyList<string> sites)
    {
        if (closed || !begun) return null;
        var start = controller.StartReadingUp(guides, app, sites);
        RenderActions();
        return start;
    }
}
