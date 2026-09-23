using System.Collections.Immutable;
using System.Security;
using System.Security.Cryptography;
using System.Text;

namespace Martlet.LocalStt;

public sealed class PhysicalLocalSttPackageImporter
{
    private const int MaximumNoticeBytes = 262_144;
    private const int MaximumPackageEntries = 256;
    private const long MetadataReserveBytes = 2 * 1024 * 1024;
    private const string PayloadDirectory = "payload";
    private const string DownloadsDirectory = "payload/downloads";
    private const string RuntimeDirectory = "payload/runtime";
    private const string ModelsDirectory = "payload/models";
    private const string NoticesDirectory = "notices";
    private const string MetadataDirectory = "metadata";
    private const string OwnerFile = "package-owner.v1.json";
    private const string ManifestFile = "whisper-package.v1.json";
    private const string EvidenceFile = "import-evidence.v1.json";
    private const string SbomFile = "sbom.cdx.json";
    private const string ReceiptFile = "package-receipt.v1.json";

    private readonly LocalSttPackageManifest manifest;
    private readonly ILocalPathInspector pathInspector;
    private readonly Func<string, long> availableBytes;
    private int busy;
    private long generation;
    private string? pendingDirectory;
    private string? pendingIdentity;
    private string? pendingTransactionId;
    private string[] pendingNotices = [];

    internal Action<LocalSttImportIoPoint, CancellationToken>? Io { get; init; }

    public PhysicalLocalSttPackageImporter()
        : this(
            LocalSttPackageManifest.Current,
            new PhysicalLocalPathInspector(),
            parent => new DriveInfo(Path.GetPathRoot(parent)!).AvailableFreeSpace)
    {
    }

    internal PhysicalLocalSttPackageImporter(
        LocalSttPackageManifest manifest,
        ILocalPathInspector pathInspector,
        Func<string, long>? availableBytes = null)
    {
        this.manifest = manifest ??
            throw new ArgumentNullException(nameof(manifest));
        this.pathInspector = pathInspector ??
            throw new ArgumentNullException(nameof(pathInspector));
        this.availableBytes = availableBytes ??
            (parent => new DriveInfo(Path.GetPathRoot(parent)!).AvailableFreeSpace);
    }

    public LocalSttImportPlan Preview(
        LocalSttImportRequest request,
        CancellationToken cancellationToken = default) =>
        Run(() =>
        {
            ArgumentNullException.ThrowIfNull(request);
            cancellationToken.ThrowIfCancellationRequested();
            generation = checked(generation + 1);
            var normalized = Normalize(request);
            var evidenceBytes = normalized.Evidence.ToCanonicalJson();
            var evidenceSha256 = ProvisioningWire.Hash(evidenceBytes);
            var blocker = ValidateEvidence(normalized.Evidence);
            var transactionId = ProvisioningGuard.Fingerprint(
                manifest.DocumentSha256,
                evidenceSha256)[..32];
            var parent = Path.GetDirectoryName(normalized.DestinationPath)!;
            var stagingPath = Path.Combine(
                parent,
                $".martlet-local-stt-{transactionId}.pending");
            if (PathExists(normalized.DestinationPath))
                throw new LocalSttProvisioningException(
                    LocalSttProvisioningFailure.DestinationExists);
            if (PathExists(stagingPath))
                throw new LocalSttProvisioningException(
                    LocalSttProvisioningFailure.CleanupPending,
                    stagingPath);

            if (blocker is { } blocked)
            {
                var fingerprint = PlanFingerprint(
                    normalized,
                    evidenceSha256,
                    stagingPath,
                    null,
                    0);
                return new LocalSttImportPlan(
                    this,
                    generation,
                    manifest,
                    normalized,
                    evidenceSha256,
                    fingerprint,
                    stagingPath,
                    LocalSttImportDisposition.Blocked,
                    blocked,
                    0,
                    null);
            }

            var sources = InspectSources(normalized, cancellationToken);
            var runtimeBytes = normalized.Evidence.RuntimeFiles.Aggregate(
                0L,
                (total, file) => checked(total + file.Bytes));
            var noticeBytes = normalized.Evidence.LicenseNotices.Aggregate(
                0L,
                (total, notice) => checked(total + notice.Bytes));
            var requiredBytes = checked(
                manifest.Document.Runtime.ArchiveBytes +
                manifest.Document.Model.Bytes +
                runtimeBytes +
                noticeBytes +
                MetadataReserveBytes);
            ProvisioningGuard.Require(
                requiredBytes <= manifest.MaximumProvisioningBytes,
                LocalSttProvisioningFailure.ArchiveLimitExceeded);
            RequireSpace(parent, requiredBytes);
            var readyFingerprint = PlanFingerprint(
                normalized,
                evidenceSha256,
                stagingPath,
                sources,
                requiredBytes);
            return new LocalSttImportPlan(
                this,
                generation,
                manifest,
                normalized,
                evidenceSha256,
                readyFingerprint,
                stagingPath,
                LocalSttImportDisposition.Ready,
                null,
                requiredBytes,
                sources);
        }, cancellationToken);

    public LocalSttPackageReceipt Import(
        LocalSttImportPlan plan,
        LocalSttImportAuthorization authorization,
        CancellationToken cancellationToken = default) =>
        Run(() =>
        {
            ArgumentNullException.ThrowIfNull(plan);
            ArgumentNullException.ThrowIfNull(authorization);
            ProvisioningGuard.Require(
                plan.Generation == generation &&
                plan.CanImport &&
                plan.Sources is not null,
                LocalSttProvisioningFailure.AuthorizationMismatch);
            var sourceSnapshot = plan.Sources ??
                throw new LocalSttProvisioningException(
                    LocalSttProvisioningFailure.AuthorizationMismatch);
            authorization.Consume(this, plan);
            cancellationToken.ThrowIfCancellationRequested();
            var request = Normalize(new LocalSttImportRequest(
                plan.ArchivePath,
                plan.ModelPath,
                plan.NoticeDirectory,
                plan.DestinationPath,
                plan.ReadEvidence()));
            ProvisioningGuard.Require(
                ValidateEvidence(request.Evidence) is null,
                LocalSttProvisioningFailure.ContentPinIncomplete);
            var evidenceBytes = request.Evidence.ToCanonicalJson();
            ProvisioningGuard.Require(
                ProvisioningWire.Hash(evidenceBytes) ==
                    plan.ImportEvidenceSha256,
                LocalSttProvisioningFailure.AuthorizationMismatch);
            var parent = Path.GetDirectoryName(plan.DestinationPath)!;
            using var parentLease = new WindowsDirectoryLease(parent);
            using var transactionLease = new ProvisioningTransactionLease(plan.StagingPath);
            RequireSpace(parent, plan.RequiredFreeBytes);
            EnsureDestinationAvailable(plan.DestinationPath, plan.StagingPath);

            using var pinned = OpenPinnedSources(
                request,
                sourceSnapshot,
                cancellationToken);
            var stageCreated = false;
            var activePath = plan.StagingPath;
            WindowsDirectoryLease? stageLease = null;
            string? stageIdentity = null;
            try
            {
                Point(LocalSttImportIoPoint.BeforeCreateDirectory,
                    cancellationToken);
                CreateOnlyDirectory.Create(plan.StagingPath);
                stageCreated = true;
                pendingDirectory = plan.StagingPath;
                pendingIdentity = null;
                pendingTransactionId = StageTransactionId(plan.StagingPath);
                pendingNotices = request.Evidence.LicenseNotices.Select(n => n.FileName).ToArray();
                pathInspector.AssertSafeExisting(plan.StagingPath, directory: true);
                stageLease = new WindowsDirectoryLease(plan.StagingPath);
                stageIdentity = ProvisioningFileIdentity.ReadDirectory(plan.StagingPath);
                pendingIdentity = stageIdentity;

                var owner = new PackageStageOwnerDocument
                {
                    FormatVersion = 1,
                    Kind = "martlet_local_stt_owned_package",
                    TransactionId = StageTransactionId(plan.StagingPath),
                    PackageId = manifest.Id,
                    ManifestSha256 = manifest.DocumentSha256,
                    ImportEvidenceSha256 = plan.ImportEvidenceSha256,
                    NoticeFiles = request.Evidence.LicenseNotices.Select(n => n.FileName).Order(StringComparer.Ordinal).ToArray()
                };
                var files = new List<PackageFileDocument>();
                var ownerBytes = ProvisioningWire.Write(owner);
                WriteCreateOnly(
                    PackagePath(plan.StagingPath, OwnerFile),
                    ownerBytes,
                    cancellationToken);
                files.Add(FileDocument(
                    OwnerFile,
                    ownerBytes,
                    "package-owner"));
                CreateDirectories(plan.StagingPath, cancellationToken);

                pinned.Archive.Position = 0;
                var archivePath = PackagePath(
                    plan.StagingPath,
                    $"{DownloadsDirectory}/{manifest.Document.Runtime.ArchiveFileName}");
                CopyCreateOnly(
                    pinned.Archive,
                    archivePath,
                    manifest.Document.Runtime.ArchiveBytes,
                    manifest.Document.Runtime.ArchiveSha256,
                    LocalSttProvisioningFailure.SourceChanged,
                    cancellationToken);
                files.Add(new()
                {
                    Path = $"{DownloadsDirectory}/{manifest.Document.Runtime.ArchiveFileName}",
                    Bytes = manifest.Document.Runtime.ArchiveBytes,
                    Sha256 = manifest.Document.Runtime.ArchiveSha256,
                    Purpose = "runtime-archive"
                });

                pinned.Model.Position = 0;
                var modelPath = PackagePath(
                    plan.StagingPath,
                    $"{ModelsDirectory}/{manifest.Document.Model.FileName}");
                var modelHeader = CopyCreateOnly(
                    pinned.Model,
                    modelPath,
                    manifest.Document.Model.Bytes,
                    manifest.Document.Model.Sha256,
                    LocalSttProvisioningFailure.SourceChanged,
                    cancellationToken,
                    WhisperModelInspector.HeaderBytes);
                WhisperModelInspector.Verify(
                    modelHeader,
                    manifest.Document.Model.Bytes);
                files.Add(new()
                {
                    Path = $"{ModelsDirectory}/{manifest.Document.Model.FileName}",
                    Bytes = manifest.Document.Model.Bytes,
                    Sha256 = manifest.Document.Model.Sha256,
                    Purpose = "model"
                });

                foreach (var notice in request.Evidence.LicenseNotices
                             .OrderBy(notice => notice.FileName, StringComparer.Ordinal))
                {
                    var source = pinned.Notices[notice.FileName];
                    source.Position = 0;
                    var noticePath = PackagePath(
                        plan.StagingPath,
                        $"{NoticesDirectory}/{notice.FileName}");
                    CopyCreateOnly(
                        source,
                        noticePath,
                        notice.Bytes,
                        notice.Sha256,
                        LocalSttProvisioningFailure.SourceChanged,
                        cancellationToken);
                    files.Add(new()
                    {
                        Path = $"{NoticesDirectory}/{notice.FileName}",
                        Bytes = notice.Bytes,
                        Sha256 = notice.Sha256,
                        Purpose = "license-notice"
                    });
                }

                using (var archive = OpenPackageRead(archivePath))
                {
                    files.AddRange(RestrictedRuntimeArchive.Verify(
                        archive,
                        manifest,
                        request.Evidence,
                        file => CreateOutput(
                            PackagePath(
                                plan.StagingPath,
                                $"{RuntimeDirectory}/{file.InstalledName}"),
                            cancellationToken),
                        () => Point(
                            LocalSttImportIoPoint.BeforeWrite,
                            cancellationToken),
                        cancellationToken));
                }

                var manifestBytes = manifest.CopyDocumentBytes();
                WriteCreateOnly(
                    PackagePath(
                        plan.StagingPath,
                        $"{MetadataDirectory}/{ManifestFile}"),
                    manifestBytes,
                    cancellationToken);
                files.Add(FileDocument(
                    $"{MetadataDirectory}/{ManifestFile}",
                    manifestBytes,
                    "package-manifest"));
                WriteCreateOnly(
                    PackagePath(
                        plan.StagingPath,
                        $"{MetadataDirectory}/{EvidenceFile}"),
                    evidenceBytes,
                    cancellationToken);
                files.Add(FileDocument(
                    $"{MetadataDirectory}/{EvidenceFile}",
                    evidenceBytes,
                    "import-evidence"));

                var sbomBytes = BuildSbom(request.Evidence, files);
                ProvisioningGuard.Require(
                    sbomBytes.Length <= ProvisioningWire.MaximumSbomBytes,
                    LocalSttProvisioningFailure.InvalidReceipt);
                WriteCreateOnly(
                    PackagePath(
                        plan.StagingPath,
                        $"{MetadataDirectory}/{SbomFile}"),
                    sbomBytes,
                    cancellationToken);
                var sbomSha256 = ProvisioningWire.Hash(sbomBytes);
                files.Add(new()
                {
                    Path = $"{MetadataDirectory}/{SbomFile}",
                    Bytes = sbomBytes.Length,
                    Sha256 = sbomSha256,
                    Purpose = "sbom-declaration"
                });

                var receiptDocument = CreateReceipt(
                    plan.ImportEvidenceSha256,
                    sbomSha256,
                    files);
                var receiptBytes = ProvisioningWire.WriteReceipt(
                    receiptDocument);
                WriteCreateOnly(
                    PackagePath(
                        plan.StagingPath,
                        $"{MetadataDirectory}/{ReceiptFile}"),
                    receiptBytes,
                    cancellationToken);

                Point(LocalSttImportIoPoint.AfterSourceCopy,
                    cancellationToken);
                Point(LocalSttImportIoPoint.BeforeReadBack,
                    cancellationToken);
                Point(LocalSttImportIoPoint.BeforeFinalize,
                    cancellationToken);
                using var publicationLease = new ProvisioningReadTree(plan.StagingPath,
                    MaximumPackageEntries, cancellationToken);
                var staged = InspectInstalledCore(
                    plan.StagingPath,
                    cancellationToken);
                ProvisioningGuard.Require(
                    staged.Receipt.IntegritySha256 ==
                        ProvisioningWire.ReadReceipt(receiptBytes)
                            .IntegritySha256,
                    LocalSttProvisioningFailure.InvalidReceipt);

                Point(LocalSttImportIoPoint.AfterReadBack, cancellationToken);
                pinned.ValidateNamedSources(cancellationToken);
                EnsureDestinationAvailable(
                    plan.DestinationPath,
                    exceptStagingPath: null);
                cancellationToken.ThrowIfCancellationRequested();
                stageLease.Dispose();
                stageLease = null;
                // Windows cannot rename a directory while descendant file handles remain open.
                // Retain IDs, then pin and reverify the published bytes before returning success.
                publicationLease.Dispose();
                ProvisioningFileIdentity.MoveDirectory(
                    plan.StagingPath,
                    plan.DestinationPath,
                    stageIdentity);
                activePath = plan.DestinationPath;
                pendingDirectory = activePath;
                Point(LocalSttImportIoPoint.AfterPublish, cancellationToken);
                using var publishedRoot = new WindowsDirectoryLease(activePath);
                ProvisioningGuard.Require(ProvisioningFileIdentity.ReadDirectory(activePath) == stageIdentity,
                    LocalSttProvisioningFailure.StagingConflict);
                using var publishedLease = new ProvisioningReadTree(activePath, MaximumPackageEntries,
                    cancellationToken: cancellationToken);
                publicationLease.ValidatePublished(activePath);
                var published = InspectInstalledCore(activePath, cancellationToken);
                ProvisioningGuard.Require(published.Receipt.IntegritySha256 == staged.Receipt.IntegritySha256,
                    LocalSttProvisioningFailure.InvalidReceipt);
                cancellationToken.ThrowIfCancellationRequested();
                pendingDirectory = null;
                pendingIdentity = null;
                stageCreated = false;
                return published.Receipt;
            }
            catch (Exception error) when (Handled(error))
            {
                var original = Failure(error, cancellationToken);
                stageLease?.Dispose();
                stageLease = null;
                if (stageCreated && Directory.Exists(activePath))
                {
                    try
                    {
                        DeleteOwnedTree(
                            activePath,
                            StageTransactionId(plan.StagingPath),
                            CancellationToken.None,
                            requireOwner: false,
                            expectedIdentity: stageIdentity,
                            noticeFiles: request.Evidence.LicenseNotices.Select(n => n.FileName).ToArray());
                        pendingDirectory = null;
                        pendingIdentity = null;
                    }
                    catch (Exception cleanupError) when (Handled(cleanupError))
                    {
                        pendingDirectory = activePath;
                        throw new LocalSttProvisioningException(
                            LocalSttProvisioningFailure.CleanupPending,
                            activePath,
                            original);
                    }
                }
                throw Translate(error, cancellationToken);
            }
            finally
            {
                stageLease?.Dispose();
            }
        }, cancellationToken);

    public LocalSttPackageInspection InspectInstalled(
        string destinationPath,
        CancellationToken cancellationToken = default) =>
        Run(() =>
        {
            var destination = NormalizeExistingDirectory(destinationPath);
            return InspectInstalledCore(destination, cancellationToken);
        }, cancellationToken, allowPending: true);

    public LocalSttCleanupReceipt CleanupOwnedStaging(
        string parentDirectory,
        CancellationToken cancellationToken = default) =>
        Run(() =>
        {
            var parent = NormalizeExistingDirectory(parentDirectory);
            using var parentLease = new WindowsDirectoryLease(parent);
            var removed = 0;
            var preserved = 0;
            if (pendingDirectory is not null &&
                string.Equals(Path.GetDirectoryName(pendingDirectory), parent, PathComparison))
            {
                using var pendingLease = new ProvisioningTransactionLease(
                    Path.Combine(parent, $".martlet-local-stt-{pendingTransactionId}.pending"));
                if (PathExists(pendingDirectory))
                {
                    DeleteOwnedTree(pendingDirectory, pendingTransactionId!, cancellationToken,
                        requireOwner: false, expectedIdentity: pendingIdentity, noticeFiles: pendingNotices);
                    removed++;
                }
                pendingDirectory = null;
                pendingIdentity = null;
            }
            foreach (var directory in new DirectoryInfo(parent)
                         .EnumerateDirectories(
                             ".martlet-local-stt-*.pending",
                             SearchOption.TopDirectoryOnly)
                         .Take(MaximumPackageEntries + 1)
                         .OrderBy(directory => directory.Name,
                             StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                ProvisioningGuard.Require(removed + preserved < MaximumPackageEntries,
                    LocalSttProvisioningFailure.StagingConflict);
                using var transactionLease = new ProvisioningTransactionLease(directory.FullName);
                if (!TryStageTransactionId(
                        directory.Name,
                        out var transactionId) ||
                    !TryReadOwner(
                        directory.FullName,
                        transactionId,
                        cancellationToken))
                {
                    preserved++;
                    continue;
                }
                DeleteOwnedTree(
                    directory.FullName,
                    transactionId,
                    cancellationToken,
                    requireOwner: true);
                if (string.Equals(
                        pendingDirectory,
                        directory.FullName,
                        PathComparison))
                    pendingDirectory = null;
                removed++;
            }
            return new LocalSttCleanupReceipt(removed, preserved);
        }, cancellationToken, allowPending: true);

    private LocalSttPackageInspection InspectInstalledCore(
        string destination,
        CancellationToken cancellationToken)
    {
        pathInspector.AssertSafeExisting(destination, directory: true);
        using var directoryLease = new WindowsDirectoryLease(destination);
        using var filesLease = new ProvisioningReadTree(destination, MaximumPackageEntries,
            cancellationToken: cancellationToken);
        var receiptPath = PackagePath(
            destination,
            $"{MetadataDirectory}/{ReceiptFile}");
        var receiptBytes = ReadBoundedFile(
            receiptPath,
            ProvisioningWire.MaximumReceiptBytes,
            cancellationToken);
        var document = ProvisioningWire.ReadReceipt(receiptBytes);
        ValidateReceipt(document);

        var evidencePath = PackagePath(
            destination,
            $"{MetadataDirectory}/{EvidenceFile}");
        var evidenceBytes = ReadBoundedFile(
            evidencePath,
            LocalSttImportEvidence.MaximumDocumentBytes,
            cancellationToken);
        var evidence = LocalSttImportEvidence.Read(evidenceBytes);
        var canonicalEvidence = evidence.ToCanonicalJson();
        ProvisioningGuard.Require(
            ProvisioningWire.Hash(canonicalEvidence) ==
                document.ImportEvidenceSha256 &&
            ValidateEvidence(evidence) is null,
            LocalSttProvisioningFailure.InvalidReceipt);
        var expected = BuildExpectedPackage(
            evidence,
            canonicalEvidence);
        ProvisioningGuard.Require(
            document.SbomSha256 ==
                ProvisioningWire.Hash(expected.SbomBytes) &&
            document.Files.SequenceEqual(expected.Files),
            LocalSttProvisioningFailure.InvalidReceipt);
        VerifyLayout(
            destination,
            expected.Files,
            cancellationToken);

        var sbomPath = PackagePath(
            destination,
            $"{MetadataDirectory}/{SbomFile}");
        var sbomBytes = ReadBoundedFile(
            sbomPath,
            ProvisioningWire.MaximumSbomBytes,
            cancellationToken);
        ProvisioningGuard.Require(
            sbomBytes.AsSpan().SequenceEqual(expected.SbomBytes) &&
            ProvisioningWire.Hash(sbomBytes) == document.SbomSha256,
            LocalSttProvisioningFailure.InvalidReceipt);
        var sbom = ProvisioningWire.Read<CycloneDxDocument>(
            sbomBytes,
            ProvisioningWire.MaximumSbomBytes,
            LocalSttProvisioningFailure.InvalidReceipt,
            canonical: true);
        ValidateSbom(sbom, document);
        using (var archive = OpenPackageRead(PackagePath(destination,
            $"{DownloadsDirectory}/{manifest.Document.Runtime.ArchiveFileName}")))
            RestrictedRuntimeArchive.Verify(archive, manifest, evidence, null, null, cancellationToken);
        using (var model = OpenPackageRead(PackagePath(destination,
            $"{ModelsDirectory}/{manifest.Document.Model.FileName}")))
        {
            var header = new byte[WhisperModelInspector.HeaderBytes];
            model.ReadExactly(header);
            WhisperModelInspector.Verify(header, model.Length);
        }

        var verifier = new PhysicalLocalSttPackageVerifier(
            PackagePath(destination, PayloadDirectory),
            manifest,
            pathInspector,
            () => true);
        var verified = verifier.VerifyForLaunchAsync(cancellationToken)
            .GetAwaiter()
            .GetResult();
        if (verified.Status == PackageVerificationStatus.Canceled)
            throw new LocalSttProvisioningException(
                LocalSttProvisioningFailure.Canceled);
        if (verified.Status != PackageVerificationStatus.Verified ||
            verified.Package is null)
            throw new LocalSttProvisioningException(
                LocalSttProvisioningFailure.PackageVerifierRejected);
        verified.Package.DisposeAsync().AsTask().GetAwaiter().GetResult();
        return new LocalSttPackageInspection(
            destination,
            new LocalSttPackageReceipt(document),
            verified.Status);
    }

    private (
        PackageFileDocument[] Files,
        byte[] SbomBytes) BuildExpectedPackage(
            LocalSttImportEvidence evidence,
            byte[] evidenceBytes)
    {
        var evidenceSha256 = ProvisioningWire.Hash(evidenceBytes);
        var transactionId = ProvisioningGuard.Fingerprint(
            manifest.DocumentSha256,
            evidenceSha256)[..32];
        var ownerBytes = ProvisioningWire.Write(
            new PackageStageOwnerDocument
            {
                FormatVersion = 1,
                Kind = "martlet_local_stt_owned_package",
                TransactionId = transactionId,
                PackageId = manifest.Id,
                ManifestSha256 = manifest.DocumentSha256,
                ImportEvidenceSha256 = evidenceSha256,
                NoticeFiles = evidence.LicenseNotices.Select(n => n.FileName).Order(StringComparer.Ordinal).ToArray()
            });
        var files = new List<PackageFileDocument>
        {
            FileDocument(
                OwnerFile,
                ownerBytes,
                "package-owner"),
            new()
            {
                Path = $"{DownloadsDirectory}/{manifest.Document.Runtime.ArchiveFileName}",
                Bytes = manifest.Document.Runtime.ArchiveBytes,
                Sha256 = manifest.Document.Runtime.ArchiveSha256,
                Purpose = "runtime-archive"
            },
            new()
            {
                Path = $"{ModelsDirectory}/{manifest.Document.Model.FileName}",
                Bytes = manifest.Document.Model.Bytes,
                Sha256 = manifest.Document.Model.Sha256,
                Purpose = "model"
            }
        };
        foreach (var notice in evidence.LicenseNotices
                     .OrderBy(notice => notice.FileName, StringComparer.Ordinal))
        {
            files.Add(new()
            {
                Path = $"{NoticesDirectory}/{notice.FileName}",
                Bytes = notice.Bytes,
                Sha256 = notice.Sha256,
                Purpose = "license-notice"
            });
        }
        var pins = evidence.RuntimeFiles.ToDictionary(
            pin => pin.ArchiveEntry,
            StringComparer.Ordinal);
        foreach (var file in manifest.Document.Runtime.Files
                     .OrderBy(file => file.InstalledName, StringComparer.Ordinal))
        {
            var pin = pins[file.ArchiveEntry];
            files.Add(new()
            {
                Path = $"{RuntimeDirectory}/{file.InstalledName}",
                Bytes = pin.Bytes,
                Sha256 = pin.Sha256,
                Purpose = file.Purpose == RuntimeFilePurpose.Executable
                    ? "runtime-executable"
                    : "runtime-dependency"
            });
        }
        var manifestBytes = manifest.CopyDocumentBytes();
        files.Add(FileDocument(
            $"{MetadataDirectory}/{ManifestFile}",
            manifestBytes,
            "package-manifest"));
        files.Add(FileDocument(
            $"{MetadataDirectory}/{EvidenceFile}",
            evidenceBytes,
            "import-evidence"));
        var sbomBytes = BuildSbom(evidence, files);
        files.Add(new()
        {
            Path = $"{MetadataDirectory}/{SbomFile}",
            Bytes = sbomBytes.Length,
            Sha256 = ProvisioningWire.Hash(sbomBytes),
            Purpose = "sbom-declaration"
        });
        return (
            files.OrderBy(file => file.Path, StringComparer.Ordinal).ToArray(),
            sbomBytes);
    }

    private NormalizedImportRequest Normalize(LocalSttImportRequest request)
    {
        ArgumentNullException.ThrowIfNull(request.Evidence);
        var archive = NormalizeExistingFile(request.ArchivePath);
        var model = NormalizeExistingFile(request.ModelPath);
        var notices = NormalizeExistingDirectory(request.NoticeDirectory);
        var destination = NormalizeDestination(request.DestinationPath);
        ProvisioningGuard.Require(
            !string.Equals(archive, model, PathComparison) &&
            !Within(destination, notices) &&
            !Within(notices, destination) &&
            !string.Equals(destination, archive, PathComparison) &&
            !string.Equals(destination, model, PathComparison),
            LocalSttProvisioningFailure.SourceUnsafe);
        var evidenceBytes = request.Evidence.ToCanonicalJson();
        var evidence = LocalSttImportEvidence.Read(evidenceBytes);
        return new(archive, model, notices, destination, evidence);
    }

    private LocalSttProvisioningFailure? ValidateEvidence(
        LocalSttImportEvidence evidence)
    {
        var runtime = manifest.Document.Runtime;
        var model = manifest.Document.Model;
        ProvisioningGuard.Require(
            evidence.FormatVersion == 1 &&
            evidence.ManifestSha256 == manifest.DocumentSha256 &&
            evidence.RuntimeRepository == runtime.Repository &&
            evidence.RuntimeRevision == runtime.Revision &&
            evidence.ArchiveSha256 == runtime.ArchiveSha256 &&
            evidence.ModelRepository == model.Repository &&
            evidence.ModelRevision == model.Revision &&
            evidence.ModelSha256 == model.Sha256 &&
            evidence.RuntimeLocatorBehavior ==
                manifest.RuntimeLocatorBehavior &&
            evidence.ModelLocatorBehavior ==
                manifest.ModelLocatorBehavior,
            LocalSttProvisioningFailure.InvalidRequest);
        ProvisioningGuard.Sha256(
            evidence.ManifestSha256,
            LocalSttProvisioningFailure.InvalidRequest);
        ProvisioningGuard.Revision(
            evidence.RuntimeRevision,
            LocalSttProvisioningFailure.InvalidRequest);
        ProvisioningGuard.Revision(
            evidence.ModelRevision,
            LocalSttProvisioningFailure.InvalidRequest);
        if (!evidence.ArchiveContentHashComplete ||
            !evidence.ModelContentHashComplete ||
            !evidence.RuntimeFileHashesComplete ||
            !evidence.LicenseNoticesComplete)
            return LocalSttProvisioningFailure.ContentPinIncomplete;

        var runtimeFiles = evidence.RuntimeFiles ??
            throw new LocalSttProvisioningException(
                LocalSttProvisioningFailure.ContentPinIncomplete);
        var licenseNotices = evidence.LicenseNotices ??
            throw new LocalSttProvisioningException(
                LocalSttProvisioningFailure.ContentPinIncomplete);
        ProvisioningGuard.Require(
            runtimeFiles.Length == runtime.Files.Length &&
            licenseNotices.Length is >= 2 and <= 16,
            LocalSttProvisioningFailure.ContentPinIncomplete);
        var pins = new Dictionary<string, LocalSttRuntimeFilePin>(
            StringComparer.Ordinal);
        var installedNames = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);
        long runtimeBytes = 0;
        foreach (var pin in runtimeFiles)
        {
            if (pin is null)
                throw new LocalSttProvisioningException(
                    LocalSttProvisioningFailure.ContentPinIncomplete);
            ProvisioningGuard.Require(
                pin.ArchiveEntry is not null &&
                pin.InstalledName is not null &&
                pins.TryAdd(pin.ArchiveEntry, pin) &&
                installedNames.Add(pin.InstalledName) &&
                pin.Bytes is >= 256 &&
                pin.Bytes <= manifest.Document.Provisioning
                    .MaximumExpandedRuntimeBytes,
                LocalSttProvisioningFailure.ContentPinIncomplete);
            ProvisioningGuard.Sha256(
                pin.Sha256,
                LocalSttProvisioningFailure.ContentPinIncomplete);
            runtimeBytes = checked(runtimeBytes + pin.Bytes);
        }
        ProvisioningGuard.Require(
            runtimeBytes <= manifest.Document.Provisioning
                .MaximumExpandedRuntimeBytes,
            LocalSttProvisioningFailure.ArchiveLimitExceeded);
        foreach (var file in runtime.Files)
        {
            ProvisioningGuard.Require(
                pins.TryGetValue(file.ArchiveEntry, out var pin) &&
                pin.InstalledName == file.InstalledName,
                LocalSttProvisioningFailure.ContentPinIncomplete);
        }

        var expectedArtifacts = runtime.Files
            .Select(file => $"runtime/{file.InstalledName}")
            .Append($"models/{model.FileName}")
            .ToHashSet(StringComparer.Ordinal);
        var covered = new HashSet<string>(StringComparer.Ordinal);
        var noticeNames = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);
        var primaryRuntime = false;
        var primaryModel = false;
        foreach (var notice in licenseNotices)
        {
            ValidateNoticePin(notice);
            ProvisioningGuard.Require(
                noticeNames.Add(notice.FileName) &&
                notice.AppliesTo is { Length: >= 1 and <= 32 } &&
                notice.AppliesTo.Distinct(StringComparer.Ordinal).Count() ==
                    notice.AppliesTo.Length &&
                notice.AppliesTo.All(expectedArtifacts.Contains),
                LocalSttProvisioningFailure.LicenseNoticeMismatch);
            covered.UnionWith(notice.AppliesTo);
            if (notice.EvidenceUrl == manifest.Document.Rights.RuntimeEvidenceUrl &&
                notice.Spdx == manifest.Document.Rights.RuntimeSpdx &&
                notice.SourceRevision == runtime.Revision &&
                notice.AppliesTo.Any(path =>
                    path.StartsWith(
                        "runtime/",
                        StringComparison.Ordinal)))
                primaryRuntime = true;
            if (notice.EvidenceUrl == manifest.Document.Rights.ModelEvidenceUrl &&
                notice.Spdx == manifest.Document.Rights.ModelSpdx &&
                notice.AppliesTo.Contains(
                    $"models/{model.FileName}",
                    StringComparer.Ordinal))
                primaryModel = true;
        }
        ProvisioningGuard.Require(
            covered.SetEquals(expectedArtifacts) &&
            primaryRuntime &&
            primaryModel,
            LocalSttProvisioningFailure.LicenseNoticeMismatch);
        return null;
    }

    private static void ValidateNoticePin(LocalSttLicenseNoticePin notice)
    {
        if (notice is null)
            throw new LocalSttProvisioningException(
                LocalSttProvisioningFailure.LicenseNoticeMismatch);
        ProvisioningGuard.Require(
            notice.FileName is { Length: >= 5 and <= 128 } &&
            notice.FileName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) &&
            notice.FileName.All(character =>
                char.IsAsciiLetterOrDigit(character) ||
                character is '.' or '_' or '-') &&
            notice.Component is { Length: >= 1 and <= 128 } &&
            notice.Component.All(character =>
                char.IsAsciiLetterOrDigit(character) ||
                character is ' ' or '.' or '_' or '-' or '+') &&
            notice.Spdx is { Length: >= 1 and <= 64 } &&
            notice.Spdx.All(character =>
                char.IsAsciiLetterOrDigit(character) ||
                character is '.' or '-' or '+') &&
            notice.Bytes is > 0 and <= MaximumNoticeBytes,
            LocalSttProvisioningFailure.LicenseNoticeMismatch);
        ProvisioningGuard.Revision(
            notice.SourceRevision,
            LocalSttProvisioningFailure.LicenseNoticeMismatch);
        ProvisioningPath.ValidateSegment(notice.FileName);
        ProvisioningGuard.Sha256(
            notice.Sha256,
            LocalSttProvisioningFailure.LicenseNoticeMismatch);
        ProvisioningGuard.Require(
            Uri.TryCreate(
                notice.EvidenceUrl,
                UriKind.Absolute,
                out var evidenceUri) &&
            evidenceUri.Scheme == Uri.UriSchemeHttps &&
            evidenceUri.IsDefaultPort &&
            string.IsNullOrEmpty(evidenceUri.UserInfo) &&
            string.IsNullOrEmpty(evidenceUri.Query) &&
            string.IsNullOrEmpty(evidenceUri.Fragment) &&
            evidenceUri.AbsolutePath.Contains(
                "/" + notice.SourceRevision + "/",
                StringComparison.Ordinal),
            LocalSttProvisioningFailure.LicenseNoticeMismatch);
    }

    private ImportSourceSnapshot InspectSources(
        NormalizedImportRequest request,
        CancellationToken cancellationToken)
    {
        var archive = InspectFile(
            request.ArchivePath,
            manifest.Document.Runtime.ArchiveBytes,
            manifest.Document.Runtime.ArchiveSha256,
            LocalSttProvisioningFailure.ArchiveHashMismatch,
            null,
            cancellationToken);
        using (var archiveStream = OpenSourceRead(request.ArchivePath))
        {
            RestrictedRuntimeArchive.Verify(
                archiveStream,
                manifest,
                request.Evidence,
                null,
                null,
                cancellationToken);
        }

        var modelHeader = new byte[WhisperModelInspector.HeaderBytes];
        var model = InspectFile(
            request.ModelPath,
            manifest.Document.Model.Bytes,
            manifest.Document.Model.Sha256,
            LocalSttProvisioningFailure.ModelHashMismatch,
            modelHeader,
            cancellationToken);
        WhisperModelInspector.Verify(
            modelHeader,
            manifest.Document.Model.Bytes);

        var notices = ImmutableArray.CreateBuilder<SourceFileSnapshot>(
            request.Evidence.LicenseNotices.Length);
        var occupied = new HashSet<string>(PathComparer)
        {
            request.ArchivePath,
            request.ModelPath
        };
        foreach (var notice in request.Evidence.LicenseNotices
                     .OrderBy(notice => notice.FileName, StringComparer.Ordinal))
        {
            var path = Path.GetFullPath(Path.Combine(
                request.NoticeDirectory,
                notice.FileName));
            ProvisioningGuard.Require(
                string.Equals(
                    Path.GetDirectoryName(path),
                    request.NoticeDirectory,
                    PathComparison) &&
                occupied.Add(path),
                LocalSttProvisioningFailure.SourceUnsafe);
            ProvisioningGuard.Require(
                File.Exists(path) && !Directory.Exists(path),
                LocalSttProvisioningFailure.LicenseNoticeMissing);
            pathInspector.AssertSafeExisting(path, directory: false);
            var snapshot = InspectFile(
                path,
                notice.Bytes,
                notice.Sha256,
                LocalSttProvisioningFailure.LicenseNoticeMismatch,
                null,
                cancellationToken);
            ValidateNoticeContent(
                ReadBoundedFile(path, MaximumNoticeBytes, cancellationToken));
            notices.Add(snapshot);
        }
        return new ImportSourceSnapshot(
            archive,
            model,
            notices.MoveToImmutable());
    }

    private SourceFileSnapshot InspectFile(
        string path,
        long expectedBytes,
        string expectedSha256,
        LocalSttProvisioningFailure mismatch,
        byte[]? header,
        CancellationToken cancellationToken)
    {
        pathInspector.AssertSafeExisting(path, directory: false);
        using var input = OpenSourceRead(path);
        ProvisioningGuard.Require(input.Length == expectedBytes, mismatch);
        var captured = PackageContentReader.CopyAndHash(
            input,
            null,
            expectedBytes,
            expectedSha256,
            mismatch,
            null,
            cancellationToken,
            header?.Length ?? 0);
        if (header is not null)
            captured.CopyTo(header, 0);
        var info = new FileInfo(path);
        info.Refresh();
        ProvisioningGuard.Require(
            info.Exists &&
            info.Length == expectedBytes,
            LocalSttProvisioningFailure.SourceChanged);
        return new(
            path,
            expectedBytes,
            expectedSha256,
            info.LastWriteTimeUtc.Ticks,
            ProvisioningFileIdentity.Read(input));
    }

    private PinnedSources OpenPinnedSources(
        NormalizedImportRequest request,
        ImportSourceSnapshot expected,
        CancellationToken cancellationToken)
    {
        FileStream archive;
        try
        {
            archive = OpenSourceRead(request.ArchivePath);
        }
        catch (Exception error) when (
            error is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new LocalSttProvisioningException(
                LocalSttProvisioningFailure.SourceChanged);
        }
        try
        {
            FileStream model;
            try
            {
                model = OpenSourceRead(request.ModelPath);
            }
            catch (Exception error) when (
                error is FileNotFoundException or DirectoryNotFoundException)
            {
                throw new LocalSttProvisioningException(
                    LocalSttProvisioningFailure.SourceChanged);
            }
            try
            {
                var notices = new Dictionary<string, FileStream>(
                    StringComparer.Ordinal);
                try
                {
                    foreach (var notice in request.Evidence.LicenseNotices)
                    {
                        var path = Path.Combine(
                            request.NoticeDirectory,
                            notice.FileName);
                        try
                        {
                            notices.Add(
                                notice.FileName,
                                OpenSourceRead(path));
                        }
                        catch (Exception error) when (
                            error is FileNotFoundException or
                                DirectoryNotFoundException)
                        {
                            throw new LocalSttProvisioningException(
                                LocalSttProvisioningFailure.SourceChanged);
                        }
                    }
                    var pinned = new PinnedSources(
                        this,
                        expected,
                        archive,
                        model,
                        notices);
                    pinned.ValidateHandles(cancellationToken);
                    return pinned;
                }
                catch
                {
                    foreach (var stream in notices.Values)
                        stream.Dispose();
                    throw;
                }
            }
            catch
            {
                model.Dispose();
                throw;
            }
        }
        catch
        {
            archive.Dispose();
            throw;
        }
    }

    private void CreateDirectories(
        string root,
        CancellationToken cancellationToken)
    {
        foreach (var name in new[]
                 {
                     PayloadDirectory,
                     DownloadsDirectory,
                     RuntimeDirectory,
                     ModelsDirectory,
                     NoticesDirectory,
                     MetadataDirectory
                 })
        {
            var path = PackagePath(root, name);
            Point(LocalSttImportIoPoint.BeforeCreateDirectory,
                cancellationToken);
            ProvisioningGuard.Require(
                !PathExists(path),
                LocalSttProvisioningFailure.StagingConflict);
            CreateOnlyDirectory.Create(path);
            pathInspector.AssertSafeExisting(path, directory: true);
        }
    }

    private byte[] CopyCreateOnly(
        Stream source,
        string destination,
        long expectedBytes,
        string expectedSha256,
        LocalSttProvisioningFailure mismatch,
        CancellationToken cancellationToken,
        int capturedHeaderBytes = 0)
    {
        using var output = CreateOutput(destination, cancellationToken);
        var header = PackageContentReader.CopyAndHash(
            source,
            output,
            expectedBytes,
            expectedSha256,
            mismatch,
            () => Point(
                LocalSttImportIoPoint.BeforeWrite,
                cancellationToken),
            cancellationToken,
            capturedHeaderBytes);
        Point(LocalSttImportIoPoint.BeforeFlush, cancellationToken);
        output.Flush(flushToDisk: true);
        return header;
    }

    private void WriteCreateOnly(
        string path,
        byte[] bytes,
        CancellationToken cancellationToken)
    {
        using var output = CreateOutput(path, cancellationToken);
        Point(LocalSttImportIoPoint.BeforeWrite, cancellationToken);
        output.Write(bytes);
        Point(LocalSttImportIoPoint.BeforeFlush, cancellationToken);
        output.Flush(flushToDisk: true);
    }

    private FileStream CreateOutput(
        string path,
        CancellationToken cancellationToken)
    {
        Point(LocalSttImportIoPoint.BeforeCreateFile, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        ProvisioningGuard.Require(
            !PathExists(path),
            LocalSttProvisioningFailure.StagingConflict);
        return ProvisioningFile.Create(path);
    }

    private static PackageFileDocument FileDocument(
        string path,
        byte[] bytes,
        string purpose) => new()
        {
            Path = path,
            Bytes = bytes.Length,
            Sha256 = ProvisioningWire.Hash(bytes),
            Purpose = purpose
        };

    private byte[] BuildSbom(
        LocalSttImportEvidence evidence,
        IReadOnlyCollection<PackageFileDocument> files)
    {
        var runtimeLicense = new[]
        {
            new CycloneDxLicense
            {
                License = new CycloneDxLicenseId
                {
                    Id = manifest.Document.Rights.RuntimeSpdx
                }
            }
        };
        var modelLicense = new[]
        {
            new CycloneDxLicense
            {
                License = new CycloneDxLicenseId
                {
                    Id = manifest.Document.Rights.ModelSpdx
                }
            }
        };
        var components = new List<CycloneDxComponent>
        {
            new()
            {
                Type = "application",
                BomRef = "package:martlet-local-stt",
                Name = manifest.Id,
                Version = manifest.Document.Runtime.ReleaseTag,
                Properties =
                [
                    new()
                    {
                        Name = "martlet:manifest:sha256",
                        Value = manifest.DocumentSha256
                    },
                    new()
                    {
                        Name = "martlet:automatic-acquisition",
                        Value = "false"
                    },
                    new()
                    {
                        Name = "martlet:denied-egress-evidence",
                        Value = "separate-required-before-launch"
                    }
                ]
            },
            new()
            {
                Type = "application",
                BomRef = "component:whisper.cpp",
                Name = manifest.Document.Runtime.Repository,
                Version = manifest.Document.Runtime.Revision,
                Hashes =
                [
                    new()
                    {
                        Alg = "SHA-256",
                        Content = manifest.Document.Runtime.ArchiveSha256
                    }
                ],
                Licenses = runtimeLicense
            },
            new()
            {
                Type = "machine-learning-model",
                BomRef = "component:whisper-model",
                Name = manifest.Document.Model.Id,
                Version = manifest.Document.Model.Revision,
                Hashes =
                [
                    new()
                    {
                        Alg = "SHA-256",
                        Content = manifest.Document.Model.Sha256
                    }
                ],
                Licenses = modelLicense
            }
        };
        foreach (var file in files
                     .Where(file =>
                         file.Purpose is "runtime-executable" or
                             "runtime-dependency" or
                             "runtime-archive" or
                             "model" or
                             "license-notice")
                     .OrderBy(file => file.Path, StringComparer.Ordinal))
        {
            components.Add(new()
            {
                Type = "file",
                BomRef = "file:" + file.Path,
                Name = file.Path,
                Hashes =
                [
                    new()
                    {
                        Alg = "SHA-256",
                        Content = file.Sha256
                    }
                ],
                Properties =
                [
                    new()
                    {
                        Name = "martlet:file:bytes",
                        Value = ProvisioningGuard.Invariant(file.Bytes)
                    },
                    new()
                    {
                        Name = "martlet:file:purpose",
                        Value = file.Purpose
                    }
                ]
            });
        }
        var licenseDependencies = new List<CycloneDxDependency>();
        foreach (var notice in evidence.LicenseNotices
                     .OrderBy(notice => notice.FileName, StringComparer.Ordinal))
        {
            var reference = "component:license:" + notice.FileName;
            components.Add(new()
            {
                Type = "library",
                BomRef = reference,
                Name = notice.Component,
                Version = notice.SourceRevision,
                Licenses =
                [
                    new()
                    {
                        License = new CycloneDxLicenseId
                        {
                            Id = notice.Spdx
                        }
                    }
                ],
                Properties =
                [
                    new()
                    {
                        Name = "martlet:license:evidence-url",
                        Value = notice.EvidenceUrl
                    },
                    new()
                    {
                        Name = "martlet:license:notice-path",
                        Value = $"{NoticesDirectory}/{notice.FileName}"
                    }
                ]
            });
            licenseDependencies.Add(new()
            {
                Ref = reference,
                DependsOn = notice.AppliesTo
                    .Select(path => "file:payload/" + path)
                    .Order(StringComparer.Ordinal)
                    .ToArray()
            });
        }
        var runtimeFiles = files
            .Where(file => file.Path.StartsWith(
                RuntimeDirectory + "/",
                StringComparison.Ordinal))
            .Select(file => "file:" + file.Path)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var modelFile = "file:" +
            $"{ModelsDirectory}/{manifest.Document.Model.FileName}";
        var packageDependencies = components
            .Where(component =>
                component.BomRef != "package:martlet-local-stt")
            .Select(component => component.BomRef)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var dependencies = new List<CycloneDxDependency>
        {
            new()
            {
                Ref = "package:martlet-local-stt",
                DependsOn = packageDependencies
            },
            new()
            {
                Ref = "component:whisper.cpp",
                DependsOn = runtimeFiles
            },
            new()
            {
                Ref = "component:whisper-model",
                DependsOn = [modelFile]
            }
        };
        dependencies.AddRange(licenseDependencies);
        var document = new CycloneDxDocument
        {
            BomFormat = "CycloneDX",
            SpecVersion = "1.6",
            Version = 1,
            Metadata = new CycloneDxMetadata
            {
                Tools = new CycloneDxTool
                {
                    Components =
                    [
                        new()
                        {
                            Type = "application",
                            BomRef = "tool:martlet-local-stt-package-importer",
                            Name = "Martlet.LocalStt offline package importer",
                            Version = "0.1.0"
                        }
                    ]
                },
                Properties =
                [
                    new()
                    {
                        Name = "martlet:import-evidence:sha256",
                        Value = ProvisioningWire.Hash(
                            evidence.ToCanonicalJson())
                    },
                    new()
                    {
                        Name = "martlet:sbom:status",
                        Value = "caller-supplied-declaration-not-rights-clearance"
                    }
                ]
            },
            Components = components
                .OrderBy(component => component.BomRef, StringComparer.Ordinal)
                .ToArray(),
            Dependencies = dependencies
                .OrderBy(dependency => dependency.Ref, StringComparer.Ordinal)
                .ToArray()
        };
        return ProvisioningWire.Write(document);
    }

    private PackageReceiptDocument CreateReceipt(
        string evidenceSha256,
        string sbomSha256,
        IEnumerable<PackageFileDocument> files) => new()
        {
            FormatVersion = 1,
            PackageId = manifest.Id,
            ManifestSha256 = manifest.DocumentSha256,
            ImportEvidenceSha256 = evidenceSha256,
            RuntimeRevision = manifest.Document.Runtime.Revision,
            ModelRevision = manifest.Document.Model.Revision,
            ArchiveSha256 = manifest.Document.Runtime.ArchiveSha256,
            ModelSha256 = manifest.Document.Model.Sha256,
            SbomSha256 = sbomSha256,
            AutomaticAcquisitionPerformed = false,
            DeniedEgressEvidenceIncluded = false,
            Files = files
                .OrderBy(file => file.Path, StringComparer.Ordinal)
                .ToArray(),
            IntegritySha256 = ""
        };

    private void ValidateReceipt(PackageReceiptDocument document)
    {
        ProvisioningGuard.Require(
            document.FormatVersion == 1 &&
            document.PackageId == manifest.Id &&
            document.ManifestSha256 == manifest.DocumentSha256 &&
            document.RuntimeRevision == manifest.Document.Runtime.Revision &&
            document.ModelRevision == manifest.Document.Model.Revision &&
            document.ArchiveSha256 ==
                manifest.Document.Runtime.ArchiveSha256 &&
            document.ModelSha256 == manifest.Document.Model.Sha256 &&
            !document.AutomaticAcquisitionPerformed &&
            !document.DeniedEgressEvidenceIncluded &&
            document.Files is { Length: >= 1 and <= MaximumPackageEntries },
            LocalSttProvisioningFailure.InvalidReceipt);
        ProvisioningGuard.Sha256(
            document.ImportEvidenceSha256,
            LocalSttProvisioningFailure.InvalidReceipt);
        ProvisioningGuard.Sha256(
            document.SbomSha256,
            LocalSttProvisioningFailure.InvalidReceipt);
        var paths = new HashSet<string>(StringComparer.Ordinal);
        var casePaths = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var file in document.Files)
        {
            ProvisioningGuard.Require(file is not null,
                LocalSttProvisioningFailure.InvalidReceipt);
            if (file is null)
                throw new LocalSttProvisioningException(LocalSttProvisioningFailure.InvalidReceipt);
            ValidatePackageRelativePath(file.Path);
            ProvisioningGuard.Require(
                paths.Add(file.Path) &&
                casePaths.Add(file.Path) &&
                file.Bytes > 0 &&
                file.Purpose is { Length: >= 1 and <= 64 },
                LocalSttProvisioningFailure.InvalidReceipt);
            ProvisioningGuard.Sha256(
                file.Sha256,
                LocalSttProvisioningFailure.InvalidReceipt);
        }
        ProvisioningGuard.Require(
            paths.Contains(OwnerFile) &&
            paths.Contains($"{MetadataDirectory}/{ManifestFile}") &&
            paths.Contains($"{MetadataDirectory}/{EvidenceFile}") &&
            paths.Contains($"{MetadataDirectory}/{SbomFile}") &&
            !paths.Contains($"{MetadataDirectory}/{ReceiptFile}"),
            LocalSttProvisioningFailure.InvalidReceipt);
    }

    private void VerifyLayout(
        string root,
        IReadOnlyCollection<PackageFileDocument> inventory,
        CancellationToken cancellationToken)
    {
        var expectedFiles = inventory.ToDictionary(
            file => file.Path,
            StringComparer.Ordinal);
        expectedFiles.Add(
            $"{MetadataDirectory}/{ReceiptFile}",
            new PackageFileDocument
            {
                Path = $"{MetadataDirectory}/{ReceiptFile}",
                Bytes = 1,
                Sha256 = "",
                Purpose = "package-receipt"
            });
        var expectedDirectories = new HashSet<string>(
            new[]
            {
                PayloadDirectory,
                DownloadsDirectory,
                RuntimeDirectory,
                ModelsDirectory,
                NoticesDirectory,
                MetadataDirectory
            },
            StringComparer.Ordinal);
        var remaining = expectedFiles.Keys.ToHashSet(StringComparer.Ordinal);
        var directories = new Stack<string>();
        directories.Push(root);
        var entries = 0;
        while (directories.TryPop(out var directory))
        {
            pathInspector.AssertSafeExisting(directory, directory: true);
            foreach (var path in Directory.EnumerateFileSystemEntries(directory)
                         .Take(MaximumPackageEntries + 1))
            {
                cancellationToken.ThrowIfCancellationRequested();
                entries++;
                ProvisioningGuard.Require(
                    entries <= MaximumPackageEntries,
                    LocalSttProvisioningFailure.InvalidReceipt);
                var attributes = File.GetAttributes(path);
                ProvisioningGuard.Require(
                    (attributes & (FileAttributes.ReparsePoint |
                        FileAttributes.Device)) == 0,
                    LocalSttProvisioningFailure.InvalidReceipt);
                var relative = Path.GetRelativePath(root, path)
                    .Replace(Path.DirectorySeparatorChar, '/');
                ValidatePackageRelativePath(relative);
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    ProvisioningGuard.Require(
                        expectedDirectories.Remove(relative),
                        LocalSttProvisioningFailure.InvalidReceipt);
                    directories.Push(path);
                    continue;
                }
                ProvisioningGuard.Require(
                    remaining.Remove(relative),
                    LocalSttProvisioningFailure.InvalidReceipt);
                if (relative == $"{MetadataDirectory}/{ReceiptFile}")
                    continue;
                var expected = expectedFiles[relative];
                using var input = OpenPackageRead(path);
                PackageContentReader.CopyAndHash(
                    input,
                    null,
                    expected.Bytes,
                    expected.Sha256,
                    LocalSttProvisioningFailure.InvalidReceipt,
                    null,
                    cancellationToken);
            }
        }
        ProvisioningGuard.Require(
            remaining.Count == 0 &&
            expectedDirectories.Count == 0,
            LocalSttProvisioningFailure.InvalidReceipt);
    }

    private void ValidateSbom(
        CycloneDxDocument sbom,
        PackageReceiptDocument receipt)
    {
        ProvisioningGuard.Require(
            sbom.BomFormat == "CycloneDX" &&
            sbom.SpecVersion == "1.6" &&
            sbom.Version == 1 &&
            sbom.Components is { Length: >= 3 and <= MaximumPackageEntries } &&
            sbom.Dependencies is { Length: >= 3 and <= 19 } &&
            sbom.Metadata.Tools.Components.Length == 1 &&
            sbom.Components.Any(component =>
                component.BomRef == "package:martlet-local-stt") &&
            sbom.Components.Any(component =>
                component.BomRef == "component:whisper.cpp") &&
            sbom.Components.Any(component =>
                component.BomRef == "component:whisper-model"),
            LocalSttProvisioningFailure.InvalidReceipt);
        var references = sbom.Components
            .Select(component => component.BomRef)
            .ToHashSet(StringComparer.Ordinal);
        ProvisioningGuard.Require(
            references.Count == sbom.Components.Length &&
            sbom.Dependencies.All(dependency =>
                references.Contains(dependency.Ref) &&
                dependency.DependsOn.All(references.Contains)),
            LocalSttProvisioningFailure.InvalidReceipt);
        var sbomReceipt = receipt.Files.SingleOrDefault(file =>
            file.Path == $"{MetadataDirectory}/{SbomFile}");
        ProvisioningGuard.Require(
            sbomReceipt is not null &&
            sbomReceipt.Sha256 == receipt.SbomSha256,
            LocalSttProvisioningFailure.InvalidReceipt);
    }

    private bool TryReadOwner(
        string directory,
        string transactionId,
        CancellationToken cancellationToken)
    {
        try
        {
            pathInspector.AssertSafeExisting(directory, directory: true);
            var ownerPath = PackagePath(directory, OwnerFile);
            var bytes = ReadBoundedFile(
                ownerPath,
                ProvisioningWire.MaximumOwnerBytes,
                cancellationToken);
            var owner = ProvisioningWire.Read<PackageStageOwnerDocument>(
                bytes,
                ProvisioningWire.MaximumOwnerBytes,
                LocalSttProvisioningFailure.InvalidReceipt,
                canonical: true);
            return IsExpectedOwner(owner, transactionId);
        }
        catch (Exception error) when (
            error is LocalSttProvisioningException or
                FileNotFoundException or
                DirectoryNotFoundException)
        {
            return false;
        }
    }

    private void DeleteOwnedTree(
        string directory,
        string transactionId,
        CancellationToken cancellationToken,
        bool requireOwner,
        string? expectedIdentity = null,
        string[]? noticeFiles = null)
    {
        if (!Directory.Exists(directory))
            return;
        ProvisioningGuard.Require(requireOwner || expectedIdentity is not null,
            LocalSttProvisioningFailure.StagingConflict);
        Point(LocalSttImportIoPoint.BeforeCleanup, cancellationToken);
        using var parentLease = new WindowsDirectoryLease(Path.GetDirectoryName(directory)!);
        var ownerPath = PackagePath(directory, OwnerFile);
        FileStream? ownerLock = null;
        byte[]? savedOwner = null;
        string? ownerIdentity = null;
        var directoryLeases = new Dictionary<string, WindowsDirectoryLease>(PathComparer);
        try
        {
            directoryLeases.Add(directory, new WindowsDirectoryLease(directory));
            var actualIdentity = ProvisioningFileIdentity.ReadDirectory(directory);
            ProvisioningGuard.Require(expectedIdentity is null || actualIdentity == expectedIdentity,
                LocalSttProvisioningFailure.StagingConflict);
            if (File.Exists(ownerPath))
            {
                ownerLock = OpenPackageRead(ownerPath);
                ownerIdentity = ProvisioningFileIdentity.Read(ownerLock);
                if (requireOwner)
                {
                    var ownerBytes = ReadBoundedStream(
                        ownerLock,
                        ProvisioningWire.MaximumOwnerBytes,
                        cancellationToken,
                        LocalSttProvisioningFailure.StagingConflict);
                    var owner = ProvisioningWire.Read<PackageStageOwnerDocument>(
                        ownerBytes,
                        ProvisioningWire.MaximumOwnerBytes,
                        LocalSttProvisioningFailure.StagingConflict,
                        canonical: true);
                    ProvisioningGuard.Require(IsExpectedOwner(owner, transactionId),
                        LocalSttProvisioningFailure.StagingConflict);
                    noticeFiles = owner.NoticeFiles;
                    savedOwner = ownerBytes;
                }
                else if (ownerLock.Length is > 0 and <= ProvisioningWire.MaximumOwnerBytes)
                {
                    savedOwner = ReadBoundedStream(ownerLock, ProvisioningWire.MaximumOwnerBytes,
                        cancellationToken, LocalSttProvisioningFailure.StagingConflict);
                }
            }
            else
            {
                ProvisioningGuard.Require(
                    !requireOwner,
                    LocalSttProvisioningFailure.StagingConflict);
            }

            var pending = new Stack<string>();
            var directories = new List<(string Path, string Identity)>();
            var files = new List<(string Path, string Identity)>();
            pending.Push(directory);
            var entries = 0;
            while (pending.TryPop(out var current))
            {
                cancellationToken.ThrowIfCancellationRequested();
                pathInspector.AssertSafeExisting(current, directory: true);
                if (!directoryLeases.ContainsKey(current))
                    directoryLeases.Add(current, new WindowsDirectoryLease(current));
                directories.Add((current, ProvisioningFileIdentity.ReadDirectory(current)));
                foreach (var path in Directory.EnumerateFileSystemEntries(current))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    entries++;
                    ProvisioningGuard.Require(
                        entries <= MaximumPackageEntries,
                        LocalSttProvisioningFailure.StagingConflict);
                    var attributes = File.GetAttributes(path);
                    var relative = Path.GetRelativePath(directory, path).Replace('\\', '/');
                    ValidateCleanupEntry(relative, attributes.HasFlag(FileAttributes.Directory), noticeFiles ?? []);
                    ProvisioningGuard.Require(
                        (attributes & (FileAttributes.ReparsePoint |
                            FileAttributes.Device)) == 0,
                        LocalSttProvisioningFailure.StagingConflict);
                    if (string.Equals(path, ownerPath, PathComparison))
                        continue;
                    if ((attributes & FileAttributes.Directory) != 0)
                        pending.Push(path);
                    else
                    {
                        using var file = OpenPackageRead(path);
                        files.Add((path, ProvisioningFileIdentity.Read(file)));
                    }
                }
            }
            foreach (var file in files)
            {
                Point(LocalSttImportIoPoint.BeforeCleanupEntry, cancellationToken);
                ProvisioningFileIdentity.DeleteFile(file.Path, file.Identity);
            }
            foreach (var item in directories.AsEnumerable().Reverse())
            {
                Point(LocalSttImportIoPoint.BeforeCleanupEntry, cancellationToken);
                if (string.Equals(item.Path, directory, PathComparison) && ownerLock is not null)
                {
                    ownerLock.Dispose();
                    ownerLock = null;
                    ProvisioningFileIdentity.DeleteFile(ownerPath, ownerIdentity!);
                }
                directoryLeases[item.Path].Dispose();
                directoryLeases.Remove(item.Path);
                try
                {
                    ProvisioningFileIdentity.DeleteDirectory(item.Path, item.Identity);
                }
                catch (Exception error) when (Handled(error))
                {
                    if (item.Path == directory && savedOwner is not null && !File.Exists(ownerPath) &&
                        ProvisioningFileIdentity.ReadDirectory(directory) == actualIdentity)
                    {
                        using var restored = ProvisioningFile.Create(ownerPath);
                        restored.Write(savedOwner);
                        restored.Flush(flushToDisk: true);
                    }
                    throw;
                }
            }
        }
        finally
        {
            ownerLock?.Dispose();
            foreach (var lease in directoryLeases.Values.Reverse())
                lease.Dispose();
        }
    }

    private void ValidateCleanupEntry(string relative, bool directory, string[] noticeFiles)
    {
        ValidatePackageRelativePath(relative);
        var allowed = directory
            ? relative is PayloadDirectory or DownloadsDirectory or RuntimeDirectory or ModelsDirectory or NoticesDirectory or MetadataDirectory
            : relative == OwnerFile ||
              relative == $"{DownloadsDirectory}/{manifest.Document.Runtime.ArchiveFileName}" ||
              relative == $"{ModelsDirectory}/{manifest.Document.Model.FileName}" ||
              manifest.Document.Runtime.Files.Any(file => relative == $"{RuntimeDirectory}/{file.InstalledName}") ||
              relative == $"{MetadataDirectory}/{ManifestFile}" || relative == $"{MetadataDirectory}/{EvidenceFile}" ||
              relative == $"{MetadataDirectory}/{SbomFile}" || relative == $"{MetadataDirectory}/{ReceiptFile}" ||
              noticeFiles.Any(name => relative == $"{NoticesDirectory}/{name}");
        ProvisioningGuard.Require(allowed, LocalSttProvisioningFailure.StagingConflict);
    }

    private bool IsExpectedOwner(
        PackageStageOwnerDocument owner,
        string transactionId)
    {
        if (owner.FormatVersion != 1 ||
            owner.Kind != "martlet_local_stt_owned_package" ||
            owner.TransactionId != transactionId ||
            owner.PackageId != manifest.Id ||
            owner.ManifestSha256 != manifest.DocumentSha256 ||
            owner.NoticeFiles is not { Length: >= 2 and <= 16 } ||
            owner.NoticeFiles.Distinct(StringComparer.OrdinalIgnoreCase).Count() != owner.NoticeFiles.Length)
            return false;
        try
        {
            foreach (var name in owner.NoticeFiles)
            {
                ProvisioningGuard.Require(name is { Length: >= 5 and <= 128 } &&
                    name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase),
                    LocalSttProvisioningFailure.StagingConflict);
                ProvisioningPath.ValidateSegment(name);
            }
            ProvisioningGuard.Sha256(
                owner.ImportEvidenceSha256,
                LocalSttProvisioningFailure.StagingConflict);
        }
        catch (Exception error) when (error is LocalSttProvisioningException or LocalPathException)
        {
            return false;
        }
        var expectedTransaction = ProvisioningGuard.Fingerprint(
            manifest.DocumentSha256,
            owner.ImportEvidenceSha256)[..32];
        return expectedTransaction == transactionId;
    }

    private static bool TryStageTransactionId(
        string directoryName,
        out string transactionId)
    {
        const string prefix = ".martlet-local-stt-";
        const string suffix = ".pending";
        transactionId = "";
        if (!directoryName.StartsWith(prefix, StringComparison.Ordinal) ||
            !directoryName.EndsWith(suffix, StringComparison.Ordinal))
            return false;
        transactionId = directoryName[
            prefix.Length..^suffix.Length];
        return transactionId.Length == 32 &&
            transactionId.All(ManifestRules.IsLowerHex);
    }

    private static string StageTransactionId(string stagePath)
    {
        if (!TryStageTransactionId(
                Path.GetFileName(stagePath),
                out var transactionId))
            throw new LocalSttProvisioningException(
                LocalSttProvisioningFailure.StagingConflict);
        return transactionId;
    }

    private static string PlanFingerprint(
        NormalizedImportRequest request,
        string evidenceSha256,
        string stagingPath,
        ImportSourceSnapshot? sources,
        long requiredBytes)
    {
        var values = new List<string>
        {
            "local-stt-import-plan-v1",
            request.ArchivePath,
            request.ModelPath,
            request.NoticeDirectory,
            request.DestinationPath,
            stagingPath,
            evidenceSha256,
            ProvisioningGuard.Invariant(requiredBytes)
        };
        if (sources is not null)
        {
            foreach (var source in new[]
                     {
                         sources.Archive,
                         sources.Model
                     }.Concat(sources.Notices))
            {
                values.Add(source.Path);
                values.Add(ProvisioningGuard.Invariant(source.Bytes));
                values.Add(source.Sha256);
                values.Add(ProvisioningGuard.Invariant(
                    source.LastWriteUtcTicks));
                values.Add(source.FileIdentity);
            }
        }
        return ProvisioningGuard.Fingerprint(values.ToArray());
    }

    private void ValidateSource(
        FileStream stream,
        SourceFileSnapshot expected,
        CancellationToken cancellationToken)
    {
        stream.Position = 0;
        ProvisioningGuard.Require(
            ProvisioningFileIdentity.Read(stream) == expected.FileIdentity,
            LocalSttProvisioningFailure.SourceChanged);
        PackageContentReader.CopyAndHash(
            stream,
            null,
            expected.Bytes,
            expected.Sha256,
            LocalSttProvisioningFailure.SourceChanged,
            null,
            cancellationToken);
        stream.Position = 0;
        var info = new FileInfo(expected.Path);
        info.Refresh();
        ProvisioningGuard.Require(
            info.Exists &&
            info.Length == expected.Bytes &&
            info.LastWriteTimeUtc.Ticks == expected.LastWriteUtcTicks,
            LocalSttProvisioningFailure.SourceChanged);
    }

    private static void ValidateNoticeContent(byte[] bytes)
    {
        string text;
        try
        {
            text = new UTF8Encoding(
                encoderShouldEmitUTF8Identifier: false,
                throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            throw new LocalSttProvisioningException(
                LocalSttProvisioningFailure.LicenseNoticeMismatch);
        }
        ProvisioningGuard.Require(
            text.Length > 0 &&
            !text.Any(character =>
                character == '\0' ||
                char.IsControl(character) &&
                character is not '\r' and not '\n' and not '\t'),
            LocalSttProvisioningFailure.LicenseNoticeMismatch);
    }

    private string NormalizeExistingFile(string path)
    {
        var normalized = NormalizeAbsolute(path);
        try
        {
            pathInspector.AssertSafeExisting(normalized, directory: false);
            return normalized;
        }
        catch (FileNotFoundException)
        {
            throw new LocalSttProvisioningException(
                LocalSttProvisioningFailure.SourceMissing);
        }
        catch (DirectoryNotFoundException)
        {
            throw new LocalSttProvisioningException(
                LocalSttProvisioningFailure.SourceMissing);
        }
    }

    private string NormalizeExistingDirectory(string path)
    {
        var normalized = NormalizeAbsolute(path);
        try
        {
            pathInspector.AssertSafeExisting(normalized, directory: true);
            return normalized;
        }
        catch (FileNotFoundException)
        {
            throw new LocalSttProvisioningException(
                LocalSttProvisioningFailure.SourceMissing);
        }
        catch (DirectoryNotFoundException)
        {
            throw new LocalSttProvisioningException(
                LocalSttProvisioningFailure.SourceMissing);
        }
    }

    private string NormalizeDestination(string path)
    {
        var normalized = NormalizeAbsolute(path);
        var name = Path.GetFileName(normalized);
        ProvisioningGuard.Require(
            name.Length is >= 1 and <= 128 &&
            name[0] != '.' &&
            name.All(character =>
                char.IsAsciiLetterOrDigit(character) ||
                character is '.' or '_' or '-'),
            LocalSttProvisioningFailure.SourceUnsafe);
        var parent = Path.GetDirectoryName(normalized) ??
            throw new LocalSttProvisioningException(
                LocalSttProvisioningFailure.SourceUnsafe);
        pathInspector.AssertSafeExisting(parent, directory: true);
        return normalized;
    }

    private static string NormalizeAbsolute(string path)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(path);
            ProvisioningGuard.Require(
                path.Length is >= 4 and <= 1024 &&
                Path.IsPathFullyQualified(path) &&
                !path.StartsWith(@"\\", StringComparison.Ordinal) &&
                !path.StartsWith("//", StringComparison.Ordinal) &&
                !path.Contains('\0'),
                LocalSttProvisioningFailure.SourceUnsafe);
            var full = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(path));
            ProvisioningGuard.Require(
                string.Equals(full, path, PathComparison) &&
                full != Path.TrimEndingDirectorySeparator(
                    Path.GetPathRoot(full)!),
                LocalSttProvisioningFailure.SourceUnsafe);
            if (OperatingSystem.IsWindows())
                ProvisioningGuard.Require(
                    full.Length >= 3 &&
                    full[1] == ':' &&
                    !full[2..].Contains(':'),
                    LocalSttProvisioningFailure.SourceUnsafe);
            return ProvisioningPath.Normalize(full);
        }
        catch (LocalSttProvisioningException)
        {
            throw;
        }
        catch (Exception error) when (
            error is ArgumentException or
                NotSupportedException or
                PathTooLongException)
        {
            throw new LocalSttProvisioningException(
                LocalSttProvisioningFailure.SourceUnsafe);
        }
    }

    private static string PackagePath(string root, string portablePath)
    {
        ValidatePackageRelativePath(portablePath);
        var relative = portablePath.Replace(
            '/',
            Path.DirectorySeparatorChar);
        var path = Path.GetFullPath(Path.Combine(root, relative));
        ProvisioningGuard.Require(
            Within(path, root),
            LocalSttProvisioningFailure.StagingConflict);
        return path;
    }

    private static void ValidatePackageRelativePath(string path)
    {
        ProvisioningGuard.Require(
            path.Length is >= 1 and <= 512 &&
            !path.StartsWith('/') &&
            !path.Contains('\\') &&
            !path.Contains(':') &&
            path.Split('/').All(segment =>
                segment.Length is >= 1 and <= 128 &&
                segment is not "." and not ".." &&
                segment.All(character =>
                    char.IsAsciiLetterOrDigit(character) ||
                    character is '.' or '_' or '-')),
            LocalSttProvisioningFailure.InvalidReceipt);
        foreach (var segment in path.Split('/'))
            ProvisioningPath.ValidateSegment(segment);
    }

    private void EnsureDestinationAvailable(
        string destination,
        string? exceptStagingPath)
    {
        pathInspector.AssertSafeExisting(
            Path.GetDirectoryName(destination)!,
            directory: true);
        if (PathExists(destination) ||
            exceptStagingPath is not null &&
            PathExists(exceptStagingPath))
            throw new LocalSttProvisioningException(
                LocalSttProvisioningFailure.DestinationExists);
    }

    private void RequireSpace(string parent, long requiredBytes)
    {
        pathInspector.AssertSafeExisting(parent, directory: true);
        ProvisioningGuard.Require(
            availableBytes(parent) >= requiredBytes,
            LocalSttProvisioningFailure.InsufficientDisk);
    }

    private FileStream OpenSourceRead(string path)
    {
        pathInspector.AssertSafeExisting(path, directory: false);
        return ProvisioningFile.OpenRead(path);
    }

    private FileStream OpenPackageRead(string path)
    {
        pathInspector.AssertSafeExisting(path, directory: false);
        return ProvisioningFile.OpenRead(path);
    }

    private byte[] ReadBoundedFile(
        string path,
        int maximum,
        CancellationToken cancellationToken)
    {
        using var input = OpenPackageRead(path);
        return ReadBoundedStream(
            input,
            maximum,
            cancellationToken,
            LocalSttProvisioningFailure.InvalidReceipt);
    }

    private static byte[] ReadBoundedStream(
        FileStream input,
        int maximum,
        CancellationToken cancellationToken,
        LocalSttProvisioningFailure failure)
    {
        input.Position = 0;
        ProvisioningGuard.Require(
            input.Length is > 0 && input.Length <= maximum,
            failure);
        var bytes = new byte[checked((int)input.Length)];
        var offset = 0;
        while (offset < bytes.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = input.Read(
                bytes,
                offset,
                bytes.Length - offset);
            if (read == 0)
                throw new LocalSttProvisioningException(
                    failure);
            offset += read;
        }
        input.Position = 0;
        return bytes;
    }

    private void Point(
        LocalSttImportIoPoint point,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Io?.Invoke(point, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
    }

    private T Run<T>(
        Func<T> operation,
        CancellationToken cancellationToken,
        bool allowPending = false)
    {
        if (Interlocked.CompareExchange(ref busy, 1, 0) != 0)
            throw new LocalSttProvisioningException(
                LocalSttProvisioningFailure.Busy);
        try
        {
            ProvisioningHost.RequireSupported();
            cancellationToken.ThrowIfCancellationRequested();
            if (!allowPending && pendingDirectory is not null)
                throw new LocalSttProvisioningException(
                    LocalSttProvisioningFailure.CleanupPending,
                    pendingDirectory);
            return operation();
        }
        catch (Exception error) when (Handled(error))
        {
            throw Translate(error, cancellationToken);
        }
        finally
        {
            Volatile.Write(ref busy, 0);
        }
    }

    private static bool Handled(Exception error) =>
        error is LocalSttProvisioningException or
            LocalPathException or
            IOException or
            UnauthorizedAccessException or
            SecurityException or
            OperationCanceledException or
            ArgumentException or
            NotSupportedException or
            OverflowException;

    private static LocalSttProvisioningException Translate(
        Exception error,
        CancellationToken cancellationToken) =>
        error as LocalSttProvisioningException ??
        new LocalSttProvisioningException(
            Failure(error, cancellationToken));

    private static LocalSttProvisioningFailure Failure(
        Exception error,
        CancellationToken cancellationToken) => error switch
        {
            LocalSttProvisioningException provisioning =>
                provisioning.Failure,
            OperationCanceledException when cancellationToken
                .IsCancellationRequested =>
                LocalSttProvisioningFailure.Canceled,
            UnauthorizedAccessException or SecurityException =>
                LocalSttProvisioningFailure.AccessDenied,
            LocalPathException =>
                LocalSttProvisioningFailure.SourceUnsafe,
            IOException io when (io.HResult & 0xffff) is 112 or 39 or 28 =>
                LocalSttProvisioningFailure.InsufficientDisk,
            IOException =>
                LocalSttProvisioningFailure.StorageFailure,
            _ => LocalSttProvisioningFailure.InvalidRequest
        };

    private static bool PathExists(string path) =>
        File.Exists(path) || Directory.Exists(path);

    private static bool Within(string path, string directory)
    {
        var prefix = directory.EndsWith(Path.DirectorySeparatorChar)
            ? directory
            : directory + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, PathComparison);
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

    private sealed class PinnedSources : IDisposable
    {
        private readonly PhysicalLocalSttPackageImporter owner;
        private readonly ImportSourceSnapshot expected;
        internal FileStream Archive { get; }
        internal FileStream Model { get; }
        internal IReadOnlyDictionary<string, FileStream> Notices { get; }

        internal PinnedSources(
            PhysicalLocalSttPackageImporter owner,
            ImportSourceSnapshot expected,
            FileStream archive,
            FileStream model,
            IReadOnlyDictionary<string, FileStream> notices)
        {
            this.owner = owner;
            this.expected = expected;
            Archive = archive;
            Model = model;
            Notices = notices;
        }

        internal void ValidateHandles(CancellationToken cancellationToken)
        {
            owner.ValidateSource(
                Archive,
                expected.Archive,
                cancellationToken);
            owner.ValidateSource(
                Model,
                expected.Model,
                cancellationToken);
            foreach (var notice in expected.Notices)
                owner.ValidateSource(
                    Notices[Path.GetFileName(notice.Path)],
                    notice,
                    cancellationToken);
        }

        internal void ValidateNamedSources(
            CancellationToken cancellationToken)
        {
            ValidateHandles(cancellationToken);
            foreach (var source in new[]
                     {
                         expected.Archive,
                         expected.Model
                     }.Concat(expected.Notices))
            {
                using var named = owner.OpenSourceRead(source.Path);
                owner.ValidateSource(
                    named,
                    source,
                    cancellationToken);
            }
        }

        public void Dispose()
        {
            foreach (var notice in Notices.Values)
                notice.Dispose();
            Model.Dispose();
            Archive.Dispose();
        }
    }
}
