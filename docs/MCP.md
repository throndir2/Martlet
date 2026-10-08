# MCP in Martlet

Martlet speaks the Model Context Protocol in both directions:

- **As a client** (below): MCP servers on your PC give Martlet tools it can use while
  you talk.
- **As a server** ([Local MCP control](#local-mcp-control-windows)): `Martlet.Mcp`
  lets an MCP client run Martlet's diagnostics and drive its desktop UI.

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
the background when you open a talk window (or press *Start servers now*), and a
reply waits at most 10 seconds for servers still starting. Servers that stopped are
retried when a talk window opens again or from the Tools page. Each server's state,
tools and recent error output are on the Tools page.

**Which replies get tools.** Only replies to what you say or type (and Martlet's
own reply bringing up finished background work), and only when
Thinking uses OpenAI or a Chat Completions endpoint (local Ollama, LM Studio,
OpenRouter, NVIDIA Build...). Screen and camera glances and memory requests never
get tools, and a paired Martlet host's gateway has no function calling, so replies
from a host's model don't offer tools. If the model rejects a request because it
doesn't support tools (many small local models), Martlet asks it once more without
tools and stops offering them to that model for a week on this PC
(`tools-unsupported.json` in the data folder). Martlet's own `think_longer` and
`cancel_thinking` ([Thinking longer](#thinking-longer)) come first, then the
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
Thinking model with each reply that offers tools; each tool result (cut to 12,000
characters, and to the reply's 64 KB tool budget) goes to the same model. The model
is told that tool output is data, not instructions.

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

### Thinking longer

**Companion > Thinking pool > Thinking longer** (on by default; *Where it thinks* ›
*Off* turns it off) gives every reply on a route that does function calling
Martlet's own `think_longer` (`task`, the complete instruction, and an optional
`reason`) and `cancel_thinking` (optional `id`), while Deep thinking can run where
it is set to think. The call never asks first and returns at once; the task is
worked out in parallel, in a background request with Thinking steps on, by Deep
thinking (the Thinking model when its provider answers several requests at once,
another of your computers, a second model in Ollama on this PC or a cloud
provider, chosen on each PC), and brought up when it's done
([Thinking longer and background work](CONVERSATION.md#thinking-longer-and-background-work)).
The Tools page's *Recent tool use* lists each call (`Martlet > think_longer:
started think-1`); the desktop log notes each start, fit check and end without the
task or result (`{"name":"logs_tail","arguments":{"contains":"Background"}}`).

### Thinking pool

**Companion > Thinking pool** is one shared set of Thinking models for
background work ([The Thinking pool](CONVERSATION.md#the-thinking-pool)).
`thinking_pool_status` (`dataDirectory`) reads `thinking-pool.json` (or what
Martlet would make from the older `deep-thinking.json`, without writing it):
each member with its slots, whether it sees pictures or hears recordings and
whether it can run; the computers the owner keeps out (`leftByOwner`, host IDs
only: they never join by themselves); *Use the conversation model when the pool is empty*; the
usable slots and whether one stays free for fast jobs; each job kind's
priority, whether it is fast and whether a member can run it (`canRun`);
guidance and likely-slowdown warnings; and the desktop's
`thinking-pool-status.json` (`leftByOwner`, running and waiting jobs by kind, the live floor's
level, the jobs waiting for the conversation and the ones it stopped this turn
and in all, `resting`: each member whose computer refused a request as invalid
with what such jobs need and when it gets them again, never a job's text).
`thinking_pool_check` rehearses the production job board with simulated
members (NOT models): no member, capabilities, the fast slot, priorities, retry
on another member, a stale job dropped, the migration, and a member that
refused a request as invalid resting for such jobs while it still takes the
others. Its `auto-join` steps
run the production rule (`ThinkingPoolAutoJoin`) on sample hosts: a host with
the Thinking pool role joins with its slots, an Ollama-only host joins unless it
does this PC's Thinking, a member on Ollama moves to the role, and a computer
kept out, one Sharing work never uses, a full pool and a host PC are skipped. On the desktop the
card reads through `ThinkingPoolSummary`, `ThinkingPoolGuidance`,
`ThinkingPoolWarnings`, `ThinkingPoolLiveFloor` (which members start no new
pool work while you talk with Martlet because they share the conversation's
computer) and `ThinkingPoolMember-<n>` (it says *Offline now* for a member whose
computer doesn't answer); *One of your computers* reads through
`DeepThinkingAutoJoin` (computers with a Thinking model join by themselves, and
which ones are kept out) and `DeepThinkingHost-<host>` (*In the pool, offline
now*, *Kept out of the pool*, or what joins at its next check). The
`ThinkingPoolUseConversationModel` box, `ThinkingPoolSlots-<n>`,
`ThinkingPoolRemove-<n>` and `DeepThinkingPool-<host>` (*In the Thinking
pool*: unticking keeps the computer out, ticking adds it again) save
`thinking-pool.json`, so they need `--allow-ui-effects`.

The pool follows which computers answer
([Computers that go offline](CONVERSATION.md#computers-that-go-offline)).
`thinking-pool-status.json` gives each member `online` (whether its computer
answers now) and `offlineSince`, and the pool `slots` and `free` (members that
answer now), `configuredSlots` (every member) and `conversationModelStandsIn`
(every member that would run is offline, so thinking longer and research use the
conversation model). The desktop writes the file again on each change.
`thinking_pool_status` copies this into each member's `online` and
`offlineSince` and into `presence` (`slots`, `free`, `configuredSlots`, the
`offline` names); both stay null until the desktop has written the file. The
`presence` steps of `thinking_pool_check` take a member offline and back with
the production broker and plan. They check that its slots leave and come back,
jobs go to the other members, a job waiting in line starts on the member that
answers again, and the conversation model stands in when every member is
offline. They also check that the `think_longer` tool text stays byte-identical
during all of this. The member list in `background-jobs.json` (`places`) gives
each place `offline`.

```powershell
.\scripts\Invoke-MartletMcp.ps1 -Calls '[{"name":"thinking_pool_check"},{"name":"thinking_pool_status"}]'
```

**Backup Thinking** ([a hedged request](CONVERSATION.md#backup-thinking-a-hedged-request),
off by default): when the reply's Thinking model has no first words after the
wait, the same request also goes to a member that may answer for the
conversation, and the first to start gives the reply. `thinking_pool_status`
shows each member's `answersForConversation` and `paid` (a cloud provider), a
`backup` part (on, `delayMs` or automatic, the automatic wait's limits and
`wouldAsk`: the member the production choice picks now for a reply that is
taken, and why), and in the desktop's file `backup` (the wait used, the
automatic wait, recent replies, its 95th percentile, the last results and
counts; never what was said). `backup_thinking_check` (`scenario`, `delayMs`
one of 500-3000, 900 by default) rehearses the production race in
`ConversationTurn`, `ConversationRuntime.OpenTextAsync` and the member choice
(`ThinkingBackupMembers`) with two fixture Chat Completions endpoints on
127.0.0.1 (canned words, NOT AI), which note when the client stopped their
stream. Scenarios: `backup-wins`, `conversation-wins`, `late-conversation`
(the member asked, then stopped), `conversation-fails`, `no-member`, `held`
(only a paid cloud member: asked once the reply is taken), `let-go` (a reply
started early and let go stops its member's stream too) and `members` (the
rules). Each turn reports its outcome, the member, when it was asked, the
first words, each endpoint's requests and whether its stream was stopped, and
the reply latency line. On the desktop the card reads through
`ThinkingPoolBackup` (the box), `ThinkingPoolBackupDelay` (the wait),
`ThinkingPoolBackupStatus` (who may answer and the wait now) and each member's
`ThinkingPoolAnswers-<n>` (*May answer for the conversation*); the box, the
wait and each member's box save `thinking-pool.json`, so they need
`--allow-ui-effects`.

```powershell
.\scripts\Invoke-MartletMcp.ps1 -Calls '[{"name":"backup_thinking_check"}]'
```

### Image and audio models

Thinking, the text model, writes every reply. Pictures and recordings go to it,
or to an image or audio model of their own that puts them into words for it
([Image and audio models](SENSE_MODELS.md)). `sense_models_status`
(`dataDirectory`) reads `sense-models.json` (each kind's `source`: `Thinking`,
`OtherSense` or `Own`, and the model of its own with where it runs, whether it
uses its own key or Thinking's, and whether it sees and hears, never a key),
the Thinking model with whether it sees and hears, and for pictures and
recordings the `path` (`Thinking`, `Described` or `None`), the model, `unknown`
(Martlet can't tell whether that model sees or hears) and `why`. `oneModel`
says both kinds use the same model of their own, and `allThinking` that
neither has one. `desktop` is the desktop's `sense-models-status.json`: for
each kind its path, model and why, `sharesConversation`, and its line (`busy`,
`waiting`, `held` for a reply, `runs` and the `last` job's purpose, outcome,
milliseconds, model and problem; never what was sent or said).

`sense_models_check` rehearses the production routing (`SenseRouting`) over
the combinations of text, image and audio models with fixture model names, the
`sense-models.json` round trip, and the production lines (`SenseLanes`) with a
simulated runner, NOT models: no model of its own, one job at a time, a newer
picture taking the place of a waiting one, priorities, a stale job, refusals,
failures, a timeout, the kind check, one line for one model used for both
kinds, and the conversation first (`lanes-hold`: a job waits while a reply
holds the model's hardware, a running job is stopped, and a job the hold
outlasts is dropped). In-process; it reads nothing.

```powershell
.\scripts\Invoke-MartletMcp.ps1 -Calls '[{"name":"sense_models_check"},{"name":"sense_models_status"}]'
```

### Live floor (the live turn first)

The live floor puts the live conversation turn before all background work
([The live floor](CONVERSATION.md#the-live-floor-the-live-turn-comes-first)).
`live_floor_status` (`dataDirectory`) reads the data directory's routes and
Thinking pool: what the conversation runs on (`resources`: Thinking, voice and
listening, each on `this-pc`, a home computer or a cloud provider, with the
paired hosts and routes the floor holds while you talk), which pool members
share it (`members[].shares`), what the floor does to each job kind on such a
member at Listening and at Live (`rules`), and the desktop's `live-floor.json`
(`desktop.file`: its level, replies holding it, Live periods, live resources,
members, what it held and stopped by kind this turn and in all with the reply
latency part, the hold client, the hosts asked and the holds they granted, the
work queue's stopped background requests and the last changes with why; never
what was said). `Invoke-MartletMcp.ps1` gives it the disposable data directory
unless one is named. `live_floor_check` (optional `said`: up to 32 lines of
your own to classify) rehearses the production `LiveFloor`, `LiveFloorRules`,
`ThinkingJobBoard`, `BackgroundJobs` and `WorkQueue` with fixture inputs and
simulated members (NOT models), and returns `passed`, each line's `realWords`
and each step: which words go Live, the levels on a clock of their own (voice,
quiet, a sound, words, a reply and its grace), the board at Listening (new work
waits for the conversation, running work and judges go on) and at Live (a
summary dropped as `Preempted`, remembering and naming stopped and queued again,
touch zones going on, judges running), a member on another computer never held,
a think stopped and going on from what it wrote (in place as the unfinished
assistant message, or again with it as context), research waiting for the
conversation instead of being refused, the conversation model's own place held
above Idle, and the work queue stopping this PC's background request for a live
reply. The desktop log's `Live floor:` lines say each change, and the reply
latency line ends with what the floor held and stopped
(`latency_report` returns it as `liveFloor`).

```powershell
.\scripts\Invoke-MartletMcp.ps1 -Calls '[{"name":"live_floor_check","arguments":{"said":["Mm-hmm.","Can you check the weather?"]}},{"name":"live_floor_status"}]'
```

### Searching past conversations

**Companion > Memory > Conversation history > Let Martlet search the record on
its own** (off by default) gives every reply on a route that does function
calling Martlet's own `search_conversations` (`query`, words to look for, and/or
`when`: today, yesterday, 3 days ago, last week, a weekday or YYYY-MM-DD), after
`think_longer` and `cancel_thinking`, always worded the same. It never asks
first, searches the [record of conversations](MEMORY.md#conversation-history)
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
[Creations](CREATIONS.md). The Tools page's *Recent tool use* lists each call
(`Martlet > perform_creation: performed`), never titles or options.

### Reading

Companion › Reading ([Reading](READING.md)) chooses where Martlet reads the text
on the screen while it watches: Windows OCR on this PC (the default), the `ocr`
host role (route `martlet.gateway.ocr.v1`, [Reading host role](OCR_HOST.md)) or
off. The local server's `reading_check` reads a data directory's `reading.json`
(`dataDirectory`), then reads a drawn 1024 x 576 test picture with known text
(`HEALTH 87 / 100`, `Score: 12450`, `VICTORY`, a chat line) with Windows OCR on
this PC, as watching does. It returns the lines, the joined text, the missing
words and the milliseconds (the first read and a second one). With `endpoint`, a
Reading worker on loopback such as `http://127.0.0.1:50087/`, it also calls the
worker's `GET /status` and `POST /read` with the same picture as a PNG. It never
captures the real screen.

Desktop automation: Companion › Reading's `ReadingPlace-<place>` and
`ReadingHost-<host>` choices are passive clicks. `ReadingNow`, `ReadingLast`,
`ReadingTestState`, `ReadingWindowsState`, `ReadingEngine`, `ReadingFeatures`,
`ReadingHostState` and the labels of `ReadingSetUp`, `ReadingUseHost`,
`ReadingUseThisPc`, `ReadingTurnOff` and `ReadingTest` are safe values. Read my
screen now (`ReadingTest`) captures the screen, so it needs
`--allow-ui-effects`. The text it read (`ReadingTestText`) is never returned.

### Pictures

While Companion › Pictures has a place (or `MARTLET_PICTURES_FIXTURE=1`), every
reply on a route that does function calling also gets `draw_picture`
(`description`, optional `title`, `shape`, `avoid`), after the song tools. It
starts a `picture-N` background job and returns at once; the finished picture is
kept as a `picture` creation and shown in the talk window (`LivePicture`, whose
click opens `LivePictureViewer`), and `perform_creation` shows a kept one again.
See [Pictures](PICTURES.md). The talk window's status says *Starting a picture in
the background…* while it is called, and the desktop log notes `Pictures:`
lines (where, size, seconds; never the description).

The local server's `pictures_status` reads a data directory's `pictures.json`
(place, workflow, checkpoint or model, whether an own key is saved; never a key),
the loaded workflow's node count, the picture creations (shape, size, engine,
model, seconds, fixture; never titles or descriptions) and the tool and job kind.
`pictures_check` draws one picture through the production maker: `place`
`fixture` (default) or `comfyui` with `address` (and `workflow`
`z-image-turbo`, `checkpoint` with `checkpoint`, or `custom` with `workflowFile`),
reporting availability, every progress stage, the media type, size, SHA-256 and
seconds; with `dataDirectory` it keeps the picture as a creation there and reads it
back, with `saveDirectory` it writes the file. It never calls OpenRouter or NVIDIA
Build (a picture costs money). Desktop automation: Companion › Pictures'
`PicturesPlace-<place>` and `PicturesHost-<host>` choices, `PicturesCheck` and
`PicturesComfyConnect` are passive clicks; `PicturesNow`, `PicturesTestState`,
`PicturesEngine`, `PicturesFeatures`, `PicturesHostState`, `PicturesSetUp`,
`PicturesUseHost`, `PicturesComfyAddress`, `PicturesComfyState`,
`PicturesWorkflow`, `PicturesLoadWorkflow`, `PicturesUseComfy`, `PicturesModel`,
`PicturesKeyStatus`, `PicturesUseCloud`, `PicturesTurnOff` and `PicturesTest`
return their text. Set up, Draw with..., Turn pictures off and Draw a test picture
save, install or draw, so they need `--allow-ui-effects`; the Creations page shows
a picture as `CreationPicture`.

### Reminders

On a PC that keeps reminders (any with a data folder), every reply on a route
that does function calling also gets Martlet's own `reminders` (`action` set,
list or cancel; `text`, `in_minutes` or `at` for set; `id` for cancel), last,
after `manage_memories`, always worded the same. It never asks first. Set
returns the id and the time it is due ("Reminder 3f9a1c set for 4:12 PM (1 h
from now)"), so the model needs no clock of its own. *Recent tool use* lists
each call (`Martlet > reminders: set`), never the text; the desktop log notes
each offer, take and reminder said by id only (`Reminders:`). See
[Reminders](CONVERSATION.md#reminders).

### Check-ins

[Check-ins](CONVERSATION.md#check-ins) aren't a tool the model calls: every few
minutes a Thinking pool member answers one short question for Martlet (do the
lingering emotes still fit, does the gaze a reply chose still fit, did the
character keep its promises, did it stay in character, does it keep saying the
same things, and the owner's own),
and Martlet acts on the answer. Nothing changes in the conversation's tools or
instructions. A reminder for the next reply goes in the notes of that one
message only (context board source `check-in-<id>`), and the desktop log notes
each run with words and counts only (`Check-ins:`).

Companion › Check-ins reads through `ui_value`: `CheckInsNow` (how many are on
and the member that takes them first, or why they can't run), `CheckInsLast`
(the last check-in that ran, when, on which member and what came of it, never
what was said or answered) and, for each check-in, `CheckInStatus-<id>` (why it
waits, its last run, runs and actions since Martlet started), `CheckInOn-<id>`
and `CheckInEvery-<id>`; for the owner's own, also `CheckInOutcome-<id>` and
`CheckInFact-<id>-<fact>`. `CheckInsOpenPool`, `CheckInsOpenPrompts` and
`CheckInPrompt-<id>` only open a page. The boxes and choices, the name and task
boxes, *Add a check-in* (`CheckInAdd`) and *Remove* (`CheckInRemove-<id>`) save
`check-ins.json`, and *Check now* (`CheckInRun-<id>`) sends the check to a
Thinking pool member, which may be a paid provider, so they need
`--allow-ui-effects`.

Setting `MARTLET_CHECK_INS_FIXTURE` to a text file before launching the desktop
makes every check-in read its answer from that file instead of asking the
Thinking pool (FIXTURE - NOT AI; read again before each run, and check-ins run
then without a pool member). `CheckInsNow` and each run's member say *FIXTURE -
NOT AI*, and `check-ins-status.json` says `pool.fixture`. A file with a
`REMIND:` line and a `SAY:` line answers both kinds of the owner's own
check-ins, so *Check now* shows the whole flow on a disposable data directory:
a reminder waiting for the next reply, or something Martlet brings up.

## Local MCP control (Windows)

`Martlet.Mcp` is a local stdio Model Context Protocol server. It does not listen
on a network port, start the desktop, or activate a provider, microphone or
speaker on launch. Configure an MCP client to start the Windows executable
built from `src\Martlet.Mcp` (for development:
`src\Martlet.Mcp\bin\Release\net10.0-windows\Martlet.Mcp.exe`). The server
speaks newline-delimited JSON-RPC 2.0 on standard input/output; stderr is for
diagnostics. This developer companion is not included in the existing internal
installer payload. Build locally with:

```powershell
dotnet build src\Martlet.Mcp\Martlet.Mcp.csproj -c Release
```

For example, a client configuration with an absolute executable path:

```json
{
  "mcpServers": {
    "martlet": {
      "command": "C:\\path\\to\\Martlet.Mcp.exe",
      "args": []
    }
  }
}
```

The headless tools `doctor_status`, `doctor_list` and `doctor_run` call the
production Doctor implementation in-process. Status and selected probes are
local read-only checks. They return Doctor's structured JSON report and exit
code; nonzero exit codes describe incomplete, failed or invalid results, not a
passed check. An optional absolute `dataDirectory` argument isolates settings
reads; without it, Doctor uses the current user's Martlet directory. No
headless MCP tool creates a profile, opens a device, plays a tone, sends a
request or handles credentials.

`doctor_run` with `platform.architecture` reports this PC's processor type:
`platform.x64`, or on Windows on Arm `platform.arm64_emulated` (the x64 build
under Windows' x64 emulation; the real processor is read with
`IsWow64Process2`) or `platform.arm64_native`. The same line is the desktop's
`ThisPcArchitecture` status field in Settings › Tools ("This PC: x64
processor; Martlet runs natively.", or the Windows on Arm wording and what it
means for NVIDIA jobs), and the Devices map's This PC shows it as *Processor
type*.

`voices_status` reads [voice recognition and Parakeet](VOICES.md) state from a data
directory (optional absolute `dataDirectory`, default the current user's): the
recognition choice (`on (default)` until it is turned off; a shared setting) and
`sharing` (the voice list travels while *Keep Martlet the same on all my
computers* is on, from `cluster-sync.txt`),
whether a Martlet folder (optional absolute `martletDirectory`, default the
installed release's `Desktop` folder; `Invoke-MartletMcp.ps1` passes this
checkout's Desktop build when it exists) includes the sherpa-onnx runtime and
the voice models (`included`: `found`, `runtime`, `voiceModels`), whether any
Parakeet model is downloaded (`parakeet`), `parakeetModels` (Companion ›
Listening › *Parakeet in Martlet*: `listening`, the Listening route type with
its `parakeetModel`, whether this Martlet `known`s it and whether it is
`downloaded`; `standIn`, the Parakeet model that hears an utterance on this
PC's processor when Listening's own route (a paired host or OpenAI) fails, as
the desktop chooses it (`model`, `name`), or why none does (`reason`:
*Listening isn't set up*, *Listening already runs on this PC*, *no Parakeet
model hears Windows' display language (ja-JP)* or *parakeet-tdt-110m-en isn't
downloaded in speechDirectory*); `displayLanguage` and the `recommended` model for it,
`parakeet-tdt-110m-en` for English and `parakeet-tdt-0.6b-v3-int8` otherwise;
and `models`, each with `id`, `name`, `languages`, `englishOnly`,
`downloadMb`, `revision`, `downloaded`, `notice` (its NOTICE file is there),
`recommended` and `inUse`), and counts from `voices.json` (voices, named, owner, with
learned names, `withCompanionName`: voices that learned one of the companion's
own names from the saved personas, which Martlet drops when it next hears them,
`withPlaceholderName`: voices that learned a placeholder such as "no name
yet", dropped the same way, `mostNames`: the most names one voice has, merged,
tombstones) and `clips` (`keep`: People's *Keep the last 5 clips* choice,
`voices` and `clips`: how many clips of voices not named yet are kept). It never
returns names, voiceprints or audio and runs no model.

`voices_naming_check` rehearses [learning names](VOICES.md#what-happens-in-a-conversation)
with the production checks and changes (`CompanionNames`, `VoiceUpdates` and the
voice list's name rules) on a fixture voice list in memory: V1 is the owner
(typed Robert, learned Bob) and not heard; V2 (typed Sammy, learned Sam and, by
mistake, Jane) and V3 (new, speaking) are heard; the fixture companion is the
persona "Jane Doe" ("You are Jane, ..."). Its `scenarios` (each `passed` with a
`detail`; `passed` is all of them) check that the companion's names, a word of
them and a name Martlet's reply gives itself are refused, a heard voice drops
Jane, a voice learns a name, keeps five names and shows the one it asked for
(CALL), a wrong learned name is dropped (NOT) but a typed one is kept, a voice
not heard gets no name, and SAME merges V3 into the owner's voice once per
exchange. With a `dataDirectory` whose settings have personas,
`companionNames` lists their names (`companionSource`) and one more scenario
refuses a saved persona's name. An optional `answer` (NAME/CALL/NOT/SAME lines
about V1-V3, at most 8192 characters) and `reply` are checked against the same
fixture: `answer.updates`, `refused` (line and reason), `applied` (what the talk
window would say) and the fixture `voices` afterwards. It never reads or writes
the saved voice list and uses no audio, model or network.

`parakeet_check` runs Companion › Listening › *Parakeet in Martlet*'s models
the way the desktop does (optional absolute `dataDirectory`; `speechDirectory`,
default the data directory's `speech` folder, where the desktop downloads
them; `martletDirectory` for the sherpa-onnx runtime, which the script fills
with this checkout's Desktop build). It loads each downloaded model (or those
named in `models`) through the production `ParakeetEngine` and transcribes
`phrases` (default four English sentences without numbers or names; up to 8)
said by a Windows voice (System.Speech rendered to memory, never played) after
0.3 s and before 1 s of faint noise. Per model it returns `loadMs`,
`memoryMb` (process memory the model added), each phrase's `transcript`,
`wordErrors`, `words`, `transcribeMs` and `meanProbability`, the
`wordErrorRate` and `medianTranscribeMs`, and `ok` (at most 20% word errors);
`ok` overall needs every model to pass, and `status` is `voices_status`'s
`parakeetModels`. Without the runtime or a downloaded model, `ran` is false
with the reason. Nothing is downloaded, recorded or played, and nothing leaves
this PC.

`voices_engine_check` runs the voice recognition engine that ships in a Martlet
folder (same optional `martletDirectory`) the way the desktop does: it loads the
bundled runtime and both models, takes a voiceprint of a generated test tone
(`loaded`, `voiceprintDimensions`, `loadMs`) and, for optional `wavFiles`
(absolute paths to at most 16 canonical 16 kHz mono PCM16 WAV files of at most a
minute each), reports for each file how many voices were heard, each
recognizable voice's clean seconds, overlap and speech seconds, plus a
`similarity` matrix of the files' main voices (cosine; Martlet treats 0.70 as
the same person). It returns counts and scores only, never audio or
voiceprints, and uses no data directory, device or network. Without the files
it returns `included: false`.

`f5_voices` lists Martlet's [starter voices](F5_VOICE.md#desktop-voices-and-playback)
as `starters` (key, name, `female`, `cute`, description, licence, transcript,
SHA-256, sample rate and duration; each clip is checked against its SHA-256 and the
reference store's audio, name and transcript rules, `valid` or the failure; a new
voice list starts with them, after which they are ordinary voices), the
`default` key, `defaultName`, `defaultFemale` (true) and `defaultCute` (false;
the default is the first starter voice, `jenny-dioco`), `cute`, the
keys of the two cute voices (`librivox-annie` and `librivox-woollybee`), and `retired`, the recordings Martlet no
longer ships (`key` and `name`: `retired-sample`, the F5-TTS example clip, and
`retired-librivox-annie-anime` and `retired-librivox-woollybee-anime`, the
former "anime" voices). From a data
directory (optional absolute `dataDirectory`, default the current user's) it reads
the shared voice list as `library` (`speaking-voices.json`; `state` `none` until a
voice is first used, added or removed or the desktop shares voices with a host,
`loaded` or `unreadable`): the number of live `voices`, `revision`, the keys of
the starter voices in it (`starters`), the keys of former starter voices it still
lists (`retired`: an older Martlet's list, empty once this desktop brings its
voices in step), the count of the owner's own voices
(`own`), tombstones (`removed`) and the keys of removed starter and former
starter voices (`removedStarters`), and the voice chosen on all computers
(`chosen`: a starter key, a retired key, `own` or null) with the device that chose it (`chosenBy`), and
`severalRecordings`: each voice made from several recordings, by the key the
Voices page uses (the first 16 hex digits of its ID), with how many `recordings`,
each one's length (`clipMs`), the joined `durationMs` and `sampleRate`, the
`engines` that can clone it and those that learn from each recording
(`learnsFromEach`; the others hear them joined). `list` is this PC's
recordings (the `f5-voices` store): `state` (`none`, `loaded`, `busy` while the desktop holds it,
or `unreadable`), the number of voices, the keys of starter voices in it (`starters`), the
count of the owner's own voices, the keys of retired recordings still there
(`retired`; each is deleted once nothing speaks with it) and the applied voice
(a starter key, a retired key, `own` or null).
`speaking` reads `settings.json`: `state` (`none`, `loaded` or `unreadable` with
the settings rule it broke or the error type as `problem`), the
speaking route's type (for example `GatewayF5`, null without one) and the voice it
records (a starter key, a retired key, `own` or null), plus `engine` (the
self-hosted voice engine whose route it records: `chatterbox`, `chatterbox-original`, `chatterbox-nano`, `f5`, `xtts`, `gpt-sovits` or `dia`), `host` and
`model` for a host route. `engines` lists the voice engines
([Chatterbox Turbo, Chatterbox Original and Chatterbox Nano](CHATTERBOX_VOICE.md), [F5-TTS](F5_VOICE.md),
[XTTS-v2](XTTS_VOICE.md), [GPT-SoVITS](GPT_SOVITS_VOICE.md), [Dia](DIA_VOICE.md); `key`, `name`,
`hostRole`, `routeId`, `path`, `model`, `weightsLicence`, `minimumGpuMemoryGb`
(0 for Chatterbox Nano, which also runs on the CPU),
`minimumReferenceMs`, `maximumReferenceMs`, `summary`, `default` (true for
Chatterbox Turbo), `supportsTags` and `tags`, each tag's `text` in the engine's
syntax, `kind` `Sound` or `Emotion` and `usage`, and `multipleReferences` (true
for XTTS-v2 and GPT-SoVITS, which learn from each of a voice's several
recordings); Chatterbox clones only
recordings longer than 5 s, GPT-SoVITS only 3,000-10,000 ms), `features` (its
other needs: Docker, recording lengths, streaming, languages), `runsOn` (where
it runs: `on` `gpu`, `cpu` or `online`, `vramGb` its typical and `peakVramGb`
its most graphics memory, `minimumGpuGb` the smallest card, `evidence`
`Measured`, `Sourced` or `Estimate`, all from the footprint catalog
([resource footprints](RESOURCE_FOOTPRINTS.md)), and `text`, the line
Companion › Voice shows) and `abilities`, the rundown Companion › Voice shows for it:
`cloning` and `sounds` (true or false), `emotions` (`None`, `WhisperOnly`,
`Intensity` or `Tags`), `items` (`Voice cloning`, `Laughs & sighs` and
`Emotions`, each with `level` `Yes`, `Partly` or `No`, a `note` when partly, its
`help` sentence and the `tags` that do it) and `summary` ("Voice cloning: yes.
Laughs & sighs: yes. Emotions: whispering only." for Chatterbox Turbo, whose
tones other than `[whispering]` don't change the voice and whose `[whispering]`
the model itself whispers only now and then). `otherVoices` gives the
same `abilities` and `runsOn` for the `windows` (CPU) and `openai` (online)
voices, neither of which clones, laughs or shows emotions. Each starter
voice adds `engines` (the engines that can clone it) and `language` (`en` or
`ja`, read from its transcript), `chosenEngine` is the engine chosen on this
desktop (`speaking-engine.txt`, default `chatterbox`) and `chatterboxStyle` is
Chatterbox Original's style saved there (`saved`, `generalExaggeration`,
`generalCfgWeight`, `expressiveExaggeration`, `expressiveCfgWeight` and
`summary`; Resemble's suggestions until Companion › Voice saves one). After the desktop
loads settings, a route or applied voice that was a retired key reads the
chosen or first voice. It never returns own voices' names, transcripts or audio, plays
nothing and contacts nothing.

`voice_recording_check` shows what *Add a voice* does with one file: `path`
(required) is an audio or video file on this PC. It runs the production
converter (`VoiceRecordingImport`) and F5's reference rules on the result and
returns `usable`; when usable, `sourceFormat` (the file's extension in capitals,
for example `MP3`; an Ogg file adds its codec, `OGG (Vorbis)` or `OGG (Opus)`),
`sourceChannels`, `sourceSampleRate` (for Opus, the rate it was recorded at, from
its header), `converted` (false
when the file is already a mono 16-bit PCM WAV Martlet keeps byte for byte),
the kept WAV's `sampleRate` (the source rate when it is 16, 22.05, 24, 44.1 or
48 kHz, else the next one above it, at most 48 kHz), `channels` (1),
`bitsPerSample` (16), `durationMs`, `bytes`, `sha256`, `peakDbfs` and `rmsDbfs`
(how loud that WAV, the one **Play** plays, is: about -96 means silence),
`voiceAlone` (whether
it passes F5's reference rules and so can be a voice by itself, with
`voiceAloneProblem` when not), `engines` (the voice
engines that can clone a recording that long) and `shown` (the line
`F5AddVoiceRecording` shows); otherwise `problem` (too long, too short, silent,
missing, unreadable, or for an Ogg file damaged or another codec). Ogg Vorbis
and Ogg Opus are decoded by Martlet itself, everything else by Windows.
Recordings from 0.5 seconds (the shortest one of several
may be) to 30 seconds are usable; a voice from one recording needs at least 1
second. Decoding stops just past 30 seconds. It never returns
the path or audio, saves nothing, plays nothing and contacts nothing.

`voice_tags` shows how a reply's [voice tags](CONVERSATION.md#voice-tags) are
handled: `text` (required) is a reply, `engine` an engine key (default the
default engine, `chatterbox`; `none` for a voice without tags such as OpenAI or
Windows) and optional `dataDirectory` whose saved prompt edits are used. It
returns the engine, `supportsTags`, its `tags`, the same split into `sounds`
(non-word sounds such as `[laugh]`) and `tones` (tones of voice such as
`[whispering]`), `cues` (each tag's
engine-independent cue, such as `laugh` for `[laugh]`), `synonyms` (each tag's
other words that also count as it, such as `whisper`, `whispers` and `hushed`
for `[whispering]`), `prompt` (the *Voice
sounds and tones* instructions the Thinking model gets, the sounds and the tones
each under a line saying where they go, or null), `spoken` (the
pieces the real speech segmenter hands that engine for a spoken reply, its own
tags kept), `suppressedPieces` and `shown` (the chat and caption text, every
tag stripped). The pieces break where the persona's [speech
breaks](CONVERSATION.md#voice-latency-streaming-overlap-and-barge-in) allow:
`dataDirectory`'s saved persona named `persona` (else the one Martlet uses,
else the defaults), with any of `breaks`' `periods`,
`questionMarks`, `exclamationMarks` (booleans) and `shortEndingWords` (0-5)
on top; `persona` and `breaks` (with `isDefault`) say what was used. Commas,
semicolons and dashes never break a piece.
With `characterTags` (the character's [emote and motion
tags](AVATARS.md#emotes-and-motions), such as `["{blush}"]`), those are stripped
too and `characterCues` lists the cues the character acts on: each one's
`piece` (index in `spoken`, or -1 for a tag after the last words), `tag` (a
character tag or the engine's own voice tag) and character `offset` in the
piece. [Other spellings](CONVERSATION.md#voice-tags) of a tag count as it
(`[nod]` or `*nods*` for `{nod}`, `(sighs)` for `[sigh]`): `acted` lists what the
reply's tags did, in order (`tag` as the character or engine spells it, `kind`
`Character`, `Sound` or `Emotion`, `name`, and `written`, the other spelling
used, or null), and `note` is the line the talk window shows under the reply,
such as *Tone: happy. Sound: laugh. Emotes: nod, blush.* (null without tags).
For example `{"name":"voice_tags","arguments":{"text":"Oh, look at all that
activity! [nod] What are you working on right now?","characterTags":["{nod}"]}}`
returns both sentences in `spoken` (before, the `[` silenced the second and the
chat showed `[nod]`), the `{nod}` cue at the start of the second, `written`
`[nod]` and `note` *Emote: nod.* It synthesizes and contacts nothing.

`cluster_status` reads [shared who does what](CLUSTER.md) from a data directory
(optional absolute `dataDirectory`, default the current user's): `sync` is
`on (default)` when `cluster-sync.txt` is missing, `on`, or `off` once the owner
unticked **Keep Martlet the same on all my computers**; `plan` is this PC's `cluster.json`
(`state` `none`, `loaded` or `unreadable`; when loaded its revision, each job's
`host` (null for this PC's own choice), `off`, `failover`, `movedFrom`,
`updatedBy` and `updatedAt`, and each host's ID, roles and `removed`). It never
returns host addresses or keys and contacts nothing.

`settings_sync_status` reads [one Martlet on every computer](CLUSTER.md#one-martlet-on-every-computer)
from a data directory (optional absolute `dataDirectory`, default the current
user's): `sync` as above, `state` (`none` before the first sync, else `loaded`),
the copy's `revision` and `count`, and for each shared setting its `key`
(`thinking`, `listening`, `speaking`, `thinking-fallback`, `companion`,
`replies`, `prompts`, `memory`, `lorebooks`, `character`, `character-actions`,
`talk`, `speech-display`, `appearance`, `voice-recognition`, `voice-id`,
`smart-home`, `updates`, a computer's own `pc.<device ID>`, a computer's
`role.<device ID>` (`companion` or `host`: written by that computer, or by
another one asking it to switch), or a newer
Martlet's), `updatedBy`, `updatedAt`,
`revision`, `usesKey`, `characters`, `off` (the value is null) and `here`:
`same` when this PC had exactly that value at its last sync, `different` while
it can't follow it yet (or changed it since), `unknown` when it never had the
setting. `value` is shown only for non-personal settings: each job's route
(`type`, `origin`, `model`, `voice`), the fallback's `origin` and `model`,
memory, how you talk, speech bubbles and subtitles, the theme, recognizing
voices (`on`), what Martlet may do with Home Assistant (`control`,
`allow_sensitive`, `model_tools`), app updates (`checks`,
`interval_minutes`, `auto_install`, `auto_update_hosts`) and each computer's
`pc.<device ID>` (`role`, `host`) and `role.<device ID>`. It never returns keys,
key digests, personality, prompt, lorebook or emote text, or the Voice ID
voiceprint, and contacts nothing.

`memory_sync_status` reads [one memory on every computer](MEMORY.md#one-memory-on-every-computer)
from a data directory (optional absolute `dataDirectory`, default the current
user's; the disposable one in `Invoke-MartletMcp.ps1`): `sync` as above,
`state` (`none` before the first memory sync, else `loaded`), `syncedAt`,
`facts` (how many facts the store had at the last sync), `byComputer` (how many
of those each computer wrote) and `forgotten` (the tombstones every computer
agreed on). It reads only `memory-sync.json` (IDs, revisions, digests and device
IDs), never a fact, and contacts nothing.

`memory_status` reads what Martlet remembers and [whose](MEMORY.md#whose-memories)
from a data directory (optional absolute `dataDirectory`, default the current
user's; the disposable one in `Invoke-MartletMcp.ps1`): `memory` (`on`, `off`,
`on (default)` for settings from before memory, `not set up`), `storage`
(`Martlet folder` or `custom folder`), `state` (`none` without a store file,
`loaded` or `unreadable`), `voiceList` (`voices.json`: `none`, `loaded` or
`unreadable`), `facts`, `typed`, `fromConversation`, `expiring` and `whose`:
`everyone` (facts not tied to a voice), `voices` (each voice facts belong to,
by its tag such as `V3`, with `named`, `owner` and `facts`) and
`forgottenVoices`. It reads the store's file as JSON without opening or
locking the store (the desktop can keep running), and never returns a fact's
text, a name, a voice ID or a path. It contacts nothing.

`conversation_history_status` reads the [record of conversations](MEMORY.md#conversation-history)
from a data directory (optional absolute `dataDirectory`; the disposable one in
`Invoke-MartletMcp.ps1`): `memory` (`on`, `off` or `not chosen`), `preferences`
(`conversation-history.json`: `keep`, on by default, and `search`, off by
default), `recording` and `recallWhenMentioned` (memory on and `keep`), `tool`
(whether replies are offered `search_conversations`, whether the Thinking route
does function calling, and the tool exactly as the model gets it with its
`utf8Bytes` and `estimatedTokens`), `prompt` (Companion > Prompts > *Past
conversations* as sent), `record` (`files`, `bytes`, `conversations`,
`exchanges`, `skippedLines`, `notIndexed`, `oldest`, `newest` and `apps`, the
exchanges per app: `pc`, `telegram`, `discord`, `whatsapp`) and
`platformChanges` (`conversations\platform-changes.json`: `pending`,
`pendingByApp`, `done`, `refused`, `gaveUp`, `lastProblem`). Never what was
said; it contacts nothing.

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
app waiting, kept over a restart, *Stop waiting changes*), and reading `bulkExchanges`
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
512 everywhere (oldest conversation facts forgotten, typed facts kept); no fact
in any desktop data folder while the hosts' copy holds them; and an unsigned
request refused (HTTP 401). Synthetic facts, loopback only; the folder is
deleted.

`settings_sync_selftest` (no arguments) rehearses shared settings end to end
with the production code (`src\Martlet.NodeLinkCheck`, mode `settings`,
`SettingsRehearsal.cs`, run as its own process like `node_link_check`; returns
`{exitCode, report}`): two real gateways (`lab-settings-1`, `lab-settings-2`;
Kestrel, pinned TLS, signed requests, an in-memory `shared-settings.json`) and
three simulated desktops (`lab-desktop-a..c`) with real `settings.json`,
`lorebooks.json` and `shared-settings.json` in a temporary folder, an in-memory
stand-in for Windows Credential Manager (with write times), the desktop's paired
client (`HostSettings.cs`) and the real sync engine and sections
(`Martlet.Core.Sync`). Its steps follow the owner's case: B chose NVIDIA Build
with its key on Sept 1, A chose OpenRouter with its key on Sept 20; after
updating, B (now the companion) syncs first and shares NVIDIA Build stamped
Sept 1, A (now the host PC) syncs and OpenRouter wins, and B takes OpenRouter,
the model and A's key into its own credential store with the choice recorded
and its NVIDIA key set aside, not deleted; the same in the other order on a
third host; a model change (B reuses its key, no new credential); a new key;
offline edits of different settings on both (both kept) and of the same
setting (the later edit wins); a host that was down while a change was made,
restarted with its saved copy and got the change on the next sync; a stale copy
that can't undo newer changes; a newer Martlet's setting passed through; a
Windows voice a new computer lacks (it waits, records nothing, then follows); a
new computer taking everything without its defaults overriding anything; the
Thinking fallback with its own key, turned off again (the key removed); lorebooks;
no key in any desktop file while the hosts' private copy holds them and every
copy ends the same; and an unsigned request refused (HTTP 401). Loopback only;
the folder is deleted and the real vault is never touched.

`network_status` reads the [Martlet network](NETWORK.md) from a data directory
(optional absolute `dataDirectory`, default the current user's): `state`
(`none`, `member`, `waiting` or `unreadable`), `key` (whether
`network\device_ecdsa` exists), `networkId`, the roster's `revision` and
`founder`, `waiting` (`hostId`, `checkNumber`, `since`) while this PC asks to
join, each `desktops` and `hosts` entry (`id`, `name`, `removed`, `updatedBy`,
`changedAt`; hosts also `outsideAddresses`, how many outside addresses the roster
lists), `adopt` (hosts paired here on purpose, added to the network on
the next sync), `ignored` (network hosts forgotten here) and `removedFrom`;
`pairedHosts`, each pairing in `hosts.json` with how many outside addresses are
kept with it and its `access` (`member` for your own hosts, `friend` for a host
a friend shares with this PC: its engines only, never in this PC's network);
and `friends`, what **Devices › Friends** last read from each of your hosts
(`friends.json`: `checkedAt`, and per host `hostId`, `read`, `problem`, each
friend's `label`, `provider` and `computers`, and how many are `asking`), or
null before it read anything. It never returns keys, signatures or host
addresses and contacts nothing.

`network_selftest` (no arguments) rehearses the network end to end with the
production code: three real gateways (`lab-host-1..3`: Kestrel, pinned TLS,
volatile credentials, a throwaway certificate) on `127.0.0.1`, simulated
desktops driving the desktop's own client (`HostNetwork.cs`) and sync engine
(`NetworkSync.cs`), and a simulated host PC. Like `node_link_check` it runs `src\Martlet.NodeLinkCheck`
(mode `network`, `NetworkRehearsal.cs`) as its own process, because the gateway
needs the ASP.NET Core runtime, and returns `{exitCode, report}`. Its steps: desktop A pairs with a host by a typed code and
founds a network that binds it; A adds a second host to the same network; B
pairs with one host and asks to join with a check number; A sees the same
number; the host tells B (not yet a member) who is paired with it, A and B,
each with when it last made a signed request; A allows B, and B pairs with the
other host by itself; A, treating the second host as its own host service,
lets in D (paired with that host) with no second Allow
(`NetworkSyncEngine.ApproveThrough`) while E, asking through the first host,
still waits for one; every host announces the Martlet release it runs to A, B
and B reading only (the gateway's own release), and a desktop that last saw a
host on 0.0.1 (simulated) takes the announced release as an update that needs
nothing more, while a host still older than the desktop keeps needing one
(`HostRelease`); a host PC C outside the network pairs with the first host
and only watches (`NetworkSyncEngine.ReadOnlyAsync`): it sees A, B and itself
and starts, joins and asks nothing; A sets up a third
host and B is paired with it on its next sync; a key outside the network
(`network.denied`), a member's ID with the wrong key (`pairing.invalid`) and a
roster entry not signed by a member are refused; A removes a host (it stops
trusting the network's desktops, and A and B both forget it, B although the
host no longer answers it); A pairs that host again by a code and it rejoins
(B pairs with it again by itself); on a fourth host (`lab-host-4`) a typed code
still pairs 12 hours later on that host's clock (codes have no deadline), stops
working once used, and five wrong tries close another code so even the right
one gets `pairing.closed`; A removes B (every host revokes it, B leaves
and forgets its hosts and needs a new key). Each desktop's state goes through
`network.json`'s format between syncs. The report has `ok`, `passed`,
`total`, `seconds`, the `scope` and each step's `ok` and `detail`. Nothing
leaves loopback, nothing is written to disk or Windows Credential Manager, and
it does not cover the desktop window, `network.json` on a Linux host,
`martlet-host`, SSH or a real LAN.

`api_keys_status` reads the [API keys](API.md) of this PC's Martlet network
from a data directory (optional absolute `dataDirectory`, default the current
user's; the script supplies its disposable one): `state` (`none`, `loaded` or
`unreadable`), counts of `live`, `revoked` and `expired` keys, and each key's
`id`, `name`, `scopes`, `createdBy`, `createdAt`, `expiresAt`, `revoked`,
`expired`, `updatedBy` and `hasVerifier`. It never returns a key or its
verifier (Martlet keeps no key) and contacts nothing.

`mac_host_check` (no arguments) checks the Mac host ([macOS](MACOS.md#host)) as
far as a Windows PC can. In the `mcr.microsoft.com/dotnet/sdk:10.0.401` image
(never pulled; NuGet packages cached in `martlet-outside-check-nuget`) it
publishes this checkout's `Martlet.Gateway.Host.Linux` self-contained for
`osx-arm64` and `osx-x64` (the files the Mac app bundles), runs
`macos-setup help` and checks `macos-status` refuses with exit 4
(`macos.unsupported`) off a Mac. In-process it checks the platform catalog for a
Mac host's machine report: Ollama and whisper allowed, F5 and Audio2Face refused
for want of an NVIDIA GPU, roles not managed from the desktop. It returns
`{passed, exitCode, steps, notRun}`; steps `catalog-mac-host`,
`publish-osx-arm64`, `publish-osx-x64`, `macos-setup-help`,
`macos-commands-refused-off-a-mac`. Running on a Mac (launchd, APFS custody,
Metal, native Ollama and whisper.cpp) is always reported in `notRun`.

`outside_path_check` (no arguments) checks reaching a host from outside home on
real sockets ([NETWORK](NETWORK.md#reaching-your-network-from-outside-home)). It
builds this checkout's `Martlet.Gateway.Host.Linux` in the
`mcr.microsoft.com/dotnet/sdk:10.0.401` image (NuGet packages cached in the
`martlet-outside-check-nuget` volume) and runs it as uid 1000 in disposable
`mcr.microsoft.com/dotnet/aspnet:10.0.12` containers with `host.json` and its
state on an ext4 named volume. The containers are on a Docker network numbered from TEST-NET-3
(`203.0.113.0/24`) with the gateway port published on `127.0.0.1` only, so the
gateway sees every connection coming from `203.0.113.1`: a real outside source.
The host's home origin, `https://192.168.77.20:9443`, answers nothing. It returns
`{exitCode, report: {passed, total, network, outsideSource, home, published,
steps}, notCovered}`. Steps: `gateway-built`; `config-valid`; `identity-created`
(`owner-init`); `owner-exposure` (an address with a shell character is refused
with exit 2, the published address is saved); `typed-code-refused-from-outside`
(`owner-pair` shows a code, the desktop's code pairing falls back to the outside
address and gets `pair.outside_home`, the code stays open until `cancel`);
`card-pairs-from-outside` (`owner-pair --device-id` and the desktop's real
pairing); `serve-healthy`; `roster-signs-outside-address` (two network syncs
found the network and sign in the address the host advertises);
`exposure-needs-signin-first` (before sign-in `owner-exposure --outside` exits 5
with `outside.needs_signin`; `owner-signin-owner` then sets an owner account
with an authenticator from the code the check computes);
`home-fails-outside-succeeds` (with no outside address the home address times
out; with the roster's, the paired connection reaches the host outside, and the
next connection goes straight there); `guard-locks-out-outside-source` (five
`401 auth.missing`, then `429 auth.throttled` with `Retry-After`);
`audit-names-outside-source` (`internet_reachable` from the roster, failures
from `203.0.113.1 (outside)`, the host log's lockout line);
`outside-paused-without-signin` (`signin.json` removed from the running host: the
paired desktop and a stranger get `outside.paused`, sign-in's settings still
answer the owner with `signin.not_set_up`); and
`probe-tells-home-and-outside-apart`. It never pulls (without Docker or the images
it returns `exitCode` 2 and `notRun`) and removes its containers, volumes and
network. Not covered: a real router, overlay or internet path.

`outside_reachability_check` checks how each host in a data directory's
network can be reached (optional absolute `dataDirectory`; `contactHosts`).
Without `contactHosts: true` it only lists each host's `id` and how many
`outsideAddresses` it has (`checkedNow: false`) and contacts nothing. With it,
it dials each host's home address and each outside address directly, checks the
TLS key against the roster's pin and asks `GET /health/live` (no credential):
per host `home` and `outside` (by `address` number) with `reachable`, `ms` and
`problem` (`refused`, `no answer in time`, `name not found`, `another key`,
or `answered 429 ...` when the key answered but the guard throttled the
address), and `wouldUse` (`home`, `outside <n>` or `none`, home first as the
desktop dials). It never returns the addresses themselves.

`exposure_selftest` (no arguments) rehearses a host reachable from outside
home ([NETWORK](NETWORK.md#reaching-your-network-from-outside-home)) with the
production code: one real gateway (`lab-exposure`, Kestrel, pinned TLS, a
throwaway certificate) on `127.0.0.1`, a desktop that pairs while at home
through its paired client, then the host told to treat every connection as
outside home, and a stranger's pinned HTTPS client. It runs
`src\Martlet.NodeLinkCheck` (mode `exposure`, `ExposureRehearsal.cs`) and returns
`{exitCode, report}` like `network_selftest`. Its steps: a typed pairing code
used from outside is refused (`pair.outside_home`, also as the desktop's code
pairing sees it) and stays open; a one-use card opened for one named device
pairs from outside without the opt-in; once the owner allows typed codes from
outside without sign-in, outside access pauses (the code and the desktop's audit
read get `outside.paused`, `OutsideAccessBlockedReason` is `signin.not_set_up`),
the desktop sets up an owner account over the sign-in settings route (still
reachable) and the pause ends; then the same code pairs; five failed requests lock the
address out (`auth.throttled`, `Retry-After` 1 s) and the next failure doubles
it, after which the paired desktop's signed requests work again; liveness
answers 120 requests a minute per outside address; and the paired desktop reads
the security audit (`ReadSecurityAuditAsync`: refused, success, failure and
throttled entries with their source) and the host log lines naming each source.
Then outside addresses, played by other loopback ports: with the home address
closed the desktop reaches the host at its outside address, pinned to the same
key, and the next connection tries it first; with the home address answering it
is used at once (no wait on an outside one); with nothing answering the error
names every address tried (`host.unreachable`); outside addresses set on the
host itself are signed into the roster by a member desktop's network sync; the
reachability probe behind `outside_reachability_check` tells an answering, a
closed and a wrong-key address apart; and a
different computer with another key at the home address is skipped for the
outside address. Not covered: a real internet source, a router port forward or
an overlay.

`signin_selftest` (no arguments) rehearses [joining from outside home by
signing in](NETWORK.md#joining-from-outside-home-by-signing-in) with the
production code: one real gateway (`lab-signin-host`, Kestrel, pinned TLS, a
throwaway certificate, in-memory `signin.json` and `network.json`) on
`127.0.0.1`, a home PC, a laptop and another PC simulated with the desktop's
sign-in client (`HostSignIn.cs`) and network sync engine. It runs
`src\Martlet.NodeLinkCheck` (mode `signin`, `SignInRehearsal.cs`) and returns
`{exitCode, report}` like `network_selftest`. Its steps: the home PC pairs by
code and founds the network; it sets up the owner account (a wrong
authenticator code is refused, the right one gives ten recovery codes, the
password is kept only as a verifier); a computer outside the network can't
change sign-in (`signin.denied`); the laptop pins the host from an invite whose
outside address is `localhost:<port>` (the certificate names `127.0.0.1`, so
only the pin is trusted) and a forged pin reaches nothing; a wrong password and
the reused setup code are refused (`signin.invalid`); the laptop signs in with a
recovery code, is paired under the host's home origin and its signed requests
work; it asks to join and the home PC lets it in on the host's attestation
(`NetworkSyncEngine.ApproveSignedIn`) with no check number, while a PC paired by
code still waits for one; the home PC adds an OpenID Connect provider (its
client secret kept on the host, `has_client_secret` only) routed to an issuer in
the same process (`GatewayServer.UseSignInProviderHandler`); a tablet signs in
through a simulated browser that follows the redirect to the desktop's real
loopback listener (`LoopbackRedirect`), is refused (`signin.not_allowed`) until
the home PC allows the identity listed under `refused`, then is paired (the
host exchanged the code with the client secret and checked the ID token) and
let into the network the same way; a Steam account allowed by its SteamID64
signs in through the simulated browser with an OpenID 2.0 assertion the host
confirms with (simulated) Steam; the home PC allows the same OpenID Connect
identity as a friend (`access` `friend`, [sharing a host with
friends](NETWORK.md#sharing-a-host-with-friends)) and the desktop's client reads
it back as a friend's: FRIEND-PC signs in through the simulated browser, its
sign-in answer says `friend`, the host keeps a friend's credential for it and
it lists the engines, and the home PC reads it as a friend's computer in the
sign-in settings and in the host's network answer (`devices[].access`), while 15 other routes (network, hardware, who does what,
settings, memories, voices, speaking voices, characters, creations, Home
Assistant, API keys, commands, GPU priority, security audit, sign-in settings)
refuse it with `access.friend` and it never asks to join; a sign-in under
HOME-PC's ID is refused (`signin.device_taken`) while HOME-PC keeps its
pairing; stopping sharing revokes FRIEND-PC at once (`auth.revoked`) and records
no network removal; removing the owner account revokes the
laptop (`auth.revoked`); the host's security audit holds the sign-in successes
and failures and no secret. Not covered: the desktop windows, Windows
Credential Manager, a host reached over the internet, a real browser and a real
issuer.

`signin_lab` (`action` `start`, `status` or `stop`; `mode` `owner`, the
default, or `friend` with `start`; `dataDirectory` filled in by the script)
runs a live sign-in lab for the desktop on a disposable data directory, so
**Sign-in from outside**, **Devices › Friends** and **Join with an invite** can
be driven against a real host. Mode `owner`: `src\Martlet.NodeLinkCheck` mode
`signin-lab` (`SignInLab.cs`) starts a gateway on 127.0.0.1 with an owner
account, an OpenID Connect provider (an issuer in that process), an allowed
identity and an outside address set on the host; it pairs the desktop of the
data directory (`hosts.json` there, the secret in the lab credential folder), a
simulated laptop signs in and keeps syncing, and a simulated friend's computer
(`lab-friend-pc`, identity `ana@example.net`, `authentik` subject
`lab-friend-7`) signs in once and is refused, so it waits under *Signed in but
not allowed yet* and in **Devices › Friends**; once the owner shares the host
with it (as a friend) it signs in again. `status` returns what the lab sees
(`network`, `laptopMember`, `laptopWasMember`, `laptopRemoved`,
`laptopCanUseHost`, the laptop's network events, and `friend`: `allowedAs`,
`state`, `access`, `hostKeepsAs`, `canUseEngines`, `failure`,
`refusedElsewhere` (routes that answered `access.friend`), `otherwiseAnswered`
and `inRoster`). Mode `friend` (`SignInLab.RunFriendAsync`): the desktop of the
data directory is the friend. The gateway (`lab-shared-host`) belongs to a
simulated owner, `lab-owner`, who started their own network with it, set up the
provider and allowed `lab-user-42` (`me@example.net`) as a friend; it runs a
fixture Ollama Thinking route (`fixture-model:1b`, canned text, NOT AI). The
start answer and `status` carry the `invite` to paste in **Join with an
invite**; the lab's simulated browser answers the desktop's sign-in (the desktop
writes the sign-in page's address to `signin-browser.url` in the
`MARTLET_LAB_BROWSER` folder, only in a lab run). `status` returns
`desktopSignedInAsFriend`, `friendComputers`, `ownersRoster` (the owner's
network members: never the friend), `askedToJoin`, `refusals` and
`accessFriendRefusals` (what the host refused, which stays 0 in normal use) and
`thinkingRequests` (chat requests that reached the fixture). For a locked or
headless session, where the window can't be driven: `signInDesktop` `true`
(with `mode` `friend`) has the lab sign the data directory's desktop in as the
friend itself, under the device ID that desktop names itself by, and keep the
pairing as **Join with an invite** does (`hosts.json` with `access` `friend`),
so a desktop started on that data directory afterwards runs with a shared host;
`shareWithFriend` `true` (with `mode` `owner`) has the lab's own admin desktop
share the host with the simulated friend from the start, so the friend's
computer is in the host's network answer from the desktop's first sync. It needs
`Invoke-MartletMcp.ps1 -LabCredentials`, which points `MARTLET_LAB_CREDENTIALS`
of the desktop and MCP server at a `lab-credentials` folder in the data
directory, so pairing secrets go there (plaintext, thrown away with the folder)
instead of Windows Credential Manager, and `MARTLET_LAB_BROWSER` at a
`lab-browser` folder there. Example (owner): start the lab, click `NavHome`
(the desktop reads `hosts.json` again), poll `network_status` until
`"state":"member"`, `signin_lab status` until `"laptopMember":true`, open Add a
computer › `HostsStepRoles` › `HostSignInSettings`, type `authentik` /
`lab-user-42`, click `SignInDisallow`, then poll `network_status` until the
laptop is `"removed":true` and `signin_lab status` until
`"laptopRemoved":true`. Sharing with the friend: click `NavDevices`, then
`FriendsCheck`, and poll `FriendsStatus` until *1 person asked*; click
`FriendShare-lab-signin-host-authentik-lab-friend-7` and `ConfirmationYes`
(with `-AllowUiEffects`), then poll `signin_lab status` until
`"canUseEngines":true` and `network_status` until `friends` lists
`ana@example.net`. Example (friend): start with `mode` `friend`, open Add a
computer › `HostsJoinWithInvite`, `ui_set_text` `SignInInvite` with the
`invite`, click `SignInConnect`, `SignInProvider-authentik` and `SignInSubmit`,
poll `SignInJoinStatus` until *shared with this PC by a friend*, close the
windows, then poll `network_status` until `pairedHosts` shows `"access":"friend"`
and `SharedHostsStatus` until *1 host a friend shares with this PC*, click
`SharedHostUse-lab-shared-host-thinking` and `ConfirmationYes`, and check
`signin_lab status`: `"askedToJoin":false` and `"accessFriendRefusals":0`.

`role_lab` (`action` `start`, `status`, `ask` or `stop`; `dataDirectory` filled
in by the script) runs a live lab for [switching your computers between
companion and host PC](CLUSTER.md#switching-another-computer-between-companion-and-host)
from both sides, for the desktop on a disposable data directory:
`src\Martlet.NodeLinkCheck` mode `role-lab` (`RoleLab.cs`) starts a gateway on
127.0.0.1 that keeps the network and the shared settings in memory, pairs the
desktop of the data directory under the device ID that desktop names itself by
(`desktop-<computer>`; `hosts.json` there, the secret in the lab credential
folder, so it also needs `Invoke-MartletMcp.ps1 -LabCredentials`), and runs a
simulated companion PC, `lab-companion` (*LAB-COMPANION*). Once the desktop has
bound the host to its network, the simulated PC asks to join it. Every
2 seconds it syncs the shared settings with the real sync engine as a desktop
does: it says what it is (`pc.lab-companion`) and follows an ask that it switch
(`role.lab-companion`). `ask` with `role` `host` or `companion` makes it ask the
desktop to switch (`role.<desktop device>`). `status` returns `companionRole`,
`companionMember`, `companionWaiting` (its check number while it waits to be
let in), what the host's copy says each computer is (`desktopSays`,
`companionSays`) and the newest ask about each (`desktopAsk`, `companionAsk`:
value, by, at), and its events. The values are JSON text, so an `until` that
looks inside them has to match its escaped form. Example (with `-Desktop
-LabCredentials -AllowUiEffects`): click `TourSkip`, start the lab, click
`NavDevices` and `RefreshDevices` (the desktop reads `hosts.json` again), poll
`network_status` until `"state":"member"` and `role_lab status` until
`"companionWaiting":"`, click `NetworkCheck`, `NetworkAllow-lab-companion` and
`ConfirmationYes`, poll until `"companionMember":true`; then click
`Node-pc:lab-companion`, `NodeAction-MakeHostPc` and `ConfirmationYes` and poll
`role_lab status` until `"companionRole":"host"` and `logs_tail` until *LAB-COMPANION
is a host PC now, as you asked*; `role_lab ask host` and poll `DeviceRoleSummary`
until *Host PC*.

Sign-in from outside in the desktop: Add a computer's **Join with an invite**
(`HostsJoinWithInvite`) opens `SignInJoinWindow` (invite `SignInInvite`,
`SignInConnect`, provider choices `SignInProvider-<id>`, `SignInUser`,
`SignInPassword`, `SignInCode`, `SignInSubmit`, status `SignInJoinStatus`, the
checked host `SignInHost`, `SignInJoinClose`); a paired host's **Sign-in from
outside** (`HostSignInSettings`) opens `SignInSettingsWindow` (status
`SignInSettingsStatus`, owner state `SignInOwnerState`, `SignInOwnerUser`,
`SignInOwnerPassword`, `SignInTotpNew`, `SignInTotpSecret`, `SignInTotpLink`,
`SignInOwnerCode`, `SignInOwnerSave`, `SignInRecoveryNew`, `SignInOwnerRemove`,
`SignInRecoveryCodes`, `SignInAllowedList`, `SignInProvidersList`,
`SignInProviderKind`, `SignInProviderId`, `SignInProviderName`,
`SignInProviderIssuer`, `SignInProviderClientId`, `SignInProviderSecret`,
`SignInProviderScopes`, `SignInProviderPort`, `SignInProviderSave`,
`SignInProviderRemove`, `SignInRefusedList`, `SignInRefusedAllow` (allow the
newest as one of your computers), `SignInRefusedAllowFriend` (as a friend),
`SignInRemovedList` (computers your member PCs still have to remove from the
network), `SignInOutsideWarning` (outside access paused or about to be),
`SignInAllowProvider`, `SignInAllowSubject`, `SignInAllowLabel`,
`SignInAllowAccess` (with `SignInAllowAccess-member` and
`SignInAllowAccess-friend`), `SignInAllow`, `SignInDisallow`, each allowed
identity's row `SignInAllowed-<provider>-<subject>` (one of your computers or a
friend's) with `SignInAccess-<provider>-<subject>` (**Make a friend** or **Make
one of my computers**) and `SignInRemove-<provider>-<subject>`,
`SignInEnrolledList` (each computer says whether it is a friend's),
`SignInInviteAddress`, `SignInInviteMake`, `SignInInviteText`,
`SignInInviteCopy`, `SignInSettingsClose`); identities in IDs keep letters,
digits, dots and dashes, anything else becomes `_`. Opening and closing both
windows are safe clicks; the status lines and lists are safe values. Everything
else contacts a host or changes it and needs `--allow-ui-effects`.

Sharing hosts with friends on the Devices page (`MainWindow.Friends.cs`):
**Friends** (`FriendsCard`, shown with paired hosts; status
`FriendsStatus`, `FriendsCheck` reads each of your hosts' sign-in settings and
is a safe click) lists each person as `Friend-<provider>-<subject>` (*name
(provider). Shares gpu-box: their engines only. Their computers: ...*, or that
they signed in to a host and wait) with `FriendShare-<host>-<provider>-<subject>`
and `FriendStop-<host>-<provider>-<subject>` (both ask first and change the
host, so they need `--allow-ui-effects`), and `FriendsHost-<host>` for a host
it couldn't read. **Hosts shared with this PC** (`SharedHostsCard`, shown when
a friend shares a host with this PC; status `SharedHostsStatus`; it reads what
they offer by itself when the Devices page shows) lists each as
`SharedHost-<host>` (who this PC signed in as, what it offers this PC, what
this PC uses it for, or that its owner stopped sharing it) with
`SharedHostsCheck` and `SharedHostCheck-<host>` (read again and, like **Check
all hosts**, follow a model the owner changed for a job of this PC there),
`SharedHostUse-<host>-<job>` (`thinking`, `listening`, `speaking`, `lip-sync`,
`reading`) and `SharedHostForget-<host>`; all of them need
`--allow-ui-effects`. In *Your Martlet network* a friend's computer
is `NetworkFriend-<device ID>` (*a friend's computer: it signed in to gpu-box as
a friend and uses only that host's engines*), never `NetworkPaired-`. The job
choices (`ThinkingOwner`, `ListeningOwner`, `SpeakingOwner`, `LipSyncOwner`)
and Companion's computer cards (`HostChoice-<job>-<host>`,
`SetupUseHost-<job>-<host>`) list a shared host as *shared by a friend*.

`api_selftest` (no arguments) rehearses API keys for software outside the
network end to end with the production code: two real gateways
(`lab-api-1`, `lab-api-2`: Kestrel, pinned TLS, a throwaway certificate) on
`127.0.0.1`, each with the real Ollama relay route over a fixture Ollama
(canned text, NOT AI), a simulated desktop using its paired client
(`HostApiKeys.cs`), and a plain HTTPS client that pins the host key and sends
`Authorization: Bearer`. It runs `src\Martlet.NodeLinkCheck` (mode `api`,
`ApiRehearsal.cs`) and returns `{exitCode, report}` like `network_selftest`.
Its steps: the desktop pairs with both hosts and creates a read+voice and a
manage key, and both hosts keep verifiers and no secret; no key
(`auth.missing`), a malformed and a wrong key (`key.invalid`) are refused; the
read+voice key reads version (naming the key), status and capabilities, the
documented `curl -k --pinnedpubkey` request (System32's curl.exe) answers and a
wrong pin is refused (exit 90), and the key chats through the native route (and
still gets its reply when the model's Gemma 4 speculative-decoding draft fails
to load: the relay saves `draft_num_predict 0` on the model with `/api/create`
and retries); it gets `key.scope` for commands, voices,
network, api-keys, posting the plan and posting logs; the manage key sends
`host.status` and follows it but can't chat or read status; the same key works
on the second host and after it restarts from its saved copy; the desktop sees
when keys were last used; revoking a key cuts its streaming reply and every
host refuses it after sync; a stale copy can't bring it back; an expired key
gets `key.expired`. Nothing leaves loopback and nothing is written to disk or
Windows Credential Manager; it does not cover the desktop window,
`api-keys.json` on a Linux host, a real model or a real LAN.

`deep_thinking_role_selftest` (no arguments) rehearses the Deep thinking host
role (`deep-thinking`, deploy/host/roles/deep-thinking) end to end with the
production code: one real gateway (`lab-deep`: Kestrel, pinned TLS, a throwaway
certificate) on `127.0.0.1` that publishes both of a host's Ollama roles the way
`martlet-host` does, Thinking's `martlet.gateway.ollama-chat.v1` (the real
`OllamaRelayWorker` for `gemma4:e4b`) and Deep thinking's own
`martlet.gateway.deep-thinking-chat.v1` (`OllamaRelayWorker.DeepThinking` for
`qwen3:8b`), each over its own fixture Ollama (canned text, NOT AI) and placed on
its own graphics card (so live turn first never stops the think), and a
simulated desktop that pairs and streams through the desktop's paired client
(`HostChat.cs`). It runs `src\Martlet.NodeLinkCheck` (mode `deep-thinking`,
`DeepThinkingRehearsal.cs`) and returns `{exitCode, report}` like
`network_selftest`. Its steps: the host advertises both routes with their own
paths and models and the same contract and fifteen-minute bound; Thinking's
advertised route saves as this PC's Thinking route through the desktop's own
handoff (`HostHandoff.ToHost` with `HostRoute.Snapshot`), so its long-think
bound fits the saved settings; a think on the
Deep thinking route is held mid-answer while a reply streams on Thinking's
route, and the reply finishes first (parallel, never queued); each request
reached its own Ollama (the think with `"think":true`, its own model and a
32,768-token context, the reply without Thinking steps); a Thinking pool job's
request (remembering: a budget of 25,600 tokens, the role's largest 32,768-token
window and Thinking steps off) is accepted and loads that window (Martlet 0.54.0
sent the budget as `maximum_context_tokens` below `context_tokens`, and the
gateway refused it with `request.invalid`); two thinks run at once
on the Deep thinking role's two slots (`OllamaRelayWorker.DeepThinking` with
`slots: 2`, advertised as the route's `maximum_concurrency` and read as
`HostRoute.MaximumConcurrency`) while a reply streams, a third is turned away
with `job.busy` and each finishes once released; and the chat client
refuses a route whose ID and path don't match. Nothing leaves loopback and
nothing is written to disk or Windows Credential Manager; it does not cover
`martlet-host` installing the role, a real Ollama or model (the slots' graphics
memory), a GPU or a real LAN.

`gpu_priority_selftest` (no arguments) rehearses GPU priority (live turn first,
[Gateway README](../src/Martlet.Gateway/README.md#gpu-priority-live-turn-first))
end to end with the production code: real gateways on `127.0.0.1` (Kestrel,
pinned TLS) with Thinking's Ollama relay (lane `live`) and the Deep thinking
role's (lane `pool`) placed on graphics cards the way `martlet-host` places them
(`gpus.json`), each over its own fixture Ollama server on loopback (canned text,
NOT AI; no GPU is used), a simulated desktop with the desktop's paired client and
its hold client (`HostLiveGpuHold`, the desktop's `ILiveGpuHold`). It runs
`src\Martlet.NodeLinkCheck` (mode `gpu-priority`, `GpuPriorityRehearsal.cs`) and
returns `{exitCode, report}`. Its steps: the host's GPU map (both routes on one
card, the warning to pin each Ollama server to its own GPU); a live reply stops a
running think on the same card at once (`job.preempted`, the fixture sees the
Ollama request aborted); while a reply runs, a new think there is turned away
(`job.busy`, detail `live`) and never reaches Ollama; the hold client keeps the
card (a think is turned away, renewing keeps one hold, release frees it); a 1 s
hold ends on its own; a hold stops a running think and replies never wait for
it; a think on its own card runs beside replies and holds; and an older host
without holds never breaks the live turn (the client notes it once). The
`report.priority` field is the host's `GET /martlet/v1/priority`: each route's
lane and cards, each card's hold state (`held`, `live`, `pool`, `holds`),
`whole_host_held`, the holds, the `preempted` and `refused` counts, the last
preemptions and refusals (with what held the card) and the warnings. It does not
cover a real GPU, a real Ollama stopping mid-token, a host with two or more
cards or when the desktop's live turn holds a card.

```powershell
.\scripts\Invoke-MartletMcp.ps1 -Build -Calls '[{"name":"gpu_priority_selftest"}]'
```

`gpu_priority_status` (`dataDirectory`) reads GPU priority on every host paired in
a desktop data directory (`hosts.json`), through each host's own gateway with the
desktop's pairing (pinned TLS; the secret from Windows Credential Manager only
signs the request and is never returned): the host's `GET /martlet/v1/priority`
report as above, or, for a host older than GPU priority, `gpuPriority: false` with
the advice to update it, or `reachable: false` with the problem. With no
`hosts.json` it contacts nothing. Read-only: it takes no hold and starts no work.
`Invoke-MartletMcp.ps1` gives it the disposable data directory unless one is
named.

`speaking_voices_selftest` (no arguments) rehearses the
[shared speaking voices](CLUSTER.md#the-shared-speaking-voices) end to end with
the production code: two real gateways on 127.0.0.1 (pinned TLS, the real
reference-voice relay routes of F5-TTS and XTTS-v2 over fixture voice services, NOT AI, and in-memory
`speaking-voices.json` and recordings) and two simulated desktops that keep real
F5 voice stores in a temporary folder and use the desktop's paired client and
Martlet.F5's reconcile engine. It runs `src\Martlet.NodeLinkCheck` (mode
`voices`, `VoiceRehearsal.cs`) and returns `{exitCode, report}` like
`network_selftest`. Its steps: a new list starts with the starter voices
(revision 1) and an own recording joins it, with every recording in the store;
the list and every recording reach a host; speaking there names the recording
by SHA-256 alone and the engine gets the exact recording; a host that has the
list but not the recording gets it once (`reference.missing`, then kept) and the
next reply names it; a new, empty desktop takes every voice from a host (starter
recordings from Martlet, the own one downloaded); a choice made on one desktop
reaches the other; removing a starter voice deletes its recording on both hosts
and the other desktop's copy; a stale copy can't bring it back; a list from an
older Martlet that still lists the retired "anime" voices loses them in the
next reconcile without waiting for their recordings, the host follows and a
stale copy can't bring them back; a list from before a new starter voice
(Jenny) gets it in the next reconcile, once, with its recording from Martlet,
while a starter the owner removed stays removed; speaking with a
removed voice sends the recording, which the host doesn't keep; a host restart
keeps the list and recordings; a wrong SHA-256, a recording no voice has and a
listed recording that isn't a WAV are refused; reading a missing recording
answers none; one voice made from three recordings (24, 16 and 24 kHz) is joined
at 24 kHz after 0.5 s pauses with each recording's place kept, reaches the other
desktop through a host with those places, and speaking with it hands the XTTS-v2
relay's service the three places (`reference.clips`) and F5-TTS the joined
recording only; a list entry whose recordings don't match its recording or
transcript is refused; a voice service that fails a reply (out of graphics
memory, a model that failed to load and answers 503 with its state and why, a
service that stops mid-reply) gives the desktop `worker.failed` or
`worker.unavailable` and the host's own log, read by the desktop as the
Diagnostics page does, says why (the service's error code, stage and summary,
its state and detail, or that its stream ended unfinished); and a reply with a
pause (full-size frames of near-silent audio, whose base64 is full of `+`) is
spoken whole and the voice keeps working afterwards: the host writes base64
unescaped, so ordinary audio never exceeds the route's 16 KiB event limit
(`stream.limit`), which would quarantine the voice until the host restarts; and
when the host's clock steps back 1.3 s (as Windows' time sync does, which a
WSL2/Docker host follows) the voice keeps working with no `auth.clock_invalid`
in the host's log (the host holds its time through a step of up to 30 s), while
a 45 s step still closes that host's authority (`auth.clock_invalid`, also after
the clock is corrected, until the host service restarts). Nothing leaves loopback, the temporary folder is deleted and
Windows Credential Manager is not touched; it does not cover the desktop window
and its sync, the Linux host's files, a real engine, an older host or a real LAN.

`character_models_selftest` (no arguments) rehearses the
[shared character models](CLUSTER.md#the-shared-character-models) end to end
with the production code: two real gateways on 127.0.0.1 (pinned TLS, in-memory
`character-models.json` and pieces) and three simulated desktops that keep their
character copies in a temporary folder and use the desktop's paired client and
Martlet.Avatar.Hosting's import and reconcile engine. The fixtures are generated
bytes in the Live2D folder and VRM file shapes (NOT real models; nothing is
rendered). It runs `src\Martlet.NodeLinkCheck` (mode `characters`,
`CharacterRehearsal.cs`) and returns `{exitCode, report}` like
`network_selftest`. Its steps: adding a Live2D model (its model3.json declares a
7 MiB file in three pieces, a texture and a motion in subfolders; a readme beside
it stays behind) and a VRM keeps copies the
renderer's own file reader reads exactly like the originals; the list and every
3 MiB piece reach a host, each piece in one signed request; a new, empty desktop
copies both characters byte for byte; a copy interrupted after three pieces
continues with only the rest; a desktop passes the characters on to a host the
first desktop never reached; removing a character deletes its pieces on both
hosts and the other desktops' copies; a stale copy can't bring it back; a
computer keeps the copy it shows until another is chosen; a host restart keeps
the list and pieces; a wrong SHA-256, a piece no character has and a removed
character's piece are refused; reading a missing piece answers none; a 17th
character and a Live2D model that refers to a script are refused. Nothing leaves
loopback, the temporary folder is deleted and Windows Credential Manager is not
touched; it does not cover the desktop window and its 30-second sync, the Linux
host's files, rendering a copied model or a real LAN.

`character_models` reads the shared character models from a data directory
(optional absolute `dataDirectory`, default the current user's): `state`
(`none`, `loaded` or `unreadable` for `character-models.json`), `live`,
`tombstones`, `totalBytes`, `revision`, and per live character its `key` (the
first 16 hex digits of its ID, as in `CharacterModelState-<key>`), `renderer`
(`live2d` or `vrm`), `files`, `pieces`, `bytes`, `addedBy`, `addedAt`,
`updatedBy`, `ready` (this PC's copy is complete) and `shown` (`avatar.json`
shows it); `copies` (folders in `character-models`), `incoming` (copies still
arriving: key and pieces so far) and `showing` (`built-in`, `shared:<key>`,
`unlisted-copy:<key>` for a copy removed elsewhere that this PC still shows, or
`model-file-outside-list`). Character names and file paths are never returned.
Read-only; it contacts nothing.

`character_profiles` reads the character profiles (Companion › Profiles) from a
data directory (optional absolute `dataDirectory`, default the current user's):
`state` (`no-settings`, `none` or `loaded`), `count`, `lastUsed` (the key of the
profile switched to last), `current` (the key of the profile that matches what
Martlet uses now: the active persona, the look in `avatar.json` and the voice the
speaking route keeps or the shared voice list chose; null when none does),
`look` (what this PC shows: `builtin`, `shared:<key>` or
`model-file-outside-list`), `voiceChosen`, and per profile its `key` (the first 8
hex digits of its ID, as in `CharacterProfileState-<key>`), `personaSaved`,
`personaActive`, `look` (`keep`, `builtin`, `ready`, `copying` or `missing`),
`voice` (`keep`, `listed` or `missing`) and `inUse`. It also reads what each
profile keeps on this PC (`character-profiles-local.json`, never shared):
`hereState` (`none`, `loaded` or `unreadable`), `hereInUse` (the key of the
profile whose choices this PC uses now: the one switched to here or followed
after a switch on another computer), and per profile `here` (null when it keeps
nothing here yet) with `place` (`locked`, `left`, `top`, `width`, `height` in
device-independent pixels and `screen`, or null), `gaze` (`personality`,
`mouse`, `near`, `ahead` or `window`), `gazeFree` and `touchInterrupts` (`any`,
`intimate` or `never`). Names are never returned. Read-only; it contacts
nothing.

`creations_status` reads [Martlet's creations](CREATIONS.md) from a data
directory (optional absolute `dataDirectory`; the script gives a disposable one):
`state` (`none`, `loaded` or `unreadable` for `creations.json`), `live`,
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
too large); and the per-host record in `creations-sync.json`. With
`seedDataDirectory` (an absolute folder under the temporary folder, never
Martlet's own) it instead writes two FIXTURE - NOT AI test tones there and
returns their keys, so the Creations page can be checked with `-Desktop
-DataDirectory` on that folder. Nothing leaves loopback, folders are deleted and
Windows Credential Manager is not touched; it does not cover the desktop window's
30-second sync with real hosts, the Linux host's files, a real song or a real LAN.

`character_actions` reads a character model's
[emotes and motions](AVATARS.md#emotes-and-motions) the way Companion ›
Character › Emotes and motions uses them: `modelPath` (a `.model3.json` or
`.vrm` on this PC) or the model `dataDirectory`'s `avatar.json` shows (with a
`dataDirectory`, its `character-actions.json` and edited prompts are used too).
It returns `renderer`, `key` (first 16 hex digits of the model's ID), `files`
(what the renderer reads, a VTube Studio model's `.vtube.json` and loose
`.exp3.json`/`.motion3.json` included), `expressions`, `motions`,
`fromVTubeStudio` (expressions and motion groups taken from outside the
model3.json, with their model-relative file names, and the `Idle` group made
from VTube Studio's idle animation), `saved`, `detectedBy` (`names` or
`thinking`), `actions` (each one's `n` as in `CharacterActionName-<n>`, `id`,
`kind`, `name`, `detail`, `tag`, `cue`, `use` (the When to use text, null while
the box is empty), `hint` (what the reply prompt says next to the tag: `use`, or
Martlet's own hint while `use` is null), `enabled`, `mode` (`brief`, or
`lingering`: stays on after `{tag}` until `{/tag}`), `modeSaved` (false while it
is the default), `vtsToggle` (a VTube Studio ToggleExpression hotkey turns it
on) and whether replies
are `offered` it for `engine`, a voice engine key, `none` or absent for a voice
without tags), `blushLevels` (the model's blush levels, faintest first: `level`
1 to 3 for `blush`, `blush_deep` and `blush_fierce`, with the row's `n`, `id`,
`kind`, `name`, `tag`, `mode` and `offered`; the first is the model's own emote
tagged `blush` when that replaces Martlet's blush), `replyPrompt` and `replyTags` (what replies get while the
character shows; lingering emotes add their `{/tag}` off tags) and `namingPrompt` (`instructions` and the numbered `list` the
Thinking model is sent). With `showing`, the lingering emotes the character
would show now (`["glasses", "blush:12"]`, minutes after the colon),
`showingNote` is the line the newest message's notes get (*Your character is
showing {blush} (12 min), ...*); it never goes in the instructions. With `voiceTag`, a voice's tag such as `[laugh]` or
`(sighs)` or a reply tag such as `{nod}` or `{/blush}`, `setsOff` lists what it sets off
(`kind`, `name` and `holds`, whether it lingers, such as the `laugh` voice emote; one expression and one
motion picked at random when several share a cue; for a combo's tag such as `{flustered}`, each of its parts that is on)
and `turnsOff` lists what an off tag turns off (the lingering emote, or a combo's lingering parts); `combo` is the combo
the tag names, or null. `combos` lists the model's [combos](AVATARS.md#emotes-and-motions) (Martlet's own and the owner's): each one's `n`
as in `CharacterComboTag-<n>`, `tag`, `parts` (each part's `id`, `kind`, `name`, `tag`, `enabled` and `mode`; `kind`
is `missing` for a part the model doesn't have), `use`, `hint` (what replies get next to the tag), `enabled`, `lingers`
(a part that is on lingers, so `{/tag}` is offered too) and `offered`. `martletCombos` lists each of Martlet's own
combos: `tag`, `parts` (gesture tags), `use`, `startsOn` (false for `ahegao`), `given` (whether the model was given
it; `given_combos` in `character-actions.json`) and `n` (its row in `combos` while one has its tag, otherwise null,
such as after the owner removed it). With `combos`, strings such as
`flustered: blush hearts nod | when flattered` read the way the Combos section's boxes are read, those combos replace
the saved ones; when they can't be saved, `combosProblem` says why (such as *no emote has the tag 'wave'.*) and the
saved ones stay. With `answer`, a simulated Thinking reply such as
`1: blush | - | stays | when shy` (the mode may be left out), `parsed` shows what the production parser makes of
it (`read`, `problem`, `actions`, `prompt`). Model-authored names only, never
the model's path; it reads and contacts nothing else.

`character_touch_zones` rehearses Companion › Touch › [Touch zones](AVATARS.md#touch-zones)
with no vision request: `zones` (how many Martlet knows, which are
`intimate`, used only with *Include intimate zones*, and the `defaults`
*Detect zones* looks for on every character, with `defaultParts` in words),
`request` (the step-by-step requests for the model: `wanted`, the zones a
detection looks for, which are the defaults and the ones the owner added
(`added`), and `required`, the ones it must end with; `parts` with the whole
character (its `ids`: head, upper_body and lower_body, and a tail, wings or
held item only when the owner added one), `zones` with each close-up (its
`regions` and the wanted zones each asks for) and `check` with the numbered
boxes, each with its `instructions` and an example `text`; `extras`, the
tail, wings or held item it looks for; `special`, what is special about the
character: the `maximum` zones of its own the model may add (6 by default),
its `instructions`, an example `text` and `before`, the special zones found
before with their `id` and `name`, whose IDs the model is asked to keep), `parsed`
(what the production parser makes of `answer`, a simulated vision reply about
the whole picture: JSON boxes as fractions or named edges (`left`, `top`,
`right`, `bottom`), pixels of a `width` × `height` picture, 400 × 800 by
default, or Qwen-style 0..1000 `bbox_2d` grounding; an answer cut off part way
keeps the zones it finished), `detected` (those zones
bound to `probe`, a simulated renderer zones probe of Live2D `drawables` (each
with the `part` it belongs to) and VRM `bones` in page fractions, and a Live2D
model's own `parts` (`id`, its DisplayInfo `name`, such as 头 or 右腿, and its
`parent`); a zone also takes the drawables of its own feature's part that lie
by its box, such as an eye's white and lashes outside a tight eye box; a
probe's `chains` (a Live2D model's swinging parts: each `name`, the physics
settings' names, its `drawables` from root to tip and its reach `left`, `top`,
`right`, `bottom` in page fractions) and `springs` (a VRM's spring-bone chains:
`name` and `joints`, each `bone` (its node's name), `x` and `y`) let a tail,
wings or animal ears follow all of their part, and the hair a ponytail; each
zone then has `follows` (the part's own names) and `areas` (each `box` as edges,
its `drawables`, `bones` and `nodes`, and `fromModel`, true for an area that
follows the model's own part)), with `crop`, `"left,top,width,height"` where
the snapshot sat on the page; `probePath` reads the `probe.json` that *Detect
zones* keeps with the pictures it sent, crop and all, or a bare probe), `hints`
(what the probe tells: how many parts the model has (`modelParts`) and names
(`namedModelParts`), the body parts its names place (`namedParts`; each of
`areas` with its `part`, the character's own `side` for one that comes in pairs
and its `box` as left, top, right and bottom fractions of the snapshot), the
close-ups' windows they give (`regions`: head, upper_body and lower_body, or
null), the `extras` its names place (a tail, wings or animal ears, which the
first guess and *Detect zones* add as zones special to the character), and its swinging parts: `chains` (each `name`, how many
`drawables`, the body `part` its name says, its `root` and `tip` drawable) and
`springs` (each `name`, how many `joints` and the `part` its name says)), `saved` (the model's zones in
`character-touch-zones.json`: how many, how many are `active`, who found them,
whether they were found with the character framed `whole`, the `crop` (where
the picture sat on the page with the character framed whole, as `left`, `top`,
`width` and `height` fractions; a `top` plus `height` above 1 means the picture
zoomed out to show parts the model draws past its own canvas, such as legs),
whether a snapshot
is kept and its `snapshotProbe` (how many `chains` and `springs` the probe kept
beside it has; the desktop binds the zones again with it when the owner moves
one), what the last detection `sent` (its plain `line`, `requests`,
`pictures`, `steps` and, from its `probe.json`, the `probe`'s hints as above) and
each zone's parts, its number of `areas`, what it `follows`, `plays`, whether
Martlet `notices` it, whether the owner `added` it, whether it is `special` to
the character (found as special, not added) and the owner's `hint`) and,
with `touch` (a `CharacterTouch` object as JSON; `wholeX` and `wholeY` are where
it lands with the character framed whole, `restX`, `restY`, `restWholeX` and
`restWholeY` where the touched point of the character was in its rest pose, now
and framed whole, as the renderer traces it on the touched mesh, and `hair`
whether the topmost drawable is hair), `match`: the zone it lands
in, `how` (`drawable`, `node`, `bone`, `hair`, `box` or `coarse`; a VRM spring-bone
joint or a drawable a zone follows by the model's own part, such as a tail's, is
that zone's first, before hair (cat ears hang from the head as hair does), and
reads `node` or `drawable`, with `traced` false; with a VRM `bone`, the
smallest zone on the part of the body that bone moves whose box holds the
point wins, and reads `bone` when it holds that bone, else `box`; a zone whose
box holds the point inside the box of a zone that owns the touched drawable,
with none of its own drawables under the touch, wins as its finer part and reads `box`;
without `hair`, a drawable lower in `drawables` that a smaller zone inside the
top drawable's zones owns wins, as an overlay over that part), `traced`
(whether the boxes were compared with the rest point), `at` (the point they were
compared with: `x`, `y` and `rest`, true when it is the rest point), `area` and
`areas` (for a zone with several areas, which one the touch landed in, from 0, and
how many it has; `area` is null for a zone with one), `follows` (what of the model
the zone follows, or null), `touched`
(every zone the touch lands in, the matched one first: where zones overlap, each
other zone in use with an area whose box holds that point, on the same part of the body as
the touch, the hit VRM bone's part, else the matched zone's; a zone whose box
frames a smaller touched zone is left out), its rough `coarse`
zone, what it `plays` (the matched zone's reaction), whether Martlet `notices` it,
`noticing` (the touched zones Martlet notices), the line the Thinking model would
get for that one touch on all of them (`noticed`, such as *They patted the top of
your head once.* or *They poked your groin and your left thigh once.*; a press of
600 ms or more in `heldMilliseconds` is a hold) and how long it `rests`.
With `detect`, the production detection (`TouchZoneDetection`) runs on
`snapshotPath` (a PNG of the character, transparent around it, as the renderer
takes it): it composes and encodes every picture it would send (the whole
character on its backdrop with the grid, each close-up, each check with the
numbered boxes; `previewDirectory` keeps them as files to look at) while a
FIXTURE - NOT AI stand-in answers each request from `answer`'s zones (fractions
of the snapshot). `guess`, a wrong first answer in the same form, answers the
close-ups instead, so the checks have something to correct; `checks` sets the
rounds per part (0 to 5, 2 by default); `failAt` makes that request (1 for the
first) fail instead of answering, as a model whose computer stopped answering,
so the detection stops there. The detection looks for `request`'s `wanted`
zones. `add` (zone IDs, comma-separated, such as `hand_left,tail`) adds zones
as the owner's *Add zone* does (in the middle of the picture, marked `added`;
a zone the model already has is only marked): the detection then looks for
them too and must end with them, and `detected` keeps one it can't place where
it was (moved with the picture when the `crop` changed). With `save` and no
`answer` or `detect`, `add` saves the zones with the ones added, as *Add zone*
does. With Include intimate zones on (`includeIntimate`, on unless it or the
saved zones turn it off) the intimate zones it looks for (by default the neck,
mouth (`lips`), ears, breasts, hips (`hip_left`, `hip_right`) and groin;
`TouchZoneDetection.Erogenous` lists every intimate kind) must be found, and
so must the zones the owner added: the ones the close-ups miss are
asked for again on the whole character (the `missing` step), then the intimate
ones are worked out from the zones around them. After the close-ups (and the
`missing` step), the `special` step asks the whole character what is special
about it (at most `special` zones, 0 to 10, 6 by default; 0 doesn't ask): the
stand-in lists `answer`'s zones that are special, which are the extras Martlet
knows (such as `tail`) and zones of its own that `answer` names with an ID and
a `name` (such as `{"id":"hair_bow","name":"hair bow",...}`). Each becomes a
zone with that name; ordinary body parts and anything intimate are left out.
A tail, wings or animal ears the probe's part names place are added too. The
`special check` rounds then check them on the whole character (the `steps` say
*special: found hair_bow (Hair bow)*). With a probe whose parts name body parts, the
close-ups' windows come from them and boxes that clearly miss their named part
are moved onto it (the `steps` say *took head, upper_body, lower_body from the
model's own named parts* and *moved neck onto the model's own neck*).
`detection` then reports each request
(`asked`: its `step`, `kind`, picture size and type, its `region` in the
snapshot, `marks`, the message,
the stand-in's answer and whether it `failed`), the `steps` (what each found,
swapped, moved, removed, added or worked out), `requestCount`, the `failure` it
stopped at (null when none), what it `missed`, the `wanted` zones, the `required` zones and those
still missing (`requiredMissing`), the zones `special` to the character it
found (each `Id` and `Name`), and how far the found boxes are
from `answer`'s (`worstEdge`, `meanEdge`).
With `estimate` (and no `answer` or `detect`), the first guess that the Touch
zones page places on a model with no zones and no picture
(`TouchZoneDetection.Estimate`) runs on `snapshotPath`, with the probe's hints
(`probe` or `probePath`: its face, named parts and skeleton). No vision request
is made. `estimate` then reports the `face` the probe gave, the `steps` (where
the face came from, how far the body was stretched and which zones each source
placed: *from the model's own named parts: ...*, *from the model's skeleton:
...*, *from the body's proportions: ...*), the `count`, the `wanted` zones and
the ones it couldn't place (`missing`), and the `zones`; `detected` shows them
bound to the probe, and `save` writes them as the page does (marked
`estimate`, with `snapshotPath` as their picture).
The model is `modelPath`, `modelId` or the one the `dataDirectory`'s
`avatar.json` shows. `save` (an explicit, disposable `dataDirectory` only)
writes the parsed zones (with `detect`, the detected ones and every picture the
detection sent, as *Detect zones* would) with `snapshotPath` (a PNG)
as their picture and `includeIntimate` setting the switch, so the section can
be checked with `-Desktop`. With `temperament` (a simulated Thinking answer
for [Touch temperament](AVATARS.md#touch-temperament), such as
`{"groups":{"head":{"attitude":2,"reactions":["hearts","blush"]}}}`) or
`personaId` (the temperament that persona uses in the `dataDirectory`'s
`character-temperaments.json`: its own, the built-in reactions or a custom
one), `match` plays what the temperament decides
when the zone has no pick of its own, and its `reaction` tells `from`
(`owner`, `temperament` or `default`), the `attitude` word, whether it
`escalated` (with `repeats`, the touches in a row) and how long it `linger`s.
`temperament` in the result shows the request Thinking gets (with
`personality`, its text), the `vocabulary` and `attitudes` allowed, the six
`categories` (each `Id`, `Label` and the zone kinds it covers, `parts`; every
zone kind is in exactly one, and `intimate` holds the intimate ones), whether the
answer was `read`, and what is `used` (who decided it, or `custom` with
`custom` naming the custom temperament, a `summary`, each category and zone
and the escalation). `personas` lists each persona of `settings.json` (and any
other the file names): its `personaId`, `name`, whether it is `active`, what it
`uses` (`own`, `built-in` or `custom`), the `custom` temperament's name and its
`own` temperament's source and summary. `custom` lists the custom temperaments:
`Id`, `Name`, `summary`, `groups`, `zones` and the personas that use it
(`usedBy`). Never the model's path; it contacts nothing.

The section's status fields (on Companion › Touch, `CompanionTab-Touch`) are `TouchZonesStatus` (how many zones, how many in
use and who found them: the Thinking model, you, or *A first guess Martlet placed from the character's own parts and shape,
with no AI...*; or that none are found yet), `TouchZonesVision`
(which model sees the pictures: a Thinking pool member that can see, else
whether the Thinking model can see and where pictures go; read from the saved
setup, so it is right before the talk window opens), `TouchZonesDetectNote`
(shown only when `TouchZonesDetect` is off because no model can see pictures:
*Detect zones is off: no model that can see pictures is set up...* or *...the
Thinking model is text-only...*), `TouchZonesDetection`
(the first guess on a model with no zones and no picture: *Drawing the
character to place its first zones (no AI, nothing is sent)...* as soon as the
page opens, then *First zones: Martlet placed 24 zones at ... with no AI and
nothing sent...*, with `TouchZonesPicture` and one `TouchZoneRect-<n>` per zone,
no click and no `--allow-ui-effects` needed; a first guess that couldn't be
placed is tried again when the page opens again. Then
how *Detect zones* went, and each step while it runs: *Step 2: finding the
zones of the character's head in a close-up...*, *Checking the zones of ...,
round 1 of 2...*, *Step 5: asking again for 3 zones the close-ups missed, on
the whole character...*; it shows *Taking a picture of the character...* at
once, even when the click left the keyboard focus on `TouchZonesDetect`. When a
request fails it reads *Finding zones stopped at request 4: couldn't ask the
Thinking model (ResponseTruncated). Your 38 zones from before are kept. Try
again when it answers.*: the zones from before and their picture stay (over a
first guess: *The 24 zones of the first guess are kept.*), or, with
none before, *The 5 zones found until then are kept. Press Detect again to find
the rest.*; while it runs over a first guess, the zones it has found replace
their first guesses on the picture and the others stay until it is done), `TouchZonesSent` (what the last detection sent: how many
pictures, how large and what they showed, such as *..., and the whole character
again for the zones the close-ups missed*, or *FIXTURE - NOT AI answered
these.*), `TouchZonesLast` (the zone the last click landed in,
how it was found (*box, traced to the rest pose* when the renderer traced the
touched point back to the rest pose the zones were found in), the other zones it
landed in where zones overlap (*Groin (box), with Left thigh, at ...*), what it
played or that it was resting, and which of the zones Martlet
noticed: *it*, *them* or their names), `TouchZonesNoticed` (what Martlet noticed that waits for a reply,
the plain touch line, *Martlet stopped talking for it.* when a touch stopped
Martlet talking, and when a touch-only reply starts, or that it waits for
your next message because you started talking or typing or Martlet can't reply
now), `TouchZonesNoticedLast` (which reply took the last touches, the short
history line and exactly what the Thinking model was told),
`CharacterPhysicalLast` (the last stroke across the locked character: zones
crossed, pace, passes, seconds and samples on the character; or the last move,
zoom, pan, lock, hide or show as Martlet's touch ledger heard it),
`TouchInterrupt-any`, `TouchInterrupt-intimate` and `TouchInterrupt-never`
(*When you touch Martlet while it talks*: radio buttons whose `selected` state
reads in `ui_snapshot`; choosing one saves `talk-preferences.json`, so it
needs `--allow-ui-effects`),
`TouchZonesSaveState` and each zone's `TouchZoneState-<n>` (its ID, the parts
it follows, for a zone with several areas how many, for a zone that follows the
model's own part *follows the model's own 尾巴 wherever it moves: 21 parts in 6
areas*, *added by you* for a zone the owner added, which *Detect again*
looks for too and keeps where it is when it can't find it, or *special to this
character* for a zone *Detect zones* found as special to the character, and its
default reaction), and `TouchZonesAddNote` (which zones *Detect zones* looks for:
*Detect zones looks for the hair, eyes, ears, nose, mouth, neck, breasts, upper
arms, forearms, stomach, hips, groin, thighs, calves and feet, and for anything
special to this character, such as animal ears, a tail, wings, a hat or a bow.
Add any other zone here...*). When a detection finds zones special to the
character, `TouchZonesDetection` names them: *Found 27 zones (2 special to this
character: hair bow and tail) at ...*. `TouchZonesDetect` sends the character's
pictures to Thinking; it is disabled only while a detection runs, while the
character is still being read, or when no model can see pictures (never because
the character is hidden). The picture comes from a second renderer that loads
the character off screen (its window *Martlet character picture* sits outside
every screen and is fully transparent), never animates, draws one frame in the
rest pose and closes; the character on the desktop (`SetupCharacterNow`,
`SetupCharacterView`) doesn't change. The desktop log records *Character model
loaded off screen for its touch zones picture* and *Finding touch zones: a ...
picture of the character in its rest pose, drawn off screen*, with *zoomed out
to 0.94x to show the parts drawn past the model's own canvas* when the model
draws past its own canvas, and how many bones and named parts the model gives
(*0 bones and 12 named parts from the model (39 of its 39 parts named in its
DisplayInfo file, so the close-ups hold their parts and boxes that miss their
part move onto it)*). `TouchZonesStop` (shown while it runs; a passive click)
stops it and keeps the zones found until then, `TouchZonesSentView` (*Show the
picture Thinking saw*, a check box) shows the whole character as Thinking saw
it under the boxes, `TouchZonesSentOpen` opens the folder of pictures in
Explorer, `TouchZoneTry-<n>` plays on the character, and
`TouchZonesIntimate` (its value is its label, which names every intimate part,
the breasts and the groin too), `TouchZonesAdd`/`TouchZonesAddKind` (its value
is the zone chosen to add; it offers every zone the model doesn't have yet, and
the zone it adds is marked *added by you*) and each zone's
`TouchZoneOn-`, `TouchZoneName-`, `TouchZoneReaction-`, `TouchZoneReaction2-`,
`TouchZoneNotices-` (*Martlet notices*; its checked state reads in `ui_snapshot`), `TouchZoneNarration-` (the owner's optional hint; it shows only while *Martlet notices* is on), `TouchZoneCooldown-`, `TouchZoneBox-`
(its value is each area's box, left, top, width and height in percent, the areas
separated by `|`; for a zone that follows the model's own part, setting a
different box places the zone there as one box again),
`TouchZoneAddArea-` (*Add area*: a box beside the zone's last one; shown while
the zone has fewer than 8 areas and doesn't follow the model's own part),
`TouchZoneRemoveArea-` (*Remove area*: takes the last area away; shown while the
zone has more than one),
`TouchZoneDelete-` and its areas on the picture (`TouchZoneRect-<n>` for the
first, `TouchZoneRect-<n>-<k>` for area k from 2, inside
`TouchZonesPicture`) save, so they all (except Stop) need `--allow-ui-effects`.
Saving binds the zones to the model again with the probe kept beside the
picture, so a box moved onto a tail gets that tail's areas, and the page then
shows them. `TouchZonesShowOnCharacter` (*Show the zones on the character*, a
check box for this session only; `ui_toggle`, like every check box, needs
`--allow-ui-effects`) draws the zones' areas over the showing character as they
move; `character_zones` reads them, drawn or not.
`ui_snapshot` lists the picture and each area's box on it as custom controls;
with `{"idPrefix":"TouchZoneRect","layout":true}` each box's `bounds` show
where it sits on the picture (`TouchZonesPicture`'s `bounds`), one box per
area of each saved zone.
The picture sits in the zone map, `TouchZonesMap` (a frame that zooms, for
moving and resizing boxes precisely). `TouchZonesZoomIn`, `TouchZonesZoomOut`
and `TouchZonesZoomReset` (*Zoom in*, *Zoom out*, *Reset zoom*) are passive
clicks: they change only how large the map shows the picture, in steps of 1x,
1.5x, 2x, 3x, 4x, 6x and 8x, and save nothing. `TouchZonesZoom` reads how far
it is zoomed in (*Zoom 2x*). Zoomed in, `TouchZonesPicture`'s `bounds` grow
with the zoom while `TouchZonesMap`'s keep the height they have at 1x (the
map grows as wide as the picture or the page), each `TouchZoneRect-<n>` (and
`TouchZoneRect-<n>-<k>`)
grows with the picture, and `ui_scroll` on `TouchZonesMap` reads and moves the
part of the picture that shows (`shows`: left, right, top and bottom in percent
of the picture). The zoom buttons keep the middle of what shows; Ctrl+wheel over
the picture zooms where the pointer is. The page keeps the zoom and the part
shown while it draws again (Add zone, each step of Detect zones) and shows the
whole picture when it opens again or shows another model. A zone added while
zoomed in starts in the middle of the part shown, as large on the screen as
one added at 1x (a fifth of the picture each way).
The Character page lists more than `ui_snapshot`'s 200 controls; read the
section with `{"idPrefix":"TouchZone"}`. With `"layout":true`, each row's text
boxes, choices and buttons in Touch zones, Touch temperament and Emotes and
motions read 32 pixels tall in `bounds`, with their check boxes and labels on
the same centre line. Setting
`MARTLET_TOUCH_ZONES_FIXTURE` to a text file before launching the desktop makes
a FIXTURE - NOT AI stand-in answer every request of *Detect zones* from that
file's zones (JSON about the whole snapshot, as `answer` above; shown in
`TouchZonesVision`, `TouchZonesDetection` and `TouchZonesSent`) after taking the real snapshot,
composing and keeping every picture and probing the showing model's drawables
or bones, so the whole detection runs with no vision request (and *Detect
zones* is on without a model that can see). With it, setting
`MARTLET_TOUCH_ZONES_FIXTURE_FAIL_AT` to a request number (1 for the first)
makes the stand-in fail that request instead of answering, as a model whose
computer stopped answering: *Detect zones* then stops there and keeps the zones
from before (FIXTURE - NOT AI in `TouchZonesDetection`).

`character_eyes` rehearses Companion › Eyes › [Where the eyes are](AVATARS.md#eyes)
with no vision request: `request` (the close-up's `edge`, 768 pixels, and its
width in `faceWidths`, 1.6, with the `instructions`, the first `text`, the
`check` message that goes with the boxes drawn and numbered, and the `again`
message after an answer Martlet couldn't use), `closeUp` (the picture's
`width` and `height`, the `area` it cuts from the snapshot and the `face` in
the snapshot's pixels with its `rollDegrees`), `measurement` (with `answer`, a
simulated vision reply about the close-up such as
`{"left":{"iris":{"left":0.34,"top":0.46,"right":0.41,"bottom":0.52},"eye":{...}},"right":{...}}`,
as fractions, pixels or the 0..1000 grid, or flat keys such as `left_iris`;
and `second`, the answer to the second request when the first can't be read
or fails Martlet's checks: the `steps`, each request `asked` (its `step`,
`kind` `Eyes`, `Check` or `Again`, picture, `marks`, message and answer), the
four `boxes` as fractions of the close-up, the `problems` the checks found,
the `failure`, and the `hint` the renderer gets: each eye's `iris` (`x`, `y`,
`r`) and opening (`eye`: `x`, `y`, `rx`, `ry`) in face widths from the face's
middle, roll removed), `checks` (the limits the checks use, in face widths),
`saved` (the model's measurement in `character-eyes.json`: who measured it,
when, the `hint`, the pictures kept, whether the picture of its boxes is kept
and its plain `line`) and, with `eyesFrom` (`mesh`, `bones`, `vision` or
`estimate`, what the renderer says the eyes use), the `status` line the
section shows. Without `snapshotPath` the close-up is exactly 1.6 face widths
around an upright face; with `snapshotPath` (a PNG of the character,
transparent around it) and `face` (`"x,y,width[,rollDegrees]"`, the face's
middle and width as fractions of the snapshot) the production close-up is
composed and encoded as the desktop sends it (`previewDirectory` keeps the
pictures). `save` (an explicit, disposable `dataDirectory` only) writes the
measurement for the model (`modelPath`, `modelId` or the one the
`dataDirectory`'s `avatar.json` shows) with its pictures, marked FIXTURE - NOT
AI, as *Measure the eyes* would; `forget` removes it. Never the model's path;
it contacts nothing.

Companion › Eyes › *Where the eyes are* (`CompanionTab-Eyes`) reads through `CharacterEyesStatus`
(where the shown model's eyes come from: *From the model's own meshes.*,
*From the model's own eye bones and meshes.*, *Measured with vision at 3:12
PM.* or *Estimated: Martlet guesses where the eyes are from the face...*; while
the character is hidden, the saved measurement or *Show the character to see
where its eyes come from.*), `CharacterEyesProgress` (how measuring goes or
went: each step while it runs, the result or why it failed, such as *Couldn't
ask the Thinking model (...)* or *The Thinking model's eyes didn't pass
Martlet's checks: ...*) and `CharacterEyesNote` (shown only when no model can
see pictures: *Measure the eyes is off: ...*). `CharacterEyesMeasure` (*Measure
the eyes*) sends a close-up of the character's face to Thinking and
`CharacterEyesForget` (*Forget the measurement*) deletes it, so both need
`--allow-ui-effects`; `CharacterEyesPicture` is the close-up Thinking saw with
its four numbered boxes. The measurement uses the same off-screen still
renderer as *Detect zones*, whose picture now also carries the face anchor; the
desktop log records *Measuring the eyes: a ... picture of the character in its
rest pose, drawn off screen, with its face ... pixels wide*, each picture sent
and each step. When a renderer reports `eyesFrom` `estimate` and a model that
can see is set up, the desktop measures the model once on its own (once per
model each time Martlet starts, and never again after *Forget the
measurement* until it starts again). The hint goes to the renderer (`eyes`)
after each model load and after each measurement; the desktop log records
*The character's eyes got Martlet's vision measurement; they use ...*.
Setting `MARTLET_EYES_FIXTURE` to a text file before launching the desktop
makes a FIXTURE - NOT AI stand-in answer every request of the measurement with
that file's text (JSON about the close-up, as `answer` above; shown in
`CharacterEyesProgress` and `CharacterEyesStatus`, and saved with `by`
`fixture`) after taking the real snapshot and composing and keeping every
picture, so the whole path runs with no vision request (and *Measure the eyes*
is on without a model that can see).

Touch temperament (on Companion › Touch, below Touch zones) reads through `TouchTemperamentStatus`
(for which persona and who decided it: built-in reactions, the Thinking model,
`FIXTURE - NOT AI` or your own choices; or what it uses instead: *built-in
reactions, as you chose* or *your custom temperament "Shy cat"*; its `help` is
the whole temperament in words: the attitude per category and zone, such as
*head loves, torso neutral (no reaction), ..., intimate hates*, or *intimate as
body groups* for a temperament without an intimate line, the eyes (*eyes: look
straight ahead*), the parts whose touch turns them to your mouse and after how
many touches it escalates),
`TouchTemperamentDecision` (how deciding went, or that a personality change
left your own choices in place; shown until you change something yourself),
`TouchTemperamentSaveState` (whether table edits saved),
`TouchTemperamentUseState` (what the last *Uses*, *Create*, *Rename* or
*Delete* did, or why not, such as *Not saved: "Built-in reactions" is already
a choice...*; these three only while they have something to say),
`TouchTemperamentUse` (*Uses*: *Decided from its personality*, *Built-in
reactions* or a custom temperament's name), `TouchTemperamentNewName` (the
name typed for a new custom temperament), `TouchTemperamentName` and
`TouchTemperamentCustomUsers` (shown while the persona uses a custom
temperament: its name and *Used by Mira and Aki...*),
`TouchTemperamentGaze` (*Eyes usually*: a gaze's label or *(not decided:
follow your mouse)*), `TouchTemperamentAfter` (touches in a row before it
escalates), each table line's `TouchTemperamentAttitude-<category or zone
ID>` (an attitude word, *(built-in)*, or for `intimate` *(as the body)*),
`TouchTemperamentReaction-` (*(default)*, the feeling's usual reactions,
*(nothing)* or a reaction such as *look away*), `TouchTemperamentReaction2-`
(*(nothing)* or a reaction), `TouchTemperamentLinger-` and
`TouchTemperamentLook-<category or zone ID>` (the seconds the first reaction
stays on and the eyes look at your mouse after a touch there), each category's
`TouchTemperamentParts-<category ID>` (*Parts:* and the zones it covers; the six
categories are `head`, `torso`, `arms`, `lower_body`, `extras` and
`intimate`, and `TouchTemperamentParts-intimate` names the breasts and the
groin) and `TouchTemperamentAddKind` (the part chosen to give its own line;
it offers every zone). A line shows only the controls that apply: a category at
*(built-in)* shows only its attitude, `TouchTemperamentReaction2-` shows after
a chosen first reaction and `TouchTemperamentLinger-` not after *(nothing)*,
so the others are not in `ui_snapshot` until then.
`TouchZonesLast` and `TouchZoneState-<n>` also name the attitude, whether the
reaction came from the temperament and how long it looks at your mouse.
`TouchTemperamentDecide` (*Decide from personality* before anything is
decided, then *Re-decide from personality*) sends the personality to Thinking
(and the persona then uses its own decided temperament),
and `TouchTemperamentUse`, `TouchTemperamentNewName`, `TouchTemperamentNew`
(*Create*: a custom temperament copied from what the persona uses now, which
it then uses), `TouchTemperamentName`, `TouchTemperamentRename`,
`TouchTemperamentDelete` (the personas that used it use their own again),
`TouchTemperamentGaze`, `TouchTemperamentAttitude-`,
`TouchTemperamentReaction-`, `TouchTemperamentReaction2-`,
`TouchTemperamentLinger-`, `TouchTemperamentLook-`, `TouchTemperamentAfter`, `TouchTemperamentAddKind`,
`TouchTemperamentAdd` and `TouchTemperamentRemove-<zone ID>` (the small ✕ by a
part's name) save, so they all need
`--allow-ui-effects`. While the persona uses a custom temperament, the table
edits that custom temperament for every persona that uses it; with *Built-in
reactions* the table is hidden. `character_touch_zones` shows the temperament's `gaze`,
each entry's `look`, the matched touch's `reaction.look`, the categories, the
custom temperaments and which persona uses which. Setting `MARTLET_TOUCH_TEMPERAMENT_FIXTURE` to a textfile before launching the desktop makes deciding read that file (read again
each time) as the Thinking model's answer (FIXTURE - NOT AI, shown in
`TouchTemperamentStatus` and `TouchTemperamentDecision`, and saved with the
source `fixture`). Saving a changed personality (`OpenCompanion`,
`CompanionText`, `CompanionClose`) then runs the real decide, store and route
path with no model.

`character_gaze` shows [where the character looks](SCREEN_COMMENTARY.md#where-the-character-looks).
`usual` is the usual gaze from the `dataDirectory`'s `talk-preferences.json`
(Companion › Eyes › Where the character looks and the overlay's Eyes
menu): `choice` (`personality`, the default, `mouse`, `near`, `ahead` or
`window`; `GazeUsual`), `free` (whether replies may change it; `GazeFree`),
`personality` (the gaze of the temperament that the active persona of
`settings.json`, or `personaId`, uses in `character-temperaments.json`: its own
or a custom one), `personaId`, `gaze` (the gaze
that applies) and `from` (`owner`, `personality` or `default`), `prompt`
(what every reply is told: `instructions`, with the data directory's edited
prompts, and `tags`; null when the character may not change it or *Where you
look* is emptied) and `noteWhenChanged` (the note a reply gets while its own
choice holds the eyes, one minute after it chose another gaze). `aim`
rehearses the production `CharacterGaze.Aim` the overlay runs every 50 ms for
the character's frame near the lower-right corner, each with its `expected`
and actual `target` (`mouse`, `ahead`, `window` or `point`) and the point `at`:
`mouseFar`, `nearModeMouseFar` (`ahead`), `nearModeMouseNear` (`mouse`),
`aheadModeMouseNear` (`ahead`), `windowMode` (the window's middle),
`windowModeWorking` (where you worked in the window), `windowModeWorkedOutside`
(a place outside the window: its middle again), `windowModeNoWindow`
(`ahead`), `touchWhileAhead` (`mouse`) and `glanceWhileFollowing` (`point`).
`watch` rehearses the production `WindowWatch` the window gaze uses, with
`CharacterGaze.Aim`, over a sequence of moments, each with its `expected` and
actual `watching` (`pointer`, `text cursor` or `middle`; null without a
window), `target` and point `at`: `switchedWithTheKeyboard` (`middle`),
`pointerMovesOverTheWindow` (`pointer`), `typingWhileThePointerRests` and
`typingOn` (`text cursor`), `pointerOnTheCharacter` (still the text cursor),
`pointerMovesOverTheWindowAgain` (`pointer`), `pointerBackOnTheCharacter`
(still the pointer), `anotherWindowWithATextCursor` (`text cursor`),
`desktopInFront` (`ahead`) and `clickedBackIntoTheWindow` (`pointer`). `saved` is Companion › Vision › Glances at
your screen (`usual gaze`, the default, or `martlet decides`, from `DecideGaze`
in `talk-preferences.json`), then the change `grid` (32×18 cells, `ChangeThreshold`
and the stronger `CharacterChangeThreshold` under the character's overlay) and
`holdSeconds` (`glance`, `chosen` and the `gap` between glances), then
`scenarios`: the production `GazeDirector` on generated 1920×1080 pictures
(NOT screenshots; nothing is captured or shown), each with its `expected` and
actual `verdict` and the `spot` looked at (`x`, `y` in physical pixels,
`Place`, `reason`, `holdSeconds`): `still`, a `notification` popping up near
the lower-right corner (a glance at the bottom right),
`anotherChangeRightAfter` (`TooSoon`), `sameSpotAgainSoon` (`Seen`),
`sameSpotAgainLater` (a glance), `notificationBehindTheCharacter` (a glance),
`onlyTheCharacterMoved` and `speechBubble` (`OnlyCharacter`), `newScene`
(`Everywhere`), `byTheMouse` (`ByMouse`) and `changesAllOver` (`Scattered`);
`ok` is true when every verdict, aim and watch moment is as expected. `tags` gives where each look tag
points on one screen and on two side by side, `modeTags` the five gaze tags
and the gaze each sets (`usual` for `{look usual}`), `notTags` lists tags that
aren't look tags, `prompt` is what a screen glance is told (`instructions`, with the
data directory's edited prompts, and `tags`; null when *Where the character
looks* is emptied) and `replies` shows what the production segmenter makes of
answers (`answer` replaces the samples): `spoken`, `shown`, `quiet`
(a `[pass]`), `looks` (each look cue's tag, `place` and `afterPiece`, -1 for
a look without words) and `gazes` (each gaze cue's tag, `gaze` and
`afterPiece`). Read-only; it contacts nothing.

Companion › Eyes › Where the character looks (`CompanionTab-Eyes`) reads through
`CharacterGazeNow`: what the eyes do now and why (*The character looks straight
ahead, as its personality decided. It may change where it looks in its
replies.*; *as you chose*; what a reply chose and when; *Right now it looks at
your mouse after a touch on top of head.*), and its `help` is the overlay's
last answer (*The character's overlay last turned toward: mouse (-0.86,
-0.39); usual gaze ahead.*). Its `CharacterGaze-personality`, `-mouse`,
`-near`, `-ahead` and `-window` radio buttons (`selected`) and the
`CharacterGazeFree` check box (`checkedState`) save `talk-preferences.json`, so
they need `--allow-ui-effects`. The desktop log records *The character looks at
the mouse for 3 s after a touch on top of head.*, *The Thinking model chose the
character's gaze: ahead (usually mouse).* and the overlay menu's *The
character's menu chose 'look-window'.*

`character_theme` makes a character model's colors and palettes the way
Settings › Appearance does ([Character palettes](UI_DESIGN.md#character-palettes)),
with the production code: `modelPath` (a `.model3.json` or `.vrm` on this PC),
else the model `dataDirectory`'s `avatar.json` shows, else the built-in
character (only where the Live2D runtime is beside the server). It returns
`renderer`, `key`, `textures`, `thumbnail` (a VRM's own picture), `analyzeMs`,
`swatches` (each main color's `Hex`, `share`, `Kind` and `Name`), `sources`
(`tint` and `tintStrength`, the `accents` candidates best first, the `light`
and `darkest` neutrals), `rules` (`light` and `dark`: `colors` by role,
`problems` and the `lowestContrast` of each role) and `saved` (how many colors
`dataDirectory`'s `character-themes.json` keeps for the model). With
`previewDirectory` (an absolute folder) it writes `<label>-rules-light.png` and
`<label>-rules-dark.png` (Martlet's window drawn with the real styles in each
palette); `label` defaults to the key. It never returns the model's path,
writes only to `previewDirectory` and contacts nothing.

`nearby_status` reads whether this PC lets Martlet on the owner's other
computers [find it](ARCHITECTURE.md#finding-your-other-computers) (optional
absolute `dataDirectory`, default the current user's): `share` is
`on (default)` when `nearby.txt` is missing, `on`, or `off` once the owner
unticked **Let my other computers find this PC**; `port` (9444); and `hosts`
from `hosts.json` (`state` `none`, `loaded` or `unreadable`; when loaded the
number `paired` and the `shareable` ones with `hostId` and `reach`
(`ThisPcDocker`, `SshDocker` or `SshNative`)). This PC's own host service set
up from the host dashboard is found by the desktop from Docker, not here
(`host_service_status` reads it). It
never returns addresses, SSH targets or keys and contacts nothing.

`node_link_check` runs [commands between computers](CLUSTER.md#commands-between-your-computers)
end to end on this PC's loopback and returns `{exitCode, report: {passed,
steps: [{name, ok, detail}]}}`: the real gateway (Kestrel, pinned TLS with a
throwaway fixture certificate, pairing, signed requests, the command mailbox
and its storage), the desktop's real client and agent loop with a fixture
runner (FIXTURE: it installs nothing), and two fixture devices. Steps check
that only known commands and arguments are accepted, anonymous requests are
refused, only the agent's local token takes and reports commands, output and
outcomes reach the sender, secrets never appear in lists, commands or the saved
copy, the shared Home Assistant connection (including token sharing, revision
wins, tombstones, invalid bodies, restart storage and no token in gateway logs),
cancel works (waiting and running), commands run side by side
(`runs-side-by-side`: the agent's `TakeAsync` starts a slow describe and two
installs of different roles at once, each on its own connection, and the
agent info says `parallel`), a second change to one role waits for the first
and the sender's `HostCommandList.WaitingText` says so (`same-role-waits`)
while a status runs beside them (`others-run-beside`), an update waits for
what runs and holds what was sent after it (`update-waits-for-running`), the
waiting change runs once the first ends (`same-role-runs-next`), the
`host.exposure` command reaches the agent with exactly its martlet-host options
while an address with a shell character or an extra argument is refused
(`exposure-command`), the update
then runs alone and the held command after it (`update-runs-alone-then-the-rest`),
an agent from before commands ran side by side (no `running` list) still gets
one at a time (`serial-agent-one-at-a-time`), an update that continues later
(FIXTURE: "Martlet is in use here") stays first and holds the queue until it
finishes and the waiting command runs right after it, commands survive a
restart with a new token and the queue is bounded. It runs `src\Martlet.NodeLinkCheck` (built with
`Martlet.Mcp`) as its own process, because the gateway needs the ASP.NET Core
runtime; it takes no arguments and contacts nothing outside loopback. The same
program's `live <pairing-code> <container>` mode checks a disposable Linux
gateway container built from this checkout (not the real host service).

`host_engine_check` (no arguments) checks that a host runs
[changes side by side](../deploy/host/README.md#changes-side-by-side) with this
checkout's real `deploy\host\martlet-host`: it starts one disposable
`ubuntu:24.04` container (`--network none`, `--pull never`, removed afterwards,
the engine in native mode against a fixture setup under `/tmp`; Martlet's own
host containers and volumes are never touched) and returns `{exitCode, report:
{passed, total, image, engine, steps: [{name, ok, detail}]}}`. Steps: `flock`
is present; a change (a `network-reset` waiting for a typed yes, like a console
left open) holds its locks (`engine.lock` 0600), records itself in
`engine.holders/` (scope `gateway`, running) and in `engine.holder`; `roles`
still runs; `status` says `Busy now: ...`; an automatic `update` (no terminal,
no `--yes`) stops at once with exit 75 and `MARTLET-BUSY ...`; a `--yes update`
with `MARTLET_LOCK_WAIT=3` waits, says what it waits for and gives up with 75;
a role change runs alongside it without waiting (`role-change-runs-alongside`);
a remove that already stopped its role and must still publish that through the
gateway keeps waiting past `MARTLET_LOCK_WAIT` instead of claiming nothing was
changed with `MARTLET-BUSY`, then unpublishes it
(`changed-host-never-claims-nothing-changed`);
`machine`, which restarts the gateway, waits for its lock and continues once the
holder is killed (SIGKILL), and so does an `update` without a terminal or
`--yes` that sets `MARTLET_LOCK_WAIT=60` (*Update hosts now*): it waits instead
of stopping and then runs; the next automatic run is not blocked (no stale
lock); `logs/engine.log` records the waits. With a fake `docker` CLI
(`/tmp/nativebin`, nothing real runs) two adds of different roles run side by
side, both stopped at their terms question (`role-changes-run-side-by-side`);
a remove of the same role waits for its add (`same-role-waits`) and an add of
another engine of the same `exclusive=` group waits too (`same-group-waits`);
an automatic `update` stops with `MARTLET-BUSY` naming all three adds; a
waiting `--yes update` holds back a role change asked for after it
(`later-change-waits-for-update`); once the adds are killed every waiting
change continues (`waiting-changes-continue`); and no records are left in
`engine.holders/` (`ended-changes-leave-no-records`). With a fake `dotnet` and
`systemctl`, `exposure` without options asks the gateway once and restarts
nothing (`exposure-prints`), with options and no `--yes` or terminal it stops
with *Nothing changed.* (`exposure-asks-first`), and `--yes exposure --outside
... --treat-all-as-outside yes` passes exactly those options to the gateway's
`owner-exposure` and restarts it (`exposure-saves-and-restarts`). The desktop's reader
(`HostEngineBusy.Read`) reads the engine's real busy line. It then checks the
Docker method's launcher and engine against a fake `docker` CLI (state in
`/tmp/fake`): an automatic `setup` while an `add` engine session runs in the
network holder's namespace stops with exit 75 and `MARTLET-BUSY installing
chatterbox (...)` without replacing `martlet-host-net`; a `--yes setup` waits
for it, then removes and recreates the holder and runs its engine; an engine
left in a replaced holder's namespace stops at once (`... was replaced while
this ran ... Nothing was changed`) while one in the current namespace
continues; and the desktop's reader reads that busy line. Finally a native
`--yes add` of a fixture role against a fake `docker` whose `compose up` fails
with exit 17 (as a role image build does when a download times out) stops with
exit 1 and `Stopped: Building or starting fixture-build failed ... run
'martlet-host add fixture-build' again` (`build-failure-says-run-again`, also in
`logs/engine.log`). A fixture role with two variants (`FX_ENGINE` `alpha`, with
a GPU option, a model suggested by GPU memory and its own prepare step, or
`beta`, with its own models and prepare step), like the `stt` role's whisper and
Parakeet engines, then checks that `describe` lists each variant's choices,
suggestion and GPU option with their condition (`role.choice_when`,
`role.suggested_when`, `role.gpu_when`; `variant-describe`); that an add of
`beta` keeps its own model and prepare step and ignores a GPU answer
(`variant-own-choices`); that an install from before the role had variants
(`profile_legacy`) moves to `beta` by preparing it, creating it, and only then
stopping `alpha` (`variant-switch-after-prepare`); that the same switch run again
after it stopped at `up -d` (as a failed build or download would, with `.env`
already naming `beta`) still stops `alpha` before `beta` starts
(`variant-retry-stops-the-other`); and that `alpha` refuses
`beta`'s model, then runs on the CPU with its suggested model on this GPU-less
fixture (`variant-gpu-and-suggestion`). With `uname -m` answering `aarch64`
(a shim), an ARM64 host refuses an add of a role marked `requires=x86_64` (the
NVIDIA CUDA roles) with *built only for 64-bit Intel or AMD (x86_64)* and
`describe` prints `role.unavailable=` (`arm64-refuses-x86-only-role`), and a role
with an ARM64 build (`arm64=<overlay>`, like the `stt` role's whisper.cpp arm64
image) gets that overlay as `compose.arch.yaml`, runs on the CPU even when the
GPU is asked for, and `describe` offers no GPU option
(`arm64-overlay-runs-on-cpu`). Last, `warm` on its own fixture host (fake
`docker` and `systemctl`) starts the stopped gateway (`systemctl --user start`),
runs `compose up -d --no-build` for every installed role, then the warm step of
`fixture-warm` with its saved choice filled in (`warm-it m2`; its `post_start`
step is not run again), reports `fixture-cold`, whose port never answers, as
*Not ready* and exits 1 (`warm-starts-and-warms-roles`); with `fixture-warm`'s
role lock held by another change it leaves that role alone (*Another change is
working on fixture-warm now*; `warm-leaves-held-role`). Without Docker or the
image it returns `exitCode` 2 and `notRun` (it never pulls). It does not cover
a real Docker daemon or a real host.

`host_supply_check` (optional `cacheDirectory`; default
`%TEMP%\Martlet\host-supply-check`) checks how Martlet sets up a native Linux
host [without internet access](../deploy/host/README.md#computers-without-internet),
with the production `HostSupplier` and `HostCheckout` and this checkout's real
engine. It archives this checkout like GitHub's source archive and starts one
disposable `ubuntu:24.04` container (`--pull never`) on an internal Docker
network (a private LAN address, no route out) with its home folder on a
disposable ext4 volume; container, volume and network are removed afterwards.
This PC downloads the .NET SDK and the gateway's NuGet packages for real (about
240 MB the first time, cached in `cacheDirectory`); the container gets them only
from this PC, through `docker exec` instead of SSH. It returns `{exitCode,
report: {passed, total, image, cache, steps: [{name, ok, detail}]},
notCovered}`. Steps: `checkout-archived`; `needs-read` (the engine's .NET SDK
matches `global.json`, the packages from the gateway's lock files);
`host-is-offline` (`HostCheckout.InternetProbe` says `internet=no`);
`online-setup-stops-plainly` (the online command, with no git, sudo or
internet, stops with `Stopped: git is not installed here` and never runs a
missing engine); `supplied` (the files arrive intact, `~/Martlet` is unpacked
and the source archive removed); `setup-without-internet` (`martlet-host --yes
setup` with `MARTLET_SUPPLY` builds the gateway, creates its identity and starts
it healthy); `again-sends-nothing` (a second pass sends nothing and removes the
installed SDK's archive); and `update-without-internet` (`update` rebuilds the
gateway offline). FIXTURE `systemctl` (runs the gateway unit's `ExecStart`) and
`ip` (the container's address) stand in for systemd and iproute2. Not covered:
Martlet's SSH runner itself, and roles or Docker hosts without internet access
(Martlet stops with an explanation). Without Docker or the image it returns
`exitCode` 2 and `notRun` (it never pulls).

`host_update_check` (no arguments) rehearses how one Martlet keeps its own host
service updates from colliding, with the desktop's production
`HostUpdateTracker` and `HostEngineBusy` reader, and returns `{exitCode, report:
{passed, total, steps: [{name, ok, detail}]}}`. The timeline is the one seen on
a host PC right after Martlet updated itself: *Update this PC's host service*
(a run window) claims this PC's host service (`keys`,
`run-window-claims-host`); the automatic pass then leaves that host to the run,
both as its local pairing and as this PC's own host service, with no second
engine run and no "busy" note, while another host still updates
(`automatic-pass-leaves-host-to-run-window`); overlapping routes (a run window
and a command from another computer) end separately
(`overlapping-routes-end-separately`); the engine's real busy line for another
update is named as *Another update of that host was already running ...* while
an install keeps *Waiting to update ... busy* (`busy-note-names-another-update`);
a host found current stops waiting and its stale note becomes *Updated to
Martlet 0.22.0 (seen at ...)* (once), while other hosts keep theirs
(`current-host-replaces-stale-note`, `retry-takes-waiting`); and *Update
hosts now* waits up to 30 minutes for another change, inside an unattended
run's 45-minute limit (`asked-update-waits`). Then, with the production
`OwnHostFollower`, this PC's own host service follows the app's version after
Martlet restarted into its update: an older running one is updated
(`own-host-follows-app-update`); with Docker not answering or the host service
stopped it is read again on the next minute's check
(`own-host-waits-until-it-runs`); another route updating it, this PC's own
pending update and a reply or speech being heard go first
(`own-host-gives-way`); a busy one is not read again for three minutes, then
updated (`own-host-busy-retries`); and once updated it isn't read again, while
a failed update isn't retried automatically for that version but the next
version is (`own-host-settles`). It contacts nothing and touches no
Docker, host or data directory; the engine side of waiting is
`host_engine_check`'s `asked-update-waits-then-runs`.

`app_update_check` (no arguments) rehearses how Martlet installs its own update,
with the desktop's production update helper (`AppUpdateHelper`: the same
`install-update.cmd` script and the same hidden start Martlet uses) in a
disposable temp folder, and returns `{exitCode, report: {passed, total, steps:
[{name, ok, detail}], notCovered}}`. A windowless process that exits after about
two seconds stands in for Martlet; `Martlet.NodeLinkCheck` (built with
`Martlet.Mcp`) stands in for the installer and for the restarted Martlet
(FIXTURE: with `MARTLET_UPDATE_CHECK_RECORD` set it records its arguments,
whether its console window shows and which processes share its console, writes
one line to the `/LOG` file and exits with `MARTLET_UPDATE_CHECK_EXIT`). Three
installs run side by side: one another computer asked for
(`asked-by-another-computer`), an automatic one from the notification area whose
installer fails with exit 5 (`automatic-from-tray-fails`) and one you confirmed
(`confirmed`). For each, the steps check that the installer starts only after
Martlet's process exited (`waits-for-martlet`, with how long after: about a
second, since the helper looks again at once for its first rounds and then waits
one second for what Martlet started to let go of its files); its switches
(`installer-switches`: `/VERYSILENT` with no window at all for the unattended
two, `/SILENT` with only the progress window when confirmed, always
`/SUPPRESSMSGBOXES /NORESTART /SP- /TASKS=` and `/LOG=...\install.log`); that it
runs inside the helper's console, which shows no window (`helper-hidden`); that
Martlet starts again after it with `--data-directory`, plus `--after-update`
(minimized, no focus) when unattended and `--tray` when it was in the
notification area, without a console window of its own (`restarts`); that
`last-install.txt` holds the exit code and version (`records-result`); and that
`update.log` has every step (`logs-steps`). After the failed install the
installer log's tail, which Martlet copies into its log, is checked too
(`installer-log-for-failure`). It installs nothing, starts no real Martlet and
contacts nothing; the real Inno Setup installer is not run (`notCovered`). What
Martlet does with these files when it starts again shows in `logs_tail`
(`Update helper: ...` lines, then *Martlet updated to ...* or a WARN *The update
to ... didn't finish* followed by the installer log's last lines), and
`ui_snapshot`'s `windowStates` shows the restarted window `minimized` and not
`foreground` (launch the desktop with `-DesktopArguments '--after-update'`).
Steps `resume: ...` check the note Martlet leaves itself as it closes to install
(`updates\resume.txt`, `AppUpdateResume`), so the restarted Martlet shows the
character and starts listening and watching again: it is read once
(`picks-up-character-and-listening-once`), carries only what was on
(`only-what-was-on`, `watching-alone`), still reads a note from a Martlet that
didn't save watching yet (`reads-note-without-watching`), leaves nothing when nothing was on
(`nothing-on-leaves-no-note`) and is ignored after 15 minutes, when you started
Martlet yourself rather than the update (`stale-note-ignored`).

**Automatic installs in the desktop.** With *Install updates automatically*
(`AutomaticUpdateInstall`), a downloaded update installs at once, even with
Martlet's window in front, other Martlet windows open, the character showing
or always listening on. It waits only for a reply, speech being heard or
transcribed, a modal question, or work exiting would cut short; then
`AppUpdateStatus` reads *Martlet x.y.z is downloaded and installs as soon as
Martlet isn't busy. Waiting: <what>.* and the next one-minute tick tries again.
Installing logs *Installing Martlet x.y.z automatically, with no installer
window. Martlet closes and restarts into it[, showing the character, listening
and watching again].* (only what was on), and the restarted Martlet logs *Martlet restarted after its
update and is showing the character[, listening and watching] again, as before the
update.* To exercise this without GitHub or a real install, set
`MARTLET_SIMULATE_APP_UPDATE` to a version newer than the build (for example
`9.9.9`) before launching the desktop (FIXTURE): checks find that version
without contacting GitHub, its download is a small text file, and installing it
runs the real update helper, whose stand-in installer fails at once (Windows
can't run it), so nothing is installed; the helper records the failure and
starts Martlet again (`--after-update`, same data directory), which reports
*The update to 9.9.9 didn't finish* and doesn't install it automatically again
that session. Turning on `AutomaticUpdateInstall` (`ui_toggle`) saves
`updates.json`, so it needs `--allow-ui-effects`. Every download is a
background task, *Download Martlet x.y.z*: out of sight when Martlet downloads
by itself, in its run window when you chose Install. `TaskState-<id>` reads its
percentage while it runs and *Done at ... Martlet x.y.z is downloaded and ready
to install.* at the end, and its Cancel task stops the download (`AppUpdateStatus`
*The download of Martlet x.y.z was canceled.*). With the fixture the download
ends at once; to keep Martlet from installing it while you look, launch with
`MARTLET_SIMULATE_BACKGROUND_TASK` too: `AppUpdateStatus` then reads *... Waiting:
simulated background task (in Background tasks).*

**This PC's own host service after an update.** On a PC that runs a host
service (a host PC, or one paired with its host service on Docker Desktop),
Settings › App updates shows `OwnHostUpdateStatus` (returned; hidden on other
PCs): *This PC's host service runs Martlet x.y.z, like this app...*, *Updating
this PC's host service from Martlet a.b.c to x.y.z in the background. Martlet
stays usable...*, *...is busy (<what>), so updating it ... waits; nothing was
changed. Martlet tries again at <time>.*, *Updated this PC's host service from
Martlet a.b.c to x.y.z at <time>...*, stopped, not running, or why the update
stopped. Martlet starts it right after its own startup (after an update, once its
window is back) and looks again every minute, whatever `AutomaticHostUpdate`
says; `logs_tail` shows *Updating this PC's host service from Martlet a.b.c to
x.y.z in the background.* and *Updated ... (n s).*, and `host-runs` the run
(*Keep this PC's host service current*). That run is a background task out of
sight: `TaskTitle-<id>` *Keep this PC's host service current*, `TaskState-<id>`
*Running for 1 s. Updating this PC's host service from Martlet a.b.c to Martlet
x.y.z...*, then *Done at ... This PC's host service runs Martlet x.y.z.* A try
that finds the host busy changes nothing and leaves Background tasks. Its
Cancel task stops it, and `OwnHostUpdateStatus` then reads *You canceled
updating this PC's host service to Martlet x.y.z; nothing more changes by
itself...*: Martlet doesn't start it again by itself for that version. To exercise it without touching a real
host service, set `MARTLET_SIMULATE_OWN_HOST` to an older version (for example
`0.37.0`, or `0.37.0,busy` to have the first try find the host busy) and
`DOCKER_HOST` to an unused named pipe before launching the desktop, on a
disposable data directory holding `device-role.txt` with `Host` (FIXTURE): this
PC then reads as running a host service of that version, and its update takes 20
seconds, contacts nothing, changes nothing and logs `FIXTURE` lines in
`host-runs`; a busy try is repeated about a minute later. With
`-DesktopArguments '--after-update'` the window stays minimized (`ui_snapshot`
`windowStates`) while `ui_click` navigation keeps working during the update.

`audio2face_check` animates a short synthesized speech-like test signal (a vowel
pulse train generated in the tool, never microphone audio, nothing played) with
an Audio2Face service on a numeric loopback `endpoint` (default
`http://127.0.0.1:52000`; `seconds` 1-10, default 2; `sampleRate` 16000, 24000,
44100 or 48000, default 24000) through the production `Audio2FaceAdapter`, the
client the host gateway's lip-sync relay uses. It works with either engine of the
`audio2face` host role ([local open-source SDK service](../workers/audio2face/README.md)
or NVIDIA's NIM) and returns `ok`, `frames`, `framesPerSecond`,
`firstFrameSeconds`/`lastFrameSeconds`, `channels` (blendshapes returned) and
`movingChannels`, `jawOpenPeak`, the `strongest` channels with their peaks,
`firstFrameMs` and `elapsedMs`, or `ok: false` with the client's `failure`
category (for example `DeadlineExceeded` or `TransportFailure` when nothing answers, `InvalidProtocol`
for a malformed reply). A role service on a host listens only inside the host's
own loopback, so check it there or through a forward to this PC's loopback.

`voice_engine_check` speaks one sentence with a self-hosted voice engine's
loopback service through the production path: Martlet.NodeLinkCheck's
`voice-engine` mode starts a real gateway on 127.0.0.1 (pinned TLS, pairing)
with that engine's own relay (the one the Linux host creates for the role) and
speaks through the desktop's paired client, with the first starter voice whose
length the engine accepts as the reference. Nothing is played or recorded.
Arguments: `engine` (`chatterbox` default, `chatterbox-original`,
`chatterbox-nano`, `f5`, `xtts`, `gpt-sovits` or `dia`), a numeric loopback
`endpoint` (default the role's port: 50083, 50089, 50088, 50080, 50081, 50082
or 50084), optional `text` (at most 300 characters; default a sentence with the
engine's first sound tag, such as `[laugh]`, when it has tags, or with an
`[expressive]` second sentence for Chatterbox Original) and optional
`dataDirectory`: for `chatterbox-original` the check sends the style saved there
(`chatterbox-style.json`, as Companion › Voice saves it), else Resemble's
suggestions, exactly as the desktop does. It returns `{exitCode, report}` with `ok` (no failure, at least 0.5 s
of audible audio), `engine`, `route`, `voice`, `text`, `style` (Chatterbox
Original's sent style in words, else null), `statusBefore` and
`statusAfter` (the service's own `/status`: `answered`, `state`, `ready`,
`error`, `model` and `device` (`cuda:0` or `cpu`) and `runtime`, for Chatterbox
its torch, torchaudio and CUDA versions, `decoderSteps` (the decoder steps
each decoding takes on Chatterbox Turbo or Nano: 1 on the CPU, 2 on a GPU),
`streaming` (Turbo and Nano: `on`, whether pieces are spoken as they are made;
`hold_tokens`, the most speech tokens each chunk but the last keeps back for
the next decoding: 8 on the CPU, which keeps back fewer when a piece's timing
needs it, and 3 on a GPU; on the CPU also `first_tokens`, the speech tokens
before a piece's first chunk (55, 0 for whole pieces), and what the service
measured to time its chunks: `token_ms`, T3's time for a speech token, and
`decoding_scale`, decoding times against the expected shape; null on a GPU),
`cpu`
on the CPU (`threads`, PyTorch's threads, at most 8 and never more than the
performance cores, and `pinned_cpus`, the CPUs of the performance cores it is
pinned to on native Linux, empty when not pinned, as always on Docker Desktop; null on a GPU)
and `idleCheck` (`checks`, `every_seconds`, `fastest_ms`, `last_ms` of its
[idle check](CHATTERBOX_VOICE.md#how-it-runs)) and, for Chatterbox
Original, `style` (`default`, `expressive_parts` and `last`, the style the last
reply asked for, so the owner's values can be checked at the service), or why
it could not be read), `seconds` of 24 kHz audio, `firstAudioMs`,
`elapsedMs`, `realTimeFactor`, `pauses` and `pauseMs` (what a listener who
plays the first audio at once would hear: the pauses longer than 20 ms, when
audio arrives after everything before it has played, and all pauses together;
0 for a piece that streams in time or comes whole), `peakDbfs`, `rmsDbfs`, `audible`,
`voicedShare` (the share of the loud 40 ms frames that have a pitch between 70
and 400 Hz, from `Martlet.Core.Audio.Voicing`; about 0.6-0.9 for ordinary
speech and nearly 0 for a whisper, so a `text` that starts with `[whispering]`
shows if the Chatterbox model itself whispered; Martlet adds no
[whisper of its own](CHATTERBOX_VOICE.md#tags)), and `failure`
and `problem` (the client's error code and message, for example
`worker.unavailable` when nothing answers or the model could not load). A
loading model can take minutes, so the tool allows six; pass
`-TimeoutSeconds 400` to the script. As with `audio2face_check`, a role
service on a host listens only in the host's loopback, so run it there or
forward the port.

`listening_engine_check` is Listening on another computer, headless: the `stt`
host role's live speech-to-text service on a numeric loopback `endpoint`
(default `http://127.0.0.1:8178/`; whisper.cpp or Martlet's
[Parakeet service](../workers/parakeet/README.md)) transcribes phrases a
Windows voice says (System.Speech rendered to memory after 0.3 s and before 1 s
of faint noise, never played; optional `phrases`, up to 8 English sentences)
through the production path: Martlet.NodeLinkCheck's `listening-engine` mode
starts a real gateway on 127.0.0.1 (pinned TLS, pairing) with the role's relay
(`SttRelayWorker`, the one the Linux host creates) and transcribes through the
desktop's paired client. Optional `model` names the route's model; by default
the one the service's `/status` names (Parakeet's), else `small`. It returns
`ok` (every phrase back with at most 20% word errors overall),
`wordErrorRate`, `medianTranscribeMs`, `endpoint`, `route`, `model`,
`modelRevision` (`sherpa-onnx-1.13.8` for a Parakeet model, `whisper.cpp-1.9.4`
otherwise), `status` (the service's own `/status`: Parakeet's `engine`,
`model`, `threads` and `runtime` versions; whisper.cpp has none), each phrase's
`said`, `transcript`, `wordErrors` and `transcribeMs`, and `failure` and
`problem` (for example `worker.unavailable` when nothing answers). Nothing is
recorded, played or kept. As with `audio2face_check`, run it where the role
listens or forward the port.

`singing_check` makes one song through the singing role's production path
([Singing](SINGING.md)): Martlet.NodeLinkCheck's `singing-check` mode starts a
real gateway on 127.0.0.1 (pinned TLS, pairing) with the role's own relay
(`SongRelayWorker`, the one the Linux host creates), merges the starter voices
into the gateway's shared speaking-voice list as a paired desktop does, and makes
the song through the desktop's paired client (`MakeSongAsync`, what `SongClient`
runs), sending the voice's recording once when the gateway lacks it. Arguments:
`endpoint` (`fixture`, the default, starts this checkout's `workers/singing`
service with the FIXTURE - NOT AI engine using the `python` on `PATH` or
`MARTLET_PYTHON`; or a numeric loopback address of a live singing service, for
example `http://127.0.0.1:50085/`), `seconds` (15-180; default 20 for the fixture,
30 otherwise), `quality` (`fast` or `high_quality`) and `voiceMatch` (`soulx` or
`vevosing`), and optionally `voiceRecording` (the absolute path of a copy of a
mono 16-bit PCM WAV, 1-30 s, such as one of the owner's voice recordings) with
its `voiceTranscript`, which the check adds to the gateway's voice list as the
desktop adds a recorded voice and sings in instead of the starter voice, and
`bpm` (default 90; 0 for none) and `key` (default `G major`; empty for none),
sent as Martlet's own model writes them (without either, the host's music
planner runs first). It returns `{exitCode, report}` with `ok` (no failure, three
sample-aligned tracks of the requested length and a beat grid), `stages` (each
stage the client saw, with its fraction and `atMs`), `elapsedMs`,
`realTimeFactor`, `song` (`engine` with `Fixture`, `seconds`, `bpm`, `key`,
`beatsPerBar`, `beats` and `downbeats` counts with the first downbeats, `words`
with `wordTimingSource` and the first timed words, `lines` with the first timed
lines and their sections, `stageTimings` from the host plus `Delivering`, and
`mix`/`vocals`/`backing` with sample rate, channels, seconds, peak and RMS),
`host` (what the host measured beyond the contract: `wordTiming` with the median
distance from word starts to vocal onsets before and after snapping,
`lyricTimingSource`, `vocalBleedDb` (how loud the sound removed outside the sung
phrases was, relative to the singing), `peakVramMib`, `plannedBpm`, the engine's
planner, quantization, whether the music model stayed on the card (`dit_resident`)
and is still warm there (`dit_warm_after`), the voice match's own report (`match`:
octave shift, singer and voice pitch, load, pitch and convert seconds, whether
its process was `warm`), and the raw stage timings, with `lyric_timestamps`), `saved` (with
`saveDirectory`, an absolute disposable folder, the check writes `mix.wav`,
`vocals.wav` and `backing.wav` there), `statusBefore`/`statusAfter` (the service's status through the
gateway: state, engine, voice matches, queue, whether its worker holds the
graphics card, the card's used and total memory, and the number, total size and
licences of its models) and `failure`/`problem` (a `SongException` code such as
`voice.missing`, `singing.busy` or `singing.unavailable`, or the gateway's).
Nothing is played. A real song with models still loading can take minutes, so
the tool allows 20; pass `-TimeoutSeconds 1300` to the script. The .NET 10 runtime
must be where `Martlet.NodeLinkCheck.exe` finds it (set `DOTNET_ROOT` when it is
not installed system-wide).

With `dataDirectory` (the absolute path of a desktop data directory paired with
a host, so it has `hosts.json`) `singing_check` goes through that real paired host
instead, exactly as the desktop's `SongClient` does: the first host in
`hosts.json` whose gateway offers the song route (or the one named by `host`), its
own gateway (pinned TLS, signed with the pairing secret the desktop saved in
Windows Credential Manager, used only to sign the requests and never returned),
its singing role, and `voiceId` (a voice ID from that gateway's shared voice list,
or a unique prefix of 8+ characters such as `7fa3706c`; default the list's first
live voice). Nothing is added to the host's voice list. When the host lacks that
voice's recording it is sent once, from `voiceRecording` or the data directory's
voice store (`f5-voices/audio/<preset>/<voice ID>.wav`), checked against the voice's
SHA-256. This is how to make a song with a singing role that runs in Docker on
this PC or another computer, whose service listens only inside its gateway's
network. The report adds `paired`: the host, route and model, the voice (ID,
name, seconds, rights, whether its recording was already on the host) and the
hosts skipped. `endpoint` is ignored then; `voiceId` and `host` need
`dataDirectory`.

`singing_status` reads a singing service's own `/status` over a numeric loopback
`endpoint` (default `http://127.0.0.1:50085/`): `answered`, `state`
(`not_provisioned`, `ready`, `loading`, `busy`), `ready`, `engine` (`song` or
`fixture`), `qualities`, `voiceMatches`, `queue`, `running`, `workerRunning`
(whether the worker process holds models), `restarts`, `idleReleaseSeconds`,
`gpu` (used and total MiB), `evidence`, `sources` (pinned commits) and `models`
(each pinned file's ID, revision, licence and size) with `modelBytes`, or why it
could not be read. With a `dataDirectory` (the script passes its disposable one)
it adds `choices`, the Singing card's saved quality and voice match
(`singing.json`; the defaults when none are saved), `host` (the computer the
desktop last saw running Singing) and `setUp` (that computer is still paired:
what `SongClient.IsSetUp` answers without the fixture). When that data directory
is paired with hosts (`hosts.json`), it adds `paired` (Martlet.NodeLinkCheck's
`singing-status` mode): for each paired host, read through its own gateway as the
Singing card reads it (the pairing secret from Windows Credential Manager only
signs the requests), `reachable`, `offersSinging`, `model` and `service` (the same
status fields as above plus the models' count, total bytes and licences), or the
`problem`; and `thisPcDocker`, what this PC's Docker shows of the role:
`containers` (name, image, state, status), `images` (`martlet-singing` tags,
sizes), `modelsVolume` (whether `martlet-singing-models` exists) and
`setupRunning` with `setup` (a `martlet-host add singing` engine session running
now, its image and how long: a setup or *Add VevoSing there* in progress). A role
in Docker listens only inside its gateway's network, so `paired` is how to read
it from this PC. Read-only. As with `voice_engine_check`, a role service on a host
listens only in the host's loopback, so `endpoint` reads it only there or through
a forwarded port.

`virtualization_status` reports whether Windows is ready for Docker Desktop's
WSL 2 engine, from the same read-only checks the desktop runs before it starts
Docker Desktop (optional absolute `dataDirectory`, default the current user's):
`ready`, `blocked`, `firmwareOff`, `needsWindowsChanges`, `problems` and
`recovery` (plain words), `probeIssues` (which checks could not be read),
`firmware`, `hypervisor`, `virtualMachinePlatform` and
`windowsSubsystemForLinux` (`Enabled`, `Disabled`, `Absent` or `Unknown`),
`wsl` (version, `none` or null), `wslStatus` (`Available`, `Unavailable`,
`RestartRequired`, `Failed` or `Unknown`), `wslStatusExitCode`,
`hostComputeService` (`vmcompute`) and `hostNetworkService` (`hns`), each
`Running`, `Stopped`, `Disabled`, `Absent` or `Unknown`, `restartPending`
(Windows component servicing or Windows Update needs a restart, or null when
unreadable), `restartRequired`, `virtualMachine`, `summary`,
`dockerDesktop {installed, running, engine, failedStartCheck,
windowsCanFixFailedStartCheck, failedStartCheckNeedsWindowsChanges}` (`engine` is what
`docker desktop status` reports, for example `running`, `starting` or
`stopped`, or null when Docker Desktop doesn't answer within 15 seconds;
`failedStartCheck` is the Windows check Docker Desktop's engine last failed in
its current session, in Docker's words from its warning and error log, for
example `Virtual Machine Platform not enabled` or `No virtualization available`
(its window then says *Virtualization support not detected*), or null;
`windowsCanFixFailedStartCheck` is true when that check names something
Martlet's Windows setup installs (WSL missing or too old, for example
`checking WSL version: wsl is not installed`, or Virtual Machine Platform not
enabled), and `failedStartCheckNeedsWindowsChanges` when, in addition, the
Windows checks could not read that fact themselves (they timed out or were
unavailable): Docker Desktop's word then counts, `needsWindowsChanges` is true
and `problems` quotes it, so a slow Windows probe never hides missing WSL; a
run window then sets up Windows (one administrator prompt) instead of
restarting Docker Desktop; a
run window restarts Docker Desktop once when it is open but its engine stays
`stopped` at two checks in a row or its start check failed while Windows is
ready, and always after Martlet changed Windows for
it) and `continueSetup {pending, kind, task,
created, startsAtSignIn}`: the setup Martlet continues after a Windows restart
(`continue-setup.json` in the data directory, and whether the per-user `RunOnce`
entry that starts Martlet at the next sign-in exists). It reads CIM facts,
Windows services and pending-restart registry markers and runs `wsl --version`
and `wsl --status` in a hidden Windows PowerShell, plus `docker desktop status`
and `docker desktop logs --boot 0 --priority 1`. It changes
nothing, starts no Linux VM and returns no paths or distribution names.
`Available` means WSL's status command answered without a recognized WSL 2
problem, not that a VM or GPU workload was tested. WSL 2 unavailability is
recognized even when that command exits **0**; a WSL 1-only warning is not a
WSL 2 blocker. A service that is merely stopped is not missing or disabled:
Windows can start it on demand. An unrelated pending Windows update alone
does not block otherwise-ready WSL. When features report enabled but their
runtime is unavailable and Windows has a restart pending, `restartRequired`
is true and `needsWindowsChanges` false: restart Windows rather than
reinstalling features or repeatedly restarting Docker Desktop.

`host_service_status` reads this PC's own Martlet host service on Docker
Desktop (the one the host dashboard sets up) with the same production code as
the dashboard (`LocalHostService` in `Martlet.Core`): `stage` (`DockerMissing`,
`DockerNotRunning`, `NotSetUp`, `Stopped` or `Running`), `ready` (running,
answering and still at one of this PC's addresses), `version` (the
`martlet-host:x.y.z` image), `hostId`, `published`, `addressOnThisPc` (false
once the network gave this PC another address than the one set up),
`answering` (a TCP connect to the published port), `roles` (the installed role
records, for example `["audio2face","f5","stt"]`; null until the gateway runs),
`network` (`unbound`, `bound`, `removed` or `unreadable`), `desktops` (`{id,
name}` of the active desktops in the host's Martlet network, that is the
computers paired with it, including this PC when it is one), `sharedGpu` (the
warning Companion › Voice and Devices show when the host's voice engine shares
this PC's graphics card with its other roles, or null; see
[Chatterbox](CHATTERBOX_VOICE.md#sharing-the-graphics-card)) and `problem`
(Docker's first error line). It runs `docker container inspect` on
`martlet-host-gateway` and `martlet-host-net` and one `docker exec` that lists
the role records and prints `host_id` and `network.json`; it never reads the
agent token, keys, secrets or pairings, returns no addresses and changes
nothing. Setting `DOCKER_HOST` to a missing named pipe gives `DockerNotRunning`.

`logs_tail` reads the last `lines` (1-400, default 100) of one local log under
`<dataDirectory>\logs` (`log`: `desktop` (default), `avatar-renderer` or
`host-runs`), optionally only lines that `contains` some text (case-insensitive,
at most 200 characters). It returns `{log, exists, truncated, lines}` and never
writes, rotates or deletes a log. Failed provider requests appear in the desktop
log with their endpoint, model, HTTP status and the provider's own short
explanation, followed by a `Reply failed (...)` line naming the route (`Spoken reply failed (...)` naming the voice
route when the text arrived but speaking it failed), for
example `{"name":"logs_tail","arguments":{"contains":"failed"}}`. Each exchange
Martlet couldn't remember logs `Remembering failed (<code>)` (a provider failure
such as `RateLimited`, or a store problem such as `memory.Busy`), and an empty or
declined answer to the memory request logs `Remembering got no usable answer
(...)`; read them with `{"name":"logs_tail","arguments":{"contains":"Remembering"}}`.
Logs can
include local paths and provider error text (never keys or conversation content).

`mcp_servers_status` reads `mcp.json` from a data directory (optional absolute
`dataDirectory`, default the current user's) as the desktop parses it: `state`
(`none`, `loaded`, `invalid` or `unreadable` with `problem`) and per server its
`name`, `transport`, `command`, raw `args` (with `${env:...}` and
`${secret:...}` references, never their values), `host` for HTTP servers, `env`
and `headers` names, `disabled`, `autoApproveAll`, `autoApprove`, the MCP
directory `registry` and `registryVersion` it was installed from, the `secrets`
names it uses and its `problem`. It starts no server and reads no credentials.

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
`messaging.json` (this PC only, never synced): `state` (`none`, `loaded` or
`unreadable` with `problem`) and `telegram` with `connected` (a bot is set
up), `enabled` (Martlet answers it on this PC), `bot` (its username),
`botName`, `tokenSaved` (never the token), `chats` (how many chats are paired;
never their names or IDs) and
`speakReplies`, and `whatsApp` with `connected` (a number is set up),
`enabled`, `number`, `name`, `appId`, `businessAccountId`, `phoneNumberId`,
`port` (Martlet's localhost webhook port), `publicAddress` (the owner's own;
empty with `quickTunnel` true for a Cloudflare quick tunnel), `secretsSaved`
(never the access token or app secret), `chats` and `speakReplies`. The secrets
live in Windows Credential Manager and are never read. In the desktop,
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
the check boxes, and WhatsApp's `MessagingWhatsAppConnect`,
`MessagingWhatsAppGetCloudflared` (a download, after its confirmation), Meta
links, `MessagingWhatsAppPairOpen`, `MessagingWhatsAppDisconnect`,
`MessagingWhatsAppOn` and `MessagingWhatsAppSpeak` need
`--allow-ui-effects`. `ui_set_text` fills `MessagingWhatsAppToken`,
`MessagingWhatsAppSecret`, `MessagingWhatsAppPhoneId`,
`MessagingWhatsAppAccountId` and `MessagingWhatsAppAddress`. To verify WhatsApp
without Meta, launch the desktop with `MARTLET_WHATSAPP_API` set to a loopback
fake Graph API that performs the webhook check on `POST /<app>/subscriptions`,
seed `messaging.json`'s `WhatsApp.Port`, and use `http://127.0.0.1:<port>/` as
the public address; the fake can then post a signed delivery to the callback
and record the reply on `POST /<phone>/messages`.

`prompts_status` reads Companion › Prompts from a data directory's
`settings.json` (optional absolute `dataDirectory`, default the current
user's): `state` (`none`, `loaded` or `unreadable` with `problem`),
`total`, `edited` and `emptied` counts, and every internal prompt Martlet
sends to the Thinking model (`id`, `group`, `title`, `placeholders`,
`state` `builtin`, `edited` or `empty`, `characters` and `tokens`), plus
`tokens`, the estimate for all prompts together. Token counts are Martlet's
own request-size estimate (`BoundedTextInput.TextTokens`: about a token per
three UTF-8 bytes, no message overhead; 0 for an emptied prompt), not a
provider's count, and cover each prompt as written, before placeholders are
filled in. With an
`id` it also returns `prompt` with that prompt's effective `text` (the
saved edit or the built-in text), exactly what Martlet fills in and sends.
`shortFirstSentence` is Companion › Replies › *Short first sentence*: `on`
(on by default), `chosen` (whether a choice is saved), the prompt's `state`,
and `spokenClosing` and `unspokenClosing`, what closes a spoken and an
unspoken reply's instructions, built by the desktop's own
`PromptSettings.ReplyClosing` (the short first sentence prompt, then *Reply
length*; *Reply length* alone when it is off, emptied or the reply isn't
spoken). On Companion › Replies, `RepliesShortFirstSentence` reads the chosen
option (*On* or *Off*; choosing one with `ui_select` saves it, so it needs
`--allow-ui-effects`) and `RepliesNow` says whether spoken replies start with a
short first sentence.
`adultContent` is Companion › Replies › *Adult content*: `on` (off by
default), the *Adult content* prompt's `state`, and while it is on the
`instructions` it adds after the *One moment* prompt of every reply and screen
remark (never in a Discord call), exactly as the desktop sends them.
`RepliesAdultContent` reads the chosen option (*Off* or *On (18+)*; choosing
one with `ui_select` saves it, so it needs `--allow-ui-effects`), and
`RepliesNow` adds *Adult content is on.* while it is.
On the page, `PromptsNow` reads how many prompts are edited or emptied,
`PromptsTokens` the estimated tokens of all prompts together as typed
(*All prompts together: about 3,456 tokens. ...*) and
`PromptState-<id>` each prompt's state (*Built-in text. About 52 tokens.*,
*Edited. About 52 tokens.*, *Empty: nothing is sent for this prompt.*, plus
*Saving...* while an edit is still being saved); none returns prompt text.
`OpenPrompts` (Personality's *Edit
prompts*) only opens the page. There is no Save button: an edit saves a moment
after typing stops (or at once when another page opens), into the newest saved
settings, and `PromptsNow` then reads the new counts. The editors
`Prompt-<id>`, their `PromptReset-<id>` buttons and `PromptsDefaults` write
settings, so they need `--allow-ui-effects`; `ui_set_text` with an empty
`text` empties a prompt.

`character_status` reads Companion › Personality and Character as saved in a
data directory (optional absolute `dataDirectory`, default the current
user's): `personality` (`state` `none`, `loaded` or `unreadable` with
`problem`; `active`, the persona Martlet uses; and each persona's `name`,
`active`, `instructionCharacters` and `speechBreaks`
(`periods`, `questionMarks`, `exclamationMarks`, `shortEndingWords`
and `isDefault`), never its
instructions), `character` (from `avatar.json`: `model` `built-in` with
`builtInCharacter`, or `own model` with `ownModelType` `.vrm` or
`.model3.json`, never the path; `renderer`, `lipSync`, `autoShow` and
`lipSyncHost`, the paired host's ID), `placement` (from
`character-placement.json`, this PC only: `state` `none` when no position is
saved (the character shows at its default spot), or `loaded` with `locked`,
`left`, `top`, `width` and `height` in device-independent pixels, and
`screen` (the monitor's device name, such as `\\.\DISPLAY2`), `screenLeft` and
`screenTop` (its spot on that monitor's work area); see the character overlay below), `clickThrough`
(from `character-click-through.json`, this PC only: `state` `none`, `loaded`
or `unreadable` and `on`, whether clicks pass through the character; see the
character overlay below), `voice`
(from `talk-preferences.json`: `state` `none`, `loaded` or `unreadable`,
`speakReplies`, Companion › Voice's *Speak Martlet's replies aloud*, on unless
saved off, `muted`, its opposite, which the overlay menu's *Mute voice* and
*Unmute voice* change, and `volume`, Companion › Voice's *Voice volume* from 0
to 1, full unless saved lower; see below) and
`lorebooks` (`books`, `on` and
`entries` counts). Those editors have no Save button; each change saves on its
own into the newest saved file, keeping what was saved elsewhere meanwhile
(another page, or sync from your other computers, such as the lip-sync host).
`OpenCompanion` (Personality's *Edit personality*), `OpenAvatar` (Character's
*Choose and customize*), `OpenLorebooks` and `OpenMemory` open their windows
(the character itself doesn't show), and their `CompanionClose`,
`AvatarClose`, `LorebookClose` and `MemoryClose` close them; these are passive
clicks, as are the character window's `AvatarAdvanced` and
`RemoteHostSection` expanders. Companion > Memory's *Conversation history* card
shows `HistoryStatus` (text: whether Martlet keeps a record and may search it,
and how many conversations and exchanges it holds since when; never what was
said) and the checkboxes `HistoryKeep` and `HistorySearch` (toggling either
saves `conversation-history.json`, so it needs `--allow-ui-effects`).
`OpenHistory` opens the history window and `HistoryClose` closes it;
`HistorySearchRun` and `HistoryShowAll` only filter what it lists, and
`HistoryEditMessage` only opens the editor (`HistoryEditCancel` closes it). Its
`HistoryWindowStatus` reads as text (counts per app, or what a search or the
last change did) and `HistoryPlatformStatus` reads the changes waiting for
Telegram and Discord (*2 changes waiting for Discord (not connected; they go
once it is).*). `HistorySearchText` and `HistoryEditText` take text through
`ui_set_text`; `HistoryAppFilter` (*All apps*, *This PC*, *Telegram*,
*Discord*, *WhatsApp*), `HistoryConversations` (items named *Conversation 2
(Telegram)*) and `HistoryMessages` (items named *Message 3: Martlet ·
Discord*) take `ui_select`; what was said is not a readable value.
`HistoryAlsoThere` (on) makes deletes and edits also queue changes for the
apps. `HistoryEditSave`, `HistoryDeleteMessage`, `HistoryDeleteConversation`,
`HistoryDeleteAll` and `HistoryPlatformCancel` write (deletes ask first, No by
default) and need `--allow-ui-effects`. Each editor's footer line, `CompanionSaveState`,
`AvatarSaveState` and `LorebookSaveState`, reads *All changes saved.*,
*Saving...*, *Not saved yet: <why>* (for example an empty persona name, or
*Choose your model file: an existing .vrm or
.model3.json file.*) or *Not saved: <why>*; `AvatarStatus` reads the
character's state (*Character is showing. ...*, *Character hidden.*). Memory's
`MemoryFactStatus` reads how many facts it remembers, how many belong to how
many people Martlet knows by voice and how many to forgotten voices, how many
its *Show* choice (`MemoryPersonFilter`) and search (`MemorySearch`, set with
`ui_set_text`; it only filters the list) list (*Showing N.*) and what the last
action did, never a fact or a name; `MemoryStatus` (its bottom line) reads
whether memory is on, is saving or why it can't be (*Memory is on.*, *Saved.
Memory is off: ...*), never a fact or a folder. The window reads its facts the
moment it opens, even while a reply holds the setup slot, and follows facts
remembered, changed or forgotten elsewhere (remembering after a reply,
`manage_memories`) on its own, so `MemoryFactStatus` changes without
`MemoryReload` (*Refresh*). `MemoryNewFact` (clears the fact editor) and
the `MemoryStorageSection` and `MemoryExportSection` expanders are passive
clicks; `MemoryDeleteFact` (the selected fact or facts), `MemoryDeleteShown`
(every fact listed now: one person's or what the search found) and
`MemoryDeleteAll` ask first and need `--allow-ui-effects`. Their
fields (`CompanionName`, `CompanionText`,
the *Where the voice pauses* check boxes
`CompanionBreakPeriods`, `CompanionBreakQuestions` and
`CompanionBreakExclamations` (their `checkedState` is the persona's choice) and
`CompanionShortEnding` (its value reads *Never*, *1 word* or *Up to N words*),
`CompanionPersona`, which also makes the chosen persona the one Martlet uses,
`CompanionNew`, `CompanionDuplicate`, `CompanionDelete`, `CharacterChoice`,
`AvatarModelPath`, `LipSyncChoice`, `AutoShowCharacter`, the lorebook fields,
`MemoryEnable` and Memory's fact fields: `MemoryFactContent`, `MemoryPerson`
(*Belongs to*), `MemoryRetention`, `MemorySaveFact`, `MemoryEditFact`) save, and
choosing in `MemoryPersonFilter` (it only filters the list) still goes through
`ui_select`, so these need `--allow-ui-effects`; `ShowCharacter`/`StopAvatar` show or hide the
character, so they need `--allow-ui-effects` too. Typing a persona name or text and
closing at once still saves it; closing with a change that can't be saved asks
with `ConfirmationYes` (close and drop it) or `ConfirmationNo` (stay). With the
character showing, choosing another model or lip-sync mode switches it right
away (`AvatarStatus` changes, and `logs_tail` `desktop` records *Avatar renderer
stopped by Martlet.* for the old one).

`hearing_check` shows whether the Thinking model can hear the user's
recording (Companion › Listening › **Let Thinking hear my voice**; optional
absolute `dataDirectory`, default the current user's, and optional
`modelId` to classify instead of the saved Thinking model): `model`,
`source` (`argument`, `settings` or `default`), `settings` (`none`,
`loaded` or `unreadable`), `routeType`, `localOllama`, `modelHearing` (the
name-based `Supported`, `Unsupported` or `Unknown`), `routeHearing` (the saved
route's, the production decision `HearingModelCatalog.ForRoute`: only Chat
Completions endpoints take audio, Ollama on this PC included, then what
`model-abilities.json` says about the model, then its name; null with
`modelId`), `savedAbility` (what Martlet found out about the saved model:
`Hears`, `Sees`, `Source` and `CheckedAt`, or null), `hearVoice` (whether
Thinking hears your recording as replies decide it: your choice, or, never
chosen, on only while the recording stays on this PC), `hearVoiceChoice` (`on`,
`off` or `unset`; a `false` saved before talk-preferences version 4 counts as
`unset`), `staysOnThisPc` (the saved route is Ollama on this PC,
`http://127.0.0.1:11434/v1`, with a model that isn't a `:cloud` or `-cloud` tag), `hearVoiceWhy`
(in words), `voicePath` (Companion › Listening › **When Thinking
can hear you**: `straight`, the default, or `transcribeFirst`),
`straightApplies` (always listening sends the recording alone right away: the
choice is on, straight and the route hears) and `lastTurn`: which way the newest
spoken reply's message went, from the desktop log (`path` `straight` or
`transcribeFirst` and its *Voice path* `line`; for a straight one
`transcriptReadyAfterReplyStartMs` and `speechToTextMs` from the *Background
transcript ready* line, `wordsLine`, the *Straight to Thinking:* line saying
where the words went: the conversation, its record, remembering; never the
words, and `notWords`: `dropped` when the quick check of something short found
it wasn't words and the reply was dropped before it played, `tooLate` when the
reply had begun, with its `notWordsLine`), or null before one. Its `fixture`
rehearses the production Chat Completions adapter against a canned endpoint
on 127.0.0.1 (NOT AI) with a 1.5 s synthesized speech-like clip (never
microphone audio, nothing played): `withRecording` (outcome, the user
message's `contentParts` `text` and `input_audio`, `audioFormat` `wav`,
`wavValid`, `audioSeconds`, `audioBytes`), `withoutAudioPermission`
(`ConsentMissing` with `requestsSent` 0: text permission never covers the
recording) and `transcriptOnly` (the retry without the recording sends a plain
text message); `ok` is true when all three hold. It reads no credentials and
nothing leaves loopback. On the Listening page `TalkHearVoiceStatus` reads
whether the saved Thinking model hears and where the recording goes, or what to
change, and `TalkHearVoiceChoice` which applies: *On: you turned it on.*, *Off:
you turned it off...*, *On: your voice stays on this PC...* or *Off until you
tick it: your recording would leave this PC for ...*. The `TalkHearVoice` check
box shows the effective value; ticking or unticking it saves your choice, so it
needs `--allow-ui-effects`. While Thinking hears and the choice is on, the page shows
**When Thinking can hear you**: the radio buttons `TalkVoicePathStraight` and
`TalkVoicePathTranscribeFirst` (`ui_snapshot`'s `selected`; choosing one saves
`talk-preferences.json`, so it needs `--allow-ui-effects`) and
`TalkVoicePathStatus`, which says which way your voice goes and when a message
is transcribed first anyway. A real reply with a recording needs a microphone (or
the simulated one below) and a model that hears; the talk window then notes
*Thinking heard your voice.* (*... straight away.* on the straight path, or that
it got the transcript only) under what you said.

`straight_voice_check` rehearses **Send my voice straight to Thinking**
headless with the production pieces: the conversation runtime and Chat
Completions adapter, and the desktop's own `SpokenWords` (the words of a
recording sent alone, transcribed beside the reply) and
`ConversationContextBuffer` (what each next request carries). Three utterances
said by a Windows voice (never a microphone, nothing played) go one turn at a
time as the recording alone. `straight.turns` gives per turn the request's
`requestHadAudio`, `wavValid`, `audioSeconds`, `requestText` (only the stand-in
*(spoken: listen to the recording)*) and `transcriptInRequest` (false), the
earlier messages it carried (`historyMessages`, `historyRecordings` 0),
`firstWordsMs`, `transcriptReadyAfterReplyStartMs`, `speechToTextMs`, the
`transcript` and word check, `inputTokens`/`cachedTokens`/`cachedShare` and the
`reply`. `history` shows the user messages the conversation now carries (the
transcripts, never the stand-in) and the request after the last turn
(`recordingsInHistory` 0, `standInLeft` false). `transcribeFirst` runs the same
utterances the other way (speech-to-text, then the transcript and recording) and
`compare` gives both medians from the end of the recording to the first words.
`refusal` uses a fixture endpoint that refuses any recording: the reply waits
for the words and asks again with them (`retry.text` is the transcript,
`retry.audio` false, `audioRejected` true). `quickCheck` runs the quick check of
something short (the desktop's, beside the request of what went straight with
less than `belowVoiceMs` of voice) on fixtures (a hum, coughs, *Mmm.*, *Yes,
please.*, *Stop.*): per fixture the voice the production detector measured
(`voicedMs`, `quickCheck` when it is short enough), Parakeet's `transcript` and
`parakeetMs`, whether the production word check counts it as `words`, and
`replyDropped`; `ok` needs every short non-word dropped and no real words ever
dropped. With `live: true`, `contention` sends the same short straight request
(*Yes, please.*, new after the instructions each time) in rounds: alone, with
Parakeet started at the request's start (what the desktop does) and with it
started at the model's first words, giving the median first words, reply end and
when the check finished. Speech-to-text is Parakeet on this
PC when it is downloaded (`speechDirectory`, default the current user's; the
sherpa runtime from `martletDirectory`, which the script fills in with this
checkout's Desktop build), else a fixture transcriber. With `live: true`
Thinking is Ollama on this PC (`model`, default `gemma4:e2b`, which Ollama must
say hears) through a loopback relay that records each request and sends it on as
the desktop does (Thinking steps Off, usage with the prompt cache); otherwise a
fixture endpoint answers (canned replies, NOT AI). `ok` needs every straight
request to carry the recording and only the stand-in, the history to carry the
transcripts and no recordings, the refusal to be answered from the words and the
quick check to be right on every fixture.
Nothing leaves this PC and no credentials are read.

`model_ability_check` shows what Thinking models were found to hear (recorded
audio) and see (pictures), and rehearses how Martlet finds out (optional
absolute `dataDirectory`, default the current user's). `saved` lists
`model-abilities.json` (`file` `none` or `loaded`, `count`, and per model
`Origin`, `ModelId`, `Hears`, `Sees`, `Source` and `CheckedAt`); the same list
travels to the owner's other computers as the `model-abilities` shared setting
(`settings_sync_status` shows its value). `fixture` runs the production
detection against servers on 127.0.0.1 shaped like OpenRouter's model list
(`architecture.input_modalities`: `openRouterOmni` hears and sees,
`openRouterSight` only sees, `openRouterUnlisted` unknown), llama.cpp
(`/props` `modalities`: `llamaCpp` hears, doesn't see) and Ollama
(`/api/show` `capabilities`: `ollamaGemma4E2b` hears and sees, `ollamaQwen3`
neither), each with `Hears`, `Sees` and `AbilitySource`. Then Test hearing
(`ModelHearingTest`) against a fixture Chat Completions endpoint that answers
the test word only when the request carries the recording (it is told the
word: NOT AI): `testHears` true, `testDropsAudio` (answers something else) and
`testRefusesAudio` (error 400 about audio) false, `testWrongKey` (401) null;
`testRequest` checks the request: `text` and `input_audio` parts, a valid WAV,
not streamed, Thinking steps off and the word only in the recording
(`wordInRequestText` false). `decisions` are the hearing and vision decisions
replies use (Gemma 4 E2B in Ollama on this PC hears by name, Gemma 4 12B only
once Ollama says so, OpenRouter's grok-4.3 doesn't when its list says so, a
host's Ollama and OpenAI's Responses route never, a retired model never), and
`shared` checks the shared value's round trip (`roundTrip`, `keptBoth`: a list
that says only what a model sees doesn't erase a test's answer,
`newerRefused`). Each has `ok`. With `baseUrl` (an `http://` server on this PC
only, for example `http://127.0.0.1:11434/v1` or a llama.cpp server) and
`modelId`, `real.metadata` asks that real server what the model takes, and
`test: true` also sends it the real Test hearing request (`real.hearingTest`:
the `word`, `Hears`, `Reply` and `Milliseconds`), a word said by an English
Windows voice; nothing leaves this PC, no credentials are read and nothing is
saved. On Companion › Listening, `TalkHearVoiceTestStatus` reads what Test
hearing does (and whether it stays on this PC) or the last result (the model's
one-word answer and how long it took). The `TalkHearVoiceTest` button sends the
Thinking model a test recording (a provider request; a cloud model asks first,
with `ConfirmationYes`/`ConfirmationNo`), so it needs `--allow-ui-effects`.

`local_model_servers` is Companion › Thinking › This PC › *A model app you
already use* without the window ([Local model apps](LOCAL_MODEL_APPS.md)).
`apps` lists the apps Martlet looks for, each with `Id`, `Name`, `BaseUrl`
(its default address on 127.0.0.1), `OwnedBy` (set when other apps share the
port) and `HowToStart`. `found` is the production `LocalModelServers.DetectAsync`
on this PC's loopback ports now (about 1.5 s): each answering app's `Id`
(`port-8080` when its model list doesn't say which app it is), `Name`,
`baseUrl`, `Models`, `Unusable` (names Martlet can't send), `NeedsKey` and
`HowToStart`. `address` takes what the owner would type (`localhost:1234`,
`8080`, `http://127.0.0.1:5001/v1`) and returns the canonical `baseUrl` with
the app's `name` and `answer` (`kind` `Models`, `NeedsKey`, `NotAModelServer`
or `NoAnswer`, with `Models`, `OwnedBy` and `Problem`), or `problem` for an
address that isn't on this PC. `test: true` sends the production Test model
request to `model` at `address` (or to the first found app other than Ollama):
streamed, the default reply length and Thinking steps, one tool offered and
asked again without it when the app refuses tools. It returns `passed`,
`Summary`, `Warning`, `ToolsRejected`, `firstWordsMs`, `totalMs` and the run
window's `output`, or `error`. It runs the real model on this PC. `fixture:
true` rehearses the same code against fixture servers on 127.0.0.1 (canned
words, NOT AI): llama.cpp without `--jinja` (`llamaCpp`: its model list says
`llamacpp`, the request with a tool is refused, the test asks again without it
and warns) and an app that asks for a key (`keyed`: `NeedsKey` without the key,
its models with it), plus an address off this PC (`offComputer`, refused).
`fixture.ok` needs all of them. Loopback only; never a key; saves nothing.

On the Thinking page, `LocalApp-Ollama` and `LocalApp-Other` (*Model app*:
*Ollama (recommended)* and *A model app you already use*) only show that
app's card. `LocalServersStatus` reads what looking found (*Found on this PC:
LM Studio at http://127.0.0.1:1234/v1 (3 models).*, or *No other model app
answers on this PC...*) and the model Thinking uses there. `LocalServerPick`
reads the app picked (*LM Studio · http://127.0.0.1:1234/v1 · 3 models* or
*Another address on this PC*), `LocalServerModel` the model,
`LocalServerModels` what the app lists, `LocalServerKeyStatus` what the key box
will do (never the key), `LocalServerHint` how to start an app typed by address
and where messages go, and `LocalServerTestResult` the last Test model result.
`LocalServersScan` (*Look again*) and `LocalServerFind` (*Find models* for the
typed address) only ask this PC's loopback, so they are safe clicks.
`LocalServerTest` (a real request to the model) and `LocalServerUse` (switches
Thinking; a model the app doesn't list first asks `LocalServerModelQuestion`)
need `--allow-ui-effects`. `SetupLocalOwnModels` (This PC › Ollama) says that
any Ollama model works. When the app Thinking uses stops answering, Home shows
*<app> isn't ready on this PC* (`HealthIssue-local-model-app`, and `StageText`
when it is the top problem). On
Martlet.Companion, `LocalModelsFound` reads what *Find model apps*
(`FindLocalModels`, which fills the unsaved Thinking fields, so it needs
`--allow-ui-effects`) found.

`spoken_reply_check` rehearses a spoken reply whose voice fails partway, end to
end with the production conversation runtime (`ConversationRuntime`, the Chat
Completions adapter, the Martlet host voice stream and the playback sink). A
fixture endpoint on 127.0.0.1 streams a canned four-sentence reply (NOT AI) a
sentence at a time, the way OpenRouter streams; a fixture Martlet host voice (a
quiet tone, NOT AI) fails on the `failAt`-th piece (1-4, default 1) it is asked
to say, as `voiceFailure`: `server` (default; the host's voice worker failed,
`worker.failed`), `unavailable` (it is reloading, `worker.unavailable`),
`stall` (no audio until the voice's time runs out, shortened to a few seconds),
`slow` (every piece slower than real time: half of its audio, a 1.5 s pause,
then the rest, as Chatterbox streams on a busy host's graphics card)
or `none`; `muted` instead has the user mute Martlet's voice (what the
character's *Mute voice* does, `ConversationTurn.MuteVoice`) as the
`failAt`-th piece is asked, and `text-only` sends the reply with no voice at
all (*Speak Martlet's replies aloud* off); `stopped` has the user stop the
reply (Stop, or talking over it: `ConversationTurn.StopAsync`) as the
`failAt`-th piece starts playing; a fixture speaker opens no device and plays nothing. It returns
`reply` (`state`, `failure`, `textComplete`, `fullText`, `characters` of
`servedCharacters`, and the fixture `text`) and `voice` (`stopped`, `why` (the
turn's `SpeechFailure`), `muted` (the turn's `VoiceMuted`), `provider` and `failedJob`, `piecesAsked`,
`piecesSpoken`, `speechLimitReached`, `speakerOpens`, `samplesPlayed`,
`mayHavePlayed`, and `pauses` and `pausedMs`: how often and how long the
speakers ran dry mid-piece waiting for the voice's next audio) and `captions`, what the speech bubble and subtitles were
given (`complete`, `shown`, `spoken`, `unsaid` and each line's `text`, `atMs`
and `spoken`): a line as each piece starts playing and, after the voice
failed, every sentence it couldn't say, one after another for its reading
time (2-20 s). `ok` is true when the reply completed with all of its text,
only the voice stopped, at the chosen piece with the expected provider code
(or, with `none`, every piece was spoken), and the captions together showed
the whole reply. With `muted`, `ok` needs the voice muted at that piece
(`voice.muted`, no `SpeechFailure`, nothing more asked of the voice) and, with
`text-only`, no voice request or speaker at all; both still need the whole
text and captions that show all of it (every line unsaid for `text-only`).
With `stopped`, `ok` needs the reply `Canceled` instead of completed, no voice
failure and the `failAt`-th piece made; the text and the captions stop there.
Before the fix this reported `Partial` with only the text up
to the failed sentence, and the captions then showed nothing past the last
spoken piece. With `reply` (up to 1,024 characters of one-line text) that text
is streamed a word at a time instead, like a model's tokens, and spoken with
speech breaks chosen like `voice_tags`' (`dataDirectory`'s `persona`, else the
one Martlet uses, else the defaults; `breaks` on top); `voice.pieces` lists
exactly what the voice was asked to say, in order, with `voice.persona` and
`voice.breaks`. Use `voiceFailure` `none` to hear every piece. With `slow`,
`ok` needs every piece spoken whole, nothing stopped, and the reply latency
line saying *The voice paused N times for X ms in all, waiting for its next
audio.* with at least one pause per piece of about the gap each. Before the
playback fix, a pause that long ended the voice after 1 s
(`PlaybackFailed`, `StreamTruncated`): a paired host making speech slower than
real time cut each reply short. With `paused`, the reply pauses once its first
audio has played (`ConversationTurn.Pause`, as *Pause and decide* does when you
talk over it), stays paused for 1 s and plays on (`Resume`): `ok` also needs
`voice.hold` to show no samples played while paused (`SamplesBefore` equals
`SamplesAfter`), the next piece made by the end of the pause (`MadeAfter` at
least 2: synthesis runs one piece ahead of playback and goes on while paused),
every piece said once (nothing made again: the pieces together say the reply's
words once), `timings` with one
pause and one resume, and the latency line ending *, paused N ms when you
talked over it, then resumed*. `voice.hold` also has `PausedAtMs` and
`ResumedAtMs`, on the same clock as the character cues. It reads no
credentials and nothing leaves loopback. A real
paired host's voice failing is NOT reproduced; the talk window then notes
*The voice failed, so this wasn't spoken.* or *The voice stopped partway, so
only the beginning was spoken.* under the reply, and a reply muted partway
*Muted partway, so only the beginning was spoken.*

With `reasoningMs` (0-5000) the fixture endpoint first streams a hidden
reasoning delta (as OpenRouter streams a reasoning model's thinking) and waits
that long before the words; with `voiceDelayMs` (0-5000) the fixture voice
takes that long to make each piece. `latency` returns the reply's step timings
(`timings`: Thinking request, response headers, first reasoning, first piece,
voice request, first voice audio, first piece made and how much speech it held,
playback start; plus `firstWordsMs` and `firstAudioMs`), the desktop log's
reply latency line for this reply (`line`, counted from sending the message)
and that line read back as `latency_report` reads it (`totalMs`, `steps`,
`stepsSumMs`, `missingSteps`). With `voiceFailure` `none`, `ok` also needs
every step from *Thinking authorization* to *speakers*, the steps adding up to
the total within a millisecond each, *hidden reasoning* at least 80% of
`reasoningMs` and *voice synthesis* at least 80% of `voiceDelayMs`. For
example `{"name":"spoken_reply_check","arguments":{"voiceFailure":"none",
"reasoningMs":600,"voiceDelayMs":400}}` returned *hidden reasoning 590* and
*voice synthesis 469* out of 1,215 ms. Like the desktop, the reply is spoken
with speech breaks (the defaults unless chosen), so each piece waits for the
next few words before it goes to the voice: the canned sentences arrive 300 ms
apart, which adds about that much to *first sentence* here (a real model's
next words come within tens of milliseconds); `"breaks":{"shortEndingWords":0}`
measures without that wait.

With `thinkingSteps` (`off` or `on`) the reply carries Companion › Replies ›
Thinking steps (a loopback server gets the chat template's `enable_thinking`);
with `refuseThinking` the fixture endpoint answers a request carrying it with
HTTP 400 *Reasoning is mandatory...*, as a model that always thinks does.
`thinking` returns `steps`, `refused`, `requests`, `sentControl` (whether each
request carried the control, in order) and `reasoningRejected` (the turn's
snapshot). `ok` then also needs one request with the control when it isn't
refused, none without `thinkingSteps`, and with `refuseThinking` the reply
asked once more without it (`sentControl` `[true, false]`,
`reasoningRejected`) and still completed; before this, a refused Off failed
the reply (or handed it to the Thinking fallback when one was set).

With `chattiness` the reply is offered the chattiness tags the desktop offers
while [Martlet decides how chatty it is](SCREEN_COMMENTARY.md#martlet-decides-how-chatty-it-is)
(`ConversationRequest.ControlTags`). `chattiness` returns `offered`, `found`
(the tags the reply wrote, in order, from `ConversationTurn.Controls`),
`switchesTo` (the level the last one picks) and `hidden`; `ok` also needs every
tag out of `reply.text`, `voice.pieces` and the captions, and `reply.fullText`
compares with the served text without its tags. For example
`{"voiceFailure":"none","chattiness":true,"reply":"Sure, I will keep it down. Tell me if you need me. [chattiness: quiet]"}`
returned `switchesTo` *quiet*, the two sentences as the only pieces, and the
same `firstWordsMs` (about 2 ms) and `firstAudioMs` (340-470 ms) as the reply
without its tag: a tag at the end never holds back the first words.

With `characterTags` (such as `["{nod}","{blush}"]`) the reply is offered the
desktop character's tags as while the character shows
(`ConversationRequest.CharacterTags`), and the runtime gets a
`CharacterCueFeed`. `character` returns `offered`, `acted` (what the reply's
tags did, from `ConversationTurn.Acted`, as `voice_tags` lists it: `tag`,
`kind`, `name`, `written`), `note` (the line the talk window shows under the
reply), `cues` (each cue the character got: `tag`, `atMs` when its sentence
started playing, `delayMs` into that sentence, and `actedMs` when the
character acted it or `dropped` when the reply stopped first), `dropped` (how
many), `cuesOk`, `stoppedAtMs` (with `stopped`, when the stop came) and
`hidden`. The check waits for each cue through `CharacterCueLine.ReachedAsync`,
as the desktop's character does. `ok` also needs
every [spelling](CONVERSATION.md#voice-tags) of the tags out of `reply.text`
and `voice.pieces` and, with `voiceFailure` `none`, `slow` or `text-only`, a
cue for every tag acted (`everyCue`) and every cue acted (`cuesOk`). With
`paused`, `cuesOk` needs every cue acted and none while the reply was paused:
a cue that falls in the pause waits for it. With `stopped`, `cuesOk` needs no
cue acted after the stop: the cues still waiting are dropped. For example
`{"voiceFailure":"none","characterTags":["{nod}","{shake_head}","{blush}"],"reply":"Oh, look at all that activity! [nod] What are you working on right now? I bet it's cool *blushes* tell me everything."}`
spoke all three sentences, with `{nod}` (written `[nod]`) at the start of the
second and `{blush}` (written `*blushes*`) 1071 ms into the third, and `note`
*Emotes: nod, blush.* In the desktop the same note shows under the reply in
the talk window (chat text isn't returned by `ui_snapshot`), and `logs_tail`
`contains` `Reply acted` reads *Reply acted: {nod} (written [nod]).* next to
*Character gesture 'nod' played for {nod}.*

For example, `{"voiceFailure":"paused","characterTags":["{wink}"],"reply":"Hey cutie (winks), I like you. Tell me all about your day."}`
paused the reply from 330 to 1349 ms. The wink was due 643 ms into a sentence
that started at 315 ms, inside the pause. It acted at 1989 ms, at the same
point in the speech. With the cue clock's pause and stop taken out, the same
call had the wink act at 1047 ms, during the pause (`cuesOk` false). With
`stopped` and *Hey cutie, I like how you are (winks). Tell me all about your
day.*, the stop came at 372 ms and the wink, due 2071 ms into its sentence, was
`dropped`; without the cue clock it acted at 2446 ms, after the stop. With
`failAt` 2 and *(winks) Hey cutie, I like you. Okay, I see you better now
{wink} so tell me more.*, the first wink acted at once and the second, due
151 ms after the stop, was dropped. In the desktop a dropped cue logs
*Character cue {wink} wasn't acted: the reply stopped before it got there.*

`chattiness_status` reads Companion › Vision › **How often it comments** (the
same choice as Listening › Watch along) from a data directory's
`talk-preferences.json` (optional absolute `dataDirectory`, default the current
user's): `choice` (*Quiet*, *Normal*, *Chatty* or *Martlet decides*), `saved`
and `source` (`saved` or `default`), `martletDecides`, `visionOn` (on unless
saved off) and `hearPcOn` (replies are told about Martlet decides only while
one of them is on), `visionLooksAt` (what Companion › Vision looks at: `whole
screen` unless saved as `active window`, `camera` or `camera address`),
`startsAt` (*normal*), `tags` (every spelling a reply switches with) and
`prompts` (`state` of settings.json, `decides`: Companion › Prompts ›
*Chattiness: Martlet decides* exactly as it is sent, and `notes`: *Chattiness
right now* for each level). Its `rehearsal` sends sample replies (or `reply`,
up to 1,024 characters of one line) through the production speech segmenter
and chat stripper with those tags offered: `spoken`, `shown`, `silent` (the
shown text is `[pass]`), `tags` and `switchesTo`. The level a running
conversation picked shows in the talk window's `LiveChattiness` line (*Chattiness:
quiet (Martlet decides).*) and in
`logs_tail` as *Chattiness: Martlet went from normal to quiet (your message;
Martlet decides).* It reads no credentials and contacts nothing.

`vision_history_check` rehearses how what Martlet sees is kept in the
conversation ([Screen commentary](SCREEN_COMMENTARY.md#what-martlet-saw-stays-in-the-conversation))
with the desktop's production code: `prompts.seen` is Companion › Prompts ›
*What you saw* as the data directory's settings.json sends it (optional
absolute `dataDirectory`, default the current user's), `tag` the seen tag
(`[seen:…]`, any words on one line) and `markers` the line markers
(`[Screen]`, `[Camera]`). Each sample look reply (or `reply`, up to 1,024
characters of one line) goes through the production speech segmenter and chat
stripper with the seen and chattiness tags a look is offered (`spoken`,
`shown`, `passed`, `tags`, `seen`: the description kept, `tagHidden`) and is
kept in a production conversation buffer as the desktop keeps a look
(`replacedPassedLook` when it took the place of the passed look before it),
then a typed message that came with a picture. `history` is the conversation
as the next reply sends it (`role`, `text`, `vision`, and `memoryReads`: what
memory and learning names may read of a user line, null for a look). A running
conversation's looks show in `logs_tail` `contains` `Vision:` as *Vision: the
conversation keeps a screen glance (passed, described, in place of the passed
look before it).* It reads no credentials and contacts nothing.

`active_app_check` reads the [program in
front](SCREEN_COMMENTARY.md#the-program-in-front) the way Martlet's screen
glances do, with the desktop's production `ActiveApp`: `inFront` is the window
in front right now (`found`, `app`, its name such as *Visual Studio Code*,
`fullScreen`, `told`, the words a look sends, such as *Google Chrome (full
screen)*, and `readMs`, how long reading it took the `first` time and `again`
with the name already known, as each capture every 3 seconds reads it; never
the window's title), `sample` is the name `explorer.exe` gets
(*Windows Explorer*), and `rules` is the full-screen rule on FIXTURE windows
(`window`, `maximized`, `titleBar`, `fullScreen`): a borderless full-screen
game or video and a maximized borderless window fill their monitor, a
maximized window with a title bar never does (also over a taskbar that hides
itself), a window smaller than its monitor doesn't, and a borderless window
filling the second monitor does. `prompts` shows, for a FIXTURE full-screen
game, Companion › Prompts › *Screen glance message* (`glance`) and *Active app
with your message* (`withMessage`) as the data directory's settings.json fills
them (`state` `none`, `loaded` or `unreadable`; optional absolute
`dataDirectory`, a disposable one through `scripts\Invoke-MartletMcp.ps1`), and
the `[Screen]` line the conversation keeps (`kept`). In a running
conversation, the talk window's `LiveVisionStatus` `help` shows it as *Active
app: Google Chrome (full screen).* It reads no credentials and contacts
nothing.

`screen_digest_check` runs the [screen summary over
time](SCREEN_COMMENTARY.md#martlet-knows-what-changed-over-time) once with the
desktop's production `ScreenDigester` on **FIXTURE** frames (made-up pictures of
a code editor, then a game with low health; no screen capture) and a FIXTURE
thinker and context board (no model; nothing is sent). `setting` is Companion ›
Vision › *Screen summary over time* from the data directory's
talk-preferences.json (on by default; optional absolute `dataDirectory`, a
disposable one through `scripts\Invoke-MartletMcp.ps1`), `timing` the window,
spacing, fresh, stale and board ages, `observed` which frames the ring kept or
skipped, `job` the reason, frames, contact sheet size and bytes and the message
as Companion › Prompts › *Screen summary over time* makes it, `answer` the
FIXTURE reply (or `reply`, up to 1,024 characters) and `parsed` what Martlet
keeps of it, `board` the note posted as source `screen` with its maximum age,
`status` the frames, last summary, its age, time taken and job counts, `line`
the talk window's `LiveScreenSummary` line, and `staleAnswer` that an answer
that comes after the stale limit is dropped. It reads no credentials and
contacts nothing.

`discord_text_check` feeds simulated Discord messages through the production
[Discord](DISCORD.md) text pipeline (`DiscordTextChat` in `src\Martlet.Discord`,
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
considered, 1 answered, 1 passed, 0 dropped, 0 failed. Last reply: DM. Last
problem: none.*; no message text, names or IDs). The live bot logs each
answered, passed, dropped or failed turn as *Discord text: Answered (Mentions,
addressed), 1 message(s)*.

`discord_companion_check` shows Martlet's [Discord](DISCORD.md) companion
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
friends, 1 request waiting. No call now. Last call: ...*), `DiscordPresenceStatus`
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

`discord_voice_check` rehearses [Discord](DISCORD.md) voice without Discord. It
loads `libdave.dll` (Discord's DAVE end-to-end voice encryption, which NetCord
calls) beside `Martlet.Mcp` and in `martletDirectory` (the script passes this
checkout's Desktop build, where it ships) and runs an offline DAVE session
(`natives` and `shipped`: `LibDaveLoaded`, `DaveProtocolVersion`, `DaveSession`,
`KeyPackageBytes` of the MLS key package, `FrameCrypto` for the frame encryptor
and decryptor, `OpusManaged` for the managed Opus codec, `Ok`). Then one
utterance a Windows voice says (rendered to memory, never played) goes through
the production path (`DiscordVoiceConversation`) with a fake transport: 48 kHz
stereo Opus packets as a Discord client sends them, per-speaker ordering,
decoding, 16 kHz downsampling and endpointing, FIXTURE speech-to-text and a
FIXTURE reply engine (NOT AI), and the reply spoken by a Windows voice into
memory and encoded back to Opus. `utterance` has `saidMs`, `heardMs`,
`heardLevel` and the `turn` (`Addressed`, `source` `Voice`, owner); `reply` has
the frames sent, `spokenMs`, their decoded `level` and the Speaking flag turned
on and off; `bargeIn.stopped` says a second person talking over a long reply
stopped it (`framesBeforeStop` of `longReplyFrames`). `ok` is all of these. No
Discord connection, network, microphone, speaker, provider or credential.

The desktop's Companion › Discord `DiscordVoiceStatus` line (in `SafeValues`)
reads like *Voice: in General (My server) · 2 speakers heard, 3 utterances
transcribed, 2 replies spoken · DAVE on · libdave loaded (DAVE v1) · Last
problem: ...* (counts only, never what was said). The live bot logs *Discord
voice: joined a channel (2 people there).*

### ElevenLabs voice

`elevenlabs_check` rehearses [ElevenLabs as the Voice](ELEVENLABS_VOICE.md)
(the owner's cloned voice with tones) end to end against `ElevenLabsFixture`, a
local stand-in on 127.0.0.1 that follows ElevenLabs' documented protocol.
FIXTURE, NOT ElevenLabs, NOT AI: its voice is a quiet tone, and its first audio
waits 100 ms to stand in for model latency (not a measurement). It runs three
steps:

1. **Cloning.** Instant Voice Cloning with a synthetic WAV. `clone.sent` shows
   the form Martlet sent (`name`, `fileName` `voice.wav`, `contentType`
   `audio/wav`, `wave`, `removeBackgroundNoise` `false`, `description`) and
   whether the key matched.
2. **One piece** straight through `ElevenLabsDialogueClient`. `segment` gives
   its `audioBytes`, any `failure` and `detail`, and the fixture timings
   `connectMs`, `firstAudioMs` and `totalMs`.
3. **A whole spoken reply** through the production conversation runtime. A
   fixture Chat Completions endpoint streams `reply` a word at a time (by
   default one with `[laughs]`, `[whispers]` and `[happy]`), and a fixture
   speaker plays nothing.

`ok` needs all of these:

- `thinkingPrompt.listsElevenLabsTags`: the Thinking prompt lists ElevenLabs'
  own tags (`tags` gives each tag, its kind and its cue).
- `tags.reachedElevenLabs`: every tag the reply wrote reaches ElevenLabs as
  written.
- `tags.chatClean` and `tags.captionsClean`: the chat text and every caption
  show no tag.
- `protocol.ok`: each connection carries `model_id`, `output_format=pcm_24000`,
  the key in the `xi-api-key` header (`keyInHeader`, never `keyInBody`), the
  one cloned voice and `close_socket`.

`scenario` is `reply` (default), `model-refused` or `bad-key`. With
`model-refused`, the WebSocket refuses the model with `param: model_id`, as its
API reference describes. The voice then fails as `ModelUnsupported`, and
`segment.detail` says to choose Eleven v3 Conversational. The text still
completes. With `bad-key`, cloning and speech fail as `Authentication`. `model`
is `eleven_v4_turbo` (default) or `eleven_v3_conversational`. With
`dataDirectory`, `saved` reports the saved ElevenLabs choice: `voiceRoute`,
`usesElevenLabs`, `model`, `enabled`, `confirmed`, `keySaved`, `clonedVoice`,
`requiresVerification` and `keysFromBefore`. It never gives the key, the voice
ID or the voice's name. Nothing is sent to ElevenLabs: `live` is always NOT
RUN, because there is no ElevenLabs account or key.

```powershell
.\scripts\Invoke-MartletMcp.ps1 -Build -Calls '[
  {"name":"elevenlabs_check"},
  {"name":"elevenlabs_check","arguments":{"scenario":"model-refused"}},
  {"name":"elevenlabs_check","arguments":{"scenario":"bad-key"}}]'
```

On Companion › Voice › *A cloud provider*, the ElevenLabs card's status is
readable: `ElevenLabsStatus` (*Not in use...* or *In use: Eleven v4 Turbo with
your cloned voice. Key saved.*, and whether ElevenLabs asked to verify the
voice; never the voice's name), `ElevenLabsKeyStatus` (what the key field will
do; never the key) and `ElevenLabsModel` (the chosen model). The card's
abilities and where it runs read through `VoiceEngineAbilities-elevenlabs` and
`VoiceEngineRunsOn-elevenlabs`. `ElevenLabsVoice` holds the owner's voice names,
so it is not readable. `ElevenLabsKey` and `ElevenLabsConsent` are inputs.
`ElevenLabsSave` uploads a recording and saves the route, so it needs
`--allow-ui-effects`. Never press it in a check: it would contact ElevenLabs.

### Latency

Every reply writes one *Reply latency* line to the desktop log: how long from
when you stopped talking (always listening), let go of the talk button or sent
your message to the first audio (or the first words when nothing was spoken),
then each step in parentheses, each the wait that ended there, so they add up
to the total: *end of speech* (or, when always listening's end-of-turn judge
decided, *end-of-turn wait* and *end-of-turn judge*, followed by *end of
speech* when it found the turn unfinished or was slow), *recording*, *Voice ID*, *speech-to-text*,
*voice recognition*, *waiting to answer*, *preparing*, *memory*, *lore*,
*tools*, *Home Assistant*, *building the request*, *Thinking authorization*,
*Thinking connection*, *Thinking before reasoning* and *hidden reasoning* (or
*Thinking first words* when no reasoning was streamed), *first sentence*,
*voice authorization*, *voice synthesis*, *promoted* (a reply started early
taken as the reply), *playback start* and *speakers*
(steps that didn't happen are left out). It goes on with the time to the first
words and audio from the reply's start, the number of spoken pieces, how much
speech the first piece held and how long it took to make, when the speakers ran
dry mid-reply waiting for a voice made slower than real time how often and how
long (*The voice paused 2 times for 3120 ms in all, waiting for its next
audio.*; left out below 100 ms), and the model IDs
(`Models: Thinking ..., voice ..., speech-to-text ...`). What each step covers
is in [Voice latency](VOICE_LATENCY.md#measure-first-the-reply-latency-line).

`latency_report` reads those lines from a data directory's desktop log (with
its rotated copies; optional absolute `dataDirectory`, default the current
user's) for the newest `replies` (1-500, default 20) and returns `measured`
(lines with steps), `legacy` (older lines that only gave the first words and
audio from the reply's start), `firstAudio` (from the moment that counts for
you), `firstWordsFromReplyStart` and `firstAudioFromReplyStart` (each `{Count,
Median, P90, Min, Max}` in ms), `steps` (the same for every step),
`slowestSteps` (the five with the largest median), `voicePauses` (`replies`
whose voice paused, `pauses` in all and `pausedMs` statistics), `pausedForYou`
(replies paused because you talked over them with *When you talk over Martlet*
on *Pause and decide*: `replies`, how many `resumed` and `stopped`, and
`pausedMs` statistics, read from *, paused N ms when you talked over it, then
resumed* or *, paused N ms, then stopped when you talked over it*), `early`
(replies started early with Companion › Listening › *Start replies early*:
`replies` whose turn started any, how many were `promoted`, `starts` and
`cancelled` in all, `startedAtMs` statistics of how far into your pause the
promoted ones started, and `firstAudioPromoted` and `firstAudioOthers`
statistics, read from *Started early at N ms, promoted* and *Started early N
times, M cancelled*) and `newest` (each reply's
`at`, `measured`, `totalMs`, `from`, `steps`, `firstWordsMs`, `firstAudioMs`,
`spokenPieces`, `firstPieceSpeechSeconds`, `firstPieceMadeMs`, `voicePauses`,
`voicePausedMs`, `models`,
`interrupted`, `restarted`, `pausedForYouMs`, `resumed`, `startedEarlyMs`,
`earlyStarts`, `earlyCancelled`, `liveFloor` (what the live floor held and stopped for
that turn, from *Live floor: held 2 pool jobs, stopped 1 (think longer).*, else null), `quickSoundMs`
(when a quick sound played, from *Quick sound at 712 ms.*, else null), `backup`, `backupMember`,
`backupAskedMs` and `backupWonMs` (Backup Thinking: `won`, `lost`, `no answer` or `no member`, from
*Backup Thinking won at 1104 ms (diva (qwen3-8b), asked at 912 ms).* and the like, else null),
`legacy`), `quickSounds`
(replies that played a quick sound in Martlet's own voice: `replies`, `atMs` and their `firstAudio`
statistics) and `backupThinking` (replies that asked Backup Thinking: `replies`, `won`, `lost`,
`noAnswer`, `noMember`, and `askedAtMs`, `wonAtMs` and `firstAudioWon` statistics). It only reads the log: no audio, network or provider
request.

`quick_sounds_status` reads Companion › Voice › *Quick sounds while Martlet
thinks* from a data directory (optional absolute `dataDirectory`; the script
gives a disposable one): `on` (off by default) and `delayMs` (500, 700, 1000 or
1500; 700 by default) from `talk-preferences.json` with their `source`,
`cooldownSeconds` (20), the `voice` replies speak with (`words`, whether it is
`paid` (OpenAI's cloud voice) and its `key` per voice and character, or null
without a voice), whether its clips are `ready` (each clip's `text` and
`milliseconds`) or it `needsClick` (a paid voice waits for *Make quick sounds
now*), every set `kept` in `quick-sounds\` (`key`, `voice`, `madeAt`, `clips`)
and the newest desktop `log` lines about quick sounds (*Quick sound: "Mm," 712
ms after the reply was confirmed (...)*, *Quick sounds: made 4 with ...*).
Read-only; it never plays anything.

`quick_sounds_check` rehearses quick sounds (see
[Voice latency](VOICE_LATENCY.md#quick-sounds-while-martlet-thinks)) with the
production rules (`QuickSoundGate`, `QuickSoundWatcher`,
`ConversationTurn.PlayQuickSound`) on fixture turns through the production
conversation runtime, Chat Completions adapter, host voice stream and playback
sink. FIXTURES, NOT AI: a Chat Completions endpoint on 127.0.0.1 answers one
sentence after a set wait, a host voice makes a quiet tone and the speakers
open no device. `scenario` (default all): `slow` (first words after 1.8 s: the
quick sound plays once the reply had no audio for the delay, the whole clip
before the reply, which follows on its own run uncut), `fast` (none: its own
audio was ready), `cooldown` (a second slow reply within 20 s gets none),
`early` (a reply started early is held for 1 s, then taken: counted from when
it was taken), `let-go` (held, then let go: none, nothing played),
`reasoning` (hidden reasoning streams before the words: after 300 ms) and
`paused` (paused because you talked over it: none). `delayMs` is 500, 700,
1000 or 1500 (700 by default). Per turn it returns `played`, `why`, `clip`,
`quickSoundAtMs` (from the turn's start), `afterConfirmedMs`, `releasedAtMs`
(a reply started early), `firstAudioAtMs`, `playbackRuns`,
`replyFollowedUncut` (the clip's run closed before the reply's opened, and it
played whole), `state` and the reply `latencyLine`; `ok` when every scenario
does what it should. Nothing is recorded, played or sent off this PC.

`turn_judge_check` checks always listening's end-of-turn judge (Companion ›
Listening › *Judge when I finish talking*, see
[Voice latency](VOICE_LATENCY.md#the-end-of-turn-judge)). It loads the Smart
Turn v3.2 model and ONNX Runtime bundled in `martletDirectory` (optional
absolute path; `Invoke-MartletMcp.ps1` uses this checkout's Desktop build)
through the production `SmartTurnEngine`. It judges six phrases a Windows voice
says (three finished, three that trail off; rendered to memory, never played),
each cut 260 ms into the pause that follows, as always listening asks it. It
returns `loadMs`, per phrase `expected`, `verdict`, `probability` and `judgeMs`,
then `agreed` and `medianJudgeMs`. Its `gate` part steps the production
`EndOfTurnGate` with the plain 800 ms pause, frame by frame: a complete answer
ends the turn at 300 ms, an incomplete one waits for 1600 ms, and a slow or
failed judge leaves it to 800 ms (`endedAtMs`, `ok` each). `ok` is a median
judge time of at most 100 ms and every gate case as expected; agreement on a
synthetic voice is informative only. Without the model or the runtime it
returns `ran: false` with the reason. Nothing is recorded, played, downloaded
or sent.

`early_reply_check` rehearses Companion › Listening › *Start replies early*
(see [Voice latency](VOICE_LATENCY.md#starting-replies-early)) headless, in
real time (20 ms frames), with the production `EndOfTurnGate`,
`EarlyReplyGate`, request comparison (`EarlyAsk`) and the conversation
runtime's held turn (`ConversationRuntime.StartEarly`,
`ConversationTurn.Release`) through the Chat Completions adapter, a paired
host's voice stream and the playback sink. FIXTURES, NOT AI: a quick
transcript after `sttMs` (default 90), a judge answer after `judgeMs` (26), a
Chat Completions endpoint on 127.0.0.1 whose first words come after
`thinkingMs` (200) and stream for 1.5 s, and a host voice whose first audio
comes after `voiceMs` (350); the speakers open no device. `scenario` (default
`all`): `incomplete` (the judge finds the pause unfinished and the 1.6 s pause
ends the turn), `plain` (no judge: the 800 ms pause), `complete` (the judge
ends the turn at about 300 ms), `resumed` (you go on talking 700 ms into the
pause, then pause again) or `changed` (the final transcript differs from the
quick one). Each runs twice, with replies started early (`withEarlyReplies`)
and `without`, and returns per run `decisions` (each with `atMs` from when you
stopped talking), `turnEndedMs`, `firstAudioAfterTurnEndMs`, `startedEarly`,
`cancelled`, `outcome` (`promoted` or `changed`) and `reason`,
`thinkingRequests`, `abortedRequests`, `askedWith`, `letGo` (each reply let
go: its `state`, `mayHavePlayed` and `textCharacters`), `voicePieces`,
`firstVoiceAskedMs`, `playedBeforeTurnEnded` and `shownBeforeTurnEnded` (both
0), `liveFloor` (the production `LiveFloor`, fed as the desktop feeds it:
`atTurnEnd`, its level when the turn ended, `repliesAtTurnEnd` and
`repliesWhenDone`, how many replies held it then and once the reply was done;
its changes are among the `decisions`) and the reply `latencyLine`, plus
`savedMs` per scenario. `ok` when each
scenario does what it should: promoted with one request (`incomplete`,
`plain`), let go once with its request aborted and the second start promoted
(`resumed`), let go and started again with the final words (`changed`),
nothing started or promoted for `complete`; nothing heard or shown before
the turn ended, every reply let go `Canceled` with nothing played, the live
floor `Live` and held by the one reply started early when the turn ended and
by no reply once the reply was done, and for
`incomplete`, `plain` and `resumed` the first audio sooner by at least 60% of
`thinkingMs` + `voiceMs`. Nothing is recorded, played or sent off this PC.

`context_board` rehearses the [context board](CONVERSATION.md#context-board)
with the production board, request layout and Chat Completions adapter
against a fixture endpoint on 127.0.0.1 (canned reply, NOT AI). It posts
FIXTURE notes from four sources (`screen`, `character`, a stale `sound` note
and a `touch` note that is consumed on read), plus an optional test note of
your own: `source` (1-32 lower-case letters, digits or `-`), `text`,
`maxAgeSeconds` (1-3600, default 60), `ageSeconds` (0-7200, how long ago it
was posted) and `consume`. It sends two requests in a row and returns
`firstRequest` and `secondRequest` (`notes`: the sources each snapshot took,
`bytes`, `carried`: the fixture sources found in the sent message,
`outcome`), `staleSkipped`, `consumedOnce`, `orderStable`, `boardAt` (*end
of the user's message, after Martlet's other notes*),
`historyKeepsBoardNotes` (false), `keptMessageIsStartOfSent`,
`messagesSentAgainUnchanged`, `lastSent`, `posted` (your note: `inFirstRequest`,
`inSecondRequest`) and `limits` (`noteBytes` 600, `totalBytes` 2048,
`sources` 16, `maxAgeSeconds` 3600); `ok` is true when all of them hold.
Live replies and looks log *Context board: the request took N notes (sources;
bytes; consumed)* in the desktop log (`logs_tail`), and `LiveTurnInputs`
counts them (*... and 2 context notes*). Loopback only; no credentials.

`context_check` shows the Thinking model's [context](CONVERSATION.md) as
replies use it (optional absolute `dataDirectory`, default the current user's):
`settings` (`none`, `loaded` or `unreadable`), `thinking` (`routeType`,
`localOllama`, `inNetwork`, `replyRequestSeconds`, `model`; Thinking on this
PC, a paired host or a Chat Completions server at a private or local address is
`inNetwork` and gets 120 s per reply request and the whole action, a cloud
route 45 s), `savedContextTokens` (Companion › Replies › Context
size, null when blank), `modelLimit` (what `model-limits.json` says about the
model: `ContextTokens`, `ModelMaximum`, `Source`, `checkedAt`) and
`modelLimitsKept`, and `context` (the production `ContextBudget`: `Tokens`,
`source` such as `Saved`, `Default`, `ModelLimit`, `HostDefault`, `Ollama` or
`OllamaAssumed`, `ReplyTokens`, `InputTokens`, `ModelTokens` and `described`,
the words the Replies page shows). Its `probe` rehearses the production model
limit check against fixture servers on 127.0.0.1 shaped like OpenRouter (the
smaller of `context_length` and the top provider's), vLLM, Groq and llama.cpp
model lists, an unlisted model, a redirect (never followed; the fixture key
goes only to its own base URL) and Ollama's `/api/show`, `/api/ps` and
`/api/generate` (loaded, not loaded, loaded by the check, missing). Its `fit`
fits a 1,000-exchange synthetic conversation the way a reply does into a cloud
model's default 100,000 tokens, 1,000,000 tokens on gpt-4.1, a paired host
(8,192 tokens, 16 KiB, 16 messages) and Ollama on this PC at 32,768:
`exchangesSent`, `exchangesLeftOut`, `estimatedTokens` and `bytes`, `ok` when
the newest exchanges fit and one more wouldn't. Its `cache` part rehearses the
[request layout that lets prompt caches work](CONVERSATION.md#prompt-caching-and-the-request-layout)
with the production Chat Completions adapter against a fixture endpoint on
127.0.0.1 (canned reply and usage, NOT AI): two replies in a row whose notes
differ send the instructions and earlier messages again unchanged
(`messagesSentAgainUnchanged` of `messagesBeforeTheMessage`, `stableStart`),
the notes close the user's message (`notesAt`, `notesInInstructions` false),
the usage chunk's cached tokens are read (`usage`), only Ollama on this PC is
asked for usage (`asksUsageFromOllamaOnThisPc`, `asksUsageFromOtherServers`),
and `trimming` replays 60 replies of a synthetic conversation into Ollama's
smallest context: how often the request's start moved letting go of the
oldest quarter at once (`startMovedLettingGoAQuarter`, 7) against one exchange
at a time (`startMovedOneAtATime`, 37). Each part has an `ok`. It
reads no credentials and nothing leaves loopback. A real model's cache use shows in
the desktop log's *Thinking input (Reply): first words after … ms; N input
tokens, M of them (P %) from the model's prompt cache.* lines
(`{"name":"logs_tail","arguments":{"contains":"Thinking input"}}`, also for
glances and *Remembering*/*Learning names*) and in the `help` (tooltip) of the talk
window's `LiveContext`. On Companion › Replies,
`RepliesContextStatus` reads the size in use and where it comes from and what
Martlet knows of the model's own limit; `RepliesCheckContext` (*Check model
limit*) asks the Thinking model's server (loading the model in Ollama on this
PC first), so it needs `--allow-ui-effects`. Choosing a Chat Completions model
on Companion › Thinking checks it the same way, and *Test model* and the talk
window's model loading record what Ollama on this PC gives the model; the
desktop log says *Context of gemma4:12b: 16,384 tokens (Ollama on this PC).* or
*Ollama on this PC gives gemma4:12b 16,384 tokens of context; the next
conversation uses it.*

`thinking_steps_check` shows Companion › Replies › **Thinking steps** (whether a
reasoning model thinks before it answers) as replies use it (optional absolute
`dataDirectory`, default the current user's; optional `model` and `live`):
`settings`, `thinkingSteps` (`Off` or `On`: what replies use; Off unless On is
chosen), `chosen` (whether a choice is saved), `thinking` (the
Thinking route's `routeType`, `localOllama`, `model`, its `control` and `use`
and `sends`, exactly what its replies add for that choice), and `routes`,
what Off and On send on every kind of route: `reasoning_effort` `none`/`medium`
for Ollama on this PC (Used), OpenAI and Gemini; OpenRouter's `reasoning`
(`{"effort":"none"}` or `{"enabled":true}`, Used); `chat_template_kwargs`
`enable_thinking` and `thinking` for NVIDIA Build and other servers (vLLM,
SGLang, llama.cpp; ServerDependent); Ollama's `think` on a paired host (Used,
carried by the gateway); nothing on the OpenAI route (Unused). Its `fixture`
runs the production Chat Completions adapter against an endpoint on 127.0.0.1
(canned reply, NOT AI) for Off, On and the model's own default (what a reply is
asked again with after a model refused the choice: no field), `ok` when each
request carries exactly the expected fields. With `live: true`, `live` asks Ollama on this PC (the
saved local Thinking model, or `model`) a fixed question, never anything the
owner said, with the model's default and with Off: the production adapter's
`reply` (`Outcome`, `FirstWordsMs`, `TotalMs`, `text`) and one `plain` request
each (`ReasoningCharacters`, `CompletionTokens`), `thinksByDefault`, and `ok`
when Off completed with no thinking. On this PC with `gemma4:12b`, Off answered
with first words after about 0.1 s and 16 tokens against about 1-3 s and
260-300 tokens (600 characters of thinking) by default. It reads no credentials
and nothing leaves loopback. A paired host's `think` (desktop client, gateway,
Ollama relay) is checked by `OllamaRelayTests`; real cloud providers are NOT RUN.

`work_sharing_status` shows Devices › **Sharing work** (optional absolute
`dataDirectory`, default the current user's; optional `deviceId`, default this
PC's): `file` (`loaded` or `none`), `shared` (the `work-sharing` shared
setting's canonical JSON), `isDefault`, `device`, `ownHost` (the pairing saved
as this PC's own host service), `paired`, `planHosts`, and for each of
`speaking`, `thinking`, `listening` and `deep-thinking`: `role` (the host role
that does it), `shares` and `sharedByDefault` (Speaking and Listening yes,
Thinking no), the `order` and `never` chosen, `planned` (the computer the job
uses now, from `settings.json`), `runs` (paired computers the shared plan says
run that role) and `tries` (the production `WorkSharing.Order` for that device;
null for Deep thinking, whose places come from Companion › Deep thinking), and
`kept` (computers kept for some companion PCs, with `usableHere`). Host and
device IDs only.

`work_sharing_check` rehearses the production planner and queue (`WorkQueue`)
on the four-computer example (machines 1 and 3 companions with Chatterbox,
machine 2 a companion with Deep thinking and no voice, machine 4 lip-sync and
pictures) with simulated computers that run one request at a time and turn
another away at once as a host's gateway does (`job.busy`), **NOT real hosts
or models**. Each step reports `passed` and its detail: each companion's order
with its own computer first; Thinking left unshared; a busy voice passed over
at once (`WaitedMs` near 0); machine 2 waiting while both voices are busy and
taken by whichever frees first (machine 3, `WaitedMs` about 250); four segments
at once spread two and two; a computer kept for one companion PC or unticked
for Speaking left out; an unanswering computer skipped; Deep thinking leaving
out a kept computer; and the shared setting's round trip. `ok` is true when
every step passed. On the desktop, `ui_snapshot` reads `WorkSharingStatus`
(how many of this PC's requests another computer took since Martlet started)
and every `WorkSharing*` control on the Devices page: `WorkSharingJob-<job>`,
`WorkSharingPlace-<job>-<host>` ("1. m3-host. never used for it."),
`WorkSharingHost-<host>` and the `WorkSharingShare-<job>`,
`WorkSharingOwnFirst-<job>`, `WorkSharingUse-<job>-<host>`,
`WorkSharingUp/Down-<job>-<host>` and `WorkSharingKeep-<host>` controls, which
save `work-sharing.json` and so need `--allow-ui-effects`.

`recommended_setup_status` shows Home's **Recommended setup** without the
desktop. It builds the network recommender's request with the desktop's own
builder (`RecommendedSetupInputs`) and runs the production recommender
(`NetworkRecommender`). Give an absolute `dataDirectory` (default the current
user's; the script gives a disposable one) to plan from that directory's
`hosts.json`, `host-hardware.json`, `cluster.json`, `settings.json`,
`work-sharing.json`, `thinking-pool.json` and `speaking-engine.txt`. A data
directory has no live host checks: every host counts as online, and its roles
are the shared plan's record. This PC's hardware is its own host service's
report (the desktop reads this PC live). Give `fixture: "network"` to plan the
built-in four-computer network instead (**NOT real computers**): this PC, a
companion PC with an RTX 4080 that runs Thinking, Speaking and Listening on its
own host service; `gpu-box`, a Linux host PC with an RTX 4090 and nothing
installed; `DIVA`, a companion PC whose host service runs Deep thinking; and
`old-box`, a host without a hardware report. The result has:

- `source` and `computers`: each computer's `id` (the cluster plan's host ID,
  else the device ID), `name`, `kind` (`Companion` or `Host`), `thisPc`,
  `hasHostService`, `manageable`, `online`, `planned` (false: left as it is),
  `hardware` and `roles` (`kind=model`).
- `notes`: why a computer is left as it is.
- `today`: each job's `host`, `off`, `option` and `pool` (the other computers
  that take its requests when the one in charge is busy, in Sharing work
  order), `thinkingPool`, `thinkingPoolOptOut`, `voiceEngine`, `preference` and
  `offlineGraceMinutes`.
- `recommendation`: `alreadyOptimal`, `worthAsking`, `fingerprint`, the
  `changes` (`kind`, `computer`, `summary`, `why`, `benefit`, `roleKind`,
  `model`, `job`, `needsSomeoneThere`, `downloadGb`), each computer's
  recommended `roles`, `why` and `load` (percent of graphics memory, memory and
  processor), the `jobs`, the `thinkingPool` and `notes`.
- `companionInUseAsks`: what a companion PC someone uses would do after an
  automatic check (`Ask`, `Wait` or `Nothing`, and why), with `declinedHere`
  (this setup is in the directory's `recommended-setup.json`).

It is read-only, contacts nothing and reads no keys. On the desktop, Home's
`HomeRecommendedSetup` is in `SafeClicks`. In a Martlet network it opens the
review window (`RecommendedSetupWindow`). On a PC alone, it opens Set it all up
for me's question (`DefaultSetupQuestion`). In the review, `ui_snapshot` reads
every `RecommendedSetup*` text: `RecommendedSetupTitle`,
`RecommendedSetupSummary`, `RecommendedSetupChange-<n>` (its name is the
benefit, the summary and why), `RecommendedSetupComputer-<n>`,
`RecommendedSetupComputerKind-<n>`, `RecommendedSetupToday-<n>`,
`RecommendedSetupTarget-<n>`, `RecommendedSetupLoad-<n>`,
`RecommendedSetupBar-<n>-<vram|ram|cpu>`, `RecommendedSetupJob-<n>`,
`RecommendedSetupManual-<n>`, `RecommendedSetupDownloads`,
`RecommendedSetupPreflight-<n>`, `RecommendedSetupTerms-<n>`,
`RecommendedSetupSecret-<n>` (the label only; the key box `SetupSecretInput-<n>`
is never read), `RecommendedSetupNote-<n>` and `RecommendedSetupStatus` (the
preflight state, or why Reconfigure couldn't start, such as *Martlet is already
reconfiguring your computers. Background tasks shows its progress.*).
`RecommendedSetupClose` only closes the window. `RecommendedSetupApply`
(Reconfigure: it changes every computer) and `RecommendedSetupCancel` (Not now:
it saves `recommended-setup.json`) need `--allow-ui-effects`. Reconfigure
closes the review and starts the background task *Reconfigure your computers*
in its run window: `HostRunStatus` and `TaskState-<id>` read the progress
(*Configuring your computers: 0 of 2 finished. gpu-box: Installing Chatterbox
Turbo (1 of 6).*), then the outcome (*Reconfigured your computers: 9 of 9
changes made.*), and `logs_tail` `host-runs` returns its output: the numbered
changes, each computer's steps (*gpu-box: done. All 6 changes made.*), what
each host engine prints and each change's outcome (*Done: ...*, *Failed: ...* or
*Needs you: ...*). Cancel task stops the changes not made yet (*Needs you: ...
Stopped before it ran.*) and the task reads *Canceled at ...*. To drive
Reconfigure without your computers, set `MARTLET_SIMULATE_RECOMMENDED_SETUP` to a
number of seconds (1-600) before launching the desktop (FIXTURE, **NOT real
computers**): Home's Recommended setup then plans the fixture network that
`recommended_setup_status` plans with `fixture: "network"` (also on a PC
alone), and Reconfigure applies it to simulated computers. Each role change
takes that many seconds and writes `FIXTURE` lines; nothing is installed,
contacted, saved or shared, and the run shows on this PC's Home
(`HomeConfiguringStatus`) only. When an automatic
check finds a better setup, Home shows `HealthIssue-recommended-setup`. Its
Review (`HealthOpen-recommended-setup-review`) opens the review, and its Not now
(`HealthFix-recommended-setup-decline`) saves `recommended-setup.json`, so it
needs `--allow-ui-effects`.

`network_recommendation_check` runs the production network recommender
(`NetworkRecommender`, Home's
[recommended setup for all your computers](RECOMMENDED_SETUPS.md#recommended-setup-for-all-your-computers))
on built-in fixture networks, **NOT real computers**. It takes no arguments
and reads nothing. Each step names its rule (1 to 12) and reports `passed` and
its detail: the change list (kind, computer, benefit, summary and why), the
target roles, jobs, pools, Thinking pool and notes. The steps are: two
companion PCs and two hosts with nothing set up (companion PCs run no host
roles; Thinking gets Gemma 4 E2B on a card of its own; one more voice for the
second companion PC; no Thinking pool change); make before break; a host with
two NVIDIA cards (Thinking and Deep thinking pinned to cards of their own); a
Windows host whose voice shares its card (the voice moves to a card of its
own); a crowded network (no card over its capacity); Deep thinking beside the
voice (it moves to the card no live job uses); heavy roles on a companion PC
(they move to the host, Improvement); Thinking on a companion PC's card with
only a processor host (it stays: no added latency); hosted Thinking that the
owner chose (it stays, unless everything is kept local); a host left out of
the Thinking pool; the voice host away 4 and 25 minutes (no change, then
Speaking moves as Required); and the applied recommendation (no changes and the
same fingerprint in any order). `ok` is true when every step passed.

`node_presence_status` shows when your other computers go away or come back
([CLUSTER](CLUSTER.md#when-a-computer-goes-away-or-comes-back); optional
absolute `dataDirectory`, default the current user's): `awayMinutes` (Settings
› Your other computers, `node-presence.txt`, 10 by default) and
`awayMinutesSaved`, the rules (`missingAfterSeconds` 30, `backAfterSeconds`
30, `backShownForMinutes` 10), `report` (`loaded`, or `none` until the desktop
writes `node-presence.json`, which it does when it starts and when a
computer's state changes), `updatedAt`, `companion`, `ownHost` (left out of the
notices), each paired computer in `hosts` (`state` `Answering`,
`NotAnswering`, `Missing`, `Away`, `Returning` or `Back`, `since`, `backAt` and
`awayForMinutes` as of now) and the `notices` Home shows (`id`, `level`,
`title`, `detail`). Host IDs, computer names and times only.

`node_presence_check` rehearses the production rules (`PresenceWatch`,
`NodePresenceNotices`, `NodePresenceSettings`, `NodePresenceReport`) on
scripted timelines: a check every 15 seconds with the desktop's presence rule
and a 5-second tick, **NOT real hosts**. Each step reports `passed` and the
events with the second they are dated and the second they were noticed: one
missed check says nothing; 30 seconds of silence goes missing once (noticed at
second 45 for a host silent from second 15), with a notice that names the
failover move, the job that waits, the pools that go on and the one with no
other computer; still missing after 10 minutes stays away once; 30 seconds of
answers comes back once, and the back notice clears after 10 minutes or when
dismissed; back before the away time never stays away; a flapping computer is
missing, away and back once each; the away time follows the per-PC choice
(parsing 1 to 240 minutes); the report's round trip; and an unpaired computer
is forgotten. `ok` is true when every step passed. On the desktop, `ui_snapshot`
reads Home's `HealthIssue-presence-missing-<hostId>` ("Warning: Working with
less: gpu-box isn't answering. It hasn't answered for 2 minutes. ...") and
`HealthIssue-presence-back-<hostId>`, the Devices map's `Node-host:<hostId>`
("gpu-box, 192.0.2.10. Not answering for 2 min. ..."), and Settings'
`PresenceAwayMinutes` and `PresenceAwayStatus`. Choosing another time with
`ui_select` saves `node-presence.txt`, so it needs `--allow-ui-effects`. The
back notice's Dismiss (`HealthOpen-presence-back-<hostId>-dismiss`) is passive.

`helper_jobs_status` (optional absolute `dataDirectory`, default the current
user's) reads the desktop's `helper-jobs.json`: for each helper job kind
(`memory`: remembering and learning names after a reply; `action_naming`:
naming a character's emotes; `temperament`: deciding its touch temperament;
`touch_zones`: finding its touch zones; `eyes`: measuring its eyes) the last
`route` (`pool` with the `member` that ran it and its model, or `fallback`: the
conversation's own Thinking model after the reply finished speaking),
`priority`, `outcome` (`answered`, `no answer` or `failed: <why>`), `at` and
`waitedMs` (how long the fallback waited for the reply). It never holds a prompt
or an answer. `state` is `none` until the desktop runs a helper job with that
data directory. The jobs go to the [Thinking pool](CONVERSATION.md#the-thinking-pool) as its
Memory, Naming and TouchZones kinds (the eyes as a TouchZones job at the helpers'
low priority); `thinking_pool_status` shows the pool itself.
`helper_jobs_check` (no arguments) rehearses the desktop's production router
(`HelperJobs`) with a fixture pool and fixture answers (NOT AI): memory and
naming go to a free text member, touch zones wait for a running reply and fall
back while no member can see, then go to a vision member, and memory falls back
when no member is free. It returns each step with `passed` and the status file
it wrote. See [helper jobs](MEMORY.md#helper-jobs-on-the-thinking-pool).

`think_longer_status` shows Companion › **Deep thinking** as replies use it(optional absolute `dataDirectory`, default the current user's): `settings`,
`thinkLonger` (`enabled`, on by default and turned off by *Where it thinks* ›
*Off*; `effort` *Medium* or *High*; `timeLimit` and `hourlyLimit` *none*;
`delivery` *WhenFree* or *NextMessage*; `chosen`; `webResearch`, Companion ›
Deep thinking › *Web research*, off by default, and `researches`, whether it
applies with Thinking longer), `thinking` (the Thinking
route's `routeType`, `model`, `supportsTools`, `toolsRejected` from
`tools-unsupported.json`, `offered` (only where Deep thinking can run),
`researchOffered` (whether replies get `research`), `onThisPc`), `deepThinking` (this PC's `deep-thinking.json`: `file` *none*,
*loaded* or *unreadable*, `place` *SameAsThinking*, *Endpoint* or *Host*, `where`,
`model`, `hostId`, `hostRoute` (a paired computer's route a think goes to:
`martlet.gateway.deep-thinking-chat.v1` for its Deep thinking role, else its
Ollama role's) and `hostRole` (true for the Deep thinking role), an endpoint's `origin`, `ownKey` and `usesThinkingKey`
(never a key), `available` (and `parallel`, the same: a think always runs
alongside the conversation), `checksFit` (a second model in Ollama on this PC,
checked to fit beside Thinking's before each think) and `why` from the
production `DeepThinkingPlan`, its Thinking steps `use`, what a think `sends` at
that effort, such as `{"reasoning_effort":"medium"}` or `{"think":true}`,
`outputTokens` and `carriesTools`, true only with the Thinking model, and
`pool`: every place it thinks on, the first place then each computer ticked
*Think here too*, each with `computer` (its name), `where`, `place`,
`hostRole`, `available`, `rank` (lower goes first), `checksFit` and `why`, then
`usable`, `maxThinks` (slots in all), `atOnce` (how many thinks run at once:
one fewer than the slots when there are two or more, because the last free
slot stays free for quick jobs) and the pool's `available` and
`why`), `tools`
(`think_longer` and `cancel_thinking`, and `research` while web research is on,
exactly as the model gets them), the filled `prompt` and `researchPrompt`, and
`jobs`: the desktop's `background-jobs.json` (`active` and
`recent` jobs with `id`, `kind`, `state`, `progress`, `startedAt`,
`finishedAt`, `elapsedSeconds`, `timeLimitSeconds`, `offer`,
`resultCharacters`, `cut`, `problem`, `canceledBy`, `delivery` and `place`, the
computer it runs on; `startedLastHour`; `thinks`, each running think's `id`,
`where`, `computer`, `available`, `checksFit`, `why`, `rank`, `parallel` and
`attempts` (`thinking` is the first of them); `places`, each Deep thinking place
with `computer`, `where`, `available`, `rank`, `slots` and `heldBy` (the job IDs holding
it now); `maxThinks` (slots in all); each job's `inLine` (its place in the line for a
free computer, 0 when it isn't waiting), the broker's `line` (who waits, first
in line first) and `keptFor` (the other work each computer is kept free for,
such as `singing`)),
never a task or result. Read-only.

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

The Companion › Thinking pool page's `DeepThinkingPoolStatus` says how many
places think at once, and each paired computer's `DeepThinkingPool-<host>` box
(*diva in the Thinking pool*, ticked or not) reads; changing it saves
`thinking-pool.json`, so it needs `--allow-ui-effects`.

`reminders_status` shows Martlet's [reminders](CONVERSATION.md#reminders)
from a data directory's `shared-settings.json` (optional absolute
`dataDirectory`): `computers` with a reminders entry, `unreadable` entries
(a newer Martlet's), `pending`, and each reminder's `id`, `text`, `due`, `set`,
`setOn`, `state` (*Pending*, *Done*, *Canceled*, *Missed*), `settledBy`,
`settledAt`, `dueIn` and `marks` (`kind` *Bid* with `idleSeconds`, *Claim*,
*Done*, *Cancel* or *Missed*, `by` and `at`), plus the `reminders` `tool`
exactly as the model gets it. Read-only.

`setup_run_status` shows how applying the recommended setup to all your
computers stands ([Applying the recommended setup](CLUSTER.md#applying-the-recommended-setup),
[Configuring](CLUSTER.md#configuring)), from a data directory (optional
absolute `dataDirectory`): `state` (*none* without `shared-settings.json`,
*no-runs*, *loaded*), every computer's published run (`setup-run.<device>`
entries: `runId`, `startedBy`, `startedAt`, `updatedAt`, `finishedAt`,
`active`, `shown`, `summary` and each computer's `machineId`, `state`
*Pending*, *Configuring*, *Done*, *Failed* or *NeedsAttention*, `step`, `done`
and `steps`; an entry a newer Martlet wrote is `readable: false`), `plan` (who
does each job in `cluster.json`, with `failover` and `movedFrom`) and `sharing`
(Sharing work: each job's `shares`, `order` and `never`). Machine IDs, role
names and counts only. Read-only.

`setup_run_check` rehearses applying a recommended setup with the production
executor (`SetupExecutor`) on a fixture recommendation against simulated
computers (FIXTURE, NOT real hosts): gpu-box with two NVIDIA cards (through
Martlet there), desk-host (this PC's own host service), linux-box (SSH),
old-box (Martlet can't reach it) and laptop (no host service). The preflight:
Chatterbox Turbo's terms with the RTX 4090 by UUID, Parakeet chosen as the stt
variant with that variant's terms only, moving Thinking keeping its model, Audio2Face
showing and sending its default engine's terms, an NGC key the owner enters, old-box needing someone there, laptop unable to run
host roles, the Thinking pool joining by itself, thinking moving to each companion
PC's own Ollama (ready, with the download's terms), a hosted provider the owner
must choose in Companion (never made here) and the role still doing that job
kept until it moves, and the downloads added up.
The run: the host commands in order with their arguments (nothing for skipped
changes), the key only to Audio2Face and never in text, the terms recorded as
accepted, speaking on gpu-box with failover and thinking back to each PC's
choice in the plan, thinking switched through the Companion path, the hosted
provider reported as needing the owner with its plan unchanged, loudness
lip-sync off in the plan, the role whose job didn't move kept, Sharing work, one cluster check, a failed removal that
doesn't stop the others, a job this PC can't follow yet reported, the run
record (all waiting, then *Configuring* with the step and its count, then how
each computer ended) read back from the shared settings as
`setup-run.<device>`, a per-computer entry, and a change missing from the
review skipped. `passed` and each step's `passed` and `detail`. In-process; no
network, model or credential.

Home's `HomeConfiguring` (on a host PC `HostConfiguring`; passive: it opens
the Devices map) and `HomeConfiguringStatus` (`HostConfiguringStatus`) show
the newest run from any computer, for example *Configuring your computers: 1
of 3 finished. gpu-box: Installing Chatterbox Turbo (2 of 4). Started on
desk-b.*, then for ten minutes how it ended (*Your computers were reconfigured
at 6:30 PM: 2 done, 1 needs you.*), or a host role this PC changes now.
Hidden when nothing is configured.

`reminders_check` rehearses reminders with the production code (`Reminders`,
`ReminderBoard`, `BackgroundJobs`, `SharedSettings`) on two simulated companion
PCs whose entries merge through the shared settings: set in minutes and at a
local time on one, listed and canceled on the other, a refused call, both
offering when it is due, the PC used most recently (5 s against 10 minutes
idle) taking it while the other stays quiet, the conversation's message when
Martlet brings it up on its own and the notes when the user talks first, said
once and settled everywhere, a PC alone taking it at once and one far too late
let go. `passed` and each step's `passed` and `detail`. No model, network or
credentials.

`check_ins_status` shows Martlet's [check-ins](CONVERSATION.md#check-ins) from a
data directory (optional absolute `dataDirectory`): `settings` from
`check-ins.json` (`state` *none*, *loaded* or *unreadable*, and each check-in's
`id`, `name`, `custom`, `on`, `everyMinutes`, `outcome` *EmotesOff*,
*GazeUsual*, *Note* or *Say*, built-in `prompt`, the owner's `task` and
`facts`, and what it `does`), `desktop` from `check-ins-status.json` (written by
the desktop on a companion PC: `role`, the check-in `running`, `pool` with
`canRun` and the `member` and `model` that take them first, and for each
check-in `waiting`, `nextAt`, `runs`, `acted` and `last` with `at`, `result`,
`acted`, `member` and `ms`; never what was said, answered or reminded) and the
fixed `rules` (the 15-second look, the 3-minute minimum, the 10-second settle,
the 10-minute idle wait, the pace choices and `keptPace`, `repeatsSayings` and
`saidLatelyMinutes` for Saying the same things, the job kind
`check-in` at the `Helper` priority, not fast, stopped while the floor is Live).
Read-only.

`check_ins_check` rehearses check-ins with the production code (`CheckIns`,
`CheckInSettings`, `ThinkingJobBoard`, `HeldEmotes`, `ContextBoard`,
`BackgroundJobs`), FIXTURE facts and canned answers (NOT AI): the job kind's
rules; `check-ins.json` saved and read back, with a bad pace refused; when each
built-in check-in waits or runs (a young emote, a hidden character, the pace,
three times the pace after an answer that kept everything, you talking, nobody
at the PC, a young gaze, nothing new, no personality, too little said lately,
and *Check now* on one that is off); the message each one sends (Saying the
same things with each thing said and when), run on a
production job board with a fixture member; the answers read (`OFF {blush}`
after a `<think>` block, `**USUAL**` after thinking, a `REMIND:` bullet, a
`REMIND:` after a `<think>` block, `OK`,
`SAY:`) and odd answers that change nothing (`KEEP`, a tag it wasn't asked
about, chatter, `REMIND: nothing`); and what Martlet does: a reply's emote off
on a production `HeldEmotes` while the owner's try stays, a reminder on a
production context board that goes with one request only, and a check-in's
`SAY:` worded in its own words beside a due reminder. `passed` and each step's
`passed` and `detail`. No model, network or credentials.

`said_lately_check` rehearses [what Martlet said
lately](CONVERSATION.md#what-you-said-lately) with the production code
(`SaidLately`, the prompt, `MomentTurn`, `BoundedTextInput`, `CheckIns`) and
FIXTURE sayings at fixed times (NOT anything Martlet said): `what is noted`
(never a `[pass]` or nothing; one line, cut to 160 characters; the newest 10
within the hour), `lines with when` (`- 10:05 PM (12 min ago): "..."`),
`the note` (Companion › Prompts › *What you said lately*, your own edit of it,
and nothing when it is emptied or nothing was said), `which requests carry it`
(`Look`, `Report` and `PcAudio` do; `User` and `Touch` never, so a reply to you
starts as fast as before), `sent once, never kept` (the last notes of the
message, left out of what the conversation keeps, and the talk window's line
*the picture and 5 things Martlet said lately*) and `the check-in reads the
same lines` (Saying the same things, and its wait with too little said).
`passed` and each step's `passed` and `detail`. No model, network or
credentials. Live looks and replies count it in the desktop log's *Turn took*
line and the talk window's `LiveTurnInputs`, never what was said.

`think_longer_check` rehearses Thinking longer with the production scheduler
(`BackgroundJobs`), think runner (`BackgroundThink`), tool texts and request
layout (`ThinkLonger`), conversation runtime and Chat Completions adapter
against a fixture endpoint on 127.0.0.1 (canned replies, NOT AI; optional
`reasoningMs` 200-3000, default 1200, for the fixture's hidden reasoning).
`flow`: a reply says it'll think it over and calls `think_longer`; the tool
returns within a few ms (`toolTookMs`), after the first words, and the reply
completes with nothing more to say; the background request repeats the reply's
messages and tools unchanged before what Martlet said and the task
(`layout.sameStart`, `sameTools`, `then`), with Thinking steps on and the
Medium budget against the reply's off and 4,096 (`replyThinking`,
`thinkThinking`, `replyBudget`, `thinkBudget`); the result is brought up as
Martlet's own reply whose message carries it and whose request starts like the
reply's (`delivery.report`), after which nothing waits, and the notes for the
next message carry it too. `limits`: one think at a time (the second is refused
with what the model is told) beside a song job, the user's Cancel (mentioned
only with the next message, kept when that reply didn't happen), the time limit
(`TimedOut`), the hourly limit, Martlet's own cancel (nothing to bring up) and
the conversation ending (dropped), all on a fixture kind with limits;
`deepThinkingUnlimited`: Deep thinking's own kind has no time or hourly limit
(`timeLimit`/`hourlyLimit` *none*, 20 thinks `startedInARow`, one still running
past the fixture's time limit, `requestTimeHours` 24); and `fullLine`: on three
one-slot places, Deep thinking's own kind lets six thinks run or wait
(`runningOrWaiting`, `maxActive`) and runs two at once (`atOnce`), and a seventh
is refused as `busy` with those real numbers (`message`: *Martlet already has 6
thinks running or waiting, and it runs up to 2 at once...*, and `toldModel`). `plans`: the production `DeepThinkingPlan`
for thirteen setups (Same as Thinking with Thinking on this PC, on OpenRouter or
on a paired computer; OpenRouter with Thinking local; Ollama on this PC with
another model or Thinking's own beside Thinking local, or with the voice on
another computer or in this PC's host service; a paired computer that does
nothing else, also speaks, or also does Thinking; that computer's Deep thinking
role beside its Thinking, or with Thinking local), each `available` against
`expected` with `checksFit` and its `why`.
`parallel`: a think on a destination of its own (a second fixture endpoint
stands in for the other machine) keeps working while three replies go to the
conversation's endpoint (`replyFirstWordsMs`), is never stopped (`attempts` 1)
and its request has no tools, Thinking steps on and the conversation then the
task. `sideBySide`: the production `OllamaSideBySide` reads a fixture Ollama's
`/api/ps` and `/api/tags` (`readFromOllama`, `loaded`, `downloads`) and decides
whether a second model fits beside Thinking's (`decisions`, each `fits` against
`expected` with `needGb`, `roomGb` and `why`: a large model on a 24 GB and a
12 GB card, a small one on a 12 GB card free or filled by a game, only
Windows' total known, graphics memory unknown, Thinking's own model, a model
not downloaded, both already loaded, Thinking's already partly on the
processor), and once the think's model has loaded
whether Thinking's was unloaded or pushed partly off the card (`afterLoading`,
each `stops` against `expected`). `hostFit`: a 160-message conversation
fitted to a paired computer's gateway (16 KiB, 16 messages, no tools, the newest
kept, `inputTokens` 24,576 beside 8,192 for output). `pool`: the production
`DeepThinkingPool` of three paired computers' Deep thinking roles (diva and
ripley do none of the conversation's jobs, imouto also speaks: `configured`
with each `rank`), `maxThinks` 3 (slots), `atOnce` 2, the tool's *Up to 2 at
once; more wait in line* (`tool`) and the pool's *Up to 2 thinks run at once...*
(`plan`); the
production job list starts thinks as the desktop does (waiting in line when no
place may take them), places think-1 on diva and think-2 on ripley, both
working at once on their own fixture endpoints (standing in for the two
computers, each through a runtime of its own: `thinkingAtOnce`, `overlapped`).
A long job never takes the pool's last free slot while the pool has two or more
slots, so think-3 waits in line behind them (`waitedInLine`, with its
`progress`) and imouto's slot stays free (`heldWhileBusy`); a quick job (a
screen summary through the production `ThinkingJobBoard` on the same broker, a
simulated member, NOT a model) takes that slot at once while think-3 still
waits (`quickJob`: `on` imouto, `tookMs`, `held`). Think-3 then runs on the
first of diva and ripley to free up, never on imouto (`placed`,
`waitedInLine.startedOn`); every place is free once they finish (`freedAfter`)
and the next think goes to diva again (`nextPlacedOn`). `moment`: the production
`MomentTurn` plan for eight situations (`plans.cases`: a look that comes due
while the PC played and work finished, while only work finished, or alone;
finished work that comes up while the PC played or a look is due; the PC's pace
coming up while work finished or while Esc held it; you talking while all of it
waits), each `route` (*Reply*, *Report* or *Glance*) with what it takes along
(`takesPcAudio`, `takesFinishedWork`, `takesTheLook`), and `combinedTurn`: the
owner's example (a song and a report finish while the game plays and a look is
due) sent as one reply whose message `carries` the PC's marked lines and both
results in its notes (the song marked to offer), after which both jobs are
delivered (`newsAfter` false), and whose instructions start exactly like a
plain reply's up to the end of the One moment instruction
(`sameStartAsAPlainReply`, `sharedStartCharacters`; `momentInstruction` is the
text). `broker`: the production
background broker (`BackgroundPlaces`) and scheduler place four thinks on a
companion PC's three hosts, one general, one kept for image generation and one
that sings (`places` with `Rank`, `Slots`, `Duties` and `Standing`): the
general one first, then the singing one; the images one is the pool's last free
slot, which a long job never takes while the pool has two or more slots, so the
third and fourth wait in line (`placed`, `queuedBehind`, `inLine`) and a quick
job (an end-of-turn judge) takes that slot at once (`quickJob`). A song holding
the singing computer keeps both waiting when that computer's think ends
(`waitedWhileTheSongHeldTheSinger`); the third runs on the general one once
that frees up (`nextInLineRanOn`), the fourth waits until two slots are free
again (`lastInLineWaitedForTwoFreeSlots`) and runs on the singing one when the
song ends (`lastInLineRanOn`);
`decidedMs` is how long the four placements took (no model is asked). Each part has an `ok`; on
this PC the tool returned in 33 ms and replies beside a parallel think answered
in 2-7 ms. Loopback only; reads no credentials.

`research_check` rehearses [web research](CONVERSATION.md#web-research) end to
end with Martlet's own tool texts and job kind (`WebResearch`), scheduler
(`BackgroundJobs`), web client (`WebAccess`), research loop (`WebResearchRun`,
each step a `BackgroundThink` through the conversation runtime and Chat
Completions adapter) and report creation (`ResearchReports`), against fixtures
on 127.0.0.1 (NOT AI): a DuckDuckGo-like search page (an ad, results behind
redirect links), web pages (one with scripts and navigation, a PDF, one
redirecting to 192.168.1.1, one only a later step asks for) and a model with
canned answers. `settings`: off by default, on only with Thinking longer on,
saved lean. `guard`: which addresses count as public (`WebAccess.IsPublic`: no
loopback, private, shared, link-local, cloud metadata, ULA, multicast or mapped
private addresses). `flow`: a reply says it'll look into it and calls
`research`; the tool returns before the reply ends (`toolReturnedMs`,
`replyMs`) and the job is still running when the reply completes
(`researchStillRunning`); the job (`research-1`, `Succeeded`, `offer` true,
`Researching`, 12 minutes, 4 an hour) made 2 searches (`searchQueries`: the
topic, then the model's), read 3 pages and found 2 unreadable (the PDF, and the
redirect to a private address, never followed: `privateRedirectFollowed`) in 2
model steps, each a background message under 16 KiB carrying the numbered
sources (`steps`); the `note` the conversation gets marks it to offer first and
says to call `perform_creation` with the report's id; the report is kept as a
`report` creation in a temporary Creations library (`creation`) and showing it
writes a page with its source links (`page.links`) and no scripts (`shown`).
`tool` and `prompt` are exactly what replies get. `limits`: a second research is
refused as `busy` (and what the model is told) while a think runs beside it,
Cancel ends it as `Canceled`, the fifth in an hour is refused (`hourly_limit`),
and a failed first search fails the job (`failedSearch`). `limits.placement`:
on Deep thinking's places, a think holding the only place keeps research from
starting (the message names it); research is a long job, so with one other
place free it is refused too, because the pool's last free slot stays free for
quick jobs (`lastFreeSlot`, where a screen summary takes it at once), and with
two other places free it runs on the one sharing least with the conversation
(`researchOn`). Each part has an `ok`.
No real web search or model is used; reads no credentials; the temporary folder
is deleted.

`songs_status` shows [singing in conversation](CONVERSATION.md#singing-in-conversation)
(optional absolute `dataDirectory`, default the current user's):
`backgroundWork` (Thinking longer, which the song tools come with), `creations`
(the song creations in the shared Creations library: `count`, and each song's
`id` (its key), `durationSeconds`, `lines`, `words`, `wordsEstimated`,
`wordTimingSource`, `bpm`, `titleCharacters`, `lyricsCharacters`, `generator`,
`converter`, `quality`, `voiceMatch`, `fixture`, `mouthSource` (*Audio2Face*,
*Visemes* or *Loudness*) and `mouthNote`, its `assets` (name, media type, bytes),
whether they are all `here` on this computer, `createdBy` and `createdAt`; never
a title or words), `desktop` (the desktop's
`songs-status.json`: `offered`, `songs`, `output` (*Martlet's voice output*, or
the silent fixture output under `MARTLET_SINGING_FIXTURE=1`), `playing` with
`songId`, `state` (*Starting*, *LeadIn*, *Singing*, *Stopping*...),
`positionSeconds`, `durationSeconds`, `line`, `lines`, `section`, `from`,
`leadInBars`, `leadInSeconds`, `fadeInMs`, `vamps`, `ducked`, `lipSync` (the
mouth track's `source`, `frames` and `channels`, how many mouth frames were
`sent` to the character, their `averageSendMs`, and the `route`: *mapped mouth
shapes* through the character's mouth mapping or *mouth opening*), its `stop` plan
(`musical`, `requestedSeconds`, `vocalsEndSeconds`, `vocalsFadeMs`,
`backingFromSeconds`, `backingFadeMs`, `silentAfterSeconds`) and `failure`;
`lastStop` with `songId`, `atSeconds`, `line`, `lines`, `section`, `nextLine`,
`cause` (*UserWords*, *Button*, *Martlet*, *Ended*, *Failed*), `ended`,
`wordsCharacters` and a button's `reason`; and `noteWaiting`), the song job
`kind` (one at a time, 4 an hour, 15 minutes, `offer`, *Making a song*), the
three `tools` exactly as the Thinking model gets them and the filled Singing
`prompt`. Read-only.

`song_playback_check` runs the production playback (`SongTransport`,
`SongMixer`, `SongPlayer` pumping a fixture output that plays ten times faster
than real time and keeps what it is given; nothing is played aloud) on the
FIXTURE - NOT AI tone song (40 s, 96 BPM, ten lines in verse, chorus, verse 2 and
chorus 2), or on a song creation (`songId`: its key, with its `dataDirectory`), and measures
what it produced. `resolve`: where `from` points for start, a section, *second
verse*, `line:3`, a time and two misses. `leadIn` (resuming line 4, rendered from
the backing alone and the vocals alone): `entrySeconds` on a downbeat,
`leadInBars`, `fadeInMs`, the backing's gain in its first 10 ms, at half the fade
(equal power: 0.707) and after it, and the vocals' peak before the gate (0) and
level after the onset. `vamp`: Martlet still talking at the line, the band
repeats the bar twice and the vocals are first heard two bars later
(`vocalsFirstHeardAfterSeconds` against `expectedAfterSeconds`). `duck`: -12 dB.
`played`: sung from the top and stopped musically mid-line by the user's words
(`musicalStop`: the stop record's line and section, the word's end, the beat the
band fades from and over how long, the planned and measured silence and the
note), then resumed (`resume`: the line where it stopped, its lead-in, 2 vamps,
the states *LeadIn*, *Singing*, *Stopped*) and stopped with Esc (`quickStop`: a
300 ms fade from where the audio already handed to the output ends). `lipSync`:
the mouth tracks Martlet makes from the vocals stem and how far each opens from
the vocal onsets (`onsets`, `matched`, `medianOffsetMs`, `meanAbsoluteOffsetMs`,
`p90AbsoluteOffsetMs`, `good`): `audio2Face` (run once over the vocals when a
service answers on 127.0.0.1:52000, otherwise *NOT RUN* with why), `visemes`
(from the sung words, `words.estimated` when spread over each line's singing),
`loudness`, a song creation's own `stored` track, which one was `used`, and
`playback`: the player resuming line 4 with its lead-in on the vocals alone,
the mouth it sends on the playback clock (`mouthUpdates`), whether it stayed
`closedDuringLeadIn`, and the offsets between the mouth opening and the onsets of
the vocals it actually played. Each part has an `ok`; on this PC the musical stop
went silent 0.88 s after the request (planned 9.375 s, measured 9.370 s), Esc
0.4 s after it, the viseme track opened +10 ms from all 10 onsets and, played
after a lead-in, within 1-4 ms median (90% within 20 ms).

`echo_check` checks [echo reduction](CONVERSATION.md#echo-reduction)
(Companion › Listening › **Reduce echo from my speakers**; optional absolute
`dataDirectory`, default the current user's, and optional `delayMs` 0-300,
default 60): `reduceEcho` (the saved choice, on by default) with
`reduceEchoSource` (`saved` or `default`), `bargeIn` (*Let me interrupt Martlet
by talking*, optional and off by default) with `bargeInSource` (`saved`,
`default`, or `reset` for a file saved before barge-in became opt-in that had
it on only by the old default), `wordCheck` (*Word check*: `Relaxed`, `Normal`
by default, or `Sensitive`) with `wordCheckSource` (`saved` or `default`), and
`canceller` (`WebRTC AEC3` once
the native canceller loads, otherwise null with `cancellerProblem` and `ok`
false). Its `rehearsal` runs the production microphone path
(`MicrophoneCapture`, `EchoReducer`, the WebRTC canceller, the capture
normalizer) twice on one synthesized scene, with and without echo reduction,
using a fixture microphone and fixture speaker loopback (48 kHz stereo float
with device-style timestamps and a pause in playback) on a simulated clock: no
microphone or speaker is opened and nothing plays. A synthesized Martlet voice
reaches the microphone through a simulated room (`delayMs`, reflections, about
6 dB down) beside the user's own synthesized voice: 0-4 s only Martlet speaks
(`martletOnly`), 4.5-6 s only the user (`userOnly`), 6-8 s both (`bothTalking`,
barge-in). It returns `state` (the reducer's report, `Active`), `frames`,
`speakerFrames`, `deviceReducedDb` (the device's own measure over every frame
while the speakers played, the user's voice included), and per part the levels
`withoutDb`/`withDb` (dBFS), `martletOnly.reducedDb` and
`firstSecondsReducedDb` (0-1.5 s, while the canceller learns the room),
`userOnly.keptDb` and `bothTalking.userAloneDb`, plus `speechFrames*`: the
20 ms frames Martlet's own voice-activity detector counted as speech. Its
`talkOver` runs the voice gate (`TalkOverDetector`, `requiredMs` 1000 and
`gapMs` 500, with the capture's own `EchoTimeline`, `speakersRemovedDb` 10),
which tells the user's own voice from the speakers' sound (what actually stops
Martlet is real words: `utterance_filter_check`),
over the cleaned recording the way always listening does, for `martletOnly`,
`userOnly`, `shortSound` (the first 0.8 s of the user alone) and
`bothTalking`: `userFrames` and `speakerFrames` (loud 20 ms frames that were
a voice or the speakers' sound), `timeline` (`room`, `user`, `speakers`
10 ms frames) and `talkedOver` with `afterMs`. `ok` is true when the reducer
was active, Martlet's echo got at least 20 dB quieter, the detector heard it
without reduction but not with it, it still heard the user alone (kept within
3 dB) and over Martlet, and `talkOver.ok`: Martlet's echo and the short sound
never talked over it, while the user over Martlet did, no sooner than
`requiredMs`. `listensWhileSpeaking` says whether always listening goes on
while Martlet speaks with the saved choices: `bargeIn`, or `reduceEcho` with
echo reduction that works (`EchoReducer.Works` with the rehearsal's own
timeline; `rehearsal.reducing` is that capture's echo state: `true` while the
canceller cleaned it, `false` once lost, `null` if it never started); `ok`
also requires the rehearsal's echo reduction to work. Without a canceller
`listensWhileSpeaking` is `bargeIn`. It contacts nothing.

`utterance_filter_check` checks [listening for words](CONVERSATION.md#listening-for-words)
and barge-in (Companion › Listening › **Word check**; optional absolute
`dataDirectory`, default the current user's, `sensitivity` `relaxed`, `normal`
or `sensitive` to override the saved choice, `audio` default true, absolute
`speechDirectory` where Parakeet is downloaded, default the current user's,
`parakeetModel` to choose the model, default Listening's when it is downloaded,
else v3, else the one downloaded,
and absolute `martletDirectory` for the sherpa-onnx runtime, which the script
fills with this checkout's Desktop build). It runs the production
`UtteranceFilter` and `BargeInPolicy` on `samples` (up to 64 of `text` with
optional `voicedMs`, `speechMs` (how long the voice went on; `voicedMs` when
absent), `meanProbability`, `minimumProbability`,
`noSpeechProbability`, `averageLogProbability`, `engine`, `afterQuestion`,
`persona`, `playback` `reply` or `song`, and `expectKeep`/`expectInterrupt`),
by default a fixed set with the outcome Normal must give (fillers, laughter,
sound tags, "Thank you." from noise or said clearly, subtitle credits, lone
words, short answers, stop words, backchannels, too many words for the speech,
"I'm gonna make it public." with little loud voice in a second of speech, a
whisper loop, Martlet's name, a song that only stops when asked, and the
"Yeah." Parakeet and whisper.cpp wrote for coughs on this PC with their
measured evidence; another sensitivity only reports what it makes of them).
Each sample returns `keep`, `kind`, `Reason`, `Words`, `shown` (the talk
window's *Ignored ...* note), `interrupts` and `interruptReason`. It returns
`wordCheck` (the one used) with `savedWordCheck`/`savedWordCheckSource`,
`bargeIn` (the saved choice), `limits` (`UtteranceFilter.For`),
`bargeInPolicy` (`voiceBeforeCheckMs`, `firstRecheckMs`, `recheckMs`,
`pauseMs`, `wordsToInterrupt`) and `filterCost` (`microsecondsPerCall` over
thousands of calls: what the filter adds to a reply). With `audio` and
Parakeet downloaded, `audio` runs fixtures through the real local
speech-to-text path (`ParakeetEngine` with the chosen model, named in `model`
and `engine`): "Stop!", "Wait,
hold on a second.", "Can you tell me more about that?", "Yes." (after a
question), "Yeah.", "Mmmmmm.", "Hmm." and "Ha ha ha ha!" said by a Windows
voice (System.Speech, rendered to memory, never played), "I'm gonna make it
public." said quietly over a fan's hum (`quiet-room`: only about half of it is
as loud as a voice must be to start), plus a hum, two
coughs, a breath, typing, music and noise, each after 0.3 s and before 1 s of
faint noise. Per fixture: `voicedMs` (loud frames by the production
voice-activity detector) and `speechMs` (from its onset to the silence after
it), Parakeet's `transcript`, `evidence` and
`transcribeMs`, the filter's verdict, and `bargeIn`: the production
`BargeInGate` fed 20 ms at a time as if Martlet were speaking, each quick check
transcribing the stretch so far (no other check starts while one runs, as in
the listener) with its `quick` transcripts, probabilities, voice and speech
(`VoicedMs`, `SpeechMs`) and milliseconds,
`interrupted`, `reason` and `afterMs` (from the start of the voice to the
decision). `stopDelayMs` summarizes the fixtures that stopped Martlet. `ok`
needs every expectation met: words kept, non-words and noise dropped, stop
words and the question stopping Martlet, backchannels and non-words never.
Nothing is recorded or played and nothing leaves this PC; without Parakeet,
`audio.ran` is false with the reason.

`barge_in_check` checks [pause and decide](CONVERSATION.md#voice-latency-streaming-overlap-and-barge-in)
(Companion › Listening › **When you talk over Martlet**; optional absolute
`dataDirectory`, default the current user's). It simulates words said over a
reply and returns the production verdict (`BargeInJudging.RuleAsync` with
`RulesBargeInJudge`) for `samples` (up to 64 of `heard`, with optional
`sentence` (what Martlet is saying), `recentReply`, `voicedMs` (default 900)
and `expectVerdict` `interrupt` or `notForMe`), by default a fixed set: a stop
word and Martlet's name (a clear cue, never judged), "Yeah.", agreeing,
laughing along and Martlet's own sentence heard back (not for Martlet), and a
question and a new request (for Martlet). Each sample returns `verdict`,
`reason`, `source` (`Cue` or `Judge`), `judge`, `judgeMs`,
`quickCheckInterrupts`, `cue` and `action` with the saved choice (*keeps
playing*, *stops at once*, *pauses, then stops* or *pauses, then plays on*).
`deadlines` runs a fixture model judge (NOT AI) slower than the deadline
(`judgeDelayMs`, default 1000; `deadlineMs`, default 400): the local rules
must decide (`source` `Timeout`) within about the deadline; a judge in time
must be used (`source` `Judge`). `holds` runs `BargeInHold` frame by frame on
a simulated clock: quiet after a not-for-Martlet verdict plays on, talking on
past 1.5 s stops, an interrupt verdict stops, and no verdict plays on at the
pause's 4 s limit; each with `outcome`, `why`, `source`, `pausedMs` and
`voiceMs`. It returns `behavior` (`PauseAndDecide` or `StopAtOnce`) with
`behaviorSource`, `bargeIn`, `wordCheck`, `timings` (`deadlineMs`,
`keepTalkingLimitMs`, `quietToResumeMs`, `maximumPauseMs`,
`voiceBeforeCheckMs`, `resumeFadeMs`), `modelJudge` (the Thinking pool's model
judge, `ModelBargeInJudge`, with fixture answers (NOT AI) in place of a pool
member: a verdict is used (`source` `Judge`, `judge` *Thinking pool*), no
member lets the rules decide at once (*no Thinking pool judge was available*)
and an answer without a verdict lets them decide; each with `answer`,
`verdict`, `source`, `reason`, `tookMs` and `promptLines`) and `ok` when every
expectation held. `thinking_pool_status` says whether the pool has a member
that can run the judge.
Nothing is recorded or played and nothing leaves this PC. `spoken_reply_check`
`paused` rehearses the pause and resume through the production runtime; the
talk window's `LiveBargeIn` shows the last real decision.

`discord_call_check` checks [Martlet in your own Discord calls](DISCORD.md#martlet-in-your-own-calls)
(Companion › Discord › **Martlet in your Discord calls**; optional absolute
`dataDirectory`, default the current user's). `saved` is the mode from
`discord-calls.json` (`on`, off by default; `capture` `DiscordApp` or
`EverythingButMartlet`; `seeSpeakers`; `ownerNameSet`, never the name;
`output`, the chosen output's name; `alsoSpeakers`; `bargeIn`;
`cameraBackground`; `cameraPicture`, where a saved picture came from,
`cameraPictureSaved`, and `cameraTool`, whether replies get
`set_camera_background`). `doctor` checks this PC without recording or playing:
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

`sound_digest_check` checks [describing PC sounds](CONVERSATION.md#describing-pc-sounds)
(Companion › Listening › Watch along › **Describe PC sounds**; optional absolute
`dataDirectory`, `martletDirectory` and `wavFile`). `saved` has `HearPc` (off
by default), `DescribePcSounds` (on by default) and `Source` (`saved` or
`default`). `status` is the desktop's `sound-digest.json` or null: `on`, the
active `judge` and `judgeKind` (`pool` or `cpu`), `runs`, `lines`, `dropped`,
`skipped`, `lastStep`, `lastJudge`, `lastAgeSeconds`, `lastMs`,
`maximumAgeSeconds`, `everySeconds` and `clipSeconds`; it never holds a line
or a sound. `tagger` says whether `martletDirectory` (by default the installed
release; `Invoke-MartletMcp.ps1` passes this checkout's Desktop build) has the
bundled sound tagger and how long it took to load. `rehearsal` plays a
**FIXTURE** clip (synthesized music with hand claps for 10 s, then silence)
through a fixture loopback on a simulated clock, `PcAudioCaptureFactory`,
`MicrophoneCapture` and the capture normalizer into `PcSoundBuffer`, then runs
one `SoundDigestScheduler` tick with `CpuSoundJudge`: `bufferedSeconds`,
`activeShare`, the tagger's top `tags` with scores, `tagMs`, `step`, the
`line` and `bufferClearedWhenOff`. `file` tags `wavFile` (16 kHz mono 16-bit)
the same way. `ok` needs the tick to start a judge, a line and the buffer
cleared when the digest goes off. It records, plays, sends and saves nothing.

`pc_audio_check` checks [hearing what this PC plays](CONVERSATION.md#hearing-what-this-pc-plays)
(Companion › Listening › Watch along › **Hear what this PC plays**; optional
absolute `dataDirectory`, default the current user's): `hearPc` (the saved
choice, off by default) with `hearPcSource` (`saved` or `default`),
`handsFree` and `reduceEcho` (it works only with always listening, and through
speakers wants echo reduction on). `windows` says whether this Windows can hear
the PC without Martlet's own sound (`withoutMartlet`, with the process
loopback's `format`, or `problem` and the `fallback`): a process loopback that
leaves out the MCP server's own process is set up and closed again without
starting, so `recorded` is always false. It also reads the outputs' sessions
(never their sound): `output` (the output you hear, Windows' default),
`elsewhere` (another output an app other than Martlet streams to right now,
such as a voice changer's or microphone app's virtual cable, or null) and
`hears` (every app's sound except Martlet's, or only what plays on `output`
while listening to the PC holds off for Martlet's voice, which is what
happens whenever `elsewhere` is set). Its `rehearsal` runs the production
path (`PcAudioCaptureFactory`, `MicrophoneCapture`, the capture normalizer and
the voice-activity detector with the defaults the PC listener uses) on a
fixture loopback and a simulated clock: a synthesized video voice 0-3 s, the
video paused 3-6 s with no packets at all (as a real loopback), the voice
again 6-9 s, then nothing, 12 s in all. It returns `recordedSeconds` (12 when
the gaps were filled), `segments` (`startS`, `endS`, `endedAtS`),
`endedInPause` (the pause ended the first utterance, so always listening sends
it), `resumed` and `pauseSpeechFrames`. `yourVoice` runs the production matcher
(`PcEcho`) that leaves out your own voice when this PC plays it back: its
`rule`, `share` (the share of the PC's words, in order, that must be yours),
`ok` and `samples` (`scene`, `spoken`, `played`, `expected`, `leftOut`: your
voice played back, including the two reported pairs, transcribed the same or
differently or in part, is left out; a video playing while you talk or one you
quote is kept). `ok` is true when all hold, every sample came out as expected
and the fixture's Martlet-free source was used. It reads no credentials and
contacts nothing.

`pc_activity_check` checks [where what this PC plays comes from](CONVERSATION.md#where-it-comes-from-and-what-you-are-doing)
(optional `seconds`, 1-20, default 3). `live` runs the production
`PcActivityMonitor` with `WindowsPcActivitySource` on this PC for those seconds
(the volume mixer's session meters, the apps' windows, the window in front, full
screen and the GPU Engine counters; nothing is recorded, played, kept or sent,
so `recorded` is always false, and raw window titles are never returned):
`ticks`, `averageTickMs`, `problem`, the `summary` and the `note` a reply would
read (the context board's `activity` note), and `apps`: every app with an audio
session (`app`, `kind`, `label`, `peak`, `audible`, `foreground`, `fullScreen`,
`exclusiveFullScreen`, `gpuPercent`, `programKnown`, `windows`). `rehearsal`
runs FIXTURE apps, levels and lines on a simulated clock: `classify` (the
production classifier on fixed apps such as Plex full screen, a YouTube video
or a Twitch stream in a browser, Netflix in a browser, a private window, Discord,
Zoom, Google Meet, Spotify, a game in a Steam library, an unknown app full screen
with the graphics card busy, OBS and VLC with a movie or a song; each `scene`,
`kind`, `label`, `expected`, `ok`), `lines` (a voice chat over a game, the game
alone, a YouTube video and silence: each fixture line's `[PC audio] From ...:`
text against `expected` with `mediaOnly`, then the `note` against `expectedNote`), `speakers` (the
production check `PcEcho.Speakers` on a simulated clock that tells the
microphone hearing this PC's speakers apart from you: a video or a game line
the microphone heard at that same moment is the speakers; the same words the
video said 10 seconds before, your voice played back by a voice changer, a game
while a voice changer also played, a voice chat repeating you, a short answer,
common words scattered through a long video, you quoting the video and you
talking over it are not; each `scene`, `heard`, `played`,
`playedFrom`, `expected`, `fromSpeakers`, `ok`) and `prompts` (the built-in
*What this PC plays* and *Always listening* prompts with what they must say,
and `missing`). `ok` is true when the live look had no problem and every
fixture came out as expected. It reads no credentials and contacts nothing.

`logs_timeline` reads the logs as the desktop's
[Diagnostics page](DIAGNOSTICS.md#diagnostics-page-and-shared-logs) shows
them (optional absolute `dataDirectory`, default the current user's): this PC's
`desktop`, `avatar-renderer` and `host-runs` with their rotated copies and the
other computers' lines log sharing collected (`logs\network-logs.json`: other
desktops' parts and every host's `gateway`), merged into one timeline, newest
first, of `{at, level, source, component, seq, relayedBy, message}` (a stack
trace or output lines stay in their line's `message`; `relayedBy` names the
desktop that passed a line on). Optional filters: `level` (`all`, `warnings`,
`errors`), `component` (also `gateway`), `source` (a computer's ID), `contains`
(at most 200 characters) and `lines` (1-1000, default 200). It also returns
`device` (this PC's ID as a log source), `logsFolder`, `total`, `errors`,
`warnings`, per-`components` and per-`sources` counts, `network` (`state`:
`none`, `loaded` or `unreadable`, and its `lines`) and `matching`. Read-only;
it contacts no host.

`logs_export` is Diagnostics' **Save logs to share** without the window:
`outputPath` (required; absolute, ending `.zip`, in an existing folder; an
existing file is never replaced) and optional `dataDirectory`. It writes the
same ZIP with the production writer (`LogBundle`): `about.txt` and
`martlet-logs.txt` with every line once, oldest first, from this PC's logs and
`network-logs.json`. It returns `path`, `bytes`, `files` (name and size),
`lines`, `computers`, `errors` and `warnings` (last 24 hours), `oldest`,
`newest`, `network` and `about` (the text of `about.txt`). It writes only that
file and contacts nothing.

`logs_share_selftest` (no arguments) rehearses shared logs end to end with the
production code (`src\Martlet.NodeLinkCheck`, mode `logs`, `LogRehearsal.cs`;
returns `{exitCode, report}`): two real gateways (`lab-logs-1`, `lab-logs-2`;
Kestrel, pinned TLS, signed requests, an in-memory `logs.json`) and three
simulated desktops (`lab-desktop-a..c`) with real logs folders (`desktop.log`
in Martlet's format, read with `LocalLogs`), the desktop's paired client
(`HostLogPeer`) and the real sharing engine (`Martlet.Core.Logs.LogShare`). Its
steps: A's lines (an error with its stack trace among them) reach both hosts
and A holds both hosts' own lines; C, which reaches only `lab-logs-2`, has its
lines passed on to `lab-logs-1` by A (recorded as passed on by A), and each
host's own lines reach the other; a new desktop B holds every computer's lines
and C gets them through `lab-logs-2`; repeated runs keep every line once on
every host and desktop; a host that was down catches up after restarting with
its saved log (the run while it was down says *Waiting for lab-logs-1*); B's
`network-logs.json` survives a restart; Save logs to share on B holds every
computer's lines once in `about.txt` and `martlet-logs.txt`; a host that
comes back from a power cut without its newest lines (it restarts from an older
saved `logs.json`) is read from the start again and gets them back; an unsigned
request is refused. Synthetic lines; loopback only; the folder is deleted.

`host_connections_selftest` (no arguments) rehearses how the desktop connects
to a paired host and reports its status (`src\Martlet.NodeLinkCheck`, mode
`host-connections`, `HostConnectionRehearsal.cs`; returns `{exitCode, report}`).
One real gateway (`lab-connections`; Kestrel, pinned TLS, signed requests) runs
on 127.0.0.1 behind a loopback TCP forwarder that the check stops and starts at
the same address. A simulated desktop checks the host as the 15-second sync does
(a new `Audio2FaceHostConnection` per check: routes, then the plan copy) and
logs through the desktop's status tracker (`HostAnswers`). Its steps: twelve
checks share one kept TCP and TLS connection (`HostRoutes` connections dialed
and connections the forwarder accepted are both 0 after the first); the host
stops and one missed check logs nothing, the second logs `Host lab-connections
stopped answering: ...` once, later misses log nothing, and the route status
says the home address didn't answer (refused); the host comes back and the next
check logs `Host lab-connections answers again.` once over one new connection;
a host that misses every other check logs nothing. The report's `log` holds the
lines. Loopback only; writes nothing. Windows running out of ports
(`NoBufferSpaceAvailable`) is not simulated; unit tests check its wording.

To drive the visible desktop, start `Martlet.Desktop.exe` yourself in the **same
interactive Windows session** (ideally with a disposable `--data-directory`).
Call `ui_connect` with that process ID. `ui_snapshot` returns window accessible names,
automation IDs, enabled states, checkbox states, and selected read-only status
fields (a text block's text, or a button's accessible name); it does not dump arbitrary editable fields or credentials.
It returns the first 200 controls; `idPrefix` keeps only those whose automation ID
starts with it (`TouchZone` for Companion › Touch › Touch zones, below the
long emotes list).
A status text whose details sit in its tooltip (the talk window's `LiveVisionStatus`,
`LivePcAudio`, `LiveChattiness` and `LiveContext`) also returns them as `help`
(its accessible help text).
`{"name":"ui_snapshot","arguments":{"layout":true}}` also returns each control's
screen `bounds` (`[x, y, width, height]` in pixels) and, for text controls, the
`textBounds` of their first line of text (geometry only, never the text), so
alignment can be checked: in the talk window, the empty box's hint
`LivePlaceholder` must have the same `bounds` position as the `textBounds` of
text typed into `LiveInput`. Each message bubble (`LiveMessage-*`) is only as
wide as its words: for a one-line message, `bounds` is about 4 pixels wider
than `textBounds` (room for the caret), even under a longer caption or note.
`windowStates` lists each window's `name`, automation `id`, `enabled`, and
whether its frame is `resizable`, `minimizable` and `maximizable`, whether it is
`minimized`, whether it is the `foreground` window (has the focus) and whether
it is `clickThrough` (the mouse passes through it to the window under it,
`WS_EX_TRANSPARENT`: the character overlay while click-through is on); with
`layout` it adds the window's `bounds` and its monitor's `workArea` (the screen
minus the taskbar), both in physical screen pixels. Every Martlet window opens
within that work area at any display scale: no larger than it (minimum sizes
shrink to fit), centered over the window it belongs to (or the main window),
title bar on screen. Dialogs that size to their content (confirmations, the
problem dialog, *Add a voice*, *Add a character*, API keys, host input, a
computer asking to join) are resizable with only Close, scroll when the screen
is shorter than they are, and stay inside the work area as they grow. To check
a higher display scale than this PC uses, set `MARTLET_SIMULATE_DISPLAY_SCALE`
(a percentage, such as `300`) before launching the desktop (for example before
`Invoke-MartletMcp.ps1 -Desktop`): windows then fit a work area shrunk from its
top-left corner as that scale would shrink it, which `workArea` does not show
(at 300% on a 2560 x 1332 work area at 225%, windows stay within
`[0, 0, 1920, 999]`).
Every read-only text box has a Copy button `Copy-<box ID>` (the box's
automation ID, or its `x:Name` when it has none: `Copy-HostRunOutput`,
`Copy-PrepareOutput`, `Copy-SupportReport`, `Copy-LogDetail`,
`Copy-FoundationStatus`) above its top-right corner (its `bounds` sit above the
box's text and scroll bar, which keep the box's full width), shown only while
the box has text. Snapshots return
its label (*Copy*, or *Copied*/*Couldn't copy* for about three seconds after a
click), never the copied text. A run window's and *Prepare this computer*'s Copy
starts with *Martlet <version>: <title>* (and *SSH target: ...* for Prepare)
and *Status: <status line>*, then a blank line and the output.
`ConfirmationCopy` (confirmation dialogs: version, title and question) and
`HostInputCopy` (install and prerequisites dialogs: version, title, heading,
message and the terms shown; never what was typed) work the same way. Copy
buttons write the clipboard, so they need `--allow-ui-effects`; check the
outcome with `ui_snapshot` and, on the dev machine, `Get-Clipboard`. The
problem dialog (`ProblemDialog`: an unexpected error, or *Martlet couldn't
start*) returns `ProblemHeading`; its report `ProblemText` (exception text and
paths) is not returned, `Copy-ProblemText` copies it, `ProblemClose` is
passive and `ProblemOpenLogs` opens Explorer (`--allow-ui-effects`).
`ui_connect` also attaches to a Martlet that shows only its problem dialog.
Status fields include `VisionNow` (Companion › Vision's *Now* line: *On. Martlet looks at your whole screen occasionally. Comments: Normal.* by default, or *Off. ...* once turned off; a saved `talk-preferences.json` keeps its choices, and nothing is captured until Start watching), `VisionToggle` (*Turn vision off* while vision is on, *Turn vision on* otherwise; clicking it saves `talk-preferences.json`, so it needs `--allow-ui-effects`; the `VisionSource-ActiveWindow`, `-ActiveScreen`, `-Camera` and `-Url` choices report `selected`, `-ActiveScreen` by default), `VisionStatus` (Companion › Vision: whether the Thinking model can see, or has been retired, and the fix), `VisionDisclosure` (Companion › Vision: exactly what vision captures and sends and where, including that what you type or say goes with the newest picture and, for the whole screen, the looks at notifications and flashing taskbar buttons), `VisionGazeStatus` (Companion › Vision › Glances at your screen: the character's usual gaze, such as *The character follows your mouse.*, why Martlet can't decide yet (vision off, a camera, the character hidden, not watching yet) or what the eyes are on now; its `VisionGaze-Mouse` (*Keep its usual gaze*) and `VisionGaze-Martlet` choices save `talk-preferences.json`, so they need `--allow-ui-effects`, and `character_gaze` reads the saved choice as `saved`), `FallbackNow` (Companion › Thinking › If Thinking fails: the saved fallback endpoint and model and whether it has its own key, uses Thinking's or none; never the key), `FallbackKeyStatus` (what the fallback's key box will do; its fields `FallbackProvider`, `FallbackBaseUrl`, `FallbackModel`, `FallbackKey`, `FallbackConsent` and its `FallbackSave`/`FallbackOff` buttons write settings or a key, so they need `--allow-ui-effects`; `logs_tail` shows each use as *Thinking failed (...) ... the Thinking fallback ... answered instead*, and a rate-limited glance shows in `LiveVisionStatus`'s `help` as *Last look 10:17 PM: the provider is limiting requests. Looking again in 1 minute.*), `RepliesNow` (Companion › Replies: that Martlet asks for replies of one or two sentences, the max reply length ceiling in effect, 4096 tokens including any hidden thinking on a Chat Completions or paired-host Ollama route unless set, whether Thinking steps are off (the default) or on, and the other saved settings), `RepliesThinking` (Companion › Replies › Thinking steps: *Off*, the default, or *On*; choosing one with `ui_select` saves it, so it needs `--allow-ui-effects`) and `RepliesThinkingStatus` (how the Thinking route takes it: *Used by Ollama on this PC.*, *Depends on the model at ...* for servers where it depends on the model, or not used on the OpenAI route), `SetupCloudHint-Thinking` (the cloud provider's recommended Thinking model, or a retired-model warning), `SetupJobNow-Thinking`, `SetupJobNow-Voice` and `SetupJobNow-Listening` (the job's *Now* line: where it runs and the model, such as *Ollama on this PC: gemma4:12b*), `SetupJobNetwork-Thinking`, `-Voice` and `-Listening` (shown when a host does the job for your Martlet network: *Your Martlet network does thinking on diva-host, as chosen on desktop-diva. This PC switches to it as soon as it can: pair diva-host with this PC first.*, or *Your other computers use this PC for thinking, through diva-host.*; a computer that hasn't chosen yet then selects `Place-Thinking-Computer` and its *Now* line reads *Not set up on this PC yet.*), `SetupCloudKeyStatus-Thinking`, `-Voice` and `-Listening` (under *A cloud provider*, what the key field does for the chosen provider: keep the saved key, use again *Your OpenRouter key from before*, set aside when the job left that provider, or ask for one; never the key; `SetupCloudSave-<page>` and `SetupUseLocalThinking` save the route, so they need `--allow-ui-effects`, and keys set aside never block them), `SetupOldKey-Thinking-<n>`, `-Voice-<n>` and `-Listening-<n>` (the page's *Keys from before*, shown only while the job has a key Martlet set aside when it stopped using it, newest first: *Your OpenRouter key* or *The pairing key for diva-host*; never the key; each `SetupOldKeyRemove-<page>-<n>` button reads *Remove your OpenRouter key* and first asks `OldKeyRemoveQuestion`, *Remove your OpenRouter key from this PC? You can't undo this.*, where `ConfirmationYes` deletes the key from Windows Credential Manager, so it needs `--allow-ui-effects`; `Invoke-MartletMcp.ps1 -LabCredentials` keeps such keys in the disposable data directory instead), `SetupLocalRecommendation` (the local Ollama model recommended for this PC: the fastest, Gemma 4 E2B, on every graphics card, and the largest that fits this card as the smarter, slower choice, each leaving about 5 GB for a game and Martlet's character), `SetupLocalModelPicks` (the suggestion picked from the list: its size, the card it fits, whether it *hears your voice* or *gets the transcript*, and *fastest, recommended* or *smartest that fits here*; choosing one with `ui_select` only fills `SetupLocalModel`, the model name, and saves nothing, but needs `--allow-ui-effects`), `AdvisorStep`, `AdvisorSummary` and `AdvisorChoice-<n>` (the setup advisor that Home's `OpenSetupAdvisor` opens: which step it shows, its plan's summary and each role's pick and status, such as *Speech-to-text: Parakeet speech recognition (Available)*; `GoalFastest` and the other goals, `AdvisorNext`, `AdvisorBack` and `AdvisorClose` only change what it shows), `SetupOllamaStatus` (whether Ollama is installed or running and which models it has, read over loopback when the Thinking tab opens, and which one Thinking uses), `SetupLocalModelTest` (Thinking › This PC: the last *Test model* result for the model in the box, or that it isn't tested yet; a model that doesn't fit in the free graphics memory says so and names a smaller one), `AppUpdateStatus` (Settings › App updates: the installed version, the check schedule and the last check or download result), `OwnHostUpdateStatus` (Settings › App updates, only on a PC running its own host service: where keeping it on this app's version stands), `AppCurrentVersion` (Settings › App updates: always-visible *Current version: Martlet x.y.z*). On Companion › Voice › Voice engine, `VoiceEngineUse-<engine key>` under This PC asks one confirmation (what it installs, the engine it replaces and its model's licence; installing Docker Desktop still asks for its own terms) and then sets up and switches in a run window, so it needs `--allow-ui-effects`. `SetupTestLocalModel` (Thinking › This PC's *Test model*) starts Ollama if needed, loads the model in the box and sends it one short loopback chat request in a run window, so it needs `--allow-ui-effects` too; read the outcome from `HostRunStatus` and `SetupLocalModelTest`. `SetupUseLocalThinking` (*Use Ollama on this PC*, `--allow-ui-effects`) gets the model in `SetupLocalModel` ready before Thinking switches: for a model Ollama doesn't have it first asks `LocalModelDownloadQuestion` (the tag, its size when Martlet knows it and what Thinking keeps using until then; `ConfirmationYes` downloads, `ConfirmationNo` logs *Status: Thinking didn't change.*), then a run window titled *Switch Thinking to <model>* downloads (when needed) and loads it, ending with `HostRunStatus` *<model> is loaded (n s). Thinking switches to it now.*, and only then does `SetupOllamaStatus` say *Thinking uses <model>*. An open talk window follows any saved job change between replies and logs *The open conversation follows the changed setup between replies: Llm ChatCompletions <model>, ...* (`logs_tail` `contains` `open conversation follows`). A run window (`HostRunWindow`, titled `Martlet - <run>`) returns its status line as `HostRunStatus` (for example *Waiting for Docker Desktop to start...* or why it stopped); its output (`HostRunOutput`, which can show a one-use pairing code) is not returned, so read it with `logs_tail` `host-runs`, which also records each status change. `HostRunHide` (*Hide*, also Esc and the window's close button) only hides a running run, which keeps going in Background tasks, and closes the window once the run has finished; `HostRunHideHint` says so while it runs. `HostRunCancel` (*Cancel task...*) asks first (`CancelTaskQuestion`; `ConfirmationYes` cancels, `ConfirmationNo` keeps it running), so it needs `--allow-ui-effects`. A fresh data directory needs no saved settings first: pairing, setting up this PC's host service and a voice engine's setup all work before Setup. Setting `DOCKER_HOST` (for example to a local test named pipe) before launching the desktop points its Docker checks away from the real engine. `ui_click` invokes a control by automation ID and `ui_select` selects a named combo-box
option. By default only passive navigation and
diagnostics controls can be clicked. `ui_click` with `"focus": true` gives the
control the keyboard focus first, as a mouse click does (its window comes to
the front when Windows lets it): use it to check a page that updates while a
button it just started still has the focus, such as *Detect zones*. `ui_scroll`
reads or scrolls a control that scrolls, through UI Automation's Scroll
pattern: a page, or Companion › Touch's zone map (`TouchZonesMap`) when it is
zoomed in. `horizontal` and `vertical` (0 to 100, each optional) say how far
along to scroll it; without them it only reads. It returns how far along it is
each way (-1 when it can't scroll that way) and which part of its content shows
(`shows`: `left`, `right`, `top` and `bottom` in percent of the content), plus
its `bounds`. Scrolling changes only what shows, so `ui_scroll` needs no
`--allow-ui-effects`. The main window is split into pages, and a
page's controls are only visible after you open it: click `NavHome`,
`NavDevices`, `NavCompanion`, `NavCreations`, `NavTasks`, `NavDiagnostics` or `NavSettings` first (for example
`NavCompanion` before `CompanionTab-Listening`). On Settings, click `DiagnosticsSection` to
expand the pipeline and status fields. On a fresh data directory, the welcome wizard shows
([WELCOME_WIZARD.md](WELCOME_WIZARD.md)): `TourSkip` dismisses it, and `TourBegin`
and `TourBack` step through it. Step 1's `WizardNewNetwork` and
`WizardJoinNetwork` save the device role, so they need `--allow-ui-effects`;
`WizardJoinNetwork` also looks for Martlet on the local network, and
`WizardScanAgain` (passive) looks again: `WizardScanStatus` says what answered
and each `WizardFound-<n>` reads *name (address): Martlet version, with hosts*.
`WizardConnect-<n>` (*Join*, `--allow-ui-effects`) opens *Add a computer*
already asking that computer (`NearbyNumber` shows the check number), and
`WizardJoinManual` (passive) opens *Add a computer* for an address and code.
`TourHost` makes this a host PC and closes the wizard on the host dashboard.
Step 2 reads `WizardSpecRow-Gpu`, `-Vram` (with the memory in use when
nvidia-smi answers), `-Ram`, `-Cpu` and `WizardSpecs` (the whole line, for
example *NVIDIA GeForce RTX 2070 SUPER (Nvidia, 8 GB graphics memory) · 32 GB
memory · 24 processor threads*); `WizardSpecsNext` is passive. Step 3's
`WizardPreferLocal` and `WizardPreferOnline` (passive) choose the preference
and show the placement engine's suggestion: `WizardPlanSummary` (the preference
and whether Thinking goes online), `WizardPlanItem-Thinking`, `-Voice`,
`-Listening` and `-LipSync` (what, where, *Uses 64% graphics memory, 5% memory,
6% processor* and why, or why it's left out; a part that grows while it works
reads *Uses 31-35% graphics memory*: what it usually holds, then the most),
`WizardJoinSuggestion-<n>` after
joining a network, and `WizardPlanTotals`. `WizardAccept` (*Use these
suggestions*) needs `--allow-ui-effects`: when Thinking goes to NVIDIA Build and
none is set up it shows the key step (`WizardKeyIntro`, `WizardKeySteps`,
`WizardKeyStatus`, readable; never the key), otherwise it closes the wizard on
Home and asks `DefaultSetupQuestion`. `WizardKeyOpen` opens the browser and
`WizardKeySave` saves a key (both `--allow-ui-effects`; never type a real key);
`WizardKeySkip` goes on to `DefaultSetupQuestion` without one.
`DefaultSetupQuestion` (readable) lists what *Set it all up for me* sets up on
this PC, lip-sync and the downloads; `ConfirmationYes` installs and downloads,
so it needs `--allow-ui-effects` (as do `HealthFix-thinking-setup-defaults` on
Home and `HealthFix-listening-setup-defaults` / `HealthFix-voice-setup-defaults`
for one job). `ConfirmationNo` changes nothing. Companion's side list items (`CompanionTab-<Page>`,
for example `CompanionTab-People`) and `OpenPeople` (on Listening) are passive
navigation too. People shows `PeopleStatus` (on, off, or that the installation
lacks the voice recognition files), `PeopleSyncStatus` and
`PeopleVoiceCount`, and Listening shows `ListenParakeetStatus` (*Parakeet in
Martlet* with *in use*, *downloading* or *no Docker*) and, for each Parakeet
model, `ListenParakeetModel-<model ID>` (what it is for, with *recommended*
or *in use*: "Fastest in English  ·  recommended") and
`ListenParakeetModelState-<model ID>` (its name, languages, what it is good
at, its memory and *Downloaded.*, *Downloads once: 477 MB.* or, while it
downloads, *Downloading: 46% of 477 MB...*); snapshots return
these status texts, as does the talk window's `LiveStatus` (the line under "Martlet": what it is doing, or why the last reply failed, naming the job that failed: *Martlet couldn't speak. ...* for the voice, and *Your Martlet host <ID> didn't answer ...* when the job runs on a paired host). Each voice's controls are numbered by voice: its name chips
`PeopleNameShow-3-<i>` (make name *i* the one shown; the first is the one
shown) and `PeopleNameRemove-3-<i>`, the box `PeopleAddName-3` with
`PeopleAddNameButton-3` (Add; Enter also adds), `PeopleClip-3-<i>` (plays
clip *i*, newest first), `PeopleOwner-3` (*This is me*),
`PeopleMergeTarget-3` with `PeopleMerge-3` (enabled once a voice is chosen)
and `PeopleForget-3`; names save at once, then sync. `PeopleClips-3` reads
*Hear them (N):* while voice 3 has clips (a passive value). Like
`PeopleRecognize` (ticked by default; a shared setting), `PeopleKeepClips`
(ticked by default; this PC only; unticking deletes every clip),
`PeopleSync`, `PeopleForgetAll` and `SetupListenParakeet-<model ID>` (*Download
and use* asks one confirmation, `ConfirmationYes`, then downloads that model
and switches Listening to it; *Use it* switches to a downloaded model at once),
they
change data or download and need `--allow-ui-effects` (People has no sharing
switch of its own: the list follows `ClusterSync`). While Listening uses a paired
host or OpenAI, Listening's *Now* card has `SetupJobStandIn-Listening` (a passive
value): what hears you on this PC's processor when that route can't (*If OpenAI
can't hear you, Parakeet TDT 110M (English) hears you on this PC's processor
instead. Nothing is sent anywhere.*), or *... Martlet can't either. Download
Parakeet TDT 110M (English) and this PC's processor hears you instead.* with
`SetupListenStandInDownload`, which asks one confirmation (`ConfirmationYes`),
downloads that model and leaves Listening as it is, so it needs
`--allow-ui-effects`. A turn the stand-in heard shows in `logs_tail` as
*Transcription failed (outcome Failed, provider ...; Parakeet &lt;model&gt; on
this PC heard it instead in N ms)*, then, for 60 s while the route isn't asked,
*Listening: Parakeet &lt;model&gt; on this PC heard it instead in N ms, without
asking Listening's own route ...*; its *Reply latency* line ends with
*speech-to-text &lt;model&gt; on this PC, standing in for &lt;route model&gt;*,
and Home's `HealthIssue-failed-listening` keeps the route's failure until it
answers again. Each voice's
`PeopleMemories-3` (*Memories*: what Martlet remembers about them) only opens Memory
showing that voice's facts, so it is a passive click; read `MemoryFactStatus`
there (*Showing N.*) or `memory_status` for whose facts are. On Devices, `Node-<id>`
selects a device on the map (`Node-this-pc`, `Node-host:<host ID>`,
`Node-pc:<device ID>` for another Martlet computer that runs no host service,
`Node-cloud:<server>`, `Node-add`, `Node-missing:brain`) and
`CoverageShow-<job>` selects the device doing a job; both only show details, so
they are passive clicks, as are the `DeviceFactsSection`, `DeviceRolesSection`
and `DeviceReachSection` expanders. Each `Node-<id>` also returns the device's
card as text: its name, subtitle, status and what it runs (for example
`IMOUTO, desktop-imouto · imouto-host. Connected. Runs: Martlet companion, Martlet host, Listening`
or `DIVA, desktop-diva · diva-host. Connected. Runs: Martlet host PC, Speaking, Lip-sync`),
so one snapshot shows the whole map. The map fits up to six devices on each
side of This PC; the rest fold into a `Node-more:computers` (or
`Node-more:services`) card, *44 more computers* with how many need attention,
whose click opens the list.
A device that Martlet changes now (a recommended setup applied from any of
your computers, a host role this PC changes, a host PC running a role command
from another computer, this PC following a plan change) shows the status
*Configuring: <step>* on its card and in `SelectedDeviceHealth`, for example
*Configuring: Installing Chatterbox Turbo (2 of 4)*
([Configuring](CLUSTER.md#configuring)).
`DevicesViewMap` and `DevicesViewList` switch
between the map and the list (passive); the list shows by itself once the map
can't fit every device. The list shows every device as a `Node-<id>` card (This
PC, then those needing attention, then by name) with `DeviceFilter-all`,
`-attention`, `-hosts`, `-computers` and `-cloud` pills (passive; each returns
its count) and a `DeviceSearch` box (`ui_set_text`, `--allow-ui-effects`).
`DevicesSummary` returns *53 devices, 2 need attention. Select one to see
details.* and `DeviceListStatus` *Showing 12 of 53 devices.* (or *No device
matches "gpu".*). Martlet remembers up to 64 paired hosts.

The selected computer's **Resources** section (`DeviceResources`, for This PC
and paired hosts) shows how much of the device each job takes, from the
placement engine's measure of today's setup (`PlacementEngine.Measure` over the
footprint catalog). `DeviceSpecs` returns its hardware (*NVIDIA GeForce RTX
5090 (32 GB) · 64 GB memory · 32 processor threads*), one
`DeviceResource-<vram|ram|cpu|disk>` per resource it reported (*Graphics
memory: 14 of 32 GB planned (44%), 15 GB free for Martlet.*; This PC's memory
adds *In use now: 9.5 GB (59%).*, read live; hosts report no live use yet). A
resource whose jobs grow while they work shows a range: what they usually hold,
then the most they take (*Graphics memory: 11-14 of 32 GB planned (34-44%), ...*).
When the usual amount fits but the most does not, the bar is tight (*..., tight:
at their busiest the jobs can need 2 GB more than it can give, and slow down or
fail.*); when even the usual amount does not fit, it is over (*..., 1-3 GB more
than it can give.*). One
`DeviceShare-<option>` per job (*Deep thinking (Gemma 4 12B): 16-25% graphics
memory, 3% memory, 6% processor.*; one number when the job does not grow),
`DeviceHeadroom` (*Left free: ...*, then *Tight on graphics memory: ...* or
*Planned to use more ... than it has.*) and
`DeviceAlsoFits-<n>` from `PlacementEngine.Afford` (*Room for another Deep
thinking model (Gemma 4 12B) here.*). The **What your computers can run** card
(`CapacityCard`) returns `CapacityCoverage` (*On your computers: Thinking
(gpu-box). Online: Voice (OpenAI voice). Not set up: Singing, Pictures.*),
`CapacityTotals` (*Totals across 2 computers: 32 GB graphics memory, 80 GB
memory, ...*) and `CapacityFits` (*Your computers could also run 2 more Thinking
models (Gemma 4 E4B) and another Deep thinking model (Gemma 4 12B).*). All are
read-only.

`SelectedDevice` and `SelectedDeviceHealth`
return the selected device's name and status. When a paired host is older
than this PC, its status *Update available* is a button,
`SelectedDeviceHealthAction` (returned: its status and what it does, for
example *Update available: Update to Martlet 0.40.0*); clicking it runs the
same update as `NodeAction-UpdateHost`, so it needs `--allow-ui-effects`. For a
paired host Martlet manages (this PC's host service, one over SSH, or one whose
own Martlet runs its commands), `SelectedDeviceOutside`
(in *Details*) returns its outside access in counts and choices only (*2 outside
addresses; pairing codes from outside home refused; every connection treated as
outside home.*, with *Outside access paused: sign-in is off; sign-in needed first
(signin.not_set_up)* while it is), and `NodeAction-OutsideAccess` opens the *Outside access* dialog
(`HostInput-addresses`, `HostInput-allowCodes`, `HostInput-treatAll`, on by
default for Docker hosts; while sign-in isn't usable on the host its note
`OutsideAccessBlockedReason` returns why, starting *Outside access paused: sign-in
is off.* when the host has outside addresses, ending with the reason code, the
outsideAccessBlockedReason `signin.not_set_up` or `signin.no_allowed_identity`,
and `OutsideAccessSetUpSignIn` opens the host's sign-in settings (a passive click);
`HostInputCancel` closes it, `HostInputOk` runs
`martlet-host exposure` and restarts the host's gateway, so it needs
`--allow-ui-effects`). For a
paired host, `SelectedDeviceRelease` (in *Details*) returns its Martlet release
as this PC knows it, kept current by the release every host announces on each
network sync (`0.22.0, up to date`, `Needs update from 0.21.0 to 0.22.0`), and
`SelectedDeviceUpdate` the note on what this PC last did to update it (for
example *Asked Martlet on gpu-pc to update to 0.22.0 ...*, *Waiting to update
to Martlet 0.22.0: that host is busy (...)* or *Another update of that host was
already running (...)*, then *Updated to Martlet 0.22.0 (seen at 9:41 PM).* once
the host announces it, a check finds it current or another route of this
Martlet updated it). `SelectedDeviceSharedGpu` (shown only then) warns that the
computer runs on Windows and its voice engine shares the graphics card with its
other roles (the same wording as `SpeakingEngineSharedGpu`). Each row title
`DeviceComponent-<part>` (`job-Llm`, `job-Stt`, `job-Tts`, `lipsync`,
`character`, `audio`, `host-service`, `host`, `users`, `member`, `role-<role>`)
returns the job's name, and its detail line `DeviceComponentDetail-<part>`
returns the row's text. Another Martlet computer's `member` row (*Martlet
companion*, *Martlet host PC*, or *Martlet app* for one on an older Martlet)
gives its device ID, what it is, where it stands with your network and where it
was last active, for example `desktop-imouto. Conversations, its microphone and
speakers; it runs a host service too (imouto-host). In your Martlet network.
Active now on diva-host.`. A paired host's `users` row (*Computers using it*) lists
the computers paired with it as the host reports them, for example
`DeviceComponentDetail-users`: `IMOUTO (desktop-imouto), active now; This PC,
active now.`; on this PC's own host service, `DeviceComponentDetail-host-service`
reads `Paired as diva-host. Used by IMOUTO (desktop-imouto), active now.` (or *No
other computer uses it yet.*). Job owners are `ThinkingOwner`, `ListeningOwner`, `SpeakingOwner`
and `LipSyncOwner`, device commands `NodeAction-<action>`
(`NodeAction-InstallRole-<role>`, `NodeAction-ChangeRole-<role>` (*Change ... settings*, on the row of a role the host
runs: its dialog shows what the role runs with now, so it needs `--allow-ui-effects`) and `NodeAction-RemoveRole-<role>` for host
roles; on another of your computers that said what it is, `NodeAction-MakeHostPc` (*Make it a host PC*) or
`NodeAction-MakeCompanionPc` (*Make it a companion PC*, or *Keep it a companion PC* while an ask to become a host PC waits),
which switch that computer, so they need `--allow-ui-effects` and then `ConfirmationYes`; while the ask waits,
its `DeviceComponentDetail-member` ends with *Asked by this PC at ... to become a host PC: it switches the next time
Martlet there syncs its settings ...*; once it has switched, the desktop log and status line say *IMOUTO is a host PC now,
as you asked.*, and a host PC with no host service yet reads *Its host service isn't set up yet, so it does no work for your
other computers until someone at it chooses Set up host service on its Home ...* there instead of the host service it runs;
Settings › What this PC is for lists the same computers of your network: `OtherRolesStatus` (what the list offers, or why it
is empty or can't switch them), `OtherRole-<device ID>` (*IMOUTO (desktop-imouto). Companion PC that also runs a host service
(imouto-host). Active now on diva-host.*, with the waiting ask) and `OtherRoleSwitch-<device ID>`, the same button, which
needs `--allow-ui-effects`), and Settings for all devices holds `CheckHosts`, `ClusterSync` (checked by
default; unticking it needs `--allow-ui-effects` and saves `off`),
`ClusterStatus` (returned as text), `SettingsSyncStatus` (text: how many
settings are shared, on how many hosts they are the same, when checked and
what was last taken from another computer), `MemorySyncStatus` (text: how many
facts Martlet remembers, on how many hosts they are the same, when checked, how
many were taken from or forgotten because of other computers, or why it waits:
no host paired, the switch or memory off, the store in use; never a fact),
`SettingsSyncWaiting` (text, shown
only when this PC can't follow a setting yet: which, and why),
`SettingsSyncClaim` (*Use this PC's settings on all my computers*; it changes
every computer's settings, so it needs `--allow-ui-effects` and then
`ConfirmationYes`) and `RoleSetup-<role>` for jobs nobody does.
`Node-add` (*Add a computer*) shows only the ways to add one, each a whole
clickable card named for what it does, with its line as help text:
`NodeAction-AddComputer` (*Add a computer*, the highlighted first card),
`NodeAction-PrepareComputer` (*Prepare a Linux computer*) and, on a companion,
`NodeAction-HostThisPc` (*Run host services on this PC*; it sets up this PC's
host service, so it needs `--allow-ui-effects`). The + beside the title,
`SelectedDeviceAdd`, opens the same *Add a computer* wizard as
`NodeAction-AddComputer` and the page's `AddComputer`, so all three are
passive clicks.
In *Prepare this computer* (`PrepareHostWindow`), each GPU whose power limit
can change has a `PreparePower-<index>` slider (watts), its chosen limit
`PreparePowerValue-<index>` (*300 W*) and its limits
`PreparePowerDetail-<index>` (*Now 370 W, default 370 W, allowed 100-450 W.*);
all three are in `SafeValues`. The slider stretches to the checklist's width,
so its handle stays in view at any window width.
The **Your Martlet network** card ([NETWORK](NETWORK.md)) holds `NetworkStatus`
(status text: member with how many computers and hosts and how many are reached
from outside home right now, waiting to join with
the check number, a host PC in no network that only watches, or in no network),
`NetworkCheck` (syncs now; it contacts the paired hosts, so it is not a passive
click), each computer's row title `NetworkMember-<desktop|host>-<ID>` (status
text, for example `lab-gpu. Host, not paired with this PC yet; added on
desktop-diva. 2 outside addresses. Reached from outside home (outside address 1).`;
or *Not reachable at home or outside right now*; for a host with outside
addresses also the guard's totals it reported, *Guard: 3 failed and 1 throttled
request(s) since it started, 0 address(es) locked out now.*),
and for another computer where it was last active, *Active now
on diva-host.*) with `NetworkRemove-<desktop|host>-<ID>` and, for hosts,
`NetworkOutside-<ID>` (opens the *Outside addresses* dialog, field
`HostInput-addresses`, saved by `HostInputOk`; it signs the roster, so it needs
`--allow-ui-effects`), each computer that uses
one of this PC's hosts without being a member `NetworkPaired-<device ID>` (status
text: which hosts it uses and when it was last active), and each request to join
`NetworkJoin-<device ID>` (status text with the check number) with
`NetworkAllow-<device ID>` and `NetworkDeny-<device ID>`. Remove, Allow and
Turn down change the network and need `--allow-ui-effects` (then
`ConfirmationYes`); Allow also hands that computer access to every host, so
keep it to disposable lab networks. The card works on a host PC too: one that
is in a network keeps syncing (so it can let others in), and one in no network
only watches and makes no key or `network.json` (`network_status` then reads
`state: none`, `key: false`); each change in what the PC sees is logged as a
`Martlet network: ...` line (`logs_tail` with `contains: "network"`).

The **Apps and API keys** card ([API](API.md)) holds `ApiKeysStatus` (status
text: how many keys, and on how many hosts they are or why not), each key's
row title `ApiKeyRow-<key ID>` (status text: name, what it may do, which
computer made it, last use, expiry and the ID's first characters) with
`ApiKeyRevoke-<key ID>`, and `ApiKeyCreate`. Create opens
`ApiKeyCreateDialog` with `ApiKeyName`, `ApiKeyScope-<read|voice|perception|manage>`
(read is ticked), `ApiKeyExpiry` (*Never*, *In 30 days*, *In 90 days*, *In a
year*), `ApiKeyCreateConfirm` and `ApiKeyCreateCancel` (passive). Creating
shows `ApiKeyCreatedDialog`: `ApiKeyCreatedTitle`, `ApiKeyHosts` (the hosts'
addresses and public key pins) and `ApiKeyExample` (a curl request naming
`$MARTLET_API_KEY`) are returned as text; the key itself (`ApiKeyValue`) is
never returned, and `ApiKeyCopy` writes the clipboard; `ApiKeyCreatedDone`
(passive) closes it. Create, the dialog's fields, Copy and Revoke (then
`ConfirmationYes`) change data and need `--allow-ui-effects`.

A paired host's `DeviceReachSection` holds `HostReachNow` (*Reached via: ...*,
status text), `HostReachMethod` (a combo box: *Through Martlet on that computer
(paired connection)*, *SSH, with Docker there*, *SSH, native Linux*, *This
PC, with Docker Desktop*; `ui_select` needs `--allow-ui-effects` and saves at
once), `HostReachHint` (status text) and `HostReachSsh` (saved when it loses
focus, on Enter or 1.5 seconds after typing stops; there is no Save button).
Host
actions (`NodeAction-UpdateHost`, `NodeAction-HostStatus`, roles) on a host
reached through Martlet there open a run window (`HostRunStatus`) that sends
the command through its gateway; on a disposable data directory without a
stored pairing secret it stops at *This PC's pairing secret is missing*.
Settings › *Your other computers* has `AllowNodeCommands` (checked by default;
`ui_toggle` needs `--allow-ui-effects` and saves `node-commands.txt`) and
`NodeAgentStatus` (status text: off, no host service on this PC, ready, the
last command it ran, or where bringing its own host service to this PC's
version stands). The same card
has `NearbyShare` (*Let my other computers find this PC and ask to use its
hosts*, checked by default; unticking it needs `--allow-ui-effects` and saves
`off` in `nearby.txt`), `NearbyShareStatus` (returned: off, nothing to share,
the port in use, *Checking Windows Firewall...*, Windows Firewall or a Public
network keeping other computers out (then only Martlet on this PC can find
it), or on with the hosts it offers; then *Last request:* allowed, denied,
withdrawn or stopped) and `NearbyFirewall` (*Let my other computers reach this
PC*, shown only when blocked: an administrator prompt, never part of
verification).
Home and host-dashboard steps have their buttons as `Step-<step>-<n>` (returned:
the button's label and step, for example *Add Thinking: Add roles*; the host
dashboard's *Add roles* step offers every role in `HostRoles` that this PC's host
service doesn't run yet, *Add Deep thinking* included, then *Remove* for each it
runs), whether
each is ticked as `StepState-<step>` (returned: *Host service: done*, *Pair your
main PC: to do* or *Add roles: optional, not done*) and their detail line as
`StepDetail-<step>` (status text). A step with more than two
buttons (or long labels, like `Step-roles-<n>`) wraps them on rows under its
detail, so with `layout` the buttons' `bounds` start at the detail's left edge
and stay inside the window. The host dashboard reads this PC's own host service
by itself (the same read as `host_service_status`): when it opens, every 30
seconds while the window shows and when it shows again, so steps tick without a
button. `HostServiceStatus` (returned) is the status under its icon (*Checking...*,
*Needs Docker Desktop*, *Needs Windows restart*, *Windows isn't ready for Docker
Desktop*, *Waiting for Docker Desktop*, *Not set up yet*, *Host
service stopped*, *Address changed*, *Not answering yet* or *Host is running*),
`HostStepsHeading` (returned) reads *This host is ready* once the required steps
(Docker Desktop, host service, pairing) are done and `HostStepsSummary`
(returned) says how many steps are left and the next one, or *All set*, and when
it last checked. `CheckHostService` (*Check again*) only repeats that read and
says what it found in the status line, so it is a passive click. On the host
dashboard,
`StepDetail-docker` says whether Docker Desktop's **engine** answers, or why
Windows can't start it (firmware/nested virtualization, Windows features, WSL
status, missing/disabled host services or a pending restart), with the same
recovery guidance as `virtualization_status`. This is checked even while Docker
Desktop's window is open, and refreshed by `CheckHostService`. `Step-docker-0`
then reads *Restart Windows*, *Turn on virtualization*, *Turn on Windows features*
or *Review Windows setup*. Setup and *Start Docker Desktop* use the same guarded
run-window recovery. These actions can ask for administrator approval or a
restart, so they need `--allow-ui-effects`; never execute an actual Windows
change or restart during verification. `StepDetail-service`
says the host service is not set up (`Step-service-0` *Set up host service*),
set up but stopped (*Start host service*), set up for an address this PC no
longer has or not answering (*Set up again*), or *Running and reachable on your
network as <host ID>* (done, no button). `StepDetail-pair` names the computers
in the host's Martlet network and, when this PC is paired with its own host
service, every other computer that service reports as paired, member or not,
with when each was last active (*Paired with IMOUTO (active now) and this PC.*,
done, with `Step-pair-0` *Pair another computer*). `StepDetail-roles` lists the installed
roles (*Runs Lip-sync and Listening.*, done) with an *Add* button for each other
role and a *Remove* button for each installed one. `StepDetail-update` reads
*Up to date: the host service runs Martlet x.y.z* (done, no button) or offers
*Update host service*. After a restart for
virtualization, Martlet opens a run window by itself (`HostRunWindow`) that
continues the setup; `continueSetup` in `virtualization_status` shows what is
pending.
When this PC becomes a host PC (Settings' *Use this PC as a host*, the welcome
wizard, or a request from another computer) or Martlet starts on one, it brings
the host roles up by itself: when the host service on this PC runs roles (read
now, or remembered in `this-pc-host-roles.txt` from the last read, or, before
any read, because this PC is paired with its own host service), Docker Desktop
is installed and Windows is ready for it, a hidden run *Start this host's roles*
(in Background tasks and `HostRunsNow`) starts Docker Desktop when needed, waits
for the network holder to restart the gateway and roles, and runs
`martlet-host warm`, which loads each role's model (`warm=` in its
`role.conf`). `HostAutoStart` (returned) says what it decided or did (*Starting
Docker Desktop, then this host's roles (ollama, stt), and loading their
models.*, *This host's roles are running and their models are loaded. Martlet
started them by itself at 7:02 PM.*) or why it didn't (*Docker Desktop isn't
installed, so nothing started by itself...*, *Windows isn't ready for Docker
Desktop...*, *This PC's host service runs no roles yet...*); it never installs
anything.
Setup runs go **side by side**; nothing refuses a step because another one is
working. What two runs share is done once: installing Docker Desktop, getting
Windows ready for it, starting it, building the host image, checking Windows
Firewall and setting up and pairing this PC's own host service. Changes that
aren't a setup or update (adding or removing a role, status, pairing, warming)
don't wait for this version's host image: while it isn't built yet (for
example while *Keep this PC's host service current* builds it right after
Martlet updated itself), they run with the engine of the version the host
service runs now, when its image is on this PC and knows the role, and their
output says so. Opening Windows Firewall during *Set up this PC as a host* runs
while Docker Desktop starts and the image builds, and *Update hosts now* checks
and updates every paired host side by side. The second run's
`HostRunStatus` reads *Waiting: "<other run>" is starting Docker Desktop. This
continues once that's done...* and carries on afterwards; when the run doing it
fails, the waiting one stops with the same reason, and when it is canceled, the
waiting one does the step itself (the host-runs log says *"<run>" was canceled
before it finished ..., so this run carries on by itself*). Changes to the host
service itself run side by side in its engine, where only colliding ones wait
([Changes side by side](../deploy/host/README.md#changes-side-by-side)). Pressing a step whose
run is still working brings that run's window forward instead of starting it
twice. While runs work, `HostRunsNow` (returned) lists them with their status
lines (*2 runs working side by side: Start Docker Desktop: Waiting for Docker
Desktop to start... · Check this PC's host: Waiting: ...*), `StepDetail-docker`
says which run is installing or starting Docker Desktop (*"Start Docker Desktop"
is starting Docker Desktop. You don't have to wait: ...*), and `Step-docker-0`
says what that run does (*Installing Docker Desktop...*, *Starting Docker
Desktop...* or *Getting Windows ready...*) and is disabled (`enabled: false`)
until it ends. This includes the hidden *Start this host's roles* run of a PC
that just became a host PC. When no run works on Docker Desktop any more, the
dashboard reads the host service again at once (the desktop log says *No run
works on Docker Desktop any more; reading this PC's host service again now.*),
so the step ticks or offers its button again. While a host
service Martlet hasn't seen set up waits for Docker Desktop, `StepDetail-service`
offers `Step-service-0` *Set up host service* already (its run waits for Docker
Desktop and continues). The dashboard keeps reading the host service every 30
seconds while runs work. Several run windows have the same controls, so name the
one to click: `ui_click` `{"id":"HostRunCancel","window":"Martlet - Start Docker
Desktop"}`. To exercise it without the real engine, launch the desktop with
`DOCKER_HOST` pointing at a missing pipe and Docker Desktop already running:
*Start Docker Desktop* (`Step-docker-0`) then waits for an engine that never
answers, and `HostStatusConsole` (*Show host status*) waits for it. To exercise
a start without touching Docker Desktop or Windows at all, also set
`MARTLET_SIMULATE_DOCKER_START` to a number of seconds (1-600) before launching
the desktop (FIXTURE, `SimulatedDockerStart.cs`): every Docker Desktop start
then waits that long, shared by the runs that need it as a real start is, and
stops as a start that failed; the host-runs log says *FIXTURE
(MARTLET_SIMULATE_DOCKER_START)*. With `this-pc-host-roles.txt` (one role per
line, such as `ollama`) in the data directory, a desktop made a host PC (for
example with `role_lab ask host`) starts Docker Desktop by itself, so
`Step-docker-0` reads *Starting Docker Desktop...* and is disabled until the
fixture ends.
Devices' `AddComputer` (and Settings' `OpenHosts`, and on a new PC Home's
`HomeConnectComputers` or the `HealthOpen-thinking-setup-network` fix) opens the *Add a computer*
wizard (`HostsWindow`, titled *Martlet - add a computer*; the click may return
`completed: false` while that dialog stays open). Pairing needs no saved
settings: on a fresh data directory the wizard opens with `HostStatus` *No
Martlet host paired.* and `PairHost` goes straight to the host. It has two
steps. Its rail steps (`HostsStepConnect`, `HostsStepRoles`), `HostsBack`,
`HostsNext`, `HostsClose`, `HostsEnterCode` (*Enter a pairing code*: shows the
address and code fields, which also open by themselves when nobody answers on
the network) and the `HostAddressSection` and `DeviceIdSection` expanders only
change what the wizard shows, so they are passive clicks. `SetupThisPc` (*Set
up this PC*) and `SetupHost` (*Set up over SSH*, with `SshTarget`; Martlet
picks Docker or native Linux from what the computer has) set up and pair a
new host and need `--allow-ui-effects`; a successful connection of any kind
moves to Roles. Snapshots return `HostStatus` (the wizard's status line: what
pairing did, or why it was refused, such as *That code doesn't match...* or *No
Martlet host answered at ...*), `DockerState` (whether Docker Desktop is
running, installed or missing), `PairCodeHelp`, `PairedHost` (the host the
Roles step acts on; `HostChoice` picks another when several are paired) and
`RolesSummaryText` (how its roles run, or *Connect a computer first...*).
`AddRole-<role>`/`RemoveRole-<role>` act on that host. `PairAddress` and
`PairingCode` take the host's address and short code (`ui_set_text`, so
`--allow-ui-effects`), and `PairHost` pairs; a successful pairing stores a
device secret in Windows Credential Manager, so verification stops at refused
codes. On the host dashboard, *Show a pairing code* (`Step-pair-0`) shows the
address (`HostRunPairAddress`, returned) and the one-use code (`HostRunPairCode`,
never returned) in the run window's `HostRunPairing` panel; the host-runs log
masks codes. The code has no deadline: it works until the other desktop uses it
or the run is canceled, and `HostRunPairNote` (returned) says so. *Copy code*
(`HostRunPairCopy`, its label returned: *Copy code*, then *Copied* or *Couldn't
copy*) puts the code on the clipboard, kept out of Windows clipboard history and
the cloud clipboard and cleared again when the code stops working if it is still
there, so clicking it needs `--allow-ui-effects`. While the host isn't paired, `StepDetail-pair` tells the owner to find this PC from the
main PC (*Martlet on your network*), and when Windows Firewall keeps other
computers out it says so and `Step-pair-1` (*Let my other computers find this
PC*, an administrator prompt) appears. While a computer asks to
join the network this host PC is in, a step `join-<device ID>` (*Let IMOUTO into
your Martlet network*) follows it: `StepDetail-join-<device ID>` gives the check
number, `Step-join-<device ID>-0` is **Allow** and `Step-join-<device ID>-1`
**Turn down** (both change the network, so they need `--allow-ui-effects`).

*Martlet on your network* ([how it works](ARCHITECTURE.md#finding-your-other-computers))
is the first card of the wizard's *Connect* step. Opening the wizard on
that step (so `AddComputer`) and `NearbyFind` (*Find again*) send Martlet's
discovery query to port 9444 on loopback and the local network's broadcast
addresses and list who answers; they pair nothing and change nothing, so they
are passive clicks. `NearbyStatus` (returned) says what was found (*Found 1
computer with a host this PC doesn't use yet.*, *No other Martlet
answered...*), what a request is doing or why it stopped (*DIVA denied the
request (or it expired there).*, *Stopped asking DIVA.*, the sharing
computer's reason, *Paired with ... through ...*). Each found computer is a row
with `NearbyItem-<n>` (returned: *<name> (<address or this PC>): <hosts> ·
Martlet <version>*) and `NearbyConnect-<n>`, which starts a request and needs
`--allow-ui-effects`. While asking, `NearbyNumber` returns the check number and
`NearbyCancel` (*Stop asking*) withdraws the request (passive). On the
computer asked, a separate window `JoinRequestWindow` (*Martlet - <name> wants
to use your hosts*) returns `JoinRequestTitle`, `JoinRequestText` (who, from
which address, which hosts), `JoinRequestNumber` (must equal the asking side's
`NearbyNumber`) and `JoinRequestExpiry`; `JoinAllow` and `JoinDeny` need
`--allow-ui-effects`. Allow opens a run window (`HostRunWindow`, *Martlet - Let
<name> use your hosts*) that asks each host for a one-use code; its
`HostRunStatus` and the host-runs log show progress, never the codes. To
exercise it on one PC, run two desktops on disposable data directories with
`DOCKER_HOST` pointed at a missing pipe (so neither finds this PC's real host
service): give the sharing one a `hosts.json` with an SSH host that refuses
(for example `martlet@127.0.0.1:1`), so it answers on loopback, Allow reaches
the run window and the request fails with that host's reason on both sides
before any real code or credential exists.
Use `ui_snapshot` again to observe asynchronous effects. Modal
actions may return `completed: false` while their dialog remains open; this
means the invoke is still pending, not that the action finished.

Home (companion mode) shows what needs attention, then one Health tile per
part. `StageTitle` and `StageText` return the hero's headline and line,
`HealthTitle` (*Needs attention* or *All good*) and `HealthSummary` (for
example *1 problem stops Martlet replying · 3 good to know.*) the list's
heading, and `HealthAllClear` shows when nothing needs attention. Each item's
title `HealthIssue-<id>` returns its level, title and detail (*Problem: Ollama
isn't running on this PC. ...*); ids include `data-folder`, `settings`,
`thinking-setup`, `thinking-retired`, `ollama`, `job-<job>` (coverage, for
example `job-listening`), `host-service`, `failed-thinking`, `failed-listening`,
`failed-voice` (a reply's text arrived but speaking it failed),
`microphone`, `microphone-blocked`, `speakers`, `listening-setup`,
`voice-setup`, `audio2face`, `webview2`, `vision`, `vision-source`,
`host-<host ID>`, `host-update-<host ID>`, `tools-config`, `tools-<server>`,
`update`, `update-failed`, `update-cleanup`, `errors` and `crash`. Its fixes
are `HealthOpen-<id>-<fix>` when they only open a page or hide the item
(passive clicks, for example `HealthOpen-thinking-setup-open-thinking` or
`HealthOpen-crash-dismiss`) and `HealthFix-<id>-<fix>` when they do something
(start or install software, check a host, install an update), which needs
`--allow-ui-effects`. Tiles `HealthCheck-<part>` (`thinking`, `listening`,
`voice`, `lipsync`, `microphone`, `speakers`, `character`, `devices`,
`hostservice`, `tools`, `updates`, `app`) return *<Part>: OK*, *needs attention* or *not checked or
not set up* with the state, and clicking one only opens its page.
A companion PC paired with a host service on this PC (`DeviceRoleSummary`
reads *Companion PC + host*) reads that host service from this PC's Docker as
the host dashboard does (every 30 seconds while the window shows, not while a
conversation replies or hears you): `HealthCheck-hostservice` shows its stage
(*Not set up yet*, *Waiting for Docker Desktop*, *Host service stopped*,
*Host is running*), and while it isn't ready `HealthIssue-host-service` says
why and which of this PC's jobs stop (they no longer show as `job-<job>`
items), with the host dashboard's next step as `HealthFix-host-service-repair-0`
(Install or Start Docker Desktop, Set up or Start host service; its label is
returned, such as *Start Docker Desktop: This PC's host service isn't working*),
then `HealthFix-host-service-check` and `HealthOpen-host-service-show`. While a
run installs or starts Docker Desktop, `HealthFix-host-service-repair-0` reads
*Starting Docker Desktop...* (or *Installing Docker Desktop...*) and is
disabled, the hero (when this item is the top problem) offers *Check again*
instead, and on Devices the
`CoverageFix-<job>-RepairHostService` buttons are disabled with the same label
until that run ends. On Devices,
`Node-this-pc` and `SelectedDeviceHealth` read *Host service not working* (or
*not answering*), and coverage names it *This PC's host service*, never by its
host ID. To see it, pair a disposable data directory's `hosts.json` with
this PC's LAN address (`method` `ThisPcDocker`) on a PC whose Docker has no
`martlet-host-gateway` container.
`HealthRecheck` re-reads settings, devices and this PC's own loopback services
(Ollama when Thinking uses it) and contacts no other computer, so it is
passive. To see a problem on a disposable data directory, put invalid JSON in
`settings.json` (*settings*), a stale `logs\desktop.<pid>.running` marker
(*crash*), or choose Ollama on this PC in Companion › Thinking while Ollama
isn't running (*ollama*). After an unclean exit the desktop looks up that
run's process ID in Windows' Application event log in the background:
`HealthIssue-crash` then reads *Windows recorded <exception> (0x<code>) in
<module>* when Windows has a crash record, and `logs_tail` (`contains`:
`Windows recorded`) returns the full line, or the *no crash* line for a kill or
power loss (a made-up marker PID gives the latter, about 20 seconds after
launch).

The Background tasks page (`NavTasks`) lists every run window's run since
Martlet started, newest first: setting up, updating or pairing a computer,
reconfiguring your computers with the recommended setup (*Reconfigure your
computers*), a download (*Download Martlet x.y.z*, *Download Parakeet ...*,
*Download cloudflared*, a model), Docker Desktop and the like. What Martlet
starts by itself runs out of sight from the start and is listed the same way:
the automatic update download, host updates (*Update gpu-box to Martlet
x.y.z*; a try that finds the host busy changes nothing and leaves the list),
*Keep this PC's host service current*, and the changes your other computers ask
this PC's host service to make (*Install chatterbox (from desk-main)*, *Update
this PC's host service to Martlet x.y.z (from desk-main)*; their output still
goes back to that computer, and Cancel task tells it the owner canceled it
here). Hiding a run window (`HostRunHide`, Esc
or its close button) only hides it while it runs; a question the run asks (a
password, a role's choices, a confirmation) shows its window again with it, and
a run outlives the window that started it (the hosts wizard). `NavTasksCount`
is how many run now (shown only while some run). `TasksSummary` reads *1 running
now.*, *Nothing is running now. 1 finished since Martlet started; ...* or
*Nothing has run in the background since Martlet started.* (then `TasksEmpty`
shows). Each task `<id>` (1, 2, ... in this run of Martlet) has `TaskTitle-<id>`
(its run window's title), `TaskState-<id>` (*Running for 2 min. <status>*,
*Done at 3:47 PM after 25 s. <summary>*, *Canceled at ...*, *Stopped at ...
<why>* or *Paused at ... for a Windows restart*), `TaskShow-<id>` (*Show:
<title>* shows the run window again; *Show output: <title>* once it has finished,
which opens a window with the output it kept, pairing codes hidden) and, while
it runs, `TaskCancel-<id>` (*Cancel: <title>*), which asks the same
`CancelTaskQuestion` and needs `--allow-ui-effects`. A run that finishes while
hidden closes its window. `TasksClear` (*Clear finished*) drops the finished
tasks and their kept output from the list; `logs_tail` `host-runs` keeps every
run's output. `NavTasks`, `HostRunHide`, `TaskShow-<id>` and `TasksClear` are
safe clicks. The desktop log records *Run window hidden; the run keeps going in
Background tasks: <run>* and *Background task canceled by the owner: <run>*.
Setting `MARTLET_SIMULATE_BACKGROUND_TASK` to a number of seconds (1-3600)
before launching the desktop starts *Simulated background task* in a run window
once the window shows: it writes a line each second and changes nothing, to
check Hide, Background tasks, Cancel task and the exit question.
The Diagnostics page (`NavDiagnostics`) lists log lines newest first. Each
shown line is a list item `LogEntry-<n>` (`LogEntry-0` is the newest shown)
whose value reads *<time> <level> <computer> · <part>: <first line>*, where
*<computer>* is *This PC* for this PC's desktop app and for its own host
service's gateway (its host ID, paired here or read from Docker on a host PC);
clicking one only selects it, and `LogDetail` then returns the whole line
(time, level, computer with its ID, such as *This PC (diva-host) · Host
gateway*, part, who passed it on and every following line).
`LogSummary` says how many lines are shown of how many, from how many
computers (this PC's app and host service count once), the last 24 hours'
errors and warnings. Lines from other computers come from this PC's copy of
everyone's lines (`logs\network-logs.json`, which `logs_timeline` also reads).
The filters are
pills that only filter: `LogLevel-all`, `LogLevel-warnings`, `LogLevel-errors`,
`LogPart-<part>` (`all`, `desktop`, `avatar-renderer`, `host-runs`, `gateway`)
and `LogSource-<computer>` (`all`, this PC's device ID such as
`LogSource-desktop-diva`, which also covers this PC's host service, or another
computer's ID); each returns its label as its value (*From: All computers*,
*From: This PC (desktop-diva, diva-host)*, *From: gpu-pc*). All are passive clicks, and snapshots
report which is chosen in `selected`. `LogSearch` needs `ui_set_text` (and so
`--allow-ui-effects`). `LogsRefresh` reads the logs again and sends nothing, so
it is passive (it reads each paired host's new lines); `LogsCopy` (clipboard),
`LogsOpenFolder` (Explorer) and `LogsSave` (*Save logs to share...*: a save
dialog, then the ZIP written and shown in Explorer; `logs_export` writes the
same ZIP headlessly) are not. There is no log host to choose: `LogShareStatus`
says how sharing with every paired host went (*Sharing logs with 2 of 3 hosts.
Waiting for gpu-box. Update old-box to share its logs. Checked at 1:40 AM.*),
or that no host is paired yet (*Checking your hosts...* until the first run).
To see lines on a
disposable data directory, write `logs\desktop.log` (lines like
`2026-10-01 22:15:44.974 -07:00 WARN [1] message`), `logs\avatar-renderer.log`
or `logs\host-runs.log` before launching (other computers' lines come from
`logs\network-logs.json`: `{"schema_version": 1, "entries": [{"source",
"component", "seq", "at", "level", "message"}]}`). Home's `HealthOpen-errors-diagnostics`
and `HealthOpen-crash-diagnostics` open this page.

`ui_snapshot` reports `selected` (true or false) for controls that are chosen
rather than ticked (navigation, Companion's side list, radio buttons and
filter pills, list items), a combo box in the status fields reads as its
chosen option, and a check box in the status fields reads as its label (its
`checkedState` says whether it is ticked).

The desktop character has its own group in Companion's side list, *How it
looks*: `CompanionTab-Character` (the character model, showing and hiding it,
its position, zoom and your characters), `CompanionTab-SpeechBubbles`,
`CompanionTab-Emotes` (emotes, motions and combos), `CompanionTab-Eyes` (where
the character looks and where its eyes are) and `CompanionTab-Touch` (touch
zones and touch temperament). When Emotes and motions or Touch opens (or is
drawn again while scrolled to its top), only its first six rows show at once;
the other rows join a batch at a time once the page has drawn, within about
half a second, so poll `ui_snapshot` with `until` for a later row's ID (such as
`CharacterActionOn-40` or `TouchTemperamentStatus`). Every row is built at
once, so a save always includes every row. Drawn again further down the page,
every row shows at once and the page keeps its place. The desktop log records
how long each Companion page took to show when it opens, such as *Companion ›
Touch drew in 62 ms (21 ms to build, 41 ms to lay out), 919 elements.*, and,
for a page whose rows join in batches, *Companion › Touch showed all 43 rows
in 8 batches, 290 ms after it opened.*

For the desktop character, open `CompanionTab-Character`; with
`--allow-ui-effects`, `SetupCharacterToggle` shows or hides it and
`SetupCharacterZoomIn`, `SetupCharacterZoomOut` and `SetupCharacterResetZoom`
zoom its overlay. `SetupCharacterNow` returns the page's Now line (the model,
then *on your desktop* or *hidden*), and `SetupCharacterNowProblem` appears when
the character's last stop did not finish cleanly (pressing Show or Hide
character retries; details go to the `desktop` log). Exiting never waits on the
character: Settings' `ExitMartlet` (needs `--allow-ui-effects`) closes Martlet
even then, and Windows ends the renderer with it (see **Exiting** below). After a zoom, the `SetupCharacterView` status reports the
character frame's size (with, in parentheses, the overlay's full width: the
frame plus the transparent room on each side the model can move into), its
distance from the top of the screen, the camera zoom and where the
top of the character's head sits relative to the frame's top edge (it must
stay in view at every zoom). While the character shows, `SetupCharacterModel`
describes what its model drives, for example *Model: 236 controls; 1 texture
shown at 1/2 size to fit the graphics budget; blinks with ParamEyeLOpen,
ParamEyeROpen; mouth moves ParamMouthOpenY; no idle motions; 0 expressions;
physics on.* (parameter IDs only, never paths); the desktop log records the same
as *Character model loaded: ...*.

In the character window (`OpenAvatar`, see above), `AvatarModelInfo` gives the
same *Model: ...* description while the character shows, or *Model not loaded:*
and why the chosen model couldn't be shared or shown (for example *The model
refers to x.png, which isn't in its folder.* or, when the renderer rejects it,
*This model can't be shown: ...*, which `logs_tail` `avatar-renderer` also
records).

The character overlay itself is drawn by Martlet's renderer child process
(`Martlet.Avatar.RendererHost`); `ui_snapshot` includes its windows (the
overlay is titled *Martlet character overlay*; another Martlet's renderer is
never included). With `layout`, its window bounds are twice the character
frame's width, centered on the frame: the extra half-frame on each side is
transparent room for the model's motion and may run past the screen's edge.
Its drag surface `MoveAvatar` supports UI Automation
expand/collapse, so `ui_click` on it opens (or closes again) the character's
right-click menu with no flag; opened this way, the menu stays open until a
choice or another `MoveAvatar` click. While it is open, snapshots list
`CharacterMenu` and its items: `CharacterTalk` (*Talk to Martlet*, like
`TrayTalk`), `CharacterMuteVoice` (*Mute voice*, or *Unmute voice* while
Martlet's voice is muted; see below), `CharacterOpenMartlet` (*Open Martlet*, shows the window like
`TrayOpen`, also from the notification area) and `CharacterSettings`
(*Character settings*, opens Companion › Character), which are passive clicks
(except `CharacterMuteVoice`), and `CharacterEyes` (*Eyes*), whose click only
opens its submenu: `CharacterEyes-personality` (*As the personality decides*),
`CharacterEyes-mouse`, `CharacterEyes-near`, `CharacterEyes-ahead`,
`CharacterEyes-window` and `CharacterEyes-free` (*Let the character change
it*). Their `checkedState` shows what Martlet last told the overlay applies;
choosing one goes through Martlet (*The character's menu chose
'look-ahead'.*, `look-free-off`), saves `talk-preferences.json` like Companion ›
Character › Where the character looks (`character_gaze` `usual`), so they need
`--allow-ui-effects`;
then `CharacterZoomIn`, `CharacterZoomOut`, `CharacterResetZoom` (disabled at
the default zoom), `CharacterResetPosition`, `CharacterLockPosition`,
`CharacterClickThrough` (*Let clicks pass through*; see below), the checkable `CharacterOnTop`
(*Keep on top*, on by default; its `checkedState` is the current choice for
this showing) and `CharacterHide` (*Hide character*; Esc on the overlay does
the same), which need `--allow-ui-effects`. Talk, Mute, Open, Settings and Hide are
carried out by Martlet itself, so the desktop log records *The character's menu
chose 'hide'.* (and so on), and a hide is followed by *Avatar renderer stopped
by Martlet.* and `SetupCharacterNow` reading *hidden*.

**Renderer health** shows in the logs (`logs_tail`). Martlet and the renderer
send only whole messages, so a slow reply or a timeout never puts later
commands out of step; a reply that comes after its caller stopped waiting is
dropped. When the renderer's messages break (an unreadable message, a reply
when nothing was asked, a failed write) or it answers nothing for 90 seconds,
Martlet ends it and the character counts as stopped. Then `desktop` records
*Martlet ended the character renderer because it stopped answering properly:
...* and *Avatar renderer ended by Martlet (code 0x00000001) ...*. When the
renderer closes by itself, `avatar-renderer` records why: *The character
overlay closed: its window was closed (not by Martlet).* or *The character
renderer stopped after an error; it tells Martlet and closes.* with the error.
Then `desktop` records the warning *Avatar renderer closed itself ...* (with
the error code when it sent one). Only another exit code (a crash or a kill)
is an error: *Avatar renderer exited unexpectedly with code ...*.

**Muting Martlet's voice**: the overlay menu's `CharacterMuteVoice` (in
`SafeValues`: its name, *Mute voice* or *Unmute voice*, carries the state)
asks Martlet to mute (*The character's menu chose 'mute'.*) or unmute it. It is
the same choice as Companion › Voice's `SpeakReplies` check box (*Speak
Martlet's replies aloud*), so it needs `--allow-ui-effects`: it saves
`talk-preferences.json` (`character_status`'s `voice.muted`), is shared with the
paired computers like the check box, and the desktop log records *Martlet's
voice is muted: replies show as text only.* (or *... unmuted ...*; a choice
from the menu also logs the window's *Status: Martlet's voice is muted: ...*
or *Status: Martlet's voice is on again: ...*) while
`avatar-renderer` records *Martlet's voice is muted.* once the overlay's menu
follows; muting or unmuting there, in Companion or from another computer
changes the menu's item too, and a newly shown character starts with it.
Muting silences a reply Martlet is saying at once (its turn's `VoiceMuted`;
`spoken_reply_check` `voiceFailure` `muted` rehearses it), and the rest of its
words, like every reply while muted, show in the talk window and as speech
bubble and subtitle captions, one sentence per reading time
(`spoken_reply_check` `text-only`).

**Voice volume**: Companion › Voice's `VoiceVolume` slider (0 to 100, full by
default) sets how loud Martlet speaks and sings on this PC, applied to
Martlet's own audio (never Windows' volume); a reply or song playing now
follows within one device buffer. Both it (its number) and `VoiceVolumeLevel`
(*80%*) are in `SafeValues`. `ui_set_range` on it needs `--allow-ui-effects`
because it saves `talk-preferences.json` (`VoiceVolume`, 0 to 1, this PC only
and not shared with paired computers); `character_status`'s `voice.volume`
reads the saved level (1 without a file).

**Quick sounds while Martlet thinks**: Companion › Voice's card has the
`VoiceQuickSounds` check box (off by default), `VoiceQuickSoundsDelay` (*After
0.5 s*, *After 0.7 s (recommended)*, *After 1 s* or *After 1.5 s*),
`VoiceQuickSoundsStatus` (*Off.*, *On. Making the quick sounds with ...*, *On:
4 quick sounds in the Windows voice ...*, *On, but OpenAI ... is a paid cloud
voice: press Make quick sounds now ...*, why they couldn't be made, or *On, but
Martlet has no voice to make them with yet ...*) and `VoiceQuickSoundsMake`
(*Make quick sounds now*, shown once they are on and a voice is set up). All
three values are in `SafeValues`; changing the box or the delay saves
`talk-preferences.json` (`QuickSounds`, `QuickSoundDelayMs`; this PC only), and
the button makes the clips with the voice, so they need `--allow-ui-effects`.
`quick_sounds_status` reads the same choice headless, and `quick_sounds_check`
rehearses the rules.

**Chatterbox Original style**: while Speaking uses Chatterbox Original or it is
the chosen engine (`speaking-engine.txt` is `chatterbox-original`), Companion ›
Voice shows its card with four sliders,
`ChatterboxStyle-GeneralExaggeration`, `ChatterboxStyle-GeneralCfgWeight`,
`ChatterboxStyle-ExpressiveExaggeration` and `ChatterboxStyle-ExpressiveCfgWeight`
(exaggeration 0.25-2 and CFG weight 0-1, in steps of 0.05), each value's label
`ChatterboxStyleValue-<name>` ("0.7") and `ChatterboxStyleState` ("Resemble's
suggestions. General: exaggeration 0.5, CFG weight 0.5. Expressive: exaggeration
0.7, CFG weight 0.3." or "Saved on this PC. ..."), all in `SafeValues`.
`ui_set_range` on a slider and clicking `ChatterboxStyleReset` (*Use Resemble's
suggestions*) save `chatterbox-style.json`, so they need `--allow-ui-effects`;
`f5_voices`' `chatterboxStyle` reads what is saved and `voice_engine_check` with
the same `dataDirectory` sends it.

`MoveAvatar` also supports UI Automation's move: with `--allow-ui-effects`,
`ui_move` moves the character by `dx`, `dy` screen pixels like a drag and
returns its bounds before and after, and `ui_snapshot` reports `movable` for
it. **Locking the character's position**: Home's `ToggleCharacterLock`

**Tapping the character**: a left click on the character that doesn't drag
it (it comes up within Windows' drag distance, within 0.7 seconds; also when
its position is locked, while zoomed in with Ctrl and in the camera view) is a
tap. The renderer page hit-tests the point: Live2D reports the model3.json
HitAreas there and the visible drawables under it (topmost first, at most 8)
and whether the topmost is hair (it sits in a part the model names as hair,
by its ID or DisplayInfo name, such as `PartHairSide` or 前髪),
VRM the humanoid bone of the mesh skinned most to the hit triangle (or its
nearest humanoid ancestor), the actual node, whether that node is hair (a
spring-bone or hair-named joint under the head), the mesh and the material.
The VRM hit test measures the skinned meshes in the pose they have now, so a
sleeve or glove that the idle pose moved away from the T-pose is hit where it
shows.
Martlet then reacts locally, without asking any model: the model's own tap
motion when it has one (a group named like `TapHead`, `Tap@Head`, `TapBody`
or `Tap`), else a head tilt (or nod) for the head, hair and face and a
surprised look (or gasp or nod) elsewhere; Companion › Touch › Touch zones (`character_touch_zones`) replaces that with the reaction of the zone the tap lands in (`TouchZonesLast`). The desktop log records *The
character was tapped on the body (hit areas Body).* and *Character motion
'TapBody' played for a tap on the body.* `character_touch` taps it through
UI Automation (`MoveAvatar`'s value, `"x,y"`) at `x`, `y` (fractions 0 to 1
of the overlay's drawing, +y down; unzoomed the head is near 0.5, 0.15),
which needs `--allow-ui-effects`, waits for the hit test and returns it as
`last`: `n` (the tap's number), `x`, `y`, `hit`, `zone` (`head`, `hair`,
`face`, `body`, `arm`, `hand`, `leg` or `foot`; null on a miss), `hitAreas`,
`drawables`, `bone`, `node`, `hair`, `mesh` and `material` (model-authored
names only, never paths), `held` (how long the press lasted, in ms) and `rest`
(`{x, y}`, fractions like `x` and `y`: where the touched point of the character
was in its rest pose, the pose the touch zones picture shows, traced on the
touched mesh and drawn as the overlay frames it now; null when the renderer
can't trace it). At rest `rest` equals `x`, `y`; while the head follows the mouse
or a motion moves the character, it stays on the spot of the skin that was
touched, and the touch zones compare it with their boxes. `holdMs` presses
that long (`"x,y,ms"` as `MoveAvatar`'s value; 600 or more is a hold), `repeat`
taps the same point up to 20 times `gapMs` apart, and `taps`
(`[{x, y, holdMs}]`, up to 20) taps a sequence of points, each after the hit
test of the one before. With Companion › Touch › Touch zones showing,
`noticed` reads what Martlet noticed after `settleMs`: `waiting`
(`TouchZonesNoticed`), `last` (`TouchZonesNoticedLast`) and `zone`
(`TouchZonesLast`). Without `x`, `y` or `taps` it only reads the last tap, as
does `MoveAvatar`'s `value` in `ui_snapshot`.

**Touches reach the Thinking model** for zones with *Martlet notices* on (on
by default; a `character-touch-zones.json` of version 1 where no zone of a model
had it on loads with it on for all of that model's zones, and saves as version
2): a
reply to what you say or type carries the touch line in its notes (the desktop
log's *Touches: 3 went to Thinking in the notes of your message.*), and touches
on their own start a short reply of their own (*... as a short reply of their
own.*; the talk window's `LiveTurnInputs` reads *Last reply took 2 touches.*).
Its message (Companion › Prompts › *Touched*) asks for a sound or words out
loud, never silence, an emote alone or `[pass]`. The touch line names how the
persona feels about the touched zones (its touch temperament) and, after 5 or
more touches on one place in the last 10 minutes across replies, *They keep
coming back to ...*.
Without a Thinking setup `TouchZonesNoticed` says the touches wait for your next
message.

**Touching Martlet while it talks**: with *When you touch Martlet while it
talks* on *any* (the default) or, for a touch on an intimate part, *intimate*,
a touch on the character while Martlet says a reply, a report or a screen
remark aloud (never its reaction to an earlier touch) stops it at once
(`controller.Stop(..., keepContext: true)`, status `touch.cut_in`, or
`commentary.interrupted` for a remark; the reply's note reads *Stopped for
your touch.*). The touch ledger keeps what Martlet had said aloud
(`ConversationTurn.SaidAloud`, the sentences that started playing, at most
the last 300 characters) and the user's own words it was answering, and the
reaction starts 0.5 seconds after the last touch with no cooldown; its message
adds Companion › Prompts › *Touched, cutting you off*. The desktop log reads
*Touches: Martlet stopped its reply for a touch (poke on groin); its reaction
starts about 0.5 s after the last touch.* Moving or zooming the character never
stops Martlet, and a touch while it still thinks waits for its next reply.

**Stroking the character**: while the character's position is locked, a left
press that drags beyond Windows' drag distance can't move it, so it strokes
the character. The overlay samples the path every 40 ms (at most 400
samples), hit-tests the samples in batches with one `touches` message to the
renderer page, and sends them to Martlet as `stroke` messages on the request
pipe (`move` batches while the stroke goes on, then `end`). Martlet matches
each sample to a touch zone, and to every other zone it lands in where zones
overlap (`CharacterTouchZones.Touched`): each zone the stroke enters as the
matched zone plays its reaction
(unless it is resting), and the first zone's first emote or gesture is held
until the stroke ends. At the end Martlet summarizes the stroke (zones crossed
in order, each overlapping zone a sample was on included, pace `slow`, `steady` or `quick`, passes back and forth, seconds,
where it ended from where it began and its main direction)
and, for the crossed zones with *Martlet notices* on (like a tap there),
records it in the touch ledger once for each pass (at most 8) with its whole
path (`CharacterPhysicalWords.Stroke`: every zone in the order first crossed,
at most 8 named, a left and a right zone crossed one after the other said
together), so the next reply hears *They slowly stroked your hair 4 times* or
*They slowly stroked down from your chest over your stomach to your thighs
once* (*up and down over* or *back and forth over* the zones when it turned
back); a stroke, like a tap, can
start a touch-only reply. Ctrl+drag or middle-drag still pans a zoomed view,
and an unlocked drag still moves the character. `character_stroke` strokes it
through UI Automation (`MoveAvatar`'s value, `"stroke:ms;x,y;x,y;..."`) along
`points` (`[[x, y], ...]`, 2 to 200, fractions of the overlay's drawing), one
every `stepMs` (default 40), which needs `--allow-ui-effects` and a locked
position, waits for the stroke to end and returns the overlay's reading as
`last`: the last tap's fields plus `stroke` (`n`, `samples`, `hits`, `ms` and
the coarse `zones` crossed) and `physical`. Without `points` it only reads.
Companion › Touch › Touch zones' `CharacterPhysicalLast` shows Martlet's
summary.

**Where Martlet draws over the face**: the blush levels (the blush on a model
without a blush of its own, and `blush_deep` and `blush_fierce` on every model,
over its own blush) and the overlay emotes are drawn around the face each time
the renderer page draws a frame. A Live2D model's face is pinned to its own
face meshes. Live2D draws a face in layers that move apart as the head nods and
turns (the back hair, the skin, the eyes, nose and mouth over it), so when the
model loads, the page first looks for the face's skin: of the drawables that
show, the one drawn highest whose triangles hold the face's middle and both
cheeks (one more than three face widths across or high is not skin). The face
is then pinned to every vertex of the skin, so the blush stays on the cheeks,
also on a model whose physics turns its head (head angles that only feed
parameters such as `ParamFaceAngleX`). A model without such a drawable is
pinned to the vertices that turn with the head instead: the page moves each
head angle (`ParamAngleX`, `ParamAngleY`, `ParamAngleZ`) to find them, then
moves every other parameter to its limits, to drop the vertices that change
shape on their own (hair physics, eyelids, eyes, mouth, brows), and puts every
parameter back. In each frame the eyes, cheeks,
mouth and top of the head move with those vertices, so they follow idle
motions, body sway, breathing, the mouse, a look at a point and gestures as
the model draws them. A model with neither uses the
earlier estimate from its head angles. A VRM's face follows its posed head
bone. Each blush lies on its cheek's surface: a turned head shows the near
cheek wider and the far cheek narrower, and the far cheek fades out as it
turns away. `character_face` reads this through UI Automation (`MoveAvatar`'s
value `"face"`), `samples` times (1 to 60) `gapMs` apart (default 250). It
changes nothing, so it needs no `--allow-ui-effects`. Each reading in `faces`
has `n`, `found`, `tracking` (`mesh`, `bones` or `estimate`), `x`, `y` and
`width` (fractions of the overlay's drawing, +y down), `tilt` (degrees,
clockwise), `cheekLeft` and `cheekRight` (`x`, `y`, `visible` from 0 to 1,
`across`, the cheek's width against the face's width, and the hit test there:
`hit`, `drawables`, `bone`, `mesh`), `eyeLeft`, `eyeRight`, `mouth` and `top`
(`x`, `y`: the eye and mouth points the overlay emotes such as `tears` or
`tongue_out` are drawn from, and the top of the head; left out when the
renderer has none; an eye with a known iris has its point at the eye's
middle), `overlays` (the overlays showing, such as
`["blush_deep"]`; one fading out is listed until it is gone) and
`pinned` (Live2D: `carriers`, how many mesh vertices the face rides on,
`skin`, the ID of the face's skin drawable when they are its vertices, or
`null` when they are the vertices that turn with the head,
`milliseconds`, how long finding them took at load, and `eyeMilliseconds`, how
long finding the eyes' meshes took). The eyes for drawings over them come with
each reading: `eyesFrom` (`mesh`: a Live2D model's iris and eye-white meshes;
`bones`: a VRM's eye bones with its iris and eye-white meshes; `vision`: eyes
measured by vision fill what the model can't give; `estimate`: an eye has
neither, so its iris and opening are left out), `irisLeft` and `irisRight`
(`x`, `y`, `rx`, `ry`: the iris's middle and radii across and down the face;
`x` and `rx` are fractions of the drawing's width, `y` and `ry` of its height;
`null` when unknown), and `eyeLeftShape` and `eyeRightShape` (the eye's visible
opening now: `points`, `triangles`, `null` for an outline, its box `left`,
`top`, `right`, `bottom`, and `irisInside`, whether the iris's middle is in
it; 0 `points` when the eye is closed or hidden). `summary` gives the
`tracking` used, how far the face `moved` (`x`, `y`, `width`, `tilt`) and, for
each cheek, `onCharacter` (the share of readings over the character),
`mostlyOver` and `mostlyOverShare` (the topmost drawable, mesh or bone there
most often, and for what share of readings), `visibleLeast` and `across`
(`least`, `most`). It also gives `eyesFrom` (the sources seen) and, for
`eyeLeft` and `eyeRight`, `iris` (the share of readings with an iris),
`irisMoved` (`x`, `y`: how far the iris's middle moved), `irisInside` (the
share of open readings with the iris inside its opening), `opening` (`least`,
`most`: the opening's height, which a blink closes) and `closed` (the share of
readings with no opening). Sample several times to see the irises follow the
gaze and the openings close on a blink. `MoveAvatar`'s value in `ui_snapshot`
shows the last reading as `face`.

`character_picture` takes a picture of the showing character as it shows now
(`MoveAvatar`'s value `"picture"`). It is the renderer's own capture of the
overlay's page, so Martlet's drawings over the face are in it, such as the
blush glow and overlay emotes like heart eyes. It is cropped to the character
with a little room. Zoom the character first (`SetupCharacterZoomIn`) to see
small parts such as the eyes larger. It returns `taken`, `picture` and `saved`.
`picture` has `n`, `path` (the renderer's PNG file in the temp folder, replaced
each time), `width` and `height` in pixels, and `left`, `top`, `cropWidth` and
`cropHeight`: where it sits on the overlay's drawing, as fractions like
`character_face`'s positions. When the picture can't be taken, `picture` has
`error`. `saved` is the copy at `outputPath` (a full path to a `.png` file)
when given. It changes nothing on the character, so it needs no
`--allow-ui-effects`. `MoveAvatar`'s value then shows the last picture as
`picture`.

**Where the touch zones are now**: `character_zones` reads where each area of
the showing character's touch zones is, as each frame is drawn (`MoveAvatar`'s
value `"zones"`), `samples` times (1 to 60, default 1) `gapMs` apart (0 to
5000, default 250). The zones are the ones Martlet last gave the renderer: the
saved zones in use of the model it shows. *Show the zones on the character*
(`TouchZonesShowOnCharacter`) draws the same areas over the character. An area
that follows Live2D drawables (a tail's, which swings) is the box around those
drawables where they are drawn now (`from`: `drawables`). An area that follows
two or more of a VRM's spring-bone joints (a tail's) is the box around those
joints where they are now, as wide on each side as its box is at its narrowest
(`nodes`), so a tail segment that swung sideways lies sideways. An area that
follows VRM bones, or one joint, keeps its size and moves with them (`bones`). Any other
area stays at its box, moved only by the view's zoom and pan (`box`). `last`
has `n`, `found`, `renderer`, `draw` (whether the areas are drawn on the
character) and `areas`: each has `zone`, `area` (0 for the zone's first),
`from`, and `left`, `top`, `right` and `bottom` (fractions of the overlay's
drawing, +y down). `summary` gives, for each area, what placed it, its last box
and how far its middle moved (`x`, `y`). Sample several times while the idle
motion plays to see a tail's areas move with it. Reading changes nothing, so it
needs no `--allow-ui-effects`. `MoveAvatar`'s value in `ui_snapshot` shows the
last reading as `zones`.

**The character's idle body**: while it shows, a VRM stands in a relaxed pose
(arms hanging close to the body, elbows softly bent, fingers and thumbs
curled), breathes and sways slowly; see *VRM idle pose and breathing* in
[Emotes and motions](AVATARS.md#emotes-and-motions). `character_pose` reads
it through UI Automation (`MoveAvatar`'s value `"pose"`), `samples` times (1
to 60) `gapMs` apart (default 250). It changes nothing, so it needs no
`--allow-ui-effects`. Each reading in `poses` has `n`, `found` (false before a
model shows and for a Live2D model, whose own breathing isn't read),
`renderer`, `idle`, `breathing` (`phase`, 0 to 1 of one breath; `inhale`, how
full the chest is, 0 to 1; `perMinute`), `arms` (`left` and `right`:
`fromDown`, the upper arm's angle from straight down, and `elbow`, its bend,
in degrees measured on the posed bones), `curl` (`left` and `right`: the
middle finger's curl in degrees, 0 straight as in the T-pose), `sway` (the
spine's sideways lean, degrees) and `bones` (`head`, `neck`, the shoulders,
upper arms, hands and upper legs: `x`, `y` as fractions of the overlay's
drawing, +y down). `summary` gives `found`, whether it stayed `idle`, the
`inhale`, `perMinute`, `arms`, `curl` and `sway` ranges (`least`, `most`) and
how far each bone `moved` (`x`, `y`). Over 20 readings 250 ms apart, a VRM
breathes in from 0 to about 1 at 13 to 16 breaths a minute, with its arms 15
to 18 degrees from straight down, its elbows bent about 16 degrees and its
fingers curled about 60 degrees. `MoveAvatar`'s value in `ui_snapshot` shows
the last reading as `pose`.

**Who moves the character's mouth**: while Martlet speaks, its voice has the
character's mouth, and an emote's mouth comes back about a second after the
voice stops (see *The voice has the mouth* in
[Emotes and motions](AVATARS.md#emotes-and-motions)). `character_mouth` reads
it through UI Automation (`MoveAvatar`'s value `"mouth"`), `samples` times (1
to 60) `gapMs` apart (default 250). Each reading in `mouths` has `n`, `found`
(false before a model shows), `renderer`, `parameter` (Live2D: the parameter
read, `ParamMouthOpenY` when the model has it), `voice` (0 to 1: how much the
voice has the mouth), `speaking` (the voice moved the mouth within the last
second), `level` (the voice's loudness on it), `emote` (how far the emotes
open the mouth before the voice takes it; a VRM: its held open mouth), `open`
(how far the mouth is open now, 0 at rest to 1) and, for a VRM, `blocked` (how
much the expressions showing block its mouth expressions). `summary` gives
`found`, the share of readings `speaking`, the `voice`, `level`, `emote`,
`open` and `blocked` ranges (`least`, `most`) and the `last` reading. Reading
changes nothing, so it needs no `--allow-ui-effects`.

With `levels` (1 to 400 loudness levels from 0 to 1), the mouth first moves as
Martlet's loudness lip-sync moves it, one level every `stepMs` (10 to 1000,
default 50), without a sound (`MoveAvatar`'s value `"voice:ms;level;..."`).
The readings start at once. This changes the character, so it needs
`--allow-ui-effects`. For example:

1. Hold `mouth_open` with its `CharacterActionTry-<n>` button.
2. Send 20 levels with 40 samples 100 ms apart.
3. Check that `open` follows the levels while `speaking`, stays near 0 in a
   pause, and goes back to `emote` about a second after the last level.

`MoveAvatar`'s value in `ui_snapshot` shows the last reading as `mouth`.

**Where the character looks now**: `character_look` reads it through UI
Automation (`MoveAvatar`'s value `"look"`), `samples` times (1 to 60) `gapMs`
apart (default 250), as the overlay last turned the head and eyes (it does
every 50 ms; see [Where the character looks](SCREEN_COMMENTARY.md#where-the-character-looks)).
Each reading in `looks` has `n`, `target` (`mouse`; `window`: where you work in
the window you're using; `point`: a spot Martlet asked it to look at; or
`ahead`), `x` and `y` (the direction, -1 to 1, +x right, +y up), `at` (the
point on the desktop in screen pixels, or null), `usual` (the usual gaze),
`window` (the window you're using: `left`, `top`, `width` and `height`, never
its title; null when none) and `watching` (for the window gaze: `pointer`,
`text cursor` or `middle`). `summary` gives the `targets` and `watching` seen
and the `x` and `y` ranges (`least`, `most`). Reading changes nothing, so it
needs no `--allow-ui-effects`. While the Windows session is locked, no window
is in front and the mouse can't be read, so the window gaze keeps the last
window it knew, or reads `ahead` with no `window` when it knew none.
`MoveAvatar`'s value in `ui_snapshot` shows the last reading as `look`.

**Moves, zooms and other changes Martlet hears about**: the overlay notes each
drag, arrow-key nudge, `ui_move`, zoom (wheel, menu, keys or Martlet's zoom
buttons), reset zoom, pan of a zoomed view and Reset position, and once it has
settled for 0.8 seconds sends one `physical` message (`moved`, `home`,
`zoomed`, `zoom_reset` or `panned`, with how far and which way it moved, the
monitors before and after, the character's size before and after, and the
renderer page's hit test of what a zoom closed in on or a pan centers on).
Martlet records it in the touch ledger with Locked, Unlocked, Hidden and Shown
(from its own buttons and the overlay menu): *They moved you to their other
monitor*, *They zoomed in on your face*. A move (`moved` or `home`) starts a
short reply of its own like a touch (`TouchZonesNoticed` says when); the
others never do and go with the next one. `MoveAvatar`'s value carries the last one as
`physical` (`kind`, `dx`, `dy`, `from`, `to`, `zoomFrom`, `zoomTo`, `focus`),
and `CharacterPhysicalLast` shows the ledger's words. The camera view's own
framing is not reported.

`character_physical_check` runs the same summary and wording headless, with no
desktop and no model request: `stroke` (a JSON `CharacterStroke` with its
hit-tested samples) is summarized against the touch zones saved for `modelId`
(or the rough zones before any were found), `changes` (a JSON array of
`RendererPhysical`) are worded, and both go into a touch ledger. It returns the
stroke's zones (where zones overlap, each zone a sample was on), pace, passes, `dx` and `dy` (where it ended from where it
began, page heights), `sideways`, `way` (`down`, `up` or null) and `words`
(`where`, `label`, `pace`, `times` and `hint`: how the ledger says its whole
path), each change's ledger kind, words and `startsTurn` (true for touches,
strokes and moves), the plain
`line` the next reply would carry, the `history` line and `startsTurn`,
`character` (what the talk window and the Thinking model call the character:
the name of the persona the data directory's settings use, else *Martlet*)
and `note` (the talk window's note for a reply to them alone, such as *You
touched Ivy (touch: hair stroke x4, moved)*, or null when they wouldn't start
one).
`noticeAll` (default true) treats every zone as having *Martlet notices* on;
false uses the zones' own setting. `personaId` takes that persona's touch
temperament from `character-temperaments.json` and returns the stroke's
`feeling` (*you love it on your chest, and hate it on your groin*), with
`intimate` and `interrupts` (whether the stroke would stop Martlet talking with
`touchInterrupts`: `any`, the default, `intimate` or `never`). `earlier` (0 to
20) records the same stroke that many times before, a minute apart, each taken
by a reply, so `often` lists the places the user keeps coming back to (`place`,
`count`, `minutes`) and the line ends with *They keep coming back to ...*.
`said` and `answering` stand for a touch that stopped Martlet talking. It also
returns `told` (what the Thinking model hears of the touches, with *Touched,
cutting you off* when a touch stopped Martlet), `message` (a touch-only
reply's whole message, Companion › Prompts › *Touched*) and `notes` (*Touched,
with your message*), with the prompts saved in the data directory.

**Locking the character's position**: Home's `ToggleCharacterLock`
(*Lock character position*, shown while the character shows or is locked),
Companion › Character's `SetupCharacterLock` (*Lock position*) and the overlay
menu's `CharacterLockPosition` (*Lock position*, carried out by Martlet: *The
character's menu chose 'lock'.*) lock it where it is; all need
`--allow-ui-effects` because they save `character-placement.json` (see
`character_status`'s `placement`). Locked, `movable` is false and `ui_move` is
refused; the overlay ignores dragging, the arrow keys and Home, and its own
`CharacterResetPosition` is disabled; and zoom (the wheel, the menu or
`SetupCharacterZoomIn`) only zooms the camera, keeping the overlay's bounds.
The same `ToggleCharacterLock` and `SetupCharacterLock` then read *Unlock
character position* and *Unlock position* (also while the character is
hidden), and the overlay menu's `CharacterLockPosition` reads *Unlock
position* (carried out by Martlet: *The character's menu chose 'unlock'.*);
each unlocks it. `SetupCharacterPlacement` says whether the position is locked and
where (device-independent pixels and the monitor), and `SetupCharacterView` ends with
*Position locked.* when the overlay reports it. A locked character shows at
its locked place again after Hide/Show or a Martlet restart (at its default
spot, still locked, if that place is no longer on a screen); the desktop log
records *Character position locked at ...* and *Character position unlocked.*,
and `avatar-renderer` *The character's position is locked.*

**Letting clicks pass through the character**: the overlay menu's
`CharacterClickThrough` (*Let clicks pass through*, carried out by Martlet:
*The character's menu chose 'click-through-on'.*), Home's
`ToggleCharacterClickThrough` (*Turn on click-through*, shown while the
character shows or click-through is on), Companion › Character's
`SetupCharacterClickThrough` (*Turn on click-through*) and the notification-area
menu's checkable `TrayCharacterClickThrough` (*Let clicks pass through the
character*, listed while the character shows or click-through is on) turn it
on; all need `--allow-ui-effects` because they save
`character-click-through.json` on this PC (`character_status`'s
`clickThrough`; never shared, and Reset position leaves it alone). On, the
overlay window (and its speech bubble) gets `WS_EX_TRANSPARENT`, so every
click, the wheel and right-click go to the window under it: `ui_snapshot`'s
`windowStates` reads `clickThrough` true for *Martlet character overlay*, and
the character can't be dragged, zoomed, tapped, stroked or right-clicked with
the mouse (its eyes still follow the mouse, and it still talks and moves).
The mouse can't reach the character's menu then, so it is turned off in
Martlet: `ToggleCharacterClickThrough` and `SetupCharacterClickThrough` read
*Turn off click-through* (also while the character is hidden),
`TrayCharacterClickThrough`'s `checkedState` is `On`, and clicking any of them
turns it off (opened through UI Automation, the overlay menu's
`CharacterClickThrough` reads *Stop letting clicks pass through* and does the
same). `SetupCharacterClickThroughNote` says whether it is on, and
`SetupCharacterView` ends with *Clicks pass through.* when the overlay reports
it. A newly shown character starts click-through when it is on (after a
Hide/Show or a restart). The camera view always catches clicks and applies
click-through again when it closes. The desktop log records *Click-through
turned on: clicks pass through the character.* (or *... turned off ...*) and
`avatar-renderer` *Clicks pass through the character.* or *The character
catches clicks again.*

**Remembering where the character is**: whenever the character is dragged,
nudged, resized, zoomed or sent home (`ui_move` included), the overlay asks
Martlet (request `placed`, never logged as a menu choice) to read its place
(renderer command `where`) and save it in `character-placement.json`, locked
or not, with the monitor it is on; the desktop log records *Character position
saved at ... on DISPLAY2.* The next showing (Hide/Show, a restart, a shutdown
or an update) puts it back on that monitor at the same spot, with the
character's middle kept on the monitor's work area; if that monitor is gone,
where it was when that spot is still on a screen, else its default spot
(`avatar-renderer` logs *The character is back where it was left on
DISPLAY2.*). Home's `ResetCharacterPosition` and Companion's
`SetupCharacterResetPosition` (*Reset position*, need `--allow-ui-effects`)
show while the character shows or a place is saved: showing, they move it to
the lower-right of the main screen even when locked (it stays locked there)
and save that; hidden, they forget the saved place so it next shows at its
default spot, unlocked (`placement.state` `none`).

**When the character's renderer fails a command** (its pipe breaks, it sends
something unreadable or it runs out of time): only the work that draws the
character ends. A sentence's lip-sync (loudness mouth or Audio2Face frames), a
song's mouth, a gaze, an emote or saving where the character is stops, and the
voice goes on. `ToggleCharacter` (*Hide character*) always finishes, and no
*Martlet recovered from an unexpected error* dialog shows. Each kind of failure
gets one short desktop log line a minute, without a stack trace, for example
*The character's new position couldn't be read to save it: Renderer message
length is invalid (InvalidDataException).*; the next line for the same failure
adds *(N more like it in the minute before weren't logged.)*. To check this
without a broken renderer, set `MARTLET_SIMULATE_RENDERER_FAILURE` to the
renderer commands to fail, comma-separated (for example `where,lock,zoom`),
before launching the desktop (`-Desktop` passes the environment on). FIXTURE,
never a real failure: the shown character's renderer fails those commands the
way a broken pipe does (*Renderer message length is invalid (simulated by
MARTLET_SIMULATE_RENDERER_FAILURE).*), and the desktop log says so each time
the character shows (*FIXTURE: the character renderer fails its ... commands*).
The renderer still starts, draws and closes normally. Commands include `where`
(saving its place after `ui_move`), `lock` (`ToggleCharacterLock`), `zoom`
(`ResetCharacterZoom`), `home` (`ResetCharacterPosition`), `place` (a character
profile's place when you switch profiles), `mouth` (the
loudness mouth), `reset` and `apply` (Audio2Face frames), `gaze`, `action`
(emotes and motions), `say` (speech bubbles), `theme` and `camera`.

Companion › Speech bubbles (`CompanionTab-SpeechBubbles`) has the *Speech bubbles and subtitles* card with the checkboxes
`SetupCharacterSpeechBubbles` (on by default) and `SetupCharacterSubtitles`
(off by default); snapshots return their states, and `SetupCharacterSpeechDisplay`
returns whether each is on and whether bubbles show now (character showing) or
once it is. Ticking either saves `speech-display.json` (the same choices as the
character window's `SpeechBubbleChoice` and `SubtitleChoice`), so `ui_toggle`
needs `--allow-ui-effects`. With the character showing,
`SetupCharacterPreviewBubble` (also `--allow-ui-effects`) sends a sample bubble
to the overlay for a few seconds; `SetupCharacterSpeechDisplay` then says
whether the overlay took it and where it put it (to the left or right of the
character's head, above it, or in its fixed place, with the bubble's screen
position and size), whether the text as laid out on screen lies inside the
bubble (*holding all its text*, or *but its text doesn't fit inside it*), and
whether the colors it is drawn in are the palette Martlet's windows use now
(*in the Character dark colors*, or *but not in the Pink light colors (fill
#..., outline #...)* naming each part that differs). The overlay reports the
fill, outline, text and halo colors of the bubble as shown; they are the
palette's Surface, Accent, Text and Glow (no halo in Windows' high contrast).
Every bubble is sized to its whole text, including the first one after the
bubble was hidden; very long speech widens it (up to 640 pixels) so it stays
within half the screen's height. The bubble itself is drawn by the separate
renderer process, so its text is not in snapshots.

The same card sets where the bubble goes. `SetupCharacterBubblePlacement`
returns *Follows the character* (default: beside the head on whichever side has
room on its screen, then above it, following moves, zoom and pan) or *Stays in
one place*; `SetupCharacterBubbleOffsetX` and `SetupCharacterBubbleOffsetY`
return the pixel offsets (positive is right and down; in one place they are
measured from the top-left of the character's screen). `ui_select` and
`ui_set_text` on them save `speech-display.json` (`StaticBubble`,
`BubbleOffsetX`, `BubbleOffsetY`), so they need `--allow-ui-effects`;
`SetupCharacterSpeechDisplay` reads back the saved position, or says an offset
isn't a number from -4000 to 4000. Where the bubble sits depends on the
computer's screens, so it stays with each computer; whether bubbles and
subtitles show is the same on all of them (the `speech-display` shared setting).

Companion › Character's *Your characters* card lists the built-in character and
every [shared character](CLUSTER.md#the-shared-character-models) in the order
they joined. `CharacterModelsStatus` reads how many characters of the owner's
own there are and what this PC shows ("1 character of your own. This PC shows
one of your characters." or "... the built-in character.", never a name).
`CharacterModelsShared` reads whether they are shared with the paired Martlet
computers ("Characters shared with 2 of 2 computers at 7:15 PM.", characters
still copying to this PC, hosts to update, or "No other Martlet computers are
paired yet, so your characters stay on this PC."). Each row's detail line
`CharacterModelState-<key>` (`builtin`, or the first 16 hex digits of the
character's ID) returns its renderer, size, where and when it was added,
"Copying to this PC..." while pieces are missing and "Shown on this PC." for the
one shown, never its name. Its controls are `CharacterModelUse-<key>` (disabled
while it is shown or still copying) and, unless shown, `CharacterModelRemove-<key>`
(asks with `ConfirmationYes`/`ConfirmationNo` and removes it on every computer).
`CharacterModelAdd` opens *Add a character* (`CharacterModelAddDialog`):
`CharacterModelAddPath` (the `.model3.json` or `.vrm` full path),
`CharacterModelAddName`, `CharacterModelAddOk` (adds, shares and shows it on this
PC) and `CharacterModelAddCancel` (passive); `CharacterModelAddProblem` returns
why it couldn't (never the typed name or path). Use, Remove and adding need
`--allow-ui-effects`. In the character settings window (`OpenAvatar`), a model
typed into `AvatarModelPath` (after `ui_select CharacterChoice` "My own model
file") joins the shared list as soon as it is saved (once it names an existing model file; there is no Save button) or shown (`ShowCharacter`), and the
saved profile then shows Martlet's copy. `character_models` reads the same list
and copies headlessly.

**Character profiles** (`CompanionTab-Profiles`, first under *Who it is*) switch
the look, the voice and the personality together. `CharacterProfilesNow` is the
Now card (it names the character, so it isn't a safe value);
`CharacterProfilesStatus` reads how many profiles there are and whether one is
in use ("2 profiles. One of them is in use." or "None matches what Martlet uses
now."). Each row's `CharacterProfileState-<key>` (the first 8 hex digits of the
profile's ID) reads "In use.", "Ready." or why a part can't switch here ("Its
look is still copying to this PC. Using it switches the rest.", "Its voice is no
longer in your voices."), never a name. Each row's `CharacterProfileHere-<key>`
reads what the profile keeps on this PC ("On this PC: its own spot and size (360
× 480, locked) · Eyes: Watch the window you're using, replies can't change it ·
While it talks: only intimate touches stop it." or "On this PC: nothing yet.
..."). While a profile is in use, moving, resizing, locking or resetting the
character, choosing where it looks (`CharacterGaze-<choice>`,
`CharacterGazeFree` or the overlay's Eyes menu) and choosing which touches stop
it (`TouchInterrupt-<choice>`) are kept for that profile on this PC. Using a
profile first keeps the one in use, then puts back the new one's choices: the
showing character moves to its place without counting as a move of yours
(renderer command `place`; `avatar-renderer` logs *The character moved to where
its profile left it on DISPLAY1.*) and the desktop log records *Switched to a
character profile (<key>); on this PC: place 360 × 480 locked on DISPLAY1, gaze
window (fixed), touches while talking intimate.* A switch made on another
computer, also while Martlet was closed, is followed after the shared settings
arrive (*Following the character profile switched to last (<key>); ...*). A
profile that keeps nothing here yet takes on what this PC uses. Its controls are
`CharacterProfileUse-<key>` (disabled while in use), `CharacterProfileEdit-<key>`
(passive: opens the form) and `CharacterProfileRemove-<key>` (asks with
`ConfirmationYes`/`ConfirmationNo`). `CharacterProfileNew` (passive) opens the
form filled in with what Martlet uses now: `CharacterProfileName`,
`CharacterProfilePersona`, `CharacterProfileLook` ("Keep the current look", the
built-in character or one of your characters), `CharacterProfileVoice` ("Keep
the current voice" or one of your voices), `CharacterProfileSave` and
`CharacterProfileCancel` (passive); `CharacterProfileEditorProblem` returns why
it couldn't save. `ProfilesOpenCharacter`, `ProfilesOpenVoice`,
`ProfilesOpenPersonality`, the Character and Personality pages' `OpenProfiles`
and Home's `HomeManageCharacters` only open pages. Home's `HomeCharacterProfile`
combo box lists every profile ("A mix of your own" when none matches) and
switches on selection; the notification-area menu's `TrayCharacterProfiles`
submenu has `TrayCharacterProfile-<key>` items, the one in use ticked. Use,
Save, Remove and switching from Home or the menu need `--allow-ui-effects`.
`character_profiles` reads them headlessly.

The **Creations** page (`NavCreations`, between Companion and Diagnostics; its
content is `CreationsPage`) lists [what Martlet made](CREATIONS.md), newest first.
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
checking the page.

Companion › *Emotes and motions* (`CompanionTab-Emotes`) lists the
[emotes and motions](AVATARS.md#emotes-and-motions) of the character this PC
shows (or would show). `CharacterActionsStatus` reads how many emotes and
motions the model has (with the Martlet gestures its rig supports) and whether they were named
by the Thinking model (and when) or from the model's own files, or why they
couldn't be read; `CharacterActionsNaming` the Thinking model's naming
(*Asking the Thinking model...*, *The Thinking model named 10 emotes and motions
at 3:12 PM and turned off 3 that aren't feelings or gestures.*, or why it
couldn't, such as *Thinking isn't set up yet*); `CharacterActionsOffered` the
tags replies get with the voice chosen now and which follow the voice's cues;
`CharacterActionsLast` the last one played (*Played the expression "脸红" for
{blush} at 3:14:05 PM.*, *... for a try ...*, or *The character couldn't play
...*; for a gesture followed by what the renderer now plays and every gesture
and drawing it holds, *Gestures now: wink playing, eyes_up, mouth_open, blush,
hearts held.* (held gestures layer; see
[Layers](AVATARS.md#emotes-and-motions)); an emote Martlet drew over the face itself, such
as the blush glow on a model without a blush of its own, or `blush_deep` and
`blush_fierce` on any model, adds *drawn by Martlet
over the face at 414, 88 (50 pixels wide, tilted 3°, pinned to the face's meshes)* with the face's middle and
width in the overlay's page pixels, the head's roll (clockwise) and how the face is followed (*pinned to the
face's meshes* for Live2D, *following the head bone* for VRM, or *estimated from the head's angles* for a
Live2D model without face meshes to pin to; a Live2D estimate's roll is a damped share of `ParamAngleZ`, at
most 12°; see *Where Martlet draws over the face* and `character_face`), or *(not in view now)* when the face can't be found
or faces away), also in `logs_tail` `desktop` as *Character expression '脸红'
played for {blush}.* (*Character gesture 'blush' played for a try, drawn by
Martlet over the face at ...*, a gesture's line ending with the same *Gestures
now: ...*); and `CharacterActionsSaveState` *All changes saved.* or *Not saved:
<why>*. Row `<n>` (as in `character_actions`) has `CharacterActionName-<n>`
(its name and kind; a status field), `CharacterActionOn-<n>` (check box),
`CharacterActionTag-<n>` (an English tag; a tag in another script reads *Not
saved: ... use up to 24 English letters (a-z) ...*), `CharacterActionCue-<n>` (combo box: `(none)` or a
cue such as `laugh`), `CharacterActionUse-<n>` (the When to use box),
`CharacterActionHint-<n>` (the grey
hint in that box while it is empty, the text replies get then, such as *nod, for
yes or agreement* or *the character's emote named "Glasses"*; hidden once the
box has text), `CharacterActionMode-<n>` (the
*Stays on* check box, its mode as `checkedState`: on for a lingering emote) and `CharacterActionTry-<n>`
(plays it on the showing character, or turns a lingering one on; its label, a
status field, reads *Turn off* while that lingering emote is on, and clicking it
then turns it off; disabled while it is hidden). `CharacterActionsHeld` reads
the lingering emotes on now, all of them, held gestures and drawings included
(*On now: Glasses (12 min), Blushing (just now).
Clear emotes on the character's menu turns them off.* or *No lingering emotes
are on.*); `CharacterActionsLast` then reads *Turned on the expression ...* or
*Turned off ...* (for a gesture with the renderer's *Gestures now: ...*, which
shows the others still held), and `logs_tail` `desktop` *Character expression 'Glasses' held
for a try.* `CharacterActionsClear` (*Clear emotes*) and the character overlay
menu's `CharacterClearEmotes` turn every lingering emote off (*Cleared 2
lingering emotes for Clear emotes ...*).

The card ends with **Combos**. `CharacterCombosStatus` reads how many combos
the model has and which tags replies get (*9 combos. Replies can use 8:
{lovestruck} {flustered} {overheated} {fuming} {heartbroken} {dozing}
{starstruck} {shocked}.* for the built-in character, which has all of
Martlet's combos with `{ahegao}` off, or *No combos yet.*). `CharacterCombosAdd` (*Add a combo*,
passive) adds an empty row; nothing is saved until the row has a tag and parts.
Row `<n>` (as in `character_actions`' `combos`) has `CharacterComboOn-<n>`
(check box: its `checkedState`, and `ui_toggle` turns it on or off),
`CharacterComboName-<n>` (*{flustered} · combo*, or *New combo*;
a status field), `CharacterComboState-<n>` (a status field: what it sets off,
such as *Turns on "hearts" until {/flustered}; plays "blush" and "nod" once.*,
or why its parts can't be read, such as *No emote has the tag 'wave'.*),
`CharacterComboTag-<n>`, `CharacterComboParts-<n>` (the parts' tags, such as
`blush hearts nod`), `CharacterComboUse-<n>` (the When to use box),
`CharacterComboHint-<n>` (the grey hint in that box while it is empty, such as
*a combination of {blush}, {hearts} and {nod}*), `CharacterComboTry-<n>` (its
label, a status field, reads *Turn off* while one of its lingering parts is
on) and `CharacterComboRemove-<n>`. A part's tag that names no emote reads
*Not saved: no emote has the tag 'wave'.* in `CharacterActionsSaveState`. After
Try, `CharacterActionsLast` reads *Combo {flustered} for a try at 6:22:47 PM:
turned on "hearts"; played "blush" and "nod".* (*Combo {/flustered} for a try
...: turned off "hearts".* after Turn off, and *for a reply* when a reply's tag
set it off), and `logs_tail` `desktop` has a line for each part and *Character
combo {flustered} for a try: 1 turned on, 0 kept on, 2 played, 0 failed.* Typing
in a combo's boxes, its check box and Remove save, and Try and Turn off change
the overlay, so they need `--allow-ui-effects`.

Editing an emote's row saves `character-actions.json`, `CharacterActionsDetect` (*Name them with
Thinking*) sends the model's emote and motion names and details to the Thinking
model, `CharacterActionsReset` goes back to the model's own names (the combos stay), and Try,
Turn off and Clear emotes change the overlay, so all of them need `--allow-ui-effects`. The first time a model
shows with a Thinking model set up, Martlet names its emotes once on its own.
`character_actions` reads the same settings headlessly.

For voices, open `CompanionTab-Voice` (the Voices card shows unless the
voice comes from a cloud provider). There are no built-in voices and no groups:
one list, in the order voices joined it (a new list starts with the starter
voices). `F5VoicesStatus` reads how many voices there are and which is chosen or in
use (a starter voice's name, "one of your recordings", or "a voice no longer in
the list"), for example "6 voices. None chosen yet; Martlet starts with Jenny
(Dioco)." `F5VoicesShared` reads whether the list is shared with the
paired Martlet computers ("Voices shared with 2 of 2 computers at 7:15 PM.",
voices still copying to this PC, hosts to update, or "No other Martlet computers
are paired yet, so your voices stay on this PC."). Each voice whose recording is a
starter clip has a title `F5VoiceRow-<key>` (for example `F5VoiceRow-arctic-slt`)
that returns its name with "· chosen" or "· in use" when it is. Its controls are
`F5VoicePlay-<key>`, `F5VoiceUse-<key>` and, unless it is in use or chosen on all
computers, `F5VoiceRemove-<key>`; any other voice's controls use the first 16 hex
digits of its ID (its reference revision) instead of the key, and its name is not
returned. Every voice's detail line, `F5VoiceDetail-<key>`, returns its length
(or, for a voice made from several recordings, "3 recordings, 10.5 seconds
joined, added ..." and whether the speaking engine learns from each recording or
hears them joined), where a starter voice comes from and why the engine can't
use it; never its name or words. When the speaking engine cannot clone a voice (Chatterbox: 5 seconds or
shorter; GPT-SoVITS: shorter than 3 or longer than 10 seconds, unless one of
its recordings is 3-10 seconds) its title adds
"· wrong length for this engine" and `F5VoiceUse-<key>` is disabled, with the reason
as its help text; Play and Use are also disabled while its recording is still
being copied to this PC. Remove asks with `ConfirmationYes`/`ConfirmationNo` and
removes the voice on every computer. `F5AddVoice` opens *Add a voice*
(`F5AddVoiceDialog`): `F5AddVoicePath` (the recording's full path: almost any
audio or video file), `F5AddVoiceRecording` (read about half a second after the
path changes: "Reading the recording...", then what Martlet found, for example
"MP3, 7.5 seconds. Martlet converts it to a mono 16-bit WAV at 44.1 kHz.", or
why it can't be used, such as "This recording is 45 seconds long. ..."),
`F5AddVoiceName`,
`F5AddVoiceTranscript`, `F5AddVoiceBasis` (whose voice), `F5VoiceRights` (the
rights confirmation) and `F5AddVoiceOk`, which adds the voice (from the
converted WAVs, which are also what Play and Play joined play), shares it and
uses it; `F5AddVoiceProblem` returns why it couldn't (the typed name, transcript
and path are never returned). `F5AddVoiceMore` (passive: it only adds an empty row)
adds another recording of the same voice, whose controls end in its number
(`F5AddVoicePath-2`, `F5AddVoiceRecording-2`, `F5AddVoiceTranscript-2`,
`F5AddVoicePlay-2`, `F5AddVoiceBrowse-2`); with several, each row has
`F5AddVoiceDrop-<n>` (passive) and `F5AddVoicePlayJoined` plays them joined.
`F5AddVoiceRecordings` returns how many recordings there are and, once their
files are read, how long they are joined with the pauses ("3 recordings make one
voice, 10.5 seconds joined with the pauses.", more than 30 seconds, "Reading the
recordings...", or which recording Martlet can't use); never
paths or words. `F5AddVoiceAbout` returns the dialog's intro, which names the
speech-to-text that fills in each recording's words ("... Martlet fills in its
words with Parakeet on this PC ...") or, without one, says to download Parakeet.
With one, each recording's transcript is filled in from the converted WAV once
the file is read, unless the owner typed words there, and `F5AddVoiceHeard`
(`F5AddVoiceHeard-2`, ...) returns how that went ("Filled in by Parakeet on this
PC: 25 words. Check them and fix anything it misheard.", no words heard, "Kept
the words you typed." or why it couldn't; never the words). `F5AddVoiceFill`
(`F5AddVoiceFill-2`, ...) fills them in again, replacing what is there; it runs
speech-to-text (and may send the recording to the Listening host), so it needs
`--allow-ui-effects`. Use, Remove and adding change the voice list and need
`--allow-ui-effects`; Play plays audio and is not for automated verification.
Passive navigation writes nothing: the list is shown as it would start until a
voice is first used, added or removed. `f5_voices` reads the same list headlessly
and `voice_recording_check` runs the same conversion on a file.

Above the voices, the Voice engine card lists every way Martlet can speak on the
shown computer as one row each, keyed by engine (`chatterbox`,
`chatterbox-original`, `chatterbox-nano`, `f5`, `xtts`,
`gpt-sovits`, `dia`, and `windows` for a Windows voice under This PC):
`VoiceEngine-<key>` reads its name and badge ("Chatterbox Turbo · recommended",
"Windows voice · in use"), `VoiceEngineAbilities-<key>` the rundown of what it
can do ("Voice cloning: yes. Laughs & sighs: yes. Emotions: whispering only.";
shown as ✓ yes, ◐ partly and ✕ no, the same as `abilities.summary` in
`f5_voices`), `VoiceEngineRunsOn-<key>` where it runs and how much graphics
memory it takes ("Runs on an NVIDIA GPU: about 3.7 GB of graphics memory, up to
4.2 GB (6 GB+ card).", "Runs on the CPU: no graphics card needed." for a Windows
voice; `runsOn.text` in `f5_voices`; the cloud provider card shows
`VoiceEngineAbilities-openai` and `VoiceEngineRunsOn-openai`, "Runs online:
nothing runs on your computers.", when Speaking uses OpenAI),
`VoiceEngineFeatures-<key>` its other needs as chips ("Docker, 5 s+ samples,
English" for Chatterbox Turbo, "No Docker or download, Built-in Windows voices"
for a Windows voice;
the same list as `features` in `f5_voices`), `VoiceEngineState-<key>` (shown only
when the button doesn't already say it) where it stands ("Speaking on this PC.",
"Ready on gpu-pc.", "Setting up on gpu-pc...", or why it can't run there), and
`VoiceEngineUse-<key>` its one button ("Set up and use Dia", "Use XTTS-v2", "In
use Windows voice"; disabled with the reason as help text when the computer can't
run it). Under *Another of your computers*, the computer pills
`SpeakingHost-<host ID>` ("gpu-pc · speaking", "laptop · not reachable") only
choose which computer the rows set up, so clicking one is passive;
`SpeakingHostStatus` says when the shown one isn't reachable and
`HostChoices-speaking` when no other computer is paired. Clicking a
`VoiceEngineUse-<key>` button needs `--allow-ui-effects`: it asks one
confirmation, sets the engine up on that computer when needed (a run window) and
switches Speaking to it. Switching cleans up: a host runs one voice engine at a
time (adding one stops the others there; using one already installed stops the
leftovers), and the engine Speaking leaves on another computer, or when it moves
to a Windows or cloud voice, stops there once Speaking has moved (named in the
confirmation; kept when failover keeps the same engine there as a backup).
`SpeakingEngineOthers` (shown only then) names voice engines the speaking
computer still runs besides the one that speaks, for example a host set up before
that rule; `SpeakingEngineRelease` stops them after a confirmation
(`martlet-host remove`, downloads kept) and needs `--allow-ui-effects`.
`SpeakingEngineSharedGpu` (shown only then) warns that the shown computer runs
on Windows and its graphics card also does other jobs (its other roles, and
Thinking in Ollama on this PC), so its voice can fall behind
([Chatterbox](CHATTERBOX_VOICE.md#sharing-the-graphics-card)); it names the
voice engine there, or says one would share the card before one is set up.
`f5_voices` returns the chosen engine as `chosenEngine`.

Below the voice engine, the Singing card ([Singing](SINGING.md)) reads like a
voice engine row: `SingingEngine` ("Singing" or "Singing · ready"),
`SingingFeatures` its chips ("NVIDIA GPU 6 GB+, shared, Docker, Sings in your cloned
voice, With backing music, A few minutes per song, ACE-Step MIT · SoulX-Singer
Apache-2.0"), `SingingState` where it stands on the shown computer ("Not set up on
this PC yet.", "Setting up on gpu-pc...", "Ready on gpu-pc with SoulX-Singer.",
"Ready on this PC with SoulX-Singer and VevoSing.", "Adding VevoSing on this
PC...", "Setup failed on this PC: ..." or why that computer can't sing, such as
"Needs an NVIDIA graphics card with 6 GB+; this PC has ..."; the voice matches come
from that computer's singing service, read through its gateway once the card shows
it) and `SingingSetUp` its button ("Set up", "Setting up...", "Ready"; disabled
with the reason as help text when the computer can't sing). `SingingGpu` (fixed
text) answers whether Singing needs a graphics card of its own. With another
computer paired, the pills `SingingHost-this-pc` and `SingingHost-<host ID>` only
choose the shown computer (passive). `SingingQuality` ("Fast (recommended)", "High
quality ...") and `SingingVoiceMatch` ("SoulX-Singer (recommended: ...)",
"VevoSing ...") report the saved choices (SoulX-Singer by default); `ui_select` on
them saves `singing.json`, so it needs `--allow-ui-effects`. With VevoSing chosen
on a ready computer whose service doesn't list it, `SingingVoiceMatchState` says
so ("VevoSing isn't set up on this PC. Songs use SoulX-Singer until you add it.",
or "Adding VevoSing on this PC...") and `SingingSetUpVevo` (*Add VevoSing there*,
"Adding VevoSing..." while it runs) asks its own confirmation naming VevoSing's
CC-BY-NC-ND-4.0 terms and downloads only then. `SingingSetUp` asks one
confirmation naming the downloads, licences and terms and sets the role up through
`martlet-host add singing` (a run window on this PC whose `HostRunStatus` follows
the image build, the service starting and each model file's download, for example
"Singing: downloading model-svc.pt, 45% of 2730 MiB..."); both need
`--allow-ui-effects`. There is no play button: songs are only performed by Martlet
in conversation, so make and inspect real songs headlessly with `singing_check`.

On Companion › Tools (`CompanionTab-Tools`), the Terminal card comes first:
`ToolsTerminalOn` (*Let Martlet run terminal commands*, off by default) and
`ToolsTerminalAskFirst` (*Ask before every command*, on by default) report their
state as `checkedState`; `ToolsTerminalStatus` reads the state in words (*Off.
Martlet can't run commands on this PC.*, *On. Windows PowerShell, asks before
every command, stops a command after 30 seconds.*, or what keeps it from working:
the shell isn't installed, the start folder is gone, Thinking isn't set up or
can't use tools, or the model turned tools down); `ToolsTerminalShell` and
`ToolsTerminalTimeLimit` return the chosen shell and time limit. Everything
there saves `terminal.json`, so it needs `--allow-ui-effects`: `ui_toggle` on the
check boxes, `ui_select` on `ToolsTerminalShell` (*Windows PowerShell*,
*PowerShell 7*, *Command Prompt*, with *(not installed)* when missing) and
`ToolsTerminalTimeLimit` (*15 seconds*, *30 seconds*, *1 minute*),
`ToolsTerminalHome` (*Use my home folder*, shown only for a chosen folder) and
`ToolsTerminalFolder` (*Choose folder...*, a Windows folder picker MCP can't
drive; the folder's path is never returned). Turning *Ask before every command*
off asks first: the dialog's `ToolsTerminalNoAskQuestion` is returned, and
`ConfirmationYes` (*Run without asking*) or `ConfirmationNo` (*Keep asking*)
answers it. `terminal_status` reads the saved result. In the talk window, a
waiting call shows `LiveToolApproval` with `LiveToolApprovalTitle` (*Allow this
tool?*, or *Run this command?* for the terminal) and `LiveToolApprovalText`
(*Martlet wants to run this in Windows PowerShell, on this PC as you.
Automatically denied in 52 s.*); the command or arguments
(`LiveToolApprovalArguments`) are never returned, and `LiveStatus` reads *Allow
the command? Answer above.*, then *Running a command…*. `LiveToolDeny` is a
passive click (it only declines the call); `LiveToolAllow` and `LiveToolAlways`
run it, so they need `--allow-ui-effects`. With the terminal on, Home's
`HealthCheck-tools` tile shows (*Tools: OK. Terminal on*).

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

Companion › Smart home (`CompanionTab-SmartHome`): `SmartHomeFind` (*Find on my
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
(`ConfirmationYes`) and removes it; `DiscordEnabled` (on/off) and
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

Status fields include the talk window's `LiveStatus` (its status line),
`LiveMic` (the Start listening / Stop listening button; its value starts with
the state: *Not listening* until it is pressed, then *Listening*, *Can't
listen* or *Mic unavailable* with the reason; while Martlet speaks it reads
*Listening. Martlet listens for you, even while it speaks…* when echo
reduction works (or barge-in is on), and *Not listening while Martlet speaks,
so it doesn't hear itself* when listening pauses instead (echo reduction off
or not running, without barge-in). Clicking it opens the microphone, so it
needs `--allow-ui-effects`; errors, Stop and `LiveStop` never stop listening,
only the button or *Pause Martlet* in the notification-area menu does, and
listening that can't start yet, such as Voice ID not set up, reads *Can't
listen* and keeps retrying), Home's `OpenLiveConversation` (*Start talking*,
or *Show conversation* while a conversation runs, shown or hidden), Home's
`HomeListen` (*Start listening* / *Stop listening*, shown with always
listening; it runs the conversation hidden, without the talk window, so it
needs `--allow-ui-effects`) and `HomeListeningStatus` (the listening
indicator: *Not listening*, *Getting ready to listen…*, *Listening. Just start
talking.*, *Hearing you…*, *Martlet is replying…*, *Martlet is speaking…*,
*Paused…* or why it can't listen, such as *Set up Thinking in Companion, then
come back to talk.*, or, when the microphone fails, why and how often Martlet
tries it again, such as *Martlet can't open the microphone. Check that it is
connected and enabled. Martlet keeps trying every 10 s.*; `logs_tail` with
`contains` *Always listening* shows each failure's error code), Home's `HomeWatch` (*Start watching* / *Stop watching*,
shown while vision is on in Companion; it runs the conversation hidden and
starts screen or camera capture, so it needs `--allow-ui-effects`; Stop watching
only stops it) and `HomeWatchingStatus` (the watching indicator: *Not
watching*, *Getting ready to watch…*, *Watching your active window.* (or *your
whole screen*, a camera), *Taking a look…*, *Paused…* or why Martlet can't see,
such as *Choose a camera in Companion › Vision.*). Listening and watching are
separate: neither button starts or stops the other, and opening the talk window
or turning vision on in Companion starts neither. While Martlet listens or watches, the talk window's close
button only hides it (`ui_snapshot` stops listing *Talk with Martlet*;
`OpenLiveConversation` shows it again),
`LiveVision` (the talk window's *Start watching* / *Stop watching* button, or
*Can't see* with the reason; its value starts with the state: *Not watching*,
*Watching*, *Looking* while a look is with the model, or *Can't see*, then when
it last checked the screen; it checks every 3 s, and with the whole screen it
also says it looks right away at pop-up notifications and flashing taskbar
buttons; clicking it to start captures the screen or camera, so it needs
`--allow-ui-effects`), `LiveVisionStatus` (while
vision is on, kept short: what it sees, for example *Watching your whole
screen.* or *Watching the window behind Martlet.*, then only a look in progress
(*Taking a look…*), why it is holding off (*Waiting until you're back.*,
*Taking a break from looking.*, *The provider is busy; waiting before the next
look.*, *Martlet noticed a notification and looks once you're done talking.*)
or a look that failed; its `help` (the tooltip) says how many monitors the
whole screen spans (*Your whole screen is 2 monitors.*), the program in front
as the Thinking model is told it (*Active app: Google Chrome (full screen).*),
how the last look went
(*Last look 10:17 PM (a flashing taskbar button): nothing to say.*) and what
wanted your attention but wasn't looked at (*Noticed a notification at 10:17 PM
but didn't look: you seem away.*); whether a message went with the picture is
the note on its bubble (*Ivy saw your whole screen.*, by the persona's name); neither contains
window titles; to rehearse a flash, show any test window minimized and call
`FlashWindowEx` on it), `LiveGaze` (while Martlet decides where the character
looks and watches your screen: what its eyes are on now, *Looking at your
mouse: nothing new on screen.*, *Glancing at something new at the bottom right
of your screen.* or *Looking at the top right of your screen, where Martlet
chose to look.*, and when they last looked away; never what is on screen),
`LiveScreenSummary` (while Martlet watches and Companion › Vision › *Screen
summary over time* runs: *Screen summary: 4 pictures kept; last summary 12 s
ago (took 2.1 s).*, *…; summarizing…* or why the last one failed; its `help`
is the last summary, one or two lines on what changed on the screen),
`VisionScreenSummary` (the check box; `ui_toggle` with `--allow-ui-effects`
saves talk-preferences.json) and `VisionScreenSummaryStatus` (*On.*,
*Off.*, or *Off for now: the Thinking pool has no other model that sees.* with
what to do),
`LiveContext` (*Keeps the last N exchanges in mind.*, or *Keeps the last N
exchanges in mind; replies send the newest that fit.* once they outgrow the
context; its `help` (the tooltip) says *About T tokens of its C-token context.*
(or *About T tokens, more than fit its C-token context.*), followed by *Last
reply: P% of its N input tokens came from the model's cache.* once the Thinking
model reported its cache use: how many exchanges
of the open talk window the next reply can see, their estimated tokens and the
context size from Companion › Replies; absent when none, and unchanged when a
settings change is picked up; beside it,
`LiveRefreshContext` (*Refresh context*, a passive click, disabled mid-reply)
forgets them so the next reply starts fresh, adds the note *Context refreshed.*
to `LiveHistory` and hides `LiveContext`), `LiveTurnInputs` (once Martlet has
replied or looked: what its newest reply, report or look took together, from
[one moment](CONVERSATION.md#one-moment-everything-in-one-reply): *Last reply
took your words, 1 line this PC played and the picture.*, *Last reply took 2
lines this PC played, the picture (a notification) and 2 finished jobs,
counted as a look.*, *Last report took the picture and 1 finished job.* or
*Last look took the picture.*; counts only, never what was said, seen or
found; *... and 2 context notes* when it carried\n[context board](CONVERSATION.md#context-board) notes; the desktop log has the\nsame as *Turn took: ...* lines),
`LiveTasks` (the header's background
tasks chip, shown once Martlet starts a task in the conversation: its name reads
*Background tasks: 2 running*, *1 running · 1 ready*, *1 ready* or *3 done*; a
passive click that only opens and closes the task list `LiveTasksPanel` over the
conversation, which `LiveTasksClose`, Esc or a click in the conversation also
close), and in that list `LiveJobs` (*Martlet keeps working on these while you
talk. Stop (Esc) doesn't end them.*, *Finished work comes up as soon as Martlet
is free.* or *... when you talk next.*; never what a task is about), each task's
card `LiveTask-<id>` with `LiveJob-<id>` (what the task is about, so snapshots
don't return it), `LiveJobState-<id>` (*Checking it fits beside Thinking.*,
*Done after 1:02. Martlet brought it up.*, *You stopped it.*, *Couldn't finish:
it failed on this PC.*), `LiveJobResultToggle-<id>` (*Show result*, a passive
click that shows `LiveJobResult-<id>`, which isn't a readable value) and
`LiveJobCancel-<id>` (a passive click: it only stops that task, and the next
thing you say tells Martlet). Setting `MARTLET_BACKGROUND_FIXTURE=1` before
Martlet starts makes opening the talk window start one *FIXTURE - NOT AI* task
(`fixture-1`) that works until canceled, so these can be checked without a
model. Then the song panel
`LiveSongPanel` (shown while Martlet sings or has a song to offer; it has no Play
button, since only Martlet performs songs): `LiveSong`
(*Singing 3fa2c19b0d71 · 0:22 of 1:00 · verse line 4 of 12.*, *Starting
3fa2c19b0d71 line 4 where you stopped: a bar of the band first · 0:08 of 0:40.*,
*Stopping 3fa2c19b0d71 at 0:10: finishing the word, then the band rings out on the
beat.*, *Stopped 3fa2c19b0d71 at 0:17 (verse 2 line 7 of 10): you pressed Stop
singing. Ask Martlet to pick up where it left off.* or *Song 3fa2c19b0d71 is ready
(0:40, 10 lines). Martlet will offer it.*; never the title or words, which
`LiveSongLine` holds, word by word as they are sung) and `LiveSongStop` (*Stop
singing*, a passive click: it only ends the song musically), with the status
line reading *Starting to think it over in the background…* while
`think_longer` runs, *Starting a song in the background…* while `sing_song`
starts a song job, and *Martlet is bringing up what it worked on…* while
Martlet's own report is on its way; Companion › **Deep thinking**'s
`DeepThinkingNow` (*Thinks on diva (gemma4:27b), in parallel with the
conversation.*, *On, but it can't think on the Thinking model (gemma4:e4b), so
Martlet doesn't offer to think things over. Choose another place below.*, or
*Off. Martlet answers everything right away and never thinks in the
background.*) and `DeepThinkingParallel` (why: *Thinking's model (gemma4:e4b)
runs on this PC and can't think something over while it answers you. ...*,
*It runs as a second model beside Thinking's gemma4:e4b on this PC, ...*, *diva
does none of the conversation's jobs, so a think runs there alongside the
conversation.*), `ThinkLongerStatus`
(*On. When a task needs it, Martlet says it'll think it over and works on it in
the background (Medium effort, no time limit, no limit on how many) while you keep
talking, then brings it up as soon as it's free.*, *Off. ...*, or what keeps it
from working: no Thinking, a paired host's model, a model that turned tools
down, nowhere to think in parallel) and the choices `ThinkLongerEffort` and
`ThinkLongerDelivery` (returned; a think has no time limit or hourly limit, so
there is no choice for either;
`ui_select` on them saves the reply settings, so it needs
`--allow-ui-effects`); *Web research*'s `WebResearchStatus` (*Off. Martlet
never searches the web or reads web pages.*, *Off, because Deep thinking is
off. ...*, *On. When you ask, Martlet looks it up (up to 12 minutes, at most 4 an
hour), then offers the report.* or *On, but Martlet can't look things up yet:
...*), its fixed `WebResearchDisclosure` and the `WebResearchOn` check box
(`checkedState`; `ui_toggle` saves the reply settings, so it needs
`--allow-ui-effects`); *Where it thinks* with the passive options
`DeepPlace-Off`, `DeepPlace-Same`, `DeepPlace-Computer`, `DeepPlace-ThisPc` and
`DeepPlace-Cloud` (each only shows its card): `DeepThinkingSameStatus` (what
Same as Thinking thinks with, or why Thinking's own model can't think here),
each paired computer's
`DeepThinkingHost-<host ID>` (*diva: Its Deep thinking role runs qwen3-8b.*,
*diva: Ollama runs gemma4:27b. Add the Deep thinking role ...*, *Thinks here
(...)*, *Its Ollama (...) does Thinking for the conversation. Add the Deep
thinking role there ...*; on Companion › Thinking pool also *In the pool,
offline now: its 2 slots come back when it answers again.*, *... Kept out of
the pool, because you unticked it. Tick In the Thinking pool to add it again.*
and *... It joins the pool by itself at its next check.*) and, when Deep thinking there would share one
graphics card with the computer's Thinking model,
`DeepThinkingShare-<host ID>` (*diva: diva already runs a Thinking model
(gemma4:e4b) on its only graphics card. ... We recommend one graphics card for
each Thinking model ...*), and, for a reachable computer without the role, its
`DeepThinkingAddRole-<host ID>` button (returned: *Add Deep thinking on diva*;
clicking it first asks `DeepThinkingShareQuestion` when the card is shared, then
installs the role in a run window and then thinks there, so it
needs `--allow-ui-effects`) or, for one with the role, its
`DeepThinkingChangeModel-<host ID>` button (returned: *Change the Deep thinking
model on diva (now gemma4:e4b)*; clicking it opens the role's settings there
with that model selected, so it needs `--allow-ui-effects`), or `DeepThinkingHosts` when none is
paired, `DeepThinkingLocalStatus` (what Ollama on this PC has downloaded),
`DeepThinkingLocalFit` (whether the model in `DeepThinkingLocalModel` fits
beside Thinking's on the graphics card, read from Ollama's `/api/ps` and
`/api/tags` and the NVIDIA driver without loading anything: *Fits: gemma4:e2b
(about 5.3 GB) fits beside ...*, *Doesn't fit: ... Choose a smaller model.*,
*... is Thinking's own model ...*, or *Thinking runs elsewhere, so ... has
Ollama on this PC to itself ...*), `DeepThinkingLocalShare` (shown when Thinking
uses Ollama on this PC and this PC has fewer than two graphics cards: *This PC
already runs a Thinking model (...) on its only graphics card. ... We recommend
one graphics card for each Thinking model ...*) and `DeepThinkingKeyStatus` (what the key
field will do; never a key or typed base URL). `DeepThinkingTurnOff` (Off;
saves the reply settings), `DeepThinkingUseSame`, `DeepThinkingUseHost-<host
ID>` (checks that computer and saves its Deep thinking role's route, else its
Ollama route), `DeepThinkingUseLocal`
(refuses Thinking's own model, and asks `DeepThinkingShareQuestion` first when
`DeepThinkingLocalShare` shows) and `DeepThinkingSaveCloud` (with
`DeepThinkingProvider`, `DeepThinkingBaseUrl`, `DeepThinkingModel`,
`DeepThinkingKey` and `DeepThinkingConsent`) save `deep-thinking.json` (and turn
Deep thinking back on when it was off) and need `--allow-ui-effects`; an open
conversation's next reply and think use it.
Companion › Replies' `RepliesOpenDeepThinking` (passive) opens the page
and Companion › Lip-sync's `LipSyncNow` and `LipSyncNowProblem` (whether this
PC's own Audio2Face service answers). Lip-sync's places are *This PC* and
*Another of your computers*; under *This PC*, `LipSyncDockerTitle` (*Audio2Face,
with Docker*) and `LipSyncLoudnessTitle` (*Voice loudness, no setup*) read each
way's title with *in use*, *recommended for this PC* or *chosen, not installed
yet*; `LipSyncDockerAbout` says what Audio2Face with Docker needs (NVIDIA's
open-source engine, no NVIDIA account or key; the NIM engine needs an NGC key);
and `LipSyncOwnTitle` the advanced *Your own Audio2Face service* (with
*in use*, *not running* or *checking* while it is the setting in effect,
Martlet's default) with `LipSyncOwnState` (what it does now). Voice loudness
reads *in use* while the default's own service doesn't answer.
`SetupLipSyncLoudness`, `SetupLipSyncOwnService` and the Audio2Face buttons
change lip-sync and need `--allow-ui-effects`.
When Thinking runs in Ollama on this PC, the talk window has Ollama load the
model as it opens (and again on activity after a few quiet minutes), and
`LiveStatus` says *Ollama is loading <model> on this PC (N s)…* while it loads,
or why it can't (Ollama not running, model not downloaded, Ollama's own error);
the desktop log records each load's duration
(`{"name":"logs_tail","arguments":{"contains":"Ollama on this PC"}}`).
When a load fails because a Gemma 4 model's speculative-decoding draft model
doesn't fit in graphics memory (*Gemma4Assistant requires ctx_other ... error
loading model: vector*), the talk window and *Test model* save
`draft_num_predict 0` on that model in Ollama (`/api/create`, same tag) and load
it again; the log records *Turned off the speculative-decoding draft model*
and the test run's output says so.
The talk window is modeless: `ui_snapshot`'s `windowStates` lists each window
with `enabled` (false while a modal dialog such as Audio setup blocks it), and
the main window stays enabled while the talk window is open. Opening the talk
window never opens the microphone or looks at the screen; `LiveMic` does the
first with always listening on, `LiveVision` the second with vision on. For
verification, save a fixed microphone that does not exist in the disposable
data directory, so listening starts after `LiveMic`,
fails without capturing real audio and shows *Mic unavailable* while it keeps
retrying (it never stops by itself). The talk window's `LiveStop` (Stop, Esc)
is a passive click: it only stops a reply, recording, vision or a song (with a
quick fade). Changing How
you talk on Companion › Listening (`TalkModePushToTalk`, `TalkModeAlways`)
applies to an open talk window at once (`LivePtt` replaces `LiveMic`). With
always listening, the same card has `TalkWordCheck` (*Word check*: *Relaxed*,
*Normal (recommended)* or *Sensitive*; returned as the chosen option, and
`ui_select` on it needs `--allow-ui-effects` because it saves
`talk-preferences.json`; an open talk window restarts listening with it) and
`TalkWordCheckAbout` (returned: that Martlet ignores sounds that aren't words
and words speech-to-text makes up from noise, what Relaxed and Sensitive change,
that short answers and Martlet's name always count and that ignored sounds show
faded in the talk window; `utterance_filter_check` runs the filter itself),
then `TalkJudgeTurns` (*Judge when I finish talking (recommended)*, on by
default; its `checkedState` is the saved choice, and `ui_toggle` on it needs
`--allow-ui-effects` because it saves `talk-preferences.json`; an open talk
window restarts listening with it) and `TalkJudgeTurnsStatus` (returned, never
words or audio: *Off. The pause above alone decides when you finished
talking.*, *On, but the judge can't run here (...)*, *On. Smart Turn can't run here (...), so a
Thinking-pool model judges instead. ...*, or *On. Smart Turn v3.2 on
this PC (loaded in 1519 ms). Last 4 pauses: 2 finished, 1 unfinished, 1 left
to the pause; judge median 30 ms. Last: complete after 280 ms of silence.*,
updated after each decision; the desktop log has one *End of turn: ...* line
per decision, and `turn_judge_check` runs the judge headless),
then `TalkEarlyReplies` (*Start replies early (recommended)*, on by default),
with it on `TalkEarlyRepliesCloud` (*Also for cloud models (may add a small
cost)*, off by default) and `TalkEarlyVoice` (*Prepare the voice early too*,
on by default); their `checkedState` is the saved choice, and `ui_toggle` on
any of them needs `--allow-ui-effects` because it saves
`talk-preferences.json` (an open talk window restarts listening with it).
`TalkEarlyRepliesStatus` (returned, never words or audio: *Off. Martlet starts
each reply once you finished talking.*, *On, but replies start early only with
Parakeet on this PC as Listening.*, *On, but not with this setup: Thinking is a
cloud model; turn on Also for cloud models ...*, or *On. Thinking runs on your
own computer; the first spoken words are prepared early too. Last 3: 1 taken as
the reply, 1 let go because you went on talking, 1 let go for another reason.
Last: changed after 900 ms.*, updated after each reply started early; the
desktop log has its *Early reply: ...* lines, and `early_reply_check`
rehearses it headless) and `TalkEarlyRepliesAbout` (returned: what it does,
that nothing shows or is said before your turn ends, that it needs Parakeet on
this PC and why cloud models need the second box),
then `TalkBargeIn` (*Let me interrupt Martlet by
talking*, optional and off by default; its `checkedState` is the saved choice, and
`ui_toggle` on it needs `--allow-ui-effects` because it saves
`talk-preferences.json`) and `TalkBargeInAbout` (returned: that it is optional
and off by default, that Martlet keeps listening while it speaks either way
(with echo reduction on) and answers what was said after the reply, and what talking over
Martlet takes: real words, a word like "stop" or "wait" right away, never a hum,
a cough, laughter, a quick "yeah" or what this PC plays, checked while you talk
with Parakeet on this PC and otherwise once you pause; `utterance_filter_check`
rehearses it with Parakeet and `echo_check`'s `talkOver` the voice gate), then
`TalkBargeInBehavior` (*When you talk over Martlet*: *Pause and decide
(recommended)*, the default, or *Stop at once*; choosing one with `ui_select`
saves `talk-preferences.json`, so it needs `--allow-ui-effects`) and
`TalkBargeInBehaviorAbout` (returned: that a clear word or Martlet's name still
stops at once, other words pause Martlet at once and it decides, words for it
stop the reply, a backchannel, agreeing, laughing, side talk or a TV leave it
playing on from where it paused, and talking on stops it; `barge_in_check`
returns the verdicts). In the
talk window, `LiveBargeIn` (shown once you talked over Martlet with barge-in on)
says what happened the last time, never the words: *Talked over at 14:02:11:
paused 430 ms, then resumed (not for Martlet: agreeing or laughing along; rules
judge, 1 ms).* or *... stopped at once (for Martlet: a stop word; a clear
cue).* In the
talk window, what always listening ignored shows in `LiveHistory` as a faded
note (*Ignored "Mmm" (not words).*), and the desktop log (`logs_tail`) has
*Always listening ignored what it heard: ...*, *Always listening heard you
while Martlet spoke; ...* (said while a reply played, without barge-in), *Barge-in: Martlet paused its
reply N ms after you started talking over it (...); the rules judge decides
...*, *Barge-in: Martlet resumed its reply after a N ms pause: what you said
wasn't for it (...)* and *Barge-in: Martlet stopped its
reply N ms after you started talking over it (...; paused N ms first, then the
rules judge (N ms) said it was for Martlet; ...)*, never the words. Each
message in `LiveHistory` has an automation ID for whose it is, never its words:
`LiveMessage-You`, `LiveMessage-Martlet`, `LiveMessage-Note` or
`LiveMessage-PcAudio`; so `ui_snapshot` shows, for example, that something
that went straight to Thinking and speech-to-text couldn't transcribe left no
`LiveMessage-You` bubble (the log says *Background transcript: speech-to-text
couldn't transcribe what went straight to Thinking ...*). The talk window calls
the character by the name of the persona Martlet uses (Companion ›
Personality): its title, header, message box and empty conversation, each
reply's label (*Ivy · 10:39 PM*, *Ivy, about your whole screen*) and the notes
in its history (*You touched Ivy (touch: ...)*, *Ivy stayed quiet.*). MCP never
returns that name: the window's accessible name stays *Talk with Martlet*, the
history's words and labels are not values, and the status lines MCP reads
(`LiveStatus`, `HomeListeningStatus`, `TouchZonesNoticed`, `TouchZonesNoticedLast`)
keep saying Martlet; `character_physical_check` returns the touch note with
the data directory's persona name. Below it, the *Speakers and echo* card has
`TalkReduceEcho` (*Reduce echo from my speakers*, on by default; its
`checkedState` is the saved choice and `ui_toggle` needs `--allow-ui-effects`)
and `TalkReduceEchoStatus` (returned: *On. Martlet removes what this PC plays
from the microphone whenever it listens.*, how the last listen went, why echo
reduction couldn't run, or *Off. ...*); `echo_check` reads the same saved
choice. The *Watch along* card under it has `TalkHearPc` (*Hear what this PC
plays*, off by default; `checkedState` is the saved choice and `ui_toggle`
needs `--allow-ui-effects` because it saves `talk-preferences.json`) and
`TalkHearPcStatus` (returned: *Off. Martlet hears only your microphone.*, *On.
While Martlet listens it also hears what this PC plays, without its own
voice.*, *On. <another output> is in use too (a virtual cable there can carry
your own voice), so Martlet hears only what plays on <your output> and stops
hearing it while it speaks.*, or why it doesn't apply: push-to-talk, echo
reduction off, or Martlet's voice can't be left out; the card reads which
outputs are in use, never their sound); `pc_audio_check` reads the same choice.
Under it, `TalkDescribePcSounds` (*Describe PC sounds*, on by default, enabled
only while *Hear what this PC plays* is on; `ui_toggle` needs
`--allow-ui-effects` because it saves `talk-preferences.json`) and
`TalkDescribePcSoundsStatus` (returned: *Works while Hear what this PC plays is
on.*, *Off. Only the words this PC plays reach Thinking.*, or *On.* with the
active judge, a Thinking pool model that hears or the CPU sound tagger, whether
it describes now, and *Last: "<line>" (<age> ago, <judge>, <ms> ms).* or *No
line yet.*); `sound_digest_check` reads the same choice.
The card also has `TalkPcChattiness`, the same choice as Vision's
`VisionChattiness` (*Quiet*, *Normal*, *Chatty* or *Martlet decides*; returned,
and `ui_select` saves `talk-preferences.json`, so it needs
`--allow-ui-effects`), each with its `...Status` line (`TalkPcChattinessStatus`,
`VisionChattinessStatus`: what the level means, or what Martlet decides means
and, while a conversation runs, *Right now it is quiet.*); `chattiness_status`
reads the same choice. While Martlet decides and vision or hearing the PC is
on, the talk window's `LiveChattiness` line (returned) says *Chattiness:
normal (Martlet decides).*, then *Chattiness: quiet (Martlet decides).* once a
reply switched it (its `help` says *Martlet picks how chatty it is about what
it sees and hears and switched to quiet at 10:14 PM.*), and `LiveHistory` gets
a note such as *Martlet went quiet about what it sees and hears.* With it on
and always listening chosen, the talk window's `LivePcAudio` line (returned)
says *Also hears this PC once you start listening.*, *Also hearing this PC.*,
*Hearing this PC play something…* or why it can't hear the PC; its `help` says
how: *Martlet hears everything this PC plays except its own voice.* or
*Martlet hears what plays on <your output>, paused while it speaks.*, then what
you seem to be doing on the PC (*Now: playing a game (Elden Ring), full screen;
in a voice chat in Discord.*, from `PcActivityMonitor`), *The microphone also
heard this PC's speakers; Martlet left out N line(s) of it.* once the
microphone repeated what a video, show, game or music played, and *This PC
plays your voice back too; Martlet left out N line(s) of it.* once
a line the PC played repeated what you said; what the PC played shows in
`LiveHistory` as *Playing on this PC* bubbles, named after where it came from
when Martlet can tell (*Playing on this PC: a YouTube video in Chrome*). Pressing `LiveMic` with it on
records what the PC plays, so leave it off (or don't start listening) when
verifying on a desktop whose sound must not be captured. Each reply writes a
*Reply latency* line to the desktop log (see [Latency](#latency)), which
`logs_tail` returns and `latency_report` summarizes.

At the end of Companion › Discord, *Martlet in your Discord calls* (see
[DISCORD.md](DISCORD.md#martlet-in-your-own-calls)) has `DiscordCallOn` (the
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
while Companion › Pictures has a place or `MARTLET_PICTURES_FIXTURE=1`, it
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
`DiscordCallStatus` (*Off. Martlet isn't in your Discord calls.* or *On.
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
...*.

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

**Notification area.** Closing the main window keeps Martlet running in the
notification area by default, and Martlet started with `--tray` (Start with
Windows) shows no window, so `ui_connect` also attaches when only the icon's
window exists (it returns `inTray`). `ui_tray` drives the icon:
`{"name":"ui_tray"}` (or `"action":"status"`) returns `running`, `trayIcon`
(the icon is in the notification area), `mainWindowVisible`, `inTray`,
`menuOpen` (the icon's menu is open) and `menuBounds` (the open menu's
`[x, y, width, height]` in physical screen pixels, null when closed);
`"action":"open"` and `"action":"menu"` post the icon what Explorer
sends for a left click (show Martlet) and a right click (its menu), at the
mouse pointer or at optional `x`, `y`, in physical screen pixels as Explorer
reports them whatever Martlet's display scale, and let Martlet take the
foreground as Explorer does when the MCP server may itself, so they need no
flag. The menu opens beside that point (its corner on it, flipped to stay on
the screen) even when Martlet's display scale differs from the monitor's (the
scale changed after Martlet started, or a monitor with another scale): to
check that here, launch the desktop with `__COMPAT_LAYER=DPIUNAWARE` set on a
display scaled above 100% and compare `menuBounds` with `x`, `y`.
`"action":"close"` presses the main window's
close button, which hides Martlet or (with *Keep running when closed* off) exits
it, so it needs `--allow-ui-effects`. While the menu is open `ui_snapshot` lists
`TrayMenu` and its items: `TrayStatus` (status text: *Martlet is running*,
*Martlet is listening*, *Martlet is paused*, *Martlet is watching* or *Martlet:
talk window open*), `TrayOpen`, `TrayTalk` (*Talk to Martlet*, or *Show the
talk window* while it is open), and while the talk window is open `TrayPause` or
`TrayResume` and `TrayEndTalk`, then `TrayCharacter`, the checkable
`TrayCharacterClickThrough` (while the character shows or click-through is on;
see the character overlay above), the checkable
`TrayCloseToTray` and `TrayStartWithWindows` (their `checkedState` is the
current choice) and `TrayExit`. The menu, like text boxes' Cut/Copy/Paste
menus, is drawn in Martlet's palette (Themes\Controls.xaml), with no light icon
column in the dark palettes; `ui_snapshot` returns the palette as `AppearanceTheme`
(*Pink light*, *Rose dark*, *Character light* or *Character dark*; choosing one with
`ui_select` saves `appearance.txt`, so it needs `--allow-ui-effects`) and
Settings' line about it as `AppearanceStatus`. Settings › Appearance also has
`AppearanceCharacterStatus` (the character's colors: how many and where the
accent comes from, or why they couldn't be read; never its name),
`AppearanceColor-<n>` (each main color: *#2B3440 31% dark grayish blue*),
and `AppearancePreview-rules-<light|dark>` (each character palette's colors by
role). The
desktop log records *Read N colors from the character's textures.* and
*Applied the Character dark palette (#D194AE accent on #161E24).*
`character_theme` makes the same colors and palettes headlessly.
On a Martlet host (Settings › *Use as a Martlet
host*) the menu has no `TrayTalk`, `TrayStartListening`, `TrayStartWatching` or `TrayCharacter`: a
host doesn't talk, listen, watch or show the character. With always listening the menu
has `TrayStartListening` (*Start listening*) or, once started, `TrayStopListening`
(*Stop listening*); with vision on in Companion it also has `TrayStartWatching`
(*Start watching*) or, once started, `TrayStopWatching` (*Stop watching*), each
working without the other. `TrayOpen`, `TrayTalk` (like
`OpenLiveConversation`), `TrayPause` (it only stops work), `TrayStopListening`,
`TrayStopWatching` (they only stop listening or watching) and `TrayEndTalk`
(like `CloseLive`) are passive clicks; `TrayStartListening`, `TrayStartWatching`,
`TrayResume`, `TrayCharacter`, `TrayCharacterClickThrough`, the two
choices and `TrayExit` need `--allow-ui-effects`. The menu's status line reads
*Martlet is listening and watching* when both run. While another Martlet dialog
(Setup, Companion...) is open, `TrayTalk` and `TrayCharacter` are disabled and
`TrayExit` shows Martlet instead of exiting (the status line names the open
window to close first). Settings › *Startup and closing* has
`CloseToTray` (checked by default; saves `background.json`), `StartWithWindows`
(the per-user Run entry `Martlet`: this executable, the same `--data-directory`
and `--tray` when `StartInTray` is checked; verify with a disposable data
directory and turn it off again afterwards), `StartInTray`, `StartCompanion`
(*When Martlet starts, show the character and start listening (and watching,
while vision is on)*; saves
`background.json` and applies on every start, `--tray` included, so the
desktop log records *Martlet started with the character and listening.*, or
*... and listening, and watching.* with vision on) and
`BackgroundStatus`
(status text: what closing does, and whether Windows starts Martlet, including
when Windows' own Startup apps switch turned it off). On a PC that runs its own
host service, `StayAwakeStatus` (hidden on other PCs) says whether Martlet keeps
the PC awake: while that host service answers and serves another computer (the
computers paired with it, as its network sync reports them every 20 seconds,
window shown or not), Martlet holds a Windows power request
(`PowerRequestSystemRequired`, reason *Martlet: this PC's host service <id>
serves <names>*, listed by an administrator's `powercfg /requests`) so idle sleep
doesn't take the host off the network; the screen can still turn off and Sleep
or shutting down by hand still work. A host service that misses a check keeps
the request for 2 minutes. A Martlet host PC holds it whenever Martlet runs,
from the moment it starts and whether its host service serves anyone or not
(reason *Martlet: this PC is a Martlet host (<id>)*). The desktop log records
*This PC stays awake while its host service <id> serves <names>...*, *This PC
stays awake because it is a Martlet host...* and *This PC can sleep again when
it is left idle...* on each change. A host PC whose host service serves nobody
(a disposable data directory holding `device-role.txt` with `Host`, for
example) reads *This PC stays awake because it is a Martlet host...*; a
companion PC whose own host service serves nobody reads *This PC can sleep when
it is left idle: its host service serves none of your other computers right
now...*. On a Martlet host
`StartCompanion` is disabled but keeps its saved state, `BackgroundStatus` says
the character and listening don't start there, and the log records *Martlet
started as a Martlet host: the character and listening stay off on this PC*
instead (the character's *Show at startup* and Parakeet's warm-up are skipped
too). Choosing `UseAsHost` (Settings › *What this PC is for*; it saves
`device-role.txt`, so it needs `--allow-ui-effects`) ends a running
conversation and hides the character, logging *This PC became a Martlet host,
so Martlet ended the conversation...*. `UseAsCompanion` on a host PC (or
another computer's ask that it be a companion PC again) brings back at once what
was on when it became a host: the character, always listening and watching
(*This PC is your companion PC again, so Martlet brings back what was on before
it became a host: showing the character.*); after Martlet started as a host it
does what it does at the start of a companion PC instead (the character's *Show
at startup*, `StartCompanion`). It changes no saved companion choice. `DeviceRoleSummary`
(*Companion PC* or *Host PC*) and `DeviceRoleText` return the role as text. A second start with the
same data directory shows the running Martlet and exits (with `--tray` it only
exits); a different `--data-directory` runs beside it, so disposable
verification desktops never reach your own Martlet. `-DesktopArguments '--tray'`
on `scripts\Invoke-MartletMcp.ps1` starts the disposable desktop in the
notification area.

**Exiting.** `ExitMartlet`, `TrayExit` and (with *Keep running when closed*
off) `ui_tray` `close` exit Martlet, so they need `--allow-ui-effects`. An exit
that would cut work short (backup and restore, a setup task other than a reply,
a troubleshooting report being made or waiting to be exported, a command
from another computer, a running run window, shown or hidden in Background
tasks and listed as *<run> (in Background tasks)* (for example an update,
Parakeet or cloudflared download, a host update, reconfiguring your computers or
a change another computer asked for), or *Prepare this computer*) waits
up to 1.5 seconds for quick work to finish, then shows the window and asks in
an *Exit Martlet* confirmation whose `ExitBusyQuestion` lists what Martlet is
still busy with: `ConfirmationYes` (*Exit anyway*) interrupts it,
`ConfirmationNo` (*Keep Martlet open*) keeps Martlet running and logs *Status:
Martlet stays open...*. Windows signing out never asks. While Martlet closes,
`ClosingPanel` covers the window (the rest is disabled) and `ClosingStatus`
names the step (*Stopping your tool servers...*, *Ending the conversation...*,
*Closing the character...*); the tray icon's tooltip says *Martlet is closing:
<step>*. After three seconds the window shows even from the notification area
(clicking the icon, its menu or starting Martlet again shows it at once),
`ClosingSlow` says what exiting without waiting leaves undone and
`ClosingExitNow` (or closing the window again, `ui_tray` `close`) opens *Exit
Martlet now*, whose `ExitNowQuestion` names the step: `ConfirmationYes` exits
at once (logging *Exited without waiting: Martlet was still <step>*),
`ConfirmationNo` keeps waiting. A step that fails is logged (*While exiting,
<step> didn't finish; Martlet exits anyway.*) and closing goes on. Setting
`MARTLET_SIMULATE_SLOW_EXIT` to a number of seconds (1-600) before launching
the desktop adds a last step that only waits that long (*Waiting on a simulated
slow step*), to check the closing panel and Exit now.

To drive conversations without a microphone or speakers (always listening,
replies and their latency lines), set `MARTLET_SIMULATE_MICROPHONE` to a WAV
file or a folder of WAV files (16-bit PCM mono at 16, 24 or 48 kHz, taken in
name order) and `MARTLET_SIMULATE_SPEAKERS=1` before launching the desktop
(`-Desktop` passes the environment on). FIXTURE devices, never real ones: the
microphone hears each clip once in real time, after a short lead-in, at least
`MARTLET_SIMULATE_MICROPHONE_GAP` seconds (default 8) after the previous one
ended, and silence otherwise; the speakers take replies at real-time pace and
play nothing. Echo reduction and hearing what this PC plays are off while the
microphone is simulated; since the fixture microphone hears only its clips,
never what plays, always listening goes on while Martlet speaks (a clip due
during a reply is heard then and answered after it). The desktop log says so at start (*Simulated
microphone ... FIXTURE*) and as each clip plays. With a disposable data
directory whose Thinking is Ollama on this PC, Listening Parakeet and Voice a
Windows voice, `ui_click` `HomeListen` (with `--allow-ui-effects`) runs whole
spoken turns; `latency_report`, `logs_tail`, `hearing_check`'s `lastTurn` and
`conversation_history_status` show what happened.

For broader **explicitly authorized** live UI testing, start the MCP server
with `--allow-ui-effects`. This unlocks arbitrary ID-based `ui_click` and
`ui_select`, plus `ui_set_text` (an empty `text` clears a field), `ui_toggle`,
`ui_set_range` (sets a slider to `value` within its range and returns its value
and range, such as Companion › Voice's `VoiceVolume`, 0 to 100)
and `ui_move` (moves a control that UI Automation can move, such as the
character overlay's `MoveAvatar`, by `dx`, `dy` screen pixels). It does **not** waive the
desktop's own per-action confirmations, spending/data disclosures, or Stop
controls. This opt-in can allow the LLM to approve chargeable provider calls,
audio capture/playback, credential actions and file operations by manipulating
the UI; only enable it for an attended, isolated test with the intended
permissions and budget (agent verification with a disposable data directory and
no real credentials counts; see below). Do not use a real profile or provide
this flag to an untrusted MCP client. UI Automation needs an unlocked interactive desktop and
can fail in an unattended/headless session; use Doctor's headless tools there.
An automation click is not proof of actual microphone, speaker or provider
readiness.

## Martlet for Linux and macOS

`companion_status` runs this checkout's `src/Martlet.Companion` build with
`--status` (building `Martlet.Mcp` builds it) and returns what it detected about
the computer (OS, architecture, Apple silicon vs Intel, NVIDIA, Wayland/X11), which
platform services work, the character renderer in the build, the defaults, and the
platform-catalog guardrails: every engine per job with `offered`, the catalog's
verdict and reason, plus local-model warnings. `platform` (`linux-x64`,
`linux-nvidia`, `linux-arm64`, `macos-arm64`, `macos-x64`) computes all of it for
that platform from any computer (`simulated: true`). `settingsFile` checks a
settings.json from another device against it and returns `import.refusals` (each
with the catalog's reason) and the engines kept. No audio, network or window.

```powershell
.\scripts\Invoke-MartletMcp.ps1 -Build -Calls '[{"name":"companion_status","arguments":{"platform":"macos-x64","settingsFile":"C:\\temp\\windows-settings.json"}}]'
```

On a Windows dev run the companion's window also answers `ui_connect` (process
`Martlet.Companion`, main window `MartletMainWindow`): the tabs `TalkTab`,
`SettingsTab` and `ComputerTab` are safe clicks, and `StatusLine`,
`CharacterState`, `CompanionStatus`, `Refusals`, `NotOffered`,
`ThinkingWarnings`, `KeyNote` and the engine choices are readable. On Linux or a
Mac, where this server doesn't run, use `Martlet.Companion --status` and
`Martlet.Companion --character-check` (opens only the character, prints each
renderer state as a JSON line, exits 0 once it shows and lip-sync was sent).

## Verifying changes with Martlet MCP

Every new feature or behavior change is verified on the dev machine through
this server before merge, whenever the machine can exercise it, alongside the
targeted tests (the policy is in [AGENTS.md](../AGENTS.md#validate-before-merge)
and the whole flow in [Validating changes](VALIDATION.md)).
`scripts\Invoke-MartletMcp.ps1` runs this checkout's `Martlet.Mcp`, sends a list
of tool calls in order and prints one JSON array of results; it exits 1 if any
call fails or an `until` is not met.

```powershell
# Headless: Doctor against a fresh disposable data directory
.\scripts\Invoke-MartletMcp.ps1 -Build -Calls '[{"name":"doctor_status"}]'

# UI: launch a disposable desktop, connect, navigate and poll until it shows
.\scripts\Invoke-MartletMcp.ps1 -Build -Desktop -Calls '[
  {"name":"ui_click","arguments":{"id":"TourSkip"}},
  {"name":"ui_click","arguments":{"id":"NavSettings"}},
  {"name":"ui_snapshot","until":"AutomaticUpdateCheck"}]'
```

- Each call is `{"name", "arguments"}` plus optional `waitMs` (pause after it;
  `ui_*` effects default to 300 ms) and `until` (repeat the call for up to 20
  seconds until its result text contains that string). `-Calls` also takes a
  path to a JSON file.
- `voices_status`, `voices_engine_check`, `utterance_filter_check`, `parakeet_check`, `sound_digest_check`, `straight_voice_check`, `discord_voice_check` and `turn_judge_check` calls without a `martletDirectory`
  use this checkout's Desktop build when it is built.
- Doctor, `voices_status`, `voices_naming_check`, `f5_voices`, `cluster_status`, `network_status`, `nearby_status`, `logs_tail`, `logs_timeline`, `logs_export`, `latency_report`, `virtualization_status`, `mcp_servers_status`, `api_keys_status`, `smart_home_status`, `messaging_status`, `discord_status`, `discord_check`, `terminal_status`, `terminal_check`, `think_longer_status`, `helper_jobs_status`, `thinking_pool_status`, `sense_models_status`, `work_sharing_status`, `reminders_status`, `check_ins_status`, `discord_reply_status`, `discord_reply_check`, `conversation_history_status`, `creations_status`, `songs_status`, `prompts_status`, `settings_sync_status`, `memory_sync_status`, `memory_status`, `character_status`, `hearing_check`, `model_ability_check`, `echo_check`, `pc_audio_check`, `discord_call_check`, `chattiness_status`, `discord_text_check`, `discord_companion_check`, `vision_history_check`, `active_app_check`, `utterance_filter_check`, `barge_in_check`, `parakeet_check`, `context_check`, `context_board`, `thinking_steps_check`, `character_models`, `character_profiles`, `character_actions`, `character_gaze`, `character_touch_zones`, `character_eyes`, `character_physical_check`, `character_theme`, `singing_status`, `gpu_priority_status`, `live_floor_status`, `quick_sounds_status`, `node_presence_status`, `recommended_setup_status` and `sound_digest_check` calls without a `dataDirectory` get the script's disposable data
  directory, which `-Desktop` also uses, so Doctor sees the desktop's settings
  and `logs_tail` its logs. The directory and the desktop are removed at the end.
- `-KeepDesktop` leaves the desktop running and prints its `-DesktopProcessId`
  and `-DataDirectory` for follow-up runs; stop it and delete the directory
  when done. `-DesktopArguments` adds launch flags after the data directory
  (for example `'--tray'` to start in the notification area).
- `-AllowUiEffects` passes `--allow-ui-effects` (disposable data and no real
  credentials only; it never authorizes spending, provider requests, credential
  handling, audio capture/playback or data disclosure).
- `-Build` builds `Martlet.Mcp` (and `Martlet.Desktop` with `-Desktop`) in
  `-Configuration` (default Release) with the `dotnet` that has the SDK pinned in
  `global.json` (found the same way as `scripts\Test-Martlet.ps1`, including the
  developer profile's `dotnet`).

Check the outcome the change should produce (status values, control states,
Doctor probes), not only that calls succeeded. What the machine cannot exercise
(locked desktop, missing hardware, credentials, paid services, another OS) is
reported as NOT RUN with the reason.

## Extending the server

New features extend the server in the same change, so that MCP can reach and
observe them:

- **Controls:** give every new interactive control and status field a stable,
  unique `AutomationProperties.AutomationId` (`ui_snapshot` lists controls by
  ID; duplicates fail as ambiguous).
- **Passive clicks:** add navigation, open/close, refresh and expand controls
  that start no work to `SafeClicks` in
  `src\Martlet.Mcp.Protocol\DesktopAutomation.cs` (or `SafeClickPrefixes` for a
  family of generated IDs such as `CompanionTab-` and `Node-`). Anything that sends,
  records, plays, spends, writes files or handles credentials stays behind
  `--allow-ui-effects`.
- **Status:** add read-only, non-secret status fields to `SafeValues` (or
  `SafeValuePrefixes`) so snapshots return their text (a value pattern's value,
  a text block's text, or a status button's accessible name).
  Never expose editable fields, credentials, personal data or file paths.
- **Headless capabilities:** add a tool to `Tools` and `CallAsync` in
  `src\Martlet.Mcp.Protocol\McpServer.cs` (strict input schema, bounded
  arguments, ID-based results), or a Doctor probe that `doctor_run` reaches.
  Keep tools local, read-only by default and free of network, audio and
  credential effects unless gated like `--allow-ui-effects`.
- **Docs:** update this page with the new tools, IDs and status fields.
