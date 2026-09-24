using static Martlet.HostArtifacts.Tests.SyntheticManifests;

namespace Martlet.HostArtifacts.Tests;

public sealed class AcquisitionCandidateTests
{
    [Fact]
    public void Exact_candidate_binds_source_payload_roles_and_license_claims()
    {
        var manifest = ArtifactManifestReader.Read(CandidateBytes());

        var candidate = manifest.DescribeArtifact("ollama-linux-amd64");

        Assert.Equal(manifest.DocumentSha256, candidate.ManifestSha256);
        Assert.Equal("upstream_metadata", candidate.InventoryProvenance);
        Assert.False(candidate.DirectTransportEligible);
        Assert.Equal("ollama-linux-amd64", candidate.ArtifactId);
        Assert.Equal("runtime_archive", candidate.Kind);
        Assert.Equal("github", candidate.SourceKind);
        Assert.Equal("ollama/ollama", candidate.SourceRepository);
        Assert.Equal(
            "d8ab4b4f0ca24b51d3a46b3bf4f462e58ce66b1f",
            candidate.SourceRevision);
        Assert.Equal("https://github.com", candidate.SourceOrigin);
        Assert.Equal(
            "https://github.com/ollama/ollama/releases/download/v0.34.0/ollama-linux-amd64.tar.zst",
            candidate.SourceUrl);
        Assert.Equal(1_433_537_033L, candidate.ExpectedBytes);
        Assert.Equal(
            "cf95886728959aa09910bb34de5cca1cc5a8f68003b5597197d3f2c2d57c0804",
            candidate.ExpectedSha256);
        Assert.Equal("github_release_metadata", candidate.HashEvidence);
        Assert.Equal(["ollama-llm"], candidate.RoleIds.ToArray());
        var license = Assert.Single(candidate.Licenses);
        Assert.Equal("ollama-bundled-components", license.Id);
        Assert.Equal("runtime_archive_components", license.Scope);
        Assert.Equal("unknown", license.Spdx);
        Assert.Equal("unreviewed", license.Disposition);
        Assert.Null(license.EvidencePath);
        Assert.Null(license.EvidenceUrl);
        Assert.Equal(64, candidate.IdentityFingerprint.Length);
        Assert.Equal(ArtifactAcquisitionProvider.GithubReleaseAsset, candidate.Source.Provider);
        Assert.Equal("api.github.com", candidate.Source.RequestUri.Host);
        Assert.Contains("/releases/assets/", candidate.Source.RequestUri.AbsolutePath);
    }

    [Fact]
    public void Missing_payload_hash_remains_explicit_and_never_promoted()
    {
        var candidate = ArtifactManifestReader.Read(CandidateBytes())
            .DescribeArtifact("f5-v1-vocabulary");

        Assert.Null(candidate.ExpectedSha256);
        Assert.Equal("unavailable", candidate.HashEvidence);
        Assert.Equal("hugging_face", candidate.SourceKind);
        Assert.Equal("https://huggingface.co", candidate.SourceOrigin);
        Assert.Equal(["f5-tts"], candidate.RoleIds.ToArray());
        Assert.Equal("CC-BY-NC-4.0", Assert.Single(candidate.Licenses).Spdx);
        Assert.Equal(ArtifactAcquisitionProvider.Unsupported, candidate.Source.Provider);
    }

    [Fact]
    public void Candidate_identity_changes_with_exact_manifest_bytes()
    {
        var first = ArtifactManifestReader.Read(CandidateBytes())
            .DescribeArtifact("ollama-linux-amd64");
        var synthetic = Read(Fixture())
            .DescribeArtifact("ollama-linux-amd64");
        byte[] padded = [.. CandidateBytes(), (byte)' '];
        var second = ArtifactManifestReader.Read(padded)
            .DescribeArtifact("ollama-linux-amd64");

        Assert.NotEqual(first.ManifestSha256, second.ManifestSha256);
        Assert.NotEqual(first.IdentityFingerprint, second.IdentityFingerprint);
        Assert.Equal(first.SourceUrl, second.SourceUrl);
        Assert.Equal(first.ExpectedSha256, second.ExpectedSha256);
        Assert.Equal("synthetic_fixture", synthetic.InventoryProvenance);
        Assert.False(synthetic.DirectTransportEligible);
        Assert.NotEqual(first.IdentityFingerprint, synthetic.IdentityFingerprint);
    }

    [Theory]
    [InlineData("")]
    [InlineData("OLLAMA-LINUX-AMD64")]
    [InlineData("../ollama-linux-amd64")]
    [InlineData("unknown")]
    public void Invalid_or_unselected_artifact_id_is_sanitized(string artifactId)
    {
        var manifest = ArtifactManifestReader.Read(CandidateBytes());

        var error = Assert.Throws<ArtifactManifestException>(
            () => manifest.DescribeArtifact(artifactId));

        Assert.Equal("inspection.invalid_invocation", error.DiagnosticCode);
        if (artifactId.Length != 0)
            Assert.DoesNotContain(artifactId, error.Message);
    }

    [Fact]
    public void Exact_selection_uses_owner_inventory_and_excludes_optional_roles()
    {
        var manifest = ArtifactManifestReader.Read(CandidateBytes());
        var llm = manifest.DescribeAcquisition(["ollama-llm"], "ubuntu-24.04-x64");
        var both = manifest.DescribeAcquisition(["f5-tts", "ollama-llm"], "ubuntu-24.04-x64");
        Assert.Equal(ArtifactInspector.InspectRoles(manifest, ["ollama-llm"], "ubuntu-24.04-x64").KnownPayloadBytes,
            llm.KnownListedBytes);
        Assert.All(llm.Artifacts, artifact => Assert.Equal(["ollama-llm"], artifact.RoleIds.ToArray()));
        Assert.Equal(ArtifactInspector.InspectRoles(manifest, ["ollama-llm", "f5-tts"], "ubuntu-24.04-x64").KnownPayloadBytes,
            both.KnownListedBytes);
        Assert.Equal(both.Fingerprint,
            manifest.DescribeAcquisition(["ollama-llm", "f5-tts"], "ubuntu-24.04-x64").Fingerprint);
        Assert.NotEqual(llm.Fingerprint, both.Fingerprint);
        Assert.True(manifest.DescribeAcquisition(["ollama-llm"], "wrong-target").DeclaredMismatch);
        Assert.Throws<ArtifactManifestException>(() => manifest.DescribeAcquisition(["missing"]));
        Assert.Throws<ArtifactManifestException>(() => manifest.DescribeAcquisition(["ollama-llm", "ollama-llm"]));
    }

    [Fact]
    public void V2_candidate_projection_preserves_deduplicated_OCI_byte_classes()
    {
        var manifest = ArtifactManifestReader.Read(File.ReadAllBytes(
            Path.Combine(AppContext.BaseDirectory, "fixtures", "container-images.v2.json")));
        var both = manifest.DescribeAcquisition(["ollama-llm", "f5-tts"], "ubuntu-24.04-x64", "linux/amd64");
        var llm = manifest.DescribeAcquisition(["ollama-llm"], "ubuntu-24.04-x64", "linux/amd64");
        var tts = manifest.DescribeAcquisition(["f5-tts"], "ubuntu-24.04-x64", "linux/amd64");
        Assert.Equal(2, both.FormatVersion);
        Assert.Equal(660, both.KnownListedBytes);
        Assert.Equal(390, llm.KnownListedBytes);
        Assert.Equal(450, tts.KnownListedBytes);
        Assert.True(both.KnownImageCompressedBytes < llm.KnownImageCompressedBytes + tts.KnownImageCompressedBytes);
        Assert.Equal(2, both.Images.Length);
        Assert.All(both.Images, image => Assert.Equal("linux/amd64", image.Platform));
        Assert.True(manifest.DescribeAcquisition(["ollama-llm"], "ubuntu-24.04-x64", "linux/arm64").DeclaredMismatch);
        Assert.False(both.DeclaredMismatch);
    }

    [Fact]
    public void Image_candidates_preserve_named_sources_rights_sizes_and_ordered_occurrences()
    {
        var manifest = ArtifactManifestReader.Read(File.ReadAllBytes(
            Path.Combine(AppContext.BaseDirectory, "host-artifacts.v2.json")));
        var selection = manifest.DescribeAcquisition(
            ["ollama-llm", "f5-tts"], "ubuntu-24.04-x64", "linux/amd64");
        Assert.Equal(2, selection.ImageCandidates.Length);
        var ollama = Assert.Single(selection.ImageCandidates, image => image.ArtifactId == "ollama-image");
        var f5 = Assert.Single(selection.ImageCandidates, image => image.ArtifactId == "f5-image");
        Assert.Equal(ArtifactImageAcquisitionProvider.DockerHubPublic, ollama.Source.Provider);
        Assert.Equal("https://auth.docker.io/token", ollama.Source.TokenRealm);
        Assert.Equal("registry.docker.io", ollama.Source.TokenService);
        Assert.Equal("repository:ollama/ollama:pull", ollama.Source.PullScope);
        Assert.Equal("https://production.cloudfront.docker.com", ollama.Source.CdnOrigin);
        Assert.Equal(647, ollama.IndexBytes);
        Assert.Equal(1065, ollama.ManifestBytes);
        Assert.Equal(19_380, ollama.Blobs[0].CompressedBytes);
        Assert.Equal(ArtifactImageAcquisitionProvider.GithubContainerRegistryPublic, f5.Source.Provider);
        Assert.Equal("https://ghcr.io/token", f5.Source.TokenRealm);
        Assert.Equal("ghcr.io", f5.Source.TokenService);
        Assert.Equal("repository:swivid/f5-tts:pull", f5.Source.PullScope);
        Assert.Equal("https://pkg-containers.githubusercontent.com", f5.Source.CdnOrigin);
        Assert.Null(f5.IndexDigest);
        Assert.Null(f5.IndexBytes);
        Assert.Equal(4305, f5.ManifestBytes);
        Assert.Equal(20, f5.Blobs.Length);
        Assert.Equal(3, f5.Blobs.Count(blob =>
            blob.Digest == "sha256:4f4fb700ef54461cfa02571ae0db9a0dc1e0cdb5577484a6d75e68dc38e8acc1"));
        Assert.All(selection.ImageCandidates, image =>
        {
            Assert.Equal(manifest.DocumentSha256, image.ManifestSha256);
            Assert.Equal("linux/amd64", image.Platform);
            Assert.True(image.BlobInventoryComplete);
            Assert.Equal("registry_metadata", image.MetadataEvidence);
            Assert.Single(image.RoleIds);
            Assert.Equal("container_image_components", Assert.Single(image.Licenses).Scope);
            Assert.Equal(64, image.IdentityFingerprint.Length);
            Assert.Null(image.GetType().GetProperty(nameof(image.Digest))!.SetMethod);
        });
        Assert.Equal(selection.KnownImageCompressedBytes, selection.ImageContentInventory.KnownBytes);
        Assert.Equal(15_476_196_026, selection.ImageContentInventory.KnownBytes);
        Assert.Equal(26, selection.ImageContentInventory.Contents.Length);
        Assert.Equal(0, selection.ImageContentInventory.UnknownBytesCount);
        Assert.Single(selection.ImageContentInventory.Contents, content =>
            content.Digest == "sha256:4f4fb700ef54461cfa02571ae0db9a0dc1e0cdb5577484a6d75e68dc38e8acc1");
        Assert.Equal(selection.ImageContentInventory.KnownBytes,
            selection.ImageContentInventory.Contents.Sum(content => content.ExpectedBytes!.Value));
    }

    [Fact]
    public void Image_projection_is_additive_for_v1_and_reuses_shared_v2_inventory()
    {
        var v1 = ArtifactManifestReader.Read(CandidateBytes())
            .DescribeAcquisition(["ollama-llm"], "ubuntu-24.04-x64");
        Assert.Empty(v1.ImageCandidates);
        Assert.Empty(v1.ImageContentInventory.Contents);
        Assert.Equal(0, v1.ImageContentInventory.KnownBytes);
        var manifest = ArtifactManifestReader.Read(File.ReadAllBytes(
            Path.Combine(AppContext.BaseDirectory, "fixtures", "container-images.v2.json")));
        var both = manifest.DescribeAcquisition(["ollama-llm", "f5-tts"], "ubuntu-24.04-x64", "linux/amd64");
        var again = manifest.DescribeAcquisition(["f5-tts", "ollama-llm"], "ubuntu-24.04-x64", "linux/amd64");
        Assert.Equal(both.ImageContentInventory.Fingerprint, again.ImageContentInventory.Fingerprint);
        Assert.Equal(both.KnownImageCompressedBytes, both.ImageContentInventory.KnownBytes);
        Assert.Contains(both.ImageContentInventory.Contents, content => content.ImageIds.Length == 2);
        Assert.All(both.ImageContentInventory.Contents, content =>
        {
            Assert.Equal(content.ImageIds.Order(StringComparer.Ordinal), content.ImageIds);
            Assert.Same(both.ImageCandidates.Single(image => image.ArtifactId == content.ImageIds[0]).Source,
                content.Source);
        });
    }
}
