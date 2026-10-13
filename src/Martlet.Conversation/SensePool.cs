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

/// <summary>The image and audio models' pools (docs/SENSE_MODELS.md, The image and audio pools). Each kind has a list of its own
/// (Companion › Vision, Companion › Hearing; <see cref="SenseLists"/>). A sense job goes to the chosen model first: the first
/// model in the list that isn't known not to take the kind (<see cref="Chosen"/>). When that model is busy (another companion
/// PC's job, its computer's own pool work), its computer doesn't answer, has no room beside Thinking or keeps its graphics card
/// for a live turn, the job goes to the next model in the list. When every member is busy the job waits for whichever frees
/// first, until its timeout. Requests go through <see cref="WorkQueue"/>, so a free first member costs nothing extra: no request,
/// file or wait is added. A member outside this PC and your paired computers gets pictures and recordings only with the owner's
/// agreement (<see cref="SenseLists.MayReceive"/>). The conversation's own Thinking model is never a member after the chosen
/// one, so the conversation keeps its prompt cache.</summary>
public static class SensePool
{
    /// <summary>The most members a job tries.</summary>
    public const int MaximumMembers = 8;

    /// <summary>The <see cref="WorkQueue"/> lane of <paramref name="kind"/>'s jobs: its pool area's ID (<see cref="PoolAreas.Vision"/>,
    /// <see cref="PoolAreas.Hearing"/>).</summary>
    public static string Lane(SenseKind kind) => SenseLists.Area(kind).Id;

    /// <summary>The model a job of <paramref name="kind"/> goes to first: the first of <paramref name="models"/> (the list's, in
    /// order) not known not to take the kind; the first one when every one is (its route then says why); null for an empty list
    /// (the text model takes it).</summary>
    public static DeepThinkingSettings? Chosen(SenseKind kind, IReadOnlyList<DeepThinkingSettings> models, ModelAbilities? abilities)
    {
        ArgumentNullException.ThrowIfNull(models);
        return models.FirstOrDefault(m => !Refuses(kind, m, abilities)) ?? models.FirstOrDefault();
    }

    /// <summary>The choices as <see cref="SenseRouting"/> reads them: for each kind its <see cref="Chosen"/> model of its own, or
    /// the text model for an empty list.</summary>
    public static SenseModels Senses(IReadOnlyList<DeepThinkingSettings> image, IReadOnlyList<DeepThinkingSettings> audio, ModelAbilities? abilities)
    {
        static SenseModel Of(DeepThinkingSettings? own) => own is null ? new() : new() { Source = SenseSource.Own, Own = own };
        return new() { Image = Of(Chosen(SenseKind.Image, image, abilities)), Audio = Of(Chosen(SenseKind.Audio, audio, abilities)) };
    }

    /// <summary>The members a job of <paramref name="kind"/> tries, first to last: <paramref name="chosen"/>, then the list's other
    /// models (<paramref name="others"/>, in order). Left out: a model twice, one known not to see or hear (<see cref="Refuses"/>),
    /// the conversation's own Thinking model (<paramref name="thinking"/>), and what <paramref name="leaveOut"/> says (the
    /// desktop: one on the conversation's own computer and graphics card).</summary>
    public static IReadOnlyList<DeepThinkingSettings> Members(SenseKind kind, DeepThinkingSettings chosen, IEnumerable<DeepThinkingSettings>? others,
        SetupRoute? thinking, ModelAbilities? abilities, Func<DeepThinkingSettings, bool>? leaveOut = null)
    {
        ArgumentNullException.ThrowIfNull(chosen);
        List<DeepThinkingSettings> members = [chosen];
        foreach (var candidate in others ?? [])
        {
            if (members.Count >= MaximumMembers) break;
            var member = candidate.Single;
            if (!member.Separate || members.Any(m => m.Key == member.Key) || Refuses(kind, member, abilities) ||
                SenseRouting.IsThinking(member, thinking) || leaveOut?.Invoke(member) == true)
                continue;
            members.Add(member);
        }
        return members;
    }

    /// <summary>Whether <paramref name="member"/>'s model is known not to see (an image job) or hear (an audio job)
    /// (<see cref="ModelSenses"/>, <see cref="SenseUse.List"/>). The owner put it in the list, so a model Martlet can't tell about
    /// is tried, and a refusal is remembered.</summary>
    public static bool Refuses(SenseKind kind, DeepThinkingSettings member, ModelAbilities? abilities)
    {
        ArgumentNullException.ThrowIfNull(member);
        return !ModelSenses.For(member, abilities).Takes(kind, SenseUse.List);
    }

    /// <summary>Runs one job of <paramref name="kind"/> on the first of <paramref name="members"/> that takes it
    /// (<see cref="PoolRunner{TStop}"/> through <see cref="WorkQueue"/>, lane <see cref="Lane"/>, background priority):
    /// <paramref name="attempt"/> sends it to one member. A member that is busy is passed over for the next; when every member that
    /// answered is busy the job waits for whichever frees first, until <paramref name="until"/>. A member that is unavailable or
    /// keeps its graphics card for a live turn is passed over. The answer is the member's, or the last member's reason when none
    /// took it. Canceling <paramref name="token"/> throws <see cref="OperationCanceledException"/>.</summary>
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
        PassedOn? last = null;
        // When no member took the job: the busy and unavailable tries up to and with the last member that passed it on.
        SensePoolRoute NoneTook()
        {
            var (busy, unavailable) = last is { Tried: var tried, Attempt.Refusal: var refusal }
                ? (tried.Busy + (refusal == WorkRefusal.Busy ? 1 : 0), tried.Unavailable + (refusal == WorkRefusal.Busy ? 0 : 1)) : (0, 0);
            return new(null, null, -1, busy, unavailable, time.GetElapsedTime(began));
        }
        try
        {
            var run = await new PoolRunner<DeepThinkingSettings>(Lane(kind), m => m.Key,
                error => error is PassedOn passed ? passed.Attempt.Refusal : WorkRefusal.None, WorkPriority.Background, queue, time)
                .RunAsync(members, async (member, tried, t) =>
                {
                    started = time.GetTimestamp();
                    var answer = await attempt(member, t).ConfigureAwait(false);
                    if (answer.Refusal == WorkRefusal.None) return answer.Answer;
                    throw last = new PassedOn(answer, tried);
                }, until, token).ConfigureAwait(false);
            // Waited: from the job's start to the start of the attempt that took it.
            return (run.Result, new(run.Stop.Key, run.Stop.Describe(), run.Position, run.Busy, run.Unavailable,
                time.GetElapsedTime(began, started)));
        }
        catch (PassedOn passed) when (!token.IsCancellationRequested)
        {
            return (passed.Attempt.Answer, NoneTook());
        }
        catch (WorkPreemptedException) when (!token.IsCancellationRequested)
        {
            // Every member that answered keeps its graphics card for a live conversation turn: the job ends without words.
            return (SenseAnswer.Failed(members.Count == 1 ? $"{members[0].Describe()} keeps its graphics card for a live conversation"
                : "every model that could take it keeps its graphics card for a live conversation"), NoneTook());
        }
    }

    // A member passed the job on (busy, unavailable, held for a live turn); the queue tries the next.
    private sealed class PassedOn(SenseAttempt attempt, PoolTry tried) : Exception("The model passed the sense job on.")
    {
        public SenseAttempt Attempt { get; } = attempt;
        public PoolTry Tried { get; } = tried;
    }
}
