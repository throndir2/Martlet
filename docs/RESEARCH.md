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
distribution and asset terms. A01 blocks avatar distribution, not voice MVP.

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
