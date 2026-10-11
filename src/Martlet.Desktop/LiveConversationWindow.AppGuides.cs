using Martlet.Conversation;
using Martlet.Conversation.Guides;

namespace Martlet.Desktop;

/// <summary>App guides in the talk window (docs/APP_GUIDES.md): reading up on its own on a game that came to the front, and
/// reading up started from Companion › App guides, while this conversation runs (its task list shows "Reading up on").</summary>
public partial class LiveConversationWindow
{
    /// <summary>Martlet reads up on <paramref name="offer"/> (a game without a guide in front) on its own as this conversation's
    /// job, and tells you as soon as it is free, or with what you say next. Only in a conversation that runs; null otherwise (the
    /// main window reads up by itself then).</summary>
    internal BackgroundJobStart? ReadUpOnItsOwn(AppGuideService guides, AppGuideOfferCandidate offer)
    {
        if (closed || !begun) return null;
        var start = controller.ReadUpOnItsOwn(guides, offer);
        RenderActions();
        return start;
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
