using System.Net;
using System.Text;
using System.Text.Json.Serialization;
using Martlet.Core.Contracts;

namespace Martlet.Core.Settings;

// What a self-host route is bound to: the paired host's endpoint, the probed gateway route, the applied F5 reference voice
// and the verified local speech-to-text package.

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
        // A TTS gateway route may name any reference-voice engine's route; all of them share F5's worker contract.
        var engine = RouteType == SetupRouteType.GatewayF5 ? SpeechEngines.ForRoute(RouteId) : null;
        ContractRules.Require(ContractId == named.ContractId &&
            (engine is not null ? Path == engine.Path : RouteId == named.RouteId && Path == named.Path),
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
            MaximumDurationSeconds is > 0 and <= SelfHostSetup.MaximumGatewayRouteDurationSeconds,
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
