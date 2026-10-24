using Martlet.Conversation.Guides;
using Martlet.Core.Settings;
using Martlet.Providers;

namespace Martlet.Conversation;

/// <summary>The director (docs/CONVERSATION.md#the-director): the built-in check-in <see cref="CheckIns.Director"/> that starts
/// when the user says something, on a Thinking pool member, never on the conversation's own model, and does the work with every
/// tool while the reply is spoken. This is what it reads that other check-ins don't: the skill index.</summary>
public static class Director
{
    /// <summary>How long one skill's line in the index may be.</summary>
    public const int MaximumIndexLineCharacters = 240;

    /// <summary>How many of the user's turns the director keeps while it is busy (the newest ones).</summary>
    public const int MaximumWaitingAsks = 4;

    /// <summary>How long one waiting turn may be in the joined text.</summary>
    public const int MaximumAskCharacters = 450;

    /// <summary>Adds the user's <paramref name="words"/> to <paramref name="waiting"/>, the turns the director hasn't read yet,
    /// and drops the oldest past <see cref="MaximumWaitingAsks"/>. A turn the user makes while the director runs is kept for its
    /// next run, never replaced by a newer one.</summary>
    public static void Wait(List<string> waiting, string? words)
    {
        ArgumentNullException.ThrowIfNull(waiting);
        if (string.IsNullOrWhiteSpace(words)) return;
        waiting.Add(words.Trim());
        if (waiting.Count > MaximumWaitingAsks) waiting.RemoveRange(0, waiting.Count - MaximumWaitingAsks);
    }

    /// <summary>What the director reads as "what the user just said" ({asked}): one turn as it is; several turns (the user spoke
    /// again while the director was busy) oldest first, each on its own line, so a request in an earlier turn isn't lost.
    /// Null when there is none.</summary>
    public static string? Asked(IReadOnlyList<string> waiting)
    {
        ArgumentNullException.ThrowIfNull(waiting);
        if (waiting.Count == 0) return null;
        if (waiting.Count == 1) return waiting[0];
        return $"{waiting.Count} things, oldest first. Each one may ask for work; do what none of the replies did yet:\n" +
            string.Join("\n", waiting.Select(w => "- " + Clip(w.Replace('\n', ' '))));

        static string Clip(string words) => words.Length <= MaximumAskCharacters ? words : words[..(MaximumAskCharacters - 3)] + "...";
    }

    /// <summary>The skill index the director reads ({skills}): one line "key: when it helps" for each skill with text that isn't
    /// pinned (every reply has those), in the skills' order; null when there is none.</summary>
    public static string? SkillIndex(SkillSettings? skills)
    {
        var lines = SkillSettings.All(skills).Where(s => !s.Pinned && !string.IsNullOrWhiteSpace(s.Body))
            .Select(s =>
            {
                var line = $"{s.Key}: {(string.IsNullOrWhiteSpace(s.UseWhen) ? s.Name : s.UseWhen.Replace('\n', ' ').Trim())}";
                return line.Length <= MaximumIndexLineCharacters ? line : line[..(MaximumIndexLineCharacters - 3)] + "...";
            }).ToArray();
        return lines.Length == 0 ? null : string.Join("\n", lines);
    }

    /// <summary>What load_skill returns for <paramref name="key"/>: a first line that names the skill (it shows on the card), then
    /// its text with {name} and {silent} filled in; an error for a skill that isn't there or is empty.</summary>
    public static ConversationToolResult Load(SkillSettings? skills, string? key, string name, string silent)
    {
        if (key is null) return new("Say which skill to load (key).", true);
        if (SkillSettings.Find(skills, key.Trim()) is not { } skill)
            return new($"There is no skill \"{(key.Length > 40 ? key[..40] : key)}\". Use a key from the skill index.", true);
        if (string.IsNullOrWhiteSpace(skill.Body)) return new($"The skill {skill.Key} is empty.", true);
        return new($"Loaded the skill {skill.Key}.\n" + PromptSettings.FillText(skill.Body.Trim(), ("name", name), ("silent", silent)));
    }
}

/// <summary>Skills (<see cref="SetId"/>): load_skill gives the text of one skill from the skill index.</summary>
public static class SkillTools
{
    public const string SetId = "skills", LoadName = "load_skill";

    public static TextToolDefinition LoadDefinition { get; } = new(LoadName,
        "Loads one skill (instructions for a kind of work) by its key from the skill index. Load a skill before you do the work it covers.",
        """{"type":"object","properties":{"key":{"type":"string","description":"The skill's key, like web-research."}},"required":["key"],"additionalProperties":false}""");

    /// <summary>Only reads; a check-in without a member that calls tools runs without it.</summary>
    public static CheckInToolSet Set { get; } = new(SetId, "Skills",
        "Loads one of Martlet's skills (Companion › Skills), instructions for a kind of work, when it needs them.", [LoadDefinition])
    {
        Optional = true
    };
}

/// <summary>Knowledge (<see cref="SetId"/>): search_knowledge looks through what Martlet already keeps on this PC, with no model
/// call: the app guides (with the pages looked up on the web), remembered facts and lorebook entries.</summary>
public static class KnowledgeTools
{
    public const string SetId = "knowledge", SearchName = "search_knowledge";
    /// <summary>The most hits one search returns, and the most characters of each.</summary>
    public const int MaximumHits = 6, MaximumHitCharacters = 400;

    public static TextToolDefinition SearchDefinition { get; } = new(SearchName,
        "Searches what Martlet already knows on this PC: app and game guides, pages looked up on the web before, remembered " +
        "facts and lorebook entries. Fast and free; try it before looking anything up on the web.",
        """{"type":"object","properties":{"query":{"type":"string","description":"A few words to search for."}},"required":["query"],"additionalProperties":false}""");

    public static CheckInToolSet Set { get; } = new(SetId, "Knowledge",
        "Searches what Martlet already keeps on this PC (app guides and pages it looked up, memory, lorebooks), with no model and " +
        "no web request.", [SearchDefinition])
    {
        Optional = true
    };

    /// <summary>One thing found: where it is from ("guide: Elden Ring › Bosses", "memory", "lorebook: Places") and its text.</summary>
    public sealed record Hit(string Source, string Text, double Relevance);

    /// <summary>The search's answer: a first line with counts only (it shows on the card), then each hit, best first, cut to
    /// <see cref="MaximumHitCharacters"/>.</summary>
    public static ConversationToolResult Answer(IReadOnlyList<Hit> hits)
    {
        var best = hits.OrderByDescending(h => h.Relevance).Take(MaximumHits).ToArray();
        if (best.Length == 0) return new("Found nothing. Martlet doesn't keep anything about it on this PC.");
        var lines = best.Select(h => $"- ({h.Source}) " + Cut(h.Text.Replace('\n', ' ').Trim()));
        return new($"Found {best.Length} thing{(best.Length == 1 ? "" : "s")}.\n" + string.Join("\n", lines));
    }

    private static string Cut(string text) => text.Length <= MaximumHitCharacters ? text : text[..(MaximumHitCharacters - 3)] + "...";
}

/// <summary>Past conversations (<see cref="SetId"/>): the reply's search_conversations, for the director. It takes the reply's
/// tool over.</summary>
public static class PastConversationTools
{
    public const string SetId = "conversations";

    public static CheckInToolSet Set { get; } = new(SetId, "Past conversations",
        "Searches the record of your earlier conversations (Companion › Memory), when you let Martlet search it.", [PastConversations.Definition])
    {
        Replaces = [PastConversations.Definition.Name], Optional = true
    };
}

/// <summary>App guides (<see cref="SetId"/>): the reply's read_up_on, search_guide, skip_guide and update_guide_brief, for the
/// director. It takes the reply's tools over.</summary>
public static class GuideToolSet
{
    public const string SetId = "guides";

    public static CheckInToolSet Set { get; } = new(SetId, "App guides",
        "Reads up on a game or app, searches its guide and stops reading up, as Martlet does when you ask it in a conversation.",
        [AppGuideTools.ReadUpDefinition, AppGuideTools.SearchDefinition, AppGuideTools.SkipDefinition, AppGuideTools.BriefDefinition])
    {
        Replaces = [AppGuideTools.ReadUpName, AppGuideTools.SearchName, AppGuideTools.SkipName, AppGuideTools.BriefName]
    };
}

/// <summary>PC tools (<see cref="SetId"/>): the MCP servers and the terminal of this PC (Companion › Tools), as two tools, so the
/// list of tools never changes from run to run: list_pc_tools names them, call_pc_tool calls one. It takes them over from the
/// reply, except Home Assistant's own tools, which act at once and ask first for locks in the reply's turn.</summary>
public static class PcTools
{
    public const string SetId = "pc-tools", ListName = "list_pc_tools", CallName = "call_pc_tool";
    /// <summary>What <see cref="CheckInToolSet.Replaces"/> names for the MCP servers' tools and the terminal: not a reply tool of its
    /// own, but the reply's toolset leaves them out while it is handed off.</summary>
    public const string ServersName = "pc_tools";
    /// <summary>The most characters list_pc_tools shows of one tool's parameters.</summary>
    public const int MaximumSchemaCharacters = 600;

    public static TextToolDefinition ListDefinition { get; } = new(ListName,
        "Lists the tools of the user's PC (MCP servers and the terminal): each tool's name, what it does and its parameters.",
        """{"type":"object","properties":{},"additionalProperties":false}""");

    public static TextToolDefinition CallDefinition { get; } = new(CallName,
        "Calls one tool of the user's PC by its name from list_pc_tools, with its arguments as one JSON object.",
        """{"type":"object","properties":{"name":{"type":"string","description":"The tool's name from list_pc_tools."},"arguments":{"type":"object","description":"The tool's arguments."}},"required":["name"],"additionalProperties":false}""");

    public static CheckInToolSet Set { get; } = new(SetId, "PC tools",
        "Uses the tools of this PC (the MCP servers and the terminal on Companion › Tools), asking first where you chose that.",
        [ListDefinition, CallDefinition])
    {
        Replaces = [ServersName]
    };

    /// <summary>What list_pc_tools returns for <paramref name="tools"/>: a first line with the count, then one block for each.</summary>
    public static ConversationToolResult List(IReadOnlyList<TextToolDefinition> tools)
    {
        if (tools.Count == 0) return new("No tool of this PC is on now.");
        var blocks = tools.Select(t => $"{t.Name}: {t.Description}\nParameters: " +
            (t.ParametersJson.Length <= MaximumSchemaCharacters ? t.ParametersJson : t.ParametersJson[..MaximumSchemaCharacters] + "..."));
        return new($"{tools.Count} tool{(tools.Count == 1 ? "" : "s")} of this PC.\n" + string.Join("\n\n", blocks));
    }

    /// <summary>The tool and its arguments of a call_pc_tool <paramref name="call"/>, or why it can't be read.</summary>
    public static (TextToolCall? Call, string? Problem) Parse(TextToolCall call)
    {
        ArgumentNullException.ThrowIfNull(call);
        try
        {
            if (System.Text.Json.Nodes.JsonNode.Parse(call.ArgumentsJson) is not System.Text.Json.Nodes.JsonObject arguments ||
                CheckInToolSets.Argument(call, "name") is not { } name)
                return (null, "Say which tool to call (name).");
            if (name.Length > TextToolCall.MaxNameLength || name.Any(char.IsControl)) return (null, "There is no such tool. Use a name from list_pc_tools.");
            var inner = arguments["arguments"] is System.Text.Json.Nodes.JsonObject given ? given.ToJsonString() : "{}";
            return (new(call.CallId, name, inner), null);
        }
        catch (System.Text.Json.JsonException) { return (null, "The arguments must be one JSON object."); }
    }
}
