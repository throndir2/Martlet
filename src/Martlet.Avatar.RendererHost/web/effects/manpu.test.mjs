import test from "node:test";
import assert from "node:assert/strict";
import { MANPU, MANPU_NAMES, drawManpu, easeOutBack, headTop, localPoint, particleTime, registerManpu } from "./manpu.mjs";

// A 2D context that records the transform it is given and every drawing call, without a browser.
function recorder() {
  const calls = [], stack = [];
  let state = { alpha: 1, matrix: [1, 0, 0, 1, 0, 0] };
  const multiply = ([a, b, c, d, e, f], [g, h, i, j, k, l]) =>
    [a * g + c * h, b * g + d * h, a * i + c * j, b * i + d * j, a * k + c * l + e, b * k + d * l + f];
  const ctx = {
    calls,
    get globalAlpha() { return state.alpha; }, set globalAlpha(value) { state.alpha = value; },
    save() { stack.push({ ...state }); }, restore() { state = stack.pop(); },
    translate(x, y) { state.matrix = multiply(state.matrix, [1, 0, 0, 1, x, y]); },
    scale(x, y) { state.matrix = multiply(state.matrix, [x, 0, 0, y, 0, 0]); },
    rotate(a) { state.matrix = multiply(state.matrix, [Math.cos(a), Math.sin(a), -Math.sin(a), Math.cos(a), 0, 0]); },
    createLinearGradient: () => ({ addColorStop() {} }),
  };
  const point = (x, y) => { const [a, b, c, d, e, f] = state.matrix; return { x: a * x + c * y + e, y: b * x + d * y + f }; };
  for (const name of ["beginPath", "closePath", "moveTo", "lineTo", "bezierCurveTo", "quadraticCurveTo", "arc", "ellipse"])
    ctx[name] = (...args) => { if (name === "moveTo" || name === "lineTo") calls.push({ name, at: point(args[0], args[1]) }); };
  for (const name of ["fill", "stroke", "fillText", "strokeText"])
    ctx[name] = (...args) => calls.push({ name, alpha: state.alpha, at: name.endsWith("Text") ? point(args[1], args[2]) : undefined });
  return ctx;
}

const anchor = (width = 200, angle = 0) => ({ x: 400, y: 300, width, angle, cheekLeft: { x: 340, y: 340 }, cheekRight: { x: 460, y: 340 },
  eyeLeft: { x: 358, y: 305 }, eyeRight: { x: 442, y: 305 }, mouth: { x: 400, y: 370 }, top: { x: 400, y: 300 - 0.75 * width } });

test("every overlay emote registers with its timing and draws on any face", () => {
  const registered = new Map();
  registerManpu((name, overlay) => registered.set(name, overlay));
  assert.deepEqual([...registered.keys()], [...MANPU_NAMES]);
  for (const name of MANPU_NAMES) {
    const overlay = registered.get(name);
    assert.ok(overlay.duration >= 1.5 && overlay.duration <= 4, name);
    assert.ok(overlay.fadeIn > 0 && overlay.fadeOut > 0, name);
    for (const t of [0.1, 0.6, 1.5, 3, 30]) {
      const ctx = recorder();
      overlay.draw(ctx, anchor(), t, 1);
      assert.ok(ctx.calls.some(c => c.name === "fill" || c.name === "stroke" || c.name === "fillText"), `${name} at ${t}s`);
      assert.ok(ctx.calls.every(c => c.alpha === undefined || (c.alpha >= 0 && c.alpha <= 1)), name);
    }
  }
});

test("emotes stay with the face: they scale with its width and turn with the head", () => {
  for (const name of ["question", "exclaim", "sleepy"]) {
    const small = recorder(), large = recorder();
    drawManpu(name, small, anchor(100), 1, 1);
    drawManpu(name, large, anchor(200), 1, 1);
    const at = ctx => ctx.calls.find(c => c.name === "fillText").at;
    // Offsets from the face centre double with the face.
    assert.ok(Math.abs((at(large).x - 400) - 2 * (at(small).x - 400)) < 1e-6, name);
    assert.ok(Math.abs((at(large).y - 300) - 2 * (at(small).y - 300)) < 1e-6, name);
    assert.ok(at(large).x > 400 && at(large).y < 300, `${name} sits above and beside the head`);
  }
  const turned = recorder(), straight = recorder();
  drawManpu("question", straight, { ...anchor(200, 0), top: undefined }, 1, 1);
  drawManpu("question", turned, { ...anchor(200, Math.PI / 2), top: undefined }, 1, 1);
  const a = straight.calls.find(c => c.name === "fillText").at, b = turned.calls.find(c => c.name === "fillText").at;
  // A quarter turn clockwise maps (dx, dy) to (-dy, dx).
  assert.ok(Math.abs((b.x - 400) + (a.y - 300)) < 1e-6 && Math.abs((b.y - 300) - (a.x - 400)) < 1e-6);
});

test("tears fall from the eyes and need them", () => {
  const ctx = recorder();
  drawManpu("tears", ctx, anchor(), 1.2, 1);
  const starts = ctx.calls.filter(c => c.name === "moveTo").map(c => c.at);
  assert.ok(starts.some(p => Math.abs(p.x - 358) < 20 && p.y > 305 && p.y < 330), "left eye");
  assert.ok(starts.some(p => Math.abs(p.x - 442) < 20 && p.y > 305 && p.y < 330), "right eye");
  const blind = recorder();
  drawManpu("tears", blind, { ...anchor(), eyeLeft: undefined }, 1.2, 1);
  assert.equal(blind.calls.length, 0);
});

test("weight fades the whole emote and nothing draws without a face", () => {
  const ctx = recorder();
  drawManpu("anger", ctx, anchor(), 1, 0.25);
  assert.ok(ctx.calls.length > 0 && ctx.calls.every(c => c.alpha === undefined || c.alpha <= 0.25 + 1e-9));
  for (const [face, weight] of [[undefined, 1], [{ ...anchor(), width: 0 }, 1], [anchor(), 0]]) {
    const none = recorder();
    drawManpu("hearts", none, face, 1, weight);
    assert.equal(none.calls.length, 0);
  }
  const unknown = recorder();
  drawManpu("nod", unknown, anchor(), 1, 1);
  assert.equal(unknown.calls.length, 0);
});

test("frame math", () => {
  assert.ok(Math.abs(easeOutBack(0)) < 1e-9);
  assert.equal(easeOutBack(1), 1);
  assert.ok(easeOutBack(0.7) > 1, "overshoots before settling");
  assert.equal(particleTime(0.2, 1, 0.5, 2), undefined);
  assert.equal(particleTime(0.7, 1, 0.5, 2), 0.7 - 0.5);
  assert.ok(Math.abs(particleTime(4.7, 1, 0.5, 2) - 0.2) < 1e-9, "repeats while held");
  const p = localPoint({ x: 100, y: 100, width: 50, angle: Math.PI / 2 }, { x: 100, y: 150 });
  assert.ok(Math.abs(p.x - 100) < 1e-9 && Math.abs(p.y) < 1e-9);
  assert.equal(headTop(anchor()), -75);
  assert.equal(headTop({ ...anchor(), top: undefined }), -75);
  assert.equal(headTop({ ...anchor(), top: { x: 400, y: 0 } }), -130);
  assert.ok(Object.values(MANPU).every(e => typeof e.draw === "function"));
});
