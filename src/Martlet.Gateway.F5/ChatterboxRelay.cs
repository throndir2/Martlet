using Martlet.Core.Settings;

namespace Martlet.Gateway.F5;

/// <summary>
/// Gateway relay from a paired client to this host's own loopback Chatterbox Turbo voice service (the <c>chatterbox</c> host
/// role: workers/chatterbox/martlet_chatterbox_host.py). The service speaks the f5 role's protocol (/synthesize, /cancel and
/// the <c>martlet.f5.worker</c> event stream of contiguous 24 kHz mono PCM16 frames), so the F5 relay serves it on
/// Chatterbox's own route. Reply text may carry Chatterbox's own tags ([laugh], [sigh]...), which the model speaks as sounds.
/// Enabling the role is the host owner's standing permission for paired <c>voice</c> devices to send reply text and the
/// reference recording they chose to it.
/// </summary>
public static class ChatterboxRelay
{
    public const string DefaultWorkerId = "chatterbox-relay";
    public const string DefaultModel = "chatterbox-turbo";

    /// <summary>The model the chatterbox role provisions (martlet_chatterbox_host.py): Hugging Face
    /// ResembleAI/chatterbox-turbo revision and t3_turbo_v1.safetensors SHA-256. Its decoder, voice encoder and tokenizer
    /// files are pinned with it.</summary>
    public static readonly IReadOnlyDictionary<string, (string Revision, string Sha256)> PinnedModels =
        new Dictionary<string, (string, string)>(StringComparer.Ordinal)
        {
            [DefaultModel] = ("749d1c1a46eb10492095d68fbcf55691ccf137cd",
                "fcf1f8c1d651bb7e3acd69ee5be269b4ac10c02980b7708213d598bc9f7cdf87")
        };

    public static F5RelayWorker Create(Uri endpoint, string model, HttpMessageHandler? handler = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (!PinnedModels.TryGetValue(model, out var pinned))
            throw new ArgumentException("The Chatterbox model is not one the chatterbox role provisions.", nameof(model));
        return new F5RelayWorker(endpoint, GatewayInferenceRoute.ReferenceSpeechRelay(SpeechEngines.Chatterbox,
            SpeechEngines.VoiceDestination, DefaultWorkerId, model, pinned.Revision, pinned.Sha256), handler);
    }
}
