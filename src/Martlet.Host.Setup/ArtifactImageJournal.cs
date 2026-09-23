using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Martlet.HostArtifacts;

namespace Martlet.Host.Setup;

internal enum ArtifactImageJournalPurpose { OciImageAcquisition }
internal enum ArtifactImageJournalState
{
    Prepared, AcquiringMetadata, AcquiringContent, Interrupted, Failed,
    ClosureVerified, Publishing, Published, Quarantining, Quarantined
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ArtifactImageContentProgress
{
    public required string Digest { get; init; }
    public required ArtifactAcquisitionJournalState State { get; init; }
    public required string? Identity { get; init; }
    public required long Bytes { get; init; }
    public required string? Sha256 { get; init; }
    public required string? MediaType { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ArtifactImageOwnedFile
{
    public required string Key { get; init; }
    public required string Identity { get; init; }
    public required long Bytes { get; init; }
    public required string Sha256 { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ArtifactImageTree
{
    public required string RootIdentity { get; init; }
    public required Dictionary<string, string> Directories { get; init; }
    public required ArtifactImageOwnedFile[] Files { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ArtifactImageJournalDocument
{
    public required int FormatVersion { get; init; }
    public required ArtifactImageJournalPurpose Purpose { get; init; }
    public required Guid JournalId { get; init; }
    public required long Revision { get; init; }
    public required string SelectionFingerprint { get; init; }
    public required string InventoryFingerprint { get; init; }
    public required string PlanFingerprint { get; init; }
    public required string RightsFingerprint { get; init; }
    public required string SetupDesiredStateFingerprint { get; init; }
    public required Guid SetupJournalId { get; init; }
    public required string SetupJournalVersion { get; init; }
    public required int Supersessions { get; init; }
    public required string ParentIdentity { get; init; }
    public required string LeaseIdentity { get; init; }
    public required string LeaseNonce { get; init; }
    public required string? RootIdentity { get; init; }
    public required Dictionary<string, string> Directories { get; init; }
    public required ArtifactImageContentProgress[] Contents { get; init; }
    public required ArtifactImageOwnedFile[] PublicationFiles { get; init; }
    public required ArtifactImageTree? Quarantine { get; init; }
    public required ArtifactImageTree? RecoveryTree { get; init; }
    public required bool RecoveryWasPublished { get; init; }
    public required ArtifactImageJournalState State { get; init; }
    public required DateTimeOffset CreatedAtUtc { get; init; }
    public required DateTimeOffset UpdatedAtUtc { get; init; }
    public required ArtifactAcquisitionFailure? LastFailure { get; init; }
    public required string IntegritySha256 { get; init; }
}

internal static class ArtifactImageJournalCodec
{
    internal const int MaximumBytes = 524_288;
    private static readonly JsonSerializerOptions Options = CreateOptions();

    internal static byte[] Write(ArtifactImageJournalDocument document)
    {
        Validate(document);
        var hash = Convert.ToHexStringLower(SHA256.HashData(
            JsonSerializer.SerializeToUtf8Bytes(document with { IntegritySha256 = "" }, Options)));
        var bytes = JsonSerializer.SerializeToUtf8Bytes(document with { IntegritySha256 = hash }, Options);
        if (bytes.Length > MaximumBytes)
            throw Failure(ArtifactAcquisitionFailure.JournalTooLarge);
        return bytes;
    }

    internal static ArtifactImageJournalDocument Read(ReadOnlyMemory<byte> bytes)
    {
        if (bytes.Length > MaximumBytes) throw Failure(ArtifactAcquisitionFailure.JournalTooLarge);
        try
        {
            using var json = ArtifactImageJson.Parse(bytes, 16);
            var document = JsonSerializer.Deserialize<ArtifactImageJournalDocument>(bytes.Span, Options)
                ?? throw Failure();
            Validate(document);
            Fingerprint(document.IntegritySha256);
            var sealedBytes = Write(document);
            var canonical = JsonSerializer.SerializeToUtf8Bytes(document, Options);
            if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(sealedBytes), SHA256.HashData(canonical)))
                throw Failure();
            return document;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or ArgumentException or
            FormatException or ArtifactAcquisitionException or OverflowException)
        {
            throw Failure();
        }
    }

    internal static void ValidateBinding(ArtifactImageJournalDocument document, ArtifactAcquisitionSelection selection)
    {
        if (document.SelectionFingerprint != selection.Fingerprint ||
            document.InventoryFingerprint != selection.ImageContentInventory.Fingerprint ||
            document.Contents.Length != selection.ImageContentInventory.Contents.Length)
            throw Failure(ArtifactAcquisitionFailure.PlanChanged);
        foreach (var progress in document.Contents)
        {
            var content = selection.ImageContentInventory.Contents.SingleOrDefault(item => item.Digest == progress.Digest);
            if (content?.ExpectedBytes is not { } expected || progress.Bytes > expected ||
                progress.State is ArtifactAcquisitionJournalState.Verified or ArtifactAcquisitionJournalState.Finalized &&
                (progress.Bytes != expected || progress.Sha256 != progress.Digest[7..]))
                throw Failure();
            if (progress.MediaType is { } media && (content.Kind switch
                {
                    ArtifactImageContentKind.Index => media is not (HttpsArtifactImageTransport.OciIndex or HttpsArtifactImageTransport.DockerIndex),
                    ArtifactImageContentKind.Manifest => media is not (HttpsArtifactImageTransport.OciManifest or HttpsArtifactImageTransport.DockerManifest),
                    _ => true
                }) ||
                progress.Identity is not null && content.Kind is ArtifactImageContentKind.Index or ArtifactImageContentKind.Manifest &&
                progress.MediaType is null)
                throw Failure();
        }
    }

    private static void Validate(ArtifactImageJournalDocument document)
    {
        if (document.FormatVersion != 3 || document.Purpose != ArtifactImageJournalPurpose.OciImageAcquisition ||
            document.JournalId == Guid.Empty || document.SetupJournalId == Guid.Empty ||
            document.Revision is < 1 or >= long.MaxValue || document.Supersessions is < 0 or > 16 ||
            !Enum.IsDefined(document.State) || document.LastFailure is { } failure && !Enum.IsDefined(failure) ||
            document.CreatedAtUtc == default || document.UpdatedAtUtc < document.CreatedAtUtc)
            throw Failure();
        foreach (var fingerprint in new[] { document.SelectionFingerprint, document.InventoryFingerprint,
            document.PlanFingerprint, document.RightsFingerprint, document.SetupDesiredStateFingerprint,
            document.SetupJournalVersion, document.LeaseNonce })
            Fingerprint(fingerprint);
        Identity(document.ParentIdentity);
        Identity(document.LeaseIdentity);
        if (document.RootIdentity is { } rootIdentity) Identity(rootIdentity);
        Directories(document.Directories);
        if (document.Contents is not { Length: > 0 and <= 264 } ||
            document.Contents.Any(content => content is null) ||
            document.Contents.Select(content => content.Digest).Distinct(StringComparer.Ordinal).Count() != document.Contents.Length)
            throw Failure();
        foreach (var content in document.Contents)
        {
            ArtifactImageMetadata.Digest(content.Digest);
            if (content.Bytes is < 0 or > 17_592_186_044_416L ||
                content.State is not (ArtifactAcquisitionJournalState.Prepared or ArtifactAcquisitionJournalState.Downloading or
                    ArtifactAcquisitionJournalState.Interrupted or ArtifactAcquisitionJournalState.Verified or
                    ArtifactAcquisitionJournalState.Finalized or ArtifactAcquisitionJournalState.Failed))
                throw Failure();
            if (content.Identity is { } identity) Identity(identity);
            if (content.Sha256 is { } hash) Fingerprint(hash);
            if (content.State == ArtifactAcquisitionJournalState.Prepared && content.Identity is not null ||
                content.Identity is null && (content.Bytes != 0 || content.Sha256 is not null ||
                content.State != ArtifactAcquisitionJournalState.Prepared) ||
                content.Identity is not null && content.Sha256 is null)
                throw Failure();
            if (content.MediaType is { } media && media is not (
                HttpsArtifactImageTransport.OciIndex or HttpsArtifactImageTransport.DockerIndex or
                HttpsArtifactImageTransport.OciManifest or HttpsArtifactImageTransport.DockerManifest))
                throw Failure();
        }
        Files(document.PublicationFiles, 2);
        if (document.PublicationFiles.Any(file => file.Key is not ("index" or "layout")))
            throw Failure();
        if (document.Quarantine is { } quarantine) Tree(quarantine);
        if (document.RecoveryTree is { } recovery) Tree(recovery);
        if (document.State == ArtifactImageJournalState.Quarantining &&
            (document.RecoveryTree is null || document.Quarantine is not null))
            throw Failure();
        if (document.State is ArtifactImageJournalState.Publishing or ArtifactImageJournalState.Published &&
            (document.RootIdentity is null || document.PublicationFiles.Length != 2 ||
             document.Directories.ContainsKey("partials") ||
             document.Contents.Any(content => content.State != ArtifactAcquisitionJournalState.Finalized)))
            throw Failure();
    }

    private static void Directories(Dictionary<string, string> directories)
    {
        if (directories is null || directories.Count > 3 ||
            directories.Keys.Any(key => key is not ("blobs" or "sha256" or "partials")))
            throw Failure();
        foreach (var value in directories.Values) Identity(value);
    }

    private static void Tree(ArtifactImageTree tree)
    {
        Identity(tree.RootIdentity);
        Directories(tree.Directories);
        Files(tree.Files, 266);
    }

    private static void Files(ArtifactImageOwnedFile[] files, int maximum)
    {
        if (files is null || files.Length > maximum || files.Any(file => file is null) ||
            files.Select(file => file.Key).Distinct(StringComparer.Ordinal).Count() != files.Length)
            throw Failure();
        foreach (var file in files)
        {
            if (file.Key is null) throw Failure();
            if (file.Key is not ("index" or "layout"))
            {
                if (!(file.Key.StartsWith("blob:", StringComparison.Ordinal) ||
                    file.Key.StartsWith("partial:", StringComparison.Ordinal))) throw Failure();
                Fingerprint(file.Key[(file.Key.IndexOf(':') + 1)..]);
            }
            Identity(file.Identity);
            Fingerprint(file.Sha256);
            if (file.Bytes is < 0 or > 17_592_186_044_416L + ArtifactAcquisitionCoordinator.DefaultFreeSpaceReserveBytes)
                throw Failure();
        }
    }

    private static void Identity(string? value) => AcquisitionGuard.Text(value, 256, ArtifactAcquisitionFailure.JournalCorrupt);
    private static void Fingerprint(string? value) => AcquisitionGuard.Fingerprint(value, ArtifactAcquisitionFailure.JournalCorrupt);
    private static ArtifactAcquisitionException Failure(ArtifactAcquisitionFailure failure = ArtifactAcquisitionFailure.JournalCorrupt) => new(failure);

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            MaxDepth = 16,
            TypeInfoResolver = new DefaultJsonTypeInfoResolver()
        };
        options.Converters.Add(new ArtifactAcquisitionJournalCodec.ExactEnumConverter<ArtifactImageJournalPurpose>());
        options.Converters.Add(new ArtifactAcquisitionJournalCodec.ExactEnumConverter<ArtifactImageJournalState>());
        options.Converters.Add(new ArtifactAcquisitionJournalCodec.ExactEnumConverter<ArtifactAcquisitionJournalState>());
        options.Converters.Add(new ArtifactAcquisitionJournalCodec.ExactEnumConverter<ArtifactAcquisitionFailure>());
        options.MakeReadOnly();
        return options;
    }
}
