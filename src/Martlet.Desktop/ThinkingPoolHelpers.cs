using Martlet.Conversation;

namespace Martlet.Desktop;

/// <summary>Helper jobs (<see cref="HelperJobs"/>) on the Thinking pool, each as the pool kind whose row in
/// <see cref="ThinkingJobKinds.Rows"/> lists it: remembering, tidying memory and the memory brief as Memory jobs, learning names, emote naming and
/// touch temperament as Naming jobs, and touch zones, measuring the eyes and writing a character as TouchZones jobs. A job the
/// pool can't finish (no member, none free in time, failed or timed out) returns null, so the conversation's own Thinking model
/// does it after the reply, as before.</summary>
internal sealed class ThinkingPoolHelpers(Func<DesktopThinkingPool> pool) : IHelperJobPool
{
    internal static ThinkingJobKind Kind(HelperJobKind kind) => ThinkingJobKinds.Helper(kind).Pool.Kind;

    /// <summary>The pool priority of a helper kind when it isn't its pool kind's own: the eyes wait like the other helpers, and
    /// the memory brief waits behind everything (it is never urgent).</summary>
    internal static ThinkingPriority? Priority(HelperJobKind kind) => ThinkingJobKinds.Helper(kind).Helper.Priority;

    internal static ThinkingCapability Needs(HelperCapability capability) =>
        capability == HelperCapability.Vision ? ThinkingCapability.Text | ThinkingCapability.Vision : ThinkingCapability.Text;

    // The pool has no kind for "any helper": a member that can take one text kind can take them all.
    public bool Has(HelperCapability capability) =>
        pool().CanRun(capability == HelperCapability.Vision ? ThinkingJobKind.TouchZones : ThinkingJobKind.Memory, Needs(capability));

    public bool CallsTools(HelperJobKind kind) => pool().CanRun(Kind(kind), ThinkingCapability.Text | ThinkingCapability.Tools);

    public async Task<HelperPoolAnswer?> TryRunAsync(HelperJob job, CancellationToken token)
    {
        var result = await pool().RunAsync(new ThinkingJob
        {
            Kind = Kind(job.Kind),
            Label = job.Purpose,
            Priority = Priority(job.Kind),
            Instructions = job.Input.Personality ?? "",
            Text = job.Input.UserText,
            Image = job.Input.Image,
            Needs = Needs(job.Capability),
            // Only a member that calls tools takes a job with tools (ThinkingJob.Required).
            Tools = job.Tools?.Definitions ?? [],
            ToolHost = job.Tools?.Host,
            MaxToolRounds = job.Tools?.Rounds ?? 0,
            // The owner waits for touch zones and a vision model reads a picture first; memory and naming wait in line.
            Timeout = job.Kind == HelperJobKind.TouchZones ? TimeSpan.FromMinutes(3) : TimeSpan.FromMinutes(5),
            DropWhenStale = true,
            MaxOutputTokens = job.Kind switch
            {
                HelperJobKind.Memory or HelperJobKind.MemoryBrief or HelperJobKind.SpeakerNaming => 1_024,
                HelperJobKind.MemoryUpkeep => 2_048,
                _ => 4_096
            }
        }, token).ConfigureAwait(false);
        if (result.Succeeded) return new(result.Model is { } model ? $"{result.Member} ({model})" : result.Member ?? "a pool member", result.Text, null);
        if (result.Outcome != ThinkingJobOutcome.NoMember)
            ErrorLog.Info($"{job.Purpose}: the Thinking pool didn't do it ({result.Outcome}: {result.Problem}).");
        return null;
    }

    public override string ToString() => nameof(ThinkingPoolHelpers);
}
