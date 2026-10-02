# Research and provenance ledger

**Accessed 2026-09-12, America/Los_Angeles, unless stated otherwise.**
Sources below are primary upstream documentation, repositories, or model
metadata read without installing software, cloning additional repositories,
making inference requests, or downloading model weights. External queries
contained public technology names only, not private repository contents.

Labels: **Verified upstream** means documented or source-inspected behavior at
the recorded reference, not a Martlet test. **Proposed** means an engineering
recommendation. **Unresolved** requires further selection, rights review, or
execution on the target environment. Mutable documentation can change; H02/V03
must pin tested artifacts and recheck licensing/capabilities before distribution.

## Desktop and packaging

### S01

[Microsoft .NET support policy](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core)

**Verified upstream:** .NET 10 is an LTS release in active support in the
accessed table. **Proposed:** use its supported patch line for a Windows-first
client and optional Linux gateway. Do not freeze this plan to an untested patch
number; pin SDK/runtime at implementation and keep self-contained builds patched.

### S02

[Microsoft .NET publishing overview](https://learn.microsoft.com/en-us/dotnet/core/deploying/)

**Verified upstream:** self-contained publishing includes the platform-specific
runtime; framework-dependent publishing requires an installed runtime.
**Consequence:** a no-.NET-install Windows route is feasible, but native audio
libraries, installer behavior, and OS support still need packaging tests.

### S03

[Microsoft Windows 11 release information](https://learn.microsoft.com/en-us/windows/release-health/windows11-release-information)

**Verified upstream:** servicing differs by release and edition. At access,
25H2 is listed as serviced; 24H2 Home/Pro has an October 2026 end-of-updates
date. **Proposed:** 25H2 x64 Home/Pro first, rather than build a new support
promise around a nearly expired release. Recheck at every Martlet release.

### S04

[NAudio repository and documentation](https://github.com/naudio/NAudio)

**Verified upstream:** .NET audio library with WASAPI capture/playback,
resampling, codecs, and device APIs; current main documents a major package/API
transition. **Proposed:** isolate it behind an audio adapter. Select a tested
release instead of copying examples from another major version. This source
does not prove Martlet device recovery or acoustic echo cancellation.

### S05

[Microsoft Core Audio device events](https://learn.microsoft.com/en-us/windows/win32/coreaudio/device-events)

**Verified upstream:** clients can subscribe to endpoint additions/removals,
state, property, and default-role changes. **Consequence:** a robust fixed/default
device policy is implementable; application-specific privacy and replay behavior
must be designed and tested, not delegated to device enumeration.

### S06

[Microsoft Credential Manager: CredWriteW](https://learn.microsoft.com/en-us/windows/win32/api/wincred/nf-wincred-credwritew)

**Verified upstream:** Windows supports storing credentials in the user's
credential set. **Proposed:** key-store references in settings, secrets in
Credential Manager; confirm storage/retrieval/deletion and account/session
behavior. This is not protection against a compromised logged-in user.

### S07

[Inno Setup features](https://jrsoftware.org/isinfo.php),
[Inno Setup license](https://jrsoftware.org/files/is/license.txt),
[WiX release maintenance terms](https://docs.firegiant.com/wix/osmf/)

**Verified upstream:** Inno supports non-administrative installs, signed
install/uninstall, and a single setup EXE. The accessed license permits use
including commercial applications subject to its conditions; its site requests
commercial users purchase a license. WiX's current release documentation
describes an Open Source Maintenance Fee and EULA acceptance. **Proposed:**
Inno first, MSIX/MSI as evaluated alternatives. Review the exact chosen
builder's distribution/notice terms; no purchase or perpetual-free guarantee
is implied. Signing and application-aware rollback remain separate work.

## API and lightweight speech routes

### S08

[OpenAI speech-to-text guide](https://developers.openai.com/api/docs/guides/speech-to-text)

**Verified upstream:** completed-file transcription is distinct from realtime
incoming-audio transcription; supported formats, model-specific capabilities,
and a 25 MB file limit are documented at access. **Proposed:** bounded utterance
uploads first. Whisper was a product candidate, not a requirement that every
API transcription model be Whisper. Pick model IDs/response fields by tested
adapter capability and current account access.

### S09

[OpenAI streaming responses guide](https://developers.openai.com/api/docs/guides/streaming-responses)

**Verified upstream:** Responses API HTTP streaming uses typed server-sent
events; other transport modes and semantics exist. **Consequence:** normalize
text deltas, refusal, terminal and error events; do not apply another endpoint's
SSE schema or assume disconnect guarantees server compute cancellation.

### S10

[OpenAI text-to-speech guide](https://developers.openai.com/api/docs/guides/text-to-speech)

**Verified upstream:** speech output supports transport streaming and multiple
formats, with voice choices dependent on model. The guide requires disclosure
that the voice is AI-generated. **Consequence:** test the selected model/voice/
format, show the disclosure, and do not hard-code a universal voice count.
The accessed page itself had differing voice counts in different sections;
use model-specific catalog/probes rather than repeat that count as a guarantee.

### S09a: pinned GPT-4.1 text models

[OpenAI GPT-4.1 model reference](https://developers.openai.com/api/docs/models/gpt-4.1)

**Verified upstream 2026-09-21:** the `gpt-4.1-2025-04-14` snapshot supports
text output, the Responses endpoint and streaming. Martlet's existing
`gpt-4.1-mini-2025-04-14` and this full snapshot remain exact local adapter
allowlist entries, not aliases, discovery results, account-access evidence,
price guarantees or live qualification.

### S11

[OpenAI API pricing](https://developers.openai.com/api/docs/pricing)

**Verified upstream:** pricing is model/modality/tier dependent and mutable.
**Proposed:** dated catalog, account verification and configurable budgets;
no fixed price, free quota, or permanently free service promise in Martlet.
The older marketing pricing URL redirected to general product pricing at access;
use the API documentation for implementation-era estimates. No account was
provisioned or billable call made for this plan.

### S12

[OpenAI API data controls](https://developers.openai.com/api/docs/guides/your-data)

**Verified upstream:** API data is not used for training by default unless
opted in, but abuse-monitoring/application-state retention depends on endpoint
and configuration; special retention controls require eligibility/approval.
**Consequence:** do not equate no-training with no-retention. Use stateless
requests and explicit storage settings where available, disclose the exact
selected endpoint policy, and avoid cloud uploads without consent.

### S13

[Ollama OpenAI compatibility](https://docs.ollama.com/api/openai-compatibility)

**Verified upstream:** Ollama describes compatibility with *parts* of the
OpenAI API. Its local examples use an ignored placeholder API key.
**Consequence:** a placeholder key is not LAN authentication; keep raw services
private and expose a secured gateway. Verify chat/Responses/tool/image/stream/
cancel capabilities per version and model. Ollama is a proposed first backend,
not selected weights or a universal OpenAI server.

#### S13a: pinned native chat contract (H02b)

Read-only source inspection on **2026-09-15**, pinned to Ollama v0.34.0
`d8ab4b4f0ca24b51d3a46b3bf4f462e58ce66b1f`; full immutable source links and the
supported subset are in [the adapter guide](../src/Martlet.Providers/OLLAMA_CHAT.md#source-contract-and-limits-of-the-local-selector).
The native `/api/chat` wire shape separates assistant content, thinking and
tools. `:local` is source-traced through name parsing/local manifests to a
remote-backed-alias rejection before proxy dispatch. A loopback destination
alone would not prevent the source's ordinary cloud/remote alias paths.

**Evidence limit:** this selector is not server attestation. Names/tags remain
mutable, response.model echoes the request, and internal manifest digests are
not an atomic caller-bound invocation digest. Model-owned system/messages and
optional upstream request logs prevent whole-prompt/no-retention claims.
The H02b implementation exercises only controlled HTTP/stream fixtures; no
runtime/model was executed and no production authorization issuer or route is
supplied. HostArtifacts metadata remains ineligible and grants no invocation.

### S14

[whisper.cpp](https://github.com/ggml-org/whisper.cpp),
[OpenAI Whisper README/license](https://github.com/openai/whisper#license),
[Silero VAD](https://github.com/snakers4/silero-vad),
[Silero VAD license](https://github.com/snakers4/silero-vad/blob/master/LICENSE)

**Verified upstream:** whisper.cpp provides a lightweight native Whisper
implementation and quantized model support; its table lists tiny/base disk
sizes of 75/142 MiB and approximate memory of 273/388 MB. OpenAI documents MIT
code and weight licensing for Whisper. Silero provides ONNX inference and
documents that non-Python consumers must implement I/O/wrapping; its inspected
license is MIT. **Proposed:** packaged CPU VAD first, optional selected CPU
Whisper later. These upstream size/performance statements are not measured
Martlet CPU/game-load results; audit converted model provenance and exact
runtime/model licensing before bundling.

## F5-TTS: packaging, streaming, models, and rights

### S15

[Official F5-TTS](https://github.com/SWivid/F5-TTS),
[inspected package metadata](https://github.com/SWivid/F5-TTS/blob/9c614e9657089213efc6a7421b30630be138a3f5/pyproject.toml)

Source revision resolved at access:
`9c614e9657089213efc6a7421b30630be138a3f5`.

**Verified upstream:** package name `f5-tts`; metadata at the inspected source
declares `1.1.22`; CLI/Gradio entry points and many broad dependency ranges.
README describes Python 3.10+, a Python 3.11 environment example, device-matched
PyTorch/torchaudio, FFmpeg, Docker and other runtime examples. Code is MIT.
**Unresolved:** the selected PyPI/image release and complete compatible lock
have not been tested. Never substitute a mutable GHCR `main` example for a
Martlet release digest or assume one Python environment fits all inference.

### S16

[Pinned inference source](https://github.com/SWivid/F5-TTS/blob/9c614e9657089213efc6a7421b30630be138a3f5/src/f5_tts/infer/utils_infer.py),
[pinned socket example](https://github.com/SWivid/F5-TTS/blob/9c614e9657089213efc6a7421b30630be138a3f5/src/f5_tts/socket_server.py)

**Verified by source inspection:** default inference rate is 24 kHz.
`infer_batch_process(..., streaming=True)` reaches
`infer_single_process_streaming`; `_infer_basic` finishes model sampling and
vocoder decode for that text chunk before the generator yields waveform slices.
The socket example further chunks text and warms its model.

**Meaning:** remote audio can be streamed for playback and text can be split
into synthesis chunks. This does **not** demonstrate incremental audio output
while a single chunk is being synthesized, universal low latency, or prompt
GPU cancellation. Alternate TensorRT/Triton/upstream runtime paths would need
their own investigation and measurement.

The same source can initialize Whisper transcription when reference text is
empty and can fetch the vocoder from a model hub. **Consequence:** provide
reference transcripts and provision all auxiliary artifacts explicitly; do
not silently add downloads/VRAM pressure during first speech.

### S17

[Official F5 weight card](https://huggingface.co/SWivid/F5-TTS),
[raw card including license](https://huggingface.co/SWivid/F5-TTS/raw/main/README.md),
[official v1 base metadata](https://huggingface.co/api/models/SWivid/F5-TTS/tree/main/F5TTS_v1_Base)

**Verified upstream:** card declares `cc-by-nc-4.0`, distinct from MIT source
code. Metadata lists `model_1250000.safetensors` as **1,348,435,761 bytes** and
`vocab.txt` as **13,800 bytes** at access. Weight LFS SHA-256 reported:
`670900fd14e6c458b95da6e9ed317cdb20dbaf7a1c02ac06a05475a9d32b6a38`.
This is repository metadata, not a locally computed verification or release pin.

**Consequence:** resolve intended noncommercial/commercial usage and attribution
requirements before enabling distribution/use profiles. Do not bundle the
weights under Martlet's eventual code license or assume a user-supplied file
is unrestricted. H02 resolves immutable model revision and verifies acquired
bytes only after download authorization.

### S18

[Vocos model card](https://huggingface.co/charactr/vocos-mel-24khz/raw/main/README.md),
[Vocos artifact metadata](https://huggingface.co/api/models/charactr/vocos-mel-24khz/tree/main)

**Verified upstream:** the card declares MIT; `pytorch_model.bin` is
**54,365,991 bytes**, config is 461 bytes at access. Weight LFS SHA-256 reported:
`97ec976ad1fd67a33ab2682d29c0ac7df85234fae875aefcc5fb215681a91b2a`.
**Consequence:** vocoder is a separate artifact/license entry, not included
in the F5 weight-size estimate. Recheck its dependency/model chain at the
selected revision; the combined runtime size and VRAM remain unmeasured.

## Ubuntu, containers, and GPU administration

### S19

[Docker Engine on Ubuntu](https://docs.docker.com/engine/install/ubuntu/)

**Verified upstream:** current docs list Ubuntu 22.04, 24.04 and 26.04 and warn
that published container ports can bypass UFW/firewalld rules. Official package
installation and explicit versions are supported; convenience scripts are
not recommended as a production setup strategy. **Consequence:** choose one
Martlet-qualified distro, show a reviewed prerequisite plan, and verify actual
LAN access control instead of relying on UFW alone.

### S20

[NVIDIA Container Toolkit platform support](https://docs.nvidia.com/datacenter/cloud-native/container-toolkit/latest/supported-platforms.html),
[installation documentation](https://docs.nvidia.com/datacenter/cloud-native/container-toolkit/latest/install-guide.html),
[versioned driver prerequisite guidance](https://docs.nvidia.com/datacenter/cloud-native/container-toolkit/1.19.0/install-guide.html)

**Verified upstream:** the platform table (inspected in raw HTML because the
text extractor omitted it) lists Ubuntu 24.04 x86_64, also 22.04/26.04. NVIDIA
installation guidance requires a functioning host driver and recommends
distribution package-manager installation. **Consequence:** platform listing
is not proof of a specific GPU/driver/PyTorch/image tuple. Keep preflight stages
separate and pin exact toolkit/runtime versions in H02.

### S21

[NVIDIA CUDA compatibility](https://docs.nvidia.com/deploy/cuda-compatibility/minor-version-compatibility.html)

**Verified upstream:** minor-version compatibility has driver requirements
and limitations; the docs direct users to specific CUDA release notes.
**Consequence:** do not publish one guessed minimum driver for all model
images/GPU generations. Resolve driver/runtime/kernel features for the actual
selected image and require a real inference run.

### S22

[PyTorch previous versions](https://pytorch.org/get-started/previous-versions/)

**Research limitation:** the page was accessed but its extracted body contained
site markup/encoded content rather than a reliable version matrix. No exact
PyTorch/CUDA compatibility claim is based on that extraction. **Required H02
work:** inspect official instructions/release metadata for the chosen version,
pin PyTorch and matching torchaudio/CUDA wheels, and execute the target-GPU
smoke test. F5 README examples in S15 are examples, not qualification.

### S23

[Docker Compose GPU support](https://docs.docker.com/compose/how-tos/gpu-support/)

**Verified upstream:** GPU reservations require `capabilities`; `count` and
`device_ids` cannot both be specified. Device access depends on a correctly
configured host. **Consequence:** assign chosen devices explicitly; do not
treat reservation as exclusive VRAM isolation or actual inference validation.

### S24

[Docker Linux post-installation](https://docs.docker.com/engine/install/linux-postinstall/)

**Verified upstream:** Docker group membership grants root-level privileges.
**Consequence:** host setup must not silently add this membership; inference
gateway and Windows desktop do not get a Docker socket. A service's restart
behavior and application readiness still need separate lifecycle design.

### S25

[NVIDIA System Management Interface](https://docs.nvidia.com/deploy/nvidia-smi/index.html)

**Verified upstream:** power limits are watt-based, require administrator/root
rights, apply only on supported devices, and must fit reported min/max bounds;
UUID/PCI identity is preferable to unstable enumeration indexes.
**Proposed:** an optional explicitly approved host startup policy with readback
and restore. "80%" alone specifies neither a safe watt limit nor electrical
circuit safety. No power setting or privileged command was executed.

## Inspiration, optional features, and licensing

### S26

[Project AIRI repository](https://github.com/moeru-ai/airi),
[pinned AIRI license](https://github.com/moeru-ai/airi/blob/00c6867b7fd8064938de1805814578db8273dafe/LICENSE)

Revision resolved at access: `00c6867b7fd8064938de1805814578db8273dafe`.

**Verified upstream:** self-described AI companion with web/desktop targets,
provider integration and character-related subsystems; repository code license
is MIT. **Unresolved identity:** searches for exact "Project Airy" did not
establish the user's intended repository. AIRI is a plausible candidate, not
a confirmed identification.

**Suitability:** inspect provider configuration UX, audio state transitions,
and renderer separation for ideas once identity is confirmed. Its broader
web/character/game scope and evolving integration stack do not themselves
solve Martlet's installer, privacy, or two-GPU-host support problem. Do not
fork wholesale, copy assets, or assume MIT extends to SDKs/model weights.
No AIRI repository or asset was cloned/downloaded for this plan.

### S27

[Live2D SDK release license](https://www.live2d.com/en/sdk/license/),
[Live2D licensing FAQ](https://help.live2d.com/en/sdk/sdk_001/)

**Verified upstream:** SDK trial/development, release licensing, and Expandable
Applications have different requirements. General small-user exemptions do
not universally cover Expandable Applications; AI/chatbot/software cases are
explicitly addressed. **Unresolved:** Martlet's user-supplied avatar importer
needs classification/permission review, including Cubism Core/framework,
distribution and asset terms. The 2026-09-23 amendment authorizes Live2D
development now; A03b owns the unresolved release classification/rights gate,
not a prerequisite to A01/A02 independent development or voice MVP.

An initially attempted SDK-manual URL returned 404; the official licensing
page and FAQ above are the references used. No SDK or assets were downloaded.

### S28

[Ultralytics licensing](https://www.ultralytics.com/license)

**Verified upstream:** vendor presents AGPL-3.0 and Enterprise licensing
paths for its YOLO ecosystem and documents broad compliance requirements.
**Consequence:** determine actual code/model/architecture provenance and
applicable terms; do not assume a subprocess/container removes obligations.
"YOLO" is not a universal license. Prefer no detector until a task justifies
one, then compare rights-compatible implementations. This is a licensing
risk flag, not a legal opinion or authorization to buy a license.

### S29

[Sunshine getting started](https://docs.lizardbyte.dev/projects/sunshine/latest/md_docs_2getting__started.html),
[Moonlight project](https://moonlight-stream.org/)

**Verified upstream:** Sunshine describes itself as a self-hosted game-stream
host for Moonlight, recommends release binaries, and warns Docker images are
not recommended for most users. Its Linux packages have capture/driver
constraints. **Proposed:** optional separately consented host-admin guide with
package/display/GPU/firewall/boot verification. No claim of universal headless
Ubuntu remote desktop, no required privileged container, no public exposure.

## Installation flow research refresh

**Accessed 2026-09-22, America/Los_Angeles.** S30-S35 below are direct reads of
primary upstream documentation, not search-summary claims. Mutable upstream
pages describe candidate deployment paths; they neither update the frozen H02a/
H02b artifacts nor qualify a Martlet installation. No installer, container,
model, host probe or inference was executed.

### S30

[Docker Engine installation matrix](https://docs.docker.com/engine/install/),
[Ubuntu installation](https://docs.docker.com/engine/install/ubuntu/),
[Docker Desktop on Linux](https://docs.docker.com/desktop/setup/install/linux/)

**Verified upstream:** Docker supplies package instructions for named distro/
architecture combinations including Ubuntu, Debian, Fedora, RHEL and CentOS.
Its Ubuntu page currently lists 22.04/24.04/26.04; derivatives are not tested/
verified by Docker. Static binaries are available for other distributions,
which is not a tested GPU/service-lifecycle matrix. Docker Desktop on Linux
runs a VM with separate image/container storage and a `desktop-linux` context;
it can coexist with Engine but introduce port/resource conflicts.

**Consequence:** "all Linux supports Docker" is too broad for a support promise.
Recommend native Docker Engine + Compose for the managed Ubuntu host, even
with an Ubuntu desktop UI. Martlet's graphical host setup need not install
Docker Desktop. Qualify distribution, kernel, CPU architecture, engine,
driver/toolkit and image/model together. Detect existing contexts/services
without silently switching context, stopping another engine or removing packages.
S19's warning about published ports bypassing ordinary firewall rules remains.

### S31

[Docker Desktop Windows installation](https://docs.docker.com/desktop/setup/install/windows-install/),
[Desktop GPU support](https://docs.docker.com/desktop/features/gpu/),
[Desktop license terms](https://docs.docker.com/subscription-billing/desktop-license/)

**Verified upstream:** Windows Linux-container hosting has virtualization/WSL
prerequisites. Docker documents Desktop GPU access on Windows with the WSL2
backend and NVIDIA GPU/compatible Windows driver/WSL kernel. This is not an
AMD/Intel, native Windows-container GPU or Linux-Desktop GPU support claim.
Windows Server is excluded from Docker Desktop support. Installation/user
scope and backend have different permission requirements; a per-user install
does not establish that all prerequisites need no administrator or reboot.

Docker Desktop has its own subscription agreement: personal use, education,
noncommercial open source and qualifying small businesses have free-use terms;
larger commercial organizations and government entities require paid terms.
The stated small-business thresholds are fewer than 250 employees **and**
less than USD 10 million annual revenue. The page distinguishes these terms
from the unchanged open-source Docker Engine/Moby terms.

**Consequence:** Windows Docker/WSL2 is an explicit advanced lane, not a
prerequisite for the Windows companion or native Ollama. Recheck current OS
servicing, backend, GPU and licensing eligibility at the chosen release; do
not promise silent installation, universal GPU support or unattended boot.
No purchase or acceptance of third-party terms is authorized by this research.

### S32

[Ollama Windows](https://docs.ollama.com/windows),
[Ollama Linux](https://docs.ollama.com/linux),
[Ollama Docker](https://docs.ollama.com/docker)

**Verified upstream:** Ollama documents a native Windows application with a
per-user installer, background API, NVIDIA/AMD device-specific support, and a
standalone CLI archive for embedding/service integration. Its Linux guide
documents native archives and a systemd service. Its Docker guide provides
CPU, NVIDIA toolkit and AMD ROCm/device-access paths. These options do not
prove that an arbitrary GPU, driver or model will work.

**Consequence:** native Ollama is the simplest Windows hosting candidate;
Docker is not required just to run a Windows LLM. Containerized Ollama is the
first managed Ubuntu candidate. A tray/background process is not proof of
reboot-without-login service availability. Upstream installer auto-update and
external model changes must invalidate stale Martlet compatibility evidence;
do not call an externally managed install immutable. No service wrapper is
selected or installed by this plan.

The current H02b library remains literal-loopback-only and has no production
authorization issuer or application route. Container service-name transport,
host trust, model binding and native Windows lifecycle require implementation,
not just a Compose file. Current upstream docs do not supersede the pinned
native chat contract in S13a.

### S33

[Official F5-TTS README](https://github.com/SWivid/F5-TTS/blob/main/README.md),
[official weight card](https://huggingface.co/SWivid/F5-TTS)

**Verified upstream:** SWivid/F5-TTS documents Python environment/pip/editable
installation, matched PyTorch/torchaudio device variants, CLI and Gradio
inference, and Docker/Compose examples. Docker is one documented option, not
the only upstream installation path. The example uses a mutable GHCR `main`
image and exposes a Gradio port; it is not a pinned authenticated Martlet worker.
The README distinguishes MIT code from noncommercial pretrained-model terms,
consistent with S17's recorded CC-BY-NC-4.0 model card. Reference audio without
reference text can invoke extra ASR and GPU memory use.

**Consequence:** prefer an isolated pinned Linux worker for managed F5
deployment, with reference rights/transcript, explicit auxiliary provisioning
and a bounded adapter behind the gateway. Do not infer native Windows support
or a universal production API from pip install, Gradio, Docker examples or
device-specific instructions. Keep F5 optional: users may select API TTS or
text-only output. S16's chunk-generation versus audio-transport streaming
distinction and real cancellation/latency qualification remain unchanged.

### S34

[whisper.cpp README](https://github.com/ggml-org/whisper.cpp/blob/master/README.md),
[whisper.cpp server example](https://github.com/ggml-org/whisper.cpp/blob/master/examples/server/README.md),
[Microsoft .NET containers](https://learn.microsoft.com/en-us/dotnet/core/docker/introduction)

**Verified upstream:** whisper.cpp lists Windows (MSVC/MinGW), Linux and Docker
and supports CPU-only inference as well as backend-specific acceleration.
Its HTTP server is an example accepting audio uploads; the documentation warns
against administrative execution and requires isolation/input validation.
Microsoft supplies official .NET runtime/ASP.NET and SDK container images,
including Linux images and non-root options.

**Consequence:** package optional client-side CPU STT natively; use a separate
worker if users choose hosted STT. Do not expose an example upload server on
the LAN as a qualified/authenticated Martlet service. The planned ASP.NET Core
gateway fits a small non-root Linux container; this does not make WPF, WASAPI,
Windows credentials or device setup Linux/container applications. SQLite-backed
memory needs one owning service and persistent storage, not a newly required
PostgreSQL/Redis/vector database deployment.

### S35

[Compose GPU reservations](https://docs.docker.com/compose/how-tos/gpu-support/),
[Compose startup/readiness ordering](https://docs.docker.com/compose/how-tos/startup-order/)

**Verified upstream:** GPU access requires host support and explicit
`capabilities`; `count` and `device_ids` are mutually exclusive. Compose waits
for dependencies to start by default, not become ready; `service_healthy`
can gate dependency startup on a configured health check.

**Consequence:** device assignment is not a per-role VRAM quota. Measure
co-located inference workloads and preserve capacity for the desktop/other
users. Separate process health, trusted connectivity, artifact availability,
warmup and actual role inference. Cheap health checks must not download/load
models or perform recurring paid inference. Startup ordering alone does not
establish ongoing recovery, model readiness or a successful conversation.

## Avatar direction research refresh

**Accessed 2026-09-23, America/Los_Angeles.** S42-S47 are direct primary-source
reads supporting the [accepted avatar direction](AVATARS.md), not execution
evidence. No models, SDK/Core binaries or artist assets were acquired; no
license accepted, inference run or service installed. These mutable references
must be pinned/rechecked by each implementation owner before support/release.

### S42

[Cubism SDK lip-sync](https://docs.live2d.com/en/cubism-sdk-manual/lipsync/),
[SDK licensing](https://www.live2d.com/en/sdk/license/),
[selected Web Framework source](https://github.com/Live2D/CubismWebFramework/tree/8df84780f2aa1298f3b30965cdae143e049f3c8e)

**Verified upstream:** `.model3.json` lip-sync groups identify parameters;
the SDK manual describes scaling live audio level to model mouth controls.
The framework loads models in conjunction with separate Cubism Core.
The licensing page distinguishes development subject to SDK agreements from
release licensing, and calls out Expandable Applications for separate review.
Small-user exemptions are not a blanket exemption for an avatar importer.

**Accepted Martlet direction:** develop Live2D now, with explicit model-specific
mapping and bounded local assets, without implying permission to acquire SDKs,
accept terms or distribute them. First adapter targets Framework 5-r.4 at the
source above; the renderer owner's inspected matching SDK Core is
05.01.0000 (`0x05010000`). SDK/editor marketing versions are not Core versions.
Newer MOC/5.3 blend/offscreen support is not claimed. Exact native parsing,
rendering and licensing remain unqualified; framework availability alone
does not supply Core or rights to user artwork.

### S43

[Cubism Editor Motion-sync](https://docs.live2d.com/en/cubism-editor-manual/motion-sync/),
[official Unity MotionSync components](https://github.com/Live2D/CubismUnityMotionSyncComponents)

**Verified upstream:** audio is converted into time-series visemes and mouth
motion is produced by blending corresponding shapes predefined by the author.
This is distinct from amplitude-only mouth opening; arbitrary models do not
gain viseme shapes automatically. The official Unity plugin explicitly requires
Cubism SDK for Unity and states that MotionSync Core is not included in its
repository; its linked package is a separate acquisition.

**Consequence:** MotionSync is a later Live2D analyzer candidate. Account for
MotionSync Core separately from its GitHub framework, with exact runtime,
license and platform qualification before integration. The editor manual
does not prove a Martlet realtime streaming adapter or supply Core binaries.
The verified Unity plugin does not establish a supported Web MotionSync
runtime; that integration/availability remains an explicit candidate check.
Do not infer framework licensing covers Core or reuse the editor preview as
proof of actual Martlet playback synchronization.

### S44

[VRM 1.0 expressions specification](https://github.com/vrm-c/vrm-specification/blob/master/specification/VRMC_vrm-1.0/expressions.md),
[VRM Animation](https://vrm.dev/en/vrma/),
[VRMC_vrm_animation 1.0 specification](https://github.com/vrm-c/vrm-specification/blob/master/specification/VRMC_vrm_animation-1.0/README.md),
[three-vrm](https://github.com/pixiv/three-vrm)

**Verified upstream:** all VRM preset expressions are optional. Expressions
can combine morph/material/texture bindings; `overrideMouth`, `overrideBlink`
and `overrideLookAt` address procedural-expression conflicts. Custom expressions
exist, but the specification does not guarantee an ARKit rig. VRM Animation
describes humanoid bones, expressions and eye gaze with retargeting constraints;
draft and released-version behavior can differ. `three-vrm` provides VRM
support on Three.js with GLTFLoader.

**Consequence:** require per-model capability inspection and authored detailed
ARKit mapping. A basic vowel-only rig is reduced, not full face fidelity.
VRMA body/gaze applies to a supported VRM implementation, not directly to
Live2D. First Martlet VRM slice is facial only; VRMA playback is unsupported.
Do not adopt upstream CDN examples into a no-remote-assets renderer or assume
library code terms cover model/texture/motion assets.

### S45

[NVIDIA Audio2Face-3D SDK](https://github.com/NVIDIA/Audio2Face-3D-SDK),
[SDK license](https://raw.githubusercontent.com/NVIDIA/Audio2Face-3D-SDK/main/LICENSE.txt),
[Audio2Face-3D NIM overview](https://docs.nvidia.com/nim/digital-human/a2f-3d/latest/index.html),
[NIM sample application](https://docs.nvidia.com/ace/audio2face-3d-microservice/latest/text/interacting/sample-app.html),
[NIM gRPC contract](https://docs.nvidia.com/ace/audio2face-3d-microservice/latest/text/interacting/a2f-rpc.html)

**Verified upstream:** SDK repository code is MIT and documents NVIDIA
CUDA/TensorRT acceleration, external runtime dependencies and model acquisition,
including separately gated Audio2Emotion access. NIM documents speech-to-facial
ARKit output, not whole-body gestures. NIM v2 uses bidirectional
`ProcessAudioStream`, requires explicit `EndOfAudio`, and retains the
`nvidia_ace` 1.2 protocol module; old unidirectional endpoints are removed and
v1.3 configuration is not directly interchangeable. The interaction pages
redirected to NVIDIA's documentation archive at access.

**Selected first implementation lane:** a client for an already-running,
user-provisioned literal-loopback NIM v2 service, not native MIT SDK embedding.
The adapter owner pins official Samples v2.0 proto source at
`a2d0150043be7dc15db2fad8193a78b660e1100f`. Bounded authorized generated PCM
and output time conversion retain the original sample clock; the selected
adapter emits only the shared facial subset, not NIM head/extended tongue or
emotion metadata. Actual service/GPU/model execution remains NOT RUN.
**Update 2026-09-28:** the owner authorized Desktop's Automatic lip-sync to TCP-probe
the configured loopback endpoint before each generated-speech sentence and fall
back to local loudness lip-sync; provisioning remains the user's.

**Rights and evidence limit:** SDK MIT does not license NIM distribution,
weights, CUDA/TensorRT dependencies or avatar artwork. Record and approve each
separately. Upstream performance/minimum-hardware recommendations are not
measured Martlet requirements or latency results. No automatic provisioning,
model download, service probe or fallback is authorized by this choice.

### S46

[wawa-lipsync](https://github.com/wass08/wawa-lipsync),
[uLipSync](https://github.com/hecomi/uLipSync)

**Verified upstream:** `wawa-lipsync` is a TypeScript/web-audio realtime
lip-sync library with a viseme result and MIT repository license. Its examples
connect an audio element to an analyzer; this does not demonstrate integration
with Martlet's WASAPI clock. `uLipSync` is a Unity asset using Job System/Burst,
per-character calibration, runtime analysis and prebake/Timeline options.

**Consequence:** wawa is a later CPU/web candidate; qualify its actual host,
audio bridge, mapping and cost rather than assuming low latency. uLipSync is
conditional on Unity being independently chosen; do not introduce Unity just
to list an analyzer. Neither is implemented by this documentation or a silent
replacement for unavailable Audio2Face.

### S47

[Rhubarb Lip Sync](https://github.com/DanielSWolf/rhubarb-lip-sync),
[upstream README](https://raw.githubusercontent.com/DanielSWolf/rhubarb-lip-sync/master/README.adoc)

**Verified upstream:** Rhubarb analyzes recorded audio files and generates
mouth animation cues, with CLI TSV/XML/JSON outputs. Optional dialog text can
improve recognition. **Consequence:** later buffered/offline candidate, not a
presumed realtime streaming replacement. Provider-supplied visemes are a
different path and require an explicitly supported provider/model/timestamp
contract; ordinary TTS audio support alone establishes none.

## S36-S40: multi-engine voice research, 2026-09-23

The owner expanded self-hosted voice scope to F5-TTS, Qwen3-TTS, Chatterbox,
GPT-SoVITS and XTTS-v2. [Voice Studio research](VOICE_STUDIO.md#research-implementation-not-marketing-compatibility)
and primary sources V1-V5 record the respective S36-S40 findings: actual
Python/native API entry points, reference limits, training recipes, conflicting
dependency versions, implicit downloads/preprocessing and license boundaries.
These are upstream observations, not pinned/installed/qualified Martlet
runtimes. In particular, XTTS CPML restricts model and output use to
noncommercial purposes; Chatterbox managed training is unverified. Each engine
requires isolated dependencies and its own artifact closure.

## S48: iOS and iPadOS platform research, 2026-09-30

Primary Apple, Microsoft and GitHub sources are listed with the
[iOS plan](IOS.md#sources). **Verified upstream:** Foundation Models is
Swift-only, on Apple Intelligence devices, 4,096-token context on iOS 26; iOS 27
adds on-device image input and an entitled Private Cloud Compute model (WWDC26
session 241). SpeechAnalyzer (iOS 26) is on-device and Swift-only. Kestrel is
not supported on iOS. The `macos-26` hosted runner image ships Xcode 26.x.
**Consequence:** a native Swift app that reimplements gateway protocol 2.0
against C#-generated vectors; host mode is foreground-only; screen watching
needs a user-started broadcast. Free-provisioning limits, extension memory,
Game Mode effects and App Review stances are community-reported and remain
unverified.

## S49: smart home, Matter and IP camera research, 2026-09-30

Sources are listed with the [smart home plan](SMART_HOME.md#sources).
**Verified upstream:** Home Assistant's official MCP Server integration serves
its Assist API at `/api/mcp` over stateless Streamable HTTP with a long-lived
token or OAuth, limited to exposed entities, without sampling or
notifications. python-matter-server is archived; its successor matterjs-server
(Apache-2.0, Node.js) is beta and targets Matter 1.6. Matter 1.5 added cameras
over WebRTC; 1.6 added NFC commissioning and Joint Fabric. go2rtc (MIT) serves
`/api/frame.jpeg`; Frigate is MIT. On ollama.com, `gemma3` and `qwen2.5vl` are
vision-only, while `qwen3-vl` and `gemma4` list vision and tools. Google Home
APIs ship only Android and iOS SDKs. **Consequence:** integrate through Home
Assistant's MCP server first; add opt-in tool calling and an MCP client; treat
cameras as snapshot frame sources sharing the video-source seam; native Matter
only as an optional host container. Vendor API details for Hue, Shelly,
SmartThings, Alexa, Nest and Ring are summarized from documentation and
remain unexercised.

**Addendum 2026-10-01 (S49a).** Verified on developers.home-assistant.io: the
Conversation API `POST /api/conversation/process` takes `text`, optional
`language`, `agent_id` and `conversation_id`, and answers `response_type`
`action_done`, `query_answer` or `error` (`data.code` `no_intent_match`,
`no_valid_targets`, `failed_to_handle`, `unknown`) with `speech.plain.speech`
and `data.success`/`data.failed` targets typed `area`, `floor`, `domain`,
`device_class`, `device`, `entity` or `custom`. **Consequence:** SH00 uses the
built-in agent through this API so control works with models that lack tool
calling; HA MCP tools follow through the shared MCP client. Not exercised
against a real Home Assistant.

## Linux service state custody and durable I/O (H03b3)

**Accessed 2026-09-23.** Primary upstream contracts, not native Martlet evidence:
[Secret Service introduction](https://specifications.freedesktop.org/secret-service/latest/ch01.html)
describes a login-session service that may need unlocking.
[systemd-creds 255](https://www.freedesktop.org/software/systemd/man/255/systemd-creds.html)
describes host/TPM key custody; its `tpm2-absent` mode explicitly provides neither
confidentiality nor authenticity. Credential delivery is not automatically a
writable crash-safe checkpoint store. No such null-key mode is proposed.

[openat2](https://man7.org/linux/man-pages/man2/openat2.2.html) supplies non-following,
beneath and no-mount-crossing resolution;
[statx](https://man7.org/linux/man-pages/man2/statx.2.html) requires checking
returned metadata masks;
[flock](https://man7.org/linux/man-pages/man2/flock.2.html) provides cooperative
open-file-description locks;
[renameat2](https://man7.org/linux/man-pages/man2/rename.2.html) provides atomic
replacement/no-replace semantics. Crucially,
[fsync](https://man7.org/linux/man-pages/man2/fsync.2.html) of a file does not
persist its directory entry: directory fsync is also required.

**Implemented candidate, not native-qualified:** explicitly selected
`LinuxServicePermissions` under a stable non-root UID, local ext4, 0700/0600;
plaintext at rest with damage checksum, not encrypted/authenticated against a
state writer. Same-UID/root/offline-disk/rollback threats remain. Operator-managed
disk encryption is separate; no machine policy is changed. Windows DPAPI is
unchanged and never silently downgraded. Portable modeled checks and compilation
do not establish real Ubuntu/UID/mount/fsync/reboot/container evidence.

## Provenance policy for future implementation

For every shipped dependency/artifact, record upstream URL, immutable source/
release revision, integrity digest, build provenance, selected version, license
identifier/text, notices, redistribution constraints, known vulnerabilities,
and responsible maintainer. Keep code, model weights, tokenizer/vocoder,
training-data-related restrictions, reference voices, Cubism SDK components,
and avatar assets as separate entries. Request explicit model/license consent
before downloads; consent does not make prohibited use permissible.

Repository code has **no project license yet**. Recommend an owner-approved
permissive license for original code if distribution is intended, without
imposing that choice or relicensing upstream material. Generated/synthetic
fixtures also need provenance; do not publish a user's conversation or an
unlicensed speaker imitation as a test asset.

Unknown model sizes, VRAM floors, commercial rights, SDK classification, provider
quotas, and tested versions are release-gate work, not blanks to fill with
plausible numbers. Recheck volatile sources at model/provider selection and
before release; preserve the dated rationale when a decision changes.
