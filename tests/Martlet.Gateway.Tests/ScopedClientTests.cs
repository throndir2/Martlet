using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;
using Martlet.Providers;

namespace Martlet.Gateway.Tests;

public sealed class ScopedClientTests
{
    [Theory]
    [InlineData("clean")]
    [InlineData("late")]
    [InlineData("truncated")]
    [InlineData("duplicate")]
    [InlineData("v1")]
    [InlineData("empty")]
    public async Task TerminalIsPublishedOnlyAfterStrictFramesAndCleanEof(string scenario)
    {
        using var credential = ScopedGatewayCredential.Restore(Origin, Identity, GatewayProtocolVersion.Current,
            CredentialId, "device", GatewayRole.Voice, new PairedDeviceLifetime(), Secret);
        using var pinned = PinnedGatewayClient.CreateForFixture(Origin, new EventHandler(scenario));
        using var client = GatewayAuthenticatedClient.CreateForFixture(pinned, credential);
        var seen = new List<GatewayInferenceEvent>();
        async Task Run()
        {
            await foreach (var item in client.StreamOllamaAsync(
                GatewayInferenceRouteCapability.From(GatewayInferenceTestData.OllamaRoute()),
                Ids(), 1, DateTimeOffset.UtcNow.AddSeconds(5), new("fixture"), new(), 0.25))
                seen.Add(item);
        }
        if (scenario == "clean")
        {
            await Run();
            Assert.Equal(GatewayInferenceEventKind.Completed, seen[^1].Kind);
        }
        else
        {
            await Assert.ThrowsAsync<GatewayProtocolException>(Run);
            Assert.DoesNotContain(seen, item => item.IsTerminal);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BufferedEventsCannotPublishAfterCancellationOrOriginalDeadline(bool expire)
    {
        var clock = new ManualGatewayClock(DateTimeOffset.UtcNow);
        using var credential = ScopedGatewayCredential.Restore(Origin, Identity, GatewayProtocolVersion.Current,
            CredentialId, "device", GatewayRole.Voice, new PairedDeviceLifetime(), Secret);
        using var pinned = PinnedGatewayClient.CreateForFixture(Origin, new EventHandler("clean"));
        using var client = GatewayAuthenticatedClient.CreateForFixture(pinned, credential, clock);
        using var stop = new CancellationTokenSource();
        await using var events = client.StreamOllamaAsync(
            GatewayInferenceRouteCapability.From(GatewayInferenceTestData.OllamaRoute()), Ids(), 1,
            clock.GetUtcNow().AddSeconds(5), new("fixture"), new(), 0.25, stop.Token).GetAsyncEnumerator();
        Assert.True(await events.MoveNextAsync());
        Assert.Equal(GatewayInferenceEventKind.Started, events.Current.Kind);
        if (expire) clock.Advance(TimeSpan.FromSeconds(6));
        else stop.Cancel();
        if (expire)
            Assert.Equal("gateway.deadline", (await Assert.ThrowsAsync<GatewayClientException>(
                () => events.MoveNextAsync().AsTask())).Failure.Code);
        else
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => events.MoveNextAsync().AsTask());
    }

    [Theory]
    [InlineData("f5-frame")]
    [InlineData("f5-offset")]
    [InlineData("f5-chunk")]
    [InlineData("f5-count")]
    public async Task F5CrossEventContinuityIsRequiredBeforePublication(string scenario)
    {
        using var credential = ScopedGatewayCredential.Restore(Origin, Identity, GatewayProtocolVersion.Current,
            CredentialId, "device", GatewayRole.Voice, new PairedDeviceLifetime(), Secret);
        using var pinned = PinnedGatewayClient.CreateForFixture(Origin, new EventHandler(scenario));
        using var client = GatewayAuthenticatedClient.CreateForFixture(pinned, credential);
        var route = GatewayInferenceTestData.F5Route();
        var request = GatewayInferenceJson.ParseRequest(GatewayInferenceTestData.Request(route, DateTimeOffset.UtcNow),
            route, DateTimeOffset.UtcNow);
        var seen = new List<GatewayInferenceEvent>();
        await Assert.ThrowsAsync<GatewayProtocolException>(async () =>
        {
            await foreach (var item in client.StreamAsync(request, CancellationToken.None)) seen.Add(item);
        });
        Assert.DoesNotContain(seen, item => item.IsTerminal);
    }

    [Fact]
    public async Task ActualTlsPairCapabilitiesAndSignedPostUseProtocolTwoAndPermanentCredentials()
    {
        var worker = new SyntheticInferenceWorker(GatewayInferenceTestData.OllamaRoute());
        var f5 = new SyntheticInferenceWorker(GatewayInferenceTestData.F5Route());
        await using var host = await GatewayTestHost.StartAsync(inferenceWorkers: [worker, f5]);
        var card = host.OpenPairing();
        using var token = new SecretLease(card.Token.Reveal());
        using var credential = await GatewayPairingClient.PairAsync(host.Client, host.Origin, host.Identity,
            card.PairingId, "fixture-device", token);
        Assert.IsType<PairedDeviceLifetime>(credential.Lifetime);
        using var client = GatewayAuthenticatedClient.CreateForFixture(host.Client, credential, host.Clock);
        var capabilities = await client.ReadCapabilitiesAsync(GatewayRole.Voice);
        Assert.Equal(2, capabilities.ProtocolVersion.Major);
        Assert.Equal(2, capabilities.Routes.Count);
        var events = new List<GatewayInferenceEvent>();
        await foreach (var item in client.StreamOllamaAsync(capabilities.Routes.Single(r => r.Kind == GatewayInferenceKind.OllamaChat),
            Ids(), 1, host.Clock.GetUtcNow().AddSeconds(10), new("FIXTURE - NOT AI"), new(), 0.25))
            events.Add(item);
        Assert.Equal(GatewayInferenceEventKind.Completed, events[^1].Kind);
        Assert.Equal(1, worker.Calls);
        host.Server.Credentials.RevokeCredential(credential.CredentialId);
        await Assert.ThrowsAsync<GatewayRemoteException>(() => client.ReadCapabilitiesAsync(GatewayRole.Voice));
        // A connection/auth refusal does not erase client-held pairing material.
        credential.UseSecret(value => Assert.Equal(43, value.Length));
    }

    [Theory]
    [InlineData("v1")]
    [InlineData("missing")]
    [InlineData("null")]
    [InlineData("retiring")]
    [InlineData("expires")]
    [InlineData("duplicate")]
    [InlineData("role")]
    public async Task PairingNeverInterpretsTimedOrMissingLifetimeAsPermanent(string mutation)
    {
        var response = JsonSerializer.Serialize(new
        {
            protocol_version = new { major = 2, minor = 0 }, host_id = Identity.HostId,
            credential_id = CredentialId, credential_secret = Secret, device_id = "device",
            roles = new[] { "voice" }, lifetime = new { kind = "paired" }
        });
        response = mutation switch
        {
            "v1" => response.Replace("\"major\":2", "\"major\":1"),
            "missing" => response.Replace(",\"lifetime\":{\"kind\":\"paired\"}", ""),
            "null" => response.Replace("{\"kind\":\"paired\"}", "null"),
            "retiring" => response.Replace("{\"kind\":\"paired\"}",
                "{\"kind\":\"retiring\",\"expires_at\":\"9999-12-31T23:59:59Z\"}"),
            "expires" => response.Replace("\"kind\":\"paired\"", "\"kind\":\"paired\",\"expires_at\":null"),
            "duplicate" => response.Replace("\"kind\":\"paired\"", "\"kind\":\"paired\",\"kind\":\"paired\""),
            _ => response.Replace("\"voice\"", "\"memory\"")
        };
        using var pinned = PinnedGatewayClient.CreateForFixture(Origin, new JsonHandler(response));
        using var token = new SecretLease(Secret);
        await Assert.ThrowsAsync<GatewayProtocolException>(() => GatewayPairingClient.PairAsync(
            pinned, Origin, Identity, CredentialId, "device", token));
    }

    [Fact]
    public void RestoreRequiresExplicitV2LifetimeAndKeepsOriginHostDeviceAndRoleBound()
    {
        Assert.Throws<GatewayProtocolException>(() => ScopedGatewayCredential.Restore(Origin, Identity,
            new() { Major = 1, Minor = 0 }, CredentialId, "device", GatewayRole.Voice, new PairedDeviceLifetime(), Secret));
        Assert.Throws<GatewayProtocolException>(() => ScopedGatewayCredential.Restore(Origin, Identity,
            GatewayProtocolVersion.Current, CredentialId, "device", GatewayRole.Memory, new PairedDeviceLifetime(), Secret));
        Assert.Throws<GatewayProtocolException>(() => ScopedGatewayCredential.Restore(Origin, Identity,
            GatewayProtocolVersion.Current, CredentialId, "device", GatewayRole.Voice, null!, Secret));
        using var restored = ScopedGatewayCredential.Restore(Origin, Identity, GatewayProtocolVersion.Current,
            CredentialId, "device", GatewayRole.Voice, new PairedDeviceLifetime(), Secret);
        Assert.Equal(Origin.CanonicalOrigin, restored.Origin.CanonicalOrigin);
        Assert.Equal(Identity, restored.Identity);
        Assert.Equal("device", restored.DeviceId);
        var clock = new ManualGatewayClock(DateTimeOffset.UtcNow.AddYears(100));
        using var signer = restored.CreateSigner(clock);
        using var message = new HttpRequestMessage(HttpMethod.Get, Origin.CanonicalOrigin + "/martlet/v1/capabilities");
        signer.Sign(message, GatewayRole.Voice);
        Assert.NotNull(message.Headers.Authorization);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SignerPreservesExistingGetAndPostWireBytes(bool post)
    {
        var crypto = new BlockingCrypto();
        using var signer = Signer(crypto);
        using var request = Request(post);
        if (post) signer.Sign(request, GatewayRole.Voice, "{}"u8);
        else signer.Sign(request, GatewayRole.Voice);
        var nonce = request.Headers.GetValues("X-Martlet-Nonce").Single();
        var timestamp = long.Parse(request.Headers.GetValues("X-Martlet-Timestamp").Single());
        var canonical = GatewayRequestSigner.Canonical(Identity.HostId, CredentialId,
            request.Method.Method, request.RequestUri!.PathAndQuery, "voice", timestamp, nonce,
            SHA256.HashData(post ? "{}"u8 : []));
        Assert.True(Base64Url.TryDecode(Secret, 32, out var secret));
        var signature = Base64Url.Encode(HMACSHA256.HashData(SHA256.HashData(secret), canonical));
        CryptographicOperations.ZeroMemory(secret);
        Assert.Equal($"Martlet-HMAC {CredentialId}.{signature}", request.Headers.Authorization!.ToString());
    }

    [Fact]
    public async Task DisposeWaitsForInflightSignThenAllFurtherSignsFail()
    {
        var crypto = new BlockingCrypto { Block = true };
        using var signer = Signer(crypto);
        using var request = Request(false);
        var signing = Task.Run(() => signer.Sign(request, GatewayRole.Voice));
        Assert.True(crypto.Entered.Wait(TimeSpan.FromSeconds(5)));
        var disposal = Task.Run(signer.Dispose);
        try
        {
            await Task.Delay(50);
            Assert.False(disposal.IsCompleted);
            Assert.Contains(crypto.ObservedKey.ToArray(), value => value != 0);
        }
        finally { crypto.Release.Set(); }
        await Task.WhenAll(signing, disposal).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.All(crypto.ObservedKey.ToArray(), value => Assert.Equal(0, value));
        using var after = Request(false);
        Assert.Throws<ObjectDisposedException>(() => signer.Sign(after, GatewayRole.Voice));
        Assert.Null(after.Headers.Authorization);
    }

    private static GatewayOrigin Origin => new("https://127.0.0.1:7443");
    private static GatewayHostIdentity Identity => new() { HostId = "fixture-host", SpkiFingerprint = "sha256:" + new string('a', 64) };
    private static string CredentialId => Base64Url.Encode(new byte[16]);
    private static string Secret => Base64Url.Encode(Enumerable.Repeat((byte)1, 32).ToArray());
    private static CorrelationIds Ids() => new() { SessionId = Guid.NewGuid(), TurnId = Guid.NewGuid(), RequestId = Guid.NewGuid() };
    private static HttpRequestMessage Request(bool post) => new(post ? HttpMethod.Post : HttpMethod.Get,
        Origin.CanonicalOrigin + "/martlet/v1/capabilities") { Content = post ? new StringContent("{}") : null };
    private static GatewayRequestSigner Signer(IGatewayCrypto crypto) => new(Identity, new()
    {
        CredentialId = CredentialId, DeviceId = "device", Roles = [GatewayRole.Voice],
        Secret = new(Secret), Lifetime = new PairedDeviceLifetime()
    }, crypto: crypto);

    private sealed class JsonHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created)
            { Content = new StringContent(json, Encoding.UTF8, "application/json") });
    }

    private sealed class EventHandler(string scenario) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var parsed = JsonDocument.Parse(await request.Content!.ReadAsByteArrayAsync(cancellationToken));
            var value = new Dictionary<string, object?>();
            foreach (var name in new[] { "route_id", "destination_id", "worker_id", "model_id", "model_revision",
                "model_sha256", "artifact_identity_sha256", "session_id", "turn_id", "request_id", "epoch" })
                value[name] = parsed.RootElement.GetProperty(name);
            value["protocol_version"] = new { major = scenario == "v1" ? 1 : 2, minor = 0 };
            value["registry_version"] = "1.0";
            value["trace_id"] = Guid.NewGuid();
            value["sequence"] = 0;
            value["type"] = "started";
            var started = JsonSerializer.Serialize(value) + "\n";
            value["sequence"] = 1;
            value["type"] = "text_delta";
            value["text"] = "FIXTURE - NOT AI";
            var data = JsonSerializer.Serialize(value) + "\n";
            value.Remove("text");
            if (scenario.StartsWith("f5-", StringComparison.Ordinal))
            {
                value["type"] = "audio_frame";
                value["data_base64"] = "AQA=";
                value["data_media_type"] = "audio/L16;rate=24000;channels=1";
                value["frame_sequence"] = scenario == "f5-frame" ? 1 : 0;
                value["sample_offset"] = scenario == "f5-offset" ? 1 : 0;
                value["chunk_index"] = scenario == "f5-chunk" ? 1 : 0;
                value["sample_count"] = 1;
                data = JsonSerializer.Serialize(value) + "\n";
                foreach (var name in new[] { "data_base64", "data_media_type", "frame_sequence", "sample_offset", "chunk_index", "sample_count" })
                    value.Remove(name);
                value["final_sample_count"] = 2;
            }
            value["sequence"] = scenario == "empty" ? 1 : 2;
            value["type"] = "completed";
            var completed = JsonSerializer.Serialize(value) + "\n";
            var wire = scenario == "truncated" ? started :
                scenario == "empty" ? started + completed : started + data + completed;
            if (scenario == "late") wire += "{}\n";
            if (scenario == "duplicate") wire = wire.Replace("\"sequence\":0", "\"sequence\":0,\"sequence\":0");
            return new(HttpStatusCode.OK) { Content = new StringContent(wire, Encoding.UTF8, "application/x-ndjson") };
        }
    }

    private sealed class BlockingCrypto : IGatewayCrypto
    {
        private readonly SystemGatewayCrypto inner = new();
        internal bool Block { get; init; }
        internal ManualResetEventSlim Entered { get; } = new();
        internal ManualResetEventSlim Release { get; } = new();
        internal ReadOnlyMemory<byte> ObservedKey { get; private set; }
        public byte[] RandomBytes(int count) => inner.RandomBytes(count);
        public byte[] Sha256(ReadOnlyMemory<byte> value) => inner.Sha256(value);
        public byte[] HmacSha256(ReadOnlyMemory<byte> key, ReadOnlyMemory<byte> value)
        {
            ObservedKey = key;
            Entered.Set();
            if (Block && !Release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
            return inner.HmacSha256(key, value);
        }
        public bool FixedTimeEquals(ReadOnlyMemory<byte> left, ReadOnlyMemory<byte> right) => inner.FixedTimeEquals(left, right);
        public string SpkiFingerprint(X509Certificate2 certificate) => inner.SpkiFingerprint(certificate);
    }
}
