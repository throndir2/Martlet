using System.IO;
using System.Text.Json;
using Martlet.Doctor;

namespace Martlet.Mcp;

internal sealed class McpServer(DesktopAutomation desktop)
{
    private const int MaxLineLength = 1024 * 1024;
    private static readonly object[] Tools =
    [
        Tool("doctor_status", "Read local diagnostic status without starting audio or network.", new
        {
            dataDirectory = new { type = "string" }
        }),
        Tool("doctor_list", "List available local read-only probes.", new
        {
            dataDirectory = new { type = "string" }
        }),
        Tool("doctor_run", "Run selected local read-only probes.", new
        {
            probes = new { type = "array", items = new { type = "string" }, minItems = 1 },
            dataDirectory = new { type = "string" }
        }, ["probes"]),
        Tool("fixture", "Run an offline scripted fixture with audio OFF; this is NOT AI.", new
        {
            scenario = new { type = "string", @enum = new[]
            {
                "complete", "streaming", "refused", "refused-after-partial", "no-speech",
                "not-addressed", "canceled", "truncated", "slow", "failed"
            } },
            dataDirectory = new { type = "string" }
        }, ["scenario"]),
        Tool("ui_connect", "Attach to an already-running Martlet.Desktop process in this interactive session.", new
        {
            pid = new { type = "integer", minimum = 1 }
        }, ["pid"]),
        Tool("ui_snapshot", "Inspect automation IDs, enabled state and selected non-secret status fields of attached Martlet windows.", new { }),
        Tool("ui_click", "Invoke an automation-ID control. Only safe fixture/navigation controls work without --allow-ui-effects.", new
        {
            id = new { type = "string" }
        }, ["id"]),
        Tool("ui_select", "Select a named option from a combo box. Only FixtureScenario works without --allow-ui-effects.", new
        {
            id = new { type = "string" }, item = new { type = "string" }
        }, ["id", "item"]),
        Tool("ui_set_text", "Enter text into an editable control (requires --allow-ui-effects).", new
        {
            id = new { type = "string" }, text = new { type = "string" }
        }, ["id", "text"]),
        Tool("ui_toggle", "Toggle an enabled checkbox (requires --allow-ui-effects).", new
        {
            id = new { type = "string" }
        }, ["id"])
    ];

    private static object Tool(string name, string description, object properties, string[]? required = null) =>
        new { name, description, inputSchema = new { type = "object", properties, required = required ?? [], additionalProperties = false } };

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
                        serverInfo = new { name = "martlet", version = "0.1.0" }
                    }),
                    "ping" => Success(id, new { }),
                    "tools/list" => Success(id, new { tools = Tools }),
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
            object result = name switch
            {
                "doctor_status" => await DoctorAsync(["status", "--json"], arguments, cancellation),
                "doctor_list" => await DoctorAsync(["list", "--json"], arguments, cancellation),
                "doctor_run" => await DoctorAsync(
                    ["run", .. RequiredStrings(arguments, "probes"), "--json"], arguments, cancellation),
                "fixture" => await DoctorAsync(
                    ["self-test", "--scenario", RequiredString(arguments, "scenario"), "--json"], arguments, cancellation),
                "ui_connect" => desktop.Connect(RequiredInt(arguments, "pid")),
                "ui_snapshot" => desktop.Snapshot(),
                "ui_click" => await desktop.ClickAsync(RequiredString(arguments, "id")),
                "ui_select" => desktop.Select(RequiredString(arguments, "id"), RequiredString(arguments, "item")),
                "ui_set_text" => desktop.SetText(RequiredString(arguments, "id"), RequiredString(arguments, "text")),
                "ui_toggle" => desktop.Toggle(RequiredString(arguments, "id")),
                _ => throw new ArgumentException($"Unknown tool '{name}'.")
            };
            return new { content = new[] { new { type = "text", text = JsonSerializer.Serialize(result) } } };
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or
            System.ComponentModel.Win32Exception or System.Runtime.InteropServices.COMException or
            System.Windows.Automation.ElementNotAvailableException)
        {
            return new { content = new[] { new { type = "text", text = ex.Message } }, isError = true };
        }
    }

    private static async Task<object> DoctorAsync(string[] args, JsonElement arguments, CancellationToken cancellation)
    {
        if (arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty("dataDirectory", out var directory))
            args = [.. args, "--data-directory", directory.GetString() ?? throw new ArgumentException("Invalid data directory.")];
        using var output = new StringWriter();
        var exitCode = await DoctorCommand.RunAsync(args, output, cancellation);
        using var document = JsonDocument.Parse(output.ToString());
        return new { exitCode, report = document.RootElement.Clone() };
    }

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
