using Martlet.Conversation;
using Martlet.Core.Settings;

namespace Martlet.Desktop;

/// <summary>Screen summaries as Thinking pool jobs (<see cref="SenseJobKind.ScreenSummary"/>'s pool kind and needs in
/// <see cref="SenseJobRouting.Rows"/>): a member that sees, never the conversation's own route (<see cref="DesktopThinkingPool"/>).
/// The job waits for a free member and runs at most <see cref="Timeout"/>, so its answer comes
/// before <see cref="ScreenDigestTiming.Stale"/>; a job no member took in time is dropped.</summary>
internal sealed class PoolScreenDigestThinker(Func<DesktopThinkingPool> pool) : IScreenDigestThinker
{
    internal static TimeSpan Timeout => ScreenDigestTiming.JobTime;
    internal const string Instructions =
        "You write short private notes for a companion app about what changed on the user's screen. Answer only with the note.";
    private static readonly SenseJobRow Row = SenseJobRouting.Row(SenseJobKind.ScreenSummary);
    private static readonly ThinkingJobKind Kind = Row.PoolKind!.Value;
    private static readonly ThinkingCapability Needs = Row.Needs;

    public bool CanSee => pool().CanRun(Kind, Needs);

    // While you talk with Martlet, only a member that shares no hardware with the conversation may take a summary (live floor).
    public bool MayStartNow => pool().Board.MayStartNow(Kind, Needs);

    public bool SeesBesideConversation => pool().Board.CanRunBeside(Kind, Needs);

    public async Task<string?> DigestAsync(ScreenDigestJob job, CancellationToken cancellation)
    {
        var result = await pool().RunAsync(new ThinkingJob
        {
            Kind = Kind,
            Label = "Summary of the screen",
            Instructions = Instructions,
            Text = job.Message,
            Image = job.Picture,
            Needs = Needs,
            Timeout = Timeout,
            DropWhenStale = true,
            MaxOutputTokens = 160,
            Reasoning = false
        }, cancellation).ConfigureAwait(false);
        return result.Outcome switch
        {
            ThinkingJobOutcome.Succeeded => result.Text,
            // A summary the live conversation stopped is only dropped, like one that got stale.
            ThinkingJobOutcome.Stale or ThinkingJobOutcome.TimedOut or ThinkingJobOutcome.Preempted => throw new OperationCanceledException(result.Problem),
            _ => throw new InvalidOperationException(result.Problem ?? "The Thinking pool could not make a screen summary.")
        };
    }
}

/// <summary>Screen summaries on the context board as source <see cref="ContextBoard.Screen"/>: each one replaces the one
/// before, and the board drops it once it is older than its maximum age.</summary>
internal sealed class BoardScreenDigest(ContextBoard board) : IScreenDigestBoard
{
    public void Post(string text, DateTimeOffset at, TimeSpan maximumAge) => board.Post(ContextBoard.Screen, text, at, maximumAge);
    public void Clear() => board.Clear(ContextBoard.Screen);
}

/// <summary>Screen summaries as <see cref="SenseJobRouting"/> decides them (<see cref="SenseJobKind.ScreenSummary"/>, the
/// controller's <see cref="LiveConversationController.SenseDecision"/>), step by step in its order. The image model (path Described,
/// docs/SENSE_MODELS.md) runs the same job as the pool's on its own lane with the lowest priority, so the next reply's picture goes
/// first; on the conversation's own computer and graphics card it starts only while the live floor is Idle. A Thinking pool member
/// runs it as a pool job (<paramref name="pool"/>). When the image model can't take it after all (pictures went back to Thinking
/// meanwhile), the next step takes it.</summary>
internal sealed class ImageModelScreenDigestThinker(LiveConversationController controller, IScreenDigestThinker pool) : IScreenDigestThinker
{
    /// <summary>The lane key of screen summaries: a newer one takes the place of one that waits.</summary>
    internal const string Key = "screen-summary";

    private SenseDecision Decision => controller.SenseDecision(SenseJobKind.ScreenSummary);
    private bool Shares => controller.SenseSharesConversation(SenseKind.Image);

    public bool CanSee => Decision.Taker != SenseTaker.Nobody;

    public bool MayStartNow => Decision.Taker switch
    {
        SenseTaker.SenseModel => !Shares || controller.LiveFloor.Level == LiveFloorLevel.Idle,
        SenseTaker.PoolMember => pool.MayStartNow,
        _ => false
    };

    public bool SeesBesideConversation => Decision.Taker switch
    {
        SenseTaker.SenseModel => !Shares,
        SenseTaker.PoolMember => pool.SeesBesideConversation,
        _ => false
    };

    public async Task<string?> DigestAsync(ScreenDigestJob job, CancellationToken cancellation)
    {
        foreach (var step in Decision.Steps.Where(s => s.Takes))
        {
            if (step.Taker == SenseTaker.PoolMember) return await pool.DigestAsync(job, cancellation).ConfigureAwait(false);
            if (step.Taker != SenseTaker.SenseModel) break;
            var result = await controller.RunSenseAsync(SenseKind.Image, new SenseJob
            {
                Purpose = "screen summary", Key = Key, Priority = 0, Instructions = PoolScreenDigestThinker.Instructions, Text = job.Message,
                Image = job.Picture, Timeout = PoolScreenDigestThinker.Timeout, DropWhenStale = true, MaxOutputTokens = 160, Reasoning = false
            }, cancellation).ConfigureAwait(false);
            switch (result.Outcome)
            {
                case SenseJobOutcome.Succeeded: return result.Text;
                // Pictures went back to Thinking meanwhile: the next step.
                case SenseJobOutcome.NoModel: continue;
                // A summary the conversation stopped, or that got stale, is only dropped.
                case SenseJobOutcome.Stale or SenseJobOutcome.TimedOut or SenseJobOutcome.Preempted: throw new OperationCanceledException(result.Problem);
                default: throw new InvalidOperationException(result.Problem ?? "The image model could not make a screen summary.");
            }
        }
        throw new InvalidOperationException("No model makes screen summaries now.");
    }
}
