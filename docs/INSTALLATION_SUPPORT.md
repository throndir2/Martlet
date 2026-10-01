# Installation and support design

The [five-engine Voice Studio plan](VOICE_STUDIO.md) expands optional
self-hosted speech beyond F5. Its guided experience isolates each engine's
dependencies, distinguishes installation/loading/voice preparation, and allows
reference or training-material import followed by A/B previews before Apply.
VS01's local Voice Library is implemented preparation only; host installation,
engine execution, training and previews remain later slices, not enabled
services or qualified setup.

The implemented [V06b Desktop troubleshooting path](TROUBLESHOOTING.md) now
provides passive shared status/remedies, explicit local metadata recording and
frozen preview/default-No consent/local ZIP export. There is no configured
support contact/upload channel and no CLI export. The broader specification
below remains a target, not a claim that those services are available.

**Future product specification, not an end-user installation guide.**
The [foundation](FOUNDATION.md) implements a narrow local status CLI and WPF
shell. The current [internal packaging recipe](../packaging/windows/README.md)
now provides a self-contained payload/unsigned installer build and real Desktop/
Doctor status commands. The broader host utilities, live setup and qualified
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

The internal [H05c-A configuration publisher](../src/Martlet.Host.Setup/README.md#selected-role-configuration-publication-h05c-a)
now binds selected host/owner roles to verified OCI-byte observations and writes
reviewed owned configuration revisions. Its partial service fragments and
missing-recipe findings are **not runnable Compose, installation completion or
runtime permission**. The [next service/recipe and supervision work](../deploy/ubuntu/compose/README.md)
must supply a real Linux gateway/Ollama path independently of optional F5/STT.
Permanent device identity remains separate and is never reset by publication.

Target Windows 11 25H2 x64 Home/Pro on a currently serviced patch. Test a clean
standard-user account with no Git, Python, Node, Docker, developer SDK, or
preinstalled .NET required by Martlet. The app bundles its selected .NET runtime
and necessary redistributable native dependencies. No desktop inference GPU is
required for P1. Windows 24H2 is a compatibility candidate only while its edition
is serviced; it is close to Home/Pro end-of-servicing at the research date
([S03](RESEARCH.md#s03)). ARM64, Windows 10, Linux/macOS desktop, and enterprise
deployment tooling are not initial support claims.

**Packaging:** unsigned per-user Inno Setup EXE (code signing is not required
for this personal project), normal install/uninstall
registration, Start menu shortcut, local help, and bundled diagnostic CLI.
Install to the user's application directory without an elevation prompt in the
golden path. Keep code and mutable data separate. Default install must not add
firewall exceptions, install services, modify drivers, start at login, fetch
large models, or require a browser login. Setup asks no questions and installs
no [prerequisites](PREREQUISITES.md); it offers to start Martlet, whose welcome
tour offers what is missing (a missing WebView2 runtime and a blocked microphone
setting ticked; Windows speech, Ollama, WSL 2 + Docker Desktop unticked unless
the PC is an NVIDIA host or the setup advisor's plan needs them).

Publish GitHub asset SHA-256, provenance and notices for the exact versioned
installer. A GitHub-supplied checksum is not an independent publisher signature.
SmartScreen/policy issues are support cases: do not tell users to disable
antivirus or bypass Windows protection. Internal-unsigned artifacts must not
be mislabeled as public releases; they have distinct identities.

A self-contained ZIP is a developer/support diagnostic artifact only in M1-M2,
not a promised fully portable mode. If supported later, specify what remains in
Credential Manager/AppData, absence of automatic updates/uninstall registration,
and version coexistence. Do not silently store credentials next to a portable EXE.

### First-run storyboard and resumable state

| Step | User experience | Persisted checkpoint / probe |
| --- | --- | --- |
| 1. Download/install | Release page states supported OS, that the installer is not code-signed, size, no-GPU API option, and planned costs | Verify official GitHub origin, exact version and asset SHA-256, OS/architecture, free disk, standard-user writeability; preserve Windows warnings and display install failure code/log location |
| 2. Welcome | Choose **Try fixture demo**, **Use an API provider**, or **Connect existing endpoints**; "No avatar" is normal | Profile and onboarding schema version; no credential or network call needed for demo |
| 3. Explain data | Show mic -> STT destination, text -> LLM, response -> TTS; screen off; memory on by default (facts saved on this PC, one extra LLM request per exchange to pick them out); generated voice disclosure | Explicit consent per role/destination; not one blanket analytics checkbox |
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

### Feature-first multi-machine setup

**Design update, 2026-09-22; not implemented.** The storyboard above is the
voice preset, not a requirement to configure every role. One Windows coordinator
owns the user's desired experience and overall progress. A local host installer
owns approved changes on each managed machine. Users should not need to
understand Compose, Python environments or port numbers to choose guided setup.

| User selection | Required capabilities | What setup must skip |
| --- | --- | --- |
| Fixture demo | Authored local fixture only | Providers, host provisioning, keys, models and audio unless separately requested |
| Typed text conversation | Text LLM | Microphone, STT, TTS and output-device prerequisites |
| Microphone input | Selected capture path and STT, plus LLM for conversation | TTS/output if replies remain text; continuous listening is a separate opt-in |
| Spoken replies | TTS and selected output, plus conversation LLM | Microphone/STT when input is typed |
| Screen context, memory or avatar | Only dependencies of the explicitly enabled feature | Every disabled feature's services, keys, downloads and readiness checks |

Presets populate this dependency plan but never lock roles to numbered machines.
Keep a stable machine/host identity separate from its label/address, role routes,
runtime instances and ownership. A Windows PC can both run the client and host
an LLM; one Ubuntu machine can host LLM/TTS/STT; another can provide context.
An external API can supply any supported role alongside either layout.
Do not count a WSL VM's resources as additional physical RAM or GPU capacity.

The proposed guided flow is:

1. **Choose features.** Start with typed text, microphone input and spoken
   replies as understandable choices. Screen, memory and avatar remain off.
   Explain which roles each choice adds; no provider calls or downloads yet.
2. **Choose where each role runs.** Offer external API, this computer, another
   computer, or compatible existing service. Recommend only qualified presets;
   label unqualified lanes as unavailable/experimental with an exact reason.
   Show data destinations and usage costs together, including mixed routes.
3. **Review a single plan.** Group actions by named machine, with role badges,
   managed/external ownership, selected runtime/model, download and expanded
   sizes, storage, shared resource budget, licenses, privileges and reboot needs.
   Reuse compatible owned dependencies once; do not take over existing services
   or silently uninstall conflicting packages. Resolve port/engine collisions.
4. **Prepare selected hosts.** Run local setup directly where supported. For a
   remote host, show "Run Host Setup on Kitchen Ubuntu PC" with the correct OS
   bundle, a non-secret role plan and clear return/pairing instructions. No API
   keys, admin passwords or reusable tokens belong in that plan. Initial trust
   is established out of band; discovery is opt-in, never a subnet-wide scan.
5. **Approve local changes and pair.** The host shows its own plan and obtains
   scoped local approvals. Resume after reboot without replaying approval or
   starting capture. Present the host identity/fingerprint and short-lived
   pairing flow from Architecture. The coordinator receives scoped readiness,
   not a shell, Docker socket, administrator token or arbitrary install API.
   Established device pairing is non-expiring and must survive routine
   restart, reboot, updates and recoverable interruption. Do not present an
   offline/clock/storage fault as unpaired or recommend reset-all-devices.
   One-use invitation expiry and request freshness/replay remain independent.
   Renew TLS certificates automatically with the same key/pin before serving;
   no routine certificate expiry should require pairing again.
6. **Check connection, then try a request.** Use the staged checks below for only
   the selected roles. A failed optional role must not erase successful checks.
   Offer an explicit feature change to continue in text-only mode if speech is
   unavailable; never silently send data elsewhere.
7. **Finish for this experience.** Show "Ready for text chat" or the exact tested
   voice path, with disabled/deferred features distinct from failures. Include
   local/remote startup expectations and a per-machine resume/remedy link.
   Adding another role later reuses this plan without restarting onboarding.

Most coordination occurs on one computer, but an untouched remote machine still
needs its local setup/trust/privilege step. Headless Ubuntu uses the same
packaged host engine through a guided CLI; an administrator may access that CLI
through an independently secured existing session. Martlet does not install
SSH/remote desktop or retain remote admin credentials. The initial host GUI is
a loopback-only browser UI launched locally by the host bundle with local-session
authorization, origin/CSRF protection and narrowly scoped privileged actions.
It is not exposed on the LAN; the paired inference/status gateway is separate.
Packaging and novice accessibility of this UI remain H09 work.

**Implemented uniform host flow (2026-09-28).** [`martlet-host`](../deploy/host/README.md)
is the single CLI for every managed host role: `setup` (gateway, identity, boot
service), `pair`, `roles`, `add <role>`, `remove <role>`, `status`. Each role is
data only (`role.conf` + `compose.yaml`) and goes through the same steps:
requirements, terms, secrets, choices, registry login, pinned assets,
loopback-only Compose service, readiness, and a gateway route listed in
`host.json` `roles`. Audio2Face is the first role; other roles need their gateway
relay worker before they are listed. The same engine runs by several methods:
natively on Ubuntu, or as the `martlet-host` container image on any Docker host
(including Windows with Docker Desktop and remote Docker), launched on the host
itself or from the desktop's **Martlet hosts** window (this PC via Docker Desktop,
another computer over SSH with Docker or native Ubuntu). Over SSH the desktop
drives everything itself (Martlet's own key after one password, pinned host key,
unattended `martlet-host --yes` with the owner's click as the confirmation) and
pairs automatically; elsewhere pairing uses one pasted `martlet-pair-v1` code. The Docker method was run end to end on Windows Docker
Desktop with a stand-in role; it is not yet the graphical/journaled coordinator
described above and has not been run on a real GPU with the Audio2Face NIM.

### Deployment choices and ownership

| Choice | Recommended use / responsibilities |
| --- | --- |
| Guided Ubuntu Desktop or Server host | First managed Linux target: Docker Engine + Compose on the host, selected pinned workers, persistent volumes and reviewed boot supervision. Desktop users get a graphical host setup; headless users use the same plan/journal via CLI. No Docker Desktop requirement. |
| Advanced Docker/Compose | User installs/manages the qualified engine and explicit Compose project. Supply versioned role configuration and checks; no automatic adoption, upgrade, restart or removal by Martlet. Still require trusted transport, models, consent and capability checks. |
| Native Windows hosting | Qualify native Ollama first; upstream provides a normal installer and standalone CLI. Reuse an existing install in connect-only mode. A Martlet-managed native lifecycle/package is separate H10 work, not permission to invoke an arbitrary installer or add a service wrapper. |
| Windows Docker hosting | Separate advanced Docker Desktop + WSL2 Linux-container lane. Explain virtualization, disk/memory, driver, possible reboot, Desktop licensing and login/startup behavior. Do not claim Windows containers or Windows Server support. |
| Existing native/container service | Connect only through a named tested adapter and approved transport. Validate capabilities without assuming ownership of its models, configuration, volumes or upgrades. Remote raw inference ports are not a substitute for TLS/authentication. |
| External API | No host runtime to install; configure only selected roles, credentials and data/cost consent. Never require Docker for the API or fixture path. |

Docker is a useful managed Linux packaging default, not "all Linux is supported."
Docker lists distro/version/architecture-specific packages and does not test
every derivative. Native Engine and Docker Desktop are different deployments:
Desktop on Linux adds a VM, separate storage and a separate context. Qualify
the host kernel, architecture, driver, engine/toolkit and selected image/model
together. The initial target remains Ubuntu 24.04 x86_64; Desktop and Server
need their own lifecycle evidence. Other distros, AMD/Intel, rootless GPU and
WSL are separate matrix entries, not inferred passes ([S30-S32](RESEARCH.md#s30)).

For every role, record who owns start/stop/update/removal. Switching from
external to managed requires a new explicit adoption/migration plan; changing
the route alone never authorizes takeover. Removing one feature stops only its
owned work and preserves data; shared dependencies stay while other roles need
them. Uninstall never removes a shared engine, driver or an external project's
volumes. Pin the intended engine/context rather than changing the user's global
Docker context or assuming the CLI's current context targets this computer.

### Quick connection checks and a small real trial

**Proposed observable targets, not measured performance:** acknowledge Check
within one second; cap the cheap connection group at 15 seconds wall time,
including queueing, with at most four independent probes and five seconds per
network stage. Cache one host handshake per selected host for that check, then
check role capabilities. Use monotonic deadlines, no automatic retry loop and
no arbitrary network discovery. Timeout yields an exact stage/remedy, not a
green result; queued checks that never started remain "not checked." Users can
retry a selected host without repeating healthy hosts. Model downloads/warmup
are not squeezed into the connection deadline.

| Stage | What it establishes | Authorization and result limits |
| --- | --- | --- |
| Local plan check | Required routes present, supported adapter/OS selections, structural settings and storage/port inventory where accessible | No network, credential retrieval, devices, driver changes, pulls or inference on opening setup. Denied/unavailable observations stay unknown. |
| Check connection | Selected endpoint reachable, TLS identity and scoped authentication valid, protocol/role capability compatible | Explicit destination-bound action; bounded metadata requests only through the named adapter. No secret on redirects, no guessed health URL; absent metadata support is unknown, not failure or a request to infer. |
| Check runtime/model | Managed runtime responds; exact selected model/dependencies provisioned; GPU visible if needed | Container/GPU execution and model loading require their own approval. A version string, model listing or `nvidia-smi` is not proof of inference, immutable weights, fit or data locality. |
| Try a small request | Real selected STT/LLM/TTS operation returns a valid bounded result | Separate explicit data/cost/compute authorization; show exact destination and sample. Use an owned/licensed short speech fixture for STT, short text for LLM/TTS, approved voice reference for F5. No personal microphone data or playback by default. |
| Try my selected experience | Actual selected end-to-end path, and audio capture/playback only if requested | Fresh action approval and separate human audibility confirmation. Typed-only needs no audio checks. This does not substitute for release/hardware qualification. |

Small live probes should default to a visible 60-second action deadline with
strict adapter-specific input/output bounds. Preflight may disclose a different
qualified model-specific limit before approval; cold warmup needs separate
progress and explicit bounded waiting, not a hidden extended timeout.
Stop suppresses late output immediately; a timeout or closed HTTP connection
must not claim server/GPU work ended. Retain operation ownership until cleanup
completes and prohibit overlapping retries while cleanup is unresolved.

Show **configured**, **connected**, **model available**, **warming**, **request
passed**, **failed**, **unknown**, **stale** and **disabled** separately, with
timestamp, scope, duration and actionable remedy. Model/route/credential/runtime
changes invalidate affected evidence and approvals, not unrelated roles.
Ordinary status displays stored observations; it does not run billable probes
or start stopped workers. Compose health checks stay cheap/non-inference.
`service_healthy` helps startup ordering but cannot prove a model or whole
conversation works ([S35](RESEARCH.md#s35)). All validation is local or a
separately authorized selected endpoint action, never remote CI.

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
Desktop implements the companion editor and named OpenAI LLM catalog selection;
the remaining surfaces are not yet integrated.

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
| Host OS | Ubuntu 24.04 LTS x86_64 Desktop/Server, latest qualified security point release/kernel | Qualify both setup experiences and boot without graphical login; 22.04/26.04 are separate expansion lanes, although Docker and NVIDIA list them upstream |
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
out of the initial supported host matrix. Windows native and WSL2 containers
are planned H10 expansion lanes, not categorically excluded product goals;
native F5 is not promised. Only selected GPU roles require GPU prerequisites:
an API-only client or qualified CPU role must not be blocked for lacking CUDA.

### Linux gateway state backend boundary

The canonical gateway includes an opt-in `LinuxServicePermissions` storage
candidate for non-root Linux x86_64/glibc on local persistent ext4. Setup must
not treat library availability or a modeled check as host qualification.
Existing factory signatures remain Windows DPAPI; Linux selection is an
explicit trusted local-owner call, not a stored wizard permission.

Linux stores signing/private keys as service-permission-isolated **plaintext**:
0700 directory, 0600 files, strict UID/ACL/link/mount checks and durable redo.
This is not Windows DPAPI parity. Explain same-UID/root/offline-disk exposure
and operator-managed disk encryption where needed. The library creates only an
absent leaf under an existing safe private parent; it never provisions a UID,
repairs ownership/modes, migrates Windows ciphertext or resets devices.
Storage/clock failures mean access blocked, not unpaired.

Linux local approval/disclosure, GUI/headless lifecycle, real native execution,
reboot, service/container mounts and LAN qualification remain **NOT RUN**.
The Windows Host CLI has not become portable. Preserve H01/#21 and managed
preset holds; a future installer must use the same authority rather than fork
pairing records. See [backend contract](../src/Martlet.Gateway.Persistence/README.md#explicit-linux-service-permissions-candidate).

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
   `voice-host` (preset LLM/TTS, optional STT), `context-host` (optional
   perception/memory), or `single-host` with explicitly chosen roles. These
   are editable presets, not mandatory bundles: disabled roles or roles supplied
   by an external API add no host prerequisite. CPU fixture-only mode remains
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
Unknown expanded/installed/peak quantities must remain unknown, not inferred
from compressed descriptor bytes.

The focused [H05b acquisition library](../src/Martlet.Host.Setup/README.md)
now separates durable local review from fresh exact-artifact acquisition consent.
It supports pinned public GitHub release assets through a bounded reviewed CDN
policy and owned, hash-verified local staging. Its separate image API also
acquires the current named public Docker Hub Ollama and GHCR F5 candidates,
exact `linux/amd64`, using scoped anonymous tokens and documented CDN redirects.
The entire selected image batch is verified before atomic OCI-layout publication;
original Docker schema-2 bytes and repeated layer order are retained. Deduplication
is within that batch only, so separate batches may repeat downloads/storage.
Neither API installs, loads or executes containers. Hugging Face/model downloads
remain unsupported, so this is not a completed model-provisioning or F5
installation journey. Catalog inspection, host/runtime qualification and
permanent device-pairing trust remain unchanged.

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
| Upgrade | Verify GitHub origin/digest (and signature only for separately signed candidates) and schema/protocol compatibility; preview changes/disk and obtain download/install/model consent; snapshot configuration/database; stage new version; health-gate activation |
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

The Desktop supports ON-by-default automatic checks (at launch, then every
15 minutes to 24 hours while it runs; a found version is offered once with an
*Update now* prompt) or an explicit manual check against the
public GitHub releases list. A newer versioned win-x64 installer from a normal
(non-draft, non-prerelease) release is downloaded into the local `updates\`
folder only after an exact GitHub asset size/SHA-256 check. *Install* (or *Update now*, or the
opt-in automatic install, which waits until the character, conversations and
Martlet windows are closed, or until exit) closes Martlet, runs the installer
silently with its progress window and no optional prerequisite tasks, records
the exit code and restarts Martlet. That digest is not publisher trust.
Releases ship an unsigned installer with a narrow personal/noncommercial
binary-use grant; code signing is not a requirement for this personal project.
Rollback is reinstalling an older release; there is no automatic downgrade.
Paired hosts report their Martlet version, and `martlet-host update` rebuilds
a host's gateway from the desktop's version in place (identity, pairings,
roles and data kept); the desktop runs it in a console per host or, when
opted in, in the background over SSH keys or Docker Desktop.
Windows may display publisher warnings;
never suppress or bypass them. No surprise model updates or
unreviewed `latest` image tags. Beta remains an unimplemented opt-in channel,
not an automatic fallback. Self-contained .NET runtime security patches
require new Martlet packages.

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
| Installer will not open: `INSTALL_SIGNATURE` | Damaged download, unsigned publisher warning or blocked OS policy | Official Release origin, asset SHA-256, file version and OS warning | Re-download the exact official asset if its digest failed; if Windows or enterprise policy blocks an unsigned build, contact the administrator; do not disable protection | Installer hash, unsigned status, OS/policy error |
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
| TLS/pairing fails: `PAIR_TRUST` | Wrong host, certificate renewal failure, clock skew, expired invitation, identity change | Time/identity/pin/invitation lifecycle probe | Correct clock or same-key renewal/access problem while retaining pairing; replace only an expired invitation. A genuine changed identity requires deliberate verification, never automatic re-trust | Error category; never token/private key |
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
