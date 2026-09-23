using System.Collections.Immutable;

namespace Martlet.Host.Inventory;

public enum DoctorScope { Inventory, Prerequisites }
public enum Provenance { LiveLocal, AuthoredFixture, NotObserved }
public enum ProbeId
{
    Platform, Context, Cpu, Memory, Disk, Nvidia, DockerEngine, Compose, ContainerToolkit,
    DockerAccess, Network, GatewayPort, LocalClock, Reboot, QualifiedTuple, ContainerGpu, ModelInference, ClockAccuracy, PairingFirewall
}
public enum FindingState { Observed, Missing, Unknown, Warning, Unsupported, NotRun }
public enum FindingCode
{
    HOST_OBSERVED, HOST_MISSING, HOST_PERMISSION_DENIED, HOST_UNSUPPORTED_EXECUTION,
    HOST_UNQUALIFIED_PLATFORM, HOST_INCOMPLETE, HOST_UNSUPPORTED_RESPONSE, HOST_TIMEOUT,
    HOST_CANCELED, HOST_MALFORMED, HOST_OUTPUT_LIMIT, HOST_IO_ERROR, HOST_COMMAND_FAILED,
    HOST_PORT_IN_USE, HOST_NOT_RUN, HOST_UNQUALIFIED_VERSIONS, HOST_DOCKER_PRIVILEGE,
    HOST_DRIVER_UNAVAILABLE, HOST_GPU_NOT_VISIBLE, HOST_REBOOT_PENDING
}
public enum ActionId { ReviewObservation, ReviewTarget, ReviewAccess, RetryReadOnly, ReviewPrerequisite, ReviewPort, AwaitQualification, ReviewDockerPrivilege, ReviewReboot }
public enum Distro { Ubuntu, Debian, Other, Unknown, NonLinux }
public enum KernelFlavor { Generic, Azure, LowLatency, Virtual, Wsl, Other, Unknown }
public enum ExecutionContext { ContainerIndicators, Wsl, NoContainerIndicators, Unknown }
public enum BinaryPresence { Present, Missing, Unknown }
public enum PackagePresence { Installed, NotInstalled, Unknown }
public enum ToolKind { DockerEngine, Compose, ContainerToolkit }

public sealed record PlatformData(Distro Distribution, string? Version, string? KernelRelease, KernelFlavor KernelFlavor,
    string OsArchitecture, string ProcessArchitecture, string DotnetRuntime, bool Linux);
public sealed record ContextData(ExecutionContext Context, bool PhysicalHostConfirmed = false);
public sealed record CpuData(int LogicalProcessors, bool Sse42, bool Avx, bool Avx2, bool Avx512F);
public sealed record MemoryData(long TotalBytes, long AvailableBytes, long SwapTotalBytes, long SwapFreeBytes);
public sealed record DiskData(long TotalBytes, long AvailableBytes, string Location = "RootFilesystem", long? FreeInodes = null);
public sealed record GpuData(string Model, string DriverVersion, long MemoryMiB);
public sealed record ToolData(ToolKind Tool, PackagePresence Package, BinaryPresence StandardFile, string? VersionCore);
public sealed record DockerAccessData(bool EffectiveRoot, bool DockerGroupMember, BinaryPresence StandardSocket,
    bool DaemonContacted = false, bool? DaemonAccess = null);
public sealed record NetworkData(int Interfaces, int LoopbackV4, int PrivateV4, int LinkLocalV4, int OtherV4,
    int LoopbackV6, int LinkLocalV6, int UniqueLocalV6, int OtherV6);
public sealed record PortData(int Port, bool OccupancyObserved, bool GatewayAvailabilityEstablished = false);
public sealed record ClockData(DateTimeOffset LocalUtc, bool AccuracyEstablished = false);
public sealed record RebootData(bool MarkerPresent, bool RebootOutcomeEstablished = false);

// A closed union keeps native output and arbitrary metadata out of the shareable contract.
public sealed record Evidence
{
    public PlatformData? Platform { get; init; }
    public ContextData? Context { get; init; }
    public CpuData? Cpu { get; init; }
    public MemoryData? Memory { get; init; }
    public DiskData? Disk { get; init; }
    public ImmutableArray<GpuData>? Gpus { get; init; }
    public ToolData? Tool { get; init; }
    public DockerAccessData? DockerAccess { get; init; }
    public NetworkData? Network { get; init; }
    public PortData? Port { get; init; }
    public ClockData? Clock { get; init; }
    public RebootData? Reboot { get; init; }
}

public sealed record ProbeResult(ProbeId Id, bool Required, FindingCode Code, Provenance Provenance,
    DateTimeOffset? ObservedAt, double DurationMilliseconds, double? AgeMilliseconds, string Source, Evidence? Evidence)
{
    public FindingState State => RemedyCatalog.Get(Code).State;
    public string Summary => RemedyCatalog.Get(Code).Summary;
    public Remedy Remedy => RemedyCatalog.For(Id, Code);
}

public sealed record HostReport(int SchemaVersion, string ApplicationVersion, string ManifestId, DoctorScope Scope,
    Provenance Provenance, DateTimeOffset CreatedAt, ImmutableArray<ProbeResult> Probes)
{
    public bool DeploymentQualified => false;
    public CandidateManifest CandidateRequirements => CandidateManifest.Current;
    public string ScopeNotice => Provenance == Provenance.AuthoredFixture
        ? "AUTHORED FIXTURE - NOT THIS HOST, NOT AI, NOT GPU OR DEPLOYMENT QUALIFICATION."
        : "LOCAL READ-ONLY OBSERVATIONS - NOT GPU/MODEL FIT, PAIRING, FIREWALL OR DEPLOYMENT QUALIFICATION.";
    public int ExitCode
    {
        get
        {
            Validate();
            if (Probes.Any(p => p.Code == FindingCode.HOST_UNSUPPORTED_EXECUTION)) return 3;
            if (Probes.Any(p => p.Required && p.State == FindingState.Missing)) return 1;
            if (!Probes.Any(p => p.Required) || Probes.Any(p => p.Required && p.State != FindingState.Observed)) return 2;
            return 0;
        }
    }

    public static bool RequiredInScope(ProbeId id, DoctorScope scope) =>
        id is ProbeId.Platform or ProbeId.Context or ProbeId.Cpu or ProbeId.Memory or ProbeId.Disk or ProbeId.Network or ProbeId.GatewayPort or ProbeId.LocalClock ||
        scope == DoctorScope.Prerequisites && id is ProbeId.Nvidia or ProbeId.DockerEngine or ProbeId.Compose or ProbeId.ContainerToolkit or ProbeId.DockerAccess or ProbeId.QualifiedTuple;

    public void Validate()
    {
        if (SchemaVersion != 1 || ManifestId != CandidateManifest.Current.Id || ApplicationVersion != "0.1.0" ||
            !Enum.IsDefined(Scope) || !Enum.IsDefined(Provenance) || Provenance == Provenance.NotObserved ||
            CreatedAt == default || Probes.IsDefaultOrEmpty || Probes.Length != Enum.GetValues<ProbeId>().Length ||
            Probes.Any(p => p is null) ||
            Probes.Select(p => p.Id).Distinct().Count() != Probes.Length)
            throw new InvalidDataException("Unsupported host report contract.");
        foreach (var probe in Probes)
        {
            if (!Enum.IsDefined(probe.Id) || !Enum.IsDefined(probe.Code) || !Enum.IsDefined(probe.Provenance) ||
                probe.Required != RequiredInScope(probe.Id, Scope) ||
                probe.Provenance != Provenance && probe.Provenance != Provenance.NotObserved ||
                !double.IsFinite(probe.DurationMilliseconds) || probe.DurationMilliseconds < 0 ||
                probe.AgeMilliseconds is { } age && (!double.IsFinite(age) || age < 0) ||
                probe.Source != EvidenceSources.Description(probe.Id) ||
                (probe.ObservedAt is null) != (probe.Provenance == Provenance.NotObserved) ||
                probe.ObservedAt > CreatedAt || probe.ObservedAt == default(DateTimeOffset) ||
                probe.AgeMilliseconds != (probe.ObservedAt is { } at ? (CreatedAt - at).TotalMilliseconds : null) ||
                probe.State == FindingState.Observed && (probe.Evidence is null || probe.ObservedAt is null) ||
                probe.Id is ProbeId.ContainerGpu or ProbeId.ModelInference or ProbeId.ClockAccuracy or ProbeId.PairingFirewall &&
                    probe.Code != FindingCode.HOST_NOT_RUN ||
                probe.Id == ProbeId.QualifiedTuple && probe.Code is not (FindingCode.HOST_NOT_RUN or FindingCode.HOST_UNQUALIFIED_VERSIONS) ||
                probe.Code == FindingCode.HOST_NOT_RUN && (probe.Provenance != Provenance.NotObserved || probe.Evidence is not null))
                throw new InvalidDataException("Invalid host probe evidence.");
            EvidenceRules.Validate(probe);
        }
    }
}
