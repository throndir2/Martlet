import test from "node:test";
import assert from "node:assert/strict";
import { activeOverlays, BLUSH_LEVELS, blushDrawing, clearOverlays, drawBlush, drawOverlays, hasOverlay, overlayNames, registerBlush,
  registerOverlay, startOverlay, stopOverlay, toCssAnchor } from "./overlay.js";

const anchor = { x: 100, y: 100, width: 80, angle: 0, cheekLeft: { x: 80, y: 115 }, cheekRight: { x: 120, y: 115 } };

// A 2D context that records what is drawn: each fill (alpha, the arc's radius, the gradient's stops and where it was moved
// to), each stroke (alpha, color) and each transform, with save and restore keeping alpha and styles as a canvas does.
function recorder() {
  const log = { fills: [], strokes: [], transforms: [] };
  const stack = [];
  let radius = 0, at = [0, 0];
  const state = { globalAlpha: 1, fillStyle: undefined, strokeStyle: undefined, lineWidth: 1 };
  const methods = {
    save: () => stack.push({ ...state, at }), restore: () => { const saved = stack.pop(); at = saved.at; delete saved.at; Object.assign(state, saved); },
    translate: (x, y) => { at = [at[0] + x, at[1] + y]; }, transform: (...matrix) => log.transforms.push(matrix),
    arc: (_x, _y, r) => { radius = r; },
    createRadialGradient: () => ({ stops: [], addColorStop(offset, color) { this.stops.push([offset, color]); } }),
    fill: () => log.fills.push({ alpha: state.globalAlpha, radius, stops: state.fillStyle?.stops, at }),
    stroke: () => log.strokes.push({ alpha: state.globalAlpha, color: state.strokeStyle }),
  };
  const ctx = new Proxy({}, {
    get: (_target, key) => key in methods ? methods[key] : key in state ? state[key] : () => {},
    set: (_target, key, value) => { state[key] = value; return true; } });
  return { ctx, log };
}
const rgba = color => color.match(/[\d.]+/g).map(Number);

test("overlays fade in, last their time and fade out, or stay while held until stopped", () => {
  clearOverlays();
  const weights = [];
  registerOverlay("test", { duration: 2, fadeIn: 0.5, fadeOut: 0.5, draw: (_ctx, _anchor, _t, weight) => weights.push(weight) });
  assert.ok(hasOverlay("test") && overlayNames().includes("test"));
  assert.equal(startOverlay("missing"), false);
  assert.equal(startOverlay("test"), true);
  const ctx = { save() {}, restore() {} };
  drawOverlays(ctx, anchor, 0.25);
  drawOverlays(ctx, anchor, 0.5);
  assert.ok(weights[0] > 0 && weights[0] < 1 && weights[1] === 1);
  drawOverlays(ctx, anchor, 1.5);
  assert.deepEqual(activeOverlays(), [], "it ends after its duration");

  weights.length = 0;
  startOverlay("test", { hold: true });
  for (let i = 0; i < 10; i++) drawOverlays(ctx, anchor, 1);
  assert.equal(weights.at(-1), 1, "held, it stays");
  drawOverlays(ctx, undefined, 0.1);
  assert.equal(weights.length, 10, "no face, nothing drawn, but it keeps going");
  assert.equal(stopOverlay("test"), true);
  drawOverlays(ctx, anchor, 0.25);
  assert.ok(weights.at(-1) > 0 && weights.at(-1) < 1);
  drawOverlays(ctx, anchor, 0.3);
  assert.deepEqual(activeOverlays(), []);
  assert.equal(stopOverlay("test"), false);
});

test("the blush paints a pink glow on both cheeks and adapter pixels become CSS pixels", () => {
  clearOverlays();
  registerBlush();
  const arcs = [];
  const ctx = new Proxy({}, { get: (target, key) => key in target ? target[key] : key === "arc" ? (x, y, r) => arcs.push(r)
    : key === "createRadialGradient" ? () => ({ addColorStop() {} }) : () => {}, set: (target, key, value) => { target[key] = value; return true; } });
  startOverlay("blush");
  drawOverlays(ctx, anchor, 1);
  assert.equal(arcs.length, 2);
  assert.ok(arcs.every(r => r > 10));
  const css = toCssAnchor({ ...anchor, eyeLeft: { x: 90, y: 100 } }, 0.5, 0.5);
  assert.deepEqual([css.x, css.width, css.cheekLeft.x, css.eyeLeft.y], [50, 40, 40, 50]);
  assert.equal(toCssAnchor(undefined, 1, 1), undefined);
  const framed = toCssAnchor({ ...anchor, tracking: "mesh", cheekLeftFrame: { right: { x: 80, y: 0 }, down: { x: 0, y: 80 }, visible: 2 },
    cheekRightFrame: { right: { x: Number.NaN, y: 0 }, down: { x: 0, y: 1 }, visible: 1 } }, 0.5, 0.5);
  assert.equal(framed.tracking, "mesh");
  assert.deepEqual(framed.cheekLeftFrame, { right: { x: 40, y: 0 }, down: { x: 0, y: 40 }, visible: 1 });
  assert.equal(framed.cheekRightFrame, undefined, "a frame that isn't finite is dropped");
  clearOverlays();
});

test("the blush lies on each cheek's surface: wider on a turned head's near cheek, squashed and fading on the far one", () => {
  const transforms = [], fills = [];
  const ctx = new Proxy({}, { get: (target, key) => key in target ? target[key]
    : key === "transform" ? (...matrix) => transforms.push(matrix)
    : key === "fill" ? () => fills.push(target.globalAlpha)
    : key === "createRadialGradient" ? () => ({ addColorStop() {} }) : () => {},
  set: (target, key, value) => { target[key] = value; return true; } });
  const turned = { ...anchor, angle: 0.2, cheekLeftFrame: { right: { x: 100, y: 0 }, down: { x: 0, y: 80 }, visible: 1 },
    cheekRightFrame: { right: { x: 40, y: -8 }, down: { x: 2, y: 80 }, visible: 0.5 } };
  drawBlush(ctx, turned, 0, 1);
  // The glow and the strokes of each cheek are laid with its frame, per pixel of the face's 80-pixel width.
  assert.deepEqual(transforms[0], [1.25, 0, 0, 1, 0, 0]);
  assert.deepEqual(transforms[2], [0.5, -0.1, 0.025, 1, 0, 0]);
  assert.deepEqual(fills, [0.5, 0.25], "the far cheek fades as it turns away");
  fills.length = 0;
  drawBlush(ctx, { ...turned, cheekRightFrame: { ...turned.cheekRightFrame, visible: 0 } }, 0, 1);
  assert.equal(fills.length, 1, "a cheek turned out of sight isn't drawn");
  transforms.length = 0;
  drawBlush(ctx, { ...anchor, angle: 0.2 }, 0, 1);
  const [a, b, c, d] = transforms[0];
  assert.ok([a - Math.cos(0.2), b - Math.sin(0.2), c + Math.sin(0.2), d - Math.cos(0.2)].every(e => Math.abs(e) < 1e-12),
    "without frames it turns with the face's roll");
});

test("every blush level is registered, the blush first, and each lasts like the blush or stays while held", () => {
  clearOverlays();
  registerBlush();
  assert.deepEqual([...BLUSH_LEVELS], ["blush", "blush_deep", "blush_fierce"]);
  for (const level of BLUSH_LEVELS) assert.ok(hasOverlay(level), level);
  assert.throws(() => blushDrawing("blush_mild"), RangeError);
  startOverlay("blush_deep");
  startOverlay("blush_fierce", { hold: true });
  drawOverlays(recorder().ctx, anchor, 4.5);
  assert.deepEqual(activeOverlays(), ["blush_fierce"], "a passing level ends after 4 seconds, a held one stays");
  clearOverlays();
});

test("each blush level is stronger than the one below: wider, stronger, redder, more strokes, and the fierce one crosses the nose", () => {
  const drawn = BLUSH_LEVELS.map(level => {
    const { ctx, log } = recorder();
    blushDrawing(level)(ctx, anchor, 0, 1);
    const cheeks = log.fills.filter(fill => [anchor.cheekLeft, anchor.cheekRight].some(c => Math.hypot(fill.at[0] - c.x, fill.at[1] - c.y) < 1e-9));
    const middle = rgba(cheeks[0].stops[0][1]), line = rgba(log.strokes[0].color);
    return { level, radius: cheeks[0].radius, alpha: cheeks[0].alpha, green: middle[1], ink: middle[3] * cheeks[0].alpha,
      lineGreen: line[1], strokes: log.strokes.length, bridge: log.fills.length - cheeks.length,
      overNose: log.fills.some(fill => fill.at[0] > 90 && fill.at[0] < 110) };
  });
  assert.deepEqual(drawn.map(d => d.radius), [80 * 0.17, 80 * 0.2, 80 * 0.23]);
  for (let i = 1; i < drawn.length; i++) {
    const [weaker, stronger] = [drawn[i - 1], drawn[i]];
    assert.ok(stronger.radius > weaker.radius && stronger.alpha > weaker.alpha && stronger.ink > weaker.ink, stronger.level);
    assert.ok(stronger.green < weaker.green && stronger.lineGreen < weaker.lineGreen, `${stronger.level} is redder`);
    assert.ok(stronger.strokes > weaker.strokes, `${stronger.level} has more strokes`);
  }
  assert.deepEqual(drawn.map(d => d.strokes), [6, 10, 18], "3, 5 and 7 strokes on each cheek, and 4 over the nose");
  assert.deepEqual(drawn.map(d => [d.bridge > 0, d.overNose]), [[false, false], [false, false], [true, true]],
    "only the fierce flush runs across the nose");
});

test("every blush level lies on the cheeks' surfaces and fades with its weight, a cheek turning away and the fierce pulse", () => {
  const turned = { ...anchor, eyeLeft: { x: 84, y: 100 }, eyeRight: { x: 116, y: 100 },
    cheekLeftFrame: { right: { x: 100, y: 0 }, down: { x: 0, y: 80 }, visible: 1 },
    cheekRightFrame: { right: { x: 40, y: -8 }, down: { x: 2, y: 80 }, visible: 0.5 } };
  for (const level of BLUSH_LEVELS) {
    const full = recorder(), half = recorder();
    blushDrawing(level)(full.ctx, turned, 0, 1);
    blushDrawing(level)(half.ctx, turned, 0, 0.5);
    // Each cheek's glow and strokes are laid with its frame, per pixel of the face's 80-pixel width.
    assert.deepEqual(full.log.transforms[0], [1.25, 0, 0, 1, 0, 0], level);
    assert.deepEqual(full.log.transforms[2], [0.5, -0.1, 0.025, 1, 0, 0], level);
    const [left, right] = full.log.fills;
    assert.ok(Math.abs(right.alpha - left.alpha / 2) < 1e-12, `${level}: the far cheek fades as it turns away`);
    assert.deepEqual(half.log.fills.map(f => f.alpha), full.log.fills.map(f => f.alpha / 2), `${level} fades with its weight`);
    assert.deepEqual(half.log.strokes.map(s => s.alpha), full.log.strokes.map(s => s.alpha / 2), level);
  }
  // The squashed far cheek (about half as wide) keeps fewer of a stronger level's strokes; the blush keeps its three.
  const strokes = level => { const { ctx, log } = recorder(); blushDrawing(level)(ctx, { ...turned, cheekLeftFrame: undefined, cheekLeft: undefined },
    0, 1); return log.strokes.length; };
  assert.deepEqual(BLUSH_LEVELS.map(strokes), [3, 3, 4]);
  // The fierce flush's glow pulses slowly; its strokes don't.
  const at = t => { const { ctx, log } = recorder(); blushDrawing("blush_fierce")(ctx, anchor, t, 1); return log; };
  const [start, half] = [at(0), at(0.8)];
  assert.ok(half.fills[0].alpha < 0.9 * start.fills[0].alpha && half.fills[0].alpha > 0.8 * start.fills[0].alpha);
  assert.deepEqual(half.strokes.map(s => s.alpha), start.strokes.map(s => s.alpha));
  // Its band over the nose runs along the cheeks, bowed toward the eyes, each end as strong as its cheek shows.
  const band = recorder();
  blushDrawing("blush_fierce")(band.ctx, turned, 0, 1);
  const bridge = band.log.fills.slice(2);
  assert.equal(bridge.length, 5);
  assert.ok(bridge.every(fill => fill.at[1] < 115) && bridge[2].at[1] < bridge[0].at[1], "bowed up toward the eyes");
  assert.ok(bridge[0].alpha > bridge[4].alpha, "fading toward the cheek turning away");
});

test("a blush level draws nothing without a face, without cheeks or with both cheeks turned away", () => {
  clearOverlays();
  registerBlush();
  for (const level of BLUSH_LEVELS) startOverlay(level, { hold: true });
  const { ctx, log } = recorder();
  drawOverlays(ctx, undefined, 0.5);
  assert.deepEqual(activeOverlays(), [...BLUSH_LEVELS], "they keep going");
  for (const level of BLUSH_LEVELS) {
    blushDrawing(level)(ctx, { x: 100, y: 100, width: 80, angle: 0 }, 0, 1);
    const hidden = { right: { x: 80, y: 0 }, down: { x: 0, y: 80 }, visible: 0 };
    blushDrawing(level)(ctx, { ...anchor, cheekLeftFrame: hidden, cheekRightFrame: hidden }, 0, 1);
  }
  assert.deepEqual([log.fills.length, log.strokes.length, log.transforms.length], [0, 0, 0]);
  clearOverlays();
});
