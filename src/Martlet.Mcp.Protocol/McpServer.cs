using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Martlet.Core.Installation;
using Martlet.Doctor;
using Martlet.Mcp.Client;

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
        Tool("logs_timeline", "Read this PC's logs as the desktop's Diagnostics page shows them: desktop, avatar-renderer and host-runs " +
            "(with rotated copies) parsed into one timeline of {at, level, component, seq, message}, newest first, with counts of " +
            "errors and warnings and the log host chosen in the shared plan. Filters: level (all, warnings, errors), component, " +
            "contains. Read-only; contacts no host.", new
        {
            level = new { type = "string", @enum = LogTimeline.Levels },
            component = new { type = "string", @enum = Martlet.Core.Logs.LogComponents.Local },
            contains = new { type = "string", maxLength = LogTail.MaximumFilterLength },
            lines = new { type = "integer", minimum = 1, maximum = LogTimeline.MaximumLines },
            dataDirectory = new { type = "string" }
        }),

        Tool("ui_connect", "Attach to an already-running Martlet.Desktop process in this interactive session.", new
        {
            pid = new { type = "integer", minimum = 1 }
        }, ["pid"]),
        Tool("ui_snapshot", "Inspect automation IDs, enabled state and selected non-secret status fields of attached Martlet windows. " +
            "With layout, each control also returns its screen bounds and, for text, where its first line of text sits " +
            "(geometry only, never the text).", new
        {
            layout = new { type = "boolean" }
        }),
        Tool("ui_click", "Invoke an automation-ID control. Only safe navigation controls work without --allow-ui-effects.", new
        {
            id = new { type = "string" }
        }, ["id"]),
        Tool("ui_select", "Select a named option from a combo box. Requires --allow-ui-effects.", new
        {
            id = new { type = "string" }, item = new { type = "string" }
        }, ["id", "item"]),
        Tool("ui_set_text", "Enter text into an editable control; an empty text clears it (requires --allow-ui-effects).", new
        {
            id = new { type = "string" }, text = new { type = "string" }
        }, ["id", "text"]),
        Tool("ui_toggle", "Toggle an enabled checkbox (requires --allow-ui-effects).", new
        {
            id = new { type = "string" }
        }, ["id"]),
        Tool("ui_tray", "Martlet's notification-area icon. \"status\" (default) reads whether the icon is shown, whether the main " +
            "window is visible or hidden in the notification area, and whether Martlet still runs. \"open\" and \"menu\" send the icon " +
            "what Explorer sends for a left click (show Martlet) and a right click (its menu at the mouse pointer; ui_snapshot then " +
            "lists the Tray* items). \"close\" presses the main window's close button, which hides Martlet in the notification area " +
            "by default or exits it, so it requires --allow-ui-effects.", new
        {
            action = new { type = "string", @enum = DesktopAutomation.TrayActions }
        }),
        Tool("voices_status", "Read voice recognition and Parakeet status from a data directory: on/off choices, which downloads " +
            "are installed and counts of known voices (never names, voiceprints or audio). Read-only; no audio, network or models run.", new
        {
            dataDirectory = new { type = "string" }
        }),
        Tool("f5_voices", "List Martlet's starter voices (key, name, female, cute, licence, transcript, format; each clip is checked " +
            "against its SHA-256 and F5's reference rules; a new voice list starts with them, after which they are ordinary voices) and " +
            "the default voice; from a data directory, the shared speaking-voice list (speaking-voices.json: live voices, which starter " +
            "voices are in it or removed, how many of the owner's own, tombstones and the voice chosen on all computers), this PC's " +
            "recordings (the F5 voice store: which starter voices, how many own and which is applied); which voice the speaking route " +
            "uses and on which self-hosted engine, host and model; and the voice engines (Chatterbox " +
            "Turbo, the default; F5-TTS; XTTS-v2; GPT-SoVITS; Dia: host role, gateway route, model, weights licence, GPU memory, reference " +
            "length bounds and tag catalog; each starter voice lists the engines that can clone it and its language) with the one chosen on this desktop " +
            "(never own voices' names, transcripts or audio). Plays nothing and contacts nothing.", new
        {
            dataDirectory = new { type = "string" }
        }),
        Tool("voice_tags", "Show how a reply's voice tags are handled for a self-hosted voice engine (engine key, default the " +
            "default engine Chatterbox Turbo; \"none\" for a voice without tags such as OpenAI or Windows): the engine's tag catalog in " +
            "its own syntax, the Thinking prompt it adds (Companion > Prompts > Voice sounds and tones, from dataDirectory's settings " +
            "when given), the pieces the real speech segmenter hands that engine and the text the chat and captions show. " +
            "Synthesizes and contacts nothing.", new
        {
            text = new { type = "string" }, engine = new { type = "string" }, dataDirectory = new { type = "string" }
        }, ["text"]),
        Tool("cluster_status", "Read shared \"who does what\" sync from a data directory: whether sync is on (on by default, " +
            "\"off\" only after the owner turned it off) and this PC's copy of the plan (each job's host, failover and which device " +
            "changed it last; each host's roles). Read-only; contacts nothing and returns no addresses or keys.", new
        {
            dataDirectory = new { type = "string" }
        }),
        Tool("network_status", "Read this PC's Martlet network from a data directory (network.json and whether its network key " +
            "exists): member, waiting for approval (with the check number) or in no network; the network ID; each desktop and host " +
            "in the roster (ID, name, removed, who changed it last); hosts paired on purpose (adopt) and forgotten here (ignored). " +
            "Read-only; contacts nothing and returns no keys or addresses.", new
        {
            dataDirectory = new { type = "string" }
        }),
        Tool("network_selftest", "Rehearse the Martlet network end to end with the production code: three real gateways on " +
            "127.0.0.1 (pinned TLS, volatile credentials) and two simulated desktops using the desktop's network client and sync " +
            "engine (found, bind hosts, join with a check number, pair every member with every host by itself, refuse forged keys " +
            "and rosters, remove a desktop and a host). Loopback only; writes nothing to disk or the credential vault.", new { }),
        Tool("nearby_status", "Read whether this PC lets Martlet on the owner's other computers find it and ask to use its hosts " +
            "(on by default, \"off\" only after the owner turned it off) and which paired hosts it could share from hosts.json (hosts " +
            "it runs or reaches over SSH; this PC's own host service set up from the host dashboard is found from Docker by the " +
            "desktop, not here). Read-only; contacts nothing and returns no addresses, SSH targets or keys.", new
        {
            dataDirectory = new { type = "string" }
        }),
        Tool("virtualization_status", "Read whether Windows is ready for Docker Desktop's WSL 2 engine (virtualization in the firmware, " +
            "the Windows hypervisor, Virtual Machine Platform, Windows Subsystem for Linux, the WSL version), whether Docker Desktop is " +
            "installed and running and its engine state, and any setup Martlet continues after a Windows restart. Read-only; changes nothing.", new
        {
            dataDirectory = new { type = "string" }
        }),
        Tool("host_service_status", "Read this PC's own Martlet host service on Docker Desktop the way the host dashboard does: its stage " +
            "(DockerMissing, DockerNotRunning, NotSetUp, Stopped, Running), Martlet version, host ID, whether its published address is " +
            "still this PC's and answers, the installed roles, and the Martlet network it joined (state and the desktops paired " +
            "with it). Reads Docker and the gateway's nonsecret files only (never its agent token, keys or pairings); returns no " +
            "addresses and changes nothing.", new { }),
        Tool("node_link_check", "Run commands between Martlet computers end to end on this PC's loopback: the real gateway (pinned TLS, " +
            "pairing, signed requests, the command mailbox and its storage), the desktop's real client and agent loop with a fixture " +
            "runner, two fixture devices. Checks that only known commands are accepted, only the host's agent (local token) takes them, " +
            "output and outcomes reach the sender, secrets never appear in lists or saved copies, cancel works and commands survive a " +
            "restart. Contacts nothing outside loopback and touches no real credentials, Docker or installs.", new { }),
        Tool("api_keys_status", "Read the API keys of this PC's Martlet network from a data directory (api-keys.json, docs/API.md): for " +
            "each key its ID, name, scopes, who made it and when, expiry and whether it is revoked or expired. Read-only; contacts " +
            "nothing and never returns a key or its verifier.", new
        {
            dataDirectory = new { type = "string" }
        }),
        Tool("api_selftest", "Rehearse API keys for software outside the Martlet network end to end with the production code: two " +
            "real gateways on 127.0.0.1 (pinned TLS, the real Ollama relay route over a fixture Ollama, NOT AI), a simulated desktop " +
            "that creates, syncs and revokes keys through its paired client, and a plain HTTPS client sending Authorization: Bearer. " +
            "Checks scopes (read, voice, manage), refusals (no key, wrong key, endpoints keys may never use), sync to a second host and " +
            "a restart, last-used reports, revocation mid-reply, stale copies and expiry. Loopback only; writes nothing to disk or the " +
            "credential vault.", new { }),
        Tool("speaking_voices_selftest", "Rehearse the shared speaking voices end to end with the production code: two real gateways on " +
            "127.0.0.1 (pinned TLS, the real reference-voice relay route over a fixture voice service, NOT AI, with in-memory " +
            "speaking-voices.json and recordings) and two simulated desktops with real F5 voice stores in a temporary folder, using the " +
            "desktop's paired client and Martlet.F5's reconcile engine. Checks the starter voices, sharing the list and recordings, " +
            "speaking by recording SHA-256 alone, the one-time fallback that sends a recording a host lacks, a new desktop taking every " +
            "voice from a host, the shared choice, removal everywhere (host and desktop copies deleted), stale copies, a host restart and " +
            "upload checks. Loopback only; the temporary folder is deleted and the credential vault is not touched.", new { }),
        Tool("audio2face_check", "Animate a short synthesized speech-like test signal (generated here; no microphone, nothing played) " +
            "with an Audio2Face service on a numeric loopback endpoint (default http://127.0.0.1:52000) through Martlet's production " +
            "Audio2Face client, the one the host gateway's lip-sync relay uses, so either Audio2Face engine (the local open-source SDK " +
            "service or NVIDIA's NIM) can be checked. Returns ok, frames, frames per second, the frame time span, how many blendshape " +
            "channels came back and moved, the jawOpen peak, the strongest channels and timings, or the failure category. Loopback only.", new
        {
            endpoint = new { type = "string", maxLength = 64 },
            seconds = new { type = "integer", minimum = 1, maximum = 10 },
            sampleRate = new { type = "integer", @enum = Audio2FaceCheck.SampleRates }
        }),
        Tool("mcp_servers_status", "Read the MCP servers in a data directory's mcp.json as Martlet parses them: each server's name, " +
            "transport, program and raw arguments (with ${env:...} and ${secret:...} references, never their values), environment and " +
            "header names, on/off, auto-approve, the MCP directory entry it was installed from and the secret names it uses. " +
            "Read-only; starts no server and reads no credentials.", new
        {
            dataDirectory = new { type = "string" }
        }),
        Tool("mcp_directory_plan", "Show how Martlet's MCP directory would install one MCP Registry entry (a server.json object, as " +
            "the registry's v0.1 API returns it under \"server\"): the ways to run it, the inputs each needs, and with values (by input " +
            "key) the exact mcp.json entry and secret names it would write. Local only: fetches, writes and starts nothing.", new
        {
            server = new { type = "object" },
            name = new { type = "string", maxLength = 64 },
            values = new { type = "object", additionalProperties = new { type = "string", maxLength = 4096 } }
        }, ["server"]),
        Tool("home_assistant_probe", "Check one Home Assistant address the way Companion > Smart home does before setting it up: " +
            "whether it answers like Home Assistant and how far its first-run setup got (owner account, regional settings, analytics, " +
            "integration step; GET /api/onboarding, which needs no sign-in). Sends no token or password and changes nothing.", new
        {
            address = new { type = "string", maxLength = 512 }
        }, ["address"]),
        Tool("home_assistant_find", "Ask the local network for Home Assistant the way Smart home's Find on my network does: one " +
            "multicast DNS question for _home-assistant._tcp.local on each local network, listing every Home Assistant that answers " +
            "(name, address, version). Sends nothing else and changes nothing.", new
        {
            seconds = new { type = "number", minimum = 1, maximum = 10 }
        }),
        Tool("prompts_status", "Read Companion > Prompts from a data directory's settings.json: every internal prompt Martlet sends " +
            "to the Thinking model (id, group, title, placeholders) and whether it uses the built-in text, is edited or is emptied " +
            "(sent as nothing), with its character count. With id, also returns that one prompt's effective text (the saved edit or " +
            "the built-in text) exactly as Martlet uses it. Read-only.", new
        {
            dataDirectory = new { type = "string" },
            id = new { type = "string", maxLength = 64 }
        }),
        Tool("hearing_check", "Whether the Thinking model can hear the user's recording (the saved Thinking route in a data " +
            "directory, or modelId): the model's and the route's hearing support and whether Companion > Listening > Let Thinking hear " +
            "my voice is on. Then rehearses the production Chat Completions adapter against a fixture endpoint on 127.0.0.1 (canned " +
            "reply, NOT AI) with a synthesized speech-like clip (never microphone audio, nothing played): the clip goes as an " +
            "input_audio WAV part beside the transcript, is refused without its own audio permission before any request, and is " +
            "left out of a transcript-only retry. Loopback only; reads no credentials.", new
        {
            dataDirectory = new { type = "string" },
            modelId = new { type = "string", maxLength = 128 }
        }),
        Tool("smart_home_status", "Read Companion > Smart home's saved connection from a data directory: the Home Assistant address, " +
            "name and version, whether a token is saved (never the token), the control, locks and flexible-request settings, and whether " +
            "the connection is shared through the paired hosts (shared revision, which host it came from). Read-only.", new
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
                "logs_timeline" => LogTimeline.Read(OptionalString(arguments, "dataDirectory"), OptionalString(arguments, "level"),
                    OptionalString(arguments, "component"), OptionalString(arguments, "contains"), OptionalInt(arguments, "lines")),

                "ui_connect" => desktop.Connect(RequiredInt(arguments, "pid")),
                "ui_snapshot" => desktop.Snapshot(OptionalBool(arguments, "layout") ?? false),
                "ui_click" => await desktop.ClickAsync(RequiredString(arguments, "id")),
                "ui_select" => desktop.Select(RequiredString(arguments, "id"), RequiredString(arguments, "item")),
                "ui_set_text" => desktop.SetText(RequiredString(arguments, "id"),
                    OptionalString(arguments, "text") ?? throw new ArgumentException("Missing string 'text'.")),
                "ui_toggle" => desktop.Toggle(RequiredString(arguments, "id")),
                "ui_tray" => desktop.Tray(OptionalString(arguments, "action") ?? "status"),
                "voices_status" => VoicesStatus(arguments),
                "f5_voices" => F5Voices(arguments),
            "voice_tags" => VoiceTagsCheck(arguments),
                "cluster_status" => ClusterStatus(arguments),
                "network_status" => NetworkStatus(arguments),
                "network_selftest" => await NodeLinkCheckAsync(cancellation, "network"),
                "nearby_status" => NearbyStatus(arguments),
                "virtualization_status" => await VirtualizationStatusAsync(arguments, cancellation),
                "host_service_status" => await HostServiceStatusAsync(cancellation),
                "node_link_check" => await NodeLinkCheckAsync(cancellation),
                "api_keys_status" => ApiKeysStatus(arguments),
                "api_selftest" => await NodeLinkCheckAsync(cancellation, "api"),
                "speaking_voices_selftest" => await NodeLinkCheckAsync(cancellation, "voices"),
                "audio2face_check" => await Audio2FaceCheck.RunAsync(OptionalString(arguments, "endpoint"),
                    OptionalInt(arguments, "seconds"), OptionalInt(arguments, "sampleRate"), cancellation),
                "mcp_servers_status" => McpServersStatus(arguments),
                "mcp_directory_plan" => McpDirectoryPlan(arguments),
                "home_assistant_probe" => await HomeAssistantProbeAsync(arguments, cancellation),
                "home_assistant_find" => await HomeAssistantFindAsync(arguments, cancellation),
                "smart_home_status" => SmartHomeStatus(arguments),
                "prompts_status" => await PromptsStatusAsync(arguments, cancellation),
                "hearing_check" => await HearingCheck.RunAsync(OptionalString(arguments, "modelId"), DataDirectory(arguments), cancellation),
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

    /// <summary>The network's API keys as the desktop keeps them in a data directory (the file name matches Martlet.Desktop's
    /// MainWindow.ApiKeys). Names, scopes and stamps only: the verifier is never returned, and the key itself is kept nowhere.</summary>
    private static object ApiKeysStatus(JsonElement arguments)
    {
        var path = Path.Combine(DataDirectory(arguments), "api-keys.json");
        if (!File.Exists(path)) return new { state = "none" };
        Martlet.Core.Access.ApiKeyList list;
        try { list = Martlet.Core.Access.ApiKeyList.Parse(File.ReadAllBytes(path)); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Martlet.Core.Contracts.ContractException)
        {
            return new { state = "unreadable", problem = error.Message };
        }
        var now = DateTimeOffset.UtcNow;
        return new
        {
            state = "loaded",
            live = list.Live(now).Count,
            revoked = list.Keys.Count(k => k.Revoked),
            expired = list.Keys.Count(k => !k.Revoked && k.Expired(now)),
            keys = list.Keys.Select(k => new
            {
                id = k.Id, name = k.Name, scopes = k.Scopes, createdBy = k.CreatedBy, createdAt = k.CreatedAt, expiresAt = k.ExpiresAt,
                revoked = k.Revoked, expired = !k.Revoked && k.Expired(now), updatedBy = k.UpdatedBy, hasVerifier = k.Verifier is not null
            })
        };
    }

    /// <summary>Runs Martlet.NodeLinkCheck (built next to this server, in the same configuration) with <paramref name="arguments"/>
    /// and returns its JSON report. A separate process, because the in-process gateway needs the ASP.NET Core runtime and this
    /// server does not.</summary>
    private static async Task<object> NodeLinkCheckAsync(CancellationToken cancellation, params string[] arguments)
    {
        var output = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
        var configuration = output.Parent?.Name ?? "Release";
        var source = output.Parent?.Parent?.Parent?.Parent?.FullName
            ?? throw new InvalidOperationException("Run node_link_check from a Martlet source checkout's build.");
        var program = Path.Combine(source, "Martlet.NodeLinkCheck", "bin", configuration, "net10.0", "Martlet.NodeLinkCheck.exe");
        if (!File.Exists(program))
            throw new InvalidOperationException($"Build src\\Martlet.NodeLinkCheck ({configuration}) first; building Martlet.Mcp builds it too.");
        var start = new System.Diagnostics.ProcessStartInfo(program)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = System.Diagnostics.Process.Start(start) ?? throw new InvalidOperationException("Could not start Martlet.NodeLinkCheck.");
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        limit.CancelAfter(TimeSpan.FromMinutes(2));
        var report = process.StandardOutput.ReadToEndAsync(limit.Token);
        var errors = process.StandardError.ReadToEndAsync(limit.Token);
        try { await process.WaitForExitAsync(limit.Token); }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new InvalidOperationException("Martlet.NodeLinkCheck did not finish within two minutes.");
        }
        var text = (await report).Trim();
        try
        {
            using var document = JsonDocument.Parse(text);
            return new { exitCode = process.ExitCode, report = document.RootElement.Clone() };
        }
        catch (JsonException)
        {
            throw new InvalidOperationException($"Martlet.NodeLinkCheck exited {process.ExitCode} without a report: {(await errors).Trim()}");
        }
    }

    /// <summary>mcp.json in a data directory as the desktop's McpToolService parses it (the file name matches). Arguments are
    /// the raw ones from the file, so ${env:...} and ${secret:...} stay references; no server starts and no credential is read.</summary>
    private static object McpServersStatus(JsonElement arguments)
    {
        var path = Path.Combine(DataDirectory(arguments), "mcp.json");
        if (!File.Exists(path)) return new { state = "none" };
        string text;
        try
        {
            if (new FileInfo(path).Length > McpConfiguration.MaxFileBytes) return new { state = "invalid", problem = "mcp.json is larger than 1 MB." };
            text = File.ReadAllText(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return new { state = "unreadable", problem = error.Message };
        }
        McpConfiguration configuration;
        try { configuration = McpConfiguration.Parse(text, secrets: _ => ""); }
        catch (McpConfigurationException error) { return new { state = "invalid", problem = error.Message }; }
        return new
        {
            state = "loaded",
            servers = configuration.Servers.Select(server =>
            {
                var raw = McpConfiguration.FindServer(text, server.Name);
                return new
                {
                    name = server.Name,
                    transport = server.Transport.ToString().ToLowerInvariant(),
                    command = (raw?["command"] as JsonValue)?.ToString(),
                    args = (raw?["args"] as JsonArray)?.Select(a => a?.ToString()).ToArray() ?? [],
                    host = server.Url?.Host,
                    env = server.Env.Keys.ToArray(),
                    headers = server.Headers.Keys.ToArray(),
                    disabled = server.Disabled,
                    autoApproveAll = server.AutoApproveAll,
                    autoApprove = server.AutoApprove,
                    registry = server.Registry,
                    registryVersion = server.RegistryVersion,
                    secrets = server.Secrets,
                    problem = server.Problem
                };
            }).ToArray()
        };
    }

    /// <summary>How the desktop's MCP directory would install one registry entry. Secret values are never returned, only the
    /// ${secret:...} names the entry would use.</summary>
    private static object McpDirectoryPlan(JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object || !arguments.TryGetProperty("server", out var given) ||
            given.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Missing object 'server'.");
        var node = JsonNode.Parse(given.GetRawText()) as JsonObject ?? throw new ArgumentException("Missing object 'server'.");
        if (node["server"] is JsonObject wrapped) node = wrapped;
        McpDirectoryEntry entry;
        try { entry = McpDirectoryEntry.Parse(node); }
        catch (FormatException error) { throw new ArgumentException(error.Message); }
        var name = OptionalString(arguments, "name") ?? entry.SuggestedName;
        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (arguments.TryGetProperty("values", out var given2) && given2.ValueKind == JsonValueKind.Object)
            foreach (var value in given2.EnumerateObject())
                values[value.Name] = value.Value.ValueKind == JsonValueKind.String ? value.Value.GetString()
                    : throw new ArgumentException($"values.{value.Name} must be a string.");
        return new
        {
            name = entry.Name, displayName = entry.DisplayName, suggestedName = entry.SuggestedName, version = entry.Version,
            unsupported = entry.Unsupported,
            options = entry.Options.Select(option =>
            {
                object? plan = null;
                string? problem = null;
                try
                {
                    var built = option.Build(name, values);
                    plan = new { entry = built.Entry, secrets = built.Secrets.Keys.ToArray(), preview = built.Preview };
                }
                catch (McpConfigurationException error) { problem = error.Message; }
                return new
                {
                    kind = option.Kind.ToString(), summary = option.Summary, runtime = option.Runtime,
                    runtimeAvailable = option.RuntimeAvailable(), host = option.Host,
                    inputs = option.Inputs.Select(input => new
                    {
                        key = input.Key, label = input.Label, required = input.Required, secret = input.Secret, flag = input.Flag,
                        isPath = input.IsPath, @default = input.Default, placeholder = input.Placeholder, choices = input.Choices
                    }).ToArray(),
                    plan, problem
                };
            }).ToArray()
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
                running = Running("com.docker.backend") || Running("Docker Desktop"),
                engine = await DockerDesktopStatus.ReadAsync(cancellation)
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

    /// <summary>This PC's own host service as the desktop's host dashboard reads it (<see cref="LocalHostService"/>). The
    /// published address itself is not returned, only whether it is still this PC's and answers.</summary>
    private static async Task<object> HostServiceStatusAsync(CancellationToken cancellation)
    {
        var state = await LocalHostService.ProbeAsync(cancellation);
        return new
        {
            stage = state.Stage.ToString(), ready = state.Ready, version = state.Version, hostId = state.HostId,
            published = state.Address is not null, addressOnThisPc = state.AddressOnThisPc, answering = state.Answering,
            roles = state.Roles, network = state.Network,
            desktops = state.Desktops.Select(d => new { id = d.Id, name = d.Name }).ToArray(),
            problem = state.Problem
        };
    }

    /// <summary>F5's included reference voices, each checked, and the data directory's F5 voice list (the "f5-voices" store
    /// Martlet.Desktop keeps). Own voices are counted, never named; a starter voice is recognized by its clip's SHA-256.</summary>
    private static object VoiceTagsCheck(JsonElement arguments)
    {
        var text = OptionalString(arguments, "text") ?? throw new ArgumentException("'text' is required.");
        var key = OptionalString(arguments, "engine");
        var engine = key is "none" ? null : key is null ? Martlet.Core.Settings.SpeechEngines.Default
            : Martlet.Core.Settings.SpeechEngines.ForKey(key) ?? throw new ArgumentException($"Unknown voice engine '{key}'.");
        Martlet.Core.Settings.PromptSettings? prompts = null;
        if (OptionalString(arguments, "dataDirectory") is { } directory && File.Exists(Path.Combine(directory, "settings.json")))
            prompts = Martlet.Core.Settings.SettingsJson.Read(File.ReadAllBytes(Path.Combine(directory, "settings.json"))).Prompts;
        var preview = Martlet.Conversation.SpeechTextPreview.For(text, engine);
        return new
        {
            engine = engine?.Key, name = engine?.Name, supportsTags = engine?.SupportsTags ?? false,
            tags = (engine?.Tags ?? []).Select(tag => tag.Text).ToArray(),
            prompt = Martlet.Core.Settings.VoiceTags.Instructions(engine, prompts),
            spoken = preview.Spoken, suppressedPieces = preview.SuppressedPieces, shown = preview.Shown
        };
    }
    private static object F5Voices(JsonElement arguments)
    {
        var directory = DataDirectory(arguments);
        var starters = Martlet.F5.F5BundledVoices.All.Select(voice =>
        {
            try
            {
                var format = voice.Check();
                return (object)new
                {
                    key = voice.Key, name = voice.Name, female = voice.Female, cute = voice.Cute, description = voice.Description,
                    licence = voice.Licence, transcript = voice.Transcript,
                    sha256 = voice.AudioSha256, sampleRate = format.SampleRate, durationMs = format.DurationMilliseconds, valid = true,
                    // The engines that can clone this clip (GPT-SoVITS needs 3-10 s) and the language its transcript is read in.
                    engines = Martlet.Core.Settings.SpeechEngines.All
                        .Where(engine => Martlet.Core.Settings.SpeechEngines.ReferenceProblem(engine, format.DurationMilliseconds) is null)
                        .Select(engine => engine.Key).ToArray(),
                    language = Martlet.Core.Settings.SpeechEngines.ReferenceLanguage(voice.Transcript)
                };
            }
            catch (Martlet.F5.F5Exception error)
            {
                return new { key = voice.Key, name = voice.Name, valid = false, problem = error.Failure.ToString() };
            }
        }).ToArray();
        static string Kind(string sha256) => Martlet.F5.F5BundledVoices.ForAudio(sha256)?.Key ??
            (Martlet.F5.F5BundledVoices.IsRetiredSample(sha256) ? "retired-sample" : "own");
        // A voice's ID is its reference revision, so a starter voice's ID is known even after only its tombstone remains.
        var starterIds = Martlet.F5.F5BundledVoices.All.ToDictionary(
            voice => Martlet.Core.Voices.SpeakingVoiceLibrary.ReferenceId(voice.AudioSha256, voice.Transcript), voice => voice.Key);
        string KindOfId(string id) => starterIds.GetValueOrDefault(id) ?? "own";
        // The shared list (speaking-voices.json, the file Martlet.Desktop's F5Voices keeps; absent until a voice is first used,
        // changed or shared). Own voices are counted, never named.
        object library;
        var libraryPath = Path.Combine(directory, "speaking-voices.json");
        if (!File.Exists(libraryPath)) library = new { state = "none" };
        else
        {
            try
            {
                var shared = Martlet.Core.Voices.SpeakingVoiceLibrary.Parse(File.ReadAllBytes(libraryPath));
                var live = shared.Live;
                library = new
                {
                    state = "loaded", voices = live.Count, revision = shared.Revision,
                    starters = live.Select(v => KindOfId(v.Id)).Where(kind => kind != "own").ToArray(),
                    own = live.Count(v => KindOfId(v.Id) == "own"),
                    removed = shared.Voices.Count(v => v.Removed),
                    removedStarters = shared.Voices.Where(v => v.Removed).Select(v => KindOfId(v.Id)).Where(kind => kind != "own").ToArray(),
                    chosen = shared.ChosenVoice is { } chosenVoice ? KindOfId(chosenVoice.Id) : null,
                    chosenBy = shared.ChosenVoice is not null ? shared.Chosen!.UpdatedBy : null
                };
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or Martlet.Core.Contracts.ContractException)
            {
                library = new { state = "unreadable", problem = error is Martlet.Core.Contracts.ContractException ? error.Message : error.GetType().Name };
            }
        }
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
                    starters = latest.Where(p => p.Kind is not ("own" or "retired-sample")).Select(p => p.Kind).Distinct().ToArray(),
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
                    voice = route?.Reference is { } reference ? Kind(reference.AudioSha256) : null,
                    engine = route?.GatewaySnapshot is { } snapshot
                        ? Martlet.Core.Settings.SpeechEngines.ForRoute(snapshot.RouteId)?.Key : null,
                    host = route?.RouteType == Martlet.Core.Settings.SetupRouteType.GatewayF5 ? route.Gateway?.HostId : null,
                    model = route?.RouteType == Martlet.Core.Settings.SetupRouteType.GatewayF5 ? route.ModelId : null
                };
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or Martlet.Core.Contracts.ContractException or JsonException)
            {
                speaking = new { state = "unreadable", problem = error is Martlet.Core.Contracts.ContractException ? error.Message : error.GetType().Name };
            }
        }
        var fallback = Martlet.F5.F5BundledVoices.Default;
        // The self-hosted voice engines (F5-TTS, XTTS-v2, GPT-SoVITS, Dia) and the one chosen on this desktop (speaking-engine.txt, the file
        // Martlet.Desktop's SpeakingEngineChoice keeps; a speaking route on a host's engine wins over it).
        string? chosen = null;
        try { chosen = File.ReadAllText(Path.Combine(directory, "speaking-engine.txt")).Trim(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        var engines = Martlet.Core.Settings.SpeechEngines.All.Select(engine => new
        {
            key = engine.Key, name = engine.Name, hostRole = engine.HostRoleKind, routeId = engine.RouteId, path = engine.Path,
            model = engine.DefaultModel, weightsLicence = engine.WeightsLicense, minimumGpuMemoryGb = engine.MinimumGpuMemoryGb,
            minimumReferenceMs = engine.MinimumReferenceMilliseconds, maximumReferenceMs = engine.MaximumReferenceMilliseconds,
            summary = engine.Summary, @default = engine == Martlet.Core.Settings.SpeechEngines.Default,
            supportsTags = engine.SupportsTags,
            tags = engine.Tags.Select(tag => new { text = tag.Text, kind = tag.Kind.ToString(), usage = tag.Usage }).ToArray()
        }).ToArray();
        return new
        {
            @default = fallback.Key, defaultName = fallback.Name, defaultFemale = fallback.Female, defaultCute = fallback.Cute,
            cute = Martlet.F5.F5BundledVoices.All.Where(voice => voice.Cute).Select(voice => voice.Key).ToArray(), starters, library, list, speaking,
            engines, chosenEngine = Martlet.Core.Settings.SpeechEngines.ForKey(chosen)?.Key ?? Martlet.Core.Settings.SpeechEngines.Default.Key
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

    /// <summary>The Martlet network as the desktop keeps it in a data directory (network.json and network\device_ecdsa, the
    /// names Martlet.Desktop's NetworkIdentity uses). No keys, signatures or addresses are returned.</summary>
    private static object NetworkStatus(JsonElement arguments)
    {
        var directory = DataDirectory(arguments);
        var key = File.Exists(Path.Combine(directory, "network", "device_ecdsa"));
        var path = Path.Combine(directory, Martlet.Avatar.Audio2Face.Remote.NetworkLocalState.FileName);
        if (!File.Exists(path)) return new { state = "none", key };
        Martlet.Avatar.Audio2Face.Remote.NetworkLocalState local;
        try { local = Martlet.Avatar.Audio2Face.Remote.NetworkLocalState.Parse(File.ReadAllBytes(path)); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Martlet.Core.Contracts.ContractException)
        {
            return new { state = "unreadable", key };
        }
        var roster = local.Roster;
        return new
        {
            state = roster is not null ? "member" : local.Waiting is not null ? "waiting" : "none",
            key,
            networkId = roster?.NetworkId ?? local.Waiting?.NetworkId,
            revision = roster?.Revision,
            founder = roster?.Founder?.Id,
            waiting = local.Waiting is { } wait ? new { hostId = wait.HostId, checkNumber = wait.CheckNumber, since = wait.Since } : null,
            desktops = roster?.Members.Where(m => m.IsDesktop).Select(m => new { id = m.Id, name = m.Name, removed = m.Removed, updatedBy = m.UpdatedBy, changedAt = m.ChangedAt }).ToArray(),
            hosts = roster?.Members.Where(m => m.IsHost).Select(m => new { id = m.Id, name = m.Name, removed = m.Removed, updatedBy = m.UpdatedBy, changedAt = m.ChangedAt }).ToArray(),
            adopt = local.Adopt,
            ignored = local.Ignored,
            removedFrom = local.RemovedFrom
        };
    }

    /// <summary>"Let my other computers find this PC" as the desktop keeps it (the file names match Martlet.Desktop's Nearby and
    /// HostRegistry): the choice, on unless nearby.txt says "off", and the paired hosts this PC could share (those it runs, saved
    /// as ThisPcDocker, or reaches over SSH). Host IDs and how each is reached only: no addresses, SSH targets or keys.</summary>
    private static object NearbyStatus(JsonElement arguments)
    {
        var directory = DataDirectory(arguments);
        string? choice;
        try { choice = File.ReadAllText(Path.Combine(directory, "nearby.txt")).Trim(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { choice = null; }
        object hosts;
        var path = Path.Combine(directory, "hosts.json");
        if (!File.Exists(path)) hosts = new { state = "none", paired = 0, shareable = Array.Empty<object>() };
        else
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllBytes(path));
                var list = document.RootElement.GetProperty("hosts").EnumerateArray().Select(host =>
                {
                    var method = host.TryGetProperty("method", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : "OnHost";
                    var ssh = host.TryGetProperty("sshTarget", out var target) && target.ValueKind == JsonValueKind.String &&
                        !string.IsNullOrWhiteSpace(target.GetString());
                    var id = host.GetProperty("pairing").GetProperty("hostId").GetString();
                    var shareable = method == "ThisPcDocker" || method is "SshDocker" or "SshNative" && ssh;
                    return (id, method, shareable);
                }).ToArray();
                hosts = new
                {
                    state = "loaded", paired = list.Length,
                    shareable = list.Where(h => h.shareable).Select(h => new { hostId = h.id, reach = h.method }).ToArray()
                };
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or
                InvalidOperationException)
            {
                hosts = new { state = "unreadable" };
            }
        }
        return new { share = choice switch { "off" => "off", null => "on (default)", _ => "on" }, port = 9444, hosts };
    }

    /// <summary>One Home Assistant's first-run state (no sign-in needed), as Smart home checks it before offering Set up.</summary>
    private static async Task<object> HomeAssistantProbeAsync(JsonElement arguments, CancellationToken cancellation)
    {
        Uri address;
        try { address = Martlet.Home.HomeAssistantEndpoint.Normalize(RequiredString(arguments, "address")); }
        catch (Martlet.Home.HomeAssistantException error) { throw new ArgumentException(error.Message); }
        using var client = new Martlet.Home.HomeAssistantClient();
        try
        {
            var state = await client.OnboardingAsync(address, cancellation);
            return new
            {
                address = Martlet.Home.HomeAssistantEndpoint.Display(address), homeAssistant = true,
                onboarding = new { owner = state.Owner, coreConfig = state.CoreConfig, analytics = state.Analytics, integration = state.Integration, done = state.Done },
                next = state.NeedsOwner ? "Set up (Martlet can create its owner account)" : "Sign in with Home Assistant"
            };
        }
        catch (Martlet.Home.HomeAssistantException error)
        {
            return new { address = Martlet.Home.HomeAssistantEndpoint.Display(address), homeAssistant = false, failure = error.Failure.ToString(), problem = error.Message };
        }
    }

    private static async Task<object> HomeAssistantFindAsync(JsonElement arguments, CancellationToken cancellation)
    {
        var seconds = arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty("seconds", out var value) &&
            value.ValueKind == JsonValueKind.Number ? Math.Clamp(value.GetDouble(), 1, 10) : 2.5;
        var found = await Martlet.Home.HomeAssistantDiscovery.FindAsync(TimeSpan.FromSeconds(seconds), cancellation);
        return new
        {
            service = Martlet.Home.HomeAssistantDiscovery.ServiceType, listenedSeconds = seconds, count = found.Count,
            found = found.Select(f => new { name = f.Name, address = Martlet.Home.HomeAssistantEndpoint.Display(f.Address), version = f.Version })
        };
    }

    /// <summary>Companion › Prompts as saved in a data directory's settings.json: each prompt's state, and one prompt's
    /// effective text on request.</summary>
    private static async Task<object> PromptsStatusAsync(JsonElement arguments, CancellationToken cancellation)
    {
        var id = OptionalString(arguments, "id");
        if (id is not null && Martlet.Core.Settings.PromptCatalog.Find(id) is null) throw new ArgumentException($"Unknown prompt '{id}'.");
        var loaded = await new Martlet.Core.Settings.SettingsStore(DataDirectory(arguments)).LoadAsync(cancellation);
        var prompts = loaded.Settings?.Prompts;
        var state = loaded.State switch
        {
            Martlet.Core.Settings.SettingsLoadState.Loaded => "loaded",
            Martlet.Core.Settings.SettingsLoadState.FirstRun => "none",
            _ => "unreadable"
        };
        string Of(string prompt) => prompts?.Overrides.TryGetValue(prompt, out var text) != true ? "builtin"
            : string.IsNullOrWhiteSpace(text) ? "empty" : "edited";
        var list = Martlet.Core.Settings.PromptCatalog.All.Select(p => new
        {
            id = p.Id, group = p.Group, title = p.Title, placeholders = p.Placeholders, state = Of(p.Id),
            characters = Martlet.Core.Settings.PromptSettings.Text(prompts, p.Id).Length
        }).ToArray();
        return new
        {
            state, problem = loaded.Error?.Summary, total = list.Length,
            edited = list.Count(p => p.state == "edited"), emptied = list.Count(p => p.state == "empty"), prompts = list,
            prompt = id is null ? null : new { id, state = Of(id), text = Martlet.Core.Settings.PromptSettings.Text(prompts, id) }
        };
    }

    /// <summary>smart-home.json in a data directory (the file name and fields match Martlet.Desktop's HomePreferences). The
    /// token lives in Windows Credential Manager and is never read here.</summary>
    private static object SmartHomeStatus(JsonElement arguments)
    {
        var path = Path.Combine(DataDirectory(arguments), "smart-home.json");
        if (!File.Exists(path)) return new { state = "none", connected = false };
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            var root = document.RootElement;
            string Text(string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
            bool Flag(string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
            var tokenSaved = root.TryGetProperty("CredentialId", out var id) && id.ValueKind == JsonValueKind.String &&
                Guid.TryParse(id.GetString(), out var guid) && guid != Guid.Empty;
            return new
            {
                state = "loaded", connected = Text("Address").Length > 0 && tokenSaved, address = Text("Address"), name = Text("LocationName"),
                version = Text("Version"), tokenSaved, control = Flag("Control"), allowSensitive = Flag("AllowSensitive"), modelTools = Flag("ModelTools"),
                shared = Flag("FollowShare"), sharedBy = Text("SharedBy"),
                sharedRevision = root.TryGetProperty("SharedRevision", out var revision) && revision.TryGetInt64(out var number) ? number : 0
            };
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return new { state = "unreadable", problem = error.Message };
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
