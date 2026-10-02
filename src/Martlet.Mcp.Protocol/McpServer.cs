using System.IO;
using System.Text.Json;
using Martlet.Core.Installation;
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
        Tool("logs_tail", "Read the last lines of a local Martlet log (desktop, avatar-renderer or host-runs), optionally only lines " +
            "containing some text. Failed provider requests appear in the desktop log with their HTTP status and the provider's " +
            "own short explanation. Read-only; logs can include local paths and provider error text, never keys.", new
        {
            log = new { type = "string", @enum = LogTail.Logs },
            lines = new { type = "integer", minimum = 1, maximum = LogTail.MaximumLines },
            contains = new { type = "string", maxLength = LogTail.MaximumFilterLength },
            dataDirectory = new { type = "string" }
        }),

        Tool("ui_connect", "Attach to an already-running Martlet.Desktop process in this interactive session.", new
        {
            pid = new { type = "integer", minimum = 1 }
        }, ["pid"]),
        Tool("ui_snapshot", "Inspect automation IDs, enabled state and selected non-secret status fields of attached Martlet windows.", new { }),
        Tool("ui_click", "Invoke an automation-ID control. Only safe navigation controls work without --allow-ui-effects.", new
        {
            id = new { type = "string" }
        }, ["id"]),
        Tool("ui_select", "Select a named option from a combo box. Requires --allow-ui-effects.", new
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
        }, ["id"]),
        Tool("voices_status", "Read voice recognition and Parakeet status from a data directory: on/off choices, which downloads " +
            "are installed and counts of known voices (never names, voiceprints or audio). Read-only; no audio, network or models run.", new
        {
            dataDirectory = new { type = "string" }
        }),
        Tool("f5_voices", "List the reference voices Martlet includes for F5 (key, name, female, licence, transcript, format; each " +
            "clip is checked against its SHA-256 and F5's reference rules) and the default voice; from a data directory's F5 voice " +
            "list, which included voices were added, how many of the owner's own voices there are and which voice is applied; and " +
            "which voice the speaking route uses (never own voices' names or audio). Plays nothing and contacts nothing.", new
        {
            dataDirectory = new { type = "string" }
        }),
        Tool("cluster_status", "Read shared \"who does what\" sync from a data directory: whether sync is on (on by default, " +
            "\"off\" only after the owner turned it off) and this PC's copy of the plan (each job's host, failover and which device " +
            "changed it last; each host's roles). Read-only; contacts nothing and returns no addresses or keys.", new
        {
            dataDirectory = new { type = "string" }
        }),
        Tool("virtualization_status", "Read whether Windows is ready for Docker Desktop's WSL 2 engine (virtualization in the firmware, " +
            "the Windows hypervisor, Virtual Machine Platform, Windows Subsystem for Linux, the WSL version), whether Docker Desktop is " +
            "installed and running, and any setup Martlet continues after a Windows restart. Read-only; changes nothing.", new
        {
            dataDirectory = new { type = "string" }
        })
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
                "logs_tail" => LogTail.Read(OptionalString(arguments, "dataDirectory"), OptionalString(arguments, "log"),
                    OptionalInt(arguments, "lines"), OptionalString(arguments, "contains")),

                "ui_connect" => desktop.Connect(RequiredInt(arguments, "pid")),
                "ui_snapshot" => desktop.Snapshot(),
                "ui_click" => await desktop.ClickAsync(RequiredString(arguments, "id")),
                "ui_select" => desktop.Select(RequiredString(arguments, "id"), RequiredString(arguments, "item")),
                "ui_set_text" => desktop.SetText(RequiredString(arguments, "id"), RequiredString(arguments, "text")),
                "ui_toggle" => desktop.Toggle(RequiredString(arguments, "id")),
                "voices_status" => VoicesStatus(arguments),
                "f5_voices" => F5Voices(arguments),
                "cluster_status" => ClusterStatus(arguments),
                "virtualization_status" => await VirtualizationStatusAsync(arguments, cancellation),
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

    /// <summary>Voice recognition (Companion › People) and Parakeet as the desktop keeps them in a data directory (the file
    /// names match Martlet.Desktop's LocalVoices). Counts only: names and voiceprints are personal and never returned.</summary>
    private static object VoicesStatus(JsonElement arguments)
    {
        var directory = DataDirectory(arguments);
        string? Choice(string file)
        {
            try { return File.ReadAllText(Path.Combine(directory, file)).Trim(); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
        }
        object roster;
        var path = Path.Combine(directory, "voices.json");
        if (!File.Exists(path)) roster = new { state = "none" };
        else
        {
            try
            {
                var list = Martlet.Core.Speakers.VoiceRoster.Parse(File.ReadAllBytes(path));
                roster = new
                {
                    state = "loaded", voices = list.Live.Count, named = list.Live.Count(v => v.Named), owner = list.Live.Count(v => v.Owner),
                    withLearnedNames = list.Live.Count(v => v.Names.Any(n => n.Source == Martlet.Core.Speakers.VoiceNameSource.Conversation)),
                    merged = list.Live.Sum(v => v.MergedVoices), tombstones = list.Voices.Count(v => v.Removed)
                };
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or Martlet.Core.Contracts.ContractException)
            {
                roster = new { state = "unreadable" };
            }
        }
        var speech = Path.Combine(directory, "speech");
        return new
        {
            recognition = Choice("voice-recognition.txt") ?? "off (never chosen)",
            sharing = Choice("voice-sharing.txt") ?? "on (default)",
            installed = new
            {
                runtime = Martlet.Sherpa.SherpaComponents.IsInstalled(speech, Martlet.Sherpa.SherpaPart.Runtime),
                voiceModels = Martlet.Sherpa.SherpaComponents.IsInstalled(speech, Martlet.Sherpa.SherpaPart.Speakers),
                parakeet = Martlet.Sherpa.SherpaComponents.IsInstalled(speech, Martlet.Sherpa.SherpaPart.Parakeet)
            },
            roster
        };
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

    /// <summary>Whether Windows can run Docker Desktop (the desktop's WindowsVirtualizationSetup reads the same facts), whether
    /// Docker Desktop is installed and running, and the setup Martlet continues after a restart. Never returns paths.</summary>
    private static async Task<object> VirtualizationStatusAsync(JsonElement arguments, CancellationToken cancellation)
    {
        var directory = DataDirectory(arguments);
        var state = await WindowsVirtualization.ProbeAsync(cancellation);
        var note = ContinueSetup.Read(directory, DateTimeOffset.Now);
        bool startsAtSignIn;
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\RunOnce");
            startsAtSignIn = key?.GetValue("MartletContinueSetup") is string;
        }
        catch (Exception error) when (error is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            startsAtSignIn = false;
        }
        static bool Running(string name)
        {
            var processes = System.Diagnostics.Process.GetProcessesByName(name);
            try { return processes.Length > 0; }
            finally { foreach (var process in processes) process.Dispose(); }
        }
        return new
        {
            ready = state.Ready,
            firmwareOff = state.FirmwareOff,
            needsWindowsChanges = state.NeedsChanges,
            problems = state.Problems(),
            firmware = state.Firmware,
            hypervisor = state.Hypervisor,
            virtualMachinePlatform = state.MachinePlatform.ToString(),
            windowsSubsystemForLinux = state.Subsystem.ToString(),
            wsl = state.Wsl,
            virtualMachine = state.VirtualMachine,
            summary = state.Describe(),
            dockerDesktop = new
            {
                installed = File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    "Docker", "Docker", "Docker Desktop.exe")),
                running = Running("com.docker.backend") || Running("Docker Desktop")
            },
            continueSetup = new
            {
                pending = note is not null,
                kind = note?.Kind.ToString(),
                task = note?.Task,
                created = note?.Created,
                startsAtSignIn
            }
        };
    }

    /// <summary>F5's included reference voices, each checked, and the data directory's F5 voice list (the "f5-voices" store
    /// Martlet.Desktop keeps). Own voices are counted, never named; an included voice is recognized by its clip's SHA-256.</summary>
    private static object F5Voices(JsonElement arguments)
    {
        var directory = DataDirectory(arguments);
        var included = Martlet.F5.F5BundledVoices.All.Select(voice =>
        {
            try
            {
                var format = voice.Check();
                return (object)new
                {
                    key = voice.Key, name = voice.Name, female = voice.Female, description = voice.Description, licence = voice.Licence,
                    transcript = voice.Transcript,
                    sha256 = voice.AudioSha256, sampleRate = format.SampleRate, durationMs = format.DurationMilliseconds, valid = true
                };
            }
            catch (Martlet.F5.F5Exception error)
            {
                return new { key = voice.Key, name = voice.Name, valid = false, problem = error.Failure.ToString() };
            }
        }).ToArray();
        static string Kind(string sha256) => Martlet.F5.F5BundledVoices.ForAudio(sha256)?.Key ??
            (Martlet.F5.F5BundledVoices.IsRetiredSample(sha256) ? "retired-sample" : "own");
        var storeDirectory = Path.Combine(directory, "f5-voices");
        object list;
        if (!File.Exists(Path.Combine(storeDirectory, ".martlet-f5-references.v1.json"))) list = new { state = "none" };
        else
        {
            try
            {
                using var store = Martlet.F5.F5ReferencePresetStore.Open(storeDirectory);
                var inspection = store.Inspect();
                var latest = inspection.Presets.Select(p => (p.Id, Kind: Kind(p.Snapshots.LastOrDefault()?.AudioSha256 ?? ""))).ToArray();
                list = new
                {
                    state = "loaded", voices = latest.Length,
                    included = latest.Where(p => p.Kind is not ("own" or "retired-sample")).Select(p => p.Kind).Distinct().ToArray(),
                    own = latest.Count(p => p.Kind == "own"), retiredSample = latest.Any(p => p.Kind == "retired-sample"),
                    applied = latest.Where(p => p.Id == inspection.AppliedPresetId).Select(p => p.Kind).FirstOrDefault()
                };
            }
            catch (Martlet.F5.F5Exception error) { list = new { state = error.Failure == Martlet.F5.F5Failure.Busy ? "busy" : "unreadable", problem = error.Failure.ToString() }; }
        }
        // The voice the speaking (TTS) route keeps, which is what F5 actually speaks with.
        object speaking;
        var settingsPath = Path.Combine(directory, "settings.json");
        if (!File.Exists(settingsPath)) speaking = new { state = "none" };
        else
        {
            try
            {
                var settings = Martlet.Core.Settings.SettingsJson.Read(File.ReadAllBytes(settingsPath));
                var route = settings.Setup?.Routes.FirstOrDefault(r => r.Role == Martlet.Core.Settings.SetupRole.Tts);
                speaking = new
                {
                    state = "loaded", route = route?.RouteType.ToString(),
                    voice = route?.Reference is { } reference ? Kind(reference.AudioSha256) : null
                };
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or Martlet.Core.Contracts.ContractException or JsonException)
            {
                speaking = new { state = "unreadable", problem = error is Martlet.Core.Contracts.ContractException ? error.Message : error.GetType().Name };
            }
        }
        var fallback = Martlet.F5.F5BundledVoices.Default;
        return new
        {
            @default = fallback.Key, defaultName = fallback.Name, defaultFemale = fallback.Female, included, list, speaking
        };
    }

    /// <summary>Shared "who does what" as the desktop keeps it in a data directory (the file names match Martlet.Desktop's
    /// ClusterSync): the sync choice, on unless cluster-sync.txt says "off", and cluster.json without host addresses.</summary>
    private static object ClusterStatus(JsonElement arguments)
    {
        var directory = DataDirectory(arguments);
        string? choice;
        try { choice = File.ReadAllText(Path.Combine(directory, "cluster-sync.txt")).Trim(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { choice = null; }
        object plan;
        var path = Path.Combine(directory, "cluster.json");
        if (!File.Exists(path)) plan = new { state = "none" };
        else
        {
            try
            {
                var copy = Martlet.Core.Cluster.ClusterPlan.Parse(File.ReadAllBytes(path));
                plan = new
                {
                    state = "loaded", revision = copy.Revision,
                    jobs = copy.Assignments.Select(a => new
                    {
                        job = a.Job, host = a.HostId, off = a.Off, failover = a.Failover, movedFrom = a.MovedFrom,
                        updatedBy = a.UpdatedBy, updatedAt = a.UpdatedAt
                    }).ToArray(),
                    hosts = copy.Nodes.Select(n => new { hostId = n.HostId, removed = n.Removed, roles = n.Roles.Select(r => r.Kind).ToArray() }).ToArray()
                };
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or Martlet.Core.Contracts.ContractException)
            {
                plan = new { state = "unreadable" };
            }
        }
        return new { sync = choice switch { "off" => "off", null => "on (default)", _ => "on" }, plan };
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

    private static string? OptionalString(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var value) ||
            value.ValueKind == JsonValueKind.Null)
            return null;
        if (value.ValueKind != JsonValueKind.String) throw new ArgumentException($"'{property}' must be a string.");
        return value.GetString();
    }

    private static int? OptionalInt(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var value) ||
            value.ValueKind == JsonValueKind.Null)
            return null;
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            ? number : throw new ArgumentException($"'{property}' must be an integer.");
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
