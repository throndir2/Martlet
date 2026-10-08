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

    /// <summary>Each voice host role's loopback port (deploy/host/roles/*/role.conf); declared before <see cref="Tools"/>,
    /// which lists its keys.</summary>
    private static readonly IReadOnlyDictionary<string, int> VoiceEnginePorts = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["chatterbox"] = 50083, ["chatterbox-original"] = 50089, ["chatterbox-nano"] = 50088, ["f5"] = 50080, ["xtts"] = 50081,
        ["gpt-sovits"] = 50082, ["dia"] = 50084
    };

    private static readonly object[] Tools =
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
        }),
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
        }),
        Tool("logs_export", "Diagnostics > Save logs to share, from a data directory: every log line it has (this PC's logs and the other " +
            "computers' lines log sharing collected) saved in one new ZIP at outputPath (absolute, ending .zip, in an existing folder; an " +
            "existing file is never replaced) with about.txt (where and when, computers and parts, what logs can contain) and " +
            "martlet-logs.txt (every line once, oldest first). Returns the files, line, computer, error and warning counts and about.txt's " +
            "text. Writes only that file; contacts nothing.", new
        {
            outputPath = new { type = "string" },
            dataDirectory = new { type = "string" }
        }, ["outputPath"]),
        Tool("logs_share_selftest", "Rehearse shared logs (Diagnostics: every computer's logs on every computer) end to end with the " +
            "production code: two real gateways on 127.0.0.1 (pinned TLS, signed requests, in-memory logs.json) and three simulated " +
            "desktops with real log folders, the desktop's paired client and the real log sharing engine (Martlet.Core.Logs.LogShare). " +
            "Walks a desktop's lines reaching both hosts, a desktop that reaches only one host whose lines still reach the other, each " +
            "host's own gateway lines reaching the other host and every desktop, every line kept once however often it is delivered, a " +
            "host that was down catching up, a host that lost its newest lines in a power cut getting them back, the copy of everyone's " +
            "lines surviving a restart, Save logs to share holding every computer " +
            "and an unsigned request refused. Synthetic lines only; loopback only; the folder is deleted.", new { }),
        Tool("host_connections_selftest", "Rehearse how the desktop connects to a paired host and reports its status, with the " +
            "production code: one real gateway on 127.0.0.1 (pinned TLS, signed requests) behind a loopback TCP forwarder stopped and " +
            "started at the same address, and a simulated desktop checking it as the 15-second sync does (a new paired connection per " +
            "check) and logging through the desktop's status tracker. Checks that twelve checks share one kept TCP and TLS connection " +
            "(connections dialed and accepted are counted), that one missed check is not reported, that a host that stops is logged " +
            "once as stopped answering with the refused address named, and once as answering again when it is back, and that a host " +
            "missing every other check is never reported. Returns the log lines. Loopback only; writes nothing.", new { }),
        Tool("latency_report", "Summarize voice latency from the desktop log's reply latency lines: for the newest replies, how long " +
            "from when you stopped talking (or sent your message) to the first audio, each step's milliseconds (end of speech, " +
            "speech-to-text, preparing, Thinking connection, hidden reasoning, first sentence, voice synthesis, speakers...), the " +
            "median and 90th percentile of the total and of each step, the slowest steps and the models used. Read-only; starts " +
            "no audio, network or provider request.", new
        {
            replies = new { type = "integer", minimum = 1, maximum = LatencyReport.MaximumReplies },
            dataDirectory = new { type = "string" }
        }),

        Tool("ui_connect", "Attach to an already-running Martlet.Desktop (or, on a Windows dev run, Martlet.Companion) process in this interactive session.", new
        {
            pid = new { type = "integer", minimum = 1 }
        }, ["pid"]),
        Tool("ui_snapshot", "Inspect automation IDs, enabled state and selected non-secret status fields of attached Martlet windows " +
            "(with a status line's tooltip details as help), " +
            "and whether each window can be resized, minimized and maximized. With layout, each control also returns its screen " +
            "bounds and, for text, where its first line of text sits (geometry only, never the text), and each window its bounds " +
            "and its monitor's work area. idPrefix keeps only controls whose automation ID starts with it (the first 200 controls are " +
            "returned, so a long page's later sections need it).", new
        {
            layout = new { type = "boolean" }, idPrefix = new { type = "string" }
        }),
        Tool("ui_click", "Invoke an automation-ID control. Only safe navigation controls work without --allow-ui-effects. With " +
            "several windows that have the control (side-by-side run windows each have HostRunCancel), window names the one to use: " +
            "its title as ui_snapshot lists it (for example \"Martlet - Start Docker Desktop\"). With focus true the control takes the " +
            "keyboard focus first, as a mouse click gives it (its window comes to the front).", new
        {
            id = new { type = "string" }, window = new { type = "string" }, focus = new { type = "boolean" }
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
        Tool("ui_set_range", "Set an enabled slider to a number within its range, such as Companion › Voice's VoiceVolume " +
            "(0 to 100); the result reports the slider's value and range (requires --allow-ui-effects).", new
        {
            id = new { type = "string" }, value = new { type = "number" }
        }, ["id", "value"]),
        Tool("ui_move", "Move a movable control by dx, dy screen pixels through UI Automation's Transform pattern and report its " +
            "bounds before and after: the character overlay's MoveAvatar moves the character like a drag. ui_snapshot reports " +
            "movable for such controls (false while the character's position is locked, when this is refused). Requires " +
            "--allow-ui-effects.", new
        {
            id = new { type = "string" },
            dx = new { type = "integer", minimum = -DesktopAutomation.MaximumMove, maximum = DesktopAutomation.MaximumMove },
            dy = new { type = "integer", minimum = -DesktopAutomation.MaximumMove, maximum = DesktopAutomation.MaximumMove }
        }, ["id", "dx", "dy"]),
        Tool("character_touch", "Tap the showing character like a left click that doesn't drag, at x, y (fractions 0 to 1 of the " +
            "character overlay's drawing, +y down; unzoomed, its head is near 0.5, 0.15), and return the renderer's hit test as " +
            "last: n, x, y, hit, zone (head, hair, face, body, arm, hand, leg or foot), hitAreas and drawables (Live2D), bone, node, " +
            "hair, mesh and material (VRM). Martlet then plays its tap reaction (the desktop log records 'The character was " +
            "tapped on the ...'). holdMs presses that long (600 or more is a hold, up to 10000), repeat taps the same point up to " +
            "20 times gapMs apart (default 150), and taps ([{x, y, holdMs}], up to 20) taps a sequence of points instead. With " +
            "Companion > Character > Touch zones showing, noticed reads what Martlet noticed after settleMs: waiting (the touch " +
            "line that waits for a reply and when a touch reply starts), last (which reply took the last touches and what " +
            "Thinking was told) and zone (TouchZonesLast). Tapping requires --allow-ui-effects; without x, y or taps it only " +
            "reads the last tap.", new
        {
            x = new { type = "number", minimum = 0, maximum = 1 },
            y = new { type = "number", minimum = 0, maximum = 1 },
            holdMs = new { type = "integer", minimum = 0, maximum = 10_000 },
            repeat = new { type = "integer", minimum = 1, maximum = DesktopAutomation.MaximumTaps },
            gapMs = new { type = "integer", minimum = 0, maximum = 10_000 },
            settleMs = new { type = "integer", minimum = 0, maximum = 15_000 },
            taps = new
            {
                type = "array", maxItems = DesktopAutomation.MaximumTaps,
                items = new
                {
                    type = "object",
                    properties = new
                    {
                        x = new { type = "number", minimum = 0, maximum = 1 },
                        y = new { type = "number", minimum = 0, maximum = 1 },
                        holdMs = new { type = "integer", minimum = 0, maximum = 10_000 }
                    },
                    required = new[] { "x", "y" }
                }
            }
        }),
        Tool("character_stroke", "Stroke the showing character, whose position must be locked, like pressing the left button and " +
            "dragging across it: along points [[x, y], ...] (2 to 200, fractions 0 to 1 of the character overlay's drawing, +y " +
            "down; unzoomed, its hair is near 0.5, 0.1 and its face near 0.5, 0.2), one point every stepMs milliseconds (10 to " +
            "2000, default 40). Martlet hit-tests the path, plays each crossed zone's reaction (the first zone's emote held until " +
            "the stroke ends) and records the stroke in the touch ledger. Returns the overlay's stroke record as last.stroke (n, " +
            "samples, hits, ms, coarse zones crossed); Companion > Character > Touch zones' CharacterPhysicalLast reads Martlet's " +
            "summary (zones, pace, passes). last.physical is the last settled move, zoom or pan (kind, dx, dy, from and to monitors, " +
            "zoomFrom, zoomTo, focus). Requires --allow-ui-effects; without points it only reads the last stroke.", new
        {
            points = new { type = "array", items = new { type = "array", items = new { type = "number", minimum = 0, maximum = 1 }, minItems = 2, maxItems = 2 } },
            stepMs = new { type = "integer", minimum = 10, maximum = 2000 }
        }),
        Tool("character_face", "Read where Martlet draws over the showing character's face (its blush levels blush, blush_deep and blush_fierce, and overlay emotes " +
            "such as hearts or a sweat drop), samples times (1 to 60, default 1) gapMs apart (0 to 5000, default 250), as each frame " +
            "is drawn. Each reading (faces) has n, found, tracking (mesh: pinned to the Live2D model's own face meshes; bones: a " +
            "VRM's head bone; estimate: a Live2D model's head angles, when no face meshes were found), x, y and width (fractions of " +
            "the character overlay's drawing, +y down), tilt (degrees, clockwise), cheekLeft and cheekRight (x, y; visible, 0 to " +
            "1 as the cheek turns away; across, the cheek's width against the face's, below 1 on a turned head's far cheek; and " +
            "the renderer's hit test there: hit, drawables, bone, mesh), the overlays showing and pinned (Live2D: carriers, the " +
            "mesh vertices the face rides on, and milliseconds, how long finding them took at load). summary says which tracking " +
            "was used, how far the face moved (x, y, width, tilt) and, per cheek, the share of readings over the character, " +
            "what it was mostly over and for what share, the least it showed and its across range. Reading changes nothing, so " +
            "it needs no --allow-ui-effects.", new
        {
            samples = new { type = "integer", minimum = 1, maximum = DesktopAutomation.MaximumFaceSamples },
            gapMs = new { type = "integer", minimum = 0, maximum = 5000 }
        }),
        Tool("ui_tray", "Martlet's notification-area icon. \"status\" (default) reads whether the icon is shown, whether the main " +
            "window is visible or hidden in the notification area, whether its menu is open (menuOpen, with the menu's menuBounds " +
            "[x, y, width, height] in physical screen pixels) and whether Martlet still runs. \"open\" and \"menu\" send the icon " +
            "what Explorer sends for a left click (show Martlet) and a right click (its menu, which opens beside the click; " +
            "ui_snapshot then lists the Tray* items), at the mouse pointer or at optional x, y (physical screen pixels, as Explorer " +
            "reports them). \"close\" presses the main window's close button, which hides Martlet in the notification area by " +
            "default or exits it, so it requires --allow-ui-effects.", new
        {
            action = new { type = "string", @enum = DesktopAutomation.TrayActions },
            x = new { type = "integer", minimum = short.MinValue, maximum = short.MaxValue },
            y = new { type = "integer", minimum = short.MinValue, maximum = short.MaxValue }
        }),
        Tool("voices_status", "Read voice recognition and Parakeet status from a data directory: on/off choices (recognition is on " +
            "unless turned off), whether a Martlet folder (optional absolute martletDirectory, default the installed release's Desktop " +
            "folder) includes the voice recognition runtime and models, whether any Parakeet model is downloaded (parakeet) and, in " +
            "parakeetModels, each model Companion > Listening > Parakeet in Martlet offers (id, name, languages, download size, " +
            "downloaded, its NOTICE, recommended for Windows' display language, in use), the Listening route and its Parakeet model, " +
            "the Parakeet model that hears you on this PC's processor when that route (a paired host or OpenAI) fails, or why none " +
            "(standIn), " +
            "and counts of known voices (never names, voiceprints or audio), including how many go by a name of the companion's own " +
            "(from the saved personas) or a placeholder such as \"no name yet\", and the most names one voice has; and clips: whether " +
            "People keeps the last few clips of voices not named yet (voice-clips.txt) and how many clips over how many voices (never " +
            "the audio). Read-only; no audio, network or models run.", new
        {
            dataDirectory = new { type = "string" },
            martletDirectory = new { type = "string" }
        }),
        Tool("turn_judge_check", "Companion > Listening > Judge when I finish talking: load the Smart Turn v3.2 end-of-turn model " +
            "and ONNX Runtime bundled in martletDirectory (optional absolute path, default the installed release's) through the " +
            "production SmartTurnEngine and judge finished and unfinished phrases a Windows voice says (System.Speech rendered to " +
            "memory, never played), each ending 260 ms into the pause as always listening asks it: loadMs, each phrase's expected " +
            "and judged verdict, probability and judgeMs, agreed and medianJudgeMs. Its gate part steps the production " +
            "EndOfTurnGate with the plain 800 ms pause frame by frame: a complete answer ends the turn at 300 ms, an incomplete one " +
            "waits for 1600 ms, a slow or failed judge leaves it to 800 ms. ok when the median judge time is at most 100 ms and the " +
            "gate ends each case where expected (agreement on a synthetic voice is informative only). Nothing is recorded, played, " +
            "downloaded or sent.", new
        {
            martletDirectory = new { type = "string" }
        }),
        Tool("early_reply_check", "Companion > Listening > Start replies early, rehearsed headless in real time with the production " +
            "EndOfTurnGate, EarlyReplyGate, request comparison (EarlyAsk) and the conversation runtime's held turn (StartEarly, " +
            "Release) through the Chat Completions adapter, a paired host's voice stream and the playback sink. FIXTURES, NOT AI: a " +
            "quick transcript after sttMs (default 90), a judge answer after judgeMs (26), a Chat Completions endpoint on 127.0.0.1 " +
            "whose first words come after thinkingMs (200) and a host voice whose first audio comes after voiceMs (350); speakers " +
            "open no device. scenario (default all): incomplete (the judge finds the pause unfinished and the longer 1600 ms pause " +
            "ends the turn), plain (no judge: the 800 ms pause), complete (the judge ends the turn at about 300 ms), resumed (you go " +
            "on talking 700 ms into the pause, then pause again) or changed (the final transcript differs from the quick one). " +
            "Each runs with replies started early and without: per run the decisions with their times, when the turn ended, the " +
            "first audio after the turn ended, starts, cancelled, the outcome (promoted or changed), Thinking requests and aborted " +
            "ones, voice pieces, samples played and captions shown before the turn ended (both 0), the live floor (production " +
            "LiveFloor fed as the desktop feeds it: its level and reply holds when the turn ended and once the reply was done, its " +
            "changes among the decisions) and the reply latency line; " +
            "savedMs per scenario. ok when each scenario does what it should (promoted, let go or restarted as described, nothing " +
            "heard before the turn ended, the floor Live and held by the one reply started early when the turn ended and by none " +
            "once the reply was done, and for incomplete, plain and resumed the first audio at least 60% of thinkingMs + " +
            "voiceMs sooner). Nothing is recorded, played or sent off this PC.", new
        {
            scenario = new { type = "string", @enum = new[] { "all", "incomplete", "plain", "complete", "resumed", "changed" } },
            thinkingMs = new { type = "integer", minimum = 0, maximum = 3000 },
            voiceMs = new { type = "integer", minimum = 0, maximum = 3000 },
            sttMs = new { type = "integer", minimum = 0, maximum = 1000 },
            judgeMs = new { type = "integer", minimum = 0, maximum = 500 }
        }),
        Tool("parakeet_check", "Companion > Listening > Parakeet in Martlet: load each Parakeet model downloaded in speechDirectory " +
            "(optional absolute path, default the data directory's speech folder, where the desktop downloads them; or name them in " +
            "models) through the production ParakeetEngine with the sherpa-onnx runtime from martletDirectory, and transcribe phrases " +
            "a Windows voice says (System.Speech rendered to memory, never played; optional phrases, up to 8 English sentences). Per " +
            "model: loadMs, memoryMb (process memory it added), each phrase's transcript, word errors and transcribeMs, the word " +
            "error rate and the median time; ok when every model ran with at most 20% word errors. Also returns voices_status's " +
            "Parakeet part, with standIn: the model that hears you here when Listening's own route fails. Nothing is downloaded, " +
            "recorded or played; nothing leaves this PC.", new
        {
            dataDirectory = new { type = "string" },
            martletDirectory = new { type = "string" },
            speechDirectory = new { type = "string" },
            models = new { type = "array", maxItems = 3, items = new { type = "string", @enum = Martlet.Sherpa.ParakeetModels.All.Select(m => m.Id).ToArray() } },
            phrases = new { type = "array", maxItems = 8, items = new { type = "string", maxLength = 200 } }
        }),
        Tool("voices_naming_check", "Rehearse learning names (Companion › People) with the production checks and changes on a fixture " +
            "voice list in memory: the companion's own names (persona names, \"You are ...\" in persona text, \"I'm ...\" in Martlet's " +
            "reply) are never given to a voice, a heard voice drops one it learned by mistake, a voice keeps many names and shows the " +
            "one it asked for (CALL), a wrong learned name is dropped (NOT) but never one the owner typed, names go only to voices " +
            "heard, and two voices merge into the owner's (SAME) at most once per exchange. Optional dataDirectory supplies the saved " +
            "personas' names (one more scenario); optional answer (a Thinking answer of NAME/CALL/NOT/SAME lines about V1-V3) and " +
            "reply (Martlet's reply) are checked against the same fixture. Never reads or writes the saved voice list; no audio, model " +
            "or network.", new
        {
            dataDirectory = new { type = "string" },
            answer = new { type = "string" },
            reply = new { type = "string" }
        }),
        Tool("voices_engine_check", "Run the voice recognition engine that ships in a Martlet folder (optional absolute " +
            "martletDirectory, default the installed release's Desktop folder): load its sherpa-onnx runtime and the WeSpeaker and " +
            "pyannote models, take a voiceprint of a generated test tone and, for optional wavFiles (absolute paths to 16 kHz mono " +
            "PCM16 WAV files of at most a minute), how many voices each has, their clean seconds and how alike the files' main " +
            "voices are (cosine; Martlet calls 0.70 the same person). Returns counts and scores only, never audio or voiceprints; no " +
            "data directory, device or network is used.", new
        {
            martletDirectory = new { type = "string" },
            wavFiles = new { type = "array", items = new { type = "string" } }
        }),
        Tool("f5_voices", "List Martlet's starter voices (key, name, female, cute, licence, transcript, format; each clip is checked " +
            "against its SHA-256 and F5's reference rules; a new voice list starts with them, after which they are ordinary voices) and " +
            "the default voice; from a data directory, the shared speaking-voice list (speaking-voices.json: live voices, which starter " +
            "voices are in it or removed, how many of the owner's own, tombstones, the voice chosen on all computers and, for each voice " +
            "made from several recordings, how many, their lengths and which engines learn from each or hear them joined), this PC's " +
            "recordings (the F5 voice store: which starter voices, how many own and which is applied); which voice the speaking route " +
            "uses and on which self-hosted engine, host and model; and the voice engines (Chatterbox " +
            "Turbo, the default; F5-TTS; XTTS-v2; GPT-SoVITS; Dia: host role, gateway route, model, weights licence, GPU memory, reference " +
            "length bounds, tag catalog, summary, languages, whether it learns from several recordings, the feature chips Companion > " +
            "Voice > Voice engine shows, its rundown (voice cloning, laughs & sighs, emotions: yes, partly or no) and where it runs " +
            "(GPU with its typical and peak graphics memory, CPU or online); the same rundown for the Windows and OpenAI voices; each " +
            "starter voice lists the engines that can clone it and its language) with the one chosen on this desktop " +
            "(never own voices' names, transcripts or audio). Plays nothing and contacts nothing.", new
        {
            dataDirectory = new { type = "string" }
        }),
        Tool("voice_recording_check", "What Companion > Voice > Add a voice does with an audio or video file (path, read on this PC): " +
            "whether it is readable, the kind of file (Ogg files name their codec: OGG (Vorbis), OGG (Opus)), its channels and sample " +
            "rate, and the mono 16-bit WAV Martlet would keep and Play plays (sample rate, length, bytes, SHA-256, peak and RMS level " +
            "in dBFS, kept as is or converted), with the line Add a voice shows, or why it can't be used. Uses " +
            "the production converter; saves, plays and contacts nothing and never returns the path or the audio.", new
        {
            path = new { type = "string" }
        }, ["path"]),
        Tool("voice_tags", "Show how a reply's voice tags are handled for a self-hosted voice engine (engine key, default the " +
            "default engine Chatterbox Turbo; \"none\" for a voice without tags such as OpenAI or Windows): the engine's tag catalog in " +
            "its own syntax with each tag's engine-independent cue, split into its non-word sounds and tones of voice, the Thinking " +
            "prompt it adds (Companion > Prompts > Voice sounds " +
            "and tones, from dataDirectory's settings when given), the pieces the real speech segmenter hands that engine for a " +
            "spoken reply, broken where the persona's speech breaks allow (Personality > Where the voice pauses: dataDirectory's " +
            "persona by name, else the one Martlet uses, else the defaults;             \"breaks\" overrides periods, questionMarks, " +
                        "exclamationMarks and shortEndingWords; commas, semicolons and dashes never break), the text the chat and captions show and, with characterTags (the character's " +
            "tags such as \"{blush}\"), the character cues found in each spoken piece (piece index, -1 for cues after the last " +
                        "words; tag; character offset). Other spellings of a tag count as it ([nod] or *nods* for {nod}, (sighs) for [sigh]): " +
                        "acted lists what the reply's tags did (each tag as the character or engine spells it, its kind, name and the other " +
                        "spelling written) and note the line the talk window shows under the reply (\"Tone: happy. Sound: laugh. Emotes: " +
                        "nod.\"). Synthesizes and contacts nothing.", new
        {
            text = new { type = "string" }, engine = new { type = "string" }, dataDirectory = new { type = "string" },
            characterTags = new { type = "array", items = new { type = "string" } }, persona = new { type = "string" },
            breaks = BreaksSchema()
        }, ["text"]),
        Tool("cluster_status", "Read shared \"who does what\" sync from a data directory: whether sync is on (on by default, " +
            "\"off\" only after the owner turned it off) and this PC's copy of the plan (each job's host, failover and which device " +
            "changed it last; each host's roles). Read-only; contacts nothing and returns no addresses or keys.", new
        {
            dataDirectory = new { type = "string" }
        }),
        Tool("network_status", "Read this PC's Martlet network from a data directory (network.json and whether its network key " +
            "exists): member, waiting for approval (with the check number) or in no network; the network ID; each desktop and host " +
            "in the roster (ID, name, removed, who changed it last); hosts paired on purpose (adopt) and forgotten here (ignored); " +
            "each paired host in hosts.json with how many outside addresses are kept with its pairing (pairedHosts). " +
            "Read-only; contacts nothing and returns no keys or addresses.", new
        {
            dataDirectory = new { type = "string" }
        }),
        Tool("outside_reachability_check", "Check how each host in this PC's Martlet network (network.json in a data directory) can " +
            "be reached: its home address and each owner-set outside address (overlay or port forward), each dialed directly, checked " +
            "against the host key pinned in the roster and asked GET /health/live (no credential, nothing else). Returns per host which " +
            "address answered, in how many ms, why the others didn't (refused, no answer in time, name not found, another key) and which " +
            "one a desktop would use (home first). Contacts the owner's hosts only when contactHosts is true; otherwise it lists what it " +
            "would check. Returns host IDs and address numbers, never the addresses.", new
        {
            dataDirectory = new { type = "string" },
            contactHosts = new { type = "boolean" }
        }),
        Tool("network_selftest", "Rehearse the Martlet network end to end with the production code: three real gateways on " +
            "127.0.0.1 (pinned TLS, volatile credentials) and two simulated desktops using the desktop's network client and sync " +
            "engine (found, bind hosts, join with a check number, pair every member with every host by itself, refuse forged keys " +
            "and rosters, remove a desktop and a host). Loopback only; writes nothing to disk or the credential vault.", new { }),
        Tool("signin_lab", "A live sign-in lab for the desktop on a disposable data directory, so the Sign-in from outside " +
            "window can be driven against a real paired host. action \"start\": a real gateway on 127.0.0.1 (Martlet.NodeLinkCheck " +
            "signin-lab) with an owner account, an OpenID Connect provider (an issuer in that process) and an allowed identity; it " +
            "pairs the desktop of dataDirectory (hosts.json there; the secret in the lab credential folder of " +
            "MARTLET_LAB_CREDENTIALS, never Windows Credential Manager, so the MCP server and desktop must run with " +
            "Invoke-MartletMcp.ps1 -LabCredentials), and a simulated laptop signs in with that identity and keeps syncing the network. " +
            "\"status\": what the lab sees (whether the desktop bound the host, whether the laptop is a member, was removed, can still " +
            "use the host, the laptop's network events). \"stop\": ends it (it also ends with this server).", new
        {
            action = new { type = "string", @enum = new[] { "start", "status", "stop" } },
            dataDirectory = new { type = "string" }
        }, ["action"]),
        Tool("signin_selftest", "Rehearse joining from outside home by signing in, end to end with the production code: a real " +
            "gateway on 127.0.0.1 (pinned TLS, in-memory signin.json and network.json), a member desktop at home that sets up the " +
            "owner account (password plus a real authenticator secret and recovery codes) and makes an invite, and a laptop that " +
            "only has the invite: it pins the host (reached by name, so only the pin is trusted), is refused with a wrong password, " +
            "a reused code and a forged pin, signs in, asks to join and is let in by the home PC on the host's sign-in attestation " +
            "with no check number; then an OpenID Connect provider (an issuer in this process, a simulated browser and the desktop's " +
            "real loopback redirect) is refused until the home PC allows the identity it saw, and joins the same way; a Steam " +
            "assertion is confirmed by the host; a non-member can't change sign-in and removing the owner account revokes the " +
            "laptop. Reports " +
            "each step; loopback only, writes nothing to disk or the credential vault.", new { }),
        Tool("nearby_status", "Read whether this PC lets Martlet on the owner's other computers find it and ask to use its hosts " +
            "(on by default, \"off\" only after the owner turned it off) and which paired hosts it could share from hosts.json (hosts " +
            "it runs or reaches over SSH; this PC's own host service set up from the host dashboard is found from Docker by the " +
            "desktop, not here). Read-only; contacts nothing and returns no addresses, SSH targets or keys.", new
        {
            dataDirectory = new { type = "string" }
        }),
        Tool("virtualization_status", "Read whether Windows is ready for Docker Desktop's WSL 2 engine (virtualization in the firmware, " +
            "the Windows hypervisor, Virtual Machine Platform, Windows Subsystem for Linux, their host services, WSL version and status), " +
            "pending and required restarts, blockers and recovery guidance, whether Docker Desktop is installed and running, its engine " +
            "state and the Windows check its engine last failed when it started (\"Virtualization support not detected\"), and any setup " +
            "Martlet continues after a Windows restart. Read-only; starts no VM, changes nothing and returns no distribution names.", new
        {
            dataDirectory = new { type = "string" }
        }),
        Tool("host_service_status", "Read this PC's own Martlet host service on Docker Desktop the way the host dashboard does: its stage " +
            "(DockerMissing, DockerNotRunning, NotSetUp, Stopped, Running), Martlet version, host ID, whether its published address is " +
            "still this PC's and answers, the installed roles, the Martlet network it joined (state and the desktops paired " +
            "with it) and sharedGpu, the warning Martlet shows when its voice engine shares the graphics card with other roles. Reads " +
            "Docker and the gateway's nonsecret files only (never its agent token, keys or pairings); returns no addresses and " +
            "changes nothing.", new { }),
        Tool("node_link_check", "Run commands between Martlet computers end to end on this PC's loopback: the real gateway (pinned TLS, " +
            "pairing, signed requests, the command mailbox and its storage), the desktop's real client and agent loop with a fixture " +
            "runner, two fixture devices. Checks that only known commands are accepted, only the host's agent (local token) takes them, " +
            "output and outcomes reach the sender, secrets never appear in lists or saved copies, cancel works, commands run side by " +
            "side (a second change to one role waits for the first; an update waits for what runs and holds what was sent after it; " +
            "an older agent still gets one at a time), an update that waits holds the queue and the sender sees what its command " +
            "waits behind, and commands survive a restart. Contacts nothing " +
            "outside loopback and touches no real credentials, Docker or installs.", new { }),
        Tool("host_engine_check", "Check that a Martlet host runs changes side by side and makes only colliding ones wait: runs " +
            "this checkout's real martlet-host engine in one disposable ubuntu:24.04 container (no network, never pulled, removed " +
            "afterwards; Martlet's own host containers and volumes are never touched) against a fixture setup. A change holds and " +
            "records its locks; read-only commands still run; status names the holders; an automatic run (no terminal, no --yes) " +
            "stops at once with exit 75 and MARTLET-BUSY, changing nothing; an attended run waits and gives up after " +
            "MARTLET_LOCK_WAIT; adds of different roles run side by side while the same role or exclusive group waits; setup and " +
            "update wait for every change and hold back later ones; a waiting run continues when the holder is killed, and so does " +
            "a run without a terminal or --yes told to wait (Update hosts now); no stale lock or record remains; the journal " +
            "records it; the desktop's reader reads the busy line. With a " +
            "fake docker CLI it also checks the Docker method: setup does not replace the network holder while an engine session " +
            "(an add) runs in it (an automatic setup stops with MARTLET-BUSY, an attended one waits, then replaces it), and an engine " +
            "left in a replaced holder's namespace stops at once. A fixture role with two variants (like the stt role's whisper and " +
            "Parakeet engines) shows describe listing each variant's own choices, suggestion and GPU option with their condition, an " +
            "add keeping a variant's own choices, GPU option and prepare step, and a switch stopping the previous variant only once " +
            "the chosen one is prepared (also when a failed switch is run again). Returns notRun when Docker or the image is missing.", new { }),
        Tool("host_supply_check", "Check that Martlet sets up a native Ubuntu host without internet access by sending what it needs " +
            "from this PC: with the production HostSupplier and HostCheckout and this checkout's real martlet-host, in one disposable " +
            "ubuntu:24.04 container on an internal Docker network (a private LAN address, no way out; never pulled, removed " +
            "afterwards). This PC downloads the .NET SDK and the gateway's NuGet packages for real (about 240 MB the first time, cached " +
            "in cacheDirectory); the online command stops plainly (no git, no internet) instead of running a missing engine; the files " +
            "arrive intact over a tar stream; setup builds the gateway, creates its identity and starts it healthy without internet; " +
            "a second pass sends nothing and removes the installed SDK's archive; update rebuilds offline. FIXTURE systemctl and ip " +
            "stand in for systemd and iproute2. Returns notRun when Docker or the image is missing.", new
        {
            cacheDirectory = new { type = "string", maxLength = 260 }
        }),
        Tool("host_update_check", "Rehearse how Martlet coordinates its own host service updates, with the desktop's production " +
            "update tracker and busy reader: an Update host run window claims its host so the automatic pass leaves it to that run " +
            "(no second engine run that finds the host locked by Martlet's own update and reports it busy, for its pairing or as this " +
            "PC's own host service); overlapping routes end separately; an update started elsewhere is named as another update; a " +
            "host found current stops waiting and its stale note says it is updated; Update hosts now waits for another change; this " +
            "PC's own host service follows the app's version after an update by itself (in the background, giving way to another " +
            "route, this PC's own pending update and the conversation, retried when busy, settled once current). Pure logic: contacts " +
            "nothing and touches no Docker, host or data directory.", new { }),
        Tool("app_update_check", "Rehearse how Martlet installs its own update end to end with the desktop's production update " +
            "helper (the same script and hidden start) in a disposable folder: a stand-in for Martlet that exits, and a FIXTURE " +
            "standing in for the installer and the restarted Martlet. For an install another computer asked for and an automatic " +
            "one (from the notification area, whose installer fails), the helper waits for Martlet to exit, runs the installer with " +
            "no window at all (/VERYSILENT, no questions, no restart), records its exit code, logs every step in update.log and " +
            "starts Martlet again minimized (in the notification area when it was there); one you confirmed shows the installer's " +
            "progress window (/SILENT). Also checks the note Martlet leaves itself so the restarted Martlet shows the character and " +
            "listens again (read once, only what was on, ignored when stale). Installs nothing, starts no real Martlet and contacts " +
            "nothing.", new { }),
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
        Tool("outside_path_check", "Reach a host from outside home on real sockets: builds this checkout's Linux gateway in the .NET SDK " +
            "image and runs it (owner-init, owner-exposure, owner-pair, serve) in disposable aspnet containers on a Docker network " +
            "numbered from TEST-NET-3, its port published on 127.0.0.1, so every connection reaches it from 203.0.113.1 (an outside " +
            "source). The host's home address answers nothing; the desktop's real pairing client, connection, network sync and " +
            "HostRoutes must fall back to the outside address. Checks typed codes refused and a device card allowed from outside, " +
            "the roster signing the advertised address, home-fail then outside-succeed, the guard locking out a stranger and the audit " +
            "naming the outside source. Never pulls; removes its containers, volumes and network (keeps a NuGet cache volume).", new { }),
        Tool("mac_host_check", "Check the Mac host as far as this PC can: publishes this checkout's gateway self-contained for osx-arm64 " +
            "and osx-x64 in the .NET SDK image (the files the Mac app bundles), runs its macos-setup help and checks macos-status " +
            "refuses off a Mac, and checks the platform catalog for a Mac host's machine report (Ollama and whisper allowed, F5 and " +
            "Audio2Face refused for want of an NVIDIA GPU). Running on a Mac is reported NOT RUN. Never pulls.", new { }),
        Tool("exposure_selftest", "Rehearse a host reachable from outside home end to end with the production code: one real gateway " +
            "on 127.0.0.1 (pinned TLS) told to treat every connection as outside home, a desktop paired at home through its paired " +
            "client, and a stranger's pinned HTTPS client. Checks that pairing from outside is refused until the owner allows it, " +
            "guessing is locked out with a doubling Retry-After, routes anyone may call have a per-address budget, and the paired " +
            "desktop reads the security audit (/martlet/v1/security/audit) and the host log lines naming each source. Loopback only; " +
            "writes nothing to disk.", new { }),
        Tool("deep_thinking_role_selftest", "Rehearse the Deep thinking host role end to end with the production code: one real " +
            "gateway on 127.0.0.1 (pinned TLS) serving a host's Thinking route (the ollama role) and the deep-thinking role's own route " +
            "(martlet.gateway.deep-thinking-chat.v1), each relay over its own fixture Ollama (NOT AI) on its own graphics card, and a " +
            "simulated desktop using the desktop's paired client. Checks both routes and their models are advertised, Thinking's advertised route saves as the " +
            "desktop's job route (handing Thinking to the host), a think on the Deep thinking route runs " +
            "while a reply streams on Thinking's route (the reply finishes first), each request reaches its own Ollama (the think " +
            "with Thinking steps on), a Thinking pool job's request (the role's largest context window above the job's own budget) " +
            "is accepted, two thinks run at once on the role's two slots (advertised as the route's maximum_concurrency) " +
            "while a reply streams and a third gets job.busy, and that the chat client refuses a mismatched route. Loopback only; writes nothing to disk or " +
            "the credential vault.", new { }),
        Tool("gpu_priority_status", "Read GPU priority (live turn first) on every Martlet host paired in a desktop data directory " +
            "(hosts.json), through each host's own gateway (pinned TLS; the pairing secret from Windows Credential Manager only signs " +
            "the request, never returned): GET /martlet/v1/priority with each route's lane (live or pool) and graphics cards, each " +
            "card's hold state (held, live and pool requests running, holds), whether the whole host is held, the holds, the preempted " +
            "and refused counts, the last preemptions and refusals (what held the card) and placement warnings. A host older than " +
            "GPU priority is reported as such. Read-only: takes no hold and starts no work; contacts only paired hosts.", new
        {
            dataDirectory = new { type = "string" }
        }),
        Tool("gpu_priority_selftest", "Rehearse GPU priority (live turn first) end to end with the production code: real gateways on " +
            "127.0.0.1 (pinned TLS) with Thinking's Ollama relay (lane live) and the Deep thinking role's (lane pool) placed on graphics " +
            "cards as martlet-host places them, each over its own fixture Ollama server on loopback (NOT AI, no GPU used), a simulated " +
            "desktop with the desktop's paired client and its hold client (HostLiveGpuHold). Checks the host's GPU map and its warning " +
            "to pin each Ollama server to its own GPU, that a live reply stops a running think on the same card at once (job.preempted, " +
            "the Ollama request aborted), that a think is turned away while a live reply or a hold keeps the card (job.busy), holds " +
            "(renew, release, a 1 s hold that ends on its own, a hold that stops running work), that live replies never wait, that a " +
            "think on its own card runs on, and that an older host without holds leaves the live turn alone. Returns each step and the " +
            "host's GET /martlet/v1/priority report (per-card hold state, holds, last preemptions and refusals, warnings). Loopback " +
            "only; writes nothing to disk or the credential vault.", new { }),
        Tool("speaking_voices_selftest", "Rehearse the shared speaking voices end to end with the production code: two real gateways on " +
            "127.0.0.1 (pinned TLS, the real reference-voice relay route over a fixture voice service, NOT AI, with in-memory " +
            "speaking-voices.json and recordings) and two simulated desktops with real F5 voice stores in a temporary folder, using the " +
            "desktop's paired client and Martlet.F5's reconcile engine. Checks the starter voices, sharing the list and recordings, " +
            "speaking by recording SHA-256 alone, the one-time fallback that sends a recording a host lacks, a new desktop taking every " +
            "voice from a host, the shared choice, removal everywhere (host and desktop copies deleted), stale copies, a host restart and " +
            "upload checks. Loopback only; the temporary folder is deleted and the credential vault is not touched.", new { }),
        Tool("character_models", "Read the shared character models from a data directory (character-models.json and the copies in " +
            "character-models, docs/CLUSTER.md \"The shared character models\"): live characters and tombstones, total size, and for " +
            "each character its key (first 16 hex digits of its ID, as in CharacterModelState-<key>), renderer, files, pieces, size, " +
            "the computer it was added on, whether this PC's copy is complete and whether this PC shows it (avatar.json); copies " +
            "still waiting in character-models-incoming; and what this PC shows (built-in, a shared copy, a copy no longer listed or " +
            "a model file outside the list). Never returns character names or file paths. Read-only; contacts nothing.", new
        {
            dataDirectory = new { type = "string" }
        }),
        Tool("character_profiles", "Read the character profiles (Companion > Profiles) from a data directory: each profile's key (first " +
            "8 hex digits of its ID, as in CharacterProfileState-<key>), whether its personality is saved, what its look is (keep, " +
            "builtin, a listed character whose copy is ready or still copying here, or missing) and its voice (keep, listed or " +
            "missing); the profile switched to last; and which profile matches what Martlet uses now (the active persona, the look " +
            "in avatar.json and the voice the speaking route keeps or the shared list chose). Never returns names. Read-only; " +
            "contacts nothing.", new
        {
            dataDirectory = new { type = "string" }
        }),
        Tool("character_actions", "Read a character model's emotes and motions as Companion > Character > Emotes and motions uses " +
            "them (Martlet.Avatar.Hosting, docs/AVATARS.md \"Emotes and motions\"): modelPath (a .model3.json or .vrm on this PC) or the " +
            "model dataDirectory's avatar.json shows. Returns the renderer, the model's key, how many files the renderer reads (a VTube " +
            "Studio model's .vtube.json and loose .exp3/.motion3 files included) and what came from VTube Studio's settings, then each " +
            "expression, motion group and Martlet gesture the model's rig supports (nod, shake, tilt, bow, sway; the blush levels on every model, " +
            "faintest first: blush, blush_deep and blush_fierce, one showing at a time; the blush is the model's own ParamCheek or blush expression, " +
            "or a glow Martlet draws on the cheeks when it has neither, and Martlet draws the stronger levels over the model's own blush; " +
            "Live2D smile, surprise; VRM wave, shrug, bounce; " +
            "and the voice emotes linked to every voice sound and tone: laugh, chuckle, sigh, gasp, cough, clear_throat, groan, sniff, shush, inhale, exhale, " +
            "mumble, hum, sneeze, whistle, happy, sarcastic, angry, fear, crying, whispering, dramatic; and the overlay emotes drawn over the face of any " +
            "Live2D model or VRM with a head: sweat, anger, hearts, sparkles, tears, gloom, question, exclaim, sleepy, music; then the held face parts " +
            "eyes_up (Live2D ParamEyeBallY, VRM eye bones) and mouth_open (Live2D ParamMouthOpenY, a VRM's oh or aa mouth), which stay on with held " +
            "gestures that move other parts of the face) with what it changes, its tag, voice cue, when to use it (use: the owner's or the Thinking model's text, null when empty; hint: what the reply prompt says, which is Martlet's own hint while use is null), whether " +
            "it is on, its mode (brief, or lingering: stays on after {tag} until {/tag}; modeSaved false when it is the default, " +
            "vtsToggle when a VTube Studio ToggleExpression hotkey turns it on) and whether replies are offered it for engine (a voice engine key; \"none\" or absent: a voice without tags); the " +
            "saved settings (character-actions.json in dataDirectory) or the defaults from the model's names; " +
            "blushLevels (the model's blush levels, faintest first: level, n, id, kind, name, tag, mode and offered); the reply prompt and tags " +
            "(lingering emotes add their {/tag} off tags); with showing (tags the character would show now, as \"glasses\" or " +
            "\"glasses:12\" for 12 minutes) also showingNote, the line the newest message's notes get; " +
            "and the Thinking naming prompt. With voiceTag (a voice's tag such as \"[laugh]\" or \"(sighs)\", or a reply tag such as " +
            "\"{nod}\", \"{/glasses}\" or a combo's \"{flustered}\" or \"{/flustered}\"), also what it sets off (setsOff: kind, name and " +
            "whether it holds; with several expressions or motions on one cue, one picked at random; a combo's parts that are on) or " +
            "turns off (turnsOff: the lingering emote, or a combo's lingering parts) and the combo it names (combo). " +
            "Also the owner's combos for the model (combos: n as in CharacterComboTag-<n>, tag, each part's id, kind, name, tag, " +
            "whether it is on and its mode, use, hint, whether it is on, whether it has a lingering part and whether replies are " +
            "offered it); with combos (strings such as \"flustered: blush hearts nod | when flattered\", parsed as the Combos " +
            "section's boxes are) those replace the saved ones, or combosProblem says why they can't be saved. " +
            "With answer (a simulated Thinking reply such as \"1: blush | - | when shy\"), also what the " +
            "production parser makes of it. Reads only; contacts nothing and never returns the model's path.", new
        {
            dataDirectory = new { type = "string" }, modelPath = new { type = "string" }, engine = new { type = "string" },
            answer = new { type = "string" }, voiceTag = new { type = "string" }, showing = new { type = "array", items = new { type = "string" } },
            combos = new { type = "array", items = new { type = "string" } }
        }),
        Tool("character_physical_check", "What Martlet makes of a stroke across the locked character and of moves and zooms " +
            "(Martlet.Avatar.Hosting CharacterStrokes and CharacterPhysicalWords with Martlet.Conversation's TouchLedger), headless, " +
            "with no desktop and no model request. stroke is a JSON CharacterStroke {\"id\",\"phase\":\"end\",\"aspect\",\"samples\":" +
            "[{\"x\",\"y\",\"ms\",\"touch\":CharacterTouch or null}]} summarized against the touch zones saved for modelId in dataDirectory " +
            "(or the rough zones before any were found): zones crossed, main zone, ms, length, speed, pace (slow, steady or quick) and " +
            "passes. changes is a JSON array of RendererPhysical {\"kind\":\"moved|home|zoomed|zoom_reset|panned\",\"dx\",\"dy\"," +
            "\"screenWidth\",\"fromScreen\",\"toScreen\",\"zoomFrom\",\"zoomTo\",\"focus\"}. Returns each change's ledger kind and words, " +
            "the plain line the next reply would carry (\"They slowly stroked your hair 4 times, then moved you to their other " +
            "monitor.\"), the history line and whether it would start a reply on its own. noticeAll (default true) treats every zone as " +
            "having Martlet notices on; false uses the zones' own setting.", new
        {
            dataDirectory = new { type = "string" }, modelId = new { type = "string" }, stroke = new { type = "string" },
            changes = new { type = "string" }, noticeAll = new { type = "boolean" }
        }),
        Tool("character_touch_zones", "Companion > Character > Touch zones (Martlet.Avatar.Hosting CharacterTouchZones and TouchZoneDetection; " +
            "docs/AVATARS.md \"Touch zones\") with NO vision request: the zones Martlet knows (which are intimate), the step-by-step vision " +
            "requests (parts on the whole character, zones on each close-up, checks of the numbered boxes), what the production parser makes " +
            "of answer (a simulated vision reply about the whole picture: JSON boxes as fractions or named edges, pixels of a width x height " +
            "picture or Qwen-style 0..1000 bbox_2d grounding) bound to probe (a simulated renderer zones probe: {\"drawables\":[{\"id\",\"left\"," +
            "\"top\",\"right\",\"bottom\"}],\"bones\":[{\"bone\",\"x\",\"y\"}]} in page fractions) with crop (\"left,top,width,height\": where the " +
            "snapshot sat on the page), the zones saved for the model (modelPath, modelId or the model dataDirectory's avatar.json shows) in " +
            "character-touch-zones.json with what its last detection sent, and with touch (a CharacterTouch: {\"x\",\"y\",\"hitAreas\"," +
            "\"drawables\",\"bone\",\"node\",\"hair\",\"mesh\",\"material\",\"wholeX\",\"wholeY\"}) the zone it lands in, how it was found, what it " +
            "plays and what it tells the character. detect runs the production detection on snapshotPath (a PNG of the character, transparent " +
            "around it), composing and encoding every picture it would send (previewDirectory keeps them), with a FIXTURE - NOT AI stand-in " +
            "that answers from answer's zones (guess, a wrong first answer, makes the checks correct it; checks sets the rounds, 0 to 5; " +
            "failAt makes that request fail, as a model that stopped answering); with includeIntimate on (the default) the intimate zones " +
            "must be found: asked for again on the whole character, then worked out from the zones around them; it " +
            "reports each request, the steps and how far the found boxes are from answer's. save writes the parsed (or detected) zones (and " +
            "snapshotPath as their picture, and with detect the pictures sent; includeIntimate sets Include intimate zones) into an explicit, " +
            "disposable dataDirectory as Detect zones would. temperament (a simulated Thinking answer for Touch temperament: {\"groups\":{\"head\":" +
            "{\"attitude\":2,\"reactions\":[\"hearts\",\"blush\"],\"linger\":3}},\"zones\":{...},\"escalation\":{\"after\":3,...}}) or " +
            "personaId (the temperament saved in the dataDirectory's character-temperaments.json) decides what the touch plays when the " +
            "zone has no pick of its own, with repeats (touches in a row, for escalation); personality shows the request Thinking gets. " +
            "Contacts nothing; never returns the model's path.", new
        {
            dataDirectory = new { type = "string" }, modelPath = new { type = "string" }, modelId = new { type = "string" },
            answer = new { type = "string" }, width = new { type = "integer" }, height = new { type = "integer" },
            crop = new { type = "string" }, probe = new { type = "string" }, touch = new { type = "string" },
            save = new { type = "boolean" }, includeIntimate = new { type = "boolean" }, snapshotPath = new { type = "string" },
            temperament = new { type = "string" }, personaId = new { type = "string" }, personality = new { type = "string" },
            repeats = new { type = "integer", minimum = 1 }, detect = new { type = "boolean" }, guess = new { type = "string" },
            previewDirectory = new { type = "string" }, checks = new { type = "integer", minimum = 0, maximum = 5 },
            failAt = new { type = "integer", minimum = 1 }
        }),
        Tool("character_gaze", "Where the character looks (Companion > Character > Where the character looks, the overlay's Eyes " +
            "menu and Companion > Vision > Glances at your screen; docs/SCREEN_COMMENTARY.md \"Where the character looks\"): usual " +
            "is the usual gaze saved in a data directory's talk-preferences.json (GazeUsual: personality, mouse, near, ahead or " +
            "window; GazeFree: whether the character may change it in replies), the persona's gaze from character-temperaments.json " +
            "(the active persona, or personaId), the gaze that applies and who set it, what every reply is told about it and the " +
            "note while its own choice holds the eyes. aim rehearses the production CharacterGaze.Aim the overlay runs for each " +
            "gaze (a mouse far from and near the character, a window, a touch's look at the mouse, a glance). saved is the glances " +
            "choice (DecideGaze: usual gaze unless Martlet decides), then a rehearsal of the production decision " +
            "(Martlet.Avatar.Hosting CharacterGaze and GazeDirector) on " +
            "generated 1920x1080 pictures (NOT screenshots; nothing is captured): a notification popping up, the same spot again soon " +
            "and later, another change right after a glance, a notification behind the character, the character's own motion, its " +
            "speech bubble, a new scene, a change by the mouse and changes all over, each with the expected and actual verdict and " +
            "the spot looked at (ok: all scenarios and aims as expected). Also where each look tag points on one and two screens, " +
            "the gaze tags, the screen glance's look instructions (the data directory's edited prompts included) and what the " +
            "production segmenter makes of answers with look tags (spoken, shown, quiet, the look and gaze cues); answer replaces " +
            "the sample answers. Reads only; contacts nothing.", new
        {
            dataDirectory = new { type = "string" }, answer = new { type = "string", maxLength = 2000 }, personaId = new { type = "string" }
        }),
        Tool("character_theme", "A character model's colors and palettes as Settings > Appearance makes them (docs/UI_DESIGN.md " +
            "\"Character palettes\"): modelPath (a .model3.json or .vrm on this PC), else the model dataDirectory's avatar.json shows, " +
            "else the built-in character. Returns the main colors read from its textures (hex, share, kind, name), the colors the " +
            "rules build from (tint, accents, lightest and darkest), Martlet's rule-based light and dark palettes with their " +
            "lowest contrasts and any rule problems, and whether dataDirectory's character-themes.json has the model's colors. With " +
            "previewDirectory (an absolute folder), writes PNG pictures of Martlet's window in each palette (label names the files). " +
            "Never returns the model's path.", new
        {
            dataDirectory = new { type = "string" }, modelPath = new { type = "string" }, previewDirectory = new { type = "string" },
            label = new { type = "string", maxLength = 40 }
        }),
        Tool("character_models_selftest", "Rehearse the shared character models end to end with the production code: two real " +
            "gateways on 127.0.0.1 (pinned TLS, in-memory character-models.json and pieces) and three simulated desktops using the " +
            "desktop's paired client and Martlet.Avatar.Hosting's import and reconcile engine over a temporary folder, with generated " +
            "Live2D-folder and VRM-file fixtures (NOT real models; nothing rendered). Checks importing (copies read exactly like the " +
            "originals with the renderer's file reader), sharing the list and every 3 MiB piece, a new desktop copying both characters, " +
            "resuming an interrupted copy, passing characters on to a host the first desktop never reached, removal everywhere (host " +
            "pieces and desktop copies deleted), keeping the copy a computer shows, stale copies, a host restart, piece checks and the " +
            "list's limits. Loopback only; the temporary folder is deleted and the credential vault is not touched.", new { }),
        Tool("creations_status", "Read Martlet's creations (Creations, docs/CREATIONS.md: songs and other things Martlet made, shared " +
            "with every paired Martlet computer) from a data directory's creations.json, creations folder and creations-sync.json: " +
            "live creations and tombstones, total size, counts and sizes per kind, and for each creation its short id, kind, kind " +
            "version, size, length, when and on which device it was made, whether Martlet may clean it up, its assets (name, media " +
            "type, size, pieces), whether this PC holds all of it and on how many hosts it is complete; the asset files here (unused " +
            "ones and copies in progress); the last sync (when, its summary, each paired host's state and how many creations it " +
            "holds); and the limits. Never a title, text, voice or personality. Read-only; contacts nothing.", new
        {
            dataDirectory = new { type = "string" }
        }),
        Tool("creations_check", "Rehearse Martlet's creations end to end with the production code: list_creations and " +
            "perform_creation (Martlet.Conversation.CreationTools) on a disposable folder with the production store and the FIXTURE - " +
            "NOT AI test-tone kind (no tools without a registered kind; the same two tools and texts every time; listing, filters, " +
            "performing through the kind's handler, and clear refusals for no handler, unknown ids, bad arguments, a creation still " +
            "copying and an unknown kind), then the sync: two real gateways on 127.0.0.1 (pinned TLS, signed requests, in-memory " +
            "creations.json and pieces) and three simulated desktops with the production CreationStore and CreationSync over the " +
            "desktop's paired client. Checks FLAC sizes, sharing every 3 MiB piece, skipping unchanged hosts, a new desktop and a " +
            "relay host, resuming an interrupted copy, performing on another computer, an unknown kind passing through, rename, " +
            "delete everywhere (pieces and files deleted), stale copies, a host restart, piece checks, an unsigned request refused, " +
            "a kind's rules and the per-host sync record. Loopback only; folders are deleted and the credential vault is untouched. " +
            "With seedDataDirectory (a disposable folder under the temporary folder, never Martlet's own), it instead writes two " +
            "FIXTURE - NOT AI test tones there, so the Creations page can be checked with a desktop on that folder.", new
        {
            seedDataDirectory = new { type = "string" }
        }),
        Tool("settings_sync_status", "Read one Martlet on every computer (the settings shared through the paired hosts) from a data " +
            "directory's shared-settings.json: whether sync is on, each shared setting (thinking, listening, speaking, thinking-fallback, " +
            "companion, replies, prompts, memory, lorebooks, character, character-actions, talk, speech-display, appearance, " +
            "voice-recognition, voice-id, smart-home, updates) with which computer changed it and when, its revision, whether it uses an " +
            "API key (never the key or its digest), the provider and model of each job's route, the values of non-personal settings, " +
            "and whether this PC still has the same value (\"same\", \"different\" or \"unknown\" for its own files). Read-only; " +
            "contacts nothing and reads no credentials.", new
        {
            dataDirectory = new { type = "string" }
        }),
        Tool("memory_sync_status", "Read one memory on every computer (what Martlet remembers, the same on all the owner's computers " +
            "through the paired hosts) from a data directory's memory-sync.json: whether sync is on, when this PC last synced its memory " +
            "store, how many facts it had then and which computer wrote each version, and how many forgotten facts every computer agreed " +
            "on. Never a fact or its text. Read-only; contacts nothing.", new
        {
            dataDirectory = new { type = "string" }
        }),
        Tool("memory_status", "Read what Martlet remembers, and whose, from a data directory: whether memory is on and where it is kept " +
            "(settings.json), then the memory store's facts counted by where they came from (typed, conversation), how many expire, and " +
            "whose they are: everyone's, each voice they belong to by its tag from voices.json (V3, whether it is named or the owner's) " +
            "and those of forgotten voices. Never a fact's text, a name, a voice ID or a path. Read-only (it never opens or locks the " +
            "store); contacts nothing.", new
        {
            dataDirectory = new { type = "string" }
        }),
        Tool("memory_sync_selftest", "Rehearse one memory on every computer end to end with the production code: two real gateways on " +
            "127.0.0.1 (pinned TLS, signed requests, in-memory memories.json) and three simulated desktops, each with a real Martlet.Memory " +
            "store in a temporary folder, the desktop's paired client and the real memory sync engine (Martlet.Core.Sync.MemorySyncNode). " +
            "Walks saving on one computer and recalling on another, an edit, a deletion reaching every computer (and never coming back), " +
            "offline edits on two computers, a host that missed changes, a new computer taking everything, an expired fact, a full store " +
            "making room by forgetting the oldest conversation fact, a fact from a newer Martlet passing through, a new memory folder and " +
            "an unsigned request refused. Synthetic facts only; loopback only; the folder is deleted.", new { }),
        Tool("settings_sync_selftest", "Rehearse one Martlet on every computer end to end with the production code: two real gateways on " +
            "127.0.0.1 (pinned TLS, signed requests, in-memory shared-settings.json) and three simulated desktops with real settings.json, " +
            "lorebooks.json and shared-settings.json in a temporary folder, an in-memory stand-in for Windows Credential Manager, the " +
            "desktop's paired client, sync engine and settings sections. Walks the owner's case (a PC on OpenRouter becomes a host, the " +
            "other PC still on NVIDIA Build becomes the companion and takes OpenRouter, its model and key), model and key changes, offline " +
            "edits on both sides (different and the same setting; the later edit wins), a host that missed a change, a stale copy, a " +
            "newer Martlet's setting, a Windows voice a new computer lacks, a new computer, the Thinking fallback and its key, lorebooks, " +
            "no keys in desktop files and an unsigned request refused. Loopback only; the folder is deleted and the vault untouched.", new { }),
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
        Tool("listening_engine_check", "Listening on another computer: transcribe phrases a Windows voice says (System.Speech " +
            "rendered to memory, never played; optional phrases, up to 8 English sentences) with the stt host role's live " +
            "speech-to-text service on a numeric loopback endpoint (default http://127.0.0.1:8178/: whisper.cpp or Martlet's Parakeet " +
            "service, workers/parakeet) through the production path: the role's relay (Martlet.Gateway.Stt) inside a real gateway " +
            "on 127.0.0.1 (pinned TLS, pairing) and the desktop's paired client. Returns the service's /status (Parakeet's engine, " +
            "model, threads and runtime versions; whisper.cpp has none), the route's model and engine release (modelRevision), each " +
            "phrase's transcript, word errors and transcribeMs, the word error rate and median time; ok when every phrase came " +
            "back with at most 20% word errors. model (optional) names the route's model; by default the one /status names, " +
            "else small. Loopback only; runs Martlet.NodeLinkCheck; nothing is recorded, played or kept.", new
        {
            endpoint = new { type = "string", maxLength = 64 },
            model = new { type = "string", maxLength = 64 },
            phrases = new { type = "array", maxItems = 8, items = new { type = "string", maxLength = 200 } }
        }),
        Tool("voice_engine_check", "Speak one sentence with a self-hosted voice engine's loopback service (a host role's service, " +
            "default chatterbox on http://127.0.0.1:50083; chatterbox-original 50089, chatterbox-nano 50088, f5 50080, xtts 50081, " +
            "gpt-sovits 50082, dia 50084) through the production " +
            "path: the engine's own gateway relay inside a real gateway on 127.0.0.1 (pinned TLS, pairing) and the desktop's paired " +
            "client, with a starter voice as the reference (nothing played or recorded). Returns the service's /status before and " +
            "after (state, error, model, device, runtime versions such as torch and CUDA, Chatterbox's whispered parts, Chatterbox " +
            "Original's style), the audio length, time to " +
            "first audio, total time, real-time factor, peak and RMS level, how much of it is voiced (voicedShare: near 0 for a " +
            "whisper, so text starting with [whispering] shows Chatterbox whispering), or the failure code and message. For " +
            "chatterbox-original it sends the General and Expressive style saved in dataDirectory (chatterbox-style.json, as " +
            "Companion › Voice saves it), else Resemble's suggestions, and returns it as style. Loopback only; runs " +
            "Martlet.NodeLinkCheck.", new
        {
            engine = new { type = "string", @enum = VoiceEnginePorts.Keys.ToArray() },
            endpoint = new { type = "string", maxLength = 64 },
            text = new { type = "string", maxLength = 300 },
            dataDirectory = new { type = "string" }
        }),
        Tool("reading_check", "Companion › Reading (docs/READING.md): read reading.json for a data directory (where Martlet reads " +
            "the text on the screen: Windows OCR on this PC, a host's Reading role or off) and read a drawn test picture with known " +
            "text (HEALTH 87 / 100, Score: 12450, VICTORY, a chat line) through Windows OCR on this PC, as watching does: lines, " +
            "the joined text, missing words and milliseconds. With endpoint (a Reading role's worker on loopback, such as " +
            "http://127.0.0.1:50087/) that worker's GET /status and POST /read read the same picture as a PNG. Never captures the " +
            "real screen.", new
        {
            dataDirectory = new { type = "string" },
            endpoint = new { type = "string", maxLength = 64 }
        }),
        Tool("pictures_status", "Read Companion › Pictures for a data directory: where Martlet draws (pictures.json: off, " +
            "Martlet's Pictures host role, the owner's ComfyUI at an address, OpenRouter or NVIDIA Build; the workflow, checkpoint or " +
            "model; whether an own key is saved, never the key), the loaded custom workflow's node count, the picture creations " +
            "(shape, size, engine, model, seconds, fixture, assets, whether they're on this PC; never titles or descriptions) and the " +
            "draw_picture tool and job kind the conversation offers. Read-only.", new
        {
            dataDirectory = new { type = "string" }
        }),
        Tool("pictures_check", "Draw one picture through the production picture maker and report it: place \"fixture\" (the " +
            "default: the FIXTURE - NOT AI gradient maker) or \"comfyui\" (a ComfyUI at address, such as http://127.0.0.1:8188, " +
            "through Martlet's ComfyUI client: status and model check, the workflow (z-image-turbo, checkpoint with checkpoint, or " +
            "custom with workflowFile, an absolute path to an Export (API) file), queue, history, the picture fetched). Returns " +
            "availability, every progress stage, the media type, size, SHA-256, seconds and the workflow's node types. With " +
            "dataDirectory (disposable) it keeps the picture as a picture creation there and reads it back as the talk window " +
            "does; with saveDirectory (absolute) it writes the picture there. Never calls a paid cloud provider.", new
        {
            place = new { type = "string", @enum = new[] { "fixture", "comfyui" } },
            address = new { type = "string", maxLength = 512 },
            workflow = new { type = "string", @enum = new[] { "z-image-turbo", "checkpoint", "custom" } },
            checkpoint = new { type = "string", maxLength = 255 },
            workflowFile = new { type = "string", maxLength = 260 },
            prompt = new { type = "string", maxLength = 2000 },
            shape = new { type = "string", @enum = new[] { "square", "landscape", "portrait", "wide", "tall" } },
            dataDirectory = new { type = "string", maxLength = 260 },
            saveDirectory = new { type = "string", maxLength = 260 }
        }),
        Tool("singing_status", "Read the singing host role: its loopback service's own status (default http://127.0.0.1:50085/: " +
            "state, engine (song, or the FIXTURE - NOT AI tone engine), the pinned models with their licences and sizes, sources, " +
            "voice matches set up (soulx, vevosing), queue, whether the worker process holds the graphics card, the card's memory " +
            "and the idle release time) and, with a data directory, the Singing card's saved choices (singing.json: quality and " +
            "voice match) and, when that directory is paired with hosts (hosts.json), Singing on each paired host read through its " +
            "own gateway as the card reads it (reachable, offers the song route, the service's state, voice matches, models and " +
            "their size, worker and GPU; a role in Docker on this PC listens only inside the gateway's network, so this is how " +
            "to read it) plus what this PC's Docker shows of the role (container, images, models volume, and whether a " +
            "\"martlet-host add singing\" is running now: a setup in progress). Read-only; the pairing secret from Windows " +
            "Credential Manager only signs the requests and is never returned.", new
        {
            endpoint = new { type = "string", maxLength = 64 },
            dataDirectory = new { type = "string" }
        }),
        Tool("singing_check", "Make one song through the singing role's production path: its gateway relay inside a real gateway " +
            "on 127.0.0.1 (pinned TLS, pairing, the shared speaking-voice list with a starter voice) and the desktop's paired song " +
            "client. endpoint \"fixture\" (the default) starts this checkout's workers/singing service with the FIXTURE - NOT AI " +
            "engine; a numeric loopback endpoint (for example http://127.0.0.1:50085/) uses a live singing service and its real " +
            "models. With dataDirectory (a desktop data directory paired with a host) it goes through that real paired host " +
            "instead, exactly as the desktop does: the host offering Singing (or host), its own gateway and singing role, and " +
            "voiceId (a full ID or a unique 8+ character prefix from that gateway's shared voice list; nothing is added to the " +
            "list); when the host lacks that voice's recording it is sent once from voiceRecording or the data directory's voice " +
            "store, checked against the voice's SHA-256. Returns every stage seen and when, the host's stage timings, the three tracks (seconds, peak and RMS), the " +
            "beat grid and timed lyric lines, the service status before and after (models, GPU memory), or the failure code and " +
            "message, plus the host's word-timing source and sanity metric (median word start to vocal onset), the backing " +
            "bleed removed from the vocals and its graphics-memory peak. Nothing is played; with saveDirectory (an absolute, " +
            "disposable folder) it writes mix.wav, vocals.wav and backing.wav there for a report. voiceRecording (an absolute path " +
            "to a copy of a mono 16-bit PCM WAV, 1-30 s) with its voiceTranscript sings in that voice instead of the starter voice. " +
            "bpm (default 90; 0 for none) and key (default \"G major\"; empty for none) are sent as Martlet's own model writes " +
            "them; without either the host's music planner runs first. " +
            "Runs Martlet.NodeLinkCheck; a real song can take minutes (the tool allows 20).", new
        {
            endpoint = new { type = "string", maxLength = 64 },
            dataDirectory = new { type = "string", maxLength = 260 },
            host = new { type = "string", maxLength = 64 },
            voiceId = new { type = "string", minLength = 8, maxLength = 128 },
            seconds = new { type = "integer", minimum = 15, maximum = 180 },
            quality = new { type = "string", @enum = new[] { "fast", "high_quality" } },
            voiceMatch = new { type = "string", @enum = new[] { "soulx", "vevosing" } },
            saveDirectory = new { type = "string", maxLength = 260 },
            voiceRecording = new { type = "string", maxLength = 260 },
            voiceTranscript = new { type = "string", maxLength = 4096 },
            bpm = new { type = "integer", minimum = 0, maximum = 240 },
            key = new { type = "string", maxLength = 16 }
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
            "(sent as nothing), with its character count and estimated tokens (Martlet's own request-size estimate, about a token " +
            "per three UTF-8 bytes), plus the tokens of all prompts together. With id, also returns that one prompt's effective text " +
            "(the saved edit or the built-in text) exactly as Martlet uses it. shortFirstSentence: Companion > Replies > Short " +
            "first sentence (on by default) and what closes a spoken and an unspoken reply's instructions with it, exactly as the " +
            "desktop sends them. Read-only.", new
        {
            dataDirectory = new { type = "string" },
            id = new { type = "string", maxLength = 64 }
        }),
        Tool("character_status", "Read Companion > Personality and Character as saved in a data directory (they save on their own, " +
            "with no Save button): the personas (name, whether Martlet uses it, speech breaks, instruction " +
            "length; never the instructions), the character model (built-in character name or the own model's file type, never its " +
            "path; renderer, lip-sync mode, show at startup, the lip-sync host's ID), whether the character's position is locked on " +
            "this PC and where (placement), whether clicks pass through the character on this PC (clickThrough), whether Martlet's " +
            "voice is muted (voice: Speak Martlet's replies aloud, which the " +
            "character's Mute voice / Unmute voice menu item changes) and the lorebooks (counts only). Read-only.", new
        {
            dataDirectory = new { type = "string" }
        }),
        Tool("hearing_check", "Whether the Thinking model can hear the user's recording (the saved Thinking route in a data " +
            "directory, or modelId): the model's name-based hearing, the route's (the production decision: only Chat Completions routes, " +
            "Ollama on this PC included, then what model-abilities.json says, then the name), savedAbility (what Martlet found out about " +
            "the saved model and where), hearVoice (whether Thinking hears your recording: Companion > Listening > Let Thinking hear my " +
            "voice as chosen, or never chosen, on only while the recording stays on this PC), hearVoiceChoice (on, off or unset), " +
            "staysOnThisPc, hearVoiceWhy, voicePath (When Thinking can " +
            "hear you: straight, the default, or transcribeFirst), straightApplies (always listening sends the recording alone right " +
            "away) and lastTurn: which way the newest spoken reply went, from the desktop log's Voice path line, with the background " +
            "transcript's timing (transcriptReadyAfterReplyStartMs, speechToTextMs) and where its words went (never the words). Then " +
            "rehearses the production Chat Completions adapter against " +
            "a fixture endpoint on 127.0.0.1 (canned " +
            "reply, NOT AI) with a synthesized speech-like clip (never microphone audio, nothing played): the clip goes as an " +
            "input_audio WAV part beside the transcript, is refused without its own audio permission before any request, and is " +
            "left out of a transcript-only retry. Loopback only; reads no credentials.", new
        {
            dataDirectory = new { type = "string" },
            modelId = new { type = "string", maxLength = 128 }
        }),
        Tool("straight_voice_check", "Companion > Listening > When Thinking can hear you > Send my voice straight to Thinking, " +
            "rehearsed headless with the production conversation runtime and Chat Completions adapter, the desktop's own SpokenWords " +
            "(the words of a recording sent alone) and ConversationContextBuffer (what each next request carries). Three utterances " +
            "synthesized by a Windows voice (never a microphone, nothing played) go one turn at a time as the recording alone: each " +
            "request must carry an input_audio WAV and only the stand-in text, never the transcript. Speech-to-text runs beside the " +
            "reply (Parakeet on this PC when downloaded in speechDirectory with the sherpa runtime from martletDirectory, else a " +
            "fixture transcriber), its words replace the recording in the conversation and every next request carries them (no " +
            "recordings or stand-ins in history). Then the same utterances transcribed first (the transcript, then both), and a model " +
            "that refuses the recording: the reply waits for the words and asks again with them. quickCheck: the quick check of " +
            "something short (less than 1 s of voice) on fixtures (a hum, coughs, Mmm., Yes, please., Stop.): Parakeet's words, the " +
            "production word check and whether the desktop would drop the reply (never for real words). Returns per turn the first-words " +
            "time, when the transcript was ready after the reply started, speech-to-text time and the prompt cache (input and cached " +
            "tokens), and the medians of both ways. With live=true Thinking is Ollama on this PC (model, default gemma4:e2b, must " +
            "hear) through a loopback relay that records each request, and contention compares the model's first words for a short " +
            "straight request alone, with Parakeet started at the request's start and at the first words; otherwise a fixture " +
            "endpoint (canned replies, NOT AI). Nothing leaves this PC; reads no credentials.", new
        {
            live = new { type = "boolean" },
            model = new { type = "string", maxLength = 128 },
            martletDirectory = new { type = "string" },
            speechDirectory = new { type = "string" }
        }),
        Tool("discord_voice_check", "Discord voice (Companion > Discord) without Discord: loads libdave.dll (DAVE, Discord's " +
            "mandatory end-to-end voice encryption, which NetCord calls) from this server and from martletDirectory (the desktop " +
            "build or install, where it ships) and runs an offline DAVE session (protocol version, MLS key package, frame encryptor " +
            "and decryptor); checks the managed Opus codec. Then pushes one utterance a Windows voice says (rendered to memory, " +
            "never played) through the production voice path with a fake transport: 48 kHz stereo Opus packets as a Discord client " +
            "sends them, per-speaker ordering, decoding, 16 kHz downsampling and endpointing (DiscordVoiceConversation), FIXTURE " +
            "speech-to-text and a FIXTURE reply engine (NOT AI), the reply spoken by a Windows voice into memory and encoded back " +
            "to Opus frames, decoded again to measure them; then a second person talks over a long reply (barge-in must stop it). " +
            "Returns natives, utterance (heardMs, turn: addressed, source), reply (frames, spokenMs, level, Speaking on/off) and " +
            "bargeIn. No Discord connection, network, microphone, speaker, provider or credential.", new
        {
            martletDirectory = new { type = "string" }
        }),
        Tool("model_ability_check", "What Thinking models were found to hear (recorded audio) and see (pictures): model-abilities.json in " +
            "a data directory, also shared with the owner's other computers as the model-abilities setting. Then rehearses the production " +
            "detection (ModelContextProbe) against fixture servers on 127.0.0.1 shaped like OpenRouter's model list " +
            "(architecture.input_modalities), llama.cpp (/props modalities) and Ollama (/api/show capabilities), Companion > Listening > " +
            "Test hearing (ModelHearingTest) against a fixture Chat Completions endpoint that answers the test word only when the request " +
            "carries the recording (it is told the word: NOT AI), a model that ignores audio, one that refuses it and a wrong key; the " +
            "hearing and vision decisions replies use (decisions) and the shared value's round trip (shared). With baseUrl (an http:// " +
            "server on this PC only, for example Ollama's http://127.0.0.1:11434/v1 or a llama.cpp server) and modelId it also asks that " +
            "real server what the model takes (real.metadata), and with test=true sends it the real Test hearing request: one word said " +
            "by Windows speech (never microphone audio, nothing played). Nothing leaves this PC; reads no credentials; saves nothing.", new
        {
            dataDirectory = new { type = "string" },
            baseUrl = new { type = "string", maxLength = 256 },
            modelId = new { type = "string", maxLength = 128 },
            test = new { type = "boolean" }
        }),
        Tool("spoken_reply_check", "Rehearse a spoken reply whose voice fails partway, end to end with the production conversation " +
            "runtime (Chat Completions adapter, Martlet host voice stream, playback sink): a fixture endpoint on 127.0.0.1 streams a " +
            "canned four-sentence reply (NOT AI) a sentence at a time, like OpenRouter; a fixture host voice (a quiet tone, NOT AI) " +
            "fails on the failAt-th piece (1-4, default 1) it is asked to say, as voiceFailure: server (the host worker failed), " +
            "unavailable (it is reloading), stall (no audio until the voice's time runs out), slow (every piece slower than real " +
            "time: half its audio, a 1.5 s pause, then the rest, as Chatterbox streams on a busy graphics card; every piece must " +
            "still be spoken whole and the latency line must say the pauses, voice.pauses and voice.pausedMs) or none; muted " +
            "instead has the user mute Martlet's voice (the character's Mute voice) as the failAt-th piece is asked, which ends " +
            "only what is said aloud and is not a failure (voice.muted); paused pauses the reply once its first audio played, as " +
            "Pause and decide does when you talk over it, holds it 1 s and plays it on: ok also needs no samples played while paused, " +
            "the next piece still made meanwhile (voice.hold), every piece said once (nothing made again) and the " +
            "latency line saying it paused and resumed (with characterTags, also no cue acted while paused: a cue that falls in " +
            "the pause waits for it); stopped has the user stop the reply (Stop, or talking over it) as the failAt-th piece starts " +
            "playing, and ok then needs the reply canceled and no cue acted after the stop (character.stoppedAtMs; the cues still " +
            "waiting are dropped); text-only sends the reply with no voice at all (Speak " +
            "Martlet's replies aloud off), so every sentence goes to the captions; a fixture speaker opens no " +
            "device and plays nothing. Returns the reply's state and whether its whole text arrived, how far the voice got and why " +
            "it stopped, and the captions (speech bubble and subtitles): each line with when it was shown and whether it was " +
            "spoken; after the voice fails every unsaid sentence is still shown, one per reading time. ok means the text completed, " +
            "only the voice stopped and the captions showed the whole reply. With reasoningMs the fixture first streams hidden " +
            "reasoning and waits that long before the words; with voiceDelayMs the fixture voice takes that long to make each " +
            "piece. latency returns the reply's step timings and the desktop log's reply latency line for it, parsed back as " +
            "latency_report reads it (with voiceFailure none, ok also needs every step and steps adding up to the total). With " +
            "reply, that text is streamed instead, a word at a time like a model's tokens; the reply is broken into pieces with " +
            "speech breaks (dataDirectory's persona by name, else the one Martlet uses, else the defaults; breaks overrides stops) " +
            "and voice.pieces lists exactly what the voice was asked to say. thinkingSteps (off or on) sends Companion > Replies > " +
            "Thinking steps with the reply; with refuseThinking the fixture endpoint refuses a request that carries it, as a model " +
            "that always thinks does, and ok needs the reply asked once more without it (thinking.sentControl [true, false], " +
            "reasoningRejected). With chattiness, the reply is offered the chattiness tags as it is while Companion > Vision > How " +
            "often it comments is Martlet decides (such as \"[chattiness:quiet]\"): chattiness returns the tags the reply wrote and " +
            "the level they switch to, and ok also needs them out of reply.text and voice.pieces. With characterTags (such as " +
            "[\"{nod}\", \"{blush}\"]) the reply is offered the desktop character's tags as while the character shows: character " +
            "returns what the reply's tags did (acted: each tag, its kind, name and the other spelling the reply used, such as " +
            "[nod] or *nods* for {nod}), the note the talk window shows under the reply (\"Tone: happy. Emotes: nod.\") and each cue " +
            "the character got (tag, atMs when its sentence started playing, delayMs into that sentence, and actedMs when the " +
            "character acted it, waiting for it as the desktop's character does, or dropped when the reply stopped first), and ok " +
            "also needs every " +
            "spelling of the tags out of reply.text and voice.pieces and, with voiceFailure none, slow or text-only, a cue for " +
            "every tag acted. Loopback only; reads no credentials.", new
        {
            voiceFailure = new { type = "string", @enum = SpokenReplyCheck.Failures },
            failAt = new { type = "integer", minimum = 1, maximum = 4 },
            reasoningMs = new { type = "integer", minimum = 0, maximum = 5000 },
            voiceDelayMs = new { type = "integer", minimum = 0, maximum = 5000 },
            reply = new { type = "string", maxLength = 1024 }, dataDirectory = new { type = "string" }, persona = new { type = "string" },
            breaks = BreaksSchema(),
            thinkingSteps = new { type = "string", @enum = new[] { "off", "on" } },
            refuseThinking = new { type = "boolean" },
            chattiness = new { type = "boolean" },
            characterTags = new { type = "array", items = new { type = "string" } }
        }),
        Tool("smart_home_status", "Read Companion > Smart home's saved connection from a data directory: the Home Assistant address, " +
            "name and version, whether a token is saved (never the token), the control, locks and flexible-request settings, and whether " +
            "the connection is shared through the paired hosts (shared revision, which host it came from). Read-only.", new
        {
            dataDirectory = new { type = "string" }
        }),
        Tool("discord_status", "Read Companion > Discord's saved setup from a data directory's discord.json: whether a bot token is saved " +
            "and readable in Windows Credential Manager (never the token), the application ID, whether the bot connects when Martlet runs, " +
            "the chat modes, channel-rule and people counts, whether the owner's account and home server are set, and the next setup step. Read-only.", new
        {
            dataDirectory = new { type = "string" }
        }),
        Tool("discord_check", "Connect the data directory's saved Discord bot once with Martlet's production bot host and report whether " +
            "Discord accepts it: Online with the bot's name and server count, a rejected token, or Message Content Intent left off in the " +
            "Developer Portal; then disconnect. Sends no messages. Needs a saved token (Companion > Discord).", new
        {
            dataDirectory = new { type = "string" },
            seconds = new { type = "integer", minimum = 3, maximum = 30 }
        }),
        Tool("messaging_status", "Read Companion > Messaging from a data directory's messaging.json (this PC only, never synced): " +
            "whether Martlet answers its Telegram bot and its WhatsApp number on this PC, the bot's name and username, the WhatsApp number, name, IDs, port and " +
            "public address, whether secrets are saved, how many chats are paired and whether replies are also said aloud. Never the bot token, WhatsApp " +
            "access token or app secret (Windows Credential Manager) or the chats' names and IDs. Read-only.", new
        {
            dataDirectory = new { type = "string" }
        }),
        Tool("terminal_status", "Read Companion > Tools > Terminal from a data directory's terminal.json (this PC only, never " +
            "synced): whether replies may run terminal commands (off by default), the shell and whether it is installed, which shells " +
            "this PC has, whether every command asks first (on by default), the time limit, whether commands start in the home folder " +
            "or a chosen one (never its path) and whether it exists, and run_terminal_command exactly as the Thinking model gets it " +
            "(the start folder shown as {folder}). Read-only; runs nothing.", new
        {
            dataDirectory = new { type = "string" }
        }),
        Tool("terminal_check", "Rehearse Companion > Tools > Terminal with the desktop's production terminal runner and fixed, " +
            "harmless commands (never anything a model or the owner chose), in the saved shell or shell (WindowsPowerShell, " +
            "PowerShell or CommandPrompt), in a fresh temporary folder removed afterwards: UTF-8 output and the start folder, quotes " +
            "and & | in a command, an error line with exit code 3, closed input (a command that reads input ends at once), a 2-second time limit stopping " +
            "the shell and its child process (a loopback ping), 20,000 lines of output kept as its start and end, the commands " +
            "refused before anything runs, and a program the command starts in the background (a 3-second loopback ping) not " +
            "holding up the run. Returns ok, each step and what the model would be told. Runs whether or not the " +
            "terminal is on; local only, reads no credentials.", new
        {
            dataDirectory = new { type = "string" },
            shell = new { type = "string", @enum = Enum.GetNames<TerminalShell>() }
        }),
        Tool("echo_check", "Companion > Listening > Reduce echo from my speakers: the saved choice (on by default), the saved " +
            "Let me interrupt Martlet by talking choice (bargeIn, opt-in and off by default) and whether the " +
            "WebRTC echo canceller loads, then a rehearsal of the production microphone path (MicrophoneCapture, EchoReducer, the " +
            "canceller) with fixture devices on a simulated clock: no microphone or speaker is opened and nothing plays. A synthesized " +
            "Martlet voice plays on the fixture speakers and reaches the fixture microphone through a simulated room (delayMs, " +
            "default 60), with the user's synthesized voice alone and over it. Returns how much quieter Martlet's echo got, how much " +
            "of the user's voice was kept and what Martlet's voice-activity detector heard, with and without echo reduction, and " +
            "talkOver: what the voice gate (TalkOverDetector with the capture's echo timeline, which tells the user's voice over the " +
            "speakers' sound) heard in each part: Martlet's own echo must never count as the user, and the user's voice over it must, " +
            "only after the required second of voice. wordCheck is the saved Word check (what stops Martlet is real words; see " +
            "utterance_filter_check). listensWhileSpeaking: whether always listening goes on while Martlet speaks with these choices " +
            "(barge-in, or echo reduction that works as it did here; rehearsal.reducing is the capture's own echo state), so what is " +
            "said then is heard and answered after the reply.", new
        {
            dataDirectory = new { type = "string" },
            delayMs = new { type = "integer", minimum = 0, maximum = 300 }
        }),
        Tool("utterance_filter_check", "Companion > Listening > Word check: runs the production utterance filter (UtteranceFilter: " +
            "fillers like mm, hmm, uh-huh, laughter, sound tags, punctuation, a lone word, more words than the voice could hold, and " +
            "phrases speech-to-text makes up from noise such as 'Thank you.' when the evidence is weak) and barge-in policy " +
            "(BargeInPolicy: only real words stop a reply; a stop word or Martlet's name at once, a backchannel never; a song only " +
            "when asked to stop) on samples (text with optional voicedMs, speechMs, meanProbability, minimumProbability, noSpeechProbability, " +
            "averageLogProbability, afterQuestion, persona, playback reply|song, expectKeep, expectInterrupt; default: a fixed set " +
            "with the outcome Normal must give, including evidence measured from whisper.cpp and Parakeet on this PC). " +
            "With audio (default true) and Parakeet downloaded on this PC (speechDirectory, default the current user's; the sherpa " +
            "runtime from martletDirectory; parakeetModel, default Listening's model when downloaded, else v3, else the one " +
            "downloaded), it also runs fixtures synthesized with a Windows voice (stop, wait, a question, a quiet " +
            "phrase over a fan's hum, yes, yeah, mmm, hmm, laughter) and generated ones (a hum, coughs, noise) through the production " +
            "voice-activity detector, Parakeet and the barge-in gate, with the time from the start of the voice to the decision to stop. Returns each " +
            "decision with its reason, the filter's cost per call, the saved Word check (sensitivity overrides it: relaxed, normal, " +
            "sensitive) and ok. Nothing is recorded or played; nothing leaves this PC.", new
        {
            dataDirectory = new { type = "string" },
            martletDirectory = new { type = "string" },
            speechDirectory = new { type = "string" },
            parakeetModel = new { type = "string", @enum = Martlet.Sherpa.ParakeetModels.All.Select(m => m.Id).ToArray() },
            sensitivity = new { type = "string", @enum = new[] { "relaxed", "normal", "sensitive" } },
            audio = new { type = "boolean" },
            samples = new
            {
                type = "array", maxItems = 64,
                items = new
                {
                    type = "object",
                    properties = new
                    {
                        name = new { type = "string", maxLength = 64 },
                        text = new { type = "string", maxLength = 1000 },
                        voicedMs = new { type = "number", minimum = 0 },
                        speechMs = new { type = "number", minimum = 0 },
                        meanProbability = new { type = "number", minimum = 0, maximum = 1 },
                        minimumProbability = new { type = "number", minimum = 0, maximum = 1 },
                        noSpeechProbability = new { type = "number", minimum = 0, maximum = 1 },
                        averageLogProbability = new { type = "number" },
                        engine = new { type = "string", maxLength = 32 },
                        afterQuestion = new { type = "boolean" },
                        persona = new { type = "string", maxLength = 64 },
                        playback = new { type = "string", @enum = new[] { "reply", "song" } },
                        expectKeep = new { type = "boolean" },
                        expectInterrupt = new { type = "boolean" }
                    },
                    required = new[] { "text" },
                    additionalProperties = false
                }
            }
        }),
        Tool("barge_in_check", "Companion > Listening > When you talk over Martlet: simulates words said over a reply and returns the " +
            "production verdict (BargeInJudging with the local rules judge). A clear cue (a stop word, Martlet's name) stops at once and " +
            "is never judged; other words that the quick check would stop on pause the reply and the judge says interrupt (stop and " +
            "answer) or notForMe (a backchannel, agreeing, laughing along, Martlet's own words heard back: play on from where it " +
            "paused). samples: heard with optional sentence (what Martlet is saying), recentReply, voicedMs and expectVerdict " +
            "(interrupt or notForMe); default: a fixed set with the verdict each must give. Each sample returns verdict, reason, " +
            "source (Cue, Judge), judge, judgeMs and the action with the saved choice. deadlines: a fixture model judge (NOT AI) " +
            "slower than the deadline (judgeDelayMs, default 1000; deadlineMs, default 400) must give way to the rules within the " +
            "deadline, and one in time must be used. holds: what a pause does on a simulated clock (quiet after notForMe plays on; " +
            "talking on past 1.5 s stops; interrupt stops; no verdict plays on at the pause's limit). modelJudge: the Thinking pool's model " +
            "judge with fixture answers (NOT AI): a verdict is used, no pool member lets the rules decide at once, an answer without " +
            "a verdict lets them decide. Also returns the saved choice " +
            "(behavior PauseAndDecide or StopAtOnce from talk-preferences.json, bargeIn, wordCheck) and the timings. ok when every " +
            "expectation held. Nothing is recorded or played; nothing leaves this PC.", new
        {
            dataDirectory = new { type = "string" },
            deadlineMs = new { type = "integer", minimum = 1, maximum = 5000 },
            judgeDelayMs = new { type = "integer", minimum = 0, maximum = 10000 },
            samples = new
            {
                type = "array", maxItems = 64,
                items = new
                {
                    type = "object",
                    properties = new
                    {
                        name = new { type = "string", maxLength = 64 },
                        heard = new { type = "string", maxLength = 1024 },
                        sentence = new { type = "string", maxLength = 1024 },
                        recentReply = new { type = "string", maxLength = 1024 },
                        voicedMs = new { type = "integer", minimum = 0, maximum = 30000 },
                        expectVerdict = new { type = "string", @enum = new[] { "interrupt", "notForMe" } }
                    },
                    required = new[] { "heard" },
                    additionalProperties = false
                }
            }
        }),
        Tool("pc_audio_check", "Companion > Listening > Hear what this PC plays: the saved choice (off by default) with HandsFree and " +
            "ReduceEcho, which outputs are in use (sessions only) and what Martlet would hear (every app but Martlet, or only the output " +
            "you hear while another output such as a virtual cable is in use), whether this Windows can hear the PC without Martlet's own sound (a process loopback is set up and closed " +
            "without starting: nothing is recorded), then a rehearsal of the production path (PcAudioCaptureFactory, " +
            "MicrophoneCapture, the capture normalizer, the voice-activity detector) with a fixture loopback on a simulated clock: a " +
            "synthesized video voice 0-3 s, a pause with no packets 3-6 s, the voice again 6-9 s. Returns whether the stream stayed " +
            "continuous and the pause ended the first utterance, and yourVoice: the production matcher that leaves out your own voice " +
            "when this PC plays it back, on fixed samples. Reads no credentials and contacts nothing.", new
        {
            dataDirectory = new { type = "string" }
        }),
        Tool("sound_digest_check", "Companion > Listening > Describe PC sounds (on by default while Hear what this PC plays is on): the " +
            "saved choices, the desktop's sound-digest.json (on, the active judge: a Thinking pool model that hears or the CPU sound " +
            "tagger; runs, lines, drops and skips; the last line's age and how long judging took; never the line), then a FIXTURE " +
            "rehearsal of the production path: synthesized music with hand claps from a fixture loopback on a simulated clock through " +
            "PcAudioCaptureFactory and the capture normalizer into the in-memory PcSoundBuffer, then one SoundDigestScheduler tick " +
            "with the CPU sound tagger bundled in martletDirectory (sherpa-onnx Zipformer AudioSet tagger). Returns its labels, the " +
            "line and timings. wavFile (an absolute 16 kHz mono 16-bit WAV) is tagged too. Records, plays, sends and saves nothing.", new
        {
            dataDirectory = new { type = "string" },
            martletDirectory = new { type = "string" },
            wavFile = new { type = "string" }
        }),
        Tool("discord_call_check", "Martlet in your own Discord calls (Companion > Discord, companion mode on the owner's own " +
            "account; Martlet never automates Discord): the saved mode (discord-calls.json), a doctor check of this PC without recording " +
            "or playing (a process loopback of one app set up and closed unstarted, Discord's process and window, Windows' OCR language, " +
            "the playback devices, the chosen output or a virtual cable), then a simulated call utterance through the production path: a " +
            "fixture call voice through PcAudioCaptureFactory, MicrophoneCapture and voice activity on a simulated clock, fixture " +
            "pictures of the Discord window (member list, call grid, the owner's own tile) through the speaking detector and Windows' " +
            "OCR on this PC, the call lines and the In your Discord call prompt, and a fixture reply (NOT AI) that answers when " +
            "Martlet's name is said and otherwise passes. Contacts nothing.", new
        {
            dataDirectory = new { type = "string" }
        }),
        Tool("discord_text_check", "Discord text chat (src\\Martlet.Discord DiscordTextChat, the production pipeline the desktop's " +
            "bot uses) fed simulated messages through a fake transport and a fixture reply engine (NOT AI, NOT Discord): chat modes " +
            "(Off, Mentions, Sometimes, Always), addressing (DM, @mention, reply to Martlet, its name), other bots ignored, per-place " +
            "recent lines, stale turns dropped, replies split under 2,000 characters with @everyone/@here neutralized, quoted replies " +
            "in channels and /martlet delivery. Without messages it runs fixed scenarios on fixture preferences (scenarios, each " +
            "passed with a detail); with messages (1-16 of {text, place: server|dm, author: owner|known|stranger|bot, mention, " +
            "replyToMartlet, channelId, command}) it uses the data directory's discord.json. Returns outcomes, what was sent and the " +
            "text-chat stats (the desktop's DiscordTextStatus line). Reads no credentials and contacts nothing.", new
        {
            dataDirectory = new { type = "string" },
            reply = new { type = "string", maxLength = 4096 },
            messages = new { type = "array", maxItems = 16, items = new { type = "object" } }
        }),
        Tool("chattiness_status", "Companion > Vision > How often it comments (the same choice as Listening > Watch along) as saved " +
            "in a data directory's talk-preferences.json: the choice (Quiet, Normal, Chatty or Martlet decides; Normal by default), " +
            "whether vision (on by default) and hearing the PC are on (replies are told about Martlet decides only while one is), " +
            "what vision looks at (the whole screen by default), the level Martlet " +
            "decides starts at, the tags a reply switches the level with, what Martlet decides tells the Thinking model and the " +
            "note that says the level (Companion > Prompts, from settings.json's edits), then a rehearsal: sample replies (or reply) " +
            "through the production speech segmenter and chat stripper with those tags offered, returning what is spoken and shown, " +
            "whether it stays silent, the tags found and the level they switch to. The level a running conversation picked shows in " +
            "the talk window's LiveChattiness line and the desktop log. Reads no credentials and contacts nothing.", new
        {
            dataDirectory = new { type = "string" },
            reply = new { type = "string", maxLength = 1024 }
        }),
        Tool("discord_companion_check", "Martlet's Discord companion as saved in a data directory (discord.json and " +
            "discord-companion.json): friends (names, how many take calls), friend requests waiting for the owner, recent declines, the " +
            "private call channels it made and when the bot's picture last changed (never the token). Then rehearses the production " +
            "DiscordCompanion against an in-memory Discord (no token; contacts nothing): /friend ask (and asking twice), the owner's " +
            "approval and welcome DM, a call (the private channel 'character & person' and its permission overwrites for @everyone, " +
            "the friend, the bot and the owner, the ring DM with a jump link and a server invite), calling again (channel reused, " +
            "joined), cleanup, calls turned off, /friend remove, the presence each state shows and its rate limit, and the avatar " +
            "rate limit. requestFrom (a Discord user ID as a string, with requestName) files a friend request into the given " +
            "dataDirectory as /friend ask would (requires an explicit, disposable dataDirectory), so Companion > Discord's Friends " +
            "and calls card lists it for DiscordFriendApprove-<id>.", new
        {
            dataDirectory = new { type = "string" },
            person = new { type = "string", maxLength = 64 },
            character = new { type = "string", maxLength = 64 },
            requestFrom = new { type = "string", pattern = "^[0-9]{1,20}$" },
            requestName = new { type = "string", maxLength = 64 }
        }),
        Tool("vision_history_check", "How what Martlet sees is kept in the conversation (docs/SCREEN_COMMENTARY.md), rehearsed with " +
            "the desktop's production code: Companion > Prompts > What you saw as a data directory's settings.json sends it (seen), " +
            "then sample look replies (or reply) through the production speech segmenter and chat stripper with the [seen: ...] " +
            "and chattiness tags a look is offered (spoken, shown, passed, tags, seen: the description kept, tagHidden), each kept " +
            "in a production conversation buffer as the desktop keeps a look ([Screen] line; passed looks in a row keep only the " +
            "last: replacedPassedLook), then a message that came with a picture. Returns the conversation's lines as the next " +
            "reply sends them (history: role, text, vision, memoryReads: what memory and learning names may read of a user " +
            "line). Live looks show in the desktop log (\"Vision: the conversation keeps ...\"). Reads no credentials and " +
            "contacts nothing.", new
        {
            dataDirectory = new { type = "string" },
            reply = new { type = "string", maxLength = 1024 }
        }),
        Tool("screen_digest_check", "The screen summary over time (docs/SCREEN_COMMENTARY.md), run once with the desktop's " +
            "production ScreenDigester on FIXTURE frames (made-up pictures of a code editor, then a game with low health; no " +
            "screen capture) and a FIXTURE thinker and context board (no model, nothing sent). Returns the setting (Companion > " +
            "Vision > Screen summary over time, from talk-preferences.json, on by default), which frames the ring kept or skipped, " +
            "the job (reason, frames, contact sheet size and bytes, the message as Companion > Prompts > Screen summary over time " +
            "in settings.json makes it), the answer (reply, or a FIXTURE sentence) as parsed, what went to the board (text, " +
            "max age), the status (frames, last text, age, time taken, jobs, posted, dropped) and the talk window's line, then " +
            "that a stale answer is dropped. Reads no credentials and contacts nothing.", new
        {
            dataDirectory = new { type = "string" },
            reply = new { type = "string", maxLength = 1024 }
        }),
        Tool("context_board", "The context board (where background sources such as a screen digest, the sounds this PC plays, " +
            "touches on the character and the character's lingering emotes keep their newest short note for the live " +
            "conversation), rehearsed with the production board, request layout and Chat Completions adapter against a fixture " +
            "endpoint on 127.0.0.1 (canned reply, NOT AI). Posts FIXTURE notes from four sources (one stale, one consumed on read) " +
            "and optionally your own test note (source: 1-32 lower-case letters, digits or '-'; text; maxAgeSeconds 1-3600, default " +
            "60; ageSeconds 0-7200: how long ago it was posted; consume: goes with one request only), sends two requests in a row " +
            "and returns the notes each one carried, whether the stale note was skipped and the consumed one went once, where the " +
            "notes sat in the message, and whether the conversation kept the message without them (so the next request starts " +
            "the same). Live replies log \"Context board: the request took N notes (sources; bytes)\" in the desktop log and the " +
            "talk window's LiveTurnInputs counts them. Loopback only; reads no credentials.", new
        {
            dataDirectory = new { type = "string" },
            source = new { type = "string", maxLength = Martlet.Conversation.ContextBoard.MaximumSourceLength },
            text = new { type = "string", maxLength = 2048 },
            maxAgeSeconds = new { type = "integer", minimum = 1, maximum = 3600 },
            ageSeconds = new { type = "integer", minimum = 0, maximum = 7200 },
            consume = new { type = "boolean" }
        }),
        Tool("context_check", "The Thinking model's context as Martlet uses it, from a data directory: the saved route, Companion > " +
            "Replies > Context size, what model-limits.json says about the model (from Check model limit, choosing or testing a " +
            "model, or Ollama loading it) and the context size, reply room and text room replies get (the production ContextBudget). " +
            "Then rehearses the production model-limit check (ModelContextProbe) against fixture servers on 127.0.0.1 shaped like " +
            "OpenRouter, vLLM, Groq, llama.cpp and Ollama (NOT the real services; a fixture key goes only to its own base URL and " +
            "redirects aren't followed) and the production history fit of a 1,000-exchange synthetic conversation into a cloud " +
            "model's default, a 1,000,000-token setting, a paired host and Ollama on this PC. Loopback only; reads no credentials.", new
        {
            dataDirectory = new { type = "string" }
        }),
        Tool("thinking_steps_check", "Companion > Replies > Thinking steps (whether a reasoning model thinks before it answers) as " +
            "replies use it, from a data directory: the choice replies use (thinkingSteps Off or On; Off unless On is chosen) and " +
            "whether one was chosen, the Thinking route, how it takes the choice " +
            "(control, use) and exactly what its replies send (sends), and what every kind of route sends for Off and On. Then the " +
            "production Chat Completions adapter against a fixture endpoint on 127.0.0.1 (canned reply, NOT AI) for Off, On and the " +
            "model's own default (sent only after a model refused the choice). With " +
            "live: true it also asks Ollama on this PC (the saved local Thinking model, or model) a fixed question, never anything the " +
            "owner said, with the model's default and with Off: the production adapter's reply and first-words time, and one plain " +
            "request each showing how much Ollama thought first. Loopback only; reads no credentials.", new
        {
            dataDirectory = new { type = "string" },
            model = new { type = "string", maxLength = 128 },
            live = new { type = "boolean" }
        }),
        Tool("discord_reply_status", "Martlet's Discord reply engine, from a data directory: the desktop's discord-replies.json " +
            "(whether the engine is wired, replies, passes, skipped turns with the last reason, failures with the last code, places " +
            "with history, requests running, the last reply and pass times, its latency, first-words time and prompt-cache use, " +
            "the Thinking route it last used (route type and model), how it waits for the local conversation on a shared model, " +
            "turns waiting for it and requests a local reply stopped; never what was said or who said it) and discord.json's chat " +
            "setup (configured, enabled, owner set, chat modes, rule and people counts; never the token, IDs or names). Read-only.", new
        {
            dataDirectory = new { type = "string" }
        }),
        Tool("discord_reply_check", "Rehearse the Discord reply engine's production Discord side (DiscordReplier: per-place history, " +
            "the ambient gate's cooldown and chance, multi-party prompt shaping, [pass] and Discord's 2000-character and voice " +
            "limits) through the production Chat Completions adapter against a fixture endpoint on 127.0.0.1 (canned replies, NOT " +
            "AI): seven made-up turns in a server channel, a voice call and the owner's DM, each with its expected outcome (reply, " +
            "pass or skip and why), what the request carried (roles, lengths, the start of each message) and ok. With live: true it " +
            "also asks Ollama on this PC (the saved local Thinking model, or model) two made-up turns with the saved persona, never " +
            "anything anyone said. Loopback only; reads no credentials and spends nothing.", new
        {
            dataDirectory = new { type = "string" },
            model = new { type = "string", maxLength = 128 },
            live = new { type = "boolean" }
        }),
        Tool("reminders_status", "Martlet's reminders (the reply model's reminders tool: set, list, cancel; docs/CONVERSATION.md#reminders), " +
            "from a data directory's shared-settings.json: every computer's reminders entry, each reminder's text, due and set times, " +
            "the computer it was set on, its state (Pending, Done, Canceled, Missed) and who settled it, and each computer's marks " +
            "(Bid with its idle seconds, Claim, Done, Cancel, Missed), plus the tool exactly as the model gets it. Read-only.", new
        {
            dataDirectory = new { type = "string" }
        }),
        Tool("reminders_check", "Rehearse reminders end to end with the production code on two simulated companion PCs whose entries " +
            "merge through the shared settings: setting one with the tool (in minutes and at a local time), the other PC listing and " +
            "canceling one, who says a due reminder (both offer, the PC used most recently takes it, the other stays quiet), the " +
            "conversation's wording through BackgroundJobs (on its own as soon as Martlet is free, or in the notes of the next " +
            "message), a PC alone taking it at once and one far too late let go. No model, network or credentials.", new { }),
        Tool("setup_run_status", "Applying the recommended setup to all your computers and the Configuring state (docs/CLUSTER.md), " +
            "from a data directory: every computer's published run (shared-settings.json, setup-run.<device>: who started it and when, " +
            "whether it is active, its summary, each computer's state Pending/Configuring/Done/Failed/NeedsAttention with its step " +
            "and step count, when it finished), who does each job in cluster.json (with failover) and Sharing work (work-sharing.json). " +
            "Machine IDs, role names and counts only. Read-only.", new
        {
            dataDirectory = new { type = "string" }
        }),
        Tool("setup_run_check", "Rehearse applying a recommended setup with the production executor (SetupExecutor) on a fixture " +
            "recommendation against simulated computers (FIXTURE, NOT real hosts): the preflight (a role's terms and variant terms, " +
            "a graphics card by UUID, an NGC key the owner enters, a change that needs someone there, a computer without a host " +
            "service, the Thinking pool joining by itself, downloads), then the run: the host commands sent in order with their " +
            "arguments and the secret only to its role, terms recorded as accepted, the plan assignments with failover, Sharing work, " +
            "one cluster check, a failed step that doesn't stop the others, a change missing from the review skipped, and the run " +
            "record's states as every computer reads it from the shared settings. In-process; no network, model or credential.", new { }),
        Tool("helper_jobs_status", "Where Martlet's helper jobs ran last, from a data directory's helper-jobs.json (written by the " +
            "desktop): for each kind (memory: remembering and learning names after a reply; action_naming: naming a character's " +
            "emotes; touch_zones: finding its touch zones in one picture) its priority, whether it ran on a Thinking pool member " +
            "(route pool, with the member) or on the conversation's own Thinking model after the reply finished speaking (route " +
            "fallback, with how long it waited for the reply), how it ended and when. Never a prompt or answer. Read-only.", new
        {
            dataDirectory = new { type = "string" }
        }),
        Tool("helper_jobs_check", "Rehearse the desktop's production helper-job router (HelperJobs) with a fixture Thinking pool and " +
            "fixture answers (NOT AI): memory and emote naming go to a free text member, touch zones wait for a running reply and " +
            "fall back while no member sees pictures, then go to a vision member, and memory falls back when no member is free. " +
            "Returns each step's route, member, outcome and wait, and the helper-jobs.json it wrote. No model, network or credentials.",
            new { }),
        Tool("think_longer_status", "Companion > Deep thinking > Thinking longer (think_longer: Martlet decides, sparingly, to think a " +
            "task through in the background while the conversation carries on), from a data directory: the settings replies use " +
            "(on by default, Off from Where it thinks; effort, time limit, hourly limit, when it shares the result) and whether any " +
            "was chosen, the Thinking route (whether it does function calling, whether the model turned tools down, whether " +
            "think_longer is offered), Companion > Deep thinking (this PC's deep-thinking.json: same as Thinking, an endpoint or a " +
            "paired computer, never a key; whether a think can run there alongside the conversation and why: Deep thinking needs a " +
            "model of its own, never Thinking's own model on this PC or a paired computer; whether a second model in Ollama on this " +
            "PC is checked to fit beside Thinking's first; and what it sends for Thinking steps at that effort), think_longer and " +
            "cancel_thinking exactly as the Thinking model gets them with the Thinking longer prompt, and the desktop's " +
            "background-jobs.json: each running or finished job's id, kind, state, progress, times, result length, problem and " +
            "delivery (never its task or result), how many thinks started in the last hour, and where the running think works. " +
            "Read-only.", new
        {
            dataDirectory = new { type = "string" }
        }),
        Tool("think_longer_check", "Rehearse think_longer end to end with the production background-job scheduler (BackgroundJobs), " +
            "think runner (BackgroundThink), tool texts and request layout (ThinkLonger), conversation runtime and Chat Completions " +
            "adapter against a fixture endpoint on 127.0.0.1 (canned replies, NOT AI): a reply that says it'll think it over and " +
            "calls think_longer (the call returns at once and the reply completes), the background request (Thinking steps on, its " +
            "own output budget, the reply's instructions, tools and messages unchanged before the task), delivery as a message at " +
            "the end of the conversation (as soon as Martlet is free, or in the notes of the next message), the limits (one think " +
            "at a time beside a song job, the hourly limit, Cancel, Martlet's cancel, the time limit, the conversation ending); the " +
            "production Deep thinking plan for eleven setups (whether a think can run there, and whether it is checked to fit); a " +
            "think on a destination of its own running in parallel while replies go to the conversation's endpoint (no tools, " +
            "Thinking steps on, never stopped); the production side-by-side check for a second model in Ollama on this PC (a " +
            "fixture Ollama's /api/ps and /api/tags, graphics cards of several sizes, and stopping when loading it pushed " +
            "Thinking's model off the card); a long conversation fitted into a paired computer's 16 KiB and 16 messages; and one " +
            "moment (MomentTurn): what each trigger (you, what this PC played, finished work, a due look) takes along when the others " +
            "wait, and a combined reply carrying the PC's lines and a finished song and report with the One moment instruction at " +
            "the same place as a plain reply's. " +
            "reasoningMs (200-3000, default 1200) is how long the fixture's hidden " +
            "reasoning takes. Loopback only; reads no credentials.", new
        {
            reasoningMs = new { type = "integer", minimum = 200, maximum = 3000 }
        }),
        Tool("thinking_pool_status", "Companion > Thinking pool from a data directory: thinking-pool.json (or what Martlet would make " +
            "from the older deep-thinking.json, without writing it): each member (a paired computer's Thinking pool role or Ollama, " +
            "a model in Ollama on this PC or an OpenAI-compatible endpoint; never a key) with its slots, whether it sees pictures " +
            "or hears recordings, whether it can run and why; Use the conversation model when the pool is empty; the usable slots " +
            "and whether one is kept free for fast jobs (judges and summaries); each job kind's priority, whether it is fast and " +
            "whether a member can run it (the cheap CanRun answer); guidance (such as 1 slot: long thinking can delay screen and " +
            "sound summaries); warnings about likely slowdowns (a member beside the conversation's Thinking model or the voice); " +
            "Backup Thinking's choices (on or off, the delay or automatic, each member's May answer for the conversation and whether " +
            "it is a paid cloud provider) and the member it would ask now for a reply that is taken; " +
            "and the desktop's thinking-pool-status.json (running and waiting jobs by kind, never a job's text; Backup Thinking's " +
            "automatic delay, recent replies and how it ended lately). From that file, each member's online state (whether its " +
            "computer answers now, and since when it doesn't) and the pool's slots now against its slots when every computer " +
            "answers (presence). Read-only.", new
        {
            dataDirectory = new { type = "string" }
        }),
        Tool("thinking_pool_check", "Rehearse the Thinking pool's job board (ThinkingJobBoard over BackgroundPlaces, the production " +
            "code) with simulated members, NOT models: an empty pool answering no member at once, a picture going to the member " +
            "that sees and a recording finding none, two slots where a second long job waits while a judge takes the last free " +
            "slot, one slot where waiting jobs run highest priority first (barge-in judge, digest, research), a busy member passed " +
            "over for the next, a stale judge dropped, deep-thinking.json read once into thinking-pool.json, paired hosts joining " +
            "the pool by themselves (ThinkingPoolAutoJoin, sample hosts), and presence: a " +
            "member's computer going offline (its slots leave the pool, jobs go to the others and wait for it, the last-free-slot " +
            "rule counts only computers that answer, every computer offline lets the conversation model stand in) and answering " +
            "again (its slots come back and a job waiting in line starts there), while the think_longer tool text stays " +
            "byte-identical. In-process; reads nothing.", new { }),
        Tool("backup_thinking_check", "Rehearse Backup Thinking (Companion > Thinking pool, a hedged request: when the " +
            "conversation's Thinking model has no first words after the delay, the same request also goes to a pool member that " +
            "may answer for the conversation, and whichever starts first gives the reply) with the production race in " +
            "ConversationTurn, ConversationRuntime.OpenTextAsync and the production member choice (ThinkingBackupMembers) on " +
            "fixture turns: two fixture Chat Completions endpoints on 127.0.0.1 answer after set waits (canned words, NOT AI) and " +
            "note when the client stopped their stream. Scenarios: backup-wins (the conversation's model is slow: the member is " +
            "asked at the delay and answers, the conversation's stream is stopped, the latency line says Backup Thinking won), " +
            "conversation-wins (fast: no member asked), late-conversation (the conversation's model starts after the member was " +
            "asked: the member is stopped), conversation-fails (it fails after the member was asked: the member answers), " +
            "no-member (none may answer), held (a reply started early with only a paid cloud member: asked only once the reply is " +
            "taken), let-go (a reply started early and let go: its member's stream stops with it) and members (the rules: off, no " +
            "member ticked, a cloud member never unless ticked, a member on the conversation's computer passed over, tools only on " +
            "an endpoint). scenario runs one; delayMs is one of 500, 700, 900, 1200, 1500, 2000, 3000 (900 by default). Loopback " +
            "only; plays nothing.", new
        {
            scenario = new { type = "string", @enum = BackupThinkingCheck.Scenarios },
            delayMs = new { type = "integer", @enum = new[] { 500, 700, 900, 1200, 1500, 2000, 3000 } }
        }),
        Tool("quick_sounds_status", "Companion > Voice > Quick sounds while Martlet thinks, from a data directory: the choice (on or " +
            "off, off by default, and the delay, from talk-preferences.json), the voice replies speak with (in words, whether it is " +
            "a paid cloud voice, and its key per voice and character), whether its quick sounds are made (each clip's words and " +
            "length) or need the owner's click (a paid voice), every set kept in quick-sounds\\ and the newest desktop log lines " +
            "about quick sounds. Read-only; never plays anything.", new
        {
            dataDirectory = new { type = "string" }
        }),
        Tool("quick_sounds_check", "Rehearse quick sounds with the production rules (QuickSoundGate, QuickSoundWatcher, " +
            "ConversationTurn.PlayQuickSound) on fixture turns through the production conversation runtime, Chat Completions " +
            "adapter, host voice stream and playback sink: a fixture endpoint on 127.0.0.1 answers after a set wait (canned " +
            "words, NOT AI), a fixture voice makes a quiet tone (NOT AI) and a fixture speaker plays nothing. Scenarios: slow (the " +
            "quick sound plays once the reply has had no audio for the delay, the whole clip before the reply, which follows it on " +
            "its own run uncut, and the reply latency line says when), fast (none), cooldown (a second slow reply within 20 s gets " +
            "none), early (a reply started early and held for a second: counted from when it is taken, never while held), let-go " +
            "(held, then let go: none), reasoning (hidden reasoning before the words: after 300 ms) and paused (paused because you " +
            "talked over it: none). scenario runs one; delayMs is one of 500, 700, 1000, 1500 (700 by default). Loopback only; " +
            "plays nothing.", new
        {
            scenario = new { type = "string", @enum = QuickSoundCheck.Scenarios },
            delayMs = new { type = "integer", @enum = new[] { 500, 700, 1000, 1500 } }
        }),
        Tool("elevenlabs_check", "Rehearse ElevenLabs as the Voice (the owner's cloned voice with tones) end to end against a local " +
            "fixture on 127.0.0.1 that follows ElevenLabs' documented protocol (FIXTURE, NOT ElevenLabs, NOT AI: its voice is a quiet " +
            "tone): Instant Voice Cloning (POST /v1/voices/add with a synthetic WAV; clone.sent shows the form Martlet sent), one " +
            "segment straight through the Text to Dialogue WebSocket client (segment: its audio and fixture timings), and a whole " +
            "spoken reply through the production conversation runtime (a fixture Chat Completions endpoint streams reply, by default " +
            "one with [laughs], [whispers] and [happy], a word at a time; a fixture speaker plays nothing). ok needs the Thinking " +
            "prompt to list ElevenLabs' own tags (thinkingPrompt), every tag the reply wrote to reach ElevenLabs as written " +
            "(tags.reachedElevenLabs), the chat text and every caption to show none (tags.chatClean, tags.captionsClean), and each " +
            "connection to carry model_id, output_format=pcm_24000, the key in the xi-api-key header (never the body), the one cloned " +
            "voice and close_socket (protocol). scenario: reply (default), model-refused (the WebSocket refuses the model with " +
            "param model_id, as its API reference describes: the voice fails as ModelUnsupported and segment.detail says to choose " +
            "Eleven v3 Conversational; the text still completes) or bad-key (a wrong key: cloning and speech fail as Authentication). " +
            "model is eleven_v4_turbo (default) or eleven_v3_conversational. With dataDirectory, saved reports the saved ElevenLabs " +
            "choice (route, model, on, confirmed, key saved, cloned voice saved, verification asked, keys from before; never the key, " +
            "voice ID or voice name). Nothing is sent to ElevenLabs: live is always NOT RUN.", new
        {
            scenario = new { type = "string", @enum = ElevenLabsCheck.Scenarios },
            model = new { type = "string", @enum = Martlet.Core.Settings.ElevenLabsSetup.ModelIds },
            reply = new { type = "string", maxLength = 1024 },
            dataDirectory = new { type = "string" }
        }),
        Tool("live_floor_status", "The live floor (the live conversation turn comes before all background work) from a data directory: " +
            "what the conversation runs on (its Thinking, voice and listening routes, each on this PC, a computer on the home network " +
            "or a cloud provider, with the paired hosts and routes the floor holds while you talk), which Thinking pool members share " +
            "that hardware, what the floor does to each job kind on such a member at Listening and at Live, and the desktop's " +
            "live-floor.json: its level (Idle, Listening, Live), the jobs it held and stopped by kind this turn and in all, the hold " +
            "client and hosts held, the work queue's stopped background requests and its last changes (never what was said). Read-only.", new
        {
            dataDirectory = new { type = "string" }
        }),
        Tool("live_floor_check", "Rehearse the live floor with the production LiveFloor, LiveFloorRules, ThinkingJobBoard, BackgroundJobs " +
            "and WorkQueue on fixture inputs and simulated members, NOT models: which things said are real words (said: your own " +
            "lines, else fixtures such as \"Mmm.\", \"Yeah, right.\" and \"What time is it in Tokyo?\"), the levels on a clock of their " +
            "own (voice, quiet, a sound, words, a reply and its grace), the board at Listening (new work waits, running work and " +
            "judges go on) and at Live (a summary dropped, remembering and naming stopped and queued again, touch zones going on, " +
            "judges running), a member on another computer never held, a think stopped and going on from what it wrote (in place, " +
            "or again with it as context), research waiting for the conversation instead of being refused, the conversation " +
            "model's own place held above Idle, and the work queue stopping this PC's background request for a live reply. " +
            "In-process; reads nothing.", new
        {
            said = new { type = "array", items = new { type = "string" }, maxItems = 32 }
        }),
        Tool("work_sharing_status", "Devices > Sharing work from a data directory: the choices (work-sharing.json, the work-sharing " +
            "shared setting: for Speaking, Thinking, Listening and Deep thinking whether it is shared when its computer is busy, " +
            "the order chosen (this-pc being each companion PC's own host service) and the computers never used; which computers " +
            "are kept for one companion PC), the paired computers the shared plan says run each job's engine, the computer each " +
            "job uses now, and the order this PC (or deviceId) tries them in with the production planner (WorkSharing.Order). " +
            "Host and device IDs only. Read-only.", new
        {
            dataDirectory = new { type = "string" },
            deviceId = new { type = "string" }
        }),
        Tool("work_sharing_check", "Rehearse Sharing work with the production planner (WorkSharing.Order) and queue (WorkQueue) on " +
            "the four-computer example (three companion PCs with host services, machine 2 without a voice, machine 4 for lip-sync " +
            "and pictures) with simulated computers that run one request at a time and turn another away at once, as a host's " +
            "gateway does (job.busy), NOT real hosts or models: each companion's order with its own computer first, Thinking left " +
            "unshared, a busy voice passed over at once, machine 2 waiting while both voices are busy and taken by whichever frees " +
            "first, four segments at once spread over both, a computer kept for one companion PC or unticked for a job left out, " +
            "an unanswering computer skipped, Deep thinking leaving out a kept computer, and the shared setting's round trip. " +
            "In-process; reads nothing.", new { }),
        Tool("node_presence_status", "When your other computers go away or come back, from a data directory: the per-PC away time " +
            "(node-presence.txt; Settings > Your other computers, default 10 minutes), the rules (missing after 30 seconds without " +
            "an answer, back after 30 seconds of answers, the back notice shown 10 minutes) and the report the desktop writes when " +
            "a computer's state changes (node-presence.json: each paired computer's state Answering, NotAnswering, Missing, Away, " +
            "Returning or Back, since when, and the notices Home shows, such as \"Working with less: gpu-box isn't answering\"). " +
            "Host IDs, computer names and times only. Read-only.", new
        {
            dataDirectory = new { type = "string" }
        }),
        Tool("node_presence_check", "Rehearse the presence notices and events with the production rules (PresenceWatch, " +
            "NodePresenceNotices, NodePresenceSettings, NodePresenceReport) on scripted timelines: a check every 15 seconds with " +
            "the desktop's presence rule and a 5-second tick, NOT real hosts. One flaky miss says nothing; 30 seconds without an " +
            "answer goes missing once with a notice that names the failover move, the job that waits and the pools; still missing " +
            "after 10 minutes stays away once; 30 seconds of answers comes back once and the notice clears after 10 minutes or " +
            "when dismissed; a flapping computer stays one absence; the away time follows the per-PC choice; the report round trip; " +
            "an unpaired computer is forgotten. In-process; writes only a temporary folder.", new { }),
        Tool("research_check", "Rehearse web research (the research tool: Companion > Deep thinking > Web research, off by default) " +
            "end to end with Martlet's own tool texts and job kind (WebResearch: one at a time, 4 an hour, 12 minutes, offered when " +
            "done), background-job scheduler, web client (WebAccess: DuckDuckGo results parser with ads left out and redirect links " +
            "unwrapped, page reader keeping readable text, public-address guard on every connection and redirect), research loop " +
            "(WebResearchRun: first search and pages, then model steps of SEARCH, READ or the report, each a background think through " +
            "the conversation runtime and Chat Completions adapter), report creation and its web page (ResearchReports), against " +
            "fixtures on 127.0.0.1 (a search page, web pages including a PDF and a redirect to a private address, and a model with " +
            "canned answers, NOT AI): a reply says it'll look into it and calls research (returns at once, the reply completes while " +
            "the job runs), the steps' requests, the note the conversation gets (offer first, perform_creation with the report's " +
            "id), the report kept in a temporary Creations library and shown as a page; plus the settings (off by default, off with " +
            "Thinking longer off), the address guard and the limits (busy beside a think, Cancel, the hourly limit, a failed search, " +
            "and placement on Deep thinking's places, where research never takes the pool's last free slot, kept for quick jobs). " +
            "Loopback only; no real search or model; reads no credentials.", new { }),
        Tool("songs_status", "Martlet singing in conversation (sing_song, play_song, stop_singing), from a data directory: whether " +
            "background work (Thinking longer, which the song tools come with) is on; the song creations (each song's key, " +
            "length, lines and timed words, tempo, engine, mouth track source, assets and whether it is the FIXTURE - NOT AI song; never its " +
            "title or words); the desktop's songs-status.json (whether singing is offered, the song playing: state, position, line " +
            "number and section, where it started, lead-in bars and fade-in, vamps, ducking, the mouth frames sent and how, and its " +
            "stop plan; and the last stop: " +
            "where, which line and section, and why, without the user's words); the song job kind's limits; and the three tools and " +
            "Singing prompt exactly as the Thinking model gets them. Read-only.", new
        {
            dataDirectory = new { type = "string" }
        }),
        Tool("song_playback_check", "Run Martlet's production song playback headlessly (SongTransport, SongMixer and SongPlayer " +
            "pumping a fixture output ten times faster than real time; nothing is played aloud) on the FIXTURE - NOT AI tone song, " +
            "or on a song creation (songId: its key, with its dataDirectory), and measure the transitions in the audio it produced: where " +
            "play_song's from points (start, a section, line:N, a time, misses); the resume lead-in (entry downbeat, 1 or 2 bars, " +
            "equal-power fade-in gain at its start, middle and end, vocals silent until just before the line); the band vamping " +
            "twice while Martlet talks before the vocals come in; ducking (-12 dB); and a full run: sung from the top, stopped " +
            "musically mid-line by the user's words (the stop record and its note, the word's end, the beat the band fades from, " +
            "when the output went silent), resumed from that line with its lead-in, and stopped with Esc (a 300 ms fade); and lip sync: " +
            "the mouth tracks made from the vocals stem (Audio2Face when a service answers on 127.0.0.1:52000, visemes from the sung " +
            "words, loudness) with their offsets from the vocal onsets, and the mouth sent on the playback clock after a lead-in " +
            "against the onsets of the vocals actually played.", new
        {
            dataDirectory = new { type = "string" },
            songId = new { type = "string", maxLength = 32 }
        }),
        Tool("conversation_history_status", "Companion > Memory > Conversation history, from a data directory: whether memory is " +
            "on, this PC's choices (conversation-history.json: keep a record of conversations, on by default; let Martlet search it " +
            "on its own, off by default), whether exchanges are recorded and recalled when a message mentions an earlier " +
            "conversation, whether replies are offered search_conversations (exactly as the Thinking model gets it, with its size " +
            "in UTF-8 bytes and estimated tokens) and the Past conversations prompt, and what the record in the data folder's " +
            "conversations folder holds: files, bytes, conversations, exchanges per app (pc, telegram, discord, whatsapp), unreadable " +
            "lines and the oldest and newest times, and the deletions and edits waiting for Telegram and Discord " +
            "(platform-changes.json: pending per app, done, refused, given up, last problem). " +
            "Never what was said. Read-only.", new
        {
            dataDirectory = new { type = "string" }
        }),
        Tool("conversation_history_check", "Rehearse the record of conversations with the production code (ConversationHistory, " +
            "PastConversations and HistoryPlatforms) on synthetic conversations in a disposable folder: recording exchanges into month files, a line " +
            "cut short by a crash skipped after a restart, an ordinary message recalling nothing, \"Do you remember...\" and " +
            "\"What did we talk about yesterday?\" bringing back the right exchanges (never the conversation going on), " +
            "search_conversations by words and by time and its answers, deleting one conversation and everything, exchanges from " +
            "Telegram and Discord keeping their app, chat and message IDs (Discord never recalled in the talk window), what " +
            "deleting and editing one message asks of each app (48 hours on Telegram, never your DM messages on Discord, edited " +
            "replies cut to their pieces), editing and deleting single messages, the queue of changes for the apps (each app's " +
            "pace, a slow-down waited out, a refusal dropped, an unconnected app waiting, kept over a restart) with a fixture app " +
            "(not Telegram or Discord), and reading " +
            "bulkExchanges (1,000-100,000, default 20,000) exchanges with recall timings. Nothing leaves this PC.", new
        {
            bulkExchanges = new { type = "integer", minimum = 1_000, maximum = 100_000 }
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
                "companion_status" => await CompanionStatusAsync(OptionalString(arguments, "platform"), OptionalString(arguments, "settingsFile"), cancellation),
                "doctor_status" => await DoctorAsync(["status", "--json"], arguments, cancellation),
                "doctor_list" => await DoctorAsync(["list", "--json"], arguments, cancellation),
                "doctor_run" => await DoctorAsync(
                    ["run", .. RequiredStrings(arguments, "probes"), "--json"], arguments, cancellation),
                "logs_tail" => LogTail.Read(OptionalString(arguments, "dataDirectory"), OptionalString(arguments, "log"),
                    OptionalInt(arguments, "lines"), OptionalString(arguments, "contains")),
                "logs_timeline" => LogTimeline.Read(OptionalString(arguments, "dataDirectory"), OptionalString(arguments, "level"),
                    OptionalString(arguments, "component"), OptionalString(arguments, "contains"), OptionalInt(arguments, "lines"),
                    OptionalString(arguments, "source")),
                "logs_export" => LogTimeline.Export(OptionalString(arguments, "dataDirectory"), RequiredString(arguments, "outputPath")),
                "logs_share_selftest" => await NodeLinkCheckAsync(cancellation, "logs"),
                "host_connections_selftest" => await NodeLinkCheckAsync(cancellation, "host-connections"),
                "latency_report" => LatencyReport.Read(OptionalString(arguments, "dataDirectory"), OptionalInt(arguments, "replies")),

                "ui_connect" => desktop.Connect(RequiredInt(arguments, "pid")),
                "ui_snapshot" => desktop.Snapshot(OptionalBool(arguments, "layout") ?? false, OptionalString(arguments, "idPrefix")),
                "ui_click" => await desktop.ClickAsync(RequiredString(arguments, "id"), OptionalString(arguments, "window"),
                    OptionalBool(arguments, "focus") ?? false),
                "ui_select" => desktop.Select(RequiredString(arguments, "id"), RequiredString(arguments, "item")),
                "ui_set_text" => desktop.SetText(RequiredString(arguments, "id"),
                    OptionalString(arguments, "text") ?? throw new ArgumentException("Missing string 'text'.")),
                "ui_toggle" => desktop.Toggle(RequiredString(arguments, "id")),
                "ui_set_range" => desktop.SetRange(RequiredString(arguments, "id"), RequiredDouble(arguments, "value")),
                "ui_move" => desktop.Move(RequiredString(arguments, "id"), RequiredInt(arguments, "dx"), RequiredInt(arguments, "dy")),
            "character_touch" => await desktop.TouchCharacterAsync(OptionalDouble(arguments, "x"), OptionalDouble(arguments, "y"),
                OptionalInt(arguments, "holdMs"), OptionalInt(arguments, "repeat"), OptionalInt(arguments, "gapMs"), Taps(arguments),
                OptionalInt(arguments, "settleMs")),
                "character_stroke" => await desktop.StrokeCharacterAsync(StrokePoints(arguments), OptionalInt(arguments, "stepMs") ?? 40),
                "character_face" => await desktop.FaceCharacterAsync(OptionalInt(arguments, "samples"), OptionalInt(arguments, "gapMs")),
                "ui_tray" => desktop.Tray(OptionalString(arguments, "action") ?? "status", OptionalInt(arguments, "x"), OptionalInt(arguments, "y")),
                "voices_status" => VoicesStatus(arguments),
                "turn_judge_check" => await TurnJudgeCheck.RunAsync(arguments, MartletDirectory(arguments), cancellation),
                "early_reply_check" => await EarlyReplyCheck.RunAsync(OptionalString(arguments, "scenario"), OptionalInt(arguments, "thinkingMs"),
                    OptionalInt(arguments, "voiceMs"), OptionalInt(arguments, "sttMs"), OptionalInt(arguments, "judgeMs"), cancellation),
                "parakeet_check" => await ParakeetCheck.RunAsync(arguments, DataDirectory(arguments), MartletDirectory(arguments),
                    OptionalString(arguments, "speechDirectory") is not null ? SpeechDirectory(arguments) : Path.Combine(DataDirectory(arguments), "speech"),
                    cancellation),
                "voices_naming_check" => VoiceNamingCheck.Run(DataDirectory(arguments), OptionalString(arguments, "answer"),
                    OptionalString(arguments, "reply")),
                "voices_engine_check" => await Task.Run(() => VoicesEngineCheck(arguments), cancellation),
                "f5_voices" => F5Voices(arguments),
                "voice_recording_check" => await VoiceRecordingCheckAsync(RequiredString(arguments, "path"), cancellation),
            "voice_tags" => VoiceTagsCheck(arguments),
                "cluster_status" => ClusterStatus(arguments),
                "network_status" => NetworkStatus(arguments),
                "outside_reachability_check" => await OutsideReachabilityAsync(arguments, cancellation),
                "network_selftest" => await NodeLinkCheckAsync(cancellation, "network"),
            "signin_selftest" => await NodeLinkCheckAsync(cancellation, "signin"),
            "signin_lab" => await SignInLabAsync(arguments, cancellation),
                "nearby_status" => NearbyStatus(arguments),
                "virtualization_status" => await VirtualizationStatusAsync(arguments, cancellation),
                "host_service_status" => await HostServiceStatusAsync(cancellation),
                "node_link_check" => await NodeLinkCheckAsync(cancellation),
                "host_engine_check" => await HostEngineCheck.RunAsync(cancellation),
                "host_supply_check" => await HostSupplyCheck.RunAsync(arguments, cancellation),
                "host_update_check" => HostUpdateCheck.Run(),
                "app_update_check" => await AppUpdateCheck.RunAsync(NodeLinkCheckProgram(), cancellation),
                "api_keys_status" => ApiKeysStatus(arguments),
                "api_selftest" => await NodeLinkCheckAsync(cancellation, "api"),
                "exposure_selftest" => await NodeLinkCheckAsync(cancellation, "exposure"),
                "outside_path_check" => await OutsidePathCheck.RunAsync(cancellation),
            "mac_host_check" => await MacHostCheck.RunAsync(cancellation),
                "deep_thinking_role_selftest" => await NodeLinkCheckAsync(cancellation, "deep-thinking"),
                "gpu_priority_selftest" => await NodeLinkCheckAsync(cancellation, "gpu-priority"),
                "gpu_priority_status" => await GpuPriorityStatusAsync(arguments, cancellation),
                "speaking_voices_selftest" => await NodeLinkCheckAsync(cancellation, "voices"),
                "character_models" => CharacterModels(arguments),
                "character_profiles" => CharacterProfiles(arguments),
                "character_actions" => await CharacterActionsCheckAsync(arguments, cancellation),
                "character_gaze" => GazeCheck.Run(DataDirectory(arguments), OptionalString(arguments, "answer"), OptionalString(arguments, "personaId")),
                "character_physical_check" => PhysicalCheck.Run(DataDirectory(arguments), OptionalString(arguments, "modelId"),
                    OptionalString(arguments, "stroke"), OptionalString(arguments, "changes"), OptionalBool(arguments, "noticeAll") ?? true),
                "character_touch_zones" => await TouchZonesCheck.RunAsync(DataDirectory(arguments),
                    OptionalString(arguments, "dataDirectory") is not null, OptionalString(arguments, "modelPath"), OptionalString(arguments, "modelId"),
                    OptionalString(arguments, "answer"), OptionalInt(arguments, "width"), OptionalInt(arguments, "height"),
                    OptionalString(arguments, "crop"), OptionalString(arguments, "probe"), OptionalString(arguments, "touch"),
                    OptionalBool(arguments, "save") ?? false, OptionalBool(arguments, "includeIntimate"), OptionalString(arguments, "snapshotPath"),
                    cancellation, OptionalString(arguments, "temperament"), OptionalString(arguments, "personaId"), OptionalString(arguments, "personality"),
                    OptionalInt(arguments, "repeats"), OptionalBool(arguments, "detect") ?? false, OptionalString(arguments, "guess"),
                    OptionalString(arguments, "previewDirectory"), OptionalInt(arguments, "checks"), OptionalInt(arguments, "failAt")),
                "character_theme" => await CharacterThemeCheck.RunAsync(OptionalString(arguments, "modelPath"), OptionalString(arguments, "dataDirectory"),
                    OptionalString(arguments, "previewDirectory"), OptionalString(arguments, "label"), cancellation),
                "character_models_selftest" => await NodeLinkCheckAsync(cancellation, "characters"),
                "creations_status" => CreationsCheck.Status(DataDirectory(arguments)),
                "creations_check" => OptionalString(arguments, "seedDataDirectory") is { } seed
                    ? await CreationsCheck.SeedAsync(seed, cancellation)
                    : await CreationsCheck.RunAsync(() => NodeLinkCheckAsync(cancellation, "creations"), cancellation),
                "settings_sync_status" => SettingsSyncStatus(arguments),
                "settings_sync_selftest" => await NodeLinkCheckAsync(cancellation, "settings"),
                "memory_sync_status" => MemorySyncStatus(arguments),
                "memory_status" => await MemoryStatusAsync(arguments, cancellation),
                "memory_sync_selftest" => await NodeLinkCheckAsync(cancellation, "memories"),
                "audio2face_check" => await Audio2FaceCheck.RunAsync(OptionalString(arguments, "endpoint"),
                    OptionalInt(arguments, "seconds"), OptionalInt(arguments, "sampleRate"), cancellation),
                "voice_engine_check" => await VoiceEngineCheckAsync(arguments, cancellation),
                "listening_engine_check" => await ListeningEngineCheck.RunAsync(arguments, OptionalString(arguments, "endpoint"),
                    OptionalString(arguments, "model"), cancellation),
                "singing_status" => await SingingStatusAsync(arguments, cancellation),
                "singing_check" => await SingingCheckAsync(arguments, cancellation),
                "reading_check" => await ReadingCheck.RunAsync(
                    OptionalString(arguments, "dataDirectory") is null ? null : DataDirectory(arguments),
                    OptionalString(arguments, "endpoint"), cancellation),
                "pictures_status" => PicturesCheck.Status(DataDirectory(arguments)),
                "pictures_check" => await PicturesCheck.RunAsync(OptionalString(arguments, "place"), OptionalString(arguments, "address"),
                    OptionalString(arguments, "workflow"), OptionalString(arguments, "checkpoint"), OptionalString(arguments, "workflowFile"),
                    OptionalString(arguments, "prompt"), OptionalString(arguments, "shape"),
                    OptionalString(arguments, "dataDirectory") is null ? null : DataDirectory(arguments), OptionalString(arguments, "saveDirectory"),
                    cancellation),
                "mcp_servers_status" => McpServersStatus(arguments),
                "mcp_directory_plan" => McpDirectoryPlan(arguments),
                "home_assistant_probe" => await HomeAssistantProbeAsync(arguments, cancellation),
                "home_assistant_find" => await HomeAssistantFindAsync(arguments, cancellation),
                "smart_home_status" => SmartHomeStatus(arguments),
                "discord_status" => DiscordCheck.Status(DataDirectory(arguments)),
                "discord_check" => await DiscordCheck.RunAsync(DataDirectory(arguments), OptionalInt(arguments, "seconds"), cancellation),
                "messaging_status" => MessagingStatus(arguments),
                "terminal_status" => TerminalCheck.Status(DataDirectory(arguments)),
                "terminal_check" => await TerminalCheck.RunAsync(DataDirectory(arguments), OptionalString(arguments, "shell"), cancellation),
                "prompts_status" => await PromptsStatusAsync(arguments, cancellation),
                "character_status" => await CharacterStatusAsync(arguments, cancellation),
                "hearing_check" => await HearingCheck.RunAsync(OptionalString(arguments, "modelId"), DataDirectory(arguments), cancellation),
                "straight_voice_check" => await StraightVoiceCheck.RunAsync(MartletDirectory(arguments), SpeechDirectory(arguments),
                    OptionalBool(arguments, "live") ?? false, OptionalString(arguments, "model"), cancellation),
                "discord_voice_check" => await DiscordVoiceCheck.RunAsync(OptionalString(arguments, "martletDirectory") is null ? null : MartletDirectory(arguments), cancellation),
                "model_ability_check" => await ModelAbilityCheck.RunAsync(DataDirectory(arguments), OptionalString(arguments, "baseUrl"),
                    OptionalString(arguments, "modelId"), OptionalBool(arguments, "test") ?? false, cancellation),
                "spoken_reply_check" => await SpokenReplyCheck.RunAsync(OptionalString(arguments, "voiceFailure"),
                    OptionalInt(arguments, "failAt"), cancellation, OptionalInt(arguments, "reasoningMs"),
                    OptionalInt(arguments, "voiceDelayMs"), OptionalString(arguments, "reply"),
                    SpeechBreaksFrom(arguments, SavedSettings(arguments), out var speaker), speaker?.Name,
                    OptionalString(arguments, "thinkingSteps"), OptionalBool(arguments, "refuseThinking") ?? false,
                    OptionalBool(arguments, "chattiness") ?? false, OptionalStrings(arguments, "characterTags")),
                "echo_check" => await EchoCheck.RunAsync(DataDirectory(arguments), OptionalInt(arguments, "delayMs"), cancellation),
                "utterance_filter_check" => await UtteranceFilterCheck.RunAsync(arguments, DataDirectory(arguments), MartletDirectory(arguments),
                    SpeechDirectory(arguments), cancellation),
                "barge_in_check" => await BargeInCheck.RunAsync(arguments, DataDirectory(arguments), cancellation),
                "pc_audio_check" => await PcAudioCheck.RunAsync(DataDirectory(arguments), cancellation),
                "sound_digest_check" => await SoundDigestCheck.RunAsync(DataDirectory(arguments), MartletDirectory(arguments),
                    OptionalString(arguments, "wavFile"), cancellation),
                "discord_call_check" => await DiscordCallCheck.RunAsync(DataDirectory(arguments), cancellation),
                "discord_text_check" => await DiscordTextCheck.RunAsync(DataDirectory(arguments), arguments, cancellation),
                "chattiness_status" => await ChattinessCheck.RunAsync(DataDirectory(arguments), OptionalString(arguments, "reply"), cancellation),
                "vision_history_check" => await VisionHistoryCheck.RunAsync(DataDirectory(arguments), OptionalString(arguments, "reply"), cancellation),
                "screen_digest_check" => await ScreenDigestCheck.RunAsync(DataDirectory(arguments), OptionalString(arguments, "reply"), cancellation),
                "context_check" => await ContextCheck.RunAsync(DataDirectory(arguments), cancellation),
                "context_board" => await ContextBoardCheck.RunAsync(OptionalString(arguments, "source"), OptionalString(arguments, "text"),
                    OptionalInt(arguments, "maxAgeSeconds"), OptionalBool(arguments, "consume"), OptionalInt(arguments, "ageSeconds"), cancellation),
                "discord_companion_check" => await DiscordCompanionCheck.RunAsync(DataDirectory(arguments),
                    arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty("dataDirectory", out _),
                    OptionalString(arguments, "person"), OptionalString(arguments, "character"),
                    OptionalString(arguments, "requestFrom") is { } from
                        ? ulong.TryParse(from, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var id)
                            ? id : throw new ArgumentException("requestFrom must be a Discord user ID.")
                        : null,
                    OptionalString(arguments, "requestName"), cancellation),
                "thinking_steps_check" => await ThinkingStepsCheck.RunAsync(DataDirectory(arguments), OptionalString(arguments, "model"),
                    OptionalBool(arguments, "live") ?? false, cancellation),
                "reminders_status" => await RemindersCheck.StatusAsync(DataDirectory(arguments), cancellation),
                "reminders_check" => await RemindersCheck.RunAsync(cancellation),
                "setup_run_status" => await SetupRunCheck.StatusAsync(DataDirectory(arguments), cancellation),
                "setup_run_check" => await SetupRunCheck.RunAsync(cancellation),
                "think_longer_status" => await ThinkLongerCheck.StatusAsync(DataDirectory(arguments), cancellation),
                "helper_jobs_status" => HelperJobsCheck.Status(DataDirectory(arguments)),
                "helper_jobs_check" => await HelperJobsCheck.RunAsync(cancellation),
                "thinking_pool_status" => await ThinkingPoolCheck.StatusAsync(DataDirectory(arguments), cancellation),
                "quick_sounds_status" => QuickSoundCheck.Status(DataDirectory(arguments)),
                "quick_sounds_check" => await QuickSoundCheck.RunAsync(OptionalString(arguments, "scenario"), OptionalInt(arguments, "delayMs"), cancellation),
                "elevenlabs_check" => await ElevenLabsCheck.RunAsync(OptionalString(arguments, "scenario"), OptionalString(arguments, "model"),
                    OptionalString(arguments, "reply"), OptionalString(arguments, "dataDirectory") is null ? null : DataDirectory(arguments), cancellation),
                "thinking_pool_check" => await ThinkingPoolCheck.RunAsync(cancellation),
                "backup_thinking_check" => await BackupThinkingCheck.RunAsync(OptionalString(arguments, "scenario"), OptionalInt(arguments, "delayMs"), cancellation),
                "live_floor_status" => await LiveFloorCheck.StatusAsync(DataDirectory(arguments), cancellation),
                "live_floor_check" => await LiveFloorCheck.RunAsync(OptionalStrings(arguments, "said")?.Take(32).ToArray(), cancellation),
                "work_sharing_status" => await WorkSharingCheck.StatusAsync(DataDirectory(arguments), OptionalString(arguments, "deviceId"), cancellation),
                "work_sharing_check" => await WorkSharingCheck.RunAsync(cancellation),
                "node_presence_status" => NodePresenceCheck.Status(DataDirectory(arguments)),
                "node_presence_check" => NodePresenceCheck.Run(),
                "discord_reply_status" => DiscordReplyCheck.Status(DataDirectory(arguments)),
                "discord_reply_check" => await DiscordReplyCheck.RunAsync(DataDirectory(arguments), OptionalString(arguments, "model"),
                    OptionalBool(arguments, "live") ?? false, cancellation),
                "think_longer_check" => await ThinkLongerCheck.RunAsync(OptionalInt(arguments, "reasoningMs"), cancellation),
            "research_check" => await ResearchCheck.RunAsync(cancellation),
                "conversation_history_status" => await ConversationHistoryCheck.StatusAsync(DataDirectory(arguments), cancellation),
                "conversation_history_check" => await ConversationHistoryCheck.RunAsync(OptionalInt(arguments, "bulkExchanges"), cancellation),
                "songs_status" => await SongsCheck.StatusAsync(DataDirectory(arguments), cancellation),
                "song_playback_check" => await SongsCheck.RunAsync(
                    OptionalString(arguments, "dataDirectory") is null ? null : DataDirectory(arguments), OptionalString(arguments, "songId"),
                    cancellation),
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
                Martlet.Core.Speakers.CompanionNames? companion;
                try { companion = VoiceNamingCheck.SavedCompanion(directory).Names; }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or Martlet.Core.Contracts.ContractException)
                {
                    companion = null;
                }
                companion ??= Martlet.Core.Speakers.CompanionNames.Martlet;
                roster = new
                {
                    state = "loaded", voices = list.Live.Count, named = list.Live.Count(v => v.Named), owner = list.Live.Count(v => v.Owner),
                    withLearnedNames = list.Live.Count(v => v.Names.Any(n => n.Source == Martlet.Core.Speakers.VoiceNameSource.Conversation)),
                    // Voices that learned one of the companion's own names; Martlet drops it when it next hears them.
                    withCompanionName = list.Live.Count(v => v.Names.Any(n => n.Source == Martlet.Core.Speakers.VoiceNameSource.Conversation &&
                        companion.Matches(n.Text))),
                    withPlaceholderName = list.Live.Count(v => v.Names.Any(n => n.Source == Martlet.Core.Speakers.VoiceNameSource.Conversation &&
                        Martlet.Core.Speakers.VoiceUpdates.IsNotName(n.Text))),
                    mostNames = list.Live.Select(v => v.Names.Count).DefaultIfEmpty(0).Max(),
                    merged = list.Live.Sum(v => v.MergedVoices), tombstones = list.Voices.Count(v => v.Removed)
                };
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or Martlet.Core.Contracts.ContractException)
            {
                roster = new { state = "unreadable" };
            }
        }
        var speech = Path.Combine(directory, "speech");
        var martlet = MartletDirectory(arguments);
        return new
        {
            recognition = Choice("voice-recognition.txt") ?? "on (default)",
            // The voice list travels with the rest of Martlet while "Keep Martlet the same on all my computers" is on.
            sharing = Choice("cluster-sync.txt") is "off" ? "off" : "on (Keep Martlet the same on all my computers)",
            included = new
            {
                found = File.Exists(Path.Combine(martlet, "Martlet.Desktop.exe")),
                runtime = Martlet.Sherpa.SherpaComponents.RuntimeDirectory(martlet) is not null,
                voiceModels = Martlet.Sherpa.SherpaComponents.VoiceRecognitionIncluded(martlet)
            },
            parakeet = Martlet.Sherpa.SherpaComponents.InstalledParakeetModels(speech).Count > 0,
            parakeetModels = ParakeetCheck.Status(directory, speech),
            roster,
            clips = Clips()
        };

        object Clips()
        {
            var root = Path.Combine(directory, "voice-clips");
            int[] counts;
            try { counts = Directory.Exists(root) ? Directory.GetDirectories(root).Select(d => Directory.GetFiles(d, "*.wav").Length).Where(n => n > 0).ToArray() : []; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { counts = []; }
            return new { keep = Choice("voice-clips.txt") ?? "on (default)", voices = counts.Length, clips = counts.Sum() };
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

    /// <summary>Runs the voice recognition engine bundled in a Martlet folder the way the desktop does (Martlet.Sherpa's
    /// SpeakerEngine with that folder's runtime and models): a generated test tone proves both models load and run, and optional
    /// WAV files show how many voices each has and how alike their main voices are. Scores and counts only.</summary>
    private static object VoicesEngineCheck(JsonElement arguments)
    {
        var martlet = MartletDirectory(arguments);
        var files = new List<string>();
        if (arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty("wavFiles", out var given) && given.ValueKind != JsonValueKind.Null)
        {
            if (given.ValueKind != JsonValueKind.Array || given.GetArrayLength() > 16) throw new ArgumentException("wavFiles must be at most 16 paths.");
            foreach (var item in given.EnumerateArray())
            {
                var path = item.ValueKind == JsonValueKind.String ? item.GetString()! : throw new ArgumentException("wavFiles must be strings.");
                if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("Each WAV file must be an absolute path.");
                files.Add(path);
            }
        }
        if (!Martlet.Sherpa.SherpaComponents.VoiceRecognitionIncluded(martlet))
            return new { included = false, runtime = Martlet.Sherpa.SherpaComponents.RuntimeDirectory(martlet) is not null };
        var clock = System.Diagnostics.Stopwatch.StartNew();
        using var engine = new Martlet.Sherpa.SpeakerEngine(martlet);
        // Three seconds of a gliding harmonic tone: not speech, but enough for both models to load and produce a voiceprint.
        var tone = new float[3 * Martlet.Sherpa.SpeakerEngine.SampleRate];
        for (var i = 0; i < tone.Length; i++)
        {
            var t = i / (double)Martlet.Sherpa.SpeakerEngine.SampleRate;
            var f = 140 + 40 * Math.Sin(2 * Math.PI * 0.5 * t);
            tone[i] = (float)(0.2 * Math.Sin(2 * Math.PI * f * t) + 0.1 * Math.Sin(4 * Math.PI * f * t) + 0.05 * Math.Sin(6 * Math.PI * f * t));
        }
        var probe = engine.Voiceprint(tone);
        var toneAnalysis = engine.Analyze(tone);
        var loadMs = clock.ElapsedMilliseconds;
        var prints = new List<float[]?>();
        var results = new List<object>();
        foreach (var path in files)
        {
            var wave = Martlet.Providers.BoundedWaveAudio.FromWave(File.ReadAllBytes(path));
            if (wave.Format.SampleRate != Martlet.Sherpa.SpeakerEngine.SampleRate) throw new ArgumentException("Each WAV file must be 16 kHz.");
            var bytes = File.ReadAllBytes(path).AsSpan(44);
            var samples = new float[bytes.Length / 2];
            for (var i = 0; i < samples.Length; i++)
                samples[i] = System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(bytes.Slice(i * 2, 2)) / 32768f;
            var started = clock.ElapsedMilliseconds;
            var analysis = engine.Analyze(samples);
            Array.Clear(samples);
            prints.Add(analysis.Speakers.FirstOrDefault()?.Voiceprint ?? analysis.Whole);
            results.Add(new
            {
                file = Path.GetFileName(path), seconds = Math.Round(wave.Duration.TotalSeconds, 2), voicesHeard = analysis.Voices,
                recognizable = analysis.Speakers.Select(s => new { cleanSeconds = s.CleanSeconds, start = s.Start, end = s.End }),
                overlap = analysis.Overlap, speechSeconds = analysis.SpeechSeconds, wholeVoiceprint = analysis.Whole is not null,
                ms = clock.ElapsedMilliseconds - started
            });
        }
        static double? Cosine(float[]? a, float[]? b) => a is null || b is null ? null : Math.Round(a.Zip(b, (x, y) => (double)x * y).Sum(), 3);
        return new
        {
            included = true, loaded = true, voiceprintDimensions = probe?.Length, toneVoices = toneAnalysis.Voices, loadMs,
            files = results,
            similarity = prints.Select(a => prints.Select(b => Cosine(a, b)).ToArray()).ToArray()
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

    /// <summary>voice_engine_check: Martlet.NodeLinkCheck's voice-engine mode against a live voice service on loopback. A model
    /// that is still loading can take minutes, so it gets longer than the rehearsals.</summary>
    private static async Task<object> VoiceEngineCheckAsync(JsonElement arguments, CancellationToken cancellation)
    {
        var engine = OptionalString(arguments, "engine") ?? "chatterbox";
        if (!VoiceEnginePorts.TryGetValue(engine, out var port))
            throw new ArgumentException($"engine must be one of {string.Join(", ", VoiceEnginePorts.Keys)}.");
        var endpoint = OptionalString(arguments, "endpoint") ?? $"http://127.0.0.1:{port}/";
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttp ||
            !System.Net.IPAddress.TryParse(uri.Host, out var address) || !System.Net.IPAddress.IsLoopback(address))
            throw new ArgumentException("endpoint must be a numeric loopback address such as http://127.0.0.1:50083/.");
        var text = OptionalString(arguments, "text") is { Length: > 0 } said ? said : "-";
        var styleDirectory = OptionalString(arguments, "dataDirectory") is { Length: > 0 } data ? data : null;
        if (styleDirectory is not null && !Path.IsPathFullyQualified(styleDirectory))
            throw new ArgumentException("dataDirectory must be an absolute path.");
        string[] command = styleDirectory is null
            ? ["voice-engine", engine, uri.GetLeftPart(UriPartial.Authority) + "/", text]
            : ["voice-engine", engine, uri.GetLeftPart(UriPartial.Authority) + "/", text, styleDirectory];
        return await NodeLinkCheckAsync(TimeSpan.FromMinutes(6), cancellation, command);
    }

    /// <summary>singing_check: Martlet.NodeLinkCheck's singing-check mode, with the fixture service, a live one on loopback, or
    /// (with dataDirectory) a real paired host through its own gateway.</summary>
    private static async Task<object> SingingCheckAsync(JsonElement arguments, CancellationToken cancellation)
    {
        var endpoint = OptionalString(arguments, "endpoint") ?? "fixture";
        var paired = OptionalString(arguments, "dataDirectory") is { Length: > 0 } pairedData ? pairedData : null;
        if (paired is not null)
        {
            if (!Path.IsPathFullyQualified(paired) || !File.Exists(Path.Combine(paired, "hosts.json")))
                throw new ArgumentException("dataDirectory must be the absolute path of a Martlet desktop data directory paired with a host (it has hosts.json).");
            endpoint = "paired:" + paired;
        }
        else if (endpoint != "fixture" && (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttp ||
            !System.Net.IPAddress.TryParse(uri.Host, out var address) || !System.Net.IPAddress.IsLoopback(address)))
            throw new ArgumentException("endpoint must be \"fixture\" or a numeric loopback address such as http://127.0.0.1:50085/.");
        if (paired is null && endpoint != "fixture") endpoint = new Uri(endpoint).GetLeftPart(UriPartial.Authority) + "/";
        var seconds = arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty("seconds", out var value) &&
            value.TryGetInt32(out var number) ? number : endpoint == "fixture" ? 20 : 30;
        if (seconds is < 15 or > 180) throw new ArgumentException("seconds must be 15 to 180.");
        var quality = OptionalString(arguments, "quality") ?? "fast";
        var voiceMatch = OptionalString(arguments, "voiceMatch") ?? "soulx";
        if (quality is not ("fast" or "high_quality")) throw new ArgumentException("quality must be fast or high_quality.");
        if (voiceMatch is not ("soulx" or "vevosing")) throw new ArgumentException("voiceMatch must be soulx or vevosing.");
        string[] command = ["singing-check", endpoint, seconds.ToString(System.Globalization.CultureInfo.InvariantCulture), quality, voiceMatch];
        var save = OptionalString(arguments, "saveDirectory") is { Length: > 0 } folder ? folder : null;
        if (save is not null && !Path.IsPathFullyQualified(save)) throw new ArgumentException("saveDirectory must be an absolute folder.");
        command = [.. command, save ?? "-"];
        var recording = OptionalString(arguments, "voiceRecording") is { Length: > 0 } wav ? wav : null;
        if (recording is not null && (!Path.IsPathFullyQualified(recording) || !recording.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(recording)))
            throw new ArgumentException("voiceRecording must be the absolute path of an existing .wav file.");
        var bpm = arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty("bpm", out var tempo) && tempo.TryGetInt32(out var beats)
            ? beats : 90;
        if (bpm is not 0 and (< 40 or > 240)) throw new ArgumentException("bpm must be 0 (none) or 40 to 240.");
        var key = arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty("key", out var keyValue) ? keyValue.GetString() ?? "" : "G major";
        var voiceId = OptionalString(arguments, "voiceId") is { Length: > 0 } id ? id : null;
        var host = OptionalString(arguments, "host") is { Length: > 0 } named ? named : null;
        if (voiceId is not null && (voiceId.Length < 8 || !voiceId.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')))
            throw new ArgumentException("voiceId must be a voice ID or a prefix of 8+ of its characters.");
        if (host is not null && !host.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-'))
            throw new ArgumentException("host must be a paired host's ID.");
        if (paired is null && (voiceId is not null || host is not null))
            throw new ArgumentException("voiceId and host need dataDirectory (a paired desktop data directory).");
        command = [.. command, recording ?? "-", OptionalString(arguments, "voiceTranscript") is { Length: > 0 } transcript ? transcript : "-",
            bpm == 0 ? "-" : bpm.ToString(System.Globalization.CultureInfo.InvariantCulture), key.Length == 0 ? "-" : key,
            voiceId ?? "-", host ?? "-"];
        return await NodeLinkCheckAsync(TimeSpan.FromMinutes(20), cancellation, command);
    }

    /// <summary>singing_status: the singing service's own /status over loopback (nothing secret: model IDs, licences, sizes,
    /// state, queue, GPU memory) and the Singing card's saved choices in a data directory.</summary>
    private static async Task<object> SingingStatusAsync(JsonElement arguments, CancellationToken cancellation)
    {
        var endpoint = OptionalString(arguments, "endpoint") ?? "http://127.0.0.1:50085/";
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttp ||
            !System.Net.IPAddress.TryParse(uri.Host, out var address) || !System.Net.IPAddress.IsLoopback(address))
            throw new ArgumentException("endpoint must be a numeric loopback address such as http://127.0.0.1:50085/.");
        object service;
        try
        {
            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            using var response = await http.GetAsync(new Uri(new Uri(uri.GetLeftPart(UriPartial.Authority) + "/"), "status"), cancellation);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellation));
            var root = document.RootElement;
            JsonElement? Field(string name) => root.TryGetProperty(name, out var field) ? field.Clone() : null;
            var worker = root.TryGetProperty("worker", out var identity) && identity.ValueKind == JsonValueKind.Object ? identity : default;
            var artifacts = worker.ValueKind == JsonValueKind.Object && worker.TryGetProperty("artifacts", out var list) &&
                list.ValueKind == JsonValueKind.Array ? list.EnumerateArray().ToArray() : [];
            service = new
            {
                answered = true, state = Field("state"), ready = Field("ready"), engine = Field("engine"), error = Field("error"),
                qualities = Field("qualities"), voiceMatches = Field("voice_matches"), queue = Field("queue"), running = Field("running"),
                workerRunning = Field("worker_running"), restarts = Field("restarts"), idleReleaseSeconds = Field("idle_release_seconds"),
                gpu = Field("gpu"),
                evidence = worker.ValueKind == JsonValueKind.Object && worker.TryGetProperty("evidence", out var evidence) ? evidence.GetString() : null,
                sources = worker.ValueKind == JsonValueKind.Object && worker.TryGetProperty("sources", out var sources) ? sources.Clone() : (JsonElement?)null,
                models = artifacts.Select(a => new
                {
                    id = a.GetProperty("artifact_id").GetString(), revision = a.GetProperty("revision").GetString(),
                    license = a.GetProperty("license_id").GetString(), bytes = a.GetProperty("bytes").GetInt64()
                }),
                modelBytes = artifacts.Sum(a => a.GetProperty("bytes").GetInt64())
            };
        }
        catch (Exception error) when (error is System.Net.Http.HttpRequestException or TaskCanceledException or JsonException or
            InvalidOperationException or KeyNotFoundException)
        {
            service = new { answered = false, problem = error.Message };
        }
        object? choices = null;
        object? paired = null;
        if (OptionalString(arguments, "dataDirectory") is { } directory)
        {
            // Singing on the hosts this desktop is paired with, through their own gateways (as the card reads it).
            if (Path.IsPathFullyQualified(directory) && File.Exists(Path.Combine(directory, "hosts.json")))
                paired = await NodeLinkCheckAsync(TimeSpan.FromMinutes(2), cancellation, ["singing-status", directory]);
            var path = Path.Combine(directory, "singing.json");
            try
            {
                using var saved = JsonDocument.Parse(File.ReadAllBytes(path));
                var host = saved.RootElement.TryGetProperty("host", out var h) && h.ValueKind == JsonValueKind.String ? h.GetString() : null;
                choices = new
                {
                    quality = saved.RootElement.TryGetProperty("quality", out var q) ? q.GetString() : null,
                    voiceMatch = saved.RootElement.TryGetProperty("voiceMatch", out var m) ? m.GetString() : null,
                    // The computer the desktop last saw running Singing; set up (SongClient.IsSetUp) while it is still paired.
                    host,
                    setUp = host is not null && PairedHostIds(directory).Contains(host)
                };
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
            {
                choices = new { quality = "fast", voiceMatch = "soulx", saved = false };
            }
        }
        return new { endpoint = uri.GetLeftPart(UriPartial.Authority) + "/", route = "martlet.gateway.song.v1", port = 50085, service, choices, paired };
    }

    /// <summary>The host IDs in a data directory's hosts.json (nothing secret: pairing secrets stay in Credential Manager).</summary>
    private static HashSet<string> PairedHostIds(string directory)
    {
        try
        {
            using var hosts = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(directory, "hosts.json")));
            return hosts.RootElement.TryGetProperty("hosts", out var list) && list.ValueKind == JsonValueKind.Array
                ? list.EnumerateArray().Select(h => h.TryGetProperty("pairing", out var pairing) && pairing.TryGetProperty("hostId", out var id)
                    ? id.GetString() : null).OfType<string>().ToHashSet(StringComparer.Ordinal)
                : [];
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException) { return []; }
    }

    /// <summary>Runs Martlet.NodeLinkCheck (built next to this server, in the same configuration) with <paramref name="arguments"/>
    /// and returns its JSON report. A separate process, because the in-process gateway needs the ASP.NET Core runtime and this
    /// server does not.</summary>
    private static Task<object> NodeLinkCheckAsync(CancellationToken cancellation, params string[] arguments) =>
        NodeLinkCheckAsync(TimeSpan.FromMinutes(2), cancellation, arguments);

    /// <summary>Martlet.NodeLinkCheck's executable in this source checkout's build (the same configuration as this server).</summary>
    private static System.Diagnostics.Process? signInLab;

    /// <summary>Starts, reads or stops the live sign-in lab (Martlet.NodeLinkCheck signin-lab) for a disposable data directory.</summary>
    private static async Task<object> SignInLabAsync(JsonElement arguments, CancellationToken cancellation)
    {
        var directory = DataDirectory(arguments);
        var status = Path.Combine(directory, "signin-lab.json");
        switch (OptionalString(arguments, "action"))
        {
            case "start":
            {
                if (signInLab is { HasExited: false }) throw new InvalidOperationException("The sign-in lab is already running; stop it first.");
                if (Environment.GetEnvironmentVariable(Martlet.Credentials.Windows.LabCredentialNative.Variable) is not { Length: > 0 })
                    throw new InvalidOperationException("Run with Invoke-MartletMcp.ps1 -LabCredentials: the lab keeps its pairing secret in a lab folder, never Windows Credential Manager.");
                Directory.CreateDirectory(directory);
                var start = new System.Diagnostics.ProcessStartInfo(NodeLinkCheckProgram())
                {
                    UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
                };
                start.ArgumentList.Add("signin-lab");
                start.ArgumentList.Add(directory);
                var process = System.Diagnostics.Process.Start(start) ?? throw new InvalidOperationException("Couldn't start the sign-in lab.");
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                timeout.CancelAfter(TimeSpan.FromSeconds(90));
                var line = await process.StandardOutput.ReadLineAsync(timeout.Token);
                if (line is null || !line.Contains("\"ready\":true", StringComparison.Ordinal))
                {
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                    throw new InvalidOperationException("The sign-in lab didn't start: " + (line ?? await process.StandardError.ReadToEndAsync(cancellation)));
                }
                signInLab = process;
                return JsonSerializer.Deserialize<JsonElement>(line);
            }
            case "status":
                return File.Exists(status) ? JsonSerializer.Deserialize<JsonElement>(await File.ReadAllBytesAsync(status, cancellation))
                    : new { ready = false, running = signInLab is { HasExited: false } };
            case "stop":
                if (signInLab is { HasExited: false } running)
                {
                    running.StandardInput.Close();
                    if (!running.WaitForExit(10_000)) running.Kill(entireProcessTree: true);
                }
                signInLab = null;
                return new { stopped = true };
            default:
                throw new InvalidOperationException("action is start, status or stop.");
        }
    }

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
        var failedStartCheck = await DockerDesktopStatus.ReadPreconditionAsync(cancellation);
        var state = await WindowsVirtualization.ProbeAsync(cancellation) with { DockerPrecondition = failedStartCheck };
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
            blocked = state.Blocked,
            firmwareOff = state.FirmwareOff,
            needsWindowsChanges = state.NeedsChanges,
            restartPending = state.RestartPending,
            restartRequired = state.RestartRequired,
            problems = state.Problems(),
            recovery = state.Recovery,
            probeIssues = state.ProbeIssues,
            firmware = state.Firmware,
            hypervisor = state.Hypervisor,
            virtualMachinePlatform = state.MachinePlatform.ToString(),
            windowsSubsystemForLinux = state.Subsystem.ToString(),
            wsl = state.Wsl,
            wslStatus = state.WslStatus.ToString(),
            wslStatusExitCode = state.WslStatusExitCode,
            hostComputeService = state.ComputeService.ToString(),
            hostNetworkService = state.NetworkService.ToString(),
            virtualMachine = state.VirtualMachine,
            summary = state.Describe(),
            dockerDesktop = new
            {
                installed = File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    "Docker", "Docker", "Docker Desktop.exe")),
                running = Running("com.docker.backend") || Running("Docker Desktop"),
                engine = await DockerDesktopStatus.ReadAsync(cancellation),
                failedStartCheck,
                windowsCanFixFailedStartCheck = WindowsVirtualization.WindowsCanFix(failedStartCheck),
                failedStartCheckNeedsWindowsChanges = state.DockerNeedsWindows
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

    /// <summary>gpu_priority_status: GPU priority (live turn first) on every host paired in the data directory, through
    /// Martlet.NodeLinkCheck gpu-priority-status, which signs with the pairing secret from Windows Credential Manager and returns
    /// no secret. Nothing is contacted when the data directory pairs no host.</summary>
    private static async Task<object> GpuPriorityStatusAsync(JsonElement arguments, CancellationToken cancellation)
    {
        var directory = DataDirectory(arguments);
        if (!File.Exists(Path.Combine(directory, "hosts.json")))
            return new { dataDirectory = directory, hosts = Array.Empty<object>(), note = "No host is paired in this data directory (no hosts.json)." };
        return await NodeLinkCheckAsync(TimeSpan.FromMinutes(2), cancellation, ["gpu-priority-status", directory]);
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
            // The warning Companion › Voice and Devices show when this PC's voice engine shares the card with other roles.
            sharedGpu = SharedGpu.Warning("This PC", onWindows: true, SharedGpu.VoiceName(state.Roles), SharedGpu.Neighbours(state.Roles, false)),
            problem = state.Problem
        };
    }

    /// <summary>character_actions: a character model's emotes and motions as Companion › Character › Emotes and motions uses
    /// them (Martlet.Avatar.Hosting's inventory, saved settings and prompts), optionally with a simulated Thinking answer parsed
    /// by the production naming parser. Model-authored names and model-relative file names only; never the model's path.</summary>
    private static async Task<object> CharacterActionsCheckAsync(JsonElement arguments, CancellationToken cancellation)
    {
        var directory = OptionalString(arguments, "dataDirectory") is { } given ? given : null;
        var modelPath = OptionalString(arguments, "modelPath");
        string? renderer = null;
        if (modelPath is null && directory is not null)
        {
            try
            {
                using var avatar = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(directory, "avatar.json")));
                modelPath = avatar.RootElement.TryGetProperty("model_path", out var value) ? value.GetString() : null;
                renderer = avatar.RootElement.TryGetProperty("renderer", out var kind) ? kind.ToString() : null;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException) { }
        }
        if (modelPath is null) throw new ArgumentException("Give modelPath (a .model3.json or .vrm), or a dataDirectory whose avatar.json shows one.");
        var vrm = modelPath.EndsWith(".vrm", StringComparison.OrdinalIgnoreCase) || renderer?.Contains("vrm", StringComparison.OrdinalIgnoreCase) == true;
        var avatarRenderer = vrm ? Martlet.Avatars.AvatarRenderer.Vrm : Martlet.Avatars.AvatarRenderer.Live2D;
        Martlet.Avatar.Hosting.CharacterActionInventory inventory;
        IReadOnlyList<Martlet.Avatar.Hosting.AvatarAsset> assets;
        string path;
        try
        {
            path = Martlet.Avatar.Hosting.BundledLive2D.IsBuiltIn(modelPath) ? Martlet.Avatar.Hosting.BundledLive2D.ModelPath(modelPath) : modelPath;
            assets = await Martlet.Avatar.Hosting.LocalAvatarFiles.ReadModelAsync(avatarRenderer, path, cancellation);
            inventory = Martlet.Avatar.Hosting.CharacterActionInventory.From(avatarRenderer, vrm ? assets[0].Name : Path.GetFileName(path), assets);
        }
        catch (Martlet.Core.Contracts.ContractException error) { throw new InvalidOperationException("The model can't be read: " + error.Message); }
        catch (IOException error) { throw new InvalidOperationException("The model can't be read: " + error.Message); }
        Martlet.Core.Settings.PromptSettings? prompts = null;
        if (directory is not null && File.Exists(Path.Combine(directory, "settings.json")))
            prompts = Martlet.Core.Settings.SettingsJson.Read(File.ReadAllBytes(Path.Combine(directory, "settings.json"))).Prompts;
        var saved = directory is null ? null : Martlet.Avatar.Hosting.CharacterActions.Load(directory, inventory.ModelId);
        var catalog = new Martlet.Avatar.Hosting.CharacterActionCatalog(inventory, Martlet.Avatar.Hosting.CharacterActions.Merge(inventory, saved));
        // Combos to rehearse ("flustered: blush hearts nod | when flattered"), read as the Combos section's boxes are; they replace
        // the saved combos when they could be saved.
        string? combosProblem = null;
        if (OptionalStrings(arguments, "combos") is { } comboLines)
        {
            var written = new List<Martlet.Avatar.Hosting.CharacterCombo>();
            foreach (var line in comboLines)
            {
                var halves = line.Split('|', 2);
                var colon = halves[0].IndexOf(':');
                var ids = Martlet.Avatar.Hosting.CharacterActions.ParseParts(colon < 0 ? "" : halves[0][(colon + 1)..], catalog.Settings.Actions,
                    out var why);
                if (ids is null)
                {
                    combosProblem ??= why;
                    continue;
                }
                written.Add(new()
                {
                    Tag = (colon < 0 ? halves[0] : halves[0][..colon]).Trim().Trim('{', '}').Trim().ToLowerInvariant(), Parts = ids,
                    Use = halves.Length > 1 && halves[1].Trim() is { Length: > 0 } use ? use : null
                });
            }
            var rehearsed = catalog.Settings with { Combos = written.Count == 0 ? null : written };
            combosProblem ??= Martlet.Avatar.Hosting.CharacterActions.Problem(rehearsed);
            if (combosProblem is null) catalog = catalog with { Settings = rehearsed };
        }
        var key = OptionalString(arguments, "engine");
        var engine = key is null or "none" ? null
            : Martlet.Core.Settings.SpeechEngines.ForKey(key) ?? throw new ArgumentException($"Unknown voice engine '{key}'.");
        object Describe(Martlet.Avatar.Hosting.CharacterActionCatalog of)
        {
            var offered = of.Offered(engine).Select(e => e.Source.Id).ToHashSet(StringComparer.Ordinal);
            return of.Entries.Select((e, n) => new
            {
                n, id = e.Source.Id, kind = e.Source.Kind.ToString().ToLowerInvariant(), name = e.Source.Name, detail = e.Source.Detail,
                tag = e.Action.Tag, cue = e.Action.Cue, use = e.Action.Use,
                hint = Martlet.Avatar.Hosting.CharacterActions.Hint(e.Source, e.Action), enabled = e.Action.Enabled,
                offered = offered.Contains(e.Source.Id),
                mode = e.Action.Mode ?? Martlet.Avatar.Hosting.CharacterActions.DefaultMode(e.Source, e.Action.Tag),
                modeSaved = e.Action.Mode is not null, vtsToggle = e.Source.Toggle
            }).ToArray();
        }
        // Lingering emotes the character would be showing ("glasses" or "glasses:12" for 12 minutes), for the note replies get.
        var now = DateTimeOffset.Now;
        var showing = (OptionalStrings(arguments, "showing") ?? []).Select(text =>
        {
            var parts = text.Split(':', 2);
            var minutes = parts.Length > 1 && int.TryParse(parts[1], System.Globalization.CultureInfo.InvariantCulture, out var m) ? m : 0;
            var source = catalog.Entries.FirstOrDefault(e => string.Equals(e.Action.Tag, parts[0].Trim('{', '}'), StringComparison.OrdinalIgnoreCase)).Source ??
                throw new ArgumentException($"No emote has the tag '{parts[0]}'.");
            return new Martlet.Avatar.Hosting.HeldEmote(source, path, now - TimeSpan.FromMinutes(minutes));
        }).ToArray();
        var reply = catalog.Prompt(engine, prompts, showing, now);
        var naming = Martlet.Avatar.Hosting.CharacterActions.NamingPrompt(inventory, prompts);
        object? parsed = null;
        if (OptionalString(arguments, "answer") is { } answer)
        {
            var named = Martlet.Avatar.Hosting.CharacterActions.Parse(answer, inventory, catalog.Settings, DateTimeOffset.Now);
            parsed = named is null ? new { read = false } : new
            {
                read = true, problem = Martlet.Avatar.Hosting.CharacterActions.Problem(named), actions = Describe(catalog with { Settings = named }),
                prompt = (catalog with { Settings = named }).Prompt(engine, prompts)?.Instructions
            };
        }
        var extras = vrm ? null : Martlet.Avatar.Hosting.LocalAvatarFiles.Extras(assets, Path.GetFileName(path));
        var voiceTag = OptionalString(arguments, "voiceTag");
        // Each blush level the model has, faintest first: Martlet's gesture, or for the blush the model's own emote tagged blush
        // when it replaces the gesture.
        var offeredNow = catalog.Offered(engine).Select(e => e.Source.Id).ToHashSet(StringComparer.Ordinal);
        var rows = catalog.Entries.Select((e, n) => (e.Source, e.Action, N: n)).ToArray();
        var blushLevels = Martlet.Avatar.Hosting.CharacterActionInventory.BlushLevels.Select((level, i) =>
        {
            var row = rows.FirstOrDefault(r => r.Source.Id == "gesture:" + level);
            if (row.Source is null) row = rows.FirstOrDefault(r => string.Equals(r.Action.Tag, level, StringComparison.OrdinalIgnoreCase));
            return row.Source is null ? null : new
            {
                level = i + 1, n = row.N, id = row.Source.Id, kind = row.Source.Kind.ToString().ToLowerInvariant(), name = row.Source.Name,
                tag = row.Action.Tag, mode = row.Action.Mode ?? Martlet.Avatar.Hosting.CharacterActions.DefaultMode(row.Source, row.Action.Tag),
                offered = offeredNow.Contains(row.Source.Id)
            };
        }).Where(level => level is not null).ToArray();
        return new
        {
            renderer = avatarRenderer.ToString(), key = inventory.ModelId[..16], files = assets.Count,
            expressions = inventory.Sources.Count(s => s.Kind == Martlet.Avatar.Hosting.CharacterActionKind.Expression),
            motions = inventory.Sources.Count(s => s.Kind == Martlet.Avatar.Hosting.CharacterActionKind.Motion),
            gestures = inventory.Sources.Where(s => s.Kind == Martlet.Avatar.Hosting.CharacterActionKind.Gesture).Select(s => s.Name).ToArray(),
            fromVTubeStudio = extras is null ? null : new
            {
                expressions = extras.Expressions.Select(e => new { e.Name, e.File }), motions = extras.Motions.Select(m => new { m.Group, m.File })
            },
            saved = saved is not null, detectedBy = catalog.Settings.DetectedBy, detectedAt = catalog.Settings.DetectedAt,
            engine = engine?.Key, actions = Describe(catalog), blushLevels,
            replyPrompt = reply?.Instructions, replyTags = reply?.Tags, showingNote = reply?.Showing,
            namingPrompt = naming is { } ask ? new { instructions = ask.Instructions, list = ask.List } : null,
            combos = catalog.Combos.Select((c, n) => new
            {
                n, tag = c.Tag,
                parts = c.Parts.Select(id => catalog.Entries.FirstOrDefault(e => e.Source.Id == id) is { Source: { } source } part
                    ? new
                    {
                        id, kind = source.Kind.ToString().ToLowerInvariant(), name = source.Name, tag = part.Action.Tag, enabled = part.Action.Enabled,
                        mode = part.Action.Mode ?? Martlet.Avatar.Hosting.CharacterActions.DefaultMode(source, part.Action.Tag)
                    }
                    : new { id, kind = "missing", name = id, tag = (string?)null, enabled = false, mode = "" }).ToArray(),
                use = c.Use, hint = catalog.Hint(c), enabled = c.Enabled, lingers = catalog.Lingers(c),
                offered = reply?.Tags.Contains("{" + c.Tag + "}") == true
            }).ToArray(),
            combosProblem,
            voiceTag, setsOff = voiceTag is null ? null
                : catalog.For(voiceTag).Select(s => new
                {
                    kind = s.Kind.ToString().ToLowerInvariant(), name = s.Name, holds = voiceTag.StartsWith('{') && catalog.Lingers(s)
                }).ToArray(),
            turnsOff = voiceTag is null ? null : catalog.Off(voiceTag).Select(off => new { kind = off.Kind.ToString().ToLowerInvariant(), name = off.Name })
                .ToArray(),
            combo = voiceTag is not null && catalog.Combo(voiceTag) is { } voiceCombo ? voiceCombo.Tag : null,
            parsed
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
        var settings = SavedSettings(arguments);
        var prompts = settings?.Prompts;
        var characterTags = OptionalStrings(arguments, "characterTags");
        var breaks = SpeechBreaksFrom(arguments, settings, out var persona);
        var preview = Martlet.Conversation.SpeechTextPreview.For(text, engine, characterTags, breaks);
        return new
        {
            engine = engine?.Key, name = engine?.Name, supportsTags = engine?.SupportsTags ?? false,
            tags = (engine?.Tags ?? []).Select(tag => tag.Text).ToArray(),
            sounds = (engine?.Tags ?? []).Where(tag => tag.Kind == Martlet.Core.Settings.VoiceTagKind.Sound).Select(tag => tag.Text).ToArray(),
            tones = (engine?.Tags ?? []).Where(tag => tag.Kind == Martlet.Core.Settings.VoiceTagKind.Emotion).Select(tag => tag.Text).ToArray(),
            cues = (engine?.Tags ?? []).Select(tag => new { tag = tag.Text, cue = tag.Cue }).ToArray(),
            synonyms = (engine?.Tags ?? []).Where(tag => Martlet.Core.Settings.VoiceTags.Synonyms.ContainsKey(tag.Cue))
                .Select(tag => new { tag = tag.Text, words = Martlet.Core.Settings.VoiceTags.Synonyms[tag.Cue] }).ToArray(),
            prompt = Martlet.Core.Settings.VoiceTags.Instructions(engine, prompts),
            persona = persona?.Name, breaks = Breaks(breaks),
            spoken = preview.Spoken, suppressedPieces = preview.SuppressedPieces, shown = preview.Shown,
            characterCues = preview.Cues.Select(c => new { piece = c.Piece, tag = c.Tag, offset = c.Offset }).ToArray(),
            acted = preview.Acted.Select(tag => new { tag = tag.Tag, kind = tag.Kind.ToString(), name = tag.Name, written = tag.Written }).ToArray(),
            note = preview.Note
        };
    }

    /// <summary>An optional array of strings (at most 128), or null when it isn't given.</summary>
    private static string[]? OptionalStrings(JsonElement arguments, string name) =>
        arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty(name, out var listed) && listed.ValueKind == JsonValueKind.Array
            ? listed.EnumerateArray().Where(t => t.ValueKind == JsonValueKind.String).Select(t => t.GetString()!).Take(128).ToArray()
            : null;

    /// <summary>A data directory's settings.json, or null without a dataDirectory or before anything was saved there.</summary>
    private static Martlet.Core.Settings.AppSettings? SavedSettings(JsonElement arguments) =>
        OptionalString(arguments, "dataDirectory") is { } directory && File.Exists(Path.Combine(directory, "settings.json"))
            ? Martlet.Core.Settings.SettingsJson.Read(File.ReadAllBytes(Path.Combine(directory, "settings.json"))) : null;

    /// <summary>The speech breaks a check uses (Personality › Where the voice pauses): the saved persona named "persona", else the
    /// one Martlet uses, else the defaults; then any stop set in "breaks" on top.</summary>
    private static Martlet.Core.Settings.SpeechBreaks SpeechBreaksFrom(JsonElement arguments,
        Martlet.Core.Settings.AppSettings? settings, out Martlet.Core.Settings.PersonaProfile? persona)
    {
        persona = null;
        var named = OptionalString(arguments, "persona");
        if (settings?.Companion is { } companion)
            persona = named is null ? companion.ActivePersona
                : companion.Personas.FirstOrDefault(p => string.Equals(p.Name, named, StringComparison.OrdinalIgnoreCase))
                    ?? throw new ArgumentException($"No persona named '{named}' is saved there.");
        else if (named is not null)
            throw new ArgumentException("'persona' needs a dataDirectory with saved settings.");
        var breaks = persona?.SpokenBreaks ?? Martlet.Core.Settings.SpeechBreaks.Default;
        if (arguments.ValueKind != JsonValueKind.Object || !arguments.TryGetProperty("breaks", out var set) ||
            set.ValueKind != JsonValueKind.Object)
            return breaks;
        bool Stop(string stop, bool current) =>
            set.TryGetProperty(stop, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : current;
        breaks = breaks with
        {
            Periods = Stop("periods", breaks.Periods),
            QuestionMarks = Stop("questionMarks", breaks.QuestionMarks), ExclamationMarks = Stop("exclamationMarks", breaks.ExclamationMarks),
            ShortEndingWords = set.TryGetProperty("shortEndingWords", out var words) && words.TryGetInt32(out var count) ? count : breaks.ShortEndingWords
        };
        try { breaks.Validate(); }
        catch (Martlet.Core.Contracts.ContractException error) { throw new ArgumentException(error.Message); }
        return breaks;
    }

    private static object BreaksSchema() => new
    {
        type = "object",
        properties = new
        {
            periods = new { type = "boolean" }, questionMarks = new { type = "boolean" },
            exclamationMarks = new { type = "boolean" },
            shortEndingWords = new { type = "integer", minimum = 0, maximum = Martlet.Core.Settings.SpeechBreaks.MaximumShortEndingWords }
        }
    };

    /// <summary>Speech breaks as MCP reports them (Personality › Where the voice pauses).</summary>
    internal static object Breaks(Martlet.Core.Settings.SpeechBreaks breaks) => new
    {
        periods = breaks.Periods, questionMarks = breaks.QuestionMarks,
        exclamationMarks = breaks.ExclamationMarks, shortEndingWords = breaks.ShortEndingWords, isDefault = breaks.IsDefault
    };
    /// <summary>The character profiles saved in a data directory (Companion › Profiles), by key, with whether each part
    /// resolves here and which one matches what Martlet uses now. Names are the owner's and are never returned.</summary>
    private static object CharacterProfiles(JsonElement arguments)
    {
        var directory = DataDirectory(arguments);
        var settingsPath = Path.Combine(directory, "settings.json");
        var settings = File.Exists(settingsPath) ? Martlet.Core.Settings.SettingsJson.Read(File.ReadAllBytes(settingsPath)) : null;
        var companion = settings?.Companion;
        string? shownPath = null;
        try
        {
            using var avatar = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(directory, "avatar.json")));
            shownPath = avatar.RootElement.TryGetProperty("model_path", out var value) ? value.GetString() : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException) { }
        var library = Martlet.Avatar.Hosting.SharedCharacterModels.Load(directory) ?? Martlet.Core.Characters.CharacterModelLibrary.Empty;
        var modelNow = shownPath is null || Martlet.Avatar.Hosting.BundledLive2D.IsBuiltIn(shownPath)
            ? Martlet.Core.Settings.CharacterProfile.BuiltInModel
            : Martlet.Avatar.Hosting.SharedCharacterModels.ForPath(directory, library, shownPath)?.Id;
        Martlet.Core.Voices.SpeakingVoiceLibrary voices;
        try { voices = Martlet.Core.Voices.SpeakingVoiceLibrary.Parse(File.ReadAllBytes(Path.Combine(directory, "speaking-voices.json"))); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Martlet.Core.Contracts.ContractException)
        {
            voices = Martlet.Core.Voices.SpeakingVoiceLibrary.Empty.Seed(Martlet.F5.F5SharedVoices.Starters);
        }
        var voiceNow = settings?.Setup?.Routes.FirstOrDefault(r => r.Role == Martlet.Core.Settings.SetupRole.Tts) is
            { RouteType: Martlet.Core.Settings.SetupRouteType.GatewayF5, Reference: { } reference }
            ? reference.ReferenceRevision : voices.ChosenVoice?.Id;
        var current = companion?.CurrentCharacter(modelNow, voiceNow);
        var profiles = companion?.CharacterList ?? [];
        return new
        {
            state = settings is null ? "no-settings" : profiles.Count == 0 ? "none" : "loaded",
            count = profiles.Count,
            lastUsed = profiles.FirstOrDefault(c => c.Id == companion!.ActiveCharacterId)?.Key,
            current = current?.Key,
            look = modelNow is null ? "model-file-outside-list" : modelNow == Martlet.Core.Settings.CharacterProfile.BuiltInModel ? "builtin"
                : "shared:" + Martlet.Avatar.Hosting.SharedCharacterModels.Key(modelNow),
            voiceChosen = voiceNow is not null,
            profiles = profiles.Select(p => new
            {
                key = p.Key,
                personaSaved = companion!.Personas.Any(persona => persona.Id == p.PersonaId),
                personaActive = p.PersonaId == companion.ActivePersonaId,
                look = p.ModelId switch
                {
                    null => "keep",
                    Martlet.Core.Settings.CharacterProfile.BuiltInModel => "builtin",
                    var id => library.Live.FirstOrDefault(m => m.Id == id) is { } model
                        ? Martlet.Avatar.Hosting.SharedCharacterModels.IsComplete(directory, model) ? "ready" : "copying"
                        : "missing"
                },
                voice = p.VoiceId is null ? "keep" : voices.Find(p.VoiceId) is { Removed: false } ? "listed" : "missing",
                inUse = p.Id == current?.Id
            }).ToArray()
        };
    }

    /// <summary>The shared character models as the desktop keeps them in a data directory (Martlet.Avatar.Hosting's
    /// SharedCharacterModels): keys, renderers, sizes and whether each copy is complete here. Names and paths are the owner's
    /// and are never returned.</summary>
    private static object CharacterModels(JsonElement arguments)
    {
        var directory = DataDirectory(arguments);
        string? shownPath = null;
        try
        {
            using var avatar = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(directory, "avatar.json")));
            shownPath = avatar.RootElement.TryGetProperty("model_path", out var value) ? value.GetString() : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException) { }
        var path = Path.Combine(directory, Martlet.Avatar.Hosting.SharedCharacterModels.LibraryFile);
        Martlet.Core.Characters.CharacterModelLibrary? library = null;
        string state;
        if (!File.Exists(path)) state = "none";
        else
        {
            library = Martlet.Avatar.Hosting.SharedCharacterModels.Load(directory);
            state = library is null ? "unreadable" : "loaded";
        }
        library ??= Martlet.Core.Characters.CharacterModelLibrary.Empty;
        var shown = Martlet.Avatar.Hosting.SharedCharacterModels.ForPath(directory, library, shownPath);
        var shownKey = Martlet.Avatar.Hosting.SharedCharacterModels.KeyOfPath(directory, shownPath);
        var incoming = Path.Combine(directory, Martlet.Avatar.Hosting.SharedCharacterModels.IncomingDirectoryName);
        var root = Martlet.Avatar.Hosting.SharedCharacterModels.Root(directory);
        return new
        {
            state,
            live = library.Live.Count,
            tombstones = library.Models.Count(m => m.Removed),
            totalBytes = library.Live.Sum(m => m.Bytes),
            revision = library.Revision,
            models = library.Live.Select(m => new
            {
                key = Martlet.Avatar.Hosting.SharedCharacterModels.Key(m.Id), renderer = m.Renderer, files = m.Files!.Count,
                pieces = m.Files.Sum(f => f.Chunks.Count), bytes = m.Bytes, addedBy = m.AddedBy, addedAt = m.AddedAt, updatedBy = m.UpdatedBy,
                ready = Martlet.Avatar.Hosting.SharedCharacterModels.IsComplete(directory, m), shown = shown?.Id == m.Id
            }),
            copies = Directory.Exists(root) ? Directory.EnumerateDirectories(root).Count() : 0,
            incoming = Directory.Exists(incoming)
                ? Directory.EnumerateDirectories(incoming).Select(d => new { key = Path.GetFileName(d)[..Math.Min(16, Path.GetFileName(d).Length)],
                    pieces = Directory.EnumerateFiles(d).Count() }).ToArray()
                : [],
            showing = shownPath is null ? "built-in (nothing saved)"
                : Martlet.Avatar.Hosting.BundledLive2D.IsBuiltIn(shownPath) ? "built-in"
                : shown is not null ? "shared:" + Martlet.Avatar.Hosting.SharedCharacterModels.Key(shown.Id)
                : shownKey is not null ? "unlisted-copy:" + shownKey
                : "model-file-outside-list"
        };
    }

    /// <summary>voice_recording_check: Add a voice's conversion of one file, through the production
    /// <see cref="Martlet.Audio.Windows.VoiceRecordingImport"/>, then F5's own reference rules on the WAV it would keep.</summary>
    private static async Task<object> VoiceRecordingCheckAsync(string path, CancellationToken cancellation)
    {
        try
        {
            var ready = await Task.Run(() => Martlet.Audio.Windows.VoiceRecordingImport.Prepare(path, cancellation), cancellation);
            // F5's reference rules decide whether the recording can be a voice by itself (one of several may be shorter).
            string? aloneProblem = null;
            try { Martlet.F5.F5ReferenceAudioFormat.Parse(ready.Wave); }
            catch (Martlet.F5.F5Exception failure) { aloneProblem = failure.Failure.ToString(); }
            // How loud the WAV that Play plays is, so a decode is seen to give real sound; never the audio itself.
            var samples = Martlet.Core.Voices.PcmWaveInfo.Samples(ready.Wave, Martlet.Core.Voices.SpeakingVoiceLibrary.MaximumAudioBytes, out _);
            double peak = 0, energy = 0;
            foreach (var sample in samples)
            {
                var value = sample / 32768d;
                peak = Math.Max(peak, Math.Abs(value));
                energy += value * value;
            }
            static double Dbfs(double level) => level <= 0 ? -96 : Math.Round(Math.Max(-96, 20 * Math.Log10(level)), 1);
            return new
            {
                usable = true, sourceFormat = ready.SourceFormat, sourceChannels = ready.SourceChannels, sourceSampleRate = ready.SourceSampleRate,
                converted = ready.Converted, sampleRate = ready.SampleRate, channels = BitConverter.ToInt16(ready.Wave, 22),
                bitsPerSample = BitConverter.ToInt16(ready.Wave, 34), durationMs = ready.DurationMilliseconds, bytes = ready.Wave.Length,
                sha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(ready.Wave)),
                peakDbfs = Dbfs(peak), rmsDbfs = Dbfs(samples.Length == 0 ? 0 : Math.Sqrt(energy / samples.Length)),
                voiceAlone = aloneProblem is null, voiceAloneProblem = aloneProblem,
                engines = Martlet.Core.Settings.SpeechEngines.All
                    .Where(engine => Martlet.Core.Settings.SpeechEngines.ReferenceProblem(engine, ready.DurationMilliseconds) is null)
                    .Select(engine => engine.Key).ToArray(),
                shown = ready.Describe()
            };
        }
        catch (Martlet.Audio.Windows.VoiceRecordingException failure)
        {
            return new { usable = false, problem = failure.Message };
        }
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
                    chosenBy = shared.ChosenVoice is not null ? shared.Chosen!.UpdatedBy : null,
                    // Voices made from several recordings, by the key the Voices page uses (the ID's first 16 hex digits): how many
                    // recordings, each one's length, the joined length, the engines that can clone it and those that learn from each.
                    severalRecordings = live.Where(v => v.Clips is not null).Select(v => new
                    {
                        key = v.Id[..16], recordings = v.Clips!.Count, clipMs = v.ClipMilliseconds, durationMs = v.DurationMilliseconds,
                        sampleRate = v.SampleRate,
                        engines = Martlet.Core.Settings.SpeechEngines.All
                            .Where(engine => Martlet.Core.Settings.SpeechEngines.ReferenceProblem(engine, v.DurationMilliseconds, v.ClipMilliseconds) is null)
                            .Select(engine => engine.Key).ToArray(),
                        learnsFromEach = Martlet.Core.Settings.SpeechEngines.All
                            .Where(engine => Martlet.Core.Settings.SpeechEngines.UsesClips(engine, v.ClipMilliseconds))
                            .Select(engine => engine.Key).ToArray()
                    }).ToArray()
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
            model = engine.DefaultModel, weightsLicence = engine.WeightsLicense, nonCommercial = engine.NonCommercial,
            minimumGpuMemoryGb = engine.MinimumGpuMemoryGb,
            minimumReferenceMs = engine.MinimumReferenceMilliseconds, maximumReferenceMs = engine.MaximumReferenceMilliseconds,
            summary = engine.Summary, languages = engine.Languages, streams = engine.StreamsWhileGenerating, features = engine.Features,
            abilities = Abilities(engine.Abilities, engine.Tags), runsOn = RunsOn(engine.Footprint, engine.RunsOn),
            @default = engine == Martlet.Core.Settings.SpeechEngines.Default,
            supportsTags = engine.SupportsTags, multipleReferences = engine.MultipleReferences,
            tags = engine.Tags.Select(tag => new { text = tag.Text, kind = tag.Kind.ToString(), usage = tag.Usage }).ToArray()
        }).ToArray();
        // The other ways Martlet speaks, with the same rundown Companion › Voice shows (VoiceEngineAbilities-<key>,
        // VoiceEngineRunsOn-<key>).
        var catalog = Martlet.Core.Planning.FootprintCatalog.Default;
        var windowsVoice = catalog.Find(Martlet.Core.Planning.FootprintCatalog.WindowsVoiceId);
        var openAiVoice = catalog.Find(Martlet.Core.Planning.FootprintCatalog.OpenAiVoiceId);
        var otherVoices = new[]
        {
            new { key = "windows", name = "Windows voice", abilities = Abilities(Martlet.Core.Settings.VoiceAbilities.WindowsVoice, []),
                runsOn = RunsOn(windowsVoice, windowsVoice?.WhereItRuns ?? "") },
            new { key = "openai", name = "OpenAI voice", abilities = Abilities(Martlet.Core.Settings.VoiceAbilities.OpenAiVoice, []),
                runsOn = RunsOn(openAiVoice, openAiVoice?.WhereItRuns ?? "") }
        };
        return new
        {
            @default = fallback.Key, defaultName = fallback.Name, defaultFemale = fallback.Female, defaultCute = fallback.Cute,
            cute = Martlet.F5.F5BundledVoices.All.Where(voice => voice.Cute).Select(voice => voice.Key).ToArray(), starters, library, list, speaking,
            engines, otherVoices, chosenEngine = Martlet.Core.Settings.SpeechEngines.ForKey(chosen)?.Key ?? Martlet.Core.Settings.SpeechEngines.Default.Key,
            // Chatterbox Original's General and Expressive style as Companion › Voice saved it (Resemble's suggestions until then).
            chatterboxStyle = ChatterboxStyleReport(directory)
        };

        static object ChatterboxStyleReport(string directory)
        {
            var style = Martlet.Core.Settings.ChatterboxStyle.Load(directory);
            return new
            {
                saved = File.Exists(Path.Combine(directory, Martlet.Core.Settings.ChatterboxStyle.FileName)),
                generalExaggeration = style.GeneralExaggeration, generalCfgWeight = style.GeneralCfgWeight,
                expressiveExaggeration = style.ExpressiveExaggeration, expressiveCfgWeight = style.ExpressiveCfgWeight,
                summary = style.Describe()
            };
        }

        // What a voice can do: cloning, sounds and emotions (yes, partly or no), the tags that do it and the rundown's words.
        static object Abilities(Martlet.Core.Settings.VoiceAbilities abilities, IReadOnlyList<Martlet.Core.Settings.VoiceTag> tags) => new
        {
            cloning = abilities.Cloning, sounds = abilities.Sounds, emotions = abilities.Emotions.ToString(),
            items = abilities.Items.Select(item => new
            {
                name = item.Name, level = item.Level.ToString(), note = item.Note, help = item.Help,
                tags = abilities.TagsFor(item, tags).Select(tag => tag.Text).ToArray()
            }).ToArray(),
            summary = abilities.Describe()
        };

        // Where a voice runs: on a GPU (with its graphics memory in GB: typical, most and the smallest card), the CPU or
        // online, as the footprint catalog says, and the line Companion › Voice shows.
        static object RunsOn(Martlet.Core.Planning.ComponentOption? option, string text) => new
        {
            on = option is null ? "gpu" : !option.IsLocal ? "online" : option.UsesGpu ? "gpu" : "cpu",
            vramGb = option?.UsesGpu == true ? option.Usual.VramGb : 0, peakVramGb = option?.UsesGpu == true ? option.GpuGb : 0,
            minimumGpuGb = option?.MinGpuGb ?? 0, evidence = option?.Evidence.ToString(), text
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

    /// <summary>The settings this PC shares with the owner's other computers (shared-settings.json, the name
    /// Martlet.Core.Sync.SharedSettingsState uses). Personal text (personality, prompts, lorebooks) and keys are never returned:
    /// only who changed each setting and when, its size, whether it uses a key, and for jobs the provider and model.</summary>
    private static object SettingsSyncStatus(JsonElement arguments)
    {
        var directory = DataDirectory(arguments);
        string? choice;
        try { choice = File.ReadAllText(Path.Combine(directory, "cluster-sync.txt")).Trim(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { choice = null; }
        var sync = choice switch { "off" => "off", null => "on (default)", _ => "on" };
        if (!File.Exists(Path.Combine(directory, Martlet.Core.Sync.SharedSettingsState.FileName))) return new { sync, state = "none" };
        var (document, observed) = Martlet.Core.Sync.SharedSettingsState.Load(directory);
        object? Route(Martlet.Core.Sync.SharedSetting setting)
        {
            try
            {
                if (setting.Key is "thinking" or "listening" or "speaking")
                {
                    var route = Martlet.Core.Sync.SharedRoute.Parse(setting.Value);
                    return new { type = route.Type, origin = route.Origin, model = route.Model, voice = route.Voice };
                }
                if (setting.Key == "thinking-fallback" && setting.Value != "null")
                {
                    using var parsed = JsonDocument.Parse(setting.Value);
                    return new { origin = parsed.RootElement.GetProperty("origin").GetString(), model = parsed.RootElement.GetProperty("model").GetString() };
                }
                if (setting.Key is "memory" or "appearance" or "talk" or "speech-display" or "voice-recognition" or "smart-home" or "updates" or
                    "model-abilities" or "work-sharing" || Martlet.Core.Sync.SharedSettings.IsDeviceKey(setting.Key))
                {
                    using var parsed = JsonDocument.Parse(setting.Value);
                    return parsed.RootElement.Clone();
                }
            }
            catch (Exception error) when (error is JsonException or Martlet.Core.Contracts.ContractException or KeyNotFoundException or InvalidOperationException) { }
            return null;
        }
        return new
        {
            sync, state = "loaded", revision = document.Revision, count = document.Settings.Count,
            settings = document.Settings.Select(s => new
            {
                key = s.Key, updatedBy = s.UpdatedBy, updatedAt = s.UpdatedAt, revision = s.Revision, usesKey = s.SecretSha256 is not null,
                characters = s.Value.Length, off = s.Value == "null",
                here = !observed.TryGetValue(s.Key, out var seen) ? "unknown"
                    : seen == Martlet.Core.Sync.SharedSettings.ContentDigest(s.Value, s.SecretSha256) ? "same" : "different",
                value = Route(s)
            }).ToArray()
        };
    }

    /// <summary>One memory on every computer as this PC keeps it (memory-sync.json, the name Martlet.Core.Sync.MemorySyncState
    /// uses): whether sync is on, which store it last synced, when, how many facts it had then and who wrote them, and the
    /// forgotten facts every computer agreed on. Never a fact: the file holds IDs, revisions, digests and device IDs only.</summary>
    private static object MemorySyncStatus(JsonElement arguments)
    {
        var directory = DataDirectory(arguments);
        string? choice;
        try { choice = File.ReadAllText(Path.Combine(directory, "cluster-sync.txt")).Trim(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { choice = null; }
        var sync = choice switch { "off" => "off", null => "on (default)", _ => "on" };
        if (!File.Exists(Path.Combine(directory, Martlet.Core.Sync.MemorySyncState.FileName))) return new { sync, state = "none" };
        var state = Martlet.Core.Sync.MemorySyncState.Load(directory);
        return new
        {
            sync, state = state.StoreId == Guid.Empty ? "unreadable" : "loaded", syncedAt = state.SyncedAt, facts = state.Observed.Count,
            byComputer = state.Observed.Values.GroupBy(s => s.By, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => new { device = g.Key, facts = g.Count() }).ToArray(),
            forgotten = state.Forgotten.Count
        };
    }

    /// <summary>What Martlet remembers and whose, from a data directory: the memory setting in settings.json, then the store's
    /// authoritative file (".martlet-memory.v1.json", the name Martlet.Memory's MemoryStore uses) read as JSON without opening or
    /// locking the store, and voices.json for the voices facts belong to. Counts and voice tags only: never a fact's text, a
    /// name, a voice ID or a path.</summary>
    private static async Task<object> MemoryStatusAsync(JsonElement arguments, CancellationToken cancellation)
    {
        var directory = DataDirectory(arguments);
        var loaded = await new Martlet.Core.Settings.SettingsStore(directory).LoadAsync(cancellation);
        var settings = loaded.Settings?.Memory;
        var memory = loaded.State switch
        {
            Martlet.Core.Settings.SettingsLoadState.FirstRun => "not set up",
            Martlet.Core.Settings.SettingsLoadState.Loaded when settings is null => "on (default)",
            Martlet.Core.Settings.SettingsLoadState.Loaded => settings!.Enabled ? "on" : "off",
            _ => "unreadable"
        };
        var storage = settings?.StoragePolicy == Martlet.Core.Settings.MemoryStoragePolicy.CustomLocalDirectory ? "custom folder" : "Martlet folder";
        string folder;
        try { folder = (settings ?? Martlet.Core.Settings.MemorySettings.Create()).ResolveDirectory(directory); }
        catch (Martlet.Core.Contracts.ContractException) { return new { memory, storage, state = "unreadable" }; }

        var roster = Martlet.Core.Speakers.VoiceRoster.Empty;
        string voiceList;
        var voicesPath = Path.Combine(directory, "voices.json");
        try
        {
            if (File.Exists(voicesPath))
            {
                roster = Martlet.Core.Speakers.VoiceRoster.Parse(File.ReadAllBytes(voicesPath));
                voiceList = "loaded";
            }
            else voiceList = "none";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Martlet.Core.Contracts.ContractException)
        {
            voiceList = "unreadable";
        }

        var path = Path.Combine(folder, ".martlet-memory.v1.json");
        if (!File.Exists(path)) return new { memory, storage, state = "none", voiceList };
        List<(string? Voice, string? Source, string? Retention)> facts = [];
        try
        {
            byte[] bytes;
            await using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                if (stream.Length > 8 * 1024 * 1024) return new { memory, storage, state = "unreadable", voiceList };
                bytes = new byte[stream.Length];
                await stream.ReadExactlyAsync(bytes, cancellation);
            }
            using var document = JsonDocument.Parse(bytes);
            foreach (var fact in document.RootElement.GetProperty("facts").EnumerateArray())
            {
                string? Text(params string[] names)
                {
                    var element = fact;
                    foreach (var name in names)
                        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out element)) return null;
                    return element.ValueKind == JsonValueKind.String ? element.GetString() : null;
                }
                facts.Add((Text("voice_id"), Text("created_from", "source_kind"), Text("retention", "kind")));
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or
            InvalidOperationException)
        {
            return new { memory, storage, state = "unreadable", voiceList };
        }
        var owned = facts.Where(f => f.Voice is not null).Select(f => roster.Resolve(f.Voice!)).ToArray();
        return new
        {
            memory, storage, state = "loaded", voiceList,
            facts = facts.Count,
            typed = facts.Count(f => f.Source is "user_entry" or "user_reviewed_import"),
            fromConversation = facts.Count(f => f.Source == "conversation"),
            expiring = facts.Count(f => f.Retention == "expires_at"),
            whose = new
            {
                everyone = facts.Count(f => f.Voice is null),
                voices = owned.OfType<Martlet.Core.Speakers.KnownVoice>().GroupBy(v => v.Id, StringComparer.Ordinal)
                    .OrderBy(g => g.First().Number)
                    .Select(g => new { voice = g.First().Tag, named = g.First().Named, owner = g.First().Owner, facts = g.Count() }).ToArray(),
                forgottenVoices = owned.Count(v => v is null)
            }
        };
    }

    /// <summary>The Martlet network as the desktop keeps it in a data directory (network.json and network\device_ecdsa, the
    /// names Martlet.Desktop's NetworkIdentity uses). No keys, signatures or addresses are returned.</summary>
    private static object NetworkStatus(JsonElement arguments)
    {
        var directory = DataDirectory(arguments);
        var key = File.Exists(Path.Combine(directory, "network", "device_ecdsa"));
        var path = Path.Combine(directory, Martlet.Avatar.Audio2Face.Remote.NetworkLocalState.FileName);
        var pairedHosts = PairedHostsSummary(directory);
        if (!File.Exists(path)) return new { state = "none", key, pairedHosts };
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
            hosts = roster?.Members.Where(m => m.IsHost).Select(m => new { id = m.Id, name = m.Name, removed = m.Removed, updatedBy = m.UpdatedBy, changedAt = m.ChangedAt, outsideAddresses = m.Addresses?.Count ?? 0 }).ToArray(),
            adopt = local.Adopt,
            ignored = local.Ignored,
            removedFrom = local.RemovedFrom,
            pairedHosts
        };
    }

    /// <summary>hosts.json in short: each paired host's ID and how many outside addresses are kept with its pairing.</summary>
    private static object? PairedHostsSummary(string directory)
    {
        try
        {
            var path = Path.Combine(directory, "hosts.json");
            if (!File.Exists(path)) return Array.Empty<object>();
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            return document.RootElement.GetProperty("hosts").EnumerateArray().Select(h => new
            {
                hostId = h.GetProperty("pairing").GetProperty("hostId").GetString(),
                outsideAddresses = h.TryGetProperty("outsideAddresses", out var outside) && outside.ValueKind == JsonValueKind.Array
                    ? outside.GetArrayLength() : 0
            }).ToArray();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return "unreadable";
        }
    }

    /// <summary>Probes each roster host's home and outside addresses (pinned TLS, GET /health/live) when contactHosts is true.</summary>
    private static async Task<object> OutsideReachabilityAsync(JsonElement arguments, CancellationToken cancellation)
    {
        var directory = DataDirectory(arguments);
        var contact = OptionalBool(arguments, "contactHosts") == true;
        var path = Path.Combine(directory, Martlet.Avatar.Audio2Face.Remote.NetworkLocalState.FileName);
        Martlet.Core.Network.NetworkRoster? roster = null;
        try { if (File.Exists(path)) roster = Martlet.Avatar.Audio2Face.Remote.NetworkLocalState.Parse(File.ReadAllBytes(path)).Roster; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Martlet.Core.Contracts.ContractException)
        {
            return new { state = "unreadable" };
        }
        if (roster is null) return new { state = "none", hosts = Array.Empty<object>() };
        var hosts = new List<object>();
        foreach (var host in roster.ActiveHosts.Where(h => h.Origin is not null && h.Spki is not null))
        {
            var outside = host.Addresses ?? [];
            if (!contact)
            {
                hosts.Add(new { id = host.Id, outsideAddresses = outside.Count, checkedNow = false });
                continue;
            }
            var timeout = TimeSpan.FromSeconds(4);
            var probes = await Task.WhenAll(new string?[] { null }.Concat(outside).Select(address =>
                Martlet.Avatar.Audio2Face.Remote.HostRoutes.ProbeAsync(host.Origin!, host.Spki!, address, timeout, cancellation)));
            var home = probes[0];
            var outsideResults = probes.Skip(1).Select((p, i) => new { address = i + 1, reachable = p.Reachable, ms = p.Milliseconds, problem = p.Problem }).ToArray();
            var use = home.Reachable ? "home" : outsideResults.FirstOrDefault(o => o.reachable) is { } first ? $"outside {first.address}" : "none";
            hosts.Add(new
            {
                id = host.Id, outsideAddresses = outside.Count, checkedNow = true,
                home = new { reachable = home.Reachable, ms = home.Milliseconds, problem = home.Problem },
                outside = outsideResults, wouldUse = use
            });
        }
        return new { state = "member", contacted = contact, hosts };
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
        var generation = loaded.Settings?.Generation;
        var state = loaded.State switch
        {
            Martlet.Core.Settings.SettingsLoadState.Loaded => "loaded",
            Martlet.Core.Settings.SettingsLoadState.FirstRun => "none",
            _ => "unreadable"
        };
        string Of(string prompt) => prompts?.Overrides.TryGetValue(prompt, out var text) != true ? "builtin"
            : string.IsNullOrWhiteSpace(text) ? "empty" : "edited";
        var list = Martlet.Core.Settings.PromptCatalog.All.Select(p =>
        {
            var text = Martlet.Core.Settings.PromptSettings.Text(prompts, p.Id);
            return new
            {
                id = p.Id, group = p.Group, title = p.Title, placeholders = p.Placeholders, state = Of(p.Id),
                characters = text.Length,
                tokens = string.IsNullOrWhiteSpace(text) ? 0 : Martlet.Providers.BoundedTextInput.TextTokens(text)
            };
        }).ToArray();
        return new
        {
            state, problem = loaded.Error?.Summary, total = list.Length,
            edited = list.Count(p => p.state == "edited"), emptied = list.Count(p => p.state == "empty"),
            tokens = list.Sum(p => p.tokens), prompts = list,
            prompt = id is null ? null : new { id, state = Of(id), text = Martlet.Core.Settings.PromptSettings.Text(prompts, id) },
            // Companion › Replies › Short first sentence (on by default) and what closes a reply's instructions with it, exactly
            // as the desktop sends it (the production PromptSettings.ReplyClosing): for a spoken reply the short first sentence
            // prompt, then reply length; for a reply that isn't spoken, reply length alone. The same every time, so caches keep it.
            shortFirstSentence = new
            {
                on = Martlet.Core.Settings.GenerationSettings.StartsShort(generation),
                chosen = generation?.ShortFirstSentence is not null,
                prompt = Of(Martlet.Core.Settings.PromptCatalog.ShortFirstSentence),
                spokenClosing = Martlet.Core.Settings.PromptSettings.ReplyClosing(prompts, generation, true, Martlet.Conversation.StayQuiet.Marker),
                unspokenClosing = Martlet.Core.Settings.PromptSettings.ReplyClosing(prompts, generation, false, Martlet.Conversation.StayQuiet.Marker)
            }
        };
    }

    /// <summary>Companion › Personality and Character as saved in a data directory: settings.json's personas, avatar.json (read as
    /// JSON; the field names match Martlet.Avatar.Hosting's AvatarProfile) and lorebooks.json. Persona instructions, model paths
    /// and lorebook text are personal and never returned.</summary>
    private static async Task<object> CharacterStatusAsync(JsonElement arguments, CancellationToken cancellation)
    {
        var directory = DataDirectory(arguments);
        var loaded = await new Martlet.Core.Settings.SettingsStore(directory).LoadAsync(cancellation);
        var companion = loaded.Settings?.Companion;
        object personality = new
        {
            state = loaded.State switch
            {
                Martlet.Core.Settings.SettingsLoadState.Loaded => "loaded",
                Martlet.Core.Settings.SettingsLoadState.FirstRun => "none",
                _ => "unreadable"
            },
            problem = loaded.Error?.Summary,
            active = companion?.Personas.FirstOrDefault(p => p.Id == companion.ActivePersonaId)?.Name,
            personas = companion?.Personas.Select(p => new
            {
                name = p.Name, active = p.Id == companion.ActivePersonaId, instructionCharacters = p.Text.Length,
                speechBreaks = Breaks(p.SpokenBreaks)
            }).ToArray() ?? []
        };

        object character;
        var avatar = Path.Combine(directory, "avatar.json");
        if (!File.Exists(avatar)) character = new { state = "none" };
        else
        {
            try
            {
                using var document = JsonDocument.Parse(await File.ReadAllBytesAsync(avatar, cancellation));
                var root = document.RootElement;
                string? Text(JsonElement parent, string name) =>
                    parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
                var model = Text(root, "model_path") ?? "";
                const string builtIn = "builtin:";
                character = new
                {
                    state = "loaded",
                    model = model.StartsWith(builtIn, StringComparison.Ordinal) ? "built-in" : "own model",
                    builtInCharacter = model.StartsWith(builtIn, StringComparison.Ordinal) ? model[builtIn.Length..] : null,
                    ownModelType = model.StartsWith(builtIn, StringComparison.Ordinal) ? null
                        : model.EndsWith(".model3.json", StringComparison.OrdinalIgnoreCase) ? ".model3.json" : Path.GetExtension(model).ToLowerInvariant(),
                    renderer = Text(root, "renderer"),
                    lipSync = Text(root, "lip_sync") ?? "auto",
                    autoShow = root.TryGetProperty("auto_show", out var show) && show.ValueKind == JsonValueKind.True,
                    lipSyncHost = root.TryGetProperty("remote_host", out var host) && host.ValueKind == JsonValueKind.Object ? Text(host, "host_id") : null
                };
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
            {
                character = new { state = "unreadable", problem = error.GetType().Name };
            }
        }

        var lore = await new Martlet.Core.Lorebooks.LorebookStore(directory).LoadAsync(cancellation);
        object lorebooks = new
        {
            state = lore.Loaded ? File.Exists(Path.Combine(directory, Martlet.Core.Lorebooks.LorebookStore.FileName)) ? "loaded" : "none" : "unreadable",
            books = lore.Library.Books.Count,
            on = lore.Library.Books.Count(book => book.Activation != Martlet.Core.Lorebooks.LorebookActivation.Off),
            entries = lore.Library.Books.Sum(book => book.Entries.Count)
        };
        return new { personality, character, placement = CharacterPlacement(directory), clickThrough = CharacterClickThrough(directory),
            voice = CharacterVoice(directory), lorebooks };
    }

    /// <summary>character-click-through.json in a data directory (Martlet.Desktop's CharacterClickThroughStore): whether clicks
    /// pass through the character on this PC. No file means it catches clicks.</summary>
    private static object CharacterClickThrough(string directory)
    {
        var path = Path.Combine(directory, "character-click-through.json");
        if (!File.Exists(path)) return new { state = "none", on = false };
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            if (document.RootElement.ValueKind != JsonValueKind.Object) return new { state = "unreadable", on = false, problem = "NotAnObject" };
            return new
            {
                state = "loaded",
                on = document.RootElement.TryGetProperty("ClickThrough", out var on) && on.ValueKind == JsonValueKind.True
            };
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return new { state = "unreadable", on = false, problem = error.GetType().Name };
        }
    }

    /// <summary>Whether Martlet's voice is muted and how loud it is, from talk-preferences.json in a data directory
    /// (Martlet.Desktop's TalkPreferences): SpeakReplies (Companion › Voice's Speak Martlet's replies aloud) is on unless saved
    /// off, and Mute voice / Unmute voice on the character's right-click menu change the same choice; VoiceVolume (Companion ›
    /// Voice's Voice volume, 0 to 1) is full unless saved lower.</summary>
    private static object CharacterVoice(string directory)
    {
        var path = Path.Combine(directory, "talk-preferences.json");
        if (!File.Exists(path)) return new { state = "none", speakReplies = true, muted = false, volume = 1.0 };
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return new { state = "unreadable", speakReplies = true, muted = false, volume = 1.0, problem = "NotAnObject" };
            var speak = !(document.RootElement.TryGetProperty("SpeakReplies", out var value) && value.ValueKind == JsonValueKind.False);
            var volume = document.RootElement.TryGetProperty("VoiceVolume", out var saved) && saved.ValueKind == JsonValueKind.Number &&
                saved.TryGetDouble(out var number) && double.IsFinite(number) ? Math.Clamp(number, 0, 1) : 1.0;
            return new { state = "loaded", speakReplies = speak, muted = !speak, volume };
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return new { state = "unreadable", speakReplies = true, muted = false, volume = 1.0, problem = error.GetType().Name };
        }
    }

    /// <summary>character-placement.json in a data directory (Martlet.Desktop's CharacterPlacementStore): where the character
    /// was last left on this PC (device-independent pixels), on which monitor and whether it is locked there. No file means it
    /// shows at its default spot.</summary>
    private static object CharacterPlacement(string directory)
    {
        var path = Path.Combine(directory, "character-placement.json");
        if (!File.Exists(path)) return new { state = "none", locked = false };
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return new { state = "unreadable", locked = false, problem = "NotAnObject" };
            double? Number(string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number &&
                value.TryGetDouble(out var number) ? number : null;
            return new
            {
                state = "loaded", locked = root.TryGetProperty("Locked", out var locked) && locked.ValueKind == JsonValueKind.True,
                left = Number("Left"), top = Number("Top"), width = Number("Width"), height = Number("Height"),
                screen = root.TryGetProperty("Screen", out var screen) && screen.ValueKind == JsonValueKind.String ? screen.GetString() : null,
                screenLeft = Number("ScreenLeft"), screenTop = Number("ScreenTop")
            };
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return new { state = "unreadable", locked = false, problem = error.GetType().Name };
        }
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

    /// <summary>messaging.json in a data directory (the file name and fields match Martlet.Desktop's MessagingPreferences). The
    /// bot token lives in Windows Credential Manager and is never read here; paired chats are only counted.</summary>
    private static object MessagingStatus(JsonElement arguments)
    {
        var path = Path.Combine(DataDirectory(arguments), "messaging.json");
        object NoTelegram() => new { connected = false, enabled = false, chats = 0 };
        object NoWhatsApp() => new { connected = false, enabled = false, chats = 0 };
        if (!File.Exists(path)) return new { state = "none", telegram = NoTelegram(), whatsApp = NoWhatsApp() };
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            static string Text(JsonElement section, string name) =>
                section.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
            static bool Flag(JsonElement section, string name) => section.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
            static int Chats(JsonElement section) =>
                section.TryGetProperty("Chats", out var list) && list.ValueKind == JsonValueKind.Array ? list.GetArrayLength() : 0;
            static bool Saved(JsonElement section) => Guid.TryParse(Text(section, "CredentialId"), out var credential) && credential != Guid.Empty;
            var root = document.RootElement;
            object telegramStatus = root.TryGetProperty("Telegram", out var telegram) && telegram.ValueKind == JsonValueKind.Object
                ? new
                {
                    connected = Text(telegram, "BotUsername").Length > 0, enabled = Flag(telegram, "Enabled"), bot = Text(telegram, "BotUsername"),
                    botName = Text(telegram, "BotName"), tokenSaved = Saved(telegram), chats = Chats(telegram), speakReplies = Flag(telegram, "SpeakReplies")
                }
                : NoTelegram();
            object whatsAppStatus = root.TryGetProperty("WhatsApp", out var whatsApp) && whatsApp.ValueKind == JsonValueKind.Object
                ? new
                {
                    connected = Text(whatsApp, "PhoneNumberId").Length > 0, enabled = Flag(whatsApp, "Enabled"), number = Text(whatsApp, "Number"),
                    name = Text(whatsApp, "Name"), appId = Text(whatsApp, "AppId"), businessAccountId = Text(whatsApp, "BusinessAccountId"),
                    phoneNumberId = Text(whatsApp, "PhoneNumberId"),
                    port = whatsApp.TryGetProperty("Port", out var port) && port.TryGetInt32(out var number) ? number : 0,
                    publicAddress = Text(whatsApp, "PublicAddress"), quickTunnel = Text(whatsApp, "PublicAddress").Length == 0,
                    secretsSaved = Saved(whatsApp), chats = Chats(whatsApp), speakReplies = Flag(whatsApp, "SpeakReplies")
                }
                : NoWhatsApp();
            return new { state = "loaded", telegram = telegramStatus, whatsApp = whatsAppStatus };
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

    // character_touch's taps: [{x, y, holdMs}].
    private static IReadOnlyList<(double X, double Y, int HoldMs)>? Taps(JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object || !arguments.TryGetProperty("taps", out var taps) || taps.ValueKind == JsonValueKind.Null)
            return null;
        if (taps.ValueKind != JsonValueKind.Array) throw new ArgumentException("'taps' must be an array of {x, y, holdMs}.");
        return taps.EnumerateArray().Take(DesktopAutomation.MaximumTaps + 1).Select(tap => (
            OptionalDouble(tap, "x") ?? throw new ArgumentException("Each tap needs x."),
            OptionalDouble(tap, "y") ?? throw new ArgumentException("Each tap needs y."),
            OptionalInt(tap, "holdMs") ?? 0)).ToArray();
    }

    /// <summary>character_stroke's points, [[x, y], ...], or null when none were given.</summary>
    private static (double X, double Y)[]? StrokePoints(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty("points", out var value) || value.ValueKind == JsonValueKind.Null)
            return null;
        if (value.ValueKind != JsonValueKind.Array) throw new ArgumentException("'points' must be an array of [x, y] pairs.");
        return [.. value.EnumerateArray().Select(point =>
            point.ValueKind == JsonValueKind.Array && point.GetArrayLength() == 2 && point[0].TryGetDouble(out var x) && point[1].TryGetDouble(out var y)
                ? (x, y) : throw new ArgumentException("Each point must be [x, y]."))];
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
