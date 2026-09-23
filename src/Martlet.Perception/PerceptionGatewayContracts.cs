namespace Martlet.Perception;

public enum PerceptionGatewayAuthentication
{
    ScopedDeviceCredential
}

public enum PerceptionGatewayRole
{
    Perception
}

public sealed record PerceptionGatewayBinding
{
    public required string DestinationId { get; init; }
    public required string HostId { get; init; }
    public required PerceptionGatewayRole Role { get; init; }
    public required PerceptionGatewayAuthentication Authentication { get; init; }
    public required bool AutomaticRedirectsAllowed { get; init; }

    internal void Validate()
    {
        PerceptionWorkerGuard.Identifier(DestinationId, 128);
        PerceptionWorkerGuard.Identifier(HostId);
        PerceptionWorkerGuard.Defined(Role);
        PerceptionWorkerGuard.Defined(Authentication);
        PerceptionWorkerGuard.Require(Role == PerceptionGatewayRole.Perception);
        PerceptionWorkerGuard.Require(Authentication ==
            PerceptionGatewayAuthentication.ScopedDeviceCredential);
        PerceptionWorkerGuard.Require(!AutomaticRedirectsAllowed,
            PerceptionWorkerFailure.RedirectRejected);
    }
}

public sealed record PerceptionGatewayResponse
{
    public required string DestinationId { get; init; }
    public required string HostId { get; init; }
    public required PerceptionGatewayRole AuthenticatedRole { get; init; }
    public required PerceptionWorkerResponse WorkerResponse { get; init; }
}

public enum PerceptionTransportFailure
{
    AuthenticationFailed,
    HostUnavailable,
    DeadlineExceeded,
    RedirectRejected,
    ProtocolViolation
}

public sealed class PerceptionTransportException : Exception
{
    public PerceptionTransportException(PerceptionTransportFailure failure)
        : base("The authenticated perception gateway transport failed.")
    {
        PerceptionWorkerGuard.Defined(failure);
        Failure = failure;
    }

    public PerceptionTransportFailure Failure { get; }
}

public interface IAuthenticatedPerceptionWorkerTransport
{
    PerceptionGatewayBinding Binding { get; }

    ValueTask<PerceptionGatewayResponse> ExecuteAsync(
        PerceptionWorkerRequest request,
        CancellationToken cancellationToken);

    ValueTask<PerceptionCancelResponse> CancelAsync(
        PerceptionCancelRequest request,
        CancellationToken cancellationToken);
}

public enum PerceptionJobOutcome
{
    Completed,
    NotScheduled,
    Canceled,
    DeadlineExceeded,
    Failed
}

public sealed record PerceptionJobResult
{
    public required PerceptionJobIds Ids { get; init; }
    public required Guid ActionId { get; init; }
    public required long Epoch { get; init; }
    public required PerceptionRole Role { get; init; }
    public required PerceptionJobOutcome Outcome { get; init; }
    public required PerceptionWorkerFailure? Failure { get; init; }
    public PerceptionObservation? Observation { get; init; }
    public PerceptionCancellationCapability? ComputeCancellation { get; init; }
    public required bool WorkerMayContinue { get; init; }
    public required bool OutputDiscarded { get; init; }
    public PerceptionWorkerError? WorkerError { get; init; }

    public PerceptionWorkerFailureDetails? FailureDetails =>
        Failure is { } failure ? PerceptionWorkerFailureCatalog.Get(failure) : null;

    public override string ToString() =>
        $"Perception job result {{ ActionId = {ActionId}, Role = {Role}, Outcome = {Outcome}, Failure = {Failure}, observation = omitted }}";
}

public interface IPerceptionJobExecutor
{
    PerceptionRole Role { get; }
    PerceptionWorkerIdentity Worker { get; }

    ValueTask<PerceptionJobResult> ExecuteAsync(
        PerceptionJobIntent request,
        PerceptionVisionAuthorization authorization,
        CancellationToken cancellationToken = default);
}
