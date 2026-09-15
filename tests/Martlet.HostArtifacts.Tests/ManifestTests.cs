using System.Text;
using System.Text.Json.Nodes;
using Martlet.HostArtifacts;
using static Martlet.HostArtifacts.Tests.SyntheticManifests;

namespace Martlet.HostArtifacts.Tests;

public sealed class ManifestTests
{
    public static IEnumerable<object[]> RequiredFields()
    {
        var doc = Candidate();
        string[] paths = ["", "sources/0", "runtimes/0", "runtimes/1/dependencies/0",
            "runtimes/1/dependencies/0/constraints/0", "artifacts/0", "artifacts/0/release", "licenses/0",
            "roles/0", "roles/0/components/0"];
        foreach (var path in paths)
            foreach (var field in At(doc, path))
                yield return [path, field.Key];
    }

    [Theory]
    [MemberData(nameof(RequiredFields))]
    public void EveryWirePropertyIsRequired(string path, string field)
    {
        var doc = Fixture();
        At(doc, path).Remove(field);
        Assert.Throws<ArtifactManifestException>(() => Read(doc));
    }

    [Theory]
    [InlineData("")]
    [InlineData("sources/0")]
    [InlineData("runtimes/0")]
    [InlineData("runtimes/1/dependencies/0")]
    [InlineData("runtimes/1/dependencies/0/constraints/0")]
    [InlineData("artifacts/0")]
    [InlineData("artifacts/0/release")]
    [InlineData("licenses/0")]
    [InlineData("roles/0")]
    [InlineData("roles/0/components/0")]
    public void EveryWireObjectRejectsUnknownFieldsAndHooks(string path)
    {
        var doc = Fixture();
        At(doc, path)["execute"] = "PRIVATE-CANARY";
        Rejected(doc);
    }

    [Theory]
    [InlineData("sources")]
    [InlineData("runtimes")]
    [InlineData("artifacts")]
    [InlineData("licenses")]
    [InlineData("roles")]
    public void NullCollectionsAndElementsAreSanitizedContractErrors(string collection)
    {
        var doc = Fixture();
        doc[collection] = null;
        Rejected(doc);
        doc = Fixture();
        doc[collection]![0] = null;
        Rejected(doc);
    }

    [Theory]
    [InlineData("runtimes/0", "dependencies")]
    [InlineData("runtimes/1/dependencies/0", "constraints")]
    [InlineData("artifacts/0", "depends_on")]
    [InlineData("artifacts/0", "license_ids")]
    [InlineData("roles/0", "components")]
    [InlineData("roles/0", "root_artifact_ids")]
    public void NestedNullElementsAreContractErrors(string path, string field)
    {
        var doc = Fixture();
        At(doc, path)[field] = new JsonArray((JsonNode?)null);
        Rejected(doc);
    }

    [Theory]
    [InlineData("{}", "manifest.invalid_json")]
    [InlineData("[]", "manifest.invalid_json")]
    [InlineData("null", "manifest.invalid_json")]
    [InlineData("true", "manifest.invalid_json")]
    [InlineData("{\"format_version\":99,\"future\":true}", "manifest.unsupported_version")]
    [InlineData("{\"format_version\":1,\"format_version\":1}", "manifest.invalid_json")]
    [InlineData("{\"format_version\":1,\"\\u0066ormat_version\":1}", "manifest.invalid_json")]
    [InlineData("{\"format_version\":1} {}", "manifest.invalid_json")]
    [InlineData("{\"format_version\":1,}", "manifest.invalid_json")]
    [InlineData("{\"format_version\":1/* comment */}", "manifest.invalid_json")]
    [InlineData("{\"format_version\":1,\"secret\":\"\\uD800\"}", "manifest.invalid_json")]
    [InlineData("{\"format_version\":1,\"\\uDC00\":true}", "manifest.invalid_json")]
    public void JsonAndVersionErrorsUseStableCodes(string text, string code) =>
        Assert.Equal(code, Assert.Throws<ArtifactManifestException>(() => ArtifactManifestReader.Read(Encoding.UTF8.GetBytes(text))).DiagnosticCode);

    [Fact]
    public void RawUtf8AndExcessiveDepthAreRejected()
    {
        byte[] prefix = Encoding.UTF8.GetBytes("{\"format_version\":1,\"secret\":\"");
        byte[] invalid = [.. prefix, 0xED, 0xA0, 0x80, (byte)'"', (byte)'}'];
        Assert.Throws<ArtifactManifestException>(() => ArtifactManifestReader.Read(invalid));
        var depth = "{\"format_version\":1,\"nested\":" + new string('[', 17) + "0" + new string(']', 17) + "}";
        Assert.Throws<ArtifactManifestException>(() => ArtifactManifestReader.Read(Encoding.UTF8.GetBytes(depth)));
    }

    [Fact]
    public void InputLimitIsExactAndNotACharacterLimit()
    {
        var valid = CandidateBytes();
        var atLimit = Enumerable.Repeat((byte)' ', ArtifactManifestReader.MaximumBytes).ToArray();
        valid.CopyTo(atLimit, 0);
        Assert.NotNull(ArtifactManifestReader.Read(atLimit));
        byte[] tooLarge = [.. atLimit, (byte)' '];
        Assert.Equal("manifest.too_large", Assert.Throws<ArtifactManifestException>(() =>
            ArtifactManifestReader.Read(tooLarge)).DiagnosticCode);
    }

    [Theory]
    [InlineData("sources/0", "kind", "Github")]
    [InlineData("sources/0", "kind", "github, hugging_face")]
    [InlineData("runtimes/0", "family", "ollama ")]
    [InlineData("runtimes/0", "role", "3")]
    [InlineData("runtimes/0", "role", "vision")]
    [InlineData("licenses/0", "disposition", "approved")]
    [InlineData("artifacts/0", "sha256_evidence", "locally_verified")]
    [InlineData("roles/0/components/0", "kind", "RuntimeArchive")]
    public void NamedEnumsNeverAcceptAliasesOrUnimplementedClaims(string path, string field, string value)
    {
        var doc = Fixture();
        At(doc, path)[field] = value;
        Rejected(doc);
        At(doc, path)[field] = 0;
        Rejected(doc);
    }

    [Theory]
    [InlineData("id")]
    [InlineData("Id")]
    [InlineData("ID")]
    public void WronglyCasedOrDuplicatePropertiesAreNotNormalized(string spelling)
    {
        var text = Encoding.UTF8.GetString(CandidateBytes());
        text = text.Replace("\"id\": \"host-artifacts-h02a\"", $"\"id\": \"host-artifacts-h02a\", \"{spelling}\": \"PRIVATE-CANARY\"", StringComparison.Ordinal);
        Assert.Throws<ArtifactManifestException>(() => ArtifactManifestReader.Read(Encoding.UTF8.GetBytes(text)));
    }

    [Theory]
    [InlineData("main")]
    [InlineData("latest")]
    [InlineData("v0.34.0")]
    [InlineData("0000000000000000000000000000000000000000")]
    [InlineData("D8AB4B4F0CA24B51D3A46B3BF4F462E58CE66B1F")]
    public void SourceRequiresFullCanonicalCommitNotFloatingReference(string pin)
    {
        var doc = Fixture();
        Item(doc, "sources", 0)["revision"] = pin;
        Rejected(doc);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("0000000000000000000000000000000000000000000000000000000000000000")]
    [InlineData("CF95886728959AA09910BB34DE5CCA1CC5A8F68003B5597197D3F2C2D57C0804")]
    [InlineData("cd934390e8f4b3ce98eb319ae618c084d01504b5")]
    public void PayloadHashIsNotAGitOidOrPlaceholder(string hash)
    {
        var doc = Fixture();
        Item(doc, "artifacts", 0)["sha256"] = hash;
        Rejected(doc);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    [InlineData(17_592_186_044_417L)]
    [InlineData(long.MaxValue)]
    public void PayloadSizeMustBeExactPositiveBoundedBytes(long bytes)
    {
        var doc = Fixture();
        Item(doc, "artifacts", 0)["bytes"] = bytes;
        Rejected(doc);
    }

    [Fact]
    public void BytesRejectStringsFractionsAndAcceptTheDeclaredUpperBound()
    {
        var doc = Fixture();
        Item(doc, "artifacts", 0)["bytes"] = "1433537033";
        Rejected(doc);
        Item(doc, "artifacts", 0)["bytes"] = 1.5;
        Rejected(doc);
        Item(doc, "artifacts", 0)["bytes"] = 17_592_186_044_416L;
        Assert.NotNull(Read(doc));
    }

    [Theory]
    [InlineData("../vocab.txt")]
    [InlineData("/vocab.txt")]
    [InlineData("x//vocab.txt")]
    [InlineData("x/./vocab.txt")]
    [InlineData("x\\vocab.txt")]
    [InlineData("C:\\PRIVATE-CANARY")]
    [InlineData("vocab.txt:stream")]
    [InlineData("x/%2e%2e/vocab.txt")]
    [InlineData("x%2fvocab.txt")]
    [InlineData("vocab.txt?token=PRIVATE-CANARY")]
    [InlineData("NUL")]
    [InlineData("con.txt")]
    [InlineData("COM1.json")]
    [InlineData("vocab.")]
    [InlineData("vocab.txt ")]
    [InlineData("vocab.txt\n")]
    [InlineData("\u202evocab.txt")]
    public void PathsRemainInertCanonicalUpstreamData(string path)
    {
        var doc = Fixture();
        ChangeArtifactPath(doc, 2, path);
        Rejected(doc);
    }

    [Fact]
    public void IdentifierAndPathLimitsAreExact()
    {
        var doc = Fixture();
        doc["id"] = new string('a', 64);
        Assert.NotNull(Read(doc));
        doc["id"] = new string('a', 65);
        Rejected(doc);
        doc = Fixture();
        var path = string.Join('/', new string('a', 128), new string('b', 128), new string('c', 128), new string('d', 125));
        Assert.Equal(512, path.Length);
        ChangeArtifactPath(doc, 2, path);
        Assert.NotNull(Read(doc));
        ChangeArtifactPath(doc, 2, path + "a");
        Rejected(doc);
        ChangeArtifactPath(doc, 2, new string('a', 129));
        Rejected(doc);
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r")]
    [InlineData("\r\n")]
    [InlineData("\t")]
    [InlineData("\0")]
    public void CanonicalTokensRejectTrailingControlsInEveryValidationLayer(string control)
    {
        var doc = Fixture();
        doc["id"] = "fixture" + control;
        Rejected(doc);
        doc = Fixture();
        At(doc, "runtimes/1/dependencies/0/constraints/0")["version"] = "0.33.0" + control;
        Rejected(doc);
        doc = Fixture();
        var source = Item(doc, "sources", 2);
        source["repository"] = "SWivid/F5-TTS" + control;
        source["commit_url"] = $"https://huggingface.co/SWivid/F5-TTS{control}/tree/{source["revision"]!.GetValue<string>()}";
        Rejected(doc);
    }

    [Theory]
    [InlineData("http://huggingface.co/a/b")]
    [InlineData("https://huggingface.co.evil.example/a/b")]
    [InlineData("https://huggingface.co@evil.example/a/b")]
    [InlineData("https://PRIVATE-CANARY@huggingface.co/a/b")]
    [InlineData("https://huggingface.co:443/a/b")]
    [InlineData("https://huggingface.co:7443/a/b")]
    [InlineData("https://127.0.0.1/model")]
    [InlineData("https://[::1]/model")]
    [InlineData("https://192.168.1.10/model")]
    [InlineData("file:///C:/PRIVATE-CANARY")]
    [InlineData("\\\\host\\PRIVATE-CANARY")]
    [InlineData("/relative/model")]
    public void UnsupportedOriginsAreRejectedWithoutResolution(string url)
    {
        var doc = Fixture();
        Item(doc, "artifacts", 1)["source_url"] = url;
        Rejected(doc);
    }

    [Theory]
    [InlineData("?token=PRIVATE-CANARY")]
    [InlineData("#PRIVATE-CANARY")]
    [InlineData("/../model_1250000.safetensors")]
    [InlineData("/")]
    public void UrlAliasesAreRejectedBeforeNormalization(string suffix)
    {
        var doc = Fixture();
        var artifact = Item(doc, "artifacts", 1);
        artifact["source_url"] = artifact["source_url"]!.GetValue<string>() + suffix;
        Rejected(doc);
    }

    [Theory]
    [InlineData("sources/0", "commit_url")]
    [InlineData("runtimes/0", "dependency_evidence_url")]
    [InlineData("artifacts/0", "evidence_url")]
    [InlineData("artifacts/0/release", "metadata_url")]
    [InlineData("licenses/0", "evidence_url")]
    public void EveryEvidenceUrlIsBoundToItsSource(string path, string field)
    {
        var doc = Fixture();
        At(doc, path)[field] = "https://example.com/PRIVATE-CANARY";
        Rejected(doc);
    }

    [Fact]
    public void MissingSelfCyclicAndDuplicateEdgesAreDistinctErrors()
    {
        var doc = Fixture();
        Item(doc, "artifacts", 1)["depends_on"] = new JsonArray("missing");
        Assert.Equal("manifest.reference_missing", Rejected(doc).DiagnosticCode);
        Item(doc, "artifacts", 1)["depends_on"] = new JsonArray("f5-v1-weights");
        Assert.Equal("manifest.dependency_cycle", Rejected(doc).DiagnosticCode);
        doc = Fixture();
        Item(doc, "artifacts", 4)["depends_on"] = new JsonArray("f5-v1-weights");
        Assert.Equal("manifest.dependency_cycle", Rejected(doc).DiagnosticCode);
        doc = Fixture();
        Item(doc, "artifacts", 1)["depends_on"] = new JsonArray("f5-v1-vocabulary", "f5-v1-vocabulary");
        Assert.Equal("manifest.alias_invalid", Rejected(doc).DiagnosticCode);
        doc = Fixture();
        Item(doc, "artifacts", 1)["depends_on"] = new JsonArray("f5-v1-vocabulary", "vocos-config");
        Assert.Equal("manifest.role_invalid", Rejected(doc).DiagnosticCode);
    }

    [Theory]
    [InlineData("sources")]
    [InlineData("runtimes")]
    [InlineData("artifacts")]
    [InlineData("licenses")]
    [InlineData("roles")]
    public void DuplicateRecordsAndCaseAliasesCannotHideRequirements(string collection)
    {
        var doc = Fixture();
        doc[collection]!.AsArray().Add(doc[collection]![0]!.DeepClone());
        Rejected(doc);
        doc = Fixture();
        doc[collection]![0]!["id"] = doc[collection]![0]!["id"]!.GetValue<string>().ToUpperInvariant();
        Rejected(doc);
    }

    [Fact]
    public void DuplicateContentAndPathIdentitiesCannotCreateDifferentComponents()
    {
        var doc = Fixture();
        Item(doc, "artifacts", 3)["sha256"] = Item(doc, "artifacts", 1)["sha256"]!.DeepClone();
        Assert.Equal("manifest.alias_invalid", Rejected(doc).DiagnosticCode);
        doc = Fixture();
        ChangeArtifactPath(doc, 2, Item(doc, "artifacts", 1)["path"]!.GetValue<string>());
        Assert.Equal("manifest.alias_invalid", Rejected(doc).DiagnosticCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OneGithubAssetCannotHaveConflictingObservations(bool changeRelease)
    {
        var doc = WithArchiveAlias(uniqueAsset: false, changeRelease);
        Assert.Equal("manifest.alias_invalid", Rejected(doc).DiagnosticCode);
    }

    [Fact]
    public void DifferentAssetsInOneReleaseRemainDistinctDeclaredPayloads()
    {
        var report = Report(WithArchiveAlias(uniqueAsset: true, changeRelease: false));
        Assert.Equal(2_836_353_047L, report.GetProperty("disk").GetProperty("known_listed_payload_bytes").GetInt64());
        Assert.False(report.GetProperty("execution_eligible").GetBoolean());
    }

    private static JsonObject WithArchiveAlias(bool uniqueAsset, bool changeRelease)
    {
        var doc = Fixture();
        var artifact = Item(doc, "artifacts", 0).DeepClone();
        artifact["id"] = "ollama-archive-alias";
        artifact["path"] = "other.tar.zst";
        artifact["bytes"] = 1;
        artifact["sha256"] = new string('1', 64);
        artifact["source_url"] = "https://github.com/ollama/ollama/releases/download/v0.34.0/other.tar.zst";
        if (uniqueAsset)
        {
            artifact["release"]!["asset_id"] = 553800780;
            artifact["evidence_url"] = "https://api.github.com/repos/ollama/ollama/releases/assets/553800780";
        }
        if (changeRelease)
        {
            artifact["release"]!["release_id"] = 383028724;
            artifact["release"]!["metadata_url"] = "https://api.github.com/repos/ollama/ollama/releases/383028724";
        }
        doc["artifacts"]!.AsArray().Add(artifact);
        var runtime = Item(doc, "runtimes", 0).DeepClone();
        runtime["id"] = "ollama-runtime-alias";
        doc["runtimes"]!.AsArray().Add(runtime);
        var role = Item(doc, "roles", 0).DeepClone();
        role["id"] = "ollama-role-alias";
        role["runtime_id"] = "ollama-runtime-alias";
        role["root_artifact_ids"] = new JsonArray("ollama-archive-alias");
        role["components"]![0]!["artifact_id"] = "ollama-archive-alias";
        doc["roles"]!.AsArray().Add(role);
        return doc;
    }

    [Fact]
    public void OmissionAndRelabelingCannotMakeF5AuxiliariesOptional()
    {
        var doc = Fixture();
        Item(doc, "roles", 1)["components"]!.AsArray().RemoveAt(4);
        Assert.Equal("manifest.role_invalid", Rejected(doc).DiagnosticCode);
        doc = Fixture();
        Item(doc, "roles", 1)["role"] = "llm";
        Assert.Equal("manifest.role_invalid", Rejected(doc).DiagnosticCode);
        doc = Fixture();
        Item(doc, "roles", 1)["components"]![4]!["artifact_id"] = "f5-v1-weights";
        Assert.Equal("manifest.role_invalid", Rejected(doc).DiagnosticCode);
        doc = Fixture();
        Item(doc, "runtimes", 1)["family"] = "ollama";
        Assert.Equal("manifest.role_invalid", Rejected(doc).DiagnosticCode);
    }

    [Theory]
    [InlineData("sources", 9)]
    [InlineData("runtimes", 5)]
    [InlineData("artifacts", 65)]
    [InlineData("licenses", 17)]
    [InlineData("roles", 5)]
    public void CountsFailBeforeGraphWork(string collection, int count)
    {
        var doc = Fixture();
        var prototype = doc[collection]![0]!.DeepClone();
        doc[collection] = new JsonArray(Enumerable.Range(0, count).Select(_ => prototype.DeepClone()).ToArray());
        Assert.Equal("manifest.bounds_invalid", Rejected(doc).DiagnosticCode);
    }

    [Fact]
    public void UnusedInventoriesAndLicenseSubstitutionAreRejected()
    {
        var doc = Fixture();
        var license = Item(doc, "licenses", 0).DeepClone();
        license["id"] = "unused-license";
        doc["licenses"]!.AsArray().Add(license);
        Assert.Equal("manifest.unreachable", Rejected(doc).DiagnosticCode);
        doc = Fixture();
        Item(doc, "artifacts", 0)["license_ids"] = new JsonArray("ollama-code");
        Assert.Equal("manifest.license_invalid", Rejected(doc).DiagnosticCode);
        doc = Fixture();
        Item(doc, "artifacts", 1)["license_ids"] = new JsonArray("f5-code");
        Assert.Equal("manifest.license_invalid", Rejected(doc).DiagnosticCode);
    }

    [Theory]
    [InlineData("at_least", "2.0", "at_most", "1.0")]
    [InlineData("greater_than", "1.0", "at_most", "1.0.0")]
    [InlineData("exact", "1.0", "at_least", "1.0")]
    [InlineData("at_least", "1.0", "greater_than", "1.0")]
    [InlineData("at_least", "1.0", "at_least", "2.0")]
    public void ContradictoryAndDuplicateConstraintShapesAreRejected(string first, string firstVersion, string second, string secondVersion)
    {
        var doc = Fixture();
        At(doc, "runtimes/1/dependencies/0")["constraints"] = new JsonArray(
            new JsonObject { ["comparison"] = first, ["version"] = firstVersion },
            new JsonObject { ["comparison"] = second, ["version"] = secondVersion });
        Assert.Equal("manifest.dependency_invalid", Rejected(doc).DiagnosticCode);
    }

    [Fact]
    public void EqualInclusiveNumericBoundsAreConsistentButNotResolved()
    {
        var doc = Fixture();
        At(doc, "runtimes/1/dependencies/0")["constraints"] = new JsonArray(
            new JsonObject { ["comparison"] = "at_least", ["version"] = "1.0" },
            new JsonObject { ["comparison"] = "at_most", ["version"] = "1.0.0" });
        Assert.Contains("runtime.dependencies_unresolved", Codes(Report(doc)));
    }

    internal static ArtifactManifestException Rejected(JsonObject doc)
    {
        var error = Assert.Throws<ArtifactManifestException>(() => Read(doc));
        Assert.DoesNotContain("PRIVATE-CANARY", error.Message);
        Assert.DoesNotContain("PRIVATE-CANARY", error.Remedy);
        return error;
    }
}
