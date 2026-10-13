using Martlet.Core.Settings;
using Martlet.Providers;

namespace Martlet.Conversation;

/// <summary>How one kind of input reaches the reply (docs/SENSE_MODELS.md).</summary>
public enum SensePath
{
    /// <summary>The text model (Thinking) takes the pictures or recordings in its own request: an omni model, Martlet's default.</summary>
    Thinking,
    /// <summary>The image or audio model puts what it sees or hears into words, and the words go to Thinking.</summary>
    Described,
    /// <summary>No model takes it: pictures aren't sent, and recordings go as the transcript only.</summary>
    None
}

/// <summary>Where one kind of input goes now and why, in words for Companion, the desktop log and MCP. <see cref="Model"/> is the
/// model of its own on <see cref="SensePath.Described"/>, else null. <see cref="Unknown"/>: Martlet can't tell whether the model
/// that takes it sees or hears; it tries, and a refusal is remembered (model-abilities.json).</summary>
public sealed record SenseRoute(SenseKind Kind, SensePath Path, DeepThinkingSettings? Model, string Why)
{
    public bool Unknown { get; init; }

    /// <summary>Whether a model of its own puts this kind into words for Thinking.</summary>
    public bool Described => Path == SensePath.Described;

    /// <summary>The model that takes it, in words: "Ollama on this PC (qwen2.5vl:7b)" or "the text model (Thinking)".</summary>
    public string Name => Model?.Describe() ?? "the text model (Thinking)";

    public override string ToString() => $"{nameof(SenseRoute)} {Kind}: {Path} ({Name})";
}

/// <summary>Which model takes pictures and recordings (docs/SENSE_MODELS.md). The text model (Thinking) always writes the reply.
/// A kind whose model is the text model (the default, or a model of its own that is exactly Thinking's endpoint and model) goes
/// in Thinking's own request when Thinking takes it, else nowhere. A kind with a model of its own goes to that model to be put
/// into words, when it takes it (an endpoint, or a paired computer's model through its gateway). Whether a model sees or hears,
/// and what happens when Martlet can't tell, is <see cref="ModelSenses"/>' one rule.</summary>
public static class SenseRouting
{
    /// <summary>Where <paramref name="kind"/> goes, with whether the Thinking model itself sees and hears as the desktop decides
    /// it (<paramref name="thinkingSees"/>, <paramref name="thinkingHears"/>: what its server said, a test, a refusal, its name).</summary>
    public static SenseRoute For(SenseKind kind, SenseModels? senses, SetupRoute? thinking, ModelAbilities? abilities,
        VisionSupport thinkingSees, HearingSupport thinkingHears)
    {
        senses ??= new();
        var place = senses.Place(kind);
        var image = kind == SenseKind.Image;
        if (place is null || IsThinking(place, thinking))
        {
            if (thinking is null) return new(kind, SensePath.None, null, "Set up Thinking first.");
            var other = senses.For(kind).Source == SenseSource.OtherSense ? $"the {(image ? "audio" : "image")} model, which is " : "";
            if (image)
                return !ModelSenses.Takes(kind, ModelSenses.Known(thinkingSees), SenseUse.Reply)
                    ? new(kind, SensePath.None, null,
                        $"Pictures go to {other}the text model (Thinking), which is text-only, so Martlet can't see. Choose an image model, or a Thinking model that sees.")
                    : thinkingSees == VisionSupport.Unknown ? new(kind, SensePath.Thinking, null,
                        $"Pictures go to {other}the text model (Thinking). Martlet can't tell whether it sees them; it tries, and Test vision finds out.") { Unknown = true }
                    : new(kind, SensePath.Thinking, null, $"Pictures go to {other}the text model (Thinking), which sees them itself.");
            return ModelSenses.Takes(kind, ModelSenses.Known(thinkingHears), SenseUse.Reply)
                ? new(kind, SensePath.Thinking, null, $"Recordings go to {other}the text model (Thinking), which hears them itself.")
                : new(kind, SensePath.None, null, thinkingHears == HearingSupport.Unknown
                    ? $"Recordings go to {other}the text model (Thinking). Martlet can't tell whether it hears, so it gets the transcript only; Test hearing finds out."
                    : $"Recordings go to {other}the text model (Thinking), which can't hear, so it gets the transcript only. Choose an audio model to describe your voice and the sounds this PC plays.");
        }
        var name = place.Describe();
        var own = ModelSenses.For(place, abilities);
        var unknown = own.Known(kind) is null;
        if (image)
            return !own.Takes(kind, SenseUse.List)
                ? new(kind, SensePath.None, place, $"{name} doesn't see pictures, so Martlet can't see. Choose an image model that sees.")
                : unknown ? new(kind, SensePath.Described, place,
                    $"{name} describes pictures in words for the text model (Thinking). Martlet can't tell whether it sees; it tries, and Test vision finds out.") { Unknown = true }
                : new(kind, SensePath.Described, place, $"{name} describes pictures in words for the text model (Thinking).");
        return !own.Takes(kind, SenseUse.List)
            ? new(kind, SensePath.None, place, $"{name} can't hear recordings, so Thinking gets the transcript only. Choose an audio model that hears.")
            : unknown ? new(kind, SensePath.Described, place,
                $"{name} describes recordings in words for the text model (Thinking). Martlet can't tell whether it hears; it tries, and Test hearing finds out.") { Unknown = true }
            : new(kind, SensePath.Described, place, $"{name} describes recordings in words for the text model (Thinking).");
    }

    /// <summary>As the other overload, with the Thinking model's own vision and hearing from <see cref="ModelSenses"/> (what MCP
    /// and Companion use without a running conversation).</summary>
    public static SenseRoute For(SenseKind kind, SenseModels? senses, SetupRoute? thinking, ModelAbilities? abilities)
    {
        var own = ModelSenses.For(thinking, abilities);
        return For(kind, senses, thinking, abilities, own.Sees, own.Hears);
    }

    /// <summary>Whether a model of its own is the text model itself: exactly Thinking's endpoint and model, or the same paired
    /// computer's Ollama with the same model as a Thinking route on that computer.</summary>
    public static bool IsThinking(DeepThinkingSettings model, SetupRoute? thinking)
    {
        ArgumentNullException.ThrowIfNull(model);
        return model.SameAs(thinking) ||
            model is { Place: DeepThinkingPlace.Host, HostId: { } host, ModelId: { } id } && model.HostRoute == SelfHostSetup.OllamaRouteId &&
            thinking is { RouteType: SetupRouteType.GatewayOllama, Gateway.HostId: var thinkingHost } &&
            string.Equals(thinkingHost, host, StringComparison.Ordinal) && string.Equals(thinking.ModelId, id, StringComparison.Ordinal);
    }
}
