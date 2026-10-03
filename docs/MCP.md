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

**Which replies get tools.** Only replies to what you say or type, and only when
Thinking uses OpenAI or a Chat Completions endpoint (local Ollama, LM Studio,
OpenRouter, NVIDIA Build...). Screen and camera glances and memory requests never
get tools, and a paired Martlet host's gateway has no function calling, so replies
from a host's model don't offer tools. If the model rejects a request because it
doesn't support tools (many small local models), Martlet asks it once more without
tools and stops offering them to that model until Martlet restarts.

**Confirmations.** Before each call the talk window shows the tool, its server and
the exact arguments, with *Allow once*, *Always allow this tool* and *Deny*; it is
part of the talk window (not a separate dialog), so answering doesn't end the
action. Unanswered calls are declined after 60 seconds, and a declined call tells
the model so. *Always allow* adds the tool to the server's `autoApprove` list.
The model, not you, chooses what to pass, and text a tool reads (a web page, file
or email) can try to steer it, so only skip confirmation for tools whose effects
you are comfortable with.

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
recognition and sharing choices, whether the sherpa-onnx runtime, voice models and
Parakeet are downloaded, and counts from `voices.json` (voices, named, owner, with
learned names, merged, tombstones). It never returns names, voiceprints or audio and
runs no model.

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
key, `own` or null) with the device that chose it (`chosenBy`). `list` is this PC's
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
syntax, `kind` `Sound` or `Emotion` and `usage`; Chatterbox clones only
recordings longer than 5 s, GPT-SoVITS only 3,000-10,000 ms), each starter
voice adds `engines` (the engines that can clone it) and `language` (`en` or
`ja`, read from its transcript), and `chosenEngine` is the engine chosen on this
desktop (`speaking-engine.txt`, default `chatterbox`). After the desktop
loads settings, a route or applied voice that was `retired-sample` reads the
chosen or first voice. It never returns own voices' names, transcripts or audio, plays
nothing and contacts nothing.

`voice_tags` shows how a reply's [voice tags](CONVERSATION.md#voice-tags) are
handled: `text` (required) is a reply, `engine` an engine key (default the
default engine, `chatterbox`; `none` for a voice without tags such as OpenAI or
Windows) and optional `dataDirectory` whose saved prompt edits are used. It
returns the engine, `supportsTags`, its `tags`, `prompt` (the *Voice sounds and
tones* instructions the Thinking model gets, or null), `spoken` (the pieces the
real speech segmenter hands that engine, its own tags kept), `suppressedPieces`
and `shown` (the chat and caption text, every tag stripped). It synthesizes and
contacts nothing.

`cluster_status` reads [shared who does what](CLUSTER.md) from a data directory
(optional absolute `dataDirectory`, default the current user's): `sync` is
`on (default)` when `cluster-sync.txt` is missing, `on`, or `off` once the owner
unticked **Keep who does what in sync**; `plan` is this PC's `cluster.json`
(`state` `none`, `loaded` or `unreadable`; when loaded its revision, each job's
`host` (null for this PC's own choice), `off`, `failover`, `movedFrom`,
`updatedBy` and `updatedAt`, and each host's ID, roles and `removed`). It never
returns host addresses or keys and contacts nothing.

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
volatile credentials, a throwaway certificate) on `127.0.0.1`, two simulated
desktops driving the desktop's own client (`HostNetwork.cs`) and sync engine
(`NetworkSync.cs`), and a simulated host PC. Like `node_link_check` it runs `src\Martlet.NodeLinkCheck`
(mode `network`, `NetworkRehearsal.cs`) as its own process, because the gateway
needs the ASP.NET Core runtime, and returns `{exitCode, report}`. Its steps: desktop A pairs with a host by a typed code and
founds a network that binds it; A adds a second host to the same network; B
pairs with one host and asks to join with a check number; A sees the same
number; the host tells B (not yet a member) who is paired with it, A and B,
each with when it last made a signed request; A allows B, and B pairs with the
other host by itself; a host PC C outside the network pairs with the first host
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
reference-voice relay route over a fixture voice service, NOT AI, and in-memory
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
answers none. Nothing leaves loopback, the temporary folder is deleted and
Windows Credential Manager is not touched; it does not cover the desktop window
and its sync, the Linux host's files, a real engine, an older host or a real LAN.

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
(SIGKILL); the next automatic run is not blocked (no stale lock);
`logs/engine.log` records the waits; and the desktop's reader
(`HostEngineBusy.Read`) reads the engine's real busy line. Without Docker or the
image it returns `exitCode` 2 and `notRun` (it never pulls). It does not cover
the Docker method's launcher or a real host.

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

`virtualization_status` reports whether Windows is ready for Docker Desktop's
WSL 2 engine, from the same read-only checks the desktop runs before it starts
Docker Desktop (optional absolute `dataDirectory`, default the current user's):
`ready`, `firmwareOff`, `needsWindowsChanges`, `problems` (plain words),
`firmware`, `hypervisor`, `virtualMachinePlatform` and
`windowsSubsystemForLinux` (`Enabled`, `Disabled`, `Absent` or `Unknown`),
`wsl` (version, `none` or null), `virtualMachine`, `summary`,
`dockerDesktop {installed, running, engine}` (`engine` is what
`docker desktop status` reports, for example `running`, `starting` or
`stopped`, or null when Docker Desktop doesn't answer within 15 seconds; a
run window restarts Docker Desktop once when it is open but its engine stays
`stopped` at two checks in a row, and always after Martlet changed Windows for
it) and `continueSetup {pending, kind, task,
created, startsAtSignIn}`: the setup Martlet continues after a Windows restart
(`continue-setup.json` in the data directory, and whether the per-user `RunOnce`
entry that starts Martlet at the next sign-in exists). It runs a CIM query and
`wsl --version` in a hidden Windows PowerShell and `docker desktop status`,
changes nothing and returns no paths.

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
`state` `builtin`, `edited` or `empty`, and `characters`). With an
`id` it also returns `prompt` with that prompt's effective `text` (the
saved edit or the built-in text), exactly what Martlet fills in and sends.
On the page, `PromptsNow` reads how many prompts are edited or emptied and
`PromptState-<id>` each prompt's state (*Built-in text.*, *Edited.*, *Empty:
nothing is sent for this prompt.*, plus *Not saved yet.* for unsaved edits);
neither returns prompt text. `OpenPrompts` (Personality's *Edit prompts*)
only opens the page. The editors `Prompt-<id>`, their `PromptReset-<id>`
buttons, `PromptsDefaults` and `PromptsSave` (which writes settings) need
`--allow-ui-effects`; `ui_set_text` with an empty `text` empties a prompt.

`hearing_check` shows whether the Thinking model can hear the user's
recording (Companion › Listening › **Let Thinking hear my voice**; optional
absolute `dataDirectory`, default the current user's, and optional
`modelId` to classify instead of the saved Thinking model): `model`,
`source` (`argument`, `settings` or `default`), `settings` (`none`,
`loaded` or `unreadable`), `routeType`, `modelHearing` (the name-based
`Supported`, `Unsupported` or `Unknown`), `routeHearing` (the saved route's:
only Chat Completions endpoints other than Ollama take audio; null with
`modelId`) and `hearVoice` (the saved choice, off by default). Its `fixture`
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

`echo_check` checks [echo reduction](CONVERSATION.md#echo-reduction)
(Companion › Listening › **Reduce echo from my speakers**; optional absolute
`dataDirectory`, default the current user's, and optional `delayMs` 0-300,
default 60): `reduceEcho` (the saved choice, on by default) with
`reduceEchoSource` (`saved` or `default`), and `canceller` (`WebRTC AEC3` once
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
20 ms frames Martlet's own voice-activity detector counted as speech. `ok` is
true when the reducer was active, Martlet's echo got at least 20 dB quieter, the
detector heard it without reduction but not with it, and it still heard the user
alone (kept within 3 dB) and over Martlet. It contacts nothing.

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
Status fields include `VisionStatus` (Companion › Vision: whether the Thinking model can see, or has been retired, and the fix), `FallbackNow` (Companion › Thinking › If Thinking fails: the saved fallback endpoint and model and whether it has its own key, uses Thinking's or none; never the key), `FallbackKeyStatus` (what the fallback's key box will do; its fields `FallbackProvider`, `FallbackBaseUrl`, `FallbackModel`, `FallbackKey`, `FallbackConsent` and its `FallbackSave`/`FallbackOff` buttons write settings or a key, so they need `--allow-ui-effects`; `logs_tail` shows each use as *Thinking failed (...) ... the Thinking fallback ... answered instead*, and a rate-limited glance shows in `LiveVisionStatus` as *the provider is limiting requests. Looking again in 1 minute.*), `RepliesNow` (Companion › Replies: that Martlet asks for replies of one or two sentences, the max reply length ceiling in effect, 4096 tokens including any hidden thinking on a Chat Completions or paired-host Ollama route unless set, and the other saved settings), `SetupCloudHint-Thinking` (the cloud provider's recommended Thinking model, or a retired-model warning), `SetupLocalRecommendation` (the local Ollama model recommended for this PC's graphics card, leaving about 5 GB for a game and Martlet's character), `SetupOllamaStatus` (whether Ollama is installed or running and which models it has), `SetupLocalModelTest` (Thinking › This PC: the last *Test model* result for the model in the box, or that it isn't tested yet; a model that doesn't fit in the free graphics memory says so and names a smaller one), `SetupProviderHint` (Setup › Jobs prefilled model), `AppUpdateStatus` (Settings › App updates: the installed version, the check schedule and the last check or download result), `AppCurrentVersion` (Settings › App updates: always-visible *Current version: Martlet x.y.z*) and `SetupF5About` (Speaking › This PC: what the F5 voice is and its non-commercial use restriction). `SetupHostThisPc` and `SetupUseLocal-Speaking` start the F5 setup run window straight away (no extra confirmation; installing Docker Desktop still asks for its terms), so they need `--allow-ui-effects`. `SetupTestLocalModel` (Thinking › This PC's *Test model*) starts Ollama if needed, loads the model in the box and sends it one short loopback chat request in a run window, so it needs `--allow-ui-effects` too; read the outcome from `HostRunStatus` and `SetupLocalModelTest`. A run window (`HostRunWindow`, titled `Martlet - <run>`) returns its status line as `HostRunStatus` (for example *Waiting for Docker Desktop to start...* or why it stopped); its output (`HostRunOutput`, which can show a one-use pairing code) is not returned, so read it with `logs_tail` `host-runs`, which also records each status change. `HostRunCancel` cancels a running run (or closes the window afterwards) and needs `--allow-ui-effects`. On a fresh data directory, F5 setup first needs saved settings (*Complete Setup first.*): `SetupUseWindowsVoice` saves them. Setting `DOCKER_HOST` (for example to a local test named pipe) before launching the desktop points its Docker checks away from the real engine. `ui_click` invokes a control by automation ID and `ui_select` selects a named combo-box
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
navigation too. People shows `PeopleStatus`, `PeopleSyncStatus` and
`PeopleVoiceCount`, and Listening shows `ListenParakeetStatus`; snapshots return
these status texts, as does the talk window's `LiveStatus` (the line under "Martlet": what it is doing, or why the last reply failed, naming the job that failed: *Martlet couldn't speak. ...* for the voice, and *Your Martlet host <ID> didn't answer ...* when the job runs on a paired host). Each voice's controls are numbered by voice (`PeopleName-3`,
`PeopleOtherNames-3`, `PeopleSave-3`, `PeopleOwner-3`, `PeopleMergeTarget-3`,
`PeopleMerge-3`, `PeopleForget-3`); like `PeopleInstall`, `PeopleRecognize`,
`PeopleShare`, `PeopleSync`, `PeopleForgetAll` and `SetupListenParakeet`, they
change data or download and need `--allow-ui-effects`. On Devices, `Node-<id>`
selects a device on the map (`Node-this-pc`, `Node-host:<host ID>`,
`Node-cloud:<server>`, `Node-add`, `Node-missing:brain`) and
`CoverageShow-<job>` selects the device doing a job; both only show details, so
they are passive clicks, as are the `DeviceFactsSection`, `DeviceRolesSection`
and `DeviceReachSection` expanders. `SelectedDevice` and `SelectedDeviceHealth`
return the selected device's name and status, each row title
`DeviceComponent-<part>` (`job-Llm`, `job-Stt`, `job-Tts`, `lipsync`,
`character`, `audio`, `host-service`, `host`, `users`, `role-<role>`, `offer`)
returns the job's name, and its detail line `DeviceComponentDetail-<part>`
returns the row's text. A paired host's `users` row (*Computers using it*) lists
the computers paired with it as the host reports them, for example
`DeviceComponentDetail-users`: `IMOUTO (desktop-imouto), active now; This PC,
active now.`; on this PC's own host service, `DeviceComponentDetail-host-service`
reads `Paired as diva-host. Used by IMOUTO (desktop-imouto), active now.` (or *No
other computer uses it yet.*). Job owners are `ThinkingOwner`, `ListeningOwner`, `SpeakingOwner`
and `LipSyncOwner`, device commands `NodeAction-<action>`
(`NodeAction-InstallRole-<role>` and `NodeAction-RemoveRole-<role>` for host
roles), and Settings for all devices holds `CheckHosts`, `ClusterSync` (checked by
default; unticking it needs `--allow-ui-effects` and saves `off`),
`ClusterStatus` (returned as text) and `RoleSetup-<role>` for jobs nobody does.
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
PC, with Docker Desktop*; `ui_select` needs `--allow-ui-effects`),
`HostReachHint` (status text), `HostReachSsh` and `HostReachSave`. Host
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
*Needs Docker Desktop*, *Waiting for Docker Desktop*, *Not set up yet*, *Host
service stopped*, *Address changed*, *Not answering yet* or *Host is running*),
`HostStepsHeading` (returned) reads *This host is ready* once the required steps
(Docker Desktop, host service, pairing) are done and `HostStepsSummary`
(returned) says how many steps are left and the next one, or *All set*, and when
it last checked. `CheckHostService` (*Check again*) only repeats that read and
says what it found in the status line, so it is a passive click. On the host
dashboard,
`StepDetail-docker` says whether Docker Desktop runs, that it is open but its
engine isn't answering yet, or why Windows can't start
it yet (virtualization off in the firmware, Virtual Machine Platform or Windows
Subsystem for Linux off, WSL missing, hypervisor not running), and
`Step-docker-0` then reads *Turn on virtualization* or *Turn on Windows
features* (administrator prompt and possibly a restart, so it needs
`--allow-ui-effects` and is never part of verification). `StepDetail-service`
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
whose value reads *<time> <level> <computer> · <part>: <first line>*;
clicking one only selects it, and `LogDetail` then returns the whole line
(time, level, computer, part, who passed it on and every following line).
`LogSummary` says how many lines are shown of how many, from how many
computers, the last 24 hours' errors and warnings and where remote lines came
from (the log host, each paired host's own log, or why not). The filters are
pills that only filter: `LogLevel-all`, `LogLevel-warnings`, `LogLevel-errors`,
`LogPart-<part>` (`all`, `desktop`, `avatar-renderer`, `host-runs`, `gateway`)
and `LogSource-<computer>` (`all`, this PC's device ID such as
`LogSource-desktop-diva`, or a host ID); all are passive clicks, and snapshots
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
even then, and Windows ends the renderer with it. After a zoom, the `SetupCharacterView` status reports the overlay's
size, its distance from the top of the screen, the camera zoom and where the
top of the character's head sits relative to the overlay's top edge (it must
stay in view at every zoom).

The character overlay itself is drawn by Martlet's renderer child process
(`Martlet.Avatar.RendererHost`); `ui_snapshot` includes its windows (the
overlay is titled *Martlet character overlay*; another Martlet's renderer is
never included). Its drag surface `MoveAvatar` supports UI Automation
expand/collapse, so `ui_click` on it opens (or closes again) the character's
right-click menu with no flag; opened this way, the menu stays open until a
choice or another `MoveAvatar` click. While it is open, snapshots list
`CharacterMenu` and its items: `CharacterTalk` (*Talk to Martlet*, like
`TrayTalk`), `CharacterOpenMartlet` (*Open Martlet*, shows the window like
`TrayOpen`, also from the notification area) and `CharacterSettings`
(*Character settings*, opens Companion › Character), which are passive clicks;
then `CharacterZoomIn`, `CharacterZoomOut`, `CharacterResetZoom` (disabled at
the default zoom), `CharacterResetPosition`, the checkable `CharacterOnTop`
(*Keep on top*, on by default; its `checkedState` is the current choice for
this showing) and `CharacterHide` (*Hide character*; Esc on the overlay does
the same), which need `--allow-ui-effects`. Talk, Open, Settings and Hide are
carried out by Martlet itself, so the desktop log records *The character's menu
chose 'hide'.* (and so on), and a hide is followed by *Avatar renderer stopped
by Martlet.* and `SetupCharacterNow` reading *hidden*.

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
position and size). The bubble itself is drawn by the separate
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
isn't a number from -4000 to 4000.

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
returned. When the speaking engine cannot clone a voice (Chatterbox: 5 seconds or
shorter; GPT-SoVITS: shorter than 3 or longer than 10 seconds) its title adds
"· wrong length for this engine" and `F5VoiceUse-<key>` is disabled, with the reason
as its help text; Play and Use are also disabled while its recording is still
being copied to this PC. Remove asks with `ConfirmationYes`/`ConfirmationNo` and
removes the voice on every computer. `F5AddVoice` opens *Add a voice*
(`F5AddVoiceDialog`): `F5AddVoicePath` (the WAV's full path), `F5AddVoiceName`,
`F5AddVoiceTranscript`, `F5AddVoiceBasis` (whose voice), `F5VoiceRights` (the
rights confirmation) and `F5AddVoiceOk`, which adds the voice, shares it and uses
it; `F5AddVoiceProblem` returns why it couldn't (the typed name, transcript and
path are never returned). Use, Remove and adding change the voice list and need
`--allow-ui-effects`; Play plays audio and is not for automated verification.
Passive navigation writes nothing: the list is shown as it would start until a
voice is first used, added or removed. `f5_voices` reads the same list headlessly.

Above the voices, the Voice engine card's `SpeakingEngine` combo box reads the
chosen engine ("Chatterbox Turbo (recommended): Clones the voice and can laugh
...", the default; options `SpeakingEngine-chatterbox`, `SpeakingEngine-f5`,
`SpeakingEngine-xtts`, `SpeakingEngine-gpt-sovits` and `SpeakingEngine-dia`), `SpeakingEngineStatus`
says where it speaks and its model licence and `SpeakingEngineTags` lists the
engine's sound and tone tags (or says it reads words only). Choosing
another engine with `ui_select` needs `--allow-ui-effects`: when a computer
speaks it hands Speaking to that engine there (installing its role after a
confirmation) and stops the engine it replaces on that computer, since a host
runs one voice engine at a time. `SpeakingEngineOthers` (shown only then) names
voice engines the speaking computer still runs besides the one that speaks,
for example a host set up before that rule; `SpeakingEngineRelease` stops them
after a confirmation (`martlet-host remove`, downloads kept) and needs
`--allow-ui-effects`. `f5_voices` returns the same choice as `chosenEngine`.

On Companion › Tools (`CompanionTab-Tools`), each server has
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
browser), `SmartHomeConnect` (with `SmartHomeToken`), `SmartHomeDisconnect`, the
setup form (`SmartHomeOwnerName`, `SmartHomeOwnerUser`, `SmartHomeOwnerPassword`,
`SmartHomeOwnerConfirm`, `SmartHomeSetupControl`, `SmartHomeSetupShare`,
`SmartHomeSetUp`), `SmartHomeShareOnConnect`, `SmartHomeInstall-<host>` and
`SmartHomeHostUse-<host>`, `SmartHomeShare`, `SmartHomeStopShare` (asks first),
`SmartHomeUseShared`, `SmartHomeShareCheck`, `SmartHomeDevicesRefresh`,
`SmartHomeDeviceAdd-<n>`, `SmartHomeDeviceIgnore-<n>`, `SmartHomeAddMqtt`,
`SmartHomeUpdateInstall-<n>` and `SmartHomeRestart` (both ask first;
`ConfirmationYes`), `SmartHomeBackup`, `SmartHomeOpen` and
`SmartHomeManageRefresh`. Snapshots return `SmartHomeStatus` (connected or not,
address, name, version, shared), `SmartHomeAddress`, `SmartHomeFindStatus`,
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
passive; the card's own buttons commit. Under *Another of your computers*, each
paired computer that can run the job (every one except this PC's own host
service on Voice, Listening and Lip-sync; a host saved as *This PC* whose
address is another computer counts as that other computer) is listed with
`HostChoice-<job>-<host ID>` (for example `HostChoice-speaking-diva-host`),
which reads the host ID and what it does or could do. `HostChoices-<job>` says
why none are listed (none paired, only this PC's own host service, or none can
run it) and `HostChoicesUnable-<job>` names paired computers whose platform or
hardware can't run it, with why. `SetupUseHost-<job>-<host ID>` hands the job
over and needs `--allow-ui-effects`.

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
it last checked the screen; it checks every 3 s), `LiveVisionStatus` (while
vision is on: what it sees, for example *Watching the window behind Martlet*,
then the last look's outcome or why it is holding off, and the looks used this
hour; it never contains window titles), `LiveContext` (*Keeps the last N
exchanges in mind.*: how many recent exchanges the next reply sees; absent when
none, and unchanged when a settings change is picked up; beside it,
`LiveRefreshContext` (*Refresh context*, a passive click, disabled mid-reply)
forgets them so the next reply starts fresh, adds the note *Context refreshed.*
to `LiveHistory` and hides `LiveContext`)
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
always listening, the same card has `TalkBargeIn` (*Let me interrupt Martlet by
talking*, on by default; its `checkedState` is the saved choice, and
`ui_toggle` on it needs `--allow-ui-effects` because it saves
`talk-preferences.json`). Below it, the *Speakers and echo* card has
`TalkReduceEcho` (*Reduce echo from my speakers*, on by default; its
`checkedState` is the saved choice and `ui_toggle` needs `--allow-ui-effects`)
and `TalkReduceEchoStatus` (returned: *On. Martlet removes what this PC plays
from the microphone whenever it listens.*, how the last listen went, why echo
reduction couldn't run, or *Off. ...*); `echo_check` reads the same saved
choice. Each spoken reply writes a *Reply latency: first words
after … ms, first audio after … ms* line to the desktop log, which `logs_tail`
returns.

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
(the icon is in the notification area), `mainWindowVisible` and `inTray`;
`"action":"open"` and `"action":"menu"` post the icon exactly what Explorer
sends for a left click (show Martlet) and a right click (its menu at the mouse
pointer), so they need no flag; `"action":"close"` presses the main window's
close button, which hides Martlet or (with *Keep running when closed* off) exits
it, so it needs `--allow-ui-effects`. While the menu is open `ui_snapshot` lists
`TrayMenu` and its items: `TrayStatus` (status text: *Martlet is running*,
*Martlet is listening*, *Martlet is paused*, *Martlet is watching* or *Martlet:
talk window open*), `TrayOpen`, `TrayTalk` (*Talk to Martlet*, or *Show the
talk window* while it is open), and while the talk window is open `TrayPause` or
`TrayResume` and `TrayEndTalk`, then `TrayCharacter`, the checkable
`TrayCloseToTray` and `TrayStartWithWindows` (their `checkedState` is the
current choice) and `TrayExit`. On a Martlet host (Settings › *Use as a Martlet
host*) the menu has no `TrayTalk`, `TrayStartListening` or `TrayCharacter`: a
host doesn't talk, listen or show the character. `TrayOpen`, `TrayTalk` (like
`OpenLiveConversation`), `TrayPause` (it only stops work) and `TrayEndTalk`
(like `CloseLive`) are passive clicks; `TrayResume`, `TrayCharacter`, the two
choices and `TrayExit` need `--allow-ui-effects`. While another Martlet dialog
(Setup, Companion...) is open, `TrayTalk` and `TrayCharacter` are disabled and
`TrayExit` shows Martlet instead of exiting. Settings › *Startup and closing* has
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

For broader **explicitly authorized** live UI testing, start the MCP server
with `--allow-ui-effects`. This unlocks arbitrary ID-based `ui_click` and
`ui_select`, plus `ui_set_text` (an empty `text` clears a field) and `ui_toggle`. It does **not** waive the
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
this server before merge, whenever the machine can exercise it (the policy is
in [AGENTS.md](../AGENTS.md#verify-changes-through-martlet-mcp)).
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
- Doctor, `voices_status`, `f5_voices`, `cluster_status`, `network_status`, `nearby_status`, `logs_tail`, `logs_timeline`, `virtualization_status`, `mcp_servers_status`, `api_keys_status`, `smart_home_status`, `prompts_status`, `hearing_check` and `echo_check` calls without a `dataDirectory` get the script's disposable data
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
  `-Configuration` (default Release); the `dotnet` on `PATH` must provide the
  SDK pinned in `global.json`.

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
