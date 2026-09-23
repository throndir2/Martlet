using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;

namespace Martlet.LocalStt;

public enum LocalSttProvisioningFailure
{
    Busy,
    InvalidRequest,
    ContentPinIncomplete,
    SourceMissing,
    SourceUnsafe,
    SourceChanged,
    ArchiveCorrupt,
    ArchiveUnsafeEntry,
    ArchiveLimitExceeded,
    ArchiveHashMismatch,
    RuntimeFileMismatch,
    UnsupportedBinary,
    ModelInvalid,
    ModelHashMismatch,
    LicenseNoticeMissing,
    LicenseNoticeMismatch,
    RightsNotApproved,
    AuthorizationMismatch,
    AuthorizationConsumed,
    DestinationExists,
    StagingConflict,
    InsufficientDisk,
    AccessDenied,
    StorageFailure,
    Canceled,
    CleanupPending,
    InvalidReceipt,
    PackageVerifierRejected,
    UnsupportedHost,
    BuildSourceMismatch,
    ToolchainInvalid
}

public sealed class LocalSttProvisioningException : Exception
{
    public LocalSttProvisioningFailure Failure { get; }
    public LocalSttProvisioningFailure? OriginalFailure { get; }
    public string? RetainedDirectory { get; }

    internal LocalSttProvisioningException(
        LocalSttProvisioningFailure failure,
        string? retainedDirectory = null,
        LocalSttProvisioningFailure? originalFailure = null)
        : base(Summary(failure))
    {
        Failure = failure;
        OriginalFailure = originalFailure;
        RetainedDirectory = retainedDirectory;
    }

    private static string Summary(LocalSttProvisioningFailure failure) => failure switch
    {
        LocalSttProvisioningFailure.Busy =>
            "Another local STT package operation still owns the importer.",
        LocalSttProvisioningFailure.InvalidRequest =>
            "The local STT package request is malformed or outside its bounds.",
        LocalSttProvisioningFailure.ContentPinIncomplete =>
            "Exact runtime-file, model, archive, or notice content pins are incomplete.",
        LocalSttProvisioningFailure.SourceMissing =>
            "A caller-supplied local STT source file is missing.",
        LocalSttProvisioningFailure.SourceUnsafe =>
            "A caller-supplied source or destination uses an unsafe path.",
        LocalSttProvisioningFailure.SourceChanged =>
            "A caller-supplied source changed after preview.",
        LocalSttProvisioningFailure.ArchiveCorrupt =>
            "The runtime archive is corrupt or unsupported.",
        LocalSttProvisioningFailure.ArchiveUnsafeEntry =>
            "The runtime archive contains an unsafe, special, colliding, or escaping entry.",
        LocalSttProvisioningFailure.ArchiveLimitExceeded =>
            "The runtime archive exceeds an entry, expansion, path, or compression-ratio bound.",
        LocalSttProvisioningFailure.ArchiveHashMismatch =>
            "The runtime archive does not match the exact manifest content identity.",
        LocalSttProvisioningFailure.RuntimeFileMismatch =>
            "An extracted runtime file does not match its caller-supplied exact content pin.",
        LocalSttProvisioningFailure.UnsupportedBinary =>
            "A runtime executable or library is not the expected Windows x64 PE32+ format.",
        LocalSttProvisioningFailure.ModelInvalid =>
            "The model does not have the expected bounded whisper.cpp GGML header.",
        LocalSttProvisioningFailure.ModelHashMismatch =>
            "The model does not match the exact manifest content identity.",
        LocalSttProvisioningFailure.LicenseNoticeMissing =>
            "A required caller-supplied license notice is missing.",
        LocalSttProvisioningFailure.LicenseNoticeMismatch =>
            "A license notice or its artifact coverage does not match the reviewed declaration.",
        LocalSttProvisioningFailure.RightsNotApproved =>
            "Exact runtime, component, and model rights review was not explicitly approved.",
        LocalSttProvisioningFailure.AuthorizationMismatch =>
            "The one-use package authorization does not match this exact preview.",
        LocalSttProvisioningFailure.AuthorizationConsumed =>
            "The one-use package authorization was already consumed.",
        LocalSttProvisioningFailure.DestinationExists =>
            "The create-only package destination or staging path already exists.",
        LocalSttProvisioningFailure.StagingConflict =>
            "The owned staging transaction conflicts with current filesystem state.",
        LocalSttProvisioningFailure.InsufficientDisk =>
            "The destination volume lacks the bounded free space required for staging.",
        LocalSttProvisioningFailure.AccessDenied =>
            "The package transaction cannot access an exact owned path as the current user.",
        LocalSttProvisioningFailure.StorageFailure =>
            "A bounded package write, flush, read-back, or atomic finalize failed.",
        LocalSttProvisioningFailure.Canceled =>
            "The package transaction was canceled before atomic finalization.",
        LocalSttProvisioningFailure.CleanupPending =>
            "An owned staging directory could not be removed and requires explicit cleanup.",
        LocalSttProvisioningFailure.InvalidReceipt =>
            "The installed package receipt or deterministic layout is invalid.",
        LocalSttProvisioningFailure.PackageVerifierRejected =>
            "The finalized package was rejected by the launch package verifier.",
        LocalSttProvisioningFailure.UnsupportedHost =>
            "Offline package filesystem operations require Windows x64.",
        LocalSttProvisioningFailure.BuildSourceMismatch =>
            "The optional source-build facts do not match the exact upstream revision.",
        LocalSttProvisioningFailure.ToolchainInvalid =>
            "The optional source-build toolchain facts are incomplete or unsupported.",
        _ => throw new ArgumentOutOfRangeException(nameof(failure))
    };
}

public sealed record LocalSttRuntimeFilePin
{
    public required string ArchiveEntry { get; init; }
    public required string InstalledName { get; init; }
    public required long Bytes { get; init; }
    public required string Sha256 { get; init; }
}

public sealed record LocalSttLicenseNoticePin
{
    public required string FileName { get; init; }
    public required string Component { get; init; }
    public required string Spdx { get; init; }
    public required string SourceRevision { get; init; }
    public required string EvidenceUrl { get; init; }
    public required long Bytes { get; init; }
    public required string Sha256 { get; init; }
    public required string[] AppliesTo { get; init; }
}

public sealed record LocalSttImportEvidence
{
    public const int MaximumDocumentBytes = 262_144;

    public required int FormatVersion { get; init; }
    public required string ManifestSha256 { get; init; }
    public required string RuntimeRepository { get; init; }
    public required string RuntimeRevision { get; init; }
    public required string ArchiveSha256 { get; init; }
    public required bool ArchiveContentHashComplete { get; init; }
    public required string ModelRepository { get; init; }
    public required string ModelRevision { get; init; }
    public required string ModelSha256 { get; init; }
    public required bool ModelContentHashComplete { get; init; }
    public required LocalSttSourceLocatorBehavior RuntimeLocatorBehavior { get; init; }
    public required LocalSttSourceLocatorBehavior ModelLocatorBehavior { get; init; }
    public required bool RuntimeFileHashesComplete { get; init; }
    public required bool LicenseNoticesComplete { get; init; }
    public required LocalSttRuntimeFilePin[] RuntimeFiles { get; init; }
    public required LocalSttLicenseNoticePin[] LicenseNotices { get; init; }

    public static LocalSttImportEvidence Read(ReadOnlyMemory<byte> bytes) =>
        ProvisioningWire.Read<LocalSttImportEvidence>(
            bytes,
            MaximumDocumentBytes,
            LocalSttProvisioningFailure.InvalidRequest);

    public byte[] ToCanonicalJson() => ProvisioningWire.Write(this);
}

public sealed record LocalSttImportRequest(
    string ArchivePath,
    string ModelPath,
    string NoticeDirectory,
    string DestinationPath,
    LocalSttImportEvidence Evidence);

public enum LocalSttImportDisposition
{
    Ready,
    Blocked
}

public enum LocalSttRightsDecision
{
    No,
    ApproveExactRuntimeModelAndNoticeRights
}

public sealed class LocalSttImportPlan
{
    private readonly PhysicalLocalSttPackageImporter owner;
    private readonly byte[] evidenceBytes;
    internal ImportSourceSnapshot? Sources { get; }
    internal long Generation { get; }

    public string Fingerprint { get; }
    public string PackageId { get; }
    public string ManifestSha256 { get; }
    public LocalSttImportDisposition Disposition { get; }
    public LocalSttProvisioningFailure? Blocker { get; }
    public bool CanImport => Disposition == LocalSttImportDisposition.Ready;
    public bool AutomaticAcquisitionAllowed => false;
    public bool NetworkAcquisitionBlocked => true;
    public bool DeniedEgressEvidenceIncluded => false;
    public long RequiredFreeBytes { get; }
    public string ArchivePath { get; }
    public string ModelPath { get; }
    public string NoticeDirectory { get; }
    public string DestinationPath { get; }
    public string StagingPath { get; }
    public string ImportEvidenceSha256 { get; }

    internal LocalSttImportPlan(
        PhysicalLocalSttPackageImporter owner,
        long generation,
        LocalSttPackageManifest manifest,
        NormalizedImportRequest request,
        string evidenceSha256,
        string fingerprint,
        string stagingPath,
        LocalSttImportDisposition disposition,
        LocalSttProvisioningFailure? blocker,
        long requiredFreeBytes,
        ImportSourceSnapshot? sources)
    {
        this.owner = owner;
        Generation = generation;
        PackageId = manifest.Id;
        ManifestSha256 = manifest.DocumentSha256;
        ArchivePath = request.ArchivePath;
        ModelPath = request.ModelPath;
        NoticeDirectory = request.NoticeDirectory;
        DestinationPath = request.DestinationPath;
        ImportEvidenceSha256 = evidenceSha256;
        Fingerprint = fingerprint;
        StagingPath = stagingPath;
        Disposition = disposition;
        Blocker = blocker;
        RequiredFreeBytes = requiredFreeBytes;
        Sources = sources;
        evidenceBytes = request.Evidence.ToCanonicalJson();
    }

    internal LocalSttImportEvidence ReadEvidence() =>
        LocalSttImportEvidence.Read(evidenceBytes);

    public LocalSttImportAuthorization Authorize(
        LocalSttRightsDecision decision = LocalSttRightsDecision.No) =>
        new(owner, this, decision);

    public override string ToString() =>
        $"{nameof(LocalSttImportPlan)} {{ PackageId = {PackageId}, Disposition = {Disposition}, AutomaticAcquisitionAllowed = false }}";
}

public sealed class LocalSttImportAuthorization
{
    private readonly PhysicalLocalSttPackageImporter owner;
    private readonly long generation;
    private readonly string fingerprint;
    private int consumed;

    public bool IsApproved { get; }

    internal LocalSttImportAuthorization(
        PhysicalLocalSttPackageImporter owner,
        LocalSttImportPlan plan,
        LocalSttRightsDecision decision)
    {
        this.owner = owner;
        generation = plan.Generation;
        fingerprint = plan.Fingerprint;
        IsApproved = decision ==
            LocalSttRightsDecision.ApproveExactRuntimeModelAndNoticeRights &&
            plan.CanImport;
    }

    internal void Consume(
        PhysicalLocalSttPackageImporter expectedOwner,
        LocalSttImportPlan plan)
    {
        ProvisioningGuard.Require(IsApproved,
            LocalSttProvisioningFailure.RightsNotApproved);
        ProvisioningGuard.Require(
            ReferenceEquals(owner, expectedOwner) &&
            generation == plan.Generation &&
            fingerprint == plan.Fingerprint,
            LocalSttProvisioningFailure.AuthorizationMismatch);
        ProvisioningGuard.Require(Interlocked.Exchange(ref consumed, 1) == 0,
            LocalSttProvisioningFailure.AuthorizationConsumed);
    }

    public override string ToString() =>
        $"{nameof(LocalSttImportAuthorization)} {{ IsApproved = {IsApproved}, Consumed = {Volatile.Read(ref consumed) != 0} }}";
}

public sealed record LocalSttPackageFileReceipt(
    string Path,
    long Bytes,
    string Sha256,
    string Purpose);

public sealed class LocalSttPackageReceipt
{
    public int FormatVersion { get; }
    public string PackageId { get; }
    public string ManifestSha256 { get; }
    public string ImportEvidenceSha256 { get; }
    public string RuntimeRevision { get; }
    public string ModelRevision { get; }
    public string ArchiveSha256 { get; }
    public string ModelSha256 { get; }
    public string SbomSha256 { get; }
    public string IntegritySha256 { get; }
    public ImmutableArray<LocalSttPackageFileReceipt> Files { get; }
    public bool AutomaticAcquisitionPerformed => false;
    public bool DeniedEgressEvidenceIncluded => false;

    internal LocalSttPackageReceipt(PackageReceiptDocument document)
    {
        FormatVersion = document.FormatVersion;
        PackageId = document.PackageId;
        ManifestSha256 = document.ManifestSha256;
        ImportEvidenceSha256 = document.ImportEvidenceSha256;
        RuntimeRevision = document.RuntimeRevision;
        ModelRevision = document.ModelRevision;
        ArchiveSha256 = document.ArchiveSha256;
        ModelSha256 = document.ModelSha256;
        SbomSha256 = document.SbomSha256;
        IntegritySha256 = document.IntegritySha256;
        Files = document.Files
            .Select(file => new LocalSttPackageFileReceipt(
                file.Path,
                file.Bytes,
                file.Sha256,
                file.Purpose))
            .ToImmutableArray();
    }
}

public sealed record LocalSttPackageInspection(
    string DestinationPath,
    LocalSttPackageReceipt Receipt,
    PackageVerificationStatus PackageVerifierStatus)
{
    public string PayloadPath => Path.Combine(DestinationPath, "payload");
    public LocalSttCandidateStatus Status => LocalSttCandidateStatus.DisabledPendingQualification;
    public bool RuntimeQualified => false;
    public bool RightsQualified => false;
    public bool CanLaunch => false;
}

public sealed record LocalSttCleanupReceipt(
    int RemovedOwnedDirectories,
    int PreservedUnownedDirectories);

internal sealed record NormalizedImportRequest(
    string ArchivePath,
    string ModelPath,
    string NoticeDirectory,
    string DestinationPath,
    LocalSttImportEvidence Evidence);

internal sealed record SourceFileSnapshot(
    string Path,
    long Bytes,
    string Sha256,
    long LastWriteUtcTicks,
    string FileIdentity);

internal sealed record ImportSourceSnapshot(
    SourceFileSnapshot Archive,
    SourceFileSnapshot Model,
    ImmutableArray<SourceFileSnapshot> Notices);

internal enum LocalSttImportIoPoint
{
    BeforeCreateDirectory,
    BeforeCreateFile,
    BeforeWrite,
    BeforeFlush,
    AfterSourceCopy,
    BeforeReadBack,
    AfterReadBack,
    BeforeFinalize,
    AfterPublish,
    BeforeCleanup,
    BeforeCleanupEntry
}

internal static class ProvisioningGuard
{
    internal static void Require(
        bool condition,
        LocalSttProvisioningFailure failure)
    {
        if (!condition)
            throw new LocalSttProvisioningException(failure);
    }

    internal static void Sha256(
        string? value,
        LocalSttProvisioningFailure failure) =>
        Require(value is { Length: 64 } &&
            value.All(character =>
                character is >= '0' and <= '9' or >= 'a' and <= 'f'),
            failure);

    internal static void Revision(
        string? value,
        LocalSttProvisioningFailure failure) =>
        Require(value is { Length: 40 } &&
            value.All(ManifestRules.IsLowerHex),
            failure);

    internal static void Text(
        string? value,
        int maximum,
        LocalSttProvisioningFailure failure) =>
        Require(value is { Length: >= 1 } &&
            value.Length <= maximum &&
            !value.Any(char.IsControl),
            failure);

    internal static string Fingerprint(params string[] values)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var value in values)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(value);
            hash.AppendData(bytes);
            hash.AppendData([0]);
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    internal static string Invariant(long value) =>
        value.ToString(CultureInfo.InvariantCulture);
}
