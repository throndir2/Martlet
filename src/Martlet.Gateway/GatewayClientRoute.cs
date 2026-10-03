using Martlet.F5;
using Martlet.Providers.Ollama;

namespace Martlet.Gateway;

public sealed partial class GatewayInferenceRoute
{
    // Validates a passive, remote description; it does not attest runtime/model readiness.
    public static GatewayInferenceRoute FromCapability(GatewayInferenceRouteCapability capability)
    {
        ArgumentNullException.ThrowIfNull(capability);
        var expected = capability.Kind switch
        {
            GatewayInferenceKind.OllamaChat => ("martlet.gateway.ollama-chat.v1",
                "/martlet/v1/inference/ollama-chat", OllamaChatAdapter.Protocol, "1.0"),
            // Each reference-voice engine has its own route; all speak F5's worker contract.
            GatewayInferenceKind.F5Synthesis when Martlet.Core.Settings.SpeechEngines.ForRoute(capability.RouteId) is { } engine =>
                (engine.RouteId, engine.Path, F5WorkerProtocol.ContractId, F5ProtocolVersion.Current.ToString()),
            GatewayInferenceKind.Audio2Face => (Audio2FaceRouteId, Audio2FacePath,
                Audio2FaceContractId, Audio2FaceContractVersion),
            GatewayInferenceKind.Transcription => (TranscriptionRouteId, TranscriptionPath,
                TranscriptionContractId, TranscriptionContractVersion),
            _ => throw new GatewayProtocolException("worker.invalid")
        };
        GatewayRules.Require(capability.RequiredRole == GatewayRole.Voice &&
            capability.RouteId == expected.Item1 && capability.Path == expected.Item2 &&
            capability.ContractId == expected.Item3 && capability.ContractVersion == expected.Item4 &&
            capability.MaximumConcurrency == 1 && capability.Streaming &&
            capability.MaximumDurationMilliseconds is > 0 and <= 120_000, "worker.invalid");
        return new(capability.Kind, capability.RequiredRole, capability.RouteId, capability.Path,
            capability.ContractId, capability.ContractVersion, capability.DestinationId, capability.WorkerId,
            capability.AdapterVersion, capability.ModelId, capability.ModelRevision, capability.ModelSha256,
            capability.ArtifactIdentitySha256, capability.MaximumRequestBytes, capability.MaximumInputBytes,
            capability.MaximumOutputBytes, capability.MaximumEventBytes, capability.MaximumEvents,
            capability.MaximumStreamBytes, TimeSpan.FromMilliseconds(capability.MaximumDurationMilliseconds),
            capability.Cancellation);
    }
}
