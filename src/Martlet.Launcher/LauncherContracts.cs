using Martlet.Core.Settings;
using Martlet.Updates;

namespace Martlet.Launcher;

public enum LauncherFailure
{
    PlatformNotSupported,
    IncompatibleRid,
    IncompatibleVersion,
    IncompatibleSettings,
    NoActiveVersion,
    Conflict,
    Busy,
    InvalidActivation,
    TamperedPayload,
    EvidenceUnavailable,
    ProcessStartFailed,
    ProcessPathMismatch,
    ReadinessTimedOut,
    ReadinessRejected,
    ProcessExitedBeforeReadiness,
    ProcessCrashed,
    CleanupFailed,
    Cancelled,
    Unavailable,
    PublisherUnconfigured,
    PublisherRejected,
    PublisherUnavailable
}

public sealed class LauncherException : Exception
{
    public LauncherFailure Failure { get; }
    public Guid? AttemptId { get; }

    internal LauncherException(LauncherFailure failure, Guid? attemptId = null)
        : base(failure switch
        {
            LauncherFailure.PlatformNotSupported =>
                "This launcher supports only a native Windows x64 process.",
            LauncherFailure.PublisherUnconfigured =>
                "The trusted host has not provisioned an explicit publisher execution policy. Staging trust is not execution authority.",
            LauncherFailure.PublisherRejected =>
                "Current publisher execution policy does not authorize this exact release, source or purpose.",
            LauncherFailure.PublisherUnavailable =>
                "The trusted host could not read current publisher execution policy. No policy fallback was used.",
            LauncherFailure.IncompatibleRid =>
                "The verified active payload targets an unsupported runtime identifier.",
            LauncherFailure.IncompatibleVersion =>
                "The verified active payload is outside this launcher's supported application-version range.",
            LauncherFailure.IncompatibleSettings =>
                "The verified active payload cannot read this launcher's supported current settings schema.",
            LauncherFailure.NoActiveVersion =>
                "No verified active version is available for launch.",
            LauncherFailure.Conflict =>
                "The approved activation revision or retained launch evidence changed. Inspect and request one fresh launch.",
            LauncherFailure.Busy =>
                "This profile already has a cooperating launcher or readiness process. Await its actual completion.",
            LauncherFailure.InvalidActivation =>
                "Activation evidence is incomplete, corrupt, or no longer authorized. Preserve it for explicit reconciliation.",
            LauncherFailure.TamperedPayload =>
                "The exact selected payload no longer matches its retained signed evidence. Nothing was launched.",
            LauncherFailure.EvidenceUnavailable =>
                "Private launcher evidence cannot be recorded safely. Nothing new was launched.",
            LauncherFailure.ProcessStartFailed =>
                "The exact verified Desktop apphost could not be created under launcher ownership.",
            LauncherFailure.ProcessPathMismatch =>
                "The created process image did not resolve to the exact verified Desktop apphost.",
            LauncherFailure.ReadinessTimedOut =>
                "The owned Desktop process did not complete the private readiness handshake before its fixed deadline.",
            LauncherFailure.ReadinessRejected =>
                "The private readiness handshake did not match the exact version, profile, settings, payload, process, or deadline.",
            LauncherFailure.ProcessExitedBeforeReadiness =>
                "The owned Desktop process exited before reporting initialized readiness.",
            LauncherFailure.ProcessCrashed =>
                "The owned Desktop process exited unsuccessfully after launch.",
            LauncherFailure.CleanupFailed =>
                "The owned process tree did not confirm termination within the fixed cleanup deadline.",
            LauncherFailure.Cancelled =>
                "Launch was cancelled and the owned process tree was stopped.",
            _ => "The exact verified Desktop launch is unavailable."
        })
    {
        Failure = failure;
        AttemptId = attemptId;
    }
}

public enum DesktopLaunchOutcome
{
    Exited
}

public sealed class DesktopLaunchResult
{
    public Guid AttemptId { get; }
    public Guid ProfileId { get; }
    public string Version { get; }
    public int ProcessId { get; }
    public int ExitCode { get; }
    public DesktopLaunchOutcome Outcome { get; }

    internal DesktopLaunchResult(Guid attemptId, Guid profileId, string version,
        int processId, int exitCode)
    {
        AttemptId = attemptId;
        ProfileId = profileId;
        Version = version;
        ProcessId = processId;
        ExitCode = exitCode;
        Outcome = DesktopLaunchOutcome.Exited;
    }
}

internal static class LauncherSupport
{
    internal const string Rid = "win-x64";
    internal const string ExecutableRelativePath = "Desktop/Martlet.Desktop.exe";
    internal static readonly Version MinimumVersion = new(0, 1, 0, 0);
    internal static readonly Version MaximumVersionExclusive = new(1, 0, 0, 0);
    internal const int MinimumSettingsSchema = 1;
    internal static int MaximumSettingsSchema => AppSettings.CurrentSchemaVersion;
    internal static readonly TimeSpan DefaultReadinessTimeout = TimeSpan.FromSeconds(20);
    internal static readonly TimeSpan MaximumReadinessTimeout = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan ProcessCleanupTimeout = TimeSpan.FromSeconds(5);

    internal static void Validate(LaunchTarget target)
    {
        if (!OperatingSystem.IsWindows() ||
            System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture !=
            System.Runtime.InteropServices.Architecture.X64)
            throw new LauncherException(LauncherFailure.PlatformNotSupported);
        if (target.Rid != Rid)
            throw new LauncherException(LauncherFailure.IncompatibleRid);
        if (!Version.TryParse(target.Version, out var version) ||
            version.Revision < 0 || target.Version != version.ToString(4) ||
            version < MinimumVersion || version >= MaximumVersionExclusive)
            throw new LauncherException(LauncherFailure.IncompatibleVersion);
        if (target.SettingsSchemaVersion < MinimumSettingsSchema ||
            target.SettingsSchemaVersion > MaximumSettingsSchema ||
            target.SettingsSchemaVersion < target.SettingsMinimumReader ||
            target.SettingsSchemaVersion > target.SettingsMaximumReader)
            throw new LauncherException(LauncherFailure.IncompatibleSettings);
        if (target.ExecutableRelativePath != ExecutableRelativePath ||
            target.ExecutableBytes <= 0 ||
            !IsHash(target.PayloadSha256) ||
            !IsHash(target.ManifestSha256) ||
            !IsHash(target.ExecutableSha256) ||
            !Path.IsPathFullyQualified(target.LeaseDirectory) ||
            !Path.IsPathFullyQualified(target.StageDirectory) ||
            !Path.IsPathFullyQualified(target.PayloadDirectory) ||
            !Path.IsPathFullyQualified(target.ExecutablePath) ||
            !Within(target.PayloadDirectory, target.ExecutablePath))
            throw new LauncherException(LauncherFailure.InvalidActivation);
    }

    internal static TimeSpan ValidateTimeout(TimeSpan? value)
    {
        var timeout = value ?? DefaultReadinessTimeout;
        if (timeout < TimeSpan.FromMilliseconds(100) ||
            timeout > MaximumReadinessTimeout)
            throw new ArgumentOutOfRangeException(nameof(value));
        return timeout;
    }

    internal static bool Within(string parent, string child)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(parent), Path.GetFullPath(child));
        return relative != "." &&
            !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
            relative != ".." &&
            !Path.IsPathFullyQualified(relative);
    }

    private static bool IsHash(string? value) =>
        value is { Length: 64 } &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}

internal sealed record LaunchTarget(
    long? ActivationRevision,
    Guid TransitionId,
    ActivationTransitionKind? TransitionKind,
    Guid ProfileId,
    string LeaseDirectory,
    string StageDirectory,
    string PayloadDirectory,
    string ExecutablePath,
    string ExecutableRelativePath,
    string Version,
    string Rid,
    string PayloadSha256,
    string ManifestSha256,
    string ExecutableSha256,
    long ExecutableBytes,
    long SelectionRevision,
    string SettingsRevision,
    int SettingsSchemaVersion,
    int SettingsMinimumReader,
    int SettingsMaximumReader,
    DateTimeOffset? ActivationDeadlineUtc,
    string SignerId = "",
    string SourceCommit = "",
    bool SourceDirty = false)
{
    internal static LaunchTarget From(VerifiedActivationTarget target) =>
        From(target.Candidate, target.SettingsSchemaVersion) with
        {
            ActivationRevision = target.ActivationRevision, TransitionId = target.TransitionId
        };

    internal static LaunchTarget From(LauncherCandidate candidate, int? schema = null)
    {
        var target = candidate.Target;
        var payload = Path.Combine(candidate.Stage.Receipt.Destination, "payload");
        var metadata = candidate.Stage.Candidate.Manifest.Files.Single(file => file.Path == "manifest.json");
        using var stream = BoundedIo.OpenRead(Path.Combine(payload, "manifest.json"));
        var bytes = BoundedIo.Read(stream, PayloadMetadata.MaximumV2Bytes, CancellationToken.None);
        if (bytes.LongLength != metadata.Bytes || Wire.Hash(bytes) != metadata.Sha256)
            throw new LauncherException(LauncherFailure.TamperedPayload);
        using var document = Wire.ReadDocument(bytes, PayloadMetadata.MaximumV2Bytes, CancellationToken.None);
        var root = document.RootElement;
        return new(null, Guid.Empty, null, candidate.ProfileId, candidate.LeaseDirectory,
            candidate.Stage.Receipt.Destination, payload,
            Path.Combine(payload, target.ExecutableRelativePath.Replace('/', Path.DirectorySeparatorChar)),
            target.ExecutableRelativePath, target.Version, candidate.Stage.Receipt.Rid,
            target.ArchiveSha256, target.ManifestSha256, target.ExecutableSha256, target.ExecutableBytes,
            target.SelectionRevision, candidate.SettingsRevision, schema ?? target.SettingsSchemaVersion,
            target.SettingsMinimumReader, target.SettingsMaximumReader, null, target.SignerId,
            root.GetProperty("sourceCommit").GetString()!, root.GetProperty("sourceDirty").GetBoolean());
    }
}
