using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Martlet.Core.Contracts;

namespace Martlet.Core.Settings;

public enum SetupRole { Stt, Llm, Tts }
public enum SetupStep { Choice, Destinations, Credentials, Review }
public enum SetupRouteType { OpenAi, GatewayOllama, GatewayF5, LocalWhisper, ChatCompletions, LocalWindowsStt, LocalWindowsTts, GatewayStt }
public enum GatewayCancellationMode { DiscardOnly, RequestAbort, CooperativeComputeCancel }

public static class OpenAiSetup
{
    public const string Origin = "https://api.openai.com";
    public const string Pricing = "https://openai.com/api/pricing/";
    public const string Retention = "https://platform.openai.com/docs/guides/your-data";
    public const string Disclosure = "Cloud requests may cost money. Price, quota, model access and key validity are unknown. " +
        "Saving is not permission for capture or future paid requests. Screen and memory stay OFF. " +
        "No network, model discovery or billable health test is performed.";

    public static string Alias(SetupRole role) => role switch
    {
        SetupRole.Stt => "openai-stt", SetupRole.Llm => "openai-llm", SetupRole.Tts => "openai-tts",
        _ => throw new ContractException(ErrorCode.InvalidContract, "Choose STT, LLM or TTS.")
    };

    public static string Boundary(SetupRole role) => role switch
    {
        SetupRole.Stt => "Microphone audio -> cloud STT. Even speech later suppressed can be disclosed and charged.",
        SetupRole.Llm => "Transcript and conversation text -> cloud LLM.",
        SetupRole.Tts => "Response text -> cloud TTS. Output is generated voice, not a human recording.",
        _ => throw new ContractException(ErrorCode.InvalidContract, "Choose STT, LLM or TTS.")
    };

    internal static void UpstreamId(string value)
    {
        ContractRules.Require(value is { Length: >= 1 and <= 128 } &&
            value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.'),
            "Enter an explicit upstream model or voice ID (1-128 ASCII letters, digits, dot, underscore or hyphen); no endpoint, key or default is accepted.");
    }
}

public static class SelfHostSetup
{
    public const string GatewayOllamaAlias = "gateway-ollama";
    public const string GatewayF5Alias = "gateway-f5";
    public const string GatewaySttAlias = "gateway-stt";
    public const string LocalWhisperAlias = "local-whisper-cpp";
    public const string LocalOrigin = "local://windows";
    public const string GatewayRole = "voice";
    public const string RegistryId = "martlet.gateway.inference-routes";
    public const string RegistryVersion = "1.0";
    public const string OllamaRouteId = "martlet.gateway.ollama-chat.v1";
    public const string F5RouteId = "martlet.gateway.f5-synthesis.v1";
    public const string SttRouteId = "martlet.gateway.transcription.v1";
    public const string OllamaContractId = "ollama-native-chat-v034-text";
    public const string F5ContractId = "martlet.f5.worker";
    public const string SttContractId = "martlet.transcription-relay";
    public const string F5RightsStatementVersion = "voice-rights-v1";

    /// <summary>A paired Martlet host's gateway route (its credential is that pairing's device credential).</summary>
    public static bool IsGateway(SetupRouteType? routeType) =>
        routeType is SetupRouteType.GatewayOllama or SetupRouteType.GatewayF5 or SetupRouteType.GatewayStt;

    /// <summary>The role, provider alias, route ID, path and contract of each gateway route type.</summary>
    public static (SetupRole Role, string Alias, string RouteId, string Path, string ContractId) Gateway(SetupRouteType routeType) =>
        routeType switch
        {
            SetupRouteType.GatewayOllama => (SetupRole.Llm, GatewayOllamaAlias, OllamaRouteId, "/martlet/v1/inference/ollama-chat", OllamaContractId),
            SetupRouteType.GatewayF5 => (SetupRole.Tts, GatewayF5Alias, F5RouteId, "/martlet/v1/inference/f5-synthesis", F5ContractId),
            SetupRouteType.GatewayStt => (SetupRole.Stt, GatewaySttAlias, SttRouteId, "/martlet/v1/inference/transcription", SttContractId),
            _ => throw new ContractException(ErrorCode.InvalidContract, "Choose a named gateway route.")
        };

    public const string Disclosure =
        "Self-host routes remain OFF until their exact endpoint, pinned host identity, role-scoped device credential, " +
        "route snapshot and per-action disclosure are reviewed. Pair, probe, package verification, download, warmup, " +
        "reference snapshot/apply and preview are separate actions. Saving never starts network, device, process or model work.";

    internal static void Identifier(string? value, int maximum = 128)
    {
        ContractRules.Require(value is { Length: > 0 } && value.Length <= maximum &&
            char.IsAsciiLetterOrDigit(value[0]) &&
            value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-'),
            "Use a bounded ASCII identifier.");
    }

    internal static void Token(string? value, int maximum = 128)
    {
        ContractRules.Require(value is { Length: > 0 } && value.Length <= maximum &&
            !value.Any(char.IsControl), "Use a bounded version or model token.");
        try
        {
            ContractRules.Require(Encoding.UTF8.GetByteCount(value!) <= maximum,
                "Use a bounded version or model token.");
        }
        catch (EncoderFallbackException)
        {
            throw new ContractException(ErrorCode.InvalidContract, "Use valid Unicode.");
        }
    }

    internal static void Sha256(string? value, bool prefixed = false)
    {
        var expected = prefixed ? 71 : 64;
        ContractRules.Require(value is { Length: var length } && length == expected &&
            (!prefixed || value.StartsWith("sha256:", StringComparison.Ordinal)) &&
            value[(prefixed ? 7 : 0)..].All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'),
            "Use an exact lowercase SHA-256 identity.");
    }

    internal static void Utc(DateTimeOffset value) =>
        ContractRules.Require(value.Offset == TimeSpan.Zero, "Use a UTC timestamp.");

    internal static void LocalDirectory(string? value)
    {
        try
        {
            ContractRules.Require(value is { Length: >= 4 and <= 1024 } &&
                Path.IsPathFullyQualified(value) &&
                !value.StartsWith(@"\\", StringComparison.Ordinal) &&
                value == Path.TrimEndingDirectorySeparator(Path.GetFullPath(value)) &&
                value != Path.TrimEndingDirectorySeparator(Path.GetPathRoot(value)!),
                "Select one canonical absolute local package directory.");
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ContractException(ErrorCode.InvalidContract,
                "Select one canonical absolute local package directory.");
        }
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GatewayEndpointSettings : IContract
{
    public required int SchemaVersion { get; init; }
    public required string Origin { get; init; }
    public required string HostId { get; init; }
    public required string SpkiFingerprint { get; init; }
    public required string DeviceRole { get; init; }

    public void Validate()
    {
        ContractRules.Require(SchemaVersion == 1, "Unsupported gateway endpoint version.", ErrorCode.UnsupportedVersion);
        SelfHostSetup.Identifier(HostId, 64);
        SelfHostSetup.Sha256(SpkiFingerprint, prefixed: true);
        ContractRules.Require(DeviceRole == SelfHostSetup.GatewayRole, "The self-host voice routes require the scoped gateway voice role.");
        if (!Uri.TryCreate(Origin, UriKind.Absolute, out var uri) ||
            !IPAddress.TryParse(uri.IdnHost, out var address))
            throw new ContractException(ErrorCode.InvalidContract,
                "Use an explicit canonical private or loopback HTTPS IP origin with a port.");
        ContractRules.Require(uri.Scheme == Uri.UriSchemeHttps && string.IsNullOrEmpty(uri.UserInfo) &&
            string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment) &&
            uri.AbsolutePath == "/" && !Origin.EndsWith("/", StringComparison.Ordinal) &&
            IsPrivate(address) &&
            uri.Port is >= 1 and <= 65535,
            "Use an explicit canonical private or loopback HTTPS IP origin with a port.");
        var expected = address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
            ? $"https://[{address}]:{uri.Port}"
            : $"https://{address}:{uri.Port}";
        ContractRules.Require(Origin == expected, "Use the canonical pinned gateway origin.");
    }

    private static bool IsPrivate(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            return false;
        if (IPAddress.IsLoopback(address))
            return true;
        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            return bytes[0] == 10 || bytes[0] == 172 && bytes[1] is >= 16 and <= 31 ||
                bytes[0] == 192 && bytes[1] == 168;
        return address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 &&
            (bytes[0] & 0xfe) == 0xfc;
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GatewayRouteSnapshot : IContract
{
    public required int SchemaVersion { get; init; }
    public required SetupRouteType RouteType { get; init; }
    public required string RegistryId { get; init; }
    public required string RegistryVersion { get; init; }
    public required string RouteId { get; init; }
    public required string Path { get; init; }
    public required string ContractId { get; init; }
    public required string ContractVersion { get; init; }
    public required string DestinationId { get; init; }
    public required string WorkerId { get; init; }
    public required string WorkerPackageRevision { get; init; }
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
    public required int MaximumDurationSeconds { get; init; }
    public required GatewayCancellationMode Cancellation { get; init; }
    public required DateTimeOffset ObservedAtUtc { get; init; }
    public required Guid ProbeRevision { get; init; }

    public void Validate()
    {
        ContractRules.Require(SchemaVersion == 1, "Unsupported gateway route snapshot version.", ErrorCode.UnsupportedVersion);
        ContractRules.Require(SelfHostSetup.IsGateway(RouteType),
            "Only the named Ollama, F5 and speech-to-text gateway routes are supported.");
        ContractRules.Require(RegistryId == SelfHostSetup.RegistryId && RegistryVersion == SelfHostSetup.RegistryVersion,
            "The gateway route registry version is unsupported.", ErrorCode.UnsupportedVersion);
        var named = SelfHostSetup.Gateway(RouteType);
        ContractRules.Require(RouteId == named.RouteId && Path == named.Path && ContractId == named.ContractId,
            "The gateway route discriminator does not match its frozen contract.");
        SelfHostSetup.Token(ContractVersion, 32);
        SelfHostSetup.Identifier(DestinationId);
        SelfHostSetup.Identifier(WorkerId, 96);
        SelfHostSetup.Token(WorkerPackageRevision, 128);
        SelfHostSetup.Token(AdapterVersion, 64);
        SelfHostSetup.Token(ModelId, 128);
        SelfHostSetup.Token(ModelRevision, 128);
        SelfHostSetup.Sha256(ModelSha256);
        SelfHostSetup.Sha256(ArtifactIdentitySha256, prefixed: true);
        ContractRules.Require(MaximumRequestBytes is > 0 and <= 5_700_000 &&
            MaximumInputBytes is > 0 && MaximumInputBytes <= MaximumRequestBytes &&
            MaximumOutputBytes is > 0 and <= 8 * 1024 * 1024 &&
            MaximumEventBytes is > 0 and <= 262_144 &&
            MaximumEvents is > 1 and <= 16_384 &&
            MaximumStreamBytes >= MaximumEventBytes && MaximumStreamBytes <= 8 * 1024 * 1024 &&
            MaximumDurationSeconds is > 0 and <= 120,
            "The gateway route limits are unsupported.");
        ContractRules.Defined(Cancellation);
        SelfHostSetup.Utc(ObservedAtUtc);
        ContractRules.Require(ProbeRevision != Guid.Empty, "A gateway route snapshot requires an explicit probe revision.");
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record F5ReferenceSettings : IContract
{
    public required int SchemaVersion { get; init; }
    public required Guid PresetId { get; init; }
    public required string PresetName { get; init; }
    public required string ReferenceRevision { get; init; }
    public required string AudioSha256 { get; init; }
    public required string TranscriptRevision { get; init; }
    public required long StoreRevision { get; init; }
    public required string ProcessingDestinationId { get; init; }
    public required Guid RightsAcknowledgementId { get; init; }
    public required string RightsStatementVersion { get; init; }
    public required DateTimeOffset AppliedAtUtc { get; init; }
    public required Guid ApplyRevision { get; init; }

    public void Validate()
    {
        ContractRules.Require(SchemaVersion == 1, "Unsupported F5 reference selection version.", ErrorCode.UnsupportedVersion);
        ContractRules.Require(PresetId != Guid.Empty && RightsAcknowledgementId != Guid.Empty &&
            ApplyRevision != Guid.Empty && StoreRevision >= 0,
            "The F5 reference selection requires exact snapshot, rights and apply identities.");
        ContractRules.Require(PresetName is { Length: > 0 and <= 80 } &&
            !PresetName.Any(char.IsControl) &&
            Encoding.UTF8.GetByteCount(PresetName) <= 160,
            "The F5 reference preset name is invalid.");
        SelfHostSetup.Sha256(ReferenceRevision);
        SelfHostSetup.Sha256(AudioSha256);
        SelfHostSetup.Sha256(TranscriptRevision);
        SelfHostSetup.Identifier(ProcessingDestinationId);
        ContractRules.Require(RightsStatementVersion == SelfHostSetup.F5RightsStatementVersion,
            "The F5 voice-rights statement version is unsupported.");
        SelfHostSetup.Utc(AppliedAtUtc);
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LocalSttPackageSettings : IContract
{
    public required int SchemaVersion { get; init; }
    public required string PackageRoot { get; init; }
    public required string PackageId { get; init; }
    public required string ManifestSha256 { get; init; }
    public required string ModelId { get; init; }
    public required string ModelSha256 { get; init; }
    public required string ExecutableArchiveSha256 { get; init; }
    public required string Language { get; init; }
    public required string NetworkPolicy { get; init; }
    public required string RightsRevision { get; init; }
    public required bool RightsReviewed { get; init; }
    public required bool DeniedEgressRequired { get; init; }
    public required DateTimeOffset VerifiedAtUtc { get; init; }
    public required Guid VerificationRevision { get; init; }

    public void Validate()
    {
        ContractRules.Require(SchemaVersion == 1, "Unsupported local STT selection version.", ErrorCode.UnsupportedVersion);
        SelfHostSetup.LocalDirectory(PackageRoot);
        SelfHostSetup.Identifier(PackageId, 64);
        SelfHostSetup.Sha256(ManifestSha256);
        SelfHostSetup.Identifier(ModelId, 64);
        SelfHostSetup.Sha256(ModelSha256);
        SelfHostSetup.Sha256(ExecutableArchiveSha256);
        ContractRules.Require(Language == "en" && NetworkPolicy == "no_network" &&
            RightsRevision is { Length: > 0 and <= 128 } && RightsReviewed && DeniedEgressRequired &&
            VerificationRevision != Guid.Empty,
            "Local STT requires the exact reviewed package and mandatory denied-egress policy.");
        SelfHostSetup.Utc(VerifiedAtUtc);
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DestinationConsent : IContract
{
    public required int SchemaVersion { get; init; }
    public required SetupRole Role { get; init; }
    public required string ProviderAlias { get; init; }
    public required string Origin { get; init; }
    public required string ModelId { get; init; }
    public string? VoiceId { get; init; }
    public Guid? CredentialId { get; init; }
    public required Guid ConfigurationRevision { get; init; }
    public required bool UserSelected { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SetupRouteType? RouteType { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? RouteSchemaVersion { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Enabled { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SelectionSha256 { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? AllowNetworkDisclosure { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? AllowLocalProcess { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? AllowReferenceAudio { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? AllowPotentialCost { get; init; }

    public void Validate()
    {
        ContractRules.Require(SchemaVersion is 1 or 2, "Unsupported consent version.", ErrorCode.UnsupportedVersion);
        ContractRules.Defined(Role);
        ContractRules.Require(UserSelected && ConfigurationRevision != Guid.Empty,
            "Destination consent requires an explicit user selection and configuration revision.");
        if (RouteType == SetupRouteType.ChatCompletions) ChatCompletionsSetup.ModelId(ModelId);
        else if (RouteType == SetupRouteType.LocalWindowsStt) WindowsSpeechSetup.InstalledId(ModelId);
        else OpenAiSetup.UpstreamId(ModelId);
        if (VoiceId is not null)
        {
            if (RouteType == SetupRouteType.LocalWindowsTts) WindowsSpeechSetup.InstalledId(VoiceId);
            else OpenAiSetup.UpstreamId(VoiceId);
        }
        ContractRules.Require(CredentialId != Guid.Empty, "A credential reference must be a nonempty UUID.");
        if (SchemaVersion == 1)
        {
            ContractRules.Require(RouteType is null && RouteSchemaVersion is null && Enabled is null &&
                SelectionSha256 is null && AllowNetworkDisclosure is null && AllowLocalProcess is null &&
                AllowReferenceAudio is null && AllowPotentialCost is null &&
                ProviderAlias == OpenAiSetup.Alias(Role) && Origin == OpenAiSetup.Origin &&
                (Role == SetupRole.Tts ? VoiceId is not null : VoiceId is null),
                "Legacy destination consent is limited to the named OpenAI route.");
            return;
        }
        ContractRules.Require(RouteType is not null && RouteSchemaVersion == 1 && Enabled is not null &&
            AllowNetworkDisclosure is not null && AllowLocalProcess is not null &&
            AllowReferenceAudio is not null && AllowPotentialCost is not null,
            "Versioned route consent is incomplete.");
        ContractRules.Defined(RouteType!.Value);
        SelfHostSetup.Sha256(SelectionSha256);
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SetupRoute : IContract
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? RouteSchemaVersion { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SetupRouteType? RouteType { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Enabled { get; init; }
    public required SetupRole Role { get; init; }
    public required string ProviderAlias { get; init; }
    public required string Origin { get; init; }
    public required string ModelId { get; init; }
    public string? VoiceId { get; init; }
    public Guid? CredentialId { get; init; }
    public required Guid ConfigurationRevision { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public GatewayEndpointSettings? Gateway { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? GatewayDeviceId { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public GatewayRouteSnapshot? GatewaySnapshot { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public F5ReferenceSettings? Reference { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public LocalSttPackageSettings? LocalStt { get; init; }
    public DestinationConsent? Consent { get; init; }

    public DestinationConsent Selection()
    {
        if (RouteType is null)
            return new()
            {
                SchemaVersion = 1,
                Role = Role,
                ProviderAlias = ProviderAlias,
                Origin = Origin,
                ModelId = ModelId,
                VoiceId = VoiceId,
                CredentialId = CredentialId,
                ConfigurationRevision = ConfigurationRevision,
                UserSelected = true
            };
        var routeType = RouteType.Value;
        return new()
        {
            SchemaVersion = 2,
            Role = Role,
            ProviderAlias = ProviderAlias,
            Origin = Origin,
            ModelId = ModelId,
            VoiceId = VoiceId,
            CredentialId = CredentialId,
            ConfigurationRevision = ConfigurationRevision,
            UserSelected = true,
            RouteType = routeType,
            RouteSchemaVersion = RouteSchemaVersion,
            Enabled = Enabled,
            SelectionSha256 = SelectionDigest(),
            AllowNetworkDisclosure = routeType is SetupRouteType.OpenAi or SetupRouteType.ChatCompletions || SelfHostSetup.IsGateway(routeType),
            AllowLocalProcess = routeType is SetupRouteType.LocalWhisper or SetupRouteType.LocalWindowsStt or SetupRouteType.LocalWindowsTts,
            AllowReferenceAudio = routeType == SetupRouteType.GatewayF5,
            AllowPotentialCost = routeType is not (SetupRouteType.LocalWindowsStt or SetupRouteType.LocalWindowsTts)
        };
    }

    public void Validate()
    {
        ContractRules.Defined(Role);
        ContractRules.Require(CredentialId != Guid.Empty, "A credential reference must be a nonempty UUID.");
        ContractRules.Require(ConfigurationRevision != Guid.Empty,
            "A route requires a fresh configuration revision.");
        if (RouteType is null)
            ValidateLegacy();
        else
            ValidateCurrent(RouteType.Value);
        Consent?.Validate();
        ContractRules.Require(Consent is null || Consent == Selection(),
            "This route changed. Review its destination, model, voice, credential and execution boundary, then explicitly consent again.");
    }

    private void ValidateLegacy()
    {
        ContractRules.Require(RouteSchemaVersion is null && Enabled is null && Gateway is null &&
            GatewayDeviceId is null && GatewaySnapshot is null && Reference is null && LocalStt is null,
            "Legacy routes cannot contain versioned route fields.");
        ContractRules.Require(ProviderAlias == OpenAiSetup.Alias(Role) && Origin == OpenAiSetup.Origin,
            "Only the named OpenAI destination is supported by legacy routes.");
        OpenAiSetup.UpstreamId(ModelId);
        ContractRules.Require(Role == SetupRole.Tts ? VoiceId is not null : VoiceId is null,
            "Select a voice only for TTS.");
        if (VoiceId is not null)
            OpenAiSetup.UpstreamId(VoiceId);
    }

    private void ValidateCurrent(SetupRouteType routeType)
    {
        ContractRules.Defined(routeType);
        ContractRules.Require(RouteSchemaVersion == 1 && Enabled is not null,
            "A current route requires its exact route discriminator and enablement state.");
        if (routeType == SetupRouteType.ChatCompletions) ChatCompletionsSetup.ModelId(ModelId);
        else if (routeType == SetupRouteType.LocalWindowsStt) WindowsSpeechSetup.InstalledId(ModelId);
        else OpenAiSetup.UpstreamId(ModelId);
        if (VoiceId is not null)
        {
            if (routeType == SetupRouteType.LocalWindowsTts) WindowsSpeechSetup.InstalledId(VoiceId);
            else OpenAiSetup.UpstreamId(VoiceId);
        }
        switch (routeType)
        {
            case SetupRouteType.LocalWindowsStt:
            case SetupRouteType.LocalWindowsTts:
                var stt = routeType == SetupRouteType.LocalWindowsStt;
                ContractRules.Require(Role == (stt ? SetupRole.Stt : SetupRole.Tts) &&
                    ProviderAlias == (stt ? WindowsSpeechSetup.SttAlias : WindowsSpeechSetup.TtsAlias) &&
                    Origin == SelfHostSetup.LocalOrigin && CredentialId is null &&
                    Gateway is null && GatewayDeviceId is null && GatewaySnapshot is null &&
                    Reference is null && LocalStt is null &&
                    (stt ? VoiceId is null : VoiceId is not null && ModelId == WindowsSpeechSetup.TtsModelId),
                    "Installed Windows speech requires its exact local role and selection, never a cloud credential or gateway route.");
                break;
            case SetupRouteType.ChatCompletions:
                _ = ChatCompletionsSetup.BaseUri(Origin);
                ContractRules.Require(Role == SetupRole.Llm && ProviderAlias == ChatCompletionsSetup.Alias &&
                    VoiceId is null && Gateway is null && GatewayDeviceId is null &&
                    GatewaySnapshot is null && Reference is null && LocalStt is null,
                    "The Chat Completions route must have its own endpoint, model and optional scoped credential.");
                break;
            case SetupRouteType.OpenAi:
                ContractRules.Require(ProviderAlias == OpenAiSetup.Alias(Role) && Origin == OpenAiSetup.Origin &&
                    Gateway is null && GatewayDeviceId is null && GatewaySnapshot is null && Reference is null && LocalStt is null &&
                    (Role == SetupRole.Tts ? VoiceId is not null : VoiceId is null),
                    "The OpenAI route discriminator does not match its route fields.");
                break;
            case SetupRouteType.GatewayOllama:
                ContractRules.Require(Role == SetupRole.Llm && ProviderAlias == SelfHostSetup.GatewayOllamaAlias &&
                    VoiceId is null && Gateway is not null && Reference is null && LocalStt is null,
                    "The gateway Ollama route discriminator does not match its route fields.");
                ValidateGateway(routeType);
                break;
            case SetupRouteType.GatewayStt:
                ContractRules.Require(Role == SetupRole.Stt && ProviderAlias == SelfHostSetup.GatewaySttAlias &&
                    VoiceId is null && Gateway is not null && Reference is null && LocalStt is null,
                    "The gateway speech-to-text route discriminator does not match its route fields.");
                ValidateGateway(routeType);
                break;
            case SetupRouteType.GatewayF5:
                ContractRules.Require(Role == SetupRole.Tts && ProviderAlias == SelfHostSetup.GatewayF5Alias &&
                    VoiceId is null && Gateway is not null && LocalStt is null,
                    "The gateway F5 route discriminator does not match its route fields.");
                ValidateGateway(routeType);
                Reference?.Validate();
                ContractRules.Require(Reference is null || GatewaySnapshot is null ||
                    Reference.ProcessingDestinationId == GatewaySnapshot.DestinationId,
                    "The applied F5 reference is bound to a different processing destination.");
                break;
            case SetupRouteType.LocalWhisper:
                ContractRules.Require(Role == SetupRole.Stt && ProviderAlias == SelfHostSetup.LocalWhisperAlias &&
                    Origin == SelfHostSetup.LocalOrigin && VoiceId is null && CredentialId is null &&
                    Gateway is null && GatewayDeviceId is null && GatewaySnapshot is null && Reference is null,
                    "The local whisper route discriminator does not match its route fields.");
                LocalStt?.Validate();
                ContractRules.Require(LocalStt is null || ModelId == LocalStt.ModelId,
                    "The local STT route model does not match its verified package.");
                break;
        }
        if (Enabled == true)
        {
            ContractRules.Require(routeType switch
            {
                SetupRouteType.OpenAi or SetupRouteType.ChatCompletions or SetupRouteType.LocalWindowsStt or SetupRouteType.LocalWindowsTts => true,
                SetupRouteType.GatewayOllama or SetupRouteType.GatewayStt => GatewaySnapshot is not null && CredentialId is not null,
                SetupRouteType.GatewayF5 => GatewaySnapshot is not null && CredentialId is not null && Reference is not null,
                SetupRouteType.LocalWhisper => LocalStt is not null,
                _ => false
            }, "A self-host route cannot be enabled before its exact pairing, probe, reference or package evidence is configured.");
        }
    }

    private void ValidateGateway(SetupRouteType routeType)
    {
        Gateway!.Validate();
        ContractRules.Require(CredentialId is null || GatewayDeviceId is not null,
            "A paired route requires its exact device identity.");
        if (GatewayDeviceId is not null) SelfHostSetup.Identifier(GatewayDeviceId, 64);
        ContractRules.Require(Origin == Gateway.Origin,
            "The route origin must match the pinned gateway endpoint.");
        GatewaySnapshot?.Validate();
        ContractRules.Require(GatewaySnapshot is null ||
            GatewaySnapshot.RouteType == routeType && GatewaySnapshot.ModelId == ModelId,
            "The selected model does not match the exact gateway route snapshot.");
    }

    public SetupRoute WithCredential(Guid? id)
    {
        ContractRules.Require(RouteType is not (SetupRouteType.LocalWhisper or SetupRouteType.LocalWindowsStt or SetupRouteType.LocalWindowsTts),
            "Local speech has no provider credential.");
        return this with
        {
            CredentialId = id,
            Consent = null,
            Enabled = RouteType switch
            {
                null => null,
                SetupRouteType.OpenAi or SetupRouteType.ChatCompletions => Enabled,
                _ => false
            },
            ConfigurationRevision = Guid.NewGuid()
        };
    }

    public SetupRoute DisableForRestore()
    {
        if (RouteType is null)
            return WithCredential(null);
        return this with
        {
            Enabled = RouteType == SetupRouteType.OpenAi && Enabled == true,
            CredentialId = null,
            GatewayDeviceId = null,
            Consent = null,
            GatewaySnapshot = SelfHostSetup.IsGateway(RouteType)
                ? null
                : GatewaySnapshot,
            Reference = RouteType == SetupRouteType.GatewayF5 ? null : Reference,
            LocalStt = RouteType == SetupRouteType.LocalWhisper ? null : LocalStt,
            ConfigurationRevision = Guid.NewGuid()
        };
    }

    internal SetupRoute UpgradeLegacy()
    {
        if (RouteType is not null)
            return this;
        var upgraded = this with
        {
            RouteSchemaVersion = 1,
            RouteType = SetupRouteType.OpenAi,
            Enabled = true,
            Consent = null
        };
        return upgraded with { Consent = Consent is null ? null : upgraded.Selection() };
    }

    internal string CredentialScope() => RouteType switch
    {
        null or SetupRouteType.OpenAi => $"openai:{Role}",
        SetupRouteType.ChatCompletions => $"chat-completions:{Origin}",
        SetupRouteType.GatewayOllama or SetupRouteType.GatewayF5 or SetupRouteType.GatewayStt =>
            $"gateway:{RouteType}:{Gateway!.Origin}:{Gateway.HostId}:{Gateway.SpkiFingerprint}:{Gateway.DeviceRole}",
        _ => $"none:{RouteType}"
    };

    private string SelectionDigest()
    {
        var bytes = ContractJson.Write(this with { Consent = null }, 65_536);
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CredentialScopeSettings : IContract
{
    public required int SchemaVersion { get; init; }
    public required SetupRouteType RouteType { get; init; }
    public required string ProviderAlias { get; init; }
    public required string Origin { get; init; }
    public string? HostId { get; init; }
    public string? SpkiFingerprint { get; init; }
    public string? DeviceRole { get; init; }
    public string? DeviceId { get; init; }

    public void Validate()
    {
        ContractRules.Require(SchemaVersion == 1, "Unsupported credential scope version.", ErrorCode.UnsupportedVersion);
        ContractRules.Defined(RouteType);
        if (RouteType == SetupRouteType.ChatCompletions)
        {
            _ = ChatCompletionsSetup.BaseUri(Origin);
            ContractRules.Require(ProviderAlias == ChatCompletionsSetup.Alias && HostId is null &&
                SpkiFingerprint is null && DeviceRole is null && DeviceId is null,
                "The Chat Completions credential cleanup scope is invalid.");
            return;
        }
        if (RouteType == SetupRouteType.OpenAi)
        {
            ContractRules.Require(Origin == OpenAiSetup.Origin && HostId is null &&
                SpkiFingerprint is null && DeviceRole is null && DeviceId is null,
                "The OpenAI credential cleanup scope is invalid.");
            return;
        }
        ContractRules.Require(SelfHostSetup.IsGateway(RouteType),
            "Only OpenAI and paired gateway routes own provider credentials.");
        SelfHostSetup.Identifier(DeviceId, 64);
        new GatewayEndpointSettings
        {
            SchemaVersion = 1,
            Origin = Origin,
            HostId = HostId!,
            SpkiFingerprint = SpkiFingerprint!,
            DeviceRole = DeviceRole!
        }.Validate();
        ContractRules.Require(ProviderAlias == SelfHostSetup.Gateway(RouteType).Alias,
            "The gateway credential cleanup scope does not match its route.");
    }

    internal static CredentialScopeSettings From(SetupRoute route)
    {
        var routeType = route.RouteType ?? SetupRouteType.OpenAi;
        ContractRules.Require(routeType is not (SetupRouteType.LocalWhisper or SetupRouteType.LocalWindowsStt or SetupRouteType.LocalWindowsTts),
            "Local speech has no provider credential.");
        return new()
        {
            SchemaVersion = 1,
            RouteType = routeType,
            ProviderAlias = route.ProviderAlias,
            Origin = route.Origin,
            HostId = route.Gateway?.HostId,
            SpkiFingerprint = route.Gateway?.SpkiFingerprint,
            DeviceRole = route.Gateway?.DeviceRole,
            DeviceId = route.GatewayDeviceId
        };
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PendingCredentialRemoval
{
    public required SetupRole Role { get; init; }
    public required Guid CredentialId { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CredentialScopeSettings? Scope { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RetainedGatewayCredential : IContract
{
    public required SetupRole Role { get; init; }
    public required Guid CredentialId { get; init; }
    public required CredentialScopeSettings Scope { get; init; }

    public void Validate()
    {
        ContractRules.Require(CredentialId != Guid.Empty && Scope is not null,
            "A retained pairing requires its owned reference and scope.");
        Scope!.Validate();
        ContractRules.Require(SelfHostSetup.IsGateway(Scope.RouteType) && Role == SelfHostSetup.Gateway(Scope.RouteType).Role,
            "A retained pairing must match its exact gateway role.");
    }

    internal static RetainedGatewayCredential From(SetupRoute route) => new()
    {
        Role = route.Role,
        CredentialId = route.CredentialId!.Value,
        Scope = CredentialScopeSettings.From(route)
    };
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SetupSettings : IContract
{
    public const int CurrentSchemaVersion = 2;
    public const int MaximumRetainedGatewayCredentials = 16;
    public required int SchemaVersion { get; init; }
    public required SetupStep Checkpoint { get; init; }
    public required IReadOnlyList<SetupRoute> Routes { get; init; }
    public required IReadOnlyList<PendingCredentialRemoval> PendingRemovals { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<RetainedGatewayCredential>? RetainedGatewayCredentials { get; init; }

    public void Validate()
    {
        ContractRules.Require(SchemaVersion is 1 or CurrentSchemaVersion,
            "Unsupported setup version.", ErrorCode.UnsupportedVersion);
        ContractRules.Defined(Checkpoint);
        ContractRules.Require(SchemaVersion != 1 || RetainedGatewayCredentials is null,
            "Historical setup cannot contain retained gateway pairings.");
        ContractRules.Require(RetainedGatewayCredentials is null or { Count: <= MaximumRetainedGatewayCredentials },
            "Retained pairing capacity reached. Explicitly remove an unused pairing; none were evicted.");
        ContractRules.Require(Routes is { Count: <= 3 } && PendingRemovals is { Count: <= 16 },
            "Setup supports three roles and at most sixteen pending credential removals.");
        var roles = new HashSet<SetupRole>();
        var ids = new HashSet<Guid>();
        var pairings = new Dictionary<Guid, SetupRoute>();
        foreach (var route in Routes!)
        {
            ContractRules.Require(route is not null, "A route cannot be null.");
            route!.Validate();
            ContractRules.Require(SchemaVersion == 1 ? route.RouteType is null : route.RouteType is not null,
                "The setup and route discriminator versions must advance atomically.");
            ContractRules.Require(roles.Add(route.Role), "Each role must have exactly one selected route.");
            if (route.CredentialId is { } id)
            {
                // Roles handed to the same paired host share that pairing's one device credential.
                ContractRules.Require(ids.Add(id) || pairings.TryGetValue(id, out var other) && SamePairing(other, route),
                    "Credentials cannot be shared across role policies.");
                if (SelfHostSetup.IsGateway(route.RouteType)) pairings.TryAdd(id, route);
            }
        }
        foreach (var removal in PendingRemovals!)
        {
            ContractRules.Require(removal is not null && removal.CredentialId != Guid.Empty,
                "A pending removal requires its owned reference.");
            ContractRules.Defined(removal!.Role);
            removal.Scope?.Validate();
            ContractRules.Require(SchemaVersion != 1 || removal.Scope is null,
                "Historical cleanup cannot contain versioned credential scopes.");
            ContractRules.Require(removal.Scope is null ||
                removal.Scope.RouteType != SetupRouteType.LocalWhisper &&
                (removal.Scope.RouteType switch
                {
                    SetupRouteType.OpenAi => removal.Scope.ProviderAlias == OpenAiSetup.Alias(removal.Role),
                    SetupRouteType.ChatCompletions => removal.Role == SetupRole.Llm,
                    SetupRouteType.GatewayOllama => removal.Role == SetupRole.Llm,
                    SetupRouteType.GatewayF5 => removal.Role == SetupRole.Tts,
                    SetupRouteType.GatewayStt => removal.Role == SetupRole.Stt,
                    _ => false
                }), "The pending credential cleanup scope does not match its role.");
            ContractRules.Require(ids.Add(removal.CredentialId),
                "A pending removal cannot delete an active or duplicate credential.");
        }
        foreach (var retained in RetainedGatewayCredentials ?? [])
        {
            ContractRules.Require(retained is not null, "A retained pairing cannot be null.");
            retained!.Validate();
            ContractRules.Require(ids.Add(retained.CredentialId),
                "Active, retained and pending credential references must be unique.");
        }
    }

    /// <summary>Two gateway routes that use the same paired host with the same device credential.</summary>
    public static bool SamePairing(SetupRoute first, SetupRoute second) =>
        SelfHostSetup.IsGateway(first.RouteType) && SelfHostSetup.IsGateway(second.RouteType) &&
        first.CredentialId is not null && first.CredentialId == second.CredentialId &&
        first.Gateway == second.Gateway && first.GatewayDeviceId == second.GatewayDeviceId;

    internal SetupSettings UpgradeToCurrent()
    {
        if (SchemaVersion == CurrentSchemaVersion)
            return this;
        ContractRules.Require(SchemaVersion == 1, "Unsupported setup version.", ErrorCode.UnsupportedVersion);
        return this with
        {
            SchemaVersion = CurrentSchemaVersion,
            Routes = Routes.Select(route => route.UpgradeLegacy()).ToArray()
        };
    }

    internal SetupSettings DowngradeOpenAiForHistoricalSettings()
    {
        ContractRules.Require(Routes.All(route => route.RouteType == SetupRouteType.OpenAi),
            "Historical settings can contain only legacy OpenAI routes.");
        ContractRules.Require((RetainedGatewayCredentials?.Count ?? 0) == 0 &&
            PendingRemovals.All(item => item.Scope is null || item.Scope.RouteType == SetupRouteType.OpenAi),
            "Historical settings cannot discard owned gateway pairings.");
        return this with
        {
            SchemaVersion = 1,
            RetainedGatewayCredentials = null,
            Routes = Routes.Select(route =>
            {
                var legacy = route with
                {
                    RouteSchemaVersion = null,
                    RouteType = null,
                    Enabled = null,
                    Gateway = null,
                    GatewayDeviceId = null,
                    GatewaySnapshot = null,
                    Reference = null,
                    LocalStt = null,
                    Consent = null
                };
                return legacy with { Consent = route.Consent is null ? null : legacy.Selection() };
            }).ToArray(),
            PendingRemovals = PendingRemovals.Select(item => item with { Scope = null }).ToArray()
        };
    }

    public static AppSettings Begin(AppSettings? prior) =>
        AppSettings.UpgradeToCurrent(prior);

    public static AppSettings SelectRoute(AppSettings settings, SetupRole role, string modelId, string? voiceId)
    {
        settings.Validate();
        var setup = settings.Setup ?? throw new ContractException(ErrorCode.InvalidContract,
            "Open setup before selecting a route.");
        var old = setup.Routes.SingleOrDefault(r => r.Role == role);
        var route = new SetupRoute
        {
            RouteSchemaVersion = 1,
            RouteType = SetupRouteType.OpenAi,
            Enabled = true,
            Role = role,
            ProviderAlias = OpenAiSetup.Alias(role),
            Origin = OpenAiSetup.Origin,
            ModelId = modelId,
            VoiceId = voiceId,
            CredentialId = old?.RouteType == SetupRouteType.OpenAi ? old.CredentialId : null,
            ConfigurationRevision = Guid.NewGuid()
        };
        route.Validate();
        if (old is not null && old.RouteType == SetupRouteType.OpenAi &&
            old.ModelId == modelId && old.VoiceId == voiceId)
            route = old;
        return ReplaceRoute(settings, route);
    }

    public static AppSettings ConfigureGatewayEndpoint(AppSettings settings, SetupRouteType routeType,
        GatewayEndpointSettings endpoint, string expectedModelId)
    {
        settings.Validate();
        ContractRules.Require(SelfHostSetup.IsGateway(routeType),
            "Choose the named Ollama, F5 or speech-to-text gateway route.");
        endpoint.Validate();
        OpenAiSetup.UpstreamId(expectedModelId);
        var role = SelfHostSetup.Gateway(routeType).Role;
        var old = settings.Setup!.Routes.SingleOrDefault(item => item.Role == role);
        var sameScope = old?.RouteType == routeType && old.Gateway == endpoint;
        var route = new SetupRoute
        {
            RouteSchemaVersion = 1,
            RouteType = routeType,
            Enabled = false,
            Role = role,
            ProviderAlias = SelfHostSetup.Gateway(routeType).Alias,
            Origin = endpoint.Origin,
            ModelId = expectedModelId,
            CredentialId = sameScope ? old!.CredentialId : null,
            ConfigurationRevision = Guid.NewGuid(),
            Gateway = endpoint,
            GatewayDeviceId = sameScope ? old!.GatewayDeviceId : null,
            GatewaySnapshot = sameScope && old!.GatewaySnapshot?.ModelId == expectedModelId
                ? old.GatewaySnapshot
                : null,
            Reference = sameScope && routeType == SetupRouteType.GatewayF5
                ? old!.Reference
                : null
        };
        route.Validate();
        if (old is not null &&
            (old with { Consent = null, ConfigurationRevision = Guid.Empty }) ==
            (route with { Consent = null, ConfigurationRevision = Guid.Empty }))
            route = old;
        return ReplaceRoute(settings, route);
    }

    public static AppSettings ApplyGatewaySnapshot(AppSettings settings, SetupRole role,
        GatewayRouteSnapshot snapshot)
    {
        settings.Validate();
        snapshot.Validate();
        var route = settings.Setup!.Routes.SingleOrDefault(item => item.Role == role) ??
            throw new ContractException(ErrorCode.InvalidContract, "Configure the pinned gateway endpoint before probing it.");
        ContractRules.Require(route.RouteType == snapshot.RouteType &&
            SelfHostSetup.IsGateway(route.RouteType),
            "The probe result does not match the selected gateway route.");
        return ReplaceRoute(settings, route with
        {
            Enabled = false,
            ModelId = snapshot.ModelId,
            GatewaySnapshot = snapshot,
            Reference = route.Reference?.ProcessingDestinationId == snapshot.DestinationId
                ? route.Reference
                : null,
            Consent = null,
            ConfigurationRevision = Guid.NewGuid()
        });
    }

    /// <summary>Switches the F5 route to another applied reference voice, keeping its host and enabled state. A route whose
    /// selection was confirmed stays confirmed with the new voice, which the owner chose explicitly.</summary>
    public static AppSettings ApplyF5Reference(AppSettings settings, F5ReferenceSettings reference)
    {
        settings.Validate();
        reference.Validate();
        var route = settings.Setup!.Routes.SingleOrDefault(item => item.Role == SetupRole.Tts) ??
            throw new ContractException(ErrorCode.InvalidContract, "Configure and probe the F5 gateway route first.");
        ContractRules.Require(route.RouteType == SetupRouteType.GatewayF5 &&
            route.GatewaySnapshot?.DestinationId == reference.ProcessingDestinationId,
            "The applied F5 reference does not match the probed processing destination.");
        var changed = route with
        {
            Reference = reference,
            Consent = null,
            ConfigurationRevision = Guid.NewGuid()
        };
        return ReplaceRoute(settings, changed with { Consent = route.Consent is null ? null : changed.Selection() });
    }

    public static AppSettings ConfigureLocalStt(AppSettings settings, LocalSttPackageSettings package)
    {
        settings.Validate();
        package.Validate();
        var old = settings.Setup!.Routes.SingleOrDefault(item => item.Role == SetupRole.Stt);
        var route = new SetupRoute
        {
            RouteSchemaVersion = 1,
            RouteType = SetupRouteType.LocalWhisper,
            Enabled = false,
            Role = SetupRole.Stt,
            ProviderAlias = SelfHostSetup.LocalWhisperAlias,
            Origin = SelfHostSetup.LocalOrigin,
            ModelId = package.ModelId,
            ConfigurationRevision = Guid.NewGuid(),
            LocalStt = package
        };
        if (old is not null &&
            (old with { Consent = null, ConfigurationRevision = Guid.Empty }) ==
            (route with { Consent = null, ConfigurationRevision = Guid.Empty }))
            route = old;
        return ReplaceRoute(settings, route);
    }

    public static AppSettings SetRouteEnabled(AppSettings settings, SetupRole role, bool enabled,
        bool recordSelection)
    {
        settings.Validate();
        var route = settings.Setup!.Routes.SingleOrDefault(item => item.Role == role) ??
            throw new ContractException(ErrorCode.InvalidContract, "Select a route before changing its enablement.");
        if (route.Enabled == enabled && (recordSelection ? route.Consent == route.Selection() : route.Consent is null))
            return settings;
        var changed = route with
        {
            Enabled = enabled,
            Consent = null,
            ConfigurationRevision = Guid.NewGuid()
        };
        changed.Validate();
        if (recordSelection)
            changed = changed with { Consent = changed.Selection() };
        changed.Validate();
        return ReplaceRoute(settings, changed);
    }

    public static AppSettings ReconnectRetainedGateway(AppSettings settings, Guid credentialId)
    {
        settings.Validate();
        var retained = (settings.Setup?.RetainedGatewayCredentials ?? [])
            .SingleOrDefault(item => item.CredentialId == credentialId) ??
            throw new ContractException(ErrorCode.InvalidContract, "Select a current retained pairing.");
        var route = settings.Setup!.Routes.SingleOrDefault(item => item.Role == retained.Role);
        ContractRules.Require(route is not null && route.CredentialId is null &&
            route.RouteType == retained.Scope.RouteType && route.Origin == retained.Scope.Origin &&
            route.Gateway?.HostId == retained.Scope.HostId &&
            route.Gateway?.SpkiFingerprint == retained.Scope.SpkiFingerprint &&
            route.Gateway?.DeviceRole == retained.Scope.DeviceRole,
            "Saved pairing retained; route not connected/mismatched. Select the exact current binding.");
        var updated = settings with { Setup = settings.Setup with
        {
            RetainedGatewayCredentials = settings.Setup.RetainedGatewayCredentials!
                .Where(item => item != retained).ToArray()
        } };
        return ReplaceRoute(updated, route!.DisableForRestore() with
        {
            CredentialId = retained.CredentialId, GatewayDeviceId = retained.Scope.DeviceId
        });
    }

    public static AppSettings DetachRetainedGateway(AppSettings settings, Guid credentialId)
    {
        settings.Validate();
        var retained = (settings.Setup?.RetainedGatewayCredentials ?? [])
            .SingleOrDefault(item => item.CredentialId == credentialId) ??
            throw new ContractException(ErrorCode.InvalidContract, "Select a current retained pairing.");
        var updated = settings with { Setup = settings.Setup! with
        {
            RetainedGatewayCredentials = settings.Setup!.RetainedGatewayCredentials!
                .Where(item => item != retained).ToArray(),
            PendingRemovals = settings.Setup.PendingRemovals.Append(new()
            {
                Role = retained.Role, CredentialId = retained.CredentialId, Scope = retained.Scope
            }).ToArray()
        } };
        updated.Validate();
        return updated;
    }

    public static AppSettings ReplaceRoute(AppSettings settings, SetupRoute route)
    {
        route.Validate();
        var old = settings.Setup!.Routes.SingleOrDefault(item => item.Role == route.Role);
        if (old is not null && settings.Setup.PendingRemovals.Any(item => item.Role == route.Role && SelfHostSetup.IsGateway(item.Scope?.RouteType)))
            ContractRules.Require(old.CredentialScope() == route.CredentialScope(),
                "Remove this role's detached gateway credential before changing its host, pin or route type.");
        var updated = settings with
        {
            Setup = settings.Setup! with
            {
                Routes = settings.Setup!.Routes.Where(r => r.Role != route.Role)
                    .Append(route).OrderBy(r => r.Role).ToArray()
            }
        };
        updated.Validate();
        return updated;
    }

    // A destination switch must not orphan the previous owned key; list it for explicit removal instead.
    public static AppSettings QueueReplacedCredential(AppSettings settings, SetupRoute? previous)
    {
        settings.Validate();
        if (previous?.CredentialId is not { } id ||
            previous.RouteType is not (null or SetupRouteType.OpenAi or SetupRouteType.ChatCompletions))
            return settings;
        var setup = settings.Setup!;
        if (setup.Routes.Any(route => route.CredentialId == id) ||
            setup.PendingRemovals.Any(removal => removal.CredentialId == id))
            return settings;
        var updated = settings with
        {
            Setup = setup with
            {
                PendingRemovals = setup.PendingRemovals.Append(new()
                {
                    Role = previous.Role,
                    CredentialId = id,
                    Scope = previous.RouteType == SetupRouteType.ChatCompletions
                        ? CredentialScopeSettings.From(previous)
                        : null
                }).ToArray()
            }
        };
        updated.Validate();
        return updated;
    }

    public static string Describe(AppSettings? settings)
    {
        if (settings?.Setup is not { } setup)
            return "Setup not started. Open Setup / resume.";
        var lines = new List<string>
        {
            $"Saved choice: {settings.Profile.Kind}. Checkpoint: {setup.Checkpoint}. Configuration only; live account, package, model and device readiness are not established by this summary.",
            "Saved choices do not authorize recording, provider requests, local processes, reference upload, downloads or warmup. This summary starts none of them.",
            AudioSetupStatus.From(settings.Audio).Describe(),
            OpenAiSetup.Disclosure,
            SelfHostSetup.Disclosure
        };
        foreach (var role in Enum.GetValues<SetupRole>())
        {
            var route = setup.Routes.SingleOrDefault(r => r.Role == role);
            if (route is null)
            {
                lines.Add($"{role}: not configured. Select a named versioned route in Setup.");
                continue;
            }
            if (route.RouteType is null or SetupRouteType.OpenAi)
            {
                lines.Add($"{role}: route selected; " +
                    $"{(route.Consent is null ? "consent missing or invalidated by a change; review again" : "destination choice recorded, NOT per-turn authorization")}; " +
                    $"{(route.CredentialId is null ? "credential not configured" : "credential reference saved, OS presence and API validity unknown")}. Connection not checked by this summary." +
                    (route.Enabled == false ? " Route is OFF." : ""));
                continue;
            }
            var paired = SelfHostSetup.IsGateway(route.RouteType)
                ? route.CredentialId is null
                    ? (setup.RetainedGatewayCredentials ?? []).Any(item => item.Role == role)
                        ? "saved pairing retained; route not connected/mismatched"
                        : "gateway credential not paired"
                    : "permanent role-scoped gateway pairing referenced; vault presence and revocation status unknown"
                : route.RouteType == SetupRouteType.ChatCompletions
                    ? route.CredentialId is null ? "explicit keyless endpoint" : "endpoint-scoped credential reference saved; API validity unknown"
                    : "no provider credential";
            var evidence = route.RouteType switch
            {
                SetupRouteType.GatewayOllama or SetupRouteType.GatewayF5 or SetupRouteType.GatewayStt =>
                    route.GatewaySnapshot is null ? "route probe snapshot missing" : "exact route snapshot saved, not live readiness",
                SetupRouteType.LocalWhisper =>
                    route.LocalStt is null ? "package verification missing" : "exact package snapshot saved; launch re-verifies and audits denied egress",
                _ => "adapter catalog selection saved"
            };
            if (route.RouteType == SetupRouteType.GatewayF5)
                evidence += route.Reference is null
                    ? "; applied reference missing"
                    : "; named reference revision applied, source and rights still require per-action review";
            lines.Add($"{role}: {route.RouteType}; {(route.Enabled == true ? "ON in configuration" : "OFF")}; " +
                $"{(route.Consent == route.Selection() ? "selection recorded, NOT per-turn authorization" : "selection consent missing or invalidated")}; " +
                $"{paired}; {evidence}. No connection, model, process or audio action was run by this summary.");
        }
        if (setup.PendingRemovals.Count != 0)
            lines.Add("Credential cleanup pending. Remove each detached reference before changing that role's gateway host, pin or route type.");
        if (setup.RetainedGatewayCredentials is { Count: > 0 })
            lines.Add("Saved pairing retained; route not connected/mismatched. Explicitly reconnect the exact saved binding; saving never contacts the host.");
        return string.Join(Environment.NewLine, lines);
    }
}
