using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Martlet.HostArtifacts;

namespace Martlet.Host.Setup.Tests;

public sealed class ImageAcquisitionTests
{
    [Theory]
    [InlineData("ollama-llm", true)]
    [InlineData("f5-tts", true)]
    [InlineData("both", true)]
    [InlineData("both", false)]
    public async Task Public_typed_path_acquires_exact_selected_layout_through_real_local_TLS(string role, bool redirects)
    {
        using var diagnostics = new AcquisitionTransportDiagnosticsTests.HttpDiagnostics();
        await using var harness = await ImageAcquisitionHarness.CreateAsync(role);
        harness.RedirectBlobs = redirects;
        using var coordinator = harness.Coordinator();
        var reviewBefore = await File.ReadAllBytesAsync(harness.Review.JournalPath);
        var preview = await harness.PreviewAsync(coordinator);
        Assert.Empty(harness.Server.Requests);
        Assert.False(Directory.Exists(preview.Plan.StagingPath));
        Assert.False(Directory.Exists(preview.Plan.DestinationPath));
        Assert.False(preview.Approve().IsApproved);
        var result = await harness.RunAsync(coordinator);
        Assert.Null(result.Failure);
        Assert.Equal(ArtifactAcquisitionRunState.Acquired, result.State);
        Assert.True(result.Published);
        Assert.False(result.ExecutionAuthorized);
        Assert.False(result.HostReady);
        Assert.False(result.RuntimeEnabled);
        Assert.False(result.CompleteRuntimeAcquired);
        Assert.False(result.AllUpstreamPlatformsAcquired);
        Assert.False(result.UncompressedDiffIdsVerified);
        Assert.Equal(harness.Selection.ImageContentInventory.KnownBytes, result.PersistedContentBytes);
        Assert.Equal(reviewBefore, await File.ReadAllBytesAsync(harness.Review.JournalPath));
        Assert.False(Directory.Exists(preview.Plan.StagingPath));
        var root = result.LayoutPath!;
        using var layout = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(root, "oci-layout")));
        Assert.Equal("1.0.0", layout.RootElement.GetProperty("imageLayoutVersion").GetString());
        using var index = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(root, "index.json")));
        var entries = index.RootElement.GetProperty("manifests").EnumerateArray().ToArray();
        Assert.Equal(harness.Selection.ImageCandidates.Length, entries.Length);
        foreach (var image in harness.Selection.ImageCandidates)
        {
            var descriptor = Assert.Single(entries, entry => entry.GetProperty("digest").GetString() == image.Digest);
            Assert.Equal(image.ManifestBytes, descriptor.GetProperty("size").GetInt64());
            Assert.Equal(harness.MediaTypes[image.Digest], descriptor.GetProperty("mediaType").GetString());
            Assert.DoesNotContain(entries, entry => entry.GetProperty("digest").GetString() == image.IndexDigest);
            Assert.Equal(image.Blobs.Where(blob => blob.Kind == "layer").Select(blob => blob.Digest),
                result.Images.Single(receipt => receipt.ImageId == image.ArtifactId).OrderedLayerDigests);
        }
        foreach (var content in harness.Selection.ImageContentInventory.Contents)
        {
            var bytes = await File.ReadAllBytesAsync(Path.Combine(root, "blobs", "sha256", content.Digest[7..]));
            Assert.Equal(harness.Payloads[content.Digest], bytes);
            Assert.Equal(content.Digest[7..], ImageAcquisitionHarness.Hash(bytes));
        }
        Assert.Equal(harness.Selection.ImageContentInventory.Contents.Length,
            Directory.GetFiles(Path.Combine(root, "blobs", "sha256")).Length);
        foreach (var request in harness.Server.Requests)
        {
            if (request.Target.StartsWith("/token", StringComparison.Ordinal) ||
                request.Host is "production.cloudfront.docker.com" or "pkg-containers.githubusercontent.com")
                Assert.Null(request.Authorization);
            foreach (var forbidden in new[] { "Cookie:", "Proxy-Authorization:", "Referer:", "traceparent:", "tracestate:", "baggage:" })
                Assert.DoesNotContain(forbidden, request.Raw, StringComparison.OrdinalIgnoreCase);
        }
        var journalBytes = await File.ReadAllBytesAsync(preview.Plan.JournalPath);
        Assert.Equal(ArtifactImageJournalState.Published, ArtifactImageJournalCodec.Read(journalBytes).State);
        Assert.Throws<ArtifactAcquisitionException>(() => ArtifactAcquisitionJournalCodec.Read(journalBytes));
        Assert.DoesNotContain(ImageAcquisitionHarness.TokenCanary, Encoding.UTF8.GetString(journalBytes));
        Assert.DoesNotContain(ImageAcquisitionHarness.QueryCanary, Encoding.UTF8.GetString(journalBytes));
        Assert.DoesNotContain(ImageAcquisitionHarness.TokenCanary, JsonSerializer.Serialize(result));
        Assert.DoesNotContain(ImageAcquisitionHarness.QueryCanary, JsonSerializer.Serialize(result));
        using (var control = new HttpClient(harness.Server.Handler()))
            Assert.Equal(new byte[] { 1, 2, 3 }, await control.GetByteArrayAsync("https://ghcr.io/diagnostic-positive-control"));
        Assert.Contains(diagnostics.Events, value => value.Contains("diagnostic-positive-control", StringComparison.Ordinal));
        foreach (var secret in new[] { ImageAcquisitionHarness.TokenCanary, ImageAcquisitionHarness.QueryCanary })
            Assert.DoesNotContain(diagnostics.Events.Concat(diagnostics.Diagnostics).Concat(diagnostics.Activities),
                value => value.Contains(secret, StringComparison.Ordinal));
        var requestCount = harness.Server.Requests.Count;
        var second = await harness.RunAsync(coordinator);
        Assert.Equal(ArtifactAcquisitionRunState.AlreadyAcquired, second.State);
        Assert.Equal(requestCount, harness.Server.Requests.Count);
    }

    [Fact]
    public async Task Cancel_then_new_coordinator_resumes_prefix_with_fresh_token_and_exact_range()
    {
        await using var harness = await ImageAcquisitionHarness.CreateAsync(layerBytes: 300_000);
        harness.Server.AllowClientDisconnect = true;
        using var cancellation = new CancellationTokenSource();
        using var first = harness.Coordinator(new CancelingAcquisitionProgress(cancellation, 70_000));
        var preview = await harness.PreviewAsync(first);
        var permit = preview.Approve(ArtifactAcquisitionDecision.Approve, preview.Plan.RequiredConsentScopes);
        var interrupted = await first.RunImagesAsync(preview.Plan, permit, cancellation.Token);
        Assert.Equal(ArtifactAcquisitionRunState.Interrupted, interrupted.State);
        Assert.False(interrupted.Published);
        Assert.Null(interrupted.LayoutPath);
        Assert.False(Directory.Exists(preview.Plan.DestinationPath));
        var journal = ArtifactImageJournalCodec.Read(await File.ReadAllBytesAsync(preview.Plan.JournalPath));
        var partial = Assert.Single(journal.Contents, row => row.State == ArtifactAcquisitionJournalState.Downloading && row.Bytes > 0);
        Assert.Equal(partial.Sha256, ImageAcquisitionHarness.Hash(await File.ReadAllBytesAsync(
            Path.Combine(preview.Plan.StagingPath, "partials", partial.Digest[7..] + ".partial"))));
        using var resumed = harness.Coordinator();
        var result = await harness.RunAsync(resumed);
        Assert.Null(result.Failure);
        Assert.True(result.Published);
        Assert.True(result.Resumed);
        Assert.Contains(harness.Server.Requests, request => request.Range == $"bytes={partial.Bytes}-");
        Assert.Equal(ArtifactAcquisitionFailure.ConsentConsumed,
            (await first.RunImagesAsync(preview.Plan, permit)).Failure);
    }

    [Theory]
    [InlineData("realm")]
    [InlineData("service")]
    [InlineData("scope")]
    [InlineData("duplicate")]
    [InlineData("basic")]
    public async Task Unbound_challenge_never_reaches_token_endpoint(string mutation)
    {
        await using var harness = await ImageAcquisitionHarness.CreateAsync();
        var source = harness.Selection.ImageCandidates[0].Source;
        var challenge = $"Bearer realm=\"{source.TokenRealm}\",service=\"{source.TokenService}\",scope=\"{source.PullScope}\"";
        challenge = mutation switch
        {
            "realm" => challenge.Replace(source.TokenRealm!, "https://unreviewed.example/token", StringComparison.Ordinal),
            "service" => challenge.Replace(source.TokenService!, "another-service", StringComparison.Ordinal),
            "scope" => challenge.Replace(":pull\"", ":pull,push\"", StringComparison.Ordinal),
            "duplicate" => challenge + ",scope=\"" + source.PullScope + "\"",
            _ => "Basic realm=\"personal-login\""
        };
        harness.OverrideResponse = _ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new ByteArrayContent([]) };
            response.Headers.TryAddWithoutValidation("WWW-Authenticate", challenge);
            return response;
        };
        using var coordinator = harness.Coordinator();
        var result = await harness.RunAsync(coordinator);
        Assert.Equal(ArtifactAcquisitionFailure.RegistryAuthenticationRejected, result.Failure);
        Assert.Single(harness.Server.Requests);
        Assert.Null(result.LayoutPath);
    }

    [Theory]
    [InlineData("{\"token\":\"first\",\"access_token\":\"second\"}")]
    [InlineData("{\"token\":\"first\",\"token\":\"second\"}")]
    [InlineData("{\"token\":\"value\",\"refresh_token\":\"secret\"}")]
    [InlineData("{\"token\":\"bad\\r\\nheader\"}")]
    [InlineData("{\"token\":\"value\",\"expires_in\":0}")]
    [InlineData("{}")]
    public async Task Malformed_token_is_sanitized_and_never_used(string tokenJson)
    {
        await using var harness = await ImageAcquisitionHarness.CreateAsync();
        harness.OverrideResponse = request => request.RequestUri!.AbsolutePath == "/token"
            ? ImageAcquisitionHarness.Bytes(Encoding.UTF8.GetBytes(tokenJson), "application/json") : null;
        using var coordinator = harness.Coordinator();
        var result = await harness.RunAsync(coordinator);
        Assert.Equal(ArtifactAcquisitionFailure.RegistryAuthenticationRejected, result.Failure);
        Assert.Equal(2, harness.Server.Requests.Count);
        Assert.Null(result.LayoutPath);
    }

    [Fact]
    public async Task Missing_rights_and_default_no_produce_no_network_or_payload_files()
    {
        await using var harness = await ImageAcquisitionHarness.CreateAsync();
        using var coordinator = harness.Coordinator();
        var denied = await coordinator.PreviewImagesAsync(harness.Selection, harness.SetupPlan, harness.SetupPreview, []);
        Assert.Equal(ArtifactAcquisitionFailure.RightsNotApproved, denied.Failure);
        Assert.False(denied.CanApprove);
        var preview = await harness.PreviewAsync(coordinator);
        var result = await coordinator.RunImagesAsync(preview.Plan, preview.Approve());
        Assert.Equal(ArtifactAcquisitionFailure.ConsentRequired, result.Failure);
        Assert.Empty(harness.Server.Requests);
        Assert.False(Directory.Exists(preview.Plan.StagingPath));
        Assert.False(File.Exists(preview.Plan.JournalPath));
    }

    [Fact]
    public async Task Foreign_layout_is_preserved_without_network()
    {
        await using var harness = await ImageAcquisitionHarness.CreateAsync();
        using var coordinator = harness.Coordinator();
        var preview = await harness.PreviewAsync(coordinator);
        Directory.CreateDirectory(preview.Plan.DestinationPath);
        var foreign = Path.Combine(preview.Plan.DestinationPath, "foreign.txt");
        await File.WriteAllTextAsync(foreign, "must remain");
        var error = await Assert.ThrowsAsync<ArtifactAcquisitionException>(async () => await harness.PreviewAsync(coordinator));
        Assert.Equal(ArtifactAcquisitionFailure.FinalConflict, error.Failure);
        Assert.Equal("must remain", await File.ReadAllTextAsync(foreign));
        Assert.Empty(harness.Server.Requests);
    }

    [Fact]
    public async Task Corrupt_owned_published_batch_requires_reviewed_quarantine_and_full_replacement_budget()
    {
        await using var harness = await ImageAcquisitionHarness.CreateAsync();
        using var coordinator = harness.Coordinator();
        var acquired = await harness.RunAsync(coordinator);
        Assert.True(acquired.Published, acquired.Failure?.ToString());
        var layer = harness.Selection.ImageContentInventory.Contents.First(content => content.Kind == ArtifactImageContentKind.Layer);
        var path = Path.Combine(acquired.LayoutPath!, "blobs", "sha256", layer.Digest[7..]);
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None))
            stream.WriteByte(0xa5);
        var preview = await harness.PreviewAsync(coordinator);
        Assert.Equal(ArtifactAcquisitionPreviewState.RecoveryRequired, preview.State);
        Assert.True(preview.Plan.QuarantineAndReacquire);
        Assert.Equal(preview.Plan.KnownContentBytes, preview.Plan.RemainingContentBytes);
        var result = await coordinator.RunImagesAsync(preview.Plan,
            preview.Approve(ArtifactAcquisitionDecision.Approve, preview.Plan.RequiredConsentScopes));
        Assert.True(result.Published, result.Failure?.ToString());
        Assert.True(Directory.Exists(preview.Plan.QuarantinePath));
        Assert.Equal(0xa5, (await File.ReadAllBytesAsync(Path.Combine(preview.Plan.QuarantinePath, "blobs", "sha256", layer.Digest[7..])))[0]);
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None))
            stream.WriteByte(0xa5);
        var blocked = await harness.PreviewAsync(coordinator);
        Assert.False(blocked.CanApprove);
        Assert.Equal(ArtifactAcquisitionFailure.FinalConflict, blocked.Failure);
    }

    [Fact]
    public async Task Checked_native_lease_rejects_second_live_writer_and_reopens_after_release()
    {
        await using var harness = await ImageAcquisitionHarness.CreateAsync();
        using var coordinator = harness.Coordinator();
        var preview = await harness.PreviewAsync(coordinator);
        using (var lease = await harness.Storage.AcquireImageLeaseAsync(preview.Plan.Paths, preview.Plan.Storage, CancellationToken.None))
        {
            lease.Validate();
            using var second = ArtifactAcquisitionFileIdentity.OpenImageLease(preview.Plan.Paths.Lease, create: false);
            Assert.False(ArtifactAcquisitionFileIdentity.TryLockImageLease(second.SafeFileHandle));
        }
        using var reopened = ArtifactAcquisitionFileIdentity.OpenImageLease(preview.Plan.Paths.Lease, create: false);
        Assert.True(ArtifactAcquisitionFileIdentity.TryLockImageLease(reopened.SafeFileHandle));
        ArtifactAcquisitionFileIdentity.UnlockImageLease(reopened.SafeFileHandle);
        var error = await Assert.ThrowsAsync<ArtifactAcquisitionException>(async () => await harness.PreviewAsync(coordinator));
        Assert.Equal(ArtifactAcquisitionFailure.ImageLeaseUnavailable, error.Failure);
    }
}
