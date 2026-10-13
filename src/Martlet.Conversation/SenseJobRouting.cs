using Martlet.Core.Settings;
using Martlet.Providers;

namespace Martlet.Conversation;

/// <summary>A job that sends a picture or a recording to a model. <see cref="SenseJobRouting"/> says which model takes each one.</summary>
public enum SenseJobKind
{
    /// <summary>The picture with a reply: a look, or the newest picture while Martlet watches.</summary>
    ReplyPicture,
    /// <summary>A screen summary: a sheet of the last pictures that changed (Companion › Vision › Screen summary over time).</summary>
    ScreenSummary,
    /// <summary>A helper job with a picture: finding the touch zones, measuring the character's eyes.</summary>
    HelperPicture,
    /// <summary>A check-in with a screenshot.</summary>
    CheckInScreenshot,
    /// <summary>Your voice with a reply (Companion › Hearing › Hear how you say it).</summary>
    ReplyVoice,
    /// <summary>A description of a sound this PC plays (Companion › Listening › Describe PC sounds).</summary>
    PcSound,
    /// <summary>A check-in with a recording.</summary>
    CheckInRecording
}

/// <summary>Who can take a job with a picture or a recording (one step of <see cref="SenseDecision"/>).</summary>
public enum SenseTaker
{
    /// <summary>The image model or the audio model: the first model in Companion › Vision's or Hearing's list that takes the kind
    /// (<see cref="SenseRouting"/>, path <see cref="SensePath.Described"/>).</summary>
    SenseModel,
    /// <summary>A Thinking pool member whose model sees or hears (<see cref="ThinkingJobBoard"/>); never the conversation's own
    /// route.</summary>
    PoolMember,
    /// <summary>Thinking's own model, in a request of its own.</summary>
    ThinkingModel,
    /// <summary>The sound tagger on this PC's processor: no model.</summary>
    ThisPc,
    /// <summary>No one takes it: the job goes without the picture or recording, waits, or doesn't run (<see cref="SenseJobRow.WhenNobody"/>).</summary>
    Nobody
}

/// <summary>One row of <see cref="SenseJobRouting.Rows"/>: a job, the kind of input it sends, its name in the app's words, the
/// Thinking pool job kind it runs as on a pool member (null: never on the pool), the order of the takers it tries and what
/// happens when no one takes it.</summary>
public sealed record SenseJobRow(SenseJobKind Job, SenseKind Kind, IReadOnlyList<string> Names, ThinkingJobKind? PoolKind,
    IReadOnlyList<SenseTaker> Order, string WhenNobody)
{
    /// <summary>The job in the app's words: "screen summaries", "touch zones and the character's eyes".</summary>
    public string Name => SenseJobRouting.Join(Names);

    /// <summary>What the input needs from a model: <see cref="ThinkingCapability.Vision"/> or <see cref="ThinkingCapability.Audio"/>.</summary>
    public ThinkingCapability Sense => Kind == SenseKind.Image ? ThinkingCapability.Vision : ThinkingCapability.Audio;

    /// <summary>What a Thinking pool member must handle to take this job: text and <see cref="Sense"/>.</summary>
    public ThinkingCapability Needs => ThinkingCapability.Text | Sense;

    /// <summary>Whether a pool member takes it as a quick job (the <see cref="ThinkingJobKind.Digest"/> kind), else a long job.</summary>
    public bool Quick => PoolKind == ThinkingJobKind.Digest;
}

/// <summary>One step of a decision: who, whether it takes the job now (<see cref="Takes"/>), its name (a model, or a pool member
/// as "computer (model)") with its <see cref="Key"/> (a pool member's or list model's key), and why, in plain words.</summary>
public sealed record SenseStep(SenseTaker Taker, bool Takes, string? Name, string Why)
{
    public string? Key { get; init; }
}

/// <summary>Which model takes one job now, as an ordered list of steps (<see cref="SenseJobRow.Order"/>), each with whether it takes
/// the job and why. <see cref="Taken"/> is the first step that takes it; a runner tries it first and goes on to the next step that
/// takes it (<see cref="After"/>) when it can't take the job after all. Pages and MCP show this record; they don't decide again.</summary>
public sealed record SenseDecision(SenseJobRow Row, IReadOnlyList<SenseStep> Steps)
{
    public SenseJobKind Job => Row.Job;

    /// <summary>The first step that takes the job (the last step, <see cref="SenseTaker.Nobody"/>, always does).</summary>
    public SenseStep Taken => Steps.First(s => s.Takes);

    public SenseTaker Taker => Taken.Taker;
    public string? Name => Taken.Name;
    public string Why => Taken.Why;

    /// <summary>The next step that takes the job after <paramref name="taker"/>'s step, other than no one; null when none.</summary>
    public SenseStep? After(SenseTaker taker) =>
        Steps.SkipWhile(s => s.Taker != taker).Skip(1).FirstOrDefault(s => s.Takes && s.Taker != SenseTaker.Nobody);

    /// <summary>The step that takes the job when the taken one can't (<see cref="After"/> the taken step), or null.</summary>
    public SenseStep? Then => After(Taker);

    public override string ToString() => $"{nameof(SenseDecision)} {Job}: {Taker} ({Name})";
}

/// <summary>What <see cref="SenseJobRouting.Decide"/> reads about the setup now. The desktop makes it twice from the same rules:
/// from the running conversation (<c>LiveConversationController.SenseDecision</c>) and from the saved settings for a page before a
/// conversation runs.</summary>
public sealed record SenseJobInputs
{
    /// <summary>Where the kind goes for a reply now (<see cref="SenseRouting.For(SenseKind, SenseModels?, SetupRoute?, ModelAbilities?)"/>):
    /// its image or audio model on <see cref="SensePath.Described"/>, Thinking's own request on <see cref="SensePath.Thinking"/>.</summary>
    public required SenseRoute Route { get; init; }

    /// <summary>Whether Thinking's own model takes the kind in a request of its own (<see cref="ModelSenses.Takes(SenseKind, SenseUse)"/>,
    /// <see cref="SenseUse.Reply"/>): what a helper job with a picture falls back on.</summary>
    public bool ThinkingTakes { get; init; }

    /// <summary>Thinking's model ID; null before Thinking is set up.</summary>
    public string? Thinking { get; init; }

    /// <summary>The Thinking pool member that takes the job's pool kind now (<see cref="SenseJobRow.PoolKind"/> with
    /// <see cref="SenseJobRow.Needs"/>), as "computer (model)" (<see cref="Member"/>), and its key; null: none takes it.</summary>
    public string? PoolName { get; init; }
    public string? PoolKey { get; init; }

    /// <summary>Whether the sound tagger is in Martlet's folder on this PC (<see cref="SenseJobKind.PcSound"/>'s last step).</summary>
    public bool ThisPc { get; init; }

    /// <summary>Whether Hear how you say it lets the model hear your voice (<see cref="SenseJobKind.ReplyVoice"/>); the pages pass the
    /// owner's choice, and the conversation decides it for each message.</summary>
    public bool VoiceAllowed { get; init; } = true;

    /// <summary>The inputs for <paramref name="kind"/> from the saved choices (no pool member, no sound tagger: add them).</summary>
    public static SenseJobInputs For(SenseKind kind, SenseModels? senses, SetupRoute? thinking, ModelAbilities? abilities)
    {
        var own = ModelSenses.For(thinking, abilities);
        return For(SenseRouting.For(kind, senses, thinking, abilities), thinking, own.Known(kind));
    }

    /// <summary>The inputs for <paramref name="route"/>'s kind, with what is known about Thinking's own model for that kind
    /// (<paramref name="thinkingKnown"/>: true, false or null when Martlet can't tell).</summary>
    public static SenseJobInputs For(SenseRoute route, SetupRoute? thinking, bool? thinkingKnown)
    {
        ArgumentNullException.ThrowIfNull(route);
        return new()
        {
            Route = route, Thinking = thinking?.ModelId,
            ThinkingTakes = thinking is not null && ModelSenses.Takes(route.Kind, thinkingKnown, SenseUse.Reply)
        };
    }

    /// <summary>A pool member's name in decisions: "computer (model)", as the job board names its places.</summary>
    public static string Member(string computer, string? model) => model is { Length: > 0 } ? $"{computer} ({model})" : computer;
}

/// <summary>Which model takes each job with a picture or a recording (docs/SENSE_MODELS.md, Who takes each picture and recording).
/// This is the one owner: <see cref="Rows"/> is the one table of the orders, and <see cref="Decide"/> the one decision, with a
/// why for each step. The runners (the screen summary, the PC sound description, helper jobs with a picture) follow the
/// decision; Companion › Vision, Hearing, Listening, Touch and Eyes and MCP's sense_models_status show it.
/// <list type="table">
/// <listheader><term>Job</term><description>Order</description></listheader>
/// <item><term>The picture with a reply</term><description>The image model, else Thinking's own model in the reply's request, else no
/// picture (<see cref="SenseRouting.For(SenseKind, SenseModels?, SetupRoute?, ModelAbilities?)"/> decides the reply's path).</description></item>
/// <item><term>A screen summary</term><description>The image model, else a Thinking pool member that sees (Digest), else none. Never
/// Thinking's own model: its prompt cache stays the conversation's.</description></item>
/// <item><term>Touch zones and the eyes</term><description>A Thinking pool member that sees (TouchZones), else the image model, else
/// Thinking's own model after the reply. The pool goes first: these jobs are long and the image model serves the conversation.</description></item>
/// <item><term>A check-in with a screenshot</term><description>A Thinking pool member that sees (CheckIn), else it waits.</description></item>
/// <item><term>Your voice with a reply</term><description>The audio model, else Thinking's own model, each only when Hear how you say
/// it allows it; else the transcript only.</description></item>
/// <item><term>A PC sound description</term><description>The audio model, else a Thinking pool member that hears (Digest), else the
/// sound tagger on this PC's processor.</description></item>
/// <item><term>A check-in with a recording</term><description>A Thinking pool member that hears (CheckIn), else it waits.</description></item>
/// </list></summary>
public static class SenseJobRouting
{
    private const SenseTaker Model = SenseTaker.SenseModel, Pool = SenseTaker.PoolMember, Thinking = SenseTaker.ThinkingModel,
        Processor = SenseTaker.ThisPc, Nobody = SenseTaker.Nobody;

    /// <summary>The one table of the orders, one row for each <see cref="SenseJobKind"/>.</summary>
    public static IReadOnlyList<SenseJobRow> Rows { get; } =
    [
        new(SenseJobKind.ReplyPicture, SenseKind.Image, ["the picture with a reply"], null, [Model, Thinking, Nobody],
            "the reply goes without a picture"),
        new(SenseJobKind.ScreenSummary, SenseKind.Image, ["screen summaries"], ThinkingJobKind.Digest, [Model, Pool, Nobody],
            "no model makes screen summaries now"),
        new(SenseJobKind.HelperPicture, SenseKind.Image, ["touch zones", "the character's eyes"], ThinkingJobKind.TouchZones,
            [Pool, Model, Thinking, Nobody], "no model can see the pictures"),
        new(SenseJobKind.CheckInScreenshot, SenseKind.Image, ["check-ins with a screenshot"], ThinkingJobKind.CheckIn, [Pool, Nobody],
            "they wait for a Thinking pool member that sees"),
        new(SenseJobKind.ReplyVoice, SenseKind.Audio, ["your voice with a reply"], null, [Model, Thinking, Nobody],
            "Thinking gets the transcript only"),
        new(SenseJobKind.PcSound, SenseKind.Audio, ["descriptions of PC sounds"], ThinkingJobKind.Digest, [Model, Pool, Processor, Nobody],
            "only the words this PC plays reach Thinking"),
        new(SenseJobKind.CheckInRecording, SenseKind.Audio, ["check-ins with a recording"], ThinkingJobKind.CheckIn, [Pool, Nobody],
            "they wait for a Thinking pool member that hears")
    ];

    public static SenseJobRow Row(SenseJobKind job) => Rows.First(r => r.Job == job);

    /// <summary>The jobs that send <paramref name="kind"/>, in the table's order.</summary>
    public static IReadOnlyList<SenseJobRow> Of(SenseKind kind) => [.. Rows.Where(r => r.Kind == kind)];

    /// <summary>Which model takes <paramref name="job"/> now: each step of its row's order, whether it takes the job and why.</summary>
    public static SenseDecision Decide(SenseJobKind job, SenseJobInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        var row = Row(job);
        if (inputs.Route.Kind != row.Kind) throw new ArgumentException($"The inputs are for {inputs.Route.Kind}, not {row.Kind}.", nameof(inputs));
        return new(row, [.. row.Order.Select(taker => Step(row, taker, inputs))]);
    }

    private static SenseStep Step(SenseJobRow row, SenseTaker taker, SenseJobInputs inputs)
    {
        var route = inputs.Route;
        var image = row.Kind == SenseKind.Image;
        var reply = row.Job is SenseJobKind.ReplyPicture or SenseJobKind.ReplyVoice;
        var voice = row.Job != SenseJobKind.ReplyVoice || inputs.VoiceAllowed;
        var sense = image ? "image" : "audio";
        var does = image ? "sees" : "hears";
        switch (taker)
        {
            case Model:
                if (route is { Described: true, Model: { } own } && voice)
                    return new(taker, true, own.Describe(), reply ? route.Why : $"Your {sense} model, {own.Describe()}, takes {row.Name}.") { Key = own.Key };
                return new(taker, false, route.Model?.Describe(),
                    route.Described ? $"Hear how you say it is off for your audio model, {route.Name}."
                    : route.Model is not null ? route.Why
                    : $"No {sense} model is in Companion › {(image ? "Vision" : "Hearing")}'s list.") { Key = route.Model?.Key };
            case Pool:
                return inputs.PoolName is { } member
                    ? new(taker, true, member, $"{member} in the Thinking pool takes {row.Name}.") { Key = inputs.PoolKey }
                    : new(taker, false, null, $"No Thinking pool member that {does} takes {row.Name} now.");
            case Thinking:
                var takes = reply ? route.Path == SensePath.Thinking && voice : inputs.ThinkingTakes;
                var name = inputs.Thinking;
                if (takes)
                    return new(taker, true, name, reply ? route.Why : $"Thinking's own model ({name}) takes {row.Name} after the reply.");
                return new(taker, false, name,
                    reply && route.Described ? $"Your {sense} model takes them, so Thinking gets its words."
                    : reply && route.Path == SensePath.Thinking ? "Hear how you say it is off for Thinking."
                    : reply ? route.Why
                    : name is null ? "Thinking isn't set up yet."
                    : $"Thinking's own model ({name}) doesn't {(image ? "see pictures" : "hear recordings")}.");
            case Processor:
                return inputs.ThisPc
                    ? new(taker, true, "the sound tagger on this PC", "The sound tagger on this PC's processor names what it hears.")
                    : new(taker, false, null, "The sound tagger isn't in Martlet's folder on this PC.");
            default:
                // A reply without the picture or recording says why, as the reply's route does.
                return new(taker, true, null, reply && route.Path == SensePath.None && voice ? route.Why : Capitalized(row.WhenNobody) + ".");
        }
    }

    /// <summary>Who takes each of <paramref name="decisions"/>' jobs, in a few words for the list summary on Companion › Vision or
    /// Hearing while nothing in the list is on: "Thinking's own model (gemma4:e2b) takes the picture with a reply; diva (qwen2.5vl:7b)
    /// in the Thinking pool takes screen summaries, touch zones and the character's eyes; check-ins with a screenshot: they wait for
    /// a Thinking pool member that sees".</summary>
    public static string Instead(IEnumerable<SenseDecision> decisions)
    {
        ArgumentNullException.ThrowIfNull(decisions);
        var parts = new List<string>();
        foreach (var group in decisions.GroupBy(d => (d.Taker, d.Name)))
        {
            var jobs = Join(group.SelectMany(d => d.Row.Names));
            parts.Add(group.Key.Taker switch
            {
                Model => $"{group.Key.Name} takes {jobs}",
                Pool => $"{group.Key.Name} in the Thinking pool takes {jobs}",
                Thinking => $"Thinking's own model ({group.Key.Name}) takes {jobs}",
                Processor => $"the sound tagger on this PC takes {jobs}",
                _ => string.Join("; ", group.Select(d => $"{d.Row.Name}: {d.Row.WhenNobody}"))
            });
        }
        return string.Join("; ", parts);
    }

    /// <summary>"a", "a and b", "a, b and c".</summary>
    public static string Join(IEnumerable<string> items)
    {
        var all = items.Distinct(StringComparer.Ordinal).ToArray();
        return all.Length <= 1 ? all.FirstOrDefault() ?? "" : $"{string.Join(", ", all[..^1])} and {all[^1]}";
    }

    private static string Capitalized(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
}
