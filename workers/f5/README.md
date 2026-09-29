# Martlet F5 worker

This directory contains the isolated `martlet.f5.worker` protocol version
`1.0` Python process. It is a bounded canonical-JSON stdio worker; it does not
open an HTTP listener, bind a public interface, expose an OpenAI-compatible
route, run a shell, fetch a URL, download a model, select a default voice, or
transcribe reference audio.

The Martlet host `f5` role (see [deploy/host](../../deploy/host/README.md)) runs
it in a container built from [`host/Dockerfile`](host/Dockerfile): the
hash-locked runtime from [`host/requirements.lock`](host/requirements.lock),
the runtime inventory generated at build time, and
[`host/martlet_f5_host.py`](host/martlet_f5_host.py), a separate process that
listens only on 127.0.0.1:50080 for the gateway relay `Martlet.Gateway.F5`,
provisions the pinned model files into the role's volume, writes the worker
config and drives this worker over stdio (warmup, one synthesis at a time,
cancel). The worker itself still opens no listener and downloads nothing.
Two runtime fixes live in that image: `f5_tts` gets an empty `__init__.py`
(f5-tts 1.1.22 ships a namespace package; the origin checks need a regular
one) and urllib3's import-time IPv6 socket probe is replaced with its result
(the audit hook denies every socket). File-less pseudo-modules such as
`torch.ops` are skipped by the imported-origin check.

The package is outside the root solution, Desktop/Core settings and Windows
packaging graphs. Only the host `f5` role starts it, inside its container.

## Reviewed source reuse

Reused only `workers/f5` from
`6f245df0f79d46575657ab254c900a671ba91e6c` ("Add bounded F5 Python worker"),
originally authored by throndir with Copilot App co-authorship. No source
ancestry, old Gateway authentication contracts, image catalog, Docker build,
settings, root documentation, or runtime/model payload is imported.
The original engine/protocol implementation is retained with fresh
framing, deadline, event-limit, side-effect-denial and ownership fixes.

The reference is current `src/Martlet.F5` typed worker contracts on main,
not the historical Gateway v1 authentication wire. Worker protocol **1.0**
and Gateway authentication protocol **2** are distinct. Permanent device
pairing remains permanent until explicit revocation; this worker changes
neither that policy nor its storage. Each synthesis still has an independent
bounded action deadline. No automatic launch, API listener, C# stdio adapter,
production-host integration, enrollment, authorization, or deployment is added.

## Contract boundary

Each input and output record is one UTF-8 JSON object followed by LF. Objects
must use the exact canonical encoding produced by:

```python
json.dumps(value, ensure_ascii=False, allow_nan=False,
           separators=(",", ":"), sort_keys=True).encode("utf-8")
```

Duplicate/unknown/missing properties, noncanonical escapes or whitespace,
invalid UTF-8, CRLF framing, non-finite numbers, nesting over 32 levels,
a line over 6 MiB (including LF), and EOF
inside a record fail closed. The small
[`status.v1.json`](fixtures/status.v1.json) fixture is a canonical transport
example. Stdio is the only production entrypoint; lifecycle controls are
transport controls and do not add another synthesis API.

The `synthesize`, `cancel`, and `invalidate_reference_cache` objects mirror
the managed worker v1 typed contracts (not an implemented C# wire transport):

- synthesis contract/version, action/session/turn/request IDs, destination,
  and an absolute UTC deadline are mandatory; cancel and invalidation retain
  their distinct managed control shapes;
- `expected_worker` includes the exact worker/build, F5 source/package,
  Python, Torch, torchaudio, CUDA runtime, cancellation, and all five artifact
  identities;
- one through sixteen ordered chunks are accepted, with the managed
  2,048-character/4 KiB per-chunk and 16 KiB aggregate UTF-8 bounds;
- the reference is snapshotted WAV bytes plus a user-supplied transcript,
  exact audio/transcript SHA-256 values, their combined revision, preset ID,
  and exact parsed PCM format;
- the execution policy must explicitly forbid transcription, artifact
  download, default voice, model fallback, and provider fallback; and
- output is only mono 24 kHz signed PCM16 little-endian. Frames are contiguous
  and at most 4,800 samples, chunks and the stream complete explicitly, and
  the total is at most 90 seconds.

Reference parsing matches the managed RIFF/WAVE boundary: one 16-byte PCM
format chunk, one aligned non-silent data chunk, mono PCM16 at 16/22.05/24/
44.1/48 kHz, 1-30 seconds, and no more than 4 MiB. Requests contain bytes, not
paths or URLs. Transcript, audio, and combined revisions are recalculated
before engine use.

## Lifecycle and bounded execution

The worker emits `worker_state` records and starts `cold`. A canonical
`warmup` control must carry the exact expected identity and a deadline no more
than five minutes away. States are:

`cold` -> `verifying_runtime` -> `verifying_artifacts` -> `loading_model` ->
`warming` -> `ready`

Failure transitions to `failed` with a bounded actionable error. A retry must
use another explicit warmup. Synthesis is accepted only in `ready`; exactly
one job runs and a second request receives `busy` rather than entering a
queue. Warmup is also rejected while synthesis is active. An independent
watchdog publishes a warmup deadline failure even if a third-party model-load
call is still blocked; any late completion is quarantined and cannot publish
`ready`.

The deadline watchdog publishes a terminal `deadline_exceeded` and discards
all later engine output. Cancel is idempotent, acknowledges local discard,
and publishes a terminal `canceled`. The selected production identity is
`discard_only`: the worker does **not** claim that F5/Torch compute stopped.
It remains `busy` until the engine call actually returns, while all late
frames are suppressed. EOF without a complete JSON line is a transport error.
Event counts include the terminal record: one slot is always reserved for a
bounded failure. Deadlines never restart after validation or chunk boundaries.
EOF/close cancels warmup and discards active synthesis but retains engine,
reference and temporary-file ownership until the executing call returns.
`stopping` is not `stopped`; model work is not forcibly terminated. A stuck
native call can therefore keep the process alive pending external supervision.

Reference bytes are held in a wipeable buffer. Production synthesis creates
one private per-job temporary WAV only because the pinned F5 path consumes a
filename; it is removed by `TemporaryDirectory` on every success/failure path.
No generated audio or text is written, and neither is included in errors,
state records, or ordinary output other than the required transport payload.

## Identity config and artifacts

Start the worker with one explicit absolute local config:

```powershell
$env:PYTHONPATH = 'workers\f5'
python -m martlet_f5_worker --print-build-revision
python -m martlet_f5_worker --config C:\private\martlet-f5\worker-config.json
```

`--print-build-revision` returns the SHA-1-shaped revision over the worker
package sources that must appear in `runtime.worker_build_revision`. The
config itself is bounded canonical JSON with:

| Field | Requirement |
| --- | --- |
| `format_version` / `kind` | `1` / `martlet_f5_worker_config` |
| `engine` | Exactly `fake` or `f5` |
| `artifact_root` | Existing bounded absolute local directory without symlink ancestors |
| `artifact_files` | Exact role-to-canonical-relative-file mapping; no URL or traversal |
| `worker` | Full integrated v1 worker identity |
| `device` | `null` for fake; explicit `cuda:0` through `cuda:15` for F5 |

Every warmup opens and hashes exactly one regular local file for each role:
`runtime_image`, `model_weights`, `vocabulary`, `vocoder_weights`, and
`vocoder_configuration`. Role, unique ID, immutable 40-hex revision, SHA-256,
byte count, and license ID are all required. Size, stable file identity, and
digest are all checked while copying into a private verified snapshot. Engine
load receives only those snapshot paths, so replacing an operator-owned path
after verification cannot substitute different bytes. Snapshot files are private
read-only copies, not a claim to resist a malicious same-user/native process.
Snapshots are deleted
after load. F5 uses a local `.safetensors` checkpoint and requires
`pytorch_model.bin` plus `config.yaml` in one Vocos directory.

For production, the `runtime_image` artifact is itself a canonical
`martlet_f5_runtime_inventory` document. Its pinned bytes bind the F5 source
revision, the exact Python executable bytes, and every regular file under the
installed `f5_tts`, `torch`, `torchaudio`, and `vocos` module trees. Package
versions, file sizes/SHA-256 values, module origins, and the resolved import
roots are checked without executing those packages. Symlinks, bytecode
caches, writable package trees/files, missing/extra files, editable/shadow
imports, and origin drift fail before the first heavyweight import. The
production runtime must therefore expose these packages through an immutable
read-only image layer to the unprivileged worker identity.
Replaceable writable ancestor directories are rejected too. Runtime imports
still rely on the provisioned immutable filesystem and trusted import machinery;
inventory verification is not a sandbox or complete transitive import audit.

The inventory has exact top-level fields `format_version` (1), `kind`,
`f5_source_revision`, `python_executable` (`bytes`, `sha256`), and `packages`.
`packages` is sorted by distribution and contains exactly `f5-tts`/`f5_tts`,
`torch`/`torch`, `torchaudio`/`torchaudio`, and `vocos`/`vocos`; each entry has
`distribution`, `module`, exact `version`, and a complete sorted file list of
canonical module-relative `path`, `bytes`, and `sha256`. This inventory is an
offline provisioning input, not something the worker generates, downloads,
or repairs.

[`runtime-pins.v1.json`](martlet_f5_worker/runtime-pins.v1.json) is the exact direct import lock:
Python 3.12.10, F5-TTS 1.1.22 at source revision
`9c614e9657089213efc6a7421b30630be138a3f5`, Torch/torchaudio
2.6.0+cu124, CUDA 12.4, and Vocos 0.1.0. Package metadata must match before
any heavyweight import. The engine then forces Hugging Face, Transformers,
and Datasets offline, passes all model/vocabulary/vocoder paths explicitly,
and imports only the pinned F5/Torch/torchaudio/Vocos entrypoints. It calls
the lower-level F5 batch iterator with the supplied nonblank transcript; it
never calls F5 preprocessing, `transcribe`, a Whisper pipeline, high-level
file export, or a download branch. Third-party stdout/stderr is discarded
during imports, model load, and synthesis so upstream path-printing cannot
corrupt canonical stdio or disclose private artifact paths.
The standalone entrypoint additionally separates the protocol descriptor and
discards native stdout/stderr diagnostics. Its process-wide Python audit hook
denies socket and child-process operations; even swallowed denials latch failure
and require restart, never restoring `ready`. Offline flags alone are not a
network boundary. The hook is defense in depth, not containment of arbitrary
native code; actual offline/native behavior remains unqualified.

This direct pin lock is **not** a complete, hash-locked Ubuntu wheel/image
closure. Do not install from it or infer compatibility. A selected immutable
runtime image, complete transitive/native inventory, platform wheel hashes,
and locally verified payload hashes are deferred qualification inputs. The
worker intentionally fails `model_not_ready` or `identity_mismatch` when
those pinned packages/artifacts are absent or drift.
The artifact role name `runtime_image` here binds an inventory **file**, not
an OCI manifest/image digest or source/build attestation. Current
[F5 GHCR source/build gaps](../../docs/F5_VOICE.md#reuse-and-installation-boundary)
and complete dependency closure remain unresolved. Candidate direct pins are
preserved as unqualified inputs, not silently upgraded or promoted to an
installation recipe. `live_worker` identifies the requested engine type;
it is not a qualification or rights claim.

## Deterministic fake and tests

`fake` is **FIXTURE - NOT AI**. It imports no F5, Vocos, Torch, torchaudio,
CUDA, model, network, or audio-device package. It still requires five exact
fixture artifacts, reference digest binding, warmup, one-job admission, and
the real framing/state machine.

Run the standard-library-only suite:

```powershell
$env:PYTHONDONTWRITEBYTECODE = '1'
$env:PYTHONPATH = 'workers\f5'
python -W error::ResourceWarning -m unittest discover -s workers\f5\tests -v
```

The suite creates only local temporary fixture bytes. It covers canonical
schema/bounds, malformed and truncated input, all identity gates before load,
same-size artifact drift, changed audio/transcript revisions, readiness,
single-job admission, warmup/job exclusion, blocking warmup deadlines,
verified-snapshot TOCTOU resistance, import-shadow rejection, third-party
stdout isolation, cancel/deadline late discard, partial contiguous PCM frames,
engine fault/OOM mapping, managed start/failure sequencing, cache invalidation,
buffer cleanup, no heavyweight fake imports, and a real fake-worker
subprocess. It does not execute F5/CUDA, open a network/audio device, or
download a model.

## Rights and deferred gates

No F5, Torch, Vocos, model, vocabulary, voice, or audio payload is included.
See [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt). The F5 v1 weights
identified by the host inventory are declared CC-BY-NC-4.0 and remain
unreviewed. F5 source MIT terms do not license separate weights, a reference
voice, a runtime image, or transitive packages. A user-supplied transcript or
voice-rights assertion is not legal clearance or the subject's consent.

The fake suite does not pass H04, AC-19, H06, or G3. H01 native execution,
actual Ubuntu/container/GPU/model, rights and performance qualification are
**NOT RUN**. Still deferred and **NOT
RUN** are: complete artifact/runtime hashes and licenses; approved model and
reference-voice rights; an immutable Ubuntu/CUDA image; real GPU/driver fit,
VRAM/RAM/disk, warm/cold latency, throughput, concurrency and OOM recovery;
whether compute cancellation is possible; actual F5 intelligibility, voice
difference, clipping, prosody, inter-sentence gaps and listening review; and
real playback/device/barge-in qualification. No production/GPU/audio quality
claim may be derived from this worker or its fake output.
