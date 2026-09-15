using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Martlet.HostArtifacts;
using static Martlet.HostArtifacts.Tests.SyntheticManifests;

namespace Martlet.HostArtifacts.Tests;

public sealed class InventoryTests
{
    [Theory]
    [InlineData(null, 2_836_353_046L, 5)]
    [InlineData("ollama-llm", 1_433_537_033L, 1)]
    [InlineData("f5-tts", 1_402_816_013L, 4)]
    public void RealCandidateHasExactKnownSubtotalsButNoCompleteDiskOrQualification(string? role, long bytes, int count)
    {
        var report = ArtifactInspector.Inspect(ArtifactManifestReader.Read(CandidateBytes()), role, "ubuntu-24.04-x64");
        Assert.Equal(2, report.ExitCode);
        Assert.Equal(bytes, report.KnownPayloadBytes);
        using var json = JsonDocument.Parse(report.ToJson());
        var root = json.RootElement;
        Assert.Equal(count, root.GetProperty("artifacts").GetArrayLength());
        Assert.Equal(bytes, root.GetProperty("disk").GetProperty("known_listed_payload_bytes").GetInt64());
        foreach (var field in new[] { "complete_download_bytes", "expanded_archive_bytes", "peak_disk_bytes", "free_disk_bytes" })
            Assert.Equal(JsonValueKind.Null, root.GetProperty("disk").GetProperty(field).ValueKind);
        AssertDisabled(root);
        Assert.Contains(bytes.ToString(CultureInfo.InvariantCulture), report.ToHuman());
        Assert.Contains("NOT installation", report.ToHuman());
    }

    [Fact]
    public void PublishedPinsAndGitObjectIdsRemainDifferentEvidence()
    {
        var report = Report(Candidate());
        var artifacts = report.GetProperty("artifacts").EnumerateArray().ToDictionary(a => a.GetProperty("id").GetString()!);
        Assert.Equal("cf95886728959aa09910bb34de5cca1cc5a8f68003b5597197d3f2c2d57c0804",
            artifacts["ollama-linux-amd64"].GetProperty("sha256").GetString());
        Assert.Equal(553800779, artifacts["ollama-linux-amd64"].GetProperty("release").GetProperty("asset_id").GetInt64());
        Assert.Equal("670900fd14e6c458b95da6e9ed317cdb20dbaf7a1c02ac06a05475a9d32b6a38",
            artifacts["f5-v1-weights"].GetProperty("sha256").GetString());
        Assert.Equal("97ec976ad1fd67a33ab2682d29c0ac7df85234fae875aefcc5fb215681a91b2a",
            artifacts["vocos-weights"].GetProperty("sha256").GetString());
        Assert.Equal("cd934390e8f4b3ce98eb319ae618c084d01504b5",
            artifacts["f5-v1-vocabulary"].GetProperty("git_blob_sha1").GetString());
        Assert.Equal("538262138a8b43863802f279909f26ec31c766b3",
            artifacts["vocos-config"].GetProperty("git_blob_sha1").GetString());
        foreach (var id in new[] { "f5-v1-vocabulary", "vocos-config" })
            Assert.Equal("unavailable", artifacts[id].GetProperty("sha256_evidence").GetString());
        Assert.Equal("locally_computed_input_bytes", report.GetProperty("document_hash_evidence").GetString());
        Assert.Contains("artifact.content_pin_missing", Codes(report));
        var revisions = report.GetProperty("sources").EnumerateArray().ToDictionary(s => s.GetProperty("id").GetString()!,
            s => s.GetProperty("revision").GetString());
        Assert.Equal("9c614e9657089213efc6a7421b30630be138a3f5", revisions["f5-source"]);
        Assert.Equal("84e5a410d9cead4de2f847e7c9369a6440bdfaca", revisions["f5-models"]);
        Assert.Equal("0feb3fdd929bcd6649e0e7c5a688cf7dd012ef21", revisions["vocos-models"]);
    }

    [Fact]
    public void ActualF5RequirementsPreserveAllBaseAndBuildDeclarations()
    {
        var report = Report(Candidate(), "f5-tts");
        var dependencies = report.GetProperty("runtimes")[0].GetProperty("dependencies").EnumerateArray().ToArray();
        Assert.Equal(28, dependencies.Count(d => d.GetProperty("ecosystem").GetString() == "python"));
        Assert.Equal(2, dependencies.Count(d => d.GetProperty("ecosystem").GetString() == "python_build"));
        var byName = dependencies.ToDictionary(d => d.GetProperty("name").GetString()!);
        Assert.Equal("not_darwin_and_not_arm64", byName["bitsandbytes"].GetProperty("condition").GetString());
        Assert.Equal("python_at_most310", byName["numpy"].GetProperty("condition").GetString());
        Assert.Equal("6.15.0", byName["gradio"].GetProperty("constraints")[0].GetProperty("version").GetString());
        Assert.Equal("2.0.0", byName["torch"].GetProperty("constraints")[0].GetProperty("version").GetString());
        Assert.Equal("not_established", report.GetProperty("runtime_dependency_closure").GetString());
        Assert.Contains("voice.transcript_required", Codes(report));
        Assert.Contains("voice.rights_required", Codes(report));
        Assert.Contains("license.noncommercial_review_required", Codes(report));
        Assert.DoesNotContain("faster-whisper", byName.Keys);
        var prerequisites = report.GetProperty("prerequisites").EnumerateArray().ToArray();
        Assert.Equal(11, prerequisites.Length);
        Assert.All(prerequisites, p => Assert.Equal("not_observed", p.GetProperty("status").GetString()));
        Assert.Contains(prerequisites, p => p.GetProperty("id").GetString() == "host.container_tools");
        Assert.Contains(prerequisites, p => p.GetProperty("id").GetString() == "gpu.combined_fit");
    }

    [Theory]
    [InlineData(null, 2, "unknown")]
    [InlineData("ubuntu-24.04-x64", 2, "declared_match_not_qualified")]
    [InlineData("windows-11-x64", 1, "declared_mismatch")]
    public void SuppliedTargetIsNotAHostObservation(string? target, int exit, string comparison)
    {
        var root = Report(Fixture(), target: target);
        Assert.Equal(exit, root.GetProperty("exit_code").GetInt32());
        Assert.All(root.GetProperty("roles").EnumerateArray(), r => Assert.Equal(comparison, r.GetProperty("target_comparison").GetString()));
        AssertDisabled(root);
    }

    [Fact]
    public void RemovingWarningsAndRenamingRoleDoesNotRemoveMandatoryGaps()
    {
        var doc = Fixture();
        foreach (var runtime in doc["runtimes"]!.AsArray())
        {
            runtime!["unresolved"] = new JsonArray();
            runtime["dependencies"] = new JsonArray();
        }
        Item(doc, "roles", 1)["id"] = "approved-tts";
        var report = Report(doc, "approved-tts", "ubuntu-24.04-x64");
        foreach (var code in new[] { "runtime.image_unpinned", "runtime.dependencies_unresolved",
            "runtime.compatibility_unknown", "license.review_required", "gpu.fit_unknown",
            "host.prerequisites_unverified", "voice.transcript_required", "voice.rights_required",
            "disk.total_unknown", "artifact.content_pin_missing", "artifact.payload_unverified" })
            Assert.Contains(code, Codes(report));
        AssertDisabled(report);
    }

    [Fact]
    public void ExplicitMissingVocoderIsVisibleRatherThanOmittedOrZeroSized()
    {
        var doc = Fixture();
        doc["artifacts"]!.AsArray().RemoveAt(4);
        Item(doc, "artifacts", 3)["depends_on"] = new JsonArray();
        Item(doc, "roles", 1)["components"]![4]!["artifact_id"] = null;
        var report = Report(doc, "f5-tts");
        Assert.Contains("vocoder_configuration", report.GetProperty("roles")[0].GetProperty("missing_components")
            .EnumerateArray().Select(c => c.GetString()));
        Assert.Contains("artifact.component_missing", Codes(report));
        Assert.Equal(1_402_815_552L, report.GetProperty("disk").GetProperty("known_listed_payload_bytes").GetInt64());
        AssertDisabled(report);
    }

    [Fact]
    public void TransitiveDiamondCountsSharedConfigOnlyOnce()
    {
        var doc = Fixture();
        Item(doc, "artifacts", 1)["depends_on"]!.AsArray().Add("vocos-config");
        var report = Report(doc, "f5-tts");
        Assert.Equal(4, report.GetProperty("roles")[0].GetProperty("artifact_ids").GetArrayLength());
        Assert.Equal(1_402_816_013L, report.GetProperty("disk").GetProperty("known_listed_payload_bytes").GetInt64());
    }

    [Theory]
    [InlineData("ready")]
    [InlineData("execution_eligible")]
    [InlineData("download_authorized")]
    [InlineData("host_qualified")]
    [InlineData("gpu_fit")]
    [InlineData("measured_vram_bytes")]
    [InlineData("rights_approval")]
    public void SuppliedQualificationAndPermissionFieldsCannotEscalate(string field)
    {
        var doc = Fixture();
        doc[field] = true;
        ManifestTests.Rejected(doc);
        doc = Fixture();
        Item(doc, "roles", 1)[field] = true;
        ManifestTests.Rejected(doc);
    }

    [Fact]
    public void SyntheticHashesNeverBecomeDownloadedBytesOrRightsApproval()
    {
        var doc = Fixture();
        foreach (var index in new[] { 2, 4 })
        {
            var artifact = Item(doc, "artifacts", index);
            artifact["sha256"] = Convert.ToHexStringLower(SHA256.HashData([(byte)index]));
            artifact["sha256_evidence"] = "hugging_face_lfs_metadata";
        }
        var report = Report(doc, "f5-tts", "ubuntu-24.04-x64");
        Assert.DoesNotContain("artifact.content_pin_missing", Codes(report));
        Assert.Contains("artifact.payload_unverified", Codes(report));
        Assert.Equal("synthetic_fixture", report.GetProperty("inventory_provenance").GetString());
        AssertDisabled(report);
    }

    [Fact]
    public void SnapshotOwnsItsDataAndSummaryIsDeterministicAcrossCulture()
    {
        var bytes = CandidateBytes();
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var snapshot = ArtifactManifestReader.Read(bytes);
        Array.Fill(bytes, (byte)0);
        var first = ArtifactInspector.Inspect(snapshot, "f5-tts", "ubuntu-24.04-x64");
        var culture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ar-SA");
            var second = ArtifactInspector.Inspect(snapshot, "f5-tts", "ubuntu-24.04-x64");
            Assert.Equal(first.ToJson(), second.ToJson());
            Assert.Equal(first.ToHuman(), second.ToHuman());
            Assert.Equal(hash, second.DocumentSha256);
        }
        finally { CultureInfo.CurrentCulture = culture; }
        Assert.NotEqual(first.ToJson(), ArtifactInspector.Inspect(snapshot, "ollama-llm").ToJson());
        byte[] padded = [.. CandidateBytes(), (byte)' '];
        Assert.NotEqual(snapshot.DocumentSha256, ArtifactManifestReader.Read(padded).DocumentSha256);
    }

    [Fact]
    public void EntireDocumentIsValidatedBeforeRoleFiltering()
    {
        var doc = Fixture();
        Item(doc, "artifacts", 4)["source_url"] = "https://PRIVATE-CANARY.invalid/";
        ManifestTests.Rejected(doc);
        foreach (var id in new[] { "unknown", "F5-TTS", "role/secret", "" })
            Assert.Equal("inspection.invalid_invocation", Assert.Throws<ArtifactManifestException>(() =>
                ArtifactInspector.Inspect(ArtifactManifestReader.Read(CandidateBytes()), id)).DiagnosticCode);
    }

    private static void AssertDisabled(JsonElement root)
    {
        foreach (var name in new[] { "execution_eligible", "download_authorized", "host_qualified",
            "payload_verified", "source_assertions_authenticated" })
            Assert.False(root.GetProperty(name).GetBoolean());
        Assert.Equal("not_measured", root.GetProperty("gpu_fit").GetString());
        Assert.Equal("not_reviewed", root.GetProperty("rights_approval").GetString());
        Assert.Equal("not_performed", root.GetProperty("prerequisite_observations").GetString());
        Assert.False(root.TryGetProperty("ready", out _));
        Assert.Contains("candidate.disabled", Codes(root));
    }
}
