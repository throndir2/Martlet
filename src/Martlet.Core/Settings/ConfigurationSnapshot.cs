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
        RecoveryFailure.InvalidBackup => "This is not a valid Martlet configuration snapshot. Choose another file.",
        RecoveryFailure.Incompatible => "This snapshot needs a different Martlet version. Update Martlet or choose another snapshot.",
        RecoveryFailure.WrongProfile => "This snapshot belongs to another Martlet profile. Reconfigure this profile instead.",
        RecoveryFailure.Conflict => "The restore preview is out of date. Preview the snapshot again.",
        RecoveryFailure.CleanupPending => "The restore finished, but cleanup needs attention. Check access and free space, then try cleanup again.",
        RecoveryFailure.CleanupCapacity => "Too many old keys are waiting for removal. Remove some under Keys from before on Companion › Thinking, Voice or Listening, then preview again.",
        _ => "Martlet could not access recovery storage. Check free space, permissions and the destination."
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
    public const string Scope = "Configuration snapshots are local files for restoring Martlet settings. " +
        "They are not encrypted, so keep them private. They do not include secrets, conversations, audio, memory data or logs. No upload is performed.";

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
            $"Snapshot created {snapshot.Manifest.CreatedUtc:g}.",
            $"Settings will be updated from version {snapshot.Manifest.SettingsSchemaVersion} to {AppSettings.CurrentSchemaVersion}.",
            $"Profile choice: {current.Profile.Kind} -> {restored.Profile.Kind}.",
            "Saved provider approvals and audio checks will be cleared.",
            "Secrets are not included. Re-enter keys and reconnect hosts if needed.",
            "Memory facts, conversations, audio, logs and support data are not restored.",
            "Self-host routes are turned off until you review them again.",
            "Martlet will keep a backup of the current settings before replacing them."
        };
        foreach (var role in Enum.GetValues<SetupRole>())
            lines.Add($"{RoleName(role)}: {Describe(current.Setup?.Routes.SingleOrDefault(r => r.Role == role))} -> " +
                $"{Describe(restored.Setup!.Routes.SingleOrDefault(r => r.Role == role))}.");
        lines.Add($"Microphone: {Describe(current.Audio?.Input)} -> {Describe(restored.Audio?.Input)}.");
        lines.Add($"Speakers: {Describe(current.Audio?.Output)} -> {Describe(restored.Audio?.Output)}.");
        Summary = string.Join(Environment.NewLine, lines);
    }

    private static string Describe(SetupRoute? route) => route is null ? "not configured" :
        route.RouteType switch
        {
            null or SetupRouteType.OpenAi => "OpenAI",
            SetupRouteType.ChatCompletions => "custom chat endpoint",
            SetupRouteType.LocalWindowsStt => "Windows speech recognition",
            SetupRouteType.LocalWhisper or SetupRouteType.LocalParakeet => "local speech recognition",
            SetupRouteType.GatewayOllama => "paired-host model",
            SetupRouteType.GatewayF5 => "paired-host voice",
            SetupRouteType.GatewayStt => "paired-host speech recognition",
            SetupRouteType.ElevenLabs => "ElevenLabs voice",
            _ => "selected route"
        };
    private static string Describe(AudioChoice? choice) => choice is null ? "not selected" :
        choice.DisplayName;
    private static string RoleName(SetupRole role) => role switch
    {
        SetupRole.Stt => "Listening",
        SetupRole.Llm => "Thinking",
        _ => "Voice"
    };
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
