# Martlet Live2D renderer adapter

Independent, private TypeScript browser package. No Desktop, voice, analyzer, settings,
solution or transport dependency. It loads Cubism models, animates them with the
official Framework (idle motions, eye blink, breathing, physics, pose, cursor
look-at) and accepts a speech-loudness lip-sync level or host-composed parameters.

## Bundled runtime and default character

`scripts/sdk.mjs` downloads the official Cubism SDK for Web 5-r.4 archive from
`https://cubism.live2d.com/sdk-web/bin/CubismSdkForWeb-5-r.4.zip`, verifies the
pinned SHA-256 and extracts it into the git-ignored `vendor/` folder. Nothing from
the SDK is committed. The renderer host build then produces:

| Output (renderer `live2d` folder) | Source |
| --- | --- |
| `sdk/core.js` | Unmodified redistributable `Core/live2dcubismcore.min.js` (Core 05.01.0000) |
| `sdk/sdk.js` | `runtime/sdk-bundle.ts` compiled with the SDK's Framework source |
| `characters/Hiyori/` | Unmodified `Samples/Resources/Hiyori` (Live2D Original Character) |
| `LIVE2D-NOTICES.txt` | Required copyright notice and license references |

`runtime/sdk-bundle.ts` supplies `SdkModules.createAnimator`, which wires the
Framework's `CubismMotionManager`, `CubismEyeBlink`, `CubismBreath`,
`CubismPhysics`, `CubismPose` and `CubismExpressionMotionManager` to the loaded
model, following the official sample's update order. Idle-group motions play at
random; `EyeBlink` group IDs blink (or `ParamEyeLOpen`/`ParamEyeROpen` when the
model declares no group, as VTube Studio models usually don't); breathing adds to
the standard angle/breath parameters that exist; cursor look-at adds to
`ParamAngleX/Y/Z`, `ParamBodyAngleX` and `ParamEyeBallX/Y`; the loudness level is
added to the `LipSync` group (or `ParamMouthOpenY` when the model declares no
group). `Live2DAdapter.modelSummary` reports the resulting blink/mouth IDs,
textures and any downscale, motion groups, expressions and physics; the desktop
logs it and shows it in Character settings.

Emotes and motions (docs/AVATARS.md "Emotes and motions"): `LocalModelBundle`
takes optional `extras` (expressions and motion groups the host found outside
the model3.json, such as a VTube Studio model's hotkey files and idle
animation, named by the host). `setExpression(name)` fades an expression in and
`setExpression(null)` fades it out (an empty expression replaces it);
`playMotion(group)` plays one motion of a group once at normal priority, then
idling resumes; `gesture(name)` plays one of Martlet's own gestures the model has
the standard parameters for (`gestures`: `nod`, `shake`, `tilt`, `bow`, `sway`,
`smile`, `blush`, `surprise`, and the voice emotes `laugh`, `chuckle`, `sigh`,
`gasp`, `cough`, `clear_throat`, `groan`, `sniff`, `shush`, `inhale`, `exhale`,
`mumble`, `hum`, `sneeze`, `whistle`, `happy`, `sarcastic`, `angry`, `fear`,
`crying`, `whispering`, `dramatic`, then `wink`, `pout`, `shy`, `giggle`, `flinch`,
`lean_in`, `look_away`, `think`, `eye_roll`, `drowsy`, and last the stronger
blush levels `blush_deep` and `blush_fierce`), added to the look-at angles and those parameters
(`lib/gestures.ts`). `gesture(name, true)` holds `pout`, `shy`, `look_away`,
`drowsy` or a blush level until `endGesture(name)`; a gesture played meanwhile plays on top, and
`gestureState` says which plays once and which is held. Every blush level
(`BLUSH_LEVELS`) moves `ParamCheek` fully; without it `gesture` returns false
and the renderer page draws the level over the face instead.

`faceAnchor()` says where the face is now, for Martlet's drawings over it
(`lib/face.ts`). At load the adapter finds the face at rest, then pins its
eyes, cheeks, mouth and top to nearby mesh vertices that ride the head
rigidly: it moves each of `ParamAngleX`/`Y`/`Z` to find the vertices that turn
with the head, then every other parameter to its maximum and minimum to drop
those that deform on their own, and puts every parameter back
(`faceTracking` reports how many vertices and how long it took). Each frame
the pinned points follow those vertices as Core deformed them (moving least
squares), with each cheek's surface (`cheekLeftFrame`, `cheekRightFrame`), and
`tracking` is `"mesh"`. Without enough such vertices, `tracking` is
`"estimate"`: the face moved with the head angles, as before.

Licenses: Core is under the Live2D Proprietary Software License (redistributable
file only, inside Martlet), the Framework under the Live2D Open Software License,
Hiyori under the Free Material License / Sample Data Terms (design unmodified,
copyright notice required). Individuals and small businesses need no Publication
License for ordinary apps; because users can import their own models, Martlet is
an Expandable Application and its public release requires Live2D's review and
agreement (applied for by the owner). See [NOTICES.md](NOTICES.md).

## Supported development lane

| Component | Required version |
| --- | --- |
| Official Cubism Web Framework | `5-r.4`, commit `8df84780f2aa1298f3b30965cdae143e049f3c8e` |
| Official Core bundled with SDK `5-r.4` | `05.01.0000`, runtime integer `0x05010000` |
| Model | Version 3 `.model3.json`, MOC accepted by that exact Core, local PNG textures |
| Browser | Dedicated WebGL canvas, `createImageBitmap`, Web Crypto, optional RAF |
| Local tooling | Node 20.11+, npm; TypeScript 5.9.3 and esbuild 0.25.10 locked locally |

This is deliberately not blanket Cubism 5 compatibility. Current stable Framework
`5-r.5` (`198a3769c26ca3d7b600e932590433badd392edd`) was inspected on 2026-09-23.
Its `src/rendering/cubismshader_webgl.ts` uses private asynchronous
`loadShaders()`/`fetch()` behind void `generateShaders()`; the renderer's
`loadShaders()`/`drawModel()` lacks a public cancellation/readiness promise.
`5-r.4` embeds shaders and does not require that network lifecycle. Cubism 5.3
blend/offscreen features and `5-r.5` are **not supported**. Upgrading requires a
separate deterministic local shader-loading, cancellation and GPU qualification
pass. Unsupported Core/MOC combinations fail explicitly, not by asset conversion.

`checkRuntime` requires the actual global Core version. `SdkModules.revision`
identifies the injected Framework; a trusted host must obtain it from the pinned
source. The build pins the official SDK archive by SHA-256 instead.
Neither the revision string nor the version integer authenticates arbitrary code.
SDK scripts are trusted application resources, never model-supplied resources.

## Host integration: one transform only

Public exports are in `dist/index.js`, with `.d.ts` declarations. The public SDK
ports describe inspected official APIs; they do not implement a substitute SDK.

```ts
const bundle = new LocalModelBundle(selectedFileBytes, "Avatar.model3.json");
const adapter = new Live2DAdapter(canvas, { sdk, onDiagnostic: report });
const capabilities = await adapter.load(bundle);   // idle animation starts when sdk.createAnimator exists
adapter.update(deltaSeconds);                       // host render loop
adapter.setLipSync(level);                          // 0..1 speech loudness; decays after 300ms
adapter.setLook(x, y);                              // -1..1 cursor direction

// Optional host-composed (Audio2Face) path: the shared composer has already mapped/scaled these targets.
adapter.configureTargets(selectedModelParameterIds);
const configurationId = adapter.configurationId;
const identity = { sessionId, turnId, requestId, sourceId, epoch };
adapter.resetEpoch(identity);
const result = adapter.applyComposedParameters({
  identity, sequence, configurationId,
  parameters: composed.Parameters
});
adapter.update(deltaSeconds);
// adapter.dispose() never stops voice, playback or an analyzer.
```

`configureTargets()` accepts only distinct actual Core parameter IDs and returns
their measured metadata. `applyComposedParameters()` writes final model-local
values directly, without another affine transform. It rejects unapproved IDs,
nonfinite values and out-of-bounds values atomically. A missing approved target
resets to its Core-reported neutral. This path is mutually exclusive with the
normalized mapping path below. A new load/configuration produces a new
`configurationId`; queued old-model/profile output cannot act on the new model.
`configureTargets()` and `configure()` invalidate the old identity: call
`resetEpoch()` after configuration.

The host owns the strict shared `AvatarFrame` schema, original correlation IDs,
original selected `source_id`, original sequencing/sample rate/sample offset,
actual playback-time gating, provider compatibility and user-controlled aspect
composition. This initial direct sink retains the original single-source
binding; it does not invent a host-composed provider ID. For multi-source
composition the shared owner must supply the agreed output-binding contract.
**Do not put model-local parameter IDs/values in canonical blendshapes.**
The renderer-local sink is not a new transport protocol. It must only be exposed
through a trusted host bridge, never arbitrary model JavaScript or raw web messages.

Renderer identities compare session, turn, request, source and epoch. Epoch and
sequence must be integers `0..2147483647`; correlation IDs are nonempty UUIDs.
Rejected old/wrong identity, sequence or configuration is inert: it does not
neutralize the active turn or cancel its clock. Invalid active-frame values throw
`Live2DError` without consuming the sequence. Explicit reset/Stop/configure/load/
dispose transitions clear state and cancel outstanding callbacks.

`update(deltaSeconds)` draws through `CubismModel.update()` and
`CubismRenderer_WebGL.drawModel()`. The host may instead opt into `startClock()`.
Do not run both clocks concurrently. After more than 250ms without fresh accepted
output, the renderer neutralizes and stops its owned clock, emitting
`FRAME_EXPIRED`; it never replays buffered frames. This is a local starvation
watchdog, **not** a substitute for the host's original playback sample clock.
A fresh accepted frame may be followed by `startClock()` to resume.

## Model inspection and explicit mapping

`LocalModelBundle` snapshots a map of user-selected relative paths to byte arrays.
It parses JSON using the built-in JSON parser, never Cubism's JSON parser.
`load()` calls `CubismMoc.create(bytes, true)`, checks the created object's
`getMocVersion()` before `createModel()`, and reads
`getParameterId`, minimum, maximum and default for every real parameter.
The pinned Framework owns its Core version lookup using the required
`csmGetMocVersion(moc, mocBytes)` signature; this adapter never calls the newer
single-buffer overload. Version rejection releases the created MOC and Framework.
`LipSync`/`EyeBlink` groups are reported as authored metadata, not inferred names.
It never uses `getParameterIndex` for absent IDs (the SDK can synthesize virtual
entries there), assumes `ParamMouthOpenY` for a mapping, or invents a default
`0..1` output range. (Only the idle animator's blink and loudness lip-sync fall
back to the standard eye/mouth IDs, as described above.)

The standalone mapping path uses `configure(ChannelMapping[])` followed by
`resetEpoch(identity)` and `applyFrame({identity, sequence, configurationId, channels})`.
Capture `adapter.configurationId` when producing the frame, not when delivering
previously queued output. Both input modes reject missing/old configuration IDs
without changing active writes, sequence, frame age or clock state.
Inputs are normalized `0..1`, named by the host/profile:

```json
[
  {
    "channel": "semantics.mouth_open",
    "parameterId": "AnActuallyReportedMouthParameter",
    "aspect": "mouth",
    "outputMinimum": -2,
    "outputMaximum": 4
  }
]
```

Those example bounds and name are illustrative, not a default rig.
`semantics.*`, `blendshapes.*` and `local.*` are profile namespaces; the host validates
canonical names against the shared contract. A normalized `local.gaze_x` is allowed
only after explicit mapping to a real bounded parameter; there is no gaze wire
extension. Inversion and one-to-many explicit fan-out are supported. Many-to-one
reduction must be done upstream. Multiple writers to one target are rejected.
ARKit coefficients are not automatically equivalent to Live2D controls.

Capabilities report all real parameters, group memberships, missing group IDs,
disabled absent-target mappings, unmapped controls and supported reductions.
Host final-target mode uses `configureTargets()`'s returned metadata rather than
the normalized profile's mapping report to describe selected targets.
With the animator, composed writes are applied after motion/blink/breathing and
before physics/pose each frame, and loudness lip-sync is suppressed while a
composed frame is fresh. Without `createAnimator` (for example test ports), no
motion, physics or pose runs and all parameters are written each frame.
Motion `Sound` files are validated but never played; Martlet's voice drives the mouth.
Authored Layout is reported inactive; framing fits the model's canvas
without stretching. Authored HitAreas are kept for tapping: `hitTest(x, y)`
(canvas fractions, +y down) returns the HitAreas (by name) and the visible
drawables (topmost first, at most 8) whose triangles contain the point, as posed
in the last frame (`lib/touch.ts`); no authored hit-test script ever runs.

## Asset and lifecycle boundaries

- No model URLs, filesystem resolver, CDN, archive import, script plugins or
  dynamic asset code. Relative references reject absolute/remote paths, traversal,
  backslashes, percent escapes, query/fragment strings and case collisions. Names
  may use letters and digits of any script (for example `简.moc3`) plus spaces
  and `. _ - ( ) [ ] + & ' , ! ~ @ =`.
- Only bounded JSON, MOC3, PNG and inert WAV entries are accepted. Every declared
  optional reference must exist even if inactive. Unknown metadata fields fail
  explicitly instead of silently activating extensions. The desktop host reads
  only the files `model3.json` declares, so VTube Studio settings, readmes and
  icons beside a model are never opened.
- Limits: 128 files; 64 MiB/file; 1 MiB/JSON; 128 MiB total; 16 PNG textures of
  at most 8192 pixels/side and 256 million pixels in total. On the GPU, textures
  are halved (all by the same power of two) until each fits 4096 pixels/side and
  together 32 million pixels, keeping power-of-two atlases mipmapped; UVs are
  normalized, so the art is unchanged (`TEXTURES_DOWNSCALED` diagnostic). Also
  2048 canvas pixels/side; 1024 parameters; 2048 drawables; 250,000 vertices;
  750,000 indices.
- PNG headers are checked before decoding, decoded (and resized) dimensions
  rechecked before upload, and decode is limited to 20 seconds. Late results after cancellation
  are closed, not installed. Geometry limits are checked after Core creation.
  These are application resource budgets, **not a sandbox or hard limit on
  native/WASM allocation or synchronous parser execution time**. Do not feed
  untrusted internet models to an unqualified SDK.
- Dedicated canvas and single active adapter per JavaScript realm. Rejects
  externally initialized Framework ownership. Dispose releases renderer, GPU
  textures, model, MOC and owned Framework state; clears the preview; cancels RAF
  and pending decode; removes listeners. Context loss requires recreation.
  Cleanup attempts all independent releases and surfaces aggregate failures.

## Local commands and bounded harness

```powershell
Set-Location src\Martlet.Avatar.Live2D
npm ci --ignore-scripts --no-audit --no-fund
npm test
```

If a machine has an inherited PATH beyond cmd.exe's limit, use a short PATH in
that one shell (include Node, Windows and Git), or directly execute
`node node_modules\typescript\bin\tsc -p tsconfig.json` followed by
`node --test tests`. Do not change the user's machine PATH.

For the development harness, `npm run build:dev` runs the same pinned SDK
download (`scripts/sdk.mjs`) and compiles `harness/sdk-local.ts`, which re-exports
the production `runtime/sdk-bundle.ts`.

```powershell
npm run build:dev
npm run serve:dev
```

The build creates ignored **local-only** `dev-dist\core.js`, `sdk.js` (named `sdk`
export), `app.js` and `index.html`. Load the Core script before the SDK module.
The server binds only `127.0.0.1:4184`, allows only fixed static GET resources,
rejects other Host headers, disables browser network connections via CSP and
disables media capture/autoplay. Launching starts no model/analyzer/audio.
Select a folder containing exactly one model, inspect capabilities, explicitly
configure a mapping, then apply bounded preview values. Preview expires after
250ms. Close/dispose never affects voice.

## Qualification status and license gate

Local TypeScript build and parser/mapping/lifecycle tests use synthetic metadata
and recording SDK/WebGL ports. In addition, the production renderer page
(`Martlet.Avatar.RendererHost/web`) was run locally with the bundled official
Core/Framework and Hiyori: native MOC parsing, PNG upload, shader compilation,
visible rendering, idle motion, blinking, physics, lip-sync and look-at were
observed in a browser and in the real WebView2 desktop overlay. Other models,
GPUs and mixed-DPI setups are unmeasured.

The official Framework public advisory endpoint returned no entries when checked
2026-09-23. `Live2D/CubismWebFramework#36` describes unbounded SDK JSON `strtod`
scanning; the animator now uses the SDK motion/physics/pose/expression loaders,
bounded by this package's 1 MiB per-JSON limit. #37 and #38 concern 5.3 offscreen
paths not present in this lane. This is a bounded observation, not a security
clearance. Prefer trusted model sources.

Publication: Core redistribution inside Martlet follows the Proprietary Software
License's redistributable-file terms; Hiyori follows the Sample Data Terms. Live2D
treats apps that load user models as Expandable Applications, whose public release
requires Live2D's review and agreement. See [NOTICES.md](NOTICES.md).
