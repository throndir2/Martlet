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
        ["chatterbox"] = 50083, ["f5"] = 50080, ["xtts"] = 50081, ["gpt-sovits"] = 50082, ["dia"] = 50084
    };

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
        Tool("latency_report", "Summarize voice latency from the desktop log's reply latency lines: for the newest replies, how long " +
            "from when you stopped talking (or sent your message) to the first audio, each step's milliseconds (end of speech, " +
            "speech-to-text, preparing, Thinking connection, hidden reasoning, first sentence, voice synthesis, speakers...), the " +
            "median and 90th percentile of the total and of each step, the slowest steps and the models used. Read-only; starts " +
            "no audio, network or provider request.", new
        {
            replies = new { type = "integer", minimum = 1, maximum = LatencyReport.MaximumReplies },
            dataDirectory = new { type = "string" }
        }),

        Tool("ui_connect", "Attach to an already-running Martlet.Desktop process in this interactive session.", new
        {
            pid = new { type = "integer", minimum = 1 }
        }, ["pid"]),
        Tool("ui_snapshot", "Inspect automation IDs, enabled state and selected non-secret status fields of attached Martlet windows, " +
            "and whether each window can be resized, minimized and maximized. With layout, each control also returns its screen " +
            "bounds and, for text, where its first line of text sits (geometry only, never the text), and each window its bounds " +
            "and its monitor's work area.", new
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
        Tool("ui_move", "Move a movable control by dx, dy screen pixels through UI Automation's Transform pattern and report its " +
            "bounds before and after: the character overlay's MoveAvatar moves the character like a drag. ui_snapshot reports " +
            "movable for such controls (false while the character's position is locked, when this is refused). Requires " +
            "--allow-ui-effects.", new
        {
            id = new { type = "string" },
            dx = new { type = "integer", minimum = -DesktopAutomation.MaximumMove, maximum = DesktopAutomation.MaximumMove },
            dy = new { type = "integer", minimum = -DesktopAutomation.MaximumMove, maximum = DesktopAutomation.MaximumMove }
        }, ["id", "dx", "dy"]),
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
            "and counts of known voices (never names, voiceprints or audio), including how many go by a name of the companion's own " +
            "(from the saved personas) and the most names one voice has. Read-only; no audio, network or models run.", new
        {
            dataDirectory = new { type = "string" },
            martletDirectory = new { type = "string" }
        }),
        Tool("parakeet_check", "Companion > Listening > Parakeet in Martlet: load each Parakeet model downloaded in speechDirectory " +
            "(optional absolute path, default the data directory's speech folder, where the desktop downloads them; or name them in " +
            "models) through the production ParakeetEngine with the sherpa-onnx runtime from martletDirectory, and transcribe phrases " +
            "a Windows voice says (System.Speech rendered to memory, never played; optional phrases, up to 8 English sentences). Per " +
            "model: loadMs, memoryMb (process memory it added), each phrase's transcript, word errors and transcribeMs, the word " +
            "error rate and the median time; ok when every model ran with at most 20% word errors. Also returns voices_status's " +
            "Parakeet part. Nothing is downloaded, recorded or played; nothing leaves this PC.", new
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
            "length bounds, tag catalog, summary, languages, whether it learns from several recordings and the feature chips Companion > " +
            "Voice > Voice engine shows; each starter voice lists the engines that can clone it and its language) with the one chosen on this desktop " +
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
            "its own syntax with each tag's engine-independent cue, the Thinking prompt it adds (Companion > Prompts > Voice sounds " +
            "and tones, from dataDirectory's settings when given), the pieces the real speech segmenter hands that engine for a " +
            "spoken reply, broken where the persona's speech breaks allow (Personality > Where the voice pauses: dataDirectory's " +
            "persona by name, else the one Martlet uses, else the defaults;             \"breaks\" overrides periods, questionMarks, " +
                        "exclamationMarks and shortEndingWords; commas, semicolons and dashes never break), the text the chat and captions show and, with characterTags (the character's " +
            "tags such as \"{blush}\"), the character cues found in each spoken piece (piece index, -1 for cues after the last " +
            "words; tag; character offset). Synthesizes and contacts nothing.", new
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
            "the Windows hypervisor, Virtual Machine Platform, Windows Subsystem for Linux, their host services, WSL version and status), " +
            "pending and required restarts, blockers and recovery guidance, whether Docker Desktop is installed and running and its engine " +
            "state, and any setup Martlet continues after a Windows restart. Read-only; starts no VM, changes nothing and returns no distribution names.", new
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
            "output and outcomes reach the sender, secrets never appear in lists or saved copies, cancel works, an update that waits " +
            "holds the queue and the sender sees what its command waits behind, and commands survive a restart. Contacts nothing " +
            "outside loopback and touches no real credentials, Docker or installs.", new { }),
        Tool("host_engine_check", "Check that a Martlet host makes one change at a time: runs this checkout's real martlet-host " +
            "engine in one disposable ubuntu:24.04 container (no network, never pulled, removed afterwards; Martlet's own host " +
            "containers and volumes are never touched) against a fixture setup. A change holds the engine lock; read-only commands " +
            "still run; status names the holder; an automatic run (no terminal, no --yes) stops at once with exit 75 and " +
            "MARTLET-BUSY, changing nothing; an attended run waits and gives up after MARTLET_LOCK_WAIT; a waiting run continues when " +
            "the holder is killed, and so does a run without a terminal or --yes told to wait (Update hosts now); no stale lock " +
            "remains; the journal records it; the desktop's reader reads the busy line. With a " +
            "fake docker CLI it also checks the Docker method: setup does not replace the network holder while an engine session " +
            "(an add) runs in it (an automatic setup stops with MARTLET-BUSY, an attended one waits, then replaces it), and an engine " +
            "left in a replaced holder's namespace stops at once. Returns notRun when Docker or the image is missing.", new { }),
        Tool("host_update_check", "Rehearse how Martlet coordinates its own host service updates, with the desktop's production " +
            "update tracker and busy reader: an Update host run window claims its host so the automatic pass leaves it to that run " +
            "(no second engine run that finds the host locked by Martlet's own update and reports it busy, for its pairing or as this " +
            "PC's own host service); overlapping routes end separately; an update started elsewhere is named as another update; a " +
            "host found current stops waiting and its stale note says it is updated; Update hosts now waits for another change. Pure logic: contacts " +
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
        Tool("character_actions", "Read a character model's emotes and motions as Companion > Character > Emotes and motions uses " +
            "them (Martlet.Avatar.Hosting, docs/AVATARS.md \"Emotes and motions\"): modelPath (a .model3.json or .vrm on this PC) or the " +
            "model dataDirectory's avatar.json shows. Returns the renderer, the model's key, how many files the renderer reads (a VTube " +
            "Studio model's .vtube.json and loose .exp3/.motion3 files included) and what came from VTube Studio's settings, then each " +
            "expression, motion group and Martlet gesture (nod, shake) with what it changes, its tag, voice cue, when to use it, whether " +
            "it is on and whether replies are offered it for engine (a voice engine key; \"none\" or absent: a voice without tags); the " +
            "saved settings (character-actions.json in dataDirectory) or the defaults from the model's names; the reply prompt and tags; " +
            "and the Thinking naming prompt. With answer (a simulated Thinking reply such as \"1: blush | - | when shy\"), also what the " +
            "production parser makes of it. Reads only; contacts nothing and never returns the model's path.", new
        {
            dataDirectory = new { type = "string" }, modelPath = new { type = "string" }, engine = new { type = "string" },
            answer = new { type = "string" }
        }),
        Tool("character_gaze", "Where the character looks (Companion > Vision > Where the character looks; docs/SCREEN_COMMENTARY.md " +
            "\"Where the character looks\"): the saved choice in a data directory's talk-preferences.json (mouse unless Martlet " +
            "decides), then a rehearsal of the production decision (Martlet.Avatar.Hosting CharacterGaze and GazeDirector) on " +
            "generated 1920x1080 pictures (NOT screenshots; nothing is captured): a notification popping up, the same spot again soon " +
            "and later, another change right after a glance, a notification behind the character, the character's own motion, its " +
            "speech bubble, a new scene, a change by the mouse and changes all over, each with the expected and actual verdict and " +
            "the spot looked at (ok: all as expected). Also where each look tag points on one and two screens, the screen glance's " +
            "look instructions (the data directory's edited prompts included) and what the production segmenter makes of glance " +
            "answers that start with a look tag (spoken, shown, quiet, the look cue); answer replaces the sample answers. Reads " +
            "only; contacts nothing.", new
        {
            dataDirectory = new { type = "string" }, answer = new { type = "string", maxLength = 2000 }
        }),
        Tool("character_theme", "A character model's colors and palettes as Settings > Appearance makes them (docs/UI_DESIGN.md " +
            "\"Character palettes\"): modelPath (a .model3.json or .vrm on this PC), else the model dataDirectory's avatar.json shows, " +
            "else the built-in character. Returns the main colors read from its textures (hex, share, kind, name), the colors the " +
            "rules build from (tint, accent, glow, lightest and darkest), Martlet's rule-based light and dark palettes with their " +
            "lowest contrasts and any rule problems, the Thinking request (instructions, message, picture kind and size) and the " +
            "palettes saved in dataDirectory's character-themes.json. With answer (a simulated Thinking reply), what the production " +
            "parser and rule repair make of it. With live: true, asks Ollama on this PC (model, or the saved local Thinking model) " +
            "with the real instructions, message and picture, loopback only, and parses the reply. With previewDirectory (an absolute " +
            "folder), writes PNG pictures of Martlet's window in each palette and the picture sent (label names the files). name and " +
            "about stand in for the character list's name and the owner's words on who it is and where it's from (Settings > " +
            "Appearance); identity shows what the model's files say and what the Thinking model is told. Never " +
            "returns the model's path.", new
        {
            dataDirectory = new { type = "string" }, modelPath = new { type = "string" }, answer = new { type = "string", maxLength = 16384 },
            live = new { type = "boolean" }, model = new { type = "string", maxLength = 128 }, previewDirectory = new { type = "string" },
            label = new { type = "string", maxLength = 40 }, name = new { type = "string", maxLength = 80 }, about = new { type = "string", maxLength = 160 }
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
        Tool("voice_engine_check", "Speak one sentence with a self-hosted voice engine's loopback service (a host role's service, " +
            "default chatterbox on http://127.0.0.1:50083; f5 50080, xtts 50081, gpt-sovits 50082, dia 50084) through the production " +
            "path: the engine's own gateway relay inside a real gateway on 127.0.0.1 (pinned TLS, pairing) and the desktop's paired " +
            "client, with a starter voice as the reference (nothing played or recorded). Returns the service's /status before and " +
            "after (state, error, runtime versions such as torch and CUDA), the audio length, time to first audio, total time, " +
            "real-time factor, peak and RMS level, or the failure code and message. Loopback only; runs Martlet.NodeLinkCheck.", new
        {
            engine = new { type = "string", @enum = VoiceEnginePorts.Keys.ToArray() },
            endpoint = new { type = "string", maxLength = 64 },
            text = new { type = "string", maxLength = 300 }
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
            "(the saved edit or the built-in text) exactly as Martlet uses it. Read-only.", new
        {
            dataDirectory = new { type = "string" },
            id = new { type = "string", maxLength = 64 }
        }),
        Tool("character_status", "Read Companion > Personality and Character as saved in a data directory (they save on their own, " +
            "with no Save button): the personas (name, whether Martlet uses it, response-style weights, speech breaks, instruction " +
            "length; never the instructions), the character model (built-in character name or the own model's file type, never its " +
            "path; renderer, lip-sync mode, show at startup, the lip-sync host's ID), whether the character's position is locked on " +
            "this PC and where (placement), whether Martlet's voice is muted (voice: Speak Martlet's replies aloud, which the " +
            "character's Mute voice / Unmute voice menu item changes) and the lorebooks (counts only). Read-only.", new
        {
            dataDirectory = new { type = "string" }
        }),
        Tool("hearing_check", "Whether the Thinking model can hear the user's recording (the saved Thinking route in a data " +
            "directory, or modelId): the model's name-based hearing, the route's (the production decision: only Chat Completions routes, " +
            "Ollama on this PC included, then what model-abilities.json says, then the name), savedAbility (what Martlet found out about " +
            "the saved model and where) and whether Companion > Listening > Let Thinking hear " +
            "my voice is on. Then rehearses the production Chat Completions adapter against a fixture endpoint on 127.0.0.1 (canned " +
            "reply, NOT AI) with a synthesized speech-like clip (never microphone audio, nothing played): the clip goes as an " +
            "input_audio WAV part beside the transcript, is refused without its own audio permission before any request, and is " +
            "left out of a transcript-only retry. Loopback only; reads no credentials.", new
        {
            dataDirectory = new { type = "string" },
            modelId = new { type = "string", maxLength = 128 }
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
            "only what is said aloud and is not a failure (voice.muted); text-only sends the reply with no voice at all (Speak " +
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
            "the level they switch to, and ok also needs them out of reply.text and voice.pieces. Loopback only; reads no credentials.", new
        {
            voiceFailure = new { type = "string", @enum = SpokenReplyCheck.Failures },
            failAt = new { type = "integer", minimum = 1, maximum = 4 },
            reasoningMs = new { type = "integer", minimum = 0, maximum = 5000 },
            voiceDelayMs = new { type = "integer", minimum = 0, maximum = 5000 },
            reply = new { type = "string", maxLength = 1024 }, dataDirectory = new { type = "string" }, persona = new { type = "string" },
            breaks = BreaksSchema(),
            thinkingSteps = new { type = "string", @enum = new[] { "off", "on" } },
            refuseThinking = new { type = "boolean" },
            chattiness = new { type = "boolean" }
        }),
        Tool("smart_home_status", "Read Companion > Smart home's saved connection from a data directory: the Home Assistant address, " +
            "name and version, whether a token is saved (never the token), the control, locks and flexible-request settings, and whether " +
            "the connection is shared through the paired hosts (shared revision, which host it came from). Read-only.", new
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
            "utterance_filter_check).", new
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
        Tool("chattiness_status", "Companion > Vision > How often it comments (the same choice as Listening > Watch along) as saved " +
            "in a data directory's talk-preferences.json: the choice (Quiet, Normal, Chatty or Martlet decides; Normal by default), " +
            "whether vision and hearing the PC are on (replies are told about Martlet decides only while one is), the level Martlet " +
            "decides starts at, the tags a reply switches the level with, what Martlet decides tells the Thinking model and the " +
            "note that says the level (Companion > Prompts, from settings.json's edits), then a rehearsal: sample replies (or reply) " +
            "through the production speech segmenter and chat stripper with those tags offered, returning what is spoken and shown, " +
            "whether it stays silent, the tags found and the level they switch to. The level a running conversation picked shows in " +
            "the talk window's LiveChattiness line and the desktop log. Reads no credentials and contacts nothing.", new
        {
            dataDirectory = new { type = "string" },
            reply = new { type = "string", maxLength = 1024 }
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
            "Thinking's model off the card); and a long conversation fitted into a paired computer's 16 KiB and 16 messages. " +
            "reasoningMs (200-3000, default 1200) is how long the fixture's hidden " +
            "reasoning takes. Loopback only; reads no credentials.", new
        {
            reasoningMs = new { type = "integer", minimum = 200, maximum = 3000 }
        }),
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
            "conversations folder holds: files, bytes, conversations, exchanges, unreadable lines and the oldest and newest times. " +
            "Never what was said. Read-only.", new
        {
            dataDirectory = new { type = "string" }
        }),
        Tool("conversation_history_check", "Rehearse the record of conversations with the production code (ConversationHistory and " +
            "PastConversations) on synthetic conversations in a disposable folder: recording exchanges into month files, a line " +
            "cut short by a crash skipped after a restart, an ordinary message recalling nothing, \"Do you remember...\" and " +
            "\"What did we talk about yesterday?\" bringing back the right exchanges (never the conversation going on), " +
            "search_conversations by words and by time and its answers, deleting one conversation and everything, and reading " +
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
                "doctor_status" => await DoctorAsync(["status", "--json"], arguments, cancellation),
                "doctor_list" => await DoctorAsync(["list", "--json"], arguments, cancellation),
                "doctor_run" => await DoctorAsync(
                    ["run", .. RequiredStrings(arguments, "probes"), "--json"], arguments, cancellation),
                "logs_tail" => LogTail.Read(OptionalString(arguments, "dataDirectory"), OptionalString(arguments, "log"),
                    OptionalInt(arguments, "lines"), OptionalString(arguments, "contains")),
                "logs_timeline" => LogTimeline.Read(OptionalString(arguments, "dataDirectory"), OptionalString(arguments, "level"),
                    OptionalString(arguments, "component"), OptionalString(arguments, "contains"), OptionalInt(arguments, "lines")),
                "latency_report" => LatencyReport.Read(OptionalString(arguments, "dataDirectory"), OptionalInt(arguments, "replies")),

                "ui_connect" => desktop.Connect(RequiredInt(arguments, "pid")),
                "ui_snapshot" => desktop.Snapshot(OptionalBool(arguments, "layout") ?? false),
                "ui_click" => await desktop.ClickAsync(RequiredString(arguments, "id")),
                "ui_select" => desktop.Select(RequiredString(arguments, "id"), RequiredString(arguments, "item")),
                "ui_set_text" => desktop.SetText(RequiredString(arguments, "id"),
                    OptionalString(arguments, "text") ?? throw new ArgumentException("Missing string 'text'.")),
                "ui_toggle" => desktop.Toggle(RequiredString(arguments, "id")),
                "ui_move" => desktop.Move(RequiredString(arguments, "id"), RequiredInt(arguments, "dx"), RequiredInt(arguments, "dy")),
                "ui_tray" => desktop.Tray(OptionalString(arguments, "action") ?? "status", OptionalInt(arguments, "x"), OptionalInt(arguments, "y")),
                "voices_status" => VoicesStatus(arguments),
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
                "network_selftest" => await NodeLinkCheckAsync(cancellation, "network"),
                "nearby_status" => NearbyStatus(arguments),
                "virtualization_status" => await VirtualizationStatusAsync(arguments, cancellation),
                "host_service_status" => await HostServiceStatusAsync(cancellation),
                "node_link_check" => await NodeLinkCheckAsync(cancellation),
                "host_engine_check" => await HostEngineCheck.RunAsync(cancellation),
                "host_update_check" => HostUpdateCheck.Run(),
                "app_update_check" => await AppUpdateCheck.RunAsync(NodeLinkCheckProgram(), cancellation),
                "api_keys_status" => ApiKeysStatus(arguments),
                "api_selftest" => await NodeLinkCheckAsync(cancellation, "api"),
                "speaking_voices_selftest" => await NodeLinkCheckAsync(cancellation, "voices"),
                "character_models" => CharacterModels(arguments),
                "character_actions" => await CharacterActionsCheckAsync(arguments, cancellation),
                "character_gaze" => GazeCheck.Run(DataDirectory(arguments), OptionalString(arguments, "answer")),
                "character_theme" => await CharacterThemeCheck.RunAsync(OptionalString(arguments, "modelPath"), OptionalString(arguments, "dataDirectory"),
                    OptionalString(arguments, "answer"), OptionalBool(arguments, "live") ?? false, OptionalString(arguments, "model"),
                    OptionalString(arguments, "previewDirectory"), OptionalString(arguments, "label"), OptionalString(arguments, "name"),
                    OptionalString(arguments, "about"), cancellation),
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
                "mcp_servers_status" => McpServersStatus(arguments),
                "mcp_directory_plan" => McpDirectoryPlan(arguments),
                "home_assistant_probe" => await HomeAssistantProbeAsync(arguments, cancellation),
                "home_assistant_find" => await HomeAssistantFindAsync(arguments, cancellation),
                "smart_home_status" => SmartHomeStatus(arguments),
                "terminal_status" => TerminalCheck.Status(DataDirectory(arguments)),
                "terminal_check" => await TerminalCheck.RunAsync(DataDirectory(arguments), OptionalString(arguments, "shell"), cancellation),
                "prompts_status" => await PromptsStatusAsync(arguments, cancellation),
                "character_status" => await CharacterStatusAsync(arguments, cancellation),
                "hearing_check" => await HearingCheck.RunAsync(OptionalString(arguments, "modelId"), DataDirectory(arguments), cancellation),
                "model_ability_check" => await ModelAbilityCheck.RunAsync(DataDirectory(arguments), OptionalString(arguments, "baseUrl"),
                    OptionalString(arguments, "modelId"), OptionalBool(arguments, "test") ?? false, cancellation),
                "spoken_reply_check" => await SpokenReplyCheck.RunAsync(OptionalString(arguments, "voiceFailure"),
                    OptionalInt(arguments, "failAt"), cancellation, OptionalInt(arguments, "reasoningMs"),
                    OptionalInt(arguments, "voiceDelayMs"), OptionalString(arguments, "reply"),
                    SpeechBreaksFrom(arguments, SavedSettings(arguments), out var speaker), speaker?.Name,
                    OptionalString(arguments, "thinkingSteps"), OptionalBool(arguments, "refuseThinking") ?? false,
                    OptionalBool(arguments, "chattiness") ?? false),
                "echo_check" => await EchoCheck.RunAsync(DataDirectory(arguments), OptionalInt(arguments, "delayMs"), cancellation),
                "utterance_filter_check" => await UtteranceFilterCheck.RunAsync(arguments, DataDirectory(arguments), MartletDirectory(arguments),
                    SpeechDirectory(arguments), cancellation),
                "pc_audio_check" => await PcAudioCheck.RunAsync(DataDirectory(arguments), cancellation),
                "chattiness_status" => await ChattinessCheck.RunAsync(DataDirectory(arguments), OptionalString(arguments, "reply"), cancellation),
                "context_check" => await ContextCheck.RunAsync(DataDirectory(arguments), cancellation),
                "thinking_steps_check" => await ThinkingStepsCheck.RunAsync(DataDirectory(arguments), OptionalString(arguments, "model"),
                    OptionalBool(arguments, "live") ?? false, cancellation),
                "think_longer_status" => await ThinkLongerCheck.StatusAsync(DataDirectory(arguments), cancellation),
                "think_longer_check" => await ThinkLongerCheck.RunAsync(OptionalInt(arguments, "reasoningMs"), cancellation),
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
            roster
        };
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
        string[] command = OptionalString(arguments, "text") is { Length: > 0 } text
            ? ["voice-engine", engine, uri.GetLeftPart(UriPartial.Authority) + "/", text]
            : ["voice-engine", engine, uri.GetLeftPart(UriPartial.Authority) + "/"];
        return await NodeLinkCheckAsync(TimeSpan.FromMinutes(6), cancellation, command);
    }

    /// <summary>Runs Martlet.NodeLinkCheck (built next to this server, in the same configuration) with <paramref name="arguments"/>
    /// and returns its JSON report. A separate process, because the in-process gateway needs the ASP.NET Core runtime and this
    /// server does not.</summary>
    private static Task<object> NodeLinkCheckAsync(CancellationToken cancellation, params string[] arguments) =>
        NodeLinkCheckAsync(TimeSpan.FromMinutes(2), cancellation, arguments);

    /// <summary>Martlet.NodeLinkCheck's executable in this source checkout's build (the same configuration as this server).</summary>
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
        var key = OptionalString(arguments, "engine");
        var engine = key is null or "none" ? null
            : Martlet.Core.Settings.SpeechEngines.ForKey(key) ?? throw new ArgumentException($"Unknown voice engine '{key}'.");
        object Describe(Martlet.Avatar.Hosting.CharacterActionCatalog of)
        {
            var offered = of.Offered(engine).Select(e => e.Source.Id).ToHashSet(StringComparer.Ordinal);
            return of.Entries.Select((e, n) => new
            {
                n, id = e.Source.Id, kind = e.Source.Kind.ToString().ToLowerInvariant(), name = e.Source.Name, detail = e.Source.Detail,
                tag = e.Action.Tag, cue = e.Action.Cue, use = e.Action.Use, enabled = e.Action.Enabled, offered = offered.Contains(e.Source.Id)
            }).ToArray();
        }
        var reply = catalog.Prompt(engine, prompts);
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
        return new
        {
            renderer = avatarRenderer.ToString(), key = inventory.ModelId[..16], files = assets.Count,
            expressions = inventory.Sources.Count(s => s.Kind == Martlet.Avatar.Hosting.CharacterActionKind.Expression),
            motions = inventory.Sources.Count(s => s.Kind == Martlet.Avatar.Hosting.CharacterActionKind.Motion),
            fromVTubeStudio = extras is null ? null : new
            {
                expressions = extras.Expressions.Select(e => new { e.Name, e.File }), motions = extras.Motions.Select(m => new { m.Group, m.File })
            },
            saved = saved is not null, detectedBy = catalog.Settings.DetectedBy, detectedAt = catalog.Settings.DetectedAt,
            engine = engine?.Key, actions = Describe(catalog),
            replyPrompt = reply?.Instructions, replyTags = reply?.Tags,
            namingPrompt = naming is { } ask ? new { instructions = ask.Instructions, list = ask.List } : null,
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
        var characterTags = arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty("characterTags", out var listed) &&
            listed.ValueKind == JsonValueKind.Array
            ? listed.EnumerateArray().Where(t => t.ValueKind == JsonValueKind.String).Select(t => t.GetString()!).Take(128).ToArray()
            : null;
        var breaks = SpeechBreaksFrom(arguments, settings, out var persona);
        var preview = Martlet.Conversation.SpeechTextPreview.For(text, engine, characterTags, breaks);
        return new
        {
            engine = engine?.Key, name = engine?.Name, supportsTags = engine?.SupportsTags ?? false,
            tags = (engine?.Tags ?? []).Select(tag => tag.Text).ToArray(),
            cues = (engine?.Tags ?? []).Select(tag => new { tag = tag.Text, cue = tag.Cue }).ToArray(),
            prompt = Martlet.Core.Settings.VoiceTags.Instructions(engine, prompts),
            persona = persona?.Name, breaks = Breaks(breaks),
            spoken = preview.Spoken, suppressedPieces = preview.SuppressedPieces, shown = preview.Shown,
            characterCues = preview.Cues.Select(c => new { piece = c.Piece, tag = c.Tag, offset = c.Offset }).ToArray()
        };
    }

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
            @default = engine == Martlet.Core.Settings.SpeechEngines.Default,
            supportsTags = engine.SupportsTags, multipleReferences = engine.MultipleReferences,
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
                    "model-abilities")
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
            prompt = id is null ? null : new { id, state = Of(id), text = Martlet.Core.Settings.PromptSettings.Text(prompts, id) }
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
                styles = new
                {
                    helpful = p.Styles.Helpful, sarcastic = p.Styles.Sarcastic, silly = p.Styles.Silly,
                    distracted = p.Styles.Distracted, playfulTeasing = p.Styles.PlayfulTeasing
                },
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
        return new { personality, character, placement = CharacterPlacement(directory), voice = CharacterVoice(directory), lorebooks };
    }

    /// <summary>Whether Martlet's voice is muted, from talk-preferences.json in a data directory (Martlet.Desktop's
    /// TalkPreferences): SpeakReplies (Companion › Voice's Speak Martlet's replies aloud) is on unless saved off, and Mute
    /// voice / Unmute voice on the character's right-click menu change the same choice.</summary>
    private static object CharacterVoice(string directory)
    {
        var path = Path.Combine(directory, "talk-preferences.json");
        if (!File.Exists(path)) return new { state = "none", speakReplies = true, muted = false };
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return new { state = "unreadable", speakReplies = true, muted = false, problem = "NotAnObject" };
            var speak = !(document.RootElement.TryGetProperty("SpeakReplies", out var value) && value.ValueKind == JsonValueKind.False);
            return new { state = "loaded", speakReplies = speak, muted = !speak };
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return new { state = "unreadable", speakReplies = true, muted = false, problem = error.GetType().Name };
        }
    }

    /// <summary>character-placement.json in a data directory (Martlet.Desktop's CharacterPlacementStore): whether the character's
    /// position is locked on this PC and where (device-independent pixels). No file means unlocked.</summary>
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
                left = Number("Left"), top = Number("Top"), width = Number("Width"), height = Number("Height")
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
