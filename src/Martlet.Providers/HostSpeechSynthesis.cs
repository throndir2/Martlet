using Martlet.Core.Contracts;
using Martlet.Core.Settings;

namespace Martlet.Providers;

/// <summary>The paired Martlet host whose voice engine (its f5 or xtts role, <see cref="SpeechEngines"/>) speaks replies,
/// reached through the host's pinned gateway. <see cref="CredentialId"/> names the TTS route's reference to the host pairing
/// kept in the OS vault; <see cref="ModelId"/> is the host's advertised model on <see cref="RouteId"/> (the engine's gateway
/// route) and <see cref="ReferenceRevision"/> the applied reference voice.</summary>
public sealed record HostSpeechTarget(string Origin, string HostId, string SpkiFingerprint, string DeviceId, Guid CredentialId,
    string ModelId, Guid PresetId, string ReferenceRevision, string RouteId = SelfHostSetup.F5RouteId)
{
    public override string ToString() => nameof(HostSpeechTarget);
}

/// <summary>Streams one reply segment's 24 kHz mono PCM16 bytes from a paired host's F5 voice.
/// Failures throw <see cref="HostTextException"/> with the provider failure code.</summary>
public interface IHostSpeechClient
{
    IAsyncEnumerable<byte[]> StreamAsync(HostSpeechTarget target, BoundedSpeechInput input, CorrelationIds ids, long epoch,
        DateTimeOffset deadline, CancellationToken cancellationToken);
}

/// <summary>One reply segment spoken by the user's own Martlet host instead of a cloud voice, bound to the host's gateway
/// origin and F5 model (the F5 output already has the 24 kHz mono PCM16 format playback expects).</summary>
public sealed class HostSpeechSynthesisStream : PcmChunkSpeechSynthesisStream
{
    public const string ProviderId = SelfHostSetup.GatewayF5Alias;
    private readonly IHostSpeechClient client;
    private readonly HostSpeechTarget target;

    public HostSpeechSynthesisStream(IHostSpeechClient client, HostSpeechTarget target, ProviderRequestContext context,
        SpeechSynthesisSelection selection, BoundedSpeechInput input, SpeechSynthesisLimits limits,
        SpeechDisclosureAuthorization? authorization, TimeProvider? clock = null, CancellationToken callerToken = default)
        : base(ProviderId, CancellationCapability.RequestAbort, Binding(target ?? throw new ArgumentNullException(nameof(target))),
            context, selection, input, limits, authorization, clock, callerToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        this.client = client;
        this.target = target;
    }

    /// <summary>The credential binding a caller authorizes for this host: its gateway origin, the TTS role and F5 model.</summary>
    public static ProviderCredentialBinding Binding(HostSpeechTarget target) =>
        new(new Uri(target.Origin), ProviderRole.Tts, target.ModelId);

    private protected override IAsyncEnumerable<byte[]> Chunks(ProviderRequestContext context, BoundedSpeechInput input,
        SpeechSynthesisLimits limits, DateTimeOffset deadline, CancellationToken cancellationToken) =>
        client.StreamAsync(target, input, context.Ids, context.Epoch, deadline, cancellationToken);

    public override string ToString() => nameof(HostSpeechSynthesisStream);
}
