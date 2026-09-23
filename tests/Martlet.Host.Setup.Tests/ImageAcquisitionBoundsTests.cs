using System.Net;
using System.Text;
using Martlet.HostArtifacts;

namespace Martlet.Host.Setup.Tests;

public sealed class ImageAcquisitionBoundsTests
{
    [Fact]
    public async Task Disclosed_body_ceilings_include_probes_and_bound_reads_before_they_occur()
    {
        await using var harness = await ImageAcquisitionHarness.CreateAsync();
        using var coordinator = harness.Coordinator();
        var plan = (await harness.PreviewAsync(coordinator)).Plan;
        var count = harness.Selection.ImageContentInventory.Contents.Length;
        Assert.Equal(count * 4, plan.MaximumRequests);
        Assert.Equal(count, plan.MaximumTokenExchanges);
        Assert.Equal(count * 65_537L, plan.MaximumControlResponseBytes);
        Assert.Equal(plan.RemainingContentBytes + count, plan.MaximumContentResponseBytes);
        Assert.Equal(plan.MaximumContentResponseBytes + plan.MaximumControlResponseBytes, plan.MaximumResponseBodyBytes);
        var budget = new ArtifactImageRequestBudget(plan);
        using var tokenStream = new ImageCountingStream(new byte[70_000]);
        using var tokenContent = new StreamContent(tokenStream);
        var tokenFailure = await Assert.ThrowsAsync<ArtifactAcquisitionException>(async () =>
            await HttpsArtifactImageTransport.ReadBoundedAsync(tokenContent, 65_536,
                _ => ValueTask.CompletedTask, CancellationToken.None, budget.ControlWindow, budget.ReadControl));
        Assert.Equal(ArtifactAcquisitionFailure.SizeExceeded, tokenFailure.Failure);
        Assert.Equal(65_537, tokenStream.BytesRead);

        using var contentStream = new ImageCountingStream(new byte[checked((int)plan.MaximumContentResponseBytes + 100)]);
        var contentFailure = await Assert.ThrowsAsync<ArtifactAcquisitionException>(async () =>
            await ArtifactContentTransfer.CopyAsync(contentStream, plan.MaximumContentResponseBytes + 100, 0,
                _ => ValueTask.CompletedTask, (_, _) => ValueTask.CompletedTask, CancellationToken.None,
                budget.ContentWindow, budget.ReadContent));
        Assert.Equal(ArtifactAcquisitionFailure.ImageBudgetExceeded, contentFailure.Failure);
        Assert.Equal(plan.MaximumContentResponseBytes, contentStream.BytesRead);
        Assert.Empty(harness.Server.Requests);
    }

    [Fact]
    public async Task Exact_remaining_storage_threshold_is_enforced_without_inferring_expanded_bytes()
    {
        var space = new ImageSpaceProbe();
        await using var harness = await ImageAcquisitionHarness.CreateAsync("both", freeSpaceProbe: space);
        using var coordinator = harness.Coordinator();
        var first = await harness.PreviewAsync(coordinator);
        space.Available = first.Plan.RequiredAvailableBytes - 1;
        var insufficient = await harness.PreviewAsync(coordinator);
        Assert.Equal(ArtifactAcquisitionFailure.FreeSpaceInsufficient, insufficient.Failure);
        space.Available++;
        var exact = await harness.PreviewAsync(coordinator);
        Assert.True(exact.CanApprove);
        Assert.Equal(harness.Selection.KnownImageCompressedBytes, exact.Plan.RemainingContentBytes);
        Assert.Null(exact.Plan.ExpandedBytes);
        Assert.Null(exact.Plan.InstallationPeakBytes);
        Assert.False(exact.Plan.SharedAcrossSelections);
        Assert.Empty(harness.Server.Requests);
    }

    [Theory]
    [InlineData("registry", ArtifactAcquisitionFailure.ProviderUnsupported)]
    [InlineData("repository", ArtifactAcquisitionFailure.ProviderUnsupported)]
    [InlineData("size", ArtifactAcquisitionFailure.ImageInventoryMismatch)]
    [InlineData("incomplete", ArtifactAcquisitionFailure.ImageInventoryMismatch)]
    [InlineData("metadata-bound", ArtifactAcquisitionFailure.ImageMetadataInvalid)]
    public async Task Unsupported_or_incomplete_owner_facts_are_refused_before_network(string mutation, ArtifactAcquisitionFailure expected)
    {
        await using var harness = await ImageAcquisitionHarness.CreateAsync(mutate: (catalog, _, _) =>
        {
            var image = catalog["container_images"]![0]!;
            if (mutation == "registry") image["registry"] = "registry.example.org";
            else if (mutation == "repository") image["repository"] = "other/image";
            else if (mutation == "size") image["manifest_bytes"] = null;
            else if (mutation == "metadata-bound") image["manifest_bytes"] = ArtifactImageMetadata.MaximumBytes + 1;
            else image["blob_inventory_complete"] = false;
            image["evidence_url"] = $"https://{image["registry"]!.GetValue<string>()}/v2/{image["repository"]!.GetValue<string>()}/manifests/{image["digest"]!.GetValue<string>()}";
        });
        using var coordinator = harness.Coordinator();
        var preview = await harness.PreviewAsync(coordinator);
        Assert.Equal(expected, preview.Failure);
        Assert.False(preview.CanApprove);
        Assert.Empty(harness.Server.Requests);
        Assert.False(Directory.Exists(preview.Plan.StagingPath));
    }

    [Theory]
    [InlineData("length", ArtifactAcquisitionFailure.ContentLengthMismatch)]
    [InlineData("encoding", ArtifactAcquisitionFailure.ResponseInvalid)]
    [InlineData("type", ArtifactAcquisitionFailure.ImageMetadataInvalid)]
    [InlineData("hash", ArtifactAcquisitionFailure.IntegrityMismatch)]
    public async Task Wrong_response_shape_or_bytes_never_becomes_a_verified_image(string mutation, ArtifactAcquisitionFailure expected)
    {
        await using var harness = await ImageAcquisitionHarness.CreateAsync();
        harness.Server.AllowClientDisconnect = true;
        var image = harness.Selection.ImageCandidates[0];
        var original = harness.Payloads[image.IndexDigest!];
        harness.OverrideResponse = _ =>
        {
            var bytes = original.ToArray();
            if (mutation == "hash") bytes[0] ^= 1;
            var response = ImageAcquisitionHarness.Bytes(bytes,
                mutation == "type" ? "text/plain" : HttpsArtifactImageTransport.OciIndex);
            if (mutation == "length") response.Content.Headers.ContentLength = bytes.Length + 1;
            if (mutation == "encoding") response.Content.Headers.ContentEncoding.Add("gzip");
            return response;
        };
        using var coordinator = harness.Coordinator();
        var result = await harness.RunAsync(coordinator);
        Assert.Equal(expected, result.Failure);
        Assert.Null(result.LayoutPath);
        Assert.Single(harness.Server.Requests);
    }

    [Fact]
    public async Task Chunked_token_and_manifest_metadata_use_exact_observed_body_bounds()
    {
        await using var harness = await ImageAcquisitionHarness.CreateAsync();
        harness.Server.ChunkedMetadata = true;
        using var coordinator = harness.Coordinator();
        var result = await harness.RunAsync(coordinator);
        Assert.True(result.Published, result.Failure?.ToString());
    }

    [Fact]
    public async Task Unexpected_response_trailer_is_not_accepted_after_stream_completion()
    {
        await using var harness = await ImageAcquisitionHarness.CreateAsync();
        harness.Server.ChunkedMetadata = true;
        harness.Server.UnexpectedTrailer = true;
        using var coordinator = harness.Coordinator();
        var result = await harness.RunAsync(coordinator);
        Assert.False(result.Published);
        Assert.NotNull(result.Failure);
        Assert.Null(result.LayoutPath);
    }

    [Fact]
    public async Task Oversized_token_body_is_refused_without_registry_retry()
    {
        await using var harness = await ImageAcquisitionHarness.CreateAsync();
        harness.Server.AllowClientDisconnect = true;
        harness.OverrideResponse = request => request.RequestUri!.AbsolutePath == "/token"
            ? ImageAcquisitionHarness.Bytes(Encoding.UTF8.GetBytes("{\"token\":\"" + new string('a', 65_537) + "\"}"), "application/json")
            : null;
        using var coordinator = harness.Coordinator();
        var result = await harness.RunAsync(coordinator);
        Assert.Equal(ArtifactAcquisitionFailure.RegistryAuthenticationRejected, result.Failure);
        Assert.Equal(2, harness.Server.Requests.Count);
    }

    [Fact]
    public async Task Second_CDN_redirect_does_not_expand_the_reviewed_hop_policy()
    {
        await using var harness = await ImageAcquisitionHarness.CreateAsync();
        harness.OverrideResponse = request =>
        {
            if (request.RequestUri!.Host != "production.cloudfront.docker.com") return null;
            var response = new HttpResponseMessage(HttpStatusCode.TemporaryRedirect) { Content = new ByteArrayContent([]) };
            response.Headers.Location = new Uri("https://production.cloudfront.docker.com/second-hop");
            return response;
        };
        using var coordinator = harness.Coordinator();
        var result = await harness.RunAsync(coordinator);
        Assert.Equal(ArtifactAcquisitionFailure.RedirectRejected, result.Failure);
        Assert.DoesNotContain(harness.Server.Requests, request => request.Target == "/second-hop");
    }

    [Fact]
    public async Task Shared_blob_is_delivered_once_but_every_layer_occurrence_is_preserved()
    {
        await using var harness = await ImageAcquisitionHarness.CreateAsync("both");
        using var coordinator = harness.Coordinator();
        var result = await harness.RunAsync(coordinator);
        Assert.True(result.Published, result.Failure?.ToString());
        var shared = Assert.Single(harness.Selection.ImageContentInventory.Contents,
            content => content.Kind == ArtifactImageContentKind.Layer && content.ImageIds.Length == 2);
        Assert.Single(harness.Server.Requests, request =>
            request.Host is "production.cloudfront.docker.com" or "pkg-containers.githubusercontent.com" &&
            request.Target.Contains(shared.Digest[7..], StringComparison.Ordinal));
        Assert.Equal(3, result.Images.Single(image => image.ImageId == "f5-image").OrderedLayerDigests.Count(digest => digest == shared.Digest));
    }

    private sealed class ImageSpaceProbe : IArtifactFreeSpaceProbe
    {
        internal long Available { get; set; } = long.MaxValue;
        public long GetAvailableBytes(string absoluteDirectory) => Available;
    }

    private sealed class ImageCountingStream(byte[] bytes) : MemoryStream(bytes, writable: false)
    {
        internal long BytesRead { get; private set; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var count = await base.ReadAsync(buffer, cancellationToken);
            BytesRead += count;
            return count;
        }
    }
}
