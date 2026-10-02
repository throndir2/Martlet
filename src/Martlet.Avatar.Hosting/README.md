# Optional Desktop avatar integration

The main application remains .NET 10/WPF. Click **Show character** on the main
window to open the bundled Live2D character (Hiyori), or open **Character
settings** to choose a model, lip-sync source and auto-start. Nothing renders
until you ask; microphone, credentials and provider audio stay untouched.

## Default character: Hiyori with local lip-sync

Official builds bundle the Live2D Cubism SDK for Web 5-r.4 runtime and the
Hiyori Momose sample model under `AvatarRenderer\live2d` (see
[Live2D module](../Martlet.Avatar.Live2D/README.md#bundled-runtime-and-default-character)).
`AvatarProfile.ModelPath` value `builtin:Hiyori` resolves to that folder in both
the Desktop and renderer processes; `SdkDirectory` left empty uses the bundled
`core.js`/`sdk.js`. **Show character** needs no setup profile: without one, the
choice simply is not saved.

The character idles with her authored motions, blinks, breathes, sways with
physics, turns her head/eyes toward the mouse cursor and moves her mouth while
Martlet speaks. **Show the character automatically when Martlet starts** saves
`AutoShow` in `avatar.json`. Custom Live2D `.model3.json` folders and VRM1 `.vrm`
models get the same idle behavior (VRM: relaxed arms, breathing, blink).

Lip-sync modes (`AvatarLipSync`):

| Mode | Behavior |
| --- | --- |
| `Auto` (default) | Before each generated-speech sentence, TCP-probe the configured loopback Audio2Face endpoint (default `127.0.0.1:52000`). If something is listening, stream that sentence's generated PCM to it and apply its frames on the playback clock through the model's reviewed saved mapping or the built-in mouth mapping (Live2D: `jawOpen` -> LipSync group/`ParamMouthOpenY`, `mouthSmileLeft` -> `ParamMouthForm`; VRM: `jawOpen` -> `aa`, `mouthFunnel` -> `oh`, `mouthPucker` -> `ou`). Loudness levels are computed for every sentence and take over whenever Audio2Face is absent, fails, lags or stops (renderers ignore loudness while Audio2Face frames are fresh). |
| `Loudness` | Never contacts Audio2Face. |
| `Audio2Face` | Manual mapping and explicit per-session activation below; no loudness. |

Loudness derives a 0..1 mouth level from the RMS of Martlet's own generated TTS
PCM in 20 ms windows and presents it against the playback device clock (falling
back to consumed samples). Auto and Loudness survive pause/mute/configuration
revocations; hiding the character stops them. Only Martlet's generated voice is
sent, and only to a numeric loopback address; the microphone is never used.
Martlet does not start Docker, WSL or the Audio2Face NIM here. WSL 2 and Docker
Desktop can be installed on request from the setup advisor, **Prerequisites** in
Settings › Tools or Start > **Martlet prerequisites** ([Prerequisites](../../docs/PREREQUISITES.md)).

### Running an Audio2Face service yourself (NVIDIA GPU)

**As a Martlet host (recommended; another computer or this PC):** in Martlet >
**Martlet hosts** (also linked from Character settings), set up a host and add
the `audio2face` role: this PC through Docker Desktop, another computer over SSH
(Docker or native Ubuntu), or by running the same
[`martlet-host setup`, `pair` and `add audio2face`](../../deploy/host/README.md)
commands on the host yourself. It starts the NIM on the host's loopback and
publishes the gateway's Audio2Face relay route. Pair this PC by typing the
address and short one-use code (like `K7QM-4XPA`) that `martlet-host pair` shows
on the host into **Enter a pairing code**. The device
secret is stored in Windows Credential Manager; `avatar.json` keeps only the
nonsecret `RemoteHost` identity of the host that handles lip-sync, and
`hosts.json` lists every paired host. Hand lip-sync to another host, back to
this PC or to nobody from the Lip-sync row's **Done by** on the Devices page; a showing character
switches without restarting.
Automatic lip-sync then prefers the host in charge, then a local service, then
loudness. Sentences are relayed in 0.5 s / 1 s chunks with 0.5 s of context so
animation starts while Martlet is still speaking; late frames are skipped.

**This PC:** not verified on a Martlet machine. The NIM is a Linux container: on Windows use
Docker Desktop with the WSL2 backend and a current NVIDIA driver, sign in to the
NVIDIA NGC registry with your own API key (NVIDIA account; review the NIM and
model terms), then start NVIDIA's
[Audio2Face-3D samples quick start](https://github.com/NVIDIA/Audio2Face-3D-Samples/tree/main/quick-start)
(`A2F_3D_MODEL_NAME=claire docker compose up`) so its gRPC port 52000 is
reachable at `127.0.0.1:52000`. The first start builds TensorRT engines and takes
minutes. Martlet picks it up at the next sentence; set another loopback port in
**Character settings > Advanced** if needed.

## Audio2Face-only (advanced) setup

1. Create/load an ordinary Martlet profile. Select the bundled character, a
   lawful local VRM1 `.vrm` or a Live2D `.model3.json` folder containing only
   supported inert assets. An optional SDK override folder containing `core.js`
   and `sdk.js` replaces the bundled runtime.
2. Check **local GPU model inspection** and click **Inspect model**. This launches
   a private WPF/WebView2 process with the installed WebView2 runtime
   (**Prerequisites** in Settings › Tools installs it when missing).
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
   Activation replaces loudness lip-sync for the session. The state is *armed,
   awaiting generated speech*, not verified/animated. Start an explicitly
   authorized normal voice conversation. Only its accepted generated PCM is
   delivered to the analyzer; microphone data and text are not.
   The first valid A2F response establishes the actual returned channel subset.
   Missing mapped channels stop that segment's animation without delaying speech.

Only the implemented A2F **mouth/expression** path can activate. Gaze/head/body
A2F composition and arbitrary blending are not implemented here; composed A2F
writes apply after idle motion/breathing and before physics/pose. Changing a
mapping cannot create an absent authored feature or waive asset/runtime
requirements.

**Hide character / STOP avatar** is pinned above the scrolling form; Escape does
the same. Voice continues. Closing the configuration window does not hide the
character or revoke explicit A2F activation; app exit, relevant configuration/
control changes, session lock (A2F only) and explicit Stop do. A2F activation is
never restored after restart.

Stopping removes the renderer's temporary WebView2 cache when Windows releases
its files, retrying in the background; a leftover cache never blocks stopping.
If the renderer itself cannot be stopped cleanly, **Show/Hide character** retries
and the reason goes to the desktop log. Exiting (including to install an update)
never waits on it: the renderer runs in a kill-on-close job object, so Windows
ends it with Martlet.

## Draggable desktop character

The character appears in a transparent, borderless, always-on-top desktop
overlay, initially near the lower-right corner of the primary work area.

Drag the character to reposition the overlay, including onto another monitor.
The mouse wheel over the character zooms: it first grows the overlay up to the
height of the screen (growing downward instead once its top reaches the top of
the screen), then keeps zooming the camera into the character (up to 16x,
toward the cursor). The top of the character's head always stays in view: zoom
and pan never push it above the overlay's top edge (the renderer reports where
the head's top is for the loaded Live2D or VRM model). When zoomed in,
Ctrl+drag or middle-drag pans.
Right-click the character for **Zoom in**, **Zoom out**, **Reset zoom** and
**Reset position and size**. After clicking the character, use arrow keys for
10-DIP steps (device-independent pixels), Shift+arrows for 1-DIP steps, +/- to
zoom, 0 to reset zoom, or Home to return to the primary screen at the default
size. The overlay has no buttons, panel or title bar; its other controls live
in the main Martlet window.
**Reset character position** (home screen, shown while the character is
visible) returns it to the lower-right of the primary screen at its default
size and zoom, even if it was dragged off-screen. **Reset character zoom**
restores the default size and zoom without moving it. **Companion > Character**
also has **Zoom in** and **Zoom out** and shows the overlay's current size,
camera zoom and where the top of the head sits. The overlay does not take
keyboard focus on opening.
Position is session-only, and this is not a global click-through or game-injected
overlay. Exclusive-fullscreen applications may cover it.

**Hide character** in the main window, Alt+F4, or Escape while the overlay has
focus closes only the renderer; normal voice playback continues. **Show
character** opens it again.
The host uses WPF composition WebView2 and transparent page/WebGL surfaces for
both models, with Windows 10 2004+ graphics-composition bindings included in the
renderer build. The bundled Hiyori model has been rendered locally through the
real Core/Framework in the actual WebView2 overlay (idle motion, blink, physics,
lip-sync and cursor-follow checked); mixed-DPI and physical speech-sync timing
remain unmeasured.

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
Model files are data, not scripts. The bundled (or user-overridden) official
Core/Framework are trusted runtime resources. This is process/crash isolation and page
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

No hardware/acoustic audibility is inferred. Live NIM quality, witnessed
synchronization and physical p95 clearing qualification are **NOT RUN** without
separately authorized prerequisites. Controlled local protocol/clock/process
tests do not replace them.

## Local builds

Restore the checked-in VRM npm lock for the shared browser bundler using
`npm ci --prefix src\Martlet.Avatar.Vrm --no-audit --no-fund`, then restore/build
Desktop normally with the repository SDK. No Node/npm/dev server is used at
application runtime. The host build bundles both actual renderer modules,
retains complete runtime JS licenses/notices, and emits a deterministic
`avatar-bundle-receipt.json` plus raw `avatar-esbuild-metafile.json` in its
intermediate output. It also downloads (once, SHA-256 pinned) the official Live2D
SDK and writes `web\live2d-dist` (runtime, Hiyori, notices), copied to the
renderer's `live2d` folder. Without network access the build continues without
the bundled Live2D runtime; `MARTLET_REQUIRE_LIVE2D=1` (set by the public release
build) makes it mandatory.

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
