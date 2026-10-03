using Martlet.Core.Settings;
using Martlet.Gateway.F5;

namespace Martlet.Gateway.GptSovits;

/// <summary>
/// Gateway relay from a paired client to this host's own loopback GPT-SoVITS voice service (the <c>gpt-sovits</c> host
/// role: workers/gpt-sovits, GPT-SoVITS 20250606v2pro). The service speaks the f5 role's protocol (/synthesize, /cancel and
/// the <c>martlet.f5.worker</c> event stream of contiguous 24 kHz mono PCM16 frames), so the F5 relay serves it on
/// GPT-SoVITS's own route. The route names the SoVITS v2Pro weights; the relay also requires the worker to report the
/// pinned GPT weights, so both halves of the pair are verified. Enabling the role is the host owner's standing permission for
/// paired <c>voice</c> devices to send reply text and the 3-10 second reference recording they chose to it.
/// </summary>
public static class GptSovitsRelay
{
    public const string DefaultWorkerId = "gpt-sovits-relay";
    public const string DefaultModel = "gpt-sovits-v2pro";
    private const string WeightsRevision = "336b2ec4e8d4ac74740798dd40af44e74659ecaf";

    /// <summary>The pair the gpt-sovits role provisions (workers/gpt-sovits/martlet_gpt_sovits/pins.py) from Hugging Face
    /// lj1995/GPT-SoVITS: the SoVITS v2Pro weights (the route's model) and the GPT weights.</summary>
    public static readonly IReadOnlyDictionary<string, (string Revision, string Sha256, string GptArtifactId, string GptSha256)>
        PinnedModels = new Dictionary<string, (string, string, string, string)>(StringComparer.Ordinal)
        {
            [DefaultModel] = (WeightsRevision, "0f8ead815234365edf045c6d86370ed6e4f440e8195be77ff0ea72684ad406a5",
                "gpt-sovits-s1v3", "87133414860ea14ff6620c483a3db5ed07b44be42e2c3fcdad65523a729a745a")
        };

    public static F5RelayWorker Create(Uri endpoint, string model, HttpMessageHandler? handler = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (!PinnedModels.TryGetValue(model, out var pinned))
            throw new ArgumentException("The GPT-SoVITS model is not one the gpt-sovits role provisions.", nameof(model));
        return new F5RelayWorker(endpoint, GatewayInferenceRoute.ReferenceSpeechRelay(SpeechEngines.GptSovits,
                SpeechEngines.VoiceDestination, DefaultWorkerId, model, pinned.Revision, pinned.Sha256), handler,
            [("gpt_weights", pinned.GptArtifactId, pinned.Revision, pinned.GptSha256)]);
    }
}
