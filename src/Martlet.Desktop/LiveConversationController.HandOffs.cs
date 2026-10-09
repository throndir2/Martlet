using Martlet.Conversation;
using Martlet.Providers;

namespace Martlet.Desktop;

// Reply tools handed off to the check-ins that run after each exchange (docs/CONVERSATION.md#check-ins): the live reply model
// is offered fewer tools and makes fewer tool rounds, and a Thinking pool member does that work right after the reply.
internal sealed partial class LiveConversationController
{
    /// <summary>Raised (off the UI thread) once a reply ended and its exchange is in the conversation: after its voice played,
    /// never for a [pass] or a reply that failed or was stopped. The main window fires the
    /// <see cref="CheckInTriggers.ExchangeEnded"/> check-ins with it.</summary>
    internal event Action? ExchangeEnded;

    /// <summary>Raised (off the UI thread) when what a check-in should do after an exchange didn't happen, with why in a few
    /// words; once for each new problem (<see cref="ReportAfterExchange"/>).</summary>
    internal event Action<string>? AfterExchangeFailed;

    private string? lastAfterExchangeProblem;

    /// <summary>The saved check-in choices that hand reply tools off, set by the main window on a companion PC (where check-ins
    /// run); null where they don't run, so the reply keeps every tool.</summary>
    internal CheckInSettings? CheckInChoices { get; set; }

    /// <summary>Whether the configured Thinking pool has a member that takes check-ins and calls tools, whether its computer
    /// answers now or not (saved settings only). Kept until the pool or the configuration changes, so a reply doesn't read the
    /// pool's files again.</summary>
    internal bool PoolCallsTools()
    {
        var pool = Volatile.Read(ref thinkingPool);
        var configuration = Configuration;
        if (poolCallsTools is { } known && ReferenceEquals(known.Pool, pool) && ReferenceEquals(known.Configuration, configuration))
            return known.Calls;
        var calls = ThinkingPool.Board.Members.Any(m => m.Takes(ThinkingJobKind.CheckIn) && m.Can.HasFlag(ThinkingCapability.Tools));
        poolCallsTools = (pool, configuration, calls);
        return calls;
    }

    private (object Pool, object? Configuration, bool Calls)? poolCallsTools;

    /// <summary>The reply tools handed off now, each with its check-in and set (<see cref="CheckIns.HandOffs"/>); empty while
    /// check-ins don't run here or no configured pool member calls tools.</summary>
    internal IReadOnlyList<CheckInHandOff> HandOffs(LiveConversationConfiguration configured) =>
        CheckInChoices is { } choices ? CheckIns.HandOffs(choices, configured.Prompts, PoolCallsTools) : [];

    /// <summary>The names of the reply tools the check-ins take over now (<see cref="HandOffs"/>): BuiltIns doesn't offer them to
    /// the reply. From saved settings only, so the start of every request stays the same while computers come and go.</summary>
    internal IReadOnlySet<string> HandedOffReplyTools(LiveConversationConfiguration configured) =>
        HandOffs(configured).Select(h => h.Tool).ToHashSet(StringComparer.Ordinal);

    /// <summary>The conversation says once that what a check-in should do after an exchange didn't happen
    /// (<paramref name="problem"/>, a few words); the same problem again stays in the log until one works
    /// (<paramref name="problem"/> null).</summary>
    internal void ReportAfterExchange(string? problem)
    {
        lock (gate)
        {
            if (problem == lastAfterExchangeProblem) return;
            lastAfterExchangeProblem = problem;
        }
        if (problem is not null) AfterExchangeFailed?.Invoke(problem);
    }

    // Leaves out the tools handed off (and adds what the reply is told instead); null when nothing is left to say.
    private static BuiltInTools? WithoutHandedOff(BuiltInTools? tools, IReadOnlyList<CheckInHandOff> handed, string? guidance)
    {
        if (handed.Count == 0) return tools;
        var names = handed.Select(h => h.Tool).ToHashSet(StringComparer.Ordinal);
        var kept = tools?.Tools.Where(t => !names.Contains(t.Definition.Name)).ToArray() ?? [];
        var said = guidance is null ? tools?.Guidance : tools?.Guidance is { } before ? before + "\n\n" + guidance : guidance;
        return kept.Length == 0 && said is null ? null : new(kept, said);
    }
}
