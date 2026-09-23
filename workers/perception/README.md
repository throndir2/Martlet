# Martlet perception worker

Reused from original commit
`dff65aeb402e719275ab54ec9e66d7ec247bea05` (22 files), with focused contract,
ownership, cleanup and deadline regressions against current P01/P02.
The original `.gitattributes`, direct runtime pins and canonical fixture bytes
are unchanged.

This directory contains the isolated Python process for
`martlet.perception.worker` protocol version `1.0`. It implements the existing
managed OCR and visual-question-answering roles and the existing H03b
perception observation/event shapes. It does not add a detector, capture a
screen, open a listener, expose an OpenAI-compatible API, choose a model,
download an artifact, follow a redirect, or provide a generic Transformers
loader.

The package is outside the root solution, Desktop/Core settings, managed
Perception, gateway, packaging, and deployment graphs. Nothing starts it
automatically. No real OCR, VLM, model, GPU, network, capture API, or image
corpus was used to implement or test it.

## Private stdio contract

Stdin and stdout carry one UTF-8 JSON object followed by LF. Every object must
use this canonical encoding:

```python
json.dumps(value, ensure_ascii=False, allow_nan=False,
           separators=(",", ":"), sort_keys=True).encode("utf-8")
```

Duplicate, unknown, or missing properties; noncanonical whitespace/escapes;
invalid UTF-8; CRLF framing; non-finite numbers; lines over 5,700,000 bytes;
nesting deeper than 64 containers; and EOF inside a record fail closed.
Before any engine is constructed, the
transport duplicates its private protocol descriptor and permanently seals
process descriptors 1 and 2 to the null device; scoped Python and native
descriptor redirection adds defense in depth around model import, load,
inference, and cleanup. The intentional `--print-build-revision`
administrative command is the only non-NDJSON stdout mode.

The process accepts these exact message types:

| Type | Purpose |
| --- | --- |
| `status` | Read the bounded lifecycle state and exact configured worker identity |
| `warmup` | Verify the expected identity, runtime inventory, artifacts, budget, and model before readiness |
| `offer_frame` | Transfer one bounded PNG into the worker-owned ephemeral store without a path or URL |
| `execute` | Execute one exact managed `PerceptionWorkerRequest` shape |
| `cancel` | Acknowledge local discard for one exact IDs/action/epoch tuple |

The canonical [`status.v1.json`](fixtures/status.v1.json) and
[`h03b-ocr-observation.v1.json`](fixtures/h03b-ocr-observation.v1.json)
fixtures are transport/shape examples, not model evidence.

`execute` contains the managed v1 IDs, action, destination, exact expected
worker, selected-window frame/source/provenance, task, epoch, UTC deadline,
maximum frame age, and every strict policy flag. OCR has a null question. VLM
has one question bounded to 512 UTF-16 code units and 1 KiB UTF-8. The policy
must explicitly forbid screen-wide fallback, artifact download, model or
provider fallback, redirects, persistent images, arbitrary endpoints, and
arbitrary paths.

Inline frames are canonical base64 PNG bytes. Ephemeral references are opaque
identifier-only handles previously transferred through `offer_frame`; they
are one-use, expiry/digest/dimension bound, held in a wipeable byte buffer,
and removed and zeroed by one bounded idle expiry watchdog without requiring another
request. They never resolve a URL or filesystem path. Both forms are limited
to 4 MiB, 4096 x 4096, and 8,294,400 pixels. PNG parsing verifies the signature,
canonical `IHDR`/contiguous `IDAT`/`IEND` structure, CRCs, one exactly consumed
strictly output-bounded zlib stream, exact scanline size, valid filters, 8-bit
RGB/RGBA samples, and no unknown chunks or trailing data. Managed round-trip
UTC timestamps retain all seven fractional digits rather than truncating the
final 100 ns tick.

The explicit inner route epoch range is **1 through 2,147,483,646**, for
both execute and cancel. The request epoch must equal the selected worker
frame's epoch. This is narrower than P02's positive `long` contract and is
not P01's separate capture-session epoch (which can start at zero).
A process/route adapter must validate this range before dispatch and report
a contract mismatch; it must never truncate or wrap an out-of-range epoch.
Worker concurrency is exactly one. Worker IDs obey P02's 64-character limit.
OCR region text **and detected language** count toward the selected UTF-8
output budget; the original tighter canonical observation byte bound also
remains enforced.

The terminal `response` is the existing managed `PerceptionWorkerResponse`
shape: exact IDs/action/epoch/worker, one `completed`, `canceled`, or `failed`
outcome, mutually consistent observation/error/cancellation fields, and
truthful `worker_may_continue`. Completed OCR contains at most 128 contiguous
regions with normalized boxes and explicit confidence. Completed VLM contains
one bounded answer, a required uncertainty statement, and explicit confidence.
Every observation repeats the exact frame, source, permission, destination,
host, worker, evidence, role, and model provenance and has a bounded absolute
expiry.

`h03b_route_events` validates ownership, identity, terminal-field consistency
and observation provenance before mapping one response to the legacy inner
H03b sequence:

- `started`, `observation`, `completed` for success;
- `started`, `canceled` for cancellation; or
- `started`, `failed` with only an allowed `worker.failed`,
  `context.unavailable`, or `job.deadline` code.

The observation is canonical JSON carried as `application/json` base64, with
the exact OCR/VLM route ID and the same artifact-identity digest algorithm as
`GatewayInferenceRoute.Perception`. Trace ownership remains with the gateway.
This worker does not open or authenticate an HTTP route. Canonical Gateway
authentication protocol **2** and its permanent device lifetime are separate
from this inner protocol **1.0**; these fixtures are not authenticated outer
Gateway events. There is no production Gateway-to-process adapter here.
The caller must enforce fresh per-action capture, disclosure and vision
permission before dispatch; the worker's strict policy flags and echoed
permission revision are not authorization.

## Lifecycle, deadlines, and resources

Startup emits `cold`. One explicit warmup moves through:

`verifying_runtime` -> `verifying_artifacts` -> `loading_model` -> `warming`
-> `ready`

Warmup has a maximum five-minute deadline. Its independent watchdog publishes
`failed` even if a third-party load call blocks; late completion is closed and
cannot publish readiness. Verified snapshots are cleaned and the watchdog is
retired before `ready` can be published or a job admitted. Warmup and
inference exclude each other.

Exactly one job is admitted. A second job receives
`resource_exhausted` immediately and is never queued. Declared CPU/GPU
requirements must fit the configured hard budget before warmup. The job
deadline has an independent watchdog. Cancel and deadline publish one
terminal local-discard result, suppress all late engine output, and leave the
worker `busy` until the engine call really returns. Both production adapters
declare `discard_only`; the worker never claims that native/GPU compute
stopped. EOF performs the same bounded cancellation/cleanup path. Shutdown remains
`stopping` while a load/inference call retains ownership. Its returning thread
closes the engine and releases its frame/snapshot before reporting `stopped`;
it never closes an engine concurrently with its compute. A transport that has
already reached EOF closes its private descriptor and discards late cleanup
notifications. Killing stuck native compute remains a supervisor responsibility.

Job admission, completion and idle frame expiry use UTC combined with elapsed
monotonic time and a UTC high-water mark. Clock rollback cannot extend an
already admitted lifetime. Expiry equality is stale, including the seventh
timestamp tick; watchdog scheduling is not relied upon for final admission
or readiness checks. Consumed offers do not accumulate sleeping threads.
Output is synchronously backpressured, not queued: there is no event queue
whose capacity could evict a terminal response, and terminal ownership is
claimed before writing it. A blocked OS pipe still needs external process
supervision; bounded reader/writer integration is not qualified here.

Model absence is `model_not_ready`; memory exhaustion is
`resource_exhausted`; bad PNG is `invalid_image`; an expired job is
`deadline_exceeded`; malformed/unknown/partial engine output is
`internal_failure`. Raw Python memory exhaustion maps to
`resource_exhausted`. Model, adapter, unknown-output, and OOM failures
quarantine readiness, close the engine, and require another successful
warmup. Errors contain a bounded safe summary and stable remedy code, never
image/question/output bytes, paths, package exceptions, or model internals.
Engine cleanup failure latches restart-required rather than permitting another
warmup. A process-wide Python audit hook denies socket and child-process
operations before engine imports, and a denial remains latched even when
third-party code catches it or issues it from a background thread. Status
and admission recheck that latch. This is not a native-code sandbox or proof
of network isolation.

## Exact identity and immutable inputs

Run with one explicit absolute local canonical config:

```powershell
$env:PYTHONPATH = 'workers\perception'
python -m martlet_perception_worker --print-build-revision
python -m martlet_perception_worker --config C:\private\martlet-perception\worker-config.json
```

The config has format `martlet_perception_worker_config` version 1 and binds:

- one exact `fake`, `tesserocr_ocr`, or `transformers_llava_next` engine;
- one host ID and exact full managed worker/runtime/model/artifact identity;
- an absolute link-free local artifact root plus canonical relative files;
- one explicit device (`cpu`, `cuda:0` through `cuda:15`, or null for fake);
  and
- hard CPU-unit and GPU-MiB budgets.

The production config, artifact root, and source artifacts must be read-only
to the worker. Every warmup opens each regular artifact without following a
link, checks stable file identity, byte count, and SHA-256 while copying it to
a private verified snapshot, and gives only snapshots to the engine.
Replacement after verification therefore cannot substitute different model
bytes. Snapshots are deleted after load.

The `runtime_image` artifact is itself a canonical
`martlet_perception_runtime_inventory`. It binds the selected engine, exact
Python executable bytes/SHA-256, and every regular file under every direct
import tree, including `martlet_perception_worker` itself. Packages and files
must be unique and sorted. Missing/extra/writable/linked files, bytecode
caches, shadow imports, package-version drift, origin drift, and post-import
digest drift fail before model use. Production is pinned to Python 3.12.10 on
Ubuntu 24.04 and must run as an unprivileged identity against an immutable
image layer.

These source/package inventory files are **not** OCI digests, build
attestations, or complete transitive/native dependency attestations.

[`runtime-pins.v1.json`](martlet_perception_worker/runtime-pins.v1.json) is
the direct runtime/tooling lock:

| Adapter | Exact supported identity | Direct pins |
| --- | --- | --- |
| OCR | `tesserocr-ocr` 1.0.0 / `tesseract-eng` | Pillow 11.1.0, tesserocr 2.8.0, Tesseract 5.5.0 |
| VLM | `transformers-llava-next` 1.0.0 / `llava-v1.6-mistral-7b` | Pillow 11.1.0, safetensors 0.5.3, Torch 2.6.0+cu124, Transformers 4.49.0, CUDA runtime 12.4 |
| Worker/tooling | `martlet-perception-worker` 1.0.0 | Python 3.12.10, setuptools 84.0.0, Ubuntu 24.04 |

This is deliberately not a universal model compatibility promise.

### OCR adapter

The only production OCR adapter imports the exact pinned `PIL.Image` and
`tesserocr` modules after inventory verification. It accepts exactly one local
`eng.traineddata` model plus a canonical
`martlet_tesserocr_model_config` selecting English, OEM 1, PSM 6, and
Tesseract 5.5.0. The verified tessdata snapshot is sealed in
`TESSDATA_PREFIX` before the first `tesserocr` import, preventing its native
module initialization from consulting a default model path. It decodes the
already validated PNG in memory, uses no subprocess or temporary image file,
returns Tesseract text-line boxes with explicitly **uncalibrated** confidence,
then calls native `Clear()` and closes both Pillow images on every path.
Cleanup failure quarantines the engine. There is no object-detector role or
fallback language/model.
Excess native text lines fail rather than silently truncating to 128 regions;
non-finite or out-of-range confidence also fails.

### VLM adapter

The only production VLM adapter imports the exact pinned Torch,
Transformers, safetensors, and Pillow modules after inventory verification.
It supports only the pinned LLaVA-NeXT Mistral/CLIP architecture and requires
one local safetensors base-weight file, canonical model config, fast-tokenizer
JSON, and separate exact projector safetensors artifact. Architecture,
missing/unexpected state keys, image token, CUDA runtime, and explicit CUDA
device are checked before readiness. Generation is deterministic
`do_sample=False`, limited to 128 new tokens, and reports confidence as
unavailable with a required uncalibrated uncertainty statement.

Hugging Face, Transformers, and Datasets offline modes are forced before any
production import. All model/config/tokenizer/projector paths are verified
local snapshots; no `from_pretrained` download or URL path is used.

Worker-owned PNG buffers, bounded inflated scanlines and open image streams
are zeroed on release; Pillow/native images are explicitly closed. Parsed
stdio records are dropped before waiting for the next command. Python's
immutable JSON/base64/zlib intermediates and native/GPU tensor allocations
cannot be certified as securely erased by these tests; native privacy and
memory-remanence qualification remain open.

## Deterministic fake and tests

`fake` is **FIXTURE - NOT AI**. It imports no Pillow, tesserocr, Torch,
Transformers, safetensors, CUDA, model, network, capture, or GPU package. It
still requires exact role-specific identities and artifact digests, warmup,
one-job admission, PNG validation, deadlines, cleanup, and the real response
and H03b mapping.

Run the standard-library-only suite:

```powershell
$env:PYTHONDONTWRITEBYTECODE = '1'
$env:PYTHONPATH = 'workers\perception'
python -W error::ResourceWarning -m unittest discover -s workers\perception\tests -v
```

The suite uses generated inert PNGs and temporary fixture bytes only. It
covers canonical schema/framing, UTF-8/UTF-16 bounds, PNG/reference validation,
exact identity and role mismatch, artifact snapshot TOCTOU, complete runtime
tree/origin/read-only checks, resource budgets, one warmup/readiness state
machine, cleanup/readiness and warmup/job races, one-job admission,
cancel/deadline late discard, partial/unknown output, raw/OOM memory mapping,
model-health quarantine, H03b event/provenance/seventh-tick shape, bounded PNG
decompression, idle ephemeral/inline/native OCR cleanup, host loss/EOF,
Python/native stdout isolation, controlled production adapter boundary doubles,
no-heavy-import fake behavior, and a real fake-worker subprocess.
No dependencies need installing for this suite. Tests use only the active
interpreter, inert authored PNG/config/artifact bytes, controlled heavy-module
doubles and individually owned test child processes. Source dependency pins
do not authorize installing or running a production runtime.

## Rights and deferred gates

No Python wheel, native library, runtime image, OCR data, tokenizer, projector,
or model weight is included. See
[`THIRD-PARTY-NOTICES.txt`](THIRD-PARTY-NOTICES.txt). Candidate project license
statements are not approval of an acquired payload or legal advice.

This direct lock intentionally defers the complete transitive dependency
closure; platform wheel and native-library hashes; CUDA/driver/base-image
hashes; exact acquired artifact revisions/digests; and complete source,
model, tokenizer, projector, trained-data, runtime-image, and transitive
license notices. Nothing here authorizes capture, upload, model use, or
distribution.

The fake suite does not pass P01, P02, P04, H06, AC-14, AC-15, AC-20, or G4.
Still **NOT RUN** are production gateway/process supervision; physical host-2
isolation; approved OCR/VLM/model rights; real CUDA/GPU/driver fit;
VRAM/RAM/CPU/disk budgets; cold/warm latency; sustained throughput; combined
LLM/F5/game load; OOM/reboot/host-loss recovery; real compute cancellation;
OCR accuracy; VLM usefulness/hallucination/uncertainty calibration;
consented privacy review; selected-window capture/upload behavior; and
accessibility/human acceptance.
