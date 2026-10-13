using Martlet.Core.Settings;
using Martlet.Providers;

namespace Martlet.Conversation;

/// <summary>Who sends a model a picture or a recording. It decides what Martlet does when it can't tell whether the model sees
/// or hears (<see cref="ModelSenses.Takes(SenseKind, SenseUse)"/>).</summary>
public enum SenseUse
{
    /// <summary>A model in Companion › Vision's or Hearing's list: the owner chose it for that sense, and a refusal there is
    /// remembered.</summary>
    List,
    /// <summary>Thinking's own reply request (the Vision or Hearing list is empty, or its first model is Thinking's own).</summary>
    Reply,
    /// <summary>A Thinking pool job: a screen summary, a sound description, touch zones, the eyes or a check-in.</summary>
    Pool
}

/// <summary>What one model takes besides text: pictures (<see cref="Sees"/>), recordings (<see cref="Hears"/>) and tool calls
/// (<see cref="Tools"/>). This is the one rule for every place a model runs: the Thinking route, a model in the Vision or Hearing
/// list, a Thinking pool member and a model in Ollama on this PC (docs/SENSE_MODELS.md, What a model takes). Each fact comes
/// from the first of these that says:
/// <list type="number">
/// <item>The route can't carry it: recordings only go over Chat Completions and a paired computer's gateway, tool calls only
/// over Chat Completions and OpenAI's Responses route.</item>
/// <item>The model is retired: its endpoint answered HTTP 410, or it is in Martlet's built-in list for a named endpoint.</item>
/// <item>What Martlet found out about that model on that server (model-abilities.json): Test vision or Test hearing, a refused
/// picture or recording, then the server's metadata. A paired computer's model is found by its gateway's origin, the same for a
/// Thinking route there and for a Vision list, Hearing list or Thinking pool member there.</item>
/// <item>The model's name (<see cref="VisionModelCatalog"/>, <see cref="HearingModelCatalog"/>).</item>
/// </list>
/// Otherwise it is Unknown, and <see cref="Takes(SenseKind, bool?, SenseUse)"/> decides from who sends it.</summary>
public sealed record ModelSenses(VisionSupport Sees, HearingSupport Hears, ToolSupport Tools)
{
    /// <summary>Nothing known: no Thinking route yet.</summary>
    public static ModelSenses Unknown { get; } = new(VisionSupport.Unknown, HearingSupport.Unknown, ToolSupport.Unknown);

    /// <summary>What the model on a route takes: <paramref name="routeType"/> decides what the route carries, and
    /// <paramref name="origin"/> and <paramref name="modelId"/> find what Martlet found out about it.</summary>
    public static ModelSenses For(SetupRouteType? routeType, string? origin, string? modelId, ModelAbilities? abilities)
    {
        var retired = Retired(routeType, origin, modelId, abilities) is not null;
        return new(VisionModelCatalog.ForRoute(origin, modelId, abilities, retired),
            HearingModelCatalog.ForRoute(routeType, origin, modelId, abilities, retired),
            RouteAbilities.Tools(routeType, origin, modelId, abilities, retired));
    }

    /// <summary>What the Thinking route's model takes; <see cref="Unknown"/> before Thinking is set up.</summary>
    public static ModelSenses For(SetupRoute? thinking, ModelAbilities? abilities) =>
        thinking is null ? Unknown : For(thinking.RouteType, thinking.Origin, thinking.ModelId, abilities);

    /// <summary>What a model of its own takes (a Vision or Hearing list member, a Thinking pool member): an endpoint over Chat
    /// Completions by its base URL, a paired computer's model through its gateway by the gateway's origin. Same as Thinking
    /// sees as Unknown and takes no recordings or tool calls of its own.</summary>
    public static ModelSenses For(DeepThinkingSettings model, ModelAbilities? abilities)
    {
        ArgumentNullException.ThrowIfNull(model);
        return model.Place switch
        {
            DeepThinkingPlace.Endpoint => For(SetupRouteType.ChatCompletions, model.Origin, model.ModelId, abilities),
            DeepThinkingPlace.Host => For(SetupRouteType.GatewayOllama, model.HostOrigin, model.ModelId, abilities),
            _ => new(VisionSupport.Unknown, HearingSupport.Unsupported, ToolSupport.Unsupported)
        };
    }

    /// <summary>The route's model when its server retired it: found on that route (an HTTP 410 Gone answer) or in Martlet's
    /// built-in list. Null otherwise, and for a route that isn't a Chat Completions endpoint.</summary>
    public static RetiredModel? Retired(SetupRouteType? routeType, string? origin, string? modelId, ModelAbilities? abilities) =>
        routeType == SetupRouteType.ChatCompletions ? ChatCompletionsEndpointCatalog.RetiredOn(origin, modelId, abilities) : null;

    /// <summary>As the other overload, for the Thinking route.</summary>
    public static RetiredModel? Retired(SetupRoute? thinking, ModelAbilities? abilities) =>
        thinking is null ? null : Retired(thinking.RouteType, thinking.Origin, thinking.ModelId, abilities);

    /// <summary>Whether it is known to take <paramref name="kind"/>: true, false, or null when Martlet can't tell.</summary>
    public bool? Known(SenseKind kind) => kind == SenseKind.Image ? Known(Sees) : Known(Hears);

    /// <summary>Whether a job of <paramref name="use"/> sends it <paramref name="kind"/> (<see cref="Takes(SenseKind, bool?, SenseUse)"/>).</summary>
    public bool Takes(SenseKind kind, SenseUse use) => Takes(kind, Known(kind), use);

    /// <summary>Whether tool calls are offered to it: unless it is known not to call them. A model that rejects them is asked again
    /// without them.</summary>
    public bool CallsTools => Tools != ToolSupport.Unsupported;

    /// <summary>The one rule for a model Martlet can't tell about (<paramref name="known"/> null). A known answer always decides.
    /// <list type="table">
    /// <listheader><term>Who sends it</term><description>When Martlet can't tell</description></listheader>
    /// <item><term><see cref="SenseUse.List"/></term><description>It is tried: the owner chose it for that sense, and a refusal is
    /// remembered.</description></item>
    /// <item><term><see cref="SenseUse.Reply"/></term><description>A picture is tried (a refused picture is asked again with the
    /// words only, and Test vision finds out). A recording is not: a refused recording asks again and delays Martlet's first word,
    /// so Thinking gets the transcript until Test hearing finds out.</description></item>
    /// <item><term><see cref="SenseUse.Pool"/></term><description>It gets none: pool members are chosen for text work, and the pool
    /// doesn't remember a refusal, so it would fail each job.</description></item>
    /// </list></summary>
    public static bool Takes(SenseKind kind, bool? known, SenseUse use) => known ?? use switch
    {
        SenseUse.List => true,
        SenseUse.Reply => kind == SenseKind.Image,
        _ => false
    };

    public static bool? Known(VisionSupport sees) => sees switch { VisionSupport.Supported => true, VisionSupport.Unsupported => false, _ => null };

    public static bool? Known(HearingSupport hears) => hears switch { HearingSupport.Supported => true, HearingSupport.Unsupported => false, _ => null };
}
