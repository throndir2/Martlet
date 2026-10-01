using Martlet.Core.Contracts;
using Martlet.Core.Settings;

namespace Martlet.Providers;

/// <summary>The voice installed in Windows that speaks replies on this PC: <see cref="VoiceId"/> is the exact installed
/// voice token. Nothing is sent anywhere, there is no key and no per-request charge.</summary>
public sealed record WindowsVoiceTarget(string VoiceId)
{
    public override string ToString() => nameof(WindowsVoiceTarget);
}

/// <summary>Speaks one reply segment with an installed Windows voice and returns its 24 kHz mono PCM16 bytes.
/// Failures throw <see cref="WindowsVoiceException"/> with the provider failure code.</summary>
public interface IWindowsVoiceClient
{
    IAsyncEnumerable<byte[]> StreamAsync(WindowsVoiceTarget target, BoundedSpeechInput input, SpeechSynthesisLimits limits,
        DateTimeOffset deadline, CancellationToken cancellationToken);
}

public sealed class WindowsVoiceException(ProviderFailureCode code) : Exception("The Windows voice could not speak the reply.")
{
    public ProviderFailureCode Code { get; } = code;
}

/// <summary>One reply segment spoken by an installed Windows voice on this PC, bound to the local Windows speech destination
/// and the exact voice.</summary>
public sealed class WindowsVoiceSynthesisStream : PcmChunkSpeechSynthesisStream
{
    public const string ProviderId = WindowsSpeechSetup.TtsAlias;
    private readonly IWindowsVoiceClient client;
    private readonly WindowsVoiceTarget target;

    public WindowsVoiceSynthesisStream(IWindowsVoiceClient client, WindowsVoiceTarget target, ProviderRequestContext context,
        SpeechSynthesisSelection selection, BoundedSpeechInput input, SpeechSynthesisLimits limits,
        SpeechDisclosureAuthorization? authorization, TimeProvider? clock = null, CancellationToken callerToken = default)
        : base(ProviderId, CancellationCapability.CooperativeComputeCancel, Binding(target ?? throw new ArgumentNullException(nameof(target))),
            context, selection, input, limits, authorization, clock, callerToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        this.client = client;
        this.target = target;
    }

    /// <summary>The binding a caller authorizes for a Windows voice: this PC's local speech origin, the TTS role and the
    /// installed-voice model.</summary>
    public static ProviderCredentialBinding Binding(WindowsVoiceTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return new(new Uri(SelfHostSetup.LocalOrigin), ProviderRole.Tts, WindowsSpeechSetup.TtsModelId);
    }

    /// <summary>The speech selection that names <paramref name="target"/>.</summary>
    public static SpeechSynthesisSelection Selection(WindowsVoiceTarget target) =>
        new(WindowsSpeechSetup.TtsAlias, WindowsSpeechSetup.TtsModelId, target.VoiceId, SpeechOutputFormat.Pcm24KhzMono16Le);

    private protected override IAsyncEnumerable<byte[]> Chunks(ProviderRequestContext context, BoundedSpeechInput input,
        SpeechSynthesisLimits limits, DateTimeOffset deadline, CancellationToken cancellationToken) =>
        client.StreamAsync(target, input, limits, deadline, cancellationToken);

    public override string ToString() => nameof(WindowsVoiceSynthesisStream);
}
