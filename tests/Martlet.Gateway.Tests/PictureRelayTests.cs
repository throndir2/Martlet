using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Martlet.Avatar.Audio2Face.Remote;
using Martlet.Gateway.Pictures;

namespace Martlet.Gateway.Tests;

public sealed class PictureRelayTests
{
    [Fact]
    public void Picture_route_contract_parses_operations_and_rejects_paths()
    {
        var route = GatewayInferenceRoute.Picture("pictures-host", "picture-relay", PictureRelayWorker.DefaultModel,
            PictureRelayWorker.PinnedModels[PictureRelayWorker.DefaultModel].Revision,
            PictureRelayWorker.PinnedModels[PictureRelayWorker.DefaultModel].Sha256);
        Assert.Equal(("martlet.gateway.picture.v1", "/martlet/v1/inference/picture", "martlet.picture-relay", "1.0"),
            (route.RouteId, route.Path, route.ContractId, route.ContractVersion));
        var capability = GatewayInferenceRouteCapability.From(route);
        Assert.Equal(GatewayInferenceKind.Picture, GatewayInferenceRoute.FromCapability(capability).Kind);

        var prompt = Request(route, new { operation = "prompt", prompt = new { one = new { class_type = "EmptyLatentImage" } } });
        var parsed = GatewayInferenceJson.ParseRequest(prompt, route, DateTimeOffset.UtcNow);
        var payload = Assert.IsType<GatewayPicturePayload>(parsed.Payload);
        Assert.Equal(GatewayPictureOperation.Prompt, payload.Operation);
        Assert.Equal(JsonValueKind.Object, payload.Prompt!.Value.ValueKind);
        var free = Request(route, new { operation = "free" });
        Assert.Equal(GatewayPictureOperation.Free,
            Assert.IsType<GatewayPicturePayload>(GatewayInferenceJson.ParseRequest(free, route, DateTimeOffset.UtcNow).Payload).Operation);

        var badView = Request(route, new { operation = "view", filename = "../out.png", subfolder = "", type = "output", offset = 0, maximum = 1 });
        Assert.Equal("request.invalid", Assert.Throws<GatewayProtocolException>(() =>
            GatewayInferenceJson.ParseRequest(badView, route, DateTimeOffset.UtcNow)).Failure.Code);

        var tooLarge = Request(route, new { operation = "prompt", prompt = new { text = new string('x', route.MaximumInputBytes) } });
        Assert.Equal("request.too_large", Assert.Throws<GatewayProtocolException>(() =>
            GatewayInferenceJson.ParseRequest(tooLarge, route, DateTimeOffset.UtcNow)).Failure.Code);
    }

    [Fact]
    public async Task Status_prompt_history_and_queue_are_relayed_as_json_events()
    {
        var handler = new ScriptedHandler(request =>
        {
            var path = request.RequestUri!.PathAndQuery;
            if (path == "/system_stats") return Json(new { system = new { comfyui_version = "0.39.0" },
                devices = new[] { new { name = "RTX fixture", vram_total = 10_000L, vram_free = 4_000L } } });
            if (path == "/models/diffusion_models") return Json(new[] { "z_image_turbo_bf16.safetensors" });
            if (path == "/models/text_encoders") return Json(new[] { "qwen_3_4b.safetensors" });
            if (path == "/models/vae") return Json(new[] { "ae.safetensors" });
            if (path.StartsWith("/models/", StringComparison.Ordinal)) return Json(Array.Empty<string>());
            if (path == "/queue") return Json(new { queue_running = new object[] { new object[] { 1, "running-id" } },
                queue_pending = new object[] { new object[] { 2, "pending-id" }, new object[] { 3, "later-id" } } });
            if (path == "/prompt")
            {
                var body = request.Content!.ReadAsStringAsync().Result;
                Assert.Contains("\"client_id\":\"martlet-", body, StringComparison.Ordinal);
                Assert.Contains("\"prompt\":{\"3\":{\"class_type\":\"KSampler\"}}", body, StringComparison.Ordinal);
                return Json(new { prompt_id = "prompt-1", number = 7 });
            }
            if (path == "/history/prompt-1") return Json(new Dictionary<string, object> { ["prompt-1"] = new { outputs = new { } } });
            throw new InvalidOperationException(path);
        });
        await using var worker = new PictureRelayWorker(new Uri("http://127.0.0.1:50086/"), handler: handler);

        var status = Assert.Single(await ObjectsAsync(worker, new GatewayPicturePayload(GatewayPictureOperation.Status)));
        Assert.Equal("ready", status.GetProperty("state").GetString());
        Assert.Equal("0.39.0", status.GetProperty("comfyui_version").GetString());
        Assert.Equal(2, status.GetProperty("queue_pending").GetInt32());
        Assert.Equal("RTX fixture", status.GetProperty("devices")[0].GetProperty("name").GetString());
        Assert.Contains("z_image_turbo_bf16.safetensors",
            status.GetProperty("models").GetProperty("diffusion_models").EnumerateArray().Select(x => x.GetString()));

        using var promptDocument = JsonDocument.Parse("""{"3":{"class_type":"KSampler"}}""");
        var prompted = Assert.Single(await ObjectsAsync(worker, new GatewayPicturePayload(GatewayPictureOperation.Prompt,
            prompt: promptDocument.RootElement.Clone())));
        Assert.Equal("prompt-1", prompted.GetProperty("prompt_id").GetString());
        Assert.Equal(7, prompted.GetProperty("number").GetInt32());

        var history = Assert.Single(await ObjectsAsync(worker, new GatewayPicturePayload(GatewayPictureOperation.History, promptId: "prompt-1")));
        Assert.True(history.TryGetProperty("prompt-1", out _));

        var queue = Assert.Single(await ObjectsAsync(worker, new GatewayPicturePayload(GatewayPictureOperation.Queue)));
        Assert.Equal(1, queue.GetProperty("queue_running").GetArrayLength());
    }

    [Fact]
    public async Task Prompt_validation_errors_view_pages_and_cancel_are_relayed()
    {
        var image = Enumerable.Range(0, 400_000).Select(i => (byte)(i % 251)).ToArray();
        var interruptCalled = false;
        var freeCalled = false;
        var handler = new ScriptedHandler(request =>
        {
            var path = request.RequestUri!.PathAndQuery;
            if (path == "/prompt") return Json(new { error = new { message = "bad workflow" }, node_errors = new { node = new { } } },
                HttpStatusCode.BadRequest);
            if (path == "/view?filename=out.png&subfolder=&type=output") return Bytes(image, "image/png");
            if (path == "/queue" && request.Method == HttpMethod.Get)
                return Json(new { queue_running = new object[] { new object[] { 1, "run-id" } }, queue_pending = Array.Empty<object>() });
            if (path == "/queue" && request.Method == HttpMethod.Post)
            {
                Assert.Contains("\"delete\":[\"run-id\"]", request.Content!.ReadAsStringAsync().Result, StringComparison.Ordinal);
                return Json(new { });
            }
            if (path == "/interrupt")
            {
                interruptCalled = true;
                return Json(new { });
            }
            if (path == "/free")
            {
                freeCalled = true;
                Assert.Equal("""{"unload_models":true,"free_memory":true}""", request.Content!.ReadAsStringAsync().Result);
                return Json(new { });
            }
            throw new InvalidOperationException(path);
        });
        await using var worker = new PictureRelayWorker(new Uri("http://127.0.0.1:50086/"), handler: handler);
        using var promptDocument = JsonDocument.Parse("""{"bad":{"class_type":"Missing"}}""");

        var invalid = Assert.Single(await ObjectsAsync(worker, new GatewayPicturePayload(GatewayPictureOperation.Prompt,
            prompt: promptDocument.RootElement.Clone())));
        Assert.Equal("prompt.invalid", invalid.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("bad workflow", invalid.GetProperty("error").GetProperty("summary").GetString());
        Assert.True(invalid.TryGetProperty("node_errors", out _));

        var parts = await ObjectsAsync(worker, new GatewayPicturePayload(GatewayPictureOperation.View,
            filename: "out.png", subfolder: "", imageType: "output", offset: 10, maximum: 250_000));
        Assert.True(parts.Count > 1);
        var restored = new List<byte>();
        var expectedOffset = 10L;
        foreach (var part in parts)
        {
            Assert.Equal("image/png", part.GetProperty("media_type").GetString());
            Assert.Equal(image.Length, part.GetProperty("total_bytes").GetInt64());
            Assert.Equal(expectedOffset, part.GetProperty("offset").GetInt64());
            var bytes = Convert.FromBase64String(part.GetProperty("data_base64").GetString()!);
            Assert.Equal(bytes.Length, part.GetProperty("bytes").GetInt32());
            restored.AddRange(bytes);
            expectedOffset += bytes.Length;
        }
        Assert.Equal(image.AsSpan(10, 250_000).ToArray(), restored.ToArray());

        var canceled = Assert.Single(await ObjectsAsync(worker, new GatewayPicturePayload(GatewayPictureOperation.Cancel, promptId: "run-id")));
        Assert.Equal("canceled", canceled.GetProperty("state").GetString());
        Assert.True(interruptCalled);

        var freed = Assert.Single(await ObjectsAsync(worker, new GatewayPicturePayload(GatewayPictureOperation.Free)));
        Assert.Equal("freed", freed.GetProperty("state").GetString());
        Assert.True(freeCalled);
    }

    [Fact]
    public async Task Paired_desktop_reads_picture_status_through_the_gateway()
    {
        var handler = new ScriptedHandler(request => request.RequestUri!.PathAndQuery switch
        {
            "/system_stats" => Json(new { system = new { comfyui_version = "0.39.0" }, devices = Array.Empty<object>() }),
            "/queue" => Json(new { queue_running = Array.Empty<object>(), queue_pending = Array.Empty<object>() }),
            "/models/diffusion_models" => Json(new[] { "z_image_turbo_bf16.safetensors" }),
            "/models/text_encoders" => Json(new[] { "qwen_3_4b.safetensors" }),
            "/models/vae" => Json(new[] { "ae.safetensors" }),
            var path when path.StartsWith("/models/", StringComparison.Ordinal) => Json(Array.Empty<string>()),
            var path => throw new InvalidOperationException(path)
        });
        await using var worker = new PictureRelayWorker(new Uri("http://127.0.0.1:50086/"), handler: handler);
        await using var host = await GatewayTestHost.StartAsync(inferenceWorkers: [worker]);
        var card = host.OpenPairing(GatewayRole.Voice, "desktop-test");
        var (pairing, secret) = await Audio2FaceHostClient.PairAsync(host.Origin.CanonicalOrigin, card.HostId,
            card.SpkiFingerprint, "desktop-test", card.PairingId, card.Token.Reveal());
        using var connection = new Audio2FaceHostConnection(pairing, secret, host.Clock);
        var route = Assert.Single(await connection.ReadRoutesAsync(), r => r.RouteId == Audio2FaceHostConnection.PictureRouteId);

        var status = Assert.Single(await connection.PictureOperationAsync(route,
            new Dictionary<string, object> { ["operation"] = "status" }));

        Assert.Equal("ready", status.GetProperty("state").GetString());
        Assert.Equal("comfyui", status.GetProperty("engine").GetString());
    }

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
            ["deadline_utc"] = DateTimeOffset.UtcNow.AddSeconds(30).ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            ["payload"] = payload
        }, GatewayTestHost.Json);

    private static async Task<IReadOnlyList<JsonElement>> ObjectsAsync(PictureRelayWorker worker, GatewayPicturePayload payload)
    {
        var request = new GatewayInferenceRequest(worker.Route, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, 0,
            DateTimeOffset.UtcNow.AddSeconds(30), payload);
        var sequence = 0L;
        var objects = new List<JsonElement>();
        await foreach (var item in worker.ExecuteAsync(request, CancellationToken.None))
        {
            GatewayInferenceEventValidator.Validate(item, request, sequence++);
            if (item.Kind == GatewayInferenceEventKind.TextDelta)
            {
                using var document = JsonDocument.Parse(item.Payload);
                objects.Add(document.RootElement.Clone());
            }
        }
        return objects;
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

    private static HttpResponseMessage Bytes(byte[] bytes, string mediaType)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(bytes)
        };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
        return response;
    }

    private sealed class ScriptedHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
