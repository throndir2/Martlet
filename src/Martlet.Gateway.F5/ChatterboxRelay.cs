using Martlet.Core.Settings;

namespace Martlet.Gateway.F5;

/// <summary>
/// Gateway relay from a paired client to this host's own loopback Chatterbox voice service (the <c>chatterbox</c>,
/// <c>chatterbox-original</c> and <c>chatterbox-nano</c> host roles, each running workers/chatterbox/martlet_chatterbox_host.py
/// with its own model). The service speaks the f5 role's protocol (/synthesize, /cancel and the <c>martlet.f5.worker</c> event
/// stream of contiguous 24 kHz mono PCM16 frames), so the F5 relay serves it on the model's own route
/// (<see cref="SpeechEngines.Chatterbox"/>, <see cref="SpeechEngines.ChatterboxOriginal"/> or
/// <see cref="SpeechEngines.ChatterboxNano"/>). Reply text may carry the engine's own tags ([laugh], [expressive]...).
/// Enabling a role is the host owner's standing permission for paired <c>voice</c> devices to send reply text and the
/// reference recording they chose to it.
/// </summary>
public static class ChatterboxRelay
{
    public const string DefaultWorkerId = "chatterbox-relay";
    public const string DefaultModel = "chatterbox-turbo";

    /// <summary>The models the Chatterbox roles provision (martlet_chatterbox_host.py PINNED_MODELS): each model's Hugging Face
    /// revision and its T3 weights' SHA-256 (t3_turbo_v1, t3_cfg and t3_nano_v1). Their decoder, voice encoder and tokenizer
    /// files are pinned with them.</summary>
    public static readonly IReadOnlyDictionary<string, (string Revision, string Sha256)> PinnedModels =
        new Dictionary<string, (string, string)>(StringComparer.Ordinal)
        {
            [DefaultModel] = ("749d1c1a46eb10492095d68fbcf55691ccf137cd",
                "fcf1f8c1d651bb7e3acd69ee5be269b4ac10c02980b7708213d598bc9f7cdf87"),
            [SpeechEngines.ChatterboxOriginal.DefaultModel] = ("5bb1f6ee58e50c3b8d408bc82a6d3740c2db6e18",
                "914cb1696f47527fe8852ca8f1fe1fa63cb34f76f9c715e84e067b744dd0da81"),
            [SpeechEngines.ChatterboxNano.DefaultModel] = ("71ccd1d0081b430592cea481f4307e764e07bc64",
                "72b110185087d945dbdf54dee4e333848e1811bdd5fd6cb16ceb8da50006f0c9")
        };

    public static F5RelayWorker Create(Uri endpoint, string model, HttpMessageHandler? handler = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (!PinnedModels.TryGetValue(model, out var pinned) || SpeechEngines.ForModel(model) is not { } engine)
            throw new ArgumentException("The Chatterbox model is not one the Chatterbox roles provision.", nameof(model));
        return new F5RelayWorker(endpoint, GatewayInferenceRoute.ReferenceSpeechRelay(engine,
            SpeechEngines.VoiceDestination, DefaultWorkerId, model, pinned.Revision, pinned.Sha256), handler);
    }
}
