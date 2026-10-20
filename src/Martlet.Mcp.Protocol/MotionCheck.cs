using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Martlet.HostClient;
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
    internal static object Status(string dataDirectory, string? device = null)
    {
        var (settings, state) = MotionSettings.Read(dataDirectory);
        var plan = Plan(dataDirectory);
        var paired = PairedHosts(dataDirectory).Select(h => h.HostId).ToArray();
        device ??= Martlet.Diagnostics.LocalLogs.ThisDeviceId(dataDirectory);
        var own = OwnHost(dataDirectory);
        var list = PoolSettings.LoadFor(dataDirectory, PoolAreas.Motion);
        var tries = MotionPool.Members(list, settings, plan, device, own, paired);
        return new
        {
            pool = new
            {
                area = PoolAreas.Motion.Id, page = PoolAreas.Motion.Page, listed = list is not null,
                from = list is null ? "motion.json (the older choice: no Motion list yet)" : "pools.json (the Motion list)",
                off = list is null ? !settings.On : list.NoneOn,
                members = list?.Members.Select(m => new { key = m.Key, kind = m.Kind.ToString(), name = m.Name, on = !m.Off, onlyFor = m.OnlyFor,
                    usableHere = m.Allows(device) }),
                device, ownHost = own,
                tries = tries.Select(t => new { key = t.Member.Key, hostId = t.HostId, paired = paired.Contains(t.HostId) })
            },
            settings = new { file = state, sharedKey = MotionSettings.SharedKey, place = settings.Place.ToString(), hostId = settings.HostId,
                on = settings.On, isDefault = settings.IsDefault, describe = settings.Describe() },
            planHosts = plan.Nodes.Where(n => !n.Removed && n.Roles.Any(r => r.Kind == MotionSettings.RoleKind))
                .Select(n => new { hostId = n.HostId, model = n.Roles.First(r => r.Kind == MotionSettings.RoleKind).Model, paired = paired.Contains(n.HostId) }),
            paired,
            motionHost = tries.Count > 0 ? tries[0].HostId : null,
            fixtureVariable = "MARTLET_MOTION_FIXTURE",
            route = new { id = HostConnection.MotionRouteId, path = HostConnection.MotionPath,
                longestMinutes = HostConnection.MotionLongest.TotalMinutes },
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
                    hostId ??= MotionPool.Members(PoolSettings.LoadFor(dataDirectory, PoolAreas.Motion), MotionSettings.Load(dataDirectory),
                            Plan(dataDirectory), Martlet.Diagnostics.LocalLogs.ThisDeviceId(dataDirectory), OwnHost(dataDirectory),
                            [.. hosts.Select(h => h.HostId)]).Select(m => m.HostId).FirstOrDefault()
                        ?? throw new ArgumentException("No computer in that data directory's Motion list (or motion choice) runs the Motion role; pass hostId.");
                    var host = hosts.FirstOrDefault(h => h.HostId == hostId) ?? throw new ArgumentException($"{hostId} isn't paired in that data directory.");
                    where = $"{hostId}'s Motion role";
                    using var connection = Connect(host);
                    var route = (await connection.ReadRoutesAsync(cancellation)).FirstOrDefault(r => r.RouteId == HostConnection.MotionRouteId)
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
        catch (Exception error) when (error is HttpRequestException or HostConnectionException or IOException or JsonException or InvalidOperationException)
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
    private static HostConnection Connect(Martlet.Avatar.Hosting.AvatarRemoteHost host)
    {
        using var read = new Martlet.Credentials.Windows.WindowsCredentialStore().ReadAvatarHostSecret(host.HostId, host.CredentialId);
        if (read.Error != Martlet.Core.Settings.CredentialError.None || read.Secret is null)
            throw new InvalidOperationException($"This PC has no pairing secret for {host.HostId} from that data directory ({read.Error}).");
        HostConnection? connection = null;
        read.Secret.Use(secret => connection = new HostConnection(new HostPairing
        {
            Origin = host.Origin, HostId = host.HostId, SpkiFingerprint = host.SpkiFingerprint, DeviceId = host.DeviceId,
            CredentialId = host.CredentialId
        }, secret));
        return connection!;
    }

    // The pairing saved as this PC's own host service (Docker Desktop here) in hosts.json, or null.
    private static string? OwnHost(string dataDirectory)
    {
        try
        {
            var path = Path.Combine(dataDirectory, "hosts.json");
            if (!File.Exists(path) || new FileInfo(path).Length > 262_144) return null;
            return (JsonNode.Parse(File.ReadAllText(path))?["hosts"] as JsonArray ?? []).OfType<JsonObject>()
                .FirstOrDefault(h => h["method"]?.GetValue<string>() == "ThisPcDocker")?["pairing"]?["hostId"]?.GetValue<string>();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException) { return null; }
    }

    // ---------- motion_pool_check ----------

    /// <summary>A simulated computer with the motion role: free, busy with another motion (motion.busy) until it was asked
    /// <see cref="BusyFor"/> times, not answering, or failing the motion itself (motion.failed).</summary>
    private sealed class Mover(string id)
    {
        public string Id { get; } = id;
        public int BusyFor { get; init; }
        public bool Down { get; init; }
        public bool Fails { get; init; }
        public int Asked;
        public int Made;

        public async Task<string> Make(CancellationToken token)
        {
            var asked = Interlocked.Increment(ref Asked);
            if (Down) throw new HttpRequestException($"{Id} didn't answer.");
            if (asked <= BusyFor) throw new MotionException(MotionErrorCodes.Busy, $"{Id}'s Motion role: it is making another motion.");
            if (Fails) throw new MotionException(MotionErrorCodes.Failed, $"{Id}'s Motion role: the motion failed.");
            await Task.Delay(5, token);
            Interlocked.Increment(ref Made);
            return Id;
        }
    }

    private sealed record Moved(string? Host, string? Code);

    private static async Task<Moved> MoveAsync(IReadOnlyList<Mover> pool)
    {
        try
        {
            var (made, host) = await MotionPool.RunAsync(new WorkQueue { Retry = TimeSpan.FromMilliseconds(10) }, [.. pool.Select(m => m.Id)],
                (id, token) => pool.First(m => m.Id == id).Make(token), null, CancellationToken.None);
            return made == host ? new(host, null) : new(null, "mismatch");
        }
        catch (MotionException error) { return new(null, error.Code); }
        catch (HttpRequestException) { return new(null, "unreachable"); }
    }

    /// <summary>motion_pool_check: rehearses the production motion pool (<see cref="MotionPool"/> through <see cref="WorkQueue"/>,
    /// what the desktop's PoolMotionMaker runs) with simulated motion computers. NOT real hosts or models; nothing leaves the
    /// process.</summary>
    internal static async Task<object> PoolCheckAsync(CancellationToken cancellation)
    {
        List<object> steps = [];
        var ok = true;
        void Step(string name, bool passed, object detail)
        {
            ok &= passed;
            steps.Add(new { name, passed, detail });
        }
        var now = DateTimeOffset.UtcNow;
        var plan = ClusterPlan.Empty
            .Observe("m3-host", "https://192.168.1.33:9443", [new() { Kind = MotionSettings.RoleKind, Model = "hy-motion-1.0-lite" }], false, "desk-2", now)
            .Observe("desk-host", "https://192.168.1.20:9443", [new() { Kind = MotionSettings.RoleKind, Model = "hy-motion-1.0" }], false, "desk-2", now)
            .Observe("m1-host", "https://192.168.1.31:9443", [new() { Kind = "pictures", Model = "z-image-turbo" }], false, "desk-2", now)
            .Observe("friend-host", "https://192.168.1.40:9443", [new() { Kind = MotionSettings.RoleKind, Model = "hy-motion-1.0" }], false, "desk-2", now);
        string[] paired = ["m1-host", "m3-host", "desk-host"];

        var list = new PoolList
        {
            Area = PoolAreas.Motion.Id,
            Members = [PoolMember.Computer("m3-host"), PoolMember.ThisPc(), PoolMember.Computer("m1-host") with { Off = true },
                PoolMember.Computer("friend-host"), PoolMember.Computer("m4-host") with { OnlyFor = ["desk-9"] }]
        };
        var listed = MotionPool.Members(list, new MotionSettings { Place = MotionPlace.Off }, plan, "desk-2", "desk-host", paired);
        var empty = MotionPool.Members(new PoolList { Area = PoolAreas.Motion.Id }, new MotionSettings(), plan, "desk-2", "desk-host", paired);
        var allOff = MotionPool.Members(list with { Members = [.. list.Members.Select(m => m with { Off = true })] }, new MotionSettings(), plan,
            "desk-2", "desk-host", paired);
        Step("The Motion list (pools.json) wins over motion.json: its members in order, This PC as its own host service; a member turned " +
            "off, not paired here (a friend's host) or kept for another companion PC is left out; an empty list, or none on, is off",
            listed.Select(m => m.HostId).SequenceEqual(["m3-host", "desk-host"]) && listed[1].Member.Kind == PoolMemberKind.ThisPc &&
            empty.Count == 0 && allOff.Count == 0,
            new { listed = listed.Select(m => new { m.Member.Key, m.HostId }), emptyList = empty.Count, allOff = allOff.Count });

        var automatic = MotionPool.Members(null, new MotionSettings(), plan, "desk-2", "desk-host", paired);
        var one = MotionPool.Members(null, new MotionSettings { Place = MotionPlace.Host, HostId = "m3-host" }, plan, "desk-2", "desk-host", paired);
        var off = MotionPool.Members(null, new MotionSettings { Place = MotionPlace.Off }, plan, "desk-2", "desk-host", paired);
        Step("Before a list: automatic tries every paired computer the shared plan says runs Motion, this PC's own first (never a " +
            "friend's host or one without the role); one chosen computer; off: none",
            automatic.Select(m => m.HostId).SequenceEqual(["desk-host", "m3-host"]) && one.Select(m => m.HostId).SequenceEqual(["m3-host"]) &&
            off.Count == 0,
            new { automatic = automatic.Select(m => m.HostId), one = one.Select(m => m.HostId), off = off.Count });

        var runners = MotionPool.Runners(plan, paired, "desk-host");
        var fromHost = MotionPool.FirstList(new MotionSettings { Place = MotionPlace.Host, HostId = "m3-host" }, runners);
        var fromOff = MotionPool.FirstList(new MotionSettings { Place = MotionPlace.Off }, runners);
        var fromNothing = MotionPool.FirstList(new MotionSettings(), []);
        Step("The first list keeps the older choice: one computer first, then the others that run Motion; off: all of them off; " +
            "automatic with no computer running Motion makes no list (adding the role later still turns motion on)",
            fromHost?.Members.Select(m => m.Key).SequenceEqual(["host:m3-host", "host:desk-host"]) == true && fromHost.Members.All(m => !m.Off) &&
            fromOff is { NoneOn: true, Members.Count: 2 } && fromNothing is null,
            new { fromHost = fromHost?.Members.Select(m => m.Key), fromOff = fromOff?.Members.Select(m => new { m.Key, on = !m.Off }), fromNothing });

        Mover first = new("m3-host"), second = new("desk-host");
        var free = await MoveAsync([first, second]);
        Step("The first computer is free: it makes the motion and no other computer is asked",
            free.Host == "m3-host" && second.Asked == 0, new { free, asked = new { m3 = first.Asked, desk = second.Asked } });

        Mover busy = new("m3-host") { BusyFor = 1000 }, idle = new("desk-host");
        var passed = await MoveAsync([busy, idle]);
        Step("The first computer is making another motion (motion.busy): the next one takes it at once",
            passed.Host == "desk-host" && busy.Made == 0, passed);

        Mover down = new("m3-host") { Down = true }, up = new("desk-host");
        var around = await MoveAsync([down, up]);
        Step("A computer that doesn't answer is passed over", around.Host == "desk-host", around);

        Mover soon = new("m3-host") { BusyFor = 3 }, later = new("desk-host") { BusyFor = 1000 };
        var waited = await MoveAsync([soon, later]);
        Step("Every computer is busy: the motion waits and goes to the first one that frees", waited.Host == "m3-host" && soon.Asked >= 4, waited);

        Mover failing = new("m3-host") { Fails = true }, spare = new("desk-host");
        var failed = await MoveAsync([failing, spare]);
        Step("A motion that fails on its computer (motion.failed) is not made again elsewhere",
            failed.Code == MotionErrorCodes.Failed && spare.Asked == 0, failed);

        var nothing = await MoveAsync([]);
        Step("An empty list: motion.unavailable, and nothing is asked", nothing.Code == MotionErrorCodes.Unavailable, nothing);

        cancellation.ThrowIfCancellationRequested();
        return new { ok, fixture = "simulated motion computers (NOT real hosts or models)", steps };
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
