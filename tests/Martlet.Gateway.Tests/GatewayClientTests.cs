using System.Net;
using System.Net.Http.Headers;
using Martlet.Core.Contracts;
using Martlet.Providers;

namespace Martlet.Gateway.Tests;

public sealed class GatewayClientTests
{
    [Fact]
    public async Task ClientRejectsOversizedUnterminatedEventBeforeParsingIt()
    {
        var route = GatewayInferenceTestData.OllamaRoute();
        var capability = GatewayInferenceRouteCapability.From(route);
        using var credential = Credential();
        using var pinned = PinnedGatewayClient.CreateForFixture(
            Origin(), new StaticHandler((_, _) => Response(
                new byte[route.MaximumEventBytes + 1])));
        using var client = GatewayAuthenticatedClient.CreateForFixture(
            pinned, credential);

        var error = await Assert.ThrowsAsync<GatewayProtocolException>(
            async () => await DrainAsync(client.StreamOllamaAsync(
                capability,
                Ids(),
                1,
                DateTimeOffset.UtcNow.AddSeconds(10),
                new BoundedTextInput("Fixture prompt."),
                new TextGenerationLimits(),
                0.25)));

        Assert.Equal("stream.limit", error.Failure.Code);
    }

    [Fact]
    public async Task ClientDeadlineCancelsAStalledNdjsonBody()
    {
        var route = GatewayInferenceTestData.OllamaRoute();
        var capability = GatewayInferenceRouteCapability.From(route);
        using var credential = Credential();
        using var pinned = PinnedGatewayClient.CreateForFixture(
            Origin(), new StaticHandler((_, _) => Response(new BlockingStream())));
        using var client = GatewayAuthenticatedClient.CreateForFixture(
            pinned, credential);

        var error = await Assert.ThrowsAsync<GatewayClientException>(
            async () => await DrainAsync(client.StreamOllamaAsync(
                capability,
                Ids(),
                1,
                DateTimeOffset.UtcNow.AddMilliseconds(500),
                new BoundedTextInput("Fixture prompt."),
                new TextGenerationLimits(),
                0.25)));

        Assert.Equal("gateway.deadline", error.Failure.Code);
    }

    [Fact]
    public void DisposingSignerAndCredentialRetiresAuthenticationMaterial()
    {
        using var credential = Credential();
        var signer = credential.CreateSigner();
        signer.Dispose();
        using var request = new HttpRequestMessage(
            HttpMethod.Get, Origin().CanonicalOrigin + "/martlet/v1/version");
        Assert.Throws<ObjectDisposedException>(() => signer.Sign(request, GatewayRole.Voice));

        credential.Dispose();
        Assert.Throws<ObjectDisposedException>(() => credential.UseSecret(_ => { }));
    }

    private static async Task DrainAsync(
        IAsyncEnumerable<GatewayInferenceEvent> events)
    {
        await foreach (var _ in events)
        {
        }
    }

    private static HttpResponseMessage Response(byte[] bytes)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(bytes)
        };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue(
            "application/x-ndjson");
        return response;
    }

    private static HttpResponseMessage Response(Stream stream)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(stream)
        };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue(
            "application/x-ndjson");
        return response;
    }

    private static GatewayOrigin Origin() => new("https://127.0.0.1:7443");

    private static GatewayHostIdentity Identity() => new()
    {
        HostId = "fixture-host",
        SpkiFingerprint = "sha256:" + new string('a', 64)
    };

    private static ScopedGatewayCredential Credential() =>
        ScopedGatewayCredential.Restore(Origin(), Identity(), GatewayProtocolVersion.Current,
            Base64Url.Encode(Enumerable.Range(0, 16).Select(value => (byte)value).ToArray()),
            "fixture-device",
            GatewayRole.Voice, new PairedDeviceLifetime(),
            Base64Url.Encode(Enumerable.Range(0, 32).Select(value => (byte)(value + 1)).ToArray()));

    private static CorrelationIds Ids() => new()
    {
        SessionId = Guid.NewGuid(),
        TurnId = Guid.NewGuid(),
        RequestId = Guid.NewGuid()
    };

    private sealed class StaticHandler(
        Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> response)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(response(request, cancellationToken));
    }

    private sealed class BlockingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();
        public override void SetLength(long value) =>
            throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }
}
