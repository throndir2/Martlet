using System.Collections.Concurrent;
using System.Net.Http;
using System.Text.Json.Nodes;
using Martlet.Core.Cluster;
using Martlet.Core.Pictures;

namespace Martlet.Providers.Pictures;

/// <summary>One place in a <see cref="PicturePool"/>: its ID (the pool member's key: "host:&lt;id&gt;", "address:&lt;url&gt;",
/// "cloud:&lt;provider&gt;/&lt;model&gt;"), the picture maker with that member's own settings (workflow, checkpoint or model) and,
/// for Martlet's pictures role, the paired computer it draws on.</summary>
public sealed record PicturePoolMember(string Id, IPictureMaker Maker, string? HostId = null);

/// <summary>Where a pool's picture went: the member, its place in the order, how many members were busy or couldn't draw it
/// first, and, when every member was busy, how many pictures it waited behind in that member's ComfyUI queue.</summary>
public sealed record PicturePoolRoute(string Id, int Position, int Busy, int Unavailable, int? QueuedBehind);

/// <summary>
/// Draws each picture on the first member of Pictures' ordered list (<see cref="PoolAreas.Pictures"/>) that is free
/// (docs/PICTURES.md#more-than-one-picture-computer), through Martlet's queue for shared work (<see cref="WorkQueue"/>, lane
/// <see cref="Lane"/> and each member's key, background priority: pictures are never live work). A ComfyUI member whose queue
/// already holds pictures (another companion PC's, or this PC's) is busy and passed over for the next, and so is a cloud
/// provider that answers busy (429, 503). A member that doesn't answer, isn't ready, lacks the workflow's model files or
/// checkpoint, rejects the workflow (a custom node or model it doesn't have) or refuses its key can't draw it and is passed
/// over too. When every member that can draw is busy, the picture waits in the shortest ComfyUI queue among them (fewest
/// pictures ahead; first in the order on a tie) instead of asking again and again. With one member it draws there exactly as
/// that member does alone: no extra request. <see cref="Chosen"/> and <see cref="Route"/> say where it went;
/// <see cref="Placed"/> is raised when a member takes it.
/// </summary>
public sealed class PicturePool : IPictureMaker, IDisposable
{
    /// <summary>The <see cref="WorkQueue"/> lane of pictures (<see cref="PoolAreas.Pictures"/>' ID).</summary>
    public static string Lane => PoolAreas.Pictures.Id;
    private static readonly TimeSpan Recheck = TimeSpan.FromSeconds(30);
    private readonly IReadOnlyList<PicturePoolMember> members;
    private readonly WorkQueue queue;
    private readonly TimeProvider clock;
    private readonly ConcurrentDictionary<string, (PictureMakerAvailability Availability, long At)> checks = new(StringComparer.Ordinal);
    private PicturePoolMember? chosen;
    private PicturePoolRoute? route;

    public PicturePool(IReadOnlyList<PicturePoolMember> members, WorkQueue? queue = null, TimeProvider? clock = null)
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
    public IReadOnlyList<PicturePoolMember> Members => members;

    /// <summary>The member that took the last picture; null before one did.</summary>
    public PicturePoolMember? Chosen => Volatile.Read(ref chosen);

    /// <summary>How the last picture found its member; null before one did.</summary>
    public PicturePoolRoute? Route => Volatile.Read(ref route);

    /// <summary>Raised when a member takes a picture (again, on another member, when the first stopped answering).</summary>
    public event Action<PicturePoolMember>? Placed;

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
        var attempt = new Attempt();
        try
        {
            // No waiting in WorkQueue's line: a busy member's ComfyUI keeps its own queue, so asking again every 100 ms would only
            // add requests. Every member is tried once, in order.
            return await queue.RunAsync(Lane, members, m => m.Id, (m, token) => DrawAsync(m, request, progress, attempt, null, token),
                Classify, DateTimeOffset.MinValue, clock, cancellationToken, WorkPriority.Background).ConfigureAwait(false);
        }
        catch (Refusal) when (attempt.Lines.Any(l => !attempt.Failed.ContainsKey(l.Key)))
        {
            // Every member that can draw is busy: the picture waits in the shortest ComfyUI queue.
            var (id, ahead) = attempt.Lines.Where(l => !attempt.Failed.ContainsKey(l.Key))
                .OrderBy(l => l.Value).ThenBy(l => Index(l.Key)).First();
            var member = members[Index(id)];
            try
            {
                return await queue.RunAsync(Lane, [member], m => m.Id, (m, token) => DrawAsync(m, request, progress, attempt, ahead, token),
                    Classify, DateTimeOffset.MinValue, clock, cancellationToken, WorkPriority.Background).ConfigureAwait(false);
            }
            catch (Refusal refusal) { throw Failed(attempt, refusal); }
        }
        catch (Refusal refusal) { throw Failed(attempt, refusal); }
    }

    // Why no member drew it: a workflow rejection first (the owner can fix it), else the first member's reason.
    private static PictureException Failed(Attempt attempt, Refusal last)
    {
        if (attempt.Invalid is { } invalid) return invalid;
        var first = attempt.First ?? last.Problem;
        return new(first.Code, $"{first.Message.TrimEnd('.')}. No other place in your Pictures list could draw it either.", first);
    }

    // One member's try: it can draw (availability), is free (its ComfyUI queue is empty, unless the picture waits there on
    // purpose: queuedBehind), then draws. A failure before the picture is kept is a refusal: the next member tries.
    private async Task<PictureResult> DrawAsync(PicturePoolMember member, PictureRequest request, IProgress<PictureProgress>? progress,
        Attempt attempt, int? queuedBehind, CancellationToken token)
    {
        var availability = await CheckAsync(member, fresh: false, token).ConfigureAwait(false);
        if (!availability.Available)
            throw attempt.Refuse(member.Id, WorkRefusal.Unavailable,
                new(PictureErrorCodes.Unavailable, availability.Reason ?? $"{member.Maker.Where} can't draw now."));
        if (queuedBehind is null && member.Maker is ComfyPictureMaker comfy)
        {
            JsonObject state;
            try { state = await comfy.Api.QueueStateAsync(token).ConfigureAwait(false); }
            catch (PictureException error)
            {
                throw attempt.Refuse(member.Id, error.Code == PictureErrorCodes.Busy ? WorkRefusal.Busy : WorkRefusal.Unavailable, error);
            }
            var line = ComfyErrors.Running(state).Count + ComfyErrors.Pending(state).Count;
            if (line > 0)
            {
                attempt.Lines[member.Id] = line;
                throw attempt.Refuse(member.Id, WorkRefusal.Busy,
                    new(PictureErrorCodes.Busy, $"{member.Maker.Where} is drawing {line} other picture{(line == 1 ? "" : "s")}."));
            }
        }
        Choose(member, new(member.Id, Index(member.Id), attempt.Busy, attempt.Unavailable, queuedBehind));
        try { return await member.Maker.GenerateAsync(request, progress, token).ConfigureAwait(false); }
        catch (PictureException error) when (error.Code is PictureErrorCodes.RequestInvalid)
        {
            // This member rejected the workflow (a checkpoint or custom node it doesn't have): another may have it.
            attempt.Invalid ??= error;
            throw attempt.Refuse(member.Id, WorkRefusal.Unavailable, error);
        }
        catch (PictureException error) when (error.Code is PictureErrorCodes.Unavailable or PictureErrorCodes.NotAuthorized)
        {
            // It stopped answering, or a provider refused this member's key: another member may draw it.
            throw attempt.Refuse(member.Id, WorkRefusal.Unavailable, error);
        }
        catch (PictureException error) when (error.Code is PictureErrorCodes.Busy)
        {
            attempt.Lines.TryAdd(member.Id, int.MaxValue);
            throw attempt.Refuse(member.Id, WorkRefusal.Busy, error);
        }
    }

    private void Choose(PicturePoolMember member, PicturePoolRoute how)
    {
        Volatile.Write(ref chosen, member);
        Volatile.Write(ref route, how);
        Placed?.Invoke(member);
    }

    private int Index(string id)
    {
        for (var i = 0; i < members.Count; i++)
            if (members[i].Id == id) return i;
        return members.Count;
    }

    // A member's availability, asked again after Recheck (or always, when fresh).
    private async Task<PictureMakerAvailability> CheckAsync(PicturePoolMember member, bool fresh, CancellationToken token)
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

    // One picture's way through the members: the busy ones' queue lengths, the ones that failed, and the first workflow rejection.
    private sealed class Attempt
    {
        public ConcurrentDictionary<string, int> Lines { get; } = new(StringComparer.Ordinal);
        public ConcurrentDictionary<string, bool> Failed { get; } = new(StringComparer.Ordinal);
        public PictureException? Invalid { get; set; }
        public PictureException? First => Volatile.Read(ref first);
        public int Busy;
        public int Unavailable;
        private PictureException? first;

        public Refusal Refuse(string id, WorkRefusal kind, PictureException problem)
        {
            if (kind == WorkRefusal.Busy) Interlocked.Increment(ref Busy);
            else
            {
                Interlocked.Increment(ref Unavailable);
                Failed[id] = true;
                Interlocked.CompareExchange(ref first, problem, null);
            }
            return new(kind, problem);
        }
    }

    private sealed class Refusal(WorkRefusal kind, PictureException problem) : Exception(problem.Message, problem)
    {
        public WorkRefusal Kind { get; } = kind;
        public PictureException Problem { get; } = problem;
    }
}
