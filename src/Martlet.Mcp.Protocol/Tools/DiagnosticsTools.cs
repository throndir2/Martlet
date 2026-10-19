using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Martlet.Core.Installation;
using Martlet.Doctor;
using Martlet.Mcp.Client;

namespace Martlet.Mcp;

/// <summary>martlet_guide's "Diagnostics and logs" area: Doctor, the logs, latency and the Thinking trace. Each Tool(...)
/// registers one tool: its name, description, input schema, handler and, when it needs one, its permission (<see
/// cref="ToolPermission"/>). A new tool of this area goes here.</summary>
internal sealed partial class McpServer
{
    private McpTool[] DiagnosticsTools() =>
    [
        Tool("companion_status", "Martlet for Linux and macOS (src/Martlet.Companion), headless: runs its --status and returns what it " +
            "detected about the computer (OS, architecture, Apple silicon vs Intel, NVIDIA, Wayland/X11), which platform services " +
            "work, and the platform-catalog guardrails (engines offered per job, the rest with the catalog's reason, local-model " +
            "warnings). platform simulates linux-x64, linux-nvidia, linux-arm64, macos-arm64 or macos-x64 from this computer; " +
            "settingsFile checks a settings.json from another device against it and returns what is refused and why. No audio, " +
            "network or window. Build src/Martlet.Companion first (building Martlet.Mcp builds it).", new
        {
            platform = new { type = "string", @enum = new[] { "linux-x64", "linux-nvidia", "linux-arm64", "macos-arm64", "macos-x64" } },
            settingsFile = new { type = "string" }
        },
            async (arguments, cancellation) => await CompanionStatusAsync(OptionalString(arguments, "platform"), OptionalString(arguments, "settingsFile"), cancellation)),
        Tool("doctor_status", "Read local diagnostic status without starting audio or network.", new
        {
            dataDirectory = new { type = "string" }
        },
            async (arguments, cancellation) => await DoctorAsync(["status", "--json"], arguments, cancellation)),
        Tool("doctor_list", "List available local read-only probes.", new
        {
            dataDirectory = new { type = "string" }
        },
            async (arguments, cancellation) => await DoctorAsync(["list", "--json"], arguments, cancellation)),
        Tool("doctor_run", "Run selected local read-only probes.", new
        {
            probes = new { type = "array", items = new { type = "string" }, minItems = 1 },
            dataDirectory = new { type = "string" }
        }, ["probes"],
            async (arguments, cancellation) => await DoctorAsync(
                ["run", .. RequiredStrings(arguments, "probes"), "--json"], arguments, cancellation)),
        Tool("logs_tail", "Read the last lines of a local Martlet log (desktop, avatar-renderer or host-runs), optionally only lines " +
            "containing some text. Failed provider requests appear in the desktop log with their HTTP status and the provider's " +
            "own short explanation. Read-only; logs can include local paths and provider error text, never keys.", new
        {
            log = new { type = "string", @enum = LogTail.Logs },
            lines = new { type = "integer", minimum = 1, maximum = LogTail.MaximumLines },
            contains = new { type = "string", maxLength = LogTail.MaximumFilterLength },
            dataDirectory = new { type = "string" }
        },
            arguments => LogTail.Read(OptionalString(arguments, "dataDirectory"), OptionalString(arguments, "log"),
                OptionalInt(arguments, "lines"), OptionalString(arguments, "contains"))),
        Tool("logs_timeline", "Read the logs as the desktop's Diagnostics page shows them: this PC's desktop, avatar-renderer and host-runs " +
            "(with rotated copies) and the other computers' lines log sharing collected (network-logs.json: other desktops and every host's " +
            "gateway), merged into one timeline of {at, level, source, component, seq, relayedBy, message}, newest first, with counts of " +
            "errors and warnings per component and source. Filters: level (all, warnings, errors), component, source (a computer's ID), " +
            "contains. Read-only; contacts no host.", new
        {
            level = new { type = "string", @enum = LogTimeline.Levels },
            component = new { type = "string", @enum = LogTimeline.Components },
            source = new { type = "string", maxLength = 64 },
            contains = new { type = "string", maxLength = LogTail.MaximumFilterLength },
            lines = new { type = "integer", minimum = 1, maximum = LogTimeline.MaximumLines },
            dataDirectory = new { type = "string" }
        },
            arguments => LogTimeline.Read(OptionalString(arguments, "dataDirectory"), OptionalString(arguments, "level"),
                OptionalString(arguments, "component"), OptionalString(arguments, "contains"), OptionalInt(arguments, "lines"),
                OptionalString(arguments, "source"))),
        Tool("logs_export", "Diagnostics > Save logs to share, from a data directory: every log line it has (this PC's logs and the other " +
            "computers' lines log sharing collected) saved in one new ZIP at outputPath (absolute, ending .zip, in an existing folder; an " +
            "existing file is never replaced; a network path is refused) with about.txt (where and when, computers and parts, what logs can contain) and " +
            "martlet-logs.txt (every line once, oldest first). Returns the files, line, computer, error and warning counts and about.txt's " +
            "text. Writes only that file, so it needs --allow-changes; contacts nothing.", new
        {
            outputPath = new { type = "string" },
            dataDirectory = new { type = "string" }
        }, ["outputPath"],
            arguments => LogTimeline.Export(OptionalString(arguments, "dataDirectory"), RequiredString(arguments, "outputPath")),
            ToolPermission.Changes),
        Tool("logs_share_selftest", "Rehearse shared logs (Diagnostics: every computer's logs on every computer) end to end with the " +
            "production code: two real gateways on 127.0.0.1 (pinned TLS, signed requests, in-memory logs.json) and three simulated " +
            "desktops with real log folders, the desktop's paired client and the real log sharing engine (Martlet.Core.Logs.LogShare). " +
            "Walks a desktop's lines reaching both hosts, a desktop that reaches only one host whose lines still reach the other, each " +
            "host's own gateway lines reaching the other host and every desktop, every line kept once however often it is delivered, a " +
            "host that was down catching up, a host that lost its newest lines in a power cut getting them back, the copy of everyone's " +
            "lines surviving a restart, Save logs to share holding every computer " +
            "and an unsigned request refused. Synthetic lines only; loopback only; the folder is deleted.", new { },
            async (_, cancellation) => await NodeLinkCheckAsync(cancellation, "logs")),
        Tool("latency_report", "Summarize voice latency from the desktop log's reply latency lines: for the newest replies, how long " +
            "from when you stopped talking (or sent your message) to the first audio, each step's milliseconds (end of speech, " +
            "speech-to-text, preparing, Thinking connection, hidden reasoning, first sentence, voice synthesis, speakers...), the " +
            "median and 90th percentile of the total and of each step, the slowest steps and the models used. Read-only; starts " +
            "no audio, network or provider request.", new
        {
            replies = new { type = "integer", minimum = 1, maximum = LatencyReport.MaximumReplies },
            dataDirectory = new { type = "string" }
        },
            arguments => LatencyReport.Read(OptionalString(arguments, "dataDirectory"), OptionalInt(arguments, "replies"))),
        Tool("thinking_trace", "Read the desktop log's Thinking trace (docs/VOICE_LATENCY.md, Thinking trace) put back together: the " +
            "newest conversation turns (replies, glances, background thinks, Thinking pool and sense jobs, Discord replies), newest " +
            "first, each with its route and model, state, total, first words and first audio (ms from the turn's start), how many " +
            "requests it sent, every still-waiting notice (what it waited for and how long) and its lines in order (started, prepared, " +
            "each request with its authorization, send, response, hidden reasoning, first words and tokens, tool rounds, Backup " +
            "Thinking, ended); and the newest requests for the Thinking pool's slots (tr-N) with how long each waited in line and ran " +
            "and their lines. contains keeps only turns and requests with a line containing that text. Read-only; never what was said; " +
            "starts no audio, network or provider request.", new
        {
            turns = new { type = "integer", minimum = 1, maximum = ThinkingTraceReport.MaximumTurns },
            contains = new { type = "string", maxLength = LogTail.MaximumFilterLength },
            dataDirectory = new { type = "string" }
        },
            arguments => ThinkingTraceReport.Read(OptionalString(arguments, "dataDirectory"), OptionalInt(arguments, "turns"),
                OptionalString(arguments, "contains")))
    ];

    /// <summary>companion_status: Martlet.Companion --status [--as platform] [--import file] from this checkout's build.</summary>
    private static async Task<object> CompanionStatusAsync(string? platform, string? settingsFile, CancellationToken cancellation)
    {
        if (platform is not null && platform is not ("linux-x64" or "linux-nvidia" or "linux-arm64" or "macos-arm64" or "macos-x64"))
            throw new ArgumentException("platform is linux-x64, linux-nvidia, linux-arm64, macos-arm64 or macos-x64.");
        if (settingsFile is not null && !File.Exists(settingsFile))
            throw new ArgumentException("settingsFile must be an existing settings.json.");
        var output = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
        var configuration = output.Parent?.Name ?? "Release";
        var source = output.Parent?.Parent?.Parent?.Parent?.FullName
            ?? throw new InvalidOperationException("Run companion_status from a Martlet source checkout's build.");
        var program = Path.Combine(source, "Martlet.Companion", "bin", configuration, "net10.0", "Martlet.Companion.exe");
        if (!File.Exists(program))
            throw new InvalidOperationException($"Build src\\Martlet.Companion ({configuration}) first; building Martlet.Mcp builds it too.");
        var start = new System.Diagnostics.ProcessStartInfo(program)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add("--status");
        if (platform is not null) { start.ArgumentList.Add("--as"); start.ArgumentList.Add(platform); }
        if (settingsFile is not null) { start.ArgumentList.Add("--import"); start.ArgumentList.Add(settingsFile); }
        using var process = System.Diagnostics.Process.Start(start) ?? throw new InvalidOperationException("Could not start Martlet.Companion.");
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        limit.CancelAfter(TimeSpan.FromSeconds(30));
        var text = process.StandardOutput.ReadToEndAsync(limit.Token);
        try { await process.WaitForExitAsync(limit.Token); }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new InvalidOperationException("Martlet.Companion --status did not finish within 30 seconds.");
        }
        using var document = JsonDocument.Parse((await text).Trim());
        return new { exitCode = process.ExitCode, status = document.RootElement.Clone() };
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
}
