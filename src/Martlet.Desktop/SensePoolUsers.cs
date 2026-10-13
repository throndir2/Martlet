using Martlet.Conversation;
using Martlet.Core.Settings;
using Martlet.Providers;

namespace Martlet.Desktop;

/// <summary>The Thinking pool members that get pictures (or recordings) besides the models in Companion › Vision's (or
/// Companion › Hearing's) list. A member whose model sees (or hears) takes screen summaries (or PC sound descriptions) as a quick
/// job, and touch zones, the character's eyes and check-ins with a screenshot (or check-ins with a recording) as long jobs. Both
/// pages show these members under their list (<c>Pool-vision-Also-&lt;n&gt;</c>, <c>Pool-hearing-Also-&lt;n&gt;</c>), so one page
/// names every model that gets your pictures or recordings, cloud providers and servers included. What a member can do comes from
/// <see cref="ThinkingPoolCapabilities"/> and what it may take from the pool's settings, with the same rules as the job board
/// (<see cref="ThinkingJobBoard"/>), so the pages say the same thing whether a conversation runs or not.</summary>
internal static class SensePoolUsers
{
    /// <summary>One member whose model sees (or hears): whether it is on, takes quick and long jobs, may receive pictures and
    /// recordings, and whether Runs on lets it take summaries.</summary>
    internal sealed record User(DeepThinkingSettings Member, bool On, bool QuickJobs, bool LongJobs, bool MayReceive, bool SummariesAllowed)
    {
        public string Name => Member.Describe();
        /// <summary>An endpoint that is not on this PC: a cloud provider or a server on your network.</summary>
        public bool External => ThinkingPoolSettings.IsExternal(Member);
        /// <summary>Whether it takes screen summaries (or PC sound descriptions) now.</summary>
        public bool Summaries => On && MayReceive && QuickJobs && SummariesAllowed;
        /// <summary>Whether it takes long jobs with pictures (or recordings) now.</summary>
        public bool Long => On && MayReceive && LongJobs;
        /// <summary>Whether it gets pictures (or recordings) for any job now.</summary>
        public bool Gets => Summaries || Long;
    }

    internal static ThinkingCapability Needs(SenseKind kind) => kind == SenseKind.Image ? ThinkingCapability.Vision : ThinkingCapability.Audio;

    /// <summary>Every member of <paramref name="pool"/>, on or off, whose model takes <paramref name="kind"/>, in the pool's order.</summary>
    internal static IReadOnlyList<User> For(SenseKind kind, ThinkingPoolSettings pool, ModelAbilities? abilities)
    {
        ArgumentNullException.ThrowIfNull(pool);
        var needs = Needs(kind);
        var digest = pool.RunsOn(ThinkingJobKinds.Name(ThinkingJobKind.Digest));
        return [.. pool.All.Where(m => (ThinkingPoolCapabilities.For(m, abilities) & needs) == needs).Select(m => new User(m,
            !pool.IsOff(m.Key), pool.TakesQuickJobs(m.Key), pool.TakesLongJobs(m.Key), pool.MayReceiveMedia(m),
            digest.Allows(m.Key, pool.SmartsOf(m))))];
    }

    private static string Inputs(SenseKind kind) => kind == SenseKind.Image ? "pictures" : "recordings";
    private static string Takes(SenseKind kind) => kind == SenseKind.Image ? "sees pictures" : "hears recordings";

    /// <summary>"a", "a and b", "a, b and c".</summary>
    internal static string Names(IEnumerable<User> users)
    {
        var names = users.Select(u => u.Name).Distinct(StringComparer.Ordinal).ToArray();
        return names.Length <= 1 ? names.FirstOrDefault() ?? "" : $"{string.Join(", ", names[..^1])} and {names[^1]}";
    }

    /// <summary>What <paramref name="user"/> gets the pictures (or recordings) for. With <paramref name="senseModel"/> (an image or
    /// audio model of its own takes them), summaries go to that model first.</summary>
    internal static string Uses(SenseKind kind, User user, bool senseModel)
    {
        var image = kind == SenseKind.Image;
        var uses = new List<string>();
        if (user.Summaries)
            uses.Add((image ? "screen summaries" : "descriptions of PC sounds") + (senseModel ? $" when your {(image ? "image" : "audio")} model can't take them" : ""));
        if (user.Long) uses.Add(image ? "touch zones, the character's eyes and check-ins with a screenshot" : "check-ins with a recording");
        return string.Join(", and ", uses);
    }

    /// <summary>A member's line under the list (<c>Pool-vision-Also-&lt;n&gt;</c>): where it runs, then what it gets the pictures
    /// (or recordings) for, or why it gets none.</summary>
    internal static string Line(SenseKind kind, User user, bool senseModel)
    {
        var inputs = Inputs(kind);
        var where = user.External ? "A cloud provider or server in the Thinking pool"
            : user.Member.Place == DeepThinkingPlace.Host ? "One of your computers in the Thinking pool"
            : "A model on this PC in the Thinking pool";
        var state = !user.On ? $"It is off there, so it gets no {inputs}."
            : !user.MayReceive ? $"It gets no {inputs}: May receive pictures and recordings is off for it there."
            : !user.Gets ? $"It gets no {inputs}: it takes no quick jobs or long jobs there."
            : $"It gets your {inputs} for {Uses(kind, user, senseModel)}." + (user.External ? " Requests there may cost money." : "");
        return $"{where}. {state}";
    }

    /// <summary>The line above the members (<c>Pool-vision-AlsoSummary</c>): how many Thinking pool models get the pictures (or
    /// recordings) besides the list, or that none does.</summary>
    internal static string Summary(SenseKind kind, IReadOnlyList<User> users)
    {
        var inputs = Inputs(kind);
        var getting = users.Count(u => u.Gets);
        var verb = (kind == SenseKind.Image ? "see" : "hear") + (users.Count == 1 ? "s" : "");
        return users.Count == 0 ? $"No model in the Thinking pool {Takes(kind)}, so only the models above (or Thinking's own) get your {inputs}."
            : getting == 0 ? $"The Thinking pool has {users.Count} model{(users.Count == 1 ? "" : "s")} that {verb}, " +
                $"but none of them gets your {inputs} now."
            : $"Your {inputs} also go to {getting} model{(getting == 1 ? "" : "s")} in the Thinking pool, for background work. They are " +
                "set on Companion › Thinking pool.";
    }

    /// <summary>Companion › Vision's "What Martlet sends" sentence on screen summaries, when Screen summary over time is
    /// <paramref name="on"/> and Thinking pool models take them; empty otherwise.</summary>
    internal static string SummaryDisclosure(bool on, IReadOnlyList<User> users, bool imageModel)
    {
        var to = users.Where(u => u.Summaries).ToArray();
        if (!on || to.Length == 0) return "";
        return $" With Screen summary over time on, a small sheet of the last pictures that changed, with the text read on them, also " +
            $"goes to {(imageModel ? "your image model or, when it can't take it, to " : "")}{Names(to)} in the Thinking pool, about " +
            "once every 15 seconds while the screen changes." + (to.Any(u => u.External) ? " Requests to a cloud provider may cost money." : "");
    }
}
