// Martlet's own drawings over the character (a blush glow, manga symbols...), on a 2D canvas laid exactly over the avatar
// canvas. Each overlay is registered once with how long it lasts and how it draws around the face anchor the adapter
// reports; the layer fades it in and out and redraws every frame.
//
// anchor: { x, y, width, angle, cheekLeft:{x,y}, cheekRight:{x,y}, eyeLeft?, eyeRight?, mouth?, top? } in CSS pixels of
// the page; x/y is the face's middle, width the face's width (zoom included), angle its roll in radians (clockwise on
// screen), Left/Right the viewer's left and right.

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
  return [anchor.x, anchor.y, anchor.width].every(Number.isFinite) && anchor.width > 0 ? anchor : undefined;
}

// ---------- the blush ----------

/** Soft pink glows on both cheeks with a few faint diagonal strokes, anime style; the fallback for models without a blush
 *  of their own. */
export function drawBlush(ctx, anchor, _t, weight) {
  const radius = anchor.width * 0.17;
  ctx.globalAlpha = 0.5 * weight;
  for (const cheek of [anchor.cheekLeft, anchor.cheekRight]) {
    if (!cheek) continue;
    ctx.save();
    ctx.translate(cheek.x, cheek.y);
    ctx.rotate(anchor.angle || 0);
    ctx.scale(1, 0.62);
    const glow = ctx.createRadialGradient(0, 0, 0, 0, 0, radius);
    glow.addColorStop(0, "rgba(255, 92, 128, 0.85)");
    glow.addColorStop(0.55, "rgba(255, 110, 140, 0.45)");
    glow.addColorStop(1, "rgba(255, 130, 150, 0)");
    ctx.fillStyle = glow;
    ctx.beginPath(); ctx.arc(0, 0, radius, 0, Math.PI * 2); ctx.fill();
    ctx.restore();
    ctx.save();
    ctx.translate(cheek.x, cheek.y);
    ctx.rotate(anchor.angle || 0);
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
