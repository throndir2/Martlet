using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Martlet.Core.Installation;
using Martlet.Doctor;
using Martlet.Mcp.Client;

namespace Martlet.Mcp;

/// <summary>martlet_guide's "Start here" area: where an assistant starts: the guide, and running any tool by name. Each
/// Tool(...) registers one tool: its name, description, input schema, handler and, when it needs one, its permission (<see
/// cref="ToolPermission"/>). A new tool of this area goes here.</summary>
internal sealed partial class McpServer
{
    private McpTool[] StartTools() =>
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
        },
            arguments => Guide(OptionalString(arguments, "topic"), OptionalString(arguments, "search"), OptionalString(arguments, "tool"))),
        Tool("martlet_call", "Run any Martlet MCP tool by name with its arguments, including the diagnostic and self-test tools " +
            "that are not in the tool list. Find them and their arguments with martlet_guide (search, or tool for one tool's " +
            "schema). Returns that tool's result.", new
        {
            tool = new { type = "string", maxLength = 64 },
            arguments = new { type = "object" }
        }, ["tool"],
            async (arguments, cancellation) => await CallByNameAsync(arguments, cancellation))
    ];
}
