using System.Buffers.Binary;
using System.IO.Compression;
using System.Text.Json.Nodes;
using Martlet.Core.Contracts;
using Martlet.Core.Voices;

namespace Martlet.Core.Tests;

public sealed class VoiceLibraryTests
{
    [Fact]
    public async Task ConstructionAndFirstReadDoNotCreateStorage()
    {
        using var fixture = new Fixture();
        Assert.False(Directory.Exists(fixture.LibraryPath));
        Assert.Empty(await fixture.Library.ListAsync());
        Assert.False(Directory.Exists(fixture.LibraryPath));
        Assert.Equal(Enum.GetValues<VoiceEngine>(), VoiceEngineCatalog.All.Select(item => item.Id));
        Assert.All(VoiceEngineCatalog.All, item => Assert.Contains("Not integrated", item.ExecutionStatus));
        Assert.False(VoiceEngineCatalog.Get(VoiceEngine.Chatterbox).UpstreamTrainingDocumented);
    }

    [Theory]
    [InlineData(VoiceEngine.F5Tts)]
    [InlineData(VoiceEngine.Qwen3Tts)]
    [InlineData(VoiceEngine.Chatterbox)]
    [InlineData(VoiceEngine.GptSoVits)]
    [InlineData(VoiceEngine.XttsV2)]
    public async Task ImportRestartInspectAndRemovePreserveSource(VoiceEngine engine)
    {
        using var fixture = new Fixture();
        var original = await File.ReadAllBytesAsync(fixture.Source);
        var imported = await fixture.Library.ImportAsync(fixture.Request with { Engine = engine });
        var reopened = new VoiceLibrary(fixture.LibraryPath);
        Assert.Equal(imported, Assert.Single(await reopened.ListAsync()));
        Assert.Equal(10_000, imported.Wave.DurationMilliseconds);
        Assert.Equal(engine, imported.Engine);
        using (var archive = ZipFile.OpenRead(fixture.Bundle(imported)))
        {
            using var stream = archive.GetEntry("audio.wav")!.Open();
            using var copy = new MemoryStream();
            await stream.CopyToAsync(copy);
            Assert.Equal(original, copy.ToArray());
            using var manifest = new StreamReader(archive.GetEntry("manifest.json")!.Open());
            Assert.DoesNotContain(fixture.Source, await manifest.ReadToEndAsync());
        }
        Assert.DoesNotContain("PRIVATE", imported.ToString());
        Assert.DoesNotContain("PRIVATE", fixture.Request.ToString());
        await reopened.DeleteAsync(imported);
        Assert.Empty(await reopened.ListAsync());
        Assert.Equal(original, await File.ReadAllBytesAsync(fixture.Source));
    }

    [Theory]
    [InlineData(VoiceAssetPurpose.Reference, 1, true)]
    [InlineData(VoiceAssetPurpose.Reference, 30, true)]
    [InlineData(VoiceAssetPurpose.Reference, 31, false)]
    [InlineData(VoiceAssetPurpose.TrainingMaterial, 31, true)]
    [InlineData(VoiceAssetPurpose.TrainingMaterial, 600, true)]
    [InlineData(VoiceAssetPurpose.TrainingMaterial, 601, false)]
    public async Task ExactPurposeDurationBounds(VoiceAssetPurpose purpose, int seconds, bool accepted)
    {
        using var fixture = new Fixture();
        await File.WriteAllBytesAsync(fixture.Source, Wave(seconds));
        var action = () => fixture.Library.ImportAsync(fixture.Request with { Purpose = purpose });
        if (accepted)
            Assert.Equal(seconds * 1000, (await action()).Wave.DurationMilliseconds);
        else
        {
            await Assert.ThrowsAsync<ContractException>(action);
            Assert.False(Directory.Exists(fixture.LibraryPath));
        }
    }

    [Fact]
    public async Task ConsentAndTranscriptAreRequiredBeforeReadingSource()
    {
        using var fixture = new Fixture();
        File.Delete(fixture.Source);
        var permission = await Assert.ThrowsAsync<ContractException>(() =>
            fixture.Library.ImportAsync(fixture.Request with { LocalStorageConfirmed = false }));
        Assert.Contains("Confirm voice rights", permission.Message);
        await Assert.ThrowsAsync<ContractException>(() =>
            fixture.Library.ImportAsync(fixture.Request with { Transcript = " " }));
        await Assert.ThrowsAsync<ContractException>(() =>
            fixture.Library.ImportAsync(fixture.Request with { Engine = (VoiceEngine)500 }));
        await Assert.ThrowsAsync<ContractException>(() =>
            fixture.Library.ImportAsync(fixture.Request with { Rights = (VoiceRightsBasis)500 }));
        Assert.False(Directory.Exists(fixture.LibraryPath));
    }

    [Fact]
    public async Task PreCanceledImportDoesNotWrite()
    {
        using var fixture = new Fixture();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.Library.ImportAsync(fixture.Request, new CancellationToken(true)));
        Assert.False(Directory.Exists(fixture.LibraryPath));
    }

    [Fact]
    public async Task CancelAtCommitRemovesOnlyOwnedStaging()
    {
        using var fixture = new Fixture();
        using var stop = new CancellationTokenSource();
        var prior = await fixture.Library.ImportAsync(fixture.Request);
        var library = new VoiceLibrary(fixture.LibraryPath) { BeforePublication = _ => stop.Cancel() };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => library.ImportAsync(fixture.Request, stop.Token));
        Assert.Equal(prior, Assert.Single(await fixture.Library.ListAsync()));
        Assert.Empty(Directory.GetFiles(fixture.LibraryPath, "*.pending"));
        Assert.True(File.Exists(fixture.Source));
    }

    [Fact]
    public async Task AssetLimitDoesNotOverwriteOrOrphanCopies()
    {
        using var fixture = new Fixture();
        await File.WriteAllBytesAsync(fixture.Source, Wave(1));
        for (var index = 0; index < VoiceLibrary.MaximumAssets; index++)
            await fixture.Library.ImportAsync(fixture.Request);
        await Assert.ThrowsAsync<ContractException>(() => fixture.Library.ImportAsync(fixture.Request));
        Assert.Equal(VoiceLibrary.MaximumAssets, (await fixture.Library.ListAsync()).Count);
        Assert.Empty(Directory.GetFiles(fixture.LibraryPath, "*.pending"));
    }

    [Theory]
    [InlineData("relative.wav")]
    [InlineData(@"\\localhost\share\private.wav")]
    public async Task NonlocalSourceRejectedBeforeRead(string source)
    {
        using var fixture = new Fixture();
        await Assert.ThrowsAsync<ContractException>(() =>
            fixture.Library.ImportAsync(fixture.Request with { SourcePath = source }));
        Assert.False(Directory.Exists(fixture.LibraryPath));
    }

    [Fact]
    public async Task OversizeRejectedWithoutReadingPayload()
    {
        using var fixture = new Fixture();
        using (var stream = File.OpenWrite(fixture.Source)) stream.SetLength(VoiceLibrary.MaximumAudioBytes + 1L);
        await Assert.ThrowsAsync<ContractException>(() => fixture.Library.ImportAsync(fixture.Request));
        Assert.False(Directory.Exists(fixture.LibraryPath));
    }

    [Fact]
    public async Task CorruptAudioFailsWithoutChangingSavedBundle()
    {
        using var fixture = new Fixture();
        var asset = await fixture.Library.ImportAsync(fixture.Request);
        using (var archive = ZipFile.Open(fixture.Bundle(asset), ZipArchiveMode.Update))
        {
            archive.GetEntry("audio.wav")!.Delete();
            using var output = archive.CreateEntry("audio.wav").Open();
            output.Write(Wave(2));
        }
        var original = await File.ReadAllBytesAsync(fixture.Bundle(asset));
        var error = await Assert.ThrowsAsync<ContractException>(() => fixture.Library.ListAsync());
        Assert.Contains("integrity", error.Message);
        Assert.Equal(original, await File.ReadAllBytesAsync(fixture.Bundle(asset)));
    }

    [Fact]
    public async Task NewerSchemaAndMissingFieldsFailWithoutReplacement()
    {
        using var fixture = new Fixture();
        var asset = await fixture.Library.ImportAsync(fixture.Request);
        fixture.EditManifest(asset, json => json["version"] = 2);
        var original = await File.ReadAllBytesAsync(fixture.Bundle(asset));
        var error = await Assert.ThrowsAsync<ContractException>(() => fixture.Library.ListAsync());
        Assert.Equal(ErrorCode.UnsupportedVersion, error.Code);
        Assert.Equal(original, await File.ReadAllBytesAsync(fixture.Bundle(asset)));
        fixture.EditManifest(asset, json => { json["version"] = 1; json.Remove("rights"); });
        await Assert.ThrowsAsync<ContractException>(() => fixture.Library.ListAsync());
    }

    [Fact]
    public async Task RemovalRequiresUnchangedInspection()
    {
        using var fixture = new Fixture();
        var asset = await fixture.Library.ImportAsync(fixture.Request);
        fixture.EditManifest(asset, json => json["name"] = "Changed");
        await Assert.ThrowsAsync<ContractException>(() => fixture.Library.DeleteAsync(asset));
        Assert.True(File.Exists(fixture.Bundle(asset)));
        await fixture.Library.DeleteAsync(Assert.Single(await fixture.Library.ListAsync()));
    }

    [Fact]
    public async Task WriterOwnershipAndInterruptedStageFailExplicitly()
    {
        using var fixture = new Fixture();
        var asset = await fixture.Library.ImportAsync(fixture.Request);
        using (var held = new FileStream(Path.Combine(fixture.LibraryPath, ".writer.lock"),
            FileMode.Open, FileAccess.Write, FileShare.None))
            await Assert.ThrowsAsync<IOException>(() => fixture.Library.ImportAsync(fixture.Request));
        var pending = Path.Combine(fixture.LibraryPath, "interrupted.pending");
        await File.WriteAllTextAsync(pending, "PRIVATE unfinished data");
        var error = await Assert.ThrowsAsync<ContractException>(() => fixture.Library.ListAsync());
        Assert.Contains("interrupted", error.Message);
        Assert.True(File.Exists(pending));
        Assert.True(File.Exists(fixture.Bundle(asset)));
        await Assert.ThrowsAsync<ContractException>(() => fixture.Library.ImportAsync(fixture.Request));
    }

    [Fact]
    public async Task WrongArchiveLayoutRejectedWithoutExtracting()
    {
        using var fixture = new Fixture();
        var asset = await fixture.Library.ImportAsync(fixture.Request);
        using (var archive = ZipFile.Open(fixture.Bundle(asset), ZipArchiveMode.Update))
        {
            using var output = new StreamWriter(archive.CreateEntry("../outside.txt").Open());
            output.Write("untrusted");
        }
        await Assert.ThrowsAsync<ContractException>(() => fixture.Library.ListAsync());
        Assert.False(File.Exists(Path.Combine(fixture.Root, "outside.txt")));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void InvalidWaveStructureRejected(int mutation)
    {
        var bytes = Wave(1);
        switch (mutation)
        {
            case 0: bytes[0] = 0; break;
            case 1: bytes[22] = 2; break;
            case 2: bytes[34] = 32; break;
            case 3: BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(40), uint.MaxValue); break;
            case 4: bytes[4] = 0; break;
            case 5: Array.Clear(bytes, 44, bytes.Length - 44); break;
        }
        Assert.Throws<ContractException>(() => PcmWaveInfo.Inspect(bytes, VoiceLibrary.MaximumAudioBytes));
    }

    [Theory]
    [InlineData(16_000)]
    [InlineData(22_050)]
    [InlineData(24_000)]
    [InlineData(44_100)]
    [InlineData(48_000)]
    public void AllDocumentedRatesAreAccepted(int rate)
    {
        var info = PcmWaveInfo.Inspect(Wave(1, rate), VoiceLibrary.MaximumAudioBytes);
        Assert.Equal(rate, info.SampleRate);
        Assert.Equal(1000, info.DurationMilliseconds);
    }

    [Fact]
    public void PerEngineGuidanceDistinguishesCloningAndTraining()
    {
        Assert.Contains("strictly longer", VoiceEngineCatalog.DescribePreparation(
            VoiceEngine.Chatterbox, VoiceAssetPurpose.Reference, new(16_000, 80_000)));
        Assert.Contains("initial preparation", VoiceEngineCatalog.DescribePreparation(
            VoiceEngine.Chatterbox, VoiceAssetPurpose.Reference, new(16_000, 80_001)));
        Assert.Contains("3 and 10", VoiceEngineCatalog.DescribePreparation(
            VoiceEngine.GptSoVits, VoiceAssetPurpose.Reference, new(16_000, 160_001)));
        Assert.Contains("unavailable", VoiceEngineCatalog.DescribePreparation(
            VoiceEngine.Chatterbox, VoiceAssetPurpose.TrainingMaterial, new(16_000, 160_000)));
    }

    internal static byte[] Wave(int seconds, int rate = 16_000)
    {
        var data = new byte[44 + rate * seconds * 2];
        "RIFF"u8.CopyTo(data);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(4), data.Length - 8);
        "WAVEfmt "u8.CopyTo(data.AsSpan(8));
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(16), 16);
        BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(20), 1);
        BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(22), 1);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(24), rate);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(28), rate * 2);
        BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(32), 2);
        BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(34), 16);
        "data"u8.CopyTo(data.AsSpan(36));
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(40), data.Length - 44);
        data[44] = 1;
        return data;
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "Martlet.Voices." + Guid.NewGuid().ToString("N"));
        public string LibraryPath => Path.Combine(Root, "library");
        public string Source => Path.Combine(Root, "source.wav");
        public VoiceLibrary Library { get; }
        public VoiceImportRequest Request => new("PRIVATE voice", Source, "PRIVATE synthetic transcript",
            VoiceEngine.F5Tts, VoiceAssetPurpose.Reference, VoiceRightsBasis.OwnVoice, true);
        public Fixture()
        {
            Directory.CreateDirectory(Root);
            File.WriteAllBytes(Source, Wave(10));
            Library = new VoiceLibrary(LibraryPath);
        }
        public string Bundle(VoiceAsset asset) => Path.Combine(LibraryPath, $"{asset.Id:N}.voice");
        public void EditManifest(VoiceAsset asset, Action<JsonObject> edit)
        {
            using var archive = ZipFile.Open(Bundle(asset), ZipArchiveMode.Update);
            JsonObject json;
            using (var reader = new StreamReader(archive.GetEntry("manifest.json")!.Open()))
                json = JsonNode.Parse(reader.ReadToEnd())!.AsObject();
            edit(json);
            archive.GetEntry("manifest.json")!.Delete();
            using var writer = new StreamWriter(archive.CreateEntry("manifest.json").Open());
            writer.Write(json.ToJsonString());
        }
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
