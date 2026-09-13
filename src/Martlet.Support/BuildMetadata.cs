using System.Text.Json.Serialization;
using Martlet.Core.Contracts;
using Martlet.Diagnostics;

namespace Martlet.Support;

public enum BuildSource { ExecutingAssemblies, SuppliedPayloadManifest }
public enum BuildTarget { Portable, WindowsX64 }

// Sealed and constructor-controlled: no arbitrary caller labels, package lists or manifest paths.
public sealed class BuildMetadata
{
    public int SchemaVersion => 1;
    public BuildSource Source { get; }
    public BuildTarget Target { get; }
    public string ApplicationVersion { get; }
    public string? SdkVersion { get; }
    public string RuntimeVersion { get; }
    public string? SourceCommit { get; }
    public bool? SourceDirty { get; }
    public int? InventoriedFileCount { get; }
    public bool PublisherSignatureVerified => false;

    private BuildMetadata(BuildSource source, BuildTarget target, string applicationVersion, string? sdkVersion,
        string runtimeVersion, string? sourceCommit = null, bool? sourceDirty = null, int? count = null)
    {
        Source = source; Target = target; ApplicationVersion = applicationVersion; SdkVersion = sdkVersion;
        RuntimeVersion = runtimeVersion; SourceCommit = sourceCommit; SourceDirty = sourceDirty; InventoriedFileCount = count;
    }

    public static BuildMetadata FromExecutingAssemblies() => new(BuildSource.ExecutingAssemblies, BuildTarget.Portable,
        FoundationStatusService.ApplicationVersion, null, Environment.Version.ToString());

    // Accept the existing packaging manifest BYTES, never a file/directory to crawl.
    public static BuildMetadata FromPayloadManifest(ReadOnlyMemory<byte> manifest)
    {
        var value = SupportJson.Read<PayloadManifest>(manifest);
        return new(BuildSource.SuppliedPayloadManifest, BuildTarget.WindowsX64, value.ApplicationVersion,
            value.SdkVersion, value.RuntimeVersion, value.SourceCommit, value.SourceDirty, value.Files.Length);
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record PayloadFile : IContract
{
    [JsonPropertyName("path")] public required string Path { get; init; }
    [JsonPropertyName("bytes")] public required long Bytes { get; init; }
    [JsonPropertyName("sha256")] public required string Sha256 { get; init; }
    public void Validate()
    {
        // The path is checked only as a bounded source field. It is never opened or exported.
        Guard.Require(Path is { Length: > 0 and <= 1024 } && Bytes is >= 0 and <= 4L * 1024 * 1024 * 1024);
        Guard.Require(Sha256 is { Length: 64 } && Sha256.All(char.IsAsciiHexDigit));
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record PayloadManifest : IContract
{
    [JsonPropertyName("schemaVersion")] public required int SchemaVersion { get; init; }
    [JsonPropertyName("channel")] public required string Channel { get; init; }
    [JsonPropertyName("applicationVersion")] public required string ApplicationVersion { get; init; }
    [JsonPropertyName("rid")] public required string Rid { get; init; }
    [JsonPropertyName("sdkVersion")] public required string SdkVersion { get; init; }
    [JsonPropertyName("runtimeVersion")] public required string RuntimeVersion { get; init; }
    [JsonPropertyName("sourceCommit")] public required string SourceCommit { get; init; }
    [JsonPropertyName("sourceDirty")] public required bool SourceDirty { get; init; }
    [JsonPropertyName("files")] public required PayloadFile[] Files { get; init; }
    public void Validate()
    {
        Guard.Require(SchemaVersion == 1, SupportFailure.UnsupportedVersion);
        Guard.Require(Channel == "INTERNAL DEVELOPMENT ONLY - UNSIGNED" && Rid == "win-x64");
        Guard.Version(ApplicationVersion); Guard.Version(SdkVersion); Guard.Version(RuntimeVersion);
        Guard.Require(SourceCommit is { Length: 40 } && SourceCommit.All(char.IsAsciiHexDigit));
        Guard.Require(Files is { Length: > 0 and <= 4096 });
        foreach (var file in Files!) { Guard.Require(file is not null); file!.Validate(); }
        Guard.Require(Files.Select(f => f.Path).Distinct(StringComparer.Ordinal).Count() == Files.Length);
    }
}
