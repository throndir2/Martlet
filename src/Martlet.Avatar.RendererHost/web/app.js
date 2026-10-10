import { Live2DAdapter, LocalModelBundle } from "../../Martlet.Avatar.Live2D/lib/index.ts";
import { VrmAvatarAdapter } from "../../Martlet.Avatar.Vrm/src/index.ts";
import { activeOverlays, attachOverlay, BLUSH_LEVELS, clearOverlays, hasOverlay, heldOverlays, insideEyeShape, registerBlush,
  registerOverlay, renderOverlay, startOverlay, stopOverlay, toCssAnchor } from "./overlay.js";
import { registerManpu } from "./effects/manpu.mjs";
import { attachZones, renderZones, setZoneView, zoneAreas, zoneBoxes, zonesDrawn } from "./zones.js";

const canvas = document.getElementById("avatar");
attachOverlay(document.getElementById("overlay"));
attachZones(document.getElementById("zones"));
registerBlush();
registerManpu(registerOverlay);
let adapter, renderer, revision, configurationId, active = false, last = 0, failed = false, reportedTop, expression;
let view = { zoom: 1, x: 0, y: 0 };
// A still renderer (Martlet's touch zones picture, never on screen) never animates: its pictures show the model's rest pose.
let still = false;
const post = value => window.chrome.webview.postMessage(value);
// Percent-encodes every character but A-Z a-z 0-9 - . _ ~ (like .NET's Uri.EscapeDataString), so the host matches the
// request to the asset exactly for names such as 简.moc3 or "texture (1).png".
const encodePath = name => name.split("/").map(part => encodeURIComponent(part)
  .replace(/[!'()*]/g, c => "%" + c.charCodeAt(0).toString(16).toUpperCase())).join("/");
const resource = name => fetch(`asset/${encodePath(name)}`).then(response => {
  if (!response.ok) throw new Error(`Local resource ${name} unavailable.`);
  return response.arrayBuffer();
});
// Why a model couldn't load, for the host's log and the person choosing it; never a stack or a local path.
const reason = error => String(error?.message ?? error).replace(/[\u0000-\u001f]/g, " ").slice(0, 300);
const mouth = new Set(["aa", "ih", "ou", "ee", "oh"]);
const blink = new Set(["blink", "blinkLeft", "blinkRight"]);
// Where the face is in the page's CSS pixels (see overlay.js), or undefined.
const face = () => {
  const anchor = adapter?.faceAnchor?.();
  return anchor && toCssAnchor(anchor, canvas.clientWidth / Math.max(1, canvas.width), canvas.clientHeight / Math.max(1, canvas.height));
};
// One of Martlet's gestures: the model's own when it has it (Live2D's ParamCheek blush, a VRM's blush expression), otherwise
// one Martlet draws over the face (overlay.js). A held one stays until it is turned off. Held gestures layer: the adapter
// lets a held gesture go only when a new one moves a part of it (eyes, mouth, cheeks, brows, head), and held drawings all
// show together on top of whatever the model holds. One blush level shows at a time (replaceBlush), and the stronger levels
// are drawn over the model's own blush too, so each level looks different on every model.
function actGesture(name, on, hold) {
  if (!on) {
    adapter.endGesture(name);
    stopOverlay(name);
    return { started: true };
  }
  if (BLUSH_LEVELS.includes(name)) replaceBlush(name);
  if (renderer === "Live2D" ? adapter.gesture(name, hold) : adapter.playGesture(name, hold))
    return BLUSH_LEVELS.indexOf(name) > 0 ? drawOver(name, hold) : { started: true };
  if (!hasOverlay(name)) return { started: false };
  return drawOver(name, hold);
}
// Starts the overlay `name` over the face; the reply says where the face is and how it is followed.
function drawOver(name, hold) {
  const anchor = face();
  return { started: startOverlay(name, { hold }), overlay: true,
    face: anchor ? { x: Math.round(anchor.x), y: Math.round(anchor.y), width: Math.round(anchor.width),
      tilt: Math.round(anchor.angle * 180 / Math.PI), ...(anchor.tracking ? { tracking: anchor.tracking } : {}) } : null };
}
// A new blush level lets the other levels go, the model's own blush and Martlet's drawing alike.
function replaceBlush(name) {
  for (const other of BLUSH_LEVELS) if (other !== name) { adapter.endGesture(other); stopOverlay(other); }
}
// Where Martlet draws over the face now, as fractions of the canvas (+y down, like a tap) for Martlet's MCP: how the face is
// followed, its middle, width and tilt, and at each cheek how much of it shows, how wide it is for its face width and what of
// the character is there (the page's own hit test), the eye and mouth points and the top of the head the overlay emotes are
// drawn from, the measured cheeks' size (cheekSize, in face widths; null without a face measured by vision), with the
// overlays showing; and the eyes' irises and openings (see eyeReading).
function faceReading(id) {
  const anchor = face(), overlays = activeOverlays();
  const width = Math.max(1, canvas.clientWidth), height = Math.max(1, canvas.clientHeight);
  const round = value => Math.round(value * 10000) / 10000;
  const pinned = adapter?.faceTracking ? { carriers: adapter.faceTracking.carriers, skin: adapter.faceTracking.skin ?? null,
    milliseconds: adapter.faceTracking.milliseconds, eyeMilliseconds: adapter.faceTracking.eyeMilliseconds ?? 0 } : null;
  if (!anchor) return { id, found: false, overlays, pinned };
  const cheek = (point, frame) => {
    let hit;
    try { hit = adapter?.hitTest?.(point.x / width, point.y / height); } catch { hit = undefined; }
    return { x: round(point.x / width), y: round(point.y / height), visible: frame ? round(frame.visible) : null,
      across: frame ? round(Math.hypot(frame.right.x, frame.right.y) / anchor.width) : null, hit: !!hit,
      drawables: hit?.drawables?.slice(0, 3) ?? [], bone: hit?.bone ?? null, mesh: hit?.mesh ?? null };
  };
  const at = point => point ? { x: round(point.x / width), y: round(point.y / height) } : null;
  return { id, found: true, tracking: anchor.tracking ?? "estimate", x: round(anchor.x / width), y: round(anchor.y / height),
    width: round(anchor.width / width), tilt: Math.round(anchor.angle * 1800 / Math.PI) / 10,
    cheekLeft: cheek(anchor.cheekLeft, anchor.cheekLeftFrame), cheekRight: cheek(anchor.cheekRight, anchor.cheekRightFrame),
    eyeLeft: at(anchor.eyeLeft), eyeRight: at(anchor.eyeRight), mouth: at(anchor.mouth), top: at(anchor.top),
    cheekSize: anchor.cheekSize ?? null, overlays, pinned,
    ...eyeReading(anchor, width, height, round) };
}
// The eyes in a face reading: where they came from, each iris (x, y, rx, ry as fractions of the canvas: rx of its width, ry
// of its height) and each opening's box, how many points and triangles it has and whether its iris's middle is inside it
// (not every point).
function eyeReading(anchor, width, height, round) {
  const reading = { eyesFrom: anchor.eyesFrom ?? "estimate" };
  for (const [iris, shape] of [["irisLeft", "eyeLeftShape"], ["irisRight", "eyeRightShape"]]) {
    const i = anchor[iris], s = anchor[shape];
    reading[iris] = i ? { x: round(i.x / width), y: round(i.y / height), rx: round(i.rx / width), ry: round(i.ry / height) } : null;
    if (!s) { reading[shape] = null; continue; }
    const xs = s.points.map(p => p.x), ys = s.points.map(p => p.y);
    reading[shape] = { points: s.points.length, triangles: s.triangles ? s.triangles.length / 3 : null,
      ...(s.points.length ? { left: round(Math.min(...xs) / width), top: round(Math.min(...ys) / height),
        right: round(Math.max(...xs) / width), bottom: round(Math.max(...ys) / height) } : {}),
      irisInside: i ? insideEyeShape(s, i) : null };
  }
  return reading;
}
// Which gesture plays once and every one held: the model's held gestures, then the held drawings.
function gestureState() {
  const { playing, held } = adapter.gestureState ?? {};
  return { ...(playing ? { playing } : {}), held: [...new Set([...(Array.isArray(held) ? held : []), ...heldOverlays()])] };
}
// Where a hit's point of the character was in its rest pose (the pose the touch zones picture shows), traced on the touched
// mesh and drawn in the canvas's framing now (fractions, +y down, like the tap): the adapters' `restCanvas`. Touch zones
// compare it with their boxes, so a tap lands on the same zone however the head turns or the body moves. Null when the
// adapter can't say.
function restOf(hit) {
  const at = hit?.restCanvas;
  if (!at || !Number.isFinite(at.x) || !Number.isFinite(at.y)) return null;
  return { x: Math.round(at.x * 10000) / 10000, y: Math.round(at.y * 10000) / 10000 };
}
// The bones a pose reading places on the canvas.
const POSE_BONES = new Set(["head", "neck", "leftShoulder", "rightShoulder", "leftUpperArm", "rightUpperArm", "leftHand", "rightHand",
  "leftUpperLeg", "rightUpperLeg"]);
// Who moves the mouth now, for Martlet's MCP (character_mouth; the adapters' mouthReading): how much the voice has it (voice,
// 0 to 1), whether it moved it within the last second (speaking), its loudness (level), how far the emotes open the mouth
// before the voice takes it (emote), how far it is open now (open), the Live2D parameter read and how much a VRM's expressions
// showing block the voice's mouth (blocked).
function mouthReading(id) {
  const mouth = adapter?.mouthReading;
  if (!mouth) return { id, found: false, renderer: renderer ?? null };
  const round = value => Math.round(value * 10000) / 10000;
  return { id, found: true, renderer, ...(typeof mouth.parameter === "string" ? { parameter: mouth.parameter } : {}),
    voice: round(mouth.voice), speaking: mouth.speaking === true, level: round(mouth.level), emote: round(mouth.emote),
    open: round(mouth.open), ...(typeof mouth.blocked === "number" ? { blocked: round(mouth.blocked) } : {}) };
}
// Where each area of the touch zones is now (see zones.js), as fractions of the page: Live2D drawables as they are drawn now,
// VRM bones and spring-bone joints (by their nodes' names) where they are now, and the view's zoom and pan.
function zonePlaces() {
  return zoneBoxes({
    drawables: () => renderer === "Live2D" ? new Map(adapter.drawableBounds().map(d => [d.id, d])) : new Map(),
    points: () => renderer === "Vrm"
      ? new Map([...adapter.bonePoints().map(b => [b.bone, b]), ...(adapter.nodePoints?.() ?? []).map(n => [n.node, n])]) : new Map(),
    frame: view,
  });
}
// The touch zones for Martlet's MCP (character_zones): whether there are any, whether they are drawn, and each area's zone, its
// place among the zone's areas, what placed it and its box now (fractions of the page, +y down).
function zonesReading(id) {
  const round = value => Math.round(value * 10000) / 10000;
  if (zoneAreas() === 0) return { id, found: false, renderer: renderer ?? null, draw: false, areas: [] };
  return { id, found: true, renderer, draw: zonesDrawn(), areas: zonePlaces().map(box => ({ zone: box.zone, area: box.area, from: box.from,
    left: round(box.left), top: round(box.top), right: round(box.right), bottom: round(box.bottom) })) };
}
// Loudness levels MCP's character_mouth plays on the mouth the way Martlet's voice moves it, without a sound: each with when it
// is due after the first (ms), applied as frames are drawn (see playVoice).
let voiceLevels = [], voiceStart;
// Applies the levels due by `now` (the frame clock, ms); a level the adapter refuses ends them.
function playVoice(now) {
  if (!voiceLevels.length) return;
  voiceStart ??= now;
  while (voiceLevels.length && voiceLevels[0].at <= now - voiceStart) {
    const { level } = voiceLevels.shift();
    try {
      if (!(level >= 0 && level <= 1)) throw new Error("Invalid mouth level.");
      adapter.setLipSync(level);
    } catch { voiceLevels = []; }
  }
}
// What a VRM's idle body does now, for Martlet's MCP (character_pose): its breath, each arm's hang and elbow bend, how far the
// fingers curl and the sway (the runtime's idleReading), and where its head, shoulders, hands and hips are, as fractions of the
// canvas (+y down). A Live2D model's own breathing isn't read.
function poseReading(id) {
  const idle = renderer === "Vrm" ? adapter?.idleReading : undefined;
  if (!idle) return { id, found: false, renderer: renderer ?? null };
  const round = value => Math.round(value * 10000) / 10000;
  const bones = Object.fromEntries(adapter.bonePoints().filter(b => POSE_BONES.has(b.bone))
    .map(b => [b.bone, { x: round(b.x), y: round(b.y) }]));
  return { id, found: true, renderer, ...idle, bones };
}
// A picture of the whole character for touch zones, drawn on the canvas and read back in the same task, so it never shows on
// screen: `width` by `height` pixels in the framing `zoom`, `x`, `y` of a frame `frame` of its width (see setView), in the
// pose the model has now (its rest pose in a still renderer). The PNG (a data URL), where the drawables (Live2D, each with the
// ID of its part) or the humanoid bones (VRM) are in it, with a Live2D model's own parts (their names from its DisplayInfo file,
// and their parents; none when they can't be read), with `chains` its parts that swing on their own (a tail, a ponytail: the
// drawables each physics setting moves, root first, and where they can reach; see the adapter's swingingChains) or a VRM's
// `springs` (its spring-bone chains: each joint's node and place, root first; see the adapter's springChains), and where
// the face anchor puts the face (`face`: its middle, its width as a fraction of the picture's width and its roll; Martlet's
// eye measurement crops around it), as fractions of the picture; nothing when it can't be drawn. The canvas then goes back to
// its own size and framing, drawn again at once, so a showing character never changes.
function picture({ width, height, zoom, x, y, frame, chains: measure }) {
  const size = [canvas.width, canvas.height];
  const resize = (w, h) => { if (renderer === "Vrm") adapter.resize(w, h); else { canvas.width = w; canvas.height = h; } };
  const round = value => Math.round(value * 10000) / 10000;
  const parts = () => { try { return adapter.modelParts?.() ?? []; } catch { return []; } };
  // Measured only when asked (once per picture of the character); a model whose physics can't be measured swings nothing, and
  // a model with no swinging parts says nothing of them.
  const chains = () => {
    if (measure === false) return {};
    try {
      const swinging = (adapter.swingingChains?.() ?? []).map(c => ({ ...(c.name ? { name: c.name } : {}), drawables: [...c.drawables],
        left: round(c.left), top: round(c.top), right: round(c.right), bottom: round(c.bottom) }));
      return swinging.length > 0 ? { chains: swinging } : {};
    } catch { return {}; }
  };
  // A VRM's spring-bone chains cost nothing to read, so every picture has them in its own framing; a failure only leaves them out.
  const springs = () => {
    try {
      const chains = (adapter.springChains?.() ?? []).map(s => ({ ...(s.name ? { name: s.name } : {}),
        joints: s.joints.map(j => ({ bone: j.bone, x: round(j.x), y: round(j.y) })) }));
      return chains.length > 0 ? { springs: chains } : {};
    } catch { return {}; }
  };
  try {
    resize(Number(width), Number(height));
    adapter.setView(Number(zoom), Number(x), Number(y), Number(frame));
    adapter.update(0);
    const png = canvas.toDataURL("image/png");
    let face;
    try {
      const anchor = adapter.faceAnchor?.();
      if (anchor && [anchor.x, anchor.y, anchor.width].every(Number.isFinite) && anchor.width > 0)
        face = { x: round(anchor.x / canvas.width), y: round(anchor.y / canvas.height), width: round(anchor.width / canvas.width),
          angle: round(Number.isFinite(anchor.angle) ? anchor.angle : 0), ...(typeof anchor.tracking === "string" ? { tracking: anchor.tracking } : {}) };
    } catch { face = undefined; }
    return renderer === "Live2D"
      ? { png, drawables: adapter.drawableBounds().map(d => ({ id: d.id, left: round(d.left), top: round(d.top), right: round(d.right),
          bottom: round(d.bottom), ...(d.part ? { part: d.part } : {}) })), parts: parts(), ...chains(), ...(face ? { face } : {}) }
      : { png, bones: adapter.bonePoints().map(b => ({ bone: b.bone, x: round(b.x), y: round(b.y) })), ...springs(), ...(face ? { face } : {}) };
  } catch { return {}; }
  finally {
    try {
      resize(size[0], size[1]);
      adapter.setView(view.zoom, view.x, view.y, view.frame ?? 1);
      if (!still) adapter.update(0);
    } catch { }
  }
}
window.chrome.webview.addEventListener("message", async ({ data: message }) => {
  if (message.kind === "look") {
    // Fire-and-forget cursor follow from the host window; never replies and never fails the renderer.
    try { if (active && !failed) adapter?.setLook?.(message.data.x, message.data.y); } catch { }
    return;
  }
  if (message.kind === "view") {
    // Fire-and-forget camera zoom/pan from the host window; never replies and never fails the renderer.
    try { view = message.data; if (active && !failed) adapter?.setView?.(view.zoom, view.x, view.y, view.frame ?? 1); } catch { }
    return;
  }
  if (message.kind === "touch") {
    // A tap on the character at x, y (fractions 0..1 of the canvas, +y down). Answered unprompted with {touch}, never as a
    // command reply, and a failed hit test is only a miss: Live2D reports hitAreas and drawables, VRM bone, node, hair,
    // mesh and material, and both `rest` (see restOf).
    const { id, x, y } = message.data;
    let hit;
    try { if (active && !failed) hit = adapter?.hitTest?.(Number(x), Number(y)); } catch { hit = undefined; }
    post({ touch: { id, hit: !!hit, hitAreas: hit?.hitAreas ?? [], drawables: hit?.drawables ?? [], bone: hit?.bone ?? null,
      node: hit?.node ?? null, hair: hit?.hair === true, mesh: hit?.mesh ?? null, material: hit?.material ?? null, rest: restOf(hit) } });
    return;
  }
  if (message.kind === "touches") {
    // A batch of points (a stroke's path, or what a zoom closed in on), hit tested like "touch" and answered unprompted with
    // {touches: {id, hits}} in the same order; each failed test is only a miss.
    const { id, points } = message.data;
    const hits = (Array.isArray(points) ? points.slice(0, 64) : []).map(({ x, y }) => {
      let hit;
      try { if (active && !failed) hit = adapter?.hitTest?.(Number(x), Number(y)); } catch { hit = undefined; }
      return { hit: !!hit, hitAreas: hit?.hitAreas ?? [], drawables: hit?.drawables ?? [], bone: hit?.bone ?? null,
        node: hit?.node ?? null, hair: hit?.hair === true, mesh: hit?.mesh ?? null, material: hit?.material ?? null, rest: restOf(hit) };
    });
    post({ touches: { id, hits } });
    return;
  }
  if (message.kind === "face") {
    // Where Martlet draws over the face (see faceReading), answered unprompted with {faceReading}, never as a command reply
    // (an action's reply has its own "face"); a reading that fails is only "not found" and never fails the renderer.
    const { id } = message.data;
    let reading = { id, found: false };
    try { if (active && !failed) reading = faceReading(id); } catch { }
    post({ faceReading: reading });
    return;
  }
  if (message.kind === "pose") {
    // What the idle body does now (see poseReading), answered unprompted with {poseReading}, never as a command reply; a
    // reading that fails is only "not found" and never fails the renderer.
    const { id } = message.data;
    let reading = { id, found: false };
    try { if (active && !failed) reading = poseReading(id); } catch { }
    post({ poseReading: reading });
    return;
  }
  if (message.kind === "mouthState") {
    // Who moves the mouth now (see mouthReading), answered unprompted with {mouthReading}, never as a command reply; a reading
    // that fails is only "not found" and never fails the renderer.
    const { id } = message.data;
    let reading = { id, found: false };
    try { if (active && !failed) reading = mouthReading(id); } catch { }
    post({ mouthReading: reading });
    return;
  }
  if (message.kind === "zoneview") {
    // The touch zones Martlet gives (see zones.js) and whether to draw them; answered with how many areas were taken. Zones
    // that can't be used are only none, and never fail the renderer.
    let shown = 0;
    try { shown = setZoneView(message.data); } catch { setZoneView(null); }
    post({ shown });
    return;
  }
  if (message.kind === "zonesRead") {
    // Where each area of the touch zones is now (see zonesReading), answered unprompted with {zonesReading}, never as a command
    // reply; a reading that fails is only "not found" and never fails the renderer.
    const { id } = message.data;
    let reading = { id, found: false };
    try { if (active && !failed) reading = zonesReading(id); } catch { }
    post({ zonesReading: reading });
    return;
  }
  if (message.kind === "voiceLevels") {
    // Fire-and-forget, never a command reply: levels (0 to 1, at most 400) that move the mouth as Martlet's voice does, one
    // every stepMs (10 to 1000) from the next frame, replacing any still to come.
    const { levels, stepMs } = message.data ?? {};
    const step = Math.max(10, Math.min(1000, Number(stepMs) || 50));
    voiceLevels = (Array.isArray(levels) ? levels.slice(0, 400) : []).map((level, i) => ({ level: Number(level), at: i * step }));
    voiceStart = undefined;
    return;
  }
  if (message.kind === "eyes") {
    // The eyes measured by vision ({left, right}, see the adapters' setEyeHint) or null to clear them. Answered with where the
    // eyes come from now ({eyesFrom}; null while no model shows); a hint the adapter can't use is ignored and never fails the
    // renderer.
    let eyesFrom = null;
    try { if (active && !failed && adapter?.setEyeHint) eyesFrom = adapter.setEyeHint(message.data ?? undefined) ?? null; } catch { }
    try { if (eyesFrom === null && active && !failed) eyesFrom = adapter?.eyesFrom ?? null; } catch { }
    post({ eyesFrom });
    return;
  }
  try {
    if (failed) throw new Error("Renderer is terminally failed; inspect again.");
    const data = message.data;
    if (message.kind === "load") {
      renderer = data.renderer;
      still = data.still === true;
      if (renderer === "Live2D") {
        await new Promise((resolve, reject) => {
          const script = document.createElement("script");
          script.src = "asset/core.js"; script.onload = resolve; script.onerror = reject;
          document.head.append(script);
        });
        const { sdk } = await import(/* @vite-ignore */ "./asset/sdk.js");
        adapter = new Live2DAdapter(canvas, { sdk, onDiagnostic: diagnostic => {
          if (diagnostic.code === "CONTEXT_LOST") { active = false; failed = true; post({ error: "avatar.context_lost" }); }
        } });
        const files = new Map();
        for (const name of data.assets.filter(name => !["core.js", "sdk.js"].includes(name)))
          files.set(name, new Uint8Array(await resource(name)));
        const capabilities = await adapter.load(new LocalModelBundle(files, data.modelFile, data.extras ?? undefined));
        post({ modelId: data.resourceRevision.toLowerCase(), parameters: capabilities.parameters.map(p => ({
          id: p.id, minimum: p.minimum, maximum: p.maximum, neutral: p.neutral,
          aspects: p.groups.includes("LipSync") ? ["Mouth"] : p.groups.includes("EyeBlink") ? ["Expression"] : ["Mouth", "Expression"]
        })), model: adapter.modelSummary });
      } else if (renderer === "Vrm") {
        adapter = new VrmAvatarAdapter(canvas);
        const capabilities = await adapter.load(await resource(data.modelFile));
        adapter.startIdle?.();
        post({ modelId: data.resourceRevision.toLowerCase(), parameters: capabilities.expressions.filter(p => p.usable &&
          !p.name.startsWith("look")).map(p => ({ id: p.name, minimum: 0, maximum: 1, neutral: 0,
          aspects: [mouth.has(p.name) ? "Mouth" : "Expression"] })) });
      } else throw new Error("Unsupported renderer.");
      try { adapter.setView(view.zoom, view.x, view.y, view.frame ?? 1); } catch { }
      active = true;
    } else if (message.kind === "configure") {
      revision = data;
      if (renderer === "Live2D") {
        adapter.configureTargets(data.targets.map(t => t.target));
        configurationId = adapter.configurationId;
      } else {
        adapter.configure({ faceSource: data.sourceId, faceMode: "authored-explicit",
          mappings: data.targets.map((t, index) => ({ channel: `target${index}`, expression: t.target,
            aspect: t.aspect === "Mouth" ? "mouth" : blink.has(t.target) ? "blink" : "expression", minimum: 0, maximum: 1 })),
          gaze: false, head: false, secondaryMotion: false },
        { modelRevision: data.modelRevision, mappingRevision: data.mappingRevision });
      }
      post({});
    } else if (message.kind === "reset") {
      if (renderer === "Live2D") adapter.resetEpoch(data); else adapter.reset(data);
      post({});
    } else if (message.kind === "apply") {
      if (!revision || data.modelRevision !== revision.modelRevision || data.mappingRevision !== revision.mappingRevision)
        throw new Error("Stale mapping.");
      if (renderer === "Live2D") {
        const result = adapter.applyComposedParameters({ identity: data.identity, sequence: data.sequence,
          configurationId, parameters: data.parameters });
        if (!result.accepted) throw new Error("Frame rejected.");
      } else {
        const result = adapter.applyComposedParameters(data, data.actualPlaybackSampleOffset);
        if (result === false) throw new Error("Frame rejected.");
      }
      post({});
    } else if (message.kind === "stop") { adapter.stop(); post({}); }
    else if (message.kind === "picture") post(picture(data));
    else if (message.kind === "zones") {
      // Where the model's drawables (Live2D) or humanoid bones (VRM) are now, as fractions of the page, for touch zones.
      const rect = canvas.getBoundingClientRect(), pageWidth = Math.max(1, window.innerWidth), pageHeight = Math.max(1, window.innerHeight);
      const px = x => (rect.left + x * rect.width) / pageWidth, py = y => (rect.top + y * rect.height) / pageHeight;
      const round = value => Math.round(value * 10000) / 10000;
      // A probe that fails never fails the character; the reply is then empty.
      try {
        if (renderer === "Live2D") post({ drawables: adapter.drawableBounds().map(d => ({ id: d.id,
          left: round(px(d.left)), top: round(py(d.top)), right: round(px(d.right)), bottom: round(py(d.bottom)) })) });
        else post({ bones: adapter.bonePoints().map(b => ({ bone: b.bone, x: round(px(b.x)), y: round(py(b.y)) })) });
      } catch { post({}); }
    }
    else if (message.kind === "mouth") {
      const level = Number(data.level);
      if (!Number.isFinite(level) || level < 0 || level > 1) throw new Error("Invalid mouth level.");
      adapter.setLipSync(level);
      post({});
    } else if (message.kind === "motion") {
      post({ started: renderer === "Live2D" ? adapter.playMotion(String(data.group)) : false });
    } else if (message.kind === "action") {
      // An emote (expression, held until ended or replaced), a motion (played once) or a gesture (played once, or with
      // `hold` a holdable one kept until ended; drawn over the face when the model can't show it, see actGesture). Ending
      // an expression that isn't the one showing changes nothing. A gesture's reply also says which gesture now plays once
      // and every one held (`gesture: {playing, held: [...]}`). A held (lingering) expression stays on, layered with the
      // other held ones and the passing emote, until it is ended with hold set too.
      const kind = String(data.kind), name = String(data.name), on = data.on !== false, hold = data.hold === true;
      let started = false;
      if (kind === "gesture") { post({ ...actGesture(name, on, hold), gesture: gestureState() }); return; }
      else if (kind === "motion") started = on && renderer === "Live2D" ? adapter.playMotion(name) : false;
      else if (kind === "expression") {
        if (renderer === "Live2D") {
          if (hold) started = adapter.holdExpression(name, on);
          else if (on) { started = adapter.setExpression(name); if (started) expression = name; }
          else if (expression === name) { started = adapter.setExpression(null); expression = undefined; }
        } else started = adapter.setAction(name, on, hold);
      }
      post({ started });
    }
    else throw new Error("Unsupported command.");
  } catch (error) {
    active = false; failed = true;
    clearOverlays();
    try { adapter?.dispose(); }
    finally { post(message.kind === "load" ? { error: "avatar.model_rejected", detail: reason(error) } : { error: "avatar.renderer_rejected" }); }
  }
});
function draw(now) {
  if (active && !still && !failed && adapter) {
    playVoice(now);
    try {
      const ratio = Math.min(2048 / Math.max(1, canvas.clientWidth, canvas.clientHeight), window.devicePixelRatio || 1);
      const width = Math.min(2048, Math.max(1, Math.round(canvas.clientWidth * ratio)));
      const height = Math.min(2048, Math.max(1, Math.round(canvas.clientHeight * ratio)));
      if (renderer === "Vrm") adapter.resize(width, height);
      else if (canvas.width !== width || canvas.height !== height) { canvas.width = width; canvas.height = height; }
      adapter.update(Math.min(0.1, last ? (now - last) / 1000 : 0));
    } catch { failed = true; active = false; clearOverlays(); post({ error: "avatar.renderer_failed" }); }
    // Martlet's drawings over the face; only looks for the face while one shows.
    try { if (!failed) renderOverlay(activeOverlays().length ? face() : undefined, Math.min(0.1, last ? (now - last) / 1000 : 0)); }
    catch { clearOverlays(); }
    // The touch zones over the character, where their parts are now, while Martlet has them drawn.
    try { if (!failed) renderZones(zonesDrawn() ? zonePlaces() : []); }
    catch { setZoneView(null); }
    // Unsolicited, fire-and-forget: where the top of the head sits, so the host's zoom keeps it in view.
    try {
      const top = adapter.contentTop;
      if (!failed && Number.isFinite(top) && !(Math.abs(top - reportedTop) < 0.002)) { reportedTop = top; post({ bounds: { top } }); }
    } catch { }
  }
  last = now;
  requestAnimationFrame(draw);
}
requestAnimationFrame(draw);
post({ ready: true });
