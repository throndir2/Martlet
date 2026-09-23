using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Martlet.HostArtifacts.Tests;

public sealed class RoleSelectionTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Explicit_selection_preserves_existing_wire_for_equivalent_inputs(int version)
    {
        var manifest = Read(Catalog(version));
        Assert.Equal(ArtifactInspector.Inspect(manifest, "f5-tts", "ubuntu-24.04-x64").ToJson(),
            ArtifactInspector.InspectRoles(manifest, ["f5-tts"], "ubuntu-24.04-x64").ToJson());
        Assert.Equal(ArtifactInspector.Inspect(manifest, target: "ubuntu-24.04-x64").ToJson(),
            ArtifactInspector.InspectRoles(manifest, ["f5-tts", "ollama-llm"], "ubuntu-24.04-x64").ToJson());
        Assert.False(ArtifactInspector.InspectRoles(manifest, ["f5-tts"]).ExecutionEligible);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Superset_catalog_excludes_unselected_roles_and_counts_shared_artifacts_once(int version)
    {
        var doc = Catalog(version);
        AddSharedTtsRole(doc);
        var manifest = Read(doc);
        var selected = ArtifactInspector.InspectRoles(manifest, ["ollama-llm", "f5-tts"]);
        using var report = JsonDocument.Parse(selected.ToJson());
        Assert.Equal(new[] { "f5-tts", "ollama-llm" }, report.RootElement.GetProperty("roles").EnumerateArray()
            .Select(role => role.GetProperty("id").GetString()));
        Assert.Equal(2, report.RootElement.GetProperty("runtimes").GetArrayLength());
        Assert.Equal(manifest.DocumentSha256, selected.DocumentSha256);

        var shared = ArtifactInspector.InspectRoles(manifest, ["f5-tts", "extra-tts"]);
        var single = ArtifactInspector.Inspect(manifest, "f5-tts");
        Assert.Equal(single.KnownPayloadBytes, shared.KnownPayloadBytes);
        using var sharedJson = JsonDocument.Parse(shared.ToJson());
        using var singleJson = JsonDocument.Parse(single.ToJson());
        Assert.Equal(singleJson.RootElement.GetProperty("disk").GetRawText(),
            sharedJson.RootElement.GetProperty("disk").GetRawText());
        Assert.Equal(singleJson.RootElement.GetProperty("artifacts").GetArrayLength(),
            sharedJson.RootElement.GetProperty("artifacts").GetArrayLength());
        Assert.Equal(shared.ToJson(), ArtifactInspector.InspectRoles(manifest, ["extra-tts", "f5-tts"]).ToJson());
        Assert.False(shared.ExecutionEligible);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Missing_exact_identity_cannot_fall_back_to_all_catalog_roles(int version)
    {
        var doc = Catalog(version);
        doc["roles"]!.AsArray().Single(role => role!["id"]!.GetValue<string>() == "f5-tts")!["id"] = "renamed-tts";
        var manifest = Read(doc);
        Assert.False(ArtifactInspector.Inspect(manifest).ExecutionEligible);
        var error = Assert.Throws<ArtifactManifestException>(() =>
            ArtifactInspector.InspectRoles(manifest, ["ollama-llm", "f5-tts"]));
        Assert.Equal("inspection.invalid_invocation", error.DiagnosticCode);
    }

    [Fact]
    public void Invalid_selections_are_bounded_and_rejected_without_partial_inventory()
    {
        var manifest = Read(Catalog(2));
        foreach (var roles in new string[][]
        {
            [], ["missing"], ["f5-tts", "f5-tts"], ["F5-TTS"], [""], [null!],
            ["a", "b", "c", "d", "e"]
        })
            Assert.Equal("inspection.invalid_invocation",
                Assert.Throws<ArtifactManifestException>(() => ArtifactInspector.InspectRoles(manifest, roles)).DiagnosticCode);
        Assert.Throws<ArgumentNullException>(() => ArtifactInspector.InspectRoles(manifest, null!));
        Assert.Equal("inspection.invalid_invocation", Assert.Throws<ArtifactManifestException>(() =>
            ArtifactInspector.InspectRoles(manifest, TooMany())).DiagnosticCode);
        static IEnumerable<string> TooMany()
        {
            for (var index = 0; index < 5; index++) yield return "role-" + index;
            throw new InvalidOperationException("The selector must not consume more than its bounded prefix.");
        }
    }

    [Fact]
    public void Explicit_selection_preserves_target_and_platform_validation()
    {
        var manifest = Read(Catalog(2));
        Assert.Equal(1, ArtifactInspector.InspectRoles(manifest, ["f5-tts"], "other-target").ExitCode);
        Assert.Equal(ArtifactInspector.Inspect(manifest, "f5-tts", platform: "linux/amd64").ToJson(),
            ArtifactInspector.InspectRoles(manifest, ["f5-tts"], platform: "linux/amd64").ToJson());
        Assert.Equal("inspection.invalid_invocation", Assert.Throws<ArtifactManifestException>(() =>
            ArtifactInspector.InspectRoles(Read(Catalog(1)), ["f5-tts"], platform: "linux/amd64")).DiagnosticCode);
    }

    private static JsonObject Catalog(int version) => JsonNode.Parse(File.ReadAllBytes(
        Path.Combine(AppContext.BaseDirectory, $"host-artifacts.v{version}.json")))!.AsObject();

    private static ArtifactManifest Read(JsonObject doc) =>
        ArtifactManifestReader.Read(Encoding.UTF8.GetBytes(doc.ToJsonString()));

    private static void AddSharedTtsRole(JsonObject doc)
    {
        doc["provenance"] = "synthetic_fixture";
        var role = doc["roles"]!.AsArray().Single(r => r!["id"]!.GetValue<string>() == "f5-tts")!.DeepClone();
        var runtime = doc["runtimes"]!.AsArray().Single(r =>
            r!["id"]!.GetValue<string>() == role["runtime_id"]!.GetValue<string>())!.DeepClone();
        runtime["id"] = "extra-runtime";
        role["id"] = "extra-tts";
        role["runtime_id"] = "extra-runtime";
        doc["runtimes"]!.AsArray().Add(runtime);
        doc["roles"]!.AsArray().Add(role);
    }
}
