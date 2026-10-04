# F04 diagnostic experience

## Diagnostics page and shared logs

**Diagnostics** (in the main window's navigation) shows every line Martlet's
parts wrote on every computer of your [Martlet network](NETWORK.md), newest
first, in one list: each desktop app (`desktop.log`, including each
status-line message as `Status: ...`, hosts that stop or start answering again
and failed provider requests), avatar renderer (`avatar-renderer.log`) and host
runs (`host-runs.log`), each with its rotated older copies, and every Martlet
host's gateway. Filters choose the level (everything, warnings and errors,
errors only), the part (app, character, host runs, host gateway) and the
computer, and a search box matches the text. A PC writes under two IDs: its
desktop app's device ID (`desktop-<name>`) and, when it runs Martlet's host
service, that gateway's host ID (such as `diva-host`). Both are shown as *This
PC* and the *This PC* filter covers both; other computers show their ID.
Selecting a line shows it in full (with the ID it came from, who passed it on,
stack traces and output lines included), and that box's *Copy* button copies
the whole line. Home's *unexpected errors* and *closed unexpectedly* items open
this page. *Copy shown* copies the shown lines; *Open logs folder* opens this
PC's folder. The page refreshes every 15 seconds while open; *Refresh* reads
again and sends nothing.

**Save logs to share** is the easy way to send your logs to someone helping
you. It saves one ZIP (on the Desktop unless you choose another folder; named
like `Martlet logs desktop-diva 2026-10-04 0140.zip`) with every line this PC
has from every computer, whatever the filters show, then shows it in File
Explorer so you can attach it to a message or an issue. The ZIP holds
`about.txt` (when and where it was saved, the Martlet version and Windows, how
log sharing went, each computer and part with its line count and newest line,
the last 24 hours' errors and warnings, and what logs can contain) and
`martlet-logs.txt` (every line once, oldest first:
`2026-10-04 01:40:12.345 -07:00 WARN  gpu-box/gateway: ...`, with *(this PC)*
after this PC's IDs, *via* the computer that passed a line on, and a message's
following lines indented). The status line says how many lines from how many
computers were saved.

**Every computer has everyone's logs.** Your computers form one network, so
there is nothing to choose: logs are shared between all of them. Each
desktop's local files are unchanged (rotated at 2 MiB). Each Martlet host's
gateway keeps a bounded log of its own activity (start and stop, devices
paired, each finished model request with route, device and duration (for a
voice reply also how much speech it made, when its first audio left and, when
the speech after that took longer to make than to play, that it was made slower
than real time and how long the desktop's speakers waited for it at least, so a
host too busy to keep up with its voice shows on every PC's Diagnostics page),
each refused or failed request with its stable code, HTTP status and trace ID,
and, for a voice engine (Chatterbox, F5-TTS, XTTS-v2, GPT-SoVITS, Dia), why its
service failed or refused the reply: its error code, stage and summary (for
example `gpu_out_of_memory` with the CUDA error), the state of its model and
why it isn't ready, or that its reply stream ended unfinished because the
service stopped) together with every line the desktops share with it, in
`logs.json` beside `host.json`. Hosts never talk to each other, so the desktops
carry the lines: every 30 seconds while Martlet runs (companion PCs and PCs set
up as hosts alike), each desktop:

1. reads each paired host's new lines (every computer's lines that host holds,
   its own gateway's included) into its own copy of the other computers' lines,
   `logs\network-logs.json` in Martlet's data folder (the newest 10,000 lines,
   about 4 MB; this PC's own lines stay in its log files);
2. the first time it reaches a host while Martlet runs, asks for its *marks*
   (the newest sequence number it holds per computer and part);
3. gives every host the lines it lacks: this PC's own new lines and every other
   computer's lines it holds, oldest first, at most 4 batches of 1,000 lines per
   host per run (the rest follow on the next run). A host that has nothing yet
   from a computer gets at most that computer's last week.

Every line has a sequence number that only grows per computer and part
(derived from its time), and each host keeps only lines newer than its mark, so
a line sent twice, by several desktops or again after a restart is kept once.
Lines passed on by another desktop record who passed them on. Each host keeps
the newest 6,000 lines (about 1.8 MB) and saves at most every 30 seconds and
when it stops. A desktop that reaches only some hosts still reaches every
computer: another desktop that reaches both passes the lines on. A host that
stops answering only delays lines; they wait on each computer and reach it when
it answers again. The card under the title says how sharing went: *Sharing logs
with 2 of 3 hosts. Waiting for gpu-box. Update old-box to share its logs.
Checked at 1:40 AM.*, or that no host is paired yet (then the page shows only
this PC's logs). The `logs` entry older Martlet desktops kept in the
[shared plan](CLUSTER.md) (their single "log host") is no longer chosen or used;
it stays in the plan so those desktops keep sending their logs to a host where
the others read them, and updating them makes them share with every host.

Logs hold activity, errors and status only: never keys, pairing secrets or
conversation content. They can include local paths, host names and provider
error text, and they travel only over the pinned, signed pairing between the
owner's own computers. Hosts older than shared logs refuse `/martlet/v1/logs`;
the card says to update them. The desktop log also records the
[Martlet network](NETWORK.md) as each PC sees it whenever it changes
(membership, requests to join, who each host is paired with) and which role the
PC runs as at start and on each change.

**Qualification.** Parsing, filters and the page were exercised on this
Windows machine through Martlet MCP (`logs_timeline`, `logs_export` and the
desktop's Diagnostics controls). Sharing was rehearsed with `logs_share_selftest`:
two real gateways on loopback and three simulated desktops with the production
sharing engine, including a desktop that reaches only one host, a host that was
down and the saved copy after a restart; the gateway endpoint (marks,
deduplication, relaying, paging, persistence and refused requests) in-process.
Sharing between real computers over a network is **NOT RUN**.

V06b connects the existing Support engine to the real Desktop
[Troubleshooting surface](TROUBLESHOOTING.md): passive access at each setup
stage, explicit OFF-by-default local metadata recording, exact five-file frozen
preview and default-No destination-bound local export. Doctor help shares its
authored scope/bounds; ordinary status/JSON/exit semantics remain unchanged.
No CLI export or support upload/contact channel is implemented.

V04b adds a separate [real conversation timeline](CONVERSATION.md) in Desktop,
fed by actual capture/STT/policy/runtime/playback state and sanitized credential/
configuration failures. It does not turn ordinary Doctor probes into live
requests. `status` and `run` retain their existing no-network, no-key semantics;
catalog/configuration state is never a live pass.
The executable smoke now also requires unconfigured live Send/PTT disabled
and all live permission/output choices OFF.

This records the **AC-04 local registry integration**, not M1
release qualification or G1 completion. It extends the foundation's entry points without
changing Core contracts, settings serialization, original file bytes or the
provider/PCM schema. The existing foundation document describes the earlier
three-result status; this document describes the F04 replacement.

V02a adds optional typed setup metadata to the existing `settings.load` report:
checkpoint, per-role route/consent/key-reference booleans and pending cleanup
count. Human output and Desktop share that summary; JSON contains no route
IDs, origins or credential IDs. The twelve-probe registry and exit semantics
are unchanged. Saved setup is not live readiness and never resolves secrets.
See [SETUP](SETUP.md) for configuration, migration and explicit vault actions.

V02b's **Audio setup (local only)** is a separate explicit action surface.
It displays real local capture/tone stages and historical checkpoints through
`AudioSetupDiagnostics`, never as a provider or fixture pass. `settings.load`
may include optional sanitized `setup.audio` metadata: selected/default
booleans and historical UTC times/outcomes, no device identities/labels or
configuration revisions. Saved observations are stale for current readiness.
Ordinary status/list/run still do not enumerate or open devices or play a tone;
the audio guides point to separately permissioned Desktop actions instead.
The audio-OFF executable smoke opens this window only to view never-tested
status. See [local audio semantics and unrun gates](SETUP.md#explicit-local-audio-setup-v02b).

## Implemented commands and scope

```powershell
Martlet.Doctor.exe status --json --data-directory C:\Disposable\Martlet
Martlet.Doctor.exe list --json --data-directory C:\Disposable\Martlet
Martlet.Doctor.exe run settings.load application.version runtime.version --json --data-directory C:\Disposable\Martlet
Martlet.Doctor.exe --help
Martlet.Doctor.exe --version
```

Omitting the command still means `status`. Omitting `--data-directory` retains
the existing per-user location. Tests/smokes always specify unique temporary
directories. IDs and flags are exact and case-sensitive; repeated IDs/flags,
unknown IDs, empty `run`, extra arguments, relative/invalid directories and
unimplemented commands return one sanitized invocation-error report.
There is no `self-test`, `--profile`, repair, export or generic shell action.

`status` reports all twelve registered checks. Only settings, application and
runtime callbacks execute. `run` restricts the report and execution to the
named checks: a successful application/runtime run does **not** inspect settings,
test a provider or qualify installation. `list` reads no settings, executes no
callbacks and always returns incomplete catalog-only evidence (exit 2).
The human footer explicitly excludes unselected audio/GPU/cloud/host paths.
JSON stdout remains exactly one `DoctorReport`, including invocation errors.
Ctrl+C signals cancellation and produces the same report rather than a raw
exception. Help/version retain normal success and do not load settings.

| Exit | Meaning |
| --- | --- |
| 0 | Every selected check passed with current observed evidence, and at least one is required; or ordinary help/version succeeded |
| 1 | A real reported failure, callback fault or rejected invalid evidence |
| 2 | Incomplete: warning, unknown, skipped, not configured, stale, stopped, timed out, catalog-only or no required checks |
| 3 | Invalid invocation, or selected settings inspection found invalid/newer/inaccessible configuration; takes precedence over failures |

An optional failed or incomplete **selected** check also prevents green, as in
the foundation. Fixture success describes only explicitly injected test
fixtures, never live readiness.

## Production registry

| Stable probe ID | Automatically executed | Declared effects / result |
| --- | --- | --- |
| `settings.load` | Yes | Local read-only `SettingsStore.LoadAsync`; loaded, first-run, malformed/newer or inaccessible |
| `application.version` | Yes | Local read-only executing application/version evidence |
| `runtime.version` | Yes | Local read-only executing .NET 10 runtime evidence; not SDK, install, host or GPU inventory |
| `provider.connection` | No | Permissioned/network/provider-cost; not configured / not run |
| `audio.input` | No | Permissioned/device; skipped / not run |
| `audio.playback` | No | Permissioned/device; skipped / not run, no audibility claim |
| `pipeline.vad` | No | Local read-only declaration, but no diagnostic callback installed |
| `pipeline.policy` | No | Local read-only declaration, but no diagnostic callback installed |
| `pipeline.stt`, `pipeline.llm`, `pipeline.tts` | No | Permissioned/provider-cost; not wired / not run |
| `host.connection` | No | Optional permissioned/network host/GPU placeholder; not configured / not run |

Stage labels Mic/VAD use existing `Stage.Application`; no shared stage enum was
invented. The desktop maps their stable probe IDs to explicit pipeline labels.
All current automatically runnable definitions declare **only** `LocalReadOnly`.
The executor rejects any other effect combination without invoking its callback,
even when explicitly selected. It is deliberately not a permission/consent engine.

No device enumeration/open/capture/playback, HTTP/DNS/provider/key-store calls,
model pulls, automatic writes/repairs, permission changes or child processes are
performed by diagnostics. File I/O uses the existing settings boundary; a user-
selected remote filesystem, mapped drive or reparse target is **not** a network
sandbox or an offline qualification. Tests use ordinary local temporary paths.
Construction/load never creates a profile.

## Shared API, timing and lifetime

`ProbeRegistry.Local(SettingsStore)` is the single production definition source.
`FoundationStatusService` remains a compatibility wrapper over its `ProbeExecutor`.
Doctor dispatch and `DiagnosticStatusModel` (the actual WPF presentation model)
use that same registry, result contracts and `ReportFormatter.Human`.

Extension seams are `ProbeDefinition`, `ProbeObservation`, `ProbeRegistry.Select`,
`ProbeExecutor.RunAsync`, `Catalog`, `Pending`, `RefreshAge`, `ActiveOperationCount`
and bounded `WaitForIdleAsync`. Definitions and selections have at most 64 unique
IDs. Callback output is a catalog finding ID plus explicit provenance, optional
observation time and settings metadata, **not free-form exception/provider text**.
Unknown finding IDs, impossible provenance and invalid evidence are rejected.
The catalog and collections exposed to callers are read-only snapshots.

Clock and timers use injected `TimeProvider`. Default concurrency is 2 (range
1-8), per-probe timeout 2 seconds (maximum 30), whole-run timeout 5 seconds
(maximum 60), and evidence maximum age 1 minute (maximum 1 day). Only callback
execution uses worker threads; synchronous callback prefixes cannot block WPF.
Monotonic elapsed time decides timeouts, including a callback completing at the
deadline before its timer continuation is scheduled. UTC records observation
age; future observations are unknown, expired evidence is stale, and refreshing
an old report cannot resurrect a stale pass after a clock rollback.

Cancellation and deadlines bound **reporting**, not arbitrary third-party code.
The callback token is separated from the reporting signal: even a blocking
cancellation handler cannot hold the report/UI open. Interrupted results report
`timed_out` or `canceled`, warning/exit 2, not-run provenance, no accepted
observation, and `operation_still_running` when applicable. Slots remain reserved
until the callback **and cancellation handlers actually finish**. An outstanding
callback blocks reruns; concurrent active runs are rejected. Late success/fault
is observed for cleanup but can never mutate or replace the published snapshot.
No claim of cooperative compute cancellation is made.

New optional JSON properties are additive to the existing envelope:
report `started_at` / `completed_at`; probe `diagnostic_code`, `action_id`, `remedy`,
`effects`, `execution`, `started_at`, `completed_at`, `duration_milliseconds`,
`age_milliseconds`, `maximum_age_milliseconds`, `operation_still_running`.
An interrupted probe has start/duration if scheduled, but no fabricated completion
time. Report completion means reporting finished, **not** that an outstanding
callback ended. Catalog entries have no start, duration, observation or age.
Existing strict Unicode/enum/JSON validation remains in force.

`diagnostic_code` is a **Diagnostics-owned finding key**, distinct from
Core `MartletError.Code`. Existing settings error enums are unchanged.
Callback fault/invalid-evidence errors use existing `InvalidContract` plus the
precise diagnostic key; timeout/cancel are incomplete states, not new provider
errors. Future audio errors should map at the adapter boundary after integration.

## Catalog and non-executable remedies

The installed allowlist lives in `DiagnosticCatalog.Findings` / `Remedies`.
Actions are authored descriptive guides only: no command strings, shell execution,
links that change permissions, automatic reinstall, secret collection or uploads.

| Diagnostic code | Outcome | Action ID |
| --- | --- | --- |
| `settings.valid` | Passed (settings only) | `diagnostics.refresh` |
| `settings.first_run` | Not configured | `settings.create` |
| `settings.malformed`, `settings.version` | Failed / configuration exit 3 | `settings.restore` |
| `settings.inaccessible` | Failed / configuration exit 3 | `settings.check_access` |
| `application.available`, `runtime.available` | Passed (process only) | `diagnostics.refresh` |
| `runtime.unsupported` | Failed | `diagnostics.report` |
| `provider.unavailable` | Not configured | `provider.setup` |
| `audio.input_unavailable` | Skipped | `audio.input_guide` |
| `audio.output_unavailable` | Skipped | `audio.output_guide` |
| `pipeline.unavailable` | Skipped | `pipeline.unavailable` |
| `host.unavailable` | Not configured | `host.setup` |
| `probe.not_run` | Skipped, catalog only | `diagnostics.refresh` |
| `probe.running` | Running, not completed evidence | `diagnostics.wait` |
| `probe.effects_blocked` | Skipped, callback never invoked | `diagnostics.refresh` |
| `probe.timeout`, `probe.canceled` | Warning / incomplete | `diagnostics.wait` |
| `probe.busy` | Skipped; outstanding callback owns capacity | `diagnostics.wait` |
| `probe.fault`, `probe.invalid_evidence` | Failed | `diagnostics.report` |
| `probe.unknown` | Unknown | `diagnostics.refresh` |
| `fixture.passed` | Passed, fixture provenance required | `pipeline.unavailable` |

Settings-save guides also retain `settings.reload` and `settings.correct`.
Restoration means first keeping a backup and choosing compatible data/builds;
access guidance explicitly says not to run as administrator or disable protection.
Device guides describe intended input/output and Windows privacy/volume controls
without opening or changing them. Provider/host guides state the missing
capability, future consent boundary and that nothing was contacted.
`fixture.passed` remains an injection/conformance seam, never automatic registry
execution.

## Desktop and evidence limits

The status and pipeline text boxes are focusable/read-only, support keyboard
navigation and screen-reader names, and expose Mic/VAD/STT/policy/LLM/TTS/playback
and optional host states without relying on color or animation. Every node uses
the actual report result with provenance/freshness/age and next-action ID; the
details include the full remedy. A separate polite activity announcement avoids
announcing the entire aging report every second. Refresh is asynchronous;
Stop signals cancellation; overlap is disabled. Generation checks prevent
post-close updates. A one-second UI timer ages existing evidence but never runs
probes or auto-repairs.

The explicit first-run create-unconfigured-profile button remains the sole
settings write. It is disabled while diagnosing/saving and for loaded/invalid/
newer/inaccessible settings. Save uses the original atomic SettingsStore API.
Close cancels work, stops the age timer, waits at most 250 ms for callback
cleanup, queues the final close on a subsequent dispatcher turn (including
startup-error and synchronous-cleanup paths), then WPF's main-window shutdown
exits the process. An uncooperative
in-process callback cannot keep a tray/worker alive; none is launched.
Library consumers must not equate a bounded report with adapter termination.
Future independently hosted workers need their own termination boundary.

Local evidence is the pinned .NET 10.0.401 Windows developer host:
locked restore; Release solution build/tests; real Doctor subprocess smoke;
bounded WPF UIAutomation smoke. Regression coverage exercises the production
registry, exact JSON property set, CLI/model result equality, unique selection,
effect blocking, catalog-only evidence, unknown/future/stale age, total/per-probe
deadlines, caller cancellation, spontaneous cancellation, callback faults,
invalid evidence, blocking cancellation handlers, non-cooperative outstanding
work, eventual completion, late-result discard, duplicate refresh and shutdown.
Configuration smoke preserves malformed Unicode, invalid UTF-8, newer settings
and inaccessible-path behavior and original bytes. Desktop smoke also invokes
real refresh/profile creation, verifies accessible pipeline keyboard focus,
and asserts startup-error display plus clean exit for an invalid relative data
directory. Idle/no-active-work windows also close without reentering WPF's
original closing event.
Slow Stop/cancellation races are deterministic model tests, not injected fake
audio/network UI activity.

Both smoke scripts optionally accept `-ExecutablePath` selecting an existing
`.exe` (launched directly) or `.dll` (launched with dotnet). The path is resolved
and validated, and cleanup targets only that launched PID. Without this option,
the existing Configuration-derived repository output is used, preserving the
default local smoke commands.
For an intentionally relocated build, pass its actual binary explicitly:

```powershell
.\scripts\Smoke-Doctor.ps1 -ExecutablePath (Join-Path $artifacts 'bin\Martlet.Doctor\release\Martlet.Doctor.exe')
.\scripts\Smoke-Desktop.ps1 -ExecutablePath (Join-Path $artifacts 'bin\Martlet.Desktop\release\Martlet.Desktop.exe')
```
