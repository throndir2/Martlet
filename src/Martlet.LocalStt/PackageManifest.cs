using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Martlet.LocalStt;

public enum LocalSttCandidateStatus
{
    DisabledPendingQualification
}

public enum LocalSttNetworkPolicy
{
    NoNetwork,
    LoopbackOnly
}

internal enum RuntimeFilePurpose
{
    Executable,
    Dependency
}

internal enum RightsDisposition
{
    Unreviewed
}

internal enum ArtifactHashEvidence
{
    GithubReleaseMetadata,
    HuggingFaceLfsMetadata
}

internal enum RuntimeLayoutEvidence
{
    SourceWorkflowUnverified
}

public sealed class LocalSttPackageManifest
{
    public const int MaximumDocumentBytes = 65_536;
    public const int MaximumTranscriptBytes = 16_384;
    public const int MaximumTranscriptCharacters = 4_096;
    public const int MaximumStandardOutputBytes = 4_096;
    public const int MaximumStandardErrorBytes = 8_192;
    public static TimeSpan MaximumActionLifetime => TimeSpan.FromSeconds(30);
    public static TimeSpan ProcessCleanupTimeout => TimeSpan.FromSeconds(5);

    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    internal ManifestDocument Document { get; }
    public string DocumentSha256 { get; }
    public string Id => Document.Id;
    public string ModelId => Document.Model.Id;
    public string ModelSha256 => Document.Model.Sha256;
    public string Language => Document.Model.Language;
    public string Target => Document.Target;
    public LocalSttCandidateStatus Status => Document.Status;
    public LocalSttNetworkPolicy NetworkPolicy => Document.Execution.NetworkPolicy;
    public long MaximumProvisioningBytes => Document.Provisioning.MaximumStagingBytes;

    private LocalSttPackageManifest(ManifestDocument document, string documentSha256)
    {
        Document = document;
        DocumentSha256 = documentSha256;
    }

    public static LocalSttPackageManifest Current { get; } = LoadCurrent();

    public static LocalSttPackageManifest Read(ReadOnlyMemory<byte> bytes)
    {
        if (bytes.IsEmpty || bytes.Length > MaximumDocumentBytes)
            throw new LocalSttContractException(LocalSttFailureCode.PackageInvalid);
        var owned = bytes.ToArray();
        try
        {
            RejectDuplicateProperties(owned);
            var document = JsonSerializer.Deserialize<ManifestDocument>(owned, JsonOptions)
                ?? throw new JsonException();
            document.Validate();
            return new(document, Convert.ToHexStringLower(SHA256.HashData(owned)));
        }
        catch (Exception error) when (error is JsonException or OverflowException or
            InvalidOperationException or NullReferenceException)
        {
            throw new LocalSttContractException(LocalSttFailureCode.PackageInvalid);
        }
    }

    private static LocalSttPackageManifest LoadCurrent()
    {
        using var stream = typeof(LocalSttPackageManifest).Assembly
            .GetManifestResourceStream("Martlet.LocalStt.whisper-package.v1.json")
            ?? throw new InvalidDataException("The local STT package manifest is missing.");
        if (stream.Length is <= 0 or > MaximumDocumentBytes)
            throw new InvalidDataException("The local STT package manifest has an invalid size.");
        var bytes = new byte[stream.Length];
        stream.ReadExactly(bytes);
        try
        {
            return Read(bytes);
        }
        catch (LocalSttContractException error)
        {
            throw new InvalidDataException("The local STT package manifest is invalid.", error);
        }
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            RespectNullableAnnotations = true,
            RespectRequiredConstructorParameters = true,
            MaxDepth = 12,
            AllowTrailingCommas = false,
            ReadCommentHandling = JsonCommentHandling.Disallow
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false));
        return options;
    }

    private static void RejectDuplicateProperties(ReadOnlySpan<byte> bytes)
    {
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 12
        });
        var objects = new Stack<HashSet<string>>();
        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.StartObject:
                    objects.Push(new(StringComparer.Ordinal));
                    break;
                case JsonTokenType.EndObject:
                    objects.Pop();
                    break;
                case JsonTokenType.PropertyName:
                    if (objects.Count == 0 || !objects.Peek().Add(reader.GetString()!))
                        throw new JsonException();
                    break;
            }
        }
        if (objects.Count != 0)
            throw new JsonException();
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ManifestDocument
{
    public required int FormatVersion { get; init; }
    public required string Kind { get; init; }
    public required string Id { get; init; }
    public required LocalSttCandidateStatus Status { get; init; }
    public required string Target { get; init; }
    public required RuntimeDocument Runtime { get; init; }
    public required ModelDocument Model { get; init; }
    public required ExecutionDocument Execution { get; init; }
    public required ProvisioningDocument Provisioning { get; init; }
    public required RightsDocument Rights { get; init; }

    internal void Validate()
    {
        ManifestRules.Require(FormatVersion == 1 && Kind == "local_stt_package_candidate");
        ManifestRules.Id(Id);
        ManifestRules.Require(Status == LocalSttCandidateStatus.DisabledPendingQualification &&
            Target == "win-x64");
        Runtime.Validate();
        Model.Validate();
        Execution.Validate(this);
        Provisioning.Validate(this);
        Rights.Validate();
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record RuntimeDocument
{
    public required string Repository { get; init; }
    public required string Revision { get; init; }
    public required string ReleaseTag { get; init; }
    public required long ReleaseId { get; init; }
    public required string ArchiveFileName { get; init; }
    public required long AssetId { get; init; }
    public required long ArchiveBytes { get; init; }
    public required string ArchiveSha256 { get; init; }
    public required ArtifactHashEvidence ArchiveSha256Evidence { get; init; }
    public required string ArchiveUrl { get; init; }
    public required RuntimeLayoutEvidence FileLayoutEvidence { get; init; }
    public required RuntimeFileDocument[] Files { get; init; }

    internal void Validate()
    {
        ManifestRules.Require(Repository == "ggml-org/whisper.cpp" &&
            Revision.Length == 40 && Revision.All(ManifestRules.IsLowerHex) &&
            ReleaseTag.Length is >= 2 and <= 32 && ReleaseTag[0] == 'v' &&
            ReleaseId > 0 && AssetId > 0 && ArchiveBytes is > 0 and <= 536_870_912);
        ManifestRules.Require(ArchiveSha256Evidence == ArtifactHashEvidence.GithubReleaseMetadata &&
            FileLayoutEvidence == RuntimeLayoutEvidence.SourceWorkflowUnverified);
        ManifestRules.FileName(ArchiveFileName, ".zip");
        ManifestRules.Sha256(ArchiveSha256);
        ManifestRules.HttpsUrl(ArchiveUrl, "github.com");
        ManifestRules.Require(Files.Length is >= 1 and <= 16 &&
            Files.Count(file => file.Purpose == RuntimeFilePurpose.Executable) == 1);
        var archivePaths = new HashSet<string>(StringComparer.Ordinal);
        var installedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Files)
        {
            file.Validate();
            ManifestRules.Require(archivePaths.Add(file.ArchiveEntry) &&
                installedNames.Add(file.InstalledName));
        }
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record RuntimeFileDocument
{
    public required RuntimeFilePurpose Purpose { get; init; }
    public required string ArchiveEntry { get; init; }
    public required string InstalledName { get; init; }

    internal void Validate()
    {
        ManifestRules.RelativeArchivePath(ArchiveEntry);
        ManifestRules.FileName(InstalledName, Purpose == RuntimeFilePurpose.Executable ? ".exe" : ".dll");
        ManifestRules.Require(string.Equals(Path.GetFileName(ArchiveEntry), InstalledName, StringComparison.Ordinal));
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ModelDocument
{
    public required string Id { get; init; }
    public required string Repository { get; init; }
    public required string Revision { get; init; }
    public required string FileName { get; init; }
    public required long Bytes { get; init; }
    public required string Sha256 { get; init; }
    public required ArtifactHashEvidence Sha256Evidence { get; init; }
    public required string SourceUrl { get; init; }
    public required string Language { get; init; }

    internal void Validate()
    {
        ManifestRules.Id(Id);
        ManifestRules.Require(Repository == "ggerganov/whisper.cpp" &&
            Revision.Length == 40 && Revision.All(ManifestRules.IsLowerHex) &&
            Bytes is > 0 and <= 536_870_912 && Language == "en");
        ManifestRules.FileName(FileName, ".bin");
        ManifestRules.Sha256(Sha256);
        ManifestRules.Require(Sha256Evidence == ArtifactHashEvidence.HuggingFaceLfsMetadata);
        ManifestRules.HttpsUrl(SourceUrl, "huggingface.co");
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ExecutionDocument
{
    public required string Executable { get; init; }
    public required string InputFormat { get; init; }
    public required string InputMode { get; init; }
    public required string OutputMode { get; init; }
    public required LocalSttNetworkPolicy NetworkPolicy { get; init; }
    public required int Threads { get; init; }
    public required int Processors { get; init; }
    public required int TimeoutSeconds { get; init; }
    public required int MaximumAudioBytes { get; init; }
    public required int MaximumAudioSeconds { get; init; }
    public required int MaximumTranscriptBytes { get; init; }
    public required int MaximumTranscriptCharacters { get; init; }
    public required int MaximumStandardOutputBytes { get; init; }
    public required int MaximumStandardErrorBytes { get; init; }

    internal void Validate(ManifestDocument document)
    {
        ManifestRules.Require(document.Runtime.Files.Any(file =>
                file.Purpose == RuntimeFilePurpose.Executable && file.InstalledName == Executable) &&
            InputFormat == "wav_pcm16_mono_16000" &&
            InputMode == "ephemeral_file" &&
            OutputMode == "utf8_text_file" &&
            NetworkPolicy == LocalSttNetworkPolicy.NoNetwork &&
            Threads == 4 && Processors == 1 &&
            TimeoutSeconds == (int)LocalSttPackageManifest.MaximumActionLifetime.TotalSeconds &&
            MaximumAudioBytes == CanonicalWaveAudio.MaximumWaveBytes &&
            MaximumAudioSeconds == (int)CanonicalWaveAudio.MaximumDuration.TotalSeconds &&
            MaximumTranscriptBytes == LocalSttPackageManifest.MaximumTranscriptBytes &&
            MaximumTranscriptCharacters == LocalSttPackageManifest.MaximumTranscriptCharacters &&
            MaximumStandardOutputBytes == LocalSttPackageManifest.MaximumStandardOutputBytes &&
            MaximumStandardErrorBytes == LocalSttPackageManifest.MaximumStandardErrorBytes);
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ProvisioningDocument
{
    public required int MaximumArchiveEntries { get; init; }
    public required long MaximumExpandedRuntimeBytes { get; init; }
    public required long MaximumDownloadBytes { get; init; }
    public required long MaximumStagingBytes { get; init; }
    public required string[] AllowedOrigins { get; init; }

    internal void Validate(ManifestDocument document)
    {
        var exactDownload = checked(document.Runtime.ArchiveBytes + document.Model.Bytes);
        ManifestRules.Require(MaximumArchiveEntries is >= 1 and <= 64 &&
            MaximumArchiveEntries >= document.Runtime.Files.Length &&
            MaximumExpandedRuntimeBytes is > 0 and <= 1_073_741_824 &&
            MaximumDownloadBytes == exactDownload &&
            MaximumStagingBytes >= checked(exactDownload + MaximumExpandedRuntimeBytes) &&
            MaximumStagingBytes <= 2_147_483_648 &&
            AllowedOrigins.SequenceEqual(["github.com", "huggingface.co"], StringComparer.Ordinal));
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record RightsDocument
{
    public required string RuntimeSpdx { get; init; }
    public required string ModelSpdx { get; init; }
    public required RightsDisposition Disposition { get; init; }
    public required string RuntimeEvidenceUrl { get; init; }
    public required string ModelEvidenceUrl { get; init; }

    internal void Validate()
    {
        ManifestRules.Require(RuntimeSpdx == "MIT" && ModelSpdx == "MIT" &&
            Disposition == RightsDisposition.Unreviewed);
        ManifestRules.HttpsUrl(RuntimeEvidenceUrl, "github.com");
        ManifestRules.HttpsUrl(ModelEvidenceUrl, "github.com");
    }
}

internal static class ManifestRules
{
    internal static void Require(bool condition)
    {
        if (!condition)
            throw new InvalidOperationException("Invalid local STT package manifest.");
    }

    internal static bool IsLowerHex(char value) =>
        value is >= '0' and <= '9' or >= 'a' and <= 'f';

    internal static void Id(string value) =>
        Require(value.Length is >= 1 and <= 64 &&
            char.IsAsciiLetterOrDigit(value[0]) &&
            value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-'));

    internal static void Sha256(string value) =>
        Require(value.Length == 64 && value.All(IsLowerHex));

    internal static void FileName(string value, string extension) =>
        Require(value.Length is >= 1 and <= 128 &&
            value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-') &&
            value.EndsWith(extension, StringComparison.OrdinalIgnoreCase) &&
            value is not "." and not "..");

    internal static void RelativeArchivePath(string value)
    {
        Require(value.Length is >= 1 and <= 512 && !value.Contains('\\') && !value.StartsWith('/') &&
            value.Split('/').All(segment => segment.Length is >= 1 and <= 128 &&
                segment is not "." and not ".." &&
                segment.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-')));
    }

    internal static void HttpsUrl(string value, string host)
    {
        Require(Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
            uri.Scheme == Uri.UriSchemeHttps && uri.Host == host && uri.IsDefaultPort &&
            string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Query) &&
            string.IsNullOrEmpty(uri.Fragment));
    }
}
