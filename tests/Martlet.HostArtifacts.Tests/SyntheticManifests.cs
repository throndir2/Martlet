using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Martlet.HostArtifacts;

namespace Martlet.HostArtifacts.Tests;

internal static class SyntheticManifests
{
    internal static byte[] CandidateBytes() => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "host-artifacts.v1.json"));
    internal static JsonObject Candidate() => JsonNode.Parse(CandidateBytes())!.AsObject();
    internal static JsonObject Fixture()
    {
        var doc = Candidate();
        doc["provenance"] = "synthetic_fixture";
        return doc;
    }

    internal static JsonObject Item(JsonObject doc, string collection, int index) => doc[collection]![index]!.AsObject();
    internal static byte[] Bytes(JsonObject doc) => Encoding.UTF8.GetBytes(doc.ToJsonString());
    internal static ArtifactManifest Read(JsonObject doc) => ArtifactManifestReader.Read(Bytes(doc));
    internal static JsonElement Report(JsonObject doc, string? role = null, string? target = null)
    {
        using var json = JsonDocument.Parse(ArtifactInspector.Inspect(Read(doc), role, target).ToJson());
        return json.RootElement.Clone();
    }

    internal static JsonObject At(JsonObject doc, string path)
    {
        JsonNode result = doc;
        foreach (var segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
            result = int.TryParse(segment, out var index) ? result[index]! : result[segment]!;
        return result.AsObject();
    }

    internal static void ChangeArtifactPath(JsonObject doc, int index, string path)
    {
        var artifact = Item(doc, "artifacts", index);
        artifact["path"] = path;
        var source = doc["sources"]!.AsArray().Single(s => s!["id"]!.GetValue<string>() == artifact["source_id"]!.GetValue<string>())!;
        var repo = source["repository"]!.GetValue<string>();
        var revision = source["revision"]!.GetValue<string>();
        artifact["source_url"] = $"https://huggingface.co/{repo}/resolve/{revision}/{path}";
        var slash = path.LastIndexOf('/');
        artifact["evidence_url"] = $"https://huggingface.co/api/models/{repo}/tree/{revision}" + (slash < 0 ? "" : "/" + path[..slash]);
    }

    internal static string[] Codes(JsonElement report) =>
        report.GetProperty("findings").EnumerateArray().Select(f => f.GetProperty("code").GetString()!).ToArray();
}
