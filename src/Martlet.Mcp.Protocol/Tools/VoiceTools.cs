using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Martlet.Core.Installation;
using Martlet.Doctor;
using Martlet.Mcp.Client;

namespace Martlet.Mcp;

/// <summary>martlet_guide's "Voice and listening" area: speaking, listening, hearing, singing and the sounds Martlet makes.
/// Each Tool(...) registers one tool: its name, description, input schema, handler and, when it needs one, its permission (<see
/// cref="ToolPermission"/>). A new tool of this area goes here.</summary>
internal sealed partial class McpServer
{
    private McpTool[] VoiceTools() =>
    [
        Tool("voices_status", "Read voice recognition and Parakeet status from a data directory: on/off choices (recognition is on " +
            "unless turned off), whether a Martlet folder (optional absolute martletDirectory, default the installed release's Desktop " +
            "folder) includes the voice recognition runtime and models, whether any Parakeet model is downloaded (parakeet) and, in " +
            "parakeetModels, each model Companion > Listening > Parakeet in Martlet offers (id, name, languages, download size, " +
            "downloaded, its NOTICE, recommended for Windows' display language, in use), the Listening route and its Parakeet model, " +
            "the Parakeet model that hears you on this PC's processor when that route (a paired host or OpenAI) fails, or why none " +
            "(standIn), recognitionWhere (Companion > People > Where it runs: the Voice recognition list in pools.json, its members, " +
            "the places this PC tries in order with this PC's processor last as the fallback, and desktop: the running desktop's " +
            "voice-recognition-state.json with this PC's engine state, problem, failures, retry time, reloads and speed, the last " +
            "recognition's place, milliseconds and the places passed over, and each computer's last answer or rest), " +
            "sharing (the voice list always syncs with your paired hosts, whatever Keep Martlet the same on all my " +
            "computers says: state, hosts, and friendHostsNeverUsed for hosts a friend shares), " +
            "and counts of known voices (never names, voiceprints or audio), including how many go by a name of the companion's own " +
            "(from the saved personas) or a placeholder such as \"no name yet\", and the most names one voice has; and clips: whether " +
            "People keeps the last few clips of voices not named yet (voice-clips.txt) and how many clips over how many voices (never " +
            "the audio). Read-only; no audio, network or models run.", new
        {
            dataDirectory = new { type = "string" },
            martletDirectory = new { type = "string" }
        },
            arguments => VoicesStatus(arguments)),
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
        },
            async (arguments, cancellation) => await TurnJudgeCheck.RunAsync(arguments, MartletDirectory(arguments), cancellation)),
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
        },
            async (arguments, cancellation) => await EarlyReplyCheck.RunAsync(OptionalString(arguments, "scenario"), OptionalInt(arguments, "thinkingMs"),
                OptionalInt(arguments, "voiceMs"), OptionalInt(arguments, "sttMs"), OptionalInt(arguments, "judgeMs"), cancellation)),
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
        },
            async (arguments, cancellation) => await ParakeetCheck.RunAsync(arguments, DataDirectory(arguments), MartletDirectory(arguments),
                OptionalString(arguments, "speechDirectory") is not null ? SpeechDirectory(arguments) : Path.Combine(DataDirectory(arguments), "speech"),
                cancellation)),
        Tool("voices_people", "Run the People check-in tools (Companion › Check-ins, tool set people; learning names uses them too) " +
            "on a data directory's saved voice list, exactly as a check-in gets them: tool people_list (optional query: a name, part " +
            "of one, or a tag like V3; at most 20 voices, most recently heard first) or people_lately (optional minutes, 1-10080, " +
            "default 60). Returns the tool's answer: each voice's tag, the name Martlet uses first, its other names (typed by the " +
            "owner or learned), whether it is the signed-in user (accounts\\session.json) or linked to an account, and how often and " +
            "when it was last heard; firstLine is what a check-in card shows (counts only). Names are personal: this reads them from " +
            "the data directory you give. Never voiceprints, clips or audio; read-only.", new
        {
            dataDirectory = new { type = "string" },
            tool = new { type = "string", @enum = new[] { Martlet.Conversation.PeopleTools.List, Martlet.Conversation.PeopleTools.Lately } },
            query = new { type = "string", maxLength = 100 },
            minutes = new { type = "integer", minimum = 1, maximum = Martlet.Conversation.PeopleTools.MaximumLatelyMinutes }
        },
            arguments => VoicesPeople(arguments)),
        Tool("voices_naming_check", "Rehearse learning names (Companion › People) with the production checks and changes on a fixture " +
            "voice list in memory: the companion's own names (persona names, \"You are ...\" in persona text, \"I'm ...\" in Martlet's " +
            "reply) are never given to a voice, a heard voice drops one it learned by mistake, a voice keeps many names and shows the " +
            "one it asked for (CALL), a wrong learned name is dropped (NOT) but never one the owner typed, names go only to voices " +
            "heard, two voices merge into the owner's (SAME) at most once per exchange, and a SPOKE line (whose voice said a message " +
            "Martlet wasn't sure of) only labels it: it is refused when Martlet knew who spoke or names a voice that couldn't have, " +
            "and it never changes a name, voiceprint, sample or heard count. The people tools (people_list, people_lately) run on " +
            "the same fixture: names with typed or learned, the user, heard counts, a first line with counts only and no voiceprint. " +
            "Optional dataDirectory supplies the saved personas' names (one more scenario); optional answer (a Thinking answer of " +
            "NAME/CALL/NOT/SAME lines about V1-V3) and reply (Martlet's reply) are checked against the same fixture. Never reads or " +
            "writes the saved voice list; no audio, model or network.", new
        {
            dataDirectory = new { type = "string" },
            answer = new { type = "string" },
            reply = new { type = "string" }
        },
            arguments => VoiceNamingCheck.Run(DataDirectory(arguments), OptionalString(arguments, "answer"),
                OptionalString(arguments, "reply"))),
        Tool("voices_engine_check", "Run the voice recognition engine that ships in a Martlet folder (optional absolute " +
            "martletDirectory, default the installed release's Desktop folder): load its sherpa-onnx runtime and the WeSpeaker and " +
            "pyannote models, take a voiceprint of a generated test tone and, for optional wavFiles (absolute paths to 16 kHz mono " +
            "PCM16 WAV files of at most a minute), how many voices each has, their clean seconds and how alike the files' main " +
            "voices are (cosine; Martlet calls 0.70 the same person). Returns counts and scores only, never audio or voiceprints; no " +
            "data directory, device or network is used.", new
        {
            martletDirectory = new { type = "string" },
            wavFiles = new { type = "array", items = new { type = "string" } }
        },
            async (arguments, cancellation) => await Task.Run(() => VoicesEngineCheck(arguments), cancellation)),
        Tool("voice_recognition_check", "Companion > People > Where it runs (docs/VOICES.md#where-voice-recognition-runs): rehearse " +
            "the Voice recognition list with the production code and SIMULATED computers and engine (NOT real hosts): the order a " +
            "companion PC tries (each computer once, this PC's processor always last as the fallback, also with no list, an empty " +
            "list or nothing on), the list shared by two simulated computers, the request (operation, sample_rate, pcm16_base64) and " +
            "the answer checks (256 numbers of length 1, times within the audio, sane counts; a bad answer is refused), routing " +
            "through the work queue (a computer that answers in time recognizes; a busy one passes it to this PC at once; a slow one " +
            "loses to this PC after its head start and rests; a bad answer or an unreachable computer is passed over), and keeping " +
            "this PC's engine running (one failure: loaded again at once; repeated failures: it rests, tries again in 5 s, a computer " +
            "after it recognizes, and the check brings it back). With optional absolute martletDirectory it also runs the real engine " +
            "of that Martlet folder through the keeper: loadMs, analyzeMs of 3 s of a tone, routedMs (the same through the router " +
            "with this PC alone, the default list), msPerSecond and the head start a computer gets. Nothing is recorded, played or " +
            "sent; no data directory is used. For a data directory's own list and the running desktop's state, see voices_status's " +
            "recognitionWhere.", new
        {
            martletDirectory = new { type = "string" }
        },
            async (arguments, cancellation) => await VoiceRecognitionCheck.RunAsync(
                OptionalString(arguments, "martletDirectory") is not null ? MartletDirectory(arguments) : null, cancellation)),
        Tool("voice_recognition_engine_check", "Voice recognition on another computer, through a REAL voice-recognition host role's " +
            "service on a numeric loopback endpoint (default http://127.0.0.1:50091/: workers/voice-recognition, on its NVIDIA card or " +
            "processor): two Windows voices (System.Speech rendered to memory, never played) speak alone, in turns, over each other " +
            "and for 1.4 s only, and each clip goes through the production path: the role's relay (Martlet.Gateway.VoiceRecognition) " +
            "inside a real gateway on 127.0.0.1 (pinned TLS, pairing) and the desktop's paired client (HostVoiceRecognition), which " +
            "checks every answer as Companion > People's list does. Returns the service's /status, the route's model, the role's " +
            "state and provider (cuda or cpu) through the gateway, how long it took to be ready, and per clip the voices, recognized " +
            "speakers with their clean seconds and times, overlap, whether a whole voiceprint and a voiceprint came back, and " +
            "analyzeMs, voiceprintMs and the host's own ms. With optional absolute martletDirectory (a Martlet folder with its " +
            "voice-recognition files) it also runs this PC's engine on the same audio and compares: the same voices and speakers, " +
            "times within 0.05 s and voiceprints with a cosine of at least 0.995 (comparedWithThisPc). Loopback only; runs " +
            "Martlet.NodeLinkCheck; nothing is recorded, played or kept.", new
        {
            endpoint = new { type = "string", maxLength = 64 },
            martletDirectory = new { type = "string" }
        },
            async (arguments, cancellation) => await VoiceRecognitionEngineCheck.RunAsync(OptionalString(arguments, "endpoint"),
                OptionalString(arguments, "martletDirectory") is not null ? MartletDirectory(arguments) : null, cancellation)),
        Tool("voice_recording_check", "What Companion > Voice > Add a voice does with an audio or video file (path, read on this PC): " +
            "whether it is readable, the kind of file (Ogg files name their codec: OGG (Vorbis), OGG (Opus)), its channels and sample " +
            "rate, and the mono 16-bit WAV Martlet would keep and Play plays (sample rate, length, bytes, SHA-256, peak and RMS level " +
            "in dBFS, kept as is or converted), with the line Add a voice shows, or why it can't be used. Uses " +
            "the production converter; saves, plays and contacts nothing and never returns the path or the audio.", new
        {
            path = new { type = "string" }
        }, ["path"],
            async (arguments, cancellation) => await VoiceRecordingCheckAsync(RequiredString(arguments, "path"), cancellation)),
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
                        "nod.\"). A [pass] after words is dropped from what is shown and spoken; quiet says whether the reply stays " +
                        "quiet as a [pass]. Synthesizes and contacts nothing.", new
        {
            text = new { type = "string" }, engine = new { type = "string" }, dataDirectory = new { type = "string" },
            characterTags = new { type = "array", items = new { type = "string" } }, persona = new { type = "string" },
            breaks = BreaksSchema()
        }, ["text"],
            arguments => VoiceTagsCheck(arguments)),
        Tool("speaking_voices_selftest", "Rehearse the shared speaking voices end to end with the production code: two real gateways on " +
            "127.0.0.1 (pinned TLS, the real reference-voice relay route over a fixture voice service, NOT AI, with in-memory " +
            "speaking-voices.json and recordings) and two simulated desktops with real F5 voice stores in a temporary folder, using the " +
            "desktop's paired client and Martlet.F5's reconcile engine. Checks the starter voices, sharing the list and recordings, " +
            "speaking by recording SHA-256 alone, the one-time fallback that sends a recording a host lacks, a new desktop taking every " +
            "voice from a host, the shared choice, removal everywhere (host and desktop copies deleted), stale copies, retired starter " +
            "voices leaving an older list, a new starter voice joining an older list once, a host restart and upload checks. Loopback " +
            "only; the temporary folder is deleted and the credential vault is not touched.", new { },
            async (_, cancellation) => await NodeLinkCheckAsync(cancellation, "voices")),
        Tool("audio2face_check", "Animate a short synthesized speech-like test signal (generated here; no microphone, nothing played) " +
            "with an Audio2Face service on a numeric loopback endpoint (default http://127.0.0.1:52000) through Martlet's production " +
            "Audio2Face client, the one the host gateway's lip-sync relay uses, so either Audio2Face engine (the local open-source SDK " +
            "service or NVIDIA's NIM) can be checked. Returns ok, frames, frames per second, the frame time span, how many blendshape " +
            "channels came back and moved, the jawOpen peak, the strongest channels and timings, or the failure category. Loopback only.", new
        {
            endpoint = new { type = "string", maxLength = 64 },
            seconds = new { type = "integer", minimum = 1, maximum = 10 },
            sampleRate = new { type = "integer", @enum = Audio2FaceCheck.SampleRates }
        },
            async (arguments, cancellation) => await Audio2FaceCheck.RunAsync(OptionalString(arguments, "endpoint"),
                OptionalInt(arguments, "seconds"), OptionalInt(arguments, "sampleRate"), cancellation)),
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
        },
            async (arguments, cancellation) => await ListeningEngineCheck.RunAsync(arguments, OptionalString(arguments, "endpoint"),
                OptionalString(arguments, "model"), cancellation)),
        Tool("voice_engine_check", "Speak one sentence with a self-hosted voice engine's loopback service (a host role's service, " +
            "default chatterbox on http://127.0.0.1:50083; chatterbox-original 50089, chatterbox-nano 50088, f5 50080, xtts 50081, " +
            "gpt-sovits 50082, dia 50084) through the production " +
            "path: the engine's own gateway relay inside a real gateway on 127.0.0.1 (pinned TLS, pairing) and the desktop's paired " +
            "client, with a starter voice as the reference (nothing played or recorded). Returns the service's /status before and " +
            "after (state, error, model, device, runtime versions such as torch and CUDA, Chatterbox " +
            "Original's style), the audio length, time to " +
            "first audio, total time, real-time factor, peak and RMS level, how much of it is voiced (voicedShare: near 0 for a " +
            "whisper, so text starting with [whispering] shows whether the Chatterbox model itself whispered; Martlet adds no " +
            "whisper of its own), or the failure code and message. For " +
            "chatterbox-original it sends the General and Expressive style saved in dataDirectory (chatterbox-style.json, as " +
            "Companion › Voice saves it), else Resemble's suggestions, and returns it as style. Loopback only; runs " +
            "Martlet.NodeLinkCheck.", new
        {
            engine = new { type = "string", @enum = VoiceEnginePorts.Keys.ToArray() },
            endpoint = new { type = "string", maxLength = 64 },
            text = new { type = "string", maxLength = 300 },
            dataDirectory = new { type = "string" }
        },
            async (arguments, cancellation) => await VoiceEngineCheckAsync(arguments, cancellation)),
        Tool("singing_status", "Read the singing host role: its loopback service's own status (default http://127.0.0.1:50085/: " +
            "state, engine (song, or the FIXTURE - NOT AI tone engine), the pinned models with their licences and sizes, sources, " +
            "voice matches set up (soulx, vevosing), queue, whether the worker process holds the graphics card, the card's memory " +
            "and the idle release time) and, with a data directory, the Singing card's saved choices (singing.json: quality and " +
            "voice match) and, when that directory is paired with hosts (hosts.json), Singing on each paired host read through its " +
            "own gateway as the card reads it (reachable, offers the song route, the service's state, voice matches, models and " +
            "their size, worker and GPU; a role in Docker on this PC listens only inside the gateway's network, so this is how " +
            "to read it), the singing pool as the desktop's SongClient uses it (pool: the order songs try the owner's own paired " +
            "computers, what the pool does with each now for the saved voice match, and where the next song goes) " +
            "plus what this PC's Docker shows of the role (container, images, models volume, and whether a " +
            "\"martlet-host add singing\" is running now: a setup in progress). Read-only; the pairing secret from Windows " +
            "Credential Manager only signs the requests and is never returned.", new
        {
            endpoint = new { type = "string", maxLength = 64 },
            dataDirectory = new { type = "string" }
        },
            async (arguments, cancellation) => await SingingStatusAsync(arguments, cancellation)),
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
        },
            async (arguments, cancellation) => await SingingCheckAsync(arguments, cancellation)),
        Tool("singing_pool_check", "Rehearse the singing pool (SingingPool through WorkQueue, what the desktop's SongClient runs) with " +
            "simulated singing computers, NOT real hosts or models: the order (the computer Martlet sings on first, then the shared " +
            "plan's singers, fewest jobs first, then the other paired computers), a free first computer taking the song with no other " +
            "asked, a computer singing another song passed over at once, a VevoSing song passing over a computer without VevoSing " +
            "(and failing with singing.voice_match_unavailable where none has it), computers that don't answer or don't sing passed " +
            "over, every computer busy so the song waits in line on the one with the fewest songs before it, every line full " +
            "(singing.busy), a failed song not made again elsewhere, and a computer that stops answering during the song replaced " +
            "by the next. In-process; reads nothing.", new { },
            async (_, cancellation) => await SingingPoolCheck.RunAsync(cancellation)),
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
        },
            async (arguments, cancellation) => await HearingCheck.RunAsync(OptionalString(arguments, "modelId"), DataDirectory(arguments), cancellation)),
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
        },
            async (arguments, cancellation) => await StraightVoiceCheck.RunAsync(MartletDirectory(arguments), SpeechDirectory(arguments),
                OptionalBool(arguments, "live") ?? false, OptionalString(arguments, "model"), cancellation)),
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
        },
            async (arguments, cancellation) => await DiscordVoiceCheck.RunAsync(OptionalString(arguments, "martletDirectory") is null ? null : MartletDirectory(arguments), cancellation)),
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
            "waiting are dropped) and cutOff right: what the conversation keeps (kept: only saidAloud, ending with the cut-off " +
            "marker) and the rest it hadn't said (unsaid, with the note that goes once in the next request's notes, Companion > " +
            "Prompts > Cut off: what you hadn't said), which together are the reply's words; text-only sends the reply with no voice at all (Speak " +
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
        },
            async (arguments, cancellation) => await SpokenReplyCheck.RunAsync(OptionalString(arguments, "voiceFailure"),
                OptionalInt(arguments, "failAt"), cancellation, OptionalInt(arguments, "reasoningMs"),
                OptionalInt(arguments, "voiceDelayMs"), OptionalString(arguments, "reply"),
                SpeechBreaksFrom(arguments, SavedSettings(arguments), out var speaker), speaker?.Name,
                OptionalString(arguments, "thinkingSteps"), OptionalBool(arguments, "refuseThinking") ?? false,
                OptionalBool(arguments, "chattiness") ?? false, OptionalStrings(arguments, "characterTags"))),
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
        },
            async (arguments, cancellation) => await EchoCheck.RunAsync(DataDirectory(arguments), OptionalInt(arguments, "delayMs"), cancellation)),
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
        },
            async (arguments, cancellation) => await UtteranceFilterCheck.RunAsync(arguments, DataDirectory(arguments), MartletDirectory(arguments),
                SpeechDirectory(arguments), cancellation)),
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
            "a verdict lets them decide. unprompted: what Martlet says on its own (UnpromptedSpeech): words over a screen or camera " +
            "remark drop it at once; notForMe over a reminder plays on, over finished work or a check-in plays on only after a short " +
            "pause (resumeWithinMs), else drops it; and a waiting check-in and reminder checked again just before they are said on a " +
            "simulated clock (say, wait while you are mid-utterance or the live floor is Live, drop a check-in that is too old or that " +
            "the conversation moved on from; a reminder is never dropped). Also returns the saved choice " +
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
        },
            async (arguments, cancellation) => await BargeInCheck.RunAsync(arguments, DataDirectory(arguments), cancellation)),
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
        },
            async (arguments, cancellation) => await PcAudioCheck.RunAsync(DataDirectory(arguments), cancellation)),
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
        },
            async (arguments, cancellation) => await SoundDigestCheck.RunAsync(DataDirectory(arguments), MartletDirectory(arguments),
                OptionalString(arguments, "wavFile"), cancellation)),
        Tool("quick_sounds_status", "Companion > Voice > Quick sounds while Martlet thinks, from a data directory: the choice (on or " +
            "off, off by default, and the delay, from talk-preferences.json), the voice replies speak with (in words, whether it is " +
            "a paid cloud voice, and its key per voice and character), whether its quick sounds are made (each clip's words and " +
            "length) or need the owner's click (a paid voice), every set kept in quick-sounds\\ and the newest desktop log lines " +
            "about quick sounds. Read-only; never plays anything.", new
        {
            dataDirectory = new { type = "string" }
        },
            arguments => QuickSoundCheck.Status(DataDirectory(arguments))),
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
        },
            async (arguments, cancellation) => await QuickSoundCheck.RunAsync(OptionalString(arguments, "scenario"), OptionalInt(arguments, "delayMs"), cancellation)),
        Tool("voice_sounds_status", "Companion > Touch > Touch zones' voice sounds (\"sound:<cue>\" entries in a zone's reaction " +
            "list: a gasp, a giggle, a sigh that Martlet's own voice makes alone), from a data directory: the voice replies speak " +
            "with (in words, its engine, whether it is a paid cloud voice, and its key per voice and character), why no sound plays " +
            "(noSounds, when the voice makes none), each sound the voice makes (its entry, cue, label, the engine's tag, and whether " +
            "its clip is made on this PC and how long), every zone that lists sounds and whether each plays with this voice, the " +
            "clips kept in voice-sounds\\, the last sound played or skipped and why (last) and the newest desktop log lines about " +
            "voice sounds. Read-only; never plays anything.", new
        {
            dataDirectory = new { type = "string" }
        },
            arguments => VoiceSoundCheck.Status(DataDirectory(arguments))),
        Tool("voice_sounds_check", "Rehearse touch zones' voice sounds with the production code: sounds (the sounds each engine " +
            "offers: Chatterbox's nine, Dia's without (mumbles), ElevenLabs' six, none for F5 or OpenAI), rules (VoiceSoundGate: " +
            "never with no voice, Speak replies aloud off, paused or muted, a sound the voice can't make, while Martlet speaks, while " +
            "you talk or over another sound; a clip not made yet is made instead), make (a clip made with the engine's tag alone " +
            "through ConversationRuntime.SynthesizeAsync and a fixture host voice, trimmed and kept in voice-sounds\\), play " +
            "(ConversationRuntime.PlayClipAsync plays it whole at the speakers' pace with lip sync, and refuses a second sound " +
            "meanwhile) and cut (a reply's own voice cuts a sound still playing the moment it starts; the reply never waits). " +
            "scenario runs one. Fixture voice (a quiet tone, NOT AI), fixture speakers paced like real ones; loopback only; plays " +
            "nothing.", new
        {
            scenario = new { type = "string", @enum = VoiceSoundCheck.Scenarios }
        },
            async (arguments, cancellation) => await VoiceSoundCheck.RunAsync(OptionalString(arguments, "scenario"), cancellation)),
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
        },
            async (arguments, cancellation) => await ElevenLabsCheck.RunAsync(OptionalString(arguments, "scenario"), OptionalString(arguments, "model"),
                OptionalString(arguments, "reply"), OptionalString(arguments, "dataDirectory") is null ? null : DataDirectory(arguments), cancellation)),
        Tool("lip_sync_pool_status", "Lip-sync's pool (the pool contract's lip-sync area) from a data directory: who does lip-sync " +
            "(avatar.json: its mode and the assigned host), lip-sync's list (pools.json, or the list made from the older choices " +
            "until one is saved: the assigned computer, the other paired computers the shared plan says run the audio2face role in " +
            "Devices > Sharing work's order, then this PC's own Audio2Face service; empty when lip-sync was off), each member's " +
            "key, kind, off, onlyFor and settings, the member keys this PC (or deviceId) tries for each chunk with the production " +
            "router (PoolRouting.Order), and the fallback when none can (voice loudness on this PC). Host and device IDs only. " +
            "Read-only.", new
        {
            dataDirectory = new { type = "string" },
            deviceId = new { type = "string" }
        },
            arguments => LipSyncPoolCheck.Status(DataDirectory(arguments), OptionalString(arguments, "deviceId"))),
        Tool("lip_sync_pool_check", "Rehearse lip-sync's pool with the production pool (LipSyncSharing) and queue (WorkQueue) on " +
            "simulated Audio2Face computers that animate one chunk at a time and turn another away at once, as a host's gateway does " +
            "(job.busy), NOT real hosts or models: shared by default with the assigned computer first, a free first computer taking " +
            "a chunk with no wait, two companion PCs at once each kept on its own computer, the next reply choosing again, every " +
            "computer busy past half a chunk so the voice's loudness moves the mouth, a computer freeing within the wait, an " +
            "unanswering computer skipped, a kept or never-used computer left out, a saved list's order without members turned " +
            "off or kept for another companion PC, an empty list meaning voice loudness, and the list made from the older choices. " +
            "In-process; reads nothing.", new { },
            async (_, cancellation) => await LipSyncPoolCheck.RunAsync(cancellation)),
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
        },
            async (arguments, cancellation) => await SongsCheck.StatusAsync(DataDirectory(arguments), cancellation)),
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
        },
            async (arguments, cancellation) => await SongsCheck.RunAsync(
                OptionalString(arguments, "dataDirectory") is null ? null : DataDirectory(arguments), OptionalString(arguments, "songId"),
                cancellation))
    ];

    /// <summary>Each voice host role's loopback port (deploy/host/roles/*/role.conf); voice_engine_check's engine is one of its
    /// keys.</summary>
    private static readonly IReadOnlyDictionary<string, int> VoiceEnginePorts = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["chatterbox"] = 50083, ["chatterbox-original"] = 50089, ["chatterbox-nano"] = 50088, ["f5"] = 50080, ["xtts"] = 50081,
        ["gpt-sovits"] = 50082, ["dia"] = 50084
    };

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
                    // Voices linked to a person's account (docs/ACCOUNTS.md, Voices), how many accounts they link to, and how many
                    // are the account signed in on this PC (accounts\session.json); null before accounts.
                    linked = list.Live.Count(v => v.Account is not null),
                    accounts = list.Live.Select(v => v.Account).OfType<Guid>().Distinct().Count(),
                    yours = SignedInAccount(directory) is { } signedIn ? list.Live.Count(v => v.Account == signedIn) : (int?)null,
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
            // People are always shared with the household: the voice list travels to every paired host of yours whatever
            // "Keep Martlet the same on all my computers" says, and never to a host a friend shares.
            sharing = VoiceSharing(directory),
            included = new
            {
                found = File.Exists(Path.Combine(martlet, "Martlet.Desktop.exe")),
                runtime = Martlet.Sherpa.SherpaComponents.RuntimeDirectory(martlet) is not null,
                voiceModels = Martlet.Sherpa.SherpaComponents.VoiceRecognitionIncluded(martlet)
            },
            parakeet = Martlet.Sherpa.SherpaComponents.InstalledParakeetModels(speech).Count > 0,
            parakeetModels = ParakeetCheck.Status(directory, speech),
            // Companion › People › Where it runs: the Voice recognition list, the places this PC tries and the running desktop's
            // own state (voice-recognition-state.json: this PC's engine, the last recognition, the computers tried).
            recognitionWhere = VoiceRecognitionCheck.Status(directory),
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

    /// <summary>voices_people: the People check-in tools on the data directory's saved voice list, as a check-in gets them.</summary>
    private static object VoicesPeople(JsonElement arguments)
    {
        var directory = DataDirectory(arguments);
        var tool = OptionalString(arguments, "tool") ?? Martlet.Conversation.PeopleTools.List;
        if (tool is not (Martlet.Conversation.PeopleTools.List or Martlet.Conversation.PeopleTools.Lately))
            throw new ArgumentException($"tool must be {Martlet.Conversation.PeopleTools.List} or {Martlet.Conversation.PeopleTools.Lately}.");
        var path = Path.Combine(directory, "voices.json");
        Martlet.Core.Speakers.VoiceRoster list;
        string state;
        try
        {
            (list, state) = File.Exists(path) ? (Martlet.Core.Speakers.VoiceRoster.Parse(File.ReadAllBytes(path)), "loaded")
                : (Martlet.Core.Speakers.VoiceRoster.Empty, "none");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Martlet.Core.Contracts.ContractException)
        {
            return new { state = "unreadable", tool };
        }
        var call = new JsonObject();
        if (OptionalString(arguments, "query") is { } query) call["query"] = query;
        if (OptionalInt(arguments, "minutes") is { } minutes) call["minutes"] = minutes;
        var signedIn = SignedInAccount(directory);
        // As the desktop's LocalVoices.IsYours: linked to the signed-in account, or marked as the owner's before accounts.
        var answer = Martlet.Conversation.PeopleTools.Run(new Martlet.Providers.TextToolCall("mcp", tool, call.ToJsonString()), list,
            voice => signedIn is { } account ? voice.Account == account : voice.Owner, DateTimeOffset.UtcNow);
        return new
        {
            state, tool, signedIn = signedIn is not null, isError = answer.Result.IsError,
            firstLine = answer.Result.Output.Split('\n', 2)[0], shown = answer.Shown.Count, text = answer.Result.Output
        };
    }

    /// <summary>The account signed in on this device (accounts\session.json), or null before accounts or when unreadable.</summary>
    private static Guid? SignedInAccount(string directory)
    {
        try { return Martlet.Core.Accounts.AccountSessionState.Load(directory)?.Current; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Martlet.Core.Contracts.ContractException) { return null; }
    }

    /// <summary>voices_status's sharing: the voice list syncs with every paired host of yours (hosts.json entries that aren't
    /// a friend's), whatever cluster-sync.txt says; hosts a friend shares are counted but never used.</summary>
    private static object VoiceSharing(string directory)
    {
        var path = Path.Combine(directory, "hosts.json");
        int hosts = 0, friendHosts = 0;
        if (File.Exists(path))
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllBytes(path));
                foreach (var host in document.RootElement.GetProperty("hosts").EnumerateArray())
                    if (IsSharedHost(host)) friendHosts++;
                    else hosts++;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or InvalidOperationException)
            {
                return new { state = "hosts unreadable", always = true };
            }
        }
        return new { state = hosts > 0 ? "on" : "no paired hosts yet", always = true, hosts, friendHostsNeverUsed = friendHosts };
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

    /// <summary>The host IDs in a data directory's hosts.json (nothing secret: pairing secrets stay in Credential Manager), without
    /// hosts friends share with that desktop (they never offer Singing to it).</summary>
    private static HashSet<string> PairedHostIds(string directory)
    {
        try
        {
            using var hosts = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(directory, "hosts.json")));
            return hosts.RootElement.TryGetProperty("hosts", out var list) && list.ValueKind == JsonValueKind.Array
                ? list.EnumerateArray().Where(h => !IsSharedHost(h)).Select(h => h.TryGetProperty("pairing", out var pairing) && pairing.TryGetProperty("hostId", out var id)
                    ? id.GetString() : null).OfType<string>().ToHashSet(StringComparer.Ordinal)
                : [];
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException) { return []; }
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
            quiet = Martlet.Conversation.StayQuiet.IsQuiet(preview.Shown),
            characterCues = preview.Cues.Select(c => new { piece = c.Piece, tag = c.Tag, offset = c.Offset }).ToArray(),
            acted = preview.Acted.Select(tag => new { tag = tag.Tag, kind = tag.Kind.ToString(), name = tag.Name, written = tag.Written }).ToArray(),
            note = preview.Note
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
}
