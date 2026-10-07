using Martlet.Conversation;

namespace Martlet.Desktop;

/// <summary>Screen summaries as Thinking pool digest jobs: a member that sees, never the conversation's own route
/// (<see cref="ThinkingPool"/>). The job waits for a free member and runs at most <see cref="Timeout"/>, so its answer comes
/// before <see cref="ScreenDigestTiming.Stale"/>; a job no member took in time is dropped.</summary>
internal sealed class PoolScreenDigestThinker(Func<ThinkingPool> pool) : IScreenDigestThinker
{
    internal static TimeSpan Timeout => TimeSpan.FromSeconds(15);
    internal const string Instructions =
        "You write short private notes for a companion app about what changed on the user's screen. Answer only with the note.";
    private static readonly ThinkingCapability Needs = ThinkingCapability.Text | ThinkingCapability.Vision;

    public bool CanSee => pool().CanRun(ThinkingJobKind.Digest, Needs);

    public async Task<string?> DigestAsync(ScreenDigestJob job, CancellationToken cancellation)
    {
        var result = await pool().RunAsync(new ThinkingJob
        {
            Kind = ThinkingJobKind.Digest,
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
            ThinkingJobOutcome.Stale or ThinkingJobOutcome.TimedOut => throw new OperationCanceledException(result.Problem),
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
