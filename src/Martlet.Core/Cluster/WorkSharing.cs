using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Martlet.Core.Cluster;

/// <summary>The jobs your computers share when the computer that does one is busy (Devices › Sharing work).</summary>
public static class WorkSharingJobs
{
    public const string Thinking = ClusterJobs.Thinking;
    public const string Listening = ClusterJobs.Listening;
    public const string Speaking = ClusterJobs.Speaking;
    public const string DeepThinking = "deep-thinking";
    /// <summary>Reading the text on the screen (Companion › Reading): a pool of the owner's computers that run the Reading role.
    /// Not in <see cref="All"/>: Companion › Reading keeps its own list, not a Devices › Sharing work card.</summary>
    public const string Reading = "reading";
    /// <summary>Drawing pictures (Companion › Pictures): each PC's Pictures list (<see cref="PoolAreas.Pictures"/>). Not in
    /// <see cref="All"/>: Companion › Pictures keeps its own list, not a Devices › Sharing work card.</summary>
    public const string Pictures = "pictures";
    public static readonly IReadOnlyList<string> All = [Speaking, Thinking, Listening, DeepThinking];

    /// <summary>Speaking, Listening and Reading are shared unless turned off: each request stands alone. Thinking is shared only
    /// when chosen: another computer's model starts your conversation without its prompt cache (a slower first word) and pushes
    /// that computer's own conversation out of its cache. Deep thinking always thinks on the places chosen for it.</summary>
    public static bool SharedByDefault(string job) => job is Speaking or Listening or Reading;

    public static string Title(string job) => job switch
    {
        Speaking => "Speaking",
        Thinking => "Thinking",
        Listening => "Listening",
        DeepThinking => "Thinking pool",
        Reading => "Reading",
        Pictures => "Pictures",
        _ => job
    };
}

/// <summary>How one job is shared: <see cref="Share"/> lets a companion PC send it to another computer that runs the same
/// engine when the one it uses is busy (on by default); <see cref="Order"/> is the computers to try first, in order (host IDs,
/// or <see cref="WorkSharingSettings.ThisPc"/> for each companion PC's own host service); <see cref="Never"/> are computers
/// never used for it.</summary>
public sealed record WorkSharingJob
{
    public required string Job { get; init; }
    /// <summary>Whether it is shared as chosen; null: <see cref="WorkSharingJobs.SharedByDefault"/>.</summary>
    public bool? Share { get; init; }
    public IReadOnlyList<string> Order { get; init; } = [];
    public IReadOnlyList<string> Never { get; init; } = [];

    [JsonIgnore] public bool Shares => Share ?? WorkSharingJobs.SharedByDefault(Job);

    [JsonIgnore] public bool IsDefault => (Share is null || Share == WorkSharingJobs.SharedByDefault(Job)) && Order.Count == 0 && Never.Count == 0;
}

/// <summary>A computer kept for some companion PCs only (<see cref="OnlyFor"/>, their device IDs): no other computer sends it
/// work. Empty: every companion PC may use it.</summary>
public sealed record WorkSharingHost
{
    public required string HostId { get; init; }
    public IReadOnlyList<string> OnlyFor { get; init; } = [];
}

/// <summary>Devices › Sharing work (work-sharing.json in the data directory, the <c>work-sharing</c> shared setting, so it is
/// the same on all your computers): for each job, whether it is shared, which computers to try first and which never, and
/// which computers are kept for one companion PC. Nonsecret: host and device IDs only.</summary>
public sealed record WorkSharingSettings
{
    public const string FileName = "work-sharing.json";
    public const string SharedKey = "work-sharing";
    /// <summary>In <see cref="WorkSharingJob.Order"/>: the host service of the companion PC asking, whichever that is.</summary>
    public const string ThisPc = "this-pc";
    public const int MaximumHosts = 32;
    private static readonly JsonSerializerOptions Canonical = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        RespectNullableAnnotations = true,
        MaxDepth = 8
    };
    private static readonly JsonSerializerOptions Indented = new(Canonical) { WriteIndented = true };

    public int SchemaVersion { get; init; } = 1;
    public IReadOnlyList<WorkSharingJob> Jobs { get; init; } = [];
    public IReadOnlyList<WorkSharingHost> Hosts { get; init; } = [];

    /// <summary>How <paramref name="job"/> is shared (the default when nothing was chosen).</summary>
    public WorkSharingJob Job(string job) => Jobs.FirstOrDefault(j => j.Job == job) ?? new() { Job = job };

    /// <summary>The companion PCs <paramref name="hostId"/> is kept for; empty when every one may use it.</summary>
    public IReadOnlyList<string> OnlyFor(string hostId) => Hosts.FirstOrDefault(h => h.HostId == hostId)?.OnlyFor ?? [];

    /// <summary>Whether companion PC <paramref name="device"/> may send work to <paramref name="hostId"/>.</summary>
    public bool Allows(string hostId, string device)
    {
        var only = OnlyFor(hostId);
        return only.Count == 0 || only.Contains(device, StringComparer.Ordinal);
    }

    public WorkSharingSettings With(WorkSharingJob job) => Normalized(this with { Jobs = [.. Jobs.Where(j => j.Job != job.Job), job] });

    public WorkSharingSettings With(WorkSharingHost host) => Normalized(this with { Hosts = [.. Hosts.Where(h => h.HostId != host.HostId), host] });

    // Defaults left out, lists without repeats, sorted: every computer writes the same JSON for the same choices.
    private static WorkSharingSettings Normalized(WorkSharingSettings settings) => settings with
    {
        Jobs = [.. settings.Jobs.Select(j => j with
            {
                Share = j.Share == WorkSharingJobs.SharedByDefault(j.Job) ? null : j.Share,
                Order = [.. j.Order.Distinct(StringComparer.Ordinal)],
                Never = [.. j.Never.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)]
            })
            .Where(j => !j.IsDefault).OrderBy(j => j.Job, StringComparer.Ordinal)],
        Hosts = [.. settings.Hosts.Select(h => h with { OnlyFor = [.. h.OnlyFor.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)] })
            .Where(h => h.OnlyFor.Count > 0).OrderBy(h => h.HostId, StringComparer.Ordinal)]
    };

    [JsonIgnore] public bool IsDefault => Jobs.All(j => j.IsDefault) && Hosts.All(h => h.OnlyFor.Count == 0);

    private static bool Name(string? text) => text is { Length: > 0 and <= 64 } && char.IsAsciiLetterOrDigit(text[0]) &&
        text.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-');

    private bool Valid() => SchemaVersion == 1 && Jobs is { Count: <= 16 } && Hosts is { Count: <= MaximumHosts } &&
        Jobs.All(j => j is not null && Name(j.Job) && j.Order is { Count: <= MaximumHosts } && j.Never is { Count: <= MaximumHosts } &&
            j.Order.All(Name) && j.Never.All(Name)) &&
        Jobs.Select(j => j.Job).Distinct(StringComparer.Ordinal).Count() == Jobs.Count &&
        Hosts.All(h => h is not null && Name(h.HostId) && h.OnlyFor is { Count: <= MaximumHosts } && h.OnlyFor.All(Name)) &&
        Hosts.Select(h => h.HostId).Distinct(StringComparer.Ordinal).Count() == Hosts.Count;

    /// <summary>The canonical JSON every computer writes for the same choices, for sharing.</summary>
    public string Share() => JsonSerializer.Serialize(Normalized(this), Canonical);

    /// <summary>Choices another computer shared (<see cref="Share"/>); null when they aren't ones this Martlet reads.</summary>
    public static WorkSharingSettings? Parse(string json)
    {
        try
        {
            var parsed = JsonSerializer.Deserialize<WorkSharingSettings>(json, Canonical);
            return parsed is not null && parsed.Valid() ? Normalized(parsed) : null;
        }
        catch (Exception error) when (error is JsonException or NotSupportedException or ArgumentException) { return null; }
    }

    public static WorkSharingSettings Load(string? directory)
    {
        if (directory is null) return new();
        try
        {
            var path = Path.Combine(directory, FileName);
            return File.Exists(path) && new FileInfo(path).Length <= 65_536 ? Parse(File.ReadAllText(path)) ?? new() : new();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return new(); }
    }

    public bool Save(string? directory)
    {
        if (directory is null || !Valid()) return false;
        try
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, FileName);
            var temporary = Path.Combine(directory, $"work-sharing.{Guid.NewGuid():N}.tmp");
            try
            {
                File.WriteAllText(temporary, JsonSerializer.Serialize(Normalized(this), Indented));
                File.Move(temporary, path, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }
}

/// <summary>A paired computer that runs a job's engine: its host ID, whether it is the asking companion PC's own host service
/// (<see cref="Own"/>) and how many of the shared plan's jobs it already does (<see cref="Jobs"/>).</summary>
public sealed record WorkPlace(string HostId, bool Own = false, int Jobs = 0);

/// <summary>Which computers a companion PC tries for a job, in order. Deterministic and instant (no network): the same choices
/// and computers always give the same order.</summary>
public static class WorkSharing
{
    /// <summary>The computers companion PC <paramref name="device"/> tries for <paramref name="job"/>, first to last.
    /// <paramref name="planned"/> is the computer it uses for the job now (the plan's host, or its own choice); <paramref name="runs"/>
    /// the other paired computers that run the job's engine. With the job not shared, only <paramref name="planned"/>. With an
    /// order chosen: its computers (<see cref="WorkSharingSettings.ThisPc"/> being this PC's own host service), then
    /// <paramref name="planned"/>, then the rest; without: <paramref name="planned"/> (so nothing changes while it is free), this
    /// PC's own host service, then the rest, fewest jobs first, then by ID. Computers the job never uses, or kept for other
    /// companion PCs, are left out, unless that leaves none: then <paramref name="planned"/> still does it.</summary>
    public static IReadOnlyList<string> Order(WorkSharingSettings settings, string job, string device, string? planned,
        IReadOnlyList<WorkPlace> runs)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(runs);
        var rules = settings.Job(job);
        if (!rules.Shares) return planned is null ? [] : [planned];
        var own = runs.FirstOrDefault(p => p.Own)?.HostId;
        List<string> order = [];
        void Add(string? id)
        {
            if (id is null || order.Contains(id, StringComparer.Ordinal) || rules.Never.Contains(id, StringComparer.Ordinal) ||
                !settings.Allows(id, device) || id != planned && runs.All(p => p.HostId != id)) return;
            order.Add(id);
        }
        foreach (var id in rules.Order) Add(id == WorkSharingSettings.ThisPc ? own : id);
        Add(planned);
        Add(own);
        foreach (var place in runs.OrderBy(p => p.Jobs).ThenBy(p => p.HostId, StringComparer.Ordinal)) Add(place.HostId);
        if (order.Count == 0 && planned is not null) order.Add(planned);
        return order;
    }
}

/// <summary>What a computer's refusal means for a request that hasn't started: <see cref="Busy"/> (it does one job at a time
/// and is doing another: try the next, then wait for whichever frees first), <see cref="Unavailable"/> (unreachable, or it
/// doesn't run the engine now: try the next), <see cref="Preempted"/> (it keeps the graphics card free for a live conversation
/// turn, its own or another companion PC's: pool work goes elsewhere or waits and continues later; never a failure),
/// <see cref="Owner"/> (a host a friend shares with this PC keeps its graphics card for its owner's own work: a live request
/// tries the next computer at once and never waits for that one, and background work goes elsewhere or later) or
/// <see cref="None"/> (a real failure: give up).</summary>
public enum WorkRefusal { None, Busy, Unavailable, Preempted, Owner }

/// <summary>Who a shared request is for: the live conversation turn (a reply, its voice, its transcript) or this PC's own
/// background work on a live route (remembering after a reply, a think on the conversation model). Live requests go first: a
/// background request never starts while a live request of the same lane waits, and a live request that finds a computer busy
/// with this PC's own background request stops that request (<see cref="WorkPreemptedException"/>) instead of waiting behind
/// it.</summary>
public enum WorkPriority { Live, Background }

/// <summary>A live request of this PC needed the computer this background request ran on (<see cref="WorkQueue"/>), so it
/// stopped. Its caller does the work again later; it is not a failure of the computer.</summary>
public sealed class WorkPreemptedException() : OperationCanceledException("A live conversation request needed the computer, so this background request stopped.");

/// <summary>One request's way through its computers: which one took it and how many were busy or unavailable first.</summary>
public sealed record WorkRoute(string HostId, int Position, int Busy, int Unavailable, TimeSpan Waited);

/// <summary>Martlet's queue for work shared between computers. A request goes to the first computer in its order that takes
/// it; one that is busy (a computer runs one voice, one Thinking reply, one transcription at a time and turns another away
/// at once) or unavailable is passed over for the next. When every one is busy the request waits in line and tries them
/// again, in order, every <see cref="Retry"/> and whenever one of this PC's own requests finishes, so whichever computer
/// frees first takes it. Requests this PC already has running on a computer put that computer last for the next one,
/// saving a round trip it would only turn away. Live requests go first (<see cref="WorkPriority"/>): a background request
/// waits while a live request of its lane waits, and a live request that finds a computer busy with this PC's own background
/// request stops that request (it gets <see cref="WorkPreemptedException"/>) and takes the computer as soon as it is free,
/// instead of waiting behind it or moving on to a computer without the conversation's prompt cache. Thread-safe; one per
/// process (<see cref="Shared"/>).</summary>
public sealed class WorkQueue
{
    private readonly object gate = new();
    private readonly Dictionary<string, int> running = new(StringComparer.Ordinal);
    // This PC's background requests running now, by lane and computer, each with its own stop.
    private readonly Dictionary<string, List<CancellationTokenSource>> background = new(StringComparer.Ordinal);
    // How many live requests of each lane wait for a free computer now; background requests of that lane wait behind them.
    private readonly Dictionary<string, int> liveWaiting = new(StringComparer.Ordinal);
    private TaskCompletionSource freed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int waiting;
    private long stopped;

    public static WorkQueue Shared { get; } = new();

    public TimeSpan Retry { get; init; } = TimeSpan.FromMilliseconds(100);

    /// <summary>Raised when a request was taken by a computer other than its first, or waited for one.</summary>
    public event Action<string, WorkRoute>? Rerouted;

    /// <summary>Raised (lane, computer) when a live request stopped this PC's own background request on a computer.</summary>
    public event Action<string, string>? Preempted;

    /// <summary>How many requests this PC runs on <paramref name="hostId"/> for <paramref name="lane"/> now.</summary>
    public int Running(string lane, string hostId)
    {
        lock (gate) return running.GetValueOrDefault(Key(lane, hostId));
    }

    /// <summary>How many of this PC's background requests run on <paramref name="hostId"/> for <paramref name="lane"/> now.</summary>
    public int Background(string lane, string hostId)
    {
        lock (gate) return background.TryGetValue(Key(lane, hostId), out var list) ? list.Count : 0;
    }

    /// <summary>How many requests wait for a free computer now.</summary>
    public int Waiting => Volatile.Read(ref waiting);

    /// <summary>How many background requests live requests stopped since this queue was made.</summary>
    public long Stopped => Interlocked.Read(ref stopped);

    /// <summary>Streams <paramref name="lane"/>'s request from the first of <paramref name="targets"/> that takes it (see the
    /// class summary). <paramref name="start"/> starts it on one; <paramref name="classify"/> says what a failure before its
    /// first item means. Waits for a busy computer until <paramref name="until"/> at most. The last refusal is thrown when
    /// none took it. A <see cref="WorkPriority.Background"/> request that a live request stopped throws
    /// <see cref="WorkPreemptedException"/>.</summary>
    public async IAsyncEnumerable<T> StreamAsync<TTarget, T>(string lane, IReadOnlyList<TTarget> targets, Func<TTarget, string> hostOf,
        Func<TTarget, CancellationToken, IAsyncEnumerable<T>> start, Func<Exception, WorkRefusal> classify, DateTimeOffset until,
        TimeProvider? clock, [EnumeratorCancellation] CancellationToken token, WorkPriority priority = WorkPriority.Live)
    {
        ArgumentNullException.ThrowIfNull(targets);
        if (targets.Count == 0) throw new ArgumentException("A shared request needs at least one computer.", nameof(targets));
        var time = clock ?? TimeProvider.System;
        var began = time.GetTimestamp();
        var live = priority == WorkPriority.Live;
        // A background request's own stop, which a live request that needs its computer cancels.
        using var stop = live ? null : CancellationTokenSource.CreateLinkedTokenSource(token);
        var attempt = stop?.Token ?? token;
        HashSet<string> gone = new(StringComparer.Ordinal);
        int busy = 0, unavailable = 0;
        Exception? last = null;
        IAsyncEnumerator<T>? taken = null;
        string? host = null;
        var position = 0;
        var queued = false;
        var counted = false;
        var held = false;
        try
        {
            while (taken is null)
            {
                Task wake;
                bool behind;
                (TTarget target, int index, string host)[] order;
                lock (gate)
                {
                    wake = freed.Task;
                    behind = !live && liveWaiting.GetValueOrDefault(lane) > 0;
                    // A live request counts only this PC's live requests on a computer: its background ones give way.
                    order = [.. targets.Select((target, index) => (target, index, host: hostOf(target)))
                        .Where(t => !gone.Contains(t.host))
                        .OrderBy(t => targets.Count > 1 && Others(lane, t.host, live) > 0 ? 1 : 0).ThenBy(t => t.index)];
                }
                if (order.Length == 0) break;
                var anyBusy = false;
                if (!behind)
                    foreach (var (target, index, id) in order)
                    {
                        token.ThrowIfCancellationRequested();
                        Enter(lane, id, stop);
                        var enumerator = start(target, attempt).GetAsyncEnumerator(attempt);
                        (bool Has, Exception? Error) first;
                        try { first = (await enumerator.MoveNextAsync().ConfigureAwait(false), null); }
                        catch (Exception error) when (!token.IsCancellationRequested) { first = (false, error); }
                        catch
                        {
                            await Close(enumerator, lane, id, stop).ConfigureAwait(false);
                            throw;
                        }
                        if (first.Error is null)
                        {
                            if (!first.Has)
                            {
                                await Close(enumerator, lane, id, stop, served: true).ConfigureAwait(false);
                                Report(lane, id, index, busy, unavailable, time.GetElapsedTime(began));
                                yield break;
                            }
                            (taken, host, position) = (enumerator, id, index);
                            break;
                        }
                        // Stopped by a live request before its first item: the computer frees for that request now.
                        var preempted = stop?.IsCancellationRequested == true;
                        await Close(enumerator, lane, id, stop, served: preempted).ConfigureAwait(false);
                        if (preempted) throw new WorkPreemptedException();
                        var refusal = classify(first.Error);
                        if (refusal == WorkRefusal.None) throw first.Error;
                        last = first.Error;
                        if (refusal is WorkRefusal.Preempted or WorkRefusal.Owner && !live)
                        {
                            // Background work goes elsewhere or later while a live turn (or a shared host's owner) holds this
                            // computer's graphics card.
                            held = true;
                            gone.Add(id);
                        }
                        else if (refusal is WorkRefusal.Busy or WorkRefusal.Preempted)
                        {
                            busy++;
                            anyBusy = true;
                            // This PC's own background work holds the computer: stop it and wait for this computer.
                            if (live && StopBackground(lane, id)) break;
                        }
                        else
                        {
                            // Unreachable, or a shared host's owner needs it: a live request never waits for that computer.
                            unavailable++;
                            gone.Add(id);
                        }
                    }
                if (taken is not null) break;
                if (!anyBusy && !behind || time.GetUtcNow() >= until) break;
                if (!queued)
                {
                    Interlocked.Increment(ref waiting);
                    queued = true;
                }
                if (live && !counted)
                {
                    lock (gate) liveWaiting[lane] = liveWaiting.GetValueOrDefault(lane) + 1;
                    counted = true;
                }
                var left = until - time.GetUtcNow();
                var pause = left < Retry ? left : Retry;
                if (pause > TimeSpan.Zero)
                    await Task.WhenAny(wake, Task.Delay(pause, time, token)).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
            }
        }
        finally
        {
            if (queued) Interlocked.Decrement(ref waiting);
            if (counted) LeaveLine(lane);
        }
        if (taken is null)
        {
            // A background request a live turn's hold kept from its computer goes on later.
            if (held) throw new WorkPreemptedException();
            throw last ?? new InvalidOperationException("No computer took the request.");
        }
        Report(lane, host!, position, busy, unavailable, time.GetElapsedTime(began));
        try
        {
            yield return taken.Current;
            while (true)
            {
                bool moved;
                try { moved = await taken.MoveNextAsync().ConfigureAwait(false); }
                catch (OperationCanceledException) when (stop?.IsCancellationRequested == true && !token.IsCancellationRequested)
                {
                    throw new WorkPreemptedException();
                }
                if (!moved) break;
                yield return taken.Current;
            }
        }
        finally
        {
            await Close(taken, lane, host!, stop, served: true).ConfigureAwait(false);
        }
    }

    /// <summary>Runs <paramref name="lane"/>'s one-answer request (a transcription) the same way as <see cref="StreamAsync"/>.</summary>
    public async Task<T> RunAsync<TTarget, T>(string lane, IReadOnlyList<TTarget> targets, Func<TTarget, string> hostOf,
        Func<TTarget, CancellationToken, Task<T>> start, Func<Exception, WorkRefusal> classify, DateTimeOffset until,
        TimeProvider? clock, CancellationToken token, WorkPriority priority = WorkPriority.Live)
    {
        await foreach (var answer in StreamAsync(lane, targets, hostOf, (target, t) => One(start(target, t)), classify, until, clock, token,
            priority).ConfigureAwait(false))
            return answer;
        throw new InvalidOperationException("The request returned nothing.");
    }

    private static async IAsyncEnumerable<T> One<T>(Task<T> answer)
    {
        yield return await answer.ConfigureAwait(false);
    }

    private static string Key(string lane, string host) => lane + "|" + host;

    // This PC's requests on a computer that a new request would only wait behind. Called under the gate.
    private int Others(string lane, string host, bool live)
    {
        var all = running.GetValueOrDefault(Key(lane, host));
        return live && background.TryGetValue(Key(lane, host), out var mine) ? all - mine.Count : all;
    }

    // Stops this PC's background requests of lane on host; false when there are none.
    private bool StopBackground(string lane, string host)
    {
        CancellationTokenSource[] stopping;
        lock (gate) stopping = background.TryGetValue(Key(lane, host), out var list) ? [.. list] : [];
        foreach (var source in stopping)
        {
            // The callbacks (closing the request) run off this thread, so the live request goes on at once.
            try { _ = source.CancelAsync(); }
            catch (ObjectDisposedException) { }
        }
        if (stopping.Length == 0) return false;
        Interlocked.Add(ref stopped, stopping.Length);
        Preempted?.Invoke(lane, host);
        return true;
    }

    private void LeaveLine(string lane)
    {
        lock (gate)
        {
            var left = liveWaiting.GetValueOrDefault(lane) - 1;
            if (left <= 0) liveWaiting.Remove(lane);
            else liveWaiting[lane] = left;
        }
    }

    private void Report(string lane, string host, int position, int busy, int unavailable, TimeSpan waited)
    {
        if (position > 0 || busy > 0 || unavailable > 0) Rerouted?.Invoke(lane, new(host, position, busy, unavailable, waited));
    }

    private void Enter(string lane, string host, CancellationTokenSource? stop)
    {
        lock (gate)
        {
            var key = Key(lane, host);
            running[key] = running.GetValueOrDefault(key) + 1;
            if (stop is null) return;
            if (!background.TryGetValue(key, out var list)) background[key] = list = [];
            list.Add(stop);
        }
    }

    private async ValueTask Close<T>(IAsyncEnumerator<T> enumerator, string lane, string host, CancellationTokenSource? stop,
        bool served = false)
    {
        try { await enumerator.DisposeAsync().ConfigureAwait(false); }
        finally
        {
            TaskCompletionSource wake;
            lock (gate)
            {
                var key = Key(lane, host);
                if (running.GetValueOrDefault(key) <= 1) running.Remove(key);
                else running[key]--;
                if (stop is not null && background.TryGetValue(key, out var list))
                {
                    list.Remove(stop);
                    if (list.Count == 0) background.Remove(key);
                }
                wake = freed;
                // Only a request that ran frees its computer for a waiting one; a refusal changes nothing.
                if (served) freed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            if (served) wake.TrySetResult();
        }
    }
    public override string ToString() => nameof(WorkQueue);
}
