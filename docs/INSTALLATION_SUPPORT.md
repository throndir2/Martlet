# Installation and support design

The implemented [V06b Desktop troubleshooting path](TROUBLESHOOTING.md) now
provides passive shared status/remedies, explicit local metadata recording and
frozen preview/default-No consent/local ZIP export. There is no configured
support contact/upload channel and no CLI export. The broader specification
below remains a target, not a claim that those services are available.

**Future product specification, not an end-user installation guide.**
The [foundation](FOUNDATION.md) implements a narrow local status CLI and WPF
shell. The current [internal packaging recipe](../packaging/windows/README.md)
and [offline fixture experience](DIAGNOSTICS.md#offline-fixture-experience-f03c)
now provide a self-contained payload/unsigned installer build and real Desktop/
Doctor demo commands. The broader host utilities, live setup and qualified
installation journey below remain future work. Do not run
privileged setup or download models as part of implementing this documentation.
See [delivery](DELIVERY.md) for gates and [architecture](ARCHITECTURE.md) for
contracts and privacy. All version/hardware combinations below are targets
until recorded qualification evidence exists.

The configuration-only [V02a setup slice](SETUP.md) implements resumable
Choice/Destinations/Credentials/Review checkpoints and explicit Windows vault
actions. V02b additionally implements explicit local input/output selection,
bounded actual-amplitude capture tests, synthetic-tone output tests and scoped
human audibility confirmation. These are historical local observations, not
physical qualification, capabilities, first response or novice readiness, and
do not authorize live API use. The rest of this storyboard remains a target.

[V04b's explicit API conversation guide](CONVERSATION.md) now provides the
implemented typed/text-only, optional voice and PTT flow, exact supported
models/bounds, actionable same-engine remedies and a separately authorized
manual first-conversation checklist. It joins the existing setup and local
audio choices without changing installer permissions or introducing background
listening. This functional integration is not a witnessed account/device/novice
trial, clean Windows installation or signing/release qualification.

## 1. One Windows golden path

Target Windows 11 25H2 x64 Home/Pro on a currently serviced patch. Test a clean
standard-user account with no Git, Python, Node, Docker, developer SDK, or
preinstalled .NET required by Martlet. The app bundles its selected .NET runtime
and necessary redistributable native dependencies. No desktop inference GPU is
required for P1. Windows 24H2 is a compatibility candidate only while its edition
is serviced; it is close to Home/Pro end-of-servicing at the research date
([S03](RESEARCH.md#s03)). ARM64, Windows 10, Linux/macOS desktop, and enterprise
deployment tooling are not initial support claims.

**Packaging proposal:** signed per-user Inno Setup EXE, normal install/uninstall
registration, Start menu shortcut, local help, and bundled diagnostic CLI.
Install to the user's application directory without an elevation prompt in the
golden path. Keep code and mutable data separate. Default install must not add
firewall exceptions, install services, modify drivers, start at login, fetch
large models, or require a browser login.

Publish a checksum and signed provenance for the exact installer later.
Signature verification and SmartScreen/policy issues are support cases:
do not tell users to disable antivirus or click through an unknown publisher.
Unsigned artifacts are internal development artifacts, not the novice route.

A self-contained ZIP is a developer/support diagnostic artifact only in M1-M2,
not a promised fully portable mode. If supported later, specify what remains in
Credential Manager/AppData, absence of automatic updates/uninstall registration,
and version coexistence. Do not silently store credentials next to a portable EXE.

### First-run storyboard and resumable state

| Step | User experience | Persisted checkpoint / probe |
| --- | --- | --- |
| 1. Download/install | Official future release page states supported OS, publisher, size, no-GPU API option, and planned costs | Verify signature, OS/architecture, free disk, standard-user writeability; display install failure code and log location |
| 2. Welcome | Choose **Try fixture demo**, **Use an API provider**, or **Connect existing endpoints**; "No avatar" is normal | Profile and onboarding schema version; no credential or network call needed for demo |
| 3. Explain data | Show mic -> STT destination, text -> LLM, response -> TTS; screen and memory off; generated voice disclosure | Explicit consent per role/destination; not one blanket analytics checkbox |
| 4. Configure provider | Select known preset or named endpoint adapter; show HTTPS origin, model and current pricing/retention links | Save role routes; validate URL without sending secrets to arbitrary redirects |
| 5. Credentials/pairing | Paste key once in protected field or pair with host; never require editing `.env` | Store OS credential reference, not secret in config; prove retrieval after restart |
| 6. Microphone | Explain Windows microphone privacy controls, desktop-app access, input selection and local level meter; capture only on Test | Fixed endpoint or default-follow policy; permission/device probe; no cloud upload from the meter |
| 7. Output | Choose headset/output, safe-volume tone and local consented sample playback; confirm heard | Format/open/playback probe; test requires no TTS provider |
| 8. Capabilities | Verify configured STT/LLM/TTS operations, model access, voice and limits; show warming vs ready | Timestamped results; user explicitly approves small potentially billable inference tests |
| 9. First response | Prompt a short PTT utterance; show stages and play the returned voice | End-to-end real-turn result, trace ID, stage latency; user confirms audibility |
| 10. Ready | Explain Stop, mic mute, pause-all, tray/Exit, diagnostics, and optional name mode | Onboarding complete, chosen name/personality; avatar remains disabled |

Every step supports Back, Save and exit, and resume without repeating accepted
steps unnecessarily. Commit checkpoints atomically. Revalidate credentials,
device IDs, capabilities, and consent if destination/model/configuration changes;
never resume an old recording or start listening because a checkpoint was saved.
When permissions or policies prevent progress, keep setup and typed diagnostics
usable with an exact remedy, not a blocked full-screen wizard.

Selecting existing endpoints is not selecting a universal "OpenAI mode." Each
role needs a verified adapter/capability; LLM-only endpoints do not supply STT
or TTS. Show missing roles and let the user deliberately choose replacements
without turning on a paid cloud service automatically.

### Companion customization after setup

The planned [Companion controls](COMPANION_REQUIREMENTS.md) must remain
accessible after onboarding, without a reinstall or source-code change:

| Surface | Planned controls and safe application |
| --- | --- |
| Companion | Named personas, multiline editor, explicit plain-text import/export, save/duplicate/select, and separate response-style weights; show unsaved/active revision |
| Participation | Explicit typed/PTT default; opt-in conversational listening, rate/gap/cooldown/context retention and qualified speech interruption; show why unavailable or silent |
| Models | Independent LLM and VLM adapter/destination/model choices, role limits and readiness; VLM selection never enables screen capture |
| F5 voice | Reference audio and matching transcript, explicit reload after file replacement, named presets, validation and separately authorized preview; no automatic reference transcription |

Apply at idle or after explicit Stop and owned cleanup, never midway through
authorized work. Show an actionable error for invalid persona files, all-zero
style weights, changed/missing reference audio, incompatible models and
unqualified barge-in. Preserve prior saved choices without pretending a failed
Apply succeeded or falling back silently. Save/resume preserves inert choices,
not listening, spending, preview permission or model readiness. The current
setup UI does not yet implement these companion surfaces.

### Provider cost and mode controls

No promised free quota, trial balance, perpetual free tier, or bundled API key.
Account availability, regional eligibility, model access, and current prices
are external constraints. A ChatGPT subscription is not treated as proof of API
quota. Keep rates/model choices in a dated configurable provider catalog and
link to authoritative pricing ([S11](RESEARCH.md#s11)).

Set bounded input duration, output tokens, request rate, and a user-confirmed
session spend ceiling before real use. Track usage and estimated cost where
reported; label unknown or delayed usage honestly. Local budget enforcement
can stop new Martlet requests but is not a guarantee about provider invoices,
in-flight charges, taxes, or other clients using the same key. When estimates
are unavailable, require explicit limits/confirmation rather than imply zero.
Honor provider-side budgets if available, without assuming they are hard caps.

The fixture demo uses authored/appropriately licensed synthetic events and
locally generated tone PCM (not prerecorded speech). It exercises the real
validator/session/status path, plus the same sink after explicit tone permission, but
does not prove STT accuracy, model quality, cloud access, CUDA, or hardware
latency. A local mic test is real capture, not proof of AI transcription.

### Audio/device recovery

Explain fixed-device versus "follow default" behavior. Store endpoint identity
and friendly label; labels alone are not unique. Handle reconnect, changed
Windows default communications device, disabled input, Bluetooth headset
profile changes, sample-rate changes, exclusive use, and sleep/resume.
Show a meter for the **actual selected input**, not a simulated waveform.

On fixed endpoint loss, stop relevant capture/playback and offer selection;
do not unexpectedly play private speech on room speakers. Reset buffers and
resample negotiation on device re-open. Do not replay half-spoken responses
automatically. Test notification races while playing and while canceling.
Text input/output and doctor remain operable when every audio device is absent.

## 2. Ubuntu host support and qualification

### Proposed matrix

| Layer | First qualification target | Required evidence / exclusions |
| --- | --- | --- |
| Host OS | Ubuntu 24.04 LTS x86_64, latest qualified security point release/kernel | Record exact OS/kernel and reboot outcome; 22.04/26.04 are separate expansion lanes, although Docker and NVIDIA list them upstream |
| CPU/RAM | x86_64 with instruction set required by selected native wheels/engine; provisional planning envelope 4+ CPU cores, 32 GiB RAM | Not a proven minimum; inventory actual system and measure combined load, swap, and context size |
| GPU | NVIDIA discrete GPU(s), each selected by UUID, exact models/VRAM currently unknown | No generic "CUDA-capable = supported" claim; card compute capability must match all selected binaries/kernels |
| Driver | One tested distro-packaged NVIDIA branch meeting each chosen image's CUDA and GPU requirements | Numeric floor/pin unresolved until H02; Secure Boot/module status and post-reboot readiness required |
| Container layer | Docker Engine + Compose v2 plugin + NVIDIA Container Toolkit, exact tested versions | Independently verify host GPU visibility and GPU visibility inside pinned container; no dependency on Windows Docker Desktop |
| LLM runtime | One selected Ollama image digest and model digest/quantization/context | Fit and concurrency measured with F5; other engines separate qualification |
| F5 runtime | Separate image with pinned Python, PyTorch, torchaudio, CUDA user-space libraries, F5, vocoder, audio dependencies | Python 3.11 is an upstream example, not a certified environment; current broad dependency ranges are not a release lock |
| Host 2 | Same OS/toolkit policy; role-specific CPU/GPU/memory budgets | Do not assume it needs or can hold every VLM/OCR/detector/reranker simultaneously |
| Network | Private LAN; wired connection recommended for first measurements | Record RTT/loss/throughput; Wi-Fi and hostname discovery have separate failure tests |

**There are zero Martlet-qualified GPU/driver/image/model tuples at this stage.**
H02 must publish a machine-readable support manifest with the exact tested
OS/kernel, CPU features, GPU model/VRAM, driver, Docker/Compose/toolkit versions,
image digests, model revisions/checksums, precision, context size, concurrency,
RAM/disk peaks, and observed warm/steady latency. Missing rows block a support
claim; do not fill them with guessed values.

Do not prescribe one CUDA version to every GPU generation. `nvidia-smi`'s
reported CUDA compatibility is not proof that PyTorch or the container can
execute the required kernels. Check runtime/driver/GPU compatibility using
the selected versions and a real inference smoke run
([S20-S22](RESEARCH.md#s20)). AMD/ROCm, Intel, Jetson/ARM, WSL-hosted inference,
rootless GPU containers, and unmanaged existing Python environments are
out of the initial supported host matrix.

### Read-only discovery and preflight

The proposed packaged `martlet-host doctor` starts without changing the machine.
It produces readable results and a sanitized JSON report. Check OS/kernel,
CPU architecture/features, RAM/swap, free disk/inodes and storage location,
GPU UUID/model/VRAM/driver, container engine/plugin/runtime presence and access,
existing services/port collisions, DNS/TLS/clock, LAN interface/address, and
whether an admin action/reboot is pending.

Distinguish stages: GPU exists; driver responds; container can see GPU; chosen
framework loads; model artifact is valid; model fits; model warmed; one actual
inference succeeds. A container probe that requires pulling an image is not
read-only discovery and must obtain download consent first. Unavailable or
permission-denied probes are **unknown**, not successful.

For each missing prerequisite, present observed value, required value,
official instruction link, exact proposed remedy for the detected version,
expected privilege, estimated download, expected reboot, and a Verify button.
Do not silently remove conflicting Docker/container packages or alter an
existing machine's driver just because an upstream quickstart suggests it.

### Guided, explicit host bootstrap

1. User downloads a signed/checksummed, versioned host bundle through the
   documented channel. Display and verify the artifact manifest; review its
   intended changes locally. No blind curl-pipe-root execution.
2. Preflight distinguishes unsupported from repairable. Choose a profile:
   `voice-host` (LLM/TTS, optional STT), `context-host` (optional perception/memory),
   or `single-host` with explicitly chosen roles. CPU fixture-only mode remains
   available to developers.
3. Show installation plan and storage/download/license summary. Obtain separate
   approval for package repositories, driver/container runtime configuration,
   firewall/service startup, each model/voice, and advanced GPU power policy.
   No blanket consent to arbitrary future changes.
4. Apply only necessary approved prerequisite steps using official signed
   packages and pinned artifacts. Privileged steps are local and narrowly
   scoped; ordinary inference runs non-root. Never grant the desktop a host
   admin token or silently add the user to the root-equivalent Docker group.
5. Persist a journal of completed steps and desired state. Re-running preflight
   and setup must reconcile rather than duplicate repositories, rules, users,
   volumes, or services. After interruption/reboot, resume with revalidation.
6. Download selected images/models with per-artifact progress, bytes, disk
   forecast, pause/cancel/resume, retries, and checksum verification. Check
   source revision/ETag for resumed ranges; a changed remote file restarts
   safely. Finalize atomically only after validation.
7. Start selected workers, load models, warm with a consented synthetic sample,
   and display readiness per role. Then enable deliberate LAN pairing. Present
   a local status summary and a pairing card, not just a Docker log dump.
8. Verify reboot/autostart and stop behavior on the owner's schedule. A
   successful initial launch is not proof of persistence across reboot.

Docker GPU reservations assign the exact intended device; `capabilities: [gpu]`
is required, and `count` and `device_ids` are mutually exclusive upstream
([S23](RESEARCH.md#s23)). Reservations are not a per-container VRAM quota.
Budget and schedule shared LLM/F5 loads explicitly. No default `privileged`,
host-network, or desktop Docker socket mounts.

### Downloads, licenses, and storage

Show **exact selected-artifact bytes** before consent, plus expanded image
size, staging space, rollback retention, and user-data reserve. Metadata can
inform planning but is not the checksum verification of a downloaded file.

| Artifact | Observed upstream metadata / planning treatment |
| --- | --- |
| F5 v1 base `model_1250000.safetensors` | 1,348,435,761 bytes; separate vocabulary 13,800 bytes. CC-BY-NC-4.0 official weights; revision/rights must be approved |
| Vocos candidate `pytorch_model.bin` | 54,365,991 bytes plus config; audit its own license and dependency artifacts |
| whisper.cpp tiny/base examples | Upstream table lists 75 MiB / 142 MiB disk and approximately 273 MB / 388 MB memory; not Martlet memory/latency results |
| LLM/VLM/embeddings/reranker | No selection yet. Model choice, precision, context, tokenizer, auxiliary downloads, and image layers determine size; H02 must resolve before an enabled preset |
| Runtime images | Python/PyTorch/CUDA can dominate downloads; measure exact compressed/expanded bytes from selected digests, do not present weight size as total installation size |

Sources: [S14](RESEARCH.md#s14), [S17-S18](RESEARCH.md#s17). No model was downloaded
to obtain these metadata. Provisionally reserve at least twice the selected
new artifact footprint for staging/rollback plus data headroom; replace that
estimate with a precise per-filesystem plan before bootstrap writes anything.

Separate persistent storage classes: immutable verified model cache,
configuration/manifests, host credentials, user voice references, optional
memory/database, and bounded logs. Volume ownership/permissions must work for
non-root containers. Cache shared artifacts only when revision/license matches.
Keep rollback-critical artifacts until a validated upgrade completes.

Do not let F5's library or another model loader silently download a reference
ASR model or vocoder on first request. Provision every dependency explicitly,
provide F5 reference transcription up front, and qualify offline mode with
network egress denied after downloads. Unknown optional downloads block readiness.

## 3. Start, stop, update, repair, and removal

**Implemented internal V07a:** Desktop offers explicit LOCAL configuration
snapshots and exact previewed restore into an existing valid same-profile
v1/v2/v3 store. Version 3 includes inert companion personas/styles; older
sources preserve current personas. Recovery does not restore credentials,
permissions, runtime persona use or readiness.
It refuses missing/corrupt/future/foreign destinations and preserves exact
pre-replacement originals. This is neither the diagnostic bundle nor the broader
database/voice/host/binary lifecycle below. See the
[recovery walkthrough](TROUBLESHOOTING.md#local-configuration-backup--restore-v07a).
V07b now has merged [private staging/selection libraries](../src/Martlet.Updates/README.md),
explicit rollback configuration restore under the shared owner, and fresh-consent
reconciliation of a verified recorded restore without restoring again. These
are internal libraries, not a runnable activation or shipped rollback workflow.
Desktop wiring, executable/installer activation, N-1/N lifecycle and uninstall
qualification remain unimplemented or NOT RUN; V07a never launches an older
executable.

| Lifecycle operation | Required behavior |
| --- | --- |
| Windows start/login | Manual start by default; opt-in login startup, separate explicit listening policy; interrupted onboarding resumes without recording |
| Ubuntu boot | Reviewed system service reconciles selected Compose profile after engine/network prerequisites; model warmup/status visible, driver/network readiness retried within bounds |
| Runtime supervision | Container restart policy covers process exit, not proof of functional health. An unhealthy-running process requires a bounded, explicit recovery policy; avoid infinite restart/OOM loops |
| Readiness | `stopped`, `starting`, `downloading`, `loading`, `warming`, `ready`, `busy`, `degraded`, `failed`; include last change, progress, probe age and reason |
| Graceful stop | Stop accepting turns, cancel/flush playback/jobs, drain bounded worker work, flush databases; configured timeout before targeted termination; no global process-name kills |
| Upgrade | Verify signature/digest and schema/protocol compatibility; preview changes/disk and obtain download/model consent; snapshot configuration/database; stage new version; health-gate activation |
| Rollback | Retain previous executable/image/model manifest and matching data snapshot; restore only a compatible schema; never open new-schema data with an older binary blindly |
| Backup/restore | Consistent database snapshot, config/profile/voice metadata, optional voice assets with consent; omit secrets by default and re-pair; test restore on a separate profile/host |
| Repair | Read-only doctor first; restart only selected adapter/service, re-verify/re-download corrupt artifacts with consent, or reset a selected setting; preserve user data and explain scope |
| Reconfigure | Preview role/route/device changes; invalidate affected readiness/consent; preserve unrelated roles and profiles |
| Uninstall | Stop app/selected Martlet services and remove owned binaries/startup entries; preserve models, config, voices, memory and logs by default; list retained locations |
| Full purge | Separate explicit action selecting exact owned data paths, preview and confirmation; no wildcard deletion of shared caches, Docker, drivers, other projects, or user home |

Rollback and update are not free properties of Inno Setup or Compose. V07/H05
must implement application-aware staging, version checks, and migration tests.
Proposed compatibility policy: same protocol major supports current and previous
minor clients; no schema downgrade without a matching backup or explicit
migration. Use copy-on-write or a tested backup-before-migrate path; record
minimum reader/writer versions. Re-pair after restoring an obsolete credential
database, rather than accidentally reauthorizing revoked clients.

Uninstall offers separately consented credential removal/pair revocation and
explains retained credentials/data. A disconnected host may need local
revocation later; report that limitation. Removing Martlet never uninstalls
shared Docker/driver packages by default. Retained data remains subject to the
user's local backup/privacy policy.

Updates: manual check/download/install in MVP, stable and beta channels isolated
by explicit selection. No surprise model updates or unreviewed `latest` image
tags. An automated updater is not required for MVP; signed manifest validation,
rollback, and clear patch instructions are. Self-contained .NET runtime
security patches require new Martlet packages.

### Optional advanced host administration

**GPU power policy:** independent H06 extension, off by default and never part
of a privileged inference container. NVIDIA documents watt-based limits within
device-reported bounds, administrative privileges, and hardware-specific support
([S25](RESEARCH.md#s25)). If the owner wants "80% on boot," first identify the
GPU by UUID, inspect default/min/max values, explain the resulting proposed
watt limit, and obtain explicit per-device approval. Unsupported controls remain
unsupported; do not silently clamp or change a different card.

A separately reviewed host startup unit must apply and read back the selected
setting, expose failure, and restore the original policy on explicit removal.
The owner chooses whether failure of this optional policy prevents inference
startup; if they rely on it, default to holding inference until acknowledged.
A percentage setting does not prove circuit, PSU, thermal, or electrical
safety. This plan gives no wiring advice and authorizes no GPU change.

**Sunshine/Moonlight:** optional remote-desktop support path, not an AI transport
or install prerequisite. Verify the exact Sunshine package, Ubuntu display
session/headless capture behavior, encoding/GPU/driver compatibility,
permissions, pairing, firewall, and boot/login behavior. Upstream recommends
release binaries and cautions that its Docker images are not recommended for
most users ([S29](RESEARCH.md#s29)). Do not deploy it in a privileged all-in-one
Martlet container. Remote desktop and inference can contend for resources;
qualify together only if the owner opts in. No public exposure by default.

## 4. Diagnostics as a product surface

### One status page, one probe implementation

Provide a keyboard/screen-reader-accessible status page even before onboarding
completes. A pipeline strip shows **mic -> VAD -> STT -> policy -> LLM -> TTS ->
playback**, plus each host/worker/model. Each node shows state, last result and
age, duration, selected route/model/device, code, and an actionable next step.
Silence can be `NOT_ADDRESSED` or `COOLDOWN`, not just "offline."

Distinguish pass, fail, warning, unknown, running, skipped, fixture, and not
configured. Stale or skipped probes cannot create an all-green summary.
Expose last successful **real** full-path result separately from last fixture
run. "Host reachable" does not mean "model ready"; "audio decoded" does not
prove the user heard sound. Include "I heard it" and output-device guidance.

Proposed CLI counterpart: bundled `Martlet.Doctor.exe --profile demo --json`
and host `martlet-host doctor`. They share probe IDs, error codes, deadlines,
redaction, and remedies with the UI. Exit codes: 0 all requested required
probes passed; 1 failure; 2 incomplete/unknown/degraded; 3 invalid configuration
or invocation. JSON has a schema version and per-probe fixture/live provenance.
Exit success must never imply unrequested GPU/cloud paths were tested.

Offer local tests first: settings validation, credential-store access (not
secret display), device enumeration/meter, consented speaker tone, fixture
turn, DNS/TLS/host capability probes. Explicitly consented live self-test then
uses a short synthetic or user-recorded phrase with stage timings. Show
possible inference charges before running it; no recurring billable "health"
polls. A headless CLI requires an explicit consent flag for live requests.

### Logs, support bundle, and retention

Log structured metadata: UTC time, monotonic duration, severity, stable code,
component/version, role/adapter/model ID, session/turn/trace IDs, queue size,
state transition, and policy reason. Redact provider errors before logging;
provider error bodies can echo user input or authorization material.

Proposed default: local rotating diagnostic logs, at most seven days and
50 MiB total, whichever limit comes first. Raw audio, screenshots, transcripts,
prompts, responses, authorization headers, keys, tokens, voice references,
personal memory, and crash dumps are omitted. No automatic telemetry upload.
Retention is configurable and visible; log timestamps do not imply transcript
retention. Temporary audio is in-memory by default and cleared after use.

Support bundle includes version/pin manifest, sanitized settings, probe
results, selected metadata logs, and compatibility summary. Preview every
file, redaction summary, time range, and destination before local export.
Pseudonymize usernames, home paths, IPs/hostnames, GPU serial/UUID, and device
identifiers while retaining correlations. Never include secret stores, full
environment variables, process memory dumps, models, or source documents.
Use an allowlist, then canary secret/content leak tests, not regex masking
as the only safeguard.

Opt-in debug capture is a separate timed feature with a clear recording
indicator, selectable categories/destination, automatic expiry (proposed
15 minutes), and explicit consent for other speakers/screens. Store locally;
do not automatically include it in a bundle. Explain irreversibility of any
data the user chooses to export. Support channels must allow private sharing;
do not ask novices to post unreviewed bundles in public issues.

## 5. Troubleshooting matrix

All remedies are proposed UI actions or version-specific guided instructions,
not commands executed by this plan. Escalation carries sanitized metadata and
trace IDs, not private audio/transcripts/secrets.

| Symptom / stable code | Likely causes | Automated probe | Exact next step for user | Escalation evidence |
| --- | --- | --- | --- | --- |
| Installer will not open: `INSTALL_SIGNATURE` | Damaged download, unsigned build, blocked publisher/policy | Signature/hash and file version | Re-download official signed artifact; if enterprise policy blocks it, contact administrator; do not disable protection | Installer hash, signer, OS/policy error |
| Install cannot complete: `INSTALL_PREREQ` | Wrong architecture, unsupported OS, low disk, unwritable directory | OS/RID/disk/permission probe | Choose matching supported installer or free indicated space; retain existing version | Install step/code and disk summary |
| Host setup stops: `HOST_PREREQ` | Missing engine/plugin/toolkit, conflicting packages, pending reboot | Version and desired-state diff | Review specific official prerequisite remedy, approve only needed change, reboot if required, rerun preflight | Sanitized host matrix and failed step |
| GPU absent: `GPU_UNAVAILABLE` | Driver/module/Secure Boot issue, wrong GPU assignment, toolkit not configured | Host GPU -> container GPU -> framework chain | Follow detected driver/toolkit remedy; reselect actual UUID; verify after reboot; do not disable Secure Boot as a generic fix | Which boundary failed, driver/kernel/image IDs |
| Model crashes: `GPU_OOM` | LLM context/KV cache plus F5 exceeds VRAM, other workloads, too much concurrency | Per-worker allocation/VRAM/queue and selected manifest | Stop only selected optional jobs; choose documented smaller model/context or serialize; confirm quality tradeoff | Peak VRAM, context, concurrent roles; no full process dumps |
| Download stalls/fails: `MODEL_DOWNLOAD` | Offline/proxy, disk/inodes, gated access, interrupted range | Network/source revision, bytes/disk/checksum | Resume verified partial download; fix selected source auth/space; restart only corrupt artifact with consent | Artifact ID/hash, bytes/progress/error |
| Model present but unavailable: `MODEL_NOT_READY` | Wrong format/revision, missing vocab/vocoder/license, still loading | Artifact and readiness/warmup checks | Wait for stated warmup deadline or repair the named dependency; accept required terms only if appropriate | Missing artifact, load stage, model digest |
| No mic activity: `AUDIO_INPUT` | OS privacy denied, muted/fixed missing device, wrong input | Permission/open/real level meter | Open Windows microphone privacy settings, enable desktop-app access if desired, select/test intended mic | Device type/state/format, no recording |
| No sound / output disappears: `AUDIO_DEVICE_LOST` | Wrong output, Windows mixer mute, USB/Bluetooth/default change | Tone/playback/open/device event trace | Select output, check Windows app volume, reconnect and Test; manually replay only if desired | Output format and event sequence |
| Host unreachable: `NET_UNREACHABLE` | Wrong address, host asleep, service stopped, VLAN isolation | DNS -> TCP selected port -> TLS -> gateway | Verify host-local readiness, correct host address/port, reconnect same intended LAN | Stage/timeouts and pseudonymized route |
| IP works, name fails: `NET_DNS` | DNS/mDNS unavailable, stale lease, suffix mismatch | Resolve name vs saved host-ID address | Use verified current address then correct DHCP/DNS; do not re-trust a changed certificate | Resolver result and host identity comparison |
| Connection refused/times out: `NET_PORT` | Port collision, wrong binding, firewall/router policy | Host listen/bind vs client TCP and unauthorized-client denial | Review exact selected-interface and client-source rule; no blanket firewall disable or public forwarding | Binding/rule summary, both-side probe result |
| TLS/pairing fails: `PAIR_TRUST` | Wrong host, expired cert, clock skew, expired token, identity change | Time/identity/pin/token lifecycle probe | Check host clock, compare pairing fingerprint locally, generate new one-use token if needed | Error category; never token/private key |
| API key rejected: `AUTH_EXPIRED` | Revoked/expired key, wrong origin/project/scope | Non-billable auth metadata where supported; sanitized real failure otherwise | Re-enter authorized credentials for displayed origin; verify account/project access | Provider status/request ID; no key |
| Limit reached: `QUOTA_EXCEEDED` | Provider billing/rate quota or local session ceiling | Typed provider error, Retry-After, usage/budget | Wait stated interval or review provider account/local limit; user alone approves changed spending | Quota class and timestamps |
| `/v1` endpoint fails: `PROVIDER_CAPABILITY` | Partial OpenAI compatibility, wrong adapter, missing model/voice or schema | Named role contract probes, not just `/models` | Select correct adapter and supported model; configure missing STT/TTS independently | Adapter/model/version and failed contract case |
| Client/host disagree: `PROVIDER_VERSION` | Protocol major mismatch or removed capability | Capability/schema comparison | Use compatible signed client/host pair or documented rollback | Both manifests and minimum versions |
| Mic works but no reply: `TURN_SUPPRESSED` | Not addressed, cooldown, busy, no speech/low confidence, policy disabled | Stage timeline and policy reason | Use PTT or configured name, wait for gap, inspect policy; do not increase cloud listening silently | Policy reason and configuration, no transcript by default |
| Reply very slow: `TURN_SLOW` | Cold model, high RTT, rate limits, oversized context, GPU contention | Stage timings/queue/TTFA and warmup | Wait for readiness, use shorter response/context, pause optional perception or select qualified smaller model | Warm/cold status, p50/p95 sample context |
| Audio cuts/repeats: `STREAM_TRUNCATED` | Missing terminal event, disconnect, out-of-order/duplicate frames, underrun | Sequence/sample-count/epoch and transport trace | Stop, run stream self-test, retry once deliberately; do not auto-repeat partially heard speech | Last sequence/epoch, stage and buffer metrics |
| Voice hears itself: `AUDIO_FEEDBACK` | Room speakers, loopback inclusion, ineffective AEC | Playback/capture overlap and loopback configuration | Use headset or half-duplex/PTT; disable selected loopback; do not claim transcript filtering is AEC | Audio topology and policy, no raw capture |
| Voice works but vision/memory fails: `CONTEXT_UNAVAILABLE` | Host 2 down, role disabled, stale frame, unqualified model | Independent host-2 readiness/freshness probe | Keep voice-only mode; reconnect/repair only host 2 or re-enable consented role | Context age, selected role, gateway status |

Every new stable code requires a novice-readable summary, an owned remedy,
a fixture that reproduces it, and a test that the status page cannot display
it as healthy. "Try reinstalling everything" is not a default remedy.
