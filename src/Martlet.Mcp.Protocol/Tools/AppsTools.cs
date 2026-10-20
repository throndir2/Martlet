using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Martlet.Core.Installation;
using Martlet.Doctor;
using Martlet.Mcp.Client;

namespace Martlet.Mcp;

/// <summary>martlet_guide's "Apps, home and memory" area: Discord, messaging, the terminal, smart home, reminders, check-ins,
/// creations, memory and MCP servers. Each Tool(...) registers one tool: its name, description, input schema, handler and, when
/// it needs one, its permission (<see cref="ToolPermission"/>). A new tool of this area goes here.</summary>
internal sealed partial class McpServer
{
    private McpTool[] AppsTools() =>
    [
        Tool("creations_status", "Read Martlet's creations (Creations, docs/CREATIONS.md: songs and other things Martlet made, shared " +
            "with every paired Martlet computer) from a data directory's creations.json, creations folder and creations-sync.json: " +
            "live creations and tombstones, total size, counts and sizes per kind, and for each creation its short id, kind, kind " +
            "version, size, length, when and on which device it was made, whether Martlet may clean it up, its assets (name, media " +
            "type, size, pieces), whether this PC holds all of it and on how many hosts it is complete; the asset files here (unused " +
            "ones and copies in progress); the last sync (when, its summary, each paired host's state and how many creations it " +
            "holds); and the limits. Never a title, text, voice or personality. Each account keeps its own creations " +
            "(docs/ACCOUNTS.md) in accounts\\<32 hex>; with account (32 hex digits or a GUID) it reads that account's, else the " +
            "account the data folder's creations moved to (creations-moved.json), or the data folder itself before that move. " +
            "from names the account, the folder read, why it was chosen and the recorded move. Read-only; contacts nothing.", new
        {
            dataDirectory = new { type = "string" },
            account = new { type = "string", maxLength = 36 }
        },
            arguments => CreationsCheck.Status(DataDirectory(arguments), OptionalString(arguments, "account"))),
        Tool("creations_check", "Rehearse Martlet's creations end to end with the production code: list_creations and " +
            "perform_creation (Martlet.Conversation.CreationTools) on a disposable folder with the production store and the FIXTURE - " +
            "NOT AI test-tone kind (no tools without a registered kind; the same two tools and texts every time; listing, filters, " +
            "performing through the kind's handler, and clear refusals for no handler, unknown ids, bad arguments, a creation still " +
            "copying and an unknown kind), then the sync: two real gateways on 127.0.0.1 (pinned TLS, signed requests, in-memory " +
            "creations.json and pieces) and three simulated desktops with the production CreationStore and CreationSync over the " +
            "desktop's paired client. Checks FLAC sizes, sharing every 3 MiB piece, skipping unchanged hosts, a new desktop and a " +
            "relay host, resuming an interrupted copy, performing on another computer, an unknown kind passing through, rename, " +
            "delete everywhere (pieces and files deleted), stale copies, a host restart, piece checks, an unsigned request refused, " +
            "a kind's rules and the per-host sync record; then two accounts (docs/ACCOUNTS.md) on one host: each syncs through its " +
            "own list, the host keeps one pool of pieces (a piece goes only when no list uses it), the owner's sync joins the old " +
            "single list both ways for desktops on an older Martlet (the owner bridge) and skips an unchanged host, the other " +
            "account never sees the old list or the owner's creations, and a restart keeps every list. Loopback only; folders are " +
            "deleted and the credential vault is untouched. " +
            "With seedDataDirectory (a disposable folder under the temporary folder, never Martlet's own), it instead writes two " +
            "FIXTURE - NOT AI test tones there, so the Creations page can be checked with a desktop on that folder.", new
        {
            seedDataDirectory = new { type = "string" }
        },
            async (arguments, cancellation) => OptionalString(arguments, "seedDataDirectory") is { } seed
                ? await CreationsCheck.SeedAsync(seed, cancellation)
                : await CreationsCheck.RunAsync(() => NodeLinkCheckAsync(cancellation, "creations"), cancellation)),
        Tool("memory_sync_status", "Read one memory on every computer (what Martlet remembers, the same on all the owner's computers " +
            "through the paired hosts) from a data directory's memory-sync.json: whether sync is on, when this PC last synced its memory " +
            "store, how many facts it had then and which computer wrote each version, and how many forgotten facts every computer agreed " +
            "on, for the file from before accounts and for each memory space (spaces: space ID and the same counts, from " +
            "memory-sync\\<space>\\memory-sync.json). Never a fact or its text. Read-only; contacts nothing.", new
        {
            dataDirectory = new { type = "string" }
        },
            arguments => MemorySyncStatus(arguments)),
        Tool("memory_status", "Read what Martlet remembers, and whose, from a data directory: whether memory is on and where it is kept " +
            "(settings.json), then the memory store's facts counted by where they came from (typed, conversation), how many expire, how " +
            "many are replaced facts kept as history (replaced) and how many have hidden search words (withSearchWords), and " +
            "whose they are: everyone's, each voice they belong to by its tag from voices.json (V3, whether it is named or the owner's) " +
            "and those of forgotten voices; then spaces: each memory space kept in the data folder (an account's own space in " +
            "accounts\\<id>\\memory, household and character spaces in memory-spaces\\<space>) with its space ID and the same counts. The " +
            "top-level counts are the store in the memory setting's folder (memories from before accounts until they move to the owner's " +
            "space, or a custom folder). upkeep: what tidying memory last did (memory-upkeep.json: how many stores it checked for " +
            "duplicates, and the last pass's space, outcome, counts and pool member). where: how many current facts matter " +
            "everywhere, in any game (anyGame) and for each game by its name (docs/MEMORY.md, \"Memories for a game\"). Never a " +
            "fact's text, a person's name, a voice ID or a " +
            "path. Read-only (it never opens or locks a " +
            "store); contacts nothing. limits: the most facts a store holds, characters per fact and store bytes.", new
        {
            dataDirectory = new { type = "string" }
        },
            async (arguments, cancellation) => await MemoryStatusAsync(arguments, cancellation)),
        Tool("memory_brief_status", "Read the memory brief (Companion > Memory > Memory brief; docs/MEMORY.md, \"The memory brief\": " +
            "a short profile of the people Martlet talks with, written by a Thinking pool member from the remembered facts and sent " +
            "with the first message of each conversation) from a data directory's memory-brief.json: whether it is on, whether one " +
            "is written, by whom (thinking or owner), when, from how many facts and people, its length in characters and lines, and " +
            "the limits (longest brief, facts before the first one, new facts before it is written again, tries an hour, gaps). " +
            "Never the brief or a fact. Read-only; contacts nothing.", new
        {
            dataDirectory = new { type = "string" }
        },
            arguments => MemoryBriefCheck.Status(DataDirectory(arguments))),
        Tool("memory_brief_check", "Rehearse the memory brief end to end with the production code on a disposable memory store: two " +
            "facts write nothing, a third has a fixture Thinking pool member (FIXTURE - NOT AI) write it through the production " +
            "helper-job router at low priority with whose each fact is, the first message of a conversation carries it and the next " +
            "don't, a forgotten fact holds it back until it is written again without it, your own text stays, off sends nothing, " +
            "Write it again replaces your text, and a second simulated computer takes the same brief through the production " +
            "shared-settings section and sends it. Returns each step and passed. No model, network or credentials; it never opens " +
            "your memory.", new { },
            async (_, cancellation) => await MemoryBriefCheck.RunAsync(cancellation)),
        Tool("memory_forget", "Delete remembered facts, as Companion › Memory's Delete does: every fact whose text contains contains " +
            "(any case) or whose ID is in ids, in every memory store of the data folder or only in space (a space ID from " +
            "memory_status). preview: true only counts them. A running Martlet keeps working (the tool waits up to 15 s for the store " +
            "to be free) and its memory sync then forgets the facts on your other computers. Returns per space how many facts it has, " +
            "matched and deleted and the matched fact IDs; never a fact's text. Needs --allow-changes unless preview. " +
            DataDirectoryNote, new
        {
            contains = new { type = "string", maxLength = 200 },
            ids = new { type = "array", items = new { type = "string" }, maxItems = 1000 },
            space = new { type = "string", maxLength = 128 },
            preview = new { type = "boolean" },
            dataDirectory = new { type = "string" }
        },
            async (arguments, cancellation) => await MemoryForgetAsync(arguments, cancellation), ToolPermission.ChangesUnlessPreview),
        Tool("memory_where", "Set where remembered facts matter, as Companion › Memory's Where it matters does (docs/MEMORY.md, " +
            "\"Memories for a game\"): every current fact whose text contains contains (any case) or whose ID is in ids, in every " +
            "memory store of the data folder or only in space, is kept for where: \"everywhere\", \"games\" (any game) or a game's " +
            "name (only while that game is played). preview: true only counts them. A running Martlet keeps working (the tool waits " +
            "up to 15 s for the store to be free) and its memory sync carries the change to your other computers. Returns per space " +
            "how many facts matched and changed and their IDs; never a fact's text. Needs --allow-changes unless preview. " +
            DataDirectoryNote, new
        {
            where = new { type = "string", maxLength = 120 },
            contains = new { type = "string", maxLength = 200 },
            ids = new { type = "array", items = new { type = "string" }, maxItems = 1000 },
            space = new { type = "string", maxLength = 128 },
            preview = new { type = "boolean" },
            dataDirectory = new { type = "string" }
        },
            async (arguments, cancellation) => await MemoryWhereAsync(arguments, cancellation), ToolPermission.ChangesUnlessPreview),
        Tool("memory_sync_selftest", "Rehearse one memory on every computer end to end with the production code: two real gateways on " +
            "127.0.0.1 (pinned TLS, signed requests, in-memory memories.json) and three simulated desktops, each with a real Martlet.Memory " +
            "store in a temporary folder, the desktop's paired client and the real memory sync engine (Martlet.Core.Sync.MemorySyncNode). " +
            "Walks saving on one computer and recalling on another, an edit, a deletion reaching every computer (and never coming back), " +
            "offline edits on two computers, a host that missed changes, a new computer taking everything, an expired fact, a full store " +
            "making room by forgetting the oldest conversation fact, a fact from a newer Martlet passing through, a new memory folder, " +
            "memory spaces (a fact in one account's space stays apart from other spaces and the old document, and survives a host restart), " +
            "accounts with the hosts' account directory (the owner's memories from before accounts move into the owner's space and sync with " +
            "both its space and the old document; the household space is shared; a device of another account is refused the owner's space " +
            "and the old document; a desktop on an older Martlet keeps using the old document), sharing (another account gives a fact to " +
            "the owner's space through the give route without reading it, and a character's space follows the owner's household sharing " +
            "entry: open to the household while shared together, the owner's only once private again) and " +
            "an unsigned request refused. Synthetic facts only; loopback only; the folder is deleted.", new { },
            async (_, cancellation) => await NodeLinkCheckAsync(cancellation, "memories")),
        Tool("mcp_servers_status", "Read the MCP servers in a data directory's mcp.json as Martlet parses them: each server's name, " +
            "transport, program and raw arguments (${env:...} and ${secret:...} references are kept, never their values; a literal " +
            "value that looks secret, such as the value after --token or --api-key, a key=value with a secret name, a password in a " +
            "web address or a token such as sk-... or ghp_..., shows as [hidden]), environment and " +
            "header names, on/off, auto-approve, the MCP directory entry it was installed from, the secret names it uses and how a " +
            "reply names its tools (toolNames: always <server>_<tool>, whatever else runs). " +
            "Read-only; starts no server and reads no credentials.", new
        {
            dataDirectory = new { type = "string" }
        },
            arguments => McpServersStatus(arguments)),
        Tool("mcp_directory_plan", "Show how Martlet's MCP directory would install one MCP Registry entry (a server.json object, as " +
            "the registry's v0.1 API returns it under \"server\"): the ways to run it, the inputs each needs, and with values (by input " +
            "key) the exact mcp.json entry and secret names it would write. Local only: fetches, writes and starts nothing.", new
        {
            server = new { type = "object" },
            name = new { type = "string", maxLength = 64 },
            values = new { type = "object", additionalProperties = new { type = "string", maxLength = 4096 } }
        }, ["server"],
            arguments => McpDirectoryPlan(arguments)),
        Tool("home_assistant_probe", "Check one Home Assistant address the way Companion > Smart home does before setting it up: " +
            "whether it answers like Home Assistant and how far its first-run setup got (owner account, regional settings, analytics, " +
            "integration step; GET /api/onboarding, which needs no sign-in). Sends no token or password and changes nothing.", new
        {
            address = new { type = "string", maxLength = 512 }
        }, ["address"],
            async (arguments, cancellation) => await HomeAssistantProbeAsync(arguments, cancellation)),
        Tool("home_assistant_find", "Ask the local network for Home Assistant the way Smart home's Find on my network does: one " +
            "multicast DNS question for _home-assistant._tcp.local on each local network, listing every Home Assistant that answers " +
            "(name, address, version). Sends nothing else and changes nothing.", new
        {
            seconds = new { type = "number", minimum = 1, maximum = 10 }
        },
            async (arguments, cancellation) => await HomeAssistantFindAsync(arguments, cancellation)),
        Tool("smart_home_status", "Read Companion > Smart home's saved connection from a data directory: the Home Assistant address, " +
            "name and version, whether a token is saved (never the token), the control, locks and flexible-request settings, and whether " +
            "the connection is shared through the paired hosts (shared revision, which host it came from). Read-only.", new
        {
            dataDirectory = new { type = "string" }
        },
            arguments => SmartHomeStatus(arguments)),
        Tool("discord_status", "Read Companion > Discord's saved setup from a data directory's discord.json: whether a bot token is saved " +
            "and readable in Windows Credential Manager (never the token), the application ID, whether the bot connects when Martlet runs, " +
            "the chat modes, channel-rule and people counts, whether the owner's account and home server are set, and the next setup step. Read-only.", new
        {
            dataDirectory = new { type = "string" }
        },
            arguments => DiscordCheck.Status(DataDirectory(arguments))),
        Tool("discord_check", "Connect the data directory's saved Discord bot once with Martlet's production bot host and report whether " +
            "Discord accepts it: Online with the bot's name and server count, a rejected token, or Message Content Intent left off in the " +
            "Developer Portal; then disconnect. Sends no messages. Needs a saved token (Companion > Discord).", new
        {
            dataDirectory = new { type = "string" },
            seconds = new { type = "integer", minimum = 3, maximum = 30 }
        },
            async (arguments, cancellation) => await DiscordCheck.RunAsync(DataDirectory(arguments), OptionalInt(arguments, "seconds"), cancellation)),
        Tool("messaging_status", "Read Companion > Messaging from a data directory's messaging.json (this PC only, never synced): " +
            "whether Martlet answers its Telegram bot and its WhatsApp number on this PC, the bot's name and username, the WhatsApp number, name, IDs, port and " +
            "public address, whether secrets are saved, how many chats are paired and whether replies are also said aloud. Never the bot token, WhatsApp " +
            "access token or app secret (Windows Credential Manager) or the chats' names and IDs. Read-only.", new
        {
            dataDirectory = new { type = "string" }
        },
            arguments => MessagingStatus(arguments)),
        Tool("messaging_check", "Rehearse Companion > Messaging's production bridge and Telegram transport with local fakes (no Telegram, " +
            "WhatsApp or Meta server is contacted): a connection that breaks while Telegram's answer is read is a network failure " +
            "(retried, no token in the message); an unexpected receive error makes the bridge Retrying, then Running again (never a " +
            "silent Off); a failing typing call and a failing answer don't lose the rest of the batch (the chat is told Martlet " +
            "couldn't answer); and a stranger's wrong pairing codes don't use up the owner's code. Returns ok and each step.", new { },
            async (_, cancellation) => await MessagingCheck.RunAsync(cancellation)),
        Tool("smart_home_guard", "Show how Smart home's safety guard (HomeCommandGuard) treats one request before it would go to " +
            "Home Assistant: Normal (ordinary words or a status question, sent to Assist), Sensitive (asks for an action on a lock, " +
            "door, garage, gate, alarm or valve: never sent unless allowed and confirmed) or NotACommand (a longer sentence; chat). " +
            "Local only; contacts nothing.", new
        {
            text = new { type = "string", maxLength = 2000 }
        }, ["text"],
            arguments => MessagingCheck.Guard(RequiredString(arguments, "text"))),
        Tool("terminal_status", "Read Companion > Tools > Terminal from a data directory's terminal.json (this PC only, never " +
            "synced): whether replies may run terminal commands (off by default), the shell and whether it is installed, which shells " +
            "this PC has, whether every command asks first (on by default), the time limit, whether commands start in the home folder " +
            "or a chosen one (never its path) and whether it exists, and run_terminal_command exactly as the Thinking model gets it " +
            "(the start folder shown as {folder}). Read-only; runs nothing.", new
        {
            dataDirectory = new { type = "string" }
        },
            arguments => TerminalCheck.Status(DataDirectory(arguments))),
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
        },
            async (arguments, cancellation) => await TerminalCheck.RunAsync(DataDirectory(arguments), OptionalString(arguments, "shell"), cancellation)),
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
        },
            async (arguments, cancellation) => await DiscordCallCheck.RunAsync(DataDirectory(arguments), cancellation)),
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
        },
            async (arguments, cancellation) => await DiscordTextCheck.RunAsync(DataDirectory(arguments), arguments, cancellation)),
        Tool("discord_companion_check", "Martlet's Discord companion as saved in a data directory (discord.json and " +
            "discord-companion.json): friends (names, how many take calls), friend requests waiting for the owner, recent declines, the " +
            "private call channels it made and when the bot's picture last changed (never the token). Then rehearses the production " +
            "DiscordCompanion against an in-memory Discord (no token; contacts nothing): /friend ask (and asking twice), the owner's " +
            "approval and welcome DM, a call (the private channel 'character & person' and its permission overwrites for @everyone, " +
            "the friend, the bot and the owner, the ring DM with a jump link and a server invite), calling again (channel reused, " +
            "joined), cleanup, calls turned off, /friend remove, the presence each state shows and its rate limit, and the avatar " +
            "rate limit. requestFrom (a Discord user ID as a string, with requestName) files a friend request into the given " +
            "dataDirectory as /friend ask would (requires an explicit, disposable dataDirectory, never Martlet's own data folder, and --allow-changes), so Companion > Discord's Friends " +
            "and calls card lists it for DiscordFriendApprove-<id>.", new
        {
            dataDirectory = new { type = "string" },
            person = new { type = "string", maxLength = 64 },
            character = new { type = "string", maxLength = 64 },
            requestFrom = new { type = "string", pattern = "^[0-9]{1,20}$" },
            requestName = new { type = "string", maxLength = 64 }
        },
            async (arguments, cancellation) => await DiscordCompanionCheck.RunAsync(DataDirectory(arguments),
                DisposableDataDirectory(arguments),
                OptionalString(arguments, "person"), OptionalString(arguments, "character"),
                OptionalString(arguments, "requestFrom") is { } from
                    ? ulong.TryParse(from, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var id)
                        ? id : throw new ArgumentException("requestFrom must be a Discord user ID.")
                    : null,
                OptionalString(arguments, "requestName"), cancellation)) with { Writes = arguments => OptionalString(arguments, "requestFrom") is not null },
        Tool("discord_reply_status", "Martlet's Discord reply engine, from a data directory: the desktop's discord-replies.json " +
            "(whether the engine is wired, replies, passes, skipped turns with the last reason, failures with the last code, places " +
            "with history, requests running, the last reply and pass times, its latency, first-words time and prompt-cache use, " +
            "the Thinking route it last used (route type and model), how it waits for the local conversation on a shared model, " +
            "turns waiting for it and requests a local reply stopped; never what was said or who said it) and discord.json's chat " +
            "setup (configured, enabled, owner set, chat modes, rule and people counts; never the token, IDs or names). Read-only.", new
        {
            dataDirectory = new { type = "string" }
        },
            arguments => DiscordReplyCheck.Status(DataDirectory(arguments))),
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
        },
            async (arguments, cancellation) => await DiscordReplyCheck.RunAsync(DataDirectory(arguments), OptionalString(arguments, "model"),
                OptionalBool(arguments, "live") ?? false, cancellation)),
        Tool("reminders_status", "Martlet's reminders (the reply model's reminders tool: set, list, cancel; docs/CONVERSATION.md#reminders), " +
            "from the shared-settings.json of the account signed in on a data directory (accounts\\<id>, named by accounts\\working-copy.json; " +
            "the data directory's own for a folder from before accounts), with which account and copy it read: every computer's reminders entry, each reminder's text, due and set times, " +
            "the computer it was set on, its state (Pending, Done, Canceled, Missed) and who settled it, and each computer's marks " +
            "(Bid with its idle seconds, Claim, Done, Cancel, Missed), plus the tool exactly as the model gets it. Read-only.", new
        {
            dataDirectory = new { type = "string" }
        },
            async (arguments, cancellation) => await RemindersCheck.StatusAsync(DataDirectory(arguments), cancellation)),
        Tool("reminders_check", "Rehearse reminders end to end with the production code on two simulated companion PCs whose entries " +
            "merge through the shared settings: setting one with the tool (in minutes and at a local time), the other PC listing and " +
            "canceling one, who says a due reminder (both offer, the PC used most recently takes it, the other stays quiet), the " +
            "conversation's wording through BackgroundJobs (on its own as soon as Martlet is free, or in the notes of the next " +
            "message), a PC alone taking it at once and one far too late let go. No model, network or credentials.", new { },
            async (_, cancellation) => await RemindersCheck.RunAsync(cancellation)),
        Tool("check_ins_status", "Martlet's check-ins (Companion › Check-ins; docs/CONVERSATION.md#check-ins: short questions the " +
            "Thinking pool answers every few minutes, such as whether lingering emotes still fit), from a data directory: " +
            "check-ins.json (each check-in on or off, how often it runs, what it does with its answer, the owner's own with their " +
            "task and facts), check-ins-status.json written by the desktop on a companion PC (the pool member that can take them, " +
            "why each waits, the one running, runs and actions since Martlet started and each last run's time, member, duration and " +
            "result in a few words; never what was said, answered or reminded; each check-in's triggers, a trigger it keeps and what " +
            "started its last run, and the last touches that fired a trigger, in counts only) and the fixed rules (pace, minimum age, " +
            "quiet and idle waits, the job kind's priority and live floor rule, the trigger age and touch thresholds). Read-only.", new
        {
            dataDirectory = new { type = "string" }
        },
            async (arguments, cancellation) => await CheckInsCheck.StatusAsync(DataDirectory(arguments), cancellation)),
        Tool("check_ins_check", "Rehearse check-ins end to end with the production code, FIXTURE facts and canned answers (NOT AI): " +
            "the check-in job kind's rules, check-ins.json saved and read back (and a bad pace refused), when each built-in check-in " +
            "waits or runs (too young, hidden character, interval, you talking, nobody at the PC, nothing new, too little said lately, " +
            "Check now), the " +
            "message each sends, their runs on a production Thinking pool job board with a fixture member, reading answers (OFF " +
            "tags, KEEP, USUAL, REMIND:, SAY:, OK, a <think> block, chatter), and what Martlet does: a reply's lingering emote off " +
            "on a production HeldEmotes (never the owner's try), a reminder on a production context board that goes with exactly " +
            "one request, and something to bring up worded as the check-in's own beside a due reminder; and check-in triggers with " +
            "FIXTURE touches on a production touch ledger (a stroke across 3 zones, an intimate touch, a place touched again and " +
            "again fire their triggers once the touches settle, the ledger keeps every touch, and a triggered check-in runs only on " +
            "its own triggers, at most once per its pace, never held by a busy conversation). No model, network or " +
            "credentials.", new { },
            async (_, cancellation) => await CheckInsCheck.RunAsync(cancellation)),
        Tool("app_guides_check", "Rehearse App guides (Companion > App guides, on by default; docs/APP_GUIDES.md) end to end with " +
            "Martlet's own guide library service (AppGuideService), store (FileAppGuideStore), wiki reader (WebGuideBuilder with " +
            "WebAccess allowing loopback), chunker, index, notes (GuideRecall), program-name match, the rule for reading up on its " +
            "own, tool texts and job " +
            "scheduler, against fixtures on 127.0.0.1 (a tiny wiki about a made-up game and a DuckDuckGo-like search page, NOT real " +
            "sites; every fetch outside loopback is refused before it leaves this PC and counted): the defaults (on, ask on) and " +
            "libraries saved earlier (a saved off stays off, one without on is on, an unreadable one starts off), reads up from given pages and from " +
            "a search, keeps the guide, starts again from the folder, then the notes on a message while the game is in front (its " +
            "program named as Windows names it), for an unrelated message (none), for a message naming the game while another app " +
            "is in front, for a repeat the conversation already carries and while off; when Martlet reads up on its own (once a " +
            "session, a no kept across starts, Allow again, a game with a guide, a listed app, Read up when I start off) and its " +
            "notice; a game in front named as Windows names an Unreal game's program read up on with no model (on the list with its " +
            "guide and program names, its guide in the notes while in front, readable names); a read with the default caps (at " +
            "most 200 pages, 25 MB and 15 minutes), keeping what was read while reading and Stop reading; a series wiki (MediaWiki " +
            "fixture) read this game's pages first and never another game's, then more pages a stand-in Thinking pool member chose " +
            "from the links with notes on what the user does now, added to the guide; only the " +
            "indexes that can be used now kept in memory (none at start, the app in front's, the 2 used last, none after the idle " +
            "time); and the tools (read_up_on, search_guide, skip_guide: arguments, search_guide's answer, the reading-up job, one " +
            "at a time and 4 an hour). Reports counts, relevance and search time, also on a guide of 25,000 sections. Temporary folder, deleted " +
            "afterwards; reads no credentials.", new { },
            async (_, cancellation) => await AppGuidesCheck.RunAsync(cancellation)),
        Tool("app_guides_status", "Companion > App guides from a data directory (guides\\library.json): whether the library is saved " +
            "(else the defaults: both on), whether App guides are on and " +
            "read up when a game starts, and each app on the list with its program names, start pages, whether the owner said no " +
            "to reading up on it, its guide's pages, sections, size and time, and the last problem. Never a guide's text. Read-only.", new
        {
            dataDirectory = new { type = "string" }
        },
            async (arguments, cancellation) => await AppGuidesCheck.StatusAsync(DataDirectory(arguments), cancellation)),
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
        },
            async (arguments, cancellation) => await ConversationHistoryCheck.StatusAsync(DataDirectory(arguments), cancellation)),
        Tool("conversation_history_check", "Rehearse the record of conversations with the production code (ConversationHistory, " +
            "PastConversations and HistoryPlatforms) on synthetic conversations in a disposable folder: recording exchanges into month files, a line " +
            "cut short by a crash skipped after a restart, an ordinary message recalling nothing, \"Do you remember...\" and " +
            "\"What did we talk about yesterday?\" bringing back the right exchanges (never the conversation going on), " +
            "the replies in them under the persona's name (fixture persona Ivy; Martlet without one), " +
            "search_conversations by words and by time and its answers, deleting one conversation and everything, exchanges from " +
            "Telegram and Discord keeping their app, chat and message IDs (Discord never recalled in the talk window), what " +
            "deleting and editing one message asks of each app (48 hours on Telegram, never your DM messages on Discord, edited " +
            "replies cut to their pieces), editing and deleting single messages, the queue of changes for the apps (each app's " +
            "pace, a slow-down waited out, a refusal dropped, an unconnected app waiting, kept over a restart) with a fixture app " +
            "(not Telegram or Discord), how recall ranks (other forms of words, near-identical exchanges once, follow-ups, the " +
            "time named, exact phrases and speakers' names), and reading " +
            "bulkExchanges (1,000-100,000, default 20,000) exchanges with recall timings. Nothing leaves this PC.", new
        {
            bulkExchanges = new { type = "integer", minimum = 1_000, maximum = 100_000 }
        },
            async (arguments, cancellation) => await ConversationHistoryCheck.RunAsync(OptionalInt(arguments, "bulkExchanges"), cancellation)),
        Tool("memory_recall_check", "Rehearse how recall ranks remembered facts with the production memory code (Martlet.Memory: " +
            "BM25 over word stems, then the reranker) on synthetic facts in a disposable store: plurals and verb forms finding each " +
            "other (\"cats\" finds \"cat\", \"running\" finds \"runs\"), grammar words left out, facts holding more of the question " +
            "first, query words next to each other and in order first, a gentle recency boost, a near-duplicate of a better match " +
            "moving down (never one of another person), a snapshot ranking like the store, a deleted fact never recalled, and the " +
            "time to rank a query on a store of 512 facts and on a full store of 100,000 facts (median, p95 and max in microseconds, " +
            "and how long filling the store took; 512 facts must stay well under a millisecond, a full store under 10 milliseconds). " +
            "Optional query ranks those words against the sample facts, or against facts (1-100,000 strings, later " +
            "counts as newer) when given, and returns the ranked facts with their scores and matched terms. Never opens your " +
            "memory. Nothing leaves this PC.", new
        {
            query = new { type = "string" },
            facts = new { type = "array", items = new { type = "string" }, minItems = 1, maxItems = Martlet.Memory.MemoryLimits.MaximumFacts }
        },
            async (arguments, cancellation) => await MemoryRecallCheck.RunAsync(OptionalString(arguments, "query"), MemoryRecallCheck.Facts(arguments), cancellation)),
        Tool("memory_activity_check", "Rehearse memories kept for the game they come from with the production memory code " +
            "(Martlet.Memory: the activity on each fact, the recall filter, the store's search and another space's snapshot) on " +
            "synthetic facts in a disposable store: a fact made while playing Palworld recalled while playing Palworld or when the " +
            "words name it, never in another game or outside games; a fact for any game in every game; a fact for everywhere " +
            "always; a game's fact travelling with its activity while a fact for everywhere is written exactly as before; a " +
            "correction keeping where a fact matters; an activity of the wrong form refused; a lorebook entry keyed by the game " +
            "played now joining in (Martlet.Core's LorebookScanner) and the While you play prompt naming the game. Optional query (with activity, a " +
            "game's name, or none for no game) recalls those words against the sample facts. Never opens your memory. Nothing " +
            "leaves this PC.", new
        {
            query = new { type = "string" },
            activity = new { type = "string" }
        },
            async (arguments, cancellation) => await MemoryActivityCheck.RunAsync(OptionalString(arguments, "activity"),
                OptionalString(arguments, "query"), cancellation)),
        Tool("memory_upkeep_check","Rehearse search words, replaced facts kept as history and tidying memory with the production " +
            "code (Martlet.Memory's store and recall, and the desktop's own upkeep: the plan, the pool member's fact list, parsing " +
            "its answer and the store request) on synthetic facts in a disposable store: a question in other words (\"What should " +
            "I cook tonight?\") finding \"The user is vegetarian.\" only once the fact has search words; the WORDS, SAME and " +
            "OUTDATED lines of fixture answers (NOT AI) giving words, merging duplicates and keeping an outdated fact as history " +
            "with the date it stopped being true; lines across groups or about unknown numbers left out; history never recalled " +
            "and left out of a snapshot's current facts; a correction keeping the earlier version; the new fields travelling as " +
            "JSON while a fact without them is written as before; and forgetting a fact forgetting its earlier versions. Never " +
            "opens your memory; no model, network or credentials.", new { },
            async (_, cancellation) => await MemoryUpkeepCheck.RunAsync(cancellation))
    ];
}
