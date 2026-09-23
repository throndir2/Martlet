using System.Net;
using System.Net.Http.Headers;
using Martlet.HostArtifacts;

namespace Martlet.Host.Setup.Tests;

public sealed class AcquisitionTransportTests
{
    internal const string SignedLocation =
        "https://release-assets.githubusercontent.com/github-production-release-asset/123/fixture?sig=PRIVATE-CANARY%2f%2B&x=1+2";

    internal static ArtifactAcquisitionSource Source() =>
        AcquisitionTestData.Manifest([1, 2, 3]).DescribeArtifact("ollama-linux-amd64").Source;

    internal static ArtifactDownloadRequest Request(
        ArtifactAcquisitionSource source, long offset, string? etag, DateTimeOffset? modified) =>
        new(source, offset, etag, modified, static _ => ValueTask.CompletedTask);

    internal static HttpResponseMessage Body(HttpRequestMessage request, long offset = 0)
    {
        var response = new HttpResponseMessage(
            offset > 0 ? HttpStatusCode.PartialContent : HttpStatusCode.OK)
        {
            RequestMessage = request,
            Content = new ByteArrayContent(offset > 0 ? [2, 3] : [1, 2, 3])
        };
        response.Content.Headers.ContentLength = offset > 0 ? 2 : 3;
        response.Headers.ETag = new EntityTagHeaderValue("\"fixture-etag-v1\"");
        if (offset > 0)
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(1, 2, 3);
        return response;
    }

    internal static HttpResponseMessage Redirect(HttpRequestMessage request, string location = SignedLocation)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Redirect)
        {
            RequestMessage = request,
            Content = new ByteArrayContent([])
        };
        response.Headers.TryAddWithoutValidation("Location", location);
        return response;
    }

    [Fact]
    public void Production_handler_has_no_ambient_auth_redirects_tracing_or_unvetted_connect()
    {
        using var handler = HttpsArtifactDownloadTransport.CreateProductionHandler();
        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseProxy);
        Assert.Null(handler.Proxy);
        Assert.False(handler.UseCookies);
        Assert.False(handler.PreAuthenticate);
        Assert.Null(handler.Credentials);
        Assert.Null(handler.DefaultProxyCredentials);
        Assert.Null(handler.ActivityHeadersPropagator);
        Assert.Equal(DecompressionMethods.None, handler.AutomaticDecompression);
        Assert.Equal(0, handler.MaxResponseDrainSize);
        Assert.Equal(TimeSpan.Zero, handler.ResponseDrainTimeout);
        Assert.Equal(TimeSpan.Zero, handler.PooledConnectionLifetime);
        Assert.Equal(TimeSpan.FromSeconds(30), handler.ConnectTimeout);
        Assert.Equal(16, handler.MaxResponseHeadersLength);
        Assert.NotNull(handler.ConnectCallback);
        Assert.Null(handler.SslOptions.RemoteCertificateValidationCallback);
        Assert.Null(handler.SslOptions.ClientCertificates);
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 0)]
    [InlineData(false, 1)]
    [InlineData(true, 1)]
    public async Task Exact_api_get_and_optional_one_signed_hop_return_only_logical_binding(
        bool redirect, long offset)
    {
        var source = Source();
        var seen = new List<string>();
        var handler = new RecordingDownloadHandler
        {
            Respond = (request, _) =>
            {
                seen.Add(request.RequestUri!.OriginalString);
                Assert.Equal(HttpMethod.Get, request.Method);
                Assert.Equal("application/octet-stream", request.Headers.Accept.Single().MediaType);
                Assert.Equal("Martlet-ArtifactAcquisition/1.0", request.Headers.UserAgent.ToString());
                Assert.DoesNotContain(request.Headers, pair =>
                    pair.Key is "Authorization" or "Cookie" or "Proxy-Authorization" or "Referer" or
                        "traceparent" or "tracestate" or "baggage");
                if (redirect && seen.Count == 1)
                    return Task.FromResult(Redirect(request));
                Assert.Equal(seen.Count == 1, request.Headers.Contains("X-GitHub-Api-Version"));
                var response = Body(request, offset);
                response.Headers.TryAddWithoutValidation("Location", SignedLocation);
                response.Headers.TryAddWithoutValidation("Set-Cookie", "secret=PRIVATE-CANARY");
                response.Content.Headers.TryAddWithoutValidation("Content-Disposition", "PRIVATE-CANARY");
                response.ReasonPhrase = "PRIVATE-CANARY";
                return Task.FromResult(response);
            }
        };
        using var transport = new HttpsArtifactDownloadTransport(handler);
        using var result = await transport.SendAsync(
            Request(source, offset, offset > 0 ? "\"fixture-etag-v1\"" : null, null),
            CancellationToken.None);
        Assert.Equal(redirect ? 2 : 1, seen.Count);
        Assert.Equal(source.RequestUri.AbsoluteUri, seen[0]);
        if (redirect)
            Assert.Equal(SignedLocation, seen[1]);
        Assert.Equal(source.IdentityFingerprint, result.SourceIdentityFingerprint);
        Assert.Equal(source.LogicalSourceUrl, result.LogicalSourceUrl);
        Assert.Equal(redirect ? HttpsArtifactDownloadTransport.AssetOrigin : HttpsArtifactDownloadTransport.ApiOrigin,
            result.TerminalOrigin);
        Assert.Null(result.Response.RequestMessage);
        Assert.DoesNotContain("PRIVATE-CANARY", result.Response.ToString());
        Assert.Equal(offset > 0 ? new byte[] { 2, 3 } : [1, 2, 3],
            await result.Response.Content.ReadAsByteArrayAsync());
        Assert.All(handler.Requests, request =>
        {
            Assert.Equal(offset > 0 ? "bytes=1-" : null, request.Range);
            Assert.Equal(offset > 0 ? "\"fixture-etag-v1\"" : null, request.IfRange);
            Assert.Null(request.Authorization);
            Assert.Equal(["identity"], request.AcceptEncoding);
            Assert.Equal(HttpVersion.Version11, request.Version);
            Assert.Equal(HttpVersionPolicy.RequestVersionExact, request.VersionPolicy);
        });
    }

    [Theory]
    [InlineData("")]
    [InlineData("/relative?PRIVATE-CANARY")]
    [InlineData("//release-assets.githubusercontent.com/a")]
    [InlineData("http://release-assets.githubusercontent.com/a")]
    [InlineData("https://release-assets.githubusercontent.com:444/a")]
    [InlineData("https://release-assets.githubusercontent.com.evil.invalid/a")]
    [InlineData("https://release-assets.githubusercontent.com./a")]
    [InlineData("https://RELEASE-ASSETS.githubusercontent.com/a")]
    [InlineData("https://api.github.com/repos/o/r/releases/assets/1")]
    [InlineData("https://objects.githubusercontent.com/a")]
    [InlineData("https://release-assets.githubusercontent.com@evil.invalid/a")]
    [InlineData("https://user@release-assets.githubusercontent.com/a")]
    [InlineData("https://release-assets.githubusercontent.com/a#fragment")]
    [InlineData("https://release-assets.githubusercontent.com/a\\b")]
    [InlineData("https://release-assets.githubusercontent.com/a/../b")]
    [InlineData("https://release-assets.githubusercontent.com/a/%2e%2e/b")]
    [InlineData("https://release-assets.githubusercontent.com//a")]
    [InlineData("https://release-assets.githubusercontent.com/a\r\nX: canary")]
    [InlineData("https://127.0.0.1/a")]
    [InlineData("https://[::1]/a")]
    [InlineData("https://releаse-assets.githubusercontent.com/a")]
    public async Task Unapproved_locations_fail_before_second_request(string location)
    {
        var handler = new RecordingDownloadHandler
        {
            Respond = (request, _) => Task.FromResult(Redirect(request, location))
        };
        using var transport = new HttpsArtifactDownloadTransport(handler);
        var error = await Assert.ThrowsAsync<ArtifactAcquisitionException>(async () =>
            await transport.SendAsync(Request(Source(), 0, null, null), CancellationToken.None));
        Assert.Equal(ArtifactAcquisitionFailure.RedirectRejected, error.Failure);
        Assert.Equal(1, handler.Calls);
        Assert.DoesNotContain("PRIVATE-CANARY", error.ToString());
    }

    [Theory]
    [InlineData(301)]
    [InlineData(303)]
    [InlineData(307)]
    [InlineData(308)]
    public async Task Other_redirect_statuses_are_not_followed(int status)
    {
        var handler = new RecordingDownloadHandler
        {
            Respond = (request, _) =>
            {
                var response = Redirect(request);
                response.StatusCode = (HttpStatusCode)status;
                return Task.FromResult(response);
            }
        };
        using var transport = new HttpsArtifactDownloadTransport(handler);
        var error = await Assert.ThrowsAsync<ArtifactAcquisitionException>(async () =>
            await transport.SendAsync(Request(Source(), 0, null, null), CancellationToken.None));
        Assert.Equal(ArtifactAcquisitionFailure.RedirectRejected, error.Failure);
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("oversize")]
    [InlineData("second-redirect")]
    [InlineData("changed-effective-uri")]
    public async Task Invalid_chain_or_location_is_rejected(string scenario)
    {
        var calls = 0;
        var handler = new RecordingDownloadHandler
        {
            Respond = (request, _) =>
            {
                calls++;
                var response = Redirect(request);
                if (scenario == "duplicate")
                    response.Headers.TryAddWithoutValidation("Location", SignedLocation);
                if (scenario == "oversize")
                {
                    response.Headers.Remove("Location");
                    response.Headers.TryAddWithoutValidation("Location", SignedLocation + new string('a', 8192));
                }
                if (scenario == "changed-effective-uri")
                    response.RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://evil.invalid/a");
                return Task.FromResult(response);
            }
        };
        using var transport = new HttpsArtifactDownloadTransport(handler);
        var error = await Assert.ThrowsAsync<ArtifactAcquisitionException>(async () =>
            await transport.SendAsync(Request(Source(), 0, null, null), CancellationToken.None));
        Assert.Equal(ArtifactAcquisitionFailure.RedirectRejected, error.Failure);
        Assert.Equal(scenario == "second-redirect" ? 2 : 1, calls);
    }

    [Fact]
    public async Task Hugging_face_is_refused_before_handler_or_dns()
    {
        var source = AcquisitionTestData.Manifest([1, 2, 3]).DescribeArtifact("f5-v1-weights").Source;
        var handler = new RecordingDownloadHandler();
        using var transport = new HttpsArtifactDownloadTransport(handler);
        var error = await Assert.ThrowsAsync<ArtifactAcquisitionException>(async () =>
            await transport.SendAsync(Request(source, 0, null, null), CancellationToken.None));
        Assert.Equal(ArtifactAcquisitionFailure.ProviderUnsupported, error.Failure);
        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("W/\"weak\"")]
    [InlineData("*")]
    [InlineData("\"url?sig=PRIVATE-CANARY\"")]
    public async Task Resume_without_exact_safe_validator_makes_no_request(string? etag)
    {
        var handler = new RecordingDownloadHandler();
        using var transport = new HttpsArtifactDownloadTransport(handler);
        var error = await Assert.ThrowsAsync<ArtifactAcquisitionException>(async () =>
            await transport.SendAsync(Request(Source(), 1, etag, null), CancellationToken.None));
        Assert.Equal(ArtifactAcquisitionFailure.ResumeStateMismatch, error.Failure);
        Assert.Equal(0, handler.Calls);
        Assert.DoesNotContain("PRIVATE-CANARY", error.ToString());
    }

    [Fact]
    public async Task Last_modified_resume_rebuilt_on_both_hops()
    {
        var calls = 0;
        var handler = new RecordingDownloadHandler
        {
            Respond = (request, _) => Task.FromResult(++calls == 1 ? Redirect(request) : Body(request, 1))
        };
        using var transport = new HttpsArtifactDownloadTransport(handler);
        using var response = await transport.SendAsync(
            Request(Source(), 1, null, AcquisitionTestData.LastModified), CancellationToken.None);
        Assert.Equal(2, calls);
        Assert.All(handler.Requests, request =>
            Assert.Equal(AcquisitionTestData.LastModified.ToString("R"), request.IfRange));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Full_resume_response_preserves_validators_for_coordinator_classification(bool changed)
    {
        var handler = new RecordingDownloadHandler
        {
            Respond = (request, _) =>
            {
                var response = Body(request);
                response.Headers.ETag = new EntityTagHeaderValue(
                    changed ? "\"changed-content\"" : "\"fixture-etag-v1\"");
                response.Content.Headers.LastModified = AcquisitionTestData.LastModified.AddDays(changed ? 1 : 0);
                return Task.FromResult(response);
            }
        };
        using var transport = new HttpsArtifactDownloadTransport(handler);
        using var response = await transport.SendAsync(
            Request(Source(), 1, "\"fixture-etag-v1\"", AcquisitionTestData.LastModified),
            CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, response.Response.StatusCode);
        Assert.Equal(changed ? "\"changed-content\"" : "\"fixture-etag-v1\"", response.EntityTag);
        Assert.Equal(AcquisitionTestData.LastModified.AddDays(changed ? 1 : 0), response.LastModifiedUtc);
        Assert.Equal(response.EntityTag, response.Response.Headers.ETag?.ToString());
        Assert.Equal(response.LastModifiedUtc, response.Response.Content.Headers.LastModified);
        Assert.Equal("bytes=1-", Assert.Single(handler.Requests).Range);
    }

    [Fact]
    public async Task Cancellation_between_hops_disposes_redirect_without_sending_again()
    {
        using var cancellation = new CancellationTokenSource();
        var content = new DisposalContent();
        var handler = new RecordingDownloadHandler
        {
            Respond = (request, _) =>
            {
                var response = Redirect(request);
                response.Content = content;
                cancellation.Cancel();
                return Task.FromResult(response);
            }
        };
        using var transport = new HttpsArtifactDownloadTransport(handler);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await transport.SendAsync(Request(Source(), 0, null, null), cancellation.Token));
        Assert.Equal(1, handler.Calls);
        Assert.True(content.Disposed);
        Assert.False(content.Read);
    }

    [Theory]
    [InlineData(ArtifactAcquisitionFailure.PlanStale, 1)]
    [InlineData(ArtifactAcquisitionFailure.PlanStale, 2)]
    [InlineData(ArtifactAcquisitionFailure.SetupJournalChanged, 2)]
    [InlineData(ArtifactAcquisitionFailure.JournalChanged, 2)]
    [InlineData(ArtifactAcquisitionFailure.PartialConflict, 2)]
    public async Task Fresh_guard_runs_before_every_get_and_failure_prevents_next_network_request(
        ArtifactAcquisitionFailure failure, int rejectedCallback)
    {
        var callbacks = 0;
        var content = new DisposalContent();
        var handler = new RecordingDownloadHandler
        {
            Respond = (request, _) =>
            {
                Assert.Equal(1, callbacks);
                var response = Redirect(request);
                response.Content = content;
                return Task.FromResult(response);
            }
        };
        using var transport = new HttpsArtifactDownloadTransport(handler);
        using var cancellation = new CancellationTokenSource();
        var request = new ArtifactDownloadRequest(Source(), 0, null, null, token =>
        {
            Assert.Equal(cancellation.Token, token);
            callbacks++;
            Assert.Equal(callbacks - 1, handler.Calls);
            if (callbacks > 1)
                Assert.True(content.Disposed);
            if (callbacks == rejectedCallback)
            {
                var exception = new ArtifactAcquisitionException(failure);
                exception.Data["untrusted-diagnostic"] = SignedLocation;
                throw exception;
            }
            return ValueTask.CompletedTask;
        });

        var error = await Assert.ThrowsAsync<ArtifactAcquisitionException>(async () =>
            await transport.SendAsync(request, cancellation.Token));

        Assert.Equal(failure, error.Failure);
        Assert.Equal(rejectedCallback, callbacks);
        Assert.Equal(rejectedCallback - 1, handler.Calls);
        Assert.False(content.Read);
        Assert.Empty(error.Data);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("PRIVATE-CANARY", error.ToString());
    }

    [Theory]
    [InlineData("weak-etag")]
    [InlineData("oversized-etag")]
    [InlineData("duplicate-length")]
    [InlineData("encoding")]
    [InlineData("transfer-encoding")]
    [InlineData("large-headers")]
    public async Task Unsafe_response_metadata_is_not_returned(string scenario)
    {
        var handler = new RecordingDownloadHandler
        {
            Respond = (request, _) =>
            {
                var response = Body(request);
                if (scenario is "weak-etag" or "oversized-etag")
                {
                    response.Headers.Remove("ETag");
                    response.Headers.TryAddWithoutValidation("ETag",
                        scenario == "weak-etag" ? "W/\"weak\"" : "\"" + new string('a', 255) + "\"");
                }
                if (scenario == "duplicate-length")
                {
                    response.Content.Headers.ContentLength = 3;
                    response.Content.Headers.TryAddWithoutValidation("Content-Length", "3");
                }
                if (scenario == "encoding")
                    response.Content.Headers.ContentEncoding.Add("gzip");
                if (scenario == "transfer-encoding")
                    response.Headers.TryAddWithoutValidation("Transfer-Encoding", "chunked");
                if (scenario == "large-headers")
                    response.Headers.TryAddWithoutValidation("X-Test", new string('a', 16 * 1024));
                return Task.FromResult(response);
            }
        };
        using var transport = new HttpsArtifactDownloadTransport(handler);
        var error = await Assert.ThrowsAsync<ArtifactAcquisitionException>(async () =>
            await transport.SendAsync(Request(Source(), 0, null, null), CancellationToken.None));
        Assert.Equal(ArtifactAcquisitionFailure.ResponseInvalid, error.Failure);
    }

    [Fact]
    public async Task Terminal_failure_is_redacted_without_retry()
    {
        var calls = 0;
        var handler = new RecordingDownloadHandler
        {
            Respond = (request, _) => ++calls == 1
                ? Task.FromResult(Redirect(request))
                : throw new HttpRequestException(SignedLocation, new IOException(SignedLocation))
        };
        using var transport = new HttpsArtifactDownloadTransport(handler);
        var error = await Assert.ThrowsAsync<ArtifactAcquisitionException>(async () =>
            await transport.SendAsync(Request(Source(), 0, null, null), CancellationToken.None));
        Assert.Equal(2, calls);
        Assert.Equal(ArtifactAcquisitionFailure.TransportFailed, error.Failure);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("PRIVATE-CANARY", error.ToString());
    }

    [Fact]
    public async Task Body_read_failure_does_not_retain_signed_uri_exception()
    {
        var handler = new RecordingDownloadHandler
        {
            Respond = (request, _) =>
            {
                var response = Body(request);
                response.Content = new StreamContent(new FailingBodyStream());
                response.Content.Headers.ContentLength = 3;
                return Task.FromResult(response);
            }
        };
        using var transport = new HttpsArtifactDownloadTransport(handler);
        using var response = await transport.SendAsync(
            Request(Source(), 0, null, null), CancellationToken.None);
        using var stream = await response.Response.Content.ReadAsStreamAsync();
        var error = await Assert.ThrowsAsync<IOException>(async () =>
            _ = await stream.ReadAsync(new byte[3]));
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("PRIVATE-CANARY", error.ToString());
    }

    private sealed class FailingBodyStream : Stream
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
        public override int Read(byte[] buffer, int offset, int count) => throw new IOException(SignedLocation);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException<int>(new IOException(SignedLocation));
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class DisposalContent : HttpContent
    {
        internal bool Disposed { get; private set; }
        internal bool Read { get; private set; }
        protected override bool TryComputeLength(out long length) { length = 0; return true; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            Read = true;
            throw new InvalidOperationException("A redirect body must never be drained.");
        }
        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }
}
