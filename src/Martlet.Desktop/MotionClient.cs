using System.IO;
using System.Net.Http;
using System.Text.Json.Nodes;
using Martlet.Avatar.Audio2Face.Remote;
using Martlet.Core.Cluster;
using Martlet.Core.Motion;

namespace Martlet.Desktop;

/// <summary>
/// The motion maker Martlet uses (docs/MOTION_HOST.md): Martlet's <c>motion</c> host role (HY-Motion 1.0) on the motion pool
/// (<see cref="MotionPool"/>): the Motion list on Companion › Motion (pools.json, shared), or, before that list exists, the
/// older shared choice (<see cref="MotionSettings"/>, motion.json: every paired computer the shared plan says runs the role, one
/// computer, or off). Setting <c>MARTLET_MOTION_FIXTURE=1</c> before Martlet starts makes it use the FIXTURE - NOT AI
/// <see cref="FixtureMotionMaker"/>.
/// </summary>
internal static class MotionClient
{
    internal const string FixtureVariable = "MARTLET_MOTION_FIXTURE";
    /// <summary>How long the motion role keeps its model loaded after the last motion before Martlet frees the graphics card
    /// (the role also frees it by itself after five idle minutes).</summary>
    internal static readonly TimeSpan FreeAfter = TimeSpan.FromMinutes(3);
    private static readonly object Gate = new();
    private static (string Directory, DateTime Written, MotionSettings Settings)? cached;
    // The computers waiting to free their graphics card, by host ID.
    private static readonly Dictionary<string, CancellationTokenSource> freeing = new(StringComparer.Ordinal);

    internal static bool Fixture => Environment.GetEnvironmentVariable(FixtureVariable) == "1";

    /// <summary>The shared motion choice, read again only when motion.json changed (cheap enough for every reply).</summary>
    internal static MotionSettings Settings(string dataDirectory)
    {
        var path = Path.Combine(dataDirectory, MotionSettings.FileName);
        DateTime written;
        try { written = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { written = DateTime.MinValue; }
        lock (Gate)
        {
            if (cached is { } known && known.Directory == dataDirectory && known.Written == written) return known.Settings;
            var settings = MotionSettings.Load(dataDirectory);
            cached = (dataDirectory, written, settings);
            return settings;
        }
    }

    /// <summary>The motion pool from this PC's files, without the network (<see cref="MotionPool.Members"/>): each member with the
    /// paired computer it makes motions on, in the order a motion tries them. Never a host a friend shares.</summary>
    internal static IReadOnlyList<(PoolMember Member, string HostId)> Members(string dataDirectory)
    {
        var hosts = Paired(dataDirectory);
        var own = WorkSharingRoster.Shared.OwnHostId ?? hosts.FirstOrDefault(h => h.Method == HostSetupMethod.ThisPcDocker)?.HostId;
        return MotionPool.Members(WorkSharingRoster.Shared.Pool(dataDirectory, PoolAreas.Motion), Settings(dataDirectory), ClusterSync.LoadPlan(dataDirectory),
            WorkSharingRoster.Device, own, [.. hosts.Select(h => h.HostId)]);
    }

    /// <summary>The paired computers that make motions, in the order a motion tries them (files only, never the network), so a
    /// motion job can keep background thinks off them.</summary>
    internal static IReadOnlyList<string> Hosts(string dataDirectory) => [.. Members(dataDirectory).Select(m => m.HostId)];

    /// <summary>The computer a motion tries first, or null while motion is off or no computer makes motions.</summary>
    internal static string? HostId(string dataDirectory) => Hosts(dataDirectory).FirstOrDefault();

    /// <summary>Whether motions can be asked for: the fixture is on, or the motion pool has a computer (files only).</summary>
    internal static bool IsSetUp(string dataDirectory) => Fixture || Hosts(dataDirectory).Count > 0;

    /// <summary>The motion maker for the motion pool, or null while motion is off or no computer runs the role. Dispose it after
    /// use, or hand it to <see cref="FreeLater"/>, which disposes it.</summary>
    internal static IMotionMaker? For(string dataDirectory)
    {
        if (Fixture) return new FixtureMotionMaker(TimeSpan.FromMilliseconds(300));
        return Hosts(dataDirectory) is { Count: > 0 } hosts ? new PoolMotionMaker(dataDirectory, hosts) : null;
    }

    /// <summary>Frees the graphics card of every computer in the motion pool now (unloads HY-Motion); false when none could
    /// (not set up, not reachable, or making a motion).</summary>
    internal static async Task<bool> FreeAsync(string dataDirectory, CancellationToken cancellationToken)
    {
        var freed = false;
        foreach (var host in Hosts(dataDirectory))
        {
            using var maker = new HostMotionMaker(dataDirectory, host);
            try
            {
                await maker.FreeAsync(cancellationToken).ConfigureAwait(false);
                freed = true;
            }
            catch (MotionException error)
            {
                ErrorLog.Info($"Motion: couldn't free the graphics card on {maker.Where} ({error.Message}).");
            }
        }
        return freed;
    }

    /// <summary>After a motion on Martlet's motion role: frees the graphics card of each computer that made one once
    /// <see cref="FreeAfter"/> passes without another motion there, so the voice, listening and a local Thinking model get it
    /// back. Each computer has its own wait. Disposes <paramref name="maker"/>.</summary>
    internal static void FreeLater(IMotionMaker maker)
    {
        if (maker is PoolMotionMaker pool)
        {
            foreach (var used in pool.Used) FreeLater(new HostMotionMaker(pool.DataDirectory, used));
            pool.Dispose();
            return;
        }
        if (maker is not HostMotionMaker host)
        {
            (maker as IDisposable)?.Dispose();
            return;
        }
        var id = host.HostId;
        CancellationTokenSource next = new();
        lock (Gate)
        {
            if (freeing.Remove(id, out var earlier)) earlier.Cancel();
            freeing[id] = next;
        }
        Task.Run(async () =>
        {
            try
            {
                await Task.Delay(FreeAfter, next.Token).ConfigureAwait(false);
                await host.FreeAsync(next.Token).ConfigureAwait(false);
                ErrorLog.Info($"Motion: freed the graphics card on {host.Where} after {FreeAfter.TotalMinutes:0} idle minutes.");
            }
            catch (OperationCanceledException) { }
            catch (Exception error) when (error is MotionException or HttpRequestException or IOException)
            {
                ErrorLog.Info($"Motion: couldn't free the graphics card on {host.Where} ({error.Message}).");
            }
            finally
            {
                lock (Gate)
                    if (freeing.TryGetValue(id, out var current) && current == next) freeing.Remove(id);
                host.Dispose();
            }
        }).Forget();
    }

    /// <summary>Why a motion failed, in a few plain words for the conversation.</summary>
    internal static string Problem(MotionException error) => error.Code switch
    {
        MotionErrorCodes.Busy => "the motion computer is busy with another motion",
        MotionErrorCodes.OutOfMemory => "the motion computer's graphics card ran out of memory",
        MotionErrorCodes.TimedOut => "making the motion took too long",
        _ => error.Message.TrimEnd('.')
    };

    internal static IReadOnlyList<PairedHost> Paired(string dataDirectory)
    {
        // Motion stays the owner's on a host a friend shares (the gateway refuses it there), so those hosts are left out.
        try { return [.. HostRegistry.Load(dataDirectory).Where(h => !h.Shared)]; }
        catch (Exception error) when (error is InvalidDataException or IOException or UnauthorizedAccessException) { return []; }
    }
}

/// <summary>
/// The motion pool as one <see cref="IMotionMaker"/> (<see cref="MotionPool"/>): availability asks each computer in order until
/// one is ready; a motion goes through <see cref="WorkQueue.Shared"/> (lane <c>motion</c>), so a computer making another motion
/// is passed over for the next one, and when every one is busy the motion waits for the first to finish. <see cref="Used"/>
/// names the computers that made a motion, so their graphics cards are freed later.
/// </summary>
internal sealed class PoolMotionMaker(string dataDirectory, IReadOnlyList<string> hosts) : IMotionMaker, IDisposable
{
    private readonly Dictionary<string, HostMotionMaker> makers = new(StringComparer.Ordinal);
    private readonly HashSet<string> used = new(StringComparer.Ordinal);
    private readonly object gate = new();

    internal string DataDirectory => dataDirectory;

    /// <summary>The computers in the order a motion tries them.</summary>
    internal IReadOnlyList<string> Hosts => hosts;

    /// <summary>The computers that made a motion with this maker.</summary>
    internal IReadOnlyList<string> Used
    {
        get { lock (gate) return [.. used]; }
    }

    public string Where => hosts.Count == 1 ? $"{hosts[0]}'s Motion role" : $"the Motion list ({hosts.Count} computers, {hosts[0]} first)";

    public async Task<MotionMakerAvailability> GetAvailabilityAsync(CancellationToken cancellationToken)
    {
        MotionMakerAvailability? first = null;
        foreach (var host in hosts)
        {
            var availability = await Maker(host).GetAvailabilityAsync(cancellationToken).ConfigureAwait(false);
            if (availability.Available) return availability;
            first ??= availability;
        }
        return first ?? MotionMakerAvailability.Unavailable("No computer in the Motion list is on.");
    }

    public async Task<MotionResult> GenerateAsync(MotionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        var (made, host) = await MotionPool.RunAsync(WorkQueue.Shared, hosts, (id, token) => Maker(id).GenerateAsync(request, token), null,
            cancellationToken).ConfigureAwait(false);
        lock (gate) used.Add(host);
        if (host != hosts[0]) ErrorLog.Info($"Motion: {hosts[0]} couldn't take the motion now, so {host} made it.");
        return made;
    }

    private HostMotionMaker Maker(string host)
    {
        lock (gate)
        {
            if (!makers.TryGetValue(host, out var maker)) makers[host] = maker = new HostMotionMaker(dataDirectory, host, busyRetries: hosts.Count > 1 ? 4 : 40);
            return maker;
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            foreach (var maker in makers.Values) maker.Dispose();
            makers.Clear();
        }
    }
}

/// <summary>
/// Martlet's <c>motion</c> host role (HY-Motion 1.0) on one paired computer, through its gateway (route
/// <c>martlet.gateway.motion.v1</c>): status, generate (the VRMA clip comes back in base64 pieces and a summary) and free.
/// <paramref name="busyRetries"/>: how many times a request waits a quarter second while the gateway runs another request on the
/// route (a status poll, or another companion PC's motion) before it says the computer is busy.
/// </summary>
internal sealed class HostMotionMaker(string dataDirectory, string hostId, int busyRetries = 40) : IMotionMaker, IDisposable
{
    private (PairedHost Host, HostRoute Route, Audio2FaceHostConnection Connection)? open;

    public string Where => $"{hostId}'s Motion role";

    /// <summary>The computer that makes the motions.</summary>
    public string HostId => hostId;

    public async Task<MotionMakerAvailability> GetAvailabilityAsync(CancellationToken cancellationToken)
    {
        try
        {
            var status = await StatusAsync(cancellationToken).ConfigureAwait(false);
            return status["state"]?.ToString() switch
            {
                "ready" => new(true, null, Where),
                "not_provisioned" => MotionMakerAvailability.Unavailable($"{Where} hasn't finished downloading its model yet."),
                "loading" => MotionMakerAvailability.Unavailable($"{Where} is starting."),
                var state => MotionMakerAvailability.Unavailable($"{Where} isn't ready ({state ?? "no state"}).")
            };
        }
        catch (MotionException error) when (error.Code == MotionErrorCodes.Busy)
        {
            // Making another motion (the route holds one request at a time): it can take this one next; the queue waits for it.
            return new(true, null, Where);
        }
        catch (MotionException error)
        {
            return MotionMakerAvailability.Unavailable(error.Message);
        }
    }

    public async Task<MotionResult> GenerateAsync(MotionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        var started = DateTimeOffset.UtcNow;
        var payload = new Dictionary<string, object> { ["operation"] = "generate", ["prompt"] = request.Prompt.Trim(), ["seconds"] = request.Seconds };
        if (request.Seed is { } seed) payload["seed"] = seed;
        var answer = await CallAsync(payload, cancellationToken).ConfigureAwait(false);
        return MotionAnswers.Generated(answer, Where, DateTimeOffset.UtcNow - started);
    }

    /// <summary>The role's status: state (ready, not_provisioned, loading), model, loaded, busy and the seconds it makes.</summary>
    public async Task<JsonObject> StatusAsync(CancellationToken cancellationToken)
    {
        var answer = await CallAsync(new() { ["operation"] = "status" }, cancellationToken).ConfigureAwait(false);
        if (answer.Count != 1) throw Invalid();
        Fail(answer[0]);
        return answer[0];
    }

    /// <summary>Unloads HY-Motion on that computer so the voice and thinking roles get the graphics card back.</summary>
    public async Task<JsonObject> FreeAsync(CancellationToken cancellationToken)
    {
        var answer = await CallAsync(new() { ["operation"] = "free" }, cancellationToken).ConfigureAwait(false);
        if (answer.Count != 1) throw Invalid();
        Fail(answer[0]);
        return answer[0];
    }

    private async Task<IReadOnlyList<JsonObject>> CallAsync(Dictionary<string, object> payload, CancellationToken token)
    {
        for (var attempt = 0; ; attempt++)
        {
            var (host, route, connection) = await OpenAsync(token).ConfigureAwait(false);
            try
            {
                var answer = await connection.MotionOperationAsync(route, payload, token).ConfigureAwait(false);
                return [.. answer.Select(element => JsonNode.Parse(element.GetRawText()) as JsonObject ?? throw Invalid())];
            }
            // The gateway runs one request per route at a time: a status poll can hold it for a moment.
            catch (Audio2FaceHostException error) when (error.Code == "job.busy" && attempt < busyRetries)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), token).ConfigureAwait(false);
            }
            catch (Audio2FaceHostException error)
            {
                throw new MotionException(error.Code switch
                {
                    "request.invalid" or "request.too_large" => MotionErrorCodes.RequestInvalid,
                    "job.busy" => MotionErrorCodes.Busy,
                    "job.deadline" or "gateway.deadline" => MotionErrorCodes.TimedOut,
                    _ => MotionErrorCodes.Unavailable
                }, $"{host.HostId}'s Motion role: {error.Message}", error);
            }
            catch (Exception error) when (error is HttpRequestException or IOException)
            {
                Dispose();
                throw new MotionException(MotionErrorCodes.Unavailable, $"{host.HostId} stopped answering ({error.Message}).", error);
            }
        }
    }

    private async Task<(PairedHost, HostRoute, Audio2FaceHostConnection)> OpenAsync(CancellationToken token)
    {
        if (open is { } known) return known;
        var host = MotionClient.Paired(dataDirectory).FirstOrDefault(h => h.HostId == hostId) ??
            throw new MotionException(MotionErrorCodes.Unavailable, $"This PC isn't paired with {hostId}, which makes motions.");
        Audio2FaceHostConnection? connection = null;
        try
        {
            connection = ClusterSync.Connect(host.Pairing);
            var routes = await connection.ReadRoutesAsync(token).ConfigureAwait(false);
            if (routes.FirstOrDefault(r => r.RouteId == Audio2FaceHostConnection.MotionRouteId) is not { } route)
                throw new MotionException(MotionErrorCodes.Unavailable, $"{hostId} doesn't run the Motion role. Add it there in Martlet hosts.");
            open = (host, route, connection);
            connection = null;
            return open.Value;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error) when (ClusterSync.IsHostFailure(error))
        {
            throw new MotionException(MotionErrorCodes.Unavailable, $"{hostId} isn't reachable ({error.Message}).", error);
        }
        finally { connection?.Dispose(); }
    }

    private void Fail(JsonObject answer) => MotionAnswers.ThrowIfError(answer, Where);

    private MotionException Invalid() => new(MotionErrorCodes.Failed, $"{Where} returned an invalid answer.");

    public void Dispose()
    {
        open?.Connection.Dispose();
        open = null;
    }
}
