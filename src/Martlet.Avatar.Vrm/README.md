# Martlet bounded VRM adapter (VRM 1.0 and VRM 0.x)

An isolated TypeScript/WebGL slice, **not support for every VRM asset** and not yet
the Desktop audio-animation path. It loads VRM 1.0 models and VRM 0.x models (see
[VRM 0.x models](#vrm-0x-models)). Runtime packages are pinned to `three@0.180.0`
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
also use the model's preset expressions when it has them, then the held face
parts `eyes_up` (the eye bones) and `mouth_open` (the `oh` or `aa` mouth
expression)) on the idle pose. `playGesture(name, true)` holds `pout`, `shy`,
`look_away`, `drowsy`, `eyes_up`, `mouth_open` or the model's own `blush` until
`endGesture(name)`. Held gestures layer: holding one lets go only of the held
ones that move a part it moves too (`VRM_HOLD_PARTS`: eyes, mouth, cheeks,
brows, head). `gestureState` says which plays once and every one held (`held`
is a list). While the voice speaks (`setLipSync` or a composed frame, until
`VOICE_HOLD_SECONDS`, 1 s, after the last), it has the mouth: a held open mouth
gives way completely, and the emotes showing (`setAction` expressions and the
gestures' face) don't block or blend the mouth expressions (their
`overrideMouth` is `none` until the voice is done, then the model's own again).
`mouthReading` says how much the voice has the mouth, how far the held mouth and
the mouth expressions open it and how much the expressions block them. VRM files
carry no motions.

The renderer's `faceAnchor()` says where the face is now, for Martlet's drawings
over it (`src/runtime.ts` `faceGeometry`). It also gives each eye's iris and its
visible opening (`src/eyes.ts`), for drawings over the eyes. The eye bones place
each eye. The iris's middle rides its eye bone, so it follows a bone look-at. The
model's meshes give the iris's size and the opening when they can be found:
VRoid's `EyeIris` and `EyeWhite` materials, or names with iris, pupil, hitomi, 瞳
or 白目. They are measured at rest when the model loads. Each frame the opening's
vertices are projected skinned and with their morph targets, so a blink closes it.
`setEyeHint(hint)` uses eyes measured by vision for what the model can't give: a
sized iris that moves with an expression look-at and an outline that closes with
`blink`, `blinkLeft` or `blinkRight`. `eyesFrom` is `"bones"` when the bones and
meshes give both eyes, `"vision"` when the hint fills the rest, otherwise
`"estimate"`. An expression look-at doesn't move a mesh's iris: Martlet doesn't
drive the look-at outside a composed turn's gaze.

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
| `inspectVrm(buffer: ArrayBuffer)` | Synchronous, bounded preflight; returns immutable `VrmCapabilities`. Does not allocate model GPU resources. `format` is `"VRM1"` or `"VRM0"`, `specVersion` is the model's own (`"1.0"`, usually `"0.0"`), and `meta` is the model's metadata in one shape for both (`metaVersion` `"1"` or `"0"`; see [VRM 0.x models](#vrm-0x-models)). |
| `loadLocalVrm(buffer)` | Real official GLTF/VRM importer, returns `{vrm, capabilities}`; caller owns returned resources. Normally use the runtime for automatic cleanup. |
| `new VrmRuntime()` | Actual importer and controls, usable without WebGL for CPU-side importer tests. |
| `new VrmAvatarAdapter(canvas)` | Extends runtime with WebGL2 renderer, camera, lights. No owned RAF. |
| `await load(buffer)` | Copies/preflights supplied bytes before async import. Success replaces/disposes the previous model and clears all binding/configuration. Failure retains the current model. Concurrent loads reject; dispose cancels pending adoption. |
| `configure(selection, revision, inputMode = "composed")` | Requires model/mapping revisions in both exclusive input modes; validates selected aspects and ownership transactionally, then neutralizes controls and clears playback binding. |
| `reset(identity)` | Trusted explicit turn/start transition: neutralizes face, head, gaze and springs; binds full correlation/source/epoch/rate and restarts sequence. |
| `applyFrame(input, actualPlaybackSampleOffset)` | Adapter-local normalized coefficient route, applies explicit affine mapping. **Not** an AvatarFrame JSON parser. |
| `applyComposedParameters(input, actualPlaybackSampleOffset)` | Preferred shared-host route: validates current model/mapping revision and exact selected native target parameters; applies values directly, with **no second affine mapping**. |
| `setPose({gaze?, head?})` | Trusted local procedural controls, not shared v1/A2F pose data. Gaze is world-space XYZ within +/-100m; head is normalized XYZW quaternion. |
| `startIdle()` / `idleReading` | Host-chosen idle body: a relaxed standing pose instead of the VRM T-pose (mirrored left/right Euler angles on the normalized bones: `VRM_RELAXED_ARM` hangs the arms about 15 degrees out with soft elbows and wrists, `VRM_RELAXED_FINGERS` curls the fingers and thumb), breathing (`breathing(phase)`: a quicker breath in, slower out, a short rest; about `60 / BREATH_SECONDS` breaths a minute, shallower while the voice speaks) on the shoulders, chest, upper chest and neck, a slow spine sway, blinking, cursor-follow and loudness lip-sync. The first frame is breathed out with no sway. `idleReading` reports the breath, each arm's angle from straight down and elbow bend measured on the posed bones, the middle fingers' curl and the sway. |
| `springNodes` | The model's spring-bone chains (a tail, hair, a skirt), each its joints' nodes from the root to the tip. The importer makes a joint of each node and the next one in a `VRMC_springBone` spring, so a node that is no joint's child starts a chain. In a VRM 0.x model every node under a bone group's root is a joint whose child is the node's first child, so a chain runs from the root along first children to a tip, and each other branch starts a chain of its own. Empty before a model loads. |
| `springChains()` / `nodePoints()` | Renderer only, for touch zones: each spring-bone chain (at most 64, of at most 64 joints) named by its root joint's node, with each joint's node name and where it is now as fractions of the canvas, root first; and where each joint is now by its node's name. Touch zones follow a tail, wings or animal ears along these joints, and a hit test names the node it hit (`node`). Joints whose nodes have no name are left out. |
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
While the voice has the mouth, Martlet's emotes don't use `overrideMouth` (see
above); a composed turn's own mapped expressions keep it.
Bone gaze also honors look-at override amounts. Automatic blinking or additional
mouth controllers are not started. Gaze needs both usable eye bones or all four
directional morph expressions plus lookAt. Head is a local normalized humanoid
rotation. Secondary physics is opt-in. **Body/VRMA playback and general FBX/GLB
rigs are not implemented.**

## VRM 0.x models

Most models on VRoid Hub and in the Open Source Avatars registry are VRM 0.x
models. VRoid Studio and UniVRM export them with the glTF extension `VRM`. The
adapter loads them with the same bounded preflight as VRM 1.0 models. three-vrm
reads them and gives them VRM 1.0 names. A model must have `VRMC_vrm` (VRM 1.0)
or `VRM` (VRM 0.x). The preflight rejects a model with both.

What the preflight reads and checks in a VRM 0.x model:

- **Meta** (`VRM.meta`). The capabilities' `meta` has the same shape as for VRM
  1.0, with `metaVersion: "0"`. `title` becomes `name`, `author` becomes
  `authors`, and `licenseName` and `otherLicenseUrl` stay as they are.
  `allowedUserName` becomes `avatarPermission` (`OnlyAuthor` is `onlyAuthor`,
  `ExplicitlyLicensedPerson` is `onlySeparatelyLicensedPerson`, `Everyone` is
  `everyone`). `violentUssageName`, `sexualUssageName` and
  `commercialUssageName` (also spelled `...UsageName`) become
  `allowExcessivelyViolentUsage`, `allowExcessivelySexualUsage` and
  `allowCommercialUsage`. The source image of `texture` is `thumbnailImage`.
  Every meta field is optional. These are format checks, not consent.
- **Humanoid** (`VRM.humanoid.humanBones`, a list of `{bone, node}`). The same
  bones are required as for VRM 1.0, with the same ancestry checks. A bone listed
  twice, an unknown bone or two bones on one node reject. The thumbs get their
  VRM 1.0 names: `thumbProximal` is `thumbMetacarpal` and `thumbIntermediate` is
  `thumbProximal`.
- **Expressions** (`VRM.blendShapeMaster.blendShapeGroups`). Each preset gets the
  VRM 1.0 name three-vrm gives it (`VRM0_PRESET_NAMES`), so the runtime and the
  host see the same names as for VRM 1.0:

  | VRM 0.x preset | Expression | VRM 0.x preset | Expression |
  | --- | --- | --- | --- |
  | `a` `i` `u` `e` `o` | `aa` `ih` `ou` `ee` `oh` | `joy` | `happy` |
  | `blink` | `blink` | `angry` | `angry` |
  | `blink_l` | `blinkLeft` | `sorrow` | `sad` |
  | `blink_r` | `blinkRight` | `fun` | `relaxed` |
  | `lookup` `lookdown` | `lookUp` `lookDown` | `neutral` | `neutral` |
  | `lookleft` `lookright` | `lookLeft` `lookRight` | `unknown` | the group's own `name` |

  A bind is a mesh's morph target with a weight of 0 to 100. It drives that morph
  on each node in the scene that draws the mesh. Binds are checked against the
  meshes and morph targets as for VRM 1.0. `materialValues` with `_Color`,
  `_EmissionColor`, `_ShadeColor`, `_RimColor`, `_OutlineColor` or
  `_MainTex_ST` become material targets. three-vrm ignores other properties, and
  so does the preflight. Like three-vrm, the preflight skips a group whose name
  is already taken or empty, and says so in `diagnostics`. VRM 0.x has no
  override settings, so every override is `none`.
- **Look-at** (`VRM.firstPerson`). `lookAtTypeName` `Bone` needs both eye bones;
  `BlendShape` needs the four look expressions. The degree maps are bounded.
- **Spring bones** (`VRM.secondaryAnimation`). Each bone group lists root nodes.
  Every node under a root is a joint. Joints must be unique, in the scene and
  apart from the humanoid bones. Collider groups are spheres with bounded offsets
  and radii. `secondaryMotion` is true when a root has a child.
- **Materials** (`VRM.materialProperties`). three-vrm turns `VRM/MToon` and
  `VRM/Unlit...` property sets into MToon materials. The preflight bounds the
  values, and each `textureProperties` index must be a texture of the model.

The runtime calls `VRMUtils.rotateVRM0`, which turns a VRM 0.x model's root half a
turn. Then the model faces +Z (the camera) with the character's left at +X, as a
VRM 1.0 model does. The camera framing, `faceAnchor()`, the eyes and the touch
zones need no other change. A VRM 0.x model's normalized bones still face -Z, so
the idle body, the gestures and `setPose({head})` mirror their turns about X and
Z for it. The spring bones start again from the turned pose. A hit's `point` and
`rest` are in the scene root's space, so their x and z point the other way than
for a VRM 1.0 model; `restCanvas` is the same for both.

Allowed extensions in a VRM 0.x model: `VRM`, `KHR_materials_unlit`,
`KHR_texture_transform` and `KHR_materials_emissive_strength`. VRM 0.x exporters
often list extensions they don't use. So `extensionsUsed` may list an allowed
extension, `VRMC_materials_mtoon` or `VRMC_springBone` that the model doesn't use
(the Blender VRM add-on does this). `extensionsRequired` stays strict. Draco,
meshopt, `VRMC_vrm`, `VRMC_node_constraint`, a used `VRMC_materials_mtoon` and
all other extensions reject.

Still rejected in VRM 0.x models, as in VRM 1.0 models: URIs, embedded
animations, sparse accessors, matrix nodes, tangent morphs, WebP and KTX2
images, and everything over the limits below.

## Intentionally conservative asset subset

Only GLB2 with exact byte length, aligned bounded JSON and at most one embedded
BIN chunk, glTF2 asset version, a single scene, required humanoid roles and
standard ancestry are accepted. A VRM 1.0 model needs exact `VRMC_vrm` version
1.0 and VRM 1.0 metadata. A VRM 0.x model needs the `VRM` extension (see
[VRM 0.x models](#vrm-0x-models)).
Every extension object must be consistently declared in `extensionsUsed`;
required/used declarations cannot refer to absent extensions (a VRM 0.x model may
list unused ones in `extensionsUsed`). Advertised spring
support is additionally checked against imported nonzero-length runtime joints.
The `licenseUrl` format check is **not consent** or a claim of model usage rights.

Allowed extensions in a VRM 1.0 model:
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
recursively. No model scripts or dynamic plugins load. Only embedded PNG and
JPEG textures are accepted; WebP/KTX2 require re-export. PNG dimensions (from
IHDR) and JPEG dimensions (from the baseline, extended or progressive frame
header; other JPEG kinds reject) are checked before browser decode, with the
same limits. A per-parser embedded image decoder uses
`createImageBitmap` directly on the validated bytes: no object URL or fetch is
created, and decoded bitmaps are owned across success, failure, reload and late
completion/disposal. Browser `createImageBitmap` support is required for textured
models. Decode failures surface rather than render success with a silently missing
texture. The bounded parser is defense-in-depth,
**not a full glTF schema validator or an OS/browser security sandbox**.

Main hard limits are in `LIMITS`: 512MiB input (the same as the desktop host's
and the shared character list's), 2MiB JSON, 120,000 JSON entries,
48 JSON depth, 512 nodes/128 hierarchy depth, 2048 accessors, 500,000 accessor
elements, 64MiB decoded/expanded geometry, 128 meshes/16 primitives each,
64MiB aggregate buffer-view copies (overlapping views count separately),
64 morph targets per primitive, 128 materials, 32 images/64 textures,
4096px texture dimensions, and 16Mi pixels of decoded images. Two more budgets
count against limits, so satisfying each individual cap alone is not
sufficient. The geometry each node draws counts again for each node (64MiB in
all). The textures (each image with its sampler) may sample at most 32Mi pixels
in all. Materials that share a texture share its GPU upload, so they don't count
again: a VRoid model's 15 materials on a few textures fit. There are at most 128
expressions and 128 selected mappings. Model bounds must be finite and at most
100m.

## Evidence and remaining integration

Tests execute the real pinned GLTFLoader/VRMLoaderPlugin, VRM expression/morph
application and physics lifecycle against a self-authored synthetic triangle.
They cover malformed headers/resources, absent controls, subset rejections,
ownership/override behavior, finite/range constraints, stale-turn inertness,
playback gating, direct-target revision binding, stop/dispose/reload, and pending
load cancellation. These are not mocked successful import results.
PNG resource-lifetime tests run the production importer with a simulated browser
decoder; they verify ownership/error paths, not real PNG decode or visual quality.
VRM 0.x tests (`test/vrm0.test.ts`) load a self-authored VRM 0.x triangle rig
(`fixtureV0`) through the same importer: mapped names and meta, the turned root,
the idle arms, a nod, a wave, blinking, lip-sync, composed frames, the mirrored
head pose, JPEG decode handoff and bitmap release, and the rejections. Real VRM
0.x models from the Open Source Avatars registry and three-vrm's own VRoid sample
were inspected, imported and idled locally with a simulated decoder; that is
importer evidence, not visual quality evidence.

Local TypeScript compilation, importer/control tests and offline bundle creation
are runnable without GPU. The localhost harness was served and its static route
allowlist checked. Chromium decoded an embedded JPEG in the synthetic VRM 0.x
fixture and drew it facing the camera in the harness. **User/artist VRM visual
quality, VRoid Hub models, device-specific GPU performance, real Audio2Face
and device-rendered audio synchronization remain NOT QUALIFIED in this slice.**
A browser preview can be opened locally, but that alone is not visual evidence.

Desktop host wiring, strict shared AvatarFrame v1 parsing, model-to-shared
capability translation, provider licensing/activation, model terms presentation,
source selection and playback scheduling belong to the subsequent integration.
No global settings/schema/solution changes, remote workflows, pushes or releases
are included here.

References: [official three-vrm runtime](https://github.com/pixiv/three-vrm),
[official VRM1 specification](https://github.com/vrm-c/vrm-specification/tree/master/specification/VRMC_vrm-1.0),
[VRM expressions](https://github.com/vrm-c/vrm-specification/blob/master/specification/VRMC_vrm-1.0/expressions.md),
[VRM 0.x specification](https://github.com/vrm-c/vrm-specification/tree/master/specification/0.0).
