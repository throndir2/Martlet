using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Martlet.Mcp;

/// <summary>What makes the server easy for an AI assistant to find its way in: the instructions sent at initialize, a short
/// tool list of the tools an assistant needs most (with titles and hints), martlet_guide (start here: tasks step by step and
/// every tool, searchable) and martlet_call (any tool by name, including the many diagnostic tools that are not listed unless the
/// server starts with --all-tools).</summary>
internal sealed partial class McpServer
{
    private const string DataDirectoryNote = "dataDirectory: optional absolute path of a Martlet data folder; default the current " +
        "Windows user's (%LOCALAPPDATA%\\Martlet), the one the installed Martlet uses.";

    /// <summary>The tools listed by default, in order, with a title and MCP's behavior hints. The rest are listed with
    /// --all-tools and always run by name (martlet_call, scripts\Invoke-MartletMcp.ps1).</summary>
    private static readonly (string Name, string Title, bool? ReadOnly, bool? Destructive, bool? Idempotent)[] Listed =
    [
        ("martlet_guide", "Start here: what Martlet's MCP server can do", true, null, true),
        ("martlet_call", "Run any Martlet tool by name", null, null, null),
        ("characters_list", "List Martlet's characters", true, null, true),
        ("character_find_looks", "Find looks for a character online", true, null, true),
        ("character_find_on_web", "Find a character's model or voice on the web", true, null, true),
        ("character_create", "Make a character", false, false, false),
        ("character_update", "Change a character", false, true, true),
        ("character_use", "Switch Martlet to a character", false, false, true),
        ("character_delete", "Delete a character", false, true, true),
        ("settings_get", "Read Martlet's settings", true, null, true),
        ("settings_schema", "Show what Martlet's settings can hold", true, null, true),
        ("settings_set", "Change a Martlet setting", false, true, true),
        ("doctor_status", "Check Martlet's status", true, null, true),
        ("doctor_list", "List Martlet's checks", true, null, true),
        ("doctor_run", "Run Martlet's checks", true, null, true),
        ("logs_tail", "Read Martlet's log", true, null, true),
        ("ui_connect", "Connect to the running Martlet app", true, null, true),
        ("ui_snapshot", "See the Martlet app's controls and status", true, null, true),
        ("ui_click", "Click a control in the Martlet app", false, null, null),
        ("ui_select", "Choose an item in the Martlet app", false, null, null),
        ("ui_set_text", "Type into a text box in the Martlet app", false, null, true),
        ("ui_toggle", "Turn a checkbox in the Martlet app on or off", false, null, null),
        ("ui_set_range", "Move a slider in the Martlet app", false, null, true),
        ("ui_scroll", "Scroll a page in the Martlet app", true, null, true)
    ];

    private (string Area, McpTool[] Tools)[]? groups;
    private Dictionary<string, McpTool>? byName;
    private JsonObject[]? catalog;
    private JsonArray? listedTools, everyTool;

    /// <summary>The tools by martlet_guide area (<see cref="ToolGroups"/>), made once for this server.</summary>
    private (string Area, McpTool[] Tools)[] Groups => groups ??= ToolGroups();

    /// <summary>The tool with this name, or null. Two tools with one name fail here.</summary>
    private McpTool? FindTool(string name) =>
        (byName ??= Groups.SelectMany(group => group.Tools).ToDictionary(tool => tool.Name, StringComparer.Ordinal)).GetValueOrDefault(name);

    /// <summary>Every tool's definition as tools/list shows it, in the order of the guide's areas.</summary>
    private JsonObject[] Catalog => catalog ??=
        [.. Groups.SelectMany(group => group.Tools).Select(tool => JsonSerializer.SerializeToNode(tool.Definition)!.AsObject())];

    private JsonArray ListedTools => listedTools ??= new JsonArray([.. Listed.Select(entry => (JsonNode)Hinted(entry))]);

    private JsonArray EveryTool => everyTool ??= new JsonArray([.. Catalog.Select(tool =>
        Listed.FirstOrDefault(entry => entry.Name == (string)tool["name"]!) is { Name: not null } entry
            ? Hinted(entry) : (JsonNode)tool.DeepClone())]);

    private JsonObject Hinted((string Name, string Title, bool? ReadOnly, bool? Destructive, bool? Idempotent) entry)
    {
        var tool = Catalog.FirstOrDefault(t => (string)t["name"]! == entry.Name)?.DeepClone().AsObject()
            ?? throw new InvalidOperationException($"The listed tool {entry.Name} has no definition.");
        tool["title"] = entry.Title;
        var hints = new JsonObject { ["title"] = entry.Title };
        if (entry.ReadOnly is { } readOnly) hints["readOnlyHint"] = readOnly;
        if (entry.Destructive is { } destructive) hints["destructiveHint"] = destructive;
        if (entry.Idempotent is { } idempotent) hints["idempotentHint"] = idempotent;
        // The listed tools stay on this PC (they read and write Martlet's own files and windows), except the two finders, which
        // send the query to VRoid Hub, GitHub and DuckDuckGo.
        if (entry.Name is "character_find_looks" or "character_find_on_web") hints["openWorldHint"] = true;
        else if (entry.Name != "martlet_call") hints["openWorldHint"] = false;
        tool["annotations"] = hints;
        return tool;
    }

    private JsonArray ToolList() => allTools ? EveryTool : ListedTools;

    private bool IsTool(string name) => FindTool(name) is not null;

    /// <summary>The Martlet version this server was built as (Directory.Build.props), without the commit.</summary>
    internal static string Version =>
        (typeof(McpServer).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0").Split('+')[0];

    private static string DefaultDataDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Martlet");

    /// <summary>initialize's instructions: what Martlet is, where to start and what this server may do.</summary>
    private string Instructions() =>
        "Martlet is a desktop AI companion for Windows: a character with a personality, a look (a Live2D or VRM model) and a voice " +
        "that you talk with. This server (\"martlet\") reads and changes the Martlet on this PC and runs its diagnostics.\n" +
        "Start with martlet_guide: it gives the common tasks step by step and lists every tool.\n" +
        "- Characters: characters_list, character_find_looks (models online for a character you name), character_create (from a " +
        "description or a SillyTavern/Chub character card), character_update, " +
        "character_use, character_delete.\n" +
        "- Settings: settings_get, settings_schema, settings_set (personalities, replies, prompts, memory).\n" +
        "- The running app: ui_connect, ui_snapshot, ui_click and the other ui_* tools.\n" +
        "- Diagnostics: doctor_status, doctor_list, doctor_run, logs_tail.\n" +
        (allTools ? "- Every tool is listed (--all-tools).\n"
            : $"- {Catalog.Length - Listed.Length} more tools (status, checks and self-tests) are not listed; find them with " +
              "martlet_guide (search) and run them with martlet_call.\n") +
        "Tools use the current Windows user's Martlet data folder (%LOCALAPPDATA%\\Martlet) unless you pass dataDirectory.\n" +
        (allowChanges ? "Changes are on (--allow-changes): character_* and settings_set save to Martlet's settings, and a running Martlet follows them; " +
              "memory_forget deletes facts; the diagnostic tools may save or forget FIXTURE data in a disposable dataDirectory and write output files.\n"
            : "Changes are off: character_create/update/use/delete, settings_set, memory_forget (except preview: true), logs_export and the " +
              "diagnostic tools' save, forget and output files need the server started with --allow-changes.\n") +
        (desktop.AllowsEffects ? "UI effects are on (--allow-ui-effects): ui_* tools may press buttons that change things.\n"
            : "UI effects are off: ui_* tools only navigate and read; buttons that change things need --allow-ui-effects.\n") +
        "Where Martlet thinks, listens and speaks, and its API keys, change only in the app with the owner, because they need the " +
        "owner's consent.";

    private object Guide(string? topic, string? search, string? tool)
    {
        if (tool is not null)
        {
            var found = Catalog.FirstOrDefault(t => string.Equals((string)t["name"]!, tool.Trim(), StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException($"No Martlet tool is called '{tool}'. Use martlet_guide with search to find one.");
            var name = (string)found["name"]!;
            return new
            {
                tool = found.DeepClone(),
                listed = allTools || Listed.Any(entry => entry.Name == name),
                run = allTools || Listed.Any(entry => entry.Name == name) ? $"Call {name} directly."
                    : $"Call martlet_call with tool \"{name}\" and its arguments."
            };
        }
        if (search is not null)
        {
            var words = search.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (words.Length == 0) throw new ArgumentException("search needs at least one word.");
            var matches = Catalog
                .Select(t => (Name: (string)t["name"]!, Description: (string)t["description"]!))
                .Select(t => (t.Name, t.Description, Score: words.Sum(word =>
                    (t.Name.Contains(word, StringComparison.OrdinalIgnoreCase) ? 3 : 0) +
                    (t.Description.Contains(word, StringComparison.OrdinalIgnoreCase) ? 1 : 0)),
                    All: words.All(word => t.Name.Contains(word, StringComparison.OrdinalIgnoreCase) ||
                        t.Description.Contains(word, StringComparison.OrdinalIgnoreCase))))
                .Where(t => t.All)
                .OrderByDescending(t => t.Score).ThenBy(t => t.Name, StringComparer.Ordinal)
                .Take(25)
                .Select(t => new { name = t.Name, area = Area(t.Name), summary = Summary(t.Description), listed = Listed.Any(e => e.Name == t.Name) })
                .ToArray();
            return new
            {
                search, matches,
                next = matches.Length == 0 ? "Try other words, or topic tools for every tool."
                    : "martlet_guide with tool gives one tool's arguments; martlet_call runs a tool that is not listed."
            };
        }
        var chosen = (topic ?? "overview").Trim().ToLowerInvariant();
        var recipes = Recipes().Select(r => new { topic = r.Topic, task = r.Task, steps = r.Steps });
        object? shownRecipes = chosen switch
        {
            "overview" => recipes.ToArray(),
            "characters" or "settings" or "app" or "diagnose" => recipes.Where(r => r.topic == chosen).ToArray(),
            "tools" => null,
            _ => throw new ArgumentException("topic is overview, characters, settings, app, diagnose or tools.")
        };
        return new
        {
            about = "Martlet is a desktop AI companion for Windows: a character with a personality, a look (a Live2D or VRM model) and " +
                "a voice that you talk with, by voice or text. It can see the screen, remember, use tools and run on several of your " +
                "computers. This MCP server reads and changes the Martlet on this PC, drives its window and runs its diagnostics.",
            server = new
            {
                version = Version,
                changes = allowChanges ? "on (--allow-changes)" : "off: start the server with --allow-changes to make or change characters and settings",
                uiEffects = desktop.AllowsEffects ? "on (--allow-ui-effects)" : "off: ui_* tools only navigate and read until the server starts with --allow-ui-effects",
                toolsListed = allTools ? $"all {Catalog.Length} (--all-tools)" : $"{Listed.Length} of {Catalog.Length}; martlet_call runs the rest",
                dataDirectory = new { path = DefaultDataDirectory, exists = Directory.Exists(DefaultDataDirectory), note = DataDirectoryNote },
                runningApp = DesktopAutomation.RunningDesktops() is { Length: > 0 } running
                    ? new { running = true, processIds = running, note = "ui_connect connects to it." }
                    : new { running = false, processIds = Array.Empty<int>(), note = "Martlet isn't running in this Windows session. Saved changes apply when it starts." }
            },
            recipes = shownRecipes,
            areas = chosen == "tools"
                ? Catalog.GroupBy(t => Area((string)t["name"]!)).OrderBy(g => AreaOrder(g.Key)).Select(g => new
                {
                    area = g.Key,
                    tools = g.Select(t => new
                    {
                        name = (string)t["name"]!, summary = Summary((string)t["description"]!),
                        listed = allTools || Listed.Any(e => e.Name == (string)t["name"]!)
                    }).ToArray()
                }).ToArray()
                : (object)Catalog.GroupBy(t => Area((string)t["name"]!)).OrderBy(g => AreaOrder(g.Key))
                    .Select(g => new { area = g.Key, tools = g.Count() }).ToArray(),
            more = chosen == "tools" ? null
                : "martlet_guide with topic tools lists every tool by area; search finds tools; tool shows one tool's arguments."
        };
    }

    private sealed record Recipe(string Topic, string Task, string[] Steps);

    private static Recipe[] Recipes() =>
    [
        new("characters", "Make a character", [
            "Call characters_list to see the characters, and the looks and voices a character can use.",
            "Call character_create with a name and a personality: the instructions that say who the character is, what it knows and how " +
                "it talks (for example: \"You are Ava, a cheerful astronomer. Speak warmly and briefly.\").",
            "Or give cardPath: an absolute path to a SillyTavern or Chub character card (PNG, JSON or CHARX).",
            "Add look and voice from characters_list to give it a body and a voice. Martlet becomes the new character at once unless use is false.",
            "New looks (Live2D or VRM models) and new voices (from a recording) are added in the app: Companion › Character and Companion › Voice."
        ]),
        new("characters", "Make a character from a name, with a look found online", [
            "Ask the owner which character they want (for example a character from a game or an anime, or an original one they describe).",
            "Call character_find_looks with that name. Show the owner the looks: name, creator, source, license and the conditions of use.",
            "Write the personality yourself: who the character is, how it talks and how it treats the owner (120 to 250 words, \"You are ...\"). " +
                "End it with a short example dialogue of four to six lines (\"Owner:\" and the character's name) that shows its voice: a quick " +
                "reaction, playing along with a joke and a callback to something the owner said earlier. Smaller models copy examples better than rules.",
            "Call character_create with name, personality and look: the key of the look the owner chose (vroidhub:<id> or osa:<project>/<avatar>).",
            "A vroidhub: look needs the owner's VRoid Hub account linked in the app (Account › Linked services); vroid_hub_status says whether it is.",
            "Nothing usable found, or the character needs a voice? Call character_find_on_web: it gives web pages and site searches. The owner " +
                "downloads a model or a voice clip they may use and adds it in the app (Companion › Character › Add a character; Companion › Voice › Add a voice).",
            "Or let the owner do it all in the app: Companion › Character › Find a character."
        ]),
        new("characters", "Change, switch or delete a character", [
            "character_update changes a character's name, personality, look or voice.",
            "character_use switches Martlet to a character; its look and voice change when the profile is used in the app (the result's followUp says how).",
            "character_delete removes a character; profileOnly keeps its personality."
        ]),
        new("settings", "Change a setting", [
            "Call settings_get without a path to see the parts of Martlet's settings and which ones can change here.",
            "Call settings_schema with a path (for example generation) to see its fields and allowed values.",
            "Call settings_set with path and value (for example generation.temperature and 0.8); null puts a value back to its default.",
            "To see or edit Martlet's internal prompts, call martlet_call with prompts_status, then settings_set under prompts.",
            "Where Martlet thinks, listens and speaks (providers, models, API keys) changes only in the app, with the owner: Companion › Thinking, Listening and Voice."
        ]),
        new("app", "Use the running Martlet app", [
            "Call ui_connect (without pid it finds the Martlet running in this Windows session).",
            "Call ui_snapshot to see its controls by automation ID, their state and status text (idPrefix narrows it).",
            "Navigate with ui_click on NavHome, NavCompanion, NavDevices, NavSettings and the tabs (CompanionTab-Personality, CompanionTab-Profiles...).",
            "Clicks that change things, typing and toggles need the server started with --allow-ui-effects."
        ]),
        new("diagnose", "Find a problem", [
            "Call doctor_status for Martlet's status; doctor_list then doctor_run for checks.",
            "Call logs_tail (contains narrows it) for the desktop log; failed provider requests are there with the provider's reason.",
            "Call martlet_call with latency_report for slow replies, or thinking_trace for what each reply waited for.",
            "martlet_guide with topic tools lists the other status tools and self-tests."
        ])
    ];

    /// <summary>The first sentence of a description, at most 220 characters.</summary>
    private static string Summary(string description)
    {
        var end = description.IndexOf(". ", StringComparison.Ordinal);
        var first = end > 0 ? description[..(end + 1)] : description;
        return first.Length <= 220 ? first : first[..217].TrimEnd() + "...";
    }

    /// <summary>The place of an area in the guide: the order of <see cref="ToolGroups"/>.</summary>
    private int AreaOrder(string area) => Array.FindIndex(Groups, group => group.Area == area);

    /// <summary>A tool's martlet_guide area: the group (Tools\<Area>Tools.cs) that registers it.</summary>
    private string Area(string name) => Groups.First(group => group.Tools.Any(tool => tool.Name == name)).Area;

    /// <summary>martlet_call: runs another tool by name, returning its result as if it was called directly.</summary>
    private async Task<object> CallByNameAsync(JsonElement arguments, CancellationToken cancellation)
    {
        var tool = RequiredString(arguments, "tool").Trim();
        if (tool == "martlet_call") throw new ArgumentException("martlet_call runs other tools; call the tool you want through it.");
        if (!IsTool(tool)) throw new ArgumentException($"No Martlet tool is called '{tool}'. Find tools with martlet_guide (search).");
        var request = new Dictionary<string, object?> { ["name"] = tool };
        if (arguments.TryGetProperty("arguments", out var given) && given.ValueKind != JsonValueKind.Null)
        {
            if (given.ValueKind != JsonValueKind.Object) throw new ArgumentException("arguments must be an object.");
            request["arguments"] = given;
        }
        return await CallAsync(JsonSerializer.SerializeToElement(request), cancellation);
    }

    /// <summary>Runs a tool that changes Martlet's settings, when the server may (--allow-changes).</summary>
    private async Task<object> ChangingAsync(string tool, Func<Task<object>> change)
    {
        if (!allowChanges)
            throw new InvalidOperationException($"{tool} changes Martlet or writes a file, so this server must start with --allow-changes. Add " +
                "\"--allow-changes\" to the martlet server's args in your MCP client's configuration and restart it " +
                "(scripts\\Register-MartletMcp.ps1 adds it).");
        return await change();
    }

    private static JsonElement RequiredValue(JsonElement arguments, string property) =>
        arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty(property, out var value)
            ? value : throw new ArgumentException($"Missing '{property}' (use null to remove a value).");
}
