namespace Martlet.Updates;

internal interface ILauncherActivationProbe
{
    string Id { get; }
    TimeSpan Timeout { get; }
    string Prepare(LauncherCandidate candidate, CancellationToken token);
    ILauncherActivationOwnership Open(LauncherCandidate candidate, string expectedScope,
        Guid transactionId, ActivationTransitionKind kind, DateTimeOffset expiresUtc, CancellationToken token);
}

internal interface ILauncherActivationOwnership : IDisposable
{
    ActivationProbeOutcome Probe(CancellationToken token);
    void Verify();
    void CheckDeadline();
}

// Constructed only while the selection, signed stage and current settings are pinned.
internal sealed class LauncherCandidate
{
    internal Guid ProfileId { get; }
    internal string LeaseDirectory { get; }
    internal LocalStagingEngine.PinnedStage Stage { get; }
    internal ActivationTargetBinding Target { get; }
    internal string SettingsRevision { get; }

    internal LauncherCandidate(Guid profileId, string leaseDirectory,
        LocalStagingEngine.PinnedStage stage, ActivationTargetBinding target, string settingsRevision)
    {
        ProfileId = profileId;
        LeaseDirectory = leaseDirectory;
        Stage = stage;
        Target = target;
        SettingsRevision = settingsRevision;
    }
}

internal sealed record VerifiedActivationTarget(
    long ActivationRevision, Guid TransitionId, LauncherCandidate Candidate,
    int SettingsSchemaVersion);
