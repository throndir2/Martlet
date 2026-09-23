namespace Martlet.F5;

public readonly record struct F5ProtocolVersion(int Major, int Minor)
{
    public static F5ProtocolVersion Current { get; } = new(1, 0);

    internal void Validate() =>
        F5Guard.Require(Major == Current.Major && Minor >= 0 && Minor <= Current.Minor,
            F5Failure.UnsupportedVersion);

    public override string ToString() => $"{Major}.{Minor}";
}

public enum F5EvidenceKind
{
    LiveWorker,
    SyntheticFixture
}

public enum F5CancellationCapability
{
    DiscardOnly,
    RequestAbort,
    CooperativeComputeCancel
}

public enum F5ArtifactRole
{
    RuntimeImage,
    ModelWeights,
    Vocabulary,
    VocoderWeights,
    VocoderConfiguration
}

public sealed record F5ArtifactIdentity
{
    public required F5ArtifactRole Role { get; init; }
    public required string ArtifactId { get; init; }
    public required string Revision { get; init; }
    public required string Sha256 { get; init; }
    public required long Bytes { get; init; }
    public required string LicenseId { get; init; }

    internal void Validate()
    {
        F5Guard.Defined(Role);
        F5Guard.Identifier(ArtifactId);
        F5Guard.Revision(Revision);
        F5Guard.Sha256(Sha256);
        F5Guard.Require(Bytes is > 0 and <= 16L * 1024 * 1024 * 1024 * 1024,
            F5Failure.LimitExceeded);
        F5Guard.Identifier(LicenseId);
    }
}

public sealed record F5RuntimeIdentity
{
    public required string WorkerBuildId { get; init; }
    public required string WorkerBuildRevision { get; init; }
    public required string F5PackageVersion { get; init; }
    public required string F5SourceRevision { get; init; }
    public required string PythonVersion { get; init; }
    public required string TorchVersion { get; init; }
    public required string TorchaudioVersion { get; init; }
    public required string CudaRuntimeVersion { get; init; }

    internal void Validate()
    {
        F5Guard.Identifier(WorkerBuildId);
        F5Guard.Revision(WorkerBuildRevision);
        F5Guard.ExactVersion(F5PackageVersion);
        F5Guard.Revision(F5SourceRevision);
        F5Guard.ExactVersion(PythonVersion);
        F5Guard.ExactVersion(TorchVersion);
        F5Guard.ExactVersion(TorchaudioVersion);
        F5Guard.ExactVersion(CudaRuntimeVersion);
    }
}

public sealed class F5WorkerIdentity
{
    private readonly F5ArtifactIdentity[] artifacts;

    public F5WorkerIdentity(
        string workerId,
        F5EvidenceKind evidence,
        F5RuntimeIdentity runtime,
        IEnumerable<F5ArtifactIdentity> artifacts,
        F5CancellationCapability cancellation)
    {
        F5Guard.Identifier(workerId);
        F5Guard.Defined(evidence);
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(artifacts);
        F5Guard.Defined(cancellation);
        runtime.Validate();
        var materialized = artifacts.ToArray();
        F5Guard.Require(materialized.Length == Enum.GetValues<F5ArtifactRole>().Length);
        foreach (var artifact in materialized)
            artifact.Validate();
        F5Guard.Require(materialized.Select(artifact => artifact.Role).Distinct().Count() ==
            materialized.Length);
        F5Guard.Require(materialized.Select(artifact => artifact.ArtifactId)
            .Distinct(StringComparer.Ordinal).Count() == materialized.Length);

        WorkerId = workerId;
        Evidence = evidence;
        Runtime = runtime;
        this.artifacts = materialized.OrderBy(artifact => artifact.Role).ToArray();
        Artifacts = Array.AsReadOnly(this.artifacts);
        Cancellation = cancellation;
    }

    public F5ProtocolVersion ProtocolVersion => F5ProtocolVersion.Current;
    public string ContractId => F5WorkerProtocol.ContractId;
    public string WorkerId { get; }
    public F5EvidenceKind Evidence { get; }
    public F5RuntimeIdentity Runtime { get; }
    public IReadOnlyList<F5ArtifactIdentity> Artifacts { get; }
    public F5CancellationCapability Cancellation { get; }
    public bool PcmTransportStreaming => true;
    public bool IncrementalWithinChunkSynthesis => false;

    public bool Matches(F5WorkerIdentity? other)
    {
        if (other is null || WorkerId != other.WorkerId || Evidence != other.Evidence ||
            Runtime != other.Runtime || Cancellation != other.Cancellation ||
            artifacts.Length != other.artifacts.Length)
            return false;
        return artifacts.Zip(other.artifacts).All(pair => pair.First == pair.Second);
    }

    public override string ToString() =>
        $"{nameof(F5WorkerIdentity)} {{ WorkerId = {WorkerId}, Evidence = {Evidence}, content = omitted }}";
}
