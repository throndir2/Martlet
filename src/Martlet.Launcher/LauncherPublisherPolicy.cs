using Martlet.Readiness;
using Martlet.Updates;

namespace Martlet.Launcher;

public enum LauncherExecutionPurpose { ActivationReadiness, DesktopLaunch }

public sealed record PublisherExecutionGrant(
    string SignerId, string Version, string ArchiveSha256, string ManifestSha256,
    string ExecutableSha256, string SourceCommit, bool SourceDirty,
    LauncherExecutionPurpose Purpose);

/// <summary>
/// Explicit execution grants provisioned by a trusted host independently of package data.
/// This library verifies exact scope, not the provenance of the host's policy.
/// </summary>
public sealed class LauncherPublisherPolicy
{
    private readonly PublisherExecutionGrant[] grants;
    public string Revision { get; }
    public DateTimeOffset ExpiresUtc { get; }
    internal string Digest { get; }

    public LauncherPublisherPolicy(string revision, DateTimeOffset expiresUtc,
        IEnumerable<PublisherExecutionGrant> approvedGrants)
    {
        ArgumentNullException.ThrowIfNull(approvedGrants);
        grants = approvedGrants.Take(33).ToArray();
        if (revision is not { Length: > 0 and <= 128 } || revision.Any(char.IsControl) ||
            grants.Length > 32 || grants.Distinct().Count() != grants.Length ||
            grants.Any(grant => grant is null || !Wire.IsHash(grant.SignerId) ||
                !Wire.IsHash(grant.ArchiveSha256) || !Wire.IsHash(grant.ManifestSha256) ||
                !Wire.IsHash(grant.ExecutableSha256) || !Wire.IsHex(grant.SourceCommit, 40) ||
                !ReadinessProtocol.ValidVersion(grant.Version) || !Enum.IsDefined(grant.Purpose)))
            throw new ArgumentException("Supply a bounded explicit publisher execution policy.");
        Revision = revision;
        ExpiresUtc = expiresUtc;
        Digest = Wire.Hash(Wire.Write(new { revision, expiresUtc, grants }));
    }

    internal PublisherAuthorization Require(LaunchTarget target, LauncherExecutionPurpose purpose)
    {
        if (grants.Length == 0)
            throw new LauncherException(LauncherFailure.PublisherUnconfigured);
        if (DateTimeOffset.UtcNow >= ExpiresUtc ||
            !grants.Contains(new PublisherExecutionGrant(target.SignerId, target.Version,
                target.PayloadSha256, target.ManifestSha256, target.ExecutableSha256,
                target.SourceCommit, target.SourceDirty, purpose)))
            throw new LauncherException(LauncherFailure.PublisherRejected);
        return new(Wire.Hash(Wire.Write(new { Digest, target, purpose })), ExpiresUtc);
    }
}

internal sealed record PublisherAuthorization(string Scope, DateTimeOffset ExpiresUtc)
{
    internal void CheckDeadline()
    {
        if (DateTimeOffset.UtcNow >= ExpiresUtc)
            throw new LauncherException(LauncherFailure.PublisherRejected);
    }
}

public sealed class DesktopLaunchPlan
{
    internal object Owner { get; }
    internal LaunchTarget Target { get; }
    internal string PolicyScope { get; }
    internal PublisherAuthorization Authorization { get; }
    internal long Sequence { get; }
    private int approved;
    private readonly TimeProvider clock;
    private readonly long started;
    internal bool IsExpired => clock.GetUtcNow() >= ExpiresUtc ||
        clock.GetElapsedTime(started) >= TimeSpan.FromMinutes(2);
    public Guid OperationId { get; } = Guid.NewGuid();
    public long ActivationRevision => Target.ActivationRevision!.Value;
    public Guid ProfileId => Target.ProfileId;
    public string Version => Target.Version;
    public string Rid => Target.Rid;
    public string ArchiveSha256 => Target.PayloadSha256;
    public string ManifestSha256 => Target.ManifestSha256;
    public string SettingsRevision => Target.SettingsRevision;
    public int SettingsSchemaVersion => Target.SettingsSchemaVersion;
    public string ExecutableSha256 => Target.ExecutableSha256;
    public string SignerId => Target.SignerId;
    public string SourceCommit => Target.SourceCommit;
    public bool SourceDirty => Target.SourceDirty;
    public string PublisherScopeDigest => PolicyScope;
    public DateTimeOffset PublisherExpiresUtc => Authorization.ExpiresUtc;
    public DateTimeOffset ExpiresUtc { get; }
    public string PlanDigest { get; }
    public string PlannedEffects =>
        "Start only this exact authorized image under an owned Windows Job, require fresh private readiness, " +
        "and retain verification ownership until the entire owned process tree ends. No automatic fallback or settings restore.";

    internal DesktopLaunchPlan(object owner, long sequence, LaunchTarget target,
        PublisherAuthorization authorization, TimeProvider clock)
    {
        Owner = owner;
        Sequence = sequence;
        Target = target;
        Authorization = authorization;
        PolicyScope = authorization.Scope;
        this.clock = clock;
        started = clock.GetTimestamp();
        ExpiresUtc = clock.GetUtcNow().AddMinutes(2);
        PlanDigest = Wire.Hash(Wire.Write(new { OperationId, target, authorization, ExpiresUtc, PlannedEffects }));
    }

    public DesktopLaunchApproval Approve(Guid operationId, string planDigest)
    {
        if (operationId != OperationId || planDigest != PlanDigest ||
            IsExpired || Interlocked.Exchange(ref approved, 1) != 0)
            throw new LauncherException(LauncherFailure.Conflict);
        return new(this);
    }
}

public sealed class DesktopLaunchApproval
{
    private readonly DesktopLaunchPlan plan;
    private int consumed;
    internal DesktopLaunchApproval(DesktopLaunchPlan plan) => this.plan = plan;
    internal void Consume(DesktopLaunchPlan expected)
    {
        if (!ReferenceEquals(plan, expected) || Interlocked.Exchange(ref consumed, 1) != 0)
            throw new LauncherException(LauncherFailure.Conflict);
    }
}
