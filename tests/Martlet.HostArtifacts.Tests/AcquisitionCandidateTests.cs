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
}
