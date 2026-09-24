# H02d pinned container metadata candidates

**Metadata slice only; no runnable preset, installer or runtime qualification.**
[host-artifacts.v2.json](host-artifacts.v2.json) is accepted by the existing
[offline reader and ArtifactDoctor](../../../src/Martlet.HostArtifacts/README.md).
The original [v1 archive/model catalog](host-artifacts.v1.json) is unchanged.
Select the v2 file explicitly; nothing in app settings, Desktop, the gateway,
Compose or the installation planner automatically consumes or approves it.

## Capture and trust boundary

Accessed **2026-09-23 UTC (2026-09-22 America/Los_Angeles)**. Read-only public
upstream documentation, registry metadata and official model repository
metadata only. Anonymous pull-scoped registry bearer tokens were held only in
memory, not saved. No layer, executable, source archive, model or weight was
downloaded; no Docker client/daemon/socket, credential store, host/GPU probe,
inference, account provisioning or installation was used.

Registry research used a scratch-only HTTPS helper: 1 MiB maximum per response
(read limit + 1), 20-second socket timeout, at most three redirects, an explicit
official-host allowlist, and no forwarding of Authorization on redirects.
The Ollama configuration followed the registry's
`production.cloudfront.docker.com` redirect. No signed redirect URLs or tokens
are committed. These research requests are **not** part of the library, CLI or
ordinary tests; those paths remain entirely offline.

The lookup tags below were observed once, then the resulting immutable
manifest/index locators were fetched again. SHA-256 and byte lengths of the
small index, selected manifests and configurations were checked against their
descriptors (the top-level lookup digest was computed from its exact bytes).
This proves consistency of the captured metadata bytes, **not** publisher
signature verification, a source-to-build attestation, downloaded layer
verification, runtime behavior or legal approval.

The `evidence` directory contains the captured Ollama index and both image
manifests, not authored fake metadata. Registry bodies had LF line endings and
no final newline; checked-in text has a final newline and may acquire checkout
line endings. Local regression tests restore LF/remove terminal newlines,
verify the original lengths/hashes, and compare every descriptor and ordered
layer occurrence to the candidate catalog. Config bodies are not committed;
their descriptor hashes/lengths and observed facts are recorded below.
Synthetic mutation tests remain explicitly separate in the tests directory.
The offline inspector does not fetch or authenticate these evidence files,
and still reports `payload_verified`/`source_assertions_authenticated: false`.

## Official Ollama 0.34.0, linux/amd64

[Official Docker guide](https://docs.ollama.com/docker) names `ollama/ollama`.
Lookup used `registry-1.docker.io/v2/ollama/ollama/manifests/0.34.0`, not `latest`,
ROCm or a newer runtime. The
[GitHub tag metadata](https://api.github.com/repos/ollama/ollama/git/ref/tags/v0.34.0)
still resolves to H02a/H02b's
`d8ab4b4f0ca24b51d3a46b3bf4f462e58ce66b1f`.

| Object | SHA-256 (without prefix) | Exact metadata/descriptor bytes |
| --- | --- | ---: |
| OCI index | `684d8674b4315fa18f4f0e973a118ec2652ed96f67563277839985175858e0ba` | 647 |
| Selected OCI image manifest | `aa6f86f01fee264c81f1edd9083ebfb07c8116d95d8bedd1ad470874b66a40b4` | 1,065 |
| Configuration | `31aae755296d2792fe70020d9012e8f0c181375c926a32809ccd946b92cb4489` | 19,380 |

Immutable evidence: [index](https://registry-1.docker.io/v2/ollama/ollama/manifests/sha256:684d8674b4315fa18f4f0e973a118ec2652ed96f67563277839985175858e0ba),
[selected manifest](https://registry-1.docker.io/v2/ollama/ollama/manifests/sha256:aa6f86f01fee264c81f1edd9083ebfb07c8116d95d8bedd1ad470874b66a40b4),
[configuration](https://registry-1.docker.io/v2/ollama/ollama/blobs/sha256:31aae755296d2792fe70020d9012e8f0c181375c926a32809ccd946b92cb4489).
Registry URLs can require a fresh anonymous token; none is embedded here.

The index's linux/amd64 descriptor and the config's `os`/`architecture` agree.
The arm64 descriptor is captured only as index context; its image and blobs
are not selected, fetched or counted. The config declares creation
`2026-09-09T23:17:47.304461496Z`, entrypoint `/bin/ollama`, command `serve`, and
`OLLAMA_HOST=0.0.0.0:11434`. These are upstream declarations, not launch
instructions or proof that a LAN service is safe. The config's version label
is `24.04` (base OS), **not** evidence of the Ollama application version. No
application revision label/build attestation was established; tag association
and the separately pinned recipe do not prove the built binary matches it.

The [pinned Dockerfile](https://raw.githubusercontent.com/ollama/ollama/d8ab4b4f0ca24b51d3a46b3bf4f462e58ce66b1f/Dockerfile)
uses Ubuntu 24.04 for the final stage and names `ca-certificates`,
`libvulkan1`, `libopenblas0`; these remain unversioned recipe observations,
not a package lock/SBOM. Native/GPU build stages and copied libraries require
their own component, provenance, vulnerability and rights inventory. The
[source MIT license](https://raw.githubusercontent.com/ollama/ollama/d8ab4b4f0ca24b51d3a46b3bf4f462e58ce66b1f/LICENSE)
is not a license for every bundled component or any LLM weights.

Four gzip layer descriptors plus config, manifest and index total
**3,703,364,316 known listed transfer bytes**. No LLM weights are selected.
No inference, :local behavior, denied-egress behavior, native Ubuntu or GPU
qualification is claimed by this metadata slice.

## Official F5 candidate, linux/amd64

The [previously pinned official README](https://raw.githubusercontent.com/SWivid/F5-TTS/9c614e9657089213efc6a7421b30630be138a3f5/README.md)
names `ghcr.io/swivid/f5-tts:main`. That mutable lookup resolved at capture to
the following **single Docker schema-2 image manifest**, not an image index.
The catalog records no floating tag and `index: null`; an index was not
invented by treating a single manifest as one.

| Object | SHA-256 (without prefix) | Exact metadata/descriptor bytes |
| --- | --- | ---: |
| Selected image manifest | `908c0a1e0b5e2279eb8dc24f68eaddbc2911c019631bfbf3580d353491730f3b` | 4,305 |
| Configuration | `a85f6e64a9df3380162597c8d6957a0c1c2400d652cbb0109e67f9ca8d9b45ca` | 23,441 |

Immutable evidence: [manifest](https://ghcr.io/v2/swivid/f5-tts/manifests/sha256:908c0a1e0b5e2279eb8dc24f68eaddbc2911c019631bfbf3580d353491730f3b),
[configuration](https://ghcr.io/v2/swivid/f5-tts/blobs/sha256:a85f6e64a9df3380162597c8d6957a0c1c2400d652cbb0109e67f9ca8d9b45ca).
Config declares linux/amd64 and creation
`2026-09-21T14:27:16.566761569Z`. It labels source `SWivid/F5-TTS`, revision
`283252563dbf91be625e0c27926acfaac449186c`, version `main` and license `MIT`.
This is a **different recipe revision from v1**, recorded explicitly in v2;
it is not a retroactive upgrade or verification of v1's runtime.

The [label-referenced pyproject](https://raw.githubusercontent.com/SWivid/F5-TTS/283252563dbf91be625e0c27926acfaac449186c/pyproject.toml)
declares `1.1.22`, 28 base Python dependencies and two build requirements,
transcribed as declarations, excluding optional evaluation extras. This is
the catalog's recipe version, not a measured installed package version.
The [label-referenced Dockerfile](https://raw.githubusercontent.com/SWivid/F5-TTS/283252563dbf91be625e0c27926acfaac449186c/Dockerfile)
uses `pytorch/pytorch:2.4.0-cuda12.4-cudnn9-devel`, installs unpinned native
packages and performs an **unpinned git clone/submodule update and pip
installation**. Neither label revision nor captured history proves what
source/dependency revisions were installed. Source/build/dependency closure
is **unestablished**, not reproducible or known to match H02a. Config
`PYTORCH_VERSION=2.4.0` and `CUDA_VERSION=12.4.0` are declarations only, not
framework/driver compatibility or installed-package evidence.

The image config declares root user, port 7860, the NVIDIA base entrypoint,
and a Hugging Face cache volume. It is not a non-root Martlet gateway worker,
authenticated API, service lifecycle or qualified H04 implementation.
Its MIT label and [source license](https://raw.githubusercontent.com/SWivid/F5-TTS/283252563dbf91be625e0c27926acfaac449186c/LICENSE)
do not cover all base-image/native/Python components or model rights.

There are 19 ordered layer occurrences, **17 unique layer blobs** and one
configuration. The 32-byte gzip blob `sha256:4f4fb700ef54461cfa02571ae0db9a0dc1e0cdb5577484a6d75e68dc38e8acc1`
appears three times. V2 now preserves these legitimate repetitions while
requiring identical kind/size facts; repeated configuration records remain
invalid and every occurrence still consumes collection bounds.
Transfer accounting counts the shared blob once, saving 64 bytes versus a
naive occurrence sum. Manifest + unique blobs total
**11,772,831,710 known listed transfer bytes**.

## Existing F5/Vocos model inventory, not new model selection

The four model/auxiliary records are preserved exactly from v1. Immutable
[F5 tree metadata](https://huggingface.co/api/models/SWivid/F5-TTS/tree/84e5a410d9cead4de2f847e7c9369a6440bdfaca/F5TTS_v1_Base)
and [Vocos tree metadata](https://huggingface.co/api/models/charactr/vocos-mel-24khz/tree/0feb3fdd929bcd6649e0e7c5a688cf7dd012ef21)
were re-read, not the actual artifacts. The two LFS SHA-256/size declarations
remain expected payload identities, not locally verified weights. Vocabulary
and Vocos config have only Git blob SHA-1 evidence: their payload SHA-256
remains null. No inferred conversion of Git blob IDs into content hashes.

The pinned [F5 card](https://huggingface.co/SWivid/F5-TTS/raw/84e5a410d9cead4de2f847e7c9369a6440bdfaca/README.md)
declares CC-BY-NC-4.0; the pinned
[Vocos card](https://huggingface.co/charactr/vocos-mel-24khz/raw/0feb3fdd929bcd6649e0e7c5a688cf7dd012ef21/README.md)
declares MIT. Neither constitutes owner legal acceptance or verifies all
training-data, reference-voice, distribution or intended-use rights.
Reference consent and supplied transcripts remain mandatory separate gates.

## Production inspection inventory and remaining gates

| Selection | Unique image metadata/compressed bytes | Existing listed model/auxiliary bytes | Known listed subtotal |
| --- | ---: | ---: | ---: |
| `ollama-llm` | 3,703,364,316 | 0 (LLM weights missing) | 3,703,364,316 |
| `f5-tts` | 11,772,831,710 | 1,402,816,013 | 13,175,647,723 |
| Both | 15,476,196,026 | 1,402,816,013 | 16,879,012,039 |

The combined image inventory has 26 unique content identities and 23 unique
blobs, all with known encoded/compressed descriptor sizes. JSON config/index/
manifest byte lengths are uncompressed metadata lengths within the existing
transfer-size field; gzip layer lengths are **compressed**, not expanded.
Every expanded/staging blob size is null; the 23 unknowns are not zero-byte
storage. Digest equality permits deduplicating content transfer facts, not
inferring shared installed snapshots, layer-application disk cost or peak
staging. F5's repeated layers do not change that distinction.

`blob_inventory_complete: true` means all descriptors of these selected
manifests were enumerated, **not** complete model/package/runtime closure.
Total download, expanded/installed/peak/free disk, runtime fit, RAM/VRAM,
GPU/driver/toolkit and native-host evidence remain unknown. Matching
`ubuntu-24.04-x64` and `linux/amd64` selectors exit **2**, disabled/incomplete;
the target is selected intent, not a host probe or F5 base-OS claim.
`linux/arm64` exits **1** for mismatch and does not auto-select another image.

The completed catalog slice is immutable metadata/inventory and production-reader/CLI
regressions, with the v1 contract preserved. The separate
[Host.Setup image acquisition API](../../../src/Martlet.Host.Setup/README.md#verified-selected-image-acquisition)
can now acquire this exact selected-role image batch through the two named
public registries, under fresh rights/download consent, with verified CAS bytes
and atomic OCI layout publication. It does not alter this catalog or make its
offline inspection report verified. Its tests use authored local TLS fixtures,
not these actual image layers. Separate batches can repeat downloads/storage;
deduplication applies only within the selected image batch.

Remaining H02d gates include actually performing separately authorized acquisition
and content verification for a deployment, missing auxiliary
hashes, exact source/binary/package/model closure, component notices/rights,
and a deliberately selected LLM. Runtime/denied-egress/GPU tests, H01 native
evidence (PR #21 remains held), H04/H05 lifecycle and H06 combined fit are
separate unfinished work. All candidates remain non-runnable/ineligible.
