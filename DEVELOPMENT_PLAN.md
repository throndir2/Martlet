# Martlet development plan

**Proposed plan, researched 2026-09-12 (America/Los_Angeles). Documentation only.**

**Accepted avatar amendment, 2026-09-23:** develop both Live2D and VRM now,
independently of unpassed voice/release gates. Implement Audio2Face first as
the preferred speech-animation backend, alongside shared compatibility
contracts and parallel renderer development. Keep avatars OFF until enabled.
Renderer, analyzer and per-feature ownership are independent choices, with
explicit partial mappings and no silent fallback. See the
[avatar guide and permutation matrix](docs/AVATARS.md) and Delivery
A01-A03 / AC-27-AC-32. Development authorization does not grant SDK/model
downloads, license acceptance, installation, GPU inference, paid use or release
rights. Standalone modules and the internal normal Desktop route are implemented
and reviewed; native/runtime and end-user qualification remain open.

**Implementation status update:** the subsequent D01/D02-first-slice/narrow-F01
work is recorded in [Foundation decisions and boundaries](docs/FOUNDATION.md).
The current internal explicit typed/PTT API integration, preserved offline fixture and still-unpassed acceptance
gates are tracked in [Delivery](docs/DELIVERY.md); this original plan is not
a claim of live account/device qualification or qualified installation.
The implemented V04b path and remaining manual gates are in
[Explicit API conversation](docs/CONVERSATION.md); learned VAD and automatic
name/group listening remain off.
This plan's broader milestones remain proposals; that foundation does not
establish a working conversation, installer or passed release gate.

**Companion requirements update, 2026-09-19:** editable/swappable personas,
F5 reference-voice replacement, independent LLM/VLM selection, listen-first
participation, speech-triggered interruption and adjustable response-style
weights are now explicit product requirements. See
[Companion controls and behavior](docs/COMPANION_REQUIREMENTS.md) for the user
experience, current gaps and boundaries, and Delivery R19-R24 / AC-21-AC-26
for acceptance. This adds requirements, not implemented controls or permission
to enable listening, run models or spend money.

**Validation policy update, 2026-09-26:** the owner's
[validation policy](README.md#local-only-validation-policy) supersedes all
earlier hosted-CI plans and the 2026-09-13 local-gate requirement. Martlet is a
prototype: local tests, package/smoke gates and independent review are not
required per change. Do not create replacement remote validation; the
explicitly requested minimal build/package/release exception is not
authorization to create or publish one. Unrun qualification is NOT RUN, never
passed.

**V07c build foundation:** the [internal Windows packaging pipeline](packaging/windows/README.md#offline-provenance-and-cyclonedx-sbom-v07c-foundation)
records unsigned source/tool/dependency provenance and an offline CycloneDX 1.6
SBOM using its existing payload integrity boundary. This does not complete
signed distribution, license clearance, lifecycle qualification or V07/G1/G2.

## 1. Executive recommendation

**Reconciliation amendment, 2026-09-23:** the user chose to reconcile and reuse
the earlier implementation through reviewed PRs, then finish missing installation
features. The frozen canonical source is
`1715d194c03e9b1399c93c4f496f382e7076afc5`; it is not merged wholesale.
The [reuse ledger](docs/DELIVERY.md#prior-implementation-reuse-amendment-2026-09-23)
records the five-commit persona/settings-v3 baseline, independent Gateway/F5/Memory
imports, and subsequent single-owner v4 local memory -> v5 self-host voice ->
v6 perception -> v7 barge-in -> v8 remote memory integration with prerequisite
modules and fresh local gates. Installation persistence must use the next
compatible schema after that lineage, not redefine v3. Existing installation
research, pure planner, HostArtifacts and Gateway.Trust remain intact.
H01 PR #21 / `22d76df` stays held; PR #37, new host-journal work and installation
drafts remain on hold for reconciliation. No claim of all 69 commits integrated,
no old candidate receipt as new-main evidence, and no release/host authorization.
The imported companion cases use AC-21 through AC-26; AC-18 through AC-20 retain
their current installation meanings.

Build a companion that is easy to install, easy to understand when it is silent,
and safe to stop. Qualify one complete Windows voice experience without dedicated
AI hardware as the core release path. Independent optional-avatar development
can proceed now without delaying or weakening that qualification.
Installation, privacy controls, and diagnosis are product features,
not final release chores.

The recommended sequence is:

1. Approve the few consequential decisions and define executable contracts.
2. Ship an internal installer skeleton with a clearly labeled offline fixture
   demo, an accessible status page, and the same diagnostic engine in a CLI.
3. Complete the real API-backed microphone -> VAD -> STT -> talk decision ->
   LLM -> TTS -> playback path, with explicit data/cost consent and recovery.
4. Qualify Ubuntu self-hosting, first on one host and then across the owner's
   two GPU PCs. Do not call it supported until the actual topology passes.
5. Add useful, permissioned vision and inspectable memory under strict budgets.
6. Qualify optional Live2D and VRM renderers without making voice depend on them;
   shared contracts, Audio2Face-first analysis and renderers develop in parallel
   now, rather than waiting for M2-M4 release exits.

All components, commands, endpoints, defaults, estimates, and support promises
below are **proposed**, unless specifically labeled **observed**, **verified
upstream**, or **accepted direction**. A source proving an upstream feature is
not evidence that Martlet has integrated or tested it.

**Accepted direction, 2026-09-12:** the implementation coordinator confirmed the
self-contained .NET/WPF Windows path, a UI-independent provider/audio/diagnostic
core, and independently versioned Ubuntu inference roles. Proceed with that
foundation without reopening broad stack selection unless a concrete
audio/packaging constraint invalidates it. Exact dependency pins, license
grants, signing, provider spending, and optional-feature distribution remain
separate decisions; no software is implemented by this plan.

**Installation direction update, 2026-09-22:** design one feature-first setup
coordinator for API, self-hosted and mixed routes, with multiple roles per
machine and optional features genuinely optional. Provide guided Ubuntu
Desktop host setup as well as a headless path; use Docker Engine + Compose
under the guided Linux experience, not mandatory Docker Desktop. Advanced
users can manage Compose themselves. Evaluate native Windows Ollama first
for simple Windows hosting and Docker Desktop/WSL2 as a separate advanced
lane. These are planned support lanes, not enabled routes or qualified hosts.
Upstream findings are recorded in [S30-S35](docs/RESEARCH.md#s30); the
[installation flow](docs/INSTALLATION_SUPPORT.md#feature-first-multi-machine-setup)
and H08-H10/AC-18-AC-20 in [Delivery](docs/DELIVERY.md) define the remaining work.

**Voice direction update, 2026-09-23:** the owner requested all five self-hosted
voice engines: F5-TTS, Qwen3-TTS, Chatterbox, GPT-SoVITS and XTTS-v2.
[Voice setup and comparison](docs/VOICE_STUDIO.md) records implementation
research, guided isolated installation, local audio/reference/dataset import,
engine-supported fine-tuning, loading progress and switchable A/B previews
before applying a conversation voice. VS01-VS06 define staged acceptance.
All five remain support targets, not qualified providers. F5-only deployment
descriptions below are the historical first candidate, not an exclusivity rule.

## 2. Repository baseline

Observed in the assigned isolated worktree at initial inspection:

| Area | Actual state |
| --- | --- |
| Git baseline | Commit `33bd9a8`, subject `Initial commit`; its tree contains no tracked files |
| Working tree | Clean before this documentation work; only the worktree's `.git` pointer was present |
| Application and infrastructure | No application code, dependency manifests, packaging scripts, services, CI, tests, or models |
| Documentation and instructions | No README, development docs, `AGENTS.md`, `.github` instructions, or contribution workflow in the worktree |
| Licensing | No license file; repository metadata reports no license |
| Repository metadata | Default branch `main`; repository not archived |

This is a greenfield **implementation**, established by inspection rather than
assumed. These planning documents are the first project files. There is no
existing behavior to migrate and no build/test command to run. No inference,
hardware, installer, or integration validation was performed for this plan.
Future sessions must inspect their own baseline again rather than assuming it
remains empty.

## 3. People, product, and boundaries

| Persona | Need | Design consequence |
| --- | --- | --- |
| Windows gamer | Talk naturally without reducing game performance or fighting setup | Native audio, quick pause/mute, no desktop GPU inference requirement, no avatar requirement |
| Novice Ubuntu hardware owner | Start/reboot services without remembering Python environments or Linux commands | Guided preflight, reviewed host setup, persistent role profiles, explicit readiness and repair |
| Mixed-machine owner | Combine an API, services on this Windows PC, and Ubuntu hosts without several unrelated setup procedures | One role/destination plan, graphical local host setup, independent readiness and clear ownership of existing services |
| Developer without GPU hosts | Build and diagnose the real product without pretending to test CUDA | Deterministic fixtures, provider contract harness, opt-in API lane, optional CPU STT |
| Privacy-focused self-hoster | Keep conversation on chosen machines | Paired LAN endpoints, explicit routing, no cloud fallback, local data controls |
| Avatar enthusiast | Supply a character, lip sync, and lifelike movement | Optional Live2D/VRM, Audio2Face-first speech animation, selectable feature owners and comparisons; separate SDK/asset rights and performance budget |

The companion is a participant, not an automatic responder to all detected
speech. A configurable name, direct address, or push-to-talk can request a
response. Otherwise an independently observable turn-taking policy may remain
silent. Personality changes wording and style, not permission, safety, routing,
or whether the microphone is allowed to capture.

Users must be able to edit and save named personas or swap persona text files,
choose an F5 reference voice, and independently change compatible conversation
and vision models without rebuilding the app. Response-style weights (helpful,
sarcastic, silly, distracted, playful trolling/teasing) are separate from the
decision to speak. Opt-in conversational listening gathers bounded recent
context rather than producing an answer per input; qualified live VAD must
eventually let a person cut off Martlet mid-sentence. Manual PTT/Stop remains
the default until automatic listening, echo protection and native privacy are
qualified. See the [detailed requirements](docs/COMPANION_REQUIREMENTS.md).

### MVP includes

One supported Windows x64 installer; no-avatar tray/control UI; mic/output
selection and live meters; CPU VAD; one documented cloud provider preset for
STT, LLM, and TTS; typed input/output fallback; push-to-talk and name-addressed
turns; conservative optional participation; bounded streaming playback; stop
and interruption; resumable onboarding; Credential Manager storage; costs and
data destinations; status/doctor/self-test/support bundle; upgrade, rollback,
repair, and data-preserving uninstall.

The first API preset is **proposed to use OpenAI**, because its documented
transcription, text generation, and speech interfaces can cover one account.
This is not an exclusivity decision, a free-service promise, or authorization
to make billable requests. Select and pin models only after availability and
capability checks; do not equate a model listing with permission to use it.
See [S08-S12](docs/RESEARCH.md#s08).

MVP group participation means multiple people audible on a consented microphone
or an explicitly supported input feed. It does not establish speaker identity.
Mixing remote call/game audio, speaker diarization, Discord bots, and robust
full-duplex room-speaker behavior are separate later qualifications. A PC
microphone does not automatically give access to remote participants' audio.

### Explicit non-goals for MVP

No autonomous game control, keyboard injection, anti-cheat hooks, general tool
execution, public-hosted Martlet backend, account system, commercial voice
cloning catalog, universal inference compatibility,
mandatory Docker on Windows, embedded Python/CUDA suite, always-on screenshots,
automatic cloud rerouting, cross-platform desktop promise, or required avatar.

Training/fine-tuning remains outside the API MVP but is now explicitly in
scope for the optional self-hosted Voice Studio, where the chosen engine has
a verified recipe. Reference-clip cloning does not require training.

No wiring/electrical guidance or claim that GPU power limits establish circuit
safety. No automatic installation of drivers, remote-desktop tools, models, or
privileged services. Later host setup must obtain specific consent.

## 4. Deployment profiles and rollout order

| Profile | Execution and destination | Prerequisites | Eligibility |
| --- | --- | --- | --- |
| P0: Try/demo | Deterministic prerecorded fixtures and simulated provider events; clearly marked NOT AI | Windows app only; optional mic/speaker local tests | M1; offline, no credentials, no inference claims |
| P1: API voice | Windows capture/VAD/playback; selected cloud STT/LLM/TTS over HTTPS | Supported Windows, mic/output, internet, user-provided authorized API credentials | M2 golden path; no Git/Python/Node/Docker or separate runtime installation |
| P2: Existing endpoints | Client adapters use explicitly configured STT/LLM/TTS routes; may be mixed local/cloud only by consent | Reachable endpoints and known adapters; TLS/authentication for LAN | M2 expert configuration; only tested provider tuples qualify |
| P3: One Ubuntu host | Gateway plus selected LLM/F5-TTS/STT workers on one machine; Windows remains lightweight | Qualified host manifest, accepted model/voice terms, paired client | M3 self-host beta; privacy-local microphone input requires local STT too |
| P4: Two Ubuntu hosts | Host 1 LLM/F5-TTS, host 2 later vision/OCR/detection/memory; client owns routing | Each host independently qualified and paired | M3 topology qualification with host-2 fixtures, M4 real perception workloads |
| P5: Optional avatar | Separately enabled Live2D or VRM renderer; independent speech analyzer and motion sources | Selected rig/mapping/runtime, explicit activation and applicable SDK/model/asset rights | Development now; M5/G5 qualification before support/distribution; P1-P4 remain usable without it |
| P6: Windows hosting | Selected roles on the client PC or another Windows PC; native Ollama candidate first, Linux containers via Docker Desktop/WSL2 as a separate advanced lane | Named adapter, supported runtime/model/OS tuple, approved local setup; paired gateway for another PC | H10 expansion; neither lane is currently qualified; no native F5 or Windows Server promise |

Profiles are presets, not mutually exclusive modes or a fixed machine count.
Persist enabled features, required roles, role destinations, host identities
and installation ownership separately. A machine may be both client and host;
one host may serve several roles. Co-located roles share real CPU/RAM/VRAM/disk
budgets, even when separate containers or a WSL VM are involved.

Typed text conversation requires an LLM but no STT, TTS, microphone or speaker.
Microphone input adds capture and STT; spoken replies add TTS and output.
Perception, persistent memory and avatars add only their selected dependencies.
An API LLM plus self-hosted TTS is valid; a "fully local" badge requires every
enabled data path to be local. Do not download, start, probe or demand keys for
disabled roles. Disabling a feature preserves data by default and does not
silently reroute its work to a cloud provider.

Installation has three ownership modes: **guided managed host**, **advanced
user-managed Compose**, and **connect existing service**. The last two never
authorize Martlet to adopt, upgrade or remove another application's resources.
The coordinator shows one plan across machines; each new managed remote host
needs a local trusted setup/approval and pairing step, not a remote admin
credential in the desktop. Ubuntu's host setup UI is not a Linux port of the
WPF companion. See the installation design for the exact handoff and probes.

Ubuntu CPU fixtures are a development profile, not a promise of responsive CPU
F5-TTS or large-model inference. Optional `whisper.cpp` on the Windows CPU is a
later private-STT route, selected and benchmarked rather than forced on gamers.
Offline AI works only after the necessary local models and licenses are present;
offline fixtures work without those downloads.

## 5. Proposed architecture decisions

AD-01's .NET/WPF direction and AD-03's UI-independent core/isolated-role
boundaries are accepted for implementation; D01 records their detailed
disposition. AD-09's dual-renderer/Audio2Face-first development is also accepted
by the 2026-09-23 amendment. Other choices remain recommendations. Approval authorizes design
direction, not purchases, public releases, or host modification.

| Decision | Recommendation | Material alternative / tradeoff | Resolve by |
| --- | --- | --- | --- |
| AD-01: Desktop | Accepted .NET/WPF/self-contained Windows direction; propose .NET 10 LTS `win-x64` and WASAPI through a pinned NAudio adapter | Tauri + web UI improves future portability/Live2D Web reuse, but adds Rust/JS/WebView deployment boundaries. Avalonia broadens OS reach but increases initial audio/packaging matrix. Electron eases AIRI-style reuse with a larger baseline footprint | Record in D01; verify pins in F01 |
| AD-02: Packaging | Signed per-user Inno Setup EXE, ordinary unpacked self-contained application files, stable application identity | MSIX offers OS package identity/update behavior but needs certificate/sideload/App Installer policy qualification. MSI/WiX suits managed estates but adds authoring and current release-terms review. ZIP is developer-only, not a second novice support path | D01, F02 |
| AD-03: Runtime ownership | Client runs turn orchestration; shared .NET core/doctor; optional ASP.NET Core Linux gateway; inference workers isolated by engine | Python/FastAPI gateway is viable if team expertise favors it, but duplicates core contract/probe implementation across languages. Do not embed all inference in the desktop | D01, D02 |
| AD-04: First AI route | One explicit API provider preset; typed fallback and separate fixtures | Local-first CPU STT + local LLM reduces cloud exposure but adds model downloads, latency, and hardware-dependent installation before basic UX is proven | D01, V03 |
| AD-05: Self-hosted LLM | Evaluate Ollama first for simple model/service lifecycle; use its own tested adapter | `llama.cpp` server gives GGUF/control flexibility; vLLM may suit higher throughput but adds compatibility/scheduling demands. One golden host backend first, not three | H02 |
| AD-06: Host deployment | Ubuntu 24.04 LTS x86_64 Desktop/Server first; guided host UI over reviewed bootstrap and pinned Docker Engine/Compose services; advanced user-managed Compose uses the same role contracts | Native existing services remain connect-only candidates. Docker Desktop on Linux adds a VM and is not the managed-host default. Other distributions/versions and Windows native/WSL lanes need separate qualification | H01-H02, H09-H10 |
| AD-07: LAN trust | TLS 1.2+ with per-host pinned identity, out-of-band pairing token, scoped per-device credentials; no public ports | Managed CA/mTLS may suit managed homes/labs but complicates first run. Do not offer unauthenticated HTTP LAN as a supported shortcut | H03 |
| AD-08: F5 and voice rights | Optional noncommercial experimental profile until permitted usage is confirmed; supply a consented reference voice plus transcript | A commercial-compatible TTS provider or differently licensed weights may be needed. User-supplied files do not remove license/voice-rights duties | D01, H02, H04 |
| AD-09: Avatar | Accepted Live2D + VRM development now; Audio2Face first/preferred, independent renderer/analyzer, explicit per-channel composition; OFF by default | Detailed authored VRM face for richest A2F output; approved reduced Live2D mapping, amplitude baseline and later named alternatives; no silent fallback or universal rig claim | A01 contracts/development; A03 rights/runtime/release qualification |

.NET self-contained deployment includes its runtime; it does not automatically
bundle every native library or make Windows policy restrictions disappear.
Runtime security patches must therefore be redistributed with Martlet.
Inno Setup supports non-administrative installs and signed setup/uninstall;
its current license and commercial-license request must be reviewed for the
selected release. These are sourced facts, not packaging validation
([S01-S07](docs/RESEARCH.md#s01)).

See [architecture](docs/ARCHITECTURE.md) for topology, contracts, consent boundaries,
and precise failure handling. See [installation/support](docs/INSTALLATION_SUPPORT.md)
for the user journey and host lifecycle.

## 6. Evidence that changes the plan

**F5-TTS:** upstream has a Python package, CLI, Gradio, Docker examples, and a
socket server. The inspected source declares package version `1.1.22`; that is
an observation, not the selected or tested release. Code is MIT; the official
weight card is CC-BY-NC-4.0. The inspected stream generator samples and vocodes
each text chunk before yielding slices of its waveform. Plan sentence/chunk
synthesis plus streaming audio transport; do not advertise incremental
within-chunk synthesis or guaranteed cancellation of GPU work. Empty reference
transcripts can trigger extra Whisper loading, with extra download/VRAM cost.
Pin and inspect every auxiliary model too ([S15-S18](docs/RESEARCH.md#s15)).

**Compatibility:** Ollama explicitly documents compatibility with *parts* of the
OpenAI API. An arbitrary `/v1` URL is not a universal contract. Native
capabilities, error envelopes, stream schemas, cancellation, tools, and images
must be tested per provider/model/version ([S13](docs/RESEARCH.md#s13)).

**Project Airy:** no verified exact identity was established. The plausible
candidate is [Project AIRI, `moeru-ai/airi`](https://github.com/moeru-ai/airi),
whose own README describes a desktop/web companion and whose repository code
license is MIT. Its provider settings, state transitions, and optional-character
UX are useful study topics. This does not justify adopting its whole stack,
copying assets, or claiming the user meant AIRI. Confirm the URL before making
it a dependency or reuse foundation ([S26](docs/RESEARCH.md#s26)).

**Live2D and detection:** Live2D distinguishes SDK development, release, and
Expandable Applications; an app importing users' avatars needs classification
review before release, not before independent authorized development.
Audio2Face animates faces, not whole-body gestures; VRM preset expressions are
optional, and detailed ARKit-compatible mapping requires a suitable authored
rig. Live2D is a full renderer target, with model-specific reduced mappings
rather than assumed ARKit shapes. See [S42-S47](docs/RESEARCH.md#s42) for
primary-source constraints and later analyzer candidates.
Ultralytics advertises AGPL-3.0 or Enterprise terms, not universal
permissive use. Select actual implementation and model licenses before
shipping either feature ([S27-S28](docs/RESEARCH.md#s27)).

## 7. Delivery model and feasibility

| Milestone | User-visible exit | Coarse effort, not a schedule |
| --- | --- | --- |
| M0: Decisions and contracts | Agreed license/distribution direction and provider/diagnostic schemas | 1-2 person-weeks |
| M1: Installable diagnostic shell | Clean-machine install, offline fixture demo, doctor, safe uninstall | 2-4 person-weeks |
| M2: API voice MVP | Real consented mic-to-voice, turn policy, recovery, signed release candidate | 5-9 person-weeks |
| M3: Self-host beta | Repeatable Ubuntu role setup, F5 adapter, local STT option, witnessed one/two-host gates | 5-10 person-weeks plus hardware/driver/rights waiting time |
| M4: Perception and memory | Opt-in screen understanding, useful retrieval, delete/export, host-2 isolation | 4-8 person-weeks |
| M5: Optional avatar qualification | Both renderer targets, Audio2Face-first animation, explicit compatible/degraded permutations, safe import and no-avatar voice parity | Original 3-7 person-week estimate needs re-estimation for dual renderers/composition; independent development now, hardware/rights/release lead time separate |

These are uncertain engineering effort ranges for an experienced small team,
including product diagnostics and hardening, not calendar commitments. Novice
user trials, Windows audio edge cases, signature reputation, model suitability,
and GPU availability can dominate elapsed time. Re-estimate after M1 and after
the first measured F5/LLM concurrency run. Do not sum these into a promised date
or hide support work in "polish."

[Delivery](docs/DELIVERY.md) defines small task IDs, dependencies, named
acceptance cases, ownership boundaries, release gates, and requirement
traceability. The original foundation sequence was D01-D02, then F01-F04.
The accepted avatar expansion now starts A01a shared boundaries and A01b
Audio2Face, with A02a Live2D and A02b VRM in independent worktrees. A02c
composition/configuration/playback follows those boundaries. No workstream
starts a multi-GPU stack or changes the installation/settings lineage.

## 8. Risks, unresolved decisions, and constraints

| Risk / unanswered question | Recommendation and mitigation | Owner / gate |
| --- | --- | --- |
| Distribution/license intent is unknown | Decide private personal tool vs redistributable project; recommend a permissive code license only after owner approval. Keep weights/SDKs separately licensed | Product owner, D01 |
| API destination, account eligibility, and cost tolerance | Recommend one opt-in provider and explicit per-session ceilings; offer local/fixture paths. No provider is enabled or charged by this plan | Product owner, V03/G2 |
| Concrete audio/packaging constraint could invalidate accepted stack | Keep .NET/WPF; use a time-boxed audio/installer spike to resolve actual constraints, not another broad framework survey | Technical owner, F02/V01 |
| No actual GPU inventory or developer access | Request OS, CPU, RAM, GPU model/count/VRAM, driver, disk, and LAN inventory from hardware owner; establish witnessed test access, not guessed minimums | Hardware owner, H01/G3 |
| LLM and F5 may not coexist within VRAM/latency budgets | Measure combined load at selected context/voice settings; serialize jobs or choose smaller models with explicit consent; label unqualified combinations unsupported | Host/runtime owner, H04/H06 |
| F5 weights or reference voice unsuitable for intended use | No bundled restricted voice/model; clear intended usage or select a rights-compatible alternative | Product/legal owner, H02/H04 |
| Headset, room speakers, Bluetooth, and remote participants differ | Headset + explicit address first; test device churn and manual barge-in; do not claim generic echo cancellation or speaker identity | Audio owner, V01/V05/H07 |
| Provider drift/outages or unclear compatibility | Pin adapter fixtures and model catalog snapshots; surface unsupported/degraded states; never paid/cloud fallback | Provider owner, V03 |
| Host 2 failure could block conversation | Optional context gets short deadlines and freshness metadata; voice remains independent and reports unavailable context | Core owner, P02 |
| Installer signing credentials and publisher budget absent | Internal unsigned artifacts only in controlled development tests; public/novice release blocked until trusted signing path is approved | Release owner, G2 |
| AIRI identity and desired reuse unknown | Treat as candidate inspiration only; request exact link before dependency/reuse decisions | Product owner, D01 |
| Avatar import may be an Expandable Application | Continue authorized development; obtain Live2D classification/terms before release; no-avatar remains fully supported | Product/legal owner, A03b |
| Preferred Audio2Face or model rig unavailable | Show compatibility separately from readiness/performance; retain valid selected aspects only with explicit masks/mappings; do not silently use amplitude or claim missing blendshapes exist | Avatar owner, A01-A03 |

The development plan itself is not blocked by these unknowns. Subsequent
implementation can advance fixtures and contracts while decisions are made,
but cannot claim the relevant release gates have passed.

## 9. Definition of success

A novice can install without a development toolchain, tell where data goes,
complete a real conversation, and diagnose a deliberately disconnected
microphone or provider without a terminal. The hardware owner can later
reboot a qualified Ubuntu host and understand whether services are starting,
warming, ready, or broken. Silence has an understandable reason; a broken
optional subsystem does not make the whole product unusable.

The user can change persona text, response-style mix and supported models from
settings, and replace an F5 reference voice when that route is available.
Qualified conversational mode listens selectively, uses bounded recent context
and yields to human speech; it does not answer every observed input. These are
separate delivery gates, not capabilities implied by the current PTT integration.

Every advertised deployment has a signed/pinned artifact set, a completed
clean-machine and lifecycle record, accurate documentation, a private support
route, and the appropriate real-hardware evidence. Proposed targets and
fixtures are never substituted for those records.
