using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Martlet.Host.Setup.Tests;

public sealed class AcquisitionTransportDiagnosticsTests
{
    [Fact]
    public async Task Reviewed_coordinator_downloads_two_tls_hops_into_owned_final_file_and_journal()
    {
        using var diagnostics = new HttpDiagnostics();
        await using var harness = await AcquisitionHarness.CreateAsync();
        var setupPath = Path.Combine(harness.Root, "setup-journal-v2.json");
        var setupBefore = await File.ReadAllBytesAsync(setupPath);
        Assert.Equal(SetupPreviewState.ReviewRecorded, harness.SetupPreview.State);
        Assert.False(harness.SetupPlan.ExecutionAuthorized);
        Assert.False(harness.Candidate.DirectTransportEligible);
        await using var server = new LoopbackAssetServer(2, payload: harness.Payload);
        using var handler = HttpsArtifactDownloadTransport.CreateProductionHandler();
        server.RouteTestHandler(handler);
        using var transport = new HttpsArtifactDownloadTransport(handler);
        var progress = new RecordingAcquisitionProgress();
        var coordinator = harness.Coordinator(progress, transport: transport);
        var (preview, approval) = await harness.ApproveAsync(coordinator);

        var result = await coordinator.RunAsync(preview.Plan, approval, server.Cancellation);
        await server.Completion;

        Assert.Equal(ArtifactAcquisitionRunState.Acquired, result.State);
        Assert.Equal(harness.Candidate.ExpectedSha256, result.VerifiedSha256);
        Assert.Equal(harness.Payload, await File.ReadAllBytesAsync(preview.Plan.DestinationPath));
        Assert.False(File.Exists(preview.Plan.PartialPath));
        var journalBytes = await File.ReadAllBytesAsync(preview.Plan.JournalPath);
        var journal = ArtifactAcquisitionJournalCodec.Read(journalBytes);
        Assert.Equal(ArtifactAcquisitionJournalState.Finalized, journal.State);
        Assert.Equal(harness.Payload.LongLength, result.PersistedBytes);
        Assert.Equal(setupBefore, await File.ReadAllBytesAsync(setupPath));
        Assert.Equal(2, server.Requests.Count);
        Assert.Contains("Host: api.github.com", server.Requests[0]);
        Assert.Contains("Host: release-assets.githubusercontent.com", server.Requests[1]);
        Assert.Contains("PRIVATE-CANARY%2f%2B", server.Requests[1]);
        Assert.DoesNotContain("PRIVATE-CANARY", Encoding.UTF8.GetString(journalBytes));
        Assert.DoesNotContain("PRIVATE-CANARY", System.Text.Json.JsonSerializer.Serialize(result));
        Assert.DoesNotContain("PRIVATE-CANARY", System.Text.Json.JsonSerializer.Serialize(progress.Values));
        Assert.DoesNotContain(diagnostics.Events.Concat(diagnostics.Diagnostics).Concat(diagnostics.Activities),
            value => value.Contains("PRIVATE-CANARY", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Reviewed_coordinator_cancels_then_resumes_exact_range_with_fresh_signed_tls_hop()
    {
        using var diagnostics = new HttpDiagnostics();
        await using var harness = await AcquisitionHarness.CreateAsync(payloadBytes: 240_000);
        using var cancellation = new CancellationTokenSource();
        var progress = new CancelingAcquisitionProgress(cancellation, 65_536);
        ArtifactAcquisitionResult interrupted;
        string partialPath;
        await using (var server = new LoopbackAssetServer(
            2, payload: harness.Payload, allowEarlyDisconnect: true))
        {
            using var handler = HttpsArtifactDownloadTransport.CreateProductionHandler();
            server.RouteTestHandler(handler);
            using var transport = new HttpsArtifactDownloadTransport(handler);
            var coordinator = harness.Coordinator(progress, transport: transport);
            var (preview, approval) = await harness.ApproveAsync(coordinator);
            partialPath = preview.Plan.PartialPath;
            interrupted = await coordinator.RunAsync(preview.Plan, approval, cancellation.Token);
            await server.Completion;
            Assert.Equal(2, server.Requests.Count);
        }
        Assert.Equal(ArtifactAcquisitionRunState.Interrupted, interrupted.State);
        Assert.Equal(ArtifactAcquisitionFailure.Canceled, interrupted.Failure);
        Assert.InRange(interrupted.PersistedBytes, 65_536, harness.Payload.LongLength - 1);
        Assert.Equal(harness.Payload[..checked((int)interrupted.PersistedBytes)],
            await File.ReadAllBytesAsync(partialPath));

        const string renewedLocation =
            "https://release-assets.githubusercontent.com/github-production-release-asset/123/fixture?sig=PRIVATE-CANARY-renewed%2B";
        await using var resumedServer = new LoopbackAssetServer(2, payload: harness.Payload,
            offset: interrupted.PersistedBytes, signedLocation: renewedLocation);
        using var resumedHandler = HttpsArtifactDownloadTransport.CreateProductionHandler();
        resumedServer.RouteTestHandler(resumedHandler);
        using var resumedTransport = new HttpsArtifactDownloadTransport(resumedHandler);
        var resumedProgress = new RecordingAcquisitionProgress();
        var resumedCoordinator = harness.Coordinator(resumedProgress, transport: resumedTransport);
        var (resumedPreview, resumedApproval) = await harness.ApproveAsync(resumedCoordinator);
        Assert.Equal(ArtifactAcquisitionPreviewState.ResumeAvailable, resumedPreview.State);
        Assert.Equal(interrupted.PersistedBytes, resumedPreview.Plan.ExistingBytes);

        var completed = await resumedCoordinator.RunAsync(
            resumedPreview.Plan, resumedApproval, resumedServer.Cancellation);
        await resumedServer.Completion;

        Assert.Equal(ArtifactAcquisitionRunState.Acquired, completed.State);
        Assert.True(completed.Resumed);
        Assert.Equal(harness.Candidate.ExpectedSha256, completed.VerifiedSha256);
        Assert.Equal(harness.Payload, await File.ReadAllBytesAsync(resumedPreview.Plan.DestinationPath));
        Assert.False(File.Exists(partialPath));
        var journalBytes = await File.ReadAllBytesAsync(resumedPreview.Plan.JournalPath);
        Assert.Equal(ArtifactAcquisitionJournalState.Finalized, ArtifactAcquisitionJournalCodec.Read(journalBytes).State);
        Assert.Equal(2, resumedServer.Requests.Count);
        Assert.Contains("Host: api.github.com", resumedServer.Requests[0]);
        Assert.Contains("PRIVATE-CANARY-renewed%2B", resumedServer.Requests[1]);
        Assert.All(resumedServer.Requests, request =>
        {
            Assert.Contains($"Range: bytes={interrupted.PersistedBytes}-", request);
            Assert.Contains("If-Range: \"fixture-etag-v1\"", request);
        });
        Assert.DoesNotContain("PRIVATE-CANARY", Encoding.UTF8.GetString(journalBytes));
        Assert.DoesNotContain("PRIVATE-CANARY", System.Text.Json.JsonSerializer.Serialize(completed));
        Assert.DoesNotContain("PRIVATE-CANARY", System.Text.Json.JsonSerializer.Serialize(progress.Values));
        Assert.DoesNotContain("PRIVATE-CANARY", System.Text.Json.JsonSerializer.Serialize(resumedProgress.Values));
        Assert.DoesNotContain(diagnostics.Events.Concat(diagnostics.Diagnostics).Concat(diagnostics.Activities),
            value => value.Contains("PRIVATE-CANARY", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Real_sockets_two_hop_stream_preserves_signature_but_not_standard_diagnostics()
    {
        using var diagnostics = new HttpDiagnostics();
        await using var server = new LoopbackAssetServer(3);
        using var handler = HttpsArtifactDownloadTransport.CreateProductionHandler();
        server.RouteTestHandler(handler);
        using var transport = new HttpsArtifactDownloadTransport(handler);
        using var parent = new Activity("artifact-test-parent").Start();
        using var response = await transport.SendAsync(
            AcquisitionTransportTests.Request(AcquisitionTransportTests.Source(), 0, null, null),
            server.Cancellation);
        Assert.Equal(new byte[] { 1, 2, 3 }, await response.Response.Content.ReadAsByteArrayAsync(server.Cancellation));
        Assert.Equal(HttpsArtifactDownloadTransport.AssetOrigin, response.TerminalOrigin);
        Assert.Null(response.Response.RequestMessage);
        Assert.DoesNotContain("PRIVATE-CANARY", response.Response.ToString());

        // Positive control: the same loopback TLS server and enabled listeners observe a normal client.
        using var controlHandler = HttpsArtifactDownloadTransport.CreateProductionHandler();
        controlHandler.ActivityHeadersPropagator = DistributedContextPropagator.CreateDefaultPropagator();
        server.RouteTestHandler(controlHandler);
        using var control = new HttpClient(controlHandler);
        using var controlResponse = await control.GetAsync(
            "https://api.github.com/diagnostic-positive-control", server.Cancellation);
        Assert.Equal(new byte[] { 1, 2, 3 }, await controlResponse.Content.ReadAsByteArrayAsync(server.Cancellation));
        await server.Completion;

        Assert.Equal(3, server.Requests.Count);
        Assert.Contains("GET /github-production-release-asset/123/fixture?sig=PRIVATE-CANARY%2f%2B&x=1+2 HTTP/1.1",
            server.Requests[1]);
        Assert.Contains("Host: api.github.com", server.Requests[0]);
        Assert.Contains("Host: release-assets.githubusercontent.com", server.Requests[1]);
        foreach (var request in server.Requests.Take(2))
        {
            foreach (var forbidden in new[] { "Authorization:", "Cookie:", "Proxy-Authorization:",
                "Referer:", "traceparent:", "tracestate:", "baggage:" })
                Assert.DoesNotContain(forbidden, request, StringComparison.OrdinalIgnoreCase);
        }
        Assert.Contains(diagnostics.Events, value => value.Contains("diagnostic-positive-control", StringComparison.Ordinal));
        Assert.Contains(diagnostics.Diagnostics, value => value.Contains("diagnostic-positive-control", StringComparison.Ordinal));
        Assert.Contains(diagnostics.Activities, value => value.Contains("diagnostic-positive-control", StringComparison.Ordinal));
        Assert.DoesNotContain(diagnostics.Events, value => value.Contains("PRIVATE-CANARY", StringComparison.Ordinal));
        Assert.DoesNotContain(diagnostics.Diagnostics, value => value.Contains("PRIVATE-CANARY", StringComparison.Ordinal));
        Assert.DoesNotContain(diagnostics.Activities, value => value.Contains("PRIVATE-CANARY", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Signed_terminal_http_error_has_no_query_in_result_or_standard_events()
    {
        using var diagnostics = new HttpDiagnostics();
        await using var server = new LoopbackAssetServer(2, terminalStatus: 403);
        using var handler = HttpsArtifactDownloadTransport.CreateProductionHandler();
        server.RouteTestHandler(handler);
        using var transport = new HttpsArtifactDownloadTransport(handler);
        var error = await Assert.ThrowsAsync<ArtifactAcquisitionException>(async () =>
            await transport.SendAsync(
                AcquisitionTransportTests.Request(AcquisitionTransportTests.Source(), 0, null, null),
                server.Cancellation));
        await server.Completion;
        Assert.Equal(ArtifactAcquisitionFailure.ResponseInvalid, error.Failure);
        Assert.DoesNotContain("PRIVATE-CANARY", error.ToString());
        Assert.Equal(2, server.Requests.Count);
        Assert.DoesNotContain(diagnostics.Events.Concat(diagnostics.Diagnostics).Concat(diagnostics.Activities),
            value => value.Contains("PRIVATE-CANARY", StringComparison.Ordinal));
    }

    private sealed class LoopbackAssetServer : IAsyncDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(30));
        private readonly RSA key = RSA.Create(2048);
        private readonly X509Certificate2 certificate;
        internal List<string> Requests { get; } = [];
        internal Task Completion { get; }
        internal CancellationToken Cancellation => cancellation.Token;

        internal LoopbackAssetServer(int requestCount, int terminalStatus = 200,
            byte[]? payload = null, long offset = 0,
            string signedLocation = AcquisitionTransportTests.SignedLocation,
            bool allowEarlyDisconnect = false)
        {
            var request = new CertificateRequest("CN=Martlet inert loopback fixture", key,
                HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var generated = request.CreateSelfSigned(
                DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
            // Schannel cannot serve an ephemeral key. Import without PersistKeySet so disposal
            // removes its temporary key container; this never installs a certificate in a store.
            certificate = X509CertificateLoader.LoadPkcs12(
                generated.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.UserKeySet);
            listener.Start();
            Completion = ServeAsync(requestCount, terminalStatus, payload ?? [1, 2, 3],
                offset, signedLocation, allowEarlyDisconnect);
        }

        internal void RouteTestHandler(SocketsHttpHandler handler)
        {
            var endpoint = (IPEndPoint)listener.LocalEndpoint;
            handler.ConnectCallback = async (_, token) =>
            {
                var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    await client.ConnectAsync(endpoint, token);
                    return new NetworkStream(client, ownsSocket: true);
                }
                catch
                {
                    client.Dispose();
                    throw;
                }
            };
            // This test-only handler pins one generated certificate; no trust-store writes.
            handler.SslOptions.RemoteCertificateValidationCallback = (_, peer, _, _) =>
                peer is not null && peer.GetCertHashString() == certificate.GetCertHashString();
        }

        private async Task ServeAsync(int count, int terminalStatus, byte[] payload,
            long offset, string signedLocation, bool allowEarlyDisconnect)
        {
            for (var index = 0; index < count; index++)
            {
                using var client = await listener.AcceptTcpClientAsync(Cancellation);
                await using var tls = new SslStream(client.GetStream());
                await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = certificate,
                    ClientCertificateRequired = false
                }, Cancellation);
                var bytes = new List<byte>();
                var one = new byte[1];
                while (bytes.Count < 16 * 1024)
                {
                    if (await tls.ReadAsync(one, Cancellation) == 0)
                        throw new IOException("Fixture request ended before headers.");
                    bytes.Add(one[0]);
                    if (bytes.Count >= 4 && bytes[^4] == 13 && bytes[^3] == 10 &&
                        bytes[^2] == 13 && bytes[^1] == 10)
                        break;
                }
                Assert.True(bytes.Count < 16 * 1024);
                Requests.Add(Encoding.ASCII.GetString(bytes.ToArray()));
                var response = index == 0
                    ? "HTTP/1.1 302 Found\r\nLocation: " + signedLocation +
                      "\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"
                    : $"HTTP/1.1 {(offset > 0 ? 206 : terminalStatus)} Fixture\r\n" +
                      $"Content-Length: {payload.LongLength - offset}\r\n" +
                      (offset > 0 ? $"Content-Range: bytes {offset}-{payload.LongLength - 1}/{payload.LongLength}\r\n" : "") +
                      "ETag: \"fixture-etag-v1\"\r\nConnection: close\r\n\r\n";
                try
                {
                    await tls.WriteAsync(Encoding.ASCII.GetBytes(response), Cancellation);
                    if (index > 0)
                        await tls.WriteAsync(payload.AsMemory(checked((int)offset)), Cancellation);
                    await tls.FlushAsync(Cancellation);
                }
                catch (IOException) when (allowEarlyDisconnect && index > 0)
                {
                    // The cancellation fixture intentionally closes a partially consumed response.
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            await cancellation.CancelAsync();
            listener.Stop();
            try { await Completion; }
            catch (OperationCanceledException) { }
            catch (SocketException) when (cancellation.IsCancellationRequested) { }
            finally
            {
                cancellation.Dispose();
                certificate.Dispose();
                key.Dispose();
            }
        }
    }

    private sealed class HttpDiagnostics : EventListener,
        IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object?>>
    {
        private readonly ConcurrentBag<IDisposable> subscriptions = [];
        private readonly IDisposable allListeners;
        private readonly ActivityListener activities;
        internal ConcurrentQueue<string> Events { get; } = [];
        internal ConcurrentQueue<string> Diagnostics { get; } = [];
        internal ConcurrentQueue<string> Activities { get; } = [];

        internal HttpDiagnostics()
        {
            allListeners = DiagnosticListener.AllListeners.Subscribe(this);
            activities = new ActivityListener
            {
                ShouldListenTo = source => source.Name == "System.Net.Http",
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = activity => Activities.Enqueue(string.Join("|",
                    activity.TagObjects.Select(pair => pair.Key + "=" + pair.Value)))
            };
            ActivitySource.AddActivityListener(activities);
        }

        protected override void OnEventSourceCreated(EventSource eventSource)
        {
            if (eventSource.Name == "System.Net.Http")
                EnableEvents(eventSource, EventLevel.Verbose, EventKeywords.All);
        }

        protected override void OnEventWritten(EventWrittenEventArgs eventData)
        {
            if (eventData.Payload is not null)
                Events?.Enqueue(eventData.EventName + "|" + string.Join("|", eventData.Payload));
        }

        public void OnNext(DiagnosticListener listener)
        {
            if (listener.Name == "HttpHandlerDiagnosticListener")
                subscriptions.Add(listener.Subscribe(this));
        }

        public void OnNext(KeyValuePair<string, object?> value)
        {
            var payload = value.Value;
            var request = payload?.GetType().GetProperty("Request")?.GetValue(payload) as HttpRequestMessage;
            var response = payload?.GetType().GetProperty("Response")?.GetValue(payload) as HttpResponseMessage;
            Diagnostics.Enqueue(value.Key + "|" + request + "|" + response);
        }

        public void OnCompleted() { }
        public void OnError(Exception error) { }

        public override void Dispose()
        {
            allListeners.Dispose();
            activities.Dispose();
            foreach (var subscription in subscriptions)
                subscription.Dispose();
            base.Dispose();
        }
    }
}
