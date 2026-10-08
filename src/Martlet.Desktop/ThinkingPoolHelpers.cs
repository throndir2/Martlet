using Martlet.Conversation;

namespace Martlet.Desktop;

/// <summary>Helper jobs (<see cref="HelperJobs"/>) on the Thinking pool: memory goes as a Memory job, emote naming as a Naming job
/// and touch zones as a TouchZones job (needs vision), each with the pool's priority for its kind. Measuring the eyes needs vision
/// too, so it goes as a TouchZones job, but at the helpers' low priority. A job the pool can't finish (no member, none free in
/// time, failed or timed out) returns null, so the conversation's own Thinking model does it after the reply, as before.</summary>
internal sealed class ThinkingPoolHelpers(Func<ThinkingPool> pool) : IHelperJobPool
{
    internal static ThinkingJobKind Kind(HelperJobKind kind) => kind switch
    {
        HelperJobKind.Memory => ThinkingJobKind.Memory,
        HelperJobKind.ActionNaming or HelperJobKind.Temperament => ThinkingJobKind.Naming,
        _ => ThinkingJobKind.TouchZones
    };

    /// <summary>The pool priority of a helper kind when it isn't its pool kind's own: the eyes wait like the other helpers.</summary>
    internal static ThinkingPriority? Priority(HelperJobKind kind) => kind == HelperJobKind.Eyes ? ThinkingPriority.Helper : null;

    internal static ThinkingCapability Needs(HelperCapability capability) =>
        capability == HelperCapability.Vision ? ThinkingCapability.Text | ThinkingCapability.Vision : ThinkingCapability.Text;

    // The pool has no kind for "any helper": a member that can take one text kind can take them all.
    public bool Has(HelperCapability capability) =>
        pool().CanRun(capability == HelperCapability.Vision ? ThinkingJobKind.TouchZones : ThinkingJobKind.Memory, Needs(capability));

    public async Task<HelperPoolAnswer?> TryRunAsync(HelperJob job, CancellationToken token)
    {
        var result = await pool().RunAsync(new ThinkingJob
        {
            Kind = Kind(job.Kind),
            Priority = Priority(job.Kind),
            Instructions = job.Input.Personality ?? "",
            Text = job.Input.UserText,
            Image = job.Input.Image,
            Needs = Needs(job.Capability),
            // The owner waits for touch zones and a vision model reads a picture first; memory and naming wait in line.
            Timeout = job.Kind == HelperJobKind.TouchZones ? TimeSpan.FromMinutes(3) : TimeSpan.FromMinutes(5),
            DropWhenStale = true,
            MaxOutputTokens = job.Kind == HelperJobKind.Memory ? 1_024 : 4_096
        }, token).ConfigureAwait(false);
        if (result.Succeeded) return new(result.Model is { } model ? $"{result.Member} ({model})" : result.Member ?? "a pool member", result.Text, null);
        if (result.Outcome != ThinkingJobOutcome.NoMember)
            ErrorLog.Info($"{job.Purpose}: the Thinking pool didn't do it ({result.Outcome}: {result.Problem}).");
        return null;
    }

    public override string ToString() => nameof(ThinkingPoolHelpers);
}
