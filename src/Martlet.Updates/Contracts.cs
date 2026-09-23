using System.Collections.ObjectModel;
using System.Security.Cryptography;
using Martlet.Core.Settings;

namespace Martlet.Updates;

public enum StagingFailure
{
    TrustUnconfigured, UntrustedSignature, InvalidManifest, IncompatibleFormat, IncompatibleSettings,
    IncompatibleRid, InvalidVersion, CorruptArchive, UnsafeEntry, CapacityExceeded, InsufficientDisk,
    Conflict, Busy, AccessDenied, Unavailable, Cancelled, CleanupPending, InvalidReceipt
}

public sealed class StagingException : Exception
{
    public StagingFailure Failure { get; }
    public StagingFailure? OriginalFailure { get; }
    // Explicit local recovery information, deliberately absent from Message/ToString.
    public string? RetainedDirectory { get; }
    internal StagingException(StagingFailure failure, string? retained = null, StagingFailure? original = null)
        : base(failure switch
        {
            StagingFailure.TrustUnconfigured => "No approved update signing keys are configured. Ask the owner to supply an independently authorized public-key policy.",
            StagingFailure.UntrustedSignature => "The candidate is unsigned or its signature is not trusted. Obtain an intact candidate from the authorized publisher; do not trust keys supplied by the package.",
            StagingFailure.IncompatibleFormat => "Unsupported candidate or payload format. Use a compatible staging reader.",
            StagingFailure.IncompatibleSettings => "The candidate cannot read the known current settings schema. Use a compatible candidate; settings were not opened or changed.",
            StagingFailure.IncompatibleRid => "The candidate targets a different platform. Select the exact installed RID.",
            StagingFailure.InvalidVersion => "An exact newer four-part application version is required. Rollback and equal-version repair need separate authorization.",
            StagingFailure.UnsafeEntry => "The archive contains an unsafe or unsupported entry. Request a package in the documented restricted ZIP format.",
            StagingFailure.CapacityExceeded => "The candidate exceeds staging size, count, path or compression limits. Obtain a bounded package.",
            StagingFailure.InsufficientDisk => "Insufficient local staging space. Free space outside the installed version and data, then preview again.",
            StagingFailure.Conflict => "The source, destination, installed facts or preview changed, or approval was used. Preserve existing files and review a fresh preview.",
            StagingFailure.Busy => "Another staging operation or owned cleanup is pending. Wait for its owner to finish or retry its exact cleanup.",
            StagingFailure.AccessDenied => "Local storage access was denied. Check the selected directory and access without elevating or changing security policy.",
            StagingFailure.Cancelled => "Staging was cancelled before finalization. No version was activated.",
            StagingFailure.CleanupPending => "Staging failed and exact owned cleanup is pending. Retain operation ownership and retry cleanup after resolving local storage access.",
            StagingFailure.InvalidReceipt => "The staged receipt or retained verified content is damaged or no longer matches current facts. Do not activate it; stage a fresh candidate.",
            StagingFailure.InvalidManifest => "The candidate manifest is malformed, ambiguous or inconsistent. Obtain an intact signed candidate.",
            StagingFailure.CorruptArchive => "The archive is corrupt or differs from its signed inventory. Obtain the exact signed candidate.",
            _ => "Local staging IO failed. Check access, available space and competing writers; preserve the source, installed version and data."
        })
    { Failure = failure; RetainedDirectory = retained; OriginalFailure = original; }
}

public sealed record InstalledVersionFacts(
    string Version, string Rid, string InstallationDirectory, string InstallationRevision,
    int SettingsSchemaVersion, string SettingsRevision)
{
    internal void Validate()
    {
        Wire.Version(Version);
        if (Rid != "win-x64") throw new StagingException(StagingFailure.IncompatibleRid);
        if (SettingsSchemaVersion is < 1 or > AppSettings.CurrentSchemaVersion)
            throw new StagingException(StagingFailure.IncompatibleSettings);
        if (!Wire.IsHash(InstallationRevision) ||
            SettingsRevision is not { Length: > 0 and <= 128 } || SettingsRevision.Any(char.IsControl))
            throw new StagingException(StagingFailure.Conflict);
        // This is an owner-provided identity, not permission to read installed files or settings.
        LocalPaths.Canonical(InstallationDirectory);
    }
}

public sealed record StagingLimits
{
    public long MaximumArchiveBytes { get; init; } = 512L * 1024 * 1024;
    public long MaximumExpandedBytes { get; init; } = 2L * 1024 * 1024 * 1024;
    public long MaximumFileBytes { get; init; } = 256L * 1024 * 1024;
    public int MaximumEntries { get; init; } = 8192;
    public int MaximumDirectories { get; init; } = 8192;
    public int MaximumCompressionRatio { get; init; } = 200;
    internal void Validate()
    {
        if (MaximumArchiveBytes is < 22 or > 536870912 || MaximumExpandedBytes is < 1 or > 2147483648 ||
            MaximumFileBytes is < 1 or > 268435456 || MaximumEntries is < 3 or > 8192 ||
            MaximumDirectories is < 1 or > 8192 || MaximumCompressionRatio is < 1 or > 200)
            throw new ArgumentOutOfRangeException(nameof(StagingLimits));
    }
}

public sealed class UpdateTrustPolicy
{
    private readonly Dictionary<string, byte[]> keys = new(StringComparer.Ordinal);
    internal string Digest => Wire.Hash(Wire.Write(keys.Keys.Order(StringComparer.Ordinal).ToArray()));
    public UpdateTrustPolicy(IEnumerable<byte[]> approvedSubjectPublicKeyInfos)
    {
        ArgumentNullException.ThrowIfNull(approvedSubjectPublicKeyInfos);
        foreach (var bytes in approvedSubjectPublicKeyInfos)
        {
            if (keys.Count >= 16 || bytes is not { Length: > 0 and <= 1024 })
                throw new ArgumentException("Supply at most sixteen bounded RSA public keys.");
            var copy = bytes.ToArray();
            try
            {
                using var rsa = RSA.Create();
                rsa.ImportSubjectPublicKeyInfo(copy, out var read);
                if (read != copy.Length || rsa.KeySize is not (3072 or 4096) ||
                    !rsa.ExportSubjectPublicKeyInfo().AsSpan().SequenceEqual(copy))
                    throw new ArgumentException("Supply canonical DER RSA-3072 or RSA-4096 SubjectPublicKeyInfo.");
            }
            catch (CryptographicException)
            { throw new ArgumentException("Supply valid RSA public-key policy, not package-provided trust."); }
            if (!keys.TryAdd(Wire.Hash(copy), copy)) throw new ArgumentException("Duplicate approved signing key.");
        }
    }

    internal void RequireConfigured()
    {
        if (keys.Count == 0) throw new StagingException(StagingFailure.TrustUnconfigured);
    }

    internal void Verify(CandidateManifest manifest, byte[] bytes, byte[] signature)
    {
        RequireConfigured();
        if (manifest.Algorithm != "RSA-PSS-SHA256" || !keys.TryGetValue(manifest.SignerId, out var key))
            throw new StagingException(StagingFailure.UntrustedSignature);
        using var rsa = RSA.Create();
        rsa.ImportSubjectPublicKeyInfo(key, out _);
        if (signature.Length != rsa.KeySize / 8 ||
            !rsa.VerifyData(bytes, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pss))
            throw new StagingException(StagingFailure.UntrustedSignature);
    }
}

public sealed record VerifiedFile(string Path, long Bytes, string Sha256);

public sealed class StagingPlan
{
    internal object Owner { get; }
    internal long Generation { get; }
    internal VerifiedCandidate Candidate { get; }
    internal Guid Id { get; } = Guid.NewGuid();
    private int approved;
    public string ArchivePath { get; }
    public string EnvelopePath { get; }
    public string Destination { get; }
    public InstalledVersionFacts Installed { get; }
    public string ArchiveSha256 => Candidate.Manifest.ArchiveSha256;
    public string EnvelopeSha256 => Wire.Hash(Candidate.EnvelopeBytes);
    public string ManifestSha256 => Wire.Hash(Candidate.ManifestBytes);
    public string SignerId => Candidate.Manifest.SignerId;
    public string Version => Candidate.Manifest.ApplicationVersion;
    public string Rid => Candidate.Manifest.Rid;
    public long ArchiveBytes => Candidate.Manifest.ArchiveBytes;
    public long ExpandedBytes => Candidate.ExpandedBytes;
    public long RequiredFreeBytes { get; }
    public int DirectoryCount => Candidate.DirectoryCount;
    public int SettingsMinimumReader => Candidate.Manifest.SettingsMinimumReader;
    public int SettingsMaximumReader => Candidate.Manifest.SettingsMaximumReader;
    public ReadOnlyCollection<VerifiedFile> Files { get; }

    internal StagingPlan(object owner, long generation, string archive, string envelope, string destination,
        InstalledVersionFacts installed, VerifiedCandidate candidate)
    {
        Owner = owner; Generation = generation; ArchivePath = archive; EnvelopePath = envelope;
        Destination = destination; Installed = installed; Candidate = candidate;
        Files = Array.AsReadOnly(candidate.Manifest.Files.Select(f => new VerifiedFile(f.Path, f.Bytes, f.Sha256)).ToArray());
        RequiredFreeBytes = candidate.Manifest.ArchiveBytes + candidate.ExpandedBytes +
            candidate.EnvelopeBytes.Length + Wire.MaximumReceiptBytes + 16L * 1024 * 1024 +
            (Files.Count + DirectoryCount + 8L) * 65536;
    }

    public StagingApproval Approve(string archiveSha256, string envelopeSha256, string destination,
        InstalledVersionFacts installed)
    {
        if (archiveSha256 != ArchiveSha256 || envelopeSha256 != EnvelopeSha256 ||
            destination != Destination || installed != Installed || Interlocked.Exchange(ref approved, 1) != 0)
            throw new StagingException(StagingFailure.Conflict);
        return new(this);
    }
}

public sealed class StagingApproval
{
    private readonly StagingPlan plan;
    private int consumed;
    internal StagingApproval(StagingPlan plan) => this.plan = plan;
    internal void Consume(StagingPlan expected)
    {
        if (Interlocked.Exchange(ref consumed, 1) != 0 || !ReferenceEquals(plan, expected))
            throw new StagingException(StagingFailure.Conflict);
    }
}

public sealed class StagedReceipt
{
    public const string NextSteps = "NOT ACTIVATED. Require a fresh independently validated V07a pre-activation configuration snapshot receipt bound to current settings revision/profile, separate activation approval, publisher/release qualification, compatible migration, readiness checks and an N-1 rollback plan. This receipt authorizes none of them.";
    public string Destination { get; }
    public string ReceiptSha256 { get; }
    public string ArchiveSha256 { get; }
    public string ManifestSha256 { get; }
    public string SignerId { get; }
    public string Version { get; }
    public string Rid { get; }
    public InstalledVersionFacts Installed { get; }
    public ReadOnlyCollection<VerifiedFile> Files { get; }
    internal StagedReceipt(string destination, byte[] receiptBytes, VerifiedCandidate candidate, InstalledVersionFacts installed)
    {
        Destination = destination; ReceiptSha256 = Wire.Hash(receiptBytes);
        ArchiveSha256 = candidate.Manifest.ArchiveSha256; ManifestSha256 = Wire.Hash(candidate.ManifestBytes);
        SignerId = candidate.Manifest.SignerId; Version = candidate.Manifest.ApplicationVersion;
        Rid = candidate.Manifest.Rid; Installed = installed;
        Files = Array.AsReadOnly(candidate.Manifest.Files.Select(f => new VerifiedFile(f.Path, f.Bytes, f.Sha256)).ToArray());
    }
}
