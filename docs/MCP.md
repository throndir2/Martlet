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

**Companion > Deep thinking > Thinking longer** (on by default) gives every reply on a
route that does function calling Martlet's own `think_longer` (`task`, the
complete instruction, and an optional `reason`) and `cancel_thinking` (optional
`id`). The call never asks first and returns at once; the task is worked out in
a background request with Thinking steps on, by Deep thinking (the Thinking
model, another of your computers, Ollama on this PC or a cloud provider, chosen
on each PC), and brought up when it's done
([Thinking longer and background work](CONVERSATION.md#thinking-longer-and-background-work)).
The Tools page's *Recent tool use* lists each call (`Martlet > think_longer:
started think-1`); the desktop log notes each start, pause and end without the
task or result (`{"name":"logs_tail","arguments":{"contains":"Background"}}`).
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

`voices_status` reads [voice recognition and Parakeet](VOICES.md) state from a data
directory (optional absolute `dataDirectory`, default the current user's): the
recognition choice (`on (default)` until it is turned off; a shared setting) and
`sharing` (the voice list travels while *Keep Martlet the same on all my
computers* is on, from `cluster-sync.txt`),
whether a Martlet folder (optional absolute `martletDirectory`, default the
installed release's `Desktop` folder; `Invoke-MartletMcp.ps1` passes this
checkout's Desktop build when it exists) includes the sherpa-onnx runtime and
the voice models (`included`: `found`, `runtime`, `voiceModels`), whether
Parakeet is downloaded, and counts from `voices.json` (voices, named, owner, with
learned names, merged, tombstones). It never returns names, voiceprints or audio and
runs no model.

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
`default` key, `defaultName`, `defaultFemale` and `defaultCute` (both always true;
the default is the first cute voice, `librivox-annie-anime`) and `cute`, the
keys of the cute, high-pitched voices listed first. From a data
directory (optional absolute `dataDirectory`, default the current user's) it reads
the shared voice list as `library` (`speaking-voices.json`; `state` `none` until a
voice is first used, added or removed or the desktop shares voices with a host,
`loaded` or `unreadable`): the number of live `voices`, `revision`, the keys of
the starter voices in it (`starters`), the count of the owner's own voices
(`own`), tombstones (`removed`) and the keys of removed starter voices
(`removedStarters`), and the voice chosen on all computers (`chosen`: a starter
key, `own` or null) with the device that chose it (`chosenBy`), and
`severalRecordings`: each voice made from several recordings, by the key the
Voices page uses (the first 16 hex digits of its ID), with how many `recordings`,
each one's length (`clipMs`), the joined `durationMs` and `sampleRate`, the
`engines` that can clone it and those that learn from each recording
(`learnsFromEach`; the others hear them joined). `list` is this PC's
recordings (the `f5-voices` store): `state` (`none`, `loaded`, `busy` while the desktop holds it,
or `unreadable`), the number of voices, the keys of starter voices in it (`starters`), the
count of the owner's own voices, whether the retired F5-TTS example clip is still
there and the applied voice (a starter key, `own`, `retired-sample` or null).
`speaking` reads `settings.json`: `state` (`none`, `loaded` or `unreadable` with
the settings rule it broke or the error type as `problem`), the
speaking route's type (for example `GatewayF5`, null without one) and the voice it
records (a starter key, `own`, `retired-sample` or null), plus `engine` (the
self-hosted voice engine whose route it records: `chatterbox`, `f5`, `xtts`, `gpt-sovits` or `dia`), `host` and
`model` for a host route. `engines` lists the voice engines
([Chatterbox Turbo](CHATTERBOX_VOICE.md), [F5-TTS](F5_VOICE.md),
[XTTS-v2](XTTS_VOICE.md), [GPT-SoVITS](GPT_SOVITS_VOICE.md), [Dia](DIA_VOICE.md); `key`, `name`,
`hostRole`, `routeId`, `path`, `model`, `weightsLicence`, `minimumGpuMemoryGb`,
`minimumReferenceMs`, `maximumReferenceMs`, `summary`, `default` (true for
Chatterbox Turbo), `supportsTags` and `tags`, each tag's `text` in the engine's
syntax, `kind` `Sound` or `Emotion` and `usage`, and `multipleReferences` (true
for XTTS-v2 and GPT-SoVITS, which learn from each of a voice's several
recordings); Chatterbox clones only
recordings longer than 5 s, GPT-SoVITS only 3,000-10,000 ms), each starter
voice adds `engines` (the engines that can clone it) and `language` (`en` or
`ja`, read from its transcript), and `chosenEngine` is the engine chosen on this
desktop (`speaking-engine.txt`, default `chatterbox`). After the desktop
loads settings, a route or applied voice that was `retired-sample` reads the
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
returns the engine, `supportsTags`, its `tags`, `cues` (each tag's
engine-independent cue, such as `laugh` for `[laugh]`), `prompt` (the *Voice
sounds and tones* instructions the Thinking model gets, or null), `spoken` (the
pieces the real speech segmenter hands that engine for a spoken reply, its own
tags kept), `suppressedPieces` and `shown` (the chat and caption text, every
tag stripped). The pieces break where the persona's [speech
breaks](CONVERSATION.md#voice-latency-streaming-overlap-and-barge-in) allow:
`dataDirectory`'s saved persona named `persona` (else the one Martlet uses,
else the defaults), with any of `breaks`' `commas`, `periods`,
`questionMarks`, `exclamationMarks` (booleans) and `shortEndingWords` (0-5)
on top; `persona` and `breaks` (with `isDefault`) say what was used.
With `characterTags` (the character's [emote and motion
tags](AVATARS.md#emotes-and-motions), such as `["{blush}"]`), those are stripped
too and `characterCues` lists the cues the character acts on: each one's
`piece` (index in `spoken`, or -1 for a tag after the last words), `tag` (a
character tag or the engine's own voice tag) and character `offset` in the
piece. It synthesizes and contacts nothing.

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
`smart-home`, `updates`, a computer's own `pc.<device ID>`, or a newer
Martlet's), `updatedBy`, `updatedAt`,
`revision`, `usesKey`, `characters`, `off` (the value is null) and `here`:
`same` when this PC had exactly that value at its last sync, `different` while
it can't follow it yet (or changed it since), `unknown` when it never had the
setting. `value` is shown only for non-personal settings: each job's route
(`type`, `origin`, `model`, `voice`), the fallback's `origin` and `model`,
memory, how you talk, speech bubbles and subtitles, the theme, recognizing
voices (`on`), what Martlet may do with Home Assistant (`control`,
`allow_sensitive`, `model_tools`) and app updates (`checks`,
`interval_minutes`, `auto_install`, `auto_update_hosts`). It never returns keys,
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
and A takes revision 2; A forgets a fact and every computer forgets it for good;
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
`changedAt`), `adopt` (hosts paired here on purpose, added to the network on
the next sync), `ignored` (network hosts forgotten here) and `removedFrom`. It
never returns keys, signatures or host addresses and contacts nothing.

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
(B pairs with it again by itself); A removes B (every host revokes it, B leaves
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
and the other desktop's copy; a stale copy can't bring it back; speaking with a
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
`kind`, `name`, `detail`, `tag`, `cue`, `use`, `enabled` and whether replies
are `offered` it for `engine`, a voice engine key, `none` or absent for a voice
without tags), `replyPrompt` and `replyTags` (what replies get while the
character shows) and `namingPrompt` (`instructions` and the numbered `list` the
Thinking model is sent). With `answer`, a simulated Thinking reply such as
`1: blush | - | when shy`, `parsed` shows what the production parser makes of
it (`read`, `problem`, `actions`, `prompt`). Model-authored names only, never
the model's path; it reads and contacts nothing else.

`character_theme` makes a character model's colors and palettes the way
Settings › Appearance does ([Character palettes](UI_DESIGN.md#character-palettes)),
with the production code: `modelPath` (a `.model3.json` or `.vrm` on this PC),
else the model `dataDirectory`'s `avatar.json` shows, else the built-in
character (only where the Live2D runtime is beside the server). It returns
`renderer`, `key`, `textures`, `thumbnail` (a VRM's own picture), `analyzeMs`,
`identity` (`fromFiles`: the `Name` and `Source` the model's files give, a
VRM's meta or VTube Studio's name; `told`: the `name` and `about` the Thinking
model is told, from `name` and `about` when given, which stand in for the
character list's name and the owner's words in Settings › Appearance, else the
saved words in `dataDirectory` or the files),
`swatches` (each main color's `Hex`, `share`, `Kind` and `Name`), `sources`
(`tint` and `tintStrength`, the `accents` candidates best first, the `light`
and `darkest` neutrals), `rules` (`light` and `dark`: `colors` by role,
`problems` and the `lowestContrast` of each role), `request` (the Thinking
`instructions`, `message`, `picture` kind and `pictureSize`) and `saved` (the
colors and Thinking palettes in `dataDirectory`'s `character-themes.json`).
With `answer` (a simulated Thinking reply: the usual choice
`{"accent","glow","tint","strength","why"}`, or whole `light` and `dark`
palettes), `parsed` is what the production parser makes of it (`Choice`, the
`light` and `dark` palettes the rules built around it, `Why`, `Fixes`). With
`live: true` it asks Ollama on this PC (`model`, or the saved local Thinking
model) with the real instructions, message and picture over loopback (Thinking
steps Off) and returns `live` (`Outcome`, `FirstWordsMs`, `TotalMs`, `reply`,
`read`, `problem`, `theme`); on this PC Gemma 4 12B answered the choice in
about 2-3 s once loaded (much longer while a game or another GPU job ran). With
`previewDirectory` (an absolute folder) it writes `<label>-rules-light.png`,
`-rules-dark.png`, `-thinking-light.png` and `-thinking-dark.png` (Martlet's
window drawn with the real styles in each palette; the Thinking ones from
`answer`, `live` or the saved palettes) and `<label>-picture.jpg` (what the
Thinking model is sent); `label` defaults to the key. It never returns the
model's path, writes only to `previewDirectory` and contacts only Ollama on
this PC with `live`.

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
cancel works (waiting and running), a command sent while another runs waits
behind it and the sender's `HostCommandList.WaitingText` names what it waits
for, an update that continues later (FIXTURE: "Martlet is in use here") stays
first and holds the queue until it finishes and the waiting command runs right
after it, commands survive a restart with a new token
and the queue is bounded. It runs `src\Martlet.NodeLinkCheck` (built with
`Martlet.Mcp`) as its own process, because the gateway needs the ASP.NET Core
runtime; it takes no arguments and contacts nothing outside loopback. The same
program's `live <pairing-code> <container>` mode checks a disposable Linux
gateway container built from this checkout (not the real host service).

`host_engine_check` (no arguments) checks that a host makes
[one change at a time](../deploy/host/README.md#one-change-at-a-time) with this
checkout's real `deploy\host\martlet-host`: it starts one disposable
`ubuntu:24.04` container (`--network none`, `--pull never`, removed afterwards,
the engine in native mode against a fixture setup under `/tmp`; Martlet's own
host containers and volumes are never touched) and returns `{exitCode, report:
{passed, total, image, engine, steps: [{name, ok, detail}]}}`. Steps: `flock`
is present; a change (a `network-reset` waiting for a typed yes, like a console
left open) holds `engine.lock` (0600) and records itself in `engine.holder`;
`roles` still runs; `status` says `Busy now: ...`; an automatic `update` (no
terminal, no `--yes`) stops at once with exit 75 and `MARTLET-BUSY ...`; a
`--yes update` with `MARTLET_LOCK_WAIT=3` waits, says what it waits for and
gives up with 75; a waiting `--yes remove` continues once the holder is killed
(SIGKILL), and so does an `update` without a terminal or `--yes` that sets
`MARTLET_LOCK_WAIT=60` (*Update hosts now*): it waits instead of stopping and
then runs; the next automatic run is not blocked (no stale lock);
`logs/engine.log` records the waits; and the desktop's reader
(`HostEngineBusy.Read`) reads the engine's real busy line. It then checks the
Docker method's launcher and engine against a fake `docker` CLI (state in
`/tmp/fake`): an automatic `setup` while an `add` engine session runs in the
network holder's namespace stops with exit 75 and `MARTLET-BUSY installing
chatterbox (...)` without replacing `martlet-host-net`; a `--yes setup` waits
for it, then removes and recreates the holder and runs its engine; an engine
left in a replaced holder's namespace stops at once (`... was replaced while
this ran ... Nothing was changed`) while one in the current namespace
continues; and the desktop's reader reads that busy line. Without Docker or the
image it returns `exitCode` 2 and `notRun` (it never pulls). It does not cover
a real Docker daemon or a real host.

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
run's 45-minute limit (`asked-update-waits`). It contacts nothing and touches no
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
Martlet's process exited (`waits-for-martlet`); its switches
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
character and starts listening again: it is read once
(`picks-up-character-and-listening-once`), carries only what was on
(`only-what-was-on`), leaves nothing when nothing was on
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
window. Martlet closes and restarts into it[, showing the character and
listening again].*, and the restarted Martlet logs *Martlet restarted after its
update and is showing the character [and listening] again, as before the
update.* To exercise this without GitHub or a real install, set
`MARTLET_SIMULATE_APP_UPDATE` to a version newer than the build (for example
`9.9.9`) before launching the desktop (FIXTURE): checks find that version
without contacting GitHub, its download is a small text file, and installing it
runs the real update helper, whose stand-in installer fails at once (Windows
can't run it), so nothing is installed; the helper records the failure and
starts Martlet again (`--after-update`, same data directory), which reports
*The update to 9.9.9 didn't finish* and doesn't install it automatically again
that session. Turning on `AutomaticUpdateInstall` (`ui_toggle`) saves
`updates.json`, so it needs `--allow-ui-effects`.

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
Arguments: `engine` (`chatterbox` default, `f5`, `xtts`, `gpt-sovits` or
`dia`), a numeric loopback `endpoint` (default the role's port: 50083, 50080,
50081, 50082 or 50084) and optional `text` (at most 300 characters; default a
sentence with the engine's first sound tag, such as `[laugh]`, when it has
tags). It returns `{exitCode, report}` with `ok` (no failure, at least 0.5 s
of audible audio), `engine`, `route`, `voice`, `text`, `statusBefore` and
`statusAfter` (the service's own `/status`: `answered`, `state`, `ready`,
`error` and `runtime`, for Chatterbox its torch, torchaudio and CUDA versions,
or why it could not be read), `seconds` of 24 kHz audio, `firstAudioMs`,
`elapsedMs`, `realTimeFactor`, `peakDbfs`, `rmsDbfs`, `audible`, and `failure`
and `problem` (the client's error code and message, for example
`worker.unavailable` when nothing answers or the model could not load). A
loading model can take minutes, so the tool allows six; pass
`-TimeoutSeconds 400` to the script. As with `audio2face_check`, a role
service on a host listens only in the host's loopback, so run it there or
forward the port.

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
`dockerDesktop {installed, running, engine}` (`engine` is what
`docker desktop status` reports, for example `running`, `starting` or
`stopped`, or null when Docker Desktop doesn't answer within 15 seconds; a
run window restarts Docker Desktop once when it is open but its engine stays
`stopped` at two checks in a row, and always after Martlet changed Windows for
it) and `continueSetup {pending, kind, task,
created, startsAtSignIn}`: the setup Martlet continues after a Windows restart
(`continue-setup.json` in the data directory, and whether the per-user `RunOnce`
entry that starts Martlet at the next sign-in exists). It reads CIM facts,
Windows services and pending-restart registry markers and runs `wsl --version`
and `wsl --status` in a hidden Windows PowerShell, plus `docker desktop status`. It changes
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
computers paired with it, including this PC when it is one) and `problem`
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
`active`, `instructionCharacters`, `styles` weights and `speechBreaks`
(`commas`, `periods`, `questionMarks`, `exclamationMarks`, `shortEndingWords`
and `isDefault`), never its
instructions), `character` (from `avatar.json`: `model` `built-in` with
`builtInCharacter`, or `own model` with `ownModelType` `.vrm` or
`.model3.json`, never the path; `renderer`, `lipSync`, `autoShow` and
`lipSyncHost`, the paired host's ID), `placement` (from
`character-placement.json`, this PC only: `state` `none` when the character's
position is unlocked, or `loaded` with `locked`, `left`, `top`, `width` and
`height` in device-independent pixels; see the character overlay below) and
`lorebooks` (`books`, `on` and
`entries` counts). Those editors have no Save button; each change saves on its
own into the newest saved file, keeping what was saved elsewhere meanwhile
(another page, or sync from your other computers, such as the lip-sync host).
`OpenCompanion` (Personality's *Edit personality*), `OpenAvatar` (Character's
*Choose and customize*), `OpenLorebooks` and `OpenMemory` open their windows
(the character itself doesn't show), and their `CompanionClose`,
`AvatarClose`, `LorebookClose` and `MemoryClose` close them; these are passive
clicks, as are the character window's `AvatarAdvanced` and
`RemoteHostSection` expanders. Each editor's footer line, `CompanionSaveState`,
`AvatarSaveState` and `LorebookSaveState`, reads *All changes saved.*,
*Saving...*, *Not saved yet: <why>* (for example an empty persona name, all
response styles at zero, or *Choose your model file: an existing .vrm or
.model3.json file.*) or *Not saved: <why>*; `AvatarStatus` reads the
character's state (*Character is showing. ...*, *Character hidden.*). Their
fields (`CompanionName`, `CompanionText`, the `CompanionHelpful`... sliders,
the *Where the voice pauses* check boxes `CompanionBreakCommas`,
`CompanionBreakPeriods`, `CompanionBreakQuestions` and
`CompanionBreakExclamations` (their `checkedState` is the persona's choice) and
`CompanionShortEnding` (its value reads *Never*, *1 word* or *Up to N words*),
`CompanionPersona`, which also makes the chosen persona the one Martlet uses,
`CompanionNew`, `CompanionDuplicate`, `CompanionDelete`, `CharacterChoice`,
`AvatarModelPath`, `LipSyncChoice`, `AutoShowCharacter`, the lorebook fields,
`MemoryEnable`) save, and `ShowCharacter`/`StopAvatar` show or hide the
character, so they need `--allow-ui-effects`. Typing a persona name or text and
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
`Hears`, `Sees`, `Source` and `CheckedAt`, or null) and `hearVoice` (the saved
choice, off by default). Its `fixture`
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
change; the `TalkHearVoice` check box saves the choice, so it needs
`--allow-ui-effects`. A real reply with a recording needs a microphone and a
model that hears; the talk window then notes *Thinking heard your voice.* (or
that it got the transcript only) under what you said.

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

`spoken_reply_check` rehearses a spoken reply whose voice fails partway, end to
end with the production conversation runtime (`ConversationRuntime`, the Chat
Completions adapter, the Martlet host voice stream and the playback sink). A
fixture endpoint on 127.0.0.1 streams a canned four-sentence reply (NOT AI) a
sentence at a time, the way OpenRouter streams; a fixture Martlet host voice (a
quiet tone, NOT AI) fails on the `failAt`-th piece (1-4, default 1) it is asked
to say, as `voiceFailure`: `server` (default; the host's voice worker failed,
`worker.failed`), `unavailable` (it is reloading, `worker.unavailable`),
`stall` (no audio until the voice's time runs out, shortened to a few seconds)
or `none`; a fixture speaker opens no device and plays nothing. It returns
`reply` (`state`, `failure`, `textComplete`, `fullText`, `characters` of
`servedCharacters`, and the fixture `text`) and `voice` (`stopped`, `why` (the
turn's `SpeechFailure`), `provider` and `failedJob`, `piecesAsked`,
`piecesSpoken`, `speechLimitReached`, `speakerOpens`, `samplesPlayed`,
`mayHavePlayed`) and `captions`, what the speech bubble and subtitles were
given (`complete`, `shown`, `spoken`, `unsaid` and each line's `text`, `atMs`
and `spoken`): a line as each piece starts playing and, after the voice
failed, every sentence it couldn't say, one after another for its reading
time (2-20 s). `ok` is true when the reply completed with all of its text,
only the voice stopped, at the chosen piece with the expected provider code
(or, with `none`, every piece was spoken), and the captions together showed
the whole reply. Before the fix this reported `Partial` with only the text up
to the failed sentence, and the captions then showed nothing past the last
spoken piece. With `reply` (up to 1,024 characters of one-line text) that text
is streamed a word at a time instead, like a model's tokens, and spoken with
speech breaks chosen like `voice_tags`' (`dataDirectory`'s `persona`, else the
one Martlet uses, else the defaults; `breaks` on top); `voice.pieces` lists
exactly what the voice was asked to say, in order, with `voice.persona` and
`voice.breaks`. Use `voiceFailure` `none` to hear every piece. It reads no
credentials and nothing leaves loopback. A real
paired host's voice failing is NOT reproduced; the talk window then notes
*The voice failed, so this wasn't spoken.* or *The voice stopped partway, so
only the beginning was spoken.* under the reply.

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

### Latency

Every reply writes one *Reply latency* line to the desktop log: how long from
when you stopped talking (always listening), let go of the talk button or sent
your message to the first audio (or the first words when nothing was spoken),
then each step in parentheses, each the wait that ended there, so they add up
to the total: *end of speech*, *recording*, *Voice ID*, *speech-to-text*,
*voice recognition*, *waiting to answer*, *preparing*, *memory*, *lore*,
*tools*, *Home Assistant*, *building the request*, *Thinking authorization*,
*Thinking connection*, *Thinking before reasoning* and *hidden reasoning* (or
*Thinking first words* when no reasoning was streamed), *first sentence*,
*voice authorization*, *voice synthesis*, *playback start* and *speakers*
(steps that didn't happen are left out). It goes on with the time to the first
words and audio from the reply's start, the number of spoken pieces, how much
speech the first piece held and how long it took to make, and the model IDs
(`Models: Thinking ..., voice ..., speech-to-text ...`). What each step covers
is in [Voice latency](VOICE_LATENCY.md#measure-first-the-reply-latency-line).

`latency_report` reads those lines from a data directory's desktop log (with
its rotated copies; optional absolute `dataDirectory`, default the current
user's) for the newest `replies` (1-500, default 20) and returns `measured`
(lines with steps), `legacy` (older lines that only gave the first words and
audio from the reply's start), `firstAudio` (from the moment that counts for
you), `firstWordsFromReplyStart` and `firstAudioFromReplyStart` (each `{Count,
Median, P90, Min, Max}` in ms), `steps` (the same for every step),
`slowestSteps` (the five with the largest median) and `newest` (each reply's
`at`, `measured`, `totalMs`, `from`, `steps`, `firstWordsMs`, `firstAudioMs`,
`spokenPieces`, `firstPieceSpeechSeconds`, `firstPieceMadeMs`, `models`,
`interrupted`, `legacy`). It only reads the log: no audio, network or provider
request.

`context_check` shows the Thinking model's [context](CONVERSATION.md) as
replies use it (optional absolute `dataDirectory`, default the current user's):
`settings` (`none`, `loaded` or `unreadable`), `thinking` (`routeType`,
`localOllama`, `model`), `savedContextTokens` (Companion › Replies › Context
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
glances and *Remembering*/*Learning names*) and at the end of the talk
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

`think_longer_status` shows Companion › **Deep thinking** as replies use it
(optional absolute `dataDirectory`, default the current user's): `settings`,
`thinkLonger` (`enabled`, on by default; `effort` *Medium* or *High*; `minutes`
2, 5 or 10; `perHour` 3, 6 or 12; `delivery` *WhenFree* or *NextMessage*;
`chosen`), `thinking` (the Thinking route's `routeType`, `model`,
`supportsTools`, `toolsRejected` from `tools-unsupported.json`, `offered`,
`onThisPc`), `deepThinking` (this PC's `deep-thinking.json`: `file` *none*,
*loaded* or *unreadable*, `place` *SameAsThinking*, *Endpoint* or *Host*, `where`,
`model`, `hostId`, an endpoint's `origin`, `ownKey` and `usesThinkingKey`
(never a key), `parallel`/`waitsForQuiet` and `why` from the production
`DeepThinkingPlan`, its Thinking steps `use`, what a think `sends` at that
effort, such as `{"reasoning_effort":"medium"}` or `{"think":true}`,
`outputTokens` and `carriesTools`, true only with the Thinking model), `tools`
(`think_longer` and `cancel_thinking` exactly as the model gets them), the
filled `prompt`, and `jobs`: the desktop's `background-jobs.json` (`active` and
`recent` jobs with `id`, `kind`, `state`, `progress`, `startedAt`,
`finishedAt`, `elapsedSeconds`, `timeLimitSeconds`, `offer`,
`resultCharacters`, `cut`, `problem`, `canceledBy` and `delivery`;
`startedLastHour`; and the running think's `where`, `parallel`, `why`,
`waitsForQuiet`, `attempts` and `pauses`),
never a task or result. Read-only.

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
the conversation ending (dropped). `local`: a think for a model on this PC waits
for a quiet moment without sending anything, stops its request when the
conversation needs the model (`stoppedAfterMs`), sends nothing while paused and
then finishes from the same request (`attempts` 2, `pauses` 1,
`sameRequestAgain`). `plans`: the production `DeepThinkingPlan` for eight setups
(Same as Thinking with Thinking local or on OpenRouter; OpenRouter, or Ollama on
this PC, with Thinking local; Ollama on this PC with the voice on another
computer or in this PC's host service; a paired computer that does nothing else,
or also speaks), each `parallel` against `expected` with its `why`.
`parallel`: a think on a destination of its own (the plan says parallel; a
second fixture endpoint stands in for the other machine) keeps working while
three replies go to the conversation's endpoint (`replyFirstWordsMs`), is never
stopped (`attempts` 1, `pauses` 0) and its request has no tools, Thinking steps
on and the conversation then the task. `hostFit`: a 160-message conversation
fitted to a paired computer's gateway (16 KiB, 16 messages, no tools, the newest
kept, `inputTokens` 24,576 beside 8,192 for output). Each part has an `ok`; on
this PC the tool returned in 5 ms, a paused think's request was gone 123-140 ms
later (the fixture writes every 50 ms) and replies beside a parallel think
answered in 2 ms. Loopback only; reads no credentials.

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
`requiredMs`. It contacts nothing.

`utterance_filter_check` checks [listening for words](CONVERSATION.md#listening-for-words)
and barge-in (Companion › Listening › **Word check**; optional absolute
`dataDirectory`, default the current user's, `sensitivity` `relaxed`, `normal`
or `sensitive` to override the saved choice, `audio` default true, absolute
`speechDirectory` where Parakeet is downloaded, default the current user's,
and absolute `martletDirectory` for the sherpa-onnx runtime, which the script
fills with this checkout's Desktop build). It runs the production
`UtteranceFilter` and `BargeInPolicy` on `samples` (up to 64 of `text` with
optional `voicedMs`, `meanProbability`, `minimumProbability`,
`noSpeechProbability`, `averageLogProbability`, `engine`, `afterQuestion`,
`persona`, `playback` `reply` or `song`, and `expectKeep`/`expectInterrupt`),
by default a fixed set with the outcome Normal must give (fillers, laughter,
sound tags, "Thank you." from noise or said clearly, subtitle credits, lone
words, short answers, stop words, backchannels, too many words for the voice,
a whisper loop, Martlet's name, a song that only stops when asked, and the
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
speech-to-text path (`ParakeetEngine`, the desktop's model): "Stop!", "Wait,
hold on a second.", "Can you tell me more about that?", "Yes." (after a
question), "Yeah.", "Mmmmmm.", "Hmm." and "Ha ha ha ha!" said by a Windows
voice (System.Speech, rendered to memory, never played), plus a hum, two
coughs, a breath, typing, music and noise, each after 0.3 s and before 1 s of
faint noise. Per fixture: `voicedMs` (loud frames by the production
voice-activity detector), Parakeet's `transcript`, `evidence` and
`transcribeMs`, the filter's verdict, and `bargeIn`: the production
`BargeInGate` fed 20 ms at a time as if Martlet were speaking, each quick check
transcribing the stretch so far (no other check starts while one runs, as in
the listener) with its `quick` transcripts, probabilities and milliseconds,
`interrupted`, `reason` and `afterMs` (from the start of the voice to the
decision). `stopDelayMs` summarizes the fixtures that stopped Martlet. `ok`
needs every expectation met: words kept, non-words and noise dropped, stop
words and the question stopping Martlet, backchannels and non-words never.
Nothing is recorded or played and nothing leaves this PC; without Parakeet,
`audio.ran` is false with the reason.

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

`logs_timeline` reads this PC's logs as the desktop's
[Diagnostics page](DIAGNOSTICS.md#diagnostics-page-and-the-log-host) shows
them (optional absolute `dataDirectory`, default the current user's):
`desktop`, `avatar-renderer` and `host-runs` with their rotated copies, parsed
into one timeline, newest first, of `{at, level, component, seq, message}`
(a stack trace or output lines stay in their line's `message`). Optional
filters: `level` (`all`, `warnings`, `errors`), `component`, `contains` (at
most 200 characters) and `lines` (1-1000, default 200). It also returns
`device` (this PC's ID as a log source), `logsFolder`, `total`, `errors`,
`warnings`, per-`components` counts, `matching`, and `logHost` (the `logs`
entry of `cluster.json`, null when nobody collects logs; `plan` is `none`,
`loaded` or `unreadable`). Read-only; it contacts no host.

To drive the visible desktop, start `Martlet.Desktop.exe` yourself in the **same
interactive Windows session** (ideally with a disposable `--data-directory`).
Call `ui_connect` with that process ID. `ui_snapshot` returns window accessible names,
automation IDs, enabled states, checkbox states, and selected read-only status
fields (a text block's text, or a button's accessible name); it does not dump arbitrary editable fields or credentials.
`{"name":"ui_snapshot","arguments":{"layout":true}}` also returns each control's
screen `bounds` (`[x, y, width, height]` in pixels) and, for text controls, the
`textBounds` of their first line of text (geometry only, never the text), so
alignment can be checked: in the talk window, the empty box's hint
`LivePlaceholder` must have the same `bounds` position as the `textBounds` of
text typed into `LiveInput`.
`windowStates` lists each window's `name`, automation `id`, `enabled`, and
whether its frame is `resizable`, `minimizable` and `maximizable`, whether it is
`minimized` and whether it is the `foreground` window (has the focus); with
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
Status fields include `VisionStatus` (Companion › Vision: whether the Thinking model can see, or has been retired, and the fix), `VisionDisclosure` (Companion › Vision: exactly what vision captures and sends and where, including that what you type or say goes with the newest picture and, for the whole screen, the looks at notifications and flashing taskbar buttons), `FallbackNow` (Companion › Thinking › If Thinking fails: the saved fallback endpoint and model and whether it has its own key, uses Thinking's or none; never the key), `FallbackKeyStatus` (what the fallback's key box will do; its fields `FallbackProvider`, `FallbackBaseUrl`, `FallbackModel`, `FallbackKey`, `FallbackConsent` and its `FallbackSave`/`FallbackOff` buttons write settings or a key, so they need `--allow-ui-effects`; `logs_tail` shows each use as *Thinking failed (...) ... the Thinking fallback ... answered instead*, and a rate-limited glance shows in `LiveVisionStatus` as *the provider is limiting requests. Looking again in 1 minute.*), `RepliesNow` (Companion › Replies: that Martlet asks for replies of one or two sentences, the max reply length ceiling in effect, 4096 tokens including any hidden thinking on a Chat Completions or paired-host Ollama route unless set, whether Thinking steps are off (the default) or on, and the other saved settings), `RepliesThinking` (Companion › Replies › Thinking steps: *Off*, the default, or *On*; choosing one with `ui_select` saves it, so it needs `--allow-ui-effects`) and `RepliesThinkingStatus` (how the Thinking route takes it: *Used by Ollama on this PC.*, *Depends on the model at ...* for servers where it depends on the model, or not used on the OpenAI route), `SetupCloudHint-Thinking` (the cloud provider's recommended Thinking model, or a retired-model warning), `SetupLocalRecommendation` (the local Ollama model recommended for this PC's graphics card, leaving about 5 GB for a game and Martlet's character), `SetupOllamaStatus` (whether Ollama is installed or running and which models it has), `SetupLocalModelTest` (Thinking › This PC: the last *Test model* result for the model in the box, or that it isn't tested yet; a model that doesn't fit in the free graphics memory says so and names a smaller one), `SetupProviderHint` (Setup › Jobs prefilled model), `AppUpdateStatus` (Settings › App updates: the installed version, the check schedule and the last check or download result), `AppCurrentVersion` (Settings › App updates: always-visible *Current version: Martlet x.y.z*). On Companion › Voice › Voice engine, `VoiceEngineUse-<engine key>` under This PC asks one confirmation (what it installs, the engine it replaces and its model's licence; installing Docker Desktop still asks for its own terms) and then sets up and switches in a run window, so it needs `--allow-ui-effects`. `SetupTestLocalModel` (Thinking › This PC's *Test model*) starts Ollama if needed, loads the model in the box and sends it one short loopback chat request in a run window, so it needs `--allow-ui-effects` too; read the outcome from `HostRunStatus` and `SetupLocalModelTest`. A run window (`HostRunWindow`, titled `Martlet - <run>`) returns its status line as `HostRunStatus` (for example *Waiting for Docker Desktop to start...* or why it stopped); its output (`HostRunOutput`, which can show a one-use pairing code) is not returned, so read it with `logs_tail` `host-runs`, which also records each status change. `HostRunCancel` cancels a running run (or closes the window afterwards) and needs `--allow-ui-effects`. On a fresh data directory, a voice engine's setup first needs saved settings (*Complete Setup first.*): `VoiceEngineUse-windows` (a Windows voice) saves them. Setting `DOCKER_HOST` (for example to a local test named pipe) before launching the desktop points its Docker checks away from the real engine. `ui_click` invokes a control by automation ID and `ui_select` selects a named combo-box
option. By default only passive navigation and
diagnostics controls can be clicked. The main window is split into pages, and a
page's controls are only visible after you open it: click `NavHome`,
`NavDevices`, `NavCompanion`, `NavDiagnostics` or `NavSettings` first (for example
`NavCompanion` before `OpenSetup`). On Settings, click `DiagnosticsSection` to
expand the pipeline and status fields. On a fresh data directory, `TourSkip`
dismisses the welcome tour, and `TourBegin` and `TourBack` step through it
(Welcome › role › how to start; the tour installs nothing). Its role cards
(`TourCompanion`, `TourHost`) save the device role, so they need
`--allow-ui-effects`; `TourCompanion` leads to `TourAdvisor`/`TourSetup`, and
`TourHost` closes the tour on the host dashboard. Companion's side list items (`CompanionTab-<Page>`,
for example `CompanionTab-People`) and `OpenPeople` (on Listening) are passive
navigation too. People shows `PeopleStatus` (on, off, or that the installation
lacks the voice recognition files), `PeopleSyncStatus` and
`PeopleVoiceCount`, and Listening shows `ListenParakeetStatus`; snapshots return
these status texts, as does the talk window's `LiveStatus` (the line under "Martlet": what it is doing, or why the last reply failed, naming the job that failed: *Martlet couldn't speak. ...* for the voice, and *Your Martlet host <ID> didn't answer ...* when the job runs on a paired host). Each voice's controls are numbered by voice (`PeopleName-3`,
`PeopleOtherNames-3`, `PeopleOwner-3`, `PeopleMergeTarget-3`,
`PeopleMerge-3`, `PeopleForget-3`; there is no Save button: a name saves when
its field loses focus, on Enter or two seconds after typing stops, then syncs);
like `PeopleRecognize` (ticked by default; a shared setting),
`PeopleSync`, `PeopleForgetAll` and `SetupListenParakeet`, they
change data or download and need `--allow-ui-effects` (People has no sharing
switch of its own: the list follows `ClusterSync`). On Devices, `Node-<id>`
selects a device on the map (`Node-this-pc`, `Node-host:<host ID>`,
`Node-pc:<device ID>` for another Martlet computer that runs no host service,
`Node-cloud:<server>`, `Node-add`, `Node-missing:brain`) and
`CoverageShow-<job>` selects the device doing a job; both only show details, so
they are passive clicks, as are the `DeviceFactsSection`, `DeviceRolesSection`
and `DeviceReachSection` expanders. Each `Node-<id>` also returns the device's
card as text: its name, subtitle, status and what it runs (for example
`IMOUTO, desktop-imouto · imouto-host. Connected. Runs: Martlet companion, Martlet host, Listening`
or `DIVA, desktop-diva · diva-host. Connected. Runs: Martlet host PC, Speaking, Lip-sync`),
so one snapshot shows the whole map. `SelectedDevice` and `SelectedDeviceHealth`
return the selected device's name and status. When a paired host is older
than this PC, its status *Update available* is a button,
`SelectedDeviceHealthAction` (returned: its status and what it does, for
example *Update available: Update to Martlet 0.40.0*); clicking it runs the
same update as `NodeAction-UpdateHost`, so it needs `--allow-ui-effects`. For a
paired host, `SelectedDeviceRelease` (in *Details*) returns its Martlet release
as this PC knows it, kept current by the release every host announces on each
network sync (`0.22.0, up to date`, `Needs update from 0.21.0 to 0.22.0`), and
`SelectedDeviceUpdate` the note on what this PC last did to update it (for
example *Asked Martlet on gpu-pc to update to 0.22.0 ...*, *Waiting to update
to Martlet 0.22.0: that host is busy (...)* or *Another update of that host was
already running (...)*, then *Updated to Martlet 0.22.0 (seen at 9:41 PM).* once
the host announces it, a check finds it current or another route of this
Martlet updated it). Each row title
`DeviceComponent-<part>` (`job-Llm`, `job-Stt`, `job-Tts`, `lipsync`,
`character`, `audio`, `host-service`, `host`, `users`, `member`, `role-<role>`, `offer`)
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
(`NodeAction-InstallRole-<role>` and `NodeAction-RemoveRole-<role>` for host
roles), and Settings for all devices holds `CheckHosts`, `ClusterSync` (checked by
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
The **Your Martlet network** card ([NETWORK](NETWORK.md)) holds `NetworkStatus`
(status text: member with how many computers and hosts, waiting to join with
the check number, a host PC in no network that only watches, or in no network),
`NetworkCheck` (syncs now; it contacts the paired hosts, so it is not a passive
click), each computer's row title `NetworkMember-<desktop|host>-<ID>` (status
text, for example `lab-gpu. Host, not paired with this PC yet; added on
desktop-diva.`, and for another computer where it was last active, *Active now
on diva-host.*) with `NetworkRemove-<desktop|host>-<ID>`, each computer that uses
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
(paired connection)*, *SSH, with Docker there*, *SSH, native Ubuntu*, *This
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
last command it ran, or the update of its own host service). The same card
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
the button's label and step, for example *Add Thinking: Add roles*), whether
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
Devices' `AddComputer` (and Settings' `OpenHosts`) opens the *Add a computer*
wizard (`HostsWindow`, titled *Martlet - add a computer*; the click may return
`completed: false` while that dialog stays open). Its rail steps
(`HostsStepWhere`, `HostsStepInstall`, `HostsStepPair`, `HostsStepRoles`),
`HostsBack`, `HostsNext`, `HostsClose`, the method cards (`HostMethodThisPc`,
`HostMethodSshDocker`, `HostMethodSshNative`, `HostMethodOnHost`; choosing one
moves on to Install), `HostsEnterCode` (straight to Pair for a host that already
shows a code) and the `HostCommandSection`, `PairCommandSection` and
`DeviceIdSection` expanders only change what the wizard shows, so they are
passive clicks. Snapshots return `HostStatus` (the wizard's status line: what
pairing did, or why it was refused, such as *That code doesn't match...* or *No
Martlet host answered at ...*), `PairedHost`, and `PairCodeTitle`/`PairCodeHelp`
(*Enter the code shown on the host* when Martlet can't reach the host, *Or enter a
code from the host* next to `PairConsole` otherwise). `PairAddress` and
`PairingCode` take the host's address and short code (`ui_set_text`, so
`--allow-ui-effects`), and `PairHost` pairs; a successful pairing stores a
device secret in Windows Credential Manager, so verification stops at refused
codes. On the host dashboard, *Show a pairing code* (`Step-pair-0`) shows the
address (`HostRunPairAddress`, returned) and the one-use code (`HostRunPairCode`,
never returned) in the run window's `HostRunPairing` panel; the host-runs log
masks codes. While the host isn't paired, `StepDetail-pair` tells the owner to find this PC from the
main PC (*Martlet on your network*), and when Windows Firewall keeps other
computers out it says so and `Step-pair-1` (*Let my other computers find this
PC*, an administrator prompt) appears. While a computer asks to
join the network this host PC is in, a step `join-<device ID>` (*Let IMOUTO into
your Martlet network*) follows it: `StepDetail-join-<device ID>` gives the check
number, `Step-join-<device ID>-0` is **Allow** and `Step-join-<device ID>-1`
**Turn down** (both change the network, so they need `--allow-ui-effects`).

*Martlet on your network* ([how it works](ARCHITECTURE.md#finding-your-other-computers))
is the first card of the wizard's *Where it runs* step. Opening the wizard on
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
example `job-listening`), `docker`, `failed-thinking`, `failed-listening`,
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
`voice`, `lipsync`, `microphone`, `speakers`, `character`, `devices`, `tools`,
`updates`, `app`) return *<Part>: OK*, *needs attention* or *not checked or
not set up* with the state, and clicking one only opens its page.
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
errors and warnings and where remote lines came
from (the log host, each paired host's own log, or why not). The filters are
pills that only filter: `LogLevel-all`, `LogLevel-warnings`, `LogLevel-errors`,
`LogPart-<part>` (`all`, `desktop`, `avatar-renderer`, `host-runs`, `gateway`)
and `LogSource-<computer>` (`all`, this PC's device ID such as
`LogSource-desktop-diva`, which also covers this PC's host service, or another
computer's ID); each returns its label as its value (*From: All computers*,
*From: This PC (desktop-diva, diva-host)*, *From: gpu-pc*). All are passive clicks, and snapshots
report which is chosen in `selected`. `LogSearch` needs `ui_set_text` (and so
`--allow-ui-effects`). `LogsRefresh` reads the logs again and sends nothing, so
it is passive; `LogsCopy` (clipboard) and `LogsOpenFolder` (Explorer) are not.
`LogHostChoice` returns the chosen log host (*None (each computer keeps its
own)*, a host ID, or *<host> (not paired with this PC)*); changing it with
`ui_select` changes the shared plan and needs `--allow-ui-effects`.
`LogHostStatus` says what this PC last sent to the log host and which hosts
didn't answer or need a Martlet update. To see lines on a
disposable data directory, write `logs\desktop.log` (lines like
`2026-10-01 22:15:44.974 -07:00 WARN [1] message`), `logs\avatar-renderer.log`
or `logs\host-runs.log` before launching. Home's `HealthOpen-errors-diagnostics`
and `HealthOpen-crash-diagnostics` open this page.

`ui_snapshot` reports `selected` (true or false) for controls that are chosen
rather than ticked (navigation, Companion's side list, radio buttons and
filter pills, list items), and a combo box in the status fields reads as its
chosen option.

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
`TrayTalk`), `CharacterOpenMartlet` (*Open Martlet*, shows the window like
`TrayOpen`, also from the notification area) and `CharacterSettings`
(*Character settings*, opens Companion › Character), which are passive clicks;
then `CharacterZoomIn`, `CharacterZoomOut`, `CharacterResetZoom` (disabled at
the default zoom), `CharacterResetPosition`, `CharacterLockPosition`, the checkable `CharacterOnTop`
(*Keep on top*, on by default; its `checkedState` is the current choice for
this showing) and `CharacterHide` (*Hide character*; Esc on the overlay does
the same), which need `--allow-ui-effects`. Talk, Open, Settings and Hide are
carried out by Martlet itself, so the desktop log records *The character's menu
chose 'hide'.* (and so on), and a hide is followed by *Avatar renderer stopped
by Martlet.* and `SetupCharacterNow` reading *hidden*.

`MoveAvatar` also supports UI Automation's move: with `--allow-ui-effects`,
`ui_move` moves the character by `dx`, `dy` screen pixels like a drag and
returns its bounds before and after, and `ui_snapshot` reports `movable` for
it. **Locking the character's position**: Home's `ToggleCharacterLock`
(*Lock character position*, shown while the character shows or is locked),
Companion › Character's `SetupCharacterLock` (*Lock position*) and the overlay
menu's `CharacterLockPosition` (*Lock position*, carried out by Martlet: *The
character's menu chose 'lock'.*) lock it where it is; all need
`--allow-ui-effects` because they save `character-placement.json` (see
`character_status`'s `placement`). Locked, `movable` is false and `ui_move` is
refused; the overlay ignores dragging, the arrow keys and Home; its
`CharacterResetPosition`, Home's `ResetCharacterPosition` and Companion's
`SetupCharacterResetPosition` are disabled; and zoom (the wheel, the menu or
`SetupCharacterZoomIn`) only zooms the camera, keeping the overlay's bounds.
Only Martlet's window unlocks it: the same `ToggleCharacterLock` and
`SetupCharacterLock` then read *Unlock character position* and *Unlock
position* (also while the character is hidden), and the overlay menu's item
reads *Position locked: unlock in Martlet* and only opens Companion ›
Character. `SetupCharacterPlacement` says whether the position is locked and
where (device-independent pixels), and `SetupCharacterView` ends with
*Position locked.* when the overlay reports it. A locked character shows at
its locked place again after Hide/Show or a Martlet restart (at its default
spot, still locked, if that place is no longer on a screen); the desktop log
records *Character position locked at ...* and *Character position unlocked.*,
and `avatar-renderer` *The character's position is locked.*

The same page's *Speech bubbles and subtitles* card has the checkboxes
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
position and size), and whether the text as laid out on screen lies inside the
bubble (*holding all its text*, or *but its text doesn't fit inside it*).
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

Companion › Character's *Emotes and motions* card lists the
[emotes and motions](AVATARS.md#emotes-and-motions) of the character this PC
shows (or would show). `CharacterActionsStatus` reads how many emotes and
motions the model has (with Martlet's nod and shake) and whether they were named
by the Thinking model (and when) or from the model's own files, or why they
couldn't be read; `CharacterActionsNaming` the Thinking model's naming
(*Asking the Thinking model...*, *The Thinking model named 10 emotes and motions
at 3:12 PM and turned off 3 that aren't feelings or gestures.*, or why it
couldn't, such as *Thinking isn't set up yet*); `CharacterActionsOffered` the
tags replies get with the voice chosen now and which follow the voice's cues;
`CharacterActionsLast` the last one played (*Played the expression "脸红" for
{blush} at 3:14:05 PM.*, *... for a try ...*, or *The character couldn't play
...*), also in `logs_tail` `desktop` as *Character expression '脸红' played for
{blush}.*; and `CharacterActionsSaveState` *All changes saved.* or *Not saved:
<why>*. Row `<n>` (as in `character_actions`) has `CharacterActionName-<n>`
(its name and kind; a status field), `CharacterActionOn-<n>` (check box),
`CharacterActionTag-<n>` (an English tag; a tag in another script reads *Not
saved: ... use up to 24 English letters (a-z) ...*), `CharacterActionCue-<n>` (combo box: `(none)` or a
cue such as `laugh`), `CharacterActionUse-<n>` and `CharacterActionTry-<n>`
(plays it on the showing character; disabled while it is hidden). Editing a row
saves `character-actions.json`, `CharacterActionsDetect` (*Name them with
Thinking*) sends the model's emote and motion names and details to the Thinking
model, `CharacterActionsReset` goes back to the model's own names, and Try plays
on the overlay, so all of them need `--allow-ui-effects`. The first time a model
shows with a Thinking model set up, Martlet names its emotes once on its own.
`character_actions` reads the same settings headlessly.

For voices, open `CompanionTab-Voice` (the Voices card shows unless the
voice comes from a cloud provider). There are no built-in voices and no groups:
one list, in the order voices joined it (a new list starts with the starter
voices). `F5VoicesStatus` reads how many voices there are and which is chosen or in
use (a starter voice's name, "one of your recordings", or "a voice no longer in
the list"), for example "7 voices. None chosen yet; Martlet starts with Annie
(cute anime girl)." `F5VoicesShared` reads whether the list is shared with the
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
shown computer as one row each, keyed by engine (`chatterbox`, `f5`, `xtts`,
`gpt-sovits`, `dia`, and `windows` for a Windows voice under This PC):
`VoiceEngine-<key>` reads its name and badge ("Chatterbox Turbo · recommended",
"Windows voice · in use"), `VoiceEngineFeatures-<key>` its chips ("NVIDIA GPU,
6 GB+, Docker, Voice cloning, 5 s+ samples, Laughs & sighs, Emotions, English";
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
`f5_voices` returns the chosen engine as `chosenEngine`.

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

A host role's Add dialog (`HostInputDialog`) lists its choices as
`HostInput-choice.<VAR>` combo boxes whose selected value snapshots return (for
example `HostInput-choice.A2F_ENGINE` reads `local` or `nim`), the terms of the
chosen variant as `HostInputTerms-<VAR>`, and its secrets as
`HostInput-secret.<name>` password boxes, never with their values. A variant's
own secret appears only while its choice is selected (the Audio2Face NIM
engine's `HostInput-secret.ngc_api_key` only for `nim`); hidden fields are not
required and not sent. `HostInputOk` installs and needs `--allow-ui-effects`.
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
Voice lists its engines per computer instead (`SpeakingHost-<host ID>` and
`VoiceEngineUse-<key>`, above).

Status fields include the talk window's `LiveStatus` (its status line),
`LiveMic` (the Start listening / Stop listening button; its value starts with
the state: *Not listening* until it is pressed, then *Listening*, *Can't
listen* or *Mic unavailable* with the reason; while Martlet speaks it reads
*Not listening while Martlet speaks*. Clicking it opens the microphone, so it
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
come back to talk.*). While Martlet listens or watches, the talk window's close
button only hides it (`ui_snapshot` stops listing *Talk with Martlet*;
`OpenLiveConversation` shows it again),
`LiveVision` (*Watching*, *Looking*, *Vision paused* or *Can't see*, with when
it last checked the screen; it checks every 3 s, and with the whole screen it
also says it looks right away at pop-up notifications and flashing taskbar
buttons), `LiveVisionStatus` (while
vision is on: what it sees, for example *Watching the window behind Martlet*
or *Watching your whole screen (2 monitors).*, then the last look's outcome or
why it is holding off, what wanted your attention (*Last look 10:17 PM (a
flashing taskbar button): nothing to say.*, *Martlet noticed a notification and
looks once you're done talking.*, *Noticed a notification at 10:17 PM but
didn't look: you seem away.*) and whether your last message went with the
picture (*Your message at 10:14 PM went with it.*); it never contains window
titles; to rehearse a flash, show any test window minimized and call
`FlashWindowEx` on it), `LiveContext` (*Keeps the last N
exchanges in mind, about T tokens of its C-token context.*, or *Replies send the
newest that fit its C-token context.* once they outgrow it, followed by *Last
reply: P% of its N input tokens came from the model's cache.* once the Thinking
model reported its cache use: how many exchanges
of the open talk window the next reply can see, their estimated tokens and the
context size from Companion › Replies; absent when none, and unchanged when a
settings change is picked up; beside it,
`LiveRefreshContext` (*Refresh context*, a passive click, disabled mid-reply)
forgets them so the next reply starts fresh, adds the note *Context refreshed.*
to `LiveHistory` and hides `LiveContext`), `LiveJobs` (shown while Martlet
works in the background or a finished job waits to be brought up: *Working in
the background: think-1 running for 0:12. You can keep talking; Stop doesn't end
it.*, *think-1 waiting for a quiet moment*, *think-1 paused while you talk
(0:30)*, *think-1 done after 1:02. Martlet brings it up as soon as it's free.* or
*... when you talk next.*; never what a job is about), each job's chip
`LiveJob-<id>` (*Thinking about: <what> · 0:12*; it holds what the job is about,
so snapshots don't return it) and its `LiveJobCancel-<id>` (a passive click: it
only stops that job, and the next thing you say tells Martlet), with the status
line reading *Starting to think it over in the background…* while
`think_longer` runs and *Martlet is bringing up what it worked on…* while
Martlet's own report is on its way; Companion › **Deep thinking**'s
`DeepThinkingNow` (*Thinks on diva (gemma4:27b), in parallel with the
conversation.*, *Thinks on the Thinking model (gemma4:e4b), in quiet moments.*,
or *Off. When on, it thinks on ...*) and `DeepThinkingParallel` (why: *Thinking
runs on this PC and its server answers one request at a time, so a think waits
for quiet moments.*, *diva does none of the conversation's jobs, so a think runs
there alongside the conversation.*), `ThinkLongerOn` (*Let Martlet think longer
when it needs to*; `checkedState` is the saved choice), `ThinkLongerStatus`
(*On. When a task needs it, Martlet says it'll think it over and works on it in
the background (Medium effort, up to 5 minutes, at most 6 an hour) while you keep
talking, then brings it up as soon as it's free.*, *Off. ...*, or what keeps it
from working: no Thinking, a paired host's model, a model that turned tools
down) and the choices `ThinkLongerEffort`, `ThinkLongerTime`,
`ThinkLongerPerHour` and `ThinkLongerDelivery` (returned; `ui_toggle` and
`ui_select` on them save the reply settings, so they need
`--allow-ui-effects`); *Where it thinks* with the passive options
`DeepPlace-Same`, `DeepPlace-Computer`, `DeepPlace-ThisPc` and
`DeepPlace-Cloud` (each only shows its card): each paired computer's
`DeepThinkingHost-<host ID>` (*diva: Ollama runs gemma4:27b.*, *Thinks here
(...)*, *Ollama isn't installed there...*) or `DeepThinkingHosts` when none is
paired, `DeepThinkingLocalStatus` (what Ollama on this PC has downloaded) and
`DeepThinkingKeyStatus` (what the key field will do; never a key or typed base
URL). `DeepThinkingUseSame`, `DeepThinkingUseHost-<host ID>` (checks that
computer and saves its Ollama route), `DeepThinkingUseLocal` and
`DeepThinkingSaveCloud` (with `DeepThinkingProvider`, `DeepThinkingBaseUrl`,
`DeepThinkingModel`, `DeepThinkingKey` and `DeepThinkingConsent`) save
`deep-thinking.json` and need `--allow-ui-effects`; the next think uses it.
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
window never opens the microphone; `LiveMic` does with always listening on. For
verification, save a fixed microphone that does not exist in the disposable
data directory, so listening starts after `LiveMic`,
fails without capturing real audio and shows *Mic unavailable* while it keeps
retrying (it never stops by itself). The talk window's `LiveStop` (Stop, Esc)
is a passive click: it only stops a reply, recording or vision. Changing How
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
then `TalkBargeIn` (*Let me interrupt Martlet by
talking*, optional and off by default; its `checkedState` is the saved choice, and
`ui_toggle` on it needs `--allow-ui-effects` because it saves
`talk-preferences.json`) and `TalkBargeInAbout` (returned: that it is optional
and off by default, and what talking over
Martlet takes: real words, a word like "stop" or "wait" right away, never a hum,
a cough, laughter, a quick "yeah" or what this PC plays, checked while you talk
with Parakeet on this PC and otherwise once you pause; `utterance_filter_check`
rehearses it with Parakeet and `echo_check`'s `talkOver` the voice gate). In the
talk window, what always listening ignored shows in `LiveHistory` as a faded
note (*Ignored "Mmm" (not words).*), and the desktop log (`logs_tail`) has
*Always listening ignored what it heard: ...* and *Barge-in: Martlet stopped its
reply N ms after you started talking over it (...)*, never the words. Below it, the *Speakers and echo* card has
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
outputs are in use, never their sound); `pc_audio_check` reads the same choice. With it on
and always listening chosen, the talk window's `LivePcAudio` line (returned)
says *Also hears what this PC plays once you start listening.*, *Also hearing
what this PC plays (not Martlet's own voice).*, *Also hearing what this PC
plays on <your output> (paused while Martlet speaks).*, *Hearing this PC play
something…* or why it can't hear the PC, followed by *This PC plays your voice
back too; Martlet left out N line(s) of it.* once a line the PC played repeated
what you said; what the PC played shows in
`LiveHistory` as *Playing on this PC* bubbles. Pressing `LiveMic` with it on
records what the PC plays, so leave it off (or don't start listening) when
verifying on a desktop whose sound must not be captured. Each reply writes a
*Reply latency* line to the desktop log (see [Latency](#latency)), which
`logs_tail` returns and `latency_report` summarizes.

Window discovery uses visible top-level native handles filtered to the attached
process (and its own character renderer child process), then verifies ownership
around each UI Automation handle lookup.
This avoids transient omissions from UI Automation's desktop-root enumeration
when unrelated WPF windows close. The Martlet main-window automation ID is
still required on every operation, unless the window is hidden in the
notification area and the process still has its icon's (hidden) window;
closed or changed-owner windows fail rather than falling back to another
process. Same-process dialogs remain available, and duplicate control IDs
still fail as ambiguous.

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
`TrayCloseToTray` and `TrayStartWithWindows` (their `checkedState` is the
current choice) and `TrayExit`. The menu, like text boxes' Cut/Copy/Paste
menus, is drawn in Martlet's palette (Themes\Controls.xaml), with no light icon
column in the dark palettes; `ui_snapshot` returns the palette as `AppearanceTheme`
(*Pink light*, *Rose dark*, *Character light*, *Character dark*, *Character
light by Thinking* or *Character dark by Thinking*; choosing one with
`ui_select` saves `appearance.txt`, so it needs `--allow-ui-effects`) and
Settings' line about it as `AppearanceStatus`. Settings › Appearance also has
`AppearanceCharacterStatus` (the character's colors: how many and where the
accent comes from, or why they couldn't be read; never its name),
`AppearanceColor-<n>` (each main color: *#2B3440 31% dark grayish blue*),
`AppearancePreview-<rules|thinking>-<light|dark>` (each character palette's
colors by role, or *not made yet*), `AppearanceThinkingStatus` (when and from
what the Thinking model made its palettes, its reason, or how asking went) and
`AppearanceThinkingMake` (*Make with Thinking*; it sends the character's colors,
name, where it is from and picture to the Thinking model, so it needs
`--allow-ui-effects`). `AppearanceCharacterAbout` (who the character is and
where it's from, typed by the owner; it saves on its own) and
`AppearanceIdentity` (what the Thinking model is told) are listed without their
text, since they carry the character's name. The
desktop log records *Read N colors from the character's textures.* and
*Applied the Character dark palette (#D194AE accent on #161E24).*
`character_theme` makes the same colors and palettes headlessly.
On a Martlet host (Settings › *Use as a Martlet
host*) the menu has no `TrayTalk`, `TrayStartListening` or `TrayCharacter`: a
host doesn't talk, listen or show the character. `TrayOpen`, `TrayTalk` (like
`OpenLiveConversation`), `TrayPause` (it only stops work) and `TrayEndTalk`
(like `CloseLive`) are passive clicks; `TrayResume`, `TrayCharacter`, the two
choices and `TrayExit` need `--allow-ui-effects`. While another Martlet dialog
(Setup, Companion...) is open, `TrayTalk` and `TrayCharacter` are disabled and
`TrayExit` shows Martlet instead of exiting (the status line names the open
window to close first). Settings › *Startup and closing* has
`CloseToTray` (checked by default; saves `background.json`), `StartWithWindows`
(the per-user Run entry `Martlet`: this executable, the same `--data-directory`
and `--tray` when `StartInTray` is checked; verify with a disposable data
directory and turn it off again afterwards), `StartInTray`, `StartCompanion`
(*When Martlet starts, show the character and start listening*; saves
`background.json` and applies on every start, `--tray` included, so the
desktop log records *Martlet started with the character and listening.*) and
`BackgroundStatus`
(status text: what closing does, and whether Windows starts Martlet, including
when Windows' own Startup apps switch turned it off). On a Martlet host
`StartCompanion` is disabled but keeps its saved state, `BackgroundStatus` says
the character and listening don't start there, and the log records *Martlet
started as a Martlet host: the character and listening stay off on this PC*
instead (the character's *Show at startup* and Parakeet's warm-up are skipped
too). Choosing `UseAsHost` (Settings › *What this PC is for*; it saves
`device-role.txt`, so it needs `--allow-ui-effects`) ends a running
conversation and hides the character, logging *This PC became a Martlet host,
so Martlet ended the conversation...*; `UseAsCompanion` changes no saved
companion choice, so they apply again from the next start. `DeviceRoleSummary`
(*Companion PC* or *Host PC*) and `DeviceRoleText` return the role as text. A second start with the
same data directory shows the running Martlet and exits (with `--tray` it only
exits); a different `--data-directory` runs beside it, so disposable
verification desktops never reach your own Martlet. `-DesktopArguments '--tray'`
on `scripts\Invoke-MartletMcp.ps1` starts the disposable desktop in the
notification area.

**Exiting.** `ExitMartlet`, `TrayExit` and (with *Keep running when closed*
off) `ui_tray` `close` exit Martlet, so they need `--allow-ui-effects`. An exit
that would cut work short (backup and restore, a setup task other than a reply,
a troubleshooting report being made or waiting to be exported, an update
download, a Parakeet download, a host update, a command
from another computer, a running run window or *Prepare this computer*) waits
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

For broader **explicitly authorized** live UI testing, start the MCP server
with `--allow-ui-effects`. This unlocks arbitrary ID-based `ui_click` and
`ui_select`, plus `ui_set_text` (an empty `text` clears a field), `ui_toggle`
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

## Verifying changes with Martlet MCP

Every new feature or behavior change is verified on the dev machine through
this server before merge, whenever the machine can exercise it, alongside the
affected tests (the policy is in [AGENTS.md](../AGENTS.md#validate-before-merge)
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
- `voices_status`, `voices_engine_check` and `utterance_filter_check` calls without a `martletDirectory`
  use this checkout's Desktop build when it is built.
- Doctor, `voices_status`, `f5_voices`, `cluster_status`, `network_status`, `nearby_status`, `logs_tail`, `logs_timeline`, `latency_report`, `virtualization_status`, `mcp_servers_status`, `api_keys_status`, `smart_home_status`, `terminal_status`, `terminal_check`, `think_longer_status`, `prompts_status`, `settings_sync_status`, `memory_sync_status`, `character_status`, `hearing_check`, `model_ability_check`, `echo_check`, `pc_audio_check`, `utterance_filter_check`, `context_check`, `thinking_steps_check`, `character_models`, `character_actions` and `character_theme` calls without a `dataDirectory` get the script's disposable data
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
