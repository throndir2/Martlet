using System.Reflection;
using System.Buffers.Binary;
using Martlet.F5;

namespace Martlet.F5.Tests;

public sealed class ContractAndAudioTests
{
    [Fact]
    public void Fixture_identity_is_exact_and_truthfully_fixture_only()
    {
        var identity = DeterministicF5FixtureIdentity.Create();

        Assert.Equal(F5EvidenceKind.SyntheticFixture, identity.Evidence);
        Assert.Equal(F5ProtocolVersion.Current, identity.ProtocolVersion);
        Assert.Equal(F5WorkerProtocol.ContractId, identity.ContractId);
        Assert.True(identity.PcmTransportStreaming);
        Assert.False(identity.IncrementalWithinChunkSynthesis);
        Assert.Equal(Enum.GetValues<F5ArtifactRole>(), identity.Artifacts.Select(a => a.Role));
        Assert.All(identity.Artifacts, artifact =>
        {
            Assert.Equal(40, artifact.Revision.Length);
            Assert.Equal(64, artifact.Sha256.Length);
            Assert.True(artifact.Bytes > 0);
        });
    }

    [Fact]
    public void Identity_rejects_missing_artifacts_floating_versions_and_non_hashes()
    {
        var valid = DeterministicF5FixtureIdentity.Create();
        Assert.Throws<F5Exception>(() => new F5WorkerIdentity(
            valid.WorkerId,
            valid.Evidence,
            valid.Runtime,
            valid.Artifacts.Take(4),
            valid.Cancellation));

        var floating = valid.Runtime with { F5PackageVersion = "latest" };
        Assert.Throws<F5Exception>(() => new F5WorkerIdentity(
            valid.WorkerId,
            valid.Evidence,
            floating,
            valid.Artifacts,
            valid.Cancellation));

        var invalidArtifact = valid.Artifacts[0] with { Sha256 = "not-a-hash" };
        Assert.Throws<F5Exception>(() => new F5WorkerIdentity(
            valid.WorkerId,
            valid.Evidence,
            valid.Runtime,
            [invalidArtifact, .. valid.Artifacts.Skip(1)],
            valid.Cancellation));
    }

    [Fact]
    public void Public_contract_has_no_generic_compatibility_or_enable_fallback_surface()
    {
        var publicNames = typeof(F5GatewayClientAdapter).Assembly.ExportedTypes
            .SelectMany(type => new[] { type.FullName ?? string.Empty }
                .Concat(type.GetMembers(BindingFlags.Public | BindingFlags.Instance |
                    BindingFlags.Static).Select(member => member.Name)))
            .ToArray();
        Assert.DoesNotContain(publicNames,
            name => name.Contains("OpenAI", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Whisper", StringComparison.OrdinalIgnoreCase));

        var policy = typeof(F5ExecutionPolicy)
            .GetProperty("Strict", BindingFlags.NonPublic | BindingFlags.Static)!
            .GetValue(null) as F5ExecutionPolicy;
        Assert.NotNull(policy);
        Assert.True(policy!.ReferenceTranscriptSupplied);
        Assert.False(policy.AutomaticTranscriptionAllowed);
        Assert.False(policy.ArtifactDownloadAllowed);
        Assert.False(policy.DefaultVoiceAllowed);
        Assert.False(policy.ModelFallbackAllowed);
        Assert.False(policy.ProviderFallbackAllowed);
        Assert.Empty(typeof(F5ExecutionPolicy).GetConstructors(BindingFlags.Public |
            BindingFlags.Instance));
    }

    [Theory]
    [InlineData("compressed")]
    [InlineData("silent")]
    [InlineData("short")]
    [InlineData("long")]
    [InlineData("unsupported-rate")]
    [InlineData("malformed")]
    public async Task Malformed_or_unusable_audio_is_rejected_without_store_state(string kind)
    {
        using var scope = new F5TestScope();
        if (kind == "malformed")
            await File.WriteAllBytesAsync(scope.SourcePath, "not-wave"u8.ToArray());
        else
            F5TestData.WriteWav(
                scope.SourcePath,
                formatTag: kind == "compressed" ? (ushort)3 : (ushort)1,
                silence: kind == "silent",
                sampleRate: kind == "unsupported-rate" ? 32_000 : 24_000,
                durationMilliseconds: kind == "short" ? 999 :
                    kind == "long" ? 30_001 : 1_000);
        using var store = scope.OpenStore();

        await F5TestData.FailureAsync(F5Failure.InvalidAudio, async () =>
            await F5TestData.SnapshotAsync(store, scope.SourcePath));
        Assert.Empty(store.Inspect().Presets);
        Assert.False(File.Exists(Path.Combine(scope.StoreDirectory,
            F5ReferencePresetStore.StoreFileName)));
    }

    [Fact]
    public async Task Oversized_audio_and_non_absolute_paths_fail_before_snapshot()
    {
        using var scope = new F5TestScope();
        await File.WriteAllBytesAsync(scope.SourcePath,
            new byte[F5ReferenceLimits.MaximumAudioFileBytes + 1]);
        using var store = scope.OpenStore();

        await F5TestData.FailureAsync(F5Failure.LimitExceeded, async () =>
            await F5TestData.SnapshotAsync(store, scope.SourcePath));
        await F5TestData.FailureAsync(F5Failure.InvalidPath, async () =>
            await F5TestData.SnapshotAsync(store, "relative.wav"));
        await F5TestData.FailureAsync(F5Failure.InvalidPath, async () =>
            await F5TestData.SnapshotAsync(store,
                Path.GetPathRoot(scope.Root)! + new string('a',
                    F5ReferenceLimits.MaximumSourcePathCharacters) + ".wav"));
        Assert.Empty(store.Inspect().Presets);
    }

    [Fact]
    public async Task Rights_and_supplied_transcript_are_mandatory()
    {
        using var scope = new F5TestScope();
        F5TestData.WriteWav(scope.SourcePath);
        using var store = scope.OpenStore();
        var denied = new F5ReferenceSnapshotRequest
        {
            PresetName = "Denied",
            AbsoluteSourcePath = scope.SourcePath,
            Transcript = "Matching text.",
            Rights = F5TestData.Rights(confirmed: false)
        };
        await F5TestData.FailureAsync(F5Failure.RightsRequired,
            async () => await store.SnapshotAsync(denied));
        await Assert.ThrowsAsync<F5Exception>(async () => await store.SnapshotAsync(denied with
        {
            Transcript = " ",
            Rights = F5TestData.Rights()
        }));
        Assert.Empty(store.Inspect().Presets);
    }

    [Fact]
    public void Text_chunks_are_contiguous_unique_and_bounded()
    {
        Assert.Throws<F5Exception>(() => new F5TextChunk(
            0, "chunk", new string('x', F5WorkerProtocol.MaximumChunkCharacters + 1)));
        var identity = DeterministicF5FixtureIdentity.Create();
        Assert.Throws<F5Exception>(() => new F5ConversationSynthesis(
            F5TestData.Ids(),
            F5TestData.Destination,
            identity,
            new string('a', 64),
            F5TestData.Now.AddSeconds(10),
            [
                new F5TextChunk(0, "same", "first"),
                new F5TextChunk(1, "same", "second")
            ]));
        Assert.Throws<F5Exception>(() => new F5ConversationSynthesis(
            F5TestData.Ids(),
            F5TestData.Destination,
            identity,
            new string('a', 64),
            F5TestData.Now.AddSeconds(10),
            [new F5TextChunk(1, "chunk", "out of order")]));
    }

    [Fact]
    public async Task Oversized_wav_chunk_length_is_typed_invalid_audio()
    {
        using var scope = new F5TestScope();
        var bytes = new byte[44];
        "RIFF"u8.CopyTo(bytes);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4, 4), bytes.Length - 8);
        "WAVE"u8.CopyTo(bytes.AsSpan(8));
        "fmt "u8.CopyTo(bytes.AsSpan(12));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16, 4), uint.MaxValue);
        await File.WriteAllBytesAsync(scope.SourcePath, bytes);
        using var store = scope.OpenStore();

        await F5TestData.FailureAsync(F5Failure.InvalidAudio, async () =>
            await F5TestData.SnapshotAsync(store, scope.SourcePath));
    }
}
