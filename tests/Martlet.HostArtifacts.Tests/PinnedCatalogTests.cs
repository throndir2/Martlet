using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Martlet.ArtifactDoctor;
using Martlet.HostArtifacts;
using static Martlet.HostArtifacts.Tests.SyntheticManifests;

namespace Martlet.HostArtifacts.Tests;

public sealed class PinnedCatalogTests
{
    private static string CatalogPath => Path.Combine(AppContext.BaseDirectory, "host-artifacts.v2.json");

    [Theory]
    [InlineData(null, 16_879_012_039L, 15_476_196_026L, 26, 23)]
    [InlineData("ollama-llm", 3_703_364_316L, 3_703_364_316L, 7, 5)]
    [InlineData("f5-tts", 13_175_647_723L, 11_772_831_710L, 19, 18)]
    public void ActualCatalogInventoriesRemainDisabled(string? role, long total, long compressed, int contents, int blobs)
    {
        var bytes = File.ReadAllBytes(CatalogPath);
        var manifest = ArtifactManifestReader.Read(bytes);
        var report = ArtifactInspector.Inspect(manifest, role, "ubuntu-24.04-x64", "linux/amd64");
        using var json = JsonDocument.Parse(report.ToJson());
        var root = json.RootElement;
        Assert.Equal(2, report.ExitCode);
        Assert.Equal(total, report.KnownPayloadBytes);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), report.DocumentSha256);
        Assert.Equal(report.ToJson(), ArtifactInspector.Inspect(manifest, role, "ubuntu-24.04-x64", "linux/amd64").ToJson());
        Assert.Equal("supplied_upstream_metadata", root.GetProperty("inventory_provenance").GetString());
        foreach (var field in new[] { "execution_eligible", "download_authorized", "payload_verified", "host_qualified", "source_assertions_authenticated" })
            Assert.False(root.GetProperty(field).GetBoolean());
        foreach (var field in new[] { "complete_download_bytes", "expanded_archive_bytes", "peak_disk_bytes", "free_disk_bytes" })
            Assert.Equal(JsonValueKind.Null, root.GetProperty("disk").GetProperty(field).ValueKind);
        var inventory = root.GetProperty("disk").GetProperty("container_content");
        Assert.Equal(compressed, inventory.GetProperty("known_compressed_bytes").GetInt64());
        Assert.Equal(contents, inventory.GetProperty("unique_content_count").GetInt32());
        Assert.Equal(0, inventory.GetProperty("unknown_compressed_count").GetInt32());
        Assert.Equal(0, inventory.GetProperty("incomplete_image_count").GetInt32());
        Assert.Equal(0, inventory.GetProperty("known_expanded_blob_bytes").GetInt64());
        Assert.Equal(0, inventory.GetProperty("known_staging_blob_bytes").GetInt64());
        Assert.Equal(blobs, inventory.GetProperty("unknown_expanded_blob_count").GetInt32());
        Assert.Equal(blobs, inventory.GetProperty("unknown_staging_blob_count").GetInt32());
        foreach (var code in new[] { "candidate.disabled", "runtime.dependencies_unresolved", "runtime.compatibility_unknown",
            "license.review_required", "gpu.fit_unknown", "disk.total_unknown", "image.metadata_unverified" })
            Assert.Contains(code, Codes(root));
        Assert.DoesNotContain("runtime.image_unpinned", Codes(root));
        Assert.All(root.GetProperty("container_images").EnumerateArray(), image =>
        {
            Assert.Equal("declared_match_not_qualified", image.GetProperty("platform_comparison").GetString());
            Assert.Equal("declared_recipe_not_build_attestation", image.GetProperty("source_binding").GetString());
            Assert.Equal("not_established", image.GetProperty("dependency_closure").GetString());
            Assert.Equal("not_reviewed", image.GetProperty("rights_approval").GetString());
            Assert.False(image.GetProperty("payload_verified").GetBoolean());
        });
        if (role != "f5-tts")
        {
            Assert.Contains("artifact.component_missing", Codes(root));
            Assert.Contains("image.index_selection_unverified", Codes(root));
        }
        if (role != "ollama-llm")
        {
            Assert.Contains("artifact.content_pin_missing", Codes(root));
            Assert.Contains("license.noncommercial_review_required", Codes(root));
            Assert.Contains("voice.rights_required", Codes(root));
            Assert.Contains("voice.transcript_required", Codes(root));
        }
        else
        {
            Assert.Empty(root.GetProperty("artifacts").EnumerateArray());
            Assert.DoesNotContain("voice.rights_required", Codes(root));
            Assert.Single(root.GetProperty("licenses").EnumerateArray(), l => l.GetProperty("scope").GetString() == "container_image_components");
        }
    }

    [Theory]
    [InlineData("ollama", 4, 5)]
    [InlineData("f5", 19, 18)]
    public void CatalogExactlyTranscribesCapturedManifestDescriptors(string name, int layers, int uniqueBlobs)
    {
        using var catalog = JsonDocument.Parse(File.ReadAllBytes(CatalogPath));
        var image = catalog.RootElement.GetProperty("container_images").EnumerateArray()
            .Single(i => i.GetProperty("id").GetString() == name + "-image");
        using var manifest = ReadCapturedMetadata(name + "-manifest", image.GetProperty("digest").GetString()!,
            image.GetProperty("manifest_bytes").GetInt64());
        var descriptors = new[] { manifest.RootElement.GetProperty("config") }
            .Concat(manifest.RootElement.GetProperty("layers").EnumerateArray()).ToArray();
        Assert.Equal(layers + 1, descriptors.Length);
        var declared = image.GetProperty("blobs").EnumerateArray().ToArray();
        Assert.Equal(descriptors.Length, declared.Length);
        Assert.Equal(uniqueBlobs, declared.Select(b => b.GetProperty("digest").GetString()).Distinct().Count());
        for (var i = 0; i < descriptors.Length; i++)
        {
            Assert.Equal(descriptors[i].GetProperty("digest").GetString(), declared[i].GetProperty("digest").GetString());
            Assert.Equal(descriptors[i].GetProperty("size").GetInt64(), declared[i].GetProperty("compressed_bytes").GetInt64());
            Assert.Equal(i == 0 ? "configuration" : "layer", declared[i].GetProperty("kind").GetString());
            Assert.Equal(JsonValueKind.Null, declared[i].GetProperty("expanded_bytes").ValueKind);
            Assert.Equal(JsonValueKind.Null, declared[i].GetProperty("staging_bytes").ValueKind);
        }
        if (name == "ollama")
        {
            var index = image.GetProperty("index");
            using var captured = ReadCapturedMetadata("ollama-index", index.GetProperty("digest").GetString()!, index.GetProperty("bytes").GetInt64());
            var selected = captured.RootElement.GetProperty("manifests").EnumerateArray().Single(m =>
                m.GetProperty("platform").GetProperty("architecture").GetString() == "amd64" &&
                m.GetProperty("platform").GetProperty("os").GetString() == "linux");
            Assert.Equal(image.GetProperty("digest").GetString(), selected.GetProperty("digest").GetString());
            Assert.Equal(image.GetProperty("manifest_bytes").GetInt64(), selected.GetProperty("size").GetInt64());
        }
        else
        {
            Assert.Equal(JsonValueKind.Null, image.GetProperty("index").ValueKind);
            Assert.Equal(3, declared.Count(b => b.GetProperty("digest").GetString() ==
                "sha256:4f4fb700ef54461cfa02571ae0db9a0dc1e0cdb5577484a6d75e68dc38e8acc1"));
        }
    }

    [Fact]
    public void ExistingSourceAndModelPinsAreNotSilentlyUpgraded()
    {
        using var original = JsonDocument.Parse(CandidateBytes());
        using var catalog = JsonDocument.Parse(File.ReadAllBytes(CatalogPath));
        var sources = catalog.RootElement.GetProperty("sources").EnumerateArray().ToDictionary(s => s.GetProperty("id").GetString()!);
        foreach (var source in original.RootElement.GetProperty("sources").EnumerateArray())
        {
            var id = source.GetProperty("id").GetString()!;
            if (id == "f5-source")
            {
                Assert.Equal("283252563dbf91be625e0c27926acfaac449186c", sources[id].GetProperty("revision").GetString());
                Assert.NotEqual(source.GetProperty("revision").GetString(), sources[id].GetProperty("revision").GetString());
            }
            else Assert.True(JsonElement.DeepEquals(source, sources[id]));
        }
        var originals = original.RootElement.GetProperty("artifacts").EnumerateArray().ToDictionary(a => a.GetProperty("id").GetString()!);
        foreach (var artifact in catalog.RootElement.GetProperty("artifacts").EnumerateArray())
            Assert.True(JsonElement.DeepEquals(originals[artifact.GetProperty("id").GetString()!], artifact));
        Assert.DoesNotContain(catalog.RootElement.GetProperty("artifacts").EnumerateArray(),
            a => a.GetProperty("kind").GetString() == "llm_weights");
        var runtime = catalog.RootElement.GetProperty("runtimes")[0];
        Assert.Equal("0.34.0", runtime.GetProperty("upstream_version").GetString());
    }

    [Theory]
    [InlineData("ollama-llm", "linux/amd64", 2, 3_703_364_316L)]
    [InlineData("f5-tts", "linux/amd64", 2, 13_175_647_723L)]
    [InlineData("ollama-llm", "linux/arm64", 1, 3_703_364_316L)]
    public async Task ActualCliReportsPinnedInventoryAndPlatformMismatch(string role, string platform, int exit, long total)
    {
        var before = File.ReadAllBytes(CatalogPath);
        using var output = new StringWriter();
        Assert.Equal(exit, await ArtifactDoctorCommand.RunAsync(
            ["inspect", "--manifest", CatalogPath, "--role", role, "--target", "ubuntu-24.04-x64", "--platform", platform, "--json"], output));
        using var json = JsonDocument.Parse(output.ToString());
        Assert.Equal(total, json.RootElement.GetProperty("disk").GetProperty("known_listed_payload_bytes").GetInt64());
        Assert.False(json.RootElement.GetProperty("execution_eligible").GetBoolean());
        Assert.Equal(before, File.ReadAllBytes(CatalogPath));
        Assert.DoesNotContain(CatalogPath, output.ToString());
    }

    private static JsonDocument ReadCapturedMetadata(string name, string digest, long size)
    {
        // Captures have checkout line endings/final newline; registry responses used LF with no final newline.
        var text = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "evidence", name + ".json"))
            .ReplaceLineEndings("\n").TrimEnd('\n');
        var bytes = Encoding.UTF8.GetBytes(text);
        Assert.Equal(size, bytes.LongLength);
        Assert.Equal(digest, "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes)));
        return JsonDocument.Parse(bytes);
    }
}
