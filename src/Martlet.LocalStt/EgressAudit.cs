namespace Martlet.LocalStt;

public enum LocalSttEgressBeginStatus
{
    Ready,
    Unavailable,
    Canceled
}

public sealed record LocalSttEgressAuditRequest(
    Guid OperationId,
    string PackageId,
    string ManifestSha256,
    string ExecutableArchiveSha256,
    string ModelSha256,
    LocalSttNetworkPolicy Policy);

public sealed record LocalSttEgressBeginResult(
    LocalSttEgressBeginStatus Status,
    ILocalSttEgressAuditSession? Session = null);

public sealed record LocalSttEgressAuditReport(
    Guid OperationId,
    int ProcessId,
    LocalSttNetworkPolicy Policy,
    bool DenialEstablishedBeforeLaunch,
    bool BoundToProcessTree,
    bool ObservedUntilTreeExit,
    bool SystemPolicyMutated,
    bool RawEndpointDataRetained,
    int LoopbackAttempts,
    int NonLoopbackAttempts);

public sealed record LocalSttPrivacyEvidence(
    LocalSttNetworkPolicy Policy,
    bool DenialEstablishedBeforeLaunch,
    bool BoundToProcessTree,
    bool ObservedUntilTreeExit,
    int LoopbackAttempts,
    int NonLoopbackAttempts);

public interface ILocalSttEgressAuditor
{
    ValueTask<LocalSttEgressBeginResult> BeginAsync(
        LocalSttEgressAuditRequest request,
        CancellationToken cancellationToken);
}

public interface ILocalSttEgressAuditSession : IAsyncDisposable
{
    bool DenialEstablishedBeforeLaunch { get; }
    ValueTask<bool> BindProcessTreeAsync(int processId, CancellationToken cancellationToken);
    ValueTask<LocalSttEgressAuditReport> CompleteAsync(CancellationToken cancellationToken);
}

internal static class EgressAuditPolicy
{
    internal static bool TryAccept(
        LocalSttEgressAuditReport report,
        Guid operationId,
        int processId,
        LocalSttNetworkPolicy policy,
        out LocalSttPrivacyEvidence? evidence)
    {
        evidence = null;
        if (report.OperationId != operationId ||
            report.ProcessId != processId ||
            report.Policy != policy ||
            !report.DenialEstablishedBeforeLaunch ||
            !report.BoundToProcessTree ||
            !report.ObservedUntilTreeExit ||
            report.SystemPolicyMutated ||
            report.RawEndpointDataRetained ||
            report.LoopbackAttempts < 0 ||
            report.NonLoopbackAttempts < 0 ||
            report.NonLoopbackAttempts != 0 ||
            policy == LocalSttNetworkPolicy.NoNetwork && report.LoopbackAttempts != 0)
            return false;
        evidence = new(
            policy,
            report.DenialEstablishedBeforeLaunch,
            report.BoundToProcessTree,
            report.ObservedUntilTreeExit,
            report.LoopbackAttempts,
            report.NonLoopbackAttempts);
        return true;
    }
}
