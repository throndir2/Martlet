using System.IO;
using System.Net.Http;
using System.Text.Json.Nodes;
using Martlet.Avatar.Audio2Face.Remote;
using Martlet.Core.Motion;

namespace Martlet.Desktop;

/// <summary>
/// The motion maker Martlet uses (docs/MOTION_HOST.md): Martlet's <c>motion</c> host role (HY-Motion 1.0) on the paired computer
/// the shared motion choice names (<see cref="MotionSettings"/>, motion.json), or, by default, on the first paired computer the
/// shared plan says runs the role. Setting <c>MARTLET_MOTION_FIXTURE=1</c> before Martlet starts makes it use the FIXTURE - NOT AI
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

    /// <summary>The paired computer that makes motions (<see cref="MotionSettings.MotionHost"/>: the chosen one, or the first one
    /// the shared plan says runs the motion role among this PC's pairings, never a host a friend shares), or null. Reads
    /// files only, never the network, so a motion job can keep background thinks off that computer.</summary>
    internal static string? HostId(string dataDirectory) =>
        Settings(dataDirectory).MotionHost(ClusterSync.LoadPlan(dataDirectory), [.. Paired(dataDirectory).Select(h => h.HostId)]);

    /// <summary>Whether motions can be asked for: the fixture is on, or the motion choice names a computer (files only).</summary>
    internal static bool IsSetUp(string dataDirectory) => Fixture || HostId(dataDirectory) is not null;

    /// <summary>The motion maker for the shared choice, or null while motion is off or no computer runs the role. Dispose it after
    /// use, or hand it to <see cref="FreeLater"/>, which disposes it.</summary>
    internal static IMotionMaker? For(string dataDirectory)
    {
        if (Fixture) return new FixtureMotionMaker(TimeSpan.FromMilliseconds(300));
        return HostId(dataDirectory) is { } host ? new HostMotionMaker(dataDirectory, host) : null;
    }

    /// <summary>Frees the motion computer's graphics card now (unloads HY-Motion); false when it couldn't (not set up, not
    /// reachable, or making a motion).</summary>
    internal static async Task<bool> FreeAsync(string dataDirectory, CancellationToken cancellationToken)
    {
        if (HostId(dataDirectory) is not { } host) return false;
        using var maker = new HostMotionMaker(dataDirectory, host);
        try
        {
            await maker.FreeAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (MotionException error)
        {
            ErrorLog.Info($"Motion: couldn't free the graphics card on {maker.Where} ({error.Message}).");
            return false;
        }
    }

    /// <summary>After a motion on Martlet's motion role: frees the graphics card of the computer that made it once
    /// <see cref="FreeAfter"/> passes without another motion there, so the voice, listening and a local Thinking model get it
    /// back. Each computer has its own wait. Disposes <paramref name="maker"/>.</summary>
    internal static void FreeLater(IMotionMaker maker)
    {
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
/// Martlet's <c>motion</c> host role (HY-Motion 1.0) on one paired computer, through its gateway (route
/// <c>martlet.gateway.motion.v1</c>): status, generate (the VRMA clip comes back in base64 pieces and a summary) and free.
/// </summary>
internal sealed class HostMotionMaker(string dataDirectory, string hostId) : IMotionMaker, IDisposable
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
            catch (Audio2FaceHostException error) when (error.Code == "job.busy" && attempt < 40)
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
