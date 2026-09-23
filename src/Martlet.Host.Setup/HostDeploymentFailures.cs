namespace Martlet.Host.Setup;

public enum HostDeploymentFailure
{
    InvalidDefinition, ContentChanged, ReviewChanged, ConsentRequired, ConsentConsumed,
    PreviewChanged, PreviewExpired, StorageConflict, StorageFailure, JournalInvalid, Canceled
}

public sealed class HostDeploymentException(HostDeploymentFailure failure, Exception? inner = null)
    : Exception("Host configuration publication failed; preserve existing state and review the reported boundary.", inner)
{
    public HostDeploymentFailure Failure { get; } = failure;
    public string Remedy => HostDeploymentRemedies.For(Failure);
}

public static class HostDeploymentRemedies
{
    public static string For(HostDeploymentFailure failure) => failure switch
    {
        HostDeploymentFailure.InvalidDefinition => "Review the exact selected host, managed roles, owner and artifact target; do not adopt another service.",
        HostDeploymentFailure.ContentChanged => "Reinspect the selected acquisition-owned layout; preserve changed or foreign bytes and use the acquisition owner's recovery flow.",
        HostDeploymentFailure.ReviewChanged => "Refresh the current LocalReview record and obtain a new configuration preview.",
        HostDeploymentFailure.ConsentRequired or HostDeploymentFailure.ConsentConsumed =>
            "Review this exact local-file operation and provide a fresh one-use configuration publication decision.",
        HostDeploymentFailure.PreviewChanged or HostDeploymentFailure.PreviewExpired =>
            "Refresh the preview; previous decisions cannot authorize changed or stale files.",
        HostDeploymentFailure.StorageConflict or HostDeploymentFailure.JournalInvalid =>
            "Preserve existing files and resolve the specific ownership, pending-write or journal conflict locally; do not overwrite or adopt it.",
        HostDeploymentFailure.StorageFailure => "Check the selected private directory and local durability boundary, then preview again without deleting retained progress.",
        HostDeploymentFailure.Canceled => "Publication was interrupted; inspect committed progress and review any resume separately.",
        _ => throw new ArgumentOutOfRangeException(nameof(failure))
    };
}

internal static class DeploymentRules
{
    internal static void Require(bool condition, HostDeploymentFailure failure)
    {
        if (!condition) throw new HostDeploymentException(failure);
    }
}
