# Martlet Live2D development adapter

Independent, private TypeScript browser package. No Desktop, voice, analyzer, settings,
solution or transport dependency. Audio2Face remains the preferred analyzer upstream;
this adapter neither selects nor starts any provider. It is not a released Live2D
product and includes no Cubism Framework, Core, models, textures or artist assets.

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
source. The harness additionally requires a clean git checkout at that commit.
Neither the revision string nor the version integer authenticates arbitrary code.
SDK scripts are trusted application resources, never model-supplied resources.

## Host integration: one transform only

Public exports are in `dist/index.js`, with `.d.ts` declarations. The public SDK
ports describe inspected official APIs; they do not implement a substitute SDK.

```ts
const bundle = new LocalModelBundle(selectedFileBytes, "Avatar.model3.json");
const adapter = new Live2DAdapter(canvas, { sdk, onDiagnostic: report });
const capabilities = await adapter.load(bundle);

// The shared compatibility composer has already mapped/scaled these exact targets.
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
entries there), assumes `ParamMouthOpenY`, or invents a default `0..1` output range.

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
No autogenerated rig, MotionSync, automatic blink/breath/gaze layer, SDK motion,
expression-motion, physics or pose playback is started. Declared optional assets
are checked for safe references/presence and reported inactive. Model parameter
writes occur immediately before model update/draw, with no later SDK animation
writer to unexpectedly replace the composed mouth. Authored Layout/HitAreas are
reported inactive; framing fits the model's canvas without stretching.

## Asset and lifecycle boundaries

- No model URLs, filesystem resolver, CDN, archive import, script plugins or
  dynamic asset code. Relative references reject absolute/remote paths, traversal,
  backslashes, percent escapes, query/fragment strings and case collisions.
- Only bounded JSON, MOC3, PNG and inert WAV entries are accepted. Every declared
  optional reference must exist even if inactive. Unknown metadata fields fail
  explicitly instead of silently activating extensions.
- Limits: 128 files; 16 MiB/file; 1 MiB/JSON; 64 MiB total; 16 PNG textures;
  4096 pixels/side; 32 million total texture pixels; 2048 canvas pixels/side;
  1024 parameters; 2048 drawables; 250,000 vertices; 750,000 indices.
- PNG headers are checked before decoding, decoded dimensions rechecked before
  upload, and decode is limited to 10 seconds. Late results after cancellation
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

For the development harness, the user must independently obtain the SDK under
its terms. No tool here downloads it or accepts any agreement. Put the authorized
Core script at `local-sdk\Core\live2dcubismcore.min.js` and a clean pinned official
Framework git checkout at `local-sdk\Framework`.

```powershell
npm run build:dev
npm run serve:dev
```

The build creates ignored **local-only** `dev-dist\core.js`, `sdk.js` (named `sdk`
export), `app.js` and `index.html`. `harness\sdk-local.ts` shows the exact static
module binding for a host. Load the local Core script before the SDK module.
These output files contain licensed SDK code: do not publish them.
The server binds only `127.0.0.1:4184`, allows only fixed static GET resources,
rejects other Host headers, disables browser network connections via CSP and
disables media capture/autoplay. Launching starts no model/analyzer/audio.
Select a folder containing exactly one model, inspect capabilities, explicitly
configure a mapping, then apply bounded preview values. Preview expires after
250ms. Close/dispose never affects voice.

## Qualification status and license gate

Local TypeScript build and production parser/mapping/lifecycle boundary tests
exercise this implementation. The tests use synthetic metadata and recording
SDK/WebGL ports, **not a fake renderer offered as a working runtime**.
They are not evidence of successful MOC native parsing, PNG GPU upload, SDK
shader compilation or visible model rendering.

**Actual Framework/Core loading, full SDK type compatibility, native MOC parsing,
GPU rendering and end-to-end harness rendering: NOT RUN.** Minimum prerequisites:
the exact lawfully acquired Framework/Core above, a permitted compatible
original model with its declared assets, a working local browser/WebGL context,
and user-approved SDK terms. Then build the SDK bundle and exercise load/draw,
custom mouth/eye bounds, alpha/masks, resize, reset/late frames, context loss,
reload and disposal. Missing prerequisites currently cause an explicit
`MISSING_LOCAL_SDK` build failure or `MISSING_SDK`/`MISSING_CORE` runtime failure.

The official Framework public advisory endpoint returned no entries when checked
2026-09-23. Open upstream reports were inspected: `Live2D/CubismWebFramework#36`
describes unbounded SDK JSON `strtod` scanning (this lane never invokes SDK JSON,
motion, physics or expression loaders); #37 and #38 concern 5.3 offscreen paths
not present in this lane. This is a bounded observation, not a security clearance
or exhaustive audit. New applicable unresolved safety issues block activation.

Development direction is not release permission. Publication licensing,
Expandable Application review, commercial use and asset distribution permissions
remain separate gates. See [NOTICES.md](NOTICES.md).
