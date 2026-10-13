namespace Martlet.Conversation;

/// <summary>What a Thinking pool job is for. Each kind has one row in <see cref="ThinkingJobKinds.Rows"/>, which says all the
/// rest: its saved key, its labels, its <see cref="ThinkingPriority"/>, whether it is a fast kind that may take the slot the pool
/// keeps free, what it needs, what the live floor does to it and the helper jobs it covers. <see cref="CheckIn"/> is one of
/// Martlet's check-ins (<see cref="CheckIns"/>): a short question about what lingers, at the helpers' priority. To add a kind,
/// add the value here and its row in <see cref="ThinkingJobKinds.Rows"/>.</summary>
public enum ThinkingJobKind { BargeInJudge, EndOfTurnJudge, Digest, ThinkLonger, TouchZones, Memory, Naming, Research, CheckIn }

/// <summary>Which job a free slot takes first: the highest value first, then the oldest.</summary>
public enum ThinkingPriority
{
    Research = 10,
    Helper = 20,
    TouchZones = 35,
    ThinkLonger = 40,
    Digest = 50,
    EndOfTurnJudge = 60,
    BargeInJudge = 70
}

/// <summary>Background model work Martlet does on its own, besides replies and think_longer (the desktop's HelperJobs runs it).
/// On the Thinking pool, each one is a job of the <see cref="ThinkingJobKind"/> whose row lists it
/// (<see cref="ThinkingJobKinds.Helper"/>).</summary>
public enum HelperJobKind
{
    /// <summary>Remembering after a reply.</summary>
    Memory,
    /// <summary>Naming a character's emotes and motions.</summary>
    ActionNaming,
    /// <summary>Finding a character's touch zones in one picture of it.</summary>
    TouchZones,
    /// <summary>Deciding a character's touch temperament from its personality.</summary>
    Temperament,
    /// <summary>Measuring a character's eyes in one close-up of its face (vision, low priority).</summary>
    Eyes,
    /// <summary>Writing the personality of a character the owner named in Find a character (the owner waits).</summary>
    CharacterWriting,
    /// <summary>Tidying memory: search words for facts, duplicates merged and outdated facts kept as history (Thinking pool only,
    /// low priority).</summary>
    MemoryUpkeep,
    /// <summary>Writing the memory brief from the remembered facts (lowest priority).</summary>
    MemoryBrief,
    /// <summary>Learning names after a reply: who the people speaking are, from their voices and their words (a Naming job,
    /// with the People tools on a member that calls tools when Martlet wasn't sure who spoke).</summary>
    SpeakerNaming
}

/// <summary>What the live floor (<see cref="LiveFloorRules"/>) does to a kind's jobs on a member that shares the conversation's
/// hardware while you talk.</summary>
public enum ThinkingJobWhileLive
{
    /// <summary>Serves the live turn itself (the judges): it always starts.</summary>
    Starts,
    /// <summary>Waits to start; a job that already runs goes on.</summary>
    Waits,
    /// <summary>Waits to start, and a job that runs stops.</summary>
    Stops
}

/// <summary>A helper job kind in its pool kind's row. <paramref name="Key"/> is its name in helper-jobs.json, the log and a sense
/// job's purpose. <paramref name="Priority"/> is its pool priority when it isn't its pool kind's own. <paramref name="OwnerWaits"/>:
/// the owner waits for it, so it goes before the other helpers when the pool is busy.</summary>
public sealed record HelperJobKindRow(HelperJobKind Kind, string Key, ThinkingPriority? Priority = null, bool OwnerWaits = false);

/// <summary>Everything about one <see cref="ThinkingJobKind"/>. <paramref name="Key"/> is its saved key: thinking-pool.json's Runs
/// on choices, status files, logs and automation IDs (ThinkingPoolRunsOn-&lt;key&gt;) use it, so it never changes without a
/// migration. <paramref name="Label"/> names all the work the kind controls (Companion › Thinking pool › Runs on);
/// <paramref name="TypeName"/> is its short name on the Thinking requests page. <paramref name="Fast"/> kinds (the judges and the
/// summaries) may take the last free slot of the pool. <paramref name="Needs"/> is the most its jobs ask of a member: each job asks
/// for text, plus vision with a picture, audio with a recording and tools with tool calls. <paramref name="Helpers"/> are the
/// helper jobs that run as this kind.</summary>
public sealed record ThinkingJobKindRow(ThinkingJobKind Kind, string Key, string Label, string TypeName, ThinkingPriority Priority,
    bool Fast, ThinkingCapability Needs, ThinkingJobWhileLive WhileLive, IReadOnlyList<HelperJobKindRow> Helpers);

/// <summary>The one table of Thinking pool job kinds (<see cref="Rows"/>) and the lookups every other list reads.</summary>
public static class ThinkingJobKinds
{
    private const ThinkingCapability Text = ThinkingCapability.Text;

    /// <summary>One row for each kind, in the order the Runs on card shows them: long jobs first, then quick jobs.</summary>
    public static IReadOnlyList<ThinkingJobKindRow> Rows { get; } =
    [
        new(ThinkingJobKind.ThinkLonger, "think-longer", "Thinking longer and a song's lyrics", "Think longer",
            ThinkingPriority.ThinkLonger, Fast: false, Text, ThinkingJobWhileLive.Stops, []),
        new(ThinkingJobKind.Research, "research", "Web research and app guide research", "Research",
            ThinkingPriority.Research, Fast: false, Text, ThinkingJobWhileLive.Stops, []),
        new(ThinkingJobKind.CheckIn, "check-in", "Check-ins", "Check-in",
            ThinkingPriority.Helper, Fast: false, Text | ThinkingCapability.Vision | ThinkingCapability.Audio | ThinkingCapability.Tools,
            ThinkingJobWhileLive.Stops, []),
        new(ThinkingJobKind.Memory, "memory", "Remembering, tidying memory and the memory brief", "Memory",
            ThinkingPriority.Helper, Fast: false, Text, ThinkingJobWhileLive.Stops,
            [new(HelperJobKind.Memory, "memory"), new(HelperJobKind.MemoryUpkeep, "memory-upkeep"),
                // The brief is never urgent: it waits behind everything.
                new(HelperJobKind.MemoryBrief, "memory-brief", ThinkingPriority.Research)]),
        new(ThinkingJobKind.Naming, "naming", "Learning names, naming emotes and motions, and touch temperament", "Naming",
            ThinkingPriority.Helper, Fast: false, Text | ThinkingCapability.Tools, ThinkingJobWhileLive.Stops,
            [new(HelperJobKind.SpeakerNaming, "speaker-naming"), new(HelperJobKind.ActionNaming, "action-naming"),
                new(HelperJobKind.Temperament, "temperament")]),
        new(ThinkingJobKind.TouchZones, "touch-zones", "Touch zones, measuring the eyes and writing a character's personality", "Touch zones",
            ThinkingPriority.TouchZones, Fast: false, Text | ThinkingCapability.Vision, ThinkingJobWhileLive.Waits,
            [new(HelperJobKind.TouchZones, "touch-zones", OwnerWaits: true),
                // The eyes need vision too, but they wait like the other helpers.
                new(HelperJobKind.Eyes, "eyes", ThinkingPriority.Helper),
                new(HelperJobKind.CharacterWriting, "character-writing", OwnerWaits: true)]),
        new(ThinkingJobKind.Digest, "digest", "Screen and sound summaries", "Summary",
            ThinkingPriority.Digest, Fast: true, Text | ThinkingCapability.Vision | ThinkingCapability.Audio, ThinkingJobWhileLive.Stops, []),
        new(ThinkingJobKind.EndOfTurnJudge, "end-of-turn-judge", "Are you done talking (judge)", "End-of-turn judge",
            ThinkingPriority.EndOfTurnJudge, Fast: true, Text, ThinkingJobWhileLive.Starts, []),
        new(ThinkingJobKind.BargeInJudge, "barge-in-judge", "Did you interrupt (judge)", "Barge-in judge",
            ThinkingPriority.BargeInJudge, Fast: true, Text, ThinkingJobWhileLive.Starts, [])
    ];

    private static readonly Dictionary<ThinkingJobKind, ThinkingJobKindRow> ByKind = Rows.ToDictionary(row => row.Kind);
    private static readonly Dictionary<string, ThinkingJobKindRow> ByKey = Rows.ToDictionary(row => row.Key, StringComparer.Ordinal);
    private static readonly Dictionary<HelperJobKind, (ThinkingJobKindRow Pool, HelperJobKindRow Helper)> ByHelper =
        Rows.SelectMany(row => row.Helpers.Select(helper => (row, helper))).ToDictionary(pair => pair.helper.Kind);

    /// <summary>Every kind, in the enum's order.</summary>
    public static IReadOnlyList<ThinkingJobKind> All { get; } = Enum.GetValues<ThinkingJobKind>();

    /// <summary>The row of <paramref name="kind"/>; a value with no row reads as research (the lowest priority).</summary>
    public static ThinkingJobKindRow Row(ThinkingJobKind kind) => ByKind.GetValueOrDefault(kind) ?? ByKind[ThinkingJobKind.Research];

    /// <summary>The kind whose saved key is <paramref name="key"/>, or null for a key this Martlet doesn't know.</summary>
    public static ThinkingJobKind? Parse(string? key) => key is not null && ByKey.TryGetValue(key, out var row) ? row.Kind : null;

    public static ThinkingPriority Priority(ThinkingJobKind kind) => Row(kind).Priority;

    /// <summary>Fast kinds (the judges and the summaries) may take the last free slot of the pool. Every other kind leaves it
    /// free when the pool has two or more slots, so a fast job never waits behind long work.</summary>
    public static bool IsFast(ThinkingJobKind kind) => Row(kind).Fast;

    /// <summary>The kind's saved key, in thinking-pool.json, status files and logs: barge-in-judge, think-longer...</summary>
    public static string Name(ThinkingJobKind kind) => Row(kind).Key;

    /// <summary>The pool kind that <paramref name="kind"/> runs as and its own row there; a value with no row reads as touch
    /// zones.</summary>
    public static (ThinkingJobKindRow Pool, HelperJobKindRow Helper) Helper(HelperJobKind kind) =>
        ByHelper.TryGetValue(kind, out var found) ? found : ByHelper[HelperJobKind.TouchZones];
}
