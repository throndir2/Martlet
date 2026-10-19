using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Martlet.Core.Installation;
using Martlet.Doctor;
using Martlet.Mcp.Client;

namespace Martlet.Mcp;

/// <summary>martlet_guide's "Other" area: the tools that martlet_guide lists under Other. Each Tool(...) registers one tool:
/// its name, description, input schema, handler and, when it needs one, its permission (<see cref="ToolPermission"/>). A new
/// tool of this area goes here.</summary>
internal sealed partial class McpServer
{
    private McpTool[] OtherTools() =>
    [
        Tool("long_talk_check", "How long one turn of talking and one spoken reply may be (limits: the longest turn, the most one " +
            "speech-to-text request hears, the reply's time and spoken budget), and a long recording heard in pieces the way the " +
            "conversation hears it: phrases a Windows voice says (System.Speech rendered to memory, never played; optional phrases, " +
            "up to 8 English sentences) put one after another into seconds (30-600, default 70) of talking, cut by the production " +
            "splitter (BoundedWaveAudio.Split) into pieces one request takes. Reports each piece, each cut (how much quieter it is " +
            "than the recording: a pause, not a word) and whether the pieces cover every sample; with a Parakeet model downloaded in " +
            "speechDirectory (optional absolute path, default the data directory's speech folder) and the sherpa-onnx runtime in " +
            "martletDirectory, also transcribes each piece through the production ParakeetEngine and the joined words' error rate " +
            "(ok at most 20%). Nothing is downloaded, recorded or played; nothing leaves this PC.", new
        {
            dataDirectory = new { type = "string" },
            martletDirectory = new { type = "string" },
            speechDirectory = new { type = "string" },
            seconds = new { type = "integer", minimum = 30, maximum = LongTalkCheck.MaximumSeconds },
            phrases = new { type = "array", maxItems = 8, items = new { type = "string", maxLength = 200 } }
        },
            async (arguments, cancellation) => await LongTalkCheck.RunAsync(arguments, MartletDirectory(arguments),
                OptionalString(arguments, "speechDirectory") is not null ? SpeechDirectory(arguments) : Path.Combine(DataDirectory(arguments), "speech"),
                cancellation)),
        Tool("conversation_sync_selftest", "Rehearse one record of conversations on every computer end to end with the production code: " +
            "two real gateways on 127.0.0.1 (pinned TLS, signed requests, the hosts' account directory, in-memory conversation storage) and " +
            "three simulated desktops, each with a real record of conversations (Martlet.Conversation.ConversationHistory) in a temporary " +
            "folder, the desktop's paired client (HostConversationPeer) and the real record sync (ConversationRecordSync). Walks recording " +
            "on one computer and recalling and searching it on another (PastConversations), an edit, a deleted message and a deleted " +
            "conversation reaching every computer (and never coming back), offline edits of one message on two computers (the later wins), " +
            "a host that missed changes, a new computer taking the whole record, deleting everything, an exchange from a newer Martlet " +
            "passing through untouched, another account's device refused the record (conversations.account_denied), the hosts keeping " +
            "copies over a restart, no conversation text in the sync state files and an unsigned request refused. Synthetic conversations " +
            "only; loopback only; the folder is deleted.", new { },
            async (_, cancellation) => await NodeLinkCheckAsync(cancellation, "conversations")),
        Tool("kept_motions_status", "Read a VRM character's kept body motions for a data directory (docs/AVATARS.md#motions-in-conversation): " +
            "the motion creations every VRM character can do (name, seconds, size, fixture, starter, model, where, whether the clip is on " +
            "this PC; never the prompt's words), whether this computer saw the starter motions made, whether motion.json is saved and " +
            "the fixture switch is on, the body_motion tool and its quiet job kind, the motion creation kind (kept out of the creation " +
            "tools) and, with model (an absolute path to a .vrm), the kept motions as that model's emotes and motions with their reply " +
            "tags and whether replies are offered them. Read-only.", new
        {
            dataDirectory = new { type = "string" },
            model = new { type = "string", maxLength = 260 }
        },
            arguments => KeptMotionsCheck.Status(DataDirectory(arguments), OptionalString(arguments, "model"))),
        Tool("kept_motions_check", "Make one body motion (or, with starters, Martlet's five starter motions) with the FIXTURE - NOT AI " +
            "motion maker and check it the way Martlet keeps and plays it in conversation: the clip checked as the VRM renderer checks it " +
            "(size, length, bones), kept as a motion creation in dataDirectory (disposable when left out; a given dataDirectory must not " +
            "be Martlet's own data folder and needs --allow-changes) and read back, the creation " +
            "tools' words unchanged and never listing it, and the motion listed as a VRM's motion with its reply tag (model: an absolute " +
            "path to a .vrm; a FIXTURE VRM when left out). Returns ok, each step and the motions with their ids, clip names and the note " +
            "the conversation gets. Never uses a motion computer (motion_check does).", new
        {
            name = new { type = "string", maxLength = 40 },
            motion = new { type = "string", maxLength = Martlet.Core.Motion.MotionRequest.MaximumPromptCharacters },
            seconds = new { type = "number", minimum = Martlet.Core.Motion.MotionRequest.MinimumSeconds, maximum = Martlet.Core.Motion.MotionRequest.MaximumSeconds },
            starters = new { type = "boolean" },
            dataDirectory = new { type = "string", maxLength = 260 },
            model = new { type = "string", maxLength = 260 }
        },
            async (arguments, cancellation) => await KeptMotionsCheck.RunAsync(OptionalString(arguments, "name"), OptionalString(arguments, "motion"),
                OptionalDouble(arguments, "seconds"), OptionalBool(arguments, "starters") ?? false,
                OptionalString(arguments, "dataDirectory") is null ? null
                    : DisposableDataDirectory(arguments) ? DataDirectory(arguments)
                    : throw new ArgumentException("kept_motions_check writes a FIXTURE motion: give a disposable dataDirectory (not Martlet's own data folder) or leave it out."),
                OptionalString(arguments, "model"),
                cancellation)) with { Writes = arguments => OptionalString(arguments, "dataDirectory") is not null },
        Tool("greeting_check", "Rehearse Martlet's greeting once it has warmed up after it starts (docs/CONVERSATION.md#greeting-when-martlet-starts) " +
            "with the production code and fixture facts: the time of day, the weekend and special days, which account, voice or " +
            "Windows names count as a real name (user names and handles don't), the end of the newest conversation on this PC and " +
            "a newer Telegram chat from a real conversation record in a temporary folder, reminders missed while Martlet wasn't " +
            "running, only the reminders due in the next 12 hours, a Martlet update, the open apps with the one in front first, the " +
            "counts-only line for the log and Settings' GreetingStatus, and the greeting notice through BackgroundJobs (in character " +
            "or not at all with [pass], on its own as soon as Martlet is free, in the notes of the user's first message, said once, " +
            "let go after waiting too long). No model, network or credentials.", new { },
            async (_, cancellation) => await GreetingCheck.RunAsync(cancellation)),
        Tool("vroid_hub_status", "Whether Martlet can show VRoid Hub characters on this PC: whether this build has VRoid Hub app keys " +
            "(never the keys), whether a VRoid Hub account is linked for the account whose settings the data directory holds (and its " +
            "VRoid Hub user name; never a token), the VRoid Hub looks this PC used (vroid-hub-looks.json: name, creator, conditions " +
            "of use) and whether the character shown is one. Read-only; no network.", new
        {
            dataDirectory = new { type = "string" }
        },
            arguments => CharacterLooks.VRoidHubStatus(DataDirectory(arguments))),
        Tool("situation_status", "Situations (the While gaming and Host away plans), from a data directory: what the desktop decided " +
            "last on this companion PC (situation.json: Normal, While gaming or Host away; where live Thinking runs normally and now; " +
            "the whole order; why; whether the conversation uses it; the game; the hosts away; the online services preference; the " +
            "graphics card note while gaming; FIXTURE when MARTLET_SIMULATE_SITUATION drove it) and the rules (a game looked for every 3 " +
            "seconds and held 90 seconds, a host missing after 30 seconds, a card short of memory below 1 GB free). Host IDs, model " +
            "and provider names and a game's name only. Read-only.", new
        {
            dataDirectory = new { type = "string" }
        },
            arguments => SituationCheck.Status(DataDirectory(arguments))),
        Tool("situation_check", "Rehearse the While gaming and Host away plans with the production rules (LiveSituations, " +
            "SituationRoutes, GameWatch, SituationPlans) on FIXTURE facts, apps and computers, NOT a real game, host or model: a game " +
            "on a PC used for games moves live Thinking to a host that answers, then to the hosted backup unless online services are " +
            "Never, then keeps this PC's own model; a PC not used for games and Thinking on a host move nothing; a host that is away " +
            "moves live Thinking to another host, the hosted backup, then this PC's own model, and it comes back when the host answers; " +
            "a card is short of memory below 1 GB free; the moved routes are valid, consented and keep If Thinking fails' key scope; " +
            "the game watch finds a game from a Steam library, keeps it through a switch to another window and ends it after 90 " +
            "seconds; Recommended setup's three plans (Normal, While gaming, Host away). In-process; reads and writes nothing.", new { },
            _ => SituationCheck.Run()),
        Tool("search_languages_check", "Rehearse search in other languages and lorebook word forms with the production code on " +
            "synthetic text in disposable folders: English text keeping the search terms it always had (also with web addresses " +
            "in it), the language of a text (Spanish, French, German, Portuguese, Italian), light stems of those languages " +
            "(gatti and gatto, Drachen and Drache), Chinese and Japanese read as pairs of characters, memory recall (Martlet.Memory) " +
            "and past-conversation recall (ConversationHistory and PastConversations) finding the right fact or exchange from a " +
            "question in each of those languages and in Chinese, Japanese and Korean, ordinary messages not looking in the record, " +
            "times such as \"hace 3 días\", \"vor 3 Tagen\", \"3天前\" and \"어제\" naming the right days, lorebook keywords with word " +
            "forms off (exact, as SillyTavern) and on (\"dragon\" finds \"dragons\", case-sensitive keys keep their capitals, /regex/ " +
            "keys unchanged) and the SillyTavern export, with timings for reading terms, recognizing a reference to the past and " +
            "scanning 2,000 entries. Never opens your data. Nothing leaves this PC.", new { },
            async (_, cancellation) => await LanguagesCheck.RunAsync(cancellation)),
        Tool("search_engine_check", "Show and check the shared search engine that memory, app guides and past conversations use " +
            "(Martlet.Core.Search; docs/SEARCH.md): each source's search profile (fields with their BM25F weights and length " +
            "normalization, k1, positions, candidates, the reranker's weights, cutoff and selection), and on synthetic documents: the " +
            "first stage keeping exactly the best candidates and honouring a filter, a title outranking the text (BM25F), a second " +
            "field finding a document by words it doesn't say (search words), an index without positions growing while it is " +
            "searched, reciprocal rank fusion with a second ranked list, and the time of the first stage and ranking on 100,000 " +
            "documents (median and p95 in microseconds; under 10 milliseconds). Each source's ranking end to end is in " +
            "memory_recall_check, app_guides_check and conversation_history_check. Reads and writes nothing.", new { },
            (_, cancellation) => Task.FromResult<object>(SearchEngineCheck.Run(cancellation))),
        Tool("meaning_search_check", "Rehearse finding by meaning (docs/SEARCH.md, \"Finding by meaning\": an embedding model used " +
            "only away from the reply's first word, when remembering and tidying memory find related facts, in the memory find " +
            "tools and in search_guide) with the production code on synthetic text in a disposable folder: vector sets (nearest " +
            "first, a changed text embedded again, a forgotten fact's vector dropped, saved and read back, another model's file " +
            "refused, 10,000 vectors searched in milliseconds), memory's fusion through the search engine (7 held-out questions in " +
            "other words such as \"What should I cook tonight?\" for \"The user is vegetarian.\": recall in the first 3 by words " +
            "and fused; names such as Rex still first), tidying memory grouping \"lives in Seattle\" with \"moved to Portland\", a " +
            "guide's fusion (\"How do I make my weapons stronger?\" finding the blacksmith's upgrades) and the Ollama embedder " +
            "against a loopback fixture (/api/embed on the processor only: num_gpu 0, keep_alive 1h, truncate, nomic-embed-text's " +
            "search_query: start; a model that isn't downloaded told apart; held back while Ollama has 3 models loaded or one runs " +
            "on the processor). The embeddings are a FIXTURE (NOT AI). measure: true also times the real model (model, default " +
            "nomic-embed-text) in this PC's Ollama on the same questions (first request, median and longest question, time per " +
            "fact, recall in the first 3) when it is downloaded and loading it holds nothing back; it never downloads. Never opens " +
            "your data.",
            new
            {
                measure = new { type = "boolean", description = "Also time the real embedding model in this PC's Ollama (loopback; never downloads)." },
                model = new { type = "string", description = "The Ollama embedding model to time (default nomic-embed-text)." }
            },
            async (arguments, cancellation) => await MeaningSearchCheck.RunAsync(OptionalBool(arguments, "measure") ?? false,
                OptionalString(arguments, "model"), cancellation))
    ];
}
