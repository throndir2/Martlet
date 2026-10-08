// Martlet's own drawings over the character (a blush glow, manga symbols...), on a 2D canvas laid exactly over the avatar
// canvas. Each overlay is registered once with how long it lasts and how it draws around the face anchor the adapter
// reports; the layer fades it in and out and redraws every frame.
//
// anchor: { x, y, width, angle, cheekLeft:{x,y}, cheekRight:{x,y}, eyeLeft?, eyeRight?, mouth?, top?, tracking?,
// cheekLeftFrame?, cheekRightFrame?, irisLeft?, irisRight?, eyeLeftShape?, eyeRightShape?, eyesFrom? } in CSS pixels of the
// page; x/y is the face's middle, width the face's width (zoom included), angle its roll in radians (clockwise on screen),
// Left/Right the viewer's left and right. The adapters read it from what they draw each frame (`tracking`: "mesh" when pinned
// to a Live2D model's own meshes, "bones" from a VRM's head bone, "estimate" from Live2D's head angles), so it follows every
// move of the head. A cheek's frame is how its surface shows now: `right` and `down`, the steps for one face width across and
// down it (a turned head squashes the far cheek), and `visible`, 0 to 1 as it turns away.
//
// The eyes: `irisLeft`/`irisRight` ({x, y, rx, ry}) is an eye's iris now (the coloured part with the pupil): its middle and
// its radii across and down the face. `eyeLeftShape`/`eyeRightShape` ({points: [{x, y}...], triangles?}) is the eye's visible
// opening between the eyelids now, following blinks and the head: the union of `triangles` (three indices into `points` for
// each), or without them the closed outline through `points`; no points when the eye is closed or hidden. At most
// EYE_SHAPE_POINTS points and EYE_SHAPE_TRIANGLES triangles; a shape over them, or with a number that isn't finite, is
// dropped. `eyesFrom` says where they came from, for both eyes the weakest: "mesh" (a Live2D model's iris and eye-white
// meshes), "bones" (a VRM's eye bones with its iris and eye-white meshes), "vision" (the eyes measured by vision fill what
// the model can't give) or "estimate" (an eye has neither: its iris and shape are left out, and drawings over the eyes use
// their own estimate around `eyeLeft`/`eyeRight`). An eye with an iris also has `eyeLeft`/`eyeRight` at its middle.

const registry = new Map();
const active = new Map();
let canvas, context, painted = false;

const smooth = x => { const c = Math.max(0, Math.min(1, x)); return c * c * (3 - 2 * c); };

/** Registers (or replaces) an overlay: `duration` in seconds (Infinity to last until stopped), fades in seconds and
 *  `draw(ctx, anchor, t, weight)` with t the seconds since it started and weight its 0..1 fade. */
export function registerOverlay(name, { duration = 4, fadeIn = 0.6, fadeOut = 0.6, draw }) {
  if (typeof draw !== "function") throw new TypeError("An overlay needs a draw function.");
  registry.set(String(name), { duration: Math.max(0, Number(duration)), fadeIn: Math.max(0.01, fadeIn),
    fadeOut: Math.max(0.01, fadeOut), draw });
}

export const hasOverlay = name => registry.has(String(name));
export const overlayNames = () => [...registry.keys()];
/** The overlays showing now, including those fading out. */
export const activeOverlays = () => [...active.keys()];
/** The overlays held on (started with `hold` and not stopped). */
export const heldOverlays = () => [...active].filter(([, state]) => state.hold && !state.stop).map(([name]) => name);

/** Starts an overlay from the beginning; held, it stays until `stopOverlay`. False when no overlay has that name. */
export function startOverlay(name, { hold = false } = {}) {
  const overlay = registry.get(String(name));
  if (!overlay) return false;
  const running = active.get(String(name));
  // Restarting one that shows keeps its current strength instead of flashing off.
  const t = running ? Math.min(overlayWeight(running), 1) * overlay.fadeIn : 0;
  active.set(String(name), { overlay, t, hold: hold === true, stop: undefined });
  return true;
}

/** Fades an overlay out; true when it was showing. */
export function stopOverlay(name) {
  const state = active.get(String(name));
  if (!state) return false;
  if (state.stop === undefined) state.stop = { at: state.t, from: overlayWeight(state) };
  state.hold = false;
  return true;
}

export function clearOverlays() { active.clear(); }

/** An overlay's 0..1 strength now (exported for tests). */
export function overlayWeight(state) {
  const { overlay, t, hold, stop } = state;
  if (stop) return stop.from * (1 - smooth((t - stop.at) / overlay.fadeOut));
  const rise = smooth(t / overlay.fadeIn);
  if (hold || !Number.isFinite(overlay.duration)) return rise;
  return Math.min(rise, smooth((overlay.duration - t) / overlay.fadeOut));
}

/** Advances every overlay by `deltaSeconds` and draws them on `ctx` (already in CSS pixels) around `anchor`. */
export function drawOverlays(ctx, anchor, deltaSeconds) {
  for (const [name, state] of active) {
    state.t += Math.max(0, deltaSeconds || 0);
    const { overlay, t, hold, stop } = state;
    if (stop ? t - stop.at >= overlay.fadeOut : !hold && t >= overlay.duration) { active.delete(name); continue; }
    const weight = overlayWeight(state);
    if (!anchor || !ctx || weight <= 0) continue;
    ctx.save();
    try { overlay.draw(ctx, anchor, t, weight); } catch { active.delete(name); }
    finally { ctx.restore(); }
  }
}

/** Uses `target` (a canvas over the avatar canvas) for the overlays. */
export function attachOverlay(target) { canvas = target; context = target?.getContext?.("2d") ?? undefined; }

/** Sizes the overlay canvas to its avatar canvas, clears it and draws the overlays; `anchor` is in CSS pixels. */
export function renderOverlay(anchor, deltaSeconds) {
  if (!canvas || !context) { drawOverlays(undefined, undefined, deltaSeconds); return; }
  if (active.size === 0 && !painted) return;
  const ratio = Math.min(2048 / Math.max(1, canvas.clientWidth, canvas.clientHeight), globalThis.devicePixelRatio || 1);
  const width = Math.min(2048, Math.max(1, Math.round(canvas.clientWidth * ratio)));
  const height = Math.min(2048, Math.max(1, Math.round(canvas.clientHeight * ratio)));
  if (canvas.width !== width || canvas.height !== height) { canvas.width = width; canvas.height = height; }
  context.setTransform(1, 0, 0, 1, 0, 0);
  context.clearRect(0, 0, width, height);
  painted = active.size > 0;
  if (!painted) return;
  context.setTransform(width / Math.max(1, canvas.clientWidth), 0, 0, height / Math.max(1, canvas.clientHeight), 0, 0);
  drawOverlays(context, anchor, deltaSeconds);
}

/** The anchor `face` from an adapter (in the avatar canvas's drawing-buffer pixels) in CSS pixels. */
export function toCssAnchor(face, scaleX, scaleY) {
  if (!face) return undefined;
  const point = p => p && { x: p.x * scaleX, y: p.y * scaleY };
  const anchor = { x: face.x * scaleX, y: face.y * scaleY, width: face.width * scaleX, angle: face.angle ?? 0,
    cheekLeft: point(face.cheekLeft), cheekRight: point(face.cheekRight) };
  for (const key of ["eyeLeft", "eyeRight", "mouth", "top"]) if (face[key]) anchor[key] = point(face[key]);
  for (const key of ["cheekLeftFrame", "cheekRightFrame"]) {
    const frame = face[key];
    if (frame?.right && frame?.down && [frame.right.x, frame.right.y, frame.down.x, frame.down.y].every(Number.isFinite))
      anchor[key] = { right: point(frame.right), down: point(frame.down),
        visible: Number.isFinite(frame.visible) ? Math.max(0, Math.min(1, frame.visible)) : 1 };
  }
  if (typeof face.tracking === "string") anchor.tracking = face.tracking;
  addEyes(anchor, face, scaleX, scaleY);
  return [anchor.x, anchor.y, anchor.width].every(Number.isFinite) && anchor.width > 0 ? anchor : undefined;
}

/** The most points and triangles of one eye's shape (see the header). */
export const EYE_SHAPE_POINTS = 512, EYE_SHAPE_TRIANGLES = 1024;
const EYES_FROM = new Set(["mesh", "bones", "vision", "estimate"]);

/** Copies the eye fields (see the header) of adapter anchor `face` onto `anchor` in CSS pixels, dropping any that aren't
 *  finite or break the caps. */
function addEyes(anchor, face, scaleX, scaleY) {
  for (const key of ["irisLeft", "irisRight"]) {
    const iris = face[key];
    if (iris && [iris.x, iris.y, iris.rx, iris.ry].every(Number.isFinite) && iris.rx > 0 && iris.ry > 0)
      anchor[key] = { x: iris.x * scaleX, y: iris.y * scaleY, rx: iris.rx * scaleX, ry: iris.ry * scaleY };
  }
  for (const key of ["eyeLeftShape", "eyeRightShape"]) {
    const shape = cssShape(face[key], scaleX, scaleY);
    if (shape) anchor[key] = shape;
  }
  if (EYES_FROM.has(face.eyesFrom)) anchor.eyesFrom = face.eyesFrom;
}

function cssShape(shape, scaleX, scaleY) {
  const points = shape?.points;
  if (!Array.isArray(points) || points.length > EYE_SHAPE_POINTS) return undefined;
  const css = new Array(points.length);
  for (let i = 0; i < points.length; i++) {
    const p = points[i];
    if (!p || !Number.isFinite(p.x) || !Number.isFinite(p.y)) return undefined;
    css[i] = { x: p.x * scaleX, y: p.y * scaleY };
  }
  const triangles = shape.triangles;
  if (triangles === undefined) return { points: css };
  if (!Array.isArray(triangles) || triangles.length % 3 !== 0 || triangles.length > 3 * EYE_SHAPE_TRIANGLES) return undefined;
  for (let i = 0; i < triangles.length; i++)
    if (!Number.isInteger(triangles[i]) || triangles[i] < 0 || triangles[i] >= css.length) return undefined;
  return { points: css, triangles };
}

/** Whether `point` lies inside an eye's shape (see the header): in one of its triangles or inside its outline. */
export function insideEyeShape(shape, point) {
  const points = shape?.points;
  if (!point || !Array.isArray(points) || points.length < 3) return false;
  const side = (a, b) => (b.x - a.x) * (point.y - a.y) - (b.y - a.y) * (point.x - a.x);
  if (shape.triangles) {
    for (let k = 0; k + 2 < shape.triangles.length; k += 3) {
      const a = points[shape.triangles[k]], b = points[shape.triangles[k + 1]], c = points[shape.triangles[k + 2]];
      const s1 = side(a, b), s2 = side(b, c), s3 = side(c, a);
      if ((s1 >= 0 && s2 >= 0 && s3 >= 0) || (s1 <= 0 && s2 <= 0 && s3 <= 0)) return true;
    }
    return false;
  }
  let inside = false;
  for (let i = 0, j = points.length - 1; i < points.length; j = i++) {
    const a = points[i], b = points[j];
    if ((a.y > point.y) !== (b.y > point.y) && point.x < (b.x - a.x) * (point.y - a.y) / (b.y - a.y) + a.x) inside = !inside;
  }
  return inside;
}

// ---------- the blush ----------

/** Soft pink glows on both cheeks with a few faint diagonal strokes, anime style; the fallback for models without a blush
 *  of their own. Each glow is laid on its cheek's surface (its frame), so it turns, tilts and squashes with the face and
 *  fades as the cheek turns away; without a frame it turns with the face's roll. */
export function drawBlush(ctx, anchor, _t, weight) {
  const radius = anchor.width * 0.17;
  const c = Math.cos(anchor.angle || 0), s = Math.sin(anchor.angle || 0);
  for (const [cheek, frame] of [[anchor.cheekLeft, anchor.cheekLeftFrame], [anchor.cheekRight, anchor.cheekRightFrame]]) {
    if (!cheek) continue;
    const shown = frame ? frame.visible ?? 1 : 1;
    if (!(shown > 0.01)) continue;
    // The cheek's steps across and down for one pixel of face width.
    const across = frame ? { x: frame.right.x / anchor.width, y: frame.right.y / anchor.width } : { x: c, y: s };
    const down = frame ? { x: frame.down.x / anchor.width, y: frame.down.y / anchor.width } : { x: -s, y: c };
    const onCheek = () => { ctx.translate(cheek.x, cheek.y); ctx.transform(across.x, across.y, down.x, down.y, 0, 0); };
    ctx.globalAlpha = 0.5 * weight * Math.min(1, shown);
    ctx.save();
    onCheek();
    ctx.scale(1, 0.62);
    const glow = ctx.createRadialGradient(0, 0, 0, 0, 0, radius);
    glow.addColorStop(0, "rgba(255, 92, 128, 0.85)");
    glow.addColorStop(0.55, "rgba(255, 110, 140, 0.45)");
    glow.addColorStop(1, "rgba(255, 130, 150, 0)");
    ctx.fillStyle = glow;
    ctx.beginPath(); ctx.arc(0, 0, radius, 0, Math.PI * 2); ctx.fill();
    ctx.restore();
    ctx.save();
    onCheek();
    ctx.strokeStyle = "rgba(225, 60, 95, 0.55)";
    ctx.lineCap = "round";
    ctx.lineWidth = Math.max(0.75, radius * 0.07);
    const step = radius * 0.32, length = radius * 0.32;
    for (let i = -1; i <= 1; i++) {
      ctx.beginPath();
      ctx.moveTo(i * step - length * 0.35, length * 0.5);
      ctx.lineTo(i * step + length * 0.35, -length * 0.5);
      ctx.stroke();
    }
    ctx.restore();
  }
}

export function registerBlush(register = registerOverlay) {
  register("blush", { duration: 4, fadeIn: 0.6, fadeOut: 0.6, draw: drawBlush });
}
