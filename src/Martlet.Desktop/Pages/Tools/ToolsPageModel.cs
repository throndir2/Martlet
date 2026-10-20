using Martlet.Core.Settings;
using Martlet.Mcp.Client;

namespace Martlet.Desktop;

/// <summary>Companion › Tools' decisions, without WPF. The page makes one each time it draws: it reads the servers' state and the
/// terminal settings once and says what the terminal's status, each server's state and the log lines show. <see cref="ToolsPage"/>
/// draws it. <see cref="TerminalStatus"/> is also the Now card's terminal problem (MainWindow.PageNow.cs).</summary>
internal sealed class ToolsPageModel
{
    internal ToolsPageModel(McpToolService service, SetupRoute? thinking)
    {
        Statuses = service.Started ? service.Hub.Status.ToDictionary(s => s.Name, StringComparer.Ordinal) : new Dictionary<string, McpServerStatus>(StringComparer.Ordinal);
        Terminal = service.Terminal;
        TerminalState = TerminalStatus(service, Terminal, thinking);
    }

    /// <summary>Each started server's state, by name (empty before the servers start).</summary>
    internal IReadOnlyDictionary<string, McpServerStatus> Statuses { get; }
    /// <summary>The terminal settings, as saved.</summary>
    internal TerminalSettings Terminal { get; }
    /// <summary>The terminal's state in words (ToolsTerminalStatus), and whether it is a problem that keeps it from working.</summary>
    internal (string Text, bool Problem) TerminalState { get; }

    /// <summary>The terminal's state in words, and whether it is a problem that keeps it from working.</summary>
    internal static (string Text, bool Problem) TerminalStatus(McpToolService service, TerminalSettings saved, SetupRoute? route)
    {
        if (!saved.Enabled) return ("Off: Martlet can't run commands on this PC.", false);
        var shell = TerminalRunner.Name(saved.Shell);
        if (TerminalRunner.Find(saved.Shell) is null) return ($"On, but {shell} isn't installed on this PC, so choose another shell.", true);
        if (!System.IO.Directory.Exists(saved.StartFolder)) return ("On, but the start folder doesn't exist any more, so choose another.", true);
        if (route is null) return ("On, but set up Thinking so Martlet can use it.", true);
        if (route.RouteType is not (SetupRouteType.OpenAi or SetupRouteType.ChatCompletions))
            return ("On, but your Thinking model can't use tools here: use OpenAI or a Chat Completions endpoint (such as Ollama on " +
                "this PC) in Companion › Thinking.", true);
        if (service.IsUnsupported(McpToolService.ModelKey($"{route.RouteType}", route.Origin, route.ModelId)))
            return ($"On, but {route.ModelId} turned down tools, so Martlet stops offering them to it for a week: choose a model " +
                "that can use tools.", true);
        return ($"On: {shell}, {(saved.AskFirst ? "asks before every command" : "runs commands without asking")}, stops a command " +
            $"after {TimeLimit(saved.TimeLimitSeconds)}.", false);
    }

    /// <summary>A shell in the Shell list: its name, and whether it is missing on this PC.</summary>
    internal static string ShellName(TerminalShell shell) => TerminalRunner.Name(shell) + (TerminalRunner.Find(shell) is null ? " (not installed)" : "");

    /// <summary>A time limit in words.</summary>
    internal static string TimeLimit(int seconds) => seconds < 60 ? $"{seconds} seconds" : "1 minute";

    /// <summary>Where terminal commands start, in words.</summary>
    internal static string Folder(TerminalSettings saved) => saved.CustomFolder ? saved.Folder : $"Your home folder ({TerminalSettings.HomeFolder})";

    /// <summary>The action line after the terminal settings changed from <paramref name="before"/> to <paramref name="next"/>.</summary>
    internal static string Saved(TerminalSettings before, TerminalSettings next) =>
        next.Enabled != before.Enabled
            ? next.Enabled
                ? next.AskFirst ? "The terminal is on, and Martlet asks before each command." : "The terminal is on, and commands run without asking."
                : "The terminal is off."
            : next.AskFirst != before.AskFirst
                ? next.AskFirst ? "Martlet asks before every terminal command." : "Terminal commands run without asking."
                : "Terminal settings saved.";

    /// <summary>A server's state line (ToolsServerState-&lt;name&gt;).</summary>
    internal static string ServerState(McpServerDefinition server, McpServerStatus? status) =>
        server.Disabled ? "Off."
            : status is null ? "Not started yet: it starts when you open a talk window or press Start servers now."
            : status.State switch
            {
                McpServerState.Starting => "Starting... first start may take a minute.",
                McpServerState.Ready => $"Running{(status.ServerName is { } name && name != server.Name ? $" ({name})" : "")}, " +
                    (status.Tools.Count == 1 ? "1 tool." : $"{status.Tools.Count} tools."),
                McpServerState.Failed => "Stopped: " + status.Error,
                _ => "Off."
            };

    /// <summary>Where a server came from in the MCP directory, and where its secrets are kept, or null when it isn't from there.</summary>
    internal static string? FromDirectory(McpServerDefinition server) => server.Registry is not { } registry ? null
        : $"From the MCP directory: {registry}{(server.RegistryVersion is { } version ? $" {version}" : "")}" +
            (server.Secrets.Count > 0 ? $"; {server.Secrets.Count} secret value{(server.Secrets.Count == 1 ? " is" : "s are")} kept in " +
                "Windows Credential Manager." : ".");

    /// <summary>One line of Recent tool use.</summary>
    internal static string LogLine(ToolActivity entry) =>
        $"{entry.At.LocalDateTime:t}  {entry.Server} › {entry.Tool}: {entry.Outcome}" + (entry.Arguments.Length > 0 ? $"  {entry.Arguments}" : "");
}
