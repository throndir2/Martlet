using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Martlet.Gateway.VoiceRecognition;

namespace Martlet.Gateway.Tests;

public sealed class VoiceRecognitionRelayTests
{
    private static readonly byte[] Pcm = [1, 0, 2, 0, 0xFF, 0x7F, 0x00, 0x80];

    [Fact]
    public void Voice_recognition_route_contract_parses_payloads_and_rejects_other_audio()
    {
        var route = Route();
        Assert.Equal(("martlet.gateway.voice-recognition.v1", "/martlet/v1/inference/voice-recognition", "martlet.voice-recognition-relay", "1.0"),
            (route.RouteId, route.Path, route.ContractId, route.ContractVersion));
        Assert.Equal((GatewayInferenceKind.VoiceRecognition, GatewayRole.Voice, GatewayLane.Live), (route.Kind, route.RequiredRole, route.Lane));
        Assert.Equal(("wespeaker-en-voxceleb-resnet34-lm", "e9848563da86f263117134dfd7ad63c92355b37de492b55e325400c9d9c39012"),
            (route.ModelId, route.ModelSha256));
        Assert.Equal(GatewayInferenceKind.VoiceRecognition, GatewayInferenceRoute.FromCapability(GatewayInferenceRouteCapability.From(route)).Kind);
        Assert.Equal("Voice recognition", GatewayGpus.RouteName(route));
        Assert.True(GatewayInferenceRouteRegistry.FriendsMayUse(route));

        var status = Assert.IsType<GatewayVoiceRecognitionPayload>(GatewayInferenceJson.ParseRequest(
            Request(route, new { operation = "status" }), route, DateTimeOffset.UtcNow).Payload);
        Assert.Equal(GatewayVoiceRecognitionOperation.Status, status.Operation);
        Assert.True(status.Pcm.IsEmpty);

        foreach (var (operation, expected) in new[] { ("analyze", GatewayVoiceRecognitionOperation.Analyze), ("voiceprint", GatewayVoiceRecognitionOperation.Voiceprint) })
        {
            var parsed = Assert.IsType<GatewayVoiceRecognitionPayload>(GatewayInferenceJson.ParseRequest(
                Request(route, new { operation, sample_rate = 16000, pcm16_base64 = Convert.ToBase64String(Pcm) }), route, DateTimeOffset.UtcNow).Payload);
            Assert.Equal(expected, parsed.Operation);
            Assert.Equal(Pcm, parsed.Pcm.ToArray());
            Assert.Equal(16000, parsed.SampleRate);
        }

        // A minute of audio is the most; other rates, odd or empty audio, other fields and other operations are refused.
        var minute = new byte[GatewayInferenceRoute.VoiceRecognitionMaximumPcmBytes];
        Assert.Equal(minute.Length, Assert.IsType<GatewayVoiceRecognitionPayload>(GatewayInferenceJson.ParseRequest(
            Request(route, new { operation = "analyze", sample_rate = 16000, pcm16_base64 = Convert.ToBase64String(minute) }), route,
            DateTimeOffset.UtcNow).Payload).Pcm.Length);
        Assert.Equal("request.too_large", Assert.Throws<GatewayProtocolException>(() => GatewayInferenceJson.ParseRequest(
            Request(route, new { operation = "analyze", sample_rate = 16000, pcm16_base64 = Convert.ToBase64String(new byte[minute.Length + 2]) }),
            route, DateTimeOffset.UtcNow)).Failure.Code);
        foreach (var bad in new object[]
        {
            new { operation = "analyze", sample_rate = 48000, pcm16_base64 = Convert.ToBase64String(Pcm) },
            new { operation = "analyze", sample_rate = 16000, pcm16_base64 = Convert.ToBase64String(Pcm[..3]) },
            new { operation = "analyze", sample_rate = 16000, pcm16_base64 = "" },
            new { operation = "analyze", sample_rate = 16000, pcm16_base64 = "not base64!" },
            new { operation = "analyze", pcm16_base64 = Convert.ToBase64String(Pcm) },
            new { operation = "voiceprint", sample_rate = 16000, pcm16_base64 = Convert.ToBase64String(Pcm), model = "other" },
            new { operation = "status", sample_rate = 16000 },
            new { operation = "enroll", sample_rate = 16000, pcm16_base64 = Convert.ToBase64String(Pcm) }
        })
            Assert.Equal("request.invalid", Assert.Throws<GatewayProtocolException>(() =>
                GatewayInferenceJson.ParseRequest(Request(route, bad), route, DateTimeOffset.UtcNow)).Failure.Code);
    }

    [Fact]
    public async Task Permission_denies_wrong_role_or_payload()
    {
        await using var worker = new VoiceRecognitionRelayWorker(new Uri("http://127.0.0.1:50091/"), handler: new ScriptedHandler(_ => Json(new { state = "ready" })));
        var request = Inference(worker.Route, new GatewayVoiceRecognitionPayload(GatewayVoiceRecognitionOperation.Status));
        var wrongRole = await Assert.ThrowsAsync<GatewayInferenceWorkerException>(async () =>
            await worker.AcquirePermissionAsync(request, Principal(GatewayRole.Memory), CancellationToken.None));
        Assert.Equal(GatewayInferenceWorkerFailure.PermissionDenied, wrongRole.Failure);
        var wrongPayload = Inference(worker.Route, new GatewayOcrPayload(GatewayOcrOperation.Status));
        var wrongType = await Assert.ThrowsAsync<GatewayInferenceWorkerException>(async () =>
            await worker.AcquirePermissionAsync(wrongPayload, Principal(GatewayRole.Voice), CancellationToken.None));
        Assert.Equal(GatewayInferenceWorkerFailure.PermissionDenied, wrongType.Failure);
        await using var lease = await worker.AcquirePermissionAsync(request, Principal(GatewayRole.Voice), CancellationToken.None);
    }

    [Fact]
    public async Task Status_analyze_and_voiceprint_are_relayed_as_one_json_event_each()
    {
        await using var down = new VoiceRecognitionRelayWorker(new Uri("http://127.0.0.1:1/"));
        var loading = Assert.Single(await ObjectsAsync(down, new GatewayVoiceRecognitionPayload(GatewayVoiceRecognitionOperation.Status)));
        Assert.Equal(("loading", "wespeaker-en-voxceleb-resnet34-lm"), (loading.GetProperty("state").GetString(), loading.GetProperty("model").GetString()));

        var voiceprint = Enumerable.Range(0, 256).Select(i => i == 0 ? 1.0 : 0.0).ToArray();
        var handler = new ScriptedHandler(request =>
        {
            var path = request.RequestUri!.PathAndQuery;
            if (path == "/status")
                return Json(new { state = "ready", provider = "cuda", model = "wespeaker-en-voxceleb-resnet34-lm", threads = 4 });
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("application/octet-stream", request.Content!.Headers.ContentType!.MediaType);
            Assert.Equal(Pcm, request.Content!.ReadAsByteArrayAsync().Result);
            return path switch
            {
                "/analyze" => Json(new
                {
                    speakers = new[] { new { local = 0, voiceprint, clean_seconds = 2.4, start = 0.5, end = 3.1 } },
                    voices = 1, overlap = false, whole = (double[]?)null, speech_seconds = 3.0, milliseconds = 42
                }),
                "/voiceprint" => Json(new { voiceprint, milliseconds = 12 }),
                _ => throw new InvalidOperationException(path)
            };
        });
        await using var worker = new VoiceRecognitionRelayWorker(new Uri("http://127.0.0.1:50091/"), handler: handler);

        var status = Assert.Single(await ObjectsAsync(worker, new GatewayVoiceRecognitionPayload(GatewayVoiceRecognitionOperation.Status)));
        Assert.Equal(("ready", "cuda"), (status.GetProperty("state").GetString(), status.GetProperty("provider").GetString()));

        var analysis = Assert.Single(await ObjectsAsync(worker, new GatewayVoiceRecognitionPayload(GatewayVoiceRecognitionOperation.Analyze, [.. Pcm])));
        var speaker = Assert.Single(analysis.GetProperty("speakers").EnumerateArray());
        Assert.Equal(256, speaker.GetProperty("voiceprint").GetArrayLength());
        Assert.Equal(2.4, speaker.GetProperty("clean_seconds").GetDouble());
        Assert.Equal(JsonValueKind.Null, analysis.GetProperty("whole").ValueKind);
        Assert.Equal(42, analysis.GetProperty("milliseconds").GetInt32());

        var one = Assert.Single(await ObjectsAsync(worker, new GatewayVoiceRecognitionPayload(GatewayVoiceRecognitionOperation.Voiceprint, [.. Pcm])));
        Assert.Equal(1.0f, one.GetProperty("voiceprint")[0].GetSingle());
    }

    [Fact]
    public async Task Worker_errors_are_mapped_to_gateway_failures()
    {
        async Task<string?> FailureAsync(HttpResponseMessage answer, GatewayVoiceRecognitionOperation operation = GatewayVoiceRecognitionOperation.Analyze)
        {
            await using var worker = new VoiceRecognitionRelayWorker(new Uri("http://127.0.0.1:50091/"), handler: new ScriptedHandler(_ => answer));
            return Assert.Single(await EventsAsync(worker, new GatewayVoiceRecognitionPayload(operation, [.. Pcm]), GatewayInferenceEventKind.Failed)).ErrorCode;
        }

        Assert.Equal("request.invalid", await FailureAsync(Json(new { error = "request.invalid" }, HttpStatusCode.BadRequest)));
        Assert.Equal("worker.unavailable", await FailureAsync(Json(new { error = "worker.loading" }, HttpStatusCode.ServiceUnavailable)));
        Assert.Equal("worker.failed", await FailureAsync(Json(new { error = "worker.failed" }, HttpStatusCode.InternalServerError)));
        Assert.Equal("worker.failed", await FailureAsync(Json(new { huge = new string('x', GatewayInferenceRoute.VoiceRecognitionMaximumResultBytes + 1) })));
        // An answer without what the operation asks for is not passed on.
        Assert.Equal("worker.failed", await FailureAsync(Json(new { voiceprint = (double[]?)null })));
        Assert.Equal("worker.failed", await FailureAsync(Json(new { speakers = Array.Empty<int>() }), GatewayVoiceRecognitionOperation.Voiceprint));
        await using var down = new VoiceRecognitionRelayWorker(new Uri("http://127.0.0.1:1/"));
        Assert.Equal("worker.unavailable", Assert.Single(await EventsAsync(down,
            new GatewayVoiceRecognitionPayload(GatewayVoiceRecognitionOperation.Voiceprint, [.. Pcm]), GatewayInferenceEventKind.Failed)).ErrorCode);
    }

    [Fact]
    public void Only_the_pinned_voice_model_on_a_loopback_service()
    {
        Assert.Throws<ArgumentException>(() => new VoiceRecognitionRelayWorker(new Uri("http://127.0.0.1:50091/"), "wespeaker-resnet293"));
        foreach (var endpoint in new[] { "http://192.168.1.5:50091/", "https://127.0.0.1:50091/", "http://127.0.0.1:50091/x" })
            Assert.Throws<ArgumentException>(() => new VoiceRecognitionRelayWorker(new Uri(endpoint)));
    }

    [Fact]
    public void Client_json_round_trips_the_audio()
    {
        var route = Route();
        var request = Inference(route, new GatewayVoiceRecognitionPayload(GatewayVoiceRecognitionOperation.Analyze, [.. Pcm]));
        var bytes = GatewayClientJson.Request(request);
        using var document = JsonDocument.Parse(bytes);
        var payload = document.RootElement.GetProperty("payload");
        Assert.Equal("analyze", payload.GetProperty("operation").GetString());
        Assert.Equal(16000, payload.GetProperty("sample_rate").GetInt32());
        Assert.Equal(Convert.ToBase64String(Pcm), payload.GetProperty("pcm16_base64").GetString());
        var parsed = Assert.IsType<GatewayVoiceRecognitionPayload>(GatewayInferenceJson.ParseRequest(bytes, route, DateTimeOffset.UtcNow).Payload);
        Assert.Equal(Pcm, parsed.Pcm.ToArray());
        using var status = JsonDocument.Parse(GatewayClientJson.Request(Inference(route, new GatewayVoiceRecognitionPayload(GatewayVoiceRecognitionOperation.Status))));
        Assert.Equal("""{"operation":"status"}""", status.RootElement.GetProperty("payload").GetRawText());
    }

    private static GatewayInferenceRoute Route() => GatewayInferenceRoute.VoiceRecognition(
        VoiceRecognitionRelayWorker.DefaultDestinationId,
        VoiceRecognitionRelayWorker.DefaultWorkerId,
        VoiceRecognitionRelayWorker.DefaultModel,
        VoiceRecognitionRelayWorker.PinnedModels[VoiceRecognitionRelayWorker.DefaultModel].Revision,
        VoiceRecognitionRelayWorker.PinnedModels[VoiceRecognitionRelayWorker.DefaultModel].Sha256);

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

    private static async Task<IReadOnlyList<JsonElement>> ObjectsAsync(VoiceRecognitionRelayWorker worker, GatewayVoiceRecognitionPayload payload)
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
        VoiceRecognitionRelayWorker worker, GatewayVoiceRecognitionPayload payload, GatewayInferenceEventKind kind)
    {
        var request = Inference(worker.Route, payload);
        var sequence = 0L;
        var events = new List<GatewayInferenceEvent>();
        await foreach (var item in worker.ExecuteAsync(request, CancellationToken.None))
        {
            GatewayInferenceEventValidator.Validate(item, request, sequence++);
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
