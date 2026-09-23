# Optional Desktop avatar integration

This is an opt-in development application surface, not a qualified GPU/model/
hardware release. The main application remains .NET 10/WPF. Open **Optional
Avatar** from the main window or **Avatar setup / STOP avatar** from an active
conversation. Opening it only reads local choices. Renderer, inference,
microphone, credentials and audio effects remain off.

## Explicit local setup

1. Create/load an ordinary Martlet profile. Select a lawful local VRM1 `.vrm` or
   Live2D `.model3.json`. Live2D requires a dedicated folder containing only its
   supported inert assets, plus a separately prepared local SDK resource folder
   containing `core.js` and `sdk.js`. Follow the Live2D module's `build:dev`
   instructions with your own Framework 5-r.4 and Core 05.01.0000. None is
   downloaded, licensed on your behalf, or redistributed.
2. Check **local GPU model inspection** and click **Inspect model**. This launches
   a private WPF/WebView2 process with your already-installed WebView2 runtime.
   Actual model parsing supplies target IDs/bounds; missing runtime, unsupported
   model subset or unsafe resources fail explicitly. Inspection does not connect
   Audio2Face or play sound. Runtime visual quality is not inferred from metadata.
3. Choose an ARKit channel and an inspected native target, then add the mapping.
   Review native bounds and reduced-fidelity acceptance. The helper explicitly
   omits other aspects. The editable JSON is the actual shared
   `AvatarConfiguration`, not another mapping language. Use **Validate
   compatibility** for shared classifications/remedies. A preview knows the
   permitted schema, not what an uncontacted NIM deployment actually returns.
4. Save the reviewed configuration. Set the numeric-loopback HTTP root of your
   independently provisioned Audio2Face-3D NIM v2 service. No container, model,
   driver or service is installed or started by Martlet; review those terms and
   prerequisites separately.
5. Check the separate generated-speech analysis permission and **Activate**.
   The state is *armed, awaiting generated speech*, not verified/animated.
   Start an explicitly authorized normal voice conversation. Only its accepted
   generated PCM is delivered to the analyzer; microphone data and text are not.
   The first valid A2F response establishes the actual returned channel subset.
   Missing mapped channels stop that segment's animation without delaying speech.

Only the implemented A2F **mouth/expression** path can activate. Other analyzers
(including amplitude), gaze/head/body/secondary-motion composition, arbitrary
blending, motion playback and model-feature overrides are not implemented here.
The two renderer adapters have their own documented conservative asset subsets.
Changing a mapping cannot create an absent authored feature or waive asset/runtime
requirements.

**STOP avatar** is pinned above the scrolling form; Escape does the same.
Voice continues. Closing the configuration window does not revoke explicit
session activation; app exit, session lock, relevant configuration/control
changes and explicit Stop do. No activation is restored after restart.

## Draggable desktop character

After explicit **Inspect model**, both Live2D and VRM appear in a transparent,
borderless, always-on-top desktop overlay, initially near the lower-right corner
of the primary work area. The character stays visible over other ordinary
windows while an activated, authorized voice conversation animates her through
the existing generated-speech path. Inspection alone does not enable analysis.

Drag the character or **Move character** handle to reposition the overlay,
including onto another monitor. With the move handle focused, use arrow keys
for 10-DIP steps (device-independent pixels), Shift+arrows for 1-DIP steps, or Home to return to the
primary screen. The small move/close controls stay available; the model has no
opaque panel or title bar. The overlay does not take keyboard focus on opening.
Position is session-only, and this is not a global click-through or game-injected
overlay. Exclusive-fullscreen applications may cover it.

**Close**, Alt+F4, or Escape while the overlay has focus closes only the renderer;
normal voice playback continues. Inspect again before reactivation. Closing
the setup form alone still leaves the character visible; **STOP avatar** removes
it. No new analysis, microphone, provider or startup permissions are introduced.
The host uses WPF composition WebView2 and transparent page/WebGL surfaces for
both models, with Windows 10 2004+ graphics-composition bindings included in the
renderer build. Local controlled shell checks and actual WebView2 rendering of
the repository's synthetic VRM triangle establish the transparent host path,
not artist-model/Live2D Core, mixed-DPI or physical speech-sync qualification;
those checks remain **NOT RUN**.

## Ownership and isolation

`AvatarProfileStore` owns one atomic, revision-checked `avatar.json` under the
existing data directory. It contains the existing profile ID, shared configuration,
resource paths/identity, renderer and local endpoint. It neither changes nor
versions `settings.json`. Default activation is OFF even when a saved preference
says enabled. No credentials, PCM, frames, transcripts or runtime availability
are persisted.

The existing global configuration backup/restore **does not include** this
feature document. The Avatar window provides local export and explicit restore;
restore retains prior bytes as a unique `.bak`, clears the resource inspection
binding, and never grants activation. Profile mismatch, malformed/future version,
conflict or inaccessible storage cannot silently overwrite existing bytes.
Disable preserves all settings and user assets.

Desktop owns playback and analysis; `Martlet.Avatar.RendererHost` owns WebView2.
The private inherited pipes are bounded, typed and serialized. They carry only
approved resources/configuration, capabilities, full source/playback identity,
native composed target values and lifecycle acknowledgements. A kill-on-close
Windows job owns the renderer descendants. Missing acknowledgement or child
failure stops avatar delivery, not voice. Cleanup must finish before reactivation.

The renderer has no vault/provider/audio reference and receives no microphone,
PCM, transcript, credentials or arbitrary command. Its fixed local application
origin intercepts only allowlisted resources; external navigation, frames,
downloads, permission prompts, host objects and page network requests are denied.
Model files are data, not scripts. User-supplied official Core/Framework are
separate trusted runtime prerequisites. This is process/crash isolation and page
resource restriction, not an OS sandbox against malicious same-user software or
every browser-runtime background activity.

## Playback and failure semantics

The production tee is attached only after successful `ConversationTurn` admission
of the epoch-mapped PCM frame. It has a 64-frame bound and one pending segment;
the A2F stream additionally has its own 16-frame ingress bound. There are no
unbounded per-frame tasks, producer waits for avatar capacity, or analyzer
callbacks on the native audio worker. A saturated/failed path invalidates only
segment animation and reports why; voice retains its original bytes and lifetime.

The nullable device clock is separate from accepted/read/submitted and
committed-minus-padding accounting. It uses raw NAudio device position/frequency,
converted to source samples; no wall-clock extrapolation or queue-counter
fallback. Production rejects test-clock provenance. Clock observations older
than 100 ms, unavailable/regressed clocks, any observed empty endpoint after
start (including queued refill), >1000 ms future output or >250 ms late frames
make that segment unavailable. Voice can resume after underrun; **resumed
animation is not supported for that segment**. A fresh segment establishes a
new identity/epoch/clock binding.

The shared gate and composer retain original session/turn/request/source IDs,
epoch, sequence and sample clock. Affine mapping occurs once in the shared
composer; the renderer consumes native targets and validates them again against
the inspected model and current configuration. Stop/replace invalidation precedes
asynchronous cleanup. Old frames and old configuration replies cannot revive
stopped animation.

No hardware/acoustic audibility is inferred. Licensed native Core rendering,
real WebView/model/PNG/GPU paths, live NIM quality, witnessed synchronization and
physical p95 clearing qualification are **NOT RUN** without separately authorized
prerequisites. Controlled local protocol/clock/process tests do not replace them.

## Local builds

Restore the checked-in VRM npm lock for the shared browser bundler using
`npm ci --prefix src\Martlet.Avatar.Vrm --no-audit --no-fund`, then restore/build
Desktop normally with the repository SDK. No Node/npm/dev server is used at
application runtime. The host build bundles both actual renderer modules,
retains complete runtime JS licenses/notices, and emits a deterministic
`avatar-bundle-receipt.json` plus raw `avatar-esbuild-metafile.json` in its
intermediate output. Source Core/Framework/models are never bundled.

Run the actual browser-message/fatal-state bridge regression without starting a
browser or GPU:

```powershell
node --experimental-vm-modules --test src\Martlet.Avatar.RendererHost\web\bridge.test.mjs
```

Node 20 requires `--experimental-vm-modules` for this test's isolated
`vm.SourceTextModule` harness. Plain `node --test` is not an equivalent command.

Normal Desktop output includes its independent `AvatarRenderer` executable,
dependencies and `web` shell. Local normal win-x64 framework-dependent publish
was exercised; this does not qualify an installer or authorize release publication.
Packaging provenance/SBOM and update-consumer integration have separate owners.
