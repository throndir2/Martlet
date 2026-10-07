import test from "node:test";
import assert from "node:assert/strict";
import { activeOverlays, clearOverlays, drawOverlays, hasOverlay, overlayNames, registerBlush, registerOverlay, startOverlay,
  stopOverlay, toCssAnchor } from "./overlay.js";

const anchor = { x: 100, y: 100, width: 80, angle: 0, cheekLeft: { x: 80, y: 115 }, cheekRight: { x: 120, y: 115 } };

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
  clearOverlays();
});
