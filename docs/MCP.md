# MCP in Martlet

Martlet speaks the Model Context Protocol in both directions:

- **As a client** (below): MCP servers on your PC give Martlet tools it can use while
  you talk.
- **As a server** ([Local MCP control](#local-mcp-control-windows)): `Martlet.Mcp`
  lets an MCP client run Martlet's diagnostics and drive its desktop UI.

## Tools while you talk (MCP client)

Add MCP servers on **Companion > Tools** (*Edit servers (mcp.json)*). The file is
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

`f5_voices` lists the [F5 reference voices](F5_VOICE.md#desktop-voices-and-playback)
Martlet includes (key, name, `female`, description, licence, transcript, SHA-256,
sample rate and duration; each clip is checked against its SHA-256 and the
reference store's audio, name and transcript rules, `valid` or the failure), the
`default` key, `defaultName` and `defaultFemale` (always true). From a data
directory (optional absolute `dataDirectory`, default the current user's) it reads
the `f5-voices` list: `state` (`none`, `loaded`, `busy` while the desktop holds it,
or `unreadable`), the number of voices, the keys of included voices in it, the
count of the owner's own voices, whether the retired F5-TTS example clip is still
there and the applied voice (an included key, `own`, `retired-sample` or null).
`speaking` reads `settings.json`: `state` (`none`, `loaded` or `unreadable` with
the settings rule it broke or the error type as `problem`), the
speaking route's type (for example `GatewayF5`, null without one) and the voice it
records (an included key, `own`, `retired-sample` or null). After the desktop
loads settings, a route or applied voice that was `retired-sample` reads
`lj-speech`. It never returns own voices' names, transcripts or audio, plays
nothing and contacts nothing.

`cluster_status` reads [shared who does what](CLUSTER.md) from a data directory
(optional absolute `dataDirectory`, default the current user's): `sync` is
`on (default)` when `cluster-sync.txt` is missing, `on`, or `off` once the owner
unticked **Keep who does what in sync**; `plan` is this PC's `cluster.json`
(`state` `none`, `loaded` or `unreadable`; when loaded its revision, each job's
`host` (null for this PC's own choice), `off`, `failover`, `movedFrom`,
`updatedBy` and `updatedAt`, and each host's ID, roles and `removed`). It never
returns host addresses or keys and contacts nothing.

`virtualization_status` reports whether Windows is ready for Docker Desktop's
WSL 2 engine, from the same read-only checks the desktop runs before it starts
Docker Desktop (optional absolute `dataDirectory`, default the current user's):
`ready`, `firmwareOff`, `needsWindowsChanges`, `problems` (plain words),
`firmware`, `hypervisor`, `virtualMachinePlatform` and
`windowsSubsystemForLinux` (`Enabled`, `Disabled`, `Absent` or `Unknown`),
`wsl` (version, `none` or null), `virtualMachine`, `summary`,
`dockerDesktop {installed, running}` and `continueSetup {pending, kind, task,
created, startsAtSignIn}`: the setup Martlet continues after a Windows restart
(`continue-setup.json` in the data directory, and whether the per-user `RunOnce`
entry that starts Martlet at the next sign-in exists). It runs a CIM query and
`wsl --version` in a hidden Windows PowerShell, changes nothing and returns no
paths.

`logs_tail` reads the last `lines` (1-400, default 100) of one local log under
`<dataDirectory>\logs` (`log`: `desktop` (default), `avatar-renderer` or
`host-runs`), optionally only lines that `contains` some text (case-insensitive,
at most 200 characters). It returns `{log, exists, truncated, lines}` and never
writes, rotates or deletes a log. Failed provider requests appear in the desktop
log with their endpoint, model, HTTP status and the provider's own short
explanation, followed by a `Reply failed (...)` line naming the route (`Spoken reply failed (...)` naming the voice
route when the text arrived but speaking it failed), for
example `{"name":"logs_tail","arguments":{"contains":"failed"}}`. Logs can
include local paths and provider error text (never keys or conversation content).

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
Status fields include `VisionStatus` (Companion › Vision: whether the Thinking model can see, or has been retired, and the fix), `RepliesNow` (Companion › Replies: that Martlet asks for replies of one or two sentences, the max reply length ceiling in effect and the other saved settings), `SetupCloudHint-Thinking` (the cloud provider's recommended Thinking model, or a retired-model warning), `SetupLocalRecommendation` (the local Ollama model recommended for this PC's graphics card), `SetupOllamaStatus` (whether Ollama is installed or running and which models it has), `SetupLocalModelTest` (Thinking › This PC: the last *Test model* result for the model in the box, or that it isn't tested yet), `SetupProviderHint` (Setup › Jobs prefilled model) and `SetupF5About` (Speaking › This PC: what setting up F5 installs and its licence). `SetupHostThisPc` and `SetupUseLocal-Speaking` start the F5 setup run window straight away (no extra confirmation; installing Docker Desktop still asks for its terms), so they need `--allow-ui-effects`. `SetupTestLocalModel` (Thinking › This PC's *Test model*) starts Ollama if needed, loads the model in the box and sends it one short loopback chat request in a run window, so it needs `--allow-ui-effects` too; read the outcome from `HostRunStatus` and `SetupLocalModelTest`. A run window (`HostRunWindow`, titled `Martlet - <run>`) returns its status line as `HostRunStatus` (for example *Waiting for Docker Desktop to start...* or why it stopped); its output (`HostRunOutput`, which can show a one-use pairing code) is not returned, so read it with `logs_tail` `host-runs`, which also records each status change. `HostRunCancel` cancels a running run (or closes the window afterwards) and needs `--allow-ui-effects`. On a fresh data directory, F5 setup first needs saved settings (*Complete Setup once...*): `SetupUseWindowsVoice` saves them. Setting `DOCKER_HOST` (for example to a local test named pipe) before launching the desktop points its Docker checks away from the real engine. `ui_click` invokes a control by automation ID and `ui_select` selects a named combo-box
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
these status texts, as does the talk window's `LiveStatus` (the line under "Martlet": what it is doing, or why the last reply failed, naming the job that failed: *Martlet couldn't speak: ...* for the voice, and *Your Martlet host <ID> didn't run ...* when the job runs on a paired host). Each voice's controls are numbered by voice (`PeopleName-3`,
`PeopleOtherNames-3`, `PeopleSave-3`, `PeopleOwner-3`, `PeopleMergeTarget-3`,
`PeopleMerge-3`, `PeopleForget-3`); like `PeopleInstall`, `PeopleRecognize`,
`PeopleShare`, `PeopleSync`, `PeopleForgetAll` and `SetupListenParakeet`, they
change data or download and need `--allow-ui-effects`. On Devices, `Node-<id>`
selects a device on the map (`Node-this-pc`, `Node-host:<host ID>`,
`Node-cloud:<server>`, `Node-add`, `Node-missing:brain`) and
`CoverageShow-<job>` selects the device doing a job; both only show details, so
they are passive clicks, as are the `DeviceFactsSection`, `DeviceRolesSection`
and `DeviceReachSection` expanders. `SelectedDevice` and `SelectedDeviceHealth`
return the selected device's name and status, and each row title
`DeviceComponent-<part>` (`job-Llm`, `job-Stt`, `job-Tts`, `lipsync`,
`character`, `audio`, `host-service`, `host`, `role-<role>`, `offer`) returns the
job's name. Job owners are `ThinkingOwner`, `ListeningOwner`, `SpeakingOwner`
and `LipSyncOwner`, device commands `NodeAction-<action>`
(`NodeAction-InstallRole-<role>` and `NodeAction-RemoveRole-<role>` for host
roles), and Settings for all devices holds `CheckHosts`, `ClusterSync` (checked by
default; unticking it needs `--allow-ui-effects` and saves `off`),
`ClusterStatus` (returned as text) and `RoleSetup-<role>` for jobs nobody does.
Home and host-dashboard steps have their buttons as `Step-<step>-<n>` and their
detail line as `StepDetail-<step>` (status text): on the host dashboard,
`StepDetail-docker` says whether Docker Desktop runs or why Windows can't start
it yet (virtualization off in the firmware, Virtual Machine Platform or Windows
Subsystem for Linux off, WSL missing, hypervisor not running), and
`Step-docker-0` then reads *Turn on virtualization* or *Turn on Windows
features* (administrator prompt and possibly a restart, so it needs
`--allow-ui-effects` and is never part of verification). After a restart for
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
(*Type the code the host shows* when Martlet can't reach the host, *Or type a
code the host shows* next to `PairConsole` otherwise). `PairAddress` and
`PairingCode` take the host's address and short code (`ui_set_text`, so
`--allow-ui-effects`), and `PairHost` pairs; a successful pairing stores a
device secret in Windows Credential Manager, so verification stops at refused
codes. On the host dashboard, *Show a pairing code* (`Step-pair-0`) shows the
address (`HostRunPairAddress`, returned) and the one-use code (`HostRunPairCode`,
never returned) in the run window's `HostRunPairing` panel; the host-runs log
masks codes.
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
isn't running (*ollama*).

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
`LogHostChoice` returns the chosen log host (*Nobody: each computer keeps its
own*, a host ID, or *<host> (not paired with this PC)*); changing it with
`ui_select` changes the shared plan and needs `--allow-ui-effects`.
`LogHostStatus` says what this PC last sent to the log host, what it passed on
and which hosts didn't answer or run an older Martlet. To see lines on a
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

The same page's *Speech bubbles and subtitles* card has the checkboxes
`SetupCharacterSpeechBubbles` (on by default) and `SetupCharacterSubtitles`
(off by default); snapshots return their states, and `SetupCharacterSpeechDisplay`
returns whether each is on and whether bubbles show now (character showing) or
once it is. Ticking either saves `speech-display.json` (the same choices as the
character window's `SpeechBubbleChoice` and `SubtitleChoice`), so `ui_toggle`
needs `--allow-ui-effects`. With the character showing,
`SetupCharacterPreviewBubble` (also `--allow-ui-effects`) sends a sample bubble
to the overlay for a few seconds; `SetupCharacterSpeechDisplay` then says
whether the overlay took it. The bubble itself is drawn by the separate
renderer process, so its text is not in snapshots.

For F5 voices, open `CompanionTab-Voice` (the Voices card shows unless the
voice comes from a cloud provider). `F5VoicesStatus` reads how many included
and own voices there are and which is chosen or in use (an included voice's
name, "one of your voices" or the retired F5-TTS sample). Each included voice's
title `F5VoiceRow-<key>` (for example `F5VoiceRow-arctic-slt`) returns its name
with "· chosen" or "· in use" when it is. Its controls are `F5VoicePlay-<key>`,
`F5VoiceUse-<key>` and, once it is in the list and not in use,
`F5VoiceRemove-<key>`; an own voice's controls use its preset ID (32 hex digits)
instead of the key, and its name is not returned. Use and Remove change the
voice list and need `--allow-ui-effects`; Play plays audio and is not for
automated verification. `f5_voices` reads the same list headlessly.

On Thinking, Voice, Listening and Lip-sync, each "Where it runs" option
(`Place-<page>-<place>`, for example `Place-Voice-Computer` or
`Place-LipSync-Loudness`) only shows that place's choices, so clicking it is
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
`LiveMic` (*Listening*, *Listening paused*, *Can't listen* or *Mic unavailable*
with the reason; while Martlet speaks it reads *Not listening while Martlet
speaks*; *Listening paused* only ever follows a click on it: errors, Stop and
`LiveStop` never pause listening, and listening that can't start yet, such as
Voice ID not set up, reads *Can't listen* and keeps retrying),
`LiveVision` (*Watching*, *Looking*, *Vision paused* or *Can't see*, with when
it last checked the screen; it checks every 3 s), `LiveVisionStatus` (while
vision is on: what it sees, for example *Watching the window behind Martlet*,
then the last look's outcome or why it is holding off, and the looks used this
hour; it never contains window titles)
and Companion › Lip-sync's `LipSyncNow` and `LipSyncNowProblem` (whether this
PC's own Audio2Face service answers), plus, while that own service is the
setting in effect (Martlet's default), `LipSyncOwnTitle` (its title with *in
use*, *not running* or *checking*) and `LipSyncOwnState` (what it does now).
When Thinking runs in Ollama on this PC, the talk window has Ollama load the
model as it opens (and again on activity after a few quiet minutes), and
`LiveStatus` says *Ollama is loading <model> on this PC (N s)…* while it loads,
or why it can't (Ollama not running, model not downloaded, Ollama's own error);
the desktop log records each load's duration
(`{"name":"logs_tail","arguments":{"contains":"Ollama on this PC"}}`).
Opening the talk window with always
listening on opens the microphone; for verification, save a fixed microphone
that does not exist in the disposable data directory, so listening starts,
fails without capturing real audio and shows *Mic unavailable* while it keeps
retrying (it never pauses by itself). The talk window's `LiveStop` (Stop, Esc)
is a passive click: it only stops a reply, recording or vision.

Window discovery uses visible top-level native handles filtered to the attached
process, then verifies ownership around each UI Automation handle lookup.
This avoids transient omissions from UI Automation's desktop-root enumeration
when unrelated WPF windows close. The Martlet main-window automation ID is
still required on every operation; hidden, closed or changed-owner windows
fail rather than falling back to another process. Same-process dialogs remain
available, and duplicate control IDs still fail as ambiguous.

For broader **explicitly authorized** live UI testing, start the MCP server
with `--allow-ui-effects`. This unlocks arbitrary ID-based `ui_click` and
`ui_select`, plus `ui_set_text` and `ui_toggle`. It does **not** waive the
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
- Doctor, `voices_status`, `f5_voices`, `cluster_status`, `logs_tail` and `virtualization_status` calls without a `dataDirectory` get the script's disposable data
  directory, which `-Desktop` also uses, so Doctor sees the desktop's settings
  and `logs_tail` its logs. The directory and the desktop are removed at the end.
- `-KeepDesktop` leaves the desktop running and prints its `-DesktopProcessId`
  and `-DataDirectory` for follow-up runs; stop it and delete the directory
  when done.
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
