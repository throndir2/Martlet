using Martlet.Core.Settings;
using Martlet.Gateway.F5;

namespace Martlet.Gateway.Xtts;

/// <summary>
/// Gateway relay from a paired client to this host's own loopback XTTS-v2 voice service (the <c>xtts</c> host role:
/// workers/xtts/host/martlet_xtts_host.py). The service speaks the f5 role's protocol (/synthesize, /cancel and the
/// <c>martlet.f5.worker</c> event stream of contiguous 24 kHz mono PCM16 frames), so the F5 relay serves it on XTTS's own
/// route. XTTS streams frames while it is still generating a sentence; enabling the role is the host owner's standing
/// permission for paired <c>voice</c> devices to send reply text and the reference recording they chose to it.
/// </summary>
public static class XttsRelay
{
    public const string DefaultWorkerId = "xtts-relay";
    public const string DefaultModel = "xtts-v2";

    /// <summary>The checkpoint the xtts role provisions (workers/xtts/host/martlet_xtts_host.py PINNED_MODELS): Hugging Face
    /// coqui/XTTS-v2 revision and model.pth SHA-256. Its config, tokenizer and speaker files are pinned with it.</summary>
    public static readonly IReadOnlyDictionary<string, (string Revision, string Sha256)> PinnedModels =
        new Dictionary<string, (string, string)>(StringComparer.Ordinal)
        {
            [DefaultModel] = ("6c2b0d75eae4b7047358e3b6bd9325f857d43f77",
                "c7ea20001c6a0a841c77e252d8409f6a74fb423e79b3206a0771ba5989776187")
        };

    public static F5RelayWorker Create(Uri endpoint, string model, HttpMessageHandler? handler = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (!PinnedModels.TryGetValue(model, out var pinned))
            throw new ArgumentException("The XTTS model is not one the xtts role provisions.", nameof(model));
        return new F5RelayWorker(endpoint, GatewayInferenceRoute.ReferenceSpeechRelay(SpeechEngines.Xtts,
            SpeechEngines.VoiceDestination, DefaultWorkerId, model, pinned.Revision, pinned.Sha256), handler);
    }
}
