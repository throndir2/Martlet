using Martlet.Updates;

namespace Martlet.Launcher;

public sealed class LocalDesktopLauncher
{
    private readonly LocalActivationEngine activation;
    private readonly LauncherExecution execution;
    private readonly LauncherActivationReadinessProbe? authority;
    private long sequence;
    internal TimeProvider Clock { get; init; } = TimeProvider.System;

    internal Action<LauncherEvidencePoint, Guid, int?>? Io
    {
        get => execution.Io;
        init => execution.Io = value;
    }

    public LocalDesktopLauncher(LocalActivationEngine activation, string existingPrivateLauncherRoot,
        TimeSpan? readinessTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(activation);
        this.activation = activation;
        authority = activation.LauncherProbe as LauncherActivationReadinessProbe;
        execution = new(existingPrivateLauncherRoot, readinessTimeout);
    }

    public DesktopLaunchPlan PrepareLaunch(long expectedActivationRevision, CancellationToken token = default)
    {
        try
        {
            var next = Interlocked.Increment(ref sequence);
            return activation.WithVerifiedActive(expectedActivationRevision, (verified, reverify) =>
            {
                var target = LaunchTarget.From(verified);
                LauncherSupport.Validate(target);
                var scope = RequirePolicy(target, token);
                reverify();
                return new DesktopLaunchPlan(this, next, target, scope, Clock);
            }, token);
        }
        catch (ActivationException error) { throw Map(error); }
    }

    public Task<DesktopLaunchResult> LaunchActiveAsync(DesktopLaunchPlan plan,
        DesktopLaunchApproval approval, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(approval);
        approval.Consume(plan);
        return Task.Run(() =>
        {
            try
            {
                if (!ReferenceEquals(plan.Owner, this) || plan.Sequence != Volatile.Read(ref sequence) ||
                    plan.IsExpired)
                    throw new LauncherException(LauncherFailure.Conflict);
                return activation.WithVerifiedActive(plan.ActivationRevision, (verified, reverify) =>
                {
                    var target = LaunchTarget.From(verified);
                    if (target != plan.Target)
                        throw new LauncherException(LauncherFailure.Conflict);
                    void Verify()
                    {
                        RequireConsent();
                        if (RequirePolicy(target, token).Scope != plan.PolicyScope)
                            throw new LauncherException(LauncherFailure.Conflict);
                        reverify();
                        RequireConsent();
                    }
                    void RequireConsent()
                    {
                        token.ThrowIfCancellationRequested();
                        if (plan.IsExpired)
                            throw new LauncherException(LauncherFailure.Conflict);
                        plan.Authorization.CheckDeadline();
                    }
                    Verify();
                    var launch = execution.StartDesktop(target, token, Verify, RequireConsent);
                    // Do not transfer the process outside the verified state/settings/staging scope.
                    return execution.CompleteDesktopAsync(launch, token).GetAwaiter().GetResult();
                }, token);
            }
            catch (ActivationException error) { throw Map(error); }
        }, CancellationToken.None);
    }

    private PublisherAuthorization RequirePolicy(LaunchTarget target, CancellationToken token) =>
        authority?.Require(target, LauncherExecutionPurpose.DesktopLaunch, token) ??
        throw new LauncherException(LauncherFailure.PublisherUnconfigured);

    private static LauncherException Map(ActivationException error) => new(error.Failure switch
    {
        ActivationFailure.NoActiveVersion or ActivationFailure.NoSelectedVersion => LauncherFailure.NoActiveVersion,
        ActivationFailure.Conflict => LauncherFailure.Conflict,
        ActivationFailure.Busy => LauncherFailure.Busy,
        ActivationFailure.IncompatibleSettings => LauncherFailure.IncompatibleSettings,
        ActivationFailure.InvalidSelectedVersion => LauncherFailure.TamperedPayload,
        ActivationFailure.Cancelled => LauncherFailure.Cancelled,
        ActivationFailure.Unavailable => LauncherFailure.Unavailable,
        _ => LauncherFailure.InvalidActivation
    });
}

internal sealed class LauncherActivationReadinessProbe : ILauncherActivationProbe
{
    public const string ProbeId = "martlet.named-pipe-readiness.v1";
    private readonly LauncherExecution execution;
    private readonly Func<LauncherPublisherPolicy>? publisherPolicy;
    public string Id => ProbeId;
    public TimeSpan Timeout { get; }

    internal Action<LauncherEvidencePoint, Guid, int?>? Io
    {
        get => execution.Io;
        init => execution.Io = value;
    }

    internal LauncherActivationReadinessProbe(string existingPrivateLauncherRoot,
        TimeSpan? timeout, Func<LauncherPublisherPolicy>? publisherPolicy)
    {
        Timeout = LauncherSupport.ValidateTimeout(timeout);
        execution = new(existingPrivateLauncherRoot, Timeout);
        this.publisherPolicy = publisherPolicy;
    }

    internal PublisherAuthorization Require(LaunchTarget target, LauncherExecutionPurpose purpose, CancellationToken token)
    {
        LauncherPublisherPolicy? policy;
        token.ThrowIfCancellationRequested();
        try { policy = publisherPolicy?.Invoke(); }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw new OperationCanceledException(token);
        }
        catch (Exception error) when (error is System.Text.Json.JsonException or TimeoutException or
            InvalidOperationException or IOException or UnauthorizedAccessException or ArgumentException or
            System.Security.SecurityException or OperationCanceledException)
        {
            throw new LauncherException(LauncherFailure.PublisherUnavailable);
        }
        if (policy is null)
            throw new LauncherException(LauncherFailure.PublisherUnconfigured);
        return policy.Require(target, purpose);
    }

    public string Prepare(LauncherCandidate candidate, CancellationToken token)
    {
        var target = LaunchTarget.From(candidate);
        LauncherSupport.Validate(target);
        return Require(target, LauncherExecutionPurpose.ActivationReadiness, token).Scope;
    }

    public ILauncherActivationOwnership Open(LauncherCandidate candidate, string expectedScope,
        Guid transactionId, ActivationTransitionKind kind, DateTimeOffset expiresUtc, CancellationToken token)
    {
        var target = LaunchTarget.From(candidate);
        var authorization = Require(target, LauncherExecutionPurpose.ActivationReadiness, token);
        if (authorization.Scope != expectedScope)
            throw new LauncherException(LauncherFailure.Conflict);
        return new Ownership(this, target, authorization, transactionId, kind,
            expiresUtc, execution.Acquire(target), token);
    }

    private sealed class Ownership(LauncherActivationReadinessProbe owner, LaunchTarget target,
        PublisherAuthorization authorization, Guid transactionId, ActivationTransitionKind kind,
        DateTimeOffset deadline, LauncherEvidenceStore.LauncherLease lease,
        CancellationToken token) : ILauncherActivationOwnership
    {
        public ActivationProbeOutcome Probe(CancellationToken cancellation)
        {
            Verify();
            try
            {
                return owner.execution.Probe(target with
                    { ActivationDeadlineUtc = deadline, TransitionId = transactionId, TransitionKind = kind },
                    lease, cancellation, CheckDeadline)
                    ? ActivationProbeOutcome.Ready : ActivationProbeOutcome.NotReady;
            }
            catch (LauncherException error) when (
                error.Failure == LauncherFailure.Cancelled && cancellation.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellation);
            }
        }

        public void Verify()
        {
            token.ThrowIfCancellationRequested();
            if (owner.Require(target, LauncherExecutionPurpose.ActivationReadiness, token).Scope != authorization.Scope)
                throw new LauncherException(LauncherFailure.Conflict);
        }

        public void CheckDeadline() => authorization.CheckDeadline();
        public void Dispose() => lease.Dispose();
    }
}

public static class LauncherActivationComposition
{
    public static LocalActivationEngine Create(string existingPrivateActivationRoot,
        LocalSelectionEngine selection, string existingPrivateLauncherRoot,
        TimeSpan? readinessTimeout = null, Func<LauncherPublisherPolicy>? publisherPolicy = null) =>
        new(existingPrivateActivationRoot, selection,
            new LauncherActivationReadinessProbe(existingPrivateLauncherRoot, readinessTimeout, publisherPolicy));
}
