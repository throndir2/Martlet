namespace Martlet.Perception;

public readonly record struct PerceptionProtocolVersion(int Major, int Minor)
{
    public static PerceptionProtocolVersion Current { get; } = new(1, 0);

    internal void Validate() =>
        PerceptionWorkerGuard.Require(Major == Current.Major &&
            Minor >= 0 && Minor <= Current.Minor,
            PerceptionWorkerFailure.UnsupportedVersion);

    public override string ToString() => $"{Major}.{Minor}";
}

public enum PerceptionRole
{
    Ocr,
    VisualQuestionAnswering
}

public enum PerceptionEvidenceKind
{
    LiveWorker,
    SyntheticFixture
}

public enum PerceptionCancellationCapability
{
    DiscardOnly,
    RequestAbort,
    CooperativeComputeCancel
}

public enum PerceptionArtifactRole
{
    RuntimeImage,
    ModelWeights,
    ModelConfiguration,
    Tokenizer,
    Projector
}

public sealed record PerceptionArtifactIdentity
{
    public required PerceptionArtifactRole Role { get; init; }
    public required string ArtifactId { get; init; }
    public required string Revision { get; init; }
    public required string Sha256 { get; init; }
    public required long Bytes { get; init; }
    public required string LicenseId { get; init; }

    internal void Validate()
    {
        PerceptionWorkerGuard.Defined(Role);
        PerceptionWorkerGuard.Identifier(ArtifactId);
        PerceptionWorkerGuard.Revision(Revision);
        PerceptionWorkerGuard.Sha256(Sha256);
        PerceptionWorkerGuard.Require(Bytes is > 0 and <= 16L * 1024 * 1024 * 1024 * 1024,
            PerceptionWorkerFailure.LimitExceeded);
        PerceptionWorkerGuard.Identifier(LicenseId);
    }
}

public sealed record PerceptionRuntimeIdentity
{
    public required string WorkerBuildId { get; init; }
    public required string WorkerBuildRevision { get; init; }
    public required string RuntimeId { get; init; }
    public required string RuntimeVersion { get; init; }
    public required string RuntimeRevision { get; init; }
    public required string OperatingSystemId { get; init; }
    public required string OperatingSystemVersion { get; init; }

    internal void Validate()
    {
        PerceptionWorkerGuard.Identifier(WorkerBuildId);
        PerceptionWorkerGuard.Revision(WorkerBuildRevision);
        PerceptionWorkerGuard.Identifier(RuntimeId);
        PerceptionWorkerGuard.ExactVersion(RuntimeVersion);
        PerceptionWorkerGuard.Revision(RuntimeRevision);
        PerceptionWorkerGuard.Identifier(OperatingSystemId);
        PerceptionWorkerGuard.ExactVersion(OperatingSystemVersion);
    }
}

public sealed record PerceptionModelIdentity
{
    public required PerceptionRole Role { get; init; }
    public required string AdapterId { get; init; }
    public required string AdapterVersion { get; init; }
    public required string ModelId { get; init; }
    public required string ModelRevision { get; init; }
    public required string ModelSha256 { get; init; }
    public required string LicenseId { get; init; }

    internal void Validate()
    {
        PerceptionWorkerGuard.Defined(Role);
        PerceptionWorkerGuard.Identifier(AdapterId);
        PerceptionWorkerGuard.ExactVersion(AdapterVersion);
        PerceptionWorkerGuard.Identifier(ModelId, 128);
        PerceptionWorkerGuard.Revision(ModelRevision);
        PerceptionWorkerGuard.Sha256(ModelSha256);
        PerceptionWorkerGuard.Identifier(LicenseId);
    }
}

public sealed record PerceptionResourceRequirements
{
    public required int CpuUnits { get; init; }
    public required int GpuMemoryMiB { get; init; }

    internal void Validate()
    {
        PerceptionWorkerGuard.Require(CpuUnits is >= 1 and <= 64,
            PerceptionWorkerFailure.ResourceBudgetExceeded);
        PerceptionWorkerGuard.Require(GpuMemoryMiB is >= 0 and <= 1_048_576,
            PerceptionWorkerFailure.ResourceBudgetExceeded);
    }
}

public sealed record PerceptionWorkerLimits
{
    public required int MaximumInputBytes { get; init; }
    public required int MaximumWidth { get; init; }
    public required int MaximumHeight { get; init; }
    public required long MaximumPixels { get; init; }
    public required int MaximumOutputUtf8Bytes { get; init; }
    public required int MaximumConcurrency { get; init; }

    internal void Validate()
    {
        PerceptionWorkerGuard.Require(MaximumInputBytes is > 0 and <= PerceptionProtocol.MaximumImageBytes);
        PerceptionWorkerGuard.Require(MaximumWidth is > 0 and <= PerceptionProtocol.MaximumImageWidth);
        PerceptionWorkerGuard.Require(MaximumHeight is > 0 and <= PerceptionProtocol.MaximumImageHeight);
        PerceptionWorkerGuard.Require(MaximumPixels is > 0 and <= PerceptionProtocol.MaximumImagePixels);
        PerceptionWorkerGuard.Require(MaximumOutputUtf8Bytes is > 0 and <=
            PerceptionProtocol.MaximumOutputUtf8Bytes);
        PerceptionWorkerGuard.Require(MaximumConcurrency is >= 1 and <= 8);
    }
}

public sealed class PerceptionWorkerIdentity
{
    private readonly PerceptionArtifactIdentity[] artifacts;

    public PerceptionWorkerIdentity(
        string workerId,
        PerceptionEvidenceKind evidence,
        PerceptionRole role,
        PerceptionRuntimeIdentity runtime,
        PerceptionModelIdentity model,
        IEnumerable<PerceptionArtifactIdentity> artifacts,
        PerceptionWorkerLimits limits,
        PerceptionResourceRequirements resources,
        PerceptionCancellationCapability cancellation)
    {
        PerceptionWorkerGuard.Identifier(workerId);
        PerceptionWorkerGuard.Defined(evidence);
        PerceptionWorkerGuard.Defined(role);
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(artifacts);
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(resources);
        PerceptionWorkerGuard.Defined(cancellation);
        runtime.Validate();
        model.Validate();
        PerceptionWorkerGuard.Require(model.Role == role, PerceptionWorkerFailure.RoleMismatch);
        limits.Validate();
        resources.Validate();

        var materialized = artifacts.Take(17).ToArray();
        PerceptionWorkerGuard.Require(materialized.Length is >= 3 and <= 16);
        foreach (var artifact in materialized)
        {
            PerceptionWorkerGuard.Require(artifact is not null);
            ArgumentNullException.ThrowIfNull(artifact);
            artifact.Validate();
        }
        PerceptionWorkerGuard.Require(materialized.Select(artifact => artifact.ArtifactId)
            .Distinct(StringComparer.Ordinal).Count() == materialized.Length);
        PerceptionWorkerGuard.Require(materialized.Count(artifact =>
            artifact.Role == PerceptionArtifactRole.RuntimeImage) == 1);
        PerceptionWorkerGuard.Require(materialized.Any(artifact =>
            artifact.Role == PerceptionArtifactRole.ModelWeights));
        PerceptionWorkerGuard.Require(materialized.Count(artifact =>
            artifact.Role == PerceptionArtifactRole.ModelConfiguration) == 1);
        if (role == PerceptionRole.VisualQuestionAnswering)
        {
            PerceptionWorkerGuard.Require(materialized.Any(artifact =>
                artifact.Role == PerceptionArtifactRole.Tokenizer));
            PerceptionWorkerGuard.Require(materialized.Any(artifact =>
                artifact.Role == PerceptionArtifactRole.Projector));
        }

        WorkerId = workerId;
        Evidence = evidence;
        Role = role;
        Runtime = runtime;
        Model = model;
        this.artifacts = materialized
            .OrderBy(artifact => artifact.Role)
            .ThenBy(artifact => artifact.ArtifactId, StringComparer.Ordinal)
            .ToArray();
        Artifacts = Array.AsReadOnly(this.artifacts);
        Limits = limits;
        Resources = resources;
        Cancellation = cancellation;
    }

    public PerceptionProtocolVersion ProtocolVersion => PerceptionProtocolVersion.Current;
    public string ContractId => PerceptionProtocol.ContractId;
    public string WorkerId { get; }
    public PerceptionEvidenceKind Evidence { get; }
    public PerceptionRole Role { get; }
    public PerceptionRuntimeIdentity Runtime { get; }
    public PerceptionModelIdentity Model { get; }
    public IReadOnlyList<PerceptionArtifactIdentity> Artifacts { get; }
    public PerceptionWorkerLimits Limits { get; }
    public PerceptionResourceRequirements Resources { get; }
    public PerceptionCancellationCapability Cancellation { get; }

    public bool Matches(PerceptionWorkerIdentity? other)
    {
        if (other is null ||
            WorkerId != other.WorkerId ||
            Evidence != other.Evidence ||
            Role != other.Role ||
            Runtime != other.Runtime ||
            Model != other.Model ||
            Limits != other.Limits ||
            Resources != other.Resources ||
            Cancellation != other.Cancellation ||
            artifacts.Length != other.artifacts.Length)
            return false;

        return artifacts.Zip(other.artifacts)
            .All(pair => pair.First == pair.Second);
    }

    public override string ToString() =>
        $"{nameof(PerceptionWorkerIdentity)} {{ WorkerId = {WorkerId}, Role = {Role}, Evidence = {Evidence}, model content = omitted }}";
}
