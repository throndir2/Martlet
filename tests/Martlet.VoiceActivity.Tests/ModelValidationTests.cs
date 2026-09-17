using System.Text;
using System.Text.Json.Nodes;

namespace Martlet.VoiceActivity.Tests;

public sealed class ModelValidationTests
{
    private static VoiceActivityInferenceAccess Granted() => new(() => { });

    [Fact]
    public void EmbeddedManifestPinsTheReviewedArtifacts()
    {
        var manifest = SileroVadModel.ReadManifest(Granted());
        Assert.Equal("silero-vad-v6.2.1-16k-op15", manifest.ModelId);
        Assert.Equal("7e30209a3e901f9842f81b225f3e93d8199902b1", manifest.SourceRevision);
        Assert.Equal("silero_vad_16k_op15.onnx", manifest.FileName);
        Assert.Equal(1289603, manifest.ModelBytes);
        Assert.Equal("7ed98ddbad84ccac4cd0aeb3099049280713df825c610a8ed34543318f1b2c49", manifest.ModelSha256);
        Assert.Equal("1.30.0", manifest.RuntimeVersion);
        Assert.Equal("f2c39fe2f838cf35ce7da92824f5a5e3ee6e88a7", manifest.RuntimeSourceRevision);
        Assert.Equal(2, manifest.NativeFiles.Count);
        Assert.Contains(new VadNativeArtifact("onnxruntime.dll", 16462648,
            "7e39e2bdbba836d98071ef28620735ba36a47c554cf794585269aecc50fab0da"), manifest.NativeFiles);
        Assert.Contains(new VadNativeArtifact("onnxruntime_providers_shared.dll", 21816,
            "b9b7ab9e2a8b08ee7ae4a7ac1c8bfd44a741f17ad8d0f764953140a57de0b796"), manifest.NativeFiles);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("unknown")]
    [InlineData("version")]
    [InlineData("runtime")]
    [InlineData("hash")]
    [InlineData("size")]
    [InlineData("duplicate-native")]
    [InlineData("native-path")]
    public void ManifestRejectsUnrecognizedOrMalformedContracts(string change)
    {
        var json = ManifestJson();
        switch (change)
        {
            case "missing": json.Remove("modelSha256"); break;
            case "unknown": json["fallbackUrl"] = "https://example.invalid/unused"; break;
            case "version": json["schemaVersion"] = 2; break;
            case "runtime": json["runtimeVersion"] = "1.31.0"; break;
            case "hash": json["modelSha256"] = "not-a-digest"; break;
            case "size": json["modelBytes"] = int.MaxValue; break;
            case "duplicate-native":
                json["nativeFiles"]![1]!["name"] = "onnxruntime.dll";
                break;
            case "native-path":
                json["nativeFiles"]![0]!["name"] = @"..\onnxruntime.dll";
                break;
        }
        Failure(VoiceActivityFailureCode.ModelUnavailable,
            () => SileroVadModel.ParseManifest(Encoding.UTF8.GetBytes(json.ToJsonString())));
    }

    [Fact]
    public void ManifestRejectsDuplicatePropertiesAndOversizeInput()
    {
        string json = ManifestJson().ToJsonString();
        string duplicate = "{\"schemaVersion\":1," + json[1..];
        Failure(VoiceActivityFailureCode.ModelUnavailable,
            () => SileroVadModel.ParseManifest(Encoding.UTF8.GetBytes(duplicate)));
        Failure(VoiceActivityFailureCode.ModelUnavailable, () => SileroVadModel.ParseManifest(new byte[16_385]));
    }

    [Theory]
    [InlineData(@"relative.onnx")]
    [InlineData(@"C:relative.onnx")]
    [InlineData(@"\\server\share\model.onnx")]
    [InlineData(@"\\?\C:\model.onnx")]
    [InlineData(@"\\.\PhysicalDrive0")]
    [InlineData(@"C:\models\model.onnx:stream")]
    [InlineData(@"C:\models\..\model.onnx")]
    [InlineData(@"C:\models\model.onnx.")]
    [InlineData(@"C:\models\NUL.onnx")]
    [InlineData(@"C:\models\COM1")]
    [InlineData(@"C:\models\COM¹.bin")]
    [InlineData(@"C:\models\NUL .onnx")]
    public void NonlocalAmbiguousOrDevicePathsAreRejectedWithoutOpeningThem(string path) =>
        Failure(VoiceActivityFailureCode.ModelUnavailable, () => SileroVadModel.ValidateLocalPath(path));

    [Fact]
    public void AccessFailurePrecedesEvenPathValidation()
    {
        var access = new VoiceActivityInferenceAccess(() => throw new VoiceActivityException(VoiceActivityFailureCode.Canceled));
        Failure(VoiceActivityFailureCode.Canceled, () => SileroVadModel.Load("not-a-local-path", access));
    }

    [Fact]
    public void WrongLengthIsRejectedBeforeAnyPayloadRead()
    {
        using var source = new ReportedStream([], 1);
        Failure(VoiceActivityFailureCode.InvalidModelSize, () => SileroVadModel.ReadPinnedSnapshot(source, Granted()));
        Assert.Equal(0, source.Reads);
    }

    [Fact]
    public void TinyAuthoredLocalFileIsNotAcceptedAsTheModel()
    {
        string path = Path.Combine(Path.GetTempPath(), $"vad-model-validation-{Guid.NewGuid():N}.fixture");
        using var source = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
            4096, FileOptions.DeleteOnClose);
        source.Write([1, 2, 3]);
        source.Flush();
        Failure(VoiceActivityFailureCode.InvalidModelSize, () => SileroVadModel.ReadPinnedSnapshot(source, Granted()));
    }

    [Fact]
    public void CorrectLengthSyntheticBytesStillFailTheEmbeddedDigest()
    {
        var manifest = SileroVadModel.ReadManifest(Granted());
        var bytes = new byte[manifest.ModelBytes];
        using var source = new ReportedStream(bytes, bytes.Length);
        Failure(VoiceActivityFailureCode.InvalidModelHash, () => SileroVadModel.ReadPinnedSnapshot(source, Granted()));
        Assert.NotNull(source.Destination);
        Assert.True(source.Destination.AsSpan().IndexOfAnyExcept((byte)0) < 0);
    }

    [Fact]
    public void ParsingOtherMetadataDoesNotReplaceTheLoadersDigest()
    {
        var json = ManifestJson();
        json["modelSha256"] = new string('0', 64);
        Assert.Equal(new string('0', 64),
            SileroVadModel.ParseManifest(Encoding.UTF8.GetBytes(json.ToJsonString())).ModelSha256);
        Assert.Equal("7ed98ddbad84ccac4cd0aeb3099049280713df825c610a8ed34543318f1b2c49",
            SileroVadModel.ReadManifest(Granted()).ModelSha256);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OriginalAccessFailureAfterReadOrReadFailureWinsAndClearsPrivateSnapshot(bool ioThrows)
    {
        var manifest = SileroVadModel.ReadManifest(Granted());
        bool expired = false;
        using var source = new ReportedStream([12, 34, 56], manifest.ModelBytes)
        {
            AfterRead = () =>
            {
                expired = true;
                if (ioThrows) throw new IOException("PRIVATE-MODEL-IO-CANARY");
            }
        };
        var access = new VoiceActivityInferenceAccess(() =>
        {
            if (expired) throw new VoiceActivityException(VoiceActivityFailureCode.DeadlineExceeded);
        });
        Failure(VoiceActivityFailureCode.DeadlineExceeded, () => SileroVadModel.ReadPinnedSnapshot(source, access));
        Assert.NotNull(source.Destination);
        Assert.True(source.Destination.AsSpan().IndexOfAnyExcept((byte)0) < 0);
    }

    [Fact]
    public void ATruncatedSnapshotIsNotAHashOrNoActivityResult()
    {
        var manifest = SileroVadModel.ReadManifest(Granted());
        using var source = new ReportedStream([12, 34, 56], manifest.ModelBytes);
        Failure(VoiceActivityFailureCode.InvalidModelSize, () => SileroVadModel.ReadPinnedSnapshot(source, Granted()));
        Assert.NotNull(source.Destination);
        Assert.True(source.Destination.AsSpan().IndexOfAnyExcept((byte)0) < 0);
    }

    [Fact]
    public void ExactlyThePinnedDeclaredSchemaIsAccepted()
    {
        var (inputs, outputs) = Schema();
        ModelSchema.Validate(inputs, outputs, 0);
        Array.Reverse(inputs);
        Array.Reverse(outputs);
        ModelSchema.Validate(inputs, outputs, 0);
    }

    [Theory]
    [InlineData("name")]
    [InlineData("duplicate-name")]
    [InlineData("sparse")]
    [InlineData("element")]
    [InlineData("sr-vector")]
    [InlineData("input-static")]
    [InlineData("input-symbol")]
    [InlineData("state-rank")]
    [InlineData("state-axis")]
    [InlineData("state-output-static")]
    [InlineData("state-output-symbol")]
    [InlineData("output-rank")]
    [InlineData("output-element")]
    [InlineData("state-output-sparse")]
    [InlineData("scalar-missing-dimensions")]
    [InlineData("initializer")]
    public void DeclaredMetadataCannotBeGuessedOrRelaxed(string change)
    {
        var (inputs, outputs) = Schema();
        switch (change)
        {
            case "name": inputs[0] = inputs[0] with { Name = "Input" }; break;
            case "duplicate-name": inputs[1] = inputs[0]; break;
            case "sparse": inputs[0] = inputs[0] with { PlainTensor = false }; break;
            case "element": inputs[1] = inputs[1] with { ElementType = ModelElementType.Float32 }; break;
            case "sr-vector": inputs[1] = inputs[1] with { Dimensions = [1], Symbols = [""] }; break;
            case "input-static": inputs[0] = inputs[0] with { Dimensions = [1, 576] }; break;
            case "input-symbol": inputs[0] = inputs[0] with { Symbols = ["batch", "other"] }; break;
            case "state-rank": inputs[2] = inputs[2] with { Dimensions = [2, 128], Symbols = ["", ""] }; break;
            case "state-axis": inputs[2] = inputs[2] with { Dimensions = [-1, -1, 128] }; break;
            case "state-output-static": outputs[1] = outputs[1] with { Dimensions = [2, 1, 128] }; break;
            case "state-output-symbol": outputs[1] = outputs[1] with { Symbols = ["", "batch", ""] }; break;
            case "output-rank": outputs[0] = outputs[0] with { Dimensions = [-1], Symbols = ["batch"] }; break;
            case "output-element": outputs[0] = outputs[0] with { ElementType = ModelElementType.Int64 }; break;
            case "state-output-sparse": outputs[1] = outputs[1] with { PlainTensor = false }; break;
            case "scalar-missing-dimensions": inputs[1] = inputs[1] with { Dimensions = null! }; break;
        }
        Failure(VoiceActivityFailureCode.ModelSchemaMismatch,
            () => ModelSchema.Validate(inputs, outputs, change == "initializer" ? 1 : 0));
    }

    [Fact]
    public void ValidFixedOutputAndFullWindowAreAccepted()
    {
        ModelSchema.ValidateWindow(new float[512]);
        Assert.Equal(0.5f, ModelSchema.ValidateResult([1, 1], [0.5f], [2, 1, 128], new float[256]));
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    [InlineData(-0.001f)]
    [InlineData(1.001f)]
    public void InvalidScoresAreTypedFailures(float score) =>
        Failure(VoiceActivityFailureCode.InvalidScore,
            () => ModelSchema.ValidateResult([1, 1], [score], [2, 1, 128], new float[256]));

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void EveryRecurrentElementMustBeFinite(float value)
    {
        var state = new float[256];
        state[^1] = value;
        Failure(VoiceActivityFailureCode.InvalidScore,
            () => ModelSchema.ValidateResult([1, 1], [0.5f], [2, 1, 128], state));
    }

    [Fact]
    public void DynamicDeclarationsNeverRelaxActualTensorShapesOrLengths()
    {
        Failure(VoiceActivityFailureCode.InvalidScore,
            () => ModelSchema.ValidateResult([1], [0.5f], [2, 1, 128], new float[256]));
        Failure(VoiceActivityFailureCode.InvalidScore,
            () => ModelSchema.ValidateResult([1, 1], [], [2, 1, 128], new float[256]));
        Failure(VoiceActivityFailureCode.InvalidScore,
            () => ModelSchema.ValidateResult([1, 1], [0.5f], [2, 128, 1], new float[256]));
        Failure(VoiceActivityFailureCode.InvalidScore,
            () => ModelSchema.ValidateResult([1, 1], [0.5f], [2, 1, 128], new float[255]));
        Failure(VoiceActivityFailureCode.InvalidInput, () => ModelSchema.ValidateWindow(new float[511]));
        var window = new float[512];
        window[17] = float.NaN;
        Failure(VoiceActivityFailureCode.InvalidInput, () => ModelSchema.ValidateWindow(window));
    }

    private static void Failure(VoiceActivityFailureCode expected, Action action)
    {
        var error = Assert.Throws<VoiceActivityException>(action);
        Assert.Equal(expected, error.Failure.Code);
        Assert.Null(error.InnerException);
        Assert.Equal(new VoiceActivityFailure(expected).Summary, error.Message);
        Assert.DoesNotContain("CANARY", error.Message, StringComparison.Ordinal);
    }

    private static (ModelTensorSchema[] Inputs, ModelTensorSchema[] Outputs) Schema() =>
        ([
            new("input", true, ModelElementType.Float32, [-1, -1], ["batch", "sequence"]),
            new("sr", true, ModelElementType.Int64, [], []),
            new("state", true, ModelElementType.Float32, [2, -1, 128], ["", "batch", ""])
        ], [
            new("output", true, ModelElementType.Float32, [-1, 1], ["batch", ""]),
            new("stateN", true, ModelElementType.Float32, [-1, -1, -1],
                ["AddstateN_dim_0", "batch", "AddstateN_dim_2"])
        ]);

    private static JsonObject ManifestJson()
    {
        using var stream = typeof(SileroVadModel).Assembly.GetManifestResourceStream(
            "Martlet.VoiceActivity.model-manifest.json")!;
        return JsonNode.Parse(stream)!.AsObject();
    }

    private sealed class ReportedStream(byte[] bytes, long reportedLength) : MemoryStream(bytes, writable: false)
    {
        internal int Reads;
        internal byte[]? Destination;
        internal Action? AfterRead;
        public override long Length => reportedLength;
        public override int Read(byte[] buffer, int offset, int count)
        {
            Reads++;
            Destination = buffer;
            int read = base.Read(buffer, offset, count);
            AfterRead?.Invoke();
            return read;
        }
    }
}
