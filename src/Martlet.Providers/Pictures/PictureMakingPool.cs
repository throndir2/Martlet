using System.Collections.Concurrent;
using System.Net.Http;
using System.Text.Json.Nodes;
using Martlet.Core.Cluster;
using Martlet.Core.Pictures;

namespace Martlet.Providers.Pictures;

/// <summary>One place in a <see cref="PictureMakingPool"/>: its ID (the pool member's key: "host:&lt;id&gt;", "address:&lt;url&gt;",
/// "cloud:&lt;provider&gt;/&lt;model&gt;"), the picture maker with that member's own settings (workflow, checkpoint or model) and,
/// for Martlet's pictures role, the paired computer it draws on.</summary>
public sealed record PictureMakingPoolMember(string Id, IPictureMaker Maker, string? HostId = null);

/// <summary>Where a pool's picture went: the member, its place in the order, how many members were busy or couldn't draw it
/// first, and, when every member was busy, how many pictures it waited behind in that member's ComfyUI queue.</summary>
public sealed record PictureMakingPoolRoute(string Id, int Position, int Busy, int Unavailable, int? QueuedBehind);

/// <summary>
/// Draws each picture on the first member of Pictures' ordered list (<see cref="PoolAreas.PictureMaking"/>) that is free
/// (docs/PICTURES.md#more-than-one-picture-computer), through the pool runner (<see cref="PoolRunner{TStop}.RunOrWaitInLineAsync"/>:
/// Martlet's queue for shared work, <see cref="WorkQueue"/>, lane
/// <see cref="Lane"/> and each member's key, background priority: pictures are never live work). A ComfyUI member whose queue
/// already holds pictures (another companion PC's, or this PC's) is busy and passed over for the next, and so is a cloud
/// provider that answers busy (429, 503). A member that doesn't answer, isn't ready, lacks the workflow's model files or
/// checkpoint, rejects the workflow (a custom node or model it doesn't have) or refuses its key can't draw it and is passed
/// over too. When every member that can draw is busy, the picture waits in the shortest ComfyUI queue among them (fewest
/// pictures ahead; first in the order on a tie) instead of asking again and again. With one member it draws there exactly as
/// that member does alone: no extra request. <see cref="Chosen"/> and <see cref="Route"/> say where it went;
/// <see cref="Placed"/> is raised when a member takes it.
/// </summary>
public sealed class PictureMakingPool : IPictureMaker, IDisposable
{
    /// <summary>The <see cref="WorkQueue"/> lane of pictures (<see cref="PoolAreas.PictureMaking"/>' ID).</summary>
    public static string Lane => PoolAreas.PictureMaking.Id;
    private static readonly TimeSpan Recheck = TimeSpan.FromSeconds(30);
    private readonly IReadOnlyList<PictureMakingPoolMember> members;
    private readonly WorkQueue queue;
    private readonly TimeProvider clock;
    private readonly ConcurrentDictionary<string, (PictureMakerAvailability Availability, long At)> checks = new(StringComparer.Ordinal);
    private PictureMakingPoolMember? chosen;
    private PictureMakingPoolRoute? route;

    public PictureMakingPool(IReadOnlyList<PictureMakingPoolMember> members, WorkQueue? queue = null, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(members);
        if (members.Count == 0) throw new ArgumentException("A picture pool needs at least one member.", nameof(members));
        if (members.Any(m => m is null || string.IsNullOrEmpty(m.Id) || m.Maker is null) ||
            members.Select(m => m.Id).Distinct(StringComparer.Ordinal).Count() != members.Count)
            throw new ArgumentException("Each member needs its own ID and a picture maker.", nameof(members));
        this.members = members;
        this.queue = queue ?? WorkQueue.Shared;
        this.clock = clock ?? TimeProvider.System;
    }

    /// <summary>The members, in the order a picture tries them.</summary>
    public IReadOnlyList<PictureMakingPoolMember> Members => members;

    /// <summary>The member that took the last picture; null before one did.</summary>
    public PictureMakingPoolMember? Chosen => Volatile.Read(ref chosen);

    /// <summary>How the last picture found its member; null before one did.</summary>
    public PictureMakingPoolRoute? Route => Volatile.Read(ref route);

    /// <summary>Raised when a member takes a picture (again, on another member, when the first stopped answering).</summary>
    public event Action<PictureMakingPoolMember>? Placed;

    public string Where => Chosen?.Maker.Where ?? (members.Count == 1 ? members[0].Maker.Where
        : $"{members[0].Maker.Where} (or {string.Join(" or ", members.Skip(1).Take(3).Select(m => m.HostId ?? m.Maker.Where))}" +
          $"{(members.Count > 4 ? " or another" : "")} when it is busy)");

    /// <summary>Available when a member can draw now (the first that can, in order); else the first member's reason.</summary>
    public async Task<PictureMakerAvailability> GetAvailabilityAsync(CancellationToken cancellationToken)
    {
        PictureMakerAvailability? first = null;
        foreach (var member in members)
        {
            var availability = await CheckAsync(member, fresh: true, cancellationToken).ConfigureAwait(false);
            if (availability.Available) return availability;
            first ??= availability;
        }
        return members.Count == 1 ? first! : PictureMakerAvailability.Unavailable(
            $"{(first!.Reason ?? "It can't draw now.").TrimEnd('.')}. None of the other {members.Count - 1} places in your Pictures list can draw now either.");
    }

    public async Task<PictureResult> GenerateAsync(PictureRequest request, IProgress<PictureProgress>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        if (members.Count == 1)
        {
            Choose(members[0], new(members[0].Id, 0, 0, 0, null));
            return await members[0].Maker.GenerateAsync(request, progress, cancellationToken).ConfigureAwait(false);
        }
        var problems = new Problems();
        try
        {
            // Every member is tried once, in order; when every one that can draw is busy, the picture waits in the shortest
            // ComfyUI queue (one line: the member's own queue, not WorkQueue's).
            var run = await new PoolRunner<PictureMakingPoolMember>(Lane, m => m.Id, Classify, WorkPriority.Background, queue, clock)
                .RunOrWaitInLineAsync(members, (m, tried, token) => DrawAsync(m, request, progress, problems, tried, token), 1, cancellationToken)
                .ConfigureAwait(false);
            return run.Result;
        }
        catch (Refusal refusal) { throw Failed(problems, refusal); }
    }

    // Why no member drew it: a workflow rejection first (the owner can fix it), else the first member's reason.
    private static PictureException Failed(Problems problems, Refusal last)
    {
        if (problems.Invalid is { } invalid) return invalid;
        var first = problems.First ?? last.Problem;
        return new(first.Code, $"{first.Message.TrimEnd('.')}. No other place in your Pictures list could draw it either.", first);
    }

    // One member's try: it can draw (availability), is free (its ComfyUI queue is empty, unless the picture waits there on
    // purpose: tried.Ahead), then draws. A failure before the picture is kept is a refusal: the next member tries.
    private async Task<PictureResult> DrawAsync(PictureMakingPoolMember member, PictureRequest request, IProgress<PictureProgress>? progress,
        Problems problems, PoolTry tried, CancellationToken token)
    {
        var availability = await CheckAsync(member, fresh: false, token).ConfigureAwait(false);
        if (!availability.Available)
            throw problems.Refuse(WorkRefusal.Unavailable,
                new(PictureErrorCodes.Unavailable, availability.Reason ?? $"{member.Maker.Where} can't draw now."));
        if (tried.Ahead is null && member.Maker is ComfyPictureMaker comfy)
        {
            JsonObject state;
            try { state = await comfy.Api.QueueStateAsync(token).ConfigureAwait(false); }
            catch (PictureException error)
            {
                throw problems.Refuse(error.Code == PictureErrorCodes.Busy ? WorkRefusal.Busy : WorkRefusal.Unavailable, error);
            }
            var line = ComfyErrors.Running(state).Count + ComfyErrors.Pending(state).Count;
            if (line > 0)
            {
                tried.Line(line);
                throw problems.Refuse(WorkRefusal.Busy,
                    new(PictureErrorCodes.Busy, $"{member.Maker.Where} is drawing {line} other picture{(line == 1 ? "" : "s")}."));
            }
        }
        Choose(member, new(member.Id, tried.Position, tried.Busy, tried.Unavailable, tried.Ahead));
        try { return await member.Maker.GenerateAsync(request, progress, token).ConfigureAwait(false); }
        catch (PictureException error) when (error.Code is PictureErrorCodes.RequestInvalid)
        {
            // This member rejected the workflow (a checkpoint or custom node it doesn't have): another may have it.
            problems.Invalid ??= error;
            throw problems.Refuse(WorkRefusal.Unavailable, error);
        }
        catch (PictureException error) when (error.Code is PictureErrorCodes.Unavailable or PictureErrorCodes.NotAuthorized)
        {
            // It stopped answering, or a provider refused this member's key: another member may draw it.
            throw problems.Refuse(WorkRefusal.Unavailable, error);
        }
        catch (PictureException error) when (error.Code is PictureErrorCodes.Busy)
        {
            tried.Line(int.MaxValue);
            throw problems.Refuse(WorkRefusal.Busy, error);
        }
    }

    private void Choose(PictureMakingPoolMember member, PictureMakingPoolRoute how)
    {
        Volatile.Write(ref chosen, member);
        Volatile.Write(ref route, how);
        Placed?.Invoke(member);
    }

    // A member's availability, asked again after Recheck (or always, when fresh).
    private async Task<PictureMakerAvailability> CheckAsync(PictureMakingPoolMember member, bool fresh, CancellationToken token)
    {
        var now = clock.GetTimestamp();
        if (!fresh && checks.TryGetValue(member.Id, out var known) && clock.GetElapsedTime(known.At, now) < Recheck) return known.Availability;
        PictureMakerAvailability availability;
        try { availability = await member.Maker.GetAvailabilityAsync(token).ConfigureAwait(false); }
        catch (Exception error) when (error is PictureException or HttpRequestException or IOException)
        {
            availability = PictureMakerAvailability.Unavailable($"{member.Maker.Where} isn't reachable ({error.Message.TrimEnd('.')}).");
        }
        checks[member.Id] = (availability, now);
        return availability;
    }

    private static WorkRefusal Classify(Exception error) => error is Refusal refusal ? refusal.Kind : WorkRefusal.None;

    public void Dispose()
    {
        foreach (var member in members) (member.Maker as IDisposable)?.Dispose();
    }

    // One picture's problems on its way through the members: the first member's reason and the first workflow rejection. The
    // runner (PoolRunner) keeps the busy and unavailable counts and the busy members' queue lengths.
    private sealed class Problems
    {
        private PictureException? first;

        public PictureException? Invalid { get; set; }
        public PictureException? First => Volatile.Read(ref first);

        public Refusal Refuse(WorkRefusal kind, PictureException problem)
        {
            if (kind != WorkRefusal.Busy) Interlocked.CompareExchange(ref first, problem, null);
            return new(kind, problem);
        }
    }

    private sealed class Refusal(WorkRefusal kind, PictureException problem) : Exception(problem.Message, problem)
    {
        public WorkRefusal Kind { get; } = kind;
        public PictureException Problem { get; } = problem;
    }
}
