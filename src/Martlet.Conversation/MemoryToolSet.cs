using Martlet.Providers;

namespace Martlet.Conversation;

/// <summary>The Memory check-in tool set (<c>memory</c>): looks over, finds, remembers, corrects, notes, merges, marks outdated,
/// reassigns and forgets facts in Martlet's memory. It does what the reply's manage_memories does, which it takes over
/// (<see cref="CheckInToolSet.Replaces"/>), and more, so a check-in can look after memory in several steps (Memory care, Act on
/// what was said). The desktop runs each tool as that tool's matching action (docs/MEMORY.md).</summary>
public static class MemoryToolSet
{
    public const string Id = "memory";
    /// <summary>The reply tool this set takes over (the desktop's MemoryTools.Name).</summary>
    public const string ReplyTool = "manage_memories";
    public const string Overview = "memory_overview", Find = "memory_find", History = "memory_history", Remember = "memory_remember",
        Update = "memory_update", UpdateMany = "memory_update_many", Note = "memory_note", Merge = "memory_merge",
        Outdated = "memory_outdated", Forget = "memory_forget";

    /// <summary>The most facts one memory_find call shows, and the most edits one memory_update_many call makes.</summary>
    public const int MaximumFindLimit = 50, MaximumEdits = 25;
    /// <summary>The most facts one memory_note call notes, the longest note and the most facts one memory_merge call merges.</summary>
    public const int MaximumNoted = 50, MaximumNoteCharacters = 80, MaximumMerged = 10;

    private const string PersonText = "Whose facts: a name or voice tag like V3, \\\"me\\\" for who spoke last, or \\\"everyone\\\" for no one in particular.";
    private const string WhereText = "Where the fact matters: \\\"everywhere\\\", \\\"here\\\" for what the user does now (such as the game they play), \\\"games\\\" for any game, or a game\u0027s name like Minecraft. A fact for one game comes up only while the user plays it.";

    public static CheckInToolSet Set { get; } = new(Id, "Memory",
        "Looks over, finds, remembers, corrects, notes, merges, marks outdated, gives to someone else and forgets facts in " +
        "Martlet's memory, as Martlet does when you ask it in a conversation, in as many steps as it takes.",
    [
        new(Overview, "Gives an overview of the character's long-term memory: how many facts there are, whose they are, how many " +
            "earlier versions are kept as history and the dates of the oldest and newest. Shows no facts. A good first step.",
            """{"type":"object","properties":{},"additionalProperties":false}"""),
        new(Find, "Looks up facts in the character's long-term memory to get their ids: words to look for and/or whose they are. " +
            $"Empty lists the newest. It shows up to limit facts (default 20, at most {MaximumFindLimit}); skip pages through the " +
            "rest (count says how many match). Always find before you change facts; never guess ids.",
            $$$"""{"type":"object","properties":{"query":{"type":"string","description":"Words to look for (empty lists the newest)."},"person":{"type":"string","description":"{{{PersonText}}} Only theirs."},"skip":{"type":"integer","minimum":0,"description":"How many matches to skip, to see the next page."},"limit":{"type":"integer","minimum":1,"maximum":{{{MaximumFindLimit}}},"description":"How many to show (default 20)."}},"additionalProperties":false}"""),
        new(History, "Shows the earlier versions of one fact (what it said before it was corrected or replaced), newest first, by " +
            "its id from memory_find.",
            """{"type":"object","properties":{"id":{"type":"string","description":"The fact's id from memory_find."}},"required":["id"],"additionalProperties":false}"""),
        new(Remember, "Saves a new fact the user asked to be remembered: one short standalone third-person sentence.",
            $$$"""{"type":"object","properties":{"fact":{"type":"string","description":"The fact, one short sentence."},"person":{"type":"string","description":"{{{PersonText}}} Who it belongs to (by default who spoke last)."},"where":{"type":"string","description":"{{{WhereText}}} By default everywhere."}},"required":["fact"],"additionalProperties":false}"""),
        new(Update, "Corrects one fact's text and/or gives it to someone else, by its id from memory_find. The earlier version is " +
            "kept as history.",
            $$$"""{"type":"object","properties":{"id":{"type":"string","description":"The fact's id from memory_find."},"fact":{"type":"string","description":"The corrected fact."},"person":{"type":"string","description":"{{{PersonText}}} Who it belongs to now."},"where":{"type":"string","description":"{{{WhereText}}} Only to move it."}},"required":["id"],"additionalProperties":false}"""),
        new(UpdateMany, $"Corrects up to {MaximumEdits} facts in one call, each like memory_update: its id from memory_find and its " +
            "corrected text and/or whose it is now. Each edit is done or refused on its own.",
            $$$$"""{"type":"object","properties":{"edits":{"type":"array","maxItems":{{{{MaximumEdits}}}},"items":{"type":"object","properties":{"id":{"type":"string","description":"The fact's id from memory_find."},"fact":{"type":"string","description":"The corrected fact."},"person":{"type":"string","description":"{{{{PersonText}}}} Who it belongs to now."},"where":{"type":"string","description":"{{{{WhereText}}}} Only to move it."}},"required":["id"],"additionalProperties":false}}},"required":["edits"],"additionalProperties":false}"""),
        new(Note, $"Adds the same short note to the end of up to {MaximumNoted} facts at once, by their ids from memory_find, such " +
            "as \"(in Minecraft)\" for facts about that game that don't say so. A fact that already has the note is left as it is. " +
            "The earlier versions are kept as history.",
            $$$"""{"type":"object","properties":{"ids":{"type":"array","items":{"type":"string"},"maxItems":{{{MaximumNoted}}},"description":"Ids from memory_find."},"note":{"type":"string","maxLength":{{{MaximumNoteCharacters}}},"description":"The note to add, like (in Minecraft)."}},"required":["ids","note"],"additionalProperties":false}"""),
        new(Merge, $"Folds 2 to {MaximumMerged} facts that say the same thing into the first one, by their ids from memory_find. " +
            "The others are forgotten and their history moves to the kept fact. Give fact to write the kept fact again with what " +
            "all of them said.",
            $$$"""{"type":"object","properties":{"ids":{"type":"array","items":{"type":"string"},"minItems":2,"maxItems":{{{MaximumMerged}}},"description":"Ids from memory_find; the first is kept."},"fact":{"type":"string","description":"The kept fact's new text, one short sentence (optional)."}},"required":["ids"],"additionalProperties":false}"""),
        new(Outdated, "Marks a fact as no longer true because a newer fact replaced it (they moved, changed jobs, finished the " +
            "game). It is kept as history, not deleted. Both ids from memory_find.",
            """{"type":"object","properties":{"id":{"type":"string","description":"The fact that is no longer true."},"by":{"type":"string","description":"The newer fact that replaced it."}},"required":["id","by"],"additionalProperties":false}"""),
        new(Forget, "Deletes facts the user asked to be forgotten, or plain mistakes, by their ids from memory_find.",
            """{"type":"object","properties":{"ids":{"type":"array","items":{"type":"string"},"description":"One or more ids from memory_find."}},"required":["ids"],"additionalProperties":false}""")
    ])
    { Replaces = [ReplyTool] };

    /// <summary>The manage_memories action a tool of this set runs, or null for a tool it doesn't have.</summary>
    public static string? Action(string? tool) => tool switch
    {
        Overview => "overview",
        Find => "find",
        History => "history",
        Remember => "remember",
        Update => "update",
        UpdateMany => "update_many",
        Note => "note",
        Merge => "merge",
        Outdated => "outdated",
        Forget => "forget",
        _ => null
    };
}
