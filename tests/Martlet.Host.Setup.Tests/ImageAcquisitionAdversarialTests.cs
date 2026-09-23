using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Martlet.HostArtifacts;

namespace Martlet.Host.Setup.Tests;

public sealed class ImageAcquisitionAdversarialTests
{
    [Theory]
    [InlineData("index", "platform")]
    [InlineData("index", "ambiguous")]
    [InlineData("index", "nested")]
    [InlineData("index", "urls")]
    [InlineData("manifest", "plugin")]
    [InlineData("manifest", "foreign")]
    [InlineData("manifest", "urls")]
    [InlineData("manifest", "data")]
    [InlineData("manifest", "subject")]
    [InlineData("manifest", "artifact")]
    [InlineData("manifest", "size")]
    [InlineData("manifest", "digest")]
    [InlineData("manifest", "order")]
    [InlineData("manifest", "extra")]
    [InlineData("configuration", "platform")]
    [InlineData("configuration", "rootfs")]
    [InlineData("configuration", "diffids")]
    public async Task Verified_but_disallowed_metadata_never_starts_a_layer_request(string part, string mutation)
    {
        await using var harness = await ImageAcquisitionHarness.CreateAsync(mutate: (catalog, payloads, media) =>
            ImageAcquisitionHarness.ChangeMetadata(catalog, payloads, media, "ollama-image", part, root =>
            {
                switch ((part, mutation))
                {
                    case ("index", "platform"): root["manifests"]![0]!["platform"]!["architecture"] = "arm"; break;
                    case ("index", "ambiguous"): root["manifests"]![1]!["platform"]!["architecture"] = "amd64"; break;
                    case ("index", "nested"): root["manifests"]![0]!["mediaType"] = HttpsArtifactImageTransport.OciIndex; break;
                    case ("index", "urls"): root["manifests"]![0]!["urls"] = new JsonArray("https://unreviewed.example/image"); break;
                    case ("manifest", "plugin"): root["config"]!["mediaType"] = "application/vnd.docker.plugin.v1+json"; break;
                    case ("manifest", "foreign"): root["layers"]![0]!["mediaType"] = "application/vnd.oci.image.layer.nondistributable.v1.tar+gzip"; break;
                    case ("manifest", "urls"): root["layers"]![0]!["urls"] = new JsonArray("https://unreviewed.example/layer"); break;
                    case ("manifest", "data"): root["config"]!["data"] = "e30="; break;
                    case ("manifest", "subject"): root["subject"] = root["config"]!.DeepClone(); break;
                    case ("manifest", "artifact"): root["artifactType"] = "application/vnd.test.plugin"; break;
                    case ("manifest", "size"): root["layers"]![0]!["size"] = 33; break;
                    case ("manifest", "digest"): root["layers"]![0]!["digest"] = "sha256:" + new string('d', 64); break;
                    case ("manifest", "order"):
                        root["layers"] = new JsonArray(root["layers"]![1]!.DeepClone(), root["layers"]![0]!.DeepClone());
                        break;
                    case ("manifest", "extra"): root["layers"]!.AsArray().Add(root["layers"]![0]!.DeepClone()); break;
                    case ("configuration", "platform"): root["architecture"] = "arm64"; break;
                    case ("configuration", "rootfs"): root["rootfs"]!["type"] = "plugin"; break;
                    case ("configuration", "diffids"): root["rootfs"]!["diff_ids"] = new JsonArray("sha256:" + new string('a', 64)); break;
                }
            }));
        using var coordinator = harness.Coordinator();
        var result = await harness.RunAsync(coordinator);
        Assert.False(result.Published);
        Assert.NotNull(result.Failure);
        Assert.Null(result.LayoutPath);
        var layers = harness.Selection.ImageContentInventory.Contents.Where(content => content.Kind == ArtifactImageContentKind.Layer)
            .Select(content => content.Digest).ToArray();
        Assert.DoesNotContain(harness.Server.Requests, request => layers.Any(digest =>
            request.Target.Contains(digest, StringComparison.Ordinal) || request.Target.Contains(digest[7..], StringComparison.Ordinal)));
        if (part == "index")
            Assert.DoesNotContain(harness.Server.Requests, request =>
                request.Target.Contains(harness.Selection.ImageCandidates[0].Digest, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("https://unreviewed.example/blob")]
    [InlineData("http://production.cloudfront.docker.com/blob")]
    [InlineData("https://production.cloudfront.docker.com:444/blob")]
    [InlineData("https://user@production.cloudfront.docker.com/blob")]
    [InlineData("https://production.cloudfront.docker.com/blob#fragment")]
    [InlineData("https://production.cloudfront.docker.com/%2e%2e/blob")]
    [InlineData("https://production.cloudfront.docker.com/../blob")]
    [InlineData("https://production.cloudfront.docker.com//blob")]
    [InlineData("https://pkg-containers.githubusercontent.com/blob")]
    [InlineData("https://production.cloudflare.docker.com/blob")]
    public async Task Unreviewed_blob_redirect_is_refused_before_next_hop(string location)
    {
        await using var harness = await ImageAcquisitionHarness.CreateAsync();
        harness.OverrideResponse = request =>
        {
            if (!request.RequestUri!.AbsolutePath.Contains("/blobs/", StringComparison.Ordinal)) return null;
            var response = new HttpResponseMessage(HttpStatusCode.TemporaryRedirect) { Content = new ByteArrayContent([]) };
            response.Headers.TryAddWithoutValidation("Location", location);
            return response;
        };
        using var coordinator = harness.Coordinator();
        var result = await harness.RunAsync(coordinator);
        Assert.Equal(ArtifactAcquisitionFailure.RedirectRejected, result.Failure);
        Assert.All(harness.Server.Requests, request =>
            Assert.Contains(request.Host, new[] { "registry-1.docker.io", "auth.docker.io" }));
        Assert.Null(result.LayoutPath);
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.0.0.1")]
    [InlineData("169.254.169.254")]
    [InlineData("100.64.0.1")]
    [InlineData("192.0.2.1")]
    [InlineData("::1")]
    [InlineData("::ffff:8.8.8.8")]
    [InlineData("fe80::1")]
    public async Task Mixed_DNS_is_refused_without_a_socket(string badAddress)
    {
        await using var harness = await ImageAcquisitionHarness.CreateAsync();
        var source = harness.Selection.ImageCandidates[0].Source;
        var connections = 0;
        var error = await Assert.ThrowsAsync<ArtifactAcquisitionException>(async () =>
            await HttpsArtifactImageTransport.ConnectAsync(source, new DnsEndPoint(source.Registry, 443), CancellationToken.None,
                (_, _) => Task.FromResult(new[] { IPAddress.Parse("8.8.8.8"), IPAddress.Parse(badAddress) }),
                (_, _, _) => { connections++; return ValueTask.FromResult<Stream>(new MemoryStream()); }));
        Assert.Equal(ArtifactAcquisitionFailure.TransportFailed, error.Failure);
        Assert.Equal(0, connections);
    }

    [Fact]
    public async Task Unknown_connect_host_is_refused_before_DNS_and_vetted_address_is_used_once()
    {
        await using var harness = await ImageAcquisitionHarness.CreateAsync();
        var source = harness.Selection.ImageCandidates[0].Source;
        var resolves = 0;
        var connections = 0;
        Task<IPAddress[]> Resolve(string host, CancellationToken token)
        {
            resolves++;
            return Task.FromResult(new[] { IPAddress.Parse("8.8.8.8") });
        }
        ValueTask<Stream> Connect(IPAddress address, int port, CancellationToken token)
        {
            connections++;
            Assert.Equal(IPAddress.Parse("8.8.8.8"), address);
            Assert.Equal(443, port);
            return ValueTask.FromResult<Stream>(new MemoryStream());
        }
        await Assert.ThrowsAsync<ArtifactAcquisitionException>(async () =>
            await HttpsArtifactImageTransport.ConnectAsync(source, new DnsEndPoint("unreviewed.example", 443),
                CancellationToken.None, Resolve, Connect));
        Assert.Equal(0, resolves);
        using var stream = await HttpsArtifactImageTransport.ConnectAsync(source, new DnsEndPoint(source.Registry, 443),
            CancellationToken.None, Resolve, Connect);
        Assert.Equal(1, resolves);
        Assert.Equal(1, connections);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Review_change_or_expiry_after_challenge_prevents_token_request(bool expire)
    {
        await using var harness = await ImageAcquisitionHarness.CreateAsync();
        harness.OverrideResponse = _ =>
        {
            if (expire) harness.Clock.UtcNow = harness.Clock.UtcNow.AddMinutes(11);
            else harness.Review.AppendWhitespace();
            return null;
        };
        using var coordinator = harness.Coordinator();
        var result = await harness.RunAsync(coordinator);
        Assert.Equal(expire ? ArtifactAcquisitionFailure.PlanStale : ArtifactAcquisitionFailure.SetupJournalChanged, result.Failure);
        Assert.Single(harness.Server.Requests);
        Assert.Null(result.LayoutPath);
    }

    [Fact]
    public async Task Digest_header_disagreement_fails_before_any_content_use()
    {
        await using var harness = await ImageAcquisitionHarness.CreateAsync();
        var image = harness.Selection.ImageCandidates[0];
        harness.OverrideResponse = _ =>
        {
            var response = ImageAcquisitionHarness.Bytes(harness.Payloads[image.IndexDigest!], HttpsArtifactImageTransport.OciIndex);
            response.Headers.TryAddWithoutValidation("Docker-Content-Digest", "sha256:" + new string('0', 64));
            return response;
        };
        using var coordinator = harness.Coordinator();
        var result = await harness.RunAsync(coordinator);
        Assert.Equal(ArtifactAcquisitionFailure.IntegrityMismatch, result.Failure);
        Assert.Single(harness.Server.Requests);
        Assert.Null(result.LayoutPath);
    }

    [Theory]
    [InlineData("{\"a\":1,\"\\u0061\":2}")]
    [InlineData("{\"a\":\"\\uD800\"}")]
    [InlineData("{\"a\":1} trailing")]
    public void Metadata_JSON_rejects_duplicate_decoded_names_and_invalid_text(string text)
    {
        Assert.Throws<ArtifactAcquisitionException>(() => ArtifactImageJson.Parse(Encoding.UTF8.GetBytes(text)));
    }

    [Fact]
    public void Metadata_JSON_rejects_invalid_UTF8_and_excess_depth()
    {
        Assert.Throws<ArtifactAcquisitionException>(() => ArtifactImageJson.Parse(new byte[] { 0xff, 0xfe }));
        Assert.Throws<ArtifactAcquisitionException>(() => ArtifactImageJson.Parse(
            Encoding.UTF8.GetBytes(new string('[', 33) + "0" + new string(']', 33))));
    }
}
