namespace Martlet.Perception;

public static class DeterministicPerceptionFixtureIdentity
{
    private const string Revision = "1111111111111111111111111111111111111111";

    public static PerceptionWorkerIdentity Create(
        PerceptionRole role,
        int cpuUnits = 1,
        int gpuMemoryMiB = 0,
        PerceptionCancellationCapability cancellation =
            PerceptionCancellationCapability.DiscardOnly)
    {
        PerceptionWorkerGuard.Defined(role);
        PerceptionWorkerGuard.Defined(cancellation);
        var artifacts = new List<PerceptionArtifactIdentity>
        {
            Artifact(PerceptionArtifactRole.RuntimeImage, "fixture-runtime", '1'),
            Artifact(PerceptionArtifactRole.ModelWeights, "fixture-model", '2'),
            Artifact(PerceptionArtifactRole.ModelConfiguration, "fixture-config", '3')
        };
        if (role == PerceptionRole.VisualQuestionAnswering)
        {
            artifacts.Add(Artifact(PerceptionArtifactRole.Tokenizer, "fixture-tokenizer", '4'));
            artifacts.Add(Artifact(PerceptionArtifactRole.Projector, "fixture-projector", '5'));
        }

        var roleId = role == PerceptionRole.Ocr ? "ocr" : "vlm";
        return new(
            $"martlet-perception-{roleId}-fixture",
            PerceptionEvidenceKind.SyntheticFixture,
            role,
            new()
            {
                WorkerBuildId = $"martlet-perception-{roleId}-fixture",
                WorkerBuildRevision = Revision,
                RuntimeId = "managed-fixture",
                RuntimeVersion = "1.0.0-fixture",
                RuntimeRevision = Revision,
                OperatingSystemId = "fixture-os",
                OperatingSystemVersion = "1.0.0-fixture"
            },
            new()
            {
                Role = role,
                AdapterId = $"martlet-{roleId}-fixture-adapter",
                AdapterVersion = "1.0.0-fixture",
                ModelId = $"synthetic-{roleId}-fixture",
                ModelRevision = Revision,
                ModelSha256 = new string(role == PerceptionRole.Ocr ? '6' : '7', 64),
                LicenseId = "fixture-only"
            },
            artifacts,
            new()
            {
                MaximumInputBytes = PerceptionProtocol.MaximumImageBytes,
                MaximumWidth = PerceptionProtocol.MaximumImageWidth,
                MaximumHeight = PerceptionProtocol.MaximumImageHeight,
                MaximumPixels = PerceptionProtocol.MaximumImagePixels,
                MaximumOutputUtf8Bytes = PerceptionProtocol.MaximumOutputUtf8Bytes,
                MaximumConcurrency = 1
            },
            new()
            {
                CpuUnits = cpuUnits,
                GpuMemoryMiB = gpuMemoryMiB
            },
            cancellation);
    }

    private static PerceptionArtifactIdentity Artifact(
        PerceptionArtifactRole role,
        string id,
        char digest) => new()
        {
            Role = role,
            ArtifactId = id,
            Revision = Revision,
            Sha256 = new string(digest, 64),
            Bytes = 1,
            LicenseId = "fixture-only"
        };
}

