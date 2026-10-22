# MCP: Apps, home and memory

Part of [MCP in Martlet](../MCP.md): the tools and screens of the *Apps, home and memory* area of `martlet_guide` (MCP servers Martlet uses, the terminal, Discord, messaging, smart home, memory, reminders, check-ins and app guides). The [index](../MCP.md#pages) lists the other pages.

## Tools while you talk (MCP client)


Add MCP servers on **Companion > Tools**: *Browse MCP directory* finds and
installs one with a click ([MCP directory](#tools-while-you-talk-mcp-client),
below), and *Edit servers (mcp.json)* edits the file. The file is
`mcp.json` in Martlet's data folder and uses the format Claude Desktop, Cursor and
Cline use (VS Code's `"servers"` key works too; comments and trailing commas are
accepted), so a server's published configuration can be pasted as is:

```json
{
  "mcpServers": {
    "filesystem": {
      "command": "npx",
      "args": ["-y", "@modelcontextprotocol/server-filesystem", "${userHome}\\Documents"]
    },
    "my-http-server": {
      "type": "http",
      "url": "http://127.0.0.1:3000/mcp",
      "headers": { "Authorization": "Bearer ${env:MY_SERVER_TOKEN}" },
      "autoApprove": ["search"]
    }
  }
}
```

- **stdio** servers (`command`, `args`, optional `env` and `cwd`) run as programs on
  this PC with your permissions. `npx`/`.cmd` commands are started through
  `cmd.exe` with safe quoting. Every server Martlet starts is in one Windows job
  object, so it ends when Martlet ends (even if Martlet is killed).
- **Streamable HTTP** servers (`"type": "http"`, `url`, optional `headers`) are
  reached with MCP sessions; the older SSE transport is not supported.
- `${env:NAME}` and `${userHome}` are expanded, so secrets can stay in environment
  variables instead of the plain-text file. A missing variable stops only that server.
- `"disabled": true` turns a server off; `"autoApprove"` lists tools that run without
  asking (`true` or `"*"` for every tool of that server). The Tools page edits these
  for you (comments in the file are not kept when it does).
- `${secret:NAME}` is a value the MCP directory (below) kept in Windows Credential
  Manager instead of the file; a missing one stops only that server.

**MCP directory.** *Browse MCP directory* on the Tools page finds servers and
installs them without editing JSON. It searches one of two public directories that
speak the [MCP Registry API](https://registry.modelcontextprotocol.io/docs)
(`GET /v0.1/servers?search=&limit=&cursor=&version=latest`): the **GitHub MCP
Registry** (`api.mcp.github.com`, the default: it opens on the most popular
servers, by GitHub stars) or the **Official MCP Registry**
(`registry.modelcontextprotocol.io`, everything published, A to Z; its search
matches names only and can take up to a minute). Neither API takes a sort
parameter, so each always lists in that order, search results included; the
directory chooser shows it. Opening the
window loads the first page; what you search for is sent to that directory, and
nothing else is. Choosing a server shows its description, links and how Martlet
would run it:

- **npm** packages run with `npx -y <package>@<version>`, **PyPI** with
  `uvx <package>@<version>`, **OCI** images with `docker run -i --rm` (each
  environment variable passed with `-e`), **NuGet** tools with
  `dnx <package>@<version> --yes`, and **hosted** streamable HTTP addresses
  connect directly. A hosted address listed as SSE is offered (as streamable
  HTTP) only when it doesn't end in `/sse` and no streamable HTTP address is
  listed. `.mcpb` bundles, local-HTTP packages and other package types aren't
  installable and the window says why. Whether `npx`, `uvx`, `docker` or `dnx`
  is on this PC is shown before installing.
- The entry's environment variables, headers, arguments and `{placeholders}`
  become fields (required ones first, optional ones in *Optional settings*; empty
  optional ones are left out). Secret fields are saved in Windows Credential
  Manager (scoped to this mcp.json) and written as `${secret:<server>.<NAME>}`;
  typing `${env:NAME}` in any field uses an environment variable instead.
- *Install and start* adds the entry to mcp.json with `"registry"` (the
  directory name) and `"version"`, then starts the servers. Installing an entry
  that is already installed (same `registry`) replaces it; using another
  server's name asks first. Each mcp.json server on the Tools page has *Remove*,
  which deletes its entry and the secrets only it used.

**When servers run.** Nothing starts when Martlet starts. Enabled servers start in
the background when you open a talk window (or press *Start servers now*). A reply
never waits for them: a server that is still starting (or stopped) offers the tools
it listed last in this session, so the tool list stays the same while servers come
and go, and a call to it waits at most 10 seconds for it to start or tells the
model that it isn't running. A server whose tools were never listed yet offers
none until it is ready. Servers that stopped are
retried when a talk window opens again or from the Tools page. Each server's state,
tools and recent error output are on the Tools page.

**Tool names.** Each server tool is offered as `<server>_<tool>` (made safe for
function calling), for example `home-assistant_HassTurnOn`. The name never
depends on which other servers run, so the start of every request stays the same
for prompt caches.

**Which replies get tools.** Only replies to what you say or type (and Martlet's
own reply bringing up finished background work), and only when
Thinking uses OpenAI or a Chat Completions endpoint (local Ollama, LM Studio,
OpenRouter, NVIDIA Build...). Screen and camera glances and memory requests never
get tools, and a paired Martlet host's gateway has no function calling, so replies
from a host's model don't offer tools. If the model rejects a request because it
doesn't support tools (many small local models), Martlet asks it once more without
tools and stops offering them to that model for a week on this PC
(`tools-unsupported.json` in the data folder). Martlet's own `think_longer` and
`cancel_thinking` ([Thinking longer](thinking.md#thinking-longer)) come first, then the
terminal, then the servers' tools.

**Confirmations.** Before each call the talk window shows the tool, its server and
the exact arguments, with *Allow once*, *Always allow this tool* and *Deny*; it is
part of the talk window (not a separate dialog), so answering doesn't end the
action. Unanswered calls are declined after 60 seconds, and a declined call tells
the model so. *Always allow* adds the tool to the server's `autoApprove` list.
The model, not you, chooses what to pass, and text a tool reads (a web page, file
or email) can try to steer it, so only skip confirmation for tools whose effects
you are comfortable with. Terminal commands ([Terminal](#terminal)) ask with *Run
this command?* and offer only *Allow once* and *Deny*.

**Servers other features manage.** A Martlet feature can add its own server with
`McpToolService.SetManagedServer(name, definition with ManagedBy, policy)` (for example a Home
Assistant MCP endpoint). It is never written to mcp.json, appears on the Tools page as
managed by that feature, and loses to an mcp.json entry with the same name. Its
`policy(tool, arguments)` decides each call first: `AutoApprove`, `AskEveryTime` (no
*Always allow*), `Deny` (the model is told it's blocked) or `Default` (the normal
rules); a policy that throws counts as `AskEveryTime`. Managed servers never offer
*Always allow*.

**What is sent where.** Tool names, descriptions and parameter schemas go to the
Thinking model with each reply that offers tools; each tool result (cut to 120,000
characters, and to its share of the reply's 640 KB tool budget and of the room left in
its context) goes to the same model. A cut result ends with *[output cut short: the first
N of M characters, as much as this reply has room for]*, and the Thinking trace notes the
cut. The model is told that tool output is data, not instructions.

**Bounds.** At most 128 tools (96 KB of descriptions) per reply, 16 calls per round
and 4 tool rounds per reply; each round is one more LLM request under the same
action, and the reply's last request must answer in text. A tool call has 60
seconds; a reply that may use tools has 140 seconds inside the 150-second action.
Calls run one at a time. The Tools page lists recent tool use (server, tool,
outcome and a short argument preview) until Martlet closes; it is never written to
disk.

**Protocol.** Martlet's client implements MCP 2025-06-18 (and accepts servers that
answer with 2025-03-26 or 2024-11-05): `initialize`, `tools/list` (with paging and
`notifications/tools/list_changed`), `tools/call` and `notifications/cancelled` when
a reply stops. It declares no client capabilities, so servers can't ask it for
sampling, roots or elicitation. Text, resource text and structured results reach
the model; images and audio are described, not sent. The client library is
`src\Martlet.Mcp.Client`; the Desktop glue is `McpToolService`.

### Terminal

**Companion > Tools > Terminal** lets Martlet run commands on this PC when you
ask, as one built-in tool beside the MCP servers' tools: `run_terminal_command`
with a `command` string (at most 4,000 characters). It is **off by default** and
its settings stay on this PC (`terminal.json` in the data folder; never part of
the settings your computers share):

- *Let Martlet run terminal commands*: off until you turn it on.
- *Shell*: Windows PowerShell (default), PowerShell 7 (`pwsh`, when installed) or
  Command Prompt.
- *Starts in*: your home folder (default) or a folder you choose.
- *Time limit*: 15 seconds, 30 seconds (default) or 1 minute per command, so a
  confirmation and the command both fit in one reply.
- *Ask before every command*: on by default. The talk window shows the command
  and its start folder with *Allow once* and *Deny* (never *Always allow*); no
  answer within 60 seconds means Deny. Turning it off asks once to be sure.

Each command runs hidden in a new shell (nothing carries over between commands),
as you and never as administrator, with its input closed so a command that waits
for typing ends at once; `NO_COLOR=1` and `GIT_TERMINAL_PROMPT=0` keep output
plain and git from waiting for a password. Output and errors are read as UTF-8
(Command Prompt runs the command in a cmd started after `chcp 65001`), in the
order they arrive, without color codes; long output keeps its first 3,000 and
last 7,000 characters. At the time limit, or when the reply stops, the shell and
everything it started are stopped; a program the command started in the
background keeps running, and output it holds open is not waited for beyond a
second after the shell ends. The model gets the exit code (or that it was
stopped) and the output; the Tools page's *Recent tool use* lists each command
(`Terminal > command`), and the desktop log notes each run without the command or
its output (`{"name":"logs_tail","arguments":{"contains":"Terminal:"}}`).

The terminal follows the same rules as MCP tools: only replies to what you say or
type, only on a Thinking route that does function calling, and its settings as
they are now apply to the next command even mid-reply (turning it off blocks the
rest). The tool's description tells the model the shell, start folder, time limit
and whether you approve each command, and asks it to prefer commands that only
read and to say the result in a sentence or two. The command, its start folder and
what it prints go to the Thinking model.

### Searching past conversations

**Companion > Memory > Conversation history > Let Martlet search the record on
its own** (off by default) gives every reply on a route that does function
calling Martlet's own `search_conversations` (`query`, words to look for, and/or
`when`: today, yesterday, 3 days ago, last week, a weekday or YYYY-MM-DD), after
`think_longer` and `cancel_thinking`, always worded the same. It never asks
first, searches the [record of conversations](../MEMORY.md#conversation-history)
on this PC from memory (other conversations than the one going on) and returns
at most six exchanges with their dates, as data, never instructions. *Recent
tool use* lists each call (`Martlet > search_conversations: found 2`); the
desktop log notes how many were found, never what (`Past conversations:`).

### Creations

While at least one kind of creation is registered (songs, once singing lands),
every reply on a route that does function calling also gets Martlet's own
`list_creations` (optional `kind` and `query`) and `perform_creation` (`id` from
the list, optional `options` object), after `think_longer`, `cancel_thinking` and
`search_conversations` and always the same two with the same texts, so the start of every request stays
the same. Neither asks first. `perform_creation` hands the creation to its kind's
handler (a song is sung by the conversation); an unknown id, a kind this Martlet
doesn't know, no handler or a creation still copying to this PC get a clear
refusal for the model. The owner never presses Play: see
[Creations](../CREATIONS.md). The Tools page's *Recent tool use* lists each call
(`Martlet > perform_creation: performed`), never titles or options.

### Reminders

On a PC that keeps reminders (any with a data folder), every reply on a route
that does function calling also gets Martlet's own `reminders` (`action` set,
list or cancel; `text`, `in_minutes` or `at` for set; `id` for cancel), last,
after `manage_memories`, always worded the same. It never asks first. Set
returns the id and the time it is due ("Reminder 3f9a1c set for 4:12 PM (1 h
from now)"), so the model needs no clock of its own. *Recent tool use* lists
each call (`Martlet > reminders: set`), never the text; the desktop log notes
each offer, take and reminder said by id only (`Reminders:`). See
[Reminders](../CONVERSATION.md#reminders).

### Check-ins

[Check-ins](../CONVERSATION.md#check-ins) aren't a tool the model calls: every few
minutes a Thinking pool member answers one short question for Martlet (do the
lingering emotes still fit, does the gaze a reply chose still fit, did the
character keep its promises, did it stay in character, does it keep saying the
same things, the four that wait for a signal (welcome back, an unanswered
question, a call, someone else here), what to say after the next message
(think ahead), what is worth bringing back later (the callback list), whether
to pick a quiet conversation back up (quiet moments) and the owner's own),
and Martlet acts on the answer. Nothing changes in the conversation's tools or
instructions. A reminder for the next reply, what a check-in adds to what
Martlet knows, or its thoughts ahead, go in the notes of that one
message only (context board source `check-in-<id>`), and the desktop log notes
each run with words and counts only (`Check-ins:`).

The talk and conversation settings also put their explanations behind a *?* with a wiki link
(`help` is the card text): Companion › Listening has `Help-TalkListensAgain`
(`Talking-to-Martlet#ways-to-talk`), `Help-SmartTurn`, `Help-TalkEarlyReplies` and
`Help-TalkWordCheck` (`#how-martlet-takes-turns`), `Help-TalkOptional` and
`Help-TalkBargeInBehavior` (`#talking-over-martlet`), `Help-EchoCancel` (`#speakers-and-echo`),
and `Help-TalkHearPcAbout` and `Help-SoundDigest` (`Vision#hearing-what-your-pc-plays`);
Companion › Hearing has `Help-TalkHearVoiceAbout` (`#hear-how-you-say-it`); Companion › Voice
has `Help-TalkSpeaks` (`Voices#volume-and-muting`) and `Help-Fillers` (`#how-martlet-answers`);
Companion › Replies has `Help-<setting ID>` for each setting, such as `Help-RepliesThinking`
and `Help-RepliesContextTokens` (`Thinking#reply-settings`); Companion › Thinking's *If Thinking
fails* has `Help-FallbackAbout` (`Thinking#when-thinking-fails-or-is-slow`); Companion › Vision's
*What Martlet sends* (`VisionDisclosure`, its first sentence) has `Help-VisionSends` with all of
it (`Vision#privacy`); and the Conversations page has `Help-HistoryAbout`
(`#the-conversations-page`). The talk window's error lines are one sentence each; the wiki's
`Talking-to-Martlet#messages-in-the-talk-window` lists what to do for each. A job page's *Keys from
before* card has `Help-OldKeys-<role>` (`App-Settings-and-Updates#keys-from-before`). Settings'
update, appearance, exit and troubleshooting status lines are one sentence each; the wiki's
`App-Settings-and-Updates` and `Troubleshooting#the-troubleshooting-window` have the rest. The Linux
and Mac Companion app has its own `HelpMark` (`src/Martlet.Companion/HelpMark.cs`):
`Help-CompanionThinking` next to Settings › *Thinking* (`Linux-and-Mac#what-the-early-app-can-do`),
with `-Card` and `-Wiki` IDs like `HelpTip`; MCP doesn't drive that app.

Companion › Check-ins reads through `ui_snapshot`: `CheckInsNow` (how many are on
and the member that takes them first, or why they can't run), `CheckInsLast`
(the last check-in that ran, when, on which member and what came of it, never
what was said or answered) and, for each check-in, `CheckInStatus-<id>` (why it
waits, its last run, runs and actions since Martlet started). `Help-CheckInsAbout`
next to *Now* explains check-ins in one short line (`help`); its card links to the
wiki's *Check-ins* section (`Help-CheckInsAbout-Wiki`), and *Your own check-ins*
has `Help-CheckInsOwn` with a link to *Your own check-ins*. Every check-in,
built-in or the owner's own, has the same editor: `CheckInOn-<id>`,
`CheckInEvery-<id>` and `CheckInOutcome-<id>` show on its card, and the rest
is under *More settings* (`Fold-CheckInMore-<id>`, closed at first: click it
before you read or change these), each group with its own *?*
(`Help-CheckInFacts-<id>`, `Help-CheckInWhen-<id>`, `Help-CheckInTriggers-<id>`,
`Help-CheckInScript-<id>`, `Help-CheckInTools-<id>`, `Help-CheckInNeeds-<id>`,
and the placeholders in `Help-CheckInPrompt-<id>` or `Help-CheckInTask-<id>`,
each with a link to its *Check-ins* subsection in the wiki; `Help-CheckInOutcome-<id>`
after *Its answer* links to *What its answer does*, and a built-in check-in's description shows its
first sentence with `Help-CheckInAbout-<id>`):
`CheckInFact-<id>-<fact>` (what it
gets to know: `Conversation`, `Persona`, `Replies`, `Said`, `Character`,
`Work`, `Screen`, `Sound`, `Presence`, `Touches`, `Activity`, `People`, `WhileAway`,
`Latest`, `Heard`, `Callbacks`),
`CheckInWhen-<id>-<condition>` (when it runs: `CharacterShows`, `EmoteShown`,
`GazeChosen`, `Talked`, `SomethingNew`, `Persona`, `Replies`, `NewReplies`,
`Sayings`, `SlowWhenKept`, `CameBack`, `NotOnCall`, `ActivityChanged`,
`Between`, `Unanswered`, `Attention`, `SongEnded`, `ScriptChanged`,
`SomeoneElse`, `Lull`), `CheckInTrigger-<id>-<trigger>` (what starts it at once:
`TouchesEnded`, `IntimateTouch`, `StrokeAcrossZones`, `KeepsComingBack`,
`ExchangeEnded`, `Spoke`), its
hours (`CheckInFrom-<id>` and `CheckInUntil-<id>`, such
as *8 AM* and *10 PM*, used with `Between`) and its cap
(`CheckInMostPerHour-<id>`: *No limit*, *Once an hour* or *2*, *3*, *4*, *6* or
*12 times an hour*), the model it
needs (`CheckInNeeds-<id>-Text`, `-Vision` and `-Audio`: checked, and disabled
when an input forces it) and what each run takes (`CheckInScreenshot-<id>`,
`CheckInRecording-<id>` and `CheckInSeconds-<id>`), and the tool sets it may
call (`CheckInTools-<id>-<setId>`, such as `CheckInTools-c1-next-reply`; its
`help` says what the set does and names its tools). Each fact, condition and
trigger box, `CheckInOutcome-<id>` and `CheckInSeconds-<id>` also return `help`: what
the choice does (the same text as its tooltip), and which models hear a whole
minute of recording. A built-in check-in's card has its prompt box
(`CheckInPrompt-<id>`), its state (`CheckInPromptState-<id>`, such as *Edited.
About 180 tokens.*, with *Saving...* until it is saved) and *Use built-in
settings* (`CheckInReset-<id>`: Martlet's own prompt, facts, conditions,
answer, inputs and tools back; On and Every stay). Each check-in has *Copy as your own*
(`CheckInCopy-<id>`), which adds an own check-in (off) with the same prompt and
choices. The script and prompt boxes
(`CheckInScript-<id>`, `CheckInPrompt-<id>`) have automation IDs, but
`ui_snapshot` never reads them: the owner's words stay on the page.
`CheckInsOpenPool` only opens a page. The boxes and choices, the name, task,
prompt and script boxes, *Use built-in settings*, *Copy as your own*, *Add a
check-in* (`CheckInAdd`)
and *Remove* (`CheckInRemove-<id>`) save `check-ins.json` or the prompts, and
*Check now* (`CheckInRun-<id>`) sends the check to a
Thinking pool member, which may be a paid provider, so they need
`--allow-ui-effects`. *Check now* on a check-in with a script runs that script,
takes a screenshot or sends a recording, so use it only on a disposable data
directory with a check-in you made for the test.

Setting `MARTLET_CHECK_INS_FIXTURE` to a text file before launching the desktop
makes every check-in read its answer from that file instead of asking the
Thinking pool (FIXTURE - NOT AI; read again before each run, and check-ins run
then without a pool member). `CheckInsNow` and each run's member say *FIXTURE -
NOT AI*, and `check-ins-status.json` says `pool.fixture`. A file with a
a `REMIND:` line, a `SAY:` line, a `KNOW:` line and a `PLAN:` line answers all
four kinds of the owner's own check-ins that use a line, so *Check now* shows the
whole flow on a disposable data directory: a reminder waiting for the next
reply, something Martlet brings up, background for the next reply, or
thoughts ahead for the next reply. A reply's end fires Think ahead (`ahead`)
with that file too: on a disposable data directory with Thinking on
`model_lab`, send two messages in the talk window; `check_ins_status` shows
Think ahead's run (*thoughts ahead wait for the next reply*), the second
reply's `LiveTurnInputs` says *Last reply took your words and 1 context
note.*, and the desktop log says *Context board: the request took 1 note
(check-in-ahead; ...)*. Add `CALLBACK:` lines to the file (such as
`CALLBACK: quote - FIXTURE they said "one more try"`), and the same reply
fires Callback list after Think ahead:
`check_ins_status` shows its run (*the callback list has 2 things now*) and
`desktop.callbacks.count` 2, and `CheckInStatus-lull` says Quiet moments
*waits for 3 min of quiet*.

[Talking to someone else](../CONVERSATION.md#talking-to-someone-else) is
checked on the desktop the same way. Launch with a fixture file that holds
`ASIDE: FIXTURE on the phone with their mom`, open `CompanionTab-CheckIns`
and click `CheckInRun-aside`: `check_ins_status`'s `desktop.aside` says
`holding` true with `since`, `until` (3 minutes later) and `aboutCharacters`,
`CheckInStatus-aside` says *Martlet holds back while you talk to someone
else*, and the desktop log says *Check-ins: Talking to someone else says you
talk to someone else, so Martlet holds back ...*. Write `BACK` to the file and
click it again: `holding` is false and the status says *Martlet talks as usual
again*. Its trigger box `CheckInTrigger-aside-Spoke` (*You say something*) is
checked; that trigger needs always listening and a microphone.

[Check-in triggers](../CONVERSATION.md#check-in-triggers) are driven live with
`character_touch` and `character_stroke` (both need `--allow-ui-effects` and
the character showing with touch zones): about 1.2 seconds after the last
touch, `check_ins_status`'s `desktop.touches.lastFired` names the triggers
that fired, and a check-in that starts on one shows its run with
`last.trigger`, or keeps it as `pending` while its pace runs. Check one on a
disposable data directory: write a `check-ins.json` with an own check-in that
has `"Triggers": "TouchesEnded"`, launch with `MARTLET_CHECK_INS_FIXTURE`, show
the character (`ToggleCharacter`), open `CompanionTab-Touch` so Martlet places
its first zones, then tap it.

## Local MCP control (Windows)

`memory_sync_status` reads [one memory on every computer](../MEMORY.md#one-memory-on-every-computer)
from a data directory (optional absolute `dataDirectory`, default the current
user's; the disposable one in `Invoke-MartletMcp.ps1`): `sync` as above,
`state` (`none` before the first memory sync, else `loaded`), `syncedAt`,
`facts` (how many facts the store had at the last sync), `byComputer` (how many
of those each computer wrote) and `forgotten` (the tombstones every computer
agreed on). Those top-level fields are the `memory-sync.json` from before
accounts (until it moves to the owner's space). `spaces` lists each
[memory space](../MEMORY.md#memory-spaces-on-the-desktop) with a sync state
(`memory-sync\<space>\memory-sync.json`): `space` (its ID) and `sync` with the
same `state`, `syncedAt`, `facts`, `byComputer` and `forgotten`. It reads only
those files (IDs, revisions, digests and device IDs), never a fact, and
contacts nothing.

`memory_status` reads what Martlet remembers and [whose](../MEMORY.md#whose-memories)
from a data directory (optional absolute `dataDirectory`, default the current
user's; the disposable one in `Invoke-MartletMcp.ps1`): `memory` (`on`, `off`,
`on (default)` for settings from before memory, `not set up`), `storage`
(`Martlet folder` or `custom folder`), `state` (`none` without a store file,
`loaded` or `unreadable`), `voiceList` (`voices.json`: `none`, `loaded` or
`unreadable`), `facts`, `typed`, `fromConversation`, `expiring`, `replaced`
(facts kept as history, [Search words, history and tidying](../MEMORY.md#search-words-history-and-tidying)),
`withSearchWords` (facts with hidden search words) and `whose`:
`everyone` (facts not tied to a voice), `voices` (each voice facts belong to,
by its tag such as `V3`, with `named`, `owner`, `linked` (to a person's
account) and `facts`) and
`forgottenVoices`. Those top-level counts are the store in the memory setting's
folder: this PC's memories from before accounts (until they move to the
owner's space; then `state` is `none`) or a custom folder. `spaces` lists each
[memory space](../MEMORY.md#memory-spaces-on-the-desktop) kept in the data folder
(an account's own space in `accounts\<32 hex>\memory`, the household and
character spaces in `memory-spaces\<space>`): `space` (its ID) and `store` with
the same `state`, `facts`, `typed`, `fromConversation`, `expiring`, `replaced`,
`withSearchWords` and `whose`. `upkeep` reads the desktop's `memory-upkeep.json`
(`state` `none`, `loaded` or `unreadable`; `storesChecked`, how many stores
tidying has checked for duplicates; `last`, the last pass's `at`, `space`,
`outcome`, `listed`, `groups`, `need_words`, `retired`, `merged`, `worded` and
`member`). `where` counts the current facts by where they matter
([Memories for a game](../MEMORY.md#memories-for-a-game)): `everywhere`,
`anyGame` and `activities` (each game a fact is kept for, `where` such as
*while playing Palworld*, and its `facts`).
It reads each store's file as JSON without opening or locking a store (the
desktop can keep running), and never returns a fact's text, a person's name, a
voice ID or a path. `limits` gives the most facts a store holds (`facts`, 100,000),
characters per fact (`factCharacters`, 32,768) and store bytes (`storeBytes`,
256 MiB). It contacts nothing.

`memory_brief_status` reads the [memory brief](../MEMORY.md#the-memory-brief)
from a data directory's `memory-brief.json` (optional absolute
`dataDirectory`, default the current user's; the disposable one in
`Invoke-MartletMcp.ps1`): `state` (`none` before a brief was written or turned
off, `loaded` or `unreadable`), `on`, `written`, `by` (`thinking` or `owner`),
`writtenAt`, `characters`, `lines`, `facts` (how many facts it was written
from) and `people` (how many people it names), and `limits`
(`maximumCharacters`, `minimumFacts`, `newFactsToWriteAgain`, `triesPerHour`,
`gapMinutes`, `gapAfterLossMinutes`). It never returns the brief or a fact,
and contacts nothing.

`memory_brief_check` (no arguments) rehearses the memory brief with the
production code (`MemoryBriefService`, the `HelperJobs` router and the
`memory-brief` shared-settings section) on a disposable memory store, with a
fixture Thinking pool member whose answers are FIXTURE - NOT AI. Its `steps`
show that two facts write nothing; a third has the pool member write the brief
(route `pool`, kind `memory-brief`, priority `Low`) from each fact with whose it
is; the first message of a conversation carries it and the next don't; a
forgotten fact holds it back (`waiting`) until it is written again without it a
minute later; your own text stays; off sends nothing; *Write it again*
replaces your text; and a second simulated computer takes the same brief
through the shared settings and sends it. `passed` is true when every step
passed. No model, network or credentials; it never opens your memory.

On the desktop, Companion > Memory's *Memory brief* card (`CompanionTab-Memory`)
shows `MemoryBriefStatus` (text: off, not written yet, written how long ago
from how many memories, your own text, held back because a memory it used
changed, or why it couldn't be written; never the brief) and the check box
`MemoryBriefOn` (its `checkedState`; toggling it saves `memory-brief.json`, so
it needs `--allow-ui-effects`). The brief's box `MemoryBriefText` is never a
readable value. `MemoryBriefSave` (*Save my changes*) and `MemoryBriefWrite`
(*Write it again*, which asks a model) need `--allow-ui-effects`.

Companion > Memory's *Finding by meaning* card shows `MeaningSearchStatus`
(text: off; checking; not ready because Ollama isn't installed or running on
this PC or the model isn't downloaded; waiting because loading the model could
unload another one; or on, with how many memories, guide sections and report
sections it can find by meaning and what it is reading now; counts and fixed
wording, never a fact or a text), the check box `MeaningSearchOn` (its
`checkedState`; `ui_toggle` saves `meaning-search.json`, so it needs
`--allow-ui-effects`) and the model's name in `MeaningSearchModel` (typing in
it saves too). `MeaningSearchCheck` (*Check again*) only asks this PC's Ollama
(loopback) what it has, so it is a safe click. `MeaningSearchDownload`
(*Download*, shown only while the model isn't downloaded) asks first
(`MeaningSearchDownloadQuestion`) and downloads a model, so it needs
`--allow-ui-effects`.

`memory_forget` deletes remembered facts the way Companion > Memory's Delete
does (`DeleteManyAsync` in `src\Martlet.Memory`): each fact whose text contains
`contains` (any case, at most 200 characters) or whose ID is in `ids` (at most
1,000 GUIDs). It looks in every memory store of the data folder that
`memory_status` lists, or only in `space` (a space ID, or `this-pc` for the
store in the memory setting's folder). `preview: true` only counts the facts.
Without `preview` it needs `--allow-changes`. A running desktop opens a store
only during each operation, so the tool waits up to 15 seconds for the store to
be free. Then the desktop's memory sync sees the facts gone and forgets them on
the owner's other computers. It returns `preview`, `matched`, `deleted` and
`spaces` (each with `space`, `facts`, `matched`, `deleted` and the matched fact
`ids`), and never a fact's text. Without `dataDirectory`,
`Invoke-MartletMcp.ps1` gives it a disposable data folder.

`memory_where` sets where remembered facts matter, the way Companion >
Memory's *Where it matters* does
([Memories for a game](../MEMORY.md#memories-for-a-game)): each current fact whose
text contains `contains` or whose ID is in `ids` (as `memory_forget` finds
them, in every store or only in `space`) is kept for `where`: `everywhere`,
`games` (any game) or a game's name (only while that game is played). Replaced
facts (history) don't change. `preview: true` only counts the facts; without it
the tool needs `--allow-changes` and waits up to 15 seconds for a running
desktop to let the store go. It returns `preview`, `where` (*everywhere*,
*while gaming* or *while playing <game>*), `matched`, `changed` (or
`wouldChange` with `preview`) and `spaces` (each with `space`, `facts`,
`matched`, `changed` and the matched fact `ids`), and never a fact's text.

`memory_activity_check` (optional `query`, optional `activity`: a game's name)
rehearses [memories for a game](../MEMORY.md#memories-for-a-game) with the
production code (`MemoryActivity`, `MemoryActivityFilter`, `MemoryStore` and
`MemorySnapshot` in `src\Martlet.Memory`) on synthetic facts in a disposable
store, and returns `{passed, failures, steps, asked, sampleFacts, note}`. The
steps: playing Palworld recalls its favorite and a fact for everywhere but not
another game's; playing Elden Ring never recalls Palworld's favorite; with no
game only facts for everywhere come back; naming Palworld recalls its facts
with no game and in another game; a fact for any game comes back in every game
and not outside games; a search without the filter (the Memory window,
`manage_memories` `find`) finds every fact; a snapshot leaves out the same facts
as the store; a game's fact travels with `activity` while a fact for
everywhere is written exactly as before; a correction keeps where a fact
matters and choosing it moves the fact; an activity of the wrong form is
refused; a lorebook entry keyed by the game played now joins in though the
words don't name it (`LorebookScanner` in `src\Martlet.Core`); the *While you
play* prompt names the game; and game names compare without regard to case or
spacing. With
`query`, `asked.recalled` lists the sample facts (by key) those words recall
while `activity` is played (none: no game). It never opens the owner's memory
and contacts nothing.

`memory_recall_check` (optional `query`, optional `facts`: 1-100,000 strings)
rehearses how recall ranks remembered facts
([Lexical retrieval and reranking](../MEMORY.md#lexical-retrieval-and-reranking))
with the production code (`MemoryStore` and `LexicalIndex` in
`src\Martlet.Memory`, on the shared [search engine](../SEARCH.md)) on synthetic facts in disposable stores, and returns
`{passed, failures, steps, query, note}`. The steps: plurals and verb forms
find each other (*Do I have any cats?* finds the cat fact, *running* finds
*runs*), grammar words are left out, the fact that holds more of the question
comes first, a message of only grammar words has no words to look for, a
snapshot ranks exactly like the store, a deleted fact is never recalled, query
words next to each other and in order rank first, recency is a gentle boost
(under 11%), a near-duplicate of a better match moves below a different match
(never one of another person), and the time to rank one query on a store of
512 facts and on a full store of 100,000 facts (`rankMedianMicroseconds`,
`rankP95Microseconds`, `rankMaxMicroseconds`, `storeRetrieveMedianMicroseconds`
and `fillMilliseconds`, how long filling the store took; the 512-fact step passes
when the median is under 1,000 and the full-store step when it is under 10,000). With `query`, `query.ranked` lists the facts that
query recalls (`fact`, `score`, `matchedTerms`, best first) from the sample
facts, or from `facts` when given (a later fact counts as newer), and
`query.words` shows the words recall looked for. It never opens the owner's
memory and contacts nothing.

`memory_upkeep_check` (no arguments) rehearses [search words, history and
tidying](../MEMORY.md#search-words-history-and-tidying) with the production code
(`MemoryStore` in `src\Martlet.Memory` and the desktop's own
`src\Martlet.Desktop\MemoryUpkeep.cs`, linked into the server) on synthetic facts
in a disposable store, and returns `{passed, failures, steps, note}`. The steps:
*What should I cook tonight?* doesn't find *The user is vegetarian.* without
search words; a pass asks words for every fact without them and lists them
numbered with their dates; fixture `WORDS` lines (NOT AI) give words without
changing when a fact last changed; the question then finds the fact; the next
pass groups *vegetarian* with *doesn't eat meat* and *lives in Portland* with
*moved to Seattle*; lines across groups, about unknown numbers or calling the
newer fact outdated by the older are left out; a fixture `SAME` line merges the
duplicates (the kept fact gets the words of both); a fixture `OUTDATED` line
keeps the older fact as history, dated when the newer one was learned; history
is never recalled and a snapshot's current facts (`MemorySnapshot.Live`) leave it
out; a correction keeps the earlier version; the new fields travel as JSON while
a fact without them is written as before; forgetting a fact forgets its earlier
versions; and search words from a model line are cleaned up. It never opens the
owner's memory and contacts nothing.

`conversation_history_status` reads the [record of conversations](../MEMORY.md#conversation-history)
from a data directory (optional absolute `dataDirectory`; the disposable one in
`Invoke-MartletMcp.ps1`): `memory` (`on`, `off` or `not chosen`), `preferences`
(`conversation-history.json`: `keep`, on by default, and `search`, off by
default), `recording` and `recallWhenMentioned` (memory on and `keep`), `tool`
(whether replies are offered `search_conversations`, whether the Thinking route
does function calling, and the tool exactly as the model gets it with its
`utf8Bytes` and `estimatedTokens`), `prompt` (Companion > Prompts > *Past
conversations* as sent), `record` (`files`, `bytes`, `conversations`,
`exchanges`, `skippedLines`, `notIndexed`, `oldest`, `newest` and `apps`, the
exchanges per app: `pc`, `telegram`, `discord`, `whatsapp`),
`platformChanges` (`conversations\platform-changes.json`: `pending`,
`pendingByApp`, `done`, `refused`, `gaveUp`, `lastProblem`) and `sync` (the
last [record sync](../MEMORY.md#one-record-on-every-computer) of the owner's
record, from `conversations\sync\status.json`, or null when it never synced:
`at`, `months`, `exchanges`, `hosts`, `same`, `down`, `old`, `denied`,
`taken`, `deleted`, `recorded`, `unreadable`, `problem`). The choices are the
`conversation-history` shared setting, so `settings_sync_status` shows its
value too. Never what was said; it contacts nothing.

`conversation_history_check` (optional `bulkExchanges`, 1,000-100,000, default
20,000) rehearses the record with the production code (`ConversationHistory`,
`PastConversations` and `HistoryPlatforms` in `src\Martlet.Conversation`) on synthetic
conversations in a disposable folder and returns `{passed, failures, steps,
tool}`: recording exchanges into month files, a line cut short by a crash
skipped after a restart, an ordinary message recalling nothing, *Do you
remember what I said about Kyoto?* and *What did we talk about yesterday?*
bringing back the right exchanges (never the conversation going on) with the
notes' size, the replies in them under the persona's name (*Ivy: ...* and
*Ivy, on its own: ...* for the fixture persona Ivy, *Martlet: ...* without
one), `search_conversations` by words and by time and what it tells the
model, deleting one conversation and everything, exchanges from Telegram and
Discord keeping their app, chat and message IDs over a restart (Discord never
recalled in the talk window), what deleting and editing one message asks of
each app (Telegram for 48 hours, never your Discord DM messages, an edited
reply cut to its pieces), editing and deleting single messages, the queue of
changes for the apps (`PlatformChanges` with a fixture app, not Telegram or
Discord: its pace, a slow-down waited out, a refusal dropped, an unconnected
app waiting, kept over a restart, *Stop waiting changes*), how recall ranks
(*the apple tree I was planting* finding *planted three apple trees*, four
near-identical exchanges coming back once and one already in the notes keeping
its twins out, a follow-up ranked with the trip it follows, *yesterday* finding
the evening before, an exact phrase and a speaker's name ranked first, the
answer that follows a match coming with it), and reading `bulkExchanges`
exchanges with the time recall takes (`recallMedianMs`, `recallMaxMs`) and the
time an ordinary message's check takes (`ordinaryMessageCheckMs`). It is not a
real conversation or Thinking model; the desktop's tests and `ui_*` tools cover
the talk window.

`memory_sync_selftest` (no arguments) rehearses shared memories end to end with
the production code (`src\Martlet.NodeLinkCheck`, mode `memories`,
`MemoryRehearsal.cs`; returns `{exitCode, report}`): two real gateways
(`lab-memory-1`, `lab-memory-2`; Kestrel, pinned TLS, signed requests, an
in-memory `memories.json`) and three simulated desktops (`lab-desktop-a..c`),
each with a real `Martlet.Memory` store in a temporary folder, the desktop's
paired client (`HostMemories.cs`) and the real sync engine
(`Martlet.Core.Sync.MemorySyncNode`) wired as the desktop wires them. Its steps:
A remembers a typed and a conversation fact and both hosts keep them; B takes
them (same IDs, revisions and provenance) and recalls the cat fact; B edits it
and A takes revision 2; A remembers a fact that belongs to a voice, B takes it
as that voice's and makes it everyone's, and A takes that (a fact with no voice
has no `voice_id` field); A forgets a fact and every computer forgets it for good;
offline edits on A and B of different facts (both kept) and of the same fact
(the later edit wins); a host that was down while A remembered a fact gets it
after restarting; a new computer with a fact of its own takes everything and
shares its fact; a fact expiring in two seconds is forgotten everywhere once
expired; a newer Martlet's fact passes through hosts and desktops without
entering this version's stores; a new memory folder takes everything again and
forgets nothing; 600 old conversation facts from two computers end as the same
512 everywhere in this rehearsal's small store (oldest conversation facts
forgotten, typed facts kept, and the sync says how many didn't fit); memory
spaces ([accounts](../ACCOUNTS.md#memory-spaces)): A merges a fact into Sam's
`account-<id>` space on both hosts with `MergeMemorySpaceAsync`, B reads it
there (`ReadMemorySpaceAsync`) with the same digest
(`ReadMemorySpaceDigestAsync`), Alex's space is refused to B
(`memories.space_denied`: Alex isn't signed in there), `household` and the old
document stay without it, it survives a host restart, a bad space ID is refused
before anything is sent, and the step names the spaces the host keeps;
accounts (both hosts start with an account directory: Sam, the owner, signed in
on A, B and C; Alex on `lab-desktop-d`; `lab-desktop-e` is an older Martlet with
no account): A's store from before accounts moves into Sam's space
(`MemoryStore.MoveStore`, its sync state too) and its next sync of Sam's space
together with the old document records nothing new and gives both hosts every
fact; B syncs Sam's space and the household; D shares the household fact but
is refused Sam's space and the old document; E keeps using the old document,
and A's next sync brings E's fact into Sam's space; sharing
([accounts](../ACCOUNTS.md#sharing)): D gives a fact to Sam's space through the give
route (`GiveMemoriesAsync`; the host takes it once, D is still refused Sam's
space, and A's next sync takes it), and a character space follows Sam's
household sharing entry (`sharing.<account>`, merged into both hosts' settings):
shared together, D remembers in it and A reads it; private again, D is refused on
both hosts and A still syncs it; no fact in any desktop data
folder while the hosts' copy holds them; and an unsigned request refused (HTTP
401). Synthetic facts, loopback only; the folder is deleted.

`creations_status` reads [Martlet's creations](../CREATIONS.md) from a data
directory (optional absolute `dataDirectory`; the script gives a disposable one).
Each [account](../ACCOUNTS.md) keeps its own creations in `accounts\<32 hex>`: with
`account` (32 hex digits or a GUID) it reads that account's; without it, the
account in use on that device (`accounts\session.json`), else the account the
data folder's creations moved to (`accounts\creations-moved.json`), else the
data folder itself. `from` names the `account` (null before accounts), the
`folder` it read, `chosenBy` (`argument`, `signed in`, `moved` or `data folder
(before accounts)`), the recorded `moved` (account, `movedAt`, `creations`,
`assets`) and `leftInDataFolder` (a list still in the data folder after the move,
such as one that couldn't be read). `songs_status` and `pictures_status` read the
same folder. Then `state` (`none`, `loaded` or `unreadable` for `creations.json`), `live`,
`tombstones`, `totalBytes`, `revision`, `digest` (its first 16 hex digits),
`kinds` (count and bytes per kind), and per live creation its `key` (the short id
the tools and `Creation-<key>` use), `kind`, `kindVersion`, `bytes`,
`durationMs`, `createdAt`, `createdOn` (device ID), `autoCleanup`, `assets`
(name, media type, bytes, pieces), `completeHere` and `onHosts` (how many paired
hosts held every piece at the last sync); `storage` (asset files in `creations`,
their bytes, `unused` ones and copies still arriving in `creations-incoming`);
`sync` from `creations-sync.json` (when, its summary and each host's `state`,
`complete` and `missing`, or null before the first sync); and the `limits`.
Never a title, text, voice or personality. Read-only; it contacts nothing.

`creations_check` rehearses creations end to end with the production code and
returns `{ok, tools, sync}`. `tools` checks `list_creations` and
`perform_creation` (`Martlet.Conversation.CreationTools`) in process on a
disposable folder with the production `CreationStore` and the FIXTURE - NOT AI
test-tone kind: no tools without a registered kind, the same two tools and texts
on every build, listing newest first with short ids and filtering by words and
kind, performing through the kind's handler with options, and clear refusals for
no handler, an unknown id, bad arguments and options, a creation still copying
and an unknown kind. `sync` runs `src\Martlet.NodeLinkCheck` (mode `creations`,
`CreationRehearsal.cs`) and returns `{exitCode, report}`: two real gateways on
127.0.0.1 (pinned TLS, signed requests, in-memory `creations.json` and pieces)
and three simulated desktops with the production `CreationStore` and
`CreationSync` over the desktop's paired client (`HostCreationPeer`). Its steps:
FLAC sizes for a 60 s music-like signal (stereo and mono with silences, decoded
identical); a 40 s noisy tone in two 3 MiB pieces; a host taking the list and
every piece; an unchanged host read by digest only; a new desktop taking both and
relaying them to a host the first never reached; an interrupted copy resuming;
another desktop performing a creation through the kind's handler from its own
copy; a kind this Martlet doesn't know passing through; a rename reaching
everyone; a delete reaching both hosts and every desktop (pieces and files
deleted); a stale copy not bringing it back; a host restart; a wrong SHA-256, a
piece no creation has and an oversized piece refused (`request.invalid`); an
unsigned request refused; a kind's rules (unregistered kind, missing part, part
too large); the per-host record in `creations-sync.json`; and two accounts
(Sam, the owner, and Alex) on one host with their own desktops, which use
`HostCreationPeer.ForAccount`: each syncs through its own list (the second sends
no piece the pool already holds), the owner's sync joins the old single list both
ways with desktop A (`CreationOwnerBridge`) and reads only digests when nothing
changed, Alex never gets the old list or Sam's creations, a piece goes only when
no list uses it, and a restart keeps both lists. The access hook that refuses
other accounts' devices is covered by the gateway tests. With
`seedDataDirectory` (an absolute folder under the temporary folder, never
Martlet's own) it instead writes two FIXTURE - NOT AI test tones there and
returns their keys, so the Creations page can be checked with `-Desktop
-DataDirectory` on that folder. Nothing leaves loopback, folders are deleted and
Windows Credential Manager is not touched; it does not cover the desktop window's
30-second sync with real hosts, the Linux host's files, a real song or a real LAN.

`mcp_servers_status` reads `mcp.json` from a data directory (optional absolute
`dataDirectory`, default the current user's) as the desktop parses it: `state`
(`none`, `loaded`, `invalid` or `unreadable` with `problem`) and per server its
`name`, `transport`, `command`, raw `args` (`${env:...}` and `${secret:...}`
references stay, never their values; a literal value that looks secret shows
as `[hidden]`: the value after a flag such as `--token`, `--api-key` or
`--password`, a `key=value` with a secret name, the user and password in a web
address, and tokens such as `sk-...`, `ghp_...` or a long random string), `host` for HTTP servers, `env`
and `headers` names, `disabled`, `autoApproveAll`, `autoApprove`, the MCP
directory `registry` and `registryVersion` it was installed from, the `secrets`
names it uses, `toolNames` (how a reply names its tools: always
`<server>_<tool>`, whatever else runs) and its `problem`. It starts no server and reads no credentials.

`terminal_status` reads Companion › Tools › Terminal from a data directory's
`terminal.json` (optional absolute `dataDirectory`, default the current user's):
`state` (`none`, `loaded` or `unreadable`, which reads as the defaults),
`enabled` (off by default), `shell` and `shellName`, `shellInstalled`,
`installedShells`, `askFirst` (on by default), `timeLimitSeconds`, `startFolder`
(`home` or `chosen`, never the path) and `startFolderExists`, and `tool`:
`run_terminal_command`'s name, description and parameters exactly as the Thinking
model gets them, with the start folder shown as `{folder}`. It runs nothing.
`terminal_check` runs the desktop's production terminal runner with fixed,
harmless commands (never anything a model or the owner chose) in the saved shell
or `shell` (`WindowsPowerShell`, `PowerShell` or `CommandPrompt`), in a fresh
temporary folder it removes afterwards, whether or not the terminal is on. It
returns `ok` and each step: `output` (UTF-8 text such as `héllo ✓ 日本` and the
start folder), `special-characters` (quotes, `&` and `|` reach the shell as
typed), `errors` (an error line kept, exit code 3), `input-closed` (a command
that reads input ends at once), `time-limit` (a 2-second limit stops the shell
and its loopback `ping` child; `childProcessesLeft` must be 0), `long-output`
(20,000 lines kept as their start and end, under 11,000 characters), `refused`
(empty, too long and multi-line Command Prompt commands) and
`program-outlives-shell` (a background loopback ping keeps running while the run
ends with the shell), each with what the model would be told, plus `tool`.

`mcp_directory_plan` shows how the MCP directory would install one registry
entry without fetching, writing or starting anything: pass `server` (a
server.json object as the v0.1 API returns it, or the whole list item with
`server` inside), optionally `name` (the mcp.json name; default the suggested
one) and `values` (input key to text, for example `"env:CONTEXT7_API_KEY"`,
`"var:api_key"`, `"header:Authorization"`, `"arg:--project-ref"`). It returns
`name`, `displayName`, `suggestedName`, `version`, `unsupported` (why other ways
to run it aren't offered) and per option its `kind`, `summary`, `runtime`,
`runtimeAvailable`, `host`, `inputs` (key, label, required, secret, flag,
default, choices) and either `plan` (the mcp.json `entry`, the `secrets` names
it would save and a `preview`) or the `problem` (such as a required field left
empty). Secret values are never returned.

`home_assistant_probe` checks one Home Assistant `address` the way Smart home
does before offering Set up: it returns the normalized `address`,
`homeAssistant` (whether it answered like Home Assistant), `onboarding`
(`owner`, `coreConfig`, `analytics`, `integration`, `done`; a Home Assistant
restarted after its setup reports everything done) and `next`, or `failure` and
`problem`. It uses only the unauthenticated onboarding and sign-in-provider
endpoints, sends no token or password and changes nothing.
`home_assistant_find` asks the local network once for Home Assistant's mDNS
service type (optional `seconds`, 1-10, default 2.5) and returns `count` and
each answer's `name`, `address` and `version`. `smart_home_status` reads a data
directory's `smart-home.json`: `connected`, `address`, `name`, `version`,
`tokenSaved` (never the token), `control`, `allowSensitive`, `modelTools`,
`shared` (this PC follows the connection shared through the hosts), `sharedBy`
and `sharedRevision`.

`discord_status` reads a data directory's `discord.json` (Companion ›
Discord): `state` (`none`, `loaded` or `unreadable`), `configured`,
`applicationId`, `token` (`readable` when Windows Credential Manager holds the
bot token, `none`, or the credential error; never the token), `enabled`,
`ownerSet`, `homeServerSet`, `serverChat`, `directChat`, `voiceChat`,
`directFromAnyone`, `channelRules`, `people`, `peopleMayCall`, `chat` (one
line) and `next` (the next setup step). `discord_check` (optional `seconds`,
3-30, default 15) connects the saved bot once with the production
`DiscordBot` and disconnects: `state` (`Online`, `Failed`, `Connecting` when
Discord didn't answer in time, or `notConfigured`/`tokenUnreadable`),
`botName`, `servers`, `problem`, `messageContentIntentOff` (Discord closed
with 4014: turn on Message Content Intent), `tokenRejected` (4004),
`milliseconds` and `next`. It sends no messages; a desktop already connected
with the same bot stays connected.
`messaging_status` reads Companion › Messaging from a data directory's
`messaging.json` through its store (`Martlet.Messaging.MessagingPreferences`; the account, chats and
speaking replies travel as the `messaging` shared setting, answering stays on each PC): `state` (`none`, `loaded` or
`unreadable` with `problem`) and `telegram` with `connected` (a bot is set
up), `enabled` (Martlet answers it on this PC), `bot` (its username),
`botName`, `tokenSaved` (never the token), `chats` (how many chats are paired;
never their names or IDs) and
`speakReplies`, and `whatsApp` with `connected` (a number is set up),
`enabled`, `number`, `name`, `appId`, `businessAccountId`, `phoneNumberId`,
`port` (Martlet's localhost webhook port), `publicAddress` (the owner's own;
empty with `quickTunnel` true for a Cloudflare quick tunnel), `secretsSaved`
(never the access token or app secret), `chats` and `speakReplies`, and
`cloudflared` with `downloaded` (Martlet's own copy is in the data directory's
`tools` folder), `version` (the cloudflared release it came from) and
`verified` (it still matches the size and SHA-256 that GitHub publishes for
that release, checked at download and recorded in `tools\cloudflared.json`;
Martlet runs its own copy only when this is true), `signed` (Windows confirms a
valid Authenticode signature from *Cloudflare, Inc.* on it; Martlet runs no
cloudflared.exe without it, also one on PATH) and `signatureProblem` (why not,
or null). The secrets
live in Windows Credential Manager and are never read.
`messaging_check` rehearses the production `MessagingBridge` and
`TelegramTransport` with local fakes and contacts no Telegram, WhatsApp or
Meta server. It returns `ok` and `steps`: `telegramBodyReadBreaks` (a
connection that breaks while the answer is read is a `Network` failure, with
no token in the message), `unexpectedErrorRetries` (an unexpected receive
error gives `Retrying`, then `Running` again, never a silent `Off`),
`failedAnswerKeepsTheBatch` (a failing typing call and a failing answer do not
lose the next message; the chat is told Martlet couldn't answer), `stopsAsOff`
and `pairingCodeSurvivesAStranger` (wrong codes count per chat, so a stranger
can't use up the owner's code). `smart_home_guard` shows how Smart home's
safety guard (`HomeCommandGuard`) treats one `text`: `risk` (`Normal`,
`Sensitive` or `NotACommand`), `mentionsSensitive` and `meaning`. A request
that asks for an action on a lock, door, garage, gate, alarm or valve is
`Sensitive` also when it starts with a question word ("why don't you unlock
the door"); it is local only and contacts nothing. In the desktop,
Companion › Messaging's `MessagingStatus` (whether Martlet
answers the bot now, or why not), `MessagingNote` (the last connect outcome),
`MessagingChats` (how many chats) and `MessagingPairStatus` (until when the
pairing code works) are readable values, and WhatsApp's card has the same as
`MessagingWhatsAppStatus`, `MessagingWhatsAppNote`, `MessagingWhatsAppChats`
and `MessagingWhatsAppPairStatus`, plus `MessagingWhatsAppTunnel` (where Meta
delivers messages and whether cloudflared is on this PC); the codes themselves
(`MessagingPairCode`, `MessagingWhatsAppPairCode`), chat names and the secret
fields are not. The Cancel buttons (`MessagingPairCancel`,
`MessagingWhatsAppPairCancel`) only withdraw the code and are safe clicks;
Connect, Pair a chat, Open BotFather, Open in Telegram, Remove, Disconnect and
the check boxes, the *Answer messages* main choice (the radio buttons
`MessagingTelegramOn`, `MessagingTelegramOff`, `MessagingWhatsAppOn` and
`MessagingWhatsAppOff`; an app's On is disabled until it is set up), and WhatsApp's `MessagingWhatsAppConnect`,
`MessagingWhatsAppGetCloudflared` (a download, after its confirmation), Meta
links, `MessagingWhatsAppPairOpen`, `MessagingWhatsAppDisconnect` and
`MessagingWhatsAppSpeak` need
`--allow-ui-effects`. `ui_set_text` fills `MessagingWhatsAppToken`,
`MessagingWhatsAppSecret`, `MessagingWhatsAppPhoneId`,
`MessagingWhatsAppAccountId` and `MessagingWhatsAppAddress`. To verify WhatsApp
without Meta, launch the desktop with `MARTLET_WHATSAPP_API` set to a loopback
fake Graph API that performs the webhook check on `POST /<app>/subscriptions`,
seed `messaging.json`'s `WhatsApp.Port`, and use `http://127.0.0.1:<port>/` as
the public address; the fake can then post a signed delivery to the callback
and record the reply on `POST /<phone>/messages`.

`discord_text_check` feeds simulated Discord messages through the production
[Discord](../DISCORD.md) text pipeline (`DiscordTextChat` in `src\Martlet.Discord`,
the one the desktop's bot uses) with a fake transport and a fixture reply engine
(NOT AI, NOT Discord; no token, no network). Without `messages` it runs fixed
`scenarios` on fixture preferences (each with `name`, `passed` and `detail`;
`passed` is all of them): DMs from the owner, a known person and a stranger, a
Mentions channel's chatter, @mention, name and reply to Martlet, a Sometimes
channel the engine passes on, an Off channel, an Always channel, another bot, a
long reply split under 2,000 characters with `@everyone` neutralized, `/martlet`
in a group DM and a turn dropped as stale when a newer message arrives while
Martlet thinks. With `messages` (1-16 objects: `text`, `place` `server` or `dm`,
`author` `owner`, `known`, `stranger` or `bot`, `mention`, `replyToMartlet`,
`channelId`, `command` for `/martlet`) it uses the data directory's
`discord.json` (optional absolute `dataDirectory`; a missing owner is a fixture
ID) and returns each `outcome` (`Answered`, `Passed`, `NotConsidered`,
`IgnoredBot`, `Dropped`, `NoEngine`, `Failed`), `mode`, `addressed`, what was
`sent` (`quoted` when sent as a Discord reply, `viaCommand`) and `recentLines`.
`reply` (up to 4,096 characters) replaces the fixture's answer. Both return
`preferences` (counts and modes only) and `stats`, the same counts as the
desktop's Companion › Discord `DiscordTextStatus` line (*Text chat: 3 seen, 2
considered, 1 answered, 1 passed, 0 dropped, 0 failed; last reply: DM; last
problem: none.*; no message text, names or IDs). The live bot logs each
answered, passed, dropped or failed turn as *Discord text: Answered (Mentions,
addressed), 1 message(s)*.

`discord_companion_check` shows Martlet's [Discord](../DISCORD.md) companion
state as saved in a data directory (`discord.json` and `discord-companion.json`:
friends' names and how many take calls, friend requests waiting, recent
declines, the call channels it made, when the bot's picture last changed; never
the token), then rehearses the production `DiscordCompanion` against an
in-memory Discord (`DiscordRehearsalTransport`; NOT Discord, no token, no
network): `friendRequest` (`/friend ask` and asking twice, the owner's approval
and its welcome DM, `/friend remove`), `call` (the private channel `Hiyori &
Ana` with its permission `overwrites`: @everyone denied ViewChannel and Connect,
the friend, the bot and the owner allowed to talk; the ring DM with its jump
link and a server invite; calling again reuses the channel and joins; the
channel is removed after the call; a friend who turned calls off is refused),
`presence` (the status and text for each Martlet state and the 20-second rate
limit) and `avatar` (the same character is skipped, a new one waits 30 minutes,
Update now 10). Optional `person` and `character` name the rehearsal. With
`requestFrom` (a Discord user ID as a string) and `requestName` it also files a
friend request into the given `dataDirectory`, as `/friend ask` would; it
refuses without an explicit (disposable) `dataDirectory`. The desktop then lists
it on Companion › Discord › Friends and calls (see below).

Companion › Discord's **Friends and calls** card: `DiscordFriendsStatus` (*2
friends, 1 request waiting, no call now (last call: ...).*), `DiscordPresenceStatus`
(*Discord status: Online, "Hanging out".*), `DiscordAvatarStatus` (when the
bot's picture last changed and why it didn't), `DiscordFriendsResult` (what the
last action did) and each friend's line `DiscordFriend-<user id>` (*Ana (123) —
Martlet also knows Ana by voice* when exactly one voice in People has that
name) are readable values. `DiscordFriendsAbout` (What Discord allows) only
expands. `DiscordFriendApprove-<id>`, `DiscordFriendDecline-<id>`,
`DiscordFriendMayCall-<id>`, `DiscordCall-<id>`, `DiscordFriendRemove-<id>`,
`DiscordFriendAdd` (with `DiscordFriendAddId` and `DiscordFriendAddName`) and
`DiscordAvatarUpdate` change `discord.json`/`discord-companion.json` or contact
Discord, so they need `--allow-ui-effects` (Call and Update picture now only do
something while the bot is connected). The desktop log notes *Discord: someone
asked to be Martlet's friend* and *Discord: call_on_discord ran.*

`discord_reply_status` shows Martlet's Discord reply engine (optional absolute
`dataDirectory`): `file` (*none*, *loaded* or *unreadable*) and `engine`, the
desktop's `discord-replies.json`, written when the desktop wires the engine at
start and after every Discord turn: `wired`, `startedAt`, `updatedAt`,
`replies`, `passes` ([pass] or nothing to add), `skipped` and `lastSkip`
(*chance*, *cooldown*, *hourly_limit*, *not_twice_in_a_row*, *place_busy*,
*local.busy*, *local.preempted*), `failures`, `lastError` and `lastErrorAt`
(codes such as *thinking.not_set_up* or a provider failure), `places` (places
with history), `running`, `lastReplyAt`, `lastPassAt`, `lastLatencyMs`,
`lastFirstWordsMs`, `lastInputTokens`, `lastCachedTokens`, `waitingForLocal`,
`preemptedByLocal`, `route` (`routeType`, `model`, `onYourNetwork`),
`localStrategy` and `lanes`; and `discord`, discord.json's chat setup
(`configured`, `enabled`, `ownerSet`, `serverChat`, `directChat`, `voiceChat`,
`channelRules`, `people`). Never what was said, names, IDs or the token.
Read-only.

`discord_reply_check` rehearses the engine's production Discord side
(`DiscordReplier`: per-place history, the ambient gate, multi-party prompt
shaping, [pass] and Discord's limits) through the production Chat Completions
adapter against a fixture endpoint on 127.0.0.1 (canned replies, NOT AI).
`fixture.steps` are seven made-up turns, each with `expected`, `outcome`
(*reply*, *pass* or *skip:reason*), `replyCharacters`, `reply` and `sent` (each
message's `role`, `characters` and start; `tools` false): ambient chatter
skipped by chance, an addressed turn answered, ambient right after it skipped
by the cooldown, ambient naming Martlet passed with [pass], an over-long answer
kept within 2000 characters, a voice-call answer without markdown or emoji and
the owner's DM; `ok` when every outcome matches. The desktop sends the same
shaped turns inside the live conversation's own request (persona, style, lore,
memory, reply length; `DiscordReplyEngine`), which `DiscordReplyEngineTests`
check. With `live: true` it also asks Ollama on this PC (the saved local Thinking
model, or `model`) two made-up turns with the saved persona: `live.turns`
(`outcome`, `reply`, `ms`). Loopback only; reads no credentials.

On the Companion › Thinking pool page, each machine's `ThinkingPoolOn-<n>` box
(*diva's Thinking pool (qwen3:8b): On*, ticked or not) and each paired computer's
`DeepThinkingPool-<host>` box that isn't in the list (*diva in the Thinking
pool*) read; changing one saves `thinking-pool.json`, so it needs
`--allow-ui-effects`.

`reminders_status` shows Martlet's [reminders](../CONVERSATION.md#reminders)
from the `shared-settings.json` of the account signed in on a data directory
(`accounts\<id>`, named by `accounts\working-copy.json`; the data directory's
own for a folder from before accounts; optional absolute `dataDirectory`):
`account` and `source` (which copy it read), `computers` with a reminders entry, `unreadable` entries
(a newer Martlet's), `pending`, and each reminder's `id`, `text`, `due`, `set`,
`setOn`, `state` (*Pending*, *Done*, *Canceled*, *Missed*), `settledBy`,
`settledAt`, `dueIn` and `marks` (`kind` *Bid* with `idleSeconds`, *Claim*,
*Done*, *Cancel* or *Missed*, `by` and `at`), plus the `reminders` `tool`
exactly as the model gets it and `handOff`: the check-in tool set that takes
it over (`checkInToolSet`, `replaces`) and the read-only `replyTool`
(`list_reminders`) the reply gets then. Read-only.

`reminders_check` rehearses reminders with the production code (`Reminders`,
`ReminderBoard`, `BackgroundJobs`, `SharedSettings`) on two simulated companion
PCs whose entries merge through the shared settings: set in minutes and at a
local time on one, listed and canceled on the other, a refused call, both
offering when it is due, the PC used most recently (5 s against 10 minutes
idle) taking it while the other stays quiet, the conversation's message when
Martlet brings it up on its own and the notes when the user talks first, said
once and settled everywhere, a PC alone taking it at once, one far too late
let go, and the reply's read-only `list_reminders` call listing without
changing anything while the *Reminders* set replaces `reminders`. `passed` and each step's `passed` and `detail`. No model, network or
credentials.

`check_ins_status` shows Martlet's [check-ins](../CONVERSATION.md#check-ins) from a
data directory (optional absolute `dataDirectory`): `settings` from
`check-ins.json` (`state` *none*, *loaded* or *unreadable*, and each check-in's
`id`, `name`, `custom`, `on`, `everyMinutes`, `outcome` *EmotesOff*,
*GazeUsual*, *Note*, *Say*, *Context*, *Tools*, *Plan*, *HoldBack* or *Callbacks*, built-in `prompt`, the owner's `task`, the
`facts` and `conditions` (such as *Said, Replies* or *CharacterShows,
EmoteShown, SlowWhenKept*), `changed` (a built-in one with more than On and
Every changed on its card), and what it `does`; for every check-in the `needs` (*Text*, or
*Text, Vision, Audio, Tools*), the `screenshot`, the `recording` (*None*,
*Microphone* or *PcSound*), `recordingSeconds` (null without a recording),
whether it runs a `script` and `scriptCharacters`, never the script itself;
`hours` such as *8 AM-10 PM* with `Between`, else null; `mostPerHour`, null
without a cap; its `triggers`, such as *None* or *TouchesEnded,
IntimateTouch*; and its `toolSets` (set IDs such as *next-reply*)), `desktop` from `check-ins-status.json` (written by
the desktop on a companion PC: `role`, the check-in `running` and the
triggered one `runningTriggered`, `touches` with `settling` (touches wait to
settle), `settleMs` and `lastFired` (`triggers`, `at` and `what` in counts
only, such as *3 touches, an intimate one* or *2 exchanges*), `afterExchange` (the
reply tools [the director](../CONVERSATION.md#the-director) takes over:
`poolCallsTools`, whether the configured Thinking pool has a member that takes
check-ins and calls tools; `handedOff`, each `tool` with its `checkIn` and `set`;
`why` in plain words; and `lastProblem`, what the last run after an exchange
couldn't do, or null), `aside` (whether Martlet [holds
back](../CONVERSATION.md#talking-to-someone-else) now: `holding`, the check-in
`by`, `since`, `until` and `aboutCharacters`, never who the user talks to),
`callbacks` (how many things the [callback list](../CONVERSATION.md#callback-list)
keeps now, `count`, and its `maximum`, never what they are),
`pool` with
`canRun` and the `member` and `model` that take them first, and for each
check-in `needs`, `canRun` (a member that handles what it needs), `screenshot`,
`recording`, `recordingSeconds`, `script`, `triggers`, `pending` (the trigger
it keeps: `triggers`, `at`, `what` and `expiresAt`), `toolSets`, `waiting`, `hours`, `mostPerHour`,
`lastHour` (its runs in the last hour), `nextAt`, `runs`, `acted` and `last` with `at`, `result`, `acted`, `member`, `gathered` (what the run
took in a few words, such as *a screenshot (1280x720), 10 s of the
microphone*), `trigger` (what started it, or null), `ms` and `tools` (each call the run made: its `set`, the `tool`,
the `result`, the first line of the tool's answer at most 120 characters, and
`failed`); `sound` with `microphoneKept`, `microphoneHeard`,
`pcKept` and `pcHeard`; `signals` with `attentionWatched` (Martlet watches
for flashing taskbar buttons and notifications because a check-in that is on
waits for them), `activityKnown`, `cameBackMinutesAgo`, `voicesLately` and
`heardLately` (counts); never what was said, answered, reminded, taken or printed, what the
user does or who spoke) and the
fixed `rules` (the 15-second look, the 3-minute minimum, the 10-second settle,
the 10-minute idle wait, the pace choices and `keptPace`, `repeatsSayings` and
`saidLatelyMinutes` for Saying the same things, `recordingChoices`,
`maximumScriptCharacters`, `maximumScriptOutputCharacters` and
`scriptTimeoutSeconds` for the owner's inputs, `signalWindowMinutes`,
`unansweredAfterMinutes`, `unansweredWithinMinutes`, `peopleWindowMinutes` and
`mostPerHourChoices` for the signals, `lullAfterMinutes` (3) for Quiet
moments, `maximumCallbacks` (8), `maximumCallbackCharacters` (160) and
`callbackWindowHours` (2) for the callback list, `maximumToolRounds` (4),
`maximumToolCalls` (8), `maximumToolResultCharacters` (120) and `toolSets`
(each set's `id`, `name`, `does`, `tools`, `replaces`: the reply tools it
takes over, such as `manage_memories` for `memory`, and `optional`: its tools
only look things up, so a check-in gets them only when a member that calls tools
can take it, as for `people`), the job kind
`check-in` at the `Helper` priority, not fast, stopped while the floor is Live,
and for triggers the `triggers` offered, `triggerAgeSeconds`, `exchangeAgeSeconds` (600),
`touchesSettleMs`, `strokeZones`, `oftenTouches` and `oftenWindowMinutes`).
Read-only.

`check_ins_check` rehearses check-ins with the production code (`CheckIns`,
`CheckInSettings`, `ThinkingJobBoard`, `HeldEmotes`, `ContextBoard`,
`BackgroundJobs`), FIXTURE facts and canned answers (NOT AI): the job kind's
rules; `check-ins.json` saved and read back, with a bad pace refused and a
built-in check-in's changed answer and facts kept; when each
built-in check-in waits or runs (a young emote, a hidden character, the pace,
three times the pace after an answer that kept everything, you talking, nobody
at the PC, a young gaze, nothing new, no personality, too little said lately,
and *Check now* on one that is off); the message each one sends (Saying the
same things with each thing said and when), run on a
production job board with a fixture member; the answers read (`OFF {blush}`
after a `<think>` block, `**USUAL**` after thinking, a `REMIND:` bullet, a
`REMIND:` after a `<think>` block, `OK`,
`SAY:`) and odd answers that change nothing (`KEEP`, a tag it wasn't asked
about, chatter, `REMIND: nothing`); `built-in ones recreated as your own`
(each built-in check-in copied as an own check-in with its prompt, facts,
conditions and answer sends the same message, waits the same way and reads
answers the same way, and an own check-in that gets what Martlet said in the
last hour and its last replies); `adult content line` (`{adult}` is the
non-explicit line while Adult content is off and the explicit line only while
it is on); and what Martlet does: a reply's emote off
on a production `HeldEmotes` while the owner's try stays, a reminder on a
production context board that goes with one request only, and a check-in's
`SAY:` worded in its own words beside a due reminder. Six `own:` steps check
the owner's inputs: `own: inputs saved and read back` (a recording length of 7,
a script with a null character and an unknown ability refused), `own: the model
it needs` (a screenshot adds pictures, a recording adds recordings),
`own: the microphone's last seconds` (a production `PcSoundBuffer` keeps sound
only while wanted, gives the last seconds and forgets them when no longer
wanted), `own: waits for the sound it records`, `own: the message carries what
it took` (a real FIXTURE script, `Write-Output ('FIXTURE ' + (6 * 7))`, run by
the production `TerminalRunner`, a FIXTURE picture and recording) and
`own: only a capable member takes it` (a text-only member never gets it; a
member with vision and audio gets the picture and the sound). `touches fact`
records FIXTURE pats, strokes across intimate zones, a move and a poke on a
production `TouchLedger`, with replies draining some of them, then fills
`{touches}` into an own check-in: each run with when, which were intimate,
the persona's feeling and the places touched again and again. It shows that
reading the history (`TouchLedger.History`) leaves what the next reply gets
the same, that the history outlives a reply's `Drain`, that the fact goes
after the prompt when it is ticked but not placed, and that facts past
`Touches` are refused. Its `detail` gives counts (`runs`, `things`,
`intimate`, `often`), `filled` and the FIXTURE message. Three
`triggers:` steps check [check-in triggers](../CONVERSATION.md#check-in-triggers)
with FIXTURE touches: `triggers: saved and read back` (an own and a built-in
check-in's triggers, an unknown trigger refused, and by default Describe touches starts on `TouchesEnded`, How I react on every touch trigger, Think ahead and Callback list on `ExchangeEnded`, Talking to someone else on `Spoke` and the director on `UserTurn`),
`triggers: fixture touches fire them` (on a production `TouchLedger`, a poke
and a stroke from the tail over the buttocks to the groin, after 5 earlier
touches there, fire all four triggers once they settle; the ledger still holds
every touch; a pat alone fires only `TouchesEnded`; a zoom fires nothing) and
`triggers: when a triggered check-in waits` (only on its own triggers, not
after 2 minutes, at most once per its pace, never held by a busy
conversation, still off, still waiting while nobody is at the PC, *Check now*
with no trigger, and a paced check-in still waiting for a busy conversation).
Three `director:` steps check [the director](../CONVERSATION.md#the-director):
`the built-in check-in, its trigger and its pool kind` (on, *Its tools act*,
`UserTurn` with no cooldown, job kind `director` that stops while you talk on
shared hardware, its tool sets are Martlet's next words, every set that
replaces reply tools, People, Skills and Knowledge; an exchange never starts it;
Act on what was said is gone), `what it reads` (`{asked}`, the exchanges since
its last run, the work list and the skill index without the pinned skills;
`load_skill` gives a skill's text) and `the reply hands tools off` (the tools the
sets replace, PC tools too, only while it is on and the configured pool has a
member that takes director jobs and calls tools, the pool asked only when
needed, an old Act on what was said turned off keeps the director off, and the
*Things done after the reply* line). Three
`context:` steps check *Adds to what Martlet knows*: `context: asked and read`
(an own check-in's message asks for a `KNOW:` line or `OK`, and a fixture
member's `KNOW:` bullet after a `<think>` block is read on a production job
board), `context: goes with one request` (the description, wrapped in
*Check-in: adds to what Martlet knows*, goes on a production context board as
`check-in-c7` with one request only, is dropped after
`CheckIns.ContextAge` (3 minutes) and is not posted when that prompt is
emptied) and `context: nothing changes nothing` (`KNOW: nothing to add`, `OK`,
a `REMIND:` line and chatter post nothing). `describe touches` checks the
built-in Describe touches check-in end to end with FIXTURE touches and a
canned answer: it is on, runs at most once a minute, adds to what Martlet
knows and starts on *Your touches end*; it waits with no trigger, runs on a
fired trigger while the touch reply keeps the conversation busy, waits during
its cooldown and while the character is hidden; its message has the strokes
from `{touches}`, the personality, the `KNOW:` format and the non-explicit
`{adult}` line (the explicit line only with Adult content on); and a fixture
member's `KNOW:` answer goes on a production context board as
`check-in-touches` for one request only. `think ahead` checks the built-in
[Think ahead](../CONVERSATION.md#think-ahead) check-in the same way with FIXTURE
facts and a canned answer: it is on, runs at most once a minute, thinks ahead
for the next reply (`Plan`) and starts on `ExchangeEnded`; it waits with no
trigger, runs on a fired trigger while you talk, waits during its cooldown and
with nothing said; its message has the personality, the conversation and the
`PLAN:` format; a fixture member's `PLAN:` line after a `<think>` block goes on
a production context board as `check-in-ahead` for one request only, the next
exchange drops thoughts that still wait (`CheckIns.DropPlans`), they are gone
after `CheckIns.PlanAge` (10 minutes), `OK` and `PLAN: nothing to plan` change
nothing, a `KNOW:` line can't be read, and an emptied *Check-in: thoughts ahead
for the next reply* prompt posts nothing. Four steps check [Keep the
conversation going](../CONVERSATION.md#keep-the-conversation-going) with FIXTURE
exchanges and canned answers: `callback list` (the built-in
[Callback list](../CONVERSATION.md#callback-list) is on, runs at most once a
minute after each reply, reads `Latest` and `Callbacks`; its message has the
list with when each item came up, such as *- 9:37 PM (40 min ago): open - ...*,
what was said and the `CALLBACK:` format; a fixture member's `CALLBACK:` lines
after a `<think>` block and a header are read on a production job board and
kept on a production `CallbackList`, where an item already there keeps when it
came up; a list forgotten meanwhile isn't written; `KEEP` changes nothing,
`NONE` empties it and chatter can't be read), `think ahead reads the callback
list` (Think ahead's message has the list and asks for one move; the note
for the next reply says to make that move), `quiet moments` (the built-in
[Quiet moments](../CONVERSATION.md#quiet-moments) is on, every 10 minutes, at
most 2 times an hour, and Unanswered question is on; it runs after 5 quiet
minutes and waits after 1, after 45, with an empty list, with you away, once
it picked this quiet spell up, while a question waits, on a call and at 10:17
PM; its message has the list and the `SAY:` format) and `prompts keep the
conversation going` (*Keep the conversation going* opens the reply's closing
instructions, the same text every time, and an emptied one sends nothing; the
repeat check lets callbacks through; remembering keeps running jokes, the
brief lists them, and *Writing a character's personality* asks for an example
dialogue). Three `signals:` steps check the
signal conditions and facts with FIXTURE facts: `signals: when they wait` (each
condition waits for its signal since the last run and runs on it: you came
back, a call, a change in what you do, the hours past midnight, an unanswered
question asked once, a flashing taskbar button, a song that ended, someone
else's voice, a check-in with no script to compare, the per-hour cap, and a
script whose output hash didn't change, which *Check now* still asks),
`signals: the facts and the check-ins` (Welcome back, Unanswered question,
Talking to someone else and Someone else is here send `{away}`, `{activity}`
and `{people}` and read `SAY:`, `ASIDE:`, `REMIND:` and `OK`) and `signals: hours and the per-hour cap saved
and read back` (equal hours, a cap of 5 and an unknown condition refused).
The settings step also checks that the two built-in check-ins that wait for
a signal and are off by default (Welcome back, Someone else is here) are off,
and that the others are on. `talking to someone else` checks [Talking to someone
else](../CONVERSATION.md#talking-to-someone-else) with a FIXTURE one-sided phone
call and a canned answer: it is on, runs at most once a minute, holds Martlet
back (`HoldBack`) and starts on `Spoke`; it waits with no trigger and on a
reply's end alone, runs while the reply is busy and waits during its
cooldown; its message has what always listening heard (who spoke, whether
Martlet answered or stayed quiet, whether Martlet holds back), the
conversation, what the user does, the voices and the `ASIDE:`/`BACK` format; a
fixture member's `ASIDE:` line after a `<think>` block is read on a production
job board; its note goes on a production context board as `check-in-aside`
with every request (not once), lasts while the user keeps talking and is gone
3 minutes after they stop; `BACK`, `OK`, `ASIDE: nobody` and a `REMIND:` line
read as they should; an emptied *Check-in: holding back while you talk to
someone else* prompt posts nothing; and a saved choice for the retired On a
call (`call`) is dropped. Three `tools:`
steps check [tool sets](../CONVERSATION.md#check-in-tool-sets): `tools: sets saved
and read back` (an own check-in's and a built-in choice's sets kept; an unknown
set and the same set twice refused), `tools: the job offers them` (the job
needs a member that calls tools, offers the chosen sets' tools for 16 rounds and
asks for the *Its tools act* answer; with no sets it waits) and `tools: only a
member that calls tools runs them` (a text-only member never gets it; on a
member that calls tools, FIXTURE calls are kept with their first line only, an
unknown tool and a failing handler are errors, and every call after the 64th
is refused). `memory care: a built-in check-in that works on memory in several
steps` checks the built-in Memory care check-in: on, every hour, *Its tools
act* with the Memory set, its job offering all ten Memory tools for 16 rounds
with 8 minutes and 2,048 tokens a request, its message saying it works in
steps and keeps facts for the right game, the three tools that save or correct
facts taking `where`, and 12 FIXTURE calls (more than the old bound of 8) that all run. The
rules list `maximumToolRounds`, `maximumToolCalls`, `maximumToolOutputTokens`,
`toolTimeoutSeconds` and `hardMaxToolRounds`. `tools: background work takes over the reply's think_longer and
research` checks the `background-work` set: its two tools take the reply's
arguments, it replaces `think_longer` and `research` (never `cancel_thinking`),
and a refusal's first line is `Not started: ...`. `how I react: the built-in check-in changes how the character
reacts, bounded` runs [How I react](../CONVERSATION.md#how-i-react): it is on,
starts only on touches (it waits without a trigger and runs with a fresh one),
offers the five Touch reactions tools and asks with `{touches}`; on a FIXTURE
member that calls them (NOT AI) through the production tool host and
`CharacterReactionTools` on FIXTURE zones, a mood, a feeling, an arms feeling
and a hand list are made, a call without a reason and a fifth change are
refused, and no call's first line holds the character's reasons (`detail`:
`waits`, `fired`, `tools`, `made`, `changes` and `hand`). `passed` and each
step's `passed` and `detail`. No model, network or credentials.

`app_guides_check` rehearses [App guides](../APP_GUIDES.md) end to end with
Martlet's own parts against fixtures on 127.0.0.1 (NOT real sites): a tiny wiki
about a made-up game (sections about iron ore and a sword, links to more pages,
navigation to leave out), a second wiki found only through a DuckDuckGo-like
search page, and `robots.txt`. It uses the guide library service
(`AppGuideService`), the store (`FileAppGuideStore`), the wiki reader
(`WebGuideBuilder` with `WebAccess(allowLoopback: true)`), the chunker, the index,
the notes (`GuideRecall`), the program-name match, the rule for reading up on its
own, the tool texts
and the job scheduler (`AppGuideTools`, `BackgroundJobs`). Every fetch outside
loopback is refused before it leaves the PC (`build.refusedOutsideLoopback`
counts them), so nothing reaches a real site whatever the wiki reader tries.
- `library`: App guides are on by default and *Read up when I start a game or
  app* is on by default (`readUpWhenStartedByDefault`). `saved`: libraries
  saved earlier, each loaded by a new service (`noLibrary` on, `savedOff` off,
  `savedOffThenAdded` still off after an app is added, `savedOn` on,
  `withoutOn` on, `unreadable` off).
- `build`: reading up from the pages the owner gave (`fromGivenPages`: the wiki's
  `robots.txt` is read and the wiki is crawled through its own links, at least 2
  pages) and from a web search alone (`fromSearch`: the wiki farms named after the
  app are tried first and refused before they leave the PC, then `searchQueries`
  holds *<name> wiki*, which finds the second wiki; `pagesFetched`), each with
  pages, sections, bytes kept, sites, unreadable pages, bytes downloaded and time;
  the guide file's size; and `restarted`: a new service on the same folder reads
  the library with no index loaded (`indexesAtStart` 0) and builds the game's
  index when asked (`indexReady`).
- `notes`: the game in front, named as Windows names its program
  (*STARFALL VALLEY™* matches *Starfall Valley*: `matchedKey`), gets notes for
  *hey martlet, where tf do I find iron ore?* (`inFront`: matched and used sections, best
  relevance, search time; the notes start and end with the labels, say they are
  reference text and name the Ember Mines); an unrelated message gets none; a
  message that names the game while Notepad is in front gets its notes; the same
  message while Notepad is in front without naming it gets nothing; sections the
  conversation already carries are not sent again (`sectionsSentAgain` 0); App
  guides off gives nothing. `firstSearchMs` is the first search in a new process
  and `laterSearchMs` a later one.
- `offer`: when Martlet reads up on its own (`AppGuideService.Offer`): a game
  without a guide (`gameWithoutGuide`), once a session (`afterAsked` null); a start
  dropped before Martlet could may come again (`droppedOfferAgain`); a no is kept
  (`afterNo`), also after a new start (`noKeptAfterRestart`), until Allow again
  (`afterAskAgain`); a game with a guide, an app that isn't a game and *Read up
  when I start* off never are; an app on the list is (`listedApp`). `notice` and
  `message` are the `guideoffer` notice and the message of the reply Martlet
  starts on its own (*App guides: reading up on a game you started*): it says
  Martlet already reads up, and doesn't ask the model to call `read_up_on`.
- `onItsOwn`: a game in front named as Windows names an Unreal game's program
  (*MoonlitAbyss*, *MoonlitAbyss-Win64-Shipping*) is read up on with no model, as
  the desktop's job does it: under a readable name (`readsUpOn` *Moonlit Abyss*),
  on the list with its guide and both program names (`onTheList`), matched while
  in front (`matchedInFront`), its guide in the notes of a question about it
  (`notesWhileInFront`), and not read up on again (`readsAgainAfterward` null).
  `readableNames` shows `AppGuideService.Readable` on more program names.
- `reading`: a read with the default caps (`defaultCaps`: at most 200 pages,
  25 MB and 15 minutes, 2 sites, a 1 second pause, a 20 minute job time
  limit, keeping every 3 minutes; `readMoreCaps`: at most 300 more pages, 25 MB
  and 20 minutes from 1 site; `storeBounds`: the most pages, sections and
  megabytes one guide keeps). It reads the 3-page fixture wiki (`firstRead`),
  keeps what it read while it reads (`pagesKeptWhileReading` above 0, *Saving
  what it read so far* in `progress`), and its progress says *Reading page 1 of
  at most 200*. `stopReading`:
  Stop reading at page 2 of a second read ends it and keeps the page read again
  and the guide's 2 other pages (3 pages, `stoppedAtPage2`), and stopping while
  nothing is read does nothing.
- `series`: [choosing the pages](../APP_GUIDES.md#choosing-the-pages) on a
  MediaWiki fixture about a series (NOT a real wiki), with a stand-in Thinking
  pool member that gives canned answers (NOT a model), for *Starfall Valley 2*.
  `firstRead`: with a cap of 3 pages, the read takes *Moonstone Sword
  (Starfall Valley 2)* (its title names the game), then *Ember Mines* (the
  wiki's own search for the game's name) and then *Frost Caves* (a link of
  the first page) (`readInOrder`). It never reads or asks about the
  *Starfall Valley 3* pages (`choice`: 8 titles, 2 about another game, 6
  chosen, 0 asks), and leaves 3 titles to read more (`leftToReadMore`: a
  chosen link, a main-page link and a link of *Frost Caves*). `readMore`:
  then, as on the desktop, the brief is written, the pool member gets those
  titles with *What the user does now* (`firstAskHadWhatTheUserDoes`),
  chooses *Orla* and *Crystal Golem* and leaves out *Voice cast*
  (`leftOutByTheMember`); the chosen pages join the guide
  (`pagesInTheGuide`, 5) and the brief is written again (`status`). The log
  has counts only (`holdsTitlesOrLinks` false).
- `readMore`: [Read more](../APP_GUIDES.md#reading-more) on the same series
  fixture with no Thinking pool member, as the Read more buttons and asking
  Martlet do it. `firstRead` reads 2 pages and the guide file keeps what is
  left (`savedWhatIsLeft`). `afterRestart`: a new service on the same folder
  reads more twice from the guide file alone; each time it adds pages the guide
  doesn't have (`guidePages`, `guidePagesAgain`, `noPageTwice`). `readAgain`:
  Read again keeps the pages Read more added (`keptReadMorePages`).
  `guideKeptWithoutState`: a guide file without what is left finds its wiki
  again from its pages and adds pages. `words`: what
  `AppGuideTools.AskedToReadMore` takes from 5 messages (*read more about it*
  is the app in front; *don't read more about it* and *I read more books* are
  nothing). `toolResult` is the job's result for the model.
- `indexes`: only the indexes that can be used now stay in memory, with three
  small guides and a clock moved forward for the idle time: none at start
  (`atStart` 0), 3 once all are used, 2 when the app in front leaves (the 2
  used last, `keptAfterLeaving`), 3 when it comes back, 1 after the idle time
  (`afterIdleTime`: only the app in front, `keptWhenIdle`) and 0 when App
  guides stop watching (`afterWatchStopped`). `idleMinutes` and
  `otherIndexesAtMost` are the limits.
- `tools`: `read_up_on`, `search_guide`, `skip_guide` and `update_guide_brief` exactly as replies get
  them, their arguments (sites made into `https://` links, others left out),
  `search_guide`'s answer, the reading-up job (`guide-1`, *Reading up on*, a 20
  minute time limit, 4 an hour, its result), a second job refused as `busy` and the
  fifth in an hour as `hourly_limit`, and the *App guides* prompt.
- `latency`: what App guides add to a reply's path, on a guide of 2,000
  sections (`sections`) and on a guide of 25,000 sections
  (`wholeWiki`): `indexBuiltOffThePathMs`, and the notes step's median and worst
  time of 200 runs for a message about the game in front, an unrelated message
  and App guides off; `everyRequestWhileOn` is the bytes the four tool
  definitions and the prompt add to every request (the same every time, so
  prompt caches keep them). On this PC the notes step took about 0.2 ms
  (median) on 2,000 sections, about 0.6 ms on 25,000 sections and nothing
  while off.
- `research`: [the brief and area research](../APP_GUIDES.md#the-brief) with a
  stand-in Thinking pool member that gives canned answers (NOT a model):
  `briefAfterRead` (a brief is written after a read), `briefWithMessage` (it
  goes with the first message and is not `sentAgain`), `screen` (an area the
  guide covers is not read on the web; an area it doesn't cover is searched
  with `searches` *wiki*, *reddit* and *guide*, its page is read and sections
  are added; `redditSnippetKept` is true and `redditOrVideoPageRead` false;
  a screen with no area gives none), `readAgain` (the research sections stay
  and a new brief is written), `updateGuideBrief` (the tool's result, bad
  arguments, the same area within the hour, and no pool member: refused),
  `saved` (the brief and areas in the library), `thinkingRequests` (each kind
  with its count and largest input), `sameInstructionsEveryTime` and `log`
  (counts and times only; no area names or links).
Each part has an `ok`. Temporary folder, deleted afterwards; reads no
credentials.

`app_guides_status` reads Companion › App guides from a data folder (optional
absolute `dataDirectory`, default the current user's; the script gives it its
disposable one): `folder` (`<data folder>\guides`), whether it `exists`, whether
`library.json` is `saved` (else `on` and `readUpWhenStarted` are the defaults,
both on), `on`,
`readUpWhenStarted` (kept as `askWhenStarted` in the file), how many `guides`, `pages` and `bytes`, and each app on the
list (`apps`): `key`, `name`, `programs`, `sites` (the start pages the owner
gave), `declined`, `builtAt`, `pages`, `sections`, `bytes`, `problem`, whether
its guide `file` is there, whether it has a `brief` with `briefAt` and
`briefCharacters`, how many `areas` were looked into and `researchedAreas`
found on the web, `researchedSections` and `lastAreaAt`. Never a guide's or
brief's text, nor area names. Read-only.

`discord_call_check` checks [Martlet in your own Discord calls](../DISCORD.md#martlet-in-your-own-calls)
(Companion › Discord › **Martlet in your Discord calls**; optional absolute
`dataDirectory`, default the current user's). `saved` is the mode from
`discord-calls.json` (`on`, off by default; `capture` `DiscordApp` or
`EverythingButMartlet`; `seeSpeakers`; `ownerNameSet`, never the name;
`output`, the chosen output's name; `alsoSpeakers`; `bargeIn`;
`cameraBackground`; `cameraPicture`, where a saved picture came from,
`cameraPictureSaved`, `cameraTool`, whether replies get
`set_camera_background`, and `checkInToolSet`, the Discord calls and camera
check-in tool set: its `id` (`discord`), its `tools` and the reply tools it
`replaces`). `doctor` checks this PC without recording or playing:
`appLoopback` (Windows can hear one app alone: a process loopback of Discord,
or of the MCP server itself while Discord isn't running, is set up and closed
unstarted, so `recorded` is always false; `appLoopbackProblem` otherwise),
`discordRunning`, `discordWindowShown`, `textReading` (Windows' OCR has a
language for the user's profile), `outputs` (how many playback devices),
`virtualCable` (the first one that looks like a virtual cable's input),
`chosenOutputPresent` and `voiceGoesTo`. `simulation` runs a simulated
call utterance through the production path: `audio` (a fixture call voice
through `PcAudioCaptureFactory`, `MicrophoneCapture` and voice activity on a
simulated clock, two `utterances`), `attribution` (fixture pictures of the
Discord window drawn with GDI, never shown, kept or sent: a voice channel's
member list with Alice lit, the call grid with Bob's tile lit and the member
list with only the owner, Ben, lit; each `scene` has its `speakingGreenPixels`, `marks`
(`Ring`/`Tile`), `expected` and `named` from the production
`DiscordSpeakingDetector` and Windows' real OCR on this PC), `message` (the
lines as the talk window sends them, `[PC audio] Alice in the call: ...`),
`turns` (each line, whether it says Martlet's name and so is answered at
once, and the fixture reply: NOT AI, it answers when the name is said and
otherwise passes) and `prompt` (the default *In your Discord call*
instructions). `ok` needs two utterances, every scene named as expected
(skipped where Windows has no OCR language) and the fixture replies. It
contacts nothing; real Discord calls, a virtual cable and OBS are not
exercised.

The **Creations** page (`NavCreations`, between Companion and Diagnostics; its
content is `CreationsPage`) lists [what Martlet made](../CREATIONS.md), newest first.
`CreationsNote` reads the fixed "Ask Martlet to sing or show any of these.",
`CreationsEmpty` the empty state "Things Martlet makes, like songs, appear
here.", `CreationsSummary` how many creations and how large ("2 creations,
1.2 MB on every computer.") and `CreationsStatus` whether they are shared with the
paired computers ("Creations shared with 2 of 2 computers at 9:41 PM.", copies
still arriving, hosts to update, or "No other Martlet computers are paired yet, so
your creations stay on this PC."). Each creation is a choice card
`Creation-<key>` (passive: it only shows that creation) with its line
`CreationState-<key>` (kind, length, size, when and on which computer it was made,
and whether it is on this PC and on how many hosts; never its title). The selected
creation shows `CreationTitle` (never returned), `CreationKind` (the same line
without where it is), `CreationSync` (this PC and which hosts hold it),
`CreationAsk` ("Ask Martlet to sing it.", or that this Martlet doesn't know its
kind yet), its text and details. There is no Play, Show or Activate for any kind.
`CreationRenameText` with `CreationRename` (or Enter) renames it on every computer,
and `CreationDelete` asks with `ConfirmationYes`/`ConfirmationNo` and deletes it
everywhere (a tombstone travels); both need `--allow-ui-effects`.
`creations_status` reads the same list headlessly, and `creations_check
{"seedDataDirectory": ...}` fills a disposable folder with two test tones for
checking the page. It puts them in the data folder itself, as an install from
before accounts kept them: a desktop started on that folder moves them into the
first account that signs in there, and a person added with `AccountAddPerson`
starts with none (*No creations yet.*).

Below it, each MCP server has
`ToolsServerState-<name>`, `ToolsServerOn-<name>`, `ToolsServerTrust-<name>`,
`ToolsRestart-<name>` and, for mcp.json servers, `ToolsRemove-<name>` (asks with
`ConfirmationYes`/`ConfirmationNo`, then edits mcp.json). `ToolsBrowseDirectory`
opens the MCP directory (`McpDirectoryWindow`), which loads a page from a public
directory at once, so it needs `--allow-ui-effects`, as do `McpDirectorySource`
(choosing a directory searches it), `McpDirectorySearch` with
`McpDirectorySearchButton`, `McpDirectoryMore`, `McpDirectoryRepository` and
`McpDirectoryWebsite` (open a browser) and `McpDirectoryInstall` (writes
mcp.json and Windows Credential Manager, then starts the server). Passive:
`McpDirectoryClose`, the `McpDirectoryOptional` expander and each result
`McpDirectoryResult-<registry name>` (for example
`McpDirectoryResult-io.github.upstash/context7`), which only shows that server.
Snapshots return `McpDirectoryStatus` (loading, how many found and in what
order, for example *The 20 most popular servers on the GitHub MCP Registry (by
GitHub stars); ...*, why a search failed, or what was installed;
`ui_select` on `McpDirectorySource` takes the plain directory name, such as
`Official MCP Registry`), `McpDirectoryNoSelection`, and for the selected
server `McpDirectoryDetailTitle`, `McpDirectoryDetailName` (registry name,
version, stars), `McpDirectorySummary` (the chosen way to run it),
`McpDirectoryNeeds` (whether its runtime is on this PC, or which host it
connects to), `McpDirectoryInstalled` and `McpDirectoryCantInstall`. The form's
`McpDirectoryOption`, `McpDirectoryName` and `McpDirectoryInput-<input key>`
fields and the `McpDirectoryRuns` preview are not returned; `mcp_directory_plan`
shows the same plan headlessly and `mcp_servers_status` what was installed. A
running server shows in Home's `HealthCheck-tools` (*1 of 1 server ready*).

Companion › Smart home (`CompanionTab-SmartHome`) starts with its Now line,
`SmartHomeNow`, then the main choice in an option picker (`Picker-SmartHome-HomeAssistant`
and `Picker-SmartHome-Off` only show that option's details, so they are passive):
*Home Assistant*'s details have `SmartHomeControl` (*Use Home Assistant when I
ask*, shown once Home Assistant is connected and not in use yet) and *Off*'s
(*Martlet doesn't control your smart home*) have `SmartHomeControlOff` (*Turn
smart home off*, shown while Home Assistant is in use); both save
`smart-home.json`, so they need `--allow-ui-effects`. `PickerState-SmartHome`
reads whether Home Assistant is connected. `SmartHomeFind` (*Find on my
network*) is passive: it only sends one mDNS question for Home Assistant's
service type and lists who answers; `SmartHomeSetupCancel` only hides the setup
form. Everything else needs `--allow-ui-effects` and a disposable Home Assistant:
`SmartHomeCheck` (*Set up a new one*, reads the onboarding state of the address
in `SmartHomeAddress`), `SmartHomeFoundUse-<n>`, `SmartHomeSignIn` (opens the
browser), `SmartHomeConnect` (with `SmartHomeToken`), `SmartHomeDisconnect`
(asks first; with a paired host and the switch on it disconnects every
computer), the setup form (`SmartHomeOwnerName`, `SmartHomeOwnerUser`,
`SmartHomeOwnerPassword`, `SmartHomeOwnerConfirm`, `SmartHomeSetupControl`,
`SmartHomeSetUp`), `SmartHomeInstall-<host>` and
`SmartHomeHostUse-<host>`, `SmartHomeShareCheck`, `SmartHomeDevicesRefresh`,
`SmartHomeDeviceAdd-<n>`, `SmartHomeDeviceIgnore-<n>`, `SmartHomeAddMqtt`,
`SmartHomeUpdateInstall-<n>` and `SmartHomeRestart` (both ask first;
`ConfirmationYes`), `SmartHomeBackup`, `SmartHomeOpen` and
`SmartHomeManageRefresh`. Snapshots return `SmartHomeStatus` (connected or not,
address, name, version, whether the other computers use it), `SmartHomeAddress`, `SmartHomeFindStatus`,
`SmartHomeFound-<n>` (*Home: http://192.168.1.20:8123 (Home Assistant
2026.9.4)*), `SmartHomeSetupTarget`, `SmartHomeSetupStatus`,
`SmartHomeHost-<host>` (whether that host runs or can run Home Assistant, or
why not), `SmartHomeShareState`, `SmartHomeShareStatus`,
`SmartHomeToolsStatus`, `SmartHomeDevicesStatus`, `SmartHomeDevice-<n>`
(*ESPHome: Kitchen light (kitchen-light)*), `SmartHomeMqtt`,
`SmartHomeManageStatus` (version, installation type, integrations, last
backup), `SmartHomeManageProblem` and `SmartHomeUpdate-<n>`. Token and password
fields are never returned; outcomes of actions are in `logs_tail` (`Status:`
lines).

Companion › Discord (`CompanionTab-Discord`): `DiscordSetupSteps` (*Step by
step*) only expands the setup steps. Everything else needs
`--allow-ui-effects`, a disposable data directory and no real bot token:
`DiscordOpenPortal`, `DiscordOpenBotPage`, `DiscordFixIntent` and the invite
buttons `DiscordInviteServer`, `DiscordInviteHome` and `DiscordInviteUser` open
the browser; `DiscordToken` (a password box, never returned) with
`DiscordTokenSave` saves the token in Windows Credential Manager and connects
(a token that isn't one shows *That isn't a Discord bot token...* in
`DiscordTokenStatus` and saves nothing); `DiscordForget` asks first
(`ConfirmationYes`) and removes it; the main choice's radio buttons `DiscordEnabled`
(*Connect Martlet to Discord whenever Martlet runs*, disabled until a bot token is saved) and
`DiscordEnabledOff` (*Off*), and
`DiscordReconnect` connect or disconnect the bot; `DiscordServerChat`,
`DiscordDirectChat` and `DiscordVoiceChat` (*Off*, *Only when mentioned*,
*Sometimes*, *Always*; `ui_select`), `DiscordDirectFromAnyone`, the channel
rule picker (`DiscordRuleChannel`, `DiscordRuleMode`, `DiscordRuleAdd`,
`DiscordRuleRemove-<channel>`), `DiscordOwnerId` with `DiscordOwnerSave`
(digits, a `<@mention>` or a link), `DiscordOwnerPick-<n>` (*That's me:
name*, from people the bot saw write) and `DiscordHomeServer` save
`discord.json`. Snapshots return `DiscordSetupNext` (the next setup step),
`DiscordConfigured` (*A bot token is saved for application 123...*),
`DiscordTokenStatus`, `DiscordState` (*Online as Martlet in 2 servers.*, or
*Not connected:* and why), `DiscordEnabledStatus`, `DiscordBotName`,
`DiscordServers`, `DiscordProblem` (a rejected token, or *Turn on Message
Content Intent...*), the invite links `DiscordServerLink`, `DiscordHomeLink`
and `DiscordUserLink` (each with its `Copy-` button), `DiscordChatModes`, the
three chat-mode choices, `DiscordRule-<channel>` (*Server › #general:
Always*), `DiscordRuleChannelsStatus`, `DiscordPeopleCount` (counts only),
`DiscordOwnerStatus`, `DiscordOwnerId` and `DiscordHomeServer`. The token is
never returned.

A host role's Add dialog (`HostInputDialog`) lists its choices as
`HostInput-choice.<VAR>` combo boxes whose selected value snapshots return (for
example `HostInput-choice.A2F_ENGINE` reads `local` or `nim`), the terms of the
chosen variant as `HostInputTerms-<VAR>`, and its secrets as
`HostInput-secret.<name>` password boxes, never with their values. A variant's
own secret appears only while its choice is selected (the Audio2Face NIM
engine's `HostInput-secret.ngc_api_key` only for `nim`); hidden fields are not
required and not sent. A variant's own choices work the same way, as
`HostInput-choice.<VAR>@<CHOICE>=<value>`: the `stt` role's speech recognizer
(`HostInput-choice.STT_ENGINE`, `whisper` or `parakeet`) shows either
`HostInput-choice.STT_MODEL@STT_ENGINE=whisper` (base to large-v3-turbo, with
*Run on* `HostInput-choice.accelerator`) or
`HostInput-choice.STT_MODEL@STT_ENGINE=parakeet` (*Parakeet TDT 110M
(English)*, *Parakeet TDT 0.6B v2 (English)* or *Parakeet TDT 0.6B v3 (25
European languages)*; Parakeet runs on the CPU, so *Run on* is hidden), and
either answers as `choice.STT_MODEL`. Adding listening preselects the
recognizer for that computer (whisper with an NVIDIA graphics card, else
Parakeet when it understands Windows' display language) and the Parakeet model
for that language, each with its reason in the label.
`HostInputOk` installs and needs `--allow-ui-effects`.
For a role the host already runs (*Change ... settings*, *Change model*), the
same dialog's `HostInputHeading` reads *Change <role> on <host>* (otherwise *Add
<role> on <host>*), `HostInputOk` reads *Apply*, and each
`HostInput-choice.<VAR>` (and `HostInput-choice.accelerator`) starts on what the
role runs with now, which `martlet-host describe` reports as
`role.choice_current` and `role.accelerator_current` (a variant's own choice only while that variant runs), without *Automatic*.
On a host with two or more NVIDIA cards the dialog adds
`HostInput-choice.gpu` (Automatic, each card by name and memory with the roles
already on it, or All cards); one-click installs on such a host (listening,
voice, singing on this PC) show a dialog with only that combo box before
installing. Hosts with one card never show it.
Adding a voice engine to a host that runs another one says in the dialog's
message that installing it stops that engine there (`martlet-host describe`
reports it as `role.stops`).

On Thinking, Voice, Listening and Lip-sync, each "Where it runs" option
(`Place-<page>-<place>`, for example `Place-Voice-Computer` or
`Place-LipSync-ThisPc`) only shows that place's choices, so clicking it is
passive; the card's own buttons commit. Under *Another of your computers* on
Thinking, Listening and Lip-sync, each paired computer that can run the job
(every one except this PC's own host service on Listening and Lip-sync; a host
saved as *This PC* whose address is another computer counts as that other
computer) is listed with `HostChoice-<job>-<host ID>` (for example
`HostChoice-listening-diva-host`), which reads the host ID and what it does or
could do. `HostChoices-<job>` says why none are listed (none paired, only this
PC's own host service, or none can run it) and `HostChoicesUnable-<job>` names
paired computers whose platform or hardware can't run it, with why.
`Help-HostJobData-<job>` (after the note under the list) reads what goes to
that computer, and for a voice engine its model's licence, with a link to the
wiki's *Hand a job to another computer*. A job's problem in its *Now* card
shows its first sentence with `Help-SetupJobProblem-<page>` (all of it, with a
link to *When no machine can do a job*).
`SetupUseHost-<job>-<host ID>` hands the job over and needs `--allow-ui-effects`.
A computer that runs the job's role also has `SetupChangeHost-<job>-<host ID>`
(*Change model* on Thinking and Listening, *Change settings* on Lip-sync; its
returned name says what it runs now, for example *Change model: conversation
model on diva (now gemma4-e4b)*), which opens the role's settings there and
needs `--allow-ui-effects`. Once a host serves a new model, this PC's job and
Deep thinking follow it on the next check (`logs_tail` and the status line say
*Thinking on diva now uses ...*).
Voice lists its engines per computer instead (`SpeakingHost-<host ID>` and
`VoiceEngineUse-<key>`, above).

Companion › *App guides* (`CompanionTab-AppGuides`,
[App guides](../APP_GUIDES.md)): `AppGuidesNow` (*Off: Martlet doesn't read up on
games and apps, or notice which one you play.*, *On: no guides yet.* or *On: 2
guides, 84 pages, 6.1 MB; reading up on Stardew Valley (Reading page 3).*), the
fixed `AppGuidesDisclosure` (its first sentence, with `Help-AppGuidesAbout`), `AppGuidesFront` (*In front:
Elden Ring (a game, full screen), its guide is ready.*, or why Martlet doesn't
look), `AppGuidesEmpty` and `AppGuidesResult` (what the last action did), the
`AppGuidesOn` and `AppGuidesAsk` (*Read up when I start a game or app*) check boxes (`checkedState`; `ui_toggle` saves
`guides\library.json`, so it needs `--allow-ui-effects`), and for each app on
the list `AppGuideName-<key>` and `AppGuideStatus-<key>` (*42 pages, 3.1 MB, read
10 Oct 2026, 18:40; reads from stardewvalleywiki.com; programs: StardewValley.*,
*No guide yet.*, *Reading up now (...).* or the last problem; never a guide's
text). Its buttons `AppGuideRead-<key>` (*Read up now* or *Read again*: reads on
the web and keeps the pages Read more added), `AppGuideMore-<key>` (*Read more*,
on an app with a guide: reads more pages of its wiki on the web), `AppGuideStop-<key>` (*Stop reading*, on the app being read: ends the
read and keeps what it read), `AppGuideAskAgain-<key>` (*Allow again*),
`AppGuideDelete-<key>`, `AppGuideAdd` and `AppGuideAddRead`, and typing in
`AppGuideAddName`, `AppGuideAddPrograms` and `AppGuideAddSites`
(`ui_set_text` or `ui_type`), need `--allow-ui-effects`. These three boxes read
as what is typed in them. They stay the same boxes when the page draws again
(reading up starts, adds the app or ends, or Home refreshes), so they keep the
text, the caret and the keyboard focus. To check it, `ui_type` a letter into
`AppGuideAddName`, change a setting with `settings_set` (Martlet reads
`settings.json` again and draws the page again), then `ui_type` more letters and
`backspace` without `id`: the box shows the typing and `ui_snapshot` marks it
`focused`. While a box is empty, its grey hint (`AppGuideAddNameHint`,
`AppGuideAddProgramsHint` and `AppGuideAddSitesHint`, Martlet's own fixed
words) shows; with `layout`, a hint's `bounds` start at the same `x` as the
`textBounds` of text typed into its box. The box labels line up with the
card's heading and buttons, so a box's `bounds` start one label column (110
pixels at 100 % display scale) right of `AppGuideAddRead`'s left edge.

At the end of Companion › Discord, *Martlet in your Discord calls* (see
[DISCORD.md](../DISCORD.md#martlet-in-your-own-calls)) has `DiscordCallOn` (the
mode, off by default), `DiscordCallCapture` (*The Discord app only* or
*Everything this PC plays except Martlet*), `DiscordCallSeeSpeakers`,
`DiscordCallOwnerName` (the owner's Discord name; never returned),
`DiscordCallOutput` (*Martlet's usual output* or a playback device, a virtual
cable marked *(virtual cable)*), `DiscordCallAlsoSpeakers`,
`DiscordCallBargeIn`, `DiscordCallCameraBackground` (*Green*, *Blue*,
*Magenta*, *Black*, and *Picture* once a picture is saved), the camera
picture's `DiscordCallCameraFile` (*Choose a picture file...*, a file dialog),
`DiscordCallCameraCreation` (*A picture from Creations...* and each picture
creation on this PC; shown only when there is one, its titles never returned),
`DiscordCallCameraPrompt` and `DiscordCallCameraDraw` (*Draw it*; shown only
while Companion › Picture making has a place or `MARTLET_PICTURES_FIXTURE=1`, it
draws a 16:9 picture, keeps it as a `picture` creation and uses it) and
`DiscordCallCameraPicture` (the saved picture's preview),
`DiscordCallCamera` (*Open camera view* / *Close camera
view*), the camera framing buttons `DiscordCallCameraZoomIn` (*Bigger*),
`DiscordCallCameraZoomOut` (*Smaller*), `DiscordCallCameraLeft`,
`DiscordCallCameraRight`, `DiscordCallCameraUp`, `DiscordCallCameraDown`
(each moves the character 10 pixels) and `DiscordCallCameraReset` (*Reset
framing*), enabled while the camera view shows, and `DiscordCallCheck` (*Check this PC*, a SafeClick: it lists the
playback devices, looks for Discord and sets up a process loopback unstarted).
Toggling, choosing, the picture controls, the camera button and the framing buttons save
`discord-calls.json`, draw or show a window, so they need `--allow-ui-effects`.
Returned (SafeValues):
`DiscordCallStatus` (*Off: Martlet isn't in your Discord calls.* or *On:
Martlet hears the Discord app* (or *hears everything this PC plays except
itself*)*, sees who talks: <source>, and speaks into <output>.*),
`DiscordCallAttribution` (*Who is talking: the Discord window; 2 people named
so far.*, never who), `DiscordCallOutputStatus` (where Martlet's voice goes,
or that the chosen output isn't connected), `DiscordCallCameraStatus` (open
or closed, with its background), `DiscordCallCameraPictureStatus` (*The
camera shows a picture from a file.* / *from Creations* / *Martlet drew*,
*Drawing it on ...…*, or why a picture couldn't be used; never a title or the
instruction), `DiscordCallCameraFraming` (*Framing: the character at its
fitted size, centered.* or, say, *at 150% of its fitted size, 12.5% right and
5% up of center*, the saved framing, which dragging, the wheel and the arrow
keys in the camera window also save once they settle; `SetupCharacterView`
then reads the camera's zoom), `DiscordCallDoctor` (Check this PC's result)
and the three choices. While the mode is on, the talk window's `LivePcAudio`
line says *In your Discord call.* or *Hearing someone in your Discord call…*
(its `help` is the mode's line) and lines from the call show in
`LiveHistory` as bubbles labelled *Discord call*, each starting with who said
it (*Alice in the call: ...*). `discord_call_check` reads the same mode.
While the mode is on, every reply on a route that does function calling also
gets `set_camera_background` (last among Martlet's own tools): `color`,
`picture` (a picture creation's id) or `draw` (a new 16:9 picture drawn as a
`picture-N` job and used when it's ready), so Martlet changes its own webcam
background. It updates `DiscordCallCameraPictureStatus` like the card's own
choices (*...a picture from Creations* / *a picture Martlet drew*, never a
title), and the desktop log notes *Discord call: set_camera_background chose
...*. When the After each exchange check-in takes it over (the `discord`
check-in tool set), the reply is not offered it and the check-in's run record
lists the call instead (`check_ins` status, `CheckInRun.Tools`).

Window discovery uses visible top-level native handles filtered to the attached
process (and its own character renderer child process), then verifies ownership
around each UI Automation handle lookup.
This avoids transient omissions from UI Automation's desktop-root enumeration
when unrelated WPF windows close. The Martlet main-window automation ID is
still required on every operation, unless the window is hidden in the
notification area and the process still has its icon's (hidden) window;
closed or changed-owner windows fail rather than falling back to another
process. Same-process dialogs remain available, and duplicate control IDs
still fail as ambiguous unless `ui_click` names the window (`window`: its title
as `ui_snapshot` lists it), as for side-by-side run windows.
