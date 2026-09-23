# Selected-window perception foundations (P01/P02)

**Experimental isolated software foundation; production Windows capture is
unavailable.** `Martlet.Perception` defines selected-window discovery, capture,
preview and disclosure contracts with strict in-memory ownership and lifecycle
limits. It is not referenced by Desktop, Core settings, the root solution,
packaging, a gateway or a provider. It cannot enumerate a real source, acquire
a screenshot, upload, persist, log, OCR or analyze an image.

The public `SelectedWindowCapture` constructor is inert. Its
`Availability` is ineligible, and public source enumeration fails with
`NativePrivacyUnqualified` **before** consuming authorization or entering a
native boundary. Only internal friend test/qualification composition can inject
`INativeWindowCaptureFactory`; ordinary tests use controlled authored pixels.
No real screenshot, app/game launch, Windows permission request or native
capture occurs in the automated suite.

This bounded port reuses the original P01 implementation from
`29b50b1b3638be490980a1a50367afc7b6a44c40` ("Add selected-window perception
foundation"), without importing its ancestry, later P02/Windows adapters,
Python workers or application integration. The original source-owned state
machine is retained with the consent and freshness corrections described below.

## Permission and identity boundary

Capture is OFF by default. Each stage has a distinct, one-use authorization:

1. `SourceEnumerationAuthorization` binds the current session and configuration
   revision plus the owner's current lifecycle/authorization revision for one
   selected-window enumeration lasting at most 30 seconds.
   The initial owner authorization revision is unique even when two owners
   share a session and configuration; enumeration consent cannot transfer.
   Construction and saved choices do not enumerate. Returned sources have a
   random opaque ID and enumeration revision; native handles are not exposed.
   Application/title labels exist only for a future selection surface and are
   omitted from `ToString`, failures and frame provenance.
2. `WindowCaptureAuthorization` binds the exact source object, opaque source
   identity/revision, exact destination object/identity/revision/kind,
   configuration revision, increasing epoch, mode, budgets, lifetime and clock.
   It also binds an owner lifecycle revision advanced by Stop, pause/lock,
   source/destination/configuration changes and each new enumeration, so a
   pending approval cannot be used after an idle-state change.
   Continuous mode needs an additional explicit continuous-capture assertion.
   Mismatch, expiry, cancellation or reuse is rejected before native open.
3. `WindowPreviewAuthorization` grants one local preview lease for one exact
   current frame. It is not remote disclosure permission.
4. `WindowDisclosureAuthorization` separately binds that exact frame and exact
   remote destination. It returns an in-process disposable byte lease; the
   library has no network or upload implementation. A local-only destination
   can never produce a disclosure lease.

   Permanent device pairing is not per-action capture or disclosure consent.
   Pairing cannot remove these one-use assertions, expiry, exact-source/frame
   binding or budgets. Any future inner Perception worker protocol 1 is separate
   from permanent Gateway authentication protocol 2. The inner worker contract
   is defined below; no Gateway authentication client is implemented here.

The authorization objects are trusted-caller assertions, not proof of human
consent, a Windows permission grant, provider permission or a security sandbox
against arbitrary code in the same process. Future UI composition must present
the source labels and `CaptureDestination.DisplayName`, obtain the described
choices, and keep the shared application effect owner until native release.
Validation, revocation, expiry, one-use consumption and final source publication
are serialized. A revocation either wins before admission/publication or follows
one already-linearized action; there is no validate-then-consume gap.

## Bounds and frame contract

The managed boundary accepts only tightly packed premultiplied BGRA32 frames.
The native adapter must scale/crop within the selected window before returning a
frame; the library never silently trims malformed or oversized bytes.

| Boundary | Default | Hard ceiling / behavior |
| --- | --- | --- |
| Selected sources | 64 | 128; malformed, duplicate or excess entries fail enumeration |
| Longest frame edge | 1,280 px | 1,280 px |
| Frame bytes | 1 MiB | 1 MiB; exact `width * height * 4` required |
| Capture rate | 0.2 frames/s | 1 frame/s; no pending frame queue |
| Session lifetime | 30 seconds | 5 minutes, explicitly bound into consent |
| Frame freshness | 750 ms | 5 seconds; stale frames are discarded, never observations |
| Native shutdown observation | 2 seconds | 2 seconds; timeout does not claim resource release |

One operation owns at most one raw managed frame. A new valid frame atomically
zeros and replaces the previous frame, invalidating any older preview/disclosure
lease. A lease serializes copying with replacement and exposes only
caller-supplied-span copying, so any caller-created copy has explicit caller
ownership. Every copy rechecks capture and lease authorization as well as frame
freshness, even when timers have not fired. Copying is serialized with both
authorization revocations and replacement; retaining a lease does not retain
permission after revocation or expiry. The operation zeros its owned pixels on
replacement, freshness or
permission expiry, Stop, pause, lock, source/destination/configuration change,
failure and disposal. Native frame buffers are disposed and zeroed after every
read, including malformed, oversized and stale frames.
An observed native failure invalidates retained pixels and is visible in the
snapshot before native cleanup begins; blocked cleanup keeps ownership pending,
not frame access enabled.

`WindowFrameProvenance` records the capture/session/epoch/frame sequence,
opaque source and enumeration revision, destination/revision/kind,
configuration revision, observed capture/freshness timestamps, dimensions,
format and byte count. It contains no pixels or window labels. UTC freshness
and the owner's monotonic acceptance age are both checked at access. UTC
rollback cannot extend an already-old frame's remaining age: its monotonic
budget is the freshness remaining at acceptance.
Timer delivery is not the sole authority: every native boundary, snapshot and frame
lease admission rechecks the original permission and clocks.

The rate gate belongs to the capture owner, not an individual operation.
Restarting one-shot or continuous operations cannot obtain another native frame
before the same interval elapses; premature starts fail without consuming
consent or creating a delayed queue.

The worker performs no desktop/monitor fallback and the injectable interface
has no desktop, region, process, injection or alternate-source parameter.
Source close, native identity change and access denial are explicit failures.
Pause, lock, caller cancellation, consent revocation, selected-source change,
destination revision change, configuration revision change and session timeout
stop capture, discard the latest frame and never restart or queue work.
Blocked/faulted native cancellation or cleanup retains/quarantines ownership;
starting a replacement backend is not recovery.

Enumeration is also explicit owned work on a dedicated worker. Its original
deadline, revocation, caller cancellation and manager pause/lock/Stop/disposal
cancel an internal token. Completion may report cancellation promptly, but a
blocked native enumerator keeps the enumeration slot quarantined until the
actual call and its cancellation callbacks retire. No labels are published
after invalidation, including caller cancellation after native retirement but
before source publication, and overlapping enumeration/replacement capture is
refused.

## Deliberate exclusions

This foundation adds no Desktop page, tray/background startup, Core setting,
saved permission, source allowlist persistence, Windows Graphics Capture code,
provider credential, HTTP client, upload, host-2 protocol, image encoder,
diagnostic content log, support-bundle image, file write, OCR, detector, VLM,
memory ingest or game integration. It does not capture the desktop, secure
desktop, monitor, system/Discord audio or remote participants and does not use
game injection, overlays, hooks or anti-cheat bypasses.

Default diagnostics and support bundles must continue to omit screenshots.
Window titles and pixels are private content, not metadata suitable for routine
logs. A future debug capture remains a separately timed/support-authorized
feature under the rules in
[Installation and support](INSTALLATION_SUPPORT.md#logs-support-bundle-and-retention).

## Managed-only validation

Use the pinned SDK without adding these isolated projects to `Martlet.slnx`:

```powershell
$artifacts = 'C:\OwnedSession\perception-artifacts'
dotnet restore tests\Martlet.Perception.Tests --locked-mode --artifacts-path $artifacts
dotnet build tests\Martlet.Perception.Tests --no-restore -c Release --artifacts-path $artifacts
dotnet test tests\Martlet.Perception.Tests --no-build --no-restore -c Release `
  --artifacts-path $artifacts --results-directory "$artifacts\results"
```

The tests exercise production contract/owner algorithms with a controlled
fake-native backend: OFF-by-default admission; malformed enumeration/frame
input; exact one-use source/destination/preview/disclosure binding; frame
dimension/byte/rate/lifetime limits; stale exclusion; latest-frame races;
source close/change; pause/lock/configuration/destination/cancellation/timeout;
idle pending-consent invalidation; blocked enumeration/read/cancellation/
cleanup ownership; cross-operation rate enforcement; post-completion
revocation; canary privacy; and managed/native buffer zeroing.

Historical source implementation evidence on 2026-09-21 used exact SDK 10.0.401 with
`CI=true`, a clean locked restore, Release build with zero warnings/errors and
all 65 tests passing. The authorization/lifecycle/enumeration/frame-ownership
race subset also passed ten fresh test-host repetitions. Outputs stayed in one
session-owned `C:` artifact tree. This was managed/fake-native evidence only.

The bounded reuse on 2026-09-23 used the same SDK with a locked restore and
Release build. All 81 synthetic tests passed, including ten fresh test-host
repetitions of the full suite. New regressions cover retained-lease consent and
expiry without timers, old-frame freshness across UTC rollback, foreign-owner
enumeration permission, cancellation after native retirement, and pixel
invalidation before blocked cleanup after source/privacy failure. The original
65-test baseline passed before fixes; 12 added consent/freshness cases and three
review-discovered cleanup cases failed before their respective corrections.
This evidence does not qualify native Windows capture or application wiring.

## Qualification still required

Before any production adapter or app integration, perform a separately
authorized review and real local qualification of the exact Windows capture
API/source-picker implementation and binaries. Evidence must show:

- construction and app/background startup acquire nothing; the selected window
  cannot broaden to a monitor/desktop or silently switch sources;
- OS privacy denial/loss, source close/recreation/title/process changes,
  minimize/occlusion/display-scale/HDR/format changes, session lock,
  sleep/resume and user Pause/Stop all fail closed with measured cleanup;
- the adapter supplies correctly scaled BGRA32 frames within the declared rate,
  dimension, byte, freshness, CPU/memory/GPU and game-frame-time budgets;
- supported Windows versions, window types, protected/unsupported content,
  full-screen games and anti-cheat-sensitive games behave without injection,
  hooks, overlays, bypasses or unsupported claims;
- real pixels/window labels never enter ordinary logs, crash material or
  support exports, using authorized canary privacy exercises;
- the visible source, capture indicator and exact local/host/cloud destination
  remain accurate through revision changes, and shared app ownership prevents
  races with Pause/lock/Exit;
- installer/package/signing and clean-machine behavior are qualified without
  enabling background startup or persisting permission.

Real transport authentication, upload cancellation, host-2 VLM/OCR usefulness and
resource qualification belong to P02/H03 and require fresh disclosure consent.
Real source/privacy/device/game evidence, useful labeled-task accuracy and
combined-load measurements remain **NOT RUN**. Therefore AC-15, P01 overall and
G4 are not passed by this foundation.

## P02 isolated worker and scheduler reuse

The additive worker foundation reuses canonical source
`9e117c10ac000a0392b8d7fd5e20c0d66ba6b822` ("Add isolated P02 perception
foundation") followed by
`4ef7eac9e82c08fe452fd59fdc32456b7b7f4f7a` ("Separate perception failure
domains"), without importing source ancestry. P01 implementation files and
all reviewed P01 consent/ownership fixes remain unchanged.

**This is not working AI or native capture.** The only supplied transport is
`DeterministicPerceptionWorkerTransport`, explicitly marked
`PerceptionEvidenceKind.SyntheticFixture`; it produces authored OCR/VLM-shaped
responses, not inference. Nothing is referenced by Desktop, Core settings,
Gateway, installation planning, the root solution, packaging or startup.
An optional installation plan must never enable capture, enumerate sources,
download/warm a model, or grant permissions automatically.

### Contract and consent boundary

`PerceptionProtocolVersion.Current` is inner worker **1.0**, contract
`martlet.perception.worker`, not permanent Gateway authentication protocol 2.
`PerceptionWorkerIdentity` pins OCR or visual-question-answering role, build
and runtime revisions, OS version, model/adapter identity, model SHA-256,
licensed artifact identities, declared limits, resource requirements and
cancellation capability. Identity matching includes every field and artifact;
there is no detector role, compatibility fallback or model substitution.
Artifact enumeration reads at most 17 items to reject more than 16.

`SelectedWindowFrame` binds an opaque selection, source and capture-permission
revision, frame ID, increasing worker capture epoch, timestamp and content.
Content is either an immutable copied PNG or an opaque ephemeral reference:

| Worker boundary | Hard ceiling / behavior |
| --- | --- |
| Encoded image | 4 MiB, checked before copying |
| Dimensions | 4,096 px per edge, 8,294,400 pixels total |
| PNG | 8-bit RGB/RGBA, non-interlaced, exact rows, CRC/zlib validation, no unknown/trailing chunks |
| Ephemeral reference | Opaque identifier, digest, size, dimensions and expiry; no URL/path |
| VLM question | 512 characters / 1,024 UTF-8 bytes |
| OCR result | 128 sequential bounded regions, immutable snapshot, normalized bounds |
| Output text | 16 KiB UTF-8 or the smaller pinned worker limit, including OCR language |
| Job / frame age | 15 seconds / 30 seconds maximum; callers select tighter bounds |
| Cancellation observation | 500 ms per cancellation/retirement boundary |
| Scheduler | One latest pending slot per role; at most two globally concurrent jobs |

These are independent worker ceilings, **not an increase to P01 capture
limits**. No conversion or bridge from a P01 lease is supplied. Future
composition must copy only through P01's per-copy consent/freshness boundary,
retain P01 provenance, and assign a strictly increasing worker frame epoch
for each dispatched frame. Worker `CaptureEpoch` is not P01's session epoch:
multiple P01 frame sequences in one capture session cannot reuse a worker
epoch. A scheduler is scoped to one explicitly enabled perception session;
epoch history is per role, not a cross-session/global sequence service.

`PerceptionJobIntent` binds one exact request, destination, worker, frame,
task/question, deadline and maximum age. Its default-No
`PerceptionVisionAuthorization` is expiring, one-use, exact-object-bound and
reserved before scheduler state mutation. It is a trusted-caller assertion,
not a human-consent mechanism or an OS permission. It does not authorize
capture or make an earlier disclosure legal. A new action always needs new
permission; caller cancellation/Stop must be propagated for ongoing work.

### Gateway and optional failure domains

`PerceptionGatewayClientAdapter` depends only on an injected
`IAuthenticatedPerceptionWorkerTransport`. Its binding asserts one destination,
host, scoped perception role and disabled redirects. This assertion is not
authentication evidence. No credential, network client, TLS/signing route,
Gateway auth integration, Python worker or host action is included.

The adapter validates exact request/action/epoch, complete worker identity,
destination/host/role, bounded output and exact observation provenance.
Observation expiry cannot exceed the original frame freshness or deadline.
Freshness uses both UTC and the monotonic time elapsed since admission, so
rollback cannot refund an already-old frame's remaining age. Dispatch rechecks
permission and freshness after constructing the byte snapshot; publication
rechecks cancellation, deadline and freshness after traversing worker output.
Malformed/mutating output cannot expose unvalidated entries or raw exceptions.
Content-bearing records redact their default string representations; explicit
content properties remain private application data and must not be logged.

`PerceptionFreshnessScheduler` enforces CPU, GPU-memory, global and exact-worker
concurrency budgets. Newer epochs supersede older slots; repeated/late epochs,
expired frames and non-fitting work are refused without a backlog. Optional
role priority can preempt another role. A pending replacement retains the
original running job's retirement barrier even if intermediate frames are
superseded. Final latest/cancellation checks and result publication share the
same scheduler lock.

Worker calls and cancellation callbacks do not execute on the scheduler caller
or under its state lock. Blocked/faulted execution or cancellation yields a
bounded local failure, never a released-compute claim. Discard-only,
request-abort, host loss, deadline or uncertain retirement keeps the numeric
resource reservation quarantined for the scheduler lifetime. A late response
cannot publish or silently reclaim that budget. Creating a replacement
scheduler is not proof of host cleanup.
The adapter's bounded task includes output snapshotting as well as dispatch;
even a blocked worker collection getter cannot hold its caller indefinitely.
A cooperative cancel acknowledgment cannot release local ownership until the
dispatch/validation task and cancellation callbacks have also retired.
Scheduler callback retirement and cancellation admission share an atomic
handshake: already-started callbacks must retire, and no new callback can start
after retirement is finalized but before resource release/publication.
Pre-launch deadline expiry releases unused capacity rather than quarantining it.

`PerceptionWorkerFailure`, `PerceptionWorkerFailureCatalog`,
`PerceptionWorkerException` and `PerceptionWorkerGuard` are separate from P01's
capture failure domain. Results expose typed stable remedies; arbitrary worker
error text is not a safe user notification. Perception has no voice lock,
queue, reference or callback. Future callers must continue voice when optional
vision is unavailable, with no fabricated context or automatic retry/fallback.

### Reuse evidence and remaining gates

The unmodified additive port passed all 166 tests (81 current P01 plus 85
canonical P02 cases). Ten new regression cases failed against that baseline
before corrections: oversized allocation, unbounded artifact enumeration,
content diagnostics, exact freshness expiry, direct/scheduled cancellation
during output validation, UTC rollback, expiry during validation, mutable OCR
counts and the three-frame retirement chain. Additional controlled blocked
executor/cancellation cases exercise optional failure isolation. All inputs
are authored synthetic frames/outputs; no real capture, labels, HWNDs,
clipboard, devices, models, host or network actions are used.

Independent actual-diff review identified three additional races: cancellation
could acknowledge retirement before a delayed dispatch, output collection
validation could outlive the adapter's cancellation bound, and expiry before
executor launch could quarantine unused resources. Each has a failing-before,
passing-after controlled regression. On 2026-09-23, SDK 10.0.401 locked
restore and Release build completed with zero warnings/errors; all **184**
tests (81 P01, 85 source P02, 18 reuse regressions) passed ten fresh test-host
repetitions. This is managed synthetic evidence only, not live qualification.

A subsequent independent final-fix review closed those three findings and
identified a callback-admission/retirement race. A controlled release barrier
reproduced it before correction. The scheduler now atomically closes callback
admission while capturing already-started cancellation work, preserving
quarantine until that work actually retires. The new regression increases the
suite to 185 cases. The 32 affected scheduler/reuse lifecycle tests passed after
this focused correction, with a Release build reporting zero warnings/errors;
the unchanged full-suite evidence above was not repeated.

Run the full direct Perception project with the locked Release commands above;
do not add it to the root graph or hosted CI. Held-out fixtures assert shapes,
limits and scheduling only, not OCR/VLM accuracy or human usefulness.

Native privacy/rights/runtime gates remain explicit and unqualified. Real
Windows capture and indicators, authenticated inference/TLS/cancellation,
model/code licenses and exact binaries, consented accuracy tasks, calibrated
uncertainty, concurrent voice/LLM/OCR/VLM load, GPU fit, game impact, retention,
support-export privacy and application/installation integration remain
**NOT RUN**. Source AC-15 and G4 are not passed. Installation AC18-20 are
unmodified; historical companion criteria belong to the mapped AC21-26 range,
not installation acceptance. No overall P01/P02 qualification is claimed.
