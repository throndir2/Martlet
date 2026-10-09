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
            // The conversation model and the Thinking pool roles' own Ollama servers (one per graphics card) share the native-chat
            // contract.
            GatewayInferenceKind.OllamaChat when Martlet.Core.Settings.SelfHostSetup.DeepThinkingCard(capability.RouteId) is { } card =>
                (Martlet.Core.Settings.SelfHostSetup.DeepThinkingRouteIdFor(card), Martlet.Core.Settings.SelfHostSetup.DeepThinkingPathFor(card),
                    OllamaChatAdapter.Protocol, "1.0"),
            GatewayInferenceKind.OllamaChat => (Martlet.Core.Settings.SelfHostSetup.OllamaRouteId,
                Martlet.Core.Settings.SelfHostSetup.OllamaPath, OllamaChatAdapter.Protocol, "1.0"),
            // Each reference-voice engine has its own route; all speak F5's worker contract.
            GatewayInferenceKind.F5Synthesis when Martlet.Core.Settings.SpeechEngines.ForRoute(capability.RouteId) is { } engine =>
                (engine.RouteId, engine.Path, F5WorkerProtocol.ContractId, F5ProtocolVersion.Current.ToString()),
            GatewayInferenceKind.Audio2Face => (Audio2FaceRouteId, Audio2FacePath,
                Audio2FaceContractId, Audio2FaceContractVersion),
            GatewayInferenceKind.Transcription => (TranscriptionRouteId, TranscriptionPath,
                TranscriptionContractId, TranscriptionContractVersion),
            GatewayInferenceKind.Song => (SongRouteId, SongPath, SongContractId, SongContractVersion),
            GatewayInferenceKind.Picture => (PictureRouteId, PicturePath, PictureContractId, PictureContractVersion),
            GatewayInferenceKind.Ocr => (OcrRouteId, OcrPath, OcrContractId, OcrContractVersion),
            _ => throw new GatewayProtocolException("worker.invalid")
        };
        GatewayRules.Require(capability.RequiredRole == GatewayRole.Voice &&
            capability.RouteId == expected.Item1 && capability.Path == expected.Item2 &&
            capability.ContractId == expected.Item3 && capability.ContractVersion == expected.Item4 &&
            (capability.MaximumConcurrency == 1 || Martlet.Core.Settings.SelfHostSetup.IsDeepThinkingRoute(capability.RouteId) &&
                capability.MaximumConcurrency is > 1 and <= Martlet.Core.Settings.SelfHostSetup.DeepThinkingMaximumSlots) &&
            capability.Streaming &&
            capability.MaximumDurationMilliseconds > 0 &&
            capability.MaximumDurationMilliseconds <= GatewayInferenceProtocol.MaximumJobDuration.TotalMilliseconds, "worker.invalid");
        var route = new GatewayInferenceRoute(capability.Kind, capability.RequiredRole, capability.RouteId, capability.Path,
            capability.ContractId, capability.ContractVersion, capability.DestinationId, capability.WorkerId,
            capability.AdapterVersion, capability.ModelId, capability.ModelRevision, capability.ModelSha256,
            capability.ArtifactIdentitySha256, capability.MaximumRequestBytes, capability.MaximumInputBytes,
            capability.MaximumOutputBytes, capability.MaximumEventBytes, capability.MaximumEvents,
            capability.MaximumStreamBytes, TimeSpan.FromMilliseconds(capability.MaximumDurationMilliseconds),
            capability.Cancellation, maximumConcurrency: capability.MaximumConcurrency);
        // A host older than GPU priority sends neither; a newer one names the lane its route ID implies.
        GatewayRules.Require(capability.Lane is null || capability.Lane == route.Lane, "worker.invalid");
        return capability.Gpus.Count == 0 ? route : route.PlaceOn(capability.Gpus);
    }
}
