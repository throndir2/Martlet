namespace Martlet.LocalStt;

public sealed record LocalSttToolchainComponentFacts
{
    public required string Name { get; init; }
    public required string Version { get; init; }
    public required string Sha256 { get; init; }
    public required string Observation { get; init; }
}

public sealed record LocalSttSourceBuildFacts
{
    public const int MaximumDocumentBytes = 65_536;

    public required int FormatVersion { get; init; }
    public required string ManifestSha256 { get; init; }
    public required string Repository { get; init; }
    public required string Revision { get; init; }
    public required long SourceArchiveBytes { get; init; }
    public required string SourceArchiveSha256 { get; init; }
    public required bool SourceContentHashComplete { get; init; }
    public required bool SourceTreeClean { get; init; }
    public required string Target { get; init; }
    public required string Architecture { get; init; }
    public required string Generator { get; init; }
    public required LocalSttToolchainComponentFacts Compiler { get; init; }
    public required LocalSttToolchainComponentFacts Cmake { get; init; }
    public required LocalSttToolchainComponentFacts WindowsSdk { get; init; }

    public static LocalSttSourceBuildFacts Read(ReadOnlyMemory<byte> bytes) =>
        ProvisioningWire.Read<LocalSttSourceBuildFacts>(
            bytes,
            MaximumDocumentBytes,
            LocalSttProvisioningFailure.ToolchainInvalid);

    public byte[] ToCanonicalJson() => ProvisioningWire.Write(this);
}

public sealed record LocalSttBuildOutput(
    string InstalledName,
    string Purpose);

internal sealed record LocalSttBuildPlanDocument
{
    public required int FormatVersion { get; init; }
    public required string PackageId { get; init; }
    public required string ManifestSha256 { get; init; }
    public required string Repository { get; init; }
    public required string Revision { get; init; }
    public required long SourceArchiveBytes { get; init; }
    public required string SourceArchiveSha256 { get; init; }
    public required string Target { get; init; }
    public required string Architecture { get; init; }
    public required string Generator { get; init; }
    public required LocalSttToolchainComponentFacts[] Toolchain { get; init; }
    public required string[] ConfigureDefinitions { get; init; }
    public required LocalSttBuildOutput[] ExpectedOutputs { get; init; }
    public required bool CompilerExecutionAllowedByDefault { get; init; }
    public required bool AutomaticDependencyAcquisitionAllowed { get; init; }
    public required bool NetworkAllowed { get; init; }
    public required bool RuntimeFileHashQualificationRequired { get; init; }
    public required string PlanSha256 { get; init; }
}

public sealed class LocalSttBuildPlan
{
    private readonly LocalSttBuildPlanDocument document;

    public string PackageId => document.PackageId;
    public string ManifestSha256 => document.ManifestSha256;
    public string Repository => document.Repository;
    public string Revision => document.Revision;
    public string SourceArchiveSha256 => document.SourceArchiveSha256;
    public string Target => document.Target;
    public string Architecture => document.Architecture;
    public string Generator => document.Generator;
    public IReadOnlyList<LocalSttToolchainComponentFacts> Toolchain =>
        Array.AsReadOnly(document.Toolchain);
    public IReadOnlyList<string> ConfigureDefinitions =>
        Array.AsReadOnly(document.ConfigureDefinitions);
    public IReadOnlyList<LocalSttBuildOutput> ExpectedOutputs =>
        Array.AsReadOnly(document.ExpectedOutputs);
    public bool CompilerExecutionAllowedByDefault =>
        document.CompilerExecutionAllowedByDefault;
    public bool AutomaticDependencyAcquisitionAllowed =>
        document.AutomaticDependencyAcquisitionAllowed;
    public bool NetworkAllowed => document.NetworkAllowed;
    public bool RuntimeFileHashQualificationRequired =>
        document.RuntimeFileHashQualificationRequired;
    public string PlanSha256 => document.PlanSha256;

    private LocalSttBuildPlan(LocalSttBuildPlanDocument document)
    {
        this.document = document;
    }

    public static LocalSttBuildPlan Create(LocalSttSourceBuildFacts facts) =>
        Create(LocalSttPackageManifest.Current, facts);

    internal static LocalSttBuildPlan Create(
        LocalSttPackageManifest manifest,
        LocalSttSourceBuildFacts facts)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(facts);
        var runtime = manifest.Document.Runtime;
        ProvisioningGuard.Require(
            facts.FormatVersion == 1 &&
            facts.ManifestSha256 == manifest.DocumentSha256 &&
            facts.Repository == runtime.Repository &&
            facts.Revision == runtime.Revision &&
            facts.SourceArchiveBytes is > 0 and <= 2_147_483_648 &&
            facts.SourceContentHashComplete &&
            facts.SourceTreeClean &&
            facts.Target == manifest.Target &&
            facts.Architecture == "x64",
            LocalSttProvisioningFailure.BuildSourceMismatch);
        ProvisioningGuard.Sha256(
            facts.ManifestSha256,
            LocalSttProvisioningFailure.BuildSourceMismatch);
        ProvisioningGuard.Revision(
            facts.Revision,
            LocalSttProvisioningFailure.BuildSourceMismatch);
        ProvisioningGuard.Sha256(
            facts.SourceArchiveSha256,
            LocalSttProvisioningFailure.BuildSourceMismatch);
        ProvisioningGuard.Require(
            facts.Generator is { Length: >= 1 and <= 128 } &&
            facts.Generator.All(character =>
                char.IsAsciiLetterOrDigit(character) ||
                character is ' ' or '.' or '_' or '-' or '+'),
            LocalSttProvisioningFailure.ToolchainInvalid);
        ValidateTool(facts.Compiler, "msvc");
        ValidateTool(facts.Cmake, "cmake");
        ValidateTool(facts.WindowsSdk, "windows-sdk");

        var unsealed = new LocalSttBuildPlanDocument
        {
            FormatVersion = 1,
            PackageId = manifest.Id,
            ManifestSha256 = manifest.DocumentSha256,
            Repository = runtime.Repository,
            Revision = runtime.Revision,
            SourceArchiveBytes = facts.SourceArchiveBytes,
            SourceArchiveSha256 = facts.SourceArchiveSha256,
            Target = manifest.Target,
            Architecture = facts.Architecture,
            Generator = facts.Generator,
            Toolchain =
            [
                facts.Compiler,
                facts.Cmake,
                facts.WindowsSdk
            ],
            ConfigureDefinitions =
            [
                "BUILD_SHARED_LIBS=ON",
                "GGML_NATIVE=OFF",
                "WHISPER_BUILD_EXAMPLES=ON",
                "WHISPER_BUILD_TESTS=OFF",
                "WHISPER_SDL2=ON"
            ],
            ExpectedOutputs = runtime.Files
                .OrderBy(file => file.InstalledName, StringComparer.Ordinal)
                .Select(file => new LocalSttBuildOutput(
                    file.InstalledName,
                    file.Purpose == RuntimeFilePurpose.Executable
                        ? "executable"
                        : "dependency"))
                .ToArray(),
            CompilerExecutionAllowedByDefault = false,
            AutomaticDependencyAcquisitionAllowed = false,
            NetworkAllowed = false,
            RuntimeFileHashQualificationRequired = true,
            PlanSha256 = ""
        };
        var planSha256 = ProvisioningWire.Hash(
            ProvisioningWire.Write(unsealed));
        return new LocalSttBuildPlan(
            unsealed with { PlanSha256 = planSha256 });
    }

    public byte[] ToCanonicalJson() =>
        ProvisioningWire.Write(document);

    private static void ValidateTool(
        LocalSttToolchainComponentFacts tool,
        string expectedName)
    {
        if (tool is null)
            throw new LocalSttProvisioningException(
                LocalSttProvisioningFailure.ToolchainInvalid);
        ProvisioningGuard.Require(
            tool.Name == expectedName &&
            tool.Version is { Length: >= 1 and <= 64 } &&
            tool.Version.All(character =>
                char.IsAsciiLetterOrDigit(character) ||
                character is '.' or '_' or '-' or '+') &&
            tool.Observation is { Length: >= 1 and <= 128 } &&
            !tool.Observation.Any(char.IsControl),
            LocalSttProvisioningFailure.ToolchainInvalid);
        ProvisioningGuard.Sha256(
            tool.Sha256,
            LocalSttProvisioningFailure.ToolchainInvalid);
    }
}
