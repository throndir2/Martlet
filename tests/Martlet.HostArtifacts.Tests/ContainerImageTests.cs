using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Martlet.ArtifactDoctor;
using Martlet.HostArtifacts;
using static Martlet.HostArtifacts.Tests.SyntheticManifests;

namespace Martlet.HostArtifacts.Tests;

public sealed class ContainerImageTests
{
    private static string FixturePath => Path.Combine(AppContext.BaseDirectory, "fixtures", "container-images.v2.json");
    private static JsonObject Images() => JsonNode.Parse(File.ReadAllBytes(FixturePath))!.AsObject();
    private static JsonElement Inspect(JsonObject doc, string? role = null, string? platform = "linux/amd64")
    {
        using var result = JsonDocument.Parse(ArtifactInspector.Inspect(Read(doc), role, "ubuntu-24.04-x64", platform).ToJson());
        return result.RootElement.Clone();
    }

    [Fact]
    public void AuthoredGoldenInventoryDeduplicatesLayerAndIndexButNeverQualifies()
    {
        var root = Inspect(Images());
        var expected = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "container-inventory.golden.json")));
        Assert.True(JsonNode.DeepEquals(expected, JsonNode.Parse(root.GetProperty("disk").GetProperty("container_content").GetRawText())));
        Assert.Equal(660, root.GetProperty("disk").GetProperty("known_listed_payload_bytes").GetInt64());
        Assert.Equal(2, root.GetProperty("format_version").GetInt32());
        Assert.Equal(2, root.GetProperty("exit_code").GetInt32());
        Assert.Equal("synthetic_fixture", root.GetProperty("inventory_provenance").GetString());
        foreach (var field in new[] { "execution_eligible", "download_authorized", "payload_verified", "host_qualified", "source_assertions_authenticated" })
            Assert.False(root.GetProperty(field).GetBoolean());
        foreach (var field in new[] { "complete_download_bytes", "expanded_archive_bytes", "peak_disk_bytes", "free_disk_bytes" })
            Assert.Equal(JsonValueKind.Null, root.GetProperty("disk").GetProperty(field).ValueKind);
        foreach (var code in new[] { "candidate.disabled", "artifact.component_missing", "runtime.dependencies_unresolved",
            "runtime.compatibility_unknown", "license.review_required", "gpu.fit_unknown", "disk.total_unknown",
            "image.metadata_unverified", "image.index_selection_unverified" })
            Assert.Contains(code, Codes(root));
        Assert.DoesNotContain("runtime.image_unpinned", Codes(root));
        Assert.Equal(390, Inspect(Images(), "ollama-llm").GetProperty("disk").GetProperty("known_listed_payload_bytes").GetInt64());
        Assert.Equal(450, Inspect(Images(), "f5-tts").GetProperty("disk").GetProperty("known_listed_payload_bytes").GetInt64());
    }

    [Theory]
    [InlineData(null, 2, "unknown")]
    [InlineData("linux/amd64", 2, "declared_match_not_qualified")]
    [InlineData("linux/arm64", 1, "declared_mismatch")]
    [InlineData("linux/amd64/v3", 1, "declared_mismatch")]
    [InlineData("windows/amd64", 1, "declared_mismatch")]
    public void PlatformSelectorComparesMetadataNotTheHost(string? platform, int exit, string comparison)
    {
        var root = Inspect(Images(), platform: platform);
        Assert.Equal(exit, root.GetProperty("exit_code").GetInt32());
        Assert.All(root.GetProperty("container_images").EnumerateArray(),
            image => Assert.Equal(comparison, image.GetProperty("platform_comparison").GetString()));
        Assert.DoesNotContain("target.mismatch", Codes(root));
        Assert.False(root.GetProperty("execution_eligible").GetBoolean());
        if (exit == 1) Assert.Equal("declared_platform_mismatch", root.GetProperty("disposition").GetString());
    }

    [Fact]
    public void SuppliedRegistryMetadataIsNeverAuthenticatedOrLocallyVerified()
    {
        var doc = Images();
        doc["provenance"] = "upstream_metadata";
        foreach (var image in doc["container_images"]!.AsArray()) image!["evidence"] = "registry_metadata";
        var report = Inspect(doc);
        Assert.Equal("supplied_upstream_metadata", report.GetProperty("inventory_provenance").GetString());
        Assert.False(report.GetProperty("source_assertions_authenticated").GetBoolean());
        Assert.All(report.GetProperty("container_images").EnumerateArray(), image =>
        {
            Assert.False(image.GetProperty("payload_verified").GetBoolean());
            Assert.Equal("declared_recipe_not_build_attestation", image.GetProperty("source_binding").GetString());
            Assert.Equal("declared_not_verified", image.GetProperty("index_selection").GetString());
            Assert.Equal("not_established", image.GetProperty("dependency_closure").GetString());
            Assert.Equal("not_reviewed", image.GetProperty("rights_approval").GetString());
        });
    }

    [Theory]
    [InlineData("os", "Linux")]
    [InlineData("architecture", "amd64\n")]
    [InlineData("variant", "v8/extra")]
    [InlineData("os", "")]
    [InlineData("architecture", "a space")]
    public void PlatformDeclarationsAreStrictAndNotNormalized(string field, string value)
    {
        var doc = Images();
        At(doc, "container_images/0/platform")[field] = value;
        ManifestTests.Rejected(doc);
    }

    [Fact]
    public void UnknownSizesPlatformAndClosureRemainExplicit()
    {
        var doc = Images();
        foreach (var image in doc["container_images"]!.AsArray())
        {
            image!["platform"] = null;
            image["manifest_bytes"] = null;
            image["index"]!["bytes"] = null;
            image["blob_inventory_complete"] = false;
            foreach (var blob in image["blobs"]!.AsArray())
                foreach (var field in new[] { "compressed_bytes", "expanded_bytes", "staging_bytes" })
                    blob![field] = null;
        }
        var root = Inspect(doc);
        var disk = root.GetProperty("disk").GetProperty("container_content");
        Assert.Equal(0, disk.GetProperty("known_compressed_bytes").GetInt64());
        Assert.Equal(6, disk.GetProperty("unknown_compressed_count").GetInt32());
        Assert.Equal(3, disk.GetProperty("unknown_expanded_blob_count").GetInt32());
        Assert.Equal(3, disk.GetProperty("unknown_staging_blob_count").GetInt32());
        Assert.Equal(2, disk.GetProperty("incomplete_image_count").GetInt32());
        Assert.Contains("image.platform_unknown", Codes(root));
        Assert.Contains("image.inventory_incomplete", Codes(root));
        var metadata = root.GetProperty("container_images")[0].GetProperty("metadata");
        Assert.Equal(JsonValueKind.Null, metadata.GetProperty("platform").ValueKind);
        Assert.Equal(JsonValueKind.Null, metadata.GetProperty("blobs")[0].GetProperty("expanded_bytes").ValueKind);
        Assert.Equal(2, root.GetProperty("exit_code").GetInt32());
    }

    [Theory]
    [InlineData("registry", "https://ghcr.io")]
    [InlineData("registry", "ghcr.io:443")]
    [InlineData("registry", "localhost")]
    [InlineData("registry", "127.0.0.1")]
    [InlineData("registry", "GHCR.io")]
    [InlineData("registry", "user@ghcr.io")]
    [InlineData("registry", "ghcr.io/evil")]
    [InlineData("repository", "Owner/image")]
    [InlineData("repository", "owner/image:latest")]
    [InlineData("repository", "owner/image:main")]
    [InlineData("repository", "owner/image@sha256:abcd")]
    [InlineData("repository", "../image")]
    [InlineData("repository", "owner//image")]
    [InlineData("repository", "owner/%2e%2e")]
    [InlineData("digest", "main")]
    [InlineData("digest", "latest")]
    [InlineData("digest", "sha256:abcd")]
    [InlineData("digest", "sha512:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("digest", "sha256:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("digest", "sha256:0000000000000000000000000000000000000000000000000000000000000000")]
    [InlineData("evidence_url", "https://registry.example.invalid/v2/authored/ollama/manifests/latest")]
    [InlineData("evidence", "locally_verified")]
    public void MutableOrMalformedReferencesAndEvidenceAreRejected(string field, string value)
    {
        var doc = Images();
        Item(doc, "container_images", 0)[field] = value;
        ManifestTests.Rejected(doc);
    }

    public static IEnumerable<object[]> ImageFields()
    {
        var doc = Images();
        foreach (var path in new[] { "container_images/0", "container_images/0/index", "container_images/0/platform", "container_images/0/blobs/0" })
            foreach (var field in At(doc, path))
                yield return [path, field.Key];
    }

    [Theory]
    [MemberData(nameof(ImageFields))]
    public void NewPropertiesAreRequiredAndRejectDuplicates(string path, string field)
    {
        var doc = Images();
        At(doc, path).Remove(field);
        ManifestTests.Rejected(doc);
        doc = Images();
        var obj = At(doc, path);
        var original = obj.ToJsonString();
        var duplicate = original.Insert(1, JsonSerializer.Serialize(field) + ":" + (obj[field]?.ToJsonString() ?? "null") + ",");
        var text = doc.ToJsonString().Replace(original, duplicate, StringComparison.Ordinal);
        Assert.Equal("manifest.invalid_json", Assert.Throws<ArtifactManifestException>(() =>
            ArtifactManifestReader.Read(Encoding.UTF8.GetBytes(text))).DiagnosticCode);
    }

    [Theory]
    [InlineData("container_images/0")]
    [InlineData("container_images/0/index")]
    [InlineData("container_images/0/platform")]
    [InlineData("container_images/0/blobs/0")]
    public void EveryNewObjectRejectsAuthorityFields(string path)
    {
        var doc = Images();
        At(doc, path)["execution_eligible"] = true;
        ManifestTests.Rejected(doc);
    }

    [Theory]
    [InlineData("compressed_bytes")]
    [InlineData("expanded_bytes")]
    [InlineData("staging_bytes")]
    [InlineData("kind")]
    public void SharedDigestFactsMustAgreeIncludingUnknowns(string field)
    {
        var doc = Images();
        var blob = At(doc, "container_images/1/blobs/1");
        blob[field] = field == "kind" ? JsonValue.Create("configuration") : JsonValue.Create(101L);
        ManifestTests.Rejected(doc);
        if (field == "kind") return;
        blob[field] = null;
        Assert.Equal("image.facts_conflict", Assert.Throws<ArtifactManifestException>(() => Read(doc)).DiagnosticCode);
    }

    [Fact]
    public void ConflictingIndexManifestAndFileAliasesAreRejected()
    {
        var doc = Images();
        At(doc, "container_images/1/index")["bytes"] = 81;
        ManifestTests.Rejected(doc);
        doc = Images();
        At(doc, "container_images/0/index")["digest"] = Item(doc, "container_images", 0)["digest"]!.DeepClone();
        Assert.Equal("image.facts_conflict", Assert.Throws<ArtifactManifestException>(() => Read(doc)).DiagnosticCode);
        doc = Images();
        var image = Item(doc, "container_images", 0);
        image["digest"] = Item(doc, "container_images", 1)["digest"]!.DeepClone();
        image["evidence_url"] = "https://registry.example.invalid/v2/authored/ollama/manifests/" + image["digest"]!.GetValue<string>();
        Assert.Equal("manifest.alias_invalid", Assert.Throws<ArtifactManifestException>(() => Read(doc)).DiagnosticCode);
        doc = Mixed();
        At(doc, "container_images/0/blobs/1")["digest"] = "sha256:" + Item(doc, "artifacts", 0)["sha256"]!.GetValue<string>();
        Assert.Equal("manifest.alias_invalid", Assert.Throws<ArtifactManifestException>(() => Read(doc)).DiagnosticCode);
    }

    [Fact]
    public void MixedV1FilesAndV2ImagesUseTheSameClosureAndRightsChecks()
    {
        var doc = Mixed();
        var report = Inspect(doc);
        Assert.Equal(1_402_816_403L, report.GetProperty("disk").GetProperty("known_listed_payload_bytes").GetInt64());
        Assert.Contains("runtime.image_unpinned", Codes(report)); // F5 still lacks its runtime.
        At(doc, "container_images/0")["license_ids"] = new JsonArray("ollama-code");
        Assert.Equal("manifest.license_invalid", Assert.Throws<ArtifactManifestException>(() => Read(doc)).DiagnosticCode);
    }

    private static JsonObject Mixed()
    {
        var doc = Fixture();
        doc["format_version"] = 2;
        doc["artifacts"]!.AsArray().RemoveAt(0);
        doc["container_images"] = new JsonArray(Item(Images(), "container_images", 0).DeepClone());
        At(doc, "licenses/1")["scope"] = "container_image_components";
        At(doc, "container_images/0")["license_ids"] = new JsonArray("ollama-bundled-components");
        At(doc, "roles/0")["root_artifact_ids"] = new JsonArray("ollama-image");
        At(doc, "roles/0/components/0")["kind"] = "container_image";
        At(doc, "roles/0/components/0")["artifact_id"] = "ollama-image";
        return doc;
    }

    [Fact]
    public void V1IsNotReinterpretedAndV2RequiresAnExplicitCollection()
    {
        var doc = Fixture();
        doc["container_images"] = null;
        ManifestTests.Rejected(doc);
        doc["container_images"] = new JsonArray();
        ManifestTests.Rejected(doc);
        doc = Images();
        doc.Remove("container_images");
        ManifestTests.Rejected(doc);
        doc["container_images"] = null;
        ManifestTests.Rejected(doc);
        doc = Fixture();
        At(doc, "roles/0/components/0")["kind"] = "container_image";
        ManifestTests.Rejected(doc);
        var report = Report(Candidate());
        Assert.Equal(1, report.GetProperty("format_version").GetInt32());
        Assert.False(report.TryGetProperty("container_images", out _));
        Assert.Equal(2_836_353_046L, report.GetProperty("disk").GetProperty("known_listed_payload_bytes").GetInt64());
    }

    [Fact]
    public void GraphCyclesMissingReferencesAndHiddenInvalidImagesFailBeforeSelection()
    {
        var doc = Images();
        At(doc, "container_images/0")["depends_on"] = new JsonArray("f5-image");
        At(doc, "container_images/1")["depends_on"] = new JsonArray("ollama-image");
        Assert.Equal("manifest.dependency_cycle", Assert.Throws<ArtifactManifestException>(() => Read(doc)).DiagnosticCode);
        At(doc, "container_images/1")["depends_on"] = new JsonArray("missing");
        Assert.Equal("manifest.reference_missing", Assert.Throws<ArtifactManifestException>(() => Read(doc)).DiagnosticCode);
        doc = Images();
        At(doc, "container_images/1")["digest"] = "latest";
        Assert.Throws<ArtifactManifestException>(() => Inspect(doc, "ollama-llm"));
    }

    [Fact]
    public void BlobAndByteBoundsAreEnforcedAndInputLimitRemainsExact()
    {
        foreach (var path in new[] { "container_images/0", "container_images/0/index", "container_images/0/blobs/0" })
        {
            var field = path.EndsWith("/0", StringComparison.Ordinal) && path.Contains("blobs", StringComparison.Ordinal) ? "compressed_bytes" :
                path.EndsWith("index", StringComparison.Ordinal) ? "bytes" : "manifest_bytes";
            foreach (var size in new[] { -1L, 0L, 17_592_186_044_417L, long.MaxValue })
            {
                var doc = Images();
                At(doc, path)[field] = size;
                ManifestTests.Rejected(doc);
            }
        }
        var excessive = Images();
        var blobs = At(excessive, "container_images/0")["blobs"]!.AsArray();
        while (blobs.Count <= 128) blobs.Add(blobs[0]!.DeepClone());
        Assert.Equal("manifest.bounds_invalid", Assert.Throws<ArtifactManifestException>(() => Read(excessive)).DiagnosticCode);
        var padded = Enumerable.Repeat((byte)' ', ArtifactManifestReader.MaximumBytes).ToArray();
        Bytes(Images()).CopyTo(padded, 0);
        Assert.NotNull(ArtifactManifestReader.Read(padded));
        byte[] tooLarge = [.. padded, (byte)' '];
        Assert.Equal("manifest.too_large", Assert.Throws<ArtifactManifestException>(() =>
            ArtifactManifestReader.Read(tooLarge)).DiagnosticCode);
    }

    [Fact]
    public void ExactBlobOccurrenceAndAggregateSizeLimitsAreEnforced()
    {
        var doc = Images();
        for (var i = 0; i < 2; i++)
        {
            var blobs = At(doc, $"container_images/{i}")["blobs"]!.AsArray();
            while (blobs.Count < 128)
            {
                var blob = blobs[1]!.DeepClone();
                blob["digest"] = "sha256:" + (i * 128 + blobs.Count + 1).ToString("x64", CultureInfo.InvariantCulture);
                blobs.Add(blob);
            }
        }
        Assert.NotNull(Read(doc)); // 256 occurrences, including one shared layer.
        var thirdImage = Item(doc, "container_images", 1).DeepClone();
        thirdImage["id"] = "third-image";
        thirdImage["digest"] = "sha256:" + new string('1', 64);
        thirdImage["evidence_url"] = "https://registry.example.invalid/v2/authored/f5/manifests/sha256:" + new string('1', 64);
        thirdImage["blobs"] = new JsonArray(thirdImage["blobs"]![0]!.DeepClone());
        doc["container_images"]!.AsArray().Add(thirdImage);
        Assert.Equal("manifest.bounds_invalid", Assert.Throws<ArtifactManifestException>(() => Read(doc)).DiagnosticCode);

        doc = Images();
        const long maximum = 17_592_186_044_416L;
        foreach (var image in doc["container_images"]!.AsArray())
        {
            image!["manifest_bytes"] = maximum;
            image["index"]!["bytes"] = maximum;
            foreach (var blob in image["blobs"]!.AsArray()) blob!["compressed_bytes"] = null;
        }
        At(doc, "container_images/0/blobs/1")["compressed_bytes"] = maximum;
        At(doc, "container_images/1/blobs/1")["compressed_bytes"] = maximum;
        Assert.Equal(70_368_744_177_664L, Inspect(doc).GetProperty("disk").GetProperty("known_listed_payload_bytes").GetInt64());
        At(doc, "container_images/0/blobs/0")["compressed_bytes"] = 1;
        Assert.Equal("manifest.bounds_invalid", Assert.Throws<ArtifactManifestException>(() => Read(doc)).DiagnosticCode);
    }

    [Fact]
    public void EmptyUnknownBlobInventoryDoesNotClaimZeroDownloadOrAQualifiedRuntime()
    {
        var doc = Images();
        foreach (var image in doc["container_images"]!.AsArray())
        {
            image!["index"] = null;
            image["manifest_bytes"] = null;
            image["blob_inventory_complete"] = false;
            image["blobs"] = new JsonArray();
        }
        var report = Inspect(doc);
        var inventory = report.GetProperty("disk").GetProperty("container_content");
        Assert.Equal(0, inventory.GetProperty("known_compressed_bytes").GetInt64());
        Assert.Equal(2, inventory.GetProperty("unknown_compressed_count").GetInt32());
        Assert.Equal(2, inventory.GetProperty("incomplete_image_count").GetInt32());
        Assert.Equal(JsonValueKind.Null, report.GetProperty("disk").GetProperty("complete_download_bytes").ValueKind);
        Assert.Contains("runtime.dependencies_unresolved", Codes(report));
        Assert.DoesNotContain("image.index_selection_unverified", Codes(report));
    }

    [Fact]
    public void NullCollectionsElementsDuplicatesAndFalseCompletenessAreRejected()
    {
        foreach (var path in new[] { "", "container_images/0" })
        {
            var field = path.Length == 0 ? "container_images" : "blobs";
            var doc = Images();
            At(doc, path)[field] = null;
            ManifestTests.Rejected(doc);
            doc = Images();
            At(doc, path)[field] = new JsonArray((JsonNode?)null);
            ManifestTests.Rejected(doc);
        }
        var bad = Images();
        At(bad, "container_images/0")["blobs"] = new JsonArray();
        ManifestTests.Rejected(bad);
        At(bad, "container_images/0")["blob_inventory_complete"] = false;
        Assert.Contains("image.inventory_incomplete", Codes(Inspect(bad)));
        bad = Images();
        var blobs = At(bad, "container_images/0")["blobs"]!.AsArray();
        blobs.Add(blobs[0]!.DeepClone());
        ManifestTests.Rejected(bad);
        bad = Images();
        bad["provenance"] = "upstream_metadata";
        ManifestTests.Rejected(bad);
    }

    [Fact]
    public void RepeatedLayerOccurrencesPreserveOrderButCountIdenticalContentOnce()
    {
        var doc = Images();
        var blobs = At(doc, "container_images/0")["blobs"]!.AsArray();
        blobs.Insert(1, blobs[1]!.DeepClone());
        blobs.Add(blobs[1]!.DeepClone());
        var report = Inspect(doc);
        Assert.Equal(660, report.GetProperty("disk").GetProperty("known_listed_payload_bytes").GetInt64());
        var image = report.GetProperty("container_images").EnumerateArray()
            .Single(i => i.GetProperty("metadata").GetProperty("id").GetString() == "ollama-image");
        Assert.Equal(blobs.Select(b => b!["digest"]!.GetValue<string>()),
            image.GetProperty("metadata").GetProperty("blobs").EnumerateArray().Select(b => b.GetProperty("digest").GetString()));
        Assert.Equal(JsonValueKind.Null, report.GetProperty("disk").GetProperty("peak_disk_bytes").ValueKind);
    }

    [Theory]
    [InlineData("compressed_bytes", 101L)]
    [InlineData("expanded_bytes", 301L)]
    [InlineData("staging_bytes", 101L)]
    [InlineData("expanded_bytes", null)]
    public void RepeatedLayersCannotContradictAnyFact(string field, long? value)
    {
        var doc = Images();
        var blobs = At(doc, "container_images/0")["blobs"]!.AsArray();
        var repeated = blobs[1]!.DeepClone();
        repeated[field] = value;
        blobs.Add(repeated);
        Assert.Equal("image.facts_conflict", Assert.Throws<ArtifactManifestException>(() => Read(doc)).DiagnosticCode);
    }

    [Fact]
    public void RepeatedLayersStillConsumeOccurrenceBounds()
    {
        var doc = Images();
        var blobs = At(doc, "container_images/0")["blobs"]!.AsArray();
        while (blobs.Count < 128) blobs.Add(blobs[1]!.DeepClone());
        Assert.Equal(660, Inspect(doc).GetProperty("disk").GetProperty("known_listed_payload_bytes").GetInt64());
        blobs.Add(blobs[1]!.DeepClone());
        Assert.Equal("manifest.bounds_invalid", Assert.Throws<ArtifactManifestException>(() => Read(doc)).DiagnosticCode);
    }

    [Fact]
    public void DeterministicAcrossCollectionOrderAndCultureExceptExactInputHash()
    {
        var first = Images();
        var second = Images();
        second["container_images"] = new JsonArray(second["container_images"]!.AsArray().Reverse().Select(i => i!.DeepClone()).ToArray());
        var before = JsonNode.Parse(Inspect(first).GetRawText())!;
        var culture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ar-SA");
            var after = JsonNode.Parse(Inspect(second).GetRawText())!;
            before.AsObject().Remove("document_sha256");
            after.AsObject().Remove("document_sha256");
            Assert.True(JsonNode.DeepEquals(before, after));
            var report = ArtifactInspector.Inspect(Read(first), platform: "linux/amd64");
            Assert.Contains("660 known compressed", report.ToHuman());
            Assert.Contains("not locally verified", report.ToHuman());
        }
        finally { CultureInfo.CurrentCulture = culture; }
    }

    [Theory]
    [InlineData("linux/amd64", 2)]
    [InlineData("linux/arm64", 1)]
    [InlineData("Linux/amd64", 3)]
    [InlineData("linux/amd64/", 3)]
    [InlineData("linux", 3)]
    public async Task RealCliHandlesV2AndPlatformExitConventions(string platform, int exit)
    {
        using var output = new StringWriter();
        Assert.Equal(exit, await ArtifactDoctorCommand.RunAsync(
            ["inspect", "--manifest", FixturePath, "--role", "ollama-llm", "--target", "ubuntu-24.04-x64", "--platform", platform, "--json"], output));
        using var json = JsonDocument.Parse(output.ToString());
        Assert.Equal(exit, json.RootElement.GetProperty("exit_code").GetInt32());
        Assert.False(json.RootElement.GetProperty("execution_eligible").GetBoolean());
        Assert.DoesNotContain(FixturePath, output.ToString());
    }
}
