using System.Text;
using Martlet.LocalStt;

namespace Martlet.LocalStt.Tests;

public sealed class AudioAndManifestTests
{
    [Fact]
    public void Canonical_audio_is_copied_bounded_hashed_and_unusable_after_disposal()
    {
        var source = LocalSttTestData.Wave(400);
        var audio = CanonicalWaveAudio.FromWave(source);
        source[44] ^= 0x7f;

        Assert.Equal(844, audio.ByteLength);
        Assert.Equal(400, audio.SamplesPerChannel);
        Assert.Equal(TimeSpan.FromMilliseconds(25), audio.Duration);
        Assert.Equal(64, audio.Sha256.Length);
        audio.Dispose();
        Assert.Throws<ObjectDisposedException>(() => _ = audio.ByteLength);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(20)]
    [InlineData(22)]
    [InlineData(24)]
    [InlineData(32)]
    [InlineData(34)]
    [InlineData(40)]
    public void Noncanonical_wav_fields_are_rejected(int offset)
    {
        var bytes = LocalSttTestData.Wave();
        bytes[offset] ^= 1;
        var error = Assert.Throws<LocalSttContractException>(() =>
            CanonicalWaveAudio.FromWave(bytes));
        Assert.Equal(LocalSttFailureCode.AudioFormatUnsupported, error.Failure.Code);
    }

    [Fact]
    public void Audio_bounds_are_exact()
    {
        using var maximum = CanonicalWaveAudio.FromWave(
            LocalSttTestData.Wave(CanonicalWaveAudio.MaximumPcmBytes / 2));
        Assert.Equal(CanonicalWaveAudio.MaximumWaveBytes, maximum.ByteLength);
        var tooLarge = LocalSttTestData.Wave(CanonicalWaveAudio.MaximumPcmBytes / 2 + 1);
        var error = Assert.Throws<LocalSttContractException>(() =>
            CanonicalWaveAudio.FromWave(tooLarge));
        Assert.Equal(LocalSttFailureCode.AudioLimit, error.Failure.Code);
    }

    [Fact]
    public void Committed_manifest_is_disabled_bounded_and_exactly_pinned()
    {
        var manifest = LocalSttPackageManifest.Current;
        Assert.Equal(LocalSttCandidateStatus.DisabledPendingQualification, manifest.Status);
        Assert.Equal("win-x64", manifest.Target);
        Assert.Equal(LocalSttNetworkPolicy.NoNetwork, manifest.NetworkPolicy);
        Assert.Equal("en", manifest.Language);
        Assert.Equal(64, manifest.DocumentSha256.Length);
        Assert.Equal(64, manifest.ModelSha256.Length);
        Assert.InRange(manifest.MaximumProvisioningBytes, 1, 2_147_483_648);
        Assert.Equal(CanonicalWaveAudio.MaximumWaveBytes,
            manifest.Document.Execution.MaximumAudioBytes);
        Assert.Equal(LocalSttPackageManifest.MaximumTranscriptCharacters,
            manifest.Document.Execution.MaximumTranscriptCharacters);
    }

    [Fact]
    public void Manifest_rejects_unknown_duplicate_case_aliased_and_oversized_input()
    {
        var current = ReadEmbedded();
        var unknown = current.Replace(
            "\"format_version\": 1,",
            "\"format_version\": 1, \"unknown\": true,",
            StringComparison.Ordinal);
        var duplicate = current.Replace(
            "\"format_version\": 1,",
            "\"format_version\": 1, \"format_version\": 1,",
            StringComparison.Ordinal);
        var aliased = current.Replace("\"format_version\"", "\"Format_Version\"", StringComparison.Ordinal);

        AssertInvalid(unknown);
        AssertInvalid(duplicate);
        AssertInvalid(aliased);
        Assert.Throws<LocalSttContractException>(() =>
            LocalSttPackageManifest.Read(new byte[LocalSttPackageManifest.MaximumDocumentBytes + 1]));
    }

    [Fact]
    public void Manifest_rejects_wrong_language_model_policy_and_bounds()
    {
        var current = ReadEmbedded();
        AssertInvalid(current.Replace("\"language\": \"en\"", "\"language\": \"fr\"", StringComparison.Ordinal));
        AssertInvalid(current.Replace("\"network_policy\": \"no_network\"",
            "\"network_policy\": \"loopback_only\"", StringComparison.Ordinal));
        AssertInvalid(current.Replace("\"maximum_transcript_characters\": 4096",
            "\"maximum_transcript_characters\": 4097", StringComparison.Ordinal));
        AssertInvalid(current.Replace("\"status\": \"disabled_pending_qualification\"",
            "\"status\": 0", StringComparison.Ordinal));
    }

    [Fact]
    public void Manifest_explicit_nulls_are_normalized_to_package_invalid()
    {
        var current = ReadEmbedded();
        AssertInvalid(current.Replace(
            "\"id\": \"whisper-cpp-v1.9.2-base.en-win-x64-cpu\"",
            "\"id\": null",
            StringComparison.Ordinal));
        var runtimeStart = current.IndexOf("\"runtime\": {", StringComparison.Ordinal);
        var modelStart = current.IndexOf("\"model\": {", StringComparison.Ordinal);
        Assert.True(runtimeStart > 0 && modelStart > runtimeStart);
        AssertInvalid(string.Concat(
            current.AsSpan(0, runtimeStart),
            "\"runtime\": null,\n  ",
            current.AsSpan(modelStart)));
        AssertInvalid(current.Replace(
            "\"files\": [",
            "\"files\": [ null,",
            StringComparison.Ordinal));
    }

    private static string ReadEmbedded()
    {
        using var stream = typeof(LocalSttPackageManifest).Assembly
            .GetManifestResourceStream("Martlet.LocalStt.whisper-package.v1.json")!;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static void AssertInvalid(string json)
    {
        var error = Assert.Throws<LocalSttContractException>(() =>
            LocalSttPackageManifest.Read(Encoding.UTF8.GetBytes(json)));
        Assert.Equal(LocalSttFailureCode.PackageInvalid, error.Failure.Code);
    }
}
