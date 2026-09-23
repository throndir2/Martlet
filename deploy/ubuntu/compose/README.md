# Selected-role configuration boundary (H05c-A)

There is intentionally **no static runnable Compose file** here.
[Host.Setup](../../../src/Martlet.Host.Setup/README.md#selected-role-configuration-publication-h05c-a)
now generates and publishes actual owned configuration files. It does not
start services or claim that current candidate images implement Martlet hosts.

The canonical shared plan selects roles and groups physical machines/resources.
An exact LLM-only selection does not require F5, STT, perception or memory.
Existing API/paired-remote choices are not silently installed as containers.
Stable explicit host/deployment/owner IDs survive configuration revision changes.
Each preview distinguishes local configuration destination from proposed Ubuntu
runtime/state paths; publication requires a new default-No local-file decision.

Verified image content can produce only an accurate partial Ollama service
object: immutable digest/platform, no-pull and proposed private isolation.
There are no guessed commands, health executables, compatible UID/GID, ports or
model mounts. Missing content produces a report without that fragment.
Unknown gateway/F5/non-root recipes, source/build provenance, model selection/
rights and engine import remain explicit findings. Individual fragment JSON
must not be treated as an executable Compose document or qualified preset.
The public composition has no arbitrary executor or caller-supplied live facts.

## Source reuse, not source assumptions

Generation/publication reuse is bounded to
`016f1131e899d1aaa03c7e130534d6d7c5cf108d`, with current-owner adaptations; its
`97a76e94011d851b6ee6a9b5c8b2f4751551c9e9` dependency update is not copied.
The old Completed journal, Gateway v1, `/app/martlet-health` and
definition-hash project identity are not valid current deployment contracts.

The later source `4be5aeb903e1cb71c58f603e0b6c2d9bb0a84b23` was inspected, not
imported. Its offline Dockerfile/context generator still requires caller-supplied
gateway/health/private-worker executables that it does not implement. It forces
optional roles and assumes official Ollama contains Martlet host/health wrappers.
A payload/archive SHA-256 is not interchangeable with an OCI manifest digest.
Those gaps require implementation, not a renamed candidate or invented digest.

## Next executable target: gateway plus Ollama

The next focused service owner must supply the actual dedicated non-root Linux
gateway executable and exact config/health/state contract using the existing
durable authority. Linux permission-backed state is an explicit unqualified,
plaintext-at-rest candidate, distinct from Windows DPAPI. Do not reuse a
Windows-only loopback CLI as an assumed Linux/LAN service.

Compose an actual typed `IOllamaGatewayInferenceWorker` and cheap `IGatewayWorker`
status through the canonical Gateway protocol-2 owner. Retain
`IGatewayInferenceWorker.AcquirePermissionAsync` and
`GatewayInferencePermissionLease` per-action authorization. Private container
transport needs its own reviewed route boundary; do not relax the existing
provider loopback issuer globally. A paired device, healthy process or
successful publication does not grant inference authority.

Build definitions must describe those real selected executables, exact base/
dependency materials, non-root data/config/model mounts and separate identities.
No new model is selected by this slice. Actual locally authorized builds and
image-digest/provenance reconciliation are separate from authoring definitions.
Current OCI layouts remain opaque verified bytes until an explicit import
adapter is implemented and tested; they are not assumed `docker load` inputs.

F5/perception are optional additions requiring their actual bounded stdio
transport/private host adapter. The upstream F5 root/Gradio image is not that
adapter. Hosted STT and other optional roles have their own contracts; they
cannot become prerequisites for typed LLM use.

## H05c-B supervision and remaining gates

After the service contracts, implement a real bounded native adapter using an
exact executable, selected local engine/context/daemon, committed config,
stable owned project and fixed argument lists. Separate observation from fresh
host-local start/stop/reconcile permission. No arbitrary shell/arguments,
ambient context adoption, implicit pull/build, remove-orphans or volume deletion.
Foreign or ambiguous services remain untouched.

Full Compose must use an isolated private worker network, gateway-only ingress,
verified non-root recipes, no host networking/privilege or gateway Docker socket.
Preserve protected permanent identity outside images/revision directories.
Restart/update/certificate same-key renewal must not regenerate keys, expire
pairings or revoke devices. Default removal preserves identity and user data.

Supervision needs bounded retry/backoff, OOM/deadline/cancel and orphan-retirement
ownership, not indefinite Docker restarts. Downloading, loading, warming, ready,
busy and failed are distinct. Cheap health is not actual selected-model inference
or readiness; boot availability and reboot recovery require their own evidence.

Tests in A use actual temporary files and inert OCI bytes through the existing
controlled transport fixture; there is no real registry/model/engine operation.
Future command-adapter tests may use an inert owned fixture executable, not
Docker on the development machine. Ubuntu/native locking/fsync, images, model/
GPU, inference, boot/reboot and LAN qualification remain **NOT RUN**.
Windows Docker Desktop/WSL2 and other Linux distributions are independent gates.
Guided Ubuntu UI/headless and advanced Docker must eventually consume the same
coordinator, not duplicate plans or infer success from stored review history.
