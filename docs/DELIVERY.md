# Delivery, acceptance, and release plan

**Implementation ledger plus future gates.** The internal fixture and explicit
API integration below
is implemented; this is not a supported AI companion or passed release gate.
Read [scope and decisions](../DEVELOPMENT_PLAN.md), [contracts](ARCHITECTURE.md),
[installation/support](INSTALLATION_SUPPORT.md), and [research](RESEARCH.md).
The original empty baseline is historical. Current implementation and evidence
must not be confused with the broader planned milestones below.

**Current validation policy, 2026-09-13:** follow the
[local-only repository policy](../README.md#local-only-validation-policy).
Earlier hosted-CI plans are superseded, not evidence of local success. All
test, smoke, package-integrity and qualification gates below run locally in
suitable authorized environments, with independent review and normal merge
requirements. Missing native/OS/model/hardware evidence remains NOT RUN or
blocked for that qualification; independent local development can continue.
No replacement Action or release is requested by this plan.

**Requirements expansion, 2026-09-19:** [Companion controls and behavior](COMPANION_REQUIREMENTS.md)
defines R19-R24 and AC-21-AC-26: editable personas, F5 reference-voice replacement,
independent LLM/VLM choices, listen-first context, VAD-driven interruption and
response-style weights. All are **planned / not accepted as complete**. Existing
policy, settings, cancellation and standalone VAD foundations do not close
these new end-user requirements. Native VAD holds and local-only qualification
rules remain unchanged.

### Current internal implementation

**Canonical local-memory reuse:** the memory-only delta of `b80920a` and the
memory named-argument correction from `68bd6c8` are adapted onto PR #44, not
copied as old project snapshots. Schema 3 remains companion; schema 4 adds
OFF-by-default local-memory policy and Desktop management/retrieval. PR #39
finalization-time expiry/top-K regressions and PR #44 exact snapshot limits,
legacy-v2 receipts, mandatory General/CompanionOnly native smokes and wrapper
failure propagation remain required. No avatar/listen-first, Gateway,
installation schema, remote-memory or host-action integration is imported.
AC-16 and installation AC-18-AC-20 remain unchanged; source companion AC-18-23
map to canonical AC-21-26 (source H07b barge-in remains canonical H07c).
Historical package hashes elsewhere in this ledger are not evidence for this
candidate. Its exact local commands, results and review are recorded in its PR.

| Item | Actual status |
| --- | --- |
| D01 | Dispositions recorded in [FOUNDATION.md](FOUNDATION.md). .NET/WPF/core direction and pins chosen; license, distribution/signing, provider spending and optional model/SDK rights remain deferred gates. |
| D02 | Versioned settings/profile/provider contracts, bounded PCM, production JSON and temporal text validators, explicit refusal, epochs/cancel/EOF/deadlines and golden traces exist. Role request bodies, remote schemas/SSE/binary wire framing remain; **AC-01 is partial, not frozen or fully passed**. |
| F01 | Offline accessible text/status shell, explicit unconfigured-profile save, atomic validated settings and truthful shared-status CLI implemented. Local developer-host build/tests/CLI and bounded desktop-launch scope are documented; no clean consumer-OS claim. |
| P03c local memory | [Consented local memory](MEMORY.md): strict schema-4 policy, explicit enable/save/inspect/edit/delete/purge/frozen export and next-turn-only bounded retrieval using the existing single-owner library. No transcript ingestion, remote memory, embedding or installation activation. v1-v3 migration keeps exact originals; v1-v4 recovery excludes facts and forces memory OFF. Desktop/root/package graph includes Memory with regenerated normal/RID locks. AC-16/G4, clean-machine, real-user usefulness and physical storage failure remain unqualified. |
| F03a/F03b/F04 | Merged deterministic 30-script fixture engine, bounded real PCM sink/WASAPI adapter and local diagnostic registry/CLI/WPF shell. Headless evidence is not actual listening or install qualification. |
| F03c | Shared `FixtureSession` now joins production cursor/validator, ordered synthetic text, optional actual sink, shared status and accessible Desktop/Doctor commands. Ten novice scenarios, per-action tone permission, Stop/fresh IDs/epochs, no default device/network/write effects. |
| F05 integration | Normal solution includes fixture projects/tests and session tests; portable/Windows Doctor executable smokes remain required local checks with the pinned SDK. Packaging includes actual fixture/audio references, separate normal/RID locks, notices and audio-OFF native fixture smokes. |
| V02a | Configuration-only [Setup / resume](SETUP.md): fixture or named OpenAI routes, exact per-role destination selection, strict v2 settings with explicit v1 snapshot migration, and real scoped Windows Credential Manager code. No provider registration/calls, fake completed voice setup or persisted per-turn spending permission. Native wrapper is fake-boundary tested, not OS-vault roundtrip qualified. V01b/V02b/V03/V04 live/audio integration remains. |
| V02b / V05 / V04a | Reviewed local audio choices/tests, deterministic participation policy and reusable typed text-to-voice runtime are merged foundations; their internal fixture evidence does not qualify a real account or device. |
| V01b standalone | [Post-capture voice activity library](VAD.md): bounded managed window/endpoint/ownership path with an internal pinned CPU classifier; normal public native initialization fails closed before consent consumption or data/native access. No app/solution/payload integration or PTT change. Unfiltered tests are managed/fake-native only; the new classifier's native execution remains pending, production privacy/eligibility blocked, and speech/device/performance/V01/G2 unqualified. |
| V05a companion settings | [Companion personas](COMPANION_REQUIREMENTS.md#r19-editable-persona-profiles): strict settings v3 adds up to 16 named persona profiles, bounded editable text, validated helpful/sarcastic/silly/distracted/playful-teasing weights, fresh per-persona revisions and explicit create-only UTF-8 export/import. Desktop uses the app-shared effect owner; v1/v2 migration keeps an atomic original, v3 configuration recovery includes personas and older snapshots preserve current personas. This is inert configuration only: no runtime prompt/style selection, automatic listening, model call, capture or provider permission. |
| V05b persona runtime | Explicit typed/PTT turns snapshot the active persona revision after participation accepts, select one dominant style from validated weights with an injectable production selector, and include both in the existing bounded LLM request. Combined persona/style/user bytes and token reservation fail closed without truncation or provider access; settings are revalidated before credential disclosure. No conversation history, automatic listening, response-frequency change or new permission is added. |
| V02c LLM model selection | Setup exposes exact local model catalogs for each named OpenAI role and validates an applied model/voice before replacing the working draft. The LLM adapter accepts pinned `gpt-4.1-mini-2025-04-14` and `gpt-4.1-2025-04-14` Responses snapshots; a changed route clears destination consent and each fresh action/credential/request binds the saved exact model. No discovery, probe, fallback, VLM route or live account claim. |
| V05b explicit context | The explicit Desktop conversation retains only completed typed/PTT user/assistant pairs in a bounded eight-turn, 16 KiB, two-minute in-memory buffer. Fresh authorization includes and budget-trims whole oldest pairs with persona/style/current input; failed/refused/suppressed turns are excluded. Pause, lock, configuration load/change, Stop and conversation close clear it. No automatic listening, unsolicited dispatch, persistence or memory service. |
| V04b | [Explicit Desktop API conversation](CONVERSATION.md) joins saved route/vault bindings, local audio policies, typed or <=25 s PTT, named STT, pre-dispatch policy and streaming LLM/optional TTS/PCM sink. One app-shared effect owner, fresh bounded per-action envelope, exact reservations/role permissions, original cancellation/expiry and ownership quarantine. Text-only sends no TTS and opens no output. Root graph now includes existing Providers/Conversation/Participation and their suites; dedicated local project commands remain. |
| V04b Stop accessibility | Conversation Stop remains above the scrolling form; window-local Escape uses the same discard/revoke path from input, response or held PTT. Unused permissions can also be revoked without starting work. Existing exact-operation ownership, cleanup, retained text and fresh-consent rules remain. In-process WPF/controlled-boundary regression coverage is not physical Stop latency, native apphost or device qualification. |
| V06b | [Desktop Troubleshooting](TROUBLESHOOTING.md) integrates the merged Support engine: passive shared status, explicit OFF-by-default local journal, bounded typed live/fixture metadata, exact five-file preview, default-No destination-bound create-only ZIP and retained cleanup ownership. Support projects join the solution/Desktop/package graph. Doctor help is read-only; no CLI export, upload or support contact channel. Internal functionality, not G2/support-service qualification. |
| V07a | [Local configuration recovery](TROUBLESHOOTING.md#local-configuration-backup--restore-v07a): bounded versioned integrity envelope of exact validated settings; explicit Desktop create-only backup and immutable default-No restore preview; existing valid same-profile v1/v2/v3 only; v3 restores personas while older sources preserve current personas; unbound imported keys, current owned cleanup retained; fresh byte-exact pre-restore original and shared writer/effect ownership. Not portable profile import, corrupt-store repair, binary rollback or clean-VM qualification. |
| V07b recovery coordination | [Private rollback configuration restore](../src/Martlet.Updates/SELECTION.md#explicit-rollback-configuration-restoration) joins the recorded signed rollback selection to actual V07a restore under the supplied shared effect runner and Core writer/source/original/result ownership. Separate bounded one-use consent and explicit v3 fence; a distinct [fresh-consent acknowledgment](../src/Martlet.Updates/SELECTION.md#fresh-consent-acknowledgment-of-a-recorded-restore) handles only a valid recorded commit with authoritative pending selection, no conflicting publication and verified present settings, without restoring/writing settings again. New v4 history preserves the earlier interrupted operation; unproven/ambiguous cases still require manual reconciliation. Library surface only: no Desktop wiring, executable activation, runnable receipt, shipped rollback workflow or overall V07b completion. |
| V07b payload compatibility | [Signed-candidate metadata verification](../src/Martlet.Updates/README.md#signed-format-1--byte-contract) accepts current production-shaped internal v2/provenance-v1/CycloneDX 1.6 metadata through real staging and retained verification, while explicitly preserving supported legacy v1 bytes/history. Separate 16 MiB v2 internal metadata caps do not widen signed-envelope, receipt, depth or ZIP policy. Actual producer-constructor/serializer conformance uses inert ephemeral-signed fixtures; authenticated declarations and implemented consistency checks are not full provenance/build/SBOM qualification, publisher authorization or executable readiness. No app graph, launcher or activation change; receipts remain non-runnable. |
| V07c foundation | [Offline package evidence](../packaging/windows/README.md#offline-provenance-and-cyclonedx-sbom-v07c-foundation): required unsigned source/tool/locked-dependency provenance and CycloneDX 1.6 SBOM integrated with the existing actual-file manifest and installer handoff. No publisher attestation, license clearance, vulnerability scan, signed release or lifecycle qualification is implied; overall V07c remains incomplete. |
| H02a foundation | [Standalone offline artifact inspection](../src/Martlet.HostArtifacts/README.md): strict bounded v1 role/runtime/artifact metadata, source/pin/dependency/license checks, deterministic known-byte/prerequisite inventory and an explicit-local-document CLI. One incomplete Ollama/F5/Vocos five-file catalog preserves published identities versus Git blob IDs, missing content hashes, unreviewed rights and unknown runtime/image/host/GPU/disk facts. All candidates remain disabled; no downloads, execution, host probing, settings or app/package integration. H02 backend/runtime closure and H01/H04/H05/H06/G3 qualification remain separate incomplete gates. |
| H02b wire contract | [Named Ollama native chat adapter](../src/Martlet.Providers/OLLAMA_CHAT.md): actual bounded HTTP encoder/NDJSON path, canonical literal-loopback origin, required constructed `:local` selector, exact-action one-use trusted-caller permit/dispatch lease, original cancellation/deadlines and owned cleanup/quarantine. Controlled in-process fixtures only; no production issuer, app/gateway route, model management, immutable weights guarantee or runtime/host/locality attestation. Core/OpenAI/catalog authority is unchanged. H02 backend/model/host qualification and H01/H03/H04/H05/H06/G3 remain incomplete. |
| F02 / G1 onward | F02 merged at `8a762cd930741208daa61e9df9b8d860cf4768de` and was normally integrated, not copied. Self-contained payload/unsigned installer build paths exist. Clean-VM lifecycle, physical audio, real AI/GPU, Ubuntu, signing, rights and release qualification remain **NOT RUN / NOT PASSED**. |

### Requirements and acceptance status (F03c / coupled F05)

| Requirement / acceptance | Implemented internal result | Remaining gate |
| --- | --- | --- |
| R02 / AC-03 offline try | Desktop **Try fixture (audio OFF)** and real Doctor `self-test`; no profile, credentials, network, model or device required. Scripted text and silence/refusal/error stages are visible. | Clean consumer Windows, no-dev-tool installation is AC-02/G1, not established by developer-host launch. |
| R04/R10 / AC-01 | Same `FixtureCursor` drives old runner and new sessions through actual validator/queue. No duplicate parser/sink or unmerged provider registration. | Remote request/stream contracts and real provider qualification remain D02/V03. |
| R10 / AC-03/AC-08 | Fresh request/turn IDs and epochs; Stop clears pending output; partial/refusal separated; bounded text/trace/PCM; actual sink drain distinct from text terminal. Existing 120 text schedules plus focused real sink/session fault schedules. | Zero stale **audible** segments and p95 physical Stop <=250 ms require actual audio. No audibility inference from submitted samples. |
| R05/R14 / permission and diagnosis | Per-action default-No desktop confirmation or Windows CLI `--play-tone`; no consent persistence; no read-only probe effects. Fixture and live status kept separate and age honestly. | Capture/device onboarding and persistent destination consent belong to V01/V02. |
| R08/R14 / AC-04/AC-10 | Exact report/exits/remedies; no startup writes; corrupt/newer/inaccessible originals preserved. V06b adds explicit local metadata journal and frozen preview/consented export with engine retention and cleanup. | Full real-environment troubleshooting matrix, support-service readiness and release gates remain unqualified. |
| R17 / F05 | Locked solution/build/tests plus real portable/Windows CLI and bounded interactive WPF smoke, using isolated data paths and audio OFF. No runner/pin/gate workaround. | Package payload/compiler and clean-VM/lifecycle/signature gates recorded separately; never equated with G1. |
| R08/R17 / F02-F05 packaging | Both native Windows targets include actual Core/Fixtures/Sessions/Diagnostics/Audio and pinned runtime/NAudio/Tensors assets/notices. Multi-target RID lock routing cannot rewrite normal locks; package/installer omission and integrity assertions remain enforced. | Compiling an unsigned installer does not install it or pass AC-02, AC-11 or G1. |

Local evidence uses official SDK 10.0.401 and `CI=true`: locked restore, Release
build and full affected solution tests with one session-local `C:` output via
`--artifacts-path` (the existing `D:` pooled-drive native-testhost issue).
Repository source remains on `D:`. At the original F03c/F05 checkpoint, the
hosted CI commands/pins were unchanged; no framework, runner or protection was
weakened. The current policy removes hosted validation definitions, not the
underlying local commands or assertions. Executable smokes
select these actual outputs using `-ExecutablePath`, not stale repository
binaries. See [DIAGNOSTICS](DIAGNOSTICS.md#offline-fixture-experience-f03c) for
commands, public seam, metadata/content scope, bounds and deterministic cases.

The original F03c fixture slice did not use providers. V04b registers the already
reviewed provider/runtime/policy libraries in a separate explicit real-mode
surface; its in-process HTTP/native fixtures are not paid/live evidence.
Learned VAD, automatic acoustic/name/group listening, actual provider account/
model quality/cost/performance, OS-vault roundtrip, physical audio and full
first-conversation user trial remain **NOT RUN**. Ordinary diagnosis remains
side-effect-free. V06b references the already merged Support engine without
changing provider/capture/runtime/credential behavior. Its only engine API
addition is a pure validated empty journal selection for report-only preview;
the existing internal IO test seam also grants the Desktop test assembly access.

V04b's production-path offline evidence includes real WPF typed text-only,
typed voice and keyboard PTT -> canonical WAV/STT -> policy -> runtime/TTS/sink;
fake-native Windows credential roles/revisions/lease disposal; missing consent,
unsupported selection, no-speech, refusal/markup, partial TTS, output loss,
slow native work, original-token cancellation, UTC rollback, original capture/
transfer/STT expiry, policy ownership quarantine and stale Stop. Actual native
Desktop/portable Doctor/Windows Doctor executable smokes remain no-key/network/
audio OFF. Their observed results are not a passed live account/device gate.

V06b's initial `b5dc308` developer-host evidence uses the same pinned SDK and one isolated C:
artifact tree: 1,855 integrated solution tests (including both Audio targets),
plus repeated direct Support 47, Providers 635, Conversation 129 and
Participation 121. These repeated runs are not additional distinct cases.
Native Desktop smoke opens passive support from main/setup and keeps recording/
export/audio/network/keys OFF; portable and Windows Doctor subprocess smokes
retain shared JSON/exit semantics. Support coverage includes actual malformed
settings/fixture/live-UI metadata, exact preview/archive bytes, destination and
source changes during confirmation, noncooperative IO/cancellation, faults,
retained cleanup and preserved unrelated targets. Packaging qualification for
this slice is tracked separately from this application/test checkpoint.

V07a's initial developer-host checkpoint used the same pinned SDK/CI settings
and one isolated C: artifact tree: the full integrated solution passed 1,913
cases; directly repeated Core/Desktop suites passed 176/171. Expanded Core
recovery boundary coverage subsequently passed 185 cases. Repeated runs are
not extra distinct cases. Native Desktop (including passive recovery from
main/setup), portable Doctor and Windows Doctor smokes passed with real
vault/network/audio OFF and only isolated test data. Coverage exercises exact
snapshot bytes, current/foreign/future/malformed/tampered/ambiguous/bounded input,
consent/source/revision changes, actual Setup + controlled Windows native
credentials, staged/flush/replace/cleanup faults, exact retained originals and
real WPF timeout/close/reopen/callback ownership, including paused blocked live
vault work. This is internal filesystem/fixture evidence, not physical disk
failure, power-loss, clean-VM N-1/N, real vault/account/device or novice evidence.
V07b activation/rollback/uninstall and signing/rights/G2 remain separate gates.

The exact committed `d471e6ab12eb763caa4b26d1758360ee0df50b77` checkpoint
subsequently passed 1,922 integrated cases, both native Doctor targets and
Desktop smokes, two 626-file self-contained publishes, 144 production package
assertions and protected-sentinel native smoke. Its unsigned installer was
73,758,737 bytes; manifest source was that exact hash with `sourceDirty=false`.
These are historical artifacts, not receipts for later changes. Independent
review then identified missing recovery callback forwarding through
Conversation -> Setup. Two shown-WPF navigation cases reproduced the missing
dialog before the focused callback/active-modal-owner correction. That path now
shares the same app recovery owner and busy slot; corrected-head validation and
package receipts are recorded separately in the PR evidence, not inferred from
the earlier artifacts.

The corrected `2f70dca9dadcee83fa96a895cb08d331ebdb5819` full run exposed one
existing support-test synchronization failure (1,923 passed, one failed).
The UI's completed observation preceded a deliberately dropped busy journal
append; waiting for a subsequently idle writer cannot recover that dropped
event. Controlled real-UI/HTTP/append evidence reproduced the distinction.
The approved test-only correction samples a held Generating state while
recording is OFF, awaits explicit journal start, then releases the response.
The exact completed-event/archive/privacy assertions remain. A negative case
holds actual append work through real UI completion, verifies visible bounded
drops and independent live progress, and refuses to invent a completed record
when freezing later. Production logging remains optional, lossy and queue-free;
no retry, longer deadline or delivery guarantee was introduced.

Historical F03c/F05 developer-host package evidence: that self-contained payload contained
620 inventoried files; actual native Doctor and interactive WPF fixture smokes
passed with audio OFF and runtime discovery pointed away from the SDK.
Production packaging assertions passed, including omitted new assemblies/
notices, wrong managed-package versions/archive hashes, all-project RID lock
regeneration, exact outer/inner effective restore sources and unchanged normal
locks. The verified Inno Setup 7.1.0 compiler produced an internal unsigned
installer below the proposed 200 MiB compressed target. The installer was
**not executed**. This is build/fixture evidence, not a passed clean-VM install,
uninstall, signature, physical-audio, novice or G1 gate.

## 1. Execution rules

Each row below is an owned work item; small items can be one PR, while the
larger items have mandatory PR slices listed below. Retain the parent ID and
acceptance criteria across slices. If another item grows beyond a focused
change, split it before assigning implementation. Do not combine installer,
network security, model selection, and avatar work in one change.

Before implementation, approve the architecture/contract decisions affected by
the task, inspect current repository instructions, and coordinate ownership of
shared files. A PR needs production-path tests where feasible, a reproducible
acceptance description, documentation/remedies for new behavior, independent
review, and fixes/local revalidation before merge. Run targeted checks first
and the full affected suites before acceptance; retain actual local package
and smoke gates when the app graph changes. No weakened assertions, bypassed
required checks or fixture substitution for missing hardware evidence. If a
pre-existing required hosted check cannot be produced under the policy, report
the exact requirement rather than change protections or fabricate a pass.

The product owner's distribution/cloud choices and restricted-model/SDK rights
remain real decisions. Signing credentials, a spending budget, deployment,
host services/drivers, hardware changes, and heavyweight model downloads need
separate authorization. Routine implementation does not grant these permissions.
Code can merge behind unavailable/experimental features without falsely claiming
their deployment has qualified.

## 2. Ordered PR-sized backlog

Risk is implementation uncertainty: L low, M medium, H high. Dependencies are
task IDs; a milestone exit also requires its release gate. Different tasks may
run concurrently only within the boundaries in section 3.

### M0: Decisions, provenance, and contracts

| ID | Deliverable / owner | Depends on | Acceptance | Risk |
| --- | --- | --- | --- | --- |
| D01 | Record AD-01 through AD-09 disposition, project license/distribution direction, no-cloud-fallback policy, first OS/provider targets, candidate license inventory / product + technical owner | Plan reviewed | Record accepted .NET/WPF/UI-independent core direction; other decisions approved, deferred, or rejected with tradeoffs; no invented project license or unapproved SDK/weights; uncertain AIRI identity retained | M |
| D02 | Versioned provider/event/error/settings/probe schemas, turn state machine, binary audio framing and golden payloads / core | D01 architecture subset | Examples cover complete/streaming/refused/canceled/failed/no-speech, bounds, capability mismatch, unknown optional fields; UI and adapter owners agree; AC-01 specification frozen | H |

**Exit G0:** architecture and distribution decisions needed by M1 are explicit;
schema ownership is assigned. Deferred commercial/host/avatar decisions may
remain gates for their own features rather than block fixture work.

### M1: Installer and diagnostic foundation

| ID | Deliverable / owner | Depends on | Acceptance | Risk |
| --- | --- | --- | --- | --- |
| F01 | Minimal solution, WPF accessible shell, shared core/settings and CLI entry point / client | D01, D02 | Standard-user launch, settings round-trip with version, text status without mic/provider, no capture/network on launch | M |
| F02 | Self-contained Windows publish and per-user installer skeleton / packaging | F01 | AC-02 on clean Windows: install/launch/repair/uninstall with no dev tools/runtime download; stable app identity and preserved settings; internal unsigned artifact labeled non-release | M |
| F03 | Deterministic fixture provider + contract harness, owned/licensed audio fixtures and minimal reusable PCM playback sink / core + audio | D02, F01 | Actual core/playback paths run without keys/internet/GPU; simulated failures/delays/duplicates identifiable; AC-01/AC-03 fixture cases. V01 extends this sink rather than creating a second audio stack | M |
| F04 | Shared read-only probe registry, CLI JSON/exit codes, UI pipeline status and first error remedies / diagnostics | F01, F03 | AC-04: same probe results/codes in UI/CLI; stale/unknown/skipped never green; explicit fixture provenance; first mic/output probes fit registry | M |
| F05 | Local validation commands and artifact manifests for solution, contracts, fixtures, packaging / release | F02, F03, F04 | Local build/test/package checks use pinned dependencies, no credentials or network inference/model downloads in ordinary validation; failed fixture or package tasks fail local acceptance; independent review before merge | M |

**Exit G1:** a clean Windows machine runs the fixture demo and diagnosis without
AI hardware, credentials, or development tools. No claim of real AI yet.
F02/F04 are required early; neither may be postponed until after voice features.

### M2: API-backed voice MVP

| ID | Deliverable / owner | Depends on | Acceptance | Risk |
| --- | --- | --- | --- | --- |
| V01 | WASAPI capture/playback, NAudio adapter, CPU VAD integration and device policy / audio | D02, F01, F03, F04 | AC-05 with actual USB/headset/default-change cases; correct formats and bounded VAD segmentation; no unwanted room-speaker switch; PTT and Stop work | H |
| V02 | Resumable onboarding, profile/model/voice choices, destination consent, Credential Manager, typed fallback; independent role-model settings / client | F02, F04, V01 | AC-06 checkpoint/credential cases using fixture adapters; AC-23 LLM model switching without rebuilding, capability/consent revalidation and idle-boundary application; no key in config/log/export. VLM counterpart lands with P02; full real novice trial belongs to V07 | H |
| V03 | One real API provider preset for STT, text LLM, TTS, versioned catalog and capability/error adapters / provider | D02, F03 | AC-07 contract fixtures and authorized live smoke; no surprise charges, model access assumed from listing, free-tier promise, or automatic cloud/model fallback | H |
| V04 | End-to-end streaming orchestration, text segmentation, epochs/cancel, bounded queues and partial states / core | V01, V02, V03, F03 | AC-03/AC-08: real consented mic-to-voice; stop suppresses late audio; network/slow-consumer/duplicate events never replay stale speech or leak memory | H |
| V05 | Separate talk-decision policy, editable named personas, weighted response styles, bounded recent context and listen-first group behavior / core + client | D02, F03, V04 | AC-09/AC-21/AC-24/AC-26: explicit controls work; silence has reasons; persona import/edit/restart and deterministic style selection; consented context accumulation without per-input replies, backlog or hidden listening | H |
| V06 | Redacted structured logs, status remedies, local support bundle preview/consent and retention / diagnostics | F04, V02, V04, V05 | AC-04/AC-10: new failures have stable codes, golden remedies and canary leak tests; no transcript/audio/screen/secret collection by default | H |
| V07 | Application-aware upgrade/rollback/uninstall, signed candidate pipeline, onboarding/usability/support docs / release | F05, V02, V04, V05, V06 | AC-02/AC-06/AC-11 on clean target OS; publisher signature/provenance verified; novice trials meet gate; no real-GPU claim | H |

**Exit G2:** supported API MVP only after the approved provider live lane,
actual Windows install/audio/lifecycle evidence, independent review, signing,
license notices, support readiness, and explicit cost/data copy all pass.
If signing/budget access is unavailable, keep an internal pre-release status.

### M3: Ubuntu self-host beta

| ID | Deliverable / owner | Depends on | Acceptance | Risk |
| --- | --- | --- | --- | --- |
| H01 | Packaged read-only host inventory/preflight and precise prerequisite remedies / host | D02, F04 | AC-12 discovery: unsupported/missing/permission-denied are distinct; dry-run changes nothing; Ubuntu CPU fixtures exercise remedies, not GPU qualification | M |
| H02 | Locked role/image/model manifest, LLM backend spike, dependency/license/disk inventory / runtime + release | D01, H01, V03 contract patterns | Concrete downloadable bytes/digests/revisions; no floating `main`/`latest`; exact candidate GPU matrix has known/unknown fields; no preset enabled before rights and fit validation | H |
| H03 | Gateway TLS/pairing/scoped authentication, rotation/revocation, capability negotiation and firewall guidance / gateway | D02, H01 | AC-13 against synthetic workers on two processes/hosts; reject unpaired clients, bad pins, expiry, replay, wrong roles, unsafe redirects; no Docker/admin API | H |
| H04 | Isolated pinned Python F5 worker/gateway adapter plus named reference-audio/transcript presets and replacement/preview controls / runtime + client | H02, H03, V04 | AC-22: validated reference revision/cache invalidation, explicit rights/destination/preview consent and safe voice changes; truthful chunked transport/cancellation/bounds, no hidden downloads/transcription; actual voice/TTFA/VRAM evidence awaits H06 | H |
| H05 | Reviewed idempotent bootstrap, selected-role Compose profiles, boot readiness, persistent volumes, repair/upgrade/backup/rollback/uninstall / host | H01, H02, H03; H04 only for F5 roles | AC-12 lifecycle on target Ubuntu with declared fixture/real workers: interruption/resume, precise privileges, no root worker/default socket, failed warmup stays failed, data-preserving removal/restore; LLM-only has no F5 prerequisite; GPU cells await H06 | H |
| H07 | Optional native CPU whisper.cpp STT package, selected hosted-STT worker, private-route audit, qualified live VAD/feedback protection and speech barge-in / audio + runtime | V01, V04, H02; H03/H05 for hosted lane | AC-05/AC-07/AC-08/AC-25: consented model provisioning, denied-egress native STT and independently qualified hosted adapter/worker with explicit audio destination; speech-onset interruption without STT/name wait, no stale audio; automatic barge-in only on privacy/feedback-qualified topology, manual controls retained | H |
| H06 | Witnessed single/two-GPU-host acceptance, combined LLM/F5 fit, host-2 fault isolation, optional admin guide / hardware + release | H04, H05, H07, V06 | AC-12/AC-13/AC-14 on exact owner inventory: reboot, concurrent load, unplug host 2, OOM recovery, local-only data path; optional power/remote desktop tests separately consented | H |
| H08 | Feature/role/host/ownership plan and unified resumable setup coordinator / core + client | D02, V02, F04; H03/H05 for managed-host integration | AC-18/AC-19: hybrid role routes, multiple roles on one machine, disabled-feature dependency pruning, selected-route probes, stable host identities, per-machine progress and no remote admin authority; production Ollama issuer/internal transport reviewed, not a blanket loopback relaxation | H |
| H09 | Graphical Ubuntu host setup and matching headless CLI / host + UX | H01, H05, H08 | AC-12/AC-18/AC-19 on Ubuntu Desktop and Server: locally authorized loopback browser UI, accessible plan/remedies, same journal/probes as CLI, local approvals and safe remote handoff, resume after reboot; no Linux WPF companion promise | H |
| H10 | Windows hosting qualification, native Ollama first and separate advanced Docker Desktop/WSL2 lane / host + release | H02, H03, H08; H05 role manifests for container lane | AC-20: exact native and container matrices, ownership/coexistence, licensing/prerequisites, local and paired-remote use, selected-role trial and documented login/boot lifecycle; no native F5, Windows Server or generic GPU guarantee | H |

H07 precedes H06 deliberately: a private microphone-enabled self-host profile
needs a proven local STT route, not an undisclosed cloud transcription
dependency. Typed-only profiles do not require STT. H06 qualifies the
two-host transport using fixture context on host 2; **real VLM/OCR/detector/memory
qualification is still P04**, with explicit labels in the support matrix.
The local-STT part of H07 is required for this private route. Automatic speech
barge-in can remain disabled and does not block a clearly labeled manual/PTT
G3 profile, but R23 remains required before advertising conversational
speech interruption. H07c is independent of the local-STT choice: it must
eventually work with either authorized API or local STT, not require F5.

H08/H09 specify the guided installation experience; they do not claim that the
current OpenAI-only setup can already connect to hosts. H10 is a separately
gated expansion and does not delay a qualified Ubuntu preset. Use the
[installation flow](INSTALLATION_SUPPORT.md#feature-first-multi-machine-setup)
and [upstream evidence](RESEARCH.md#s30) as acceptance inputs. Native existing
Linux services can be connect-only candidates; no second managed Python/native
Linux stack or all-distribution installer is promised.

**Exit G3:** publish a tested host manifest and reproducible host setup/support
guide only after real NVIDIA evidence. Remote witness reports must identify
operator, build, machines, steps, logs/timings, consent, and outcomes. Optional
power policy/Sunshine are not blockers if disabled; enabling them requires their
own hardware-specific evidence. Unvalidated deployments remain experimental.

### M4: Useful perception and memory

| ID | Deliverable / owner | Depends on | Acceptance | Risk |
| --- | --- | --- | --- | --- |
| P01 | Explicit selected-window screenshot capture, preview, pause/lock handling and budgets / client | V02, V06, H03 | AC-15: zero capture before consent; source closed/locked/denied stops; no desktop-wide fallback or game injection; exact destination visible | H |
| P02 | Host-2 VLM/OCR adapters, independent VLM model selection, optional separately licensed detector, freshness/resource scheduler / perception | P01, H02, H03, H06, V02c | AC-15/AC-14/AC-23 VLM: compatible model changes without rebuilding or changing LLM/voice; stale model results excluded, capture permission unchanged, bounded latest-frame queue and host-2 fault isolation | H |
| P03 | Opt-in memory service, user-save/inspect/edit/delete/export, lexical retrieval then evaluated embedding/reranker option / memory | D02, H03, V06 | AC-16: provenance/consent and deletion propagate through index/cache/in-flight work; no store-wide cloud upload; backup/restore semantics documented | H |
| P04 | Real host-2 model and combined gaming-load qualification, privacy and restore exercises / release | P02, P03 | AC-14/AC-15/AC-16 with real enabled roles/models; offline/host-loss fallback truthful; no advertised unlimited simultaneous models | H |

**Exit G4:** each enabled feature improves a defined task on a labeled evaluation
set and stays within measured resource/privacy limits. Defer unnecessary YOLO,
vector database, and reranking complexity. App-scoped remote-participant audio
or diarization, if prioritized, needs an additional V01/V05-style acceptance
task and consent review; it is not implied by P01 screenshot capture.

### M5: Optional avatar

| ID | Deliverable / owner | Depends on | Acceptance | Risk |
| --- | --- | --- | --- | --- |
| A01 | Exact SDK/renderer decision, Expandable Application classification, asset import rules / product + avatar | D01, G2 | Recorded distribution permission/terms and user asset rights workflow; no assumption MIT project code licenses Cubism Core or assets | H |
| A02 | Isolated renderer, safe import, actual-playback lip sync, blinking/breathing/gaze motions / avatar | A01, V04 | AC-17: voice unchanged without renderer; late/canceled audio not animated; malformed/oversized/path-traversal assets rejected | H |
| A03 | Avatar performance/device/crash/uninstall and licensing release checklist / release | A02, G4 | AC-17/AC-14: bounded idle/FPS, renderer crash/restart and removal cannot erase or stop voice; notices and distribution terms complete | M |

**Exit G5:** avatar remains optional, with measured cost and rights-cleared
distribution. A01 research may run earlier after voice MVP; no avatar
implementation takes priority over unpassed voice/self-host gates.

### Mandatory slices for larger work items

Slice dependencies define the earliest isolated development work; integrating
the parent still requires all parent dependencies. A parent is complete only
when all required slices and its acceptance cases are complete; optional
slices may be explicitly disabled, not silently described as shipped.

| PR slice | Focused change | Slice dependency / merge acceptance |
| --- | --- | --- |
| F03a | Fixture transport/adapters and failure scenarios | D02/F01; schema and deterministic state tests, no device requirement |
| F03b | Minimal production PCM sink and sample playback | D02/F01; correct format/stop/bounds and actual speaker test; no capture/VAD yet |
| F03c | Wire fixture session through sink and status hooks | F03a/F03b; AC-03 trace distinguishes fixture generation from real playback |
| V03a | STT adapter and shared provider catalog structure | Contract fixtures; authorized small transcription smoke recorded separately |
| V03b | Text LLM adapter and streaming error normalization | V03a shared infrastructure only; final/partial/refusal/error fixtures |
| V03c | TTS adapter and selected voice/format validation | V03a shared infrastructure only; PCM/stream/error fixtures; no auto fallback |
| V02c | User-editable LLM role/model selection using reusable role-selection contracts | V02/V03 foundations and D02 schema review; AC-23 LLM cases, explicit named-adapter support, no arbitrary catalog bypass, no automatic download/probe; P02 adds VLM using the same boundary |
| V05a | Named persona text editor/import/export and validated style-weight settings | Existing settings/migration ownership and V02; AC-21 persistence/import/error cases and AC-26 weight validation; inert settings only, no permission changes |
| V05b | Persona/context prompt budgeting and weighted style selection in the real conversation path | V05a/V04; AC-21 next-action revision and AC-26 production selection/prompt tests; no current-turn mutation, no change to response frequency or permissions |
| V05c | Bounded observation context and opt-in listen-first participation | V05b/V01 qualified live capture/VAD integration; AC-09/AC-24, metadata reasons, no ignored-input LLM/TTS or stale backlog; managed fixtures do not enable native production listening |
| V07a | Implemented internal settings/configuration snapshot and previewed restore transaction; see [scope and limits](SETUP.md#local-configuration-recovery-transaction-v07a) | Existing valid same-profile v1/v2/v3 only; v3 persona data restored and older sources preserve current personas; controlled interruption retains original; physical crash/power-loss and actual upgrade qualification remain NOT RUN |
| V07b | Version staging, activation, rollback and uninstall preservation | V07a; compatible restore and N-1/N failure cases |
| V07c | Signed build/manifest, provenance, SBOM and notices | Build foundation may proceed in parallel; protected signing access required before release, never mocked as valid |
| V07d | Clean Windows and novice walkthrough/support release record | V07b/V07c; AC-02/AC-06/AC-11 real-environment exit |
| H04a | Version-qualified F5 worker/gateway synthesis, formats, bounds and cancellation | H02/H03/V04; explicit valid reference/audio permission, truthful chunked transport and compute-cancel limits; no model execution/download without authorization |
| H04b | Reference-voice picker, transcript, named presets, revision/cache lifecycle and preview | H04a/V02; AC-22 actual adapter-path A/B/same-path replacement/error tests; real voice difference and GPU evidence recorded separately in H06 |
| H07c | Live speech-onset interruption with topology-qualified feedback protection | V01/V04/V05c and native privacy approval; AC-25 Stop/flush/late-output/ownership cases plus authorized actual-audio latency/echo evidence; neither F5 nor local STT is a prerequisite |
| H05a | Reviewed prerequisite plan and idempotent setup journal | H01/H02; dry-run mutates nothing, resume reconciles approved steps |
| H05b | Artifact download consent/progress/resume/integrity | H05a; interrupted/corrupt/changed-ETag/disk-full fixtures; no heavy download without authorization |
| H05c | Selected-role Compose generation and boot/readiness supervision | H05b/H03; H04 only for F5; explicit owned engine/context, disabled roles absent, shared dependencies retained; real Ubuntu fixture-role reboot/stop evidence, no GPU claim |
| H05d | Consistent backup and restore | H05c; separate-profile restore preserves data/schema and requires safe re-pairing |
| H05e | Host upgrade staging and compatible rollback | H05d; failed warmup/migration restores documented prior state |
| H05f | Targeted repair/reconfigure and data-preserving removal | H05c/H05d; no unrelated host package/volume deletion |
| H07a | Native optional CPU STT packaging and adapter | H02/V01/V04; declared CPU/model and denied-egress speech trials; no Docker required, no STT prerequisite for typed input |
| H07b | Optional hosted STT worker and gateway adapter | H02/H03/H05c; pinned selected worker, bounded upload/cancel/format checks, explicit audio destination and real speech trial; no exposed unqualified example server |
| H08a | Feature/role/host/ownership schema and pure installation planner | D02/V02; AC-18 fixtures for typed-only, mixed routes, co-location, external ownership, disabled roles, unsupported tuples and migration preserving existing API behavior |
| H08b | Bounded connection/model/real-trial probes through shared diagnostics | H08a/F04/H03; AC-19 deadlines, no side effects on open, scoped consent, no inference in cheap checks, cleanup ownership and stale-evidence invalidation |
| H08c | Coordinator UX, local/remote handoff and exact-experience completion | H08a/H08b; AC-18 checkpoint/back/resume and actionable per-host failure; managed provisioning integration uses H05, never remote admin credentials |
| H09a | Local graphical Ubuntu host setup over the existing host engine | H05/H08c; AC-12/AC-18/AC-19, Desktop browser accessibility, session authorization/origin protection, CLI parity and no LAN management listener |
| H10a | Native Windows Ollama route and lifecycle qualification | H02/H03/H08c; AC-20; connect-only first, approved managed packaging separately, native gateway packaging for paired remote access, no tray-process-as-boot-service assumption |
| H10b | Advanced Windows Docker Desktop/WSL2 qualification | H02/H03/H05c/H08c; AC-20; explicit Linux-container engine, WSL/driver/license prerequisites, shared resources and actual login/reboot evidence |
| P03a | Consented fact store, inspect/edit/delete/export API | H03/V06; AC-16 storage/privacy/deletion cases |
| P03b | Lexical retrieval with provenance and expiry | P03a; held-out useful-retrieval evaluation and in-flight delete invalidation |
| P03c | Optional embedding/reranking adapter and index lifecycle | P03b; only after measured benefit/rights review, derived-data rebuild/delete cases |

### Installation program execution order (2026-09-22)

#### Prior implementation reuse amendment (2026-09-23)

**User decision:** reconcile and reuse earlier work through focused reviewed
PRs, then finish the missing installation features. Canonical frozen source:
`1715d194c03e9b1399c93c4f496f382e7076afc5` (69 unmerged commits at discovery).
This is selective reuse, not a merge of that branch or a claim that all 69 are
integrated. The source stays untouched. Its old candidate
`54a71ae8d741572bb898a4a3194097c4d1c79872` and historical reviews/test/package
receipts are context only, never fresh-head evidence.

| Reuse phase | Exact source / boundary | Required gates and remaining work |
| --- | --- | --- |
| Persona baseline (this change) | In order, `e031817021838d528ed027372f343603323f3563`, `1e1615c24226d6914fbc072a5659912c8b4a9ce2`, `585f86518b29bd432bcb422e48d1d62cec40eb6e`, `f48fc94df7e562470212481b5491e3e890f59eea`, `bc0c954c91954d9dba85f36e6e67eaf572f76902`, reused with `cherry-pick -x` onto `5f7a2881577dd0520e178a48cc4b1b10b194d3fe` | Editable personas/settings v3, explicit-turn style, compatible LLM selection, visible Stop/Escape and bounded explicit context only. Preserve v1/v2 migration, credential ownership, recovery and historical Updates receipts. Fresh Core/Desktop/Providers/Updates, full affected solution, audio-OFF native/package gates and independent diff review; no live provider qualification. |
| Independent modules (separate PRs, not included here) | Gateway `ec6db3979a1e2b8164176d455e4017475b9865ca`; F5 `b46e49be729977411a8c73bc14baeaeb2d8a2ceb`; Memory `fc1fd57f59fed0899ccbd5991797f1699d4e9bfb` | Isolated module/tests/module-doc imports with original attribution and fresh local direct-project checks. Reconcile Gateway against current Gateway.Trust; no competing settings/UI ownership, listener deployment, model run or qualification inferred. |
| Sequential settings reuse (not included here) | v4 local memory -> v5 self-host voice -> v6 perception -> v7 barge-in -> v8 remote memory, one settings/Desktop/Updates owner | Review each original diff and bring its legacy prerequisite modules first: Memory for v4; gateway, reference-voice, STT/F5 and provider boundaries for v5; capture/perception contracts for v6; live VAD/participation/feedback boundaries for v7; remote memory protocol/store for v8. Each PR must prove exact migrations, recovery/history compatibility, credential/consent behavior and affected app/package paths locally; real account/device/native privacy/GPU/host rights remain separate gates. |
| Installation completion (after reconciliation) | Retain the current research, Core/Installation pure planner, HostArtifacts OCI v2/candidate catalog and isolated Gateway.Trust | Reconcile prior host/artifact/journal implementations with these foundations before adding missing guided installation. H08d's installation-v3 draft is paused: schema 3 belongs to personas. Use the next compatible schema after v8, not a conflicting schema or second journal/planner. |

H01 PR #21 / `22d76dfdd278d24b8a2d9c7b87621216dc93c687` remains held and
**is not imported**, including its ancestry. Pending PR #37, the new journal and
new installation drafts are preserved on hold, not silently superseded or
deleted. The earlier wave table below is the installation program, not permission
to bypass this reconciliation sequence. Normal reviewed PR publication is
authorized for selected reuse only; no self-merge, remote CI, release, live
provider, host/device/credential/model/driver/Docker/WSL installation is authorized.

Documentation reconciliation preserves installation AC-18/AC-19/AC-20 and
maps the source companion AC-18..AC-23 to AC-21..AC-26 respectively. Source H07b
speech interruption becomes H07c here; existing H07a native-STT and H07b hosted-STT
retain their meanings. These are identifier reconciliations, not changed gates.

**Owner direction:** implement the researched installation program in dependency
order, using independent worktree branches/PRs in parallel where ownership does
not overlap. The coordinator may publish and normally merge locally validated,
independently reviewed changes to `main`. This is not authorization to waive
qualification, install host prerequisites, run paid inference, publish releases,
change protections/permissions or start remote CI.

Scope is the installation and connection experience for the roles described
above, not implementing the separate perception/memory/avatar product backlogs
early. Disabled/unimplemented optional features remain honest unavailable
selections; when those products exist they join the same installation planner.
No empty worker, mock authorization issuer or fixture pass may enable a live
hosting preset. Foundation slices are explicitly internal until integrated.

| Wave | Parallel work / branch ownership | Dependency and deliverable boundary |
| --- | --- | --- |
| 0: Plan and reconcile | This installation plan; inspect existing work and source/publication holds | The existing H01 preflight PR #21 is retained, not reimplemented. Its target-native evidence hold remains until real authorized local Ubuntu evidence and review satisfy it. |
| 1: Independent foundations | H08a pure feature/role/host/ownership planner in Core; H03a isolated gateway trust/authorization library; H02c container artifact inventory in HostArtifacts | Three independent PRs from current main. Do not change Desktop settings/UI in this wave. Planner uses explicit capability/eligibility inputs, not invented qualified tuples; gateway library opens no listener; artifact inventory downloads/executes nothing. |
| 2: Persist and connect | H08d versioned installation settings/checkpoint migration; H03b persistent host identity/TLS gateway; H02d exact runtime/image/model closure | H08d follows H08a; H03b follows H03a; H02d follows H02c. Keep each owner separate. Resolve named provider capability and credential boundaries before any real route/probe is registered. |
| 3: First usable guided path | H08b/H08c Windows feature-first setup and bounded checks; H05a/H05b host desired-state journal and authorized download engine; H10a native Windows connect-only integration | UI follows H08d and reviewed providers/gateway. Host lifecycle follows H01/H02 and consumes the shared plan, never a second competing planner. Windows local Ollama can proceed independently of Ubuntu qualification but cannot use an untrusted issuer or raw remote LAN port. |
| 4: Managed Ubuntu services | H05c selected-role Compose reconciliation; H04 F5 worker; H07a native CPU STT / H07b hosted STT | First real managed preset is LLM-only; F5 and STT are conditional additions, not prerequisites for typed text. Workers can be implemented in parallel once contracts are frozen. Integrate per-role artifacts, rights, budgets, cancellation and private transport. |
| 5: Installers and platform UX | H09 graphical Ubuntu host setup + CLI parity; H10a approved native Windows lifecycle; H10b advanced Windows Docker/WSL2; H05d/H05e/H05f backup/update/rollback/removal | Consume the same tested host plan/journal and role contracts. Separate per-OS/per-runtime qualification. Windows Docker does not require native Windows F5. No desktop-client Docker dependency. |
| 6: End-to-end qualification | H06 Ubuntu combined load/reboot/multi-host; AC-18/AC-19 novice/hybrid/quick checks; AC-20 Windows lanes | Real declared machines, local-only execution, explicitly authorized samples and installed software. Qualify each exact selected experience, not every optional feature at once. Missing hardware/rights/signing remains blocked; never relabel it as a pass. |

H03a owns bounded one-use pairing and scoped device authorization, expiry/
revocation and clock/cancellation behavior with production-backed local tests.
It is not a production gateway until H03b supplies protected persistence,
trusted TLS identity, transport admission and deliberate local approval.
H03b must exercise actual loopback TLS in local tests and unauthorized-client
cases; protected key storage and target-host lifecycle need separate native
evidence. The gateway never receives Docker/admin privileges.

H02c extends the existing offline artifact eligibility/inventory boundary for
immutable container image identities/platforms, dependency and disk/rights
unknowns. H02d records exact upstream runtime/model artifacts and closes their
dependency/rights/runtime-fit requirements; registry metadata is not locally
verified bytes or executable approval. Unknown sizes, rights or compatibility
remain unknown. No floating-image example becomes a deployable preset.

**H02c implementation evidence (foundation only):** the standalone
[HostArtifacts v2 contract](../src/Martlet.HostArtifacts/README.md#version-2-container-metadata-h02c)
preserves v1 and adds strict selected-image/index/platform metadata, shared
content fact consistency and explicit compressed/expanded/staging unknowns.
Production-path tests use authored synthetic image metadata and a golden byte
inventory, not upstream/runtime evidence. No image/catalog qualification,
download, daemon access, host observation, installation or execution is added;
H02d and all native rights/runtime/host gates remain separate.

H08d integrates the planner into versioned persistent settings with migration,
backup/restore and support metadata. Preserve existing v1/v2 files, profile/
credential ownership and the current OpenAI conversation behavior. Only this
owner changes the shared settings schema; H08c consumes it after merge.
Disabled role configuration can be retained without authorizing its execution.

**Per-PR completion and merge protocol:** keep one bounded change per branch,
test the production path locally, record exact commit/commands/outcomes and
unrun gates, obtain independent review, fix findings and rerun affected checks.
Inspect base/head workflow trees and triggering events before every push/PR/
merge so publishing cannot start remote validation. Merge one eligible PR at
a time against the reviewed head; if main advanced, reconcile and revalidate
the affected integration before merging. Do not force-push, bypass protections,
manufacture statuses or merge a held PR. Stop only dependent waves for a genuine
blocker and continue independent work. Update this ledger with actual merged
evidence; a queued session or code-only foundation is not a completed feature.

#### H03b3: explicit Linux state backend candidate

The canonical durable owner now shares one checkpoint/redo engine across its
unchanged Windows-DPAPI default and an explicit `LinuxServicePermissions`
candidate. Linux x86_64/glibc/local-ext4 state uses native FD/UID/mode/ACL/link/
mount verification, cooperative lock and atomic rename plus directory fsync.
Its distinct envelope is **permission-isolated plaintext**, not DPAPI parity;
no silent migration/fallback or second paired-device authority exists.

Permanent relationships, revocations/nonces, same-key renewal, default No and
loopback-only behavior are retained. Windows regressions and the modeled
production Linux boundary run locally; the separate native Linux test target
is compile-only here. Actual Linux execution, reboot/service/container lifecycle,
portable approval UI/console, private worker transport and LAN remain
**NOT RUN / unqualified**. H01/#21 and hosting-preset holds remain unchanged.
See [custody/recovery and evidence](../src/Martlet.Gateway.Persistence/README.md#explicit-linux-service-permissions-candidate).

## 3. Parallelization boundaries and first implementation batch

Start from the reviewed plan. The shortest useful batch is:

1. **D01-D02:** one owner resolves stack/settings/protocol decisions and freezes
   schemas with reviewers. Record unresolved rights/hardware items explicitly.
2. **F01:** create only the native shell/core/CLI and configuration skeleton.
3. On F01's agreed interfaces, **F02** owns `packaging\windows`, **F03a** owns
   `tests\fixtures` and fixture adapters, **F03b** owns the minimal
   `src\Martlet.Audio` playback sink, and **F04** owns diagnostic UI/probes.
   F04 can implement its registry in parallel, but its acceptance waits for F03.
4. **F05:** retain the existing solution/fixture/packaging commands as local
   gates with recorded evidence and independent review. Run a clean Windows
   install/fixture walkthrough in a separately authorized local environment.
5. Then parallelize **V01** audio and **V03** provider adapters against D02;
   V02 onboarding integrates validated audio/provider contracts, and V04 joins
   the paths. Policy, retention, and diagnostics are not deferred to release.

Do not ask separate sessions to implement full companions, select different UI
frameworks, or share a worktree. Assign one owner per changed schema/settings/
solution/validation file; interface changes require coordination and contract review.
Feature branches depend on merged prerequisites or explicitly stacked PRs.
Hardware tests, signing, and external paid calls are scheduled gates, not tasks
silently delegated to agents without access.

Independent later scopes: H01 read-only preflight and H03 gateway can start from
stable D02/F04; H04 owns only its Python worker; H05 owns host lifecycle, not
provider internals. P03 memory can progress against fixture retrieval independently
of P02 model work. A01 licensing research can proceed without touching audio.
Never let simultaneous sessions edit one model lock or migration schema.

## 4. Acceptance cases

IDs below are stable requirement-level scenarios, not currently existing tests.
Each implementation PR adds the narrowest executable test it can and records
what remains a real-device/manual gate.

| Case | Observable pass condition |
| --- | --- |
| AC-01: Contract conformance | Provider and gateway accept valid golden payloads, reject invalid/bounded/version-mismatch cases, normalize errors, and distinguish unimplemented/unknown from ready; SSE/audio framing and terminal conditions covered |
| AC-02: Windows lifecycle | On clean standard-user target OS, install and launch without toolchain/runtime fetch; upgrade N-1 -> N, interrupt/retry, rollback with compatible snapshot, repair and uninstall preserve selected data; no unintended startup/firewall/admin changes |
| AC-03: Deterministic fixtures | No internet/key/GPU; actual orchestration/playback handles complete/refused/empty/canceled/slow/truncated/duplicate/out-of-order streams with expected codes, bounded memory and no fake real-AI badges |
| AC-04: Diagnosis | GUI and CLI agree on probe IDs, outcomes and remedies; every troubleshooting-matrix row has fixture coverage; unknown/stale/warming/skipped is not green; live vs fixture last-success is separate |
| AC-05: Audio devices | Actual USB/wired headset and Bluetooth tests cover 44.1/48 kHz capture, mono conversion, silence/noise, disabled permission, fixed/default change, removal during speech, sleep/resume, safe reconnect and typed fallback |
| AC-06: Onboarding | Five first-time testers on documented target setup: at least four complete a real first conversation without developer intervention inside the proposed active-time target; each interruption resumes without recording or exposing credentials |
| AC-07: Real providers | Authorized account/selected models pass small real STT/LLM/TTS requests; wrong origin/key/model/voice/quota/capability conditions remain actionable; record date/model/adapter and charges, no free-quota assumption |
| AC-08: Streaming/interruption | At least 100 deterministic stop/replace/disconnect permutations yield zero stale/duplicate audible segments; real audio Stop meets latency target; exact network failure shows partial state; unsupported compute cancellation labeled honestly |
| AC-09: Participation | Labeled direct-address, PTT, unaddressed, quoted name, cooldown, busy, silence, self-audio, and unknown-confidence corpus produces expected reason codes; opt-out suppresses all unsolicited turns |
| AC-10: Privacy/support | Canary keys, transcripts, raw audio/screens and paths never enter default logs/bundles; consent preview accurately enumerates files; retention limits expire data; pause/lock/exit stops capture; memory disabled means no persistent conversation store |
| AC-11: Release trust | Signed binaries/installer/update manifest, hash/provenance/SBOM/notices, scanned dependencies and reviewed high-risk findings; verify clean download and older version rollback; bad signature never executes |
| AC-12: Ubuntu lifecycle | Real supported OS: discovery/dry-run, approved provisioning, interrupted download/setup resume, pinned runtime/model warmup, reboot without login, graceful stop, failed-worker repair, upgrade/rollback/backup restore and data-preserving uninstall |
| AC-13: LAN isolation | One/two-host paths reject unauthorized/wrong-scope/expired/revoked clients, wrong pins, replay and unsafe redirects; port/binding/firewall checked from unauthorized LAN source; DNS/IP changes do not change trusted identity |
| AC-14: Hardware/load | Witnessed selected GPU/driver/image/model tuples fit and execute under combined roles; bounded OOM/slow/reboot recovery, host-2 loss, game frame time, sustained streaming, and optional admin policies measured on actual hardware |
| AC-15: Perception | No capture/upload without selected-source/destination consent; lock/close/pause works; output has age/provenance; stale/failed perception excluded; useful labeled-task accuracy and resource budgets recorded |
| AC-16: Memory | Explicit saved facts retrieved with provenance; inspect/delete/export works; cascaded delete invalidates embeddings/cache/in-flight results; restore compatible snapshot; no surprise cloud upload or backup-erasure claim |
| AC-17: Avatar | Approved SDK/assets, safe bounded import, lip sync follows played samples, predictable idle/motion budget; disabled/missing/crashed/uninstalled renderer leaves voice and provider setup intact |
| AC-18: Topology-aware installation | One plan covers typed-only, API-only, one multi-role Ubuntu host, Windows client+host, mixed cloud/local roles and split hosts. Each selected experience requires only its dependencies; zero downloads/keys/probes for disabled roles. Failed optional context leaves voice/text usable with disclosure. Per-machine Back/Save/resume survives interruption; remote handoff contains no secrets/admin authority; external services are never silently adopted. Removing one role preserves shared dependencies/data. Ubuntu Desktop GUI and Server CLI operate the same journal/engine with accessible remedies. |
| AC-19: Fast honest readiness | Check acknowledgement within 1 s; cheap group completes or reports exact-stage timeout within 15 s wall time including queueing, at most four probes, 5 s per network stage; unstarted checks remain not checked. No live probe starts on opening setup. Connected/model-present/request-passed/stale/disabled remain distinct; unsupported metadata is unknown. Small real probes default to a visible 60 s deadline or disclose a qualified override before approval; downloads/warmup are separate. Test failure/cancel/slow/unsupported/wrong-role cases, timestamp/config binding and no overlapping retries before cleanup; no billable periodic health calls. Witness actual selected-role trials separately from fixture timing cases. |
| AC-20: Windows hosting lanes | Record independent native Ollama and Docker Desktop/WSL2 Linux-container matrices and real-machine results, not inherited Ubuntu passes. Exercise co-resident client+host and paired remote client, compatible external installation reuse, ports/engine ownership, driver/GPU availability, model fit, sleep/resume, user-login versus boot availability, scoped repair/removal and text-only/API alternatives. No automatic WSL/driver/firewall changes or terms acceptance; unavailable prerequisites remain blocked. Managed lifecycle claims require their own install/update/rollback/uninstall evidence. |
| AC-21: Editable personas | Create two named profiles; edit/import/export/reselect/restart preserves exact valid text and weights; malformed/oversized/unreadable import leaves saved state and draft intact; next fresh authorized LLM action uses the selected bounded revision, never mutating an active request or permissions |
| AC-22: F5 reference voice | Through the real adapter boundary, select A then B and replace A's bytes at the same path; new authorized synthesis uses only the applied audio/transcript revision with no stale conditioning; invalid/missing/changed reference blocks use with a remedy, no hidden transcript model/default voice; preview consent and mid-turn switching enforced; separately authorized F5 listening trial confirms audible change on the exact model/runtime |
| AC-23: Model selection | Exercise at least two compatible IDs per enabled role through production selection/adapter paths without rebuild; LLM and VLM choices are independent, survive restart and preserve persona/voice; unsupported/unavailable or over-budget selection fails visibly; busy changes, stale results, capability refresh and fresh consent covered; fixtures do not qualify live model access/fit or enable screen capture |
| AC-24: Listen first | A scripted multi-utterance conversation retains only eligible bounded context, ignores irrelevant/no-speech/self-audio inputs, resets the quiet gap on new speech and produces at most one fresh eligible response, not one per observation; assert zero LLM/TTS for suppressed inputs, exact time/count/byte/token caps, eviction/clear/revocation/expiry and zero unsolicited replies when opted out; explicit typed/PTT remains usable |
| AC-25: Speech interruption | Inject onset during generation, synthesis and playback through the live activity/cancellation boundary; flush unsaid output, reject late frames, retain cleanup ownership and never auto-resume; actual authorized headset/speaker trials distinguish external speech from self-voice/noise, record missed/false interruptions and acoustic detection latency, and meet p95 <=250 ms confirmed onset event to last rendered sample over at least 20 interruptions per advertised topology; unqualified modes remain unavailable |
| AC-26: Response-style ratios | Production selector rejects invalid/all-zero weights, never samples zero-weight styles and always selects the sole positive style; fixed-seed 10,000-selection corpus matches a mixed target within two percentage points per style; chosen style reaches the next authorized prompt, not participation/permissions; held-out human review separately checks perceived helpful/sarcastic/silly/distracted/teasing style and factual/control boundaries |

For quality evaluation, use consented/licensed fixtures and a held-out scenario
set. Measure STT errors on companion names and gaming terms, suppression false
positives/negatives, and human-perceived audio clarity. Do not optimize exclusively
to one developer's microphone or use personal conversations as public fixtures.

## 5. Proposed quantitative targets, not results

Targets are acceptance hypotheses to calibrate at M1/M2/H06. Report distribution
and failures, not only best-case averages. Freeze scenario/hardware/provider
conditions in the evidence record before comparing results.

| Metric | Proposed target | Conditions and evidence |
| --- | --- | --- |
| Installer size | At most 200 MiB compressed, excluding optional STT/avatar/models | Self-contained Windows x64 API package; measure exact artifact. Revise explicitly if native/runtime dependencies exceed it |
| Installation | At most 3 minutes from installer launch to usable shell | Warm local disk, already downloaded installer, supported Windows, standard user, ordinary SSD; exclude OS/account provisioning |
| First real conversation | At most 10 minutes active onboarding time for 4/5 novices | Working internet/mic/headset and authorized key already available; exclude account billing setup/download time but report it separately |
| API response onset | End of user speech -> first audible sample: median at most 5 s, p95 at most 10 s | 20+ short English turns, bounded reply, warm provider, recorded model/account region, stable link with measured RTT at most 100 ms |
| Qualified self-host onset | Median at most 4 s, p95 at most 8 s | Same conversation set, warmed selected LLM/F5/local-STT profile, wired LAN RTT at most 5 ms; GPU/model/precision/context/concurrency disclosed |
| Stop responsiveness | p95 at most 250 ms to silence | Stop activation to last rendered sample, real output device; compute abort is a separate measurement |
| Qualified speech barge-in | p95 at most 250 ms from confirmed speech-onset event to silence | At least 20 real interruptions per advertised topology; report acoustic-onset-to-detection latency and false/missed triggers separately, including self-audio/noise; AC-25, not implied by manual Stop or post-capture VAD |
| Stability | Zero canceled-turn/duplicate audio; no unbounded queue growth | 100 deterministic fault cases plus 30-minute real conversation/load session |
| Desktop CPU/RAM | Idle under 3% total CPU and 350 MiB private bytes; API conversation under 15% total CPU and 600 MiB | Defined 4-core/8-thread-or-better reference PC, avatar/perception/CPU STT off; record actual CPU, sample interval and percentile |
| Desktop GPU | No required GPU inference workload | Confirm provider/audio path does not allocate CUDA models; UI compositing is not "zero GPU use" |
| Gaming impact | Under 5% p95 frame-time regression against repeated baseline | Same game scene/settings, warmed system, voice-only first; perception/avatar evaluated separately with limits rather than assumed free |
| Host readiness after boot | At most 5 minutes for selected cached profile | No model downloads, driver ready, disclosed SSD/GPU/model footprint; startup timeout and degraded states visible |
| Diagnosis | Identify failed pipeline stage and next action within 60 s for common injected faults | GUI/CLI, bounded probes, no billable inference without consent |

No GPU requirement can be derived solely from parameter count or weight file
size; combined allocations, context/KV cache, vocoder, framework and temporary
buffers matter. No API latency is guaranteed by upstream documentation.
Failure to hit targets triggers scope/model/UX reconsideration, not changed
measurement conditions hidden from the user.

## 6. Local validation lanes and clean-machine matrix

| Lane | Environment / execution | What it proves and does not prove |
| --- | --- | --- |
| Local unit | Existing pinned .NET/xUnit runner, pure policy/settings/bounds/redaction tests | Deterministic logic; not audio devices or cloud/GPU inference |
| Local contract | Fake HTTP/SSE/WebSocket servers, golden vendor/native payloads | Schema normalization, truncation/duplicate/timeout/cancel semantics; not provider availability |
| Local fixture integration | Real core and diagnostic CLI with synthetic providers/audio | End-to-end state/probe/privacy plumbing with no paid requests; not model quality |
| Local packaging smoke | Windows build/install artifacts on authorized local hosts; Ubuntu CPU image/config checks on an actual suitable local Linux environment | Build/package layout and declared dependencies; developer hosts are not clean consumer PCs and Windows inspection is not Linux execution |
| Manual opt-in API | Separately authorized low-budget account/data/credentials, driven locally; never ordinary validation or untrusted code | Actual selected STT/LLM/TTS tuple/date only; tracks quota/cost and drift |
| Real Windows gate | Clean Windows 11 25H2 x64 Home/Pro VM plus physical audio PC; standard/admin accounts | Install, permissions, signatures, device churn, no-dev-tool prerequisites, usability |
| Real Ubuntu gate | Clean 24.04 x86_64, then qualified NVIDIA hardware | System services, driver/runtime/model fit, reboot/start/stop; CPU containers alone cannot pass GPU cells |
| Witnessed two-host gate | Actual Windows + both Ubuntu PCs, known LAN, selected role manifests | Authentication/firewalls, combined roles, failures, streaming, performance; host-2 fixture gate cannot certify real perception |

Use the existing pinned .NET/xUnit runner consistently. Add Python worker tests
only when a worker exists, using that worker's chosen tooling. No expensive
model pulls in ordinary local validation. Run narrow related tests first, then
the full affected suites and applicable local build/package/smoke gates. Keep
environment, exact revision, results and known limitations with the evidence.
Unavailable environments remain explicit qualification blockers. Do not use
remote validation runners to fill the gap. Installing VM/WSL/Docker/driver
infrastructure requires separate host authorization; this plan grants none.

Required cross-cutting scenarios for relevant lanes:

| Dimension | Cases / owning acceptance |
| --- | --- |
| Install/state | Clean install; long/non-ASCII user paths; disk full; standard-user permissions; corrupted installer; two versions; cancel mid-install; AC-02/AC-11 |
| Lifecycle/data | N-1 upgrade, migration interruption, failed readiness rollback, old-schema reader refusal, retained settings/models/voices/memory, revoke/re-pair after restore; AC-02/AC-12/AC-16 |
| Network | Offline first launch, intermittent internet/LAN, high latency/loss, DNS failure, blocked port, TLS clock/pin failure, endpoint disappearance, interrupted model download; AC-03/AC-07/AC-12/AC-13 |
| Audio | No devices, privacy denial, Windows mixer mute, USB/Bluetooth removal/default change, sleep/lock/resume, room-speaker feedback, long silence and game noise; AC-05/AC-08/AC-09 |
| Provider | Invalid/revoked key, expired pairing, rate/billing quota, absent model/voice, wrong OpenAI subset, refused/empty response, partial stream/cancel; AC-01/AC-07/AC-08/AC-13 |
| GPU/load | Unsupported architecture/driver, host-visible but container-invisible GPU, VRAM exhaustion with LLM/F5, warmup stuck, slow consumer, concurrent game/perception/remote desktop if enabled; AC-12/AC-14 |
| Privacy | No capture before consent, pause/lock, destination change, content in error bodies, trace/bundle redaction, memory delete while query active; AC-10/AC-15/AC-16 |
| Companion controls | Persona import/edit/restart, invalid weights, F5 same-path replacement and stale caches, independent LLM/VLM switching and busy apply, bounded context without reply backlog, style selection versus actual wording, speech-onset/echo/late-audio races; AC-21-AC-26 |

## 7. Release, provenance, and support gates

Every release candidate has a record of commit, artifacts/signatures/digests,
dependency lock/SBOM and notices, model/voice provenance (if included), protocol
and schema compatibility, expected downloads, test environment, cases and
results, known limitations, and rollback instructions. Store private hardware
identifiers/recordings outside public artifacts; publish sanitized evidence.

Signing keys never enter source, ordinary logs, or untrusted PR jobs. Choose a
publisher/signing mechanism with owner approval and budget before G2; do not
buy a certificate or publish a release merely to satisfy a checklist. Local
builds may produce unsigned internal artifacts, but must not label them official.
Only an explicitly requested minimal remote build/package/release is permitted;
it contains no tests, smoke, qualification or reproducibility checks, and uploads
or release publication require separate authorization. Do not create one merely
to satisfy this plan. Validate update signatures, trusted origin, rollback policy,
and provenance locally; a matching unsigned checksum alone does not establish
publisher identity.

Stable and beta are explicit channels with a supported current/previous
compatible version policy. Document schema incompatibility, security patch
urgency, model license changes, and any required re-pairing. Do not silently
update providers/models via floating tags; compatibility re-probes precede
activation. Never use rollback to bypass required security policy unnoticed.

Release blockers: unresolved critical/high-risk findings, leaking default logs,
missing essential permission/cost disclosure, unauthenticated LAN routes,
unbounded capture/queues, stale/duplicate audible turns, lost user data on
upgrade/removal, invalid signature, absent rights for included assets/models,
or missing evidence for an advertised deployment. Performance misses must be
disclosed and resolved or the supported scope narrowed explicitly.

Support readiness requires installation/upgrade/rollback/uninstall docs,
troubleshooting entries for shipped error codes, a private support contact,
bundle consent instructions, known-issues list, and named release/triage
owners. Proposed triage target during beta: acknowledge installation blockers
within two working days when maintainers are available; do not advertise a
24/7 SLA. Categorize defects by data loss/security, cannot install/start,
core voice failure, optional subsystem, and enhancement. Collect environment,
version, stage/code and reproducible steps first, not a full raw recording.

## 8. Requirements-to-delivery traceability

| Requirement | Owning tasks / milestone | Acceptance / gate |
| --- | --- | --- |
| R01: Honest baseline, planned status, license and provenance | D01 / M0 | G0; README and research accurately distinguish proposal from implementation |
| R02: No-hardware novice Windows first experience | F01-F05, V01-V04 / M1-M2 | AC-02/AC-03/AC-06/AC-07, G1-G2 |
| R03: Cloud/self-host choice with no silent paid/cloud fallback | V02-V03, H03, H07 / M2-M3 | AC-07/AC-10/AC-13, G2-G3 |
| R04: Maintainable native UI, adapters and isolated services | D02, F01, H02-H04 / M0-M3 | AC-01, G0/G3 |
| R05: Permissions, secure credentials, resume and device changes | V01-V02, H03 / M2-M3 | AC-05/AC-06/AC-10/AC-13 |
| R06: Supported Ubuntu discovery, pins, downloads and consent | H01-H02, H05 / M3 | AC-12, G3 |
| R07: Boot, readiness, graceful stop and targeted repair | H05-H06, F04, V06 / M1-M3 | AC-04/AC-12/AC-14 |
| R08: Upgrade/rollback/backups/uninstall preserve data | F02, V07, H05, P03 / M1-M4 | AC-02/AC-11/AC-12/AC-16 |
| R09: Authenticated LAN, version/capability negotiation, host-2 loss | H03, H06, P02 / M3-M4 | AC-01/AC-13/AC-14 |
| R10: VAD/STT/LLM/TTS streaming/cancel/backpressure/audio formats | D02, V01, V03-V04, H04, H07 / M0-M3 | AC-01/AC-03/AC-05/AC-08 |
| R11: Participant behavior, name/personality, observable silence | V05 / M2 | AC-09; expanded by R19/R22/R24 and AC-21/AC-24/AC-26 |
| R12: Opt-in perception and gaming resource budgets | P01-P02, P04 / M4 | AC-14/AC-15, G4 |
| R13: Inspectable/deletable/exportable memory and provenance | P03-P04 / M4 | AC-10/AC-16, G4 |
| R14: Unified status/doctor, fixtures, redaction and support | F03-F04, V06-V07 / M1-M2 | AC-03/AC-04/AC-10, G1-G2 |
| R15: F5 licensing/streaming truth, separate upstream asset terms | D01, H02, H04, A01 / M0-M5 | AC-01/AC-11/AC-17; research S15-S18/S26-S28 |
| R16: Hardware-dependent tuning/remote desktop remain optional | H06 / M3 | AC-14 only when enabled; no wiring/circuit-safety claim |
| R17: Measured real-OS/GPU gates, local validation, signing and support readiness | F05, V07, H06, P04, A03 / M1-M5 | G1-G5, all relevant AC cases; never fixture-only hardware support |
| R18: Optional later avatar and no-avatar parity | A01-A03 / M5 | AC-17, G5 |
| R19: Easy persona text editing, named profiles and swappable text files | V05a-V05b, V02 / M2 | AC-21; persisted data and runtime use, not permission or multiple autonomous agents |
| R20: User-changeable F5 reference audio/transcript and consented preview | H04a-H04b, H06 / M3 | AC-22, G3; actual reference replacement/voice evidence, separate model/voice rights |
| R21: Independently change compatible LLM and VLM models without rebuilding | V02c, V03 / M2 LLM; P02 / M4 VLM | AC-23; capability/consent revalidation and atomic idle-boundary selection, no universal compatibility promise |
| R22: Listen-first bounded context, selective replies and separate participation controls | V05b-V05c, V01 / M2 | AC-09/AC-24; ignored input is not a reply queue or LLM/TTS call, automatic listening separately qualified |
| R23: VAD-driven cut-off during TTS with feedback protection | V01, V04, H07c / M3, independent of F5/local STT | AC-08/AC-25; mandatory before advertising automatic speech interruption, PTT-only earlier profiles explicitly exclude it |
| R24: Adjustable helpful/sarcastic/silly/distracted/playful trolling response ratios | V05a-V05b / M2 | AC-26; test selection weights and human-perceived style separately, no permission/frequency override |

## 9. Handoff and evidence still required

The next implementation coordinator should assign D01-D02 and the short batch
above, preserve the no-GPU development lane, and maintain a gate ledger with
not-run/blocked/failed/passed states. Evidence records are created by future
work, not supplied by this plan.

Outstanding inputs: owner distribution/code-license preference; first API
preset/model/budget selection; actual GPU/OS/RAM/disk/LAN inventory
and a witnessed test arrangement; F5 intended-use/voice rights; publisher and
signing access/budget; exact "Project Airy" URL if reuse is desired; Live2D
classification before avatar release. Recommended choices and material
tradeoffs are in the main plan. The coordinator has accepted the Windows-only
.NET/WPF direction and UI-independent core; do not block foundation work on
re-approving it or on optional F5/Live2D distribution gates. No input requires
installing services, spending, or changing the owner's machines merely to
start D02/F01/F03.
