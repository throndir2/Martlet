using System.Text.Json;

namespace Martlet.Mcp;

/// <summary>What a tool needs before it may change something.</summary>
internal enum ToolPermission
{
    /// <summary>Runs as it is: it reads, or it rehearses on fixtures.</summary>
    None,

    /// <summary>Saves Martlet's characters or settings, or writes a file: <see cref="McpServer"/> refuses it unless the server
    /// started with --allow-changes.</summary>
    Changes,

    /// <summary>Deletes only with --allow-changes, but its preview (preview: true) runs without it. The tool checks this
    /// itself.</summary>
    ChangesUnlessPreview,

    /// <summary>Drives the running desktop: a control or an action that changes something needs --allow-ui-effects.
    /// DesktopAutomation checks each one (<see cref="AutomationPolicy"/> lists the controls that are safe without it).</summary>
    UiEffects
}

/// <summary>One MCP tool: its name, description and input schema (as tools/list shows them), what it needs before it may change
/// something, and the code that runs it with the call's arguments.</summary>
internal sealed record McpTool(string Name, string Description, object Properties, string[] Required, ToolPermission Permission,
    Func<JsonElement, CancellationToken, Task<object>> Run)
{
    /// <summary>For a tool that only sometimes writes (a save or forget argument, an output file): whether this call's arguments
    /// make it write. When it does, <see cref="McpServer"/> refuses the call unless the server started with --allow-changes, as
    /// for <see cref="ToolPermission.Changes"/>.</summary>
    internal Func<JsonElement, bool>? Writes { get; init; }

    /// <summary>The tool as tools/list shows it.</summary>
    internal object Definition => new
    {
        name = Name, description = Description,
        inputSchema = new { type = "object", properties = Properties, required = Required, additionalProperties = false }
    };
}

internal sealed partial class McpServer
{
    // Tool(...) registers one tool in Tools\<Area>Tools.cs. The handler gets the call's arguments (an object, or undefined when
    // none were given); an async handler also gets the call's cancellation.

    private static McpTool Tool(string name, string description, object properties, Func<JsonElement, object> run,
        ToolPermission permission = ToolPermission.None) =>
        Tool(name, description, properties, [], run, permission);

    private static McpTool Tool(string name, string description, object properties, string[] required, Func<JsonElement, object> run,
        ToolPermission permission = ToolPermission.None) =>
        new(name, description, properties, required, permission, (arguments, _) => Task.FromResult(run(arguments)));

    private static McpTool Tool(string name, string description, object properties,
        Func<JsonElement, CancellationToken, Task<object>> run, ToolPermission permission = ToolPermission.None) =>
        new(name, description, properties, [], permission, run);

    private static McpTool Tool(string name, string description, object properties, string[] required,
        Func<JsonElement, CancellationToken, Task<object>> run, ToolPermission permission = ToolPermission.None) =>
        new(name, description, properties, required, permission, run);
}
