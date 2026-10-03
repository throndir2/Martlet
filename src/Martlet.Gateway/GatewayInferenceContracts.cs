using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Martlet.F5;
using Martlet.Perception;
using Martlet.Providers.Ollama;

namespace Martlet.Gateway;

public static class GatewayInferenceProtocol
{
    public const string RegistryId = "martlet.gateway.inference-routes";
    public const string RegistryVersion = "1.0";
    public const int MaximumRoutes = 16;
    public const int MaximumRequestBytes = 5_700_000;
    public const int MaximumCancelRequestBytes = 2_048;
    public static readonly TimeSpan MaximumCancellationDuration = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan MaximumRetirementDuration = TimeSpan.FromSeconds(2);
}

public enum GatewayInferenceKind
{
    OllamaChat,
    F5Synthesis,
    PerceptionOcr,
    PerceptionVlm,
    Audio2Face,
    Transcription
}

public enum GatewayInferenceEventKind
{
    Started,
    TextDelta,
    AudioFrame,
    ChunkCompleted,
    Observation,
    Completed,
    Canceled,
    Failed,
    FaceFrame
}

public sealed partial class GatewayInferenceRoute
{
    private GatewayInferenceRoute(
        GatewayInferenceKind kind,
        GatewayRole requiredRole,
        string routeId,
        string path,
        string contractId,
        string contractVersion,
        string destinationId,
        string workerId,
        string adapterVersion,
        string modelId,
        string modelRevision,
        string modelSha256,
        string artifactIdentitySha256,
        int maximumRequestBytes,
        int maximumInputBytes,
        int maximumOutputBytes,
        int maximumEventBytes,
        int maximumEvents,
        int maximumStreamBytes,
        TimeSpan maximumDuration,
        GatewayCancellationCapability cancellation,
        PerceptionWorkerIdentity? perceptionIdentity = null)
    {
        GatewayRules.Defined(kind);
        GatewayRules.Defined(requiredRole);
        GatewayRules.Identifier(routeId, 96);
        GatewayRules.Require(path is { Length: > 0 and <= 128 } &&
            path[0] == '/' && path.All(char.IsAscii), "worker.invalid");
        GatewayRules.Identifier(contractId, 96);
        GatewayRules.Token(contractVersion, 32);
        GatewayRules.Identifier(destinationId, 128);
        GatewayRules.Identifier(workerId, 96);
        GatewayRules.Token(adapterVersion, 64);
        GatewayRules.Token(modelId, 128);
        GatewayRules.Token(modelRevision, 128);
        GatewayRules.Sha256(modelSha256);
        GatewayRules.Sha256Fingerprint(artifactIdentitySha256);
        GatewayRules.Require(maximumRequestBytes is > 0 and <= GatewayInferenceProtocol.MaximumRequestBytes &&
            maximumInputBytes is > 0 && maximumInputBytes <= maximumRequestBytes &&
            maximumOutputBytes is > 0 && maximumOutputBytes <= 8 * 1024 * 1024 &&
            maximumEventBytes is > 0 and <= 262_144 &&
            maximumEvents is > 1 and <= 16_384 &&
            maximumStreamBytes >= maximumEventBytes &&
            maximumStreamBytes <= 8 * 1024 * 1024 &&
            maximumDuration > TimeSpan.Zero &&
            maximumDuration <= TimeSpan.FromSeconds(120), "worker.invalid");
        GatewayRules.Defined(cancellation);

        Kind = kind;
        RequiredRole = requiredRole;
        RouteId = routeId;
        Path = path;
        ContractId = contractId;
        ContractVersion = contractVersion;
        DestinationId = destinationId;
        WorkerId = workerId;
        AdapterVersion = adapterVersion;
        ModelId = modelId;
        ModelRevision = modelRevision;
        ModelSha256 = modelSha256;
        ArtifactIdentitySha256 = artifactIdentitySha256;
        MaximumRequestBytes = maximumRequestBytes;
        MaximumInputBytes = maximumInputBytes;
        MaximumOutputBytes = maximumOutputBytes;
        MaximumEventBytes = maximumEventBytes;
        MaximumEvents = maximumEvents;
        MaximumStreamBytes = maximumStreamBytes;
        MaximumDuration = maximumDuration;
        Cancellation = cancellation;
        PerceptionIdentity = perceptionIdentity;
    }

    public GatewayInferenceKind Kind { get; }
    public GatewayRole RequiredRole { get; }
    public string RouteId { get; }
    public string Path { get; }
    public string ContractId { get; }
    public string ContractVersion { get; }
    public string DestinationId { get; }
    public string WorkerId { get; }
    public string AdapterVersion { get; }
    public string ModelId { get; }
    public string ModelRevision { get; }
    public string ModelSha256 { get; }
    public string ArtifactIdentitySha256 { get; }
    public int MaximumRequestBytes { get; }
    public int MaximumInputBytes { get; }
    public int MaximumOutputBytes { get; }
    public int MaximumEventBytes { get; }
    public int MaximumEvents { get; }
    public int MaximumStreamBytes { get; }
    public TimeSpan MaximumDuration { get; }
    public int MaximumConcurrency => 1;
    public bool Streaming => true;
    public GatewayCancellationCapability Cancellation { get; }
    internal PerceptionWorkerIdentity? PerceptionIdentity { get; }

    public static GatewayInferenceRoute OllamaChat(
        string destinationId,
        string workerId,
        OllamaChatModelSelection selection,
        string modelRevision,
        string modelSha256)
    {
        ArgumentNullException.ThrowIfNull(selection);
        GatewayRules.Token(modelRevision, 128);
        GatewayRules.Sha256(modelSha256);
        var capabilities = OllamaChatAdapter.Describe(selection);
        return new(
            GatewayInferenceKind.OllamaChat,
            GatewayRole.Voice,
            "martlet.gateway.ollama-chat.v1",
            "/martlet/v1/inference/ollama-chat",
            OllamaChatAdapter.Protocol,
            GatewayInferenceProtocol.RegistryVersion,
            destinationId,
            workerId,
            capabilities.AdapterVersion,
            selection.ModelAlias,
            modelRevision,
            modelSha256,
            IdentityDigest(
                OllamaChatAdapter.SourceRevision,
                selection.RequestModel,
                modelRevision,
                modelSha256),
            // Room for JSON escaping of the bounded persona, history and user text, plus one base64 screen image.
            maximumRequestBytes: 96 * 1024 + GatewayOllamaChatPayload.MaximumImageBase64Characters + 1024,
            maximumInputBytes: capabilities.MaxInputBytes,
            maximumOutputBytes: 64 * 1024,
            maximumEventBytes: 64 * 1024,
            maximumEvents: 4_096,
            maximumStreamBytes: 4 * 1024 * 1024,
            maximumDuration: TimeSpan.FromSeconds(60),
            GatewayCancellationCapability.RequestAbort);
    }

    public static GatewayInferenceRoute F5Synthesis(
        string destinationId,
        F5WorkerIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var model = identity.Artifacts.Single(
            artifact => artifact.Role == F5ArtifactRole.ModelWeights);
        var identityFields = new List<string>
        {
            identity.ContractId,
            identity.ProtocolVersion.ToString(),
            identity.WorkerId,
            identity.Evidence.ToString(),
            identity.Runtime.WorkerBuildId,
            identity.Runtime.WorkerBuildRevision,
            identity.Runtime.F5PackageVersion,
            identity.Runtime.F5SourceRevision,
            identity.Runtime.PythonVersion,
            identity.Runtime.TorchVersion,
            identity.Runtime.TorchaudioVersion,
            identity.Runtime.CudaRuntimeVersion,
            identity.Cancellation.ToString()
        };
        identityFields.AddRange(identity.Artifacts
            .OrderBy(artifact => artifact.Role)
            .SelectMany(artifact => new[]
            {
                artifact.Role.ToString(),
                artifact.ArtifactId,
                artifact.Revision,
                artifact.Sha256,
                artifact.Bytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
                artifact.LicenseId
            }));
        return new(
            GatewayInferenceKind.F5Synthesis,
            GatewayRole.Voice,
            "martlet.gateway.f5-synthesis.v1",
            "/martlet/v1/inference/f5-synthesis",
            identity.ContractId,
            identity.ProtocolVersion.ToString(),
            destinationId,
            identity.WorkerId,
            "1.0.0",
            model.ArtifactId,
            model.Revision,
            model.Sha256,
            IdentityDigest(identityFields.ToArray()),
            maximumRequestBytes: GatewayInferenceProtocol.MaximumRequestBytes,
            maximumInputBytes: F5ReferenceLimits.MaximumAudioFileBytes,
            maximumOutputBytes: checked((int)F5WorkerProtocol.MaximumSamples * 2),
            maximumEventBytes: 16 * 1024,
            maximumEvents: F5WorkerProtocol.MaximumEvents,
            maximumStreamBytes: 7 * 1024 * 1024,
            maximumDuration: F5WorkerProtocol.MaximumRequestDuration,
            Map(identity.Cancellation));
    }

    /// <summary>
    /// The F5 route a host relays to its own loopback F5 service (the <c>f5</c> host role). The route names the pinned
    /// model weights the host's worker verifies; the complete worker identity (runtime inventory, vocabulary and
    /// vocoder) stays with that worker, which checks it before every synthesis. Output is the F5 worker's contiguous
    /// 24 kHz mono PCM16; the worker cannot stop GPU compute, so cancellation is discard-only.
    /// </summary>
    public static GatewayInferenceRoute F5Relay(
        string destinationId,
        string workerId,
        string modelId,
        string modelRevision,
        string modelSha256) =>
        ReferenceSpeechRelay(Martlet.Core.Settings.SpeechEngines.F5, destinationId, workerId, modelId, modelRevision,
            modelSha256);

    /// <summary>
    /// The route of one reference-voice engine (<see cref="Martlet.Core.Settings.SpeechEngines"/>: F5, XTTS-v2...) a host
    /// relays to its own loopback service. Every engine has its own route ID and path but the same F5 synthesis payload,
    /// <c>martlet.f5.worker</c> event stream and 24 kHz mono PCM16 output, so the same relay and checks serve all of them.
    /// </summary>
    public static GatewayInferenceRoute ReferenceSpeechRelay(
        Martlet.Core.Settings.SpeechEngine engine,
        string destinationId,
        string workerId,
        string modelId,
        string modelRevision,
        string modelSha256)
    {
        ArgumentNullException.ThrowIfNull(engine);
        GatewayRules.Token(modelId, 128);
        GatewayRules.Token(modelRevision, 128);
        GatewayRules.Sha256(modelSha256);
        return new(
            GatewayInferenceKind.F5Synthesis,
            GatewayRole.Voice,
            engine.RouteId,
            engine.Path,
            F5WorkerProtocol.ContractId,
            F5ProtocolVersion.Current.ToString(),
            destinationId,
            workerId,
            "1.0.0",
            modelId,
            modelRevision,
            modelSha256,
            IdentityDigest(F5WorkerProtocol.ContractId, F5ProtocolVersion.Current.ToString(), workerId, modelId,
                modelRevision, modelSha256),
            maximumRequestBytes: GatewayInferenceProtocol.MaximumRequestBytes,
            maximumInputBytes: F5ReferenceLimits.MaximumAudioFileBytes,
            maximumOutputBytes: checked((int)F5WorkerProtocol.MaximumSamples * 2),
            maximumEventBytes: 16 * 1024,
            maximumEvents: F5WorkerProtocol.MaximumEvents,
            maximumStreamBytes: 7 * 1024 * 1024,
            maximumDuration: F5WorkerProtocol.MaximumRequestDuration,
            GatewayCancellationCapability.DiscardOnly);
    }

    public static GatewayInferenceRoute Perception(
        string destinationId,
        PerceptionWorkerIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var kind = identity.Role switch
        {
            PerceptionRole.Ocr => GatewayInferenceKind.PerceptionOcr,
            PerceptionRole.VisualQuestionAnswering => GatewayInferenceKind.PerceptionVlm,
            _ => throw new GatewayProtocolException("worker.invalid")
        };
        var identityFields = new List<string>
        {
            identity.ContractId,
            identity.ProtocolVersion.ToString(),
            identity.WorkerId,
            identity.Evidence.ToString(),
            identity.Role.ToString(),
            identity.Runtime.WorkerBuildId,
            identity.Runtime.WorkerBuildRevision,
            identity.Runtime.RuntimeId,
            identity.Runtime.RuntimeVersion,
            identity.Runtime.RuntimeRevision,
            identity.Runtime.OperatingSystemId,
            identity.Runtime.OperatingSystemVersion,
            identity.Model.AdapterId,
            identity.Model.AdapterVersion,
            identity.Model.ModelId,
            identity.Model.ModelRevision,
            identity.Model.ModelSha256,
            identity.Model.LicenseId,
            identity.Limits.MaximumInputBytes.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            identity.Limits.MaximumWidth.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            identity.Limits.MaximumHeight.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            identity.Limits.MaximumPixels.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            identity.Limits.MaximumOutputUtf8Bytes.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            identity.Limits.MaximumConcurrency.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            identity.Resources.CpuUnits.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            identity.Resources.GpuMemoryMiB.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            identity.Cancellation.ToString()
        };
        identityFields.AddRange(identity.Artifacts
            .OrderBy(artifact => artifact.Role)
            .ThenBy(artifact => artifact.ArtifactId, StringComparer.Ordinal)
            .SelectMany(artifact => new[]
            {
                artifact.Role.ToString(),
                artifact.ArtifactId,
                artifact.Revision,
                artifact.Sha256,
                artifact.Bytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
                artifact.LicenseId
            }));
        return new(
            kind,
            GatewayRole.Perception,
            kind == GatewayInferenceKind.PerceptionOcr
                ? "martlet.gateway.perception-ocr.v1"
                : "martlet.gateway.perception-vlm.v1",
            kind == GatewayInferenceKind.PerceptionOcr
                ? "/martlet/v1/inference/perception/ocr"
                : "/martlet/v1/inference/perception/vlm",
            identity.ContractId,
            identity.ProtocolVersion.ToString(),
            destinationId,
            identity.WorkerId,
            identity.Model.AdapterVersion,
            identity.Model.ModelId,
            identity.Model.ModelRevision,
            identity.Model.ModelSha256,
            IdentityDigest(identityFields.ToArray()),
            maximumRequestBytes: GatewayInferenceProtocol.MaximumRequestBytes,
            maximumInputBytes: identity.Limits.MaximumInputBytes,
            maximumOutputBytes: identity.Limits.MaximumOutputUtf8Bytes,
            maximumEventBytes: 32 * 1024,
            maximumEvents: 4,
            maximumStreamBytes: 64 * 1024,
            maximumDuration: PerceptionProtocol.MaximumJobDuration,
            Map(identity.Cancellation),
            identity);
    }

    public override string ToString() =>
        $"Gateway inference route {{ RouteId = {RouteId}, Kind = {Kind}, content = omitted }}";

    /// <summary>
    /// Relays one bounded generated-speech PCM chunk to the host's own loopback Audio2Face service
    /// and streams its facial frames back. The model identity is the host-selected NIM and model.
    /// </summary>
    public static GatewayInferenceRoute Audio2Face(
        string destinationId,
        string workerId,
        string modelId,
        string modelRevision)
    {
        GatewayRules.Token(modelId, 128);
        GatewayRules.Token(modelRevision, 128);
        var modelSha256 = Convert.ToHexStringLower(SHA256.HashData(
            Encoding.UTF8.GetBytes(modelId + "\n" + modelRevision)));
        return new(
            GatewayInferenceKind.Audio2Face,
            GatewayRole.Voice,
            Audio2FaceRouteId,
            Audio2FacePath,
            Audio2FaceContractId,
            Audio2FaceContractVersion,
            destinationId,
            workerId,
            "1.0.0",
            modelId,
            modelRevision,
            modelSha256,
            IdentityDigest(Audio2FaceContractId, workerId, modelId, modelRevision),
            maximumRequestBytes: 640 * 1024,
            maximumInputBytes: Audio2FaceMaximumPcmBytes,
            maximumOutputBytes: 4 * 1024 * 1024,
            maximumEventBytes: 8 * 1024,
            maximumEvents: 2_048,
            maximumStreamBytes: 8 * 1024 * 1024,
            maximumDuration: TimeSpan.FromSeconds(30),
            GatewayCancellationCapability.RequestAbort);
    }

    public const string Audio2FaceRouteId = "martlet.gateway.audio2face.v1";
    public const string Audio2FacePath = "/martlet/v1/inference/audio2face";
    public const string Audio2FaceContractId = "martlet.audio2face-relay";
    public const string Audio2FaceContractVersion = "1.0";
    /// <summary>At most four seconds of 48 kHz mono signed 16-bit PCM per request.</summary>
    public const int Audio2FaceMaximumPcmBytes = 48_000 * 2 * 4;

    /// <summary>
    /// Relays one bounded utterance (16 kHz mono PCM16) to the host's own loopback speech-to-text
    /// service and returns its final transcript as text events. The model identity is the host-selected model.
    /// </summary>
    public static GatewayInferenceRoute Transcription(
        string destinationId,
        string workerId,
        string modelId,
        string modelRevision)
    {
        GatewayRules.Token(modelId, 128);
        GatewayRules.Token(modelRevision, 128);
        var modelSha256 = Convert.ToHexStringLower(SHA256.HashData(
            Encoding.UTF8.GetBytes(modelId + "\n" + modelRevision)));
        return new(
            GatewayInferenceKind.Transcription,
            GatewayRole.Voice,
            TranscriptionRouteId,
            TranscriptionPath,
            TranscriptionContractId,
            TranscriptionContractVersion,
            destinationId,
            workerId,
            "1.0.0",
            modelId,
            modelRevision,
            modelSha256,
            IdentityDigest(TranscriptionContractId, workerId, modelId, modelRevision),
            // Base64 PCM plus the request envelope.
            maximumRequestBytes: 1_400_000,
            maximumInputBytes: TranscriptionMaximumPcmBytes,
            maximumOutputBytes: TranscriptionMaximumTextBytes,
            maximumEventBytes: TranscriptionMaximumTextBytes,
            maximumEvents: 16,
            maximumStreamBytes: 256 * 1024,
            maximumDuration: TimeSpan.FromSeconds(60),
            GatewayCancellationCapability.RequestAbort);
    }

    public const string TranscriptionRouteId = "martlet.gateway.transcription.v1";
    public const string TranscriptionPath = "/martlet/v1/inference/transcription";
    public const string TranscriptionContractId = "martlet.transcription-relay";
    public const string TranscriptionContractVersion = "1.0";
    public const int TranscriptionSampleRate = 16_000;
    /// <summary>At most 30 seconds of 16 kHz mono signed 16-bit PCM per utterance.</summary>
    public const int TranscriptionMaximumPcmBytes = TranscriptionSampleRate * 2 * 30;
    /// <summary>Final transcript bound: 4,096 characters of up to four UTF-8 bytes each.</summary>
    public const int TranscriptionMaximumTextBytes = 16 * 1024;

    private static GatewayCancellationCapability Map(F5CancellationCapability capability) =>
        capability switch
        {
            F5CancellationCapability.DiscardOnly => GatewayCancellationCapability.DiscardOnly,
            F5CancellationCapability.RequestAbort => GatewayCancellationCapability.RequestAbort,
            F5CancellationCapability.CooperativeComputeCancel =>
                GatewayCancellationCapability.CooperativeComputeCancel,
            _ => throw new GatewayProtocolException("worker.invalid")
        };

    private static GatewayCancellationCapability Map(
        PerceptionCancellationCapability capability) =>
        capability switch
        {
            PerceptionCancellationCapability.DiscardOnly =>
                GatewayCancellationCapability.DiscardOnly,
            PerceptionCancellationCapability.RequestAbort =>
                GatewayCancellationCapability.RequestAbort,
            PerceptionCancellationCapability.CooperativeComputeCancel =>
                GatewayCancellationCapability.CooperativeComputeCancel,
            _ => throw new GatewayProtocolException("worker.invalid")
        };

    private static string IdentityDigest(params string[] fields)
    {
        var canonical = Encoding.UTF8.GetBytes(string.Join('\n', fields));
        return "sha256:" + Convert.ToHexStringLower(SHA256.HashData(canonical));
    }
}

public sealed record GatewayInferenceRouteCapability
{
    public required string RouteId { get; init; }
    public required string Path { get; init; }
    public required GatewayInferenceKind Kind { get; init; }
    public required GatewayRole RequiredRole { get; init; }
    public required string ContractId { get; init; }
    public required string ContractVersion { get; init; }
    public required string DestinationId { get; init; }
    public required string WorkerId { get; init; }
    public required string AdapterVersion { get; init; }
    public required string ModelId { get; init; }
    public required string ModelRevision { get; init; }
    public required string ModelSha256 { get; init; }
    public required string ArtifactIdentitySha256 { get; init; }
    public required int MaximumRequestBytes { get; init; }
    public required int MaximumInputBytes { get; init; }
    public required int MaximumOutputBytes { get; init; }
    public required int MaximumEventBytes { get; init; }
    public required int MaximumEvents { get; init; }
    public required int MaximumStreamBytes { get; init; }
    public required long MaximumDurationMilliseconds { get; init; }
    public required int MaximumConcurrency { get; init; }
    public required bool Streaming { get; init; }
    public required GatewayCancellationCapability Cancellation { get; init; }

    internal static GatewayInferenceRouteCapability From(
        GatewayInferenceRoute route) => new()
        {
            RouteId = route.RouteId,
            Path = route.Path,
            Kind = route.Kind,
            RequiredRole = route.RequiredRole,
            ContractId = route.ContractId,
            ContractVersion = route.ContractVersion,
            DestinationId = route.DestinationId,
            WorkerId = route.WorkerId,
            AdapterVersion = route.AdapterVersion,
            ModelId = route.ModelId,
            ModelRevision = route.ModelRevision,
            ModelSha256 = route.ModelSha256,
            ArtifactIdentitySha256 = route.ArtifactIdentitySha256,
            MaximumRequestBytes = route.MaximumRequestBytes,
            MaximumInputBytes = route.MaximumInputBytes,
            MaximumOutputBytes = route.MaximumOutputBytes,
            MaximumEventBytes = route.MaximumEventBytes,
            MaximumEvents = route.MaximumEvents,
            MaximumStreamBytes = route.MaximumStreamBytes,
            MaximumDurationMilliseconds = Convert.ToInt64(
                route.MaximumDuration.TotalMilliseconds,
                System.Globalization.CultureInfo.InvariantCulture),
            MaximumConcurrency = route.MaximumConcurrency,
            Streaming = route.Streaming,
            Cancellation = route.Cancellation
        };
}

public abstract class GatewayInferencePayload
{
    internal abstract void Clear();
    public override string ToString() => $"{GetType().Name} (content omitted)";
}

public sealed class GatewayOllamaChatPayload : GatewayInferencePayload
{
    /// <summary>At most one image (base64 JPEG/PNG) rides with the current user input.</summary>
    public const int MaximumImages = 1;
    public const int MaximumImageBase64Characters = (Martlet.Providers.BoundedImage.HardMaxBytes + 2) / 3 * 4;

    internal GatewayOllamaChatPayload(
        string input,
        double temperature,
        int maximumOutputTokens,
        int maximumContextTokens,
        string? system = null,
        IReadOnlyList<Martlet.Providers.TextHistoryMessage>? history = null,
        IReadOnlyList<string>? images = null,
        GatewayOllamaSampling? sampling = null)
    {
        Input = input;
        Temperature = temperature;
        MaximumOutputTokens = maximumOutputTokens;
        MaximumContextTokens = maximumContextTokens;
        System = system;
        History = history ?? [];
        Images = images ?? [];
        Sampling = sampling ?? GatewayOllamaSampling.None;
    }

    public string Input { get; }
    public double Temperature { get; }
    public int MaximumOutputTokens { get; }
    public int MaximumContextTokens { get; }
    /// <summary>Optional persona/instructions, sent to the model as its system message.</summary>
    public string? System { get; }
    /// <summary>Earlier exchanges of this conversation, oldest first; the current user input follows them.</summary>
    public IReadOnlyList<Martlet.Providers.TextHistoryMessage> History { get; }
    /// <summary>Base64 JPEG/PNG images attached to the current input (a screen glance); empty for plain chat.</summary>
    public IReadOnlyList<string> Images { get; }
    /// <summary>Optional Ollama sampling options and context window; unset values keep the host's defaults.</summary>
    public GatewayOllamaSampling Sampling { get; }
    internal override void Clear() { }
}

/// <summary>Optional Ollama sampling options a client may send with a chat request; null keeps Ollama's own default.
/// <see cref="ContextTokens"/> replaces the relay's default context window (num_ctx).</summary>
public sealed record GatewayOllamaSampling(
    double? TopP = null, int? TopK = null, double? MinP = null, double? RepeatPenalty = null,
    double? FrequencyPenalty = null, double? PresencePenalty = null, int? ContextTokens = null)
{
    public static GatewayOllamaSampling None { get; } = new();

    public static GatewayOllamaSampling From(Martlet.Core.Settings.GenerationSettings? settings) => settings is null ? None : new(
        settings.TopP, settings.TopK, settings.MinP, settings.RepeatPenalty, settings.FrequencyPenalty, settings.PresencePenalty,
        settings.ContextTokens);
}

public sealed record GatewayF5TextChunk(int Index, string ChunkId, string Text)
{
    public override string ToString() =>
        $"Gateway F5 text chunk {{ Index = {Index}, content = omitted }}";
}

public sealed class GatewayF5SynthesisPayload : GatewayInferencePayload
{
    private readonly byte[] referenceAudio;
    private readonly GatewayF5TextChunk[] chunks;

    internal GatewayF5SynthesisPayload(
        Guid presetId,
        string referenceRevision,
        string referenceAudioSha256,
        string transcript,
        string transcriptRevision,
        byte[] referenceAudio,
        GatewayF5TextChunk[] chunks,
        string? referenceLanguage = null,
        IReadOnlyList<Martlet.Core.Voices.SpeakingVoiceClip>? referenceClips = null)
    {
        PresetId = presetId;
        ReferenceRevision = referenceRevision;
        ReferenceAudioSha256 = referenceAudioSha256;
        Transcript = transcript;
        TranscriptRevision = transcriptRevision;
        ReferenceLanguage = referenceLanguage;
        ReferenceClips = referenceClips;
        this.referenceAudio = referenceAudio;
        this.chunks = chunks;
        Chunks = Array.AsReadOnly(this.chunks);
    }

    public Guid PresetId { get; }
    public string ReferenceRevision { get; }
    public string ReferenceAudioSha256 { get; }
    public string Transcript { get; }
    public string TranscriptRevision { get; }
    /// <summary>The recording's language ("en" or "ja") when the client sent it; GPT-SoVITS reads the transcript in it.</summary>
    public string? ReferenceLanguage { get; }
    /// <summary>For a voice made from several recordings and an engine that learns from each (XTTS-v2, GPT-SoVITS), where
    /// each lies in <see cref="ReferenceAudio"/> and its words; null when the engine gets the recording as one.</summary>
    public IReadOnlyList<Martlet.Core.Voices.SpeakingVoiceClip>? ReferenceClips { get; }
    public ReadOnlyMemory<byte> ReferenceAudio => referenceAudio;
    public IReadOnlyList<GatewayF5TextChunk> Chunks { get; }

    internal override void Clear() => CryptographicOperations.ZeroMemory(referenceAudio);
}

public sealed class GatewayPerceptionPayload : GatewayInferencePayload
{
    internal GatewayPerceptionPayload(
        SelectedWindowFrame frame,
        string? question,
        TimeSpan maximumFrameAge)
    {
        Frame = frame;
        Question = question;
        MaximumFrameAge = maximumFrameAge;
    }

    public SelectedWindowFrame Frame { get; }
    public string? Question { get; }
    public TimeSpan MaximumFrameAge { get; }
    internal override void Clear() { }
}

/// <summary>Mono signed 16-bit little-endian generated-speech PCM; never microphone audio.</summary>
public sealed class GatewayAudio2FacePayload : GatewayInferencePayload
{
    private readonly byte[] pcm;

    internal GatewayAudio2FacePayload(int sampleRate, byte[] pcm)
    {
        SampleRate = sampleRate;
        this.pcm = pcm;
    }

    public int SampleRate { get; }
    public ReadOnlyMemory<byte> Pcm => pcm;
    public int SampleCount => pcm.Length / 2;
    internal override void Clear() => CryptographicOperations.ZeroMemory(pcm);
}

/// <summary>One microphone utterance as 16 kHz mono signed 16-bit little-endian PCM, to be transcribed.</summary>
public sealed class GatewayTranscriptionPayload : GatewayInferencePayload
{
    private readonly byte[] pcm;

    internal GatewayTranscriptionPayload(int sampleRate, byte[] pcm)
    {
        SampleRate = sampleRate;
        this.pcm = pcm;
    }

    public int SampleRate { get; }
    public ReadOnlyMemory<byte> Pcm => pcm;
    internal override void Clear() => CryptographicOperations.ZeroMemory(pcm);
}

public sealed class GatewayInferenceRequest
{
    internal GatewayInferenceRequest(
        GatewayInferenceRoute route,
        Guid sessionId,
        Guid turnId,
        Guid requestId,
        Guid? parentRequestId,
        long epoch,
        DateTimeOffset deadlineUtc,
        GatewayInferencePayload payload)
    {
        Route = route;
        SessionId = sessionId;
        TurnId = turnId;
        RequestId = requestId;
        ParentRequestId = parentRequestId;
        Epoch = epoch;
        DeadlineUtc = deadlineUtc;
        Payload = payload;
    }

    public GatewayProtocolVersion ProtocolVersion => GatewayProtocolVersion.Current;
    public GatewayInferenceRoute Route { get; }
    public Guid SessionId { get; }
    public Guid TurnId { get; }
    public Guid RequestId { get; }
    public Guid? ParentRequestId { get; }
    public long Epoch { get; }
    public DateTimeOffset DeadlineUtc { get; }
    public GatewayInferencePayload Payload { get; }

    public override string ToString() =>
        $"Gateway inference request {{ RouteId = {Route.RouteId}, RequestId = {RequestId}, content = omitted }}";
}

public sealed class GatewayInferenceEvent
{
    private readonly byte[] payload;

    public GatewayInferenceEvent(
        GatewayInferenceEventKind kind,
        long sequence,
        string routeId,
        string destinationId,
        string workerId,
        string modelId,
        string modelRevision,
        string modelSha256,
        string artifactIdentitySha256,
        Guid sessionId,
        Guid turnId,
        Guid requestId,
        long epoch,
        ReadOnlyMemory<byte> payload = default,
        string? errorCode = null,
        long? frameSequence = null,
        int? chunkIndex = null,
        long? sampleOffset = null,
        int? sampleCount = null,
        long? finalSampleCount = null)
    {
        Kind = kind;
        Sequence = sequence;
        RouteId = routeId;
        DestinationId = destinationId;
        WorkerId = workerId;
        ModelId = modelId;
        ModelRevision = modelRevision;
        ModelSha256 = modelSha256;
        ArtifactIdentitySha256 = artifactIdentitySha256;
        SessionId = sessionId;
        TurnId = turnId;
        RequestId = requestId;
        Epoch = epoch;
        this.payload = payload.ToArray();
        ErrorCode = errorCode;
        FrameSequence = frameSequence;
        ChunkIndex = chunkIndex;
        SampleOffset = sampleOffset;
        SampleCount = sampleCount;
        FinalSampleCount = finalSampleCount;
    }

    public GatewayInferenceEventKind Kind { get; }
    public long Sequence { get; }
    public string RouteId { get; }
    public string DestinationId { get; }
    public string WorkerId { get; }
    public string ModelId { get; }
    public string ModelRevision { get; }
    public string ModelSha256 { get; }
    public string ArtifactIdentitySha256 { get; }
    public Guid SessionId { get; }
    public Guid TurnId { get; }
    public Guid RequestId { get; }
    public long Epoch { get; }
    public ReadOnlyMemory<byte> Payload => payload;
    public string? ErrorCode { get; }
    public long? FrameSequence { get; }
    public int? ChunkIndex { get; }
    public long? SampleOffset { get; }
    public int? SampleCount { get; }
    public long? FinalSampleCount { get; }
    public bool IsTerminal => Kind is GatewayInferenceEventKind.Completed or
        GatewayInferenceEventKind.Canceled or GatewayInferenceEventKind.Failed;

    internal static GatewayInferenceEvent Failure(
        GatewayInferenceRequest request,
        long sequence,
        string code) => new(
            GatewayInferenceEventKind.Failed,
            sequence,
            request.Route.RouteId,
            request.Route.DestinationId,
            request.Route.WorkerId,
            request.Route.ModelId,
            request.Route.ModelRevision,
            request.Route.ModelSha256,
            request.Route.ArtifactIdentitySha256,
            request.SessionId,
            request.TurnId,
            request.RequestId,
            request.Epoch,
            errorCode: code);

    internal static GatewayInferenceEvent Canceled(
        GatewayInferenceRequest request,
        long sequence,
        long? finalSampleCount = null) => new(
            GatewayInferenceEventKind.Canceled,
            sequence,
            request.Route.RouteId,
            request.Route.DestinationId,
            request.Route.WorkerId,
            request.Route.ModelId,
            request.Route.ModelRevision,
            request.Route.ModelSha256,
            request.Route.ArtifactIdentitySha256,
            request.SessionId,
            request.TurnId,
            request.RequestId,
            request.Epoch,
            finalSampleCount: finalSampleCount);

    public override string ToString() =>
        $"Gateway inference event {{ Kind = {Kind}, Sequence = {Sequence}, RequestId = {RequestId}, content = omitted }}";
}

public sealed record GatewayInferenceCancellationRequest
{
    public required string RouteId { get; init; }
    public required Guid RequestId { get; init; }
}

public sealed record GatewayInferenceCancellationReceipt
{
    public required string RouteId { get; init; }
    public required Guid RequestId { get; init; }
    public required bool LocalDiscardAcknowledged { get; init; }
    public required GatewayCancellationCapability ComputeCancellation { get; init; }
    public required bool WorkerMayContinue { get; init; }
}

public enum GatewayInferenceWorkerFailure
{
    Unavailable,
    OptionalContextUnavailable,
    Failed,
    PermissionDenied,
    IdentityMismatch
}

public sealed class GatewayInferenceWorkerException : Exception
{
    public GatewayInferenceWorkerException(GatewayInferenceWorkerFailure failure)
        : base("The private inference worker failed.")
    {
        GatewayRules.Defined(failure);
        Failure = failure;
    }

    public GatewayInferenceWorkerFailure Failure { get; }
    internal string Code => Failure switch
    {
        GatewayInferenceWorkerFailure.Unavailable => "worker.unavailable",
        GatewayInferenceWorkerFailure.OptionalContextUnavailable => "context.unavailable",
        GatewayInferenceWorkerFailure.PermissionDenied => "action.denied",
        GatewayInferenceWorkerFailure.IdentityMismatch => "worker.identity",
        _ => "worker.failed"
    };
}

public interface IGatewayInferenceWorker
{
    GatewayInferenceRoute Route { get; }

    ValueTask<GatewayInferencePermissionLease> AcquirePermissionAsync(
        GatewayInferenceRequest request,
        GatewayPrincipal principal,
        CancellationToken cancellationToken);

    IAsyncEnumerable<GatewayInferenceEvent> ExecuteAsync(
        GatewayInferenceRequest request,
        CancellationToken cancellationToken);

    ValueTask<GatewayInferenceCancellationReceipt> CancelAsync(
        GatewayInferenceCancellationRequest request,
        CancellationToken cancellationToken);
}

// Host-supplied, per-action authorization and validated worker readiness/identity.
// Paired-device authentication alone must never create this lease.
public abstract class GatewayInferencePermissionLease : IAsyncDisposable
{
    protected GatewayInferencePermissionLease(GatewayInferenceRequest request, GatewayPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(principal);
        Request = request;
        HostId = principal.HostId;
        DeviceId = principal.DeviceId;
        CredentialId = principal.CredentialId;
        Role = principal.Role;
    }

    internal GatewayInferenceRequest Request { get; }
    internal string HostId { get; }
    internal string DeviceId { get; }
    internal string CredentialId { get; }
    internal GatewayRole Role { get; }
    public abstract CancellationToken Revoked { get; }
    public abstract void Validate();
    public abstract ValueTask DisposeAsync();
    public override string ToString() => "Gateway inference permission lease (content omitted)";
}

public interface IOllamaGatewayInferenceWorker : IGatewayInferenceWorker
{
}

public interface IF5GatewayInferenceWorker : IGatewayInferenceWorker
{
}

public interface IPerceptionGatewayInferenceWorker : IGatewayInferenceWorker
{
}

public interface IAudio2FaceGatewayInferenceWorker : IGatewayInferenceWorker
{
}

public interface ITranscriptionGatewayInferenceWorker : IGatewayInferenceWorker
{
}

internal static class GatewayInferenceEventValidator
{
    internal static void Validate(
        GatewayInferenceEvent? value,
        GatewayInferenceRequest request,
        long expectedSequence)
    {
        GatewayRules.Require(value is not null, "stream.invalid");
        var item = value!;
        GatewayRules.Defined(item.Kind);
        GatewayRules.Require(
            item.Sequence == expectedSequence &&
            item.RouteId == request.Route.RouteId &&
            item.DestinationId == request.Route.DestinationId &&
            item.WorkerId == request.Route.WorkerId &&
            item.ModelId == request.Route.ModelId &&
            item.ModelRevision == request.Route.ModelRevision &&
            item.ModelSha256 == request.Route.ModelSha256 &&
            item.ArtifactIdentitySha256 == request.Route.ArtifactIdentitySha256 &&
            item.SessionId == request.SessionId &&
            item.TurnId == request.TurnId &&
            item.RequestId == request.RequestId &&
            item.Epoch == request.Epoch,
            "worker.identity");
        GatewayRules.Require(expectedSequence != 0 ||
            item.Kind == GatewayInferenceEventKind.Started, "stream.invalid");
        GatewayRules.Require(expectedSequence == 0 ||
            item.Kind != GatewayInferenceEventKind.Started, "stream.invalid");

        var payloadLength = item.Payload.Length;
        GatewayRules.Require(payloadLength <= request.Route.MaximumEventBytes,
            "stream.limit");
        if (request.Route.Kind is GatewayInferenceKind.PerceptionOcr or
            GatewayInferenceKind.PerceptionVlm)
            GatewayRules.Require(
                payloadLength <= request.Route.MaximumOutputBytes,
                "stream.limit");
        switch (item.Kind)
        {
            case GatewayInferenceEventKind.Started:
                GatewayRules.Require(payloadLength == 0 && item.ErrorCode is null,
                    "stream.invalid");
                RequireNoF5Metadata(item);
                break;
            case GatewayInferenceEventKind.TextDelta:
                GatewayRules.Require(
                    request.Route.Kind is GatewayInferenceKind.OllamaChat or GatewayInferenceKind.Transcription &&
                    payloadLength > 0 &&
                    item.ErrorCode is null &&
                    IsUtf8(item.Payload.Span) &&
                    HasNoF5Metadata(item),
                    "stream.invalid");
                break;
            case GatewayInferenceEventKind.AudioFrame:
                GatewayRules.Require(
                    request.Route.Kind == GatewayInferenceKind.F5Synthesis &&
                    item.FrameSequence is >= 0 &&
                    item.ChunkIndex is >= 0 &&
                    item.SampleOffset is >= 0 &&
                    item.SampleCount is > 0 and <=
                        F5WorkerProtocol.MaximumFrameSamples &&
                    payloadLength == item.SampleCount * 2 &&
                    payloadLength <= F5WorkerProtocol.MaximumFrameBytes &&
                    item.FinalSampleCount is null &&
                    item.ErrorCode is null,
                    "stream.invalid");
                break;
            case GatewayInferenceEventKind.ChunkCompleted:
                GatewayRules.Require(
                    request.Route.Kind == GatewayInferenceKind.F5Synthesis &&
                    payloadLength == 0 &&
                    item.FrameSequence is null &&
                    item.ChunkIndex is >= 0 &&
                    item.SampleOffset is null &&
                    item.SampleCount is null &&
                    item.FinalSampleCount is >= 0 and <=
                        F5WorkerProtocol.MaximumSamples &&
                    item.ErrorCode is null,
                    "stream.invalid");
                break;
            case GatewayInferenceEventKind.Observation:
                GatewayRules.Require(
                    (request.Route.Kind is GatewayInferenceKind.PerceptionOcr or
                        GatewayInferenceKind.PerceptionVlm) &&
                    payloadLength > 0 &&
                    item.ErrorCode is null &&
                    HasNoF5Metadata(item) &&
                    IsJsonObject(item.Payload),
                    "stream.invalid");
                break;
            case GatewayInferenceEventKind.FaceFrame:
                GatewayRules.Require(
                    request.Route.Kind == GatewayInferenceKind.Audio2Face &&
                    payloadLength > 0 &&
                    item.ErrorCode is null &&
                    item.FrameSequence is >= 0 &&
                    item.SampleOffset is >= 0 &&
                    item.ChunkIndex is null &&
                    item.SampleCount is null &&
                    item.FinalSampleCount is null &&
                    IsJsonObject(item.Payload),
                    "stream.invalid");
                break;
            case GatewayInferenceEventKind.Completed:
            case GatewayInferenceEventKind.Canceled:
                GatewayRules.Require(payloadLength == 0 &&
                    item.ErrorCode is null, "stream.invalid");
                if (request.Route.Kind == GatewayInferenceKind.F5Synthesis)
                {
                    GatewayRules.Require(item.FrameSequence is null &&
                        item.ChunkIndex is null &&
                        item.SampleOffset is null &&
                        item.SampleCount is null &&
                        item.FinalSampleCount is >= 0 and <=
                            F5WorkerProtocol.MaximumSamples,
                        "stream.invalid");
                }
                else
                {
                    RequireNoF5Metadata(item);
                }
                break;
            case GatewayInferenceEventKind.Failed:
                GatewayRules.Require(payloadLength == 0 &&
                    item.ErrorCode is "worker.unavailable" or "worker.failed" or
                        "context.unavailable" or "job.deadline" or "job.canceled",
                    "stream.invalid");
                RequireNoF5Metadata(item);
                break;
        }
    }

    private static bool HasNoF5Metadata(GatewayInferenceEvent item) =>
        item.FrameSequence is null &&
        item.ChunkIndex is null &&
        item.SampleOffset is null &&
        item.SampleCount is null &&
        item.FinalSampleCount is null;

    private static void RequireNoF5Metadata(GatewayInferenceEvent item) =>
        GatewayRules.Require(HasNoF5Metadata(item), "stream.invalid");

    private static bool IsUtf8(ReadOnlySpan<byte> value)
    {
        try
        {
            _ = new UTF8Encoding(false, true).GetString(value);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    internal sealed class GatewayInferenceStreamState(
        GatewayInferenceRequest request,
        string hostId,
        TimeProvider clock)
    {
        private long expectedFrameSequence;
        private long expectedSampleOffset;
        private int completedChunks;
        private int dataEvents;
        private bool nonWhitespaceText;
        private long outputBytes;
        private ReadOnlyMemory<byte>? observation;
        private readonly int f5ChunkCount =
            request.Payload is GatewayF5SynthesisPayload f5
                ? f5.Chunks.Count
                : 0;
        internal long F5FinalSampleCount => expectedSampleOffset;

        internal void Accept(GatewayInferenceEvent item)
        {
            switch (item.Kind)
            {
                case GatewayInferenceEventKind.TextDelta:
                    AddOutputBytes(item.Payload.Length);
                    dataEvents++;
                    nonWhitespaceText |= !string.IsNullOrWhiteSpace(
                        Encoding.UTF8.GetString(item.Payload.Span));
                    break;
                case GatewayInferenceEventKind.AudioFrame:
                    GatewayRules.Require(
                        completedChunks < f5ChunkCount &&
                        item.FrameSequence == expectedFrameSequence &&
                        item.SampleOffset == expectedSampleOffset &&
                        item.ChunkIndex == completedChunks,
                        "stream.invalid");
                    AddOutputBytes(item.Payload.Length);
                    expectedFrameSequence++;
                    expectedSampleOffset = checked(
                        expectedSampleOffset + item.SampleCount!.Value);
                    GatewayRules.Require(expectedSampleOffset <=
                        F5WorkerProtocol.MaximumSamples, "stream.limit");
                    dataEvents++;
                    break;
                case GatewayInferenceEventKind.ChunkCompleted:
                    GatewayRules.Require(completedChunks < f5ChunkCount &&
                        item.ChunkIndex == completedChunks &&
                        item.FinalSampleCount == expectedSampleOffset,
                        "stream.invalid");
                    completedChunks++;
                    break;
                case GatewayInferenceEventKind.Observation:
                    GatewayPerceptionOutput.Validate(
                        item.Payload,
                        request,
                        hostId,
                        clock.GetUtcNow());
                    observation = item.Payload;
                    AddOutputBytes(item.Payload.Length);
                    dataEvents++;
                    GatewayRules.Require(dataEvents == 1, "stream.invalid");
                    break;
                case GatewayInferenceEventKind.FaceFrame:
                    GatewayRules.Require(
                        request.Payload is GatewayAudio2FacePayload face &&
                        item.FrameSequence == expectedFrameSequence &&
                        item.SampleOffset >= expectedSampleOffset &&
                        item.SampleOffset <= face.SampleCount,
                        "stream.invalid");
                    AddOutputBytes(item.Payload.Length);
                    expectedFrameSequence++;
                    expectedSampleOffset = item.SampleOffset!.Value;
                    dataEvents++;
                    break;
                case GatewayInferenceEventKind.Completed:
                    ValidateCompleted(item);
                    break;
                case GatewayInferenceEventKind.Canceled:
                    if (request.Route.Kind == GatewayInferenceKind.F5Synthesis)
                        GatewayRules.Require(item.FinalSampleCount ==
                            expectedSampleOffset, "stream.invalid");
                    break;
            }
        }

        private void AddOutputBytes(int bytes)
        {
            outputBytes = checked(outputBytes + bytes);
            GatewayRules.Require(outputBytes <= request.Route.MaximumOutputBytes,
                "stream.limit");
        }

        internal void ValidatePublication()
        {
            if (observation is { } bytes)
                GatewayPerceptionOutput.Validate(bytes, request, hostId, clock.GetUtcNow());
        }

        private void ValidateCompleted(GatewayInferenceEvent item)
        {
            switch (request.Route.Kind)
            {
                case GatewayInferenceKind.OllamaChat:
                    GatewayRules.Require(dataEvents > 0 && nonWhitespaceText,
                        "stream.invalid");
                    break;
                case GatewayInferenceKind.F5Synthesis:
                    var payload = request.Payload as GatewayF5SynthesisPayload;
                    GatewayRules.Require(payload is not null &&
                        completedChunks == payload.Chunks.Count &&
                        request.Route.Kind == GatewayInferenceKind.F5Synthesis &&
                        item.FinalSampleCount == expectedSampleOffset,
                        "stream.invalid");
                    break;
                case GatewayInferenceKind.PerceptionOcr:
                case GatewayInferenceKind.PerceptionVlm:
                    GatewayRules.Require(dataEvents == 1, "stream.invalid");
                    break;
                case GatewayInferenceKind.Audio2Face:
                    GatewayRules.Require(dataEvents > 0, "stream.invalid");
                    break;
                case GatewayInferenceKind.Transcription:
                    // No text means no speech was recognized; that is a completed transcription.
                    break;
            }
        }
    }

    private static bool IsJsonObject(ReadOnlyMemory<byte> value)
    {
        try
        {
            using var document = JsonDocument.Parse(value, new JsonDocumentOptions
            {
                MaxDepth = 16
            });
            return document.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
