using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Martlet.Core.Contracts;
using Martlet.Core.Installation;
using Martlet.HostArtifacts;

namespace Martlet.Host.Setup.Tests;

internal sealed class ImageAcquisitionHarness : IAsyncDisposable
{
    internal const string TokenCanary = "OCI_TOKEN_CANARY";
    internal const string QueryCanary = "OCI_QUERY_CANARY";
    internal string Root { get; } = Path.Combine(Path.GetTempPath(), "Martlet.ImageAcquisition.Tests", Guid.NewGuid().ToString("N"));
    internal MutableTimeProvider Clock { get; } = new(SetupTestData.Now);
    internal ArtifactManifest Manifest { get; private set; } = null!;
    internal ArtifactAcquisitionSelection Selection { get; private set; } = null!;
    internal SetupPlan SetupPlan { get; private set; } = null!;
    internal SetupPreview SetupPreview { get; private set; } = null!;
    internal AcquisitionReviewFileSystem Review { get; private set; } = null!;
    internal ArtifactRightsAuthorization[] Rights { get; private set; } = [];
    internal Dictionary<string, byte[]> Payloads { get; } = new(StringComparer.Ordinal);
    internal Dictionary<string, string> MediaTypes { get; } = new(StringComparer.Ordinal);
    internal LocalArtifactAcquisitionStorage Storage { get; private set; } = null!;
    internal RecordingDirectoryCommitter Committer { get; } = new();
    internal ImageTlsServer Server { get; private set; } = null!;
    internal bool RedirectBlobs { get; set; } = true;
    internal Func<HttpRequestMessage, HttpResponseMessage?>? OverrideResponse { get; set; }
    internal JsonObject Catalog { get; private set; } = null!;

    internal static async Task<ImageAcquisitionHarness> CreateAsync(string role = "ollama-llm", int layerBytes = 12_345,
        Action<JsonObject, Dictionary<string, byte[]>, Dictionary<string, string>>? mutate = null,
        IArtifactFreeSpaceProbe? freeSpaceProbe = null)
    {
        var result = new ImageAcquisitionHarness();
        try
        {
        Directory.CreateDirectory(result.Root);
        result.Catalog = result.BuildCatalog(layerBytes);
        mutate?.Invoke(result.Catalog, result.Payloads, result.MediaTypes);
        result.Manifest = ArtifactManifestReader.Read(Encoding.UTF8.GetBytes(result.Catalog.ToJsonString()));
        var roles = role == "both" ? new[] { "ollama-llm", "f5-tts" } : [role];
        result.Selection = result.Manifest.DescribeAcquisition(roles, "ubuntu-24.04-x64", "linux/amd64");
        result.SetupPlan = result.BuildPlan(roles);
        result.Review = new AcquisitionReviewFileSystem(Path.Combine(result.Root, "review.json"));
        var reviews = new SetupCoordinator(result.Review, result.Clock);
        var preview = await reviews.PreviewReviewAsync(result.SetupPlan);
        Assert.True((await reviews.RecordReviewAsync(result.SetupPlan,
            preview.Approve(SetupApprovalDecision.Approve, [SetupConsentScope.LocalJournal]))).ReviewRecorded);
        result.SetupPreview = await reviews.PreviewReviewAsync(result.SetupPlan);
        result.Rights = result.Selection.ImageCandidates.Select(image =>
            new ArtifactRightsReview(result.Selection, image.ArtifactId, "inert-image-rights-v1", Hash("rights"u8),
                image.Licenses.Select(license => new ArtifactReviewedLicense(license.Id,
                    "Authored inert fixture image bytes only", Hash("fixture-terms"u8))), result.Clock)
                .Authorize(ArtifactRightsDecision.Approve)).ToArray();
        result.Storage = new LocalArtifactAcquisitionStorage(result.Root, result.Committer, freeSpaceProbe);
        result.Server = new ImageTlsServer(result.Response);
        return result;
        }
        catch
        {
            await result.DisposeAsync();
            throw;
        }
    }

    internal ArtifactAcquisitionCoordinator Coordinator(IArtifactAcquisitionProgressSink? progress = null) =>
        ArtifactAcquisitionCoordinator.CreateForImageFixture(Review, Storage, Server.Handler, Clock, progress);

    internal async Task<ArtifactImageAcquisitionPreview> PreviewAsync(ArtifactAcquisitionCoordinator coordinator) =>
        await coordinator.PreviewImagesAsync(Selection, SetupPlan, SetupPreview, Rights);

    internal async Task<ArtifactImageAcquisitionResult> RunAsync(ArtifactAcquisitionCoordinator coordinator)
    {
        var preview = await PreviewAsync(coordinator);
        Assert.True(preview.CanApprove, preview.Failure?.ToString());
        return await coordinator.RunImagesAsync(preview.Plan,
            preview.Approve(ArtifactAcquisitionDecision.Approve, preview.Plan.RequiredConsentScopes), Server.Token);
    }

    private JsonObject BuildCatalog(int layerBytes)
    {
        var catalog = JsonNode.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "host-artifacts.v2.json")))!.AsObject();
        catalog["provenance"] = "synthetic_fixture";
        var common = Enumerable.Range(0, 32).Select(i => (byte)(i * 3)).ToArray();
        foreach (var node in catalog["container_images"]!.AsArray())
        {
            var image = node!.AsObject();
            var ollama = image["id"]!.GetValue<string>() == "ollama-image";
            var layer = Enumerable.Range(0, layerBytes).Select(i => (byte)((i * 31 + (ollama ? 1 : 2)) % 251)).ToArray();
            // Deliberately opaque inert bytes: no archive reader or decompressor is needed.
            layer[0] = 0x1f; layer[1] = 0x8b;
            var layers = ollama ? new[] { common, layer } : [common, layer, common, common];
            var configuration = JsonSerializer.SerializeToUtf8Bytes(new
            {
                architecture = "amd64", os = "linux",
                rootfs = new { type = "layers", diff_ids = layers.Select(bytes => "sha256:" + Hash(bytes)).ToArray() },
                config = new { Entrypoint = new[] { "NEVER_EXECUTE_FIXTURE" }, Env = new[] { "FIXTURE_ONLY=1" } }
            });
            var configDigest = Store(configuration, "application/octet-stream");
            var layerDescriptors = layers.Select(bytes => new
            {
                mediaType = ollama ? "application/vnd.oci.image.layer.v1.tar+gzip" : "application/vnd.docker.image.rootfs.diff.tar.gzip",
                digest = Store(bytes, "application/octet-stream"),
                size = bytes.Length
            }).ToArray();
            var manifestType = ollama ? HttpsArtifactImageTransport.OciManifest : HttpsArtifactImageTransport.DockerManifest;
            var manifest = JsonSerializer.SerializeToUtf8Bytes(new
            {
                schemaVersion = 2, mediaType = manifestType,
                config = new { mediaType = ollama ? "application/vnd.oci.image.config.v1+json" : "application/vnd.docker.container.image.v1+json",
                    digest = configDigest, size = configuration.Length },
                layers = layerDescriptors
            });
            var digest = Store(manifest, manifestType);
            image["digest"] = digest;
            image["manifest_bytes"] = manifest.Length;
            image["evidence"] = "synthetic_fixture";
            image["evidence_url"] = $"https://{image["registry"]!.GetValue<string>()}/v2/{image["repository"]!.GetValue<string>()}/manifests/{digest}";
            var blobs = new JsonArray { Blob(configDigest, "configuration", configuration.Length) };
            foreach (var descriptor in layerDescriptors) blobs.Add(Blob(descriptor.digest, "layer", descriptor.size));
            image["blobs"] = blobs;
            if (ollama)
            {
                var index = JsonSerializer.SerializeToUtf8Bytes(new
                {
                    schemaVersion = 2, mediaType = HttpsArtifactImageTransport.OciIndex,
                    manifests = new[]
                    {
                        new { mediaType = manifestType, digest, size = manifest.Length, platform = new { os = "linux", architecture = "amd64" } },
                        new { mediaType = manifestType, digest = "sha256:" + new string('e', 64), size = 321, platform = new { os = "linux", architecture = "arm64" } }
                    }
                });
                image["index"] = new JsonObject { ["digest"] = Store(index, HttpsArtifactImageTransport.OciIndex), ["bytes"] = index.Length };
            }
        }
        return catalog;
    }

    private string Store(byte[] bytes, string media)
    {
        var digest = "sha256:" + Hash(bytes);
        Payloads[digest] = bytes;
        MediaTypes[digest] = media;
        return digest;
    }

    internal static JsonObject Blob(string digest, string kind, int size) => new()
    {
        ["digest"] = digest, ["kind"] = kind, ["compressed_bytes"] = size, ["expanded_bytes"] = null, ["staging_bytes"] = null
    };

    internal static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    internal static void ChangeMetadata(JsonObject catalog, Dictionary<string, byte[]> payloads,
        Dictionary<string, string> mediaTypes, string imageId, string part, Action<JsonObject> change)
    {
        var image = catalog["container_images"]!.AsArray().Select(node => node!.AsObject())
            .Single(node => node["id"]!.GetValue<string>() == imageId);
        string StoreChanged(JsonObject value, string previous)
        {
            var bytes = Encoding.UTF8.GetBytes(value.ToJsonString());
            var digest = "sha256:" + Hash(bytes);
            payloads[digest] = bytes;
            mediaTypes[digest] = mediaTypes[previous];
            return digest;
        }
        var oldManifest = image["digest"]!.GetValue<string>();
        var manifest = JsonNode.Parse(payloads[oldManifest])!.AsObject();
        if (part == "configuration")
        {
            var old = manifest["config"]!["digest"]!.GetValue<string>();
            var configuration = JsonNode.Parse(payloads[old])!.AsObject();
            change(configuration);
            var digest = StoreChanged(configuration, old);
            manifest["config"]!["digest"] = digest;
            manifest["config"]!["size"] = payloads[digest].Length;
            var blob = image["blobs"]!.AsArray().Single(node => node!["kind"]!.GetValue<string>() == "configuration")!;
            blob["digest"] = digest;
            blob["compressed_bytes"] = payloads[digest].Length;
        }
        else if (part == "manifest") change(manifest);
        if (part != "index")
        {
            var digest = StoreChanged(manifest, oldManifest);
            image["digest"] = digest;
            image["manifest_bytes"] = payloads[digest].Length;
            image["evidence_url"] = $"https://{image["registry"]!.GetValue<string>()}/v2/{image["repository"]!.GetValue<string>()}/manifests/{digest}";
        }
        if (image["index"] is { } indexRecord)
        {
            var old = indexRecord["digest"]!.GetValue<string>();
            var index = JsonNode.Parse(payloads[old])!.AsObject();
            if (part == "index") change(index);
            else
            {
                var selected = index["manifests"]!.AsArray().Single(node => node!["digest"]!.GetValue<string>() == oldManifest)!;
                selected["digest"] = image["digest"]!.DeepClone();
                selected["size"] = image["manifest_bytes"]!.DeepClone();
            }
            var digest = StoreChanged(index, old);
            indexRecord["digest"] = digest;
            indexRecord["bytes"] = payloads[digest].Length;
        }
    }

    private SetupPlan BuildPlan(string[] roles)
    {
        if (roles.Length == 1)
            return AcquisitionTestData.BoundPlan(Manifest, Clock.UtcNow, roles[0] == "ollama-llm" ? SetupRole.Llm : SetupRole.Tts);
        var host = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var owner = new InstallationOwner(InstallationOwnership.GuidedManaged, Guid.Parse("44444444-4444-4444-4444-444444444444"));
        var fact = new InstallationFact(PlanningState.Unknown, "supplied-not-observed");
        var llm = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var tts = Guid.Parse("33333333-3333-3333-3333-333333333333");
        var llmResource = Guid.Parse("55555555-5555-5555-5555-555555555555");
        var ttsResource = Guid.Parse("66666666-6666-6666-6666-666666666666");
        var request = new InstallationRequest
        {
            ClientHostId = host,
            Features = [InstallationFeature.TypedConversation, InstallationFeature.SpokenReplies],
            Hosts = [new(host, "Unverified fixture", new(100, 100, 100, 100))],
            Resources = [
                new(llmResource, host, "llm-resource", owner, new(null, null, null, null), false, fact, []),
                new(ttsResource, host, "tts-resource", owner, new(null, null, null, null), false, fact, [])],
            Runtimes = [
                new(llm, host, "ollama", "candidate", InstallationRuntimeKind.Compose, owner, fact, [llmResource], []),
                new(tts, host, "f5", "candidate", InstallationRuntimeKind.Compose, owner, fact, [ttsResource], [])],
            Destinations = [
                new(InstallationRole.Llm, llm, "ollama-llm", CapabilitySupport.Supported, fact, []),
                new(InstallationRole.Tts, tts, "f5-tts", CapabilitySupport.Supported, fact, [])]
        };
        return new SetupPlanBuilder(Clock).Build(new SetupConfiguration("image-fixture", [SetupRole.Llm, SetupRole.Tts]),
            request, host, SetupTestData.HostReport(), Manifest);
    }

    private HttpResponseMessage Response(HttpRequestMessage request)
    {
        if (OverrideResponse?.Invoke(request) is { } custom) return custom;
        var uri = request.RequestUri!;
        if (uri.AbsolutePath == "/diagnostic-positive-control") return Bytes([1, 2, 3], "application/octet-stream");
        if (uri.AbsolutePath == "/token")
        {
            Assert.Null(request.Headers.Authorization);
            Assert.Contains("service=", uri.Query);
            Assert.Contains("%3Apull", uri.Query);
            return Bytes(JsonSerializer.SerializeToUtf8Bytes(new { token = TokenCanary }), "application/json");
        }
        var cdn = uri.Host is "production.cloudfront.docker.com" or "pkg-containers.githubusercontent.com";
        var digest = cdn ? "sha256:" + uri.AbsolutePath.Split('/')[^1] : uri.AbsolutePath.Split('/')[^1];
        if (!cdn)
        {
            var source = Selection.ImageCandidates.Select(image => image.Source).First(source => source.Registry == uri.Host);
            if (request.Headers.Authorization?.Parameter != TokenCanary)
            {
                var response = new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new ByteArrayContent([]) };
                response.Headers.TryAddWithoutValidation("WWW-Authenticate",
                    $"Bearer realm=\"{source.TokenRealm}\",service=\"{source.TokenService}\",scope=\"{source.PullScope}\"");
                return response;
            }
            if (RedirectBlobs && uri.AbsolutePath.Contains("/blobs/", StringComparison.Ordinal))
            {
                var redirect = new HttpResponseMessage(HttpStatusCode.TemporaryRedirect) { Content = new ByteArrayContent([]) };
                redirect.Headers.Location = new Uri(source.CdnOrigin + "/blobs/" + digest[7..] + "?sig=" + QueryCanary + "%2f%2B");
                return redirect;
            }
        }
        else Assert.Null(request.Headers.Authorization);
        var offset = request.Headers.Range?.Ranges.Single().From ?? 0;
        var payload = Payloads[digest];
        var result = Bytes(payload[checked((int)offset)..], MediaTypes[digest]);
        result.Headers.TryAddWithoutValidation("Docker-Content-Digest", digest);
        if (offset > 0)
        {
            result.StatusCode = HttpStatusCode.PartialContent;
            result.Content.Headers.ContentRange = new ContentRangeHeaderValue(offset, payload.Length - 1, payload.Length);
        }
        return result;
    }

    internal static HttpResponseMessage Bytes(byte[] bytes, string media) => new(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent(bytes) { Headers = { ContentType = new MediaTypeHeaderValue(media) } }
    };

    public async ValueTask DisposeAsync()
    {
        if (Server is not null) await Server.DisposeAsync();
        if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
    }
}

internal sealed class ImageTlsServer : IAsyncDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(90));
    private readonly X509Certificate2 certificate;
    private readonly Task serving;
    internal CancellationToken Token => cancellation.Token;
    internal ConcurrentQueue<ImageRecordedRequest> Requests { get; } = new();
    internal bool AllowClientDisconnect { get; set; }
    internal bool ChunkedMetadata { get; set; }
    internal bool UnexpectedTrailer { get; set; }

    internal ImageTlsServer(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=Martlet inert OCI fixture", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        certificate = X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.UserKeySet);
        listener.Start();
        serving = ServeAsync(respond);
    }

    internal HttpMessageHandler Handler()
    {
        var handler = HttpsArtifactImageTransport.CreateProductionHandler();
        var endpoint = (IPEndPoint)listener.LocalEndpoint;
        handler.ConnectCallback = async (_, token) =>
        {
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            try { await socket.ConnectAsync(endpoint, token); return new NetworkStream(socket, ownsSocket: true); }
            catch { socket.Dispose(); throw; }
        };
        handler.SslOptions.RemoteCertificateValidationCallback = (_, peer, _, _) =>
            peer is not null && peer.GetCertHashString() == certificate.GetCertHashString();
        return handler;
    }

    private async Task ServeAsync(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                using var client = await listener.AcceptTcpClientAsync(cancellation.Token);
                await using var stream = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);
                try
                {
                    await stream.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = certificate }, cancellation.Token);
                    var buffer = new byte[1];
                    var header = new StringBuilder();
                    while (!header.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
                    {
                        if (header.Length > 16_384 || await stream.ReadAsync(buffer, cancellation.Token) != 1)
                            throw new IOException("Invalid fixture request.");
                        header.Append((char)buffer[0]);
                    }
                    var lines = header.ToString().Split("\r\n");
                    var target = lines[0].Split(' ')[1];
                    var headers = lines.Skip(1).Where(line => line.Contains(':')).Select(line => line.Split(':', 2))
                        .ToDictionary(parts => parts[0], parts => parts[1].Trim(), StringComparer.OrdinalIgnoreCase);
                    using var request = new HttpRequestMessage(HttpMethod.Get, "https://" + headers["Host"] + target);
                    foreach (var pair in headers) request.Headers.TryAddWithoutValidation(pair.Key, pair.Value);
                    Requests.Enqueue(new(headers["Host"], target, headers.GetValueOrDefault("Authorization"),
                        headers.GetValueOrDefault("Range"), header.ToString()));
                    using var response = respond(request);
                    var body = await response.Content.ReadAsByteArrayAsync(cancellation.Token);
                    var chunked = ChunkedMetadata && (request.RequestUri!.AbsolutePath == "/token" ||
                        request.RequestUri.AbsolutePath.Contains("/manifests/", StringComparison.Ordinal));
                    var output = new StringBuilder($"HTTP/1.1 {(int)response.StatusCode} Fixture\r\nConnection: close\r\n");
                    foreach (var field in response.Headers.NonValidated.Concat(response.Content.Headers.NonValidated))
                    {
                        if (chunked && field.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) continue;
                        foreach (var value in field.Value) output.Append(field.Key).Append(": ").Append(value).Append("\r\n");
                    }
                    if (chunked) output.Append("Transfer-Encoding: chunked\r\n");
                    else if (!response.Content.Headers.Contains("Content-Length"))
                        output.Append("Content-Length: ").Append(body.Length).Append("\r\n");
                    output.Append("\r\n");
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(output.ToString()), cancellation.Token);
                    if (chunked)
                    {
                        if (body.Length > 0)
                        {
                            await stream.WriteAsync(Encoding.ASCII.GetBytes(body.Length.ToString("x", System.Globalization.CultureInfo.InvariantCulture) + "\r\n"), cancellation.Token);
                            await stream.WriteAsync(body, cancellation.Token);
                            await stream.WriteAsync("\r\n"u8.ToArray(), cancellation.Token);
                        }
                        await stream.WriteAsync(Encoding.ASCII.GetBytes("0\r\n" +
                            (UnexpectedTrailer ? "X-Untrusted: fixture-trailer\r\n" : "") + "\r\n"), cancellation.Token);
                    }
                    else await stream.WriteAsync(body, cancellation.Token);
                    await stream.FlushAsync(cancellation.Token);
                }
                catch (IOException) when (AllowClientDisconnect || cancellation.IsCancellationRequested) { }
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (SocketException) when (cancellation.IsCancellationRequested) { }
    }

    public async ValueTask DisposeAsync()
    {
        cancellation.Cancel();
        listener.Stop();
        try { await serving; }
        finally { certificate.Dispose(); cancellation.Dispose(); }
    }
}

internal sealed record ImageRecordedRequest(string Host, string Target, string? Authorization, string? Range, string Raw);
