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
    /// area: their definitions, handlers and permissions, and the code only they use. An area whose code throws exceptions of its
    /// own projects names them here, so they come back as expected failures (<see cref="McpToolException"/>).</summary>
    private (string Area, McpTool[] Tools)[] ToolGroups() =>
    [
        ("Start here", StartTools()),
        // The character finders (character_find_looks, character_find_on_web, a look key in character_create) go online.
        ("Characters", Expecting(CharacterTools(), error => error is Martlet.Core.Settings.CharacterCardException or
            Martlet.Avatar.Hosting.VRoidHubException or Martlet.Conversation.WebResearchException)),
        ("Settings", SettingsTools()),
        // UI Automation fails when a window or control goes away while a tool reads or drives it.
        ("The app's window", Expecting(UiTools(), error => error is System.ComponentModel.Win32Exception or
            System.Runtime.InteropServices.COMException or System.Windows.Automation.ElementNotAvailableException)),
        ("Diagnostics and logs", DiagnosticsTools()),
        ("Voice and listening", VoiceTools()),
        ("Thinking and models", ThinkingTools()),
        ("Computers, network and accounts", NetworkTools()),
        ("Apps, home and memory", AppsTools()),
        ("Other", OtherTools())
    ];

    /// <summary>Reads JSON-RPC requests, one a line, and writes the responses. Tool calls run one after another, in the order they
    /// came, but the server keeps reading while one runs: ping and the other methods answer at once, and notifications/cancelled
    /// stops a running or waiting tool call (a cancelled call gets no response, as MCP says).</summary>
    internal async Task RunAsync(TextReader input, TextWriter output, CancellationToken cancellation)
    {
        using var writing = new SemaphoreSlim(1, 1);
        var calls = new System.Collections.Concurrent.ConcurrentDictionary<string, CancellationTokenSource>(StringComparer.Ordinal);
        var toolCalls = Task.CompletedTask;

        async Task WriteAsync(object response)
        {
            await writing.WaitAsync(CancellationToken.None);
            try
            {
                await output.WriteLineAsync(JsonSerializer.Serialize(response));
                await output.FlushAsync(CancellationToken.None);
            }
            finally { writing.Release(); }
        }

        async Task ToolCallAsync(Task previous, object id, string key, JsonElement parameters, CancellationTokenSource call)
        {
            try { await previous; } catch (Exception) { }
            try
            {
                if (call.IsCancellationRequested) return;
                object result;
                try { result = await CallAsync(parameters, call.Token); }
                catch (OperationCanceledException) when (call.IsCancellationRequested) { return; }
                catch (Exception ex) { result = ToolError(ex); }
                if (!call.IsCancellationRequested) await WriteAsync(Success(id, result));
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
            finally
            {
                calls.TryRemove(new KeyValuePair<string, CancellationTokenSource>(key, call));
                call.Dispose();
            }
        }

        try
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
                    JsonDocument document;
                    try { document = JsonDocument.Parse(line); }
                    catch (JsonException ex)
                    {
                        // Only a request line that is not JSON is a parse error; a tool's own JSON problems are tool errors.
                        await WriteAsync(Error(null, -32700, "Parse error: " + ex.Message));
                        continue;
                    }
                    using (document)
                    {
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
                        var parameters = request.TryGetProperty("params", out var value) ? value.Clone() : default;
                        if (id is null)
                        {
                            if (method.GetString() == "notifications/cancelled" && parameters.ValueKind == JsonValueKind.Object &&
                                parameters.TryGetProperty("requestId", out var cancelled) &&
                                calls.TryGetValue(cancelled.GetRawText(), out var running))
                            {
                                try { running.Cancel(); } catch (ObjectDisposedException) { }
                            }
                            continue;
                        }
                        if (method.GetString() == "tools/call")
                        {
                            var key = ((JsonElement)id).GetRawText();
                            var call = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                            calls[key] = call;
                            toolCalls = ToolCallAsync(toolCalls, id, key, parameters, call);
                            continue;
                        }
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
                            _ => Error(id, -32601, "Method not found.")
                        };
                    }
                }
                catch (ArgumentException ex) { response = Error(id, -32600, ex.Message); }
                catch (Exception ex) when (ex is not OperationCanceledException) { response = Error(id, -32603, "Internal error: " + ex.Message); }
                await WriteAsync(response);
            }
        }
        finally
        {
            // The calls already read still answer before the server ends (stdin closed).
            await toolCalls;
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
            var result = tool.Permission == ToolPermission.Changes || tool.Writes?.Invoke(arguments) == true
                ? await ChangingAsync(name, () => tool.Run(arguments, cancellation))
                : await tool.Run(arguments, cancellation);
            return new { content = new[] { new { type = "text", text = JsonSerializer.Serialize(result) } } };
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is not (OutOfMemoryException or InsufficientExecutionStackException)) { return ToolError(ex); }
    }

    /// <summary>A tool's failure as an MCP tool result (isError), so one bad call never ends the server. The exceptions the
    /// tools throw on purpose give their message; any other gives its type too.</summary>
    private static object ToolError(Exception ex)
    {
        var text = ex switch
        {
            OperationCanceledException or TimeoutException => "The tool took too long: " + ex.Message,
            _ when McpToolException.IsExpected(ex) => ex.Message,
            _ => $"{ex.GetType().Name}: {ex.Message}"
        };
        if (text.Length > 2000) text = text[..2000] + "...";
        return new { content = new[] { new { type = "text", text } }, isError = true };
    }

    /// <summary>Whether the call names a dataDirectory that is not the current user's own Martlet data folder: the only kind
    /// the diagnostic tools write FIXTURE data into.</summary>
    private static bool DisposableDataDirectory(JsonElement arguments)
    {
        if (OptionalString(arguments, "dataDirectory") is not { Length: > 0 } given || !Path.IsPathFullyQualified(given)) return false;
        static string Normal(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        return !string.Equals(Normal(given), Normal(DefaultDataDirectory), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Refuses an output file on another computer (a \\server\share path or a network drive) or one that exists, so a
    /// tool only adds a new file on this PC.</summary>
    internal static void RequireLocalNewFile(string path, string name)
    {
        var full = Path.GetFullPath(path);
        if (full.StartsWith(@"\\", StringComparison.Ordinal) || full.StartsWith("//", StringComparison.Ordinal))
            throw new ArgumentException($"{name} must be on this PC, not a network path.");
        if (Path.GetPathRoot(full) is { Length: > 0 } root)
        {
            DriveType type;
            try { type = new DriveInfo(root).DriveType; }
            catch (ArgumentException) { type = DriveType.Unknown; }
            if (type == DriveType.Network) throw new ArgumentException($"{name} must be on this PC, not a network drive.");
        }
        if (File.Exists(full) || Directory.Exists(full)) throw new ArgumentException($"{name} already exists; choose a new file name.");
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
        int Replaced = 0, int WithSearchWords = 0, object? Where = null);

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
