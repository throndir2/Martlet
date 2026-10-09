using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Martlet.Gateway.Ocr;

namespace Martlet.Gateway.Tests;

public sealed class OcrRelayTests
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3];
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3];

    [Fact]
    public void Ocr_route_contract_parses_payloads_and_rejects_invalid_images()
    {
        var route = Route();
        Assert.Equal(("martlet.gateway.ocr.v1", "/martlet/v1/inference/ocr", "martlet.ocr-relay", "1.0"),
            (route.RouteId, route.Path, route.ContractId, route.ContractVersion));
        Assert.Equal(GatewayInferenceKind.Ocr, route.Kind);
        Assert.Equal(GatewayRole.Voice, route.RequiredRole);
        Assert.Equal(GatewayInferenceKind.Ocr, GatewayInferenceRoute.FromCapability(GatewayInferenceRouteCapability.From(route)).Kind);

        var status = Assert.IsType<GatewayOcrPayload>(GatewayInferenceJson.ParseRequest(
            Request(route, new { operation = "status" }), route, DateTimeOffset.UtcNow).Payload);
        Assert.Equal(GatewayOcrOperation.Status, status.Operation);

        var read = Assert.IsType<GatewayOcrPayload>(GatewayInferenceJson.ParseRequest(
            Request(route, new { operation = "read", media_type = "image/png", image_base64 = Convert.ToBase64String(Png) }),
            route, DateTimeOffset.UtcNow).Payload);
        Assert.Equal(GatewayOcrOperation.Read, read.Operation);
        Assert.Equal("image/png", read.MediaType);
        Assert.Equal(Png, read.Image.ToArray());

        Assert.Equal("request.invalid", Assert.Throws<GatewayProtocolException>(() => GatewayInferenceJson.ParseRequest(
            Request(route, new { operation = "read", media_type = "image/gif", image_base64 = Convert.ToBase64String(Png) }),
            route, DateTimeOffset.UtcNow)).Failure.Code);
        Assert.Equal("request.invalid", Assert.Throws<GatewayProtocolException>(() => GatewayInferenceJson.ParseRequest(
            Request(route, new { operation = "read", media_type = "image/jpeg", image_base64 = Convert.ToBase64String(Png) }),
            route, DateTimeOffset.UtcNow)).Failure.Code);
        var tooLarge = new byte[GatewayInferenceRoute.OcrMaximumImageBytes + 1];
        tooLarge[0] = 0x89; tooLarge[1] = 0x50; tooLarge[2] = 0x4E; tooLarge[3] = 0x47;
        tooLarge[4] = 0x0D; tooLarge[5] = 0x0A; tooLarge[6] = 0x1A; tooLarge[7] = 0x0A;
        Assert.Equal("request.too_large", Assert.Throws<GatewayProtocolException>(() => GatewayInferenceJson.ParseRequest(
            Request(route, new { operation = "read", media_type = "image/png", image_base64 = Convert.ToBase64String(tooLarge) }),
            route, DateTimeOffset.UtcNow)).Failure.Code);
    }

    [Fact]
    public async Task Permission_denies_wrong_role_or_payload()
    {
        await using var worker = new OcrRelayWorker(new Uri("http://127.0.0.1:50087/"), handler: new ScriptedHandler(_ => Json(new { state = "ready" })));
        var request = Inference(worker.Route, new GatewayOcrPayload(GatewayOcrOperation.Status));
        var wrongRole = await Assert.ThrowsAsync<GatewayInferenceWorkerException>(async () =>
            await worker.AcquirePermissionAsync(request, Principal(GatewayRole.Memory), CancellationToken.None));
        Assert.Equal(GatewayInferenceWorkerFailure.PermissionDenied, wrongRole.Failure);

        var wrongPayload = Inference(worker.Route, new GatewayPicturePayload(GatewayPictureOperation.Status));
        var wrongType = await Assert.ThrowsAsync<GatewayInferenceWorkerException>(async () =>
            await worker.AcquirePermissionAsync(wrongPayload, Principal(GatewayRole.Voice), CancellationToken.None));
        Assert.Equal(GatewayInferenceWorkerFailure.PermissionDenied, wrongType.Failure);
    }

    [Fact]
    public async Task Status_loading_ready_and_read_are_relayed_as_one_json_event()
    {
        await using var down = new OcrRelayWorker(new Uri("http://127.0.0.1:1/"));
        var loading = Assert.Single(await ObjectsAsync(down, new GatewayOcrPayload(GatewayOcrOperation.Status)));
        Assert.Equal("loading", loading.GetProperty("state").GetString());

        var handler = new ScriptedHandler(request =>
        {
            if (request.RequestUri!.PathAndQuery == "/status")
                return Json(new { state = "ready", engine = "rapidocr", engine_version = "1.4.4", threads = 4 });
            if (request.RequestUri!.PathAndQuery == "/read")
            {
                Assert.Equal(HttpMethod.Post, request.Method);
                Assert.Equal("image/jpeg", request.Content!.Headers.ContentType!.MediaType);
                Assert.Equal(Jpeg, request.Content!.ReadAsByteArrayAsync().Result);
                return Json(new
                {
                    lines = new[] { new { text = "Score", score = 0.99, box = new[] { 1, 2, 30, 10 } } },
                    width = 100,
                    height = 50,
                    milliseconds = 12
                });
            }
            throw new InvalidOperationException(request.RequestUri.PathAndQuery);
        });
        await using var worker = new OcrRelayWorker(new Uri("http://127.0.0.1:50087/"), handler: handler);

        var status = Assert.Single(await ObjectsAsync(worker, new GatewayOcrPayload(GatewayOcrOperation.Status)));
        Assert.Equal("ready", status.GetProperty("state").GetString());
        Assert.Equal("rapidocr", status.GetProperty("engine").GetString());
        Assert.Equal(4, status.GetProperty("threads").GetInt32());

        var read = Assert.Single(await ObjectsAsync(worker, new GatewayOcrPayload(GatewayOcrOperation.Read, Jpeg, "image/jpeg")));
        Assert.Equal("Score", read.GetProperty("lines")[0].GetProperty("text").GetString());
        Assert.Equal(100, read.GetProperty("width").GetInt32());
        Assert.Equal(12, read.GetProperty("milliseconds").GetInt32());
    }

    [Fact]
    public async Task Each_ppocrv5_model_gets_its_own_pinned_route_and_an_unknown_model_is_refused()
    {
        await using var mobile = new OcrRelayWorker(new Uri("http://127.0.0.1:50087/"), "ppocrv5-mobile");
        Assert.Equal("ppocrv5-mobile", mobile.Route.ModelId);
        Assert.Equal("rapidocr-3.10.0", mobile.Route.ModelRevision);
        await using var server = new OcrRelayWorker(new Uri("http://127.0.0.1:50087/"), "ppocrv5-server");
        Assert.Equal("ppocrv5-server", server.Route.ModelId);
        Assert.NotEqual(mobile.Route.ModelSha256, server.Route.ModelSha256);
        Assert.Throws<ArgumentException>(() => new OcrRelayWorker(new Uri("http://127.0.0.1:50087/"), "ppocrv6-mobile"));
    }

    [Fact]
    public async Task Worker_errors_are_mapped_to_gateway_failures()
    {
        await using var invalidWorker = new OcrRelayWorker(new Uri("http://127.0.0.1:50087/"),
            handler: new ScriptedHandler(_ => Json(new { error = new { code = "request.invalid" } }, HttpStatusCode.BadRequest)));
        Assert.Equal("request.invalid", Assert.Single(await EventsAsync(invalidWorker,
            new GatewayOcrPayload(GatewayOcrOperation.Read, Png, "image/png"), GatewayInferenceEventKind.Failed)).ErrorCode);

        var huge = new string('x', GatewayInferenceRoute.OcrMaximumResultBytes + 1);
        await using var hugeWorker = new OcrRelayWorker(new Uri("http://127.0.0.1:50087/"),
            handler: new ScriptedHandler(_ => Json(new { huge })));
        Assert.Equal("worker.failed", Assert.Single(await EventsAsync(hugeWorker,
            new GatewayOcrPayload(GatewayOcrOperation.Read, Png, "image/png"), GatewayInferenceEventKind.Failed)).ErrorCode);
    }

    [Fact]
    public void Client_json_round_trips_read_payload()
    {
        var route = Route();
        var request = Inference(route, new GatewayOcrPayload(GatewayOcrOperation.Read, Png, "image/png"));
        var bytes = GatewayClientJson.Request(request);
        using var document = JsonDocument.Parse(bytes);
        var payload = document.RootElement.GetProperty("payload");
        Assert.Equal("read", payload.GetProperty("operation").GetString());
        Assert.Equal("image/png", payload.GetProperty("media_type").GetString());
        Assert.Equal(Convert.ToBase64String(Png), payload.GetProperty("image_base64").GetString());
        var parsed = Assert.IsType<GatewayOcrPayload>(GatewayInferenceJson.ParseRequest(bytes, route, DateTimeOffset.UtcNow).Payload);
        Assert.Equal(Png, parsed.Image.ToArray());
    }

    private static GatewayInferenceRoute Route() => GatewayInferenceRoute.Ocr(
        OcrRelayWorker.DefaultDestinationId,
        OcrRelayWorker.DefaultWorkerId,
        OcrRelayWorker.DefaultModel,
        OcrRelayWorker.PinnedModels[OcrRelayWorker.DefaultModel].Revision,
        OcrRelayWorker.PinnedModels[OcrRelayWorker.DefaultModel].Sha256);

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

    private static async Task<IReadOnlyList<JsonElement>> ObjectsAsync(OcrRelayWorker worker, GatewayOcrPayload payload)
    {
        var events = await EventsAsync(worker, payload, GatewayInferenceEventKind.TextDelta);
        var objects = new List<JsonElement>();
        foreach (var item in events)
        {
            using var document = JsonDocument.Parse(item.Payload);
            objects.Add(document.RootElement.Clone());
        }
        return objects;
    }

    private static async Task<IReadOnlyList<GatewayInferenceEvent>> EventsAsync(
        OcrRelayWorker worker, GatewayOcrPayload payload, GatewayInferenceEventKind kind)
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
