using Martlet.Conversation;
using Martlet.Providers;

namespace Martlet.Desktop;

// Background work a check-in starts after an exchange (the background-work check-in tool set, BackgroundWorkTools): the same
// think and research job as the reply's think_longer and research, continuing the last exchange's request and brought up the
// same way when done (docs/CONVERSATION.md, Thinking longer and background work).
internal sealed partial class LiveConversationController
{
    /// <summary>The last exchange kept in the conversation: the request its reply sent and the reply as the conversation keeps it.</summary>
    private sealed record KeptExchange(BoundedTextInput? Sent, string Reply);

    private KeptExchange? lastKept;

    /// <summary>Whether a check-in can start background work now: Thinking longer is on and Deep thinking can think (the reply's
    /// think_longer is offered or handed off), from the settings only.</summary>
    internal bool OffersBackgroundWork => Configuration is { } configured && ReplyThinkTools(configured).Count > 0;

    /// <summary>Runs one call of the background-work tool set for the check-in <paramref name="checkIn"/>: think_longer or research
    /// with the current settings, continuing the last exchange. Returns at once; the work runs on the Thinking pool.</summary>
    internal ConversationToolResult StartBackgroundWork(TextToolCall call, string checkIn)
    {
        ArgumentNullException.ThrowIfNull(call);
        LiveConversationConfiguration? configured;
        KeptExchange? last;
        lock (gate) (configured, last) = (configuration, lastKept);
        if (configured is null || ReplyThinkTools(configured).Count == 0)
            return new(BackgroundWorkTools.NotStarted("Thinking longer is off or Deep thinking can't run"), true);
        if (last is null) return new(BackgroundWorkTools.NotStarted("there is no exchange to continue yet"), true);
        return call.Name switch
        {
            ThinkLonger.Name => StartThink(configured, last.Sent, () => last.Reply, toldUser: true, call, checkIn),
            WebResearch.Name when OffersResearch(configured) => StartResearch(configured, last.Sent, () => last.Reply, toldUser: true, call, checkIn),
            WebResearch.Name => new(BackgroundWorkTools.NotStarted("Web research is off"), true),
            _ => new($"This set has no tool called {call.Name}.", true)
        };
    }
}
