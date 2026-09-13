# F04 diagnostic experience

This is the **AC-04 local registry / CLI / desktop subset**, not F03c, M1 or G1
completion. It extends the foundation's diagnostic entry points without
changing Core contracts, settings serialization, original file bytes or the
provider/PCM schema. The existing foundation document describes the earlier
three-result status; this document describes the F04 replacement.

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
the foundation. Fixture success describes only the explicitly injected fixture
check, never live readiness.

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
without opening or changing them. Provider/host guides state the missing capability,
future consent boundary and that nothing was contacted. `fixture.passed` is an
injection/conformance seam only; no production fixture runner is installed here.

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
the existing Configuration-derived repository output is used, preserving CI.
For an intentionally relocated build, pass its actual binary explicitly:

```powershell
.\scripts\Smoke-Doctor.ps1 -ExecutablePath (Join-Path $artifacts 'bin\Martlet.Doctor\release\Martlet.Doctor.exe')
.\scripts\Smoke-Desktop.ps1 -ExecutablePath (Join-Path $artifacts 'bin\Martlet.Desktop\release\Martlet.Desktop.exe')
```

**Not run / not qualified:** clean consumer OS, denied-egress sandbox, actual
screen-reader/novice usability trial, mic/speaker/audibility, real provider or GPU,
full fixture-to-PCM session, installer lifecycle, signing/distribution. F03c must
explicitly register fixture/audio results after sibling merges; no compile-time
reference to absent projects or competing fixture/audio provider is added.
Do not claim AC-04 as fully integrated, M1/G1, or live full-path success.
Logs/support bundles/retention/export remain V06, not an implicit feature.
