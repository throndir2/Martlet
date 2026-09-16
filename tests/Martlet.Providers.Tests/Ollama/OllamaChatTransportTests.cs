using System.Net;
using System.Text;
using System.Text.Json;
using Martlet.Core.Contracts;
using Martlet.Providers.Ollama;

namespace Martlet.Providers.Tests.Ollama;

public sealed class OllamaChatTransportTests
{
    [Fact]
    public async Task Exact_request_body_origin_options_and_single_send_policy()
    {
        var handler = OllamaFixtures.Handler();
        handler.Inspect = request =>
        {
            Assert.Equal("http://127.0.0.1:12345/api/chat", request.RequestUri!.AbsoluteUri);
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal(HttpVersion.Version11, request.Version);
            Assert.Equal(HttpVersionPolicy.RequestVersionExact, request.VersionPolicy);
            Assert.Null(request.Headers.Authorization);
            Assert.False(request.Headers.Contains("Cookie"));
            Assert.Equal("application/x-ndjson", Assert.Single(request.Headers.Accept).MediaType);
            Assert.Equal("application/json; charset=utf-8", request.Content!.Headers.ContentType!.ToString());
        };
        handler.Respond = async (request, token) =>
        {
            using var replay = new MemoryStream();
            await Assert.ThrowsAsync<HttpRequestException>(() => request.Content!.CopyToAsync(replay, token));
            Assert.Empty(replay.ToArray());
            return OllamaFixtures.Response(new FragmentedTextBody(Encoding.UTF8.GetBytes(OllamaFixtures.Trace)));
        };
        var source = new OllamaAuthority();
        await using var adapter = OllamaChatAdapter.CreateForFixture(handler, source, new FixtureClock());
        const string input = "Exact \"quoted\" \u00e9\ninput.";
        var result = await OllamaFixtures.Collect(OllamaFixtures.Stream(adapter, input: new(input)));
        using var document = JsonDocument.Parse(handler.Body);
        var root = document.RootElement;
        Assert.Equal(9, root.EnumerateObject().Count());
        Assert.Equal("fixture-model:v1:local", root.GetProperty("model").GetString());
        Assert.True(root.GetProperty("stream").GetBoolean());
        foreach (var name in new[] { "think", "truncate", "shift", "logprobs" }) Assert.False(root.GetProperty(name).GetBoolean());
        Assert.Empty(root.GetProperty("tools").EnumerateArray());
        var message = Assert.Single(root.GetProperty("messages").EnumerateArray());
        Assert.Equal(2, message.EnumerateObject().Count());
        Assert.Equal("user", message.GetProperty("role").GetString());
        Assert.Equal(input, message.GetProperty("content").GetString());
        var options = root.GetProperty("options");
        Assert.Equal(3, options.EnumerateObject().Count());
        Assert.Equal(0.25, options.GetProperty("temperature").GetDouble());
        Assert.Equal(256, options.GetProperty("num_predict").GetInt32());
        Assert.Equal(32768, options.GetProperty("num_ctx").GetInt32());
        Assert.Equal(TextGenerationOutcome.Completed, result.Result.Outcome);
        Assert.Equal(1, source.Calls);
        Assert.Equal(1, handler.Calls);
        Assert.Equal(1, source.Lease.Calls);
        Assert.InRange(handler.Body.Length, 1, OllamaChatRequestEncoder.MaxRequestBytes);
    }

    [Fact]
    public void Production_handler_is_inspected_without_opening_a_socket()
    {
        using var handler = OllamaChatTransport.CreateHandler();
        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseCookies);
        Assert.False(handler.UseProxy);
        Assert.Null(handler.Proxy);
        Assert.Null(handler.Credentials);
        Assert.Null(handler.DefaultProxyCredentials);
        Assert.Null(handler.SslOptions.RemoteCertificateValidationCallback);
        Assert.Equal(DecompressionMethods.None, handler.AutomaticDecompression);
        Assert.Equal(0, handler.MaxResponseDrainSize);
        Assert.Equal(TimeSpan.Zero, handler.ResponseDrainTimeout);
        Assert.Equal(16, handler.MaxResponseHeadersLength);
    }

    [Theory]
    [InlineData("http://localhost:12345")]
    [InlineData("http://127.1:12345")]
    [InlineData("http://2130706433:12345")]
    [InlineData("http://0177.0.0.1:12345")]
    [InlineData("http://127.0.0.2:12345")]
    [InlineData("http://[::1]:12345")]
    [InlineData("https://127.0.0.1:12345")]
    [InlineData("HTTP://127.0.0.1:12345")]
    [InlineData("http://127.0.0.1")]
    [InlineData("http://127.0.0.1:0")]
    [InlineData("http://127.0.0.1:65536")]
    [InlineData("http://127.0.0.1:012345")]
    [InlineData("http://127.0.0.1:12345/")]
    [InlineData("http://127.0.0.1:12345/api/chat")]
    [InlineData("http://127.0.0.1:12345?x=1")]
    [InlineData("http://127.0.0.1:12345#fragment")]
    [InlineData("http://127.0.0.1:12345\\")]
    [InlineData("http://user@127.0.0.1:12345")]
    [InlineData("http://127.0.0.1%2e:12345")]
    [InlineData("http://192.168.1.2:12345")]
    [InlineData("http://0.0.0.0:12345")]
    [InlineData(" http://127.0.0.1:12345")]
    [InlineData("http://127.0.0.1:12345\n")]
    public void Origin_is_checked_before_uri_normalization(string input) =>
        Assert.Throws<ContractException>(() => new OllamaLoopbackOrigin(input));

    [Theory]
    [InlineData("model")]
    [InlineData("model:")]
    [InlineData("model:tag:local")]
    [InlineData("model:tag:cloud")]
    [InlineData("model:cloud")]
    [InlineData("model:local")]
    [InlineData("model:tag-cloud")]
    [InlineData("model:tag:CLOUD")]
    [InlineData("model:cloud:local")]
    [InlineData("model:tag:local:local")]
    [InlineData("model:tag-cloud:local")]
    [InlineData("model:tag:unknown")]
    [InlineData("registry.test/ns/model:tag")]
    [InlineData("registry.test:123/ns/model:tag")]
    [InlineData("model:tag@sha256:abc")]
    [InlineData("model:tag\n")]
    [InlineData("model:tag ")]
    [InlineData("model:Tag")]
    [InlineData("../model:tag")]
    [InlineData("name%2fmodel:tag")]
    [InlineData("model\\name:tag")]
    [InlineData("ns.one/model:tag")]
    public void Ambiguous_or_remote_source_syntax_cannot_be_forwarded(string value) =>
        Assert.Throws<ContractException>(() => new OllamaChatModelSelection("test", value));

    [Theory]
    [InlineData("ns/model-name:v1")]
    [InlineData("model:latest")]
    [InlineData("my-cloud-model:v1")]
    public void Explicit_mutable_names_construct_exactly_one_local_selector(string value) =>
        Assert.Equal(value + ":local", new OllamaChatModelSelection("test", value).RequestModel);

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(-0.1)]
    [InlineData(2.1)]
    public void Sampling_option_is_finite_and_bounded(double value) =>
        Assert.Throws<ContractException>(() => new OllamaChatOptions(value));

    [Theory]
    [InlineData(301, ProviderFailureCode.RedirectRejected)]
    [InlineData(302, ProviderFailureCode.RedirectRejected)]
    [InlineData(307, ProviderFailureCode.RedirectRejected)]
    [InlineData(308, ProviderFailureCode.RedirectRejected)]
    [InlineData(400, ProviderFailureCode.RequestRejected)]
    [InlineData(401, ProviderFailureCode.Authentication)]
    [InlineData(403, ProviderFailureCode.PermissionDenied)]
    [InlineData(404, ProviderFailureCode.RequestRejected)]
    [InlineData(429, ProviderFailureCode.RateLimited)]
    [InlineData(499, ProviderFailureCode.RequestRejected)]
    [InlineData(500, ProviderFailureCode.Server)]
    [InlineData(503, ProviderFailureCode.Server)]
    public async Task Status_redirect_and_missing_remote_alias_fixtures_never_retry_or_manage_models(int status, ProviderFailureCode expected)
    {
        var body = new FragmentedTextBody([])
        {
            OnRead = () => throw new IOException("model missing or remote alias " + ProviderFixtures.ContentCanary)
        };
        var handler = OllamaFixtures.Handler();
        handler.Respond = (_, _) =>
        {
            var response = OllamaFixtures.Response(body, status);
            response.Headers.Location = new("http://192.168.1.2:9999/model-admin");
            response.Headers.RetryAfter = new(TimeSpan.FromSeconds(400));
            return Task.FromResult(response);
        };
        await using var adapter = OllamaChatAdapter.CreateForFixture(handler, new OllamaAuthority(), new FixtureClock());
        var result = await OllamaFixtures.Collect(OllamaFixtures.Stream(adapter));
        Assert.Equal(expected, result.Result.FailureCode);
        Assert.Equal(TimeSpan.FromSeconds(300), result.Result.RetryAfter);
        Assert.False(result.Result.Error!.Retryable);
        Assert.Equal(0, body.BytesRead);
        Assert.True(body.Disposed);
        Assert.Equal(1, handler.Calls);
        Assert.Contains("fixture-model:v1:local", Encoding.UTF8.GetString(handler.Body));
    }

    [Theory]
    [InlineData("application/json")]
    [InlineData("text/event-stream")]
    [InlineData("application/x-ndjson; charset=latin1")]
    [InlineData("application/x-ndjson; x=1")]
    [InlineData("gzip")]
    public async Task Wrong_media_type_or_encoding_is_not_interpreted_as_text(string media)
    {
        var handler = OllamaFixtures.Handler();
        handler.Respond = (_, _) =>
        {
            var response = OllamaFixtures.Response(new FragmentedTextBody(Encoding.UTF8.GetBytes(OllamaFixtures.Trace)));
            if (media == "gzip") response.Content.Headers.ContentEncoding.Add("gzip");
            else response.Content.Headers.ContentType = new(media.Split(';')[0]);
            if (media.Contains(';')) response.Content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse(media);
            return Task.FromResult(response);
        };
        await using var adapter = OllamaChatAdapter.CreateForFixture(handler, new OllamaAuthority(), new FixtureClock());
        Assert.Equal(ProviderFailureCode.ResponseSchema, (await OllamaFixtures.Collect(OllamaFixtures.Stream(adapter))).Result.FailureCode);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    public async Task Declared_http_length_must_match_actual_complete_body(int difference)
    {
        var bytes = Encoding.UTF8.GetBytes(OllamaFixtures.Trace);
        var handler = OllamaFixtures.Handler();
        handler.Respond = (_, _) => Task.FromResult(OllamaFixtures.Response(new FragmentedTextBody(bytes, 1),
            length: bytes.Length + difference));
        await using var adapter = OllamaChatAdapter.CreateForFixture(handler, new OllamaAuthority(), new FixtureClock());
        var result = await OllamaFixtures.Collect(OllamaFixtures.Stream(adapter));
        Assert.Equal(difference == 0 ? TextGenerationOutcome.Completed : TextGenerationOutcome.Failed, result.Result.Outcome);
        if (difference != 0) Assert.Equal(ProviderFailureCode.ResponseTruncated, result.Result.FailureCode);
    }
}
