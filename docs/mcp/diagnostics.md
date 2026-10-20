# MCP: Diagnostics and logs

Part of [MCP in Martlet](../MCP.md): the tools and screens of the *Diagnostics and logs* area of `martlet_guide` (Doctor, logs, latency and the Linux and macOS companion app). The [index](../MCP.md#pages) lists the other pages.

## Local MCP control (Windows)

The headless tools `doctor_status`, `doctor_list` and `doctor_run` call the
production Doctor implementation in-process. Status and selected probes are
local read-only checks. They return Doctor's structured JSON report and exit
code; nonzero exit codes describe incomplete, failed or invalid results, not a
passed check. An optional absolute `dataDirectory` argument isolates settings
reads; without it, Doctor uses the current user's Martlet directory. Apart from
the character and settings tools, which save only with `--allow-changes`, no
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

`doctor_status` (and `doctor_run` with `planning.catalog`) also says where
Recommended setup's options come from, by reading files only:
`planning.catalog_daily` (the model catalog's daily copy and its local facts,
plus Martlet's own list), `planning.catalog_snapshot` (the snapshot shipped
with Martlet, because no newer daily copy is on this PC) or
`planning.seed_only` (a Warning: the catalog can't be read, so only Martlet's
own list). It says `planning.catalog_refresh_failing` (a Warning) when the
daily refresh tried in the last week, a source failed, and no refresh read
every source for more than seven days (`model-catalog-status.json`): Martlet
plans with the last good copy. `recommended_setup_status` gives the details
for each option, and `model_catalog_status` the refresh's details.

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
is in [Voice latency](../VOICE_LATENCY.md#measure-first-the-reply-latency-line).

`thinking_trace` reads the desktop log's
[Thinking trace](../VOICE_LATENCY.md#thinking-trace-each-thinking-request-step-by-step)
(optional absolute `dataDirectory`, default the current user's) and puts it
back together. `newest` holds the newest `turns` (1-200, default 10)
conversation turns, newest first. Each turn has its `name` (*Thinking turn 12
(reply)*), `purpose`, `route`, `model`, `state` (null while it has no end
line), `totalMs`, `firstWordsMs`, `firstAudioMs`, the number of `requests`,
`waits` (each *still waiting* notice: `atMs`, `what` and `waitedMs`) and its
`lines` in order. `newestPoolRequests` holds the newest requests for the
Thinking pool's slots (`id` such as `tr-3`, `kind`, `holder`, `state`,
`totalMs`, `waitedMs`, `ranMs` and `lines`). The counts are `turns`,
`unfinishedTurns`, `stillWaitingLines` and `poolRequests`. Turn numbers and
request IDs start again each time Martlet starts, so a *started* or *waits in
line* line begins a new one. With `contains`, only the turns and requests that
have a line with that text are kept. It is read-only, never shows what was
said, and starts no audio, network or provider request:
```powershell
.\scripts\Invoke-MartletMcp.ps1 -Calls '[{"name":"thinking_trace","arguments":{"turns":5,"contains":"still waiting","dataDirectory":"C:\\Users\\me\\AppData\\Local\\Martlet"}}]'
```

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

`logs_timeline` reads the logs as the desktop's
[Diagnostics page](../DIAGNOSTICS.md#diagnostics-page-and-shared-logs) shows
them (optional absolute `dataDirectory`, default the current user's): this PC's
`desktop`, `avatar-renderer` and `host-runs` with their rotated copies and the
other computers' lines log sharing collected (`logs\network-logs.json` and its
`logs\network-logs.journal`, a week of lines: other
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
`outputPath` (required; absolute, ending `.zip`, in an existing folder on this
PC, never a network path; an existing file is never replaced) and optional
`dataDirectory`. It writes a file, so it needs `--allow-changes`. It writes the
same ZIP with the production writer (`LogBundle`): `about.txt` and
`martlet-logs.txt` with every line once, oldest first, from this PC's logs and
`network-logs.json` (with its journal). It returns `path`, `bytes`, `files` (name and size),
`lines`, `computers`, `errors` and `warnings` (last 24 hours), `oldest`,
`newest`, `network` and `about` (the text of `about.txt`). It writes only that
file and contacts nothing.

`logs_share_selftest` (no arguments) rehearses shared logs end to end with the
production code (`src\Martlet.NodeLinkCheck`, mode `logs`, `LogRehearsal.cs`;
returns `{exitCode, report}`): two real gateways (`lab-logs-1`, `lab-logs-2`;
Kestrel, pinned TLS, signed requests, an in-memory `logs.json` and
`logs-recent.json`) and three
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
host voice (for example Chatterbox Nano in this PC's host service), `ui_click` `HomeListen` (with `--allow-ui-effects`) runs whole
spoken turns; `latency_report`, `logs_tail`, `hearing_check`'s `lastTurn` and
`conversation_history_status` show what happened.

A real microphone's problems are in the desktop log, never its name or what it
heard. The *Always listening: the microphone failed* warning gives the error
and the device's own reason (for example *Windows removed it*, *Windows error
0x88890004*, the format the microphone gives and the property changes Windows
reported on it first). *Microphone:* lines name each property Windows changed on
the microphone in use, once per property per run; such a change alone no longer
stops listening ([Capture](../CAPTURE.md)). Read them with
`{"name":"logs_tail","arguments":{"contains":"icrophone"}}`. The fixture
microphone never fails or changes, so only a real one shows these lines.

For broader **explicitly authorized** live UI testing, start the MCP server
with `--allow-ui-effects`. This unlocks arbitrary ID-based `ui_click` and
`ui_select`, plus `ui_set_text` (an empty `text` clears a field), `ui_type`,
`ui_toggle`,
`ui_set_range` (sets a slider to `value` within its range and returns its value
and range, such as Companion › Voice's `VoiceVolume`, 0 to 100)
and `ui_move` (moves a control that UI Automation can move, such as the
character overlay's `MoveAvatar`, by `dx`, `dy` screen pixels). `ui_type` types
the way a person does, one key at a time: with `id` that text box takes the
keyboard focus first; without `id` the keys go to the control that has the
focus. Each character of `text` (at most 100), then each of `keys`
(`backspace`, `delete`, `left`, `right`, `up`, `down`, `home`, `end`, `escape`; `up` and
`down` move through a list such as the Devices list), is a key message posted
to that Martlet window only (`window` names one; the main window by default),
one every `stepMs` (default 50). It returns `focused`, the automation ID of the
control that has the keyboard focus afterwards. `ui_snapshot` marks that control
`focused: true`. So a test can type, let the page draw again and type on without
clicking, and see whether the box kept the typing. It does **not** waive the
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
with the catalog's reason) and the engines kept. `sync` reports how the companion keeps
its settings the same as the owner's other computers (#980): on or off, its device
ID, its paired hosts and, for `thinking`, `listening`, `speaking` and `companion`,
which computer changed it last and when (from `dataDirectory`, default the
companion's own data folder), plus what stays on that computer. Never a key. The
companion's Settings tab shows the same in `SyncStatus` (a safe value). No audio,
network or window.

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
