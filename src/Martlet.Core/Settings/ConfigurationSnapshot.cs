using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Martlet.Core.Contracts;

namespace Martlet.Core.Settings;

public enum RecoveryFailure { InvalidBackup, Incompatible, WrongProfile, Conflict, Unavailable, CleanupPending, CleanupCapacity }

public sealed class RecoveryException : Exception
{
    public RecoveryFailure Failure { get; }
    public string? RetainedFile { get; }
    internal RecoveryException(RecoveryFailure failure, string? retainedFile = null) : base(failure switch
    {
        RecoveryFailure.InvalidBackup => "Invalid, ambiguous, oversized or damaged configuration snapshot. Select an intact Martlet configuration snapshot; no settings were replaced.",
        RecoveryFailure.Incompatible => "Unsupported application or schema version. Use a compatible Martlet build. Diagnostic bundles and raw historical settings files are not configuration envelopes.",
        RecoveryFailure.WrongProfile => "This snapshot belongs to another profile. V07a supports only same-profile recovery into an existing valid profile. Reconfigure a new profile explicitly; do not copy foreign identities.",
        RecoveryFailure.Conflict => "The source, preview or destination revision changed, or approval was already used. Read a fresh preview and review it again; no replacement was authorized.",
        RecoveryFailure.CleanupPending => "Owned staging cleanup failed. Keep the retained file and retry owned cleanup after checking access and free space. A completed replacement is not rolled back.",
        RecoveryFailure.CleanupCapacity => "Restore would exceed sixteen owned credential cleanup references. Open Setup and explicitly remove selected detached keys first, then read a fresh restore preview. No reference was discarded or key automatically deleted.",
        _ => "Cannot access local recovery storage. Check free space, permissions, existing output and other Martlet writers. Preserve originals; do not elevate. Missing, corrupt or newer destination settings require separate manual recovery, not overwrite."
    })
    { Failure = failure; RetainedFile = retainedFile; }
}

// A local, lossless configuration envelope, NOT the redacted diagnostic projection.
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record SnapshotManifest : IContract
{
    public required int FormatVersion { get; init; }
    public required string Application { get; init; }
    public required string ProducerVersion { get; init; }
    public required int MinimumReaderFormat { get; init; }
    public required int SettingsSchemaVersion { get; init; }
    public required Guid SnapshotId { get; init; }
    public required Guid ProfileId { get; init; }
    public required DateTimeOffset CreatedUtc { get; init; }
    public required string SourceRevision { get; init; }
    public required byte[] SettingsBytes { get; init; }

    public void Validate()
    {
        ContractRules.Require(FormatVersion == 1 && MinimumReaderFormat == 1 &&
            Application == "Martlet.Configuration" && SettingsSchemaVersion is >= 1 and <= AppSettings.CurrentSchemaVersion,
            "Unsupported configuration envelope.", ErrorCode.UnsupportedVersion);
        ContractRules.Require(ProducerVersion is { Length: > 0 and <= 64 } &&
            Version.TryParse(ProducerVersion, out _) && SnapshotId != Guid.Empty && ProfileId != Guid.Empty &&
            CreatedUtc.Offset == TimeSpan.Zero && CreatedUtc > DateTimeOffset.UnixEpoch,
            "Invalid snapshot metadata.");
        ContractRules.Require(SettingsBytes is { Length: > 0 and <= AppSettings.MaxFileBytes } &&
            SourceRevision == ConfigurationSnapshot.Hash(SettingsBytes), "Invalid source digest.");
        var settings = SettingsJson.Read(SettingsBytes!);
        ContractRules.Require(settings.SchemaVersion == SettingsSchemaVersion && settings.Profile.Id == ProfileId,
            "Snapshot metadata does not match its payload.");
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record SnapshotEnvelope : IContract
{
    public required SnapshotManifest Manifest { get; init; }
    public required string Sha256 { get; init; }
    public void Validate()
    {
        ContractRules.Require(Manifest is not null, "Missing snapshot manifest.");
        Manifest!.Validate();
        ContractRules.Require(Sha256 == ConfigurationSnapshot.Hash(ContractJson.Write(Manifest)),
            "Snapshot integrity check failed.");
    }
}

public static class ConfigurationSnapshot
{
    public const int MaximumBytes = 262_144;
    public const string Scope = "LOCAL configuration only; NOT encrypted or a sanitized support bundle. " +
        "Includes exact settings, profile/route/model/device preferences, memory enable/path policy, opaque credential references and cleanup metadata. " +
        "Device identifiers and configuration may be personal. Excludes secret values/OS vault, environment, conversations, audio, " +
        "memory facts/store/exports, models, arbitrary files, crash dumps and optional support journal/logs. No upload or cloud storage.";

    // Envelope consistency only: callers still own freshness, source lifetime and authorization.
    public static ConfigurationSnapshotInspection Inspect(ReadOnlyMemory<byte> bytes)
    {
        if (bytes.Length is 0 or > MaximumBytes) throw new RecoveryException(RecoveryFailure.InvalidBackup);
        var owned = bytes.ToArray();
        var snapshot = Read(owned);
        return new(snapshot.Manifest.SnapshotId, snapshot.Manifest.ProfileId,
            snapshot.Manifest.SettingsSchemaVersion, snapshot.Manifest.SourceRevision,
            Hash(owned), snapshot.Sha256);
    }

    internal static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    internal static byte[] Create(byte[] settingsBytes)
    {
        var settings = SettingsJson.Read(settingsBytes);
        var manifest = new SnapshotManifest
        {
            FormatVersion = 1, MinimumReaderFormat = 1, Application = "Martlet.Configuration",
            ProducerVersion = typeof(SettingsStore).Assembly.GetName().Version!.ToString(),
            SettingsSchemaVersion = settings.SchemaVersion, SnapshotId = Guid.NewGuid(),
            ProfileId = settings.Profile.Id, CreatedUtc = DateTimeOffset.UtcNow,
            SourceRevision = Hash(settingsBytes), SettingsBytes = settingsBytes
        };
        return ContractJson.Write(new SnapshotEnvelope
        {
            Manifest = manifest, Sha256 = Hash(ContractJson.Write(manifest))
        }, MaximumBytes);
    }

    internal static SnapshotEnvelope Read(byte[] bytes)
    {
        try
        {
            ContractJson.Read<SnapshotVersionHeader>(bytes, MaximumBytes);
            return ContractJson.Read<SnapshotEnvelope>(bytes, MaximumBytes);
        }
        catch (ContractException ex)
        {
            throw new RecoveryException(ex.Code == ErrorCode.UnsupportedVersion
                ? RecoveryFailure.Incompatible : RecoveryFailure.InvalidBackup);
        }
    }

    private sealed record SnapshotVersionHeader : IContract
    {
        public required ManifestHeader Manifest { get; init; }
        public void Validate()
        {
            ContractRules.Require(Manifest is not null, "Missing manifest.");
            ContractRules.Require(Manifest!.FormatVersion == 1 && Manifest.MinimumReaderFormat == 1 &&
                Manifest.Application == "Martlet.Configuration" &&
                Manifest.SettingsSchemaVersion is >= 1 and <= AppSettings.CurrentSchemaVersion,
                "Unsupported snapshot version.", ErrorCode.UnsupportedVersion);
        }
    }
    private sealed record ManifestHeader
    {
        public required int FormatVersion { get; init; }
        public required int MinimumReaderFormat { get; init; }
        public required string Application { get; init; }
        public required int SettingsSchemaVersion { get; init; }
    }
}

public sealed class ConfigurationSnapshotInspection
{
    public Guid SnapshotId { get; }
    public Guid ProfileId { get; }
    public int SettingsSchemaVersion { get; }
    public string SourceRevision { get; }
    public string FileDigest { get; }
    public string ManifestDigest { get; }

    internal ConfigurationSnapshotInspection(Guid snapshotId, Guid profileId, int schema,
        string sourceRevision, string fileDigest, string manifestDigest)
    {
        SnapshotId = snapshotId; ProfileId = profileId; SettingsSchemaVersion = schema;
        SourceRevision = sourceRevision; FileDigest = fileDigest; ManifestDigest = manifestDigest;
    }
}

// Only immutable text/scalars leave this boundary. Mutable settings arrays are never exposed.
public sealed class ConfigurationRestorePlan
{
    private readonly byte[] candidate;
    private int approved;
    internal Guid Id { get; } = Guid.NewGuid();
    internal string SourcePath { get; }
    internal string SourceFileDigest { get; }
    public string SnapshotDigest { get; }
    public string Destination { get; }
    public Guid ProfileId { get; }
    public string ExpectedRevision { get; }
    public string CandidateDigest { get; }
    public string CandidateJson => Encoding.UTF8.GetString(candidate);
    public string Summary { get; }

    internal ConfigurationRestorePlan(string source, byte[] sourceBytes, SnapshotEnvelope snapshot,
        string destination, string revision, AppSettings current, AppSettings restored)
    {
        SourcePath = source;
        SourceFileDigest = ConfigurationSnapshot.Hash(sourceBytes);
        SnapshotDigest = snapshot.Sha256;
        Destination = destination;
        ProfileId = current.Profile.Id;
        ExpectedRevision = revision;
        candidate = ContractJson.Write(restored, AppSettings.MaxFileBytes);
        CandidateDigest = ConfigurationSnapshot.Hash(candidate);
        var lines = new List<string>
        {
            $"Compatible Martlet configuration format 1; source settings v{snapshot.Manifest.SettingsSchemaVersion} -> current settings v{AppSettings.CurrentSchemaVersion}.",
            $"Snapshot: {snapshot.Manifest.SnapshotId}; created {snapshot.Manifest.CreatedUtc:O}; producer {snapshot.Manifest.ProducerVersion}.",
            $"Snapshot SHA-256: {SnapshotDigest}", $"Source file SHA-256: {SourceFileDigest}",
            $"Same profile only: {ProfileId}", $"Destination: {Destination}", $"Current revision: {ExpectedRevision}",
            $"Exact candidate SHA-256: {CandidateDigest}",
            $"Profile choice: {current.Profile.Kind} -> {restored.Profile.Kind}; setup checkpoint -> Destinations.",
            "ALL saved destination acknowledgments and audio checkpoints are invalidated. Capture and logging are NOT enabled.",
            "ALL imported credential IDs and imported cleanup markers remain historical only; no key is read, rebound or deleted.",
            "Version 3+ persona profiles and style weights are restored when present; older snapshots preserve the current companion profiles.",
            "Memory facts, store files and exports are NOT backed up or restored. Memory is forced OFF; a version 4 storage policy is retained for explicit review and re-enablement.",
            $"Current legacy references retained unchanged: {current.Profile.Credentials.Count}. Current owned cleanup references retained/queued: {restored.Setup!.PendingRemovals.Count}.",
            "Imported legacy references are NOT restored. Reconfigure keys and review destinations/devices explicitly.",
            "Support journal, secrets and voice/model databases are NOT restorable here. The separately owned memory fact store is excluded.",
            "An exact pre-replacement raw settings snapshot is kept under a new settings.recovery.*.bak name. Historical v1 snapshots remain untouched."
        };
        foreach (var role in Enum.GetValues<SetupRole>())
            lines.Add($"{role}: {Describe(current.Setup?.Routes.SingleOrDefault(r => r.Role == role))} -> " +
                $"{Describe(restored.Setup.Routes.SingleOrDefault(r => r.Role == role))}; key unbound; selection must be renewed.");
        lines.Add($"Input: {Describe(current.Audio?.Input)} -> {Describe(restored.Audio?.Input)}; unqualified.");
        lines.Add($"Output: {Describe(current.Audio?.Output)} -> {Describe(restored.Audio?.Output)}; unqualified.");
        Summary = string.Join(Environment.NewLine, lines);
    }

    private static string Describe(SetupRoute? route) => route is null ? "not configured" :
        $"{route.ProviderAlias} / {route.Origin} / {route.ModelId} / {route.VoiceId ?? "(no voice)"}";
    private static string Describe(AudioChoice? choice) => choice is null ? "not selected" :
        $"{choice.DisplayName} [{choice.EndpointId ?? "Windows default policy"}]";
    internal byte[] CandidateBytes() => candidate.ToArray();

    public ConfigurationRestoreApproval Approve(string snapshotDigest, string destination, string currentRevision)
    {
        if (snapshotDigest != SnapshotDigest || destination != Destination || currentRevision != ExpectedRevision ||
            Interlocked.Exchange(ref approved, 1) != 0)
            throw new RecoveryException(RecoveryFailure.Conflict);
        return new(this);
    }
}

public sealed class ConfigurationRestoreApproval
{
    private readonly ConfigurationRestorePlan plan;
    private int consumed;
    internal ConfigurationRestoreApproval(ConfigurationRestorePlan plan) => this.plan = plan;
    internal void Consume(ConfigurationRestorePlan expected)
    {
        if (Interlocked.Exchange(ref consumed, 1) != 0 || !ReferenceEquals(plan, expected))
            throw new RecoveryException(RecoveryFailure.Conflict);
    }
}

public sealed record ConfigurationRecoveryReceipt(string Path, string Revision, string? OriginalSnapshot = null);
