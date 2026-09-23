using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Martlet.Host.Inventory.Tests;

public sealed class CodecTests
{
    private static string ValidJson() => HostJson.Serialize(ReportFixtures.Create());
    private static HostReport Read(string json) => HostJson.Deserialize(Encoding.UTF8.GetBytes(json));

    [Fact]
    public void Whitespace_and_object_order_are_not_canonical_byte_requirements()
    {
        var node = JsonNode.Parse(ValidJson())!.AsObject();
        var reversed = new JsonObject(node.Reverse().Select(p => KeyValuePair.Create(p.Key, p.Value?.DeepClone())));
        Assert.Equal(ValidJson(), HostJson.Serialize(Read(" \r\n" + reversed.ToJsonString() + "\n")));
    }

    [Fact]
    public void Synthetic_lf_input_normalizes_to_documented_crlf_not_platform_default()
    {
        var canonical = ValidJson();
        Assert.Equal("\r\n", HostJson.Options.NewLine);
        Assert.Contains("\r\n", canonical);
        var lf = canonical.Replace("\r\n", "\n", StringComparison.Ordinal);
        Assert.DoesNotContain('\r', lf);
        Assert.Equal(canonical, HostJson.Serialize(Read(lf)));
    }

    [Fact]
    public void Exact_byte_limits_apply_to_reads_and_writes()
    {
        var json = ValidJson();
        var bytes = Encoding.UTF8.GetBytes(json);
        Assert.Equal(json, HostJson.Serialize(ReportFixtures.Create(), bytes.Length));
        Assert.Throws<InvalidDataException>(() => HostJson.Serialize(ReportFixtures.Create(), bytes.Length - 1));
        Assert.Equal(json, HostJson.Serialize(HostJson.Deserialize(bytes, bytes.Length)));
        Assert.Throws<InvalidDataException>(() => HostJson.Deserialize(bytes, bytes.Length - 1));
        var maximum = new byte[HostJson.MaximumBytes];
        maximum.AsSpan().Fill((byte)' ');
        bytes.CopyTo(maximum, 0);
        Assert.Equal(json, HostJson.Serialize(HostJson.Deserialize(maximum)));
        byte[] oversized = [.. maximum, (byte)' '];
        Assert.Throws<InvalidDataException>(() => HostJson.Deserialize(oversized));
        foreach (var limit in new[] { 0, -1, HostJson.MaximumBytes + 1 })
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => HostJson.Serialize(ReportFixtures.Create(), limit));
            Assert.Throws<ArgumentOutOfRangeException>(() => HostJson.Deserialize(bytes, limit));
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("true")]
    [InlineData("{")]
    public void Malformed_or_wrong_root_fails_explicitly(string json) =>
        Assert.Throws<InvalidDataException>(() => Read(json));

    [Fact]
    public void Duplicates_including_escaped_names_are_rejected()
    {
        var json = ValidJson();
        foreach (var duplicate in new[] { "\"schemaVersion\":1,", "\"schema\\u0056ersion\":1," })
            Assert.Throws<InvalidDataException>(() => Read("{" + duplicate + json[1..]));
        Assert.Throws<InvalidDataException>(() => Read(json.Replace("\"logicalProcessors\": 2", "\"logicalProcessors\": 2, \"logicalProcessors\": 2", StringComparison.Ordinal)));
        Assert.Throws<InvalidDataException>(() => Read(json.Replace("\"deploymentQualified\": false", "\"deploymentQualified\": false, \"deploymentQualified\": false", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData("\"future\"")]
    [InlineData("\"inventory\"")]
    [InlineData("\"0\"")]
    [InlineData("0")]
    [InlineData("null")]
    public void Exact_enum_contract_rejects_alternatives(string value)
    {
        var node = JsonNode.Parse(ValidJson())!;
        node["scope"] = JsonNode.Parse(value);
        Assert.Throws<InvalidDataException>(() => Read(node.ToJsonString()));
    }

    [Fact]
    public void Derived_fields_and_candidate_requirements_are_checked_not_ignored()
    {
        Action<JsonNode>[] mutations =
        [
            n => n["deploymentQualified"] = true,
            n => n["exitCode"] = 0,
            n => n["scopeNotice"] = "VERIFIED LIVE HOST",
            n => n["candidateRequirements"]!["status"] = "Qualified",
            n => n["candidateRequirements"]!["qualifiedTuple"]!["rightsApproved"] = true,
            n => n["candidateRequirements"]!["requirements"]![0]!["version"]!["exactVersion"] = "999.0",
            n => n["probes"]![0]!["state"] = "Unknown",
            n => n["probes"]![0]!["summary"] = "SECRET_native_text",
            n => n["probes"]![0]!["remedy"]!["officialUrl"] = "https://example.invalid/execute",
            n => n["probes"]![0]!["remedy"]!["action"] = "RetryReadOnly",
            n => n["probes"]![0]!["remedy"] = null,
            n => n["candidateRequirements"] = null,
            n => n["probes"]![0] = null,
            n => n["probes"] = null,
            n => n["applicationVersion"] = null,
            n => n["probes"]![5]!["evidence"]!["gpus"]![0]!["model"] = null,
            n => n["schemaVersion"] = 2
        ];
        foreach (var mutate in mutations)
        {
            var node = JsonNode.Parse(ValidJson())!;
            mutate(node);
            Assert.Throws<InvalidDataException>(() => Read(node.ToJsonString()));
        }
    }

    [Fact]
    public void Every_serialized_field_is_required_including_optional_nulls_and_derived_properties()
    {
        var root = JsonNode.Parse(ValidJson())!;
        foreach (var obj in Objects(root))
        {
            foreach (var name in obj.Select(p => p.Key).ToArray())
            {
                var value = obj[name];
                obj.Remove(name);
                Assert.Throws<InvalidDataException>(() => Read(root.ToJsonString()));
                obj.Add(name, value);
            }
        }
    }

    private static IEnumerable<JsonObject> Objects(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            yield return obj;
            foreach (var child in obj.Select(p => p.Value).Where(v => v is not null).ToArray())
                foreach (var nested in Objects(child!)) yield return nested;
        }
        else if (node is JsonArray array)
            foreach (var child in array.Where(v => v is not null))
                foreach (var nested in Objects(child!)) yield return nested;
    }

    [Fact]
    public void Unknown_fields_at_every_object_level_fail()
    {
        var root = JsonNode.Parse(ValidJson())!;
        foreach (var obj in Objects(root))
        {
            obj.Add("unknown", "SECRET_native_text");
            Assert.Throws<InvalidDataException>(() => Read(root.ToJsonString()));
            obj.Remove("unknown");
        }
    }

    [Fact]
    public void Malformed_utf8_and_surrogates_do_not_escape_as_runtime_errors()
    {
        foreach (var text in new[] { "{\"\\uD800\":0}", "{\"bad\":\"\\uD800\"}", "{\"bad\":\"\\uDC00\"}" })
            Assert.Throws<InvalidDataException>(() => Read(text));
        var bytes = Encoding.UTF8.GetBytes(ValidJson());
        var index = Array.IndexOf(bytes, (byte)'A');
        Assert.True(index > 0);
        bytes[index] = 0xff;
        Assert.Throws<InvalidDataException>(() => HostJson.Deserialize(bytes));
    }

    [Theory]
    [InlineData(16)]
    [InlineData(17)]
    public void Nested_documents_do_not_bypass_depth_or_schema_limit(int depth)
    {
        Assert.Equal(16, HostJson.Options.MaxDepth);
        var json = new string('[', depth) + "0" + new string(']', depth);
        if (depth == 16)
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = HostJson.MaximumDepth });
            Assert.Equal(JsonValueKind.Array, document.RootElement.ValueKind);
        }
        else
            Assert.ThrowsAny<JsonException>(() => JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = HostJson.MaximumDepth }));
        Assert.Throws<InvalidDataException>(() => Read(json));
    }
}
