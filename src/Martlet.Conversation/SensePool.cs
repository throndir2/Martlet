using Martlet.Core.Cluster;
using Martlet.Core.Settings;
using Martlet.Providers;

namespace Martlet.Conversation;

/// <summary>What one attempt of a sense job on one pool member got: the model's answer, or why the member passed the job on to
/// the next one (<see cref="Refusal"/>: busy, unavailable, or its computer keeps its graphics card for a live conversation).</summary>
public sealed record SenseAttempt(SenseAnswer Answer, WorkRefusal Refusal = WorkRefusal.None)
{
    /// <summary>The member answered (words, a refusal of the picture or recording, or a real failure): the job ends here.</summary>
    public static SenseAttempt Done(SenseAnswer answer) => new(answer);

    /// <summary>The member does another job now: try the next member, then wait for whichever frees first.</summary>
    public static SenseAttempt Busy(string problem) => new(SenseAnswer.Failed(problem), WorkRefusal.Busy);

    /// <summary>The member can't take it now (offline, not reachable, no room beside Thinking, its provider limits requests).</summary>
    public static SenseAttempt Unavailable(string problem) => new(SenseAnswer.Failed(problem), WorkRefusal.Unavailable);

    /// <summary>The member's computer keeps its graphics card for a live conversation turn: another member takes the job, or it
    /// ends without one.</summary>
    public static SenseAttempt Held(string problem) => new(SenseAnswer.Failed(problem), WorkRefusal.Preempted);

    public override string ToString() => $"{nameof(SenseAttempt)} {Refusal}";
}

/// <summary>How one sense job went through its pool: the member that answered (<see cref="Member"/>, its key, and
/// <see cref="Name"/>; null when none took it), its place in the order (0 is the chosen model), how many tries found a member
/// busy or unavailable first, and how long the job waited for a member.</summary>
public sealed record SensePoolRoute(string? Member, string? Name, int Position, int Busy, int Unavailable, TimeSpan Waited)
{
    /// <summary>Whether a member other than the chosen model took the job.</summary>
    public bool Elsewhere => Member is not null && Position > 0;
}

/// <summary>The image and audio models' pools (docs/SENSE_MODELS.md, The image and audio pools). A sense job goes to the chosen
/// model first. When that model is busy (another companion PC's job, its computer's own pool work), its computer doesn't answer,
/// has no room beside Thinking or keeps its graphics card for a live turn, the job goes to the next member whose model sees (for
/// pictures) or hears (for recordings). When every member is busy the job waits for whichever frees first, until its timeout.
/// Requests go through <see cref="WorkQueue"/>, so a free first member costs nothing extra: no request, file or wait is added.
/// The members after the chosen model are the Thinking pool's members that take the kind (Companion › Thinking pool), until the
/// image and audio models get lists of their own. A member outside this PC and your paired computers gets pictures and
/// recordings only when the owner ticked May receive pictures and recordings for it. The conversation's own Thinking model is
/// never a member, so the conversation keeps its prompt cache.</summary>
public static class SensePool
{
    /// <summary>The most members a job tries.</summary>
    public const int MaximumMembers = 8;

    /// <summary>The <see cref="WorkQueue"/> lane of <paramref name="kind"/>'s jobs: <c>vision</c> or <c>hearing</c>.</summary>
    public static string Lane(SenseKind kind) => kind == SenseKind.Image ? "vision" : "hearing";

    /// <summary>The members a job of <paramref name="kind"/> tries, first to last: <paramref name="chosen"/> (the image or audio
    /// model of its own), then <paramref name="pool"/>'s members whose model is known to see or hear (<see cref="Takes"/>),
    /// in the pool's order. Left out: a member twice, the conversation's own Thinking model (<paramref name="thinking"/>), an
    /// external member the owner didn't allow to receive pictures and recordings, and what <paramref name="leaveOut"/> says
    /// (the desktop: a computer a friend shares, a computer kept for other companion PCs, the conversation's own computer and
    /// graphics card).</summary>
    public static IReadOnlyList<DeepThinkingSettings> Members(SenseKind kind, DeepThinkingSettings chosen, ThinkingPoolSettings? pool,
        SetupRoute? thinking, ModelAbilities? abilities, Func<DeepThinkingSettings, bool>? leaveOut = null)
    {
        ArgumentNullException.ThrowIfNull(chosen);
        List<DeepThinkingSettings> members = [chosen];
        if (pool is null) return members;
        foreach (var candidate in pool.Members)
        {
            if (members.Count >= MaximumMembers) break;
            var member = candidate.Single;
            if (!member.Separate || members.Any(m => m.Key == member.Key) || !Takes(kind, member, abilities) || !pool.MayReceiveMedia(member) ||
                SenseRouting.IsThinking(member, thinking) || leaveOut?.Invoke(member) == true)
                continue;
            members.Add(member);
        }
        return members;
    }

    /// <summary>Whether <paramref name="member"/>'s model is known to see (an image job) or hear (an audio job): what Martlet found
    /// out about it, then its name (<see cref="SenseRouting.Sees"/>, <see cref="SenseRouting.Hears"/>). A model Martlet can't
    /// tell about isn't a member: only the chosen model is tried without knowing.</summary>
    public static bool Takes(SenseKind kind, DeepThinkingSettings member, ModelAbilities? abilities)
    {
        ArgumentNullException.ThrowIfNull(member);
        return kind == SenseKind.Image
            ? SenseRouting.Sees(member, abilities) == VisionSupport.Supported
            : SenseRouting.Hears(member, abilities) == HearingSupport.Supported;
    }

    /// <summary>Runs one job of <paramref name="kind"/> on the first of <paramref name="members"/> that takes it
    /// (<see cref="WorkQueue.RunAsync"/>, lane <see cref="Lane"/>): <paramref name="attempt"/> sends it to one member. A member that
    /// is busy is passed over for the next; when every member that answered is busy the job waits for whichever frees first, until
    /// <paramref name="until"/>. A member that is unavailable or keeps its graphics card for a live turn is passed over. The answer
    /// is the member's, or the last member's reason when none took it. Canceling <paramref name="token"/> throws
    /// <see cref="OperationCanceledException"/>.</summary>
    public static async Task<(SenseAnswer Answer, SensePoolRoute Route)> RunAsync(SenseKind kind, IReadOnlyList<DeepThinkingSettings> members,
        Func<DeepThinkingSettings, CancellationToken, Task<SenseAttempt>> attempt, DateTimeOffset until, TimeProvider? clock,
        CancellationToken token, WorkQueue? queue = null)
    {
        ArgumentNullException.ThrowIfNull(members);
        ArgumentNullException.ThrowIfNull(attempt);
        if (members.Count == 0) throw new ArgumentException("A sense job needs at least one model.", nameof(members));
        var time = clock ?? TimeProvider.System;
        var began = time.GetTimestamp();
        var started = began;
        int busy = 0, unavailable = 0;
        DeepThinkingSettings? served = null;
        // Waited: from the job's start to the start of the attempt that took it (or to the end, when none took it).
        SensePoolRoute Route() => new(served?.Key, served?.Describe(), served is null ? -1 : IndexOf(members, served), busy, unavailable,
            served is null ? time.GetElapsedTime(began) : time.GetElapsedTime(began, started));
        try
        {
            var answer = await (queue ?? WorkQueue.Shared).RunAsync(Lane(kind), members, m => m.Key, async (member, t) =>
            {
                started = time.GetTimestamp();
                var tried = await attempt(member, t).ConfigureAwait(false);
                switch (tried.Refusal)
                {
                    case WorkRefusal.None:
                        served = member;
                        return tried.Answer;
                    case WorkRefusal.Busy:
                        busy++;
                        break;
                    default:
                        unavailable++;
                        break;
                }
                throw new PassedOn(tried);
            }, error => error is PassedOn passed ? passed.Attempt.Refusal : WorkRefusal.None, until, time, token,
                WorkPriority.Background).ConfigureAwait(false);
            return (answer, Route());
        }
        catch (PassedOn last) when (!token.IsCancellationRequested)
        {
            return (last.Attempt.Answer, Route());
        }
        catch (WorkPreemptedException) when (!token.IsCancellationRequested)
        {
            // Every member that answered keeps its graphics card for a live conversation turn: the job ends without words.
            return (SenseAnswer.Failed(members.Count == 1 ? $"{members[0].Describe()} keeps its graphics card for a live conversation"
                : "every model that could take it keeps its graphics card for a live conversation"), Route());
        }
    }

    private static int IndexOf(IReadOnlyList<DeepThinkingSettings> members, DeepThinkingSettings member)
    {
        for (var i = 0; i < members.Count; i++)
            if (ReferenceEquals(members[i], member)) return i;
        return -1;
    }

    // A member passed the job on (busy, unavailable, held for a live turn); the queue tries the next.
    private sealed class PassedOn(SenseAttempt attempt) : Exception("The model passed the sense job on.")
    {
        public SenseAttempt Attempt { get; } = attempt;
    }
}
