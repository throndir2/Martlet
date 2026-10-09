using Martlet.Providers;

namespace Martlet.Conversation;

/// <summary>The Memory check-in tool set (<c>memory</c>): finds, remembers, corrects, reassigns and forgets facts in Martlet's
/// memory, as the reply's manage_memories does, which it takes over (<see cref="CheckInToolSet.Replaces"/>). The desktop runs
/// each tool as that tool's matching action (docs/MEMORY.md).</summary>
public static class MemoryToolSet
{
    public const string Id = "memory";
    /// <summary>The reply tool this set takes over (the desktop's MemoryTools.Name).</summary>
    public const string ReplyTool = "manage_memories";
    public const string Find = "memory_find", Remember = "memory_remember", Update = "memory_update", Forget = "memory_forget";

    private const string PersonText = "Whose facts: a name or voice tag like V3, \\\"me\\\" for who spoke last, or \\\"everyone\\\" for no one in particular.";

    public static CheckInToolSet Set { get; } = new(Id, "Memory",
        "Finds, remembers, corrects, gives to someone else and forgets facts in Martlet's memory, as Martlet does when you ask it " +
        "in a conversation.",
    [
        new(Find, "Looks up facts in the character's long-term memory to get their ids: words to look for and/or whose they are. " +
            "Empty lists the newest. Always find before update or forget; never guess ids.",
            $$$"""{"type":"object","properties":{"query":{"type":"string","description":"Words to look for (empty lists the newest)."},"person":{"type":"string","description":"{{{PersonText}}} Only theirs."}},"additionalProperties":false}"""),
        new(Remember, "Saves a new fact the user asked to be remembered: one short standalone third-person sentence.",
            $$$"""{"type":"object","properties":{"fact":{"type":"string","description":"The fact, one short sentence."},"person":{"type":"string","description":"{{{PersonText}}} Who it belongs to (by default who spoke last)."}},"required":["fact"],"additionalProperties":false}"""),
        new(Update, "Corrects one fact's text and/or gives it to someone else, by its id from memory_find.",
            $$$"""{"type":"object","properties":{"id":{"type":"string","description":"The fact's id from memory_find."},"fact":{"type":"string","description":"The corrected fact."},"person":{"type":"string","description":"{{{PersonText}}} Who it belongs to now."}},"required":["id"],"additionalProperties":false}"""),
        new(Forget, "Deletes facts the user asked to be forgotten, by their ids from memory_find.",
            """{"type":"object","properties":{"ids":{"type":"array","items":{"type":"string"},"description":"One or more ids from memory_find."}},"required":["ids"],"additionalProperties":false}""")
    ])
    { Replaces = [ReplyTool] };

    /// <summary>The manage_memories action a tool of this set runs, or null for a tool it doesn't have.</summary>
    public static string? Action(string? tool) => tool switch
    {
        Find => "find",
        Remember => "remember",
        Update => "update",
        Forget => "forget",
        _ => null
    };
}
