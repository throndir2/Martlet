# H05a local review boundary

The reused [Host.Setup library](../../../src/Martlet.Host.Setup/README.md)
provides bounded planning and a real local review journal, **not an Ubuntu
bootstrap command**. There is intentionally no deploy script here.

Supply the canonical Inventory report metadata and current HostArtifacts v1/v2
manifest to the library. Its bound overload uses the shared Core Installation
planner; the frozen Llm/Tts adapter never adopts disabled roles or external
services and does not execute proposed filesystem/runtime/artifact/gateway work.
Decoded `LiveLocal` and planner eligibility are not actual host observations,
ownership, qualification, acquired content verification or executable consent.

Select an existing private local journal directory. Public review approval
permits only the exact journal writes; recorded review remains untrusted
historical metadata. Old v1 completion journals are preserved and rejected,
not migrated into an execution or pairing permission. A stale `.pending` file
requires explicit operator investigation; do not blindly delete it or user data.

## Separate verified image acquisition

After recording review, the same coordinator's `PreviewImagesAsync` /
`RunImagesAsync` can acquire the exact selected-role Ollama/F5 image batch into
an explicitly selected existing private local parent. Each image needs separate
current rights evidence and the batch needs fresh one-use download approval.
Supported public paths are Docker Hub `ollama/ollama` and GHCR `swivid/f5-tts`,
exact `linux/amd64`, including bounded anonymous token and documented CDN hops.
There is no credential lookup, Docker daemon, extraction or container execution.

The resulting OCI layout appears only after all selected manifest/config/layer
bytes are verified and its staging directory is atomically published. Original
Docker schema-2 bytes and ordered repeated layers are preserved. A multi-platform
index is retained only as verified evidence, not a promise that other platforms
were downloaded. Deduplication is within the exact batch; separate selections
can repeat downloads and storage. Owned corruption needs reviewed whole-layout
quarantine and a fresh full replacement budget; foreign files are preserved.

The image journal is strict format 3, distinct from format-2 LocalReview and
GitHub file acquisition. None of these records is host/runtime qualification.
HF/model acquisition and the end-user installation/supervision journey remain
separate. See the [image API contract](../../../src/Martlet.Host.Setup/README.md#verified-selected-image-acquisition).

Device pairing remains permanent until explicitly revoked, not reset on reboot
or tied to journal review expiry. This module issues no gateway credentials and
stores no perishable paired-readiness state. The separate permanentGateway
protocol 2 and H01 PR #21 native qualification gates remain unchanged.

Local Windows IO tests do not establish native Linux `fsync`, symlink, power-loss,
boot, GPU/model or deployment qualification. Source attribution, supported APIs,
resume semantics and the exact later acquisition/supervision port adaptations
are documented in the module README. No packaging, release or workflow is added.
