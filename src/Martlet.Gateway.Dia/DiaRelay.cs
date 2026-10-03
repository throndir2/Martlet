using Martlet.Core.Settings;
using Martlet.Gateway.F5;

namespace Martlet.Gateway.Dia;

/// <summary>
/// Gateway relay from a paired client to this host's own loopback Dia voice service (the <c>dia</c> host role:
/// workers/dia/martlet_dia/host.py). The service speaks the f5 role's protocol (/synthesize, /cancel and the
/// <c>martlet.f5.worker</c> event stream of contiguous 24 kHz mono PCM16 frames, resampled from Dia's 44.1 kHz codec), so
/// the F5 relay serves it on Dia's own route. Reply text reaches Dia unchanged, so nonverbal cues such as (laughs) are
/// performed. Enabling the role is the host owner's standing permission for paired <c>voice</c> devices to send reply text
/// and the reference recording they chose to it.
/// </summary>
public static class DiaRelay
{
    public const string DefaultWorkerId = "dia-relay";
    public const string DefaultModel = "dia-1.6b-0626";

    /// <summary>The weights the dia role provisions (workers/dia/martlet_dia/pins.py PINNED_MODELS): Hugging Face
    /// nari-labs/Dia-1.6B-0626 revision and pytorch_model.bin SHA-256. Its config and the DAC codec are pinned with it.</summary>
    public static readonly IReadOnlyDictionary<string, (string Revision, string Sha256)> PinnedModels =
        new Dictionary<string, (string, string)>(StringComparer.Ordinal)
        {
            [DefaultModel] = ("ef2795fcc29c5abe6ffc91fd33808588b49bbc66",
                "8a5106c06899aeea013a7f6ef32e84a15a30f965be676b97996b3ddaf1eb55b9")
        };

    public static F5RelayWorker Create(Uri endpoint, string model, HttpMessageHandler? handler = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (!PinnedModels.TryGetValue(model, out var pinned))
            throw new ArgumentException("The Dia model is not one the dia role provisions.", nameof(model));
        return new F5RelayWorker(endpoint, GatewayInferenceRoute.ReferenceSpeechRelay(SpeechEngines.Dia,
            SpeechEngines.VoiceDestination, DefaultWorkerId, model, pinned.Revision, pinned.Sha256), handler);
    }
}
