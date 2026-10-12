using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Martlet.Core.Motion;
using Martlet.Gateway.Motion;

namespace Martlet.Gateway.Tests;

public sealed class MotionRelayTests
{
    [Fact]
    public void Motion_route_contract_parses_payloads_and_rejects_invalid_ones()
    {
        var route = Route();
        Assert.Equal(("martlet.gateway.motion.v1", "/martlet/v1/inference/motion", "martlet.motion-relay", "1.0"),
            (route.RouteId, route.Path, route.ContractId, route.ContractVersion));
        Assert.Equal(GatewayInferenceKind.Motion, route.Kind);
        Assert.Equal(GatewayRole.Voice, route.RequiredRole);
        Assert.Equal(TimeSpan.FromMinutes(8), route.MaximumDuration);
        Assert.Equal(GatewayInferenceKind.Motion, GatewayInferenceRoute.FromCapability(GatewayInferenceRouteCapability.From(route)).Kind);
        Assert.False(GatewayInferenceRouteRegistry.FriendsMayUse(route));
        Assert.Equal("Motion", GatewayGpus.RouteName(route));

        Assert.Equal(GatewayMotionOperation.Status, Parse(route, new { operation = "status" }).Operation);
        Assert.Equal(GatewayMotionOperation.Free, Parse(route, new { operation = "free" }).Operation);
        var generate = Parse(route, new { operation = "generate", prompt = "A person waves with the right hand.", seconds = 3.5, seed = 42 });
        Assert.Equal((GatewayMotionOperation.Generate, "A person waves with the right hand.", 3.5, (long?)42),
            (generate.Operation, generate.Prompt, generate.Seconds, generate.Seed));
        Assert.Null(Parse(route, new { operation = "generate", prompt = "A person bows.", seconds = 2 }).Seed);

        foreach (var invalid in new object[]
        {
            new { operation = "generate", prompt = "A person bows.", seconds = 0.5 },
            new { operation = "generate", prompt = "A person bows.", seconds = 12.5 },
            new { operation = "generate", prompt = "A person bows." },
            new { operation = "generate", prompt = "A person bows.", seconds = 2, seed = -1 },
            new { operation = "generate", prompt = "A person bows.", seconds = 2, seed = 2_147_483_648L },
            new { operation = "generate", prompt = "A person bows.", seconds = 2, model = "hy-motion-1.0" },
            new { operation = "generate", prompt = "ok", seconds = 2 },
            new { operation = "generate", prompt = "A person\u0001bows.", seconds = 2 },
            new { operation = "generate", prompt = new string('a', GatewayInferenceRoute.MotionMaximumPromptBytes + 1), seconds = 2 },
            new { operation = "status", prompt = "A person bows." },
            new { operation = "view" }
        })
            Assert.Equal("request.invalid", Assert.Throws<GatewayProtocolException>(() => GatewayInferenceJson.ParseRequest(
                Request(route, invalid), route, DateTimeOffset.UtcNow)).Failure.Code);
    }

    [Fact]
    public async Task Permission_denies_wrong_role_or_payload_and_unknown_models_are_refused()
    {
        await using var worker = new MotionRelayWorker(new Uri("http://127.0.0.1:50090/"), handler: new ScriptedHandler(_ => Json(new { state = "ready" })));
        var request = Inference(worker.Route, new GatewayMotionPayload(GatewayMotionOperation.Status));
        Assert.Equal(GatewayInferenceWorkerFailure.PermissionDenied, (await Assert.ThrowsAsync<GatewayInferenceWorkerException>(async () =>
            await worker.AcquirePermissionAsync(request, Principal(GatewayRole.Memory), CancellationToken.None))).Failure);
        var wrongPayload = Inference(worker.Route, new GatewayPicturePayload(GatewayPictureOperation.Status));
        Assert.Equal(GatewayInferenceWorkerFailure.PermissionDenied, (await Assert.ThrowsAsync<GatewayInferenceWorkerException>(async () =>
            await worker.AcquirePermissionAsync(wrongPayload, Principal(GatewayRole.Voice), CancellationToken.None))).Failure);

        await using var standard = new MotionRelayWorker(new Uri("http://127.0.0.1:50090/"), "hy-motion-1.0");
        Assert.Equal(("hy-motion-1.0", "620dd559f8d964aac2f82f1204fe6a35ad8ad14d"), (standard.Route.ModelId, standard.Route.ModelRevision));
        Assert.NotEqual(worker.Route.ModelSha256, standard.Route.ModelSha256);
        Assert.Throws<ArgumentException>(() => new MotionRelayWorker(new Uri("http://127.0.0.1:50090/"), "hy-motion-2.0"));
        Assert.Throws<ArgumentException>(() => new MotionRelayWorker(new Uri("http://192.168.1.5:50090/")));
    }

    [Fact]
    public async Task Status_and_free_are_relayed_and_a_stopped_service_reads_as_loading()
    {
        await using var down = new MotionRelayWorker(new Uri("http://127.0.0.1:1/"));
        Assert.Equal("loading", Assert.Single(await ObjectsAsync(down, new(GatewayMotionOperation.Status))).GetProperty("state").GetString());
        Assert.Equal("worker.unavailable", Assert.Single(await EventsAsync(down, new(GatewayMotionOperation.Free),
            GatewayInferenceEventKind.Failed)).ErrorCode);

        await using var worker = new MotionRelayWorker(new Uri("http://127.0.0.1:50090/"), handler: new ScriptedHandler(request =>
            request.RequestUri!.AbsolutePath switch
            {
                "/status" => Json(new { state = "ready", engine = "hy-motion", model = "hy-motion-1.0-lite", loaded = false, busy = false }),
                "/free" when request.Method == HttpMethod.Post => Json(new { state = "freed", was_loaded = true }),
                var path => throw new InvalidOperationException(path)
            }));
        var status = Assert.Single(await ObjectsAsync(worker, new(GatewayMotionOperation.Status)));
        Assert.Equal(("ready", "hy-motion"), (status.GetProperty("state").GetString(), status.GetProperty("engine").GetString()));
        var freed = Assert.Single(await ObjectsAsync(worker, new(GatewayMotionOperation.Free)));
        Assert.True(freed.GetProperty("was_loaded").GetBoolean());
    }

    [Fact]
    public async Task Generate_sends_only_prompt_seconds_and_seed_and_returns_the_clip_in_pieces_and_a_summary()
    {
        // FIXTURE - NOT AI: a VRMA from the fixture maker padded past two event pieces.
        var clip = FixtureMotionMaker.Glb(Encoding.UTF8.GetBytes("{\"extensionsUsed\":[\"VRMC_vrm_animation\"]}"), new byte[400_000]);
        string? sent = null;
        await using var worker = new MotionRelayWorker(new Uri("http://127.0.0.1:50090/"), handler: new ScriptedHandler(request =>
        {
            Assert.Equal(("/generate", HttpMethod.Post), (request.RequestUri!.AbsolutePath, request.Method));
            sent = request.Content!.ReadAsStringAsync().Result;
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(clip) };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("model/gltf-binary");
            response.Headers.Add("X-Martlet-Motion-Frames", "120");
            response.Headers.Add("X-Martlet-Motion-Fps", "30");
            response.Headers.Add("X-Martlet-Motion-Seed", "42");
            response.Headers.Add("X-Martlet-Motion-Milliseconds", "9000");
            response.Headers.Add("X-Martlet-Motion-Load-Milliseconds", "41000");
            return response;
        }));

        var objects = await ObjectsAsync(worker, new(GatewayMotionOperation.Generate, "A person jumps.", 4, 42));
        using (var body = JsonDocument.Parse(sent!))
            Assert.Equal(["prompt", "seconds", "seed"], body.RootElement.EnumerateObject().Select(p => p.Name));
        Assert.Equal(4, objects.Count);
        using var joined = new MemoryStream();
        foreach (var piece in objects.Take(3))
        {
            Assert.Equal(("model/gltf-binary", clip.Length, joined.Length),
                (piece.GetProperty("media_type").GetString(), piece.GetProperty("total_bytes").GetInt32(), piece.GetProperty("offset").GetInt64()));
            joined.Write(Convert.FromBase64String(piece.GetProperty("data_base64").GetString()!));
        }
        Assert.Equal(clip, joined.ToArray());
        var summary = objects[3];
        Assert.Equal(("done", "hy-motion-1.0-lite", 120, 30, 42, 41000),
            (summary.GetProperty("state").GetString(), summary.GetProperty("model").GetString(), summary.GetProperty("frames").GetInt32(),
                summary.GetProperty("fps").GetInt32(), summary.GetProperty("seed").GetInt32(), summary.GetProperty("load_milliseconds").GetInt32()));
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(clip)), summary.GetProperty("sha256").GetString());
    }

    [Fact]
    public async Task Service_failures_come_back_as_one_error_object_and_bad_answers_fail()
    {
        await using var busy = Worker(_ => Json(new { error = new { code = "motion.busy", summary = "Making another motion." } }, HttpStatusCode.Conflict));
        var error = Assert.Single(await ObjectsAsync(busy, new(GatewayMotionOperation.Generate, "A person runs.", 2))).GetProperty("error");
        Assert.Equal(("motion.busy", "Making another motion."), (error.GetProperty("code").GetString(), error.GetProperty("summary").GetString()));

        await using var odd = Worker(_ => Json(new { error = new { code = "weird", summary = "x\u0007y" } }, HttpStatusCode.InternalServerError));
        error = Assert.Single(await ObjectsAsync(odd, new(GatewayMotionOperation.Generate, "A person runs.", 2))).GetProperty("error");
        Assert.Equal(("motion.failed", "xy"), (error.GetProperty("code").GetString(), error.GetProperty("summary").GetString()));

        await using var notClip = Worker(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3, 4]) };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("model/gltf-binary");
            return response;
        });
        Assert.Equal("worker.failed", Assert.Single(await EventsAsync(notClip, new(GatewayMotionOperation.Generate, "A person runs.", 2),
            GatewayInferenceEventKind.Failed)).ErrorCode);

        await using var down = new MotionRelayWorker(new Uri("http://127.0.0.1:1/"));
        Assert.Equal("worker.unavailable", Assert.Single(await EventsAsync(down, new(GatewayMotionOperation.Generate, "A person runs.", 2),
            GatewayInferenceEventKind.Failed)).ErrorCode);
    }

    [Fact]
    public void Client_json_round_trips_generate_with_and_without_a_seed()
    {
        var route = Route();
        foreach (var seed in new long?[] { 7, null })
        {
            var bytes = GatewayClientJson.Request(Inference(route, new GatewayMotionPayload(GatewayMotionOperation.Generate, "A person sits down.", 3, seed)));
            using var document = JsonDocument.Parse(bytes);
            Assert.Equal(seed is not null, document.RootElement.GetProperty("payload").TryGetProperty("seed", out _));
            var parsed = Assert.IsType<GatewayMotionPayload>(GatewayInferenceJson.ParseRequest(bytes, route, DateTimeOffset.UtcNow).Payload);
            Assert.Equal(("A person sits down.", 3.0, seed), (parsed.Prompt, parsed.Seconds, parsed.Seed));
        }
    }

    private static MotionRelayWorker Worker(Func<HttpRequestMessage, HttpResponseMessage> respond) =>
        new(new Uri("http://127.0.0.1:50090/"), handler: new ScriptedHandler(respond));

    private static GatewayMotionPayload Parse(GatewayInferenceRoute route, object payload) =>
        Assert.IsType<GatewayMotionPayload>(GatewayInferenceJson.ParseRequest(Request(route, payload), route, DateTimeOffset.UtcNow).Payload);

    private static GatewayInferenceRoute Route() => GatewayInferenceRoute.Motion(
        MotionRelayWorker.DefaultDestinationId,
        MotionRelayWorker.DefaultWorkerId,
        MotionRelayWorker.DefaultModel,
        MotionRelayWorker.PinnedModels[MotionRelayWorker.DefaultModel].Revision,
        MotionRelayWorker.PinnedModels[MotionRelayWorker.DefaultModel].Sha256);

    private static byte[] Request(GatewayInferenceRoute route, object payload) =>
        JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object?>
        {
            ["protocol_version"] = GatewayProtocolVersion.Current,
            ["route_id"] = route.RouteId,
            ["contract_id"] = route.ContractId,
            ["contract_version"] = route.ContractVersion,
            ["destination_id"] = route.DestinationId,
            ["worker_id"] = route.WorkerId,
            ["adapter_version"] = route.AdapterVersion,
            ["model_id"] = route.ModelId,
            ["model_revision"] = route.ModelRevision,
            ["model_sha256"] = route.ModelSha256,
            ["artifact_identity_sha256"] = route.ArtifactIdentitySha256,
            ["session_id"] = Guid.NewGuid(),
            ["turn_id"] = Guid.NewGuid(),
            ["request_id"] = Guid.NewGuid(),
            ["epoch"] = 0,
            ["deadline_utc"] = DateTimeOffset.UtcNow.AddSeconds(10).ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            ["payload"] = payload
        }, GatewayTestHost.Json);

    private static GatewayInferenceRequest Inference(GatewayInferenceRoute route, GatewayInferencePayload payload) =>
        new(route, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, 0, DateTimeOffset.UtcNow.AddSeconds(10), payload);

    private static GatewayPrincipal Principal(GatewayRole role) => new()
    {
        HostId = "host",
        CredentialId = "credential",
        DeviceId = "device",
        Role = role,
        CredentialLifetime = new PairedDeviceLifetime()
    };

    private static async Task<IReadOnlyList<JsonElement>> ObjectsAsync(MotionRelayWorker worker, GatewayMotionPayload payload)
    {
        var objects = new List<JsonElement>();
        foreach (var item in await EventsAsync(worker, payload, GatewayInferenceEventKind.TextDelta))
        {
            using var document = JsonDocument.Parse(item.Payload);
            objects.Add(document.RootElement.Clone());
        }
        return objects;
    }

    private static async Task<IReadOnlyList<GatewayInferenceEvent>> EventsAsync(
        MotionRelayWorker worker, GatewayMotionPayload payload, GatewayInferenceEventKind kind)
    {
        var request = Inference(worker.Route, payload);
        var sequence = 0L;
        var events = new List<GatewayInferenceEvent>();
        var state = new GatewayInferenceEventValidator.GatewayInferenceStreamState(request, "host", TimeProvider.System);
        await foreach (var item in worker.ExecuteAsync(request, CancellationToken.None))
        {
            GatewayInferenceEventValidator.Validate(item, request, sequence++);
            state.Accept(item);
            if (item.Kind == kind) events.Add(item);
        }
        return events;
    }

    private static HttpResponseMessage Json(object value, HttpStatusCode status = HttpStatusCode.OK)
    {
        var response = new HttpResponseMessage(status)
        {
            Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(value, GatewayTestHost.Json))
        };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return response;
    }

    private sealed class ScriptedHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
