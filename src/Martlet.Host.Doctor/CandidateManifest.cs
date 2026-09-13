using System.Collections.Immutable;
using System.Text.Json;

namespace Martlet.Host.Doctor;

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
        using var stream = typeof(CandidateManifest).Assembly.GetManifestResourceStream("Martlet.Host.Doctor.candidate-requirements.json")
            ?? throw new InvalidDataException("The candidate requirements manifest is missing.");
        var value = JsonSerializer.Deserialize(stream, HostJson.ManifestType)
            ?? throw new InvalidDataException("The candidate requirements manifest is empty.");
        if (value.SchemaVersion != 1 || value.Id != "ubuntu-24.04-x64-h01-candidate" ||
            value.Status != "CandidateNotQualified" || value.DiagnosticOs != "Ubuntu" ||
            value.DiagnosticVersion != "24.04" || value.DiagnosticArchitecture != "X64" ||
            value.Requirements.IsDefaultOrEmpty || value.Requirements.Length > 16 ||
            value.Requirements.Select(r => r.Probe).Distinct().Count() != value.Requirements.Length ||
            value.Requirements.Any(r => !Enum.IsDefined(r.Probe) || !Enum.IsDefined(r.Version.State) ||
                r.Version.ExactVersion is not null ||
                (r.Probe == ProbeId.Compose ? r.Version.RequiredMajor != 2 : r.Version.RequiredMajor is not null)))
            throw new InvalidDataException("Unsupported candidate requirements manifest.");
        return value;
    }
}
