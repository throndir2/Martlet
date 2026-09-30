using System.Net;
using System.Text;
using System.Text.Json;
using Martlet.Gateway;
using Martlet.Perception;

namespace Martlet.Gateway.Tests;

public sealed class InferenceRouteAuthenticationTests
{
    [Fact]
    public async Task Capability_registry_is_exact_versioned_and_role_filtered()
    {
        var ollama = new SyntheticInferenceWorker(
            GatewayInferenceTestData.OllamaRoute());
        var f5 = new SyntheticInferenceWorker(
            GatewayInferenceTestData.F5Route());
        var ocr = new SyntheticInferenceWorker(
            GatewayInferenceTestData.PerceptionRoute(PerceptionRole.Ocr));
        var vlm = new SyntheticInferenceWorker(
            GatewayInferenceTestData.PerceptionRoute(
                PerceptionRole.VisualQuestionAnswering));
        await using var host = await GatewayTestHost.StartAsync(
            inferenceWorkers: [ollama, f5, ocr, vlm]);

        var voiceCredential = await host.PairAsync(GatewayRole.Voice);
        var voiceSigner = new GatewayRequestSigner(
            host.Identity,
            voiceCredential,
            host.Clock);
        using var voiceRequest = host.SignedGet(
            "/martlet/v1/capabilities",
            GatewayRole.Voice,
            voiceSigner);
        using var voiceResponse = await host.Client.SendAsync(voiceRequest);
        using var voiceDocument = JsonDocument.Parse(
            await voiceResponse.Content.ReadAsStringAsync());
        var voice = voiceDocument.RootElement;
        Assert.Equal(GatewayInferenceProtocol.RegistryId,
            voice.GetProperty("registry_id").GetString());
        Assert.Equal(GatewayInferenceProtocol.RegistryVersion,
            voice.GetProperty("registry_version").GetString());
        var voiceRoutes = voice.GetProperty("routes").EnumerateArray().ToArray();
        Assert.Equal(2, voiceRoutes.Length);
        Assert.Equal(
            [
                "martlet.gateway.f5-synthesis.v1",
                "martlet.gateway.ollama-chat.v1"
            ],
            voiceRoutes.Select(route =>
                route.GetProperty("route_id").GetString()!).ToArray());
        Assert.All(voiceRoutes, route =>
        {
            Assert.Equal("voice",
                route.GetProperty("required_role").GetString());
            Assert.Equal(1,
                route.GetProperty("maximum_concurrency").GetInt32());
            Assert.True(route.GetProperty("streaming").GetBoolean());
            Assert.StartsWith("sha256:",
                route.GetProperty("artifact_identity_sha256").GetString(),
                StringComparison.Ordinal);
        });
        var serializedVoice = voice.GetRawText();
        Assert.DoesNotContain("/api/chat", serializedVoice, StringComparison.Ordinal);
        Assert.DoesNotContain("http://", serializedVoice, StringComparison.Ordinal);
        Assert.DoesNotContain("docker", serializedVoice, StringComparison.OrdinalIgnoreCase);

        var perceptionCredential = await host.PairAsync(GatewayRole.Perception);
        var perceptionSigner = new GatewayRequestSigner(
            host.Identity,
            perceptionCredential,
            host.Clock);
        using var perceptionRequest = host.SignedGet(
            "/martlet/v1/capabilities",
            GatewayRole.Perception,
            perceptionSigner);
        using var perceptionResponse = await host.Client.SendAsync(
            perceptionRequest);
        using var perceptionDocument = JsonDocument.Parse(
            await perceptionResponse.Content.ReadAsStringAsync());
        var perceptionRoutes = perceptionDocument.RootElement
            .GetProperty("routes").EnumerateArray().ToArray();
        Assert.Equal(2, perceptionRoutes.Length);
        Assert.All(perceptionRoutes, route =>
            Assert.Equal("perception",
                route.GetProperty("required_role").GetString()));
        Assert.DoesNotContain(perceptionRoutes, route =>
            route.GetProperty("route_id").GetString()!.Contains(
                "ollama",
                StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("ollama")]
    [InlineData("f5")]
    [InlineData("ocr")]
    [InlineData("vlm")]
    public async Task Exact_signed_routes_stream_only_the_registered_contract(
        string kind)
    {
        var route = Route(kind);
        var worker = new SyntheticInferenceWorker(route);
        await using var host = await GatewayTestHost.StartAsync(
            inferenceWorkers: [worker]);
        var credential = await host.PairAsync(route.RequiredRole);
        var signer = new GatewayRequestSigner(
            host.Identity,
            credential,
            host.Clock);
        var body = GatewayInferenceTestData.Request(
            route,
            host.Clock.GetUtcNow());
        using var request = host.SignedPost(
            route.Path,
            route.RequiredRole,
            signer,
            body);

        using var response = await host.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/x-ndjson",
            response.Content.Headers.ContentType?.MediaType);
        var events = await GatewayInferenceTestData.ReadEventsAsync(response);

        var expectedKinds = kind == "f5"
            ? new[] { "started", "audio_frame", "chunk_completed", "completed" }
            : ["started", DataEvent(kind), "completed"];
        Assert.Equal(
            expectedKinds,
            events.Select(item => item.GetProperty("type").GetString()!).ToArray());
        Assert.Equal(
            Enumerable.Range(0, events.Length).Select(index => (long)index),
            events.Select(item => item.GetProperty("sequence").GetInt64()));
        Assert.All(events, item =>
        {
            Assert.Equal(route.RouteId,
                item.GetProperty("route_id").GetString());
            Assert.Equal(route.DestinationId,
                item.GetProperty("destination_id").GetString());
            Assert.Equal(route.WorkerId,
                item.GetProperty("worker_id").GetString());
            Assert.Equal(route.ModelId,
                item.GetProperty("model_id").GetString());
            Assert.Equal(route.ModelRevision,
                item.GetProperty("model_revision").GetString());
            Assert.Equal(route.ArtifactIdentitySha256,
                item.GetProperty("artifact_identity_sha256").GetString());
        });
        Assert.Equal(1, worker.Calls);
        Assert.Equal(1, worker.MaximumActive);
    }

    [Fact]
    public async Task Role_path_and_body_hash_are_enforced_before_parsing_or_dispatch()
    {
        var ollama = new SyntheticInferenceWorker(
            GatewayInferenceTestData.OllamaRoute());
        var f5 = new SyntheticInferenceWorker(
            GatewayInferenceTestData.F5Route());
        await using var host = await GatewayTestHost.StartAsync(
            inferenceWorkers: [ollama, f5]);
        var credential = await host.PairAsync(GatewayRole.Voice);
        var signer = new GatewayRequestSigner(
            host.Identity,
            credential,
            host.Clock);
        var valid = GatewayInferenceTestData.Request(
            ollama.Route,
            host.Clock.GetUtcNow());

        using (var changedBody = host.SignedPost(
            ollama.Route.Path,
            GatewayRole.Voice,
            signer,
            valid))
        {
            var tampered = valid.ToArray();
            var marker = Encoding.UTF8.GetBytes("Fixture prompt.");
            var index = tampered.AsSpan().IndexOf(marker);
            Assert.True(index >= 0);
            tampered[index] = (byte)'X';
            changedBody.Content = JsonContent(tampered);
            using var response = await host.Client.SendAsync(changedBody);
            Assert.Equal("auth.invalid",
                await GatewayTestHost.FailureCode(response));
        }

        var malformed = "{"u8.ToArray();
        using (var wrongRole = host.SignedPost(
            ollama.Route.Path,
            GatewayRole.Perception,
            signer,
            malformed))
        using (var response = await host.Client.SendAsync(wrongRole))
        {
            Assert.Equal("auth.role",
                await GatewayTestHost.FailureCode(response));
        }

        using (var invalidJson = host.SignedPost(
            ollama.Route.Path,
            GatewayRole.Voice,
            signer,
            malformed))
        using (var response = await host.Client.SendAsync(invalidJson))
        {
            Assert.Equal("request.invalid",
                await GatewayTestHost.FailureCode(response));
        }

        using (var changedPath = host.SignedPost(
            ollama.Route.Path,
            GatewayRole.Voice,
            signer,
            valid))
        {
            changedPath.RequestUri = new(
                host.Origin.CanonicalOrigin + f5.Route.Path);
            using var response = await host.Client.SendAsync(changedPath);
            Assert.Equal("auth.invalid",
                await GatewayTestHost.FailureCode(response));
        }

        Assert.Equal(0, ollama.Calls);
        Assert.Equal(0, f5.Calls);
    }

    [Fact]
    public async Task Post_nonce_rotation_and_revocation_apply_to_body_routes()
    {
        var worker = new SyntheticInferenceWorker(
            GatewayInferenceTestData.OllamaRoute());
        await using var host = await GatewayTestHost.StartAsync(
            inferenceWorkers: [worker]);
        var original = await host.PairAsync(GatewayRole.Voice);
        var originalSigner = new GatewayRequestSigner(
            host.Identity,
            original,
            host.Clock);
        var firstBody = GatewayInferenceTestData.Request(
            worker.Route,
            host.Clock.GetUtcNow());
        using var first = host.SignedPost(
            worker.Route.Path,
            GatewayRole.Voice,
            originalSigner,
            firstBody);
        using var replay = GatewayTestHost.ClonePost(first, firstBody);

        using (var accepted = await host.Client.SendAsync(first))
        {
            Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
            _ = await GatewayInferenceTestData.ReadEventsAsync(accepted);
        }
        using (var replayed = await host.Client.SendAsync(replay))
        {
            Assert.Equal("auth.replay",
                await GatewayTestHost.FailureCode(replayed));
        }

        var replacement = host.Server.Credentials.Rotate(
            original.CredentialId,
            TimeSpan.Zero);
        using (var expiredRequest = host.SignedPost(
            worker.Route.Path,
            GatewayRole.Voice,
            originalSigner,
            GatewayInferenceTestData.Request(
                worker.Route,
                host.Clock.GetUtcNow())))
        using (var expired = await host.Client.SendAsync(expiredRequest))
        {
            Assert.Equal("auth.expired",
                await GatewayTestHost.FailureCode(expired));
        }

        var replacementSigner = new GatewayRequestSigner(
            host.Identity,
            replacement,
            host.Clock);
        using (var replacementRequest = host.SignedPost(
            worker.Route.Path,
            GatewayRole.Voice,
            replacementSigner,
            GatewayInferenceTestData.Request(
                worker.Route,
                host.Clock.GetUtcNow())))
        using (var replacementResponse = await host.Client.SendAsync(
            replacementRequest))
        {
            Assert.Equal(HttpStatusCode.OK, replacementResponse.StatusCode);
            _ = await GatewayInferenceTestData.ReadEventsAsync(
                replacementResponse);
        }

        Assert.True(host.Server.Credentials.RevokeCredential(
            replacement.CredentialId));
        using var revokedRequest = host.SignedPost(
            worker.Route.Path,
            GatewayRole.Voice,
            replacementSigner,
            GatewayInferenceTestData.Request(
                worker.Route,
                host.Clock.GetUtcNow()));
        using var revoked = await host.Client.SendAsync(revokedRequest);
        Assert.Equal("auth.revoked",
            await GatewayTestHost.FailureCode(revoked));
        Assert.Equal(2, worker.Calls);
    }

    [Fact]
    public async Task Oversized_and_identity_drifted_requests_never_dispatch()
    {
        var worker = new SyntheticInferenceWorker(
            GatewayInferenceTestData.OllamaRoute());
        await using var host = await GatewayTestHost.StartAsync(
            inferenceWorkers: [worker]);
        var credential = await host.PairAsync(GatewayRole.Voice);
        var signer = new GatewayRequestSigner(
            host.Identity,
            credential,
            host.Clock);

        var oversized = new byte[worker.Route.MaximumRequestBytes + 1];
        Array.Fill(oversized, (byte)' ');
        using (var request = host.SignedPost(
            worker.Route.Path,
            GatewayRole.Voice,
            signer,
            oversized))
        {
            // The route admits a screen image (~1.5 MB), so wait for the gateway's verdict instead of
            // streaming the whole oversized body into a connection it closes.
            request.Headers.ExpectContinue = true;
            using var response = await host.Client.SendAsync(request);
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge,
                response.StatusCode);
            Assert.True(response.Headers.ConnectionClose);
            Assert.Equal("request.too_large",
                await GatewayTestHost.FailureCode(response));
        }

        var valid = Encoding.UTF8.GetString(
            GatewayInferenceTestData.Request(
                worker.Route,
                host.Clock.GetUtcNow()));
        var changedIdentity = Encoding.UTF8.GetBytes(valid.Replace(
            $"\"model_id\":\"{worker.Route.ModelId}\"",
            "\"model_id\":\"other-model\"",
            StringComparison.Ordinal));
        using (var request = host.SignedPost(
            worker.Route.Path,
            GatewayRole.Voice,
            signer,
            changedIdentity))
        using (var response = await host.Client.SendAsync(request))
        {
            Assert.Equal("worker.identity",
                await GatewayTestHost.FailureCode(response));
        }
        Assert.Equal(0, worker.Calls);
    }

    private static GatewayInferenceRoute Route(string kind) => kind switch
    {
        "ollama" => GatewayInferenceTestData.OllamaRoute(),
        "f5" => GatewayInferenceTestData.F5Route(),
        "ocr" => GatewayInferenceTestData.PerceptionRoute(
            PerceptionRole.Ocr),
        "vlm" => GatewayInferenceTestData.PerceptionRoute(
            PerceptionRole.VisualQuestionAnswering),
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private static string DataEvent(string kind) => kind switch
    {
        "ollama" => "text_delta",
        "f5" => "audio_frame",
        "ocr" or "vlm" => "observation",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private static ByteArrayContent JsonContent(byte[] bytes)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new("application/json")
        {
            CharSet = "utf-8"
        };
        return content;
    }
}
