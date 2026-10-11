using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Martlet.Mcp;

/// <summary>What makes the server easy for an AI assistant to find its way in: the instructions sent at initialize, a short
/// tool list of the tools an assistant needs most (with titles and hints), martlet_guide (start here: tasks step by step and
/// every tool, searchable) and martlet_call (any tool by name, including the 150+ diagnostic tools that are not listed unless the
/// server starts with --all-tools).</summary>
internal sealed partial class McpServer
{
    private const string DataDirectoryNote = "dataDirectory: optional absolute path of a Martlet data folder; default the current " +
        "Windows user's (%LOCALAPPDATA%\\Martlet), the one the installed Martlet uses.";

    /// <summary>The tools for assistants that use and configure Martlet, first in every list.</summary>
    private static readonly object[] AssistantTools =
    [
        Tool("martlet_guide", "Start here. Explains what Martlet is and what this server can do, gives the common tasks step by step " +
            "(make or change a character, change settings, use the running app, find a problem) and lists every tool with a short " +
            "summary. topic: overview (default), characters, settings, app, diagnose or tools (every tool by area). search: words to " +
            "find tools by name or description. tool: one tool's full description and input schema (also for tools not listed, " +
            "which martlet_call runs). Read-only.", new
        {
            topic = new { type = "string", @enum = new[] { "overview", "characters", "settings", "app", "diagnose", "tools" } },
            search = new { type = "string", maxLength = 200 },
            tool = new { type = "string", maxLength = 64 }
        }),
        Tool("martlet_call", "Run any Martlet MCP tool by name with its arguments, including the diagnostic and self-test tools " +
            "that are not in the tool list. Find them and their arguments with martlet_guide (search, or tool for one tool's " +
            "schema). Returns that tool's result.", new
        {
            tool = new { type = "string", maxLength = 64 },
            arguments = new { type = "object" }
        }, ["tool"]),
        Tool("characters_list", "List Martlet's characters: each personality (name, whether Martlet is it now, how long its " +
            "instructions are, and the instructions themselves with includeText) with its character profile (its look and voice), " +
            "plus the looks (character models) and voices a character can use, and the limits. Use the names or IDs with the " +
            "other character_* tools. " + DataDirectoryNote + " Read-only.", new
        {
            includeText = new { type = "boolean" },
            dataDirectory = new { type = "string" }
        }),
        Tool("character_create", "Make a new character in Martlet: a personality (name and personality, the instructions that say who " +
            "the character is and how it talks, up to 8,192 characters) and a character profile that gives it a look and a voice " +
            "(look: 'builtin' or a look's name or ID from characters_list; voice: a voice's name or ID; both optional, profile " +
            "false makes only the personality). Or import a SillyTavern, Chub or other Tavern character card with cardPath (an " +
            "absolute path to a PNG, JSON or CHARX card; name and personality then override the card's), which also keeps the card's " +
            "keyword lorebook entries for it. Martlet becomes the new character at once unless use is false; a running Martlet " +
            "follows by itself. Needs the server started with --allow-changes. " + DataDirectoryNote, new
        {
            name = new { type = "string", maxLength = 64 },
            personality = new { type = "string", maxLength = 8192 },
            cardPath = new { type = "string" },
            look = new { type = "string", maxLength = 128 },
            voice = new { type = "string", maxLength = 128 },
            profile = new { type = "boolean" },
            use = new { type = "boolean" },
            dataDirectory = new { type = "string" }
        }),
        Tool("character_update", "Change a character: character is its name, ID or profile key (from characters_list); name renames " +
            "it, personality replaces its instructions, look and voice change its profile's look and voice ('keep' keeps whatever " +
            "Martlet uses now; a character without a profile gets one). Needs --allow-changes. " + DataDirectoryNote, new
        {
            character = new { type = "string", maxLength = 128 },
            name = new { type = "string", maxLength = 64 },
            personality = new { type = "string", maxLength = 8192 },
            look = new { type = "string", maxLength = 128 },
            voice = new { type = "string", maxLength = 128 },
            dataDirectory = new { type = "string" }
        }, ["character"]),
        Tool("character_use", "Switch Martlet to a character (its name, ID or profile key): Martlet talks with its personality from " +
            "the next reply. A profile's look and voice change on this PC when the profile is used in the app; the result says how " +
            "(followUp). Needs --allow-changes. " + DataDirectoryNote, new
        {
            character = new { type = "string", maxLength = 128 },
            dataDirectory = new { type = "string" }
        }, ["character"]),
        Tool("character_delete", "Delete a character (its name, ID or profile key): its personality and its profiles. profileOnly " +
            "removes only the profile and keeps the personality. Martlet keeps at least one personality; looks and voices stay. " +
            "Needs --allow-changes. " + DataDirectoryNote, new
        {
            character = new { type = "string", maxLength = 128 },
            profileOnly = new { type = "boolean" },
            dataDirectory = new { type = "string" }
        }, ["character"]),
        Tool("settings_get", "Read Martlet's settings (settings.json) or one part of them by path (snake_case, dots and [index], for " +
            "example generation, generation.temperature or companion.personas[0].name). Without a path it also lists the parts and " +
            "which of them settings_set may change. Returns the revision to pass to settings_set. Holds no API keys. " +
            DataDirectoryNote + " Read-only.", new
        {
            path = new { type = "string", maxLength = 256 },
            dataDirectory = new { type = "string" }
        }),
        Tool("settings_schema", "Show what a part of Martlet's settings can hold: the JSON schema of settings.json at path (property " +
            "names, types, allowed values; for example generation or memory), cut short below depth levels (default 2 for the whole " +
            "file, 4 for a path). Read-only.", new
        {
            path = new { type = "string", maxLength = 256 },
            depth = new { type = "integer", minimum = 1, maximum = 12 }
        }),
        Tool("settings_set", "Change one part of Martlet's settings: path (as for settings_get) and value (any JSON; null removes it, " +
            "which puts an optional value back to its default). Changes companion (personalities and profiles), generation (how " +
            "replies are made), prompts and memory; where Martlet thinks, listens and speaks, its API keys and devices change only " +
            "in the app, because they need the owner's consent. Checked by Martlet's own settings rules before it is saved; revision " +
            "(from settings_get) refuses the change if the settings changed since. A running Martlet follows by itself. Needs " +
            "--allow-changes. " + DataDirectoryNote, new
        {
            path = new { type = "string", maxLength = 256 },
            value = new { },
            revision = new { type = "string", maxLength = 128 },
            dataDirectory = new { type = "string" }
        }, ["path", "value"])
    ];

    /// <summary>The tools listed by default, in order, with a title and MCP's behavior hints. The rest are listed with
    /// --all-tools and always run by name (martlet_call, scripts\Invoke-MartletMcp.ps1).</summary>
    private static readonly (string Name, string Title, bool? ReadOnly, bool? Destructive, bool? Idempotent)[] Listed =
    [
        ("martlet_guide", "Start here: what Martlet's MCP server can do", true, null, true),
        ("martlet_call", "Run any Martlet tool by name", null, null, null),
        ("characters_list", "List Martlet's characters", true, null, true),
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

    private static readonly Lazy<JsonObject[]> Catalog = new(() =>
        [.. AssistantTools.Concat(Tools).Select(tool => JsonSerializer.SerializeToNode(tool)!.AsObject())]);

    private static readonly Lazy<JsonArray> ListedTools = new(() => new JsonArray([.. Listed.Select(entry => (JsonNode)Hinted(entry))]));

    private static readonly Lazy<JsonArray> EveryTool = new(() => new JsonArray([.. Catalog.Value.Select(tool =>
        Listed.FirstOrDefault(entry => entry.Name == (string)tool["name"]!) is { Name: not null } entry
            ? Hinted(entry) : (JsonNode)tool.DeepClone())]));

    private static JsonObject Hinted((string Name, string Title, bool? ReadOnly, bool? Destructive, bool? Idempotent) entry)
    {
        var tool = Catalog.Value.FirstOrDefault(t => (string)t["name"]! == entry.Name)?.DeepClone().AsObject()
            ?? throw new InvalidOperationException($"The listed tool {entry.Name} has no definition.");
        tool["title"] = entry.Title;
        var hints = new JsonObject { ["title"] = entry.Title };
        if (entry.ReadOnly is { } readOnly) hints["readOnlyHint"] = readOnly;
        if (entry.Destructive is { } destructive) hints["destructiveHint"] = destructive;
        if (entry.Idempotent is { } idempotent) hints["idempotentHint"] = idempotent;
        // The listed tools stay on this PC: they read and write Martlet's own files and windows.
        if (entry.Name != "martlet_call") hints["openWorldHint"] = false;
        tool["annotations"] = hints;
        return tool;
    }

    private JsonArray ToolList() => allTools ? EveryTool.Value : ListedTools.Value;

    private static bool IsTool(string name) => Catalog.Value.Any(tool => (string)tool["name"]! == name);

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
        "- Characters: characters_list, character_create (from a description or a SillyTavern/Chub character card), character_update, " +
        "character_use, character_delete.\n" +
        "- Settings: settings_get, settings_schema, settings_set (personalities, replies, prompts, memory).\n" +
        "- The running app: ui_connect, ui_snapshot, ui_click and the other ui_* tools.\n" +
        "- Diagnostics: doctor_status, doctor_list, doctor_run, logs_tail.\n" +
        (allTools ? "- Every tool is listed (--all-tools).\n"
            : $"- {Catalog.Value.Length - Listed.Length} more tools (status, checks and self-tests) are not listed; find them with " +
              "martlet_guide (search) and run them with martlet_call.\n") +
        "Tools use the current Windows user's Martlet data folder (%LOCALAPPDATA%\\Martlet) unless you pass dataDirectory.\n" +
        (allowChanges ? "Changes are on (--allow-changes): character_* and settings_set save to Martlet's settings, and a running Martlet follows them.\n"
            : "Changes are off: character_create/update/use/delete and settings_set need the server started with --allow-changes.\n") +
        (desktop.AllowsEffects ? "UI effects are on (--allow-ui-effects): ui_* tools may press buttons that change things.\n"
            : "UI effects are off: ui_* tools only navigate and read; buttons that change things need --allow-ui-effects.\n") +
        "Where Martlet thinks, listens and speaks, and its API keys, change only in the app with the owner, because they need the " +
        "owner's consent.";

    private object Guide(string? topic, string? search, string? tool)
    {
        if (tool is not null)
        {
            var found = Catalog.Value.FirstOrDefault(t => string.Equals((string)t["name"]!, tool.Trim(), StringComparison.OrdinalIgnoreCase))
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
            var matches = Catalog.Value
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
                toolsListed = allTools ? $"all {Catalog.Value.Length} (--all-tools)" : $"{Listed.Length} of {Catalog.Value.Length}; martlet_call runs the rest",
                dataDirectory = new { path = DefaultDataDirectory, exists = Directory.Exists(DefaultDataDirectory), note = DataDirectoryNote },
                runningApp = DesktopAutomation.RunningDesktops() is { Length: > 0 } running
                    ? new { running = true, processIds = running, note = "ui_connect connects to it." }
                    : new { running = false, processIds = Array.Empty<int>(), note = "Martlet isn't running in this Windows session. Saved changes apply when it starts." }
            },
            recipes = shownRecipes,
            areas = chosen == "tools"
                ? Catalog.Value.GroupBy(t => Area((string)t["name"]!)).OrderBy(g => AreaOrder(g.Key)).Select(g => new
                {
                    area = g.Key,
                    tools = g.Select(t => new
                    {
                        name = (string)t["name"]!, summary = Summary((string)t["description"]!),
                        listed = allTools || Listed.Any(e => e.Name == (string)t["name"]!)
                    }).ToArray()
                }).ToArray()
                : (object)Catalog.Value.GroupBy(t => Area((string)t["name"]!)).OrderBy(g => AreaOrder(g.Key))
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

    private static readonly string[] Areas =
        ["Start here", "Characters", "Settings", "The app's window", "Diagnostics and logs", "Voice and listening", "Thinking and models",
         "Computers, network and accounts", "Apps, home and memory", "Other"];

    private static int AreaOrder(string area) => Array.IndexOf(Areas, area) is var index and >= 0 ? index : Areas.Length;

    private static string Area(string name)
    {
        bool Starts(params string[] prefixes) => prefixes.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal));
        bool Has(params string[] parts) => parts.Any(part => name.Contains(part, StringComparison.Ordinal));
        if (Starts("martlet_")) return "Start here";
        if (Starts("ui_") || name is "character_touch" or "character_stroke" or "character_face" or "character_picture" or "character_pose" or
            "character_zones" or "character_mouth" or "character_look") return "The app's window";
        if (Starts("character", "f5_")) return "Characters";
        if (Starts("settings_", "prompts_", "api_keys_", "recommended_setup", "setup_run", "chattiness")) return "Settings";
        if (Starts("doctor_", "logs_", "latency_", "thinking_trace", "companion_status")) return "Diagnostics and logs";
        if (Has("voice", "parakeet", "hearing", "listening", "echo", "utterance", "barge_in", "turn_judge", "early_reply", "spoken_reply",
                "quick_sound", "singing", "song", "elevenlabs", "audio2face", "lip_sync", "speaking", "pc_audio", "sound_digest")) return "Voice and listening";
        if (Has("thinking", "think_longer", "model", "ollama", "sense_", "live_floor", "context", "research", "helper_jobs", "said_lately",
                "screen_digest", "vision", "active_app", "pc_activity", "reading", "pictures", "image")) return "Thinking and models";
        if (Has("host", "network", "node_", "cluster", "nearby", "account", "signin", "role_lab", "outside", "exposure", "api_selftest",
                "gpu_", "mac_host", "app_update", "virtualization", "pc_scope", "work_sharing", "household", "settings_sync")) return "Computers, network and accounts";
        if (Has("discord", "messaging", "terminal", "smart_home", "home_assistant", "reminders", "check_ins", "creations", "conversation_history",
                "memory", "mcp_")) return "Apps, home and memory";
        return "Other";
    }

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
            throw new InvalidOperationException($"{tool} changes Martlet, so this server must start with --allow-changes. Add " +
                "\"--allow-changes\" to the martlet server's args in your MCP client's configuration and restart it " +
                "(scripts\\Register-MartletMcp.ps1 adds it).");
        return await change();
    }

    private static JsonElement RequiredValue(JsonElement arguments, string property) =>
        arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty(property, out var value)
            ? value : throw new ArgumentException($"Missing '{property}' (use null to remove a value).");
}
