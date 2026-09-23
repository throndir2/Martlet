using System.Collections.Immutable;
using System.Text.Json;

namespace Martlet.Host.Inventory;

public enum ConstraintState { Known, UnknownUntilH02 }
public sealed record VersionConstraint(ConstraintState State, int? RequiredMajor, string? ExactVersion);
public sealed record CandidateRequirement(ProbeId Probe, string Role, VersionConstraint Version);
public sealed record QualificationTuple(string? OsKernel, string? CpuFeatures, string? GpuModel, long? GpuMemoryMiB,
    string? Driver, string? Docker, string? Compose, string? Toolkit, string? Cuda, string? Framework,
    string? LlmImageDigest, string? TtsImageDigest, string? SttImageDigest, string? ModelRevisions,
    string? Precision, int? ContextTokens, int? Concurrency, long? PeakRamBytes, long? PeakDiskBytes,
    double? WarmLatencyMilliseconds, bool? RightsApproved);
public sealed record CandidateManifest(int SchemaVersion, string Id, string Status, string DiagnosticOs,
    string DiagnosticVersion, string DiagnosticArchitecture, ImmutableArray<CandidateRequirement> Requirements,
    QualificationTuple QualifiedTuple)
{
    public static CandidateManifest Current { get; } = Load();

    private static CandidateManifest Load()
    {
        using var stream = typeof(CandidateManifest).Assembly.GetManifestResourceStream("Martlet.Host.Inventory.candidate-requirements.json")
            ?? throw new InvalidDataException("The candidate requirements manifest is missing.");
        var value = JsonSerializer.Deserialize(stream, HostJson.ManifestType)
            ?? throw new InvalidDataException("The candidate requirements manifest is empty.");
        value.Validate();
        return value;
    }

    public void Validate()
    {
        if (SchemaVersion != 1 || Id != "ubuntu-24.04-x64-h01-candidate" ||
            Status != "CandidateNotQualified" || DiagnosticOs != "Ubuntu" ||
            DiagnosticVersion != "24.04" || DiagnosticArchitecture != "X64" ||
            Requirements.IsDefaultOrEmpty || Requirements.Length != 5 ||
            Requirements.Any(r => r is null || r.Version is null) ||
            Requirements.Select(r => r.Probe).Distinct().Count() != Requirements.Length ||
            Requirements.Any(r => r.Probe is not (ProbeId.Nvidia or ProbeId.DockerEngine or ProbeId.Compose or ProbeId.ContainerToolkit or ProbeId.QualifiedTuple) ||
                r.Role is not { Length: > 0 and <= 160 } || r.Role.Any(char.IsControl) ||
                r.Version.ExactVersion is not null ||
                (r.Probe == ProbeId.Compose
                    ? r.Version.RequiredMajor != 2 || r.Version.State != ConstraintState.Known
                    : r.Version.RequiredMajor is not null || r.Version.State != ConstraintState.UnknownUntilH02)) ||
            QualifiedTuple is not
            {
                OsKernel: null, CpuFeatures: null, GpuModel: null, GpuMemoryMiB: null,
                Driver: null, Docker: null, Compose: null, Toolkit: null, Cuda: null, Framework: null,
                LlmImageDigest: null, TtsImageDigest: null, SttImageDigest: null, ModelRevisions: null,
                Precision: null, ContextTokens: null, Concurrency: null, PeakRamBytes: null, PeakDiskBytes: null,
                WarmLatencyMilliseconds: null, RightsApproved: null
            })
            throw new InvalidDataException("Unsupported candidate requirements manifest.");
    }
}
