# Martlet bounded VRM1 adapter

An isolated TypeScript/WebGL slice, **not support for every VRM1 asset** and not yet
the Desktop audio-animation path. Runtime packages are pinned to `three@0.180.0`
and the official MIT `@pixiv/three-vrm@3.5.5`. No CDN, remote model fetch, microphone,
audio device, inference provider, hidden animation loop, or legal-acceptance flow
is activated by the module. The host selects Audio2Face first/preferred, or an
explicit supported alternative; this renderer does not start a provider.

## Local build and preview

Requires Node 20.11+ and npm. From this directory:

```powershell
npm ci --ignore-scripts --no-audit --no-fund
node .\harness\tasks.mjs test
node .\harness\tasks.mjs dev
```

Equivalent npm scripts are `npm test`, `npm run build`, `npm run bundle`, and
`npm run dev`. The explicit Node form also works when the machine's npm `cmd`
script environment cannot resolve PATH executables. No global installation is
needed. The compiler uses bundler module resolution for upstream three-vrm's
extensionless declaration imports.

Emotes (docs/AVATARS.md "Emotes and motions"): `setAction(name, on)` fades an
authored emotion or custom expression in or out, one at a time (mouth, blink and
gaze presets are refused), and `playGesture(name)` plays one of Martlet's own
gestures the model has the humanoid bones for (`gestures`: `nod`, `shake`,
`tilt`, `bow`, `sway`, `wave`, `shrug`, `bounce`, and the voice emotes `laugh`,
`chuckle`, `sigh`, `gasp`, `cough`, `clear_throat`, `groan`, `sniff`, `shush`,
`inhale`, `exhale`, `mumble`, `hum`, `sneeze`, `whistle`, `happy`, `sarcastic`,
`angry`, `fear`, `crying`, `whispering`, `dramatic`, then `wink`, `pout`, `shy`,
`giggle`, `flinch`, `lean_in`, `look_away`, `think`, `eye_roll`, `drowsy`, which
also use the model's preset expressions when it has them) on the idle pose.
`playGesture(name, true)` holds `pout`, `shy`, `look_away` or `drowsy` until
`endGesture(name)`; `gestureState` says which plays once and which is held. VRM files
carry no motions.

`dev` bundles all JavaScript locally to ignored `public/app.js`, then serves only
three allowlisted static files at **http://127.0.0.1:4178**. It does not serve the
repository, upload files, or accept network/model URLs. Dependencies must already
be installed for fully offline use. The preview has a restrictive CSP and denies
microphone/camera access. Stop its foreground Node process to close the server.

Select a user-owned `.vrm`/`.glb`, independently review its model terms, and inspect
the reported capabilities. All optional controls start **omitted**. Pick an exact
authored expression to exercise the manual slider; opt into gaze/springs only
when reported usable. The synthetic-fixture button creates our own tiny triangle
and bone hierarchy. It is **importer evidence, not artist-rig or visual-quality
evidence**. The manual preview uses no playback clock or Audio2Face results.

## Exported API

Import from `src/index.ts` in a bundler, or compiled `dist/src/index.js` in Node.
`build` emits JavaScript/declarations, not a browser-ready dependency-free library;
bundle the entry with the host's existing bundler. The harness's `app.js` is a
standalone preview, not a Desktop bridge.

| API | Contract |
| --- | --- |
| `inspectVrm(buffer: ArrayBuffer)` | Synchronous, bounded preflight; returns immutable `VrmCapabilities`. Does not allocate model GPU resources. |
| `loadLocalVrm(buffer)` | Real official GLTF/VRM importer, returns `{vrm, capabilities}`; caller owns returned resources. Normally use the runtime for automatic cleanup. |
| `new VrmRuntime()` | Actual importer and controls, usable without WebGL for CPU-side importer tests. |
| `new VrmAvatarAdapter(canvas)` | Extends runtime with WebGL2 renderer, camera, lights. No owned RAF. |
| `await load(buffer)` | Copies/preflights supplied bytes before async import. Success replaces/disposes the previous model and clears all binding/configuration. Failure retains the current model. Concurrent loads reject; dispose cancels pending adoption. |
| `configure(selection, revision, inputMode = "composed")` | Requires model/mapping revisions in both exclusive input modes; validates selected aspects and ownership transactionally, then neutralizes controls and clears playback binding. |
| `reset(identity)` | Trusted explicit turn/start transition: neutralizes face, head, gaze and springs; binds full correlation/source/epoch/rate and restarts sequence. |
| `applyFrame(input, actualPlaybackSampleOffset)` | Adapter-local normalized coefficient route, applies explicit affine mapping. **Not** an AvatarFrame JSON parser. |
| `applyComposedParameters(input, actualPlaybackSampleOffset)` | Preferred shared-host route: validates current model/mapping revision and exact selected native target parameters; applies values directly, with **no second affine mapping**. |
| `setPose({gaze?, head?})` | Trusted local procedural controls, not shared v1/A2F pose data. Gaze is world-space XYZ within +/-100m; head is normalized XYZW quaternion. |
| `update(deltaSeconds)` | Host-driven step, finite 0..0.1 seconds. Humanoid, gaze, expression overrides, spring and material updates, then draw for the renderer. Host must handle suspension explicitly rather than send huge deltas. |
| `resize(width, height)` | Renderer only: integer 1..4096 each; pixel ratio fixed at 1 to bound the drawing buffer. |
| `stop()` / `dispose()` | Stop neutralizes/clears playback binding and freezes selected procedural motion until reset. Dispose releases geometry/material/texture/bitmap/skeleton resources and WebGL context; idempotent and terminal. |

`PlaybackIdentity` is `{sessionId,turnId,requestId,sourceId,epoch,sampleRate}`.
Correlation IDs are nonempty UUIDs; source is a bounded identifier; epoch and
sequence are integers 0..2147483647; original PCM sample rate is one of
16000/24000/44100/48000. Frame offsets are nonnegative JavaScript safe integers.

`Selection` is:

```typescript
{
  faceSource: string, // preserve the actual selected source_id
  faceMode: "authored-explicit" | "reduced-vowel-jaw-only",
  mappings: [{
    channel: string,
    expression: string, // exact inspected authored expression, no guessed alias
    aspect: "mouth" | "expression" | "blink",
    minimum: number, maximum: number // 0 <= minimum < maximum <= 1
  }],
  gaze: boolean, head: boolean, secondaryMotion: boolean
}
```

The coefficient route requires explicit `inputMode: "coefficients"` (third
configure argument). Its input is
`{identity,sequence,sampleOffset,modelRevision,mappingRevision,coefficients}`. It must
contain exactly the selected source channels, finite 0..1; no implicit missing
values or ignored unknown channels. One channel may explicitly drive multiple
non-overlapping targets. Distinct expressions may not write the same morph or
material property; gaze owns its directional expressions. Blink is an internal
expression subcategory, not a new shared compatibility aspect.

For the shared compositor, call `configure(selection, {modelRevision,
mappingRevision})` (default `"composed"` mode) with host-bound, nonempty bounded revision identifiers. Pass
`{identity,sequence,sampleOffset,modelRevision,mappingRevision,parameters}` to
`applyComposedParameters`. `parameters` contains exactly the selected authored
expression target names and their native **0..1** values. Shared composition has
already performed source mapping; the direct sink never applies mapping again.
Revisions are trusted host bindings, not a substitute for the host's asset hash
verification. The single-source A2F route preserves its original full identity,
source ID and sequence; do not invent a universal composed source.

The two entrypoints are mutually exclusive for a configuration: coefficient input
cannot overwrite a composed session, or vice versa. Both enforce the current
model/mapping revision, including after a same-identity reset. Changing route
requires trusted reconfiguration and reset.

Both routes use a monotonic sequence and sample clock gate. Wrong identity,
stale sequence, wrong revision, future frame, frame older than 250ms, non-finite/
out-of-range values, and missing/extra controls **throw before any active state
change**. A late turn A never resets or overwrites current turn B. Future frames
are rejected, not buffered: the host owns a bounded, validated queue and submits
only due frames. `actualPlaybackSampleOffset` must come from actual device-rendered
playback progress, **not submitted PCM, a software queue cursor, arrival time, or
the render-loop clock**. Device progress and shared wire validation remain host
responsibilities. No real audio-sync qualification is claimed.

## Facial and aspect compatibility

Inspection checks actual nonzero morph bindings and standardized humanoid node
assignments; an extension's mere presence does not imply controls. Empty or
material-only expressions are not qualified as facial controls. Presets are
optional. `aa/ih/ou/ee/oh` are vowel presets, **not detailed ARKit channels**.
Custom names never establish semantic meaning. `facialDetail` is `absent`,
`preset-only`, or `explicit-mapping-required`; none certifies full ARKit.

For example, a reviewed mapping of A2F `jawOpen` to an authored VRM `aa` is a
**reduced jaw/vowel-only approximation** and must use `reduced-vowel-jaw-only`.
It is not the vowel-specific result of a phoneme analyzer and cannot preserve
cheeks, brows, tongue or detailed lips. Detailed support requires the artist to
author the required expressions and explicitly map each named normalized
coefficient after inspection. Missing coefficients/targets require an actionable
host compatibility decision: author/re-export, select a supported source, or
explicitly omit that aspect. Do not bypass missing rig controls.

The official expression manager applies `overrideMouth`, `overrideBlink`, and
`overrideLookAt`. Custom mouth/blink mappings are included in those groups.
Bone gaze also honors look-at override amounts. Automatic blinking or additional
mouth controllers are not started. Gaze needs both usable eye bones or all four
directional morph expressions plus lookAt. Head is a local normalized humanoid
rotation. Secondary physics is opt-in. **Body/VRMA playback, general FBX/GLB
rigs and VRM0 migration are not implemented.**

## Intentionally conservative asset subset

Only GLB2 with exact byte length, aligned bounded JSON and at most one embedded
BIN chunk, glTF2 asset version, exact `VRMC_vrm` version1.0, required humanoid
roles and standard ancestry, a single scene, and VRM1 metadata are accepted.
Every extension object must be consistently declared in `extensionsUsed`;
required/used declarations cannot refer to absent extensions. Advertised spring
support is additionally checked against imported nonzero-length runtime joints.
The `licenseUrl` format check is **not consent** or a claim of model usage rights.

Allowed extensions:
`VRMC_vrm`, `VRMC_materials_mtoon`, `VRMC_springBone`,
`KHR_materials_unlit`, `KHR_texture_transform`,
`KHR_materials_emissive_strength`.
The installed official package includes other functionality, but the preflight
does not expose it. `VRMC_node_constraint`, Draco, meshopt, extra spring collider
extensions, unknown plugins and all other extensions reject explicitly.

Supported geometry is triangle primitives with known attributes, POSITION/NORMAL
morphs (tangent morphs reject), nonsparse
embedded accessors, positive TRS node transforms (no matrix nodes), unique
acyclic node ownership, and bounded skins/morphs. Embedded animations reject:
VRMA requires separate future integration. Reachable spring joints must be
unique and separate from standardized humanoid controls. This intentionally
rejects some otherwise compliant rigs; re-export to this subset or wait for
qualified support, never silently discard unsupported controls.

All URI fields (including data/file/network URIs) and prototype keys reject
recursively. No model scripts or dynamic plugins load. Only embedded PNG
textures are accepted; JPEG/WebP/KTX require re-export. PNG dimensions are
checked before browser decode. A per-parser embedded image decoder uses
`createImageBitmap` directly on the validated bytes: no object URL or fetch is
created, and decoded bitmaps are owned across success, failure, reload and late
completion/disposal. Browser `createImageBitmap` support is required for textured
models. Decode failures surface rather than render success with a silently missing
texture. The bounded parser is defense-in-depth,
**not a full glTF schema validator or an OS/browser security sandbox**.

Main hard limits are in `LIMITS`: 32MiB input, 2MiB JSON, 120,000 JSON entries,
48 JSON depth, 512 nodes/128 hierarchy depth, 2048 accessors, 500,000 accessor
elements, 64MiB decoded/expanded geometry, 128 meshes/16 primitives each,
64MiB aggregate buffer-view copies (overlapping views count separately),
64 morph targets per primitive, 128 materials, 32 images/64 textures,
4096px texture dimensions, and 16Mi pixels. Conservative repeated-mesh and
material/texture-instance multipliers also count against budgets, so satisfying
each individual cap alone is not sufficient. There are at most 128 expressions
and 128 selected mappings. Model bounds must be finite and at most100m.

## Evidence and remaining integration

Tests execute the real pinned GLTFLoader/VRMLoaderPlugin, VRM expression/morph
application and physics lifecycle against a self-authored synthetic triangle.
They cover malformed headers/resources, absent controls, subset rejections,
ownership/override behavior, finite/range constraints, stale-turn inertness,
playback gating, direct-target revision binding, stop/dispose/reload, and pending
load cancellation. These are not mocked successful import results.
PNG resource-lifetime tests run the production importer with a simulated browser
decoder; they verify ownership/error paths, not real PNG decode or visual quality.

Local TypeScript compilation, importer/control tests and offline bundle creation
are runnable without GPU. The localhost harness was served and its static route
allowlist checked. **Actual WebGL rendering, user/artist VRM visual quality,
embedded PNG browser decode, device-specific GPU performance, real Audio2Face
and device-rendered audio synchronization remain NOT QUALIFIED in this slice.**
A browser preview can be opened locally, but that alone is not visual evidence.

Desktop host wiring, strict shared AvatarFrame v1 parsing, model-to-shared
capability translation, provider licensing/activation, model terms presentation,
source selection and playback scheduling belong to the subsequent integration.
No global settings/schema/solution changes, remote workflows, pushes or releases
are included here.

References: [official three-vrm runtime](https://github.com/pixiv/three-vrm),
[official VRM1 specification](https://github.com/vrm-c/vrm-specification/tree/master/specification/VRMC_vrm-1.0),
[VRM expressions](https://github.com/vrm-c/vrm-specification/blob/master/specification/VRMC_vrm-1.0/expressions.md).
