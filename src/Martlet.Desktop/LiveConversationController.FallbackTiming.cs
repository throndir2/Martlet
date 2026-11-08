using Martlet.Conversation;
using Martlet.Core.Planning;
using Martlet.Core.Settings;

namespace Martlet.Desktop;

// If Thinking fails, timed (ThinkingFallbackTimer; docs/CONVERSATION.md, If Thinking fails): a cloud Thinking model with a fallback
// has a first-token wait from its own measured times, and after it failed in the last replies the next replies ask the fallback
// first for a while. It owns the timer; replies get its plan before they start and give it their result after they end, never
// on their path. A Thinking route on this PC or the home network keeps its own long limit (the model may be loading).
// Tests: ThinkingFallbackTimerTests (Martlet.Conversation.Tests); MCP: thinking_fallback_timing.
internal sealed partial class LiveConversationController
{
    private readonly ThinkingFallbackTimer fallbackTimer = new();

    /// <summary>The first-token timer of If Thinking fails.</summary>
    internal ThinkingFallbackTimer FallbackTimer => fallbackTimer;

    /// <summary>The timer's key for <paramref name="configured"/>'s Thinking route (its server and model), or null when the
    /// timer doesn't apply: no If Thinking fails, or Thinking runs on this PC or the home network.</summary>
    internal static string? FallbackTimingKey(LiveConversationConfiguration configured) =>
        configured.NetworkThinking ? null : ThinkingFallbackTimer.KeyFor(configured.Routes.ThinkingRoute(), configured.Fallback is not null);

    private ThinkingFallbackPlan? FallbackPlanFor(LiveConversationConfiguration configured) =>
        FallbackTimingKey(configured) is { } key ? fallbackTimer.Plan(key, clock.GetUtcNow()) : null;

    // A finished reply's first token or failure; a new model first gets the times model-speed.json measured for it.
    private void NoteFallbackTiming(LiveConversationConfiguration configured, ConversationSnapshot terminal)
    {
        if (FallbackTimingKey(configured) is not { } key || configured.Routes.ThinkingRoute() is not { } route) return;
        if (fallbackTimer.Model != key && dataDirectory is { } directory &&
            MeasuredFirstWords.Load(directory).Find(route.Origin, route.ModelId) is { } measured)
            fallbackTimer.Seed(key, measured.Samples);
        var now = clock.GetUtcNow();
        var before = fallbackTimer.Plan(key, now).FallbackFirst;
        fallbackTimer.Noted(key, terminal, now);
        var after = fallbackTimer.Plan(key, now);
        if (after.FallbackFirst && !before)
            ErrorLog.Warn($"Thinking {route.ModelId} on {route.Origin} failed {fallbackTimer.Failures} replies in a row: " +
                $"replies ask If Thinking fails first for {ThinkingFallbackTimer.SkipFor.TotalMinutes:0} minutes, then try it again.");
        else if (before && !after.FallbackFirst)
            ErrorLog.Info($"Thinking {route.ModelId} on {route.Origin} answers again: replies ask it first.");
    }
}
