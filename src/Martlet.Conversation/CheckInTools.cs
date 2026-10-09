using System.Collections;
using System.Text.Json;
using System.Text.Json.Nodes;
using Martlet.Providers;

namespace Martlet.Conversation;

/// <summary>A named set of tools a check-in may call (Companion › Check-ins, "It may use these tools"): its ID (lowercase
/// kebab-case, kept in check-ins.json), its name and what it does in plain words for the card, and its tools. Tool names are
/// unique across every set. The desktop runs each set's calls with one <see cref="CheckInToolHandler"/>.</summary>
public sealed record CheckInToolSet(string Id, string Name, string Does, IReadOnlyList<TextToolDefinition> Tools)
{
    /// <summary>The reply tools (Martlet's own tools on the live reply, by name) this set takes over when an After each exchange
    /// check-in that is on ticks it and the configured Thinking pool has a member that calls tools: the reply is then offered
    /// none of them, and the check-in does that work after the reply instead. Empty: the reply keeps all its tools.</summary>
    public IReadOnlyList<string> Replaces { get; init; } = [];

    public override string ToString() => $"{nameof(CheckInToolSet)} {Id}";
}

/// <summary>Who calls a tool and when: the check-in's ID and name, the active personality's ID (null: none) and the time the
/// run began, so a handler can record who changed what and when.</summary>
public sealed record CheckInToolContext(string CheckInId, string CheckInName, string? PersonaId, DateTimeOffset Now);

/// <summary>One tool call a check-in run made: its set, the tool, what came of it (the first line of the tool's answer, at most
/// <see cref="CheckIns.MaximumToolResultCharacters"/> characters; it shows on the card and in check-ins-status.json, so a
/// handler's first line never holds private text) and whether it failed.</summary>
public sealed record CheckInToolUse(string Set, string Tool, string Result, bool Failed);

/// <summary>Runs one call of a tool set's tools for a check-in. A failed or declined call is a result with IsError, not an
/// exception. Handlers run on a pool thread.</summary>
public delegate ValueTask<ConversationToolResult> CheckInToolHandler(TextToolCall call, CheckInToolContext context, CancellationToken token);

/// <summary>Every tool set a check-in may choose. To add one: give it a static set here or in its own class, add it to
/// <see cref="All"/>, and register its handler in the desktop's check-in tool handlers (MainWindow.CheckInTools.cs).</summary>
public static class CheckInToolSets
{
    public const string CharacterId = "character", NextReplyId = "next-reply", RemindersId = "reminders";
    public const string TurnOffEmote = "turn_off_emote", LookUsual = "look_usual", RemindNextReply = "remind_next_reply", BringUp = "bring_up";

    private const string NoArguments = """{"type":"object","properties":{},"additionalProperties":false}""";

    /// <summary>Emotes and gaze: what Lingering emotes and Where the character looks do with their answers, as tools.</summary>
    public static CheckInToolSet Character { get; } = new(CharacterId, "Emotes and gaze",
        "Turns off a lingering emote a reply turned on, or takes the character's eyes back to their usual gaze.",
    [
        new(TurnOffEmote, "Turns off one lingering emote a reply turned on that still shows on the character, by its tag " +
            "without braces (such as blush).",
            """{"type":"object","properties":{"tag":{"type":"string","description":"The emote's tag without braces, like blush."}},"required":["tag"],"additionalProperties":false}"""),
        new(LookUsual, "Takes the character's eyes back to their usual gaze, ending the gaze a reply chose.", NoArguments)
    ]);

    /// <summary>Martlet's next words: a reminder in the notes of the next message, or something Martlet brings up on its own.</summary>
    public static CheckInToolSet NextReply { get; } = new(NextReplyId, "Martlet's next words",
        "Puts a short reminder in the notes of your next message, so Martlet's next reply follows it, or has Martlet bring " +
        "something up on its own as soon as it's free.",
    [
        new(RemindNextReply, "Puts a short reminder in the notes of the user's next message, so the character's next reply " +
            "follows it. Write it to the character, in one short line.",
            """{"type":"object","properties":{"text":{"type":"string","description":"What to keep in mind or do in the next reply, one short line."}},"required":["text"],"additionalProperties":false}"""),
        new(BringUp, "Has the character bring something up with the user on its own as soon as it's free. Say what to bring " +
            "up in one short line.",
            """{"type":"object","properties":{"text":{"type":"string","description":"What to bring up with the user now, one short line."}},"required":["text"],"additionalProperties":false}""")
    ]);

    /// <summary>Reminders: the conversation's own reminders tool (set, list, cancel).</summary>
    public static CheckInToolSet ReminderSet { get; } = new(RemindersId, "Reminders",
        "Sets, lists and cancels your reminders, as Martlet does when you ask it in a conversation.", [Reminders.Definition]);

    /// <summary>Every set, in the order the card shows them. The static sets above come first, so they exist when this list is made.</summary>
    public static IReadOnlyList<CheckInToolSet> All { get; } = [Character, NextReply, ReminderSet, TouchReactions.Set, BackgroundWorkTools.Set];

    public static CheckInToolSet? Find(string? id) => id is null ? null : All.FirstOrDefault(s => s.Id == id);

    /// <summary>Whether <paramref name="id"/> is a set ID's shape: 1-40 lowercase ASCII letters, digits and hyphens.</summary>
    public static bool IsId(string? id) =>
        id is { Length: > 0 and <= 40 } && id.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-');

    /// <summary>The string argument <paramref name="name"/> of <paramref name="call"/>, trimmed, or null when it is missing or empty.</summary>
    public static string? Argument(TextToolCall call, string name)
    {
        ArgumentNullException.ThrowIfNull(call);
        try
        {
            return JsonNode.Parse(call.ArgumentsJson) is JsonObject arguments && arguments[name] is JsonValue value &&
                value.TryGetValue<string>(out var text) && text.Trim() is { Length: > 0 } trimmed ? trimmed : null;
        }
        catch (JsonException) { return null; }
    }
}

/// <summary>A check-in's chosen tool set IDs, compared by value (so a choice or one of the owner's check-ins with the same sets
/// is equal, and the page doesn't save it again).</summary>
public sealed class CheckInToolSetIds : IReadOnlyList<string>, IEquatable<CheckInToolSetIds>
{
    private readonly string[] ids;

    public CheckInToolSetIds(IEnumerable<string> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        this.ids = [.. ids];
    }

    public static CheckInToolSetIds Empty { get; } = new([]);
    public string this[int index] => ids[index];
    public int Count => ids.Length;
    public IEnumerator<string> GetEnumerator() => ((IEnumerable<string>)ids).GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    public bool Equals(CheckInToolSetIds? other) => other is not null && ids.SequenceEqual(other.ids, StringComparer.Ordinal);
    public override bool Equals(object? obj) => obj is CheckInToolSetIds other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var id in ids) hash.Add(id, StringComparer.Ordinal);
        return hash.ToHashCode();
    }

    public override string ToString() => string.Join(", ", ids);
}

/// <summary>Runs a check-in's tool calls during one run: only the tools of its chosen sets that have a handler, at most
/// <see cref="CheckIns.MaximumToolCalls"/> calls in all (over every member the pool tries), and keeps each call's
/// <see cref="CheckInToolUse"/> for the run record. A handler that throws gives "The tool failed."</summary>
public sealed class CheckInToolHost : IConversationToolHost
{
    private readonly object gate = new();
    private readonly List<CheckInToolUse> uses = [];
    private readonly Dictionary<string, (CheckInToolSet Set, CheckInToolHandler Handler)> tools = new(StringComparer.Ordinal);
    private int calls;

    public CheckInToolHost(IEnumerable<string> setIds, CheckInToolContext context, IReadOnlyDictionary<string, CheckInToolHandler> handlers)
    {
        ArgumentNullException.ThrowIfNull(setIds);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(handlers);
        Context = context;
        var definitions = new List<TextToolDefinition>();
        foreach (var id in setIds.Distinct(StringComparer.Ordinal))
        {
            if (CheckInToolSets.Find(id) is not { } set || !handlers.TryGetValue(id, out var handler)) continue;
            foreach (var tool in set.Tools)
                if (tools.TryAdd(tool.Name, (set, handler))) definitions.Add(tool);
        }
        Tools = definitions;
    }

    public CheckInToolContext Context { get; }
    /// <summary>The tools the model is offered.</summary>
    public IReadOnlyList<TextToolDefinition> Tools { get; }

    /// <summary>The calls made so far, in order.</summary>
    public IReadOnlyList<CheckInToolUse> Uses
    {
        get
        {
            lock (gate) return [.. uses];
        }
    }

    public async ValueTask<ConversationToolResult> CallAsync(TextToolCall call, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(call);
        tools.TryGetValue(call.Name, out var entry);
        var set = entry.Set?.Id ?? "";
        var tool = call.Name.Length > 64 ? call.Name[..64] : call.Name;
        lock (gate)
        {
            if (calls >= CheckIns.MaximumToolCalls)
                return Record(set, tool, new($"This check-in already made {CheckIns.MaximumToolCalls} tool calls, the most one run " +
                    "may make. Answer now without tools.", true));
            calls++;
        }
        if (entry.Handler is null) return Record(set, tool, new($"This check-in has no tool called {tool}.", true));
        ConversationToolResult result;
        try { result = await entry.Handler(call, Context, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { result = new("The tool failed.", true); }
        return Record(set, tool, result ?? new("The tool failed.", true));
    }

    private ConversationToolResult Record(string set, string tool, ConversationToolResult result)
    {
        var line = (result.Output ?? "").Split('\n', 2)[0].Trim();
        if (line.Length > CheckIns.MaximumToolResultCharacters) line = line[..(CheckIns.MaximumToolResultCharacters - 3)] + "...";
        lock (gate) uses.Add(new(set, tool, line, result.IsError));
        return result;
    }

    public override string ToString() => $"{nameof(CheckInToolHost)} ({Tools.Count} tools)";
}
