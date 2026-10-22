using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Martlet.Core.Installation;
using Martlet.Doctor;
using Martlet.Mcp.Client;

namespace Martlet.Mcp;

/// <summary>martlet_guide's "Thinking and models" area: the Thinking pool, models, vision, reading, pictures and context. Each
/// Tool(...) registers one tool: its name, description, input schema, handler and, when it needs one, its permission (<see
/// cref="ToolPermission"/>). A new tool of this area goes here.</summary>
internal sealed partial class McpServer
{
    private McpTool[] ThinkingTools() =>
    [
        Tool("deep_thinking_role_selftest", "Rehearse the Deep thinking host role end to end with the production code: one real " +
            "gateway on 127.0.0.1 (pinned TLS) serving a host's Thinking route (the ollama role) and the deep-thinking role's own route " +
            "(martlet.gateway.deep-thinking-chat.v1), each relay over its own fixture Ollama (NOT AI) on its own graphics card, and a " +
            "simulated desktop using the desktop's paired client. Checks both routes and their models are advertised, Thinking's advertised route saves as the " +
            "desktop's job route (handing Thinking to the host), a think on the Deep thinking route runs " +
            "while a reply streams on Thinking's route (the reply finishes first), each request reaches its own Ollama (the think " +
            "with Thinking steps on), a Thinking pool job's request (the role's largest context window above the job's own budget) " +
            "is accepted, two thinks run at once on the role's two slots (advertised as the route's maximum_concurrency) " +
            "while a reply streams and a third gets job.busy, and that the chat client refuses a mismatched route. Loopback only; writes nothing to disk or " +
            "the credential vault.", new { },
            async (_, cancellation) => await NodeLinkCheckAsync(cancellation, "deep-thinking")),
        Tool("reading_check", "Companion › Reading (docs/READING.md): read a data directory's Reading list (where Martlet reads " +
            "the text on the screen, in order: Windows OCR on this PC, the Reading role of your computers; nothing on is off) and read a drawn test picture with known " +
            "text (HEALTH 87 / 100, Score: 12450, VICTORY, a chat line) through Windows OCR on this PC, as watching does: lines, " +
            "the joined text, missing words and milliseconds. desktop: a drawn 1920 x 1080 desktop of 108 small (12 px) text lines " +
            "read at full size, as the desktop reads the screen, and at a vision look's 1024 x 576: lines found, lines read right " +
            "and milliseconds for each. With endpoint (a Reading role's worker on loopback, such as " +
            "http://127.0.0.1:50087/) that worker's GET /status and POST /read read the same picture as a PNG. pool (with " +
            "dataDirectory): the list (pools-local.json, or made from reading.json until the page saves it), its members and the " +
            "places a read tries, first to last. poolCheck: the production list code, planner and queue with simulated computers " +
            "(NOT real hosts): the list's order, off when nothing is on, a busy computer passed over, a wait for the first to free, " +
            "a friend's host left for its owner. pace (with dataDirectory): Companion › Reading › Read the screen (talk-preferences.json's " +
            "ReadEveryMs: how often a full-size picture is read, 3000 by default). paceCheck: the production read pace for each choice " +
            "over a simulated minute (reads of a changing screen with a fast and a slow reader, and of a still screen). settleCheck: " +
            "the production OCR flicker filter (ScreenText.Settle) over simulated reads: a misread once never counts, new text counts " +
            "at its second read, a timer counts as a live value, another window drops the old text. Never " +
            "captures the real screen.", new
        {
            dataDirectory = new { type = "string" },
            endpoint = new { type = "string", maxLength = 64 }
        },
            async (arguments, cancellation) => await ReadingCheck.RunAsync(
                OptionalString(arguments, "dataDirectory") is null ? null : DataDirectory(arguments),
                OptionalString(arguments, "endpoint"), cancellation)),
        Tool("pictures_status", "Read Companion › Picture making for a data directory: where Martlet draws (pictures.json: off, " +
            "Martlet's Picture making host role, the owner's ComfyUI at an address, OpenRouter or NVIDIA Build; the workflow, checkpoint or " +
            "model; whether an own key is saved, never the key), the Picture making list (pool: pools-local.json's members with their keys, " +
            "settings and agreement, or the list the desktop would make from pictures.json; tries: the places a picture tries now, in " +
            "order), the loaded custom workflow's node count, the picture creations " +
            "(shape, size, engine, model, seconds, fixture, assets, whether they're on this PC; never titles or descriptions) and the " +
            "draw_picture tool and job kind the conversation offers. Read-only.", new
        {
            dataDirectory = new { type = "string" }
        },
            arguments => PicturesCheck.Status(DataDirectory(arguments))),
        Tool("pictures_check", "Draw one picture through the production picture maker and report it: place \"fixture\" (the " +
            "default: the FIXTURE - NOT AI gradient maker) or \"comfyui\" (a ComfyUI at address, such as http://127.0.0.1:8188, " +
            "through Martlet's ComfyUI client: status and model check, the workflow (z-image-turbo, checkpoint with checkpoint, or " +
            "custom with workflowFile, an absolute path to an Export (API) file), queue, history, the picture fetched). Returns " +
            "availability, every progress stage, the media type, size, SHA-256, seconds and the workflow's node types. With " +
            "dataDirectory (disposable) it keeps the picture as a picture creation there and reads it back as the talk window " +
            "does; with saveDirectory (absolute) it writes the picture there. Place \"pool\" instead rehearses the Picture making list's " +
            "routing (PictureMakingPool and WorkQueue) on simulated ComfyUI computers (FIXTURE): busy, absent or unsuitable computers " +
            "passed over, all busy waiting in the shortest queue, two pictures on two computers and the first list made from " +
            "pictures.json; it returns ok and each step. Never calls a paid cloud provider.", new
        {
            place = new { type = "string", @enum = new[] { "fixture", "comfyui", "pool" } },
            address = new { type = "string", maxLength = 512 },
            workflow = new { type = "string", @enum = new[] { "z-image-turbo", "checkpoint", "custom" } },
            checkpoint = new { type = "string", maxLength = 255 },
            workflowFile = new { type = "string", maxLength = 260 },
            prompt = new { type = "string", maxLength = 2000 },
            shape = new { type = "string", @enum = new[] { "square", "landscape", "portrait", "wide", "tall" } },
            dataDirectory = new { type = "string", maxLength = 260 },
            saveDirectory = new { type = "string", maxLength = 260 }
        },
            async (arguments, cancellation) => await PicturesCheck.RunAsync(OptionalString(arguments, "place"), OptionalString(arguments, "address"),
                OptionalString(arguments, "workflow"), OptionalString(arguments, "checkpoint"), OptionalString(arguments, "workflowFile"),
                OptionalString(arguments, "prompt"), OptionalString(arguments, "shape"),
                OptionalString(arguments, "dataDirectory") is null ? null : DataDirectory(arguments), OptionalString(arguments, "saveDirectory"),
                cancellation)),
        Tool("model_catalog_status", "Martlet's internal model catalog (docs/MODEL_CATALOG.md): which copy is in use (the snapshot " +
            "shipped with Martlet or the daily copy in the PC folder, %ProgramData%\\Martlet for the default data folder, else the data " +
            "folder itself), when each was built and read each source, what the last daily refresh did (lastAttempt, lastFinished, " +
            "lastSuccess, lastSaved, due, nextDue, problem, and per source: tried, read, items, bytes, seconds, problem), and counts " +
            "(models, routes, open weights, sees, hears, takes video, calls tools, inputs the sources disagree on, ranked, LMArena " +
            "rated, free and retired routes, routes per provider). Read-only; contacts nothing.", new
        {
            dataDirectory = new { type = "string" }
        },
            arguments => ModelCatalogCheck.Status(DataDirectory(arguments))),
        Tool("model_catalog_lookup", "Find a model in Martlet's internal model catalog by any name: a Hugging Face repository, an " +
            "OpenRouter, NVIDIA Build, models.dev or provider model ID (\":free\" variants too), an Ollama tag (\"gemma4:26b\", by family " +
            "and size) or \"hf.co/{repo}:{quant}\" name, or an LMArena name. Returns how it was found, the model (key, names on each " +
            "route, family, size, inputs text/image/audio/video/videoAsFrames, open weights, and every fact with each source's answer, " +
            "note and date and the answer worked out from them: the maker first (config.json, vLLM by architecture), then catalogs and " +
            "servers; a disagreement at the same level is unknown), its routes (provider, model ID, free, context, expires, retired, " +
            "deprecated, inputs, tools, reasoning: the server's own metadata, then the model's facts, then models.dev's row for that " +
            "provider only) and how smart it is in words, with LMArena's rating and its credit (never the Artificial Analysis index). " +
            "Read-only; contacts nothing.", new
        {
            name = new { type = "string", maxLength = 256 },
            dataDirectory = new { type = "string" }
        },
            arguments => ModelCatalogCheck.Lookup(DataDirectory(arguments), RequiredString(arguments, "name"))),
        Tool("model_catalog_refresh", "Rehearse the model catalog's daily refresh (ModelCatalogRefresh) against FIXTURE sources on " +
            "127.0.0.1 shaped like OpenRouter, models.dev (models.json and api.json), NVIDIA Build (models.md and its pages), vLLM's " +
            "supported models and LMArena's dataset rows, in a temporary folder: every source read and saved; no Artificial Analysis " +
            "index kept; once a day; the measured conflicts (Gemma 4 26B video, Qwen3.5 122B audio, the gemma-4-31b-it provider " +
            "split); NVIDIA routes; an expired route retired; find by any name; smartness with its fallback; a failing source and an " +
            "oversized one keep their last good part; and nothing is asked while Martlet is replying. live=true instead reads the real " +
            "public sources (no key) into the data folder's daily copy when a refresh is due (force=true: now); with snapshotPath (an " +
            "absolute path) it writes the release snapshot instead, only when every source was read (scripts\\Update-ModelCatalog.ps1).", new
        {
            live = new { type = "boolean" },
            force = new { type = "boolean" },
            snapshotPath = new { type = "string" },
            dataDirectory = new { type = "string" }
        },
            async (arguments, cancellation) => await ModelCatalogCheck.RefreshAsync(DataDirectory(arguments), OptionalBool(arguments, "live") ?? false,
                OptionalString(arguments, "snapshotPath"), OptionalBool(arguments, "force") ?? false, cancellation)),
        Tool("model_ability_check", "What models were found to take on each route (a server and a model): hearing (recorded audio), " +
            "seeing (pictures), video itself, tool calls and retired (HTTP 410), each with its source and date: model-abilities.json in " +
            "a data directory, also shared with the owner's other computers as the model-abilities setting. Then rehearses the production " +
            "detection (ModelContextProbe) against fixture servers on 127.0.0.1 shaped like OpenRouter's model list " +
            "(architecture.input_modalities, supported_parameters), llama.cpp (/props modalities), Ollama (/api/show capabilities) and " +
            "NVIDIA Build (IDs-only model list, models.md and a model page's Specifications), Test hearing " +
            "(ModelHearingTest), Test vision (ModelVisionTest, with the desktop's own picture of one word drawn on this PC) and Test tools " +
            "(ModelToolTest, one made-up tool) against a " +
            "fixture Chat Completions endpoint that answers the test word only when the request carries the recording or the picture (it " +
            "is told the word: NOT AI), a model that ignores audio, pictures or tools, one that refuses them, one retired (410) and a " +
            "wrong key; the hearing, vision, video, tools and retired decisions replies use, with a test outranking later metadata " +
            "(decisions), whether a paired computer's Thinking route takes a recording " +
            "(hostRoute) and the shared value's round trip, including what an older Martlet still reads (shared). With baseUrl (an http:// " +
            "server on this PC only, for example Ollama's http://127.0.0.1:11434/v1 or a llama.cpp server) and modelId it also asks that " +
            "real server what the model takes (real.metadata); test=true sends it the real Test hearing request (one word said by Windows " +
            "speech, never microphone audio, nothing played), testVision=true the real Test vision request (one word drawn on this PC, " +
            "never the screen) and testTools=true the real Test tools request (one made-up tool). Nothing leaves this PC; reads no " +
            "credentials; saves nothing.", new
        {
            dataDirectory = new { type = "string" },
            baseUrl = new { type = "string", maxLength = 256 },
            modelId = new { type = "string", maxLength = 128 },
            test = new { type = "boolean" },
            testVision = new { type = "boolean" },
            testTools = new { type = "boolean" }
        },
            async (arguments, cancellation) => await ModelAbilityCheck.RunAsync(DataDirectory(arguments), OptionalString(arguments, "baseUrl"),
                OptionalString(arguments, "modelId"), OptionalBool(arguments, "test") ?? false, OptionalBool(arguments, "testVision") ?? false,
                OptionalBool(arguments, "testTools") ?? false, cancellation)),
        Tool("local_model_facts", "What an open-weight model needs to run locally, through the production LocalModelFactsReader and " +
            "LocalModelMemory: model (a Hugging Face repository such as google/gemma-4-E2B-it, hf.co/{repo}:{quant}, or an Ollama tag " +
            "such as gemma4:e2b), or huggingFaceRepo and ollamaTag together (the repository gives the shape and inputs, the tag its exact " +
            "Ollama download). Reads Hugging Face's model API (license, task, parameters, every file's size), config.json (layers, KV " +
            "heads, head size, sliding window, experts; image, audio and video inputs) and its GGUF search (prefers ggml-org, unsloth, " +
            "lmstudio-community, bartowski), and the Ollama registry's manifest and config blob (registry.ollama.ai, never ollama.com). " +
            "Returns facts (parameters total, in the weights file and active; inputs; architecture; quantizations with weights, encoder " +
            "and draft sizes and install names; sources; problems), estimate (weights + encoder + draft + KV cache at contextTokens, " +
            "default 8,192, + 0.5 GB buffers; graphicsGb and systemMemoryGb, for Gemma's per-layer embeddings), speed (card such as " +
            "\"RTX 4070\" or bandwidthGbps: at most bandwidth / bytes read for each token), footprintCatalog (Martlet's own entry for " +
            "the Ollama tag) and measured (what Ollama reported on this PC, model-memory.json in dataDirectory). Keyless; at most five " +
            "requests a lookup, each answer cached for a day, at most 100 requests to each source in five minutes. fixture=true uses " +
            "a FIXTURE transport shaped like Hugging Face and the registry (NOT the real services); without a model it checks the " +
            "estimate against the measured Gemma 4 E2B, E4B and Qwen3.5 4B numbers and rehearses keeping a FIXTURE /api/ps " +
            "measurement in a temporary folder (ok). Downloads no model; saves nothing.", new
        {
            model = new { type = "string", maxLength = 256 },
            huggingFaceRepo = new { type = "string", maxLength = 200 },
            ollamaTag = new { type = "string", maxLength = 200 },
            quantization = new { type = "string", maxLength = 64 },
            contextTokens = new { type = "integer", minimum = 256, maximum = 1_048_576 },
            card = new { type = "string", maxLength = 128 },
            bandwidthGbps = new { type = "number", minimum = 1, maximum = 20_000 },
            dataDirectory = new { type = "string" },
            fixture = new { type = "boolean" }
        },
            async (arguments, cancellation) => await LocalModelFactsCheck.RunAsync(OptionalString(arguments, "model"),
                OptionalString(arguments, "huggingFaceRepo"), OptionalString(arguments, "ollamaTag"), OptionalString(arguments, "quantization"),
                OptionalInt(arguments, "contextTokens"), OptionalString(arguments, "card"), OptionalDouble(arguments, "bandwidthGbps"),
                OptionalString(arguments, "dataDirectory") is null ? null : DataDirectory(arguments), OptionalBool(arguments, "fixture") ?? false,
                cancellation)),
        Tool("model_lab", "A live OpenAI-compatible fixture endpoint on 127.0.0.1 for the desktop on a disposable data directory, so " +
            "Companion > Vision > Image model, Companion > Listening > Audio model and Thinking can choose and test models without a real " +
            "provider (NOT AI). action \"start\" (returns baseUrl, such as http://127.0.0.1:52341/v1, to type as a custom server's API base " +
            "URL), \"status\" or \"stop\" (it also ends with this server). /v1/models lists lab/sees (text and pictures), lab/hears (text and " +
            "recordings), lab/omni (text, pictures, recordings and video), lab/text (text only), lab/notools (text only, no tool calls) and " +
            "lab/gone (retired) with their input modalities and supported_parameters (tools), as OpenRouter lists them. Chat " +
            "completions refuse a picture or a recording a lab model doesn't take (error 400), lab/notools refuses a request with tools " +
            "(error 400), lab/gone answers every chat completion with HTTP 410 Gone, the other tool models call Test tools' made-up tool. " +
            "The lab reads Test vision's word by comparing the " +
            "picture with this PC's own drawings of the test words, reads Test hearing's word with Windows speech recognition limited to " +
            "the test words (when this PC has an English recognizer), and answers anything else with fixed text (streamed when asked). " +
            "status lists the requests by model, the kinds of parts they carried and what the lab answered, never their content. No key " +
            "is needed or read. port (1024-65535) fixes the port for a scripted desktop run; left out, a free one is taken.", new
        {
            action = new { type = "string", @enum = new[] { "start", "status", "stop" } },
            port = new { type = "integer", minimum = 1024, maximum = 65535 }
        }, ["action"],
            async (arguments, cancellation) => await ModelLab.RunAsync(RequiredString(arguments, "action"), OptionalInt(arguments, "port"), cancellation)),
        Tool("local_model_servers", "Companion > Thinking > This PC > A model app you already use, headless: the model apps Martlet looks " +
            "for on this PC by default address (Ollama 11434, LM Studio 1234, llama.cpp 8080, KoboldCpp 5001, Jan 1337, vLLM 8000, Lemonade " +
            "13305 and 8000/api, SGLang 30000, text-generation-webui or TabbyAPI 5000, GPT4All 4891, Docker Model Runner 12434/engines, " +
            "LiteLLM 4000; apps), which answer now on 127.0.0.1 with their models, models Martlet can't use, whether they ask for a key and " +
            "how to start each (found: the production LocalModelServers.DetectAsync, about 1.5 s). address (what the owner would type, " +
            "such as localhost:1234, 8080 or http://127.0.0.1:5001/v1) shows the canonical base URL Thinking takes, or why it is refused " +
            "(an address off this PC), and what that server says. test=true sends the production Test model request (a short streamed " +
            "reply with one tool offered, asked again without it when the app refuses tools) to model at address, or to the first " +
            "found app other than Ollama; it runs the real model on this PC. fixture=true rehearses the same code against fixture " +
            "servers on 127.0.0.1 (NOT AI): llama.cpp without --jinja (refuses tools; the test must retry without them and warn) and " +
            "an app that asks for a key. Loopback only; never a key; saves nothing.", new
        {
            address = new { type = "string", maxLength = 256 },
            model = new { type = "string", maxLength = 128 },
            test = new { type = "boolean" },
            fixture = new { type = "boolean" }
        },
            async (arguments, cancellation) => await LocalModelServersCheck.RunAsync(OptionalString(arguments, "address"),
                OptionalString(arguments, "model"), OptionalBool(arguments, "test") ?? false, OptionalBool(arguments, "fixture") ?? false,
                cancellation)),
        Tool("ollama_model_file", "Companion > Thinking > This PC > Use a model file you downloaded, headless. paths (absolute paths of " +
            "GGUF files the owner would pick, such as a Hugging Face or LM Studio download) says what Martlet would add to this PC's " +
            "Ollama: the files (every part of a split model name-00001-of-0000N.gguf, and a lone mmproj*.gguf projector in the same " +
            "folder so the model can see), the Ollama model name made from the file name and the bytes Ollama would copy, or why it " +
            "refuses; it reads only the folder listing and file sizes and contacts nothing. fixture=true runs the production import " +
            "(SHA-256 of each file, HEAD and POST /api/blobs/sha256:..., then POST /api/create with the files) on FIXTURE files (small " +
            "text, NOT real models) against a fixture Ollama on 127.0.0.1 that checks each digest, plus the refusals (not a GGUF, only " +
            "a projector, a missing part) and an Ollama that refuses the model; ok is all of them. Never touches the real Ollama; " +
            "saves nothing.", new
        {
            paths = new { type = "array", items = new { type = "string", maxLength = 1024 }, maxItems = 16 },
            fixture = new { type = "boolean" }
        },
            async (arguments, cancellation) => await OllamaModelFileCheck.RunAsync(OptionalStrings(arguments, "paths"),
                OptionalBool(arguments, "fixture") ?? false, cancellation)),
        Tool("ollama_recovery",  "Why Ollama on this PC doesn't answer, headless, as the desktop's Home and Companion > Thinking find it " +
            "(the production OllamaRecovery diagnosis): whether it answers on 127.0.0.1:11434, its tray app and server processes, the " +
            "end of its server.log and app.log (failed starts, the last Error: line, the models path of the last start, why the app " +
            "couldn't use its model location), its app database's model location (db.sqlite settings.models, read-only through " +
            "Windows' SQLite), the user's OLLAMA_MODELS and whether a models folder is reached through a junction or symbolic link. " +
            "kind is Answering, NotInstalled, NotRunning, StopsOnStart or CrashLoop; knownCause means Ollama refuses its linked " +
            "models folder; canRepair means the desktop would point Ollama at the real folder. Never stops, starts or changes Ollama, " +
            "its folders, its settings or OLLAMA_MODELS; paths under the user's folders read as %LOCALAPPDATA% and %USERPROFILE%. " +
            "fixture=true also runs the production diagnosis and repair on FIXTURE folders (a junction made with mklink /J, a " +
            "db.sqlite made with Windows' SQLite, Ollama's own log lines, a stand-in for its processes): Linked (repaired: stopped " +
            "before the backup and the change, model location and OLLAMA_MODELS set to the real folder, started, answers), " +
            "UnknownSettings (guidance, nothing changed), OtherError (Ollama's own words, nothing changed) and Answering (nothing " +
            "stopped); passed is all of them.", new
        {
            fixture = new { type = "boolean" }
        },
            async (arguments, cancellation) => await OllamaRecoveryCheck.RunAsync(OptionalBool(arguments, "fixture") ?? false, cancellation)),
        Tool("pc_activity_check", "Hear what this PC plays: which app plays what Martlet hears and what kind of thing it is (a YouTube " +
            "video in a browser, a show or movie in Plex or a player, a game, a voice chat in Discord, a call, music), told apart " +
            "deterministically from the app, the site its browser window shows, where its program is installed (a game library, or " +
            "a game engine's files beside it), " +
            "the window in front and full screen, and how busy it keeps the graphics card. live: the production PcActivityMonitor and " +
            "WindowsPcActivitySource on this PC for seconds (default 3): every app with an audio session (the volume mixer's meters " +
            "only; nothing is recorded, played, kept or sent; raw window titles are never returned) with its kind and label, the " +
            "average tick cost, the summary and the note a reply reads (the context board's activity note). rehearsal (FIXTURE apps, " +
            "levels and lines on a simulated clock): the classifier on fixed apps, where fixture lines came from as their " +
            "[PC audio] From ...: labels, the note, the speakers check (PcEcho.Speakers on a simulated clock: a line the microphone " +
            "heard at the same moment as what only a video, show, game or music played is the speakers; never earlier words, a voice " +
            "chat, a voice changer or your own voice played back) and the " +
            "built-in What this PC plays and Always listening prompts. Reads no credentials and contacts nothing.", new
        {
            seconds = new { type = "integer", minimum = 1, maximum = 20 }
        },
            async (arguments, cancellation) => await PcActivityCheck.RunAsync(OptionalInt(arguments, "seconds"), cancellation)),
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
        },
            async (arguments, cancellation) => await VisionHistoryCheck.RunAsync(DataDirectory(arguments), OptionalString(arguments, "reply"), cancellation)),
        Tool("active_app_check", "The program in front and whether it is full screen, as Martlet's screen glances tell the " +
            "Thinking model (docs/SCREEN_COMMENTARY.md), read with the desktop's production ActiveApp: inFront (the window in " +
            "front now: found, app, fullScreen, told (the words a look sends) and readMs (how long reading it took, first and " +
            "again); never its title), sample (the name explorer.exe " +
            "gets), rules (the full-screen rule on FIXTURE windows: borderless, maximized with and without a title bar, smaller " +
            "than its monitor, on a second monitor) and prompts (Companion > Prompts > Screen glance message and Active app " +
            "with your message as a data directory's settings.json fills them for a FIXTURE full-screen game, and the [Screen] " +
            "line the conversation keeps) and sinceLook (the desktop's production LookChange on FIXTURE thumbnails: per " +
            "scenario, since (First, Same, Little or Changed), looks (false while nothing changed since the last look, so " +
            "Martlet doesn't look), picture (how much the thumbnail changed) and changed (the words a look's message gets)). " +
            "Reads no credentials and contacts nothing.", new
        {
            dataDirectory = new { type = "string" }
        },
            async (arguments, cancellation) => await ActiveAppCheck.RunAsync(DataDirectory(arguments), cancellation)),
        Tool("screen_digest_check", "The screen summary over time (docs/SCREEN_COMMENTARY.md), run once with the desktop's " +
            "production ScreenDigester on FIXTURE frames (made-up pictures of a code editor, then a game whose health drops; no " +
            "screen capture) and a FIXTURE thinker and context board (no model, nothing sent). Returns the setting (Companion > " +
            "Vision > Screen summary over time, from talk-preferences.json, on by default), which frames the ring kept or skipped, " +
            "the job (reason, frames, contact sheet size and bytes, the message as Companion > Prompts > Screen summary over time " +
            "in settings.json makes it), the answer (reply, or a FIXTURE sentence) as parsed, what went to the board (text, " +
            "max age), the status (frames, last text, age, time taken, jobs, posted, dropped) and the talk window's line, then " +
            "that a stale answer is dropped. Reads no credentials and contacts nothing.", new
        {
            dataDirectory = new { type = "string" },
            reply = new { type = "string", maxLength = 1024 }
        },
            async (arguments, cancellation) => await ScreenDigestCheck.RunAsync(DataDirectory(arguments), OptionalString(arguments, "reply"), cancellation)),
        Tool("game_progress_check", "Where the user is in a game (docs/CONVERSATION.md, Where you are in a game): the running note " +
            "a Thinking pool member keeps from the screen summaries while a game is in front, rehearsed with the production note " +
            "keeper (GameProgress) and context board and a FIXTURE pool member (canned answers, NOT AI; nothing is sent). Two " +
            "FIXTURE screen summaries make a note and a newer note built on it, \"[same]\" keeps it, a bad answer leaves the board " +
            "as it was. Returns each update, whether the second request carried the earlier note, whether the instructions stay " +
            "the same, the board's sources and game note (source game, right after screen) and the limits. reply (optional) is " +
            "cleaned like a pool member's answer and shown as the board line. Live updates log \"Game progress: updated the note\" " +
            "in the desktop log. Reads no credentials and contacts nothing.", new
        {
            reply = new { type = "string", maxLength = 2048 }
        },
            async (arguments, cancellation) => await GameProgressCheck.RunAsync(OptionalString(arguments, "reply"), cancellation)),
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
        },
            async (arguments, cancellation) => await ContextBoardCheck.RunAsync(OptionalString(arguments, "source"), OptionalString(arguments, "text"),
                OptionalInt(arguments, "maxAgeSeconds"), OptionalBool(arguments, "consume"), OptionalInt(arguments, "ageSeconds"), cancellation)),
        Tool("context_check", "The Thinking model's context as Martlet uses it, from a data directory: the saved route, Companion > " +
            "Replies > Context size, what model-limits.json says about the model (from Check model limit, choosing or testing a " +
            "model, or Ollama loading it) and the context size, reply room and text room replies get (the production ContextBudget), " +
            "and how much room a screen glance has (look: the persona's and the picture's estimated tokens, the room left for " +
            "Martlet's instructions, message and notes, what a look leaves out when it doesn't fit, and a problem when the picture " +
            "and the persona alone don't fit). " +
            "Then rehearses the production model-limit check (ModelContextProbe) against fixture servers on 127.0.0.1 shaped like " +
            "OpenRouter, vLLM, Groq, llama.cpp and Ollama (NOT the real services; a fixture key goes only to its own base URL and " +
            "redirects aren't followed) and the production history fit of a 1,000-exchange synthetic conversation into a cloud " +
            "model's default, a 1,000,000-token setting, a paired host and Ollama on this PC. Loopback only; reads no credentials.", new
        {
            dataDirectory = new { type = "string" }
        },
            async (arguments, cancellation) => await ContextCheck.RunAsync(DataDirectory(arguments), cancellation)),
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
        },
            async (arguments, cancellation) => await ThinkingStepsCheck.RunAsync(DataDirectory(arguments), OptionalString(arguments, "model"),
                OptionalBool(arguments, "live") ?? false, cancellation)),
        Tool("said_lately_check", "Rehearse what Martlet said lately (docs/CONVERSATION.md#what-you-said-lately) with the " +
            "production code and FIXTURE sayings at fixed times (NOT anything Martlet said): what is noted (never a [pass] or " +
            "nothing; one line, cut to 160 characters; the newest 10 within the hour), the lines with the time of day and how long " +
            "ago (\"10:05 PM (12 min ago)\"), the note through Companion > Prompts > What you said lately (nothing when it is " +
            "emptied or nothing was said), which requests carry it (looks, reports and remarks on what this PC plays; never a " +
            "reply to the user's words or touches, so their first words never wait), where it sits in a request (the last notes, " +
            "sent once and never kept) and the Saying the same things check-in reading the same lines. No model, network or " +
            "credentials.", new { },
            async (_, cancellation) => await SaidLatelyCheck.RunAsync(cancellation)),
        Tool("helper_jobs_status", "Where Martlet's helper jobs ran last, from a data directory's helper-jobs.json (written by the " +
            "desktop): for each kind (memory: remembering and learning names after a reply; action-naming: naming a character's " +
            "emotes; touch-zones: finding its touch zones in one picture; character-writing: writing a character's personality in " +
            "Find a character) its priority, whether it ran on a Thinking pool member " +
            "(route pool, with the member) or on the conversation's own Thinking model after the reply finished speaking (route " +
            "fallback, with how long it waited for the reply), how it ended and when. Never a prompt or answer. Read-only.", new
        {
            dataDirectory = new { type = "string" }
        },
            arguments => HelperJobsCheck.Status(DataDirectory(arguments))),
        Tool("helper_jobs_check", "Rehearse the desktop's production helper-job router (HelperJobs) with a fixture Thinking pool and " +
            "fixture answers (NOT AI): memory and emote naming go to a free text member, touch zones wait for a running reply and " +
            "fall back while no member sees pictures, then go to a vision member, and memory falls back when no member is free. " +
            "Returns each step's route, member, outcome and wait, and the helper-jobs.json it wrote. No model, network or credentials.",
            new { },
            async (_, cancellation) => await HelperJobsCheck.RunAsync(cancellation)),
        Tool("think_longer_status", "Companion > Deep thinking > Thinking longer (think_longer: Martlet decides, sparingly, to think a " +
            "task through in the background while the conversation carries on), from a data directory: the settings replies use " +
            "(on by default, Off from Thinking longer's own box; effort, time limit, hourly limit, when it shares the result) and whether any " +
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
        },
            async (arguments, cancellation) => await ThinkLongerCheck.StatusAsync(DataDirectory(arguments), cancellation)),
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
        },
            async (arguments, cancellation) => await ThinkLongerCheck.RunAsync(OptionalInt(arguments, "reasoningMs"), cancellation)),
        Tool("thinking_pool_status", "Companion > Background thinking from a data directory: thinking-pool.json (or what Martlet would make " +
            "from the older deep-thinking.json, without writing it): each member (a paired computer's Thinking pool role or Ollama, " +
            "a model in Ollama on this PC or an OpenAI-compatible endpoint; never a key) with its slots, whether it sees pictures " +
            "or hears recordings, whether it can run and why, and its Quick jobs and Long jobs boxes; the machines the owner turned " +
            "off (offMembers: each one's key, where, model, graphics card and boxes; they take no jobs, and with no member on the " +
            "pool is off: poolOff); whether thinking longer " +
            "and research use the conversation model when no member takes long jobs; the usable slots " +
            "and whether one is kept free for fast jobs (judges and summaries); each job kind's priority, whether it is fast and " +
            "whether a member that takes that kind can run it (the cheap CanRun answer); kinds: the one table of job kinds (each " +
            "kind's saved key, Runs on label, priority, needs, live floor rule and the helper jobs that run as it); guidance (such as 1 slot: long thinking can delay screen and " +
            "sound summaries); warnings about likely slowdowns (a member beside the conversation's Thinking model or the voice); " +
            "Backup Thinking's choices (on or off, the delay or automatic, each member's Backup for slow replies box and whether " +
            "it is a paid cloud provider) and the member it would ask now for a reply that is taken; " +
            "and the desktop's thinking-pool-status.json (running and waiting jobs by kind, never a job's text; Backup Thinking's " +
            "automatic delay, recent replies and how it ended lately). From that file, each member's online state (whether its " +
            "computer answers now, and since when it doesn't) and the pool's slots now against its slots when every computer " +
            "answers (presence). Read-only.", new
        {
            dataDirectory = new { type = "string" }
        },
            async (arguments, cancellation) => await ThinkingPoolCheck.StatusAsync(DataDirectory(arguments), cancellation)),
        Tool("thinking_pool_check", "Rehearse the Thinking pool's job board (ThinkingJobBoard over BackgroundPlaces, the production " +
            "code) with simulated members, NOT models: an empty pool answering no member at once, a picture going to the member " +
            "that sees and a recording finding none, two slots where a second long job waits while a judge takes the last free " +
            "slot, one slot where waiting jobs run highest priority first (barge-in judge, digest, research), a busy member passed " +
            "over for the next, a stale judge dropped, deep-thinking.json read once into thinking-pool.json, paired hosts joining " +
            "the pool by themselves (ThinkingPoolAutoJoin, sample hosts), a graphics card the owner turned off staying off while the " +
            "computer's other card keeps working, and presence: a " +
            "member's computer going offline (its slots leave the pool, jobs go to the others and wait for it, the last-free-slot " +
            "rule counts only computers that answer, every computer offline lets the conversation model stand in) and answering " +
            "again (its slots come back and a job waiting in line starts there), while the think_longer tool text stays " +
            "byte-identical; and quick and long jobs: each kind goes only to a member ticked for it, a long job on a member that " +
            "takes no quick jobs keeps no slot free, and a member that leaves takes its boxes with it; and the request journal " +
            "(ThinkingRequests, which the Thinking requests page and thinking-requests.json show): a success, a retry on another " +
            "member (two tries) and a stale drop recorded with plausible timings and counted in the totals by kind. In-process; " +
            "reads nothing.", new { },
            async (_, cancellation) => await ThinkingPoolCheck.RunAsync(cancellation)),
        Tool("thinking_requests", "The Thinking requests page from a data directory: the desktop's thinking-requests.json (every " +
            "request for the Thinking pool's slots it keeps, waiting and running first, then the newest ended ones: id, kind, " +
            "source, task, companion, priority, state and note, line position, each try's member, model, start, end and ending, " +
            "retries, stops for the conversation, how long it waited and ran in ms, the input's and the answer's lengths; and the totals by kind " +
            "since Martlet started). Never a request's text, input, answer or topic. state is none (no file yet), unreadable or loaded. " +
            "Filters: state (active, done, problems or one state such as succeeded or stale), kind (a job kind such as memory) " +
            "and limit (1-200, default 50). Read-only.", new
        {
            dataDirectory = new { type = "string" },
            state = new { type = "string", @enum = ThinkingPoolCheck.RequestStates },
            kind = new { type = "string", @enum = ThinkingPoolCheck.RequestKinds },
            limit = new { type = "integer", minimum = 1, maximum = 200 }
        },
            arguments => ThinkingPoolCheck.Requests(DataDirectory(arguments), OptionalString(arguments, "state"),
                OptionalString(arguments, "kind"), OptionalInt(arguments, "limit"))),
        Tool("sense_models_status", "Companion > Vision's image models and Companion > Hearing's audio models from a data directory " +
            "(docs/SENSE_MODELS.md): the Vision and Hearing lists in pools-local.json (file: lists; before the desktop made them, " +
            "sense-models.json: loaded; none or unreadable), each kind's list as saved (each member's key, kind, on, model, engine, " +
            "whether pictures and recordings may go there, whether the owner agreed, whether this PC can use it; never a key), the " +
            "Thinking model and whether it sees and hears, and for pictures and recordings where they go now (Thinking: in the " +
            "text model's own request; Described: the first model in the list that may take it puts them into words for Thinking; " +
            "None: nowhere) and why, with what that model is known to do (model-abilities.json, with where that came from). Each " +
            "kind's pool (SensePool, lane vision or hearing): the places a job tries in order (the chosen model, then the list's " +
            "other models that may take the kind, never the Thinking model). With the desktop's sense-models-status.json: each " +
            "lane's recent jobs (purposes, outcomes and times; never what was sent or said), whether the model shares the " +
            "conversation's computer, and how many jobs went to another member or waited and which member took the last one " +
            "(position, busy and unavailable counts, wait). Read-only: it never makes the lists.", new
        {
            dataDirectory = new { type = "string" }
        },
            async (arguments, cancellation) => await SenseModelsCheck.StatusAsync(DataDirectory(arguments), cancellation)),
        Tool("sense_models_check", "Rehearse the image and audio models' production routing (SenseRouting) over the combinations " +
            "of text, image and audio models with fixture model names (the defaults with an omni or a text-only Thinking model, an " +
            "image model only, separate image and audio models, the audio model the same as an image model that can't hear, one " +
            "model for both, same-as both ways, a model of its own that is exactly Thinking's, a paired computer, what Martlet found " +
            "out winning over names), the sense-models.json round trip and the Vision and Hearing lists made from it once (a " +
            "damaged list file read as the text model), and the production lanes (SenseLanes) with a simulated " +
            "runner, NOT models: no model of its own answers at once, one job at a time, a newer picture replacing the one " +
            "waiting, priorities, a stale job dropped, refusals, failures, timeouts, the kind check, one lane for one model used " +
            "for both kinds, and the conversation first (a job waits while a reply holds the model's hardware, a running job is " +
            "stopped when a reply starts, a job the hold outlasts is dropped), and a background job (a helper, priority below zero) " +
            "giving way to a reply's picture and starting again, while a summary waits behind it. Then the pools (SensePool) with " +
            "simulated members on their own queue: who is a member (a Vision list's members as places: a computer not paired here, a " +
            "cloud provider without agreement, a server not allowed pictures, a member off or kept for another PC left out; the " +
            "first model that may see chosen; one known not to see and the Thinking model left out after it), a free chosen model " +
            "taking the job with one request, a busy or unreachable member passing it on at once, a busy pool waiting for whichever " +
            "member frees first, and a member held for a live turn passed over. In-process; reads nothing.", new { },
            async (_, cancellation) => await SenseModelsCheck.RunAsync(cancellation)),
        Tool("image_model_check", "The image model (docs/SENSE_MODELS.md, Pictures: the image model) from a data directory: where " +
            "pictures go now (sense-models.json, the Thinking route and model-abilities.json through the production SenseRouting, or " +
            "an image model of its own given as imageOrigin and imageModel), the state of its three prompts, the desktop's " +
            "image-model-status.json (described replies and replies that went without, looks, the newest description's age and time; " +
            "never a description) and the newest \"Picture path:\" and \"Image model:\" log lines. Then a rehearsal with the " +
            "production SenseLanes, PictureDescriptions, prompts and Chat Completions adapter against two fixture endpoints on " +
            "127.0.0.1 (the image model and Thinking; canned words, NOT AI) and synthetic screenshots (no screen capture): a " +
            "description made ahead of time when you start to talk, a described reply (no picture to Thinking, the note and the " +
            "fixed Pictures as words instruction, no seen tag, the kept [Screen] line), a reply that doesn't wait while the image " +
            "model is still describing a changed picture (delayMs, default 1500), and a look in two stages (the description of the " +
            "same picture used again, then a new picture described first). Reads no credentials; contacts only its own fixtures.", new
        {
            dataDirectory = new { type = "string" },
            imageOrigin = new { type = "string" },
            imageModel = new { type = "string" },
            delayMs = new { type = "integer" }
        },
            async (arguments, cancellation) => await ImageModelCheck.RunAsync(DataDirectory(arguments), OptionalString(arguments, "imageOrigin"),
                OptionalString(arguments, "imageModel"), OptionalInt(arguments, "delayMs"), cancellation)),
        Tool("audio_model_check", "Rehearse Companion > Listening > Audio model (docs/SENSE_MODELS.md, Recordings: the audio model) " +
            "with the production pieces against fixture Chat Completions endpoints on 127.0.0.1 (canned answers, NOT AI): where " +
            "recordings go for the audio model's choices (SenseRouting: an audio model of its own means Thinking never gets a " +
            "recording), the hearing consent (never chosen, only an audio model in Ollama on this PC hears you), and the desktop's own " +
            "voice notes through SenseLanes and the Chat Completions adapter: the audio model gets the recording with its fixed " +
            "instructions and the last lines said; Thinking gets the transcript and the audio model's words as a note sent with that " +
            "request only, and the conversation keeps a short (voice: ...) line; words that come late go to the context board for the " +
            "next request while the reply never waits; none adds nothing; a refused recording ends Refused. A synthesized clip, never " +
            "a microphone; nothing leaves loopback; no credentials. Optional absolute dataDirectory: where recordings go with its " +
            "saved choices.", new
        {
            dataDirectory = new { type = "string" }
        },
            async (arguments, cancellation) => await AudioModelCheck.RunAsync(OptionalString(arguments, "dataDirectory") is null ? null : DataDirectory(arguments),
                cancellation)),
        Tool("backup_thinking_check", "Rehearse Backup Thinking (Companion > Background thinking, a hedged request: when the " +
            "conversation's Thinking model has no first words after the delay, the same request also goes to a pool member that " +
            "may answer for the conversation, and whichever starts first gives the reply) with the production race in " +
            "ConversationTurn, ConversationRuntime.OpenTextAsync and the production member choice (ThinkingBackupMembers) on " +
            "fixture turns: two fixture Chat Completions endpoints on 127.0.0.1 answer after set waits (canned words, NOT AI) and " +
            "note when the client stopped their stream. Scenarios: backup-wins (the conversation's model is slow: the member is " +
            "asked at the delay and answers, the conversation's stream is stopped, the latency line says Backup Thinking won), " +
            "conversation-wins (fast: no member asked), late-conversation (the conversation's model starts after the member was " +
            "asked: the member is stopped), conversation-fails (it fails after the member was asked: the member answers), " +
            "no-member (none may answer), open-fails (a member is chosen but its stream can't be opened: Failed with the reason " +
            "in why, not no member), held (a reply started early with only a paid cloud member: asked only once the reply is " +
            "taken), let-go (a reply started early and let go: its member's stream stops with it) and members (the rules: off, no " +
            "member ticked, a cloud member never unless ticked, a member on the conversation's computer passed over, tools only on " +
            "an endpoint). scenario runs one; delayMs is one of 500, 700, 900, 1200, 1500, 2000, 3000 (900 by default). Loopback " +
            "only; plays nothing.", new
        {
            scenario = new { type = "string", @enum = BackupThinkingCheck.Scenarios },
            delayMs = new { type = "integer", @enum = new[] { 500, 700, 900, 1200, 1500, 2000, 3000 } }
        },
            async (arguments, cancellation) => await BackupThinkingCheck.RunAsync(OptionalString(arguments, "scenario"), OptionalInt(arguments, "delayMs"), cancellation)),
        Tool("live_floor_status", "The live floor (the live conversation turn comes before all background work) from a data directory: " +
            "what the conversation runs on (its Thinking, voice and listening routes, each on this PC, a computer on the home network " +
            "or a cloud provider, with the paired hosts and routes the floor holds while you talk), which Thinking pool members share " +
            "that hardware, what the floor does to each job kind on such a member at Listening and at Live, and the desktop's " +
            "live-floor.json: its level (Idle, Listening, Live), the jobs it held and stopped by kind this turn and in all, the hold " +
            "client and hosts held, the work queue's stopped background requests and its last changes (never what was said). Read-only.", new
        {
            dataDirectory = new { type = "string" }
        },
            async (arguments, cancellation) => await LiveFloorCheck.StatusAsync(DataDirectory(arguments), cancellation)),
        Tool("live_floor_check", "Rehearse the live floor with the production LiveFloor, LiveFloorRules, ThinkingJobBoard, BackgroundJobs " +
            "and WorkQueue on fixture inputs and simulated members, NOT models: which things said are real words (said: your own " +
            "lines, else fixtures such as \"Mmm.\", \"Yeah, right.\" and \"What time is it in Tokyo?\"), the levels on a clock of their " +
            "own (voice, quiet, a sound, words, a reply and its grace), the microphone on the production detector (a click, typing " +
            "and Martlet's own voice stay Idle; 300 ms of voice is Listening), the board at Listening (new work waits, running work and " +
            "judges go on) and at Live (a summary dropped, remembering and naming stopped and queued again, touch zones going on, " +
            "judges running), a member on another computer never held, a think stopped and going on from what it wrote (in place, " +
            "or again with it as context), research waiting for the conversation instead of being refused, the conversation " +
            "model's own place held above Idle, and the work queue stopping this PC's background request for a live reply. " +
            "In-process; reads nothing.", new
        {
            said = new { type = "array", items = new { type = "string" }, maxItems = 32 }
        },
            async (arguments, cancellation) => await LiveFloorCheck.RunAsync(OptionalStrings(arguments, "said")?.Take(32).ToArray(), cancellation)),
        Tool("research_check", "Rehearse web research (the research tool: Companion > Deep thinking > Web research, on by default) " +
            "end to end with Martlet's own tool texts and job kind (WebResearch: one at a time, no hourly limit, no time limit, " +
            "offered when done), background-job scheduler, web client (WebAccess: DuckDuckGo results parser with ads left out and redirect links " +
            "unwrapped, page reader keeping readable text, public-address guard on every connection and redirect), research loop " +
            "(WebResearchRun: first search and pages, then model steps that rewrite their notes and answer SEARCH, READ or the " +
            "report, each a background think through " +
            "the conversation runtime and Chat Completions adapter), report creation and its web page (ResearchReports), against " +
            "fixtures on 127.0.0.1 (a search page, web pages including a PDF and a redirect to a private address, and a model with " +
            "canned answers, NOT AI): a reply says it'll look into it and calls research (returns at once, the reply completes while " +
            "the job runs), the steps' requests (the second carries the first one's notes and only the pages read since), the note " +
            "the conversation gets (offer first, perform_creation with the report's " +
            "id), the report kept in a temporary Creations library and shown as a page; plus the settings (on by default, off when " +
            "turned off or with Thinking longer off), the address guard, the limits (busy beside a think, Cancel, no hourly limit, " +
            "a failed search, and placement on Deep thinking's places, where research never takes the pool's last free slot, kept " +
            "for quick jobs), the budget (an in-memory web and fixture model reading until the production budget of pages runs " +
            "out: searches, pages, model steps, notes and the largest step's bytes) and the levels (the depth parameter and the " +
            "reply's prompt for answering, a quick look-up, full or deep research; each depth's budget and job kind; a quick look-up " +
            "beside research that ends on the watcher's answer found in a long page's matching parts; the early finding notice " +
            "while research goes on; and the quiet source check that starts only after the report's result is ready and adds its " +
            "section to the kept report). " +
            "Loopback only; no real search or model; reads no credentials.", new { },
            async (_, cancellation) => await ResearchCheck.RunAsync(cancellation))
    ];
}
