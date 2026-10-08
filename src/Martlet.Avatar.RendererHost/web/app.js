import { Live2DAdapter, LocalModelBundle } from "../../Martlet.Avatar.Live2D/lib/index.ts";
import { VrmAvatarAdapter } from "../../Martlet.Avatar.Vrm/src/index.ts";
import { activeOverlays, attachOverlay, clearOverlays, hasOverlay, heldOverlays, registerBlush, registerOverlay, renderOverlay,
  startOverlay, stopOverlay, toCssAnchor } from "./overlay.js";
import { registerManpu } from "./effects/manpu.mjs";

const canvas = document.getElementById("avatar");
attachOverlay(document.getElementById("overlay"));
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
// show together on top of whatever the model holds.
function actGesture(name, on, hold) {
  if (!on) {
    adapter.endGesture(name);
    stopOverlay(name);
    return { started: true };
  }
  if (renderer === "Live2D" ? adapter.gesture(name, hold) : adapter.playGesture(name, hold)) return { started: true };
  if (!hasOverlay(name)) return { started: false };
  const anchor = face();
  return { started: startOverlay(name, { hold }), overlay: true,
    face: anchor ? { x: Math.round(anchor.x), y: Math.round(anchor.y), width: Math.round(anchor.width),
      tilt: Math.round(anchor.angle * 180 / Math.PI), ...(anchor.tracking ? { tracking: anchor.tracking } : {}) } : null };
}
// Where Martlet draws over the face now, as fractions of the canvas (+y down, like a tap) for Martlet's MCP: how the face is
// followed, its middle, width and tilt, and at each cheek how much of it shows, how wide it is for its face width and what of
// the character is there (the page's own hit test), with the overlays showing.
function faceReading(id) {
  const anchor = face(), overlays = activeOverlays();
  const width = Math.max(1, canvas.clientWidth), height = Math.max(1, canvas.clientHeight);
  const round = value => Math.round(value * 10000) / 10000;
  const pinned = adapter?.faceTracking ? { carriers: adapter.faceTracking.carriers, milliseconds: adapter.faceTracking.milliseconds } : null;
  if (!anchor) return { id, found: false, overlays, pinned };
  const cheek = (point, frame) => {
    let hit;
    try { hit = adapter?.hitTest?.(point.x / width, point.y / height); } catch { hit = undefined; }
    return { x: round(point.x / width), y: round(point.y / height), visible: frame ? round(frame.visible) : null,
      across: frame ? round(Math.hypot(frame.right.x, frame.right.y) / anchor.width) : null, hit: !!hit,
      drawables: hit?.drawables?.slice(0, 3) ?? [], bone: hit?.bone ?? null, mesh: hit?.mesh ?? null };
  };
  return { id, found: true, tracking: anchor.tracking ?? "estimate", x: round(anchor.x / width), y: round(anchor.y / height),
    width: round(anchor.width / width), tilt: Math.round(anchor.angle * 1800 / Math.PI) / 10,
    cheekLeft: cheek(anchor.cheekLeft, anchor.cheekLeftFrame), cheekRight: cheek(anchor.cheekRight, anchor.cheekRightFrame),
    overlays, pinned };
}
// Which gesture plays once and every one held: the model's held gestures, then the held drawings.
function gestureState() {
  const { playing, held } = adapter.gestureState ?? {};
  return { ...(playing ? { playing } : {}), held: [...new Set([...(Array.isArray(held) ? held : []), ...heldOverlays()])] };
}
// A picture of the whole character for touch zones, drawn on the canvas and read back in the same task, so it never shows on
// screen: `width` by `height` pixels in the framing `zoom`, `x`, `y` of a frame `frame` of its width (see setView), in the
// pose the model has now (its rest pose in a still renderer). The PNG (a data URL) and where the drawables (Live2D, each with
// the ID of its part) or the humanoid bones (VRM) are in it, as fractions of the picture, with a Live2D model's own parts (their
// names from its DisplayInfo file, and their parents; none when they can't be read); nothing when it can't be drawn. The canvas
// then goes back to its own size and framing, drawn again at once, so a showing character never changes.
function picture({ width, height, zoom, x, y, frame }) {
  const size = [canvas.width, canvas.height];
  const resize = (w, h) => { if (renderer === "Vrm") adapter.resize(w, h); else { canvas.width = w; canvas.height = h; } };
  const round = value => Math.round(value * 10000) / 10000;
  const parts = () => { try { return adapter.modelParts?.() ?? []; } catch { return []; } };
  try {
    resize(Number(width), Number(height));
    adapter.setView(Number(zoom), Number(x), Number(y), Number(frame));
    adapter.update(0);
    const png = canvas.toDataURL("image/png");
    return renderer === "Live2D"
      ? { png, drawables: adapter.drawableBounds().map(d => ({ id: d.id, left: round(d.left), top: round(d.top), right: round(d.right),
          bottom: round(d.bottom), ...(d.part ? { part: d.part } : {}) })), parts: parts() }
      : { png, bones: adapter.bonePoints().map(b => ({ bone: b.bone, x: round(b.x), y: round(b.y) })) };
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
    // mesh and material.
    const { id, x, y } = message.data;
    let hit;
    try { if (active && !failed) hit = adapter?.hitTest?.(Number(x), Number(y)); } catch { hit = undefined; }
    post({ touch: { id, hit: !!hit, hitAreas: hit?.hitAreas ?? [], drawables: hit?.drawables ?? [], bone: hit?.bone ?? null,
      node: hit?.node ?? null, hair: hit?.hair === true, mesh: hit?.mesh ?? null, material: hit?.material ?? null } });
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
        node: hit?.node ?? null, hair: hit?.hair === true, mesh: hit?.mesh ?? null, material: hit?.material ?? null };
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
