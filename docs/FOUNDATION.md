# Foundation decisions and executable boundaries

**D01 / D02-first-slice / narrow F01, 2026-09-12.** This document records the
implemented foundation, not completion of the broader development plan.
This is the historical first slice; current merged fixture/audio/diagnostic
integration is described in [DELIVERY](DELIVERY.md) and
[DIAGNOSTICS](DIAGNOSTICS.md#offline-fixture-experience-f03c). Statements below
about then-unimplemented projects or commands describe that earlier scope.
Schema ownership stays with the core owner; coordinate shared edits through
the implementation coordinator.

**Current policy, 2026-10-03:** the
[validation policy](../CONTRIBUTING.md#local-only-validation-policy) supersedes this
first slice's hosted-CI configuration and F05 handoff. Every change passes its
targeted tests locally through `scripts\Test-Martlet.ps1` before merge
([Validating changes](VALIDATION.md)); build, package and smoke commands remain
available as optional gates. Historical workflow descriptions below do not
request hosted execution or reclassify past runs as local passes.

**V02a update:** [Resumable setup](SETUP.md) added strict settings v2 with
explicit atomic v1 migration/snapshot, role-scoped destination choices, and
explicit Windows Credential Manager actions. Original v1 reads and profile
references are preserved. The historical "no migration/credential setup"
statements below describe F01, not the current setup implementation.

**V05a settings update:** [Companion personas](COMPANION_REQUIREMENTS.md)
adds strict settings v3 and an inert Desktop editor for named persona text and
response-style weights. Explicit save migrates v1/v2 with an atomic source
snapshot. This does not inject persona text into the current conversation,
choose a style, start listening or authorize provider use.

## Decision disposition

| Decision | Disposition for this foundation |
| --- | --- |
| AD-01 | Accepted: .NET 10 LTS, WPF Windows desktop, eventual self-contained `win-x64`. SDK **10.0.401** is an available official release, pinned without roll-forward in `global.json`. Windows 11 25H2 x64 is the consumer qualification target, not a passed gate. WASAPI/NAudio package selection belongs to F03b/V01; no audio package is added here. |
| AD-02 | Accepted direction: per-user, self-contained ordinary app files, no elevation/runtime prerequisite for end users. Inno Setup remains the first installer candidate; exact builder pin/terms/identity/publish layout belong to F02. Signing, update policy, distribution authorization and clean-machine lifecycle qualification are deferred release gates. |
| AD-03 | Accepted: client-owned orchestration; UI-independent Core, shared Diagnostics, Desktop and Doctor entry points. Later Ubuntu roles remain separately deployed/versioned. No gateway/worker/adapter implementations or empty projects in this change. |
| AD-04 | Accepted: an explicitly selected API route plus separately labeled fixtures; no automatic cloud/model fallback. OpenAI remains the first preset candidate, not an enabled provider, model pin, credential, consent or spending authorization. V02/V03 own those decisions. |
| AD-05 | Deferred to H02: evaluate Ollama first; no model, image, GPU tuple or generic OpenAI compatibility claim. |
| AD-06 | Deferred to H01/H02: Ubuntu 24.04 LTS x64 is a target; no Compose pin, host setup, service installation or qualified machine. |
| AD-07 | Accepted security direction only: paired identity, scoped credentials and TLS for LAN; no unauthenticated LAN shortcut. Protocol, certificate lifecycle and implementation remain H03 gates. |
| AD-08 | Deferred/disabled: F5 weights, auxiliary models and reference-voice rights. No downloads or distribution approval. |
| AD-09 | Deferred/disabled: no required avatar; Live2D distribution/Expandable Application classification remains A01. |

**Project license grant remains an owner decision.** No `LICENSE` is added and
no license is granted over user code/assets. The uncertain Project Airy/AIRI
identity remains unresolved; no upstream implementation/assets are copied.

Build dependencies are centrally pinned and every project commits its NuGet
`packages.lock.json`, including transitive hashes. At this checkpoint, CI was
configured with `--locked-mode`, read-only permissions and commit-pinned actions.
Current local validation retains locked mode. Intentional updates modify
`Directory.Packages.props`, restore normally to update locks, review the diff,
then restore in locked mode and rerun the targeted tests.

| Dependency | Current use / upstream license metadata |
| --- | --- |
| .NET SDK/runtime 10.0.401 SDK line | Build toolchain; official release archive SHA-512 verified for local bootstrap. .NET repository/runtime licensing and bundled third-party notices must accompany later self-contained distribution. No SDK is bundled now. |
| Microsoft.NET.Test.Sdk 18.10.0 | Test-only; package metadata MIT. Includes TestPlatform/CodeCoverage transitive test dependencies. |
| xunit 2.9.3 | Test-only; Apache-2.0 package family, including its analyzer/assertion/execution dependencies. |
| xunit.runner.visualstudio 3.1.5 | Test-only; Apache-2.0 package metadata. |
| NAudio, Inno Setup, model/worker/Live2D packages | Not dependencies of this PR. Exact pins, transitive/native artifacts and notices require review in their owning slices. |

Sources: official [.NET 10 release metadata](https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json),
NuGet metadata for the pinned package versions, and the
[research ledger](RESEARCH.md#desktop-and-packaging). This is a candidate
inventory, not a release SBOM or distribution clearance.

## Projects and dependency direction

```text
Martlet.Core (net10.0; no package, UI, device or HTTP dependencies)
    ^
Martlet.Diagnostics (net10.0; references Core)
    ^                    ^
Martlet.Desktop       Martlet.Doctor
(net10.0-windows)     (net10.0)
```

Two test projects reference Diagnostics and Doctor respectively. Core compiles
without WPF or Windows hardware. It is also the current contract home: a
separate contracts assembly would add no actual boundary yet. No service
container is needed; construction takes an explicit `SettingsStore`.

| Public surface | Intended next consumer |
| --- | --- |
| `Martlet.Core.Contracts.IContract.Validate()`, `ContractJson.Read<T>(ReadOnlyMemory<byte>, int)`, `ContractJson.Write<T>(T, int)`, `ContractException.Code` | Fixture/harness serialization and validation. Invoke validation at adapter ingress, not just in tests. |
| `ContractVersion`, `CorrelationIds`, `Stage`, `EvidenceProvenance`, `MartletError`, `ErrorCode` | Shared identifiers, version and metadata-only diagnostics/errors. Error summaries must be authored user-safe text, never raw exception/provider bodies. |
| `ProviderEvent`, `ProviderEventKind`, `ProviderEvent.IsTerminal`, `TurnResult`, `TurnOutcome`, `SuppressionReason` | F03a event and terminal vocabulary. Suppression carries a reason, not an error. `Text` is explicit content, not diagnostic metadata; do not log event envelopes by default. |
| `ProviderCapabilities.Validate()`, `.RequireFeature(ProviderFeature)`, `CapabilitySupport`, `CancellationCapability`, `ProviderRole` | F03a capability mismatch scenarios. Transport streaming, incremental synthesis and compute cancellation are independent. Unknown is not unsupported or verified support. |
| `Martlet.Core.Audio.PcmFormat`, `PcmEncoding`, `PcmFrame(CorrelationIds, long epoch, long sequence, long sampleOffset, PcmFormat, ReadOnlySpan<byte>)` | F03b in-process PCM sink input; `Data` is owned read-only memory, `SamplesPerChannel` and `Format.BlockAlignment` are derived. No audio devices are opened by these types. |
| `Martlet.Core.Settings.AppSettings.CreateUnconfigured()`, `ProfileSettings`, `ProfileKind`, `SecretReference`, `SettingsJson.Read(ReadOnlyMemory<byte>)` | F02 data-preservation boundary and later V02 onboarding. No credential resolution or startup consent is implemented. |
| `SettingsStore(string absoluteDataDirectory)`, `.DefaultDataDirectory()`, `.LoadAsync(CancellationToken)`, `.SaveAsync(AppSettings, string? expectedRevision, CancellationToken)` | Shared local settings store. `SettingsLoadResult` exposes state/settings/revision/error; `SettingsSaveResult` exposes saved/revision/error. |
| `Martlet.Diagnostics.ProbeResult`, `ProbeOutcome`, `EvidenceFreshness`, `DoctorReport`, `DoctorExitCodes.Evaluate(DoctorReport)` | F04 shared result vocabulary; no registry/remedy catalog yet. Exit evaluation validates the report before calculating readiness. |
| `FoundationStatusService(SettingsStore).GetReportAsync(CancellationToken)`, static `.ApplicationVersion`, `ReportFormatter.Human(DoctorReport)` | Current Desktop/Doctor shared status and future F04 replacement seam. No inference or hardware services are constructed. |
| `Martlet.Doctor.DoctorCommand.RunAsync(string[], TextWriter, CancellationToken)` | Actual CLI dispatch/output/exit-code path, covered directly and through the executable. |

## JSON compatibility and bounds

The C# models plus production validators and `contracts\golden` are the
executable first-slice schemas; they are **not** a frozen remote gateway wire
protocol. JSON is UTF-8, snake_case property names and named snake_case enums.
Required fields/nullability are enforced. Enum input must be one exact,
case-sensitive declared snake_case token; composite names, whitespace/case
aliases, numeric strings and integer/unknown values are rejected. JSON escapes
that decode to the exact token remain valid. Malformed UTF-8 or unpaired
surrogates in property names and string values (including ignored optional
fields), duplicate properties, comments, trailing commas/content and depth
over 16 are rejected with sanitized errors.
Output is indented; callers must not rely on whitespace/property ordering.

Provider/capability/report version major must equal 1; minor 0-9999 accepts
unknown optional fields. Unknown fields are discarded, not preserved.
Persisted settings/profile versions must equal 1 and **reject unknown fields**
to avoid silently deleting a newer application's settings on save.
`SettingsJson.Read` checks versions before strict field decoding.
Derived `is_terminal`/PCM alignment are not serialized. Report `exit_code` and
`ready` are recomputed on read; supplied values never override result evaluation.

| Bound | Enforced behavior |
| --- | --- |
| JSON / settings | At most 256 KiB per contract document; settings at most 128 KiB. Settings reads stop at limit + 1 before decoding. |
| IDs | Nonempty UUID session/turn/request/profile/credential IDs. Provider/model/adapter/action/probe tokens: 1-64 ASCII letters/digits/dot/underscore/hyphen, starting with a letter/digit. These are internal aliases, not arbitrary upstream model names or URLs. |
| Text / summaries | At most 16,384 UTF-16 characters per event text and 512 per error/probe summary; no control characters except CR/LF/tab. JSON byte cap also applies. |
| Sequence / epoch | 0 through signed 32-bit maximum, held in `long`. These are per-attempt/turn labels; temporal ordering is not yet enforced. |
| PCM | Declared signed PCM16 little-endian only, 16/24/44.1/48 kHz, 1-2 channels; nonempty aligned frame, at most 100 ms and 19,200 bytes. Sample offset is per channel, nonnegative, with overflow rejected. Input is copied after validation. |
| Audio completion | Optional `final_sample_count` only on completion: 0-4,320,000 samples per channel (90 seconds at 48 kHz). No zero-length end frame; terminal event is separate. |
| Provider / profile / report | Input byte capability 1-16 MiB; at most 16 unique provider credential references; at most 64 unique probe IDs. No arbitrary metadata dictionaries. |

An emitted provider event/turn result must be fixture or live. Unknown/not-run
are supported for capability/probe state but cannot manufacture a pass.
Capabilities have no `ready` flag: a known format/feature is not readiness.
Errors must be normalized by future adapters; accepting a summary string is
not automatic redaction of arbitrary provider input.

**Remaining D02 / AC-01 work:** temporal state machine, response/request role
correlation and deadlines, deduplication/out-of-order handling, epochs and
late cancellation discard, stream EOF/truncation detection, aggregate text/
audio/queue bounds, golden multi-event traces, SSE parsing and binary audio
header widths/framing, role-specific request payloads/limits and remote version
negotiation. F03a should own the deterministic event-sequence validator and
fixtures first. Gateway-specific schemas/framing stay separate. AC-01 is
partially covered, not frozen or fully passed by this PR.

## Settings and safe launch

Default data location is `%LocalAppData%\Martlet\settings.json`; constructors
and both entry points accept an explicit absolute data directory. Nothing is
created on load/launch. Missing settings below a usable existing ancestor are
`FirstRun`; malformed/newer/inaccessible files yield explicit errors and no
invented defaults. User-safe remedies appear in the same CLI/UI status.

The only UI write is **Create unconfigured local profile**, enabled on first
run. It creates a profile identifier, `not_configured` kind and zero credential
references; it does not grant consent or connect anything. `SecretReference`
stores an internal provider alias and an opaque UUID only. Future V02 must
define Credential Manager resolution, approved-origin binding, rotation/deletion
and destination-specific consent before using these references.

Saves validate first, take an exclusive cooperating-writer lock, reload and
compare a SHA-256 revision, write/flush a same-directory temporary file, and
atomically create/replace the destination. Null revision means create-only.
Conflicts require reload/review; corrupt/newer files are never overwritten by
this API. The small persistent `settings.json.lock` file is not profile data.
Failed staging is cleaned up; replacement failures do not truncate the original.
No migration/backup/rollback is implemented. The lock coordinates Martlet
writers, not arbitrary external editors; manually edit only while Martlet is
closed. Network filesystems, hard power-loss durability and hostile concurrent
filesystem manipulation are not qualified by these tests.

Closing the main window exits. There is no tray, startup registration, microphone
enumeration/capture, speaker playback, network access, credential access,
recording, memory, avatar, login, elevation or environment modification in the
application. There is deliberately no fake Start Conversation command.

## Doctor scope and exit semantics

Implemented: `[status] [--json] [--data-directory ABSOLUTE_PATH]`, `--help`, and
`--version`. Unknown/repeated flags, nonexistent commands (including `self-test`
and `--profile`) are invalid, not stubs. Settings determine the active profile;
there is no multi-profile selector yet. JSON is one `DoctorReport` on stdout,
including invocation errors; no raw supplied arguments, file contents, secrets
or local paths are echoed. Human-readable status identifies the same probes.

Exit 0 means requested checks passed (or a help/version command completed),
1 means a reported probe failed, 2 means incomplete/degraded/unknown/skipped/
stale/no-required-check state, and 3 means invalid invocation/configuration or
inaccessible settings. Configuration errors take precedence. Failed and
incomplete optional probes also prevent a green report. `fixture.passed` can
only describe injected test fixtures, not live readiness.

The present `status` always remains non-ready: `settings.load` inspects the
actual local file with live **settings-only** provenance; `provider.connection`
is not configured/not run; `audio.playback` is skipped/not run. There are no
real provider/device probes and no fixtures pretending to be AI.

## Validation scope and next ownership

Local foundation evidence uses SDK 10.0.401 on the existing Windows development
host: locked restore, Release solution build including WPF, unit/contract tests,
actual CLI JSON/exit paths and `scripts\Smoke-Desktop.ps1` UI Automation first-run
launch/close with an isolated temporary data path. The smoke reads the actual
accessible status control, enforces a timeout and confirms launch writes no data.
It neither opens devices nor changes firewall/permissions to emulate denied
egress. At this checkpoint, hosted CI was configured for build/tests/CLI, not an
interactive desktop smoke. Those commands now belong only to local validation.

**Not evidenced:** clean consumer Windows without .NET, installer lifecycle,
screen-reader/keyboard usability with actual users, real audio/audibility,
denied-egress environment, provider requests, Ubuntu, GPU, signing or release
qualification. No G1/G2/release gate is passed from these unit tests.

| Owner after merge | Primary paths and coordination |
| --- | --- |
| F02 packaging | New `packaging\windows` and publish profiles; consume existing Desktop/Doctor projects and preserve the documented data path. Coordinate any project/solution changes. |
| F03a fixtures | In-process fixture adapters/harness use `Core\Contracts` vocabulary. Add stateful conformance rather than redesigning settings. Coordinate shared contract/solution additions. |
| F03b PCM sink | New `src\Martlet.Audio` plus focused tests; reference Core and consume `PcmFrame`/`PcmFormat`. Select a real isolated adapter, no duplicate audio contract or settings store. |
| F04 diagnostics | Own `src\Martlet.Diagnostics`, Doctor, Desktop status integration and relevant tests; extend result semantics with a registry/remedies/deadlines/freshness calculation. This slice does not already implement F04. |
| Coordinator/shared owner | `Martlet.slnx`, `Directory.*.props`, `global.json`, `NuGet.config`, `.github\workflows`, `src\Martlet.Core`, `contracts\golden`, shared docs and lock updates. Serialize overlapping changes through the parent. |

The original F05 handoff proposed expanded hosted fixture/packaging/artifact
lanes after this early build/unit-test/CLI gate. The current policy supersedes
that plan: F05 retains local fixture/packaging/artifact checks and independent
review, with no replacement remote validation.
