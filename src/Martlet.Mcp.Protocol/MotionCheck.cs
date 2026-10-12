using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Martlet.Avatar.Audio2Face.Remote;
using Martlet.Core.Cluster;
using Martlet.Core.Motion;

namespace Martlet.Mcp;

/// <summary>motion_status and motion_check (docs/MOTION_HOST.md). The status reads a data directory's motion choice (motion.json,
/// the shared <c>motion</c> setting), the computers the shared plan (cluster.json) says run the motion role, this PC's pairings
/// (hosts.json) and the computer a motion goes to now, plus the route and the request bounds; it never uses the network. The
/// check makes one motion and reports the VRMA clip: with the FIXTURE - NOT AI maker, with a motion role's worker on loopback
/// (its GET /status and POST /generate), or through a paired computer's gateway (route martlet.gateway.motion.v1, as the desktop
/// asks it); it can save the .vrma file and free the graphics card after.</summary>
internal static class MotionCheck
{
    internal static object Status(string dataDirectory)
    {
        var (settings, state) = MotionSettings.Read(dataDirectory);
        var plan = Plan(dataDirectory);
        var paired = PairedHosts(dataDirectory).Select(h => h.HostId).ToArray();
        return new
        {
            settings = new { file = state, sharedKey = MotionSettings.SharedKey, place = settings.Place.ToString(), hostId = settings.HostId,
                on = settings.On, isDefault = settings.IsDefault, describe = settings.Describe() },
            planHosts = plan.Nodes.Where(n => !n.Removed && n.Roles.Any(r => r.Kind == MotionSettings.RoleKind))
                .Select(n => new { hostId = n.HostId, model = n.Roles.First(r => r.Kind == MotionSettings.RoleKind).Model, paired = paired.Contains(n.HostId) }),
            paired,
            motionHost = settings.MotionHost(plan, paired),
            fixtureVariable = "MARTLET_MOTION_FIXTURE",
            route = new { id = Audio2FaceHostConnection.MotionRouteId, path = Audio2FaceHostConnection.MotionPath,
                longestMinutes = Audio2FaceHostConnection.MotionLongest.TotalMinutes },
            role = new { kind = MotionSettings.RoleKind, models = new[] { "hy-motion-1.0-lite", "hy-motion-1.0" }, minimumGraphicsMemoryGb = new { lite = 24, standard = 26 } },
            bounds = new
            {
                MotionRequest.MinimumSeconds, MotionRequest.MaximumSeconds, MotionRequest.DefaultSeconds, MotionRequest.MinimumPromptCharacters,
                MotionRequest.MaximumPromptCharacters, MotionRequest.MaximumPromptWords, MotionRequest.MaximumSeed, MotionFiles.MaximumBytes
            }
        };
    }

    /// <summary>Makes one motion with <paramref name="place"/> ("fixture", "worker" with <paramref name="endpoint"/>, or "host"
    /// with <paramref name="dataDirectory"/> and optionally <paramref name="hostId"/>) and reports it.</summary>
    internal static async Task<object> RunAsync(string? place, string? endpoint, string? dataDirectory, string? hostId, string? prompt,
        double? seconds, long? seed, bool free, string? saveDirectory, CancellationToken cancellation)
    {
        place = (place ?? "fixture").ToLowerInvariant();
        var request = new MotionRequest
        {
            Prompt = prompt ?? "A person waves with the right hand, then bows.", Seconds = seconds ?? 3, Seed = seed ?? 7
        };
        try { request.Validate(); }
        catch (MotionException error) { throw new ArgumentException(error.Message); }
        if (saveDirectory is not null && !Path.IsPathFullyQualified(saveDirectory))
            throw new ArgumentException("saveDirectory must be an absolute path.");
        var watch = Stopwatch.StartNew();
        JsonNode? status = null;
        object? freed = null;
        MotionResult result;
        string where;
        try
        {
            switch (place)
            {
                case "fixture":
                {
                    var maker = new FixtureMotionMaker(TimeSpan.FromMilliseconds(20));
                    where = maker.Where;
                    result = await maker.GenerateAsync(request, cancellation);
                    break;
                }
                case "worker":
                {
                    var uri = Endpoint(endpoint ?? throw new ArgumentException("place worker needs endpoint, like http://127.0.0.1:50090/."));
                    where = $"the motion worker at {uri}";
                    using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(10) };
                    status = JsonNode.Parse(await http.GetStringAsync(new Uri(uri, "status"), cancellation));
                    (result, var problem) = await WorkerGenerateAsync(http, uri, request, where, cancellation);
                    if (problem is not null) return new { ok = false, place, where, status, error = problem, milliseconds = watch.ElapsedMilliseconds };
                    result = result with { Model = status?["model"]?.ToString() ?? "", Fixture = status?["fixture"] is not null };
                    if (free)
                    {
                        using var answer = await http.PostAsync(new Uri(uri, "free"), new StringContent("{}", Encoding.UTF8, "application/json"), cancellation);
                        freed = JsonNode.Parse(await answer.Content.ReadAsStringAsync(cancellation));
                    }
                    break;
                }
                case "host":
                {
                    if (dataDirectory is null) throw new ArgumentException("place host needs dataDirectory (the desktop's data folder with its pairings).");
                    var hosts = PairedHosts(dataDirectory);
                    hostId ??= MotionSettings.Load(dataDirectory).MotionHost(Plan(dataDirectory), [.. hosts.Select(h => h.HostId)])
                        ?? throw new ArgumentException("No paired computer runs the Motion role in that data directory; pass hostId.");
                    var host = hosts.FirstOrDefault(h => h.HostId == hostId) ?? throw new ArgumentException($"{hostId} isn't paired in that data directory.");
                    where = $"{hostId}'s Motion role";
                    using var connection = Connect(host);
                    var route = (await connection.ReadRoutesAsync(cancellation)).FirstOrDefault(r => r.RouteId == Audio2FaceHostConnection.MotionRouteId)
                        ?? throw new MotionException(MotionErrorCodes.Unavailable, $"{hostId} doesn't run the Motion role.");
                    status = Objects(await connection.MotionOperationAsync(route, new Dictionary<string, object> { ["operation"] = "status" }, cancellation)).Single();
                    var payload = new Dictionary<string, object> { ["operation"] = "generate", ["prompt"] = request.Prompt, ["seconds"] = request.Seconds };
                    if (request.Seed is { } chosen) payload["seed"] = chosen;
                    var started = DateTimeOffset.UtcNow;
                    result = MotionAnswers.Generated(Objects(await connection.MotionOperationAsync(route, payload, cancellation)), where,
                        DateTimeOffset.UtcNow - started);
                    if (free) freed = Objects(await connection.MotionOperationAsync(route, new Dictionary<string, object> { ["operation"] = "free" }, cancellation)).Single();
                    break;
                }
                default:
                    throw new ArgumentException("place is fixture, worker or host.");
            }
        }
        catch (MotionException error)
        {
            return new { ok = false, place, status, code = error.Code, error = error.Message, milliseconds = watch.ElapsedMilliseconds };
        }
        catch (Exception error) when (error is HttpRequestException or Audio2FaceHostException or IOException or JsonException or InvalidOperationException)
        {
            return new { ok = false, place, status, error = $"{error.GetType().Name}: {error.Message}", milliseconds = watch.ElapsedMilliseconds };
        }

        string? saved = null;
        if (saveDirectory is not null)
        {
            Directory.CreateDirectory(saveDirectory);
            saved = Path.Combine(saveDirectory, "motion" + MotionFiles.Extension);
            await File.WriteAllBytesAsync(saved, result.Vrma, cancellation);
        }
        return new
        {
            ok = MotionFiles.IsVrma(result.Vrma) && result.Frames > 0, place, where, fixture = result.Fixture, engine = result.Engine, model = result.Model,
            requested = new { prompt = request.Prompt, seconds = request.Seconds, seed = request.Seed },
            frames = result.Frames, fps = result.Fps, seconds = result.Seconds, seed = result.Seed, bytes = result.Vrma.Length,
            sha256 = Convert.ToHexStringLower(SHA256.HashData(result.Vrma)), vrma = Facts(result.Vrma),
            took = Math.Round(result.Took.TotalSeconds, 2), milliseconds = watch.ElapsedMilliseconds, status, freed, saved
        };
    }

    // POST /generate on a worker: the clip and its X-Martlet-Motion-* facts, or the worker's error.
    private static async Task<(MotionResult, object?)> WorkerGenerateAsync(HttpClient http, Uri endpoint, MotionRequest request, string where,
        CancellationToken cancellation)
    {
        var body = new Dictionary<string, object> { ["prompt"] = request.Prompt, ["seconds"] = request.Seconds };
        if (request.Seed is { } seed) body["seed"] = seed;
        var started = DateTimeOffset.UtcNow;
        using var response = await http.PostAsync(new Uri(endpoint, "generate"),
            new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"), cancellation);
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellation);
        if (!response.IsSuccessStatusCode || response.Content.Headers.ContentType?.MediaType != MotionFiles.MediaType)
            return (null!, new { httpStatus = (int)response.StatusCode, answer = Encoding.UTF8.GetString(bytes.AsSpan(0, Math.Min(bytes.Length, 2000))) });
        long? Header(string name) => response.Headers.TryGetValues("X-Martlet-Motion-" + name, out var values) &&
            long.TryParse(values.FirstOrDefault(), out var value) ? value : null;
        MotionFiles.Require(bytes, where);
        var frames = (int)(Header("Frames") ?? 0);
        var fps = (int)(Header("Fps") ?? 30);
        return (new MotionResult
        {
            Vrma = bytes, Frames = frames, Fps = fps, Seconds = frames / (double)Math.Max(1, fps), Seed = Header("Seed"), Engine = "hy-motion",
            Model = "", Where = where, Took = DateTimeOffset.UtcNow - started
        }, null);
    }

    /// <summary>What the clip's glTF says: its VRMC_vrm_animation version, how many humanoid bones and animation channels it has,
    /// its keyframe count and length in seconds.</summary>
    internal static object Facts(byte[] vrma)
    {
        var length = (int)BinaryPrimitives.ReadUInt32LittleEndian(vrma.AsSpan(12));
        var json = JsonNode.Parse(Encoding.UTF8.GetString(vrma, 20, length))!;
        var animation = json["extensions"]?["VRMC_vrm_animation"];
        var input = json["animations"]?[0]?["samplers"]?[0]?["input"]?.GetValue<int>();
        var accessor = input is { } index ? json["accessors"]?[index] : null;
        return new
        {
            specVersion = animation?["specVersion"]?.ToString(),
            humanBones = animation?["humanoid"]?["humanBones"]?.AsObject().Count ?? 0,
            channels = json["animations"]?[0]?["channels"]?.AsArray().Count ?? 0,
            keyframes = accessor?["count"]?.GetValue<int>(),
            lastKeySeconds = accessor?["max"]?[0]?.GetValue<double>()
        };
    }

    private static IReadOnlyList<JsonObject> Objects(IReadOnlyList<JsonElement> answer) =>
        [.. answer.Select(element => JsonNode.Parse(element.GetRawText()) as JsonObject ?? throw new InvalidOperationException("Not a JSON object."))];

    private static ClusterPlan Plan(string dataDirectory)
    {
        try { return ClusterPlan.Parse(File.ReadAllBytes(Path.Combine(dataDirectory, "cluster.json"))); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Martlet.Core.Contracts.ContractException) { return ClusterPlan.Empty; }
    }

    // This PC's pairings in hosts.json, other than hosts a friend shares (they keep motion for their owner).
    private static IReadOnlyList<Martlet.Avatar.Hosting.AvatarRemoteHost> PairedHosts(string dataDirectory)
    {
        try
        {
            var path = Path.Combine(dataDirectory, "hosts.json");
            if (!File.Exists(path) || new FileInfo(path).Length > 262_144) return [];
            var hosts = new List<Martlet.Avatar.Hosting.AvatarRemoteHost>();
            foreach (var entry in (JsonNode.Parse(File.ReadAllText(path))?["hosts"] as JsonArray ?? []).OfType<JsonObject>())
            {
                if (entry["access"]?.GetValue<string>() == HostSignInAccess.Friend || entry["pairing"] is not JsonObject pairing) continue;
                var host = new Martlet.Avatar.Hosting.AvatarRemoteHost
                {
                    Origin = pairing["origin"]!.GetValue<string>(), HostId = pairing["hostId"]!.GetValue<string>(),
                    SpkiFingerprint = pairing["spkiFingerprint"]!.GetValue<string>(), DeviceId = pairing["deviceId"]!.GetValue<string>(),
                    CredentialId = pairing["credentialId"]!.GetValue<string>()
                };
                host.Validate();
                hosts.Add(host);
            }
            return hosts;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or
            NullReferenceException or Martlet.Core.Contracts.ContractException)
        {
            return [];
        }
    }

    // A connection signed with the pairing secret the desktop saved in Windows Credential Manager (as the desktop connects).
    private static Audio2FaceHostConnection Connect(Martlet.Avatar.Hosting.AvatarRemoteHost host)
    {
        using var read = new Martlet.Credentials.Windows.WindowsCredentialStore().ReadAvatarHostSecret(host.HostId, host.CredentialId);
        if (read.Error != Martlet.Core.Settings.CredentialError.None || read.Secret is null)
            throw new InvalidOperationException($"This PC has no pairing secret for {host.HostId} from that data directory ({read.Error}).");
        Audio2FaceHostConnection? connection = null;
        read.Secret.Use(secret => connection = new Audio2FaceHostConnection(new Audio2FaceHostPairing
        {
            Origin = host.Origin, HostId = host.HostId, SpkiFingerprint = host.SpkiFingerprint, DeviceId = host.DeviceId,
            CredentialId = host.CredentialId
        }, secret));
        return connection!;
    }

    private static Uri Endpoint(string value)
    {
        if (!Uri.TryCreate(value.EndsWith('/') ? value : value + "/", UriKind.Absolute, out var endpoint) || endpoint.Scheme != Uri.UriSchemeHttp ||
            !System.Net.IPAddress.TryParse(endpoint.Host.Trim('[', ']'), out var address) || !System.Net.IPAddress.IsLoopback(address) ||
            endpoint.AbsolutePath != "/")
            throw new ArgumentException("endpoint must be a numeric loopback address such as http://127.0.0.1:50090/.");
        return endpoint;
    }
}
