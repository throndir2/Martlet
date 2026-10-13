using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Martlet.Core.Installation;
using Martlet.Doctor;
using Martlet.Mcp.Client;

namespace Martlet.Mcp;

/// <summary>Martlet's local MCP server. <paramref name="allowChanges"/> (--allow-changes) lets the character and settings tools
/// save; <paramref name="allTools"/> (--all-tools) lists every tool instead of the short list for assistants (every tool runs by
/// name either way).</summary>
internal sealed partial class McpServer(DesktopAutomation desktop, bool allowChanges = false, bool allTools = false)
{
    private const int MaxLineLength = 1024 * 1024;

    /// <summary>Every tool, by martlet_guide area, in the order of the guide's areas. Tools\<Area>Tools.cs holds the tools of one
    /// area: their definitions, handlers and permissions, and the code only they use.</summary>
    private (string Area, McpTool[] Tools)[] ToolGroups() =>
    [
        ("Start here", StartTools()),
        ("Characters", CharacterTools()),
        ("Settings", SettingsTools()),
        ("The app's window", UiTools()),
        ("Diagnostics and logs", DiagnosticsTools()),
        ("Voice and listening", VoiceTools()),
        ("Thinking and models", ThinkingTools()),
        ("Computers, network and accounts", NetworkTools()),
        ("Apps, home and memory", AppsTools()),
        ("Other", OtherTools())
    ];

    internal async Task RunAsync(TextReader input, TextWriter output, CancellationToken cancellation)
    {
        while (!cancellation.IsCancellationRequested)
        {
            var line = await input.ReadLineAsync(cancellation);
            if (line is null) break;
            object? id = null;
            object response;
            try
            {
                if (line.Length > MaxLineLength) throw new ArgumentException("Request exceeds 1 MiB.");
                using var document = JsonDocument.Parse(line);
                var request = document.RootElement;
                if (request.ValueKind != JsonValueKind.Object ||
                    !request.TryGetProperty("jsonrpc", out var version) ||
                    version.ValueKind != JsonValueKind.String || version.GetString() != "2.0" ||
                    !request.TryGetProperty("method", out var method) || method.ValueKind != JsonValueKind.String)
                    throw new ArgumentException("Invalid JSON-RPC request.");
                if (request.TryGetProperty("id", out var requestId))
                {
                    if (requestId.ValueKind is not (JsonValueKind.String or JsonValueKind.Number))
                        throw new ArgumentException("Invalid JSON-RPC request ID.");
                    id = requestId.Clone();
                }
                if (id is null) continue;
                var parameters = request.TryGetProperty("params", out var value) ? value : default;
                response = method.GetString() switch
                {
                    "initialize" => Success(id, new
                    {
                        protocolVersion = "2025-06-18",
                        capabilities = new { tools = new { } },
                        serverInfo = new { name = "martlet", title = "Martlet", version = Version },
                        instructions = Instructions()
                    }),
                    "ping" => Success(id, new { }),
                    "tools/list" => Success(id, new { tools = ToolList() }),
                    "tools/call" => Success(id, await CallAsync(parameters, cancellation)),
                    _ => Error(id, -32601, "Method not found.")
                };
            }
            catch (JsonException ex) { response = Error(id, -32700, ex.Message); }
            catch (ArgumentException ex) { response = Error(id, -32600, ex.Message); }
            await output.WriteLineAsync(JsonSerializer.Serialize(response));
            await output.FlushAsync(cancellation);
        }
    }

    private async Task<object> CallAsync(JsonElement parameters, CancellationToken cancellation)
    {
        try
        {
            var name = RequiredString(parameters, "name");
            var arguments = parameters.TryGetProperty("arguments", out var value) ? value : default;
            var tool = FindTool(name) ?? throw new ArgumentException($"Unknown tool '{name}'.");
            // martlet_call's result is already the result of the tool it ran.
            if (tool.Name == "martlet_call") return await tool.Run(arguments, cancellation);
            var result = tool.Permission == ToolPermission.Changes
                ? await ChangingAsync(name, () => tool.Run(arguments, cancellation))
                : await tool.Run(arguments, cancellation);
            return new { content = new[] { new { type = "text", text = JsonSerializer.Serialize(result) } } };
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or
            System.ComponentModel.Win32Exception or System.Runtime.InteropServices.COMException or
            System.Windows.Automation.ElementNotAvailableException or Martlet.Core.Contracts.ContractException or
            Martlet.Core.Settings.CharacterCardException or IOException or UnauthorizedAccessException or
            // The character finders (character_find_looks, character_find_on_web, a look key in character_create) go online.
            System.Net.Http.HttpRequestException or Martlet.Avatar.Hosting.VRoidHubException or Martlet.Conversation.WebResearchException ||
            ex is TaskCanceledException && !cancellation.IsCancellationRequested)
        {
            return new { content = new[] { new { type = "text", text = ex is TaskCanceledException ? "A web request took too long." : ex.Message } }, isError = true };
        }
    }

    /// <summary>The optional absolute speechDirectory argument (where Parakeet is downloaded), or the current user's.</summary>
    private static string SpeechDirectory(JsonElement arguments)
    {
        var speech = OptionalString(arguments, "speechDirectory") ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Martlet", "speech");
        if (!Path.IsPathFullyQualified(speech)) throw new ArgumentException("speechDirectory must be an absolute path.");
        return speech;
    }

    /// <summary>The optional absolute martletDirectory argument (a folder with Martlet.Desktop.exe), or the installed release's.</summary>
    private static string MartletDirectory(JsonElement arguments)
    {
        var martlet = OptionalString(arguments, "martletDirectory") ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Martlet", "Desktop");
        if (!Path.IsPathFullyQualified(martlet)) throw new ArgumentException("martletDirectory must be an absolute path.");
        return martlet;
    }

    /// <summary>Runs Martlet.NodeLinkCheck (built next to this server, in the same configuration) with <paramref name="arguments"/>
    /// and returns its JSON report. A separate process, because the in-process gateway needs the ASP.NET Core runtime and this
    /// server does not.</summary>
    private static Task<object> NodeLinkCheckAsync(CancellationToken cancellation, params string[] arguments) =>
        NodeLinkCheckAsync(TimeSpan.FromMinutes(2), cancellation, arguments);

    private static string NodeLinkCheckProgram()
    {
        var output = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
        var configuration = output.Parent?.Name ?? "Release";
        var source = output.Parent?.Parent?.Parent?.Parent?.FullName
            ?? throw new InvalidOperationException("Run node_link_check from a Martlet source checkout's build.");
        var program = Path.Combine(source, "Martlet.NodeLinkCheck", "bin", configuration, "net10.0", "Martlet.NodeLinkCheck.exe");
        if (!File.Exists(program))
            throw new InvalidOperationException($"Build src\\Martlet.NodeLinkCheck ({configuration}) first; building Martlet.Mcp builds it too.");
        return program;
    }

    private static async Task<object> NodeLinkCheckAsync(TimeSpan timeLimit, CancellationToken cancellation, string[] arguments)
    {
        var (exitCode, report) = await NodeLinkCheckReportAsync(timeLimit, cancellation, arguments);
        return new { exitCode, report };
    }

    /// <summary>Runs Martlet.NodeLinkCheck with <paramref name="arguments"/> and returns its exit code and JSON report.</summary>
    internal static async Task<(int ExitCode, JsonElement Report)> NodeLinkCheckReportAsync(TimeSpan timeLimit, CancellationToken cancellation,
        string[] arguments)
    {
        var program = NodeLinkCheckProgram();
        var start = new System.Diagnostics.ProcessStartInfo(program)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = System.Diagnostics.Process.Start(start) ?? throw new InvalidOperationException("Could not start Martlet.NodeLinkCheck.");
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        limit.CancelAfter(timeLimit);
        var report = process.StandardOutput.ReadToEndAsync(limit.Token);
        var errors = process.StandardError.ReadToEndAsync(limit.Token);
        try { await process.WaitForExitAsync(limit.Token); }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new InvalidOperationException($"Martlet.NodeLinkCheck did not finish within {timeLimit.TotalMinutes:0} minutes.");
        }
        var text = (await report).Trim();
        try
        {
            using var document = JsonDocument.Parse(text);
            return (process.ExitCode, document.RootElement.Clone());
        }
        catch (JsonException)
        {
            throw new InvalidOperationException($"Martlet.NodeLinkCheck exited {process.ExitCode} without a report: {(await errors).Trim()}");
        }
    }

    /// <summary>The optional absolute dataDirectory argument, or the current user's Martlet directory.</summary>
    private static string DataDirectory(JsonElement arguments)
    {
        var directory = arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty("dataDirectory", out var given)
            ? given.GetString() ?? throw new ArgumentException("Invalid data directory.")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Martlet");
        if (!Path.IsPathFullyQualified(directory)) throw new ArgumentException("dataDirectory must be an absolute path.");
        return directory;
    }

    /// <summary>An optional array of strings (at most 128), or null when it isn't given.</summary>
    private static string[]? OptionalStrings(JsonElement arguments, string name) =>
        arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty(name, out var listed) && listed.ValueKind == JsonValueKind.Array
            ? listed.EnumerateArray().Where(t => t.ValueKind == JsonValueKind.String).Select(t => t.GetString()!).Take(128).ToArray()
            : null;

    /// <summary>Speech breaks as MCP reports them (Personality › Where the voice pauses).</summary>
    internal static object Breaks(Martlet.Core.Settings.SpeechBreaks breaks) => new
    {
        periods = breaks.Periods, questionMarks = breaks.QuestionMarks,
        exclamationMarks = breaks.ExclamationMarks, shortEndingWords = breaks.ShortEndingWords, isDefault = breaks.IsDefault
    };

    private sealed record MemoryCounts(string State, int Facts = 0, int Typed = 0, int FromConversation = 0, int Expiring = 0, object? Whose = null,
        int Replaced = 0, int WithSearchWords = 0);

    /// <summary>Whether a hosts.json entry is a host a friend shares with that desktop (Martlet.Desktop's PairedHost.Access).</summary>
    internal static bool IsSharedHost(JsonElement host) =>
        host.TryGetProperty("access", out var access) && access.ValueKind == JsonValueKind.String && access.GetString() == "friend";

    private static string RequiredString(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(value.GetString()))
            throw new ArgumentException($"Missing string '{property}'.");
        return value.GetString()!;
    }

    private static int RequiredInt(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(property, out var value) || !value.TryGetInt32(out var number))
            throw new ArgumentException($"Missing integer '{property}'.");
        return number;
    }

    private static double RequiredDouble(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var value) ||
            value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number))
            throw new ArgumentException($"Missing number '{property}'.");
        return number;
    }

    private static string? OptionalString(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var value) ||
            value.ValueKind == JsonValueKind.Null)
            return null;
        if (value.ValueKind != JsonValueKind.String) throw new ArgumentException($"'{property}' must be a string.");
        return value.GetString();
    }

    private static JsonElement? OptionalObject(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var value) ||
            value.ValueKind == JsonValueKind.Null)
            return null;
        if (value.ValueKind != JsonValueKind.Object) throw new ArgumentException($"'{property}' must be an object.");
        return value.Clone();
    }

    private static double? OptionalDouble(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var value) ||
            value.ValueKind == JsonValueKind.Null)
            return null;
        return value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number)
            ? number : throw new ArgumentException($"'{property}' must be a number.");
    }

    private static int? OptionalInt(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var value) ||
            value.ValueKind == JsonValueKind.Null)
            return null;
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            ? number : throw new ArgumentException($"'{property}' must be an integer.");
    }

    private static bool? OptionalBool(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var value) ||
            value.ValueKind == JsonValueKind.Null)
            return null;
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new ArgumentException($"'{property}' must be a boolean.")
        };
    }

    private static string[] RequiredStrings(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.Array ||
            value.GetArrayLength() == 0 || value.GetArrayLength() > 128)
            throw new ArgumentException($"Missing nonempty array '{property}'.");
        return value.EnumerateArray().Select(item =>
            item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString())
                ? item.GetString()! : throw new ArgumentException($"Invalid '{property}' item.")).ToArray();
    }

    private static object Success(object id, object result) => new { jsonrpc = "2.0", id, result };
    private static object Error(object? id, int code, string message) =>
        new { jsonrpc = "2.0", id, error = new { code, message } };
}
