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
Martlet includes (key, name, description, licence, transcript, SHA-256, sample
rate and duration; each clip is checked against its SHA-256 and the reference
store's audio, name and transcript rules, `valid` or the failure) and the
`default` key. From a data directory (optional absolute `dataDirectory`, default
the current user's) it reads the `f5-voices` list: `state` (`none`, `loaded`,
`busy` while the desktop holds it, or `unreadable`), the number of voices, the
keys of included voices in it, the count of the owner's own voices, whether the
retired F5-TTS example clip is still there and the applied voice (an included
key, `own`, `retired-sample` or null). It never returns own voices' names,
transcripts or audio, plays nothing and contacts nothing.

`logs_tail` reads the last `lines` (1-400, default 100) of one local log under
`<dataDirectory>\logs` (`log`: `desktop` (default), `avatar-renderer` or
`host-runs`), optionally only lines that `contains` some text (case-insensitive,
at most 200 characters). It returns `{log, exists, truncated, lines}` and never
writes, rotates or deletes a log. Failed provider requests appear in the desktop
log with their endpoint, model, HTTP status and the provider's own short
explanation, followed by a `Reply failed (...)` line naming the route, for
example `{"name":"logs_tail","arguments":{"contains":"failed"}}`. Logs can
include local paths and provider error text (never keys or conversation content).

To drive the visible desktop, start `Martlet.Desktop.exe` yourself in the **same
interactive Windows session** (ideally with a disposable `--data-directory`).
Call `ui_connect` with that process ID. `ui_snapshot` returns window accessible names,
automation IDs, enabled states, checkbox states, and selected read-only status
fields (a text block's text, or a button's accessible name); it does not dump arbitrary editable fields or credentials.
Status fields include `VisionStatus` (Companion › Vision: whether the Thinking model can see, or has been retired, and the fix), `SetupCloudHint-Thinking` (the cloud provider's recommended Thinking model, or a retired-model warning), `SetupLocalRecommendation` (the local Ollama model recommended for this PC's graphics card), `SetupProviderHint` (Setup › Jobs prefilled model) and `SetupF5About` (Speaking › This PC: what setting up F5 installs and its licence). `SetupHostThisPc` and `SetupUseLocal-Speaking` start the F5 setup run window straight away (no extra confirmation; installing Docker Desktop still asks for its terms), so they need `--allow-ui-effects`. `ui_click` invokes a control by automation ID and `ui_select` selects a named combo-box
option. By default only passive navigation and
diagnostics controls can be clicked. The main window is split into pages, and a
page's controls are only visible after you open it: click `NavHome`,
`NavDevices`, `NavCompanion` or `NavSettings` first (for example
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
these status texts, as does the talk window's `LiveStatus` (the line under "Martlet": what it is doing, or why the last reply failed). Each voice's controls are numbered by voice (`PeopleName-3`,
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
roles), and Settings for all devices holds `CheckHosts`, `ClusterSync`,
`ClusterStatus` (returned as text) and `RoleSetup-<role>` for jobs nobody does.
Use `ui_snapshot` again to observe asynchronous effects. Modal
actions may return `completed: false` while their dialog remains open; this
means the invoke is still pending, not that the action finished.

For the desktop character, open `CompanionTab-Character`; with
`--allow-ui-effects`, `SetupCharacterToggle` shows or hides it and
`SetupCharacterZoomIn`, `SetupCharacterZoomOut` and `SetupCharacterResetZoom`
zoom its overlay. The `SetupCharacterView` status then reports the overlay's
size, its distance from the top of the screen, the camera zoom and where the
top of the character's head sits relative to the overlay's top edge (it must
stay in view at every zoom).

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

Status fields include the talk window's `LiveStatus` (its status line) and
`LiveMic` (*Listening*, *Listening paused* or *Can't listen* with the reason),
and Companion › Lip-sync's `LipSyncNow` and `LipSyncNowProblem` (whether this
PC's own Audio2Face service answers), plus, while that own service is the
setting in effect (Martlet's default), `LipSyncOwnTitle` (its title with *in
use*, *not running* or *checking*) and `LipSyncOwnState` (what it does now).
Opening the talk window with always
listening on opens the microphone; for verification, save a fixed microphone
that does not exist in the disposable data directory, so listening starts and
fails without capturing real audio.

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
- Doctor, `voices_status`, `f5_voices` and `logs_tail` calls without a `dataDirectory` get the script's disposable data
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
